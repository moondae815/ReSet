# SP 명세서의 함수 서술을 없애고 기계 확정 표로 바꾼다

작성일: 2026-08-20 · 상태: 설계 승인 대기

## 1. 문제

축 A 교차 대조에서 **`EXCEPTION_PROC` 명세서의 「UDF 활용 규칙」 표 10행 중 8행이 결함**이었고
그중 🔴 5건이 나왔다. 그 감사 전체 🔴 7건 중 5건이 이 표 하나에 있었다.

같은 부류의 표가 **14개 SP 중 6개**에 있고 합계 28행이다.

| SP | 행 수 | 대조 |
|---|---|---|
| EXCEPTION_PROC | 10 | 완료 (8행 결함 · 🔴 5) |
| INS_EXTRA4PLCARD | 5 | 미대조 |
| COMM_UPD | 4 | 미대조 |
| INS_EXTRA | 4 | 미대조 |
| EXPECT_PROC | 3 | 미대조 |
| SETTLE_INS | 2 | 미대조 |

🔴 4건의 형태는 모두 **요약이 조건을 잃는 것**이다.

- `UF_GET_COMM4PG4INTEREST` — 2차 조회의 필수 술어 `USESTATE = 0`이 통째로 빠짐
- `UF_GET_COMM4CLIENT` — 조회 키와 값이 동시에 갈리는 `IIF(flag IN (0,2), …)` 분기 누락
- `UF_GET_COMM4CLIENT4PARTIALCANCEL` — 같은 분기가 1차·2차 조회 모두에서 누락
- `UF_GET_PGCommOption` — 미조회 시 기본값 `0`(=반올림) 반환 누락

## 2. 진단 — 재료가 없어서가 아니다

처음에는 "파서가 함수 이름만 담고 동작은 담지 않는다"고 판단했으나 **틀렸다.**
확인한 사실은 이렇다.

- `Dependencies`에 함수 10개가 `SQL_SCALAR_FUNCTION` 타입으로 있고, 각각
  `ReferencedDdlText`에 **DDL 전문**이 담긴다(555 ~ 6,055자).
- 그 본문이 `RawPromptContext`(135,954자)에
  `### 객체: dbo.UF_XXX (SQL_SCALAR_FUNCTION) - 발견 깊이: 1단계` 절로 실려 프롬프트에 들어간다.
- 지시까지 있다 — *"If the source code of a referenced User Defined Function (UDF)
  is provided, analyze its logic."*

**재료도 지시도 있는데 요약이 틀렸다.** 같은 함수를 SP마다 다르게 쓴다는 것이 직접 증거다.

```
UF_GET_INCVTAXRATE 를 5개 SP가 이렇게 쓴다
  COMM_UPD          "부가가치세율을 결정합니다"                 ← 동작 없음
  EXCEPTION_PROC    "0이면 0.1, 1이면 0, 그 외에는 0"            ← 정확
  INS               "포함 여부에 따라 결정합니다"                ← 동작 없음
  INS_EXTRA         "0이면 10.0/100.0, 1이면 0, 그 외 값도 0"   ← 정확
  INS_EXTRA4PLCARD  "계산에 사용합니다"                         ← 동작 전무
```

`UF_GET_ROUND4VAT`도 같고, `UIF_SettleYMD`는 411자 / 228자 / 29자로 갈린다.

실제 차이는 **재료의 유무가 아니라 형태와 계약**이다.

| | 집합 술어 표 (먹혔음) | 함수 DDL (안 먹힘) |
|---|---|---|
| 형태 | 기계 추출 **구조화 표** | **원문 SQL 덩어리** |
| 위치 | 쓰는 자리 바로 옆 | 136K 컨텍스트 어딘가 |
| 계약 | "수정 금지, 축자 복사" | "분석하라" (해석 여지) |
| 검증 | `MechanicalValidator`가 확인 | 없음 |

## 3. 결정

**결정 1 — SP 명세서는 함수 동작을 아예 서술하지 않는다.**
호출 지점·인자·함수 명세서 링크만 싣는다. 요약을 정확하게 만드는 대신 **요약 자체를 없애
결함 부류를 구조적으로 제거한다.** 함수 동작의 단일 진실의 원천은 그 함수의 `Spec.md`다.

**결정 2 — 그 표는 조립기가 쓴다.**
LLM에게 지시로 타이르지 않고 쓸 기회 자체를 주지 않는다. 집합 술어 표와 같은 선례다.

## 4. 설계

### 4-1. 재료 출처는 `Dependencies`다

`StaticAnalysis.ReferencedFunctions`가 아니라 `Dependencies`의 `FUNCTION` 타입 항목을 쓴다.
파서가 인라인 TVF를 `ReferencedFunctions`에 싣지 못하기 때문이다.

```
EXPECT_PROC   ReferencedFunctions 2개  ·  Dependencies 함수 3개
INS_EXTRA     ReferencedFunctions 3개  ·  Dependencies 함수 4개
              차이는 둘 다 UIF_SettleYMD
```

부수 효과로 **감사 스킬이 "파서 한계"로 문서화한 구멍이 함께 닫힌다.**
내장 함수(`ISNULL`·`ROUND`·`CAST`)는 `Dependencies`에 없으므로 자연히 걸러진다.

### 4-2. 새로 만드는 것

