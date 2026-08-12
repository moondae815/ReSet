# Claude 프롬프트 캐시 중단점 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** L2 통합 배치 리뷰의 재생성 회차에서 명세서 블록이 Anthropic 프롬프트 캐시를 읽게 하되, 1회차로 끝나는 잡에는 비용을 추가하지 않는다.

**Architecture:** `ClaudeClient`가 안정 접두사(시스템 프롬프트 + user 프롬프트)의 해시를 기억하고, **두 번째로 보는 접두사에만** `cache_control`을 찍는다. "2회차인가?"와 "이 접두사를 전에 보냈는가?"가 같은 질문이므로 회차 번호를 인자로 전달할 필요가 없고, 따라서 `IAiService` 시그니처가 바뀌지 않는다. `AiService`는 리뷰 프롬프트를 고정 조각(명세서)과 가변 조각(잡 이름 + 계획서 본문)으로 나눠 기존 `volatileUserSuffix` 인자로 넘긴다.

**Tech Stack:** C# / .NET 10, xUnit, NSubstitute, Serilog, `System.Text.Json`, `System.Security.Cryptography.SHA256`

## Global Constraints

- `IAiService`와 `IAiClient`의 시그니처를 바꾸지 않는다. 특히 `ReviewConsolidatedPlanAsync`에 인자를 추가하지 않는다 — NSubstitute 스텁 약 100곳이 인자 5개를 명시하고 있어, 선택적 인자를 붙이면 컴파일은 통과하지만 런타임에 스텁 매칭이 빗나간다.
- `volatileUserSuffix`가 비어 있으면 `ClaudeClient`는 지금의 **평문 문자열** user 메시지를 그대로 보낸다. 표현이 바뀌는 것 자체가 접두사를 바꿔, 접미사 없는 호출들끼리의 캐시를 깬다.
- 시스템 블록의 기존 `cache_control`(`ClaudeClient.cs:77`)은 어떤 경우에도 유지한다.
- TTL은 Anthropic 기본값(5분)을 쓴다. 1시간 TTL은 이번 범위 밖이다.
- 정책 기억의 항목 상한은 64개.
- Claude 경로만 손댄다. Google/Ollama/Z.ai는 범위 밖.
- 정책 판정이 실패하면 예외를 던지지 않고 `false`를 돌려준다 — 중단점 없는 요청은 오늘과 동일한 동작이다.
- 기존 `PromptCacheBreakpointTests`의 테스트 12개가 그대로 통과해야 한다(gpt-5 경로 무변경).
- 빌드 경고 8개 기준선을 유지한다.

**전체 테스트 실행:** `dotnet test ReSet.slnx`
**단일 테스트 실행:** `dotnet test ReSet.slnx --filter "FullyQualifiedName~<TestName>"`

---

## File Structure

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/PromptCacheBreakpointPolicy.cs` (신규) | 안정 접두사를 해시로 기억하고 중단점 여부를 판정한다. 순수하고 결정론적 |
| `src/ReSet.Core/Services/Clients/ClaudeClient.cs` (수정) | 접미사가 있을 때 타입 블록으로 보내고, 정책이 참이면 `cache_control`을 찍는다. 응답의 usage를 읽어 로그로 남긴다 |
| `src/ReSet.Core/Services/AiService.cs` (수정) | L2 리뷰 프롬프트를 고정/가변으로 나누고, 잡 이름을 명세서 뒤로 옮긴다 |
| `tests/ReSet.Core.Tests/PromptCacheBreakpointPolicyTests.cs` (신규) | 정책 단위 테스트 |
| `tests/ReSet.Core.Tests/PromptCacheBreakpointTests.cs` (수정) | Claude 경로 테스트 추가 |
| `tests/ReSet.Core.Tests/ClaudeClientTests.cs` (수정) | usage 파싱 테스트 추가 |
| `tests/ReSet.Core.Tests/AiServiceTests.cs` (수정) | 리뷰 프롬프트 분할 테스트 추가 |

기존 `ClaudeRequestSpyHandler`(`ClaudeClientTests.cs:127`)와 `MockHttpMessageHandler`를 재사용한다. 새 스파이를 만들지 않는다.

---

### Task 1: `PromptCacheBreakpointPolicy`

**Files:**
- Create: `src/ReSet.Core/Services/PromptCacheBreakpointPolicy.cs`
- Test: `tests/ReSet.Core.Tests/PromptCacheBreakpointPolicyTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `public sealed class PromptCacheBreakpointPolicy`, 생성자 `PromptCacheBreakpointPolicy(int capacity = 64)`, 메서드 `bool ShouldMarkBreakpoint(string systemPrompt, string userPrompt)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/PromptCacheBreakpointPolicyTests.cs`를 새로 만든다.

