# 통합 배치 계획서 단계별 분할 생성 설계

- 작성일: 2026-08-06
- 상태: 설계 승인됨 (구현 계획 수립 전)

## 배경

같은 12개 정산 프로시저를 같은 프롬프트로 두 번 돌린 산출물이 `output/jobs/`에 남아 있다. `SettleProcDaily`(2026-08-05 15:37)와 `POQSettleProcDaily`(2026-08-06 15:23)다. 두 실행 사이에 `AiService.cs`에서 바뀐 것은 목차 재수립 경로 하나뿐이고(`185c097`), `POQSettleProcDaily/raw/`에 `PlanStructure.superseded-*.md`가 없으므로 그 경로는 발동조차 하지 않았다. 즉 두 문서의 모든 차이는 **동일 프롬프트에서의 실행 간 편차**다.

그 편차의 크기가 문제다.

| | `SettleProcDaily` | `POQSettleProcDaily` |
|---|---|---|
| L2 종합 신뢰도 | 82 | 88 |
| `## 단계별 이행 상세 및 의사코드` 총량 | 939줄 | 1,062줄 |
| 앞 2단계가 차지한 비중 | 348줄 (37%) | **702줄 (66%)** |
| 뒤쪽 단계 평균 | 54줄 (9개 단계) | **22줄 (7개 단계)** |

`POQSettleProcDaily`는 L2에서 88점을 받았지만, `S10 PG 회수 통계 생성`은 12줄이고 코드 블록이 하나도 없다. `S06`, `S08`, `S11`도 같다. `S08 회수일 및 지급일 산정`은 원본 오류코드 11단계를 표로 나열만 하고 SQL이 없다. 반면 `S01`과 `S02`는 레거시 SQL을 컬럼 단위로 전사해 702줄을 썼다. 코딩 에이전트에 이 문서를 넘기면 앞 두 단계만 구현되고 나머지는 원본 프로시저를 다시 봐야 한다.

원인은 출력 예산의 배분이다. `OpenAiClient.cs:226`은 비로컬 공급자에게 `max_tokens`를 아예 보내지 않는다 — ReSet이 자른 것이 아니라 모델이 스스로 예산을 나눈 결과이며, 두 문서 모두 2,287줄 / 2,415줄로 비슷한 총량에 수렴했다. **총량은 사실상 고정인데, 그 예산을 단계 사이에 어떻게 나눌지에 대한 규칙이 지침에 한 줄도 없다.**

검출도 되지 않는다. 세 계층 어디에서도 이 결함이 걸리지 않았다.

| 계층 | 강제하는 것 | 강제하지 않는 것 |
|---|---|---|
| L1 `ValidateConsolidated` (`MechanicalValidator.cs:108`) | H2 4개 존재, Mermaid 문법 | 단계 커버리지, 단계별 최소 내용 |
| L2 `ReviewConsolidatedPlanAsync` (`AiService.cs:2023`) | 5개 축 채점, 8점 미만 시 결함 | 12개 SP의 오류코드 전수 대조, 단계별 밀도 |
| 생성 지침 규칙 1~15 (`AiService.cs:1904`) | SQL 안전성(SNAPSHOT/Shadow/청킹/오류코드) | 문서 완성도·예산 배분 |

특히 규칙 7(「UNION·JOIN·원천 테이블을 단순화하지 마라」)과 규칙 9(「원본 오류코드를 정확히 재사용하라」)는 **산문으로 요구되기만 하고 어디서도 검증되지 않는다.** 12개 SP의 오류코드를 전수 대조하는 것은 AI가 신뢰성 있게 하는 일이 아니라 문자열 대조라 기계의 일인데, 지금은 기계가 그 일을 하지 않는다.

복구 경로도 없다. 배치 경로는 실패하면 **문서를 통째로 다시 생성한다**. `RegenerationScope`는 단일 SP 명세서 전용(`Overview`/`Crud`/`Logic`)이고 통합 계획서에는 적용되지 않는다. 그래서 검출만 강화하면 "조용히 나쁜 문서"가 "L1Exhausted로 실패하는 문서"로 바뀔 뿐이다 — 같은 예산으로 다시 뽑으니 쏠림이 재발하고 재시도 예산만 탄다.

## 목표와 범위

출력 예산 경쟁을 구조적으로 제거해 뒤쪽 단계의 하한을 확보하고, 그 하한을 기계가 검사하며, 미달을 국소적으로 보수한다.

