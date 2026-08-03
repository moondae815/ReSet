# 검증 정직성 후속 과제 설계

- 작성일: 2026-08-03
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 선행 작업: `2026-08-01-verification-outcome-honesty` (병합 완료, `7588a9f`)

## 배경

직전 사이클에서 검증 파이프라인의 네 종료 상태를 `VerificationOutcome` 값으로 모델링하고, 세 종료 영역이 하나의 `VerificationBanner`를 공유하도록 정리했다. 그 상태는 명세서 YAML 헤더(`검증 상태`)와 L3 승인 화면까지 전달된다.

병합 시점에 다섯 건을 후속 과제로 기록했다. 이번 사이클은 그중 다섯 건(A~E)을 닫는다. L2 리뷰 호출 재시도 인프라는 정직성이 아니라 복원력에 관한 변경이고 재시도 횟수·백오프·취소 전파·비용 정책을 새로 정해야 하므로 별도 사이클로 남긴다.

조사 과정에서 D는 애초에 기록해 둔 것보다 큰 문제로 드러났다. 아래 D 절에 기술한다.

## 대상 결함

| # | 결함 | 근거 |
|---|---|---|
| A | 수정 이전 코드가 저장한 캐시 엔트리는 여전히 미검증 문서를 "통과"로 재발행한다 | `VerificationPipelineOrchestrator.cs:164`, `:277` |
| B | dynamic 영역에서 L2 보완본이 L1을 통과해도 배너가 `L1 미통과`로 남는다 | `VerificationPipelineOrchestrator.cs:624`, `:698-723`, `:733` |
| C | 통합 계획서에 검증 상태 헤더가 없다 | `VerificationPipelineOrchestrator.cs:1561`, `Program.cs:729`, `:1177` |
| D | 단일 SP 계획서가 **명세서의** 점수를 자기 점수인 것처럼 싣는다 | `Program.cs:662`, `:1632` |
| E | `SpecHeaderReader`의 별칭 19개 중 2개만 테스트된다 | `SpecHeaderReaderTests.cs` |

### A — 레거시 캐시 엔트리

직전 사이클은 `verificationOutcome == VerificationOutcome.Passed`일 때만 캐시를 쓰도록 고쳤다(`:1141-1155`, `:1171-1183`). 그러나 그 이전에 저장된 엔트리는 종료 상태와 무관하게 기록되어 있고, 무효화 수단이 없다.

캐시 히트 경로는 다음과 같다.

```csharp
var verificationOutcome = VerificationOutcome.Passed;   // :164
...
if (_cacheManager.IsCacheValid(cacheObjectKey, compositeHash, outputPaths))   // :262
{
    ...
    return (cachedSpec, spDef, cachedReview, null, verificationOutcome);      // :277 — 항상 Passed
}
```

레거시 엔트리가 히트하면 파이프라인은 `Passed`를 반환하고, 문서는 `검증 상태: 통과`로 재작성된다. 원래 문서에 붙어 있던 배너는 본문에 그대로 남는다 — `VerificationBanner.L1Exhausted`와 `QualityRejected`는 `> [!CAUTION]`로 시작하는데, `ParseCachedSpecification`의 제거 정규식(`:1522`)은 `> [!NOTE]`만 겨냥하기 때문이다.

결과는 **자기모순 문서**다. YAML 헤더는 `통과`, 본문 최상단은 `[!CAUTION] [품질 불합격] 정합성/가독성 기준 미달`.

`ReviewNotRun` 배너는 `> [!NOTE]`로 시작하므로 그 정규식에 걸려 소실될 수 있다. 이 경우 모순조차 남지 않고 흔적 없는 거짓 통과가 된다.

레거시 엔트리 중 어느 것이 미검증이었는지 판별할 방법은 없다. 그 정보가 애초에 저장되지 않았다.

### B — dynamic 영역의 L1 플래그

```csharp
var consolidatedL1Valid = finalL1.IsValid;              // :606
...
    consolidatedL1Valid = postFixL1.IsValid;            // :624 — 자가 수정 1회 후
...
// L2 결함 보완 재생성
var fixL1Result = _validator.Validate(finalConsolidatedFixResult.Content);   // :698
if (fixL1Result.IsValid)
{
    specificationMarkdown = fixL1Result.CleansedMarkdown ?? ...;             // :701
    // consolidatedL1Valid는 갱신되지 않는다
}
...
if (!consolidatedL1Valid)                               // :733
{
    verificationOutcome = VerificationOutcome.L1Exhausted;
    specificationMarkdown = VerificationBanner.L1Exhausted(...) + specificationMarkdown;
}
```

