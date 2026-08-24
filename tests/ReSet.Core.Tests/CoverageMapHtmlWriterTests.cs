using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
            // I9 (여섯 번째 무의미 테스트였다): 범례(#summary legend)와 필터 버튼은
            // 상태와 무관하게 기호를 항상 찍으므로, data-state 행 밖에서 기호를
            // 찾으면 <i class="sym">{symbol}</i>를 통째로 지워도 이 검사가 통과한다.
            // data-state="..." 행 안에서 상태별 기호를 찾도록 좁힌다(축 테스트가 이미
            // 쓰는 기법과 같다) - 그래야 실제 렌더된 줄이 기호를 잃으면 깨진다.
            // Sample()의 SpecMissing 라인(10+)은 DDL 3줄짜리 픽스처 범위 밖이라
            // <pre>에 아예 렌더되지 않으므로, 두 상태 모두 실제로 찍히는 전용
            // 픽스처를 쓴다.
            var consistent = new StatementCoverage(
                new DdlStatement(1, 1, "DeleteStatement", 1, false),
                CoverageState.Consistent,
                new[] { 1 },
                new List<SpecAnchor> { new(1, "표: 집합 술어", "| DELETE 1 | 1 | PGNAME |", false) },
                new List<SpecAnchor>(),
                false);
            var specMissing = new StatementCoverage(
                new DdlStatement(2, 2, "UpdateStatement", 1, false),
                CoverageState.SpecMissing,
                new[] { 2 },
                new List<SpecAnchor>(),
                new List<SpecAnchor>(),
                false);
            var coverage = new ObjectCoverage(
                "dbo.A", "line1\nline2\n", new List<StatementCoverage> { consistent, specMissing }, 1);

            var html = CoverageMapHtmlWriter.Render(new[] { coverage }, "T");

            var rows = Regex.Matches(html, "<span class=\"row[^\"]*\" data-state=\"([^\"]+)\"[^>]*>(.*?)</span>")
                .Select(m => (State: m.Groups[1].Value, Row: m.Groups[2].Value))
                .ToList();

            Assert.NotEmpty(rows);
            AssertRowCarriesSymbol(rows, nameof(CoverageState.Consistent), "■");
            AssertRowCarriesSymbol(rows, nameof(CoverageState.SpecMissing), "▲");
        }

        private static void AssertRowCarriesSymbol(
            List<(string State, string Row)> rows, string state, string symbol)
        {
            var row = Assert.Single(rows, r => r.State == state);
            Assert.Contains(symbol, row.Row);
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

            // Fix Round 1 리뷰 지적: html 전체에서 "20.0%"/"80.0%"를 찾으면
            // AppendObjectList의 막대 세그먼트 폭(count * 100.0 / o.LeafCount, 이
            // 픽스처에선 우연히 같은 값)이 새어 들어와도 통과한다 - 두 축을
            // 합쳐도(AppendAxisLine 두 번째 호출이 LineWeight를 쓰도록 바꿔도)
            // 이 테스트가 안 깨지는 원인이었다. class="axis-line" 문단만 오려내
            // 그 안에서만 값을 확인한다.
            var axisLines = Regex.Matches(html, "<p class=\"axis-line\">.*?</p>")
                .Select(m => m.Value)
                .ToList();
            Assert.Equal(2, axisLines.Count);

            var lineWeightedAxis = Assert.Single(axisLines, l => l.Contains("라인 가중"));
            var statementCountAxis = Assert.Single(axisLines, l => l.Contains("문장 수"));

            // 라인 가중 문단 안: 정합 10/14=71.4%, 관할 밖 4/14=28.6%
            Assert.Contains("71.4%", lineWeightedAxis);
            Assert.Contains("28.6%", lineWeightedAxis);
            Assert.Contains("외부 보고용", lineWeightedAxis);

            // 문장 수 문단 안: 정합 1/5=20.0%, 관할 밖 4/5=80.0%
            Assert.Contains("20.0%", statementCountAxis);
            Assert.Contains("80.0%", statementCountAxis);
            Assert.Contains("내부 백로그용", statementCountAxis);

            // 두 문단이 같은 계산을 공유하면(예: 둘 다 라인 가중으로 통일) 문단
            // 텍스트 자체가 같아진다 - 서로 달라야 한다.
            Assert.NotEqual(lineWeightedAxis, statementCountAxis);
        }

        [Fact]
        public void Render_EvidencePanel_ShouldBePinnedToViewportNotDocumentBottom()
        {
            // C1: #evidence가 모든 pane 뒤 마지막 요소로만 있으면, 10MB짜리 문서에서
            // 줄을 클릭해도 뷰포트에는 아무 변화가 없다(문서 맨 밑바닥이 갱신될 뿐).
            // 스크롤 위치와 무관하게 보이려면 CSS가 뷰포트에 고정해야 한다.
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            var styleMatch = Regex.Match(html, "#evidence\\s*\\{[^}]*\\}");
            Assert.True(styleMatch.Success, "#evidence 스타일 블록을 찾지 못했다");
            Assert.Matches("position:\\s*(sticky|fixed)", styleMatch.Value);
            Assert.Contains("bottom:", styleMatch.Value);
        }

        [Fact]
        public void Render_EvidencePanel_ShouldHideWhenEmpty()
        {
            // 뷰포트에 고정된 패널이 비어 있을 때도 자리를 차지하면 화면을 가린다.
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.Contains("#evidence:empty", html);
        }

        [Fact]
        public void Render_EvidenceScript_ShouldNotReparseDecodedRowTextAsHtml()
        {
            // I1: getAttribute('data-evidence')는 디코드된 원문을 준다. 그것을
            // innerHTML로 다시 파싱하면 렌더 시점의 Encode가 무효화되고, 'A<B' 같은
            // 술어 원문이 태그로 먹혀 근거가 조용히 사라진다. textContent로 대입해야
            // 원문이 보존된다.
            //
            // [Fix Round 2 재리뷰 지적] 옛 단언(`.innerHTML = html`·`.innerHTML=html`
            // 리터럴 검색)은 옛 한 줄짜리 구현("var html = ...; box.innerHTML = html;")
            // 하나만 겨눈 죽은 가드였다 - 새 DOM API 구조에서 `evidenceBody.innerHTML =
            // ...`처럼 특정 한 곳만 innerHTML로 되돌려도 문자열 "html"이라는 식별자가
            // 아예 없어 안 걸렸다(재현: textContent 6개 호출 중 5개가 남아
            // Contains("textContent")도 통과했다). `.innerHTML =` 대입 전부를
            // 정규식으로 잡아야 부분 되돌림도 걸린다.
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.DoesNotMatch(@"\.innerHTML\s*=", html);
        }

        [Fact]
        public void Render_SpecMissingCount_ShouldCarryScopeFootnote()
        {
            // I6: 「명세서 결함 0」에 단서가 없으면 - 판정이 앵커의 출처 표를
            // 구분하지 않는다는 사실이 화면에서 사라진다.
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 0) }, "T");

            Assert.Contains("출처 표", html);
        }

        [Fact]
        public void Render_LineWeightedAxis_ShouldShowDenominatorFootnote()
        {
            // I5: 라인 가중의 분모(잎 문장 줄 수 / 원본 전체 줄 수)가 안 보이면
            // "67.5%가 분모다" 같은 실측이 화면에서 사라진다.
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.Contains("분모:", html);
            Assert.Contains("원본", html);
        }

        [Fact]
        public void Render_TableKindsRead_ShouldCarryDefinitionFootnote()
        {
            // M4: 「읽은 표 종수」가 "라인 칸을 가진 표 종수"라는 정의 없이 나오면
            // "기계 확정 표 전체 종수"로 오독된다(참조 함수 표는 헤더가 '호출 위치'라
            // 여기서 안 세어진다).
            var coverage = new ObjectCoverage("dbo.K", "x\ny\n", new List<StatementCoverage>(), 7);

            var html = CoverageMapHtmlWriter.Render(new[] { coverage }, "T");

            Assert.Contains("라인", html);
            Assert.Contains("호출 위치", html);
        }

        [Fact]
        public void Render_ParseFailedObject_ShouldBeVisiblyFlaggedNotBlendedWithNormalObjects()
        {
            // I4: DdlText가 있는데 잎이 0이면 파스 실패의 확정 신호다. 막대 없는
            // "정상" 항목으로 섞이면 안 되고, 눈에 띄게 표시돼야 한다.
            var parseFailed = new ObjectCoverage(
                "dbo.Broken", "CREATE PROC ((( 이건 SQL이 아니다",
                new List<StatementCoverage>(), 0);

            var html = CoverageMapHtmlWriter.Render(new[] { parseFailed }, "T");

            Assert.True(parseFailed.ParseFailed);
            Assert.Contains("파스 실패", html);
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
