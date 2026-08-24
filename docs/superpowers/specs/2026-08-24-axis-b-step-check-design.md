# 축 B 단계 검사 — 명세서 기계 확정 사실을 단계 지시서와 대조한다

2026-08-24. POQSettleBatch1 축 B 재감사에서 나온 46건 중 **🔴 2건 · 🟠 7건**을 닫는다.

## 배경 — 왜 46건이 나왔나

축 B의 산출물은 `output/Jobs/[job]/agent/steps/SNN.md`이고, 기준값은 그 단계가 흡수한 SP의
`Spec.md`다. 생성은 `AiService.GenerateBatchStepSectionAsync`가 하고, 검사는
`MechanicalValidator.ValidateBatchStep`이 한다. 검사가 떨어지면
`StepValidationResult.SuggestedPromptFix` → `floorFeedback` → 다음 시도 프롬프트로 시정 지시가 실린다.
**이 순환은 이미 깔려 있다.**

문제는 검사가 받는 재료다. `ValidateBatchStep`이 기준값으로 받는 것은

- 목차(`BatchStepPlan.TargetTables`, `ErrorCodes`)
- `SpecConditions` — 명세서 본문에서 뽑은 **조건 컬럼 이름 목록**

둘뿐이다. 명세서의 기계 확정 표(`### DML 범위`, `### 집합 술어`, `### 지역 변수 및 시스템 값`,
`### UPDATE 대상 테이블: … (갱신 N)`)는 단계 검사에 **전달되지 않는다**.

게다가 `CheckMissingConditionColumns`의 대조는 "문서 어딘가에 이 컬럼 이름이 있는가"다.
S07처럼 `YMD`가 42곳에 흩어진 문서는 갱신 13의 최상위 WHERE에서 `YMD`가 통째로 빠져도 통과한다.

즉 46건의 근원은 하나다 — **명세서가 기계로 확정한 사실이 단계 검사에 들어가지 않고,
들어간 것마저 문장 단위가 아니라 문서 단위로 대조된다.**

## 목표 — 닫을 9건

| 등급 | 단계 | 결함 | 닫는 수단 |
|---|---|---|---|
| 🔴 | S07 | 갱신 4~11·14·15의 UPDATE 본문 전량 누락(18개 중 10개가 주석 한 줄) | 검사 A |
| 🔴 | S14 | 지역 변수 9개가 `DECLARE` 없이 쓰임(금액 3종의 `MONEY` 타입 소실) | 검사 D |
| 🟠 | S07 | 명세서에 없는 `HAVING SUM(TxAmt) = 0` 집계를 원본 로직으로 서술 | 검사 C |
| 🟠 | S07 | 갱신 13의 최상위 WHERE(`Y.YMD`, `Y.PGNAME`) 누락 | 검사 B |
| 🟠 | S09 | `-9` 사전 검증 EXISTS에 `SM.TxAmt = 0` 추가 | 검사 C |
| 🟠 | S09 | `UIF_SettleYMD` 스칼라 하위질의 → `CROSS APPLY` 이관 | 규약 ① |
| 🟠 | S11 | 갱신 9 `TPLCardEDIMst` 조인 키 `YMD`·`UseState` 누락 | 검사 B |
| 🟠 | S13 | 상태 변수 초기값 `0`이 성공 코드와 겹쳐 실패가 성공으로 보고됨 | 검사 E |
| 🟠 | S14 | 비집계 2단계 조회를 `MAX(ID)` 한 문장으로 통합해 분기 역전 | 규약 ② |

## 비목표

- **SET 산식 대조(S08 🟡 4건)를 넣지 않는다.** 갱신 절의 `원천 표현식 (SET)`은 전문이라
  문자열 대조가 곧바로 오탐이 된다. 별도 회차의 문제다.
- **축 B 재감사를 다시 돌리지 않는다.** 완료 기준은 이 9건의 소멸 실측이다.
- **원본 `.sql` DDL을 읽지 않는다.** 축 B의 기준값은 `Spec.md`뿐이라는 원칙을 유지한다.

## 설계 §1 — 재료를 나르는 통로

새 통로를 만들지 않는다. `VerificationPipelineOrchestrator`가 이미 재시도 루프 **밖에서 한 번**
명세서 재료를 만든다:

