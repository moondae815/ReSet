using System.Collections.Generic;
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CoverageMapHtmlWriterTests
    {
        private static ObjectCoverage Sample(string name, int specMissing)
        {
            var statements = new List<StatementCoverage>();
            for (var i = 0; i < specMissing; i++)
            {
                statements.Add(new StatementCoverage(
                    new DdlStatement(10 + i, 10 + i, "UpdateStatement", 1, false),
                    CoverageState.SpecMissing,
                    new[] { 10 + i },
                    new List<SpecAnchor>(),
                    new List<SpecAnchor>(),
                    false));
            }

            statements.Add(new StatementCoverage(
                new DdlStatement(1, 1, "DeleteStatement", 1, false),
                CoverageState.Consistent,
                new[] { 1 },
                new List<SpecAnchor> { new(1, "표: 집합 술어", "| DELETE 1 | 1 | PGNAME |", false) },
                new List<SpecAnchor>(),
                false));

            return new ObjectCoverage(name, "line1\nline2\nline3\n", statements, 3);
        }

        [Fact]
        public void Render_ShouldNotReferenceAnyExternalResource()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.DoesNotContain("http://", html);
            Assert.DoesNotContain("https://", html);
            Assert.DoesNotContain("<script src", html);
            Assert.DoesNotContain("<link rel=\"stylesheet\"", html);
        }

        [Fact]
        public void Render_ShouldSortObjectsBySpecMissingDescending()
        {
            var html = CoverageMapHtmlWriter.Render(
                new[] { Sample("dbo.Few", 1), Sample("dbo.Many", 5) }, "T");

            Assert.True(html.IndexOf("dbo.Many") < html.IndexOf("dbo.Few"),
                "🟥이 많은 객체가 먼저 와야 한다");
        }

        [Fact]
        public void Render_ShouldCarrySymbolsNotOnlyColors()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.Contains("■", html);
            Assert.Contains("▲", html);
            Assert.Contains("◆", html);
        }

        [Fact]
        public void Render_ShouldEmbedAnchorRowTextAsEvidence()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 0) }, "T");

            Assert.Contains("PGNAME", html);
        }

        [Fact]
        public void Render_ShouldEscapeHtmlInDdl()
        {
            var coverage = new ObjectCoverage(
                "dbo.A", "SELECT * FROM T WHERE A < 1 AND B > 2\n",
                new List<StatementCoverage>(), 0);

            var html = CoverageMapHtmlWriter.Render(new[] { coverage }, "T");

            Assert.Contains("&lt;", html);
            Assert.Contains("&gt;", html);
        }

        [Fact]
        public void Render_ShouldReportTableKindsReadPerObject()
        {
            // 함정 회귀: DdlText가 3줄이면 TableKindsRead=3과 우연히 같은 숫자라
            // "3"이 그냥 줄 번호로 이미 나온다 - 그 상태로는 이 검사가 구현을 지우고도
            // 통과한다. 줄 수(2)와 표 종수(7)를 일부러 다르게 둬 라벨 문구까지 고정한다.
            var coverage = new ObjectCoverage("dbo.K", "x\ny\n", new List<StatementCoverage>(), 7);

            var html = CoverageMapHtmlWriter.Render(new[] { coverage }, "T");

            Assert.Contains("읽은 표 종수: 7", html);
        }

        [Fact]
        public void Render_ShouldSupportDarkMode()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.Contains("prefers-color-scheme", html);
        }

        [Fact]
        public void Render_ShouldFoldOutOfScopeByStatementType()
        {
            // 설계서 §2: 접지 않으면 SET 대입 수십 개가 목록을 덮어 신호가 죽는다.
            var statements = new List<StatementCoverage>();
            for (var i = 0; i < 12; i++)
            {
                statements.Add(new StatementCoverage(
                    new DdlStatement(i + 1, i + 1, "SetVariableStatement", 1, false),
                    CoverageState.OutOfScope,
                    System.Array.Empty<int>(),
                    new List<SpecAnchor>(), new List<SpecAnchor>(), false));
            }
            statements.Add(new StatementCoverage(
                new DdlStatement(20, 20, "ExecuteStatement", 1, false),
                CoverageState.OutOfScope,
                System.Array.Empty<int>(),
                new List<SpecAnchor>(), new List<SpecAnchor>(), false));

            var html = CoverageMapHtmlWriter.Render(
                new[] { new ObjectCoverage("dbo.A", "x\n", statements, 1) }, "T");

            // 유형과 개수가 함께 보여야 한다.
            Assert.Contains("SetVariableStatement", html);
            Assert.Contains("12", html);
            Assert.Contains("ExecuteStatement", html);
        }

        [Fact]
        public void Render_KnownUncoveredMerge_ShouldBeLabelledSeparately()
        {
            // 설계서 §2: 몰라서 빈 것과 알고 비운 것은 다른 사실이다.
            var merge = new StatementCoverage(
                new DdlStatement(5, 9, "MergeStatement", 1, false),
                CoverageState.OutOfScope,
                System.Array.Empty<int>(),
                new List<SpecAnchor>(), new List<SpecAnchor>(),
                IsKnownUncovered: true);

            var html = CoverageMapHtmlWriter.Render(
                new[] { new ObjectCoverage("dbo.A", "a\nb\nc\nd\ne\nf\ng\nh\ni\n", new[] { merge }, 1) },
                "T");

            Assert.Contains("알려진 사각지대", html);
        }

        [Fact]
        public void Render_ShouldShowStatementCountAndLineWeightedAxesSeparately()
        {
            // (가) Task 4 실측: 같은 코퍼스가 문장 수 기준과 라인 가중 기준에서
            // 정반대 그림을 낸다. 한 축만 남기거나 두 축이 같은 계산을 공유하면
            // 아래 네 숫자 중 최소 하나는 사라진다 - 그러면 이 테스트가 깨진다.
            var big = new StatementCoverage(
                new DdlStatement(1, 10, "InsertStatement", 1, false),
                CoverageState.Consistent,
                new[] { 1 },
                new List<SpecAnchor> { new(1, "표: 집합 술어", "| INSERT 1 | 1 | X |", false) },
                new List<SpecAnchor>(),
                false);

            var smalls = Enumerable.Range(11, 4).Select(line => new StatementCoverage(
                new DdlStatement(line, line, "SetVariableStatement", 1, false),
                CoverageState.OutOfScope,
                System.Array.Empty<int>(),
                new List<SpecAnchor>(), new List<SpecAnchor>(), false)).ToList();

            var statements = new List<StatementCoverage> { big };
            statements.AddRange(smalls);

            var ddl = string.Join("\n", Enumerable.Range(1, 14).Select(i => $"line{i}")) + "\n";
            var coverage = new ObjectCoverage("dbo.C", ddl, statements, 1);

            var html = CoverageMapHtmlWriter.Render(new[] { coverage }, "T");

            // 문장 수 기준: 정합 1/5=20.0%, 관할 밖 4/5=80.0%
            Assert.Contains("20.0%", html);
            Assert.Contains("80.0%", html);
            // 라인 가중: 정합 10/14=71.4%, 관할 밖 4/14=28.6%
            Assert.Contains("71.4%", html);
            Assert.Contains("28.6%", html);
            // 각 축의 용도를 밝히는 문구
            Assert.Contains("외부 보고용", html);
            Assert.Contains("내부 백로그용", html);
        }

        [Fact]
        public void Render_AnchorRowTextWithEscapedPipe_ShouldStayVerbatimInEvidencePanel()
        {
            // (나) 이월 항목 D2: RowText가 실제로 렌더되는 자리가 여기다.
            // 언이스케이프(\| -> |)되거나 큰따옴표가 인코딩 안 돼 속성을 깨뜨리면
            // 이 테스트가 깨진다 - RowText를 그대로 다시 파싱/가공하지 않는 한
            // 통과할 수 없다.
            var anchor = new SpecAnchor(
                5, "표: 원본 주석 기록",
                "| 77 | -- SET FLAGS \\| 4 AND Nm = \"PG\" |", true);
            var statement = new StatementCoverage(
                new DdlStatement(5, 5, "SetVariableStatement", 1, false),
                CoverageState.ProseOnly,
                System.Array.Empty<int>(),
                new List<SpecAnchor> { anchor },
                new List<SpecAnchor>(),
                false);
            var coverage = new ObjectCoverage(
                "dbo.B", "a\nb\nc\nd\ne\n", new List<StatementCoverage> { statement }, 1);

            var html = CoverageMapHtmlWriter.Render(new[] { coverage }, "T");

            // 원문 보존: 백슬래시-파이프가 그대로 남아야 한다.
            Assert.Contains("FLAGS \\| 4", html);
            // 언이스케이프됐다면 이 형태(백슬래시 소실)로 나타난다 - 나오면 안 된다.
            Assert.DoesNotContain("FLAGS | 4", html);
            // 원문의 큰따옴표는 속성을 깨지 않도록 반드시 인코딩된다.
            Assert.DoesNotContain("Nm = \"PG\"", html);
            Assert.Contains("&quot;PG&quot;", html);
        }
    }
}
