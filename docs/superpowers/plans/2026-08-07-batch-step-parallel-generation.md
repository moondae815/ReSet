# 통합 배치 단계 본문 병렬 생성 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 통합 배치 계획서의 단계 본문 생성을 순차에서 제한된 병렬로 바꿔, 실측 48분 실행의 81%를 차지하던 구간의 벽시계를 줄인다. 프롬프트 접두사 캐시의 이점과 산출물의 결정성은 그대로 유지한다.

**Architecture:** `VerificationPipelineOrchestrator.GenerateBySplitAsync`의 `for` 루프를 "첫 단계 단독 실행(캐시 워밍) → 나머지를 `SemaphoreSlim`으로 제한한 `Task.WhenAll` → 단일 스레드 병합"으로 바꾼다. 병렬 태스크가 공유 컬렉션을 만지지 않도록 `GenerateStepSectionWithFloorRetryAsync`가 하한 위반을 바깥 사전에 쓰는 대신 반환하게 고친다. 동시 실행 수는 `AiSettings:StepConcurrency` 설정 키로 노출한다.

**Tech Stack:** .NET 10 / C#, xUnit + NSubstitute, Spectre.Console(진행률 UI), `System.Threading.SemaphoreSlim`

**설계 문서:** [docs/superpowers/specs/2026-08-07-batch-step-parallel-generation-design.md](../specs/2026-08-07-batch-step-parallel-generation-design.md)

## Global Constraints

- **`src/ReSet.Cli/appsettings.json`에는 사용자의 커밋되지 않은 로컬 수정이 있다.** 이 파일에 대해 `git checkout --`, `git restore`, `git stash`, `git clean`, `git reset`을 **절대** 실행하지 않는다. 또한 `git add src/ReSet.Cli/appsettings.json`이나 `git commit -a`로 통째로 스테이징하지 않는다 — Task 1이 명시하는 blob 스테이징 절차만 쓴다.
- 취소 토큰을 기다리는 `await`를 감싸는 모든 `catch`는 `when (ex is not OperationCanceledException)` 필터를 가져야 한다. `CancellationPolicyTests`가 Roslyn 스캔으로 자동 검사한다.
- Spectre 프롬프트/마크업에 들어가는 AI 생성 문자열은 `Markup.Escape`로 감싼다 (AGENTS.md 규칙).
- 산출물 결정성: 같은 입력이면 같은 문서와 같은 배너가 나와야 한다. 병렬화가 이 성질을 바꾸면 안 된다.
- 완료 시 `dotnet clean && dotnet build`의 경고가 정확히 **8건**(기존 `DbMetadataServiceTests`의 CS8600/CS8602)이어야 한다. 새 경고를 남기지 않는다.
- 착수 시점 실측: `dotnet test` **763건 통과**, 빌드 경고 8건.
- `VerificationPipelineOrchestratorTests`에는 오케스트레이터를 생성하는 곳이 **99군데** 있다. 생성자 새 매개변수의 기본값이 1(=종전 순차)이어야 그 99곳이 한 줄도 바뀌지 않는다. **기존 호출부를 고치지 않는 것이 이 작업의 회귀 방어 본체다.**

---

### Task 1: `StepConcurrency` 설정 배선과 로컬 공급자 경고

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (필드 선언부 ~29행, 생성자 ~31-57행, `RunConsolidatedPipelineAsync` 진입부 ~1656-1662행)
- Modify: `src/ReSet.Cli/Program.cs:461-493`
- Modify: `src/ReSet.Cli/appsettings.json` (`AiSettings:MaxL2Attempts` 바로 아래)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: 없음 (첫 태스크)
- Produces: `VerificationPipelineOrchestrator` 생성자의 14번째 매개변수 `int stepConcurrency = 1`, private 필드 `_stepConcurrency`. Task 3이 이 필드를 읽는다. 테스트 도우미 `RunBatchPipelineWithConcurrency(IAiService, IVerificationUserInteraction, int)`도 Task 3이 쓴다.

배경: 이 값은 두 곳에서 기본값이 다르며 **의도된 것**이다. `appsettings.json`은 4(실사용 값 — 설정을 건드리지 않은 사용자가 병렬의 이득을 본다), 생성자 매개변수는 1(테스트 기본값 — 인자를 넘기지 않는 99개 호출부가 종전 순차를 유지한다). `Program.cs`는 항상 설정값을 명시적으로 넘기므로 실제 실행에서 생성자 기본값이 쓰이는 일은 없다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

새 테스트는 `RunConsolidatedPipeline_WithStepList_GeneratesOneSectionPerStep` 바로 뒤, 분할 생성 테스트 묶음 안에 넣는다.

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의 `RunBatchPipelineWithUi` 도우미 바로 아래에 도우미를 추가한다.

```csharp
        /// <summary>
        /// RunBatchPipeline과 같되 stepConcurrency를 명시적으로 넘긴다.
        /// 기본값 1에 기대는 99개 기존 호출부를 건드리지 않기 위해 별도 도우미로 둔다.
        /// </summary>
        private async Task<ConsolidatedPipelineResult> RunBatchPipelineWithConcurrency(
            IAiService aiService, IVerificationUserInteraction userInteraction, int stepConcurrency)
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var validator = new MechanicalValidator();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8, stepConcurrency);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };
            return await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);
        }
```

같은 파일의 분할 생성 테스트 묶음 끝(`RunConsolidatedPipeline_WithStepList_GeneratesOneSectionPerStep` 근처)에 테스트 3개를 추가한다.

