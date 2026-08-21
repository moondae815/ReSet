using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>
    /// Critic이 돌려준 5축 점수를 기준 점수와 대조하는 단일 지점.
    ///
    /// 이 클래스가 존재하는 이유: 같은 비교가 세 곳에 흩어져 있었다 - 단일 객체 루프의
    /// 인라인 블록, <see cref="RegenerationScopeSelector.FromReview"/>, 그리고
    /// <see cref="VerificationBanner"/>의 불합격 사유. 통합 계획서 루프에는 그 블록 자체가
    /// 없어서 Critic이 낮은 점수와 함께 HasDefects: false를 내면 "검증 상태: 통과" 옆에
    /// 낮은 종합 신뢰도가 나란히 찍혔다.
    ///
    /// 프롬프트도 같은 규칙("5축 중 하나라도 기준 미만이면 HasDefects: true")을 지시하지만
    /// 그것은 모델에게 주는 안내일 뿐이다. 게이트는 코드가 잡는다.
    /// </summary>
    public static class CriticScoreGate
    {
        /// <summary>
        /// 기준 점수 미만인 축의 이름. 순서는 <see cref="VerificationBanner"/>의 평가 점수
        /// 줄과 같다 - 사람이 두 줄을 나란히 놓고 대조하기 때문이다.
        ///
        /// review가 null이면 다섯 축 전부를 돌려준다. 리뷰가 없는 것은 통과가 아니다.
        /// </summary>
        public static IReadOnlyList<string> FailedAxes(ReviewResult review, int scoreThreshold)
        {
            var failed = new List<string>();

            if (review == null)
            {
                return new[] { "정합성", "CRUD", "인터페이스", "가독성", "예외" };
            }

            if (review.ScoreAccuracy < scoreThreshold) failed.Add("정합성");
            if (review.ScoreCrud < scoreThreshold) failed.Add("CRUD");
            if (review.ScoreInterface < scoreThreshold) failed.Add("인터페이스");
            if (review.ScoreReadability < scoreThreshold) failed.Add("가독성");
            if (review.ScoreException < scoreThreshold) failed.Add("예외");

            return failed;
        }

        /// <summary>기준 미만인 축이 하나라도 있는가. Critic의 HasDefects 자기 신고를 덮어쓰는 근거다.</summary>
        public static bool HasAxisBelowThreshold(ReviewResult review, int scoreThreshold) =>
            FailedAxes(review, scoreThreshold).Count > 0;
    }
}
