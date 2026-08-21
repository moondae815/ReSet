# 잠금 힌트·ORDER BY·객체 선언을 기계 확정 재료로 올린다

> 2026-08-21 · 축 A 감사 6회차가 남긴 🟡 다섯을 닫는다.

## 무엇을 왜 고치는가

2026-08-21 축 A 감사는 🟡 9건을 남겼다. 그중 다섯이 한 부류다.

| 객체 | 결함 |
|---|---|
| `UF_GET_OUTYMD4REFUND` | `WITH SCHEMABINDING`이 없음이 DDL에서 확정되는데 "확인할 수 없음"으로 적음 |
| `UF_GET_SETTLE_EXCHANGERATE` | 같은 결함 |
| `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` | `NOLOCK`이 문장·테이블마다 갈리는데 "5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉갬 |
| `UP_Util_Settle_Summary_AcqManual` | `DELETE` 대상 스캔의 `NOLOCK`을 빠뜨려 3곳 중 2곳만 서술 |
| `UP_UTIL_STAT_PGCOLLECT_INS` | `ORDER BY INYMD, CLIENTID, PGNAME, MALLID`가 문서 어디에도 없음 |

**다섯 다 원본에서 한 번 읽으면 끝나는 사실인데, 모델의 서술에 맡겨 두어 실행마다 흔들린다.**
같은 자리가 재생성마다 다른 답을 내는 것이 실측됐다 — `UF_GET_OUTYMD4REFUND`는 8/20 판에
스키마 바인딩 언급이 아예 없다가 8/21 판에서 "확인할 수 없음"이 새로 생겼다.

이 프로젝트는 같은 문제를 이미 한 번 풀었다. 「참조 함수」 표를 기계 확정 재료로 올리자
축 A 교차의 🔴 5건이 닫혔고, 두 번째 실행에서 8/8 정합이 됐다. **지시를 강화하면 그때뿐이고,
재료로 확정하면 부류가 닫힌다.** 같은 방법을 세 사실에 적용한다.

## 범위

**포함**: 잠금 힌트 · `ORDER BY` · 객체 선언(함수 `WITH` 옵션).

**제외**: 주석 처리된 죽은 코드 블록(`UP_UTIL_SETTLE_INS`의 🟡). 주석은 AST 노드가 아니라
토큰이라 별도 경로가 필요하고, "무엇이 죽은 코드 블록인가"(한 줄 주석 vs 통째 주석 처리된
SQL)를 판정하는 규칙을 새로 세워야 한다. 이번 셋과 성격이 다르므로 별도 과제로 남긴다.

**제외**: 프로시저의 `WITH ENCRYPTION`·`RECOMPILE`·`EXECUTE AS`. 같은 자리에서 뽑을 수
있지만 감사가 지적한 결함이 없다. 넣으면 모든 SP에 표가 하나 더 붙고 L1 검사 면적이 는다.
나중에 필요해지면 「객체 선언」 표에 SP를 더하면 된다 — 칸 구성이 같다.

## 추출 계약

세 사실이 AST에서 확정 가능함을 프로브로 실측했다(ScriptDom 180.37.3).

### ① 잠금 힌트

`NamedTableReference.TableHints`가 힌트를 그대로 준다.

```
TSettleMst / -       -> (없음)              ← UPDATE 대상 노드
TSettleMst / A       -> NoLock
dbo.TPGProperty / PG -> (없음)
dbo.TPGProperty / Y  -> NoLock,ReadUncommitted
```

두 가지가 설계를 정한다.

**행이 되는 자리는 셋이다.** 처음에는 "대상 노드를 싣지 않는다"로 정했다가, 프로브가 그
규칙이 사실을 잃는 것을 보여 다듬었다.

```
DELETE T FROM dbo.T A WITH(NOLOCK)   대상노드 (없음) · FROM 참조 NoLock   ← 대상은 껍데기
DELETE FROM dbo.T WITH(NOLOCK)       대상노드 NoLock  · FROM 없음         ← 대상이 곧 스캔
UPDATE dbo.T WITH(NOLOCK) SET …      같음
```

1. **`FROM` 절의 모든 테이블 참조** — 전수, 힌트 유무 무관.
   `INSERT`는 원천 `SELECT`의 `FROM`이 그 자리다.
2. **`FROM` 절이 없는 `UPDATE`·`DELETE`의 대상 노드** — 그 자체가 스캔이므로 한 행.
3. **힌트를 진 대상 노드** — `INSERT INTO T WITH(TABLOCK)` 같은 쓰기 대상의 힌트도 사실이다.

`FROM`이 있을 때 대상 노드를 빼는 이유는 그것이 스캔이 아니라 갱신 대상 지시자이고 힌트를
지지 않기 때문이다. 그대로 실으면 같은 테이블이 "힌트 있음 / 없음" 두 행으로 나와 독자를
오도한다. 감사가 지적한 자리(`DELETE TSettleByOUT FROM … WITH(NOLOCK)`)는 1번에 걸린다.

