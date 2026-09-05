using System;
using System.Linq;
using System.Threading.Tasks;
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

    // ─────────────────────────────────────────────────────────────────────
    // 하위 스코프 술어 — 원본이 최상위에 두었던 술어를 이행이 CTE·파생 테이블·
    // EXISTS로 옮기면, 최상위만 보는 PredicateColumns로는 "없어졌다"로 보인다.
    // 소실과 이전을 구분하려면 옮겨간 자리도 재료로 실어야 한다.
    // 대상 행을 거를 수 있는 세 자리(WITH·FROM·최상위 WHERE)에서만 모은다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CollectsCtePredicatesIntoSubordinateColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH FeeSource AS (\n" +
            "    SELECT A.PLTID, A.ID FROM dbo.TSettleMst AS A\n" +
            "    WHERE A.YMD = @p AND A.PGName = 'pointpay'\n" +
            ")\n" +
            "UPDATE Y SET Y.PGComm = 0 FROM dbo.TSettleMst AS Y\n" +
            "INNER JOIN FeeSource AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.SubordinatePredicateColumns);
        Assert.Contains("PGName", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("YMD", statement.PredicateColumns);
    }

    [Fact]
    public void CollectsDerivedTablePredicatesIntoSubordinateColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.PGComm = X.Amt FROM dbo.TSettleMst AS Y\n" +
            "INNER JOIN (\n" +
            "    SELECT S.PLTID, S.ID, 1 AS Amt FROM dbo.TSettleMst AS S\n" +
            "    WHERE S.DiscountFlag = 'Y'\n" +
            ") AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("DiscountFlag", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("DiscountFlag", statement.PredicateColumns);
    }

    [Fact]
    public void CollectsExistsSubqueryPredicatesIntoSubordinateColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.OutState = 9 FROM dbo.TSettleMst AS A\n" +
            "WHERE A.UseState = 0\n" +
            "  AND EXISTS (SELECT 1 FROM dbo.TSettleMst AS B\n" +
            "              WHERE B.PLTID = A.PLTID AND B.OutState = 9);"));

        var statement = Assert.Single(statements);
        Assert.Contains("PLTID", statement.SubordinatePredicateColumns);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.DoesNotContain("PLTID", statement.PredicateColumns);
    }

    // 갱신할 "값"을 고르는 술어이지 갱신할 "행"을 고르는 술어가 아니다. 이것을
    // 세면 우연히 이름이 같은 컬럼이 진짜 소실을 가려 잘못 침묵시킨다.
    [Fact]
    public void DoesNotCollectPredicatesFromSetClauseSubqueries()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = (SELECT TOP 1 X.Amt FROM dbo.TCost AS X WHERE X.Hidden = 1)\n" +
            "FROM dbo.TSettleMst AS Y WHERE Y.YMD = @p;"));

        var statement = Assert.Single(statements);
        Assert.DoesNotContain("Hidden", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("Hidden", statement.PredicateColumns);
    }

    [Fact]
    public void CollectsNestedSubordinateScopes()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Outer1 AS (\n" +
            "    SELECT S.PLTID FROM dbo.TSettleMst AS S\n" +
            "    WHERE S.YMD = @p\n" +
            "      AND S.PLTID IN (SELECT T.PLTID FROM dbo.TTx AS T WHERE T.Cancelled = 1)\n" +
            ")\n" +
            "UPDATE Y SET Y.OutState = 9 FROM dbo.TSettleMst AS Y\n" +
            "INNER JOIN Outer1 AS X ON X.PLTID = Y.PLTID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.SubordinatePredicateColumns);
        Assert.Contains("Cancelled", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void SubordinateColumnsAreEmptyWhenNoSubordinateScopeExists()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y WHERE Y.YMD = @p;"));

        Assert.Empty(Assert.Single(statements).SubordinatePredicateColumns);
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
    //
    // [Task 20] 이 신호는 이제 "펜스 전체 소실"이 아니라 "파싱에 실패해
    // 잃어버린 DML 문장 개수"를 센다 - 아래 GenuinelyUnparsableDmlStatement_
    // CountsAsOneLostStatement와 NonSqlProseFence_DoesNotCountAsLostStatement가
    // 그 차이를 보인다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void GenuinelyUnparsableDmlStatement_CountsAsOneLostStatement()
    {
        // 펜스 전체가 파싱 불가능한 DML 문장 하나뿐이면(예전 실측 그대로),
        // 손실 신호는 여전히 1이어야 한다 - 실재하는 INSERT 시도가 있었는데
        // 못 읽었다는 사실은 여전히 필요하다.
        var statements = StepSqlStatementReader.Read(
            Fence("INSERT INTO dbo.T SELECT /* 주석뿐 */ FROM dbo.S;"), out var lostStatementCount);

        Assert.Empty(statements);
        Assert.Equal(1, lostStatementCount);
    }

    [Fact]
    public void ParsableFenceWithNoStatements_DoesNotCountAsUnparsed()
    {
        // 파싱은 성공했지만 DML 문장이 없는 것(예: SET만 있는 펜스)은 "못
        // 읽음"이 아니다 - 이 둘을 같은 신호로 합치면 정상 펜스도 검사 A를
        // 접게 만든다.
        var statements = StepSqlStatementReader.Read(
            Fence("SET NOCOUNT ON;"), out var lostStatementCount);

        Assert.Empty(statements);
        Assert.Equal(0, lostStatementCount);
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

        var statements = StepSqlStatementReader.Read(markdown, out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "DELETE" && s.TargetTable == "TSettleByTX");
        Assert.Equal(1, lostStatementCount);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Task 20 - 파싱 실패가 펜스 전체를 삼키지 않게 한다.
    //
    // [실측] `TSqlParser.Parse`는 구문 오류를 만나면 그 지점에서 완전히
    // 멈춘다 - 오류 뒤의 실재하는 문장은 fragment에 전혀 나타나지 않는다
    // (실물 확인: POQSettleProc3/S08의 sp_getapplock 오류 뒤 UPDATE 2개가
    // 배치 전체 실패로 통째로 사라짐). 그래서 "부분 결과의 문장 중 오류와
    // 겹치는 것만 뺀다"는 접근은 통하지 않는다 - 오류 뒤 문장은 애초에
    // 부분 결과에 없다. 대신 펜스를 최상위(괄호 깊이 0) 세미콜론으로 잘라
    // 조각마다 독립적으로 파싱한다 - 한 조각의 오류가 다른 조각에 번지지
    // 않는다.
    // ─────────────────────────────────────────────────────────────────────

    private static string FenceRaw(string sql) => $"### 단계\n\n```sql\n{sql}\n```\n";

    [Fact]
    public void FailingStatementInTheMiddleOfAFence_DoesNotDropStatementsAfterIt()
    {
        // 실물 모양(POQSettleProc3/S08:81-85): 함수 호출식을 named-parameter
        // 값으로 쓰는 EXEC sp_getapplock 관용구는 ScriptDom 전 버전에서
        // 구문 오류를 낸다. 그 뒤에 오는 진짜 DELETE·UPDATE는 살아야 한다.
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "SET NOCOUNT ON;\n" +
            "EXEC @v_lockResult = sys.sp_getapplock @Resource = CONCAT('Job:S08:', @p), @LockMode = 'Exclusive';\n" +
            "DELETE FROM dbo.TSettleMst WHERE YMD = @p;\n" +
            "UPDATE dbo.TSettleMst SET CLCOMM = 1 WHERE YMD = @p;"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "DELETE" && s.TargetTable == "TSettleMst");
        Assert.Single(statements, s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst");
        // EXEC은 INSERT·UPDATE·DELETE가 아니므로 이 실패로 잃어버린 DML은 없다.
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void GenuinelyBrokenDmlStatement_IsExcludedButSurroundingStatementsSurvive()
    {
        // 실물 모양(S12.md:74-84): SELECT 목록이 통째로 주석으로 비어 그
        // 문장 자체가 파싱 불가능하다(산출물 결함 자체) - 같은 펜스의 앞뒤
        // DELETE·INSERT는 살아야 하고, 못 읽은 INSERT 1개는 손실로 집계된다.
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "DELETE FROM dbo.TSettleByTX WHERE YMD = @p;\n" +
            "INSERT INTO dbo.TPartialCancelByTX\n" +
            "SELECT\n" +
            "    /* 동일한 집계 열 */\n" +
            "FROM dbo.TSettleMst\n" +
            "WHERE YMD = @p\n" +
            "GROUP BY YMD;\n" +
            "INSERT INTO dbo.TSettleByOUT (YMD) VALUES (@p);"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "DELETE" && s.TargetTable == "TSettleByTX");
        Assert.Single(statements, s => s.Kind == "INSERT" && s.TargetTable == "TSettleByOUT");
        Assert.DoesNotContain(statements, s => s.TargetTable == "TPartialCancelByTX");
        Assert.Equal(1, lostStatementCount);
    }

    [Fact]
    public void FailedChunkWithNoDmlKeyword_DoesNotCountAsLost()
    {
        // BEGIN TRY·IF EXISTS 같은 제어문 조각은 세미콜론 분할 과정에서
        // 자연스럽게 단독으로는 파싱되지 않는 조각이 될 수 있다 - 그 조각에
        // INSERT·UPDATE·DELETE가 없으면 잃어버린 DML이 아니므로 세면 안 된다.
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "BEGIN TRY\n" +
            "    BEGIN TRAN;\n" +
            "    UPDATE dbo.TSettleMst SET CLCOMM = 1 WHERE YMD = @p;\n" +
            "    COMMIT TRAN;\n" +
            "END TRY\n" +
            "BEGIN CATCH\n" +
            "    ROLLBACK TRAN;\n" +
            "END CATCH;"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst");
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void DmlStatementImmediatelyAfterBareBegin_IsRecoveredNotLost()
    {
        // 실물 모양(POQSettleBatch1/S01.md): `IF @ErrorCode IS NOT NULL BEGIN
        // UPDATE ... WHERE ...; END`처럼 DML이 세미콜론 없는 `BEGIN` 바로
        // 다음에 오면, `BEGIN`을 분할 지점으로 안 두면 "IF ... BEGIN UPDATE
        // ...;" 조각 전체가 BEGIN 짝이 안 맞아 파싱에 실패해 실재하는 UPDATE가
        // 억울하게 손실로 잡힌다(코퍼스 실측 25개 표본 중 6개가 이 모양).
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "IF @ErrorCode IS NOT NULL\n" +
            "BEGIN\n" +
            "    UPDATE dbo.TSettleMst SET CLCOMM = 1 WHERE YMD = @p;\n" +
            "END;"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst");
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void BareBeginTransaction_IsNotSplitAwayFromItsOwnStatement()
    {
        // `BEGIN TRAN`·`BEGIN TRANSACTION`은 블록 여는 `BEGIN`이 아니라 그
        // 자체로 완결된 문장이다 - 이걸 분할 지점으로 다뤄도 결과가 달라지지는
        // 않지만(그 문장에는 DML 키워드가 없다), 있는 그대로 한 조각으로 남아야
        // 불필요한 조각 증식을 피한다.
        var statements = StepSqlStatementReader.Read(FenceRaw(
            "BEGIN TRANSACTION;\n" +
            "UPDATE dbo.TSettleMst SET CLCOMM = 1 WHERE YMD = @p;\n" +
            "COMMIT TRANSACTION;"),
            out var lostStatementCount);

        Assert.Single(statements, s => s.Kind == "UPDATE" && s.TargetTable == "TSettleMst");
        Assert.Equal(0, lostStatementCount);
    }

    [Fact]
    public void NonSqlProseFence_DoesNotCountAsLostStatement()
    {
        // 순수 산문·의사코드에는 애초에 DML이 없으니 잃어버릴 것도 없다 -
        // 이전 구현은 이런 펜스도 "1개 손실"로 셌지만, 이는 근거 없는 신호였다.
        var statements = StepSqlStatementReader.Read(
            FenceRaw("이것은 SQL이 아니다 <<<>>>"), out var lostStatementCount);

        Assert.Empty(statements);
        Assert.Equal(0, lostStatementCount);
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

    // ─────────────────────────────────────────────────────────────────────
    // Task 22 - 문장↔spec 행 대응. 실물(S07·S11) 관용구는 앵커 주석과 DML
    // 사이에 `SET @v_currentStepId = <오류코드>;` 한 줄이 끼어 있다(AiService의
    // [Precise Error Tracking] 규칙이 요구하는 필수 패턴) - 예전 ReadAnchor는
    // "바로 앞 토큰"만 봐서 이 SET에 막혀 앵커를 전부 놓쳤다(코퍼스 326개 전수
    // 0개, docs/known-defects.md 참고).
    //
    // 다만 SET을 무조건 건너뛰는 것만으로는 부족하다 - 태스크 11이 실측으로
    // 반증했다: 미구현 갱신의 서술 주석(DML 없음)이 SET과 함께 남아 있으면,
    // 그 뒤에 오는 무관한 실제 DML이 그 주석을 "훔친다"(S07:244가 U15를
    // 훔쳐 실제로는 UPDATE 16인데 15로 오귀속된 실물 3건).
    //
    // 그래서 여기서는 "가장 가까운 주석 하나"가 아니라 "직전 문장(또는 펜스
    // 시작)과 이 문장 사이 구간에 앵커 모양 주석이 정확히 1개일 때만" 신뢰한다.
    // 훔친 사례는 이 구간에 주석이 2개 이상(U14 자리 + U15 자리) 걸리므로
    // 자동으로 침묵한다 - 내용 매칭 없이 순수하게 기계적으로 판별된다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnchorSurvivesInterveningSetStatement()
    {
        // 실물 관용구 그대로: 주석 → SET @v_currentStepId = <코드> → DML.
        var statements = StepSqlStatementReader.Read(Fence(
            "/* U13: 원천카드 수수료 */\n" +
            "SET @v_currentStepId = -20;\n" +
            "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y WHERE Y.YMD = @p;"));

        Assert.Equal(13, Assert.Single(statements).Anchor);
    }

    [Fact]
    public void OrphanedPlaceholderAnchors_AreNotStolenByALaterUnrelatedStatement()
    {
        // 실물 모양(S07:238-244): U14·U15는 서술 주석만 있고 DML이 없다(미구현).
        // 그 뒤에 오는 실제 DML(진짜로는 spec UPDATE 16)이 가장 가까운 주석
        // "U15"를 훔치면 안 된다 - 이 구간에 주석이 2개(U14, U15) 있으므로
        // 유일하지 않아 침묵해야 한다.
        var statements = StepSqlStatementReader.Read(Fence(
            "/* U13: 원천카드 수수료 */\n" +
            "SET @v_currentStepId = -20;\n" +
            "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y WHERE Y.YMD = @p;\n" +
            "SET @v_currentStepId = -201;\n" +
            "/* U14: 미구현 자리 */\n" +
            "SET @v_currentStepId = -21;\n" +
            "/* U15: 미구현 자리 */\n" +
            "SET @v_currentStepId = -27;\n" +
            "UPDATE Z SET Z.CLCOMM = 1 FROM dbo.TSettleMst AS Z WHERE Z.YMD = @p;"));

        Assert.Equal(2, statements.Count);
        Assert.Equal(13, statements[0].Anchor);
        Assert.Null(statements[1].Anchor); // U14·U15 둘 다 걸려 유일하지 않다 - 훔치지 않는다.
    }

    [Fact]
    public void SecondStatementsCleanAnchor_IsNotConfusedByFirstStatementsAnchor()
    {
        // 앞 문장의 앵커가 뒤 문장의 구간으로 새지 않아야 한다 - 창은 직전
        // 문장의 끝부터 시작한다.
        var statements = StepSqlStatementReader.Read(Fence(
            "/* U1: 첫 번째 */\n" +
            "SET @v_currentStepId = -101;\n" +
            "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
            "/* U2: 두 번째 */\n" +
            "SET @v_currentStepId = -102;\n" +
            "UPDATE B SET B.CLVT = 2 FROM dbo.TSettleMst AS B WHERE B.YMD = @p;"));

        Assert.Equal(2, statements.Count);
        Assert.Equal(1, statements[0].Anchor);
        Assert.Equal(2, statements[1].Anchor);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Task 22 - HasOpaqueJoinSource. 실물(S07 U2·U13·U17)은 원본의 단일
    // UPDATE를 `UPDATE Y ... FROM 대상 AS Y INNER JOIN <CTE 또는 파생 테이블>
    // AS X ON <좁은 키>`로 재구성한다 - 조인 키 칸의 실제 필터(예: PGName·
    // ClientID)는 최상위 ON절이 아니라 그 CTE·파생 테이블 자신의 WHERE/ON
    // 안에 있다. 최상위만 보는 JoinColumns 수집은 이걸 볼 수 없어 "조인 키가
    // 없다"는 거짓 결과를 낸다 - 문장 내용은 멀쩡한데 조인 파트너가 물리
    // 테이블이 아니라 계산용 서브쿼리라서 생기는 구조적 사각지대다. 이
    // 신호는 그 사각지대가 있었는지만 표시한다 - 값을 보정하지 않는다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FlagsOpaqueJoinSource_WhenJoinPartnerIsACte()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH X AS (SELECT A.PLTID, A.ID, A.PGName FROM dbo.TSettleMst AS A WHERE A.PGName = 'PLCard')\n" +
            "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y\n" +
            "INNER JOIN X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;"));

        Assert.True(Assert.Single(statements).HasOpaqueJoinSource);
    }

    [Fact]
    public void FlagsOpaqueJoinSource_WhenJoinPartnerIsADerivedTable()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = R.PGCOMM FROM dbo.TSettleMst AS Y\n" +
            "INNER JOIN (SELECT S.PLTID, S.ID, S.PGCOMM FROM dbo.TStage AS S WHERE S.PGName = 'PLCard') AS R\n" +
            "  ON R.PLTID = Y.PLTID AND R.ID = Y.ID;"));

        Assert.True(Assert.Single(statements).HasOpaqueJoinSource);
    }

    [Fact]
    public void DoesNotFlagOpaqueJoinSource_WhenAllJoinPartnersArePhysicalTables()
    {
        // 회귀 방지: S11 갱신 9처럼 조인 파트너가 전부 실물 테이블이면(CTE·파생
        // 테이블 없음) 이 신호가 서지 않아야 한다 - 정상적인 조인 키 검사를
        // 계속 신뢰할 수 있어야 한다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.EDIReqYMD = E.ReqYMD FROM dbo.TSettleMst AS A\n" +
            "INNER JOIN dbo.TPLCardEDIMst AS E ON A.PLTID = E.PLTID;"));

        Assert.False(Assert.Single(statements).HasOpaqueJoinSource);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Task 5: 코드 앵커(음수 오류 코드 라벨) — ReadAnchor와 같은 구간에서
    // 「구간에 정확히 하나」일 때만 읽는다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_CodeLabelBeforeUpdate_ShouldBeReadAsCodeAnchor()
    {
        const string step = @"```sql
-- -13: 원천카드 수동매입 지급일 및 매입요청일
SET @v_currentStepId = -13;
UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;
```";

        var statement = Assert.Single(StepSqlStatementReader.Read(step));

        Assert.Equal("-13", statement.CodeAnchor);
    }

    [Fact]
    public void Read_TwoNegativeAssignmentsInInterval_ShouldStaySilent()
    {
        const string step = @"```sql
SET @v_currentStepId = -12;
SET @v_currentStepId = -13;
UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;
```";

        var statement = Assert.Single(StepSqlStatementReader.Read(step));

        Assert.Null(statement.CodeAnchor);
    }

    [Fact]
    public void Read_NonNegativeAssignmentsInInterval_ShouldNotBeCandidates()
    {
        // 초기화 0과 @@ROWCOUNT 대입, 그리고 `SET @v = 5;`처럼 부호만 다른 진짜
        // 후보 모양(양수 정수 리터럴 SET 대입)이 후보가 되면 「구간에 하나」가
        // 절대 성립하지 않는다. `SET @v_rowCount = 5;`가 이 셋 중 실제로 부호
        // 필터(음수만 후보)가 없으면 후보가 될 모양이다 - 나머지 둘(DECLARE·
        // @@ROWCOUNT)은 부호와 무관한 이유로 이미 제외된다.
        const string step = @"```sql
DECLARE @v_currentStepId INT = 0;
SET @v_cnt = @@ROWCOUNT;
SET @v_rowCount = 5;
SET @v_currentStepId = -13;
UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;
```";

        var statement = Assert.Single(StepSqlStatementReader.Read(step));

        Assert.Equal("-13", statement.CodeAnchor);
    }

    [Fact]
    public void Read_OnlyNonNegativeSetAssignmentInInterval_ShouldNotYieldCodeAnchor()
    {
        // 구간에 음수 후보가 전혀 없고 양수 정수 리터럴 SET 대입만 있으면
        // CodeAnchor는 null이어야 한다 - 부호 필터가 없으면 이 양수 대입이
        // 유일한 후보로 잡혀 "-13" 같은 값 대신 "5"가 나온다.
        const string step = @"```sql
SET @v_rowCount = 5;
UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;
```";

        var statement = Assert.Single(StepSqlStatementReader.Read(step));

        Assert.Null(statement.CodeAnchor);
    }

    [Fact]
    public void Read_VariableNameIsNotFixed()
    {
        // 규약 6-1은 @v_currentStepId를 예시로 들 뿐 이름을 못 박지 않는다.
        const string step = @"```sql
SET @v_step = -7;
UPDATE A SET A.X = 1 FROM dbo.T AS A;
```";

        var statement = Assert.Single(StepSqlStatementReader.Read(step));

        Assert.Equal("-7", statement.CodeAnchor);
    }

    [Fact]
    public void Read_UAnchorAndCodeAnchorCanCoexist()
    {
        const string step = @"```sql
/* U13: 카드사 원가 반영 */
SET @v_currentStepId = -13;
UPDATE A SET A.X = 1 FROM dbo.T AS A;
```";

        var statement = Assert.Single(StepSqlStatementReader.Read(step));

        Assert.Equal(13, statement.Anchor);
        Assert.Equal("-13", statement.CodeAnchor);
    }

    [Fact]
    public void Read_SetPatternInsideCommentOnly_ShouldNotBeReadAsCodeAnchor()
    {
        // 실물 관용구(output/Jobs/POQSettleProc12/agent/common/01-step-contract.md의
        // "-- 예시: SET @v_currentStepId = -101;" 같은 예시 문구)가 주석 안에만
        // 있을 뿐 실제 SET 문이 아니면 CodeAnchor는 잡히면 안 된다. ReadAnchor가
        // 주석 토큰만 후보로 보는 것과 반대로, ReadCodeAnchor는 주석이 아닌
        // 실코드 토큰만 후보로 봐야 한다.
        const string step = @"```sql
-- 예시: SET @v_currentStepId = -101;
UPDATE A SET A.X = 1 FROM dbo.T AS A;
```";

        var statement = Assert.Single(StepSqlStatementReader.Read(step));

        Assert.Null(statement.CodeAnchor);
    }

    // ─────────────────────────────────────────────────────────────────────
    // INSERT 원천 술어 배선(설계 2026-08-26-insert-source-predicate-design.md).
    //
    // InsertSpecification에는 WhereClause·FromClause 속성이 없다 - 술어는
    // InsertSource(→ SelectInsertSource.Select)의 QuerySpecification 안에 있다.
    // 예전에는 그 자리에 null을 넘겨 모든 INSERT의 PredicateColumns가 구조적으로
    // 항상 비었고, 그 빈 목록이 검사 B의 거짓양성 199건(코퍼스 스윕 269건 중 74%)을
    // 만들었다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Insert_SourceWhere_FillsPredicateColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
            "SELECT S.YMD, SUM(S.TXAMT) FROM dbo.TSettleMst AS S\n" +
            "WHERE S.UseState = 0 AND S.YMD = @p\n" +
            "GROUP BY S.YMD;"));

        var statement = Assert.Single(statements);
        Assert.Equal("INSERT", statement.Kind);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.Contains("YMD", statement.PredicateColumns);
    }

    [Fact]
    public void Insert_SourceJoin_FillsJoinColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
            "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S\n" +
            "INNER JOIN dbo.TCost AS C ON C.PLTID = S.PLTID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("PLTID", statement.JoinColumns);
    }

    [Fact]
    public void Insert_UnionSource_MergesBothBranches()
    {
        // DmlScopeExtractor는 UNION 갈래들을 같은 서수 하나로 합쳐 명세서에 적는다.
        // 읽기 쪽이 한 갈래만 보면 나머지 갈래의 술어가 "없어졌다"로 보인다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
            "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S WHERE S.UseState = 0\n" +
            "UNION ALL\n" +
            "SELECT T.YMD, T.TXAMT FROM dbo.TSettleEtc AS T WHERE T.Cancelled = 1;"));

        var statement = Assert.Single(statements);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.Contains("Cancelled", statement.PredicateColumns);
    }

    [Fact]
    public void Insert_ValuesSource_CollectsNothing()
    {
        // VALUES 원천은 조건 없이 실리는 행이라 대조할 술어가 없다.
        // SourceQuerySpecifications가 빈 열거를 내고, 그 결과 목록이 비어야 한다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO batch.BatchStepJournal (RunId, StepCode) VALUES (1, 'S07');"));

        var statement = Assert.Single(statements);
        Assert.Empty(statement.PredicateColumns);
        Assert.Empty(statement.JoinColumns);
        Assert.Empty(statement.SubordinatePredicateColumns);
        Assert.False(statement.HasOpaqueJoinSource);
    }

    [Fact]
    public void Insert_DerivedTableSource_GoesToSubordinate()
    {
        // UP_UTIL_SETTLE_INS의 INSERT 1이 이 모양이다 - 명세서는 최상위 술어 칸에
        // "(없음)"을 적고 실제 필터는 「집합 술어」표에 "파생 테이블 X"로 따로 적는다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleMst (YMD, PGNAME)\n" +
            "SELECT X.YMD, X.PGNAME FROM (\n" +
            "  SELECT A.YMD, A.PGNAME FROM dbo.TRaw AS A WHERE A.UseState = 0\n" +
            ") AS X;"));

        var statement = Assert.Single(statements);
        Assert.Contains("UseState", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("UseState", statement.PredicateColumns);
    }

    [Fact]
    public void Insert_TargetNotResolvedFromSourceAlias()
    {
        // INSERT 대상은 별칭일 수 없다. 원천 FROM의 별칭 사전을 대상 해석에 쓰면
        // 여기서 대상이 "TFoo"로 잘못 풀린다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO TSettleMst (YMD)\n" +
            "SELECT TSettleMst.YMD FROM dbo.TFoo AS TSettleMst WHERE TSettleMst.UseState = 0;"));

        var statement = Assert.Single(statements);
        Assert.Equal("TSettleMst", statement.TargetTable);
    }

    [Fact]
    public void Insert_OpaqueSourceJoin_SetsHasOpaqueJoinSource()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH CardCost AS (SELECT A.PLTID, A.Amt FROM dbo.TCost AS A WHERE A.YMD = @p)\n" +
            "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
            "SELECT S.YMD, C.Amt FROM dbo.TSettleMst AS S\n" +
            "INNER JOIN CardCost AS C ON C.PLTID = S.PLTID;"));

        var statement = Assert.Single(statements);
        Assert.True(statement.HasOpaqueJoinSource);
    }

    [Fact]
    public void Insert_CteBodyPredicate_GoesToSubordinate()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH CardCost AS (SELECT A.PLTID FROM dbo.TCost AS A WHERE A.YMD = @p)\n" +
            "INSERT INTO dbo.TSettleSum (PLTID)\n" +
            "SELECT C.PLTID FROM CardCost AS C;"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("YMD", statement.PredicateColumns);
    }

    [Fact]
    public void Update_UnchangedAfterPluralClauseSignature()
    {
        // Add의 절 인자가 목록이 된 뒤에도 UPDATE 경로의 관측 동작은 그대로다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE Y SET Y.CLCOMM = 1\n" +
            "FROM dbo.TSettleMst AS Y INNER JOIN dbo.TCost AS C ON C.PLTID = Y.PLTID\n" +
            "WHERE Y.YMD = @p AND Y.UseState = 1;"));

        var statement = Assert.Single(statements);
        Assert.Equal("TSettleMst", statement.TargetTable);
        Assert.Contains("YMD", statement.PredicateColumns);
        Assert.Contains("UseState", statement.PredicateColumns);
        Assert.Contains("PLTID", statement.JoinColumns);
    }
    // ─────────────────────────────────────────────────────────────────────
    // 하위 범위의 JOIN ON — 최상위와의 비대칭을 없앤다.
    //
    // [왜 필요한가 - 2026-08-26 코퍼스 판정 15건]
    // 최상위 대조는 `PredicateColumns ∪ JoinColumns`다(명세서 술어 칸이 "조인 결합
    // 포함"이므로). 그런데 하위 범위는 WHERE만 봐서, CTE 안 `ON A.CLIENTID = B.CLIENTID`의
    // 조인 키가 이전으로 인정되지 않았다. 명세서는 그 컬럼을 술어 칸에 적으므로
    // 검사 B가 "없어졌다"고 오인했다 - 증가분 37건 중 15건이 이 축이다
    // (docs/known-defects.md (5-3-3) 부류 1).
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SubordinateJoinOn_ColumnsAreCollected()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH ClientRateSource AS (\n" +
            "  SELECT A.CLIENTID, B.Rate FROM dbo.TClient AS A\n" +
            "  INNER JOIN dbo.TRate AS B ON A.CLIENTID = B.CLIENTID\n" +
            "  WHERE A.USESTATE = 0\n" +
            ")\n" +
            "UPDATE Y SET Y.CLCOMM = C.Rate\n" +
            "FROM dbo.TSettleMst AS Y INNER JOIN ClientRateSource AS C ON C.CLIENTID = Y.CLIENTID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("USESTATE", statement.SubordinatePredicateColumns);   // 종전에도 잡혔다
        Assert.Contains("CLIENTID", statement.SubordinatePredicateColumns);   // CTE 안 ON - 이번에 추가
    }

    [Fact]
    public void SubordinateJoinOn_DoesNotReachIntoDeeperDerivedTable_AtThisLevel()
    {
        // ColumnCollector가 QueryDerivedTable 하강을 막으므로 이 층에서는 그 층의 ON만
        // 모은다. 더 깊은 층은 SubordinatePredicateCollector가 그 QuerySpecification을
        // 따로 방문할 때 잡는다 - 중복도 누락도 없다는 것을 못으로 박는다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Outer2 AS (\n" +
            "  SELECT X.PLTID FROM (\n" +
            "    SELECT D.PLTID FROM dbo.TDeep AS D INNER JOIN dbo.TOther AS E ON D.DeepLeft = E.DeepRight\n" +
            "  ) AS X INNER JOIN dbo.TMid AS M ON X.PLTID = M.PLTID\n" +
            ")\n" +
            "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y INNER JOIN Outer2 AS O ON O.PLTID = Y.PLTID;"));

        var statement = Assert.Single(statements);
        Assert.Contains("PLTID", statement.SubordinatePredicateColumns);    // CTE 층의 ON
        Assert.Contains("DeepLeft", statement.SubordinatePredicateColumns); // 더 깊은 층도 결국 잡힌다

        // [이 단언이 하강 차단을 실제로 잰다]
        // 위 둘만으로는 부족하다 - ColumnCollector가 QueryDerivedTable 하강을 막지
        // **않았더라도** 둘 다 통과한다. 차단이 관측되는 자리는 **중복**이다.
        // 바깥 층에서 하강했다면 안쪽 ON의 컬럼을 거기서 한 번, 안쪽
        // QuerySpecification을 따로 방문할 때 또 한 번 모아 두 번 들어온다.
        //
        // [왜 조인 양변의 이름을 다르게 두는가 - 이 테스트를 처음 쓸 때 걸린 함정]
        // `ON D.Key = E.Key`처럼 양변 이름이 같으면 한 번의 수집만으로도 컬럼 참조가
        // **둘** 잡힌다(ColumnCollector는 이름을 모은다). 그러면 중복 수가 하강 여부를
        // 가리지 못한다. 양변을 DeepLeft·DeepRight로 갈라야 판별자가 된다.
        // (SubordinatePredicateColumns는 중복 제거를 하지 않는다 - 별개 미결 항목.)
        Assert.Equal(1, statement.SubordinatePredicateColumns
            .Count(c => string.Equals(c, "DeepLeft", StringComparison.OrdinalIgnoreCase)));
    }

    // ─────────────────────────────────────────────────────────────────────
    // CTE 투영 별칭 — 바깥 CTE가 이름을 바꿔 투영하고 안쪽 CTE가 그 별칭으로
    // 거르면, 수집되는 이름이 명세서의 원래 컬럼 이름과 글자가 달라 이전으로
    // 인정되지 않는다(docs/known-defects.md (5-3-3) 부류 2, 코퍼스 3건).
    // 별칭을 원천 컬럼 이름으로 되돌려 **함께** 싣는다 - 별칭도 남긴다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CteProjectionAlias_InlineAs_ResolvesToSourceColumn()
    {
        // POQSettlePrco20/S03 축소판. 별칭은 SELECT 목록에만 있고 WHERE에는
        // 별칭 이름만 나오므로, 해석이 없으면 USESTATE·ContractCancelYMD는
        // 어디에서도 수집되지 않는다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base AS (\n" +
            "  SELECT A.CLIENTID, A.USESTATE AS ContractUseState,\n" +
            "         B.ContractCancelYMD AS CMRateCancelYMD\n" +
            "  FROM dbo.TClientContract AS A\n" +
            "  INNER JOIN dbo.TClientCMRate AS B ON A.CLIENTID = B.CLIENTID\n" +
            "),\n" +
            "Filtered AS (\n" +
            "  SELECT CLIENTID FROM Base\n" +
            "  WHERE ContractUseState IN (0, 4) AND CMRateCancelYMD = @p\n" +
            ")\n" +
            "INSERT INTO dbo.TClientSettleRate (CLIENTID) SELECT CLIENTID FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.Contains("USESTATE", statement.SubordinatePredicateColumns);
        Assert.Contains("ContractCancelYMD", statement.SubordinatePredicateColumns);

        // 별칭도 남는다 - 지우면 다른 대조가 무엇에 기대는지 알 수 없다.
        Assert.Contains("ContractUseState", statement.SubordinatePredicateColumns);
        Assert.Contains("CMRateCancelYMD", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_PositionalColumnList_ResolvesToSourceColumn()
    {
        // POQSettleProc17/S04 축소판. `AS`가 **하나도 없다** - 별칭이 CTE의
        // 명시 컬럼 목록에 위치로 붙는다. SelectScalarExpression.ColumnName만
        // 보는 구현은 이 갈래를 통째로 놓친다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base (CID, ContractUseState, RateUseState) AS (\n" +
            "  SELECT A.CLIENTID, A.USESTATE, B.USESTATE\n" +
            "  FROM dbo.TClientContract AS A\n" +
            "  INNER JOIN dbo.TClientCMRate AS B ON A.CLIENTID = B.CLIENTID\n" +
            "),\n" +
            "Filtered AS (\n" +
            "  SELECT CID FROM Base WHERE ContractUseState IN (0, 4) AND RateUseState = 5\n" +
            ")\n" +
            "INSERT INTO dbo.TClientSettleRate (CLIENTID) SELECT CID FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.Contains("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_UnionBody_ResolvesThroughEveryBranch()
    {
        // 별칭을 **정의하는** CTE 본문이 BinaryQueryExpression이면 캐스트 하나로는
        // 갈래를 못 편다. DmlScopeExtractor.QuerySpecificationsOf가 그 자리다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base AS (\n" +
            "  SELECT A.USESTATE AS ContractUseState FROM dbo.TClientContract AS A\n" +
            "  UNION ALL\n" +
            "  SELECT B.USESTATE AS ContractUseState FROM dbo.TClientCMRate AS B\n" +
            "),\n" +
            "Filtered AS (\n" +
            "  SELECT 1 AS Kept FROM Base WHERE ContractUseState = 0\n" +
            ")\n" +
            "INSERT INTO dbo.TClientSettleRate (CLIENTID) SELECT Kept FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.Contains("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_TwoCtesInOneScopeDisagree_IsNotResolved()
    {
        // 한 스코프가 CTE 둘을 읽고 둘이 같은 별칭을 다른 원천에 붙이면 그 별칭은
        // 이 스코프에서 모호하다. 조용한 거짓 음성보다 사람이 판정하는 거짓 양성을
        // 택하는 이 저장소의 관례를 따라 해석하지 않는다(MergeErrorCodeMaps의 충돌
        // 코드 제거·재사용 가드의 모호 서수 제거).
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH B1 AS ( SELECT A.CLIENTID, A.USESTATE AS Flag FROM dbo.TClientContract AS A ),\n" +
            "B2 AS ( SELECT C.CLIENTID, C.INSTATE AS Flag FROM dbo.TClientCMRate AS C ),\n" +
            "Filtered AS (\n" +
            "  SELECT 1 AS Kept FROM B1 INNER JOIN B2 ON B2.CLIENTID = B1.CLIENTID WHERE Flag = 0\n" +
            ")\n" +
            "INSERT INTO dbo.TClientSettleRate (CLIENTID) SELECT Kept FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.Contains("Flag", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("INSTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_UnreadCte_DoesNotLeakIntoOtherScopes()
    {
        // [리뷰 F1] 사상이 문장 전역이면 아무도 읽지 않는 CTE의 별칭이 무관한
        // 스코프의 **같은 이름 실컬럼**에 붙는다. 여기서 걸러지는 것은 `TZ.YMD`
        // 이고 `Unused`는 아무도 읽지 않는다. 그런데 CANCELYMD가 실리면,
        // 명세서가 CANCELYMD를 확정해 두었고 이행이 그 필터를 **실제로 지웠어도**
        // 검사 B가 침묵한다 - 이 목록을 넓히는 변경의 유일한 위험축이다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Unused AS ( SELECT A.CANCELYMD AS YMD FROM dbo.TContract AS A )\n" +
            "DELETE FROM dbo.TSettleMst\n" +
            "WHERE EXISTS (SELECT 1 FROM dbo.TZ AS Z WHERE Z.YMD = @p);"));

        var statement = Assert.Single(statements);
        Assert.Contains("YMD", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("CANCELYMD", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_NonColumnSource_PoisonsTheAlias()
    {
        // [리뷰 F2] 원천 식이 컬럼 참조가 아니면 어느 컬럼에서 왔는지 알 수 없다.
        // "정의가 없다"가 아니라 "정의가 모호하다"이므로 경쟁하는 정의로 세야
        // 한다. 안 세면 B1의 Flag를 거르는 스코프가 B2의 USESTATE를 넘겨받는다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH B1 AS ( SELECT A.CLIENTID, CONVERT(int, A.INSTATE) AS Flag FROM dbo.T1 AS A ),\n" +
            "B2 AS ( SELECT C.CLIENTID, C.USESTATE AS Flag FROM dbo.T2 AS C ),\n" +
            "Filtered AS (\n" +
            "  SELECT 1 AS Kept FROM B1 INNER JOIN B2 ON B2.CLIENTID = B1.CLIENTID WHERE Flag = 0\n" +
            ")\n" +
            "INSERT INTO dbo.TOut (X) SELECT Kept FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_IdentityProjection_CountsAsCompetingDefinition()
    {
        // [리뷰 F9] 항등 사상(`X AS X`)을 경쟁 정의로 세지 않으면, 같은 이름을
        // 그대로 투영하는 갈래가 다른 갈래의 개명과 충돌해도 안 걸린다.
        // 한 CTE의 UNION 두 갈래가 서로 다르게 말하는 모양으로 못을 박는다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH B AS (\n" +
            "  SELECT A.USESTATE AS Flag FROM dbo.T1 AS A\n" +
            "  UNION ALL\n" +
            "  SELECT C.Flag AS Flag FROM dbo.T2 AS C\n" +
            "),\n" +
            "Filtered AS ( SELECT 1 AS Kept FROM B WHERE Flag = 0 )\n" +
            "INSERT INTO dbo.TOut (X) SELECT Kept FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_ResolvesCaseInsensitively()
    {
        // [리뷰 F10] T-SQL 기본 대조가 대소문자를 안 가리므로 사상도 안 가려야
        // 한다. **두 자리**를 한꺼번에 잰다 - 별칭 키(`contractusestate`)와
        // CTE 이름 조회(`FROM base`). 어느 쪽을 Ordinal로 바꿔도 이 테스트가 죽는다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base AS ( SELECT A.USESTATE AS ContractUseState FROM dbo.T1 AS A ),\n" +
            "Filtered AS ( SELECT 1 AS Kept FROM base WHERE contractusestate = 0 )\n" +
            "INSERT INTO dbo.TOut (X) SELECT Kept FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.Contains("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_NeverEmitsNullIntoTheColumnList()
    {
        // [리뷰 F8] "해석하지 않는다"는 결정이 목록의 **형태**로도 지켜져야 한다.
        // DoesNotContain 단언만으로는 null이 실려도 통과한다 - 이 목록은
        // MechanicalValidator에서 HashSet<string>으로 들어간다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH B1 AS ( SELECT A.CLIENTID, A.USESTATE AS Flag FROM dbo.T1 AS A ),\n" +
            "B2 AS ( SELECT C.CLIENTID, C.INSTATE AS Flag FROM dbo.T2 AS C ),\n" +
            "Filtered AS (\n" +
            "  SELECT 1 AS Kept FROM B1 INNER JOIN B2 ON B2.CLIENTID = B1.CLIENTID WHERE Flag = 0\n" +
            ")\n" +
            "INSERT INTO dbo.TOut (X) SELECT Kept FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.All(statement.SubordinatePredicateColumns, Assert.NotNull);
    }

    [Fact]
    public void CteProjectionAlias_PositionalCountMismatch_IsSkipped()
    {
        // 이름 둘에 요소 셋이면 위치 짝짓기가 성립하지 않는다. 그냥 앞에서부터
        // 짝지으면 Flag가 USESTATE로 잘못 풀린다 - 그 오답이 관측되는 자리다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base (CID, Flag) AS (\n" +
            "  SELECT A.CLIENTID, A.USESTATE, A.INSTATE FROM dbo.TClientContract AS A\n" +
            "),\n" +
            "Filtered AS ( SELECT CID FROM Base WHERE Flag = 0 )\n" +
            "INSERT INTO dbo.TClientSettleRate (CLIENTID) SELECT CID FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.Contains("Flag", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_SelectStar_IsSkipped()
    {
        // 별표는 요소 하나가 몇 컬럼으로 펼쳐지는지 파서가 모른다. 위치 짝짓기의
        // 전제가 깨지므로 그 CTE는 통째로 건너뛴다.
        //
        // [왜 `SELECT *`가 아니라 `SELECT A.*, A.USESTATE`인가 - 이 테스트를 처음
        // 쓸 때 걸린 함정] 요소가 별표 하나뿐이면 개수(1)가 이름 수(2)와 달라
        // **개수 검사**에 먼저 걸린다. 그러면 별표 가드를 지워도 이 테스트는
        // 죽지 않아 자기 이름을 재지 못한다. 개수가 맞으면서 별표가 섞여야
        // 판별자가 된다 - 가드가 없으면 Flag가 USESTATE로 잘못 풀린다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base (CID, Flag) AS (\n" +
            "  SELECT A.*, A.USESTATE FROM dbo.TClientContract AS A\n" +
            "),\n" +
            "Filtered AS ( SELECT CID FROM Base WHERE Flag = 0 )\n" +
            "INSERT INTO dbo.TClientSettleRate (CLIENTID) SELECT CID FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.Contains("Flag", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_SameNameRealColumnInScope_IsNotResolved()
    {
        // [재리뷰 R1] F1을 스코프로 가둔 뒤에도 **스코프 안**에 같은 병이 남아
        // 있었다. 걸러지는 것은 `TReal.Flag`인데 B1의 `Flag → USESTATE`가 실렸다.
        // 한정자가 실테이블 별칭이면 해석하지 않아야 한다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH B1 AS ( SELECT A.CLIENTID, A.USESTATE AS Flag FROM dbo.T1 AS A ),\n" +
            "Filtered AS (\n" +
            "  SELECT 1 AS Kept FROM B1 INNER JOIN dbo.TReal AS R ON R.CLIENTID = B1.CLIENTID\n" +
            "  WHERE R.Flag = 9\n" +
            ")\n" +
            "INSERT INTO dbo.TOut (X) SELECT Kept FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.Contains("Flag", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_OtherCteRealColumnInScope_IsNotResolved()
    {
        // [재리뷰 R1 시나리오 B] 스코프가 CTE 둘을 읽는데 하나만 그 별칭을
        // 정의하고, 다른 하나는 **같은 이름의 실컬럼**을 투영한다. 실행 가능한
        // SQL이면서 F1과 같은 병인 모양이라 「CTE 둘이면 버린다」로는 안 잡힌다 -
        // 한정자가 B2를 가리키므로 B1의 정의를 쓰면 안 된다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH B1 AS ( SELECT A.CLIENTID, A.USESTATE AS Flag FROM dbo.T1 AS A ),\n" +
            "B2 AS ( SELECT C.CLIENTID, C.Flag FROM dbo.T2 AS C ),\n" +
            "Filtered AS (\n" +
            "  SELECT 1 AS Kept FROM B1 INNER JOIN B2 ON B2.CLIENTID = B1.CLIENTID\n" +
            "  WHERE B2.Flag = 9\n" +
            ")\n" +
            "INSERT INTO dbo.TOut (X) SELECT Kept FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_QualifiedByCteAlias_IsResolved()
    {
        // 한정자가 이 스코프에서 CTE에 결합돼 있으면 해석한다. T-SQL은 별칭을
        // 붙이면 원래 이름을 못 쓰므로, 결합 이름은 별칭 쪽이다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base AS ( SELECT A.USESTATE AS ContractUseState FROM dbo.T1 AS A ),\n" +
            "Filtered AS ( SELECT 1 AS Kept FROM Base AS X WHERE X.ContractUseState = 0 )\n" +
            "INSERT INTO dbo.TOut (X) SELECT Kept FROM Filtered;"));

        var statement = Assert.Single(statements);
        Assert.Contains("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_ScopeSourcesDoNotReachIntoDerivedTable()
    {
        // [재리뷰 R3] NamedSourceFinder가 QueryDerivedTable 하강을 막는 결정은
        // 주석이 명시적으로 정당화하는데 무방비였다. 하강하면 파생 테이블 **안**의
        // `Base`가 바깥 스코프에 결합돼, 바깥의 한정자 없는 `Flag`가 USESTATE로
        // 풀린다. 이 층의 FROM은 파생 테이블 `D` 하나뿐이다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base AS ( SELECT A.USESTATE AS Flag FROM dbo.T1 AS A )\n" +
            "DELETE FROM dbo.TSettleMst\n" +
            "WHERE ID IN (SELECT ID FROM (SELECT Flag, 1 AS ID FROM Base) AS D WHERE Flag = 9);"));

        var statement = Assert.Single(statements);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_ScopeSourcesDoNotReachIntoScalarSubquery()
    {
        // [재리뷰 R3] 같은 결정의 스칼라 하위질의 판. JOIN ON의 하위질의가 CTE를
        // 읽어도 그것은 **이 층의 원천이 아니다** - 하강하면 바깥의 한정자 없는
        // `Flag`가 그 CTE의 별칭으로 풀린다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base AS ( SELECT A.USESTATE AS Flag FROM dbo.T1 AS A )\n" +
            "DELETE FROM dbo.TSettleMst\n" +
            "WHERE ID IN (\n" +
            "  SELECT R.ID FROM dbo.TReal AS R\n" +
            "  INNER JOIN dbo.TOther AS O ON O.ID = R.ID AND O.Cnt = (SELECT COUNT(*) FROM Base)\n" +
            "  WHERE Flag = 9\n" +
            ");"));

        var statement = Assert.Single(statements);
        Assert.Contains("Flag", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
    }

    [Fact]
    public void CteProjectionAlias_ResolutionUsesOnlyThisScopesColumns()
    {
        // [재리뷰 R3 - 셋 중 가장 중요] 「어느 CTE를 읽는가」는 잠갔지만 「어느
        // 컬럼에 적용하는가」는 아무도 재지 않았다. 해석 대상을 이 스코프가 모은
        // 것에서 누적 목록으로 바꾸면 F1이 그대로 되살아난다 - 앞 스코프의 `Flag`가
        // `Base`를 읽는 뒤 스코프에서 USESTATE로 풀린다. 두 하위 스코프는 형제이고
        // 서로의 원천을 모른다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Base AS ( SELECT A.CLIENTID, A.USESTATE AS Flag FROM dbo.T1 AS A )\n" +
            "DELETE FROM dbo.TSettleMst\n" +
            "WHERE ID IN (SELECT ID FROM dbo.TZ WHERE Flag = 9)\n" +
            "  AND ID IN (SELECT CLIENTID FROM Base WHERE CLIENTID > 0);"));

        var statement = Assert.Single(statements);
        Assert.Contains("Flag", statement.SubordinatePredicateColumns);
        Assert.DoesNotContain("USESTATE", statement.SubordinatePredicateColumns);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 단계 내부 스테이징 계보 — 이행이 원본 한 문장을 「스테이징 적재」와
    // 「대상 게시」로 쪼개면 술어는 앞 문장에 남고 앵커는 뒤 문장에 붙는다
    // (docs/known-defects.md (5-3-3) 부류 3·5, 코퍼스 15건).
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lineage_PublishFromEarlierStagingWrite_InheritsItsColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO batch_shadow.S13_After\n" +
            "SELECT M.PLTID FROM SETTLE_POQ_DB.dbo.TSettleMst AS M\n" +
            "WHERE M.YMD = @pi_strYMD AND M.USESTATE = 2;\n" +
            "INSERT INTO SETTLE_POQ_DB.dbo.TSettleByTX\n" +
            "SELECT PLTID FROM batch_shadow.S13_After\n" +
            "WHERE ExecutionId = @pi_executionId;"));

        var publish = statements.Single(s => s.TargetTable == "TSettleByTX");
        var source = Assert.Single(publish.LineageSources);
        Assert.Equal("S13_After", source.SourceTable);
        Assert.Contains("YMD", source.Columns);
        Assert.Contains("USESTATE", source.Columns);

        // 적재문 자신은 계보가 없다 - 원천 TSettleMst 를 앞서 쓴 문장이 없다.
        Assert.Empty(statements.Single(s => s.TargetTable == "S13_After").LineageSources);
    }

    [Fact]
    public void Lineage_SourceNotWrittenEarlier_YieldsNoLineage()
    {
        // [불변식] 원천이 하나라도 앞서 쓰인 적 없으면 빈 목록이어야 한다.
        // 부분집합을 내면 검사 쪽 All(…)이 공허하게 참이 되어, 실물 테이블을
        // 함께 읽는 문장이 스테이징 전용으로 판정된다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.S06Cancel\n" +
            "SELECT A.PLTID FROM dbo.TTxMst AS A WHERE A.YMDCANCEL = @p;\n" +
            "INSERT INTO dbo.TSettleMst\n" +
            "SELECT S.PLTID FROM stage.S06Cancel AS S\n" +
            "INNER JOIN dbo.TReal AS R ON R.PLTID = S.PLTID;"));

        Assert.Empty(statements.Single(s => s.TargetTable == "TSettleMst").LineageSources);
    }

    [Fact]
    public void Lineage_WriteAfterRead_IsNotCounted()
    {
        // 앞선다는 것은 문서 순서다. 뒤에서 쓰는 테이블은 계보가 아니다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleMst\n" +
            "SELECT S.PLTID FROM stage.S06Cancel AS S;\n" +
            "INSERT INTO stage.S06Cancel\n" +
            "SELECT A.PLTID FROM dbo.TTxMst AS A WHERE A.YMDCANCEL = @p;"));

        Assert.Empty(statements.Single(s => s.TargetTable == "TSettleMst").LineageSources);
    }

    [Fact]
    public void Lineage_CteNameIsNotARowSource()
    {
        // CTE 이름은 테이블이 아니다. 세면 「원천이 전부 앞선 쓰기 대상」이
        // 거짓이 되어 진짜 계보를 놓친다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.S02Candidate\n" +
            "SELECT A.PGName FROM dbo.TTxMst AS A WHERE A.YMD = @p;\n" +
            ";WITH Pick AS ( SELECT PGName FROM stage.S02Candidate )\n" +
            "INSERT INTO dbo.TSettleMst SELECT PGName FROM Pick;"));

        var publish = statements.Single(s => s.TargetTable == "TSettleMst");
        Assert.DoesNotContain("Pick", publish.RowSourceTables);
        Assert.Contains("S02Candidate", publish.LineageSources.Select(l => l.SourceTable));
    }

    [Fact]
    public void Lineage_DoesNotFollowChains()
    {
        // 한 홉만. S1 → S2 → 게시 사슬에서 게시문은 S2의 컬럼만 받는다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.S1 SELECT A.PLTID FROM dbo.TTxMst AS A WHERE A.HopOne = 1;\n" +
            "INSERT INTO stage.S2 SELECT PLTID FROM stage.S1 WHERE HopTwo = 2;\n" +
            "INSERT INTO dbo.TSettleMst SELECT PLTID FROM stage.S2;"));

        var publish = statements.Single(s => s.TargetTable == "TSettleMst");
        var columns = publish.LineageSources.SelectMany(l => l.Columns).ToList();
        Assert.Contains("HopTwo", columns);
        Assert.DoesNotContain("HopOne", columns);
    }

    [Fact]
    public void Lineage_InheritsJoinAndSubordinateColumns()
    {
        // 적재문의 조인 키와 하위 범위 컬럼도 함께 물려받는다 - 부류 3의 실물
        // 둘(Proc1/S02·Proc8/S05)이 조인 키 PGName만으로 발화했다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Src AS ( SELECT A.PGName FROM dbo.TTxMst AS A WHERE A.SubCol = 1 )\n" +
            "INSERT INTO stage.S02Candidate\n" +
            "SELECT X.PGName FROM Src AS X\n" +
            "LEFT JOIN dbo.TPGProperty AS Y ON Y.PGName = X.PGName;\n" +
            "INSERT INTO dbo.TSettleMst SELECT PGName FROM stage.S02Candidate;"));

        var columns = statements.Single(s => s.TargetTable == "TSettleMst")
            .LineageSources.SelectMany(l => l.Columns).ToList();
        Assert.Contains("PGName", columns);   // 조인 키
        Assert.Contains("SubCol", columns);   // 하위 범위
    }

    [Fact]
    public void Lineage_SpansFences()
    {
        // [실물] POQSettleProc2/S13은 적재가 펜스 2, 게시가 펜스 3에 있다.
        // ReadFence 안에서 계보를 돌면 이 관용구를 통째로 놓친다.
        var markdown =
            "### S13 단계\n\n```sql\n" +
            "INSERT INTO batch_shadow.S13_After\n" +
            "SELECT M.PLTID FROM dbo.TSettleMst AS M WHERE M.YMD = @p;\n" +
            "```\n\n```sql\n" +
            "INSERT INTO dbo.TSettleByTX SELECT PLTID FROM batch_shadow.S13_After;\n" +
            "```\n";

        var statements = StepSqlStatementReader.Read(markdown);
        var columns = statements.Single(s => s.TargetTable == "TSettleByTX")
            .LineageSources.SelectMany(l => l.Columns).ToList();
        Assert.Contains("YMD", columns);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 픽스 라운드 1 - 독립 리뷰가 합성 SQL로 재현한 Critical 둘.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lineage_SelfReferencingUpdate_ExcludesOwnTargetFromRowSources()
    {
        // Critical 1. `UPDATE 대상 AS A ... FROM 대상 AS A`는 자기 자신을 다시
        // 참조하는 것이지 다른 테이블을 읽는 것이 아니다. 원천에 자기 대상이
        // 섞이면 안 된다.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.X = 1 FROM dbo.T AS A WHERE A.Y = 1;"));

        var statement = Assert.Single(statements);
        Assert.DoesNotContain("T", statement.RowSourceTables);
    }

    [Fact]
    public void Lineage_SelfReferencingUpdateOnStaging_DoesNotInheritUnrelatedPredicate()
    {
        // Critical 1 - 리뷰가 낸 정확한 재현. Foo는 명세서 대상이 아니므로
        // Task 2/3의 specTargets 필터가 이 경우를 막지 못한다. 자기 참조
        // UPDATE는 다른 상류 문장을 읽는 것이 아니라 자기가 이미 쓴 행을
        // 되읽을 뿐이므로, 앞선 INSERT의 RealFilter를 계보로 물려받으면
        // 안 된다(무관한 술어가 「이전됨」으로 조용히 붙는다).
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.Foo SELECT A.PLTID FROM dbo.TX AS A WHERE A.RealFilter = 1;\n" +
            "UPDATE X SET X.Y = 1 FROM stage.Foo AS X WHERE X.SomeCond = 1;"));

        var selfUpdate = statements.Single(s => s.Kind == "UPDATE");
        Assert.Empty(selfUpdate.LineageSources);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 최종 리뷰 Critical 1 - 자기참조 가드가 RowSourceTables에서 대상 자신을
    // 지우면서, 그 사실 자체(자기참조가 있었다는 것)를 보존할 곳이 없었다.
    // ReadsOwnTarget이 그 지워진 사실을 보존한다.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadsOwnTarget_TrueWhenSelfReferenceIsJoinedWithPreviouslyWrittenStaging()
    {
        // 대상 자신을 FROM 별칭으로 되읽으면서(자기참조), 동시에 앞서 쓰인
        // 스테이징도 JOIN하는 관용구. RowSourceTables에서는 자기참조가 지워져
        // 스테이징 하나만 남지만, ReadsOwnTarget은 그 지워진 사실을 참으로
        // 남겨야 한다 - 이 값이 없으면 MechanicalValidator.ReadsOnlyStaging이
        // "원천이 전부 스테이징"으로 오판한다(최종 리뷰 Critical 1).
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.Keys SELECT A.PLTID FROM dbo.TTxMst AS A WHERE A.YMD = @p;\n" +
            "UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A " +
            "INNER JOIN stage.Keys AS K ON K.PLTID = A.PLTID WHERE A.CLVTTYPE = 1;"));

        var update = statements.Single(s => s.Kind == "UPDATE");
        Assert.True(update.ReadsOwnTarget);
        Assert.DoesNotContain("TSettleMst", update.RowSourceTables);
        Assert.Contains("Keys", update.RowSourceTables);
    }

    [Fact]
    public void ReadsOwnTarget_FalseWhenStatementHasNoSelfReference()
    {
        // 대상이 자기 자신을 FROM 별칭으로 되읽지 않는 평범한 문장 - 대상을
        // 별칭 없이 직접 이름으로 쓰고(TSettleLog), FROM은 다른(앞서 쓰인)
        // 스테이징 테이블(Foo)만 참조한다. ReadsOwnTarget은 거짓이어야 한다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.Foo SELECT A.PLTID FROM dbo.TX AS A WHERE A.RealFilter = 1;\n" +
            "UPDATE dbo.TSettleLog SET Z = 1 FROM stage.Foo WHERE TSettleLog.SomeCond = 1;"));

        var update = statements.Single(s => s.Kind == "UPDATE");
        Assert.False(update.ReadsOwnTarget);
    }

    [Fact]
    public void Lineage_UnreachableCte_DoesNotContributeRowSourceOrLineage()
    {
        // Critical 2 - 리뷰가 낸 정확한 재현. 최상위 FROM은 Pick만 참조하고
        // Unused는 어디서도 참조되지 않는 죽은 CTE다. Unused의 본문이 앞서
        // 쓰인 Ghost를 참조해도, 이 문장이 실제로 도달하지 않으므로 원천도
        // 계보도 아니다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.Ghost SELECT G.PLTID FROM dbo.TGhostSrc AS G WHERE G.GhostFilter = 1;\n" +
            "INSERT INTO stage.S02Candidate SELECT A.PGName FROM dbo.TTxMst AS A WHERE A.YMD = @p;\n" +
            ";WITH Unused AS ( SELECT PLTID FROM stage.Ghost ), Pick AS ( SELECT PGName FROM stage.S02Candidate )\n" +
            "INSERT INTO dbo.TSettleMst SELECT PGName FROM Pick WHERE SomeRealFilter = 1;"));

        var publish = statements.Single(s => s.TargetTable == "TSettleMst");
        Assert.DoesNotContain("Ghost", publish.RowSourceTables);
        Assert.DoesNotContain("Ghost", publish.LineageSources.Select(l => l.SourceTable));
        Assert.Contains("S02Candidate", publish.LineageSources.Select(l => l.SourceTable));
    }

    [Fact]
    public void Lineage_CteChainReachability_FollowsNestedCte()
    {
        // Critical 2 수정의 부수 결정 - 도달 가능성을 너비 우선으로 따라가므로
        // CTE가 다른 CTE를 참조하는 사슬(Outer → Inner → 물리 테이블)도 끝까지
        // 따라가야 한다. 한 겹만 보는 얕은 구현이면 이 테스트가 죽는다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.S02Candidate SELECT A.PGName FROM dbo.TTxMst AS A WHERE A.YMD = @p;\n" +
            ";WITH InnerCte AS ( SELECT PGName FROM stage.S02Candidate ), OuterCte AS ( SELECT PGName FROM InnerCte )\n" +
            "INSERT INTO dbo.TSettleMst SELECT PGName FROM OuterCte;"));

        var publish = statements.Single(s => s.TargetTable == "TSettleMst");
        Assert.DoesNotContain("InnerCte", publish.RowSourceTables);
        Assert.DoesNotContain("OuterCte", publish.RowSourceTables);
        Assert.Contains("S02Candidate", publish.LineageSources.Select(l => l.SourceTable));
    }

    [Fact]
    public async Task Lineage_CyclicCte_ReadReturnsWithinBoundedTime()
    {
        // 픽스 라운드 2 재리뷰 실측 - Critical이 아니라 이 가드 자체가 실전
        // 위험임을 실측으로 확인했다. ScriptDom은 문법 전용 파서라 비재귀 CTE의
        // 순환 참조(A→B, B→A)를 실행 시점 규칙(전방 참조 금지·바인딩 오류)과
        // 무관하게 그대로 파싱한다 - 사람이 손으로 쓴, 실행 불가능할 수도 있는
        // 단계 SQL이 이 리더를 그대로 통과한다는 뜻이다. 재리뷰가 visitedCtes
        // 가드를 실제로 지우고 이 입력을 돌려 dotnet test가 CPU 100%로 20초
        // 넘게 멈추는 것을 강제 종료 전까지 직접 확인했다 - 이론이 아니라 실측.
        // 순환을 직접 도는 테스트는 무한 루프면 스위트 전체를 멈출 위험이 있어
        // 유계 타임아웃(Task.WhenAny)으로 안전하게 잠근다 - 가드가 있으면 이 호출은
        // 밀리초 안에 끝나므로 타임아웃을 넉넉히 잡아도 오탐(거짓 실패) 위험이
        // 없다.
        var sql =
            ";WITH A AS ( SELECT X FROM B ), B AS ( SELECT X FROM A )\n" +
            "INSERT INTO dbo.T SELECT X FROM A;";

        var readTask = Task.Run(() => StepSqlStatementReader.Read(Fence(sql)));
        var winner = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(winner == readTask, "순환 CTE 입력에서 Read가 10초 안에 반환하지 못했다 - visitedCtes 가드가 없으면 이렇게 무한 루프에 빠진다.");
    }

    [Fact]
    public void Lineage_CteReferencedWithDifferentCase_StillResolvesToPhysicalTable()
    {
        // Minor - 픽스 라운드 2 재리뷰. cteBodies 딕셔너리가 대소문자를 무시하지
        // 않으면, CTE를 한 대소문자로 선언하고 다른 대소문자로 참조하는 실물
        // SQL(대소문자가 늘 일관되진 않다)에서 그 CTE가 물리 테이블로 오분류된다
        // - 방향은 과소(계보를 놓침)이므로 안전하지만, 검사 B/C가 침묵하는
        // 대신 그 문장이 그냥 계보 없음으로 떨어져 원래 기대한 이전이 안 된다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.S02Candidate SELECT A.PGName FROM dbo.TTxMst AS A WHERE A.YMD = @p;\n" +
            ";WITH Pick AS ( SELECT PGName FROM stage.S02Candidate )\n" +
            "INSERT INTO dbo.TSettleMst SELECT PGName FROM PICK;"));

        var publish = statements.Single(s => s.TargetTable == "TSettleMst");
        Assert.Contains("S02Candidate", publish.LineageSources.Select(l => l.SourceTable));
    }

    // ── N5: 조인 짝 ──────────────────────────────────────────────
    // 설계: docs/superpowers/specs/2026-09-05-n5-join-pair-design.md §2-2
    // 이행 쪽은 **최상위만** 본다. 별칭은 테이블로 해석하고 두 변을 정렬해
    // 방향을 없앤다 - 명세서 별칭과 생성본 별칭이 다를 수 있기 때문이다.

    [Fact]
    public void JoinPairs_ResolveAliasesToTables_AndAreDirectionless()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.OutYMD = 1\n" +
            "  FROM dbo.TSettleMst A\n" +
            "  JOIN dbo.TSettleMst B ON A.MPLTID = B.PLTID\n" +
            "  JOIN dbo.TClientCMRate C ON A.ClientID = C.ClientID\n" +
            " WHERE A.YMD = @p;"));

        var statement = Assert.Single(statements);
        Assert.Equal(
            new[] { "TClientCMRate.ClientID=TSettleMst.ClientID", "TSettleMst.MPLTID=TSettleMst.PLTID" },
            statement.JoinPairs.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void JoinPairs_DropTheEqualityWhenAnAliasIsNotAPhysicalTable()
    {
        // 파생 테이블 별칭은 물리 테이블이 아니다 - 짝을 만들면 없는 사실이 생긴다.
        // 설계 §2-3 침묵 조건 2.
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.OutYMD = 1\n" +
            "  FROM dbo.TSettleMst A\n" +
            "  JOIN (SELECT PLTID FROM dbo.TSettleMst) D ON A.MPLTID = D.PLTID\n" +
            " WHERE A.YMD = @p;"));

        Assert.Empty(Assert.Single(statements).JoinPairs);
    }

    [Fact]
    public void JoinPairs_IgnoreEqualitiesWithinOneAlias()
    {
        // 같은 한정자 안의 비교는 조인이 아니다 - DmlScopeExtractor 의
        // HaveDifferentQualifiers 와 같은 규약(리뷰 라운드 2 가 오탐 여섯 자리로 샀다).
        var statements = StepSqlStatementReader.Read(Fence(
            "UPDATE A SET A.OutYMD = 1\n" +
            "  FROM dbo.TSettleMst A\n" +
            "  JOIN dbo.TSettleMst B ON A.YMD = A.AYMD AND A.MPLTID = B.PLTID\n" +
            " WHERE A.YMD = @p;"));

        Assert.Equal(
            new[] { "TSettleMst.MPLTID=TSettleMst.PLTID" },
            Assert.Single(statements).JoinPairs.ToArray());
    }
}