```csharp
        [Fact]
        public async Task RunConsolidatedPipeline_WhenLocalProviderAndConcurrencyAboveOne_WarnsOnce()
        {
            var aiService = SplitCapableAiService();
            aiService.ProviderName.Returns("Ollama");
            var ui = Substitute.For<IVerificationUserInteraction>();

            await RunBatchPipelineWithConcurrency(aiService, ui, 4);

            ui.Received(1).NotifyStatus(Arg.Is<string>(m => m.Contains("StepConcurrency")));
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WhenLocalProviderAndConcurrencyIsOne_DoesNotWarn()
        {
            var aiService = SplitCapableAiService();
            aiService.ProviderName.Returns("Ollama");
            var ui = Substitute.For<IVerificationUserInteraction>();

            await RunBatchPipelineWithConcurrency(aiService, ui, 1);

            ui.DidNotReceive().NotifyStatus(Arg.Is<string>(m => m.Contains("StepConcurrency")));
        }

```

**절상(`Math.Max(1, …)`)에 대한 테스트는 이 태스크에 두지 않는다.** 이 시점에는 관측 지점이 없다 — 경고는 `_stepConcurrency > 1`일 때만 뜨는데 원값 `0`·`-5`는 절상 전에도 이미 `> 1`이 아니므로, 절상을 통째로 지워도 어떤 단언이든 그대로 통과한다. 절상이 실제로 하는 일은 Task 3에서 관측 가능해진다: 절상이 없으면 `new SemaphoreSlim(0)`이 슬롯을 하나도 내주지 않아 단계 생성이 영구 대기한다. 그 테스트는 Task 3 Step 2의 다섯 번째 테스트로 배정돼 있다.

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunConsolidatedPipeline_When" 2>&1 | tail -20
```
Expected: 컴파일 실패 — 생성자에 14번째 매개변수가 없다.

- [ ] **Step 3: 생성자에 매개변수와 필드를 추가한다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`의 `private readonly int _criticScoreThreshold;` 바로 아래에 필드를 추가한다.

```csharp
        private readonly int _stepConcurrency;
```

생성자 시그니처의 `int criticScoreThreshold = 8)`를 다음으로 바꾼다.

```csharp
            int criticScoreThreshold = 8,
            int stepConcurrency = 1)     // 기본값 1 = 종전 순차. 실사용 값은 appsettings.json이 4로 넘긴다.
```

`_criticScoreThreshold = criticScoreThreshold;` 바로 아래에 대입을 추가한다.

```csharp
            // 0·음수는 1로 절상한다. 상한은 두지 않는다 — 사용자가 12를 원하면 12를 쓴다.
            _stepConcurrency = Math.Max(1, stepConcurrency);
```

- [ ] **Step 4: 로컬 공급자 경고를 추가한다**

`RunConsolidatedPipelineAsync`의 `outputRoot` 가드 블록(`throw new ArgumentException("출력 디렉터리가 필요합니다.", nameof(outputRoot));` 를 닫는 `}`) 바로 다음 줄에 삽입한다.

```csharp
            // 이 경고는 분할 생성 진입 여부와 무관하게 실행당 한 번 뜬다. 목차 JSON
            // 파싱에 실패해 단일 호출로 폴백하는 회차에서도 뜨지만, 설정이 로컬
            // 공급자와 함께 쓰이고 있다는 사실 자체는 여전히 참이고 조치도 같다.
            //
            // 로컬 모델은 보통 단일 GPU를 공유하므로 동시 실행이 순차보다 느리거나
            // 메모리가 터진다. 값을 조용히 1로 뒤집지 않는 이유: 사용자가 명시한
            // 설정을 말없이 무시하는 것보다 이유를 말하고 그대로 두는 편이 정직하고,
            // 증상이 "그냥 느림"이라 경고가 없으면 원인을 찾을 길이 없다.
            //
            // provider 매개변수(Actor)가 아니라 Consolidator를 보는 이유: 단계 본문을
            // 실제로 만드는 것은 _consolidatorService다.
            if (_stepConcurrency > 1 &&
                ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(_consolidatorService.ProviderName))
            {
                _userInteraction.NotifyStatus(
                    $"[yellow]{jobName}[/] - StepConcurrency={_stepConcurrency}이지만 Consolidator가 로컬 공급자({_consolidatorService.ProviderName})입니다. " +
                    "단일 GPU에서는 동시 실행이 순차보다 느리거나 메모리가 부족할 수 있습니다 — appsettings.json의 AiSettings:StepConcurrency를 1로 낮추는 것을 권장합니다.");
            }
```

`AiClientFactory.IsLocalProvider`는 `provider?.ToLowerInvariant()`로 시작해 null을 안전하게 false로 처리한다. `Substitute.For<IAiService>()`의 `ProviderName` 기본값이 null이므로 기존 테스트들은 이 분기에 걸리지 않는다.

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunConsolidatedPipeline_When" 2>&1 | tail -20
```
Expected: 추가한 3개 포함 전부 PASS

- [ ] **Step 6: `Program.cs`에서 설정값을 읽어 넘긴다**

`src/ReSet.Cli/Program.cs:461`의 `var maxL2Attempts = ...` 줄 바로 아래에 추가한다.

```csharp
            // 설정 키가 없거나 숫자가 아니면 실사용 기본값 4. 생성자 기본값(1)과 다른 것은
            // 의도된 것이다 — 자세한 근거는 설계 문서 §4를 보라.
            if (!int.TryParse(configuration["AiSettings:StepConcurrency"], out int stepConcurrency))
            {
                stepConcurrency = 4;
            }
