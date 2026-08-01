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
}
