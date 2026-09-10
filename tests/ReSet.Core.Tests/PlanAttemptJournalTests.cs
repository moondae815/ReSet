using System;
using System.IO;
using System.Text.Json;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class PlanAttemptJournalTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"ReSet-Journal-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private PlanAttemptJournal NewJournal() =>
            PlanAttemptJournal.Create(_root, "Job_Test", "OpenAI", "gpt-4", "high", "C#", "specshash");

        [Fact]
        public void OpenRun_CreatesNumberedRunDirectoryWithManifest()
        {
            var journal = NewJournal();

            journal.OpenRun("## 목차 A", "run-start");

            var dir = journal.CurrentRunDirectory;
            Assert.NotNull(dir);
            Assert.Equal("run-001", Path.GetFileName(dir));

            var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dir!, "manifest.json"))).RootElement;
            Assert.Equal(1, manifest.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(1, manifest.GetProperty("Run").GetInt32());
            Assert.Equal("Job_Test", manifest.GetProperty("Job").GetString());
            Assert.Equal("run-start", manifest.GetProperty("OpenedBy").GetString());

            var key = manifest.GetProperty("ReuseKey");
            Assert.Equal("gpt-4", key.GetProperty("Model").GetString());
            Assert.Equal("specshash", key.GetProperty("SpecsSha256").GetString());
            Assert.Equal(
                PlanAttemptJournal.ComputeSha256("## 목차 A"),
                key.GetProperty("PlanStructureSha256").GetString());
        }

        // 판 경계는 유도하지 않고 디렉터리로 박는다 - L1AttemptLog.ResolveRun 이
        // 시도 번호에서 판을 유도하며 감수한 취약함을 반복하지 않기 위해서다.
        [Fact]
        public void OpenRun_Twice_OpensASecondRunDirectory()
        {
            var journal = NewJournal();

            journal.OpenRun("## 목차 A", "run-start");
            journal.OpenRun("## 목차 B", "structure-redraft");

            Assert.Equal("run-002", Path.GetFileName(journal.CurrentRunDirectory));
            Assert.True(Directory.Exists(Path.Combine(
                _root, "Jobs", "Job_Test", "raw", "attempts", "run-001")));
        }

        // 같은 Job 을 다시 돌린 판이 이어 붙는다 - 앞 판의 번호를 이어받는다.
        [Fact]
        public void Create_AfterAPreviousProcess_ContinuesTheRunNumbering()
        {
            NewJournal().OpenRun("## 목차 A", "run-start");

            var second = NewJournal();
            second.OpenRun("## 목차 A", "run-start");

            Assert.Equal("run-002", Path.GetFileName(second.CurrentRunDirectory));
        }

        // 빈 내용의 해시는 "미계산" sentinel 과 달라야 한다 - 둘 다 "" 면 2단계
        // 재개가 "빈 목차끼리 일치"와 "둘 다 미계산"을 구분 못 한다.
        [Fact]
        public void ComputeSha256_EmptyString_ReturnsTheRealDigestNotASentinel()
        {
            Assert.Equal(
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                PlanAttemptJournal.ComputeSha256(string.Empty));
        }

        // null 은 계산할 내용이 없으므로 미계산을 뜻하는 "" 로 남는다.
        [Fact]
        public void ComputeSha256_Null_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, PlanAttemptJournal.ComputeSha256(null!));
        }

        // 소프트페일 - 관측이 파이프라인을 죽이면 안 된다.
        [Fact]
        public void OpenRun_WhenOutputRootIsAFile_StaysInactiveAndDoesNotThrow()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"ReSet-NotADir-{Guid.NewGuid():N}");
            File.WriteAllText(filePath, "이 경로는 디렉터리가 아니다");
            try
            {
                var journal = PlanAttemptJournal.Create(
                    filePath, "Job_Test", "OpenAI", "gpt-4", null, "C#", "specshash");

                journal.OpenRun("## 목차", "run-start");

                Assert.Null(journal.CurrentRunDirectory);
            }
            finally
            {
                File.Delete(filePath);
            }
        }
    }
}
