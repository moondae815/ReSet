# 목차 명단 공급과 분할 실패 가시화 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 목차 수립 단계가 채우도록 요구받는 `LegacyProcedures`를 실제로 채울 수 있게 명단을 공급하고, 분할 생성이 무산됐을 때 그 사실이 산출물에 드러나게 한다.

**Architecture:** 두 갈래다. (1) `DraftBatchPlanStructureAsync`에 원본 명세서 파일명 목록을 넘겨 프롬프트에 싣고, 모델이 *암기* 대신 *선택*하게 한다. (2) 목차가 망가져도 살아남는 검사 두 개를 붙인다 — 분할 미실행 배너와, 목차를 전혀 쓰지 않는 문서 전체 오류코드 대조. 두 갈래는 서로 독립이라 한쪽이 실패해도 다른 쪽이 그 사실을 드러낸다.

**Tech Stack:** C# / .NET 10, xUnit, NSubstitute, Serilog

## Global Constraints

- 대상 명세서: `docs/superpowers/specs/2026-08-13-outline-roster-and-split-failure-visibility-design.md`
- 명단은 **원본 `specs`**에서 만든다. 작업 사본 `specsCopy`는 재시도 회차마다 `Feedback_Log.txt`가 덧붙으므로 절대 쓰지 않는다.
- 새 파라미터에 **기본값을 두지 않는다.** 호출부가 빠뜨렸을 때 컴파일이 깨져야 한다.
- 배너는 `VerificationOutcome`을 바꾸지 않는다. 가시성만 확보한다.
- 문서 전체 오류코드 대조는 **분할 여부와 무관하게 항상** 실행한다.
- 모든 주석과 사용자 표시 문자열은 한국어로 쓴다. 기존 파일의 어조를 따른다.
- 커밋 메시지 끝에 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`를 붙인다.

## File Structure

| 파일 | 책임 | 작업 |
|---|---|---|
| `src/ReSet.Core/Services/IAiService.cs` | 계약 | Task 1 |
| `src/ReSet.Core/Services/AiService.cs` | 목차 프롬프트 조립 | Task 1 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 명단 전달, 배너 배선 | Task 2, 3, 4 |
| `src/ReSet.Core/Services/VerificationBanner.cs` | 배너 렌더링 | Task 3, 4 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 문서 전체 코드 대조 | Task 4 |
| `tests/ReSet.Core.Tests/AiServiceTests.cs` | 프롬프트 검증 | Task 1 |
| `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` | 배선·회귀 검증 | Task 1, 2, 3, 4 |
| `tests/ReSet.Core.Tests/VerificationBannerTests.cs` | 문구 검증 | Task 3, 4 |
| `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` | 매칭 규칙 검증 | Task 4 |

Task 1 → 2는 순차다(2가 1의 시그니처에 의존). Task 3, 4는 1·2와 서로 독립이다.

---

### Task 1: `DraftBatchPlanStructureAsync`가 명단을 받아 프롬프트에 싣는다

**Files:**
- Modify: `src/ReSet.Core/Services/IAiService.cs:18`
- Modify: `src/ReSet.Core/Services/AiService.cs:2040` (시그니처), `:2067` 부근(규칙), `:2089` 부근(userPrompt)
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1822`, `:2557` (컴파일만 통과시키는 임시 배선)
- Test: `tests/ReSet.Core.Tests/AiServiceTests.cs`
- Modify(기계적): `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Produces: `Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, IReadOnlyList<string> sourceProcedures, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests.cs`의 `DraftBatchPlanStructureAsync_TellsTheModelWhatLegacyProceduresIsFor` 테스트를 **통째로 아래로 교체**한다. 그 테스트가 검증하던 규칙이 바로 이번에 회귀를 만든 문구이므로 남겨두면 안 된다.

```csharp
        // 2026-08-13에 넣은 규칙이 회귀를 만들었다. "명세서가 부르는 그대로 쓰라"고
        // 요구했는데 목차 단계는 명세서를 받지 않는다. codex-cli는 추정이 규칙 위반이라
        // 판단해 단계 목록을 통째로 비웠고(POQSettleProc7), 단계별 섹션 33개와 원본
        // 오류코드 20개가 사라진 문서가 92점으로 통과했다. 명단을 주면 같은 요구가
        // 암기가 아니라 선택이 되어 비로소 지킬 수 있는 규칙이 된다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_PutsTheProcedureRosterInTheUserPrompt()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job",
                new[] { "dbo.UP_UTIL_SETTLE_INS", "dbo.UP_Util_Settle_Summary" });

            Assert.Contains("[Source Procedures", result.UserPrompt);
            Assert.Contains("- dbo.UP_UTIL_SETTLE_INS", result.UserPrompt);
            Assert.Contains("- dbo.UP_Util_Settle_Summary", result.UserPrompt);
        }

        // 명단은 잡마다 달라지므로 시스템 프롬프트에 실으면 캐시 접두사가 매번 깨진다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_KeepsTheRosterOutOfTheSystemPrompt()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

            Assert.DoesNotContain("dbo.UP_UTIL_SETTLE_INS", result.SystemPrompt);
        }

        [Fact]
        public async Task DraftBatchPlanStructureAsync_TellsTheModelToSelectFromTheRoster()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

            Assert.Contains("copied verbatim from the supplied Source", result.SystemPrompt);
            // 회귀를 만든 옛 문구는 남아 있으면 안 된다.
            Assert.DoesNotContain("exactly as the source specifications name it", result.SystemPrompt);
        }

        // 거부가 더 비싸다는 사실을 알려주지 않으면 모델은 거부를 택한다. 실측에서
        // 빈 Steps 목록 하나가 단계별 섹션 전부와 단계별 검사 전부를 없앴다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_ForbidsAnEmptyStepList()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job", new[] { "dbo.UP_UTIL_SETTLE_INS" });

            Assert.Contains("Never emit an empty `Steps` list", result.SystemPrompt);
            Assert.Contains("an absent one discards every per-step section", result.SystemPrompt);
        }
