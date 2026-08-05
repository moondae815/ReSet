# 통합 배치 설계 목차 재수립 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 통합 배치 계획서 재시도가 점수를 개선하지 못할 때 목차를 한 번 다시 세우고, L3 사용자가 구조 변경을 요구하면 그 요구를 목차에 반영한다.

**Architecture:** 정체 판정과 Job당 1회 상한을 `StructureRedraftPolicy` 하나가 소유한다. 정체 신호는 이미 존재하는 `BestAttempt.TryRecord`의 반환값(최고점 갱신 여부)을 그대로 쓰고 비교식을 새로 만들지 않는다. `DraftBatchPlanStructureAsync`에 재수립 입력(이전 목차·누적 피드백) 두 개를 추가해 2/3 단계를 재시도 루프 안에서도 호출할 수 있게 하고, L3에서는 `HumanReviewResult`가 나르는 새 플래그로 같은 경로를 탄다.

**Tech Stack:** .NET (C#), xUnit, NSubstitute, Serilog, Spectre.Console

## Global Constraints

이 절의 규칙은 모든 태스크의 요구사항에 암묵적으로 포함된다.

- 설계 스펙: `docs/superpowers/specs/2026-08-05-batch-structure-redraft-design.md`
- 프롬프트의 `systemPrompt`는 영문으로 작성한다. 한국어 전면 번역 금지 (AGENTS.md 하이브리드 영문 프롬프트 구조 준수)
- 통합 배치 계획 프롬프트의 4개 필수 H2 헤더(`## 통합 배치 아키텍처 개요`, `## Mermaid 기반 통합 흐름도`, `## 단계별 이행 상세 및 의사코드`, `## 통합 데이터 정합성 검증 SQL 세트`) 강제를 절대 완화하지 않는다. `MechanicalValidator`가 같은 헤더를 요구한다
- Anti-Shortcut 제약 등 기존 프롬프트 규칙을 삭제·간소화하지 않는다. 이번 작업은 **추가만** 한다
- 취소 토큰을 넘기는 `await`를 감싸는 광범위 `catch`에는 반드시 `when (ex is not OperationCanceledException)` 필터를 단다. `CancellationPolicyTests`가 Roslyn 구문 트리로 자동 검사한다
- 검증 종료 상태는 `VerificationOutcome`의 네 값으로만 표현한다. `bool` 플래그나 `ReviewResult`의 널 여부로 대체 판정하지 않는다
- `VerificationPipelineOrchestrator.cs`에는 `using System.IO`가 없다. 파일·경로 API는 `System.IO.Path`, `System.IO.File`, `System.IO.Directory`로 완전 수식해서 쓴다 (기존 코드와 동일)
- 문자열 보간(`$"..."`) 안의 프롬프트 텍스트에 중괄호가 필요하면 `{{`/`}}`로 이스케이프한다
- 최종 완료 기준: `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l`이 정확히 8, `dotnet test` 전건 통과

## File Structure

| 파일 | 책임 | 상태 |
|---|---|---|
| `src/ReSet.Core/Services/StructureRedraftPolicy.cs` | 정체의 정의와 Job당 1회 상한. 이 규칙을 아는 유일한 자리 | 신규 |
| `tests/ReSet.Core.Tests/StructureRedraftPolicyTests.cs` | 위 규칙의 단위 테스트 | 신규 |
| `src/ReSet.Core/Services/IAiService.cs` | `DraftBatchPlanStructureAsync`에 재수립 입력 2개 추가 | 수정 |
| `src/ReSet.Core/Services/AiService.cs` | 재수립 모드 프롬프트 분기 | 수정 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | L2 정체 경로·L3 구조 피드백 경로 배선, 재수립 헬퍼 2개 | 수정 |
| `src/ReSet.Core/Models/HumanReviewResult.cs` | `RedraftStructure` 플래그 | 수정 |
| `src/ReSet.Cli/ConsoleUserInteraction.cs` | 피드백 성격 확인 프롬프트 | 수정 |
| `tests/ReSet.Core.Tests/AiServiceTests.cs` | 재수립 프롬프트 검증 | 수정 |
| `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` | 기존 27개 스텁 마이그레이션 + 신규 시나리오 | 수정 |
| `docs/architecture.md`, `README.md`, `AGENTS.md` | 문서 동기화 | 수정 |

---

### Task 1: StructureRedraftPolicy

정체 판정과 1회 상한을 소유하는 클래스. 다른 어떤 것에도 의존하지 않으므로 먼저 만든다.

**Files:**
- Create: `src/ReSet.Core/Services/StructureRedraftPolicy.cs`
- Test: `tests/ReSet.Core.Tests/StructureRedraftPolicyTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `ReSet.Core.Services.StructureRedraftPolicy` — `bool Consumed { get; }`, `bool TryConsume(bool improvedThisAttempt)`

- [ ] **Step 1: Write the failing test**

`tests/ReSet.Core.Tests/StructureRedraftPolicyTests.cs`를 새로 만든다.

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StructureRedraftPolicyTests
    {
        [Fact]
        public void NewPolicy_HasNotConsumedItsRedraft()
        {
            var policy = new StructureRedraftPolicy();

            Assert.False(policy.Consumed);
        }

        // 개선이 나오는 동안은 목차가 원인이 아니다. 멀쩡한 구조를 갈아엎지 않는다.
        [Fact]
        public void ImprovingAttempt_DoesNotRedraft()
        {
            var policy = new StructureRedraftPolicy();

            Assert.False(policy.TryConsume(improvedThisAttempt: true));
            Assert.False(policy.Consumed);
        }

        // 이 설계의 핵심. 기본 예산(총 3회)에서 2차가 최고점을 못 넘기면
        // 그 자리에서 목차를 다시 세워야 3차가 새 구조로 생성된다.
        // 2회 연속을 요구하면 기본 예산에서는 영원히 발동하지 못한다.
        [Fact]
        public void FirstAttemptWithoutImprovement_Redrafts()
        {
            var policy = new StructureRedraftPolicy();

            Assert.True(policy.TryConsume(improvedThisAttempt: false));
            Assert.True(policy.Consumed);
        }

        // Job당 1회. 구조를 한 번 갈아엎었는데도 정체하면 원인은 목차가 아니다.
        [Fact]
        public void SecondAttemptWithoutImprovement_DoesNotRedraftAgain()
        {
            var policy = new StructureRedraftPolicy();
            policy.TryConsume(improvedThisAttempt: false);

            Assert.False(policy.TryConsume(improvedThisAttempt: false));
            Assert.True(policy.Consumed);
        }

        // 소진 이후에는 개선 여부와 무관하게 항상 false다.
        [Fact]
        public void AfterConsumption_ImprovementStillDoesNotRedraft()
        {
            var policy = new StructureRedraftPolicy();
            policy.TryConsume(improvedThisAttempt: false);

            Assert.False(policy.TryConsume(improvedThisAttempt: true));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StructureRedraftPolicyTests"
```

Expected: 컴파일 실패 — `StructureRedraftPolicy` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: Write minimal implementation**

`src/ReSet.Core/Services/StructureRedraftPolicy.cs`를 새로 만든다.

```csharp
namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도가 계획서를 개선하지 못할 때 목차(PlanStructure)를 다시 세울지 결정한다.
    ///
    /// 이 클래스가 존재하는 이유: 통합 배치 경로는 목차를 재시도 루프 밖에 고정한다.
    /// Actor가 회차마다 백지에서 다시 쓰기 때문에(CriticFeedbackLog 참조) 목차가 없으면
    /// 회차마다 문서 뼈대가 달라져 누적 피드백이 엉뚱한 자리에 붙는다. 그 대가로,
    /// 목차 자체가 원인인 결함 — 스텝 누락, 청킹 불가 스텝을 청킹으로 배치 — 은
    /// 몇 번을 재시도해도 고쳐지지 않고 재시도 예산만 소진했다. 이 클래스가 그
    /// 상태를 관측해 탈출구를 연다.
    ///
    /// 정체의 정의를 새로 만들지 않는다. BestAttempt.TryRecord가 이미 "최고점을
    /// 갱신했는가"를 엄격 부등호로 판정해 소유하므로 그 반환값을 그대로 받는다.
    ///
    /// L3 사용자 지시는 이 정책을 거치지 않는다. 사용자가 구조 변경을 명시적으로
    /// 요청하면 상한과 무관하게 수행한다 — 사용자의 지시를 자동화 예산으로 막지 않는다.
    /// </summary>
    public sealed class StructureRedraftPolicy
    {
        /// <summary>이미 재수립을 1회 소비했는가.</summary>
        public bool Consumed { get; private set; }

        /// <summary>
        /// 이번 회차가 최고점을 갱신하지 못했고 아직 재수립을 쓰지 않았다면 true를
        /// 돌려주고 소비를 기록한다.
        ///
        /// 미갱신 1회로 발동한다. 2회 연속을 요구하면 기본 예산(MaxL2Attempts=2 →
        /// 총 3회)에서 발동할 자리가 없다. 1차는 후보가 없어 항상 갱신되므로,
        /// 2차의 갱신 실패가 "재시도가 개선을 못 냈다"의 첫 증거다.
        /// </summary>
        public bool TryConsume(bool improvedThisAttempt)
        {
            if (Consumed || improvedThisAttempt)
            {
                return false;
            }

            Consumed = true;
            return true;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StructureRedraftPolicyTests"
```

Expected: 5건 PASS

- [ ] **Step 5: Commit**

```bash
git add src/ReSet.Core/Services/StructureRedraftPolicy.cs tests/ReSet.Core.Tests/StructureRedraftPolicyTests.cs
git commit -m "feat: own the plan structure redraft rule in one policy class"
```

---

### Task 2: DraftBatchPlanStructureAsync 재수립 입력

2/3 단계가 이전 목차와 누적 피드백을 받아 구조를 다시 짤 수 있게 한다. 시그니처가 바뀌므로 기존 호출부 전부가 컴파일 에러를 낸다 — 이 태스크가 그것까지 복구한다.

**Files:**
- Modify: `src/ReSet.Core/Services/IAiService.cs:18`
- Modify: `src/ReSet.Core/Services/AiService.cs:1836-1866`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1705` (호출부 복구)
- Test: `tests/ReSet.Core.Tests/AiServiceTests.cs` (신규 테스트 2건)
- Modify: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` (기존 스텁 27곳 마이그레이션)

**Interfaces:**
- Consumes: 없음
- Produces: `IAiService.DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default)` → `Task<AiResult>`

- [ ] **Step 1: Write the failing test**

`tests/ReSet.Core.Tests/AiServiceTests.cs`의 클래스 안에 두 테스트를 추가한다. 기존 `GenerateConsolidatedBatchPlanAsync_Prompt_ContainsDomainConstraints`와 같은 형태다(`MockHttpMessageHandler`와 `OpenAiClient`를 그대로 쓴다).

```csharp
        // 재수립 모드가 아니면 프롬프트가 지금과 똑같아야 한다.
        // 1회차 목차 설계는 이번 변경의 영향을 받지 않는다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_WithoutPreviousStructure_HasNoRedraftInstruction()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync("brainstorming", "C#", "Test_Job");

            Assert.DoesNotContain("[Redraft]", result.SystemPrompt);
            // 4개 필수 H2 강제는 두 모드 모두에서 유지된다.
            Assert.Contains("통합 배치 아키텍처 개요", result.SystemPrompt);
        }

        // 재수립 모드에서는 이전 구조를 반복하지 말라는 지시와,
        // 그 판단 근거인 누적 피드백이 프롬프트에 실려야 한다.
        [Fact]
        public async Task DraftBatchPlanStructureAsync_WithPreviousStructure_CarriesRedraftInstructionAndFeedback()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 목차\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.DraftBatchPlanStructureAsync(
                "brainstorming", "C#", "Test_Job",
                effort: null,
                previousStructure: "## 낡은 목차",
                redraftFeedback: "청킹 불가 스텝이 청킹으로 배치됨");

            Assert.Contains("[Redraft]", result.SystemPrompt);
            Assert.Contains("## 낡은 목차", result.UserPrompt);
            Assert.Contains("청킹 불가 스텝이 청킹으로 배치됨", result.UserPrompt);
            Assert.Contains("통합 배치 아키텍처 개요", result.SystemPrompt);
        }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~DraftBatchPlanStructureAsync"
```

Expected: 컴파일 실패 — `previousStructure`/`redraftFeedback` 이름 있는 인수가 없음 (CS1739)

- [ ] **Step 3: 인터페이스 시그니처 변경**

`src/ReSet.Core/Services/IAiService.cs:18`을 다음으로 교체한다.

```csharp
        Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: AiService 구현 변경**

`src/ReSet.Core/Services/AiService.cs:1836`의 메서드 시그니처를 바꾸고, 시스템 프롬프트 조립 뒤와 사용자 프롬프트 조립 안에 재수립 분기를 넣는다. 기존 본문은 그대로 두고 아래 표시된 부분만 더한다.

```csharp
        public async Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default)
        {
            var systemPrompt = $@"You are a principal database modernization architect. Based on the previous brainstorming, draft a detailed step-by-step structural plan (Table of Contents and execution flow) for the final '{jobName}' {targetLanguage} batch application document.
You MUST use exactly the following 4 mandatory H2 headers in Korean, and design the detailed sub-headers (H3, H4) beneath them:
1. ## 통합 배치 아키텍처 개요
2. ## Mermaid 기반 통합 흐름도
3. ## 단계별 이행 상세 및 의사코드
4. ## 통합 데이터 정합성 검증 SQL 세트";

            // 재수립 모드. 이전 구조로 만든 본문이 리뷰를 반복 통과하지 못했다는 뜻이므로
            // 같은 구조를 다시 내면 재시도 예산만 소진된다. 4개 H2 강제는 유지한다 —
            // MechanicalValidator가 같은 헤더를 요구하므로 여기서 풀면 L1이 깨진다.
            var isRedraft = !string.IsNullOrWhiteSpace(previousStructure);
            if (isRedraft)
            {
                systemPrompt += @"

[Redraft]
The previous structure below repeatedly failed cross-review. Do NOT reproduce it.
- Diagnose which structural decision caused the reported defects: a missing step, a step placed under the wrong architecture (e.g. chunking a GROUP BY aggregation that cannot be chunked), or an execution order that breaks data consistency.
- Change that decision. Reordering sub-headers without changing the underlying step design is not an acceptable redraft.
- Keep the 4 mandatory H2 headers exactly as specified above.";
            }

            var userPrompt = new System.Text.StringBuilder();
            userPrompt.AppendLine($"Unified Batch Job Name: {jobName}");
            userPrompt.AppendLine($"Target Language Stack: {targetLanguage}");
            userPrompt.AppendLine();
            userPrompt.AppendLine("[Brainstorming Analysis Result]");
            userPrompt.AppendLine(brainstormingResult);
            userPrompt.AppendLine();

            if (isRedraft)
            {
                userPrompt.AppendLine("[Previous Structure That Failed Review]");
                userPrompt.AppendLine(previousStructure);
                userPrompt.AppendLine();

                if (!string.IsNullOrWhiteSpace(redraftFeedback))
                {
                    userPrompt.AppendLine("[Accumulated Review Feedback]");
                    userPrompt.AppendLine(redraftFeedback);
                    userPrompt.AppendLine();
                }
            }

            userPrompt.AppendLine("Please draft the detailed structural plan and step-by-step instructions for the final markdown document.");

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                systemPrompt += "\n\n[Ollama Thinking Requirements]\n- Detail your analytical thoughts inside <think> and </think> tags. The final text must be placed outside the think tags.";
            }

            Log.Information("AI 배치 계획 목차 수립 요청 전송 - JobName: {JobName}, TargetLanguage: {TargetLanguage}, Effort: {Effort}, Redraft: {IsRedraft}", jobName, targetLanguage, effort ?? "Default", isRedraft);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), _temperature, effort, cancellationToken);
            if (aiResult == null) aiResult = new AiResult();
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt.ToString();
            return aiResult;
        }
