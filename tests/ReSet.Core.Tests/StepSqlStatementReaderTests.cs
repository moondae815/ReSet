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
}
