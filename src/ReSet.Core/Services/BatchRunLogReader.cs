using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>회차 하나의 Critic 점수. ReviewResult와 달리 로그에서 복원된 값만 든다.</summary>
    public sealed record AttemptScore(
        int ScoreAccuracy, int ScoreCrud, int ScoreInterface, int ScoreException, int ScoreReadability)
    {
        public int TotalScore =>
            ScoreAccuracy + ScoreCrud + ScoreInterface + ScoreException + ScoreReadability;

        public int NormalizedScore => (int)Math.Round((TotalScore * 100.0) / 50.0);
    }

    /// <summary>
    /// 실행 한 판의 재현성 지표. WallClock이 nullable인 이유: 재료가 없을 때
    /// 0으로 채우면 "0초에 끝났다"는 거짓 측정값이 남는다.
    /// </summary>
    public sealed record BatchRunMetrics(
        IReadOnlyList<AttemptScore> Trajectory,
        int MonotonicityViolations,
        int L1ExhaustedAttempts,
        int TotalAttempts,
        long CacheWriteTokens,
        long CacheReadTokens,
        long OutputTokens,
        TimeSpan? WallClock,
        bool StructureRedrafted);

    /// <summary>
    /// 실행 로그에서 재현성 지표를 뽑는다.
    ///
    /// 손으로 grep하지 않는 이유: 명세서 본문에도 `"ScoreAccuracy"`와 숫자가 있어서
    /// 앵커 없이 읽으면 궤적이 오염된다. 이 설계를 쓰는 동안 실제로 그 사고가 났고,
    /// 오염된 값으로 "Batch2는 66 -> 80 -> 86"이라는 거짓 궤적을 만들었다.
    ///
    /// 방어는 둘이다. (1) `리뷰 응답 수신 완료` 앵커 뒤에서만 읽는다.
    /// (2) 다섯 값이 모두 0~10이어야 채택한다. 하나라도 벗어나면 그 블록 전체를 버린다 -
    /// 거짓 궤적보다 짧은 궤적이 낫다.
    /// </summary>
    public static class BatchRunLogReader
    {
        private static readonly Regex ReviewAnchor = new(@"리뷰 응답 수신 완료", RegexOptions.Compiled);
        private static readonly Regex ScoreLine = new(
            @"""Score(?<axis>Accuracy|Crud|Interface|Exception|Readability)""\s*:\s*(?<value>\d+)",
            RegexOptions.Compiled);
        private static readonly Regex AttemptLine = new(@"\(시도 (?<n>\d+)/(?<max>\d+)\)", RegexOptions.Compiled);
        private static readonly Regex L1Line = new(@"L1 기계 검증 오류 발견 \(시도 \d+/\d+\)", RegexOptions.Compiled);
        private static readonly Regex UsageLine = new(
            @"캐시 쓰기:\s*(?<w>\d+),\s*캐시 읽기:\s*(?<r>\d+),\s*출력:\s*(?<o>\d+)",
            RegexOptions.Compiled);
        private static readonly Regex TimestampLine = new(
            @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+", RegexOptions.Compiled);
        private static readonly Regex RedraftLine = new(@"목차를 다시 설계합니다", RegexOptions.Compiled);

        public static BatchRunMetrics Read(string? logText)
        {
            var lines = (logText ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            var trajectory = new List<AttemptScore>();
            long cacheWrite = 0, cacheRead = 0, output = 0;
            int l1Exhausted = 0, totalAttempts = 0;
            DateTime? first = null, last = null;
            var redrafted = false;

            var collecting = false;
            var axes = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                var ts = TimestampLine.Match(line);
                if (ts.Success &&
                    DateTime.TryParseExact(ts.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    first ??= parsed;
                    last = parsed;
                }

                if (ReviewAnchor.IsMatch(line))
                {
                    collecting = true;
                    axes.Clear();
                    continue;
                }

                if (collecting)
                {
                    var score = ScoreLine.Match(line);
                    if (score.Success)
                    {
                        var value = int.Parse(score.Groups["value"].Value, CultureInfo.InvariantCulture);
                        if (value < 0 || value > 10)
                        {
                            // 점수가 아니다. 이 블록을 통째로 버린다.
                            collecting = false;
                            axes.Clear();
                        }
                        else
                        {
                            axes[score.Groups["axis"].Value] = value;
                            if (axes.Count == 5)
                            {
                                trajectory.Add(new AttemptScore(
                                    axes["Accuracy"], axes["Crud"], axes["Interface"],
                                    axes["Exception"], axes["Readability"]));
                                collecting = false;
                                axes.Clear();
                            }
                        }

                        continue;
                    }

                    // 점수 줄도 아니고 앵커도 아닌 줄이 타임스탬프로 시작하면 블록이 끝난 것이다.
                    if (ts.Success)
                    {
                        collecting = false;
                        axes.Clear();
                    }
                }

                if (L1Line.IsMatch(line)) l1Exhausted++;
                if (RedraftLine.IsMatch(line)) redrafted = true;

                var attempt = AttemptLine.Match(line);
                if (attempt.Success)
                {
                    totalAttempts = Math.Max(
                        totalAttempts, int.Parse(attempt.Groups["n"].Value, CultureInfo.InvariantCulture));
                }

                var usage = UsageLine.Match(line);
                if (usage.Success)
                {
                    cacheWrite += long.Parse(usage.Groups["w"].Value, CultureInfo.InvariantCulture);
                    cacheRead += long.Parse(usage.Groups["r"].Value, CultureInfo.InvariantCulture);
                    output += long.Parse(usage.Groups["o"].Value, CultureInfo.InvariantCulture);
                }
            }

            return new BatchRunMetrics(
                trajectory,
                CountMonotonicityViolations(trajectory),
                l1Exhausted,
                totalAttempts,
                cacheWrite,
                cacheRead,
                output,
                first.HasValue && last.HasValue ? last.Value - first.Value : null,
                redrafted);
        }

        /// <summary>
        /// 직전까지의 최고점보다 낮은 회차의 수. 동점은 위반이 아니다 —
        /// BestAttempt.TryRecord가 동점을 교체하지 않는 것과 같은 규칙이다.
        /// </summary>
        private static int CountMonotonicityViolations(IReadOnlyList<AttemptScore> trajectory)
        {
            var violations = 0;
            var runningMax = int.MinValue;

            foreach (var attempt in trajectory)
            {
                if (runningMax != int.MinValue && attempt.NormalizedScore < runningMax)
                {
                    violations++;
                }

                runningMax = Math.Max(runningMax, attempt.NormalizedScore);
            }

            return violations;
        }
    }
}
