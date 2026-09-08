# OpenRouter 라우팅 정책화 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** OpenRouter 백엔드를 이름 목록이 아니라 양자화 성질로 고르게 해서, `ByModel` 손표를 6항목에서 1항목으로 줄이고 모델을 바꿔도 404로 죽지 않게 한다.

**Architecture:** `OpenRouterRoutingOptions`에 `Quantizations`를 더해 요청 `provider.quantizations`로 보낸다. 설정의 `Default`는 `Order`를 버리고 `Quantizations: ["fp8"]` + `AllowFallbacks: true`를 갖는다. OpenRouter의 필터 순서가 `Quantization → … → Fallback`이므로, 폴백이 열려 있어도 fp8 밖으로 나가지 않는다. `Order`는 프롬프트 캐시 고착성을 사는 용도로만 `ByModel`에 남는다.

**Tech Stack:** C# / .NET 10, xUnit, `Microsoft.Extensions.Configuration.Json`

**Spec:** `docs/superpowers/specs/2026-09-08-openrouter-routing-policy-design.md`

## Global Constraints

- 게이트 합격 기준은 **실패 0 · 건너뜀 0 · 경고 0**이다. 절대 통과 수는 게이트로 쓰지 않는다.
- 코퍼스 심링크 `output/`이 없으면 건너뜀이 난다. 작업 시작 전 확인한다.
- 두 설정 파일(`src/ReSet.Cli/appsettings.json`, `src/ReSet.Validator.Cli/appsettings.json`)은 OpenRouter 구획에 **같은 값**을 갖는다.
- `ReSet.Core`에 설정 패키지 의존을 더하지 않는다. `OpenRouterRoutingOptions.Parse`는 계속 문자열/문자열 열거만 받는다.
- 커밋은 경로를 못박는다 — 각 태스크의 커밋 단계에 적힌 그대로,
  `git add <그 태스크의 경로들>` 후 `git commit -F "$msg" -- <같은 경로들>`.
  옵션은 반드시 `--` **앞**에 온다(뒤에 두면 전부 경로로 읽혀 `-m`이 먹지 않는다).
  이 저장소는 병행 세션과 인덱스를 공유하므로 `git add -A`와 `git stash`를 쓰지 않는다.
- 커밋 메시지 끝에 붙인다:
  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01FdfL2gWozqPqCyAMP7dyr9
  ```
- 실측 근거값(2026-09-08): fp8 필터 `29→13` · `sort`는 `order`를 대체하지 못함(고유 공급자 4종 대 1종, 비용 +33%) · `require_parameters` 좁힘 0 · `glm-5.3` 1순위 교체로 −18.5%.

---

### Task 1: `Quantizations`를 라우팅 옵션에 더한다

**Files:**
- Modify: `src/ReSet.Core/Services/Clients/OpenRouterRoutingOptions.cs`
- Test: `tests/ReSet.Core.Tests/OpenRouterRoutingOptionsTests.cs` (없으면 생성)

**Interfaces:**
- Consumes: 없음 (첫 태스크)
- Produces: `OpenRouterRoutingOptions.Quantizations` (`IReadOnlyList<string>?`), 그리고 네 번째 선택 인자가 붙은
  `static OpenRouterRoutingOptions? Parse(IEnumerable<string>? order, string? allowFallbacks, string? requireParameters, IEnumerable<string>? quantizations = null)`.
  선택 인자로 두는 것은 Task 3까지 두 `Program.cs`가 고치지 않아도 컴파일되게 하기 위해서다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/OpenRouterRoutingOptionsTests.cs`가 이미 있으면 아래 세 개를 그 클래스에 더한다. 없으면 파일째 만든다.

```csharp
using System;
using ReSet.Core.Services.Clients;
using Xunit;

namespace ReSet.Core.Tests
{
    public class OpenRouterRoutingOptionsQuantizationTests
    {
        // 양자화만 지정해도 라우팅이 성립해야 한다. IsEmpty가 이것을 세지 않으면
        // Parse가 null을 돌려주고, 요청에서 provider 구획이 통째로 사라진다.
        [Fact]
        public void Parse_WithOnlyQuantizations_ReturnsOptions()
        {
            var options = OpenRouterRoutingOptions.Parse(
                order: null, allowFallbacks: null, requireParameters: null,
                quantizations: new[] { "fp8" });

            Assert.NotNull(options);
            Assert.Equal(new[] { "fp8" }, options!.Quantizations);
            Assert.Null(options.Order);
        }

        // 공백 항목은 Order와 같은 규칙으로 걸러야 한다 - 설정에서 배열 원소를
        // 지우면 빈 문자열이 남는데, 그것이 그대로 나가면 400이 난다.
        [Fact]
        public void Parse_WithBlankQuantizationEntries_DropsThem()
        {
            var options = OpenRouterRoutingOptions.Parse(
                order: null, allowFallbacks: null, requireParameters: null,
                quantizations: new[] { " fp8 ", "", "   " });

            Assert.NotNull(options);
            Assert.Equal(new[] { "fp8" }, options!.Quantizations);
        }

        // 모델별 항목은 Order만 적는다. 그때 Default의 양자화 하한이 살아남지 않으면
        // 그 모델 호출에서만 fp4로 샐 길이 조용히 열린다.
        [Fact]
        public void Merge_PerModelOrderOnly_InheritsQuantizationsFromDefault()
        {
            var defaults = new OpenRouterRoutingOptions
            {
                Quantizations = new[] { "fp8" },
                AllowFallbacks = true
            };
            var perModel = new OpenRouterRoutingOptions
            {
                Order = new[] { "gmicloud/fp8", "baseten/fp8" }
            };

            var merged = OpenRouterRoutingOptions.Merge(defaults, perModel);

            Assert.NotNull(merged);
            Assert.Equal(new[] { "gmicloud/fp8", "baseten/fp8" }, merged!.Order);
            Assert.Equal(new[] { "fp8" }, merged.Quantizations);
            Assert.True(merged.AllowFallbacks);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~OpenRouterRoutingOptionsQuantizationTests
```
기댓값: 컴파일 실패 — `Quantizations`가 없고 `Parse`가 인자 4개를 받지 않는다.

