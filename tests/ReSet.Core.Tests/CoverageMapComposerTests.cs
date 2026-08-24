using System.Linq;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CoverageMapComposerTests
    {
        private const string Ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DELETE FROM dbo.T WHERE PGNAME IN ('a', 'b')
    PRINT 'done'
END";
        // 라인 3 = DELETE, 라인 4 = PRINT

        private static SpDefinition Def() => new()
        {
            Schema = "dbo",
            Name = "P",
            DdlText = Ddl
        };

        [Fact]
        public void Compose_ExtractorMaterialAndAnchorBothPresent_ShouldBeConsistent()
        {
            const string spec = @"### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 | 연산 |
| :--- | :--- | :--- | :--- |
| DELETE 1 | 3 | PGNAME | IN |
";

            var delete = Compose(spec, "DeleteStatement");

            Assert.Equal(CoverageState.Consistent, delete.State);
        }

        [Fact]
        public void Compose_ExtractorMaterialButNoAnchor_ShouldBeSpecMissing()
        {
            // 명세서에 그 문장을 지목한 행이 없다 - 재생성으로 닫히는 결함.
            const string spec = "## 개요\n\n설명만 있고 표가 없다.\n";

            var delete = Compose(spec, "DeleteStatement");

            Assert.Equal(CoverageState.SpecMissing, delete.State);
        }

        [Fact]
        public void Compose_NoExtractorMaterialAndNoAnchor_ShouldBeOutOfScope()
        {
            // PRINT는 어떤 기계 확정 표의 관할도 아니다 - 도구를 고쳐야 닫힌다.
            const string spec = "## 개요\n";

            var print = Compose(spec, "PrintStatement");

            Assert.Equal(CoverageState.OutOfScope, print.State);
        }

        [Fact]
        public void Compose_AnchorWithoutExtractorMaterial_ShouldBeProseOnly()
        {
            // 문서가 PRINT 줄을 지목했지만 추출기가 낸 재료는 없다.
            const string spec = @"### 실행 의미 (기계 확정 — 수정 금지)

| 종류 | 라인 | 대상 | 확정 사실 |
| :--- | :--- | :--- | :--- |
| 기타 | 4 | PRINT | 로그를 남긴다 |
";

            var print = Compose(spec, "PrintStatement");

            Assert.Equal(CoverageState.ProseOnly, print.State);
        }

        [Fact]
        public void Compose_CommentAnchorAlone_ShouldNotMakeStatementConsistent()
        {
            // 주석이 붙어 있다고 그 문장이 문서화된 것이 아니다.
            const string spec = @"### 원본 주석 기록

| 라인 | 원본 주석 |
| :--- | :--- |
| 3 | -- 대상 삭제 |
";

            var delete = Compose(spec, "DeleteStatement");

            Assert.Equal(CoverageState.SpecMissing, delete.State);
            Assert.NotEmpty(delete.CommentAnchors);
            Assert.Empty(delete.Anchors);
        }

        [Fact]
        public void Compose_ContainerStatements_ShouldNotBeCounted()
        {
            var coverage = CoverageMapComposer.Compose("dbo.P", Def(), "## 개요\n");

            Assert.All(coverage.Statements, s => Assert.False(s.Statement.IsContainer));
            Assert.Equal(coverage.LeafCount, coverage.Statements.Count);
        }

        [Fact]
        public void Compose_Merge_ShouldBeMarkedAsKnownUncovered()
        {
            const string mergeDdl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    MERGE dbo.T AS D USING dbo.S AS S ON D.A = S.A
    WHEN MATCHED THEN UPDATE SET D.B = S.B;
END";
            var def = new SpDefinition { Schema = "dbo", Name = "P", DdlText = mergeDdl };

            var coverage = CoverageMapComposer.Compose("dbo.P", def, "## 개요\n");
            var merge = coverage.Statements.Single(s => s.Statement.StatementType == "MergeStatement");

            Assert.True(merge.IsKnownUncovered);
            Assert.Equal(CoverageState.OutOfScope, merge.State);
        }

        [Fact]
        public void Compose_ShouldReportTableKindsRead()
        {
            const string spec = @"### 집합 술어 (기계 확정 — 수정 금지)

| 문장 | 라인 | 컬럼 |
| :--- | :--- | :--- |
| DELETE 1 | 3 | PGNAME |
";

            Assert.Equal(1, CoverageMapComposer.Compose("dbo.P", Def(), spec).TableKindsRead);
        }

        private static StatementCoverage Compose(string spec, string statementType) =>
            CoverageMapComposer.Compose("dbo.P", Def(), spec)
                .Statements.Single(s => s.Statement.StatementType == statementType);
    }
}
