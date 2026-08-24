using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 커버리지 판정을 자립형 HTML 한 장으로 렌더한다.
    ///
    /// [왜 외부 자원을 하나도 안 쓰는가] 이 파일은 메일로 넘겨지고 망 분리 환경에서
    /// 열린다. CDN 하나가 걸리면 회의실에서 빈 화면이 뜬다.
    ///
    /// [왜 색만으로 구분하지 않는가] 색각 이상이 있는 리뷰어에게 빨강과 초록은 같은
    /// 회색이다. 상태마다 기호를 병기한다.
    ///
    /// [두 축을 나란히 두는 이유 - 2026-08-24 실측이 뒤집었다] 같은 코퍼스가 문장 수
    /// 기준으로는 🟧가 압도적이고 라인 가중으로는 🟩가 압도적이다 - 🟧 상위 유형
    /// (RETURN·SET·ROLLBACK·DECLARE)이 대부분 한 줄짜리고 🟩는 여러 줄 DML
    /// 덩어리이기 때문이다. 라인 가중(외부 보고용 - "내 SP 본문을 얼마나 봐줬나")과
    /// 문장 수(내부 백로그용 - "다음에 무엇을 기계 확정 표로 넓힐까")는 서로 다른
    /// 질문에 답한다. 하나만 보이면 오독된다.
    ///
    /// [보안] 이 HTML에는 원본 SP 전문이 그대로 실린다. 로컬 파일로만 쓰고,
    /// 외부 호스팅에 올릴지는 사람이 그때 판단한다. 이 클래스는 파일만 만든다.
    /// </summary>
    public static class CoverageMapHtmlWriter
    {
        private static readonly IReadOnlyDictionary<CoverageState, (string Symbol, string Label, string Css)> Legend =
            new Dictionary<CoverageState, (string, string, string)>
            {
                [CoverageState.Consistent] = ("■", "정합", "st-ok"),
                [CoverageState.SpecMissing] = ("▲", "명세서 결함", "st-missing"),
                [CoverageState.ProseOnly] = ("◆", "산문만", "st-prose"),
                [CoverageState.OutOfScope] = ("·", "관할 밖", "st-out")
            };

        private static readonly CoverageState[] AllStates =
        {
            CoverageState.Consistent, CoverageState.SpecMissing,
            CoverageState.ProseOnly, CoverageState.OutOfScope
        };

        public static string Render(IReadOnlyList<ObjectCoverage> objects, string title)
        {
            ArgumentNullException.ThrowIfNull(objects);
            ArgumentNullException.ThrowIfNull(title);

            var ordered = objects
                .OrderByDescending(o => o.Count(CoverageState.SpecMissing))
                .ThenBy(o => o.ObjectName, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"ko\"><head><meta charset=\"utf-8\">");
            sb.AppendLine($"<title>{Encode(title)}</title>");
            sb.AppendLine("<style>");
            AppendStyle(sb);
            sb.AppendLine("</style></head><body>");

            AppendSummary(sb, ordered);
            AppendObjectList(sb, ordered);
            foreach (var o in ordered) AppendObjectPane(sb, o);

            sb.AppendLine("<div id=\"evidence\"></div>");
            sb.AppendLine("<script>");
            AppendScript(sb);
            sb.AppendLine("</script></body></html>");
            return sb.ToString();
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value) ?? string.Empty;

        private static int LineWeight(StatementCoverage s) => s.Statement.EndLine - s.Statement.StartLine + 1;

        /// <summary>
        /// 줄 번호(1-based) → 그 줄을 덮는 잎 문장. 어떤 잎에도 안 속한 줄은 없다(무채색).
        /// 컨테이너를 뺐으므로 잎끼리는 겹치지 않는다.
        /// </summary>
        private static Dictionary<int, StatementCoverage> BuildLineMap(ObjectCoverage o)
        {
            var map = new Dictionary<int, StatementCoverage>();
            foreach (var s in o.Statements)
            {
                for (var line = s.Statement.StartLine; line <= s.Statement.EndLine; line++)
                {
                    map[line] = s;
                }
            }
            return map;
        }

        private static void AppendStyle(StringBuilder sb)
        {
            sb.AppendLine(@"
:root {
  --bg: #ffffff; --fg: #1a1a1a; --muted: #6b6b6b; --panel: #f5f5f5; --border: #d8d8d8;
  --ok: #1a7f37; --missing: #c53030; --prose: #2b6cb0; --out: #8a8a8a;
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg: #14161a; --fg: #e6e6e6; --muted: #9a9a9a; --panel: #1d2025; --border: #33363b;
    --ok: #58c76a; --missing: #ff7b7b; --prose: #6fb2ff; --out: #a6a6a6;
  }
}
body { background: var(--bg); color: var(--fg); font-family: Consolas, 'Malgun Gothic', monospace; margin: 0; }
h2, h3 { color: var(--fg); }
#summary, #list, .pane { padding: 12px; border-bottom: 1px solid var(--border); }
#list { display: flex; flex-direction: column; gap: 6px; }
.obj-link { color: var(--fg); text-decoration: none; display: block; }
.obj-bar { display: flex; height: 8px; width: 100%; background: var(--panel); border-radius: 3px; overflow: hidden; }
.seg { display: inline-block; height: 100%; }
.st-ok, .seg.st-ok { border-left: 4px solid var(--ok); background: var(--ok); }
.st-missing, .seg.st-missing { border-left: 4px solid var(--missing); background: var(--missing); }
.st-prose, .seg.st-prose { border-left: 4px solid var(--prose); background: var(--prose); }
.st-out, .seg.st-out { border-left: 4px solid var(--out); background: var(--out); }
.ddl { white-space: pre; font-family: inherit; }
.row { display: block; padding-left: 4px; border-left: 4px solid transparent; cursor: default; }
.row.st-ok, .row.st-missing, .row.st-prose, .row.st-out { cursor: pointer; }
.row.hidden { display: none; }
.ln { color: var(--muted); font-style: normal; margin-right: 4px; }
.sym { margin-right: 4px; }
.fold { margin: 8px 0; }
.dual-axis { margin: 8px 0; }
.axis-line { margin: 4px 0; }
#evidence { padding: 12px; border-top: 2px solid var(--border); white-space: pre-wrap; }
button.filter { margin-right: 6px; }
");
        }

        private static void AppendSummary(StringBuilder sb, IReadOnlyList<ObjectCoverage> ordered)
        {
            var all = ordered.SelectMany(o => o.Statements).ToList();
            var total = all.Count;

            sb.AppendLine("<section id=\"summary\">");
            sb.AppendLine($"<h1>{Encode("커버리지 맵")}</h1>");
            sb.AppendLine($"<p>잎 문장 총계: {total}</p>");
            sb.AppendLine("<p class=\"legend\">");
            foreach (var state in AllStates)
            {
                var (symbol, label, _) = Legend[state];
                var count = all.Count(s => s.State == state);
                sb.Append($"{symbol} {Encode(label)} {count} &middot; ");
            }
            sb.AppendLine("</p>");

            sb.AppendLine("<p>");
            foreach (var state in AllStates)
            {
                sb.Append($"<button class=\"filter\" data-filter=\"{state}\">{Legend[state].Symbol}만</button>");
            }
            sb.AppendLine("<button class=\"filter\" data-filter=\"all\">전체</button>");
            sb.AppendLine("</p>");

            AppendDualAxis(sb, all);
            sb.AppendLine("</section>");
        }

        /// <summary>
        /// (가) 문장 수 기준과 라인 가중 기준을 나란히 낸다. 한쪽만 내면 오독된다 -
        /// 문장 수로는 🟧가 커 보이고 라인 가중으로는 🟩가 커 보이는 코퍼스가
        /// 실측으로 확인됐다(2026-08-24 Task 4).
        /// </summary>
        private static void AppendDualAxis(StringBuilder sb, IReadOnlyList<StatementCoverage> all)
        {
            var totalLeaf = all.Count;
            var totalLineWeight = all.Sum(LineWeight);

            sb.AppendLine("<div class=\"dual-axis\">");
            sb.AppendLine("<h3>커버리지 두 축</h3>");
            AppendAxisLine(sb, all, "라인 가중", "외부 보고용 — 내 SP 본문을 얼마나 봐줬나",
                totalLineWeight, LineWeight);
            AppendAxisLine(sb, all, "문장 수", "내부 백로그용 — 다음에 무엇을 기계 확정 표로 넓힐까",
                totalLeaf, _ => 1);
            sb.AppendLine("</div>");
        }

        private static void AppendAxisLine(
            StringBuilder sb, IReadOnlyList<StatementCoverage> all,
            string axisName, string purpose, int denominator, Func<StatementCoverage, int> weight)
        {
            var parts = AllStates.Select(state =>
            {
                var (symbol, label, _) = Legend[state];
                var numerator = all.Where(s => s.State == state).Sum(weight);
                var pct = denominator == 0 ? 0.0 : numerator * 100.0 / denominator;
                return $"{symbol} {Encode(label)} {pct.ToString("F1", CultureInfo.InvariantCulture)}%";
            });

            sb.AppendLine(
                $"<p class=\"axis-line\"><strong>{Encode(axisName)}</strong> " +
                $"({Encode(purpose)}): {string.Join(" &middot; ", parts)}</p>");
        }

        private static void AppendObjectList(StringBuilder sb, IReadOnlyList<ObjectCoverage> ordered)
        {
            sb.AppendLine("<nav id=\"list\">");
            foreach (var o in ordered)
            {
                var total = o.LeafCount;
                sb.Append($"<a class=\"obj-link\" href=\"#pane-{Encode(o.ObjectName)}\">");
                sb.Append($"<span class=\"obj-name\">{Encode(o.ObjectName)}</span> ");
                sb.Append($"<span class=\"obj-missing\">▲{o.Count(CoverageState.SpecMissing)}</span>");
                sb.Append("<span class=\"obj-bar\">");
                foreach (var state in AllStates)
                {
                    var count = o.Count(state);
                    if (count == 0) continue;
                    var pct = total == 0 ? 0.0 : count * 100.0 / total;
                    var (symbol, label, css) = Legend[state];
                    sb.Append(
                        $"<i class=\"seg {css}\" style=\"width:{pct.ToString("F1", CultureInfo.InvariantCulture)}%\" " +
                        $"title=\"{symbol} {Encode(label)} {count}\"></i>");
                }
                sb.AppendLine("</span></a>");
            }
            sb.AppendLine("</nav>");
        }

        private static void AppendObjectPane(StringBuilder sb, ObjectCoverage o)
        {
            var map = BuildLineMap(o);
            var lines = o.DdlText.Replace("\r\n", "\n").Split('\n');

            sb.AppendLine($"<section class=\"pane\" id=\"pane-{Encode(o.ObjectName)}\">");
            sb.AppendLine($"<h2>{Encode(o.ObjectName)}</h2>");
            sb.AppendLine($"<p class=\"kinds\">읽은 표 종수: {o.TableKindsRead}</p>");
            AppendOutOfScopeFold(sb, o);
            sb.AppendLine("<pre class=\"ddl\">");

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNo = i + 1;
                var text = Encode(lines[i]);

                if (!map.TryGetValue(lineNo, out var s))
                {
                    sb.AppendLine($"<span class=\"row\"><i class=\"ln\">{lineNo}</i> <i class=\"sym\">&nbsp;</i>{text}</span>");
                    continue;
                }

                var (symbol, _, css) = Legend[s.State];
                var evidence = Encode(
                    string.Join("\n", s.Anchors.Select(a => $"[{a.Source}] {a.RowText}")));
                var comments = Encode(
                    string.Join("\n", s.CommentAnchors.Select(a => $"[{a.Source}] {a.RowText}")));
                var known = s.IsKnownUncovered ? " data-known=\"알려진 사각지대\"" : string.Empty;

                sb.AppendLine(
                    $"<span class=\"row {css}\" data-state=\"{s.State}\" data-evidence=\"{evidence}\" " +
                    $"data-comment=\"{comments}\"{known}>" +
                    $"<i class=\"ln\">{lineNo}</i> <i class=\"sym\">{symbol}</i>{text}</span>");
            }

            sb.AppendLine("</pre></section>");
        }

        /// <summary>🟧을 문장 유형별로 접는다. 접지 않으면 SET 대입 수십 개가 목록을 덮는다.</summary>
        private static void AppendOutOfScopeFold(StringBuilder sb, ObjectCoverage o)
        {
            var groups = o.Statements
                .Where(s => s.State == CoverageState.OutOfScope)
                .GroupBy(s => s.Statement.StatementType)
                .OrderByDescending(g => g.Count())
                .ToList();
            if (groups.Count == 0) return;

            var total = groups.Sum(g => g.Count());
            sb.AppendLine($"<details class=\"fold\"><summary>· 관할 밖 {total}</summary><ul>");
            foreach (var g in groups)
            {
                var known = g.Any(s => s.IsKnownUncovered) ? " <em>알려진 사각지대</em>" : string.Empty;
                sb.AppendLine(
                    $"<li>{g.Count()} &middot; {Encode(g.Key)}{known}</li>");
            }
            sb.AppendLine("</ul></details>");
        }

        private static void AppendScript(StringBuilder sb)
        {
            sb.AppendLine(@"
document.addEventListener('click', function (e) {
  var row = e.target.closest('.row[data-state]');
  if (row) {
    var box = document.getElementById('evidence');
    var html = '<h3>근거</h3><div>' + (row.getAttribute('data-evidence') || '(근거 없음)') + '</div>';
    var comment = row.getAttribute('data-comment');
    if (comment) {
      html += '<h4>원본 주석(참고)</h4><div>' + comment + '</div>';
    }
    var known = row.getAttribute('data-known');
    if (known) {
      html += '<p><strong>' + known + '</strong></p>';
    }
    box.innerHTML = html;
    return;
  }

  var btn = e.target.closest('button.filter');
  if (btn) {
    var filter = btn.getAttribute('data-filter');
    document.querySelectorAll('.row[data-state]').forEach(function (r) {
      if (filter === 'all' || r.getAttribute('data-state') === filter) {
        r.classList.remove('hidden');
      } else {
        r.classList.add('hidden');
      }
    });
  }
});
");
        }
    }
}
