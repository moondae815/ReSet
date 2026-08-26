using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// SP 하나가 여러 단계로 쪼개지면 어느 단계가 그 SP의 무엇을 맡는지 알 수 없다.
    /// 단계마다 전량을 요구하면 만족 불가능하고, 아무것도 요구하지 않으면 그 SP의
    /// 코드가 문서 어디에도 없어도 통과한다. 의무를 문서 단위로 올려 둘 다 피한다.
    /// </summary>
    public class SplitProcedureObligationTests
    {
        private static readonly BatchStepPlan S10 = new(
            "S10", "예외 1", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
            new[] { "-1", "-2" }, false, new string[0]);

        private static readonly BatchStepPlan S11 = new(
            "S11", "예외 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
            new[] { "-1", "-2" }, false, new string[0]);

        // [대소문자 함정] SpecReturnCodeExtractor.BareName은 키를 소문자로 정규화하고
        // 프로덕션 사전은 StringComparer.OrdinalIgnoreCase로 만들어진다. 여기서 기본
        // 비교자를 쓰면 "UP_X" 키가 소문자 조회("up_x")에 걸리지 않아 조회가 항상
        // 실패하고, 테스트가 엉뚱한 이유로 통과하거나 깨진다.
        private static readonly Dictionary<string, IReadOnlyList<string>> Codes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_X"] = new[] { "-1", "-2" }
            };

        [Fact]
        public void ShouldPassWhenEveryCodeAppearsInAtLeastOneSharingStep()
        {
            var sections = new Dictionary<string, string>
            {
                ["S10"] = "SET @v = -1;",
                ["S11"] = "SET @v = -2;"
            };

            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                sections, new[] { S10, S11 }, Codes, null);

            Assert.Empty(defects);
        }

        [Fact]
        public void ShouldFlagEverySharingStepWhenACodeAppearsNowhere()
        {
            // 한 단계로 지목할 수 없다 - 어느 단계가 그 코드를 맡았어야 하는지
            // 알 방법이 없기 때문이다. 공유 단계 전부가 재생성 대상이 된다.
            var sections = new Dictionary<string, string>
            {
                ["S10"] = "SET @v = -1;",
                ["S11"] = "SET @v = -1;"
            };

            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                sections, new[] { S10, S11 }, Codes, null);

            Assert.Equal(2, defects.Count);
            Assert.Contains("-2", defects["S10"].Reason);
            Assert.Contains("-2", defects["S11"].Reason);
            Assert.Equal(StepDefectKind.QualityFloor, defects["S10"].Kind);
        }

        [Fact]
        public void ShouldIgnoreAProcedureThatOnlyOneStepOwns()
        {
            // 분할되지 않은 SP는 단계 검사가 그대로 본다. 여기서 또 보면
            // 같은 결함이 두 번 발화된다.
            var solo = new BatchStepPlan(
                "S05", "원장", new[] { "dbo.UP_Y" }, new[] { "dbo.T1" },
                new[] { "-9" }, false, new string[0]);

            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                new Dictionary<string, string> { ["S05"] = "본문에 코드가 없다" },
                new[] { solo },
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UP_Y"] = new[] { "-9" }
                },
                null);

            Assert.Empty(defects);
        }

        [Fact]
        public void WithoutMaterial_ShouldReportNothing()
        {
            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                new Dictionary<string, string> { ["S10"] = "", ["S11"] = "" },
                new[] { S10, S11 }, null, null);

            Assert.Empty(defects);
        }
    }
}
