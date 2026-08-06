namespace ReSet.Core.Models
{
    public enum UserDecision
    {
        Approve,          // 승인 및 최종 저장 (Approve)
        ProvideFeedback,   // 추가 보완 요청 피드백 입력 (Feedback)
        Cancel            // 저장 없이 이탈 (Cancel)
    }

    public class HumanReviewResult
    {
        public UserDecision Decision { get; set; }
        public string? UserFeedback { get; set; }

        /// <summary>
        /// 이 피드백이 문서 구조(목차)까지 바꾸는가. Decision이 ProvideFeedback일 때만
        /// 의미가 있다.
        ///
        /// 별도 인터페이스 메서드를 두지 않는 이유: 피드백 본문과 그 성격은 함께
        /// 움직이는 값이므로 이미 피드백을 나르는 이 자리에 싣는다.
        /// </summary>
        public bool RedraftStructure { get; set; }
    }
}
