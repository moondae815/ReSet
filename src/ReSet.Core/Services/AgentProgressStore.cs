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

            try
            {
                var document = JsonSerializer.Deserialize<ProgressDocument>(
                    File.ReadAllText(path), JsonOptions);

                if (document?.Stages == null)
                {
                    return null;
                }

                return new AgentProgressStore(agentDir, document.JobName ?? string.Empty, document.Stages);
            }
            catch (JsonException ex)
            {
                // 상태 파일이 깨졌다고 회차 실행을 막지 않는다. 처음부터 다시 도는 편이
                // 낫고, 그 사실은 로그로 남긴다.
                Log.Warning(ex, "진행 상태 파일을 읽지 못했습니다 - Path: {Path}", path);
                return null;
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

            await File.WriteAllTextAsync(
                Path.Combine(_agentDir, "progress.json"), json, Encoding.UTF8, cancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(_agentDir, "todo.md"), RenderTodo(), Encoding.UTF8, cancellationToken);
        }

        private string RenderTodo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 📋 {_jobName} 통합 배치 마이그레이션 진행 상태");
            sb.AppendLine();
            sb.AppendLine("이 파일은 **도구가** 검증 결과를 근거로 갱신합니다. 직접 편집하지 마십시오 — 다음 회차에서 덮어씁니다.");
            sb.AppendLine();

            foreach (var stage in _stages)
            {
                var box = stage.Status == StageStatus.Passed ? "x" : " ";
                var label = stage.StepCode != null ? $"`{stage.StepCode}`" : stage.Id;
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
                    sb.AppendLine($"  - {stage.LastGapSummary!.Trim()}");
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        private sealed class ProgressDocument
        {
            public string? JobName { get; set; }
            public List<StageProgress>? Stages { get; set; }
        }
    }
}
