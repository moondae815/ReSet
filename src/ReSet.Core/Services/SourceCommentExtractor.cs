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
        ///
        /// 콜론 뒤 라벨은 반드시 글자(또는 밑줄)로 시작해야 한다 - 범례는 코드를
        /// "이름"에 대응시키는 것이고, 그 이름이 다시 숫자면 범례가 아니라 다른 무엇
        /// (실측: UP_UTIL_SETTLE_COMM_UPD.Procedure:95의 "2019.06-10 17:37"이라는
        /// 괄호 속 시각 - "17:37"은 콜론 뒤가 "37"이라는 숫자라 범례가 아니다)이다.
        /// 이 판별자가 없으면 그 우연한 문자열이 유일한 앵커가 되어 L1이 재생성으로
        /// 고칠 수 없는 요구를 낸다. 글자로 시작하기만 하면 되므로 항목이 하나뿐인
        /// 범례("0:반올림, 0&lt;&gt;절사")는 여전히 범례로 인정된다 - 나열 개수가
        /// 아니라 라벨의 모양이 판별자다.
        ///
        /// 라벨 문자 집합에 여는/닫는 괄호를 넣지 않는다 - 그래야 "(1:해외카드
        /// 0:그외카드)"처럼 마지막 항목 뒤에 닫는 괄호가 바로 붙는 실측 형태에서도
        /// 앵커에 괄호가 섞이지 않는다(정규식이 스스로 멈추므로 별도 트림이 필요 없다).
        /// </summary>
        private static readonly Regex CodeLegendRegex =
            new(@"\d+:[\p{L}_][\p{L}\p{N}_]*", RegexOptions.Compiled);

        /// <summary>
        /// 순수 기호(구분선·배너)만 있고 글자·숫자가 하나도 없는 줄. 이런 배너는
        /// 재료에도 넣지 않는다 - 캡(MaxBlocks) 예산을 배너가 먼저 먹으면 뒤쪽의
        /// 진짜 범례·비실행 주석이 밀려난다(실측: COMM_UPD 466줄 중 다수가 이
        /// 모양의 구분선이었다).
        /// </summary>
        private static readonly Regex NoiseRegex =
            new(@"^[^\p{L}\p{N}]*$", RegexOptions.Compiled);

        /// <summary>식별자 앵커. 밑줄이 있거나 대문자가 섞인 3자 이상 토큰.</summary>
        private static readonly Regex IdentifierAnchorRegex =
            new(@"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b|\b[A-Z][a-z]+[A-Z][A-Za-z0-9]*\b",
                RegexOptions.Compiled);

        /// <summary>날짜 앵커. 2021.11.29 / 2021-11-29 / 2021.11.29자 모두.</summary>
        private static readonly Regex DateAnchorRegex =
            new(@"\b\d{4}[.\-]\d{1,2}[.\-]\d{1,2}\b", RegexOptions.Compiled);

        /// <summary>
        /// 앵커를 낳는 종류(NonExecutable · CodeLegend)가 우선순위 0, 그렇지 않은
        /// 종류(Header · Prose)가 우선순위 1이다. 캡을 넘길 때 이 우선순위로 자른다 -
        /// 실측(COMM_UPD)에서 위치(파일 순서)로 자르면 12행의 구분선이 300행의
        /// 진짜 범례보다 앞서 캡을 차지했다.
        /// </summary>
        private static int Priority(string kind) => kind is "NonExecutable" or "CodeLegend" ? 0 : 1;

        public static IReadOnlyList<SourceCommentBlock> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<SourceCommentBlock>();

            var all = new List<SourceCommentBlock>();
            var lines = ddlText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var createSeen = false;

            // 캡을 걸지 않고 파일 전체를 한 번 훑는다 - 캡 선정을 정보성 기준으로
            // 하려면 무엇이 있는지 전부 알아야 한다. 실측 SP는 최대 수백 줄이라
            // 전체 스캔 비용은 무시할 만하다.
            for (var i = 0; i < lines.Length; i++)
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
                if (NoiseRegex.IsMatch(body)) continue; // 배너·구분선은 재료에도 넣지 않는다.

                var kind = !createSeen ? "Header"
                    : CodeLegendRegex.IsMatch(body) ? "CodeLegend"
                    : SqlTokenRegex.IsMatch(body) ? "NonExecutable"
                    : "Prose";

                if (kind is "Prose" or "Header")
                {
                    // Prose: 앵커가 없으므로 프롬프트 전용이다. 재료에는 남긴다 -
                    // 체크리스트가 이 주석의 존재를 알려야 한다.
                    //
                    // Header: [Fix Round 5 - 리뷰 실측] NonExecutable과 같은 식별자·날짜
                    // 앵커 규칙을 헤더 블록에도 적용했더니, "-- Copyright ⓒ 2001 by
                    // PayLetter Inc."의 "PayLetter"나 "-- Author : kks, 2019-04-30"의
                    // 날짜가 앵커가 됐다. 실측 코퍼스(output/**/docs/Spec.md 26건)
                    // 전부에 "PayLetter"가 0건 등장하는데도 CheckSourceComments가
                    // 이 앵커의 부재를 결함으로 들어, 저작권 고지 전사를 강제하는
                    // 오류 180건 중 다수를 차지했다. 헤더 재료의 존재 이유는 설계
                    // §2.4의 A5(헤더/구현 모순) 검사 하나뿐이고, 그 검사는
                    // MechanicalValidator.HeaderContractTerms("헤더"·"주석"·
                    // "Inner SP"·"NONE")로 이미 "선언 키워드"를 별도로 다룬다 -
                    // 이 Anchors 필드가 아니라 블록의 Text 원문을 직접 본다. 그래서
                    // Header 블록은 Prose와 같이 앵커를 비워 CheckSourceComments의
                    // 개별 앵커 대조에서 조용히 빠지고, 헤더 모순 검사만 계속
                    // 이 블록을 소비한다.
                    all.Add(new SourceCommentBlock(kind, body, i + 1, Array.Empty<string>()));
                    continue;
                }

                all.Add(new SourceCommentBlock(kind, body, i + 1, BuildAnchors(kind, body)));
            }

            if (all.Count <= MaxBlocks) return all;

            // 우선순위로 캡을 채우고, 같은 우선순위 안에서는 파일 순서를 유지한다.
            // 마지막에 다시 원래 줄 순서로 정렬해 체크리스트가 읽기 순서를 지키게 한다.
            return all
                .Select((block, index) => (block, index))
                .OrderBy(t => Priority(t.block.Kind))
                .ThenBy(t => t.index)
                .Take(MaxBlocks)
                .OrderBy(t => t.index)
                .Select(t => t.block)
                .ToList();
        }

        private static IReadOnlyList<string> BuildAnchors(string kind, string body)
        {
            var anchors = new List<string>();

            if (kind == "CodeLegend")
            {
                // CodeLegendRegex의 문자 집합이 공백·괄호를 애초에 담지 않으므로
                // 매치 값을 그대로 앵커로 쓴다 - 후처리 트림이 따로 필요 없다.
                foreach (Match m in CodeLegendRegex.Matches(body))
                {
                    if (!anchors.Contains(m.Value, StringComparer.Ordinal)) anchors.Add(m.Value);
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
