using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// `settlement-process.md`를 모델로 읽는다.
    ///
    /// H2가 단계, 그 아래 `- ` 목록이 소속 프로시저, 순서는 등장 순서다.
    /// HTML 주석(`&lt;!--`)은 초안이 근거를 남기는 자리이므로 항목으로 읽지 않는다.
    /// 섹션 경계 판정은 MarkdownSectionLocator의 관행(H2 접두사)을 따르되, 이 문서는
    /// 코드 펜스를 쓰지 않으므로 펜스 계산은 하지 않는다.
    /// </summary>
    public static class SettlementProcessRosterParser
    {
        public static SettlementProcessRoster Parse(string? markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return SettlementProcessRoster.Empty;
            }

            var stages = new List<PolicyStage>();
            var excluded = new List<string>();

            string? currentTitle = null;
            var currentItems = new List<string>();
            var inExcluded = false;

            void Flush()
            {
                if (inExcluded)
                {
                    excluded.AddRange(currentItems);
                }
                else if (currentTitle is not null)
                {
                    stages.Add(new PolicyStage(currentTitle, currentItems.ToList()));
                }

                currentItems.Clear();
            }

            foreach (var raw in MarkdownSectionLocator.SplitLines(markdown))
            {
                var line = raw.Trim();

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    Flush();
                    inExcluded = line.Equals(SettlementProcessRoster.ExcludedHeading, StringComparison.Ordinal);
                    currentTitle = inExcluded ? null : line[3..].Trim();
                    continue;
                }

                if (!line.StartsWith("- ", StringComparison.Ordinal))
                {
                    continue;
                }

                var item = line[2..].Trim();
                if (item.Length == 0 || item.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                currentItems.Add(item);
            }

            Flush();

            return new SettlementProcessRoster(stages, excluded);
        }
    }
}
