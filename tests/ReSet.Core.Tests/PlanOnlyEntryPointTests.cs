using System;
using System.IO;
using System.Linq;
using ReSet.Cli;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// `--plan-only` 가 `--sp` 로 받은 진입점 이름을 저장된 명세서 경로로 옮기는 규칙.
    ///
    /// 경로를 손조립하지 않고 <see cref="BatchStepCatalog.FindStepCandidates"/> 결과를
    /// 대조하는 이유: 이름에 `.` 이 든 객체의 디렉터리 인코딩 규칙이 두 벌이 되면
    /// 캐시 조회 경로와 갈라져 산출물이 두 자리로 흩어진다(AGENTS.md 범주 2).
    /// </summary>
    public class PlanOnlyEntryPointTests
    {
        [Fact]
        public void 나열한_순서를_그대로_보존한다()
        {
            // 이 순서가 곧 배치 스텝의 실행 순서다. 사전순으로 정렬하면 정산 순서가 바뀐다.
            var root = CreateTree();
            try
            {
                var resolution = BatchStepCatalog.ResolveEntryPointSpecPaths(
                    root, new[] { "dbo.USP_C", "dbo.USP_A", "dbo.USP_B" });

                Assert.Equal(
                    new[]
                    {
                        "Procedures/dbo.USP_C/docs/Spec.md",
                        "Procedures/dbo.USP_A/docs/Spec.md",
                        "Procedures/dbo.USP_B/docs/Spec.md"
                    },
                    resolution.SpecPaths.Select(Normalize));
                Assert.Empty(resolution.NotFound);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void 명세서가_없는_이름은_조용히_빠지지_않고_보고된다()
        {
            // 조용히 빼면 사용자는 자기가 지정한 스텝이 다 들어간 줄 안다
            // (`--policy-sps` 폐기 때 남긴 규약과 같다).
            var root = CreateTree();
            try
            {
                var resolution = BatchStepCatalog.ResolveEntryPointSpecPaths(
                    root, new[] { "dbo.USP_A", "dbo.USP_MISSING" });

                Assert.Equal(new[] { "dbo.USP_MISSING" }, resolution.NotFound);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void 배치_스텝이_될_수_없는_UDF는_찾지_못한_것으로_본다()
        {
            // 배치 스텝은 프로시저다. 함수 명세서를 스텝 자리에 넣으면 지시서가
            // 스텝으로 만들 수 없는 대상을 스텝이라고 말하게 된다.
            var root = CreateTree();
            try
            {
                var resolution = BatchStepCatalog.ResolveEntryPointSpecPaths(
                    root, new[] { "dbo.UF_Helper" });

                Assert.Empty(resolution.SpecPaths);
                Assert.Equal(new[] { "dbo.UF_Helper" }, resolution.NotFound);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void 대소문자가_달라도_찾고_경로는_저장된_표기를_쓴다()
        {
            // SQL 객체 이름은 대소문자를 가리지 않지만 산출물 경로는 저장된 표기가
            // 정본이다. 사용자가 친 표기로 경로를 만들면 파일을 못 연다.
            var root = CreateTree();
            try
            {
                var resolution = BatchStepCatalog.ResolveEntryPointSpecPaths(
                    root, new[] { "DBO.usp_a" });

                Assert.Equal(
                    new[] { "Procedures/dbo.USP_A/docs/Spec.md" },
                    resolution.SpecPaths.Select(Normalize));
                Assert.Empty(resolution.NotFound);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void 같은_이름이_외부_DB에도_있으면_현재_DB의_명세서를_쓴다()
        {
            // 분석 루트 DB의 것이 정본이다. 외부 DB 사본을 집으면 스텝이 다른 DB의
            // 로직을 가리키게 된다.
            var root = CreateTree();
            try
            {
                var resolution = BatchStepCatalog.ResolveEntryPointSpecPaths(
                    root, new[] { "dbo.USP_Shared" });

                Assert.Equal(
                    new[] { "Procedures/dbo.USP_Shared/docs/Spec.md" },
                    resolution.SpecPaths.Select(Normalize));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string Normalize(string path) =>
            path.Replace(Path.DirectorySeparatorChar, '/');

        private static string CreateTree()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-PlanOnly-{Guid.NewGuid():N}");
            foreach (var relative in new[]
                     {
                         Path.Combine("Procedures", "dbo.USP_A"),
                         Path.Combine("Procedures", "dbo.USP_B"),
                         Path.Combine("Procedures", "dbo.USP_C"),
                         Path.Combine("Procedures", "dbo.USP_Shared"),
                         Path.Combine("Functions", "dbo.UF_Helper"),
                         Path.Combine("External", "AuditDB", "Procedures", "dbo.USP_Shared")
                     })
            {
                var docs = Path.Combine(root, relative, "docs");
                Directory.CreateDirectory(docs);
                File.WriteAllText(Path.Combine(docs, "Spec.md"), "# Spec");
            }

            return root;
        }
    }
}
