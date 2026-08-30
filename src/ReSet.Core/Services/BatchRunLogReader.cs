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
    /// 방어는 셋이다. (1) `리뷰 응답 수신 완료` 앵커 뒤에서만, 앵커로부터
    /// <see cref="MaxAnchorWindowLines"/>줄 안에서만 읽는다. (2) 다섯 값이 모두
    /// 0~10이어야 채택한다. 하나라도 벗어나면 그 블록 전체를 버린다 - 거짓 궤적보다
    /// 짧은 궤적이 낫다. (3) 다섯 값이 다 모이면 즉시 그 회차를 확정하고 그 창에서는
    /// 더 읽지 않는다.
    ///
    /// [2026-08-30 수정 - Fix Round 1] 예전 규칙은 "앵커 뒤에서 점수 줄이 아닌
    /// 타임스탬프 줄을 만나면 블록이 끝난 것"이었다. 실물 로그(POQSettleBatch4
    /// 2026-08-29)는 앵커 바로 다음 줄이 `[DBG] [AI 응답 내용]:`이고 그 줄도
    /// 타임스탬프로 시작한다 - 그 규칙이 JSON 본문을 보기도 전에 매 회차 블록을
    /// 닫아, 리더가 실물 로그에서 0% 작동했다(궤적 0건). 그 실물 로그는 같은 점수를
    /// `[추출된 JSON 내용]`으로 한 번 더 싣기도 한다 - 창을 너무 넓게 잡으면 그
    /// 중복을 두 번째 항목으로 세게 된다. 그래서 창을 "앵커 뒤 몇 줄"로 좁혀서, 첫
    /// 블록이 다 채워지는 순간(위 (3)) 중복 블록에 닿기 전에 멈추게 한다.
    /// </summary>
    public static class BatchRunLogReader
    {
        /// <summary>
        /// 앵커 뒤 몇 줄까지 점수 블록을 기다리는가. 실물 로그의 최장 형태
        /// (앵커 -> DBG 헤더 -> ```json 펜스 -> { -> HasDefects -> FeedbackComment ->
        /// DefectiveSteps -> 점수 5줄 -> ScoreReadability)는 앵커에서 11번째 줄에서
        /// 끝난다. 이 값은 그보다 넉넉하되, 중복 JSON 블록의 점수 줄(약 16번째 줄
        /// 이후)에는 닿지 않을 만큼 좁다.
        /// </summary>
        private const int MaxAnchorWindowLines = 15;

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
            var linesSinceAnchor = 0;
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
                    linesSinceAnchor = 0;
                    axes.Clear();
                    continue;
                }

                if (collecting)
                {
                    linesSinceAnchor++;
                    if (linesSinceAnchor > MaxAnchorWindowLines)
                    {
                        // 창을 넘었다 - 이 응답에는 점수 블록이 없다고 본다.
                        // 채택하지 않고 버린다: 거짓 궤적보다 짧은 궤적이 낫다.
                        collecting = false;
                        axes.Clear();
                    }
                    else
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
                                    // 창이 남아 있어도 여기서 멈춘다 - 실물 로그는 같은
                                    // 점수를 [추출된 JSON 내용]으로 한 번 더 싣는데, 계속
                                    // 읽으면 그 중복을 두 번째 회차로 세게 된다.
                                    collecting = false;
                                    axes.Clear();
                                }
                            }

                            continue;
                        }

                        // 점수 줄도 앵커도 아니다 - DBG 헤더나 ```json 펜스 같은
                        // 중간 줄이다. 창 안이면 그냥 넘어가고 계속 기다린다.
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
