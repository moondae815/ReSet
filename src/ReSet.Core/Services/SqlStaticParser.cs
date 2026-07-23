using System;
using System.Collections.Generic;
using System.IO;
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

        private readonly HashSet<string> _foundLinked = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _foundFuncs = new(StringComparer.OrdinalIgnoreCase);

        private readonly Stack<string> _statementContext = new();
        private readonly Dictionary<QuerySpecification, Dictionary<string, string>> _queryLocalAliasMaps = new();
        private readonly Dictionary<string, string> _globalAliasToTableMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<QuerySpecification> _querySpecs = new();
        private int _indentLevel = 0;
        private string? _currentInsertTarget = null;
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
            base.ExplicitVisit(node);
            _statementContext.Pop();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            _statementContext.Push("DELETE");
            base.ExplicitVisit(node);
            _statementContext.Pop();
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
            base.ExplicitVisit(node);
            _statementContext.Pop();
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            _statementContext.Push("DELETE");
            base.ExplicitVisit(node);
            _statementContext.Pop();
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
                                if (_foundUpdate.Add(tableName)) UpdateTables.Add(tableName);
                                break;
                            case "DELETE":
                                if (_foundDelete.Add(tableName)) DeleteTables.Add(tableName);
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

        private string GetSchemaObjectString(SchemaObjectName schemaObject)
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