**힌트는 목록이다.** 한 참조에 `NOLOCK, READUNCOMMITTED`처럼 여럿이 붙을 수 있다. 칸은
불리언이 아니라 힌트 목록이고, `READPAST`·`UPDLOCK` 같은 것도 그대로 실린다 —
"`NOLOCK` 여부"가 아니라 "이 참조에 걸린 힌트"가 사실이다.

**파생 테이블 안으로 내려가지 않는다.** 파생 테이블의 테이블 참조는 그 스코프의 것이고
바깥 문장의 잠금 동작과 별개다. `ExplicitVisit(QueryDerivedTable)`을 비워 막는다 —
`Visit`을 비우면 ScriptDom이 자식으로 계속 내려간다(2026-08-20 실측).

### ② `ORDER BY`

`InsertSpecification.InsertSource`가 `SelectInsertSource`일 때
`QuerySpecification.OrderByClause`로 잡힌다.

```
INSERT INTO T (A,B) SELECT A,B FROM S GROUP BY A,B ORDER BY A,B   -> 2개 요소
INSERT INTO T (A,B) SELECT A,B FROM S                             -> (없음)
INSERT INTO T (A,B) VALUES (1,2)                                  -> (없음)
```

`UPDATE`·`DELETE`는 최상위 `ORDER BY`가 **문법상 불가**하므로 이 사실은 `INSERT`에만 붙는다.

**존재 여부가 아니라 컬럼 목록을 싣는다.** 더 충실하고 비용이 같다.

### ③ 객체 선언

`CreateFunctionStatement.Options`가 `SchemaBinding`·`ReturnsNullOnNullInput` 등을 준다.
없으면 빈 목록이다. 스칼라 함수든 인라인 TVF든 같다.

```
CREATE FUNCTION dbo.F1(...) RETURNS INT AS ...                        -> (없음)
CREATE FUNCTION dbo.F2(...) RETURNS INT WITH SCHEMABINDING AS ...     -> SchemaBinding
CREATE FUNCTION dbo.F3(...) WITH SCHEMABINDING, RETURNS NULL ON NULL INPUT
                                                                      -> SchemaBinding,ReturnsNullOnNullInput
```

**프로시저에는 이 옵션 자체가 없으므로 이 표는 함수에만 실린다.**

## 표 모양

기존 넷의 관례를 잇는다 — **표 하나 = 사실 한 종류, 셀 의미가 행마다 같음.**
이 성질이 「참조 함수」 표가 🔴 5건을 닫은 힘이므로 깨지 않는다.

### 잠금 힌트 — 새 표, `## CRUD 분석` 아래

```markdown
### 잠금 힌트 (기계 확정 — 수정 금지)

| 문장 | 라인 | 테이블 | 별칭 | 힌트 |
| :--- | :--- | :--- | :--- | :--- |
| DELETE 1 | 36 | TSettleMst | A | (없음) |
| INSERT 1 | 167 | TPGProperty | P | NOLOCK |
| INSERT 1 | 173 | TPGProperty | Y | NOLOCK |
| UPDATE 1 | 189 | TSettleMst | A | NOLOCK |
```

행 단위는 (DML 문장 × 스캔 자리)다. 전수로 싣는다 — "수정 금지" 표에 빈 칸이 있으면 계약이
서지 않고, 독자가 "여기 없는 문장은 어떻다는 뜻인가"를 추론해야 한다.

**행이 하나도 없는 문장이 있다.** `FROM` 절도 없고 대상 노드에 힌트도 없는 `INSERT … VALUES`나
단순 `UPDATE T SET C = 1`이 그렇다. 그런 문장은 이 표에 나타나지 않으며, 그것이 "스캔할
자리가 없다"는 뜻이다. 「DML 범위」 표가 모든 DML 문장을 한 행씩 싣고 있으므로 두 표를 나란히
보면 어느 문장이 이 표에서 빠졌는지 바로 보인다 — 표 안에 빈 행을 만들어 채우지 않는다.

`INS_EXTRA4PLCARD`의 결함이 이 표에서 눈에 보인다: 같은 `TPGProperty`가 `P`·`Y`에는 힌트가
붙고 `PG`에는 안 붙는다는 것이 한 표 안에 나란히 선다.

### `ORDER BY` — 기존 「DML 범위」 표에 칸 추가

```
| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼 | 기준일 파라미터 적용 | 조인 키 | ORDER BY |
| INSERT 1 | 104 | TStatPGCollect | (없음) | 예 | … | INYMD, CLIENTID, PGNAME, MALLID |
| UPDATE 1 | 179 | TSettleMst | ProcYMD, … | 예 | … | — |
```

행 집합이 이미 DML 문장 단위라 표를 새로 만들 이유가 없다.
`UPDATE`·`DELETE`는 `—`(문법상 불가), `ORDER BY` 없는 `INSERT`는 `(없음)`.

### 객체 선언 — 새 표, `## 개요` 아래, 함수에만

```markdown
### 객체 선언 (기계 확정 — 수정 금지)

| 객체 | WITH 옵션 |
| :--- | :--- |
| dbo.UF_GET_OUTYMD4REFUND | (없음) |
```

`(없음)`이 곧 "스키마 바인딩 아님"이다. 명세서가 "확인할 수 없음"이라고 쓸 여지가 사라진다.