**범위 안**
- 목차 수립 단계가 구조화된 단계 목록을 함께 출력
- `## 단계별 이행 상세 및 의사코드` H2 하나만 단계별로 분할 생성
- 단계 단위 기계 검사(`ValidateBatchStep`)와 단계당 1회 국소 재시도
- L2 결함을 단계로 귀속시키는 구조화 신호(`DefectiveSteps`)와 지목 재생성

**범위 밖**
- 나머지 H2 3개(`아키텍처 개요`, `Mermaid 흐름도`, `검증 SQL 세트`)의 분할. 검증 SQL 세트는 두 실행에서 항목당 평균 47줄 / 55줄로 붕괴하지 않았고, 개요·Mermaid도 마찬가지다. 쏠림은 오직 예산을 다투는 12~13개 항목이 있는 `단계별 이행 상세`에서만 관측됐다
- 생성 지침 규칙 1~15의 추가·수정. 규칙을 늘리면 앞 단계가 지침 소화에 예산을 더 쓰고 뒤가 더 얇아진다 — 지금 관측된 쏠림을 악화시킨다
- 개선전 문서에는 있었고 개선후에 사라진 항목들(재시작 흐름도, UDF Golden Master 게이트). 이는 H2③이 아니라 개요·정책 영역의 문제이므로 분할이 쏠림을 제거한 뒤에 별건으로 다룬다
- `MaxL2Attempts` 예산, `BestAttempt`/`RetryRescue`, `StructureRedraftPolicy`, `VerificationOutcome` enum

## 설계

### 1. 계약 객체 — 구조화 단계 목록

목차 수립 단계가 산문 목차와 함께 다음을 출력한다.

```json
{
  "Steps": [
    {
      "Code": "S08",
      "Name": "PG 회수일 및 고객 지급일 산정",
      "LegacyProcedures": ["UP_UTIL_SETTLE_EXPECT_PROC"],
      "TargetTables": ["dbo.TSettleMst", "SETTLE_POQ_DB.dbo.TSettleMst"],
      "ErrorCodes": ["-1", "-2", "-3", "-4", "-5", "-10", "-11", "-12", "-13", "-15", "-17"],
      "Chunkable": false
    }
  ]
}
```

C# 쪽 표현은 `ReSet.Core/Services/BatchStepPlan.cs`에 둔다.

```csharp
public sealed record BatchStepPlan(
    string Code,
    string Name,
    IReadOnlyList<string> LegacyProcedures,
    IReadOnlyList<string> TargetTables,
    IReadOnlyList<string> ErrorCodes,
    bool Chunkable);
```

이 객체가 세 가지 역할을 동시에 한다.

1. **분할 단위.** `Steps` 원소 하나가 생성 호출 하나다.
2. **하한 검사 기준.** `TargetTables`와 `ErrorCodes`가 본문에 등장하는지 기계적으로 대조한다.
3. **재생성 좌표.** L2가 결함을 지적하면 `Code`로 그 단계를 특정해 그 단계만 다시 뽑는다.

헤딩 파싱으로 이 목록을 대신하지 않는다. 두 실행의 목차가 이미 반증한다 — `SettleProcDaily`는 단계를 H3(`### P00.`)에 뒀고 `POQSettleProcDaily`는 H4(`#### S00.`)에 두었으며, 후자는 `#### Step 0. 레거시 기준선`, `#### Workflow 실행 의사코드`, `#### Phase 1. 분석`처럼 단계가 아닌 헤딩을 같은 레벨에 섞었다. 결정적으로 `SettleProcDaily`는 `### P20~P23. 정산 원장 생성`으로 **4개 단계를 헤딩 하나에 묶었다**. 헤딩을 세면 단계가 4개 줄어든다.

최종 문서가 목차의 중첩을 따르지도 않는다. `POQSettleProcDaily` 목차는 단계를 `### 단계별 실행 상세` 아래 H4로 뒀지만 최종 문서는 `### 5. S01 …`로 평탄화했다. 목차와 산출물 사이에 기계가 신뢰할 계약이 없다는 뜻이고, 이 객체가 그 계약이다.

