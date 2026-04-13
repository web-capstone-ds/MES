# MES 서버 작업명세서
## DS 비전 검사 장비 모바일 모니터링 프로젝트

> **레포지토리**: `web-capstone-ds / EAP_VM`  
> **작성일**: 2026-04-13  
> **작업 범위**: MES 서버 전체 (M1 ~ M5)  
> **플랫폼**: C# / .NET 8 / MQTTnet v4.3.7  
> **상태**: ✅ 완료

---

## 1. 개요

N대의 비전 검사 장비(EAP)를 MQTT Broker를 통해 일괄 제어하고, 장비 상태를 실시간으로 집계·모니터링하는 MES 서버. `multi_equipment_4x.json` 시나리오를 기반으로 4대 장비에 Mock 이벤트를 병렬 발행하여 전체 파이프라인을 검증한다.

---

## 2. 프로젝트 구조

```
mes-server/
├── MesServer.csproj
├── appsettings.json
├── Program.cs
├── Infrastructure/
│   ├── IMqttClientService.cs       — MQTT 클라이언트 인터페이스
│   └── MqttClientService.cs        — 재연결 백오프 + Retained 정책 구현
├── Models/
│   ├── MqttOptions.cs              — Broker 연결 설정 모델
│   ├── ControlCommand.cs           — CONTROL_CMD + LotEvent 페이로드 모델
│   └── EquipmentStatus.cs          — HeartbeatEvent / StatusEvent / AlarmEvent 모델
├── Services/
│   ├── LotControlService.cs        — LOT 제어 명령 발행 + LOT_END 수신 처리
│   ├── EquipmentMonitorService.cs  — 장비 상태 집계 + Rule 실시간 감지
│   └── RecipeControlService.cs     — (스켈레톤, 추후 구현)
└── Scenarios/
    ├── ScenarioLoader.cs           — N=4 다설비 시나리오 실행
    └── ScenarioModels.cs           — 시나리오 파싱 모델
```

---

## 3. NuGet 의존성

| 패키지 | 버전 | 용도 |
| :--- | :--- | :--- |
| `MQTTnet` | 4.3.7.1207 | MQTT 클라이언트 |
| `Microsoft.Extensions.Hosting` | 8.0.0 | Generic Host / DI |
| `Serilog.Extensions.Hosting` | 8.0.0 | 구조적 로깅 |
| `Serilog.Sinks.Console` | 5.0.1 | 콘솔 출력 |

---

## 4. 설정 파일 (appsettings.json)

```json
{
  "Mqtt": {
    "Host": "localhost",
    "Port": 1883,
    "Username": "mes_server",
    "Password": "CHANGE_ME",
    "ClientId": "mes-server-001",
    "KeepAliveSec": 60,
    "ReconnectBackoffSec": [1, 2, 5, 15, 30, 60]
  },
  "Equipments": [
    { "Id": "DS-VIS-001", "DisplayName": "비전 #1", "Site": "Carsem-A" },
    { "Id": "DS-VIS-002", "DisplayName": "비전 #2", "Site": "Carsem-A" },
    { "Id": "DS-VIS-003", "DisplayName": "비전 #3", "Site": "Carsem-A" },
    { "Id": "DS-VIS-004", "DisplayName": "비전 #4", "Site": "Carsem-A" }
  ],
  "ScenarioPath": "../EAP_mock_data/scenarios/multi_equipment_4x.json"
}
```

---

## 5. 컴포넌트별 구현 명세

### 5.1 MqttClientService (Infrastructure)

MQTT 연결·재연결·발행·구독을 담당하는 핵심 인프라 컴포넌트.

**재연결 백오프** — 명세서 §부록 A.6

| 시도 | 대기 시간 | Jitter |
| :--- | :--- | :--- |
| 1회 | 1s | ±20% |
| 2회 | 2s | ±20% |
| 3회 | 5s | ±20% |
| 4회 | 15s | ±20% |
| 5회 | 30s | ±20% |
| 6회 이상 | 60s (max) | ±20% |

**Retained 토픽 정책** — 명세서 §1.1