```

- [ ] **Step 2: 컴파일 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DraftBatchPlanStructureAsync_PutsTheProcedureRoster"`
Expected: FAIL — `CS1501` 또는 `CS7036` (인자 개수 불일치)

- [ ] **Step 3: 인터페이스 시그니처를 바꾼다**

`src/ReSet.Core/Services/IAiService.cs:18`을 아래로 교체한다.

```csharp
        Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, IReadOnlyList<string> sourceProcedures, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: 구현 시그니처를 바꾼다**

`src/ReSet.Core/Services/AiService.cs:2040`을 아래로 교체한다.

```csharp
        public async Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, IReadOnlyList<string> sourceProcedures, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default)
```

- [ ] **Step 5: 규칙을 재작성한다**

`AiService.cs`에서 아래 한 줄(2026-08-13에 추가된 것으로, `- \`LegacyProcedures\` must name every source procedure`로 시작한다)을 찾아 **두 줄로 교체**한다.

교체 전(이 줄 전체를 지운다):

```
- `LegacyProcedures` must name every source procedure the step derives from, exactly as the source specifications name it. This is the field the rest of the pipeline keys off: the coverage check compares it against the supplied specifications, and the enrichment pass uses it to fill `ErrorCodes`, `TargetTables` and the schema list. An empty array silently disables all of them — the step is then reported as covering nothing, and its section is never mechanically checked. A step that genuinely derives from no source procedure (pure orchestration, locking, final publish) is the only case where the array may be empty.
```

교체 후:

```
- `LegacyProcedures` must be copied verbatim from the supplied Source Procedures list. It is how the pipeline links a step to its origin: the coverage check compares these names against that same list, and the enrichment pass uses them to fill `ErrorCodes` and `TargetTables`. Leave it empty only for a step with no legacy origin (input validation, locking, final publish).
- Never emit an empty `Steps` list and never omit the JSON block, however incomplete the supplied analysis feels. A step list with imperfect `LegacyProcedures` is recoverable; an absent one discards every per-step section and every per-step check.
```

- [ ] **Step 6: 명단 블록을 사용자 프롬프트에 넣는다**

`AiService.cs`의 `userPrompt` 조립부에서 `[Brainstorming Analysis Result]` 블록 **바로 뒤**에 아래를 삽입한다.

```csharp
            // 명단이 없으면 블록을 만들지 않는다. 빈 목록에 "아래 목록에서 고르라"고
            // 하면 모델이 고를 것이 없어 다시 거부를 택한다 - 이번 회귀의 원인이었다.
            if (sourceProcedures != null && sourceProcedures.Count > 0)
            {
                userPrompt.AppendLine("[Source Procedures — use these names verbatim in `LegacyProcedures`]");
                foreach (var procedure in sourceProcedures)
                {
                    userPrompt.AppendLine($"- {procedure}");
                }
                userPrompt.AppendLine();
            }
```

- [ ] **Step 7: 오케스트레이터 두 호출부를 임시 배선한다**

