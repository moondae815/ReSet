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
        public void Render_SpecMissingCount_ShouldCarryTransitionalStateFootnote()
        {
            // 설계서 §3의 전이 상태. 명세서가 캐시 버전보다 오래된 판이면 「명세서
            // 결함」이 크게 나오는데, 이것이 회귀가 아니라 재생성으로 사라질 예정된
            // 중간 상태라는 것을 각주가 밝혀야 한다.
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 0) }, "T");

            Assert.Contains(
                "명세서가 현재 캐시 버전보다 오래된 판이면 「명세서 결함」이 크게 나옵니다", html);
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

        // ------------------------------------------------------------------
        // 가독성 계약 — 2026-08-25
        //
        // 실물(dbo.UP_Util_PG_Client_CMRate_Ins, 234줄)을 브라우저로 띄워 잰 값이
        // 근거다. 아래 여섯은 그때 확인된 결함을 각각 하나씩 잠근다.
        // ------------------------------------------------------------------

        /// <summary>
        /// 강조하려던 줄이 제일 안 읽히는 역전을 잠근다.
        ///
        /// [무엇이 이 테스트를 깨뜨리는가] 막대 조각(.seg)과 코드 줄(.row)이 CSS 규칙
        /// 하나를 공유하던 시절, `.st-ok { background: var(--ok) }` 한 줄이 234줄짜리
        /// SP의 정합 줄을 통째로 진초록(#1a7f37)으로 덮었다. 글자색은 --fg(#1a1a1a)
        /// 그대로여서 실측 대비가 약 2:1 — WCAG AA(4.5:1)에 한참 못 미친다. 규칙을
        /// 다시 합치면 이 테스트가 깨진다.
        /// </summary>
        [Fact]
        public void Render_StatementRow_ShouldUseTintBackgroundNotSolidFillColor()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            var rowRule = Regex.Match(html, @"\.row\.st-ok\s*\{[^}]*\}");
            Assert.True(rowRule.Success, ".row.st-ok 전용 스타일 블록을 찾지 못했다");
            // 줄 배경은 틴트 토큰이어야 한다 - 채움색(--ok)을 배경으로 직접 쓰면 안 된다.
            Assert.DoesNotContain("background: var(--ok)", rowRule.Value);
            Assert.Contains("--ok-tint", rowRule.Value);
            // 좌측 테두리는 채움색 그대로여야 상태가 눈에 띈다.
            Assert.Contains("border-left: 4px solid var(--ok)", rowRule.Value);

            // 막대 조각은 반대로 채워야 맞다 - 틴트로 바뀌면 막대가 사라진다.
            var segRule = Regex.Match(html, @"\.seg\.st-ok\s*\{[^}]*\}");
            Assert.True(segRule.Success, ".seg.st-ok 전용 스타일 블록을 찾지 못했다");
            Assert.Contains("background: var(--ok)", segRule.Value);
        }

        /// <summary>
        /// 코드 행간이 두 배로 벌어지던 결함을 잠근다.
        ///
        /// [원인] .row가 display:block이라 줄바꿈은 이미 보장되는데, writer가
        /// AppendLine으로 넣은 개행을 &lt;pre&gt;가 한 번 더 렌더해 줄마다 빈 줄이
        /// 하나씩 꼈다. 실측: 234줄 SP가 9,793px. 10MB짜리 Job 맵에서는 이 배율이
        /// 그대로 스크롤 비용이 된다.
        /// </summary>
        [Fact]
        public void Render_DdlBlock_ShouldNotEmitNewlinesBetweenRows()
        {
            var coverage = new ObjectCoverage(
                "dbo.A", "line1\nline2\nline3\n", new List<StatementCoverage>(), 1);

            var html = CoverageMapHtmlWriter.Render(new[] { coverage }, "T");

            var block = Regex.Match(html, "<pre class=\"ddl\">(.*?)</pre>", RegexOptions.Singleline);
            Assert.True(block.Success, "<pre class=\"ddl\"> 블록을 찾지 못했다");
            // pre는 공백을 그대로 렌더한다 - 줄 사이에 개행이 있으면 빈 줄이 된다.
            Assert.DoesNotContain("</span>\n", block.Value);
        }

        /// <summary>
        /// 제목·각주까지 고정폭 한글로 나오던 결함을 잠근다. monospace가 필요한 것은
        /// 원본 DDL(.ddl)뿐이고, 설명 문장은 산세리프여야 자간이 정상으로 읽힌다.
        /// </summary>
        [Fact]
        public void Render_ProseFont_ShouldDifferFromCodeFont()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            var bodyRule = Regex.Match(html, @"(?<![\w.-])body\s*\{[^}]*\}");
            Assert.True(bodyRule.Success, "body 스타일 블록을 찾지 못했다");
            Assert.DoesNotContain("monospace", bodyRule.Value);

            var ddlRule = Regex.Match(html, @"\.ddl\s*\{[^}]*\}");
            Assert.True(ddlRule.Success, ".ddl 스타일 블록을 찾지 못했다");
            Assert.Contains("monospace", ddlRule.Value);
        }

        /// <summary>
        /// 범례가 기호만 이고 색을 안 실으면 코드 줄의 색과 눈으로 연결되지 않는다.
        /// 색 칩을 더하되 기호는 그대로 남는다 - 색각 이상 리뷰어를 위한 원래 계약이
        /// 이 변경으로 후퇴하면 안 된다(파일 헤더 「왜 색만으로 구분하지 않는가」).
        /// </summary>
        [Fact]
        public void Render_Legend_ShouldCarryColorChipAlongsideSymbol()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            var legend = Regex.Match(html, "<p class=\"legend\">(.*?)</p>", RegexOptions.Singleline);
            Assert.True(legend.Success, "범례를 찾지 못했다");
            Assert.Contains("class=\"chip st-ok\"", legend.Value);
            Assert.Contains("class=\"chip st-missing\"", legend.Value);
            // 기호 병기는 유지된다.
            Assert.Contains("■", legend.Value);
            Assert.Contains("▲", legend.Value);
            // 마지막 항목 뒤에 구분자가 남으면 안 된다.
            Assert.DoesNotContain("&middot; </p>", legend.Value);
        }

        /// <summary>
        /// 필터를 걸어도 어느 필터가 켜져 있는지 화면에 표시가 없던 결함, 그리고
        /// 근거 패널을 한 번 열면 닫을 방법이 없던 결함을 함께 잠근다.
        /// </summary>
        [Fact]
        public void Render_Script_ShouldMarkActiveFilterAndAllowClosingEvidence()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            // 활성 필터 - 스타일과 토글이 둘 다 있어야 화면에 나타난다.
            Assert.Contains("button.filter.active", html);
            Assert.Contains("classList.add('active')", html);
            // 닫기 - 버튼과 Esc 두 경로.
            Assert.Contains("evidence-close", html);
            Assert.Contains("Escape", html);
        }

        /// <summary>
        /// 근거 패널의 닫기 버튼을 서버 렌더로 넣으면 #evidence가 영영 :empty가 아니게
        /// 되어, 빈 패널이 화면 아래를 항상 가린다(Render_EvidencePanel_ShouldHideWhenEmpty가
        /// 지키려던 것이 무력해진다). 버튼은 스크립트가 채울 때 만들어야 한다.
        /// </summary>
        [Fact]
        public void Render_EvidencePanel_ShouldBeEmptyAtRenderTime()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            Assert.Contains("<div id=\"evidence\"></div>", html);
        }

        /// <summary>
        /// 31개 객체가 실린 10MB 문서에서 pane을 스크롤하는 동안 "지금 어느 객체인지"가
        /// 사라지던 결함을 잠근다. 상단 고정 툴바가 앵커 점프의 제목을 가리지 않도록
        /// scroll-margin도 함께 요구한다.
        /// </summary>
        [Fact]
        public void Render_LongDocument_ShouldKeepObjectNameAndFiltersOnScreen()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            var toolbarRule = Regex.Match(html, "#toolbar\\s*\\{[^}]*\\}");
            Assert.True(toolbarRule.Success, "#toolbar 스타일 블록을 찾지 못했다");
            Assert.Matches("position:\\s*fixed", toolbarRule.Value);
            Assert.Contains("<div id=\"toolbar\">", html);

            var headingRule = Regex.Match(html, @"\.pane\s+h2\s*\{[^}]*\}");
            Assert.True(headingRule.Success, ".pane h2 스타일 블록을 찾지 못했다");
            Assert.Matches("position:\\s*sticky", headingRule.Value);

            Assert.Contains("scroll-margin-top", html);
        }

        /// <summary>
        /// 줄 번호(.ln)와 상태 기호(.sym)는 &lt;i&gt; 요소로 나간다 - 브라우저 기본값이
        /// 이탤릭이라 스타일이 빠지면 고정폭 숫자가 통째로 기울어 열이 어긋나 보인다.
        /// 상태 기호(■▲◆)는 색각 계약이 기대는 유일한 구분자라 더더욱 또렷해야 한다.
        /// </summary>
        [Fact]
        public void Render_LineNumberAndSymbol_ShouldNotInheritItalicFromIElement()
        {
            var html = CoverageMapHtmlWriter.Render(new[] { Sample("dbo.A", 1) }, "T");

            var lnRule = Regex.Match(html, @"\.ln\s*\{[^}]*\}");
            Assert.True(lnRule.Success, ".ln 스타일 블록을 찾지 못했다");
            Assert.Contains("font-style: normal", lnRule.Value);

            var symRule = Regex.Match(html, @"\.sym\s*\{[^}]*\}");
            Assert.True(symRule.Success, ".sym 스타일 블록을 찾지 못했다");
            Assert.Contains("font-style: normal", symRule.Value);
        }
    }
}