- [ ] **Step 3: 최소 구현**

`OpenRouterRoutingOptions.cs`에서 세 곳을 고친다.

`Order` 속성 아래에 더한다:
```csharp
        /// <summary>
        /// 허용할 양자화(예: <c>fp8</c>). 이름이 아니라 성질로 후보를 좁히므로
        /// 백엔드 목록을 손으로 적지 않아도 품질 하한이 지켜진다. OpenRouter의
        /// 필터 순서가 <c>Quantization → … → Fallback</c>이라(실측 2026-09-08:
        /// fp8 지정 시 29→13), 이 값이 있으면 <c>AllowFallbacks</c>를 열어도
        /// 목록 밖 fp4·unknown 백엔드로 넘어가지 않는다.
        /// </summary>
        public IReadOnlyList<string>? Quantizations { get; init; }
```

`IsEmpty`를 교체한다:
```csharp
        public bool IsEmpty =>
            (Order is null || Order.Count == 0)
            && (Quantizations is null || Quantizations.Count == 0)
            && !AllowFallbacks.HasValue && !RequireParameters.HasValue;
```

`Parse`의 시그니처와 본문을 교체한다:
```csharp
        public static OpenRouterRoutingOptions? Parse(
            IEnumerable<string>? order,
            string? allowFallbacks,
            string? requireParameters,
            IEnumerable<string>? quantizations = null)
        {
            var cleanedOrder = order?
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => o.Trim())
                .ToArray();

            var cleanedQuantizations = quantizations?
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q.Trim())
                .ToArray();

            var options = new OpenRouterRoutingOptions
            {
                Order = cleanedOrder is { Length: > 0 } ? cleanedOrder : null,
                Quantizations = cleanedQuantizations is { Length: > 0 } ? cleanedQuantizations : null,
                AllowFallbacks = bool.TryParse(allowFallbacks, out var af) ? af : null,
                RequireParameters = bool.TryParse(requireParameters, out var rp) ? rp : null
            };

            return options.IsEmpty ? null : options;
        }
```

`Merge`의 `merged` 초기화에 한 줄 더한다 (`Order = …` 바로 아래):
```csharp
                Quantizations = overrideOptions.Quantizations ?? baseOptions.Quantizations,
```

- [ ] **Step 4: 통과를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~OpenRouterRoutingOptionsQuantizationTests
```
기댓값: 3개 통과.

- [ ] **Step 5: 커밋**

```bash
msg="$(git rev-parse --git-dir)/RESET_COMMIT_MSG"
cat > "$msg" <<'MSG'
feat(OpenRouter): 라우팅 선호가 허용 양자화를 싣는다

백엔드를 이름 목록으로 고르면 모델이 바뀔 때마다 손으로 다시 적어야 하고,
그 목록은 조용히 썩는다. 성질로 고르면 그 일이 사라진다.

