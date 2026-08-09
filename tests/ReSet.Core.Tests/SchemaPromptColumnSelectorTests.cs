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

        [Fact]
        public void Select_WhenDepDatabaseIsNullButObjectKeyHasDatabase_ShouldUseObjectKeyDatabaseForContext()
        {
            // Arrange - hasDbContext의 계산 출처가 실제로 갈라지는 유일한 조합: dep.Database는
            // null(같은 DB 소속이라 비어 있는 정상적인 경우)이고 spDef.ObjectKey.Database는
            // 있다. 이 둘이 항상 동시에 null이거나 동시에 값이 있으면 어느 쪽을 기준으로
            // 삼든 hasDbContext 값이 같아서 이 가드는 아무 하중도 지지 않는다.
            //
            // spDef.ObjectKey.Database("SETTLE_POQ_DB")를 기준으로 삼으면(올바른 구현)
            // hasDbContext=true가 되어 정식 3-part 비교가 성립한다. 키 "OtherDB.dbo.TSettleMst"는
            // 이미 3-part로 완전히 한정돼 있어 그대로 유지되고, dep의 canonical 이름은
            // fallback DB인 SETTLE_POQ_DB로 한정되어 "SETTLE_POQ_DB.dbo.TSettleMst"가 된다.
            // 두 이름이 다르므로(OtherDB != SETTLE_POQ_DB) 매칭 실패 -> 이 키의 컬럼은
            // 병합되지 않는다.
            //
            // 만약 dep.Database(null)를 기준으로 삼으면(회귀) hasDbContext=false가 되어
            // 베이스 이름("TSettleMst" vs "TSettleMst") 비교로 내려가 버젓이 매칭에
            // 성공한다 - OtherDB에 있는 실제로 다른 물리 테이블의 컬럼을 SETTLE_POQ_DB의
            // TSettleMst로 지어내는 것과 같다. 이 오매칭이 keepCols를 채워 필터를
            // 발동시키므로, 정당하게 남아 있어야 할 ETC가 프롬프트에서 사라진다.
            var dep = Dep("TSettleMst", null, ("AMT", false), ("ETC", false));
            var sp = Sp("SETTLE_POQ_DB", new Dictionary<string, List<string>>
            {
                ["OtherDB.dbo.TSettleMst"] = new List<string> { "AMT" }
            });

            // Act
            var shown = SchemaPromptColumnSelector.Select(dep, sp);

            // Assert - 정식 비교가 어긋나 매칭이 실패해야 하므로 keepCols가 비고
            // 폴백이 걸려 두 컬럼이 모두 나와야 한다. ETC가 사라지면 hasDbContext가
            // dep.Database 기준으로 잘못 계산되고 있다는 뜻이다.
            Assert.Equal(2, shown.Count);
            Assert.Contains("ETC", shown);
        }

        [Fact]
        public void DetectOrphanedColumnKeys_WhenCanonicalMismatchDropsColumns_ShouldReport()
        {
            // Arrange - 14개 명세서를 망가뜨린 결함의 재현. 의존성은 DB 한정
            // SETTLE_POQ_DB.dbo.TSettleMst인데 AST 키는 비한정 "TSettleMst"이고,
            // 분석 대상 SP는 다른 DB에 있다. 정식 비교가 어긋나 CYMD/INSTATE가
            // 프롬프트 어디에도 실리지 않는다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("CYMD", false), ("INSTATE", false));
            var sp = Sp("PaymentDB", new Dictionary<string, List<string>>
            {
                ["TSettleMst"] = new List<string> { "CYMD", "INSTATE" }
            });
            sp.Dependencies.Add(dep);

            // Act
            var defects = SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp);

            // Assert
            var defect = Assert.Single(defects);
            Assert.Contains("TSettleMst", defect);
            Assert.Contains("CYMD", defect);
        }

        [Fact]
        public void DetectOrphanedColumnKeys_WhenMatchingSucceeds_ShouldReportNothing()
        {
            // Arrange - 정상 경로. 정식 비교가 성립한다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("CYMD", false));
            var sp = Sp("SETTLE_POQ_DB", new Dictionary<string, List<string>>
            {
                ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "CYMD" }
            });
            sp.Dependencies.Add(dep);

            // Act & Assert
            Assert.Empty(SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp));
        }

        [Fact]
        public void DetectOrphanedColumnKeys_WithoutDbContext_ShouldReportNothing()
        {
            // Arrange - DB 컨텍스트가 없으면 실제 매칭이 이미 베이스 이름으로 내려가
            // 병합에 성공한다. 조건을 "정식 비교 실패"로 못 박으면 이 정상 동작이
            // 전부 위반으로 보고된다 - 그래서 조건은 "실제 매칭에서 병합되지 않음"이다.
            var dep = Dep("TSettleMst", null, ("CYMD", false));
            var sp = Sp(null, new Dictionary<string, List<string>>
            {
                ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "CYMD" }
            });
            sp.Dependencies.Add(dep);

            // Act & Assert
            Assert.Empty(SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp));
        }

        [Fact]
        public void DetectOrphanedColumnKeys_ForTempTable_ShouldReportNothing()
        {
            // Arrange - 임시 테이블은 애초에 의존성이 아니다. 정당하게 매칭되지 않는다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("CYMD", false));
            var sp = Sp("PaymentDB", new Dictionary<string, List<string>>
            {
                ["#TMP"] = new List<string> { "SEQ" }
            });
            sp.Dependencies.Add(dep);

            // Act & Assert
            Assert.Empty(SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp));
        }

        [Fact]
        public void DetectOrphanedColumnKeys_WhenDependencyHasNoColumns_ShouldReportNothing()
        {
            // Arrange - TPGProperty처럼 메타데이터 수집이 안 된 의존성은 스키마 표
            // 자체가 없다. 명세서가 "스키마 정의는 제공되지 않았습니다"라고 쓰는 것은
            // 참인 진술이고, 이것은 입력 결함이 아니다.
            var dep = Dep("TPGProperty", "SETTLE_POQ_DB");
            var sp = Sp("PaymentDB", new Dictionary<string, List<string>>
            {
                ["TPGProperty"] = new List<string> { "CommMethod" }
            });
            sp.Dependencies.Add(dep);

            // Act & Assert
            Assert.Empty(SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp));
        }
    }
}