도달 경로: 자가 수정이 실패해 `consolidatedL1Valid == false` → 그 문서로 L2 검토 → 결함 발견 → 재생성본이 L1 통과. 이때 최종 문서는 L1을 통과했지만 `L1 미통과` 배너를 단다.

방향은 안전 쪽(실제보다 나쁘게 주장)이므로 병합을 막지 않았으나, 배너가 사실과 다르면 배너 자체의 신뢰도가 떨어진다.

### C — 통합 계획서

`RunConsolidatedPipelineAsync`는 `planOutcome`을 정확히 추적하고(`:1655`, `:1703`, `:1715`, `:1805`) 승인 화면에 전달하지만(`:1746`), 반환 튜플은 `(string? Plan, AiResult? Result)`이라 호출부가 그 상태를 알 수 없다. 코드 주석 `:1581-1583`이 이 사실을 명시하고 있다.

`Program.cs`는 헤더를 직접 조립한다.

```csharp
var metadataHeader = $"> [!NOTE]\n> **문서 작성일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n\n";
await File.WriteAllTextAsync(planFileName, metadataHeader + consolidatedPlan);
```

이 두 줄은 `:729`(배치 경로)와 `:1177`(TUI 경로)에 **바이트 단위로 동일하게** 중복되어 있다. `:1633`은 여기에 점수 줄이 붙은 세 번째 변형이다.

배치 모드는 L3 루프 이전에 조기 반환하므로(`:1741`) 사람이 지켜보지 않는 경로일수록 검증 상태가 전혀 기록되지 않는다.

또한 `RunConsolidatedPipelineAsync`에는 단일 SP 경로의 `finalReview`에 대응하는 리뷰 변수가 없다. `:1802-1803`의 주석이 이를 한계로 적어 두고 있으며, 그 때문에 L3 재생성 시 점수를 비울 수단이 없다.

### D — 단일 SP 계획서 (조사 중 확대됨)

`Program.cs:662`에서 생성되는 단일 SP의 `BatchMigrationPlan.md`는 **L1도 L2도 거치지 않는다.**

```csharp
var migrationResult = await aiService.GenerateBatchMigrationPlanAsync(spDef, targetLanguage, globalCts.Token);
migrationPlan = migrationResult.Content;
...
await SaveOutputsAsync(spDef, specMarkdown, migrationPlan, ...);
```

검증 없이 바로 저장된다. 그런데 `:1632`는 이 문서에 명세서의 점수를 찍는다.

```csharp
var scoreHeader = review is null
    ? ""
    : $"> **AI 최종 신뢰도**: {review.NormalizedScore}/100점 (정합성: {review.ScoreAccuracy}, ...)\n";
```

여기서 `review`는 `Spec.md`를 평가한 결과다. 읽는 사람은 이 계획서가 그 점수를 받았다고 이해한다.

원래 기록해 둔 후속 과제는 "점수 헤더가 `review is null`에만 의존한다"였고, 해결책은 `outcome` 게이팅이었다. 그러나 게이팅을 해도 통과 케이스에서는 **여전히 명세서 점수가 계획서 점수인 척한다.** 게이팅은 이 거짓 주장을 줄이지 못한다.

### E — 별칭 커버리지

`SpecHeaderReader`는 19개 키를 인식한다: `검증 상태` 1개, 종합 신뢰도 4개, 정합성 3개, CRUD 3개, 가독성 3개, 예외처리 5개. 현재 테스트는 `종합 신뢰도`와 `정합성 점수` 2개만 확인한다.

소비부의 폴백이 위험을 만든다.

```csharp
// ConsoleUserInteraction.cs:105-110
var score = header.NormalizedScore ?? 100;
var acc = header.Accuracy ?? 10;
var crud = header.Crud ?? 10;
var read = header.Readability ?? 10;
var ex = header.Exception ?? 10;
var scoreFound = header.NormalizedScore.HasValue;
```

별칭 하나를 놓치면 그 항목은 `null` → **만점**으로 폴백한다. `scoreFound`는 종합 점수만 보므로, 종합만 파싱되면 화면에는 진짜 점수와 지어낸 만점이 섞여 표시된다. 이번 사이클이 다루는 결함 유형과 정확히 같다.

폴백값 자체(`?? 10`)는 직전 사이클에서 "원래값 복원"으로 판단된 사항이므로 변경하지 않는다.

## 설계

### 새 컴포넌트: `VerificationDocumentFormatter`

