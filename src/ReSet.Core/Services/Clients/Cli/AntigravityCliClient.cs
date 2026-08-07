using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// agy의 JSON 응답에서 뽑아낸 값. 실패 판정은 여기서 하지 않는다 -
    /// 판정과 예외 생성은 CliFailureClassifier를 거쳐야 하기 때문이다.
    /// </summary>
    public sealed class AntigravityCliResponse
    {
        public string? Status { get; init; }
        public string? Response { get; init; }

        public bool IsSuccess =>
            string.Equals(Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
    }

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
            // OS의 명령행 한계는 바이트 단위다. UTF-16 문자 수로 재면 한글(UTF-8 3바이트)
            // 프롬프트에서 3배까지 과소 평가한다. 35만 자짜리 한글 프롬프트가 100만 검사를
            // 통과한 뒤 execve가 E2BIG로 거절하고, 그것이 Win32Exception으로 올라와
            // "명령을 찾지 못했습니다 - PATH를 확인하십시오"라는 엉뚱한 안내가 된다.
            // 인용부호와 구분 공백을 감안해 인자마다 여유를 더한다.
            var length = Encoding.UTF8.GetByteCount(command);
            foreach (var argument in arguments)
            {
                length += Encoding.UTF8.GetByteCount(argument) + 3;
            }

            if (length <= MaxCommandLineLength)
            {
                return;
            }

            throw new InvalidOperationException(
                $"이 프롬프트는 agy-cli로 처리할 수 없습니다 " +
                $"(명령행 {length:N0}바이트, 플랫폼 한계 {MaxCommandLineLength:N0}바이트). " +
                "agy는 프롬프트를 표준 입력으로 받지 못해 명령행으로 넘겨야 하며, 우회로가 없습니다. " +
                "claude-cli 또는 API provider를 사용하십시오.");
        }

        /// <summary>
        /// JSON을 값으로만 옮긴다. 실패라도 여기서 예외를 만들지 않는다 -
        /// 손으로 만든 예외는 CliFailureClassifier를 우회해 종류 판정과
        /// "다른 provider로 바꾸라"는 안내를 통째로 잃는다.
        /// </summary>
        public static AntigravityCliResponse ParseResult(string standardOutput)
        {
            try
            {
                using var document = JsonDocument.Parse(standardOutput);
                var root = document.RootElement;

                return new AntigravityCliResponse
                {
                    Status = ReadString(root, "status"),
                    Response = ReadString(root, "response")
                };
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"agy-cli 응답을 JSON으로 해석할 수 없습니다.\n[출력]\n{standardOutput}", ex);
            }
        }

        /// <summary>
        /// 문자열이 아닌 JSON 종류에 GetString()을 부르면 InvalidOperationException이 나는데,
        /// 그것은 위의 catch (JsonException)에 걸리지 않는다. 스키마가 바뀌면 출력 덤프 없이
        /// 프레임워크 기본 메시지만 튀어나온다. ClaudeCliClient.ReadString과 같은 방식으로 막는다.
        /// </summary>
        private static string? ReadString(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        public async Task<AiResult> ChatAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            string? effort = null,
            string? volatileUserSuffix = null,
            CancellationToken cancellationToken = default)
        {
            userPrompt = PromptComposition.MergeVolatileSuffix(userPrompt, volatileUserSuffix);

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

            var response = ParseResult(processResult.StandardOutput);

            // agy는 쿼터 소진 같은 실패도 종료 코드 0으로 끝내면서 stdout JSON에만 담는다.
            // 분류기는 stdout을 보지 않으므로(오진 방지) 원문 JSON을 extraDetail로 직접 넘긴다.
            // 이렇게 해야 claude-cli와 같은 계약(종류 판정 + provider 전환 안내)이 유지된다.
            if (!response.IsSuccess)
            {
                throw CliFailureClassifier.ToException(
                    ProviderName, _command, processResult,
                    $"agy-cli가 실패 상태를 반환했습니다 (status: {response.Status}).\n" +
                    $"[출력]\n{processResult.StandardOutput}");
            }

            if (string.IsNullOrWhiteSpace(response.Response))
            {
                throw CliFailureClassifier.ToException(
                    ProviderName, _command, processResult,
                    $"agy-cli 응답에 response 속성이 없거나 비어 있습니다.\n" +
                    $"[출력]\n{processResult.StandardOutput}");
            }

            return new AiResult { Content = response.Response };
        }
    }
}
