# 통합 배치 계획 규칙의 강제 수단 전수 조사 (2026-08-29)

`ConsolidatedPlanRules`의 규칙 하나하나가 **무엇으로 강제되는가**를 전수로 셌다.
물음은 「규칙이 있는가」가 아니라 **「모델이 바뀌어도 지켜지는가」**다.

계기: 4단계 2차 통제군(`POQSettleBatch3`)에서 L1이 **아무것도 발화하지 않았는데**,
그 문서에는 우리가 이미 아는 위반이 둘 있었다 — 규칙 3-1이 금지한 API 지정 11건,
`S05`의 T-SQL 잔존. **침묵이 깨끗함이 아니라 실명(失明)이었다.**

선행: `docs/superpowers/specs/2026-08-27-stage3-rule-rewrite-design.md` §9·§10

## 0. 측정 조건

- 소스를 직접 읽어 셌다. 산출물 표본이 아니라 **코드가 부르는 검사 목록**이다.
- 대상: `src/ReSet.Core/Services/MechanicalValidator.cs`(검사 정의 49개),
  `AiService.cs`의 `ConsolidatedPlanRules`(규칙 번호 21개 · 하위 조항을 갈라 24행).
- 잰 시각의 HEAD는 `dd6041e`. 코퍼스는 이 조사에 쓰지 않았다.
- 「기계 강제」는 **L1만** 센다. 프롬프트와 Critic은 둘 다 모델 재량이므로 이 조사의
  분모가 아니라 분자를 만드는 쪽이다.

## 1. 강제 수단은 세 경로, 그중 둘이 이 축이다

| 진입점 | 검사 수 | 받는 것 | 이 조사의 대상 |
|---|---:|---|:--|
| `Validate(markdown, expectations)` | 26 | SP 명세서 | ✘ 축 B의 것 |
| `ValidateConsolidated(markdown)` | **4** → **8**(A급 셋 + B급 4 이행) | 계획서 본문**만** | ✔ |
| `ValidateBatchStep(...)` | **16** | 단계 + `SpecExpectations` | ✔ |

`ValidateConsolidated`가 받는 것이 `string` 하나뿐이라는 사실이 §4의 설계 제약을 낳는다.

**L1 실패는 진짜 강제다.** `VerificationPipelineOrchestrator.cs:2006`이 `!IsValid`면
`ComposeAfterL1Failure`로 자가 수정을 되돌린다(:2013). 보고가 아니라 되돌림이고,
정규식 판정이라 **모델과 무관**하다.

## 2. 규칙 21개 × 기계 강제

✔ 온전 · ◐ 부분 · ✘ 없음

| 규칙 | 요구 | 강제 | 검사 |
|---|---|:--:|---|
| 1 | Korean Markdown | ✘ | |
| 2 | H2 넷 | ✔ | `ValidateMarkdownStructure` |
| 2-a | 검증 SQL의 `CROSS JOIN` 금지 | ✔ | `CheckVerificationCartesianComparison` |
| 2-b | 의사코드는 앱 코드다 | ✘ | |
| 3 | 순서 보존·병렬 금지 | ✘ | |
| **3-1** | **SQL 거처** — 신규 SP·제어 흐름·API 금지 | ✔ | 제어 흐름 `CheckSqlSideControlFlow`·API `CheckPrescribedFrameworkType`·신규 객체 `CheckNewDatabaseObjectDefinition` (2026-08-29 이행) |
| 4 | SNAPSHOT 격리 의무 | ✘ | |
| 4 | Shadow 역학 (a)~(e) | ◐ | `CheckShadowBackupContract` |
| 4-1 | `batch`·`batch_shadow` 스키마 | ✔ | `CheckNonCanonicalBatchSchema` |
| 5 | 재시작 파라미터 추가 금지 | ✔ | `CheckStepInterface` |
| 6 | 청크 DELETE에 청크 키 | ✘ | |
| 6-1 | 실패 지점 충실도 | ◐ **위태** | `CheckLegacyReturnCodeBinding` + §3의 둘 |
| 6-2 | 제어 단계 예약 코드 대역 | ✔ | `CheckControlStepErrorCodeBand` |
| 7 | 비즈니스 로직 축약 금지 | ✔ | `CheckMissingConditionColumns`·`CheckStatementCountAgainstSpec`·`CheckAnchoredStatementFacts`·`...Extras` |
| 7-1 | UNION 분기 컬럼 정렬 | ✘ | |
| 8 | 청크 필터 보존 | ◐ | `CheckMissingConditionColumns` 부분 |
| 8-1 | 청크마다 자기 트랜잭션 | ✘ | |
| 9 | 원본 오류 코드 재사용 | ✔ | 레거시 없는 단계 `CheckControlStepErrorCodeBand` · **있는 단계 `CheckLegacyStepErrorCodeInvention`** (2026-08-30 이행) |
| 10 | `NOLOCK` 금지 | ✔ | `CheckNoLockHints` (2026-08-29 §5 이행) |
| 11 | INSERT 전용 롤백 | ◐ | `CheckShadowBackupContract` 부분 |
| 12 | 청크 키가 실재하는 컬럼인가 | ✘ | 테이블은 보나 **컬럼은 안 본다** |
| 13 | 출력 파라미터 매핑 | ◐ | `CheckStepInterface` 부분 |
| 14 | 전체를 코드블록으로 감싸지 말 것 · mermaid 블록 사용 | ◐ | 직접 검사 없음. `PostProcessMarkdown`은 **mermaid 안쪽만 정화**하고(`:5776`), 전체를 감싸면 H2 검사가 간접적으로 걸린다 |
| 15 | 잡담 금지 | ✘ | |

