# 삭제 대장 — AGENTS.md 재구조화

각 줄의 삭제 근거. `중복`은 동등 이상의 서술이 이미 그곳에 있다는 뜻이다.
`근거없음`은 삭제하지 않고 이동한다.

**표기 규칙.** 이 문서에서 `L<n>`은 언제나 AGENTS.md의 줄 번호(아래 표의 `LINE`
열)를 뜻한다. `docs/architecture.md` 안의 위치는 `architecture.md:<n>`처럼 파일
이름을 붙여 적는다. 두 좌표계에 같은 접두사를 쓰지 않는다 — 인용을 직접 찾아
확인할 수 있다는 것이 이 대장의 유일한 값어치이므로, 숫자가 어느 파일을 가리키는지
모호하면 그 값어치가 사라진다.

**이 표를 읽기 전에.** `scripts/doc-audit.sh`는 클래스 이름이 아니라 마크다운 링크의
경로로 근거를 찾는다(2026-08-14 개정, 아래 "개정 이력" 참고). 그래도 이 도구가
증명하지 못하는 것이 네 가지 있다.

- **근소한 차이는 판정이 아니라 확인 요청이다.** ARCH_MD 또는 SUMMARY가 대상 줄의
  바이트 수 대비 대략 10% 이내로 기준에 못 미쳐 `근거없음`이 나온 경우, 실제로는
  거의 같은 서술이 이미 있을 수 있다. 예: `CancellationPolicyTests`(447 vs 448바이트,
  1바이트 차이), `ValidatorAiServiceTests`(179 vs 181), `DataComparisonServiceTests`
  (176 vs 183, L141의 클래스), `JavaProcessRunner`(L125, 204 vs 206),
  `DataComparisonService`(L126, 233 vs 237). 마지막 둘은 형제 이름
  (`JavaProcessRunnerTests`, `DataComparisonServiceTests`)과의 충돌로 예전 점수가
  일부 부풀려져 있었던 것도 사실이라 이번 경로 기반 수정이 옳았지만, 수정된
  점수도 이 근접 구간 안에 들어와 있다. 이런 항목은 대장 반영 전 사람이 직접
  대조한다.
- **묶음 레이블은 개별 파일 경로로 찾을 수 없다.** `docs/architecture.md`가 여러
  클래스를 하나의 링크 레이블로 묶어 서술하면(예: `[Clients (Claude, OpenAi,
  Ollama) Tests]`, `[CLI Clients (ClaudeCli, CodexCli, AntigravityCli) Tests]`)
  개별 파일 경로 대조는 그 서술을 찾지 못해 `근거없음`을 낸다. `ClaudeClientTests`와
  `ClaudeCliClientTests`가 그 경우다 — 근거가 없는 게 아니라 스크립트가 못 찾을
  뿐이며, architecture.md를 열어 보면 그 내용이 실제로 있다.
- **이 스크립트는 `중복`을 증명하지 `근거없음`을 증명하지 않는다.** `근거없음`이
  뜻하는 것은 "이 스크립트가 경로/이름으로 찾지 못했다"이지 "근거가 세상 어디에도
  없다"가 아니다. 최종 판단(이동 vs 그대로 둠)은 각 Task의 사람 판정이 한다.
- **링크 없는 서술은 경로 매칭이 보지 못한다.** `docs/architecture.md`가 어떤
  클래스를 백틱으로 이름만 언급하며 본문 문단에서 길게 설명하면서도, 그 문단이
  파일 경로로 링크하지는 않는 경우가 있다. 경로 대조는 정확히 그 경로가 등장하는
  줄만 세므로, 이런 문단은 존재해도 카운트에 들어오지 않는다. 이 실패 방향은
  "묶음 레이블" 항목과 다르다 — 묶음 레이블은 하나의 링크가 여러 클래스를 덮는
  경우고, 이것은 그 클래스를 가리키는 링크 자체가 (그 문단에는) 아예 없는
  경우다. 아래 "개정 이력"의 일곱 줄이 이 경우다 — `근거없음`이 나왔지만
  architecture.md에 실질적 서술이 있으므로, Task 2는 이동 전에 그 문단을 먼저
  읽는다.

## 개정 이력

**2026-08-14 (Fix Round 1):** 최초 스크립트는 링크 텍스트의 클래스 *이름*으로
architecture.md와 `<summary>`를 찾았다. `src/` 안에는 같은 basename을 가진 파일이
둘 있다(`Program.cs`, `ConsoleUserInteraction.cs`가 각각 `ReSet.Cli`와
`ReSet.Validator.Cli`에 있다). 이름 기반 대조는 이 두 짝의 근거를 합산해, 서로 다른
AGENTS.md 두 줄에 같은(부풀려진) 분량을 배정했다 — 위험한 방향의 오류였다. 실측에서
합산치가 두 줄 각각의 기준을 넘어 우연히 오탐은 없었지만, 이름이 우연히 짧거나 흔한
다른 클래스라면 엉뚱한 근거를 빌려 거짓 `중복` 판정(부당 삭제의 근거)을 낼 수 있었다.
같은 이유로 `find src -name "$sym.cs"`는 `tests/`를 뒤지지 않아, `tests/` 아래 클래스의
SUMMARY가 항상 0으로 나왔다(`StepErrorCodeRegressionTests`가 실제로는 302바이트의
`///` 요약을 갖고 있었는데도).

지금 스크립트는 링크 텍스트가 아니라 **링크의 경로**로 찾는다. 경로는 파일을
유일하게 식별하므로 이름이 겹쳐도 섞이지 않는다. 이 개정으로 9개 항목의 판정이
`중복:architecture.md`에서 `근거없음`으로 바뀌었다(`git show
4556437:docs/superpowers/plans/2026-08-13-deletion-ledger.md`의 표와 현재 표를
줄 번호 기준으로 대조한 결과: L37, L88, L89, L110, L111, L121, L125, L126,
L144 — 정확히 9줄). 이 9줄이 전부이며, 아래 일곱 개(사각지대)와 두 개(근접
구간)로 완전히 나뉜다. **세 번째 범주는 없다.**

`Program`(L96, L129)과 `ConsoleUserInteraction`(L97, L130)은 이 9줄에
들어가지 않는다 — 네 줄 모두 개정 전후로 판정이 `중복`에서 움직이지 않았다
(L96 `Program`의 ARCH_MD는 1730 → 216, L129는 1730 → 297로 숫자만 바뀌었을 뿐
`중복:architecture.md`인 채로 남았다). 이름 충돌이 이 두 클래스의 근거를
합산해 부풀렸던 것은 사실이고 그것이 대조 방식을 이름에서 경로로 바꾼
이유지만, 그 결과는 **배정을 바로잡았을 뿐 근거없음을 드러내지 않았다** — 이
두 클래스를 근거없음 사례로 인용하지 않는다.