**저장 위치는 `raw/PlanStructure.md` 안의 ` ```json ` 블록이다.** 별도 파일로 빼지 않는다. 기존 계약 — 「`raw/PlanStructure.md`는 파이프라인이 종료하거나 문서를 사용자에게 건네는 모든 지점에서 그 산출물을 실제로 만든 목차를 담는다」 — 을 그대로 지키기 위해서다. 파일이 둘이면 `TryCommitPlanStructureAsync`(`VerificationPipelineOrchestrator.cs:2132`)가 두 파일의 원자성을 보장해야 하고, 구제 채택 시 `superseded-{n}` 처리도 이중화된다. 한 파일 안에 있으면 목차를 되돌리는 것만으로 단계 목록도 함께 되돌아간다.

### 2. 시그니처 변경 3건

**(1) `IAiService.DraftBatchPlanStructureAsync`** — 프롬프트에 JSON 블록 출력 지시를 추가한다. 시그니처는 바뀌지 않는다. 재수립 모드(`previousStructure`가 비어 있지 않을 때)에도 같은 지시가 유지된다. 프롬프트는 영문으로 작성한다(AGENTS.md 하이브리드 영문 프롬프트 규칙).

**(2) `IAiService`에 단계 본문 생성 메서드 신설**

```csharp
Task<AiResult> GenerateBatchStepSectionAsync(
    BatchStepPlan step,
    IReadOnlyList<BatchStepPlan> allSteps,
    string sharedConventions,
    List<(string FileName, string Content)> specs,
    string targetLanguage,
    string jobName,
    string? effort = null,
    CancellationToken cancellationToken = default);
```

`GenerateConsolidatedBatchPlanAsync`를 확장하지 않고 메서드를 나누는 이유는 반환 계약이 다르기 때문이다. 전자는 H2 4개를 갖춘 완결 문서를 돌려주고 후자는 H3 섹션 하나를 돌려준다. 같은 메서드에 플래그로 두 계약을 겹치면 L1 검증 대상이 무엇인지가 호출부마다 달라진다.

**(3) `ReviewResult`에 `DefectiveSteps` 추가**

```csharp
public List<string> DefectiveSteps { get; set; } = new();
```

Critic 프롬프트의 출력 JSON 스키마에 같은 필드를 추가하고 `ParseReviewResult`가 파싱한다. `ScoreAccuracy` 등과 정확히 같은 패턴이다.

**`FeedbackComment` 산문에서 단계 코드를 파싱하지 않는다.** `RegenerationScopeSelector`의 클래스 주석이 이미 그 실패를 기록하고 있다 — *"이전 구현은 Actor에게 보낼 피드백 문자열에 키워드를 매칭해 범위를 정했다. … LLM이 쓴 산문에 키워드를 거는 방식이라 프롬프트 문구가 바뀌면 아무 신호 없이 오작동한다."* 같은 실수를 다른 자리에 반복하지 않는다.

### 3. 데이터 흐름

브레인스토밍과 목차 수립(`VerificationPipelineOrchestrator.cs:1708`, `:1716`)은 그대로다. 바뀌는 것은 `:1722`의 단일 생성 호출이다.

```
목차 수립 → 산문 목차 + Steps[] JSON
    │
    └─ 파싱 실패 → 분할 포기, 현행 GenerateConsolidatedBatchPlanAsync 단일 호출로 폴백
    ▼
[3a] 골격 생성 · 1회 호출
     H2① 아키텍처 개요 / H2② Mermaid / H2④ 검증 SQL
     + H2③의 공통 소절(오류 추적 패턴 · Shadow 정책 · Chunk 정책)
     단계 섹션 자리는 비워 둔다
    ▼
[3b] 단계 본문 생성 · Steps[] 원소당 1회
     프롬프트 = 명세서 → Steps[] 전체 → 3a의 공통 규약 → "이번 단계는 S08"
     출력 = 그 단계의 H3 섹션 마크다운 하나
    │
    ├─ 생성 직후 ValidateBatchStep → 미달이면 그 단계만 1회 재시도 → 여전히 미달이면 기록하고 진행
    ▼
[3c] 조립 — 골격의 빈 자리에 Steps[] 순서대로 삽입
    ▼
