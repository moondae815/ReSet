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
        public void ShouldPassWhenTheTableAppearsInAtLeastOneSharingStep()
        {
            // 계획서가 준 네 테스트는 codesByProcedure만 넘긴다. tablesByProcedure를
            // 아예 안 보고 스킵해도 그 네 테스트는 통과한다 - 테이블 축을 단독으로
            // 세워 확인한다.
            var tables = new Dictionary<string, SpecTargetTableExtractor.StepTableSets>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["UP_X"] = new(new[] { "dbo.T2" }, new string[0])
            };

            var sections = new Dictionary<string, string>
            {
                ["S10"] = "INSERT INTO dbo.T2 VALUES (1);",
                ["S11"] = "SET @v = -2;"
            };

            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                sections, new[] { S10, S11 }, null, tables);

            Assert.Empty(defects);
        }

        [Fact]
        public void ShouldFlagEverySharingStepWhenATableAppearsNowhere()
        {
            var tables = new Dictionary<string, SpecTargetTableExtractor.StepTableSets>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["UP_X"] = new(new[] { "dbo.T2" }, new string[0])
            };

            var sections = new Dictionary<string, string>
            {
                ["S10"] = "SET @v = -1;",
                ["S11"] = "SET @v = -2;"
            };

            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                sections, new[] { S10, S11 }, null, tables);

            Assert.Equal(2, defects.Count);
            Assert.Contains("T2", defects["S10"].Reason);
            Assert.Contains("T2", defects["S11"].Reason);
        }

        [Fact]
        public void WithNullSections_ShouldReportNothingAndNotThrow()
        {
            // sectionsByStepCode·allSteps는 시그니처가 non-nullable로 선언하지만, 호출부
            // 결함으로 null이 들어올 수 있다 - 조기 반환이 없으면 아래 SelectMany·
            // TryGetValue가 그대로 NullReferenceException을 던진다.
            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                null!, new[] { S10, S11 }, Codes, null);

            Assert.Empty(defects);
        }

        [Fact]
        public void WithNullAllSteps_ShouldReportNothingAndNotThrow()
        {
            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                new Dictionary<string, string> { ["S10"] = "SET @v = -1;" }, null!, Codes, null);

            Assert.Empty(defects);
        }

        [Fact]
        public void WhenAStepSharesTwoDifferentSplitProcedures_BothMissingReasonsSurvive()
        {
            // S20은 UP_A(S20·S21이 나눠 맡음)와 UP_B(S20·S22가 나눠 맡음) 양쪽에 다
            // 걸쳐 있다. 두 프로시저 모두 결함이 있으면, 나중에 처리되는 프로시저의
            // 대입이 먼저 것을 통째로 덮어써 첫 결함의 진단이 사라질 수 있다 -
            // 같은 단계가 서로 다른 두 분할 SP를 겸하는 흔치 않은 경우다.
            var s20 = new BatchStepPlan(
                "S20", "겸임", new[] { "dbo.UP_A", "dbo.UP_B" }, new[] { "dbo.T1" },
                new string[0], false, new string[0]);
            var s21 = new BatchStepPlan(
                "S21", "UP_A 분담", new[] { "dbo.UP_A" }, new[] { "dbo.T1" },
                new string[0], false, new string[0]);
            var s22 = new BatchStepPlan(
                "S22", "UP_B 분담", new[] { "dbo.UP_B" }, new[] { "dbo.T1" },
                new string[0], false, new string[0]);

            var codes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_A"] = new[] { "-1" },
                ["UP_B"] = new[] { "-2" }
            };

            var sections = new Dictionary<string, string>
            {
                ["S20"] = "본문에 아무 코드도 없다.",
                ["S21"] = "본문에 아무 코드도 없다.",
                ["S22"] = "본문에 아무 코드도 없다."
            };

            var defects = new MechanicalValidator().ValidateSplitProcedureObligations(
                sections, new[] { s20, s21, s22 }, codes, null);

            Assert.Equal(3, defects.Count);
            Assert.Contains("-1", defects["S20"].Reason);
            Assert.Contains("-2", defects["S20"].Reason);
            Assert.Contains("-1", defects["S21"].Reason);
            Assert.Contains("-2", defects["S22"].Reason);
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