```

이어서 `orchestrator`(463행)와 `recursiveOrchestrator`(478행) **두 곳 모두**의 마지막 인자 `criticThresholdScore` 뒤에 `stepConcurrency`를 더한다. 둘 다 고쳤는지 확인한다.

```bash
grep -n "criticThresholdScore" src/ReSet.Cli/Program.cs
```
Expected: `stepConcurrency`가 뒤따르는 줄이 2개

- [ ] **Step 7: `appsettings.json`에 키를 추가한다**

`"MaxL2Attempts": 2,` 줄 **바로 아래**에 다음을 삽입한다. (`"TimeoutSeconds"` 줄 위)

```jsonc
    "StepConcurrency": 4,              // [통합 배치] 단계 본문 동시 생성 수. 1이면 순차(종전 동작). 첫 단계는 항상 단독 실행해 프롬프트 접두사 캐시를 채운 뒤 나머지를 이 수만큼 동시 실행합니다. 값을 올려도 전체 벽시계는 골격 호출(약 2분) 아래로 내려가지 않습니다. 로컬 모델(Ollama·mlx·local-openai)에서는 1을 권장합니다 — 단일 GPU에서 동시 실행은 순차보다 느리거나 메모리가 터집니다.
```

- [ ] **Step 8: 빌드하고 전체 테스트를 돌린다**

```bash
dotnet clean && dotnet build 2>&1 | tail -5
dotnet test 2>&1 | tail -3
```
Expected: 경고 8개 · 오류 0개, 765건 통과 (763 + 신규 2)

- [ ] **Step 9: 커밋 — `appsettings.json`은 blob 스테이징으로만**

`appsettings.json`에는 사용자의 커밋되지 않은 로컬 수정(Provider, ModelName, OfflineSnapshotPath, CLI 인자 등)이 들어 있다. 통째로 `git add`하면 그 설정이 저장소에 섞여 들어간다. HEAD 사본에만 새 줄을 넣어 그 blob을 색인에 직접 등록한다.

```bash
SCRATCH=$(mktemp -d)
git show HEAD:src/ReSet.Cli/appsettings.json > "$SCRATCH/head.json"
python3 - "$SCRATCH/head.json" <<'PY'
import sys, io
path = sys.argv[1]
line = '    "StepConcurrency": 4,              // [통합 배치] 단계 본문 동시 생성 수. 1이면 순차(종전 동작). 첫 단계는 항상 단독 실행해 프롬프트 접두사 캐시를 채운 뒤 나머지를 이 수만큼 동시 실행합니다. 값을 올려도 전체 벽시계는 골격 호출(약 2분) 아래로 내려가지 않습니다. 로컬 모델(Ollama·mlx·local-openai)에서는 1을 권장합니다 — 단일 GPU에서 동시 실행은 순차보다 느리거나 메모리가 터집니다.\n'
with io.open(path, encoding='utf-8') as f:
    lines = f.readlines()
idx = next(i for i, l in enumerate(lines) if '"MaxL2Attempts"' in l)
assert '"StepConcurrency"' not in ''.join(lines), '이미 키가 있다'
lines.insert(idx + 1, line)
with io.open(path, 'w', encoding='utf-8') as f:
    f.writelines(lines)
PY
BLOB=$(git hash-object -w "$SCRATCH/head.json")
git update-index --cacheinfo 100644,"$BLOB",src/ReSet.Cli/appsettings.json

git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs \
        src/ReSet.Cli/Program.cs \
        tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: expose StepConcurrency and warn when local providers run it above one

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

검증 — 커밋에 사용자의 로컬 설정이 섞이지 않았는지 확인한다.

```bash
git show --stat HEAD
git show HEAD -- src/ReSet.Cli/appsettings.json
```
Expected: `appsettings.json`의 diff가 `+ "StepConcurrency": 4, ...` **한 줄 추가뿐**. Provider·ModelName·OfflineSnapshotPath·Arguments 줄이 보이면 커밋을 되돌리고(`git reset --soft HEAD~1` — 작업 트리는 건드리지 않는다) 다시 한다.

```bash
git status --short src/ReSet.Cli/appsettings.json
```
Expected: 여전히 ` M` (사용자의 로컬 수정이 살아 있다)

---

### Task 2: 하한 위반 기록을 반환값으로 바꾼다 (순수 리팩터링)

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:2667-2765` (`GenerateBySplitAsync`의 `for` 루프와 `GenerateStepSectionWithFloorRetryAsync`)

**Interfaces:**
- Consumes: Task 1의 `_stepConcurrency` (이 태스크에서는 쓰지 않는다)
- Produces: `private async Task<(string Markdown, string? FloorViolation)> GenerateStepSectionWithFloorRetryAsync(BatchStepPlan step, IReadOnlyList<BatchStepPlan> steps, string conventions, List<(string FileName, string Content)> specs, string targetLanguage, string jobName, CancellationToken cancellationToken)` — `floorViolations` 매개변수가 사라지고 위반 문자열을 반환값의 두 번째 요소로 돌려준다. Task 3이 이 계약 위에서 병렬화한다.

배경: 현행 루프는 두 사전에 동시 쓰기를 하게 된다 — 호출부가 `sections`에, 헬퍼 내부가 `floorViolations`에 쓴다. 잠금으로 막을 수도 있지만 공유 자체를 없애는 편이 낫다. **잠금은 "동시에 써도 깨지지 않는다"만 보장하고 순서는 보장하지 않는다.** 이 태스크는 동작을 바꾸지 않는 준비 작업이다.

- [ ] **Step 1: 헬퍼 시그니처와 반환 지점을 바꾼다**

`GenerateStepSectionWithFloorRetryAsync`의 선언에서 `Dictionary<string, string> floorViolations,` 줄을 지우고 반환 타입을 튜플로 바꾼다.

```csharp
        private async Task<(string Markdown, string? FloorViolation)> GenerateStepSectionWithFloorRetryAsync(
            BatchStepPlan step,
            IReadOnlyList<BatchStepPlan> steps,
            string conventions,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            CancellationToken cancellationToken)
