# 검증 표기 정리 설계

- 작성일: 2026-08-03
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 선행 작업: `2026-08-03-verification-honesty-followups` (병합 완료, `a61e85e`), 지시서 번들 표기(`626aa85`)

## 배경

직전 두 사이클에서 검증 파이프라인의 네 종료 상태를 값으로 모델링하고, 명세서·통합 계획서·단일 SP 계획서·지시서 번들이 그 상태를 정직하게 보고하도록 정리했다.

최종 리뷰가 남긴 후속 과제 8건 중, **정답이 이미 정해진 정리 작업 5건**이 이번 사이클의 범위다. 나머지 3건(L2 리뷰 재시도 인프라, 통합 루프의 점수 임계값 강제, 인터페이스 점수 필드)은 새 정책 결정을 필요로 하므로 별도 사이클로 남긴다.

이번 5건에는 공통점이 있다. **모두 "코드가 사실이 아닌 것을 말하거나, 말해야 할 것을 말하지 않는" 문제이며, 올바른 형태가 이미 같은 저장소 안에 존재한다.**

## 대상 결함

직전 사이클 최종 리뷰의 번호를 함께 적는다.

| # | (이전) | 결함 | 근거 |
|---|---|---|---|
| A | M4 | `catch { }`가 `OperationCanceledException`을 삼켜 사용자의 취소가 무시된다 | `VerificationPipelineOrchestrator.cs:642`, `:1447`, `:1803` |
| B | M1' | 점수 줄의 설명 주석 하나가 거짓이다 | `VerificationDocumentFormatter.cs:26` |
| C | M1'' | 소스 주석이 교정된 설계 문서와 모순된다 | `VerificationDocumentFormatter.cs:30` |
| D | — | `StatusLabel` switch가 두 곳에 중복되어 있다 | `ConsoleUserInteraction.cs:125-131` |
| E | M5 | 정산 정책 문서에 검증 표기가 없다 | `Program.cs:556-558`, `:1385-1387` |

### A — 취소가 삼켜진다

세 지점 모두 `cancellationToken`을 받는 AI 호출을 감싸고 있고, 예외를 통째로 버린다.

```csharp
catch { }
```

구체적 증상은 `:1803`에서 가장 뚜렷하다. 통합 계획서 L3 승인 화면에서 피드백을 넣어 재생성하는 도중 Ctrl-C를 누르면, `OperationCanceledException`이 삼켜지고 `rePlan`이 수정 전 값을 유지한 채 `:1810`에 도달해 승인 화면으로 되돌아간다. **사용자의 취소가 조용히 무시되고 같은 질문을 다시 받는다.**

