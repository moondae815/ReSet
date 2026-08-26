# 표 블록 수집의 코퍼스 영향 측정 (2026-08-26)

L1 기계 검증기(`MechanicalValidator`)의 표 대조 검사들은 헤딩 절 전체에서 `|`로 시작하는
줄을 **블록 구분 없이** 한 덩어리로 모은다. 같은 절에 정당한 표가 둘 이상 있으면 인접 표의
행이 대조 대상에 섞여, 진짜 결손이 우연한 토큰 일치로 가려질 수 있다(거짓 음성).

이 문서는 그 모양이 **몇 개의 검사에 있고, 실물 코퍼스에서 실제로 몇 건의 판정을
바꾸는지**를 잰 관측 기록이다. 프로덕션 코드는 바꾸지 않았다.

측정 도구는 임시 프로브(`tests/ReSet.Core.Tests/TempTableBlockProbe.cs`)와
`MechanicalValidator.LocateHeadingSection`을 잠시 열어 둔 통로였고, **둘 다 측정 후
되돌렸다.** 이 커밋에는 이 보고서만 들어 있다.

## 0. 측정 조건

- 코퍼스 심링크 둘을 걸고 실행했다(`output`, `output.bak-2026-08-22`).
  프로브는 **건너뜀 0**으로 돌았다 — 두 저장 경로에서 각각 `Spec.md` 31건, 합 62건을 읽었다.
- 2단계 사실 대조는 `raw/metadata.json`의 DDL 원문에 추출기를 다시 돌려 얻은
  **실제 기대 사실**로 했다. 합성 픽스처가 아니다.
- 대조 술어는 프로덕션 코드를 그대로 베꼈다(`MarkdownTableCellCodec.SplitRow` +
  `cells.Any(c => c == 기대값)`).

## 1. 좌표표 — 순진한 수집이 있는 자리

브리프의 기대 목록은 **13개 이름**이었고(브리프 본문은 "14개 검사"라고 쓴다),
`MechanicalValidator.cs`에서 `StartsWith("|"`는 **15군데**에 있다. 두 메서드가 두 군데씩
쓰기 때문이다(`CheckParameterColumnClaims` 2491·2493, `CheckParameterTableRows` 2594·2602).
**검사 이름 13개 = 목록과 일치**한다. "14"는 자리 수를 센 것으로 보이며, 이 보고서는
이름 13개를 기준으로 쓴다.

| 검사 (줄) | 절 앵커 | 추출기 | 프롬프트 렌더 지점 (`AiService.cs`) | 수집 모양 |
| :--- | :--- | :--- | :--- | :--- |
| `CheckInsertMappingTableNames` (2186) | `InsertHeadingPrefix` + 테이블명 (`### INSERT 대상 테이블: X`) | 없음 (헤딩이 테이블명을 물어 상수 하나로 안 묶임) | 없음 (기계 확정 표가 아님 — 모델이 쓴다) | 절 전체 평면 수집 |
| `CheckParameterColumnClaims` (2460) | `## 개요` · `## 파라미터 목록` (`MarkdownSectionLocator.LocateSection`) | 없음 | 없음 | **이미 블록 인식** (블록별 `rows[0]`에서 헤더 칸 탐색) |
| `CheckParameterTableRows` (2578) | `## 파라미터 목록` | 없음 | 없음 | **이미 블록 인식** (첫 블록만, 비-`\|` 줄에서 `break`) |
| `CheckDmlScopeTable` (3390) | `DmlScopeExtractor.DmlScopeTableHeading` (`LocateDmlScopeSection` 3525) | `DmlScopeExtractor` | 859 (헤더 행 860) | 절 전체 평면 수집 |
| `CheckSetPredicates` (3680) | `DmlScopeExtractor.SetPredicateTableHeading` (`LocateSetPredicateSection` 4183) | `DmlScopeExtractor` | 1079 (헤더 행 1080) | 절 전체 평면 수집 |
| `CheckReferencedFunctionsCore` (4066) | `DmlScopeExtractor.ReferencedFunctionTableHeading` (`LocateHeadingSection` 4105) | `DmlScopeExtractor` | 1127 (헤더 행 1128) | 절 전체 평면 수집 |
| `CheckLockHints` (4245) | `DmlScopeExtractor.LockHintTableHeading` (`LocateLockHintSection` 4313) | `DmlScopeExtractor` | 1189 (헤더 행 1190) | 절 전체 평면 수집 |
| `CheckExecutionSemantics` (4487) | `ExecutionSemanticsFacts.TableHeading` | `ExecutionSemanticsFacts` | 1221 (헤더 행 1222) | 절 전체 평면 수집 |
| `CheckCaseBranches` (4564) | `CaseBranchExtractor.TableHeading` | `CaseBranchExtractor` | 1246 (헤더 행 1247) | 절 전체 평면 수집 |
| `CheckTransactionBoundaries` (4643) | `TransactionBoundaryExtractor.TableHeading` | `TransactionBoundaryExtractor` | 1272 (헤더 행 1273) | 절 전체 평면 수집 |
| `CheckSetAssignments` (4723) | `SetAssignmentExtractor.TableHeading` | `SetAssignmentExtractor` | 1297 (헤더 행 1298) | 절 전체 평면 수집 |
| `CheckErrorCodes` (4806) | `DmlScopeExtractor.ErrorCodeTableHeading` | `DmlScopeExtractor` | 1322 (헤더 행 1323) | 절 전체 평면 수집 |
| `ReportTableShapeBreaks` (4965) | 호출부가 준다 — `MachineConfirmedTables.All`의 11개 헤딩(`CheckMachineTableShape` 4891) + `InsertHeadingPrefix` 절(`CheckInsertMappingTableShape` 4922) | 카탈로그 | 표마다 다름 | **이미 블록 인식** (빈 줄을 경계로 쪼갬, 4968-4982) |

