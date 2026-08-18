# 집합 술어 재료 설계

- 작성일: 2026-08-18
- 계기: [축 A 재감사 보고서](../../../output/Jobs/POQSettleProc16/consistency/ConsistencyReport-AxisA-2026-08-18.md) §4가 남긴 🟠 2건
- 선행 설계: [축 A 명세서 충실도](2026-08-17-axis-a-spec-fidelity-design.md) — 이 문서는 그 설계의 §0 지배 계약을 그대로 따른다

## 0. 지배 계약

추출기 하나가 사실을 내고 **프롬프트와 L1이 같은 사실을 소비한다.** 규칙만 있고
물리는 기계 검사가 없으면 그 규칙은 없는 것과 같다. 이 문서의 모든 절은 그 계약
아래에서 읽어야 한다.

## 1. 문제 규정

`UP_UTIL_SETTLE_EXPECT_PROC` 갱신 1(`object_definition.sql:39`)과
`UP_UTIL_SETTLE_COMM_UPD` 문장 2(`object_definition.sql:76-77`)는 대상 행 집합을
`IN`/`NOT IN` 리터럴 목록으로 정한다.

```sql
-- EXPECT_PROC:39
AND A.PGName NOT IN ('PLCard','SamSungPay','SSGPayCard','KakaoPay','KakaoCard','impaymobile','NaverCard','ApplePay','TossCardAuth')

-- COMM_UPD:77
AND A.PGNAME IN ('ALLTHEGATE','DACOMCARD','UNIONPAY','INICARD','TOSSCARD','NICECARD')
```

### 1.1 결함은 "리터럴이 없다"가 아니다

명세서를 실측하면 원소가 **부분적으로 존재한다.**

| SP | 원본 집합 | 명세서에 등장하는 원소 | 아예 없는 원소 |
|---|---|---|---|
| `EXPECT_PROC` 갱신 1 | 9 | 7 | `SSGPayCard`, `KakaoCard` |
| `COMM_UPD` 문장 2 | 6 | 3 | `DACOMCARD`, `INICARD`, `TOSSCARD` |

결함은 **"그 문장에 걸린 하나의 집합으로 제시되지 않는다"** 이다. 원소들은 다른
문장·다른 맥락에서 흩어져 등장하고, 이관자는 그 문장의 멤버십을 복원할 수 없다.
`EXPECT_PROC`의 `Spec.md:82`가 그 자리에 5개짜리 `@v_PLCardSettlePeriodPG`를
"원천 PG 목록"으로 정의해 그럴듯한 대체물을 놓은 것이 그 공백의 귀결이다.
갱신 1은 그 변수를 쓰지 않는다.

### 1.2 그래서 문서 전체 토큰 검색은 틀린 질문이다

"각 리터럴이 명세서 어딘가에 있는가"를 L1로 삼으면 `EXPECT_PROC`는 9개 중 7개로
통과하고, 그것도 **다른 문장에서의 우연한 등장** 덕분에 통과한다. 이 저장소는 같은
함정을 이미 한 번 밟았다 — `CheckHeaderContractContradiction`의 Fix Round 2가
"이 단어가 문서 어딘가에 있는가라는 질문 자체가 잘못됐다"로 판정 단위를 문서에서
문장으로 좁혔다(`MechanicalValidator.HeaderContractTerms` 주석).

**판정 단위는 표의 행이다.** 재료가 `(문장, 라인, 컬럼, 연산, 원소 목록)`을 한 행으로
확정하고, L1은 그 표 구간 안에서만 대조한다.

## 2. 범위 — 왜 집합 리터럴만인가

코퍼스 실측(SP 14개, `output/Objects/*/raw/object_definition.sql`):

| 후보 재료 | 부피 |
|---|---|
| `IN`/`NOT IN` 리터럴 목록 | 약 104건 (문자열 49 · 숫자 55) — SP당 평균 7 |
| 스칼라 리터럴 비교(`= 'x'`, `= 1`, `<> 1` …) | **474건** — `COMM_UPD` 하나가 100 |

스칼라까지 담으면 부피가 5배가 되고, 이것이 "값까지 대조하면 노이즈"라는 축 B의
기존 판단이 옳았던 지점이다(`SpecConditionColumnExtractor` 주석).

구조적 차이가 둘을 가른다. `INSTATE = 0`은 컬럼 이름만 봐도 "상태 필터가 있다"를
알고 원본을 열게 된다. 그러나 `PGName NOT IN (…)`은 **집합의 크기와 원소를 컬럼
이름으로 추측할 수 없다** — 실제로 명세서가 그 자리에 다른 집합을 채워 넣었고,
그것이 이번 실패 방식이다.

`COMM_UPD`의 🟠에 함께 적힌 `A.ABROADCHK = 1`은 스칼라라 이 재료에 담기지 않는다.
그 결함의 실체는 6개 PG 화이트리스트이고 `ABROADCHK`는 같은 행에 묶여 보고됐을
뿐이다. 같은 WHERE의 `A.INSTATE = 0`·`C.CollectFlag = 1`이 지적되지 않은 것도
같은 이유다 — 감사의 선별 기준은 구조가 아니라 "명세서가 빠뜨렸는가"였다.

