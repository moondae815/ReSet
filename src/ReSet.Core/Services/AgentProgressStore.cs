using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ReSet.Core.Services
{
    public enum StageStatus
    {
        Pending,
        InProgress,
        Passed,
        Failed
    }

    /// <param name="Id">회차 식별자. task 파일 이름에서 접두와 확장자를 뗀 것과 같다.</param>
    /// <param name="StepCode">단계 회차면 그 코드, Bootstrap/Assembly면 null.</param>
    public sealed record StageProgress(
        string Id,
        string? StepCode,
        string TaskFileName,
        StageStatus Status,
        int Attempts,
        string? LastGapSummary);

    /// <summary>
    /// 회차 진행 상태를 소유한다.
    ///
    /// 이전에는 지시서가 에이전트에게 `todo.md`의 `[x]`를 직접 갱신하라고 요구했다.
    /// 그것은 에이전트의 자기 보고를 검증 없이 신뢰하는 구조이고, 지키지 않아도
    /// 아무 일도 일어나지 않았다. 이제 검증 결과만이 상태를 바꾸며, `todo.md`는
    /// 이 상태에서 렌더링되는 사람용 표시다.
    /// </summary>
    public sealed class AgentProgressStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string _agentDir;
        private readonly string _jobName;
        private readonly List<StageProgress> _stages;

        private AgentProgressStore(string agentDir, string jobName, IEnumerable<StageProgress> stages)
        {
            _agentDir = agentDir;
            _jobName = jobName;
            _stages = stages.ToList();
        }

        public IReadOnlyList<StageProgress> Stages => _stages;

        /// <summary>검증을 통과하지 못한 단계 코드. 조립 회차가 제외할 목록이다.</summary>
        public IReadOnlyList<string> FailedStepCodes => _stages
            .Where(s => s.Status == StageStatus.Failed && s.StepCode != null)
            .Select(s => s.StepCode!)
            .ToList();

        public static AgentProgressStore Create(
            string agentDir, string jobName, IReadOnlyList<StageProgress> stages) =>
            new(agentDir, jobName, stages);

        public static AgentProgressStore? Load(string agentDir)
        {
            var path = Path.Combine(agentDir, "progress.json");
            if (!File.Exists(path))
            {
                return null;
            }

            string raw;
            try
            {
                raw = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 파일을 여는 것 자체가 실패했다 - 권한 없음, 동시 잠금, 디스크 오류 등.
                // 파싱 실패와 마찬가지로 오케스트레이터를 죽이지 않고 "이전 진행 없음"으로
                // 물러난다. 다만 다음 SaveAsync가 원본 위 이 자리를 덮어쓰면 무슨 일이
                // 있었는지 볼 방법이 사라지므로, 지우지 말고 옆으로 옮겨 보존한다.
                Log.Warning(ex, "진행 상태 파일을 열지 못했습니다 - Path: {Path}", path);
                PreserveCorrupt(path, ex.Message);
                return null;
            }

            try
            {
                var document = JsonSerializer.Deserialize<ProgressDocument>(raw, JsonOptions);

                if (document?.Stages == null)
                {
                    // 파싱은 됐지만 기대한 형태가 아니다 - 예외가 아니므로 아래 catch를
                    // 타지 않는다. 여기서도 원본을 지우지 않고 보존해야 다음 저장이
                    // 조용히 덮어써 증거를 지우는 일을 막는다.
                    PreserveCorrupt(path, "Stages 필드가 없거나 비어 있습니다");
                    return null;
                }

                return new AgentProgressStore(agentDir, document.JobName ?? string.Empty, document.Stages);
            }
            catch (JsonException ex)
            {
                // 상태 파일이 깨졌다고 회차 실행을 막지 않는다. 처음부터 다시 도는 편이
                // 낫고, 그 사실은 로그로 남긴다.
                Log.Warning(ex, "진행 상태 파일을 읽지 못했습니다 - Path: {Path}", path);
                PreserveCorrupt(path, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 읽거나 파싱하지 못한 progress.json을 지우지 않고 옆으로 옮겨 보존한다.
        /// 그대로 두면 이 호출의 반환값(null)을 받은 호출자가 Create(...)로 새로
        /// 시작해 다음 SaveAsync에서 같은 자리를 덮어쓴다 - 완료된 작업의 유일한
        /// 기록이 조용히 사라지는 경로다. 보존 자체가 실패해도(권한 없음 등) 이
        /// 메서드는 던지지 않는다 - Load가 null을 돌려주고 호출자가 계속 진행하는
        /// 흐름을 이 부수적 정리 작업 때문에 막을 이유가 없다.
        /// </summary>
        private static void PreserveCorrupt(string path, string reason)
        {
            try
            {
                var corruptPath = path + ".corrupt";
                if (File.Exists(corruptPath))
                {
                    // 이전에도 보존한 적이 있다면 덮어쓰지 않는다 - 두 번째 손상이
                    // 첫 번째 증거를 지우게 둘 이유가 없다.
                    corruptPath = $"{path}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                }

                File.Move(path, corruptPath, overwrite: false);
                Log.Warning(
                    "손상된 진행 상태 파일을 보존했습니다 - From: {From}, To: {To}, Reason: {Reason}",
                    path, corruptPath, reason);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "손상된 진행 상태 파일을 보존하지 못했습니다 - Path: {Path}", path);
            }
        }

        public void Mark(string stageId, StageStatus status, int attempts, string? gapSummary)
        {
            var index = _stages.FindIndex(s => s.Id == stageId);
            if (index < 0)
            {
                Log.Warning("알 수 없는 회차 식별자입니다 - StageId: {StageId}", stageId);
                return;
            }

            _stages[index] = _stages[index] with
            {
                Status = status,
                Attempts = attempts,
                LastGapSummary = gapSummary,
            };
        }

        public async Task SaveAsync(CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(_agentDir);

            var json = JsonSerializer.Serialize(
                new ProgressDocument { JobName = _jobName, Stages = _stages }, JsonOptions);

            // progress.json은 진실의 원천이므로 원자적으로 써야 한다 - 외부 코딩
            // 에이전트를 돌리는 오케스트레이터는 크래시·OOM·취소가 실제로 일어나는
            // 환경이고, 최종 경로에 바로 File.WriteAllTextAsync로 쓰면(먼저 잘라내고
            // 쓰는 방식) 그 도중에 죽었을 때 손상된 파일이 남는다. 다음 Load가 그
            // 손상을 JsonException으로 잡더라도, 완료된 작업의 유일한 기록이 이미
            // 망가진 뒤다. todo.md는 그 상태에서 파생된 사람용 표시라 원자성이
            // 필수는 아니지만, 같은 헬퍼를 쓰는 것이 더 단순하고 "읽다가 절반만
            // 쓰인 상태 파일을 본다"는 사용자 경험 문제도 함께 없앤다.
            await WriteAtomicAsync(Path.Combine(_agentDir, "progress.json"), json, cancellationToken);
            await WriteAtomicAsync(Path.Combine(_agentDir, "todo.md"), RenderTodo(), cancellationToken);
        }

        /// <summary>
        /// 같은 디렉터리의 임시 파일에 먼저 쓴 뒤 File.Move로 최종 경로에 옮긴다.
        /// 같은 볼륨 안의 File.Move는 원자적이므로, 쓰기 도중 크래시가 나도 최종
        /// 경로는 "이전 내용 그대로" 아니면 "새 내용 전체" 둘 중 하나만 보인다 -
        /// 잘린 중간 상태가 관측되는 경우가 없다.
        /// </summary>
        private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
        {
            var tmpPath = $"{path}.tmp-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllTextAsync(tmpPath, content, Encoding.UTF8, cancellationToken);
                File.Move(tmpPath, path, overwrite: true);
            }
            finally
            {
                // 정상 경로에서는 Move가 tmp 파일을 이미 최종 경로로 옮겼으므로
                // 여기 남아 있지 않다. 쓰기 실패나 취소로 예외가 난 경로에서만
                // 남은 tmp 파일을 정리한다.
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
        }

        private string RenderTodo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 📋 {CollapseNewlines(_jobName)} 통합 배치 마이그레이션 진행 상태");
            sb.AppendLine();
            sb.AppendLine("이 파일은 **도구가** 검증 결과를 근거로 갱신합니다. 직접 편집하지 마십시오 — 다음 회차에서 덮어씁니다.");
            sb.AppendLine();

            foreach (var stage in _stages)
            {
                var box = stage.Status == StageStatus.Passed ? "x" : " ";
                var label = stage.StepCode != null ? $"`{CollapseNewlines(stage.StepCode)}`" : stage.Id;
                var note = stage.Status switch
                {
                    StageStatus.Failed => $" — ❌ 검증 실패 ({stage.Attempts}회 시도)",
                    StageStatus.InProgress => " — ⏳ 진행 중",
                    StageStatus.Passed => $" — ✅ 통과 ({stage.Attempts}회 시도)",
                    _ => string.Empty,
                };

                sb.AppendLine($"- [{box}] {label}{note}");

                if (stage.Status == StageStatus.Failed && !string.IsNullOrWhiteSpace(stage.LastGapSummary))
                {
                    sb.AppendLine($"  - {CollapseNewlines(stage.LastGapSummary)}");
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>
        /// StepCode·LastGapSummary·JobName은 AI가 생성한 텍스트라 개행이 섞여
        /// 있을 수 있다. todo.md는 사람이 읽고 다시 파싱되지 않으니 이스케이프는
        /// 필요 없지만, 개행을 그대로 두면 목록 항목 하나가 여러 줄로 쪼개져
        /// 렌더링된 목록/표 구조가 깨진다. 공백으로 접어 한 줄을 지킨다.
        /// </summary>
        private static string CollapseNewlines(string? value) =>
            string.IsNullOrEmpty(value)
                ? string.Empty
                : value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

        private sealed class ProgressDocument
        {
            public string? JobName { get; set; }
            public List<StageProgress>? Stages { get; set; }
        }
    }
}
