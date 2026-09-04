# 비재귀 분석 경로 통일 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `AnalyzeReferencedCodeObjects`가 꺼져 있어도 분석과 저장이 `DependencyAnalysisOrchestrator` 한 경로를 타게 해서, 산출물 규칙에서 「어느 경로가 쓰나」라는 축을 없앤다.

**Architecture:** `DependencyAnalysisRequest`에 플래그 하나를 더해 그래프 재귀와 메타데이터 수집 범위를 함께 결정하게 하고, CLI의 OFF 분기와 그 전용 저장 코드를 삭제한다. 저장은 `PersistArtifactsAsync` + `ExportCodeObjectArtifactsAsync` 한 벌만 남는다.

**Tech Stack:** .NET / C# 12, xUnit, NSubstitute, Roslyn(`Microsoft.CodeAnalysis.CSharp` — 정책 검사 테스트용)

**Spec:** `docs/superpowers/specs/2026-09-04-unify-analysis-path-design.md`

## Global Constraints

프로젝트 `AGENTS.md`의 「에이전트 작업 완료 체크리스트」가 모든 태스크에 적용된다.

- **컴파일 경고 0건.** 증분 빌드는 기존 경고를 다시 보고하지 않으므로 `dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"`로 센다. 쓰이지 않게 된 지역 변수(`CS0219`)를 남기지 말 것 — 이 계획은 지역 변수 3개를 지운다.
- **`dotnet test`가 실패 0 · 건너뜀 0.** 통과 **개수**를 합격 기준으로 쓰지 않는다(환경에 따라 흔들린다).
- **취소 정책.** 취소 가능한 `await`를 감싸는 넓은 `catch`에는 `when (ex is not OperationCanceledException)` 필터가 있어야 한다(`CancellationPolicyTests`). 이 계획이 지우는 `catch` 둘(`Program.SaveRawArtifactsAsync`, `MetadataExporter.ExportRawMetadataAsync`)은 **토큰을 넘기는 `await`가 없어 기준선에 세어져 있지 않다.** 따라서 `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`는 **고치지 않는다.** 그래도 Task 5에서 이 테스트를 직접 돌려 확인한다.
- **심볼을 지우면 문서도 지운다.** `grep -rn "<지운 이름>" docs/`로 남은 서술을 함께 고친다. 이 계획이 지우는 이름: `SaveRawArtifactsAsync`, `SaveDocumentsAsync`, `FromSingleObjectPipeline`, `ExportRawMetadataAsync`, `SaveRawJson`, `SaveRawContext`, `SaveRawFiles`.
- **AGENTS.md의 줄 하나는 600바이트 이하**(`DocumentationBudgetTests`).
- **워크트리에서 작업한다면** 코퍼스 재료 **셋**을 심링크한다(`output/`, `output.bak-2026-08-22`, `output.bak-stage4-control-20260828`). 일부만 걸면 다른 테스트가 조용히 꺼진다(`CorpusSetupGuardTests`).

---

## 파일 구조

| 파일 | 이 계획에서의 책임 |
| :--- | :--- |
| `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs` | 유일한 분석·저장 진입점이 된다. 재귀 억제, scope 전파, `Thinking.md` 캐시 가드 |
| `src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs` | `DependencyAnalysisRequest`에 플래그 추가 |
| `src/ReSet.Core/Services/MetadataExporter.cs` | `prompt-context.md` 이사, `ExportRawMetadataAsync` 삭제 |
| `src/ReSet.Core/Services/IMetadataExporter.cs` | 삭제된 메서드 선언 제거 |
| `src/ReSet.Core/Models/SpAnalysisOutcome.cs` | `FromSingleObjectPipeline` 삭제, scope를 인자로 받기 |
| `src/ReSet.Cli/Program.cs` | OFF 분기·저장 게이트·전용 저장 메서드 삭제, 계획서 경로 정리 |
| `src/ReSet.Cli/appsettings.json` | `SaveRaw*` 세 키 삭제 |
| `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs` | 새 동작 4건 |
| `tests/ReSet.Core.Tests/SpAnalysisOutcomeTests.cs` | **계획서 초판이 빠뜨렸다.** Task 3의 3인자 시그니처가 호출부 3곳을 깨고, Task 4의 `FromSingleObjectPipeline` 삭제가 그 테스트 1건을 지운다 |
| `tests/ReSet.Core.Tests/MetadataExporterTests.cs` | 경로 단언 3건 갱신, 죽은 테스트 4건 삭제 |
| `docs/output-artifacts.md` · `docs/known-defects.md` | 규칙 단일화 반영 |

---

### Task 1: `Thinking.md`가 캐시 히트에 파괴되지 않게 한다

통일의 **전제조건**이다. 지금 OFF 경로는 CLI의 `if (!result.FromCache)` 게이트 덕에 이 결함을 우연히 피하고 있으므로, 게이트를 지우기 전에 가드를 옮겨야 한다.

**Files:**
- Modify: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:551-590` (`PersistThinkingAsync`)
- Test: `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`

**Interfaces:**
- Consumes: 없음(기존 코드만)
- Produces: `PersistThinkingAsync`의 새 계약 — 추론 본문이 비었고 파일이 이미 있으면 **덮지 않는다.** Task 4가 CLI의 캐시 게이트를 지울 때 이 계약에 의존한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`의 `AnalyzeAsync_ThinkingLogCarriesTheAnalysisModelIdentity` 바로 아래에 넣는다.

