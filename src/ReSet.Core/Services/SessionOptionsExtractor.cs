using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 프로시저 본문의 세션 옵션을 뽑는다.
    ///
    /// AS 이후의 것만 담는다. CREATE 배치 앞머리의 SET ANSI_NULLS ON 같은 것은
    /// 관례적 노이즈이지 이 SP의 로직이 아니다 - 담으면 모든 명세서가 같은
    /// 결함을 하나씩 갖게 되고, 그러면 이 검사를 아무도 믿지 않는다.
    ///
    /// Util_Settle_Summary의 SET NOCOUNT ON이 AS 직후 BEGIN TRAN 앞에 있는데
    /// 명세서 전체에 언급이 없었던 것이 이 재료가 있는 이유다.
    /// </summary>
    public static class SessionOptionsExtractor
    {
        private static readonly Regex CreateBodyStartRegex = new(
            @"\bCREATE\s+(?:OR\s+ALTER\s+)?PROC(?:EDURE)?\b.*?\bAS\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex SetOptionRegex = new(
            @"^\s*SET\s+(?<option>NOCOUNT|XACT_ABORT|ARITHABORT|ANSI_WARNINGS|ANSI_NULLS|"
            + @"QUOTED_IDENTIFIER|CONCAT_NULL_YIELDS_NULL|TRANSACTION\s+ISOLATION\s+LEVEL)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static IReadOnlyList<string> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<string>();

            var bodyStart = CreateBodyStartRegex.Match(ddlText);
            if (!bodyStart.Success) return Array.Empty<string>();

            var body = ddlText[(bodyStart.Index + bodyStart.Length)..];

            var options = new List<string>();
            foreach (Match match in SetOptionRegex.Matches(body))
            {
                var option = Regex.Replace(match.Groups["option"].Value, @"\s+", " ").ToUpperInvariant();
                if (!options.Contains(option, StringComparer.Ordinal)) options.Add(option);
            }

            return options;
        }
    }
}
