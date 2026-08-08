# 조용한 실패 세 경로를 닫는 설계

> 대상: `PlanBoundaryResolver`, `CodegenWorkflowOrchestrator`(레거시 경로),
> `BatchStepPlanParser` / `PlanStructureEnricher`

## 배경

선행 두 브랜치의 「남은 후속 작업」에 기록된 14건 중 세 건은 성격이 같다.
**실패가 표면에 드러나지 않아 사람이 알아챌 방법이 없다.**

- 계획서의 일부 구간이 어느 산출물 파일에도 담기지 않고 사라진다. 로그도 배너도 없다.
- 무인 배치가 끝나지 않는다. 실패로 보이지 않고 "계속 도는 중"으로 보인다.
- 목차 파일에 기록된 단계 목록과 파이프라인이 실제로 쓰는 단계 목록이 갈릴 수 있다.

나머지 11건은 이번 범위 밖이다. 그것들은 증상이 드러나거나(오탐·비용), 이미 알려진 채로
관리되고 있다.

### 세 결함의 현재 상태

**결함 1 — `PlanBoundaryResolver.Resolve`의 성공 경로에 고아 구간이 셋 있다.**

골격 H2 넷을 모두 찾은 경로(`PlanBoundaryResolver.cs:314-349`)가 만드는 조각의 기하는
다음과 같다. `p0..p3`은 `MechanicalValidator.RequiredConsolidatedHeaders` 네 개의 줄 인덱스다.

```
preamble      [0,         p0)
architecture  [p0,        p2)            ← 단계 분할 성공 시. 실패 시 [p0, p3)
                 ✗ [p2,   p2+1)          "단계별 이행 상세" H2 줄 자체
stepContract  (p2+1,      firstStep)
steps         [firstStep, lastEnd)
                 ✗ [lastEnd, p3)         마지막 단계 끝과 검증 SQL H2 사이
verification  [p3,        verificationEnd)
                 ✗ [verificationEnd, 끝) 검증 SQL 섹션 뒤의 모든 것
```

`Materialize`는 마지막 단계를 **다음 `## ` 헤딩**에서 끊는다(`:210`). 그 헤딩이 검증 SQL이
아니면(`## 부록`, `## 성능 고려사항` 등) 그 구간 전체가 사라진다. `verificationEnd` 역시
문서 끝이 아니라 다음 H2이므로, 검증 SQL **뒤**에 붙은 절도 같은 방식으로 사라진다.

골격 탐색이 실패한 분기(`!allFound`)는 이미 `BuildWholeSkeletonAroundSteps`로 꼬리를
개요에 흡수한다(`:397-422`). 그 주석이 이유를 이미 적어 두었다 — "여기 담기지 않은 구간은
에이전트가 읽을 방법이 아예 없다." 성공 경로에만 그 대응물이 없다.

**결함 2 — 레거시 Job 경로의 `nothingVerified`가 무한히 재시도한다.**

`CodegenWorkflowOrchestrator.RunSelfHealingWorkflowAsync`는 `consecutiveNoArtifactRetries`
캡만 갖는다(`:99`). 산출물은 나왔는데 검증 대조 쌍을 하나도 찾지 못한 상태
(`nothingVerified`, `:129`)에는 상한이 없다. `MaxL2Attempts: "unlimited"`면
`maxAttempts = int.MaxValue`이므로 무인 배치에서 끝나지 않는 유료 기동이 된다.

회차 경로는 같은 상황을 `MaxConsecutiveUnverifiedRetries`로 막는다(`:469`).

게다가 두 경로는 **비대칭**이다. 회차 경로는 `gate.Feedback`을 작업 파일에 붙이고 나서
재시도한다(`:461`). 레거시 경로는 `failedResults.Count > 0`일 때만 피드백을 붙이므로
(`:153`), `nothingVerified` 재시도는 에이전트에게 아무 신호도 주지 않은 채 같은 명령을
다시 던지는 것이다.

