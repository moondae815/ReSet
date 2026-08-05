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

        /// <summary>
        /// L1 실패 회차의 프롬프트를 조립한다. 이번 회차에 반드시 해소해야 할 형식 오류를
        /// 앞에 두고, 지금까지 누적된 Critic 지적을 뒤에 붙인다.
        ///
        /// 이전에는 호출부가 L1 수정 지시로 feedbackLog를 통째로 덮어썼다. Actor는 매번
        /// 백지에서 다시 쓰므로 그 회차는 내용 교정 이력이 전부 빠진 채 생성됐다.
        /// history 자체는 살아남아 다음 L2 실패 때 되살아나므로 영구 손실은 아니었지만,
        /// 한 회차가 비어서 나가는 것만으로도 품질이 무너진다.
        ///
        /// 아직 L2 라운드가 없으면 l1Fix를 그대로 돌려준다 — 가장 흔한 경우의 프롬프트가
        /// 달라지지 않아야 한다.
        /// </summary>
        public static string ComposeAfterL1Failure(string? l1Fix, IReadOnlyList<string> history)
        {
            var fix = l1Fix ?? string.Empty;

            if (history == null || history.Count == 0)
            {
                return fix;
            }

            return $"[L1 기계 검증 오류 — 이번 회차에 반드시 해소]\n{fix}\n\n" +
                Compose(history,
                    "※ 지시사항: 위 형식 오류를 먼저 해소하고, 누적 피드백에서 이미 반영한 " +
                    "내용 교정의 서술 수준을 낮추지 마십시오. 원본 DDL을 절대적 기준으로 삼으십시오.");
        }
    }
}
