using System.Threading.Tasks;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Anthropic은 캐시 쓰기에 1.25배, 읽기에 0.1배를 청구한다. 실측 5개 잡 중 4개가
    /// L2를 1회차에 끝내므로, 첫 전송에 중단점을 찍으면 그 4건은 손해가 확정된다.
    /// 두 번째 전송부터 찍으면 1회차 잡의 비용은 그대로이고 재생성 회차만 이득을 본다.
    /// </summary>
    public class PromptCacheBreakpointPolicyTests
    {
        [Fact]
        public void FirstSightOfAPrefix_DoesNotMarkABreakpoint()
        {
            var policy = new PromptCacheBreakpointPolicy();

            Assert.False(policy.ShouldMarkBreakpoint("SharedSystem", "SharedContext"));
        }

        [Fact]
        public void SecondSightOfTheSamePrefix_MarksABreakpoint()
        {
            var policy = new PromptCacheBreakpointPolicy();

            policy.ShouldMarkBreakpoint("SharedSystem", "SharedContext");

            Assert.True(policy.ShouldMarkBreakpoint("SharedSystem", "SharedContext"));
        }

        // 접두사는 시스템 프롬프트와 user 프롬프트를 함께 본다. 둘 중 하나만 달라도
        // 캐시 접두사가 다르므로 처음 보는 것으로 취급해야 한다.
        [Fact]
        public void ADifferentPrefix_IsTrackedIndependently()
        {
            var policy = new PromptCacheBreakpointPolicy();

            policy.ShouldMarkBreakpoint("SharedSystem", "SharedContext");

            Assert.False(policy.ShouldMarkBreakpoint("SharedSystem", "OtherContext"));
            Assert.False(policy.ShouldMarkBreakpoint("OtherSystem", "SharedContext"));
        }

        // 장시간 프로세스가 SP를 계속 분석해도 기억이 무한히 자라면 안 된다.
        // 축출된 접두사는 처음 보는 것으로 되돌아간다 — 중단점을 찍지 않아 손해가 없다.
        [Fact]
        public void WhenCapacityIsExceeded_TheOldestPrefixIsEvicted()
        {
            var policy = new PromptCacheBreakpointPolicy(capacity: 2);

            policy.ShouldMarkBreakpoint("S", "A");
            policy.ShouldMarkBreakpoint("S", "B");
            policy.ShouldMarkBreakpoint("S", "C");

            Assert.False(policy.ShouldMarkBreakpoint("S", "A"));
            Assert.True(policy.ShouldMarkBreakpoint("S", "C"));
        }

        // StepConcurrency가 4라 동시 호출이 있다. 같은 접두사가 병렬로 들어와도
        // 예외 없이 정확히 한 번만 "처음"으로 판정되어야 한다.
        [Fact]
        public async Task ConcurrentCallsWithTheSamePrefix_YieldExactlyOneFirstSight()
        {
            var policy = new PromptCacheBreakpointPolicy();
            var tasks = new Task<bool>[16];

            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() => policy.ShouldMarkBreakpoint("S", "U"));
            }

            var results = await Task.WhenAll(tasks);

            int firstSights = 0;
            foreach (var r in results)
            {
                if (!r) firstSights++;
            }
            Assert.Equal(1, firstSights);
        }
    }
}