올바른 형태는 같은 파일 `:671`, `:722`에 이미 있다.

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Warning(ex, "...");
}
```

**상위 처리는 이미 존재한다.** `Program.cs:968`(명세서 파이프라인)과 `:1262`(통합 파이프라인)를 포함해 다섯 곳이 `catch (OperationCanceledException)`으로 메시지를 찍고 메뉴로 돌아간다. 취소를 전파해도 새 배관이 필요 없다.

**정정**: 직전 사이클 리뷰는 이 결함을 4곳으로 기록했으나, 네 번째인 `Program.cs:1683`은 `File.Delete`를 감싼다. 동기 호출이라 `OperationCanceledException`을 던지지 않으므로 실제 대상이 아니다. 최선노력 IO 정리로서 그대로 둔다.

### B, C — 라벨이 프롬프트에서 드리프트했다

`VerificationDocumentFormatter`는 YAML 점수 줄에 설명 주석을 붙인다.

```
가독성 점수: 9/10 # 코드 가독성 및 표준 준수
```

명세서 Critic의 다섯 번째 평가 기준은 `AiService.cs:1585-1589`에서 다음을 채점한다.

> 5. Diagram Syntax and Readability (ScoreReadability):
>    - Ensure the Mermaid flowchart TD diagram has no syntax errors.
>    - Node text labels must be wrapped in double quotes. …

코드 가독성도 표준 준수도 채점하지 않는다. **이 주석은 거짓이다.**

`:30`의 소스 주석은 이 오해를 코드에 고정해 두고 있다.

```csharp
// 쓰지만 평가 대상이 다르다 - 특히 가독성은 다이어그램 문법을 본다.
```

"특히 가독성은"이라는 표현은 명세서 Critic의 가독성은 다이어그램 문법이 *아니라*는 함의를 담는데, 위에서 본 대로 사실이 아니다.

#### 이 결함이 발생한 구조

주석은 Critic 프롬프트의 내용을 사람이 옮겨 적은 것이고, **둘의 연결을 강제하는 장치가 없다.** 프롬프트가 바뀌어도 주석은 따라가지 않는다. 실제로 드리프트했고, 리뷰어가 프롬프트를 직접 읽기 전까지 아무도 몰랐다.

Critic은 셋이다 — 프로시저 명세서(`AiService.cs:1573-1588`), UDF 명세서(`:1656-1660`), 통합 계획서(`:1998-2019`). 그런데 `FormatSpecification`은 프로시저와 UDF 명세서를 모두 처리하면서 둘을 구분하지 못한다. **어떤 라벨 집합을 쓰더라도 UDF 명세서에는 부정확하다.**

또한 프로시저 명세서 Critic과 통합 계획서 Critic의 **표제 기준명 5개는 문자 그대로 동일**하고 차이는 하위 불릿에만 있다. 두 라벨 테이블을 나눈 근거 자체가 성립하지 않는다.

### D — `StatusLabel` 중복

지시서 번들 작업(`626aa85`)에서 `VerificationDocumentFormatter.StatusLabel`을 공개해 번들과 문서 헤더가 같은 상태를 다르게 표기할 여지를 없앴다. 그러나 `ConsoleUserInteraction.cs:125-131`에 같은 switch가 남아 있다.

```csharp
var statusLabel = outcome switch
{
    VerificationOutcome.L1Exhausted => "L1 미통과",
    VerificationOutcome.QualityRejected => "품질 미달",
    VerificationOutcome.ReviewNotRun => "리뷰 미수행",
    _ => "알 수 없음"
};
```

`VerificationOutcome`에 상태가 추가되면 한 곳이 빠뜨릴 수 있고, 그러면 승인 화면만 다른 말을 한다.

### E — 정산 정책 문서에 표기가 없다

`SettlementPolicyService.cs:104`가 AI 결과를 받아 그대로 반환하고, `Program.cs:558`과 `:1387`이 직접 조립한 메타 헤더를 앞에 붙여 파일로 쓴다. **L1도 L2도 없다.** 단일 SP 계획서와 같은 범주이면서 표기만 없다.

`BatchMigrationPlan.md`와 달리 자동 코딩 에이전트에 전달되지는 않으므로 위험도는 낮지만, 검증 파이프라인을 거치지 않은 AI 생성 문서가 그 사실을 밝히지 않는다는 점은 동일하다.

## 설계

### 진입점을 문서 종류가 아니라 보장 수준으로 나눈다

라벨을 없애면 `FormatSpecification`과 `FormatConsolidatedPlan`의 **유일한 차이가 사라져** 두 메서드가 완전히 동일해진다. 동작이 같은 두 이름을 남겨두면 뒤에 오는 사람이 차이가 있다고 믿게 되므로 합친다.

```csharp
public static class VerificationDocumentFormatter
{
    /// 검증 파이프라인을 통과한 문서 — 명세서와 통합 계획서
    public static string FormatVerifiedDocument(
        string body, ReviewResult? review, VerificationOutcome outcome,
        string provider, string modelName, string? effort, DateTime timestamp);

    /// 파이프라인에 진입한 적 없는 문서 — 단일 SP 계획서와 정산 정책 문서
    public static string FormatUnverifiedDocument(
        string body, VerificationOutcome? sourceOutcome,
        string provider, string modelName, string? effort, DateTime timestamp);

