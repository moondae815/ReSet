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
    ///
    /// <para>
    /// [2026-08-30 수정 - Fix Round 3, Important 4] <c>UnscoredAttempts</c>는
    /// "L1 오류 로그 줄이 몇 번 찍혔는가"가 아니라 "채점을 받지 못한 회차가 몇
    /// 개인가"다. 이 둘은 다르다 - §3-3 이후로는 L1 위반이 단계에 귀속되어
    /// 수리되면, "L1 기계 검증 오류 발견 (시도 N/M)" 줄이 여전히 찍히면서도
    /// 그 회차가 결국 채점까지 도달할 수 있다(예산을 먹지 않는다). 로그 줄 수를
    /// 세면 이 경우를 소진으로 오판해, §3-3이 만들려는 개선이 판정에서
    /// 사라진다. 그래서 "TotalAttempts - Trajectory.Count(채점된 회차 수)"로
    /// 계산한다 - 이 값만이 "채점을 못 받은 회차의 수"라는 정의와 일치한다.
    /// Batch4 기준선에서 둘(줄 수 대 이 계산)이 우연히 같은 값(2)을 내는 것은
    /// 그 로그가 "L1 실패 = 곧 전량 재생성 = 곧 회차 소모"였던 옛 규칙 아래서
    /// 났기 때문이지, 이 계산이 늘 로그 줄 수와 같다는 뜻이 아니다.
    /// </para>
    /// </summary>
    public sealed record BatchRunMetrics(
        IReadOnlyList<AttemptScore> Trajectory,
        int MonotonicityViolations,
        int UnscoredAttempts,
        int TotalAttempts,
        long CacheWriteTokens,
        long CacheReadTokens,
        long OutputTokens,
        TimeSpan? WallClock,
        // [Fix Round 3 - Minor 5] 옛 하나의 StructureRedrafted 필드는 L2 정체
        // 재설계(루프가 스스로 판단해 다시 그리는 사건)와 L3 사용자 요청
        // 재설계(사람이 명시적으로 요구한 행동)를 같은 불리언으로 뭉갰다.
        // 두 사건은 로그 문구로 갈린다 - VerificationPipelineOrchestrator.cs의
        // 두 호출 지점이 각각 "재시도가 점수를 개선하지 못해..."와 "사용자가
        // 문서 구조 변경을 요청하여..."로 서로 다른 접두문을 쓰기 때문에,
        // 정규식으로 정직하게 갈랐다.
        bool LoopStagnationRedrafted,
        bool UserRequestedRedrafted);

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
    /// 짧은 궤적이 낫다. (3) 다섯 값이 다 모이면 그 창에서는 더 읽지 않는다.
    /// (4) 그러나 **즉시 확정하지는 않는다** - 다음 앵커(또는 로그 끝)까지 파싱 실패
    /// 줄이 나오지 않은 것을 보고서야 궤적에 싣는다.
    ///
    /// [2026-09-02 A2 - 대조 실행이 드러낸 조용한 결함 둘] 둘 다 이 리더가 **파이프라인이
    /// 실제로 쓴 점수가 아니라 모델이 뱉은 원문**을 읽는 데서 온다.
    /// (a) 다섯 축이 <b>한 줄에</b> 실린 JSON을 못 읽었다 - `ScoreLine.Match`가 줄당 첫
    /// 매치만 취해 축이 영영 다섯이 안 됐고, 그 회차(84점)가 통째로 사라졌다. 위 상수
    /// 주석이 「JSON이 한 줄로 덤프되는 지금 형식」을 전제한다고 적어 둔 것과 정규식이
    /// 요구하는 것이 정반대였다 - 639편 실측이 pretty-print만 봤다. 지금은 `Matches`로
    /// 한 줄 안의 다섯 축을 모두 읽는다.
    /// (b) 파이프라인이 파싱 실패로 <b>버린</b> 응답에서 점수를 건졌다 - 원문의 82점이
    /// 궤적에 실렸지만 파이프라인은 그 회차를 0점으로 처리하고 되돌렸다. 위 (4)가 그것을
    /// 막는다.
    /// 이 판에서는 (a)가 한 회차를 빼고 (b)가 한 회차를 더해 **상쇄**됐다 -
    /// `Trajectory.Count`가 우연히 맞았고 그래서 `UnscoredAttempts`만 옳았다.
    /// 다음 판엔 상쇄되지 않는다.
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
        /// 앵커 뒤 몇 줄까지 점수 블록을 기다리는가.
        ///
        /// [2026-08-30 실측 - Fix Round 2] 이 저장소의 모든 `output.bak-*` 트리에
        /// 있는 로그 639개 전수에서 "앵커 -> 5축 완성"까지의 거리를 쟀다. 최댓값은
        /// 11줄이다(앵커 -> DBG 헤더 -> ```json 펜스 -> { -> HasDefects ->
        /// FeedbackComment -> DefectiveSteps -> 점수 5줄 -> ScoreReadability).
        /// 상한 15는 그 위 4줄의 여유다.
        ///
        /// 이 여유는 JSON이 한 줄로 덤프되는 지금 형식에 기대고 있다.
        /// `FeedbackComment`나 `DefectiveSteps`가 여러 줄로 pretty-print되도록
        /// 형식이 바뀌면, 정상 블록도 이 창을 넘어 조용히 잘릴 수 있다 - 그때는
        /// 이 상수만 올릴 게 아니라 639개를 다시 재야 한다.
        /// </summary>
        private const int MaxAnchorWindowLines = 15;

        private static readonly Regex ReviewAnchor = new(@"리뷰 응답 수신 완료", RegexOptions.Compiled);
        private static readonly Regex ScoreLine = new(
            @"""Score(?<axis>Accuracy|Crud|Interface|Exception|Readability)""\s*:\s*(?<value>\d+)",
            RegexOptions.Compiled);
        // [Fix Round 3 - 조용한 0] max가 항상 숫자라고 가정하면 안 된다.
        // `MaxL2Attempts: "unlimited"`이면 ConsoleUserInteraction.cs:97이
        // "(시도 3/검증 완료까지)"를 찍는다 - 분모가 숫자가 아니라고 분자(회차
        // 번호) 파싱까지 실패하면 TotalAttempts가 조용히 0이 되어 "회차가
        // 없었다"는 거짓 성공처럼 읽힌다. 그래서 분모는 숫자 또는 이 리터럴
        // 문구 중 하나를 받는다.
        private static readonly Regex AttemptLine = new(
            @"\(시도 (?<n>\d+)/(?:\d+|검증 완료까지)\)", RegexOptions.Compiled);
        private static readonly Regex UsageLine = new(
            @"캐시 쓰기:\s*(?<w>\d+),\s*캐시 읽기:\s*(?<r>\d+),\s*출력:\s*(?<o>\d+)",
            RegexOptions.Compiled);
        private static readonly Regex TimestampLine = new(
            @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+", RegexOptions.Compiled);

        // [2026-09-02 A2] 파이프라인이 응답을 **버렸다**는 신호.
        // AiService.ParseReviewResult의 catch가 찍는 줄이고, 그때 ReviewResult는
        // 다섯 축이 전부 0으로 돌아간다 - 즉 원문에 무슨 숫자가 있든 그 회차의
        // 점수는 0이다. 실측(reset-20260830.log:32539)에서 원문은 82점이었는데
        // 파이프라인은 같은 로그 32577행에 "3차 시도(0/100)"라고 적었다.
        //
        // 컨텍스트 이름을 패턴에 넣지 않는다 - 같은 메서드가 단일 SP 리뷰에서도
        // 불리므로 문구만 잡고, 어느 회차의 실패인지는 앵커와의 순서가 정한다.
        private static readonly Regex ReviewParseFailureLine = new(
            @"JSON 검토 보고서 파싱 중 오류 발생", RegexOptions.Compiled);

        // [Fix Round 3 - Minor 5] 두 재설계 사건을 접두문으로 가른다. 접미
        // "목차를 다시 설계합니다"는 공유하지만, 앞의 주어·이유가 사건의 출처를
        // 정직하게 말해준다(VerificationPipelineOrchestrator.cs:2433, :2752).
        private static readonly Regex LoopStagnationRedraftLine = new(
            @"재시도가 점수를 개선하지 못해 목차를 다시 설계합니다", RegexOptions.Compiled);
        private static readonly Regex UserRequestedRedraftLine = new(
            @"사용자가 문서 구조 변경을 요청하여 목차를 다시 설계합니다", RegexOptions.Compiled);

        public static BatchRunMetrics Read(string? logText)
        {
            var lines = (logText ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            var trajectory = new List<AttemptScore>();
            long cacheWrite = 0, cacheRead = 0, output = 0;
            int totalAttempts = 0;
            DateTime? first = null, last = null;
            var loopStagnationRedrafted = false;
            var userRequestedRedrafted = false;

            var collecting = false;
            var linesSinceAnchor = 0;
            var axes = new Dictionary<string, int>(StringComparer.Ordinal);

            // 다섯 축이 모였지만 아직 확정하지 않은 회차. 다음 앵커나 로그 끝에서
            // 확정되고, 그 전에 파싱 실패 줄을 만나면 버려진다.
            AttemptScore? pending = null;

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
                    // 앞 회차가 여기까지 실패 줄 없이 왔으면 파이프라인이 그것을 썼다.
                    if (pending != null)
                    {
                        trajectory.Add(pending);
                        pending = null;
                    }

                    collecting = true;
                    linesSinceAnchor = 0;
                    axes.Clear();
                    continue;
                }

                // 이 앵커의 응답을 파이프라인이 버렸다. 원문에 점수가 있어도 그 회차의
                // 점수는 0이므로 궤적에 실으면 안 된다. 아직 창 안에서 축을 모으는
                // 중이었다면 그것도 함께 버린다.
                if (ReviewParseFailureLine.IsMatch(line))
                {
                    pending = null;
                    collecting = false;
                    axes.Clear();
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
                        // 줄당 첫 매치가 아니라 **모든** 매치를 읽는다 - JSON이 한 줄로
                        // 덤프되면 다섯 축이 같은 줄에 있다(위 (a)).
                        var scores = ScoreLine.Matches(line);
                        if (scores.Count > 0)
                        {
                            foreach (Match score in scores)
                            {
                                var value = int.Parse(score.Groups["value"].Value, CultureInfo.InvariantCulture);
                                if (value < 0 || value > 10)
                                {
                                    // 점수가 아니다. 이 블록을 통째로 버린다.
                                    collecting = false;
                                    axes.Clear();
                                    break;
                                }

                                axes[score.Groups["axis"].Value] = value;
                                if (axes.Count == 5)
                                {
                                    // 확정하지 않고 보류한다 - 파이프라인이 이 응답을
                                    // 파싱하지 못하고 버릴 수 있다(위 (b)).
                                    pending = new AttemptScore(
                                        axes["Accuracy"], axes["Crud"], axes["Interface"],
                                        axes["Exception"], axes["Readability"]);
                                    // 창이 남아 있어도 여기서 멈춘다 - 실물 로그는 같은
                                    // 점수를 [추출된 JSON 내용]으로 한 번 더 싣는데, 계속
                                    // 읽으면 그 중복을 두 번째 회차로 세게 된다.
                                    collecting = false;
                                    axes.Clear();
                                    break;
                                }
                            }

                            continue;
                        }

                        // 점수 줄도 앵커도 아니다 - DBG 헤더나 ```json 펜스 같은
                        // 중간 줄이다. 창 안이면 그냥 넘어가고 계속 기다린다.
                    }
                }

                // [Fix Round 3 - Important 4] L1 오류 줄 자체는 더 이상 세지 않는다.
                // 귀속·수리된 L1 위반도 같은 줄을 찍으면서 채점까지 도달할 수 있어,
                // 줄 수가 "채점 못 받은 회차 수"와 더 이상 같지 않다. 아래 루프 끝의
                // UnscoredAttempts = TotalAttempts - Trajectory.Count가 그 정의를
                // 직접 계산한다.
                if (LoopStagnationRedraftLine.IsMatch(line)) loopStagnationRedrafted = true;
                if (UserRequestedRedraftLine.IsMatch(line)) userRequestedRedrafted = true;

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

            // 마지막 회차는 뒤에 앵커가 없다. 로그가 끝날 때까지 실패 줄이 없었으므로
            // 파이프라인이 그 점수를 썼다.
            if (pending != null)
            {
                trajectory.Add(pending);
            }

            // "채점을 받지 못한 회차의 수"의 정의 그 자체. 로그 줄 수가 아니라
            // TotalAttempts(관측된 최대 시도 번호)에서 Trajectory.Count(실제로
            // 5축이 다 채워져 채점된 회차 수)를 뺀다. Math.Max(0, ...)는 방어적
            // 하한이다 - 두 수가 서로 다른 신호(하나는 "(시도 N/M)" 텍스트, 하나는
            // 앵커+점수 블록)에서 나오므로, 로그가 부분적이거나 형식이 어긋나면
            // 이론상 음수가 나올 수 있다. 그럴 땐 "모른다"에 가까운 0이 "음수
            // 소진"이라는 무의미한 값보다 낫다.
            var unscoredAttempts = Math.Max(0, totalAttempts - trajectory.Count);

            return new BatchRunMetrics(
                trajectory,
                CountMonotonicityViolations(trajectory),
                unscoredAttempts,
                totalAttempts,
                cacheWrite,
                cacheRead,
                output,
                first.HasValue && last.HasValue ? last.Value - first.Value : null,
                loopStagnationRedrafted,
                userRequestedRedrafted);
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
