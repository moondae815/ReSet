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
        ValidateWith(null, sections);

    private static IReadOnlyDictionary<string, StepDefect> ValidateWith(string? conventions, params (string Code, string Markdown)[] sections) =>
        new MechanicalValidator().ValidateControlTotalNameConsistency(
            sections.ToDictionary(s => s.Code, s => s.Markdown, StringComparer.OrdinalIgnoreCase),
            sections.Select(s => ControlStep(s.Code)).ToList(),
            conventions);

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
        // 재생성 프롬프트로 가는 문구다 - 짝이 될 쓰기 이름이 잘려 나가면 모델이 맞출 수 없다.
        foreach (var (_, writer) in NameFixes) Assert.Contains("`" + writer + "`", defect.Reason);
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

    // 실물 Batch4/S16 은 자기 몫을 읽지만 StepCode 리터럴로 거르지 않는다 - 규칙 ③(몫을 모르면 침묵)으로 조용하다.
    // 「교집합이 있으면 침묵」(규칙 ⑤)은 이 시험이 아니라 이름을 맞춘 짝 시험이 잠근다(2026-09-13 최종 리뷰 M2 정정).
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

    // 문장 안에 리터럴 이름이 보여도 ControlName 칸에 들어가는 값이 매개변수면 실제로 무엇이 쓰이는지 모른다 - 침묵한다.
    // 위 시험만으로는 이 가지가 안 잠긴다: Batch10/S17 은 리터럴이 0 이라 「쓰는 이름 0」 조건이 먼저 침묵시킨다
    // (되돌림으로 확인 - 첫 구현에서 매개변수 판정을 걷어내도 위 시험은 초록이었다).
    [Fact]
    public void ReaderOfAWriterMixingLiteralNamesWithANameParameter_IsSilent()
    {
        var original = Fixture("Batch11-S13.md");
        var writer = original.Replace("    C.ControlName,\n    C.ControlValue,", "    @p_controlName,\n    C.ControlValue,");
        Assert.NotEqual(original, writer);

        Assert.Empty(Validate(("S13", writer), ("S20", Fixture("Batch11-S20.md"))));
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

    // [리뷰 I2] 이름 칸 값이 임시 표에서 오면(같은 문장의 인라인 VALUES 가 아니다) 무엇을 쓰는지 모른다 - 문장에 엉뚱한
    // 리터럴(N'Ledger')이 섞여도 침묵해야 한다. 첫 구현은 그 리터럴을 쓰기 이름으로 세어 옳은 읽는 단계를 고발했다.
    // 같은 S13 의 CTE(VALUES 를 담은 ControlValues)는 문장에 그대로 남겨, 「문장 어딘가의 VALUES」로 해석하면 안 됨을 함께 잰다.
    [Fact]
    public void WriterWhoseNameColumnComesFromATemporaryTable_IsUnknownEvenWithAStrayLiteral()
    {
        var original = Fixture("Batch11-S13.md");
        var writer = original.Replace("FROM ControlValues AS C\nWHERE NOT EXISTS", "FROM #LedgerNames AS C\nWHERE C.Kind = N'Ledger' AND NOT EXISTS");
        Assert.NotEqual(original, writer);

        // 짝: 원본 S13 이면 같은 S20 에 발화한다(첫 시험).
        Assert.Empty(Validate(("S13", writer), ("S20", Fixture("Batch11-S20.md"))));
    }

    // [리뷰 I3] 같은 문장의 다른 표(단계 저널) StepCode 조건은 몫이 아니다. 실물 Batch8/S22(리터럴 이름으로 쓴다)를 S22 로 두고,
    // 이름을 맞춘 S20 이 저널을 조인하며 j.StepCode = N'S22' 로 거르게 한다 - 그 조건을 몫으로 세면 이름이 안 겹쳐 발화한다.
    [Fact]
    public void StepCodeConditionOnAnotherTableInTheSameQuery_IsNotAnOwner()
    {
        var reader = Fixture("Batch11-S20.md");
        foreach (var (from, to) in NameFixes) reader = reader.Replace("N'" + from + "'", "N'" + to + "'");
        var joined = reader.Replace(
            "      FROM batch.ControlTotal\n     WHERE RunId = @p_runId\n",
            "      FROM batch.ControlTotal\n      JOIN batch.BatchStepJournal AS j ON j.RunId = @p_runId\n     WHERE RunId = @p_runId\n       AND j.StepCode = N'S22'\n");
        Assert.NotEqual(reader, joined);

        Assert.Empty(Validate(("S13", Fixture("Batch11-S13.md")), ("S20", joined), ("S22", Fixture("Batch8-S22.md"))));
    }

    // [리뷰 I4] 공통 규약(실물 Batch11 골격)이 읽는 쪽 이름(LedgerRowCount)을 담고 쓰는 쪽 이름을 하나도 안 담으면 어긴 것은
    // 쓰는 단계다 - 결함을 쓰는 단계에 귀속한다. 실물의 방향을 뒤집어 만든다: S13 의 다섯 이름을 LEDGER_* 로, S20 은 S13 원래 이름으로.
    [Fact]
    public void WhenOnlyTheReaderFollowsTheSharedConventions_TheWriterIsAttributed()
    {
        var writer = Fixture("Batch11-S13.md");
        foreach (var (from, to) in NameFixes) writer = writer.Replace("N'" + to + "'", "N'" + from + "'");
        var reader = Fixture("Batch11-S20.md");
        foreach (var (from, to) in NameFixes) reader = reader.Replace("N'" + from + "'", "N'" + to + "'");
        var conventions = Fixture("Batch11-skeleton.md");
        Assert.Contains("'LedgerRowCount'", conventions);

        var withConventions = ValidateWith(conventions, ("S13", writer), ("S20", reader));
        var defect = Assert.Single(withConventions);
        Assert.Equal("S13", defect.Key);
        Assert.Contains("공통 규약", defect.Value.Reason);

        // 규약을 모르면 종전대로 읽는 단계다.
        Assert.Equal("S20", Assert.Single(ValidateWith(null, ("S13", writer), ("S20", reader))).Key);
    }

    // 실물 방향(읽는 쪽 LEDGER_* 가 규약에 없다)은 규약을 주어도 읽는 단계 그대로다.
    [Fact]
    public void RealCaseWithSharedConventions_StillAttributesTheReader()
    {
        var defects = ValidateWith(Fixture("Batch11-skeleton.md"),
            ("S13", Fixture("Batch11-S13.md")), ("S20", Fixture("Batch11-S20.md")));

        Assert.Equal("S20", Assert.Single(defects).Key);
    }

    // [배선] 발화를 재는 시험은 이 검사가 파이프라인에서 아예 안 불려도 초록이다. 오케스트레이터가 결과를
    // floorViolations 에 합치는 자리를 소스에서 잠근다(T25 ValidateControlStatusTerminalWrites 와 같은 모양).
    [Fact]
    public void OrchestratorMergesTheDefectsIntoFloorViolations()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));

        Assert.Matches(new Regex(
            @"foreach\s*\(\s*var\s*\(\s*code\s*,\s*defect\s*\)\s*in\s*_validator\.ValidateControlTotalNameConsistency\(\s*sections\s*,\s*steps\s*,\s*conventions\s*\)\s*\)\s*\{\s*floorViolations\[code\]"),
            source);
    }
}
