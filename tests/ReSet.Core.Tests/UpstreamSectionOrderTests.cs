using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// 지목 재생성에서 쓰는 쪽을 먼저 만들 간선 계획. 사전 선언 docs/audit-reports/2026-09-17-지목재생성-쓰는쪽먼저-사전선언.md.
/// 실물: POQSettleBatch21 1 회차 S12(Base*/Extra* 10 이름으로 씀) · S19(S12 몫을 읽고 자기 몫도 읽음) — 로그 1790·2118 행 응답 원문.
/// </summary>
public sealed class UpstreamSectionOrderTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "control-total", name));

    private static Dictionary<string, string> Sections(params (string Code, string Markdown)[] items) =>
        items.ToDictionary(i => i.Code, i => i.Markdown, StringComparer.OrdinalIgnoreCase);

    private static string Healthy(string code) => $"### {code} 단계\n\n```sql\nSELECT 1;\n```\n";

    [Fact]
    public void Batch21_ReaderAndWriterBothPending_WriterGoesFirstAndFeedsTheReader()
    {
        var plan = UpstreamSectionOrder.For(new[] { "S12", "S19" },
            Sections(("S12", Fixture("Batch21-S12-round1.md")), ("S19", Fixture("Batch21-S19-round1.md"))));

        Assert.NotNull(plan);
        Assert.Equal(new[] { "S12" }, plan!.First);
        Assert.Equal(new[] { "S12" }, Assert.Single(plan.UpstreamOf, kv => kv.Key == "S19").Value);
    }

    // P2: 무관한 대상은 먼저 만들 무리에 안 들어간다.
    [Fact]
    public void Batch21_WithAnUnrelatedPendingStep_OnlyTheWriterGoesFirst()
    {
        var plan = UpstreamSectionOrder.For(new[] { "S03", "S12", "S19" },
            Sections(("S03", Healthy("S03")), ("S12", Fixture("Batch21-S12-round1.md")), ("S19", Fixture("Batch21-S19-round1.md"))));

        Assert.Equal(new[] { "S12" }, plan!.First);
        Assert.False(plan.UpstreamOf.ContainsKey("S03"));
    }

    // P3: 대상 하나 · 쓰는 쪽이 대상이 아님 → 계획 없음.
    [Fact]
    public void WhenTheWriterIsNotPendingOrOnlyOneStepIsPending_ThereIsNoPlan()
    {
        var sections = Sections(("S12", Fixture("Batch21-S12-round1.md")), ("S19", Fixture("Batch21-S19-round1.md")), ("S05", Healthy("S05")));

        Assert.Null(UpstreamSectionOrder.For(new[] { "S19" }, sections));
        Assert.Null(UpstreamSectionOrder.For(new[] { "S05", "S19" }, sections));
    }

    // P3: 순환(쓰는 쪽도 읽는 쪽의 몫을 읽음)이면 순서를 정하지 않는다. 실물 S12 에 S19 몫 읽기 한 문장을 더한 변이.
    [Fact]
    public void WhenTheWriterAlsoReadsTheReadersShare_ThereIsNoPlan()
    {
        var writer = Fixture("Batch21-S12-round1.md") +
                     "\n```sql\n-- SQL_READ_S19_TOTALS\nSELECT ControlValue\n  FROM batch.BatchControlTotal\n WHERE RunId = @p_runId\n   AND StepCode = N'S19'\n   AND ControlName = N'BaseRowCount';\n```\n";

        Assert.Null(UpstreamSectionOrder.For(new[] { "S12", "S19" }, Sections(("S12", writer), ("S19", Fixture("Batch21-S19-round1.md")))));
    }

    // P6 n5: 실물 S19 는 자기 몫(StepCode = N'S19')도 읽는다 - 자기 코드를 빼지 않으면 S19 가 쓰는 쪽이자 읽는 쪽이 되어 순환으로 보인다.
    [Fact]
    public void Batch21_ReaderAlsoReadsItsOwnShare_IsNotACycle()
    {
        Assert.Contains("StepCode = N'S19'", Fixture("Batch21-S19-round1.md"));

        Assert.NotNull(UpstreamSectionOrder.For(new[] { "S12", "S19" },
            Sections(("S12", Fixture("Batch21-S12-round1.md")), ("S19", Fixture("Batch21-S19-round1.md")))));
    }
}
