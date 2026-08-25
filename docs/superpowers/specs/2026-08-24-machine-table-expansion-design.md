# 기계 확정 표 확장 — 트랜잭션 경계와 변수 대입을 관할에 넣는다

2026-08-24. 커버리지 맵이 실측한 🟧 382건 중 **202건**을 새 기계 확정 표 둘로 닫는다.
**(→ 실측은 205건이다. 예측 202가 낡은 이유는 §3의 실측 정정 블록에 있다.)**

## 배경 — 왜 이 둘인가

[DDL 커버리지 맵](./2026-08-24-ddl-coverage-map-design.md)이 14개 SP 전수(잎 487개)를 재고
🟧(도구 사각지대) 382건의 **85%가 문장 유형 넷에 몰려 있음**을 밝혔다. 그 백로그의 ①·②가 이 설계다.

| 유형 | 건수 | 이 설계가 닫는가 |
|---|---:|---|
| `SetVariableStatement` | 100 | **예** — 97건(나머지 3건은 `LoopVariableResetExtractor`가 이미 실행 의미로 담는 자리) **(→ 실측 100, §3 정정)** |
| `RollbackTransactionStatement` | 81 | **예** |
| `BeginTransactionStatement` | 12 | **예** |
| `CommitTransactionStatement` | 12 | **예** |
| `ReturnStatement` | 105 | 아니오 — 백로그 ③. "무엇을 반환했나"가 항상 확정 사실이 아니라 설계가 더 필요하다 |
| `DeclareVariableStatement` | 40 | 아니오 — 백로그 ④ |

**①+②로 202/382(53%)가 닫힌다.** **(→ 실측 205/382, §3 정정)** 문장 수 기준 🟧이 78.4% → 약 37%로 떨어진다.

### 이 표들이 이행에 실제로 쓸모가 있는가

숫자를 낮추려고 표를 만드는 것은 Goodhart의 법칙이다. 둘 다 그 함정이 아니다.

- **트랜잭션 경계** — 코딩 에이전트 지시서가 이미 `XACT_ABORT ON` 기반 예외 처리와 롤백 충실도를
  강제한다(README 「Level 2」). `ROLLBACK`이 81건이라는 것은 오류 분기가 그만큼 촘촘하다는 뜻이고,
  배치 코드는 그 하나하나를 재현해야 한다. **어느 줄에서 열고 닫고 되돌리는지는 계약이다.**
- **변수 대입** — 변수 상태가 분기 조건을 몰고, 금액 계산의 중간값을 진다. `SET @v = @@ERROR` 한 줄이
  빠지면 그 뒤의 모든 오류 분기가 무의미해진다.

## 목표

1. `TransactionBoundaryExtractor`·`SetAssignmentExtractor` 두 추출기를 만들어 재료를 확정한다.
2. 그 재료를 프롬프트 표로 강제하고 L1이 전사를 대조한다.
3. 커버리지 맵이 그 재료를 세도록 배선한다 — **이것이 빠지면 🟧이 안 줄어든다.**

## 비목표

- **감싼 조건을 담지 않는다.** 아래 「설계 §1 — 왜 전사만 담는가」 참고.
- **`ReturnStatement`·`DeclareVariableStatement`를 건드리지 않는다.** 백로그 ③·④다.
- **재생성을 이 회차에서 돌리지 않는다.** 구현·테스트까지만. 시점과 비용은 따로 판단한다.
- **`LoopVariableResetExtractor`를 고치지 않는다.** 실행 의미 표는 그대로 둔다(§1 끝 참고).

## 설계 §1 — 표 계약

### `TransactionBoundaryExtractor`

```
### 트랜잭션 경계 (기계 확정 — 수정 금지)

| 라인 | 종류 | 이름 |
| :--- | :--- | :--- |
| 42 | BEGIN TRANSACTION | (없음) |
| 51 | COMMIT TRANSACTION | (없음) |
| 60 | ROLLBACK TRANSACTION | (없음) |
```

`public sealed record TransactionBoundaryFact(int Line, string Kind, string Name)`

`Kind`는 ScriptDom 노드 타입에서 온다 — `BeginTransactionStatement` · `CommitTransactionStatement` ·
`RollbackTransactionStatement` · **`SaveTransactionStatement`**.