컴파일만 통과시킨다. 진짜 명단은 Task 2에서 넣는다.

`VerificationPipelineOrchestrator.cs:1822`의 호출에서 `jobName` 다음에 `System.Array.Empty<string>(),`를 넣는다.
`VerificationPipelineOrchestrator.cs:2557`의 호출에서 `jobName` 다음에 `System.Array.Empty<string>(),`를 넣는다.

- [ ] **Step 8: 테스트 스텁을 일괄 수정한다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`에서 Edit 도구의 `replace_all: true`로 아래를 치환한다. 스텁 형태가 두 가지(`Arg.Any<string>()` / `Arg.Any<string?>()`로 effort를 받는)지만, 앞 세 인자는 같으므로 한 번의 치환으로 둘 다 처리된다.

- old: `DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), `
- new: `DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), `

치환 후 남는 컴파일 오류는 `AiServiceTests.cs`의 실제 호출부다. 각 호출에서 `jobName` 다음에 `new[] { "dbo.UP_UTIL_SETTLE_INS" }`를 넣는다. **컴파일 오류가 전부 드러내므로 누락은 불가능하다.** 오류가 0이 될 때까지 반복한다.

- [ ] **Step 9: 테스트를 돌려 통과를 확인한다**

Run: `dotnet test`
Expected: PASS, 실패 0

- [ ] **Step 10: 커밋한다**

```bash
git add -A
git commit -m "$(cat <<'EOF'
fix: give the outline stage the roster its own rule demands

The rule added on 2026-08-13 asked the model to name procedures "exactly as
the source specifications name it" — but this stage is never given the
specifications. codex-cli read that correctly, judged any guess a rule
violation, and emitted no step list at all, which cost 33 per-step sections
and 20 of 76 original error codes on POQSettleProc7.

Supplying the roster turns the same requirement from recall into selection.
The second rule closes the escape hatch the first one opened: refusing is
more expensive than an imperfect answer, and the model had no way to know.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 오케스트레이터가 원본 명세서의 명단을 넘긴다

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1822`, `DraftReplacementPlanStructureAsync` (`:2536` 부근)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `DraftBatchPlanStructureAsync(..., IReadOnlyList<string> sourceProcedures, ...)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`VerificationPipelineOrchestratorTests.cs`에 추가한다.

```csharp
        // 목차 단계는 명세서를 받지 않으므로, 프로시저 이름을 알 수 있는 경로는
        // 이 명단 하나뿐이다. 여기가 끊기면 모델은 다시 추정하거나 거부한다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_PassesTheSourceProcedureRosterToTheOutlineStage()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "1", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)>
            {
                ("dbo.UP_UTIL_SETTLE_INS", "content1"),
                ("dbo.UP_Util_Settle_Summary", "content2")
            };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "RosterJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            await aiService.Received(1).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Is<IReadOnlyList<string>>(r =>
                    r.Count == 2
                    && r.Contains("dbo.UP_UTIL_SETTLE_INS")
                    && r.Contains("dbo.UP_Util_Settle_Summary")),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// 오케스트레이터는 재시도 회차마다 작업 사본(specsCopy)에 "Feedback_Log.txt"를
        /// 덧붙이고, 바로 옆의 BrainstormBatchPlanAsync가 그 사본을 받는다. 명단이 사본에서
        /// 오면 존재하지 않는 프로시저가 섞여 들어가고, 모델은 그것을 LegacyProcedures에
        /// 적으며, 커버리지 검사는 그 이름을 어느 명세서와도 대조하지 못한다. 같은 함정에
        /// 커버리지 검사가 이미 한 번 물린 적이 있다.
        ///
        /// 이 테스트가 실패해도 문서는 그럴듯하게 나오므로 사람 눈으로는 잡히지 않는다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipelineAsync_RedraftRoster_ExcludesTheRetryFeedbackWorkingCopy()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("dbo.UP_UTIL_SETTLE_INS", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "재설계 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            // 정체를 만들어 재설계를 유발한다.
            var stalled = new ReviewResult { HasDefects = true, FeedbackComment = "구조 결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(stalled));

            await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "StallRosterJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 최초 수립과 재설계 두 번 모두, 명단은 원본 명세서 하나뿐이어야 한다.
            await aiService.Received(2).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Is<IReadOnlyList<string>>(r =>
                    r.Count == 1 && r[0] == "dbo.UP_UTIL_SETTLE_INS"),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~PassesTheSourceProcedureRoster|FullyQualifiedName~RedraftRoster_Excludes"`