**결함 3 — 목차 ```json 블록을 두 곳이 따로 고른다.**

`BatchStepPlanParser`(`BatchStepPlan.cs:46`)와 `PlanStructureEnricher`(`:26`)는 바이트 단위로
같은 정규식 리터럴을 갖지만 유효성 판정이 다르다.

| | 파서 | 보강기 |
|---|---|---|
| `Steps`가 배열이 아님 | 버림 | 버림 |
| `Code`/`Name`이 빠진 항목이 있음 | **전체를 버림** | 받아들임 |
| `Steps`가 비었거나 40개 초과 | **버림** | 받아들임 |
| 프로퍼티 이름 중복 | `JsonDocument`는 통과(마지막 값 승) | `JsonNode`가 던짐 → 버림 |

```json 블록이 둘 이상이고 첫 블록이 위 어느 칸에 걸리면 **두 곳이 서로 다른 블록을 고른다.**
파일에 기록된 목차와 실제로 파이프라인이 쓰는 목차가 갈라지며, 이는
`2026-08-08-step-error-code-verification-design.md` §2가 명시적으로 금지한 상황이다.

## 목표와 범위

세 경로가 실패를 감추지 못하게 한다.

**범위 안**

- `PlanBoundaryResolver.Resolve` 성공 경로의 고아 구간 흡수
- 레거시 자가 수정 루프의 `nothingVerified` 피드백과 연속 캡
- ```json 블록 선택기의 단일화(정규식 리터럴 중복 해소 포함)

**범위 밖**

나머지 후속 작업 11건은 손대지 않는다. 특히 다음은 이 브랜치에서 무변경이다.

- `MechanicalValidator.RequiredConsolidatedHeaders`의 내용과 순서
- 배너 문구, 배너 종류, `VerificationBanner` 전반
- `TargetTables` 보강, 오류코드 추출기의 동작
- `OpenAiClient`의 재시도 정책, Claude 클라이언트의 캐시 중단점
- 회차(Staged) 경로의 루프 제어 — 이번에 고치는 것은 레거시 경로뿐이다

## 설계

### 1. 계획서 조각이 모든 줄을 담는다

**불변식: 최종 계획서의 모든 줄은 적어도 하나의 조각에 담긴다.**

세 고아 구간을 각각 따로 막지 않는다. 사례를 하나씩 패치하면 다음에 생기는 네 번째
고아 구간을 또 놓친다. 대신 조각이 덮은 범위를 모아 **덮이지 않은 구간을 계산**하고,
남은 것을 전부 개요 조각에 흡수한다.

#### 1-1. 덮인 범위의 목록

`Resolve`의 성공 경로가 만드는 조각은 다섯이고, 각각 줄 범위 `[Start, End)`로 표현된다.

| 조각 | 범위 |
|---|---|
| Preamble | `[0, p0)` |
| Architecture(본체) | `[p0, architectureEnd)` — `architectureEnd`는 단계 분할 성공 시 `p2`, 실패 시 `p3` |
| StepContract | `[p2, firstStep)` — 아래 1-2 참조. 없으면 목록에 넣지 않는다 |
| Steps | `[firstStep, lastEnd)` — 단계 분할 성공 시에만 |
| Verification | `[p3, verificationEnd)` — `verificationEnd < 0`이면 `lines.Count` |

단계 조각들은 서로 맞닿아 있으므로(`Materialize`가 각 단계를 다음 단계 시작 직전까지 자른다)
한 덩어리로 다뤄도 된다.

#### 1-2. 공통 규약이 자기 헤딩을 갖는다

지금 `stepContract`는 `[p2+1, firstStep)`이라 `## 단계별 이행 상세 및 의사코드` 줄이
어느 조각에도 없다. 이 줄을 공통 규약 조각의 첫 줄로 옮긴다.

