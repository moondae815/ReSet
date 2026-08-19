using System;
using System.Linq;
using System.Text.RegularExpressions;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class BatchControlContractTests
{
    [Fact]
    public void Tables_CoverTheFourControlTables()
    {
        var names = BatchControlContract.Tables.Select(t => t.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "batch.BatchRun",
                "batch.BatchStepJournal",
                "batch.BatchCheckpoint",
                "batch.BatchValidationIssue"
            },
            names);
    }

    // 감사 실측: 같은 저널을 S01은 StepStatus='Succeeded', S02는
    // ExecutionStatus='Completed', S03은 StepStatus='Completed'로 썼다.
    // 성공 종료 어휘가 하나가 아니면 모든 재시작이 차단된다.
    [Fact]
    public void SuccessVocabulary_IsSucceededEverywhere_AndNeverCompleted()
    {
        foreach (var table in BatchControlContract.Tables)
        {
            if (table.StatusColumn == null) continue;

            var status = table.Columns.Single(c => c.Name == table.StatusColumn);
            Assert.NotNull(status.AllowedValues);
            Assert.DoesNotContain("Completed", status.AllowedValues!);
        }

        var journal = BatchControlContract.Find("batch.BatchStepJournal")!;
        var journalStatus = journal.Columns.Single(c => c.Name == journal.StatusColumn);
        Assert.Contains("Succeeded", journalStatus.AllowedValues!);
    }

    [Fact]
    public void StatusColumnName_FollowsTheTargetStatusRule()
    {
        Assert.Equal("RunStatus", BatchControlContract.Find("batch.BatchRun")!.StatusColumn);
        Assert.Equal("StepStatus", BatchControlContract.Find("batch.BatchStepJournal")!.StatusColumn);
        Assert.Equal("CheckpointStatus", BatchControlContract.Find("batch.BatchCheckpoint")!.StatusColumn);
    }

    // 감사 실측 B3: INSERT INTO batch.BatchRun이 번들 전체에 0건이었다.
    // 행 소유권이 계약에 없으면 모든 단계가 UPDATE만 쓴다.
    [Fact]
    public void RowOrigin_IsDeclaredForEveryTable()
    {
        Assert.Equal(ControlRowOrigin.FirstStepInserts, BatchControlContract.Find("batch.BatchRun")!.Origin);
        Assert.Equal(ControlRowOrigin.EachStepInserts, BatchControlContract.Find("batch.BatchStepJournal")!.Origin);
        Assert.Equal(ControlRowOrigin.EachStepInserts, BatchControlContract.Find("batch.BatchCheckpoint")!.Origin);
        Assert.Equal(ControlRowOrigin.ProducerInsertsOnly, BatchControlContract.Find("batch.BatchValidationIssue")!.Origin);
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndAcceptsTheBareName()
    {
        Assert.NotNull(BatchControlContract.Find("BATCH.BATCHRUN"));
        Assert.NotNull(BatchControlContract.Find("BatchRun"));
        Assert.Null(BatchControlContract.Find("dbo.TSettleMst"));
    }

    // 부트스트랩 회차 문서가 실을 DDL. 감사 §6-4: 다섯 테이블의 컬럼
    // 정의가 번들 어디에도 없었다.
    [Fact]
    public void RenderDdl_EmitsCreateTableForEveryTable_WithAConstraintOnTheStatusVocabulary()
    {
        var ddl = BatchControlContract.RenderDdl();

        Assert.Contains("CREATE TABLE batch.BatchRun", ddl);
        Assert.Contains("CREATE TABLE batch.BatchStepJournal", ddl);
        Assert.Contains("CREATE TABLE batch.BatchCheckpoint", ddl);
        Assert.Contains("CREATE TABLE batch.BatchValidationIssue", ddl);
        Assert.Contains("CHECK (StepStatus IN (N'Running', N'Succeeded', N'Failed', N'Skipped'))", ddl);
    }

    [Fact]
    public void RenderPromptTable_NamesEveryColumnAndTheRowOrigin()
    {
        var table = BatchControlContract.RenderPromptTable();

        Assert.Contains("StartedAtUtc", table);
        Assert.Contains("ErrorMessage", table);
        Assert.Contains("JobName", table);
        // 행 생성 소유권이 프롬프트에 실려야 B3가 닫힌다.
        Assert.Contains("INSERT", table);
    }

    // 최종 리뷰: 프롬프트 표는 "첫 단계가 INSERT하며 RunId를 발급한다"고 말하는데
    // DDL에 발급 수단이 없었다. 18번의 독립 호출이 각자 방식을 지어내는 실패 모드가
    // 이 축에서 재현될 수 있다.
    [Fact]
    public void RenderDdl_IssuesRunIdWithIdentityOnTheRunTableOnly()
    {
        var ddl = BatchControlContract.RenderDdl();

        Assert.Contains("RunId bigint IDENTITY(1,1) NOT NULL", ddl);
        // 저널·체크포인트의 RunId는 발급받아 쓰는 자리다. 거기까지 IDENTITY면
        // 각 테이블이 자기 번호를 새로 매겨 실행 단위가 갈라진다.
        // (xUnit2013 회피: .Count를 Assert.Equal 인자에서 바로 쓰지 않는다 - 경고 기준선 9개를 넘긴다.)
        var identityCount = Regex.Matches(ddl, @"IDENTITY\(1,1\)").Count;
        Assert.Equal(1, identityCount);
    }

    [Fact]
    public void RenderDdl_DeclaresAPrimaryKeyForEveryTableThatHasATransition()
    {
        var ddl = BatchControlContract.RenderDdl();

        Assert.Contains("CONSTRAINT PK_BatchRun PRIMARY KEY (RunId)", ddl);
        Assert.Contains("CONSTRAINT PK_BatchStepJournal PRIMARY KEY (RunId, StepCode)", ddl);
        Assert.Contains("CONSTRAINT PK_BatchCheckpoint PRIMARY KEY (RunId, StepCode)", ddl);
    }

    // 전이가 없는 테이블에는 키를 두지 않는다. 한 단계가 같은 IssueCode를 여러 번
    // 낼 수 있어 자연 키가 없고, 대리 키를 넣으면 단계가 써야 할 컬럼이 늘어난다.
    [Fact]
    public void RenderDdl_DoesNotDeclareAPrimaryKeyForTheInsertOnlyTable()
    {
        Assert.DoesNotContain("PK_BatchValidationIssue", BatchControlContract.RenderDdl());
    }

    // 프롬프트 표도 발급 수단을 말해야 한다 - DDL에만 있으면 단계 문서를 쓰는
    // 모델이 그 사실을 못 본다.
    [Fact]
    public void RenderPromptTable_SaysHowRunIdIsIssued()
    {
        Assert.Contains("IDENTITY", BatchControlContract.RenderPromptTable());
    }

    // 계약이 담당을 위치("목록의 첫 단계")로 지목하면 첫 단계를 비변경 사전검증으로
    // 두는 흔한 목차 설계와 충돌한다 - 실측에서 그 충돌로 세 회차가 반려됐다.
    // 프롬프트 표의 문구와 ResolveRowCreators의 판정이 같은 규칙을 말해야 한다.
    [Fact]
    public void RenderPromptTable_AssignsTheRunRowToTheFirstStepThatTargetsIt()
    {
        var table = BatchControlContract.RenderPromptTable();

        Assert.Contains("FIRST step that lists this table as a target", table);
        Assert.DoesNotContain("FIRST step in the step list", table);
    }

    // === 실행 행 생성 담당 단계 판정 =========================================
    //
    // 실측(POQSettleProc17): 계약이 "목록의 첫 단계"라고 위치로 지목했는데 승인된
    // 첫 단계 S01은 대상 테이블이 없는 비변경 사전검증이었다. S01은 자기 정의를
    // 따라 INSERT를 쓰지 않았고, batch.BatchRun을 실제로 가진 S02는 계약을 믿고
    // UPDATE만 했다. 아무도 행을 만들지 않은 계획서가 세 번 연속으로 같은 자리에서
    // 반려됐다. 담당을 위치가 아니라 대상 보유로 정하면 두 지시가 어긋나지 않는다.

    private static BatchStepPlan Plan(string code, params string[] targetTables) => new(
        Code: code,
        Name: code + " 단계",
        LegacyProcedures: Array.Empty<string>(),
        TargetTables: targetTables,
        ErrorCodes: Array.Empty<string>(),
        Chunkable: false,
        SchemaTables: Array.Empty<string>());

    [Fact]
    public void ResolveRowCreators_NamesTheFirstStepThatTargetsTheTable()
    {
        var steps = new[]
        {
            Plan("S01"),
            Plan("S02", "batch.BatchRun", "batch.BatchRunLock"),
            Plan("S17", "batch.BatchRun")
        };

        var creators = BatchControlContract.ResolveRowCreators(steps);

        Assert.Equal(new[] { "batch.BatchRun" }, creators["S02"]);
    }

    // 뒤 단계가 같은 테이블을 대상으로 가져도 그것은 UPDATE 지점이다. 담당이
    // 둘이면 재생성 프롬프트가 두 자리에 INSERT를 요구해 실행 단위가 갈라진다.
    [Fact]
    public void ResolveRowCreators_DoesNotNameALaterStepThatAlsoTargetsTheTable()
    {
        var steps = new[]
        {
            Plan("S02", "batch.BatchRun"),
            Plan("S17", "batch.BatchRun")
        };

        var creators = BatchControlContract.ResolveRowCreators(steps);

        Assert.False(creators.ContainsKey("S17"));
    }

    // 아무 단계도 이 테이블을 대상으로 갖지 않으면 담당이 없다 - 계약을 쓰지 않는
    // Job일 수 있으므로 아무 단계에도 의무를 지우지 않는다. 통합 검사가 백스톱이다.
    [Fact]
    public void ResolveRowCreators_NamesNoStepWhenNoneTargetsTheTable()
    {
        var steps = new[]
        {
            Plan("S01"),
            Plan("S02", "SETTLE_POQ_DB.dbo.TBatchRun")
        };

        Assert.Empty(BatchControlContract.ResolveRowCreators(steps));
    }

    // 각 단계가 자기 행을 만드는 테이블은 단계 검사가 이미 전 단계에 요구한다.
    // 여기서 또 담당을 지목하면 같은 결함이 두 경로로 두 번 보고된다.
    [Fact]
    public void ResolveRowCreators_IgnoresTablesEachStepInsertsForItself()
    {
        var steps = new[] { Plan("S02", "batch.BatchStepJournal", "batch.BatchCheckpoint") };

        Assert.Empty(BatchControlContract.ResolveRowCreators(steps));
    }
}
