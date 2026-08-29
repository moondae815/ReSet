using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecMaterialCensusTests
    {
        private const string SpecWithVariables = @"# Spec

### 지역 변수 및 시스템 값

| 명칭 | 데이터 타입 | 설명 |
| :--- | :--- | :--- |
| @v_intID | INT | 식별자 |
| @v_intCLTotal | MONEY | 합계 |
";

        private const string SpecWithoutVariables = @"# Spec

### 처리 개요

지역 변수 표가 없는 명세서다.
";

        private const string DdlWithTwoDeclares = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v_intID INT;
    DECLARE @v_intCLTotal MONEY;
    SELECT @v_intID = 1;
END";

        /// <summary>
        /// [실물 규약 - 결함 B] SweepJob.Specs의 FileName은 파일 경로가 아니라
        /// 프로시저 이름 그 자체다("dbo.UP_X.md"가 아니라 "dbo.UP_X") -
        /// SweepJob 문서 주석과 SweepCommand.cs:117 실측이 근거다. ".md"를 붙이면
        /// SpecStatementFactsExtractor가 BareObjectName으로 만드는 키와 어긋나
        /// 전건 미스가 난다(계획서 초안 픽스처가 실제로 그렇게 틀렸다).
        /// </summary>
        private static SweepJob Job(string jobName, string procedure, string spec, string ddl) =>
            new(jobName,
                new List<BatchStepPlan>(),
                new Dictionary<string, string>(),
                new[] { (procedure, spec) },
                new Dictionary<string, string> { [procedure] = ddl },
                new Dictionary<string, string>());

        [Fact]
        public void CountDeclaredVariables_CountsEachDeclareOnce()
        {
            Assert.Equal(2, SpecMaterialCensus.CountDeclaredVariables(DdlWithTwoDeclares));
        }

        [Fact]
        public void CountDeclaredVariables_OnUnparsableDdl_ReturnsZeroInsteadOfThrowing()
        {
            Assert.Equal(0, SpecMaterialCensus.CountDeclaredVariables("this is not sql ((("));
        }

        /// <summary>
        /// DDL에 둘, 명세서에 0 - 소실이다. 객체 이름이 실려야 한다(개수만으로는
        /// 못 되짚는다).
        /// </summary>
        [Fact]
        public void Count_WhenDdlHasFactsButSpecHasNone_ReportsLossWithObjectName()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares) });

            var row = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(2, row.DdlFactCount);
            Assert.Equal(0, row.SpecRowCount);
            Assert.Equal(new[] { "dbo.P" }, row.ObjectsWithLoss);
        }

        /// <summary>
        /// [대조군] 명세서가 표를 담으면 소실이 아니다. 이 단언이 없으면 위 테스트가
        /// "언제나 소실이라고 말하는" 계수로도 통과한다.
        /// </summary>
        [Fact]
        public void Count_WhenSpecHasTheTable_ReportsNoLoss()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithVariables, DdlWithTwoDeclares) });

            var row = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(2, row.DdlFactCount);
            Assert.Equal(2, row.SpecRowCount);
            Assert.Empty(row.ObjectsWithLoss);
        }

        /// <summary>
        /// [판 접기] 같은 원본 SP가 Job 다섯 판에 나와도 한 번만 세어야 한다.
        /// 안 접으면 소실이 5배로 세어져 수가 통째로 왜곡된다 - 태스크 12의 판
        /// 접기와 같은 함정이다.
        /// </summary>
        [Fact]
        public void Count_FoldsTheSameProcedureAcrossJobs()
        {
            var rows = SpecMaterialCensus.Count(new[]
            {
                Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares),
                Job("JobB", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares),
                Job("JobC", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares),
            });

            var row = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(2, row.DdlFactCount);
            Assert.Equal(new[] { "dbo.P" }, row.ObjectsWithLoss);
        }

        /// <summary>
        /// [잴 수 없음 - 결함 A·C] DdlCounterpart가 null인 재료(예: SpecConditions·
        /// RoundingShapes·StepTableSets)는 DdlFactCount도 null이어야 한다. 0으로
        /// 두면 "정상"으로 읽힌다.
        /// </summary>
        [Fact]
        public void Count_ForMaterialsWithNullDdlCounterpart_LeavesDdlFactCountNull()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares) });

            var materialsWithoutCounterpart = SpecMaterials.All.Where(m => m.DdlCounterpart == null).ToList();
            Assert.NotEmpty(materialsWithoutCounterpart);
            foreach (var material in materialsWithoutCounterpart)
            {
                Assert.Null(rows.Single(r => r.MaterialName == material.Name).DdlFactCount);
            }
        }

        /// <summary>
        /// [결함 D - 이 태스크의 핵심 잠금] DdlCounterpart가 null이 아닌데도(대응물
        /// 자체는 있는데) 이 회차가 아직 안 세는 재료(DmlRows·ErrorCodeToOrdinal·
        /// SetTargets·SpecReturnCodes)는 DdlFactCount가 0이 아니라 null이어야 한다.
        /// 계획서 초안(DdlFactCountFor가 LocalVariables만 분기하고 `_ => 0`으로
        /// 떨어지는 switch)은 이 테스트에서 실패한다 - 그 넷이 "DDL 사실 0 · 소실
        /// 없음", 즉 정상으로 읽히기 때문이다.
        /// </summary>
        [Fact]
        public void Count_ForMaterialsWithDdlCounterpartButNotCountedThisRound_LeavesDdlFactCountNull()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares) });

            var uncounted = SpecMaterials.All
                .Where(m => m.DdlCounterpart != null)
                .Where(m => !SpecMaterialCensus.DdlCountedMaterials.Contains(m.Name))
                .ToList();

            // 이 회차가 실제로 무언가는 안 잰다는 사실 자체를 잠근다 - 목록이 비면
            // 아래 foreach가 아무것도 검사하지 않고 거짓으로 통과한다.
            Assert.NotEmpty(uncounted);
            foreach (var material in uncounted)
            {
                Assert.Null(rows.Single(r => r.MaterialName == material.Name).DdlFactCount);
            }
        }

        /// <summary>
        /// [결함 A·C - 명세서 쪽] SpecCountedMaterials에 없는 재료는 SpecRowCount도
        /// null이어야 한다. StepTableSets처럼 명세서 쪽 개념이 아예 없는 재료뿐
        /// 아니라, 이 회차가 아직 안 세는 SpecConditions·RoundingShapes·
        /// SpecReturnCodes도 포함한다.
        /// </summary>
        [Fact]
        public void Count_ForMaterialsNotInSpecCountedMaterials_LeavesSpecRowCountNull()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares) });

            var uncounted = SpecMaterials.All
                .Where(m => !SpecMaterialCensus.SpecCountedMaterials.Contains(m.Name))
                .ToList();

            Assert.NotEmpty(uncounted);
            foreach (var material in uncounted)
            {
                Assert.Null(rows.Single(r => r.MaterialName == material.Name).SpecRowCount);
            }
        }

        /// <summary>
        /// [가장 조용한 실패 양식] SpecMaterials.All의 모든 재료가 census 출력에
        /// 행으로 나와야 한다 - 누락된 재료는 보고서에서 아예 안 보인다.
        /// </summary>
        [Fact]
        public void Count_EveryMaterialInCatalog_AppearsAsARow()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares) });

            Assert.Equal(
                SpecMaterials.All.Select(m => m.Name).OrderBy(x => x, StringComparer.Ordinal),
                rows.Select(r => r.MaterialName).OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>
        /// [집합 자체의 오타 방지] SpecCountedMaterials·DdlCountedMaterials가 이름 댄
        /// 재료가 실제로 SpecMaterials.All에 있어야 한다 - 없으면 그 이름은 죽은
        /// 코드이고, 카탈로그가 그 재료를 조용히 잃었다는 신호일 수도 있다.
        /// </summary>
        [Fact]
        public void CountedMaterialSets_OnlyNameMaterialsThatExistInTheCatalog()
        {
            var catalogNames = SpecMaterials.All.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

            Assert.All(SpecMaterialCensus.SpecCountedMaterials, name => Assert.Contains(name, catalogNames));
            Assert.All(SpecMaterialCensus.DdlCountedMaterials, name => Assert.Contains(name, catalogNames));
        }
    }
}
