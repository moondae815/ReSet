# L3 피드백의 단계 지목 재생성 설계

- 작성일: 2026-08-07
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 선행: [2026-08-06 단계별 분할 생성](2026-08-06-batch-plan-step-split-design.md)

## 배경

통합 배치 계획서의 단계 상세를 단계마다 한 번씩 생성하는 분할이 `9e13c04`로 병합됐다. 실측에서 단계 본문이 1,062줄에서 6,514줄로 늘고 하한 미달이 0건이 됐다.

그런데 L3(인간 승인) 단계에서 사용자가 피드백을 주면, 재생성이 `GenerateConsolidatedBatchPlanAsync` — **문서 전체를 한 번에 만드는 단일 호출** — 로 넘어간다. 분할 경로를 전혀 타지 않는다.

**지금 구조로는 사용자가 피드백을 줄수록 문서가 나빠진다.** 6,514줄이 통짜 재생성으로 돌아가면 분할 이전의 예산 붕괴가 그대로 재현된다. 실제 상황이 이미 발생했다 — 마지막 실행이 `품질 미달`로 끝났고 Critic이 S05 한 곳(`UP_UTIL_SETTLE_EXCEPTION_PROC`의 `CommMethod` 반올림 분기가 산문으로 미뤄짐)을 지목했는데, 그 한 곳을 고치려고 피드백을 넣으면 나머지 12단계를 잃는다.

원인은 선행 설계의 범위 설정이다. 분할 배선을 재시도 루프에만 넣고 L3는 건드리지 않았으며, 그 설계의 흐름도도 재시도 루프까지만 그렸다.

## 목표와 범위

L3 피드백 재생성이 분할 경로를 쓰게 하고, 피드백이 겨냥하는 단계만 다시 만들 수 있게 한다.

**범위 안**
- L3 피드백 재생성을 `GenerateBySplitAsync`로 배선
- 사용자가 재생성 대상 단계를 직접 고르는 UI
- 채택된 회차와 캐시 상태의 정합 확보
- 배너 재부착과 커버리지 재계산

**범위 밖**
- **L2 재실행.** 피드백 반영본은 지금처럼 `planOutcome = ReviewNotRun`으로 끝난다. 리뷰받지 않은 문서가 이전 통과 판정을 자칭하지 않는다는 기존 규칙 그대로다
- **AI 귀속.** 피드백 산문에서 대상 단계를 추론하지 않는다. 사용자가 직접 고른다
- **단계 목록 편집.** 단계 추가·삭제·순서 변경은 기존의 "구조 변경" 경로(목차 재수립)로 처리된다
- **병렬 생성.** 별도 스펙(`2026-08-07-batch-step-parallel-generation-design.md`)에 있다. 그쪽이 먼저 들어가면 이 경로도 자동으로 혜택을 받는다 — `GenerateBySplitAsync` 하나를 공유하기 때문이다

## 설계

### 1. 피드백 처리 흐름

```
사용자 피드백 (자유 텍스트)
  ↓
"구조(목차)까지 바꾸나요?"                       ← 기존 질문, 변경 없음
  │
  ├─ 예 ──▶ 목차 재수립 (StructureRedraftPolicy 예산 밖, 기존 그대로)
  │          ↓
  │        골격 재생성 + 전 단계 재생성           ← 목차가 바뀌었으니 전부
  │
  └─ 아니오 ──▶ "어느 단계에 대한 피드백입니까?"   ← 신설 다중 선택
                 │
                 ├─ 특정 단계 선택 ──▶ 골격·나머지 섹션 재사용, 지목 단계만 재생성
                 └─ 전체 / 미선택 ──▶ 골격 재사용, 전 단계 재생성
  ↓
조립 → 문서 L1 검사 → 배너 재부착 → planOutcome = ReviewNotRun
```

구조가 바뀌면 단계 목록 자체가 바뀌므로 단계 선택을 묻지 않는다. **답을 쓸 곳이 있을 때만 묻는다**는, `structureRedraftSupported`를 도입할 때 세운 원칙 그대로다.

### 2. 재사용하는 것과 다시 만드는 것

| 사용자 답변 | 목차 | 골격 | 단계 섹션 | 소요 |
|---|---|---|---|---:|
| 구조 변경 **예** | 재수립 | 재생성 | 전부 | 약 15분 |
| 구조 유지 + **전체** | 유지 | 재사용 | 전부 | 약 13분 |
| 구조 유지 + **일부 지목** | 유지 | 재사용 | 지목분만 | 약 1~3분 |

