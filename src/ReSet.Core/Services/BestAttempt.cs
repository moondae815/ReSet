using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도 루프가 만든 후보 하나. 네 값이 함께 움직이므로 한 덩어리로 든다.
    ///
    /// 흩어 두면 두 가지가 깨진다. "후보가 있는가"를 물을 때마다 어느 필드를 봐야
    /// 하는지 정해야 하고(그 판단이 두 곳에 복제됐다), 채택된 시도를 가리키는 값들
    /// 중 하나만 갱신되어 서로 어긋날 수 있다.
    /// </summary>
    public sealed record AttemptCandidate(string Markdown, ReviewResult Review, int AttemptNumber, AiResult? Generation);

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
        /// <summary>보관 중인 최고 점수 후보. 아직 없으면 null.</summary>
        public AttemptCandidate? Current { get; private set; }

        /// <summary>
        /// 후보를 제시한다. 기존 최고보다 점수가 높을 때만 교체하고 교체 여부를 돌려준다.
        /// 동점이면 교체하지 않는다 — 나중 시도가 더 낫다는 근거가 없고, 실제로 후속
        /// 시도가 이미 만점이던 항목을 망가뜨리는 사례가 관찰됐다.
        ///
        /// generation이 nullable인 이유: 순차 SP 루프는 accumulatedThinking에 모든 시도의
        /// 추론을 누적해 내보내므로 채택본 하나를 가리킬 필요가 없다. 단일 AiResult를
        /// 스냅샷하는 배치 루프만 실제 값을 넘긴다.
        /// </summary>
        public bool TryRecord(int attemptNumber, string markdown, ReviewResult review, AiResult? generation = null)
        {
            if (review == null)
            {
                return false;
            }

            if (Current != null && review.NormalizedScore <= Current.Review.NormalizedScore)
            {
                return false;
            }

            Current = new AttemptCandidate(markdown, review, attemptNumber, generation);
            return true;
        }
    }
}