```

XML 주석 끝(`/// 같은 결함으로 골격+단계 전체 재생성을 유발해 비용만 태운다.` 다음 줄)에 문단을 하나 더한다.

```csharp
        /// 위반 기록을 바깥 사전에 쓰지 않고 돌려주는 이유: 이 메서드는 여러 단계에
        /// 대해 동시에 돈다. 공유 사전에 쓰면 잠금이 필요하고, 잠금이 있어도 기록이
        /// 들어가는 순서는 완료 순서를 따라 비결정적이 된다. 호출부가 Task.WhenAll
        /// 이후 단일 스레드에서 목록 순서대로 병합한다.
```

메서드 본문의 세 반환 지점을 바꾼다.

```csharp
                var stepResult = _validator.ValidateBatchStep(content, step);
                if (stepResult.IsValid)
                {
                    return (content, null);
                }
```

```csharp
            if (adopted == null)
            {
                return ($"### {step.Code} {step.Name}\n\n> [!WARNING]\n> 이 단계는 생성에 실패했습니다. 원본 프로시저를 직접 확인하십시오.\n",
                    $"{step.Code} (생성 실패)");
            }

            return (adopted, $"{step.Code} (하한 미달)");
```

- [ ] **Step 2: 호출부를 바꾼다**

`GenerateBySplitAsync`의 `for` 루프 본문을 다음으로 교체한다. (이 태스크에서는 아직 순차다.)

```csharp
            for (int index = 0; index < pending.Count; index++)
            {
                var step = pending[index];
                var taskKey = $"step_{step.Code}";
                progressScope.AddTask(taskKey, $"3/3. 단계 본문 생성 중 ({step.Code} · {index + 1}/{pending.Count})...");

                var (markdown, violation) = await GenerateStepSectionWithFloorRetryAsync(
                    step, steps, conventions, specs, targetLanguage, jobName, cancellationToken);

                sections[step.Code] = markdown;
                if (violation != null)
                {
                    floorViolations[step.Code] = violation;
                }

                progressScope.CompleteTask(taskKey);
            }
```

- [ ] **Step 3: 동작이 바뀌지 않았음을 기존 테스트로 확인한다**

이 태스크는 새 테스트를 만들지 않는다. 하한 위반 기록·배너·지목 재생성의 기존 커버리지가 그대로 게이트다. 아래 필터가 전부 통과해야 한다.

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunConsolidatedPipeline" 2>&1 | tail -5
```
Expected: 실패 0건. 특히 하한 미달 배너를 검사하는 테스트(`StepFloorViolations` / `하한 미달` 문자열을 단언하는 것들)와 지목 재생성이 손대지 않은 단계의 기록을 보존하는지 보는 테스트가 통과해야 한다.

```bash
dotnet clean && dotnet build 2>&1 | tail -5
dotnet test 2>&1 | tail -3
```
Expected: 경고 8개 · 오류 0개, 766건 통과 (Task 1과 동일 — 이 태스크는 테스트를 늘리지 않는다)

- [ ] **Step 4: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs
git commit -m "refactor: return step floor violations instead of writing them to a shared dictionary

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: 워밍 → 팬아웃 병렬화

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (`GenerateBySplitAsync`의 `for` 루프 자리, ~2672-2682행)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `_stepConcurrency`와 `RunBatchPipelineWithConcurrency`, Task 2의 튜플 반환 헬퍼
- Produces: 없음 (내부 구현). 새 private 레코드 `StepSectionResult(string Code, string Markdown, string? FloorViolation)`.

Task 1이 배선한 `Math.Max(1, stepConcurrency)` 절상의 커버리지가 이 태스크로 이월돼 있다(Task 1 Step 1의 각주 참조). 절상이 실제로 무엇을 막는지가 이 태스크에서 처음 관측 가능해지기 때문이다 — 테스트 ⑥이 그것이다.

- [ ] **Step 1: 테스트 픽스처를 추가한다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의 `HealthyStepSection` 정의 바로 아래에 추가한다. 파일 상단에 `using System.Diagnostics;`가 없으면 더한다.

```csharp
        /// <summary>하한을 통과하지 못하는 섹션 — 코드 블록이 없다.</summary>
        private static string SubFloorStepSection(string code) =>
            $"### {code} 단계\n\n산문만 있고 코드 블록이 없다.\n";

        /// <summary>S01..S{count} 짜리 단계 목록 JSON. 각 단계의 대상 테이블·오류코드는 코드에서 파생된다.</summary>
        private static string ManyStepsJson(int count)
        {
            var items = Enumerable.Range(1, count).Select(i =>
                $@"    {{ ""Code"": ""S{i:D2}"", ""Name"": ""{i}번 단계"", ""TargetTables"": [""dbo.T{i:D2}""], ""ErrorCodes"": [""-{i}""] }}");
            return "```json\n{\n  \"Steps\": [\n" + string.Join(",\n", items) + "\n  ]\n}\n```";
        }

        /// <summary>단계 목록에 맞는 골격. 각 단계 자리에 STEP 플레이스홀더를 둔다.</summary>
        private static string SkeletonFor(int count)
        {
            var placeholders = string.Join("\n", Enumerable.Range(1, count).Select(i => $"<!-- STEP:S{i:D2} -->"));
            return SkeletonMarkdown.Replace("<!-- STEP:S01 -->\n<!-- STEP:S02 -->", placeholders);
        }

        /// <summary>
        /// 단계 본문 생성 호출의 동시 실행 수와 시간 구간을 관측한다.
        /// 테스트 ①(상한), ②(워밍이 단독인가), ③(완료 순서 무관)이 공유한다.
        /// </summary>
        private sealed class ConcurrencyProbe
        {
            private int _current;
            private int _max;
            private readonly List<(string Code, long Start, long End)> _spans = new();
            private readonly object _lock = new();

            public int MaxObserved => Volatile.Read(ref _max);

            public IReadOnlyList<(string Code, long Start, long End)> Spans
            {
                get { lock (_lock) { return _spans.ToList(); } }
            }

            public async Task<AiResult> RunAsync(string code, string content, int delayMs)
            {
                var now = Interlocked.Increment(ref _current);
                int seen;
                while (now > (seen = Volatile.Read(ref _max)) &&
                       Interlocked.CompareExchange(ref _max, now, seen) != seen)
                {
                }

                var start = Stopwatch.GetTimestamp();
                await Task.Delay(delayMs);
                var end = Stopwatch.GetTimestamp();
                Interlocked.Decrement(ref _current);
                lock (_lock) { _spans.Add((code, start, end)); }
                return new AiResult { Content = content };
            }
        }

        /// <summary>
        /// count개 단계를 내는 분할 생성 fake. sectionFor가 null이면 전부 하한을 통과하는 섹션을 낸다.
        /// delayFor는 단계 코드별 지연(ms)을 정한다 — 완료 순서를 조작하는 데 쓴다.
        /// </summary>
        private static IAiService ManyStepAiService(
            int count,
            ConcurrencyProbe probe,
            Func<string, int> delayFor,
            Func<string, string>? sectionFor = null)
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + ManyStepsJson(count) });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonFor(count) });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    var content = sectionFor != null
                        ? sectionFor(step.Code)
                        : HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]);
                    return probe.RunAsync(step.Code, content, delayFor(step.Code));
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });
            return aiService;
        }
