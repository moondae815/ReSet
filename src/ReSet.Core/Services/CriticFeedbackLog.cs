using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도 라운드 사이에 Actor에게 주입할 Critic 피드백을 조립한다.
    /// 문구를 이곳에서만 만든다.
    ///
    /// 이전 구현은 라운드마다 이력을 통째로 비우고 최신 지적 하나만 넣었다. Actor는
    /// 매번 백지에서 다시 쓰므로(GenerateSpecificationAsync는 이전 명세서를 받지 않는다)
    /// 앞 라운드에서 이미 정리된 오류가 되살아났다. 2026-08-04 실행에서 시도 3은 앞선
    /// 시도에서 정리됐던 조인 서술을 '자체조인'으로 되돌렸다.
    ///
    /// 항목별 점수를 함께 싣는 이유: Actor는 산문 피드백만 받아 어느 항목이 기준에
    /// 미달인지 몰랐다. "예외만 부족하다"가 명시되면 멀쩡한 항목을 갈아엎을 이유가 준다.
    /// </summary>
    public static class CriticFeedbackLog
    {
        /// <summary>
        /// 보관할 최근 라운드 수. 기본 설정(MaxL2Attempts=2)에서는 최대 2개라 닿지 않고,
        /// unlimited 모드에서 프롬프트가 무한히 커지는 것을 막는다.
        /// </summary>
        public const int MaxRetainedRounds = 3;

        public static void Record(List<string> history, int attempt, ReviewResult review, int scoreThreshold)
        {
            // 점수 나열 순서는 VerificationBanner와 같게 유지한다. 두 산출물을 눈으로
            // 대조하기 때문이다.
            history.Add(
                $"### [시도 {attempt} 피드백]\n" +
                $"- 이 시도의 점수: 정합성 {review.ScoreAccuracy}, CRUD {review.ScoreCrud}, " +
                $"인터페이스 {review.ScoreInterface}, 가독성 {review.ScoreReadability}, " +
                $"예외 {review.ScoreException} (기준 {scoreThreshold})\n" +
                $"- 지적사항: {review.FeedbackComment}");

            while (history.Count > MaxRetainedRounds)
            {
                history.RemoveAt(0);
            }
        }

        public static string Compose(IReadOnlyList<string> history, string instruction) =>
            $"[L2 AI 리뷰 누적 피드백 (최근 {history.Count}개 라운드)]:\n" +
            string.Join("\n\n", history) +
            "\n\n" + instruction;
    }
}