| 토픽 | 정책 | 위반 시 |
| :--- | :--- | :--- |
| `/status` `/lot` `/alarm` `/recipe` `/oracle` | `MustRetain` — 자동 강제 | retain=false 로 호출해도 true로 override |
| `/heartbeat` `/result` `/control` | `MustNotRetain` — 금지 | `InvalidOperationException` throw |

**인바운드 채널**
- `Channel.CreateBounded` capacity=1000, `DropOldest`
- 수신 메시지 backpressure 방지

**Will Message** — 명세서 §부록 A.4
- `WillRetain = true`
- Payload 필드: `equipment_id`, `event_type: EAP_DISCONNECTED`, `timestamp`

---

### 5.2 LotControlService (Services)

LOT 제어 명령 발행 및 LOT_END 이벤트 수신 처리.

**지원 명령 코드**

| 메서드 | 명령 코드 | Retain | QoS |
| :--- | :--- | :--- | :--- |
| `EmergencyStopAsync` | `EMERGENCY_STOP` | ❌ | 2 |
| `LotAbortAsync` | `LOT_ABORT` | ❌ | 2 |
| `RecipeLoadAsync` | `RECIPE_LOAD` | ❌ | 2 |
| `AlarmClearAsync` | `ALARM_CLEAR` | ❌ | 2 |
| `StatusQueryAsync` | `STATUS_QUERY` | ❌ | 2 |
| `AlarmAckAsync` | `ALARM_ACK` | ❌ | 2 |

> 모든 CONTROL_CMD는 Retain=false 강제. `MustNotRetain` 검증 통과.

**LOT_END 수신 처리**

구독 토픽: `ds/+/lot` (QoS 2)

수율 판정 기준 (Rule R23):

| 수율 | 판정 | 처리 |
| :--- | :--- | :--- |
| ≥ 98% | EXCELLENT | LogInformation |
| ≥ 95% | NORMAL | LogInformation |
| ≥ 90% | WARNING | LogInformation |
| ≥ 80% | MARGINAL | LogInformation |
| < 80% | CRITICAL | `LogCritical` + 콘솔 빨간 출력 |

**LOT Start/End 불균형 감지 (Rule R25)**
- 장비별 Start/End 카운터 `ConcurrentDictionary` 관리
- 차이 ≥ 5 → `LogCritical`

---

### 5.3 EquipmentMonitorService (Services)

장비 상태 집계 및 Rule 기반 실시간 이상 감지.

**구독 토픽**

| 토픽 | QoS | 처리 메서드 |
| :--- | :--- | :--- |
| `ds/+/heartbeat` | 1 | `HandleHeartbeat` |
| `ds/+/status` | 1 | `HandleStatus` |
| `ds/+/alarm` | 2 | `HandleAlarm` |

**Heartbeat 판정 (Rule R01)**

| 경과 시간 | 상태 | 콘솔 색상 |
| :--- | :--- | :--- |
| ≤ 9s | Online | Green |
| 9s ~ 30s | Warning | Yellow |
| > 30s | Offline | DarkGray |

**HW_ALARM 처리**

| alarm_level | 콘솔 색상 | 로그 레벨 |
| :--- | :--- | :--- |
| CRITICAL | Red | `LogCritical` |
| WARNING | Yellow | `LogWarning` |

특수 처리:
- `EAP_DISCONNECTED` 수신 → `_lastHeartbeat = DateTime.MinValue` (즉시 Offline 강제) — 명세서 §부록 A.5
- `requires_manual_intervention = true` → 미확인 알람 목록 등록

**일별/주별 Rule 카운터**

| Rule | 파라미터 | CRITICAL 기준 | 리셋 주기 |
| :--- | :--- | :--- | :--- |
| R26 | `CAM_TIMEOUT_ERR` | > 3건/일 | 자정 UTC |
| R33 | `VISION_SCORE_ERR` + `AggregateException` 키워드 | > 5건/일 | 자정 UTC |
| R34 | `EAP_DISCONNECTED` | > 2건/주 | 월요일 자정 UTC |

**콘솔 모니터링 패널** (5초 주기 갱신)

