using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <param name="Kind">"NonExecutable" · "CodeLegend" · "Header" 중 하나.</param>
    /// <param name="Text">주석 원문(주석 기호 제외).</param>
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
    /// <param name="Anchors">
    /// 명세서 본문에서 그대로 찾을 수 있는 토큰. 비어 있으면 L1이 대조하지
    /// 않는다 - 왜 검사하지 않는지가 이 필드로 코드에 남는다.
    /// </param>
    public sealed record SourceCommentBlock(
        string Kind, string Text, int Line, IReadOnlyList<string> Anchors);

    /// <summary>
    /// 원본 DDL의 주석 중 명세서가 반드시 옮겨야 하는 것만 뽑는다.
    ///
    /// 전부 뽑지 않는 이유는 OmissionCommentScanner가 남긴 교훈과 같다 -
    /// "패턴을 좁게 유지한다. 배너가 잦으면 사람이 읽지 않는다." 큰 SP는 주석이
    /// 수백 줄이고, 전부 실으면 체크리스트가 무의미해진다.
    ///
    /// 이 추출기 하나가 프롬프트 체크리스트와 L1 대조 기준의 단일 권위다.
    /// AiService 안에만 두면 L1이 알 수 없고, 렌더링의 부수효과로 기록하면
    /// 렌더 경로가 둘이라 결과가 달라진다(SchemaPromptColumnSelector와 같은 판단).
    /// </summary>
    public static class SourceCommentExtractor
    {
        private const int MaxBlocks = 40;

        private static readonly Regex LineCommentRegex =
            new(@"--(?<body>.*)$", RegexOptions.Compiled);

        /// <summary>SQL 토큰이 들어 있으면 코드가 주석 처리된 것으로 본다.</summary>
        private static readonly Regex SqlTokenRegex = new(
            @"\b(AND|OR|SELECT|FROM|WHERE|JOIN|INSERT|UPDATE|DELETE|SUM|CASE|WHEN|NOT\s+IN|IN)\b|=",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 0:반올림, 1:자동 같은 코드 범례. 숫자와 콜론 사이에 공백이 없어야 한다 -
        /// 공백을 허용하면 "…+1 : 집계 고려" 같은 산문 속 우연한 "숫자 콜론 단어"
        /// 형태까지 범례로 오분류한다(실측: Extract_PlainProseComment 회귀).
        /// </summary>
        private static readonly Regex CodeLegendRegex =
            new(@"\d+:[^\s,;]+", RegexOptions.Compiled);

        /// <summary>식별자 앵커. 밑줄이 있거나 대문자가 섞인 3자 이상 토큰.</summary>
        private static readonly Regex IdentifierAnchorRegex =
            new(@"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b|\b[A-Z][a-z]+[A-Z][A-Za-z0-9]*\b",
                RegexOptions.Compiled);

        /// <summary>날짜 앵커. 2021.11.29 / 2021-11-29 / 2021.11.29자 모두.</summary>
        private static readonly Regex DateAnchorRegex =
            new(@"\b\d{4}[.\-]\d{1,2}[.\-]\d{1,2}\b", RegexOptions.Compiled);

        public static IReadOnlyList<SourceCommentBlock> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<SourceCommentBlock>();

            var blocks = new List<SourceCommentBlock>();
            var lines = ddlText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var createSeen = false;

            for (var i = 0; i < lines.Length && blocks.Count < MaxBlocks; i++)
            {
                var line = lines[i];

                if (!createSeen
                    && line.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
                {
                    createSeen = true;
                }

                var match = LineCommentRegex.Match(line);
                if (!match.Success) continue;

                var body = match.Groups["body"].Value.Trim();
                if (body.Length == 0) continue;

                var kind = !createSeen ? "Header"
                    : CodeLegendRegex.IsMatch(body) ? "CodeLegend"
                    : SqlTokenRegex.IsMatch(body) ? "NonExecutable"
                    : "Prose";

                if (kind == "Prose")
                {
                    // 앵커가 없으므로 프롬프트 전용이다. 재료에는 남긴다 -
                    // 체크리스트가 이 주석의 존재를 알려야 한다.
                    blocks.Add(new SourceCommentBlock(kind, body, i + 1, Array.Empty<string>()));
                    continue;
                }

                blocks.Add(new SourceCommentBlock(kind, body, i + 1, BuildAnchors(kind, body)));
            }

            return blocks;
        }

        private static IReadOnlyList<string> BuildAnchors(string kind, string body)
        {
            var anchors = new List<string>();

            if (kind == "CodeLegend")
            {
                foreach (Match m in CodeLegendRegex.Matches(body))
                {
                    var token = Regex.Replace(m.Value, @"\s+", string.Empty);
                    if (!anchors.Contains(token, StringComparer.Ordinal)) anchors.Add(token);
                }

                return anchors;
            }

            foreach (Match m in IdentifierAnchorRegex.Matches(body))
            {
                if (!anchors.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) anchors.Add(m.Value);
            }

            foreach (Match m in DateAnchorRegex.Matches(body))
            {
                if (!anchors.Contains(m.Value, StringComparer.Ordinal)) anchors.Add(m.Value);
            }

            return anchors;
        }
    }
}