```csharp
    /// <summary>
    /// 캐시 히트 회차는 ThinkingText가 비어 있다(VerificationPipelineOrchestrator가
    /// AI를 호출한 회차에만 채운다). 그 빈 값으로 덮으면 앞선 회차의 추론 기록이
    /// 「추론 없음」 자리표시자와 오늘 날짜로 사라진다 — raw/prompt-context.md가
    /// MetadataExporter에서 보호받는 것과 같은 사건이다.
    /// 파일이 아예 없을 때 자리표시자 판본을 남기는 계약은 그대로 지킨다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_PreservesExistingThinkingLogWhenReasoningIsEmpty()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-ThinkingCache-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var paths = new OutputPathResolver(root.Database, outputRoot);
        var thinkingPath = Path.Combine(paths.ResolveDocsDirectory(root), "Thinking.md");

        try
        {
            // 1회차: 실제로 AI를 호출해 추론을 남겼다.
            var analyzing = new DependencyAnalysisOrchestrator(
                metadata,
                (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
                {
                    SpDef = Definition(key),
                    SpecMarkdown = "# Spec",
                    ThinkingText = "private reasoning from attempt 1"
                }));
            await analyzing.AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            // 2회차: 캐시 히트라 추론 본문이 없다.
            var cached = new DependencyAnalysisOrchestrator(
                metadata,
                (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
                {
                    SpDef = Definition(key),
                    SpecMarkdown = "# Spec",
                    ThinkingText = null,
                    FromCache = true
                }));
            await cached.AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var thinking = await File.ReadAllTextAsync(thinkingPath);
            Assert.Contains("private reasoning from attempt 1", thinking);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    /// <summary>
    /// 위 보존 규칙이 「파일이 없으면 반드시 만든다」를 깨뜨리지 않는지 함께 잠근다.
    /// 파일 없음과 추론 없음은 산출물만 보고 구분되어야 한다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WritesPlaceholderThinkingLogWhenFileIsAbsent()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-ThinkingEmpty-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = "# Spec",
                ThinkingText = null
            }));

        try
        {
            await sut.AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var thinkingPath = Path.Combine(paths.ResolveDocsDirectory(root), "Thinking.md");
            Assert.True(File.Exists(thinkingPath));
            Assert.Contains("# AI 추론 과정 로그", await File.ReadAllTextAsync(thinkingPath));
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~AnalyzeAsync_PreservesExistingThinkingLogWhenReasoningIsEmpty"
```

기대: **실패.** 2회차가 파일을 자리표시자로 덮었으므로 `private reasoning from attempt 1`을 못 찾는다.

- [ ] **Step 3: 가드를 구현한다**

`DependencyAnalysisOrchestrator.PersistThinkingAsync`의 `thinkingPath` 계산 직후, `WriteAllTextAsync` 앞에 넣는다. 기존 주석("추론 본문이 비어도 쓴다…")은 아래 새 주석으로 **교체**한다.

```csharp
            var thinkingPath = Path.Combine(
                paths.ResolveDocsDirectory(analysis.Key),
                "Thinking.md");

            // 캐시 히트 회차는 ThinkingText가 비어 있다 — 파이프라인이 AI를 호출한
            // 회차에만 그 값을 채우기 때문이다. 그 빈 값으로 덮으면 앞선 회차의 추론
            // 기록이 자리표시자와 오늘 날짜로 사라진다. 남길 것이 없으면 이미 있는
            // 기록을 지키고, 파일이 아예 없을 때만 자리표시자 판본을 만든다 —
            // "파일 없음"과 "추론 없음"은 산출물만 보고 구분되어야 한다.
            // (raw/prompt-context.md가 MetadataExporter에서 받는 보호와 같은 규약)
            if (string.IsNullOrWhiteSpace(analysis.ThinkingText) && File.Exists(thinkingPath))
            {
                return;
            }
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~DependencyAnalysisOrchestratorTests"
```

기대: **전부 통과, 건너뜀 0.**

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs
git commit -m "fix: 캐시 히트가 Thinking.md의 추론 기록을 지우지 않게 한다"
```

---

### Task 2: `prompt-context.md`를 객체 디렉터리로 옮긴다

**Files:**
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs:61-98` (`ExportCodeObjectArtifactsAsync`)
- Test: `tests/ReSet.Core.Tests/MetadataExporterTests.cs:280-420`

**Interfaces:**
- Consumes: 없음
- Produces: `prompt-context.md`의 새 자리 — `Procedures|Functions/[스키마].[이름]/raw/prompt-context.md`. Task 7의 문서 갱신이 이 자리를 적는다.

- [ ] **Step 1: 기존 경로 단언 3건을 새 자리로 고치고, 옛 자리 역단언을 더한다**

`tests/ReSet.Core.Tests/MetadataExporterTests.cs`에서 아래 세 줄을 찾아 바꾼다. 세 줄은
`ExportCodeObjectArtifactsAsync_WritesDefinitionPromptContextEvenWhenArgumentIsOmitted`(`:287`),
`ExportCodeObjectArtifactsAsync_WritesMetadataJsonNextToManifest`(`:380` 부근),
`ExportCodeObjectArtifactsAsync_PreservesExistingPromptContextWhenPromptIsEmpty`(`:403` 부근)에 있다.

바꾸기 전(각각):

```csharp
var promptPath = Path.Combine(outputRoot, "Objects", "dbo.USP_Prompt.Procedure", "raw", "prompt-context.md");
var promptPath = Path.Combine(outputRoot, "Objects", "dbo.USP_EmptyPrompt.Procedure", "raw", "prompt-context.md");
var promptPath = Path.Combine(
    outputRoot, "Objects", "dbo.USP_CacheHit.Procedure", "raw", "prompt-context.md");
```

바꾼 뒤(각각):

```csharp
var promptPath = Path.Combine(outputRoot, "Procedures", "dbo.USP_Prompt", "raw", "prompt-context.md");
var promptPath = Path.Combine(outputRoot, "Procedures", "dbo.USP_EmptyPrompt", "raw", "prompt-context.md");
var promptPath = Path.Combine(
    outputRoot, "Procedures", "dbo.USP_CacheHit", "raw", "prompt-context.md");
```

> **정정(2026-09-04, 실행 중 발견).** 이 계획서 초판은 `:287`의 테스트 이름을
> `ExportCodeObjectArtifactsAsync_WritesPromptContextNextToCanonicalDdl`이라고 적고
> 그것을 `…NextToManifest`로 고치라고 지시했다. **그런 이름의 테스트는 저장소에 없다** —
> 계획서를 쓸 때 메서드 시그니처가 안 보이는 창으로 본문만 읽고 이름을 지어냈다.
> `:287`의 실제 이름은 `ExportCodeObjectArtifactsAsync_WritesDefinitionPromptContextEvenWhenArgumentIsOmitted`이고,
> 그 이름이 말하는 것은 **자리**가 아니라 `rawPromptContext` 인자를 생략하면
> `definition.RawPromptContext`가 쓰인다는 **다른 성질**이다.
>
> 따라서 **이름은 바꾸지 않는다.** 리뷰가 그렇게 확정했고, 근거는 일반론보다 강했다 —
> 인자 생략 폴백은 **유일 커버리지**이면서 실사용 경로다(유일한 프로덕션 호출부
> `DependencyAnalysisOrchestrator.cs:473`이 `rawPromptContext`를 생략한다). 반면 자리는
> 형제 테스트 둘이 이미 독립적으로 잠그고 있다. 즉 이 이름을 자리 쪽으로 바꾸면
> **세 번 덮인 성질을 가리키는 이름 아래에서 유일하게 덮인 성질이 깨지게** 된다.
>
> 역단언(`Objects/`에 안 생긴다)과 폴백 사실을 적은 XML doc 주석은 **남긴다.**
> 자리의 부재에 이름을 주고 싶으면 별도 테스트로 만든다
> (`…LeavesNoPromptContextInCanonicalFolder`).

