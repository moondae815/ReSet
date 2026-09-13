using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [K1] 단계 SQL 이 제어 계약 표를 비정본 이름(<see cref="BatchControlContract.FindAlias"/>)으로 부르는가.
///
/// POQSettleBatch11(2026-09-13): S13 은 정본 <c>batch.BatchControlTotal</c> 에 쓰고 S20 은 별칭 <c>batch.ControlTotal</c>
/// 에서 읽어, S20 의 대조가 어떤 실행에서도 짝을 못 찾았다. 계약은 그 별칭을 2026-08-24 부터 알았는데 제품 호출부가 0 이었다.
/// 픽스처는 배송본을 바이트 그대로 옮긴 것이다(<c>Fixtures/control-total/README.md</c>).
/// </summary>
public sealed class BatchControlTableAliasTests
{
    private const string Marker = "제어 계약 표의 비정본 이름";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "control-total", name));

    private static BatchStepPlan ControlStep(string code) => new(
        Code: code, Name: $"{code} 단계",
        LegacyProcedures: Array.Empty<string>(),
        TargetTables: new[] { "batch.BatchControlTotal" },
        ErrorCodes: new[] { "-9200" }, Chunkable: false, SchemaTables: Array.Empty<string>());

    private static StepValidationResult Validate(string markdown, string code) =>
        new MechanicalValidator().ValidateBatchStep(
            markdown, ControlStep(code), Array.Empty<string>(), new Dictionary<string, SpecConditions>());

    [Fact]
    public void RealStepReadingAndWritingTheAliasTable_IsReportedWithTheCanonicalName()
    {
        var result = Validate(Fixture("Batch11-S20.md"), "S20");

        var error = Assert.Single(result.Errors, e => e.Contains(Marker));
        Assert.Contains("`batch.ControlTotal`", error);
        Assert.Contains("`batch.BatchControlTotal`", error);
        // 단계 본문을 다시 쓰면 고쳐지는 결함이다 - 목차 결함으로 분류하면 재시도가 꺼진다.
        Assert.DoesNotContain(error, result.PlanDefects);
    }

    // S13 은 산문에서만 별칭을 말한다(「승인 단계 목록의 `batch.ControlTotal`은 논리 대상 명칭이며 …」).
    // 그 문장은 옳다 - 산문을 보면 이 검사가 옳은 단계를 고발한다.
    [Fact]
    public void RealStepThatOnlyMentionsTheAliasInProse_IsSilent()
    {
        var result = Validate(Fixture("Batch11-S13.md"), "S13");

        Assert.DoesNotContain(result.Errors, e => e.Contains(Marker));
    }

    // 양성 대조: 같은 S13 본문에서 정본 쓰기 한 곳을 대괄호 별칭으로 바꾸면 발화한다.
    // 위 시험이 조용한 것이 「이 검사가 S13 을 못 읽는다」가 아님을 보인다.
    [Fact]
    public void SameStepWithOneWriteSwitchedToTheBracketedAlias_IsReported()
    {
        var original = Fixture("Batch11-S13.md");
        var mutated = ReplaceFirst(original, "INSERT INTO batch.BatchControlTotal", "INSERT INTO [batch].[ControlTotal]");
        Assert.NotEqual(original, mutated);

        var result = Validate(mutated, "S13");

        Assert.Contains(result.Errors, e => e.Contains(Marker) && e.Contains("`batch.BatchControlTotal`"));
    }

    // SQL 주석 안의 언급은 코드가 아니다 - 검사는 주석·문자열을 지운 펜스를 본다.
    [Fact]
    public void AliasMentionedOnlyInsideASqlComment_IsSilent()
    {
        var original = Fixture("Batch11-S13.md");
        var mutated = ReplaceFirst(original, "INSERT INTO batch.BatchControlTotal",
            "-- 목차의 batch.ControlTotal 은 이 표의 논리 명칭이다\nINSERT INTO batch.BatchControlTotal");
        Assert.NotEqual(original, mutated);

        var result = Validate(mutated, "S13");

        Assert.DoesNotContain(result.Errors, e => e.Contains(Marker));
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }
}