```csharp
using System.Threading.Tasks;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Anthropic은 캐시 쓰기에 1.25배, 읽기에 0.1배를 청구한다. 실측 5개 잡 중 4개가
    /// L2를 1회차에 끝내므로, 첫 전송에 중단점을 찍으면 그 4건은 손해가 확정된다.
    /// 두 번째 전송부터 찍으면 1회차 잡의 비용은 그대로이고 재생성 회차만 이득을 본다.
    /// </summary>
    public class PromptCacheBreakpointPolicyTests
    {
        [Fact]
        public void FirstSightOfAPrefix_DoesNotMarkABreakpoint()
        {
            var policy = new PromptCacheBreakpointPolicy();

            Assert.False(policy.ShouldMarkBreakpoint("SharedSystem", "SharedContext"));
        }

        [Fact]
        public void SecondSightOfTheSamePrefix_MarksABreakpoint()
        {
            var policy = new PromptCacheBreakpointPolicy();

            policy.ShouldMarkBreakpoint("SharedSystem", "SharedContext");

            Assert.True(policy.ShouldMarkBreakpoint("SharedSystem", "SharedContext"));
        }

        // 접두사는 시스템 프롬프트와 user 프롬프트를 함께 본다. 둘 중 하나만 달라도
        // 캐시 접두사가 다르므로 처음 보는 것으로 취급해야 한다.
        [Fact]
        public void ADifferentPrefix_IsTrackedIndependently()
        {
            var policy = new PromptCacheBreakpointPolicy();

            policy.ShouldMarkBreakpoint("SharedSystem", "SharedContext");

            Assert.False(policy.ShouldMarkBreakpoint("SharedSystem", "OtherContext"));
            Assert.False(policy.ShouldMarkBreakpoint("OtherSystem", "SharedContext"));
        }

        // 장시간 프로세스가 SP를 계속 분석해도 기억이 무한히 자라면 안 된다.
        // 축출된 접두사는 처음 보는 것으로 되돌아간다 — 중단점을 찍지 않아 손해가 없다.
        [Fact]
        public void WhenCapacityIsExceeded_TheOldestPrefixIsEvicted()
        {
            var policy = new PromptCacheBreakpointPolicy(capacity: 2);

            policy.ShouldMarkBreakpoint("S", "A");
            policy.ShouldMarkBreakpoint("S", "B");
            policy.ShouldMarkBreakpoint("S", "C");

            Assert.False(policy.ShouldMarkBreakpoint("S", "A"));
            Assert.True(policy.ShouldMarkBreakpoint("S", "C"));
        }

        // StepConcurrency가 4라 동시 호출이 있다. 같은 접두사가 병렬로 들어와도
        // 예외 없이 정확히 한 번만 "처음"으로 판정되어야 한다.
        [Fact]
        public async Task ConcurrentCallsWithTheSamePrefix_YieldExactlyOneFirstSight()
        {
            var policy = new PromptCacheBreakpointPolicy();
            var tasks = new Task<bool>[16];

            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() => policy.ShouldMarkBreakpoint("S", "U"));
            }

            var results = await Task.WhenAll(tasks);

            int firstSights = 0;
            foreach (var r in results)
            {
                if (!r) firstSights++;
            }
            Assert.Equal(1, firstSights);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~PromptCacheBreakpointPolicyTests"`
