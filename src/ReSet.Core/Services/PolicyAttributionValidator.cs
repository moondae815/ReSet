using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    public enum PolicyDefectType
    {
        StageMissing,
        StageOutOfOrder,
        IdPrefixMismatch,
        EvidenceMissing,
        EvidenceLabelUnknown,
        EvidenceHeadingNotFound,
        EvidenceQuoteNotFound,
        CodeValueNotInCodebook,
        ProcedureNeverCited,
    }

    public sealed record PolicyDefect(PolicyDefectType Type, string Subject, string RuleId, string Message);

    public sealed class PolicyValidationResult
    {
        public PolicyValidationResult(IReadOnlyList<PolicyDefect> defects) => Defects = defects;

        public IReadOnlyList<PolicyDefect> Defects { get; }

        public bool IsValid => Defects.Count == 0;
    }

    /// <summary>
    /// 정책서의 규칙이 원본 명세서의 실재하는 자리를 인용하는지 대조한다.
    ///
    /// [PRD와 무엇이 다른가] 원본이 여럿이다. 근거 칸의 SP 식별자로 어느 명세서를 볼지
    /// 고른 뒤, 그 다음은 EvidenceQuoteMatcher가 PRD와 똑같이 잰다.
    ///
    /// [무엇을 못 재는가] PrdAttributionValidator와 같다 - 인용이 진짜인데 규칙 서술이
    /// 그 인용과 무관한 경우(귀속 오배치)는 이 오라클로 잴 수 없다. 정책서에도 L2가
    /// 없으므로 그 구멍은 사람 검토에 남고 문서 배너가 그 사실을 명시한다.
    /// </summary>
    public static class PolicyAttributionValidator
    {
        public static PolicyValidationResult Validate(
            string? policyMarkdown,
            IReadOnlyList<string> stageHeadings,
            IReadOnlyDictionary<string, string> specsByLabel)
        {
            var defects = new List<PolicyDefect>();
            var lines = MarkdownSectionLocator.SplitLines(policyMarkdown);

            // 1. 단계 완전성과 순서
            var positions = new List<(string Heading, int Index)>();
            foreach (var heading in stageHeadings)
            {
                var (headerIndex, _) = MarkdownSectionLocator.LocateSection(lines, heading, "## ");
                if (headerIndex < 0)
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.StageMissing, heading, string.Empty,
                        $"명부의 단계 '{heading}'이 정책서에 없습니다."));
                    continue;
                }

                positions.Add((heading, headerIndex));
            }

            for (var i = 1; i < positions.Count; i++)
            {
                if (positions[i].Index < positions[i - 1].Index)
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.StageOutOfOrder, positions[i].Heading, string.Empty,
                        $"단계 '{positions[i].Heading}'이 명부 순서보다 앞에 있습니다."));
                }
            }

            // 2. 규칙별 귀속
            var sectionBodyCache = new Dictionary<(string Label, string Heading), string?>();

            foreach (var rule in PolicyDocumentParser.Parse(policyMarkdown, stageHeadings))
            {
                var expectedPrefix = PolicySectionContract.IdPrefixFor(rule.StageNumber) + "-";
                if (!rule.Id.StartsWith(expectedPrefix, StringComparison.Ordinal))
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.IdPrefixMismatch, rule.StageHeading, rule.Id,
                        $"'{rule.StageHeading}'의 규칙 ID는 '{expectedPrefix}'로 시작해야 합니다."));
                }

                if (!PolicySectionContract.TryParseEvidence(rule.EvidenceRaw, out var evidence))
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.EvidenceMissing, rule.StageHeading, rule.Id,
                        "근거 칸이 '<SP> · ## 헤딩 > \"원문 구절\"' 형식이 아닙니다."));
                    continue;
                }

                if (!specsByLabel.TryGetValue(evidence.Label, out var specMarkdown))
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.EvidenceLabelUnknown, rule.StageHeading, rule.Id,
                        $"근거로 인용한 '{evidence.Label}'의 명세서가 재료에 없습니다."));
                    continue;
                }

                var cacheKey = (evidence.Label, evidence.Heading);
                if (!sectionBodyCache.TryGetValue(cacheKey, out var body))
                {
                    body = EvidenceQuoteMatcher.ExtractSectionBody(
                        MarkdownSectionLocator.SplitLines(specMarkdown), evidence.Heading);
                    sectionBodyCache[cacheKey] = body;
                }

                if (body is null)
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.EvidenceHeadingNotFound, rule.StageHeading, rule.Id,
                        $"인용한 헤딩 '{evidence.Heading}'이 {evidence.Label}의 명세서에 없습니다."));
                    continue;
                }

                if (!EvidenceQuoteMatcher.QuoteExistsIn(body, evidence.Quote))
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.EvidenceQuoteNotFound, rule.StageHeading, rule.Id,
                        $"인용 구절 \"{evidence.Quote}\"을 {evidence.Label}의 '{evidence.Heading}' 절에서 찾을 수 없습니다."));
                }
            }

            return new PolicyValidationResult(defects);
        }
    }
}