## 배선

기존 네 표의 방출 지점을 `grep`으로 전수로 뽑아 확정했다. `BuildSpecSectionPrompts`는
`OverviewAndParameters`(`AiService.cs:2226`) · `CrudAnalysis`(`:2254`) ·
`LogicAndVisualization` 셋으로 갈린다.

| 표 | 배선 지점 |
|---|---|
| 잠금 힌트 | SP 최초 생성 `:391-442` · 함수 경로 `:1103-1177` · `CrudAnalysis` `:2254` |
| `ORDER BY` 칸 | `BuildDmlScopeTableLines` 한 곳 — 위 세 경로가 이 헬퍼를 공유한다 |
| 객체 선언 | 함수 경로 `:1103-1177` · `OverviewAndParameters` `:2226` (객체 종류로 가드) |

**함수 경로를 놓치기 쉽다.** 2026-08-20 참조 함수 표 작업에서 이 부류를 세 번 연속 놓쳤다 —
"지점 3개"라 했는데 4개였고, 다시 세니 5개였고, 최종 리뷰가 6번째(`LogicAndVisualization`)를
찾았다. 구현 때도 `grep`으로 전수 검산한다.

## L1 앵커

`MechanicalValidator`에 검사 셋을 추가한다. 기존 `CheckReferencedFunctions`와 같은 모양이다 —
재료가 있는데 헤딩이 없으면 오류, 헤딩은 있는데 행이 빠졌으면 오류. 재료가 없으면 검사하지
않는다(잠금 힌트가 없는 객체, 함수가 아닌 객체).

**표만 넣고 검사를 안 세우면 모델이 옮겼는지 아무도 모른다.** 참조 함수 표가 그 상태로 한
판 나갔고, 그래서 L1 앵커(M1)를 나중에 따로 붙여야 했다.

지적이 모델에게 닿는 것은 이미 보장돼 있다. `BuildSuggestedPromptFix`가 2026-08-20에
catch-all 버킷을 얻어, 열거되지 않은 `ErrorType`도 내용이 실려 나간다. 그 전이었다면 이번에도
검사만 세우고 피드백은 빈 채로 나갔을 것이다.

`SpecExpectations`에 필드 셋을 추가하고 **`From()`의 조기 반환 AND 사슬에도 넣는다** —
빠뜨리면 재료가 있는데 기대값이 비어 검사가 통째로 꺼진다.

## 딸려오는 것

- **캐시 형식 7 → 8.** 프롬프트 입력이 달라지므로 31개 전건 재분석.
- **감사 스킬 `references/axis-a.md`**에 새 표 셋의 대조 계약 추가.
  기존 네 표와 같은 자리에 같은 모양으로 적는다.
- **`docs/architecture.md`**의 캐시 버전 표기 갱신.

## 검증

**단위 테스트** — 추출기 셋 각각에 정상·경계·부재 케이스. 특히:
- 잠금 힌트: `UPDATE` 대상 노드 제외, 힌트 여럿, 파생 테이블 안쪽 제외
- `ORDER BY`: `INSERT…SELECT` 있음/없음, `VALUES` 형, `UPDATE`(`—`)
- 객체 선언: 옵션 없음/하나/여럿, 인라인 TVF, 프로시저(표 없음)

**조립기 테스트** — 세 경로가 같은 표를 낸다는 것을 코드로 보장한다. 헬퍼를 공유하므로
헬퍼 하나를 테스트하고, 각 경로가 그 헬퍼를 부르는지 확인한다.

**L1 테스트** — 헤딩 누락과 행 누락을 각각 잡는지. RED를 먼저 본다.

**실물 검증** — `EXCEPTION_PROC`·`INS_EXTRA4PLCARD`·`Summary_AcqManual`·`STAT_PGCOLLECT_INS`·
`UF_GET_OUTYMD4REFUND`의 원본 DDL로 프로브를 돌려 감사 판정과 대조한다. 단위 테스트가
픽스처의 우연으로 통과하는 일을 두 번 겪었으므로(2026-08-20 파생 테이블 별칭,
2026-08-21 정규화 가드) 실물 대조를 생략하지 않는다.

## 이 설계가 닫지 않는 것

- **주석 처리된 죽은 코드 블록**(`UP_UTIL_SETTLE_INS` 🟡)은 범위 밖이다.
- **`INS_EXTRA`의 🟠**(삭제/삽입 범위 대조)와 **🟡 둘**(`NOLOCK` 게이트 연결, 산술 `NULL`
  전파)은 추출 가능한 사실이 아니라 **추론**이라 기계 재료로 올릴 수 없다. 프롬프트 쪽
  보정이나 다른 방법이 필요하다.
- **`UIF_SettleYMD`의 🟡**(파서 확정값을 문서가 부정)은 재료가 이미 있는데 모델이 뒤집은
  것이라, 새 재료가 아니라 L1 검사가 필요한 자리다. 이번 셋과 성격이 달라 별도로 본다.
- 이 설계는 **라이브 모드를 검증하지 않는다.** 오프라인 스냅샷 경로로만 실물 검증한다.