```

- [ ] **Step 5: 프로덕션 호출부 복구**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1705`가 `cancellationToken`을 5번째 위치에 positional로 넘기고 있어 컴파일 에러가 난다. 이름 있는 인수로 바꾼다.

```csharp
                            var planResult = await WrapWithProgress(_consolidatorService.DraftBatchPlanStructureAsync(brainstormResult.Content, targetLanguage, jobName, _consolidatorEffort, cancellationToken: cancellationToken), progressScope, "phase2");
```

- [ ] **Step 6: 테스트 스텁 마이그레이션**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의 `DraftBatchPlanStructureAsync` 스텁 27곳 전부에서 `Arg.Any<CancellationToken>()` **앞에** `Arg.Any<string?>(), Arg.Any<string?>(),`를 삽입한다. 세 가지 형태가 섞여 있다.

한 줄 형태 (`Arg.Any<string>()` 네 개 + 토큰):
```csharp
// 변경 전
_aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
// 변경 후
_aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
```

여러 줄 형태 (`:2183`, `:2293` 부근):
```csharp
// 변경 전
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
// 변경 후
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
```

여러 줄 압축 형태 (`:2252` 부근):
```csharp
// 변경 전
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
// 변경 후
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
```

빠짐없이 고쳤는지 확인한다. 아래 명령의 출력이 0이어야 한다.