> **왜 `SAVE TRAN`까지 넣는가.** 실측 코퍼스에는 0건이다. 그래도 넣는 이유는 세이브포인트가 하나라도
> 있으면 **롤백의 의미가 통째로 달라지기** 때문이다(전체 취소가 아니라 지점 복귀). 빠뜨리면 이 표가
> "트랜잭션 경계는 이게 전부"라고 **거짓말을 한다.** 네 종류를 다 담아야 표 이름이 참이 된다.

`Name`은 `BeginTransactionStatement.Name`/`SaveTransactionStatement.Name`이 있으면 원문 그대로,
없으면 `(없음)`. 변수 형태(`BEGIN TRAN @name`)도 원문 그대로 싣는다.

### `SetAssignmentExtractor`

```
### 변수 대입 (기계 확정 — 수정 금지)

| 라인 | 변수 | 대입식 원문 |
| :--- | :--- | :--- |
| 88 | @v_intResult | @@ERROR |
| 92 | @v_Cnt | @v_Cnt + 1 |
```

`public sealed record SetAssignmentFact(int Line, string Variable, string Expression)`

`SetVariableStatement` **전수**다. `Expression`은 `ScriptTokenStream` 슬라이스로 뜬 원문 그대로다.
요약·정규화·타입 추론을 하지 않는다.

> **원문 슬라이스는 자기 사본을 쓴다.** `DmlScopeExtractor.TextOf`는 그 클래스 내부 private이라
> 부를 수 없다. `DerivedTableColumnExtractor.cs:165`가 이미 같은 로직의 자기 사본을 갖고 있는 것이
> 이 코드베이스의 관례다 — `fragment.ScriptTokenStream == null` 가드를 포함해 그 모양을 따른다.

**`SELECT @v = ...` 형태는 여기 안 들어온다** — ScriptDom에서 그것은 `SelectSetVariable`이고
`AggregateAssignmentExtractor`(`:104`)·`NonAggregateAssignmentExtractor`(`:75`)가 그 타입만 본다
(코드 확인). 커버리지 맵 실측도 같은 결론이었다 — 두 추출기 재료 4건 전부 `SelectStatement` 잎에
떨어졌고 예외 0건. **관할이 겹치지 않는다.**

**`DECLARE @v INT = 15`도 안 들어온다** — `DeclareVariableStatement`다. 백로그 ④의 몫이다.

### 왜 전사만 담는가 — 감싼 조건을 뺀 이유

`ROLLBACK`이 어느 `IF` 아래 있는지(`@@ERROR <> 0` 등)를 담으면 이행 가치가 확실히 높다.
**그럼에도 담지 않는다.** README가 기계 확정 표의 원칙으로 못박은 문장 때문이다.

> 잎 타입을 하나라도 모르면 그 행은 아예 싣지 않습니다. **추측이 한 줄 섞이면 표 전체의 신뢰가
> 무너지기 때문입니다.**

감싼 조건 귀속은 틀리기 쉬운 자리다 — `ELSE` 분기, 중첩 `IF`, `BEGIN/END` 없는 단문 `IF`,
`TRY/CATCH` 안의 `ROLLBACK`. **틀린 조건이 달린 행은 조건이 없는 행보다 나쁘다.** 이 프로젝트는
이미 그 실패를 겪었다 — 감사 카탈로그의 🔴이 *"파서가 `UPDATE` 자기참조 목록을 잘못 계산했고,
모델은 그것을 충실히 옮겼고, Critic은 같은 목록으로 대조해 일치를 확인했다"*였다.

감싼 조건은 **별도 회차**에서 `IF` 술어 귀속을 제대로 설계해 붙인다. 지금 섞으면 표 둘의 신뢰가
함께 걸린다.

### `LoopVariableResetExtractor`와의 관계 — 중복이 아니라 층이 다르다

`WHILE` 최상위 상수 재설정 3건은 이미 실행 의미 표에 있다. 새 SET 표는 그 3건도 **함께 담는다.**

두 표는 다른 질문에 답한다.

| 표 | 답하는 질문 |
|---|---|
| 변수 대입(신규) | **"어떤 대입이 있나"** — 원본 전사 |
| 실행 의미(기존) | **"매 반복 다시 설정된다"** — DDL 원문이 말하지 않는 실행 시점의 사실 |

