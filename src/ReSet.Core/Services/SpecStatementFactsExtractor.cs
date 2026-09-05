using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;

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

    /// <summary>
    /// UPDATE 갱신 절의 SET 대상만 담는다 - INSERT·DELETE 절은 담지 않는다.
    ///
    /// [왜 UPDATE 전용인가 - 리뷰 라운드 1 실측]
    /// `output/Procedures/*/docs/Spec.md` 전체에서 `(삽입 N`·`(삭제 N`(서수 괄호)은
    /// 0건이다. INSERT·DELETE의 "대상 테이블" 제목은 서수 없이 나온다
    /// (`dbo.UP_Util_PG_Client_CMRate_Ins`·`dbo.UP_UTIL_SETTLE_CANCEL_INS`·
    /// `dbo.UP_UTIL_STAT_PGCOLLECT_INS` 등 11개 이상 파일). 그 표는 UPDATE 갱신 절과
    /// 모양도 다르다 - UPDATE는 "원천 표현식 (SET)" 열을 갖고 INSERT는 삽입 컬럼
    /// 매핑이다. 서수 없는 제목에 <see cref="Ordinal"/>을 억지로 만들어 붙이면 그
    /// 값이 무엇을 뜻하는지 아무도 모르게 되므로, 이 계약을 UPDATE 전용으로 좁힌다.
    /// </summary>
    /// <param name="Expressions">각 컬럼의 「원천 표현식 (SET)」 칸. <paramref name="Columns"/>와
    /// <b>자리를 맞춘다</b> — 같은 인덱스가 같은 행이다. 칸이 비어 있으면 빈 문자열로 남겨
    /// 자리를 지킨다(걸러 담으면 뒤 컬럼이 앞 컬럼의 산식을 갖게 된다). 표에 그 칸이 아예
    /// 없으면 전부 빈 문자열이다.
    ///
    /// [왜 표현식이 필요한가 - 축 B 감사 🔴]
    /// POQSettleBatch1/S07 의 결함은 컬럼 이름이 아니라 <b>우변 산식</b>의 소실이었다 —
    /// 상수·계수·부호·반올림 자릿수·UDF 인자. 옛 지시서는 컬럼 이름을 주석과 표에 적어 두고
    /// 산식만 뺐다. 실측(2026-09-05): 컬럼만으로 재면 통째로 빠진 갱신이 결함 문서와 정상
    /// 문서 양쪽 다 0 이라 <b>결함이 안 보인다.</b></param>
    public sealed record SpecSetTarget(
        int Ordinal, string TargetTable, IReadOnlyList<string> Columns, IReadOnlyList<string> Expressions);

    public sealed record SpecLocalVariable(string Name, string TypeOrKind, bool IsSystemValue);

    /// <param name="SetTargets">UPDATE 갱신 절의 SET 대상만 담는다. INSERT·DELETE
    /// 절은 담지 않는다 - 이유는 <see cref="SpecSetTarget"/> 참고.</param>
    public sealed record SpecStatementFacts(
        IReadOnlyList<SpecDmlRow> DmlRows,
        IReadOnlyList<SpecSetTarget> SetTargets,
        IReadOnlyList<SpecLocalVariable> LocalVariables)
    {
        /// <summary>
        /// 오류 코드 원문("-13") → 그 코드를 설정하는 문장의 (종류, 번호).
        ///
        /// [왜 이 방향인가] 단계 지시서는 코드를 갖고 번호를 찾는다.
        /// [중복 코드가 없는 이유] 같은 코드가 두 문장에 붙으면 귀속할 수 없으므로
        /// 아예 담지 않는다 - 덮어쓰면 둘 중 하나가 조용히 틀린 행과 대조된다.
        /// </summary>
        public IReadOnlyDictionary<string, (string Kind, int Ordinal)> ErrorCodeToOrdinal { get; init; }
            = new Dictionary<string, (string, int)>();
    }

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

        // 지역/내부 변수 표 헤딩은 코퍼스에서 갈린다.
        //
        // [조사 범위 - 라운드 2, `grep -rn "^###.*변수" output/Procedures/*/docs/Spec.md
        // output/Functions/*/docs/Spec.md output/External/*/*/*/docs/Spec.md`]
        // 라운드 1은 `^#.*지역\s*변수` 패턴만 훑어 "지역 변수" 계열만 찾았고 주석에
        // "이 네 가지가 전부다"라고 적었다 - 틀린 진술이었다(리뷰 Important로 지적됨).
        // "내부 변수" 계열은 그 패턴에 걸리지 않아 통째로 놓쳤고, 그 SP들(아래 4·5)은
        // 10~15개 이상의 Job 단계에서 참조되는데도 검사 D가 조용히 비활성이었다.
        // 라운드 2는 "변수"가 들어간 `###` 헤딩 전체를 훑었고, 그 결과 이 여섯
        // 가지가 전부다(Functions·External은 "지역 변수"·"내부 변수"를 산문으로만
        // 언급할 뿐 `###` 표 절 자체가 없다 - 같은 grep으로 확인, 0건):
        //   1. "### 지역 변수 및 시스템 값"(COMM_UPD·EXPECT_PROC)
        //   2. "### 지역 변수 및 시스템 상태값"(AcqManual)
        //   3. "### 지역 변수와 컬럼 매핑"(PROC_ETC - S14의 원천)
        //   4. "### 내부 변수"(INS_EXTRA,
        //      output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md:93)
        //   5. "### 내부 변수와 컬럼 관계"(SUMMARY_ETC,
        //      output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/docs/Spec.md:58)
        // 접두사를 "### 지역 변수"·"### 내부 변수" 둘로 넓힌다 - "### 지역"·
        // "### 내부"만으로 넓히면 무관한 절(가상의 "### 지역별 매출 요약",
        // "### 내부 통제 절차")까지 삼킬 수 있어 "변수"까지 반드시 포함한다.
        //
        // [알려진 한계 - 이번 라운드에서 손대지 않음]
        //   6. output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md:75도
        //      같은 "내부 변수 명칭" 표 모양을 담지만, 그 표 앞에 전용 `###`(또는
        //      `##`) 헤딩이 아예 없다 - `## 파라미터 목록`의 매개변수 표 바로 다음
        //      줄에 빈 줄 하나만 두고 곧장 이어진다. 헤딩 문자열이 없으니 이 절의
        //      시작을 앵커할 수 없다 - 절 경계 없이 표만으로 찾으면 "매개변수 명칭"
        //      표까지 "지역/내부 변수 표"로 잘못 삼킬 위험이 크다(그 표의 첫 칸도
        //      "명칭"을 포함해 iName 탐색에 우연히 걸린다). 그래서 이 SP는 여전히
        //      LocalVariables를 못 읽는다 - CheckSpecLocalVariablesDeclared는
        //      재료가 없으면 침묵하므로 거짓 오류는 안 나지만 검사 D가 비활성인
        //      채로 남는다. 헤딩 없는 절의 경계를 표 모양(헤더 칸 이름)만으로
        //      판별하는 별도 설계가 있어야 다음 라운드에서 닫을 수 있다.
        private static readonly string[] LocalVariableHeadingPrefixes = { "### 지역 변수", "### 내부 변수" };

        // 지역/시스템 값 구분 문구도 코퍼스에서 갈린다(같은 실측):
        //   "SQL Server 시스템 값"(COMM_UPD), "시스템 정수 값"(EXPECT_PROC),
        //   "시스템 상태값"(AcqManual, @@FETCH_STATUS)
        // 셋 다 "시스템"과 "값"을 함께 담는다 - 일반 SQL 타입 이름(INT·MONEY·
        // VARCHAR 등)은 한글을 담지 않으므로 이 두 조각을 함께 요구해도 정상
        // 변수를 시스템 값으로 오분류할 위험이 없다.
        //
        // [MechanicalValidator의 `@@` 접두사 이중 방어와의 관계]
        // 이 마커 일반화로도 코퍼스에 없는 네 번째 변형을 놓칠 수 있다. 그래서
        // MechanicalValidator.CheckSpecLocalVariablesDeclared는 이 IsSystemValue
        // 판정과 별개로 `@@` 접두사 이름을 항상 제외한다 - T-SQL 문법상 사용자가
        // DECLARE할 수 없는 시스템 전역값의 표식이라 여기 마커 목록이 어떤 변형을
        // 놓쳐도 거짓 오류로 이어지지 않는다. 두 방어는 서로의 대체가 아니라
        // 겹으로 쌓은 방어다 - 이 목록은 되도록 정확한 IsSystemValue를 내려 하고,
        // `@@` 방어는 그 판정이 틀려도 안전하도록 지킨다.
        private static readonly string[] SystemValueMarkerFragments = { "시스템", "값" };

        // UPDATE만 잡는다 - INSERT|DELETE로 넓히면 안 된다. 실물 코퍼스 실측:
        // `output/Procedures/*/docs/Spec.md` 전체에서 `(삽입 N`·`(삭제 N`(서수 괄호)은
        // 0건이고, INSERT·DELETE의 "대상 테이블" 제목은 서수 없이 나온다(11개 이상
        // 파일). 그리고 그 표는 UPDATE 갱신 절과 모양이 다르다 - UPDATE는
        // "원천 표현식 (SET)" 열을 갖고 INSERT는 삽입 컬럼 매핑이다. 서수 없는
        // 제목에 억지로 Ordinal을 만들어 붙이면 그 값이 무엇을 뜻하는지 아무도
        // 모르게 되므로, SpecSetTarget은 UPDATE 갱신 절 전용 계약으로 좁힌다.
        private static readonly Regex UpdateSectionPattern = new(
            @"^###\s+UPDATE\s+대상 테이블:\s*(?<table>[^\(]+?)\s*\(\s*갱신\s*(?<ordinal>\d+)",
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

                    // Task 17 C3 - 원문 FileName을 키로 쓰면 스키마 접두사 없는
                    // `step.LegacyProcedures` 항목(실측 314개 중 134개, 43%)이 영원히
                    // 못 찾는다. `CheckMissingConditionColumns`
                    // (MechanicalValidator.cs:1514)가 이미 `BareObjectName`으로 조회하는
                    // 것과 같은 규약을 여기서도 따른다 - 두 재료가 다른 키 규약을 쓰면
                    // 한쪽만 고쳐서는 조회가 여전히 어긋난다.
                    var key = MechanicalValidator.BareObjectName(fileName);
                    if (key.Length == 0) continue;

                    result[key] = new SpecStatementFacts(
                        ReadDmlRows(lines),
                        ReadSetTargets(lines),
                        ReadLocalVariables(lines))
                    {
                        ErrorCodeToOrdinal = ReadErrorCodeToOrdinal(lines),
                    };
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "명세서 기계 확정 표를 읽지 못했습니다 - Spec: {Spec}", fileName);
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

                // 표현식 칸이 없는 표도 있다(옛 세대·다른 모양). 그때는 빈 문자열로 채워
                // 자리만 지킨다 - 검사 쪽이 "재료가 없다"를 스스로 판정한다.
                var iExpression = FindColumn(table.Value.Header, "원천 표현식");

                // 컬럼과 표현식을 **한 번에** 걸러야 자리가 맞는다. 따로 Where 를 걸면
                // 산식 칸이 빈 행에서 짝이 어긋나 뒤 컬럼이 앞 컬럼의 산식을 갖는다.
                var picked = table.Value.Rows
                    .Select(cells => (
                        Column: Clean(Cell(cells, iColumn)),
                        Expression: iExpression >= 0 ? Clean(Cell(cells, iExpression)) : string.Empty))
                    .Where(row => row.Column.Length > 0)
                    .ToList();

                if (picked.Count > 0)
                {
                    targets.Add(new SpecSetTarget(
                        ordinal, targetTable,
                        picked.Select(row => row.Column).ToList(),
                        picked.Select(row => row.Expression).ToList()));
                }
            }

            return targets;
        }

        private static IReadOnlyList<SpecLocalVariable> ReadLocalVariables(IReadOnlyList<string> lines)
        {
            var variables = new List<SpecLocalVariable>();
            var table = ReadTable(lines,
                line => LocalVariableHeadingPrefixes.Any(prefix =>
                    line.TrimEnd().StartsWith(prefix, StringComparison.Ordinal)));
            if (table == null) return variables;

            var iName = FindColumn(table.Value.Header, "명칭");

            // 타입 헤더도 코퍼스에서 갈린다: "데이터 타입 또는 구분"(COMM_UPD)은
            // 두 조각을 모두 요구해도 되지만, "데이터 타입"(EXPECT_PROC·PROC_ETC)·
            // "데이터 타입 또는 종류"(AcqManual)는 "구분"이 없어 AND 조건에 걸린다.
            // 더 구체적인 후보를 먼저 시도하고, 실패하면 "데이터 타입" 단독으로
            // 넓힌다 - 다른 호출부(FindColumn(header, params string[]))의 AND
            // 의미는 그대로 둔 채 이 자리에서만 후보 목록을 순서대로 시도한다.
            var iType = FindColumn(
                new[] { new[] { "데이터 타입", "구분" }, new[] { "데이터 타입" } }, table.Value.Header);
            if (iName < 0) return variables;

            foreach (var cells in table.Value.Rows)
            {
                var name = Clean(Cell(cells, iName));
                if (!name.StartsWith("@", StringComparison.Ordinal)) continue;

                var type = Clean(Cell(cells, iType));
                var isSystemValue = SystemValueMarkerFragments.All(
                    f => type.Contains(f, StringComparison.Ordinal));
                variables.Add(new SpecLocalVariable(name, type, isSystemValue));
            }

            return variables;
        }

        // Task 4 - 오류 코드 (기계 확정 — 수정 금지) 표를 코드 → (종류, 번호)
        // 사전으로 읽는다. 표 찾기는 `ReadTable`의 제목 경계 판정에 그대로 기댄다
        // (다음 `### `를 만나면 절이 끝난다) - 다른 기계 확정 표에도
        // `| UPDATE N |` 모양 행이 섞이므로, 제목으로 구간을 먼저 자르지 않고
        // 문서 전체에서 직접 줄을 세면 엉뚱한 표의 행까지 담는다.
        private static IReadOnlyDictionary<string, (string Kind, int Ordinal)> ReadErrorCodeToOrdinal(
            IReadOnlyList<string> lines)
        {
            var map = new Dictionary<string, (string Kind, int Ordinal)>(StringComparer.Ordinal);

            var table = ReadTable(lines, DmlScopeExtractor.ErrorCodeTableHeading);
            if (table == null) return map;

            var iStatement = FindColumn(table.Value.Header, "문장");
            var iCode = FindColumn(table.Value.Header, "오류 코드");
            if (iStatement < 0 || iCode < 0) return map;

            // 같은 코드가 두 문장에 붙으면 귀속할 수 없다 - 넣고 덮어쓰지 않고
            // 아예 뺀다. dropped는 "이미 중복이라 뺀 코드"를 기억해, 세 번째 이상
            // 등장이 다시 map에 들어가는 것도 막는다.
            var dropped = new HashSet<string>(StringComparer.Ordinal);

            foreach (var cells in table.Value.Rows)
            {
                var match = StatementCellPattern.Match(Cell(cells, iStatement));
                if (!match.Success) continue;

                var code = Clean(Cell(cells, iCode));
                if (code.Length == 0 || dropped.Contains(code)) continue;

                if (map.ContainsKey(code))
                {
                    map.Remove(code);
                    dropped.Add(code);
                    continue;
                }

                map[code] = (
                    match.Groups["kind"].Value.ToUpperInvariant(),
                    int.Parse(match.Groups["ordinal"].Value));
            }

            return map;
        }

        private static (List<string> Header, List<List<string>> Rows)? ReadTable(
            IReadOnlyList<string> lines, string heading) =>
            ReadTable(lines, line => line.TrimEnd().Equals(heading, StringComparison.Ordinal));

        private static (List<string> Header, List<List<string>> Rows)? ReadTable(
            IReadOnlyList<string> lines, Func<string, bool> headingMatch)
        {
            var start = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (headingMatch(lines[i])) { start = i; break; }
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

        // 후보 조각 묶음을 순서대로 시도한다 - 첫 묶음이 안 맞으면 다음 묶음으로
        // 넓힌다. 기존 FindColumn(header, params string[])의 AND 의미(모든 조각이
        // 같은 칸에 있어야 함)는 그대로 두고, 이 자리에서만 "더 구체적인 후보가
        // 없으면 더 느슨한 후보로 물러난다"는 순서를 더한다. 다른 호출부
        // (iStatement·iLine·iTarget 등)는 이 오버로드를 쓰지 않으므로 그 칸
        // 탐색은 전혀 달라지지 않는다.
        private static int FindColumn(IReadOnlyList<string[]> candidateFragmentSets, IReadOnlyList<string> header)
        {
            foreach (var candidate in candidateFragmentSets)
            {
                var index = FindColumn(header, candidate);
                if (index >= 0) return index;
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