Expected: FAIL — 임시 배선이 빈 목록을 넘기므로 `Arg.Is` 매칭이 성립하지 않아 `Received(1)`/`Received(2)`가 0회로 보고된다

- [ ] **Step 3: 최초 수립 호출에 실제 명단을 넘긴다**

`VerificationPipelineOrchestrator.cs`의 `specReturnCodes` 계산부(`:1760` 부근) 근처에 아래를 추가한다.

```csharp
            // 목차 단계는 명세서를 받지 않으므로 이름을 알 방법이 이 명단뿐이다.
            // 반드시 원본 specs를 쓴다 - specsCopy는 재시도 회차마다 Feedback_Log.txt가
            // 덧붙어, 존재하지 않는 프로시저가 명단에 섞인다.
            var sourceProcedureRoster = specs.Select(s => s.FileName).ToList();
```

`:1822`의 임시 배선 `System.Array.Empty<string>()`를 `sourceProcedureRoster`로 바꾼다.

- [ ] **Step 4: 재설계 경로에 명단을 뚫는다**

`DraftReplacementPlanStructureAsync`의 시그니처에 파라미터를 추가한다(기존 파라미터 뒤, `CancellationToken` 앞).

```csharp
            IReadOnlyList<string> sourceProcedures,
```

메서드 안의 `:2557` 임시 배선 `System.Array.Empty<string>()`를 `sourceProcedures`로 바꾼다.

이 메서드의 호출부는 **두 곳**이다 — `:2021`과 `:2199`. 둘 다 `RunConsolidatedPipelineAsync`(`:1676`) 안이므로 Step 3에서 만든 `sourceProcedureRoster`가 그대로 스코프에 있다. 두 곳 모두에 넘긴다. 한 곳만 고치면 그 경로의 재설계만 조용히 빈 명단을 받는다.

- [ ] **Step 5: 테스트를 돌려 통과를 확인한다**

Run: `dotnet test`
Expected: PASS, 실패 0

- [ ] **Step 6: 커밋한다**

```bash
git add -A
git commit -m "$(cat <<'EOF'
fix: supply the outline roster from the original specs

The material was already in the pipeline — the plan body names all twelve
procedures correctly, because stage 3 receives the specifications. Only the
outline stage did not, so it was the one place that had to guess.

The roster is built from specs, never specsCopy: the working copy grows a
Feedback_Log.txt entry on every retry round, and the coverage check has
already been bitten once by exactly that.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 분할 미실행 배너

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationBanner.cs`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:2483` 부근
- Test: `tests/ReSet.Core.Tests/VerificationBannerTests.cs`, `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Produces: `VerificationBanner.SplitGenerationSkipped()` → `string`

- [ ] **Step 1: 실패하는 배너 테스트를 쓴다**

`VerificationBannerTests.cs`에 추가한다.

```csharp
    // 목차가 유효한 단계 목록을 못 내면 커버리지 검사와 하한 검사가 둘 다 건너뛰어지는데,
    // 종전에는 경고 로그 두 줄만 남고 문서에는 아무 흔적이 없었다. POQSettleProc7이
    // 배너 0개에 92점으로 나온 이유다 - 검증을 가장 적게 받은 문서가 가장 높은 점수를 받았다.
    [Fact]
    public void SplitGenerationSkipped_SaysWhichChecksDidNotRun()
    {
        var banner = VerificationBanner.SplitGenerationSkipped();

        Assert.StartsWith("\n> [!WARNING]", banner);
        Assert.Contains("[분할 미실행]", banner);
        Assert.Contains("단일 호출", banner);
        Assert.Contains("하한 검사", banner);
        // 내용이 부실하다고 단정해서는 안 된다 - 근거가 없다.
        Assert.Contains("부실하다는 뜻은 아니지만", banner);
    }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SplitGenerationSkipped"`
Expected: FAIL — `CS0117: 'VerificationBanner'에는 'SplitGenerationSkipped'에 대한 정의가 포함되어 있지 않습니다`

- [ ] **Step 3: 배너를 구현한다**

`VerificationBanner.cs`에 추가한다.

