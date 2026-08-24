# 알려진 결함

`docs/superpowers/specs/`의 설계 문서가 후속으로 미룬 것 중, **한 문서만 읽어서는 알 수 없는
것**을 모은다. 심각도 순위(어느 것이 P0인가)와 반복 횟수(같은 것을 몇 개의 설계가 별건으로
미뤘는가)가 그것이다. 나머지는 각 설계 문서의 `§남은 후속`에 원본이 있다.

> **이 문서는 정기 재검증을 하지 않는다.**
>
> 2026-08-13 ~ 08-21에 전수 재검증을 세 번 돌린 결과가 근거다. 9일 · src 커밋 150건 동안
> 최초 39건 중 **36건이 그대로**였고, 닫힌 4건은 전부 다른 일을 하다 우연히 닫힌 것이다
> (닫은 커밋 6개 중 이 목록을 근거로 든 것이 하나도 없다). 그동안 목록은 39 → 47로 커졌고,
> 재검증 한 회차가 앵커 70여 개 대조와 클린 빌드를 요구했다. **비용은 회차마다 들고 산출은
> 0에 수렴한다.**
>
> 그래서 위쪽 「살아 있는 목록」만 유지하고, 아래 「알려진 한계」는 **시점 기록**으로 둔다.
> 한계 쪽 항목은 **그 자리를 건드리는 사람이 그때 확인한다.** 적힌 앵커를 검증 없이 믿지 마라.

**앵커 규약: `타입.멤버`를 쓰고 줄 번호를 쓰지 않는다.** 한 멤버 안에서 자리를 좁혀야 하면
그 자리에 실제로 있는 식별자나 조건식을 함께 적는다. 이 목록은 줄 번호로 세 번 낡았다 —
`MechanicalValidator`가 1,800줄대에서 4,700줄대로 커지는 동안 멤버 이름은 하나도 바뀌지 않았다.

**여기에 없는 것**

- **산출물 정합성 결함** — `docs/audit-defect-catalog.md`가 갱신 규약과 함께 소유한다.
  아직 안 닫힌 부류(P5 `CAST(money AS INT)` 반올림 — **7회차 🔴 2건 중 하나** · P14 원본 사실
  미확정 · P10 있는 컬럼을 "없음"으로 단정 **재발** · P12 재생성 진동 · P16 호출자 명세서의
  정본 이중화 · P15 mermaid 도식 불일치 · P6 주석 보존표 누락 · P11 표↔산문 불일치 ·
  축 B B1–B8 재감사)는 그 문서 §5에 있다.
  **여기에 옮겨 적지 마라** — 닫힘 판정 기준이 다르다(카탈로그는 재감사 회차를 요구하고
  이 문서는 코드 앵커를 본다). 두 곳에 적으면 갈라진다.
- **각 설계의 상세한 배경** — 아래 항목의 `출처`가 가리키는 문서에 있다.

---

## 살아 있는 목록

### 정책 결정이 선행되어야 하는 것

코드 변경 전에 기준을 정해야 한다. **`반복` 칸이 이 표의 존재 이유다** — 여러 설계가 같은
것을 각자 "별건"으로 미룬 횟수이고, 설계 문서 한 편만 읽어서는 이 숫자가 보이지 않는다.

| 항목 | 반복 | 요지 |
|---|---|---|
| **구조화 출력(`--json-schema`)** | 1 | 세 CLI 모두 지원하며 Critic 채점 JSON 파싱을 견고하게 만든다. 다만 스키마 정의가 API 경로와 CLI 경로의 동작을 갈라놓는다 |
| **`ActorEffort: dynamic`의 CLI 동시 실행 제어** | 1 | dynamic을 쓰면 프로세스 3개가 동시에 뜨고 쿼터 소진이 빨라진다. `74d53ec`(2026-08-20)가 무인 배치의 CLI provider 차단을 `AiSettings:AllowCliProviderInBatch` 옵트인으로 열면서 이 위험이 도달 가능해졌다 — `CliProviderBatchGuard`는 경고만 남기고 동시 실행 자체는 제어하지 않는다 |
| **계획서의 SQL 배치 위치** | 1 | 한 계획서가 같은 로직을 C# 인라인 SQL과 신규 저장 프로시저로 함께 지시한다. 어느 프롬프트도 기본을 말하지 않기 때문이다. 기준 없이 프롬프트만 손대면 다음 회차에 반대로 쏠린다. 자리: `AiService`의 `ConsolidatedPlanRules` 상수와 `AiService.GenerateBatchStepSectionAsync`의 단계 섹션 프롬프트 |
| **B4 — 축 B 나머지 10건** | 1 | 레거시 불변식인지 여부를 `Spec.md`로 판정해야 판단이 선다. **지금 축 B를 돌리면 안 된다** — `agent/` 번들(2026-08-19 21:03)이 재생성 전 명세서로 만든 것이라, 현행 `Spec.md`(08-22)와 대조하면 세대 차이가 결함으로 잡힌다. 명세서 결함을 고치고 Job 설계 문서를 다시 만든 뒤가 순서다. 7회차가 축 B로 넘긴 입력 셋은 카탈로그 §5에 있다 |

### 해결 불가로 기록된 것

- codex-cli의 전역 `AGENTS.md` 주입
- codex/agy의 출력 절단 감지
- agy의 Windows 명령행 stdin 한계 — 명확한 예외로 알리는 데서 멈춘다
- **`BatchSourceWatermark`·`BatchImmutableLedgerBaseline`의 컬럼 확정** — 어느 원천을
  워터마킹하고 어느 원장을 기준선으로 잡는지에 따라 컬럼이 달라지는 **Job 형상** 객체다.
  ReSet이 정할 수 있는 사실이 아니므로 스키마·명명 규칙만 적용하고 DDL은 계획서에 맡긴다
  (`2026-08-18-axis-b-batch-skeleton-design.md` §7)
- **B5 `NOLOCK` 전면 제거 7건** — 전부 ⚪이고 "배치가 단독 실행되고 원천에 동시 커밋이
  없다"는 전제부 판단이다. 계약 결함이 아니다(같은 문서 §7)

### 돌려 봐야 아는 것

기준을 정하는 문제가 아니라 실측이 필요한 것이다. 지금 할 수 있는 일이 없으므로
체크박스를 달지 않는다. **감사 소관 실측(축 B 재감사·P5·`INS_EXTRA`)은 여기 없다** —
`audit-defect-catalog.md` §5에 있다.

| 항목 | 무엇을 봐야 하나 | 빗나갔을 때 |
|---|---|---|
| **인프라 객체 수집 목록의 과부족** | 67종은 POQSettleProc9 하나의 수치다. 두 번째 데이터 점이 있다 — POQSettlePrco20의 `task-00-bootstrap.md`가 19건을 싣는다. 그 19건이 계획서 SQL이 실제로 참조하는 것과 과부족 없이 맞는지는 아직 대조하지 않았다 | 과하면 회차 0이 필요 없는 객체를 만들고, 모자라면 SQL이 없는 테이블을 참조한다 |
| **생략 주석 배너의 빈도** | 배너가 한 Job에 몇 건이나 뜨는지. POQSettlePrco20은 통합 계획서 산출물이 없어(`docs/`에 `BatchMigrationPlan.md`뿐) 이 회차로는 못 잰다 | 수십 건이면 사람이 읽지 않게 된다. 그때는 패턴을 좁힐지, 차단으로 승격할지 다시 판단해야 한다 |
| **유령 테이블 결함이 재생성으로 고쳐지는지** | 결함 메시지를 받은 모델이 실재하는 이름으로 바꾸는지, 이름만 바꿔 다른 유령을 만드는지 | 후자면 검사가 재생성만 태우고 아무것도 고치지 못한다 |
| **함수 DDL 본문을 프롬프트에서 뺄지** | 동작 서술을 금지한 뒤에도 *호출하는 문장*을 정확히 쓰려면 본문이 필요할 수 있다. 빼면 토큰이 크게 줄지만 측정 없이 지울 일이 아니다 | 지나치게 빼면 "함수가 0을 반환하는 행은 갱신 대상에서 빠진다" 같은 호출부 서술이 무너진다 |
| **재시도 중 축 간 역행이 다시 커지는지** | 2026-08-23 재측정에서 한 축을 고치면 다른 축이 2~4점 떨어지는 역행이 실재했다(`UP_UTIL_SETTLE_INS` 8/21: 시도 2에서 CRUD 7로 고치자 시도 3에서 정합 9→7). Actor가 백지에서 다시 쓰기 때문이다. 지금은 결국 수렴하므로 결함이 아니라 관측이다. 다음 전건 재생성 뒤 같은 귀속(로그의 `[추출된 JSON 내용]`을 `L2 AI 교차 리뷰 시작` 이벤트에 **실행 단위로** 짝지어)을 다시 돌려 폭을 잰다 | 폭이 다시 20점대로 오르거나 역행이 수렴하지 않으면 「시도 간 진동 억제」를 되살린다 — 그때는 `IAiService`에 이전 명세서를 넘기는 방향이 다시 정당하다 |
| ~~**파서가 파생 테이블의 비한정 컬럼을 물리 테이블에 과잉 귀속한다**~~ | **2026-08-23 닫힘** — `SqlStaticParser`가 QuerySpecification 진입 시 그 FROM의 파생 테이블이 투영하는 이름을 미리 모아 두고, 한정자 없는 컬럼이 그 투영에 있으면(또는 `SELECT *`라 알 수 없으면) "로컬 물리 테이블이 하나면 그것" 폴백을 걸지 않는다. 코퍼스 31개 DDL 전수 대조로 귀속 4건이 빠지고(EXCEPTION_PROC `TPGProperty.PLTID`·`ID`, `COLLECTYMD` `TPGCollectPeriodMst.YMD`, `UIF_SettleYMD` `TSettlePeriodMst.YMD` — 전부 파생 테이블 투영), 추가 0. 캐시 15로 재생성되면 SELECT 대상 표에서 사라진다. 9회차 (가)-`EXCEPTION_PROC`가 닫힌다 | 재생성 전 명세서에는 옛 귀속이 남는다 — 10회차 재감사가 그 자리를 보면 재생성 여부부터 확인 |

