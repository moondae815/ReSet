# 통합 배치 설계 목차 재수립 설계

- 작성일: 2026-08-05
- 상태: 설계 승인됨 (구현 계획 수립 전)

## 배경

TUI 메뉴 2번 "통합 배치 마이그레이션 설계" 경로는 브레인스토밍 → 목차 설계 → 본문 생성의 3단계로 동작한다. 목차를 재시도 루프 **밖에** 두는 것이 이 구조의 핵심 결정이다. Actor는 회차마다 백지에서 다시 쓰므로(`CriticFeedbackLog` 클래스 주석), 목차가 없으면 회차마다 문서 뼈대가 달라져 누적 피드백이 엉뚱한 자리에 붙고 회차 간 대조가 불가능해진다. 목차를 고정한 덕에 L1/L2 재시도는 3/3만 재실행하면 되고 토큰도 절약된다.

그 결정이 대가를 하나 만들었다. **목차가 원인인 결함은 재시도로 절대 고쳐지지 않는다.**

`VerificationPipelineOrchestrator.cs:1695`의 `if (string.IsNullOrEmpty(currentPlanStructure))`가 1·2단계를 1회차로 한정하고, 이후 모든 재시도는 `:1711`의 3/3만 반복한다. L2 Critic이 지적하는 다섯 항목(정합성·CRUD·인터페이스·예외·가독성) 중 상당수는 구조에서 비롯된다 — 특정 Step이 목차에 아예 없거나, 청킹이 수학적으로 불가능한 `GROUP BY` 스텝을 청킹 스텝으로 배치한 경우가 그렇다. 이런 결함은 몇 번을 재시도해도 개선되지 않고 재시도 예산만 소진하며, `RetryRescue`가 최고점 회차를 채택해 조용히 "품질 미달"로 끝난다.

같은 고착이 L3에서는 프롬프트 충돌로 나타난다. 사용자 피드백 재생성(`:1925`)도 같은 목차를 쓰는데, 3/3의 user 프롬프트 말미는 `"STRICTLY adhering to the [Approved Document Structure & Plan]"`(`AiService.cs:1969`)이다. 사용자가 "Step 3을 둘로 쪼개라" 같은 구조 피드백을 주면 이 문구와 `User_Feedback_Log.txt`가 정면 충돌하고, "STRICTLY"가 붙은 쪽이 이길 가능성이 높다. 사용자의 구조 요구가 반영되지 않은 채 `planOutcome`만 `ReviewNotRun`으로 떨어진다.

### 조사에서 함께 확인했으나 이번 범위 밖인 사항

같은 조사에서 네 건을 더 확인했다. 이번 설계는 위 두 건만 다룬다.

| # | 내용 | 판단 |
|---|---|---|
| ① | 브레인스토밍 원문이 3/3에 전달되지 않아, 아키텍처 판단(Tasklet/Chunk 선택 등)이 목차 제목에 살아남은 만큼만 본문에 도달한다 | 별도 과제 |
| ② | 4개 H2가 2/3 프롬프트·3/3 프롬프트·L1 검증기 세 곳에 하드코딩되어 2/3의 실질 기여가 H3/H4뿐이다. 목차 단계 존치 여부는 실측이 필요하다 | 별도 과제 |
| ⑤ | 생성 호출 실패는 재시도 0회로 즉시 종료된다(`:1723`). 명세서 경로(`:956`)와 공통 정책이라 1번 경로에도 영향이 간다 | 별도 과제 |
| ⑥ | 2/3가 빈 응답을 내도 방어가 없다. 그 회차의 3/3이 빈 구조를 받고, `currentPlanStructure`가 빈 채로 남아 다음 회차에 1·2단계를 다시 돈다 | 이번 변경이 만드는 재수립 경로에 한해서만 방어한다 |

## 목표와 범위

재시도해도 개선되지 않는 상태를 관측해 목차를 한 번 다시 세운다. 사용자가 구조 변경을 요구하면 그 요구를 목차에 반영한다. 그 둘뿐이다.

**범위 안**
- L2 점수가 정체하면 목차를 재수립하고 남은 회차를 새 구조로 시도
- L3 피드백이 구조를 바꾸는지 사용자에게 확인하고, 그렇다면 목차부터 다시 세움
- 재수립 산출물 보존과 실패 흡수

**범위 밖**
- 3단계 구조 자체의 재설계(단계 수·역할 변경)
- 위 표의 ①②⑤
- 브레인스토밍 단계의 재실행. 입력(원본 Spec)이 그대로이고 피드백을 받을 통로도 없어 같은 결론만 반복한다

## 설계

### 1. 판정 규칙의 소유 — `StructureRedraftPolicy`

`ReSet.Core/Services/StructureRedraftPolicy.cs`에 인스턴스 클래스를 신설한다. 이 클래스가 소유하는 것은 **정체의 정의**와 **Job당 1회 상한** 둘뿐이다.

```csharp
public sealed class StructureRedraftPolicy
{
    /// <summary>이미 재수립을 1회 소비했는가.</summary>
    public bool Consumed { get; private set; }

    /// <summary>true를 돌려주면 목차를 다시 세운다. 소비는 이 안에서 기록한다.</summary>
    public bool TryConsume(bool improvedThisAttempt);
}
```