```bash
grep -c "DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())" tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
```

- [ ] **Step 7: Run tests to verify they pass**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~DraftBatchPlanStructureAsync"
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

Expected: 신규 2건 PASS, 기존 오케스트레이터 테스트 전건 PASS

- [ ] **Step 8: Commit**

```bash
git add src/ReSet.Core/Services/IAiService.cs src/ReSet.Core/Services/AiService.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/AiServiceTests.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: let the structure draft stage take a redraft brief"
```

---

### Task 3: L2 정체 경로 배선과 목차 이력 보존

재시도가 개선을 못 냈을 때 목차를 다시 세우고, 교체되는 목차를 감사 가능하게 남긴다.

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (`RunConsolidatedPipelineAsync` 및 private 헬퍼 2개 추가)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `StructureRedraftPolicy.TryConsume(bool)` (Task 1), `IAiService.DraftBatchPlanStructureAsync(..., previousStructure, redraftFeedback, ...)` (Task 2)
- Produces: private `Task<string> RedraftPlanStructureAsync(string currentStructure, string brainstorming, string? redraftFeedback, string targetLanguage, string jobName, string outputRoot, CancellationToken)` — 성공하면 새 목차, 실패하면 `currentStructure`를 그대로 돌려준다

- [ ] **Step 1: Write the failing tests**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`의 클래스 안에 네 테스트를 추가한다.

```csharp
        // 총 3회 예산에서 1·2차가 같은 점수로 미달하면 3차는 새 목차로 생성돼야 한다.
        // 목차가 원인인 결함은 3/3만 반복해서는 절대 고쳐지지 않는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_ScoreStalls_RedraftsPlanStructure()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "재설계 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            // 세 회차 모두 60점 미달. 최고점이 갱신되지 않으므로 2차에서 정체가 잡힌다.
            var stalled = new ReviewResult { HasDefects = true, FeedbackComment = "구조 결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(stalled));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "StallJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 1회차 설계 + 정체 후 재설계 = 2회. Job당 1회 상한이므로 3회가 되면 안 된다.
            await aiService.Received(2).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            // 재설계 호출에는 이전 목차와 누적 피드백이 실린다.
            await aiService.Received(1).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                "첫 목차", Arg.Is<string?>(f => f != null && f.Contains("구조 결함")), Arg.Any<CancellationToken>());
            // 마지막 회차는 재설계된 목차로 본문을 만든다.
            await aiService.Received().GenerateConsolidatedBatchPlanAsync(
                "재설계 목차", Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        // 점수가 오르는 중이면 목차는 원인이 아니다. 멀쩡한 구조를 갈아엎지 않는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_ScoreImproves_KeepsPlanStructure()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            // 60점 → 70점. 최고점이 갱신되므로 정체가 아니다.
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 7, ScoreCrud = 7, ScoreInterface = 7, ScoreException = 7, ScoreReadability = 7 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 8, ScoreException = 8, ScoreReadability = 8 }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "ImproveJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            await aiService.Received(1).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        // PlanStructure.md는 언제나 본문을 실제로 만든 목차를 가리켜야 하고,
        // 교체된 목차는 왜 바뀌었는지 추적할 수 있게 남아야 한다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_Redraft_PreservesSupersededStructure()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "재설계 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "PreserveJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            var rawDir = Path.Combine(_consolidatedOutputRoot, "Jobs", "PreserveJob", "raw");
            Assert.Equal("재설계 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.md")));
            Assert.Equal("첫 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.superseded-1.md")));
        }

        // 재수립은 개선 시도이지 필수 단계가 아니다. 실패해도 파이프라인을 죽이지 않는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_RedraftThrows_KeepsExistingStructureAndCompletes()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = "첫 목차" }),
                    _ => throw new InvalidOperationException("재설계 호출 실패"));
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "RedraftFailJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 재설계가 죽어도 계획서는 나온다. 목차는 첫 것을 그대로 쓴다.
            Assert.NotNull(result.Plan);
            await aiService.Received().GenerateConsolidatedBatchPlanAsync(
                "첫 목차", Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunConsolidatedPipelineAsync_Score|FullyQualifiedName~RunConsolidatedPipelineAsync_Redraft"
```

Expected: FAIL — `DraftBatchPlanStructureAsync`가 1회만 호출됨(Received(2) 불일치), `PlanStructure.superseded-1.md` 없음

- [ ] **Step 3: 재수립 헬퍼 2개 추가**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`의 `RunConsolidatedPipelineAsync` 메서드 **뒤에** 다음 두 private 메서드를 추가한다.

```csharp
        /// <summary>
        /// 목차를 다시 세운다. 실패하면 기존 목차를 그대로 돌려준다 — 재수립은 개선
        /// 시도이지 필수 단계가 아니므로 여기서 파이프라인을 죽이지 않는다.
        /// </summary>
        private async Task<string> RedraftPlanStructureAsync(
            string currentStructure,
            string brainstorming,
            string? redraftFeedback,
            string targetLanguage,
            string jobName,
            string outputRoot,
            CancellationToken cancellationToken)
        {
            _userInteraction.NotifyStatus(
                $"[yellow]{jobName}[/] - 재시도가 점수를 개선하지 못해 목차를 다시 설계합니다...");

            string redrafted;
            try
            {
                using (var progressScope = _userInteraction.CreateProgressScope("목차 재설계") ?? NullProgressScope.Instance)
                {
                    // 3단계 중 하나가 아니므로 n/3. 순번을 붙이지 않는다.
                    progressScope.AddTask("redraft", "목차 재설계 중...");
                    var result = await WrapWithProgress(
                        _consolidatorService.DraftBatchPlanStructureAsync(
                            brainstorming, targetLanguage, jobName, _consolidatorEffort,
                            currentStructure, redraftFeedback, cancellationToken),
                        progressScope, "redraft");
                    redrafted = result.Content;
                }
            }
            // 취소는 실패가 아니라 사용자의 지시이므로 전파한다.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _userInteraction.NotifyError($"{jobName} - 목차 재설계 실패 (기존 목차 유지): {ex.Message}");
                return currentStructure;
            }

            // 빈 목차로 본문을 만들면 3/3이 아무 구조 없이 생성된다.
            if (string.IsNullOrWhiteSpace(redrafted))
            {
                _userInteraction.NotifyError($"{jobName} - 목차 재설계 응답이 비어 있어 기존 목차를 유지합니다.");
                return currentStructure;
            }

            await PreserveSupersededStructureAsync(outputRoot, jobName, currentStructure, redrafted, cancellationToken);
            return redrafted;
        }

        /// <summary>
        /// 교체되는 직전 목차를 superseded 파일로 남기고 PlanStructure.md를 최종본으로
        /// 갱신한다. PlanStructure.md는 항상 본문을 실제로 만든 목차를 가리켜야 하므로
        /// 이전 목차를 그 자리에 남기지 않는다.
        /// </summary>
        private static async Task PreserveSupersededStructureAsync(
            string outputRoot,
            string jobName,
            string previousStructure,
            string redrafted,
            CancellationToken cancellationToken)
        {
            var rawDir = System.IO.Path.Combine(outputRoot, "Jobs", jobName, "raw");
            if (!System.IO.Directory.Exists(rawDir))
            {
                System.IO.Directory.CreateDirectory(rawDir);
            }

            // L2 정체로 1회, L3 사용자 요청으로 n회가 가능하므로 번호를 이어 붙인다.
            var index = 1;
            while (System.IO.File.Exists(System.IO.Path.Combine(rawDir, $"PlanStructure.superseded-{index}.md")))
            {
                index++;
            }

            await System.IO.File.WriteAllTextAsync(
                System.IO.Path.Combine(rawDir, $"PlanStructure.superseded-{index}.md"), previousStructure, cancellationToken);
            await System.IO.File.WriteAllTextAsync(
                System.IO.Path.Combine(rawDir, "PlanStructure.md"), redrafted, cancellationToken);
        }
```

- [ ] **Step 4: 상태 변수 3개 추가**

`RunConsolidatedPipelineAsync`의 `:1667` 부근, `string currentPlanStructure = string.Empty;` 바로 아래에 추가한다.

```csharp
            string currentPlanStructure = string.Empty;
            // 재수립 시점에 필요한데 기존에는 if 블록 안에서만 살아 있었다.
            // 목차가 있으면 브레인스토밍도 반드시 있다(둘은 한 몸으로만 실행된다).
            string currentBrainstorming = string.Empty;
            // 정체 판정과 1회 상한은 이 정책이 단독으로 소유한다.
            var redraftPolicy = new StructureRedraftPolicy();
```

- [ ] **Step 5: 브레인스토밍 결과 보관**

`:1698-1702` 부근, `Brainstorming.md`를 쓰는 줄 바로 뒤에 한 줄을 더한다.

```csharp
                            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(rawDir, "Brainstorming.md"), brainstormResult.Content);
                            currentBrainstorming = brainstormResult.Content;
```

- [ ] **Step 6: 정체 신호 수집**

`:1804-1808`의 `TryRecord` 블록을 다음으로 교체한다. 지금은 반환값을 버리고 있다.

```csharp
                // 불합격 여부와 무관하게 후보로 등록한다.
                // 반환값은 "이번 회차가 최고점을 갱신했는가"이며, 그것이 곧 정체 신호다.
                bool improvedThisAttempt = false;
                if (reviewSuccess && l2Result != null)
                {
                    improvedThisAttempt = bestAttempt.TryRecord(attempt, consolidatedPlan, l2Result, finalAiResult);
                }
```

- [ ] **Step 7: 재수립 호출 배선**

`:1815-1827`의 `canRetry` 분기에서 `attempt++;` **바로 앞에** 재수립 판정을 넣는다.

```csharp
                    if (canRetry)
                    {
                        CriticFeedbackLog.Record(feedbackHistory, attempt, l2Result, _criticScoreThreshold);
                        feedbackLog = CriticFeedbackLog.Compose(
                            feedbackHistory,
                            "※ 지시사항: 위 지적사항을 모두 반영하여 본문을 수정하십시오. " +
                            "이전 라운드에서 이미 기준 점수를 통과한 항목의 서술 수준을 낮추지 마십시오. " +
                            "제공된 '원본 명세서(Specifications)'와 위 피드백을 절대적 기준으로 삼으십시오. " +
                            "특히 비즈니스 로직 누락이 지적된 경우, 원본 명세서의 해당 Step(프로시저) 내용을 다시 " +
                            "주의 깊게 정독하여 누락된 비즈니스 로직(UNION, 커서, JOIN, 필터 조건 등)을 완벽히 복원하십시오.");

                        // 재시도가 점수를 못 올리면 원인은 본문이 아니라 목차일 수 있다.
                        // 3/3만 반복해서는 구조가 원인인 결함이 영원히 고쳐지지 않는다.
                        if (redraftPolicy.TryConsume(improvedThisAttempt))
                        {
                            currentPlanStructure = await RedraftPlanStructureAsync(
                                currentPlanStructure, currentBrainstorming, feedbackLog,
                                targetLanguage, jobName, outputRoot, cancellationToken);
                        }

                        attempt++;
                        continue;
                    }
```

- [ ] **Step 8: Run tests to verify they pass**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

Expected: 신규 4건 포함 전건 PASS

- [ ] **Step 9: 취소 정책 검사 통과 확인**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CancellationPolicyTests"
```

Expected: PASS (새 `catch`에 `when (ex is not OperationCanceledException)` 필터가 있어야 통과)

- [ ] **Step 10: Commit**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix: redraft the plan structure when retries stop improving the score"
```

---

### Task 4: L3 구조 피드백 경로

사용자가 구조를 바꾸라고 하면 목차부터 다시 세운다. 이것으로 `STRICTLY adhering` 문구와 사용자 피드백의 충돌이 사라진다.

**Files:**
- Modify: `src/ReSet.Core/Models/HumanReviewResult.cs`
- Modify: `src/ReSet.Cli/ConsoleUserInteraction.cs:155-171`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1911-1925` 부근
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `RedraftPlanStructureAsync(...)` (Task 3)
- Produces: `HumanReviewResult.RedraftStructure` (bool)

- [ ] **Step 1: Write the failing tests**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`에 두 테스트를 추가한다.

```csharp
        // 사용자가 구조를 바꾸라고 하면 목차부터 다시 세운다. 목차를 고정한 채
        // 피드백만 넣으면 "STRICTLY adhering to the Approved Structure"와 충돌한다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3StructuralFeedback_RedraftsBeforeRegenerating()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "1", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "사용자 반영 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            userInteraction.RequestHumanReviewAsync("L3StructJob", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Step 3을 둘로 쪼개라", RedraftStructure = true }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "L3StructJob", "OpenAI", _consolidatedOutputRoot);

            // 사용자 피드백이 재수립 입력으로 실린다.
            await aiService.Received(1).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                "첫 목차", Arg.Is<string?>(f => f != null && f.Contains("Step 3을 둘로 쪼개라")), Arg.Any<CancellationToken>());
            // 재생성 본문은 새 목차를 받는다.
            await aiService.Received().GenerateConsolidatedBatchPlanAsync(
                "사용자 반영 목차", Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        // 오타 수정 같은 피드백에까지 재수립 비용을 물리지 않는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3NonStructuralFeedback_KeepsPlanStructure()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "1", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            userInteraction.RequestHumanReviewAsync("L3PlainJob", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "오타 수정", RedraftStructure = false }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "L3PlainJob", "OpenAI", _consolidatedOutputRoot);

            await aiService.Received(1).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~RunConsolidatedPipelineAsync_L3"
```

Expected: 컴파일 실패 — `HumanReviewResult`에 `RedraftStructure` 정의 없음 (CS0117)

- [ ] **Step 3: HumanReviewResult 확장**

`src/ReSet.Core/Models/HumanReviewResult.cs`의 클래스를 다음으로 교체한다.

```csharp
    public class HumanReviewResult
    {
        public UserDecision Decision { get; set; }
        public string? UserFeedback { get; set; }

        /// <summary>
        /// 이 피드백이 문서 구조(목차)까지 바꾸는가. Decision이 ProvideFeedback일 때만
        /// 의미가 있다.
        ///
        /// 별도 인터페이스 메서드를 두지 않는 이유: 피드백 본문과 그 성격은 함께
        /// 움직이는 값이므로 이미 피드백을 나르는 이 자리에 싣는다.
        /// </summary>
        public bool RedraftStructure { get; set; }
    }
```

- [ ] **Step 4: 오케스트레이터 L3 경로 배선**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`의 `:1918` 부근, `NotifyStatus("...피드백 반영 재생성 중...")` **바로 뒤**이자 `specsCopy` 조립 **앞에** 재수립 분기를 넣는다.

```csharp
                    _userInteraction.NotifyStatus($"[yellow]{jobName}[/] - 피드백 반영 재생성 중...");

                    // 사용자가 구조까지 바꾸라고 했다면 목차부터 다시 세운다.
                    // 목차를 고정한 채로는 3/3의 "STRICTLY adhering to the [Approved
                    // Document Structure & Plan]" 지시와 사용자 피드백이 충돌하고,
                    // STRICTLY가 붙은 쪽이 이겨 사용자 요구가 조용히 무시된다.
                    //
                    // 이 경로는 StructureRedraftPolicy를 거치지 않는다. 사용자의 명시적
                    // 지시를 자동화 예산으로 막지 않는다.
                    if (reviewResult.RedraftStructure)
                    {
                        currentPlanStructure = await RedraftPlanStructureAsync(
                            currentPlanStructure, currentBrainstorming, reviewResult.UserFeedback,
                            targetLanguage, jobName, outputRoot, cancellationToken);
                    }

                    var specsCopy = new System.Collections.Generic.List<(string FileName, string Content)>(specs);
```

`:1959-1965`의 종료 상태 처리(`planReview = null; planOutcome = VerificationOutcome.ReviewNotRun;`)는 그대로 둔다. 목차까지 바뀐 문서라면 더더욱 이전 통과 판정을 자칭해선 안 된다.

- [ ] **Step 5: 콘솔 확인 프롬프트 추가**

`src/ReSet.Cli/ConsoleUserInteraction.cs`의 피드백 입력 이후 반환부(`:155-171`)를 다음으로 교체한다.

```csharp
            var userFeedback = AnsiConsole.Prompt(
                new TextPrompt<string>("보완할 피드백 내용을 구체적으로 기재해 주십시오:")
            );

            if (string.IsNullOrWhiteSpace(userFeedback))
            {
                AnsiConsole.MarkupLine("[yellow]피드백이 비어있어 승인 여부 선택 메뉴로 복귀합니다.[/]");
                return new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = null };
            }

            // 구조를 바꾸는 피드백은 본문만 다시 써서는 반영되지 않는다. 통합 배치
            // 계획서는 목차를 고정한 채 본문을 생성하므로 목차부터 다시 세워야 한다.
            var redraftStructure = AnsiConsole.Confirm(
                "이 피드백이 문서 구조(목차)까지 바꾸나요? (단계 추가/분할/순서 변경 등)", false);

            AnsiConsole.MarkupLine("[blue]사용자 피드백을 적용하여 보완 분석 프로세스를 재가동합니다...[/]");
            return new HumanReviewResult
            {
                Decision = UserDecision.ProvideFeedback,
                UserFeedback = userFeedback,
                RedraftStructure = redraftStructure
            };