출처: `2026-08-14-generated-bundle-contract-design.md` §실측이 필요한 것,
`2026-08-20-udf-machine-contract-design.md` §5

---

## 알려진 한계 — 재검증하지 않음

**2026-08-21 시점의 기록이다.** 그 뒤로 확인하지 않았으므로 앵커도 판정도 낡을 수 있다.
이 자리를 건드리게 되면 그때 확인하라. 대부분 각자의 설계 문서에 원본이 있고, `출처`가
없는 항목은 **리뷰나 감사 실측에서 이 문서로 직접 들어온 것**이라 여기가 유일한 기록이다.

### 검증 파이프라인

- **`SpecHeader`에 인터페이스 점수 필드가 없다** — `ReSet.Cli/SpecHeaderReader.cs`의
  `SpecHeader` 레코드가 5개 키만 읽어, `VerificationDocumentFormatter.FormatVerifiedDocument`가
  YAML에 쓴 `인터페이스 점수`를 승인 화면이 무시한다(캐시 왕복에서는 살아남는다).
  **고칠 때 함정** — 필드를 추가하면 `ConsoleUserInteraction.RequestHumanReviewAsync`의
  `?? 10` 폴백 뒤에 놓여 여섯 번째 조작 만점 위험이 생긴다.
- **생성 호출 실패 재시도 0회** — 두 루프의 `if (!genSuccess || …)` 분기. 명세서 경로와
  계획서 경로의 공통 정책이라 한쪽만 고칠 수 없다.
  출처: `2026-08-05-batch-structure-redraft-design.md` ⑤
- **2/3가 빈 응답을 내도 일반 방어가 없다** — `RunConsolidatedPipelineAsync`가
  `DraftBatchPlanStructureAsync`의 반환을 곧바로 `PlanStructureEnricher.Enrich`에 넘긴다.
  재수립 경로만 방어한다.
- **`Task.WhenAll`이 첫 예외만 표면화한다** — `RunCodeObjectPipelineCoreAsync`의
  `Task.WhenAll(tasks)`와 `Task.WhenAll(reviewTasks)`. 로컬 프로바이더 병렬 분기에서
  `IOException`과 `OperationCanceledException`이 동시에 발생하면 필터가 통과해 취소가
  삼켜진다. 취소 정책 스캐너는 필터가 있으므로 잡지 못한다.
- **`ScoreReadability` 라벨이 사실과 다르다** — 두 Critic 모두 코드 가독성이 아니라
  Mermaid 문법을 채점하는데 `axisScoreLines`가 `가독성 점수`로 찍는다. 명세서에서도 틀렸고
  라벨 문자열이 테스트로 고정되어 있다.
- **타임아웃 오보가 리뷰 경로 밖에는 그대로다** — `when (ex is not OperationCanceledException)`
  필터 55곳. `AiCallRetry.ExecuteAsync`가 감싼 리뷰 5곳만 `AiCallFailedException`으로 바뀌어
  정상 보고된다. 생성 호출 27곳(`AiService`의 `Generate*`·`Draft*`·`Brainstorm*`·`Deconstruct*`)을
  포함한 나머지 경로의 HttpClient 타임아웃은 여전히 "사용자에 의해 중단되었습니다"로 찍힌다.
  `AiSettings:TimeoutSeconds`가 3600이므로 한 시간을 태운 뒤 그렇게 된다.
  출처: `2026-08-21-ai-call-retry-design.md` §7
- **재시도가 벽시계 시간도 곱한다** — `AiClientFactory.CreateClient`가 CLI 클라이언트에도
  API용 `HttpClient.Timeout`(`httpClient?.Timeout ?? TimeSpan.FromSeconds(300)`)을 그대로
  넘긴다. `AiSettings:TimeoutSeconds`는 3600. 정체된 리뷰 호출 하나는 이제 타임아웃 한
  시간을 태우고, 지터로 잠들었다가, 재시도에서 다시 한 시간을 태운 뒤에야 소프트 페일
  배너에 이른다. `VerificationPipelineOrchestrator.RunCodeObjectPipelineCoreAsync`의
  리뷰 좌석은 `_actorEffort == "dynamic"` 분기에 걸리느냐로 갈린다 — 비-dynamic(`else`)
  경로는 `"review"` 한 자리, dynamic 경로는 `"final_review"`·`"refinal"`·병렬 후보 배치를
  쥔다. 두 분기는 한 객체 실행에서 동시에 겹치지 않지만, 어느 쪽이 걸리든 그 좌석에서
  재시도는 벽시계 시간을 그대로 곱한다. `Program.Main`의
  `foreach (var selectedOption in targetSps)`가 대상 객체를 순차 처리하므로, 저하된
  엔드포인트가 이제 예전 일정으로 배치를 끊지 않고 계속 갈아 넣는다. 설계 §5.1은 비용을
  돈으로만 쟀다("리뷰 호출 비용이 2배") — 시간은
  셈하지 않았다. 이 목록이 이미 미룬 "`AiSettings:TimeoutSeconds: 3600` 값 자체"의 유예가
  이 사실 때문에 전보다 비싸졌다.
  출처: `2026-08-21-ai-call-retry-design.md` §5.1
- **L2 리뷰 재시도가 콘텐츠 결함에는 닿지 않는다** — `AiService.ParseReviewResult`는
  리뷰 JSON 파싱이 깨지면 예외를 던지지 않고 `catch` 블록에서 `HasDefects = true`,
  다섯 점수 전부 0인 `ReviewResult`를 반환한다. 예외가 전혀 나지 않으므로
  `AiCallRetry.ExecuteAsync`는 이 실패를 볼 기회조차 없다. 파이프라인은 이걸 진짜
  0/100 리뷰로 여기고 Actor 전체 재생성 회차를 돌리는데, 그 비용은 이 기능이 아끼려던
  재시도 한 번보다 크다. 별도로, 비거나 절단되거나 차단된 완료(`choices` 누락, 빈
  content, Gemini `finishReason` RECITATION/OTHER 등 API 클라이언트 6곳의
  `InvalidOperationException` 계열)는 `AiRetryPolicy.Classify`의 최종 `return
  AiRetryVerdict.Fatal` 분기로 떨어진다 — 이것은 설계 §4.1이 명시한 그대로이며
  **범위 제한이지 구현 이탈이 아니다.** 분류기를 "고쳐서" 이 둘을 잡으려 들지 마라.
  둘 다 길고 값비싼 L2 리뷰 호출에서 흔한 비결정성 실패 축이라 누락의 실효가 크다.
  출처: `2026-08-21-ai-call-retry-design.md` §4.1·§7
- **병렬 후보 검토의 클로저 고정이 재시도 경로에서만 하중을 진다** —
  `VerificationPipelineOrchestrator.RunCodeObjectPipelineCoreAsync`의 병렬 후보 검토
  조립부, `var candidate = candidates[i];`. `AiCallRetry.ExecuteAsync`는 첫 시도의
  `factory()`를 동기적으로 호출하므로 그 자리를 `candidates[i]`로 인라인해도 **첫 시도**는
  여전히 맞는 인덱스를 읽는다 — 회귀가 해피 패스에서는 보이지 않는다는 뜻이다. 문제는
  **재시도**다. `for` 루프는 지연 없이 다음 반복으로 넘어가 `reviewTasks`에 태스크만
  쌓고, 지터 지연은 그 뒤 `Task.WhenAll`로 기다리는 동안 일어난다 — 이 시점에는 이미
  `i`가 `candidates.Length`까지 진행한 뒤다. 인라인된 `candidates[i]`가 재시도 시점에
  평가되면 `IndexOutOfRangeException`을 던진다. 어느 테스트도 이 자리에서 재시도를
  실제로 태우지 않는다 — `VerificationPipelineOrchestratorTests`의 구역별(하이브리드)
  경로 테스트(`RunCodeObjectPipelineAsync_SectionalPath_*`)는 후보 채점 3회를 전부
  첫 시도에 성공시키고, 실패·재시도는 그 뒤 `"final_review"` 자리에서만 일으킨다. 이는
  **API 클라이언트 StatusCode 테스트 부재나 재시도 지연-취소 타이밍 의존성과는 다른
  구멍이다** — 저 둘은 재시도 인프라 자체의 테스트 이야기고, 이것은 호출부 조립 코드의
  지역 변수 스코프가 재시도 경로에서만 열리는 회귀다.
  출처: `2026-08-21-ai-call-retry-design.md` §5
