# 목차 단계 프로시저 명단 공급과 분할 실패 가시화 설계

**작성일**: 2026-08-13
**상태**: 설계 확정

## 목표

목차 수립 단계가 채우도록 요구받는 필드를 실제로 채울 수 있게 만들고, 분할 생성이
무산됐을 때 그 사실이 산출물에 드러나게 한다.

## 배경 — 두 번의 실측

### POQSettleProc6 (2026-08-13 13:32)

33단계 전부가 `LegacyProcedures`를 빈 배열로 냈다. 보강기(`PlanStructureEnricher`)는
그 필드를 색인 키로 쓰므로 대조할 원본을 잃었고, `ErrorCodes`·`TargetTables`·
`SchemaTables`가 연쇄로 비었다. 결과는 이렇다.

| 항목 | 값 |
|---|---|
| 하한 검사를 건너뛴 단계 | 32 / 33 |
| 커버리지 검사 보고 | 12개 프로시저 전부 "누락" (**오탐** — 본문은 12개를 모두 다룸) |
| 최종 판정 | 통과 88점 |

### POQSettleProc7 (2026-08-13 14:20)

Proc6 검토 후 `LegacyProcedures`에 규칙을 넣었다. *"명세서가 부르는 그대로 이름을
쓰라"*는 요구였다. codex-cli는 그 요구를 지킬 수 없다고 판단하고 단계 목록 자체를
비웠다. 목차 첫머리에 이유를 직접 적었다.

> 분석에는 12개 원천 프로시저의 정확한 이름·전체 반환 코드·프로시저별 변경 테이블이
> 포함되어 있지 않으며 (…) 이를 추정하면 `LegacyProcedures`, `TargetTables`,
> `ErrorCodes` 정확성 규칙을 위반합니다.

`{ "Steps": [] }` → `BatchStepPlanParser`가 목차를 버림 → 단일 호출 폴백.

| | Proc6 | Proc7 |
|---|---|---|
| 문서 크기 | 455,278 B | 40,234 B |
| 단계 섹션 | 33개 | 없음 |
| 코드·SQL 블록 | 157개 | 35개 |
| 원본 오류코드 보존 | 76 / 76 | **56 / 76** |
| 에이전트 지시서 태스크 | 35개 | 2개 |
| 경고 배너 | 2개 | **0개** |
| 신뢰도 점수 | 88 | **92** |

소실된 20개는 예외 처리가 가장 복잡한 세 프로시저에 몰렸다
(`UP_UTIL_SETTLE_EXCEPTION_PROC` 9개, `UP_UTIL_SETTLE_COMM_UPD` 6개,
`UP_UTIL_SETTLE_EXPECT_PROC` 5개).

**점수는 올랐고 문서는 나빠졌다.** 짧고 깔끔한 문서가 읽기 좋았기 때문이다. 점수는
누락을 볼 수 없다.

## 근본 원인

```
DraftBatchPlanStructureAsync(brainstormingResult, targetLanguage, jobName, ...)
```

목차 단계는 **명세서를 받지 않는다.** 브레인스토밍 텍스트만 받는다. 그런데 프롬프트는
명세서에만 있는 사실(프로시저의 정확한 표기)을 요구한다. 줄 수 없는 재료를 요구한
것이다.

브레인스토밍 산출물이 이름을 실어 나르는지는 회차마다 다르다 — Proc6은 0회,
Proc7은 3개(12개 중)뿐이었다. 그래서 이 결함은 조용히, 그리고 불규칙하게 나타난다.

Proc7 문서 본문에는 12개 이름이 전부 정확히 들어 있다(S01–S12 매핑 표까지 있다).
3단계는 명세서를 받으므로 알고 있었다. **재료는 파이프라인 안에 있고, 필요한 단계에만
전달되지 않는다.**

### 같은 결함의 네 번째

`SpecReturnCodeExtractor`는 이 문제로 이미 한 번 만들어졌다. 주석이 그대로 남아 있다.

> 목차의 ErrorCodes는 AI가 채우는데 실측 두 회차에서 26개 단계 중 25개가 빈
> 배열이었다. (…) 그래서 AI에게 다시 시키는 대신 명세서에서 뽑는다.

`ErrorCodes`, `MaxSteps`, `LegacyProcedures`, 그리고 이 건 — 같은 결함이 네 번 나왔다.
`LegacyProcedures`만 아직 모델 몫으로 남아 있고, 하필 그것이 추출기의 색인 키다.

## 범위

**포함**: 목차 단계에 프로시저 명단 공급, 규칙 재작성, 분할 미실행 배너, 문서 전체
오류코드 대조.

**제외**: 신뢰도 점수와 검증 커버리지의 분리 표기(별도 설계 필요), 브레인스토밍
프롬프트 개선(명단을 코드가 실어 나르므로 불필요), 0단계 목차의 재수립 유발(재설계
예산은 점수 정체용으로 남긴다).

---

## 설계 1 — 목차 단계 프로시저 명단 공급

### 1.1 시그니처