```

- [ ] **Step 6: Run tests to verify they pass**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

Expected: 신규 2건 포함 전건 PASS

- [ ] **Step 7: Commit**

```bash
git add src/ReSet.Core/Models/HumanReviewResult.cs src/ReSet.Cli/ConsoleUserInteraction.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix: let L3 structural feedback reach the plan structure"
```

---

### Task 5: 전체 검증과 문서 동기화

**Files:**
- Modify: `docs/architecture.md` (2번 경로 다이어그램)
- Modify: `README.md` (3단계 파이프라인 설명)
- Modify: `AGENTS.md` (Core 서비스 목록)

**Interfaces:**
- Consumes: Task 1~4의 모든 산출물
- Produces: 없음

- [ ] **Step 1: 전체 빌드 경고 확인**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
```

Expected: `8` — 모두 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 CS8600/CS8602. 8보다 크면 이번 변경이 새 경고를 넣은 것이므로 고친 뒤 진행한다.

- [ ] **Step 2: 전체 테스트 실행**

```bash
dotnet test
```

Expected: 전건 PASS. 신규 13건(정책 5, AiService 2, 오케스트레이터 6)이 더해졌으므로 총계는 Task 1 착수 전 기준선보다 정확히 13 커야 한다. 기준선은 작업 시작 전에 `dotnet test`를 한 번 돌려 기록해 둔다 (AGENTS.md는 568건으로 적고 있으나 그 값은 갱신이 밀렸을 수 있다).

