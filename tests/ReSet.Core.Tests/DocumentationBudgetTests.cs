using System.Text;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class DocumentationBudgetTests
{
    [Fact]
    public void MeasureBytes_CountsUtf8BytesNotCharacters()
    {
        // 예산의 단위는 바이트다. 문자 수로 재면 한글 문서의 실제 컨텍스트
        // 비용을 3분의 1로 과소평가한다.
        Assert.Equal(2, DocumentationBudget.MeasureBytes("ab"));
        Assert.Equal(6, DocumentationBudget.MeasureBytes("한글"));
    }

    [Fact]
    public void FindOversizedLines_ReportsOnlyTheLinesOverBudget()
    {
        var text = "짧은 줄\n" + new string('x', 700) + "\n짧은 줄";

        var found = DocumentationBudget.FindOversizedLines(text, 600);

        var only = Assert.Single(found);
        Assert.Equal(2, only.Line);
        Assert.Equal(700, only.Bytes);
    }

    [Fact]
    public void FindOversizedLines_HandlesCrLfWithoutCountingTheCarriageReturn()
    {
        var text = "a\r\n" + new string('x', 601);

        var found = DocumentationBudget.FindOversizedLines(text, 600);

        var only = Assert.Single(found);
        Assert.Equal(2, only.Line);
        Assert.Equal(601, only.Bytes);
    }

    [Fact]
    public void FindOversizedLines_DoesNotSplitASurrogatePairInTheExcerpt()
    {
        // AGENTS.md의 헤딩에는 이모지가 흔하다. 발췌를 문자 수로 자르면 서러게이트
        // 쌍 가운데가 잘려, 실패 메시지에 깨진 문자가 실린다.
        var line = new string('a', 59) + "\U0001F6A8" + new string('b', 700);

        var found = DocumentationBudget.FindOversizedLines(line, 600);

        var excerpt = Assert.Single(found).Excerpt;
        Assert.Equal(excerpt, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(excerpt)));
    }
}
