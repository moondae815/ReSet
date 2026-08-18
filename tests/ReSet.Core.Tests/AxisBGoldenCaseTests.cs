using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// POQSettleProc16 정합성 감사(2026-08-17)가 실측한 축 B 결함을 코퍼스로 고정한다.
///
/// 단위 테스트가 검사 하나하나의 동작을 보는 것과 달리, 여기서는 실제 산출물에서
/// 오려낸 본문을 넣어 검사가 그 결함을 실제로 잡는지 본다. 검사가 통과하도록
/// 규칙을 느슨하게 만드는 회귀를 막는 것이 목적이다.
/// </summary>
public sealed class AxisBGoldenCaseTests
{
    private static readonly IReadOnlyDictionary<string, SpecConditions> NoConditions =
        new Dictionary<string, SpecConditions>();

    private static readonly string[] Catalog =
    {
        "dbo.TSettleMst", "dbo.TSettleByTX", "dbo.TClientSettleRate4Extra"
    };

    private static BatchStepPlan Step(string code) => new(
        Code: code,
        Name: $"{code} 단계",
        LegacyProcedures: Array.Empty<string>(),
        TargetTables: Array.Empty<string>(),
        ErrorCodes: Array.Empty<string>(),
        Chunkable: false,
        SchemaTables: Array.Empty<string>());

    private static string Section(string code, string sql) => $"""
        ### {code} 단계

        ```sql
        {sql}
        ```
        """;

    private static StepValidationResult Validate(
        string code, string sql, IReadOnlyList<StepInterface>? interfaces = null) =>
        new MechanicalValidator().ValidateBatchStep(
            Section(code, sql), Step(code), Catalog, NoConditions, interfaces);

    // 감사 S10 🟠 — 보호 검사를 우회 플래그 안에 넣었다.
    [Fact]
    public void S10_ConditionalGuardOnABypassParameter()
    {
        var result = Validate(
            "S10",
            "CREATE PROCEDURE batch.usp_S10 @pi_strYMD varchar(8), @pi_bypassPreCheck bit = 0 AS\n" +
            "IF @pi_bypassPreCheck = 0 AND EXISTS (SELECT 1 FROM dbo.TSettleMst WHERE OutState IN (1,5))\n" +
            "    RETURN -9;",
            new[] { new StepInterface("S10", new[] { "dbo.UP_UTIL_SETTLE_INS_EXTRA" },
                new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" }) });

        Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
    }

    // 감사 S03 🟡 — S03만 저널 성공 상태를 'Completed'로 썼다.
    [Fact]
    public void S03_JournalSuccessWrittenAsCompleted()
    {
        var result = Validate(
            "S03",
            "INSERT INTO batch.BatchStepJournal (RunId, StepCode, StepStatus, StartedAtUtc)\n" +
            "VALUES (@RunId, N'S03', N'Running', SYSUTCDATETIME());\n" +
            "UPDATE batch.BatchStepJournal SET StepStatus = N'Completed' WHERE StepCode = N'S03';");

        Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
    }

    // 감사 S03 🟠 — 저널 행을 만드는 지점 없이 UPDATE만 한다.
    [Fact]
    public void S03_UpdatesAJournalRowItNeverInserts()
    {
        var result = Validate(
            "S03",
            "UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded' WHERE StepCode = N'S03';");

        Assert.Contains(result.Errors, e => e.Contains("INSERT"));
    }

    // 감사 S04 🔴 — 트랜잭션 안에서 만든 그림자가 롤백과 함께 소멸한다.
    [Fact]
    public void S04_ShadowCreatedInsideTheTransaction()
    {
        var result = Validate(
            "S04",
            "BEGIN TRAN;\n" +
            "SELECT * INTO batch_shadow.TClientSettleRate4Extra_RunId_S04\n" +
            "FROM dbo.TClientSettleRate4Extra WHERE YMD = @pi_strYMD;\n" +
            "DELETE FROM dbo.TClientSettleRate4Extra WHERE YMD = @pi_strYMD;\n" +
            "COMMIT TRAN;");

        Assert.Contains(result.Errors, e => e.Contains("BEGIN TRAN"));
    }

    // 감사 S11 🟠 — EXEC() 동적 배치가 바깥 변수를 참조한다.
    [Fact]
    public void S11_OuterVariableInsideExec()
    {
        var result = Validate(
            "S11",
            "EXEC(N'INSERT INTO ' + @v_shadowTableName + " +
            "N' SELECT A.* FROM dbo.TSettleMst A WHERE A.ProcYMD = @pi_strYMD');");

        Assert.Contains(result.Errors, e => e.Contains("sp_executesql"));
    }

    // 감사 B7 — CATCH가 반환 경로 없이 THROW로 끝난다.
    [Fact]
    public void B7_CatchOnlyRethrows()
    {
        var result = Validate(
            "S07",
            "BEGIN CATCH\n    IF @@TRANCOUNT > 0 ROLLBACK TRAN;\n    THROW;\nEND CATCH");

        Assert.Contains(result.Errors, e => e.Contains("THROW"));
    }

    // 감사 S16 🔴 — 카티전 곱으로 두 집계를 비교한다.
    [Fact]
    public void S16_CartesianAggregateComparison()
    {
        var markdown = $"""
            ## 통합 배치 아키텍처 개요

            내용.

            ## Mermaid 기반 통합 흐름도

            ```mermaid
            flowchart TD
            A["시작"] --> B["끝"]
            ```

            ## 단계별 이행 상세 및 의사코드

            내용.

            ## 통합 데이터 정합성 검증 SQL 세트

            ```sql
            SELECT 1
            FROM dbo.TSettleMst AS M
            CROSS JOIN dbo.TSettleByTX AS T
            HAVING ISNULL(SUM(M.TXAMT),0) <> ISNULL(SUM(T.TXAMT),0);
            ```
            """;

        var result = new MechanicalValidator().ValidateConsolidated(markdown);

        Assert.Contains(result.DetailedErrors,
            e => e.Type == ErrorType.VerificationCartesianComparison);
    }
}