그리고 그 테스트의 `Assert.Equal("actual prompt body", …)` 바로 아래에 역단언을 더한다.

```csharp
                // 정본 폴더에는 DDL만 남는다. 한 객체의 raw가 두 집으로 쪼개지면
                // §11의 되짚는 순서("모델이 무엇을 봤나 → raw/prompt-context.md")가 거짓이 된다.
                Assert.False(File.Exists(Path.Combine(
                    outputRoot, "Objects", "dbo.USP_Prompt.Procedure", "raw", "prompt-context.md")));
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MetadataExporterTests"
```

기대: **위 3건 실패**(새 자리에 파일이 없다).

- [ ] **Step 3: 저장 자리를 옮긴다**

`MetadataExporter.ExportCodeObjectArtifactsAsync`에서 `promptContextPath` 계산만 바꾼다. `rawDirectory`는 `PortableBundle` DDL 복사가 계속 쓰므로 **지우지 않는다.** 매니페스트 경로 계산이 이 지점보다 뒤에 있으므로, `manifestPath` 계산 블록을 `promptContext` 블록 **앞으로** 옮긴다.

바꾼 뒤의 순서(`canonicalDdlPath` 기록 직후부터):

```csharp
                var rawDirectory = Path.GetDirectoryName(canonicalDdlPath)!;

                var manifestPath = paths.ResolveManifestPath(objectKey);
                var objectRawDirectory = Path.GetDirectoryName(manifestPath)!;
                Directory.CreateDirectory(objectRawDirectory);

                // 이 파일은 정본이 아니라 회차별 분석 흔적이므로 metadata.json·
                // dependency-manifest.json과 같은 집에 둔다. 정본 폴더(Objects/)에 두면
                // 한 객체의 raw가 두 폴더로 쪼개져, 문서가 안내하는 되짚는 순서가 깨진다.
                var promptContext = rawPromptContext ?? definition.RawPromptContext ?? string.Empty;
                var promptContextPath = Path.Combine(objectRawDirectory, "prompt-context.md");

                // 캐시 히트 회차는 RawPromptContext가 비어 있다 - 파이프라인이 AI를
                // 호출한 회차에만 그 값을 채우기 때문이다. 그 빈 값으로 덮으면 앞선
                // 회차가 실제로 모델에 보낸 원문이 사라져, "결과가 이상할 때 입력부터
                // 확인하라"는 이 파일의 존재 이유가 없어진다. 남길 것이 없으면 이미
                // 있는 기록을 지키고, 파일이 아예 없을 때만 빈 파일을 만든다 -
                // "파일 없음"과 "프롬프트 없음"은 산출물만 보고 구분되어야 한다.
                if (promptContext.Length > 0 || !File.Exists(promptContextPath))
                {
                    await File.WriteAllTextAsync(
                        promptContextPath,
                        promptContext,
                        Encoding.UTF8,
                        cancellationToken);
                }

                if (artifactMode == DependencyArtifactMode.PortableBundle)
                {
                    await ExportReferencedCodeDdlsAsync(definition, rawDirectory, cancellationToken);
                }

                var objectDirectoryForManifest = Path.GetDirectoryName(objectRawDirectory)!;
                var manifest = BuildManifest(definition, objectKey, graph, paths, objectDirectoryForManifest);
                var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(manifestPath, json, Encoding.UTF8, cancellationToken);

                // 지시서 번들이 참조 테이블 스키마를 만들 때 쓰는 원천이다.
                // 매니페스트와 같은 디렉터리에 두어야 Spec.md 경로에서 규칙적으로 찾을 수 있다.
                var metadataPath = Path.Combine(objectRawDirectory, "metadata.json");
                await File.WriteAllTextAsync(
                    metadataPath,
                    JsonSerializer.Serialize(definition, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8,
                    cancellationToken);
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MetadataExporterTests"
```

기대: **전부 통과, 건너뜀 0.**

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/MetadataExporter.cs tests/ReSet.Core.Tests/MetadataExporterTests.cs
git commit -m "refactor: prompt-context.md를 정본 폴더에서 객체 raw/로 옮긴다"
```

---

### Task 3: 오케스트레이터가 비재귀 요청을 받게 한다

CLI는 아직 이 기능을 쓰지 않는다. 이 태스크는 **능력만** 만든다.

**Files:**
- Modify: `src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs` (`DependencyAnalysisRequest`)
- Modify: `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:17-31`(편의 생성자 람다), `:163-199`(자식 순회), `:520-530`(`BuildPersistedSpecification`)
- Modify: `src/ReSet.Core/Models/SpAnalysisOutcome.cs:52-75` (`FromDependencyGraph`)
- Test: `tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `Thinking.md` 보존 계약
- Produces:
  - `DependencyAnalysisRequest.AnalyzeReferencedCodeObjects` (`bool`, 기본값 `true`)
  - `SpAnalysisOutcome.FromDependencyGraph(CodeObjectPipelineResult result, CodeObjectKey rootKey, AnalysisScope scope)` — **인자가 3개로 늘어난다.** Task 4가 이 시그니처를 부른다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`DependencyAnalysisOrchestratorTests`에 더한다.