```

`ManyStepsJson`은 `LegacyProcedures`를 넣지 않으므로, 이 fake로 만든 문서에는 목차 커버리지 배너(`dbo.USP_Spec1`이 어느 단계에도 담기지 않았다는 경고)가 항상 붙는다. 아래 테스트들의 단언 대상이 아니며, 세 실행 모두에 동일하게 붙으므로 문서 비교(③)도 영향을 받지 않는다.

- [ ] **Step 2: 실패하는 테스트 5개를 쓴다**

Task 1이 추가한 테스트들 아래에 이어 쓴다.

```csharp
        /// <summary>
        /// ① 동시 실행 수가 설정값을 넘지 않는다. 세마포어를 지우면 13을 관측하고 실패한다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WithStepConcurrencyFour_NeverExceedsFourInFlight()
        {
            var probe = new ConcurrencyProbe();
            var aiService = ManyStepAiService(13, probe, _ => 60);
            var ui = Substitute.For<IVerificationUserInteraction>();

            await RunBatchPipelineWithConcurrency(aiService, ui, 4);

            Assert.True(probe.MaxObserved <= 4, $"관측된 최대 동시 실행 수가 {probe.MaxObserved}였다.");
            Assert.True(probe.MaxObserved > 1, "병렬이 전혀 일어나지 않았다 — 팬아웃이 동작하지 않는다.");
        }

        /// <summary>
        /// ② 첫 단계는 항상 단독으로 돈다. 이것이 프롬프트 접두사 캐시 이점의
        /// 유일한 기계적 보증이다 — 없으면 누군가 워밍을 "불필요한 직렬화"로 보고 지운다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WarmsCacheBeforeFanningOut()
        {
            var probe = new ConcurrencyProbe();
            var aiService = ManyStepAiService(13, probe, _ => 60);
            var ui = Substitute.For<IVerificationUserInteraction>();

            await RunBatchPipelineWithConcurrency(aiService, ui, 4);

            var spans = probe.Spans;
            var first = spans.Single(s => s.Code == "S01");
            var earliestOther = spans.Where(s => s.Code != "S01").Min(s => s.Start);
            Assert.True(first.End <= earliestOther,
                "S01이 끝나기 전에 다른 단계가 시작됐다 — 캐시 워밍이 깨졌다.");
        }

        /// <summary>
        /// ③④ 완료 순서와 동시 실행 수가 산출물을 바꾸지 않는다.
        /// 순차(1), 팬아웃(4, 정방향 지연), 팬아웃(4, 역방향 지연) 세 실행이 같은 문서를 낸다.
        /// 병렬화가 산출물을 바꾸지 않는다는 것이 이 설계의 전제이므로 전제 자체를 단언한다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_ProducesSameDocumentRegardlessOfConcurrencyOrCompletionOrder()
        {
            const int count = 8;
            // S03과 S06은 하한을 통과하지 못한다 — 배너 내용까지 비교 대상에 넣기 위함.
            Func<string, string> sections = code =>
                code is "S03" or "S06"
                    ? SubFloorStepSection(code)
                    : HealthyStepSection(code, $"dbo.T{code.Substring(1)}", $"-{int.Parse(code.Substring(1))}");

            var sequential = await RunBatchPipelineWithConcurrency(
                ManyStepAiService(count, new ConcurrencyProbe(), _ => 1, sections),
                Substitute.For<IVerificationUserInteraction>(), 1);

            var forward = await RunBatchPipelineWithConcurrency(
                ManyStepAiService(count, new ConcurrencyProbe(), code => int.Parse(code.Substring(1)) * 10, sections),
                Substitute.For<IVerificationUserInteraction>(), 4);

            // 역방향: S08이 가장 먼저, S01이 가장 늦게 끝난다.
            var reverse = await RunBatchPipelineWithConcurrency(
                ManyStepAiService(count, new ConcurrencyProbe(), code => (count + 1 - int.Parse(code.Substring(1))) * 10, sections),
                Substitute.For<IVerificationUserInteraction>(), 4);

            Assert.Equal(sequential.Plan, forward.Plan);
            Assert.Equal(sequential.Plan, reverse.Plan);
            // 셋 다 실제로 하한 배너를 달고 있어야 비교가 의미를 갖는다.
            Assert.Contains("[하한 미달]", sequential.Plan);
            Assert.Contains("S03 (하한 미달)", sequential.Plan);
            Assert.Contains("S06 (하한 미달)", sequential.Plan);
        }

        /// <summary>
        /// ⑤ 팬아웃 도중의 취소가 삼켜지지 않고 호출부까지 올라온다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WhenCancelledDuringFanOut_PropagatesCancellation()
        {
            using var cts = new CancellationTokenSource();
            var calls = 0;
            var aiService = ManyStepAiService(13, new ConcurrencyProbe(), _ => 30);
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    if (Interlocked.Increment(ref calls) >= 3)
                    {
                        cts.Cancel();
                    }
                    await Task.Delay(30, call.Arg<CancellationToken>());
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            var dbService = Substitute.For<IDbMetadataService>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), Substitute.For<IVerificationUserInteraction>(),
                "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8, 4);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                orchestrator.RunConsolidatedPipelineAsync(
                    specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot,
                    isBatchMode: true, cancellationToken: cts.Token));
        }

        /// <summary>
        /// ⑥ 생성자의 Math.Max(1, ...) 절상이 실제로 막는 것. 절상이 없으면
        /// new SemaphoreSlim(0)이 슬롯을 하나도 내주지 않아 단계 생성이 영구 대기한다 —
        /// StepConcurrency에 0을 적은 사용자의 실행이 그대로 멈춘다는 뜻이다.
        /// 절상이 살아 있으면 슬롯 1개짜리 완전 순차로 정상 완주한다.
        ///
        /// Task 1에서 이 커버리지를 만들 수 없었던 이유: 그 시점에는 _stepConcurrency가
        /// 로컬 공급자 경고(> 1일 때만 발동)에만 쓰여, 원값 0·-5가 절상 전에도 이미
        /// > 1이 아니라 어떤 단언도 절상의 유무를 구분하지 못했다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WhenConcurrencyIsZero_ClampsToOneAndCompletes()
        {
            var probe = new ConcurrencyProbe();
            var aiService = ManyStepAiService(4, probe, _ => 1);
            var ui = Substitute.For<IVerificationUserInteraction>();

            var run = RunBatchPipelineWithConcurrency(aiService, ui, 0);
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.Same(run, finished);   // 절상이 없으면 여기서 타임아웃한다

            var result = await run;
            Assert.Equal(1, probe.MaxObserved);   // 슬롯 1개 = 완전 순차
            Assert.Contains("### S01 단계", result.Plan);
            Assert.Contains("### S04 단계", result.Plan);
        }
