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

        [Theory]
        // 감싼 대괄호는 여전히 벗겨져야 한다 - 이것이 원래 기능이다.
        [InlineData("[PaymentDB].[dbo].[TTxMst]", "PaymentDB.dbo.TTxMst")]
        [InlineData("[dbo].[TTxMst]", "SETTLE_POQ_DB.dbo.TTxMst")]
        // 대괄호 안의 점은 구분자가 아니다.
        [InlineData("[my.table]", "SETTLE_POQ_DB.dbo.my.table")]
        // 이름의 일부인 ']'는 보존되어야 한다. 예전 구현은 이것을 버려
        // my]table을 mytable로 손상시켰다.
        [InlineData("my]table", "SETTLE_POQ_DB.dbo.my]table")]
        [InlineData("dbo.my]table", "SETTLE_POQ_DB.dbo.my]table")]
        public void Canonicalize_PreservesBracketCharactersThatAreNotWrappers(
            string writtenName,
            string expected)
        {
            Assert.Equal(expected, StaticAnalysisNormalizer.Canonicalize(writtenName, "SETTLE_POQ_DB", "dbo"));
        }

        [Fact]
        public void Normalize_ShouldCanonicalizeUpdateMappingTableOnly()
        {
            // Arrange
            var analysis = new SpStaticAnalysisResult();
            var mapping = new AstUpdateMapping
            {
                TargetTable = "TCommMst",
                StatementOrdinal = 2,
                FromClauseText = "FROM TCommMst A"
            };
            mapping.Assignments.Add(new AstUpdateAssignment { Column = "CLVT", SourceExpression = "CLVT * -1" });
            mapping.SelfReferencedColumns.Add("CLVT");
            analysis.AstUpdateMappings.Add(mapping);

            // Act
            var normalized = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            // Assert
            var result = Assert.Single(normalized.AstUpdateMappings);
            Assert.Equal("SETTLE_POQ_DB.dbo.TCommMst", result.TargetTable);
            Assert.Equal(2, result.StatementOrdinal);
            Assert.Equal("FROM TCommMst A", result.FromClauseText);
            Assert.Equal("CLVT * -1", Assert.Single(result.Assignments).SourceExpression);
            Assert.Equal("CLVT", Assert.Single(result.SelfReferencedColumns));
        }

        [Fact]
        public void Normalize_ShouldNotShareUpdateMappingListInstancesWithInput()
        {
            // Arrange
            var analysis = new SpStaticAnalysisResult();
            var mapping = new AstUpdateMapping { TargetTable = "dbo.T" };
            mapping.Assignments.Add(new AstUpdateAssignment { Column = "A", SourceExpression = "1" });
            mapping.SelfReferencedColumns.Add("A");
            analysis.AstUpdateMappings.Add(mapping);

            // Act
            var normalized = StaticAnalysisNormalizer.Normalize(analysis, "DB", "dbo");
            normalized.AstUpdateMappings[0].Assignments.Clear();
            normalized.AstUpdateMappings[0].SelfReferencedColumns.Clear();

            // Assert - 입력을 변경하지 않는다는 Normalize의 계약: Assignments와
            // SelfReferencedColumns 둘 다 새 리스트 인스턴스여야 한다. 리스트 자체를
            // 그대로 대입(aliasing)하면 결과 쪽을 비웠을 때 입력도 같이 비게 된다.
            Assert.Single(analysis.AstUpdateMappings[0].Assignments);
            Assert.Single(analysis.AstUpdateMappings[0].SelfReferencedColumns);
        }

        [Fact]
        public void Normalize_ShouldNotShareUpdateAssignmentElementInstancesWithInput()
        {
            // Arrange
            var analysis = new SpStaticAnalysisResult();
            var mapping = new AstUpdateMapping { TargetTable = "dbo.T" };
            mapping.Assignments.Add(new AstUpdateAssignment { Column = "A", SourceExpression = "1" });
            analysis.AstUpdateMappings.Add(mapping);

            // Act
            var normalized = StaticAnalysisNormalizer.Normalize(analysis, "DB", "dbo");
            normalized.AstUpdateMappings[0].Assignments[0].SourceExpression = "MUTATED";

            // Assert - AstUpdateAssignment는 가변 참조 타입이다. 리스트 컨테이너만 새로
            // 만들고 원소를 그대로 옮기면(Add(assignment)) 컨테이너는 독립적이어도 원소를
            // 공유하게 되어, 결과 쪽 원소를 바꾸면 입력 쪽도 같이 바뀐다.
            Assert.Equal("1", analysis.AstUpdateMappings[0].Assignments[0].SourceExpression);
        }

        [Fact]
        public void Normalize_ShouldPreserveTheUpdateMappingSourceLine()
        {
            // 정규화는 테이블 이름만 다룬다. 라인을 잃으면 앵커가 프롬프트에 닿지 않는다.
            var analysis = new SpStaticAnalysisResult();
            var mapping = new AstUpdateMapping
            {
                TargetTable = "dbo.T",
                StatementOrdinal = 1,
                SourceLine = 42
            };
            mapping.Assignments.Add(new AstUpdateAssignment { Column = "C", SourceExpression = "1" });
            analysis.AstUpdateMappings.Add(mapping);

            var normalized = StaticAnalysisNormalizer.Normalize(analysis, "DB", "dbo");

            Assert.Equal(42, Assert.Single(normalized.AstUpdateMappings).SourceLine);
        }
    }
}
