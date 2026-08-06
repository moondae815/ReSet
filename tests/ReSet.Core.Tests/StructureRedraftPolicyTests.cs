using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StructureRedraftPolicyTests
    {
        [Fact]
        public void NewPolicy_HasNotConsumedItsRedraft()
        {
            var policy = new StructureRedraftPolicy();

            Assert.False(policy.Consumed);
        }

        // 개선이 나오는 동안은 목차가 원인이 아니다. 멀쩡한 구조를 갈아엎지 않는다.
        [Fact]
        public void ImprovingAttempt_DoesNotRedraft()
        {
            var policy = new StructureRedraftPolicy();

            Assert.False(policy.TryConsume(improvedThisAttempt: true));
            Assert.False(policy.Consumed);
        }

        // 이 설계의 핵심. 기본 예산(총 3회)에서 2차가 최고점을 못 넘기면
        // 그 자리에서 목차를 다시 세워야 3차가 새 구조로 생성된다.
        // 2회 연속을 요구하면 기본 예산에서는 영원히 발동하지 못한다.
        [Fact]
        public void FirstAttemptWithoutImprovement_Redrafts()
        {
            var policy = new StructureRedraftPolicy();

            Assert.True(policy.TryConsume(improvedThisAttempt: false));
            Assert.True(policy.Consumed);
        }

        // Job당 1회. 구조를 한 번 갈아엎었는데도 정체하면 원인은 목차가 아니다.
        [Fact]
        public void SecondAttemptWithoutImprovement_DoesNotRedraftAgain()
        {
            var policy = new StructureRedraftPolicy();
            policy.TryConsume(improvedThisAttempt: false);

            Assert.False(policy.TryConsume(improvedThisAttempt: false));
            Assert.True(policy.Consumed);
        }

        // 소진 이후에는 개선 여부와 무관하게 항상 false다.
        [Fact]
        public void AfterConsumption_ImprovementStillDoesNotRedraft()
        {
            var policy = new StructureRedraftPolicy();
            policy.TryConsume(improvedThisAttempt: false);

            Assert.False(policy.TryConsume(improvedThisAttempt: true));
        }
    }
}