세 줄이 표의 결론이다.

- **9개가 순진한 평면 수집**이다: `CheckDmlScopeTable`, `CheckSetPredicates`,
  `CheckReferencedFunctionsCore`, `CheckLockHints`, `CheckExecutionSemantics`,
  `CheckCaseBranches`, `CheckTransactionBoundaries`, `CheckSetAssignments`, `CheckErrorCodes`.
- **3개는 이미 블록을 안다.** 특히 `ReportTableShapeBreaks`는 2026-08-22에 바로 이 병합
  때문에 거짓 양성 10건(9개 객체)을 내고 고쳐진 자리다 — 그 수정이 나머지 검사로 옮겨지지
  않았을 뿐, 저장소 안에 이미 참조 구현이 있다.
- **`CheckInsertMappingTableNames`는 부류가 다르다.** 평면 수집이지만 행마다 `cells[1]`의
  테이블명을 보고 *대소문자만 다른* 경우에만 발화한다. 인접 블록이 섞이면 거짓 **양성**
  쪽으로 틀리지 거짓 음성이 아니다. 이 물결의 문제와 실패 방향이 반대다.

## 2. 1단계 수치 — 절 안에 표가 둘 이상인가

프로브가 헤딩 절을 찾아 빈 줄(정확히는 비-`|` 줄)을 경계로 블록을 쪼개 센 결과다.
"좁힘 인식 실패"는 **헤더 칸이 전부 든 블록이 절 안에 하나도 없었다**는 뜻이고,
"관대 전용 행"은 평면 수집에는 들어가지만 좁힘에는 안 들어가는 행이 있었다는 뜻이다.

### 2-1. 현행 코퍼스 `output/` (Spec.md 31건)

| 검사 | 절 발견 | 블록 2개 이상 | 좁힘 인식 실패 | 관대 전용 행 있는 문서 |
| :--- | ---: | ---: | ---: | ---: |
| `CheckExecutionSemantics` | 31 | **2** | 0 | 2 |
| `CheckSetAssignments` | 27 | **1** | 0 | 1 |
| `CheckDmlScopeTable` | 28 | 0 | 0 | 0 |
| `CheckSetPredicates` | 28 | 0 | 0 | 0 |
| `CheckLockHints` | 29 | 0 | 0 | 0 |
| `CheckCaseBranches` | 16 | 0 | 0 | 0 |
| `CheckTransactionBoundaries` | **12** | **0** | **0** | **0** |
| `CheckReferencedFunctionsCore` | 8 | 0 | 0 | 0 |
| `CheckErrorCodes` | **0** | 0 | 0 | 0 |
| (참고) 객체 선언 — 형태 검사만 | 17 | 2 | 0 | 2 |
| (참고) 파생 테이블 정의 — 형태 검사만 | 8 | 0 | 0 | 0 |

