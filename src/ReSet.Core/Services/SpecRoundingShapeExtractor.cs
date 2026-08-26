using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 명세서에서 중첩 <c>ROUND</c> 계산의 "모양"을 뽑는다.
    ///
    /// 이 추출기가 존재하는 이유: 정산 금액은 반올림 순서에 따라 달라진다. 원본이
    /// 합계를 먼저 반올림하고 다시 반올림하는데 계획서가 한 번만 하면 결과가 어긋나고,
    /// 그것을 보는 검사가 어디에도 없었다. 대상 테이블·오류코드·조건 컬럼이 다 맞아도
    /// 이 축은 비어 있었다.
    ///
    /// [이름을 지우고 구조를 남기는 이유]
    /// 계획서는 같은 계산을 자기 이름으로 부른다 - 원본의 <c>X.PGCOMM4SUM</c>이
    /// <c>X.RawPgComm4Sum</c>이 된다. 이름까지 대조하면 정상 이행이 전부 걸린다.
    /// 반대로 반올림 방식 플래그(<c>CommRoundFlag</c>·<c>CommSumRoundFlag</c>·
    /// <c>VatRoundFlag</c>)까지 지우면 그것을 바꿔 써도 통과하는데, 그 인자가 바로
    /// 올림/버림을 가르므로 금액이 달라진다. 그래서 피연산자는 <c>?</c>로 지우고
    /// ROUND의 세 번째 인자와 숫자 리터럴은 남긴다.
    ///
    /// 실측(POQSettleProc15): 이 정규화로 UP_UTIL_SETTLE_INS의 수식이 6종으로 갈라지고,
    /// 계획서 S05가 그 6종을 정확히 재현했다. 서로 다른 6종이 양쪽에서 같게 나온 것이라
    /// 정규화가 뭉개고 있지 않다는 근거이기도 하다.
    ///
    /// [단일 ROUND를 보지 않는 이유]
    /// 너무 흔해 신호가 되지 않는다. 반올림 순서의 오류는 중첩에서 생긴다.
    ///
    /// [이 추출기가 못 보는 것]
    /// 명세서가 수식을 SQL로 적어 두었을 때만 읽는다. 산문으로 풀어 쓴 계산은 뽑지
    /// 못한다 - 조건 추출기와 같은 한계이며, 같은 이유로 재현율보다 오탐 억제를 택했다.
    /// </summary>
    public static class SpecRoundingShapeExtractor
    {
        /// <summary>
        /// 지우지 않고 남기는 토큰. ROUND의 세 번째 인자로 오는 반올림 방식 플래그와
        /// 계산 구조를 이루는 함수 이름이다. 플래그를 지우면 올림과 버림이 같은 모양이 된다.
        /// </summary>
        /// <summary>반올림 방식을 정하는 플래그 컬럼. 이것이 모양에 남아야 대조가 뜻을 가진다.</summary>
        private static readonly string[] RoundingFlags =
            { "commroundflag", "commsumroundflag", "vatroundflag" };

        private static readonly HashSet<string> PreservedTokens =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "round", "cast", "int", "isnull", "sum", "as",
                "commroundflag", "commsumroundflag", "vatroundflag"
            };

        private static readonly Regex RoundStartRegex = new(@"ROUND\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex IdentifierRegex =
            new(@"[A-Za-z_][A-Za-z_0-9]*(?:\.[A-Za-z_][A-Za-z_0-9]*)*", RegexOptions.Compiled);

        public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Extract(
            IEnumerable<(string FileName, string Content)> specs)
        {
            var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
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

                var shapes = ReadShapes(content);
                if (shapes.Count == 0)
                {
                    // 빈 목록과 "그런 프로시저 없음"이 같아지면 대조 0건이 통과로 읽힌다.
                    // SpecReturnCodeExtractor가 키를 만들지 않는 것과 같은 이유다.
                    continue;
                }

                var key = MechanicalValidator.BareObjectName(fileName);
                if (result.TryGetValue(key, out var existing))
                {
                    var merged = new HashSet<string>(existing, StringComparer.Ordinal);
                    merged.UnionWith(shapes);
                    result[key] = merged;
                    continue;
                }

                result[key] = shapes;
            }

            return result;
        }

        /// <summary>
        /// 본문에서 중첩 ROUND 수식을 잘라내어 정규화한 모양의 집합을 돌려준다.
        /// 계획서 본문에도 같은 정규화를 적용해야 두 쪽을 견줄 수 있으므로 공개한다.
        /// </summary>
        public static IReadOnlyCollection<string> ReadShapes(string? text)
        {
            var shapes = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text))
            {
                return shapes;
            }

            foreach (Match start in RoundStartRegex.Matches(text))
            {
                var expression = SliceBalanced(text, start.Index);
                if (expression == null)
                {
                    continue;
                }

                // 바깥 ROUND( 뒤에 또 ROUND가 있어야 중첩이다.
                if (RoundStartRegex.Match(expression, start.Length) is not { Success: true })
                {
                    continue;
                }

                var shape = Normalize(expression);

                // 아는 반올림 플래그가 하나도 없으면 대조하지 않는다. 방식이 UDF 호출로
                // 정해지는 수식(실측: ROUND(IIF(...),0,dbo.UF_GET_PGCommOption(...)))은
                // IIF·UDF가 겹쳐 표현 차이만으로 모양이 어긋나고, 실제로 정상 이행을
                // 결함으로 보고했다. 이 검사가 보려는 것은 반올림 방식과 순서의 보존인데
                // 방식이 함수로 정해지면 그 판정을 모양으로 할 수 없다.
                if (!RoundingFlags.Any(flag => shape.Contains(flag, StringComparison.Ordinal)))
                {
                    continue;
                }

                shapes.Add(shape);
            }

            return shapes;
        }

        /// <summary>여는 괄호부터 짝이 맞는 닫는 괄호까지. 짝이 없으면 null.</summary>
        private static string? SliceBalanced(string text, int start)
        {
            var depth = 0;
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '(')
                {
                    depth++;
                }
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(start, i - start + 1);
                    }
                }
            }

            return null;
        }

        private static string Normalize(string expression)
        {
            var compact = Regex.Replace(expression, @"\s+", string.Empty);
            var erased = IdentifierRegex.Replace(compact, match =>
            {
                var bare = match.Value.Split('.')[^1];
                return PreservedTokens.Contains(bare) ? bare.ToLowerInvariant() : "?";
            });

            return StripRedundantParentheses(erased.ToLowerInvariant());
        }

        /// <summary>
        /// 지워도 파싱이 그대로인 괄호만 없앤다.
        ///
        /// [이 축약이 필요한 이유]
        /// 실측(POQSettleBatch1 S05·S07·S09·S10): 명세서가 <c>ROUND((a+b),0,f)</c>로 적고
        /// 계획서가 <c>ROUND(a+b,0,f)</c>로 적었을 뿐인데, 대조가 문자열 정확 일치라
        /// "반올림 계산 누락"으로 갈렸다. 같은 계산이고 같은 금액이다. 하한 미달 배너가
        /// 붙은 네 단계가 전부 이 오탐이었고, 각 단계가 재시도 5회를 헛되이 소진했다.
        ///
        /// [괄호를 전부 지우지 않는 이유]
        /// 괄호는 결합을 정한다. <c>(a+b)*c</c>와 <c>a+b*c</c>는 다른 금액이고,
        /// 전부 지우면 이 검사가 잡아야 할 진짜 결함이 통과한다. 그래서 지워도
        /// 결합이 달라지지 않음이 증명되는 두 경우만 없앤다.
        ///
        /// 1. 인자 슬롯 전체를 감싼 괄호 - 앞이 <c>(</c>나 <c>,</c>이고 짝 뒤가
        ///    <c>)</c>나 <c>,</c>. 양옆이 구분자라 결합할 상대가 없다.
        /// 2. 항 하나만 감싼 괄호 - 안쪽 최상위에 연산자가 없다. 어느 자리에 놓여도
        ///    하나의 항으로 읽히므로 괄호가 하는 일이 없다.
        ///
        /// 함수 호출 괄호(여는 괄호 앞이 식별자)는 후보로 삼지 않는다.
        /// 축약이 새 후보를 만들 수 있어(<c>((a))</c>) 더 지울 것이 없을 때까지 돈다.
        /// </summary>
        private static string StripRedundantParentheses(string shape)
        {
            bool changed;
            do
            {
                changed = false;
                for (var open = 1; open < shape.Length; open++)
                {
                    if (shape[open] != '(' || IsNameCharacter(shape[open - 1]))
                    {
                        continue;
                    }

                    var close = FindMatch(shape, open);
                    if (close < 0)
                    {
                        continue;
                    }

                    var before = shape[open - 1];
                    var after = close + 1 < shape.Length ? shape[close + 1] : '\0';

                    var wrapsWholeArgument = (before == '(' || before == ',')
                        && (after == ')' || after == ',');

                    if (!wrapsWholeArgument
                        && !IsSingleTerm(shape, open + 1, close)
                        && !BindsTighterThanItsContext(shape, open, close))
                    {
                        continue;
                    }

                    shape = shape.Remove(close, 1).Remove(open, 1);
                    changed = true;
                    break;
                }
            }
            while (changed);

            return shape;
        }

        /// <summary>
        /// 괄호 안이 바깥보다 <b>엄격히</b> 강하게 결합하는가. 그렇다면 괄호를 지워도
        /// 파싱이 같다.
        ///
        /// 실측: 이 사례가 현 코퍼스에 이미 있다. 25종 모양 중 네 쌍이
        /// <c>?+?/10.0</c>과 <c>?+(?/10.0)</c>처럼 같은 계산의 표기 차이로 갈려 있었고
        /// (Proc2·6·8·9 대 Proc10), 명세서와 계획서가 서로 다른 쪽을 고르는 순간
        /// 반올림 누락 오탐이 난다 - 여분 괄호 축약이 애초에 닫으려던 그 결함이다.
        ///
        /// 등호를 허용하지 않는 이유가 이 판정의 전부다. <c>a-(b-c)</c>와 <c>a/(b*c)</c>는
        /// 안팎의 우선순위가 같은데 값이 다르다(좌결합이라 괄호가 결합을 실제로 바꾼다).
        /// 엄격 부등호만 쓰면 그런 경우가 조건에서 저절로 떨어진다. 좌결합의 왼쪽
        /// 피연산자 자리(<c>(a+b)+c</c>)는 우선순위가 같아도 안전하지만 별도 판단이
        /// 필요하므로 여기서 다루지 않는다 - 덜 지우는 쪽이 안전하다.
        /// </summary>
        private static bool BindsTighterThanItsContext(string shape, int open, int close)
        {
            var inner = MinimumTopLevelPrecedence(shape, open + 1, close);
            if (inner == 0)
            {
                // 최상위 연산자가 없다. 그것은 IsSingleTerm이 이미 판정하는 경우다.
                return false;
            }

            var left = open > 0 ? Precedence(shape[open - 1]) : 0;
            var right = close + 1 < shape.Length ? Precedence(shape[close + 1]) : 0;

            // 왼쪽 피연산자 자리에서는 우선순위가 같아도 지울 수 있다. 이 연산자들은
            // 전부 좌결합이므로 `(a+b)+c`는 `a+b+c`로, `(a*b)/c`는 `a*b/c`로 이미 읽힌다.
            // 오른쪽 자리에서는 같은 우선순위가 위험하다 - `a-(b-c)`는 `a-b-c`가 아니다.
            if (left == 0 && right > 0)
            {
                return inner >= right;
            }

            return (left == 0 || inner > left) && (right == 0 || inner > right);
        }

        /// <summary>
        /// T-SQL 연산자 우선순위. 큰 값이 강하게 결합한다. 연산자가 아니면 0.
        /// 비트 연산자는 <c>+ -</c>와 같은 단계다(T-SQL 규정).
        /// </summary>
        private static int Precedence(char c) => c switch
        {
            '*' or '/' or '%' => 3,
            '+' or '-' or '&' or '^' or '|' => 2,
            '=' or '<' or '>' or '!' => 1,
            _ => 0
        };

        /// <summary>구간의 최상위 연산자 중 가장 약한 것의 우선순위. 없으면 0.</summary>
        private static int MinimumTopLevelPrecedence(string shape, int start, int end)
        {
            var depth = 0;
            var min = 0;
            for (var i = start; i < end; i++)
            {
                var c = shape[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (depth == 0)
                {
                    if (c == ',')
                    {
                        // 최상위 콤마가 있으면 인자 목록이라 괄호를 지울 수 없다.
                        return 0;
                    }

                    var p = Precedence(c);
                    if (p > 0 && (min == 0 || p < min))
                    {
                        min = p;
                    }
                }
            }

            return min;
        }

        /// <summary>
        /// 이름을 이루는 문자인가. 여는 괄호 바로 앞이 이것이면 함수 호출 괄호이므로
        /// 축약 후보로 삼지 않는다.
        ///
        /// <c>?</c>를 포함하는 이유: 이 판정이 도는 시점에는 <see cref="Normalize"/>가
        /// 이미 비보존 식별자를 <c>?</c>로 지웠다. <c>?</c>를 이름 문자로 보지 않으면
        /// UDF 호출(<c>dbo.UF_X(A)</c> → <c>?(?)</c>)의 괄호가 후보가 되어, 주석이
        /// 말하는 가드가 실제로는 보존 토큰(round·cast·isnull·sum)에만 걸린다.
        /// </summary>
        private static bool IsNameCharacter(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c == '?';

        /// <summary>여는 괄호의 짝 위치. 짝이 없으면 -1.</summary>
        private static int FindMatch(string shape, int open)
        {
            var depth = 0;
            for (var i = open; i < shape.Length; i++)
            {
                if (shape[i] == '(')
                {
                    depth++;
                }
                else if (shape[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// <paramref name="start"/>부터 <paramref name="end"/> 직전까지가 항 하나인가 -
        /// 최상위에 연산자도 콤마도 없는가. 부호가 붙은 숫자(<c>-1</c>)는 최상위 <c>-</c>가
        /// 있으므로 항이 아니라고 본다. 덜 지우는 쪽이 안전하다.
        ///
        /// 비트 연산자(<c>&amp; | ^</c>)와 비교 연산자를 빠뜨리면 안 된다. T-SQL에서 <c>&amp;</c>는
        /// <c>|</c>보다 강하게 결합하므로 <c>a&amp;(b|c)</c>와 <c>a&amp;b|c</c>는 값이 다른데,
        /// 이 목록에 없으면 <c>(b|c)</c>가 항 하나로 판정돼 두 수식이 같은 모양이 된다.
        /// 현 코퍼스에 비트 연산자는 없지만, 결합을 바꾸는 괄호는 구분돼야 한다는 것이
        /// 이 축약의 유일한 제약이다.
        /// </summary>
        private static bool IsSingleTerm(string shape, int start, int end)
        {
            var depth = 0;
            for (var i = start; i < end; i++)
            {
                var c = shape[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (depth == 0 && (c == ',' || Precedence(c) > 0))
                {
                    // 연산자 목록은 Precedence 한 곳에만 둔다. 두 벌로 적으면 한쪽에만
                    // 연산자가 추가돼, 그 연산자를 담은 괄호가 "항 하나"로 판정된다.
                    return false;
                }
            }

            return true;
        }
    }
}