- [ ] **Step 3: architecture.md 다이어그램 갱신**

`docs/architecture.md`의 2번 경로 상세 다이어그램에서, 재시도가 항상 P3로만 돌아가던 흐름에 재수립 분기를 넣는다. `Agentic` 서브그래프와 `VerifyB` 서브그래프를 다음으로 교체한다.

```
    %% 2단계: 명세서 경로의 dynamic/단일 분기와 달리 항상 3단계 순차 생성이다
    subgraph Agentic ["2. 3단계 Agentic Workflow (생성)"]
        JobName --> StructCheck{"목차(PlanStructure)가<br/>이미 있는가?"}
        StructCheck -- "아니오 (1회차)" --> P1["1/3. 브레인스토밍<br/>(raw/Brainstorming.md 보존)"]
        P1 --> P2["2/3. 목차 설계<br/>(raw/PlanStructure.md 보존)"]
        P2 --> P3
        StructCheck -- "예 (재시도)" --> P3["3/3. 통합 계획 본문 생성<br/>(목차 재사용, 누적 피드백 최근 3회차 주입)"]
    end

    subgraph VerifyB ["3. 검증 및 종료 상태 판정"]
        P3 --> L1B{"L1 기계 검증 통과?"}
        L1B -- "실패 (재시도 여력 있음)" --> Retry["피드백 세팅 후 3/3 단계만 재생성"]
        Retry --> P3
        L1B -- "실패 (재시도 소진)" --> OutL1["종료 상태: L1 미통과<br/>경고 배너 삽입"]
        L1B -- "성공" --> L2B{"L2 Critic 교차 리뷰<br/>(ReviewConsolidatedPlanAsync)"}
        L2B -- "리뷰 호출 실패" --> OutNR["종료 상태: 리뷰 미수행"]
        L2B -- "결함 (재시도 여력 있음)" --> Stall{"최고점을 갱신했는가?<br/>(BestAttempt.TryRecord)"}
        Stall -- "예 (개선 중)" --> Retry
        Stall -- "아니오 (정체) + 재수립 미소진" --> Redraft["2/3 재실행 — 이전 목차와<br/>누적 피드백을 넣어 구조 재설계<br/>(Job당 1회, 직전 목차는<br/>PlanStructure.superseded-n.md로 보존)"]
        Redraft --> P3
        L2B -- "결함 (재시도 소진)" --> OutQR["종료 상태: 품질 미달<br/>점수·피드백 배너 삽입"]
        L2B -- "통과" --> OutPass["종료 상태: 통과"]
    end
```