Merge는 항목 단위 그대로다 - ByModel이 Order만 적어도 Default의 양자화
하한이 살아남아야 한다. 통째 대체로 바꾸면 그 모델 호출에서만 하한이
조용히 사라진다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01FdfL2gWozqPqCyAMP7dyr9
MSG
git add src/ReSet.Core/Services/Clients/OpenRouterRoutingOptions.cs tests/ReSet.Core.Tests/OpenRouterRoutingOptionsQuantizationTests.cs
git commit -F "$msg" -- src/ReSet.Core/Services/Clients/OpenRouterRoutingOptions.cs tests/ReSet.Core.Tests/OpenRouterRoutingOptionsQuantizationTests.cs
```

---

### Task 2: 요청에 `provider.quantizations`를 실어 보낸다

**Files:**
- Modify: `src/ReSet.Core/Services/Clients/OpenRouterClient.cs:135-153`
- Test: `tests/ReSet.Core.Tests/OpenRouterClientTests.cs`

**Interfaces:**
- Consumes: Task 1의 `OpenRouterRoutingOptions.Quantizations`
- Produces: 요청 본문 `provider.quantizations` (문자열 배열). 없으면 키 자체를 넣지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`OpenRouterClientTests` 클래스에 더한다. 스파이 핸들러 `OpenRouterRequestSpyHandler`는 같은 파일 342줄에 이미 있다.

```csharp
        // 양자화 하한이 요청에 실제로 실리는지 본다. 실리지 않으면 설정만 바뀌고
        // 라우팅은 그대로여서, fp4 백엔드로 가는 것이 조용히 계속된다.
        [Fact]
        public async Task ChatAsync_WithQuantizations_ShouldSendQuantizations()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "z-ai/glm-5.3",
                routing: new OpenRouterRoutingOptions
                {
                    Quantizations = new[] { "fp8" },
                    AllowFallbacks = true
                });

            await client.ChatAsync("System", "User", 0.2f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            var provider = doc.RootElement.GetProperty("provider");
            var quantizations = provider.GetProperty("quantizations");
            Assert.Equal(1, quantizations.GetArrayLength());
            Assert.Equal("fp8", quantizations[0].GetString());
            Assert.True(provider.GetProperty("allow_fallbacks").GetBoolean());
            Assert.False(provider.TryGetProperty("order", out _));
        }

        // 지정하지 않았으면 키를 넣지 않아야 한다. 빈 배열을 보내면 OpenRouter가
        // "허용 양자화 없음"으로 읽어 후보가 0이 될 수 있다.
        [Fact]
        public async Task ChatAsync_WithoutQuantizations_ShouldOmitTheKey()
        {
            var spy = new OpenRouterRequestSpyHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
            using var http = new HttpClient(spy);
            var client = new OpenRouterClient(http, "sk-or-test", "", "z-ai/glm-5.3",
                routing: new OpenRouterRoutingOptions { Order = new[] { "gmicloud/fp8" } });

            await client.ChatAsync("System", "User", 0.2f);

            using var doc = JsonDocument.Parse(spy.LastRequestContent!);
            Assert.False(doc.RootElement.GetProperty("provider").TryGetProperty("quantizations", out _));
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~OpenRouterClientTests
```
기댓값: `ChatAsync_WithQuantizations_ShouldSendQuantizations`가 `quantizations` 속성 없음으로 실패. 두 번째는 이미 통과한다(아직 아무것도 안 실으므로) — 그것이 정상이다.

- [ ] **Step 3: 최소 구현**

`OpenRouterClient.cs`의 `if (_routing.Order is { Count: > 0 })` 블록 바로 뒤에 넣는다:

```csharp
                if (_routing.Quantizations is { Count: > 0 })
                {
                    preferences.Add("quantizations", _routing.Quantizations);
                }
```

- [ ] **Step 4: 통과를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~OpenRouterClientTests
```
기댓값: 전부 통과.

- [ ] **Step 5: 되돌림으로 검사가 사는지 확인한다**

방금 넣은 세 줄을 주석 처리하고 다시 돌린다.

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~ChatAsync_WithQuantizations
```
기댓값: **실패.** 초록이면 그 검사는 아무것도 안 지키고 있는 것이므로 멈추고 검사를 고친다. 확인 뒤 주석을 되돌린다.

- [ ] **Step 6: 커밋**

```bash
msg="$(git rev-parse --git-dir)/RESET_COMMIT_MSG"
cat > "$msg" <<'MSG'
feat(OpenRouter): 요청이 허용 양자화를 함께 보낸다

지정하지 않았으면 키를 넣지 않는다. 빈 배열을 보내면 OpenRouter가
"허용 양자화 없음"으로 읽어 후보가 0이 될 수 있다.

되돌림으로 검사가 사는 것을 확인했다 - 이 세 줄을 지우면
ChatAsync_WithQuantizations_ShouldSendQuantizations가 실패한다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01FdfL2gWozqPqCyAMP7dyr9
MSG
git add src/ReSet.Core/Services/Clients/OpenRouterClient.cs tests/ReSet.Core.Tests/OpenRouterClientTests.cs
git commit -F "$msg" -- src/ReSet.Core/Services/Clients/OpenRouterClient.cs tests/ReSet.Core.Tests/OpenRouterClientTests.cs
```

---

### Task 3: 두 CLI가 설정에서 `Quantizations`를 읽는다

**Files:**
- Modify: `src/ReSet.Cli/Program.cs:65-79` (`ParseRoutingBlock`)
- Modify: `src/ReSet.Validator.Cli/Program.cs:76-89` (`ParseRoutingBlock`)
- Test: `tests/ReSet.Core.Tests/CliProviderSettingsTests.cs`

**Interfaces:**
- Consumes: Task 1의 `Parse(order, allowFallbacks, requireParameters, quantizations)`
- Produces: `ReadOpenRouterRouting`이 돌려주는 옵션에 `Quantizations`가 채워진다. 시그니처는 그대로다.

두 파일의 `ParseRoutingBlock`은 같은 로직의 복사본이다. **둘 다 고쳐야 한다** — 한쪽만 고치면 검증기에서만 양자화 하한이 조용히 사라진다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`CliProviderSettingsTests`에 더한다.

```csharp
        // 설정의 Quantizations가 실제로 읽히는지 본다. 읽히지 않으면 설정 파일만
        // 바뀌고 요청은 그대로다.
        [Fact]
        public void ReadOpenRouterRouting_WithConfiguredQuantizations_ReadsThem()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Default:Quantizations:0"] = "fp8",
                    ["AiSettings:Providers:OpenRouter:Routing:Default:AllowFallbacks"] = "true",
                    ["AiSettings:Providers:OpenRouter:Routing:ByModel:z-ai/glm-5.3:Order:0"] = "gmicloud/fp8"
                })
                .Build();

            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "z-ai/glm-5.3");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "gmicloud/fp8" }, routing!.Order);
            Assert.Equal(new[] { "fp8" }, routing.Quantizations);
            Assert.True(routing.AllowFallbacks);
        }

        // 검증기 CLI는 같은 로직의 복사본을 갖는다. 한쪽만 고치면 검증기 호출에서만
        // 양자화 하한이 사라져, fp4 백엔드가 L2 리뷰를 조용히 맡게 된다.
        [Fact]
        public void ValidatorCli_ReadOpenRouterRouting_ReadsQuantizationsLikeAnalyzerCli()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiSettings:Providers:OpenRouter:Routing:Default:Quantizations:0"] = "fp8",
                    ["AiSettings:Providers:OpenRouter:Routing:Default:AllowFallbacks"] = "true"
                })
                .Build();

            var routing = ReSet.Validator.Cli.Program.ReadOpenRouterRouting(
                configuration, "OpenRouter", "z-ai/glm-5.3");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "fp8" }, routing!.Quantizations);
            Assert.True(routing.AllowFallbacks);
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~ReadsQuantizations
```
기댓값: 둘 다 실패 — `routing.Quantizations`가 null이다.

- [ ] **Step 3: 최소 구현**

두 파일의 `ParseRoutingBlock` 본문에서, `order`를 만든 뒤에 넣고 `Parse` 호출을 바꾼다.

`src/ReSet.Cli/Program.cs`:
```csharp
            var quantizations = section.GetSection("Quantizations").GetChildren()
                .Select(child => child.Value ?? string.Empty)
                .ToArray();

            return ReSet.Core.Services.Clients.OpenRouterRoutingOptions.Parse(
                order, section["AllowFallbacks"], section["RequireParameters"], quantizations);
