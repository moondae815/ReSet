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

        public static PrdValidationResult Validate(string? prdMarkdown, string? specMarkdown)
        {
            var defects = new List<PrdDefect>();
            var prdLines = MarkdownSectionLocator.SplitLines(prdMarkdown);

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
            }

            return new PrdValidationResult(defects);
        }
    }
}