- **API 클라이언트 5곳의 `StatusCode` 보존에 실행 가능한 테스트가 없다** —
  `ClaudeClient`·`GoogleClient`·`OllamaClient`·`ZaiClient`(그리고 `OpenAiClient`의 두 번째
  분기)가 `HttpRequestException`에 `response.StatusCode`를 싣도록 고쳐졌지만, 그것을
  단언하는 테스트는 `OpenAiClientTests`의 `ChatAsync_ErrorResponse_PreservesStatusCodeOnException`
  하나뿐이다. 여섯 자리 모두 같은 모양의 기계적 수정이고 파일별 개수 대조(`grep -c`)로는
  맞았지만, 나머지 다섯 자리는 회귀해도 어느 테스트도 잡지 못한다.
- **재시도 지연-취소 테스트가 타이밍에 의존한다** —
  `AiCallRetryTests.ExecuteAsync_CancellationDuringDelay_EscapesWithoutRetryingAgain`이
  60~90ms 지연 창의 약 15ms 지점에서 취소한다. 연속 10회 실행은 통과했고 여유는 약 4배지만,
  느린 CI 환경에서는 플레이크가 될 수 있는 잔여 위험이다.

### 배치 계획 생성

- **브레인스토밍 원문이 3/3에 전달되지 않는다** — `IAiService.GenerateConsolidatedBatchPlanAsync`
  시그니처에 자리가 없어, 아키텍처 판단(Tasklet/Chunk 선택 등)이 목차 제목에 살아남은
  만큼만 본문에 도달한다.
  출처: `2026-08-05-batch-structure-redraft-design.md` ①
- **목차가 S01의 `TargetTables`를 채우지 못한다** — `PlanStructureEnricher.RewriteTables`,
  `AiService.DraftBatchPlanStructureAsync`. Proc14 회차에서 결함 1건으로 실제 비용을 냈다
  (`87d6e30`이 별개 사안으로 남김). 보강기가 정적 분석의 쓰기 대상으로 `TargetTables`를
  통째로 교체하는데도 비는 단계가 생긴다 — **왜 그 단계만 비는지부터** 봐야 한다.
  출처: `2026-08-14-generated-bundle-contract-design.md` §제외
- **4개 필수 H2가 5곳에 하드코딩** — `AiService`의 `ConsolidatedPlanRules` 상수,
  `AiService.DraftBatchPlanStructureAsync`, `AiService.ReviewConsolidatedPlanAsync`,
  `MechanicalValidator`의 `RequiredConsolidatedHeaders`, `MechanicalValidator.BuildSuggestedPromptFix`의
  `IsConsolidated` 분기. **2/3 단계의 실질 기여가 H3/H4뿐인 이유가 여기 있다** — 3/3
  프롬프트가 이미 네 H2를 직접 지시하므로 목차가 다시 정해줄 필요가 없다.
- **계획서가 `batch.*` 모듈의 이름만 주고 본문을 주지 않는다** — `TaskFileComposer.AppendInfraObjects`.
  에이전트가 `Spec.md`에서 본문을 재구성해야 하는데 `MigrationInstructions.md` 규칙 7
  (자리표시자 금지)과 긴장 관계다. 지시서가 "회차 0은 DDL과 골격까지"로 공백의 주인을
  정해 부분 완화됐고, 남은 것은 단계 섹션 프롬프트를 고치는 쪽이다.
  출처: `2026-08-14-generated-bundle-contract-design.md` §제외·§남은 후속

### 정적 분석 / 프롬프트 계약

- **호출된 객체가 참조하는 컬럼이 호출자의 스키마 표로 접혀 올라오지 않는다** —
  `SqlStaticParser`의 `ReferencedColumnsPerTable`. `EXPECT_PROC`이 부르는 UDF
  `UF_GET_COLLECTYMD`가 읽는 `TPGCollectPeriodMst`의 8컬럼이 `EXPECT_PROC` 자신의 SQL
  본문에 없어 애초에 수집되지 않고, 그 결과 명세서가 실재하는 컬럼을 "스키마 표에 정의되지
  않았다"고 잘못 기록한다. **실측으로 원인이 뒤집힌 항목이다** — 원 추정("리졸버 결함")은
  반증됐고(UDF 자신의 `prompt-context.md`에는 8컬럼이 정상), `SchemaPromptColumnSelector.DetectOrphanedColumnKeys`를
  넓혀도 닫히지 않는다(그 검출기는 **키**가 병합 안 된 경우만 보는데 이 컬럼들은 키 자체가
  생기지 않는다). 닫으려면 하위 객체의 `ReferencedColumnsPerTable`을 상위로 접어 올리는
  별도 설계가 필요하다. 2026-08-20 참조 함수 기계 계약은 이 자리를 닫지 않는다 — 그 표는
  호출 사실과 링크만 싣는다.
  출처: `2026-08-17-axis-a-spec-fidelity-design.md` §설계 1.3
- **정확 일치 타입 테이블 2곳이 분류기 밖에 있다** — `DependencyAnalysisOrchestrator.TryParseCodeObjectType`,
  `MetadataExporter.NormalizeCodeObjectDdlFolder`. `"P"`/`"FN"`/`"TF"`는 두 테이블에서
  Procedure/Function이지만 `SqlObjectTypeClassifier`에서는 `Unresolved`이고,
  `AGGREGATE_FUNCTION`/`EXTENDED_STORED_PROCEDURE`는 반대다. 오늘 오작동하지 않는 것은
  실제 `Type` 값이 전부 `type_desc`에서 오기 때문이지 게이트가 막아서가 아니다.
  출처: `2026-08-09-type-classification-policy-design.md` §후속 4
- **내부 방어 가드의 경고가 프롬프트로 샌다** — `SqlStaticParser.RecordUpdateMapping`의
  `"내부 방어 가드 작동"` 경고가 `ControlFlowSummary`를 거쳐 `AiService.BuildSpMetadataTexts`에서
  `"식별된 제어 흐름 구조 요약 (IF/WHILE)"`이라는 어긋난 머리말 아래 실린다. 현재 호출
  그래프에서는 도달 불가하지만 `RecordDmlTarget` 계약이 바뀌면 열린다.
  출처: `2026-08-09-update-mapping-contract-design.md` §남은 후속 1
- **`CrudAnalysis` 분기에 INSERT fill-in 표가 없다** — `AiService.BuildSpecSectionPrompts`의
  `sectionType == "CrudAnalysis"` 분기. UPDATE는 `BuildUpdateMappingTemplateLines` 공유
  헬퍼로 두 경로가 정리됐으나 INSERT는 `BuildSpecificationPrompts`에만 있다. INSERT에는
  `CheckUpdateMappings`에 대응하는 L1 대조가 없어 오늘 실패를 만들지 않는다.
  출처: `2026-08-09-update-mapping-contract-design.md` §남은 후속 8
- **RAG/청크 경로가 테이블 단위 substring 필터를 쓴다** — `AiService.BuildChunkDeconstructionPrompts`의
  `ragSchemas` 조립이 `depFullName.Contains(cleanRefTable)`와 `cleanRefTable.Contains(dep.Name)`
  양방향 부분 문자열이라 이름이 겹치는 테이블을 함께 끌어온다. Stage 1 전용이고 실제 섹션
  생성은 `BuildSpMetadataTexts`를 쓰므로 현재는 무해하다.
- **`DependencyInfo.Type` 타입화** — 여전히 `string`. 문자열 가드가 아니라 타입 시스템으로
  원시 판정을 차단하는 쪽이 근본적이지만 직렬화·스냅샷 호환성까지 번진다.
- **`AliasTargetFinder`가 FROM 절 하위 트리 전체를 돈다** — `SqlStaticParser`의 중첩 클래스
  (`ResolveAliasWithinFromClause`가 생성). `ExplicitVisit`을 오버라이드하지 않아 기본 순회가
  자식까지 내려가, 중첩 서브쿼리가 바깥 대상과 같은 이름의 별칭을 쓰면 그것을 잡는다.
  근본 수정은 최상위 `TableReference`만 훑는 것. `549541a`가 별칭 바인딩 순서와 별칭 없는
  자리의 조기 결론은 고쳤으나 순회 범위는 그대로다.

### L1 기계 검증기

- ~~**DML 범위 표의 술어 컬럼 칸 대조가 중복 토큰을 허용한다**~~ — **2026-08-24 닫힘.** 실측 결과
  '허용'이 아니라 **그 칸을 아예 대조하지 않았다** — `CheckDmlScopeTable`은 문장·라인 토큰과
  GROUP BY 칸만 봤다. 술어 컬럼·조인 키 칸에 렌더 문자열(", " 결합) 정확 일치를 요구하게 했다
  (행 매칭 후·목록이 비지 않을 때만 — "(없음)" 우연 일치 함정 회피, GROUP BY 관례). 코퍼스 31개
  스윕에서 정확히 10회차 🟡 그 자리(`COMM_UPD` UPDATE 10, PGNAME 중복) 1건만 잡히고 거짓 양성 0.
  그 명세서는 다음 재생성에서 이 L1 오류가 시정 지시로 실려 고쳐진다.
  근거: `docs/audit-reports/2026-08-24-POQSettlePrco20-axisA.md` 4절
