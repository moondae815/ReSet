using System.Collections.Generic;
using System.Text.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    [Collection(GlobalSerilogLoggerCollection.Name)]
    public class CliUsageTests
    {
        private static JsonElement Parse(string json) =>
            JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void ReadCounter_ReadsNumber()
        {
            var element = Parse("{\"input_tokens\":19406}");

            Assert.Equal(19406, CliUsage.ReadCounter(element, "input_tokens"));
        }

        [Fact]
        public void ReadCounter_MissingProperty_ReturnsNull()
        {
            // 0이 아니라 null이어야 한다. 0은 "재보니 캐시 읽기가 없었다"는 측정값이고,
            // null은 "이 provider는 그 값을 아예 보고하지 않는다"는 뜻이다. 둘을 뭉개면
            // 필드가 없는 provider를 두고 "캐시를 쓰지 않는다"고 결론짓게 된다.
            var element = Parse("{\"input_tokens\":10}");

            Assert.Null(CliUsage.ReadCounter(element, "cache_write_input_tokens"));
        }

        // JsonElement.TryGetInt32는 ValueKind가 Number가 아니면 false를 돌려주는 게 아니라
        // InvalidOperationException을 던진다. 봉투 스키마가 바뀌어 숫자 자리에 null이나
        // 문자열이 오면, 토큰 집계 한 줄 때문에 분석 전체가 죽는다.
        [Theory]
        [InlineData("{\"input_tokens\":null}")]
        [InlineData("{\"input_tokens\":\"10\"}")]
        [InlineData("{\"input_tokens\":true}")]
        [InlineData("{\"input_tokens\":{\"nested\":1}}")]
        [InlineData("{\"input_tokens\":[1,2]}")]
        public void ReadCounter_NonNumberKind_ReturnsNullWithoutThrowing(string json)
        {
            var element = Parse(json);

            Assert.Null(CliUsage.ReadCounter(element, "input_tokens"));
        }

        [Fact]
        public void ReadCounter_NonIntegerNumber_ReturnsNull()
        {
            var element = Parse("{\"input_tokens\":1.5}");

            Assert.Null(CliUsage.ReadCounter(element, "input_tokens"));
        }

        [Fact]
        public void WriteToLog_RendersEveryCounter()
        {
            var usage = new CliUsage(
                Input: 2, Output: 3, CacheWrite: 9417, CacheRead: 15971, Thinking: 288);

            var messages = Capture(() => usage.WriteToLog("claude-cli"));

            var line = Assert.Single(messages);
            Assert.Contains("claude-cli", line);
            Assert.Contains("15971", line);
            Assert.Contains("9417", line);
            Assert.Contains("288", line);
        }

        [Fact]
        public void WriteToLog_RendersMissingCounterAsNotReported()
        {
            // agy는 캐시 쓰기를 보고하지 않는다. 그 자리에 0을 찍으면 "쓰기가 0회였다"는
            // 거짓 측정이 로그에 남는다.
            var usage = new CliUsage(
                Input: 19406, Output: 299, CacheWrite: null, CacheRead: 0, Thinking: 288);

            var messages = Capture(() => usage.WriteToLog("agy-cli"));

            var line = Assert.Single(messages);
            Assert.Contains("미보고", line);
            Assert.Contains("19406", line);
        }

        private static List<string> Capture(System.Action action)
        {
            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
            try
            {
                action();
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