```
--- MES 장비 모니터링 패널 (2026-01-22 16:41:41 UTC) ---
[DS-VIS-001] RUN  | Carsem_3X3 | 1,247 / 2,792 (44.7%) | 수율 95.8%
[DS-VIS-002] RUN  | Carsem_4X6 |   850 / ?     ( ?.?%) | 수율 68.5% ⚠ WARNING
[DS-VIS-003] IDLE | Carsem_3X3 | LOT 완료
[DS-VIS-004] STOP |            | 진행 정보 없음 ● ALARM
------------------------------------------------------------------
```

---

### 5.4 ScenarioLoader (Scenarios)

`multi_equipment_4x.json` 시나리오를 파싱하여 4대 장비에 Mock 이벤트를 병렬 발행.

**실행 흐름**

```
1. MQTT 연결 대기 (3초)
2. multi_equipment_4x.json 파싱
3. 사전 검증: mock_sequence 파일 전체 존재 확인
   - 미존재 시 FileNotFoundException → 즉시 중단
4. 4대 장비 Task.WhenAll 병렬 실행
   - 각 장비 예외는 개별 격리 (다른 장비 영향 없음)
5. mock_sequence 순서대로:
   - 파일 읽기 (.json 확장자 자동 보장)
   - equipment_id 동적 치환 (페이로드 내부 포함)
   - GetTopicMeta()로 토픽/QoS/Retain 결정
   - PublishAsync() 호출 (500ms 간격)
6. 완료 로그: "총 N건 발행, 소요 Xs"
```

**GetTopicMeta 라우팅 테이블** — 명세서 §1.1 완전 일치

| event_type | 토픽 suffix | QoS | Retain |
| :--- | :--- | :--- | :--- |
| HEARTBEAT | heartbeat | 1 | ❌ |
| STATUS_UPDATE | status | 1 | ✅ |
| INSPECTION_RESULT | result | 1 | ❌ |
| LOT_END | lot | 2 | ✅ |
| HW_ALARM | alarm | 2 | ✅ |
| RECIPE_CHANGED | recipe | 2 | ✅ |
| CONTROL_CMD | control | 2 | ❌ |
| ORACLE_ANALYSIS | oracle | 2 | ✅ |

---

## 6. DI 등록 구조 (Program.cs)

| 서비스 | 등록 방식 | 이유 |
| :--- | :--- | :--- |
| `MqttClientService` | Singleton + HostedService(sp=>) | 인스턴스 공유 |
| `LotControlService` | Singleton + HostedService(sp=>) | BackgroundService |
| `EquipmentMonitorService` | Singleton + HostedService(sp=>) | BackgroundService |
| `RecipeControlService` | Singleton | 명령 서비스 |
| `ScenarioLoader` | Singleton + HostedService(sp=>) | BackgroundService |

> `AddHostedService<T>()` 직접 사용 금지 — 인스턴스 이중 생성 방지를 위해 전부 `sp.GetRequiredService<T>()` 패턴 사용

---

## 7. 페이로드 모델 명세

### ControlCommand (CONTROL_CMD 발행용)

| 필드 | JSON 키 | 타입 | 비고 |
| :--- | :--- | :--- | :--- |
| MessageId | `message_id` | string | UUID v4 자동 생성 |
| EventType | `event_type` | string | "CONTROL_CMD" 고정 |
| Timestamp | `timestamp` | string | UTC `.fffZ` 자동 생성 |
| Command | `command` | string | 명령 코드 |
| IssuedBy | `issued_by` | string | "MES_SERVER" 고정 |
| Reason | `reason` | string? | 선택 |
| TargetLotId | `target_lot_id` | string? | 선택 |
| TargetBurstId | `target_burst_id` | string? | ALARM_ACK 그룹 ACK용 |

### StatusEvent (STATUS_UPDATE 수신용)

| 필드 | JSON 키 | 타입 | 비고 |
| :--- | :--- | :--- | :--- |
| EquipmentId | `equipment_id` | string | |
| Status | `equipment_status` | string | 명세서 §3.1 필드명 |
| CurrentRecipe | `current_recipe` | string? | |
| CurrentUnitCount | `current_unit_count` | int? | v3.4 진행률 필드 |
| ExpectedTotalUnits | `expected_total_units` | int? | v3.4 진행률 필드 |
| CurrentYieldPct | `current_yield_pct` | float? | v3.4 진행률 필드 |