```csharp
    /// <summary>
    /// 목차가 유효한 단계 목록을 내지 못해 분할 생성이 실행되지 않았음을 알린다.
    ///
    /// 다른 배너들과 달리 이것은 "무엇이 잘못됐다"가 아니라 "무엇을 검사하지
    /// 않았다"를 나른다. 그래서 더 중요하다 - 실측(POQSettleProc7)에서 이 경로를
    /// 탄 문서가 배너 하나 없이 92점으로 끝났고, 분할된 문서(88점)보다 높았다.
    /// 짧고 깔끔한 문서가 읽기 좋았기 때문이다. 점수는 누락을 볼 수 없다.
    ///
    /// 사유(JSON 블록 없음, 0단계, 상한 초과, 파싱 실패)는 구분하지 않는다.
    /// 운영상 결과가 같고, 사유는 이미 경고 로그에 남는다.
    /// </summary>
    public static string SplitGenerationSkipped()
    {
        return "\n> [!WARNING]\n> **[분할 미실행] 목차가 유효한 단계 목록을 내지 못해"
            + " 문서가 단일 호출로 생성되었습니다.**"
            + " 단계별 섹션 생성과 단계별 하한 검사(대상 테이블·오류코드 대조)가"
            + " 실행되지 않았습니다. 내용이 부실하다는 뜻은 아니지만, 이 문서는"
            + " 단계 단위 기계 검증을 받지 않았습니다."
            + "\n\n";
    }
```

- [ ] **Step 4: 배너 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~VerificationBannerTests"`
Expected: PASS

- [ ] **Step 5: 실패하는 배선 테스트를 쓴다**

`VerificationPipelineOrchestratorTests.cs`에 추가한다. `SkeletonMarkdown`과 `HealthyStepSection`은 이 파일에 이미 있는 헬퍼다.

```csharp
        // POQSettleProc7 재현: 모델이 빈 Steps 목록을 내면 분할이 무산되는데,
        // 종전에는 문서에 그 사실이 전혀 남지 않았다.
        [Fact]
        public async Task RunConsolidatedPipeline_WhenOutlineYieldsNoSteps_PrependsSplitSkippedBanner()
        {
            var emptyStepsJson = "```json\n{\n  \"Steps\": []\n}\n```";
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + emptyStepsJson });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = SkeletonMarkdown }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(), "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Contains("[분할 미실행]", result.Plan);
        }

        // 부재를 확인하는 테스트가 존재를 확인하는 테스트만큼 중요하다 - 조건이
        // 뒤집혀 배너가 늘 붙으면 정상 산출물마다 거짓 경고가 실린다.
        [Fact]
        public async Task RunConsolidatedPipeline_WhenSplitRuns_OmitsSplitSkippedBanner()
        {
            var stepsJson = "```json\n{\n  \"Steps\": [\n    { \"Code\": \"S01\", \"Name\": \"첫 단계\", \"LegacyProcedures\": [\"USP_Spec1\"], \"TargetTables\": [\"dbo.T1\"], \"ErrorCodes\": [\"-1\"] }\n  ]\n}\n```";
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + stepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(), "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.DoesNotContain("[분할 미실행]", result.Plan);
        }
```

- [ ] **Step 6: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SplitSkippedBanner"`
Expected: FAIL — 첫 테스트가 `[분할 미실행]`을 찾지 못한다

- [ ] **Step 7: 오케스트레이터에 배선한다**

`VerificationPipelineOrchestrator.cs`의 커버리지 검사 블록(`:2483` 부근, `var uncoveredProcedures = adoptedSteps != null` 로 시작하는 곳) **바로 앞**에 삽입한다.

```csharp
            // 분할이 무산됐다는 사실 자체가 검증 결과의 일부다. 이 경로에서는 커버리지
            // 검사와 하한 검사가 둘 다 실행되지 않는데, 종전에는 문서에 아무 흔적이
            // 남지 않아 가장 적게 검증된 문서가 가장 깨끗해 보였다.
            if (adoptedSteps == null && !string.IsNullOrEmpty(consolidatedPlan))
            {
                Log.Warning(
                    "[파이프라인] 목차가 유효한 단계 목록을 내지 못해 분할 생성이 실행되지 않았습니다 - Job: {JobName}",
                    jobName);
                consolidatedPlan = VerificationBanner.SplitGenerationSkipped() + consolidatedPlan;
            }
```

- [ ] **Step 8: 테스트 통과를 확인한다**

Run: `dotnet test`
Expected: PASS, 실패 0

- [ ] **Step 9: 커밋한다**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat: say so in the document when the split never ran

