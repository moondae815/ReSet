# 삭제 대장 — AGENTS.md 재구조화

각 줄의 삭제 근거. `중복`은 동등 이상의 서술이 이미 그곳에 있다는 뜻이다.
`근거없음`은 삭제하지 않고 이동한다.

## Phase 1 — §2 클래스 카탈로그 (AGENTS.md L16–L145)

```
LINE   BYTES   SYMBOL                             ARCH_MD   SUMMARY   VERDICT
20     59      -                                  -         -         산문(수동판정)
21     60      -                                  -         -         산문(수동판정)
22     181     SpDefinition                       2420      551       중복:architecture.md
23     200     -                                  -         -         산문(수동판정)
24     247     CodeObjectKey                      1606      0         중복:architecture.md
25     199     CodeObjectAnalysisModels           303       1360      중복:architecture.md
26     342     VerificationOutcome                3162      806       중복:architecture.md
27     404     SpAnalysisOutcome                  406       676       중복:architecture.md
28     172     DependencyInfo                     0         0         근거없음(이동필요)
29     220     ColumnInfo                         0         0         근거없음(이동필요)
30     187     TableIndexInfo                     0         0         근거없음(이동필요)
31     218     AiResult                           0         0         근거없음(이동필요)
32     146     DbSnapshot                         955       0         중복:architecture.md
33     70      -                                  -         -         산문(수동판정)
34     298     DbMetadataService                  1861      258       중복:architecture.md
35     281     SqlStaticParser                    1557      4532      중복:architecture.md
36     622     StaticAnalysisNormalizer           879       2038      중복:architecture.md
37     531     SqlObjectTypeClassifier            1677      411       중복:architecture.md
38     1802    AiService                          7178      3552      중복:architecture.md
39     683     IAiClient                          5113      724       중복:architecture.md
40     379     PromptCacheBreakpointPolicy        3313      1603      중복:architecture.md
41     420     PromptComposition                  341       410       근거없음(이동필요)
42     1548    BatchStepPlan                      4239      3169      중복:architecture.md
43     480     SpecReturnCodeExtractor            1659      1654      중복:architecture.md
44     579     SpecTargetTableExtractor           875       1920      중복:architecture.md
45     1484    PlanStructureEnricher              2180      3564      중복:architecture.md
46     709     StepDefect                         563       1549      중복:StepDefect.cs <summary>
47     451     BatchPlanAssembler                 860       1159      중복:architecture.md
48     1377    MechanicalValidator                6220      17117     중복:architecture.md
49     422     SchemaPromptColumnSelector         585       3559      중복:architecture.md
50     418     SpecExpectations                   521       2502      중복:architecture.md
51     4162    VerificationPipelineOrchestrator   3805      12852     중복:VerificationPipelineOrchestrator.cs <summary>
52     296     DependencyAnalysisOrchestrator     867       567       중복:architecture.md
53     559     VerificationDocumentFormatter      1205      1707      중복:architecture.md
54     1474    VerificationBanner                 6124      7217      중복:architecture.md
55     422     ThinkingLogPlaceholder             0         883       중복:ThinkingLogPlaceholder.cs <summary>
56     717     ThinkingLogDocument                632       854       중복:ThinkingLogDocument.cs <summary>
57     1104    BestAttempt                        121       1949      중복:BestAttempt.cs <summary>
58     1270    RetryRescue                        545       1434      중복:RetryRescue.cs <summary>
59     1330    StructureRedraftPolicy             0         1812      중복:StructureRedraftPolicy.cs <summary>
60     493     CriticFeedbackLog                  0         1947      중복:CriticFeedbackLog.cs <summary>
61     682     RegenerationScope                  0         1847      중복:RegenerationScope.cs <summary>
62     223     OutputPathResolver                 1177      87        중복:architecture.md
63     231     SpecificationLinker                1078      0         중복:architecture.md
64     616     MetadataExporter                   2334      702       중복:architecture.md
65     803     DataAccessPolicy                   2473      3627      중복:architecture.md
66     787     PlanBoundaryResolver               813       7885      중복:architecture.md
67     256     MarkdownSectionLocator             297       1791      중복:architecture.md
68     383     InstructionEntryPointComposer      1018      4356      중복:architecture.md
69     340     TaskFileComposer                   498       4911      중복:architecture.md
70     680     InstructionBundleWriter            2203      5270      중복:architecture.md
71     283     AgentProgressStore                 430       2337      중복:architecture.md
72     261     CodegenArtifactNaming              314       2954      중복:architecture.md
73     203     PlanLayout                         1067      1340      중복:architecture.md
74     648     VerificationCoverage               745       3697      중복:architecture.md
75     252     OfflineDbMetadataService           914       2403      중복:architecture.md
76     286     SnapshotManager                    414       0         중복:architecture.md
77     251     LocalAiConsolidator                404       0         중복:architecture.md
78     197     CacheManager                       235       0         중복:architecture.md
79     362     -                                  -         -         산문(수동판정)
80     254     ExternalCliCodingEngine            339       228       중복:architecture.md
81     250     ArgumentTemplateResolver           459       2445      중복:architecture.md
82     219     ArtifactChangeDetector             294       333       중복:architecture.md
83     165     CodegenRunResult                   267       825       중복:architecture.md
84     231     -                                  -         -         산문(수동판정)
85     987     ClaudeCliClient                    765       988       중복:ClaudeCliClient.cs <summary>
86     830     -                                  -         -         산문(수동판정)
87     208     CliProcessRunner                   1566      908       중복:architecture.md
88     647     CliWorkspace                       3864      413       중복:architecture.md
89     936     CliEffort                          5315      751       중복:architecture.md
90     268     CliProviderBatchGuard              5917      570       중복:architecture.md
91     157     IMultiProgressScope                0         0         근거없음(이동필요)
92     218     NullProgressScope                  0         0         근거없음(이동필요)
93     316     SettlementPolicyService            202       0         근거없음(이동필요)
95     57      -                                  -         -         산문(수동판정)
96     136     Program                            1730      252       중복:architecture.md
97     474     ConsoleUserInteraction             660       0         중복:architecture.md
98     214     ValidationUiProxy                  193       0         근거없음(이동필요)
99     1056    BatchStepCatalog                   1822      1803      중복:architecture.md
100    250     SpecHeaderReader                   255       99        중복:architecture.md
102    93      -                                  -         -         산문(수동판정)
103    141     -                                  -         -         산문(수동판정)
104    361     IValidatorPlugin                   0         154       근거없음(이동필요)
105    765     TransactionEnlistmentCheck         653       1224      중복:TransactionEnlistmentCheck.cs <summary>
106    159     IRuntimeRunner                     0         573       중복:IRuntimeRunner.cs <summary>
107    177     IValidationUserInterface           0         0         근거없음(이동필요)
108    144     L1ValidationResult                 0         0         근거없음(이동필요)
109    159     ValidationResult                   0         0         근거없음(이동필요)
110    178     MockDataDto                        329       0         중복:architecture.md
111    317     GapReport                          1664      0         중복:architecture.md
112    153     RunnerDtos                         0         0         근거없음(이동필요)
113    141     ValidatorConfig                    0         0         근거없음(이동필요)
114    87      -                                  -         -         산문(수동판정)
115    261     CodegenWorkflowOrchestrator        384       10112     중복:architecture.md
116    413     CodegenLoopPolicy                  478       1275      중복:architecture.md
117    222     CodegenWorkflowResult              233       516       중복:architecture.md
118    323     CodegenStage                       348       880       중복:architecture.md
119    258     CodeVerificationOrchestrator       1680      271       중복:architecture.md
120    249     FileMappingService                 254       771       중복:architecture.md
121    520     ValidatorAiService                 1749      0         중복:architecture.md
122    202     SpExecutionService                 337       0         중복:architecture.md
123    213     SandboxSeedingService              1057      0         중복:architecture.md
124    231     CSharpReflectionRunner             904       0         중복:architecture.md
125    206     JavaProcessRunner                  425       0         중복:architecture.md
126    237     DataComparisonService              516       0         중복:architecture.md
128    91      -                                  -         -         산문(수동판정)
129    80      Program                            1730      252       중복:architecture.md
130    142     ConsoleUserInteraction             660       0         중복:architecture.md
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
144    976     TypeClassificationPolicyTests      1677      0         중복:architecture.md
145    624     StepErrorCodeRegressionTests       0         0         근거없음(이동필요)
```
