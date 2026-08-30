using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Cli;
using ReSet.Core.Models;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchStepCatalogTests
    {
        [Fact]
        public void FindStepCandidates_ReturnsProcedureSpecsFromCurrentAndExternalDatabases()
        {
            var root = CreateOutputTree();
            try
            {
                var candidates = BatchStepCatalog.FindStepCandidates(root)
                    .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();

                Assert.Equal(
                    new[]
                    {
                        "External/AuditDB/Procedures/dbo.USP_External/docs/Spec.md",
                        "Procedures/dbo.USP_Root/docs/Spec.md"
                    },
                    candidates);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        // 회귀 재현: 통합 배치 파이프라인의 목차 커버리지 검사와 AI 프롬프트의
        // "Filename:" 레이블이 명세서를 구분하는 유일한 근거가 이 식별자다.
        // FindStepCandidates가 실제로 돌려주는 두 레이아웃(현재 DB / External DB)
        // 모두에서, 마지막 세그먼트("Spec.md")가 아니라 "Procedures" 바로 다음
        // 세그먼트("스키마.이름")가 나와야 한다.
        [Theory]
        [InlineData("Procedures/dbo.USP_Root/docs/Spec.md", "dbo.USP_Root")]
        [InlineData("External/AuditDB/Procedures/dbo.USP_External/docs/Spec.md", "dbo.USP_External")]
        public void ExtractProcedureIdentifier_ReturnsTheBareProcedureNameNotTheFilename(
            string relativePath, string expectedIdentifier)
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);

            var identifier = BatchStepCatalog.ExtractProcedureIdentifier(normalized);

            Assert.Equal(expectedIdentifier, identifier);
            Assert.NotEqual("Spec.md", identifier);
        }

        // 실측 결함 재현: 재검토가 지적한 바로 그 결함은 "마지막 경로 세그먼트를
        // 쓴다"는 순진한 접근이었다 — 어느 레이아웃에서든 마지막 세그먼트는 항상
        // "Spec.md"이므로, 이걸 식별자로 쓰면 서로 다른 프로시저 두 개가 같은
        // 값으로 뭉개진다. 목차 커버리지 검사는 이 값을 기준으로 명세서를
        // 구분하므로, 뭉개지면 N개 명세서가 1개로 보여 하나만 빼고 전부 "커버
        // 안 됨"으로 잘못 보고되거나(또는 그 반대로 과소 보고) 검사가 무의미해진다.
        // 이 테스트는 그 순진한 접근이 실제로 충돌한다는 사실과, 우리 픽스가
        // 그 충돌을 피한다는 사실을 나란히 고정한다.
        [Fact]
        public void ExtractProcedureIdentifier_UnlikeTakingTheLastPathSegment_DoesNotCollapseDifferentProceduresToTheSameValue()
        {
            var plain = "Procedures/dbo.USP_Root/docs/Spec.md".Replace('/', Path.DirectorySeparatorChar);
            var external = "External/AuditDB/Procedures/dbo.USP_External/docs/Spec.md".Replace('/', Path.DirectorySeparatorChar);

            // 순진한 접근("마지막 세그먼트를 쓴다", 코드 리뷰가 지목한 원래 결함의
            // 본질)은 두 서로 다른 프로시저를 같은 값으로 충돌시킨다.
            Assert.Equal(Path.GetFileName(plain), Path.GetFileName(external));
            Assert.Equal("Spec.md", Path.GetFileName(plain));

            // 픽스(ExtractProcedureIdentifier)는 충돌하지 않는다.
            var plainIdentifier = BatchStepCatalog.ExtractProcedureIdentifier(plain);
            var externalIdentifier = BatchStepCatalog.ExtractProcedureIdentifier(external);
            Assert.NotEqual(plainIdentifier, externalIdentifier);
            Assert.Equal("dbo.USP_Root", plainIdentifier);
            Assert.Equal("dbo.USP_External", externalIdentifier);
        }

        [Theory]
        [InlineData("Jobs/Nightly/validation/raw/Spec.md")]
        [InlineData("Functions/dbo.UF_Helper/docs/Spec.md")]
        [InlineData("Spec.md")]
        public void ExtractProcedureIdentifier_ReturnsNullForShapesItDoesNotRecognize(string relativePath)
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);

            Assert.Null(BatchStepCatalog.ExtractProcedureIdentifier(normalized));
        }

        // FindStepCandidates가 통과시킨 모든 경로는 IsProcedureSpec을 만족했다.
        // IsProcedureSpec이 이제 ExtractProcedureIdentifier에 판정을 위임하므로, 이
        // 테스트는 그 위임이 실제로 유지되는지(즉 FindStepCandidates가 절대 식별자를
        // 뽑을 수 없는 경로를 흘려보내지 않는지)를 고정한다.
        [Fact]
        public void FindStepCandidates_EveryReturnedPathYieldsANonNullIdentifier()
        {
            var root = CreateOutputTree();
            try
            {
                var candidates = BatchStepCatalog.FindStepCandidates(root);

                Assert.NotEmpty(candidates);
                Assert.All(candidates, path => Assert.NotNull(BatchStepCatalog.ExtractProcedureIdentifier(path)));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void FindStepCandidates_ExcludesFunctionsAndJobArtifacts()
        {
            var root = CreateOutputTree();
            try
            {
                var candidates = BatchStepCatalog.FindStepCandidates(root)
                    .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
                    .ToList();

                Assert.DoesNotContain(candidates, path => path.Contains("/Functions/"));
                Assert.DoesNotContain(candidates, path => path.StartsWith("Functions/", StringComparison.Ordinal));
                Assert.DoesNotContain(candidates, path => path.StartsWith("Jobs/", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void FindStepCandidates_ReturnsEmptyWhenOutputRootIsMissing()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"ReSet-Missing-{Guid.NewGuid():N}");

            Assert.Empty(BatchStepCatalog.FindStepCandidates(missing));
        }

        [Fact]
        public async Task LoadDefinitionsAsync_PreservesInputOrder()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchLoad-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_First", "USP_First");
            WriteProcedure(root, "dbo.USP_Second", "USP_Second");
            WriteProcedure(root, "dbo.USP_Third", "USP_Third");
            try
            {
                var ordered = new[]
                {
                    Path.Combine("Procedures", "dbo.USP_Third", "docs", "Spec.md"),
                    Path.Combine("Procedures", "dbo.USP_First", "docs", "Spec.md"),
                    Path.Combine("Procedures", "dbo.USP_Second", "docs", "Spec.md")
                };

                var result = await BatchStepCatalog.LoadDefinitionsAsync(root, ordered, CancellationToken.None);

                Assert.Equal(
                    new[] { "USP_Third", "USP_First", "USP_Second" },
                    result.Definitions.Select(definition => definition.Name));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task LoadDefinitionsAsync_ReportsMissingMetadataSeparately()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchLoad-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_Complete", "USP_Complete");
            WriteSpecOnly(root, "dbo.USP_NoMetadata");
            try
            {
                var selected = new[]
                {
                    Path.Combine("Procedures", "dbo.USP_Complete", "docs", "Spec.md"),
                    Path.Combine("Procedures", "dbo.USP_NoMetadata", "docs", "Spec.md")
                };

                var result = await BatchStepCatalog.LoadDefinitionsAsync(root, selected, CancellationToken.None);

                Assert.Equal("USP_Complete", Assert.Single(result.Definitions).Name);
                Assert.Equal(
                    Path.Combine("Procedures", "dbo.USP_NoMetadata", "docs", "Spec.md"),
                    Assert.Single(result.MissingMetadata));
                Assert.Empty(result.FailedToParse);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task LoadDefinitionsAsync_ReportsUnparsableMetadataSeparately()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchLoad-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_Broken", "USP_Broken");
            File.WriteAllText(
                Path.Combine(root, "Procedures", "dbo.USP_Broken", "raw", "metadata.json"),
                "{ this is not json");
            try
            {
                var selected = new[] { Path.Combine("Procedures", "dbo.USP_Broken", "docs", "Spec.md") };

                var result = await BatchStepCatalog.LoadDefinitionsAsync(root, selected, CancellationToken.None);

                Assert.Empty(result.Definitions);
                Assert.Empty(result.MissingMetadata);
                Assert.Equal(
                    Path.Combine("Procedures", "dbo.USP_Broken", "docs", "Spec.md"),
                    Assert.Single(result.FailedToParse));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void WriteProcedure(string root, string objectDirectory, string procedureName)
        {
            WriteSpecOnly(root, objectDirectory);
            var rawDirectory = Path.Combine(root, "Procedures", objectDirectory, "raw");
            Directory.CreateDirectory(rawDirectory);
            var definition = new SpDefinition { Schema = "dbo", Name = procedureName, DdlText = "SELECT 1;" };
            File.WriteAllText(
                Path.Combine(rawDirectory, "metadata.json"),
                System.Text.Json.JsonSerializer.Serialize(definition));
        }

        private static void WriteSpecOnly(string root, string objectDirectory)
        {
            var docsDirectory = Path.Combine(root, "Procedures", objectDirectory, "docs");
            Directory.CreateDirectory(docsDirectory);
            File.WriteAllText(Path.Combine(docsDirectory, "Spec.md"), "# Spec");
        }

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
        /// 「문법 오류」(위 테스트, JsonException) 와는 다른 경우다 - 이 JSON 은 문법이
        /// 맞지만 "Nodes": null 로 퇴화한 모양이다. System.Text.Json 은 키가 명시적으로
        /// null 이면 ManifestShape.Nodes 의 `= new()` 기본값을 덮어써 null 을 그대로
        /// 싣는다. manifest.Nodes 를 null 검사 없이 순회하면 NullReferenceException 이
        /// catch 블록 밖에서 던져진다 - §8 의 "예외를 밖으로 던지지 않는다" 위반이다.
        /// </summary>
        [Fact]
        public void ReadProcedureReferences_IsSilentWhenTheManifestHasAnExplicitNullNodes()
        {
            var root = CreateManifestTree();
            try
            {
                File.WriteAllText(
                    Path.Combine(root, "Procedures", "dbo.USP_Parent", "raw", "dependency-manifest.json"),
                    """
                    { "Key": "DB.dbo.USP_Parent.Procedure", "Nodes": null }
                    """);

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

        private static string CreateOutputTree()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchCatalog-{Guid.NewGuid():N}");
            WriteSpec(root, Path.Combine("Procedures", "dbo.USP_Root"));
            WriteSpec(root, Path.Combine("Functions", "dbo.UF_Helper"));
            WriteSpec(root, Path.Combine("External", "AuditDB", "Procedures", "dbo.USP_External"));
            WriteSpec(root, Path.Combine("External", "AuditDB", "Functions", "dbo.UF_ExternalHelper"));
            WriteSpec(root, Path.Combine("Jobs", "Nightly", "validation", "raw"));
            return root;
        }

        private static void WriteSpec(string root, string relativeObjectDirectory)
        {
            var docsDirectory = Path.Combine(root, relativeObjectDirectory, "docs");
            Directory.CreateDirectory(docsDirectory);
            File.WriteAllText(Path.Combine(docsDirectory, "Spec.md"), "# Spec");
        }

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

        /// <summary>
        /// closure.SpecPaths 안에 있는 항목은 그 순서대로 나와야 한다 - 원래 리스트의
        /// 순서(끝에 덧붙는 순서)와는 무관하다. 배치 모드가 참조 프로시저를 끝에
        /// 붙인 뒤 이 헬퍼로 재정렬하는 것이 바로 이 계약에 기댄다.
        /// </summary>
        [Fact]
        public void ReorderByClosure_OrdersMatchedItemsByClosureSpecPathOrder()
        {
            var pathA = Path.Combine("Procedures", "dbo.A", "docs", "Spec.md");
            var pathB = Path.Combine("Procedures", "dbo.B", "docs", "Spec.md");
            var closure = new BatchStepCatalog.ProcedureClosure(
                new[] { pathB, pathA }, Array.Empty<string>(), false);

            // 원래 리스트 순서는 A, B(끝에 덧붙은 것처럼) - 폐포 순서는 B, A다.
            var items = new[] { ("A", pathA), ("B", pathB) };

            var reordered = BatchStepCatalog.ReorderByClosure(items, item => item.Item2, closure);

            Assert.Equal(new[] { "B", "A" }, reordered.Select(i => i.Item1));
        }

        /// <summary>
        /// closure.SpecPaths에 없는 항목(경로를 못 만들었거나 폐포가 모르는 것)은
        /// 하나도 사라지면 안 된다 - 재정렬의 핵심 안전장치다. 이 항목은 자신의
        /// 원래 바로 앞에 있던, 매치된 항목이 재정렬로 어디로 옮겨가든 그 뒤에
        /// 붙어서 원래 상대 위치를 유지한다.
        /// </summary>
        [Fact]
        public void ReorderByClosure_KeepsUnmatchedItemsAndLosesNothing()
        {
            var pathA = Path.Combine("Procedures", "dbo.A", "docs", "Spec.md");
            var pathB = Path.Combine("Procedures", "dbo.B", "docs", "Spec.md");
            // 폐포 순서는 B, A(입력 순서 A, B와 반대) - X는 아무 매치도 안 되고 원래
            // 아무 매치 앞에도 없었으므로 맨 앞에 남는다. Y는 원래 A 바로 뒤에
            // 있었으므로, A가 재정렬로 어디로 옮겨가든 A 바로 뒤에 붙는다.
            var closure = new BatchStepCatalog.ProcedureClosure(
                new[] { pathB, pathA }, Array.Empty<string>(), false);

            var items = new[]
            {
                ("X", (string?)null),
                ("A", pathA),
                ("Y", (string?)null),
                ("B", pathB)
            };

            var reordered = BatchStepCatalog.ReorderByClosure(items, item => item.Item2, closure);

            Assert.Equal(new[] { "X", "B", "A", "Y" }, reordered.Select(i => i.Item1));
            Assert.Equal(items.Length, reordered.Count);
        }

        [Fact]
        public void ReorderByClosure_ReturnsEmptyListForEmptyInput()
        {
            var closure = new BatchStepCatalog.ProcedureClosure(
                Array.Empty<string>(), Array.Empty<string>(), false);

            var reordered = BatchStepCatalog.ReorderByClosure(
                Array.Empty<(string, string?)>(), item => item.Item2, closure);

            Assert.Empty(reordered);
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
    }
}
