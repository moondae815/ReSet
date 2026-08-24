using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class StepSqlStatementReaderTests
{
    private static string Fence(string sql) => $"### S07 단계\n\n```sql\n{sql}\n```\n";

    [Fact]
    public void CountsStatementsByKindAndTable()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
            "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
            "INSERT INTO batch.BatchStepJournal (RunId) VALUES (1);"));

        Assert.Equal(2, statements.Count(s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst"));
        Assert.Single(statements, s => s.Kind == "INSERT" && s.TargetTable == "BatchStepJournal");
    }

    [Fact]
    public void ReadsAnchorFromLeadingComment()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "/* U13: 카드사 원가 반영 */\n" +
            "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y WHERE Y.YMD = @p;"));

        Assert.Equal(13, Assert.Single(statements).Anchor);
    }

    [Theory]
    [InlineData("-- 갱신 4")]
    [InlineData("-- UPDATE 4")]
    [InlineData("/* U4 */")]
    public void AcceptsThreeAnchorSpellings(string comment)
    {
        var statements = StepSqlStatementReader.Read(Fence(
            comment + "\nUPDATE A SET A.CLVT = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;"));

        Assert.Equal(4, Assert.Single(statements).Anchor);
    }

    [Fact]
    public void CollectsTopLevelPredicateAndJoinColumns_ButNotSubqueryColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = (SELECT TOP 1 X.Amt FROM dbo.TCost AS X WHERE X.Hidden = 1)\n" +
            "FROM dbo.TSettleMst AS Y INNER JOIN dbo.TCost AS C ON C.PLTID = Y.PLTID\n" +
            "WHERE Y.YMD = @p AND Y.UseState = 1;"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.PredicateColumns);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.Contains("PLTID", statement.JoinColumns);
        Assert.DoesNotContain("Hidden", statement.PredicateColumns);   // 스칼라 하위질의 안쪽
    }

    [Fact]
    public void FlagsGroupingWhenGroupByOrHavingPresent()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = 0 FROM dbo.TSettleMst AS Y\n" +
            "WHERE Y.PLTID IN (SELECT PLTID FROM dbo.TTx GROUP BY PLTID HAVING SUM(TxAmt) = 0);"));

        Assert.True(Assert.Single(statements).HasGrouping);
    }

    [Fact]
    public void UnparsableFence_IsSilentlySkipped()
    {
        // 단계 문서의 펜스에는 T-SQL이 아닌 것도 온다. 재료가 없다는 사실은
        // 다른 검사가 들고, 이 읽기는 조용히 건너뛴다.
        var statements = StepSqlStatementReader.Read(Fence("이것은 SQL이 아니다 <<<>>>"));

        Assert.Empty(statements);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Task 16 - C2. 파싱 실패가 실재하는 문장을 "없다"로 만드는 것을 막으려면
    // 호출부가 "펜스를 못 읽었다"는 사실 자체를 알아야 한다. 기존 단일 인자
    // Read(markdown)은 이 신호를 전혀 내지 않는다 - 검사 A가 파싱 실패와
    // "정말 문장이 없음"을 구별할 수 없다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnparsableFence_ReportsUnparsedFenceCount()
    {
        var statements = StepSqlStatementReader.Read(
            Fence("이것은 SQL이 아니다 <<<>>>"), out var unparsedFenceCount);

        Assert.Empty(statements);
        Assert.Equal(1, unparsedFenceCount);
    }

    [Fact]
    public void ParsableFenceWithNoStatements_DoesNotCountAsUnparsed()
    {
        // 파싱은 성공했지만 DML 문장이 없는 것(예: SET만 있는 펜스)은 "못
        // 읽음"이 아니다 - 이 둘을 같은 신호로 합치면 정상 펜스도 검사 A를
        // 접게 만든다.
        var statements = StepSqlStatementReader.Read(
            Fence("SET NOCOUNT ON;"), out var unparsedFenceCount);

        Assert.Empty(statements);
        Assert.Equal(0, unparsedFenceCount);
    }

    [Fact]
    public void MixOfParsableAndUnparsableFences_KeepsRealStatementsAndCountsOnlyFailures()
    {
        // 실물 모양(S12.md:75-80): `INSERT ... SELECT /* 주석만 */ FROM ...`처럼
        // SELECT 목록이 통째로 주석으로 비면 파싱이 실패한다 - 그 펜스만
        // 버려지고 나머지 펜스의 실재하는 문장은 살아야 한다.
        var markdown =
            "### S12 단계\n\n" +
            "```sql\n" +
            "DELETE FROM dbo.TSettleByTX WHERE YMD = @pi_strYMD;\n" +
            "```\n\n" +
            "```sql\n" +
            "INSERT INTO dbo.TPartialCancelByTX\n" +
            "SELECT\n" +
            "    /* 동일한 집계 열 */\n" +
            "    /* PLTID를 포함 */\n" +
            "FROM dbo.TSettleMst\n" +
            "WHERE YMD = @pi_strYMD\n" +
            "GROUP BY YMD;\n" +
            "```\n";

        var statements = StepSqlStatementReader.Read(markdown, out var unparsedFenceCount);

        Assert.Single(statements, s => s.Kind == "DELETE" && s.TargetTable == "TSettleByTX");
        Assert.Equal(1, unparsedFenceCount);
    }

    [Fact]
    public void TrailingCommentOnPreviousStatement_IsNotMisattributedToNextStatement()
    {
        // "-- U4 ..."는 A 문장 끝에 붙은 꼬리 주석이지 B 문장의 선행 주석이 아니다.
        // 리뷰 1라운드 실측: 고치기 전에는 이 주석이 B의 앵커로 잘못 붙었다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p; -- U4 참고: A에 대한 설명\n" +
            "UPDATE B SET B.CLVT = 2 FROM dbo.TSettleMst AS B WHERE B.YMD = @p;"));

        Assert.Equal(2, statements.Count);
        Assert.Null(statements[0].Anchor);
        Assert.Null(statements[1].Anchor);
    }

    [Fact]
    public void ResolvesTargetAlias_WhenFromClauseHasMultipleAliasedTables()
    {
        // FROM 절에 별칭이 둘 있을 때 갱신 대상 별칭(A)과 같은 것만 골라야 한다 -
        // "먼저 찾은 별칭"으로 어림하면 B(TCost)를 잘못 돌려준다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.CLCOMM = 1 FROM dbo.TCost AS B INNER JOIN dbo.TSettleMst AS A " +
            "ON A.PLTID = B.PLTID WHERE A.YMD = @p;"));

        Assert.Equal("TSettleMst", Assert.Single(statements).TargetTable);
    }

    [Fact]
    public void ResolvesTargetTable_WhenUpdateHasNoFromClauseOrAlias()
    {
        // FROM 절 자체가 없으면 별칭 사전이 비어 있고, 대상 이름은 그 자체가
        // 물리 테이블명이다 - 한 부(部) 이름을 별칭으로 오인해 빈 문자열로
        // 떨어뜨리면 안 된다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE TSettleMst SET CLCOMM = 1 WHERE YMD = @p;"));

        Assert.Equal("TSettleMst", Assert.Single(statements).TargetTable);
    }

    [Fact]
    public void DoesNotMisreadAnchorFromCompoundKoreanWord()
    {
        // "재갱신4"는 "갱신 4"의 오검출 대상이다 - "갱신" 앞에 다른 단어 성분이
        // 붙어 있으면 그 자체로 앵커 표기가 아니다.
        var statements = StepSqlStatementReader.Read(Fence(
            "-- 재갱신4\nUPDATE A SET A.CLVT = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;"));

        Assert.Null(Assert.Single(statements).Anchor);
    }
}