`CheckErrorCodes`의 절 발견 0건은 오측이 아니라 관측이다 — `### 오류 코드 (기계 확정 —
수정 금지)` 헤딩이 현행 코퍼스 31개 문서 어디에도 없다. 이 검사는 코퍼스에서 한 번도
돌아 본 적이 없다.

### 2-2. 과거 코퍼스 `output.bak-2026-08-22` (Spec.md 31건)

| 검사 | 절 발견 | 블록 2개 이상 | 좁힘 인식 실패 | 관대 전용 행 있는 문서 |
| :--- | ---: | ---: | ---: | ---: |
| `CheckDmlScopeTable` | 15 | 1 | **15** | 15 |
| `CheckSetPredicates` | 12 | 0 | **12** | 12 |
| `CheckLockHints` | 13 | 0 | 0 | 0 |
| `CheckReferencedFunctionsCore` | 6 | 0 | 0 | 0 |
| `CheckTransactionBoundaries` · `CheckSetAssignments` · `CheckCaseBranches` · `CheckExecutionSemantics` · `CheckErrorCodes` | 0 | 0 | 0 | 0 |
| (참고) 객체 선언 | 17 | 3 | 0 | 3 |
| (참고) 파생 테이블 정의 | 8 | 2 | 0 | 2 |

**이 27건은 이 물결이 지나갈 때 반드시 읽어야 할 경고다.** 과거 산출물의 헤더 행이
지금 렌더러의 헤더와 다르다.

- `DML 범위` 옛 헤더: `| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼(…) | 기준일 파라미터 적용(…) | 조인 키 | ORDER BY |`
  — 현행 헤더에 있는 **`GROUP BY` 칸이 없다**.
- `집합 술어` 옛 헤더: `| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 |`
  — 현행 헤더에 있는 **`술어 원문` 칸이 없다**.

헤더 칸 전부를 요구하는 좁힘은 이 문서들에서 **블록을 하나도 못 찾는다.** 그러면 그 표의
모든 기대 사실이 "행이 없습니다"로 발화된다 — 표는 멀쩡히 옮겨져 있는데도. 헤더 리터럴이
바뀌는 날 좁힘은 **문서 전체 단위로 거짓 양성**을 낸다는 것이 여기서 실측됐다.

## 3. 2단계 사실 대조 — 판정이 실제로 갈리는가

`raw/metadata.json`의 DDL에 추출기를 돌려 얻은 실제 기대 사실로, 같은 문서에 대해
관대 수집과 좁힘 수집의 판정을 하나씩 견줬다. 대상은 추출기가 값싸게 재현되는 세 검사다.

| 검사 (`output/`) | 절 있는 문서 [^3-1] | 대조한 기대 사실 | **(가)** | **(나)** |
| :--- | ---: | ---: | ---: | ---: |
| `CheckCaseBranches` | 12 | 175 | 0 | 0 |
| `CheckSetAssignments` | 20 | 119 | 0 | 0 |
| `CheckTransactionBoundaries` | 12 | **105** | **0** | **0** |
| 합계 | — | **399** | **0** | **0** |

[^3-1]: **이 칸은 §2-1의 "절 발견"보다 작다 — 사실 대조가 절 있는 문서 전부를 덮지
    않았다.** `CheckSetAssignments`는 27 대 20, `CheckCaseBranches`는 16 대 12다.
    원인을 규명했다: 2단계 루프가 `<저장소>/Procedures`와 `<저장소>/Functions` 두
    디렉터리만 돌아, `output/External/SETTLE_CARD_DB/Functions/` 아래 7개 객체가
    통째로 빠졌다. 실측으로 확인한 차이다 — 변수 대입 절을 가진 27개 문서 중 7개가
    `External/` 아래이고(27 − 7 = 20), CASE 분기 절을 가진 16개 중 4개가 그렇다
    (16 − 4 = 12). 그 7개 객체에는 `raw/metadata.json`이 모두 있으므로 재료가 없어서가
    아니라 **경로 열거를 좁게 짠 내 실수**다.
    **게이트 값에는 영향이 없다** — 트랜잭션 경계 절을 가진 12개 문서 중 `External/`
    아래는 0개이므로 `CheckTransactionBoundaries`는 12 대 12로 절 있는 문서를 전부
    덮었다. 나머지 두 검사의 (가)·(나) 0건은 `External/` 11개 문서-검사 짝을 뺀
    범위에서의 값이다.

