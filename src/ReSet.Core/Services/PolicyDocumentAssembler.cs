using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 개요·단계 본문·부록을 하나의 문서로 잇는다.
    ///
    /// [왜 AI가 조립하지 않는가] 목차와 부록은 명부와 사전에서 기계적으로 나오는 것이다.
    /// AI에게 조립을 맡기면 그 과정에서 근거가 흐려지고, 목차가 매 회차 달라져
    /// 단계 완전성 검사의 기준이 흔들린다.
    /// </summary>
    public static class PolicyDocumentAssembler
    {
        public static string Assemble(
            string overview,
            IReadOnlyList<string> stageBodies,
            SettlementCodebook codebook,
            SettlementProcessRoster roster)
        {
            var sb = new StringBuilder();
            sb.AppendLine(overview.TrimEnd());
            sb.AppendLine();

            foreach (var body in stageBodies)
            {
                sb.AppendLine(body.TrimEnd());
                sb.AppendLine();
            }

            sb.AppendLine("## 부록 A. 코드값 사전");
            sb.AppendLine();
            sb.AppendLine("| 코드값 | 의미 | 출처 | 쓰이는 프로시저 |");
            sb.AppendLine("| :--- | :--- | :--- | :--- |");
            foreach (var entry in codebook.Entries)
            {
                var meaning = entry.Matches.Count > 0
                    ? MarkdownTableCellCodec.Escape(string.Join(
                        " / ", entry.Matches.Select(m => string.Join(", ", m.Row.Select(kv => $"{kv.Key}={kv.Value}")))))
                    : entry.MatchEligible
                        ? "의미 미상 (마스터 데이터에서 찾지 못함)"
                        : "의미 미상 (값이 짧아 판별 불가)";
                var source = entry.Matches.Count > 0
                    ? string.Join(", ", entry.Matches.Select(m => m.Table))
                    : "-";

                sb.AppendLine($"| `{entry.Value}` | {meaning} | {source} | {string.Join(", ", entry.Procedures)} |");
            }

            sb.AppendLine();
            sb.AppendLine("## 부록 B. 단계별 원본 프로시저");
            sb.AppendLine();
            sb.AppendLine("| 단계 | 원본 프로시저 |");
            sb.AppendLine("| :--- | :--- |");
            foreach (var stage in roster.Stages)
            {
                sb.AppendLine($"| {stage.Title} | {string.Join(", ", stage.Procedures)} |");
            }

            if (roster.Excluded.Count > 0)
            {
                sb.AppendLine($"| (제외) | {string.Join(", ", roster.Excluded)} |");
            }

            return sb.ToString();
        }
    }
}