    public static string StatusLabel(VerificationOutcome outcome);
}
```

`ScoreLabels` 레코드와 `SpecificationLabels`·`PlanLabels` 테이블, 그리고 `:30`의 모순 주석이 함께 삭제된다.

새 축은 **무엇이 보장되는가**다. 이것이 실제 축이다 — `Rulebook.md`와 단일 SP 계획서는 종류가 전혀 다르지만 보장 수준이 같고, 명세서와 통합 계획서는 종류가 다르지만 같은 파이프라인을 통과했다.

`sourceOutcome`이 nullable인 이유: 정산 정책 문서에는 인용할 근거가 없다. `SettlementPolicyService`는 SP 정의와 프로파일링 데이터에서 직접 생성하며 명세서를 거치지 않는다. `null`이면 `근거 명세서 검증 상태` 줄을 내지 않는다.

#### 문구를 문서 종류에 중립적으로 바꾼다

현재 `FormatUnverifiedPlan`은 "이 **계획서**는 검증 파이프라인을 거치지 않았습니다"라고 쓴다. 같은 메서드가 정산 정책 문서도 처리하게 되므로 "이 **문서**는"으로 바꾼다. YAML 주석도 `# 이 계획서는 L1/L2 검증을 거치지 않음` → `# 이 문서는 L1/L2 검증을 거치지 않음`으로 함께 바꾼다.

이는 기존 테스트 `FormatUnverifiedPlan_StatesThatTheDocumentItselfWasNeverVerified`의 단언 한 줄을 바꾼다 — 아래 테스트 절에 명시한다. 의도된 변경이며, 조용히 흘려보내지 않는다.

### 점수 줄에서 설명 주석을 없앤다

```csharp
var scoreLines = showScores
    ? $@"
종합 신뢰도: {review!.NormalizedScore}
정합성 점수: {review.ScoreAccuracy}/10
CRUD 점수: {review.ScoreCrud}/10
인터페이스 점수: {review.ScoreInterface}/10
가독성 점수: {review.ScoreReadability}/10
예외처리 점수: {review.ScoreException}/10"
    : string.Empty;
```

필드명이 이미 의미를 전달하므로 주석은 장식이다. 세 Critic 중 어느 것에도 정확할 수 없는 문구를 유지하느니 없앤다. **거짓이 될 문구가 존재하지 않으면 드리프트할 수 없다.**

`검증 상태: {label} # 검증 파이프라인 종료 상태`의 주석은 **남긴다.** 이것은 Critic 프롬프트를 복제한 것이 아니라 필드 자체의 설명이므로 드리프트할 대상이 없다.

`> **AI 최종 신뢰도**: 80/100점 (정합성: 10, CRUD: 9, …)` 블록쿼트도 그대로 둔다. 기준 설명이 아니라 점수 요약이다.

### 취소 전파

세 지점을 같은 파일이 이미 쓰는 형태로 맞춘다.

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Warning(ex, "<지점별 메시지>");
}
```

| 지점 | 감싸는 대상 | 로그 메시지 |
|---|---|---|
| `:642` | 합성본 자가 수정 생성 | 합성본 자가 수정 실패 (이전 버전 유지) |
| `:1447` | 명세서 L3 피드백 재생성 | 명세서 L3 피드백 반영 재생성 실패 |
| `:1803` | 통합 계획서 L1 재보완 | 통합 계획서 L1 재보완 실패 (직전 버전 유지) |

취소가 아닌 예외에 대한 기존 동작 — 이전 값을 유지하고 계속 진행 — 은 그대로다. 바뀌는 것은 취소가 더 이상 삼켜지지 않는다는 점뿐이다.

**동작 변화를 명시한다.** 현재는 Ctrl-C 이후에도 작업이 계속되어 승인 화면까지 간다. 수정 후에는 즉시 상위 핸들러로 올라가 메뉴로 돌아간다. 이것이 의도된 동작이다.

### `StatusLabel` 단일화

`ConsoleUserInteraction.cs:125-131`의 switch를 `VerificationDocumentFormatter.StatusLabel(outcome)` 호출로 대체한다. 기존 switch는 `Passed`를 다루지 않지만(`if (!isVerified)` 안에 있어 도달하지 않는다) `StatusLabel`은 다루므로 무해하다.

### 정산 정책 문서

```csharp
await File.WriteAllTextAsync(
    rulebookPath,
    VerificationDocumentFormatter.FormatUnverifiedDocument(
        rulebook, null, provider, modelName, actorEffort, DateTime.Now));
```

두 블록의 `effortSuffix`와 `metadataHeader` 지역 변수가 사라진다. 출력은 다음과 같다.

```
---
검증 상태: 검증 없음 # 이 문서는 L1/L2 검증을 거치지 않음
---

