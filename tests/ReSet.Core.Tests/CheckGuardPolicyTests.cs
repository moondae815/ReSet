using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「<c>Validate</c>·<c>ValidateConsolidated</c>·<c>ValidateBatchStep</c>이 부르는 검사가
    /// 전부 <c>SafeCheck</c>를 타는가.」
    ///
    /// [왜 이 게이트인가 - 2026-09-10 감사 [4]] 작성 계약 §6은 검사마다 자기
    /// <c>try/catch</c>를 요구한다. 그런데 <b>그 준수를 재는 자가 없었다</b>:
    /// <c>Validate</c>가 부르는 30자리를 하나씩 열어 보니 자기 가드가 있는 것이 13,
    /// 없는 것이 17이었다. 준수율 13/30이 아무에게도 안 걸린 채 서 있었다 - 자기 가드는
    /// 30개 메서드 본문을 각각 열어야 확인되기 때문이다.
    ///
    /// 그래서 관례를 <b>호출부 <c>SafeCheck</c></b>로 단일화했다. <c>ValidateBatchStep</c>이
    /// 이미 쓰던 쪽이고, 무엇보다 <b>한 자리에서 스캔할 수 있다.</b> 이 시험이 그 스캔이다.
    ///
    /// [정박 - 폭발 반경 밖에 둔다] 이 스캐너는 「위반 0」만 재지 않는다. <b>감싼 자리의
    /// 수</b>도 함께 잠근다 - 정규식이 조용히 아무것도 못 세게 되면 「위반 0」은 거짓
    /// 초록이 된다. 세 메서드를 못 찾으면 실패한다.
    ///
    /// 사전선언: <c>docs/audit-reports/2026-09-10-L1-예외격리-사전선언.md</c>
    /// </summary>
    public class CheckGuardPolicyTests
    {
        /// <summary>이 게이트의 관할. 지우는 catch-all을 가졌거나 호출자로 예외가 새는 셋이다.</summary>
        private static readonly string[] GuardedEntryPoints =
        {
            "Validate",
            "ValidateConsolidated",
            "ValidateBatchStep"
        };

        public sealed record UnguardedCall(string Method, int Line, string Callee, string Text);

        /// <summary>
        /// 메서드 본문에서 <c>Check*</c>/<c>Validate*</c> 호출 중 <c>SafeCheck</c>로 감싸지
        /// 않은 것을 낸다. 중괄호 매칭으로 본문 경계를 잡는다 - 줄 번호를 손으로 적으면
        /// 다음 편집에서 조용히 어긋난다.
        /// </summary>
        public static IReadOnlyList<UnguardedCall> ScanSource(string source, IEnumerable<string> entryPoints)
        {
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var found = new List<UnguardedCall>();

            foreach (var entry in entryPoints)
            {
                var (start, end) = LocateBody(lines, entry);
                if (start < 0)
                {
                    found.Add(new UnguardedCall(entry, 0, "(메서드를 못 찾음)", string.Empty));
                    continue;
                }

                for (var i = start; i <= end; i++)
                {
                    var text = lines[i];
                    var trimmed = text.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (trimmed.StartsWith("///", StringComparison.Ordinal)) continue;

                    foreach (Match m in CalleeRegex.Matches(text))
                    {
                        var callee = m.Groups["name"].Value;
                        if (callee == entry) continue;            // 시그니처 줄의 자기 이름
                        if (callee == "SafeCheck") continue;

                        // SafeCheck(() => X(...)) 안이면 감싼 것이다.
                        var before = text[..m.Index];
                        if (before.Contains("SafeCheck(() =>", StringComparison.Ordinal)) continue;

                        found.Add(new UnguardedCall(entry, i + 1, callee, trimmed));
                    }
                }
            }

            return found;
        }

        /// <summary>감싼 자리의 수. 「위반 0」이 거짓 초록이 아닌지 재는 반대편이다.</summary>
        public static IReadOnlyDictionary<string, int> CountGuarded(string source, IEnumerable<string> entryPoints)
        {
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var entry in entryPoints)
            {
                var (start, end) = LocateBody(lines, entry);
                counts[entry] = start < 0
                    ? 0
                    : Enumerable.Range(start, end - start + 1)
                        .Count(i => GuardRegex.IsMatch(lines[i]));
            }

            return counts;
        }

        private static readonly Regex CalleeRegex =
            new(@"(?<![\w.])(?<name>(?:Check|Validate)[A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

        private static readonly Regex GuardRegex =
            new(@"^\s*SafeCheck\(\(\)\s*=>", RegexOptions.Compiled);

        private static (int Start, int End) LocateBody(string[] lines, string methodName)
        {
            var signature = new Regex(
                @"^\s*(?:public|private|internal|protected)[\w\s<>,\[\]?]*\s" + Regex.Escape(methodName) + @"\s*\(",
                RegexOptions.Compiled);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!signature.IsMatch(lines[i])) continue;

                var depth = 0;
                var started = false;
                for (var j = i; j < lines.Length; j++)
                {
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') { depth++; started = true; }
                        else if (ch == '}')
                        {
                            depth--;
                            if (started && depth == 0) return (i, j);
                        }
                    }
                }
            }

            return (-1, -1);
        }

        [Fact]
        public void ScanSource_FlagsADirectCall()
        {
            const string source = @"
        public ValidationResult Validate(string markdown)
        {
            SafeCheck(() => CheckOne(markdown, result));
            CheckTwo(markdown, result);
        }";

            var found = ScanSource(source, new[] { "Validate" });

            var only = Assert.Single(found);
            Assert.Equal("CheckTwo", only.Callee);
        }

        [Fact]
        public void ScanSource_AcceptsAWrappedCall()
        {
            const string source = @"
        public ValidationResult Validate(string markdown)
        {
            SafeCheck(() => CheckOne(markdown, result));
        }";

            Assert.Empty(ScanSource(source, new[] { "Validate" }));
        }

        [Fact]
        public void ScanSource_IgnoresCommentedOutCalls()
        {
            // 주석의 「종전에는 CheckX(...) 였다」 서술이 위반으로 잡히면, 이 게이트를
            // 통과하려고 사람이 근거 주석을 지우게 된다.
            const string source = @"
        public ValidationResult Validate(string markdown)
        {
            // 종전에는 CheckOld(markdown, result); 였다.
            SafeCheck(() => CheckOne(markdown, result));
        }";

            Assert.Empty(ScanSource(source, new[] { "Validate" }));
        }

        [Fact]
        public void ScanSource_ReportsAMissingMethodRatherThanReturningEmpty()
        {
            // 「위반 0」과 「메서드를 못 찾아 아무것도 안 셌다」가 구분되지 않으면
            // 이 스캐너 자신이 조용히 죽는다. 이름이 바뀌면 여기서 큰 소리로 실패한다.
            var found = ScanSource("class X { }", new[] { "Validate" });

            var only = Assert.Single(found);
            Assert.Equal("(메서드를 못 찾음)", only.Callee);
        }

        [Fact]
        public void EveryCheckCallInTheGuardedEntryPointsGoesThroughSafeCheck()
        {
            var path = ValidatorSourcePath();
            var source = File.ReadAllText(path);

            var unguarded = ScanSource(source, GuardedEntryPoints);

            Assert.True(
                unguarded.Count == 0,
                "SafeCheck 를 안 타는 검사 호출이 " + unguarded.Count + "자리 있습니다.\n"
                + "감사 [4]: 이 자리들이 던지면 Validate·ValidateConsolidated 는 이미 찾은\n"
                + "결함을 지우고 통과하고, ValidateBatchStep 은 예외를 호출자로 흘립니다.\n"
                + string.Join("\n", unguarded.Select(u => $"  {u.Method}:{u.Line} {u.Callee} — {u.Text}")));
        }

        [Fact]
        public void TheScannerActuallySeesTheGuardedCalls()
        {
            // [반대편 정박] 위 시험은 「0건」을 재므로, 정규식이 조용히 아무것도 못 세게
            // 되어도 초록이다. 이 시험이 그 구멍을 막는다 - 자릿수는 2026-09-10 실측이고,
            // 자리를 더하면 이 수도 함께 올려야 한다(그때 이 주석을 읽게 된다).
            var counts = CountGuarded(File.ReadAllText(ValidatorSourcePath()), GuardedEntryPoints);

            // 31: 2026-09-10 에 CheckDocumentInstructsItsAuthor 가 무조건 검사로 더해졌다.
            Assert.Equal(31, counts["Validate"]);
            Assert.Equal(9, counts["ValidateConsolidated"]);
            // 27: 2026-09-11 에 CheckAnchoredStatementPredicateTerms(앵커 DML 최상위 술어 대조)가 더해졌다.
            // 28: 2026-09-12 에 CheckStepParameterTypeStated(규칙 5-2)가 더해졌다.
            // 29: 2026-09-13 에 CheckBatchControlTableAlias(제어 계약 표 별칭, K1)가 더해졌다.
            Assert.Equal(29, counts["ValidateBatchStep"]);
        }

        private static string ValidatorSourcePath() =>
            Path.Combine(RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "MechanicalValidator.cs");
    }
}
