using System.Collections.Generic;

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

        /// <summary>
        /// 사용자가 지목한 재생성 대상 단계 코드. Decision이 ProvideFeedback이고
        /// RedraftStructure가 false일 때만 의미가 있다.
        ///
        /// 비어 있으면 전체 재생성이다 — "아무것도 안 고름"과 "전체"를 같은 뜻으로
        /// 둔다. 골격을 고른 경우에도 비운다(RegenerateSkeleton 주석 참조).
        /// </summary>
        public List<string> TargetStepCodes { get; set; } = new();

        /// <summary>
        /// 골격(개요·Mermaid 흐름도·검증 SQL 세트)도 다시 만들지 여부.
        ///
        /// 공통 규약이 골격에 있고 모든 단계 섹션이 그것을 전제로 쓰였으므로,
        /// 이 값이 true면 TargetStepCodes는 비어야 한다 — 규약이 바뀌면 그것을
        /// 인용한 섹션도 전부 다시 써야 한다.
        /// </summary>
        public bool RegenerateSkeleton { get; set; }
    }
}
