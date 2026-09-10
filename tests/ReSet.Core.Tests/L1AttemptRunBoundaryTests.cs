using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「한 파일에 두 판이 이어 붙어도 판이 갈리는가.」
    ///
    /// [무엇이 결함이었나 - 2026-09-10 감사 §4(a)] <c>raw/l1-attempts.json</c> 에 판
    /// 경계가 없었다. <c>L1AttemptLog.Append</c> 는 누적하는데 판 사이에 <c>raw/</c> 를
    /// 지우는 코드가 없어, 두 판의 시도 계열이 한 파일에 합쳐질 수 있다(1 판 시도 1~6 뒤에
    /// 2 판 시도 1~5 가 붙으면 겉으로는 7~11 처럼 보인다). 그리고 승격기는 <c>cp</c> 라
    /// 같은 객체를 두 번 재생성하면 앞 판을 덮었다.
    ///
    /// [★ 오라클 - 비순환] 기대값을 이 판의 코드로 만들지 않는다. 재료는
    /// <c>docs/audit-reports/evidence/2026-09-10-EXCEPTION_PROC-run{1,2}-l1-attempts.json</c>
    /// 두 실물이고, <b>이 판 이전에 파이프라인이 쓴 것</b>이며 이 판이 안 건드린다.
    /// 기대 후보는 이 시험을 쓰기 전에 실행으로 떠 놨다:
    ///   1 판 = { AddUpdateMappingError, CheckErrorCodeUniquenessClaim }
    ///   2 판 = { CheckErrorCodeUniquenessClaim }
    ///
    /// [판 번호는 어디서 오는가] 호출부에 없다 - <c>VerificationPipelineOrchestrator</c> 가
    /// 넘기는 것은 <c>attempt</c> 하나이고 재생성마다 1 부터 다시 센다. 그래서
    /// <c>Append</c> 안에서 유도한다: <b>들어온 <c>attempt</c> 가 파일의 마지막
    /// <c>attempt</c> 를 넘지 않으면 새 판이다.</b>
    ///
    /// 사전선언: <c>docs/audit-reports/2026-09-10-승격기-판구분-사전선언.md</c>
    /// </summary>
    public class L1AttemptRunBoundaryTests
    {
        private static string EvidencePath(string run) => Path.Combine(
            RepoPaths.FindRepoRoot(), "docs", "audit-reports", "evidence",
            $"2026-09-10-EXCEPTION_PROC-{run}-l1-attempts.json");

        /// <summary>
        /// 증거 원본을 <c>(시도, 검사키, 메시지)</c> 로만 읽는다. <c>Run</c> 은 안 읽는다 -
        /// 그 파일에는 없고, 있다고 가정하면 오라클이 이 판의 산물이 된다.
        /// </summary>
        private static IReadOnlyList<(int Attempt, string CheckKey, string Message)> ReadEvidence(string run)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(EvidencePath(run)));
            return doc.RootElement.EnumerateArray()
                .Select(e => (
                    e.GetProperty("Attempt").GetInt32(),
                    e.GetProperty("CheckKey").GetString()!,
                    e.GetProperty("Message").GetString()!))
                .ToList();
        }

        private static string WriteAccumulated(string dir, params string[] runs)
        {
            // 두 판이 한 파일에 이어 붙는 실물 경로를 재현한다 - Append 를 판마다
            // 시도 순서 그대로 부른다. 사이에 raw/ 를 지우지 않는다(지우는 코드가 없다).
            foreach (var run in runs)
            {
                foreach (var group in ReadEvidence(run).GroupBy(x => x.Attempt).OrderBy(g => g.Key))
                {
                    L1AttemptLog.Append(
                        dir,
                        group.Key,
                        group.Select(x => new L1Firing(x.CheckKey, x.Message)).ToList());
                }
            }

            return Path.Combine(dir, "raw", L1AttemptLog.FileName);
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "reset-run-boundary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void TheEvidenceFilesAreTheShapeThisTestAssumes()
        {
            // 정박 - 오라클이 바뀌면 아래 시험들이 무엇을 재는지 모른 채 초록이 된다.
            var run1 = ReadEvidence("run1");
            var run2 = ReadEvidence("run2");

            Assert.Equal(10, run1.Count);
            Assert.Equal(8, run2.Count);
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, run1.Select(x => x.Attempt).Distinct().OrderBy(a => a));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, run2.Select(x => x.Attempt).Distinct().OrderBy(a => a));
        }

        [Fact]
        public void Append_StampsRunOneOnASingleRun()
        {
            var dir = NewTempDir();
            try
            {
                var path = WriteAccumulated(dir, "run1");

                var firings = L1AttemptLog.Read(path);

                Assert.Equal(10, firings.Count);
                Assert.All(firings, f => Assert.Equal(1, f.Run));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Append_StartsANewRunWhenTheAttemptNumberDoesNotAdvance()
        {
            // ★ 이 판의 핵심. 두 판이 한 파일에 이어 붙어도 판이 갈려야 한다.
            var dir = NewTempDir();
            try
            {
                var path = WriteAccumulated(dir, "run1", "run2");

                var firings = L1AttemptLog.Read(path);

                Assert.Equal(18, firings.Count);
                Assert.Equal(10, firings.Count(f => f.Run == 1));
                Assert.Equal(8, firings.Count(f => f.Run == 2));

                // 시도 번호는 판마다 1 부터다 - 이어 붙였다고 7~11 로 보이면 안 된다.
                Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 },
                    firings.Where(f => f.Run == 1).Select(f => f.Attempt).Distinct().OrderBy(a => a));
                Assert.Equal(new[] { 1, 2, 3, 4, 5 },
                    firings.Where(f => f.Run == 2).Select(f => f.Attempt).Distinct().OrderBy(a => a));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Read_RejectsAFileWhoseRecordsHaveNoRun()
        {
            // 명명 계약. Run 을 이 검사에 안 이으면, camelCase 파일이 Attempt=0 으로
            // 조용히 읽히던 그 침묵을 같은 함수에서 두 번째로 만드는 것이다.
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "attempts.json");
                File.WriteAllText(path, """[{"Attempt":1,"CheckKey":"K","Message":"m"}]""");

                Assert.Empty(L1AttemptLog.Read(path));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Read_AcceptsAFileThatCarriesRun()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "attempts.json");
                File.WriteAllText(path, """[{"Run":1,"Attempt":1,"CheckKey":"K","Message":"m"}]""");

                var only = Assert.Single(L1AttemptLog.Read(path));
                Assert.Equal(1, only.Run);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