문서 L1 (현행 ValidateConsolidated) → 문서 L2 (현행 ReviewConsolidatedPlanAsync)
```

**3a를 먼저 도는 이유.** H2③의 공통 소절(`공통 SQL 오류 추적 패턴`, `Shadow Table 및 복구 정책`, `Chunk Paging 적용 정책`)은 모든 단계가 참조하는 규약이다. 단계별로 각자 쓰게 하면 13개 단계가 서로 다른 오류 처리 관례를 선언한다. 한 번 확정해 뒤로 넘긴다.

**캐시 정합.** 3b의 N개 호출은 프롬프트 접두사(명세서 → `Steps[]` → 공통 규약)가 완전히 동일하고 마지막 지시문만 다르다. `OpenAiClient.cs:60`의 `prompt_cache_key`와 자동 접두사 캐싱이 그대로 먹으므로, 호출이 N배로 늘어도 입력 비용은 거의 1배다. 접두사를 이 순서로 고정하는 것이 설계 요구사항이며, 테스트로 회귀를 막는다.

**진행률 표시.** 3단계 구조는 유지되므로 순번은 `3/3`을 쓴다. 분할은 그 안의 하위 진행이므로 `3/3. 최종 생성 중 (S05 · 5/13)...`처럼 부제로 표기한다. 분할 자체에 `n/3` 순번을 새로 부여하지 않는다(AGENTS.md TUI 상태 표기 규칙).

### 4. 단계 하한 검사 — `ValidateBatchStep`

`MechanicalValidator`에 메서드를 추가한다. `BatchStepPlan` 하나와 그 단계의 마크다운을 받아 결정적으로 검사하며, AI 호출이 없으므로 비용이 0이다.

| # | 검사 | 근거 |
|---|---|---|
| 1 | 섹션이 `### ` 헤딩으로 시작하고 `Code`를 포함한다 | 조립 무결성 |
| 2 | 코드 블록이 1개 이상 (` ```sql ` / ` ```csharp ` / ` ```text `) | 아래 실측 |
| 3 | `TargetTables`가 전부 본문에 등장한다 | 생성 지침 규칙 7의 기계적 강제 |
| 4 | `ErrorCodes`가 전부 본문에 등장한다 | 생성 지침 규칙 9의 기계적 강제 |

대조 방식을 두 건 못 박는다. 느슨하게 두면 검사가 무의미해지기 때문이다.

- **테이블명은 스키마·DB 접두사를 뗀 이름으로 대조하고 대소문자를 무시한다.** `POQSettleProcDaily`는 같은 테이블을 `dbo.TSettleMst`와 `TSettleMst`로 섞어 쓴다. 접두사까지 포함해 대조하면 정상 문서가 실패한다.
- **오류코드는 단어 경계로 대조한다.** 단순 부분 문자열 대조는 `-1`이 `-10`·`-13` 안에서 걸려 검사가 통째로 무력화된다. `S08`의 `ErrorCodes`가 `-1`부터 `-17`까지 11개인 것이 정확히 이 경우다.

2번 검사를 두 산출물에 실제로 돌려 조준을 확인했다. `POQSettleProcDaily`에서 `S06`(19줄)·`S08`(20줄)·`S10`(12줄)·`S11`(24줄)을 잡고, `S07`(24줄·블록 1개)·`S09`·`S12`(24줄·자기 조인 SQL과 오류코드 보유)는 통과시킨다. `SettleProcDaily`에서는 `P30`(48줄·블록 0개)을 잡는다. 붕괴한 단계만 정확히 걸러낸다.

**최소 줄 수 검사를 넣지 않는다.** 빈 줄로 게임할 수 있고, 2번 검사가 같은 결함을 더 정확히 잡는다 — `S12`는 24줄이지만 통과시키는 것이 맞다.

**청킹 정합 검사(`Chunkable: false`인데 본문에 `WHILE` 청킹 루프가 있으면 실패)를 넣지 않는다.** 생성 지침 규칙 6이 금지하는 패턴이지만 두 산출물에서 실제로 발생한 적이 없다. 관측되지 않은 결함에 검사를 붙이지 않는다.

### 5. 실패 처리

| 실패 지점 | 처리 | 재시도 예산 |
|---|---|---|
| `Steps[]` JSON 없음 / 파싱 실패 | 분할을 포기하고 현행 단일 호출로 폴백. 파이프라인을 죽이지 않는다 | 소모 없음 |
| `Steps[]`가 비었거나 40개를 넘음 | 동일 폴백 (폭주 방지) | 소모 없음 |
| 3a 골격 호출 실패 / 빈 응답 | 현행 생성 실패와 동일 처리 (`:1734`의 `RetryRescue` 경로) | 소모 |
| 3b 단계 호출 실패 / 빈 응답 | 그 단계만 1회 재시도 → 실패 시 `> [!WARNING] 이 단계는 생성에 실패했습니다` 마커를 넣고 진행 | 소모 없음 |
| 3b 단계 하한 미달 | 그 단계만 1회 재시도 → 여전히 미달이면 채택하고 기록 | 소모 없음 |
| 조립 후 문서 L1 실패 | 현행 그대로 통짜 재생성 | 소모 |
| L2 결함 + `DefectiveSteps` 있음 | 지목된 단계만 3b 재호출. 골격(3a)과 나머지 단계는 그대로 재사용해 재조립 → L2 재실행 | 소모 |
| L2 결함 + `DefectiveSteps` 없음 | 현행 그대로 통짜 재생성 | 소모 |
| 재시도 소진 | 현행 그대로 `RetryRescue`가 최고점 시도 채택 + 배너 | — |
| 취소 | 실패가 아니다. 3a·3b를 감싸는 모든 `catch`에 `when (ex is not OperationCanceledException)` 필터를 단다 | — |

빈 응답에 섹션을 조용히 빼지 않고 경고 마커를 넣는다. 조용한 누락이 바로 이 설계가 없애려는 상태다 — 12줄짜리 `S10`은 아무도 실패했다고 말해주지 않았다.

**단계 재시도가 예산을 소모하지 않는 이유.** `MaxL2Attempts`는 Actor-Critic 문서 레벨 예산이다. 단계 하한 재시도는 리뷰 호출이 0인 국소 보수이므로 성격이 다르다. 대신 단계당 1회로 하드 캡해 폭주를 막는다 — 최악의 경우 추가 호출 13회이고 입력은 캐시된다.

**단계가 재시도 후에도 미달이면 문서 L1을 실패시키지 않는다.** 실패시키면 같은 결함으로 15호출짜리 통짜 재생성을 유발해 비용만 탄다. 대신 최종 배너에 미달 단계를 표기한다.

이것이 이 설계의 솔직한 한계다. 미달 단계가 있어도 L2가 5축 모두 8점 이상을 주면 종료 상태는 `Passed`이고 배너에 `⚠ 하한 미달 단계: S10`이 찍힐 뿐, 파이프라인이 강제로 막지 않는다. **절대적 보장이 아니라 가시성 확보다.** `VerificationOutcome`에 상태를 새로 추가하지 않는 이유도 같다 — L2를 통과한 문서의 종료 상태는 `Passed`가 맞고, 미달 사실은 배너가 나른다.

### 6. 기존 계약과의 정합

| 기존 메커니즘 | 영향 |
|---|---|
| `BestAttempt` / `RetryRescue` | 무변경. "시도" 단위는 여전히 조립된 문서 1개다. 단계 재시도는 문서를 만들지 않으므로 시도로 세지 않는다 |
| `StructureRedraftPolicy` (Job당 1회 재설계) | 무변경. 재수립되면 `Steps[]`도 새로 나오고 3a부터 전부 다시 돈다. 재수립 목차의 JSON 파싱이 실패하면 재설계를 통째로 폐기하고 이전 목차·이전 `Steps[]`를 유지한다(`c1e5098`이 세운 선례) |
| `TryCommitPlanStructureAsync` 계약 | 자동 충족. `Steps[]`가 `PlanStructure.md` 안에 있다 |
| `AdoptPlanStructureForRescueAsync` (구제 시 목차 복원) | 자동 충족. 목차를 되돌리면 `Steps[]`도 함께 되돌아간다 |
| `VerificationOutcome` enum | 무변경 |
| `CancellationPolicyTests` | 3a·3b의 새 await마다 취소 필터가 필요하다. 기존 테스트가 Roslyn 구문 트리로 자동 검사한다 |

### 7. 비용

| | 현행 | 신규 1회차 | 신규 2회차 이후 (단계 지목 시) |
|---|---|---|---|
| 생성 호출 | 1 | 1(골격) + N(단계) + 최대 N(단계 재시도) | 지목된 단계 수만 (통상 1~3) |
| 리뷰 호출 | 1 | 1 | 1 |
| 입력 토큰 | 1× | 접두사 캐시로 약 1~2× | 약 1× |
| 출력 토큰 | 약 30k | 약 65k | 지목분만 |

출력 토큰이 2배가 되는 것이 이 설계의 목적이다. 지금은 모델이 하나의 출력 예산 안에서 앞 단계에 66%를 쓰고 뒤를 굶겼다. 단계마다 독립 호출이면 그 경쟁 자체가 사라진다.

### 8. 설정

새 설정 키를 추가하지 않는다. 분할 생성이 기본 경로가 되고, JSON 파싱 실패 시 폴백이 이미 안전망이다. 킬 스위치를 두면 두 경로를 모두 유지·테스트해야 하는 부채가 생기는데, 옛 경로는 이 설계의 목표와 정면으로 맞지 않는다. 단계 재시도 1회와 `Steps[]` 상한 40개도 하드코딩한다.

## 테스트

**신규 `MechanicalValidatorTests` 추가분 — `ValidateBatchStep`**
- 4개 검사 각각의 실패 케이스와 통과 케이스
- 픽스처는 실제 산출물을 쓴다: `POQSettleProcDaily`의 `S10` 본문(코드 블록 0개, 실패해야 함)과 `S12` 본문(24줄이지만 SQL·오류코드 보유, 통과해야 함). 합성 문자열보다 회귀 가치가 크다
- `TargetTables` 중 하나만 빠져도 실패한다
- `ErrorCodes` 중 하나만 빠져도 실패한다
- 본문이 `TSettleMst`로만 적어도 `dbo.TSettleMst`를 만족한 것으로 본다 (접두사 무시 대조)
- `ErrorCodes`에 `-1`이 있고 본문에 `-10`만 있으면 **실패한다** (단어 경계 대조. 부분 문자열 대조로 회귀하면 이 테스트가 잡는다)

**`AiServiceTests` 추가분**
- 목차 프롬프트가 `Steps[]` JSON 출력을 지시한다 (재수립 모드에서도 유지된다)
- `GenerateBatchStepSectionAsync` 프롬프트가 지정 단계 코드와 공통 규약을 포함한다
- **N개 단계 프롬프트에서 마지막 지시문을 제외한 접두사가 완전히 동일하다** — 캐시 정합 회귀 방지
- Critic 프롬프트의 출력 스키마에 `DefectiveSteps`가 있고 `ParseReviewResult`가 이를 파싱한다

**`VerificationPipelineOrchestratorTests` 추가분**
- `Steps[]` 파싱 실패 → `GenerateConsolidatedBatchPlanAsync`가 1회 호출되고 `GenerateBatchStepSectionAsync`는 호출되지 않는다
- `Steps[]`가 41개 → 동일 폴백
- 정상 경로: `Steps[]`가 3개면 `GenerateBatchStepSectionAsync`가 3회 호출되고 조립 결과에 세 섹션이 순서대로 들어간다
- 단계 하한 미달 → 그 단계에 대해 **정확히 1회만** 재시도한다 (2회 이상 호출되지 않는다는 회귀 방지)
- 재시도 후에도 미달 → 채택하고 진행하며, 문서 L1은 그 이유로 실패하지 않는다
- 단계 호출 예외 → 경고 마커가 삽입되고 나머지 단계는 계속 생성된다
- L2 결함 + `DefectiveSteps: ["S02"]` → `GenerateBatchStepSectionAsync`가 `S02`에 대해서만 재호출되고 골격은 재생성되지 않는다
- L2 결함 + `DefectiveSteps` 비어 있음 → 현행 통짜 재생성 경로를 탄다
- 목차 재수립 발동 시 `Steps[]`가 새 목차의 것으로 갱신된다

**`CancellationPolicyTests`** — 코드 추가 없음. 기존 규칙이 새 await를 자동 검사한다.

## 문서 동기화

- `docs/architecture.md` §3.1 배치 Mermaid — 3/3 생성이 골격 1회 + 단계 N회로 나뉘고 단계 L1이 그 안에 들어가는 흐름을 반영
- `docs/architecture.md` §4.4.5 — 목차 재설계·기록 계약에 `Steps[]`가 같은 파일에 실린다는 사실 추가
- `AGENTS.md` — `AiService.cs` 항목에 단계 분할 생성과 접두사 고정 규칙, `MechanicalValidator.cs` 항목에 `ValidateBatchStep` 등재
- `README.md` — Multi-Step Agentic Workflow 설명에 단계별 분할 생성 추가

## 완료 기준

- `dotnet clean && dotnet build`에서 경고가 정확히 8건 (기존 `DbMetadataServiceTests`의 CS8600/CS8602. 착수 시점 실측값)
- `dotnet test`가 기존 667건 + 신규분 전부 통과 (착수 시점 실측값)
- 위 문서 4종 동기화 완료
- 회귀 확인: 동일 12개 SP로 통합 배치를 재실행했을 때 모든 단계 섹션이 `ValidateBatchStep`을 통과하거나, 통과하지 못한 단계가 배너에 표기된다
