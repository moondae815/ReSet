using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepFreezeStateTests
    {
        private static BatchStepPlan Step(string code) =>
            new(code, $"{code} 단계",
                LegacyProcedures: new[] { $"dbo.UP_{code}" },
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        private static readonly IReadOnlyList<BatchStepPlan> Steps =
            new[] { Step("S01"), Step("S02"), Step("S03") };

        private static readonly IReadOnlyDictionary<string, StepDefect> NoFloorViolations =
            new Dictionary<string, StepDefect>();

        [Fact]
        public void NoSignals_FreezesEveryStep()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), NoFloorViolations, Array.Empty<string>());

            Assert.NotNull(open);
            Assert.Empty(open!);
        }

        [Fact]
        public void CriticDefectiveStep_IsOpen()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S02" }, NoFloorViolations, Array.Empty<string>());

            Assert.Equal(new[] { "S02" }, open);
        }

        [Fact]
        public void QualityFloorViolation_IsOpen()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S03"] = new StepDefect(StepDefectKind.QualityFloor, "S03 (본문이 하한 미달)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), floor, Array.Empty<string>());

            Assert.Equal(new[] { "S03" }, open);
        }

        [Fact]
        public void GenerationFailure_IsOpen()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S03"] = new StepDefect(StepDefectKind.GenerationFailed, "S03 (본문 없음)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), floor, Array.Empty<string>());

            Assert.Equal(new[] { "S03" }, open);
        }

        // Unverifiable 은 "대조할 재료가 목차에 없어 검사가 돌지 못했다"이지
        // "본문이 나쁘다"가 아니다. StepDefectKind 의 주석이 "재생성으로 고쳐지지
        // 않는다"고 명시한다. 열어 두면 매 회차 같은 단계를 다시 뽑으면서 판정은
        // 영원히 그대로다 - 예산만 태우고 새 결함을 들인다.
        //
        // 재생성이 못 고치는 것은 루프가 아니라 배너가 처리한다(설계서 §3-7).
        [Fact]
        public void UnverifiableStep_IsNotOpen()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S03"] = new StepDefect(StepDefectKind.Unverifiable, "S03 (대조 재료 없음)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), floor, Array.Empty<string>());

            Assert.NotNull(open);
            Assert.Empty(open!);
        }

        // 단, Critic 이 그 단계를 따로 지목했다면 연다 - 재료가 없는 것과
        // 본문에 결함이 있는 것은 별개다.
        [Fact]
        public void UnverifiableStep_StillOpensWhenCriticNamesIt()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S03"] = new StepDefect(StepDefectKind.Unverifiable, "S03 (대조 재료 없음)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S03" }, floor, Array.Empty<string>());

            Assert.Equal(new[] { "S03" }, open);
        }

        [Fact]
        public void ErrorCodeAttributedStep_IsOpen()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), NoFloorViolations, new[] { "S01" });

            Assert.Equal(new[] { "S01" }, open);
        }

        // 세 신호가 겹쳐도 한 번만 연다. 중복이 남으면 같은 단계를 두 번 생성한다.
        [Fact]
        public void OverlappingSignals_OpenStepOnlyOnce()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S02"] = new StepDefect(StepDefectKind.QualityFloor, "S02 (본문이 하한 미달)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S02" }, floor, new[] { "S02" });

            Assert.Equal(new[] { "S02" }, open);
        }

        // 목차에 없는 코드를 Critic이 지목하면 버린다 - 생성할 대상이 없다.
        [Fact]
        public void UnknownStepCode_IsDiscarded()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S99" }, NoFloorViolations, Array.Empty<string>());

            Assert.NotNull(open);
            Assert.Empty(open!);
        }

        // 목차가 없으면 단계 단위로 열 수 없다. 빈 목록을 내면 "고칠 것이 없다"로
        // 읽히므로 호출부가 전량 재생성을 택하도록 null을 돌려준다.
        [Fact]
        public void NullSteps_ReturnsNull()
        {
            Assert.Null(StepFreezeState.OpenSteps(
                null, new[] { "S01" }, NoFloorViolations, Array.Empty<string>()));
        }

        // 순서는 목차 순서를 따른다. 집합 열거 순서에 맡기면 회차마다 생성 순서가
        // 달라져 로그 대조가 불가능해진다.
        [Fact]
        public void OpenSteps_FollowPlanOrder()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S03", "S01" }, NoFloorViolations, Array.Empty<string>());

            Assert.Equal(new[] { "S01", "S03" }, open);
        }
    }
}