같은 파일의 `ExportB` 서브그래프에서 L3 피드백 노드도 갱신한다.

```
            Human -- "2. 피드백" --> Regen["구조 변경 피드백이면 목차부터 재수립,<br/>아니면 목차는 유지한 채 본문만 재생성<br/>L2를 다시 거치지 않으므로<br/>종료 상태를 리뷰 미수행으로 되돌림"]
```

- [ ] **Step 4: README.md 갱신**

`README.md`의 Multi-Step Agentic Workflow 항목을 다음으로 교체한다.

```markdown
     * **Multi-Step Agentic Workflow 적용**: 단일 프롬프트 기반 생성을 넘어, **브레인스토밍(전략 도출) ➔ 목차 및 구조 설계 ➔ 최종 계획서 생성**의 3단계 파이프라인으로 동작하여 심층적인 아키텍처 설계를 자동 수행합니다. (중간 산출물은 `raw/` 디렉터리에 보존) 재시도가 L2 점수를 개선하지 못하면 결함의 원인을 목차로 보고 **구조를 1회 재설계**하며, 교체된 목차는 `raw/PlanStructure.superseded-n.md`로 남습니다.
```

- [ ] **Step 5: AGENTS.md 갱신**

`AGENTS.md`의 Core 서비스 목록에서 `RetryRescue.cs` 항목 바로 뒤에 다음 줄을 추가한다.

