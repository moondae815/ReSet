using System.IO;
using ReSet.Core.Services.Clients;
using ReSet.Core.Services.Clients.Cli;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// [왜 이 조립기가 있는가 - 2026-09-09 재생성 사고]
    /// `--output-format json`은 <b>마지막 턴만</b> `result`에 담는다. 한 턴의 출력 한도
    /// (실측 약 64,000 토큰)를 넘는 대상은 CLI가 여러 턴으로 이어 쓰는데, 그중 마지막
    /// 조각만 읽어 앞이 통째로 사라진 본문이 L1으로 넘어갔다. `UP_UTIL_SETTLE_EXCEPTION_PROC`
    /// 실측: 누적 출력 68,722 토큰인데 받은 본문은 6,111자.
    ///
    /// 픽스처는 실제 실행 스트림에서 오려 왔다(경계 보존, 본문만 축약). 지어낸 픽스처는
    /// 내 오해를 검사가 확인해 주는 자리가 된다.
    /// </summary>
    public class ClaudeCliStreamAssemblerTests
    {
        // 이어짐이 글자 단위일 때(겹침 0) - EXCEPTION_PROC 실물의 모양이다.
        [Fact]
        public void Assemble_WhenTurnsContinueExactly_ShouldConcatenate()
        {
            var stream = string.Join("\n", new[]
            {
                Assistant("| 404 | @"),
                Assistant("po_intRetVal | -201 |")
            });

            var result = ClaudeCliStreamAssembler.Assemble(stream);

            Assert.Equal("| 404 | @po_intRetVal | -201 |", result.Result);
        }

        // 이어짐이 행 단위 재시작일 때(겹침 있음) - 프로브 실물의 모양이다.
        // 그냥 붙이면 그 행이 두 번 쓰여 깨진다.
        [Fact]
        public void Assemble_WhenNextTurnRestartsThePartialLine_ShouldSpliceTheOverlap()
        {
            var stream = string.Join("\n", new[]
            {
                Assistant("| 1867 | 값 |\n| 1868 | @po_intRetVal"),
                Assistant("| 1868 | @po_intRetVal | -2068 | 값 |")
            });

            var result = ClaudeCliStreamAssembler.Assemble(stream);

            Assert.Equal("| 1867 | 값 |\n| 1868 | @po_intRetVal | -2068 | 값 |", result.Result);
        }

        // 빈 assistant 턴이 실제로 섞인다(실측 출력 토큰 5·2). 결합에 끼면 안 된다.
        [Fact]
        public void Assemble_ShouldIgnoreEmptyAssistantTurns()
        {
            var stream = string.Join("\n", new[]
            {
                Assistant(""),
                Assistant("본문"),
                Assistant("")
            });

            Assert.Equal("본문", ClaudeCliStreamAssembler.Assemble(stream).Result);
        }

        // result 이벤트가 usage·오류 여부를 진다 - 기존 계약을 유지해야 한다.
        [Fact]
        public void Assemble_ShouldReadUsageAndErrorFlagFromTheResultEvent()
        {
            var stream = string.Join("\n", new[]
            {
                Assistant("본문"),
                "{\"type\":\"result\",\"is_error\":false,\"usage\":{\"output_tokens\":96741,\"input_tokens\":7}}"
            });

            var result = ClaudeCliStreamAssembler.Assemble(stream);

            Assert.False(result.IsError);
            Assert.Equal(96741, result.Usage!.Output);
        }

        // 실물 스트림 - 턴 넷(빈 턴 둘 포함)이 하나로 합쳐져야 한다.
        [Fact]
        public void Assemble_OverTheRecordedStream_ShouldJoinEveryTurn()
        {
            var path = Path.Combine("Fixtures", "claude-cli-stream-multiturn.jsonl");
            Skip.If(!File.Exists(path), "픽스처가 없습니다");

            var result = ClaudeCliStreamAssembler.Assemble(File.ReadAllText(path));

            // 마지막 턴만 읽으면 앞의 행이 없다. 결합했으면 1번 행이 살아 있어야 한다.
            Assert.Contains("| 1 | @po_intRetVal | -201 |", result.Result);
            // 경계가 이어졌는가 - 끊긴 행 `| 1868 | @po_intRetVal` 이 두 번 쓰이면 안 된다.
            Assert.Contains("| 1868 | @po_intRetVal | -2068 |", result.Result);
            Assert.DoesNotContain("@po_intRetVal| 1868 |", result.Result);
        }

        private static string Assistant(string text) =>
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\""
            + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")
            + "\"}]}}";
    }
}
