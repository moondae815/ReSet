using System;
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 조인 등식을 <c>테이블.컬럼=테이블.컬럼</c> 한 문자열로 정규화한다.
    ///
    /// [왜 한 자리에 두는가] 원본 쪽(<see cref="DmlScopeExtractor"/>)과 이행 쪽
    /// (<see cref="StepSqlStatementReader"/>)이 <b>같은 규칙</b>으로 정규화해야
    /// 대조가 성립한다. 사본을 두면 한쪽만 개명·수정되어 규칙이 조용히 갈린다 -
    /// <c>BareObjectName</c>·<c>ResolveOrdinal</c>·<c>TableHeaderCells</c>가 같은
    /// 이유로 공유된다.
    ///
    /// **수집 범위는 공유하지 않는다.** 원본은 넓게(<c>ON</c> ∪ <c>WHERE</c>),
    /// 이행은 좁게(최상위 <c>ON</c>) 모은다 - 그 비대칭이 설계다
    /// (docs/superpowers/specs/2026-09-05-n5-join-pair-design.md §2-2).
    /// 여기서 공유하는 것은 <b>모은 뒤 어떻게 적는가</b>뿐이다.
    /// </summary>
    internal static class JoinPairNormalizer
    {
        /// <summary>
        /// 별칭을 테이블로 풀어 정규화한다. 한쪽이라도 못 풀면 <c>null</c>이다.
        ///
        /// [못 풀면 버리는 이유] 파생 테이블·CTE 별칭은 물리 테이블이 아니다. 별칭
        /// 문자를 테이블인 척 적으면 <b>없는 사실</b>이 생기고, 그 사실이 「이행이
        /// 원본에 없는 짝을 더했다」는 발화의 근거가 된다 - 이 검사에서 가장 비싼
        /// 오탐이다. 놓치는 쪽이 안전한 기본값이라는 것은
        /// <c>HaveDifferentQualifiers</c>가 같은 자리에서 이미 택한 원칙이다.
        ///
        /// [왜 두 변을 정렬하는가] 조인은 방향이 없다. <c>A.X = B.Y</c>와
        /// <c>B.Y = A.X</c>는 같은 사실이므로 같은 문자열이 되어야 한다.
        /// 정렬은 <b>대소문자를 무시</b>한다 - 원본이 <c>B.ClientID</c>로, 이행이
        /// <c>A.CLIENTID</c>로 적어도 같은 순서가 서야 대조가 성립한다.
        /// </summary>
        internal static string? Normalize(
            IReadOnlyDictionary<string, string> aliasToTable,
            string? leftQualifier,
            string? leftColumn,
            string? rightQualifier,
            string? rightColumn)
        {
            if (string.IsNullOrWhiteSpace(leftQualifier) || string.IsNullOrWhiteSpace(leftColumn)) return null;
            if (string.IsNullOrWhiteSpace(rightQualifier) || string.IsNullOrWhiteSpace(rightColumn)) return null;

            if (!aliasToTable.TryGetValue(leftQualifier!, out var leftTable)) return null;
            if (!aliasToTable.TryGetValue(rightQualifier!, out var rightTable)) return null;

            var left = $"{leftTable}.{leftColumn}";
            var right = $"{rightTable}.{rightColumn}";

            return StringComparer.OrdinalIgnoreCase.Compare(left, right) <= 0
                ? $"{left}={right}"
                : $"{right}={left}";
        }

        /// <summary>
        /// 이미 정규화된 짝을 목록에 더한다 - 중복은 넣지 않는다.
        /// 대조가 대소문자를 무시하므로 중복 판정도 무시해야 한다.
        /// </summary>
        internal static void AddDistinct(List<string> pairs, string? pair)
        {
            if (pair == null) return;
            if (pairs.Contains(pair, StringComparer.OrdinalIgnoreCase)) return;
            pairs.Add(pair);
        }
    }
}