`BestAttempt`(갱신 규칙), `RetryRescue`(채택 규칙), `CriticFeedbackLog`(피드백 조립), `VerificationBanner`(중단 문구)가 각각 규칙 하나씩을 단독 소유하는 기존 배치와 같은 자리다. 오케스트레이터 안에 지역 변수로 흩으면 1,900줄 메서드에 규칙이 묻혀 단위 테스트가 파이프라인 전체 모킹을 요구하게 된다.

**정체의 정의는 `BestAttempt.TryRecord`가 `false`를 반환한 것이다.** 이 클래스가 "최고점을 갱신했는가"를 엄격 부등호로 이미 판정해 소유하고 있으므로 비교식을 새로 쓰지 않는다. 현재 호출부(`:1807`)는 반환값을 버리고 있는데, 그 버려진 값이 정확히 필요한 신호다.

**미갱신 1회로 발동한다.** 2회 연속을 요구하면 기본 예산(`MaxL2Attempts: 2` → 총 3회)에서 영원히 발동하지 못한다. 1차는 `Current`가 null이라 항상 갱신되므로, 2차의 갱신 실패가 "재시도가 개선을 못 냈다"의 첫 증거다. 그 시점에 목차를 다시 세우면 3차가 새 구조로 생성된다 — 마지막 1회를 구조 재설계에 거는 것이 이 설계의 의도다.

L1 실패 회차는 판정에 참여하지 않는다. 그 경로는 `continue`로 L2에 닿지 않아 `TryRecord`가 호출조차 되지 않으므로 별도 분기 없이 제외된다.

**L3 사용자 지시는 이 상한 밖이다.** L2에서 1회를 소진했더라도 사용자가 구조 변경을 요청하면 재수립한다. 사용자의 명시적 지시를 자동화 예산으로 막지 않는다.

### 2. 시그니처 변경 2건

**(1) `IAiService.DraftBatchPlanStructureAsync`에 재수립 입력 추가**

```csharp
Task<AiResult> DraftBatchPlanStructureAsync(
    string brainstormingResult, string targetLanguage, string jobName,
    string? effort = null,
    string? previousStructure = null, string? redraftFeedback = null,
    CancellationToken cancellationToken = default);
```

재수립 모드 여부는 `previousStructure`가 비어 있지 않은지로만 판단한다. 재수립 모드이면 시스템 프롬프트에 재수립 지시를 덧붙이고, `redraftFeedback`이 있으면 함께 싣는다 — 이전 구조가 리뷰를 반복 통과하지 못했으니 같은 구조를 다시 내지 말고 지적된 원인을 구조 수준에서 해결하라는 취지다. 4개 H2 강제(`AiService.cs:1839-1843`)는 그대로 유지한다. L1 검증기가 같은 헤더를 요구하므로 여기서 풀면 L1이 깨진다. 프롬프트는 영문으로 작성한다(AGENTS.md 하이브리드 영문 프롬프트 규칙).

`redraftFeedback`에는 `CriticFeedbackLog.Compose`가 조립한 누적 피드백을 그대로 넘긴다. 피드백 문구를 이 자리에서 새로 쓰지 않는다.

기존 호출(`:1705`)은 `cancellationToken`을 5번째 위치에 positional로 넘기고 있어 이 변경으로 컴파일 에러가 난다. 의도한 것이다 — 호출부와 테스트 fake가 조용히 기본값을 먹는 대신 드러나서 고쳐진다.

**(2) `HumanReviewResult`에 `RedraftStructure` 추가**

```csharp
public bool RedraftStructure { get; set; }   // Decision이 ProvideFeedback일 때만 의미 있다
```

`IVerificationUserInteraction`에 메서드를 추가하지 않는다. 피드백 본문과 그 성격은 함께 움직이는 값이라 이미 피드백을 나르는 DTO에 싣는 편이 맞고, 인터페이스를 늘리면 모든 구현체와 테스트 fake가 함께 바뀐다. `ConsoleUserInteraction`은 피드백 입력 직후 "이 피드백이 문서 구조(목차)까지 바꾸나요?"를 한 번 확인한다. 배치 모드는 L3를 우회하므로 이 확인은 TUI 전용이다.

### 3. 데이터 흐름

**L2 정체 경로 (배치·TUI 공통)**

```
attempt N  →  L1 통과  →  L2 리뷰 성공
   improved = bestAttempt.TryRecord(...)          // :1807, 지금은 버려지는 반환값
   결함 있음 + 재시도 여력 있음
      CriticFeedbackLog.Record / Compose          // 현행 그대로
      redraftPolicy.TryConsume(improved) 가 true 면
         → 2/3 재실행 (이전 목차 + 누적 피드백 주입)
         → currentPlanStructure 교체, 직전 목차 보존
   attempt++ → 3/3 만 재실행 (현행 그대로)
```