```

`src/ReSet.Validator.Cli/Program.cs` (형식만 다르다):
```csharp
            var quantizations = section.GetSection("Quantizations").GetChildren()
                .Select(child => child.Value ?? string.Empty)
                .ToArray();

            return OpenRouterRoutingOptions.Parse(
                order, section["AllowFallbacks"], section["RequireParameters"], quantizations);
```

또한 두 파일의 `ParseRoutingBlock` XML 주석 "라우팅 항목 세 개를 한 구획에서 읽는다"를 "라우팅 항목 네 개를 한 구획에서 읽는다"로 고친다.

- [ ] **Step 4: 통과를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~ReadsQuantizations
```
기댓값: 2개 통과.

- [ ] **Step 5: 커밋**

```bash
msg="$(git rev-parse --git-dir)/RESET_COMMIT_MSG"
cat > "$msg" <<'MSG'
feat(설정): 두 CLI가 라우팅 구획의 Quantizations를 읽는다

ParseRoutingBlock은 두 Program.cs에 같은 로직의 복사본으로 있다. 한쪽만
고치면 검증기 호출에서만 양자화 하한이 사라져, fp4 백엔드가 L2 리뷰를
조용히 맡게 된다. 그래서 검사도 양쪽을 따로 부른다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01FdfL2gWozqPqCyAMP7dyr9
MSG
git add src/ReSet.Cli/Program.cs src/ReSet.Validator.Cli/Program.cs tests/ReSet.Core.Tests/CliProviderSettingsTests.cs
git commit -F "$msg" -- src/ReSet.Cli/Program.cs src/ReSet.Validator.Cli/Program.cs tests/ReSet.Core.Tests/CliProviderSettingsTests.cs
```

---

### Task 4: 설정을 교체하고 손표를 폐기한다

**Files:**
- Modify: `src/ReSet.Cli/appsettings.json:70-208`
- Modify: `src/ReSet.Validator.Cli/appsettings.json` (OpenRouter 구획 전체)
- Modify: `tests/ReSet.Core.Tests/CliProviderSettingsTests.cs:76-171`

