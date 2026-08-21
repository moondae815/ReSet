using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <param name="Kind">행의 종류. 표의 첫 칸이자 L1이 행을 특정하는 키의 일부다.</param>
    /// <param name="Line">원본 줄 번호. 줄에 매이지 않는 사실(DB 배치)은 "-".</param>
    /// <param name="Target">대상 원문 — 식·변수·커서 이름 등.</param>
    /// <param name="Fact">확정 사실 문장.</param>
    public sealed record ExecutionSemanticFact(string Kind, string Line, string Target, string Fact);

    /// <summary>
    /// 「실행 의미」 표의 행을 모은다.
    ///
    /// [왜 종류마다 표를 나누지 않았는가] 표 하나가 늘 때마다 헤딩 상수 · 렌더 조건 ·
    /// L1 검사 · 프롬프트 4갈래 배선 · 테스트 두 벌이 함께 늘어난다. 종류 칸 하나로
    /// 묶으면 그 비용을 한 번만 치른다. CASE 분기만 따로 둔 것은 행 수가 자릿수부터
    /// 다르기 때문이다(한 SP에서 수십 행).
    /// </summary>
    public static class ExecutionSemanticsFacts
    {
        public const string TableHeading = "### 실행 의미 (기계 확정 — 수정 금지)";

        public const string DatabasePlacementKind = "DB 배치";

        public const string AggregateAssignmentKind = "집계 대입";

        /// <summary>
        /// 컬럼명 → 데이터 타입 사전. ExpressionTypePathExtractor(Task 9)가 잎 타입을
        /// 판정할 때 쓴다. 같은 컬럼명이 테이블마다 타입이 다르면 판정할 수 없으므로
        /// "(모호)"로 표시해 그 CAST 행이 통째로 생략되게 한다.
        /// </summary>
        public static IReadOnlyDictionary<string, string> BuildColumnTypeMap(
            IEnumerable<DependencyInfo>? dependencies)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dep in dependencies ?? Enumerable.Empty<DependencyInfo>())
            {
                foreach (var col in dep.Columns)
                {
                    if (string.IsNullOrWhiteSpace(col.ColumnName)) continue;
                    if (string.IsNullOrWhiteSpace(col.DataType)) continue;

                    if (map.TryGetValue(col.ColumnName, out var existing)
                        && !string.Equals(existing, col.DataType, StringComparison.OrdinalIgnoreCase))
                    {
                        map[col.ColumnName] = "(모호)";
                        continue;
                    }

                    map[col.ColumnName] = col.DataType;
                }
            }

            return map;
        }

        public static IReadOnlyList<ExecutionSemanticFact> Collect(
            string? ddlText,
            SpStaticAnalysisResult? analysis,
            CodeObjectKey? objectKey,
            IReadOnlyDictionary<string, string> columnTypes)
        {
            var facts = new List<ExecutionSemanticFact>();

            var placement = DatabasePlacementExtractor.Extract(analysis, objectKey);
            if (placement != null)
            {
                facts.Add(new ExecutionSemanticFact(
                    DatabasePlacementKind, "-", "(객체 전체)", placement.Sentence));
            }

            foreach (var fact in AggregateAssignmentExtractor.Extract(ddlText))
            {
                facts.Add(new ExecutionSemanticFact(
                    AggregateAssignmentKind,
                    fact.Line.ToString(),
                    $"SELECT {fact.Variable} = {fact.Aggregate}(...)",
                    fact.Sentence));
            }

            return facts;
        }
    }
}
