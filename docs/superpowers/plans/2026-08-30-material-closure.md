# 재료 폐포 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 사람이 고른 Job 진입점이 부르는 **프로시저 타입 참조 객체**의 명세를 Job 재료(`specs`·`definitions`)에 자동으로 더한다.

**Architecture:** 폐포 계산은 `BatchStepCatalog`의 새 정적 메서드 하나에 둔다(순수 함수 — 파일을 읽되 상태가 없다). 주입은 `Program.cs`의 두 호출부에서 한다. `specs`를 넓히면 프롬프트·로스터·`codesByProcedure`·`statementFactsByProcedure`가 따라오고, `definitions`를 함께 넓혀야 `tablesByProcedure`가 따라온다.

**Tech Stack:** .NET 10 · C# · xUnit · `System.Text.Json` · Serilog

**Spec:** `docs/superpowers/specs/2026-08-30-material-closure-design.md`

## Global Constraints

- **함수 참조는 절대 더하지 않는다.** 매니페스트 노드 키의 타입 접미사가 `Procedure`인 것만 대상이다(설계서 §2). 함수까지 더하면 프롬프트가 +34%가 되고 부모 명세의 「참조 함수 표」와 중복된다.
- **순서가 실행 순서다.** `BatchStepCatalog.LoadDefinitionsAsync`의 계약이 *「입력 순서가 곧 배치 스텝 실행 순서이므로 순서를 흐트러뜨리면 안 된다」*이다. 더해진 항목은 **자기를 부른 항목 바로 뒤**에 넣는다(설계서 §6).
- **재료 없음을 실패로 바꾸지 않는다.** 매니페스트 부재·파싱 실패·명세 파일 부재는 전부 **건너뛰고 계속 간다** — 예외를 던지지 않는다. 기존 `MissingMetadata`·`FailedToParse` 관용과 같다. 다만 **파싱 실패와 명세 파일 부재는 로그 한 줄을 남긴다**(설계서 §8) — 그 둘은 분석이 중간에 끊겼다는 신호라 사람이 알아야 한다. 매니페스트 부재는 정상이므로 조용하다.
- **상한**: 폐포 크기가 진입점 수의 **2배**를 넘으면 더 넓히지 않고 경고한다(설계서 §5).
- **경로 표기**: 이 코드가 다루는 모든 상대 경로는 **`outputRoot` 기준**이고 구분자는 `Path.DirectorySeparatorChar`다. 비교·중복 판정은 `StringComparer.OrdinalIgnoreCase`로 한다(Windows·macOS 양쪽에서 같은 판정을 내기 위해서다).
- **빌드·테스트는 격리 워크트리에서만** 돌린다(`AGENTS.md` §10). 코퍼스 테스트를 돌리려면 워크트리에 `output`·`output.bak-2026-08-22`·`output.bak-stage4-control-20260828` 심링크 **셋 다**를 건다.
- **합격 기준은 실패 0 · 건너뜀 0 · 경고 0**이다. 절대 통과 수는 게이트가 아니다.

---

### Task 1: 매니페스트에서 프로시저 참조를 읽는다

폐포 계산의 **한 걸음**만 만든다. 한 명세 경로를 받아 그 SP가 부르는 **프로시저 타입** 참조의 `outputRoot` 기준 상대 명세 경로를 돌려준다.

**Files:**
- Modify: `src/ReSet.Cli/BatchStepCatalog.cs` (파일 끝, `ExtractProcedureIdentifier` 뒤)
- Test: `tests/ReSet.Core.Tests/BatchStepCatalogTests.cs`

**Interfaces:**
- Consumes: 없음(첫 태스크)
- Produces: `public static IReadOnlyList<string> ReadProcedureReferences(string outputRoot, string specRelativePath)` — `outputRoot` 기준 상대 명세 경로 목록. 매니페스트가 없거나 못 읽으면 빈 목록.