**Interfaces:**
- Consumes: Task 3의 설정 읽기
- Produces: `Routing:Default`에 `Quantizations`·`AllowFallbacks`만 있고 `Order`가 없는 설정. `ByModel`은 `z-ai/glm-5.3` 한 항목.

이 태스크는 **설정과 테스트를 한 커밋에서 함께 바꾼다.** 설정만 바꾸면 기존 손표 검사 셋이 즉시 빨개지고, 테스트만 바꾸면 검사가 비는 구간이 생긴다.

- [ ] **Step 1: 설정 구획을 교체한다**

`src/ReSet.Cli/appsettings.json`에서 70줄(`// OpenRouter(https://openrouter.ai)…` 주석 시작)부터 208줄(`},` — `"OpenRouter"` 객체를 닫는 줄)까지를 아래로 통째 교체한다. `src/ReSet.Validator.Cli/appsettings.json`의 대응 구획도 **똑같이** 교체한다.

```jsonc
      // OpenRouter(https://openrouter.ai). 수백 종 모델을 한 엔드포인트로 중개한다.
      // [주의] ModelName은 네임스페이스가 붙은 OpenRouter 모델 ID로 적는다
      //   (예: "z-ai/glm-5.3"). 네임스페이스를 빼면 어느 벤더로 풀릴지가
      //   OpenRouter의 판단에 달려 재현성이 없다.
      // 라우팅의 근거와 후보 조회법: docs/architecture/4.5-multi-llm-provider.md
      "OpenRouter": {
        "ApiKey": "",                            // https://openrouter.ai/settings/keys 에서 발급 (필수)
        "Endpoint": "https://openrouter.ai/api/v1", // /chat/completions는 자동으로 붙는다
        "NumCtx": null,                          // 지정하면 max_tokens로 전달된다. 비워 두면 모델 기본값
        "Routing": {
          "Default": {
            "Quantizations": [ "fp8" ],  // 품질 하한. fp4·unknown을 원천 배제한다
            "AllowFallbacks": true       // 밖이 이미 fp8뿐이라 열어 둔다 - 미등록 모델도 죽지 않는다
          },
          "ByModel": {
            // 캐시 고착성 전용. 백엔드가 여럿인 모델에만 적는다.
            "z-ai/glm-5.3": { "Order": [ "gmicloud/fp8", "baseten/fp8" ] }
          }
        }
      },
```

- [ ] **Step 2: 두 파일이 같은 값을 갖는지 확인한다**

```bash
python3 - <<'PY'
import json,sys
def load(p):
    raw=open(p,encoding='utf-8').read()
    out=[];s=False;e=False;i=0
    while i<len(raw):
        c=raw[i]
        if s:
            out.append(c)
            if e: e=False
            elif c=='\\': e=True
            elif c=='"': s=False
            i+=1
        elif c=='"': s=True;out.append(c);i+=1
        elif c=='/' and i+1<len(raw) and raw[i+1]=='/':
            while i<len(raw) and raw[i]!='\n': i+=1
        else: out.append(c);i+=1
    return json.loads(''.join(out))
a=load('src/ReSet.Cli/appsettings.json')['AiSettings']['Providers']['OpenRouter']['Routing']
b=load('src/ReSet.Validator.Cli/appsettings.json')['AiSettings']['Providers']['OpenRouter']['Routing']
print("일치" if a==b else "불일치"); print(json.dumps(a,ensure_ascii=False,indent=1))
assert a==b
PY
```
기댓값: `일치`, 그리고 `Default`에 `Order` 키가 없다.

- [ ] **Step 3: 손표 검사 셋을 지운다**

`tests/ReSet.Core.Tests/CliProviderSettingsTests.cs`에서 아래를 **주석까지 통째로** 삭제한다.

- `AppSettings_PinOpenRouterBackendsPerModel` (78줄)
- `PinnedBackendsPerModel` 상수 (101줄)
- `AppSettings_PerModelRouting_KeepsFallbacksClosed` (124줄)
- `AppSettings_PerModelRouting_DeclaresNothingBeyondPinnedTable` (148줄)
- `ValidatorCli_ReadOpenRouterRouting_ResolvesByModelLikeAnalyzerCli` (228줄, `deepseek-v4-pro-0813`을 쓴다 — Task 3에서 더한 `ValidatorCli_ReadOpenRouterRouting_ReadsQuantizationsLikeAnalyzerCli`가 그 자리를 대신한다)

**남긴다:** `AppSettings_DeclareOpenRouterProvider`, `AppSettings_OpenRouterRouting_NeverBlocksFallbacksWithoutOrder`(폴백을 닫으면서 목록을 비우는 조합을 계속 막는다), `AppSettings_DeclareAllThreeCliProviders`, `AppSettings_CliProvidersDeclareNoApiKey`, 그리고 `ReadOpenRouterRouting_*` 단위 검사들.

- [ ] **Step 4: 모양 불변식 검사를 쓴다**

같은 파일에 더한다.