```

- [ ] **Step 3: 테스트가 실패하는 것을 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunConsolidatedPipeline_With|FullyQualifiedName~RunConsolidatedPipeline_Warms|FullyQualifiedName~RunConsolidatedPipeline_Produces|FullyQualifiedName~RunConsolidatedPipeline_WhenCancelled" 2>&1 | tail -20
```
Expected: ①이 `관측된 최대 동시 실행 수가 1이었다`(또는 `병렬이 전혀 일어나지 않았다`)로 FAIL. 나머지는 순차 구현에서도 우연히 통과할 수 있다 — ①이 실패하는 것이 이 단계의 확인 대상이다.

- [ ] **Step 4: 결과 레코드를 추가한다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`의 `GenerateBySplitAsync` XML 주석 바로 위에 추가한다.

```csharp
        /// <summary>
        /// 단계 하나의 생성 결과. 병렬 실행 중에는 공유 컬렉션을 만지지 않고 이
        /// 레코드로 돌려주며, 병합은 Task.WhenAll 이후 단일 스레드에서 한다.
        /// </summary>
        private sealed record StepSectionResult(string Code, string Markdown, string? FloorViolation);
```

- [ ] **Step 5: `for` 루프를 워밍 + 팬아웃으로 바꾼다**

Task 2가 만든 `for` 루프 전체를 다음으로 교체한다. (`foreach (var step in pending) { floorViolations.Remove(step.Code); }` 블록은 그대로 둔다.)

```csharp
            // 동시 실행 수 제한. Dispose하지 않는다 — AvailableWaitHandle을 쓰지 않아
            // Dispose가 필요 없고, 취소로 Task.WhenAll이 먼저 빠져나간 뒤에도 아직
            // 돌고 있는 단계 태스크가 finally에서 Release를 호출하므로, 여기서
            // Dispose하면 그 태스크가 ObjectDisposedException으로 죽어 관측되지 않는
            // 예외가 된다.
            var gate = new SemaphoreSlim(_stepConcurrency);

            async Task<StepSectionResult> RunStepAsync(BatchStepPlan step, int index)
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    // 진행률 행은 슬롯을 잡은 뒤에 추가한다. 먼저 추가하면 대기 중인
                    // 단계까지 전부 "생성 중"으로 떠서, 실제로는 넷만 돌고 있다는
                    // 사실이 화면에서 사라진다.
                    var taskKey = $"step_{step.Code}";
                    progressScope.AddTask(taskKey, $"3/3. 단계 본문 생성 중 ({step.Code} · {index + 1}/{pending.Count})...");

                    var (markdown, violation) = await GenerateStepSectionWithFloorRetryAsync(
                        step, steps, conventions, specs, targetLanguage, jobName, cancellationToken);

                    progressScope.CompleteTask(taskKey);
                    return new StepSectionResult(step.Code, markdown, violation);
                }
                finally
                {
                    // 슬롯은 단계당 재시도 2회를 모두 감싼 채 유지했다가 여기서 놓는다.
                    // 재시도 사이에 놓으면 다른 단계가 끼어들어 동시 요청 수가 설정값을 넘는다.
                    gate.Release();
                }
            }

            // 워밍: 첫 단계를 끝까지 기다린 뒤에야 나머지를 띄운다. 프롬프트 접두사
            // 캐시는 요청이 "완료돼야" 채워지므로, N개를 동시에 쏘면 N개 전부 미스다.
            // 13단계·동시 4 기준으로 워밍이 있든 없든 4라운드로 같지만, 미스는
            // 4회에서 1회로 준다 — 벽시계를 쓰지 않고 얻는 이득이다.
            //
            // 이 await가 워밍의 유일한 보증이다. 세마포어가 아니다 — 슬롯이 여러
            // 개여도 두 번째 호출은 여기서 시작조차 하지 않는다. 지우지 말 것.
            var stepResults = new List<StepSectionResult>(pending.Count);
            if (pending.Count > 0)
            {
                stepResults.Add(await RunStepAsync(pending[0], 0));
            }

            if (pending.Count > 1)
            {
                var rest = pending.Skip(1).Select((step, offset) => RunStepAsync(step, offset + 1)).ToList();
                stepResults.AddRange(await Task.WhenAll(rest));
            }

            // 병합은 단일 스레드에서 목록 순서대로. Task.WhenAll은 완료 순서가 아니라
            // 넘긴 순서로 결과를 돌려주므로, 사전에 들어가는 순서가 결정적이다.
            foreach (var stepResult in stepResults)
            {
                sections[stepResult.Code] = stepResult.Markdown;
                if (stepResult.FloorViolation != null)
                {
                    floorViolations[stepResult.Code] = stepResult.FloorViolation;
                }
            }
