using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 명세서 본문에서 원본 프로시저의 반환 오류코드를 뽑는다.
    ///
    /// 이 추출기가 존재하는 이유: 목차(PlanStructure)의 ErrorCodes는 AI가 채우는데
    /// 실측 두 회차에서 26개 단계 중 25개가 빈 배열이었다. 하한 검사는 그 배열을
    /// foreach로 돌므로 0회 반복하고 통과했다 - 12/12, 13/14 단계의 오류코드 검증이
    /// 무실행인 채 "에러 개수: 0개"로 기록됐다.
    ///
    /// 코드는 문서에서 사라진 적이 없다. 같은 단계 본문이 코드를 산문으로 다 적고
    /// 있었다(S06은 배열이 비었는데 본문에 16개). 모델이 못 쓰는 것이 아니라
    /// 기계 판독 배열만 비운다. 그래서 AI에게 다시 시키는 대신 명세서에서 뽑는다.
    ///
    /// 변수명을 <c>@po_intRetVal</c>로 고정하는 이유는 좁히기 위해서가 아니라
    /// 노이즈를 배제하기 위해서다. 명세서 본문에는 "취소거래의 금액을 -1배
    /// 처리합니다" 같은 서술과 날짜(2026-08-05)의 음수가 흔해, 일반 음수 패턴으로
    /// 훑으면 그 전부를 코드로 오인한다. 실측 명세서 14종에서 반환 변수는 이
    /// 이름 하나뿐이다(247회).
    /// </summary>
    public static class SpecReturnCodeExtractor
    {
        private static readonly Regex ReturnAssignmentRegex = new(
            @"@po_intRetVal\s*=\s*(?<code>-?\d+)",
            RegexOptions.Compiled);

        public static IReadOnlyDictionary<string, IReadOnlyList<string>> Extract(
            IEnumerable<(string FileName, string Content)> specs)
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (specs == null)
            {
                return result;
            }

            foreach (var (fileName, content) in specs)
            {
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var codes = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match match in ReturnAssignmentRegex.Matches(content))
                {
                    var code = match.Groups["code"].Value;
                    if (seen.Add(code))
                    {
                        codes.Add(code);
                    }
                }

                // 매치가 없으면 키를 만들지 않는다. 빈 목록과 "그런 프로시저 없음"이
                // 같아지면, 보강기가 "명세서에 코드가 없는 프로시저"로 오해한다.
                if (codes.Count == 0)
                {
                    continue;
                }

                var key = BareName(fileName);

                // 같은 이름이 두 번 들어오면 덮어쓰지 않고 합친다. 덮어쓰면
                // 앞 항목의 코드가 조용히 사라진다.
                if (result.TryGetValue(key, out var existing))
                {
                    var merged = new List<string>(existing);
                    foreach (var code in codes)
                    {
                        if (!merged.Contains(code, StringComparer.Ordinal))
                        {
                            merged.Add(code);
                        }
                    }

                    result[key] = merged;
                    continue;
                }

                result[key] = codes;
            }

            return result;
        }

        /// <summary>
        /// 명세서 파일명("dbo.UP_X", "SETTLE_CARD_DB.dbo.UP_X")과 목차의
        /// LegacyProcedures("UP_X", "dbo.UP_X")를 같은 키로 만든다. 두 표기의
        /// 조각 수가 다르므로 마지막 점 뒤만 본다 - 기존 SpecPathForStep이
        /// 쓰는 규칙과 같다.
        /// </summary>
        public static string BareName(string procedureOrFileName)
        {
            var index = procedureOrFileName.LastIndexOf('.');
            var bare = index >= 0 ? procedureOrFileName[(index + 1)..] : procedureOrFileName;
            return bare.Trim().ToLowerInvariant();
        }
    }
}
