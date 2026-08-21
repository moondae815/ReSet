using System.Collections.Generic;
using System.Linq;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class ExecutionSemanticsFactsTests
    {
        private static readonly Dictionary<string, string> NoColumns = new();

        [Fact]
        public void Collect_NoThreePartAndNoLinkedServer_ShouldStateLocalPlacementAsFact()
        {
            // F1 무리 실측: 파서가 ThreePartObjectReferences를 빈 배열로 확정했는데
            // 명세서 9곳이 "크로스 데이터베이스 참조라고 단언할 수 없습니다"로 되짚었다.
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedTables = new List<string> { "SETTLE_POQ_DB.dbo.TPGProperty" }
            };

            var facts = ExecutionSemanticsFacts.Collect(
                "SELECT 1;",
                analysis,
                CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
                NoColumns);

            var fact = Assert.Single(facts.Where(f => f.Kind == ExecutionSemanticsFacts.DatabasePlacementKind));
            Assert.Contains("SETTLE_POQ_DB", fact.Fact);
            Assert.Contains("3부 식별자 참조 0건", fact.Fact);
            Assert.Contains("연결 서버 참조 0건", fact.Fact);
        }

        [Fact]
        public void Collect_WithThreePartReference_ShouldNameTheCrossDatabaseTargets()
        {
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ThreePartObjectReferences = new List<string> { "PaymentDB.dbo.TExtraSettleIn" }
            };

            var facts = ExecutionSemanticsFacts.Collect(
                "SELECT 1;",
                analysis,
                CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
                NoColumns);

            var fact = Assert.Single(facts.Where(f => f.Kind == ExecutionSemanticsFacts.DatabasePlacementKind));
            Assert.Contains("PaymentDB.dbo.TExtraSettleIn", fact.Fact);
        }

        [Fact]
        public void Collect_WithoutAnalysis_ShouldReturnEmpty()
        {
            var facts = ExecutionSemanticsFacts.Collect("SELECT 1;", null, null, NoColumns);

            Assert.Empty(facts);
        }
    }
}