```

경계 조건 셋은 자연히 처리된다. `pending`이 비면 아무 호출도 하지 않고, 1개면 워밍이 곧 전부이며, `_stepConcurrency == 1`이면 슬롯이 하나라 팬아웃이 사실상 순차가 된다.

- [ ] **Step 6: 테스트가 통과하는 것을 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunConsolidatedPipeline" 2>&1 | tail -10
```
Expected: 신규 4개 포함 전부 PASS

- [ ] **Step 7: 세마포어를 지웠을 때 ①이 실패하는지 뮤테이션으로 확인한다**

`new SemaphoreSlim(_stepConcurrency)`를 일시적으로 `new SemaphoreSlim(int.MaxValue)`로 바꿔 테스트 ①만 돌린다.

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~NeverExceedsFourInFlight" 2>&1 | tail -5
```
Expected: FAIL (`관측된 최대 동시 실행 수가 12였다` 류). 확인 후 **반드시 원복한다.**

같은 방식으로 워밍을 없앤 형태 — 워밍 블록을 지우고 팬아웃을 `pending.Select((step, i) => RunStepAsync(step, i))` 전체로 바꾼 것 — 로 테스트 ②가 실패하는지 확인하고 **반드시 원복한다.**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~WarmsCacheBeforeFanningOut" 2>&1 | tail -5
```
Expected: FAIL (`S01이 끝나기 전에 다른 단계가 시작됐다`)

