using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [이름 블록 · 공통 규약] <c>CheckUndefinedSqlBlockReference</c> 가 정의를 단계 절 안에서만 찾아, 골격 공통 규약이 정의한 저널·RunId 블록을
/// 부르기만 한 단계를 재생성시켰다 — POQSettleBatch16 에서 하한 재생성 16 이 전부 이 사유였고 사본 45 가 공통 정의와 똑같았다.
/// 판독: docs/audit-reports/2026-09-14-이름블록-공통규약-사전선언.md
/// </summary>
public sealed class NamedBlockSharedConventionsTests
{
    private const string UndefinedMarker = "정의돼 있지 않습니다";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "named-block", name));

    private static BatchStepPlan Step(string code, string procedure) =>
        new(code, "이름 블록", new[] { procedure }, Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>());

    private static List<string> NamedBlockErrors(string markdown, BatchStepPlan step, string? sharedConventions) =>
        new MechanicalValidator()
            .ValidateBatchStep(markdown, step, Array.Empty<string>(), new Dictionary<string, SpecConditions>(), sharedConventions: sharedConventions)
            .Errors.Where(e => e.Contains(UndefinedMarker)).ToList();

    private static List<string> NamedInError(string error) =>
        Regex.Matches(error.Split(UndefinedMarker)[1].Split(". 호출한")[0], @"`(SQL_[A-Za-z0-9_]+)`").Select(m => m.Groups[1].Value).ToList();

    private static readonly BatchStepPlan B16S04 = Step("S04", "dbo.UP_Util_PG_Client_CMRate_Ins");
    private static readonly BatchStepPlan B1S08 = Step("S08", "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC");
    private static readonly BatchStepPlan B1S05 = Step("S05", "dbo.UP_UTIL_SETTLE_INS_EXTRA");

    // N1: B16 S04 첫 초안은 공통 규약의 저널 블록 셋을 부르기만 했다 — 공통 규약과 함께면 침묵.
    [Fact]
    public void FirstDraftCallingJournalBlocksDefinedInSharedConventions_IsSilent()
    {
        var draft = Fixture("Batch16-S04-first-draft.md");
        var conventions = BatchPlanAssembler.ExtractSharedConventions(Fixture("Batch16-skeleton.md"));

        var without = NamedBlockErrors(draft, B16S04, sharedConventions: null);
        Assert.Single(without);
        Assert.Equal(new[] { "SQL_INSERT_STEP_START", "SQL_MARK_STEP_SUCCEEDED", "SQL_MARK_STEP_FAILED" }, NamedInError(without[0]));

        Assert.Empty(NamedBlockErrors(draft, B16S04, conventions));
    }

    // N2: 공통 규약에도 없는 이름은 계속 지목하되, 공통 규약이 정의한 이름은 빠진다.
    [Fact]
    public void NamesMissingFromBothTheSectionAndSharedConventions_AreStillReportedAlone()
    {
        var errors = NamedBlockErrors(Fixture("Batch1-S08.md"), B1S08, Fixture("Batch1-01-step-contract.md"));

        Assert.Single(errors);
        var named = NamedInError(errors[0]);
        Assert.Equal(18, named.Count);
        Assert.DoesNotContain("SQL_CURRENT_RUN_ID", named);
        Assert.Equal(
            new[] { "SQL_U1_PROMOTION_DISCOUNT", "SQL_U2_PG_COMM_REAPPLY", "SQL_U3_KFTC_CLIENT_SECTION_RATE", "SQL_U4_CLIENT_MIN_COMM", "SQL_U5_KFTC_PG_MIN_COMM" },
            named.Take(5));
    }

    // N3: 공통 규약 의사코드의 queryScalar(SQL_CURRENT_RUN_ID 호출은 정의가 아니다 — 정의 줄을 지우면 다시 지목된다.
    [Fact]
    public void ACallInSharedConventionsIsNotADefinition()
    {
        var contract = Fixture("Batch1-01-step-contract.md");
        Assert.Contains("queryScalar(SQL_CURRENT_RUN_ID", contract);
        var withoutDefinition = Regex.Replace(contract, @"^-- SQL_CURRENT_RUN_ID[^\n]*\n", string.Empty, RegexOptions.Multiline);
        Assert.NotEqual(contract, withoutDefinition);

        var errors = NamedBlockErrors(Fixture("Batch1-S08.md"), B1S08, withoutDefinition);

        Assert.Single(errors);
        Assert.Contains("SQL_CURRENT_RUN_ID", NamedInError(errors[0]));
    }

    // N9(최종 리뷰 C1): 공통 규약의 템플릿 블록(`<TargetTable>` 같은 자리표시자)은 정의가 아니다 - 구현자는 테이블·필터가 빠진 모양만 받는다.
    [Fact]
    public void TemplateBlocksInSharedConventionsAreNotDefinitions()
    {
        var section = Fixture("Batch1-S05.md");
        var contract = Fixture("Batch1-01-step-contract.md");
        Assert.Empty(NamedBlockErrors(section, B1S05, contract));

        var withoutOwnDefinitions = Regex.Replace(
            section, @"^-- (SQL_CREATE_AND_CAPTURE_SHADOW|SQL_DELETE_CHUNK|SQL_INSERT_CHUNK)\b[^\n]*\n", string.Empty, RegexOptions.Multiline);
        Assert.NotEqual(section, withoutOwnDefinitions);

        var errors = NamedBlockErrors(withoutOwnDefinitions, B1S05, contract);

        Assert.Single(errors);
        Assert.Equal(new[] { "SQL_CREATE_AND_CAPTURE_SHADOW", "SQL_DELETE_CHUNK", "SQL_INSERT_CHUNK" }, NamedInError(errors[0]));
    }

    // N10(최종 리뷰 C1): 공통 규약의 이름은 토큰으로 맞춘다 - `SQL_MARK_STEP_FAILED_V2` 정의가 `SQL_MARK_STEP_FAILED` 호출을 덮지 않는다.
    [Fact]
    public void ALongerNameInSharedConventionsDoesNotDefineItsPrefix()
    {
        var conventions = BatchPlanAssembler.ExtractSharedConventions(Fixture("Batch16-skeleton.md"));
        var renamed = Regex.Replace(conventions, @"^-- SQL_MARK_STEP_FAILED\s*$", "-- SQL_MARK_STEP_FAILED_V2", RegexOptions.Multiline);
        Assert.NotEqual(conventions, renamed);

        var errors = NamedBlockErrors(Fixture("Batch16-S04-first-draft.md"), B16S04, renamed);

        Assert.Single(errors);
        Assert.Equal(new[] { "SQL_MARK_STEP_FAILED" }, NamedInError(errors[0]));
    }

    // N4: 재료가 없으면 종전 동작.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutSharedConventions_TheFirstDraftStillFires(string? conventions)
    {
        Assert.Single(NamedBlockErrors(Fixture("Batch16-S04-first-draft.md"), B16S04, conventions));
    }

    // N5: 오케스트레이터가 단계 생성에 쓴 공통 규약을 단계 검증에도 넘긴다.
    [Fact]
    public void Orchestrator_PassesTheSameConventionsToStepValidation()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));
        var call = Regex.Match(source, @"_validator\.ValidateBatchStep\((?<args>.*?)\);", RegexOptions.Singleline);

        Assert.True(call.Success);
        // 주석 줄을 지우고 본다 - 주석에 인자 이름을 적기만 해도 통과하면 잠금이 아니다.
        var code = Regex.Replace(call.Groups["args"].Value, @"//[^\n]*", string.Empty);
        Assert.Matches(new Regex(@"\bsharedConventions:\s*conventions\b"), code);
    }

    // N5: 스윕 서비스는 SweepJob.SharedConventions 로 같은 판정을 한다.
    [Fact]
    public void Sweep_UsesTheJobsSharedConventions()
    {
        var steps = new[] { B16S04 };
        var markdown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["S04"] = Fixture("Batch16-S04-first-draft.md") };
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conventions = BatchPlanAssembler.ExtractSharedConventions(Fixture("Batch16-skeleton.md"));

        int Fired(string? shared) => StepSweepService.Sweep(new SweepInput(
                new[] { new SweepJob("POQSettleBatch16", steps, markdown, Array.Empty<(string, string)>(), empty, empty) { SharedConventions = shared } },
                Array.Empty<string>(), 0))
            .Findings.Count(f => f.Condition == SweepCondition.AsIs && f.Message.Contains(UndefinedMarker));

        Assert.Equal(1, Fired(null));
        Assert.Equal(0, Fired(conventions));
    }

    // N5: 스윕 명령은 배송 단계 파일과 짝이 맞는 배송 번들의 공통 규약을 읽는다.
    [Fact]
    public void SweepCommand_ReadsTheShippedStepContract()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.FindRepoRoot(), "src", "ReSet.Cli", "SweepCommand.cs"));

        var code = Regex.Replace(source, @"//[^\n]*", string.Empty);
        Assert.Matches(new Regex(@"Path\.Combine\(jobDir,\s*""agent"",\s*""common"",\s*""01-step-contract\.md""\)"), code);
        Assert.Matches(new Regex(@"SharedConventions\s*=\s*File\.Exists\(stepContractPath\)\s*\?\s*File\.ReadAllText\(stepContractPath\)"), code);
    }
}