함정이 하나 있다. 지금은 잘라낸 본문이 비면 `stepContract = null`이 되어
`HasStepContract = false`가 되고 진입점이 `common/01-step-contract.md`를 링크하지 않는다
(`InstructionBundleWriter.cs:61`). 헤딩 줄을 무조건 앞에 붙이면 **헤딩 하나뿐인 파일**이
생기고 진입점이 그것을 링크하게 된다. 회차마다 읽히는 빈 파일이 는다.

그래서 **비었는지는 산문만 보고 판정한다.**

```csharp
// 판정은 산문에만 건다. 헤딩은 산문이 있을 때만 따라붙는다.
string? stepContract = null;
if (steps.Split && steps.FirstStepLineIndex > positions[2])
{
    var prose = Join(lines, positions[2] + 1, steps.FirstStepLineIndex);
    if (prose.Length > 0)
    {
        stepContract = Join(lines, positions[2], steps.FirstStepLineIndex);
    }
}
```

산문이 비면 `stepContract`는 종전대로 `null`이고, 남겨진 헤딩 줄은 1-3의 흡수 대상이 된다.
같은 기계가 처리하므로 분기를 새로 만들지 않는다.

#### 1-3. 덮이지 않은 구간을 계산해 개요에 흡수한다

빈틈 계산은 `PlanBoundaryResolver`의 공개 정적 함수로 분리한다. `Resolve`는 프로세스도
파일도 끼지 않지만 입력이 계획서 전문이라 조합을 세우기 번거롭다. 빈틈 계산만 떼면
인덱스만으로 전 조합을 검증할 수 있다. 이 레포에는 `internal` + `InternalsVisibleTo`
선례가 없으므로 `public`으로 노출한다.

```csharp
/// <summary>
/// [0, lineCount) 중 covered가 덮지 않은 구간을 오름차순으로 돌려준다.
///
/// 조각 하나를 새로 추가할 때 이 목록에 범위를 넣는 것을 잊으면 그 구간이
/// 개요에 중복으로 실린다. 반대로 범위를 넣고 조각을 안 만들면 구간이 사라진다.
/// 둘 다 <b>조용하지 않다</b> - 전자는 회차 입력이 부풀고 후자는 테스트가 잡는다.
/// </summary>
public static IReadOnlyList<(int Start, int End)> FindUncoveredRanges(
    int lineCount, IEnumerable<(int Start, int End)> covered)
```

동작: `End <= Start`이거나 범위를 벗어난 항목을 버리고, `Start` 오름차순으로 정렬한 뒤
겹침을 합쳐 커서를 전진시키며 빈틈을 모은다. 겹치는 범위(문서가 기형이라 단계가 검증 SQL
뒤에서 시작하는 경우 등)는 합쳐지므로 빈틈을 잘못 만들지 않는다.

흡수는 기존 분기와 같은 모양이다.

```csharp
var uncovered = FindUncoveredRanges(lines.Count, covered);
foreach (var range in uncovered)
{
    var text = Join(lines, range.Start, range.End);
    if (text.Length == 0) continue;   // 공백뿐인 구간은 담을 것이 없다

    architecture = architecture.Length == 0 ? text : architecture + "\n\n" + text;
    Log.Information(
        "어느 조각에도 속하지 않은 구간을 개요에 흡수했습니다 - 줄 [{Start}, {End})",
        range.Start, range.End);
}
```

**개요에 담는 이유**: 개요는 `common/00-architecture.md`가 되어 모든 회차가 무조건 읽는
유일한 파일이다(`TaskFileComposer.Compose`의 "먼저 읽을 것" 2번). 어느 회차가 그 구간을
필요로 하는지 판별하지 못한 상태이므로, 판별 없이도 반드시 읽히는 자리에 둔다.
`!allFound` 분기가 이미 같은 판단을 내렸다.

**배너를 올리지 않는다**: 이것은 결함 보고가 아니라 복구다. 사용자에게 조치를 요구하지
않으므로 `Warnings`에 넣지 않고 `Log.Information`으로만 남긴다. `!allFound` 분기와 같은
수준이다.

