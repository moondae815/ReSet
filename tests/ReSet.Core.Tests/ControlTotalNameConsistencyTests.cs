using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [K2] 읽는 단계가 <c>StepCode</c> 로 특정 단계 몫을 걸러 읽는 통제명이, 그 단계가 실제로 쓰는 통제명과
/// <b>하나도</b> 겹치지 않는가.
///
/// POQSettleBatch11(2026-09-13): S13 은 <c>N'LedgerRowCount'</c>·<c>N'TxAmt'</c>… 로 쓰고 S20 은
/// <c>StepCode IN (N'S13', N'S20') AND ControlName IN (N'LEDGER_ROW_COUNT', …)</c> 로 읽어, S20 의 대조가
/// 어떤 실행에서도 S13 몫을 못 찾았다. 표 이름(K1)을 고쳐도 이 결함은 남는다. Critic 만 잡았다.
/// 픽스처는 배송본 그대로다(<c>Fixtures/control-total/README.md</c>).
/// </summary>
public sealed class ControlTotalNameConsistencyTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "control-total", name));

    private static BatchStepPlan ControlStep(string code) => new(
        Code: code, Name: $"{code} 단계",
        LegacyProcedures: Array.Empty<string>(),
        TargetTables: new[] { "batch.BatchControlTotal" },
        ErrorCodes: new[] { "-9200" }, Chunkable: false, SchemaTables: Array.Empty<string>());

    private static IReadOnlyDictionary<string, StepDefect> Validate(params (string Code, string Markdown)[] sections) =>
        new MechanicalValidator().ValidateControlTotalNameConsistency(
            sections.ToDictionary(s => s.Code, s => s.Markdown, StringComparer.OrdinalIgnoreCase),
            sections.Select(s => ControlStep(s.Code)).ToList());

    // S20 이 읽는 다섯 이름 → S13 이 쓰는 같은 뜻의 이름.
    private static readonly (string Reader, string Writer)[] NameFixes =
    {
        ("LEDGER_ROW_COUNT", "LedgerRowCount"),
        ("LEDGER_TX_AMT", "TxAmt"),
        ("LEDGER_CL_TOTAL", "CLTotal"),
        ("LEDGER_PG_TOTAL", "PGTotal"),
        ("LEDGER_POQ_INCOME", "POQIncome"),
    };

    [Fact]
    public void RealReaderWhoseNamesNeverMatchTheWritersNames_IsAttributedToTheReader()
    {
        var defects = Validate(("S13", Fixture("Batch11-S13.md")), ("S20", Fixture("Batch11-S20.md")));

        var defect = Assert.Single(defects).Value;
        Assert.Equal(StepDefectKind.QualityFloor, defect.Kind);
        Assert.StartsWith("S20 (", defect.Reason);
        Assert.Contains("S13", defect.Reason);
        Assert.Contains("LEDGER_ROW_COUNT", defect.Reason);
        Assert.Contains("LedgerRowCount", defect.Reason);
    }

    // 양성 대조 짝: 같은 S20 본문에서 읽는 이름만 S13 이름으로 바꾸면 조용해진다. 위 발화가
    // 「S20 을 읽을 수 있고, 이름이 안 겹쳐서」 난 것임을 보인다. S20 은 자기 몫을 LEDGER_* 로 쓰므로
    // 쓰기 쪽 이름도 함께 바꾼다(자기 몫 대조 S20↔S20 은 원래도 겹친다).
    [Fact]
    public void SameReaderAfterAligningItsNamesWithTheWriter_IsSilent()
    {
        var reader = Fixture("Batch11-S20.md");
        foreach (var (from, to) in NameFixes) reader = reader.Replace("N'" + from + "'", "N'" + to + "'");
        Assert.DoesNotContain("N'LEDGER_", reader);

        var defects = Validate(("S13", Fixture("Batch11-S13.md")), ("S20", reader));

        Assert.Empty(defects);
    }

    // 자기가 쓴 이름을 자기가 읽는다 - 실물 Batch4/S16.
    [Fact]
    public void RealStepReadingItsOwnWrites_IsSilent()
    {
        Assert.Empty(Validate(("S16", Fixture("Batch4-S16.md"))));
    }

    // 이름을 매개변수(@p_controlName)로 쓰는 단계는 무엇을 쓰는지 모른다 - 귀속할 수 없으면 침묵한다(작성 계약 7).
    // 실물 Batch10/S17 을 S13 자리에 두고, S20 이 그 몫을 읽게 한다.
    [Fact]
    public void ReaderOfAStepThatWritesNamesThroughAParameter_IsSilent()
    {
        var defects = Validate(("S13", Fixture("Batch10-S17.md")), ("S20", Fixture("Batch11-S20.md")));

        Assert.Empty(defects);
    }

    [Fact]
    public void RealWriterWithNoReader_IsSilent()
    {
        Assert.Empty(Validate(("S22", Fixture("Batch8-S22.md"))));
    }

    // StepCode 리터럴이 문장 어디에도 없으면 누구 몫을 읽는지 모른다 - 침묵한다.
    // (IN 필터만 지우면 같은 문장의 CASE WHEN StepCode = N'S13' 이 여전히 몫을 가리켜 발화하는 것이 맞다.)
    [Fact]
    public void ReaderWithoutAnyStepCodeLiteral_IsSilent()
    {
        var reader = Fixture("Batch11-S20.md")
            .Replace("AND StepCode IN (N'S13', N'S20')", "")
            .Replace("CASE WHEN StepCode = N'S13' THEN", "CASE WHEN 1 = 1 THEN")
            .Replace("CASE WHEN StepCode = N'S20' THEN", "CASE WHEN 1 = 1 THEN");
        Assert.DoesNotContain("StepCode = N'S13' THEN", reader);

        Assert.Empty(Validate(("S13", Fixture("Batch11-S13.md")), ("S20", reader)));
    }

    // 주석 속 이름은 코드가 아니다 - 읽기 필터로 세지 않는다. 짝으로 잰다: 코드의 ControlName 필터를 지우고
    // 옛 이름을 주석에만 남기면 침묵, 같은 줄에서 주석 표지만 떼면(코드가 되면) 발화한다.
    // 한쪽만 두면 주석 제거를 걷어내도 초록이 남는다(처음 쓴 시험이 그랬다 - 코드 이름을 맞춘 뒤 주석을 더해
    // 어느 쪽이든 교집합이 생겼다).
    [Theory]
    [InlineData("AND 1 = 1 -- AND ControlName IN (N'LEDGER_ROW_COUNT')", false)]
    [InlineData("AND 1 = 1    AND ControlName IN (N'LEDGER_ROW_COUNT')", true)]
    public void ANameFilterCountsOnlyWhenItIsCode(string replacement, bool expectDefect)
    {
        var original = Fixture("Batch11-S20.md");
        var reader = Regex.Replace(original, @"AND ControlName IN\s*\((?:\s*N'LEDGER_[A-Z_]+',?)+\s*\)", replacement);
        Assert.NotEqual(original, reader);

        var defects = Validate(("S13", Fixture("Batch11-S13.md")), ("S20", reader));

        Assert.Equal(expectDefect, defects.ContainsKey("S20"));
    }

    // [배선] 발화를 재는 시험은 이 검사가 파이프라인에서 아예 안 불려도 초록이다. 오케스트레이터가 결과를
    // floorViolations 에 합치는 자리를 소스에서 잠근다(T25 ValidateControlStatusTerminalWrites 와 같은 모양).
    [Fact]
    public void OrchestratorMergesTheDefectsIntoFloorViolations()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));

        Assert.Matches(new Regex(
            @"foreach\s*\(\s*var\s*\(\s*code\s*,\s*defect\s*\)\s*in\s*_validator\.ValidateControlTotalNameConsistency\(\s*sections\s*,\s*steps\s*\)\s*\)\s*\{\s*floorViolations\[code\]"),
            source);
    }
}
