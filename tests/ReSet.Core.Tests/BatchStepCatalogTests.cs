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
    }
}
