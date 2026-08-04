# 재시도 루프 수렴 설계 — 최고점 채택, 피드백 누적, Mermaid `@` 처리

- 작성일: 2026-08-04
- 상태: 설계 승인됨 (구현 계획 수립 전)

## 배경

`dbo.UP_Util_PG_Client_CMRate_Ins`를 재분석한 결과 종합 신뢰도가 이전 실행 88점에서 78점으로 떨어졌다. 로그(`output/logs/reset-20260804.log`)에서 시도별 Critic 점수를 추출한 결과, 품질이 떨어진 것이 아니라 **파이프라인이 가장 좋은 산출물을 버리고 있었다.**

| 시도 | 정합성 | CRUD | 인터페이스 | 예외 | 가독성 | 종합 | 응답 길이 | 결과 |
|---|---|---|---|---|---|---|---|---|
| 1 | 8 | 8 | 7 | 7 | 5 | 70 | 19,990자 | 재시도 |
| 2 | **10** | 9 | **9** | 7 | **10** | **90** | 18,488자 | 재시도 |
| 3 | 8 | 9 | 6 | 9 | 7 | 78 | 23,085자 | **채택** |

기준 점수는 8이고 게이트(`VerificationPipelineOrchestrator.cs:1064-1071`)는 다섯 항목 중 **하나라도** 미만이면 불합격이다. 시도 2는 예외 7점 하나만 미달했고 나머지는 정합성 10 / 인터페이스 9 / 가독성 10이었다. 재시도가 돌았고, 시도 3은 예외를 7→9로 고치는 대신 정합성 10→8, 인터페이스 9→6, 가독성 10→7로 무너졌다. 그리고 그 78점짜리가 최종 산출물이 됐다.

이전 실행의 88점과 이번 78점의 차이도 품질 변화가 아니라 **"어느 시도가 마지막이었나"의 추첨 결과**다.

### 원인 1 — 마지막 시도를 채택한다 (최고점을 버림)

`VerificationPipelineOrchestrator.cs:1097-1105`가 재시도 소진 시 점수 비교 없이 마지막 시도를 확정한다.

```csharp
_userInteraction.NotifyError($"{selectedOption} - [[L2 AI 리뷰]] 최종 보완 실패. 마지막 리뷰 반영 버전을 사용합니다.");
finalReview = l2Result;                                   // 마지막 것
specificationMarkdown = VerificationBanner.QualityRejected(...) + specificationMarkdown;
```

`specificationMarkdown`은 단일 가변 변수로 매 시도 덮어써진다(`954`, `992`행). 시도별 보관이 없다.

같은 파일 `466-493`행에는 이미 최고점 선택 로직이 있다 — 하이브리드 3후보 경로는 `NormalizedScore`가 가장 높은 후보를 고른다. 순차 재시도 루프에만 그 개념이 빠져 있다.

동일 구조의 결함이 배치 계획 루프(`1636`행~, 소진 처리는 `1749`행)에도 있다.

`778`행에도 같은 문구("마지막 리뷰 반영 버전을 사용합니다")가 있으나 그곳은 하이브리드 경로(`ActorEffort: dynamic`)의 **알림 문구일 뿐 선택 지점이 아니다.** 그 경로는 이미 `466-493`행에서 최고 후보를 고르고 Consolidator가 단일 결과를 만들므로 "마지막"이라 할 후보가 없다. 이번 범위 밖이다.

### 원인 2 — 피드백이 매 라운드 초기화되어 골포스트가 움직인다

`VerificationPipelineOrchestrator.cs:1087` (배치 계획 루프는 `1739`행)

```csharp
feedbackHistory.Clear(); // [컨텍스트 윈도우 오염 방지] 이전 실패 기록을 모두 지우고 최신 피드백만 주입
```

변수명은 `feedbackHistory`, 로그 라벨은 "Stateful Checklist"인데 실제로는 상태가 없다. 주입 문구도 "이전에 생성된 실패한 응답의 잔재에 영향을 받지 말고"라고 지시한다.