SET 표에서 3건을 빼면 표가 "전수"가 아니게 되고, 다음 사람이 **왜 이 줄만 빠졌는지**를 찾아야 한다.
전수를 지키는 편이 싸다.

## 설계 §2 — 배선 일곱 자리

| # | 자리 | 내용 |
|---|---|---|
| 1 | `src/ReSet.Core/Services/TransactionBoundaryExtractor.cs`<br>`src/ReSet.Core/Services/SetAssignmentExtractor.cs` | 신규 |
| 2 | `SpecExpectations` | 속성 2개 + `From()` 배선 |
| 3 | `MachineConfirmedTables.All` | **맨 끝에 추가** |
| 4 | `AiService.BuildMachineFactBlockLines` | 표 뼈대 렌더 2개 |
| 5 | `MechanicalValidator` | 검사 2개 + `ErrorType` 2개 + 검사 목록 등록 |
| 6 | `CacheManager.CurrentCacheFormatVersion` | 15 → 16 |
| 7 | `CoverageMapComposer.ExtractorFactLines` | 새 컬렉션 2개 |

**3번 — 순서를 흔들지 마라.** `MachineConfirmedTables.All`의 주석이 못박고 있다:
*"목록의 순서가 곧 Critic 프롬프트에 실리는 순서다. 프롬프트 접두사 캐시가 바이트 일치로 걸리므로
순서를 흔들지 마십시오."* 맨 끝에 append한다.

**4번 — 단일 진입점이 이미 지켜지고 있다.** `BuildMachineFactBlockLines`의 호출부는 5곳
(`AiService.cs:469` · `1814` · `2945` · `3085` · `3259`)이고 전부 그 하나를 통한다. 그 함수 안에
넣으면 SP·함수·분할 생성 갈래에 자동으로 실린다. 이 구조 자체가 *"표 하나가 늘 때 한 갈래만 조용히
못 받는 회귀"*를 막으려고 만들어진 것이다(`MachineFactPresentation` 3상태 주석 참고). **밖에서
직접 배선하지 마라 — 진입점이 둘이 되는 순간 그 보호가 사라진다.**

**7번이 빠지면 아무것도 안 깨지면서 목적만 달성 안 된다.** 표는 생기는데 커버리지 맵이 그 재료를
안 세어 🟧이 그대로다. 이것을 잡는 테스트를 반드시 둔다(§4).

두 표 모두 `## 로직 흐름 요약` 소관이라 `CASE 분기`의 **디스패치 메커니즘**을 재사용한다 —
`MachineFactPresentation` 3상태로 갈라지는 그 구조와, L1 검사 모양(`CheckCaseBranches`)을 따른다.
그 선례는 14개 SP에서 검증됐으므로 새 판단을 만들지 않는다.

