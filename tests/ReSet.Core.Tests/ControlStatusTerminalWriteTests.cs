using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 계약이 선언한 상태값 중 <b>그 컬럼엔 한 번도 안 쓰이는데 같은 값이 형제 상태
    /// 컬럼에는 쓰이는 것</b>을 잡는다 (축 B 잔여 결함 <b>T25</b>).
    ///
    /// 사전 선언: docs/audit-reports/2026-09-08-T25-사전선언.md
    ///
    /// [왜 「선언됐는데 0 회」로 넓히지 않는가 - 실측이 정했다]
    /// 코퍼스 실측(2026-09-08): 넓히면 발화 6 중 <b>5 가 오탐</b>이다 - `Skipped`·
    /// `Pending`·`Info`·`Warning`·`Error` 는 그냥 안 쓰는 값일 수 있고 안 쓰는 것이
    /// 결함이라는 근거가 없다. 「같은 값이 다른 상태 컬럼엔 쓰인다」로 좁히면 정확히
    /// 하나가 남고 그것이 T25 다(`RunStatus.Failed` 0 대 `StepStatus.Failed` 4).
    /// </summary>
    public class ControlStatusTerminalWriteTests
    {
        private static readonly BatchStepPlan S01 = new(
            "S01", "부트스트랩", new string[0], new[] { "batch.BatchRun", "batch.BatchRunLock" },
            new[] { "-9010" }, false, new string[0]);

        private static readonly BatchStepPlan S02 = new(
            "S02", "본작업", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
            new[] { "-1" }, false, new string[0]);

        private static IReadOnlyList<BatchStepPlan> Steps => new[] { S01, S02 };

        private static Dictionary<string, string> Sections(string s01Body, string s02Body) =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["S01"] = "### S01\n\n```sql\n" + s01Body + "\n```\n",
                ["S02"] = "### S02\n\n```sql\n" + s02Body + "\n```\n"
            };

        // 실물 모양이다 - 실행을 열고(Running), 마지막에 성공만 적고(Succeeded),
        // 단계 저널에는 실패를 적는다(StepStatus = Failed). RunStatus = Failed 만 없다.
        private const string OpensAndSucceeds =
            "INSERT INTO batch.BatchRun (JobName, BatchYmd, RunStatus) VALUES (@j, @y, N'Running');\n" +
            "UPDATE batch.BatchRun SET RunStatus = N'Succeeded' WHERE RunId = @r;";

        private const string JournalsAFailure =
            "UPDATE batch.BatchStepJournal SET StepStatus = N'Failed' WHERE RunId = @r AND StepCode = @c;";

        [Fact]
        public void ReportsAStatusValueThatIsNeverWrittenToItsOwnColumnButIsWrittenElsewhere()
        {
            var defects = new MechanicalValidator().ValidateControlStatusTerminalWrites(
                Sections(OpensAndSucceeds, JournalsAFailure), Steps);

            Assert.True(defects.ContainsKey("S01"));
            Assert.Contains("RunStatus", defects["S01"].Reason);
            Assert.Contains("Failed", defects["S01"].Reason);
        }

        [Fact]
        public void StaysSilentWhenTheValueIsActuallyWrittenToItsOwnColumn()
        {
            var defects = new MechanicalValidator().ValidateControlStatusTerminalWrites(
                Sections(OpensAndSucceeds + "\nUPDATE batch.BatchRun SET RunStatus = N'Failed' WHERE RunId = @r;",
                         JournalsAFailure),
                Steps);

            Assert.Empty(defects);
        }

        [Fact]
        public void StaysSilentWhenNoOtherColumnWritesThatValueEither()
        {
            // 이것이 A 안과 갈리는 지점이다. 아무도 안 쓰는 값은 「미구현」이 아니라
            // 「이 판에 해당 없음」일 수 있다 - 안 쓰는 것이 결함이라는 근거가 없다.
            var defects = new MechanicalValidator().ValidateControlStatusTerminalWrites(
                Sections(OpensAndSucceeds, "UPDATE dbo.T1 SET C = 1;"), Steps);

            Assert.Empty(defects);
        }

        [Fact]
        public void ProseMentionDoesNotSilenceTheCheck()
        {
            // 펜스 정책(사전 선언 §4) - 산문을 세면 문장 하나가 검사를 침묵시킨다.
            var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["S01"] = "### S01\n\n실패 시 `RunStatus = N'Failed'` 로 기록합니다.\n\n```sql\n"
                          + OpensAndSucceeds + "\n```\n",
                ["S02"] = "### S02\n\n```sql\n" + JournalsAFailure + "\n```\n"
            };

            var defects = new MechanicalValidator().ValidateControlStatusTerminalWrites(sections, Steps);

            Assert.True(defects.ContainsKey("S01"));
        }

        [Fact]
        public void StaysSilentWhenNoStepOwnsTheControlTable()
        {
            // 귀속이 불가능하면 보고하지 않는다(작성 계약 7). batch.BatchRun 을 대상으로
            // 선언한 단계가 없으면 계약의 ResolveRowCreators 가 담당을 안 내놓는다.
            var noOwner = new[]
            {
                new BatchStepPlan("S01", "부트스트랩", new string[0], new[] { "dbo.T9" },
                    new[] { "-9010" }, false, new string[0]),
                S02
            };

            var defects = new MechanicalValidator().ValidateControlStatusTerminalWrites(
                Sections(OpensAndSucceeds, JournalsAFailure), noOwner);

            Assert.Empty(defects);
        }

        [Fact]
        public void StaysSilentWithoutSectionsOrSteps()
        {
            var validator = new MechanicalValidator();

            Assert.Empty(validator.ValidateControlStatusTerminalWrites(
                new Dictionary<string, string>(), Steps));
            Assert.Empty(validator.ValidateControlStatusTerminalWrites(
                Sections(OpensAndSucceeds, JournalsAFailure), Array.Empty<BatchStepPlan>()));
        }

        [Fact]
        public void DoesNotReadStringLiteralsThroughACleansingHelper()
        {
            // [D1 이 밟은 함정 - 2026-09-06] CleanedSqlFences·CleanedCodeFencesExcludingDiagrams 는
            // BlankCommentsAndStrings 로 **문자열 리터럴을 지운다.** 이 검사가 찾는 것이
            // 정확히 문자열 리터럴이므로 그 헬퍼를 쓰면 영영 발화하지 못한다.
            // 이 시험은 대입이 문자열로만 구별되는 최소 본문으로 그 경로를 잠근다.
            var defects = new MechanicalValidator().ValidateControlStatusTerminalWrites(
                Sections("UPDATE batch.BatchRun SET RunStatus = N'Running';",
                         "UPDATE batch.BatchStepJournal SET StepStatus = N'Failed';"),
                Steps);

            Assert.True(defects.ContainsKey("S01"));
        }

        // ── 코퍼스 ────────────────────────────────────────────────────────────
        //
        // [왜 필요한가 - 이 검사는 `--sweep` 에 안 보인다]
        // 스윕은 ValidateBatchStep(단계 층)만 돌린다. 이 검사는 문서 층이라 스윕 수치가
        // 통째로 불변이고(실측 2026-09-08: A 0 · B 2 · C 1 · D 0 · E 0 · 미분류 23 그대로),
        // 그래서 **검사가 죽어도 게이트가 모른다.** 이 단언이 그 자리의 유일한 탐지기다.
        //
        // [오라클을 검사와 따로 센다] 아래 CountRaw 는 제품 헬퍼를 안 쓰고 직접 센다 -
        // 제품 코드로 기대값을 만들면 둘이 같이 틀려도 초록이다.
        //
        // [크기가 아니라 모양을 못박는다] Job 수나 발화 수를 숫자로 박으면 코퍼스가 자랄
        // 때마다 빨개지고 다음 사람이 관측 대신 기대값을 고친다. 여기서는 **Job 마다
        // 관측이 예측하는 대로 발화하는가**만 본다 - 코퍼스가 고쳐지면 발화 0 이 정상이다.

        private static int CountRaw(string body, string column, string value) =>
            Regex.Matches(body, column + @"\s*=\s*N?'" + value + "'", RegexOptions.IgnoreCase).Count;

        [SkippableFact]
        public void CorpusFiresExactlyWhereTheObservedAsymmetryIs()
        {
            var root = CorpusPaths.RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var jobsDir = Path.Combine(root, "output", "Jobs");
            Skip.IfNot(Directory.Exists(jobsDir), CorpusSkip.Reason);

            var checkedJobs = 0;
            foreach (var jobDir in Directory.EnumerateDirectories(jobsDir).OrderBy(d => d))
            {
                var plan = Path.Combine(jobDir, "raw", "PlanStructure.md");
                var stepsDir = Path.Combine(jobDir, "agent", "steps");
                if (!File.Exists(plan) || !Directory.Exists(stepsDir)) continue;

                var steps = BatchStepPlanParser.TryParse(File.ReadAllText(plan));
                if (steps == null || steps.Count == 0) continue;

                var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var st in steps)
                {
                    var f = Path.Combine(stepsDir, st.Code + ".md");
                    if (File.Exists(f)) sections[st.Code] = File.ReadAllText(f);
                }
                if (sections.Count == 0) continue;
                checkedJobs++;

                var body = string.Join("\n", sections.Values);
                // 관측: RunStatus 에 Failed 가 0 인데 StepStatus 에는 있는가.
                var asymmetric = CountRaw(body, "RunStatus", "Failed") == 0
                                 && CountRaw(body, "StepStatus", "Failed") > 0;

                var defects = new MechanicalValidator().ValidateControlStatusTerminalWrites(sections, steps);
                var fired = defects.Values.Any(d => d.Reason.Contains("RunStatus") && d.Reason.Contains("Failed"));

                Assert.True(asymmetric == fired,
                    $"{Path.GetFileName(jobDir)}: 관측 비대칭={asymmetric} 인데 발화={fired} 다.");
            }

            Skip.If(checkedJobs == 0, CorpusSkip.Reason);
        }

        [SkippableFact]
        public void CorpusNeverFiresForAValueNoStatusColumnWrites()
        {
            // 조건 ② 가 실물에서도 거르는지 - A 안(선언됐는데 0 회)이 냈던 오탐 다섯
            // (Skipped·Pending·Info·Warning·Error)이 하나도 안 나와야 한다.
            var root = CorpusPaths.RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var jobsDir = Path.Combine(root, "output", "Jobs");
            Skip.IfNot(Directory.Exists(jobsDir), CorpusSkip.Reason);

            var neverWritten = new[] { "Skipped", "Pending", "Info", "Warning", "Error" };
            var seen = 0;
            foreach (var jobDir in Directory.EnumerateDirectories(jobsDir).OrderBy(d => d))
            {
                var plan = Path.Combine(jobDir, "raw", "PlanStructure.md");
                var stepsDir = Path.Combine(jobDir, "agent", "steps");
                if (!File.Exists(plan) || !Directory.Exists(stepsDir)) continue;
                var steps = BatchStepPlanParser.TryParse(File.ReadAllText(plan));
                if (steps == null || steps.Count == 0) continue;

                var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var st in steps)
                {
                    var f = Path.Combine(stepsDir, st.Code + ".md");
                    if (File.Exists(f)) sections[st.Code] = File.ReadAllText(f);
                }
                if (sections.Count == 0) continue;
                seen++;

                var body = string.Join("\n", sections.Values);
                var defects = new MechanicalValidator().ValidateControlStatusTerminalWrites(sections, steps);

                foreach (var value in neverWritten)
                {
                    // 그 값이 이 Job 어디에도 안 쓰였다면 발화해서는 안 된다.
                    var writtenSomewhere = BatchControlContract.Tables
                        .Where(t => !string.IsNullOrWhiteSpace(t.StatusColumn))
                        .Any(t => CountRaw(body, t.StatusColumn!, value) > 0);
                    if (writtenSomewhere) continue;

                    Assert.DoesNotContain(defects.Values,
                        d => d.Reason.Contains("`" + value + "`"));
                }
            }

            Skip.If(seen == 0, CorpusSkip.Reason);
        }

    }
}