```csharp
var conditionColumns = MergeSpecMaterials(
    SpecConditionColumnExtractor.Extract(specs),
    SpecRoundingShapeExtractor.Extract(specs));
```

같은 자리에 `SpecStatementFactsExtractor.Extract(specs)`를 더한다. SP 이름별로 이것을 낸다.

```csharp
public sealed record SpecStatementFacts(
    IReadOnlyList<SpecDmlRow> DmlRows,          // ### DML 범위 표의 행
    IReadOnlyList<SpecSetTarget> SetTargets,    // ### UPDATE 대상 테이블: … (갱신 N) 절
    IReadOnlyList<SpecLocalVariable> LocalVariables);

public sealed record SpecDmlRow(
    string Kind,                                 // UPDATE / INSERT / DELETE / SELECT
    int Ordinal,                                 // "UPDATE 13"의 13
    int SourceLine,                              // 원본 DDL 라인 (대조에 쓰지 않고 메시지에만)
    string TargetTable,
    IReadOnlyList<string> PredicateColumns,      // 최상위 WHERE 술어 컬럼 칸
    IReadOnlyList<string> JoinKeys,
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<string> OrderBy);

public sealed record SpecSetTarget(int Ordinal, string TargetTable, IReadOnlyList<string> Columns);

public sealed record SpecLocalVariable(string Name, string TypeOrKind, bool IsSystemValue);
```

`ValidateBatchStep`은 인자 하나를 더 받는다:
`IReadOnlyDictionary<string, SpecStatementFacts> statementFactsByProcedure`.

파싱은 기존 마크다운 헬퍼를 쓴다 — `MarkdownSectionLocator`로 절을 찾고
`MarkdownTableCellCodec.SplitRow`로 칸을 가른다. 헤더 이름으로 열을 찾는다(열 순서에 기대지 않는다).
`(없음)`·`—`는 빈 목록으로 읽는다.

## 설계 §2 — 단계 SQL의 문장을 "갱신 N"에 붙이는 방법

**순서 대응은 쓰지 않는다.** k번째 UPDATE ↔ 갱신 k로 붙이면 단계가 문장 하나를 빼먹는 순간
이후가 전부 어긋나 오탐이 쏟아진다. S07이 정확히 10개를 빼먹은 문서다.

**3단 폴백을 쓴다.**

1. **개수 대조 (앵커 불필요)** — DML 범위 표의 `(문장 종류 × 대상 테이블)`별 행 수와
   단계 SQL의 같은 조합 문장 수를 견준다. **부족할 때만** 오류다. 초과는 침묵한다 —
   단계는 배치 제어 테이블에 정당하게 더 쓴다.
2. **앵커 대조** — 단계 SQL 주석의 갱신 번호를 앵커로 삼는다. `U4` · `갱신 4` · `UPDATE 4`
   세 표기를 인정한다(S07이 이미 `/* U4: … */`를 쓴다). 앵커가 달린 문장만 그 행과 대조한다.
3. **앵커 없음** — 앵커가 하나도 없는 단계에는 "갱신 번호를 주석으로 달라"는 요구를
   **1건만** 낸다. 문장별 오류를 쏟지 않는다.

앵커가 붙은 문장에만 2단을 적용하는 것이 핵심이다. 검사가 단계 문서의 서술 자유도를 건드리지
않으면서, 앵커를 단 문장은 정확히 대조된다. 앵커를 다는 일은 3단이 프롬프트로 유도한다.

문장 추출 재료는 `CleanedSqlFences`가 내는 **펜스별 사본**이다. 문서 전체를 한 번에 지우면
산문의 짝 없는 아포스트로피 하나가 뒤따르는 펜스를 통째로 공백으로 만들어 검사를 조용히 꺼버린다
(`CheckFirstStepRowCreation` 주석에 기록된 실측).

**문장과 술어 컬럼은 ScriptDom으로 뽑는다.** 정규식으로 `UPDATE`를 세면 문자열 리터럴 안의
단어와 주석에 적힌 예시가 함께 잡힌다. 펜스 사본을 `TSql160Parser`로 파싱하고(프로젝트가
`SqlStaticParser`에서 이미 쓰는 방식) 방문자로 `UpdateSpecification`·`InsertSpecification`·
`DeleteSpecification`을 모은다. 술어 컬럼은 **최상위 `WhereClause`와 `FromClause`의 조인 `ON`절**에서
`ColumnReferenceExpression`만 모은다 — 스칼라 하위질의 안쪽은 세지 않는다(명세서의 술어 컬럼 칸이
`최상위 WHERE 기준`이라고 못 박고 있다).