`SpecificationDocumentFormatter`를 이 이름으로 **개명**하고 진입점을 셋으로 늘린다. 얇은 위임을 남기지 않는다 — 프로덕션 호출부가 `DependencyAnalysisOrchestrator`와 `Program.cs` 둘뿐이라 잔재를 남길 이유가 없다.

```csharp
public static class VerificationDocumentFormatter
{
    public static string FormatSpecification(
        string body, ReviewResult? review, VerificationOutcome outcome,
        string provider, string modelName, string? effort, DateTime timestamp);

    public static string FormatConsolidatedPlan(
        string body, ReviewResult? review, VerificationOutcome outcome,
        string provider, string modelName, string? effort, DateTime timestamp);

    public static string FormatUnverifiedPlan(
        string body, VerificationOutcome sourceOutcome,
        string provider, string modelName, string? effort, DateTime timestamp);
}
```

세 진입점은 하나의 private 코어를 호출한다. 골격(YAML 래퍼·상태 라벨·`> [!NOTE]` 메타 블록·`statusNote`)은 현재 `SpecificationDocumentFormatter.Format`의 것을 그대로 쓴다.

`FormatConsolidatedPlan`의 점수 노출 규칙은 `FormatSpecification`과 동일하다.

```csharp
var showScores = review is not null &&
    outcome is VerificationOutcome.Passed or VerificationOutcome.QualityRejected;
```

`FormatUnverifiedPlan`에는 이 규칙이 없다. 점수 파라미터 자체가 없기 때문이다.

개명에 따라 `ConsoleUserInteraction.cs:101`의 주석에 있는 타입 이름도 함께 갱신한다.

#### 왜 명세서와 계획서의 포매터를 나누는가

두 문서는 같은 5개 점수 필드를 쓰지만 의미가 다르다. `AiService.cs:1997-2017`의 통합 계획서 평가 기준과 명세서 기준을 비교하면:

| 필드 | 명세서 YAML 주석 | 통합 계획서 평가 기준 |
|---|---|---|
| `ScoreAccuracy` | SQL 대비 기능 정합성 | Business Logic and Flow Accuracy |
| `ScoreCrud` | 데이터 변경 및 조회 검증 | 파이프라인 청킹/순서(Paging Reader) 검증 |
| `ScoreInterface` | 파라미터 및 반환셋 정합성 | Integration and Interface Definition |
| `ScoreReadability` | 코드 가독성 및 표준 준수 | **Diagram Syntax** and Readability |
| `ScoreException` | 트랜잭션 격리 및 에러 처리 | Exception Handling, Transaction and Isolation Policy |

`SpecificationDocumentFormatter`를 그대로 재사용하면 계획서에 틀린 설명 주석이 박힌다. 반대로 골격까지 복제하면 직전 사이클의 교훈 — 같은 결함이 중복된 삽입부를 따라 다섯 번 반복됐다 — 을 그대로 재현한다.

따라서 **차이나는 것만 분리한다.** 5개 설명 문자열을 담는 내부 테이블을 두고, 골격은 공유한다.

```csharp
private sealed record ScoreLabels(
    string Overall, string Accuracy, string Crud,
    string Interface, string Readability, string Exception);

private static readonly ScoreLabels SpecificationLabels = new(...);
private static readonly ScoreLabels PlanLabels = new(...);
```

#### `FormatUnverifiedPlan`이 `ReviewResult`를 받지 않는 이유

단일 SP 계획서는 자기 검증을 갖지 않으므로 실을 수 있는 점수가 없다. 파라미터를 두지 않으면 향후 어떤 호출부도 점수를 유출시킬 수 없다 — 없는 파라미터는 전달될 수 없다. 이것이 조건 분기로 막는 것보다 강한 보장이다.

출력 형태:

```
---
검증 상태: 검증 없음 # 이 계획서는 L1/L2 검증을 거치지 않음
근거 명세서 검증 상태: 통과
---

> [!NOTE]
> **문서 작성일시**: 2026-08-03 14:22:01
> **분석 AI 정보**: anthropic (claude-opus-5, Effort: high)
> **검증 상태**: 이 계획서는 검증 파이프라인을 거치지 않았습니다. 근거 명세서(Spec.md)는 '통과' 상태입니다.

(계획서 본문)
```

점수를 보려면 같은 폴더의 `Spec.md`를 보면 되므로 정보 손실이 없다.

`검증 없음`은 `VerificationOutcome`에 새 멤버로 추가하지 **않는다.** 이 문서는 파이프라인에 진입한 적이 없어 종료 상태를 갖지 않으며, 존재하지 않는 상태를 열거형에 만들면 다른 분기들이 그것을 처리해야 하는 것처럼 보이게 된다. 라벨은 `FormatUnverifiedPlan`이 렌더링하는 고정 문자열이다.

