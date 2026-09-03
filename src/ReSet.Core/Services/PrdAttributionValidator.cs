using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    public enum PrdDefectType
    {
        SectionMissing,
        ConfidenceVocabulary,
        EvidenceMissing,
        IdPrefixMismatch,
        EvidenceSourceNotAllowed,
        EvidenceHeadingNotFound,
        EvidenceQuoteNotFound,
    }

    public sealed record PrdDefect(
        PrdDefectType Type,
        string Section,
        string RequirementId,
        string Message);

    public sealed class PrdValidationResult
    {
        public PrdValidationResult(IReadOnlyList<PrdDefect> defects) => Defects = defects;

        public IReadOnlyList<PrdDefect> Defects { get; }

        public bool IsValid => Defects.Count == 0;
    }

    /// <summary>
    /// Prd.md의 요구 항목이 원본 Spec.md의 실재하는 자리를 인용하는지 대조한다.
    ///
    /// [무엇을 재고 무엇을 못 재는가]
    /// 이 검사가 참거짓을 세우는 것은 「인용이 실재하는가」까지다. 인용은 진짜인데
    /// 요구 서술이 그 인용과 무관한 경우(귀속 오배치)는 이 오라클로 잴 수 없다 -
    /// 모델이 Spec에서 아무 구절이나 복사해 붙이면 전부 통과한다. PRD에는 L2가
    /// 없으므로 그 구멍은 사람 검토에 남으며, 문서 배너가 그 사실을 명시한다.
    /// 검사가 실제보다 강한 척하는 쪽이 검사가 약한 것보다 위험하다.
    ///
    /// [MechanicalValidator에 넣지 않은 이유]
    /// 그쪽 재료는 SpecExpectations(원본 DDL·정적 분석 유래)인데 여기 오라클은
    /// Spec.md 텍스트 하나뿐이다. 재료가 겹치지 않는 검사를 같은 클래스에 넣으면
    /// IsConsolidated bool 분기가 3분기로 번진다.
    /// </summary>
    public static class PrdAttributionValidator
    {
        private static readonly string[] AllowedConfidence = { "도출", "추정" };

        /// <summary>
        /// 인용 대조용 정규화. 공백과 마크다운 강조·표 파이프를 걷어낸다.
        ///
        /// 이것이 없으면 Spec 본문의 `**강조**`나 표 정렬 공백 때문에 멀쩡한 인용이
        /// 결함으로 보고된다. 오탐이 잦은 검사는 곧 꺼지므로, 대조는 두 문자열이
        /// 같은 내용을 말하는지만 본다.
        /// </summary>
        private static string NormalizeForQuoteMatch(string text)
        {
            var kept = text.Where(ch => !char.IsWhiteSpace(ch)
                                        && ch != '*' && ch != '`' && ch != '|'
                                        && ch != '_' && ch != '~');
            return string.Concat(kept);
        }

        /// <summary>마크다운 헤딩에서 부호를 정규화한다. 앞의 # 문자, 공백, 접두 숫자·번호, 뒤의 구두점을 제거한다.</summary>
        private static string NormalizeHeading(string heading)
        {
            var text = heading.TrimStart('#').Trim();

            // 접두 숫자 제거 (3. 또는 3) 또는 3 - 형태)
            var match = System.Text.RegularExpressions.Regex.Match(text, @"^\d+[.\)\s-]+(.*)$");
            if (match.Success)
            {
                text = match.Groups[1].Value.Trim();
            }

            // 뒤의 구두점 제거 (:)
            return text.TrimEnd(':').Trim();
        }

        /// <summary>지정 헤딩 아래 본문만 이어 붙인다. 헤딩이 없으면 null. 정확 일치를 먼저 시도하고, 실패하면 부분 일치로 폴백한다.</summary>
        private static string? ExtractSectionBody(IReadOnlyList<string> specLines, string heading)
        {
            var exact = MarkdownSectionLocator.LocateSection(specLines, heading, "## ");
            var (headerIndex, endIndex) = exact.HeaderIndex >= 0
                ? exact
                : MarkdownSectionLocator.LocateSection(
                    specLines,
                    NormalizeHeading(heading),
                    "## ",
                    exact: false);

            if (headerIndex < 0)
            {
                return null;
            }

            return string.Join("\n", specLines.Skip(headerIndex + 1).Take(endIndex - headerIndex - 1));
        }

        /// <summary>근거 칸을 헤딩과 인용 구절로 가른 것.</summary>
        public sealed record PrdEvidenceReference(string Heading, string Quote);

        /// <summary>
        /// `## CRUD 분석 &gt; "TB_SETTLE_DAILY에 INSERT"` 형태를 가른다.
        ///
        /// 줄번호가 아니라 헤딩+인용을 쓰는 이유: 줄번호는 Spec을 다시 생성하는
        /// 순간 전부 거짓이 되지만, 헤딩과 원문 구절은 두 문서가 같은 이야기를
        /// 하는 한 살아 있다.
        /// </summary>
        public static bool TryParseEvidence(string? raw, out PrdEvidenceReference reference)
        {
            reference = new PrdEvidenceReference(string.Empty, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var separator = raw.IndexOf('>');
            if (separator < 0)
            {
                return false;
            }

            var heading = raw[..separator].Trim();
            var rest = raw[(separator + 1)..].Trim();

            var first = rest.IndexOfAny(new[] { '"', '"' });
            var last = rest.LastIndexOfAny(new[] { '"', '"' });
            if (first < 0 || last <= first)
            {
                return false;
            }

            var quote = rest[(first + 1)..last].Trim();
            if (heading.Length == 0 || quote.Length == 0)
            {
                return false;
            }

            reference = new PrdEvidenceReference(heading, quote);
            return true;
        }

        public static PrdValidationResult Validate(string? prdMarkdown, string? specMarkdown)
        {
            var defects = new List<PrdDefect>();
            var prdLines = MarkdownSectionLocator.SplitLines(prdMarkdown);
            var specLines = MarkdownSectionLocator.SplitLines(specMarkdown);
            var sectionBodyCache = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var rule in PrdSectionContract.Sections)
            {
                var (headerIndex, _) = MarkdownSectionLocator.LocateSection(prdLines, rule.Heading, "## ");
                if (headerIndex < 0)
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.SectionMissing,
                        rule.Heading,
                        string.Empty,
                        $"필수 섹션 '{rule.Heading}'이 없습니다."));
                }
            }

            foreach (var requirement in PrdDocumentParser.Parse(prdMarkdown))
            {
                if (!AllowedConfidence.Contains(requirement.Confidence))
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.ConfidenceVocabulary,
                        requirement.Section,
                        requirement.Id,
                        $"확신도는 '도출' 또는 '추정'이어야 합니다. 실제 값: '{requirement.Confidence}'"));
                }

                if (string.IsNullOrWhiteSpace(requirement.EvidenceRaw))
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.EvidenceMissing,
                        requirement.Section,
                        requirement.Id,
                        "근거 칸이 비어 있습니다. '추정' 항목도 재구성의 출발점이 된 인용을 달아야 합니다."));
                }

                var rule = PrdSectionContract.Sections.First(s => s.Heading == requirement.Section);

                if (!requirement.Id.StartsWith(rule.IdPrefix + "-", StringComparison.Ordinal))
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.IdPrefixMismatch,
                        requirement.Section,
                        requirement.Id,
                        $"'{requirement.Section}'의 요구 ID는 '{rule.IdPrefix}-'로 시작해야 합니다."));
                }

                if (!TryParseEvidence(requirement.EvidenceRaw, out var evidence))
                {
                    if (!string.IsNullOrWhiteSpace(requirement.EvidenceRaw))
                    {
                        defects.Add(new PrdDefect(
                            PrdDefectType.EvidenceMissing,
                            requirement.Section,
                            requirement.Id,
                            "근거 칸이 '## 헤딩 > \"원문 구절\"' 형식이 아닙니다."));
                    }

                    continue;
                }

                var normalizedEvidence = NormalizeHeading(evidence.Heading);
                var isAllowedSource = rule.AllowedSources.Any(source =>
                    NormalizeHeading(source).Equals(normalizedEvidence, StringComparison.Ordinal));
                if (!isAllowedSource)
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.EvidenceSourceNotAllowed,
                        requirement.Section,
                        requirement.Id,
                        $"'{requirement.Section}'은 {string.Join(", ", rule.AllowedSources)}에서만 파생할 수 있습니다. 실제 인용: '{evidence.Heading}'"));
                }

                if (!sectionBodyCache.TryGetValue(evidence.Heading, out var body))
                {
                    body = ExtractSectionBody(specLines, evidence.Heading);
                    sectionBodyCache[evidence.Heading] = body;
                }

                if (body is null)
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.EvidenceHeadingNotFound,
                        requirement.Section,
                        requirement.Id,
                        $"근거로 인용한 헤딩 '{evidence.Heading}'이 원본 명세서에 없습니다."));
                    continue;
                }

                if (!NormalizeForQuoteMatch(body).Contains(
                        NormalizeForQuoteMatch(evidence.Quote), StringComparison.Ordinal))
                {
                    defects.Add(new PrdDefect(
                        PrdDefectType.EvidenceQuoteNotFound,
                        requirement.Section,
                        requirement.Id,
                        $"인용 구절 \"{evidence.Quote}\"을 '{evidence.Heading}' 절 본문에서 찾을 수 없습니다."));
                }
            }

            return new PrdValidationResult(defects);
        }
    }
}
