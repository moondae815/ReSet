using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 한 프로시저의 조건 컬럼을 출처별로 담는다.
    /// </summary>
    /// <param name="BodyColumns">프로시저 본체가 직접 거르는 컬럼.</param>
    /// <param name="ByUdf">UDF 맨이름 → 그 UDF 내부가 거르는 컬럼.</param>
    public sealed record SpecConditions(
        IReadOnlyList<string> BodyColumns,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ByUdf);

    /// <summary>
    /// 명세서 본문에서 원본 프로시저가 필터·분기에 쓰는 컬럼 이름을 뽑는다.
    ///
    /// 이 추출기가 존재하는 이유: 기계 검증이 스키마·이름 층만 보고 로직 층을
    /// 통째로 비워 둔다. 대상 테이블과 오류코드가 전부 맞아도 원본이 대상을
    /// 고르는 조건이 사라지면 처리 집합이 달라지는데, 그것을 보는 검사가 없었다.
    ///
    /// [본체와 UDF를 가르는 이유]
    /// 명세서는 프로시저 본체 조건과 그 프로시저가 호출하는 UDF의 내부 조건을 같은
    /// CRUD 분석 섹션에 나란히 적는다. UDF 조건은 계획서가 그 UDF를 그대로 호출하면
    /// 옮겨 적을 이유가 없다 - 구별하지 않고 실측 산출물에 돌렸더니 검출 15건 중
    /// 14건이 이 오인이었다(S09가 UIF_SettleYMD를 7회 호출하는데도 그 안의
    /// SettleTarget·SettleState를 누락으로 보고했다).
    ///
    /// 출처는 두 가지로 판정한다. `#### `dbo.UIF_SettleYMD`` 같은 헤딩 아래의 표는
    /// 그 절이 끝날 때까지 그 UDF 소속이고, 표 행 안에서 UDF를 밝히는 형태는 그 행만
    /// 그 UDF 소속이다. 어느 쪽에도 걸리지 않으면 본체 조건이다.
    ///
    /// 값이 아니라 컬럼 이름만 뽑는 이유는 노이즈 때문이다. 같은 조건을 명세서는
    /// `UseState IN (0)`으로, 계획서는 `UseState = 0`으로 쓴다 - 값까지 대조하면
    /// 실측에서 미검출의 27%가 이런 동등 표현이었고 그 전부가 오탐이었다.
    ///
    /// [이 추출기가 못 보는 것]
    /// 백틱으로 인용되지 않고 산문으로만 서술된 조건은 뽑지 못한다. 인용 표기에
    /// 기대는 대신 SQL을 파싱하려면 정적 분석(SpDefinition) 경로가 필요하고, 그것은
    /// 이 클래스의 범위가 아니다. 재현율을 포기하는 대신 오탐을 낮게 유지하는 쪽을
    /// 택했다 - 하한 검사의 결함은 단계 재생성을 유발하므로 오탐에 비용이 붙는다.
    /// </summary>
    public static class SpecConditionColumnExtractor
    {
        /// <remarks>
        /// `IN` 앞뒤로 단어 경계를 요구한다. 이것이 없으면 `BEGIN`이 `BEG` + `IN`으로,
        /// `TSettleByIN`이 `TSettleBy` + `IN`으로 쪼개져 낱말 조각이 컬럼으로 둔갑한다 -
        /// 첫 구현을 실측 산출물에 돌렸을 때 검출 27건 중 15건이 이 한 가지 버그였다.
        /// </remarks>
        private static readonly Regex ConditionRegex = new(
            @"`\s*(?<column>[A-Za-z_@][A-Za-z_0-9]*(?:\.[A-Za-z_][A-Za-z_0-9]*)*)\s*(?:=|<>|>=|<=|\bIN\b)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UdfNameRegex = new(
            @"\b(?<udf>U(?:I)?F_[A-Za-z_0-9]+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex HeadingRegex = new(@"^\s{0,3}#{2,6}\s", RegexOptions.Compiled);

        /// <summary>
        /// 두 글자 이하는 별칭·약어와 구별되지 않아 대조에서 소음만 만든다.
        /// 세 글자는 남긴다 - 이 도메인의 `YMD`가 정확히 세 글자이고, 정산 기준일은
        /// 거의 모든 조건의 축이라 배제하면 신호를 통째로 잃는다.
        /// </summary>
        private const int MinimumColumnLength = 3;

        /// <summary>
        /// 조건 모양을 하고 있지만 컬럼이 아닌 것들. 실측: `INDEX=CIDX_TTxMst_YMD`는
        /// 인덱스 힌트다. 목록으로 두는 이유는 새 사례가 나올 때 한 줄로 늘리기 위해서다.
        /// </summary>
        private static readonly HashSet<string> NotColumns =
            new(StringComparer.OrdinalIgnoreCase) { "INDEX", "TOP", "MAXDOP", "VALUES" };

        public static IReadOnlyDictionary<string, SpecConditions> Extract(
            IEnumerable<(string FileName, string Content)> specs)
        {
            var result = new Dictionary<string, SpecConditions>(StringComparer.OrdinalIgnoreCase);
            if (specs == null)
            {
                return result;
            }

            foreach (var (fileName, content) in specs)
            {
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var body = new List<string>();
                var byUdf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                CollectFrom(content, body, byUdf);

                if (body.Count == 0 && byUdf.Count == 0)
                {
                    // 빈 목록과 "그런 프로시저 없음"이 같아지면 대조 0건이 통과로 읽힌다.
                    // SpecReturnCodeExtractor가 키를 만들지 않는 것과 같은 이유다.
                    continue;
                }

                var key = MechanicalValidator.BareObjectName(fileName);
                var conditions = new SpecConditions(
                    body,
                    ToReadOnly(byUdf));

                result[key] = result.TryGetValue(key, out var existing)
                    ? Merge(existing, conditions)
                    : conditions;
            }

            return result;
        }

        private static void CollectFrom(
            string content, List<string> body, Dictionary<string, List<string>> byUdf)
        {
            // 헤딩이 연 UDF 절은 다음 헤딩에서 닫는다. 닫지 않으면 그 뒤의 본체 조건이
            // 전부 UDF 소속으로 흡수되어 면제된다.
            string? headingUdf = null;

            // INSERT 절의 표는 대상 컬럼에 무엇을 넣는지를 적는다. `X.PGINCVTAX = 1`은
            // "그 컬럼에 상수 1을 저장한다"는 뜻이지 거르는 조건이 아닌데, 문법이 같아
            // 구별되지 않는다. UPDATE 절은 제외하지 않는다 - 거기에는 진짜 WHERE 조건이
            // 섞여 있어, 함께 버리면 재현율을 그만큼 잃는다.
            var inValueMapping = false;

            foreach (var line in MarkdownSectionLocator.SplitLines(content))
            {
                if (HeadingRegex.IsMatch(line))
                {
                    headingUdf = FindUdf(line);
                    inValueMapping = line.IndexOf("INSERT 대상 테이블", StringComparison.OrdinalIgnoreCase) >= 0;
                    continue;
                }

                if (inValueMapping)
                {
                    continue;
                }

                var conditions = ReadConditions(line);
                if (conditions.Count == 0)
                {
                    continue;
                }

                // 행이 자기 첫 열에 UDF를 내세울 때만 그 행의 소유자로 인정하고,
                // 그 밖의 언급은 헤딩 컨텍스트를 따른다. 문장 중간의 UDF는 소유자가
                // 아니라 피호출자다 - 실측: `HolidayProcFlag = 2이면 ... 그렇지 않으면
                // UF_GET_WORKDAY2를 호출합니다`의 조건은 그 절의 주인인
                // UF_GET_COLLECTYMD의 것이지 WORKDAY2의 것이 아니다.
                var owner = FindUdfInFirstCell(line) ?? headingUdf;
                var bucket = owner == null
                    ? body
                    : Bucket(byUdf, owner);

                foreach (var column in conditions)
                {
                    if (!bucket.Contains(column, StringComparer.OrdinalIgnoreCase))
                    {
                        bucket.Add(column);
                    }
                }
            }
        }

        private static List<string> ReadConditions(string line)
        {
            var columns = new List<string>();
            foreach (Match match in ConditionRegex.Matches(line))
            {
                var column = BareColumnName(match.Groups["column"].Value);
                if (column.Length < MinimumColumnLength ||
                    column.StartsWith("@", StringComparison.Ordinal) ||
                    NotColumns.Contains(column) ||
                    columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                columns.Add(column);
            }

            return columns;
        }

        private static string? FindUdf(string line)
        {
            var match = UdfNameRegex.Match(line);
            return match.Success ? match.Groups["udf"].Value : null;
        }

        /// <summary>
        /// 표 행의 첫 칸에서만 UDF를 찾는다. `| dbo.UF_GET_COLLECTYMD | ... |` 처럼
        /// 첫 열이 대상을 밝히는 표를 위한 것이고, 본문 문장 안의 언급은 보지 않는다.
        /// </summary>
        private static string? FindUdfInFirstCell(string line)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                return null;
            }

            var cells = trimmed.Split('|');
            // cells[0]은 맨 앞 '|' 앞의 빈 문자열이므로 첫 칸은 cells[1]이다.
            return cells.Length > 1 ? FindUdf(cells[1]) : null;
        }

        private static List<string> Bucket(Dictionary<string, List<string>> byUdf, string udf)
        {
            if (!byUdf.TryGetValue(udf, out var bucket))
            {
                bucket = new List<string>();
                byUdf[udf] = bucket;
            }

            return bucket;
        }

        private static SpecConditions Merge(SpecConditions left, SpecConditions right)
        {
            var body = new List<string>(left.BodyColumns);
            foreach (var column in right.BodyColumns)
            {
                if (!body.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    body.Add(column);
                }
            }

            var byUdf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in new[] { left.ByUdf, right.ByUdf })
            {
                foreach (var pair in source)
                {
                    var bucket = Bucket(byUdf, pair.Key);
                    foreach (var column in pair.Value)
                    {
                        if (!bucket.Contains(column, StringComparer.OrdinalIgnoreCase))
                        {
                            bucket.Add(column);
                        }
                    }
                }
            }

            return new SpecConditions(body, ToReadOnly(byUdf));
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToReadOnly(
            Dictionary<string, List<string>> byUdf)
        {
            var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in byUdf)
            {
                copy[pair.Key] = pair.Value;
            }

            return copy;
        }

        /// <summary>
        /// 별칭·테이블 한정자를 뗀 이름. `A.PGNAME` → `PGNAME`.
        ///
        /// 명세서가 같은 컬럼을 별칭과 함께 쓰는데 그 별칭까지 이름으로 삼으면,
        /// 계획서가 다른 별칭을 쓸 때 전부 누락으로 잡힌다.
        /// </summary>
        private static string BareColumnName(string raw)
        {
            var trimmed = (raw ?? string.Empty).Trim();
            var lastDot = trimmed.LastIndexOf('.');
            return lastDot >= 0 ? trimmed.Substring(lastDot + 1) : trimmed;
        }
    }
}
