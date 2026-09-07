using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <param name="CallName">`execute`·`queryScalar`·`queryRow`·`queryList` 중 하나.</param>
    /// <param name="StatementName">첫 인자로 준 SQL 문장 이름(`SQL_UPDATE_13`).</param>
    /// <param name="Key">바인딩 키(`p_v_valIncVat`). **검사는 이 값을 판정에 쓰지 않는다** -
    /// 모델이 개명하기 때문이다. 오류 메시지에만 싣는다.</param>
    /// <param name="Value">바인딩 값 원문(`1.1`·`batchYmd`).</param>
    public sealed record StepBindingFact(string CallName, string StatementName, string Key, string Value);

    /// <summary>
    /// 단계 본문의 의사코드 펜스에서 `execute(SQL_X, { k: v, … })` 꼴의 바인딩을 전수 뽑는다.
    ///
    /// [왜 SQL 펜스를 안 보는가] SQL 주석 안의 `execute(` 문자열에 반응하면 검사가
    /// 주석을 고발한다. 바인딩이라는 사실은 의사코드 층에만 있다.
    ///
    /// [왜 값만 쓰고 키는 안 쓰는가] 이 저장소 실측: `Batch5/S07`이
    /// `@v_valIncVat`를 `p_incVat`으로 **개명**했다. 키(이름)로 판정하면 세 자리 중
    /// 둘을 놓친다. 키는 오류 메시지의 가독성에만 쓴다.
    ///
    /// [입력의 관할 - 단계 섹션 하나] 이 추출기는 **단계 본문 하나**를 받는다. 넓히지 마라.
    /// 오탐 0 을 사 주는 것은 펜스 한정이 아니라 **섹션 한정**이다: 조립본
    /// (`BatchMigrationPlan.md`)에서 단계 섹션 **밖**의 `execute(` 14 개 중 **12 는 이미
    /// `pseudocode` 펜스 안**(공통 규약 절의 예시 의사코드)이라 펜스로 한정해도 그대로 걸린다.
    /// 그 12 가 오늘 안 우는 것은 소수 리터럴이 0/14 라는 **값의 우연**이지 계약이 아니다.
    /// (측정: `docs/audit-reports/2026-09-07-지역변수-타입계약-도달가능성.md` §3-3·§4)
    /// </summary>
    public static class StepBindingExtractor
    {
        /// <summary>
        /// 훑을 호출 형태.
        ///
        /// **[이 목록은 열거이지 도출이 아니다 - 「전량」이라고 닫지 마라]**
        /// 초판은 `execute`·`queryScalar`·`queryRow`·`queryList` 넷만 두고 「단계 본문의
        /// 호출 492 개가 **전량 이 넷**」이라고 적었다. **그 문장은 거짓이었고, 거짓인
        /// 이유는 재는 자에 있었다** - 네 이름으로 세는 자로는 「다른 이름이 바인딩
        /// 객체를 지고 있는가」라는 물음 자체를 던질 수 없다. 안 잰 것을 없다고 적은 것이다.
        ///
        /// 그래서 **이름을 가정하지 않는 자**로 다시 쟀다(2026-09-07, 단계 본문 386 편):
        /// 「식별자 `(` + 첫 인자 식별자 + 쉼표 + 최상위 중괄호」로만 훑는다. 결과는
        /// 바인딩 객체를 진 호출 **493** 이고, 그중 **14 가 초판의 넷 밖**이었다 -
        /// `chunkRanges` 7 · `query` 6 · `queryAll` 1, 그 안에 사실 **22 개**.
        /// (호출을 인자 모양과 무관하게 세면 506 = 네 이름 492 + 이 14.)
        /// 그 14 를 여기에 더해 사실이 872 → **894** 가 되었다.
        ///
        /// **여덟이 전량이라는 보증은 지금도 없다.** 위 실측은 「오늘 코퍼스에 이 여덟만
        /// 있다」는 관측이지 「이 여덟뿐이어야 한다」는 도출이 아니다. 아홉 번째 이름이
        /// 나오면 894 는 하한이다. **다음 사람은 이 주석을 근거로 관할을 닫지 마라 -**
        /// 닫으려면 위와 같이 **이름을 가정하지 않는 자로 다시 재라.**
        ///
        /// (덧: 이름을 더해도 소수 리터럴 사실은 **여전히 정확히 3** 이라 오늘의 발화는
        /// 안 바뀐다. 더한 22 사실에 소수 리터럴이 없기 때문이다.)
        /// </summary>
        private static readonly string[] Calls =
        {
            "execute", "queryScalar", "queryRow", "queryList",
            "chunkRanges", "query", "queryAll",
        };

        /// <summary>
        /// 펜스 하나. 여는 태그는 알파벳만이고 **빈 태그도 받는다**(`[a-zA-Z]*`) - 오늘
        /// 코퍼스의 빈 태그 펜스는 0 이지만, 모델이 태그를 빠뜨린 펜스에 바인딩을 적는 날
        /// 검사가 통째로 눈이 멀면 안 된다.
        /// </summary>
        private static readonly Regex FencePattern = new(
            @"```(?<lang>[a-zA-Z]*)\r?\n(?<body>.*?)```", RegexOptions.Singleline);

        /// <summary>
        /// `execute(SQL_X, { … }` 의 호출 머리와 중괄호 객체.
        ///
        /// **`[^}]*` 의 `^}` 는 개행을 배제하지 않는다 - 그것이 요점이다.** 실측(2026-09-07,
        /// 단계 본문 386 편): 중괄호가 **여러 줄에 걸친 호출이 20 자리**이고 그 안에 사실
        /// **157 개**가 산다(전체 872 의 18%). 여기에 `\n` 을 더해 줄 단위로 좁히면 검사가
        /// 그 157 에 눈이 먼다 - `Extract_ReadsBindingsSpanningSeveralLines` 가 잠근다.
        ///
        /// 탐욕적으로(`[\s\S]*`) 물어도 안 된다. 빈 바인딩 객체(`queryScalar(SQL_X, {})`,
        /// 실측 6 자리) 뒤의 호출을 통째로 삼켜 남의 값을 남의 호출에 귀속시킨다 -
        /// `Extract_OnEmptyBindingObject_YieldsNothingAndKeepsReadingLaterCalls` 가 잠근다.
        /// </summary>
        private static readonly Regex CallPattern = new(
            $@"\b(?<call>{string.Join("|", Calls)})\(\s*(?<stmt>[A-Za-z_0-9]+)\s*,\s*\{{(?<args>[^}}]*)\}}");

        /// <summary>
        /// 중괄호 객체 안의 `key: value` 한 쌍. 값은 쉼표나 닫는 중괄호에서 끊는다.
        /// </summary>
        private static readonly Regex ArgumentPattern = new(
            @"(?<key>[A-Za-z_0-9]+)\s*:\s*(?<value>[^,}]+)");

        public static IReadOnlyList<StepBindingFact> Extract(string? stepMarkdown)
        {
            if (string.IsNullOrWhiteSpace(stepMarkdown)) return Array.Empty<StepBindingFact>();

            var facts = new List<StepBindingFact>();

            foreach (Match fence in FencePattern.Matches(stepMarkdown))
            {
                var lang = fence.Groups["lang"].Value;
                // sql 펜스는 제외한다. 나머지(pseudocode·빈 태그·기타)는 다 본다.
                if (string.Equals(lang, "sql", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (Match call in CallPattern.Matches(fence.Groups["body"].Value))
                {
                    var callName = call.Groups["call"].Value;
                    var statement = call.Groups["stmt"].Value;

                    foreach (Match arg in ArgumentPattern.Matches(call.Groups["args"].Value))
                    {
                        facts.Add(new StepBindingFact(
                            callName,
                            statement,
                            arg.Groups["key"].Value.Trim(),
                            arg.Groups["value"].Value.Trim()));
                    }
                }
            }

            return facts;
        }
    }
}
