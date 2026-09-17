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

    // 한 단계가 리터럴로 쓰는 INSERT 와 매개변수로 쓰는 INSERT 를 함께 가지면 이름을 일부만 안다 - 모르는 쪽에 읽는 이름이
    // 있을 수 있으니 침묵한다. 이 가지는 위 두 시험으로 안 잠긴다: 파서 구현에서 「모름」인 쓰기는 이름을 싣지 않아 그 둘은
    // 「쓰는 이름 0」에서 먼저 조용해진다(2026-09-13 되돌림으로 확인 - 「모름」 판정을 걷어내도 둘 다 초록이었다).
    [Fact]
    public void WriterWithBothALiteralAndAParameterizedWrite_IsUnknown()
    {
        var original = Fixture("Batch11-S13.md");
        var writer = original.Replace("-- SQL_VALIDATE_CAPTURE\n",
            "-- SQL_VALIDATE_CAPTURE\nINSERT INTO batch.BatchControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)\n" +
            "VALUES (@p_runId, N'S13', @p_controlName, @p_controlValue, SYSUTCDATETIME());\n");
        Assert.NotEqual(original, writer);

        // 짝: 원본 S13(리터럴 쓰기만)이면 같은 S20 에 발화한다(첫 시험).
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

    // ── CTE 안 UNION ALL 리터럴로 쓰는 이름(POQSettleBatch20, 2026-09-17) ──────────────────────────────
    // 사전 선언: docs/audit-reports/2026-09-17-K2-CTE-UNION-쓰기이름-사전선언.md. 쓰는 단계가
    // `WITH X AS (SELECT N'a' AS ControlName … UNION ALL SELECT N'b' …) INSERT … SELECT C.ControlName FROM X AS C`
    // 로 쓰면 종전엔 「모름」이라 대조에서 빠졌다(agent/steps 관할 22 중 여섯이 이 모양).

    /// <summary>B20 1 회차 S11 의 통제명 <c>N'LedgerRowCount'</c> 를 바꿔 S18 이 읽는 이름과의 교집합을 0 으로 만든다.
    /// 같은 리터럴이 쓰기 CTE 와 뒤의 자기 재조회 SELECT(<c>LiveControl</c>) 두 자리에 있다 - <b>둘 다</b> 바꾼다. 처음엔 쓰기만 바꿨는데
    /// 그러면 변이된 S11 이 아무도 안 쓰는 이름을 스스로 읽어 ⑥(일부 겹침, 2026-09-17)이 S11 을 옳게 고발했다 - 변이가 모순이었다.</summary>
    private static string Batch20S11WithDisjointNames()
    {
        var original = Fixture("Batch20-S11-attempt1.md");
        Assert.Equal(2, original.Split("N'LedgerRowCount'").Length - 1);
        return original.Replace("N'LedgerRowCount'", "N'FrozenRowCount'");
    }

    private static (string Code, string Markdown) ReaderOf(string owner, string name) => ("S99",
        "### S99\n\n```sql\n-- SQL_READ\nSELECT ControlValue\n  FROM batch.BatchControlTotal\n WHERE RunId = @p_runId\n" +
        $"   AND StepCode = N'{owner}'\n   AND ControlName = N'{name}';\n```\n");

    // C1: 실물 1 회차 쌍에서 교집합을 0 으로 만든 변이 - 쓰는 단계의 이름을 알아야 읽는 단계(S18)를 지목한다.
    [Fact]
    public void RealCteUnionWriter_WithReaderSharingNoName_IsAttributedToTheReader()
    {
        var defects = Validate(("S11", Batch20S11WithDisjointNames()), ("S18", Fixture("Batch20-S18-attempt1.md")));

        var defect = Assert.Single(defects);
        Assert.Equal("S18", defect.Key);
        foreach (var name in new[] { "FrozenRowCount", "TxAmtSum", "CLTotalSum", "PGTotalSum", "POQIncomeSum",
                     "NonSettleAmtSum", "SeperateAmtSum", "ForeignSettleAmtSum" })
            Assert.Contains("`" + name + "`", defect.Value.Reason);
        Assert.DoesNotContain("`LedgerRowCount`, `NonSettleAmtSum`", defect.Value.Reason);
    }

    // C2(「변이 없는 실물 쌍은 ⑤ 로 조용하다」)는 ⑥(2026-09-17)이 뒤집었다 - 같은 입력이 이제 발화한다:
    // RealReaderSharingOneNameButReadingSevenNamesNobodyWrites_IsAttributedToTheReader.

    // C3: 한정자 없는 ControlName(B15 S18 · B19 S20)도 FROM 의 유일한 출처인 CTE 로 푼다. B19 S20 은 앞에 CTE 셋이 더 있고
    // 가지 일부가 다른 CTE 에서 값을 끌어온다(이름 자리는 전부 리터럴). 읽는 단계는 합성이지만 쓰는 쪽은 배송본 그대로다.
    [Theory]
    [InlineData("Batch15-S18.md", "S18", "Ledger.RowCount")]
    [InlineData("Batch19-S20.md", "S20", "Ledger.RowCount")]
    [InlineData("Batch20-S11-attempt1.md", "S11", "LedgerRowCount")]
    public void RealCteUnionWriter_IsKnown(string fixture, string owner, string writtenName)
    {
        var writer = Fixture(fixture);
        Assert.Contains("N'" + writtenName + "'", writer);

        Assert.Equal("S99", Assert.Single(Validate((owner, writer), ReaderOf(owner, "NoSuchControlName"))).Key);
        // 짝: 실제로 쓰는 이름을 읽으면 조용하다 - 위 발화가 「이름을 읽어 냈고 안 겹쳐서」임을 보인다.
        Assert.Empty(Validate((owner, writer), ReaderOf(owner, writtenName)));
    }

    // C4: CTE 가지 중 하나라도 이름 자리가 리터럴이 아니면 무엇을 쓰는지 다 모른다 - 「모름」으로 침묵한다.
    // 코퍼스에 이 모양의 실물이 없어(사전 선언 C5 의 「모양 C 실물」은 틀렸다 - B13 S18 의 조합은 CTE 가지가 아니라 INSERT
    // SELECT 자리에 있다) 실물 B20 S11 의 가지 하나를 매개변수로 바꾼다. 리터럴 가지만 담도록 바꾸면 이 시험이 빨갛다.
    [Fact]
    public void CteUnionWriterWithOneNonLiteralBranch_IsUnknown()
    {
        var original = Batch20S11WithDisjointNames();
        const string from = "    SELECT N'TxAmtSum', TxAmtSum FROM Ledger\n";
        var at = original.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0);
        var writer = original[..at] + "    SELECT @p_extraControlName, TxAmtSum FROM Ledger\n" + original[(at + from.Length)..];

        Assert.Empty(Validate(("S11", writer), ("S18", Fixture("Batch20-S18-attempt1.md"))));
    }

    // C4: 배송본 B13 S18 - CTE-UNION 위에서 이름을 `MetricName + N'.Expected'` 로 조합해 쓴다. 조합은 모른다.
    // [판별력 없음 - 회귀 방지용] 조합이 CTE 가지가 아니라 INSERT 의 SELECT 자리에 있어 새 CTE 가지에 닿기 전에 종전 default 가
    // 「모름」으로 만든다. 「가지 하나라도 비리터럴이면 모름」 가드를 재는 시험은 위 CteUnionWriterWithOneNonLiteralBranch_IsUnknown 이다.
    [Fact]
    public void RealWriterComposingNamesAtRuntimeOverACteUnion_IsUnknown()
    {
        var writer = Fixture("Batch13-S18.md");
        Assert.Contains("MetricName + N'.Expected'", writer);

        Assert.Empty(Validate(("S18", writer), ReaderOf("S18", "NoSuchControlName")));
    }

    // ── ⑥ 일부 겹침(2026-09-17) ─────────────────────────────────────────────────────────────
    // 사전 선언: docs/audit-reports/2026-09-17-K2-부분겹침-사전선언.md. 이름이 하나라도 겹치면 ⑤ 는 침묵한다 - 그래서
    // B20 1 회차 S18 이 S11 몫을 8 이름으로 읽는데 S11 이 쓰는 이름과 LedgerRowCount 하나만 겹친 것(나머지 7 행은 어떤 실행
    // 에서도 없다)을 Critic 만 잡았다. ⑥ 은 「몫 전부를 알고 · 겹치되 · 문서 어디에서도 안 쓰인 읽기 이름이 남을 때」 발화한다.

    private static readonly string[] Batch20S18MissingNames =
    {
        "LedgerCLTotal", "LedgerExtraTxAmt", "LedgerForeignSettleAmt", "LedgerPGTotal", "LedgerPOQIncome", "LedgerSeperateAmt", "LedgerTxAmt",
    };

    /// <summary>⑥ 문구의 「그중 … 은(는)」 사이 - 빠진 이름 목록.</summary>
    private static string MissingList(string reason)
    {
        var start = reason.IndexOf("그중 ", StringComparison.Ordinal);
        var end = reason.IndexOf("은(는)", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, reason);
        return reason[start..end];
    }

    private static (string Code, string Markdown)[] Batch20Attempt1() => new[]
    {
        ("S11", Fixture("Batch20-S11-attempt1.md")), ("S18", Fixture("Batch20-S18-attempt1.md")),
    };

    // P1: 변이 없는 실물 1 회차 쌍.
    [Fact]
    public void RealReaderSharingOneNameButReadingSevenNamesNobodyWrites_IsAttributedToTheReader()
    {
        var defect = Assert.Single(Validate(Batch20Attempt1()));

        Assert.Equal("S18", defect.Key);
        var missing = MissingList(defect.Value.Reason);
        foreach (var name in Batch20S18MissingNames) Assert.Contains("`" + name + "`", missing);
        Assert.DoesNotContain("`LedgerRowCount`", missing);
        Assert.Contains("`TxAmtSum`", defect.Value.Reason);   // S11 이 실제로 쓰는 이름 - 맞출 짝
    }

    // P7 n3: 빠진 이름을 다른 단계가 쓰면 그 행은 실행 때 생길 수 있다 - 「어디에서도 안 쓰인다」가 거짓이 되어 침묵한다.
    [Fact]
    public void WhenAnotherStepWritesTheMissingNames_IsSilent()
    {
        var values = string.Join(",\n", Batch20S18MissingNames.Select(n => $"    (@p_runId, N'S12', N'{n}', 0, SYSUTCDATETIME())"));
        var writer = "### S12\n\n```sql\n-- SQL_INSERT_EXTRA\nINSERT INTO batch.BatchControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)\nVALUES\n" + values + ";\n```\n";

        Assert.Empty(Validate(Batch20Attempt1().Append(("S12", writer)).ToArray()));
    }

    /// <summary>실물 B10 S17(이름을 매개변수로 쓰는 범용 헬퍼)을 S17 자리에 둔다 - 문서에 「모름」 쓰기가 생긴다.
    /// <paramref name="callsWithMissingNames"/> 면 그 의사코드의 실물 호출 줄 모양(<c>p_controlName: "…"</c>)으로 빠진 이름 7 을 넘기는 줄을 더한다.</summary>
    private static string Batch10S17AsUnknownWriter(bool callsWithMissingNames)
    {
        var original = Fixture("Batch10-S17.md");
        const string anchor = "repository.execute(SQL_INSERT_CONTROL_TOTAL, { p_runId: runId, p_stepCode: \"S17\", p_controlName: \"TSettleMst_CLTotal_TX\",  p_controlValue: ledgerTxTotal })\n";
        Assert.Contains(anchor, original);
        if (!callsWithMissingNames) return original;
        var calls = string.Concat(Batch20S18MissingNames.Select(n =>
            $"repository.execute(SQL_INSERT_CONTROL_TOTAL, {{ p_runId: runId, p_stepCode: \"S11\", p_controlName: \"{n}\",  p_controlValue: ledgerTxTotal }})\n"));
        return original.Replace(anchor, anchor + calls);
    }

    // P6(a): 「모름」 쓰기가 있고 **다른 단계**의 의사코드가 그 이름들을 헬퍼에 넘기면 그 행은 실행 때 생긴다 - 침묵한다.
    [Fact]
    public void WithAnUnknownWriter_AHelperCallElsewherePassingTheMissingNames_IsSilent()
    {
        Assert.Empty(Validate(Batch20Attempt1().Append(("S17", Batch10S17AsUnknownWriter(callsWithMissingNames: true))).ToArray()));
    }

    // P6(b): 같은 「모름」 쓰기가 있어도 이름을 담은 의사코드가 **읽는 단계 자신**뿐이면 발화한다. 실물 S18 은
    // requiredFrozenNames = ["LedgerRowCount", "LedgerTxAmt", …] 로 자기가 기대하는 이름을 나열한다 - 그것은 쓰기가 아니다.
    [Fact]
    public void WithAnUnknownWriter_TheReadersOwnPseudocodeListingTheNames_StillReports()
    {
        Assert.Contains("\"LedgerTxAmt\"", Fixture("Batch20-S18-attempt1.md"));

        var defect = Assert.Single(Validate(Batch20Attempt1().Append(("S17", Batch10S17AsUnknownWriter(callsWithMissingNames: false))).ToArray()));

        Assert.Equal("S18", defect.Key);
    }

    // P6(c): 이름을 런타임에 조합하는 문서는 「어디에서도 안 쓰인다」를 믿을 수 없다 - 통째로 침묵한다(실물 B16 조합 펜스를 옮겼다).
    [Fact]
    public void ADocumentBuildingNamesAtRuntime_IsSilent()
    {
        var withRuntimeNames = Fixture("Batch20-S11-attempt1.md") + "\n" + Fixture("Batch16-runtime-name-fence.md");

        Assert.Empty(Validate(("S11", withRuntimeNames), ("S18", Fixture("Batch20-S18-attempt1.md"))));
    }

    // P7 n6: 몫이 둘인데 하나를 모르면 빠진 이름이 그 단계 몫일 수 있다 - 침묵한다. 읽기의 몫만 IN (N'S11', N'S17') 로 넓히고
    // S17 자리에 실물 매개변수 헬퍼(B10 S17)를 둔다.
    [Fact]
    public void WhenOneOfTheReadOwnersIsUnknown_IsSilent()
    {
        var original = Fixture("Batch20-S18-attempt1.md");
        const string from = "   AND StepCode = N'S11'\n   AND ControlName IN\n";
        Assert.Contains(from, original);
        var reader = original.Replace(from, "   AND StepCode IN (N'S11', N'S17')\n   AND ControlName IN\n");

        Assert.Empty(Validate(("S11", Fixture("Batch20-S11-attempt1.md")), ("S17", Batch10S17AsUnknownWriter(callsWithMissingNames: false)), ("S18", reader)));
    }

    // ⑥ 의 공통 규약 동률: 규약이 빠진 이름을 담고 쓰는 쪽 이름을 하나도 안 담으면 어긴 것은 쓰는 단계(S11)다.
    // 규약 문장은 합성이다(B20 실물 규약에는 통제명이 없다) - ⑤ 의 같은 규칙 시험과 같은 방식.
    [Fact]
    public void PartialOverlap_WhenOnlyTheReaderFollowsTheSharedConventions_TheWriterIsAttributed()
    {
        var conventions = "통제명은 " + string.Join(", ", Batch20S18MissingNames.Select(n => "N'" + n + "'")) + " 을 쓴다.";

        var defect = Assert.Single(ValidateWith(conventions, Batch20Attempt1()));

        Assert.Equal("S11", defect.Key);
        Assert.Contains("공통 규약", defect.Value.Reason);
        // 짝: 규약이 쓰는 쪽 이름(TxAmtSum)도 담으면 종전대로 읽는 단계다.
        Assert.Equal("S18", Assert.Single(ValidateWith(conventions + " N'TxAmtSum'", Batch20Attempt1())).Key);
    }

    // ⑤ 가 난 읽기에는 ⑥ 을 겹쳐 걸지 않는다 - 몫이 둘(S13·S20)인 실물 B11 S20 은 S13 몫으로 ⑤ 가 나는데, 읽기 목록에 아무도 안 쓰는
    // 이름 하나를 더하면 합집합(S20 이 LEDGER_* 를 쓴다)과는 겹쳐 ⑥ 조건도 참이 된다. 같은 읽기를 두 문구로 두 번 여는 것을 막는다.
    [Fact]
    public void AReadAlreadyReportedForAnEmptyOverlap_GetsNoPartialOverlapReportToo()
    {
        var original = Fixture("Batch11-S20.md");
        const string from = "               N'LEDGER_POQ_INCOME'\n           )";
        Assert.Contains(from, original);
        var reader = original.Replace(from, "               N'LEDGER_POQ_INCOME',\n               N'LEDGER_NOBODY_WRITES'\n           )");

        var defect = Assert.Single(Validate(("S13", Fixture("Batch11-S13.md")), ("S20", reader)));

        Assert.Equal("S20", defect.Key);
        Assert.Contains("겹치는 이름이 하나도 없어", defect.Value.Reason);
        Assert.DoesNotContain("그중 ", defect.Value.Reason);
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
