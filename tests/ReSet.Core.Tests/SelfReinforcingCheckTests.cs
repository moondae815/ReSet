using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「한 객체의 <b>연속한 두 시도</b>에서 같은 검사키가 발화하면 자기강화 후보다.」
    ///
    /// [왜 이 서명인가 - 2026-09-09 실측] 한 번의 발화는 정상이다(모델이 틀렸고 다음에
    /// 고친다). 연속 두 번은 「고쳤는데 또 걸렸다」이고, 그것이 <b>만족 불가능한 지시</b>의
    /// 서명이다. 그날 검사 키 셋 중 둘이 이 서명을 냈다:
    ///   CheckErrorCodeUniquenessClaim — 1 판 시도 2·3·4·5·6, 3 판 시도 1·2
    ///   축약어 검사                    — 4 판 시도 1·2
    /// 나머지 하나(MachineTableShapeBroken)는 단발이라 안 걸린다. <b>그것이 옳다</b> -
    /// 그 검사는 만족 불가능하지 않았고 모델이 다음 시도에 실제로 고쳤다. 이 게이트의
    /// 관할은 「만족 불가능한 지시」이지 「나쁜 문구」가 아니다.
    ///
    /// [왜 개수가 아니라 서명인가] 발화 수·통과 수는 활동이지 효력이 아니다. 그날
    /// 「3 자리가 2 자리로 줄었다」가 실제로는 「같은 문장이 그대로 남고 하나 더 늘었다」였다.
    /// </summary>
    public class SelfReinforcingCheckTests
    {
        private static string CorpusRoot => Path.Combine(
            RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "rejected-attempts");

        /// <summary>
        /// 연속 시도 쌍을 낸다. (객체, 검사키) 마다 시도 번호를 모아 n 과 n+1 이 둘 다
        /// 있는지 본다.
        /// </summary>
        public static IReadOnlyList<string> FindSelfReinforcing(
            IEnumerable<(string ObjectName, IReadOnlyList<L1AttemptFiring> Firings)> corpus)
        {
            var found = new List<string>();

            foreach (var (objectName, firings) in corpus)
            {
                foreach (var group in firings.GroupBy(f => f.CheckKey, StringComparer.Ordinal))
                {
                    // [Distinct() 없음 - 2026-09-10 되돌림으로 확인] 원래 여기 있던
                    // .Distinct() 를 지우고 다시 돌려도 4 개 시험이 그대로 통과했다 -
                    // 시험이 없는 가지였다. 같은 시도에서 한 검사가 두 자리를 발화하면
                    // 정렬 뒤 [2,2] 가 되어 gap == 0 이라 어차피 이 게이트에 안 걸리므로
                    // Distinct() 는 결과를 바꾸지 않는다.
                    var attempts = group.Select(f => f.Attempt).OrderBy(a => a).ToList();
                    if (attempts.Zip(attempts.Skip(1), (a, b) => b - a).Any(gap => gap == 1))
                    {
                        found.Add($"{objectName} {group.Key}");
                    }
                }
            }

            return found;
        }

        [Fact]
        public void FindSelfReinforcing_FlagsConsecutiveAttempts()
        {
            var corpus = new[]
            {
                ("dbo.UP_X", (IReadOnlyList<L1AttemptFiring>)new[]
                {
                    new L1AttemptFiring(1, "CheckA", "…"),
                    new L1AttemptFiring(2, "CheckA", "…"),
                })
            };

            Assert.Equal(new[] { "dbo.UP_X CheckA" }, FindSelfReinforcing(corpus));
        }

        [Fact]
        public void FindSelfReinforcing_IgnoresASingleFiring()
        {
            // 한 번은 정상이다. 여기서 발화하면 게이트가 늘 빨개져 버려진다.
            var corpus = new[]
            {
                ("dbo.UP_X", (IReadOnlyList<L1AttemptFiring>)new[]
                {
                    new L1AttemptFiring(1, "CheckA", "…"),
                })
            };

            Assert.Empty(FindSelfReinforcing(corpus));
        }

        [Fact]
        public void FindSelfReinforcing_IgnoresNonConsecutiveFirings()
        {
            // 시도 1 과 3 은 「고쳤는데 또 걸렸다」가 아니다 - 사이에 통과한 판이 있다.
            var corpus = new[]
            {
                ("dbo.UP_X", (IReadOnlyList<L1AttemptFiring>)new[]
                {
                    new L1AttemptFiring(1, "CheckA", "…"),
                    new L1AttemptFiring(3, "CheckA", "…"),
                })
            };

            Assert.Empty(FindSelfReinforcing(corpus));
        }

        [Fact]
        public void FindSelfReinforcing_DoesNotMergeDifferentChecks()
        {
            var corpus = new[]
            {
                ("dbo.UP_X", (IReadOnlyList<L1AttemptFiring>)new[]
                {
                    new L1AttemptFiring(1, "CheckA", "…"),
                    new L1AttemptFiring(2, "CheckB", "…"),
                })
            };

            Assert.Empty(FindSelfReinforcing(corpus));
        }

        [SkippableFact]
        public void NoUnrecordedSelfReinforcingCheckInTheCorpus()
        {
            Skip.IfNot(Directory.Exists(CorpusRoot), "거부된 시도 코퍼스가 아직 없습니다.");

            var corpus = Directory
                .EnumerateDirectories(CorpusRoot)
                .OrderBy(d => d, StringComparer.Ordinal)
                .Select(d => (
                    ObjectName: Path.GetFileName(d),
                    Firings: (IReadOnlyList<L1AttemptFiring>)L1AttemptLog.Read(
                        Path.Combine(d, "attempts.json"))))
                .Where(entry => entry.Firings.Count > 0)
                .ToList();

            Skip.If(corpus.Count == 0, "거부된 시도 코퍼스가 비어 있습니다.");

            var found = FindSelfReinforcing(corpus);
            var recorded = ReadLedger();
            var unrecorded = found.Where(f => !recorded.Contains(f)).ToList();

            Assert.True(
                unrecorded.Count == 0,
                "연속한 두 시도에서 같은 검사가 발화했습니다 - 만족 불가능한 지시일 수 있습니다.\n"
                + string.Join("\n", unrecorded.Select(u => "  " + u))
                + "\n\n고쳤거나 「의도된 것」이라 판정했다면 근거와 함께"
                + " tests/ReSet.Core.Tests/l1-selfreinforcing-baseline.txt 에 적으십시오.");
        }

        private static HashSet<string> ReadLedger()
        {
            var path = Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "l1-selfreinforcing-baseline.txt");
            var recorded = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return recorded;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                // 형식: "<객체> <검사키>  # 판정과 근거"
                var withoutComment = line.Split('#', 2)[0].Trim();
                if (withoutComment.Length > 0) recorded.Add(withoutComment);
            }

            return recorded;
        }
    }
}