소요는 실측된 48분 실행(브레인스토밍 1:34, 목차 1:41, 골격 2:03, 단계당 약 1분)에서 유도한 추정이지 보장이 아니다. 병렬 생성 스펙이 먼저 들어가면 전체·구조 변경 항목이 크게 줄어든다.

구조를 유지할 때 골격을 재사용하는 근거는, 골격이 만드는 것이 개요·Mermaid·검증 SQL·공통 규약이고 목차가 그대로면 그것들이 바뀔 이유가 없다는 점이다. 다만 피드백이 개요나 검증 SQL을 향할 수도 있으므로, 선택 목록에 단계들과 나란히 **`(골격) 개요·흐름도·검증 SQL`** 항목을 하나 둔다. 그것을 고르면 골격도 재생성 대상이 된다.

### 3. 재사용하는 기계

새 생성 경로를 만들지 않는다. `GenerateBySplitAsync`는 이미 `previousSkeleton` · `previousSkeletonResult` · `previousSections` · `previousViolations` · `defectiveSteps`를 받아 지목 재생성을 수행한다 — L2 지목 경로가 쓰는 바로 그 인자다. L3는 `defectiveSteps`에 사용자가 고른 코드를 넣어 같은 메서드를 호출한다.

피드백 텍스트는 지금처럼 `User_Feedback_Log.txt`로 `specs`에 얹어 전달한다. 단계별 호출의 캐시 접두사는 `specs`를 포함하므로 피드백이 붙으면 접두사가 바뀌어 그 회차의 첫 호출이 캐시를 다시 채운다. 지목이 1~2단계면 무시할 만하고, 전체 재생성이면 첫 단계가 워밍 역할을 한다.

### 4. 채택된 회차와 캐시의 정합

이 설계에서 가장 조용히 틀리기 쉬운 부분이다.

재시도 루프를 빠져나온 시점에 다섯 값이 서로 다른 회차를 가리킬 수 있다.

| 값 | 구제 채택 시 되돌아가는가 |
|---|---|
| `consolidatedPlan` | 예 — 채택 회차 |
| `currentPlanStructure` | 예 — `AdoptPlanStructureForRescueAsync` |
| `stepFloorViolations` | 예 — `bestAttemptStepFloorViolations` |
| `lastSkeleton` · `lastSkeletonResult` | **아니오 — 마지막 생성 회차** |
| `lastStepSections` | **아니오 — 마지막 생성 회차** |

마지막 실행이 정확히 이 경우였다. 3차가 L1 실패로 중단되고 1차가 채택됐으므로, L3에서 캐시된 섹션을 재사용하면 화면의 1차 문서가 아니라 폐기된 3차의 섹션 위에 피드백이 얹힌다. 사용자가 보는 문서와 고쳐지는 문서가 다르다.

지금은 L3가 이 값들을 쓰지 않아 잠복 상태다. **이 설계가 그것을 깨운다.**

**해법: 다섯 값을 레코드 하나로 묶는다.**

```csharp
/// <summary>
/// 채택 후보(BestAttempt.Current)를 실제로 만들어 낸 상태 일체.
/// 후보가 교체되는 그 자리에서 통째로 붙잡고, 구제 채택 시 통째로 되돌린다.
///
/// 다섯 값을 개별 변수로 두면 "함께 움직여야 한다"가 규율이 되고, 규율은
/// 깨진다 — 이 파이프라인에서 이미 세 번 깨졌다. 레코드로 묶으면 구조가 된다.
/// </summary>
private sealed record AdoptedGenerationState(
    string PlanStructure,
    string? Skeleton,
    AiResult? SkeletonResult,
    IReadOnlyDictionary<string, string>? StepSections,
    IReadOnlyDictionary<string, string> FloorViolations);
```

붙잡는 곳은 한 군데 — `bestAttempt.TryRecord(...)`가 `true`를 돌려준 자리. 이미 `bestAttemptStructure`와 `bestAttemptStepFloorViolations`를 잡는 지점이라 두 문장이 한 문장이 된다.

되돌리는 곳은 네 군데 — 생성 실패·L1 소진·L2 정상 소진·리뷰 실패의 구제 채택 지점. 각각이 지금 두 줄로 하는 일을 한 줄로 한다.

**이 변경은 기존 동작을 바꾸지 않는다.** `PlanStructure`와 `FloorViolations`는 현재와 같은 시점에 같은 값으로 붙잡히고 되돌아간다. `Skeleton` · `SkeletonResult` · `StepSections` 세 개가 그 대열에 합류할 뿐이다.

