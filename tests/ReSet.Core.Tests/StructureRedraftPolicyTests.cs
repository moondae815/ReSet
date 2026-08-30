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

            Assert.False(policy.TryConsume(improvedThisAttempt: true, structureDefective: true));
            Assert.False(policy.Consumed);
        }

        // 실측(POQSettleBatch4 2026-08-29): 미갱신 1회로 발동한 재설계가 14단계 체계를
        // 16단계로 갈아엎고 골격·섹션 캐시를 전부 폐기했고, 곧바로 3·4차가 L1에서
        // 연속으로 떨어져 예산 4회 중 2회를 태웠다. 1차는 후보가 없어 항상 갱신되므로
        // 미갱신 1회 조건은 사실상 2차 결과 하나에 거는 도박이었다.
        [Fact]
        public void SingleStagnantAttempt_DoesNotRedraft()
        {
            var policy = new StructureRedraftPolicy();

            Assert.False(policy.TryConsume(improvedThisAttempt: false, structureDefective: true));
            Assert.False(policy.Consumed);
        }

        [Fact]
        public void TwoConsecutiveStagnantAttemptsWithStructureDefect_Redrafts()
        {
            var policy = new StructureRedraftPolicy();

            policy.TryConsume(improvedThisAttempt: false, structureDefective: true);

            Assert.True(policy.TryConsume(improvedThisAttempt: false, structureDefective: true));
            Assert.True(policy.Consumed);
        }

        // 정체해도 Critic이 구조 결함을 짚지 않았다면 원인은 본문이다.
        // 목차를 갈아엎으면 L1을 통과하던 구조를 잃는다.
        [Fact]
        public void StagnantWithoutStructureDefect_DoesNotRedraft()
        {
            var policy = new StructureRedraftPolicy();

            policy.TryConsume(improvedThisAttempt: false, structureDefective: false);

            Assert.False(policy.TryConsume(improvedThisAttempt: false, structureDefective: false));
            Assert.False(policy.Consumed);
        }

        // 개선이 한 번 나오면 연속 카운터가 끊긴다.
        [Fact]
        public void ImprovementResetsTheStagnationStreak()
        {
            var policy = new StructureRedraftPolicy();

            policy.TryConsume(improvedThisAttempt: false, structureDefective: true);
            policy.TryConsume(improvedThisAttempt: true, structureDefective: true);

            Assert.False(policy.TryConsume(improvedThisAttempt: false, structureDefective: true));
            Assert.False(policy.Consumed);
        }

        // Job당 1회. 구조를 한 번 갈아엎었는데도 정체하면 원인은 목차가 아니다.
        [Fact]
        public void AfterConsumption_NeverRedraftsAgain()
        {
            var policy = new StructureRedraftPolicy();
            policy.TryConsume(improvedThisAttempt: false, structureDefective: true);
            policy.TryConsume(improvedThisAttempt: false, structureDefective: true);

            Assert.False(policy.TryConsume(improvedThisAttempt: false, structureDefective: true));
            Assert.True(policy.Consumed);
        }
    }
}