### A: 캐시 포맷 버전

```csharp
// CacheEntry.cs
public int FormatVersion { get; set; }   // 레거시 JSON에는 이 키가 없어 0으로 역직렬화된다
```

`System.Text.Json`은 없는 키를 기본값으로 두므로 별도 컨버터가 필요 없다.

```csharp
// CacheManager.cs
private const int CurrentCacheFormatVersion = 1;
```

`IsCacheValid`에서 `TryGetEntry` 성공 직후, 파일 읽기와 해시 계산 이전에 조기 반환한다.

```csharp
if (entry.FormatVersion != CurrentCacheFormatVersion)
{
    Log.Information(
        "캐시 미스(포맷 버전 {EntryVersion} != {CurrentVersion}) - 코드 객체: {ObjectKey}",
        entry.FormatVersion, CurrentCacheFormatVersion, cacheKey);
    return false;
}
```

`<`가 아니라 `!=`인 이유: 신버전으로 캐시를 쌓은 뒤 구버전 바이너리로 롤백하면 `<` 검사는 구버전이 해석할 수 없는 엔트리를 히트시킨다. `!=`는 양방향 모두 안전하고, 대가는 롤백 왕복 시 재분석뿐이다.

`UpdateCache`는 엔트리 생성 시 `FormatVersion = CurrentCacheFormatVersion`을 기록한다.

`MigrateLegacyCaches`는 변경하지 않는다. 하위 디렉터리 인덱스를 전역 인덱스로 병합하는 동작은 그대로 두고, 병합된 엔트리는 `FormatVersion = 0`인 채로 남아 다음 조회에서 미스가 된다. 병합 시점에 버전을 채워 넣으면 무효화의 목적이 정확히 무너진다.

**사용자 영향**: 기존 캐시 전 항목이 1회 재분석된다. 대규모 출력 디렉터리에서는 실질적인 AI 비용이다. 이는 직전 사이클에 확립한 원칙 — 재분석 비용은 감수해도 거짓 통과는 안 된다 — 의 직접적 귀결이며, 어느 레거시 엔트리가 미검증인지 판별할 수단이 없으므로 전량 무효화 외의 선택지가 없다. `CacheManager`는 `IUserInteraction`을 갖지 않으므로 별도 알림 없이 객체별 `Log.Information`으로 남긴다.

### B: dynamic 영역 L1 플래그

```csharp
if (fixL1Result.IsValid)
{
    specificationMarkdown = fixL1Result.CleansedMarkdown ?? finalConsolidatedFixResult.Content;
    consolidatedL1Valid = true;                   // 추가
    consolidatedL1Errors = fixL1Result.Errors;    // 추가
    ...
}
```

`else` 분기는 변경하지 않는다. 이전 버전을 유지하므로 기존 플래그가 이미 정확하다.

### C: 통합 계획서

```csharp
// Models
public sealed record ConsolidatedPipelineResult(
    string? Plan,
    AiResult? Result,
    ReviewResult? Review,
    VerificationOutcome Outcome);
```

호출부가 이미 `pipelineResult.Plan` / `.Result`로 접근하므로 레코드 전환에 따른 호출부 변경이 없다. `IVerificationPipelineOrchestrator`의 시그니처도 함께 바꾼다.

`RunConsolidatedPipelineAsync` 내부에 `ReviewResult? planReview`를 추가하고 `planOutcome`과 짝지어 갱신한다.

| 지점 | `planOutcome` | `planReview` |
|---|---|---|
| L1 재시도 소진 `:1655` | `L1Exhausted` | `null` (L2 미수행) |
| L2 품질 미달 `:1703` | `QualityRejected` | `l2Result` |
| L2 리뷰 미수행 `:1711` | `ReviewNotRun` | `null` |
| 통과 `:1722` | `Passed` (초기값 유지) | `l2Result` |
| L3 피드백 재생성 `:1804` | `ReviewNotRun` | `null` |

마지막 줄이 명세서 경로 `:1451-1453`과의 대칭을 완성한다. 그 경로는 재생성 후 `finalReview = null`과 `verificationOutcome = ReviewNotRun`을 함께 설정하며 **배너를 붙이지 않고** YAML 헤더에만 의존한다. 계획서도 동일하게 처리한다.

배치 모드 조기 반환(`:1741`)과 L3 승인 반환(`:1750`) 모두 새 레코드를 반환한다. 취소 경로(`:1754`)는 `Plan`이 `null`이므로 호출부가 이미 조기 분기한다.