```csharp
    /// <summary>
    /// 참조분석 OFF는 "깊이 0"이 아니라 "그래프를 만들지 않는다"이다. 자식을 발견해
    /// 실행 목록에 넣으면 OFF에서도 자식마다 AI 비용이 나가고, 자식 Spec.md가 생겨
    /// 사용자가 고른 것과 다른 산출물이 남는다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenReferencesAreDisabled_AnalyzesRootOnly()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var executed = new List<CodeObjectKey>();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                executed.Add(key);
                return Task.FromResult(PipelineResult(key));
            });

        var result = await sut.AnalyzeAsync(
            root,
            Request() with { AnalyzeReferencedCodeObjects = false },
            CancellationToken.None);

        Assert.Equal(new[] { root }, executed);
        Assert.Empty(result.Edges);
        Assert.Equal(root, Assert.Single(result.Nodes).Key);
    }

    /// <summary>
    /// OFF 회차의 명세서는 전이적으로 모은 메타데이터로 쓰인다. 머리에 "직접 의존성"이
    /// 박히면 문서가 자기 수집 범위를 거짓으로 신고한다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenReferencesAreDisabled_StampsTransitiveScope()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Scope-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot) with { AnalyzeReferencedCodeObjects = false },
                CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var spec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));
            Assert.Contains("분석 범위: 전이 의존성", spec);
            Assert.DoesNotContain("분석 범위: 직접 의존성", spec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    /// <summary>
    /// 결과가 담는 정의는 파이프라인이 수집한 그것이어야 한다 — 발견 단계가 쓴
    /// 직접 의존성 판본으로 바뀌면 안 된다. 이 정의의 Dependencies가 CLI의 spDefs를
    /// 거쳐 StepInterfaceFacts.BuildCallGraph로 흘러가고, 그것이 계획서 Narrow 모드의
    /// 1-hop 이웃을 고른다. 얇아져도 명세서는 멀쩡해 보이고 계획서 단계 본문만
    /// 조용히 나빠지므로, 여기서 잠근다(설계 §2.1).
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenReferencesAreDisabled_CarriesPipelineCollectedDependencies()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var neighbour = Key("USP_Neighbour", CodeObjectType.Procedure);

        // 발견 단계가 보는 정의에는 의존성이 없다.
        var metadata = CreateMetadataService(Definition(root));

        // 파이프라인은 전이적으로 모아 이웃을 담아 돌려준다.
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key, neighbour),
                SpecMarkdown = "# Spec"
            }));

        var result = await sut.AnalyzeAsync(
            root,
            Request() with { AnalyzeReferencedCodeObjects = false },
            CancellationToken.None);

        var analysis = Assert.Single(result.AnalysisResults);
        var dependency = Assert.Single(analysis.Definition!.Dependencies);
        Assert.Equal("USP_Neighbour", dependency.Name);
    }

    /// <summary>
    /// 재귀 모드는 지금 그대로여야 한다. 플래그의 기본값이 뒤집히면 기존 사용자가
    /// 아무것도 바꾸지 않았는데 산출물이 줄어든다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_ByDefault_StillAnalyzesReferencesAndStampsDirectScope()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-ScopeDirect-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            var result = await sut.AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            Assert.Equal(2, result.Nodes.Count);
            var paths = new OutputPathResolver(root.Database, outputRoot);
            var spec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));
            Assert.Contains("분석 범위: 직접 의존성", spec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~DependencyAnalysisOrchestratorTests"
```

기대: **컴파일 실패** — `AnalyzeReferencedCodeObjects`가 없다.

- [ ] **Step 3: 요청 레코드에 플래그를 더한다**

`src/ReSet.Core/Services/IDependencyAnalysisOrchestrator.cs`의 `DependencyAnalysisRequest`에서 `AllowExternalDatabaseConnections` 바로 아래에 넣는다.

```csharp
    /// <summary>
    /// 루트가 참조하는 SP/UDF까지 그래프로 분석할지. 이 값 하나가 두 축을 함께
    /// 결정한다 — 그래프 재귀 여부와, 파이프라인의 메타데이터 수집 범위다.
    /// <c>false</c>면 그래프는 루트 한 노드로 끝나고, 대신 루트가 전이적 의존성
    /// 메타데이터를 전부 받는다(자식이 자기 명세서를 갖지 않으므로 루트 하나가
    /// 전부를 설명해야 한다).
    /// </summary>
    public bool AnalyzeReferencedCodeObjects { get; init; } = true;
```

같은 파일의 `PrintMembers`는 **손으로 쓴 것이다.** `AllowExternalDatabaseConnections` 줄 아래에 더한다 — 빠뜨리면 `ToString`이 새 값을 조용히 감춘다.

```csharp
        builder.Append(", AnalyzeReferencedCodeObjects = ").Append(AnalyzeReferencedCodeObjects);
```

- [ ] **Step 4: 자식 순회를 억제한다**

`DependencyAnalysisOrchestrator.DiscoverAsync`의 `execution.RegisterCanonicalKey(definition.ObjectKey);`(`:161`) 다음 줄부터 시작하는 `foreach (var dependency in GetDirectCodeObjectDependencies(definition, key))` 블록 전체(`:163-198`)를 `if`로 감싼다. 블록 내부는 **한 글자도 바꾸지 않는다.**

```csharp
            execution.RegisterCanonicalKey(definition.ObjectKey);

            // OFF는 "깊이 0"이 아니라 "그래프를 만들지 않는다"이다. 자식을 발견해
            // 실행 목록에 넣으면 OFF에서도 자식마다 AI 비용이 나가고, 사용자가 고르지
            // 않은 Spec.md가 생긴다. 루트가 잃는 정보는 파이프라인이 전이적 메타데이터를
            // 대신 실어 메운다(DependencyAnalysisRequest.AnalyzeReferencedCodeObjects).
            if (request.AnalyzeReferencedCodeObjects)
            {
                foreach (var dependency in GetDirectCodeObjectDependencies(definition, key))
                {
                    // ... 기존 본문 그대로 ...
                }
            }

            node.Status = AnalysisNodeStatus.Queued;
```

- [ ] **Step 5: 파이프라인 호출에 수집 범위를 전달한다**

`DependencyAnalysisOrchestrator`의 편의 생성자 람다(`:17-31`)에서 하드코딩된 `directDependenciesOnly: true`를 바꾼다.

```csharp
                directDependenciesOnly: request.AnalyzeReferencedCodeObjects,
```

- [ ] **Step 6: scope 하드코딩 둘을 제거한다**

