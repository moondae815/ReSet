using System;
using System.Collections.Generic;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// L1 하한 검사가 쓰는 「원본이 반환하는 오류코드」 재료를 만든다 -
    /// 명세서 산문(<see cref="SpecReturnCodeExtractor"/>)과 원본 DDL의 AST
    /// (<see cref="DmlScopeExtractor.ExtractErrorCodes"/>)의 <b>합집합</b>이다.
    ///
    /// [왜 있는가 - 2026-08-31 POQSettleBatch5 대조 실행] 재료가 산문 하나뿐이던
    /// 동안 S16이 원본에 실재하는 <c>-5</c>~<c>-8</c>을 "발명했다"고 고발당했다.
    /// 그 명세서의 반환 코드 표는 여덟 개를 다 갖고 있었고 산문만 네 개를
    /// <c>@po_intRetVal =</c> 꼴로 적었다 - 즉 오라클이 모델이 쓴 산문 표현의
    /// 흔들림에 걸려 있었다. 같은 명세서를 다섯 번 생성해 두 번 떨어뜨렸으므로
    /// 구조적 결함이 아니라 비결정적으로 재발하는 종류다.
    ///
    /// [왜 교체가 아니라 합집합인가 - 코퍼스 120쌍 실측] 산출물 트리 5 × 명세서 24를
    /// 두 추출기로 대조했다.
    /// <list type="bullet">
    /// <item>DDL에는 있는데 산문이 떨어뜨린 것: 1편 2쌍(<c>UP_Util_Settle_Summary</c>).</item>
    /// <item>산문에만 있고 DDL의 DML에는 없는 것: <b>6편 29쌍</b>(<c>-9</c>, <c>4000</c>,
    /// <c>-3</c>, <c>-15</c>). <c>ExtractErrorCodes</c>는 DML 문장 <b>뒤에 붙은</b>
    /// 가드만 기록하므로(<c>RecordErrorCode</c>) 진입부 파라미터 검사나 CATCH 블록의
    /// 코드는 원리적으로 못 낸다.</item>
    /// </list>
    /// 재료를 DDL로 <b>교체</b>하면 그 29쌍이 전부 새 오탐이 된다 - 고치려던 것보다
    /// 14배 나쁘다. 두 추출기는 서로의 사각을 덮으므로 합집합만이 답이다.
    ///
    /// [무엇을 고치지 않는가] 명세서가 같은 사실을 두 문장 표현으로 적는 것 자체는
    /// 그대로 둔다. 합집합이 L1을 그 흔들림에서 막으므로 급하지 않다.
    /// </summary>
    public static class ErrorCodeMaterial
    {
        /// <param name="specCodes">
        /// <see cref="SpecReturnCodeExtractor.Extract"/>의 결과. null이면 DDL 몫만 남는다.
        /// </param>
        /// <param name="definitions">
        /// 원본 정의. <c>RunConsolidatedPipelineAsync</c>에서 선택 인자라 null일 수 있고,
        /// 그때는 산문 재료가 그대로 나온다 - 조용히 재료를 잃지 않는다.
        /// </param>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> Merge(
            IReadOnlyDictionary<string, IReadOnlyList<string>>? specCodes,
            IReadOnlyList<SpDefinition>? definitions)
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            if (specCodes != null)
            {
                foreach (var (key, codes) in specCodes)
                {
                    result[key] = new List<string>(codes);
                }
            }

            if (definitions == null)
            {
                return result;
            }

            foreach (var def in definitions)
            {
                if (def == null || string.IsNullOrWhiteSpace(def.DdlText) || string.IsNullOrWhiteSpace(def.Name))
                {
                    continue;
                }

                // 기준일 파라미터는 SpecExpectations.From·AiService와 같은 규칙으로 고른다 -
                // 세 곳이 갈리면 같은 DDL에서 다른 표가 나온다(AiService.cs의 같은 호출 주석).
                var facts = DmlScopeExtractor.ExtractErrorCodes(
                    def.DdlText,
                    SpecExpectations.ResolveDateParameter(def.StaticAnalysis));

                var fromDdl = new List<string>();
                foreach (var fact in facts)
                {
                    // 반환 변수에 넣는 값만 재료다. 가드가 지역 변수에 담는 값까지
                    // 끌어오면, 검사가 반환 코드가 아닌 것을 문서에 요구한다.
                    if (!string.Equals(fact.Variable, SpecReturnCodeExtractor.ReturnVariableName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!fromDdl.Contains(fact.Code, StringComparer.Ordinal))
                    {
                        fromDdl.Add(fact.Code);
                    }
                }

                if (fromDdl.Count == 0)
                {
                    continue;
                }

                var key = SpecReturnCodeExtractor.BareName(def.Name);

                if (!result.TryGetValue(key, out var existing))
                {
                    // 산문이 이 프로시저에 아무 코드도 못 냈던 자리다. 여기서 키가
                    // 처음 생기는 것이 이 합집합의 핵심이다 - 그전에는 재료 없음으로
                    // 검사가 통째로 침묵했다(CheckLegacyStepErrorCodeInvention의 hasMaterial).
                    result[key] = fromDdl;
                    continue;
                }

                // 빈 목록은 만들지 않는다 - SpecReturnCodeExtractor.Extract가 지키는
                // 계약과 같다("빈 목록"과 "그런 프로시저 없음"이 같아지면 안 된다).
                var merged = new List<string>(existing);
                foreach (var code in fromDdl)
                {
                    if (!merged.Contains(code, StringComparer.Ordinal))
                    {
                        merged.Add(code);
                    }
                }

                result[key] = merged;
            }

            return result;
        }
    }
}