> **[2026-08-24 Task 7b 문구 정정 — "그대로 따른다"가 오독을 낳았다]** 원문은 *"`CASE 분기`의
> 표시 규칙(`MachineFactPresentation` 3상태)과 L1 검사 모양(`CheckCaseBranches`)을 그대로
> 따른다"*였다. Task 4b 워커가 이것을 *"`CASE 분기`가 하는 **모든 것**을 해야 한다 — Reference
> 변형 콘텐츠까지 내야 하나?"*로 읽고 질의를 올렸다(계획서 Step 4는 Table 렌더 둘만 처방했고,
> 워커는 계획대로 하고 근거를 주석에 남긴 뒤 보고했다 — 옳은 처리였다). 리뷰 판정은 **결함 아님,
> 정당한 범위 경계**였다.
>
> **의도한 뜻은 "3상태로 갈라지는 디스패치 메커니즘을 재사용하라"이지 "세 상태 모두에 대해
> 콘텐츠를 만들어라"가 아니다.** 새 표 둘은 **Reference 변형이 필요 없다.** 다만 그 근거는
> **두 축**이고 코드가 그 둘을 갈라 둔다 — 하나로 뭉치면 다음 표 저자가 **틀린 기준을 물려받는다.**
>
> **축 1 — 왜 "표"가 아니라 "참고 재료" 형태인가.** 그 갈래가 **목적지 H2를 소유하지 않아서**다
> (`BuildCaseBranchReferenceMaterialLines` docstring). `BuildCaseBranchTableLines`의
> *"Copy this table verbatim into `## 로직 흐름 요약`"* 지시를 H2 하나만 허용받은 갈래
> (예: 지역 모델 `CrudAnalysis`)에 그대로 주면 **모델이 쓰면 안 되는 헤딩까지 합성한다.**
> 형태를 바꾸는 이유는 이것뿐이고, 셀 충돌과는 무관하다.
>
> **축 2 — 왜 `Omit`이 아니라 `Reference`인가.** 일반 기준은 `MachineFactPresentation.Reference`의
> enum 주석이 말한다: *"이 갈래는 목적지 H2를 쓰지 않지만 **산문이 이 사실을 서술할 수 있다**."*
> `CrudAnalysis`에서 이 기준이 **구체화된 모습**이 「원천 표현식 (SET)」 셀 충돌이다 — 그 갈래가
> 요구하는 1:1 소스값 매핑 표에 `SET Col = CASE WHEN … END`처럼 `CASE` 원문이 그대로 들어갈 수
> 있어 그 갈래가 이 사실을 실제로 서술하기 때문이다. **셀 충돌은 기준이 아니라 기준의 한 사례다.**
>
> 새 표 둘은 **축 2에서 걸러진다.** 축 1은 물을 필요가 없다 — 실을 콘텐츠 자체가 없기 때문이다.
>
> - **트랜잭션 경계**는 제어문이라 `CrudAnalysis`가 요구하는 소스값 매핑에 나타날 수 없다.
> - **`SET @v = expr`**는 `UPDATE`의 `SET` 절과 **별개 AST 노드**라 그 셀에는 변수명만 담긴다.
> - `Table`을 받지 않는 갈래의 산문이 두 사실을 서술할 자리가 없으므로 **`Omit`과 같은 결과**가
>   맞다. 실제 구현도 `Table`일 때만 싣는다.
>
> 이건 사후 정당화가 아니라 설계 판단의 기록이다. **새 표를 또 더하는 사람이 물어야 할 것은 셀
> 충돌이 아니라 enum 주석의 일반 기준이다** — *"`Table`을 받지 않는 갈래 중에, **그 갈래의
> 산문이 이 사실을 서술할 수 있는** 갈래가 하나라도 있는가?"* 있으면 `Reference`가 필요하고
> (형태는 축 1이 정한다), 없으면 `Omit`이다. 셀 충돌은 그 질문에 "있다"고 답하는 **여러
> 방식 중 하나**일 뿐이다 — 그것만 확인하고 없다고 결론 내리면 놓친다.

## 설계 §3 — 구현 직후 🟥이 202건으로 튄다

**이것은 회귀가 아니라 예정된 중간 상태다.** 미리 적어 두지 않으면 다음에 맵을 여는 사람이
사고로 오인한다.

```
지금        재료 없음 + 앵커 없음 = 🟧 관할 밖        382건
구현 직후   재료 있음 + 앵커 없음 = 🟥 명세서 결함    202건  ← 이 창이 열린다
재생성 후   재료 있음 + 앵커 있음 = 🟩 정합
```