- ~~**「파라미터와 변수의 컬럼 관계」 표의 연결 컬럼 주장을 어떤 검사도 대조하지 않는다**~~ —
  **2026-08-23 닫힘.** `ParameterColumnBindingExtractor`(DDL의 변수↔`테이블.컬럼` 결합 - 술어·산술식·
  대입·INSERT 자리·커서 FETCH INTO, 조인 등식 전파; 함수 인자 동반은 결합 아님)와
  `MechanicalValidator.CheckParameterColumnClaims`(「## 개요」·「## 파라미터 목록」 아래 표의 백틱
  `테이블.컬럼` 주장 대조, 테이블이 `ReferencedTables`에 있는 것만). 코퍼스 31개 스윕에서 정확히
  `EXCEPTION_PROC:34`의 두 주장만 잡히고(`TPLCardTxMst.YMD`·`TClientSettleRate4MobileCo.YMD`) 거짓 양성 0 —
  첫 판의 거짓 양성 6건(PROC_ETC 커서 4·COMM_UPD 산술식 1·조인 전파 1)은 결합 정의를 넓혀 없앴다.
  **캐시 15 전건 재생성 실측(08-23)** — 이 검사가 `PROC_ETC`의 재시도 6/6 소진에 가담했다. 거짓 양성 둘:
  변수 값을 만드는 SELECT의 WHERE 컬럼(`@v_intPostChkAmt2` ↔ `TSettleMiss.IssueType`)과 변수를 거친
  데이터 흐름(`A.YMD = @pi_strYMD` → `FETCH INTO @v_strYMD` → `SET YMD = @v_strYMD`, `@pi_strYMD` ↔
  `TSettleMiss.YMD`). 둘 다 결합 정의에 넣었다(대입 SELECT의 WHERE·ON 컬럼, 컬럼→변수 한 홉 상속 —
  전체 닫힘은 검사를 무력화하므로 한 홉). 채택된 명세서(2차 시도 86점)는 수정 전·후 L1을 통과한다.
  거짓 양성이 재시도 소진으로 번진다는 `reset-l1-check` 경고의 실물이다 — 31개 스윕이 0이어도 모델이
  새로 쓰는 산문은 스윕에 없던 모양을 낸다. 같은 항목에
  적었던 `UF_GET_COLLECTYMD:93`(표에 실린 함수의 동작 서술)은 v14 재생성이 우연히 지웠고 도구
  변경이 없어 재발 가능하다 — 재발하면 참조 함수 표의 함수명 + 동작 술어를 잡는 검사가 후보다(트리거
  문구는 코퍼스로 정할 것).
  근거: `docs/audit-reports/2026-08-23-POQSettlePrco20-axisA.md` 4절·4-3절
- **DML 범위 표·집합 술어 표의 "문장" 칸을 L1이 한 번도 검증하지 않는다** —
  `MechanicalValidator.CheckDmlScopeTable`과 `CheckSetPredicates`는 대상·WHERE 술어 컬럼·
  조인 키·리터럴 목록 등 값 칸은 대조하지만 `UPDATE N`/`DELETE N` 같은 "문장" 칸 자체는
  어느 검사도 읽지 않는다. 그 결과 채번 헬퍼(`AiService.BuildStatementOrdinals`)의 회귀 —
  "같은 줄 두 문장이 번호를 덮어쓰는" 결함이나 두 표가 다른 번호를 내는 정렬 붕괴 — 가
  L1을 그대로 통과한다. `AiServiceTests_Rich`의 단위 테스트가 지금은 잡지만, 그 테스트를
  지우면 L1은 구조적으로 알아채지 못한다.
  **함께 닫을 것** — `CheckSetPredicates`의 행 매칭(`matchingRows`를 고르는 `r.Split('|')`)은
  순진한 분할인데 같은 파일의 `ExtractSetPredicateLiteralCell`은 이미 `SplitTableRowCells`
  (이스케이프된 `\|`를 존중)로 바뀌어 있다. `'x|10|y'`가 `'x\|10\|y'`로 렌더되면 행 매칭
  쪽이 유령 칸(`10`)을 만들어 다른 사실과 거짓 매칭될 수 있다.
  근거: 2026-08-18 최종 브랜치 리뷰(FIX ROUND 3) — **이 문서가 유일한 기록**
- **인정 문장이 어느 계약을 인정했는지 구분하지 못한다** —
  `MechanicalValidator.CheckHeaderContractContradiction`과 `HeaderContractTerms`. 헤더 주석
  블록에는 Inner SP 말고 반환값 등 다른 계약도 있어서, 그 중 하나를 인정한 문장이
  "주석"+"불일치"만으로 검사를 통과시킨다. 코퍼스에 실재한다 — `UP_UTIL_STAT_PGCOLLECT_INS`의
  `Spec.md`("반환값 헤더 주석의 계약은 실제 구현과 일부 불일치합니다"). 그 SP는 EXEC가
  0건이라 오늘은 무해하다. **`HeaderContractTerms`를 내부 호출 지시어로 좁히는 안은
  기각했다** — 호출 대상을 이름으로만 지목한 정당한 인정 문장이 함께 걸린다(테스트
  `Validate_AcknowledgementSentenceWithDottedInternalCallIdentifier_ShouldPass`). 근본
  해법은 `SpecExpectations.HasInternalProcedureCall`을 bool에서 **호출 대상 이름 목록**으로
  바꾸고 인정 문장이 그 이름을 담았는지 보는 것.
  근거: 2026-08-17 14개 SP 전수 재생성 실측 — **이 문서가 유일한 기록**
