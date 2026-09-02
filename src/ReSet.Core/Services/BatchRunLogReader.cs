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
    /// 호출 종류 하나의 집계. 종류는 로그의 「AI … 요청 전송」 줄에서 그대로 온다 -
    /// 판독기가 종류 목록을 알지 않는다. 새 호출 갈래가 생기면 저절로 한 줄이 는다.
    ///
    /// [왜 종류별로 가르는가 - 2026-08-31 §7-7] 「호출당 캐시 쓰기」를 전체 호출로
    /// 나누면 <b>개선이 성공할수록 나빠진다</b>. §3-9(입력 축소)가 좁힌 것은 단계
    /// 섹션 호출뿐이고, 골격·브레인스토밍·목차는 의도적으로 안 건드린다. 회차가 줄어
    /// 분모가 97→68로 작아지자 그 고정비의 몫이 커져, 단계 섹션 호출이 기준을 통과
    /// (91,455 ≤ 99,000)했는데도 전체 평균은 102,090으로 미달로 읽혔다.
    /// </summary>
    public sealed record AiCallGroup(
        string Kind, int Calls, long CacheWriteTokens, long CacheReadTokens, long OutputTokens)
    {
        /// <summary>호출당 캐시 쓰기. 호출이 0이면 0이다 - 나눗셈을 호출부에 미루지 않는다.</summary>
        public long CacheWritePerCall => Calls == 0 ? 0 : CacheWriteTokens / Calls;
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
        bool UserRequestedRedrafted,

        // [2026-09-02 A3] 아래 셋은 기존 필드 뒤에 붙인다 - 앞에 끼우면 위치
        // 인자로 만드는 자리가 조용히 어긋난다.

        /// <summary>호출 종류별 집계. §3-9의 대상인 단계 섹션 호출을 따로 읽기 위한 재료다.</summary>
        IReadOnlyList<AiCallGroup> CallsByKind,

        /// <summary>
        /// 점수가 직전 최고점 아래로 내려갔는데 <b>되돌림 로그가 없는</b> 회차의 수.
        /// §3-1이 실제로 약속한 것이고, 롤백이 옳게 작동하면 0이다.
        /// <see cref="BatchRunMetrics.MonotonicityViolations"/>와 달리 완벽한 롤백
        /// 아래서 0에 닿을 수 있다.
        /// </summary>
        int UnrolledBackRegressions,

        /// <summary>
        /// 로그가 말하는 최종 채택본의 환산 점수. 채택 줄도 통과 줄도 없으면 null이다 -
        /// 0으로 채우면 없는 판정이 생긴다.
        /// </summary>
        int? AdoptedScore,

        /// <summary>
        /// 최종 채택본이 궤적의 최고점인가. 판정할 재료가 없으면 null이다.
        /// 게이트 통과로 끝난 판은 <b>마지막 회차</b>가 채택되므로 이 값이 false일
        /// 수 있다 - 그 자리가 이 검사의 존재 이유다.
        /// </summary>
        bool? AdoptedIsBest);

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
        // [2026-09-02 A3] §3-1이 약속한 것을 재는 재료. 롤백 줄은
        // VerificationPipelineOrchestrator.cs:2435가, 채택 줄은 같은 파일의 네 자리
        // (:1035 중단 · :1072/:2299 L1 · :1182/:2719 L2 · :1201/:2750 리뷰 실패)가
        // 공유하는 문구다. 게이트 통과로 끝난 판에는 채택 줄이 없고
        // ConsoleUserInteraction.NotifyValidationSuccess의 통과 줄만 남는다 -
        // 그때 채택되는 것은 마지막 회차다.
        private static readonly Regex RollbackLine = new(
            @"(?<n>\d+)차 시도\((?<s>\d+)/100\)가 최고 후보\(", RegexOptions.Compiled);
        private static readonly Regex AdoptionLine = new(
            @"가장 높은 점수를 받은 (?<n>\d+)차 시도\((?<s>\d+)/100\)를 채택합니다", RegexOptions.Compiled);
        private static readonly Regex ValidationPassedLine = new(
            @"L1/L2 자동 검증 모두 통과", RegexOptions.Compiled);

        // 사용량 줄을 어느 호출에 귀속할지 정하는 줄.
        //
        // [왜 요청이 아니라 응답인가 - 2026-09-02 실측] 단계 섹션 호출은 **병렬로
        // 돈다**. `reset-20260830.log`의 22:25:51 부근에서 S18 재시도 요청 바로 뒤에
        // 온 사용량이 실제로는 S20의 것이었고, 요청 줄로 짝지으면 68건 중 10건
        // (673,030 토큰, 9.7%)이 짝을 잃는다. 사용량은 클라이언트가 찍고 응답은
        // 서비스가 곧바로 찍으므로 그 둘이 제어 흐름으로 붙어 있다 - 실측 68건 중
        // 66건이 **정확히 다음 줄**이고, 나머지 둘(브레인스토밍·목차 수립)은 응답
        // 줄 자체가 없는 갈래다. 그 둘은 순차 실행이라 요청 줄로 짝지어도 옳다.
        private static readonly Regex AiRequestLine = new(
            @"AI (?<kind>[^-]+?) 요청 전송", RegexOptions.Compiled);
        private static readonly Regex AiResponseLine = new(
            @"AI (?<kind>[^-]+?) 응답 수신 완료", RegexOptions.Compiled);

        /// <summary>
        /// 사용량 줄에서 몇 줄 안의 응답 줄까지 자기 짝으로 보는가. 실측 66건이
        /// 전부 정확히 1이고, 2는 그 위의 여유다. 종류가 다른 호출끼리는 동시에
        /// 돌지 않으므로(골격 → 단계 섹션은 단계가 갈린다) 병렬 구간에서 줄이
        /// 끼어들어도 귀속되는 <b>종류</b>는 같다.
        /// </summary>
        private const int MaxUsageToResponseLines = 2;

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

            // [A3] 되돌림 줄이 말한 점수를 나온 순서대로 담는다. 채점받지 못한
            // 회차(0점 처리)의 줄도 그대로 담긴다 - 궤적과 짝지을 때 건너뛴다.
            var rollbackScores = new List<int>();
            int? adoptedScore = null;
            var sawValidationPassed = false;

            // [A3] 호출 종류별 집계. 순서를 지키려고 목록과 색인을 함께 든다.
            var callKinds = new List<string>();
            var callTotals = new Dictionary<string, (int Calls, long Write, long Read, long Output)>(StringComparer.Ordinal);
            const string UnknownKind = "(요청 줄 없음)";

            // 요청 줄은 한 번만 쓰인다. 요청이 실패해 사용량 줄이 안 나오면, 다음
            // 사용량 줄이 낡은 종류를 조용히 빨아들이는 것을 막는다. 이것은
            // 응답 줄이 없는 갈래를 위한 **폴백**이다.
            string? pendingKind = null;

            // 짝지을 응답 줄을 기다리는 사용량. 창을 넘으면 폴백으로 확정된다.
            (int Line, long Write, long Read, long Output, string? Fallback)? pendingUsage = null;
            var lineNumber = 0;

            void Attribute(string kind, long w, long r, long o)
            {
                if (!callTotals.TryGetValue(kind, out var totals))
                {
                    callKinds.Add(kind);
                    totals = (0, 0, 0, 0);
                }

                callTotals[kind] = (totals.Calls + 1, totals.Write + w, totals.Read + r, totals.Output + o);
            }

            void FlushPendingUsage()
            {
                if (pendingUsage == null) return;
                var u = pendingUsage.Value;
                Attribute(u.Fallback ?? UnknownKind, u.Write, u.Read, u.Output);
                pendingUsage = null;
            }

            foreach (var line in lines)
            {
                lineNumber++;

                // 창을 넘긴 사용량은 자기 응답 줄이 없는 갈래다 - 폴백으로 확정한다.
                if (pendingUsage != null && lineNumber - pendingUsage.Value.Line > MaxUsageToResponseLines)
                {
                    FlushPendingUsage();
                }

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

                var request = AiRequestLine.Match(line);
                if (request.Success)
                {
                    pendingKind = request.Groups["kind"].Value.Trim();
                }

                var response = AiResponseLine.Match(line);
                if (response.Success && pendingUsage != null)
                {
                    var u = pendingUsage.Value;
                    Attribute(response.Groups["kind"].Value.Trim(), u.Write, u.Read, u.Output);
                    pendingUsage = null;
                }

                var rollback = RollbackLine.Match(line);
                if (rollback.Success)
                {
                    rollbackScores.Add(int.Parse(rollback.Groups["s"].Value, CultureInfo.InvariantCulture));
                }

                var adoption = AdoptionLine.Match(line);
                if (adoption.Success)
                {
                    adoptedScore = int.Parse(adoption.Groups["s"].Value, CultureInfo.InvariantCulture);
                }

                if (ValidationPassedLine.IsMatch(line))
                {
                    sawValidationPassed = true;
                }

                var usage = UsageLine.Match(line);
                if (usage.Success)
                {
                    var w = long.Parse(usage.Groups["w"].Value, CultureInfo.InvariantCulture);
                    var r = long.Parse(usage.Groups["r"].Value, CultureInfo.InvariantCulture);
                    var o = long.Parse(usage.Groups["o"].Value, CultureInfo.InvariantCulture);

                    cacheWrite += w;
                    cacheRead += r;
                    output += o;

                    // 앞의 사용량이 아직 짝을 못 찾았으면 여기서 폴백으로 확정한다 -
                    // 사용량 둘이 하나의 응답 줄을 나눠 가질 수는 없다.
                    FlushPendingUsage();

                    pendingUsage = (lineNumber, w, r, o, pendingKind);
                    pendingKind = null;
                }
            }

            FlushPendingUsage();

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

            // 채택본은 채택 줄이 말한다. 그 줄이 없고 통과 줄만 있으면 게이트를
            // 통과해 끝난 판이고, 그때 채택되는 것은 마지막 회차다.
            if (adoptedScore == null && sawValidationPassed && trajectory.Count > 0)
            {
                adoptedScore = trajectory[^1].NormalizedScore;
            }

            bool? adoptedIsBest = adoptedScore.HasValue && trajectory.Count > 0
                ? adoptedScore.Value == trajectory.Max(a => a.NormalizedScore)
                : null;

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
                userRequestedRedrafted,
                callKinds
                    .Select(k => new AiCallGroup(k, callTotals[k].Calls, callTotals[k].Write, callTotals[k].Read, callTotals[k].Output))
                    .ToList(),
                CountUnrolledBackRegressions(trajectory, rollbackScores),
                adoptedScore,
                adoptedIsBest);
        }

        /// <summary>
        /// 점수가 직전 최고점 아래로 내려갔는데 되돌림 줄이 없는 회차의 수.
        ///
        /// [왜 이것이 §3-1의 지표인가] §3-1이 약속한 것은 「하락한 회차의 산출물을
        /// 버리고 최고 후보 상태로 되감는다」이지 「Critic 점수가 안 내려간다」가
        /// 아니다. 롤백은 문서 상태를 되돌릴 뿐 다음 회차의 채점을 올려 주지
        /// 않으므로, <see cref="CountMonotonicityViolations"/>는 롤백이 완벽해도
        /// 0이 되지 않는다(§7-6).
        ///
        /// [짝짓기 규칙] 되돌림 줄은 궤적보다 <b>많을 수</b> 있다 - 채점받지 못한
        /// 회차(파싱 실패로 0점 처리)도 되돌림 줄을 남기는데 궤적에는 없다. 그래서
        /// 하락 회차마다 남은 되돌림 줄에서 같은 점수를 <b>앞으로 훑어</b> 찾고,
        /// 찾으면 거기까지 소비한다. 못 찾으면 위반 하나를 세되 <b>아무것도 소비하지
        /// 않는다</b> - 소비하면 뒤의 정상 회차까지 줄줄이 위반으로 번진다.
        /// 남는 방향은 무해하고 모자라는 방향만 결함이다.
        /// </summary>
        private static int CountUnrolledBackRegressions(
            IReadOnlyList<AttemptScore> trajectory, IReadOnlyList<int> rollbackScores)
        {
            var violations = 0;
            var runningMax = int.MinValue;
            var next = 0;

            foreach (var attempt in trajectory)
            {
                var score = attempt.NormalizedScore;

                if (runningMax != int.MinValue && score < runningMax)
                {
                    var found = -1;
                    for (var i = next; i < rollbackScores.Count; i++)
                    {
                        if (rollbackScores[i] == score)
                        {
                            found = i;
                            break;
                        }
                    }

                    if (found >= 0)
                    {
                        next = found + 1;
                    }
                    else
                    {
                        violations++;
                    }
                }

                runningMax = Math.Max(runningMax, score);
            }

            return violations;
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
