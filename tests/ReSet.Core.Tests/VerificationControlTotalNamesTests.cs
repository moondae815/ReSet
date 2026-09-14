using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [K2 · 검증 SQL 세트] 통합 데이터 정합성 검증 SQL 세트가 <c>StepCode</c> 로 특정 단계 몫을 걸러 읽는 통제명이, 그 단계가 실제로 쓰는
/// 통제명과 <b>하나도</b> 겹치지 않는가.
///
/// POQSettleBatch13(2026-09-14): V04 가 S11 몫을 <c>TSettleMst.RowCount</c>… 로 읽고 S11 은 <c>SettleFactRowCount</c>… 로 써서 V04 의
/// <c>FULL OUTER JOIN</c> 이 어떤 실행에서도 전부 불일치를 냈다. 이름이 제어 표 읽기 문장이 아니라 <b>조인한 다른 CTE</b> 에 있어
/// 단계 K2 를 넓혀도 못 봤다. 같은 측정이 POQSettleBatch11 의 <c>LedgerPOQIncome</c> 읽기(S13 은 <c>POQIncome</c>)를 새로 찾았다.
/// 판독: <c>docs/audit-reports/2026-09-14-K2-검증SQL세트-사전선언.md</c>. 픽스처는 배송본 바이트 그대로다.
/// </summary>
public sealed class VerificationControlTotalNamesTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "control-total", name));

    private static BatchStepPlan ControlStep(string code) => new(
        Code: code, Name: $"{code} 단계",
        LegacyProcedures: Array.Empty<string>(),
        TargetTables: new[] { "batch.BatchControlTotal" },
        ErrorCodes: new[] { "-9200" }, Chunkable: false, SchemaTables: Array.Empty<string>());

    private static IReadOnlyList<DetailedError> Validate(
        string plan, string? conventions = null, params (string Code, string Markdown)[] sections) =>
        new MechanicalValidator().ValidateVerificationControlTotalNames(
            plan,
            sections.ToDictionary(s => s.Code, s => s.Markdown, StringComparer.OrdinalIgnoreCase),
            sections.Select(s => ControlStep(s.Code)).ToList(),
            conventions);

    private static string Batch1Plan() => Fixture("Batch1-verification.md");

    private static (string, string)[] Batch1Steps() =>
        new[] { "S02", "S03", "S04", "S05", "S06", "S07" }.Select(c => (c, Fixture($"Batch1-{c}.md"))).ToArray();

    // V04 가 읽는 네 이름 → S11 이 쓰는 같은 뜻의 이름.
    private static readonly (string Reader, string Writer)[] Batch13NameFixes =
    {
        ("TSettleMst.RowCount", "SettleFactRowCount"),
        ("TSettleMst.TxAmt", "SettleFactTxAmt"),
        ("TSettleMst.CLTotal", "SettleFactCLTotal"),
        ("TSettleMst.PGTotal", "SettleFactPGTotal"),
    };

    [Fact]
    public void Batch13_V04ReadingS11ThroughAJoinedNameCte_IsReportedWithEveryName()
    {
        var error = Assert.Single(Validate(Fixture("Batch13-verification.md"), null, ("S11", Fixture("Batch13-S11.md"))));

        Assert.Equal(ErrorType.VerificationControlTotalNameMismatch, error.Type);
        Assert.Null(error.OwnerStepCode);   // 읽는 쪽(골격)이 고친다 - 단계를 열지 않는다
        foreach (var (reader, writer) in Batch13NameFixes)
        {
            Assert.Contains("`" + reader + "`", error.Message);
            Assert.Contains("`" + writer + "`", error.Message);
        }
        Assert.Contains("`SettleFactPOQIncome`", error.Message);
        Assert.Contains("`SettleFactExtraTxAmt`", error.Message);
    }

    [Fact]
    public void Batch11_VerificationReadingLedgerPOQIncome_IsReported()
    {
        var error = Assert.Single(Validate(Fixture("Batch11-verification.md"), null, ("S13", Fixture("Batch11-S13.md"))));

        Assert.Contains("`LedgerPOQIncome`", error.Message);
        Assert.Contains("`POQIncome`", error.Message);
    }

    // 양성 대조 짝 ①: V04 가 S11 의 이름으로 읽으면 조용하다 - 위 발화가 이름 불일치 때문임을 보인다(조인 모양 그대로).
    [Fact]
    public void Batch13_V04AfterReadingTheWritersNames_IsSilent()
    {
        var original = Fixture("Batch13-verification.md");
        var aligned = Batch13NameFixes.Aggregate(original, (text, fix) => text.Replace("N'" + fix.Reader + "'", "N'" + fix.Writer + "'"));
        Assert.NotEqual(original, aligned);

        Assert.Empty(Validate(aligned, null, ("S11", Fixture("Batch13-S11.md"))));
    }

    // 양성 대조 짝 ②: Batch11 의 틀린 이름 하나만 고치면 조용하다 - 같은 세트의 `LedgerRowCount` 읽기는 원래 맞았다.
    [Fact]
    public void Batch11_AfterFixingTheOneWrongName_IsSilent()
    {
        var original = Fixture("Batch11-verification.md");
        var fixedPlan = original.Replace("N'LedgerPOQIncome'", "N'POQIncome'");
        Assert.NotEqual(original, fixedPlan);

        Assert.Empty(Validate(fixedPlan, null, ("S13", Fixture("Batch11-S13.md"))));
    }

    // 실물 음성 ①: B1 의 검증 세트는 S02~S07 몫을 리터럴로 읽지만 그 단계들이 통제명을 리터럴로 쓰지 않는다 - 무엇을 쓰는지 모르면 침묵.
    [Fact]
    public void Batch1_ReadingStepsThatWriteNoLiteralNames_IsSilent()
    {
        Assert.Empty(Validate(Batch1Plan(), null, Batch1Steps()));
    }

    // 실물 음성 ②: B12 의 검증 세트는 매개변수 이름으로 제어 표에 쓰고, StepCode 리터럴 거름은 다른 표에 걸려 있다.
    [Fact]
    public void Batch12_VerificationSet_IsSilent()
    {
        Assert.Empty(Validate(Fixture("Batch12-verification.md"), null,
            ("S12", Fixture("Batch12-S12.md")), ("S17", Fixture("Batch12-S17.md"))));
    }

    // 「전부 리터럴」: 조인한 이름 CTE 에 리터럴이 아닌 ControlName 이 하나라도 섞이면 무엇을 읽는지 모른다 - 침묵.
    [Fact]
    public void JoinedNameSourceWithANonLiteralName_IsSilent()
    {
        var original = Fixture("Batch13-verification.md");
        var mixed = original.Replace("SELECT N'TSettleMst.PGTotal',", "SELECT @p_extraControlName,");
        Assert.NotEqual(original, mixed);

        Assert.Empty(Validate(mixed, null, ("S11", Fixture("Batch13-S11.md"))));
    }

    // 쓰는 단계가 이름을 매개변수로 쓰면 무엇을 쓰는지 모른다 - 침묵(K2 와 같은 규칙).
    [Fact]
    public void OwnerWritingNamesThroughAParameter_IsSilent()
    {
        var original = Fixture("Batch13-S11.md");
        var parameterized = original.Replace("N'SettleFactExtraTxAmt'", "@p_extraControlName");
        Assert.NotEqual(original, parameterized);

        Assert.Empty(Validate(Fixture("Batch13-verification.md"), null, ("S11", parameterized)));
    }

    // [INSERT 문장의 CTE - 최종 리뷰 Important 1] WITH 가 INSERT 에 붙으면(`WITH … INSERT INTO … SELECT … JOIN …`) 같은 문장 CTE 를
    // 풀 수 있어야 한다. 처음엔 SelectStatement 방문에서만 CTE 를 채워 이 모양이 조용했다.
    [Fact]
    public void JoinedNamesInsideAnInsertWithCtes_AreRead()
    {
        var original = Fixture("Batch13-verification.md");
        var inserted = original.Replace(
            ")\nSELECT\n    COALESCE(R.ControlName, C.ControlName) AS ControlName,",
            ")\nINSERT INTO batch.BatchReconciliation (ControlName, FrozenValue, CurrentValue)\nSELECT\n    COALESCE(R.ControlName, C.ControlName) AS ControlName,");
        Assert.NotEqual(original, inserted);

        Assert.Contains("`TSettleMst.RowCount`", Assert.Single(Validate(inserted, null, ("S11", Fixture("Batch13-S11.md")))).Message);
    }

    // [검증 세트가 제어 표에 스스로 쓴다 - 최종 리뷰 Minor 3] 검증 SQL 이 단계 코드를 달고 직접 쓴 뒤 되읽으면 그 행이 누구 몫인지 모른다 - 침묵.
    [Fact]
    public void VerificationSetThatWritesTheControlTableItself_IsSilent()
    {
        var original = Fixture("Batch13-verification.md");
        var selfWriting = original.Replace(
            "### V04 TSettleMst 동결 지문\n",
            "### V04 TSettleMst 동결 지문\n\n```sql\nINSERT INTO batch.BatchControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)\n" +
            "VALUES (@p_runId, N'S11', N'TSettleMst.RowCount', 0, SYSUTCDATETIME());\n```\n");
        Assert.NotEqual(original, selfWriting);

        Assert.Empty(Validate(selfWriting, null, ("S11", Fixture("Batch13-S11.md"))));
    }

    // [귀속] 어휘는 검증 세트의 원문 줄이다 - 조립 문서에서 골격만 열고 단계는 열지 않는다(골격 패치 수리로 간다).
    [Fact]
    public void OnTheAssembledPlan_TheDefectOpensOnlyTheSkeleton()
    {
        var sections = new[] { ("S13", Fixture("Batch11-S13.md")), ("S20", Fixture("Batch11-S20.md")) };
        var plan = BatchPlanAssembler.Assemble(Fixture("Batch11-skeleton.md"), sections.Select(s => s.Item2).ToList());
        var steps = sections.Select(s => ControlStep(s.Item1)).ToList();

        var error = Assert.Single(Validate(plan, null, sections));
        Assert.NotNull(error.Lexemes);
        Assert.NotEmpty(error.Lexemes!);

        var attributions = MechanicalValidator.ViolationLexemes(error)
            .Select(lexeme => L1ViolationAttribution.Attribute(plan, lexeme, steps))
            .ToList();
        Assert.Contains(attributions, a => a.Skeleton);
        Assert.All(attributions, a => Assert.Empty(a.StepCodes));
    }

    // [공통 규약 동률] 규약이 검증 세트의 이름을 담고 쓰는 단계의 이름을 하나도 안 담으면 어긴 것은 쓰는 단계다 - 어휘를
    // 쓰는 단계의 원문 줄로 싣는다(검증 세트 줄은 싣지 않는다).
    //
    // [하위 헤딩 - 최종 리뷰 Important 2] 실물 S11 의 쓰기 SQL 은 `#### ` 하위 헤딩 아래에 있고,
    // L1ViolationAttribution.MapRegions 는 단계 안의 코드 없는 하위 헤딩 아래를 Unknown 으로 둔다 - 어휘만 실으면 이 분기는
    // 실물에서 단계에도 골격에도 안 붙어 전량 재생성으로 떨어진다(코퍼스 GPT 판 단계 대부분이 그 모양: B11 22 중 18 · B12 18 중 17 ·
    // B13 19 중 16). 그래서 쓰는 단계 코드를 OwnerStepCode 로 직접 싣는다. MapRegions 자체는 이 브랜치 밖이라 고치지 않았다.
    [Fact]
    public void WhenOnlyTheVerificationSetFollowsTheSharedConventions_TheWritersLinesAreTheLexemes()
    {
        var s11 = Fixture("Batch13-S11.md");
        var plan = Fixture("Batch13-verification.md");
        var conventions = "통제명은 N'TSettleMst.RowCount', N'TSettleMst.TxAmt', N'TSettleMst.CLTotal', N'TSettleMst.PGTotal' 을 쓴다.";

        var error = Assert.Single(Validate(plan, conventions, ("S11", s11)));
        Assert.Contains("공통 규약", error.Message);
        // 어휘로는 이 단계에 귀속되지 않는다(아래 [하위 헤딩]) - 쓰는 단계를 직접 싣고 오케스트레이터가 그것으로 연다.
        Assert.Equal("S11", error.OwnerStepCode);

        var lexemes = MechanicalValidator.ViolationLexemes(error);
        Assert.Equal(6, lexemes.Count);
        Assert.All(lexemes, lexeme =>
        {
            Assert.Contains(lexeme, s11);
            Assert.DoesNotContain(lexeme, plan);
        });
    }

    // 조인으로 끌어온 이름은 단계 본문에도 같은 재료다 - V04 모양을 읽는 단계로 옮기면 단계 K2 가 발화한다.
    [Fact]
    public void StepLevelK2_SeesTheJoinedNameShapeToo()
    {
        var reader = Fixture("Batch13-verification.md").Replace("## 통합 데이터 정합성 검증 SQL 세트", "### S18 검증");
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["S11"] = Fixture("Batch13-S11.md"),
            ["S18"] = reader,
        };

        var defects = new MechanicalValidator().ValidateControlTotalNameConsistency(
            sections, new[] { ControlStep("S11"), ControlStep("S18") }.ToList());

        Assert.Contains("`TSettleMst.RowCount`", Assert.Contains("S18", defects).Reason);
    }

    // [배선] 통합 L1 자리에서 부르고 결과를 l1Result 에 합친다 - 발화 시험은 이 호출이 없어도 초록이다.
    [Fact]
    public void OrchestratorAddsTheErrorsToTheConsolidatedL1Result()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));

        Assert.Matches(new Regex(
            @"foreach\s*\(\s*var\s+\w+\s+in\s+_validator\.ValidateVerificationControlTotalNames\(\s*consolidatedPlan\s*,\s*lastStepSections\s*,\s*currentSteps\s*,\s*BatchPlanAssembler\.ExtractSharedConventions\(\s*lastSkeleton\s*\)\s*\)\s*\)\s*\{[^}]*l1Result\.DetailedErrors\.Add"),
            source);
    }

    // [배선 - 검사가 아는 단계로 직접 귀속] 공통 규약 동률 분기는 어휘로 단계에 안 붙는다(하위 헤딩). 귀속 루프가 유형과 무관하게
    // OwnerStepCode 로 그 단계를 연다(2026-09-14 CheckControlTotalProducer 도 같은 규칙을 탄다).
    [Fact]
    public void OrchestratorOpensTheOwnerStepFromTheErrorItself()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));

        Assert.Matches(new Regex(
            @"if\s*\(\s*detail\.OwnerStepCode\s+is\s*\{\s*\}\s*(\w+)\s*\)\s*\{\s*(?://[^\n]*\n\s*)*AddOwner\(\s*\1\s*\)\s*;\s*continue\s*;"),
            source);
    }
}
