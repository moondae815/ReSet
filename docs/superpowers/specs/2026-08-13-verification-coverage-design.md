# 검증 커버리지 표기 설계

**작성일**: 2026-08-13
**상태**: 설계 확정

## 목표

산출물이 품질(점수)과 함께 **검증량**을 말하게 한다. 지금은 둘 다 품질만 말하고,
검증이 얼마나 실행됐는지는 어디에도 없다.

## 배경 — 세 회차가 같은 자로 재보였다

| | 단계 수 | 하한 검사 실행 | 오류코드 보존 | 문서 크기 | 점수 |
|---|---|---|---|---|---|
| POQSettleProc6 | 33 | **1 / 33** | 76/76 | 455 KB | 88 |
| POQSettleProc7 | **0** (분할 미실행) | **0 / 0** | **56/76** | 40 KB | **92** |
| POQSettelProc8 | 19 | **17 / 19** | 76/76 | 318 KB | 88 |

Proc7은 문서가 8배 작고 코드 블록이 3분의 1이며 원본 오류코드 20개가 사라졌는데
**가장 높은 점수를 받았다.** 짧고 깔끔한 문서가 읽기 좋았기 때문이다. Critic은
읽기 좋음을 보고 완전함은 보지 못한다.

Proc8이 이것을 우연이 아닌 것으로 만들었다. 계약을 100% 지킨 문서가 88점,
계약을 20군데 깬 문서가 92점이다.

## 배경 — 지시서 §0도 같은 방식으로 침묵한다

`InstructionEntryPointComposer.PlanVerificationSection`은
`HasUnverifiableSteps` 불리언 하나로 단서를 붙일지 정하고, 그 값은
`InstructionBundleWriter.cs:202`에서 `Layout.FloorViolations`에 `Unverifiable`
종류가 있는지로만 계산된다.

**단계가 아예 없으면 위반도 없다.** 그래서 플래그가 꺼지고, 가장 적게 검증된
문서가 가장 깨끗한 배지를 단다. 실측으로 확인했다.

```
Proc8 (19단계, 2개 미검증)  →  ## ⚠️ 0. …  + "다만 … 검증되지 못한 단계가 있습니다"
Proc7 (0단계)               →  ## ✅ 0. …  + 단서 없음
```

Proc7 문서에는 단계별 섹션이 하나도 없고 원본 오류코드 20개가 없다. 이 결함은
`HasUnverifiableSteps`의 doc-comment가 인용하는 유형 그 자체다 — *"§0은 '모두
통과'만 말해서는 안 된다"*.

## 두 문제는 한 뿌리다

문서 헤더(`VerificationDocumentFormatter`)와 지시서 §0
(`InstructionEntryPointComposer`)이 **둘 다 품질만 말하고 검증량을 말하지 않는다.**
그래서 하나의 사실을 만들어 두 표면에 각기 다른 말투로 싣는다.

## 범위

**포함**: 검증 커버리지 값 객체, 문서 헤더의 표기 한 줄, §0 판정 조건 확장.

**제외**: `VerificationOutcome`에 새 상태를 만드는 것(이 설계는 점수를 고치지 않고
점수 옆에 빠진 사실을 놓는다), Critic 프롬프트 수정, 커버리지가 낮을 때 점수를
감추는 것(정보를 버리는 선택이고 `SpecHeaderReader` 같은 기존 소비자를 깨뜨린다).

---

## 설계 1 — 값 객체

```csharp
namespace ReSet.Core.Models;

/// <summary>
/// 이 산출물이 실제로 받은 기계 검증의 양.
///
/// 점수(ReviewResult)와 나란히 놓이지만 다른 것을 잰다. 점수는 읽어 본 품질이고
/// 이것은 대조해 본 분량이다. 실측 세 회차에서 둘이 정반대로 움직였다 —
/// 계약을 20군데 깬 문서가 92점, 100% 지킨 문서가 88점이었다.
/// </summary>
public sealed record VerificationCoverage(
    int? StepsTotal,
    int StepsVerified,
    bool HasDocumentCodeGap);
```

### 왜 `StepsTotal`이 null 허용인가

