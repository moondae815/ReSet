using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [RunId 발급 전 단계를 요구하는 게이트] 체크포인트·저널을 <c>RunId = @…</c> 와 <c>Succeeded</c> 로 읽는 게이트가, RunId 를 발급하는 단계보다 먼저
/// 도는 단계를 요구하면 그 게이트는 늘 실패한다 - 그 단계에는 이 실행의 행을 쓸 RunId 가 없다.
///
/// 실측(2026-09-14): GPT Consolidator 세 판(B11 · B12 · B13)이 모두 S01 을 RunId 발급(S02) 앞에 두고 게이트가 S01 을 요구한 채 배송했다(6 자리).
/// B13 은 Critic 두 번이 모두 놓쳤다. 판독: <c>docs/audit-reports/2026-09-14-RunId이전단계-게이트-사전선언.md</c>. 픽스처는 배송본 바이트 그대로다.
/// </summary>
public sealed class GateBeforeRunIdTests
{
    private static string Fixture(string directory, string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", directory, name));

    private static string Gate(string name) => Fixture("run-gate", name);

    private static string Document(IEnumerable<string> stepSections, string? verificationSet = null)
    {
        var body = new StringBuilder();
        body.AppendLine("## 통합 배치 아키텍처 개요\n\n내용.\n");
        body.AppendLine("## Mermaid 기반 통합 흐름도\n\n```mermaid\nflowchart TD\nA[\"시작\"] --> B[\"끝\"]\n```\n");
        body.AppendLine("## 단계별 이행 상세 및 의사코드\n");
        foreach (var section in stepSections) body.AppendLine(section.Trim() + "\n");
        body.AppendLine((verificationSet ?? "## 통합 데이터 정합성 검증 SQL 세트\n\n내용.").Trim() + "\n");
        return body.ToString();
    }

    private static DetailedError[] GateErrors(string document) =>
        new MechanicalValidator().ValidateConsolidated(document).DetailedErrors
            .Where(d => d.Type == ErrorType.GateRequiresStepBeforeRunId)
            .ToArray();

    private static string Step(string code, string sql) => $"### {code}. 단계\n\n```sql\n{sql.Trim()}\n```";

    private const string SyntheticS01 = "SELECT 1 AS EnvironmentOk;";
    private const string SyntheticS02 = "INSERT INTO batch.BatchRun (JobName, BatchYmd, RunStatus, StartedAtUtc) VALUES (N'Job', @p_ymd, N'Running', SYSUTCDATETIME());";

    private static string[] Batch13Steps(string? s19 = null, string? s02 = null) =>
        new[] { Gate("Batch13-S01.md"), s02 ?? Gate("Batch13-S02.md"), s19 ?? Gate("Batch13-S19.md") };

    // G1 · G6: B13 - S19 게이트(VALUES + NOT EXISTS)는 S19 로, 검증 세트 V13(VALUES)은 골격으로 귀속된다.
    [Fact]
    public void Batch13_S19AndV13GatesRequiringS01_AreReportedAndAttributed()
    {
        var document = Document(Batch13Steps(), Fixture("control-total", "Batch13-verification.md"));
        var errors = GateErrors(document);

        Assert.Equal(2, errors.Length);
        var step = Assert.Single(errors, e => e.OwnerStepCode != null);
        Assert.Equal("S19", step.OwnerStepCode);
        Assert.Contains("S01", step.Message);
        Assert.Contains("S02", step.Message);

        var skeleton = Assert.Single(errors, e => e.OwnerStepCode == null);
        var steps = new[] { "S01", "S02", "S19" }.Select(c => new BatchStepPlan(c, c, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>())).ToList();
        var attributions = MechanicalValidator.ViolationLexemes(skeleton)
            .Select(lexeme => L1ViolationAttribution.Attribute(document, lexeme, steps)).ToList();
        Assert.NotEmpty(attributions);
        Assert.Contains(attributions, a => a.Skeleton);
    }

    // G1: B11 - S21 의 StepCode IN (N'S01', …).
    [Fact]
    public void Batch11_S21InListGate_IsReported()
    {
        var error = Assert.Single(GateErrors(Document(new[] { Gate("Batch11-S01.md"), Gate("Batch11-S02.md"), Gate("Batch11-S21.md") })));
        Assert.Equal("S21", error.OwnerStepCode);
    }

    // G1: B12 - S17(VALUES → JOIN) · S18(VALUES + IN) · 검증 세트 SQL-12(BETWEEN).
    [Fact]
    public void Batch12_ValuesInListAndBetweenGates_AreAllReported()
    {
        var errors = GateErrors(Document(
            new[] { Gate("Batch12-S01.md"), Gate("Batch12-S02.md"), Gate("Batch12-S17.md"), Gate("Batch12-S18.md") },
            Fixture("control-total", "Batch12-verification.md")));

        Assert.Equal(new[] { "S17", "S18" }, errors.Select(e => e.OwnerStepCode).OfType<string>().OrderBy(c => c).ToArray());
        Assert.Single(errors, e => e.OwnerStepCode == null);
    }

    // G2: 짝 - S19 게이트 목록에서 S01 을 빼면 S19 는 조용하다(V13 은 그대로).
    [Fact]
    public void Batch13_S19GateWithoutS01_IsSilent()
    {
        var original = Gate("Batch13-S19.md");
        var withoutS01 = original.Replace("(N'S01'), (N'S02'),", "(N'S02'),");
        Assert.NotEqual(original, withoutS01);

        Assert.DoesNotContain(GateErrors(Document(Batch13Steps(s19: withoutS01))), e => e.OwnerStepCode == "S19");
    }

    // G3: 면제 - 발급 단계가 S01 행을 대신 기록하면 조용하다.
    [Fact]
    public void WhenTheIssuingStepRecordsS01sCheckpoint_IsSilent()
    {
        var s02 = Gate("Batch13-S02.md") + "\n\n```sql\nINSERT INTO batch.BatchCheckpoint (RunId, StepCode, CheckpointStatus, CompletedAtUtc)\nVALUES (@p_runId, N'S01', N'Succeeded', SYSUTCDATETIME());\n```\n";

        Assert.Empty(GateErrors(Document(Batch13Steps(s02: s02))));
    }

    // G4: 부정 자리의 리터럴은 요구가 아니다 / 긍정 자리 셋은 요구다.
    [Theory]
    [InlineData("AND StepCode <> N'S01'", false)]
    [InlineData("AND StepCode NOT IN (N'S01')", false)]
    [InlineData("AND StepCode NOT BETWEEN N'S01' AND N'S01'", false)]
    [InlineData("AND NOT (StepCode = N'S01')", false)]
    [InlineData("AND StepCode = N'S01'", true)]
    [InlineData("AND StepCode IN (N'S01', N'S02')", true)]
    [InlineData("AND StepCode BETWEEN N'S01' AND N'S02'", true)]
    public void OnlyPositiveStepCodeLiteralsAreRequirements(string condition, bool expectReported)
    {
        var document = Document(new[]
        {
            Step("S01", SyntheticS01),
            Step("S02", SyntheticS02),
            Step("S03", $"SELECT COUNT(*) FROM batch.BatchCheckpoint WHERE RunId = @p_runId AND CheckpointStatus = N'Succeeded' {condition};"),
        });

        Assert.Equal(expectReported, GateErrors(document).Length == 1);
    }

    // G5: RunId 를 첫 단계가 발급하면 그 앞 단계가 없다 - 조용.
    [Fact]
    public void WhenTheFirstStepIssuesTheRunId_IsSilent()
    {
        var document = Document(new[]
        {
            Step("S01", SyntheticS02),
            Step("S02", "SELECT 1;"),
            Step("S03", "SELECT COUNT(*) FROM batch.BatchCheckpoint WHERE RunId = @p_runId AND CheckpointStatus = N'Succeeded' AND StepCode IN (N'S01', N'S02');"),
        });

        Assert.Empty(GateErrors(document));
    }

    // 게이트 조건: Succeeded 를 안 보거나 RunId 로 거르지 않는 조회는 게이트가 아니다.
    [Theory]
    [InlineData("SELECT COUNT(*) FROM batch.BatchCheckpoint WHERE RunId = @p_runId AND StepCode IN (N'S01', N'S02');")]
    [InlineData("SELECT COUNT(*) FROM batch.BatchCheckpoint WHERE CheckpointStatus = N'Succeeded' AND StepCode IN (N'S01', N'S02');")]
    public void QueriesThatAreNotGates_AreSilent(string sql)
    {
        Assert.Empty(GateErrors(Document(new[] { Step("S01", SyntheticS01), Step("S02", SyntheticS02), Step("S03", sql) })));
    }
}
