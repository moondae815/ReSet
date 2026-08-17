using System;
using System.Collections.Generic;
using System.Text;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정적 분석이 "쓰인 대로" 남긴 테이블 표기를 canonical 3-part로 통일한다.
    ///
    /// 파서는 SQL에 적힌 표기를 그대로 보고한다(그게 파서의 계약이다). 그래서 같은
    /// 물리 테이블이 TSettleMst / dbo.TSettleMst / SETTLE_POQ_DB.dbo.TSettleMst 세
    /// 갈래로 나뉜다. 소비자가 이를 세 테이블로 읽으면 스키마 표가 갈라지고, 배치
    /// 계획의 대상 테이블 목록이 부풀며, 컬럼 필터가 한 갈래만 보고 나머지 컬럼을
    /// "존재하지 않음"으로 만든다.
    ///
    /// AST도 DB도 보지 않는다. 이름만 다룬다.
    /// </summary>
    public static class StaticAnalysisNormalizer
    {
        /// <summary>
        /// 입력을 변경하지 않고 정리본을 돌려준다. 이름을 담지 않는 항목은 그대로 옮긴다.
        /// </summary>
        public static SpStaticAnalysisResult Normalize(
            SpStaticAnalysisResult analysis,
            string? database,
            string? defaultSchema)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            var normalized = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = analysis.IsParsedSuccessfully,
                ParserWarningMessage = analysis.ParserWarningMessage,

                // 이름을 담지 않거나 정규화 대상이 아닌 항목은 복사만 한다.
                // 임시 테이블은 세션 지역이라 DB 한정이 무의미하고, 링크드 서버는
                // 4파트 원격 참조이며, 함수 이름은 이번 범위 밖이다.
                ControlFlowSummary = new List<string>(analysis.ControlFlowSummary),
                ProcedureParameters = new List<string>(analysis.ProcedureParameters),
                DeclaredVariables = new List<string>(analysis.DeclaredVariables),
                CreatedTempTables = new List<string>(analysis.CreatedTempTables),
                LinkedServerReferences = new List<string>(analysis.LinkedServerReferences),
                ReferencedFunctions = new List<string>(analysis.ReferencedFunctions),

                // 3부 이상 참조의 원문. 정규화하면 "원본이 몇 부로 썼는가"라는
                // 유일한 존재 이유가 사라진다.
                ThreePartObjectReferences = new List<string>(analysis.ThreePartObjectReferences),

                ReferencedTables = NormalizeList(analysis.ReferencedTables, database, defaultSchema),
                SelectTables = NormalizeList(analysis.SelectTables, database, defaultSchema),
                InsertTables = NormalizeList(analysis.InsertTables, database, defaultSchema),
                UpdateTables = NormalizeList(analysis.UpdateTables, database, defaultSchema),
                DeleteTables = NormalizeList(analysis.DeleteTables, database, defaultSchema),
                ReferencedColumnsPerTable = MergeColumnsByTable(
                    analysis.ReferencedColumnsPerTable, database, defaultSchema)
            };

            foreach (var mapping in analysis.AstInsertMappings)
            {
                normalized.AstInsertMappings.Add(new AstInsertMapping
                {
                    TargetTable = Canonicalize(mapping.TargetTable, database, defaultSchema),
                    TargetColumns = new List<string>(mapping.TargetColumns),
                    SourceQueryBlock = mapping.SourceQueryBlock
                });
            }

            // 테이블 이름만 다룬다. 컬럼과 표현식은 그대로 옮긴다 - 표현식을 정규화하면
            // SQL 재작성이 되고, 그것은 이 클래스가 하지 않기로 한 일이다.
            foreach (var mapping in analysis.AstUpdateMappings)
            {
                var copy = new AstUpdateMapping
                {
                    TargetTable = Canonicalize(mapping.TargetTable, database, defaultSchema),
                    StatementOrdinal = mapping.StatementOrdinal,
                    SourceLine = mapping.SourceLine,
                    FromClauseText = mapping.FromClauseText,
                    // 원문 표기다 - canonicalize하면 "원본이 몇 부로 썼는가"를 잃는다.
                    RawTargetText = mapping.RawTargetText
                };

                foreach (var assignment in mapping.Assignments)
                {
                    copy.Assignments.Add(new AstUpdateAssignment
                    {
                        Column = assignment.Column,
                        SourceExpression = assignment.SourceExpression
                    });
                }

                copy.SelfReferencedColumns.AddRange(mapping.SelfReferencedColumns);
                normalized.AstUpdateMappings.Add(copy);
            }

            return normalized;
        }

        /// <summary>
        /// SQL에 적힌 표기 하나를 canonical 3-part로 바꾼다.
        ///
        /// DB나 스키마 컨텍스트가 없으면 한정하지 않는다 - 없는 이름을 지어내는 것보다
        /// 갈라진 채 남는 편이 낫다.
        /// </summary>
        public static string Canonicalize(string? writtenName, string? database, string? defaultSchema)
        {
            if (string.IsNullOrWhiteSpace(writtenName)) return string.Empty;

            var trimmed = writtenName.Trim();

            // 임시 테이블과 테이블 변수는 스키마 한정 대상이 아니다.
            if (trimmed.StartsWith("#", StringComparison.Ordinal) ||
                trimmed.StartsWith("@", StringComparison.Ordinal))
            {
                return trimmed;
            }

            var parts = SplitIdentifier(trimmed);

            // 4파트는 링크드 서버 참조다. 로컬 DB 이름을 씌우면 원격 테이블이
            // 로컬 테이블로 둔갑한다.
            if (parts.Count >= 4) return string.Join(".", parts);

            if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(defaultSchema))
            {
                return string.Join(".", parts);
            }

            return parts.Count switch
            {
                1 => $"{database}.{defaultSchema}.{parts[0]}",
                2 => $"{database}.{parts[0]}.{parts[1]}",
                3 => $"{parts[0]}.{parts[1]}.{parts[2]}",
                _ => string.Join(".", parts)
            };
        }

        /// <summary>
        /// 이미 조각으로 나뉜 입력(DependencyInfo 등)을 같은 규칙으로 맞춘다.
        /// DependencyInfo.Database는 분석 대상과 같은 DB일 때 null이다.
        /// </summary>
        public static string CanonicalizeParts(
            string? database,
            string? schema,
            string name,
            string? fallbackDatabase,
            string? fallbackSchema)
        {
            var resolvedDatabase = string.IsNullOrWhiteSpace(database) ? fallbackDatabase : database;
            var resolvedSchema = string.IsNullOrWhiteSpace(schema) ? fallbackSchema : schema;

            if (string.IsNullOrWhiteSpace(resolvedDatabase) || string.IsNullOrWhiteSpace(resolvedSchema))
            {
                return Canonicalize(name, null, null);
            }

            return Canonicalize($"{resolvedDatabase}.{resolvedSchema}.{name}", resolvedDatabase, resolvedSchema);
        }

        private static List<string> NormalizeList(
            IEnumerable<string> names,
            string? database,
            string? defaultSchema)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var name in names)
            {
                var canonical = Canonicalize(name, database, defaultSchema);
                if (string.IsNullOrWhiteSpace(canonical)) continue;
                if (seen.Add(canonical)) result.Add(canonical);
            }

            return result;
        }

        private static Dictionary<string, List<string>> MergeColumnsByTable(
            Dictionary<string, List<string>> source,
            string? database,
            string? defaultSchema)
        {
            var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var seenColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in source)
            {
                var canonical = Canonicalize(entry.Key, database, defaultSchema);
                if (string.IsNullOrWhiteSpace(canonical)) continue;

                if (!merged.TryGetValue(canonical, out var columns))
                {
                    columns = new List<string>();
                    merged[canonical] = columns;
                    seenColumns[canonical] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                var seen = seenColumns[canonical];
                foreach (var column in entry.Value)
                {
                    // 첫 등장 순서를 보존한다 - 프롬프트가 이 순서를 INSERT 매핑표의
                    // 행 순서로 쓴다.
                    if (seen.Add(column)) columns.Add(column);
                }
            }

            return merged;
        }

        /// <summary>
        /// 대괄호 안의 점은 구분자가 아니다. [my.table] 같은 이름을 쪼개지 않는다.
        ///
        /// 대괄호는 구분자 판단에만 쓰고 문자는 보존한다. 예전 구현은 ']'를
        /// 무조건 버려서 my]table을 mytable로 손상시켰다 - 미지원이 아니라 손상이다.
        /// ScriptDom이 이미 ']]' 이스케이프를 해제하므로 입력에 ']]'는 오지 않고,
        /// 따라서 T-SQL 이스케이프 파싱은 구현하지 않는다.
        /// </summary>
        private static List<string> SplitIdentifier(string name)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            var inBracket = false;

            foreach (var ch in name)
            {
                if (ch == '[') inBracket = true;
                else if (ch == ']') inBracket = false;
                else if (ch == '.' && !inBracket)
                {
                    parts.Add(UnwrapBrackets(current.ToString()));
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            parts.Add(UnwrapBrackets(current.ToString()));
            return parts;
        }

        /// <summary>조각 전체를 감싼 대괄호만 벗긴다. 이름 속의 대괄호는 남긴다.</summary>
        private static string UnwrapBrackets(string part)
        {
            var trimmed = part.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
                ? trimmed[1..^1].Trim()
                : trimmed;
        }
    }
}