분할이 실행되지 않은 회차에는 **분모 자체가 없다.** `0/0`으로 찍으면 비율처럼
보여 거짓이 된다. 같은 판단을 오늘 `CliUsage`에서 한 번 했다 — 0은 "재보니
그만큼"이고 null은 "이 경로는 그것을 내지 않는다"이며, 둘을 뭉개면 없는 것이
측정값으로 둔갑한다.

### `StepsVerified` 계산

`StepsTotal`에서 `StepDefectKind.Unverifiable`인 위반의 수를 뺀다.

**`QualityFloor`는 빼지 않는다.** 그것은 검사가 돌았고 떨어진 것이다.
`StepDefect.cs`의 두 값이 이미 그 구분을 담고 있다 — `QualityFloor`는 "재생성으로
고칠 수 있다", `Unverifiable`은 "대조할 재료가 없어 실행하지 못했다". 여기서 둘을
합치면 "검사를 못 돌렸다"와 "검사에서 떨어졌다"가 다시 뭉개진다.

### 계산과 운반

`AttachPipelineBanners` **안에서** 계산한다. 세 재료가 전부 그 메서드의 지역에
있기 때문이다 — `adoptedSteps`와 `stepFloorViolations`는 파라미터로 들어오고,
누락 코드 사전은 이번 브랜치가 그 안에서 만들었다.

따라서 이 메서드의 반환형을 바꾼다.

```csharp
private (string Plan, VerificationCoverage Coverage) AttachPipelineBanners(
    string consolidatedPlan,
    string documentBody,
    IReadOnlyDictionary<string, StepDefect> stepFloorViolations,
    IReadOnlyList<BatchStepPlan>? adoptedSteps,
    List<(string FileName, string Content)> specs,
    string jobName)
```

누락 사전을 밖에서 다시 계산하지 않는다. 같은 사실을 두 곳이 각자 만들면
갈라지고, 이 저장소는 그 실패를 이미 여러 번 겪었다(`TryLocateStepsBlock`의
주석이 같은 이유를 적어 두고 있다).

호출부는 두 곳(`:2153`, `:2395`)이며 둘 다 튜플을 분해해 커버리지를 결과로 나른다.

운반은 `ConsolidatedPipelineResult`에 필드를 하나 더해서 한다.

```csharp
public sealed record ConsolidatedPipelineResult(
    string? Plan,
    AiResult? Result,
    ReviewResult? Review,
    VerificationOutcome Outcome,
    PlanLayout? Layout = null,
    VerificationCoverage? Coverage = null);   // 신규
```

`PlanLayout`에 넣지 않는 이유는 성격이 다르기 때문이다. `PlanLayout`은 문서의
*구조*(골격·섹션·단계·위반)를 담고, 커버리지는 그 구조를 얼마나 *검사했는가*다.
`Layout`과 형제로 두면 두 관심사가 각자 자기 이름으로 남는다.

---

## 설계 2 — 두 표면

사실은 하나, 말투는 둘이다. 사람은 판단 근거가 필요하고 에이전트는 행동 지침이
필요하다.

### 2.1 문서 헤더 — 사람용

```yaml
---
검증 상태: 통과
종합 신뢰도: 88
단계 검증: 17/19
정합성 점수: 9/10
...
---
```

분할이 없었으면 비율 대신 상태를 적는다.

```yaml
단계 검증: 미실행 (목차가 단계 목록을 내지 못함)
```

`VerificationDocumentFormatter.FormatVerifiedDocument`에 **선택적** 파라미터로
추가한다. 이 포매터는 단일 SP 명세서와 통합 계획서가 함께 쓰는데 단계 개념은
계획서에만 있으므로, 값이 없으면 줄 자체를 만들지 않는다. 파일 안에 이미 같은
패턴이 있다 — `scope`가 "계획서 진입점에서는 항상 null이라 이 줄 자체가 생기지
않는다"고 주석에 적혀 있다.

호출부는 5곳(`DependencyAnalysisOrchestrator.cs:521`, `Program.cs`의 4곳)이지만
값을 넘기는 곳은 통합 계획서 경로뿐이고 나머지는 손대지 않는다.

### 2.2 지시서 §0 — 에이전트용