기존 패턴을 그대로 따른다. `DmlScopeExtractor`가 사실을 뽑고 `AiService`가 표로 렌더한다.

| 무엇 | 어디 | 내용 |
|---|---|---|
| `ReferencedFunctionCallFact` 레코드 | `DmlScopeExtractor.cs` | 한정명 · 연산 · 문장 번호 · 라인 · 호출식 |
| `ExtractFunctionCalls(ddlText, knownNames)` | 〃 | 기존 문장 채번을 재사용하는 방문자 |
| `BuildReferencedFunctionTableLines(facts, spDef)` | `AiService.cs` | 기계 확정 표 렌더 |

`ExtractFunctionCalls`는 `knownNames`(= `Dependencies`의 함수 이름 집합)에 있는 호출만
수집한다. ScriptDom `FunctionCall` 방문 코드는 `DmlScopeExtractor.cs:643`에 이미 있다.

렌더 결과는 이런 형태다.

```markdown
### 참조 함수 (기계 확정 — 수정 금지)

| 함수 | 호출 위치 | 인자 | 명세서 |
| :--- | :--- | :--- | :--- |
| dbo.UF_GET_ROUND4VAT | UPDATE 3 (라인 110) | (CLCOMM 계산식) | [Spec](상대경로) |
```

### 4-3. 문장 번호는 이미 정확하다

`GlobalStatementOrdinal`은 전건 0으로 망가져 있지만 **이 작업에 필요하지 않다.**
DML 범위 표가 쓰는 `StatementOrdinal`(연산별 1..N)은 멀쩡하고 라인 번호도 정확하다.

```
| UPDATE 1 | 38  | ...
| UPDATE 2 | 55  | ...
| UPDATE 3 | 108 | ...
```

망가진 필드는 절 제목("갱신 0")에만 쓰인다. **별개 결함(⚪)이므로 이 작업에 묶지 않는다.**

### 4-4. 프롬프트 계약을 뒤집는다

```
지금   "If the source code of a referenced UDF is provided, analyze its logic"

바꿈   참조 함수 절은 기계가 채운다. 수정 금지.
       함수의 동작을 문서 어디에서도 서술하지 마라.
       SET 식이 함수를 호출하면 호출 사실과 링크만 적고
       무엇을 반환하는지 설명하지 마라.
```

`CacheManager.CurrentCacheFormatVersion`을 4 → 5로 올린다.

## 5. 범위 밖

**함수 DDL 본문을 프롬프트에서 뺄지는 이번에 정하지 않는다.** 동작 서술을 금지해도 LLM이
*호출하는 문장*을 정확히 쓰려면(예: "함수가 0을 반환하는 행은 갱신 대상에서 빠진다")
본문이 필요할 수 있다. 빼면 토큰이 크게 줄지만 측정 없이 지울 일이 아니다.
이번 변경 뒤 재생성 결과를 보고 별도로 판단한다.

**`GlobalStatementOrdinal` 수정도 범위 밖이다**(4-3).

**미대조 18행의 교차 대조도 이 작업에 포함하지 않는다.** 재생성 뒤 측정 수단으로 쓴다(6절).

## 6. 검증

TDD로 간다. RED → 실패 확인 → GREEN 순서를 지킨다.

**단위 테스트** — `ExtractFunctionCalls`

- 문장 번호와 라인이 DML 범위 표와 일치한다
- 내장 함수(`ISNULL`·`ROUND`·`CAST`)는 수집되지 않는다
- 인라인 TVF(`UIF_SettleYMD`)는 수집된다
- 중첩 호출(`UF_GET_ROUND4VAT(UF_GET_CLIENTSECTIONRATE(...) * UF_GET_INCVTAXRATE(...))`)에서
  바깥·안쪽 호출이 모두 수집된다
- 같은 함수를 여러 문장이 부르면 문장마다 한 행씩 나온다

**렌더러 테스트** — `BuildReferencedFunctionTableLines`

- 헤더 열 수와 구분자 행 열 수가 같다(GFM 렌더 보장 — 감사에서 이 결함이 두 번 나왔다)
- 호출이 없으면 절 자체를 내지 않는다

**회귀** — 기존 2007개가 그대로 통과한다.

**실물 확인** — SP 하나를 재생성해 절이 기계 출력 그대로인지, 함수 동작을 서술한 산문이
남았는지 본다.

**최종 측정** — 교차 대조 28행(대조한 10 + 미대조 18)을 돌려 **🔴 5건이 닫혔는지** 본다.
이것이 성공 기준이다.

## 7. 위험

**LLM이 다른 절에서 함수 동작을 계속 서술할 수 있다.** 표를 뺏어도 「로직 흐름」이나
「CRUD 분석」 산문에서 "이 함수는 …를 반환합니다"라고 쓸 여지가 남는다. 프롬프트 계약을
"참조 함수 절"이 아니라 **"문서 어디에서도"**로 넓게 쓴 이유다. 재생성 뒤 확인해야 한다.

**링크가 깨질 수 있다.** 함수 `Spec.md`의 상대 경로는 로컬(`output/Functions/`)과
외부 DB(`output/External/<DB>/Functions/`)가 다르다. 기존 「참조 코드 객체」 절이 이미 이
경로를 만들고 있으므로 그 로직을 재사용한다.

**캐시 전면 무효화.** 버전을 올리므로 다음 실행에서 전 객체가 재분석된다. 의도된 비용이다.
