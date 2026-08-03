using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients.Cli
{
    public sealed class ClaudeCliResponse
    {
        public bool IsError { get; init; }
        public string? Result { get; init; }
        public string? Subtype { get; init; }
        public string? ApiErrorStatus { get; init; }
    }

    /// <summary>
    /// Claude Code CLI를 헤드리스로 기동해 순수 LLM처럼 사용한다.
    /// API 키 대신 CLI에 로그인된 구독 계정을 쓴다.
    /// </summary>
    public sealed class ClaudeCliClient : IAiClient
    {
        private readonly string _command;
        private readonly string _modelName;
        private readonly TimeSpan _timeout;

        public string ProviderName => "claude-cli";
        public string ModelName => _modelName;

        /// <summary>팩토리가 HttpClient에서 읽어 넘긴 제한 시간. 배선이 끊기면 테스트가 잡는다.</summary>
        public TimeSpan Timeout => _timeout;

        public ClaudeCliClient(string command, string modelName, TimeSpan timeout)
        {
            _command = string.IsNullOrWhiteSpace(command) ? "claude" : command;
            _modelName = modelName ?? string.Empty;
            _timeout = timeout;

            // CLI는 temperature를 노출하지 않는다. 조용히 무시하면 Critic 채점이
            // 왜 흔들리는지 알 수 없게 되므로, 생성 시 한 번 알린다.
            Log.Warning("{Provider}는 temperature를 지원하지 않습니다. 설정값은 무시됩니다.", ProviderName);
        }

        public static IReadOnlyList<string> BuildArguments(
            string modelName, string? effort, string systemPromptFilePath)
        {
            var arguments = new List<string>
            {
                "-p",
                "--output-format", "json",
                // 순수 LLM으로 쓴다. 툴을 켜두면 에이전트가 파일 시스템을 돌아다닌다.
                "--tools", string.Empty,
                "--disable-slash-commands",
                "--no-session-persistence",
                // 기본 시스템 프롬프트를 '교체'한다. 추가(append)하면 코딩 에이전트
                // 프롬프트가 그대로 얹혀 호출당 오버헤드가 7배가 된다.
                "--system-prompt-file", systemPromptFilePath
            };

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                arguments.Add("--model");
                arguments.Add(modelName);
            }

            var mappedEffort = CliEffort.ForClaude(effort);
            if (mappedEffort != null)
            {
                arguments.Add("--effort");
                arguments.Add(mappedEffort);
            }

            return arguments;
        }

        public static ClaudeCliResponse ParseResponse(string standardOutput)
        {
            try
            {
                using var document = JsonDocument.Parse(standardOutput);
                var root = document.RootElement;

                return new ClaudeCliResponse
                {
                    IsError = root.TryGetProperty("is_error", out var isError)
                              && isError.ValueKind == JsonValueKind.True,
                    Result = ReadString(root, "result"),
                    Subtype = ReadString(root, "subtype"),
                    ApiErrorStatus = ReadString(root, "api_error_status")
                };
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"claude-cli 응답을 JSON으로 해석할 수 없습니다.\n[출력]\n{standardOutput}", ex);
            }
        }

        public async Task<AiResult> ChatAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            string? effort = null,
            CancellationToken cancellationToken = default)
        {
            using var workspace = new CliWorkspace();
            var systemPromptFile = workspace.WriteFile("system-prompt.txt", systemPrompt ?? string.Empty);
            var arguments = BuildArguments(_modelName, effort, systemPromptFile);

            CliProcessResult processResult;
            try
            {
                processResult = await CliProcessRunner.RunAsync(
                    _command, arguments, userPrompt, workspace.Path, _timeout, cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw CliFailureClassifier.CommandNotFound(ProviderName, _command, ex);
            }

            if (!processResult.Succeeded)
            {
                throw CliFailureClassifier.ToException(ProviderName, _command, processResult, null);
            }

            var response = ParseResponse(processResult.StandardOutput);

            // 종료 코드가 0이어도 JSON 안에만 오류가 담기는 경우가 있다.
            if (response.IsError || response.Result == null)
            {
                var detail = $"{response.Subtype} {response.ApiErrorStatus}".Trim();
                throw CliFailureClassifier.ToException(ProviderName, _command, processResult, detail);
            }

            return new AiResult { Content = response.Result };
        }

        private static string? ReadString(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