> **[2026-08-24 Task 7b 실측 정정] 창은 예측대로 열렸고, 건수는 202가 아니라 205다.**
> 이 절의 제목과 위 도식의 `202건`은 **예측값 그대로 보존한다** — 예측과 실측의 차이 자체가
> 기록할 값어치가 있다. 오늘 유효한 수는 **205**다.
>
> 실측 조건: 14 SP 코퍼스 전수(`output/Procedures`), 커밋 `abbc0c6`(Task 7 배선 포함).
> Task 7 리뷰어가 `81b2af1`(배선 전)·`ebbd066`(배선 후) 양쪽에서 먼저 분해했고, **Task 7b가
> 같은 코퍼스에서 독립 재현했다**(아래 수치는 재현으로 확인한 것이다).
>
> | 새 표 | 대상 잎 | 🟥 |
> |---|---:|---:|
> | 트랜잭션 경계 | `BEGIN` 12 + `COMMIT` 12 + `ROLLBACK` 81 = 105 | **105** (전부) |
> | 변수 대입 | `SetVariableStatement` 103 | **100** (3건 제외) |
> | 합계 | | **205** |
>
> 트랜잭션 경계 105는 설계서 추정 105와 **정확히 일치한다.**
>
> **3건 차이의 근원 — 이번 브랜치의 결함이 아니라 상류 백로그의 뺄셈 중복이다.** 예측 202는
> `105 + 97`이었고, 그 97은 DDL 커버리지 맵 설계서(`2026-08-24-ddl-coverage-map-design.md`)
> 「🟧 백로그」 표의 `SetVariableStatement` 행에서 왔다. 그 행의 「건수」 칸 **100은 이미
> 3건을 뺀 🟧 개수**다(같은 문서가 잎을 103건으로, 재료가 붙는 것을 3건으로 실측해 적어 두었다).
> 그런데 같은 칸의 서술이 거기서 3을 **한 번 더** 빼 *"나머지 97건 대부분을 닫을 수 있다"*고
> 적었고, 이 문서 §1 표(`| SetVariableStatement | 100 | 예 — 97건 … |`)와 §4·§5의 "SET 97"이
> 그 97을 그대로 물려받았다. 즉 **새 추출기는 오동작하지 않았다** — 의도한 메커니즘은 정확히
> 작동했고, 빗나간 것은 상류 추정 산술뿐이다.
>
> **방증 — 저자가 105 + 100을 실제로 더한 자리에서는 205가 나왔다.** 이 문서 「위험」 절의
> *"프롬프트가 길어진다. 코퍼스 기준 **205행**이 늘어 객체당 평균 약 15행이다"*가 그것이다
> (205 = 트랜잭션 105행 + SET 100행, 205/14 ≈ 14.6). **오직 🟥 예측 경로만 두 번 뺀 97을
> 썼다** — 산술 착오가 한 자리에 국한됐다는 증거이고, 실측 205가 옳다는 독립 확인이다.
>
> **🟥이 아닌 SET 잎 3건 — `dbo.UP_UTIL_SETTLE_PROC_ETC`의 69·113·114행.** `WHILE` 최상위
> 상수 재설정이라 기존 「실행 의미 (기계 확정 — 수정 금지)」 표 앵커가 이미 그 줄들을 짚고 있어
> 🟩을 유지한다(앵커 출처 문자열을 직접 찍어 확인했다). **이것은 §1 「`LoopVariableResetExtractor`와의
> 관계」의 결정과 모순이 아니다.** 그 절은 새 SET 표가 **그 3건도 함께 담는다**(전수)고 정했고,
> 이 관측은 커버리지 맵 층의 이야기다 — 표는 3건을 싣고, 맵은 그 줄이 *다른 표로도* 이미
> 앵커되어 있으므로 🟥으로 세지 않는다. §1이 "중복이 아니라 층이 다르다"고 말한 그 두 층이
> 여기서 그대로 갈라져 보이는 것이다.
>
> **회귀 잠금.** `CoverageMapGoldenTests.Requirement1_CurrentEdition_SpecMissingShouldMatchTransitionWindowCount`가
> 205를 못박는다. 원래 이름은 `…_SpecMissingShouldBeZero`였고 원래 계약은 "🟥 총계 0"이었다 —
> 0을 걸지 않는데 이름이 `ShouldBeZero`로 남으면 이름이 거짓말이 되므로 함께 고쳤다. **205는
> 재생성 전까지만 유효한 임시값이다**: 재생성이 돌면 이 단언을 그때의 실측값으로 내리고, 0에
> 도달하면 원래 이름·원래 계약으로 되돌린다. 그 지시는 단언 실패 메시지 안에 실려 있다.

맵은 정확히 참을 말하고 있다 — **"도구가 이제 이 사실들을 아는데 명세서엔 아직 없다."** 재생성을
이 회차에서 돌리지 않기로 했으므로 그 창이 실제로 열린다.

