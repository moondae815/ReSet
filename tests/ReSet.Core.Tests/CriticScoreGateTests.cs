using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Critic이 돌려준 5축 점수를 기준 점수와 대조하는 단일 지점.
    ///
    /// 같은 비교가 세 곳에 흩어져 있었다 - 단일 객체 루프의 인라인 블록,
    /// <see cref="RegenerationScopeSelector.FromReview"/>, <see cref="VerificationBanner"/>의
    /// 불합격 사유. 통합 루프에는 아예 없어서 Critic의 자기 신고를 그대로 믿었다.
    /// 축이 늘거나 이름이 바뀔 때 갈라지지 않도록 여기로 모은다.
    /// </summary>
    public class CriticScoreGateTests
    {
        private static ReviewResult Review(int accuracy = 10, int crud = 10, int iface = 10, int exception = 10, int readability = 10) =>
            new ReviewResult
            {
                ScoreAccuracy = accuracy,
                ScoreCrud = crud,
                ScoreInterface = iface,
                ScoreException = exception,
                ScoreReadability = readability
            };

        [Fact]
        public void FailedAxes_AllAxesAboveThreshold_ReturnsEmpty()
        {
            Assert.Empty(CriticScoreGate.FailedAxes(Review(), scoreThreshold: 8));
            Assert.False(CriticScoreGate.HasAxisBelowThreshold(Review(), scoreThreshold: 8));
        }

        [Fact]
        public void FailedAxes_AxisExactlyAtThreshold_Passes()
        {
            // 기준은 "미만이면 실패"다. 딱 맞으면 통과여야 한다.
            var review = Review(accuracy: 8, crud: 8, iface: 8, exception: 8, readability: 8);

            Assert.Empty(CriticScoreGate.FailedAxes(review, scoreThreshold: 8));
        }

        [Fact]
        public void FailedAxes_SingleAxisBelowThreshold_NamesOnlyThatAxis()
        {
            var review = Review(crud: 7);

            Assert.Equal(new[] { "CRUD" }, CriticScoreGate.FailedAxes(review, scoreThreshold: 8));
            Assert.True(CriticScoreGate.HasAxisBelowThreshold(review, scoreThreshold: 8));
        }

        [Fact]
        public void FailedAxes_SeveralAxesBelowThreshold_NamesThemInBannerOrder()
        {
            // VerificationBanner의 평가 점수 줄과 같은 순서여야 사람이 대조할 수 있다.
            var review = Review(accuracy: 1, iface: 2, readability: 3);

            Assert.Equal(new[] { "정합성", "인터페이스", "가독성" }, CriticScoreGate.FailedAxes(review, scoreThreshold: 8));
        }

        [Fact]
        public void FailedAxes_NullReview_TreatedAsFailingEverything()
        {
            // 리뷰가 없는 것은 통과가 아니다. 미탐이 오탐보다 훨씬 나쁘다.
            Assert.True(CriticScoreGate.HasAxisBelowThreshold(null!, scoreThreshold: 8));
            Assert.Equal(5, CriticScoreGate.FailedAxes(null!, scoreThreshold: 8).Count);
        }
    }
}
