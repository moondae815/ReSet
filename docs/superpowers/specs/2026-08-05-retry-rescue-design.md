# 재시도 중단 시 최선본 구제 설계

작성일: 2026-08-05

## 문제

재시도 루프의 실패 경로들이 `BestAttempt`의 존재를 모른다. 이미 L1을 통과하고 채점까지 받은 문서를 확보해 놓고도, 뒤이은 시도가 어떤 이유로든 깨지면 그 문서를 버린다.

[2026-08-04 설계](./2026-08-04-retry-best-attempt-convergence-design.md)에서 L1 소진과 L2 예외 두 경우를 "알려진 한계"로 기록했다. 이번에 코드를 다시 읽으며 **더 심각한 세 번째**를 찾았다.

`VerificationPipelineOrchestrator.cs:988`(배치 쌍둥이 `:1703`)은 재시도 루프 안에서 AI 생성 호출이 예외를 던지면 `return Result(null, ...)`로 **SP 전체를 폐기**한다. 3차 시도가 타임아웃이나 쿼터로 죽으면 2차가 만들어 둔 문서까지 함께 사라진다. 변수에는 그 내용이 그대로 남아 있는데 `genSuccess`가 false라 버려진다.

앞의 둘은 "좋은 문서 대신 나쁜 문서"지만 이것은 "좋은 문서 대신 아무것도 없음"이다.

같은 탐색에서 네 번째를 찾았다. `:1003`(쌍둥이 `:1716`)은 L1 실패 시 `feedbackLog = l1Result.SuggestedPromptFix;`로 통째로 덮어쓴다. 방금 도입한 누적 Critic 피드백이 그 회차 프롬프트에서 빠진다. `feedbackHistory` 자체는 남아 다음 L2 실패 때 되살아나므로 영구 손실은 아니지만, 한 회차는 비어서 나간다. Actor는 매번 백지에서 다시 쓰므로 이 공백은 그대로 품질 손실이다 — 2026-08-04 실행에서 관측된 86→64 붕괴가 맥락을 잃었을 때 무슨 일이 일어나는지 보여준다.

넷 다 원인이 하나다. **L1 경로와 실패 경로가 L2 경로의 상태를 모른다.**

`BestAttempt.HasCandidate`는 정확히 이 판단을 위해 정의됐으나 `src/` 어디에서도 참조되지 않는 죽은 코드로 남아 있었다.

## 결정

| 사안 | 결정 | 근거 |
|---|---|---|
| 재시도 예산을 L1/L2로 분리할까 | **하지 않는다.** 이름과 문서만 정정 | 구제가 도입되면 L1 실패의 피해가 "좋은 문서 상실"에서 "개선 기회 1회 상실"로 줄어든다. 예산을 나누면 SP당 AI 호출이 최악 3회에서 5회로 늘고 설정 스키마 하위 호환까지 설계해야 한다 |
| 구제 범위 | **세 경로 전부** — 생성 실패, L1 소진, L2 예외 | 셋 다 같은 결함이고, 가장 큰 손실인 생성 실패를 빼면 의미가 없다 |
| 구제 사실을 산출물에 적을까 | **적는다.** 배너에 한 줄 | 3차가 쿼터로 죽어 2차가 채택된 경우와, 3차까지 정상 수행했는데 2차가 최고였던 경우는 재실행 가치가 다르다. 읽는 사람이 그것을 구별할 수 있어야 한다 |
| L1 실패 회차의 프롬프트 | **L1 지시 + 누적 피드백 합성** | 두 종류의 결함이 동시에 살아 있으므로 둘 다 보내야 한다 |
| 로직 위치 | **전용 소유 클래스 신설** | 같은 규칙이 쌍둥이 루프에 흩어져 생긴 사고가 이번이 세 번째다. 구제 자리가 6곳으로 늘면 재발 확률도 그만큼 오른다 |

## 구성요소

```csharp
public enum RetryAbortReason
{
    GenerationFailed,  // AI 생성 호출이 예외를 던졌거나 빈 응답을 반환
    L1Exhausted,       // L1 기계 검증 재시도 소진
    ReviewFailed       // L2 리뷰 호출 실패
}

public sealed record RescueContext(RetryAbortReason Reason, int AbortedAttempt, int AdoptedAttempt);
public sealed record RescuedAttempt(string Markdown, ReviewResult Review, int AttemptNumber);

public static class RetryRescue
{
    /// 후보가 없으면 null을 돌려주고, 호출부는 현행 폴백으로 진행한다.
    /// reason이 null이면 정상 소진 — 구제 줄이 붙지 않는다.
    public static RescuedAttempt? TryRescue(
        BestAttempt best, int scoreThreshold, int abortedAttempt, RetryAbortReason? reason);
}
```