```csharp
        // 품질 하한은 이름이 아니라 성질로 지킨다. 이 선언이 사라지면 fp4·unknown
        // 백엔드가 말없이 후보에 들어오고, 명세서 품질이 조용히 갈린다.
        // Default가 Order를 가지면 안 되는 이유는 따로다 - 그것은 "다른 모델의
        // 목록"을 남의 모델에 물려주는 자리이고, 그렇게 물려받은 백엔드가 그 모델을
        // 서빙하지 않으면 404 "No endpoints found"로 즉시 죽는다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_OpenRouterDefault_DeclaresQuantizationFloorWithoutOrder(string relativePath)
        {
            var configuration = Load(relativePath);
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(configuration, "OpenRouter");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "fp8" }, routing!.Quantizations);
            Assert.True(routing.AllowFallbacks, "미등록 모델이 404로 죽지 않으려면 폴백이 열려 있어야 합니다");
            Assert.Null(routing.Order);
        }

        // ByModel 키는 네임스페이스가 붙은 OpenRouter 모델 ID여야 한다. 네임스페이스를
        // 빼면 어느 벤더로 풀릴지가 OpenRouter의 판단에 달려 재현성이 없다.
        // 낡은 항목의 자동 탐지는 이 저장소에서 불가능하다 - OpenRouter 모델은
        // gitignore된 appsettings.local.json에만 살아, 커밋된 설정과 대조할 수 없다.
        // 표가 한 줄이라 사람이 지우는 것으로 감당한다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_ByModelEntries_AreNamespacedWithNonEmptyOrder(string relativePath)
        {
            var byModel = Load(relativePath)
                .GetSection("AiSettings:Providers:OpenRouter:Routing:ByModel");

            Assert.True(byModel.Exists(), $"{relativePath}에 ByModel 구획이 없습니다");

            foreach (var entry in byModel.GetChildren())
            {
                Assert.Contains("/", entry.Key);

                var order = entry.GetSection("Order").GetChildren()
                    .Select(child => child.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();

                Assert.True(
                    order.Length > 0,
                    $"{relativePath}의 ByModel:{entry.Key}에 Order가 없습니다 - " +
                    "Order를 적지 않을 항목이면 항목째 지우십시오(Default가 그 일을 합니다)");
            }
        }

        // 모델별 항목은 Order만 적는다. 설정 파일에서도 그 상속이 실제로 성립하는지
        // 본다 - 성립하지 않으면 그 모델 호출에서만 양자화 하한이 조용히 사라진다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_PerModelRouting_InheritsQuantizationFloor(string relativePath)
        {
            var routing = ReSet.Cli.Program.ReadOpenRouterRouting(
                Load(relativePath), "OpenRouter", "z-ai/glm-5.3");

            Assert.NotNull(routing);
            Assert.Equal(new[] { "gmicloud/fp8", "baseten/fp8" }, routing!.Order);
            Assert.Equal(new[] { "fp8" }, routing.Quantizations);
            Assert.True(routing.AllowFallbacks);
        }
```

- [ ] **Step 5: 전체 게이트를 돌린다**

```bash
dotnet test
```
기댓값: **실패 0 · 건너뜀 0 · 경고 0.** 건너뜀이 나면 코퍼스 심링크 `output/`을 확인한다.

- [ ] **Step 6: 커밋**

```bash
msg="$(git rev-parse --git-dir)/RESET_COMMIT_MSG"
cat > "$msg" <<'MSG'
refactor(설정): 백엔드를 이름이 아니라 양자화로 고른다

ByModel 6항목 → 1항목, 주석 약 120줄 → 4줄.

Default에서 Order를 없애고 Quantizations:[fp8] + AllowFallbacks:true로 바꾼다.
OpenRouter의 필터 순서가 Quantization → … → Fallback이라 폴백을 열어도 fp8
밖으로 나가지 않는다(실측 2026-09-08: fp8 지정 시 후보 29→13, /endpoints의
fp8 개수와 일치). 이것이 "ByModel에 없는 모델은 404로 죽는다"를 구조적으로
닫는다 - 이제 캐시 고정만 잃고 돈다.

Order는 남는다. sort:"price"가 그것을 대체한다는 문서의 주장은 실측에서
기각됐다(고유 공급자 4종 대 1종, 호출당 비용 +33%).

PinnedBackendsPerModel 손표를 폐기한다. 설정을 설정의 사본으로 확인하는
절반 순환이었고, 모델이 바뀔 때 손댈 다섯째 자리였다. 잃는 것이 없다는
근거: 그 표는 현 설정의 썩은 자리 셋(도달 불가 deepseek 2순위, 서빙을
멈춘 sail-research, "본사 하나뿐"이 실제로는 28곳인 glm-5.3)을 하나도
잡지 못했다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01FdfL2gWozqPqCyAMP7dyr9
MSG
git add src/ReSet.Cli/appsettings.json src/ReSet.Validator.Cli/appsettings.json tests/ReSet.Core.Tests/CliProviderSettingsTests.cs
git commit -F "$msg" -- src/ReSet.Cli/appsettings.json src/ReSet.Validator.Cli/appsettings.json tests/ReSet.Core.Tests/CliProviderSettingsTests.cs
```