```csharp
Task<AiResult> DraftBatchPlanStructureAsync(
    string brainstormingResult,
    string targetLanguage,
    string jobName,
    IReadOnlyList<string> sourceProcedures,   // 신규 — 4번째, 기본값 없음
    string? effort = null,
    string? previousStructure = null,
    string? redraftFeedback = null,
    CancellationToken cancellationToken = default);
```

**기본값을 두지 않는다.** 기본값이 있으면 호출부가 빠뜨려도 컴파일이 통과해, 재료가
조용히 사라지는 지금과 같은 실패로 되돌아간다. 기본값 없는 파라미터는 선택적
파라미터보다 앞에 와야 하므로 위치는 `jobName` 다음이다.

이 배치는 기존 스텁 96곳(그중 59곳이 동일 문자열)을 **컴파일 오류로** 전부 드러낸다.
런타임에 조용히 어긋나는 것보다 낫다.

### 1.2 전달 경로

호출부는 두 곳이다.

| 위치 | 메서드 | 넘길 값 |
|---|---|---|
| `VerificationPipelineOrchestrator.cs:1822` | 최초 목차 수립 | `specs.Select(s => s.FileName)` |
| `VerificationPipelineOrchestrator.cs:2557` | `DraftReplacementPlanStructureAsync` (재수립) | 같은 값 (파라미터로 전달) |

**반드시 원본 `specs`를 쓴다.** 오케스트레이터는 재시도 회차마다 작업 사본
`specsCopy`에 `Feedback_Log.txt`를 덧붙이고, 바로 옆의 `BrainstormBatchPlanAsync`가
그 사본을 받는다. 사본을 넘기면 존재하지 않는 프로시저가 명단에 섞인다. 커버리지
검사가 같은 이유로 이미 한 번 물린 적이 있는 함정이다.

재수립 헬퍼 `DraftReplacementPlanStructureAsync`에도 같은 파라미터를 뚫는다.

### 1.3 프롬프트 — 명단 블록

사용자 프롬프트에 넣는다. 시스템 프롬프트가 아닌 이유는 잡마다 달라지는 값이라
캐시 접두사를 깨기 때문이다. 브레인스토밍 결과가 이미 그 자리에 있다.

```
[Source Procedures — use these names verbatim in `LegacyProcedures`]
- dbo.UP_Util_PG_Client_CMRate_Ins
- dbo.UP_UTIL_SETTLE_INS
  ...
```

### 1.4 프롬프트 — 규칙 재작성

현행 규칙(2026-08-13 도입, 이번 회귀의 원인)을 다음으로 교체한다.

```
- `LegacyProcedures` must be copied verbatim from the supplied Source
  Procedures list. It is how the pipeline links a step to its origin: the
  coverage check compares these names against that same list, and the
  enrichment pass uses them to fill `ErrorCodes` and `TargetTables`. Leave it
  empty only for a step with no legacy origin (input validation, locking,
  final publish).
- Never emit an empty `Steps` list and never omit the JSON block, however
  incomplete the supplied analysis feels. A step list with imperfect
  `LegacyProcedures` is recoverable; an absent one discards every per-step
  section and every per-step check.
```

첫 규칙은 요구를 **암기에서 선택으로** 바꾼다. 명단이 프롬프트에 있으므로 지킬 수
있는 규칙이 된다.

둘째 규칙이 이번 회귀의 직접적 방지책이다. codex는 "추정하면 규칙 위반"이라 판단해
거부를 택했다. 거부가 더 비싸다는 사실을 알려주지 않았기 때문이다.

---

## 설계 2 — 분할 실패 가시화

### 2.1 분할 미실행 배너

`adoptedSteps == null`이면 커버리지 검사와 하한 검사가 모두 건너뛰어지는데, 지금은
경고 로그 두 줄만 남고 문서에는 아무 흔적이 없다. Proc7이 배너 0개에 92점으로 나온
이유다.

`VerificationBanner.SplitGenerationSkipped()`를 추가한다.

> **[분할 미실행] 목차가 유효한 단계 목록을 내지 못해 문서가 단일 호출로
> 생성되었습니다.** 단계별 섹션 생성과 단계별 하한 검사(대상 테이블·오류코드 대조)가
> 실행되지 않았습니다. 내용이 부실하다는 뜻은 아니지만, 이 문서는 단계 단위 기계
> 검증을 받지 않았습니다.

기존 배너들과 같은 계약이다 — `VerificationOutcome`을 바꾸지 않고 가시성만 확보한다.

목차가 유효한 목록을 못 낸 사유(JSON 블록 없음, 0단계, 상한 초과, 파싱 실패)는
구분하지 않는다. 운영상 결과가 같고, 사유는 이미 경고 로그에 남는다.

### 2.2 문서 전체 오류코드 대조

이 검사의 미덕은 **목차가 필요 없다는 것**이다. `SpecReturnCodeExtractor`가 명세서에서
직접 뽑으므로 목차가 어떻게 망가지든 살아남는 유일한 검사다. 재료는 이미
`VerificationPipelineOrchestrator.cs:1760`에서 원본 `specs`로 계산되어 있다.