> **[2026-08-25 재생성 실측 — 전이 창이 닫혔다] 🟥 205 → 0.**
>
> 캐시 16 전건 재생성을 돌렸다(방법은 README의 「소비 SP 12개를 4그룹 병렬 재생성」, 캐시를
> 올린 상황이므로 그 절이 단 조건대로 공용 UDF 4종을 `INS_EXTRA` 단독 실행으로 먼저 데운 뒤
> G1·G2·G3′·G4를 병렬로 돌렸다 — G3′는 `INS_EXTRA`를 뺀 구성이다). 31객체 전부 `FormatVersion`
> 16으로 넘어갔고, 커버리지 맵 🟥 총계가 **205에서 0으로** 내려갔다. §3이 예측한 세 상태 전이
> (🟧 → 🟥 → 🟩)가 예정대로 완주했다.
>
> 회귀 잠금은 원래 형태로 되돌렸다 — `TransitionWindowSpecMissing` 상수를 지우고 메서드 이름을
> `Requirement1_CurrentEdition_SpecMissingShouldBeZero`로, 단언을 `총계 == 0`으로 복원했다.
> 그 지시는 임시 단언의 실패 메시지가 스스로 담고 있던 것이고, 실제로 그 메시지가 떠서 따랐다.

세 가지로 대응한다.

1. 이 절 자체가 기록이다.
2. **HTML 상단 각주** — 캐시 버전과 명세서 생성 시점이 어긋나면 🟥이 이렇게 보인다는 것을 적는다.
3. **완료 기준을 정직하게 쓴다**(§5). 재생성을 안 하므로 "🟧 78.4% → 37%"를 완료 기준으로 걸 수 없다.

## 설계 §4 — 테스트

**층 1 — 커밋된 합성 픽스처.** `output/` 의존이 없어 CI에서 결정적이다.

- 트랜잭션: 중첩 `BEGIN TRAN` · 이름 있는 트랜잭션 · **`SAVE TRAN`** · `COMMIT`/`ROLLBACK` 혼재 ·
  파스 실패 시 빈 목록
- SET: `SET @v = <상수>` · `SET @v = @v + 1` · `SET @v = @@ERROR` · 함수 호출이 든 식 ·
  파이프(`|`)가 든 식의 셀 이스케이프 왕복
- **`SELECT @v = ...`가 SET 표에 안 들어오는지** — 관할 경계를 잠근다
- **`DECLARE @v INT = 15`가 SET 표에 안 들어오는지**
- L1 검사: 표 없음 / 행 없음 / 행 있음
- `MachineConfirmedTables.All`에 새 헤딩 2개가 있는지 — Critic 면제가 자동으로 따라오는지
- **`CoverageMapComposer`가 새 재료를 센다** — 7번 배선을 잠그는 테스트. 없으면 배선이 빠져도
  아무것도 안 깨진다

**층 2 — `SkippableTheory` 코퍼스 스모크.** 실물 14 SP에서 폭주 없이 돌고, **실제 건수를 출력**해
백로그 예측(트랜잭션 105 · SET 97)과 대조한다. **예측이 빗나가면 그 자체가 보고 내용이다** —
숫자를 맞추려고 추출기를 조정하지 마라.

**되돌림 확인을 요구한다.** 이 프로젝트에서 무의미 테스트가 일곱 번 나왔다. 각 회귀 테스트는
해당 동작을 되돌렸을 때 실제로 깨져야 하고, 확인 방법을 보고에 적는다.

## 위험

**L1이 너무 빡빡하면 재생성이 영영 통과 못 한다.** README가 L1 기준값을 의도적으로 좁게 두는
이유이고, 이 프로젝트는 이미 그 교착을 세 번 겪었다(`MachineConfirmedTables.CriticExemptionBlock`
주석). `CASE 분기` 선례가 조건 원문 완전일치를 요구하면서 실제로 통과하고 있으므로 같은 강도로
간다. **재생성 실측에서 반려가 나면 기준을 낮추기 전에 원인부터 본다.**

**프롬프트가 길어진다.** 코퍼스 기준 205행이 늘어 객체당 평균 약 15행이다. 토큰 비용은 늘지만
`SchemaPromptColumnSelector`가 절약하는 규모에 비하면 작다.

## 코퍼스 전수 스윕 실측 (2026-08-25, Task 6)

