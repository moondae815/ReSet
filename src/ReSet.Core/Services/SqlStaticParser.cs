using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class SqlStaticParser
    {
        public SpStaticAnalysisResult Analyze(string ddlText, int compatibilityLevel = 160, Dictionary<string, List<string>>? tableColumnsMap = null)
        {
            var result = new SpStaticAnalysisResult();
            if (string.IsNullOrWhiteSpace(ddlText))
            {
                result.IsParsedSuccessfully = false;
                result.ParserWarningMessage = "DDL 텍스트가 비어 있습니다.";
                return result;
            }

            try
            {
                var parser = CreateParser(compatibilityLevel);
                using (var reader = new StringReader(ddlText))
                {
                    var fragment = parser.Parse(reader, out var errors);
                    if (errors != null && errors.Count > 0)
                    {
                        result.IsParsedSuccessfully = false;
                        var sb = new StringBuilder();
                        sb.AppendLine($"T-SQL 구문 오류 감지 (호환성 수준 {compatibilityLevel}, Soft Fail 적용):");
                        foreach (var err in errors)
                        {
                            sb.AppendLine($"- Line {err.Line}, Col {err.Column}: {err.Message}");
                        }
                        result.ParserWarningMessage = sb.ToString();
                        Log.Warning("[SqlStaticParser] T-SQL 정적 파싱 구문 오류 발생 - {Errors}", result.ParserWarningMessage);
                        return result;
                    }

                    if (fragment != null)
                    {
                        var visitor = new SpStructureVisitor(tableColumnsMap);

                        // Pre-scan table aliases to handle order-dependency (SELECT list before FROM clause)
                        var aliasVisitor = new TableAliasVisitor();
                        fragment.Accept(aliasVisitor);
                        visitor.InitializeAliasMap(aliasVisitor.QueryLocalAliasMaps, aliasVisitor.GlobalAliasToTableMap);

                        fragment.Accept(visitor);

                        result.IsParsedSuccessfully = true;
                        result.ReferencedTables = visitor.ReferencedTables;
                        result.CreatedTempTables = visitor.CreatedTempTables;
                        result.ControlFlowSummary = visitor.ControlFlowSummary;
                        result.SelectTables = visitor.SelectTables;
                        result.InsertTables = visitor.InsertTables;
                        result.AstInsertMappings = visitor.AstInsertMappings;
                        result.AstUpdateMappings = visitor.AstUpdateMappings;
                        result.UpdateTables = visitor.UpdateTables;
                        result.DeleteTables = visitor.DeleteTables;
                        result.LinkedServerReferences = visitor.LinkedServerReferences;
                        result.ReferencedFunctions = visitor.ReferencedFunctions;
                        result.ProcedureParameters = visitor.ProcedureParameters;
                        result.DeclaredVariables = visitor.DeclaredVariables;
                        result.ReferencedColumnsPerTable = visitor.ReferencedColumnsPerTable;
                    }
                }
            }
            catch (Exception ex)
            {
                result.IsParsedSuccessfully = false;
                result.ParserWarningMessage = $"정적 파싱 예외 발생: {ex.Message}";
                Log.Error(ex, "[SqlStaticParser] 예외 발생 (Soft Fail)");
            }

            return result;
        }

        private TSqlParser CreateParser(int compatibilityLevel)
        {
            if (compatibilityLevel >= 160) return new TSql160Parser(true);
            if (compatibilityLevel >= 150) return new TSql150Parser(true);
            if (compatibilityLevel >= 140) return new TSql140Parser(true);
            if (compatibilityLevel >= 130) return new TSql130Parser(true);
            if (compatibilityLevel >= 120) return new TSql120Parser(true);
            if (compatibilityLevel >= 110) return new TSql110Parser(true);
            return new TSql100Parser(true);
        }

        public List<ChunkAnalysisResult> ExtractStatementChunks(string ddlText, int compatibilityLevel = 160)
        {
            var chunks = new List<ChunkAnalysisResult>();
            if (string.IsNullOrWhiteSpace(ddlText)) return chunks;

            try
            {
                var parser = CreateParser(compatibilityLevel);
                using (var reader = new StringReader(ddlText))
                {
                    var fragment = parser.Parse(reader, out var errors);
                    if (errors == null || errors.Count == 0)
                    {
                        var chunkVisitor = new StatementChunkVisitor(ddlText);
                        fragment.Accept(chunkVisitor);
                        chunks.AddRange(chunkVisitor.Chunks);
                    }
                    else
                    {
                        Log.Warning("[SqlStaticParser] ExtractStatementChunks 구문 오류로 청크 분할 실패.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SqlStaticParser] ExtractStatementChunks 예외 발생");
            }
            return chunks;
        }
    }

    internal class StatementChunkVisitor : TSqlFragmentVisitor
    {
        private readonly string _ddlText;
        public List<ChunkAnalysisResult> Chunks { get; } = new();

        public StatementChunkVisitor(string ddlText)
        {
            _ddlText = ddlText;
        }

        public override void Visit(UpdateStatement node) { AddChunk(node); }
        public override void Visit(InsertStatement node) { AddChunk(node); }
        public override void Visit(DeleteStatement node) { AddChunk(node); }
        public override void Visit(MergeStatement node) { AddChunk(node); }
        public override void Visit(SelectStatement node) { AddChunk(node); }

        private void AddChunk(TSqlStatement node)
        {
            string text = _ddlText.Substring(node.StartOffset, node.FragmentLength);
            
            var innerVisitor = new SpStructureVisitor(null);
            var aliasVisitor = new TableAliasVisitor();
            node.Accept(aliasVisitor);
            innerVisitor.InitializeAliasMap(aliasVisitor.QueryLocalAliasMaps, aliasVisitor.GlobalAliasToTableMap);
            node.Accept(innerVisitor);

            var chunk = new ChunkAnalysisResult
            {
                StatementText = text,
                ReferencedTables = innerVisitor.ReferencedTables,
                ReferencedFunctions = innerVisitor.ReferencedFunctions
            };
            Chunks.Add(chunk);
        }
    }

    internal class SpStructureVisitor : TSqlFragmentVisitor
    {
        public List<string> ReferencedTables { get; } = new();
        public List<string> CreatedTempTables { get; } = new();
        public List<string> ControlFlowSummary { get; } = new();

        public List<string> SelectTables { get; } = new();
        public List<string> InsertTables { get; } = new();
        public List<AstInsertMapping> AstInsertMappings { get; } = new();
        public List<string> UpdateTables { get; } = new();
        public List<AstUpdateMapping> AstUpdateMappings { get; } = new();
        public List<string> DeleteTables { get; } = new();

        public List<string> LinkedServerReferences { get; } = new();
        public List<string> ReferencedFunctions { get; } = new();
        public List<string> ProcedureParameters { get; } = new();
        public List<string> DeclaredVariables { get; } = new();
        public Dictionary<string, List<string>> ReferencedColumnsPerTable { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _foundTables = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _foundTemps = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _foundSelect = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _foundInsert = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _foundUpdate = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _foundDelete = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _updateOrdinals = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _foundLinked = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _foundFuncs = new(StringComparer.OrdinalIgnoreCase);

        private readonly Stack<string> _statementContext = new();
        private readonly Dictionary<QuerySpecification, Dictionary<string, string>> _queryLocalAliasMaps = new();
        private readonly Dictionary<string, string> _globalAliasToTableMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<QuerySpecification> _querySpecs = new();
        private int _indentLevel = 0;
        private string? _currentInsertTarget = null;
        private TSqlFragment? _currentDmlTargetNode = null;
        private bool _dmlTargetResolved = false;
        private HashSet<string>? _currentCteNames = null;
        private readonly Dictionary<string, List<string>>? _tableColumnsMap;

        public SpStructureVisitor(Dictionary<string, List<string>>? tableColumnsMap = null)
        {
            _tableColumnsMap = tableColumnsMap;
        }

        public void InitializeAliasMap(
            Dictionary<QuerySpecification, Dictionary<string, string>> localMaps,
            Dictionary<string, string> globalMap)
        {
            _queryLocalAliasMaps.Clear();
            foreach (var kvp in localMaps)
            {
                _queryLocalAliasMaps[kvp.Key] = kvp.Value;
            }

            _globalAliasToTableMap.Clear();
            foreach (var kvp in globalMap)
            {
                _globalAliasToTableMap[kvp.Key] = kvp.Value;
            }
        }

        // CRUD Statement 방문 감지 및 컨텍스트 스택 처리 (ExplicitVisit 적용)
        public override void ExplicitVisit(SelectStatement node)
        {
            _statementContext.Push("SELECT");
            base.ExplicitVisit(node);
            _statementContext.Pop();
        }

        public override void ExplicitVisit(InsertStatement node)
        {
            _statementContext.Push("INSERT");
            string? prevInsertTarget = _currentInsertTarget;
            if (node.InsertSpecification != null && node.InsertSpecification.Target is NamedTableReference namedTarget && namedTarget.SchemaObject != null)
            {
                _currentInsertTarget = GetSchemaObjectString(namedTarget.SchemaObject);
            }
            base.ExplicitVisit(node);
            _currentInsertTarget = prevInsertTarget;
            _statementContext.Pop();
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            _statementContext.Push("UPDATE");
            var prevCteNames = _currentCteNames;
            _currentCteNames = CollectCteNames(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            _currentCteNames = prevCteNames;
            _statementContext.Pop();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            _statementContext.Push("DELETE");
            var prevCteNames = _currentCteNames;
            _currentCteNames = CollectCteNames(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            _currentCteNames = prevCteNames;
            _statementContext.Pop();
        }

        /// <summary>
        /// WITH 절의 CTE 이름을 모은다. `WITH C AS (...) UPDATE C SET ...`처럼 대상이
        /// CTE 이름과 같으면 그것은 별칭이 아니라 CTE 참조이고, FROM 절 별칭 탐색으로는
        /// 절대 풀리지 않는 물리 테이블 부재 상태다 - 별도로 미해결로 표시해야 한다.
        /// </summary>
        private static HashSet<string>? CollectCteNames(WithCtesAndXmlNamespaces? withClause)
        {
            if (withClause?.CommonTableExpressions == null || withClause.CommonTableExpressions.Count == 0)
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cte in withClause.CommonTableExpressions)
            {
                if (!string.IsNullOrWhiteSpace(cte.ExpressionName?.Value))
                {
                    names.Add(cte.ExpressionName!.Value);
                }
            }

            return names;
        }

        // Specification 단위 상세 감지 (ExplicitVisit 적용)
        public override void ExplicitVisit(InsertSpecification node)
        {
            _statementContext.Push("INSERT");
            string? prevInsertTarget = _currentInsertTarget;
            if (node.Target is NamedTableReference namedTarget && namedTarget.SchemaObject != null)
            {
                _currentInsertTarget = GetSchemaObjectString(namedTarget.SchemaObject);

                var mapping = new AstInsertMapping { TargetTable = _currentInsertTarget };
                if (node.Columns != null)
                {
                    foreach (var col in node.Columns)
                    {
                        mapping.TargetColumns.Add(GetFragmentText(col));
                    }
                }
                if (node.InsertSource != null)
                {
                    mapping.SourceQueryBlock = GetFragmentText(node.InsertSource);
                }
                AstInsertMappings.Add(mapping);
            }
            base.ExplicitVisit(node);
            _currentInsertTarget = prevInsertTarget;
            _statementContext.Pop();
        }

        private string GetFragmentText(TSqlFragment fragment)
        {
            if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;
            var sb = new StringBuilder();
            for (int i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
            {
                sb.Append(fragment.ScriptTokenStream[i].Text);
            }
            return sb.ToString().Trim();
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            _statementContext.Push("UPDATE");
            var prevTargetNode = _currentDmlTargetNode;
            var prevResolved = _dmlTargetResolved;

            _currentDmlTargetNode = node.Target;
            _dmlTargetResolved = RecordDmlTarget(
                node.Target, node.FromClause, UpdateTables, _foundUpdate, _currentCteNames, out var resolvedTarget);

            // 대상을 풀지 못한 문장은 매핑을 만들지 않는다. 잘못 푼 테이블 이름에 컬럼을
            // 붙이면 L1이 존재하지 않는 표를 요구하게 되고, 그것은 무한 재시도가 된다.
            if (_dmlTargetResolved && !string.IsNullOrWhiteSpace(resolvedTarget))
            {
                RecordUpdateMapping(node, resolvedTarget!);
            }

            base.ExplicitVisit(node);

            _currentDmlTargetNode = prevTargetNode;
            _dmlTargetResolved = prevResolved;
            _statementContext.Pop();
        }

        private void RecordUpdateMapping(UpdateSpecification node, string targetTable)
        {
            // targetTable이 비어 있으면 호출부 가드가 약해졌거나 우회된 상태다. 아래
            // _updateOrdinals.TryGetValue(targetTable, ...)는 null 키에서 예외를 던지고,
            // 그 예외는 Analyze의 catch-all에 잡혀 이 UPDATE 하나가 아니라 SP 전체의
            // 정적 분석 결과(ReferencedTables, AstInsertMappings 등)를 통째로 잃는다.
            // "이 UPDATE 하나를 건너뛴다"가 "SP 전체 분석을 잃는다"보다 훨씬 싸다.
            // 다만 조용히 넘어가면 상위 가드가 깨졌다는 사실 자체가 안 보이므로 흔적을
            // 남긴다 - 정상 경로에서는 이 지점에 도달하지 않으므로, 이 문구가 보인다는
            // 것 자체가 호출부 가드의 결함 신호다.
            if (string.IsNullOrWhiteSpace(targetTable))
            {
                var line = node.StartLine;
                ControlFlowSummary.Add(
                    $"Line {line}: [🚨 경고: UPDATE 대상 이름을 확인하지 못해 SET 매핑을 건너뜀 (내부 방어 가드 작동)]");
                return;
            }

            if (node.SetClauses == null) return;

            var assignments = new List<AstUpdateAssignment>();
            foreach (var clause in node.SetClauses)
            {
                var column = ExtractSetColumn(clause);
                if (string.IsNullOrWhiteSpace(column)) continue;

                assignments.Add(new AstUpdateAssignment
                {
                    Column = column!,
                    SourceExpression = ExtractSetExpression(clause)
                });
            }

            // SET 절이 컬럼을 하나도 대입하지 않으면(변수 대입뿐이면) 표로 만들 것이 없다.
            if (assignments.Count == 0) return;

            _updateOrdinals.TryGetValue(targetTable, out var previous);
            _updateOrdinals[targetTable] = previous + 1;

            var mapping = new AstUpdateMapping
            {
                TargetTable = targetTable,
                StatementOrdinal = previous + 1,
                FromClauseText = node.FromClause == null ? null : GetFragmentText(node.FromClause)
            };
            mapping.Assignments.AddRange(assignments);
            mapping.SelfReferencedColumns.AddRange(FindSelfReferences(node, assignments));
            AstUpdateMappings.Add(mapping);
        }

        private static string? ExtractSetColumn(SetClause clause)
        {
            switch (clause)
            {
                case AssignmentSetClause assignment:
                    // Column이 null이면 SET @var = ... 변수 대입이다. 컬럼이 아니다.
                    return LastIdentifier(assignment.Column?.MultiPartIdentifier);
                case FunctionCallSetClause call
                    when call.MutatorFunction?.CallTarget is MultiPartIdentifierCallTarget target:
                    // .WRITE() 변형. 컬럼만 뽑고 표현식은 절 원문을 쓴다.
                    return LastIdentifier(target.MultiPartIdentifier);
                default:
                    return null;
            }
        }

        private string ExtractSetExpression(SetClause clause) =>
            clause is AssignmentSetClause { NewValue: not null } assignment
                ? GetFragmentText(assignment.NewValue)
                : GetFragmentText(clause);

        private static string? LastIdentifier(MultiPartIdentifier? identifier)
        {
            var last = identifier?.Identifiers?.LastOrDefault();
            return string.IsNullOrWhiteSpace(last?.Value) ? null : last!.Value;
        }

        /// <summary>
        /// SET 우변이 같은 문장의 타겟 컬럼을 참조하는지 본다.
        ///
        /// 판정을 한 문장 안으로 제한한다. 전역 컬럼 사전을 쓰면 다른 문장이 갱신하는
        /// 동명 컬럼이 섞여 오탐이 난다 - RecordDmlTarget이 전역 별칭 사전을 쓰지 않는
        /// 것과 같은 이유다.
        /// </summary>
        private static List<string> FindSelfReferences(
            UpdateSpecification node, List<AstUpdateAssignment> assignments)
        {
            var targets = new HashSet<string>(
                assignments.Select(a => a.Column), StringComparer.OrdinalIgnoreCase);
            var targetAlias = ExtractTargetAlias(node);
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var clause in node.SetClauses.OfType<AssignmentSetClause>())
            {
                if (clause.NewValue == null) continue;

                var collector = new ColumnReferenceCollector();
                clause.NewValue.Accept(collector);

                foreach (var reference in collector.Columns)
                {
                    var column = LastIdentifier(reference.MultiPartIdentifier);
                    if (column == null || !targets.Contains(column)) continue;

                    // 한정자가 붙었고 갱신 대상 별칭을 알 때만 한정자를 본다.
                    // 대상이 별칭이 아니라 테이블 이름이면(UPDATE dbo.T SET ...)
                    // ExtractTargetAlias가 null을 돌려주고 이 규칙은 적용되지 않는다.
                    var qualifier = QualifierOf(reference.MultiPartIdentifier);
                    if (targetAlias != null
                        && qualifier != null
                        && !string.Equals(qualifier, targetAlias, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (seen.Add(column)) found.Add(column);
                }
            }

            return found;
        }

        /// <summary>
        /// UPDATE A SET ... FROM T A 형태의 갱신 대상 별칭. 대상이 한정된 테이블
        /// 이름이면(부(部)가 둘 이상) 별칭이 아니므로 null이다 - 이 경우 한정자
        /// 규칙을 적용하지 않는 쪽이 안전하다.
        /// </summary>
        private static string? ExtractTargetAlias(UpdateSpecification node)
        {
            if (node.Target is NamedTableReference named)
            {
                var identifiers = named.SchemaObject?.Identifiers;
                if (identifiers != null && identifiers.Count == 1)
                {
                    return identifiers[0].Value;
                }
            }

            return null;
        }

        private static string? QualifierOf(MultiPartIdentifier? identifier)
        {
            var parts = identifier?.Identifiers;
            if (parts == null || parts.Count < 2) return null;
            return parts[parts.Count - 2].Value;
        }

        private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
        {
            /// <summary>컬럼 이름이 아니라 참조 노드를 담는다 - 한정자 판정에 필요하다.</summary>
            public List<ColumnReferenceExpression> Columns { get; } = new();

            /// <summary>
            /// 중첩 질의 안으로 내려가지 않는다. 그 스코프의 컬럼은 다른 테이블 소속이다.
            /// (SELECT OutYMD FROM dbo.UIF_SettleYMD(...))의 OutYMD를 거둬 오면 갱신
            /// 대상과 이름만 같은 남의 컬럼을 자기참조로 단정한다 - EXPECT_PROC:203-205
            /// 에서 실측된 오탐이며, 그 거짓 문장이 그대로 프롬프트에 실렸다.
            ///
            /// base를 부르지 않는 것이 곧 하위 순회 중단이다.
            /// </summary>
            public override void ExplicitVisit(ScalarSubquery node) { }

            public override void Visit(ColumnReferenceExpression node) => Columns.Add(node);
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            _statementContext.Push("DELETE");
            var prevTargetNode = _currentDmlTargetNode;
            var prevResolved = _dmlTargetResolved;

            _currentDmlTargetNode = node.Target;
            _dmlTargetResolved = RecordDmlTarget(node.Target, node.FromClause, DeleteTables, _foundDelete, _currentCteNames, out _);

            base.ExplicitVisit(node);

            _currentDmlTargetNode = prevTargetNode;
            _dmlTargetResolved = prevResolved;
            _statementContext.Pop();
        }

        /// <summary>
        /// UPDATE·DELETE의 대상 테이블 하나만 기록한다. INSERT가 이미 하는 것과 대칭이다.
        ///
        /// 대상이 별칭이면(UPDATE A SET ... FROM T A) 그 문장의 FROM 절에서 푼다.
        /// 전역 별칭 사전을 쓰지 않는 이유: 마지막 등록이 이기므로, 같은 별칭을 다른
        /// 문장이 다른 테이블에 쓰면 엉뚱한 테이블로 풀린다.
        ///
        /// 풀지 못하면 false를 돌려주고 호출부는 그 문장에 한해 기존 동작(문맥 내 전체
        /// 수집)으로 돌아간다. 대상을 통째로 잃는 것보다 과다 보고가 낫다.
        /// </summary>
        private bool RecordDmlTarget(
            TableReference? target,
            FromClause? fromClause,
            List<string> targetList,
            HashSet<string> seen,
            HashSet<string>? cteNames,
            out string? resolvedName)
        {
            resolvedName = null;
            if (target is not NamedTableReference named || named.SchemaObject == null) return false;

            var written = GetSchemaObjectString(named.SchemaObject);
            if (string.IsNullOrWhiteSpace(written)) return false;

            var resolved = ResolveDmlTargetName(written, fromClause, cteNames, out var aliasResolved);

            // UpdateTables/DeleteTables 과다 보고는 이 문장 이전부터의 관용적이고 무해한
            // 동작이므로 여기서는 건드리지 않는다 - targetList.Add와 반환값 true는 별칭
            // 해석 성공 여부와 무관하게 종전대로 유지한다. 바뀌는 것은 resolvedName뿐이다.
            if (resolved.StartsWith("#", StringComparison.Ordinal))
            {
                // 임시 테이블은 UpdateTables/DeleteTables에도 들어가지 않고 명세서 CRUD
                // 분석 표에도 물리 테이블로 기술되지 않는다. resolvedName을 null로 두어
                // SET 매핑을 만들지 않는다 - 매핑만 만들어 L1이 요구하게 하면 그 비대칭이
                // 무한 재시도가 된다.
                if (_foundTemps.Add(resolved)) CreatedTempTables.Add(resolved);
                return true;
            }

            if (_foundTables.Add(resolved)) ReferencedTables.Add(resolved);
            if (seen.Add(resolved)) targetList.Add(resolved);

            // 별칭을 실제로 풀지 못했으면(한정되지 않은 이름의 별칭 선언이 FROM 절에 있으나
            // 물리 테이블이 아니거나, CTE 이름과 같은 경우) resolvedName을 null로 남긴다 -
            // 호출부는 이것으로 "매핑을 만들지 않는다"를 판단한다. 잘못 푼 테이블 이름에
            // 컬럼을 붙이면 L1이 존재하지 않는 표를 요구하게 되고, 그것은 무한 재시도가 된다.
            if (aliasResolved) resolvedName = resolved;
            return true;
        }

        /// <summary>
        /// UPDATE·DELETE 대상 이름을 물리 테이블명으로 해석한다.
        ///
        /// "별칭 해석 실패"와 "애초에 별칭이 아님"을 구분해야 한다 - 후자를 전자로
        /// 오분류하면(라운드 1의 결함) `UPDATE TSettleMst SET ... FROM TSettleMst A, ...`처럼
        /// 대상이 한정되지 않은 평범한 물리 테이블명이고 FROM 절 별칭이 전부 다른 이름일 때도
        /// "별칭을 못 풀었다"고 오판해 정상적으로 만들어져야 할 매핑을 없애 버린다.
        ///
        /// 판정 순서:
        /// 1) 한정된 이름(점 포함)은 별칭일 수 없다 - 항상 해결.
        /// 2) CTE 이름과 같으면 별칭이 아니라 CTE 참조이고 물리 테이블이 존재하지 않는다 -
        ///    항상 미해결.
        /// 3) FROM 절에 이 이름과 같은 별칭 선언이 아예 없으면 별칭이 아니라 평범한
        ///    물리 테이블명이다 - 해결.
        /// 4) 별칭 선언은 있지만 그 대상이 NamedTableReference가 아니면(파생 테이블,
        ///    테이블 변수 등) 실제 테이블명을 알 수 없다 - 미해결.
        /// </summary>
        private static string ResolveDmlTargetName(
            string written, FromClause? fromClause, HashSet<string>? cteNames, out bool aliasResolved)
        {
            if (written.Contains('.'))
            {
                aliasResolved = true;
                return written;
            }

            if (cteNames != null && cteNames.Contains(written))
            {
                aliasResolved = false;
                return written;
            }

            var (aliasDeclared, resolvedTable) = ResolveAliasWithinFromClause(fromClause, written);
            if (!aliasDeclared)
            {
                aliasResolved = true;
                return written;
            }

            if (string.IsNullOrWhiteSpace(resolvedTable))
            {
                aliasResolved = false;
                return written;
            }

            aliasResolved = true;
            return resolvedTable!;
        }

        private static (bool AliasDeclared, string? ResolvedTableName) ResolveAliasWithinFromClause(
            FromClause? fromClause, string alias)
        {
            if (fromClause == null) return (false, null);

            var finder = new AliasTargetFinder(alias);
            fromClause.Accept(finder);
            return (finder.AliasDeclared, finder.ResolvedTableName);
        }

        /// <summary>
        /// FROM 절 하나 안에서 주어진 이름과 같은 별칭 선언이 있는지, 있다면 그것이
        /// 물리 테이블(NamedTableReference)을 가리키는지 찾는다.
        ///
        /// `TableReferenceWithAlias` 하나만 오버라이드하면 별칭을 가질 수 있는 모든 참조
        /// 타입이 잡힌다. ScriptDom의 `ExplicitVisit`은 노드 하나에 대해 그 상속 사슬에
        /// 있는 **모든** 타입의 `Visit` 오버로드를 호출하기 때문이다 - 구체 타입 오버로드가
        /// 따로 있어도 공통 부모 오버로드가 함께 불린다. 그래서 `NamedTableReference`,
        /// `QueryDerivedTable`, `VariableTableReference`를 따로 잡을 필요가 없고,
        /// `OpenQueryTableReference`처럼 여기서 열거하지 않은 타입도 자동으로 덮인다.
        ///
        /// 이 디스패치 방향은 추론이 아니라 뮤테이션으로 확인됐다: 이 폴백을 지우면
        /// `Analyze_UpdateTargetIsOpenQueryAlias_ShouldNotCreateMapping`이 깨지면서
        /// 별칭 `O`가 존재하지 않는 물리 테이블로 승격된다. 구체 타입 오버로드를 다시
        /// 넣고 싶어지면, 그것이 무엇을 더 잡는지 먼저 깨지는 테스트로 보여야 한다.
        ///
        /// `AliasDeclared`와 `ResolvedTableName`을 분리하는 이유: 별칭 선언 자체가
        /// 없으면(= 대상이 애초에 별칭이 아니라 평범한 물리 테이블명) 호출부가 그 이름을
        /// 그대로 신뢰해야 하고, 별칭 선언은 있지만 물리 테이블이 아니면(파생 테이블,
        /// 테이블 변수 등) 미해결로 봐야 한다. 이 둘을 `ResolvedTableName != null` 하나로
        /// 뭉치면 구분이 불가능하다.
        /// </summary>
        private sealed class AliasTargetFinder : TSqlFragmentVisitor
        {
            private readonly string _alias;

            public bool AliasDeclared { get; private set; }
            public string? ResolvedTableName { get; private set; }

            public AliasTargetFinder(string alias)
            {
                _alias = alias;
            }

            public override void Visit(TableReferenceWithAlias node) => Consider(node);

            private void Consider(TableReferenceWithAlias node)
            {
                if (AliasDeclared) return; // 첫 번째로 찾은 선언을 신뢰한다.
                if (node.Alias == null || string.IsNullOrWhiteSpace(node.Alias.Value)) return;
                if (!string.Equals(node.Alias.Value, _alias, StringComparison.OrdinalIgnoreCase)) return;

                AliasDeclared = true;
                if (node is NamedTableReference named && named.SchemaObject != null)
                {
                    ResolvedTableName = GetSchemaObjectString(named.SchemaObject);
                }
            }
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            _querySpecs.Push(node);
            _statementContext.Push("SELECT");
            base.ExplicitVisit(node);
            _statementContext.Pop();
            _querySpecs.Pop();
        }

        // 동적 SQL 실행 노드 감지 및 경고 추가 (ExecuteStatement)
        public override void ExplicitVisit(ExecuteStatement node)
        {
            var line = node.StartLine;
            var indent = new string(' ', _indentLevel * 2);
            if (node.ExecuteSpecification != null && node.ExecuteSpecification.ExecutableEntity != null)
            {
                var entity = node.ExecuteSpecification.ExecutableEntity;
                if (entity is ExecutableProcedureReference procRef)
                {
                    if (procRef.ProcedureReference != null && procRef.ProcedureReference.ProcedureReference != null)
                    {
                        var procName = GetSchemaObjectString(procRef.ProcedureReference.ProcedureReference.Name);
                        if (string.Equals(procName, "sp_executesql", StringComparison.OrdinalIgnoreCase))
                        {
                            ControlFlowSummary.Add($"{indent}Line {line}: [🚨 경고: sp_executesql 동적 SQL 실행 감지됨]");
                        }
                    }
                }
                else if (entity is ExecutableStringList)
                {
                    ControlFlowSummary.Add($"{indent}Line {line}: [🚨 경고: EXEC (@SQL) 동적 SQL 문자열 실행 감지됨]");
                }
            }
            base.ExplicitVisit(node);
        }

        // 1. 참조하는 테이블명 방문 수집 (NamedTableReference - ExplicitVisit 적용)
        public override void ExplicitVisit(NamedTableReference node)
        {
            base.ExplicitVisit(node);

            if (node.SchemaObject != null)
            {
                var tableName = GetSchemaObjectString(node.SchemaObject);
                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    // Alias 등록
                    if (node.Alias != null && !string.IsNullOrWhiteSpace(node.Alias.Value))
                    {
                        if (_querySpecs.Count > 0)
                        {
                            var currentSpec = _querySpecs.Peek();
                            if (!_queryLocalAliasMaps.TryGetValue(currentSpec, out var localMap))
                            {
                                localMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                _queryLocalAliasMaps[currentSpec] = localMap;
                            }
                            localMap[node.Alias.Value] = tableName;
                        }
                        _globalAliasToTableMap[node.Alias.Value] = tableName;
                    }

                    // Linked Server 감지 (ServerIdentifier가 있는 4파트 명칭 구조)
                    // 대상 노드라도 이 신호는 죽으면 안 된다 - DML 대상이 링크드 서버를
                    // 가리키는 경우도 실존한다.
                    if (node.SchemaObject.ServerIdentifier != null)
                    {
                        var linkedName = GetSchemaObjectString(node.SchemaObject);
                        if (_foundLinked.Add(linkedName))
                        {
                            LinkedServerReferences.Add(linkedName);
                            var line = node.StartLine;
                            var indent = new string(' ', _indentLevel * 2);
                            ControlFlowSummary.Add($"{indent}Line {line}: [🚨 경고: Linked Server 원격 테이블 참조 감지됨 - {linkedName}]");
                        }
                    }

                    // DML 대상 노드는 RecordDmlTarget이 이미 해석해 기록했다. 여기서
                    // 또 ReferencedTables/CRUD 분류에 넣으면 UPDATE A 의 'A' 같은
                    // 별칭이 테이블 이름으로 새어 들어간다. 별칭 등록과 링크드 서버
                    // 감지는 위에서 이미 끝났으니 그 둘만 보존하고 여기서 멈춘다.
                    if (_dmlTargetResolved && ReferenceEquals(node, _currentDmlTargetNode)) return;

                    if (tableName.StartsWith("#"))
                    {
                        if (_foundTemps.Add(tableName))
                        {
                            CreatedTempTables.Add(tableName);
                        }
                    }
                    else
                    {
                        if (_foundTables.Add(tableName))
                        {
                            ReferencedTables.Add(tableName);
                        }

                        // CRUD 분류 수집
                        var currentContext = _statementContext.Count > 0 ? _statementContext.Peek() : "SELECT";
                        switch (currentContext)
                        {
                            case "SELECT":
                                if (_foundSelect.Add(tableName)) SelectTables.Add(tableName);
                                break;
                            case "INSERT":
                                if (_foundInsert.Add(tableName)) InsertTables.Add(tableName);
                                break;
                            case "UPDATE":
                                // FROM 절 조인 원본은 읽기일 뿐 갱신 대상이 아니다.
                                // 대상은 RecordDmlTarget이 이미 기록했다.
                                if (_dmlTargetResolved)
                                {
                                    if (_foundSelect.Add(tableName)) SelectTables.Add(tableName);
                                }
                                else if (_foundUpdate.Add(tableName))
                                {
                                    UpdateTables.Add(tableName);
                                }
                                break;
                            case "DELETE":
                                if (_dmlTargetResolved)
                                {
                                    if (_foundSelect.Add(tableName)) SelectTables.Add(tableName);
                                }
                                else if (_foundDelete.Add(tableName))
                                {
                                    DeleteTables.Add(tableName);
                                }
                                break;
                        }
                    }
                }
            }
        }

        // 2. 함수 호출 감지 (FunctionCall - ExplicitVisit 적용)
        public override void ExplicitVisit(FunctionCall node)
        {
            base.ExplicitVisit(node);
            if (node.FunctionName != null)
            {
                // CallTarget이 존재하는 경우 (예: dbo.fn_GetBonus 에서 dbo 에 해당)
                if (node.CallTarget != null)
                {
                    var targetStr = GetCallTargetString(node.CallTarget);
                    if (!string.IsNullOrWhiteSpace(targetStr))
                    {
                        var funcName = targetStr + "." + node.FunctionName.Value;
                        if (_foundFuncs.Add(funcName))
                        {
                            ReferencedFunctions.Add(funcName);
                        }
                    }
                }
            }
        }

        // ColumnReferenceExpression 방문 및 수집
        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            base.ExplicitVisit(node);
            if (node.MultiPartIdentifier != null && node.MultiPartIdentifier.Identifiers.Count > 0)
            {
                var idents = node.MultiPartIdentifier.Identifiers;
                string columnName = idents[idents.Count - 1].Value;
                string? tableQualifier = idents.Count > 1 ? idents[idents.Count - 2].Value : null;

                string targetTable = "Unknown";
                if (!string.IsNullOrEmpty(tableQualifier))
                {
                    bool foundLocal = false;
                    if (_querySpecs.Count > 0)
                    {
                        var currentSpec = _querySpecs.Peek();
                        if (_queryLocalAliasMaps.TryGetValue(currentSpec, out var localMap))
                        {
                            if (localMap.TryGetValue(tableQualifier, out var mappedTable))
                            {
                                targetTable = mappedTable;
                                foundLocal = true;
                            }
                        }
                    }

                    if (!foundLocal)
                    {
                        if (_globalAliasToTableMap.TryGetValue(tableQualifier, out var mappedTable))
                        {
                            targetTable = mappedTable;
                        }
                        else if (_foundTables.Contains(tableQualifier) || _foundTemps.Contains(tableQualifier))
                        {
                            targetTable = tableQualifier;
                        }
                    }
                }
                else
                {
                    // 로컬 QuerySpecification 스코프 내의 단일 물리 테이블 매핑 시도
                    bool resolvedLocally = false;
                    if (_querySpecs.Count > 0)
                    {
                        var currentSpec = _querySpecs.Peek();
                        if (_queryLocalAliasMaps.TryGetValue(currentSpec, out var localMap))
                        {
                            var localTables = new HashSet<string>(localMap.Values, StringComparer.OrdinalIgnoreCase);

                            // 스키마 메타데이터 기반 대조 리졸버 작동
                            if (_tableColumnsMap != null && _tableColumnsMap.Count > 0 && localTables.Count > 0)
                            {
                                string? matchedTable = null;
                                int matchCount = 0;
                                foreach (var t in localTables)
                                {
                                    if (TryFindColumnsForTable(t, out var columns))
                                    {
                                        bool hasCol = false;
                                        foreach (var col in columns)
                                        {
                                            if (string.Equals(col, columnName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                hasCol = true;
                                                break;
                                            }
                                        }
                                        if (hasCol)
                                        {
                                            matchedTable = t;
                                            matchCount++;
                                        }
                                    }
                                }
                                if (matchCount == 1 && !string.IsNullOrEmpty(matchedTable))
                                {
                                    targetTable = matchedTable;
                                    resolvedLocally = true;
                                }
                            }

                            if (!resolvedLocally && localTables.Count == 1)
                            {
                                foreach (var t in localTables)
                                {
                                    targetTable = t;
                                    break;
                                }
                                resolvedLocally = true;
                            }
                        }
                    }

                    if (!resolvedLocally)
                    {
                        if (_statementContext.Count > 0 && _statementContext.Peek() == "INSERT" && !string.IsNullOrEmpty(_currentInsertTarget))
                        {
                            targetTable = _currentInsertTarget;
                        }
                        else if (ReferencedTables.Count == 1)
                        {
                            targetTable = ReferencedTables[0];
                        }
                        else if (ReferencedTables.Count == 0 && CreatedTempTables.Count == 1)
                        {
                            targetTable = CreatedTempTables[0];
                        }
                    }
                }

                if (targetTable != "Unknown")
                {
                    if (!ReferencedColumnsPerTable.TryGetValue(targetTable, out var columns))
                    {
                        columns = new List<string>();
                        ReferencedColumnsPerTable[targetTable] = columns;
                    }
                    if (!columns.Contains(columnName, StringComparer.OrdinalIgnoreCase))
                    {
                        columns.Add(columnName);
                    }
                }
            }
        }

        private bool TryFindColumnsForTable(string tableName, out List<string> columns)
        {
            columns = null!;
            if (_tableColumnsMap == null) return false;

            // 1. Exact Match (e.g. 'dbo.TPGCMRate')
            if (_tableColumnsMap.TryGetValue(tableName, out var cols))
            {
                columns = cols;
                return true;
            }

            // 2. Base Name Match (e.g. 'TPGCMRate'와 'dbo.TPGCMRate' 대조)
            var cleanTableName = tableName.Split('.').Last().Replace("[", "").Replace("]", "");
            foreach (var kvp in _tableColumnsMap)
            {
                var cleanKey = kvp.Key.Split('.').Last().Replace("[", "").Replace("]", "");
                if (string.Equals(cleanKey, cleanTableName, StringComparison.OrdinalIgnoreCase))
                {
                    columns = kvp.Value;
                    return true;
                }
            }

            return false;
        }

        private string GetCallTargetString(CallTarget callTarget)
        {
            if (callTarget is MultiPartIdentifierCallTarget mpTarget && mpTarget.MultiPartIdentifier != null)
            {
                var parts = new List<string>();
                foreach (var id in mpTarget.MultiPartIdentifier.Identifiers)
                {
                    parts.Add(id.Value);
                }
                return string.Join(".", parts);
            }
            return callTarget.ToString() ?? "";
        }

        // 3. IF 조건 분기 구조 방문 수집 (ExplicitVisit 및 들여쓰기 적용)
        public override void ExplicitVisit(IfStatement node)
        {
            var indent = new string(' ', _indentLevel * 2);
            var line = node.StartLine;
            var condText = GetNodeSqlText(node.Predicate);
            ControlFlowSummary.Add($"{indent}Line {line}: IF ({condText})");

            _indentLevel++;
            base.ExplicitVisit(node);
            _indentLevel--;
        }

        // 4. WHILE 루프 분기 구조 방문 수집 (ExplicitVisit 및 들여쓰기 적용)
        public override void ExplicitVisit(WhileStatement node)
        {
            var indent = new string(' ', _indentLevel * 2);
            var line = node.StartLine;
            var condText = GetNodeSqlText(node.Predicate);
            ControlFlowSummary.Add($"{indent}Line {line}: WHILE ({condText})");

            _indentLevel++;
            base.ExplicitVisit(node);
            _indentLevel--;
        }

        private static string GetSchemaObjectString(SchemaObjectName schemaObject)
        {
            var parts = new List<string>();
            if (schemaObject.ServerIdentifier != null) parts.Add(schemaObject.ServerIdentifier.Value);
            if (schemaObject.DatabaseIdentifier != null) parts.Add(schemaObject.DatabaseIdentifier.Value);
            if (schemaObject.SchemaIdentifier != null) parts.Add(schemaObject.SchemaIdentifier.Value);
            if (schemaObject.BaseIdentifier != null) parts.Add(schemaObject.BaseIdentifier.Value);

            return string.Join(".", parts);
        }

        private string GetNodeSqlText(TSqlFragment node)
        {
            if (node == null) return "Unknown Condition";
            var sb = new StringBuilder();
            for (int i = node.FirstTokenIndex; i <= node.LastTokenIndex; i++)
            {
                if (i >= 0 && node.ScriptTokenStream != null && i < node.ScriptTokenStream.Count)
                {
                    sb.Append(node.ScriptTokenStream[i].Text);
                }
            }
            var cond = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(cond) ? "Predicate Details" : cond;
        }

        public override void ExplicitVisit(ProcedureParameter node)
        {
            if (node.VariableName != null)
            {
                var typeStr = node.DataType != null ? GetFragmentText(node.DataType) : "Unknown";
                ProcedureParameters.Add($"{node.VariableName.Value} {typeStr}");
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareVariableElement node)
        {
            if (node.VariableName != null)
            {
                var typeStr = node.DataType != null ? GetFragmentText(node.DataType) : "Unknown";
                DeclaredVariables.Add($"{node.VariableName.Value} {typeStr}");
            }
            base.ExplicitVisit(node);
        }
    }

    internal class TableAliasVisitor : TSqlFragmentVisitor
    {
        public Dictionary<QuerySpecification, Dictionary<string, string>> QueryLocalAliasMaps { get; } = new();
        public Dictionary<string, string> GlobalAliasToTableMap { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FoundTables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FoundTemps { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly Stack<QuerySpecification> _querySpecs = new();

        public override void ExplicitVisit(QuerySpecification node)
        {
            _querySpecs.Push(node);
            if (!QueryLocalAliasMaps.ContainsKey(node))
            {
                QueryLocalAliasMaps[node] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            base.ExplicitVisit(node);
            _querySpecs.Pop();
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            base.ExplicitVisit(node);
            if (node.SchemaObject != null)
            {
                var tableName = GetSchemaObjectString(node.SchemaObject);
                if (!string.IsNullOrWhiteSpace(tableName))
                {
                    // 로컬 맵 등록
                    if (_querySpecs.Count > 0)
                    {
                        var currentSpec = _querySpecs.Peek();
                        if (QueryLocalAliasMaps.TryGetValue(currentSpec, out var localMap))
                        {
                            RegisterAlias(localMap, node, tableName);
                        }
                    }

                    // 전역 맵 등록
                    RegisterAlias(GlobalAliasToTableMap, node, tableName);

                    if (tableName.StartsWith("#"))
                    {
                        FoundTemps.Add(tableName);
                    }
                    else
                    {
                        FoundTables.Add(tableName);
                    }
                }
            }
        }

        private void RegisterAlias(Dictionary<string, string> map, NamedTableReference node, string tableName)
        {
            if (node.Alias != null && !string.IsNullOrWhiteSpace(node.Alias.Value))
            {
                map[node.Alias.Value] = tableName;
            }
            map[tableName] = tableName;
            var parts = tableName.Split('.');
            if (parts.Length > 0)
            {
                map[parts[parts.Length - 1]] = tableName;
            }
        }

        private string GetSchemaObjectString(SchemaObjectName schemaObject)
        {
            var parts = new List<string>();
            if (schemaObject.ServerIdentifier != null) parts.Add(schemaObject.ServerIdentifier.Value);
            if (schemaObject.DatabaseIdentifier != null) parts.Add(schemaObject.DatabaseIdentifier.Value);
            if (schemaObject.SchemaIdentifier != null) parts.Add(schemaObject.SchemaIdentifier.Value);
            if (schemaObject.BaseIdentifier != null) parts.Add(schemaObject.BaseIdentifier.Value);

            return string.Join(".", parts);
        }
    }
}