- **`ComputeFenceLineFlags`에 미닫힘 펜스 폴백이 없다** —
  `MarkdownSectionLocator.FindIndexOutsideFence`가 의도적으로 갖는 폴백("오탐보다 미탐이
  훨씬 나쁘다")의 반쪽만 복제했다. 펜스가 홀수면 Markdig 헤더 검사가 먼저 떨궈 실무상
  도달 불가에 가깝다.
- **`~~~` 펜스를 두 구현 다 인식하지 않는다** — `ComputeFenceLineFlags`와
  `MarkdownSectionLocator` 어디에도 `~~~` 리터럴이 없다. 서로는 일치하나 Markdig와 다르다.
- **`SuggestedPromptFix` 5번 블록이 검출 패턴과 일치한다** — `BuildSuggestedPromptFix`.
  Actor가 이 피드백을 문서의 "검증 이력" 류 섹션에 옮겨 적으면 게이트가 자기 지시문에
  다시 걸린다. 확률은 낮고 회귀 테스트가 없다.
- **`NormalizeQualifiedName`이 per-segment 정규화가 아니다** — `[DB].[dbo].[T]`가
  `DB].[dbo].[T`가 된다. 대조 양쪽에 같은 변환을 적용해 오늘은 무해하다.
- **모호성 오류가 충돌하는 기대를 나열하지 않는다** — `ResolveSectionBody`의 마지막 분기가
  후보 섹션(`candidateSections`)은 찍지만 `candidateExpectations`는 빠뜨린다. 후보 섹션이
  하나뿐인데 같은 마지막 파트를 요구하는 UPDATE 대상이 여럿이라 모호해진 경우 무엇과
  충돌했는지 알 수 없다.

  위 4건 출처: `2026-08-09-schema-claim-verification-gate-design.md` §남은 후속 1·2·4·6

- **축 B 단계 검사(검사 A~E) 코퍼스 스윕 실측(2026-08-24)** — `output/Jobs/*/agent/steps/*.md`
  326개 전수(21개 Job + 이름이 겹치는 POQSettlePrco20 별도 1개 = 디렉터리 22개, 그중
  POQSettleProc4·7은 `PlanStructure.md`의 단계 목록 JSON을 파싱하지 못해 `agent/steps`
  자체가 없다 — 326쌍에는 영향 없음)를 스크래치 하네스로 돌려 `ValidateBatchStep`이
  새로 얹은 검사 5개(`MechanicalValidator.CheckStatementCountAgainstSpec`=A,
  `CheckAnchoredStatementFacts`=B, `CheckAnchoredStatementExtras`=C,
  `CheckSpecLocalVariablesDeclared`=D, `CheckStepIdInitialValue`=E)의 오류만 골라 세었다.

  **(1) 검사별 검출 건수(전체)** — A 234 · B 195 · C **0** · D 52 · E 129(코퍼스 재측정
  기준값 129와 정확히 일치, 8회차 실측이 재현됨). Job별 분포는 A·B·D·E가 거의 모든
  Job에 골고루 퍼져 있고(POQSettleProc2·POQSettleProc3만 D·E가 0인데, 이 둘은 아래
  ScriptDom 파싱 실패의 밀도가 특히 높은 Job이다), **C는 326개 전부에서 0건**이다.

  **(2) 이번 회차가 닫으려던 9건 중 5건** — 5건 모두 그 (Job, 단계, 검사) 좌표에서 오류가
  난다. 다만 두 건은 검사가 실제로 낸 메시지가 감사가 지목한 문구와 다르다:
  - S07/검사 A — **그대로 재현.** "`TSettleMst`에 대한 UPDATE를 8개만 담고 있습니다.
    명세서 DML 범위 표는 18개를 확정합니다."
  - S07/검사 B, S11/검사 B — **좌표는 잡히지만 메시지는 감사가 지목한 것과 다르다.**
    `CheckAnchoredStatementFacts`가 "갱신 13의 YMD·PGNAME 누락"·"갱신 9의 조인 키
    YMD·UseState 누락" 같은 문장 단위 메시지를 낸 게 아니라, **"SQL이 명세서 갱신
    번호를 주석으로 달지 않았습니다"라는 포괄 메시지 1건**을 냈다. 원인을
    `StepSqlStatementReader.Read(anchor-debug 모드로 직접 확인)`으로 재현: S07·S11
    둘 다 `/* U13: … */` 같은 앵커 주석 바로 다음 줄에 `SET @v_currentStepId = -20;`이
    끼어 있고, 그 다음에야 실제 UPDATE 문이 온다. `ReadAnchor`는 문장 바로 앞 토큰만
    보므로 이 SET 문이 앵커를 가로막아 `Anchor`가 전부 `null`이 된다(S07: 문장 8개 ·
    앵커 있음 0개). 이 SET-사이-끼임 관용구는 `AiService`의 [Precise Error Tracking]
    규칙(각 DML 직전에 `@v_currentStepId`를 갱신하라)이 명시적으로 요구하는 패턴이라
    **코퍼스 전역에서 재현된다** — B의 검출 195건 전부가 이 포괄 메시지이고(문장
    단위 메시지 "명세서가 확정한 … 이(가) 없습니다"는 0건), **C(검사 C)가 326개
    전부에서 0건인 이유도 같다** — `CheckAnchoredStatementExtras`는 `anchored.Count
    == 0`이면 즉시 반환하므로, 앵커가 전혀 안 잡히는 한 검사 C는 절대 발동하지
    않는다. 기존 주석(`CheckAnchoredStatementExtras` 문서화 주석)은 "S07·S09 두 건을
    닫지 못한다"로 좁혀 적었지만, 실측은 **코퍼스 전체에서 한 번도 발동하지 않는다**는
    훨씬 넓은 사실이다.
  - S14/검사 D — **그대로 재현.** 지역 변수 9개(`@v_intID`·`@v_strClientID`·
    `@v_strYMD`·`@v_strOutYMD`·`@v_intCLTotal`·`@v_intCLComm`·`@v_intCLVT`·
    `@v_intPostChkAmt1`·`@v_intPostChkAmt2`) 전부가 선언 없이 쓰였다는 오류가 났고,
    그중 3종(`intCLTotal`·`intCLComm`·`intCLVT`)의 명세서 타입이 정확히 `MONEY`다.
  - S13/검사 E — **그대로 재현.** "`@v_currentStepId`을(를) `0`로 초기화… `0`은(는)
    이 단계의 오류 코드 집합 (-9, 0, 1001, 1002)에 이미 있는 값입니다."

  **(3) 검사 D의 "정합성 검증 SQL" 펜스 건 — 판정: 드물게 진짜, 대세는 아니다.**
  D 검출 52건 중 정확히 **1건**(`POQSettleProc11/S08`의 `@v_strReqYMD`)만 `####
  정합성 검증 SQL` 펜스가 트리거였다 — `CheckSpecLocalVariablesDeclared`와 같은 순서
  (문서 순서 ```sql 펜스, "쓰였는데 그 펜스 안에 DECLARE가 없는" 첫 펜스가 보고를 낸다)로
  재현해 확인했다. 그 파일은 메인 SQL 펜스(54행)에서 `DECLARE @v_strReqYMD
  VARCHAR(8);`로 정확히 선언·사용하지만, 485행의 "#### 정합성 검증 SQL" 아래 첫
  ```sql 펜스(487행)가 그 변수를 재선언 없이 참조해 여기서 위반이 잡힌다. 이 펜스
  패턴(`#### 정합성 검증 SQL` 헤딩 + 뒤따르는 ```sql)은 코퍼스 326개 중 **71개**
  단계 파일에 있고, 그중 **25개(35%)는 자체 `DECLARE`를 두지만 46개(65%)는 두지
  않는다** — 즉 이 관용구 자체는 흔하지만, 그것이 D의 오탐으로 이어지는 경우(spec
  지역 변수 표에 실제로 있는 이름이 그 펜스에서 처음 걸리는 경우)는 71개 중 1개뿐이다.
  `agent/common/00-architecture.md`(POQSettleProc11 표본)를 열어 확인한 결과 이
  펜스는 어디에도 자동 실행 경로로 배선돼 있지 않다 — 골격 프롬프트의 "##
  통합 데이터 정합성 검증 SQL 세트"("validation SQL templates")를 단계 단위로
  본뜬 참고용 템플릿이고, 실행은 별도의 `ISettlementValidationService`(S15 통합
  정합성 검증 전용)와 `verification/integrity-sql.md`가 맡는다. **판정**: 이 1건은
  `CheckSpecLocalVariablesDeclared`가 "배포되는 프로시저 본문이 컴파일 안 된다"를
  막으려는 목적과, 실행되지 않는 참고용 SQL 템플릿이 변수 선언 없이 인쇄된
  것이라는 사실 사이의 개념적 불일치다 — 좁게 보면 오탐(실제 배포되는 절차는
  멀쩡히 컴파일된다), 넓게 보면 "인쇄된 대로 복붙하면 실패하는 문서"라는 진짜
  결함이다. 어느 쪽으로 보든 **빈도가 1/52(1.9%)**라 검사 D의 전반적 가치를
  훼손하지 않는다 — 다음 라운드에서 `#### 정합성 검증 SQL` 절 아래 펜스를
  `CheckSpecLocalVariablesDeclared`의 스캔 대상에서 빼는 것으로 간단히 닫을 수
  있다.

  **(4) ORDER BY 축 — 전수 측정(범위를 줄이지 않음).** 명세서 DML 범위 표의
  ORDER BY 칸에 값이 있는 행 36개를 코퍼스 전체에서 찾아, 그 SP를 흡수한 단계
  파일(들)의 원문 전체에 `ORDER BY` 토큰이 한 번이라도 있는지로 대조했다(문장
  단위 정밀 대응은 (2)에서 확인한 대로 앵커가 코퍼스 전역에서 깨져 있어 불가능
  하다 — 이 숫자는 "흡수 단계 파일 전체에 ORDER BY가 0회"라는 보수적 하한이다).
  귀속 불가(흡수 단계를 못 찾음) 4건, **미스매치(ORDER BY 요구가 있는데 흡수
  단계 파일 전체에 ORDER BY가 0회) 22건.** 과제가 지목한 실례
  `UP_UTIL_STAT_PGCOLLECT_INS/docs/Spec.md:133`(INSERT 1, ORDER BY = `INYMD,
  CLIENTID, PGNAME, MALLID`) ↔ `POQSettleBatch1/S15`가 정확히 이 22건 목록에
  있다(다른 20개 Job의 같은 SP도 전부 같은 패턴). 두 번째로 흔한 미스매치는
  `UP_UTIL_SETTLE_PROC_ETC`의 SELECT 1(ORDER BY = `OutYMD, ClientID`)로 7개
  Job에 반복된다. 표본 확인: `POQSettleBatch1/S15.md`를 직접 열어 대상 SQL이
  `GROUP BY`만 쓰고 `ORDER BY`가 전혀 없음을 확인했다(과제가 인용한 감사
  🟡 그대로). 이 숫자는 다음 라운드의 검사 채택 판단 재료로만 쓰고, 이번
  라운드에서 새 검사를 만들지 않았다.

  **(5) 거짓 양성 판정 — 표본과 방법.** C는 0건이라 표본이 없다. A·B·D는
  검출이 30건을 넘어 검사별 10건씩(체계적 표본 — 결과 CSV를 검사별로 추출한
  뒤 매 N번째 행을 취함, N은 검사별 건수/10) 표본을 뽑아 해당 `Spec.md`와
  단계 파일을 직접 열었다. E는 10건을 표본 확인했다(모두 `@v_currentStepId`를
  `0`으로 초기화하는 동일 패턴 — 이 값이 문자 그대로 0이라는 사실 자체가
  판정 근거라 오탐 여지가 구조적으로 없다).
  - **검사 D**: 10건 중 9건이 진짜(변수가 실제 실행 경로에서 미선언), 1건은
    위 (3)의 정합성 검증 SQL 펜스 건(경계 사례로 이미 반영).
  - **검사 B**: 10건 모두 위 (2)에서 설명한 포괄 메시지("주석으로 달지
    않았습니다")이고, 앵커가 실제로 없는 것은 사실이므로 메시지 자체는
    정확하다 — 다만 정밀도가 낮다(문장 단위 원인을 짚지 못한다).
  - **검사 A**: 10건 중 **3건이 오탐으로 확인됐다.**
    1. `POQSettleProc18/S12`(target=`A`, UPDATE 1개 확정·0개 검출) —
       `dbo.UP_UTIL_SETTLE_EXPECT_PROC/docs/Spec.md:209`의 DML 범위 표 "대상"
       칸이 테이블명이 아니라 SQL 별칭 리터럴 `A`를 담고 있다(스펙 저작
       결함). 단계 SQL(`S12.md:252-254`)은 실제로 `A.PGName = 'pointpay'
       … A.OutState = 2`를 그대로 담고 있어 원본 UPDATE 11은 존재한다 —
       검사가 스펙의 "대상" 칸 오탈자를 그대로 물려받아 오탐을 냈다.
    2. `POQSettleProc10/S16`(TSettleMiss, UPDATE 1개 확정·0개 검출) — 이
       단계는 "Single-Transaction Shadow Swap" 아키텍처로 `DELETE FROM
       dbo.TSettleMiss` 다음 `INSERT INTO dbo.TSettleMiss`로 재구축한다
       (`S16.md:292-305`). 원본 스펙의 "UPDATE 1개"는 이 DELETE+INSERT
       재구축으로 대체됐고, `CheckStatementCountAgainstSpec`은 종류(Kind)를
       "UPDATE"로 고정 대조하므로 이 아키텍처 전환을 인식하지 못한다.
    3. `POQSettleProc8/S08`(TSettleMst, UPDATE 15개 확정·0개 검출) — 이
       단계는 Stage 테이블 스왑(`SETTLE_POQ_DB.stage.TSettleMst_S08`)
       아키텍처를 쓴다: 15개 UPDATE 전부가 **실재하지만** `stage.TSettleMst_S08`을
       대상으로 하고(`S08.md:122-399`), 최종에 `INSERT INTO
       SETTLE_POQ_DB.dbo.TSettleMst`로 스왑한다. `BareObjectName`이
       `stage.TSettleMst_S08`의 마지막 세그먼트만 남겨(`TSettleMst_S08`)
       스펙의 `TSettleMst`와 글자가 달라 매칭이 실패한다.

    **위보다 크고 별도인 구조적 결함 — ScriptDom 전체 펜스 파싱 실패.**
    표본 확인 중 `POQSettleProc3/S08`(TSettleMst UPDATE 2개 확정·0개
    검출)을 열어 보니 해당 UPDATE 문 2개가 실제로 존재했다(`S08.md:389,413`).
    원인을 추적하면 `StepSqlStatementReader.Read`가 그 단계의 메인 ```sql
    펜스(53~478행, 424줄) 전체를 통째로 못 읽는다 — `TSql160Parser`에
    `parse-errors` 모드로 직접 돌려 확인: `EXEC @v_lockResult =
    sys.sp_getapplock @Resource = CONCAT('POQSettleProc3:S08:',
    @pi_strYMD), …`(단계 시작부 애플리케이션 락 획득 관용구, `AiService`가
    표준 패턴으로 요구하는 것과 같다) 근처에서 구문 오류를 내고, 그 순간
    펜스 전체가 "파싱 실패는 침묵한다"는 계약에 따라 통째로 버려진다 —
    같은 펜스 안의 진짜 UPDATE 문 2개까지 함께 사라진다. 코퍼스 전수를
    `StepSqlStatementReader.Read`로 다시 돌려 **326개 중 65개** 단계 파일이
    문장 0개를 낸다(그중 다수는 DML이 원래 없는 통제 전용 단계라 정상이지만),
    검사 A가 오류를 낸 116개 (Job, 단계) 좌표 중 **26개가 이 "전체 0문장"
    파일과 겹치고, 그 26개 좌표가 낸 A 메시지가 234건 중 92건(39%)** 이다 —
    이 92건은 진짜 누락인지 파서 실패로 가려진 것인지 이 스윕만으로는
    구별할 수 없다(위 POQSettleProc3/S08 표본은 후자로 확인됨). **결론**:
    검사 A는 스펙 저작 품질(별칭 오기재)·아키텍처 전환(Shadow/Stage
    swap)·SQL 파서 취약성(`EXEC @var = schema.sp @param = 식(...)`) 세
    갈래로 오탐을 내고, 그중 파서 취약성 갈래가 234건 중 최소 92건(39%)에
    영향을 준다 — 검사 A의 234건을 "실제 누락 234건"으로 읽으면 안 된다.

  하네스: `Program.cs`(스크래치, 저장소 미커밋) — `BatchStepPlanParser.TryParse`로
  각 Job의 단계 목록을 얻고, 그 단계가 흡수한 SP들의 `Spec.md`를 모아
  `SpecStatementFactsExtractor.Extract`로 사실을 뽑은 뒤 `ValidateBatchStep`을
  호출해 위 5개 검사의 오류만 메시지 문구로 골랐다(`ErrorType`이 아니라
  `StepValidationResult.Errors`가 `List<string>`이라 문구 대조가 유일한 구분 수단).
  근거: 2026-08-24 코퍼스 스윕(이 문서가 유일한 기록) — 태스크 9

- **검사 B·C의 앵커 방식 자체가 이 코퍼스에서 성립하지 않는다 — 태스크 11이 SET
  건너뛰기로 고치려다 실측으로 폐기, 태스크 12가 폴백을 침묵으로 되돌림
  (2026-08-24)** — 위 항목이 이미 기록한 "SET-사이-끼임" 사실(검사 B 검출 195건
  전부가 포괄 메시지, C는 326개 전부 0건)의 **후속**이다.

  **(1) 앵커 방식이 이 코퍼스에서 작동하지 않는다.** `ReadAnchor`는 문장 바로 앞의
  공백·주석만 본다. 실물은 `/* U1: … */` → `SET @v_currentStepId = -101;` →
  `UPDATE …` 순서이고, 그 `SET`은 `AiService`의 [Precise Error Tracking] 규칙이
  각 DML 직전에 요구하는 필수 패턴이라 코퍼스 전역에서 재현된다. 그 결과 앵커는
  326개 단계 파일 전체에서 **0개** 잡힌다.

  **(2) 단순히 `SET`을 건너뛰면 더 나빠진다 — 태스크 11 실측, 되돌림.** `ReadAnchor`가
  주석과 DML 사이의 리터럴 `SET` 한 줄을 건너뛰도록 고치면 앵커는 살아난다(S07
  0/8 → 8/8). 그런데 산출물에서 주석↔DML 대응이 이미 어긋나 있다 — 어떤 갱신은
  서술 주석만 있고 DML이 없고(미구현), 어떤 DML은 주석이 없다. 그래서 살아난
  앵커가 **틀린 문장에 붙는다.** 리뷰어가 실물 3건으로 재현했다:
  - `output/Jobs/POQSettleBatch1/agent/steps/S07.md:244`(실제로는 spec `UPDATE 16`과
    정확히 일치)가 `U15` 서술 주석을 훔쳐, "갱신 15의 컬럼이 없다"는 거짓 시정
    지시를 냈다.
  - `output/Jobs/POQSettleBatch1/agent/steps/S08.md:51`(실제 `UPDATE 3`)이
    `UPDATE 2` 주석을 훔쳤다.
  - `output/Jobs/POQSettleBatch1/agent/steps/S08.md:148`(실제 `UPDATE 11`)이
    `UPDATE 10` 주석을 훔쳤다.

  그 오귀속 오류가 `SuggestedPromptFix` → `floorFeedback`으로 재생성 프롬프트에
  실리면, 모델이 앵커를 (이미 달려 있는데) 다시 달아도 `SET`이 여전히 끼어 있어
  같은 요구가 재발한다 — `maxTries` 5회를 소진하고 단계가 하한 미달로 확정될 수
  있다. 이 시도는 통합하지 않고 되돌렸다.

  **(3) 그래서 검사 B·C가 사실상 비활성이다.** 태스크 12는
  `CheckAnchoredStatementFacts`의 "앵커가 하나도 없으면 요구를 1건 낸다"는 폴백을
  침묵으로 바꿨다 — 그 요구 문구("SQL이 명세서 갱신 번호를 주석으로 달지
  않았습니다")가 사실이 아니고(앵커는 달려 있다, 못 읽을 뿐이다) 위와 같이 해로워서다.
  검사 B는 이제 앵커가 없으면 조용히 지나간다(코퍼스 재측정: 326개 전수 0건 —
  이전 195건은 전부 이 폴백 메시지였다). 검사 C(`CheckAnchoredStatementExtras`)는
  손대지 않았다 — 이미 `anchored.Count == 0`이면 침묵하므로 계속 0건이다.
  결과적으로 두 검사 모두 이 코퍼스에서 사실상 아무것도 잡지 않는다.

  **(4) 다음 회차의 단서.** 위 3건의 오귀속 문장은 전부 **다른 spec 행과 술어
  컬럼 집합이 정확히 일치**했다(예: S07:244는 주석상 U15를 훔쳤지만 실제
  술어 컬럼은 spec의 UPDATE 16 행과 맞았다). 즉 주석 위치로 문장↔spec 행을
  대응시키는 대신, **각 문장의 최상위 WHERE 술어 컬럼·조인 키 집합을 spec DML
  범위 표의 각 행과 대조해 최선 일치를 찾는 것**이 다음 설계의 출발점으로
  보인다 — 주석이 어디 있든(또는 없든) 문장 자체의 모양으로 귀속할 수 있다.
  다만 이 대안은 이번 태스크의 쓰기 허용 범위 밖이라 시도하지 않았다.

  **(5) 파서 결함(별건, 태스크 11이 조사만 하고 못 고침) — 위 (412)-(432)의
  ScriptDom 전체 펜스 파싱 실패를 보강.** `EXEC @ret = schema.sp @param =
  CONCAT(...)`처럼 함수 호출식을 인자로 쓰는 관용구가 ScriptDom 문법 제약에
  걸려 펜스 전체가 버려지는 문제(코퍼스 326개 중 65개 파일 0문장, 검사 A
  234건 중 92건(39%)이 이 부작용)를, `Microsoft.SqlServer.TransactSql.ScriptDom`
  **`TSql100Parser`부터 `TSql180Parser`까지 전 버전으로 같은 입력을 돌려
  재현했다** — 전부 같은 자리에서 구문 오류를 낸다. 즉 이 결함은 특정 파서
  버전의 버그가 아니라 T-SQL 문법 자체(또는 ScriptDom이 그 문법을 인식하는
  범위)의 제약이라, **버전을 올려 우회하는 선택지는 닫혔다.** 남은 선택지는
  ScriptDom의 펜스 전체 파싱을 포기하고 문장 단위로 분리해 개별 파싱하는 것
  (한 문장이 실패해도 나머지는 살리는 구조) — 이번 태스크의 쓰기 허용 범위
  밖이라 시도하지 않았다.

  근거: 태스크 11 조사 기록(리뷰어 실물 재현, 이 문서가 유일한 기록) + 태스크 12
  코퍼스 재측정(검사 B: 326개 전수 0건).

### 메타데이터 / 지시서

- **`AllowExternalDatabaseConnections`가 메타데이터 계층에 도달하지 않는다** —
  `DependencyAnalysisOrchestrator`의 편의 생성자와 `DbMetadataService.GetCodeObjectDetailsAsync`가
  `includeExternalCodeObjects: true`를 하드코딩한다. **부분 완화** — 재귀 탐색 쪽에는
  게이트가 있고(`DiscoverAsync`가 외부 DB 노드를 `SkippedExternal` 처리), `01a0d5b`가
  `appsettings.json` 기본값을 `false`로 되돌렸다. 남은 것은 조회 호출 자체다.
  출처: `2026-08-03-stage1-analysis-flow-hardening-design.md` §범위 밖
- **`{specRoot}`가 `<outputRoot>/Procedures`만 덮는다** — `ArgumentTemplateResolver.ResolveSpecRootDirectory`.
  `External/<db>/Procedures/`와 `Functions/`의 명세서는 코딩 에이전트가 볼 수 없다.
- **Job 이름에 `.`이 들어가면 지시서 안내와 게이트 탐색이 어긋난다** —
  `FileMappingService.ResolveMappings`의 `baseName.LastIndexOf('.')`가 마지막 점 앞을 버린다.
  진입점 파일명 경로는 여전히 성립한다.

  위 2건 출처: `2026-08-07-migration-instructions-split-design.md` §남은 후속

- **`agent/` 직하 중복 `task-*.md`에서 `FirstOrDefault`가 열거 순서에 의존** —
  `InstructionBundleWriter`의 `DescribeStep`·`DependenciesForStep`·`SpecPathForStep`.
  진입점보다 나중에 쓰인 파일만 남기면 해결된다.
- **`BuildUnverifiedFeedback`의 폴더 규약이 4개 중 2개만 말한다** —
  `CodegenLoopPolicy.BuildUnverifiedFeedback`이 `CustOrderHist`와 `CustOrderHist.Batch`만
  들어, `CodegenArtifactNaming.JobProjectDirectoryNames`가 인정하는 밑줄 제거 변형 2개가
  빠졌다. 거짓은 아니고 누락이 실패를 만들지 않는다.
- **`SaveMigrationPlanAsync`가 `EncodePathSegment`를 쓰지 않는다** — `ReSet.Cli/Program.cs`가
  `Path.Combine(outputDir, "Procedures", $"{schema}.{name}", "docs")`를 직접 조립해,
  식별자에 `.`이나 파일명 금지문자가 있으면 `OutputPathResolver.EncodePathSegment`를 거치는
  캐시 조회 경로와 갈라진다.

### 테스트 커버리지

- **`DbMetadataService` 재귀 의존성 경로에 동작 테스트가 없다** — 라이브 DB가 있어야 실행돼
  커버되지 않는다(테스트의 `GetCodeObjectDetails*` 호출은 전부 NSubstitute 목). 이 경로에서
  분류기 위임을 통째로 되돌려도 지금은 테스트로 잡히지 않는다.
- **뮤테이션 저항 없는 테스트 2건** —
  `PlanBoundaryResolverTests.FindUncoveredRanges_EmptyDocument_ShouldReturnNothing`(조기
  반환을 지워도 통과, 값싼 경계 방어로 **의도적 유지**),
  `VerificationPipelineOrchestratorTests.Pipeline_ShouldNotEnrichTablesWhenDefinitionsAreOmitted`
  ("보강 스킵"과 "보강했으나 결과가 빔"을 구별 못 함).
- **`SpecExpectationsWiringPolicyScanner`가 `this._validator`를 못 잡는다** —
  `member.Expression is not IdentifierNameSyntax receiver` 판정이 `IdentifierNameSyntax`만
  본다. 현재 그런 사용은 없다.

### 그 밖

- **프롬프트의 pseudo-XML 블록이 `<`, `>`, `"`를 이스케이프하지 않는다** —
  `AiService.BuildDeconstructionPrompts`와 `BuildSpecSectionPrompts`의 `<sp-source-ddl>`
  조립 지점. 진짜 XML로 파싱하는 곳은 없다.
- **낡은 줄번호 인용 1건 잔존** — `CodegenWorkflowOrchestrator.RunSelfHealingWorkflowAsync`의
  주석이 `(:806)`을 가리킨다. 가리키려던 곳은 같은 클래스의 `BuildAbortResult`와
  `BuildAbortReason`(`CliFailureClassifier` 호출)이다. 이 문서의 앵커 규약과 같은 처방을 쓰면 된다.
- **SP 목록이 시작 시 1회만 로드**되어 세션 중 DB 변경이 반영되지 않는다.
- **TUI 선택 목록이 객체 디렉터리 이름만 렌더링**해 서로 다른 DB의 동명 프로시저가
  구분되지 않는다.
- **비재귀 경로가 `DependencyAnalysisOrchestrator`로 통일되지 않았다** — 요청 모델과
  파이프라인 호출, 배치 모드를 함께 재배선해야 한다.
- **파서가 한정자 붙은 `INSERT` 대상 컬럼을 참조로 세지 않는다 (2026-08-22 실측)** — `X.PRODUCTNAME`처럼
  별칭 한정자가 붙은 대상 컬럼이 `StaticAnalysis.ReferencedColumnsPerTable`에 들어가지 않는다.
  실측: `UP_UTIL_SETTLE_INS_EXTRA`의 `TSettleMst` `INSERT` 대상 40개 중 `PRODUCTNAME` 하나만
  빠졌고, 그 하나만 한정자가 붙어 있다(같은 목록의 `SERVICENAME`은 한정자 없는 자리가 하나
  더 있어 살아남았다). 귀결이 조용하다 — `SchemaPromptColumnSelector.Select`가 참조 컬럼
  기준으로 프롬프트 스키마 표를 좁히므로 그 컬럼이 **모델에게 보이지 않고**, 모델은
  "제공 스키마에 없다"고 사실대로 쓰고, `MechanicalValidator.CheckSchemaClaims`도 같은
  기준값이라 침묵한다. 감사만 `Dependencies` 전량과 대조해 잡는다(카탈로그 4-7절).
  자리: `SqlStaticParser`의 `INSERT` 대상 컬럼 수집부.
- **더티 리드 노출을 명세서가 적지 않는다** — 6회차 감사가 배포 구성 확인으로 등급을 🟡까지
  내렸으나 서술 부재는 그대로다. 이관 후 동시 실행 구성이 되면 위험이 따라간다.
  **7회차(전건 재생성 후)도 같은 자리를 다시 잡았다** — `INS_EXTRA`의 잠금 힌트 서술이
  "일부 읽기와 갱신 대상 별칭에 `WITH(NOLOCK)` 힌트가 있습니다" 한 문장으로 뭉개져,
  사전 확인 조회가 더티 리드를 허용하는지가 명세서에서 여전히 미확정이다(🟡).
- **`output/Jobs/POQSettleProc7/` 산출물 폐기 판단** (운영 결정).

---

## 닫힌 것

상세한 경위는 각 설계 문서의 해당 항목에 취소선과 함께 달려 있고, 커밋 메시지가 그 근거다.
여기서는 **무엇이 왜 닫혔는지만** 남긴다.

| 무엇 | 어떻게 |
|---|---|
| 축 A 재감사 🟠 2건 (`PGName NOT IN` 리터럴 · PG 화이트리스트) | 집합 술어를 기계 확정 재료로 승격. `DmlScopeExtractor.ExtractSetPredicates` → `AiService.BuildSetPredicateTableLines` → `MechanicalValidator.CheckSetPredicates`. 설계: `2026-08-18-set-predicate-material-design.md` |
| C# 아키텍처 규칙이 단일 어셈블리 스코프 | `8e4af04`가 `ArchitectureTests` 스텁에 `Targets`를 넣어 대상 0건 통과를 막고, `c86d7b7`이 워밍업으로 xUnit 병렬 순서 의존 거짓 실패를 없앴다 |
| `StepLogicTests` 배치 위치가 어느 지시서에도 없다 | `TaskFileComposer`가 `tests/StepLogicTests{확장자}`를 경로째 안내(`8d9ba62`·`37cd381`이 "원본을 지우지 말 것"까지 명시) |
| `ArtifactChangeDetector.Snapshot` TOCTOU 플래키 | `69a080c`·`b577f65`. **한계 그대로 옮김** — 두 테스트가 경합 자체를 재현하지 못하므로 원인 경로가 더는 던지지 않는다는 것만 보인다 |
| `AGENTS.md`의 경고 개수가 낡았다 | `d545f51`이 `AiServiceTests`의 `CS8604`를 없애 실제가 기록(8건)을 따라잡았다. 2026-08-21 클린 빌드로 확인 |
| 보강기·파서의 "유효한 블록" 판정 불일치 | `2ae7a2b`·`25319f6`·`933fb39`가 `BatchStepPlanParser.TryLocateStepsBlock` 공유로 닫음 |
| Claude 프롬프트 캐시 중단점 | `PromptCacheBreakpointPolicy` (두 번째 전송부터 찍어 1회차 잡의 캐시 쓰기 손실 없음) |
| `PlanBoundaryResolver`의 `allFound == true` 공백 | `acf5210`의 `AbsorbUncoveredRegions` |
| 레거시 전체 Job의 `nothingVerified` 무한 재시도 | `e1ccfbd`의 `MaxConsecutiveUnverifiedRetries`. 남아 있던 다른 조합은 아래 총 시도 상한이 닫았다 |
| 두 코드 생성 루프에 총 시도 상한이 없다 | `AiSettings:MaxTotalAttempts`(기본 20)를 `CodegenWorkflowOrchestrator`에 주입해 `RunSelfHealingWorkflowAsync`의 `while`과 `RunStageAsync`의 `for` 양쪽 머리에서 가드. **문서가 레거시 루프만 적었으나 회차 루프도 같은 구멍이었다** — `gate.Result == Failed`가 반복되면 `consecutiveUnverified`가 매 회차 0으로 리셋된다. 기존 두 연속 캡은 "같은 종류의 실패가 연속"을 세므로 산출물이 나오고 매핑도 성립하는 조합에서 둘 다 걸리지 않는다 |
| 통합 루프에 점수 임계값 강제가 없다 | `CriticScoreGate`를 신설해 두 루프가 같은 5축 비교를 쓴다(통합 루프는 `bestAttempt.TryRecord` **직전**, 단일 루프와 같은 순서 — `TryRecord`는 `NormalizedScore`만 읽으므로 안전하다). 같은 비교의 세 번째 사본이던 `VerificationBanner.RejectionReason`도 함께 묶었다. 프롬프트의 리터럴 `8`은 그대로 둔다 — 모델에 주는 안내일 뿐이고 게이트는 코드가 잡는다 |
| 신뢰도 점수와 검증 커버리지의 분리 표기 | `VerificationCoverage` 모델과 포매터의 `coverageLine` |
| UPDATE 컬럼 매핑표 / `UPDATE … FROM` 자기참조 / `SET` 절 동시평가 | `2026-08-09-update-mapping-contract` |
| 명세서 재발 방지 검증 게이트 | `63483f2`가 L2 Critic이 아니라 **L1**에 `MechanicalValidator.CheckSchemaClaims`를 넣고 `ab6dd5b`가 코드 펜스 오탐을 닫음 |
| L2 리뷰 호출 재시도 인프라 부재 (5개 설계 이월) | `AiRetryPolicy`(순수 판정) + `AiCallRetry`(2회·지터 500~1500ms, `MaxL2Attempts` 미소모)를 신설하고 리뷰 5곳에 걸었다. 판정 재료를 위해 API 클라이언트 6곳이 `HttpRequestException.StatusCode`를, CLI가 `CliInvocationException.Kind`를 보존한다. **생성 호출 27곳은 열려 있다.** 닫힌 것은 **예외로 표면화되는 전송 실패**뿐이다 — `AiService.ParseReviewResult`가 예외 없이 삼키는 파싱 실패, `AiRetryPolicy.Classify`가 설계 §4.1대로 `Fatal` 처리하는 빈/차단된 완료, 그리고 병렬 후보 검토의 클로저 고정이 재시도 경로에서만 겪는 인덱스 회귀는 여전히 열려 있다(「알려진 한계 → 검증 파이프라인」 참고). 설계: `2026-08-21-ai-call-retry-design.md` |
| HttpClient 타임아웃이 "사용자 취소"로 보고됐다 | 실측(.NET 10.0.10)으로 드러났다 — 타임아웃도 `TaskCanceledException`이라 `when (ex is not OperationCanceledException)` 필터 55곳이 전부 놓치고 최상위가 "사용자에 의해 중단되었습니다"를 찍었다. `AiCallRetry`가 소진 시 `AiCallFailedException`(취소 아님)으로 감싸 **리뷰 경로에서만** 닫혔다 |
| **시도 간 진동 억제** (정책 표에서 3회 미룸) | **근거 소멸로 닫음.** 2026-08-23에 8/21·8/22·8/23 로그의 Critic 채점 138건·재시도 객체 40개를 귀속해 재측정했다 — 종합점수 폭 **중앙 2점, 8/23 최대 4점**. 기록된 "20점 이상"은 2026-08-04의 1회씩 관측(기계 확정 표 이전)이고, 8/22에 남은 큰 폭(`UF_GET_INCVTAXRATE` 18점·`UF_Get_CLComm4MobileCo` 10점)은 피드백 원문이 `'실행 의미' table introduces an unsupported fact`로 밝히듯 **Critic이 기계 확정 표를 환각으로 오판한 교착**이었다. 그 교착은 `MachineConfirmedTables.CriticExemptionBlock`이 닫았고(8/22 12:58 이후 같은 객체 1차 통과), 그 뒤 `IAiService`에 이전 명세서를 넘길 근거가 남지 않았다. 백지 재작성의 흔적(한 축을 고치면 다른 축이 2~4점 역행)은 실재하나 결국 수렴하므로 아래 「돌려 봐야 아는 것」에 조건부로 남긴다 |
| **합격 기준 정책** (3회 미룸) | **현행 5축 전부 기준 게이트 유지로 닫음.** 같은 재측정에서 8/23 불합격 사유는 전부 **한 축 7~8점**이었고 종합은 86~92였다 — 종합 게이트를 병행했으면 통과했을 판들이다. 그런데 그 한 축은 진짜 결함이었다(`UP_Util_Settle_Summary` CRUD 7 = SELECT 네 질의를 한 행으로 뭉침, `UF_GET_COLLECTYMD` 정합 6 = PK 유일성을 부정하는 거짓 단서). 재시도가 그것을 고쳤다. 5축 전부 기준이 **종합 게이트가 놓쳤을 결함을 잡은 것**이므로 병행은 정확도를 낮춘다 |
| **조기 종료** (1회 미룸) | **이미 구현되어 있었다.** `VerificationPipelineOrchestrator`는 `HasDefects=false`면 그 자리에서 `L1+L2 검증 최종 통과 → 캐시 갱신`으로 루프를 끝낸다 — 8/22 `UF_GET_INCVTAXRATE` 로그로 확인(10:13:48 시도 5 합격 즉시 종료). "합격 후에도 재시도가 이어진다"는 인상은 같은 날 **별개 실행 셋**(10:10·10:24·12:58)의 시도를 한 줄로 놓은 귀속 오류였다. 결정할 것이 없다 |

### 이 목록 밖에서 닫힌 것

2026-08-18 ~ 08-21의 큰 작업들은 여기 등재된 적이 없다. 감사가 새로 찾은 결함에 대응한
것이고, 그 파이프라인은 `docs/audit-reports/` → `audit-defect-catalog.md` → 새 설계 문서다.
이 문서가 아니라 그쪽이 지금 일을 움직인다.

- **축 B 배치 골격 계약** — `BatchControlContract`·`StepInterfaceFacts` + L1 검사 6종
- **참조 함수 기계 확정 표** — 함수 동작 서술 전면 금지, 5회차 7/8 → 6회차 8/8 정합 →
  **7회차는 명세서 31개 전건 재생성 뒤에도 75행 전수 축자 일치**(인자 칸의 원문 공백·오타까지).
  재생성이 흔든 것은 전부 산문이었다
- **추출기 결함 셋** — 자기참조 별칭(`88b0aa2`) · 집합 술어 `LEFT()` 좌변(`39050de`) ·
  의존성 이름 표기(`a1bbcfe`). 6회차 재감사가 셋 다 산출물에서 확인, 축 A 🔴이 0이 됐다.
  **7회차에 🔴이 2건 다시 났지만 둘 다 이 셋과 무관하다** — 하나는 `CAST(money AS INT)`의
  반올림 미서술(4회차에 관측하고 재확인하지 않은 채 남겨 둔 부류, 카탈로그 P5), 다른 하나는
  실행 대조가 🟡에서 올린 것이다(mermaid가 금액 결정 규칙을 다르게 그린 자리, 카탈로그 P15)
- **`GlobalStatementOrdinal`의 "갱신 0"** — 정규화의 값 유실을 캐시 형식 6에서 닫음