> **`public`이어야 한다.** 이 저장소에 `InternalsVisibleTo`가 없으므로 `internal`이면 테스트 프로젝트에서 안 보여 Step 1의 테스트가 컴파일되지 않는다. 같은 클래스의 `FindStepCandidates`·`ExtractProcedureIdentifier`·`LoadDefinitionsAsync`가 모두 `public`인 것과 같다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/BatchStepCatalogTests.cs`의 클래스 안, `CreateOutputTree` **앞에** 넣는다.

```csharp
        /// <summary>
        /// 매니페스트의 Nodes 중 타입 접미사가 Procedure 인 것만 돌려준다.
        /// 함수를 함께 돌려주면 프롬프트가 34% 늘고 부모 명세의 「참조 함수 표」와
        /// 중복된다(설계서 §2). 자기 자신도 빼야 한다 - 매니페스트는 자기 키를
        /// Nodes 에 함께 싣는다(실물 확인).
        /// </summary>
        [Fact]
        public void ReadProcedureReferences_ReturnsOnlyProcedureTypedNodesThatHaveASpec()
        {
            var root = CreateManifestTree();
            try
            {
                var refs = BatchStepCatalog
                    .ReadProcedureReferences(root, Path.Combine("Procedures", "dbo.USP_Parent", "docs", "Spec.md"))
                    .Select(p => p.Replace(Path.DirectorySeparatorChar, '/'))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();

                Assert.Equal(
                    new[] { "Procedures/dbo.USP_Child/docs/Spec.md" },
                    refs);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void ReadProcedureReferences_IsSilentWhenTheManifestIsMissing()
        {
            var root = CreateOutputTree();
            try
            {
                Assert.Empty(BatchStepCatalog.ReadProcedureReferences(
                    root, Path.Combine("Procedures", "dbo.USP_Root", "docs", "Spec.md")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void ReadProcedureReferences_IsSilentWhenTheManifestIsNotJson()
        {
            var root = CreateManifestTree();
            try
            {
                File.WriteAllText(
                    Path.Combine(root, "Procedures", "dbo.USP_Parent", "raw", "dependency-manifest.json"),
                    "{ this is not json");

                Assert.Empty(BatchStepCatalog.ReadProcedureReferences(
                    root, Path.Combine("Procedures", "dbo.USP_Parent", "docs", "Spec.md")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>
        /// 매니페스트가 가리키는 명세 파일이 실제로 없으면 더하지 않는다. 없는 파일을
        /// 재료 목록에 넣으면 뒤의 적재기가 그것을 MissingMetadata 로 세어, 사람이
        /// 고르지도 않은 항목 때문에 경고가 뜬다.
        /// </summary>
        [Fact]
        public void ReadProcedureReferences_SkipsANodeWhoseSpecFileDoesNotExist()
        {
            var root = CreateManifestTree();
            try
            {
                File.Delete(Path.Combine(root, "Procedures", "dbo.USP_Child", "docs", "Spec.md"));

                Assert.Empty(BatchStepCatalog.ReadProcedureReferences(
                    root, Path.Combine("Procedures", "dbo.USP_Parent", "docs", "Spec.md")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
```

그리고 같은 클래스의 `WriteSpec` **뒤에** 트리 생성 헬퍼를 넣는다.

```csharp
        /// <summary>
        /// 부모 하나가 프로시저 하나와 함수 하나를 부르는 최소 트리. 매니페스트의
        /// SpecPath 는 매니페스트 자신의 디렉터리 기준 상대 경로다(실물이 그렇다).
        /// </summary>
        private static string CreateManifestTree()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-Manifest-{Guid.NewGuid():N}");
            WriteSpec(root, Path.Combine("Procedures", "dbo.USP_Parent"));
            WriteSpec(root, Path.Combine("Procedures", "dbo.USP_Child"));
            WriteSpec(root, Path.Combine("Functions", "dbo.UF_Helper"));

            var rawDirectory = Path.Combine(root, "Procedures", "dbo.USP_Parent", "raw");
            Directory.CreateDirectory(rawDirectory);
            File.WriteAllText(
                Path.Combine(rawDirectory, "dependency-manifest.json"),
                """
                {
                  "Key": "DB.dbo.USP_Parent.Procedure",
                  "Nodes": [
                    { "Key": "DB.dbo.USP_Parent.Procedure", "Status": "Succeeded", "SpecPath": "docs/Spec.md" },
                    { "Key": "DB.dbo.USP_Child.Procedure", "Status": "Succeeded", "SpecPath": "../dbo.USP_Child/docs/Spec.md" },
                    { "Key": "DB.dbo.UF_Helper.Function", "Status": "Succeeded", "SpecPath": "../../Functions/dbo.UF_Helper/docs/Spec.md" }
                  ]
                }
                """);

            return root;
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter 'FullyQualifiedName~ReadProcedureReferences' --nologo`
Expected: 컴파일 실패 — `'BatchStepCatalog' has no definition for 'ReadProcedureReferences'`

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Cli/BatchStepCatalog.cs`의 `ExtractProcedureIdentifier` **뒤**, 클래스 닫는 괄호 앞에 넣는다.

```csharp
        private const string ManifestFileName = "dependency-manifest.json";

        /// <summary>
        /// 이 명세가 부르는 <b>프로시저 타입</b> 참조 객체의 명세 경로를 outputRoot 기준
        /// 상대 경로로 돌려준다.
        ///
        /// [왜 프로시저만인가] 함수 참조는 결손이 아니다 - 부모 명세의 「참조 함수 표」가
        /// 호출 지점·라인·호출식 전문을 이미 담고, 계획서는 함수를 재구현하지 않는다.
        /// 실측(설계서 §2): 함수 참조 30건을 함께 더하면 프롬프트가 +34%가 된다.
        ///
        /// [왜 파일 존재를 확인하는가] 없는 경로를 재료 목록에 넣으면
        /// <see cref="LoadDefinitionsAsync"/>가 그것을 MissingMetadata 로 세어, 사람이
        /// 고르지도 않은 항목 때문에 경고가 뜬다.
        ///
        /// 매니페스트가 없거나 JSON 이 아니면 빈 목록이다 - 재료 없음을 실패로 바꾸지 않는다.
        /// </summary>
        public static IReadOnlyList<string> ReadProcedureReferences(
            string outputRoot, string specRelativePath)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(specRelativePath))
            {
                return Array.Empty<string>();
            }

            var objectDirectory = Path.GetDirectoryName(
                Path.GetDirectoryName(Path.Combine(outputRoot, specRelativePath)));
            if (objectDirectory is null) return Array.Empty<string>();

            var manifestPath = Path.Combine(objectDirectory, "raw", ManifestFileName);
            if (!File.Exists(manifestPath)) return Array.Empty<string>();

            ManifestShape? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ManifestShape>(
                    File.ReadAllText(manifestPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warning(
                    exception,
                    "[배치 설계] 의존 매니페스트를 읽지 못했습니다 (계속 진행): {ManifestPath}",
                    manifestPath);
                return Array.Empty<string>();
            }

            if (manifest is null) return Array.Empty<string>();

            var results = new List<string>();
            foreach (var node in manifest.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Key) || string.IsNullOrWhiteSpace(node.SpecPath)) continue;
                if (!node.Key.EndsWith(".Procedure", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(node.Key, manifest.Key, StringComparison.OrdinalIgnoreCase)) continue;

                var absolute = Path.GetFullPath(Path.Combine(objectDirectory, node.SpecPath));
                if (!File.Exists(absolute))
                {
                    // 설계서 §8: 없으면 조용히 빼되 한 줄 남긴다. 매니페스트가 가리키는데
                    // 파일이 없다는 것은 분석이 중간에 끊겼다는 뜻이라 사람이 알아야 한다.
                    Log.Warning(
                        "[배치 설계] 참조 프로시저의 명세가 없어 재료에서 제외합니다: {NodeKey} ({SpecPath})",
                        node.Key, absolute);
                    continue;
                }

                results.Add(Path.GetRelativePath(outputRoot, absolute));
            }

            return results;
        }

        // 매니페스트에서 이 클래스가 쓰는 두 칸만 받는다. MetadataExporter 의 전체
        // 모델을 여기서 다시 만들지 않는 이유는 그것이 private 이고, 이 판정에 필요한
        // 것이 Key 와 SpecPath 둘뿐이기 때문이다.
        private sealed class ManifestShape
        {
            public string Key { get; init; } = string.Empty;
            public List<ManifestNodeShape> Nodes { get; init; } = new();
        }

        private sealed class ManifestNodeShape
        {
            public string Key { get; init; } = string.Empty;
            public string SpecPath { get; init; } = string.Empty;
        }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter 'FullyQualifiedName~ReadProcedureReferences' --nologo`
Expected: PASS 4건

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Cli/BatchStepCatalog.cs tests/ReSet.Core.Tests/BatchStepCatalogTests.cs
git commit -m "feat(catalog): 의존 매니페스트에서 프로시저 타입 참조만 읽는다"
```

---

### Task 2: 진입점 목록을 폐포로 닫는다

Task 1의 한 걸음을 고정점까지 반복한다. 순환·순서·상한이 여기서 결정된다.

**Files:**
- Modify: `src/ReSet.Cli/BatchStepCatalog.cs`
- Test: `tests/ReSet.Core.Tests/BatchStepCatalogTests.cs`

**Interfaces:**
- Consumes: `BatchStepCatalog.ReadProcedureReferences(string outputRoot, string specRelativePath)` (Task 1)
- Produces: `public static ProcedureClosure CloseOverProcedureReferences(string outputRoot, IReadOnlyList<string> entryPointSpecPaths)` 와 `public sealed record ProcedureClosure(IReadOnlyList<string> SpecPaths, IReadOnlyList<string> Added, bool CapExceeded)`. `SpecPaths`는 진입점 + 더해진 것을 §6 순서로 담고, `Added`는 더해진 것만, `CapExceeded`는 상한에 걸려 멈췄는지다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/BatchStepCatalogTests.cs`의 클래스 안, `CreateOutputTree` **앞에** 넣는다.

```csharp
        /// <summary>
        /// 더해진 항목은 자기를 부른 항목 <b>바로 뒤</b>에 온다. LoadDefinitionsAsync 의
        /// 계약이 「입력 순서가 곧 배치 스텝 실행 순서」이고, 하위 프로시저는 부모 흐름
        /// 안에서 실행되므로 끝에 붙이면 실행 순서가 틀린다(설계서 §6).
        /// </summary>
        [Fact]
        public void CloseOverProcedureReferences_InsertsEachAdditionRightAfterItsReferrer()
        {
            var root = CreateManifestTree();
            try
            {
                WriteSpec(root, Path.Combine("Procedures", "dbo.USP_Tail"));

                var closure = BatchStepCatalog.CloseOverProcedureReferences(
                    root,
                    new[]
                    {
                        Path.Combine("Procedures", "dbo.USP_Parent", "docs", "Spec.md"),
                        Path.Combine("Procedures", "dbo.USP_Tail", "docs", "Spec.md")
                    });

                Assert.Equal(
                    new[]
                    {
                        "Procedures/dbo.USP_Parent/docs/Spec.md",
                        "Procedures/dbo.USP_Child/docs/Spec.md",
                        "Procedures/dbo.USP_Tail/docs/Spec.md"
                    },
                    closure.SpecPaths.Select(p => p.Replace(Path.DirectorySeparatorChar, '/')).ToList());

                Assert.Equal(
                    new[] { "Procedures/dbo.USP_Child/docs/Spec.md" },
                    closure.Added.Select(p => p.Replace(Path.DirectorySeparatorChar, '/')).ToList());
                Assert.False(closure.CapExceeded);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>
        /// 실물이 순환이다 - Summary 가 EXTRA 를 부르고 EXTRA 가 Summary 를 부른다.
        /// visited 가 없으면 끝나지 않는다.
        /// </summary>
        [Fact]
        public void CloseOverProcedureReferences_TerminatesOnACycle()
        {
            var root = CreateCyclicManifestTree();
            try
            {
                var closure = BatchStepCatalog.CloseOverProcedureReferences(
                    root,
                    new[] { Path.Combine("Procedures", "dbo.USP_A", "docs", "Spec.md") });

                Assert.Equal(
                    new[] { "Procedures/dbo.USP_A/docs/Spec.md", "Procedures/dbo.USP_B/docs/Spec.md" },
                    closure.SpecPaths.Select(p => p.Replace(Path.DirectorySeparatorChar, '/')).ToList());
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>
        /// 이미 진입점에 있는 것은 다시 더하지 않는다. 사람이 부모와 자식을 둘 다
        /// 골랐을 때 자식이 두 번 실리면 프롬프트에 같은 명세가 두 번 간다.
        /// </summary>
        [Fact]
        public void CloseOverProcedureReferences_DoesNotDuplicateAnAlreadySelectedProcedure()
        {
            var root = CreateManifestTree();
            try
            {
                var closure = BatchStepCatalog.CloseOverProcedureReferences(
                    root,
                    new[]
                    {
                        Path.Combine("Procedures", "dbo.USP_Parent", "docs", "Spec.md"),
                        Path.Combine("Procedures", "dbo.USP_Child", "docs", "Spec.md")
                    });

                Assert.Empty(closure.Added);
                Assert.Equal(2, closure.SpecPaths.Count);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>
        /// 폐포가 진입점의 2배를 넘으면 더 넓히지 않는다. BatchStepPlanParser.MaxSteps 가
        /// 이미 쓰는 폭주 방어와 같은 관용이다(설계서 §5).
        /// </summary>
        [Fact]
        public void CloseOverProcedureReferences_StopsAtTheCapAndReportsIt()
        {
            var root = CreateChainManifestTree(length: 6);
            try
            {
                var closure = BatchStepCatalog.CloseOverProcedureReferences(
                    root,
                    new[] { Path.Combine("Procedures", "dbo.USP_C0", "docs", "Spec.md") });

                Assert.True(closure.CapExceeded);
                Assert.Equal(2, closure.SpecPaths.Count);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
```

같은 클래스의 `CreateManifestTree` **뒤에** 헬퍼 둘을 넣는다.

```csharp
        /// <summary>A 가 B 를 부르고 B 가 A 를 부르는 순환 트리.</summary>
        private static string CreateCyclicManifestTree()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-Cycle-{Guid.NewGuid():N}");
            WriteSpec(root, Path.Combine("Procedures", "dbo.USP_A"));
            WriteSpec(root, Path.Combine("Procedures", "dbo.USP_B"));
            WriteManifest(root, "dbo.USP_A", "dbo.USP_B");
            WriteManifest(root, "dbo.USP_B", "dbo.USP_A");
            return root;
        }

        /// <summary>C0 → C1 → … 로 이어지는 사슬. 상한 시험용이다.</summary>
        private static string CreateChainManifestTree(int length)
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-Chain-{Guid.NewGuid():N}");
            for (var i = 0; i < length; i++)
            {
                WriteSpec(root, Path.Combine("Procedures", $"dbo.USP_C{i}"));
            }

            for (var i = 0; i < length - 1; i++)
            {
                WriteManifest(root, $"dbo.USP_C{i}", $"dbo.USP_C{i + 1}");
            }

            return root;
        }

        private static void WriteManifest(string root, string owner, string callee)
        {
            var rawDirectory = Path.Combine(root, "Procedures", owner, "raw");
            Directory.CreateDirectory(rawDirectory);
            File.WriteAllText(
                Path.Combine(rawDirectory, "dependency-manifest.json"),
                $$"""
                {
                  "Key": "DB.{{owner}}.Procedure",
                  "Nodes": [
                    { "Key": "DB.{{owner}}.Procedure", "Status": "Succeeded", "SpecPath": "docs/Spec.md" },
                    { "Key": "DB.{{callee}}.Procedure", "Status": "Succeeded", "SpecPath": "../{{callee}}/docs/Spec.md" }
                  ]
                }
                """);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter 'FullyQualifiedName~CloseOverProcedureReferences' --nologo`
Expected: 컴파일 실패 — `'BatchStepCatalog' has no definition for 'CloseOverProcedureReferences'`

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Cli/BatchStepCatalog.cs`의 `ReadProcedureReferences` **앞**에 레코드와 메서드를 넣는다.

```csharp
        /// <summary>
        /// 진입점 목록을 프로시저 참조 폐포로 닫은 결과.
        /// </summary>
        /// <param name="SpecPaths">진입점 + 더해진 것. 순서가 실행 순서다.</param>
        /// <param name="Added">더해진 것만. 호출부가 사람에게 알리는 데 쓴다.</param>
        /// <param name="CapExceeded">상한에 걸려 더 넓히지 않고 멈췄는가.</param>
        public sealed record ProcedureClosure(
            IReadOnlyList<string> SpecPaths,
            IReadOnlyList<string> Added,
            bool CapExceeded);

        /// <summary>
        /// 사람이 고른 <b>진입점</b> 목록에, 그것이 부르는 프로시저 타입 참조를 고정점까지
        /// 더한다. 사람의 선택 의미는 바뀌지 않는다 - 진입점은 그대로이고 <b>재료</b>만 닫는다.
        ///
        /// [왜 참조자 바로 뒤인가] <see cref="LoadDefinitionsAsync"/>의 계약이 순서를
        /// 실행 순서로 쓴다. 하위 프로시저는 부모 흐름 <b>안에서</b> 실행되므로 끝에
        /// 붙이면 실행 순서가 틀린다(설계서 §6).
        ///
        /// [왜 상한이 필요한가] 매니페스트가 예상보다 넓게 물리면 프롬프트가 폭주한다.
        /// <c>BatchStepPlanParser.MaxSteps</c>가 이미 쓰는 방어와 같은 관용이다.
        /// </summary>
        public static ProcedureClosure CloseOverProcedureReferences(
            string outputRoot, IReadOnlyList<string> entryPointSpecPaths)
        {
            if (entryPointSpecPaths is null || entryPointSpecPaths.Count == 0)
            {
                return new ProcedureClosure(Array.Empty<string>(), Array.Empty<string>(), false);
            }

            var cap = entryPointSpecPaths.Count * 2;
            var ordered = new List<string>(entryPointSpecPaths);
            var seen = new HashSet<string>(entryPointSpecPaths, StringComparer.OrdinalIgnoreCase);
            var added = new List<string>();
            var capExceeded = false;

            // 인덱스로 돈다 - 더해진 항목도 자기 참조를 펼쳐야 고정점이 된다.
            for (var i = 0; i < ordered.Count && !capExceeded; i++)
            {
                var insertAt = i + 1;
                foreach (var reference in ReadProcedureReferences(outputRoot, ordered[i]))
                {
                    if (!seen.Add(reference)) continue;

                    if (ordered.Count >= cap)
                    {
                        capExceeded = true;
                        Log.Warning(
                            "[배치 설계] 참조 폐포가 상한({Cap})에 걸려 더 넓히지 않습니다. 진입점 {EntryCount}개.",
                            cap, entryPointSpecPaths.Count);
                        break;
                    }

                    ordered.Insert(insertAt++, reference);
                    added.Add(reference);
                }
            }

            return new ProcedureClosure(ordered, added, capExceeded);
        }
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter 'FullyQualifiedName~CloseOverProcedureReferences' --nologo`
Expected: PASS 4건

- [ ] **Step 5: 전체 테스트로 회귀를 본다**

Run: `dotnet test --nologo`
Expected: 실패 0 · 건너뜀 0 · 경고 0

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Cli/BatchStepCatalog.cs tests/ReSet.Core.Tests/BatchStepCatalogTests.cs
git commit -m "feat(catalog): 진입점 목록을 프로시저 참조 폐포로 닫는다"
```

---

### Task 3: 실물 코퍼스로 12 → 14를 잠근다

단위 픽스처는 「내가 만든 트리에서 동작한다」만 증명한다. 실제 매니페스트 형식·순환·경로 형태로도 서는지는 코퍼스로만 답한다.

**Files:**
- Create: `tests/ReSet.Core.Tests/ProcedureClosureCorpusTests.cs`

**Interfaces:**
- Consumes: `BatchStepCatalog.CloseOverProcedureReferences(string, IReadOnlyList<string>)` → `ProcedureClosure` (Task 2)
- Produces: 없음(검증 전용)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System;
using System.IO;
using System.Linq;
using ReSet.Cli;
using Xunit;
using Xunit.Abstractions;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실물 매니페스트로 폐포를 잰다.
    ///
    /// [왜 단위 픽스처로 부족한가] 픽스처는 내가 만든 트리에서 동작한다는 것만 증명한다.
    /// 실제 SpecPath 는 `../dbo.X/docs/Spec.md` 이고 `Summary → EXTRA → Summary` 가
    /// 실제 순환이며, 매니페스트는 BOM 이 붙은 UTF-8 이다 - 셋 다 픽스처가 흉내 낸 것이지
    /// 실물이 아니다.
    ///
    /// [왜 이름까지 못박는가] 개수만 보면 「둘이 빠지고 다른 둘이 들어와도」 통과한다.
    /// 이 검사가 지키는 것은 개수가 아니라 <b>어느 프로시저가 재료가 되는가</b>다.
    /// </summary>
    public class ProcedureClosureCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public ProcedureClosureCorpusTests(ITestOutputHelper output) => _output = output;

        [SkippableFact]
        public void Batch4Roster_ClosesFromTwelveToFourteen()
        {
            var repoRoot = RepoPaths.FindRepoRoot();
            Skip.If(string.IsNullOrEmpty(repoRoot), CorpusSkip.Reason);

            var outputRoot = Path.Combine(repoRoot!, "output");
            Skip.IfNot(Directory.Exists(Path.Combine(outputRoot, "Procedures")), CorpusSkip.Reason);

            var promptContext = Path.Combine(
                repoRoot!, "output.bak-stage4-control-20260828",
                "Jobs", "POQSettleBatch4", "raw", "prompt-context.md");
            Skip.IfNot(File.Exists(promptContext), CorpusSkip.Reason);

            var roster = File.ReadLines(promptContext)
                .Where(line => line.StartsWith("Filename: ", StringComparison.Ordinal))
                .Select(line => line["Filename: ".Length..].Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => Path.Combine("Procedures", name, "docs", "Spec.md"))
                .Where(relative => File.Exists(Path.Combine(outputRoot, relative)))
                .ToList();

            Skip.IfNot(roster.Count == 12, $"로스터가 12편이 아니라 {roster.Count}편이다 - 코퍼스가 바뀌었다.");

            var closure = BatchStepCatalog.CloseOverProcedureReferences(outputRoot, roster);

            _output.WriteLine($"진입점 {roster.Count} → 폐포 {closure.SpecPaths.Count} · 더해짐 {closure.Added.Count}");
            foreach (var added in closure.Added) _output.WriteLine("  + " + added);

            Assert.False(closure.CapExceeded);
            Assert.Equal(14, closure.SpecPaths.Count);
            Assert.Equal(
                new[]
                {
                    "Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md",
                    "Procedures/dbo.UP_Util_Settle_Summary_AcqManual/docs/Spec.md"
                },
                closure.Added
                    .Select(p => p.Replace(Path.DirectorySeparatorChar, '/'))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList());
        }

        /// <summary>
        /// 함수는 30건 참조되지만 하나도 더해지면 안 된다(설계서 §2).
        /// </summary>
        [SkippableFact]
        public void Closure_NeverAddsAFunctionSpec()
        {
            var repoRoot = RepoPaths.FindRepoRoot();
            Skip.If(string.IsNullOrEmpty(repoRoot), CorpusSkip.Reason);

            var outputRoot = Path.Combine(repoRoot!, "output");
            var proceduresDirectory = Path.Combine(outputRoot, "Procedures");
            Skip.IfNot(Directory.Exists(proceduresDirectory), CorpusSkip.Reason);

            var everyProcedure = Directory.GetDirectories(proceduresDirectory)
                .Select(d => Path.Combine("Procedures", Path.GetFileName(d), "docs", "Spec.md"))
                .Where(relative => File.Exists(Path.Combine(outputRoot, relative)))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            var closure = BatchStepCatalog.CloseOverProcedureReferences(outputRoot, everyProcedure);

            Assert.DoesNotContain(
                closure.SpecPaths,
                p => p.Replace(Path.DirectorySeparatorChar, '/').Contains("/Functions/", StringComparison.OrdinalIgnoreCase));
        }
    }
}
```

- [ ] **Step 2: 실패(또는 스킵)를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter 'FullyQualifiedName~ProcedureClosureCorpusTests' --nologo --logger 'console;verbosity=detailed'`
Expected: 코퍼스 심링크가 없으면 **Skip**. 있으면 PASS. Skip 이 나오면 워크트리에 심링크 셋을 걸고 다시 돌린다.

```bash
ln -s <메인 저장소>/output output
ln -s <메인 저장소>/output.bak-2026-08-22 output.bak-2026-08-22
ln -s <메인 저장소>/output.bak-stage4-control-20260828 output.bak-stage4-control-20260828
```

- [ ] **Step 3: 통과를 확인한다**

Run: 같은 명령
Expected: PASS 2건. 출력에 `진입점 12 → 폐포 14 · 더해짐 2`가 찍힌다.

- [ ] **Step 4: 커밋**

```bash
git add tests/ReSet.Core.Tests/ProcedureClosureCorpusTests.cs
git commit -m "test(catalog): 실물 코퍼스로 폐포 12→14와 함수 배제를 잠근다"
```

---

### Task 4: TUI 흐름에 폐포를 주입한다

`selectedFiles`(사람의 진입점)를 폐포로 통과시킨 뒤 기존 `specsData`·`LoadDefinitionsAsync` 경로를 그대로 탄다.

**Files:**
- Modify: `src/ReSet.Cli/Program.cs:1483-1497` (specsData 조립) · `:1515-1517` (LoadDefinitionsAsync 호출)

**Interfaces:**
- Consumes: `BatchStepCatalog.CloseOverProcedureReferences(string, IReadOnlyList<string>)` → `ProcedureClosure` (Task 2)
- Produces: 없음(배선)

- [ ] **Step 1: 폐포를 계산하고 사람에게 알린다**

`Program.cs`에서 `var specsData = new List<(string FileName, string Content)>();`(`:1483`) **바로 앞**에 넣는다.

```csharp
                        // 사람이 고른 것은 진입점이고, 그것이 부르는 프로시저 타입 참조는
                        // 도구가 재료에 더한다(설계서 §4). 함수는 더하지 않는다 - 부모
                        // 명세의 「참조 함수 표」가 호출식 전문을 이미 담는다.
                        var closure = BatchStepCatalog.CloseOverProcedureReferences(outputDir, selectedFiles);
                        foreach (var added in closure.Added)
                        {
                            AnsiConsole.MarkupLine(
                                $"[cyan]참조 프로시저를 재료에 추가했습니다: {Markup.Escape(added)}[/]");
                            Serilog.Log.Information("[배치 설계] 참조 프로시저 재료 추가: {SpecPath}", added);
                        }

                        if (closure.CapExceeded)
                        {
                            AnsiConsole.MarkupLine(
                                "[yellow]경고: 참조 폐포가 상한에 걸려 일부 참조 프로시저가 재료에서 빠졌습니다.[/]");
                        }
```

- [ ] **Step 2: 두 소비처를 폐포로 바꾼다**

`:1484`의 `foreach (var fileName in selectedFiles)`를 아래로 바꾼다.

```csharp
                        foreach (var fileName in closure.SpecPaths)
```

그리고 `:1515`의 호출을 아래로 바꾼다.

```csharp
                            var loadResult = await BatchStepCatalog.LoadDefinitionsAsync(
                                outputDir, closure.SpecPaths, activeCts.Token);
```

- [ ] **Step 3: 빌드한다**

Run: `dotnet build src/ReSet.Cli/ReSet.Cli.csproj --nologo`
Expected: 경고 0 · 오류 0

- [ ] **Step 4: 전체 테스트로 회귀를 본다**

Run: `dotnet test --nologo`
Expected: 실패 0 · 건너뜀 0 · 경고 0

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Cli/Program.cs
git commit -m "feat(cli): TUI 흐름의 재료를 프로시저 참조 폐포로 닫는다"
```

---

### Task 5: CLI 배치 모드에 폐포를 주입한다

`--job-name`으로 분석 직후 Job을 만드는 경로(`:964~:972`)에도 같은 폐포를 건다. 이 경로를 빼면 배치 모드로 돈 판만 재료가 좁아, 같은 도구가 경로에 따라 다른 재료를 쓰게 된다.

**Files:**
- Modify: `src/ReSet.Cli/Program.cs:964-972`

**Interfaces:**
- Consumes: `BatchStepCatalog.CloseOverProcedureReferences(string, IReadOnlyList<string>)` → `ProcedureClosure` (Task 2) · `BatchStepCatalog.LoadDefinitionsAsync(string, IEnumerable<string>, CancellationToken)` → `BatchStepLoadResult`
- Produces: 없음(배선)

- [ ] **Step 1: 현재 코드를 읽는다**

Run: `sed -n '960,975p' src/ReSet.Cli/Program.cs`

이 흐름은 `specsData`와 `spDefs`를 분석 루프에서 직접 쌓는다(`:882`). 진입점 상대 경로가 손에 없으므로, 폐포를 걸려면 먼저 그것을 만들어야 한다.

- [ ] **Step 2: 진입점 상대 경로를 만들고 폐포를 건다**

`if (!string.IsNullOrEmpty(cliArgs.JobName) && specsData.Count > 0)`(`:964`) 블록 **안**, `RunConsolidatedPipelineAsync` 호출 **앞**에 넣는다.

```csharp
                        // TUI 흐름과 같은 재료 폐포를 건다(설계서 §7-2). 이 경로를 빼면
                        // 같은 도구가 진입 경로에 따라 다른 재료를 쓰게 된다.
                        var entryPointSpecPaths = specsData
                            .Select(spec => Path.Combine("Procedures", spec.FileName, "docs", "Spec.md"))
                            .Where(relative => File.Exists(Path.Combine(outputDir, relative)))
                            .ToList();

                        var closure = BatchStepCatalog.CloseOverProcedureReferences(outputDir, entryPointSpecPaths);

                        foreach (var added in closure.Added)
                        {
                            Serilog.Log.Information("[배치 설계] 참조 프로시저 재료 추가: {SpecPath}", added);

                            var addedIdentifier = BatchStepCatalog.ExtractProcedureIdentifier(added);
                            var addedFullPath = Path.Combine(outputDir, added);
                            if (addedIdentifier is null || !File.Exists(addedFullPath)) continue;

                            specsData.Add((addedIdentifier, await File.ReadAllTextAsync(addedFullPath, activeCts.Token)));
                        }

                        if (closure.Added.Count > 0)
                        {
                            var addedLoad = await BatchStepCatalog.LoadDefinitionsAsync(
                                outputDir, closure.Added, activeCts.Token);
                            spDefs.AddRange(addedLoad.Definitions);
                        }

                        if (closure.CapExceeded)
                        {
                            Serilog.Log.Warning("[배치 설계] 참조 폐포가 상한에 걸려 일부 참조 프로시저가 재료에서 빠졌습니다.");
                        }
```

> **왜 여기서는 `closure.SpecPaths`를 안 쓰는가**: 이 흐름의 `specsData`는 분석 루프가 이미 채웠고 그 순서가 사람이 준 `--target` 순서다. 그것을 폐포 순서로 갈아엎으면 실행 순서가 바뀐다. 더해진 것만 뒤에 붙이는 것이 이 경로에서 순서를 안 흐트러뜨리는 방법이다.

- [ ] **Step 3: 빌드한다**

Run: `dotnet build src/ReSet.Cli/ReSet.Cli.csproj --nologo`
Expected: 경고 0 · 오류 0

- [ ] **Step 4: 전체 테스트로 회귀를 본다**

Run: `dotnet test --nologo`
Expected: 실패 0 · 건너뜀 0 · 경고 0

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Cli/Program.cs
git commit -m "feat(cli): 배치 모드 흐름에도 같은 재료 폐포를 건다"
```

---

### Task 6: 판독 문서의 열린 물음을 닫는다

판독 §9의 「사람에게 물을 것: 로스터 12가 폐포 14보다 작은 것이 의도인가」가 답을 얻었다. 그 문면을 그대로 두면 다음 사람이 같은 물음에 다시 착수한다.

**Files:**
- Modify: `docs/audit-reports/sweeps/2026-08-29-critic-exception-axis.md` (§9의 「사람에게 물을 것」 상자)

**Interfaces:**
- Consumes: 없음
- Produces: 없음

- [ ] **Step 1: 현재 문면을 찾는다**

Run: `grep -n '사람에게 물을 것' docs/audit-reports/sweeps/2026-08-29-critic-exception-axis.md`

- [ ] **Step 2: 답으로 바꾼다**

그 상자를 아래로 바꾼다(앞뒤 문단은 건드리지 않는다).

```markdown
> ✅ **답이 나왔다 (2026-08-30).** 로스터는 **진입점**이지 폐포가 아니다 — 사람이 TUI에서
> 고르는 것이고(`Program.cs:1474`), 참조 객체는 진입점이 아니다. 실측으로 갈리는 것은
> 타입이다: 참조 32건 중 **함수 30건은 결손이 아니고**(부모 명세의 「참조 함수 표」가
> 호출식 전문을 담는다) **프로시저 2건만** 진짜 결손이었다.
>
> 그리고 **참조 명세의 내용은 프롬프트에 실리지 않았다** — 링크만 상대 경로 텍스트로
> 간다(하위 명세에만 있는 `4001`~`4007`이 프롬프트에 0건). 그것을 닫는 것이
> `docs/superpowers/specs/2026-08-30-material-closure-design.md`이고, 도구가 재료
> 폐포를 닫는다.
>
> ⚠️ **다만 이번 결함의 원인을 재료 부재로 돌리지 말 것.** 부모 명세의 요약 문장이
> 계약을 이미 담았고 **Critic이 바로 그 문장으로** 재매핑을 잡아냈다. 폐포가 고치는
> 것은 **L1 귀속**과 **Actor가 쥔 정밀도**이지 §5-1의 실패 양식이 아니다.
```

- [ ] **Step 3: 커밋**

```bash
git add docs/audit-reports/sweeps/2026-08-29-critic-exception-axis.md
git commit -m "docs: 로스터 12 대 14의 물음을 답으로 닫는다"
```

---

## 실행 후 남는 것 — 다음 통제군이 답할 것

이 계획은 **재료를 대는 것까지**다. 효과는 다음 생성 회차에서만 드러난다(설계서 §9).

`Batch4`(12편) ↔ `Batch5`(폐포 14편)로 **변인 하나**를 세워 넷을 본다.

1. `S12`·`S13`의 `LegacyProcedures`가 하위 SP를 가리키는가
2. 그 단계의 오류 코드가 `4000~4008`·`ERROR_NUMBER`로 서는가
3. `CheckLegacyStepErrorCodeInvention`이 그 단계를 **올바른 집합으로** 판정하는가
4. 프롬프트 증가가 실측 **+8%** 안에 있는가

**다음 판도 `sonnet`이어야 한다** — 모델을 함께 바꾸면 귀속이 섞인다.