결정적으로 `GenerateSpecificationAsync(spDef, userInstructions, feedbackLog, effort, ct)`는 **이전 명세서를 받지 않는다**(`IAiService.cs:12`). 매 시도 DDL과 최신 피드백만으로 백지에서 다시 쓴다. 이것이 진동의 구조적 원인이다.

Critic 지적 주제도 라운드마다 갈아탄다.

- 시도 2 → `@@TRANCOUNT` / `SAVE TRANSACTION` / NOLOCK 격리 수준
- 시도 3 → Rowset 반환 표기, Null 표기 형식, Mermaid, 자체조인 오기, TOCTOU

Actor는 새 지적만 보고 고치느라 이미 만점이던 항목을 잃는다. 시도 3은 앞선 시도에서 이미 정리됐던 조인 서술을 '자체조인'으로 되돌리기까지 했다.

### 원인 3 — `@@ERROR`가 "at at ERROR"로 깨졌다

시도 3의 Mermaid 노드 10곳이 전부 `"오류 발생 여부 확인 at at ERROR"`가 됐고 Critic이 명시적으로 지적해 가독성 점수를 깎았다.

정화기는 범인이 아니다. `CleanseMermaidCode`는 큰따옴표 안을 건드리지 않는다(`MechanicalValidator.cs:480-483`). 모델이 직접 쓴 문자열이며, 원인은 프롬프트 규칙(`AiService.cs:308`)이 금지를 앞세운 형태라는 데 있다.

```
Do not include variables prefixed with '@' inside the node text
(except system variables like '@@ERROR', which must be wrapped in double quotes).
```

mermaid-cli 11.16.0으로 실측한 결과 규칙의 *의도*는 옳지만 *범위*가 과하다.

| 라벨 형태 | 결과 |
|---|---|
| `{"@@ERROR <> 0 확인"}` (따옴표 있음) | 정상 렌더링, exit 0, SVG 생성 |
| `{@@ERROR <> 0 확인}` (따옴표 없음) | 파스 에러, exit 1 (`got 'LINK_ID'`) |

즉 따옴표만 있으면 `@`는 아무 문제가 없다. 그런데 `CleanseMermaidCode`의 자동 따옴표 트리거 목록(`MechanicalValidator.cs:451-457`)에 `@`가 없어, 모델이 따옴표를 빠뜨리면 정화기가 구제하지 못하고 파스 에러로 직행한다.

## 목표와 범위

### 목표

재시도 루프가 생성한 결과물 중 **가장 좋은 것**을 채택하고, 라운드 간 지적사항이 유실되지 않게 하며, Mermaid `@` 표기가 깨지지 않게 한다.

### 결정 사항

| 항목 | 결정 | 근거 |
|---|---|---|
| 채택 정책 | 재시도 소진 시 최고 `NormalizedScore` 시도를 채택 | 이번 사례 78→90 회복. `466-493`행의 기존 패턴을 따르므로 회귀 위험이 낮다 |
| 동점 처리 | 먼저 나온 시도를 유지(엄격 부등호로 갱신) | 나중 시도가 더 낫다는 근거가 없고, 실제로 후속 시도가 다른 축을 망가뜨렸다 |
| 조기 종료 | **채택하지 않음** | 합격 기준(다섯 항목 전부 통과)을 바꾸는 정책 결정이라 별건으로 분리 |
| 최고점 기반 재작성 | **채택하지 않음** | `IAiService` 시그니처 확장과 프롬프트 재설계가 필요하고 효과는 실행으로만 확인된다. 이번 범위 밖 |
| 피드백 유지 | 최근 3개 누적 + 항목별 점수 동봉 | 기본 설정(`MaxL2Attempts=2`)에서는 최대 2개라 부담이 없고, `unlimited` 모드의 무한 증가를 막는다 |
| Mermaid | 프롬프트 문구 교체 + 정화기 자동 따옴표에 `@` 추가 | 유인을 없애고, 모델이 따옴표를 빠뜨려도 기계적으로 복구 |
| "at at ERROR" 역변환 | **하지 않음** | 모델이 즉흥적으로 만든 표현을 추측해 복원하는 것이라 취약하다 |

