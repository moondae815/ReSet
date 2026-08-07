using System.Collections.Generic;
using ReSet.Cli;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ConsoleUserInteractionTests
    {
        private static IReadOnlyList<BatchStepPlan> ThreeSteps() => new[]
        {
            new BatchStepPlan("S01", "수수료율 스냅샷", new[] { "UP_A" }, new[] { "dbo.T1" }, new[] { "-1" }, false),
            new BatchStepPlan("S02", "정산 원장 생성", new[] { "UP_B" }, new[] { "dbo.T2" }, new[] { "-2" }, false),
            new BatchStepPlan("S03", "취소 원장 반영", new[] { "UP_C" }, new[] { "dbo.T3" }, new[] { "-3" }, false)
        };

        // 프로덕션 헬퍼를 그대로 부른다 — 여기서 직접 포맷을 복제하면, 프롬프트가
        // 이스케이프하도록 바뀌어도 이 테스트는 대괄호 없는 픽스처에서 항등을
        // 유지해 그 어긋남을 가려 버린다.
        private static string LabelOf(BatchStepPlan step) => ConsoleUserInteraction.StepSelectionLabel(step);

        [Fact]
        public void MapStepSelection_WithNoSelection_MeansFullRegeneration()
        {
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(new string[0], ThreeSteps());

            Assert.Empty(codes);
            Assert.False(skeleton);
        }

        [Fact]
        public void MapStepSelection_WithSomeSteps_ReturnsOnlyThoseCodes()
        {
            var steps = ThreeSteps();
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(
                new[] { LabelOf(steps[0]), LabelOf(steps[2]) }, steps);

            Assert.Equal(new[] { "S01", "S03" }, codes);
            Assert.False(skeleton);
        }

        // 골격의 공통 규약이 바뀌면 그것을 인용한 모든 섹션이 낡는다.
        // 그래서 골격 선택은 단계 선택을 덮어써 전체 재생성이 된다.
        [Fact]
        public void MapStepSelection_WithSkeleton_ForcesFullRegenerationRegardlessOfSteps()
        {
            var steps = ThreeSteps();
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(
                new[] { ConsoleUserInteraction.SkeletonSelectionLabel, LabelOf(steps[1]) }, steps);

            Assert.True(skeleton);
            Assert.Empty(codes);
        }

        [Fact]
        public void MapStepSelection_WithSkeletonOnly_ForcesFullRegeneration()
        {
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(
                new[] { ConsoleUserInteraction.SkeletonSelectionLabel }, ThreeSteps());

            Assert.True(skeleton);
            Assert.Empty(codes);
        }

        // 라벨이 목록에 없으면 조용히 무시한다. 프롬프트가 돌려주는 값만
        // 들어오므로 발생하지 않지만, 매핑이 예외를 던지면 승인 화면이 죽는다.
        [Fact]
        public void MapStepSelection_WithUnknownLabel_IgnoresIt()
        {
            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(
                new[] { "존재하지 않는 라벨" }, ThreeSteps());

            Assert.Empty(codes);
            Assert.False(skeleton);
        }

        // 단계 이름은 모델이 쓴 목차 JSON에서 그대로 온다. 대괄호가 섞이면
        // Spectre가 마크업 태그로 오인해 프롬프트를 죽인다(Program.cs의 다른
        // 선택지 렌더링 자리처럼 Markup.Escape가 필요한 이유). 프롬프트가 보여주는
        // 라벨(StepSelectionLabel)과 매핑이 찾는 라벨이 같은 헬퍼를 쓰는 한, 이스케이프된
        // 문자열끼리도 정상적으로 일치해야 한다.
        [Fact]
        public void MapStepSelection_WithBracketInStepName_StillMatchesTheEscapedLabel()
        {
            var steps = new[]
            {
                new BatchStepPlan("S01", "[긴급] 수수료율 스냅샷", new[] { "UP_A" }, new[] { "dbo.T1" }, new[] { "-1" }, false)
            };

            var label = ConsoleUserInteraction.StepSelectionLabel(steps[0]);

            // Spectre.Console의 이스케이프 규약(대괄호를 두 배로 늘림)을 그대로 썼는지
            // 확인한다 — Program.cs:940/:999가 이미 쓰는 Markup.Escape와 동일해야 한다.
            Assert.Equal("S01  [[긴급]] 수수료율 스냅샷", label);

            var (codes, skeleton) = ConsoleUserInteraction.MapStepSelection(new[] { label }, steps);

            Assert.Equal(new[] { "S01" }, codes);
            Assert.False(skeleton);
        }

        [Fact]
        public void HumanReviewResult_DefaultsToFullRegeneration()
        {
            var result = new ReSet.Core.Models.HumanReviewResult();

            Assert.NotNull(result.TargetStepCodes);
            Assert.Empty(result.TargetStepCodes);
            Assert.False(result.RegenerateSkeleton);
        }
    }
}
