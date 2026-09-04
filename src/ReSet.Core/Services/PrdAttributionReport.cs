using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 귀속 검사 결과를 두 독자에게 옮긴다 - 교정 재호출을 받을 모델과, 문서를 읽을 사람.
    /// </summary>
    public static class PrdAttributionReport
    {
        /// <summary>
        /// 문서 상단 배너. 결함이 없어도 낸다.
        ///
        /// 기계가 확인한 것은 「인용이 실재하는가」까지이고 「요구와 근거가 대응하는가」는
        /// 확인하지 않았다. 그 경계를 적지 않으면 독자가 검사를 실제보다 강하게 믿는다.
        ///
        /// 「모든」이라 쓰지 않는다: PrdDocumentParser는 PrdSectionContract가 정한 다섯 헤딩
        /// 사이의 표만 읽는다. 모델이 그 계약 밖에 요구 표를 더 만들면 그 행은 파서도,
        /// 따라서 이 검사도 보지 못한다 — 배너는 「검사된」 항목만큼만 말할 수 있다.
        /// </summary>
        public static string BuildBanner(PrdValidationResult result)
        {
            var sb = new StringBuilder();

            if (result.IsValid)
            {
                sb.AppendLine("> [!NOTE]");
                sb.AppendLine("> **귀속 검사**: 검사된 요구 항목의 근거 인용이 원본 명세서에 **실재**함을 기계로 확인했습니다.");
            }
            else
            {
                sb.AppendLine("> [!CAUTION]");
                sb.AppendLine($"> **귀속 검사 미통과**: 아래 {result.Defects.Count}건의 결함이 남아 있습니다.");
                foreach (var defect in result.Defects)
                {
                    var subject = string.IsNullOrEmpty(defect.RequirementId)
                        ? defect.Section
                        : $"{defect.Section} / {SafeForMarkdownBullet(defect.RequirementId)}";
                    sb.AppendLine($"> - `{subject}` — {SafeForMarkdownBullet(defect.Message)}");
                }
            }

            sb.AppendLine("> ");
            sb.AppendLine("> 검증된 것은 근거 인용의 **실재**뿐입니다. 요구와 근거의 **대응**은 **미검증**이며,");
            sb.AppendLine("> `추정` 항목은 원본 명세서에 없는 재구성입니다. 이 문서는 L2/L3 검증 파이프라인을 거치지 않았습니다.");
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>교정 재호출에 실을 결함 목록. 통과했으면 빈 문자열이다.</summary>
        public static string BuildPromptFix(PrdValidationResult result)
        {
            if (result.IsValid)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[귀속 검사 피드백] 아래 결함을 모두 고쳐 문서 전체를 다시 출력하십시오.");
            sb.AppendLine("근거 칸은 반드시 `## <Spec 헤딩> > \"<원문 구절>\"` 형식이어야 하며, 인용 구절은 원본 명세서에서 글자 그대로 옮겨야 합니다.");
            sb.AppendLine();

            foreach (var group in result.Defects.GroupBy(d => d.Section))
            {
                sb.AppendLine($"### {group.Key}");
                foreach (var defect in group)
                {
                    var subject = string.IsNullOrEmpty(defect.RequirementId) ? "(섹션 전체)" : defect.RequirementId;
                    sb.AppendLine($"- {subject}: {defect.Message}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// 모델이 작성한 텍스트 내 마크다운 문법을 무효화한다.
        /// 섹션은 파서가 화이트리스트한 다섯 개 중 하나이므로 신뢰할 수 있어 제외한다.
        /// </summary>
        private static string SafeForMarkdownBullet(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            // 백틱은 코드 스팬을 닫고, 별표와 언더스코어는 강조를 활성화한다.
            // 대괄호는 링크 [text](url)와 이미지 ![alt](url) 구문을 이루는데, 괄호만으로는
            // 링크가 되지 않으므로 ( )는 제외한다. < 는 자동 링크 <url> 과 인라인 HTML 을
            // 활성화하지만, > 는 보통 텍스트에 포함되어도 무해하고 우리 메시지에도 이미
            // 포함되어 있으므로 제외한다.
            return text
                .Replace("`", "´")
                .Replace("*", "·")
                .Replace("_", "-")
                .Replace("[", "⟦")
                .Replace("]", "⟧")
                .Replace("<", "‹");
        }
    }
}