**대가**: 고아 구간이 크면 회차마다 그만큼 토큰을 더 낸다. 그래도 담는다 — 읽히지 않는
것보다 낫고, 흡수 사실이 로그에 남아 원인을 찾을 수 있다.

### 2. 레거시 루프가 반드시 끝난다

`RunSelfHealingWorkflowAsync`에 회차 경로와 대칭인 두 가지를 넣는다.

#### 2-1. 피드백

`nothingVerified`일 때 붙일 문구를 만드는 순수 함수를 `CodegenLoopPolicy`에 둔다.
`CodegenWorkflowOrchestrator`는 프로세스와 검증기가 얽혀 목으로 감쌀 수 없고, 이미 같은
이유로 `Decide`가 그 파일로 분리돼 있다. 그 선례를 따른다.

```csharp
/// <summary>
/// 대조 쌍을 하나도 찾지 못했을 때 지시서에 붙일 피드백.
///
/// 이것이 없으면 재시도는 같은 명령을 신호 없이 다시 던지는 것이다.
/// </summary>
public static string BuildUnverifiedFeedback(string specDir, string codeDir, int attempt)
```

문구가 담아야 하는 것:

- 머리글에 시도 회차 — 지시서 끝에 여러 번 붙어도 어느 시도의 것인지 구별된다
- 설계서 디렉터리와 소스 디렉터리의 실제 경로
- 대조 규약: 검증기는 설계서 폴더명에서 스키마를 뗀 이름(`dbo.CustOrderHist` →
  `CustOrderHist`)과 같은 이름의 소스 **파일** 또는 같은 이름의 **폴더**를 찾는다
  (`FileMappingService.cs:135-160`)
- 요구: 생성한 파일·폴더 이름이 그 규약을 따르는지 확인할 것

`failedResults`가 있을 때의 기존 피드백(`BuildCriticFeedback`)은 그대로 둔다. 두 상황은
배타적이다 — `nothingVerified`는 `validationResults.Count == 0`이므로 `failedResults`도 비어 있다.

#### 2-2. 연속 캡

```csharp
// 검증 대조 쌍을 하나도 찾지 못한 시도의 연속 횟수. 회차 경로(:457)와 같은 성격이다.
int consecutiveUnverified = 0;
```

- `nothingVerified`면 증가시키고, 대조가 한 번이라도 성립하면(`validationResults.Count > 0`)
  0으로 리셋한다
- 피드백을 붙인 뒤 `consecutiveUnverified >= MaxConsecutiveUnverifiedRetries`(=2)면
  루프를 끝낸다

순서가 중요하다. **피드백을 먼저 붙이고 캡을 판정한다.** 마지막 시도에서 접더라도 지시서에는
이유가 남아 사람이 열어 볼 수 있다. 회차 경로가 같은 순서다(`:461` → `:469`).

상수는 새로 만들지 않고 기존 `MaxConsecutiveUnverifiedRetries`를 재사용한다. 두 경로가
같은 상황을 다른 숫자로 접으면 운영자가 둘을 구별해 기억해야 한다.

**`BuildAbortResult`를 쓰지 않는다.** 그 헬퍼는 `CliFailureClassifier.ToCodegenAbortException`
으로 사유를 만든다(`:806`). 즉 "CLI 기동이 실패했다"는 전제의 안내문이라 설치 여부나
`CodegenSettings:Engines:<name>:Command`를 확인하라고 말한다. `nothingVerified`는 **기동이
성공하고 산출물까지 나온** 상황이므로 그 안내는 틀렸다. 사유를 직접 만들어
`new CodegenWorkflowResult(false, reason)`으로 돌려주고, 로그에는 `Log.Error`로 남긴다.

```
[SelfHealing] 검증 대조 쌍을 찾지 못한 시도가 2회 연속 발생했습니다.
설계서 디렉터리와 소스 디렉터리에서 짝을 찾지 못했습니다 - 설계서: {specDir}, 소스: {codeDir}.
피드백을 붙여도 대조가 성립하지 않으므로 루프를 중단합니다.
```

