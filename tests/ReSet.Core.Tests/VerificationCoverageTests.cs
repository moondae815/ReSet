using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class VerificationCoverageTests
    {
        // BatchStepPlan은 위치 기반 레코드다(Code, Name, LegacyProcedures,
        // TargetTables, ErrorCodes, Chunkable, SchemaTables) - 객체 초기자로는
        // 만들어지지 않는다.
        private static IReadOnlyList<BatchStepPlan> Steps(int count)
        {
            var list = new List<BatchStepPlan>();
            for (var i = 1; i <= count; i++)
            {
                list.Add(new BatchStepPlan(
                    $"S{i:00}",
                    $"{i}번 단계",
                    new[] { "UP_X" },
                    new[] { "dbo.T1" },
                    new[] { "-1" },
                    false,
                    System.Array.Empty<string>()));
            }

            return list;
        }

        // 분할이 없었던 회차에는 분모 자체가 없다. 0을 넣으면 "0/0"이 되어
        // 비율처럼 보이는 거짓이 된다 - 실측(POQSettleProc7)에서 단계가 하나도
        // 없는 문서가 가장 높은 점수를 받았고, 그 사실을 숫자로 가리면 안 된다.
        [Fact]
        public void From_WhenSplitDidNotRun_LeavesTotalUnreported()
        {
            var coverage = VerificationCoverage.From(
                adoptedSteps: null,
                stepFloorViolations: new Dictionary<string, StepDefect>(),
                hasDocumentCodeGap: false,
                hasUncoveredProcedures: false);

            Assert.Null(coverage.StepsTotal);
            Assert.False(coverage.SplitRan);
            Assert.True(coverage.NeedsHumanAttention);
        }

        // Unverifiable은 "대조할 재료가 없어 검사를 실행하지 못했다"이므로 빠진다.
        [Fact]
        public void From_SubtractsUnverifiableStepsFromTheVerifiedCount()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S01"] = new StepDefect(StepDefectKind.Unverifiable, "S01 (검증 불가)"),
                ["S03"] = new StepDefect(StepDefectKind.Unverifiable, "S03 (검증 불가)")
            };

            var coverage = VerificationCoverage.From(Steps(19), violations, false, false);

            Assert.Equal(19, coverage.StepsTotal);
            Assert.Equal(17, coverage.StepsVerified);
            Assert.True(coverage.HasUnverifiedSteps);
            Assert.True(coverage.NeedsHumanAttention);
        }

        // QualityFloor는 검사가 돌았고 떨어진 것이다. 여기서 빼면 "검사를 못 돌렸다"와
        // "검사에서 떨어졌다"가 다시 뭉개진다 - StepDefectKind가 그 둘을 가르려고
        // 존재한다.
        [Fact]
        public void From_DoesNotSubtractQualityFloorViolations()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S01"] = new StepDefect(StepDefectKind.QualityFloor, "S01 (하한 미달)"),
                ["S02"] = new StepDefect(StepDefectKind.QualityFloor, "S02 (하한 미달)")
            };

            var coverage = VerificationCoverage.From(Steps(19), violations, false, false);

            Assert.Equal(19, coverage.StepsVerified);
            Assert.False(coverage.HasUnverifiedSteps);
        }

        [Fact]
        public void From_MixedViolations_SubtractsOnlyTheUnverifiableOnes()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S01"] = new StepDefect(StepDefectKind.Unverifiable, "S01 (검증 불가)"),
                ["S02"] = new StepDefect(StepDefectKind.QualityFloor, "S02 (하한 미달)")
            };

            var coverage = VerificationCoverage.From(Steps(10), violations, false, false);

            Assert.Equal(9, coverage.StepsVerified);
        }

        [Fact]
        public void From_WhenEverythingIsClean_NeedsNoHumanAttention()
        {
            var coverage = VerificationCoverage.From(
                Steps(19), new Dictionary<string, StepDefect>(), hasDocumentCodeGap: false, hasUncoveredProcedures: false);

            Assert.Equal(19, coverage.StepsVerified);
            Assert.False(coverage.NeedsHumanAttention);
        }

        // 단계가 전부 검증됐어도 문서 전체 오류코드 대조에서 누락이 나오면
        // 사람이 봐야 한다. 네 조건은 각자 독립적으로 발화한다.
        [Fact]
        public void From_DocumentCodeGapAloneTriggersAttention()
        {
            var coverage = VerificationCoverage.From(
                Steps(19), new Dictionary<string, StepDefect>(), hasDocumentCodeGap: true, hasUncoveredProcedures: false);

            Assert.False(coverage.HasUnverifiedSteps);
            Assert.True(coverage.NeedsHumanAttention);
        }

        // From은 재료를 그대로 옮겨 담을 뿐이다. 값이 뒤섞이지 않는지 확인한다.
        [Fact]
        public void From_MapsHasUncoveredProceduresThrough()
        {
            var coverage = VerificationCoverage.From(
                Steps(19), new Dictionary<string, StepDefect>(),
                hasDocumentCodeGap: false, hasUncoveredProcedures: true);

            Assert.True(coverage.HasUncoveredProcedures);
        }

        // 목차가 원본 프로시저 일부를 어디에도 담지 못한 것은(또는 커버리지
        // 대조 자체를 실행하지 못한 것은) 그 자체로 단계 검증률·오류코드
        // 보존과 독립적인 결함이다. 다른 세 사유가 전부 깨끗해도 이것 하나만으로
        // 사람이 봐야 한다.
        [Fact]
        public void From_HasUncoveredProceduresAlone_NeedsHumanAttention()
        {
            var coverage = VerificationCoverage.From(
                Steps(19), new Dictionary<string, StepDefect>(),
                hasDocumentCodeGap: false, hasUncoveredProcedures: true);

            Assert.True(coverage.SplitRan);
            Assert.False(coverage.HasUnverifiedSteps);
            Assert.False(coverage.HasDocumentCodeGap);
            Assert.True(coverage.NeedsHumanAttention);
        }

        // stepFloorViolations는 회차별 스냅샷이고 adoptedSteps는 채택 확정 후 다시
        // 파싱한 값이다. 구제 채택이 이전 회차 문서를 되살리면 두 집합이 어긋날 수
        // 있는데, 그때 위반 수를 그대로 빼면 존재하지도 않는 단계를 미검증으로 세어
        // 비율이 틀어진다. 채택된 목차에 있는 코드만 센다.
        [Fact]
        public void From_IgnoresViolationsForStepsTheAdoptedOutlineDoesNotContain()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S01"] = new StepDefect(StepDefectKind.Unverifiable, "S01 (검증 불가)"),
                // 채택된 목차에 없는 단계. 이전 회차의 잔재다.
                ["S99"] = new StepDefect(StepDefectKind.Unverifiable, "S99 (검증 불가)")
            };

            var coverage = VerificationCoverage.From(
                Steps(3), violations, hasDocumentCodeGap: false, hasUncoveredProcedures: false);

            Assert.Equal(3, coverage.StepsTotal);
            Assert.Equal(2, coverage.StepsVerified);
        }

        // 위 규칙이 서면 검증 수는 구조적으로 음수가 될 수 없다. 종전에는 클램프가
        // 그 불일치를 "0 검증"이라는 그럴듯한 값으로 덮고 있었다.
        [Fact]
        public void From_WhenEveryViolationIsForeign_CountsEveryStepAsVerified()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S97"] = new StepDefect(StepDefectKind.Unverifiable, "S97 (검증 불가)"),
                ["S98"] = new StepDefect(StepDefectKind.Unverifiable, "S98 (검증 불가)"),
                ["S99"] = new StepDefect(StepDefectKind.Unverifiable, "S99 (검증 불가)")
            };

            var coverage = VerificationCoverage.From(
                Steps(2), violations, hasDocumentCodeGap: false, hasUncoveredProcedures: false);

            Assert.Equal(2, coverage.StepsVerified);
            Assert.False(coverage.HasUnverifiedSteps);
        }

        // 생성 실패는 하한 검사가 돈 적이 없다 - 검사할 본문이 애초에 없었다.
        // QualityFloor와 같은 종류로 두면 "검사가 돌았고 떨어졌다"에 섞여
        // 검증됨으로 집계되고, 19/19 아래에 "이 단계는 생성에 실패했습니다"라고
        // 적힌 섹션이 남는다.
        [Fact]
        public void From_SubtractsGenerationFailuresFromTheVerifiedCount()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S01"] = new StepDefect(StepDefectKind.GenerationFailed, "S01 (생성 실패)")
            };

            var coverage = VerificationCoverage.From(
                Steps(5), violations, hasDocumentCodeGap: false, hasUncoveredProcedures: false);

            Assert.Equal(4, coverage.StepsVerified);
            Assert.True(coverage.HasUnverifiedSteps);
        }

        // 세 종류가 한꺼번에 있을 때 QualityFloor만 검증됨으로 남는다.
        [Fact]
        public void From_AllThreeKinds_OnlyQualityFloorCountsAsVerified()
        {
            var violations = new Dictionary<string, StepDefect>
            {
                ["S01"] = new StepDefect(StepDefectKind.Unverifiable, "S01 (검증 불가)"),
                ["S02"] = new StepDefect(StepDefectKind.GenerationFailed, "S02 (생성 실패)"),
                ["S03"] = new StepDefect(StepDefectKind.QualityFloor, "S03 (하한 미달)")
            };

            var coverage = VerificationCoverage.From(
                Steps(10), violations, hasDocumentCodeGap: false, hasUncoveredProcedures: false);

            Assert.Equal(8, coverage.StepsVerified);
        }
    }
}