부수 효과로 확인된 결함 하나가 함께 닫힌다. 최종 리뷰의 Finding 1이 지목 재생성 경로의 `finalAiResult`가 합성 스텁이라고 지적해 `lastSkeletonResult`가 도입됐는데, 그 값 역시 구제 시 되돌아가지 않는다. 즉 구제 채택 후의 `raw/prompt-context.md`는 현재도 채택되지 않은 회차의 프롬프트를 기록할 수 있다. 같은 묶음에 들어가면 자동으로 해소된다.

**유지보수 불변식.** `currentPlanStructure` 재계산 주석이 이미 같은 성격의 규칙을 적고 있다. 여기에 한 줄을 더한다 — 채택 문서를 이전 회차로 되돌리는 종료 경로를 새로 추가한다면 반드시 `AdoptedGenerationState`를 통째로 되돌려야 하며, 개별 필드만 되돌리는 코드를 쓰지 않는다.

### 5. 인터페이스 변경

```csharp
Task<HumanReviewResult> RequestHumanReviewAsync(
    string selectedOption,
    string specificationMarkdown,
    VerificationOutcome outcome,
    bool structureRedraftSupported = false,
    IReadOnlyList<BatchStepPlan>? steps = null);   // 신설
```

`structureRedraftSupported`를 도입할 때와 같은 방식이다. 선택적 매개변수라 단일 SP 경로의 호출부와 테스트 fake는 바뀌지 않는다.

`HumanReviewResult`에 결과를 싣는다.

```csharp
/// <summary>
/// 사용자가 지목한 재생성 대상 단계 코드. Decision이 ProvideFeedback이고
/// RedraftStructure가 false일 때만 의미가 있다. 비어 있으면 전체 재생성이다 —
/// "아무것도 안 고름"과 "전체"를 같은 뜻으로 둔다.
/// </summary>
public List<string> TargetStepCodes { get; set; } = new();

/// <summary>골격(개요·흐름도·검증 SQL)도 다시 만들지 여부.</summary>
public bool RegenerateSkeleton { get; set; }
```

`steps`가 null이거나 비면 단계 선택 질문을 띄우지 않는다.

### 6. 다중 선택 UI

`ConsoleUserInteraction`에서 `structureRedraftSupported && !redraftStructure && steps?.Count > 0`일 때만 표시한다.

```
어느 단계에 대한 피드백입니까? (Space로 선택, Enter로 확정, 미선택 시 전체)
  [ ] (골격) 개요 · Mermaid 흐름도 · 검증 SQL 세트
  [ ] S01  일별 수수료율 스냅샷 생성
  [ ] S05  예외 수수료 적용
  ...
```

Spectre의 `MultiSelectionPrompt`를 쓴다. 이 파일은 이미 `SelectionPrompt`와 `Confirm`을 쓰고 있어 새 의존성이 없다. `Required(false)`로 두어 미선택 확정을 허용하며, 그것이 곧 전체 재생성이다.

선택 결과를 결과 객체로 옮기는 규칙은 셋이다.

- 골격 항목을 고르면 `RegenerateSkeleton = true`. 이 항목은 `TargetStepCodes`에 넣지 않는다 — 단계 코드가 아니다
- 나머지 선택 항목의 단계 코드가 `TargetStepCodes`가 된다
- **골격을 고르면 단계 선택과 무관하게 전 단계를 재생성한다.** 공통 규약이 골격에 있고 모든 단계 섹션이 그것을 전제로 쓰였으므로, 규약이 바뀌면 그것을 인용한 섹션도 다시 써야 한다. 구현에서는 골격 선택 시 `TargetStepCodes`를 비워 "전체"로 만든다

### 7. 배너와 종료 상태

현재 L3는 `consolidatedPlan = rePlan`으로 배너를 전부 버린다. 통짜 재생성이면 옳지만 지목 재생성에서는 틀리다 — 손대지 않은 단계의 하한 미달 기록은 여전히 유효하다.

| 값 | 처리 |
|---|---|
| `stepFloorViolations` | `GenerateBySplitAsync`가 병합해 돌려준 결과를 채택 |
| 하한 미달 배너 | 재조립 후 다시 부착 |
| 커버리지 배너 | **항상 재계산.** `currentPlanStructure` 파싱과 문자열 대조뿐이라 비용이 없다. "구조가 안 바뀌었으면 건너뛴다"는 조건을 두면, 그 조건이 틀린 날 낡은 배너가 조용히 남는다 |
| `planReview` | `null` (변경 없음) |
| `planOutcome` | `ReviewNotRun` (변경 없음) |