`PlanVerificationSection(VerificationOutcome, bool hasUnverifiableSteps)`의
불리언을 `VerificationCoverage?`로 교체한다. `EntryPointInputs`의
`HasUnverifiableSteps`도 같이 교체하고, `InstructionBundleWriter.cs:202`의 계산은
`ConsolidatedPipelineResult.Coverage`를 그대로 전달하는 것으로 바뀐다.

`Passed`일 때 단서를 붙이는 조건이 하나에서 셋으로 늘어난다.

| 조건 | 원천 |
|---|---|
| 분할 미실행 | `StepsTotal == null` |
| 미검증 단계 존재 | `StepsVerified < StepsTotal` |
| 문서 전체 오류코드 누락 | `HasDocumentCodeGap` |

셋 중 하나라도 참이면 ⚠️와 단서를 붙인다. 문구는 참인 조건만 나열한다 — 해당
없는 사유를 적으면 읽는 사람이 실제 결함을 흘려보낸다.

Proc7 시나리오의 결과.

```markdown
## ⚠️ 0. 이 계획서의 검증 상태

**통과** — L1 기계 검증과 L2 AI 교차 리뷰를 모두 통과한 계획입니다.
다만 목차가 단계 목록을 내지 못해 단계 단위 기계 검증이 실행되지 않았고, 원본
오류코드 일부가 문서에서 확인되지 않았습니다. 구현 전에 사람의 확인이 필요합니다.
```

`Passed`가 아닌 경로는 이미 ⚠️와 "사람의 검토가 필요합니다"를 쓰므로 바꾸지 않는다.

---

## 테스트 전략

### 값 객체

| 확인할 것 |
|---|
| `Unverifiable` 위반만 `StepsVerified`에서 빠진다 |
| `QualityFloor` 위반은 빠지지 않는다 (검사가 돌았고 떨어진 것) |
| 분할 미실행이면 `StepsTotal`이 null이고 `0`이 아니다 |

두 번째가 이 설계의 핵심 회귀 테스트다. 뭉개져도 숫자가 그럴듯해 눈으로는
드러나지 않는다.

### 문서 헤더

| 확인할 것 |
|---|
| 계획서 경로에 `단계 검증: 17/19`가 실린다 |
| 분할 미실행이면 비율 대신 "미실행" 표기가 실린다 |
| **명세서 경로(값 없음)에는 줄 자체가 생기지 않는다** |

세 번째를 빠뜨리면 단일 SP 명세서마다 무의미한 `단계 검증:` 줄이 붙는다.

### §0

| 확인할 것 |
|---|
| 세 조건 각각이 단독으로 ⚠️와 단서를 유발한다 |
| 셋 다 거짓이면 ✅이고 단서가 없다 |
| 문구가 참인 조건만 나열한다 |
| `Passed`가 아닌 경로의 출력이 바뀌지 않는다 |

두 번째가 부재 확인 테스트다. 조건이 뒤집히면 정상 산출물마다 거짓 경고가 붙는데,
그것을 잡는 테스트는 이것뿐이다.

## 검증 방법

단위 테스트로는 "값이 렌더링됐다"까지만 증명된다. 실제 대비 효과는 다음 배치
실행에서 확인한다 — Proc8을 재실행하면 헤더에 `단계 검증: 17/19`가, §0에 기존
단서가 그대로 나와야 하고, 분할이 실패한 회차에서는 §0이 ✅ 대신 ⚠️를 달아야 한다.

## 위험

**커버리지가 낮은데 점수가 높은 상태가 그대로 남는다.** 이 설계는 그 모순을
해소하지 않고 **보이게** 한다. 점수 자체를 완전성에 연동하려면 Critic 프롬프트와
채점 기준을 바꿔야 하고, 그것은 이 설계의 범위 밖이다. 다만 읽는 사람이 88점과
92점 중 어느 쪽을 믿을지 판단할 재료는 생긴다.

**`StepsVerified`가 `Unverifiable`만 제외하므로, 하한 미달로 떨어진 단계도
"검증됨"으로 집계된다.** 의도된 것이다. 그 단계는 별도의 하한 미달 배너가 이미
이름으로 지목한다.

## 남은 후속

- Critic 채점에 완전성 축을 넣을지 (이 설계가 만든 재료로 판단)
- POQSettleProc9 실행 시 헤더·§0 실물 확인
