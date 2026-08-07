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

        // --- 리뷰 수정: Important 1 (원자적 쓰기), Important 2 (Load의 catch 확장 + 손상 파일 보존) ---

        [Fact]
        public async Task SaveAsync_ShouldNotLeaveTemporaryFilesBehind()
        {
            await NewStore().SaveAsync(CancellationToken.None);

            var files = Directory.GetFiles(_agentDir).Select(Path.GetFileName).OrderBy(f => f).ToArray();

            // WriteAtomicAsync가 성공 경로에서 tmp 파일을 최종 경로로 옮기므로,
            // 저장이 끝난 뒤 디렉터리에는 진짜 결과물 두 개만 남아야 한다.
            Assert.Equal(new[] { "progress.json", "todo.md" }, files);
        }

        [Fact]
        public void Load_ShouldReturnNull_AndPreserveOriginal_WhenJsonIsMalformed()
        {
            var path = Path.Combine(_agentDir, "progress.json");
            File.WriteAllText(path, "{ 이건 유효한 JSON이 아니다");

            var loaded = AgentProgressStore.Load(_agentDir);

            Assert.Null(loaded);
            // 손상된 파일을 그 자리에 그대로 두면 다음 SaveAsync가 조용히 덮어써
            // 증거가 사라진다. 원본 자리에는 더 이상 없고, .corrupt로 옮겨져
            // 내용 그대로 보존돼 있어야 한다.
            Assert.False(File.Exists(path));
            var corruptPath = path + ".corrupt";
            Assert.True(File.Exists(corruptPath));
            Assert.Equal("{ 이건 유효한 JSON이 아니다", File.ReadAllText(corruptPath));
        }

        [Fact]
        public void Load_ShouldNotThrow_WhenFileCannotBeOpenedForReading()
        {
            var path = Path.Combine(_agentDir, "progress.json");
            File.WriteAllText(path, "{}");

            // 동시 잠금 등으로 파일을 여는 것 자체가 실패하는 경우를 재현한다.
            // File.ReadAllText가 IOException을 던지는 것을 별도로 검증했다 -
            // 같은 프로세스 안에서도 FileShare.None으로 연 핸들이 있으면
            // 이 플랫폼에서 실제로 IOException이 난다.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var exception = Record.Exception(() => AgentProgressStore.Load(_agentDir));

                Assert.Null(exception);
            }
        }

        [Fact]
        public async Task SaveAsync_ShouldFoldEmbeddedNewlines_InRenderedTodo()
        {
            // StepCode·LastGapSummary·JobName은 AI가 생성한 텍스트라 개행이 섞여
            // 있을 수 있다. 개행을 그대로 렌더링하면 목록 항목 하나가 여러 줄로
            // 쪼개져 todo.md의 구조가 깨진다.
            var stages = new List<StageProgress>
            {
                new("01-S01", "S01\nEVIL", "task-01-S01.md", StageStatus.Pending, 0, null),
            };
            var store = AgentProgressStore.Create(_agentDir, "Job\nName", stages);
            store.Mark("01-S01", StageStatus.Failed, 1, "gap\nsummary");
            await store.SaveAsync(CancellationToken.None);

            var todo = await File.ReadAllTextAsync(Path.Combine(_agentDir, "todo.md"));

            Assert.Contains("Job Name 통합 배치 마이그레이션 진행 상태", todo);
            Assert.Contains("`S01 EVIL`", todo);
            Assert.Contains("gap summary", todo);
            // 개행이 접혔다면 "EVIL"이나 "summary"만 있는 줄이 따로 생기지 않는다.
            Assert.DoesNotContain("\nEVIL", todo);
            Assert.DoesNotContain("\nsummary", todo);
        }
    }
}