`DependencyAnalysisOrchestrator.BuildPersistedSpecification`의 마지막 인자(`:529`):

```csharp
            request.AnalyzeReferencedCodeObjects
                ? AnalysisScope.Direct
                : AnalysisScope.Transitive);
```

`src/ReSet.Core/Models/SpAnalysisOutcome.cs`의 `FromDependencyGraph`는 scope를 인자로 받는다. 시그니처와 본문 한 줄을 바꾼다.

```csharp
    public static SpAnalysisOutcome FromDependencyGraph(
        CodeObjectPipelineResult result,
        CodeObjectKey rootKey,
        AnalysisScope scope)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rootKey);

        var root = result.AnalysisResults.FirstOrDefault(analysis => analysis.Key == rootKey);

        return new SpAnalysisOutcome
        {
            SpecMarkdown = root?.SpecMarkdown,
            Definition = root?.Definition,
            Review = root?.Review,
            ThinkingText = root?.ThinkingText,
            Outcome = root?.Outcome ?? VerificationOutcome.ReviewNotRun,
            Scope = scope,
            Completion = result.Completion,
            FromCache = root?.FromCache ?? false,
            AnalyzedAt = root?.AnalyzedAt,
            Persistence = result.Persistence,
            PersistenceErrors = result.PersistenceErrors.ToArray()
        };
    }
```

`Program.cs:2083`의 기존 호출을 임시로 맞춘다(Task 4가 다시 손댄다).

```csharp
            return SpAnalysisOutcome.FromDependencyGraph(result, rootKey, AnalysisScope.Direct);
```

- [ ] **Step 7: 테스트가 통과하는지 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~DependencyAnalysisOrchestratorTests"
dotnet build 2>&1 | grep -cE "warning CS"
```

기대: 테스트 **전부 통과, 건너뜀 0** · 경고 **0**.

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core tests/ReSet.Core.Tests/DependencyAnalysisOrchestratorTests.cs src/ReSet.Cli/Program.cs
git commit -m "feat: 오케스트레이터가 비재귀 분석 요청을 받게 한다"
```

---

### Task 4: CLI의 OFF 분기와 전용 저장 코드를 지운다

**Files:**
- Modify: `src/ReSet.Cli/Program.cs` — `RunConfiguredAnalysisAsync`(`:2014-2083`), 게이트 블록 2개(`:935`, `:1384`), `SaveRawArtifactsAsync`(`:2204-2286`), `SaveDocumentsAsync`(`:2288~`), 지역 변수(`:364-366`)
- Modify: `src/ReSet.Core/Models/SpAnalysisOutcome.cs` — `FromSingleObjectPipeline` 삭제
- Test: `tests/ReSet.Core.Tests/CliArgsTests.cs`
- Test: `tests/ReSet.Core.Tests/SpAnalysisOutcomeTests.cs` — `FromSingleObjectPipeline_MarksTransitiveScopeAndLeavesPersistenceToTheCaller`(`:21`·`:37`)가 지워지는 심볼을 검사하므로 함께 지운다. 같은 파일의 `FromDependencyGraph_MissingRoot_…`는 scope가 인자가 된 뒤로 **자기가 방금 넘긴 값을 되읽는 단언**이 됐다 — 이름(`…MarksDirectScope…`)도 함께 볼 것

