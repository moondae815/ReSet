using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// Codex CLI를 비대화형(exec)으로 기동한다.
    /// 시스템 프롬프트 분리 개념이 없어 사용자 프롬프트와 합쳐 stdin으로 넣고,
    /// 최종 응답은 stdout이 아니라 -o 파일에서 읽는다(stdout에는 진행 로그가 섞인다).
    /// </summary>
    public sealed class CodexCliClient : IAiClient
    {
        private const string ResultFileName = "codex-result.txt";

        private readonly string _command;
        private readonly string _modelName;
        private readonly TimeSpan _timeout;

        public string ProviderName => "codex-cli";
        public string ModelName => _modelName;

        /// <summary>팩토리가 HttpClient에서 읽어 넘긴 제한 시간. 배선이 끊기면 테스트가 잡는다.</summary>
        public TimeSpan Timeout => _timeout;

        public CodexCliClient(string command, string modelName, TimeSpan timeout)
        {
            _command = string.IsNullOrWhiteSpace(command) ? "codex" : command;
            _modelName = modelName ?? string.Empty;
            _timeout = timeout;

            Log.Warning("{Provider}는 temperature를 지원하지 않습니다. 설정값은 무시됩니다.", ProviderName);
        }

        public static IReadOnlyList<string> BuildArguments(
            string modelName, string? effort, string outputFilePath)
        {
            var arguments = new List<string>
            {
                "exec",
                // 프롬프트를 stdin에서 읽는다. 191KB짜리 프롬프트를 argv로 넘길 수 없다.
                "-",
                "--sandbox", "read-only",
                "--skip-git-repo-check",
                "--ephemeral",
                // 세 CLI 중 codex만 집계를 보려면 인자를 더 붙여야 한다. 이것이 없으면
                // stdout에는 사람이 읽을 진행 로그("tokens used 16,665")만 흐른다.
                // 본문은 계속 -o 파일에서 읽으므로 기존 경로는 그대로다.
                "--json",
                "-o", outputFilePath
            };

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                arguments.Add("-m");
                arguments.Add(modelName);
            }

            var mappedEffort = CliEffort.ForThreeLevel(effort, out var clamped);
            if (mappedEffort != null)
            {
                if (clamped)
                {
                    Log.Warning(
                        "codex-cli는 low|medium|high만 지원합니다. 요청한 effort '{Requested}'를 '{Applied}'로 낮춥니다.",
                        effort, mappedEffort);
                }

                arguments.Add("-c");
                // 값은 TOML로 파싱된다. 문자열은 따옴표로 감싸야 안전하다.
                arguments.Add($"model_reasoning_effort=\"{mappedEffort}\"");
            }

            return arguments;
        }

        /// <summary>
        /// --json으로 받은 stdout(JSONL)에서 토큰 집계를 뽑는다.
        ///
        /// 필드 이름이 claude와 다르다: 캐시 읽기가 cached_input_tokens,
        /// 캐시 쓰기가 cache_write_input_tokens, 추론이 reasoning_output_tokens다.
        ///
        /// 깨진 줄은 건너뛴다. 집계는 진단 정보일 뿐이므로, 한 줄이 깨졌다고 분석
        /// 전체를 실패시킬 이유가 없다. 못 읽으면 null을 돌려주고 호출자가 경고한다.
        /// </summary>
        public static CliUsage? ParseUsage(string standardOutput)
        {
            if (string.IsNullOrWhiteSpace(standardOutput))
            {
                return null;
            }

            CliUsage? latest = null;

            foreach (var line in standardOutput.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                {
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(trimmed);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("type", out var type)
                        || type.ValueKind != JsonValueKind.String
                        || type.GetString() != "turn.completed"
                        || !root.TryGetProperty("usage", out var usage)
                        || usage.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    // 한 실행에 여러 개가 오면 마지막이 최종 상태다.
                    latest = new CliUsage(
                        Input: CliUsage.ReadCounter(usage, "input_tokens"),
                        Output: CliUsage.ReadCounter(usage, "output_tokens"),
                        CacheWrite: CliUsage.ReadCounter(usage, "cache_write_input_tokens"),
                        CacheRead: CliUsage.ReadCounter(usage, "cached_input_tokens"),
                        Thinking: CliUsage.ReadCounter(usage, "reasoning_output_tokens"));
                }
            }

            return latest;
        }

        public async Task<AiResult> ChatAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            string? effort = null,
            string? volatileUserSuffix = null,
            CancellationToken cancellationToken = default)
        {
            userPrompt = PromptComposition.MergeVolatileSuffix(userPrompt, volatileUserSuffix);

            using var workspace = new CliWorkspace();
            var outputFilePath = Path.Combine(workspace.Path, ResultFileName);
            var arguments = BuildArguments(_modelName, effort, outputFilePath);
            var prompt = CliPrompt.Combine(systemPrompt ?? string.Empty, userPrompt ?? string.Empty);

            CliProcessResult processResult;
            try
            {
                processResult = await CliProcessRunner.RunAsync(
                    _command, arguments, prompt, workspace.Path, _timeout, cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw CliFailureClassifier.CommandNotFound(ProviderName, _command, ex);
            }

            // 실패 판정보다 먼저 남긴다. 실패한 호출도 토큰을 태웠다.
            ParseUsage(processResult.StandardOutput)?.WriteToLog(ProviderName);

            if (!processResult.Succeeded)
            {
                throw CliFailureClassifier.ToException(ProviderName, _command, processResult, null);
            }

            if (!File.Exists(outputFilePath))
            {
                throw CliFailureClassifier.ToException(
                    ProviderName, _command, processResult,
                    "codex가 결과 파일을 남기지 않았습니다.");
            }

            var content = await File.ReadAllTextAsync(outputFilePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw CliFailureClassifier.ToException(
                    ProviderName, _command, processResult,
                    "codex가 빈 응답을 반환했습니다.");
            }

            return new AiResult { Content = content };
        }
    }
}
