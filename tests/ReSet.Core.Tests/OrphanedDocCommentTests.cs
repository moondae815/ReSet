using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class OrphanedDocCommentTests
{
    [Fact]
    public void StackedSummaries_AreReported()
    {
        var source = string.Join("\n",
            "        /// <summary>앞 멤버를 설명하던 것.</summary>",
            "        /// <summary>뒤 멤버를 설명하는 것.</summary>",
            "        public const string X = \"x\";");

        var offender = Assert.Single(OrphanedDocCommentScanner.ScanSource(source, "Fake.cs"));
        Assert.Equal(2, offender.Line);
    }

    [Fact]
    public void MultiLineStackedSummaries_AreReported()
    {
        // 실물은 여러 줄에 걸친다 - 닫는 줄과 여는 줄이 각각 독립된 `///` 줄이다.
        var source = string.Join("\n",
            "        /// <summary>",
            "        /// 앞 멤버.",
            "        /// </summary>",
            "        /// <summary>",
            "        /// 뒤 멤버.",
            "        /// </summary>",
            "        private static void M() { }");

        var offender = Assert.Single(OrphanedDocCommentScanner.ScanSource(source, "Fake.cs"));
        Assert.Equal(4, offender.Line);
    }

    [Fact]
    public void SeparateBlocks_AreNotReported()
    {
        // 사이에 비-`///` 줄이 있으면 별개의 주석이라 한 멤버에 겹쳐 붙지 않는다.
        var source = string.Join("\n",
            "        /// <summary>앞 멤버.</summary>",
            "        private static void A() { }",
            "",
            "        /// <summary>뒤 멤버.</summary>",
            "        private static void B() { }");

        Assert.Empty(OrphanedDocCommentScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void ParamAndReturnsTags_DoNotTriggerIt()
    {
        // `</summary>` 뒤에 오는 것이 `<summary>`가 아니면 정상이다 - 한 멤버의
        // 문서가 summary + param + returns 로 이어지는 것은 이 저장소의 흔한 모양이다.
        var source = string.Join("\n",
            "        /// <summary>설명.</summary>",
            "        /// <param name=\"x\">인자.</param>",
            "        /// <returns>결과.</returns>",
            "        private static int M(int x) => x;");

        Assert.Empty(OrphanedDocCommentScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Repository_HasNoNewOrphanedDocComments()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var baseline = ReadBaseline(repoRoot);

        var actual = OrphanedDocCommentScanner.ScanRepository(repoRoot)
            .GroupBy(o => o.File.Replace(Path.DirectorySeparatorChar, '/'))
            .ToDictionary(g => g.Key, g => g.ToList());

        var failures = new StringBuilder();

        foreach (var (file, offenders) in actual.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var allowed = baseline.TryGetValue(file, out var n) ? n : 0;
            if (offenders.Count <= allowed) continue;

            failures.AppendLine(
                $"{file}: 상한 {allowed}, 실제 {offenders.Count} — "
                + string.Join(", ", offenders.Select(o => o.Line.ToString())) + "행");
        }

        Assert.True(failures.Length == 0,
            "한 문서 주석에 <summary>가 둘 이상인 자리가 늘었습니다. C#은 연속된 /// 줄을 "
            + "하나의 주석으로 묶어 다음 멤버에 붙이므로, 앞 블록이 설명하던 멤버는 문서를 "
            + "잃고 그 근거가 엉뚱한 멤버에 실립니다. 빌드 경고가 나지 않아 조용합니다.\n\n"
            + failures
            + "\n새 멤버를 기존 문서 블록과 그 멤버 사이에 끼워 넣지 마십시오. "
            + "블록은 자기 멤버 바로 위에 있어야 합니다.\n\n"
            + "짚이는 데가 없으면 다른 세션을 의심하십시오 — 이 검사는 작업 트리를 "
            + "그대로 읽으므로, 같은 체크아웃에서 누가 편집 중이면 반쯤 쓰인 파일을 "
            + "함께 봅니다. 단독 실행(--filter)으로 재현되지 않고 재실행에서 통과하면 "
            + "그 경우입니다. (미커밋 변경을 제외하지는 않습니다 — 그러면 정작 지금 "
            + "고치는 사람의 파일을 안 보게 됩니다.)");
    }

    /// <summary>
    /// 파일별 상한. 이 게이트는 단방향이다 — 초과만 실패하고 밑으로는 자유다.
    /// 파일 헤더의 판단(양방향 잠금은 사람을 길들여 파일을 무시하게 만든다)을 따른다.
    /// </summary>
    private static Dictionary<string, int> ReadBaseline(string repoRoot)
    {
        var path = Path.Combine(
            repoRoot, "tests", "ReSet.Core.Tests", "orphaned-doc-comment-baseline.txt");
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.LastIndexOf('=');
            Assert.True(separator > 0, $"기준선 파일의 형식이 잘못되었습니다: {raw}");

            entries[line[..separator].Trim()] = int.Parse(line[(separator + 1)..].Trim());
        }

        // 이 게이트는 기준선을 전적으로 신뢰한다. 파일이 비거나 항목이 조용히 지워지면
        // 상한이 0이 되어 오히려 조여지지만, 반대로 누군가 큰 수를 적어 두면 검사가
        // 사실상 꺼진다. 그래서 합계에 천장을 둔다 - 2026-08-27 실측이 14다.
        var total = entries.Values.Sum();
        Assert.True(total <= 14,
            $"기준선 합계가 {total}입니다. 이 파일은 **줄어들기만** 해야 합니다 - "
            + "숫자를 올려 새 부채를 통과시키지 마십시오. 늘려야 할 진짜 이유가 있으면 "
            + "이 천장(14)도 함께 사람이 고쳐야 하고, 그것이 이 단언의 요점입니다.");

        return entries;
    }
}
