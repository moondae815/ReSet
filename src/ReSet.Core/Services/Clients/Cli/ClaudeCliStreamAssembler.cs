using System;
using System.Collections.Generic;
using System.Text.Json;
using ReSet.Core.Models;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// `claude -p --output-format stream-json` 의 줄 단위 JSON 스트림을 하나의 본문으로 잇는다.
    ///
    /// [왜 - 2026-09-09 재생성 사고] `--output-format json` 은 <b>마지막 턴만</b> `result` 에
    /// 담는다. 한 턴의 출력 한도(실측 약 64,000 토큰)를 넘는 대상은 CLI 가 여러 턴으로 이어
    /// 쓰는데, 그중 마지막 조각만 읽어 <b>앞이 통째로 사라진 본문</b>이 L1 으로 넘어갔다.
    /// `UP_UTIL_SETTLE_EXCEPTION_PROC` 실측: 누적 출력 68,722 토큰인데 받은 본문 6,111 자.
    /// L1 은 옳게 「필수 절이 전부 없다」고 고발했고 재시도 6 회가 그 조각 위에서 낭비됐다.
    /// 같은 자리가 hy4·sonnet 두 모델에서 재현됐다 - 모델이 아니라 <b>전송 경로</b>의 결함이다.
    ///
    /// [왜 단순 연결이 아닌가] 이어지는 모양이 <b>둘</b>이다. 실측:
    /// <list type="bullet">
    ///   <item>글자 단위 이어짐 - 앞 턴이 `| 404 | @` 에서 끊기고 뒤 턴이 `po_intRetVal…` 로
    ///         시작한다. 겹침 0 이라 그대로 붙이면 맞다.</item>
    ///   <item>행 단위 재시작 - 앞 턴이 `| 1868 | @po_intRetVal` 에서 끊기고 뒤 턴이 그 행을
    ///         <b>처음부터 다시</b> 쓴다. 그대로 붙이면 그 행이 두 번 쓰여 깨진다.</item>
    /// </list>
    /// 그래서 「앞 턴의 꼬리와 뒤 턴의 머리가 겹치는 최대 길이를 찾아 잇는다」 한 규칙을 쓴다 -
    /// 겹침이 0 이면 단순 연결과 같으므로 두 모양을 다 처리한다.
    ///
    /// [빈 턴을 거른다] 실측에서 출력 토큰 5·2 짜리 빈 assistant 턴이 섞였다.
    /// </summary>
    public static class ClaudeCliStreamAssembler
    {
        /// <summary>
        /// 겹침 탐색의 상한. 무제한이면 큰 본문에서 비용이 제곱으로 든다.
        /// 실측 겹침은 22 자였고, 한 행 길이의 여유를 크게 잡아 4,000 자로 둔다.
        /// </summary>
        private const int MaxOverlapProbe = 4000;

        public static ClaudeCliResponse Assemble(string streamOutput)
        {
            var turns = new List<string>();
            string? resultField = null;
            bool isError = false;
            string? subtype = null, apiErrorStatus = null, stopReason = null;
            TokenUsage? usage = null;

            foreach (var line in (streamOutput ?? string.Empty).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                JsonDocument document;
                try { document = JsonDocument.Parse(trimmed); }
                catch (JsonException) { continue; }   // 스트림에는 JSON 아닌 줄이 섞일 수 있다

                using (document)
                {
                    var root = document.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (type == "assistant")
                    {
                        var text = ReadAssistantText(root);
                        if (!string.IsNullOrEmpty(text)) turns.Add(text);
                    }
                    // [type 이 없는 줄 - 옛 `json` 형식] 단일 객체 응답에는 type 이 없고
                    // result 만 있다. 그 형식으로 돌아가는 경로(스텁·구버전 CLI)를 위해 받는다.
                    else if (type == "result" || (type == null && root.TryGetProperty("result", out _)))
                    {
                        isError = root.TryGetProperty("is_error", out var e)
                                  && e.ValueKind == JsonValueKind.True;
                        subtype = ClaudeCliClient.ReadString(root, "subtype");
                        apiErrorStatus = ClaudeCliClient.ReadString(root, "api_error_status");
                        stopReason = ClaudeCliClient.ReadString(root, "stop_reason");
                        usage = ClaudeCliClient.ReadUsage(root);
                        resultField = ClaudeCliClient.ReadString(root, "result");
                    }
                }
            }

            // [턴이 없으면 result 로 물러난다] 한 턴으로 끝나는 응답이나 옛 `json` 형식은
            // assistant 이벤트 없이 result 만 온다. 그때는 그 값이 곧 전문이다.
            var assembled = turns.Count > 0 ? Join(turns) : (resultField ?? string.Empty);

            // [불변식 - 2026-09-09] 조립 결과는 마지막 턴(result)으로 **끝나야** 한다.
            // 이 검사는 본문이 마크다운인지 JSON인지 가정하지 않는다 - 이 클라이언트는
            // 명세서 생성뿐 아니라 Critic·Consolidator 에도 쓰이기 때문이다. 이어붙이기가
            // 언젠가 깨지면 조각이 조용히 나가는 대신 여기서 선다.
            if (turns.Count > 0 && !string.IsNullOrEmpty(resultField)
                && !assembled.EndsWith(resultField, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "claude-cli 스트림 조립이 마지막 턴으로 끝나지 않습니다 - 턴 이어붙이기가 "
                    + $"깨졌습니다(턴 {turns.Count}개, 조립 {assembled.Length}자, 마지막 턴 {resultField!.Length}자).");
            }

            return new ClaudeCliResponse
            {
                IsError = isError,
                Result = assembled,
                Subtype = subtype,
                ApiErrorStatus = apiErrorStatus,
                StopReason = stopReason,
                Usage = usage
            };
        }

        /// <summary>턴을 겹침만큼 잘라 잇는다.</summary>
        private static string Join(IReadOnlyList<string> turns)
        {
            if (turns.Count == 0) return string.Empty;

            var joined = turns[0];
            for (var i = 1; i < turns.Count; i++)
            {
                joined += turns[i].Substring(OverlapLength(joined, turns[i]));
            }
            return joined;
        }

        /// <summary>앞 본문의 꼬리와 뒤 본문의 머리가 겹치는 최대 길이.</summary>
        private static int OverlapLength(string previous, string next)
        {
            var max = Math.Min(Math.Min(previous.Length, next.Length), MaxOverlapProbe);
            for (var k = max; k > 0; k--)
            {
                if (string.CompareOrdinal(previous, previous.Length - k, next, 0, k) == 0) return k;
            }
            return 0;
        }

        private static string ReadAssistantText(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var message)) return string.Empty;
            if (!message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array) return string.Empty;

            var text = new System.Text.StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                // thinking 블록은 signature 까지 달고 오지만 본문이 아니다 - 섞으면 명세서가 오염된다.
                if (type != "text") continue;
                if (block.TryGetProperty("text", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    text.Append(v.GetString());
                }
            }
            return text.ToString();
        }
    }
}
