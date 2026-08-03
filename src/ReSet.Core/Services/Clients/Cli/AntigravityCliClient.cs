using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// Antigravity CLI를 print 모드로 기동한다.
    ///
    /// 세 CLI 중 유일하게 stdin으로 프롬프트를 받지 못한다(실측: 파이프로 주면 툴
    /// 권한 오류로 빈 응답). argv로 넘겨야 하는데 ReSet의 실제 최대 프롬프트는
    /// 191KB이고 Windows 명령행 한계는 32,767자다. 우회로가 없으므로 호출 전에
    /// 검사해 명확히 실패시킨다.
    /// </summary>
    public sealed class AntigravityCliClient : IAiClient
    {
        // Windows CreateProcess의 명령행 한계. 그 외 플랫폼은 ARG_MAX(리눅스·macOS
        // 공통 하한 수준)에서 환경 변수 몫을 빼고 보수적으로 잡는다.
        private const int WindowsCommandLineLimit = 32_767;
        private const int PosixCommandLineLimit = 1_000_000;

        private readonly string _command;
        private readonly string _modelName;
        private readonly TimeSpan _timeout;

        public string ProviderName => "agy-cli";
        public string ModelName => _modelName;

        /// <summary>팩토리가 HttpClient에서 읽어 넘긴 제한 시간. 배선이 끊기면 테스트가 잡는다.</summary>
        public TimeSpan Timeout => _timeout;

        public static int MaxCommandLineLength =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? WindowsCommandLineLimit
                : PosixCommandLineLimit;

        public AntigravityCliClient(string command, string modelName, TimeSpan timeout)
        {
            _command = string.IsNullOrWhiteSpace(command) ? "agy" : command;
            _modelName = modelName ?? string.Empty;
            _timeout = timeout;

            Log.Warning("{Provider}는 temperature를 지원하지 않습니다. 설정값은 무시됩니다.", ProviderName);
        }

        public static IReadOnlyList<string> BuildArguments(
            string prompt, string modelName, string? effort, TimeSpan timeout)
        {
            var arguments = new List<string>
            {
                "-p", prompt,
                "--output-format", "json",
                "--print-timeout", $"{(int)timeout.TotalSeconds}s"
            };

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                arguments.Add("--model");
                arguments.Add(modelName);
            }

            var mappedEffort = CliEffort.ForThreeLevel(effort, out var clamped);
            if (mappedEffort != null)
            {
                if (clamped)
                {
                    Log.Warning(
                        "agy-cli는 low|medium|high만 지원합니다. 요청한 effort '{Requested}'를 '{Applied}'로 낮춥니다.",
                        effort, mappedEffort);
                }

                arguments.Add("--effort");
                arguments.Add(mappedEffort);
            }

            return arguments;
        }

        public static void EnsureCommandLineFits(string command, IReadOnlyList<string> arguments)
        {
            // 인용부호와 구분 공백을 감안해 인자마다 여유를 더한다.
            var length = command.Length;
            foreach (var argument in arguments)
            {
                length += argument.Length + 3;
            }

            if (length <= MaxCommandLineLength)
            {
                return;
            }

            throw new InvalidOperationException(
                $"이 프롬프트는 agy-cli로 처리할 수 없습니다 " +
                $"(명령행 {length:N0}자, 플랫폼 한계 {MaxCommandLineLength:N0}자). " +
                "agy는 프롬프트를 표준 입력으로 받지 못해 명령행으로 넘겨야 하며, 우회로가 없습니다. " +
                "claude-cli 또는 API provider를 사용하십시오.");
        }

        public static string ParseResult(string standardOutput)
        {
            try
            {
                using var document = JsonDocument.Parse(standardOutput);
                var root = document.RootElement;

                var status = root.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString()
                    : null;

                if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"agy-cli가 실패 상태를 반환했습니다 (status: {status}).\n[출력]\n{standardOutput}");
                }

                var response = root.TryGetProperty("response", out var responseElement)
                    ? responseElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(response))
                {
                    throw new InvalidOperationException(
                        $"agy-cli 응답에 response 속성이 없거나 비어 있습니다.\n[출력]\n{standardOutput}");
                }

                return response;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"agy-cli 응답을 JSON으로 해석할 수 없습니다.\n[출력]\n{standardOutput}", ex);
            }
        }

        public async Task<AiResult> ChatAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            string? effort = null,
            CancellationToken cancellationToken = default)
        {
            // agy도 시스템 프롬프트를 따로 받지 않으므로 합친다.
            var prompt = CliPrompt.Combine(systemPrompt ?? string.Empty, userPrompt ?? string.Empty);

            var arguments = BuildArguments(prompt, _modelName, effort, _timeout);

            // 프로세스를 띄우기 전에 막는다. 조용히 잘리거나 알 수 없는 오류로 죽는 것보다 낫다.
            EnsureCommandLineFits(_command, arguments);

            using var workspace = new CliWorkspace();

            CliProcessResult processResult;
            try
            {
                processResult = await CliProcessRunner.RunAsync(
                    _command, arguments, null, workspace.Path, _timeout, cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw CliFailureClassifier.CommandNotFound(ProviderName, _command, ex);
            }

            if (!processResult.Succeeded)
            {
                throw CliFailureClassifier.ToException(ProviderName, _command, processResult, null);
            }

            return new AiResult { Content = ParseResult(processResult.StandardOutput) };
        }
    }
}