**온전 6 · 부분 7 · 전무 11** (하위 조항을 갈라 24행).
규칙 번호로는 21개이고, 규칙 2와 4가 성격이 다른 조항을 품고 있어 표에서 갈랐다.

> 이 셋은 **잰 시각(`dd6041e`)의 값이다.** A급 셋과 B급 4를 이행한 뒤로는
> **온전 8 · 부분 7 · 전무 9**다 — 규칙 10과 규칙 3-1이 둘 다 전무 → 온전.
> 위 표의 그 두 행은 이행 후 값으로 갱신했다.
>
> **2026-08-30 규칙 9까지 이행해 온전 9 · 부분 6 · 전무 9다**(부분 → 온전).

> ⚠️ **부분(◐)을 「있다」로 읽지 말 것.** 규칙 9가 대표적이다 — 강제되는 것은
> 「레거시 출신이 **없는** 단계가 예약 대역을 지키는가」뿐이고, 규칙 9의 본문인
> 「레거시 출신 단계가 **원본 코드를 그대로 쓰는가**」는 아무도 안 본다.
> 2차 통제군에서 Critic이 `S11`·`S13`의 오류 코드 발명을 잡아냈는데, 그것은
> **모델이 잡은 것**이지 기계가 잡은 것이 아니다.
>
> ✅ **이행했다 (2026-08-30, `d9a455e1`).** `CheckLegacyStepErrorCodeInvention`이
> `ValidateBatchStep`에 들어갔고 `CheckControlStepErrorCodeBand`의 정확한 여집합이다.
> **규칙 9는 이제 ◐가 아니라 ✔이다.**
>
> **실측**(`output/`): 판정 194 단계 · 재료가 없어 지나간 단계 1 · **발화 10 · 오탐 0**
> (대입 지점 전량 확인). `-9` 7건은 원본이 정의한 적 없는 코드를 문서 전역 관례로
> 승격한 것이고, `1000` 3건은 `UP_UTIL_SETTLE_SUMMARY_ETC`(0·1001·1002)에 없는 값이다.
>
> **오라클의 실제 출처를 못박아 둔다** — `codesByProcedure`는
> `SpecReturnCodeExtractor.Extract(specs)`(**명세서 산문**)이다
> (`VerificationPipelineOrchestrator:1809 → :1911/:2381 → :3059 → :3415`).
> 「오류 코드(기계 확정 — 수정 금지)」 표를 읽는 `ReadErrorCodeToOrdinal`이 먹이는 것은
> **다른 매개변수**(`statementFactsByProcedure`)다. 둘을 섞으면 분모가 달라진다 —
> 표를 가진 명세는 12/14이지만 산문이 코드를 내는 명세는 **13/14**이고, 실행 중
> 침묵한 단계는 195개 중 **1개**뿐이다. 그 하나(`UP_Util_Settle_Summary_AcqManual`)는
> 잃은 재료가 아니라 **원본의 성질**이다 — 그 SP는 오류를 음수 코드가 아니라
> `ERROR_NUMBER()`로 낸다고 명세가 스스로 적는다.
>
> **펜스 리더를 새로 뒀다.** `CleanedSqlFences`는 ```sql만 본다. 3단계 이후 오류 처리는
> 앱 코드로 옮겨가 `pseudocode`·`csharp` 펜스에 실리고, 실제로 잡힌 `1000` 3건이
> `csharp` 안이다 — sql만 보는 리더였다면 이 축에서 통째로 눈멀었다. 기존
> `CleanedCodeFences`는 mermaid를 포함하므로 고치지 않고 좁은 짝을 뒀다.
>
> ⚠️ **안 닫은 오탐 계열 하나.** 명세가 「`-5`를 반환합니다」로만 적고 `@po_intRetVal`
> 철자를 안 쓰면 추출기가 못 뽑는다. 4단계 3차 통제군 트리에서 `POQSettleBatch3/S12`가
> `-5`~`-8`을 고발하는데 **전부 오탐**이고, 그 명세엔 기계 확정 표도 없어 두 번째
> 오라클도 못 덮는다. 현행 `output/`에도 `UP_UTIL_SETTLE_COMM_UPD`의 `-15`가 같은
> 형태로 남아 있다.
>
> ⛔ **넓히지 않기로 했다 (2026-08-30, 사용자 결정). 위 문단의 「잡음이 0」은 틀렸다.**
>
> **`-15`는 진짜 코드가 아니다.** `UP_UTIL_SETTLE_COMM_UPD` 명세가 스스로 적는다 —
> 「상기 블록은 `/* ... */`로 감싸져 비활성 상태입니다 … **본 프로시저의 활성 코드에는
> `-15` 오류 코드가 존재하지 않습니다**」(`Spec.md:103`·`:609`·`:622`).
>
> 그리고 **서술형 패턴은 살아있는 코드와 죽은 코드를 문법으로 못 가른다.**
>
> ```
> 진짜   「오류 시 롤백하고 `-5`를 반환합니다」            ← 활성 분기
> 죽은   「활성화될 경우 오류 코드 `-15`를 반환하도록」    ← 주석 처리된 블록
> ```
>
> **폭발 반경 실측.** `PlanStructureEnricher`는 `ErrorCodes`를 **합집합**으로 채우고
> (`:247`), 하한 검사가 그 코드를 단계 본문에서 요구하며(`MechanicalValidator:422`),
> 그 실패는 **되돌림**이다.
>
> ```
> output/              레거시 단계 195 · 더해질 코드 16 · 새 L1 실패 15  ← 전부 죽은 -15
> output.bak-…batch4   레거시 단계  38 · 더해질 코드 22 · 새 L1 실패  8  ← S12/S13 귀속 문제
> ```
>
> **결정적**: 현행 명세 생성분에는 이 구멍이 **없다.** 14편의 서술형 전용 코드는
> `-15`(죽음)와 `0`뿐이고, 진짜 서술형 코드(`-5`~`-8`)는 **옛 생성분에만** 있다 —
> 같은 SP를 현행 명세는 `@po_intRetVal = -5`로 적는다. **닫으려던 구멍이 현행
> 파이프라인에 없다.**
>
> 남는 노출은 「다음 명세 생성분이 다시 서술형으로 쓰는 경우」이고, 그때는 침묵이
> 아니라 **오탐 발화**로 나타난다 — 발화 목록의 코드를 원본 명세에서 한 번 열어 보면
> 갈린다(`LegacyErrorCodeInventionCorpusTests`가 목록을 찍는다).
>
> **되살리려는 사람에게**: 넓힐 거면 「재료 = `0`이 아닌 코드가 하나 이상」으로 함께
> 좁혀야 한다 — 안 그러면 `AcqManual`이 `0` 하나만 든 키를 얻어 그 단계의 모든 음수
> 대입이 발화한다. 그러나 **먼저 위 15건을 어떻게 할지부터 답해야 한다.**
>
> ~~⬆️ **A급으로 올릴 것을 제안한다 (2026-08-29, `2026-08-29-critic-exception-axis.md` §8).**~~
> 3차 통제군에서 같은 부류가 재발했고 **채택본에 살아남았다.** 축 4는 그 판의 **유일한**
> 불합격 사유였다. 방향을 뒤집으면(대입 코드 ∉ 명세 코드) 설계서 §3의 정지 조건
> (「정당한 미대입」 96.9% 오탐)이 걸리지 않는다 — 그것은 순방향의 문제였다.
> **실측**: `output/Jobs` 레거시 출신 168단계 발화 8건, 대입 지점 전량 확인, **오탐 0**.
> 3차 통제군 채택본에서 1건 발화(`S04`의 `-2`)이고 **Critic이 채택 회차에 놓친 것**이다.
> 재료는 이미 있다(`SpecReturnCodeExtractor` + 「오류 코드(기계 확정 — 수정 금지)」 표).
> **`Steps[].ErrorCodes`를 오라클로 쓰지 마라** — 발명과 선언이 함께 움직인다(§7).

## 3. 이미 켜져 있으나 꺼질 수 있는 검사 셋

**「좋은 일이 방어를 끈다」의 형태다.** 축 B 로드맵이 내내 경계한 것이고, 한 번은
실현됐다.

| 검사 | 무엇에 묶여 있나 | 상태 |
|---|---|---|
| `CheckStepIdInitialValue` | `DECLARE @v_currentStepId INT = 0` — T-SQL 철자 **+ `step.ErrorCodes`** | **침묵 확정. 재정박 안 함** — 아래 ⛔ 상자 |
| `CheckCatchDiscardsReturnCode` | `CATCH` 블록의 존재 | **침묵 확정. 재정박 안 함** — 의무는 다른 검사가 덮는다 |
| `CheckSpecLocalVariablesDeclared` | 명세서 「지역 변수 표」 | **이미 18 → 0으로 꺼졌던 전례**(`known-defects.md` 5-3-7) |
| `CheckUnknownTableReferences` | 스키마 카탈로그가 비면 **조용히 건너뛴다**(`:1891`) | 조건부 침묵 |

앞의 둘은 3단계·이번 회차가 프롬프트에서 그 철자를 걷어냈으므로 **지금 침묵하고 있을
가능성이 높다.** 2차 통제군에서 L1 발화가 0이었으나, 그것이 「깨끗」인지 「침묵」인지
**가르지 않았다.** 가르는 것이 이 조사의 후속 과제다.

> ⬆️ **`CheckStepIdInitialValue`의 닻은 셋이다 (2026-08-29 확인,
> `2026-08-29-critic-exception-axis.md` §9-1).** 철자만 보고 재정박하면 못 고친다.
> ① `step.ErrorCodes`가 비면 `:6439`에서 **즉시 return** — 코퍼스 실측 **94/499 단계(18.8%)**가
> 빈 배열이다. ② `CatchBlockPattern` + `SET @po_intRetVal = @var` T-SQL 철자.
> ③ **판정 전체가 `declaredCodeSet.Contains(initial)`**(`:6464`) — **모델이 같은 회차에 쓴 집합**이다.
> 오염이 **양방향**으로 흔든다: 빠뜨리면 조용히 놓치고, 부풀리면(3차 통제군 S12·S13처럼
> 부모 SP 집합을 통째로 복제) **없던 결함을 만든다.** ②③ 재정박은 **오라클 교체를 공유**하므로
> 한 회차에 묶는 것이 싸다.

> ⛔ **②③ 재정박을 하지 않기로 했다 (2026-08-30). 실측 넷이 근거다.**
>
> **(1) ②는 재정박할 자리가 아니다.** `CheckCatchDiscardsReturnCode`가 잡는 것은
> 「`CATCH`가 `RETURN` 없이 `THROW`로 끝난다」이고, 그 의무의 앱 코드 형태
> (「실패 경로가 추적 코드를 `LegacyReturnCode`에 기록하는가」)는
> **`CheckLegacyReturnCodeBinding`이 이미 문서 단위로 덮는다.** 그 검사의 주석에
> 단계 단위로 좁혔을 때의 실측이 있다 — *「이행한 계획서 6건에서도 자기 저널 행을 쓰지
> 않는 단계가 **11~12개씩 발화한다**. 그 오탐은 L1 재시도를 소진시키므로 판정은
> 「문서 어딘가에 최소 한 번」으로 닫는다」*.
>
> **(2) ②에는 안정된 앱 코드 철자가 없다.** 같은 규칙·같은 모델의 연속 두 통제군이
> 실패 경로 관용을 통째로 바꿨다.
>
> ```
>                    Batch3(2차)   Batch4(3차)
> try { / catch (      17 / 15        0 / 0
> journal.updateFailed      0            19
> ```
>
> **(3) ③의 앱 코드 실물은 전 코퍼스에 1건이다.** 「선언 코드와 겹치는 초기값」만 세면
> `Batch4`에서 5건이지만, **실패할 창이 실제로 있는지**까지 보면 넷이 떨어진다 —
> `S09`·`S11`·`S12`·`S14`는 `try` 첫 줄이 곧 코드-특정 대입이다. 남는 하나가
> `S13`(`reqYmd = queryScalar(SQL_S13_REQ_YMD, …)`가 try 안 첫 문장). 옛 규칙
> 코퍼스는 그 관용을 아예 안 써 **판정 대상 0**이다.
>
> **(4) 이 침묵은 의도된 것이라고 이미 적혀 있다.** `AiService.cs:2152` —
> *「이 조항에는 L1 기계 강제가 없다 … 의도된 상태이며 "검사가 빠졌다"로 읽지 말 것」*.
>
> **대신 원인을 고쳤다.** Few-Shot이 추적 변수를 **읽는 줄만** 보여주고 초기화도 갱신도
> 안 보여줘서 모델이 초기값을 지어냈고, `0`(성공 코드)을 골랐다. 예시에 생애주기를
> 실었다(`NULL`로 시작 — `BatchControlContract`가 `LegacyReturnCode`를 nullable `int`로
> 확정하므로 계약 자신의 「값 없음」이고 어떤 레거시 코드와도 안 겹친다).
> 규칙 6-1과 채점 기준 4는 이미 그 의무를 적고 있었으므로 **한쪽만 고쳤다** — 짝이
> 깨진 것이 아니라 예시가 짧았다. 가드:
> `ConsolidatedPlanRules_FewShotShowsTheTrackedCodeLifecycle`.
>
> ⚠️ **확인의 한계**: 가드는 프롬프트의 모양만 잠근다. 모델이 실제로 `0` 대신 `NULL`을
> 쓰는지는 **다음 생성 회차에서만** 드러난다.

> **판정법**: 발화 0을 통과로 읽지 말고, 그 검사가 **재료를 얻었는지**를 따로 확인한다.
> 검사 D가 그렇게 꺼졌다 — 좌표 차분도 침묵 분모도 못 봤고, 검사별 총 발화량을 전후로
> 나란히 놓아서야 보였다.

## 4. 설계 제약 — 신규 SP 검사는 시그니처를 넓혀야 한다

규칙 3-1은 `CREATE PROCEDURE`를 **원본 인용일 때만** 허용한다. 그러므로 검사는
「이 이름이 이 Job의 레거시 프로시저인가」를 물어야 하는데,
`ValidateConsolidated(string markdown)`은 **본문만 받는다.**

선택지 둘:

1. **시그니처를 넓힌다** — `ValidateConsolidated(string, IReadOnlyCollection<string>? legacyProcedureNames = null)`.
   호출부는 `VerificationPipelineOrchestrator.cs:2004`와 `:2476` 둘뿐이고, 그 자리에
   Job의 원본 프로시저 목록이 이미 있다. 기본값을 두면 기존 테스트가 안 깨진다.
2. **문맥으로 가른다** — 인용은 보통 원본 DDL 블록 안에 있다. 재료가 필요 없지만
   판정이 약하고, 「인용처럼 보이게 쓰면 통과」라는 우회를 남긴다.

~~**1을 권한다.**~~ 2는 이 규칙이 겨누는 위반(모델이 새 프로시저를 지어내는 것)을
정확히 놓치는 쪽으로 틀린다.

> ⛔ **1도 채택하지 않았다 (2026-08-29). 셋 중 어느 것도 아니었다 — 재료 없이 넣었다.**
> `CheckNewDatabaseObjectDefinition`이 `ValidateConsolidated`에 들어갔고 **시그니처는
> 그대로다.** 이 절이 전제한 「인용을 가려야 한다」가 실측으로 무너졌다.
>
> **(1) 인용 예외가 도달 불가능하다.** 계획서 프롬프트(`raw/prompt-context.md`)에
> 원본 프로시저 DDL이 실리지 않는다 — Actor가 받는 것은 명세서 산문이고, 프롬프트
> 전체에서 `CREATE PROCEDURE`는 규칙 본문 두 곳뿐이다. **인용할 원본을 손에 쥔 적이
> 없다.**
>
> **(2) 코퍼스 113개가 전부 지어낸 이름이다.**
>
> **(3) 결정적 — 로스터를 넣으면 검사가 오히려 약해진다.** 레거시명과 겹치는 유일한
> 1건(`POQSettlePrco20:1900`의 `dbo.UP_UTIL_SETTLE_CANCEL_INS`)이 인용이 아니라
> **재정의**다. 선택지 1은 그 진짜 위반을 이름만 보고 통과시킨다. **도달 불가능한
> 예외를 사느라 실현된 위반 하나를 놓치는 거래**이므로, 이 절의 권고는 방향이
> 반대였다.
>
> 프롬프트 조성이 바뀌어 원본 DDL이 실리게 되면 (1)이 무너진다. 그때 로스터를 붙이면
> 되고, 재료는 `StepInterfaceFacts.CollectSchemaCatalog`가 이미 같은 자리에서 만든다.
> **§4만 읽고 시그니처를 넓히지 마라 — 이 상자를 함께 읽어라.**

## 5. 기계화 순위

### A급 — 정규식 하나, 재료 불필요, 실측된 위반 있음

| # | 규칙 | 판정식 | 실측 근거 |
|---|---|---|---|
| 1 | **10 NOLOCK** | `WITH\s*\(\s*NOLOCK` | 규칙이 "explicitly remove ALL"이라 예외가 없다. ~~1차 통제군 코드 안 2건~~ → **0건, 아래 정정** |
| 2 | **3-1 API 지정** | `SqlConnection`·`SqlCommand`·`SqlParameter`·`IsolationLevel\.`·`TransactionScope`·`DbContext`·`PreparedStatement`·`EntityManager` | **2차 통제군 11건. Critic이 보고도 통과시켰다** |
| 3 | **3-1 제어 흐름** | 새 SQL의 `GOTO`·`IF @@ERROR`·`BEGIN TRY` | 1차 통제군 `GOTO` 20 · `@@ERROR` 18 |

~~3번의 유일한 설계점은 **원본 인용을 어떻게 가르는가**다. §4와 같은 물음이므로 4번과
함께 다루면 한 번에 풀린다.~~ → **아래 정정.**

> ✅ **이행하며 둘을 정정했다 (2026-08-29). 셋 다 `ValidateConsolidated`에 들어갔다**
> (`CheckNoLockHints`·`CheckPrescribedFrameworkType`·`CheckSqlSideControlFlow`).
>
> **(가) 1번의 실측 근거가 계수 착시였다.** 「1차 통제군 코드 안 2건」은 §10-1의 **줄 단위**
> 주석 필터(`--`·`/*`·`//`로 *시작하는* 줄만 제외)가 `/* */` 블록의 **이어지는 줄**을 못
> 걸러 낸 것이다. 그 줄은 `POQSettleBatch2:1380`이고 내용은 *"NOLOCK 힌트는 SNAPSHOT 격리
> 정책에 따라 전부 제거되었다"* — **위반의 정반대**다. 문자열·주석을 제대로 지우고 다시
> 세면 **22편 전부 코드 안 0건**이다. 반면 산문에는 약 300건 있다("원본의 `WITH(NOLOCK)`는
> 전부 제거한다"). **이 축에서 문서 전수 grep은 거의 전량이 이행 서술을 고발한다.**
>
> 그래도 검사를 넣었다. 지금 0인 것은 **모델이 지켜서**이지 강제되어서가 아니고, 그것이
> §6-(1)이 말한 자리다. 연료는 재료 쪽에 실재한다 — 레거시 DDL 17개 파일에 `NOLOCK` 43건,
> **프롬프트에 실리는** 원본 명세서 3편의 코드블록 안에 6건.
>
> **(나) 3번은 4번을 기다릴 필요가 없었다.** 규칙 3-1의 「원본 인용」 예외는 **도달
> 불가능하다** — `raw/prompt-context.md`에 원본 DDL이 실리지 않는다. Actor가 받는 것은
> 명세서 산문이고, 프롬프트 전체에서 `CREATE PROCEDURE`는 규칙 본문 두 곳뿐이다.
> **인용할 원본을 손에 쥔 적이 없다.**
>
> 코퍼스 22편이 같은 것을 말한다: `CREATE PROCEDURE` 113개가 **전부 지어낸 이름**이고,
> 레거시명과 겹치는 유일한 1건(`dbo.UP_UTIL_SETTLE_CANCEL_INS`, Prco20:1900)도 인용이 아니라
> **재정의**다. 제어 흐름 토큰 1,695건 중 레거시명 펜스 **안**에 있는 것은 3건뿐이고, 그
> 3건마저 그 재정의 안에 있다 — 인용이 아니므로 지목이 옳다. 예외를 뺀 대가가 0이다.
>
> **§4의 권고(시그니처 확대)는 4번에 대해서는 그대로 유효하다.** 거기서는 판정 전체가
> 이름이기 때문이다. 다만 그 재료가 없으면 검사가 불가능하다는 뜻은 아니라는 것도 위가
> 함께 보인다 — 인용이 도달 불가능한 한 `CREATE PROCEDURE`는 언제나 위반이다. 재료는
> 그 사실이 프롬프트 조성 변경으로 뒤집힐 때의 보험이다.
>
> **스윕**: 계획서 22편 전수. 거짓 양성 0 · 진짜 양성 `SqlSideControlFlow` 21편(2,233 토큰) ·
> `FrameworkTypePrescribed` 12편(59 토큰) · `NoLockHintInCode` 0. BASE 대비 다른 검사 카운트
> 불변(`LegacyReturnCodeNeverBound` 14 · `BatchRunRowNeverCreated` 2). **2차 통제군
> `POQSettleBatch3`은 제어 흐름에서 침묵하고 API에서만 발화한다** — §10-2·§10-4가 각각
> 「규칙이 닫았다」와 「안 닫혔다」로 적은 그대로다.

### B급 — 구조를 넓혀야 함

| # | 규칙 | 필요한 것 |
|---|---|---|
| ~~4~~ | **3-1 신규 저장 프로시저** | ~~원본 프로시저 목록 (§4)~~ → **재료 불필요였다. 2026-08-29 이행 완료** (`CheckNewDatabaseObjectDefinition`, 위 §4 상자) |
| 5 | 12 청크 키 실재 | 대상 테이블 DDL의 컬럼 목록 — `raw/ddl`에 이미 있다 |
| 6 | 8-1 청크 트랜잭션 경계 | **보류.** 새 Few-Shot의 두 층 표기에 의존하므로 그 표기가 정착한 뒤 |

### C급 — 판정 기준부터 설계해야 함

7-1 UNION 분기 정렬(컬럼 목록 파싱 필요) · 3 순서·병렬(판정 모호) ·
4 격리 의무(「어디서 거는지 정하지 말라」는 규칙이라 부재를 벌할 수 없다) ·
6 청크 DELETE 필터 · 2-b 의사코드 언어 ·
**3-1 「한 문서 한 표기」(2026-08-30 추가 — 아래 ⛔ 상자)**.

규칙 1·15는 기계화 가치가 낮다.

> ⛔ **3-1 「한 문서 한 표기」를 A급으로 오분류했었다. C급이 맞다 (2026-08-30).**
>
> 판독 §10이 이것을 「값싼 곁가지」로 적었는데 **근거가 계수 하나뿐이었다.**
> 실제로 프록시를 세워 재 보니 값싸지 않다.
>
> **프록시가 규칙이 말하는 것을 안 잰다.** 규칙 3-1은 *"the common design calls a
> thing by **one invented name** and a step calls **the same thing by another**"* —
> **한 가지를 두 이름으로**다. 기계로 세울 수 있는 프록시(같은 수신자를 「이름 붙은
> 동사」와 「일반 `execute`」로 섞어 부름)가 재는 것은 **한 이름에 두 종류의 메서드가
> 섞임**이고, 그건 다른(그리고 약한) 주장이다.
>
> **규칙이 실제로 겨눈 사례는 이미 잡힌다.** §10-4가 든 실물은
> `ISettleBatchConnection`/`connectionFactory.open()` 대 `SqlConnection`/`SqlCommand`인데,
> 그 절반은 `CheckPrescribedFrameworkType`이 이미 잡는다. 남는 「지어낸 이름 둘」은
> 의미 판정이다.
>
> **모집단**: 코퍼스 23편 전수에서 프록시 발화가 **1편 · 2객체 · 4건**뿐이다
> (`POQSettleBatch4`의 `journal` 3/49 · `checkpoint` 1/15). 옛 규칙 판은 그 관용을
> 아예 안 써 0이다. Critic도 **경미**로 분류했다.
>
> **닿으려면 필요한 것**: 「두 이름이 같은 것을 가리키는가」의 판정 기준. 그것이 서기
> 전에는 프록시로 대신하지 마라 — 규칙이 말하지 않은 것을 강제하게 된다.

## 6. 왜 이 순위인가 — 근거와 반례

**(1) 재량 절에 기댄 축은 모델 교체로 조용히 꺼진다.** 이것이 순위의 제1 기준이다.
실측 전례: 명세서 Actor를 `gpt-5.6-terra` → `deepseek-v4-pro-0813`으로 바꾸자 프롬프트가
요구한 「지역 변수 표」가 사라져 `CheckSpecLocalVariablesDeclared`가 **18 → 0**으로 꺼졌고,
**잃은 18건은 진짜 결함이었다**(스냅샷 복원으로 확인). 관측 변화는 없었다.

**(2) 「모델이 지키더라」는 강제가 아니다.** 2차 통제군의 신규 저장 프로시저 0은 규칙의
효력이 아니라 **`claude-sonnet-5`가 원래 그것을 쓰지 않는다**는 사실이었다 — 같은 모델의
1차 통제군이 **옛 규칙으로도 0**이었다. 기준선에서 유일한 Claude 표본 `Proc4`도 옛 규칙에
0이다. 이 축은 지금도 **한 번도 강제된 적이 없다.**

**(3) 반례 — Critic은 무능하지 않다.** 1차 통제군에서 명시 조항이 없는데도 Critic이
"SQL 의사코드 안에 TRY/CATCH·트랜잭션 제어를 두어 정책과 충돌한다"를 규칙 3-1에서
파생해 잡아냈다. **그러므로 이 조사는 「Critic을 L1로 대체하자」가 아니다.** Critic은
파생할 줄 알지만 그 능력이 모델마다 다르고, 2차 통제군의 Critic(`glm-5.3`)은 API 지정을
보고도 통과시켰다. **기계는 모델이 바뀌어도 같은 것을 본다** — 그 점 하나가 다르다.

**(4) 강제는 셋이 겹쳐야 한다.** L1은 산출물을 사후에 보고 재생성으로 되돌리므로 시도를
소모한다. 프롬프트가 먼저 옳게 쓰게 하고, Critic이 파생으로 넓게 잡고, L1이 놓칠 수 없는
축을 못박는다. A급 셋은 **L1이 없어서 두 층뿐이던 축**들이다.

> ✅ **세 번째 층이 일한 첫 실측 (2026-08-29 `POQSettleBatch4`).** 판독:
> `docs/audit-reports/sweeps/2026-08-29-stage4-pair-batch4.md`
>
> **실존 API 타입 지정이 12 → 0이 됐다.** `Batch3`은 규칙 3-1에 그 조항이 **있는 채로**
> 12건을 냈고 Critic(`glm-5.3`)은 추론 로그에 그것을 적고도 감점하지 않았다(설계서 §10-4).
> 프롬프트와 Critic 두 층이 **둘 다 흘린 축**이었고, L1을 넣자 사라졌다. 이 문단은 그때까지
> 원리였고 이제 실증이 있다.
>
> ⚠️ **다만 같은 판이 이 문단의 균형을 바꿨다 — 이제 구속 조건은 L2다.** 6회 예산 중 L1이
> 쓴 것은 2회이고(둘 다 다음 회차에 닫혔다), 판을 끝낸 것은 Critic의 예외처리 축 **7/10**
> 하나였다. 채택본은 L1을 **오류 0으로** 통과했다. **그러므로 §5의 B급 순서를 그대로
> 따르기 전에 이 사실을 함께 읽어라** — L1 검사를 하나 더 늘리는 것의 한계 수익이 그만큼
> 줄었고, 지금 산출물을 붙들고 있는 것은 L1이 아니다.

## 7. 재실행 레시피

```bash
# 진입점별 검사 목록
grep -n "public .*Validate[A-Za-z]*(" src/ReSet.Core/Services/MechanicalValidator.cs
sed -n '214,262p' src/ReSet.Core/Services/MechanicalValidator.cs        # ValidateConsolidated
sed -n '287,570p' src/ReSet.Core/Services/MechanicalValidator.cs \
  | grep -o "Check[A-Za-z]*(" | sort -u                                 # ValidateBatchStep

# 규칙 목록
python3 - <<'PY'
import io,re
s=io.open('src/ReSet.Core/Services/AiService.cs',encoding='utf-8').read()
i=s.index('private const string ConsolidatedPlanRules'); j=s.index('[Few-Shot Examples',i)
for m in re.finditer(r'^(\d+(?:-\d+)?)\. (\[[^\]]+\]|[A-Z][^\n]{0,60})', s[i:j], re.M):
    print(m.group(1), m.group(2))
PY

# 특정 규칙에 검사가 있는지 (예: NOLOCK)
grep -n -i "NOLOCK" src/ReSet.Core/Services/MechanicalValidator.cs
```

**주의**: `grep -c "Check"`로 세지 말 것. 정의는 49개인데 계획서·단계 경로가 부르는 것은
20개뿐이고 나머지는 명세서 경로(축 B)의 것이다. **정의 수가 아니라 호출 목록을 세라.**