**Interfaces:**
- Consumes: Task 3의 `AnalyzeReferencedCodeObjects`와 3인자 `FromDependencyGraph`
- Produces: `RunConfiguredAnalysisAsync`는 시그니처가 그대로이고, 이제 **항상** `Persistence != NotAttempted`인 결과를 낸다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/CliArgsTests.cs`의 기존 테스트 `RunConfiguredAnalysisAsync_UsesOfflineSnapshotDatabaseForRecursiveRoot` 바로 아래에 더한다. 같은 파일의 대역 `CapturingDependencyAnalysisOrchestrator`(`:143`)를 그대로 쓴다.

먼저 그 대역이 요청도 붙잡게 한 줄을 더한다.

```csharp
            public CodeObjectKey? LastRootKey { get; private set; }
            public DependencyAnalysisRequest? LastRequest { get; private set; }

            public Task<CodeObjectPipelineResult> AnalyzeAsync(
                CodeObjectKey rootKey,
                DependencyAnalysisRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRootKey = rootKey;
                LastRequest = request;
```

그다음 테스트를 더한다.

```csharp
        /// <summary>
        /// 참조분석 OFF도 오케스트레이터를 탄다. 예전에는 CLI가 파이프라인을 직접 부르고
        /// 저장까지 손으로 했는데, 그 경로에는 dependency-manifest.json도 Objects/ 정본도
        /// 없었고 metadata.json은 스위치 하나에 걸려 있었다.
        /// verificationPipelineOrchestrator에 null을 넘겨도 통과한다는 것이 곧 증거다 —
        /// OFF가 그 인자를 더 이상 역참조하지 않는다.
        /// </summary>
        [Fact]
        public async Task RunConfiguredAnalysisAsync_WhenReferencesAreDisabled_StillUsesOrchestrator()
        {
            var snapshot = new DbSnapshot { Database = "SnapshotDB" };
            var metadata = new OfflineDbMetadataService(snapshot);
            var dependencyOrchestrator = new CapturingDependencyAnalysisOrchestrator();

            var result = await Program.RunConfiguredAnalysisAsync(
                analyzeReferencedCodeObjects: false,
                dependencyOrchestrator,
                verificationPipelineOrchestrator: null!,
                metadata,
                connectionString: string.Empty,
                configuredDatabase: "ConfiguredDB",
                schema: "dbo",
                name: "usp_Root",
                maxDepth: 2,
                provider: "OpenAI",
                modelName: "gpt-test",
                actorEffort: "high",
                instructions: "rules",
                isBatchMode: true,
                outputDirectory: "/tmp/output",
                enableCache: false,
                allowExternalDatabaseConnections: false,
                DependencyArtifactMode.Reference,
                CancellationToken.None);

            Assert.Equal("SnapshotDB", dependencyOrchestrator.LastRootKey?.Database);
            Assert.False(dependencyOrchestrator.LastRequest?.AnalyzeReferencedCodeObjects);
            Assert.Equal(AnalysisScope.Transitive, result.Scope);
        }
```

> 기존 재귀 테스트에도 한 줄을 더해 기본값이 뒤집히지 않았음을 잠근다.
>
> ```csharp
>             Assert.True(dependencyOrchestrator.LastRequest?.AnalyzeReferencedCodeObjects);
> ```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~RunConfiguredAnalysisAsync_WhenReferencesAreDisabled_StillUsesOrchestrator"
```

기대: **실패** — OFF 분기가 `verificationPipelineOrchestrator`(`null`)를 역참조한다.

- [ ] **Step 3: OFF 분기를 지운다**

`Program.RunConfiguredAnalysisAsync`에서 `if (!analyzeReferencedCodeObjects) { … }` 블록 전체(`:2033-2055`)를 지우고, 남은 본문을 아래로 바꾼다.

```csharp
            var database = await ResolveAnalysisDatabaseAsync(
                connectionString,
                configuredDatabase,
                metadataService,
                cancellationToken);
            var rootKey = CodeObjectKey.Create(database, schema, name, CodeObjectType.Procedure);
            var result = await dependencyAnalysisOrchestrator.AnalyzeAsync(
                rootKey,
                new DependencyAnalysisRequest
                {
                    ConnectionString = connectionString,
                    MaxDepth = maxDepth,
                    Provider = provider,
                    ModelName = modelName,
                    ActorEffort = actorEffort,
                    Instructions = instructions,
                    IsBatchMode = isBatchMode,
                    OutputDirectory = outputDirectory,
                    EnableCache = enableCache,
                    AllowExternalDatabaseConnections = allowExternalDatabaseConnections,
                    AnalyzeReferencedCodeObjects = analyzeReferencedCodeObjects,
                    DependencyArtifactMode = dependencyArtifactMode
                },
                cancellationToken);

            RenderAnalysisDiagnostics(result);

            return SpAnalysisOutcome.FromDependencyGraph(
                result,
                rootKey,
                analyzeReferencedCodeObjects ? AnalysisScope.Direct : AnalysisScope.Transitive);
```

`verificationPipelineOrchestrator` 매개변수는 **남긴다** — 호출부 둘이 아직 넘기고 있고, 지우는 것은 이 태스크의 범위를 넘는다. 쓰이지 않는 매개변수는 `CS` 경고를 내지 않는다.

- [ ] **Step 4: 저장 게이트 두 곳을 지운다**

`Program.cs:935`와 `:1384`의 `if (result.Persistence == ArtifactPersistence.NotAttempted) { … }` 블록을 **블록째** 지운다. 안에 있는 `SaveRawArtifactsAsync` 호출과 `if (!result.FromCache) SaveDocumentsAsync(…)`가 함께 사라진다. 두 자리 모두 주석("재귀 경로는 오케스트레이터가 이미 저장했다…")도 함께 지우고, 아래 한 줄로 바꾼다.

```csharp
                        // 저장은 오케스트레이터가 끝냈다(참조분석 ON/OFF 공통).
```

`:935` 블록 **뒤에 이어지는** 배치 전환 계획서 저장(`SaveMigrationPlanAsync` 호출)은 게이트 밖에 있으므로 **건드리지 않는다.**

- [ ] **Step 5: 죽은 코드를 지운다**

- `Program.SaveRawArtifactsAsync` 메서드 전체(XML 주석 포함)
- `Program.SaveDocumentsAsync` 메서드 전체(XML 주석 포함)
- `Program.cs:364-366`의 지역 변수 셋 — 남기면 `CS0219`가 난다

```csharp
            bool.TryParse(configuration["OutputSettings:SaveRawJson"] ?? "false", out bool saveRawJson);
            bool.TryParse(configuration["OutputSettings:SaveRawContext"] ?? "false", out bool saveRawContext);
            bool.TryParse(configuration["OutputSettings:SaveRawFiles"] ?? "false", out bool saveRawFiles);
```

- `SpAnalysisOutcome.FromSingleObjectPipeline` 메서드 전체(XML 주석 포함)

`metadataExporter` 지역 변수가 이제 안 쓰이면 그것도 지운다. `dotnet build`가 알려 준다.

- [ ] **Step 6: 테스트와 빌드를 확인한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"
dotnet test
```

기대: 경고 **0** · 테스트 **실패 0 · 건너뜀 0**.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Cli/Program.cs src/ReSet.Core/Models/SpAnalysisOutcome.cs tests/ReSet.Core.Tests/CliArgsTests.cs
git commit -m "refactor: 비재귀 분석도 오케스트레이터를 타게 하고 CLI 전용 저장 경로를 지운다"
```

---

### Task 5: `ExportRawMetadataAsync`와 세 설정 키를 지운다

**Files:**
- Modify: `src/ReSet.Core/Services/IMetadataExporter.cs:17`
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs:254-368`
- Modify: `src/ReSet.Cli/appsettings.json:229-231`
- Test: `tests/ReSet.Core.Tests/MetadataExporterTests.cs` — 테스트 4건 삭제

**Interfaces:**
- Consumes: Task 4가 유일한 호출부를 지운 상태
- Produces: 없음(삭제만)

- [ ] **Step 1: 호출부가 정말 없는지 확인한다**

```bash
grep -rn "ExportRawMetadataAsync\|SaveRawJson\|SaveRawContext\|SaveRawFiles" --include="*.cs" --include="*.json" src tests | grep -v "/obj/\|/bin/"
```

기대: `IMetadataExporter.cs`·`MetadataExporter.cs`·`appsettings.json`과 **테스트 4건**만 남아 있다. 그 밖의 자리가 나오면 멈추고 왜 남았는지 먼저 본다.

- [ ] **Step 2: 테스트 4건을 지운다**

`tests/ReSet.Core.Tests/MetadataExporterTests.cs`에서 아래 넷을 통째로 지운다.

- `ExportRawMetadataAsync_ShouldCreateJsonFile_WhenSaveJsonIsTrue`
- `ExportRawMetadataAsync_ShouldIncludeDescriptionsInMarkdown_WhenSaveFilesIsTrue`
- `ExportRawMetadataAsync_ShouldSaveContext_WhenSaveContextIsTrue`
- `ExportRawMetadataAsync_ShouldExportProceduresAndFunctions_WhenSaveFilesIsTrue`

- [ ] **Step 3: 구현과 선언과 설정을 지운다**

- `IMetadataExporter.cs`의 `ExportRawMetadataAsync` 선언
- `MetadataExporter.cs`의 `ExportRawMetadataAsync` 구현 전체
- `appsettings.json`의 세 줄

```json
    "SaveRawJson": true,                   // [설정] SpDefinition JSON 파일 저장 여부
    "SaveRawContext": true,                // [설정] 조립된 프롬프트 텍스트 원문 저장 여부
    "SaveRawFiles": true,                  // [설정] 의존성 개별 객체 파일/폴더 분산 덤프 여부
```

`MetadataExporter.FormatTableSchemaToMarkdown`은 **지우지 않는다** — `InstructionBundleWriter.cs:556`이 Job 단위 `raw/ddl/`을 만들 때 쓴다. 지우면 빌드가 깨진다.

- [ ] **Step 4: 취소 정책 기준선이 그대로인지 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~CancellationPolicyTests"
```

기대: **통과.** 지운 `catch`의 `try` 블록에는 취소 토큰을 넘기는 `await`가 없어 애초에 세어지지 않았다. 실패하면 `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`의 `ReSet.Core/Services/MetadataExporter.cs=1`을 테스트가 요구하는 값으로 **내린다**(올리지 않는다).

- [ ] **Step 5: 전체 검증**

```bash
dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"
dotnet test
```

기대: 경고 **0** · 테스트 **실패 0 · 건너뜀 0**.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/IMetadataExporter.cs src/ReSet.Core/Services/MetadataExporter.cs src/ReSet.Cli/appsettings.json tests/ReSet.Core.Tests/MetadataExporterTests.cs
git commit -m "refactor: 쓰이지 않게 된 ExportRawMetadataAsync와 SaveRaw 설정 셋을 지운다"
```

---

### Task 6: 계획서 저장 경로를 `OutputPathResolver`로 돌린다

CLI에서 출력 경로를 손으로 조립하던 자리 둘이 Task 4에서 사라졌다. 남은 하나를 함께 닫는다(원장에 이미 등재된 결함).

**Files:**
- Modify: `src/ReSet.Cli/Program.cs` — `SaveMigrationPlanAsync`(`:2347`)와 그 호출부(`:962`)
- Test: `tests/ReSet.Core.Tests/OutputPathResolverTests.cs`

**Interfaces:**
- Consumes: `OutputPathResolver.ResolveDocsDirectory(CodeObjectKey)`
- Produces: 없음

- [ ] **Step 1: 이 태스크가 실패하는 테스트로 시작하지 않는 이유를 확인한다**

`SaveMigrationPlanAsync`는 `private static`이고 파일을 쓰는 부수효과만 있어 테스트에서 부를 수 없다. 그러므로 여기서는 **호출이 기대는 계약을 잠그고**, 손조립이 사라진 것을 grep으로 확인한다. 계약 테스트를 `tests/ReSet.Core.Tests/OutputPathResolverTests.cs`에 더한다.

```csharp
    /// <summary>
    /// 이름에 파일명 금지문자가 있으면 손조립 경로는 명세서·캐시 조회 경로와 갈라진다.
    /// 해석기는 %XX로 인코딩하므로 두 자리가 같은 폴더를 가리킨다.
    /// SaveMigrationPlanAsync가 이 계약에 기댄다.
    /// </summary>
    [Fact]
    public void ResolveDocsDirectory_EncodesReservedCharactersInObjectName()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP:Odd", CodeObjectType.Procedure);

        var directory = paths.ResolveDocsDirectory(key);

        Assert.DoesNotContain(":", Path.GetFileName(Path.GetDirectoryName(directory)!));
        Assert.EndsWith("docs", directory);
    }