이 사유는 실행 실패가 아니라 **배치 구성 문제**를 가리킨다. 두 디렉터리 경로를 담아야
사람이 무엇을 볼지 알 수 있다.

### 3. 목차 블록을 한 곳에서 고른다

#### 3-1. 파서가 선택기를 공개한다

보강기는 블록을 **제자리에서 다시 써야** 한다(미지 필드 보존). 그래서 파싱 결과가 아니라
원본 JSON 본문의 **범위**가 필요하다. "보강기가 파서를 호출해 확인한다"로는 해결되지 않고,
블록 선택 자체가 한 곳에 있어야 한다.

```csharp
/// <summary>
/// 목차에서 유효한 단계 목록 블록의 위치와 파싱 결과.
/// </summary>
/// <param name="BodyIndex">원본 마크다운에서 ```json 본문이 시작하는 문자 인덱스.</param>
/// <param name="BodyLength">본문의 길이. 이 구간만 갈아 끼우면 펜스는 보존된다.</param>
public readonly record struct StepsBlockLocation(
    int BodyIndex,
    int BodyLength,
    string Body,
    IReadOnlyList<BatchStepPlan> Steps);

/// <summary>
/// 파서와 보강기가 <b>같은</b> 블록을 고르게 하는 단일 진입점.
///
/// 두 곳이 다른 블록을 고르면 PlanStructure.md에 기록된 목차와 파이프라인이
/// 실제로 쓰는 목차가 갈라진다. 그 불일치는 아무 데도 드러나지 않는다.
/// </summary>
public static StepsBlockLocation? TryLocateStepsBlock(string? planStructureMarkdown)
```

`TryParse`는 이 선택기를 호출해 `.Steps`만 돌려주는 껍데기가 된다. **파싱은 여전히 한 번만
돈다** — 선택기가 파싱 결과를 함께 실어 보내기 때문이다. 로그 문구와 실패 시 `null` 반환은
그대로다.

#### 3-2. 보강기가 자기 정규식을 버린다

`PlanStructureEnricher`의 `JsonBlockRegex` 필드와 블록 순회 루프를 삭제하고 선택기를 쓴다.

```csharp
var located = BatchStepPlanParser.TryLocateStepsBlock(planStructureMarkdown);
if (located == null)
{
    Log.Warning("목차에서 보강할 단계 목록 JSON 블록을 찾지 못했습니다. 원본을 그대로 사용합니다.");
    return planStructureMarkdown;
}

var rewritten = TryRewriteBlock(located.Value.Body, codesByProcedure);
if (rewritten == null)
{
    return planStructureMarkdown;
}

return planStructureMarkdown[..located.Value.BodyIndex]
    + rewritten
    + planStructureMarkdown[(located.Value.BodyIndex + located.Value.BodyLength)..];
