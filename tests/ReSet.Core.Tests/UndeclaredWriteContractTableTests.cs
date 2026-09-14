using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [T36 · 제어 계약 표 면제] 코퍼스 로그의 발화 26 · 배송본 스윕 17 이 전부 제어 계약 표였다 — DDL·컬럼이 계약으로 고정돼 있고, 목차 대상 표가
/// 권한·DDL 을 끌고 가는 코드는 없다. 계약 표는 면제하고 계약 밖 표만 든다.
/// 판독: docs/audit-reports/2026-09-14-목차밖-계약표-면제-사전선언.md
/// </summary>
public sealed class UndeclaredWriteContractTableTests
{
    private const string Marker = "목차에 없습니다";

    private static readonly BatchStepPlan B16S03 = new(
        "S03", "배치 실행 등록", Array.Empty<string>(), new[] { "batch.BatchRun", "batch.BatchStepJournal" },
        new[] { "-9030" }, false, Array.Empty<string>());

    private static string Section() => InstructionBundleWriter.StripFloorBanner(File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "t36", "Batch16-S03.md")));

    private static List<string> UndeclaredErrors(string markdown, BatchStepPlan step) =>
        new MechanicalValidator()
            .ValidateBatchStep(markdown, step, Array.Empty<string>(), new Dictionary<string, SpecConditions>())
            .Errors.Where(e => e.Contains(Marker)).ToList();

    // U1: 계약 표(BatchRunLock)를 목차가 선언하지 않았어도 쓸 수 있다.
    [Fact]
    public void AContractTableTheOutlineDoesNotDeclare_IsNotReported()
    {
        var section = Section();
        Assert.Contains("batch.BatchRunLock", section);

        Assert.Empty(UndeclaredErrors(section, B16S03));
    }

    // U2: 계약 밖 표는 그대로 든다 - 그 표의 DDL 은 계약에 없다.
    [Fact]
    public void ATableOutsideTheContract_IsStillReportedWithTheContractReason()
    {
        var section = Section().Replace("batch.BatchRunLock", "batch.BatchRunLease", StringComparison.Ordinal);

        var errors = UndeclaredErrors(section, B16S03);

        Assert.Single(errors);
        Assert.Contains("BatchRunLease", errors[0]);
        Assert.Contains("제어 계약에 없으므로", errors[0]);
        Assert.DoesNotContain("권한", errors[0]);
    }

    // U3: 계약이 아는 별칭(ControlTotal)은 정본이 아니다 - 면제하지 않는다.
    [Fact]
    public void AContractAliasIsNotExempt()
    {
        var step = B16S03 with { TargetTables = new[] { "batch.BatchRun" } };
        var markdown = "### S03 배치 실행 등록\n\n```sql\nINSERT INTO batch.ControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc) VALUES (@p_runId, N'S03', N'Rows', 0, SYSUTCDATETIME());\n```\n";

        Assert.Single(UndeclaredErrors(markdown, step));
    }
}