```

- [ ] **Step 2: 계약 테스트를 돌린다**

```bash
dotnet test --filter "FullyQualifiedName~ResolveDocsDirectory_EncodesReservedCharactersInObjectName"
```

기대: **통과**(해석기는 이미 옳다). 실패하면 해석기 쪽을 먼저 보고, 이 태스크는 멈춘다.

- [ ] **Step 3: 손조립을 해석기 호출로 바꾼다**

`Program.SaveMigrationPlanAsync`(`:2347`)는 지금 `database`를 받지 않으므로 매개변수를 하나 더한다. **`OutputPathResolver`의 생성자는 빈 DB명을 거부하므로**(`OutputPathResolver.cs:28`) 호출부가 빈 값을 넘기지 않는 것이 계약이다.

```csharp
        private static async Task SaveMigrationPlanAsync(
            string migrationPlan,
            string outputDir,
            string database,
            string schema,
            string name,
            string provider,
            string modelName,
            VerificationOutcome sourceOutcome,
            string? effort)
        {
            // 손조립하면 이름에 특수문자가 있을 때 명세서·캐시 조회 경로와 갈라진다.
            var docsDir = new OutputPathResolver(database, outputDir)
                .ResolveDocsDirectory(
                    CodeObjectKey.Create(database, schema, name, CodeObjectType.Procedure));
            Directory.CreateDirectory(docsDir);
```

호출부는 한 곳뿐이다(`Program.cs:962`). 분석이 확정한 DB를 우선 쓰고, 없으면 세션의 `database`(`:279`에서 `"master"`까지 폴백하므로 절대 비지 않는다)를 쓴다.

```csharp
                            await SaveMigrationPlanAsync(
                                migrationPlan,
                                outputDir,
                                result.Definition?.ObjectKey?.Database is { Length: > 0 } analyzedDatabase
                                    ? analyzedDatabase
                                    : database,
                                schema, name,
                                provider, modelName, result.Outcome, actorEffort);
```

`:958`의 낡은 주석 한 줄(「저장 경로({outputDir}/Procedures/{schema}.{name}/docs/BatchMigrationPlan.md)는 OutputPathResolver.ResolveDocsDirectory와 **같고**」)도 고친다 — 이제 같은 것이 아니라 **그것을 쓴다.**

- [ ] **Step 4: 손조립이 사라졌는지 확인한다**

```bash
grep -n 'Path.Combine(outputDir, "Procedures"' src/ReSet.Cli/Program.cs
```

기대: **출력 없음.**

- [ ] **Step 5: 전체 검증**

```bash
dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"
dotnet test
```

기대: 경고 **0** · 테스트 **실패 0 · 건너뜀 0**.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Cli/Program.cs tests/ReSet.Core.Tests/OutputPathResolverTests.cs
git commit -m "fix: 배치 계획서 저장 경로도 OutputPathResolver를 거치게 한다"
```

---

### Task 7: 문서를 통일된 규칙으로 고친다

**Files:**
- Modify: `docs/output-artifacts.md` §3, §7.2, §11
- Modify: `docs/known-defects.md`
- Modify: `docs/architecture.md`, `AGENTS.md`(필요한 만큼만)

**Interfaces:**
- Consumes: Task 1~6이 확정한 산출물 규칙
- Produces: 없음

- [ ] **Step 1: 지운 심볼의 잔존 서술을 찾는다**

```bash
for symbol in SaveRawArtifactsAsync SaveDocumentsAsync FromSingleObjectPipeline ExportRawMetadataAsync SaveRawJson SaveRawContext SaveRawFiles; do
  echo "== $symbol"; grep -rn "$symbol" docs/ AGENTS.md README.md || true
done
```

- [ ] **Step 2: `docs/output-artifacts.md` §3을 고친다**

- `raw/` 표에서 **「어느 경로가 쓰나」 칸을 삭제**하고 아래 규칙으로 바꾼다.

| 파일 | 생성 주체 | 무엇을 · 왜 |
|---|---|---|
| `metadata.json` | 🗄 DB 조회 + 🔎 정적 분석 | (기존 설명 유지) **끌 수단이 없다** — 뒤 계층이 원천으로 읽는다 |
| `dependency-manifest.json` | ⚙️ 결정적 조립 | (기존 설명 유지) 참조분석을 끄면 **루트 한 노드짜리**로 나온다 |
| `prompt-context.md` | ⚙️ 조립 | (기존 설명 유지) **자리가 `Objects/`에서 객체 `raw/`로 옮겨졌다** |
| `deconstructed_logic.json` · `chunks/chunk_N.json` | 🤖 AI (로컬 LLM 전용) | (기존 설명 유지) |

- 「**여기 있는 것이 전부 항상 생기지는 않는다**」 문단과 「**끌 수단이 없는 것은 … 하나뿐이고**」 문단을 **삭제**한다. 두 겹 게이트가 사라졌다.
- `ddl/sp_definition.sql`·`ddl/tables/*.md`·`ddl/procedures|functions/*.sql` 세 행을 삭제한다. 정본은 `Objects/`에 있고 참조 DDL 복사본은 `PortableBundle`에만 있다.
- §3의 「먼저 — 저장 경로가 둘로 갈린다」 절을 삭제하고, §2의 「단, 이 규칙은 참조분석을 켰을 때의 것이다」 문단도 삭제한다. 경로 규칙이 하나가 됐다.
- `Objects/` 표에서 `prompt-context.md` 행을 삭제한다.

- [ ] **Step 3: §7.2와 §11을 고친다**

§7.2의 「**참조분석을 끄고 만든 산출물에 이 맵을 걸 때는 수치를 믿기 전에 폐포부터 본다**」 문단을 아래로 바꾼다.

```markdown
참조분석을 끄고 만든 산출물에서 폐포는 **분석된 객체 하나**로 끝난다. 매니페스트가
루트 한 노드만 담기 때문이며, 이것은 결손이 아니라 사실이다 — 자식을 분석하지
않았으므로 폐포에 넣을 산출물이 없다. 예전처럼 매니페스트가 없어 조용히 폴백하는
일은 더 이상 없다.
```

§11의 4번에서 「**여기서 파일이 안 보이면 결손으로 단정하기 전에 참조분석 설정부터 본다**(§3)」를 지운다. 이제 설정으로 사라지는 파일이 없다.

- [ ] **Step 4: `docs/known-defects.md`를 고친다**

- 「기본 설정에서 `dependency-manifest.json`이 아예 안 생기고 `metadata.json`이 스위치에 걸린다」 항목을 **해소**로 표시하고, 남는 사실만 한 줄로 옮긴다: 참조분석 OFF의 폐포는 루트 한 노드이며 이것은 사실이지 결손이 아니다.
- 「비재귀 경로가 `DependencyAnalysisOrchestrator`로 통일되지 않았다」 항목을 **삭제**한다.
- 「`SaveMigrationPlanAsync`가 `EncodePathSegment`를 쓰지 않는다」 항목을 **삭제**한다(Task 6).
- 「테스트 커버리지」 절에 한 줄 더한다.

```markdown
- **편의 생성자의 파이프라인 인자 매핑에 단위 테스트가 없다** —
  `DependencyAnalysisOrchestrator`의 `(metadataService, pipelineOrchestrator)` 생성자가
  `directDependenciesOnly: request.AnalyzeReferencedCodeObjects`로 넘기는 한 줄. 기존
  테스트는 전부 러너를 주입하는 생성자를 쓰므로 이 람다를 지나지 않는다. 플래그가
  러너까지 도달하는 것은 테스트가 지키지만, 그 값이 파이프라인 인자로 옳게 옮겨지는
  것은 지키지 못한다.
```

- [ ] **Step 5: 남은 문서를 동기화한다**

`reset-doc-sync` 스킬로 `README.md`·`AGENTS.md`·`docs/architecture.md`에서 분석 기동 경로가 하나가 된 사실을 반영한다. `AGENTS.md`에 600바이트를 넘는 줄을 만들지 않는다.

- [ ] **Step 6: 검증**

```bash
dotnet test
```

기대: **실패 0 · 건너뜀 0**(`DocumentationBudgetTests` 포함).

- [ ] **Step 7: 커밋**

```bash
git add docs AGENTS.md README.md
git commit -m "docs: 산출물 규칙이 하나가 된 것을 적고 닫힌 결함 셋을 원장에서 내린다"
```

---

## 완료 확인

모든 태스크가 끝난 뒤 한 번 더 돌린다.

```bash
dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"   # 기대: 0
dotnet test                                                  # 기대: 실패 0, 건너뜀 0
grep -rn "SaveRawJson\|ExportRawMetadataAsync\|FromSingleObjectPipeline" src tests docs AGENTS.md README.md | grep -v "/obj/\|/bin/"   # 기대: 출력 없음
```