## 설계

### A. 채택 정책 — 마지막에서 최고점으로

재시도 루프(`790`행~) 진입 전에 최고 기록을 둔다.

```
bestMarkdown : string?
bestReview   : ReviewResult?
bestAttemptNumber : int
```

각 시도에서 **L1을 통과하고 리뷰가 성공한 경우에만** 후보로 취급하고, `NormalizedScore`가 기존 최고를 **넘을 때만** 갱신한다(엄격 부등호 → 동점은 먼저 나온 시도 유지).

보관 대상은 **L1 정화가 끝난 뒤의** `specificationMarkdown`(`992`행 이후 값)이다. 정화 전 원본을 보관하면 채택 시 L1을 다시 돌려야 한다.

재시도 소진 경로(`1097-1105`행)만 바뀐다.

```csharp
finalReview = bestReview;
specificationMarkdown = VerificationBanner.QualityRejected(bestReview, _criticScoreThreshold) + bestMarkdown;
```

검증 통과 경로(`1121`행)는 그대로 둔다 — 통과했으면 그것이 최선이다.

사용자 안내 문구도 사실에 맞춘다.

```
(전) 최종 보완 실패. 마지막 리뷰 반영 버전을 사용합니다.
(후) 최종 보완 실패. 가장 높은 점수를 받은 2차 시도(90/100)를 채택합니다.
```

배치 계획 루프(`1636`행~)에도 동일하게 적용한다. 한쪽만 고치면 같은 증상이 그쪽에 남는다.

### B. 피드백 — 초기화에서 누적으로

`feedbackHistory.Clear()`(`1087`행, 배치 계획 루프는 `1739`행)를 제거하고 **가장 최근 3개 라운드의 피드백**을 유지한다(4개째가 들어오면 가장 오래된 항목을 제거). 각 항목에 점수 줄을 동봉한다.

```
### [시도 2 피드백]
- 이 시도의 점수: 정합성 10, CRUD 9, 인터페이스 9, 예외 7, 가독성 10 (기준 8)
- 지적사항: (Critic 피드백 원문)
```

현재 Actor는 **어느 항목이 미달인지 모른다.** 산문 피드백만 받는다. 점수를 실으면 "예외만 부족하다"가 명시되므로 멀쩡한 항목을 갈아엎을 이유가 줄어든다. 주입 지시 문구에 "이미 기준을 통과한 항목의 서술 수준을 낮추지 마십시오"를 추가한다.

**한계.** Actor는 여전히 백지에서 다시 쓰므로(최고점 기반 재작성을 범위에서 뺐다), 이 조치가 보장하는 것은 "이전에 지적당한 오류의 재발 방지"이지 "이미 잘 쓴 문장의 보존"이 아니다. 진동을 줄이되 없애지는 못한다.

### C. Mermaid — 프롬프트와 정화기

**C-1. 생성 프롬프트** (`AiService.cs:308`)

```
Node labels containing '@' (e.g. '@@ERROR', '@po_intRetVal') MUST be wrapped in
double quotes. Write the identifier exactly as it appears in the source — never
paraphrase or spell out '@' (writing 'at ERROR' for '@@ERROR' is a defect).
```

**C-2. Critic 프롬프트** (`AiService.cs:1587`) — 같은 기준으로 맞춘다. 한쪽만 바꾸면 Critic이 올바른 표기를 감점할 수 있다.

```
Node labels containing '@' must be wrapped in double quotes, with the identifier
written exactly as in the source. Flag any paraphrased or spelled-out '@'.
```

**C-3. 정화기** (`MechanicalValidator.cs:451-457`) — 자동 따옴표 트리거 목록에 `@`를 추가한다.

```csharp
trimmedLabel.Contains("/") || trimmedLabel.Contains("\\") ||
trimmedLabel.Contains("@")
```