```csharp
// MechanicalValidator
// 반환: 프로시저명 → 문서에 없는 코드 목록. 전부 있으면 빈 사전.
public static IReadOnlyDictionary<string, IReadOnlyList<string>> FindMissingErrorCodes(
    string documentMarkdown,
    IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure);

// VerificationBanner
public static string MissingErrorCodes(
    IReadOnlyDictionary<string, IReadOnlyList<string>> missingByProcedure,
    IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure);
```

입력 `codesByProcedure`는 `SpecReturnCodeExtractor.Extract`의 반환값을 그대로 쓴다.
배너가 원본 사전도 받는 이유는 "16개 중 9개"라는 분모를 보이기 위해서다 — 분자만
보이면 읽는 사람이 심각도를 가늠할 수 없다.

매칭은 기존 `ContainsToken`을 그대로 쓴다(`private` → `internal`). 두 경로가 같은
규칙을 써야 결과가 어긋나지 않는다. 이 정규식은 `-1`이 `-10` 안에서 오탐되지 않도록
단어 경계를 본다.

Proc7이라면 이렇게 나왔을 것이다.

```
UP_UTIL_SETTLE_EXCEPTION_PROC  16개 중 9개 누락: -201, -102, -101, …
UP_UTIL_SETTLE_COMM_UPD        16개 중 6개 누락: -23, -22, -20, …
UP_UTIL_SETTLE_EXPECT_PROC     11개 중 5개 누락: -17, -15, -13, …
```

**실행 시점은 분할 여부와 무관하게 항상이다.** 폴백 경로에만 걸면 Proc6(분할은 됐으나
32단계가 코드 대조를 건너뛴 경우)을 놓친다. 두 사고를 모두 잡는 유일한 배치가
"항상"이고, 비용은 문자열 검색 한 번이다.

단계별 검사와 중복되지 않는다. 저건 "이 코드가 *제 섹션에* 있는가"를 묻고, 이건
"이 코드가 *문서 어디에도* 없는가"를 묻는다. 후자에 걸리면 조건 없이 진짜 누락이다.

---

## 테스트 전략

### 설계 1

| 대상 | 확인할 것 |
|---|---|
| `AiService` | 명단이 사용자 프롬프트에 그대로 실린다 |
| `AiService` | 새 규칙 두 문장이 시스템 프롬프트에 있다 (문구를 상수 보간으로 고정) |
| 오케스트레이터 | 최초 수립 호출이 **원본 `specs`**의 `FileName`을 넘긴다 |
| 오케스트레이터 | **재시도 회차**에서도 `Feedback_Log.txt`가 명단에 섞이지 않는다 |
| 오케스트레이터 | 재수립 경로도 같은 명단을 넘긴다 |

네 번째가 이 설계에서 가장 중요한 회귀 테스트다. 실패해도 문서는 그럴듯하게
나오므로 사람 눈으로는 잡히지 않는다.

### 설계 2

| 대상 | 확인할 것 |
|---|---|
| `VerificationBanner` | 분할 미실행 배너가 "검증을 받지 않았다"를 말하고 부실을 단정하지 않는다 |
| `VerificationBanner` | 누락 코드가 프로시저별로 묶여 나온다 |
| `MechanicalValidator` | `-1`이 `-10` 안에서 오탐되지 않는다 |
| `MechanicalValidator` | 문서에 모든 코드가 있으면 빈 결과 |
| 오케스트레이터 | 0단계 목차 → 분할 미실행 배너가 붙는다 |
| 오케스트레이터 | 분할이 정상 실행됐을 때는 그 배너가 **붙지 않는다** |
| 오케스트레이터 | 분할 성공 회차에서도 오류코드 대조가 실행된다 |

부재 확인 테스트(6번)를 빠뜨리면 조건이 뒤집혀 배너가 늘 붙는 사고를 못 잡는다.

## 검증 방법

단위 테스트로는 "프롬프트에 명단이 실렸다"까지만 증명된다. 모델이 실제로 그것을
쓰는지는 실행으로만 확인된다. 구현 후 POQSettleProc8로 재실행해 다음을 본다.

- `raw/PlanStructure.md`의 `LegacyProcedures`가 채워졌는가
- 그 결과 `ErrorCodes`·`TargetTables`가 보강됐는가
- 커버리지 배너가 사라졌는가(오탐이 아니라 진짜로 커버됐으므로)

## 위험

**모델이 명단을 받고도 비울 수 있다.** 그때는 설계 2의 배너가 그 사실을 드러낸다 —
설계 1이 실패해도 설계 2는 독립적으로 동작한다. 두 설계를 함께 넣는 이유다.

**명단이 길면 프롬프트가 커진다.** 실측 최대 14개 항목이라 무시할 수준이다.

## 남은 후속

- 신뢰도 점수와 검증 커버리지의 분리 표기 (Proc7의 92점 문제)
- POQSettleProc7 산출물 폐기 판단
