using ReSet.Cli;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class SpecHeaderReaderTests
{
    [Fact]
    public void Read_ParsesStatusAndScores()
    {
        var markdown = "---\n검증 상태: 통과 # 검증 파이프라인 종료 상태\n종합 신뢰도: 80 # 설명\n정합성 점수: 9/10 # 설명\n---\n\n# 본문";

        var header = SpecHeaderReader.Read(markdown);

        Assert.Equal("통과", header.VerificationStatus);
        Assert.Equal(80, header.NormalizedScore);
        Assert.Equal(9, header.Accuracy);
    }

    [Fact]
    public void Read_ReturnsNullStatusForLegacyDocumentWithoutTheField()
    {
        var markdown = "---\n종합 신뢰도: 70\n---\n\n# 본문";

        var header = SpecHeaderReader.Read(markdown);

        Assert.Null(header.VerificationStatus);
        Assert.Equal(70, header.NormalizedScore);
    }

    [Fact]
    public void Read_ReturnsEmptyHeaderWhenThereIsNoYamlBlock()
    {
        var header = SpecHeaderReader.Read("# 본문만 있는 문서");

        Assert.Null(header.VerificationStatus);
        Assert.Null(header.NormalizedScore);
    }

    [Fact]
    public void Read_ParsesReviewNotRunStatus()
    {
        var markdown = "---\n검증 상태: 리뷰 미수행 # 검증 파이프라인 종료 상태\n---\n\n# 본문";

        var header = SpecHeaderReader.Read(markdown);

        Assert.Equal("리뷰 미수행", header.VerificationStatus);
        Assert.Null(header.NormalizedScore);
    }

    [Fact]
    public void Read_ReturnsNullForAbsentSubScoreEvenWhenOverallScorePresent()
    {
        // 종합 신뢰도만 있고 개별 하위 점수(정합성/CRUD/가독성/예외)는 없는 문서.
        // 표시 계층(ConsoleUserInteraction)이 이 계약(부재 = null)에 의존해 자체 기본값을 적용하므로 고정해 둔다.
        var markdown = "---\n종합 신뢰도: 80\n---\n\n# 본문";

        var header = SpecHeaderReader.Read(markdown);

        Assert.Equal(80, header.NormalizedScore);
        Assert.Null(header.Accuracy);
        Assert.Null(header.Crud);
        Assert.Null(header.Readability);
        Assert.Null(header.Exception);
    }

    // 소비부(ConsoleUserInteraction.cs:105-109)는 파싱 실패를 만점으로 폴백한다.
    // 별칭 하나가 어긋나면 지어낸 10점이 진짜 점수와 섞여 표시되므로 전부 고정한다.

    [Theory]
    [InlineData("AiConfidenceScore")]
    [InlineData("종합 신뢰도 점수")]
    [InlineData("종합 신뢰도")]
    [InlineData("종합신뢰도")]
    public void Read_AcceptsEveryOverallScoreAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 80\n---\n\n# 본문");

        Assert.Equal(80, header.NormalizedScore);
    }

    [Theory]
    [InlineData("AccuracyScore")]
    [InlineData("정합성 점수")]
    [InlineData("정합성")]
    public void Read_AcceptsEveryAccuracyAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 9/10\n---\n\n# 본문");

        Assert.Equal(9, header.Accuracy);
    }

    [Theory]
    [InlineData("CrudScore")]
    [InlineData("CRUD 점수")]
    [InlineData("CRUD")]
    public void Read_AcceptsEveryCrudAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 8/10\n---\n\n# 본문");

        Assert.Equal(8, header.Crud);
    }

    [Theory]
    [InlineData("ReadabilityScore")]
    [InlineData("가독성 점수")]
    [InlineData("가독성")]
    public void Read_AcceptsEveryReadabilityAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 7/10\n---\n\n# 본문");

        Assert.Equal(7, header.Readability);
    }

    [Theory]
    [InlineData("ExceptionScore")]
    [InlineData("예외처리 점수")]
    [InlineData("예외처리")]
    [InlineData("예외 처리 점수")]
    [InlineData("예외 처리")]
    public void Read_AcceptsEveryExceptionAlias(string key)
    {
        var header = SpecHeaderReader.Read($"---\n{key}: 6/10\n---\n\n# 본문");

        Assert.Equal(6, header.Exception);
    }

    [Fact]
    public void Read_StripsCommentThenParenthesisThenDenominatorInThatOrder()
    {
        // 실제 산출물은 분모와 주석을 함께 싣는다
        // (VerificationDocumentFormatter: "정합성 점수: 9/10 # SQL 대비 기능 정합성").
        // 세 정규화가 이 순서로 적용되지 않으면 값이 어긋난다.
        var markdown =
            "---\n" +
            "종합 신뢰도: 80 # 100점 만점 기준 AI 최종 신뢰도\n" +
            "정합성 점수: 9/10 # SQL 대비 기능 정합성\n" +
            "CRUD 점수: 8 (양호)\n" +
            "가독성 점수: 7/10 (우수) # 코드 가독성 및 표준 준수\n" +
            "---\n\n# 본문";

        var header = SpecHeaderReader.Read(markdown);

        Assert.Equal(80, header.NormalizedScore);
        Assert.Equal(9, header.Accuracy);
        Assert.Equal(8, header.Crud);
        Assert.Equal(7, header.Readability);
    }
}
