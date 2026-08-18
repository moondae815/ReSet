using System;
using System.Linq;
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
}