**사례 목록: 0건.** 399개 기대 사실 가운데 `관대=present, 좁힘=absent`인 것이 하나도 없었다.

(트랜잭션 경계 105건은 `MachineTableExpansionCorpusTests`가 남긴 "백로그 예측: 트랜잭션 105"와
정확히 맞는다 — 재료를 옳게 다시 뽑았다는 교차 확인이다.)

### 3-1. 관대 전용 행이 실제로 있었던 세 자리 — 그런데 왜 판정이 안 갈렸나

블록이 둘 이상인 절은 현행 코퍼스에 셋뿐이었고, 셋 다 **어휘가 겹치지 않아** 우연한
매칭이 성립하지 않았다.

1. `output/Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md:60-74` —
   `### 실행 의미` 절 안에 정당한 CRUD 요약 표(`| 작업 | 대상 | 분석 결과 |`)가 빈 줄로
   구분돼 이어 붙어 있다. 평면 수집은 그 8행을 함께 모은다. 그러나 실행 의미 행 키의 첫
   칸(`종류`)은 `ExecutionSemanticsFacts.AllKinds`(`DB 배치`·`집계 대입`·`@@ROWCOUNT`·
   `커서 수명`·`식 타입 경로`·`비집계 대입`·`루프 내 재설정`) 중 하나여야 하는데, 둘째 표의
   값은 `조회`·`삽입`·`수정`·`삭제`… 로 그 목록과 하나도 겹치지 않는다.
2. `output/Functions/dbo.UF_GET_INCVTAXRATE/docs/Spec.md` — 같은 모양, 같은 이유.
3. `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md:349-375` —
   `### 변수 대입` 절 안에 오류 코드 서술 표(`| 출력 코드 | 발생 위치 | 사실 기반 의미 |`)가
   이어 붙어 있다. 변수 대입 행 키는 (라인, `@변수`, 대입식 원문) 셋을 **같은 행에서** 요구하는데,
   둘째 표에는 `@po_intRetVal` 셀이 아예 없다.

즉 **결함의 모양은 코퍼스에 실재하지만**(같은 절에 표가 둘 붙는 일이 실제로 일어난다),
오늘의 코퍼스에서는 그 결과로 가려진 결손이 하나도 없다. 열쇠 칸이 셋이라 우연 일치가
성립하기 어려운 것이 이유다.

## 4. 조건 (가)·(나) 적용 결과 — 이식 대상 후보

설계서 §6-1의 갈래 판정과 §3-4의 정지 조건("(나)가 한 건이라도 나온 검사는 이식하지 않는다")을
검사마다 적용한 결과다.

| 검사 | (가) | (나) | 판정 |
| :--- | ---: | ---: | :--- |
| `CheckTransactionBoundaries` | 0 | **0** | **이식 가능** — 정지 조건에 걸리지 않는다 |
| `CheckSetAssignments` | 0 | 0 | 이식 가능 (사실 대조 완료) |
| `CheckCaseBranches` | 0 | 0 | 이식 가능 (사실 대조 완료) |
| `CheckExecutionSemantics` | 0 | 0 | 이식 가능 — 단, 구조 대조만 했다(§5) |
| `CheckDmlScopeTable` | — | — | **보류** — 현행 코퍼스는 깨끗하나 과거 판 15건이 좁힘에서 인식 실패 |
| `CheckSetPredicates` | — | — | **보류** — 같은 이유, 과거 판 12건 |
| `CheckLockHints` | 0 | 0 | 구조상 무관(블록 2개 이상 0건) — 이식해도 판정이 안 바뀐다 |
| `CheckReferencedFunctionsCore` | 0 | 0 | 구조상 무관(블록 2개 이상 0건) |
| `CheckErrorCodes` | — | — | **측정 불가** — 코퍼스에 절이 0건. 근거 없이 이식하지 말 것 |
| `CheckInsertMappingTableNames` | — | — | **범위 밖** — 실패 방향이 반대(거짓 양성) |
| `CheckParameterColumnClaims` · `CheckParameterTableRows` · `ReportTableShapeBreaks` | — | — | **이미 블록 인식** — 할 일 없음 |