When the outline yields no usable step list, both the coverage check and
the per-step floor check are skipped, and until now the document carried no
trace of it. POQSettleProc7 came out of that path with zero banners and a
92 — higher than the split document's 88, because it was shorter and
cleaner to read. A score cannot see what is absent.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: 문서 전체 오류코드 대조

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ContainsToken`을 `internal`로, `FindMissingErrorCodes` 추가)
- Modify: `src/ReSet.Core/Services/VerificationBanner.cs`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:2483` 부근
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`, `VerificationBannerTests.cs`, `VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `SpecReturnCodeExtractor.Extract(specs)` → `IReadOnlyDictionary<string, IReadOnlyList<string>>` (키는 스키마 접두사를 뗀 맨 이름, 값은 `"-1"` 형태의 문자열)
- Produces:
  - `MechanicalValidator.FindMissingErrorCodes(string documentMarkdown, IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)` → `IReadOnlyDictionary<string, IReadOnlyList<string>>`
  - `VerificationBanner.MissingErrorCodes(IReadOnlyDictionary<string, IReadOnlyList<string>> missingByProcedure, IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)` → `string`

- [ ] **Step 1: 실패하는 검증기 테스트를 쓴다**

`MechanicalValidatorTests.cs`에 추가한다.

```csharp
        // 이 검사의 미덕은 목차가 필요 없다는 것이다. 명세서에서 직접 뽑으므로
        // 목차가 어떻게 망가지든 살아남는 유일한 검사다. POQSettleProc7에서 원본
        // 오류코드 76개 중 20개가 사라졌는데 아무도 알리지 않았다.
        [Fact]
        public void FindMissingErrorCodes_ReportsOnlyCodesAbsentFromTheWholeDocument()
        {
            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["UP_A"] = new[] { "-1", "-2", "-3" },
                ["UP_B"] = new[] { "-9" }
            };
            var document = "S01은 `-1`을 반환하고 `-3`도 반환한다. `-9`는 UP_B의 코드다.";

            var missing = MechanicalValidator.FindMissingErrorCodes(document, codes);

            var only = Assert.Single(missing);
            Assert.Equal("UP_A", only.Key);
            Assert.Equal(new[] { "-2" }, only.Value);
        }

        [Fact]
        public void FindMissingErrorCodes_WhenEveryCodeIsPresent_ReturnsEmpty()
        {
            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["UP_A"] = new[] { "-1", "-2" }
            };

            var missing = MechanicalValidator.FindMissingErrorCodes("`-1` `-2`", codes);

            Assert.Empty(missing);
        }

        // -1이 -10 안에서 오탐되면 진짜 누락이 통과한다. 단계별 검사와 같은
        // ContainsToken을 써야 두 경로의 판정이 갈리지 않는다.
        [Fact]
        public void FindMissingErrorCodes_DoesNotMatchACodeInsideALongerNumber()
        {
            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["UP_A"] = new[] { "-1" }
            };

            var missing = MechanicalValidator.FindMissingErrorCodes("반환값은 `-10`이다.", codes);

            var only = Assert.Single(missing);
            Assert.Equal(new[] { "-1" }, only.Value);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~FindMissingErrorCodes"`
Expected: FAIL — `CS0117: 'MechanicalValidator'에는 'FindMissingErrorCodes'에 대한 정의가 포함되어 있지 않습니다`

- [ ] **Step 3: 검증기를 구현한다**

`MechanicalValidator.cs`에서 `private static bool ContainsToken`을 `internal static bool ContainsToken`으로 바꾸고, 아래를 추가한다.

```csharp
        /// <summary>
        /// 명세서에서 뽑은 원본 오류코드 중 문서 어디에도 없는 것을 프로시저별로 돌려준다.
        ///
        /// 단계별 하한 검사와 묻는 것이 다르다 - 저건 "이 코드가 제 섹션에 있는가"이고
        /// 이건 "이 코드가 문서 어디에도 없는가"다. 후자에 걸리면 조건 없이 진짜 누락이다.
        ///
        /// 목차를 전혀 쓰지 않는다는 것이 이 검사의 존재 이유다. 목차가 비거나 망가지면
        /// 단계별 검사는 통째로 무실행이 되는데(실측: 33단계 중 32단계, 그리고 다른
        /// 회차에서는 33단계 전부), 그때가 바로 누락이 가장 의심스러운 순간이다.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> FindMissingErrorCodes(
            string documentMarkdown,
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
        {
            var missing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(documentMarkdown) || codesByProcedure == null)
            {
                return missing;
            }

            foreach (var (procedure, codes) in codesByProcedure)
            {
                var absent = new List<string>();
                foreach (var code in codes)
                {
                    if (!string.IsNullOrWhiteSpace(code) && !ContainsToken(documentMarkdown, code.Trim()))
                    {
                        absent.Add(code);
                    }
                }

                if (absent.Count > 0)
                {
                    missing[procedure] = absent;
                }
            }

            return missing;
        }
```

