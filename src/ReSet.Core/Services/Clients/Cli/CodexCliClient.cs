using System;
using System.Collections.Generic;
using System.IO;
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

        public async Task<AiResult> ChatAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            string? effort = null,
            CancellationToken cancellationToken = default)
        {
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
