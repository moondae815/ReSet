using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정책서 고유의 검사 둘 - 코드값 대조와 SP 인용 커버리지.
    ///
    /// [왜 귀속 검사와 나눠 두는가] 귀속 검사의 오라클은 명세서 텍스트이고, 이 둘의
    /// 오라클은 각각 코드값 사전과 명부다. 재료가 다른 검사를 한 클래스에 넣으면
    /// 인자 목록이 부풀고 어느 검사가 어느 재료를 쓰는지 흐려진다 -
    /// PrdAttributionValidator가 MechanicalValidator에 들어가지 않은 것과 같은 판단이다.
    /// </summary>
    public static class PolicyDocumentChecks
    {
        /// <summary>
        /// 문서에 실린 코드값이 사전에 있는지 본다.
        ///
        /// [비순환 오라클] 검사가 보는 파일은 정책서이고 기준이 되는 파일은
        /// settlement-codebook.json이다. AI 출력을 기계 산출물로 재므로 순환하지 않는다.
        /// </summary>
        public static IReadOnlyList<PolicyDefect> CheckCodeValues(
            IReadOnlyList<PolicyRule> rules, SettlementCodebook codebook)
        {
            var known = codebook.Entries
                .Select(e => e.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var defects = new List<PolicyDefect>();

            foreach (var rule in rules)
            {
                var unknown = SplitCodeValues(rule.CodeValue)
                    .Where(v => !known.Contains(v))
                    .ToList();

                if (unknown.Count > 0)
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.CodeValueNotInCodebook,
                        rule.StageHeading,
                        rule.Id,
                        $"코드값 {string.Join(", ", unknown.Select(v => $"'{v}'"))}이 코드값 사전에 없습니다. 사전에 있는 값만 실을 수 있습니다."));
                }
            }

            return defects;
        }

        /// <summary>
        /// 명부에 실린 SP가 최소 한 번은 규칙의 근거로 인용됐는지 본다.
        ///
        /// [왜 필요한가] 명부 대조(SettlementRosterReconciler)는 「명부에 있는가」만 본다.
        /// 명부에 있어도 AI가 그 명세서를 읽지 않고 지나가면 그 SP의 규칙이 통째로
        /// 빠지는데 문서는 멀쩡해 보인다. 조용한 누락의 두 번째 문이다.
        /// </summary>
        public static IReadOnlyList<PolicyDefect> CheckProcedureCitationCoverage(
            IReadOnlyList<PolicyRule> rules, SettlementProcessRoster roster)
        {
            var cited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                if (PolicySectionContract.TryParseEvidence(rule.EvidenceRaw, out var evidence))
                {
                    cited.Add(evidence.Label);
                }
            }

            return roster.AllStagedProcedures()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !cited.Contains(p))
                .Select(p => new PolicyDefect(
                    PolicyDefectType.ProcedureNeverCited,
                    p,
                    string.Empty,
                    $"'{p}'의 명세서가 어떤 규칙의 근거로도 인용되지 않았습니다. 그 SP의 업무 규칙이 문서에서 빠졌을 수 있습니다."))
                .ToList();
        }

        /// <summary>한 칸에 쉼표로 여러 코드값이 적힐 수 있다. '-'는 해당 없음 표기이므로 뺀다.</summary>
        private static IEnumerable<string> SplitCodeValues(string? cell) =>
            (cell ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ResolveValue)
                .Where(v => v != PolicySectionContract.NoCodeValue);

        private static readonly char[] WrapChars = { '`', '\'', '"' };

        /// <summary>
        /// 조각 하나를 대조에 넘길 최종 값으로 정한다.
        ///
        /// [왜 벗긴 결과가 비면 원본으로 되돌리는가] 벗기기는 표기를 걷어내는 것이지
        /// 값을 없애는 것이 아니다. 벗겨서 빈 문자열이 되면 그것은 감싼 표기만 있고
        /// 값이 없는 것이므로, 원본 조각 그대로 대조에 넘겨 고발되게 한다.
        /// 2026-09-06 에 벗기기를 넣으면서 이 자리가 조용히 사라졌었다 - 빈 감쌈 쌍
        /// (``)이 벗기면 빈 문자열이 되고, 그 뒤에 있던 「빈 조각이면 버린다」 필터가
        /// 값이 아니라 이 malformed 항목까지 함께 삼켰다. 쉼표 뒤 빈 조각(끝 쉼표 등)은
        /// Split의 RemoveEmptyEntries가 이미 걸러 raw 자체가 원천적으로 비어 있지 않으므로,
        /// 이 함수에 들어오는 raw는 항상 비어 있지 않다 - 「값이 아예 없던 자리」와
        /// 「감쌈만 있고 알맹이가 없는 자리」가 여기서 갈린다.
        /// </summary>
        private static string ResolveValue(string raw)
        {
            var unwrapped = Unwrap(raw);
            return unwrapped.Length > 0 ? unwrapped : raw;
        }

        /// <summary>
        /// 값을 감싼 백틱·따옴표 한 겹을 벗긴다.
        ///
        /// [왜 벗기는가] T9 프롬프트가 코드값을 백틱으로 감싸 verbatim 을 요구한다.
        /// 그 표기 자체를 오탐으로 고발하면 교정 재호출 피드백이 모델에게 실행
        /// 불가능한 지시가 된다.
        ///
        /// [왜 바깥 짝은 엄격한데 안쪽 공백은 봐주는가] 짝이 안 맞는 것(한쪽에만
        /// 감싼 문자가 있는 것)은 모델이 형식을 잘못 쓴 신호이므로 조용히 고쳐 주지
        /// 않고 그대로 둬 사전 대조가 잡게 한다 - 엄격함이 필요한 자리다. 반면 감싼
        /// 안쪽의 앞뒤 공백(`` ` impaymobile ` ``)은 값의 의미를 바꾸지 않는 표기
        /// 흔들림이라 Trim으로 봐준다 - 관용해도 잃는 신호가 없는 자리다. 값 안쪽의
        /// 백틱·따옴표 자체는 건드리지 않는다 - 벗기는 것은 바깥 한 겹뿐이다.
        /// </summary>
        private static string Unwrap(string value)
        {
            if (value.Length >= 2 && value[0] == value[^1] && WrapChars.Contains(value[0]))
            {
                return value[1..^1].Trim();
            }

            return value;
        }
    }
}
