using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class AgentProgressStoreTests : IDisposable
    {
        private readonly string _agentDir;

        public AgentProgressStoreTests()
        {
            _agentDir = Path.Combine(Path.GetTempPath(), "reset-progress-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_agentDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_agentDir)) Directory.Delete(_agentDir, recursive: true);
        }

        private static IReadOnlyList<StageProgress> Initial() => new List<StageProgress>
        {
            new("00-bootstrap", null, "task-00-bootstrap.md", StageStatus.Pending, 0, null),
            new("01-S01", "S01", "task-01-S01.md", StageStatus.Pending, 0, null),
            new("02-S02", "S02", "task-02-S02.md", StageStatus.Pending, 0, null),
            new("99-assembly", null, "task-99-assembly.md", StageStatus.Pending, 0, null),
        };

        private AgentProgressStore NewStore() =>
            AgentProgressStore.Create(_agentDir, "TestJob", Initial());

        [Fact]
        public async Task SaveAsync_ShouldWriteBothProgressJsonAndTodo()
        {
            await NewStore().SaveAsync(CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(_agentDir, "progress.json")));
            Assert.True(File.Exists(Path.Combine(_agentDir, "todo.md")));
        }

        [Fact]
        public async Task SaveAsync_ShouldRenderTodoFromStatus()
        {
            var store = NewStore();
            store.Mark("01-S01", StageStatus.Passed, 1, null);
            store.Mark("02-S02", StageStatus.Failed, 3, "비즈니스 로직 불일치");
            await store.SaveAsync(CancellationToken.None);

            var todo = await File.ReadAllTextAsync(Path.Combine(_agentDir, "todo.md"));

            Assert.Contains("- [x] `S01`", todo);
            Assert.Contains("- [ ] `S02`", todo);
            Assert.Contains("검증 실패", todo);
            Assert.Contains("비즈니스 로직 불일치", todo);
        }

        [Fact]
        public async Task SaveAsync_ShouldStateThatTheToolOwnsTheFile()
        {
            // 에이전트가 이 파일을 편집해도 다음 저장에서 덮인다. 그 사실을 문서가 말해야 한다.
            await NewStore().SaveAsync(CancellationToken.None);

            var todo = await File.ReadAllTextAsync(Path.Combine(_agentDir, "todo.md"));

            Assert.Contains("도구가", todo);
            Assert.Contains("직접 편집하지", todo);
        }

        [Fact]
        public async Task Load_ShouldRoundTripStages()
        {
            var store = NewStore();
            store.Mark("01-S01", StageStatus.Passed, 2, null);
            await store.SaveAsync(CancellationToken.None);

            var loaded = AgentProgressStore.Load(_agentDir);

            Assert.NotNull(loaded);
            var s01 = loaded!.Stages.Single(s => s.Id == "01-S01");
            Assert.Equal(StageStatus.Passed, s01.Status);
            Assert.Equal(2, s01.Attempts);
        }

        [Fact]
        public void Load_ShouldReturnNull_WhenFileMissing()
        {
            Assert.Null(AgentProgressStore.Load(_agentDir));
        }

        [Fact]
        public void FailedStepCodes_ShouldListOnlyFailedSteps()
        {
            var store = NewStore();
            store.Mark("01-S01", StageStatus.Passed, 1, null);
            store.Mark("02-S02", StageStatus.Failed, 3, "gap");

            Assert.Equal(new[] { "S02" }, store.FailedStepCodes);
        }

        [Fact]
        public void FailedStepCodes_ShouldExcludeNonStepStages()
        {
            // Bootstrap과 Assembly는 StepCode가 없다. 조립 회차에 넘길 목록에 섞이면 안 된다.
            var store = NewStore();
            store.Mark("00-bootstrap", StageStatus.Failed, 1, "빌드 실패");

            Assert.Empty(store.FailedStepCodes);
        }

        [Fact]
        public void Mark_ShouldIgnoreUnknownStageId()
        {
            var store = NewStore();

            store.Mark("없는-회차", StageStatus.Passed, 1, null);

            Assert.All(store.Stages, s => Assert.Equal(StageStatus.Pending, s.Status));
        }
    }
}