**수집 범위는 DML 최상위 WHERE의 `IN` 전부다.** 갱신 대상 별칭인지 원천 별칭인지
가리지 않는다. 기존 `WHERE 최상위 술어 컬럼` 칸이 이미 같은 결정을 했고, 그 근거는
"대상 한정자만 남기도록 필터링하면 거짓 단언을 거짓 부재로 바꿀 뿐"이었다
(`AiService.BuildDmlScopeTableLines` 주석). 같은 논리를 이어받아 일관성을 지킨다.

## 3. 재료 추출기

### 3.1 "최상위"의 정의를 복제하지 않는다

"어디까지가 대상 범위를 정하는 술어인가"라는 지식은 `TopLevelPredicateCollector`
한 곳에 인코딩돼 있다 — 스칼라 서브쿼리와 `EXISTS`로는 내려가지 않고
`InPredicate.Expression`으로는 내려간다는 규칙이 그것이다. 새 추출기가 그 순회를
다시 구현하면 두 정의가 갈라지고, 그 순간 이 재료는 프롬프트가 말하는 "최상위"와
다른 것을 뜻하게 된다.

그래서 **수집기를 넓힌다.** `TopLevelPredicateCollector.ExplicitVisit(InPredicate)`가
이미 정확히 필요한 지점을 지나가므로(`Expression`과 `Values`를 둘 다 방문한다)
거기서 집합 사실을 함께 담고, `DmlScopeExtractor`에 두 번째 진입점을 둔다.

```csharp
public sealed record SetPredicateFact(
    string Operation,   // "UPDATE" | "DELETE" | "INSERT"
    int Line,           // 문장 시작 줄
    string Column,      // IN의 좌변 컬럼 이름
    bool IsNegated,     // NOT IN이면 true
    IReadOnlyList<string> Literals);  // 원문 그대로. 문자열은 따옴표 포함

public static IReadOnlyList<SetPredicateFact> ExtractSetPredicates(string? ddlText);
```

원소 수는 `Literals.Count`에서 나오므로 따로 담지 않는다. 순회는 두 번 돌지만
비용은 무시할 수준이고, "최상위"의 주인은 계속 한 곳이다.

`NOT IN` 판정은 `InPredicate.NotDefined`를 쓴다. ScriptDom 180.37.3 어셈블리에서
`NotDefined`·`get_Values`·`get_Subquery`가 실재함을 확인했다.

### 3.2 담지 않는 것 셋

- **`X IN (SELECT …)`** — `Subquery != null`로 구분한다. 집합이 리터럴이 아니므로
  옮겨 적을 목록 자체가 없다.
- **원소에 리터럴 아닌 것이 하나라도 섞인 경우** — `X IN ('a', B.Col)`을 리터럴
  집합으로 렌더하면 명세서에 **거짓 집합**이 실린다. 전부 리터럴일 때만 담는다.
- **스칼라 비교** — §2의 474건. 이번 실패 방식이 성립하지 않는다.

리터럴은 원문을 그대로 담는다 — `'PLCard'`처럼 따옴표까지. 파생 테이블 정의 표가
표현식 원문을 그대로 싣는 것과 같은 이유이고, 표에서 문자열과 숫자를 구분할 수 있게
한다.

## 4. 프롬프트

헤딩 상수 `SetPredicateTableHeading`이 프롬프트와 L1 양쪽의 유일한 출처다
(`DmlScopeExtractor.DmlScopeTableHeading`과 같은 방식).

```
### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| UPDATE 1 | 39 | PGName | NOT IN | 9 | 'PLCard', 'SamSungPay', 'SSGPayCard', … |
```

`원소 수`를 별도 칸으로 두는 것이 이번 실패에 직접 대응한다. 9개짜리 집합 자리에
5개짜리 대체물이 놓였을 때, 목록만 있으면 눈으로 세어야 알지만 수가 칸으로 있으면
어긋남이 즉시 보인다. 리터럴에 `|`가 들어갈 수 있으므로 기존 `EscapeTableCell`을
통과시킨다.

### 4.1 렌더 지점은 셋이다

- `BuildSpecificationPrompts` — `AiService.cs:392`
- `BuildFunctionSpecificationPrompts` — `AiService.cs:908`
- `BuildSpecSectionPrompts`의 `CrudAnalysis` 분기 — `AiService.cs:1987`

DML 범위 표가 실리는 바로 그 세 곳이다. 이 목록을 못 박는 이유는 흉터가 있기
때문이다 — Task 4의 Critical이 정확히 "규칙이 두 프롬프트 빌더 중 하나에만
연결됐고, 지역 모델 경로는 `BuildSpecificationPrompts`를 아예 호출하지 않는다"였다.

## 5. L1 검사

`CheckSetPredicates`. 판정 단위는 행이고 **행 키는 `라인 + 컬럼`** 이다 — 한 문장에
`IN`이 둘 이상일 수 있어 라인만으로는 유일하지 않다.