**Task 3의 정지 조건 답: `CheckTransactionBoundaries`의 갈래 (나)는 0건이다.**
현행 코퍼스 12개 문서 · 105개 기대 사실을 실제 재료로 대조해 나온 값이고, 좁힘이 블록을
못 찾은 문서도 0건이다. 정지 조건에 걸리지 않는다.

다만 (가)도 0건이므로 **이 이식은 오늘의 코퍼스에서 새로 잡는 결함이 없다.** 값어치는
"지금 잡히는 결함"이 아니라 "우연 일치가 성립하는 코퍼스가 왔을 때 안 뚫린다"는 예방에
있다. 그 값어치를 인정하고 진행하는 것은 타당하나, 관측되지 않은 이득을 관측된 이득처럼
적지는 말 것.

### 4-1. `DML 범위`·`집합 술어`를 보류로 두는 이유

이 둘은 현행 코퍼스에서는 (나) 0건이다. 그런데 **같은 저장소의 4일 전 산출물에서는 27건
전부가 인식 실패**였다 — 헤더 칸이 하나 늘었기 때문이다. 두 표는 이 배치에서 헤더가
실제로 움직인 유이한 표이기도 하다. (가)와 (나)를 개수로 견주지 않는다는 규칙을 그대로
적용하면, "오늘은 0건"이 아니라 "이 표의 헤더는 움직인다"가 판단 재료다. 이식하려면
헤더 칸 전부가 아니라 **불변 부분집합**(예: `문장`·`라인`)으로 블록을 인식하도록 좁힘
규칙 자체를 먼저 정해야 한다. 그것은 별건이다.

## 5. 재지 못한 것

프로브의 사각지대를 그대로 적는다. "영향 0"은 아래 범위 **밖에서는 주장하지 않는다.**

1. **사실 단위로 잰 것은 세 검사뿐이다.** `CheckTransactionBoundaries`,
   `CheckSetAssignments`, `CheckCaseBranches`만 실제 추출기 산물로 대조했다(399건).
   나머지 여섯(`CheckDmlScopeTable`, `CheckSetPredicates`, `CheckLockHints`,
   `CheckReferencedFunctionsCore`, `CheckExecutionSemantics`, `CheckErrorCodes`)은
   **구조 대조만** 했다 — "절 안에 블록이 둘 이상인가", "좁힘이 블록을 찾는가"까지다.
   블록이 하나뿐이면 두 수집이 같은 집합을 내므로 판정이 갈릴 수 없다는 논증으로
   (가)·(나) 0을 말한 것이지, 기대 사실 하나하나를 돌려 본 것이 아니다.
   그 세 검사도 절 있는 문서를 전부 덮지는 못했다 — `External/` 아래 객체가 2단계에서
   빠진 경위는 아래 3번과 §3의 각주 [^3-1]에 적었다.
2. **계획서가 지정한 코퍼스 경로는 문서 0건이었다 — 고쳐서 돌렸다.**
   Task 1 브리프의 프로브 코드는 `Path.Combine(root, "Jobs")` 아래에서 `Spec.md`를
   찾는다. 그 글롭(`output/Jobs/**/Spec.md`)에 걸리는 문서는 **0건**이다 —
   `output/Jobs/<잡이름>/` 아래에 있는 것은 `docs/BatchMigrationPlan.md`·
   `docs/Thinking.md`·`raw/PlanStructure.md`이지 `Spec.md`가 아니다. 이 물결이 재는
   기계 확정 표는 **명세서**에 있고, 명세서의 실제 위치는
   `output/{Procedures,Functions,External/<DB>/Functions}/<객체>/docs/Spec.md`다
   (현행 `output/` 31건, `output.bak-2026-08-22` 31건, 합 62건 — 그중
   `output/Procedures/*/docs/Spec.md`가 14건).
   **브리프 코드를 그대로 돌렸다면 프로브는 문서 0건을 순회하고, 건너뜀도 실패도 없이
   모든 검사에 "블록 2개 이상 0건"을 찍어 "영향 0"이라는 결론이 나왔을 것이다.**
   심링크를 걸었으므로 `Skip`도 걸리지 않았을 것이고, 코퍼스 없이 검증하는 것을
   막으려고 만든 `CorpusSkip`의 안전망도 이 실패 모양은 못 잡는다 — 코퍼스는 있는데
   글롭이 빈 경우이기 때문이다. 다음 태스크의 브리프가 이 글롭을 재사용하지 않도록
   여기 적어 둔다. **경로를 고쳐 62개 문서를 실제로 읽은 값이 이 보고서의 수치다.**
