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

        /// <summary>턴이 끝난 사유. "max_tokens"면 본문이 잘린 것이다.</summary>
        public string? StopReason { get; init; }
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
                // CliWorkspace는 작업 디렉터리 기준 파일(CLAUDE.md/AGENTS.md)만 막는다.
                // 사용자 스코프 설정은 그대로 살아 남는다. --tools ""는 도움말 그대로
                // '내장 도구'만 끄므로 MCP 서버는 남고, --disable-slash-commands는 스킬
                // 호출만 막을 뿐 플러그인의 SessionStart 훅이 밀어 넣는 지시문 본문은
                // 그대로 들어온다. 아래 두 인자가 그 두 경로를 각각 끊는다.
                // 실측: 빈 작업 디렉터리에서 이 둘이 없으면 외부 컨텍스트 약 1,760 토큰이
                // 주입되고, 붙이면 0이 된다.
                "--strict-mcp-config",
                "--setting-sources", string.Empty,
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
                    ApiErrorStatus = ReadString(root, "api_error_status"),
                    StopReason = ReadString(root, "stop_reason")
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

            // CLI는 출력 한도를 노출하지 않는다. sonnet-5 기준 64,000 토큰으로 고정되어 있고
            // CLAUDE_CODE_MAX_OUTPUT_TOKENS도 무시되므로, API 경로(128,000)에서는 통과하던
            // 대형 SP가 여기서만 잘린다. 잘린 본문을 그대로 돌려주면 Critic이 누락된 절을
            // 결함으로 채점해, 원인이 모델 품질인지 출력 절단인지 구분할 수 없게 된다.
            if (string.Equals(response.StopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{ProviderName} 응답이 CLI 출력 한도에 걸려 잘렸습니다 (stop_reason: max_tokens). " +
                    "CLI는 출력 한도를 조절할 수단을 제공하지 않으므로, 이 대상은 API provider로 " +
                    "분석하거나 더 작은 단위로 나누어 실행하십시오.\n" +
                    $"[잘린 본문 {response.Result.Length}자]\n{response.Result}");
            }

            return new AiResult { Content = response.Result };
        }

        private static string? ReadString(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
