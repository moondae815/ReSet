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