```

`TryRewriteBlock`, `MergeCodes`, `ReadStringArray`, `WriteOptions`는 무변경이다.
`TryRewriteBlock`을 감싸는 `catch (Exception ex) when (ex is not OperationCanceledException)`도
그대로 둔다 — 중복 키 블록이 여전히 `ArgumentException`을 던지고, 그것이 파이프라인 밖으로
새 나가면 안 되는 것도 그대로다.

#### 3-3. 바뀌는 동작

보강기는 이제 **뒤 블록으로 넘어가지 않는다.** 파서가 고른 블록의 재작성이 실패하면
원본을 그대로 돌려준다.

이것이 옳은 이유: 다른 블록을 보강하면 파일에 기록된 목차와 실제로 쓰이는 목차가
갈라지는데, 그것이 바로 이 작업이 닫으려는 결함이다. 보강되지 않은 단계는 하한 검사가
**"검증 불가"**로 정직하게 보고한다 — 그 경로는
`2026-08-08-step-error-code-verification-design.md`가 이미 만들어 두었다.

구체적으로 중복 키 블록에서 이렇게 된다.

| | 종전 | 이후 |
|---|---|---|
| 파서 | 그 블록을 읽는다(`JsonDocument`는 마지막 값 승) | 같음 |
| 보강기 | 그 블록을 건너뛰고 **다음 블록**을 보강한다 | 같은 블록을 시도했다 실패하고 **원본 유지** |
| 결과 | 두 목차가 갈라진다 | 갈라지지 않는다. 보강만 안 될 뿐이다 |

## 오류 처리

| 상황 | 동작 |
|---|---|
| 고아 구간이 공백뿐 | 담지 않는다. 개요에 빈 줄만 늘리지 않는다 |
| 고아 구간이 아주 큼 | 그래도 담는다. 로그에 줄 범위가 남아 원인을 추적할 수 있다 |
| 조각 범위가 서로 겹침(기형 문서) | `FindUncoveredRanges`가 병합해 처리한다. 빈틈을 잘못 만들지 않는다 |
| 골격 탐색 실패(`!allFound`) | 무변경. 기존 `BuildWholeSkeletonAroundSteps` 경로가 그대로 처리한다 |
| 지시서 파일이 없어 피드백을 못 붙임 | 기존 `Log.Warning` 경로 그대로. 캡 판정은 그래도 진행한다 |
| `nothingVerified`가 연속이 아님(중간에 대조 성립) | 카운터를 0으로 리셋한다 |
| `TryLocateStepsBlock`이 null | 파서는 종전대로 `null`, 보강기는 종전대로 원본 반환 |
| 선택된 블록의 재작성 실패 | 원본 반환. 예외는 `TryRewriteBlock`이 삼키고 경고를 남긴다 |

세 변경 어디에도 예외를 새로 던지는 경로는 없다. 선행 브랜치가
「보강 실패가 파이프라인을 멈추는 경로는 없다」를 설계에 적어 놓고 실제로는 거짓이었던
전례가 있으므로, 이번에는 **구현 중 각 함수의 예외 탈출 경로를 호출부까지 따라가 확인한다.**
특히 `FindUncoveredRanges`는 인덱스 산술이므로 범위를 벗어난 `Skip`/`Take` 조합이 없는지
본다(`Join`이 이미 방어하고 있으나 새 호출부가 늘어난다).

## 테스트

### 결함 1

| 대상 | 검증 |
|---|---|
| `FindUncoveredRanges` | 빈틈 없음 / 앞·중간·뒤 빈틈 / 겹치는 범위 / `End <= Start` 항목 / 빈 입력 / 범위를 벗어난 항목 |
| `Resolve` — 단계와 검증 SQL 사이 | 마지막 단계 뒤에 `## 부록`을 둔 문서에서 개요에 부록 본문이 담기는지 |
| `Resolve` — 검증 SQL 뒤 | 검증 SQL 다음에 `## 참고`를 둔 문서에서 개요에 참고 본문이 담기는지 |
| `Resolve` — 공통 규약 헤딩 | 산문이 있으면 `StepContract`가 `## 단계별 이행 상세`로 시작하는지 |
| `Resolve` — 공통 규약이 빈 경우 | `StepContract`가 여전히 `null`이고, 헤딩 줄이 개요에 담기는지 |
| 회귀 | 고아 구간이 없는 기존 문서에서 개요 내용이 **바뀌지 않는지** |

마지막 항목이 중요하다. 흡수 기계를 넣고 나서 정상 문서의 개요가 달라지면 회차 입력이
조용히 부푼 것이다.

### 결함 2

카운터도 테스트한다. `CodegenWorkflowOrchestratorTests`가 이미 `ScriptedCodingEngine`으로
실제 `RunSelfHealingWorkflowAsync` 루프를 돌리고 있고, `nothingVerified` 조건(계획서와
소스를 심지 않아 매핑이 0건)을 그대로 재현하는 픽스처도 있다.