**파싱에 실패한 펜스는 침묵한다.** 단계 문서의 펜스에는 T-SQL이 아닌 것도 온다(C# 조각, 의사코드,
`text` 펜스). `Parse`가 오류를 내면 그 펜스는 대조 대상에서 빼고, 문서 전체가 그렇더라도
오류를 만들지 않는다 — 재료가 없다는 사실은 이미 다른 검사가 든다.

## 설계 §3 — 검사 5개

각 검사는 자기 `try/catch`를 가진다. 하나가 던져도 나머지가 죽지 않는다.

### A. `CheckStatementCountAgainstSpec`

- **입력**: `DmlRows`, 단계 SQL 펜스
- **판정**: `(Kind, TargetTable)`별로 명세서 행 수 > 단계 문장 수이면 오류
- **침묵**: 레거시 없는 단계 / DML 범위 표 없음 / 초과
- **메시지**: `{step} 섹션이 {table}에 대한 {kind}를 {n}개만 담고 있습니다. 명세서 DML 범위 표는 {m}개를 확정합니다({빠진 갱신 번호 열거}). 각 문장의 본문을 전문으로 실으십시오 — 주석이나 "원문 그대로 적용한다"는 지시는 상수·계수·반올림 자릿수를 복원하지 못합니다.`
- **닫는 것**: S07 🔴

### B. `CheckAnchoredStatementFacts`

- **입력**: `DmlRows`, 앵커가 달린 단계 문장
- **판정**: 그 행의 `JoinKeys`·`PredicateColumns` 중 문장에 없는 컬럼이 있으면 오류
- **대조 단위**: 컬럼 **이름**만. 값은 보지 않는다 — `UseState IN (0)` ↔ `UseState = 0` 같은
  동등 표현이 실측 미검출의 27%였고 그 전부가 오탐이었다(`CheckMissingConditionColumns` 실측)
- **침묵**: 앵커 없음 / 표 없음 / 레거시 없는 단계
- **닫는 것**: S07 🟠(U13 WHERE), S11 🟠(조인 키)

### C. `CheckAnchoredStatementExtras`

- **입력**: B와 같음
- **판정**: 앵커 문장의 술어에 등장하는 컬럼 중 그 행의 `PredicateColumns`·`JoinKeys`에 없는 것,
  그리고 `GroupBy` 칸이 비었는데 문장에 `GROUP BY`/`HAVING`이 있으면 오류
- **예외 목록**: `BatchControlContract`의 제어 컬럼(`RunId`·`StepCode`·`BatchYmd`·`RunStatus`·
  `StepStatus` 등)과 청크 처리 키. 단계가 정당하게 더하는 조건이다
- **침묵**: B와 같음
- **닫는 것**: S07 🟠(`HAVING SUM(TxAmt)=0`), S09 🟠(`TxAmt = 0`)

### D. `CheckSpecLocalVariablesDeclared`

- **입력**: `LocalVariables`, 단계 SQL 펜스 전체
- **판정**: `IsSystemValue`가 아닌 변수가 단계 SQL에 **쓰이는데** `DECLARE`가 없으면 오류
- **`IsSystemValue`**: 지역 변수 표의 `데이터 타입 또는 구분` 칸이 `SQL Server 시스템 값`이면 참
  (`@@ERROR`·`@@ROWCOUNT`가 여기 걸린다)
- **메시지에 타입을 싣는다** — S14에서 소실된 것이 `MONEY`라는 사실이 시정 지시에 들어가야 한다
- **침묵**: 표 없음 / 레거시 없는 단계 / 변수가 단계에 아예 안 쓰임
- **닫는 것**: S14 🔴

### E. `CheckStepIdInitialValue`

- **입력**: 목차 `ErrorCodes`, 단계 SQL 펜스
- **판정**: `CATCH`가 `SET @po_intRetVal = @v_…`로 돌려주는 상태 변수를 찾고, 그 변수의
  `DECLARE … = <리터럴>` 초기값이 `ErrorCodes` 집합(또는 성공 코드 `0`)에 들어 있으면 오류
