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

        private const string DdlWithNoDeclares = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT 1;
END";

        private const string DdlWithOneDeclare = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v_intID INT;
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
        /// [태스크 5 변이 4 - 조율자가 리뷰의 미결 항목을 측정으로 바꾸려고 더한 변이가
        /// 잡은 결함] DeclareCursorStatement의 자식은 Name·CursorDefinition뿐이라
        /// DeclareVariableElement와 구조적으로 분리돼 있다 - "커서를 안 센다"는 참이지만,
        /// 이 테스트가 있기 전에는 그 참을 잠그는 단언이 없었다. Visit(DeclareCursorStatement)를
        /// 추가해 커서 이름도 세게 만드는 변이를 넣어도 이 테스트가 생기기 전에는 census
        /// 스위트 70개 전부가 그대로 통과했다(2026-08-29 변이 검증 - docs/audit-reports/
        /// sweeps/2026-08-29-material-census-mutations.md 참고).
        /// </summary>
        [Fact]
        public void CountDeclaredVariables_DoesNotCountCursorDeclarations()
        {
            const string ddlWithCursor = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v_intID INT;
    DECLARE cur_X CURSOR FOR SELECT 1;
    OPEN cur_X;
    CLOSE cur_X;
    DEALLOCATE cur_X;
END";

            Assert.Equal(1, SpecMaterialCensus.CountDeclaredVariables(ddlWithCursor));
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
        /// [태스크 5 변이 2 - 계획서 변이가 잡은 결함] 소실 조건은 「DDL 사실이 있는데
        /// 명세서에 없다」(ddlCount &gt; 0 &amp;&amp; specCount == 0)여야지, 「명세서에
        /// 없다」(specCount == 0)만으로는 안 된다 - DDL도 명세서도 둘 다 표가 없는
        /// 프로시저(지역 변수를 아예 안 쓰는 흔한 경우)까지 소실로 잘못 잡는다.
        /// 이 테스트가 생기기 전에는 그 조건에서 &amp;&amp;를 지워도(specCount == 0 하나로
        /// 넓혀도) census 스위트 전체가 그대로 통과했다 - 기존 픽스처가 전부
        /// DdlWithTwoDeclares(ddlCount == 2 &gt; 0)만 썼기 때문이다.
        /// </summary>
        [Fact]
        public void Count_WhenDdlHasNoFactsAndSpecHasNone_DoesNotReportLoss()
        {
            var rows = SpecMaterialCensus.Count(
                new[] { Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithNoDeclares) });

            var row = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(0, row.DdlFactCount);
            Assert.Equal(0, row.SpecRowCount);
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
        /// [태스크 5 변이 1·5 - 판 접기의 동점 규칙은 「첫 판 승」이다] 위 판 접기
        /// 테스트는 Job 셋의 DDL이 바이트 동일해서 ContainsKey 가드를 지워 매번
        /// 덮어써도(=마지막 판이 이기게 해도) 통과한다 - Dictionary 자체가 프로시저
        /// 이름을 유일 키로 접기 때문에 중복 카운팅은 가드와 무관하게 막힌다. 가드가
        /// 실제로 결정하는 것은 Job마다 내용이 "다를" 때 어느 판이 남는가뿐이다.
        /// 실물에서는 한 스윕 안에서 같은 프로시저가 언제나 같은 단일 Spec.md/DDL을
        /// 읽어 내용이 바이트 동일하므로 이 분기는 오늘의 코퍼스로는 도달 불가하다 -
        /// 그래도 정책 자체는(향후 판별 코퍼스에서 조용한 비결정성이 되지 않도록)
        /// 여기서 못 박는다: 첫 번째로 만난 Job의 내용이 남아야 한다.
        /// </summary>
        [Fact]
        public void Count_WhenProcedureAppearsInMultipleJobsWithDifferentDdl_FirstJobWins()
        {
            var rows = SpecMaterialCensus.Count(new[]
            {
                Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithOneDeclare),
                Job("JobB", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares),
            });

            var row = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(1, row.DdlFactCount);
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

        /// <summary>
        /// [Fix Round 1 Important 2 - 접힌 프로시저 수] 「재료 분모」 절이 자기 분모를
        /// 인쇄하려면 그 분모(접은 프로시저 수)가 census 출력에 실려야 한다. 이 값은
        /// 재료별이 아니라 census 전체의 값이라 모든 행에 같은 값이 실려야 한다 -
        /// 그러지 않으면 라이터가 어느 행을 읽어도 같은 분모를 볼 수 있다는 보장이
        /// 없다.
        /// </summary>
        [Fact]
        public void Count_CarriesTheFoldedProcedureCountOnEveryRow()
        {
            var rows = SpecMaterialCensus.Count(new[]
            {
                Job("JobA", "dbo.P1", SpecWithoutVariables, DdlWithTwoDeclares),
                Job("JobB", "dbo.P2", SpecWithoutVariables, DdlWithOneDeclare),
            });

            Assert.All(rows, row => Assert.Equal(2, row.FoldedProcedureCount));
        }

        /// <summary>
        /// [Fix Round 1 Important 2 - DDL 파싱 실패 분모] CountDeclaredVariables는
        /// 파싱에 실패해도 0을 소프트 페일로 돌려준다(AGENTS.md 범주 2) - 그 0이
        /// "DECLARE가 없다"인지 "파싱을 못 했다"인지는 이 카운터가 없으면 census
        /// 출력만 보고는 구별할 수 없다. FoldedProcedureCount와 마찬가지로 census
        /// 전체의 분모라 모든 행에 같은 값이 실려야 한다.
        /// </summary>
        [Fact]
        public void Count_CarriesTheDdlParseFailureCountOnEveryRow()
        {
            var rows = SpecMaterialCensus.Count(new[]
            {
                Job("JobA", "dbo.P", SpecWithoutVariables, "THIS IS NOT VALID T-SQL ((("),
            });

            Assert.All(rows, row => Assert.Equal(1, row.DdlParseFailureCount));

            var localVariablesRow = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(0, localVariablesRow.DdlFactCount);
        }

        /// <summary>
        /// [대조군] DECLARE가 정말로 하나도 없는 정상 DDL은 파싱 실패가 아니다 -
        /// 위 테스트가 "DDL 사실 0"이기만 하면 통과하는 계수로도 거짓 초록이 되지
        /// 않게 막는다.
        /// </summary>
        [Fact]
        public void Count_DoesNotCountValidDdlWithNoDeclaresAsAParseFailure()
        {
            var rows = SpecMaterialCensus.Count(new[]
            {
                Job("JobA", "dbo.P", SpecWithoutVariables, DdlWithNoDeclares),
            });

            Assert.All(rows, row => Assert.Equal(0, row.DdlParseFailureCount));
        }

        /// <summary>
        /// [Fix Round 1 Minor - per-job 격리] Count() 자신의 job 순회는 이 태스크
        /// 이전에는 가드가 없어 job.DdlByProcedure가 null이면 그 자리에서 예외를
        /// 던졌다(NullReferenceException) - StepSweepService의 per-job try/catch
        /// (jobsThatThrew)와 대칭인 가드가 이 파일에는 없었다. 그 결과 Job 하나의
        /// 결함이 이음매(StepSweepService)의 바깥쪽 try/catch에 걸려 나머지 열일곱
        /// Job의 census까지 통째로 빈 목록이 됐다. 이 테스트는 poison Job과 정상
        /// Job을 함께 넣어 (1) Count가 던지지 않고, (2) 정상 Job의 데이터가 살아
        /// 남고, (3) poison Job 이름이 JobsSkippedForFailure에 실리는 것을 확인한다.
        /// </summary>
        [Fact]
        public void Count_SkipsAPoisonedJobWithoutLosingOtherJobsData()
        {
            var goodJob = Job("GoodJob", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares);
            var poisonJob = goodJob with { JobName = "PoisonJob", DdlByProcedure = null! };

            var rows = SpecMaterialCensus.Count(new[] { poisonJob, goodJob });

            var localVariablesRow = rows.Single(r => r.MaterialName == "LocalVariables");
            Assert.Equal(2, localVariablesRow.DdlFactCount);
            Assert.Equal(new[] { "dbo.P" }, localVariablesRow.ObjectsWithLoss);
            Assert.All(rows, row => Assert.Contains("PoisonJob", row.JobsSkippedForFailure));
        }

        /// <summary>
        /// [Fix Round 1 Minor - 원자성] 한 Job의 Specs 순회는 끝까지 성공하고
        /// DdlByProcedure 순회만 던지면, 공유 사전에 직접 쓰는 구현은 그 Job의
        /// Specs만 절반 반영한 채로 catch에 들어간다 - "이 Job의 재료를 census에서
        /// 건너뜁니다"라는 로그 문구와 실제 동작이 어긋난다. 이 테스트는 poison
        /// Job의 명세서 내용(SpecWithVariables, 표 있음)을 goodJob의 것(표 없음)과
        /// 일부러 다르게 둬서, poison Job의 Specs가 조금이라도 새어 들어오면
        /// SpecRowCount가 goodJob 단독일 때와 달라지는 것으로 잡는다.
        /// </summary>
        [Fact]
        public void Count_PoisonedJobDoesNotPartiallyContributeItsSpecsBeforeFailingOnDdl()
        {
            var poisonJob = new SweepJob(
                "PoisonJob",
                new List<BatchStepPlan>(),
                new Dictionary<string, string>(),
                new[] { ("dbo.P", SpecWithVariables) },
                null!,
                new Dictionary<string, string>());
            var goodJob = Job("GoodJob", "dbo.P", SpecWithoutVariables, DdlWithTwoDeclares);

            var rows = SpecMaterialCensus.Count(new[] { poisonJob, goodJob });

            var row = rows.Single(r => r.MaterialName == "LocalVariables");
            // poisonJob("dbo.P" → 표 있음, 명세서 행 2)이 조금이라도 반영됐다면
            // SpecRowCount가 2가 된다. 원자적으로 통째로 빠졌다면 goodJob의
            // SpecWithoutVariables(표 없음)만 반영돼 0이고, DDL 사실(2)이 있는데
            // 명세서 행이 없으므로 소실로 잡힌다.
            Assert.Equal(0, row.SpecRowCount);
            Assert.Equal(new[] { "dbo.P" }, row.ObjectsWithLoss);
        }
    }
}
