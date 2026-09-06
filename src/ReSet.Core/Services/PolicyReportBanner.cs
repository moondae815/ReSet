using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 문서 상단의 배너 셋을 만든다 - 귀속 결함, 코드값 커버리지, 미인용 SP.
    ///
    /// [순서 주의] 이 배너는 FormatUnverifiedDocument의 body 인자로 들어가야 한다.
    /// 반환값 앞에 이어붙이면 YAML 프런트매터가 오프셋 0을 잃어 가로줄로 렌더링된다
    /// (PrdDerivationService에 같은 주석이 있다).
    /// </summary>
    public static class PolicyReportBanner
    {
        public static string Build(
            IReadOnlyList<PolicyDefect> defects,
            int translated,
            int unmatched,
            int skippedShort,
            bool profilingRan)
        {
            var sb = new StringBuilder();

            var attribution = defects
                .Where(d => d.Type != PolicyDefectType.ProcedureNeverCited)
                .ToList();

            if (attribution.Count > 0)
            {
                sb.AppendLine("> [!WARNING]");
                sb.AppendLine($"> **[귀속 검사 미통과] {attribution.Count}건** — 아래 자리는 근거가 원본 명세서에서 확인되지 않았습니다.");
                foreach (var defect in attribution.Take(20))
                {
                    sb.AppendLine($"> - [{defect.RuleId}] {defect.Message}");
                }

                if (attribution.Count > 20)
                {
                    sb.AppendLine($"> - (외 {attribution.Count - 20}건)");
                }

                sb.AppendLine();
            }

            sb.AppendLine("> [!NOTE]");
            sb.AppendLine(profilingRan
                ? $"> **코드값 번역**: 매칭 대상 {translated + unmatched}개 중 {translated}개 번역 · {unmatched}개 의미 미상 (값이 짧아 대상에서 제외한 것 {skippedShort}개)"
                : $"> **코드값 번역**: 0건 (DB 미연결) — 코드 상수 {translated + unmatched}개의 업무 의미가 비어 있습니다. (값이 짧아 대상에서 제외한 것 {skippedShort}개)");
            sb.AppendLine();

            var neverCited = defects
                .Where(d => d.Type == PolicyDefectType.ProcedureNeverCited)
                .ToList();

            if (neverCited.Count > 0)
            {
                sb.AppendLine("> [!WARNING]");
                sb.AppendLine($"> **[미인용 프로시저] {neverCited.Count}건** — 명부에 실렸으나 어떤 규칙의 근거로도 인용되지 않았습니다. 그 업무 규칙이 문서에서 빠졌을 수 있습니다.");
                foreach (var defect in neverCited)
                {
                    sb.AppendLine($"> - {defect.Subject}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