브레인스토밍 결과를 담는 지역 변수를 메서드 스코프로 승격해야 한다. 현재 `:1698`의 결과는 `if` 블록 안에서만 살아 있어 재수립 시점에 접근할 수 없다. 목차가 존재하면 브레인스토밍도 반드시 존재한다는 불변식이 이미 성립하므로(1·2단계는 `:1695` 조건 아래 한 몸으로만 실행됨) 보관만 하면 된다.

진행률 표시는 재수립 회차에 순번 없는 "목차 재설계 중..." 태스크를 추가하고 이어서 "3/3. 최종 생성 중..."을 띄운다. 재수립은 3단계 중 하나가 아니므로 `n/3.` 순번을 부여하지 않는다(AGENTS.md TUI 상태 표기 규칙).

**L3 구조 피드백 경로 (TUI 전용)**

`RedraftStructure`가 true면 `:1925`의 본문 생성 전에 2/3를 먼저 돌리고 새 목차로 3/3을 호출한다. 이 경로는 `StructureRedraftPolicy`를 거치지 않는다 — `TryConsume`을 호출하지 않으므로 L2가 상한을 이미 소진했든 아니든 사용자 요청은 항상 수행된다. 새 목차가 사용자 요구를 이미 반영하므로 `"STRICTLY adhering"`과 사용자 피드백이 더 이상 충돌하지 않는다 — 프롬프트 문구를 손대지 않고 충돌 자체가 사라진다. false면 현행 경로 그대로다.

이 경로의 종료 상태는 기존과 같이 `planReview = null`, `planOutcome = ReviewNotRun`으로 되돌린다. 목차까지 바뀐 문서라면 더더욱 이전 통과 판정을 자칭해선 안 된다(AGENTS.md 검증 종료 상태 정직성 규칙).

### 4. 산출물 보존

`raw/PlanStructure.md`는 **본문을 실제로 만든 최종 목차**를 가리키도록 유지하고, 교체되는 직전 목차를 `raw/PlanStructure.superseded-1.md`로 남긴다. 기존 테스트와 감사 흐름이 `PlanStructure.md`를 최종본으로 읽고 있어 그 계약을 깨지 않으면서 구조 변경 이력을 추적할 수 있다. 재수립이 2회 이상 일어나면 번호를 증가시킨다 — L2 경로 1회 + L3 경로 n회가 가능하다. `Brainstorming.md`는 변경하지 않는다.

### 5. 실패 처리

재수립은 개선 시도이지 필수 단계가 아니다. 세 가지를 모두 흡수한다.

- **호출 예외** → 기존 목차로 계속 진행하고 사용자에게 알린다. 파이프라인을 죽이지 않는다. `Consumed`는 되돌리지 않는다. 되돌리면 남은 회차마다 같은 실패를 반복한다.
- **공백 응답** → 기존 목차를 유지하고 경고한다. 빈 목차로 본문을 만드는 일을 막는다. (위 표 ⑥의 1회차 빈 응답 문제는 이번 범위 밖이며, 여기서는 재수립 경로만 방어한다.)
- **취소** → 실패가 아니다. 재수립을 감싸는 `catch`에 `when (ex is not OperationCanceledException)` 필터를 단다. `CancellationPolicyTests`가 Roslyn 구문 트리로 자동 검사하므로 누락하면 테스트가 잡는다.

## 테스트

**신규 `StructureRedraftPolicyTests`**
- 첫 미갱신에 `true`를 돌려주고 `Consumed`가 true가 된다
- 두 번째 미갱신에 `false`를 돌려준다 (Job당 1회 상한)
- `improvedThisAttempt: true`면 항상 `false`이고 `Consumed`가 소비되지 않는다

**`VerificationPipelineOrchestratorTests` 추가분**
- **정체 시나리오**: fake Critic이 1·2차 모두 동점 미달 → `DraftBatchPlanStructureAsync`가 2회 호출되고, 2번째 호출에 `previousStructure`와 `redraftFeedback`이 실린다. 3차 본문이 새 목차를 받는다
- **개선 시나리오**: 1차 60점 → 2차 70점 → `Draft`가 1회만 호출된다 (재수립이 함부로 발동하지 않는다는 회귀 방지)
- **산출물**: `PlanStructure.md`가 최종본이고 `PlanStructure.superseded-1.md`가 존재한다
- **재수립 예외** 시 기존 목차로 완주하고 파이프라인이 결과를 반환한다
- **공백 응답** 시 기존 목차가 유지된다
- **L3**: `RedraftStructure=true`면 `Draft` 재호출 후 `Generate`, `false`면 `Draft`가 호출되지 않는다

## 문서 동기화

- `docs/architecture.md` 2번 경로 다이어그램 — 현재 재시도 화살표가 P3로만 돌아가므로 정체 시 P2로 되돌아가는 분기를 반영
- `README.md` 3단계 파이프라인 설명에 재수립 조건 추가
- `AGENTS.md` Core 서비스 목록에 `StructureRedraftPolicy` 등재 (규칙 단독 소유 클래스 계열)

## 완료 기준

- `dotnet clean && dotnet build`에서 경고가 정확히 8건(기존 `DbMetadataServiceTests`의 CS8600/CS8602)
- `dotnet test`가 기존 568건 + 신규분 전부 통과
- 위 문서 3종 동기화 완료