```markdown
    *   [StructureRedraftPolicy.cs](./src/ReSet.Core/Services/StructureRedraftPolicy.cs): 통합 배치 계획서 재시도가 점수를 개선하지 못할 때 목차(PlanStructure)를 다시 세울지 결정하는 클래스. **정체의 정의**와 **Job당 1회 상한**을 단독 소유합니다. 정체 판정을 새로 만들지 마십시오 — [BestAttempt](./src/ReSet.Core/Services/BestAttempt.cs)의 `TryRecord`가 이미 "최고점을 갱신했는가"를 판정해 돌려주므로 그 반환값을 그대로 씁니다. 통합 배치 경로는 목차를 재시도 루프 밖에 고정하는데, 그 대가로 목차 자체가 원인인 결함(스텝 누락, 청킹 불가 스텝을 청킹으로 배치)이 재시도로 절대 고쳐지지 않았습니다. 이 클래스가 그 탈출구입니다. L3 사용자가 구조 변경을 요청하는 경로는 이 정책을 거치지 않습니다 — 사용자의 명시적 지시를 자동화 예산으로 막지 않기 때문입니다.
```

- [ ] **Step 6: 문서 정합성 확인**

```bash
grep -n "StructureRedraftPolicy" AGENTS.md docs/architecture.md README.md
grep -n "superseded" README.md docs/architecture.md
```

