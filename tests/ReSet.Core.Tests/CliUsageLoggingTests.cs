using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 세 CLI 클라이언트가 봉투에서 읽은 토큰 집계를 실제로 로그까지 내보내는지 본다.
    ///
    /// 파서 단위 테스트는 "JSON에서 숫자를 뽑았다"까지만 증명한다. 뽑아 놓고 로그로
    /// 내보내지 않으면 관측할 수 없다는 문제는 그대로 남는데, 캐시 미스는 오류를 내지
    /// 않으므로 그 침묵은 영원히 드러나지 않는다. 그래서 ChatAsync를 스텁으로 끝까지
    /// 돌려 로그 줄을 직접 확인한다. 진짜 claude/codex/agy 바이너리는 부르지 않는다.
    ///
    /// 전역 Log.Logger를 갈아 끼우므로 반드시 이 컬렉션에 있어야 한다.
    /// </summary>
    [Collection(GlobalSerilogLoggerCollection.Name)]
    public class CliUsageLoggingTests
    {
        [Fact]
        public async Task ClaudeCli_LogsCacheCountersFromEnvelope()
        {
            const string json =
                "{\"is_error\":false,\"result\":\"PONG\",\"usage\":{\"input_tokens\":2," +
                "\"cache_creation_input_tokens\":9417,\"cache_read_input_tokens\":15971," +
                "\"output_tokens\":3}}";

            using var stub = CliStubScript.Create(
                posixBody: $"cat > /dev/null\necho '{json}'\n",
                windowsBody: $"more > nul\r\necho {json}\r\n");

            var client = new ClaudeCliClient(stub.Path, "sonnet", TimeSpan.FromSeconds(60));

            var messages = await CaptureAsync(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            var line = Assert.Single(messages, m => m.Contains("토큰 사용량"));
            Assert.Contains("claude-cli", line);
            Assert.Contains("15971", line);
            Assert.Contains("9417", line);
        }

        [Fact]
        public async Task AgyCli_LogsCacheReadAndMarksCacheWriteUnreported()
        {
            const string json =
                "{\"status\":\"SUCCESS\",\"response\":\"PONG\",\"usage\":{\"input_tokens\":19406," +
                "\"output_tokens\":299,\"thinking_tokens\":288,\"cache_read_tokens\":0}}";

            using var stub = CliStubScript.Create(
                posixBody: $"echo '{json}'\n",
                windowsBody: $"echo {json}\r\n");

            var client = new AntigravityCliClient(stub.Path, "gemini", TimeSpan.FromSeconds(60));

            var messages = await CaptureAsync(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            var line = Assert.Single(messages, m => m.Contains("토큰 사용량"));
            Assert.Contains("agy-cli", line);
            Assert.Contains("19406", line);
            Assert.Contains("288", line);
            // 캐시 쓰기는 봉투에 없다. 0이 아니라 미보고로 남아야 한다.
            Assert.Contains(CliUsage.NotReported, line);
        }

        // codex는 본문을 -o 파일로, 집계를 stdout JSONL로 따로 낸다. 두 경로가 한
        // 호출에서 함께 살아 있어야 --json 추가가 본문 경로를 깨지 않았다고 말할 수 있다.
        [Fact]
        public async Task CodexCli_LogsUsageFromJsonlWhileStillReadingBodyFromResultFile()
        {
            const string json =
                "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":14165," +
                "\"cached_input_tokens\":9984,\"cache_write_input_tokens\":0," +
                "\"output_tokens\":5,\"reasoning_output_tokens\":0}}";

            using var stub = CliStubScript.Create(
                posixBody:
                    "cat > /dev/null\n" +
                    "prev=\"\"\n" +
                    "for a in \"$@\"; do\n" +
                    "  if [ \"$prev\" = \"-o\" ]; then printf 'PONG-FROM-CODEX\\n' > \"$a\"; fi\n" +
                    "  prev=\"$a\"\n" +
                    "done\n" +
                    $"echo '{json}'\n",
                windowsBody:
                    "more > nul\r\n" +
                    ":loop\r\n" +
                    "if \"%~1\"==\"\" goto after\r\n" +
                    "if \"%~1\"==\"-o\" goto found\r\n" +
                    "shift\r\n" +
                    "goto loop\r\n" +
                    ":found\r\n" +
                    "shift\r\n" +
                    "echo PONG-FROM-CODEX>\"%~1\"\r\n" +
                    ":after\r\n" +
                    $"echo {json}\r\n");

            var client = new CodexCliClient(stub.Path, "gpt-5.6-terra", TimeSpan.FromSeconds(60));

            AiResultHolder holder = new();
            var messages = await CaptureAsync(async () =>
                holder.Content = (await client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f)).Content);

            Assert.Equal("PONG-FROM-CODEX", holder.Content?.Trim());

            var line = Assert.Single(messages, m => m.Contains("토큰 사용량"));
            Assert.Contains("codex-cli", line);
            Assert.Contains("9984", line);
            Assert.Contains("14165", line);
        }

        private sealed class AiResultHolder
        {
            public string? Content { get; set; }
        }

        private static async Task<List<string>> CaptureAsync(Func<Task> action)
        {
            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
            try
            {
                await action();
            }
            finally
            {
                Log.CloseAndFlush();
                Log.Logger = previousLogger;
            }

            return sink.Messages;
        }

        private sealed class CapturingSink : ILogEventSink
        {
            public List<string> Messages { get; } = new();
            public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
        }
    }
}
