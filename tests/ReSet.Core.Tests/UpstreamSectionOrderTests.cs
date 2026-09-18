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

    // [최종 리뷰 Important] 사슬(S01 이 S02 몫을 읽고 S02 가 S03 몫을 읽음)도 순환과 같이 계획을 버린다 - 쓰는 쪽이 다른 대상의 몫을
    // 읽으면 한 무리 순서로는 정할 수 없어 종전대로 둔다(덜 바꾼다). 지목 재생성 19 회차에 사슬 도달 0(사전 선언 §0-3 측정의 재료).
    // 이 시험은 사슬을 지원하도록 바꾸는 변경이 선언 없이 들어오면 빨개진다.
    [Fact]
    public void AChainOfReadersIsTreatedLikeACycle_ThereIsNoPlan()
    {
        static string Reads(string code, string owner) =>
            $"### {code} 단계\n\n```sql\n-- SQL_READ_{owner}\nSELECT ControlValue\n  FROM batch.BatchControlTotal\n WHERE RunId = @p_runId\n   AND StepCode = N'{owner}'\n   AND ControlName = N'LedgerRowCount';\n```\n";
        var writer = "### S03 단계\n\n```sql\n-- SQL_CAPTURE\nINSERT INTO batch.BatchControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)\nVALUES (@p_runId, N'S03', N'LedgerRowCount', 0, SYSUTCDATETIME());\n```\n";

        Assert.Null(UpstreamSectionOrder.For(new[] { "S01", "S02", "S03" },
            Sections(("S01", Reads("S01", "S02")), ("S02", Reads("S02", "S03")), ("S03", writer))));
        // 짝: 사슬의 가운데(S02)를 빼면 S01 → S02 간선도 사라지고, S02 → S03 만 남는 두 대상은 계획이 선다.
        Assert.NotNull(UpstreamSectionOrder.For(new[] { "S02", "S03" },
            Sections(("S02", Reads("S02", "S03")), ("S03", writer))));
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
