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
        public void Compose_SourceCommentOnlyStatement_ShouldNotBecomeSpecMissing()
        {
            // Fix Round 1 실측(EXPECT_PROC:16, PROC_ETC:23) 재현. 원본 주석 표에
            // 이미 옮겨진 코드 범례 주석뿐 - 추출기 재료도, 주석이 아닌 앵커도 없다.
            // SourceComments를 재료 쪽에 넣으면 이 문장이 SpecMissing(🟥)으로
            // 뒤집힌다 - Spec.md가 성실히 옮겨 적었는데도 "명세서 결함"으로
            // 오보하게 된다. 이 테스트를 되돌려(SourceComments를 넣어) 실행하면
            // 깨져야 한다 - CoverageMapComposer.ExtractorFactLines의 배제 결정을
            // 잠그는 회귀 가드다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @x INT = 1  --0:반올림, 1:자동
    PRINT 'done'
END";
            var def = new SpDefinition { Schema = "dbo", Name = "P", DdlText = ddl };
            const string spec = @"### 원본 주석 기록

| 라인 | 원본 주석 |
| :--- | :--- |
| 3 | 0:반올림, 1:자동 |
";

            var coverage = CoverageMapComposer.Compose("dbo.P", def, spec);
            var declareLine = coverage.Statements.Single(s => s.Statement.StartLine == 3);

            Assert.Equal(CoverageState.OutOfScope, declareLine.State);
            Assert.Empty(declareLine.Anchors);
            Assert.Empty(declareLine.ExtractorLines);
            Assert.NotEmpty(declareLine.CommentAnchors);
        }

        [Fact]
        public void Compose_DdlTextPresentButUnparsable_ShouldMarkParseFailed()
        {
            // I4: DdlStatementEnumerator는 파스 오류에 빈 목록을 낸다(옳은 정책)이지만
            // 그 결과가 "정상 - 그냥 잎이 없는 객체"로 보이면 안 된다. DdlText가 비지
            // 않았는데 잎이 0개면 파스 실패의 확정 신호다.
            var def = new SpDefinition { Schema = "dbo", Name = "P", DdlText = "CREATE PROC (((( 이건 SQL이 아니다" };

            var coverage = CoverageMapComposer.Compose("dbo.P", def, "## 개요\n");

            Assert.True(coverage.ParseFailed);
            Assert.Empty(coverage.Statements);
        }

        [Fact]
        public void Compose_EmptyDdlText_ShouldNotBeParseFailed()
        {
            // 빈 DdlText는 파스 시도조차 없었던 것이다 - 파스 실패와 다른 사실이다.
            var def = new SpDefinition { Schema = "dbo", Name = "P", DdlText = "" };

            var coverage = CoverageMapComposer.Compose("dbo.P", def, "## 개요\n");

            Assert.False(coverage.ParseFailed);
        }

        [Fact]
        public void Compose_ValidDdlWithLeaves_ShouldNotBeParseFailed()
        {
            var coverage = CoverageMapComposer.Compose("dbo.P", Def(), "## 개요\n");

            Assert.False(coverage.ParseFailed);
            Assert.NotEmpty(coverage.Statements);
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

        [Fact]
        public void Compose_TransactionStatement_ShouldCountAsExtractorMaterial()
        {
            // 배선 7번을 잠근다. ExtractorFactLines에 TransactionBoundaries가 없으면
            // 표는 생기는데 🟧이 그대로다 - 아무것도 안 깨지면서 목적만 달성 안 된다.
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END";
            var def = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                DdlText = ddl
            };

            var coverage = CoverageMapComposer.Compose("dbo.P", def, "## 개요\n");
            var begin = coverage.Statements.Single(
                s => s.Statement.StatementType == "BeginTransactionStatement");

            // 재료는 있고 앵커는 없으므로 SpecMissing이다 - 설계서 §3의 전이 상태.
            Assert.NotEmpty(begin.ExtractorLines);
            Assert.Equal(CoverageState.SpecMissing, begin.State);
        }

        [Fact]
        public void Compose_SetAssignment_ShouldCountAsExtractorMaterial()
        {
            const string ddl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SET @v = 1
END";
            var def = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                DdlText = ddl
            };

            var coverage = CoverageMapComposer.Compose("dbo.P", def, "## 개요\n");
            var set = coverage.Statements.Single(
                s => s.Statement.StatementType == "SetVariableStatement");

            Assert.NotEmpty(set.ExtractorLines);
            Assert.Equal(CoverageState.SpecMissing, set.State);
        }
    }
}