BASE는 `axis-b-step-check`(`6bc3641`), NEW는 Task 5 커밋(`889958e`)이다. 계획서는 BASE를
"Task 1 직전 SHA"(`fa2bf9d`)로 적었지만 그 뒤 이 브랜치가 축 B를 병합해 `MechanicalValidator`가
따로 바뀌었다 — 그 SHA를 쓰면 축 B의 검증기 수정이 차분에 섞여 "옆 검사에 번졌는가"를 읽을 수
없다. 그래서 **축 B 병합분은 같고 확장분만 다른** 지점을 BASE로 잡았다.

세대 왜곡 없음: `output/.sp_cache_index.json` 분포 `{15: 31}` · 브랜치 코드 15 · main 15.

```
코퍼스 31쌍 · 로드 실패 0 · null expectations BASE 0 → NEW 0
  TransactionBoundaryTableMissing: 12건 (전부 "표 부재", 행 어긋남 0)
    UP_Util_PG_Client_CMRate_Ins · UP_UTIL_SETTLE_CANCEL_INS · UP_UTIL_SETTLE_COMM_UPD
    UP_UTIL_SETTLE_EXCEPTION_PROC · UP_UTIL_SETTLE_EXPECT_PROC · UP_UTIL_SETTLE_INS
    UP_UTIL_SETTLE_INS_EXTRA · UP_UTIL_SETTLE_INS_EXTRA4PLCARD · UP_UTIL_SETTLE_PROC_ETC
    UP_Util_Settle_Summary · UP_UTIL_SETTLE_SUMMARY_ETC · UP_UTIL_STAT_PGCOLLECT_INS
  SetAssignmentTableMissing:       27건 (전부 "표 부재", 행 어긋남 0)
    위 12개 중 11개(SUMMARY_EXTRA·Summary_AcqManual 추가, PGCOLLECT 포함) + UF_* 함수 13개
  다른 검사 카운트: BASE와 동일 — DmlScopeTableMissing 1건(UP_UTIL_SETTLE_COMM_UPD,
                   축 B가 이미 알고 있는 술어 컬럼 칸 결함)이 BASE·NEW 양쪽에 똑같이 있다
```

**거짓 양성 0.** 39건 전부 `기계 확정 … 표가 명세서에 없습니다` 한 종류이고, 코퍼스의
`Spec.md`는 이 표들이 생기기 전 프롬프트로 만들어진 것이라 실제로 그 표가 없다 — §3이 예고한
전이 상태의 실물 관측이지 오탐이 아니다. **행 어긋남(전사 불일치) 유형은 0건**이므로 대조
로직이 정상 산출물을 결함으로 지목한 사례는 하나도 없다.

`null expectations`는 BASE에서 이미 0이라 체인 확장의 효과가 이 코퍼스에서는 관측되지
않는다 — 31개 객체 전부가 다른 재료를 이미 갖고 있다. 체인 항이 일하는 것은
`SpecExpectationsTransactionAndSetTests`의 재료 하나짜리 픽스처가 대신 잠근다.

## 미확정 사항 — 구현 첫 단계에서 확인한다

1. ~~**실제 건수가 백로그 예측과 맞는가.**~~ **확인됨(2026-08-25).** SP 코퍼스 14개 실측:
   **트랜잭션 105 · SET 103**(`MachineTableExpansionCorpusTests` 출력). 트랜잭션은 예측 105와
   **정확히 일치**한다(`SAVE TRANSACTION` 0건 가정도 맞았다). SET은 예측 97과 **6 차이**인데
   추출기의 오동작이 아니다 — 상류 백로그의 `SetVariableStatement` 「건수」 칸이 잎 103에서 3을
   빼 100을 적고, 같은 칸 서술이 거기서 3을 **한 번 더** 빼 97을 적었다(뺄셈 중복). 잎 103 ·
   🟥 100 · 합계 205라는 분해는 `CoverageMapGoldenTests.TransitionWindowSpecMissing`의 주석에
   이미 실측으로 적혀 있고, 이번 스윕이 그 103을 독립적으로 재확인했다. **숫자를 맞추려고
   추출기를 조정하지 않았다.**
   함수까지 포함한 전 코퍼스(31쌍) 기준으로는 트랜잭션 105 · **SET 141**이다(함수 13개가 38행).
   커버리지 맵의 205는 14개 SP만 보므로 이 141과 직접 비교할 값이 아니다.