`VerificationBanner.QualityRejected`에 선택 인자 하나를 단다. 함께 움직여야 하는 값 셋을 레코드로 묶어 넘기므로 기존 호출부는 그대로 컴파일된다.

```csharp
public static string QualityRejected(ReviewResult review, int scoreThreshold, RescueContext? rescue = null)
```

책임은 기존 결을 따른다. `BestAttempt`는 순수한 보관자로 남고, 한국어 문구는 `VerificationBanner`가 계속 소유하며, `RetryRescue`는 구제 여부만 안다.

호출부는 여덟 자리 모두 같은 모양이 된다.

```csharp
var rescued = RetryRescue.TryRescue(bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.GenerationFailed);
if (rescued != null)
{
    finalReview = rescued.Review;
    verificationOutcome = VerificationOutcome.QualityRejected;
    specificationMarkdown = rescued.Markdown;   // 배너 포함
    break;
}
// 후보 없음 — 현행 폴백 유지
```

## 적용 자리

`reason`이 nullable이므로 이미 정상 동작 중인 L2 정상 소진 채택도 같은 관용구로 통일한다. 결함 수정이 아니라 구조 정리다 — 채택 관용구가 둘로 남으면 그것이 다음 드리프트의 씨앗이 된다.

| 순차 SP | 배치 계획 | 사유 | 후보 없을 때 폴백 |
|---|---|---|---|
| `:988` | `:1703` | `GenerationFailed` | `return null` — SP·잡 전체 실패 |
| `:1011` | `:1723` | `L1Exhausted` | `L1Exhausted` 배너 + 마지막 본문 |
| `:1127` | `:1795` | `ReviewFailed` | `ReviewNotRun` 배너 + 마지막 본문 |
| `:1112` | `:1780` | `null` (정상 소진) | `l2Result`로 폴백 |

**후보가 없을 때의 동작은 한 곳도 바뀌지 않는다.** 구제는 순수한 추가다.

생성 실패 두 자리만 `return`이 `break`로 바뀐다. 구제본이 본문·리뷰·상태 셋을 모두 채우므로 루프 뒤 확정 경로가 그대로 동작한다.

정상 소진 두 자리(`:1112`·`:1780`)는 후보가 없어도 `l2Result`로 `QualityRejected` 배너를 붙여 확정한다. 폴백이 "아무것도 안 함"이 아니라 "현행 그대로"라는 뜻이다.

### 콘솔 알림

각 자리는 기존 실패 알림을 그대로 유지하고, 구제가 일어났을 때 채택 알림을 **추가로** 낸다. 문구는 정상 소진 자리가 이미 쓰는 형태를 따른다.

```
가장 높은 점수를 받은 2차 시도(88/100)를 채택합니다.
```

`RescuedAttempt.AttemptNumber`가 이 문구를 위해 존재한다. 이 메시지를 빠뜨리면 로그만으로는 어느 시도가 채택됐는지 알 수 없게 된다 — 2026-08-04·08-05 두 차례의 실증이 모두 이 한 줄에 의존했다.

## 배너

구제 시 첫 불릿으로 한 줄이 들어간다. 뒤따르는 점수표가 무엇을 서술하는지 먼저 밝히기 위해서다.

```
> **[품질 불합격] 예외 기준 미달 (최종 신뢰도 점수: 88/100)**
> - **채택 경위**: 3차 시도가 AI 생성 호출 실패로 중단되어, 검증을 마친 2차 시도를 채택했습니다.
> - **평가 점수**: 정합성 9/10, CRUD 10/10, ...
```

사유별로 뒷부분만 달라진다 — `L1 기계 검증 실패로 중단되어` / `L2 리뷰 호출 실패로 중단되어`.

"다시 돌리면 나아진다" 같은 조언은 넣지 않는다. 사실만 적고 판단은 읽는 사람에게 맡긴다.

## 피드백 합성

`:1003`·`:1716`의 덮어쓰기를 합성으로 바꾼다. 소유자는 이미 누적 블록 형식을 갖고 있는 `CriticFeedbackLog`다 — 두 프롬프트 형태를 나란히 두어야 서로 어긋나지 않는다.

```csharp
public static string ComposeAfterL1Failure(string? l1Fix, IReadOnlyList<string> history)
```

