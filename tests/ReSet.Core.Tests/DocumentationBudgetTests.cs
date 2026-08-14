using System.Collections.Generic;
using System.IO;
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
        // \r은 CRLF에서 항상 \n 앞에 오므로, 긴 줄 자체가 \r\n으로 끝나야 \r 제거를
        // 실제로 검증한다. 뒤따르는 줄이 아니라 이 줄 끝에 \r이 남는다 - 여기서
        // 세지 않으면 601이 아니라 602가 된다.
        var text = new string('x', 601) + "\r\na";

        var found = DocumentationBudget.FindOversizedLines(text, 600);

        var only = Assert.Single(found);
        Assert.Equal(1, only.Line);
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

    // 라인 예산. 실제 병리는 4,162바이트짜리 "목록 항목"이었다.
    private const int MaxLineBytes = 600;

    private const string Routing =
        "이 문장을 어긴 코드가 나왔을 때 무엇이 그것을 잡습니까?\n" +
        "  테스트가 잡는다        → 규칙 한 줄 + 테스트 이름만 남기십시오\n" +
        "  그 파일 여는 사람만    → 해당 클래스의 <summary>로 옮기십시오\n" +
        "  여러 파일을 함께 봐야  → docs/architecture.md §4.x로 옮기십시오\n" +
        "  사람의 판단만이 잡는다 → AGENTS.md에 남을 자격이 있습니다\n";

    [Fact]
    public void NoAutoLoadedDocumentExceedsItsByteBudget()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var failures = new StringBuilder();

        foreach (var (relativePath, budget) in ReadBaseline(repoRoot))
        {
            var actual = DocumentationBudget.MeasureBytes(
                File.ReadAllText(Path.Combine(repoRoot, relativePath)));

            if (actual <= budget) continue;

            failures.AppendLine($"{relativePath}: 상한 {budget:N0} 바이트, 실제 {actual:N0} 바이트 ({actual - budget:N0} 초과)");
        }

        Assert.True(
            failures.Length == 0,
            "자동 로드되는 문서가 크기 예산을 넘었습니다.\n\n" + failures + "\n" + Routing);
    }

    [Fact]
    public void NoAutoLoadedDocumentHasAnOversizedLine()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var failures = new StringBuilder();

        foreach (var (relativePath, _) in ReadBaseline(repoRoot))
        {
            var oversized = DocumentationBudget.FindOversizedLines(
                File.ReadAllText(Path.Combine(repoRoot, relativePath)), MaxLineBytes);

            foreach (var line in oversized)
            {
                failures.AppendLine($"{relativePath}:{line.Line} — {line.Bytes:N0} 바이트 (상한 {MaxLineBytes})");
                failures.AppendLine($"  {line.Excerpt}");
            }
        }

        Assert.True(
            failures.Length == 0,
            $"목록 항목 하나가 {MaxLineBytes} 바이트를 넘었습니다. 항목이 아니라 문단입니다.\n\n"
            + failures + "\n" + Routing);
    }

    private static IEnumerable<(string RelativePath, int Budget)> ReadBaseline(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "tests", "ReSet.Core.Tests", "documentation-budget-baseline.txt");

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.LastIndexOf('=');
            Assert.True(separator > 0, $"기준선 파일의 형식이 잘못되었습니다: {raw}");

            yield return (line[..separator].Trim(), int.Parse(line[(separator + 1)..].Trim()));
        }
    }
}