2. ~~**`Expression` 원문에 파이프·개행이 든 사례가 실물에 있는가.**~~ **확인됨(2026-08-25):
   실물 0건.** 따라서 셀 이스케이프 왕복(`MarkdownTableCellCodec.Escape`↔`SplitRow`)은
   **합성 픽스처가 유일한 근거**로 남는다 — `SetAssignmentExtractorTests`의 파이프·개행
   픽스처가 그 자리를 지킨다. 실물에 그런 대입식이 처음 들어오는 회차가 이 왕복의 첫 실전
   시험이다.
3. ~~**L1 완전일치가 실물 재생성에서 통과하는가.**~~ **확인됨(2026-08-25) — 통과한다.**
   캐시 16 전건 재생성에서 새 두 검사(`CheckTransactionBoundaries`·`CheckSetAssignments`)가
   **한 번도 발동하지 않았다.** 대조된 재료는 SP 14개 전수 기준 트랜잭션 **105행** · SET
   **103행**이다.

   같은 재생성에서 L1은 실제로 **14라운드** 반려했고 오류 항목은 **56건**이었다 — 전부 기존
   검사다.

   | 검사 | 항목 수 |
   |---|---:|
   | `CheckSchemaClaims` | 42 (전부 `Ins_Extra4PLCard` 한 객체) |
   | `CheckParameterTableRows` | 6 |
   | `CheckParameterColumnClaims` | 4 |
   | `MachineTableShapeBroken` | 4 |
   | **`TransactionBoundaryTableMissing`** | **0** |
   | **`SetAssignmentTableMissing`** | **0** |

   **가장 강한 증거는 "조용했다"가 아니라 "같은 문서에서 다른 검사는 울렸다"는 것이다.**
   `CANCEL_INS`·`Ins_Extra4PLCard`·`EXCEPTION_PROC`·`PROC_ETC`·`COMM_UPD`는 L1이 실제로
   반려한 문서인데, 그 문서들에서 파라미터 표·스키마 주장·「DML 범위」 표 칸 수는 틀린 채로
   트랜잭션·SET 표만은 매 시도마다 정확했다. 검사가 조용한 이유가 "안 돌아서"가 아니라
   "옮겨 적기 지시가 실제로 작동해서"임을 가른다.

   곁가지 관측 하나 — `MachineTableShapeBroken`이 10칸짜리 「DML 범위」 표에서 4건 났는데
   3칸짜리 새 표 둘에서는 0건이다. **표가 좁을수록 전사가 정확하다**는 신호이고, 다음 표를
   설계할 때 칸을 늘리기 전에 한 번 더 생각할 근거다.

   재시도가 난 소비 SP는 5개다(`CANCEL_INS` 3라운드 · `Ins_Extra4PLCard` 3 · `EXCEPTION_PROC` 2
   · `PROC_ETC` 2 · `COMM_UPD` 1). 나머지 7개는 **1차 분석에서 곧바로 L1을 통과**했고, 그중에는
   표가 가장 큰 축에 드는 `CMRate_Ins`(12·11)·`EXPECT_PROC`(13·11)·`Summary`(12·9)가 들어 있다.
   재시도 소진(`L1Exhausted`)은 한 건도 없었다.

## 완료 기준

- [ ] 추출기 둘이 합성 픽스처에서 계약대로 동작한다(파스 실패 시 빈 목록 포함)
- [ ] 배선 일곱 자리가 전부 들어갔고, 7번을 잠그는 테스트가 있다
- [ ] `MachineConfirmedTables.All`에 새 헤딩 2개가 **맨 끝에** 있다(순서 불변)
- [ ] 코퍼스 스모크가 실제 건수를 출력하고, 백로그 예측(105 · 97)과의 차이가 기록됐다
- [ ] 커버리지 맵 실행 시 🟥이 늘어난 것이 **§3의 예정된 중간 상태**임이 산출물 각주와 이 문서에 있다
- [ ] `dotnet clean && dotnet build`의 `warning CS` 유일 건수가 0, `dotnet test` 실패 0
- [ ] 재생성은 돌리지 않았고, 그 사실과 미확정 사항 3번이 문서에 남았다