아래 일곱 개는 사각지대다 — `SqlObjectTypeClassifier`(L37), `CliWorkspace`
(L88), `CliEffort`(L89), `MockDataDto`(L110), `GapReport`(L111),
`ValidatorAiService`(L121), `TypeClassificationPolicyTests`(L144)는
architecture.md가 그 클래스를 백틱 이름으로 실질적으로 서술하고 있다 —
`SqlObjectTypeClassifier`와 `TypeClassificationPolicyTests`는
`architecture.md:388`, `CliWorkspace`와 `CliEffort`는 `architecture.md:589`,
`MockDataDto`는 `architecture.md:637`, `GapReport`와 `ValidatorAiService`는
`architecture.md:633`에 있다. 다만 그 문단이 파일을 링크하지 않아(또는
카탈로그 링크와 별도로 더 긴 서술이 붙어 있어) 경로 매칭이 그 서술을 보지
못한다. 이 일곱 줄의 `근거없음`은 위 네 번째 항목("링크 없는 서술은 경로 매칭이
보지 못한다")이 말하는 스크립트의 사각지대이지, 드러난 실제 공백이 아니다.
셋 — `CliWorkspace`, `CliEffort`, `ValidatorAiService` — 은 그중에서도
혼합형이다: architecture.md 카탈로그 표에 실제 링크가 있어 스크립트가 일부
분량을 이미 인정했다 — `CliWorkspace`는 표 L88이고 그 카탈로그 링크는
`architecture.md:68`에 있어 ARCH_MD 504바이트, `CliEffort`는 표 L89이고 링크는
`architecture.md:69`에 있어 893바이트, `ValidatorAiService`는 표 L121이고 링크는
`architecture.md:110`에 있어 302바이트다. 그 인정된 분량이 AGENTS.md 줄의
바이트 수에 못 미치고 카탈로그 항목과는 별도의 더 긴 서술 문단이 또 있다(CliWorkspace·
CliEffort는 `architecture.md:589`, ValidatorAiService는 `architecture.md:633`).
나머지 넷(`SqlObjectTypeClassifier`, `MockDataDto`, `GapReport`,
`TypeClassificationPolicyTests`)은 링크가 아예 없다(해당 `.cs` 경로로
`grep -c`하면 0이 나온다).

나머지 둘 — `JavaProcessRunner`(L125, 204 vs 206바이트), `DataComparisonService`
(L126, 233 vs 237바이트) — 는 형제 이름(`JavaProcessRunnerTests`,
`DataComparisonServiceTests`)과의 충돌로 이전 점수가 일부 부풀려져 있었던 것은
맞지만, 지금 점수도 첫 번째 항목("근소한 차이는 판정이 아니라 확인 요청이다")이
말하는 ~10% 근접 구간 안에 있다. 판정
자체는 옳되(경로 매칭이 형제 이름과의 혼선을 없앤 결과다), 근소한 차이이므로
사람이 대조하고 넘어간다.

Task 2는 이 목록을 근거로 쓰되, 위 일곱 줄과 두 근접 줄은 표 아래 "미해결
검토 대상" 각주와 이 절을 함께 읽는다.

## Phase 1 — §2 클래스 카탈로그 (AGENTS.md L16–L145)

이 표는 커밋 `0d602dfb815ccd4bef4bcd4e0cc618f7018cb4ba`(카탈로그가 아직 살아 있던
마지막 상태 — 바로 다음 커밋 `f5ad6e2`가 이 구간을 라우팅 표로 교체했다) 시점의
`AGENTS.md`에 `./scripts/doc-audit.sh 20 145`를 돌려 찍은 스냅샷이다(감사 구간의
헤딩과 도입 문단인 L16–L19는 스크립트 인자 범위 밖이며, 표에 잡히지 않는다). 이
재현성을 검증 없이 주장하지 않았다 — 그 커밋을 별도 워크트리에 체크아웃해 같은
명령을 실제로 다시 돌렸고, 그 출력을 아래 표와 `diff`해 바이트 단위로 동일함을
확인했다(2026-08-14, Fix Round 3). 그 커밋 이후 이 구간은 라우팅 표로 교체되었으므로,
현재 트리에 대고 같은 명령을 다시 돌리면 대부분 빈 결과(또는 완전히 다른 결과)가
나온다 — 이는 회귀가 아니라 삭제가 실제로 반영됐다는 뜻이며, 이 표의 값어치는
삭제 판정이 무엇을 근거로 이뤄졌는지를 재현 가능하게 남기는 데 있지, 현재 상태를
서술하는 데 있지 않다.

```
LINE   BYTES   SYMBOL                             ARCH_MD   SUMMARY   VERDICT
20     59      -                                  -         -         산문(수동판정)
21     60      -                                  -         -         산문(수동판정)
22     181     SpDefinition                       0         551       중복:SpDefinition.cs <summary>
23     200     -                                  -         -         산문(수동판정)
24     247     CodeObjectKey                      303       0         중복:architecture.md
25     199     CodeObjectAnalysisModels           303       1360      중복:architecture.md
26     342     VerificationOutcome                346       806       중복:architecture.md
27     404     SpAnalysisOutcome                  406       676       중복:architecture.md
28     172     DependencyInfo                     0         0         근거없음(이동필요)
29     220     ColumnInfo                         0         0         근거없음(이동필요)
30     187     TableIndexInfo                     0         0         근거없음(이동필요)
31     218     AiResult                           0         0         근거없음(이동필요)
32     146     DbSnapshot                         243       0         중복:architecture.md
33     70      -                                  -         -         산문(수동판정)
34     298     DbMetadataService                  392       258       중복:architecture.md
35     281     SqlStaticParser                    536       4532      중복:architecture.md
36     622     StaticAnalysisNormalizer           0         2038      중복:StaticAnalysisNormalizer.cs <summary>
37     531     SqlObjectTypeClassifier            0         411       근거없음(이동필요)
38     1802    AiService                          465       3552      중복:AiService.cs <summary>
39     683     IAiClient                          298       724       중복:IAiClient.cs <summary>
40     379     PromptCacheBreakpointPolicy        2633      1603      중복:architecture.md
41     420     PromptComposition                  341       410       근거없음(이동필요)
42     1548    BatchStepPlan                      965       3169      중복:BatchStepPlan.cs <summary>
43     480     SpecReturnCodeExtractor            178       1654      중복:SpecReturnCodeExtractor.cs <summary>
44     579     SpecTargetTableExtractor           381       1920      중복:SpecTargetTableExtractor.cs <summary>
45     1484    PlanStructureEnricher              382       3564      중복:PlanStructureEnricher.cs <summary>
46     709     StepDefect                         0         1549      중복:StepDefect.cs <summary>
47     451     BatchPlanAssembler                 480       1159      중복:architecture.md
48     1377    MechanicalValidator                749       17117     중복:MechanicalValidator.cs <summary>
49     422     SchemaPromptColumnSelector         477       3559      중복:architecture.md
50     418     SpecExpectations                   409       2502      중복:SpecExpectations.cs <summary>
51     4162    VerificationPipelineOrchestrator   445       12852     중복:VerificationPipelineOrchestrator.cs <summary>
52     296     DependencyAnalysisOrchestrator     292       567       중복:DependencyAnalysisOrchestrator.cs <summary>
53     559     VerificationDocumentFormatter      751       1707      중복:architecture.md
54     1474    VerificationBanner                 1583      7217      중복:architecture.md
55     422     ThinkingLogPlaceholder             0         883       중복:ThinkingLogPlaceholder.cs <summary>
56     717     ThinkingLogDocument                0         854       중복:ThinkingLogDocument.cs <summary>
57     1104    BestAttempt                        0         1949      중복:BestAttempt.cs <summary>
58     1270    RetryRescue                        0         1434      중복:RetryRescue.cs <summary>
59     1330    StructureRedraftPolicy             0         1812      중복:StructureRedraftPolicy.cs <summary>
60     493     CriticFeedbackLog                  0         1947      중복:CriticFeedbackLog.cs <summary>
61     682     RegenerationScope                  0         1847      중복:RegenerationScope.cs <summary>
62     223     OutputPathResolver                 305       87        중복:architecture.md
63     231     SpecificationLinker                305       0         중복:architecture.md
64     616     MetadataExporter                   588       702       중복:MetadataExporter.cs <summary>
65     803     DataAccessPolicy                   2473      3627      중복:architecture.md
66     787     PlanBoundaryResolver               813       7885      중복:architecture.md
67     256     MarkdownSectionLocator             297       1791      중복:architecture.md
68     383     InstructionEntryPointComposer      1018      4356      중복:architecture.md
69     340     TaskFileComposer                   498       4911      중복:architecture.md
70     680     InstructionBundleWriter            422       5270      중복:InstructionBundleWriter.cs <summary>
71     283     AgentProgressStore                 430       2337      중복:architecture.md
72     261     CodegenArtifactNaming              314       2954      중복:architecture.md
73     203     PlanLayout                         322       1340      중복:architecture.md
74     648     VerificationCoverage               745       3697      중복:architecture.md
75     252     OfflineDbMetadataService           261       2403      중복:architecture.md
76     286     SnapshotManager                    304       0         중복:architecture.md
77     251     LocalAiConsolidator                290       0         중복:architecture.md
78     197     CacheManager                       235       0         중복:architecture.md
79     362     -                                  -         -         산문(수동판정)
80     254     ExternalCliCodingEngine            339       228       중복:architecture.md
81     250     ArgumentTemplateResolver           459       2445      중복:architecture.md
82     219     ArtifactChangeDetector             294       333       중복:architecture.md
83     165     CodegenRunResult                   267       825       중복:architecture.md
84     231     -                                  -         -         산문(수동판정)
85     987     ClaudeCliClient                    765       988       중복:ClaudeCliClient.cs <summary>
86     830     -                                  -         -         산문(수동판정)
87     208     CliProcessRunner                   504       908       중복:architecture.md
88     647     CliWorkspace                       504       413       근거없음(이동필요)
89     936     CliEffort                          893       751       근거없음(이동필요)
90     268     CliProviderBatchGuard              508       570       중복:architecture.md
91     157     IMultiProgressScope                0         0         근거없음(이동필요)
92     218     NullProgressScope                  0         0         근거없음(이동필요)
93     316     SettlementPolicyService            202       0         근거없음(이동필요)
95     57      -                                  -         -         산문(수동판정)
96     136     Program                            216       9797      중복:architecture.md
97     474     ConsoleUserInteraction             354       1290      중복:ConsoleUserInteraction.cs <summary>
98     214     ValidationUiProxy                  193       0         근거없음(이동필요)
99     1056    BatchStepCatalog                   1677      1803      중복:architecture.md
100    250     SpecHeaderReader                   255       99        중복:architecture.md
102    93      -                                  -         -         산문(수동판정)
103    141     -                                  -         -         산문(수동판정)
104    361     IValidatorPlugin                   0         154       근거없음(이동필요)
105    765     TransactionEnlistmentCheck         653       1224      중복:TransactionEnlistmentCheck.cs <summary>
106    159     IRuntimeRunner                     0         573       중복:IRuntimeRunner.cs <summary>
107    177     IValidationUserInterface           0         0         근거없음(이동필요)
108    144     L1ValidationResult                 0         0         근거없음(이동필요)
109    159     ValidationResult                   0         0         근거없음(이동필요)
110    178     MockDataDto                        0         0         근거없음(이동필요)
111    317     GapReport                          0         0         근거없음(이동필요)
112    153     RunnerDtos                         0         0         근거없음(이동필요)
113    141     ValidatorConfig                    0         0         근거없음(이동필요)
114    87      -                                  -         -         산문(수동판정)
115    261     CodegenWorkflowOrchestrator        317       10112     중복:architecture.md
116    413     CodegenLoopPolicy                  478       1275      중복:architecture.md
117    222     CodegenWorkflowResult              233       516       중복:architecture.md
118    323     CodegenStage                       348       880       중복:architecture.md
119    258     CodeVerificationOrchestrator       350       271       중복:architecture.md
120    249     FileMappingService                 254       771       중복:architecture.md
121    520     ValidatorAiService                 302       0         근거없음(이동필요)
122    202     SpExecutionService                 237       0         중복:architecture.md
123    213     SandboxSeedingService              234       0         중복:architecture.md
124    231     CSharpReflectionRunner             251       0         중복:architecture.md
125    206     JavaProcessRunner                  204       0         근거없음(이동필요)
126    237     DataComparisonService              233       0         근거없음(이동필요)
128    91      -                                  -         -         산문(수동판정)
129    80      Program                            297       252       중복:architecture.md
130    142     ConsoleUserInteraction             306       0         중복:architecture.md
132    82      -                                  -         -         산문(수동판정)
133    85      -                                  -         -         산문(수동판정)
134    201     SqlStaticParserTests               291       0         중복:architecture.md
135    328     ClaudeClientTests                  0         0         근거없음(이동필요)
136    1232    ClaudeCliClientTests               0         0         근거없음(이동필요)
137    175     JavaProcessRunnerTests             221       0         중복:architecture.md
138    174     SandboxSeedingServiceTests         230       0         중복:architecture.md
139    174     CodeVerificationOrchestratorTests  232       0         중복:architecture.md
140    181     ValidatorAiServiceTests            179       0         근거없음(이동필요)
141    183     DataComparisonServiceTests         176       0         근거없음(이동필요)
142    399     DependencyAnalysisOrchestratorTests 0         0         근거없음(이동필요)
143    448     CancellationPolicyTests            447       0         근거없음(이동필요)
144    976     TypeClassificationPolicyTests      0         611       근거없음(이동필요)
145    624     StepErrorCodeRegressionTests       0         302       근거없음(이동필요)
감사 완료: 122행 처리
```

**미해결 검토 대상 — 표는 스크립트 출력 그대로이므로 여기 별도로 표시한다.**
다음 일곱 줄의 `근거없음`은 architecture.md가 링크 없이(또는 카탈로그 링크와는
별도로) 실질적으로 서술하고 있어, "이동 필요"가 아니라 "먼저 그 문단을 확인"으로
읽어야 한다 — L37 `SqlObjectTypeClassifier`, L88 `CliWorkspace`, L89 `CliEffort`,
L110 `MockDataDto`, L111 `GapReport`, L121 `ValidatorAiService`, L144
`TypeClassificationPolicyTests`(근거는 위 "개정 이력" 참고). 나머지 `근거없음` 중
L125 `JavaProcessRunner`와 L126 `DataComparisonService`는 판정 자체는 맞지만
~10% 근접 구간이라 위 "이 표를 읽기 전에"의 첫 번째 항목이 적용된다.

## Phase 1 결과 — 카탈로그 삭제와 라우팅 표 신설 (Task 2, 2026-08-14)

**삭제 범위.** `AGENTS.md`의 `## 📂 프로젝트 구조 및 주요 파일 바로가기 (Key Code
References)` 헤딩(구 L16)부터 범주 5(단위 테스트 프로젝트)의 마지막 항목(구 L145)까지
전체를 삭제하고, 아래 "🗺 어디를 만지면 무엇을 먼저 읽는가 (Routing)" 표로 교체했다.
바로 뒤의 `---` 구분선(구 L147)은 그대로 남겼다. 표는 계획서 Task 2 Step 3이 준
문구를 그대로 썼다.

**Task 1 Step 3의 예고(8개)와 실제 결과(31개)의 차이.** 계획서는 `근거없음` 판정이
소형 모델 클래스 8개 언저리일 것으로 예고했다. 실제로는 `scripts/doc-audit.sh`가
122행 중 31행을 `근거없음`으로 냈다 — 스크립트가 링크 텍스트의 클래스 *이름*으로
찾다가 경로 기반으로 개정되면서(위 "개정 이력" 참고) 이름이 겹치는 클래스들의 근거
배정이 바로잡혔고, 그 결과 원래 예고에 없던 `tests/` 대상들과 `ReSet.Validator.Core`의
인터페이스·DTO 뭉치가 새로 드러났기 때문이다. 계획서 Task 2 Step 1의 "실제 결과를
따르고, 그 차이를 대장에 적는다"는 지침에 따라 31개 전부를 판정했다.

**추가 재분류 — 작업 중 발견.** 계획서와 상위 지시가 Group A(근거 없음, 이동 필요)로
분류한 17개 중 3개는 실제로 `docs/architecture.md` §2.2 표에 이미 실질적인 행을
갖고 있었다(스크립트의 이름 기반 매칭 잔재로 오분류됐던 것으로 보인다). 표를 여는
대신 각 파일 경로로 직접 `grep`해 확인했다:

| 클래스 | 원 분류 | 확인 결과 | 재분류 |
|---|---|---|---|
| `PromptComposition` (L41) | Group A(근거없음) | `architecture.md:65`에 이미 실질 서술 존재 | Group B로 재분류, AGENTS.md 줄 삭제 |
| `SettlementPolicyService` (L93) | Group A(근거없음) | `architecture.md:101`에 이미 실질 서술 존재 | Group B로 재분류, AGENTS.md 줄 삭제 |
| `ValidationUiProxy` (L98) | Group A(근거없음) | `architecture.md:50`에 이미 실질 서술 존재 | Group B로 재분류, AGENTS.md 줄 삭제 |

세 클래스 모두 §2.2 표에 새 행을 추가하지 않았다 — 이미 있는 행에 중복 행을 만드는
것은 "삭제의 기본값은 이동이다" 원칙에 어긋난다. 이로써 최종 배분은 Group A 14개,
Group B 17개(14 + 재분류 3)로, 31개 전부를 설명한다.

### Group A — architecture.md §2.2로 이동한 14개

근거가 어디에도 없어 `docs/architecture.md` §2.2 표에 새 행으로 편입했다. 계획서
Task 2 Step 1이 준 문구를 그대로 쓴 것(플랜의 8개 중 3개는 위 재분류로 빠졌으므로
실제로는 6개)과, AGENTS.md 원문을 그대로 옮겨 직접 쓴 것(8개)으로 나뉜다.

| 클래스 (원 AGENTS.md 줄) | 원문 출처 | architecture.md 신규 위치 |
|---|---|---|
| `DependencyInfo` (L28) | 계획서 verbatim | ReSet.Core 그룹, `SettlementPolicyService` 행 다음 (§2.2) |
| `ColumnInfo` (L29) | 계획서 verbatim | 〃 |
| `TableIndexInfo` (L30) | 계획서 verbatim | 〃 |
| `AiResult` (L31) | 계획서 verbatim | 〃 |
| `IMultiProgressScope` (L91) | 계획서 verbatim | 〃 |
| `NullProgressScope` (L92) | 계획서 verbatim | 〃 |
| `IValidatorPlugin` (L104) | AGENTS.md 원문에서 직접 작성 | ReSet.Validator.Core 그룹, `DataComparisonService` 행 다음 |
| `IValidationUserInterface` (L107) | AGENTS.md 원문에서 직접 작성 | 〃 |
| `L1ValidationResult` (L108) | AGENTS.md 원문에서 직접 작성 | 〃 |
| `ValidationResult` (L109) | AGENTS.md 원문에서 직접 작성 | 〃 |
| `RunnerDtos` (L112) | AGENTS.md 원문에서 직접 작성 | 〃 |
| `ValidatorConfig` (L113) | AGENTS.md 원문에서 직접 작성 | 〃 |
| `DependencyAnalysisOrchestratorTests` (L142) | AGENTS.md 원문에서 직접 작성(구 L142는 세 클래스를 한 문장에 묶었다 — 아래 "Fix Round 1" 참고) | ReSet.Core.Tests 그룹, `CancellationPolicyTests` 행 다음 |
| `SpecificationLinkerTests` (L142, 같은 묶음 문장) | AGENTS.md 원문에서 직접 작성 | 〃 |
| `OutputPathResolverTests` (L142, 같은 묶음 문장) | AGENTS.md 원문에서 직접 작성 | 〃 |
| `StepErrorCodeRegressionTests` (L145) | AGENTS.md 원문에서 직접 작성 | 〃 |

편입 확인(각 클래스명이 architecture.md에 1회 이상 등장):
`grep -c "<클래스명>" docs/architecture.md` — 17개(재분류 3개 포함, 이미 있던 항목이라
당연히 나옴) 전부 1 이상. `ValidationResult`만 `L1ValidationResult`와의 부분 문자열
겹침으로 2가 나오는데, 두 클래스 모두 각자의 행을 갖고 있으므로 문제 없다.
(`SpecificationLinkerTests`·`OutputPathResolverTests`는 원래 31행 감사 대상이 아니었으므로
이 확인 루프의 대상도 아니었다 — Fix Round 1에서 별도로 추가·확인했다.)

### Group B — 코드 근거가 확인되어 삭제한 17개

각 행을 architecture.md의 인용 위치에서 직접 읽고 실질적 서술임을 확인한 뒤 AGENTS.md
줄을 삭제했다(위 재분류 3개 포함).

| 클래스 (원 AGENTS.md 줄) | 유형 | 확인한 위치 | 판정 |
|---|---|---|---|
| `SqlObjectTypeClassifier` (L37) | 사각지대(링크 없는 서술) | `architecture.md:388` — `SqlObjectTypeClassifier`의 판정 통합 배경과 `TypeClassificationPolicyTests` 연계를 한 문단으로 서술 | 실질적, 삭제 |
| `TypeClassificationPolicyTests` (L144) | 사각지대 | `architecture.md:388` — 위와 같은 문단 | 실질적, 삭제 |
| `CliWorkspace` (L88) | 혼합형(카탈로그 링크 + 사각지대) | `architecture.md:589`(CLI 제공자 절, 카탈로그 링크는 `:68`)가 `CliWorkspace`의 임시 디렉터리 격리를 서술하지만, 삭제된 AGENTS.md 줄의 가장 실행 지침적인 경고 — `--strict-mcp-config`/`--setting-sources ""` 두 축을 **함께** 유지해야 하는 이유 — 는 그 문단에 없다. 실제로는 [ClaudeCliClient.cs:63-69](../src/ReSet.Core/Services/Clients/Cli/ClaudeCliClient.cs)의 코드 주석이 같은 내용을 (실측 토큰 수치까지 포함해) 담고 있다. "그 파일을 여는 사람만 잡는다" 범주이므로 이곳이 맞는 거처다 — Fix Round 1에서 정정 | 실질적, 삭제(단 인용 위치는 Fix Round 1에서 정정) |
| `CliEffort` (L89) | 혼합형 | `architecture.md:589`(같은 절, 카탈로그 링크는 `:69`)가 `CliEffort`의 xhigh→high 클램프는 서술하지만, 삭제된 AGENTS.md 줄이 실제로 강조한 것은 `CliFailureClassifier`의 도메인 특화 오탐 방지 경고("권한"/"한도"/"사용량" 같은 일반 단어로 마커를 잡지 말 것)였다. 이 경고는 [CliFailureClassifier.cs:50-56](../src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs)의 코드 주석에 그대로(오히려 더 상세히) 남아 있다 — Fix Round 1에서 정정 | 실질적, 삭제(단 인용 위치는 Fix Round 1에서 정정) |
| `MockDataDto` (L110) | 사각지대 | `architecture.md:637` — 관계지향 모의 데이터가 `MockDataDto` 형태로 생성·캐싱된다고 명시 | 실질적, 삭제 |
| `GapReport` (L111) | 사각지대 | `architecture.md:633` — L2 Gap 판정 5대 범주가 `GapReport`에 담긴다고 명시 | 실질적, 삭제 |
| `ValidatorAiService` (L121) | 혼합형 | `architecture.md:633` — 같은 문단이 `ValidatorAiService`의 L2 대조 책임을 서술(카탈로그 링크는 `architecture.md:110`) | 실질적, 삭제 |
| `ClaudeClientTests` (L135) | 묶음 레이블 | `architecture.md:117` — `[Clients (Claude, OpenAi, Ollama) Tests]` 행이 세 클라이언트 테스트를 함께 서술 | 실질적, 삭제 |
| `ClaudeCliClientTests` (L136) | 묶음 레이블 | `architecture.md:118` — `[CLI Clients (ClaudeCli, CodexCli, AntigravityCli) Tests]` 행 | 실질적, 삭제 |
| `JavaProcessRunner` (L125) | 근소한 차이(204 vs 206) | `architecture.md:112` — §2.2 표 자체의 정식 행 | 실질적, 삭제 |
| `DataComparisonService` (L126) | 근소한 차이(233 vs 237) | `architecture.md:115` — §2.2 표 자체의 정식 행 | 실질적, 삭제 |
| `ValidatorAiServiceTests` (L140) | 근소한 차이(179 vs 181) | `architecture.md:122` — §2.2 표 자체의 정식 행 | 실질적, 삭제 |
| `DataComparisonServiceTests` (L141) | 근소한 차이(176 vs 183) | `architecture.md:123` — §2.2 표 자체의 정식 행 | 실질적, 삭제 |
| `CancellationPolicyTests` (L143) | 근소한 차이(447 vs 448) | `architecture.md:124` — §2.2 표 자체의 정식 행 | 실질적, 삭제 |
| `PromptComposition` (L41) | **재분류(A→B)** | `architecture.md:65` — §2.2 표 자체의 정식 행 (이름 기반 매칭 잔재로 오분류됐던 것으로 보임) | 실질적, 삭제 |
| `SettlementPolicyService` (L93) | **재분류(A→B)** | `architecture.md:101` — §2.2 표 자체의 정식 행 | 실질적, 삭제 |
| `ValidationUiProxy` (L98) | **재분류(A→B)** | `architecture.md:50` — §2.2 표 자체의 정식 행 | 실질적, 삭제 |

**정정(Fix Round 1).** 이 절은 원래 "17개 전부 실질적 서술을 직접 읽고 확인했다"고
적었으나 그 주장은 틀렸다 — 실제로 직접 읽고 확인한 것은 17개 각 줄이 인용한
"대표 근거 위치"뿐이었고, 원본 AGENTS.md 줄이 **여러 클래스를 한 문장에 묶은
경우**(L142가 `DependencyAnalysisOrchestratorTests`·`SpecificationLinkerTests`·
`OutputPathResolverTests` 셋을 묶었던 것처럼) 첫 번째 클래스만 처리하고 나머지는
검토 없이 함께 삭제되는 구멍이 있었다. 독립 리뷰가 L142에서 이 구멍을 발견했다
(뒤 "Fix Round 1" 절 참고). 리뷰는 나머지 묶음 줄(L85, L89, L104, L121, L135, L136)은
모든 형제 클래스가 architecture.md에 이미 근거를 갖고 있음을 확인했고, 이 구멍이
L142 하나뿐임을 검증했다 — 이 대장은 그 결과를 그대로 따른다.

### 크기

| 파일 | Phase 1 원안 이후 | Fix Round 1 이후 |
|---|---|---|
| `AGENTS.md` | 108,485 B → 55,096 B | 55,096 B (변경 없음) |
| `docs/architecture.md` | 137,251 B → 139,963 B | 139,963 B → 140,651 B |

AGENTS.md는 108,485 → 55,096바이트로 약 53.4KB 줄었다(계획서 예상 50~57KB 범위 안).
계획이 예상한 8개보다 Group A가 훨씬 컸음에도(14개, 최종 §2.2 표 신규 행) 범위
안에 든 이유는 §2 카탈로그의 대다수(122행 중 91행)가 애초에 `중복:architecture.md`
또는 `중복:<클래스>.cs <summary>`로 정직하게 판정됐고, 그 91행이 삭제분의 대부분을
차지했기 때문이다. Fix Round 1은 AGENTS.md를 전혀 건드리지 않았다 — 모든 보정이
`docs/architecture.md`(137,251 → 139,963 → 140,651바이트, 이번 라운드에서 +688)와
이 대장에만 실렸다.

## Fix Round 1 (2026-08-14) — 독립 리뷰가 찾은 정보 손실 보정

독립 리뷰가 Phase 1 결과에서 SPEC COMPLIANCE FAIL / TASK QUALITY FAIL을 냈다. 원인은
전부 "정보 손실"이었고 라우팅 표·삭제 경계·링크 검사·크기는 통과했다. 아래는 그
지적 각각과 실제로 무엇을 고쳤는지다.

**Critical — L142의 묶음 삭제 구멍.** 구 AGENTS.md L142는
`DependencyAnalysisOrchestratorTests.cs, SpecificationLinkerTests.cs,
OutputPathResolverTests.cs: 재귀 SP/UDF 그래프의 중복 제거·실패 격리, 성공 대상 링크
및 객체별 출력 경로를 검증.`이라는 한 문장으로 세 클래스를 묶었다. `scripts/doc-audit.sh`는
줄 하나에서 첫 `[Xxx.cs]` 링크만 `sym`으로 뽑으므로 이 묶음에서는
`DependencyAnalysisOrchestratorTests`만 31행 감사 대상에 잡혔고, 나머지 둘은 애초에
평가조차 되지 않은 채(`중복`도 `근거없음`도 아닌 "감사되지 않음") 원안이 §2 전체를
삭제할 때 함께 사라졌다. 두 파일을 열어 확인한 결과 각각 클래스 요약 XML 주석이
없고, 이 삭제 전까지는 이 문장 하나가 유일한 문서화였다.

원 문장의 세 절은 세 클래스에 1:1로 대응한다 — "중복 제거·실패 격리"는
`DependencyAnalysisOrchestratorTests`(이미 §2.2에 있음), "성공 대상 링크"는
`SpecificationLinkerTests`, "객체별 출력 경로"는 `OutputPathResolverTests`. 각 테스트
파일을 열어 그 절이 실제로 그 클래스의 동작과 일치하는지 확인한 뒤(`SpecificationLinkerTests`는
성공한 참조에 상대 링크를 쓰고 실패·외부 DB는 사유를 쓰는 것을 검증,
`OutputPathResolverTests`는 `ResolveSpecPath`/`ResolveDocsDirectory`/
`ResolveCanonicalDdlPath`/`ResolveManifestPath`로 현재/외부 DB의 출력 경로 계산을
검증), `docs/architecture.md`의 `DependencyAnalysisOrchestratorTests` 행 바로 다음에
두 행을 새로 추가했다(§2.2, ReSet.Core.Tests 그룹):

```
| | [SpecificationLinkerTests](../tests/ReSet.Core.Tests/SpecificationLinkerTests.cs) | 성공한 참조 대상에 상대 `Spec.md` 링크가 걸리고, 실패·외부 DB 등 링크할 수 없는 대상은 링크 대신 사유가 적히는지 검증. |
| | [OutputPathResolverTests](../tests/ReSet.Core.Tests/OutputPathResolverTests.cs) | 현재 DB와 외부 DB를 구분한 객체별 출력 경로(명세서·DDL·의존성 매니페스트) 계산을 검증. |
```

리뷰가 다른 묶음 줄(L85 CLI 클라이언트군, L89 CLI 헬퍼군, L104 검증 추상화군, L121
검증 서비스군, L135/L136 클라이언트 테스트군)도 같은 구멍이 있는지 확인했고, 그
전부가 architecture.md에 형제 클래스마다 이미 근거가 있음을 확인했다고 보고했다 —
L142가 유일한 사례였다. 이 대장은 그 결과를 그대로 받아들였고, 재감사는 하지 않았다.

**Important 1 — `SettlementPolicyService`가 삭제한 링크의 손실.** 재분류 표(위)가
`SettlementPolicyService`(L93)를 Group A에서 Group B로 옮기며 AGENTS.md 줄을
지웠는데, 그 줄은 `ISettlementPolicyService.cs`도 함께 언급·링크하고 있었다.
`architecture.md:101`의 살아남은 행은 구현체만 서술하고 그 인터페이스는 언급하지
않아, 삭제 시점에 그 인터페이스 파일이 대장·AGENTS.md·architecture.md 어디에도
남지 않게 됐다. 이번 라운드에서 그 행을 보강했다:

- 이전: `| | [SettlementPolicyService](../src/ReSet.Core/Services/SettlementPolicyService.cs) | DDL 상수 분석 및 DB 마스터 데이터 프로파일링을 결합한 통합 정산 정책 정의서 도출. |`
- 이후: `| | [SettlementPolicyService](../src/ReSet.Core/Services/SettlementPolicyService.cs) | DDL 상수 분석 및 DB 마스터 데이터 프로파일링을 결합한 통합 정산 정책 정의서 도출. 계약은 [ISettlementPolicyService](../src/ReSet.Core/Services/ISettlementPolicyService.cs)로 분리되어 있다. |`

이것이 재분류(A→B) 판단 자체를 무효로 하지는 않는다 — `PromptComposition`과
`ValidationUiProxy`의 재분류는 리뷰가 검증한 대로 손실이 없었다. 다만 "행이 존재한다"와
"그 행이 원문의 내용을 다 담고 있다"는 별개의 주장이며, 후자를 확인하지 않고
삭제한 것이 이번 사례의 결함이다 — 짧은 행이 살아남을 때는 삭제 전에 보강해야 한다.

**Important 2 — `CliWorkspace`/`CliEffort` 인용 위치 정정.** 조정자 판정: 내용
자체는 옳은 자리에 있다(코드 주석), 대장의 **인용**이 틀렸다. `architecture.md:589`는
`CliWorkspace`·`CliEffort`의 일반적 역할은 서술하지만, 삭제된 AGENTS.md 줄이 실제로
가장 힘주어 말한 두 경고 — (a) `CliWorkspace`의 `--strict-mcp-config`/
`--setting-sources ""` "두 축을 함께 유지하라"는 규칙, (b) `CliFailureClassifier`의
도메인 특화 오탐 방지 마커 규칙 — 는 그 문단에 없다. 둘 다 [ClaudeCliClient.cs:63-69]
(../src/ReSet.Core/Services/Clients/Cli/ClaudeCliClient.cs)와
[CliFailureClassifier.cs:50-56](../src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs)의
코드 주석에 원문과 같거나 더 상세하게 살아 있다. 이 두 경고는 "그 파일을 여는
사람만 잡는다" 범주이므로 코드 주석이 맞는 거처이며, 라우팅 표를 바꿀 필요는 없다
— 위 Group B 표의 인용을 이 절의 설명으로 정정했다.

**Minor — 두 곳 보강.**
- `DataComparisonServiceTests`(`architecture.md:135` 부근)가 서술하던 "예외 핸들링"을
  "`JsonException` 핸들링"으로 구체화했다. 삭제된 AGENTS.md 줄이 명시했던 예외
  타입이며, 실제로 `DataComparisonServiceTests.CompareOutputs_WithNullJson_ShouldHandleGracefully`가
  `DataComparisonService.cs:26`의 `catch (JsonException)` 경로를 예외 처리한다.
- `GapReport.cs`와 `MockDataDto.cs`가 삭제 시점에 잃었던 유일한 클릭 가능 링크를
  복원했다 — 새 §2.2 행을 만들지 않고, 각 클래스를 이미 실질적으로 서술하던 산문
  문단(`architecture.md` §4.6의 Gap 판정 규칙 문단, §4.7의 관계지향 모의 데이터 생성
  문단)의 백틱 이름을 마크다운 링크로 바꿨다. 산문이 이미 더 나은 거처였고, 항해
  가능성만 없었다.

## Phase 2a 결과 — 범주 2 (예외 처리 및 안정성) (Task 3, 2026-08-14)

**구간.** Task 2가 130줄을 지워 행 번호가 이동했으므로 헤딩에서 재계산했다 —
`./scripts/doc-audit.sh`를 돌린 시점 기준 범주 2는 AGENTS.md L49–L67(`### ⚡ 범주 2.`부터
`### 🎨 범주 3.` 직전까지)였다.

**판정 요약.** 계획서가 확정한 5개 판정(취소 규칙 → 테스트, SQL 타입 판정 → 테스트,
모델별 전송 규격 → architecture.md, Soft Fail 정책(DB/Exporter/캐시/재귀) → 원문 유지,
Ollama 온도·추론 제어 → 원문 유지)에 더해, 표에 없던 나머지 불릿(정합성 검증 DB 실행,
AI 클라이언트 널 가드, OpenAI Responses 추론 보존, 오프라인 스냅샷 Fail-Fast, 프롬프트
응답 정화)에도 같은 판정 질문을 직접 적용했다. 그 근거로 연 파일:
`tests/ReSet.Core.Tests/ValidatorTests.cs`(`SpExecutionService_ShouldSoftFail_OnInvalidConnectionString`),
`tests/ReSet.Core.Tests/CancellationPolicyTests.cs`, `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`
(`AnalyzeAsync_CancelledMidGraph_PersistsCompletedObjectsAndReportsPartialCompletion`,
`..._CyclicGraphCancelled_AddsTheUnresolvedReferenceBannerToTheSurvivingDocument`),
`tests/ReSet.Core.Tests/{Claude,OpenAi,Google,Zai}ClientTests.cs`(각 `ChatAsync_With*ErrorResponse_ShouldThrow*`),
`tests/ReSet.Core.Tests/OpenAiClientTests.cs`(`ChatAsync_WithGpt5MixedReasoningSummaries_ShouldPreserveNonEmptyReasoningText`),
`src/ReSet.Cli/Program.cs`(오프라인 스냅샷 Fail-Fast 구현부, 취소 최상위 핸들러
주석), `docs/architecture.md`(§4.1 L404, §4.3, §4.5 L599–600, §4.13).

### 줄여 쓴 항목(삭제, 근거 위치 포함)

| 원문 위치(구 줄) | 사유 | 근거 위치 |
|---|---|---|
| L57 "취소를 실제로 흡수하는 지점은 [Program.cs]의 최상위 핸들러 하나뿐이며, 거기서 사용자에게 취소 사실을 알리고 Serilog를 정리한 뒤 종료합니다." | "그 파일을 여는 사람만" 잡는다 — Program.cs 자신이 이미 이 사실을 인라인 주석으로 말한다 | `src/ReSet.Cli/Program.cs:1663-1665`("이 안쪽의 어떤 catch도 취소를 가로채지 못했을 때의 마지막 정류장이다...") |
| L58 스캐너가 잡는/놓치는 구문 형태의 전량 열거(원시 부분 문자열 판정 종류, 언래핑 목록, 괄호로 감싼 수신자, 널 조건부 체인 등) | 테스트가 잡는다 — `TypeClassificationPolicyTests`가 검사 범위를, `TypeClassificationPolicyScanner.cs`가 알려진 한계를 이미 코드로 문서화한다 | `tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs`, `tests/ReSet.Core.Tests/TypeClassificationPolicyScanner.cs` 상단 주석, `docs/architecture.md:404`(더 두꺼운 배경 서술) |
| L62 모델별 전송 규격의 기계 세부(캐시 breakpoint의 explicit 모드·content 블록 규격, 문자열/블록 배열 혼용 금지, 접미사 빈 호출 처리, Claude 4/5세대 옵션 조율·temperature 생략, 재생성 회차 user 블록 두 번째 중단점) | architecture.md가 보유 | `docs/architecture.md:599`(OpenAiClient 항목, breakpoint 모드·content 블록·접미사 처리를 그대로 서술), `docs/architecture.md:600`(ClaudeClient 항목, "Claude 4/5세대 추론 토큰 대응 및 temperature 생략 처리"와 재생성 회차 user 블록 두 번째 중단점을 그대로 서술), `docs/architecture.md §4.13`(캐시 중단점의 가격 근거) |

### 이동한 항목(삭제하지 않고 옮김)

| 원문 위치(구 줄) | 이동 사유 | 새 위치 |
|---|---|---|
| L58 `DependencyAnalysisOrchestrator.TryParseCodeObjectType`/`MetadataExporter.NormalizeCodeObjectDdlFolder`의 정확 일치 `switch` 테이블 서술(분류기와의 불일치, `"P"`/`"FN"`/`"TF"` 처리 차이, 오늘 오작동하지 않는 이유) | 근거를 architecture.md/코드 어디도 갖고 있지 않았다(감사 도구가 `근거없음`) — 삭제가 아니라 이동 대상 | `docs/architecture.md` §4.3 새 불릿("분류기 밖에 남은 정확 일치 `switch` 테이블") — `TryParseCodeObjectType`(`src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:327`)과 `NormalizeCodeObjectDdlFolder`(`src/ReSet.Core/Services/MetadataExporter.cs:159`)를 직접 읽고 확인한 뒤 옮겼다 |

### 원문 유지(사람의 판단만이 잡는 항목)

계획서가 확정한 대로 다음은 한 글자도 줄이지 않았다:

- DB 메타데이터 수집(`DbMetadataService.cs`) — 원문 유지
- 원천 데이터 파일 덤프(`MetadataExporter.cs`) — 원문 유지
- 캐싱 및 서브 시스템(`CacheManager.cs`) — 원문 유지
- 재귀 코드 객체 분석의 경로 인코딩 절만 — 원문 유지(테스트 없음). 나머지 절은 Fix Round 1에서
  테스트가 있음이 확인되어 축약됐다 — 아래 "Fix Round 1" 절 참고
- 오프라인 스냅샷 파일 검증 Fail-Fast — 원문 유지(테스트 없음, Program.cs Main 진입점이라 단위 테스트 대상이 아니고 설계 의도가 코드에서 자명하지 않음)
- 취소는 소프트 페일 대상이 아님(필터 요구, `when (ex is not OperationCanceledException)`) — 원문 유지, 테스트 인용만 추가
- 취소 이후 부분 완료 보존(완료 산출물 보존, 미분석 참조 표기) — 원문 유지, 테스트 인용만 추가
- Ollama 온도 매핑 중 gemma4/qwen3.6 하드코딩 분기, 반복 패널티 방어 — 원문 유지(테스트 없음).
  effort→temperature 매핑 절은 Fix Round 1에서 테스트가 있음이 확인되어 축약됐다
- Ollama 모델별 추론(Thinking) 제어 및 파싱 규칙 — 원문 유지
- 프롬프트 응답 정화(Conversational filler, 마크다운 코드 블록 금지) — 원문 유지(런타임 LLM 출력을 검사하는 테스트가 없음)
- AI 클라이언트 목록 및 `KeyNotFoundException` 차단 규칙 — 원문 유지(이미 규칙 한 줄이라 축약할 서술이 없었음)

원문 유지 확인(`grep -c`, 전부 1 이상):

```
소프트 스킵 처리해야 합니다                              1
디스크 쓰기 오류 등이 발생하더라도 핵심 산출물은         1
글로벌 해시 캐시 조작 및 레거시 마이그레이션            1
하위 SP/UDF의 메타데이터·분석                            1
사용자 DB 연결 프롬프트로 우회(Fallback)하지 말고        1
0.1/0.4/0.7/0.9로 차등 적용                              1
시스템 프롬프트 선두에 주입                              1
Conversational filler                                    1
```

### 크기

| 파일 | Phase 2a 이전(Task 2 이후, WAVE_BASE `c28183e`) | Phase 2a 원안 이후 | Fix Round 1 이후 |
|---|---|---|---|
| `AGENTS.md` | 55,096 B | 52,492 B (−2,604 B) | 52,730 B (+238 B) |
| `docs/architecture.md` | 140,798 B | 141,877 B (+1,079 B) | 141,877 B (변경 없음) |

링크 검사(둘 다 무출력): `AGENTS.md`의 `./` 링크 32개, `docs/architecture.md`의
`../src/` 링크 104개·`../tests/` 링크 23개 — 전부 존재 확인.

## Fix Round 1 (Task 3, 2026-08-14) — 이미 테스트가 있는 절에 규칙 축소를 덜 적용한 문제

독립 리뷰가 SPEC COMPLIANCE PASS / TASK QUALITY CONCERNS(Important 3건 + Minor 1건)를 냈다.
공통 원인: 판정 질문("무엇이 잡는가?")을 특정 절에 대해서는 적용하지 않고 원문을 그대로
두거나 부정확한 테스트 인용을 달았다. 아래는 지적별로 실제로 무엇을 고쳤는지다.

**Important 1 — 재귀 코드 객체 분석이 대부분 테스트가 있음에도 원문 유지됐다.** Phase 2a
원안은 이 불릿(구 L55–57, 3개 절로 줄바꿈, 1,385B)을 전부 "재귀" Soft Fail 원문 유지
대상으로 묶었다. 리뷰가 `DependencyAnalysisOrchestratorTests.cs`를 열어 절마다 대응 테스트를
지목했고, 각 테스트를 직접 열어 실제로 그 절을 검사하는지 확인했다:

| 절 | 확인한 테스트 | 실제 단언 |
|---|---|---|
| 노드별 `Failed` 격리, 다른 객체 계속 분석 | `AnalyzeAsync_ChildFailureDoesNotFailRoot` | 자식이 `Failed`+사유("AI request failed")를 갖는 동안 루트는 `Succeeded` |
| `SkippedDepth` | `AnalyzeAsync_UsesTraversalDepthToSkipGrandchildBeyondMaximum` | `maxDepth` 초과 손자 노드가 `SkippedDepth`, 메타데이터/파이프라인 요청 모두 안 감 |
| 크로스 DB 비활성 시 `SkippedExternal` | `AnalyzeAsync_UsesDirectMetadataAndSkipsExternalObjectBeforeAdditionalLookup` | 외부 DB 객체가 `SkippedExternal`, 직접 메타데이터만 조회하고 파이프라인 요청 없음 |
| 크로스 DB 활성 중 접근 실패는 `Failed` | `AnalyzeAsync_ExternalMetadataFailureIsSurfacedAsFailedNode` | `allowExternalDatabaseConnections: true`에서 메타데이터 조회 실패 시 `SkippedExternal`이 아니라 `Failed`, 루트 Spec.md에 "분석 불가" |
| 최소 깊이 우선 | `AnalyzeAsync_ShallowDiscoveryWinsOverLaterDepthExceededPath` | 공유 객체가 얕은 경로로 한 번만 분석되고, 양쪽 간선(root→shared, nested→shared) 모두 기록 |
| 실패 객체 링크 금지 | `AnalyzeAsync_SpecWriteFailureMarksChildFailedAndParentDoesNotLinkIt` | 자식 Spec.md 쓰기 실패 시 루트 Spec.md에 자식으로의 마크다운 링크가 없음 |
| Critic 점수·`Thinking.md` 보존 | `AnalyzeAsync_PersistsChildReviewScoreAndThinkingArtifacts` | 자식 Spec.md에 점수 문구, `Thinking.md`에 추론 텍스트 기록 |
| 카탈로그 표기 우선 | `AnalyzeAsync_NormalizesGraphKeysToCatalogObjectNameCasing` | 호출부 표기(`UF_Get_WorkDay2`)가 아니라 카탈로그 표기(`UF_GET_WORKDAY2`)로 노드·간선 이름이 통일 |

경로 인코딩 절만 이 파일과 `OutputPathResolverTests.cs`에 대응 테스트가 없어(`grep`로 직접
확인) 원문 유지로 남겼다. 위 표의 판정을 그대로 받아들이지 않고 각 테스트를 열어 확인한
뒤, 3개 절을 다음으로 재작성했다(`AGENTS.md` L55–57):

```
*   **재귀 코드 객체 분석 — 상태 표기**: ... (AnalyzeAsync_ChildFailureDoesNotFailRoot/
    ..._ExternalMetadataFailureIsSurfacedAsFailedNode/..._ShallowDiscoveryWinsOverLaterDepthExceededPath가 검사)
*   **재귀 코드 객체 분석 — 성공 산출물**: ... (AnalyzeAsync_SpecWriteFailureMarksChildFailedAndParentDoesNotLinkIt/
    ..._PersistsChildReviewScoreAndThinkingArtifacts/..._NormalizesGraphKeysToCatalogObjectNameCasing이 검사)
*   **재귀 코드 객체 분석 — 경로 인코딩**: 객체 키와 출력 경로는 구분자·파일명 문자를
    충돌 없이 인코딩해야 합니다.
```

`SkippedDepth`와 `AnalyzeAsync_UsesDirectMetadataAndSkipsExternalObjectBeforeAdditionalLookup`은
대표 인용에서 빠졌다(리뷰가 지목한 대응 테스트이므로 위 표에는 남긴다) — 규칙 문장 자체가
`SkippedDepth`/`SkippedExternal` 두 상태를 모두 명시하고, 인용은 상태 분기의 대표 사례
(정상 실패·외부 실패·최소 깊이 우선)로 좁혔다. 세 줄 합계가 1,385B → 1,228B로 줄었다(−157B).

**Important 2 — Ollama 온도 매핑에 정확히 맞는 테스트가 있는데도 인용이 없었다.**
`OllamaClientTests.cs`의 `ChatAsync_ShouldDiversifyTemperatureBasedOnEffort`([Theory],
low→0.1/medium→0.4/high→0.7/max→0.9)를 열어 확인했다 — 요청 JSON의 `options.temperature`가
정확히 그 값들과 일치하는지 단언한다. 이 절에 그 인용을 추가했다. 같은 불릿의 나머지
절(모델명에 `gemma4`/`qwen3.6`이 포함되면 매핑을 무시하고 하드코딩된 샘플링 설정을 쓰는
분기)은 `OllamaClient.cs:51-96`에 구현이 있지만 대응 테스트가 없어(`grep`로 확인) 원문
그대로 남겼고, 그 사실을 인용 옆에 명시했다.

**Important 3 — TryGetProperty 인용이 존재하지 않는 테스트 패턴을 주장했다.** 원 인용
"각 클라이언트 테스트의 `ChatAsync_With*ErrorResponse_ShouldThrow*` 계열"은 `Claude`/`OpenAI`/
`Zai`ClientTests.cs에서는 정확히 `ChatAsync_WithErrorResponse_ShouldThrowInvalidOperationException`로
존재하지만(`grep`로 세 파일 모두 확인), `GoogleClientTests.cs`는 이 이름 패턴을 쓰지 않고
`ChatAsync_WithMissingCandidates_ShouldThrowInvalidOperationException`을 쓰며(내용은 열어서
확인 — `candidates` 필드 누락 시 `InvalidOperationException`, 메시지에 "생성된 후보군"
포함), `OllamaClientTests.cs`에는 이 계열의 테스트가 아예 없다(전체 테스트 3개 —
Gemma4ChannelThought, StandardThinkTag, DiversifyTemperatureBasedOnEffort — 중 오류 응답 관련은
없음) — `OllamaClient.cs:145-158`에 같은 가드 구현은 있으나 미검증이다. 인용을 클라이언트별
정확한 이름으로 고치고, Ollama의 공백을 "테스트가 없다"고 명시했다(테스트를 새로 쓰지는
않았다 — 이 작업 범위 밖).

**Minor — `SpExecutionService` 인용이 검증 범위를 과장했다.** 원 인용은
`ErrorCode` 필드가 검증된다고 암시했지만, `SpExecutionService_ShouldSoftFail_OnInvalidConnectionString`을
다시 열어 확인한 결과 `Assert.Contains("FAIL", resultJson)`과
`Assert.Contains("dbo.TestProc", resultJson)`만 단언한다 — 직렬화된 문자열에 `FAIL`과
프로시저명이 있는지만 검사하고 `ErrorCode` 필드명이나 그 값은 직접 단언하지 않는다. 인용을
"결과에 `FAIL`과 프로시저명이 포함되는지 검사 — `ErrorCode` 필드 자체는 직접 단언하지
않음"으로 고쳤다.

### 크기 (Fix Round 1 이후)

`docs/architecture.md`는 이번 라운드에서 건드리지 않았다(141,877B, 변경 없음). `AGENTS.md`는
52,492B → 52,730B로 238B **늘었다** — 재귀 불릿 축소(−157B)보다 세 곳의 정밀한 테스트 인용
추가(SpExecutionService, TryGetProperty/Google/Ollama 공백 명시, Ollama 온도)가 더 컸기
때문이다. 이 라운드의 목적은 순감축이 아니라 "테스트가 있다고 주장하는 곳은 실제로 있고,
없다고 침묵하는 곳은 침묵하지 않는다"는 정확성이었다.

링크 검사(둘 다 무출력, 재확인): `AGENTS.md`의 `./` 링크 32개, `docs/architecture.md`의
`../src/` 링크 104개·`../tests/` 링크 23개 — 전부 존재 확인. `<!-- synced-through: c8d6074 -->`
변경 없음.

## Phase 2b 결과 — 범주 4 (검증 오케스트레이션 및 파이프라인 흐름) (Task 4, 2026-08-14)

**구간.** WAVE_BASE `6e7bd37`(Task 3 Fix Round 1 이후) 기준 헤딩에서 재계산: 범주 4는
AGENTS.md L86–L115(`### ⚙️ 범주 4.`부터 `### 🔒 범주 5.` 직전까지, 편집 전 상태). 이
구간에는 이번 작업이 가장 위험하다고 지목한 "기계가 잡을 수 없는 프롬프트 설계 제약"이
섞여 있어, 계획서가 확정한 8개 판정을 먼저 그대로 적용한 뒤 그것으로 해소되지 않는
600바이트 초과 줄(L89, L108, L109)에 한해서만 손댔다. 근거 없이 줄이거나 옮긴 곳은
없다 — L89·L109는 architecture.md에서 실제 중복을 확인한 뒤에만 축약했고, L108은
중복을 찾지 못했으므로 이동하지 않고 표현만 다듬었다(아래에서 각각 구분해서 적는다).

**판정 요약(계획서 확정 8개).**

| 대상(원 줄) | 판정 | 처리 |
|---|---|---|
| L107 "코드가 강제하는 제약은 프롬프트에도" | 기계가 잡을 수 없음 | 원문 유지, 계획서가 승인한 방식대로 3개 예시(`ErrorCodes`/`MaxSteps`/`LegacyProcedures`)를 불릿으로 줄바꿈만 함(내용 삭제 없음) |
| L112 하이브리드 영문 프롬프트 구조 | 기계가 잡을 수 없음 | 원문 유지, 한 글자도 안 건드림 |
| L113 Anti-Shortcut 프롬프트 제약 | 기계가 잡을 수 없음 | 원문 유지, 한 글자도 안 건드림 |
| L114 캐시 워밍 순서 | `RunConsolidatedPipeline_WarmsCacheBeforeFanningOut`가 검사(테스트를 열어 S01이 다른 단계보다 먼저 끝남을 단언함을 확인) | 계획서가 준 문구 그대로 축소, 근거는 `architecture.md §4.13`(직접 열어 같은 캐시 워밍 서술과 Claude 경로 예외를 확인) |
| L115 예외 재시도 지연 | `RunConsolidatedPipeline_WhenStepGenerationThrows_DelaysRetryWithJitter`/`..._WhenStepMissesFloor_RetriesWithoutDelay`가 검사(두 테스트를 열어 각각 지터 지연 있음/없음을 단언함을 확인) | 계획서가 준 문구 그대로 축소 |
| L101 검증 종료 상태 정직성 | 사람의 판단 + `VerificationOutcome.cs` 주석 | 규칙 문장(네 값으로만 표현, bool/null 대체 금지)만 남기고 표기·게이팅·캐시 경계 세부는 `architecture.md §4.4.4`로(직접 열어 표기 단일화·점수 게이팅·캐시 경계 세 절 모두 있음을 확인, AGENTS.md 원문보다 두꺼움) |
| L106 CLI 제공자 원칙 | `architecture.md §4.5`가 더 두껍게 보유(직접 열어 ApiKey/Command·ModelName 전달·temperature 무시·CliFailureClassifier 분류·토큰 매핑까지 AGENTS.md 원문과 동일한 내용을 더 상세히 서술함을 확인) | 핵심 규칙(ApiKey 대신 Command만, 자동 폴백 금지 후 CliFailureClassifier로 분류)만 남기고 나머지는 §4.5로 |
| L96–99(구) L2 Actor-Critic 흐름 | `architecture.md §4.4`가 보유(§4.4.2를 직접 열어 dynamic 모드 병렬 생성·Fast-Pass·Consolidator 합성, 로컬 모델 3구역 분할 생성 서술이 AGENTS.md 원문보다 상세함을 확인) | `MaxL2Attempts`·누적 피드백 규칙(사유는 [CriticFeedbackLog.cs](../../../src/ReSet.Core/Services/CriticFeedbackLog.cs) 자신의 `<summary>`가 이미 서술)과 `IsLocalProvider()` 사용 규칙만 남기고 흐름 상세는 §4.4.2로 |

**표에 없었지만 600바이트 초과로 직접 판정한 3줄.** Step 4의 전체 라인 예산 검사가
범주 4 안에서 표 밖의 줄도 초과함을 드러냈다. 각 줄을 architecture.md에서 직접
대조해 중복 여부를 확인한 뒤에만 손댔다:

| 원문 위치(구 줄) | 바이트(전/후) | 사유 | 근거 위치(직접 열어 확인) |
|---|---|---|---|
| L89 Mermaid 다이어그램 자동 정화 상세(화살표 라벨·노드 ID·래핑 규칙, subgraph/체이닝 화살표 예외) | 644 → 284 | architecture.md가 더 두껍게 보유 | `architecture.md:538`(§4.4.1) — 같은 정화 항목을 더 상세히 서술, `subgraph`/체이닝 화살표 예외까지 포함 |
| L109 재귀 객체별 검증과 산출물 모드의 `AllowExternalDatabaseConnections`/`DependencyArtifactMode`/`PortableBundle` 세부 | 1023 → 427 | architecture.md가 더 두껍게 보유 | `architecture.md:411-412`(§4.1.1) — 크로스 DB 스위치, `Reference`/`PortableBundle` DDL 정책을 그대로 서술 |
| L105(신설, 원래 CLI 원칙 문단의 일부) 토큰 집계 정직성 | 예산 초과분은 분리로 해소(내용 손실 없음) | 600바이트 초과 — 원문 유지 대상이 아니므로 두 불릿으로 나눔(토큰 정직성 / 무인 배치 가드) | 줄바꿈만, 삭제 없음 |

**L108(원 문서 "리뷰 시 풍부한 컨텍스트 유지")은 근거를 찾지 못해 표현만 다듬었다.**
`architecture.md`에 `ReviewSpecificationAsync`/`BuildSpMetadataTexts` 언급이 없어(직접
`grep` 확인, 결과 없음) 이동하지 않았다. 사람 판단만 잡는 프롬프트 컨텍스트 규칙으로
보고 의미를 그대로 둔 채 "정상"·"실제" 등 군더더기만 줄여 630B → 520B로 예산 안에
넣었다 — 단어 하나도 규칙의 의미를 바꾸지 않았다.

### 원문 유지(사람의 판단만이 잡는 항목)

- L107 "코드가 강제하는 제약은 프롬프트에도 실으십시오" — 원문 유지, 4개 불릿으로 줄바꿈
  (내용 삭제 없음이라고 이 라운드에서 적었으나, 이 주장은 `grep -c`만으로 검증한 것이었고
  실제로는 "실측 사례로" 두 글자가 빠져 있었다 — 정정 경위는 아래 "Fix Round 1" 참고)
- L112 하이브리드 영문 프롬프트 구조 준수 — 원문 유지
- L113 스키마 및 환각/숏컷(Anti-Shortcut) 차단 룰 유지 — 원문 유지

원문 유지 확인(`grep -c`, AGENTS.md 전체) — **주의: 이 표는 조각의 생존만 증명하고 전체
텍스트의 완전성은 증명하지 않는다.** 완전성 검증 방법은 아래 "Fix Round 1"에서
정정했다.

```
코드가 강제하는 제약은 프롬프트에도    1
Anti-Shortcut                          2
하이브리드 영문                        1
```

### 크기

| 파일 | Phase 2b 이전(Task 3 Fix Round 1 이후, WAVE_BASE `6e7bd37`) | Phase 2b 이후 |
|---|---|---|
| `AGENTS.md` | 52,730 B | 48,011 B (−4,719 B) |
| `docs/architecture.md` | 141,877 B | 141,877 B (변경 없음 — 인용한 모든 근거가 이미 architecture.md에 있었으므로 새로 옮길 내용이 없었다) |

링크 검사: `AGENTS.md`의 `./` 링크가 32개 → 31개로 줄었다(계획서가 승인한 캐시 워밍
불릿 교체 문구에서 `PromptCacheBreakpointPolicy.cs` 링크 1개가 빠졌기 때문 — 남은 31개
전부 존재 확인, 무출력). `docs/architecture.md`의 `../src/` 링크 104개·`../tests/` 링크
23개는 변경 없음(무출력). `<!-- synced-through: c8d6074 -->` 양쪽 파일 모두 변경 없음.
`.cs` 파일은 변경하지 않았으므로 `dotnet build`/`dotnet test`는 돌리지 않았다(`git status
--short`가 `AGENTS.md` 한 줄만 보고).

## Fix Round 1 (Task 4, 2026-08-14) — 검증 방법 자체가 부실했던 문제

독립 리뷰가 SPEC COMPLIANCE FAIL(Critical 2건 + Important 1건 + Minor 1건)을 냈다. 공통
원인: 원안이 텍스트 완전성을 `grep -c`로 "조각이 살아 있는지"만 확인했고, 전체를 이어
붙여 대조하지 않았다. 그 결과 원문 유지 대상에서 단어가 실제로 빠졌는데도 통과로
보고했다.

**Critical 1 — L107이 여전히 600바이트를 넘었다(689B).** Task 4 Step 4는 "넘는 줄이
원문 유지 대상뿐인지 확인하고, 넘으면 더 쪼갠다"고 명시했는데, 원안은 4개 불릿으로
나눈 뒤 첫 불릿(인트로+마감 규칙문 결합)이 689B로 여전히 예산을 넘는 것을 놓쳤다.
리뷰가 지목한 분할점("…아무도 눈치채지 못하는 종류의 실패입니다." 뒤)을 확인한 뒤 그
지점에서 실제로 나눴다. 처음엔 두 번째 줄에 굵은 제목을 반복("...실으십시오 (계속)**:")
했는데, 이는 원문에 없던 새 글자를 보태는 것이라 즉시 되돌리고, 다른 하위 불릿들과
같은 `        - ` 들여쓰기만 쓰는 순수 줄바꿈으로 바꿨다:

```
L107: *   **코드가 강제하는 제약은 프롬프트에도 실으십시오**: ...아무도 눈치채지 못하는 종류의 실패입니다.   (365B)
L108:     - 제약을 코드에 새로 넣을 때는... 같은 결함이 세 번 나왔습니다:                                      (333B)
L109:     - `ErrorCodes` 빈 배열                                                                                (33B)
L110:     - `MaxSteps`: 실측 사례로 목차가...32단계로 들어왔습니다.                                             (319B)
L111:     - 그리고 규칙 없이 JSON 예시에만 등장하던 `LegacyProcedures`: ...무력화됐습니다.                       (264B)
```

**Critical 2 — "실측 사례로"가 빠져 있었다.** 리뷰가 지목한 그대로였다 — `MaxSteps` 예시
불릿을 쓸 때 "목차가 73단계를..."로 바로 시작해 두 단어를 흘렸다. 복원했다.

**전체 완전성 재검증(이번 라운드에서 처음 도입).** `grep -c`는 조각의 생존만 증명하고
완전성은 증명하지 않는다는 지적을 받아들여, 이번에는 base(`6e7bd37`)의 L107 한 줄과
현재 5줄(L107–L111)을 각각 토큰화(불릿 기호·줄머리 공백 제거, 백틱 코드스팬은 통째로
보존, 나머지는 공백 및 `.,:()—` 경계로 분리)한 뒤 **정렬된 토큰 다중집합**으로 비교했다.
1차 실행에서 "그리고"가 새로 빠진 것을 발견했다(열거형 `MaxSteps, 그리고 LegacyProcedures`를
불릿으로 쪼개며 접속사를 지운 것 — 문체상 자연스러워 보였지만 "한 글자도 줄이지 않는다"
제약에는 어긋난다). 복원 후 재실행:

```
BASE token count: 117
NEW  token count: 117
IDENTICAL token multisets — no words added or removed.
```

토큰 다중집합이 완전히 같다는 것은 (a) 정렬 후 비교이므로 재배치는 허용하고 순서
정보만 버리며, (b) 같은 문자열이 두 번 나오면 두 번 다 있어야 일치하므로 우발적 중복도
잡는다는 뜻이다. 순서 자체(마감 규칙문을 예시 앞으로 옮긴 것)는 재구성이지 삭제가
아니므로 이 검증 범위 밖이며, 아래 "미해결 검토 대상"에 그 사실을 남긴다.

**미해결 검토 대상 — 순서 재구성.** 원문은 [인트로]→[MaxSteps 실측 서술]→[3항목 열거]→
[마감 규칙문] 순이었다. 지금은 [인트로]→[마감 규칙문+열거 도입]→[ErrorCodes]→[MaxSteps]→
[LegacyProcedures] 순이다 — 마감 규칙문("제약을 코드에 새로 넣을 때는...")을 열거 앞으로
옮겼다. 단어는 전부 보존됐지만 읽는 순서는 바뀌었다. 계획서의 "줄바꿈이지 삭제가
아니다"가 재배치까지 승인하는지는 이 대장이 판단할 권한 밖이므로, 조정자가 이 순서를
받아들이지 않으면 마감 규칙문을 예시 뒤(LegacyProcedures 다음)로 되돌리는 재작업이
필요하다는 것만 기록해 둔다.

**Important — L91·L92·L94-95에 누락된 테스트 인용을 추가했다.** `doc-audit.sh`는
`[Foo.cs]` 대괄호 링크에서만 심볼을 뽑으므로, 이 세 불릿처럼 클래스명을 백틱으로만
쓴 산문은 전부 `산문(수동판정)`으로 나와 자동 신호가 없었다("산문"은 "찾을 게 없다"가
아니라 "기계가 할 말이 없다"는 뜻임을 이번에 확인했다). 각 테스트를 열어 실제로
무엇을 단언하는지 확인한 뒤 인용을 추가했다:

| 불릿 | 테스트 | 실제 단언(직접 읽고 확인) |
|---|---|---|
| L91 `ErrorCodes` 빈 배열 → 검증 불가 | `MechanicalValidatorTests.ValidateBatchStep_WithEmptyErrorCodes_Fails` | 빈 `ErrorCodes`로 `ValidateBatchStep` 호출 시 `IsValid=false`, 오류 메시지에 `"ErrorCodes"` 포함 |
| L91 `LegacyProcedures`가 비면 정상 | `MechanicalValidatorTests.ValidateBatchStep_WithNoLegacyProcedure_TreatsEmptyErrorCodesAsNotApplicable` | `LegacyProcedures`와 `ErrorCodes`가 둘 다 빈 배열이면 `IsValid=true`, `PlanDefects` 비어 있음 |
| L92 `TargetTables`는 정적 분석이 진실의 원천 | `SpecTargetTableExtractorTests.Extract_ShouldSplitWriteTargetsFromReadSources` | `SpecTargetTableExtractor.Extract`가 정적 분석의 Insert/Delete를 쓰기 집합으로, Select를 읽기 집합으로 정확히 분리 |
| L94–95 대조 기준은 프롬프트에 실린 컬럼, DB 전체가 아님 | `SchemaPromptColumnSelectorTests.Select_WithReferencedColumns_ShouldKeepOnlyThoseAndKeys`/`..._WhenNothingMatches_ShouldFallBackToAllColumns` | 참조된 컬럼만 남기고 나머지는 제외(전체 컬럼이 아님을 확인), 매칭이 없을 때만 전체로 폴백 |
| L94–95 스키마 주장은 L1이 기계적으로 대조 | `SchemaClaimGateRegressionTests.TheSpecThatScoredNinetyOne_ShouldNowFailL1` 등 | 실제 존재하는 컬럼을 "존재하지 않음"으로 적은 명세서가 `SchemaClaimFalse`로 L1에서 거부됨(88~94점을 받았던 실물 픽스처로 재현) |

기각한 매핑은 없다 — 리뷰가 지목한 4개 전부 실제로 그 불릿이 서술하는 동작을 검사했다.

### 크기 (Fix Round 1 이후)

`docs/architecture.md`는 이번 라운드에서 건드리지 않았다(141,877B, 변경 없음). `AGENTS.md`는
48,011B → 48,328B로 317B 늘었다 — 복원한 두 단어("실측 사례로", "그리고")와 4개 불릿에
추가한 4개 테스트 인용이 분할로 아낀 바이트보다 컸다. 목적은 순감축이 아니라 완전성과
인용 커버리지였다.

재확인: 범주 4(L86–L119) 안에 600바이트를 넘는 줄 없음(`L107`이 365B로 원문 유지 항목
중 가장 김). 원문 유지 3항목 `grep -c` 재확인 — "코드가 강제하는 제약은 프롬프트에도" 1,
"Anti-Shortcut" 2, "하이브리드 영문" 1, "실측 사례로" 1(신규 확인 항목). 링크 검사
재확인(둘 다 무출력): `AGENTS.md`의 `./` 링크 31개, `docs/architecture.md`의 `../src/`
링크 104개·`../tests/` 링크 23개. `<!-- synced-through: c8d6074 -->` 양쪽 파일 모두
변경 없음. `git diff --stat`이 `AGENTS.md`만 보고하며, 변경된 줄은 전부 L88–L112(범주 4
안) 안에 있다. `.cs` 파일 변경 없음 — `dotnet build`/`dotnet test` 미실행.

## Phase 2c 결과 — 범주 6·7 (외부 코딩 에이전트, 메타데이터 정화) (Task 5, 2026-08-14)

**구간.** WAVE_BASE `7920c5e` 기준 AGENTS.md L125–L153(`### 🔌 범주 6.`부터
`### 🌳 범주 8.` 직전까지, 편집 전 상태). 계획서 자신의 경고대로 범주 7은 원문 유지
비율이 가장 높은 구간이었다 — 여기 있는 서술 대부분이 "AI에게 무엇을 시킬지"에 관한
프롬프트 설계 규칙이라 어떤 테스트도 잡지 못한다. `doc-audit.sh`는 이 구간의 27줄 중
25줄을 `산문(수동판정)`으로 표시했다(대괄호 `[Foo.cs]` 링크가 아니라 백틱 클래스명만
쓴 서술이라 자동 신호가 없었다는 뜻 — "찾을 게 없다"가 아니라 "기계가 할 말이 없다"로
읽고, 각 불릿이 언급한 클래스명을 테스트 프로젝트에서 직접 grep했다).

**600바이트 초과 11줄(전부 이 구간).** L127(768) L128(657) L132(950) L134(865)
L137(1286) L138(606) L142(627) L146(625) L148(1337) L149(1933) L151(661)(WAVE_BASE
기준 원 줄번호). 계획서의 우선순위(1. 라우팅+축약 2. 무손실 분할 3. 못 하면 보고)를
그대로 따랐다.

**라우팅+축약(7줄) — 각 줄을 architecture.md 또는 테스트로 확인한 뒤에만 축약.**

| 원문 위치(원 줄) | 바이트(전→후) | 사유 | 근거 위치(직접 열어/그레핑해서 확인) |
|---|---|---|---|
| L127 번들 분할 제공 | 768 → 529 | 구조 서술(진입점/공통/단계/회차 문서로 나눔)이 architecture.md에 더 상세히 있음, 구조 테스트도 있음 | `architecture.md §4.11`(직접 열어 동일 구조·이유 서술 확인), `InstructionBundleWriterTests.WriteAsync_ShouldPlaceEntryPointAtAgentRoot`/`..._ShouldWriteOneFilePerStep`/`..._ShouldWriteCommonAndVerificationFiles`(각각 열어 진입점 위치·단계별 파일 1개·공통 파일 존재를 단언함을 확인). 출력 폴더 자동 생성·개별 SP 비생성·`output/Jobs/{JobName}/` 격리 부분은 이 테스트들이 다루지 않는 CLI(Program.cs) 계층 판단이라 원문 그대로 남김(아래 "테스트 없는 규칙" 참고) |
| L128 분할 실패 시에도 지침은 앞으로 | 657 → 511 | architecture.md §4.11의 "부분 분할 금지" 불릿과 거의 같은 문장, 두 테스트가 정확히 이 주장(폴백에서도 순서 고정, 한 단계라도 못 찾으면 전체 폴백)을 단언 | `architecture.md §4.11`, `InstructionEntryPointComposerTests.Compose_ShouldPlaceGuidelinesBeforePlanLink_EvenInFallback`(폴백 마크다운에서도 지침 인덱스가 계획 링크보다 앞임을 단언), `PlanBoundaryResolverTests.ResolveSteps_ShouldFailWholly_WhenOneStepCannotBeLocated`(한 단계를 못 찾으면 `Split=false`, `Steps` 빔, 경고 있음을 단언 — 테스트 주석이 "부분 분할은 하지 않는다"고 직접 씀) |
| L134(신 L135) 대화형/배치 인자 분리 | 865 → 570 | `Arguments`/`BatchArguments` 분리와 `{jobDir}`/`{specRoot}` 스코프 분리 둘 다 전담 테스트가 있음 | `CodingEngineTests.CodingEngineFactory_ShouldUseInteractiveArguments_WhenNotBatchMode`/`..._ShouldUseBatchArguments_WhenBatchMode`/`..._ShouldThrow_WhenBatchModeAndBatchArgumentsMissing`(세 개 다 열어 대화형/무인 분기와 빈 `BatchArguments` 시 예외 메시지에 "BatchArguments" 포함을 단언함을 확인), `ArgumentTemplateResolverTests.Resolve_ShouldReplaceJobDir_WithRawGrandparentPath`/`ResolveSpecRoot_ShouldCoverTheSpecLinkTheStepTaskFileEmits`/`ResolveSpecRoot_ShouldNotGrantTheWholeOutputRoot`(형제 경로 계산과 출력 루트 전체를 열지 않음을 각각 단언) |
| L137(신 L138) 회차 게이트는 fail-closed | 1286 → 583 | "검증 대상 없으면 통과 아님" 절반은 architecture.md §4.11의 "회차별 검증 범위" 불릿과 동일, 재시도 상한/사유 선기록 절반은 전담 테스트가 있음 | `architecture.md §4.11`, `CodegenStagedWorkflowTests.RunStagedWorkflowAsync_ShouldCapRetries_WhenTheStepSourceNeverAppears`(상한 -1=무제한을 줘도 2회에서 멈춤을 단언, taskFile에 실패 사유가 다음 시도 전에 기록됨을 단언), `..._ShouldFailStepThatHasNoSpecToCompareAgainst`(대조할 설계서가 없으면 통과가 아니라 실패로 기록되고 `LastGapSummary`에 사유가 남음을 단언) |
| L146(신 L149) UPDATE 매핑표는 정적 파서가 확정 | 625 → 596 | `AstUpdateMappings` 추출 메커니즘이 architecture.md §4.3에 이미 서술, `MechanicalValidator`의 대조 동작은 전담 테스트가 있음 | `architecture.md §4.3`(L429, 동일한 "이미 채워진 표"/"MechanicalValidator가 대조" 서술 확인), `MechanicalValidatorTests.Validate_WhenAnExpectedUpdateColumnIsMissing_ShouldReportIt`(누락된 UPDATE 컬럼을 실패로 보고함을 확인) |
| L148(신 L151) 의존 스키마 덤프 필터링 | 1337 → 545 | 판정 로직(3-part 정확 일치/베이스 이름 폴백/14개 명세서 결함) 전체가 `SchemaPromptColumnSelector` 클래스와 `KeyMatchesDependency` 메서드의 `<summary>` 문서 주석에 이미 있고, 6개 테스트가 각 분기를 단언 | `SchemaPromptColumnSelector.cs`의 클래스/메서드 `<summary>`(직접 읽어 3-part 정확 일치, DB 컨텍스트 없을 때 베이스 이름 폴백, 과다 포함이 과소 포함보다 나은 이유, "14개 명세서를 망가뜨린 결함" 서술이 AGENTS.md 원문과 동일함을 확인), `SchemaPromptColumnSelectorTests.Select_WithDbContext_ShouldNotMergeDifferentDatabases`(3-part 일치 시 다른 DB를 병합하지 않음을 단언), `..._WhenCanonicalMismatchDropsColumns_ShouldReport`("14개 명세서를 망가뜨린 결함의 재현"이라는 주석과 함께 정확히 그 시나리오를 재현해 결함을 보고함을 단언) |
| L151(신 L157) Mermaid flowchart 생성 규칙화 및 정화 | 661 → 원문 프롬프트 규칙 유지, "정화기 동작" 서술만 제거 | 계획서가 지정한 정확한 분리 지점 — 화살표 라벨/노드 `@` 기호 규칙은 AI에게 시키는 프롬프트 설계라 원문 그대로 두고, "노드 ID 공백·언더스코어 일괄 제거" 서술만 `MechanicalValidator.CleanseMermaidCode`가 담당하는 코드 동작이라 인용으로 교체 | `MechanicalValidator.cs:1180`(`CleanseMermaidCode`가 실제로 노드 ID 공백/언더스코어를 제거하는 코드임을 확인), `MechanicalValidatorTests.PostProcessMarkdown_ShouldCleanseMermaidCode`(`A_1`→`A1`, `B_2`→`B2`로 언더스코어가 제거됨을 단언) |

**무손실 분할(4줄) — 근거를 찾지 못했거나 축약이 원문의 판단 근거를 왜곡할 위험이 있어
한 글자도 지우지 않고 줄만 나눴다.**

| 원문 위치(원 줄) | 바이트 | 분할 사유 |
|---|---|---|
| L132 동적 코드 생성 시점 제약 | 950 | 스탠드얼론 메뉴 재기동(Resume) 판정 로직에 대한 테스트를 찾지 못함(`grep -rn "스탠드얼론\|Resume"` tests/ 결과 없음). CLI 레벨(Program.cs) 워크플로 판단이라 사람의 판단만이 잡는 항목으로 보고 원문 유지 |
| L138(신 L139) 자가 수정 및 TDD 테스트 피드백 루프 | 606 | 외부 에이전트에게 자율 TDD 루프를 시키는 프롬프트 설계 지시라 어떤 테스트도 잡을 수 없음(`CodeVerificationOrchestratorTests`에 자가 수정 관련 테스트가 있으나 이 불릿이 말하는 "외부 에이전트의 자율 루프"가 아니라 우리 L1/L2 파이프라인의 자가 보완을 검사하는 것이라 인용 시 오귀속 위험). 600바이트를 6바이트만 초과해 축약보다 분할이 안전하다고 판단 |
| L142(신 L144) 클렌징 스크립트 및 동기화 | 627 | "기본 제거되어 있다"는 이유(로컬 LLM 환각 방지)와 "물리적 존재 시에만 승인" 동작은 부분적으로만 테스트가 있고(`VerificationPipelineOrchestratorTests.RunPipelineAsync_InteractiveMode_SyncsDb_CatchesSqlException`이 후자만 다룸), 크로스 DB 파일명 접두(`ResolveCleansingFileBaseName`)는 전담 테스트가 없음(`grep -rn "ResolveCleansingFileBaseName" tests/` 결과 없음). 부분 인용이 나머지 미검증 부분을 검증된 것처럼 보이게 할 위험이 있어 축약하지 않고 분할만 함 |
| L149(신 L152) 통합 배치 도메인 5대 핵심 제약 | 1933 | 계획서가 원문 유지를 명시한 항목(기계가 잡을 수 없는 프롬프트 설계 제약) — 축약 대상이 아니라 4줄로 분할 |

**무손실 분할의 검증 방법(모두 order-blind가 아닌 재구성 비교).** `grep -c`나 정렬된
다중집합은 재배치·중복을 놓칠 수 있다는 Task 4의 교훈에 따라, 분할된 각 줄에서
연속 공백 들여쓰기(신설한 줄의 앞 8칸)만 제거하고 단일 공백으로 다시 이어붙인 뒤
WAVE_BASE(`7920c5e`)의 원문 한 줄과 **파이썬 문자열 완전 일치**로 비교했다(토큰화나
정렬을 거치지 않아 순서·중복 오류를 모두 잡는다):

```
동적 코드 생성 시점 제약 (old L132) MATCH: True
자가 수정 TDD (old L138) MATCH: True
클렌징 스크립트 (old L142) MATCH: True
통합 배치 5대 제약 (old L149, 4줄로 분할) MATCH: True
```

### 원문 유지(사람의 판단만이 잡는 항목) — 4개 확인

계획서가 지정한 4개 항목 중 3개(컬럼 매핑 표 축약 금지 L145, DDL 기반 제약 조건 작성
L147, 복합 필터의 정확한 해석 L150)는 한 글자도 건드리지 않았다 — `diff`로 원문과
새 파일의 해당 줄을 직접 비교해 **바이트 단위로 동일함**을 확인했다(무출력 = 동일).
4번째(통합 배치 5대 제약, L149)는 600바이트를 훨씬 넘어(1933B) 계획서 지시대로 4줄로
무손실 분할했으며, 위 파이썬 완전 일치 비교로 재구성 결과가 원문과 정확히 같음을
확인했다(`MATCH: True`, 부분 문자열이 아니라 전체 문자열 비교).

### 산문(수동판정) 25줄을 직접 grep한 결과

`doc-audit.sh`가 신호를 주지 못한 나머지 줄(오버사이즈 11개 중 라우팅+축약 대상이 아닌
줄 포함, 그리고 예산 안에 들어 손대지 않은 줄들) 중 백틱 클래스명이 있는 것만 테스트
프로젝트에서 직접 grep했다. 새로 발견해 인용을 추가한 것은 위 라우팅 표에 이미
반영했다(`CodingEngineTests`, `ArgumentTemplateResolverTests`,
`SchemaPromptColumnSelectorTests`, `MechanicalValidatorTests`,
`InstructionBundleWriterTests`, `PlanBoundaryResolverTests`,
`InstructionEntryPointComposerTests`, `CodegenStagedWorkflowTests`). 손대지 않은 줄
중에는 L131 데이터 액세스 경계 규칙(`DataAccessPolicy`)이 있는데, 이미
`[DataAccessPolicy.cs]` 대괄호 링크를 갖고 있어 `doc-audit.sh`가 자동으로 "중복:
architecture.md"로 분류했었다(331B, 예산 안이라 손대지 않음).

### 테스트 없는 규칙(이름을 남긴다)

- L127의 "개별 SP 분석 시에는 번들을 만들지 않으며, 통합 배치 시에만
  `output/Jobs/{JobName}/`에 격리" — `src/ReSet.Cli/Program.cs`(854, 970, 1413행)의
  CLI 오케스트레이션 로직이며 `tests/ReSet.Core.Tests`에 이 트리거 조건을 검사하는
  테스트가 없다.
- L132 "동적 코드 생성 시점 제약"의 스탠드얼론 메뉴 재기동(Resume) 판정 — 위 무손실
  분할 표에 적은 대로 테스트 없음.
- L138(신 L139) "자가 수정 및 TDD 테스트 피드백 루프" — 외부 에이전트에게 시키는
  자율 워크플로 지시라 우리 테스트 스위트가 검사할 수 있는 대상이 아니다.
- L142(신 L144)의 "AI 분석 완료 시 보완 스크립트 생성 기능은 기본 제거되어 있다"는
  이유 서술과 크로스 DB 파일명 접두(`ResolveCleansingFileBaseName`) — 전담 테스트 없음.
- ~~L133(신 L134) "프로세스 양방향 제어"(`ExternalCliCodingEngine`의 콘솔 스트림 상속,
  `process.Kill(true)`) — `doc-audit.sh`가 "근거없음(이동필요)"로 표시했었다.~~ **정정
  (Task 6, 2026-08-14): 이 판정은 `doc-audit.sh`의 알려진 사각지대다.** 스크립트는
  대괄호 `[Foo.cs]` 링크에서만 대상 클래스를 추출하는데, 이 불릿은 클래스명을 백틱
  (`` `ExternalCliCodingEngine.cs` ``)으로만 적어 스캐너가 대상을 못 찾고 근거 없음으로
  잘못 표시했다. 실제로는 `docs/architecture.md §5.3`(746–747행, "모드별 콘솔 스트림
  처리"와 "취소 및 프로세스 강제 정리")이 이 불릿과 거의 같은 문장으로 이미 다루고
  있음을 직접 열어 확인했다: 대화형 모드의 부모 콘솔 스트림 상속
  (`RedirectStandardInput/Output = false`), 무인 배치의 stdin 닫기, 그리고
  `CancellationToken` 수신 시 `process.Kill(true)`로 프로세스 트리를 강제 정리하는
  내용까지 일치한다. L133은 범주 6(외부 코딩 에이전트)에 속해 이번 Task 6(범주
  1·3·5·8)의 쓰기 범위 밖이므로 AGENTS.md의 해당 불릿 자체는 고치지 않았고, 이 대장
  항목만 정정한다. 다음 라운드가 이 줄을 다시 감사할 때는 "근거없음"이 아니라
  "중복:architecture.md §5.3"으로 재분류해야 한다.

### 크기

| 파일 | Phase 2c 이전(WAVE_BASE `7920c5e`) | Phase 2c 이후 |
|---|---|---|
| `AGENTS.md` | 48,328 B | 46,052 B (−2,276 B) |
| `docs/architecture.md` | 141,877 B | 141,877 B (변경 없음 — 인용한 모든 근거가 이미 architecture.md 또는 코드 `<summary>`/테스트에 있었으므로 새로 옮길 내용이 없었다) |

**전체 줄 예산 재확인.** `LC_ALL=C awk 'length($0)>600'` 전체 파일에서 무출력(600바이트
초과 줄 없음, 범주 6·7의 11개 전부 해소). **링크 검사(둘 다 무출력).** 범주 6·7
구간(L125–158) 안의 대괄호 링크는 `[DataAccessPolicy.cs]`(L131, 안 건드림)와
`[ExternalCliCodingEngine.cs]`(L134, 안 건드림) 2개뿐이며 둘 다 파일이 실제로 존재함을
확인했다(`test -f` 무출력 아닌 "OK" 출력으로 확인). `<!-- synced-through: c8d6074 -->`
양쪽 파일 모두 변경 없음. `git diff 7920c5e -- AGENTS.md`의 유일한 hunk가
`@@ -124,31 +124,37 @@`로 범주 6(L125 시작)~범주 8 직전(L158) 구간 안에만 있음을
확인했다(범주 1–5·8 무변경). `docs/architecture.md`는 `git diff` 무출력(완전 무변경).
`.cs` 파일 변경 없음(`git status --short`가 `AGENTS.md` 한 줄만 보고) — `dotnet
build`/`dotnet test` 미실행.

## Phase 2d 결과 — 범주 1·3·5·8 (보안, UI/UX, 런타임 격리, 워크트리) (Task 6, 2026-08-14)

**구간.** WAVE_BASE `aa84c44` 기준 AGENTS.md L43–47(범주 1), L72–84(범주 3), L120–123(범주
5), L160–164(범주 8). 계획서 자신이 이 네 범주를 "이미 짧고 전부 판단 규칙이라 거의
손대지 않는다"고 명시했고, 실측도 그것을 확인했다 — 12개 불릿 중 11개가 어떤 테스트도
잡을 수 없는 "사람/에이전트의 판단만이 잡는" 항목이었다.

**범주 1(보안, 3줄) — 전량 유지, 인용 1건 추가.** 첫 두 불릿(비공개 키 커밋 금지,
`appsettings.local.json` 신설)은 Git 추적 정책이라 어떤 단위 테스트도 검사할 수 없다.
세 번째 불릿("검증기는 ApiKey만 가져간다")은 실제로 소스 수준 테스트가 있음을 발견해
인용을 추가했다(삭제가 아니라 인용 추가이므로 원문은 한 글자도 지우지 않았다):
`ValidatorConfigurationTests.LoadConfiguration_DoesNotMergeTheCliProjectsLocalSettings`
(`src/ReSet.Validator.Cli/Program.cs`에 `builder.AddJsonFile(Path.GetFullPath(path)`
패턴이 없음을 단언 — 파일 전체 병합 금지)과
`ApiKeyFallback_StillReadsOnlyTheApiKeyFromTheCliProject`(`LoadApiKeyWithFallback`이
존재하고 `tempConfig[$"AiSettings:Providers:{provider}:ApiKey"]`로 ApiKey 필드 하나만
읽음을 단언)를 열어 직접 확인했다. AGENTS.md 불릿이 말하는 "파일을 통째로 병합하는
코드를 되살리지 마십시오"와 "ApiKey만 가져갑니다"를 정확히 한 쌍으로 검사하는 테스트라
정확한 인용이다.

**범주 5(런타임 격리, 2줄) — 전량 유지, 테스트 없음 확인.** "트랜잭션/타임아웃 격리"
(Rollback·ValueTask 동적 대기·Java 30초 타임아웃)와 "모의 데이터 수명주기"(Seed/자동
소거)를 실제로 검사하는 테스트가 있는지 열어서 확인했다. `RunnerTests.cs`에는
`CSharpReflectionRunner_ExecuteAsync_HandleConnectionError_SoftFail` 등 소프트 페일
테스트만 있고 `Rollback()` 호출이나 타임아웃 설정을 단언하는 테스트는 없다.
`SandboxSeedingServiceTests.cs`에는 `SanitizeTableName_ShouldEscapeTableNamesCorrectly`와
`ConvertJsonValue_ShouldParseJsonElementsCorrectly` 두 개뿐이며 둘 다 문자열/JSON 변환
헬퍼 단위 테스트이고 Seed나 Truncate 생명주기를 검사하지 않는다. 계획서의 "체크리스트가
묻는 항목" 판정이 맞다 — 테스트가 아니라 사람이 체크리스트로 확인해야 한다. 인용을
붙이지 않고 원문 그대로 두었다.

**범주 8(워크트리, 3줄) — 전량 유지.** git worktree 사용 절차는 프로세스 규칙이라
정의상 코드 테스트의 대상이 아니다. 확인만 하고 손대지 않았다.

**범주 3(UI/UX, 13줄) — 12줄 원문 유지, 1줄 축소(구현 세부만 이동).**
`doc-audit.sh 72 85` 실행 결과 13줄 중 12줄이 `산문(수동판정)`(백틱 클래스명만 있어
자동 신호 없음), 1줄(L76 "연결 정보 즉석 수정")이 `중복:architecture.md`로 나왔다.
그 매치는 §2.2 클래스 카탈로그의 `ConsoleUserInteraction` 행 2개(L47, L109)를 센 것이라
이 불릿이 말하는 "즉석 서버 주소/DB명 갱신" 기능과는 무관한 우연의 일치였다 — 직접 열어
확인했다. 대신 `architecture.md §5.1`("TUI 로그인 세션 및 연결 정보 실시간 변경")이 같은
기능을 더 상세히(`.session.json` 복구까지) 이미 서술하고 있어 진짜 중복 후보였지만,
이번 태스크의 위임 지시서가 명시적으로 승인한 이동 대상은 "TUI 진행도 넘버링 형식" 한
건뿐이었으므로 **이 발견은 적용하지 않고 보고만 남긴다**(다음 라운드 후보,
"테스트 없는 규칙" 절 아래 별도 기록).

이동한 1건 — **"TUI 상태 정보 강화 및 간소화"(L78)**: 규칙 문장(메인 태스크에 모델명+
Effort 노출, 하위 진행 단계에서는 모델명 반복 금지)은 판단 규칙이라 남기고, 구체적
서식 스펙(`괄호 없는 순번(n/3.) 형식`)만 `architecture.md §5.6`(신설)로 옮겼다. 코드로
직접 확인: `VerificationPipelineOrchestrator.cs`가 `progressScope.AddTask("phase1",
"1/3. 브레인스토밍 중...")`/`"phase2", "2/3. 목차 설계 중..."`/`"phase3", "3/3. 골격
생성 중..."`/`"phase3single", "3/3. 최종 생성 중 (단일 호출)..."`로 정확히 이 서식을
쓰고, `NotifyStatus`가 별도로 `"{jobName} - AI 통합 배치 전환 계획 수립 중
({Provider} - {ModelName}{Effort}) [{attemptText}]..."` 형태로 모델명·Effort를
메인 상태 줄에만 낸다. 3단계 흐름 밖의 단발 작업(목차 재설계, `redraft` 태스크)에는
번호를 붙이지 않는다는 사실도 소스 주석("3단계 중 하나가 아니므로 n/3. 순번을 붙이지
않는다", 2664행)에서 확인해 architecture.md 문장에 반영했다. `architecture.md`에
`n/3`·`괄호 없는`·`순번` 문자열이 이 편집 전에는 전혀 없었음을 `grep`으로 확인했다 —
새로 옮긴 내용이지 기존 서술의 재진술이 아니다.

`Markup.Escape()` 관련 인용 후보도 검토했다: `ConsoleUserInteractionTests.
MapStepSelection_WithBracketInStepName_StillMatchesTheEscapedLabel`이
`StepSelectionLabel`이 대괄호를 Spectre 규약(`[[...]]`)대로 이스케이프함을 단언하지만,
이는 단계 선택 라벨이라는 한 호출부만 검사한다. AGENTS.md 불릿은 "DB 메타데이터, AI
원문, 파일 경로 등"에 대한 **전면** 규칙을 요구하므로 이 테스트를 인용하면 실제보다
넓은 보장이 있는 것처럼 읽힌다 — Task 5의 선례(부분 일치 인용 거부)를 따라 **인용하지
않고** 원문을 그대로 두었다.

### 크기

| 파일 | Phase 2d 이전(WAVE_BASE `aa84c44`) | Phase 2d 이후 |
|---|---|---|
| `AGENTS.md` | 46,052 B | 46,177 B (+125 B — 인용 1건 추가가 넘버링 규칙 축소보다 큼) |
| `docs/architecture.md` | 141,877 B | 142,721 B (+844 B — §5.6 신설) |

계획서 Step 3의 기대치(25~35KB)는 이 태스크로 달성되지 않는다 — 이 범주 넷은 계획서가
스스로 "거의 손대지 않는다"고 선언한 구간이라 애초에 실질적 축소 여력이 없었다.
46KB대는 Phase 1(카탈로그 삭제)과 Task 3~5가 이미 확정한 크기이고, Task 6은 그 크기를
그대로 유지하는 것이 올바른 판정이다.

**라인 예산.** `LC_ALL=C awk 'length($0)>600' AGENTS.md` 전체 파일 무출력(600바이트
초과 줄 없음). **링크 검사(둘 다 무출력).** `<!-- synced-through: c8d6074 -->` 양쪽
파일 모두 변경 없음(문자열 자체는 그대로, 주변에 텍스트만 추가). `git diff aa84c44 --
AGENTS.md`의 hunk가 정확히 L44(범주 1)와 L75(범주 3) 2곳뿐임을 확인했다(범주 2·4·6·7과
범주 5·8은 무변경). `docs/architecture.md`는 758행 뒤 §5.6 신설 1개 hunk뿐. `.cs` 파일
변경 없음(`git status --short`가 `AGENTS.md`/`docs/architecture.md`/이 대장 파일만
보고) — `dotnet build`/`dotnet test` 미실행.

### 테스트 없는 규칙(이름을 남긴다, 계속)

- 범주 5 "트랜잭션/타임아웃 격리"의 `Rollback()`/`ValueTask` 동적 대기/Java 30초
  타임아웃 — `RunnerTests.cs`에 소프트 페일 테스트만 있고 이 셋을 검사하는 테스트 없음.
- 범주 5 "모의 데이터 수명주기"의 Seed/자동 소거(Truncate) — `SandboxSeedingServiceTests.cs`
  는 문자열/JSON 변환 헬퍼만 검사하고 생명주기를 검사하지 않음.
- 범주 3 "유효 디렉토리 및 통합 Job 대화형 선택 유도"의 `ShowChoices(false)` — 전담
  테스트 없음(UI 렌더링 자체를 검사하는 테스트 프로젝트가 없음).
- 범주 3 "연결 정보 즉석 수정"(L76) — **다음 라운드 후보**: `architecture.md §5.1`이
  이미 더 상세히 다루는 진짜 중복이지만, 이번 위임 범위가 명시적으로 승인한 이동
  대상이 아니라 이번 라운드에서는 적용하지 않았다.

## Task 8 — 기준선 고정과 실제 트리 게이트 (2026-08-14)

Task 1~7이 AGENTS.md를 줄이는 쪽이었다면, Task 8은 그것이 다시 자라지 않게 막는
쪽이다. `tests/ReSet.Core.Tests/DocumentationBudgetTests.cs`에 두 게이트를 추가했다
— `NoAutoLoadedDocumentExceedsItsByteBudget`(문서 전체 크기 상한)과
`NoAutoLoadedDocumentHasAnOversizedLine`(줄 하나당 600바이트 상한, 실제 병리였던
4,162바이트짜리 "목록 항목"을 직접 겨냥). 둘 다 `tests/ReSet.Core.Tests/
documentation-budget-baseline.txt`를 읽어 검사 대상과 상한을 얻는다.

**상한 계산.** 손으로 적지 않고 Task 6 종료 시점 실측에 15%를 더해 계산했다:
`BUDGET=$(( $(LC_ALL=C wc -c < AGENTS.md) * 115 / 100 ))` → 실측 46,177바이트 ×
1.15 = 53,103.55 → 정수 나눗셈으로 53,103. 기준선 파일에 `AGENTS.md = 53103` 한
줄로 저장했다. 게이트는 단방향이다 — 상한 초과만 실패하고 밑으로는 자유다
(`cancellation-policy-baseline.txt`의 양방향 잠금과 의도적으로 다르다).

**실제로 무는지 확인.** AGENTS.md에 700바이트짜리 줄을 붙여 라인 게이트가 실패함을,
53,103바이트를 넘도록 짧은 줄 120개를 붙여 크기 게이트가 실패함을 각각 확인한 뒤
원복해 다시 통과함을 확인했다(둘 다 실제로 파일을 고쳐 관찰한 것이며, 통과만 보고
끝내지 않았다).

**Fix Round 1 — 빈 기준선 구멍.** 독립 리뷰가 찾음: 게이트가 무엇을 검사할지 스스로
찾아내지 못하고 기준선 파일 내용을 전적으로 신뢰한다. `documentation-budget-
baseline.txt`에서 `AGENTS.md = 53103` 한 줄만 조용히 지워도(주석은 그대로 두고)
두 게이트 모두 검사 대상이 사라져 초록으로 통과했다(6/6, 신호 없음) —
`cancellation-policy-baseline.txt`와 달리 이 파일은 대조할 독립적인 실측(예: `src/`
스캔)이 없어서 생긴 구멍이다. `ReadBaseline`을 지연 이터레이터(`yield return`)에서
즉시 실행 메서드로 바꾸고, 파싱한 항목에 `AGENTS.md`가 실제로 있는지 단언하는 코드를
추가해 닫았다 — 항목이 없으면 두 게이트 모두 "검사가 꺼졌다"는 취지의 메시지와 함께
실패한다. 파일을 통째로 지우는 경우는 이미 `FileNotFoundException`으로 시끄러웠으므로
손대지 않았다.

**Fix Round 2 — BOM/CRLF는 대장이 아니라 측정 지점 주석으로.** 두 가지를 기록만 하고
고치지 않기로 조정자가 판정했다: (1) `File.ReadAllText`는 UTF-8 BOM을 벗기지만 셸의
`wc -c`(상한을 다시 계산할 때 쓰는 명령)는 BOM까지 세어, 셸에서 계산한 상한이 이
테스트가 재는 값보다 ~3바이트 높게 잡힌다 — 상한을 느슨하게 할 뿐 조이지 않는 방향이고
AGENTS.md는 BOM이 없어 오늘은 차이가 0이다. (2) `core.autocrlf=true` 체크아웃은 줄마다
`\r`이 붙어 이 테스트가 재는 실측값이 줄 수만큼 커진다 — 여유가 6천 바이트대라 오늘은
통과를 뒤집을 수 없지만 잠재적이다. 둘 다 이 태스크 고유의 문제가 아니라 이 대장이 아닌
`DocumentationBudgetTests.cs`의 측정 지점(`File.ReadAllText` → `MeasureBytes`) 옆
주석으로 남겼다 — 다음에 그 측정을 바꾸거나 상한을 다시 계산할 사람이 열어 볼 자리이기
때문이다.

## 브랜치 전체 리뷰 대응 (2026-08-14)

머지 전 브랜치 전체를 대상으로 한 독립 리뷰가 Important 1건을 이 대장에 대해 냈다.
아래는 그 지적과 실제로 무엇을 고쳤는지다.

**Important — `SpDefinition`(L22)의 `중복:SpDefinition.cs <summary>` 판정이 거짓이었다.**
위 Phase 1 스냅샷 표의 L22 행은 `scripts/doc-audit.sh`가 실제로 낸 출력 그대로이므로
(재현성이 이미 검증된 기록이라) 표 자체는 고치지 않는다. 다만 그 판정은 틀렸다 — 여기
정정한다.

- **원인.** `scripts/doc-audit.sh:73`의 `doc=$(awk '/\/\/\//{c+=length($0)} END{print c+0}'
  "$path")`는 링크가 가리키는 파일 **전체**의 `///` 바이트를 합산해, 그 파일 안 어느
  클래스의 요약이든 링크에 이름이 걸린 클래스의 몫으로 credit한다. `ARCH_MD` 열은
  이미 경로 기반으로 개정됐지만(커밋 `78c390b`) `SUMMARY` 열은 여전히 파일 단위다 —
  같은 약점이 컬럼 하나에만 남아 있었다.
- **실측.** [`src/ReSet.Core/Models/SpDefinition.cs`](../src/ReSet.Core/Models/SpDefinition.cs)의
  551바이트짜리 `///` 요약은 전부 `SpDefinition` 클래스(5행)가 아니라 같은 파일 안에
  중첩된 `AstUpdateMapping`(27행)·`AstUpdateAssignment`(43행) 멤버에 붙어 있다.
  `SpDefinition` 클래스 자신은 클래스 레벨 `<summary>`가 **없다**. `docs/architecture.md`
  §2.2에도 `SpDefinition` 전용 행이 없었다 — 다른 행의 산문 속에 이름만 스쳐 지나갔을
  뿐이다(`SpecExpectations`·`SpecTargetTableExtractor` 행). 따라서 삭제된 AGENTS.md L22
  ("분석된 SP 메타데이터... 를 관리하는 루트 데이터 클래스")는 실제로는 **근거없음(이동
  필요)** 이었어야 하며, `중복`은 이 문서 안 어디에도 존재하지 않는 근거를 가리킨
  거짓 판정이다.
- **바로잡음.** 다른 16개 근거없음 클래스(위 "Group A" 표)와 같은 방식으로,
  `docs/architecture.md` §2.2의 ReSet.Core 그룹에 `SpDefinition` 행을 신설해 거처를
  마련했다(`CodeObjectKey`/`CodeObjectAnalysisModels` 행 바로 다음) — 원문의 의미(DDL·
  의존성 등 분석된 SP 메타데이터를 담는 루트 데이터 클래스)를 보존하되, 중첩 AST 매핑
  타입 두 개를 함께 언급해 그 두 타입이 왜 551바이트의 `///`를 갖고도 `SpDefinition`
  자신의 요약이 아닌지가 이 행만 봐도 드러나게 했다.
- **정정된 판정.** L22 SpDefinition: ~~`중복:SpDefinition.cs <summary>`~~ → **근거없음
  (이동 필요), `docs/architecture.md` §2.2에 신규 행으로 편입**. `scripts/doc-audit.sh`는
  고치지 않는다 — 나머지 `<summary>` 단독 근거 9건은 재검토 결과 전부 정상이었고(클래스
  레벨 `<summary>`가 실제로 그 바이트 수만큼 있었다), 이 스크립트를 다시 고치는 것은
  이미 마감된 재구조화 범위 밖이다.

**참고 — architecture.md 좌표는 이 절 이후로 밀렸다.** `SpDefinition` 행 삽입으로 §2.2
표 아래의 모든 줄 번호가 1씩 밀렸다. 이 대장이 그 이전에 인용해 둔 `architecture.md:<n>`
좌표(위 "이 표를 읽기 전에" 절)는 다음 절에서 새 값으로 정정한다.