### LotEvent (LOT_END 수신용)

| 필드 | JSON 키 | 타입 | 비고 |
| :--- | :--- | :--- | :--- |
| EquipmentId | `equipment_id` | string | |
| LotId | `lot_id` | string | |
| YieldPct | `yield_pct` | float? | R23 판정 기준 |
| TotalUnits | `total_units` | int? | 명세서 §5.1 필드명 |

---

## 8. 검증 체크리스트 (전체 통과)

| 항목 | 결과 |
| :--- | :--- |
| .NET 8 / MQTTnet v4.3.7 | ✅ |
| `IOptions<MqttOptions>` 패턴 (IConfiguration 직접 주입 제거) | ✅ |
| BackoffSeconds = `[1,2,5,15,30,60]`, jitter ±20% | ✅ |
| `Random.Shared` 스레드 안전 | ✅ |
| `MustRetain()` 5종 자동 강제 | ✅ |
| `MustNotRetain()` 3종 위반 시 `InvalidOperationException` | ✅ |
| `WillRetain = true` + `equipment_id` 필드 포함 | ✅ |
| `Channel.CreateBounded` capacity=1000, `DropOldest` | ✅ |
| `SubscribeAsync` QoS 파라미터 전달 | ✅ |
| 재연결 시 QoS 유지 재구독 | ✅ |
| CONTROL_CMD `retain=false` 강제 | ✅ |
| `ClassifyYield()` 5단계, yield < 80 → `LogCritical` | ✅ |
| R25 Start/End 차이 ≥5 → `LogCritical` | ✅ |
| `CancellationToken.None` 하드코딩 없음 | ✅ |
| `DateTime.Now` 미사용 (전부 `UtcNow`) | ✅ |
| `Newtonsoft.Json` 미사용 | ✅ |
| DI `AddSingleton + AddHostedService(sp=>)` 패턴 통일 | ✅ |
| `EAP_DISCONNECTED` Will 수신 즉시 STOP 처리 | ✅ |
| R26/R33/R34 카운터 + 자정/주별 리셋 | ✅ |
| R33 조건: `VISION_SCORE_ERR` + `Reason.Contains("AggregateException")` | ✅ |
| `AlarmEvent.Reason` `string?` nullable 처리 | ✅ |
| `equipment_status` 필드명 명세서 §3.1 일치 | ✅ |
| `total_units` 필드명 명세서 §5.1 일치 | ✅ |
| mock_sequence `.json` 확장자 자동 보장 (사전검증·실행 모두) | ✅ |
| `GetTopicMeta()` §1.1 QoS+Retain 쌍 완전 일치 | ✅ |
| `equipment_id` 동적 치환 (페이로드 내부 포함) | ✅ |
| 4대 병렬 실행 + 개별 장비 예외 격리 | ✅ |
| 완료 로그 "총 N건 발행, 소요 Xs" | ✅ |

---

## 9. 실행 방법

```bash
# 1. Broker 먼저 기동 (MQTT 레포)
cd ../MQTT/broker && docker-compose up -d

# 2. appsettings.json 비밀번호 설정
# Password 필드를 .env의 MQTT_PASS_MES_SERVER 값으로 변경

# 3. MES 서버 실행
cd mes-server
dotnet run

# 4. 시나리오 자동 실행 확인
# 3초 후 ScenarioLoader가 4대 장비 이벤트를 병렬 발행 시작
# 콘솔에 모니터링 패널 5초 주기 갱신
```

---

## 10. 다음 단계 개발 대상

| 컴포넌트 | 레포 | 우선순위 |
| :--- | :--- | :--- |
| 가상 EAP Publisher (C#) | EAP_VM 또는 신규 | P0 |
| RecipeControlService 구현 | EAP_VM | P1 |
| .NET MAUI 모바일 Subscriber | 신규 | P1 |
| Historian Node.js/TypeScript | 신규 | P1 |
| Oracle Python 서버 | 신규 | P2 |