`Program.cs`의 세 호출부는 다음으로 대체된다.

| 위치 | 대체 |
|---|---|
| `:727-730` (배치 통합) | `FormatConsolidatedPlan(plan, result.Review, result.Outcome, ...)` |
| `:1175-1178` (TUI 통합) | `FormatConsolidatedPlan(plan, result.Review, result.Outcome, ...)` |
| `:1629-1633`, `:1650` (단일 SP) | `FormatUnverifiedPlan(migrationPlan, outcome, ...)` |

세 번째에서 `scoreHeader`와 `metadataHeader` 지역 변수가 완전히 사라진다.

### D: 단일 SP 계획서

C의 세 번째 호출부 교체가 D의 해결이다. 별도 변경은 없다.

### E: 별칭 커버리지

`[Theory]` + `[InlineData]`로 필드별 별칭을 고정한다.

| 테스트 | 케이스 수 |
|---|---|
| `Read_AcceptsEveryOverallScoreAlias` | 4 |
| `Read_AcceptsEveryAccuracyAlias` | 3 |
| `Read_AcceptsEveryCrudAlias` | 3 |
| `Read_AcceptsEveryReadabilityAlias` | 3 |
| `Read_AcceptsEveryExceptionAlias` | 5 |
| `Read_ParsesVerificationStatusKey` | 1 |

여기에 정규화 순서를 고정하는 테스트를 더한다. 현재 코드는 `#` 주석 → `(` 괄호 → `/` 분모 순으로 벗겨내며, `정합성 점수: 9/10 # SQL 대비 기능 정합성`처럼 둘이 동시에 있는 실제 출력 형태가 이 순서에 의존한다.

기존 5개 테스트는 변경하지 않는다.

## 에러 처리

프로젝트 규약을 그대로 따른다.

- `CacheManager`의 버전 게이트는 예외를 던지지 않는다. 캐시 미스는 정상 흐름이며 기존 soft-fail 구조 안에 있다
- 포매터는 순수 함수이며 IO·AI 호출이 없어 예외 경로가 없다
- `RunConsolidatedPipelineAsync`의 반환 타입 변경은 `OperationCanceledException` 전파 경로를 건드리지 않는다
- Spectre.Console 출력에 새로 들어가는 런타임 값이 없다. 포매터 출력은 파일로만 나간다

## 테스트 전략

| 항목 | 성격 | RED 시작점 |
|---|---|---|
| A 버전 게이트 | 단위 4건, 임시 디렉터리 | `FormatVersion` 키 없는 인덱스 JSON이 히트하는 것을 확인 |
| B L1 플래그 | dynamic 영역 통합 | L1 통과 문서에 `L1 미통과` 배너가 붙는 것을 확인 |
| C 통합 계획서 | 반환값 + 포매터 출력 | `BatchMigrationPlan.md`에 `검증 상태`가 없는 것을 확인 |
| D 단일 SP 계획서 | 포매터 출력 | 계획서에 명세서 점수가 실리는 것을 확인 |
| E 별칭 | 순수 단위 19+1 | 미검증 별칭이 `null`로 떨어지는 것을 확인 |

A의 첫 번째 테스트가 이 작업의 실제 회귀 방지선이다. 현재 코드에서 돌리면 캐시 히트가 나므로 RED으로 시작한다.

**B가 유일한 위험 요소다.** `_actorEffort == "dynamic"` 영역에 진입시키고 `_validator.Validate`를 호출 순서에 따라 다른 결과로 스텁해야 한다(1차 무효 → 자가 수정 후 무효 → L2 보완 후 유효). 구현 계획 수립 시 `VerificationPipelineOrchestratorTests`에 이 영역의 기존 커버리지가 있는지 먼저 확인한다. 없다면 스텁 구축이 해당 태스크의 대부분을 차지한다.

## 범위 밖

- **L2 리뷰 호출 재시도 인프라.** `:1109-1116`과 `:1711-1719`는 일시적 API 오류 한 번에 `break`하며, `_maxAttempts`가 남아 있어도 재시도하지 않는다. 재시도 횟수·백오프·취소 전파·비용 정책을 새로 정해야 하므로 별도 사이클로 남긴다
- **`ConsoleUserInteraction.cs:105-109`의 폴백값.** 직전 사이클에서 판단된 사항이다
- **`ParseCachedSpecification`의 배너 제거 정규식.** A의 버전 게이트로 레거시 엔트리가 히트하지 않게 되고, 새 정책상 통과 문서만 캐시되므로 제거할 배너 자체가 없다
