using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StaticAnalysisNormalizerTests
    {
        private static SpStaticAnalysisResult Analysis() =>
            new SpStaticAnalysisResult { IsParsedSuccessfully = true };

        [Fact]
        public void Normalize_MergesTheThreeSpellingsOfOneTable()
        {
            // CANCEL_INS의 실제 형태다. 파서는 SELECT 측을 SETTLE_POQ_DB.dbo.TSettleMst로,
            // INSERT 대상 컬럼 목록을 한정 없는 TSettleMst로 키잉한다. 같은 물리 테이블이다.
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string>
            {
                "TSettleMst", "dbo.TSettleMst", "SETTLE_POQ_DB.dbo.TSettleMst"
            };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TSettleMst" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_KeepsSameNamedTablesInDifferentDatabasesApart()
        {
            // 4PLCARD는 dbo.TPGProperty와 PaymentDB.dbo.TPGProperty를 둘 다 참조한다.
            // 컬럼 구성이 동일해서 베이스 이름으로 병합하면 조용히 틀린다.
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "TPGProperty", "PaymentDB.dbo.TPGProperty" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TPGProperty", "PaymentDB.dbo.TPGProperty" },
                result.ReferencedTables);
        }

        [Fact]
        public void Normalize_UnionsColumnsOfMergedKeysInFirstSeenOrder()
        {
            // 프롬프트가 이 순서를 INSERT 매핑표의 행 순서로 쓴다.
            var analysis = Analysis();
            analysis.ReferencedColumnsPerTable = new Dictionary<string, List<string>>
            {
                { "SETTLE_POQ_DB.dbo.TSettleMst", new List<string> { "CLIENTID", "PGNAME" } },
                { "TSettleMst", new List<string> { "CLIENTID", "CYMD", "INSTATE" } }
            };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            var entry = Assert.Single(result.ReferencedColumnsPerTable);
            Assert.Equal("SETTLE_POQ_DB.dbo.TSettleMst", entry.Key);
            Assert.Equal(new[] { "CLIENTID", "PGNAME", "CYMD", "INSTATE" }, entry.Value);
        }

        [Fact]
        public void Normalize_LeavesTempTablesAndTableVariablesAlone()
        {
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "#TempBonus", "@RowSet" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "#TempBonus", "@RowSet" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_LeavesFourPartLinkedServerNamesAlone()
        {
            // 로컬 DB 이름을 씌우면 원격 참조가 로컬 테이블로 둔갑한다.
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "LINKED.RemoteDb.dbo.TRemote" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "LINKED.RemoteDb.dbo.TRemote" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_WithoutDatabaseContext_DoesNotInventQualifiers()
        {
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "TSettleMst", "dbo.TSettleMst" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, null, "dbo");

            Assert.Equal(new[] { "TSettleMst", "dbo.TSettleMst" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_StripsBrackets()
        {
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "[PaymentDB].[dbo].[TTxMst]" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "PaymentDB.dbo.TTxMst" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_NormalizesEveryTableBearingList()
        {
            var analysis = Analysis();
            analysis.SelectTables = new List<string> { "TSettleMst" };
            analysis.InsertTables = new List<string> { "dbo.TSettleMst" };
            analysis.UpdateTables = new List<string> { "TSettleMst" };
            analysis.DeleteTables = new List<string> { "dbo.TSettleMst" };
            analysis.AstInsertMappings = new List<AstInsertMapping>
            {
                new AstInsertMapping
                {
                    TargetTable = "TSettleMst",
                    TargetColumns = new List<string> { "YMD" },
                    SourceQueryBlock = "SELECT 1"
                }
            };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            const string canonical = "SETTLE_POQ_DB.dbo.TSettleMst";
            Assert.Equal(new[] { canonical }, result.SelectTables);
            Assert.Equal(new[] { canonical }, result.InsertTables);
            Assert.Equal(new[] { canonical }, result.UpdateTables);
            Assert.Equal(new[] { canonical }, result.DeleteTables);
            Assert.Equal(canonical, Assert.Single(result.AstInsertMappings).TargetTable);
            Assert.Equal(new[] { "YMD" }, Assert.Single(result.AstInsertMappings).TargetColumns);
            Assert.Equal("SELECT 1", Assert.Single(result.AstInsertMappings).SourceQueryBlock);
        }

        [Fact]
        public void Normalize_CarriesUntouchedFieldsThrough()
        {
            // 새 인스턴스를 만들므로 옮기는 걸 빠뜨리면 조용히 데이터가 사라진다.
            var analysis = Analysis();
            analysis.ParserWarningMessage = "경고";
            analysis.ControlFlowSummary = new List<string> { "Line 1: IF" };
            analysis.ProcedureParameters = new List<string> { "@pi_strYMD" };
            analysis.DeclaredVariables = new List<string> { "@v_intID" };
            analysis.CreatedTempTables = new List<string> { "#Temp" };
            analysis.LinkedServerReferences = new List<string> { "LINKED.RemoteDb.dbo.TRemote" };
            analysis.ReferencedFunctions = new List<string> { "dbo.UF_GET_ROUND4VAT" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal("경고", result.ParserWarningMessage);
            Assert.Equal(new[] { "Line 1: IF" }, result.ControlFlowSummary);
            Assert.Equal(new[] { "@pi_strYMD" }, result.ProcedureParameters);
            Assert.Equal(new[] { "@v_intID" }, result.DeclaredVariables);
            Assert.Equal(new[] { "#Temp" }, result.CreatedTempTables);
            Assert.Equal(new[] { "LINKED.RemoteDb.dbo.TRemote" }, result.LinkedServerReferences);
            Assert.Equal(new[] { "dbo.UF_GET_ROUND4VAT" }, result.ReferencedFunctions);
        }

        [Fact]
        public void Normalize_DoesNotMutateItsInput()
        {
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "TSettleMst" };

            StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "TSettleMst" }, analysis.ReferencedTables);
        }

        [Fact]
        public void CanonicalizeParts_FillsMissingDatabaseFromFallback()
        {
            // DependencyInfo.Database는 같은 DB일 때 null이다.
            var result = StaticAnalysisNormalizer.CanonicalizeParts(
                null, "dbo", "TSettleMst", "SETTLE_POQ_DB", "dbo");

            Assert.Equal("SETTLE_POQ_DB.dbo.TSettleMst", result);
        }

        [Fact]
        public void CanonicalizeParts_KeepsExplicitDatabase()
        {
            var result = StaticAnalysisNormalizer.CanonicalizeParts(
                "PaymentDB", "dbo", "TTxMst", "SETTLE_POQ_DB", "dbo");

            Assert.Equal("PaymentDB.dbo.TTxMst", result);
        }
    }
}
