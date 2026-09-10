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
    /// <summary>
    /// `--plan-only` 가 저장된 산출물만으로 계획 수립 재료 한 벌(명세서 본문 + 정적 분석
    /// 정의)을 갖추는 규칙. TUI 2번 메뉴가 사람의 선택으로 하는 일을 `--sp` 나열로 한다.
    /// </summary>
    public class PlanOnlyMaterialLoaderTests
    {
        [Fact]
        public async Task 진입점이_부르는_참조_프로시저를_참조자_뒤에_더한다()
        {
            // 사람이 고른 것은 진입점일 뿐이다 - 그것이 부르는 프로시저의 명세를
            // 도구가 재료에 더한다(TUI 흐름과 같은 폐포).
            var root = CreateTree();
            try
            {
                var materials = await PlanOnlyMaterialLoader.LoadAsync(
                    root, new[] { "dbo.USP_Parent" }, CancellationToken.None);

                Assert.Equal(
                    new[] { "dbo.USP_Parent", "dbo.USP_Child" },
                    materials.Specs.Select(spec => spec.FileName));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task 명세서_순서와_정의_순서가_어긋나지_않는다()
        {
            // 실측된 결함: 참조 프로시저를 목록 끝에 덧붙이면 실행 순서가 폐포 순서와
            // 어긋난다. LoadDefinitionsAsync 의 계약이 입력 순서를 실행 순서로 쓰므로
            // 두 목록의 순서가 같아야 지시서의 스텝 번호가 계획서와 맞는다.
            var root = CreateTree();
            try
            {
                var materials = await PlanOnlyMaterialLoader.LoadAsync(
                    root, new[] { "dbo.USP_Parent" }, CancellationToken.None);

                Assert.Equal(
                    materials.Specs.Select(spec => spec.FileName),
                    materials.Definitions.Select(def => $"{def.Schema}.{def.Name}"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task 명세서_본문을_실제로_읽어_담는다()
        {
            var root = CreateTree();
            try
            {
                var materials = await PlanOnlyMaterialLoader.LoadAsync(
                    root, new[] { "dbo.USP_Child" }, CancellationToken.None);

                Assert.Equal("# Spec dbo.USP_Child", Assert.Single(materials.Specs).Content);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task 찾지_못한_진입점을_그대로_올린다()
        {
            var root = CreateTree();
            try
            {
                var materials = await PlanOnlyMaterialLoader.LoadAsync(
                    root, new[] { "dbo.USP_Child", "dbo.USP_GHOST" }, CancellationToken.None);

                Assert.Equal("dbo.USP_GHOST", Assert.Single(materials.NotFound));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task 메타데이터가_없는_스텝은_경고로_알린다()
        {
            // 명세는 있는데 raw/metadata.json 이 없으면 그 SP 는 specs 에는 들어가고
            // definitions 에는 안 들어간다. 아무도 안 알리면 재료를 잃은 검사가 조용해진다.
            var root = CreateTree();
            Directory.Delete(Path.Combine(root, "Procedures", "dbo.USP_Bare", "raw"), true);
            try
            {
                var materials = await PlanOnlyMaterialLoader.LoadAsync(
                    root, new[] { "dbo.USP_Bare" }, CancellationToken.None);

                Assert.Contains("dbo.USP_Bare", Assert.Single(materials.Warnings));
                Assert.Empty(materials.Definitions);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string CreateTree()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-PlanOnlyMat-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_Parent", "USP_Parent");
            WriteProcedure(root, "dbo.USP_Child", "USP_Child");
            WriteProcedure(root, "dbo.USP_Bare", "USP_Bare");

            var rawDirectory = Path.Combine(root, "Procedures", "dbo.USP_Parent", "raw");
            File.WriteAllText(
                Path.Combine(rawDirectory, "dependency-manifest.json"),
                """
                {
                  "Key": "DB.dbo.USP_Parent.Procedure",
                  "Nodes": [
                    { "Key": "DB.dbo.USP_Parent.Procedure", "Status": "Succeeded", "SpecPath": "docs/Spec.md" },
                    { "Key": "DB.dbo.USP_Child.Procedure", "Status": "Succeeded", "SpecPath": "../dbo.USP_Child/docs/Spec.md" }
                  ]
                }
                """);

            return root;
        }

        private static void WriteProcedure(string root, string objectDirectory, string procedureName)
        {
            var docsDirectory = Path.Combine(root, "Procedures", objectDirectory, "docs");
            Directory.CreateDirectory(docsDirectory);
            File.WriteAllText(Path.Combine(docsDirectory, "Spec.md"), $"# Spec {objectDirectory}");

            var rawDirectory = Path.Combine(root, "Procedures", objectDirectory, "raw");
            Directory.CreateDirectory(rawDirectory);
            var definition = new SpDefinition { Schema = "dbo", Name = procedureName, DdlText = "SELECT 1;" };
            File.WriteAllText(
                Path.Combine(rawDirectory, "metadata.json"),
                System.Text.Json.JsonSerializer.Serialize(definition));
        }
    }
}
