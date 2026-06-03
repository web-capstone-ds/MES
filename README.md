# DS Vision MES 서버

공장 내 비전 검사 장비를 중앙에서 제어하는 MES(Manufacturing Execution System) 서버.  
MQTT를 통해 장비 상태를 모니터링하고, LOT 제어·레시피 관리·운영자 수동 명령을 처리합니다.

## 기술 스택

| 항목 | 내용 |
|---|---|
| Language | C# (.NET 8) |
| MQTT | MQTTnet 4.x |
| 로깅 | Serilog |
| DI/호스팅 | Microsoft.Extensions.Hosting |

## 디렉토리 구조

```
MES/mes-server/
├── Infrastructure/      # MQTT 클라이언트 서비스
├── Models/              # 제어 명령, 장비 상태, MQTT 옵션 모델
├── Scenarios/           # 시나리오 로더 (JSON 기반)
└── Services/
    ├── EquipmentMonitorService  # 장비 상태 모니터링 (HEARTBEAT, STATUS 구독)
    ├── LotControlService        # LOT 제어 (LOT_END 구독, 판정 기반 대응)
    ├── OperatorConsoleService   # 운영자 수동 제어 (stdin 전용)
    ├── RecipeControlService     # 레시피 변경 제어
    └── RecommendationService    # 제어 추천 생성 및 MQTT 발행
```

## MQTT 역할

**구독 토픽:**
- `ds/+/heartbeat` — 장비 생존 모니터링
- `ds/+/status` — 장비 상태/진행률 추적
- `ds/+/lot` — LOT 완료 수신
- `ds/+/alarm` — 알람 수신
- `ds/+/oracle` — Oracle 판정 결과 수신

**발행 토픽:**
- `ds/{eq}/control` — 장비 제어 명령 (EMERGENCY_STOP, STATUS_QUERY 등)
- `ds/{eq}/recommendation` — 모바일 관제용 제어 추천

## 실행 방법

```bash
cd MES/mes-server
dotnet run
```

설정 파일: `appsettings.json`
- MQTT Broker 주소, 포트, 클라이언트 ID 등 설정

운영자 수동 제어는 **stdin(콘솔)** 에서만 가능합니다. 외부 네트워크에서 직접 접근은 불가합니다.