---

### Task 5: 문서 셋을 산출물과 맞춘다

**Files:**
- Modify: `README.md:298-318`
- Modify: `docs/architecture/4.5-multi-llm-provider.md:11`

**Interfaces:**
- Consumes: Task 4의 설정 모양
- Produces: 없음 (문서)

`AGENTS.md`와 `docs/architecture.md`에는 바이트 예산이 걸려 있지만 이 둘에는 없다(`tests/ReSet.Core.Tests/documentation-budget-baseline.txt` 확인). 줄 길이 제한도 그 둘에만 적용된다.

- [ ] **Step 1: README의 표를 교체한다**

`README.md` 298-318줄(`// 후보와 단가 조회(인증 불필요):` 주석부터 `Routing` 객체를 닫는 줄까지)을 Task 4 Step 1의 `Routing` 블록과 같은 값으로 교체한다. README는 설정 예시이므로 주석은 한 줄로 줄인다:

```jsonc
        // 라우팅의 근거와 후보 조회법: docs/architecture/4.5-multi-llm-provider.md
        "Routing": {
          "Default": {
            "Quantizations": [ "fp8" ],  // 품질 하한. fp4·unknown을 원천 배제합니다
            "AllowFallbacks": true       // 미등록 모델도 죽지 않습니다(밖이 이미 fp8뿐)
          },
          "ByModel": {                   // 캐시 고착성 전용. 백엔드가 여럿인 모델에만
            "z-ai/glm-5.3": { "Order": [ "gmicloud/fp8", "baseten/fp8" ] }
          }
        }
```

- [ ] **Step 2: `4.5`의 라우팅 문단을 다시 쓴다**

`docs/architecture/4.5-multi-llm-provider.md:11`의 `OpenRouterClient` 항목에서 `Providers:OpenRouter:Routing`을 설명하는 부분을 교체한다. 담을 것(사양서 8절):

- 왜 고정하는가 — 실측 2026-09-08, `z-ai/glm-5.2` 약 8,100토큰 접두사: `order` 못박기는 고유 공급자 1종·호출당 $0.00258, `sort:"price"`는 4종·$0.00386(**+33%**). `sort`는 좁히되 고정하지 않으므로 `order`를 대체하지 못한다.
- `Quantizations`가 무엇을 대체했는가 — fp8 지정 시 후보가 `29→13`으로 줄고(`/endpoints`의 fp8 개수와 일치), 이름으로 지목해도 필터가 이긴다. 백엔드를 손으로 고르던 일의 절반이 사라졌다.
- `AllowFallbacks`를 여는 근거 — 필터 순서가 `Quantization → … → Fallback`이라 폴백이 이미 fp8 안에서만 일어난다. 그래서 `ByModel`에 없는 모델은 죽지 않고 캐시 고정만 잃는다.
- `RequireParameters`를 비워 두는 근거 — `reasoning: {effort}` 동반 시 잰 5개 모델 전부에서 좁힘 0.
- 후보 조회: `curl https://openrouter.ai/api/v1/models/<author>/<slug>/endpoints`(인증 불필요). 보는 칸은 `tag`·`quantization`·`pricing.input_cache_read`·`context_length`·`uptime_last_30m`. **정상 상태 비용은 `pricing.prompt`가 아니라 `input_cache_read`가 지배한다.** `sort`도 `max_price`도 그 축을 겨냥할 수 없다(`max_price` 키는 `prompt`·`completion`·`request`·`image`·`audio`뿐이다).
- `routing_funnel` 판독법 — 도달 불가능한 `order`로 일부러 404를 내면 응답 `metadata.routing_funnel`에 필터 단계별 잔존 엔드포인트 수가 실린다. 어느 필터가 후보를 얼마나 줄이는지 요청 한 건으로 볼 수 있다.
- 함정 둘 — `/endpoints`의 `supports_implicit_caching: false`여도 캐시가 걸린다(`glm-5.2` 33곳 전부 false인데 `cache_control` 없이 적중, 비용 −81%). 그리고 처음 보는 접두사에 적중을 보고하는 백엔드가 있다(StreamLake, 2회 관측).

기존 문단에 있던 `PinnedBackendsPerModel` 언급과 모델별 목록 서술은 지운다. 그 표는 더 이상 없다.

- [ ] **Step 3: 죽은 참조가 남지 않았는지 훑는다**

```bash
grep -rn "PinnedBackendsPerModel\|sail-research\|deepseek-v4-pro-0813\|deepseek-v4-flash-0731\|glm-5.3-flash" \
  README.md docs/architecture/ src/ tests/ --include="*.md" --include="*.cs" --include="*.json" \
  | grep -v "/obj/\|/bin/"
```
기댓값: 출력 없음. `docs/known-defects.md`와 `src/ReSet.Core/Services/LocalVariableDeclarationExtractor.cs`의 `deepseek-v4-pro-0813` 언급은 **라우팅이 아니라 과거 모델 교체 서사**이므로 위 경로에 포함하지 않았고 손대지 않는다.