- [ ] **Step 4: 검증기 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~FindMissingErrorCodes"`
Expected: PASS

- [ ] **Step 5: 실패하는 배너 테스트를 쓴다**

`VerificationBannerTests.cs`에 추가한다.

```csharp
    // 분자만 보이면 심각도를 가늠할 수 없다. "9개 누락"과 "16개 중 9개 누락"은
    // 읽는 사람에게 전혀 다른 사실이다.
    [Fact]
    public void MissingErrorCodes_ShowsTheDenominatorPerProcedure()
    {
        var codes = new Dictionary<string, IReadOnlyList<string>>
        {
            ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new[] { "-1", "-2", "-3", "-101" }
        };
        var missing = new Dictionary<string, IReadOnlyList<string>>
        {
            ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new[] { "-101" }
        };

        var banner = VerificationBanner.MissingErrorCodes(missing, codes);

        Assert.StartsWith("\n> [!WARNING]", banner);
        Assert.Contains("[오류코드 누락]", banner);
        Assert.Contains("UP_UTIL_SETTLE_EXCEPTION_PROC", banner);
        Assert.Contains("4개 중 1개", banner);
        Assert.Contains("-101", banner);
    }
```

- [ ] **Step 6: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~MissingErrorCodes_ShowsTheDenominator"`
Expected: FAIL — `CS0117: 'VerificationBanner'에는 'MissingErrorCodes'에 대한 정의가 포함되어 있지 않습니다`

- [ ] **Step 7: 배너를 구현한다**

`VerificationBanner.cs`에 추가한다.

```csharp
    /// <summary>
    /// 원본 명세서의 오류코드 중 최종 문서 어디에도 없는 것을 알린다.
    ///
    /// 레거시 반환 코드를 그대로 계승하는 것은 이 문서의 핵심 계약이다. 실측
    /// (POQSettleProc7)에서 그 계약이 20군데 깨졌는데 아무 신호도 나가지 않았다 -
    /// 오류코드 대조가 단계별 경로에만 붙어 있었고 그 경로가 통째로 건너뛰어졌기 때문이다.
    ///
    /// 분모를 함께 싣는다. "9개 누락"만으로는 읽는 사람이 심각도를 가늠할 수 없다.
    /// </summary>
    public static string MissingErrorCodes(
        IReadOnlyDictionary<string, IReadOnlyList<string>> missingByProcedure,
        IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
    {
        var lines = new List<string>();
        foreach (var (procedure, missing) in missingByProcedure)
        {
            var total = codesByProcedure != null
                        && codesByProcedure.TryGetValue(procedure, out var all)
                ? all.Count
                : missing.Count;

            lines.Add($"{procedure}: {total}개 중 {missing.Count}개 누락 — {string.Join(", ", missing)}");
        }

        var body = RenderBulletList(lines, "(누락 내역이 기록되지 않았습니다.)");

        return "\n> [!WARNING]\n> **[오류코드 누락] 원본 명세서의 반환 코드가 최종 문서에서"
            + " 확인되지 않았습니다.**"
            + " 레거시 반환 코드의 보존은 이 문서의 핵심 계약이므로, 아래 항목은 문서를"
            + " 넘기기 전에 직접 확인하십시오.\n"
            + body
            + "\n\n";
    }
```

- [ ] **Step 8: 배너 테스트 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~VerificationBannerTests"`
Expected: PASS

- [ ] **Step 9: 실패하는 배선 테스트를 쓴다**

`VerificationPipelineOrchestratorTests.cs`에 추가한다. 분할이 **정상 실행된** 회차에서도 이 검사가 도는지를 본다 — 폴백 경로에만 걸면 POQSettleProc6(분할은 됐으나 32단계가 코드 대조를 건너뛴 경우)을 놓친다.