- **침묵**: 그 구조가 아닌 단계 / `ErrorCodes` 비어 있음
- **닫는 것**: S13 🟠(`= 0`). 덤으로 S05 🟡(`= -9`)도 잡는다

## 설계 §4 — 규약 2조항

`common/01-step-contract.md`(단계 프롬프트에 실리는 공통 규약)에 넣는다.

- **① 스칼라 하위질의를 `CROSS APPLY`/`OUTER APPLY`로 바꾸지 않는다.** 명세서가 대입 우변을
  스칼라 하위질의로 적은 자리는 무결과일 때 `NULL`이 대입되는 자리다. `CROSS APPLY`는 그 행을
  갱신 대상에서 통째로 제외한다 — 같은 문장의 다른 컬럼 대입까지 사라진다(S09 실측).
  느슨하게 바꿔야 할 이유가 있으면 그 사실을 단계 본문에 적는다.
- **② 비집계 조회 여러 문장을 집계 한 문장으로 합치지 않는다.** 명세서가
  `SELECT @v = col` 뒤에 `@@ROWCOUNT > 1` 분기를 둔 자리는 "없음"과 "여럿"을 가르는 자리다.
  `MAX(col)` 한 문장으로 합치면 "없음"의 표현이 `0`에서 `NULL`로 바뀌어 분기가 역전된다(S14 실측).

## 설계 §5 — 오탐 억제

- 레거시 출신이 없는 **신설 단계는 전 검사 침묵**한다(S01·S02·S03·S16). 물려받을 원본이 없다.
- 대조 재료가 없으면 침묵한다. 재료가 없다는 사실 자체는 이미 `PlanDefects`가 든다.
- 컬럼 이름 대조는 대소문자를 무시한다. 명세서는 `USESTATE`, 단계는 `UseState`로 쓴다.
- 별칭은 제거하고 뒷마디만 본다 — `Y.YMD` ↔ `YMD`.

## 설계 §6 — 코퍼스 스윕

- 재료: `output/Jobs/*/agent/steps/*.md` — Job 20개 · 단계 파일 326개
- 기존 스윕 하네스에 `stepl1` 모드를 더한다
- **통과 기준**
  1. 이번 9건 자리가 전부 잡힌다(POQSettleBatch1의 S07·S09·S11·S13·S14)
  2. 나머지 검출을 표본으로 전건 확인해 **거짓 양성 0**
- A·D가 326개에서 대량 검출될 수 있다. 그것은 오탐이 아니라 **축 B 산출물 전반의 실제 상태**다.
  그 사실을 그대로 보고하고, 재생성 부담은 §7에서 다시 판단한다.

## 설계 §7 — 재생성 실측

1. `VerificationPipelineOrchestrator`의 `maxTries`를 **2 → 5**로 올린다. 검사가 5개 늘었는데
   시도가 2회면 첫 시도에서 2건 이상 걸린 단계가 하한 미달로 확정된다(축 A는 6회다).
2. POQSettleBatch1 번들을 재생성한다.
3. S07·S09·S11·S13·S14 다섯 단계에서 위 9건이 사라졌는지 **직접 확인한다**.
4. 축 B 재감사는 돌리지 않는다.

## 미확정 사항 — 구현 첫 단계에서 확인한다

- **번들 캐시가 명세서 캐시(현재 15)와 같은 축인가.** 프롬프트 바이트가 바뀌므로(3단의 앵커 요구,
  규약 2조항) 인상이 필요한데, 어느 버전을 올릴지는 `CacheManager`와 번들 생성 경로를 확인해야 한다.
  같은 축이면 16으로 올리고, 별도 축이면 그쪽을 올린다.

## 완료 기준

1. 검사 5개가 테스트와 함께 들어가고 전체 테스트가 통과한다(**건너뜀 0**).
2. 코퍼스 스윕에서 9건 자리가 전부 잡히고 거짓 양성 0.
3. 규약 2조항이 `common/01-step-contract.md`에 들어간다.
4. `maxTries = 5`.
5. POQSettleBatch1 번들 재생성 후 9건이 사라진 것을 실측한다.
6. `docs/architecture.md`·`docs/known-defects.md`·`docs/audit-defect-catalog.md`를 갱신한다.
