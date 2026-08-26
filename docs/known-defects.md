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

  **이 수치 전부의 공통 한계 — 표본이 0문장 파일 65개를 포함한다.** 아래 (5)(「거짓
  양성 판정」 안의 「ScriptDom 전체 펜스 파싱 실패」 문단)가 확인하듯 코퍼스 326개 중
  65개 단계 파일이 `StepSqlStatementReader.Read`에서 문장 0개로 읽힌다. 그 부작용은
  검사 A만의 것이 아니다 — 문장을 못 읽으면 그 문장을 보는 검사 B·D·E도 같은
  (Job, 단계) 좌표에서 함께 돌지 않으므로, 위 A·B·D·E 네 검사(C는 애초에 앵커 부재로
  0건)의 검출량은 전부 **65/326 파일이 0문장으로 읽히는 이 표본 위에서 잰 하한**이다
  (B의 195건은 이후 태스크 12가 앵커 폴백을 침묵시켜 0으로 재측정됐다 — 아래
  「검사 B·C의 앵커 방식」 항목). 다음 회차는 이 파서 결함(ScriptDom 펜스 전체 파싱을
  포기하고 문장 단위로 분리해 개별 파싱하는 것 — 아래 (5)와 「검사 B·C의 앵커 방식」
  항목의 (5)가 이미 남긴 선택지)을 먼저 닫는 것이 권고된다. 닫지 않은 채 검사를
  늘리면, 늘어난 검사도 같은 65개 표본 위에서 하한만 잰다.

  **2026-08-24 갱신(Task 20) — 위 하한의 기준이 크게 바뀌었다.** 이 문단이
  "65/326 파일이 0문장"이라 잰 것은 **태스크 9 스윕 시점(파서 결함이 아직 열려
  있던 상태)의 수치**다. 그 결함이 그 뒤 닫혀(아래 「검사 B·C의 앵커 방식」
  항목 (5), **2026-08-24 닫힘** 참고) 0문장 파일이 **65 → 16/326**으로 줄었다.
  이 절 위쪽의 A=234·B=195·D=52·E=129 네 수치는 이제 그 옛 65개 표본 위에서
  잰 **과거의 하한**이지 현재 값이 아니다 — 파서 수정 뒤 재스윕한 현재 값은
  아래 「파서 결함 수정 후 코퍼스 재스윕」 항목을 보라(검사 A는 20건).

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

    **2026-08-24 닫힘(Task 20)** — 이 파서 취약성 갈래는 아래 「검사 B·C의
    앵커 방식」 항목 (5)가 문장 단위 분리 파싱으로 닫았다. 닫힌 뒤 재스윕한
    수치는 「파서 결함 수정 후 코퍼스 재스윕」 항목을 보라.

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

  > **[상태: 이 항목의 (1)(2)는 뒤집혔다 — 다음 항목(태스크 22, `6bc3641`)을 함께
  > 읽어라.]** 아래 (1)이 진단한 "앵커 0개"와 (2)가 폐기한 SET 건너뛰기는 사실
  > 그대로지만, 태스크 22가 앵커 판독을 「직전 문장 끝 ~ 이 문장 시작 구간에 앵커
  > 모양 주석이 정확히 1개」라는 다른 규칙으로 다시 설계해 (2)의 거짓 귀속 없이
  > 앵커를 살렸다. 재스윕에서 검사 B는 326개 전수 1건 발화하고 그 1건이 진짜다
  > (S07 갱신 13). **"앵커 방식은 못 쓴다"는 이 항목의 제목은 더 이상 유효하지
  > 않다** — 유효한 것은 (3)(4)(5)의 실측과, 아래 (1)이 밝힌 "코퍼스 관용구가
  > SET을 사이에 끼운다"는 사실이다.

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

  ~~**(5) 파서 결함(별건, 태스크 11이 조사만 하고 못 고침) — 위 (412)-(432)의
  ScriptDom 전체 펜스 파싱 실패를 보강.**~~ — **2026-08-24 닫힘(Task 20).**
  이 항목이 남긴 선택지(ScriptDom의 펜스 전체 파싱을 포기하고 문장 단위로
  분리해 개별 파싱하는 것)를 택해 닫았다. `EXEC @ret = schema.sp @param =
  CONCAT(...)`처럼 함수 호출식을 인자로 쓰는 관용구가 ScriptDom 문법 제약에
  걸려 펜스 전체가 버려지는 문제는 `Microsoft.SqlServer.TransactSql.ScriptDom`
  `TSql100Parser`부터 `TSql180Parser`까지 전 버전으로 재현됐고(위 문단이 이미
  적은 대로 버전을 올려 우회하는 길은 없었다) — 실제로 택한 것은 아래 방법이다.

  **방법**: 펜스를 통째로 파싱하지 않고 `GetTokenStream`(어휘 분석만, 오류에
  안정적)으로 토큰을 얻어 **최상위(괄호 깊이 0) 세미콜론과 `BEGIN`(`TRAN`·
  `TRANSACTION` 제외) 경계로 조각내 조각별 독립 파싱**한다
  (`StepSqlStatementReader.SplitAtTopLevelSemicolons`). 한 조각의 오류가
  다른 조각에 번지지 않는다.

  **왜 "오류 지점 이후만 버리기"가 아니었나** — 실측으로 기각됐다. ScriptDom은
  오류가 나면 **그 지점 이후를 아예 담지 않는다.** `POQSettleProc3/S08` 펜스
  (12,798자)에서 오류가 offset 560에 나자 fragment는 offset 461까지 문장
  7개만 담았고 나머지 12,000자가 통째로 사라졌다. 뺄 대상이 없으므로 그
  방향은 무의미했다.

  **`BEGIN` 분할이 필요했던 이유**: 세미콜론만으로 나누면 `IF … BEGIN UPDATE …
  WHERE …; END`처럼 DML이 세미콜론 없는 `BEGIN` 바로 뒤에 오는 경우 진짜 DML이
  손실로 잡혔다(25개 표본 중 6개, 24% — `SplitAtTopLevelSemicolons`의 주석에
  근거가 남아 있다).

  **신호 변경**: `unparsedFenceCount` → **`lostStatementCount`**. 의미가
  "펜스 전체 소실"에서 "잃어버린 INSERT·UPDATE·DELETE 문장 수"로 바뀌었다.
  DML 키워드 없는 제어문 조각 실패는 세지 않는다(그렇지 않으면 거의 모든
  펜스가 손실 있음으로 잡혀 검사 A가 상시 접힌다). 검사 A는 이 값이 0보다
  크면 **여전히 개수 대조를 접는다**(어느 조합이 영향받았는지 알 수 없으므로).

  **실측(이 태스크가 이 워크트리에서 326개 전수 재확인)**:
  - 0문장 파일 **65/326 → 16/326**
  - 잃어버린 DML 문장 **200 → 134**(`BEGIN` 분할 추가 전후)
  - `POQSettleProc3/S08` 0개 → **6개 문장**
  - `POQSettleBatch1/S12` DELETE 4개 + INSERT 1개 복구, `lostStatementCount=3`

  **남은 16개 0문장 파일의 성격**(직접 확인) — 15개는 `lostStatementCount=0`이고
  정당하게 DML이 없다(부트스트랩·검증 단계 — 예: `POQSettleProc10/S01`·
  `POQSettleBatch1/S02`는 SQL 펜스 자체가 0개, `POQSettleProc8/S01`은 펜스
  1개지만 입력 검증 프로시저 정의뿐 DML 없음). 1개(`POQSettleProc18/agent/steps/
  S02.md`, `lost=1`)만 실제 DML을 잃었는데, 대상이 `batch.BatchRunLock`·
  `batch.BatchRun`(락 획득·실행 상태 갱신, 63·72·79행) 제어 표라 레거시 SP의
  `Spec.md` 추적 대상이 아니다.

  코드: `StepSqlStatementReader.cs`(`ReadFence`·`SplitAtTopLevelSemicolons`).
  닫은 커밋: `03ed07a`(2026-08-24, Task 20) — 위 (5)의 실측 수치는 태스크 21이
  같은 워크트리에서 스크래치 하네스로 326개 전수를 다시 돌려 그대로 재현했다.

  위 (1)-(4)의 근거: 태스크 11 조사 기록(리뷰어 실물 재현, 이 문서가 유일한
  기록) + 태스크 12 코퍼스 재측정(검사 B: 326개 전수 0건).