Expected: `AGENTS.md`에 클래스 항목 1건, `architecture.md`와 `README.md`에 재수립·superseded 언급이 각각 존재

- [ ] **Step 7: Commit**

```bash
git add docs/architecture.md README.md AGENTS.md
git commit -m "docs: document the plan structure redraft escape hatch"
```

## 자체 검토 결과

스펙 대비 커버리지를 확인했다.

| 스펙 요구 | 담당 태스크 |
|---|---|
| `StructureRedraftPolicy` 신설, 정체 정의 = `TryRecord` 반환값, 미갱신 1회 발동, Job당 1회 | Task 1 |
| `DraftBatchPlanStructureAsync` 시그니처 확장, 재수립 프롬프트, 4개 H2 유지, 영문 유지 | Task 2 |
| `CriticFeedbackLog.Compose` 결과를 `redraftFeedback`으로 재사용 | Task 3 Step 7 |
| 브레인스토밍 결과 메서드 스코프 승격 | Task 3 Step 4~5 |
| 진행률 표시(순번 없는 "목차 재설계 중...") | Task 3 Step 3 |
| L1 실패 회차가 판정에 참여하지 않음 | 별도 코드 없음 — `continue`로 L2에 닿지 않아 `TryRecord`가 호출되지 않는다 |
| `HumanReviewResult.RedraftStructure`, 콘솔 확인 프롬프트, L3 배선, 정책 우회 | Task 4 |
| L3 종료 상태를 `ReviewNotRun`으로 유지 | Task 4 Step 4 (기존 코드 보존) |
| `PlanStructure.md` 최종본 유지 + `superseded-n` 보존 | Task 3 Step 3 |
| 예외·공백·취소 흡수 | Task 3 Step 3, Step 9 |
| 문서 3종 동기화 | Task 5 |
| 완료 기준(경고 8건, 전체 테스트) | Task 5 Step 1~2 |

**범위 밖 확인:** 스펙이 제외한 ①(브레인스토밍 원문 전달) ②(목차 단계 존치 실측) ⑤(생성 실패 재시도)는 어느 태스크도 건드리지 않는다. ⑥(빈 응답 방어)은 스펙대로 재수립 경로에서만 방어하며, 1회차 목차 생성의 빈 응답은 그대로 둔다.
