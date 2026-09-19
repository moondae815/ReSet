using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// 규칙: <c>NotifyStatus</c>에 싣는 자유 문자열은 <c>EscapeMarkup</c>을 거친다.
///
/// <para>
/// 이 싱크는 인자를 Spectre 마크업으로 파싱한다. AI 본문·검사 오류문·DB 객체 이름처럼
/// 우리가 쓰지 않은 글자가 그대로 들어가면, 그 안의 <c>[…]</c>가 스타일 태그로 읽힌다.
/// POQSettleBatch24(2026-09-19)가 그렇게 죽었다 - 하한 검사 발화가 인용한 AI 본문에
/// <c>S.[value]</c>가 있었다.
/// </para>
/// <para>
/// 기준선은 부채 목록이다. 줄 번호가 아니라 <b>파일·멤버·식</b>으로 적는다 - 줄 번호는
/// 편집마다 썩고, 식만 적으면 같은 이름이 다른 메서드에서 다른 것을 뜻할 때
/// (예: <c>reason</c>은 목차 재설계 라벨이기도 하고 검사 오류문이기도 하다)
/// 한쪽의 면제가 다른 쪽을 덮어 버린다.
/// </para>
/// <para>
/// 양방향으로 잠겨 있다 - 새 자리가 생겨도, 고친 자리를 기준선에서 안 지워도 실패한다.
/// </para>
/// </summary>
public sealed class MarkupEscapePolicyTests
{
    [Fact]
    public void Scanner_FlagsAnUnescapedHole()
    {
        var source = @"
class C
{
    void M(string reason) { _userInteraction.NotifyStatus($""[yellow]* 실패: {reason}[/]""); }
}";

        var hole = Assert.Single(MarkupEscapePolicyScanner.ScanSource(source, "Fake.cs"));

        Assert.Equal("M", hole.Member);
        Assert.Equal("reason", hole.Expression);
    }

    [Fact]
    public void Scanner_DoesNotFlagAnEscapedHole()
    {
        var source = @"
class C
{
    void M(string reason) { _userInteraction.NotifyStatus($""[yellow]* 실패: {EscapeMarkup(reason)}[/]""); }
}";

        Assert.Empty(MarkupEscapePolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_FlagsAConditionalThatEscapesOnlyOneBranch()
    {
        // 드문 갈래가 새면 늦게 터진다. 두 갈래가 모두 거쳐야 면제다.
        var source = @"
class C
{
    void M(bool b, string a, string c) { _userInteraction.NotifyStatus($""{(b ? EscapeMarkup(a) : c)}""); }
}";

        Assert.Single(MarkupEscapePolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagSinksThatEscapeEverythingThemselves()
    {
        // NotifyError는 구현부(ConsoleUserInteraction)가 메시지 전량을 이스케이프한다.
        var source = @"
class C
{
    void M(string reason) { _userInteraction.NotifyError($""실패: {reason}""); }
}";

        Assert.Empty(MarkupEscapePolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void EveryFreeFormHoleIsEscapedOrListedInTheBaseline()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var actual = MarkupEscapePolicyScanner
            .ScanDirectory(Path.Combine(repoRoot, "src"))
            .Select(MarkupEscapePolicyScanner.BaselineKey)
            .ToHashSet(StringComparer.Ordinal);

        var baselinePath = Path.Combine(
            repoRoot, "tests", "ReSet.Core.Tests", "markup-escape-policy-baseline.txt");
        var allowed = ReadBaseline(baselinePath);

        var failures = new StringBuilder();

        var unexpected = actual.Except(allowed, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();
        if (unexpected.Count > 0)
        {
            failures.AppendLine($"이스케이프 없이 마크업으로 들어가는 자리 {unexpected.Count}건이 새로 생겼습니다:");
            foreach (var key in unexpected) failures.AppendLine($"  {key}");
            failures.AppendLine(
                "  → EscapeMarkup(...)으로 감싸십시오. 그 값이 마크업을 일부러 싣는 것이라면 기준선에 추가하십시오.");
            failures.AppendLine();
        }

        var stale = allowed.Except(actual, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();
        if (stale.Count > 0)
        {
            failures.AppendLine($"기준선에 있으나 소스에 없는 자리 {stale.Count}건입니다:");
            foreach (var key in stale) failures.AppendLine($"  {key}");
            failures.AppendLine("  → 고쳤거나 옮겼다면 기준선에서 지우십시오.");
        }

        Assert.True(failures.Length == 0, "마크업 이스케이프 기준선과 어긋납니다.\n\n" + failures);
    }

    private static HashSet<string> ReadBaseline(string path)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            result.Add(line);
        }

        return result;
    }
}
