using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정적 분석과 스키마 메타데이터가 확정한 사실 중 L1이 명세서 본문과 기계적으로
    /// 대조할 것들.
    ///
    /// MechanicalValidator에 두지 않는 이유: 기대값 <b>생성</b>은 정적 분석과 의존성을
    /// 읽는 일이고 <b>소비</b>는 검증기의 일이다. 나눠 두면 검증기가 SpDefinition을
    /// 몰라도 된다.
    /// </summary>
    /// <param name="UpdateColumns">정적 파서가 확정한 UPDATE SET 대상 컬럼.</param>
    /// <param name="PromptSchemaColumns">
    /// 테이블별로 프롬프트 스키마 표에 실제로 실린 컬럼. 키는 canonical 3-part 이름이다.
    /// 이것이 거짓 부재 주장 대조의 기준이다 - DB 전체 컬럼이 아니다. 정당하게 필터에서
    /// 빠진 컬럼을 기준으로 삼으면 재생성으로 고칠 수 없는 오류가 생긴다.
    /// </param>
    /// <param name="ColumnlessDependencyTables">
    /// 컬럼이 0개라 PromptSchemaColumns에서 제외된 의존성들의 canonical 이름.
    /// 대조 기준(PromptSchemaColumns)에는 넣지 않는다 - 스키마 표 자체가 렌더링되지
    /// 않으므로 이 테이블에 대한 "제공되지 않았습니다" 진술은 참이다. 그러나
    /// MechanicalValidator.ResolveSchemaTableKey의 말단 이름 모호성 판정에는 넣어야
    /// 한다 - 그렇지 않으면 컬럼 0개 테이블을 가리킨 문장·표 행이, 같은 말단 이름을
    /// 가진 컬럼 있는 동명 테이블로 조용히 오귀속된다(리뷰 실측: DB1.dbo.TSettleMst와
    /// DB2.dbo.TSettleMst 중 DB2만 메타데이터가 수집되지 않은 경우).
    /// </param>
    /// <param name="InputDefects">
    /// 프롬프트가 진실을 담지 못한 경우의 서술. <b>L1 오류가 아니다</b> - 재생성이
    /// 고칠 수 없는 코드 버그이므로 호출부가 경고로 표면화한다.
    /// </param>
    public sealed record SpecExpectations(
        IReadOnlyList<UpdateColumnExpectation> UpdateColumns,
        IReadOnlyDictionary<string, IReadOnlySet<string>> PromptSchemaColumns,
        IReadOnlySet<string> ColumnlessDependencyTables,
        IReadOnlyList<string> InputDefects)
    {
        /// <summary>원본이 3부 이상으로 표기한 테이블 참조가 하나라도 있는가.</summary>
        public bool HasThreePartReference { get; init; }

        /// <summary>원본에 Linked Server(4부) 참조가 있는가.</summary>
        public bool HasLinkedServerReference { get; init; }

        /// <summary>
        /// 대조할 것이 하나도 없으면 null을 돌려준다. 호출부가 null 검사를 하지 않고
        /// 그대로 넘길 수 있게 하기 위해서다 - Validate는 null을 "종전 동작"으로 받는다.
        /// </summary>
        public static SpecExpectations? From(SpDefinition? spDef)
        {
            if (spDef == null) return null;

            var updateColumns = BuildUpdateColumns(spDef.StaticAnalysis);

            var promptSchemaColumns = new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.OrdinalIgnoreCase);
            var columnlessDependencyTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dep in spDef.Dependencies)
            {
                var canonical = StaticAnalysisNormalizer.CanonicalizeParts(
                    dep.Database, dep.Schema, dep.Name, spDef.ObjectKey?.Database, spDef.Schema);
                if (string.IsNullOrWhiteSpace(canonical)) continue;

                if (dep.Columns.Count == 0)
                {
                    // 컬럼이 없는 의존성은 스키마 표 자체가 렌더링되지 않는다
                    // (BuildSpMetadataTexts의 dep.Columns.Count > 0 조건). 대조 기준으로
                    // 삼으면 "스키마 정의는 제공되지 않았습니다"라는 참인 진술이 대조
                    // 대상으로 잘못 올라간다. 그러나 canonical 이름 자체는 별도 집합에
                    // 담아 둔다 - 위 ColumnlessDependencyTables 문서 참고.
                    columnlessDependencyTables.Add(canonical);
                    continue;
                }

                promptSchemaColumns[canonical] = SchemaPromptColumnSelector.Select(dep, spDef);
            }

            var inputDefects = SchemaPromptColumnSelector.DetectOrphanedColumnKeys(spDef);

            var analysis = spDef.StaticAnalysis;
            var hasThreePartReference = analysis.ThreePartTableReferences.Count > 0;
            var hasLinkedServerReference = analysis.LinkedServerReferences.Count > 0;

            // 대조할 것이 하나도 없을 때만 null이다. 재료를 추가하는 태스크는 이 식에
            // 자기 항을 반드시 이어야 한다 - 빠뜨리면 그 검사가 한 번도 돌지 않고,
            // 스위트는 초록으로 남는다.
            if (updateColumns.Count == 0
                && promptSchemaColumns.Count == 0
                && inputDefects.Count == 0
                && !hasThreePartReference
                && !hasLinkedServerReference)
            {
                return null;
            }

            return new SpecExpectations(
                updateColumns, promptSchemaColumns, columnlessDependencyTables, inputDefects)
            {
                HasThreePartReference = hasThreePartReference,
                HasLinkedServerReference = hasLinkedServerReference
            };
        }

        /// <summary>
        /// 테이블 단위로 접는다. 대조가 테이블 합집합이므로 기대도 같은 단위여야 한다.
        /// </summary>
        private static List<UpdateColumnExpectation> BuildUpdateColumns(SpStaticAnalysisResult? analysis)
        {
            if (analysis == null || analysis.AstUpdateMappings.Count == 0)
            {
                return new List<UpdateColumnExpectation>();
            }

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

            return byTable
                .Where(kvp => kvp.Value.Count > 0)
                .Select(kvp => new UpdateColumnExpectation(kvp.Key, kvp.Value))
                .ToList();
        }
    }

    /// <summary>한 테이블에 대해 명세서의 UPDATE 매핑 표에 반드시 있어야 하는 컬럼들.</summary>
    public sealed record UpdateColumnExpectation(string Table, IReadOnlyList<string> Columns);
}