| 대상 | 검증 |
|---|---|
| `BuildUnverifiedFeedback` | 시도 회차·두 디렉터리 경로·대조 규약 설명을 담는지 |
| 캡 | `maxL2Attempts`를 무제한(-1)으로 두고도 2회에서 끊기는지(`CallCount == 2`) |
| 리셋 | 1회차 미대조 → 2회차 정상 매핑 통과 시 성공으로 끝나는지 |
| 피드백 | `IMetadataExporter.AppendFeedbackToInstructionsAsync`가 미대조 시도마다 불렸는지 |
| 중단 사유 | 두 디렉터리 경로를 담고, CLI 설정 키(`CodegenSettings:Engines`)를 **담지 않는지** |

**기존 테스트 하나가 깨진다.** `RunSelfHealingWorkflowAsync_NothingWasVerified_ShouldNotReportSuccess`는
`Assert.Null(result.AbortReason)`으로 "산출물을 못 만든 경로가 아님"을 고정하고 있다. 캡이
생기면 그 시점에 사유가 붙으므로 이 단언은 성립하지 않는다. **삭제하지 말고 의도를 유지한
채 바꾼다** — 사유가 있되 그것이 산출물 부재가 아니라 대조 실패를 가리키는지 확인하는
단언으로 교체한다.

### 결함 3

| 대상 | 검증 |
|---|---|
| `TryLocateStepsBlock` | 유효한 블록의 `BodyIndex`/`BodyLength`가 원본에서 정확한 구간을 가리키는지 |
| 두 곳의 일치 | 첫 블록이 `Code` 누락, 둘째가 성한 문서에서 파서와 보강기가 **같은 둘째 블록**을 고르는지 |
| 두 곳의 일치 | 첫 블록의 `Steps`가 비어 있고 둘째가 성한 문서에서 같은지 |
| 중복 키 | 파서는 읽고 보강기는 원본을 유지하는지(다음 블록으로 넘어가지 않는지) |
| 회귀 | 블록이 하나뿐인 정상 목차에서 보강 결과가 종전과 같은지 |
| 정규식 | 블록 추출 정규식 리터럴이 `src/` 아래에 한 번만 나타나는지 |

마지막 항목은 소스 텍스트를 읽는 검사다. 이 레포에는 `RepoPaths.FindRepoRoot()`로 소스를
읽는 선례가 있으므로(`CancellationPolicyScanner.cs:242`) 같은 방식을 쓴다.

찾을 대상은 **정규식 패턴 문자열**(` ```json\s*\r?\n(?<body>.*?)``` `)이지
` ```json ` 이라는 낱말이 아니다. `AiService.cs`는 프롬프트 본문에서 그 낱말을 여러 번
쓰고(`:2025`, `:2431` 등) 그것들은 이 검사의 대상이 아니다. 낱말로 세는 검사를 쓰면
프롬프트를 손질할 때마다 무관한 테스트가 깨진다.

## 문서 동기화

이 브랜치가 끝나면 `/reset-doc-sync`로 세 문서를 갱신한다. 예상되는 갱신 지점은 다음과 같다.

- `docs/architecture.md` 2.2 테이블 — `PlanBoundaryResolver` 행에 "모든 줄이 어느 조각엔가
  담긴다"는 불변식을 한 줄로 추가. `BatchStepPlanParser` 행에 선택기 공개를 반영
- `AGENTS.md` — 계획서 분할 규칙에 "조각을 새로 추가하면 `FindUncoveredRanges`에 범위를
  등록하십시오"를 추가. 체크리스트의 단위 테스트 개수 갱신
- `README.md` — 변경 없을 전망. 설정 키도 사용 방법도 바뀌지 않는다

## 완료 기준

1. 세 결함 각각에 실패를 재현하는 테스트가 먼저 빨간불로 존재했다가 초록이 된다
2. 전체 스위트 통과, 빌드 경고 8개 기준선 유지
3. 정상 문서(고아 구간 없음, 블록 하나)에서 개요 조각과 보강 결과가 종전과 동일하다
4. 나머지 후속 작업 11건에 해당하는 파일은 이 브랜치의 diff에 등장하지 않는다