```csharp
        // 분할 성공 여부와 무관하게 항상 돌아야 한다. 두 사고(분할 무산, 분할은 됐으나
        // 목차 메타데이터가 비어 단계별 대조가 무실행)를 모두 잡는 유일한 배치가 "항상"이다.
        [Fact]
        public async Task RunConsolidatedPipeline_WhenTheDocumentDropsAnOriginalErrorCode_PrependsMissingCodeBanner()
        {
            var stepsJson = "```json\n{\n  \"Steps\": [\n    { \"Code\": \"S01\", \"Name\": \"첫 단계\", \"LegacyProcedures\": [\"USP_Spec1\"], \"TargetTables\": [\"dbo.T1\"], \"ErrorCodes\": [\"-1\"] }\n  ]\n}\n```";
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + stepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(), "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);

            // 명세서는 -1과 -7을 반환한다고 적혀 있는데, 단계 섹션은 -1만 싣는다.
            var specs = new List<(string, string)>
            {
                ("dbo.USP_Spec1", "@po_intRetVal = -1 이고 @po_intRetVal = -7 이다.")
            };

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Contains("[오류코드 누락]", result.Plan);
            Assert.Contains("-7", result.Plan);
        }
```

이 테스트에서는 단계 하한 검사도 함께 걸린다. 보강기가 `-7`을 S01의 `ErrorCodes`에 합쳐 넣는데 `HealthyStepSection`은 `ErrorCodes[0]`(=`-1`)만 싣기 때문이다. 두 배너가 같이 나오는 것이 정상이며, 두 검사가 같은 사실에 동의한다는 뜻이다. 단정문은 새 배너만 본다.

- [ ] **Step 10: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~PrependsMissingCodeBanner"`
Expected: FAIL — `[오류코드 누락]`을 찾지 못한다

- [ ] **Step 11: 오케스트레이터에 배선한다**

Task 3에서 넣은 분할 미실행 블록 **바로 뒤**, 즉 `AttachPipelineBanners`(`:2413`) 안에 삽입한다. 이 메서드는 이미 `specs`를 받고 있고(두 호출부 모두 원본을 넘긴다), 코드는 그것에서 바로 뽑는다. 새 파라미터를 뚫지 않는다.

```csharp
            var specReturnCodes = SpecReturnCodeExtractor.Extract(specs);

            // 분할 여부와 무관하게 항상 돈다. 폴백 경로에만 걸면 "분할은 됐으나 목차
            // 메타데이터가 비어 단계별 대조가 무실행"인 회차를 놓친다 - 실측에서 그쪽이
            // 먼저 일어났다. 목차를 전혀 쓰지 않는다는 것이 이 검사의 존재 이유다.
            if (!string.IsNullOrEmpty(consolidatedPlan))
            {
                var missingCodes = MechanicalValidator.FindMissingErrorCodes(consolidatedPlan, specReturnCodes);
                if (missingCodes.Count > 0)
                {
                    Log.Warning(
                        "[파이프라인] 원본 오류코드가 최종 문서에서 확인되지 않았습니다 - Job: {JobName}, 프로시저: {Count}개",
                        jobName, missingCodes.Count);

                    consolidatedPlan =
                        VerificationBanner.MissingErrorCodes(missingCodes, specReturnCodes) + consolidatedPlan;
                }
            }
```

- [ ] **Step 12: 전체 테스트 통과를 확인한다**

Run: `dotnet test`
Expected: PASS, 실패 0

기존 테스트가 새 배너 때문에 깨지면, 그 테스트의 명세서 본문에 `@po_intRetVal` 대입이 있는지 확인한다. 있으면 단계 섹션이 그 코드를 싣도록 픽스처를 고친다 — 배너를 끄지 말 것. 배너가 옳고 픽스처가 불완전한 것이다.

- [ ] **Step 13: 커밋한다**

```bash
git add -A
git commit -m "$(cat <<'EOF'
feat: compare original error codes against the whole document

Preserving legacy return codes is a core contract of this document, and
POQSettleProc7 broke it in 20 places without a single signal — the error
code comparison lived only on the per-step path, and that path was skipped
whole.

This check reads the specifications directly and never touches the outline,
which is precisely the point: the moment the outline is empty or broken is
the moment omissions are most likely, and it is also the moment every other
check goes quiet. It runs on every job, split or not.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## 완료 후

- `AGENTS.md`의 단위 테스트 개수를 실제 값으로 갱신한다(현재 1396, 이 계획으로 약 13개 증가).
- `docs/architecture.md` §4.4.5에 새 배너 두 개와 "목차를 쓰지 않는 검사"의 존재를 한 문단으로 더한다.
- POQSettleProc8로 재실행해 `raw/PlanStructure.md`의 `LegacyProcedures`가 채워졌는지, 그 결과 `ErrorCodes`·`TargetTables`가 보강됐는지, 커버리지 배너가 (오탐이 아니라 진짜로) 사라졌는지 확인한다.
