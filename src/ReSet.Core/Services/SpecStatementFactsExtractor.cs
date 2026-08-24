using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    public sealed record SpecDmlRow(
        string Kind,
        int Ordinal,
        int SourceLine,
        string TargetTable,
        IReadOnlyList<string> PredicateColumns,
        IReadOnlyList<string> JoinKeys,
        IReadOnlyList<string> GroupBy,
        IReadOnlyList<string> OrderBy);

    public sealed record SpecSetTarget(int Ordinal, string TargetTable, IReadOnlyList<string> Columns);

    public sealed record SpecLocalVariable(string Name, string TypeOrKind, bool IsSystemValue);

    public sealed record SpecStatementFacts(
        IReadOnlyList<SpecDmlRow> DmlRows,
        IReadOnlyList<SpecSetTarget> SetTargets,
        IReadOnlyList<SpecLocalVariable> LocalVariables);

    /// <summary>
    /// 명세서의 기계 확정 표를 읽어 단계 검사가 쓸 사실로 만든다.
    ///
    /// [왜 필요한가 - POQSettleBatch1 축 B 감사 실측]
    /// ValidateBatchStep이 받는 기준값은 목차와 조건 컬럼 목록뿐이라, 명세서가
    /// 확정한 UPDATE 15개 중 10개를 단계가 통째로 빼먹어도 통과했다(S07 🔴).
    /// 대조가 "문서 어딘가에 이 컬럼이 있나" 수준이라 YMD가 42곳에 흩어진 문서는
    /// 갱신 13의 최상위 WHERE에서 YMD가 빠져도 통과했다(S07 🟠).
    ///
    /// [열 순서에 기대지 않는 이유]
    /// DML 범위 표의 열은 회차마다 늘었다(GROUP BY·ORDER BY가 나중에 붙었다).
    /// 인덱스로 읽으면 열이 하나 늘 때 모든 칸이 한 칸씩 밀려 조용히 오독한다.
    /// </summary>
    public static class SpecStatementFactsExtractor
    {
        private const string DmlScopeHeading = "### DML 범위 (기계 확정 — 수정 금지)";
        private const string LocalVariableHeading = "### 지역 변수 및 시스템 값";
        private const string SystemValueMarker = "SQL Server 시스템 값";

        private static readonly Regex UpdateSectionPattern = new(
            @"^###\s+(?<kind>UPDATE|INSERT|DELETE)\s+대상 테이블:\s*(?<table>[^\(]+?)\s*\(\s*(?:갱신|삽입|삭제)\s*(?<ordinal>\d+)",
            RegexOptions.Compiled);

        private static readonly Regex StatementCellPattern = new(
            @"^(?<kind>UPDATE|INSERT|DELETE|SELECT)\s+(?<ordinal>\d+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 표 구분선 칸("- - -" 정렬 표기)만 여기에 걸린다. 콜론은 정렬 표시(`:---`,
        // `---:`)라 함께 허용한다.
        private static readonly Regex SeparatorCellPattern = new(@"^:?-+:?$", RegexOptions.Compiled);

        public static IReadOnlyDictionary<string, SpecStatementFacts> Extract(
            IReadOnlyList<(string FileName, string Content)> specs)
        {
            var result = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase);
            if (specs == null) return result;

            foreach (var (fileName, content) in specs)
            {
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(content)) continue;

                // 한 명세서가 못 읽혀도 나머지는 읽는다 - 재료가 통째로 비면
                // 검사가 전부 침묵해 결함이 소리 없이 통과한다.
                try
                {
                    var lines = MarkdownSectionLocator.SplitLines(content);
                    result[fileName] = new SpecStatementFacts(
                        ReadDmlRows(lines),
                        ReadSetTargets(lines),
                        ReadLocalVariables(lines));
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "명세서 기계 확정 표를 읽지 못했습니다 - Spec: {Spec}", fileName);
                }
            }

            return result;
        }

        private static IReadOnlyList<SpecDmlRow> ReadDmlRows(IReadOnlyList<string> lines)
        {
            var rows = new List<SpecDmlRow>();
            var table = ReadTable(lines, DmlScopeHeading);
            if (table == null) return rows;

            int Col(params string[] fragments) => FindColumn(table.Value.Header, fragments);

            var iStatement = Col("문장");
            var iLine = Col("라인");
            var iTarget = Col("대상");
            var iPredicate = Col("술어 컬럼");
            var iJoin = Col("조인 키");
            var iGroup = Col("GROUP BY");
            var iOrder = Col("ORDER BY");
            if (iStatement < 0 || iTarget < 0) return rows;

            foreach (var cells in table.Value.Rows)
            {
                var statement = Cell(cells, iStatement);
                var match = StatementCellPattern.Match(statement);
                if (!match.Success) continue;

                rows.Add(new SpecDmlRow(
                    match.Groups["kind"].Value.ToUpperInvariant(),
                    int.Parse(match.Groups["ordinal"].Value),
                    int.TryParse(Cell(cells, iLine), out var line) ? line : 0,
                    BareName(Cell(cells, iTarget)),
                    SplitColumns(Cell(cells, iPredicate)),
                    SplitColumns(Cell(cells, iJoin)),
                    SplitColumns(Cell(cells, iGroup)),
                    SplitColumns(Cell(cells, iOrder))));
            }

            return rows;
        }

        private static IReadOnlyList<SpecSetTarget> ReadSetTargets(IReadOnlyList<string> lines)
        {
            var targets = new List<SpecSetTarget>();

            for (var i = 0; i < lines.Count; i++)
            {
                var match = UpdateSectionPattern.Match(lines[i]);
                if (!match.Success) continue;

                var ordinal = int.Parse(match.Groups["ordinal"].Value);
                var targetTable = BareName(match.Groups["table"].Value);

                // 이 절의 표만 읽는다. 다음 `### `를 만나면 끝이다.
                var end = lines.Count;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (lines[j].StartsWith("### ", StringComparison.Ordinal)) { end = j; break; }
                }

                var table = ReadTableInRange(lines, i + 1, end);
                if (table == null) continue;

                // 인덱스로 "두 번째 칸"을 집으면 안 된다 - "테이블명" 칸이 먼저 오고
                // "컬럼명" 칸은 그 다음이다. 헤더 이름으로 찾아야 표가 늘어도(예:
                // "비고" 칸 추가) 엉뚱한 칸(테이블명)을 컬럼으로 오독하지 않는다.
                var iColumn = FindColumn(table.Value.Header, "컬럼명");
                if (iColumn < 0) continue;

                var columns = table.Value.Rows
                    .Select(cells => Clean(Cell(cells, iColumn)))
                    .Where(c => c.Length > 0)
                    .ToList();

                if (columns.Count > 0) targets.Add(new SpecSetTarget(ordinal, targetTable, columns));
            }

            return targets;
        }

        private static IReadOnlyList<SpecLocalVariable> ReadLocalVariables(IReadOnlyList<string> lines)
        {
            var variables = new List<SpecLocalVariable>();
            var table = ReadTable(lines, LocalVariableHeading);
            if (table == null) return variables;

            var iName = FindColumn(table.Value.Header, "명칭");
            var iType = FindColumn(table.Value.Header, "데이터 타입", "구분");
            if (iName < 0) return variables;

            foreach (var cells in table.Value.Rows)
            {
                var name = Clean(Cell(cells, iName));
                if (!name.StartsWith("@", StringComparison.Ordinal)) continue;

                var type = Clean(Cell(cells, iType));
                variables.Add(new SpecLocalVariable(
                    name, type, type.Contains(SystemValueMarker, StringComparison.Ordinal)));
            }

            return variables;
        }

        private static (List<string> Header, List<List<string>> Rows)? ReadTable(
            IReadOnlyList<string> lines, string heading)
        {
            var start = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimEnd().Equals(heading, StringComparison.Ordinal)) { start = i; break; }
            }
            if (start < 0) return null;

            var end = lines.Count;
            for (var i = start + 1; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("### ", StringComparison.Ordinal)) { end = i; break; }
            }

            return ReadTableInRange(lines, start + 1, end);
        }

        // 표가 아닌 줄(헤딩과 표 사이의 빈 줄, 설명 산문)을 헤더로 오인하지 않도록
        // `|`로 시작하는 줄만 표 행으로 본다 - MechanicalValidator가 표를 찾을 때
        // 쓰는 것과 같은 판정이다(2218행 등). 이 판정이 없으면 헤딩 바로 다음의
        // 빈 줄이 SplitRow(1칸짜리 빈 문자열)로 "헤더"가 되어 진짜 헤더 행이
        // 데이터로 밀려 표 전체를 못 읽는다.
        private static (List<string> Header, List<List<string>> Rows)? ReadTableInRange(
            IReadOnlyList<string> lines, int start, int end)
        {
            List<string>? header = null;
            var rows = new List<List<string>>();

            for (var i = start; i < end; i++)
            {
                if (!lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal)) continue;

                var cells = MarkdownTableCellCodec.SplitRow(lines[i]);
                if (IsSeparator(cells)) continue;
                if (header == null) { header = cells; continue; }
                rows.Add(cells);
            }

            return header == null ? null : (header, rows);
        }

        // 헤더 칸은 회차마다 길어졌다("조인 키"가 "조인 키(등식)"이 된 적이 있다).
        // 포함으로 찾아야 그런 확장에 견딘다.
        private static int FindColumn(IReadOnlyList<string> header, params string[] fragments)
        {
            for (var i = 0; i < header.Count; i++)
            {
                if (fragments.All(f => header[i].Contains(f, StringComparison.OrdinalIgnoreCase))) return i;
            }
            return -1;
        }

        // `MarkdownTableCellCodec.SplitRow`는 맨 앞/뒤 `|` 때문에 매 행마다 빈 칸을
        // 하나씩 얹는다("| a | b |" → ["", "a", "b", ""]). 그 빈 칸은 대시를 포함하지
        // 않아 예전 판정("모든 칸이 대시")에 걸리면 구분선 자체가 구분선으로 안
        // 잡힌다 - 그러면 구분선이 데이터 행으로 들어가 ReadSetTargets가 ":---"를
        // 컬럼명으로 오독한다. 빈 칸은 무시하고, 값이 있는 칸만 대시(콜론 허용) 모양을
        // 요구한다.
        private static bool IsSeparator(IReadOnlyList<string> cells) =>
            cells.Any(c => c.Length > 0) &&
            cells.All(c => c.Length == 0 || SeparatorCellPattern.IsMatch(c.Trim()));

        private static string Cell(IReadOnlyList<string> cells, int index) =>
            index >= 0 && index < cells.Count ? cells[index] : string.Empty;

        private static IReadOnlyList<string> SplitColumns(string cell)
        {
            var cleaned = Clean(cell);
            if (cleaned.Length == 0 || cleaned == "(없음)" || cleaned == "—" || cleaned == "-")
            {
                return Array.Empty<string>();
            }

            return cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(BareName)
                .Where(c => c.Length > 0)
                .ToList();
        }

        // `A.YMD` → `YMD`. 별칭은 문서마다 다르고 대조에 쓸 수 없다.
        private static string BareName(string value)
        {
            var cleaned = Clean(value);
            var dot = cleaned.LastIndexOf('.');
            return dot >= 0 ? cleaned[(dot + 1)..] : cleaned;
        }

        private static string Clean(string value) =>
            (value ?? string.Empty).Trim().Trim('`', '*', ' ');
    }
}
