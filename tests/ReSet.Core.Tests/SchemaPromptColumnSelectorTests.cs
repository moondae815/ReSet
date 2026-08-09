using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SchemaPromptColumnSelectorTests
    {
        private static DependencyInfo Dep(
            string name, string? database, params (string Name, bool Pk)[] columns)
        {
            var dep = new DependencyInfo { Name = name, Schema = "dbo", Database = database, Type = "USER_TABLE" };
            foreach (var (columnName, pk) in columns)
            {
                dep.Columns.Add(new ColumnInfo { ColumnName = columnName, DataType = "int", IsPrimaryKey = pk });
            }
            return dep;
        }

        private static SpDefinition Sp(string? database, Dictionary<string, List<string>> referenced)
        {
            return new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = database == null ? null : new CodeObjectKey(database, "dbo", "UP_PROBE", CodeObjectType.Procedure),
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ReferencedColumnsPerTable = new Dictionary<string, List<string>>(
                        referenced, System.StringComparer.OrdinalIgnoreCase)
                }
            };
        }

        [Fact]
        public void Select_WithReferencedColumns_ShouldKeepOnlyThoseAndKeys()
        {
            // Arrange - AMT는 참조되고 ID는 PK다. ETC는 둘 다 아니라 빠져야 한다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("ID", true), ("AMT", false), ("ETC", false));
            var sp = Sp("SETTLE_POQ_DB", new Dictionary<string, List<string>>
            {
                ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "AMT" }
            });

            // Act
            var shown = SchemaPromptColumnSelector.Select(dep, sp);

            // Assert
            Assert.Contains("AMT", shown);
            Assert.Contains("ID", shown);
            Assert.DoesNotContain("ETC", shown);
        }

        [Fact]
        public void Select_WhenNothingMatches_ShouldFallBackToAllColumns()
        {
            // Arrange - 참조 정보도 PK/FK도 인덱스도 없으면 필터를 걸지 않는다.
            // 이것이 현행 폴백이고, 과다 포함은 무해하지만 과소 포함은 거짓 "컬럼 없음"을 만든다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("ID", false), ("AMT", false));
            var sp = Sp("SETTLE_POQ_DB", new Dictionary<string, List<string>>());

            // Act
            var shown = SchemaPromptColumnSelector.Select(dep, sp);

            // Assert
            Assert.Equal(2, shown.Count);
        }

        [Fact]
        public void Select_WithoutDbContext_ShouldMatchByBaseName()
        {
            // Arrange - ObjectKey.Database가 없으면 3-part 정식 비교가 성립하지 않아
            // 베이스 이름 비교로 내려간다. 이 폴백이 없으면 컬럼이 통째로 유실된다.
            var dep = Dep("TSettleMst", null, ("AMT", false), ("ETC", false));
            var sp = Sp(null, new Dictionary<string, List<string>>
            {
                ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "AMT" }
            });

            // Act
            var shown = SchemaPromptColumnSelector.Select(dep, sp);

            // Assert
            Assert.Contains("AMT", shown);
            Assert.DoesNotContain("ETC", shown);
        }

        [Fact]
        public void Select_WithDbContext_ShouldNotMergeDifferentDatabases()
        {
            // Arrange - DB 컨텍스트가 있으면 정식 3-part 비교를 유지해야 한다.
            // dbo.TPGProperty와 PaymentDB.dbo.TPGProperty를 베이스 이름으로 합치면
            // 서로 다른 물리 테이블의 컬럼이 섞인다.
            var dep = Dep("TPGProperty", "SETTLE_POQ_DB", ("OPT", false), ("ETC", false));
            var sp = Sp("SETTLE_POQ_DB", new Dictionary<string, List<string>>
            {
                ["PaymentDB.dbo.TPGProperty"] = new List<string> { "OPT" }
            });

            // Act
            var shown = SchemaPromptColumnSelector.Select(dep, sp);

            // Assert - 매칭이 없으므로 폴백이 걸려 전체가 나온다. 섞이지는 않는다.
            Assert.Equal(2, shown.Count);
        }
    }
}
