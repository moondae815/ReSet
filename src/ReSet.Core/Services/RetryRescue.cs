using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>구제로 채택된 문서와 그 리뷰. Markdown에는 배너가 이미 붙어 있다.</summary>
    public sealed record RescuedAttempt(string Markdown, ReviewResult Review, int AttemptNumber, AiResult? Generation);

    /// <summary>
    /// 재시도 루프가 끝났을 때 보관 중인 최선본을 채택할지 결정한다.
    ///
    /// 이 클래스가 존재하는 이유: 실패 경로들이 <see cref="BestAttempt"/>의 존재를 몰라,
    /// 이미 L1을 통과하고 채점까지 받은 문서를 확보해 놓고도 버렸다. 특히 생성 호출이
    /// 예외를 던지면 SP 전체를 폐기해 좋은 문서까지 함께 사라졌다.
    ///
    /// 채택 규칙을 이곳에서만 정의한다. 호출 자리가 아홉 곳(순차 SP 루프 넷, 배치 계획
    /// 루프 다섯 — 생성 실패·L1 소진·"결함이나 자리 못 댐" 무효 확정·채점 예산 소진·
    /// 리뷰 실패)이라 각자 조립하면 반드시 어긋난다 — 같은 규칙이 쌍둥이 루프에 흩어져
    /// 생긴 사고가 이미 세 번 있었다.
    /// </summary>
    public static class RetryRescue
    {
        /// <summary>
        /// 보관 중인 후보가 없으면 null을 돌려준다 — 호출부는 현행 폴백으로 진행한다.
        /// reason이 null이면 정상 소진이며 배너에 채택 경위 줄이 붙지 않는다.
        ///
        /// 구제 자리에 도달한 후보는 반드시 결함을 갖는다. 결함 없는 시도도 TryRecord로
        /// 기록되지만, 그 직후 루프가 통과로 빠져나가 이 메서드까지 오지 않는다.
        /// 따라서 품질 불합격 배너가 항상 정확하다.
        /// </summary>
        public static RescuedAttempt? TryRescue(
            BestAttempt best, int scoreThreshold, int abortedAttempt, RetryAbortReason? reason)
        {
            var candidate = best?.Current;
            if (candidate == null)
            {
                return null;
            }

            var context = reason.HasValue
                ? new RescueContext(reason.Value, abortedAttempt, candidate.AttemptNumber)
                : null;

            return new RescuedAttempt(
                VerificationBanner.QualityRejected(candidate.Review, scoreThreshold, context) + candidate.Markdown,
                candidate.Review,
                candidate.AttemptNumber,
                candidate.Generation);
        }
    }
}