```
[L1 기계 검증 오류 — 이번 회차에 반드시 해소]
표 내부에 허용되지 않는 축약어가 감지되었습니다. ...

[L2 AI 리뷰 누적 피드백 (최근 2개 라운드)]
### [시도 1 피드백] ...

※ 지시사항: 위 형식 오류를 먼저 해소하고, 누적 피드백에서 이미 반영한 내용 교정의
서술 수준을 낮추지 마십시오. 원본 DDL을 절대적 기준으로 삼으십시오.
```

`history`가 비어 있으면 `l1Fix`를 그대로 돌려준다. 아직 L2 라운드가 없던 상태 — 가장 흔한 경우 — 의 프롬프트는 오늘과 한 글자도 다르지 않다.

## 예산 문서 정정

코드는 그대로 두고 세 곳에 같은 사실을 적는다. `appsettings.json`의 `MaxL2Attempts` 주석, `VerificationPipelineOrchestrator.cs:74`의 `_maxAttempts = 1 + _maxL2Attempts` 산식 옆, `AGENTS.md`.

적을 사실: 이 설정은 이름과 달리 L2 전용이 아니라 **L1 실패와 공유하는 총 시도 예산**이다. L1에서 소진되면 채점된 후보 수가 설정값보다 적어질 수 있다. 그 경우에도 최고점 후보는 `RetryRescue`가 구제한다.

## 경계 조건

| 상황 | 동작 |
|---|---|
| 구제본의 결함 여부 | 반드시 `HasDefects=true`. 결함이 없었다면 그 자리에서 루프를 빠져나갔으므로 `QualityRejected`는 항상 정확하다 |
| 점수 노출 | 상태가 `QualityRejected`라 `VerificationDocumentFormatter`의 `showScores`가 켜진다. 채택된 문서 자신의 점수이므로 정확하다 |
| 캐시 | 배치 모드(`:1165`)와 TUI 승인(`:1194`) 모두 `Passed`일 때만 갱신한다. 구제본은 캐시되지 않는다 — 의도대로다 |
| 사용자 취소 | `OperationCanceledException`은 잡지 않아 그대로 전파된다. 구제하지 않는다. 기존 정책 유지 |
| 1차 시도 생성 실패 | 후보 없음 → 현행대로 전체 실패. 변화 없음 |
| L3 인간 승인 루프 (`:1182`, `:1830`) | 범위 밖. 재시도 루프가 아니며 그곳의 `null` 반환은 사용자가 명시적으로 취소한 것이다 |
| 하이브리드 경로 (`ActorEffort: dynamic`, `:365`·`:428`·`:592`) | 범위 밖. `bestAttempt`가 스코프에 없고, `:466-493`이 별도의 후보 선택 로직을 이미 갖고 있다 |

## 테스트 전략

이번 변경은 전부 결정적이라 단위 테스트로 못박힌다. 실증 재실행이 필요 없다 — 오히려 생성 실패는 실제로 재현하기 어려워 NSubstitute 하네스가 유일한 검증 수단이다.

| 대상 | 항목 |
|---|---|
| `RetryRescueTests` (신규) | 후보 없음 → `null` / 후보 있음 → 배너 포함 본문 / `reason=null` → 구제 줄 없음 / 사유 3종 문구 |
| `VerificationBannerTests` | `RescueContext`가 있을 때 첫 불릿이 채택 경위인지, 없을 때 기존 출력과 동일한지 |
| `CriticFeedbackLogTests` | `history`가 비면 `l1Fix`만 반환, 있으면 L1 우선 + 누적 포함 |
| `VerificationPipelineOrchestratorTests` | 2차 채점 성공 + 3차 **생성 예외** → `null`이 아니라 2차 문서 반환 ← **핵심 회귀 테스트** |
| 〃 | 2차 채점 성공 + 3차 L1 소진 → 2차 채택 |
| 〃 | 2차 채점 성공 + 3차 리뷰 예외 → 2차 채택 |
| 〃 | 1차 생성 예외(후보 없음) → 현행대로 `null` |
| 〃 | 구제 시 `NotifyError`에 채택된 시도 번호와 점수가 실리는지 |
| 〃 | 배치 루프 생성 실패 구제 1건 |
| 기존 616건 | 정상 소진 경로가 그대로 통과해야 한다. 통일 리팩터링의 안전망이다 |

## 범위 밖

- **재시도 예산 분리** — 이름·문서 정정으로 갈음한다
- **시도 간 진동 억제** — Actor가 매번 백지에서 재작성해 점수가 20점 이상 출렁이는 문제. `IAiService` 인터페이스와 프롬프트를 함께 바꿔야 하는 별건이다
- **합격 기준 정책** — 다섯 항목 전부가 기준을 넘어야 하는 현행 게이트를 유지한다
- **하이브리드 경로와 L3 승인 루프** — 위 경계 조건 표 참조
