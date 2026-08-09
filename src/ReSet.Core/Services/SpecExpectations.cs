using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정적 분석이 확정한 사실 중 L1이 명세서 본문과 기계적으로 대조할 것들.
    ///
    /// MechanicalValidator에 두지 않는 이유: 기대값 <b>생성</b>은 정적 분석을 읽는
    /// 일이고 <b>소비</b>는 검증기의 일이다. 나눠 두면 검증기가 SpStaticAnalysisResult를
    /// 몰라도 된다.
    /// </summary>
    public sealed record SpecExpectations(IReadOnlyList<UpdateColumnExpectation> UpdateColumns)
    {
        /// <summary>
        /// 대조할 것이 없으면 null을 돌려준다. 호출부가 null 검사를 하지 않고 그대로
        /// 넘길 수 있게 하기 위해서다 - Validate는 null을 "종전 동작"으로 받는다.
        ///
        /// 테이블 단위로 접는다. 대조가 테이블 합집합이므로 기대도 같은 단위여야 한다.
        /// </summary>
        public static SpecExpectations? FromStaticAnalysis(SpStaticAnalysisResult? analysis)
        {
            if (analysis == null || analysis.AstUpdateMappings.Count == 0) return null;

            var byTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in analysis.AstUpdateMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.TargetTable)) continue;

                if (!byTable.TryGetValue(mapping.TargetTable, out var columns))
                {
                    columns = new List<string>();
                    byTable[mapping.TargetTable] = columns;
                }

                foreach (var assignment in mapping.Assignments)
                {
                    if (string.IsNullOrWhiteSpace(assignment.Column)) continue;
                    if (columns.Contains(assignment.Column, StringComparer.OrdinalIgnoreCase)) continue;
                    columns.Add(assignment.Column);
                }
            }

            var expectations = byTable
                .Where(kvp => kvp.Value.Count > 0)
                .Select(kvp => new UpdateColumnExpectation(kvp.Key, kvp.Value))
                .ToList();

            return expectations.Count == 0 ? null : new SpecExpectations(expectations);
        }
    }

    /// <summary>한 테이블에 대해 명세서의 UPDATE 매핑 표에 반드시 있어야 하는 컬럼들.</summary>
    public sealed record UpdateColumnExpectation(string Table, IReadOnlyList<string> Columns);
}