순수 이득이다. 따옴표를 씌우는 것은 언제나 유효하고, 이미 따옴표가 있으면 `459`행 가드가 재감싸기를 막는다. 지금은 따옴표가 빠지면 파스 에러로 직행하는데 그것이 유효한 다이어그램으로 바뀐다.

## 경계 조건

| 상황 | 동작 |
|---|---|
| 모든 시도의 리뷰 실패 | 후보 없음 → 현행대로 마지막 결과 + `ReviewNotRun` 배너 |
| 최고점이 마지막 시도 | 동작 변화 없음 |
| 사용자 취소 | 기존 정책 유지 — `OperationCanceledException`을 감싸지 않고 전파, 보관 중인 best는 버림 |
| L1 재시도 소진(`1009-1011`행) | 범위 밖. 그 경로는 Critic 리뷰를 받지 못해 점수가 없으므로 최고점 개념이 성립하지 않는다 |
| 하이브리드 경로(`ActorEffort: dynamic`) | 범위 밖. 재시도 루프가 아니며 `466-493`행이 이미 최고 후보를 고른다 |

## 테스트 전략

**오케스트레이터** (기존 NSubstitute 하네스 `VerificationPipelineOrchestratorTests` 사용)

- 이번 사고 재현: 3시도 × 70/90/78 → 90점 문서가 채택되는지. 회귀 방지의 핵심이다
- 동점(90/90) → 먼저 나온 시도가 남는지
- 전 시도 리뷰 실패 → 현행 경로 유지
- 배치 계획 루프에도 동일 시나리오 1건

**피드백 누적**

- `GenerateSpecificationAsync` 인자를 캡처해 3번째 시도의 `feedbackLog`에 1·2차 지적이 모두 들어 있는지
- 점수 줄이 동봉되는지
- `unlimited` 모드 4번째 시도에서 가장 오래된 항목이 빠지는지(상한 3)

**Mermaid**

- 정화기: `@` 포함 무따옴표 라벨이 따옴표로 감싸지는지 / 이미 따옴표면 그대로인지 (`PostProcessMarkdown` 단위 테스트, 기존 `MechanicalValidatorTests:274` 패턴)
- 프롬프트 문구: 규칙 문자열에 역설명 금지 조항이 남아 있는지만 회귀 가드로 확인

## 검증의 한계

A와 C-3은 결정적이므로 단위 테스트로 못박힌다. B와 C-1·C-2의 품질 효과는 확률적이라 단위 테스트로 증명할 수 없다. 구현 후 `dbo.UP_Util_PG_Client_CMRate_Ins`를 다시 돌려 로그에서 시도별 점수 추이를 비교하는 것이 유일한 실증 수단이다.

mermaid-cli는 운영 경로(`ValidationSettings:UseMermaidCli`)에서만 호출되고 테스트에서는 호출하지 않는다. 위 렌더링 실측은 설계 근거로만 사용하고 테스트에 넣지 않는다.

## 범위 밖

- **합격 기준 정책**: 다섯 항목 전부가 기준을 넘어야 하는 현행 게이트를 유지한다. 종합 점수 게이트 병행이나 항목별 기준 완화는 품질 기준에 대한 결정이므로 별건으로 다룬다
- **최고점 기반 재작성**: `IAiService`에 이전 명세서를 전달해 백지 재작성 대신 수정하게 하는 방안. 진동을 근본적으로 막지만 인터페이스와 프롬프트를 함께 바꿔야 한다
- **조기 종료**: 종합 점수가 충분히 높으면 재시도를 중단하는 방안. 합격 기준 변경을 수반한다
- **L1 소진 경로의 최선본 선택**: 리뷰 점수가 없어 순위를 매길 수 없다
- **codex-cli의 전역 `AGENTS.md` 주입, codex/agy의 출력 절단 감지**: 현재 각 CLI가 제공하는 수단으로는 해결할 수 없어 미해결로 남긴다