- **문장↔spec 행 대응 재설계(2026-08-24, Task 22) — 위 (4)의 단서를 실측하고
  일부 닫음. S07 갱신 13 닫힘, S11 갱신 9는 못 닫음(정직한 미해결).**
  하네스: 이 워크트리 안 스크래치 프로젝트(`StepSqlStatementReader.Read`·
  `SpecStatementFactsExtractor.Extract`·`BatchStepPlanParser.TryParse`·
  `MechanicalValidator.ValidateBatchStep`을 그대로 호출), 종료 후 삭제.

  **1단계 실측**

  **(1) 순서 정보가 얼마나 보존되는가.** `nonContiguousOrdinalGroups=0` —
  코퍼스 31개 SP 전체에서 (SP, Kind)별 Ordinal이 항상 1..N 연속이다(빠진
  번호 없음). 단조성은 지켜지지만 **미구현으로 빠진 갱신이 있으면 위치
  하나만으로는 어느 번호가 빠졌는지 알 수 없다** — 실물(`POQSettleBatch1/S07`)
  이 정확히 이 모양이다: spec은 UPDATE 18개를 확정하는데 단계는 8개
  문장뿐이고(U4~U11·U14~U15가 서술 주석만 있고 DML 없음), 앵커를 살리면
  1,2,3,13,17,18 순서로 단조 증가한다(건너뛴 자리는 앵커가 모호해 null,
  아래 (3) 참고) — **부분 순서 보존**이 정확한 표현이다.

  **(2) 내용만으로 얼마나 유일하게 결정되는가.** 단일 SP 단계(`step.
  LegacyProcedures.Count==1`, 코퍼스 195단계·다중 SP 단계는 이 코퍼스에
  **0건**) 안에서 (Kind, TargetTable) 필터만으로 후보를 좁히면 문장
  1,808개 중 **unique 678(37%) · ambiguous 623(34%) · none 507(28%)**다
  (같은 SP의 UPDATE가 거의 전부 같은 대상 테이블 하나를 쓰므로 TargetTable
  자체가 변별력이 거의 없다 — S07은 18개 UPDATE 전부가 `TSettleMst`).
  **내용만으로는 유일하게 결정되는 경우가 소수다.**

  **(3) S07 갱신 13·S11 갱신 9가 옳은 행에 매칭되는가 — 이것이 순환의
  핵심.** 둘 다 **직접 소스를 읽어 확인**했다(둘 다 실물 파일 실측, 추정
  아님):
  - **S07 갱신 13** — 단계 SQL은 `;WITH CardCost AS (...) UPDATE Y ... FROM
    TSettleMst AS Y INNER JOIN CardCost AS X ON X.PLTID=Y.PLTID AND
    X.ID=Y.ID`(CTE, 최상위 WHERE 없음). 앵커 주석 `/* U13: ... */`이 SET
    문 바로 앞에 있고 그 사이에 다른 앵커가 끼지 않아(오귀속 위험 없음)
    유일하게 매칭된다 — **내용(컬럼)으로는 모호했겠지만(같은 TargetTable을
    쓰는 다른 17개 UPDATE 후보) 앵커만으로 충분했다.**
  - **S11 갱신 9** — 단계(`POQSettleBatch1/S11`)는 **U-표기 앵커를 아예
    안 쓴다** — 원본 오류코드를 그대로 라벨로 쓴다(`-- -13: 원천카드
    수동매입...`, `SET @v_currentStepId = -13;`). `AnchorPattern`(`\bU`·
    `\b갱신`·`\bUPDATE `·`\bINSERT `·`\bDELETE `+숫자)은 이 표기를 전혀
    인식하지 못해 앵커가 **11개 문장 전부 null**이다. 대신 **개수 일치가
    완벽하다** — spec UPDATE 11개, 단계 문장 11개, 위치 i(0-based) ↔
    Ordinal i+1로 그대로 맞춰보면 index8(=UPDATE9)의 JoinColumns가
    `{CLIENTID,PGNAME,MALLID,PLTID,DiscountFlag,DiscountAmt,TxAmt,Amt}`뿐이고
    spec의 JoinKeys `{PLTID,YMD,UseState,DiscountFlag,DiscountAmt,TxAmt,
    Amt,ClientID,PGName,MallID}`에서 **YMD·UseState가 정확히 빠진다** —
    과제가 지목한 결함과 정확히 일치. **다만 이 위치 기반(개수 일치)
    매칭은 이번 태스크의 쓰기 허용 범위 안(`MechanicalValidator.cs`)에
    구현하지 않았다** — 아래 2단계·미해결 사유 참고.

  **(4) 다른 축이 쓸 만한가 — 라인·오류코드.** `라인`(원본 DDL 라인) 칸은
  단계 SQL에 대응 정보가 전혀 없다(마이그레이션 산출물은 원본 줄번호를
  보존하지 않는다) — **못 쓴다.** `SET @v_currentStepId = <오류코드>`의
  오류코드는 **SP마다 정의된 매핑이 있지만 형식이 코퍼스 전역에서
  하나가 아니다**: `UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md:46-67`은
  전용 표(`발생 UPDATE | @po_intRetVal | 처리`, `UPDATE 13 | -20`처럼
  1:1)를 갖지만 이 표 제목·모양은 **코퍼스 31개 SP 중 이 파일 1개뿐**이고
  (`grep -rl "발생 UPDATE" output/Procedures`), `UP_UTIL_SETTLE_EXPECT_PROC`
  (S11의 원본)는 같은 정보를 `## 파라미터 목록`의 `@po_intRetVal` 설명
  칸에 **산문으로**("UPDATE 1~5·6~11 실패 시 각각 -1, -2, ... -13, ...을
  설정한다") 적어 실제로 S11의 `-13` 라벨과 맞아떨어짐을 직접 대조해
  확인했다. **이 축은 실재하고 정확하지만(S07·S11 둘 다 실측으로 확인),
  구조화된 "기계 확정" 표가 아니라 SP마다 다른 자유 산문·별도 표라서
  안정적으로 파싱하려면 `SpecStatementFactsExtractor.cs` 변경이
  필요하다 — 이 파일은 이번 태스크의 쓰기 허용 범위 밖이다.**

  **2단계 결정 — 부분 채택.** "신뢰할 만한 대응이 불가능하다"는 아니다 -
  **앵커 방식은 "정확히 하나의 앵커 후보만 있는 구간"으로 좁히면 안전하게
  작동한다(내용 매칭 불필요)**. `StepSqlStatementReader.ReadAnchor`를
  "문장 바로 앞 토큰"에서 "직전 문장의 끝 ~ 이 문장의 시작 구간에 앵커
  모양 주석이 정확히 1개"로 다시 설계했다 — SET이 사이에 껴도(코퍼스
  전역 관용구) 그 SET을 자연히 건너뛰고, 미구현 자리의 서술 주석이
  둘 이상 쌓이면(오귀속 위험 신호, 실물 S07:244 등) 개수가 2개 이상이
  되어 **자동으로 침묵**한다 — 태스크 11이 실측으로 폐기한 "SET만
  건너뛰기"와 달리 거짓 귀속을 만들지 않는다(내용 매칭에 기대지 않고
  순수하게 기계적으로 판별).

  **왜 위치 기반(개수 일치, S11의 유일한 활로)은 구현하지 않았는가.**
  구현하려면 `MechanicalValidator.CheckAnchoredStatementFacts`에
  "단일 SP + 문장수==spec 행수(같은 Kind)면 위치로 매칭"을 추가해야
  하는데, **코퍼스 전수 스윕으로 이 경로의 부작용을 먼저 쟀다** — UPDATE만
  놓고 위치 매칭을 시험 적용하면 703개 위치 중 predicate 132건·join
  101건이 "결측"으로 뜬다. 표본을 열어 확인한 결과 **다수가 거짓 양성**이다
  — 원본 단일 UPDATE를 `UPDATE 대상 ... FROM 대상 JOIN <CTE·파생 테이블>
  ON <좁은 키>`로 재구성하는 관용구(계산용 서브쿼리, S07 U2·U13·U17이
  실물)가 흔해서, 진짜 필터가 최상위가 아니라 그 서브쿼리 WHERE 안에
  있는데 최상위만 보는 JoinColumns가 이를 "없다"고 오판한다. 위치 기반은
  **이 오탐 갈래를 (Ordinal, Kind)만 보던 예전 매칭보다 훨씬 넓은 코퍼스에
  노출시켜** 위험이 더 크다. 이 회차는 그 오탐 갈래 중 확인 가능한 만큼
  (`HasOpaqueJoinSource`, 아래)을 닫았지만, S11처럼 **앵커가 아예 없어
  위치 기반이 유일한 활로인 자리**까지 안전하게 여는 것은 이 스윕이
  보여준 잔여 위험(CTE 사각지대) 때문에 이번 라운드에서는 보류했다 —
  "귀속할 수 없으면 침묵"을 지키려면 위치 기반의 오탐 갈래를 더 좁히는
  후속 설계가 먼저 필요하다.

  **3단계 구현 — S07은 실물로 닫혔다, S11은 못 닫았다.**
  - `StepSqlStatementReader.ReadAnchor` 재설계(위 2단계) — 코퍼스 재스윕
    (`ValidateBatchStep` 실전 호출, 326개 전수)으로 검사 B가 **1건**
    발화했고 그 1건이 정확히 `POQSettleBatch1/S07`의 "UPDATE 13(갱신 13)
    문장에 명세서가 확정한 최상위 WHERE 술어 컬럼 YMD, PGNAME이(가)
    없습니다"다 — **과제가 닫으려던 S07 🟠 그대로.**
  - 그런데 앵커를 살리자마자 **두 갈래 거짓 양성이 새로 드러났다**(둘 다
    검사 B·C가 예전엔 항상 침묵이라 한 번도 발화한 적 없던 자리):
    1. **대상 테이블 미대조** — `CheckAnchoredStatementFacts`·
       `CheckAnchoredStatementExtras` 둘 다 (Ordinal, Kind)만 보고
       TargetTable을 안 봤다. 실물(`POQSettleProc10/S08`)은 원본
       `TSettleMst` 대신 섀도·스테이징 테이블(`batch.
       POQSettleLedgerStageImage`)을 갱신하는데, 그 스테이징 전용 제어
       컬럼(`ImageRunId`·`ImageType`)이 원본 predicate와 안 맞아
       "명세서에 없는 술어"를 **12건**, "확정한 컬럼이 없다"를 **2건**
       거짓으로 냈다. **고침** — 후보 필터에 TargetTable 일치를 추가했다
       (검사 A가 이미 (Kind, TargetTable)로 대조하는 것과 같은 규약).
    2. **CTE·파생 테이블 조인 파트너의 조인 키 사각지대** — 위 2단계가
       설명한 계산용 서브쿼리 관용구. `S07`의 U2("PGName" 조인 키
       거짓 결측)·U13(predicate는 진짜 결측이지만 join 쪽 "ClientID,
       CardCPID"는 거짓)·U17("PGName, MallID" 거짓 결측) 3곳에서
       재현됐다. **고침** — `StepSqlStatementReader`에
       `HasOpaqueJoinSource`(FROM절 조인 파트너에 CTE·파생 테이블이
       있으면 true, TDD 3건으로 확인) 신호를 추가하고,
       `CheckAnchoredStatementFacts`가 이 신호가 서면 "조인 키" 서브
       체크만 접는다(최상위 WHERE 술어 컬럼 체크는 이 사각지대와
       무관해 그대로 둔다 — S07 U13의 진짜 결함은 이쪽에서 계속 잡힌다).
  - **재스윕 결과(326개 전수, `ValidateBatchStep` 실전 호출)**: 검사
    B **1건**(S07 갱신 13, 진짜) · 검사 C **0건**. 위 두 거짓 양성
    갈래(대상 테이블 불일치·CTE 조인 사각지대)가 낸 항목 전부(검사 B
    7건·검사 C 12건, 고치기 전 스윕에서 관측) **소멸했고, 새 거짓
    양성은 관측되지 않았다.**
  - **S11 갱신 9는 못 닫았다** — S11은 앵커가 0개(위 (3))라 검사 B가
    이 단계 전체에서 조기 반환한다(Task 12가 이미 확립한 정책, 이번에도
    유지). 위치 기반 매칭을 구현하지 않기로 한 결정(위 2단계) 때문에
    이 회차는 이 좌표를 닫지 못했다 — **정직한 미해결**이다.
  - **검사 A·D·E 회귀 확인** — `dotnet test`(코어 프로젝트) 366개 중
    `StepSqlStatementReaderTests`·`MechanicalValidatorTests` 전수
    통과(회귀 0). 검사 A·D·E는 이번 라운드가 건드리지 않은 로직이라
    이 스윕 대상에 넣지 않았다(전체 `dotnet test` 2686 통과·2건 건너뜀
    — 건너뜀 2건은 `output.bak-2026-08-22` 스냅샷 부재로 인한 사전
    존재 스킵, 이번 변경과 무관 — **확인**).

  **확인한 것과 확인하지 못한 것**
  - **확인**: S07 갱신 13이 실물 코퍼스에서 정확히 잡힘(`ValidateBatchStep`
    실전 호출, 메시지 문구까지 대조). CTE 사각지대·대상 테이블 불일치
    거짓 양성이 고치기 전 스윕에서 실재했고 고친 뒤 사라짐(전·후 스윕
    직접 비교). 326개 전수에서 검사 B·C 거짓 양성 0건(스윕 전체 출력을
    수작업으로 다 읽었다 — 발화 자체가 1건뿐이라 표본이 아니라 전수).
    S11의 오류코드-라벨 관용구가 `EXPECT_PROC/docs/Spec.md:80`의 산문과
    정확히 일치함(직접 대조).
  - **확인하지 못한 것**: S11 갱신 9는 닫지 못했다(정직하게 미해결로
    남긴다). 위치 기반(개수 일치) 매칭을 실제로 구현했을 때 코퍼스
    전체에서 몇 건이 새로 발화하고 그중 거짓 양성 비율이 얼마인지는
    측정만 했고(위 2단계, UPDATE 703위치 중 predicate 132·join 101건
    "결측" 후보) 실제 구현·전수 표본 확인까지는 안 갔다 — 다음 회차가
    이 수치를 출발점으로 쓸 수 있다. 검사 C가 코퍼스에서 발화할 조건
    (S11처럼 앵커가 없는 단계에서는 검사 B와 함께 조기 반환)은 이번
    실측 범위 밖이다.

  **다음 회차 제안** — S11류(오류코드 라벨, 앵커 0개)를 닫으려면 둘 중
  하나가 필요하다: (a) `SpecStatementFactsExtractor.cs`(이번 태스크
  쓰기 범위 밖)를 넓혀 `발생 UPDATE`류 표·`@po_intRetVal` 산문에서
  오류코드→Ordinal 매핑을 기계 확정 재료로 뽑는 것, 또는 (b)
  `MechanicalValidator.cs`에 "단일 SP + 문장수==spec 행수" 위치 기반
  매칭을 추가하되 이번 스윕이 드러낸 CTE·파생 테이블 조인 사각지대를
  predicate 체크에도 넓혀 막을 방법을 먼저 설계하는 것(현재는 join
  체크만 `HasOpaqueJoinSource`로 막았다 — predicate 체크가 CTE 사각지대의
  영향을 받는지는 표본에서 못 봤지만 위치 기반을 켜면 더 넓은 코퍼스에서
  드러날 수 있다).

  하네스: 스크래치 콘솔 프로젝트(`ReSet.Core.csproj` 참조, 워크트리
  안, 저장소 미커밋, 종료 후 삭제) — `StepSqlStatementReader.Read`·
  `SpecStatementFactsExtractor.Extract`·`BatchStepPlanParser.TryParse`로
  코퍼스 326개 단계를 읽고, `MechanicalValidator.ValidateBatchStep`을
  실전 그대로 호출해 검사 B·C 메시지만 문구로 걸렀다(다른 검사와 같은
  구분 방법 — `StepValidationResult.Errors`가 `List<string>`).
  근거: 2026-08-24 코퍼스 스윕(이 문서가 유일한 기록) — 태스크 22.

- **검사 B·C 조건 (B) 잔여 108건 표본 20건 판정 — 부분 실측(2026-08-25, Task 11).**
  **미완 고지 — 이 항목은 부분 실측이다.** 표본 20건 중 **검사 B 10건은 원본
  DDL·Spec.md 대조까지 전부 끝냈다**(진짜 2 · 거짓양성 8). **검사 C 표본 10건
  중 `HasOpaqueJoinSource=True` 3건(#14·#17·#20)도 대조를 끝냈다**(전부
  거짓양성). **나머지 검사 C 7건(#11-13·#15-16·#18-19, 전부 opaque=False)은
  미검증**이다(좌표·진단값만, "판정불가 — 미검증"). 다음 회차가 이 7건부터
  이어받으면 하네스·표본 선정 비용 없이 바로 판정에 들어갈 수 있다.

  **발화량 실측(조건 B, 326개 전수)** — 검사 B **70건** · 검사 C **38건**(기대치 70·38과
  정확히 일치).

  **하네스**: 스크래치 콘솔 프로젝트
  (`/private/tmp/claude-501/-Users-payletter-git-root-ReSet/ad026d45-890b-4455-a887-8fbd0518e8d5/scratchpad/task11-sweep/`,
  `ReSet.Core.csproj` 참조, 저장소 미커밋). `output/Procedures/*/raw/metadata.json`을
  `SpDefinition`으로 역직렬화해 `DmlScopeExtractor.ExtractErrorCodes(DdlText, dateParam)`로
  SP별 오류 코드 맵을 만들고(같은 코드가 둘 이상 문장에 붙으면 Spec 레벨과 같은 dedup
  규칙으로 제거), `SpecStatementFactsExtractor.Extract(specs)`로 만든 (A)조건 재료에
  `with` 식으로 그 맵을 `ErrorCodeToOrdinal`에 주입해 (B)조건을 만들었다. 각 Job의
  `raw/PlanStructure.md`를 `BatchStepPlanParser.TryParse`로 읽고 `agent/steps/S*.md`마다
  `MechanicalValidator.ValidateBatchStep`을 실전 그대로 호출해(`stepInterfaces`·
  `runRowOwnedTables`는 null, `statementFactsByProcedure`엔 (B)조건 재료, `allSteps`엔
  그 Job의 전체 단계) 검사 B·C 메시지만 문구로 걸렀다. `MechanicalValidator.BareObjectName`은
  internal이라 같은 로직(마지막 점 뒤만 남기고 대괄호 트림)을 하네스에 복제했다.
  추가로 `CheckAnchoredStatementFacts`/`CheckAnchoredStatementExtras`의 그룹핑
  (`ResolveOrdinal`·`MergeErrorCodeMaps`)을 표본 진단용으로 하네스에 복제해, 표본
  20건 각각의 `TargetTable`·`HasOpaqueJoinSource`·`PredicateColumns`·`JoinColumns`·
  앵커(U/코드)를 함께 뽑았다.

  **표본 선정** — 검사 B 70건 중 S07 갱신 13(스텝 코드가 정확히 "S07"인 5건)과
  S11 갱신 9(1건, `POQSettleBatch1/S11`)를 제외한 64건(70−5−1=64, 과제가 예고한
  "~64건"과 일치)에서 서로 다른 (잡, 단계, 갱신 번호) 조합 10건을 잡았다. 검사 C
  38건에서 같은 방식으로 10건. 한 잡에 몰리지 않게 배분했다(B 표본은 8개 잡,
  C 표본은 8개 잡에 분산).

  **표본 20건 좌표·진단·판정**

  | # | 검사 | 좌표(잡/단계) | 종류·갱신 | 누락/초과 컬럼(메시지) | HasOpaqueJoinSource | Predicate(실측) | 판정 |
  |---|---|---|---|---|---|---|---|
  | 1 | B(술어) | POQSettlePrco20/S06 | UPDATE 2 | YMD,PGName,DiscountFlag 없음 | **True** | `[]` | **거짓양성** — CTE(`PromoSource`→`PromoCalc`) 안에 YMD/PGName/DiscountFlag 필터가 있고, 최상위 UPDATE는 `ON A.PLTID=B.PLTID AND A.ID=B.ID`만 조인한다. Spec.md 254-256행이 이 조건들을 "최상위" 스코프로 적었으나(원본 SP 기준) 이행 시 CTE로 재구성됐다. `output/Jobs/POQSettlePrco20/agent/steps/S06.md:98-197`, `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md:225,254-257` |
  | 2 | B(조인) | POQSettleProc11/S09 | UPDATE 2 | ExtraType 없음 | False | `[ProcYMD,YMD,CompanySalesType,UseState,TxAmt,ExtraSettleFlag]` | **거짓양성** — ExtraType 필터(`P.ExtraType IN (2,3)`)는 이 UPDATE보다 먼저 실행되는 INSERT가 채우는 스테이징 테이블 `batch.S09SourceCardExtraScope`를 만들 때 걸린다(`output/Jobs/POQSettleProc11/agent/steps/S09.md:300-336`). INSERT는 `IsCandidateForAnchoredStatementCheck`가 후보에서 빼므로 이 필터가 검사에 보이지 않는다 — CTE가 아니라 "2단계 스테이징 선계산" 아키텍처의 사각지대 |
  | 3 | B(술어) | POQSettleProc11/S06 | UPDATE 13 | ID 없음 | **True** | `[YMD,PGName,UseState,AYMD]` | **거짓양성(추정, 근거 확인함)** — 원본 SP는 `TSettleMst`를 자기 자신에 PLTID+ID로 조인해 필터링했으나(Spec.md 236·321-322행), 이행 코드는 `CROSS APPLY`로 대상 행에 직접 상관 계산해 그 자기조인 자체가 사라졌다(`output/Jobs/POQSettleProc11/agent/steps/S06.md:379-446`). CROSS APPLY는 조인 키 없이 행 단위 상관이라 "ID"가 구조적으로 불필요해졌다 — 다만 PLTID+ID 유일성 전제가 실제로 깨지는 배포가 있는지는 확인 못 함 |
  | 4 | B(술어) | POQSettleProc12/S08 | UPDATE 7 | PLTID 없음 | **True** | `[]` | **거짓양성** — CTE(`CandidatePLTID`→`CancelGroup`) 안에서만 PLTID를 쓰고, 최상위 UPDATE는 `ON X.ID=K.MaxID`로만 조인한다(`output/Jobs/POQSettleProc12/agent/steps/S08.md:250-299`) |
  | 5 | B(술어+조인) | POQSettleProc13/S09 | UPDATE 3 | PGNAME,MALLID,CollectPeriodID,CollectFlag(술어)+PGNAME,MALLID,CollectPeriodID(조인) 없음 | False | `[YMD,InState,InYMD]` | **진짜 결함(확인)** — 원본 DDL(`raw/metadata.json` DdlText 69-80행)이 `TPGCMRate B`·`TPGCollectPeriodMst C`를 PGNAME/MALLID/CollectPeriodID로 조인하고 CollectFlag=1까지 재검증하는데, 이행 SQL(`output/Jobs/POQSettleProc13/agent/steps/S09.md:99-107`)은 이 조인·필터를 통째로 뺐다 — `WHERE A.YMD=@pi_strYMD AND A.InState=1 AND ISNULL(A.InYMD,'')=''`뿐이다. CTE·스테이징 어느 쪽도 아니고 원본이 명시한 재검증 조건이 순수 누락됐다 |
  | 6 | B(술어) | POQSettleProc17/S07 | UPDATE 10 | MALLID 없음 | False | `[YMD,PGName,TID,CID,UseState,AYMD]` | **진짜 결함(확인)** — 원본 DDL(`raw/metadata.json` DdlText 271-280행)이 `TSettleMst A ,TPGCMRate B WITH(NOLOCK)`를 `A.PGNAME=B.PGNAME AND A.MALLID=B.MALLID`로 조인하는데(SET절은 B 컬럼을 안 쓴다 — 순수 존재 필터), 이행 SQL(`output/Jobs/POQSettleProc17/agent/steps/S07.md:339-352`)은 이 조인 자체를 뺐다. #5와 같은 갈래(원본이 명시한 존재-필터용 조인이 이행에서 통째로 소거) |
  | 7 | B(술어) | POQSettleProc19/S11 | UPDATE 7 | PLTID 없음 | **True** | `[]` | **거짓양성** — #4와 동일 패턴(CTE `PartialTarget`, 최상위 조인은 `X.ID=P.ID`뿐). `output/Jobs/POQSettleProc19/agent/steps/S11.md:281-317` |
  | 8 | B(술어) | POQSettleProc19/S11 | UPDATE 10 | CYMD,AYMD,RefundFlag 없음 | False | `[YMD,PGName,UseState]` | **거짓양성(확인) — 세 번째 원인 갈래: 앵커 코드 자체가 착오.** 이행 SQL의 이 문장은 주석("9. easybank 취소 및 부분취소 수수료")·필터(`PGName='easybank'`)로 보아 원본의 **UPDATE 9**(Spec.md 276·361·490행 — "갱신 9는 easybank")이고, 원본 DDL은 이 문장에 오류 코드 **`-10`**을 쓴다(`raw/metadata.json` DdlText 300-320행). 그런데 이행 SQL은 이 문장에 `SET @v_currentStepId = -11`을 달았다 — `-11`은 원본에서 **다른 문장**(KFTC/INIBANK, UPDATE 10)의 코드다(DdlText 329-345행). 코드 앵커가 엉뚱한 스펙 행(UPDATE10)에 귀속시켜 "CYMD,AYMD,RefundFlag 없음"을 냈지만, 올바른 행(UPDATE9: CLIENTID,PGNAME,MALLID,YMD,USESTATE)과 대조하면 이 문장의 실제 컬럼(Predicate+Join)이 전부 충족한다 — CTE·스테이징과는 다른 원인(이행 코드 자신의 오류 코드 오기재) |
  | 9 | B(술어) | POQSettleProc8/S10 | UPDATE 5 | ProcYMD,YMD,PGNAME,CompanySalesType,TxAmt,CLVTType,ExtraSettleFlag 없음(전량) | **True** | `[]` | **거짓양성** — CTE(`BeforeValue`) 안에 전체 필터가 있고 최상위는 `ON B.ID=S.ID`뿐(`output/Jobs/POQSettleProc8/agent/steps/S10.md:419-453`) |
  | 10 | B(술어) | POQSettleProc9/S13 | DELETE 4 | OUTSTATE 없음 | False | `[]`(Join은 `[RunId,TargetName,YMD,OUTYMD]`) | **거짓양성(확인) — 네 번째 원인: 스테이징 키 테이블 아키텍처.** 레거시 `UP_Util_Settle_Summary`의 원본 DELETE 4는 단순 `WHERE YMD=@pi_strYMD AND OUTSTATE IN (2,9)`(raw DdlText 64-66행)이지만, S13은 "SummarySwapExecutor: 검증된 작업 결과를 네 운영 테이블에 단일 트랜잭션으로 교체"하는 아키텍처로 이행돼, 실제 DELETE는 사전 계산된 `batch.POQSettleS13AffectedKey`(RunId/TargetName/YMD/OUTYMD로 조인)에만 의존한다(`output/Jobs/POQSettleProc9/agent/steps/S13.md:246-254`). OUTSTATE 필터는 그 키 테이블을 채우는 앞선 INSERT/스테이징 단계에 있을 것으로 추정되나, INSERT는 `IsCandidateForAnchoredStatementCheck`가 후보에서 빼 검사에 보이지 않는다 — #2와 같은 "2단계 스테이징" 갈래 |
  | 11 | C | POQSettleBatch1/S09 | UPDATE 2 | USESTATE 초과 | False | `[ProcYMD,YMD,PGNAME,CompanySalesType,TxAmt,ExtraSettleFlag,OutState,OutYMD,USESTATE]` | **판정불가 — 미검증** |
  | 12 | C | POQSettleBatch1/S10 | UPDATE 2 | UseState 초과 | False | `[ProcYMD,YMD,CompanySalesType,UseState,TxAmt,ExtraSettleFlag]`(Join에 PGName,ExtraType) | **판정불가 — 미검증** |
  | 13 | C | POQSettlePrco20/S06 | UPDATE 12 | PGName 초과 | False | `[YMD,PGName,UseState,AYMD,DiscountFlag,ExtraSettleFlag]` | **판정불가 — 미검증** |
  | 14 | C | POQSettleProc11/S06 | UPDATE 13 | UseState,AYMD 초과 | **True**(#3과 동일 문장) | `[YMD,PGName,UseState,AYMD]` | **거짓양성(확인)** — #3과 같은 문장. UseState·AYMD는 원본 Spec.md의 "파생 테이블 X" 스코프 술어(`(A.UseState<>1 OR (A.UseState=1 AND A.YMD=A.AYMD))`, 321-327행)였는데, CROSS APPLY로 자기조인이 사라지며 대상에 직접 걸리는 최상위 WHERE로 옮겨왔다. 명세서의 "최상위" 술어 컬럼 칸(PLTID,ID,YMD,PGNAME)은 원본 구조를 반영할 뿐 이행 구조의 재배치를 못 따라간다 |
  | 15 | C | POQSettleProc11/S06 | UPDATE 18 | YMD,OutState 초과 | False | `[YMD,UseState,OutState]` | **판정불가 — 미검증** |
  | 16 | C | POQSettleProc12/S07 | UPDATE 1 | ExtraSettleFlag 초과 | False | `[YMD,UseState,ExtraSettleFlag,OrgDiscountAmt,PGName]` | **판정불가 — 미검증** |
  | 17 | C | POQSettleProc17/S08 | UPDATE 7 | UseState 초과 | **True** | `[PGName,UseState]` | **거짓양성(확인)** — 레거시 `UP_UTIL_SETTLE_COMM_UPD` UPDATE 7. 원본은 UseState IN(2) 필터를 "파생 테이블 D" 스코프에 둔다(Spec.md 344행). 이 잡의 이행 SQL은 CTE `K` 안에 UseState 필터를 두지 않는 대신 최상위 `WHERE X.PGName IN(...) AND X.UseState=2`(`output/Jobs/POQSettleProc17/agent/steps/S08.md:222-258`)로 옮겨 같은 제약을 강제한다 — CTE 사각지대의 거울상(필터가 CTE **밖으로** 나오면 명세서의 "최상위" 칸에 없는 이름이라 "초과"로 오탐) |
  | 18 | C | POQSettleProc19/S11 | UPDATE 11 | RefundFlag,CYMD,AYMD 초과 | False | `[YMD,PGName,RefundFlag,UseState,CYMD,AYMD]` | **판정불가 — 미검증** |
  | 19 | C | POQSettleProc3/S04 | UPDATE 1 | ExtraSettleFlag 초과 | False | `[YMD,UseState,OrgDiscountAmt,ExtraSettleFlag,PGName]` | **판정불가 — 미검증** |
  | 20 | C | POQSettleProc16/S08 | UPDATE 7 | CommissionCancelFlag 초과 | **True** | `[CommissionCancelFlag]` | **거짓양성(확인)** — #17과 같은 UPDATE 7(COMM_UPD). 원본의 "파생 테이블 D" 스코프 필터 `B.CommissionCancelFlag=1`(Spec.md 345행)이 이 잡에서는 최상위 조인 파트너 `C`(TClientSettleRate)를 직접 참조하는 `WHERE C.CommissionCancelFlag=1`로 옮겨왔다(`output/Jobs/POQSettleProc16/agent/steps/S08.md:289-335`) — #17과 같은 "CTE 필터 재배치" 갈래 |

  **CTE·파생 테이블 사각지대 갈래 건수(과제가 가장 값지다고 지목한 산출물) —
  20건 표본 전수 확정.** 표본 20건 중 `HasOpaqueJoinSource=True`가 **7건**
  (B에서 5건: #1·#3·#4·#7·#9, C에서 2건: #17·#20 — #14는 #3과 같은 문장이라
  중복 집계하지 않음). **이 7건 전부 원본 DDL·이행 SQL 대조로 판정을 끝냈고
  전부 거짓양성이다** — 두 방향으로 갈린다:
  - **은닉형(4건, #1·#4·#7·#9)** — Predicate가 완전히 빈 배열. 원본이 요구한
    최상위 술어가 CTE 안에 통째로 숨어 검사가 "없다"고 오판한다. 태스크 22가
    조인 키 체크만 접었던 이 사각지대가 **최상위 WHERE 술어 체크에도 그대로
    번진다는 것을 직접 확인했다.**
  - **역전형(3건, #3·#14·#17·#20 — #3·#14는 같은 문장)** — 원본에서 CTE·
    자기조인 안에 있던 필터가 이 잡의 이행 코드에서는 오히려 **최상위로
    끌려나와** 있다(CROSS APPLY 자기조인 소거, 또는 CTE 밖 WHERE로 재배치).
    명세서의 "최상위" 술어 칸은 원본 구조 기준이라 이 이름들을 모르고, 검사
    C가 "명세서에 없는 술어"로 오판한다.

  **결론 — HasOpaqueJoinSource가 서면 predicate 체크(검사 B)와 extras
  체크(검사 C) 둘 다 접어야 한다는 근거가 이번 표본 7건 전수로 섰다.**
  은닉형은 검사 B만, 역전형은 검사 C만 발화했지만 원인은 하나(CTE·자기조인
  구조가 명세서의 "최상위/파생" 스코프 라벨과 이행 코드의 실제 구조를
  어긋나게 만든다)다.

  **네 번째 원인 갈래 — 앵커 코드 자체의 착오(#8).** CTE 은닉·스테이징 아키텍처와
  달리, #8은 검사도 재료도 옳고 **이행 코드 자신이 원본과 다른 오류 코드를
  문장에 붙였다**(easybank 문장에 KFTC/INIBANK의 코드 `-11`을 닮). 코드 앵커
  방식(태스크 6)의 전제("이행 코드가 원본 오류 코드를 그대로 보존한다")가 깨지는
  실물이다 — 이 코퍼스에 몇 건이나 더 있는지는 이번 표본 밖이라 모른다.

  **오탐률(참고용, 외삽 금지) — 20건 표본 중 13건 판정 완료.** 검사 B 10건
  전수 + 검사 C의 opaque=True 3건, 합쳐서 13건. 진짜 **2건**(#5·#6, 둘 다
  "원본이 명시한 존재-필터 조인이 이행에서 소거" 같은 갈래) · 거짓양성
  **11건**(#1·#4·#7·#9는 CTE 은닉, #2·#10은 스테이징 키 테이블, #3·#14는
  CROSS APPLY 자기조인 소거, #8은 앵커 코드 착오, #17·#20은 CTE 필터 역전
  재배치) — 오탐률 11/13 ≈ **85%**. **이 비율을 103건 전체나 남은 검사 C
  7건으로 외삽하면 안 된다** — 표본 선정이 다양성 위주였고(무작위가 아니다),
  판정을 마친 13건 중 7건이 opaque=True(전부 거짓양성)라 그쪽으로 크게
  치우쳐 있다. opaque=False인 검사 C 7건(#11-13·#15-16·#18-19)은 전부
  미검증이다.

  **다음 회차 시작점** — (a) 검사 C의 opaque=False 7건(#11-13·#15-16·
  #18-19) 원본 DDL·Spec.md 대조(이 문서가 좌표·진단값을 이미 다 잡아 뒀다),
  (b) #8류(앵커 코드 착오)가 이 코퍼스에 몇 건이나 더 있는지 별도로 스윕할
  가치가 있다 — 코드 앵커의 전제 자체를 흔드는 사실이다, (c) 위 "역전형"
  거짓양성(#3·#14·#17·#20)을 닫으려면 검사 B·C 둘 다 HasOpaqueJoinSource가
  서면 그 그룹의 predicate·extras 체크를 함께 접어야 한다 — 지금 태스크
  22가 조인 키 체크에만 건 조건을 predicate·extras 체크로 넓히는 설계가
  다음 회차의 명확한 시작점이다(코드 변경은 이번 태스크 범위 밖).

  근거: 2026-08-25 코퍼스 스윕 + 표본 진단(이 문서가 유일한 기록) — Task 11
  (부분 실측, 이 회차 4번째 도구 호출 한도 중단으로 조사를 끊고 씀. 검사 B
  표본 10건은 전수 확정, 검사 C 표본 10건은 좌표·진단값만 남기고 미검증).

- **오류 코드 앵커 코퍼스 스윕 게이트 실측(2026-08-25, Task 8) — 캐시 인상(Task 9) 전 오탐 게이트.
  (A)명세서 그대로(코드 매핑 빈 사전)·(B)DDL에서 뽑은 매핑 주입(재생성 후 상태) 두 조건으로 쟀다.**

  **닭-달걀 해법(설계 §4)**: 코퍼스가 세대 16이라 「오류 코드」 표가 아직 어느 `Spec.md`에도
  없다(그 표는 캐시 17 재생성에서 생긴다 — Task 9). 그래서 하네스가 명세서를 기다리지 않고
  `DmlScopeExtractor.ExtractErrorCodes(ddlText, dateParameterName)`를 원본 DDL(`raw/metadata.json`의
  `DdlText`, 날짜 파라미터는 `SpecExpectations.ResolveDateParameter`와 같은 규칙으로 `ProcedureParameters`에서
  "YMD" 포함 이름을 찾아 해석)에 직접 돌려 매핑을 만들고, `SpecStatementFacts.ErrorCodeToOrdinal`에
  `with` 식으로 주입해 검증기에 먹였다. 기계 확정 표는 축자 전사 계약이라 추출기 출력이 곧
  재생성 후 표 내용이다.

  **다섯 수치**

  ① 「오류 코드」 표가 나오는 SP 수: **12/31**(`UP_Util_PG_Client_CMRate_Ins`·`UP_UTIL_SETTLE_CANCEL_INS`·
  `UP_UTIL_SETTLE_COMM_UPD`·`UP_UTIL_SETTLE_EXCEPTION_PROC`·`UP_UTIL_SETTLE_EXPECT_PROC`·
  `UP_UTIL_SETTLE_INS`·`UP_UTIL_SETTLE_INS_EXTRA`·`UP_UTIL_SETTLE_INS_EXTRA4PLCARD`·
  `UP_Util_Settle_Summary`·`UP_UTIL_SETTLE_SUMMARY_ETC`·`UP_UTIL_SETTLE_SUMMARY_EXTRA`·
  `UP_UTIL_STAT_PGCOLLECT_INS`, 1~18행). 나머지 19개는 10개 SQL 스칼라 함수(`Functions`)+
  7개 외부 함수(`External/SETTLE_CARD_DB/Functions`) 17개(가드 관용구 자체가 없는 함수라
  당연히 빈 표)와, DML은 있으나 가드가 없는 프로시저 2개(③ 참고)다.

  ② 가드 안에 비음수 코드를 두는 SP 수: **2** — `UP_UTIL_SETTLE_SUMMARY_ETC`
  (DELETE 1=1001, INSERT 1=1002)·`UP_UTIL_SETTLE_SUMMARY_EXTRA`(DELETE 1~4=4001,4003,4005,4007·
  INSERT 1~4=4002,4004,4006,4008). §2(단계 쪽 코드 앵커 판독)는 음수만 후보로 잡으므로 이 10개
  행은 원본 표에 기록되되 환산 상대를 영원히 못 만난다(설계 §2가 이미 예견한 대가).

  ③ TRY…CATCH로 현대화돼 가드가 없는 SP 수: **2** — `UP_UTIL_SETTLE_PROC_ETC`·
  `UP_Util_Settle_Summary_AcqManual`(둘 다 `BEGIN TRY`를 씀, `IF @@ERROR` 가드 관용구가 없어
  원천적으로 표가 빈다 — 결함이 아니라 적용 밖).

  ④ 코드 앵커가 잡히는 단계 파일 수: **199/326**(사전 추정 ~197과 근접, `StepSqlStatementReader.Read`가
  이 워크트리에서 326개 전수를 다시 읽어 `CodeAnchor != null`인 문장을 가진 파일 수로 셌다).

  ⑤ 검사 A·B·C·D·E 발화량(326개 전수, `ValidateBatchStep` 실전 호출):

  | 검사 | (A) 명세서 그대로 | (B) DDL 매핑 주입 |
  | :--- | ---: | ---: |
  | A | 20 | 20 |
  | B | 1 | 269 |
  | C | 0 | 38 |
  | D | 18 | 18 |
  | E | 59 | 59 |

  **회귀(before/after, 같은 세대 16 코퍼스) — 0.** 과제 지시대로 세대 15 시절 옛 수치(A=20,
  D=52, E=59)와 직접 비교하지 않았다. 대신 `git archive 253d9ba -- src/ReSet.Core`로 main
  최신 tip(우리 작업이 하나도 없는 상태)의 소스만 별도 스크래치 디렉터리에 뽑아 별도
  콘솔 하네스로 빌드해 같은 326개 코퍼스를 돌렸다 — **before(main 253d9ba): A=20, D=18,
  E=59.** after(WAVE_BASE bc495a6, (A)·(B) 두 조건 동일): A=20, D=18, E=59. **완전히 일치 —
  회귀 0.** (참고로 D=18은 세대 15 스윕의 D=52와 다르지만, main tip도 같은 세대 16 코퍼스에서
  D=18을 내므로 이 회차의 효과가 아니라 세대 16 재생성 자체가 만든 차이다 — 옛 수치와
  비교하지 말라는 과제 지시가 실측으로 확인됐다.)

  **S07 갱신 13 — (A)·(B) 둘 다에서 계속 잡힘(회귀 0).** 메시지 원문(양쪽 동일):
  > `S07 섹션의 UPDATE 13(갱신 13) 문장에 명세서가 확정한 최상위 WHERE 술어 컬럼 YMD,
  > PGNAME이(가) 없습니다. 명세서 DML 범위 표 UPDATE 13 행의 값은 \`PLTID, ID, YMD,
  > PGNAME\`입니다 — 이 컬럼이 빠지면 갱신 대상 행 집합이 원본과 달라집니다.`

  **S11 갱신 9 — (B)조건에서 새로 잡힘(이 회차의 목표 달성).** (A)조건에서는 여전히 0건
  (표가 명세서에 없으므로 당연하다). (B)조건 메시지 원문:
  > `S11 섹션의 UPDATE 9(갱신 9) 문장에 명세서가 확정한 조인 키 YMD, UseState이(가)
  > 없습니다. 명세서 DML 범위 표 UPDATE 9 행의 값은 \`PLTID, YMD, UseState, DiscountFlag,
  > DiscountAmt, TxAmt, Amt, ClientID, PGName, MallID\`입니다 — 이 컬럼이 빠지면 갱신 대상
  > 행 집합이 원본과 달라집니다.`

  **표본 오탐 판정 — 검사 B(269건 > 30, 표본) · 검사 C(38건 > 30, 표본).**

  검사 B 269건을 (Kind, Ordinal, 누락 컬럼) 조합으로 묶으면 **34개 고유 조합**이다 — 코퍼스
  22개 Job 중 다수가 같은 레거시 SP를 같은 코드로 반복 호출하는 공용 전처리 단계(S04~S06
  등)를 쓰기 때문에 269건 대부분이 같은 결함의 잡 단위 반복이다. 34개 조합을 전부 직접
  분류했다(>30건 표본 최소 10건 요구를 충족하고 남는다):

  - **거짓 양성 확정 15개 조합(199/269건, 74%) — 구조적 원인을 소스에서 직접 확인했다.**
    `StepSqlStatementReader.cs`의 `DmlCollector.Visit(InsertStatement node)`가
    `Add("INSERT", node, node.InsertSpecification?.Target, null, null)`로 **INSERT 문장에는
    `where`·`from`을 항상 `null`로 넘긴다** — INSERT가 `SELECT ... WHERE ...` 원천을 가져도
    그 WHERE는 절대 읽히지 않는다. 그 결과 모든 INSERT 문장의 `PredicateColumns`·`JoinColumns`가
    구조적으로 항상 빈 목록이라, 명세서가 요구하는 컬럼이 하나라도 있으면 검사 B가 무조건
    "없습니다"를 낸다. 실물로 확인: `output/Jobs/POQSettleBatch1/agent/steps/S04.md:39-52`의
    `INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate ... SELECT ... FROM
    SETTLE_POQ_DB.dbo.TPGCMRate WHERE USESTATE = 0;`는 명세서(`UP_Util_PG_Client_CMRate_Ins/
    docs/Spec.md:233`)가 요구하는 `USESTATE`를 **실제로 담고 있는데도** 검사가 "없다"고
    보고한다. 15개 조합(`UP_Util_PG_Client_CMRate_Ins` INSERT 1~5·`UP_UTIL_SETTLE_INS` INSERT
    1·`UP_UTIL_SETTLE_CANCEL_INS` INSERT 1·나머지 INSERT 1~4 계열)이 전부 이 원인이다.
    **CTE 사각지대와는 다른, 더 넓은 구조적 결함이다** — INSERT는 조인 여부·CTE 유무와
    무관하게 항상 이 함정에 걸린다. 되돌릴 지점: `StepSqlStatementReader.cs`의
    `DmlCollector.Visit(InsertStatement)` — `InsertSpecification.InsertSource`가
    `SelectInsertSource`면 그 안의 `QuerySpecification.WhereClause`·`FromClause`를 넘기도록
    고치면 닫힌다(이번 태스크의 쓰기 허용 범위 밖 — 다음 회차 판단거리로 남긴다, 코드는
    고치지 않았다).
  - **S07 갱신 13(1개 조합, 4건) · S11 갱신 9(1개 조합, 1건) — 진짜, 기존/신규 결함 그대로.**
    위 특별 확인 참고.
  - **나머지 UPDATE·DELETE 계열 17개 조합(65건) — 부분 확인, 대부분 미확인으로 남긴다.**
    `ColumnCollector`(같은 파일)는 `ScalarSubquery`·`QueryDerivedTable` 안쪽을 의도적으로
    건너뛴다(주석: "스칼라 하위질의 안쪽으로 내려가지 않는다 - 최상위 술어 컬럼만 센다") —
    이것은 버그가 아니라 설계 의도다(명세서 DML 범위 표 자신도 "최상위" 술어만 이 칸에
    담고 "파생 테이블" 스코프는 별도 집합 술어 표 칸에 담는 같은 구분을 쓴다). 다만 `PLTID
    IN (SELECT PLTID FROM ...)`처럼 IN의 좌변이 최상위에 있으면 여전히 잡혀야 하는데,
    표본 하나(`POQSettleProc1/S04`의 코드 `-2`)를 열어 보니 **같은 코드 `-2`가 두 개 이상의
    물리 UPDATE 문장(KFTC·INIBANK 등 PG종류별로 쪼갠 문장)에 반복돼 있어**, 명세서의 단일
    "UPDATE 2"가 여러 물리 문장으로 분해된 것인지 검사가 여러 조각을 하나로 합쳐 보는
    것이 실제로 맞는 대응인지 이번 스윕만으로 판별하지 못했다. **시간·도구 호출 예산
    제약으로 17개 조합 중 1개만 부분적으로 열어 봤고 나머지 16개(예: `UPDATE 10 MALLID`·
    `UPDATE 18 PLTID`·`UPDATE 7 PLTID` 등)는 명세서·단계 파일을 직접 대조하지 못했다 —
    정직하게 미확인으로 남긴다.**

  검사 C 38건은 (Kind, Ordinal, 초과 컬럼) 조합으로 묶으면 **12개 고유 조합**이고 **전부
  UPDATE 계열이며 INSERT는 0건**이다(위 INSERT null 배선 때문에 INSERT는 애초에
  PredicateColumns가 비어 "초과 컬럼"이 생길 수 없다 — C가 INSERT에서 항상 조용한 것은
  이 결함의 부작용이지 검사 C 자체의 결함은 아니다). 가장 흔한 조합은 `UPDATE 2`에 `UseState`
  초과(11건)·`UPDATE 4`에 `UseState` 초과(9건)·`UPDATE 12`에 `PGName` 초과(6건)다. **12개
  조합 모두 명세서·단계 파일을 직접 대조하지 못했다 — 시간 예산 제약으로 미확인으로
  남긴다.** 다음 회차가 이 12개 좌표를 출발점으로 쓸 수 있다.

  **predicate 쪽 CTE 사각지대(과제가 특별히 지목한 물음)** — `ColumnCollector`가
  `ScalarSubquery`·`QueryDerivedTable`을 건너뛰는 것은 **설계 의도**임을 소스 주석으로
  확인했다(위 참고). 이것이 오탐으로 이어지는지는 위 "나머지 UPDATE 계열 17개 조합"의
  미확인 범위와 겹친다 — **판별하지 못했다.** 다음 회차가 이 물음을 마저 닫아야 한다.

  **하네스**: 워크트리 안 스크래치 콘솔 프로젝트 2개, 둘 다 저장소 미커밋·종료 후 삭제.
  (1) `scratch-sweep-task8/`(`ReSet.Core.csproj` 참조) — `raw/metadata.json`(31개)을
  `SpDefinition`으로 역직렬화해 원본 DDL·`ProcedureParameters`를 얻고
  `DmlScopeExtractor.ExtractErrorCodes`로 ①~③을 재고, `Spec.md`(31개)를
  `SpecStatementFactsExtractor.Extract`로 읽어 (A)조건 재료를, 그 위에 DDL 매핑을
  `with` 주입해 (B)조건 재료를 만든 뒤 `BatchStepPlanParser.TryParse`로 22개 Job의
  326개 단계를 열어 `MechanicalValidator.ValidateBatchStep`을 실전 그대로 호출해 ④·⑤를
  쟀다. (2) `/private/tmp/.../scratchpad/main-tip-sweep/`(`git archive 253d9ba --
  src/ReSet.Core`로 뽑은 소스를 참조) — before 기준선 A·D·E만 같은 방식으로 쟀다.
  근거: 2026-08-25 코퍼스 스윕(이 문서가 유일한 기록) — Task 8.

  **Task 7(커버리지 배선) 폐기 사유 — 다음 사람이 같은 시도를 반복하지 않도록.**
  「오류 코드 표는 **관계 표**라 라인 커버리지를 늘리지 않는다 — `ErrorCodeFact`에 `Line`이
  없고, 가드의 `SET @po_intRetVal = -N` 라인은 `SetAssignmentExtractor`가 이미 담아
  `CoverageMapComposer.cs:154`가 커버리지에 싣고 있다. 이 표를 `ExtractorFactLines`에
  더하는 배선은 어떤 상태도 바꾸지 못한다(2026-08-25 리플렉션 프로브 실측).」
  **골든 테스트 관점 보강** — 최종 리뷰어 확인: 설계서 배선표는 이 배선을 하라고
  적었지만 실제로는 하지 않은 것이 옳았다. `ExtractorFactLines`(위 141행)는
  `IReadOnlyList<int>` 하나로 접혀 `CoverageMapComposer.Compose`가 잎 문장의
  State를 가르는 유일한 입력이고, `ErrorCodeFact`엔 애초에 `Line`이 없다 —
  그대로 `.Select(f => f.Line)`을 써 넣으면 컴파일이 안 되고, StatementOrdinal을
  Line으로 잘못 재해석해 억지로 끼워 넣으면 엉뚱한 잎에 사실이 붙어
  `CoverageMapGoldenTests`의 요구 1(🟥 총계 0)·요구 3(줄 37·167·190·206의
  앵커 개수 정확히 일치)을 깨뜨렸을 것이다. 배선표의 오류가 코드로 옮겨지기
  전에 골든 테스트가 걸러 낼 자리였다는 뜻이지, 실제로 짜서 돌려 본 결과는
  아니다.

- **캐시 17 인상 전 선결 조건 — I2·I3·재매핑 위험을 한 자리에 모은다(2026-08-25,
  최종 whole-branch 리뷰).** 셋 다 "병합 후, 캐시 17 재생성 전"이라는 같은
  창을 가리킨다 — 흩어져 있으면 인상하는 사람이 셋 중 하나만 보고 진행할
  위험이 있어 여기 한 절로 묶는다.

  **(1) 지금 이 축은 무동작이다 — 병합해도 S11 갱신 9류는 아직 안 닫힌다 (I2).**
  `CacheManager.cs:174`의 `CurrentCacheFormatVersion`은 **16**이고,
  `output/.sp_cache_index.json`의 31개 항목은 **전부 `FormatVersion: 16`**이다
  (직접 확인: `entries` 31개, `FormatVersion` 집합이 `{16}` 하나뿐). 그래서
  「오류 코드」 표가 아직 어느 `Spec.md`에도 없고 → `ReadErrorCodeToOrdinal`이
  항상 빈 사전을 돌려주고 → `ResolveOrdinal`의 코드 앵커 경로는 **도달
  불가**다. Task 8이 잰 코퍼스 2건(0.6%, (A)조건) → 199건(60%, (B)조건 —
  코드 앵커가 실제로 켜졌을 때 잡히는 단계 파일 수) 확대는 **캐시 17 +
  전건 재생성 뒤에야** 일어난다. 그동안 실제로 켜지는 유일한 변화는
  `IsCandidateForAnchoredStatementCheck`(`MechanicalValidator.cs:6162-6163`)가
  INSERT 문장을 후보에서 빼는 것뿐이다 — 즉 **검사 B·C의 관할이 순수하게
  줄어든다**(영향은 사실상 0이지만 부호는 음수다). **다음 사람이 코드만
  보고 "이 축은 이미 작동 중"이라 믿지 않게 하는 것이 이 항목의 목적이다.**

  **(2) 코드 사전이 SP로 스코프되지 않는다 (I3).** `MergeErrorCodeMaps`
  (`MechanicalValidator.cs:6172` 부근)는 같은 코드 문자열이 서로 다른 SP에서
  **서로 다른 값**으로 충돌할 때만 그 코드를 빼고, 한 SP에만 있는 코드는
  그대로 병합 사전에 남는다. 레거시 SP가 둘 이상인 단계에서 SP A에만 있는
  코드(예: `-13`)는 남고, 그 코드를 단 문장이 실제로는 SP B에서 왔어도
  SP A의 (Kind, Ordinal)로 환산될 수 있다. 하위 가드(`candidates.Count != 1`
  판정 + TargetTable 대조, `CheckAnchoredStatementFacts`·
  `CheckAnchoredStatementExtras` 공통)가 일부만 막는다 — 두 SP가 같은 물리
  테이블을 갱신하면 TargetTable 대조를 그대로 통과한다. **재매핑 위험과
  뿌리가 같다: 사전의 키가 코드 하나뿐이고 SP로 한정되지 않는다.**
  **미측정** — 코퍼스에 다중 레거시 SP 단계가 몇 건인지는 이번 라운드
  범위 밖이다. 인상 전에 세야 할 수치다.

  **(3) 오류 코드 재매핑 — AiService 규약 9의 전제가 실제로 깨진다.** 실측
  (`output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/raw/metadata.json`
  `DdlText` 290·319·344행): 원본 `UP_UTIL_SETTLE_COMM_UPD`는 inivacct
  블록(`PGNAME IN ('inivacct')`)에 **`-9`**, easybank 블록(`PGNAME IN
  ('easybank')`)에 `-10`, KFTC/INIBANK 블록(`PGNAME IN ('KFTC','INIBANK')`)에
  `-11`을 쓰는데, 이행 코드(`output/Jobs/POQSettleProc19/agent/steps/
  S11.md:319,362,395` 부근 — 주석 "8. inivacct 취소수수료"·"9. easybank
  취소 및 부분취소 수수료"·"10. KFTC, INIBANK 환불서비스 수수료")는 같은
  세 블록에 각각 `-10`·`-11`·`-12`를 단다. **`-9`가 소실되고 이후 전체가
  1씩 밀렸다** — 한 문장의 오기재가 아니라 SP 꼬리 전체의 체계적 이동이다.
  `AiService.cs:2117`(`[Error Codes]` 항목, "You MUST strictly reuse the
  EXACT original error codes... DO NOT remap or invent new error codes")가
  재매핑을 금지하지만 **프롬프트 수준 강제**라 지켜지지 않았다. **§3
  불일치 침묵이 이를 못 막는다** — 그 문장 주변에 U-앵커가 없어서다(코퍼스
  326개 단계 파일 중 U-앵커를 실제로 쓰는 파일은 직접 세어 **2개뿐**,
  `POQSettleBatch1/S07.md`·`POQSettleProc10/S08.md`). 나머지 324개는 코드
  앵커가 **유일한 신원 축**이라 대조할 상대가 없다. 결과는 거짓양성이
  아니라 **거짓 귀속**: 문장 X를 행 Y와 대조해 엉뚱한 행의 술어 결측을
  요구하고, 그 요구가 `SuggestedPromptFix → floorFeedback`을 타고 재생성
  프롬프트에 실려 재시도를 소진한다.

  **(4) 방어 후보 — 코드 집합 대조.** 단계의 코드 라벨 **집합**이 그 SP
  표의 코드 **집합**과 어긋나면 그 단계에서 코드 축을 **통째로 끈다.**
  관측된 사례가 `-9` 소실("표에는 있는데 단계에는 없다")이라 이 검사에
  걸린다 — 밀림을 직접 보는 대신 **밀림의 원인(라벨 소실)**을 본다.
  문장 단위가 아니라 집합 단위라 값싸다. 기존 「귀속할 수 없으면
  침묵한다」 규약과 같은 결이다. **(2)의 SP 미스코프도 상당 부분 함께
  좁아진다** — 집합이 어긋나는 SP는 애초에 코드 축이 꺼지므로
  `MergeErrorCodeMaps`의 병합 결과가 잘못 쓰일 기회 자체가 준다.
  *(코디네이터와 최종 리뷰어가 이 방어를 독립적으로 같은 결론으로
  제안했다 — 다음 사람에게 신뢰도 신호가 된다.)*

  **(5) 인상 전 재측정 항목.** 한 번에 닫으려면 함께 재라: 다중 레거시
  SP 단계 수((2)의 노출량) · 남은 발화 103건(위 Task 11 표본 오탐률 85%
  참고)의 오탐률 · 코드 집합이 어긋나는 SP 수.

  근거: 위 Task 8·11 실측 + 이번 라운드 재확인(`CacheManager.cs`,
  `output/.sp_cache_index.json`, `MechanicalValidator.cs`, `AiService.cs`,
  `output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/raw/metadata.json`,
  `output/Jobs/POQSettleProc19/agent/steps/S11.md` 직접 대조) — 최종
  whole-branch 리뷰 I2·I3·재매핑 위험 통합.

  **(5-1) 측정 도구가 이제 저장소에 있다(2026-08-26, Task 8).** 회차마다
  스크래치 하네스를 새로 짓던 것은 끝났다 — `dotnet run --project
  src/ReSet.Cli -- --sweep`가 코퍼스 전체를 스윕하고, 결과가
  [`docs/audit-reports/sweeps/2026-08-26-step-sweep.md`](audit-reports/sweeps/2026-08-26-step-sweep.md)로
  저장소에 커밋된다. 아래 수치는 전부 이 보고서에서 그대로 옮긴 것이다.

  **(5-2) (2)의 선결 지표를 실측했다.** 다중 레거시 SP 단계 수 **2** ·
  「SP 표에는 있는데 단계에 없는 코드가 있는 단계」 **64** · 「단계에는
  있는데 SP 표에 없는 코드가 있는 단계」 **68**. 이 두 코드 집합 지표에는
  분모 경고가 붙는다 — 펜스 파싱 실패로 코드 집합 대조에서 **46개 단계를
  제외**했다. 즉 64/68은 326개 전체가 아니라 그 46개를 뺀 나머지를 모집단
  삼은 수치다. 이 제외 건수 없이 64/68만 인용하면 다음 사람이 비율을
  잘못 읽는다. **분모는 정확히 326−46=280**이다 — 코드로 확인: "무재료"
  가드(코드 집합을 어느 쪽도 못 만드는 단계를 대조에서 빼는 방어)는 현재
  로직에서 계수에 영향이 없는 no-op이라, 파싱 실패로 제외된 46건 외에
  추가로 숨어 빠지는 단계는 없다. 이 280이 「코드 집합 대조가 실제로 본
  단계 수」다. 비율로 적으면 64/280≈**23%**·68/280≈**24%** — 326을
  분모로 쓰면(64/326≈20%·68/326≈21%) 실제보다 낮게 잡혀 과소평가한다.

  **(5-3) (1)의 (A)→(B) 확대량이 Task 11 정밀 재측정과 일치했다.** 검사
  B는 오늘 조건(캐시 17 무동작, 코드 앵커 도달 불가) **1** → 캐시 17
  모사 조건(오류 코드 표 완전 전사 가정) **70**, 검사 C는 **0** → **38**.
  이 두 값은 Task 11이 별도로 정밀 재측정한 `B=70 · C=38`과 자릿수까지
  일치한다 — **앞 라운드의 "103건"과 직접 비교하면 안 된다.** 103은 구식
  269 기반의 미확인 잔여(65+38)이고, 오늘 값의 올바른 비교축은 이 70·38
  이다.

  **(5-3-1) 오늘 (A)조건 값이 Task 19 기준선(A=10·D=52·E=59)과 벌어지는
  것은 새 문제가 아니라 이미 닫힌 사안의 재확인이다 — 원인은 둘로
  갈린다.**
  - **D 52→18**은 세대 15→16 코퍼스 재생성 때문이다(`output/Procedures/
    */docs/Spec.md` 14개가 전부 `2026-08-25` mtime, 위 Task 8 항목의
    분류 ①「오류 코드」 표 있는 SP 12개 + ③ TRY-CATCH 현대화 SP 2개와
    1:1 일치).
  - **A 10→20**은 파서 결함 수정(2026-08-24, Task 20 — 문장 단위 분리
    파싱) 하나가 원인이다. 위 「파서 결함 수정 후 코퍼스 재스윕」 항목이
    이미 완결 설명했다(새로 드러난 10건이 기존 10건의 strict superset,
    회귀 0). **Task 22는 원인이 아니다** — Task 22 항목 자신이 "검사
    A·D·E는 이번 라운드가 건드리지 않은 로직이라 이 스윕 대상에 넣지
    않았다"고 못박고 있다(위 690-704행 부근). 라운드 1 브리프가 Task 22를
    A 변화의 원인으로 지목했던 것은 코디네이터 지시 오류였고, 이번
    라운드 재리뷰가 기존 기록과의 모순으로 잡아 뺐다.
  - **B 0→1**도 Task 22 앵커 판독 수정으로 검사 B가 전수 1건 발화하게
    된 결과이고, 그 1건이 진짜 결함이다(S07 갱신 13 — 위 「오류 코드
    앵커 코퍼스 스윕 게이트 실측(2026-08-25, Task 8)」 항목이 (A)·(B)
    양쪽에서 회귀 0으로 계속 잡히는 것을 이미 확인했다).
  - **E는 59로 변화가 없다.**

  이 A=20·D=18·E=59 조합은 위 「오류 코드 앵커 코퍼스 스윕 게이트
  실측(2026-08-25, Task 8)」 항목이 `git archive 253d9ba`(main tip)로
  뽑은 독립 하네스와 WAVE_BASE 양쪽에서 before/after **완전히 일치**로
  이미 확정해 뒀다("before(main 253d9ba): A=20, D=18, E=59. after
  (WAVE_BASE bc495a6): A=20, D=18, E=59. 완전히 일치 — 회귀 0"). 오늘의
  값은 그 확정된 수치를 같은 세대 16 코퍼스에서 다시 재현한 것일 뿐,
  Task 19 옛 기준선과 벌어지는 것은 세대 전환 자체가 만든 차이지 이번
  회차의 결함이 아니다.

  **(5-4) 미분류 977은 분류기 고장이 아니다 — 다만 내부 분포가 안 보인다.**
  리뷰어가 1954개 원시 메시지를 전수 귀속시켜 미귀속 0을 확인했다. 주
  출처는 `CheckBatchControlVocabulary` 36% · `CheckCatchDiscardsReturnCode`
  33% · 헤딩 검사 8.5% · `CheckMissingConditionColumns` 7% ·
  `PlanDefects`(TargetTables 빈 값) 5.8%로, A~E 밖 검사들의 정상 발화다.
  **한계**: 오늘 보고서(`2026-08-26-step-sweep.md`)는 미분류 977의 내부
  분포를 싣지 않는다 — 그래서 A~E 문구가 어딘가 붕괴해 미분류로 새어
  들어가는 신호가 있어도 이 보고서만으로는 실무적으로 식별 불가능하다.
  다음 회차 개선 항목으로 남긴다. **재현 방법**: 현재 도구(`SweepCommand`/
  `StepSweepService`)는 미분류를 집계 하나로만 낸다 — 이 회차의 위 분포를
  다시 보려면 그 미분류 메시지 자체를 검사 이름별로 덤프하는 임시 훅을
  `SweepCommand`(또는 `StepSweepService`)에 넣어 다시 돌려야 한다. 이
  불편함 자체가 다음 회차 개선 항목의 근거다 — 보고서가 미분류 내부
  분포를 표로 싣게 되면 이 임시 계측이 더 이상 필요 없어진다.

  **(5-5) 아직 안 한 것.** 남은 발화 103건(오늘 기준으로는 B=1+C=0=1건 —
  캐시 17이 아직 무동작이라 (A)조건에서는 사실상 0에 가깝다. 캐시 17
  인상 후 (B)조건 기준으로는 B=70+C=38=108건)의 개별 판정은 이번 라운드
  범위 밖이다. 보고서의 「검사 B·C 발화 목록」 표 마지막 「판정」 칸이 그
  작업이 채울 자리다 — 지금은 전부 비어 있다.

  근거: `dotnet run --project src/ReSet.Cli -- --sweep` 실행 결과
  (`docs/audit-reports/sweeps/2026-08-26-step-sweep.md`, 커밋
  `facdb52` 기준) + 위 Task 8·11 실측 + T7 리뷰가 이미 닫은 원인 규명
  (세대 15→16 코퍼스 재생성, `output/Procedures/*/docs/Spec.md` 14개
  전부 2026-08-25 mtime).

- **병합 전 코퍼스 스윕 게이트 실측(2026-08-24, Task 19) — Task 16 C1·C2·Task 17 C3·I1·Task 18 I2를 모두 적용한 뒤 재측정** —
  `output/Jobs/*/agent/steps/*.md`를 스크래치 하네스로 스윕했다. 하네스는
  `VerificationPipelineOrchestrator.GenerateStepSectionWithFloorRetryAsync`의
  `_validator.ValidateBatchStep(...)` 호출을 그대로 본떠 `stepInterfaces`·
  `runRowOwnedTables`·`statementFactsByProcedure`·`allSteps`를 전부 넘겼다(단,
  `stepInterfaces`·`runRowOwnedTables`는 DB 메타데이터가 필요해 로컬에서 못
  만들므로 `null` — 이 두 값이 관여하는 검사(`CheckStepInterface` 등)는 이번
  스윕의 측정 대상 5개(A~E)에 들지 않는다). 저장소에는 커밋하지 않았다
  (`/private/tmp/.../scratchpad/sweep-task19/`).

  **하네스 집계**: Job 22개 · `PlanStructure.md` 파싱 실패 2개
  (`POQSettleProc4` — 73단계를 선언하는데 `BatchStepPlanParser.MaxSteps`(40)를
  넘어 `TryParse`가 `null`을 반환. `POQSettleProc7` — `"Steps": []`로 애초에
  빈 배열) · 단계 파일 누락 51개(파싱된 20개 Job이 선언한 단계 중 `agent/steps/`에
  실물이 없는 것) · 실측 쌍 326개(18개 Job).

  **(1) 검사별 발화량(전체 · Job별)**. 수정 전 최종 리뷰 측정(A=94단계/177오류,
  B=0, C=0, D=11, E=127)과 견주면:
  - **A: 177오류 → 10오류(9개 (Job,Step) 좌표)** — `POQSettleBatch1(2) ·
    POQSettleProc10(4, 2좌표) · POQSettleProc15(1) · POQSettleProc3(1) ·
    POQSettleProc8(2)`.
  - **B: 0 → 0**(그대로 — Task 12가 이미 폴백을 침묵시켜 이 코퍼스에서 앵커
    기반 검사가 사실상 비활성이라는 사실은 여전하다).
  - **C: 0 → 0**(그대로 — B가 비활성인 한 C도 `anchored.Count == 0` 조기
    반환에 걸려 발동하지 않는다. 아래 (2)·(3)이 이번 라운드가 닫은 것은
    C가 아니라 검사 A의 하위 결함 셋임을 다시 확인한다).
  - **D: 11 → 52**(변화 없음 — 이번 라운드는 D를 건드리지 않았다. Task 9
    코퍼스 재측정값 52와 정확히 일치해 D 로직이 이번 라운드 내내 그대로임을
    재확인했다). Job별: `Batch1=9, Proc1=1, Proc11=1, Proc12=1, Proc13=14,
    Proc14=10, Proc16=14, Proc8=1, Proc9=1`.
  - **E: 127 → 59**(약 54% 감소 — Task 17 I1이 합성 `"0"` 성공 코드를 뺀
    효과). Job별: `Batch1=3, Prco20=6, Proc1=4, Proc10=1, Proc11=2, Proc12=1,
    Proc13=1, Proc14=6, Proc15=3, Proc16=3, Proc17=8, Proc18=9, Proc19=6,
    Proc8=1, Proc9=5`.

  **(2) 고친 세 건이 실제로 닫혔는지**:
  - **C1(대조 불가능한 행을 요구로 들지 않는다)** — 위 A의 10건 전부를 직접
    읽어 확인했다. `TargetTable` 길이 1(`"—"`·별칭 `"A"`)이거나 `Kind ==
    SELECT`인 행에 대한 요구는 **0건**이다. 10건 전부 `UPDATE`·`INSERT`이고
    대상은 `TSettleMst`·`TSettleMiss` 실물 테이블명이다.
  - **C2(파싱 실패 펜스가 있으면 개수 대조를 통째로 접는다)** —
    `output/Jobs/POQSettleBatch1/agent/steps/S12.md`를
    `StepSqlStatementReader.Read(out unparsedFenceCount)`로 직접 돌려
    `unparsedFenceCount=1`(파싱 실패 펜스 실재)·`문장 0개`를 확인했다. 그럼에도
    위 A의 10건 목록에 `POQSettleBatch1/S12`는 **없다** — 거짓 "0개" 보고가
    이 좌표에서 **0건**이다. 코퍼스 전체로도 여전히 326개 중 65개 단계
    파일이 전체 펜스 파싱 실패로 문장 0개를 내지만(파서 결함 자체는
    미해결 — 범위 밖), 그중 어느 것도 검사 A의 거짓 "0개" 보고로 이어지지
    않았다(A의 10건 중 파싱 실패 펜스 좌표 0건).
  - **C3(`BareObjectName` 키로 스키마 접두사 없는 `LegacyProcedures`를 찾는다)** —
    `POQSettleProc1`(D=1·E=4)과 `POQSettleProc3`(A=1) 둘 다 이번 스윕에서
    검사가 **발동했다**(수정 전에는 이 두 Job이 통째로 0건이었다는 것이 C3의
    동기였다). `POQSettleProc2`는 이번 스윕에서 0건인데, 그 Job의 `LegacyProcedures`
    중 값이 있는 3개 항목(`UP_Util_Settle_Summary` 등)도 접두사 없는 이름이라
    같은 함정에 해당할 수 있다 — 다만 `FindSpecPath`의 `bareNameIndex` 폴백으로
    `output/Procedures/dbo.UP_Util_Settle_Summary/docs/Spec.md`가 정상적으로
    찾아지는 것은 직접 확인했으므로, 이 0건이 조회 실패가 아니라 실제로
    깨끗한 것인지는 **미확인**(S13~S15 본문을 명세서와 문장 단위로 대조하지
    않았다). `POQSettleProc4`·`POQSettleProc5`는 `agent/steps` 자체가 없어
    (전자는 위 파싱 실패, 후자는 `raw/`만 있고 `agent/`가 아예 없음) C3와
    무관하게 측정 불가 — **미확인**.

  **(3) I2 가드(`allSteps` 배선)가 실제로 작동하는지**:
  - **`POQSettleProc4` 자체는 실측 불가(미확인)** — 위 (1)이 밝힌 대로 이
    Job은 `BatchStepPlanParser.MaxSteps`(40) 상한에 걸려 73단계 선언이
    `TryParse`에서 `null`이 되고, 그 결과 분할 생성 경로 자체에 진입하지
    못한다(주석: "파싱하지 못하면 호출부가 현행 단일 호출 경로로 폴백한다").
    실측대로 `output/Jobs/POQSettleProc4/agent/`에는 `steps/` 디렉터리가
    없다 — `ValidateBatchStep`이 이 Job의 어떤 단계에도 호출되지 않으므로
    "개수 대조에서 침묵하는지"를 코퍼스로 잴 좌표가 없다.
  - **코퍼스 전체(agent/steps가 있는 18개 Job, 326개 단계) 안에서 같은
    레거시 SP가 2개 이상의 서로 다른 단계의 `LegacyProcedures`에 걸쳐
    나타나는 사례가 0건**이다(하네스로 전수 대조 — `POQSettleProc4`·`7`을
    빼면 이 코퍼스에는 애초에 "분할된 레거시 SP"가 없다). **I2 가드가 실제로
    발동할 좌표가 이 코퍼스에는 존재하지 않는다.**
  - **합성 검증(코퍼스 실측이 아님, 별도 표시)** — `POQSettleProc4`의
    `raw/PlanStructure.md` 원문 JSON을 `MaxSteps` 상한과 무관하게 직접
    파싱해 73단계를 복원하고, `S10`·`S27`(`EXCEPTION_PROC`, 확정 18행)·
    `S28`·`S42`(`COMM_UPD`, 확정 15행) 4개 단계에 대해 더미 `UPDATE` 1개짜리
    합성 본문으로 `ValidateBatchStep`을 `allSteps` 있음/없음 두 조건에서
    돌렸다. 결과: **4개 단계 전부 `allSteps` 없음 → A 1건(불가능한 개수
    요구) / `allSteps` 있음 → A 0건(침묵)**. I2 가드의 코드 메커니즘 자체는
    설계대로 동작하나, 이 검증은 실물 생성 산출물이 아니라 합성 본문 위에서
    한 것임을 분명히 한다.

  **(4) 진짜 결손이 여전히 잡히는지** — 위 A 10건에 그대로 있다.
  `POQSettleBatch1/S07`: "UPDATE를 8개만 담고 있습니다. 명세서 DML 범위
  표는 18개를 확정합니다"(8/18, 정확히 재현). `POQSettleBatch1/S08`:
  "UPDATE를 4개만... 15개를 확정"(4/15, 정확히 재현).

  **(5) 거짓 양성 판정** — B·C는 발화 0건이라 표본 없음. A(10건, ≤30)는
  전건, D(52건)·E(59건, 둘 다 >30)는 각 10건 표본(D는 매 5번째 행, E는
  15건을 직접 열람).
  - **검사 A — 10건 전건 확인, (Job,Step) 좌표 기준 4/9 진짜 결손·5/9
    거짓 양성(6건)**. 진짜 결손 4곳(`Batch1/S07` 8/18, `Batch1/S08` 4/15,
    `POQSettleProc15/S07` 4/18, `POQSettleProc3/S04` 17/18)은 전부 `dbo.TSettleMst
    AS <별칭>` 직접 테이블 `UPDATE`로 확인했다(Shadow/Stage 스왑이 아님).
    거짓 양성 5곳:
    1. `POQSettleProc10/S07`(0/18) — `anchor-debug`로 직접 확인: 18개
       `UPDATE` 전부가 `POQSettleS07Build`라는 Shadow build 테이블을
       대상으로 한다. `BareObjectName`이 스펙의 `TSettleMst`와 다르다.
    2. `POQSettleProc10/S11`(INSERT 0/1·UPDATE 0/2, 2건) — `batch.
       POQSettleS11LedgerStage`/`SourceSnapshot` Stage 테이블 갱신(직접
       확인, S11.md:451·481).
    3. `POQSettleProc10/S16`(0/1) — `DELETE FROM dbo.TSettleMiss`(295행)
       뒤 `INSERT INTO dbo.TSettleMiss`(305행) 재구축. **이전 회차 리뷰가
       이미 문서화한 좌표·원인과 동일**(위 "축 B 단계 검사 코퍼스 스윕
       실측" 항목의 (5)-2).
    4. `POQSettleProc8/S08`(0/15) — `UPDATE SETTLE_POQ_DB.stage.
       TSettleMst_S08`(S08.md:196·278). **이전 회차 리뷰가 이미 문서화한
       좌표·원인과 동일**(같은 항목의 (5)-3).
    5. `POQSettleProc8/S07`(1/18) — **이전 회차에 없던 새 하위유형.**
       18개 예외 규칙을 `@RuleNo` 루프(`CASE 1..18`)로 한 `UPDATE T ...
       FROM dbo.TSettleMst AS T` 문 하나에 통합 실행한다(S07.md:132-158).
       Shadow/Stage 스왑이 아니라 "규칙별 앵커 문장 18개" 대신 "런타임
       루프로 도는 문장 1개"로 설계한 것이라, 파서는 물리적으로 1개
       `UPDATE`만 본다.

    **판정 — 오탐 원인과 되돌릴 지점**: A의 오탐은 전부 "명세서 DML
    범위 표가 (Kind, TargetTable) 단위로 `n`개 문장을 확정하는데, 실제
    구현이 그 문장들을 물리적으로 다른 이름의 중간 테이블에 쓰거나
    (Shadow/Stage 스왑, DELETE+INSERT 재구축) 하나의 파라미터 루프 문으로
    합친다(규칙 루프 통합)"는 한 갈래에서 나온다. `CheckStatementCountAgainstSpec`이
    `BareObjectName` 정확 일치로만 `(Kind, TargetTable)`을 대조하는 것이
    되돌릴 지점이다 — 이번 라운드(C1·C2·I2)는 이 갈래를 건드리지 않았고
    (쓰기 범위 밖), 다른 오탐 갈래(대조 불가 행·파싱 실패 펜스·분할 SP)를
    닫으면서 모수가 234 → 10으로 줄어드는 사이 이 갈래의 절대 건수(5~6건)는
    거의 그대로 남아 **비중만 30%(이전 표본) → 60%(이번 표본)로 커졌다**.
    새 결함이 아니라 기존 한계의 비중 변화다 — 다만 `POQSettleProc8/S07`의
    "규칙 루프 통합" 하위유형은 이전 문서에 없었으므로 다음 라운드를 위해
    새로 기록한다.
  - **검사 D — 10건 표본(52건 중 매 5번째 행) 전부 진짜**(선언 없이 쓰인
    변수·명세서 타입 서술 일치). `D 검출 중 펜스 유래 1건`(POQSettleProc11/S08
    `@v_strReqYMD`)도 그대로 재현됐다 — 이전 회차가 이미 "빈도 1.9%, 경계
    사례"로 판정한 것과 동일 좌표·동일 개수, 이번 라운드가 손대지 않아
    변화 없음.
  - **검사 E — 15건 표본 직접 열람 + 코드 불변식으로 오탐이 구조적으로
    불가능함을 확인**. `CheckStepIdInitialValue`는 `declaredCodeSet.
    Contains(initial)`를 통과해야만 메시지를 내고, 그 메시지가 인쇄하는
    "이미 있는 값" 집합이 바로 그 `declaredCodeSet`(=`step.ErrorCodes`)이므로
    판정 근거와 인쇄 근거가 항상 같다. `POQSettleBatch1/S12`를 직접 열어
    `DECLARE @v_currentStepId INT = 0;`·CATCH의 `SET @po_intRetVal =
    @v_currentStepId;`가 메시지와 일치함을 확인했다.

  **(6) 검사 E의 변화** — Task 9 코퍼스 재측정 기준값 129건(원 리뷰 127건과
  근접)에서 **59건으로 약 54% 감소**했다(I1이 합성 `"0"` 성공 코드를 뺀
  효과). 위 코드 불변식(판정 근거 = 인쇄 근거)에 따라 **남은 59건 전부가
  그 단계의 목차 `ErrorCodes`에 실제로 있는 값**이다 — 15건 표본이 이를
  재확인했다.

  **병합 판단**: 이번 스윕 결과로 **병합해도 좋다고 본다.** C1·C2·C3·I1은
  실측으로 닫혔음이 확인됐고(위 (2)·(6)), D·B·C는 이번 라운드가 손대지
  않았으며 실측값이 이전 회차와 정확히 일치해 회귀가 없다. A의 남은
  거짓 양성(5/9 좌표)은 이번 라운드가 만든 새 결함이 아니라 이미
  문서화된 한계(Shadow/Stage 스왑·DELETE+INSERT 재구축)의 재현이고 비중만
  커진 것이며, 새 하위유형(규칙 루프 통합) 하나를 이번에 추가로 기록했다.
  다만 **I2가 실제로 해소하려던 동기 사례(`POQSettleProc4`)는 이 코퍼스로
  실측할 수 없다** — `BatchStepPlanParser.MaxSteps`(40)라는 별개의 상한에
  막혀 분할 생성 경로 자체에 도달하지 못하기 때문이다(이번 태스크가 만든
  결함이 아니라 기존에 있던 별도 제약이 드러난 것). 이 사실은 은폐하지
  않고 다음 라운드로 넘긴다 — `MaxSteps` 상한을 올리거나 73단계를 더
  작은 단위로 재설계해야 `POQSettleProc4`가 분할 생성 경로에 들어가고,
  그래야 I2 가드가 실물 산출물 위에서 검증될 수 있다.

  하네스: `/private/tmp/claude-501/-Users-payletter-git-root-ReSet/
  c5a30bfa-e9ae-4359-af7c-b2e0b422cf4b/scratchpad/sweep-task19/Program.cs`
  (스크래치, 저장소 미커밋 — 워크트리 밖에 둬 `git status`에 전혀 잡히지
  않는다). 근거: 2026-08-24 코퍼스 스윕(이 문서가 유일한 기록) — 태스크 19.

- **파서 결함 수정 후 코퍼스 재스윕(2026-08-24, Task 20) — 검사 A 10 → 20건,
  회귀 0** — 위 「검사 B·C의 앵커 방식」 항목 (5)가 닫은 파서 결함(문장 단위
  분리 파싱)을 코퍼스 326개 전체에 적용해 재측정한 결과다. 리뷰어가 base
  커밋을 별도로 빌드해 독립 재현했다.

  **(1) 검사 A 발화 — 10 → 20건, 새 10건은 기존 10건의 strict superset.**
  이전 10건(`Batch1/S07·S08` · `Proc10`(4건, 2좌표) · `Proc15/S07` ·
  `Proc3/S04` · `Proc8/S07·S08`)이 새 20건 목록에 그대로 남아 있고(회귀 0),
  새로 드러난 10건은 파싱 실패에 가려 있던 좌표들이다: `Proc10/S06`·
  `Proc10/S08`·`Proc10/S10`(3건)·`Proc13/S07`·`Proc8/S09`·`Proc9/S06`·
  `Proc9/S07`·`Proc9/S09`. 태스크 21이 이 워크트리에서 같은 하네스를 다시
  돌려 20건 발화(`errA=20`)를 그대로 재현했다.

  **(2) 표본 4건의 성격**(단계 파일을 직접 열어 확인):
  - **`POQSettleProc10/S06`(INSERT 0/1) — 거짓 양성.** 이미 기록된
    Shadow/Stage 표 패턴. 스펙은 `TSettleMst` INSERT 1건을 확정하지만
    실제 INSERT(`S06.md:282`)는 `poqbatch.POQSettleLedgerStage`를
    대상으로 하는 private staging 테이블이다.
  - **`POQSettleProc10/S08`(UPDATE 0/15) — 거짓 양성.** 같은 패턴.
    `S06.md`가 아니라 `S08.md:176`의 `UPDATE B ... FROM
    [batch].[POQSettleLedgerStageImage] AS B`가 그 예다.
  - **`POQSettleProc13/S07`(UPDATE 4/18) — 진짜 결함.** `S07.md`의 18개
    규칙 중 실행 가능한 `UPDATE` 문은 4개(-101·-102·-27·-29 규칙)뿐이고
    검사 A가 정확히 그 4개를 셌다. 나머지는 주석 플레이스홀더뿐이다(예:
    `S07.md:145` `/* UF_GET_CLIENTSECTIONRATE와 UF_GET_ROUND4VAT을
    사용한 원본 UPDATE */`). **수치 정정**: 이 항목을 전달한 설명은
    "12개가 플레이스홀더"라 적었으나, `S07.md` 전체(101~198행)에서
    플레이스홀더 주석과 실 UPDATE를 하나씩 대조하면 **플레이스홀더가
    14개, 실제 UPDATE가 4개**(합 18)다 — 이 문서는 직접 센 수치로 고쳐
    적는다.
  - **`POQSettleProc9/S06`(UPDATE 6/18) — 진짜 결함, 새 하위유형.** 아래
    항목에서 별도로 기록한다.

  **판정 — 검사 A 234 → 20 축소는 회귀가 아니라 두 겹의 진전이다.** 코퍼스
  하한이 65/326 → 16/326으로 좁혀지면서(위 (1)) 가려져 있던 진짜 결손
  10건이 새로 드러났고, 동시에 파서 취약성 갈래(234건 중 92건, 39%)가
  통째로 사라졌다 — 두 효과가 겹쳐 234 → 20이 된 것이지, 어느 쪽도 서로를
  상쇄하지 않는다.

  근거: 2026-08-24 코퍼스 재스윕(Task 20 스크래치 기록, 저장소 미커밋) —
  태스크 21이 이 워크트리에서 `StepSqlStatementReader.Read` 326개 전수
  재실행(`zeroStatementFiles=16`·`totalLostStatements=134` 일치)과 위
  4건의 파일·줄 직접 열람으로 재확인.

- **검사 A의 새 하위유형 — "하위 프로시저 위임"(리뷰어 Minor, 2026-08-24
  확인)** — `POQSettleProc9/S06`(UPDATE 6/18, 위 항목 표본 4). `S06.md`를
  직접 열어 확인: 18개 규칙 중 **정확히 12개**가 `EXEC batch.S06_ApplyXxx
  @pi_strYMD;` 호출로 위임된다(`S06.md:221-269`, 예: `S06_ApplyClientMinimum`·
  `S06_ApplyKftcPgSection`·`S06_ApplyCardPromotion` 등). 그 12개 하위
  프로시저의 `CREATE` 정의를 `output/` 트리 전체에서 찾았으나 **어디에도
  없다** — `S06_ApplyClientMinimum` 같은 이름을 전체 검색하면 이 호출부
  자신과 `docs/BatchMigrationPlan.md`의 같은 호출부 재인용만 나오고, 별도
  본문 정의는 0건이다.

  **판정: 진짜 결함이다(확인됨, "미확인" 아님).** 단계 파일이 본문을 보여
  주지 않고, 다른 산출물에도 본문이 없으므로("검사의 한계일 뿐 다른 곳에
  있다"는 가설이 반증됨) 이행자는 `S06.md`만으로 12개 규칙의 실제 로직을
  복원할 방법이 없다. 검사 A는 `EXEC` 뒤에 숨은 DML을 볼 수 없어 이 결함을
  스스로 잡지 못한다 — 검사가 낸 시정 지시("UPDATE를 6개만 담고 있다")를
  받아도 모델이 위임된 12개 규칙의 SQL을 새로 쓸 재료가 산출물 어디에도
  없다는 것이 근본 문제다.

### 반복되는 함정 — 접두사 겹침

이 저장소는 짧은 이름이 긴 이름의 접두사인 자리가 구조적으로 많다(`batch.*` 제어 표,
`@v_*` 관용 변수, `T*` 테이블). 경계 없이 문자열을 매칭하면 검사가 조용히 무력화되거나
(짧은 이름이 긴 이름 안에서 걸려 항상 "발견"되는 바람에 실제로는 아무것도 가려내지
못함) 항상 실패한다(긴 이름이 짧은 이름을 부분 문자열로 포함해 `DoesNotContain`류가
절대 통과하지 못함). 이번 회차에 같은 모양이 세 번 관측됐다 — **새 문자열 매칭을
추가할 때마다 아래 셋을 참고해 경계를 확인할 것.**

1. **`@v_int` vs `@v_intCLTotal`** — 검사 D의 변수 이름 매칭.
   `MechanicalValidator.CheckSpecLocalVariablesDeclared`가 `(?<![\w@])…\b`(시작은 부정
   후방탐색, 끝은 `\b`)로 막았다 — 그 자리에 이미 "접두사 겹침을 막는다"는 주석이 있다.
2. **`` batch.BatchRun `` vs `` batch.BatchRunLock ``** — 계약 표 테스트가 행을 고를 때
   백틱 없이 `Contains("BatchRun")`으로 고르면 두 표의 행이 섞여 테스트가 통과해도
   아무것도 검사하지 않는다.
   `BatchControlContractTests.RenderPromptTable_DoesNotClaimIdentityForATableThatHasNone`·
   `RenderPromptTable_StillSaysHowRunIdIsIssuedForTheRunTable`이
   `` `batch.BatchRunLock` ``·`` `batch.BatchRun` `` 처럼 백틱을 포함해 매칭해 피했다.
3. **`ControlTotal` vs `BatchControlTotal`** — `BatchControlContract`의 별칭 누출 가드.
   정본 이름 `batch.BatchControlTotal`이 별칭 `ControlTotal`을 문자 그대로 포함해 단순
   `DoesNotContain(alias, output)`이 정본 이름이 정상적으로 실리기만 해도 항상 실패한다.
   `BatchControlContractTests.RenderedOutput_DoesNotLeakAliasesAsIfTheyWereCanonical`이
   정본 전체 이름과 맨이름(스키마 접두사를 뗀 이름)을 먼저 걷어낸 나머지에서 별칭을
   찾는 방식으로 피했다.

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
- **기본 설정에서 `dependency-manifest.json`이 아예 안 생기고 `metadata.json`이 스위치에 걸린다** —
  두 파일은 "뒤 계층이 원천으로 읽으므로 끌 수단을 두지 않는다"가 설계 의도인데
  (`MetadataExporter.ExportCodeObjectArtifactsAsync`의 "지시서 번들이 참조 테이블 스키마를
  만들 때 쓰는 원천이다" 주석), 기본 설정이 그 의도를 지키지 못한다.
  `AnalysisSettings:AnalyzeReferencedCodeObjects`가 `false`(기본값)면 저장 책임이
  `Program.SaveRawArtifactsAsync`로 넘어가는데(`SpAnalysisOutcome.FromSingleObjectPipeline`이
  `Persistence`를 `NotAttempted`로 두고, 호출부가 그 값일 때만 부른다) 그 경로에는
  `ExportCodeObjectArtifactsAsync` 호출이 없다 — 유일한 호출부가
  `DependencyAnalysisOrchestrator`(참조분석 ON)다. 그래서 **매니페스트는 어느 설정으로도
  못 켜고**, `metadata.json`은 `OutputSettings:SaveRawJson` 하나에 통째로 걸린다.
  같은 이유로 `Objects/` 정본(`object_definition.sql`)도 OFF에서는 만들어지지 않는다.
  증상은 조용하다 — `--coverage-map`을 Job에 걸면 `CoverageMapCommand.ClosureOf`가 읽을
  매니페스트가 없어 폐포가 소비 명세서 목록으로 줄어드는데, 실패가 아니라 더 작은 수를
  찍고 정상 종료한다(`소비 명세서 N개 → 폐포 N개`). `SaveRawJson`까지 꺼져 있으면
  `LoadObject`가 전건을 건너뛴다.

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