- [ ] **Step 4: 게이트를 다시 돌린다**

```bash
dotnet test
```
기댓값: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 5: 커밋**

```bash
msg="$(git rev-parse --git-dir)/RESET_COMMIT_MSG"
cat > "$msg" <<'MSG'
docs: 라우팅 서술을 성질 기반 설정에 맞춘다

appsettings.json에서 뺀 근거 서사가 갈 자리를 만든다. 새 문서를 만들지 않고
4.5의 기존 문단을 다시 쓴다 - 같은 내용의 사본이 이미 거기 있었다.

routing_funnel 판독법과 함정 둘(메타데이터가 캐시 가능성을 말하지 않는다 ·
처음 보는 접두사에 적중을 보고하는 백엔드가 있다)을 새로 적는다.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01FdfL2gWozqPqCyAMP7dyr9
MSG
git add README.md docs/architecture/4.5-multi-llm-provider.md
git commit -F "$msg" -- README.md docs/architecture/4.5-multi-llm-provider.md
```

---

### Task 6: 실호출로 확인한다 (사람 승인 필요 · 유료)

**Files:** 없음 (검증만)

**Interfaces:**
- Consumes: Task 4의 설정
- Produces: 없음

이 태스크는 **OpenRouter API에 유료 호출을 낸다.** 시작 전에 사람에게 묻는다. 앞선 실험 전체가 약 $0.35였고 이 태스크는 그보다 훨씬 작다(요청 3건, 작은 프롬프트).

`appsettings.local.json`의 `OpenRouter:ApiKey`를 쓴다. **키를 출력하지 않는다.**

- [ ] **Step 1: 사람에게 승인을 받는다**

물을 것: "실호출 3건(약 $0.001)을 내도 되는가". 거부되면 Task 6을 건너뛰고 그 사실을 보고에 남긴다 — 조용히 통과시키지 않는다.

- [ ] **Step 2: `glm-5.3`이 새 설정대로 라우팅되는지 본다**

설정이 요청에 실렸는지는 요청 로그가 아니라 **응답의 `provider` 필드**로 판정한다.

```bash
# ApiKey는 appsettings.local.json에서 읽고 화면에 내지 않는다
curl -sS https://openrouter.ai/api/v1/chat/completions \
  -H "Authorization: Bearer $OPENROUTER_KEY" -H "Content-Type: application/json" \
  -d '{"model":"z-ai/glm-5.3","messages":[{"role":"user","content":"Reply with: ok"}],
       "max_tokens":8,"usage":{"include":true},
       "provider":{"quantizations":["fp8"],"allow_fallbacks":true,
                   "order":["gmicloud/fp8","baseten/fp8"]}}' \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print('provider=',d.get('provider'))"
```
기댓값: `provider= GMICloud`. 다른 곳이면 `Order`의 슬러그 형식(`tag` 대 base)을 의심하고 사양서 10절의 되돌림 조건을 따른다.

- [ ] **Step 3: 미등록 모델이 죽지 않는지 본다 — 이것이 이 변경의 핵심 증거다**

`ByModel`에 없는 모델을 `Default`만으로 부른다.

```bash
curl -sS https://openrouter.ai/api/v1/chat/completions \
  -H "Authorization: Bearer $OPENROUTER_KEY" -H "Content-Type: application/json" \
  -d '{"model":"z-ai/glm-5.2","messages":[{"role":"user","content":"Reply with: ok"}],
       "max_tokens":8,"usage":{"include":true},
       "provider":{"quantizations":["fp8"],"allow_fallbacks":true}}' \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print('provider=',d.get('provider'),'| error=',d.get('error'))"
```
기댓값: 200이고 `provider`가 채워져 있다. 404 `No endpoints found`가 나면 1-2를 닫지 못한 것이므로 멈춘다.

- [ ] **Step 4: 폴백이 fp8 밖으로 새지 않는지 본다**

Step 3을 6회 반복해 응답 `provider`를 모은다. 나온 이름이 전부 그 모델의 fp8 목록 안에 있어야 한다:

```bash
curl -sS "https://openrouter.ai/api/v1/models/z-ai/glm-5.2/endpoints" \
  | python3 -c "import json,sys; print(sorted(e['tag'] for e in json.load(sys.stdin)['data']['endpoints'] if e['quantization']=='fp8'))"
```
fp8이 아닌 이름이 하나라도 나오면 사양서 10절의 되돌림 조건에 걸린 것이다 — `AllowFallbacks`를 `false`로 되돌리고 `ByModel`을 다시 채운다.

- [ ] **Step 5: 결과를 보고한다**

커밋할 것은 없다. 세 단계의 실제 출력(응답 `provider` 값)을 그대로 보고한다. "확인했다"가 아니라 값을 적는다.