예상: 컴파일 실패 — `PromptCacheBreakpointPolicy` 형식을 찾을 수 없음

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/PromptCacheBreakpointPolicy.cs`를 새로 만든다.

```csharp
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 프롬프트 캐시 중단점을 찍을지 판정한다.
    ///
    /// Anthropic은 캐시 쓰기에 1.25배(5분 TTL), 읽기에 0.1배를 청구한다. 실측에서 L2
    /// 리뷰는 5개 잡 중 4개가 1회차에 끝났으므로, 무조건 중단점을 찍으면 그 4건에서
    /// 손해가 확정되어 표본 전체로는 순손실이 난다. 두 번째 전송부터 찍으면 1회차 잡의
    /// 비용은 그대로이고 재생성 회차만 이득을 본다.
    ///
    /// "2회차인가?"는 "이 접두사를 전에 보냈는가?"와 같은 질문이므로, 회차 번호를
    /// 파이프라인에서 전달받지 않고 여기서 판정한다. 덕분에 IAiService가 바뀌지 않는다.
    ///
    /// 시계를 쓰지 않아 결정론적이다. 캐시 TTL(5분)을 넘겨 재전송되면 쓴 캐시가 읽히지
    /// 못하고 버려지는데, 그 손실은 감수한다 — 회차 간격을 예측해 회피하려는 시도는
    /// 과적합이다.
    /// </summary>
    public sealed class PromptCacheBreakpointPolicy
    {
        public const int DefaultCapacity = 64;

        private readonly ConcurrentDictionary<string, byte> _seen = new();
        private readonly ConcurrentQueue<string> _insertionOrder = new();
        private readonly int _capacity;

        public PromptCacheBreakpointPolicy(int capacity = DefaultCapacity)
        {
            _capacity = capacity > 0 ? capacity : DefaultCapacity;
        }

        /// <summary>
        /// 처음 보는 접두사면 false(중단점 없음), 이미 본 접두사면 true를 돌려준다.
        /// 판정에 실패하면 false를 돌려준다 — 중단점 없는 요청은 오늘과 동일한 동작이라
        /// 파이프라인을 멈추지 않는다.
        /// </summary>
        public bool ShouldMarkBreakpoint(string systemPrompt, string userPrompt)
        {
            try
            {
                var key = ComputeKey(systemPrompt, userPrompt);

                if (!_seen.TryAdd(key, 0))
                {
                    return true;
                }

                _insertionOrder.Enqueue(key);
                while (_insertionOrder.Count > _capacity && _insertionOrder.TryDequeue(out var evicted))
                {
                    _seen.TryRemove(evicted, out _);
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "프롬프트 캐시 중단점 판정 실패 - 중단점 없이 진행합니다.");
                return false;
            }
        }

        /// <summary>
        /// 접두사 본문이 아니라 해시만 들고 있는다. 두 프롬프트 사이에 NUL을 넣어
        /// 경계를 고정한다 — 넣지 않으면 ("AB", "C")와 ("A", "BC")가 같은 키가 된다.
        /// </summary>
        private static string ComputeKey(string systemPrompt, string userPrompt)
        {
            var bytes = Encoding.UTF8.GetBytes($"{systemPrompt}\u0000{userPrompt}");
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~PromptCacheBreakpointPolicyTests"`
예상: PASS 5건

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/PromptCacheBreakpointPolicy.cs tests/ReSet.Core.Tests/PromptCacheBreakpointPolicyTests.cs
git commit -m "feat: judge cache breakpoints by whether the prefix was sent before"
```

---

### Task 2: `ClaudeClient`가 타입 블록으로 보내고 중단점을 찍는다

**Files:**
- Modify: `src/ReSet.Core/Services/Clients/ClaudeClient.cs`
- Test: `tests/ReSet.Core.Tests/PromptCacheBreakpointTests.cs`

**Interfaces:**
- Consumes: `PromptCacheBreakpointPolicy.ShouldMarkBreakpoint(string, string)` (Task 1)
- Produces: `ClaudeClient` 생성자에 선택적 5번째 인자 `PromptCacheBreakpointPolicy? cacheBreakpointPolicy = null` 추가. 생략하면 **인스턴스마다 새 정책**을 만든다(정적 공유 아님 — 테스트 간 오염을 막는다)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/PromptCacheBreakpointTests.cs` 끝의 클래스 닫는 중괄호 앞에 아래를 추가한다.

```csharp
        private static (ClaudeClient Client, ClaudeRequestSpyHandler Spy) NewClaudeClient()
        {
            var spy = new ClaudeRequestSpyHandler(
                @"{""content"":[{""type"":""text"",""text"":""ok""}]}");
            var client = new ClaudeClient(
                new HttpClient(spy), "test_api_key", "https://api.anthropic.com", "claude-sonnet-5");
            return (client, spy);
        }

        private static JsonElement UserContentOf(ClaudeRequestSpyHandler spy) =>
            JsonDocument.Parse(spy.LastRequestContent!).RootElement
                .GetProperty("messages")[0].GetProperty("content");

        // 접미사가 없으면 표현을 바꾸지 않는다. 평문 문자열을 블록 배열로 바꾸는 것
        // 자체가 접두사를 바꿔, 접미사 없는 호출들끼리의 캐시를 깨기 때문이다.
        [Fact]
        public async Task Claude_WithoutAVolatileSuffix_KeepsThePlainStringContent()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f);

            Assert.Equal(JsonValueKind.String, UserContentOf(spy).ValueKind);
            Assert.Equal("SharedContext", UserContentOf(spy).GetString());
        }

        // 첫 전송에는 중단점을 찍지 않는다. 캐시 쓰기가 1.25배라, 1회차에 끝나는 잡
        // (실측 5건 중 4건)에서 손해가 확정되기 때문이다.
        [Fact]
        public async Task Claude_OnTheFirstSend_SplitsIntoBlocksWithoutACacheBreakpoint()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");

            var content = UserContentOf(spy);
            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("SharedContext", content[0].GetProperty("text").GetString());
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("PlanBody v1", content[1].GetProperty("text").GetString());
            Assert.False(content[0].TryGetProperty("cache_control", out _));
        }

        // 재생성 회차: 같은 접두사를 다시 보내면 공유 블록에 중단점을 찍는다.
        [Fact]
        public async Task Claude_OnTheSecondSend_MarksTheSharedBlockWithCacheControl()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");
            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v2");

            var content = UserContentOf(spy);
            Assert.Equal(
                "ephemeral",
                content[0].GetProperty("cache_control").GetProperty("type").GetString());
        }

        // 가변 블록에 찍으면 그 지점의 접두사가 매번 달라 캐시가 살지 않고
        // 쓰기 비용만 늘어난다.
        [Fact]
        public async Task Claude_NeverMarksTheVolatileBlock()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");
            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v2");

            Assert.False(UserContentOf(spy)[1].TryGetProperty("cache_control", out _));
        }

        // 시스템 블록의 중단점은 이미 동작 중이고(실측 1,818 히트), user 블록이 달라진
        // 호출에서도 최소한의 폴백 접두사 역할을 한다. 어떤 경우에도 유지한다.
        [Fact]
        public async Task Claude_AlwaysKeepsTheSystemBlockBreakpoint()
        {
            var (client, spy) = NewClaudeClient();

            await client.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");

            var system = JsonDocument.Parse(spy.LastRequestContent!).RootElement
                .GetProperty("system")[0];
            Assert.Equal(
                "ephemeral",
                system.GetProperty("cache_control").GetProperty("type").GetString());
        }

        // 클라이언트마다 기억이 독립이어야 테스트가 서로를 오염시키지 않고,
        // Actor/Critic처럼 서로 다른 클라이언트가 접두사를 공유하지도 않는다.
        [Fact]
        public async Task Claude_MemoryIsPerClientInstance()
        {
            var (client1, _) = NewClaudeClient();
            var (client2, spy2) = NewClaudeClient();

            await client1.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");
            await client2.ChatAsync("SharedSystem", "SharedContext", 0.1f,
                volatileUserSuffix: "PlanBody v1");

            Assert.False(UserContentOf(spy2)[0].TryGetProperty("cache_control", out _));
        }
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~PromptCacheBreakpointTests"`
예상: `Claude_WithoutAVolatileSuffix_KeepsThePlainStringContent`는 통과하고, 나머지 5건은 FAIL — 현재는 `ClaudeClient.cs:38`에서 접미사를 합쳐 버려 `content`가 항상 문자열이므로 `GetArrayLength()`가 던진다

- [ ] **Step 3: 필드와 생성자를 고친다**

`src/ReSet.Core/Services/Clients/ClaudeClient.cs`의 필드 선언(17행 뒤)에 추가한다.

```csharp
        private readonly PromptCacheBreakpointPolicy _cacheBreakpointPolicy;
```

생성자(22행)를 바꾼다.

```csharp
        public ClaudeClient(
            HttpClient httpClient,
            string apiKey,
            string endpoint,
            string modelName,
            PromptCacheBreakpointPolicy? cacheBreakpointPolicy = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiKey = apiKey;
            _modelName = modelName;
            // 기본값은 정적 공유가 아니라 인스턴스마다 새 정책이다. 공유하면 서로 다른
            // 역할(Actor/Critic)이나 테스트끼리 접두사 기억이 섞인다.
            _cacheBreakpointPolicy = cacheBreakpointPolicy ?? new PromptCacheBreakpointPolicy();
```

이하 `var ep = ...`부터는 그대로 둔다.

- [ ] **Step 4: 병합을 걷어내고 메시지 빌더를 넣는다**

`ChatAsync`의 38행을 **삭제**한다.

```csharp
            userPrompt = PromptComposition.MergeVolatileSuffix(userPrompt, volatileUserSuffix);
```

`enableThinking` 계산(68행) 바로 뒤, `systemBlocks` 선언 앞에 추가한다.

```csharp
            var userMessages = BuildUserMessages(systemPrompt, userPrompt, volatileUserSuffix);
```

`ChatAsync` 아래에 메서드를 추가한다.

```csharp
        /// <summary>
        /// user 메시지를 만든다. 가변 접미사가 있을 때만 타입 블록 배열로 보내고, 그
        /// 접두사를 전에 보낸 적이 있으면 공유 블록에 cache_control을 찍는다.
        ///
        /// 접미사가 없으면 평문 문자열을 그대로 유지한다 — 표현이 바뀌는 것 자체가
        /// 접두사를 바꿔, 접미사 없는 호출들끼리의 캐시를 깨기 때문이다.
        /// OpenAiClient가 같은 이유로 같은 판단을 한다.
        /// </summary>
        private object[] BuildUserMessages(string systemPrompt, string userPrompt, string? volatileUserSuffix)
        {
            if (string.IsNullOrWhiteSpace(volatileUserSuffix))
            {
                return new object[] { new { role = "user", content = (object)userPrompt } };
            }

            object sharedBlock = _cacheBreakpointPolicy.ShouldMarkBreakpoint(systemPrompt, userPrompt)
                ? new { type = "text", text = userPrompt, cache_control = new { type = "ephemeral" } }
                : new { type = "text", text = userPrompt };

            var blocks = new object[]
            {
                sharedBlock,
                new { type = "text", text = volatileUserSuffix }
            };

            return new object[] { new { role = "user", content = (object)blocks } };
        }
```

- [ ] **Step 5: 네 곳의 messages 구성을 교체한다**

`ChatAsync` 안에서 `messages`를 만드는 곳이 네 군데다. 전부 `userMessages`로 바꾼다.

104행(4세대 이상 + thinking, `Dictionary<string, object>` 경로):

```csharp
                        { "messages", userMessages },
```

137-140행, 159-162행, 172-175행(익명 형식 경로) — 각각 아래로 바꾼다:

```csharp
                        messages = userMessages,
```

- [ ] **Step 6: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~PromptCacheBreakpointTests"`
예상: PASS 18건(기존 gpt-5 12건 + 신규 Claude 6건)

- [ ] **Step 7: 전체 테스트로 회귀를 확인한다**

실행: `dotnet test ReSet.slnx`
예상: 실패 0건. 특히 기존 `ClaudeClientTests` 6건이 그대로 통과해야 한다 — 접미사 없는 호출이라 평문 문자열 경로를 탄다

- [ ] **Step 8: 커밋한다**

```bash
git add src/ReSet.Core/Services/Clients/ClaudeClient.cs tests/ReSet.Core.Tests/PromptCacheBreakpointTests.cs
git commit -m "feat: give Claude a cache breakpoint on the shared prefix it has seen before"
```

---

### Task 3: L2 리뷰 프롬프트를 고정/가변으로 나눈다

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs` (`ReviewConsolidatedPlanAsync`, 2417-2436행 부근)
- Test: `tests/ReSet.Core.Tests/AiServiceTests.cs`

**Interfaces:**
- Consumes: `IAiClient.ChatAsync(..., string? volatileUserSuffix, ...)` (기존 계약)
- Produces: 없음. `ReviewConsolidatedPlanAsync`의 시그니처는 바뀌지 않는다

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests.cs`의 클래스 안에 추가한다. gpt-4o는 Chat Completions 경로라 접미사를 합쳐 보내므로, `LastRequestBody`에서 합쳐진 최종 프롬프트를 그대로 볼 수 있다.

```csharp
        // 캐시는 접두사 일치다. 잡 이름이 명세서보다 앞에 있으면 잡이 바뀔 때마다
        // 뒤따르는 명세서 전량(실측 481KB)이 무효가 된다.
        [Fact]
        public async Task ReviewConsolidatedPlanAsync_PutsTheJobNameAfterTheSpecifications()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "명세서고유표시")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": false}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "## 계획서", "Job_고유표시");

            var body = mockHandler.LastRequestBody;
            Assert.True(
                body.IndexOf("명세서고유표시") < body.IndexOf("Job_고유표시"),
                "명세서가 잡 이름보다 앞에 와야 캐시 접두사가 잡 간에 공유된다.");
        }

        // 계획서 본문은 회차마다 재생성되므로 가변 조각에 있어야 한다. 고정 조각에
        // 들어가면 접두사가 매 회차 달라져 캐시가 살지 않는다.
        [Fact]
        public async Task ReviewConsolidatedPlanAsync_SendsThePlanBodyAsTheVolatileSuffix()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "명세서 내용")
            };
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-4o");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "{\"HasDefects\": false}" });
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "계획서고유표시", "Test_Job");

            await client.Received(1).ChatAsync(
                Arg.Any<string>(),
                Arg.Is<string>(stable => stable.Contains("명세서 내용")
                                         && !stable.Contains("계획서고유표시")),
                Arg.Any<float>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(suffix => suffix != null && suffix.Contains("계획서고유표시")),
                Arg.Any<CancellationToken>());
        }
```

```csharp
        // 제공자 간 동일성: 메시지를 나눌 수 없는 경로는 PromptComposition이 이어 붙인
        // 한 덩어리를 받고, Claude는 같은 두 조각을 블록으로 받는다. 두 조각을 합친
        // 결과가 세 부분을 원래 순서대로 담고 있어야 내용이 같다고 말할 수 있다.
        [Fact]
        public async Task ReviewConsolidatedPlanAsync_MergedPromptKeepsEveryPartInOrder()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "명세서고유표시")
            };
            string? stable = null;
            string? suffix = null;
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-4o");
            client.ChatAsync(
                    Arg.Any<string>(),
                    Arg.Do<string>(s => stable = s),
                    Arg.Any<float>(),
                    Arg.Any<string?>(),
                    Arg.Do<string?>(v => suffix = v),
                    Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "{\"HasDefects\": false}" });
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "계획서고유표시", "Job_고유표시");

            var merged = PromptComposition.MergeVolatileSuffix(stable!, suffix!);
            Assert.True(
                merged.IndexOf("명세서고유표시") < merged.IndexOf("Job_고유표시"),
                "합친 결과에서도 명세서가 잡 이름보다 앞이어야 한다.");
            Assert.True(
                merged.IndexOf("Job_고유표시") < merged.IndexOf("계획서고유표시"),
                "합친 결과에서도 잡 이름이 계획서 본문보다 앞이어야 한다.");
        }
```

`AiServiceTests.cs` 상단에 아래 using이 없으면 추가한다.

```csharp
using System.Threading;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services.Clients;
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~ReviewConsolidatedPlanAsync_PutsTheJobName"`
예상: FAIL — 현재는 잡 이름이 맨 앞이므로 `IndexOf` 비교가 거짓

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~ReviewConsolidatedPlanAsync_SendsThePlanBody"`
예상: FAIL — 현재는 `volatileUserSuffix`를 넘기지 않아 `Received(1)`이 매칭되지 않음

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~ReviewConsolidatedPlanAsync_MergedPrompt"`
예상: FAIL — `suffix`가 null이라 `MergeVolatileSuffix`가 고정 조각만 돌려주고, 잡 이름을
찾지 못해 `IndexOf`가 -1이 된다

- [ ] **Step 3: 프롬프트 조립을 고친다**

`AiService.cs`에서 아래 블록(2417-2436행 부근)을 찾는다.

```csharp
            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"Unified Batch Job Name: {jobName}");
            userPrompt.AppendLine();
            userPrompt.AppendLine("[Provided Stored Procedure Specifications]");

            foreach (var spec in specs)
            {
                userPrompt.AppendLine($"---");
                userPrompt.AppendLine($"Filename: {spec.FileName}");
                userPrompt.AppendLine(spec.Content);
                userPrompt.AppendLine();
            }

            userPrompt.AppendLine("[Consolidated Batch Modernization Plan Markdown]");
            userPrompt.AppendLine(planMarkdown);
            userPrompt.AppendLine();
            userPrompt.AppendLine("Please review the consolidated plan and output the JSON result.");
```

전체를 아래로 교체한다.

```csharp
            // 프롬프트를 캐시 접두사(고정)와 회차별 가변부로 나눈다.
            //
            // 명세서는 회차 간 바이트가 같고 실측 481KB로 가변부보다 크다. 잡 이름은
            // 잡마다 달라지므로 앞에 두면 그 한 줄이 뒤의 명세서 전량을 무효로 만든다 —
            // 캐시는 접두사 일치이기 때문이다. 계획서 본문은 회차마다 재생성되므로
            // 애초에 캐시 대상이 아니다.
            var stablePrompt = new StringBuilder();
            stablePrompt.AppendLine("[Provided Stored Procedure Specifications]");

            foreach (var spec in specs)
            {
                stablePrompt.AppendLine($"---");
                stablePrompt.AppendLine($"Filename: {spec.FileName}");
                stablePrompt.AppendLine(spec.Content);
                stablePrompt.AppendLine();
            }

            var volatileSuffix = new StringBuilder();
            volatileSuffix.AppendLine($"Unified Batch Job Name: {jobName}");
            volatileSuffix.AppendLine();
            volatileSuffix.AppendLine("[Consolidated Batch Modernization Plan Markdown]");
            volatileSuffix.AppendLine(planMarkdown);
            volatileSuffix.AppendLine();
            volatileSuffix.AppendLine("Please review the consolidated plan and output the JSON result.");
```

- [ ] **Step 4: 로그와 호출을 고친다**

바로 아래의 `Log.Debug`와 `ChatAsync` 호출을 찾는다.

```csharp
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt.ToString());

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), 0.1f, effort, cancellationToken: cancellationToken);
```

아래로 교체한다. 로그는 모델이 실제로 받는 합쳐진 프롬프트를 남긴다 — RawContext가 사실과 어긋나지 않게.

```csharp
            var mergedPrompt = PromptComposition.MergeVolatileSuffix(
                stablePrompt.ToString(), volatileSuffix.ToString());
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, mergedPrompt);

            var aiResult = await _aiClient.ChatAsync(
                systemPrompt,
                stablePrompt.ToString(),
                0.1f,
                effort,
                volatileUserSuffix: volatileSuffix.ToString(),
                cancellationToken: cancellationToken);
```

- [ ] **Step 5: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~ReviewConsolidatedPlanAsync"`
예상: 신규 3건 PASS. 기존 `ReviewConsolidatedPlanAsync_*` 테스트 6건도 그대로 통과해야 한다 — 프롬프트 문구를 검사하는 테스트들은 합쳐진 본문을 보므로 순서만 바뀐 것에 영향받지 않는다

- [ ] **Step 6: 전체 테스트를 돌린다**

실행: `dotnet test ReSet.slnx`
예상: 실패 0건

- [ ] **Step 7: 커밋한다**

```bash
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/AiServiceTests.cs
git commit -m "fix: stop the job name from invalidating the specs it precedes"
```

---

### Task 4: 응답의 캐시 사용량을 읽어 로그로 남긴다

**Files:**
- Modify: `src/ReSet.Core/Services/Clients/ClaudeClient.cs`
- Test: `tests/ReSet.Core.Tests/ClaudeClientTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `public static (int Input, int CacheWrite, int CacheRead) ReadUsage(JsonElement root)` — `ClaudeClient`의 정적 메서드. 테스트 프로젝트에 `InternalsVisibleTo`가 없어 `internal`로는 검증할 수 없으므로 public으로 둔다

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/ClaudeClientTests.cs`의 `ClaudeClientTests` 클래스 안에 추가한다.

```csharp
        // 캐시 미스는 오류를 내지 않고 조용히 지나간다. usage를 읽지 않으면 중단점이
        // 실제로 동작하는지 확인할 방법이 없다.
        [Fact]
        public void ReadUsage_ExtractsInputAndCacheCounters()
        {
            var json = @"{""usage"":{""input_tokens"":357560,
                                     ""cache_creation_input_tokens"":1818,
                                     ""cache_read_input_tokens"":0}}";

            using var doc = JsonDocument.Parse(json);
            var usage = ClaudeClient.ReadUsage(doc.RootElement);

            Assert.Equal(357560, usage.Input);
            Assert.Equal(1818, usage.CacheWrite);
            Assert.Equal(0, usage.CacheRead);
        }

        // usage 필드가 없어도 응답 처리는 계속되어야 한다.
        [Fact]
        public void ReadUsage_WithoutAUsageObject_ReturnsZeros()
        {
            using var doc = JsonDocument.Parse(@"{""content"":[]}");
            var usage = ClaudeClient.ReadUsage(doc.RootElement);

            Assert.Equal(0, usage.Input);
            Assert.Equal(0, usage.CacheWrite);
            Assert.Equal(0, usage.CacheRead);
        }

        // 필드 일부만 오는 경우에도 던지지 않는다.
        [Fact]
        public void ReadUsage_WithPartialFields_FillsTheRestWithZero()
        {
            using var doc = JsonDocument.Parse(@"{""usage"":{""cache_read_input_tokens"":1818}}");
            var usage = ClaudeClient.ReadUsage(doc.RootElement);

            Assert.Equal(0, usage.Input);
            Assert.Equal(0, usage.CacheWrite);
            Assert.Equal(1818, usage.CacheRead);
        }
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~ReadUsage"`
예상: 컴파일 실패 — `ClaudeClient`에 `ReadUsage` 정의가 없음

- [ ] **Step 3: 파서를 구현한다**

`ClaudeClient.cs`의 `BuildUserMessages` 아래에 추가한다.

```csharp
        /// <summary>
        /// 응답의 usage에서 입력/캐시 쓰기/캐시 읽기 토큰 수를 읽는다.
        /// 캐시 미스는 오류를 내지 않으므로, 이 값이 중단점이 실제로 동작하는지
        /// 확인할 수 있는 유일한 신호다. 필드가 없거나 형식이 다르면 0으로 둔다.
        /// </summary>
        public static (int Input, int CacheWrite, int CacheRead) ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return (0, 0, 0);
            }

            return (
                ReadCounter(usage, "input_tokens"),
                ReadCounter(usage, "cache_creation_input_tokens"),
                ReadCounter(usage, "cache_read_input_tokens"));

            static int ReadCounter(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.TryGetInt32(out var count)
                    ? count
                    : 0;
        }
```

- [ ] **Step 4: 호출부를 넣는다**

`ChatAsync`의 응답 파싱에서 error 검사 블록(211-216행) 바로 뒤, `content` 검사 앞에 추가한다.

```csharp
                var usage = ReadUsage(root);
                Log.Information(
                    "Claude 토큰 사용량 - 입력: {Input}, 캐시 쓰기: {CacheWrite}, 캐시 읽기: {CacheRead}",
                    usage.Input, usage.CacheWrite, usage.CacheRead);
```

- [ ] **Step 5: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --filter "FullyQualifiedName~ReadUsage"`
예상: PASS 3건

- [ ] **Step 6: 전체 테스트와 빌드 경고를 확인한다**

실행: `dotnet test ReSet.slnx`
예상: 실패 0건

실행: `dotnet build ReSet.slnx 2>&1 | tail -3`
예상: `경고 8개`, `오류 0개`

- [ ] **Step 7: 커밋한다**

```bash
git add src/ReSet.Core/Services/Clients/ClaudeClient.cs tests/ReSet.Core.Tests/ClaudeClientTests.cs
git commit -m "feat: log Claude cache counters so a miss stops being invisible"
```

---

### Task 5: 문서 동기화

**Files:**
- Modify: `docs/architecture.md`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: Task 1-4의 결과물
- Produces: 없음

- [ ] **Step 1: 최종 테스트 개수를 확인한다**

실행: `dotnet test ReSet.slnx 2>&1 | tail -1`

출력의 "통과:" 뒤 숫자를 적어둔다. 기준선은 1318이었고, 이 계획은 테스트 17건(Task 1의
5건 + Task 2의 6건 + Task 3의 3건 + Task 4의 3건)을 추가하므로 1335가 되어야 한다.

- [ ] **Step 2: `docs/architecture.md` 2.2 테이블에 행을 추가한다**

`MechanicalValidator` 행이 있는 테이블에서 알파벳/맥락 순서에 맞는 위치에 아래 행을 넣는다. 기존 행들의 열 구성과 `<br/>` 사용 형식을 그대로 따른다.

```markdown
| [PromptCacheBreakpointPolicy](../src/ReSet.Core/Services/PromptCacheBreakpointPolicy.cs) | 프롬프트 캐시 중단점 판정<br/>안정 접두사의 해시를 기억해 두 번째 전송부터 `cache_control`을 찍는다. 캐시 쓰기가 1.25배라 첫 전송에 찍으면 1회차로 끝나는 잡에서 손해가 확정된다. |
```

- [ ] **Step 3: `docs/architecture.md` §4에 메커니즘 문단을 추가한다**

§4의 마지막 항목 뒤에 추가한다.

```markdown
### Claude 프롬프트 캐시 중단점

Anthropic API에는 암묵적 캐싱이 없어 `cache_control`을 명시해야 한다. L2 통합 배치 리뷰는
명세서 전문(실측 481KB)을 회차마다 다시 보내는데, 이 블록은 회차 간 바이트가 같아 캐시
대상이다. 반면 계획서 본문은 회차마다 재생성되므로 대상이 아니다.

중단점을 무조건 찍지 않는 이유는 가격 구조다. 캐시 쓰기는 1.25배, 읽기는 0.1배이고,
실측에서 L2는 5개 잡 중 4개가 1회차에 끝났다. 무조건 찍으면 그 4건에서 손해가 확정되어
표본 전체로는 순손실이 난다. `PromptCacheBreakpointPolicy`는 접두사를 전에 보낸 적이
있을 때만 중단점을 찍어, 1회차 잡의 비용을 그대로 두고 재생성 회차만 이득을 취한다.

같은 이유로 잡 이름은 명세서 뒤에 놓는다. 캐시는 접두사 일치라, 잡마다 달라지는 한 줄이
앞에 있으면 뒤따르는 명세서 전량이 무효가 된다.
```

- [ ] **Step 4: `AGENTS.md`에서 거짓이 된 문장을 고친다**

`AGENTS.md` 39-40행은 Claude를 "메시지를 나눌 수 없는 경로"로 분류한다. 이 변경으로
거짓이 되므로 반드시 함께 고쳐야 한다.

실행: `grep -n "메시지를 나눌 수 없는 경로" AGENTS.md`

40행에서 목록의 `Claude`를 뺀다.

```
(전) 메시지를 나눌 수 없는 경로(Chat Completions·Claude·Google·Ollama·CLI)가 전부 이곳을 씁니다.
(후) 메시지를 나눌 수 없는 경로(Chat Completions·Google·Ollama·CLI)가 전부 이곳을 씁니다.
```

39행의 "Responses API 경로는 이것을 별개 메시지로 떼어" 부분도 Claude를 포함하도록 고친다.

```
(전) Responses API 경로는 이것을 별개 메시지로 떼어 캐시 접두사를 지키고, 그 외 경로는
(후) Responses API와 Claude 경로는 이것을 별개 메시지·블록으로 떼어 캐시 접두사를 지키고, 그 외 경로는
```

- [ ] **Step 5: `AGENTS.md` 파일 바로가기에 항목을 추가한다**

40행의 `PromptComposition.cs` 항목 바로 앞에 같은 들여쓰기와 형식으로 추가한다.

```markdown
    *   [PromptCacheBreakpointPolicy.cs](./src/ReSet.Core/Services/PromptCacheBreakpointPolicy.cs): 프롬프트 캐시 중단점을 찍을지 판정하는 클래스. 안정 접두사의 해시를 기억해 두 번째 전송부터 `cache_control`을 찍습니다. 캐시 쓰기가 1.25배라 첫 전송에 찍으면 L2가 1회차로 끝나는 잡에서 손해가 확정됩니다.
```

- [ ] **Step 6: `AGENTS.md`의 테스트 개수를 갱신한다**

304행의 `개의 단위 테스트` 앞 숫자를 Step 1에서 확인한 값으로 바꾼다.

실행: `grep -n "개의 단위 테스트" AGENTS.md`

- [ ] **Step 7: 링크 유효성을 검증한다**

```bash
grep -o '](\.\./src/[^)]*)' docs/architecture.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "docs/$p" ] || echo "BROKEN architecture.md: $p"; done

grep -ho '](\./[^)]*)' AGENTS.md README.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "$p" ] || echo "BROKEN: $p"; done
```

예상: 출력 없음

- [ ] **Step 8: 커밋한다**

```bash
git add docs/architecture.md AGENTS.md
git commit -m "docs: record why the cache breakpoint waits for the second send"
```

---

## 사람이 직접 확인해야 하는 것

이 계획의 테스트는 **요청이 어떤 모양으로 조립되는지**까지만 보장한다. Anthropic이 실제로
캐시를 돌려주는지는 검증하지 않는다.

실제 배치 잡을 L2가 두 번 이상 도는 조건으로 한 번 돌리고, 로그에서 `Claude 토큰 사용량`
줄을 확인한다.

- 1회차 호출: `캐시 쓰기: 1818`(시스템 프롬프트분), `캐시 읽기: 0`
- 2회차 호출: `캐시 쓰기`가 명세서 블록 크기만큼 — 실측 기준 수만~수십만 토큰
- 3회차 호출: `캐시 읽기 > 0`

2회차와 3회차 사이 간격이 5분을 넘으면 3회차가 미스한다. 이는 알려진 손실이며 결함이
아니다(설계 문서 「오류 처리」 참조).
