using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class RetryRescueTests
    {
        private static ReviewResult Review() => new()
        {
            HasDefects = true,
            ScoreAccuracy = 9,
            ScoreCrud = 10,
            ScoreInterface = 9,
            ScoreReadability = 9,
            ScoreException = 7,
            FeedbackComment = "예외 처리를 보완하십시오."
        };

        // 후보가 없으면 구제할 것이 없다. 호출부는 현행 폴백으로 가야 한다.
        [Fact]
        public void TryRescue_WithNoCandidate_ReturnsNull()
        {
            var best = new BestAttempt();

            var rescued = RetryRescue.TryRescue(best, 8, 3, RetryAbortReason.GenerationFailed);

            Assert.Null(rescued);
        }

        // 구제본은 배너가 붙은 상태로 나온다. 호출부가 배너를 다시 붙이면 안 된다.
        [Fact]
        public void TryRescue_WithCandidate_PrefixesTheBannerToTheStoredMarkdown()
        {
            var best = new BestAttempt();
            best.TryRecord(2, "본문내용", Review());

            var rescued = RetryRescue.TryRescue(best, 8, 3, RetryAbortReason.GenerationFailed);

            Assert.NotNull(rescued);
            Assert.Contains("[품질 불합격]", rescued!.Markdown);
            Assert.Contains("3차 시도가 AI 생성 호출 실패로 중단되어", rescued.Markdown);
            Assert.Contains("2차 시도를 채택했습니다", rescued.Markdown);
            Assert.EndsWith("본문내용", rescued.Markdown);
            Assert.Equal(2, rescued.AttemptNumber);
            Assert.Equal(88, rescued.Review.NormalizedScore);
        }

        // 정상 소진은 구제가 아니다. 루프가 끝까지 돌았으므로 중단 사유가 없다.
        [Fact]
        public void TryRescue_WithNullReason_OmitsTheAdoptionLine()
        {
            var best = new BestAttempt();
            best.TryRecord(2, "본문내용", Review());

            var rescued = RetryRescue.TryRescue(best, 8, 3, null);

            Assert.NotNull(rescued);
            Assert.Contains("[품질 불합격]", rescued!.Markdown);
            Assert.DoesNotContain("채택 경위", rescued.Markdown);
        }
    }
}