절차:

1. `CheckDmlScopeTable`이 하듯 헤딩 다음 구간(다음 `## `/`### ` 전까지)으로 탐색을
   좁힌다. 구간 자체가 없으면 표 부재 오류를 낸다.
2. 각 사실마다 그 구간에서 라인 번호와 컬럼 이름을 함께 담은 행을 찾는다.
   없으면 행 부재 오류.
3. 그 행을 `|`로 쪼개 **`리터럴 목록` 칸 하나만** 꺼내고, 쉼표로 나눠 트림한 원소
   집합을 원본 집합과 **대칭 비교**한다. 누락과 추가를 함께 보고한다.

### 5.1 왜 행 전체가 아니라 칸 하나인가

행 전체를 부분 문자열로 훑으면 **숫자 리터럴에서 퇴화한다.**
`| UPDATE 3 | 108 | UseState | IN | 2 | 0, 1 |`에서 `0`과 `1`을 찾으면 라인 번호
`108`이 이미 둘 다 담고 있어 무조건 통과한다 — 검사가 아무것도 묻지 않게 된다.
칸을 꺼내 원소 집합으로 비교하면 숫자든 문자열이든 같은 규칙이 적용되고, 오류
메시지가 "누락: `SSGPayCard`, `KakaoCard`"로 구체화된다.

### 5.2 원소 대조가 이 검사의 전부다

행 골격만 요구하면 모델이 표를 옮기면서 원소를 흘려도 통과한다. 그것이 이번에
일어난 일이다. 대조를 행 안으로 가두었기 때문에 §1.2의 제약 — 다른 문장에서의
우연한 등장이 판정을 흔들면 안 된다 — 이 구조적으로 지켜진다. `EXPECT_PROC`에서
7개 리터럴이 문서 다른 곳에 있어도 이 검사는 그것들을 보지 않는다.

## 6. 검증

### 6.1 검사마다 레드-그린

수정을 되돌렸을 때 실제로 실패해야 한다. 이 브랜치에서 네 번 반복된 패턴 —
수정이 자기 동기 사례에 닿지 않는데 스위트는 초록 — 을 막는 유일한 수단이다.

### 6.2 골든 케이스는 실물 코퍼스로

`AxisAGoldenCaseTests`는 이미 `output/` 전체를 훑고 없으면 깨끗이 건너뛴다. 여기에
두 동기 사례를 못 박는다 — `EXPECT_PROC` 갱신 1의 `PGName NOT IN` 9원소와
`COMM_UPD` 문장 2의 `PGNAME IN` 6원소가 추출기 결과에 그 문장·그 컬럼으로 존재해야
한다.

픽스처가 아니라 실제 DDL로 잡는 이유는, 최종 리뷰에서 발견된 Critical이 "12개
태스크 리뷰가 전부 픽스처만 썼고 실물 코퍼스를 안 봐서 감사의 그 문서가
통과했다"였기 때문이다.

### 6.3 조기 반환 항을 반드시 잇는다

`SpecExpectations.From`은 AND 연쇄로 "대조할 재료가 하나도 없을 때만 `null`"을
판정하고, 호출부는 `null`을 "모든 대조 건너뜀"으로 읽는다. 새 재료를 더하면서 이
식에 자기 항을 잇지 않으면 **그 검사는 한 번도 돌지 않고 스위트는 초록으로 남는다.**

게다가 AND 연쇄라 다른 재료를 만드는 픽스처가 빠진 항을 가린다 — Task 9의
`DmlScopeExtractor`가 Task 5와 Task 6의 배선 테스트를 조용히 깬 것이 정확히 그
방식이었다. 그래서 셋을 함께 요구한다:

1. 조기 반환 식에 `SetPredicates` 항을 잇는다.
2. **다른 재료가 전혀 나오지 않는 DDL**로 배선 테스트를 쓴다.
3. 그 항을 뺐을 때 배선 테스트가 실패하는지 확인한다.

## 7. 이 설계가 보증하지 않는 것

- **명세서가 다른 곳에서 집합을 잘못 바꿔 말하는 것을 막지 않는다.**
  `EXPECT_PROC`의 `Spec.md:82`가 5개짜리 목록을 "원천 PG 목록"으로 정의한 문장은 이
  검사로 사라지지 않는다 — 표가 옆에 실려 모순이 눈에 보이게 될 뿐이다. 후자는
  자연어 판정이고, 축 B에서 그 시도가 실측 15건 중 14건 오탐을 낸 것이 이미
  기록돼 있다. 의도적으로 범위 밖에 둔다.
- **스칼라 리터럴 비교를 담지 않는다.** §2의 474건. 컬럼 이름이 이미 존재를
  알리므로 이번 실패 방식이 성립하지 않는다.
- **서브쿼리 `IN`과 혼합 원소 `IN`을 담지 않는다.** §3.2.
- **`MERGE`를 담지 않는다.** `DmlScopeExtractor`가 이미 같은 이유로 담지 않는다
  (실측 코퍼스에 없음).
