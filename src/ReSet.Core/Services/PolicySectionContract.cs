using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>정책서 근거 칸을 셋으로 가른 것 - 어느 명세서의, 어느 헤딩의, 어느 구절인가.</summary>
    public sealed record PolicyEvidenceReference(string Label, string Heading, string Quote);

    /// <summary>
    /// 정책서 규칙 표의 계약.
    ///
    /// [왜 계약을 클래스가 소유하는가] 생성 프롬프트와 검증기가 같은 표를 읽어야 둘이
    /// 갈라지지 않는다. PrdSectionContract가 이미 같은 자리를 지키고 있고, 거기 달린
    /// 주석이 하드코딩하면 조용히 갈라진다고 경고한다.
    ///
    /// [PRD와 다른 점] 정책서는 근거가 여러 명세서에 흩어져 있어 근거 칸이
    /// `dbo.UP_X · ## 헤딩 > "구절"` 로 SP 식별자를 앞에 단다.
    /// </summary>
    public static class PolicySectionContract
    {
        public const string TableHeader = "| ID | 업무 규칙 | 근거 | 코드값 |";
        public const string TableSeparator = "| :--- | :--- | :--- | :--- |";
        public const int ExpectedCellCount = 4;

        /// <summary>근거 칸에서 SP 식별자와 나머지를 가르는 구분자.</summary>
        public const string LabelSeparator = " · ";

        /// <summary>코드값이 없을 때 쓰는 표기. 빈 칸이 아니라 이 값이어야 「빠뜨림」과 「해당 없음」이 갈린다.</summary>
        public const string NoCodeValue = "-";

        public static string IdPrefixFor(int stageNumber) =>
            "S" + stageNumber.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 단계 하나의 H2 헤딩 줄. 명부 제목을 <b>변형 없이</b> 이어 붙인다.
        ///
        /// [왜 계약이 소유하는가 - 2026-09-06 C1] 이 문자열이 두 곳에 복제돼 있었다.
        /// 생성 프롬프트(AiService.GeneratePolicyStageAsync)는 `## {번호}. {제목}`을
        /// 요구했고, 서비스(SettlementPolicyService)는 `"## " + 제목`을 찾았다.
        /// 명부 제목에 번호가 있으면 프롬프트가 번호를 두 번 붙였고(`## 1. 1. 요율 적재`),
        /// 없으면 서비스가 못 찾았다 - <b>어느 명부로도 둘이 일치하지 않았다.</b>
        /// 프롬프트를 그대로 따르는 모델이 오면 단계마다 StageMissing이 나서 교정
        /// 재호출을 한 번씩 더 태우고, 조립된 문서에서는 규칙이 0개로 읽혀
        /// CheckCodeValues가 공허해지고 CheckProcedureCitationCoverage가 모든
        /// 프로시저를 고발한다. 그래서 한 함수만 남긴다.
        ///
        /// [왜 번호를 붙이지 않는가] 목차는 사람이 명부에 정한 것이고 도구가 그것을
        /// 바꾸면 단계 완전성 검사의 기준이 흔들린다(SettlementPolicyService의
        /// StageSpecCharWarningThreshold 주석과 같은 원칙). 부록 B도 stage.Title을
        /// 그대로 싣는다(PolicyDocumentAssembler) - 헤딩만 번호를 지어 붙이면 같은
        /// 단계가 문서 안에서 두 이름을 갖는다. 번호가 필요한 자리(규칙 ID 접두사
        /// <see cref="IdPrefixFor"/>)는 <see cref="EffectiveStageNumber"/>가 따로
        /// 소유하므로 헤딩이 번호를 지어낼 이유가 없다.
        ///
        /// Trim은 안전 장치다. 명부 파서가 이미 Trim한 값을 주므로 실제로는 항등이지만,
        /// 후행 공백이 남으면 MarkdownSectionLocator의 정확 일치 경로(line.Trim() ==
        /// headingLine)가 영영 못 찾는다. 여기 한 곳에서만 하므로 갈릴 수 없다.
        /// </summary>
        public static string StageHeading(string stageTitle) =>
            "## " + (stageTitle ?? string.Empty).Trim();

        /// <summary>`## 1. 제목` 에서 1을 뽑는다. 번호가 없으면 0.</summary>
        public static int StageNumberOf(string heading)
        {
            var match = Regex.Match(heading.TrimStart('#').Trim(), @"^(\d+)[.\)\s-]");
            return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
        }

        /// <summary>
        /// 단계의 실효 번호. 제목에 번호가 있으면 그것을, 없으면 명부에서의 순서(1부터)를 쓴다.
        ///
        /// [왜 이 함수가 있는가] 이 폴백이 두 곳에 따로 있으면 같은 값을 다르게 계산한다.
        /// 실제로 그랬다 - 검증기는 StageNumberOf 를 그대로 써서 번호 없는 제목에 0 을 얻고,
        /// 생성기는 i + 1 로 폴백해, 사람이 명부에서 번호를 지우는 순간 올바른 S1-01 이
        /// S0- 를 기대받아 오탐으로 고발됐다. 파서·검증기·생성기가 전부 이 함수만 부른다.
        /// </summary>
        public static int EffectiveStageNumber(string heading, int stageIndex)
        {
            var declared = StageNumberOf(heading);
            return declared > 0 ? declared : stageIndex + 1;
        }

        /// <summary>`dbo.UP_X · ## 헤딩 > "구절"` 을 가른다.</summary>
        public static bool TryParseEvidence(string? raw, out PolicyEvidenceReference reference)
        {
            reference = new PolicyEvidenceReference(string.Empty, string.Empty, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var separator = raw.IndexOf(LabelSeparator, StringComparison.Ordinal);
            if (separator < 0)
            {
                return false;
            }

            var label = raw[..separator].Trim();
            var rest = raw[(separator + LabelSeparator.Length)..].Trim();

            var arrow = rest.IndexOf('>');
            if (arrow < 0)
            {
                return false;
            }

            var heading = rest[..arrow].Trim();
            var quoted = rest[(arrow + 1)..].Trim();

            var first = quoted.IndexOfAny(new[] { '"', '“', '”' });
            var last = quoted.LastIndexOfAny(new[] { '"', '“', '”' });
            if (first < 0 || last <= first)
            {
                return false;
            }

            var quote = quoted[(first + 1)..last].Trim();
            if (label.Length == 0 || heading.Length == 0 || quote.Length == 0)
            {
                return false;
            }

            reference = new PolicyEvidenceReference(label, heading, quote);
            return true;
        }
    }
}
