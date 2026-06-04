using MesServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MesServer.Services;

/// <summary>
/// 운영자 수동 제어를 MES 로컬 운영 인터페이스에서만 처리한다.
/// 보안 원칙: 제어는 MES 로컬 콘솔/대시보드 전용 — Web-Backend 등 외부 진입점을 두지 않는다.
/// 자동 감지(R23/R25/알람)는 추천만 만들고, 운영자가 이 인터페이스에서 처분(제어 명령)을 발동한다.
/// 모바일은 ds/{eq}/recommendation을 표시만 한다(관제).
/// </summary>
public class OperatorConsoleService : BackgroundService
{
    private readonly ILogger<OperatorConsoleService> _logger;
    private readonly LotControlService _lot;
    private readonly RecommendationService _recommendations;
    private readonly ThresholdProposalService _thresholdProposals;

    public OperatorConsoleService(
        ILogger<OperatorConsoleService> logger,
        LotControlService lot,
        RecommendationService recommendations,
        ThresholdProposalService thresholdProposals)
    {
        _logger = logger;
        _lot = lot;
        _recommendations = recommendations;
        _thresholdProposals = thresholdProposals;
    }

    // stdin 읽기는 블로킹이므로 전용 스레드에서 구동한다.
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => LoopAsync(stoppingToken), stoppingToken);

    private async Task LoopAsync(CancellationToken ct)
    {
        PrintHelp();
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "운영자 콘솔 입력 오류");
                break;
            }

            // EOF(null): 비대화형 컨테이너 등 stdin 미연결 → 콘솔 제어 비활성(추천/감시는 계속).
            if (line is null)
            {
                _logger.LogInformation("운영자 콘솔: 표준입력 없음(비대화형). 콘솔 제어 비활성화. " +
                    "대화형으로 쓰려면 docker는 stdin_open/tty 후 attach 하세요.");
                return;
            }

            line = line.Trim();
            if (line.Length == 0) continue;

            try
            {
                await DispatchAsync(line, ct);
            }
            catch (ConsoleUsageException ux)
            {
                Console.WriteLine($"  사용법 오류: {ux.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "콘솔 명령 처리 실패: {Line}", line);
                Console.WriteLine($"  명령 실패: {ex.Message}");
            }
        }
    }

    private async Task DispatchAsync(string line, CancellationToken ct)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        switch (cmd)
        {
            case "help" or "?":
                PrintHelp();
                break;

            case "reco":
                PrintRecommendations();
                break;

            case "treco":
                PrintThresholdProposals();
                break;

            case "tapprove":  // tapprove <eq> <proposalId>
                RequireArgs(parts, 3);
                await _lot.ApproveThresholdAsync(parts[1], parts[2], CurrentOperator(), ct);
                _thresholdProposals.Remove(parts[2]);
                Console.WriteLine($"  → APPROVE_THRESHOLD 발행: {parts[1]} {parts[2]}");
                break;

            case "treject":  // treject <eq> <proposalId> [reason...]
                RequireArgs(parts, 3);
                await _lot.RejectThresholdAsync(parts[1], parts[2], CurrentOperator(), JoinFrom(parts, 3), ct);
                _thresholdProposals.Remove(parts[2]);
                Console.WriteLine($"  → REJECT_THRESHOLD 발행: {parts[1]} {parts[2]}");
                break;

            case "abort":  // abort <eq> [reason...]
                RequireArgs(parts, 2);
                await _lot.LotAbortAsync(parts[1], lotId: "", reason: JoinFrom(parts, 2) ?? "operator abort", ct);
                await _recommendations.ResolveAsync(parts[1], "LOT_ABORT", ct);
                Console.WriteLine($"  → LOT_ABORT 발행: {parts[1]}");
                break;

            case "recipe":  // recipe <eq> <recipeName>
                RequireArgs(parts, 3);
                await _lot.RecipeLoadAsync(parts[1], parts[2], "v1.0", ct);
                await _recommendations.ResolveAsync(parts[1], "RECIPE_LOAD", ct);
                Console.WriteLine($"  → RECIPE_LOAD({parts[2]}) 발행: {parts[1]}");
                break;

            case "estop":  // estop <eq> [reason...]
                RequireArgs(parts, 2);
                await _lot.EmergencyStopAsync(parts[1], JoinFrom(parts, 2) ?? "operator emergency stop", ct);
                await _recommendations.ResolveAsync(parts[1], "EMERGENCY_STOP", ct);
                Console.WriteLine($"  → EMERGENCY_STOP 발행: {parts[1]}");
                break;

            case "aclear":  // aclear <eq>
                RequireArgs(parts, 2);
                await _lot.AlarmClearAsync(parts[1], ct);
                await _recommendations.ResolveAsync(parts[1], "ALARM_CLEAR", ct);
                Console.WriteLine($"  → ALARM_CLEAR 발행: {parts[1]}");
                break;

            case "aack":  // aack <eq> [burstId]
                RequireArgs(parts, 2);
                await _lot.AlarmAckAsync(parts[1], parts.Length >= 3 ? parts[2] : null, ct);
                await _recommendations.ResolveAsync(parts[1], "ALARM_ACK", ct);
                Console.WriteLine($"  → ALARM_ACK 발행: {parts[1]}");
                break;

            case "squery":  // squery <eq>
                RequireArgs(parts, 2);
                await _lot.StatusQueryAsync(parts[1], ct);
                Console.WriteLine($"  → STATUS_QUERY 발행: {parts[1]}");
                break;

            default:
                Console.WriteLine($"  알 수 없는 명령: '{cmd}' (help 입력)");
                break;
        }
    }

    private void PrintRecommendations()
    {
        var all = _recommendations.GetAll();
        if (all.Count == 0)
        {
            Console.WriteLine("  (대기 중인 추천 없음)");
            return;
        }
        Console.WriteLine("  === 대기 추천 ===");
        foreach (var r in all)
        {
            Console.WriteLine($"  [{r.Severity}] {r.EquipmentId} {r.Rule} → 권고: {string.Join(", ", r.SuggestedActions)} | {r.Reason}");
        }
    }

    private void PrintThresholdProposals()
    {
        var all = _thresholdProposals.GetAll();
        if (all.Count == 0)
        {
            Console.WriteLine("  (대기 중인 임계값 제안 없음)");
            return;
        }

        Console.WriteLine("  === 대기 임계값 제안 ===");
        foreach (var r in all)
        {
            var current = FormatThreshold(r.CurrentWarning, r.CurrentCritical);
            var proposed = FormatThreshold(r.ProposedWarning, r.ProposedCritical);
            Console.WriteLine($"  {r.EquipmentId} {r.ProposalId} {r.RecipeId ?? "-"} {r.RuleId ?? "-"} {r.Metric ?? "-"}: {current} → {proposed} | lot_basis={r.LotBasis?.ToString() ?? "-"} | {r.Basis ?? "-"}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
@"=== MES 운영자 콘솔 (제어는 콘솔에서만) ===
  reco                        대기 중인 제어 추천 목록
  treco                       대기 중인 임계값 제안 목록
  tapprove <eq> <proposalId>  임계값 제안 승인
  treject  <eq> <proposalId> [reason] 임계값 제안 거부
  abort  <eq> [reason]        LOT 강제 중단 (LOT_END ABORTED)
  recipe <eq> <recipeName>    레시피 재로드(재티칭)
  estop  <eq> [reason]        비상 정지 (LOT_END ABORTED + STOP)
  aclear <eq>                 알람 해제(복구)
  aack   <eq> [burstId]       알람 확인(ACK) retained clear
  squery <eq>                 상태 조회 요청
  help                        도움말
  예) abort DS-VIS-002 teaching incomplete");
    }

    private static string? JoinFrom(string[] parts, int index)
        => parts.Length > index ? string.Join(' ', parts[index..]) : null;

    private static string CurrentOperator()
        => string.IsNullOrWhiteSpace(Environment.UserName) ? "operator" : Environment.UserName;

    private static string FormatThreshold(double? warning, double? critical)
        => $"warn={FormatNumber(warning)}, crit={FormatNumber(critical)}";

    private static string FormatNumber(double? value)
        => value.HasValue ? value.Value.ToString("0.##") : "-";

    private static void RequireArgs(string[] parts, int min)
    {
        if (parts.Length < min)
            throw new ConsoleUsageException($"인자 부족 (최소 {min}개 토큰). 'help' 참고.");
    }

    private sealed class ConsoleUsageException : Exception
    {
        public ConsoleUsageException(string message) : base(message) { }
    }
}