> [!NOTE]
> **문서 작성일시**: 2026-08-03 14:22:01
> **분석 AI 정보**: anthropic (claude-opus-5, Effort: high)
> **검증 상태**: 이 문서는 검증 파이프라인을 거치지 않았습니다. 내용을 직접 검토하십시오.
```

단일 SP 계획서는 `sourceOutcome`이 있으므로 여기에 `근거 명세서 검증 상태: 통과` 줄과, 메타 블록의 "근거 명세서(Spec.md)는 '통과' 상태입니다" 문장이 더 붙는다.

### 호출부

| 위치 | 현재 | 변경 후 |
|---|---|---|
| `DependencyAnalysisOrchestrator.cs:462` | `FormatSpecification` | `FormatVerifiedDocument` |
| `Program.cs:1650` | `FormatSpecification` | `FormatVerifiedDocument` |
| `Program.cs:730` | `FormatConsolidatedPlan` | `FormatVerifiedDocument` |
| `Program.cs:1186` | `FormatConsolidatedPlan` | `FormatVerifiedDocument` |
| `Program.cs:1667` | `FormatUnverifiedPlan` | `FormatUnverifiedDocument` (인수 불변) |
| `Program.cs:558` | 직접 조립 | `FormatUnverifiedDocument(…, null, …)` |
| `Program.cs:1387` | 직접 조립 | `FormatUnverifiedDocument(…, null, …)` |

`VerificationPipelineOrchestrator.cs:1586`의 주석이 `FormatConsolidatedPlan`을 언급하므로 함께 갱신한다.

## 테스트 전략

### 폐기

두 테스트는 **두 라벨 테이블이 서로 다름을 단언하는 것이 존재 이유**이므로, 라벨과 함께 폐기한다.

- `VerificationDocumentFormatterTests.cs:107` `FormatConsolidatedPlan_UsesPlanSpecificScoreDescriptions`
- `VerificationDocumentFormatterTests.cs:176` `FormatSpecification_KeepsSpecificationScoreDescriptions`

테스트 삭제는 커버리지가 조용히 사라지는 경로이므로 대체 테스트로 결함 유형이 구조적으로 불가능해졌음을 고정한다.

```csharp
[Fact]
public void FormatVerifiedDocument_EmitsScoreLinesWithoutDescriptiveComments()
{
    // 점수 줄의 설명 주석은 Critic 프롬프트를 복제한 것이었고, 연결을 강제하는
    // 장치가 없어 드리프트했다 - 가독성 설명은 실제로 거짓이 되어 있었다.
    // 주석 자체를 없앴으므로 거짓이 될 문구가 존재하지 않는다.
    Assert.DoesNotContain("가독성 점수: 9/10 #", result);
    // 필드 자체를 설명하는 이 주석은 남는다 - 프롬프트에서 복제한 것이 아니다.
    Assert.Contains("검증 상태: 통과 # 검증 파이프라인 종료 상태", result);
}
```

### 유지

기존 5개(`Format_WithReview_…`, `Format_Passed_…`, `Format_ReviewNotRun_…`, `Format_L1Exhausted_…`, `Format_QualityRejected_…`)는 `Assert.Contains("종합 신뢰도: 80", …)` 형태로 주석을 단언하지 않으므로 **호출 이름만 바꾸고 단언은 그대로 둔다.** 이들은 지난 사이클 개명의 회귀 방어선이며 이번에도 같은 역할을 한다.

`FormatUnverifiedPlan_*` 2개는 `FormatUnverifiedDocument_*`로 개명한다. 단언은 **한 줄만** 바뀐다 — `FormatUnverifiedPlan_StatesThatTheDocumentItselfWasNeverVerified`의

```csharp
Assert.Contains("이 계획서는 검증 파이프라인을 거치지 않았습니다", result);
```

가 `"이 문서는 검증 파이프라인을 거치지 않았습니다"`로 바뀐다. 같은 메서드가 정산 정책 문서도 처리하게 되어 문구를 중립화했기 때문이다. 나머지 단언은 그대로 둔다.

`SpecHeaderReaderTests.cs:129` `Read_NormalizesRealArtifactScoreLines`는 주석이 붙은 줄을 파싱한다. 산출물이 더는 그런 줄을 만들지 않아도 **주석 제거 로직은 그대로 필요하다** — 디스크의 기존 문서, 손으로 편집한 헤더, 그리고 여전히 주석이 붙는 `검증 상태` 줄이 있다. 테스트는 유지하되 주석 문구를 "현재 산출물 형식"이 아니라 "기존 문서 및 수기 편집 입력"을 다룬다고 고쳐 쓴다.

### 신규

| 대상 | 검증 |
|---|---|
| A `:642` | 합성 자가 수정 중 `OperationCanceledException` 발생 시 `RunCodeObjectPipelineAsync`가 전파 |
| A `:1447` | 명세서 L3 피드백 재생성 중 취소 시 전파 |
| A `:1803` | 통합 계획서 L1 재보완 중 취소 시 전파 |
| B | 위 `FormatVerifiedDocument_EmitsScoreLinesWithoutDescriptiveComments` |
| E | `FormatUnverifiedDocument(body, null, …)`가 `검증 상태: 검증 없음`은 내고 `근거 명세서 검증 상태`는 내지 않음 |

D는 새 테스트를 만들지 않는다. `ConsoleUserInteraction`에는 단위 테스트 기반이 없고, `StatusLabel`이 네 상태를 모두 다룬다는 사실은 포매터 테스트가 이미 고정하고 있다.

**A가 이번 사이클의 유일한 위험 요소다.** 세 지점 모두 자가 수정·재생성 경로라 도달시키려면 선행 조건을 만들어야 한다.

- `:642`는 `_actorEffort == "dynamic"`이면서 합성본이 L1을 통과하지 못해야 한다
- `:1447`은 L3 승인 화면에서 `ProvideFeedback`을 거쳐야 한다
- `:1803`은 통합 L3 피드백 반영본이 L1을 통과하지 못해야 한다

`MechanicalValidator`는 인터페이스가 없고 메서드가 `virtual`이 아니라 **NSubstitute로 대체할 수 없으므로**, L1 통과/실패는 실제 마크다운 본문으로 유도해야 한다. 구현 계획 수립 시 세 지점의 도달 조건을 각각 확인한다. 도달 조건을 만들 수 없는 지점이 있으면 그 사실을 보고하고, 테스트 없이 넘어가는 대신 계획을 조정한다.

## 에러 처리

프로젝트 규약을 그대로 따른다.

- 포매터는 순수 함수이며 IO·AI 호출이 없어 예외 경로가 없다
- A의 변경은 취소 외 예외의 soft-fail 동작을 유지한다. 로그가 추가될 뿐 흐름은 같다
- Spectre.Console 출력에 새로 들어가는 런타임 값이 없다
- API 키를 소스나 `appsettings.json`에 하드코딩하지 않는다

## 범위 밖

직전 사이클 최종 리뷰의 나머지 3건은 새 정책 결정을 필요로 하므로 별도 사이클로 남긴다.

- **L2 리뷰 호출 재시도 인프라.** `VerificationPipelineOrchestrator.cs:1113-1120`과 통합 경로의 대응 지점은 일시적 API 오류 한 번에 `break`하며, `_maxAttempts`가 남아 있어도 재시도하지 않는다. 재시도 횟수·백오프·취소 전파·비용 정책을 새로 정해야 한다
- **통합 루프의 점수 임계값 강제.** `:1688`은 모델의 `HasDefects`만 신뢰하는 반면 단일 객체 루프 `:1067-1071`은 다섯 점수를 직접 검사한다. Critic이 낮은 점수와 함께 `HasDefects: false`를 반환하면 `검증 상태: 통과` 옆에 낮은 종합 신뢰도가 나란히 찍힌다
- **인터페이스 점수 필드 부재.** `VerificationDocumentFormatter`가 `인터페이스 점수`를 산출물에 쓰지만 `SpecHeader`에 대응 필드가 없어 승인 화면이 무시한다(캐시 왕복에서는 살아남는다). 고칠 때 함정이 있다 — 필드를 추가하면 `ConsoleUserInteraction.cs:105-109`의 `?? 10` 폴백 뒤에 놓여 여섯 번째 조작 만점 위험이 생긴다
