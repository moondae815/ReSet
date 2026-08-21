# 명세서 결함을 기계 확정 사실로 막는다 — 설계

- 날짜: 2026-08-22
- 상태: 설계 승인 대기
- 브랜치: `worktree-spec-prompt-rules`

## 1. 배경

`POQSettlePrco20` Job의 산출물 정합성 감사(축 A, 43단위 전수)가 `Spec.md`에서 결함 125건을
냈다 — 🔴 2 · 🟠 7 · 🟡 56 · ⚪ 60. 그중 아홉 건이 금액 또는 대상 행 집합을 바꾼다. 실행
대조 6건은 로컬 SQL Server 2022로 닫았고, 그 과정에서 판정 하나가 🟡에서 🔴으로 승격됐다.

감사 보고서: `output/Jobs/POQSettlePrco20/consistency/ConsistencyReport.md`
(단위별 판정과 앵커는 같은 디렉터리의 `.cache.json`. 둘 다 gitignore 대상인 `output/` 아래라
이 저장소에는 없다.)

이 명세서들로 코드를 생성하기 전에 결함을 없애고 재생성하는 것이 목표다.

### 이 설계가 겨냥하는 것

프롬프트에 규칙 문장을 더하는 방식은 **이미 한 번 실패한 접근**이다. F2("실재하는 컬럼을
없다고 단정")를 막는 규칙은 이미 있고(`AiService.cs:138-141`), 4갈래가 공유하는 자리이며,
L1 검사(`MechanicalValidator.CheckSchemaClaims`)까지 짝지어져 있다. 그런데도 SP 3개가
어겼다. §4의 진단이 그 이유를 밝힌다.

그래서 이 설계는 **AI가 판단할 여지를 없애는 쪽**을 택한다 — 추출기가 사실을 계산해
`기계 확정 — 수정 금지` 표로 싣고, L1이 산출물과 대조한다.

## 2. 결정 사항

| # | 결정 | 근거 |
|---|---|---|
| D1 | 프롬프트 규칙이 아니라 **기계 확정 값으로 승격** | 규칙+검사 조합이 F2에서 이미 실패했다 |
| D2 | 승격 범위는 **A~G 전부** | 각 항목이 실제 🔴/🟠/🟡을 닫는다 |
| D3 | 표는 **`실행 의미` 통합 1개 + `CASE 분기` 1개** | 헤딩·상수·L1 검사·규칙 문장이 종류마다 늘어나는 것을 막는다 |
| D4 | **감사 계약(`axis-a.md`) 갱신을 같은 변경에 포함** | 감사가 새 표를 모르면 승격이 검증되지 않는다 |
| D5 | 4갈래 결합은 **신규 2종만 공유 빌더** | 재발 사고를 신규에 한해 구조적으로 막되, 기존 6종은 흔들지 않는다 |
| D6 | **H(스키마 표 과소 포함 수정)를 범위에 추가** | §4의 진단 결과. H 없이는 F2가 그대로 재발한다 |

## 3. 무엇을 확정 사실로 싣는가

### 3.1 표 1 — `### 실행 의미 (기계 확정 — 수정 금지)`

열: `| 종류 | 라인 | 대상 | 확정 사실 |`

| 종류 | 계산 내용 | 닫는 결함 |
|---|---|---|
| `식 타입 경로` | `CAST(<산술식> AS INT)`마다 피연산자 타입을 추론해 `money → int : 반올림` 또는 `numeric(p,s) → int : 절사` | 🔴 `UF_GET_COMM4CLIENT4INTEREST` · 🟡 `UF_GET_COMM4PG4INTEREST` |
| `집계 대입` | `SELECT @v = MIN/MAX/SUM/AVG(...)`(GROUP BY 없음) → 무결과 시 NULL. `COUNT`만 0. `DECLARE` 초기값이 있으면 "초기값 미유지"까지 | 🟠 `UP_UTIL_SETTLE_INS_EXTRA` · 🟠 `UP_UTIL_SETTLE_SUMMARY_EXTRA` |
| `@@ROWCOUNT` | `@@ROWCOUNT`를 읽는 자리의 직전 형제 문장이 `IF`면, **분기가 건너뛰어지면** 0으로 리셋되어 조건이 참이 되고 **분기가 실행되면** 그 마지막 문장의 행 수가 남는다는 것을 둘 다 싣는다 | 🔴 `UF_GET_COMM4CLIENT` |
| `커서 수명` | `DECLARE CURSOR`마다 ① `OPEN`~`CLOSE` 사이 `RETURN` 존재 → "오류 경로 미해제" ② `LOCAL` 미지정 → "범위가 서버 설정 의존" | 🟠 `UP_UTIL_SETTLE_SUMMARY_ETC` (+ `UP_UTIL_SETTLE_PROC_ETC` ⚪) |
| `DB 배치` | `ObjectKey.Database` + `ThreePartObjectReferences` + `LinkedServerReferences`를 확정형 문장으로 번역 | F1 무리 🟡 7 · ⚪ 2 |

**`식 타입 경로`의 근거.** 감사가 실행으로 확정한 사실이다 — `decimal/numeric`이 `money`보다
데이터 형식 우선순위가 높아, 리터럴 `100.0`(= `numeric(4,1)`)이 `CAST` 안에 있으면 `money`
피연산자가 `numeric`으로 승격돼 절사되고, 밖에 있으면 `money * money`가 남아 반올림된다.

```sql
CAST(@pi_intTxAmt * (@v_intCommission / 100.0) AS INT)   -- numeric → int : 0 방향 절사
CAST(@pi_intTxAmt *  @v_intFreeInterestRate    AS INT)   -- money   → int : 0에서 먼 쪽 반올림
```

같은 값(`10050 × 1.50%`)이 앞은 `150`, 뒤는 `151`이다. 형제 함수 7개 중 2개만 뒤쪽 경로이고,
명세서 어디에도 이 갈림이 없다. **C#의 정수 캐스트는 절사이므로 자연스러운 번역이 바로
틀린 쪽**이라는 점이 이 항목을 🔴으로 만든다.

### 3.2 표 2 — `### CASE 분기 (기계 확정 — 수정 금지)`

열: `| 라인 | 순서 | 조건 원문 | 결과 원문 |`

`CASE` 식마다 `WHEN`을 순서대로 전수 싣고, `ELSE`는 조건 칸에 `(그 외 전부)`를 쓴다.
**원문 그대로**다 — 부등호도, `RIGHT('0' + CONVERT(VARCHAR(2), SettleDayN), 2)` 같은 결합식도
요약하지 않는다.

`UIF_SettleYMD`의 🟠 3건이 각각 분기 뭉갬 · `>` 등호 생략 · 영 채움 누락이었고, 셋 다 이
표 하나로 닫힌다.

### 3.3 기존 표 확장 — `DML 범위`에 `GROUP BY` 열

현재 열은 `문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼 | 기준일 파라미터 적용 | 조인 키 | ORDER BY`다.
`GROUP BY`를 더하면 🟡 2건(`UP_Util_Settle_Summary` · `UP_Util_Settle_Summary_AcqManual`)이 닫힌다.

**기존 표의 모양을 바꾸는 유일한 항목**이므로 `MechanicalValidator.CheckDmlScopeTable`과
기존 테스트가 함께 움직인다. 그래서 작업 순서에서 분리한다.

### 3.4 한계를 명시한 세 항목

기계 확정 표에 추측이 섞이면 표 전체의 신뢰가 무너진다. 셋은 범위를 좁혀 구현한다.

- **`식 타입 경로`** — 잎 노드(파라미터·지역변수·리터럴·컬럼)의 타입을 전부 알 때만 행을
  싣는다. 하나라도 모르면 **행을 생략**하고 로그만 남긴다.
- **`@@ROWCOUNT`** — 실측으로 확인한 모양(직전 형제가 `IF`)에만 한정한다. T-SQL의 일반
  규칙을 전부 구현하려 들면 틀릴 여지가 커진다.
- **`커서 수명`** — 완전한 경로 분석 대신 렉시컬 관측("`OPEN`과 `CLOSE` 사이에 `RETURN`이
  있다")만 싣는다. 실측 두 건이 걸린 모양이다.

셋 다 `RoundingSemanticsExtractor`의 선례를 따른다 — 추출기가 `public const string`으로 의미
문장을 들고, 프롬프트와 L1이 그 상수를 함께 쓴다("두 곳이 다르게 말하면 안 된다").

## 4. F2 진단 — 규칙이 아니라 재료가 잘못 갔다

`SchemaPromptColumnSelector`는 프롬프트 스키마 표에 어떤 컬럼을 실을지 정하는 단일 권위다.
그 클래스 주석이 이 결함을 스스로 예고하고 있다.

> 이 필터는 토큰 절약용 최적화이지 정확성 장치가 아니다. 과다 포함은 표에 불필요한 행을 몇 개
> 더할 뿐이지만, **과소 포함은 모델이 그 컬럼을 "존재하지 않는다"고 잘못 기록한다 — 14개
> 명세서를 망가뜨린 바로 그 결함이다.**

필터가 남기는 것은 셋뿐이다 — ① AST가 감지한 참조 컬럼 ② PK/FK ③ 인덱스 구성 컬럼.

`TClient.ClientIDType`은 **주석 처리된 조건에만** 등장한다. AST 참조가 아니고, PK/FK도
인덱스도 아니다 → 표에서 잘려 나간다. AI는 환각하지 않았다. **자기가 받은 표를 정직하게
읽고** "없습니다"라고 썼다.

L1이 못 잡은 이유도 같다. `CheckSchemaClaims`의 기준값이 `PromptSchemaColumns`, 즉 **똑같이
잘린 집합**이다. 잘린 컬럼은 대조 대상에 아예 없으니 위반으로 잡힐 수가 없다. 규칙과 검사가
**같은 잘못된 재료를 공유**했고, 그래서 규칙을 하나 더 추가하는 방식으로는 절대 잡히지
않았을 결함이다.

### 미결 — `ProductName` 두 건

원문은 이렇다.

```
| SETTLE_POQ_DB.dbo.TSettleMst | X.PRODUCTNAME | ... | 스키마 불일치: 제공된 `TSettleMst`
스키마에는 `PRODUCTNAME` 컬럼이 없습니다. ... |
```

토큰(`스키마 불일치`)도 걸리고 백틱 식별자도 둘 이상이라 `CheckSchemaClaims`의 조건 1·2는
성립한다. 남은 것은 `PRODUCTNAME`이 잘린 집합에 있느냐인데, 원본 INSERT 목록이
`X.PRODUCTNAME`이라는 **별칭 한정 표기**여서 파서가 기록한 컬럼 키가 `ProductName`과
어긋났을 가능성이 크다. **단정하지 않는다** — H의 첫 작업이 이 두 건의 원인을 각각 못 박는
것이다.

### H — 스키마 표 과소 포함 수정

`SchemaPromptColumnSelector.Select`에 다음을 더한다.

- ④ **주석 처리된 코드가 언급한 컬럼**
- 별칭 한정 토큰(`X.PRODUCTNAME`)을 컬럼명으로 정규화해 대조

파일 주석이 스스로 "과다 포함은 싸고 과소 포함이 결함을 만든다"고 적어 두었으니, **넓히는
방향이 이 코드의 설계 의도와 같다.**

H는 A~G와 성격이 다르다. A~G는 없던 사실을 **더하는** 일이고, H는 이미 있던 사실이 **잘려
나가는 것을 막는** 일이다.

## 5. 구성 요소

### 5.1 신규 추출기 6개

`src/ReSet.Core/Services/` 아래. 이 저장소의 확립된 패턴을 따른다(`RoundingSemanticsExtractor`
91줄 · `SessionOptionsExtractor` 127줄 · `ObjectDeclarationExtractor` 169줄 ·
`DerivedTableColumnExtractor` 195줄) — 하나가 한 가지만 하고 독립적으로 테스트된다.

| 파일 | 입력 | 산출 | 담당 |
|---|---|---|---|
| `ExpressionTypePathExtractor.cs` | DDL + 타입 사전 | `TypePathFact[]` | A |
| `AggregateAssignmentExtractor.cs` | DDL | `AggregateAssignmentFact[]` | B |
| `RowCountBoundaryExtractor.cs` | DDL | `RowCountBoundaryFact[]` | C |
| `CursorLifecycleExtractor.cs` | DDL | `CursorLifecycleFact[]` | F |
| `DatabasePlacementExtractor.cs` | `StaticAnalysis` + `ObjectKey` | `DatabasePlacementFact?` | E |
| `CaseBranchExtractor.cs` | DDL | `CaseBranchFact[]` | D |

A만 입력이 다르다 — 타입 추론에 파라미터·지역변수·리터럴·컬럼 타입 사전이 필요하고, 컬럼
타입은 `Dependencies`에서 온다. DDL만으로 닫히지 않는 유일한 추출기다.

E는 AST를 보지 않는다. 재료가 이미 `StaticAnalysis`에 있어서, 그것을 확정형 문장으로
**번역**하는 것이 전부다.

### 5.2 집계자

`ExecutionSemanticsFacts.Collect(ddl, staticAnalysis, dependencies)` — A·B·C·E·F 다섯을 불러
`실행 의미` 표의 행 목록 하나로 합친다. `종류` 열이 출처를 담는다. D는 표가 달라 합치지 않는다.

이 집계자 덕에 프롬프트 쪽은 추출기 다섯을 알 필요가 없다 — 표 하나에 대응하는 호출 하나만 안다.

### 5.3 공유 빌더 (D5의 실체)

```
AppendMachineFactBlocks(spDef) → (blockText, ruleSentences)
```

표 렌더와 짝이 되는 규칙 문장을 함께 돌려준다. 4갈래는 이것을 호출해 `systemPrompt`에 붙이고
`rules`에 문장을 넣는 것이 전부다. 렌더 조건(`facts.Count > 0`)도 이 안에 있어 갈래마다
조건을 베끼지 않는다.

**왜 필요한가.** 프롬프트 빌더가 4갈래다 — `BuildSpecificationPrompts`(`AiService.cs:282`),
`BuildFunctionSpecificationPrompts`(`:1238`), `BuildSpecSectionPrompts`의 세 구역(`:2411`,
`:2465`, `:2682`). 로컬 provider 경로는 갈래 1을 아예 호출하지 않는다. "한 갈래만 고쳤다"가
이 코드베이스의 반복 사고로 주석에 명시돼 있다.

헤딩 상수 두 개는 규칙대로 **추출기가 소유**하고(`ExecutionSemanticsFacts.TableHeading`,
`CaseBranchExtractor.TableHeading`), `AiService`와 `MechanicalValidator`가 그 상수를 참조한다.

### 5.4 데이터 흐름

```
원본 DDL ──────┬─→ 추출기 6개 ─→ 집계자 ─→ 공유 빌더 ─→ 프롬프트 4갈래 ─→ AI
StaticAnalysis ┤                                                          │
Dependencies ──┘                                                          ▼
                                                                      Spec.md
                                                                          │
                                              MechanicalValidator(L1) ◄───┘
                                                          │
                                              어긋나면 자가수정 루프
```

## 6. L1 검사 짝

이 저장소에는 **"규칙 하나에 검사 하나"** 원칙이 있다(`AiService.cs:709`).

- `CheckExecutionSemantics` · `CheckCaseBranches`를 `MechanicalValidator`에 넣는다. 기대값은
  `SpecExpectations.From`이 **같은 추출기**를 불러 만든다 — 그 파일 주석이 반복해 못박듯
  "두 곳이 다르게 고르면 표와 기대값이 갈라져 재현 불가능한 실패가 생긴다".
- G는 `CheckDmlScopeTable`과 기존 테스트가 함께 움직인다.

### 6.1 F1을 막는 유보 문구 금지 검사

E(`DB 배치`)가 확정 행을 실으면, 짝이 되는 검사는 유보 문구 금지다 — 확정 사실이 존재하는데
명세서가 `단언할 수 없습니다` · `확인할 수 없습니다` · `제공되지 않았습니다`로 되짚으면 오류다.

다만 `CheckSchemaClaims`가 남긴 교훈을 적용한다. 그 주석은 **오탐이 재생성으로 고쳐지지 않아
무한 재시도를 겪었다**고 적고 있다. 그래서 이 검사도 **확정 행이 실제로 존재하는 종류에
한해서만** 발동하고, 표가 없으면 침묵한다. 실패 방향을 안전한 쪽으로 두는 것이 이 저장소의
확립된 태도다.

### 6.2 소프트 페일 격리

`MechanicalValidator.Validate`에는 catch-all이 있어서, 검사 중 하나라도 예외를 던지면
`Errors`를 전부 지우고 `IsValid = true`로 통과시킨다. 새 검사가 던지면 **기존 14개 검사 결과까지
함께 사라진다.**

이번 변경에서 그 구조는 바꾸지 않는다 — 이번 목적과 무관하고 파급이 크다. 대신 새 검사 넷을
**각각 자기 try/catch로 감싸** 자기 실패가 남의 결과를 지우지 않게 한다. 국소적이고 안전하다.

## 7. 감사 계약 갱신 (`references/axis-a.md`)

- **3-1절 대조 항목에 새 표 2종 추가** — 각각 "표가 있으면 산출물이 원문 그대로 실었는가",
  "표의 사실을 산문이 뒤집지 않았는가".
- **표 부재의 의미를 명시** — 스킬이 이미 `참조 함수` 표에 쓰는 어법("없는 것과 비어 있는
  것을 가르라")을 그대로 적용한다. 표가 없으면 **재료가 없다**는 뜻이고, 그 종류에 대해
  산문이 단정했으면 그것이 결함이다.
- **3-2-1 사각지대 절이 줄어든다** — `CASE` 분기와 실행 의미가 기계 확정으로 올라오면 그
  사실들을 산문이 홀로 지고 있던 구간이 사라진다.
- **G 반영** — `DML 범위` 표의 열이 하나 늘었음을 대조 항목에 적는다.

**실측 수치는 지어내지 않는다.** 스킬은 `SP는 호출 75건이 전부 표에 실린다` 같은 실측을 담고
있는데, 새 표의 실측은 재생성 후에야 나온다. 그 자리는 비워 두고 재생성 뒤 감사에서 채운다.

## 8. 테스트

**픽스처는 합성 DDL이다.** 실제 SP는 `output/`에 있고 gitignore 대상이라 테스트 자산이 될 수
없다. 결함 아홉 개의 **모양만** 최소 DDL로 재현한다 — `CAST(@a * (@c/100.0) AS INT)` 대
`CAST(@a * @r AS INT)`, `SELECT @v = MIN(x)`, `IF @@ROWCOUNT` 앞의 `IF`,
`OPEN`~`RETURN`~`CLOSE`, 주석에만 등장하는 컬럼.

| 층 | 어디에 | 무엇을 |
|---|---|---|
| 추출기 | 새 테스트 파일 6개 | 각 패턴 검출 + 경계(중첩·별칭·미상 타입) + 소프트 페일이 빈 목록을 돌려주는지 |
| 프롬프트 | `AiServiceTests_Rich.cs` | **4갈래 각각** 표가 실리는지 + 재료 없을 때 안 실리는지(대조군) |
| L1 | `MechanicalValidatorTests.cs` | 검사 4개 + 새 검사가 던져도 기존 검사 결과가 안 지워지는지 |
| H | `SchemaPromptColumnSelectorTests` · `SchemaClaimGateRegressionTests` | 주석에만 등장하는 컬럼이 표에 남는지 — `ClientIDType` 모양의 회귀 테스트 |

**헤딩 상수로 단언한다.** 원본 DDL에도 우연히 있는 단어로 단언하면 거짓양성이 된다는 경고가
`AiServiceTests_Rich.cs:748-754`에 있다.

기준선: `dotnet clean && dotnet build`의 `warning CS` 정확히 8개, `dotnet test` 실패 0 · 건너뜀 0.

## 9. 작업 순서

**H가 맨 앞이다.** A~G는 재료가 옳다는 전제 위에 서고, H가 그 전제를 고친다. H 하나만으로
F2 3건이 닫힌다.

1. **H** — 스키마 표 과소 포함 수정 + `ProductName` 두 건의 원인 확정
2. **E + 공유 빌더 골격** — 가장 싼 항목으로 D5의 뼈대를 세운다. 표 렌더·규칙 문장·L1 짝·
   4갈래 호출의 왕복이 여기서 한 번 완성되고, 이후 항목은 그 틀에 얹는다
3. **B · C · F** — 패턴 검출 셋
4. **D** — `CASE` 분기 표
5. **G** — 기존 표 열 추가 (기존 테스트가 움직이므로 분리)
6. **A** — 타입 추론. 가장 어렵고, 늦어져도 앞의 것들이 이미 서 있다
7. **`axis-a.md`** 갱신
8. `AGENTS.md` · `docs/architecture.md` 동기화 — `architecture.md §4.9`가 프롬프트 문구 담당
   절이고 `reset-doc-sync` 스킬 소관이다

각 단계가 독립적으로 빌드·테스트를 통과하는 상태로 끝난다. 6번이 늦어져도 1~5번은 이미
재생성에 쓸 수 있다.

## 10. 이 설계가 하지 않는 것

- **기존 기계 확정 표 6종을 공유 빌더로 통일하지 않는다.** 갈래별 렌더 조건에 미묘한
  비대칭이 있다 — 예로 `집합 술어`는 `dmlScopeFacts`가 비면 렌더하지 않는 소프트 페일 전파
  방지가 3자리에 각각 주석과 함께 박혀 있다. 잘못 통일하면 기존 표가 조용히 사라지거나
  더해진다. 표를 세 번째로 늘릴 때가 이주 시점이고, 그때 D5의 빌더가 이미 경로가 돼 있다.
- **`MechanicalValidator`의 catch-all 구조를 바꾸지 않는다**(§6.2).
- **개별 🟠 전부를 자동으로 없애지 않는다.** 승격이 닫는 것은 A~G가 겨냥한 결함이다. 재생성
  후 축 A 감사를 다시 돌려 무엇이 남았는지 확인해야 한다 — 캐시가 파일 해시라 고쳐진
  명세서만 재검증된다.
- **축 B는 이 범위가 아니다.** 현행 `agent/` 번들은 옛 명세서로 만든 것이라 지금 대조하면
  세대 차이가 결함으로 잡힌다. Job 설계 문서를 다시 만든 뒤에 돌린다.

## 11. 위험

| 위험 | 대응 |
|---|---|
| A의 타입 추론이 예상보다 어렵다 | 잎 타입을 모르면 행을 생략한다. 작업 순서 마지막이라 늦어져도 앞이 선다 |
| G가 기존 표를 깨뜨린다 | 단계를 분리하고, `CheckDmlScopeTable`과 기존 테스트를 함께 움직인다 |
| 새 L1 검사가 오탐을 내 무한 재시도를 부른다 | 확정 행이 있는 종류에만 발동하고 표가 없으면 침묵한다(§6.1) |
| 새 검사의 예외가 기존 검사 결과를 지운다 | 새 검사 넷을 각각 try/catch로 격리한다(§6.2) |
| 표가 늘어 `Spec.md`가 커진다 | 표 2종으로 통합했다(D3). `CASE` 분기는 행이 많을 수 있으나 `파생 테이블 정의`가 이미 한 SP에서 63행을 낸 선례가 있다 |