3. **2단계 사실 대조의 경로 열거가 `output/External/<DB>/Functions/`를 빠뜨렸다 —
   내 실수이고, 위 2번과 같은 부류다.** 2단계 루프를 `<저장소>/Procedures`와
   `<저장소>/Functions` 두 디렉터리만 돌게 짜서 `output/External/SETTLE_CARD_DB/Functions/`
   아래 **7개 객체**가 통째로 빠졌다. 그래서 §3 표의 "절 있는 문서" 칸이 §2-1의 "절 발견"보다
   작다 — `CheckSetAssignments` 27 대 20, `CheckCaseBranches` 16 대 12(각각 7개·4개가
   `External/` 아래다). 그 7개에는 `raw/metadata.json`이 모두 있으므로 재료가 없어서가
   아니다. **`CheckTransactionBoundaries`의 게이트 값에는 영향이 없다** — 트랜잭션 경계
   절을 가진 12개 문서 중 `External/` 아래가 0개라 12 대 12로 전부 덮었다. 나머지 두
   검사의 (가)·(나) 0건은 `External/` 11개 문서-검사 짝을 뺀 범위에서의 값이다.
   자세한 산수는 §3 각주 [^3-1]에 있다.

   두 실수(2번의 `Jobs/` 글롭, 이 항목의 2단계 열거)가 같은 부류이므로, **프로브가
   최종적으로 실제로 쓴 정정된 열거를 여기 그대로 적는다** — 다음 사람이 다시 유도하지
   않도록:

   ```text
   <저장소>/Procedures/<객체>/docs/Spec.md
   <저장소>/Functions/<객체>/docs/Spec.md
   <저장소>/External/<DB>/Functions/<객체>/docs/Spec.md
   ```

   여기서 `<저장소>`는 `output`과 `output.bak-2026-08-22` 둘이다. 1단계(구조 대조)는 이
   세 갈래를 모두 돌아 62건을 읽었고, 2단계(사실 대조)는 셋째 갈래를 빠뜨려 `External/`
   7개 객체만큼 좁게 돌았다 — **같은 열거를 두 단계가 공유하지 않은 것이 이 결함의
   기계적 원인**이다. 다음 프로브는 경로 열거를 헬퍼 하나로 뽑아 두 단계가 함께 쓸 것.
4. **`Spec.md`만 봤다.** `BatchMigrationPlan.md`·`PlanStructure.md` 등 다른 산출물은
   범위 밖이다. 이 검사들은 `Validate(markdown, expectations)` 경로에서 돌므로 Spec.md가
   주 소비자지만, `ValidateConsolidated`·`ValidateBatchStep` 경로는 확인하지 않았다.
5. **절 앵커를 못 찾은 문서는 이 측정에 들어오지 않는다.** 예를 들어 `CheckErrorCodes`는
   62개 문서 전부에서 절이 없어 한 줄도 재지 못했다. `CheckReferencedFunctionsCore`도
   현행 8건뿐이다 — 표본이 얇다.
6. **좁힘 규칙을 하나로 가정했다.** "블록의 첫 행에 헤더 칸이 **전부** 들어 있으면 그
   블록의 데이터 행을 쓴다"로 잡았다. Task 2가 실제로 도입할 `TableHeaderCells`의 판정
   규칙이 이와 다르면(부분집합 허용, 순서 요구 등) §2의 "좁힘 인식 실패" 수치가 달라진다.
   특히 §2-2의 27건은 **규칙 선택에 그대로 좌우되는 숫자**다.
7. **코퍼스가 62개 문서다.** 우연한 토큰 일치는 문서 수와 표 밀도에 비례해 나타난다.
   오늘 0건이라는 것이 규모가 커져도 0건이라는 뜻은 아니다 — 그것이 이 물결의 전제였다.
8. **거짓 음성의 "잠재량"은 안 쟀다.** 관대 수집이 지금 무관한 행을 얼마나 대조 대상에
   넣고 있는지(§3-1의 세 문서에서 각각 표 하나분 — 헤더·구분 행까지 10줄·10줄·12줄)는
   셌지만, 그 행들이 앞으로 어떤 사실과 우연히 맞을 수 있는지는 정량화하지 않았다.