세 번째로, 생성자의 `Math.Max(1, stepConcurrency)`를 `stepConcurrency`로 바꿔 테스트 ⑥이 실패하는지 확인하고 **반드시 원복한다.**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ClampsToOneAndCompletes" 2>&1 | tail -5
```
Expected: FAIL (`Assert.Same` 실패 — 30초 타임아웃. 절상이 없으면 `SemaphoreSlim(0)`에서 영구 대기한다.) 이 뮤테이션은 매달린 태스크를 남기므로 확인 즉시 원복하고 필터 없이 한 번 더 돌려 정상 상태를 확인한다.

- [ ] **Step 8: 전체 빌드·테스트**

```bash
dotnet clean && dotnet build 2>&1 | tail -5
dotnet test 2>&1 | tail -3
```
Expected: 경고 8개 · 오류 0개, 770건 통과 (765 + 신규 5)

`CancellationPolicyTests`가 통과해야 한다 — 새로 추가한 `catch`는 없지만 스캔 대상이 넓어졌다.

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs \
        tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: warm the prompt cache on the first step, then fan out the rest

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: 문서 동기화

**Files:**
- Modify: `docs/architecture.md` (§2.2 클래스 목록, §3.1 Mermaid, §4.4.5)
- Modify: `AGENTS.md` (개발 규칙, 체크리스트의 테스트 개수)
- Modify: `README.md` (`appsettings.json` 설정 레퍼런스)

**Interfaces:**
- Consumes: Task 1~3의 최종 구현 (설정 키 이름, 생성자 매개변수, 워밍/팬아웃 형태)
- Produces: 없음

`.claude/skills/reset-doc-sync` 스킬의 원칙을 따른다 — 전문을 읽지 말고 목차부터 확보한 뒤 필요한 섹션만 부분 읽기하고, 바뀌지 않은 내용은 건드리지 않는다. 신규 기능 설명이 기존 기능들과 같은 추상화 수준과 두께를 갖도록 압축한다.

- [ ] **Step 1: 대상 섹션 위치를 특정한다**

```bash
grep -n "^#\{1,4\} " docs/architecture.md | sed -n '1,60p'
grep -n "StepConcurrency\|MaxL2Attempts\|단계 본문\|GenerateBySplitAsync\|BatchPlanAssembler" README.md AGENTS.md docs/architecture.md
```

- [ ] **Step 2: `README.md`의 설정 레퍼런스에 `StepConcurrency`를 더한다**

`### 1. appsettings.json 설정` 블록에서 `MaxL2Attempts` 줄 바로 아래에, `src/ReSet.Cli/appsettings.json`에 넣은 것과 **키 이름·기본값·주석 의미가 일치하도록** 한 줄을 추가한다. README 쪽 주석은 길이에 맞춰 줄바꿈해도 되지만 의미를 바꾸지 않는다.

- [ ] **Step 3: `docs/architecture.md` §3.1 Mermaid의 단계 생성 노드를 갱신한다**

기존 단계 생성 노드의 라벨에 "첫 단계 워밍 → 나머지 팬아웃(StepConcurrency)"이 드러나게 한다. 라벨에 괄호·특수문자가 들어가므로 반드시 따옴표로 감싼다(`A["..."]`). 라벨이 이미 빽빽하면 노드를 늘리지 말고 문구를 압축한다.

`mmdc`가 설치돼 있으면 수정한 블록을 뽑아 컴파일해 문법을 확인한다. 없으면 이 검증은 건너뛴다 — 링크 검사(Step 6)는 어느 쪽이든 수행한다.

- [ ] **Step 4: `docs/architecture.md` §4.4.5에 한 문단을 더한다**

동시성과 캐시 워밍의 관계를 한 문단으로 적는다. 담을 사실은 셋이다 — (1) 단계 본문은 `StepConcurrency`만큼 동시에 생성된다, (2) 첫 단계는 설정값과 무관하게 항상 단독으로 돌아 프롬프트 접두사 캐시를 채운다, (3) 병합은 `Task.WhenAll` 이후 단일 스레드에서 목차 순서로 하므로 완료 순서가 산출물을 바꾸지 않는다. 내부 변수명·세마포어 구현 세부는 적지 않는다.

§2.2 클래스 목록 테이블은 새 공개 클래스가 없으므로 **변경하지 않는다**(`StepSectionResult`는 private 중첩 레코드다).

- [ ] **Step 5: `AGENTS.md`에 워밍 보존 규칙과 테스트 개수를 반영한다**

파이프라인 규칙 범주에 한 줄을 더한다. 요지: **`GenerateBySplitAsync`의 첫 단계 단독 실행(워밍)을 "불필요한 직렬화"로 보고 제거하지 말 것.** 지웠을 때의 증상이 "산출물은 그대로인데 입력 토큰 비용만 조용히 오름"이라 코드만 봐서는 이유를 알 수 없다. `RunConsolidatedPipeline_WarmsCacheBeforeFanningOut`이 이를 지킨다는 사실도 함께 적는다.

`AGENTS.md:264`의 테스트 개수를 실측값으로 갱신한다.

```bash
dotnet test 2>&1 | tail -3
grep -n "개의 단위 테스트" AGENTS.md
```
두 숫자가 일치해야 한다.

- [ ] **Step 6: 링크 검증**

```bash
grep -o '](\.\./src/[^)]*)' docs/architecture.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "docs/$p" ] || echo "BROKEN architecture.md: $p"; done
grep -ho '](\./[^)]*)' AGENTS.md README.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "$p" ] || echo "BROKEN: $p"; done
```
Expected: 출력 없음

- [ ] **Step 7: 커밋**

```bash
git add README.md AGENTS.md docs/architecture.md
git commit -m "docs: record step-body parallel generation and the cache-warming rule

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## 완료 기준

- `dotnet clean && dotnet build`에서 경고가 정확히 8건, 오류 0건
- `dotnet test`가 770건(763 + 신규 7) 통과
- `git show HEAD~N -- src/ReSet.Cli/appsettings.json`이 `StepConcurrency` 한 줄 추가만 담고, 작업 트리의 사용자 로컬 수정이 살아 있다
- 문서 3종(`README.md`, `AGENTS.md`, `docs/architecture.md`) 동기화 완료, 링크 검사 통과
- **실측 회귀는 사용자가 직접 수행한다** — 동일한 12개 SP로 `StepConcurrency = 4` 재실행해 단계 구간 벽시계와 캐시 히트율을 기록하고, 산출물 품질(단계별 분량, 하한 미달 배너 유무)이 순차 실행과 동등함을 확인한다. 이 항목은 구현 완료의 게이트가 아니다
