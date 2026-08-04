namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도 루프가 만들어 낸 후보 중 가장 점수가 높은 하나를 보관한다.
    ///
    /// 이 클래스가 존재하는 이유: 재시도가 소진되면 파이프라인이 마지막 시도를 그대로
    /// 확정했다. 2026-08-04 dbo.UP_Util_PG_Client_CMRate_Ins 실행에서 시도 2가 90점,
    /// 시도 3이 78점이었는데 78점이 산출물이 됐다. 시도 2는 다섯 항목 중 예외 하나만
    /// 기준에 미달했고 나머지는 정합성 10, 인터페이스 9, 가독성 10이었다.
    ///
    /// 갱신 규칙을 이곳에서만 정의한다. 두 재시도 루프가 각자 비교식을 쓰면 한쪽만
    /// 고쳐지는 사고가 그대로 재발한다.
    /// </summary>
    public sealed class BestAttempt
    {
        public string? Markdown { get; private set; }
        public ReviewResult? Review { get; private set; }
        public int AttemptNumber { get; private set; }

        public bool HasCandidate => Review != null;

        /// <summary>
        /// 후보를 제시한다. 기존 최고보다 점수가 높을 때만 교체하고 교체 여부를 돌려준다.
        /// 동점이면 교체하지 않는다 — 나중 시도가 더 낫다는 근거가 없고, 실제로 후속
        /// 시도가 이미 만점이던 항목을 망가뜨리는 사례가 관찰됐다.
        /// </summary>
        public bool TryRecord(int attemptNumber, string markdown, ReviewResult review)
        {
            if (review == null)
            {
                return false;
            }

            if (Review != null && review.NormalizedScore <= Review.NormalizedScore)
            {
                return false;
            }

            Markdown = markdown;
            Review = review;
            AttemptNumber = attemptNumber;
            return true;
        }
    }
}