배너 재부착과 커버리지 재계산은 루프 종료 직후 코드와 같은 로직이다. **한 곳으로 뽑아 두 자리에서 호출한다** — 두 벌로 두면 한쪽만 고쳐지는 날이 온다.

### 8. 문서 L1 검사

재조립 후 `ValidateConsolidated`를 한 번 돌린다. **다만 실패 시의 통짜 단일 호출 보완은 분할 경로에서 하지 않는다.** 그 보완은 문서 전체를 다시 써서 방금 지킨 6,500줄을 무너뜨린다.

분할 경로에서 문서 L1이 실패하는 경우는 H2 누락이나 Mermaid 문법이고 둘 다 골격이 만드는 것이다. 따라서 골격을 재생성 대상에 넣어 한 번 더 시도하고, 그래도 실패하면 기존 `L1Exhausted` 배너를 붙여 사용자에게 돌려준다. 단계 섹션은 손대지 않는다.

### 9. 실패 처리

| 상황 | 동작 |
|---|---|
| 목차 재수립 실패 | 기존 그대로 — 재수립을 폐기하고 이전 목차로 진행 |
| 골격 재생성 실패 | 골격을 다시 만드는 경로(구조 변경, 골격 지목, L1 재시도)에서만 발생한다. `GenerateBySplitAsync`가 null을 반환하면 피드백을 반영하지 못했음을 알리고 직전 문서로 되돌아간다. **통짜 단일 호출로 폴백하지 않는다.** 골격을 재사용하는 경로는 골격 API를 호출하지 않으므로 이 실패가 없다 |
| 단계 하나 실패 | 그 단계만 경고 마커, 나머지 유지 (기존 성질) |
| 지목 코드가 목록에 없음 | 발생하지 않는다 — 사용자가 목록에서 고른다 |
| 취소 | 기존 필터 그대로 전파 |

골격 실패 시 통짜 폴백을 하지 않는 것이 재시도 루프와 다른 점이다. 루프에서는 "문서가 아예 없는 것보다 통짜라도 있는 게 낫다"가 성립하지만, L3에는 이미 승인 대기 중인 좋은 문서가 있다. 그것을 통짜로 갈아엎는 것은 개선이 아니다.

## 테스트

- **지목 재생성** — 사용자가 S05만 고름. `GenerateBatchStepSectionAsync`가 S05에 대해서만 호출되고 골격은 호출되지 않으며, 최종 문서에 나머지 단계의 기존 본문이 그대로 남는다
- **전체 재생성** — 미선택 확정. 전 단계 호출, 골격은 호출되지 않는다
- **골격 포함** — 골격 항목 선택. 골격 1회 + 전 단계 호출
- **구조 변경** — `RedraftStructure = true`. 목차 재수립 후 골격 + 전 단계이며, 단계 선택 질문이 뜨지 않는다
- **채택 정합 (핵심)** — 3회차 시나리오로 구제 채택을 일으킨 뒤 L3 지목 재생성. 재사용된 섹션이 채택된 회차의 것임을 단언한다. `AdoptedGenerationState` 복원을 제거하면 실패해야 한다
- **배너 재부착** — 지목 재생성 후에도 손대지 않은 단계의 하한 미달이 배너에 남는다
- **단일 SP 경로 무영향** — `steps` 미전달 시 단계 선택 질문이 뜨지 않고 기존 동작이 유지된다
- `CancellationPolicyTests`가 새 `catch`의 필터를 자동 검사한다

## 문서 동기화

- `docs/architecture.md` §3.1 Mermaid의 L3 분기에 단계 지목 추가
- `docs/architecture.md` §4.4.3(구조 변경 피드백)에 단계 지목 경로 추가
- `AGENTS.md`에 `AdoptedGenerationState`는 통째로만 되돌린다는 규칙
- `README.md`의 L3 설명에 한 줄

## 완료 기준

- `dotnet clean && dotnet build`에서 경고가 정확히 8건 (기존 `DbMetadataServiceTests`의 CS8600/CS8602)
- `dotnet test`가 기존 746건 + 신규분 전부 통과
- 위 문서 3종 동기화 완료
- 실측 확인: 마지막 실행의 산출물에 대해 S05만 지목해 재생성했을 때, 나머지 12단계 본문이 바이트 단위로 보존되고 S05만 갱신된다
