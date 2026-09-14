using System;
using System.IO;
using System.Text.RegularExpressions;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [스윕 인공물] 번들 내보내기(<c>InstructionBundleWriter.BuildFloorBanner</c>)가 하한 미달·검증 불가 단계 파일 머리에 붙이는 배너를, 단계 검사 스윕이
/// 그대로 읽어 「<c>### </c> 헤딩으로 시작하지 않습니다」를 발화했다 — 판 안의 검사는 배너 붙기 전 섹션을 봐서 발화하지 않는다.
/// Batch13 발화 9 중 4 · Batch14 3 중 2 · Batch15 5 중 2 가 이것이었고, 코퍼스 배너 단계는 36 이다. 픽스처는 배송본 바이트 그대로다.
/// </summary>
public sealed class StepFileBannerStripTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "t25", name));

    [Theory]
    [InlineData("Batch15-S01.md", "### S01")]   // 검증 불가 배너
    [InlineData("Batch14-S01.md", "### S01")]   // 품질 미달 배너
    public void RealBanneredStepFile_StartsWithItsHeadingAfterStripping(string fixture, string heading)
    {
        var original = Fixture(fixture);
        Assert.StartsWith("> ⚠️ **이 단계는", original.TrimStart('﻿'));

        Assert.StartsWith(heading, InstructionBundleWriter.StripFloorBanner(original));
    }

    [Fact]
    public void StepFileWithoutBanner_IsUnchanged()
    {
        const string section = "### S02 실행 등록\n\n본문.\n";
        Assert.Equal(section, InstructionBundleWriter.StripFloorBanner(section));
    }

    // 섹션 자신의 인용문은 배너가 아니다 - 배너 머리 문구로만 판정한다.
    [Fact]
    public void ASectionThatBeginsWithItsOwnBlockquote_IsUnchanged()
    {
        const string section = "> 참고: 이 단계는 원본 두 프로시저를 합친다.\n\n### S03 합산\n";
        Assert.Equal(section, InstructionBundleWriter.StripFloorBanner(section));
    }

    // 벗긴 뒤 단계 검사가 헤딩을 본다 - 스윕이 판 안과 같은 입력을 받는다.
    [Fact]
    public void AfterStripping_TheHeadingCheckIsSilent()
    {
        var step = new BatchStepPlan("S01", "사전 검증", Array.Empty<string>(), Array.Empty<string>(), new[] { "-9010" }, false, Array.Empty<string>());
        var stripped = InstructionBundleWriter.StripFloorBanner(Fixture("Batch15-S01.md"));

        var result = new MechanicalValidator().ValidateBatchStep(stripped, step, Array.Empty<string>(), new System.Collections.Generic.Dictionary<string, SpecConditions>());

        Assert.DoesNotContain(result.Errors, e => e.Contains("헤딩으로 시작하지 않습니다"));
    }

    // [배선] 스윕이 단계 파일을 읽는 자리에서 벗긴다.
    [Fact]
    public void SweepCommand_StripsTheBannerWhenLoadingStepFiles()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.FindRepoRoot(), "src", "ReSet.Cli", "SweepCommand.cs"));
        Assert.Matches(new Regex(@"markdownByCode\[step\.Code\]\s*=\s*InstructionBundleWriter\.StripFloorBanner\(\s*File\.ReadAllText\(stepPath\)\s*\)"), source);
    }
}
