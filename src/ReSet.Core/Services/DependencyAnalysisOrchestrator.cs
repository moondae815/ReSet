using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services;

public sealed class DependencyAnalysisOrchestrator : IDependencyAnalysisOrchestrator
{
    private readonly IDbMetadataService _metadataService;
    private readonly DependencyAnalysisPipelineRunner _pipelineRunner;
    private readonly IMetadataExporter _metadataExporter;
    private readonly MechanicalValidator _validator;

    public DependencyAnalysisOrchestrator(
        IDbMetadataService metadataService,
        VerificationPipelineOrchestrator pipelineOrchestrator)
        : this(
            metadataService,
            (request, key, cancellationToken) => pipelineOrchestrator.RunCodeObjectPipelineAsync(
                request.ConnectionString,
                key,
                request.MaxDepth,
                request.Provider,
                request.Instructions,
                request.IsBatchMode,
                request.OutputDirectory,
                request.EnableCache,
                cancellationToken,
                directDependenciesOnly: true,
                includeExternalCodeObjects: true,
                analysisDatabase: request.AnalysisDatabase),
            new MetadataExporter(),
            new MechanicalValidator())
    {
        ArgumentNullException.ThrowIfNull(pipelineOrchestrator);
    }

    public DependencyAnalysisOrchestrator(
        IDbMetadataService metadataService,
        DependencyAnalysisPipelineRunner pipelineRunner,
        IMetadataExporter? metadataExporter = null,
        MechanicalValidator? validator = null)
    {
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _pipelineRunner = pipelineRunner ?? throw new ArgumentNullException(nameof(pipelineRunner));
        _metadataExporter = metadataExporter ?? new MetadataExporter();
        _validator = validator ?? new MechanicalValidator();
    }

    public async Task<CodeObjectPipelineResult> AnalyzeAsync(
        CodeObjectKey rootKey,
        DependencyAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 호출자가 무엇을 넣었든 루트 객체의 DB가 분석 기준이 된다.
        // 캐시 판정(VerificationPipelineOrchestrator)과 최종 저장(PersistArtifactsAsync)이
        // 같은 OutputPathResolver 기준을 쓰도록 보장하는 지점이다.
        var effectiveRequest = request with { AnalysisDatabase = rootKey.Database };

        var execution = new ExecutionState(rootKey.Database);
        await DiscoverAsync(rootKey, 0, effectiveRequest, execution, cancellationToken);

        // 호출부 표기(sys.sql_expression_dependencies·AST)가 아니라 카탈로그의 실제 객체명을
        // 그래프의 단일 표기로 확정한다. 파이프라인 실행 전에 적용해야 캐시 키와 산출물 경로가
        // 호출한 SP마다 갈라지지 않는다.
        execution.ApplyCanonicalKeys();
        await ExecuteDiscoveredNodesAsync(effectiveRequest, execution, cancellationToken);

        var result = new CodeObjectPipelineResult
        {
            Nodes = execution.Nodes.Values.ToList(),
            DependencyEdges = execution.Edges,
            AnalysisResults = execution.AnalysisResults
        };
        await PersistArtifactsAsync(rootKey, effectiveRequest, result, cancellationToken);
        return result;
    }

    private async Task DiscoverAsync(
        CodeObjectKey key,
        int depth,
        DependencyAnalysisRequest request,
        ExecutionState execution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var node = execution.GetOrAddNode(key);

        if (!execution.TryRegisterDepth(key, depth))
        {
            return;
        }

        node.Status = AnalysisNodeStatus.Running;

        try
        {
            SpDefinition definition;
            try
            {
                definition = await _metadataService.GetCodeObjectDetailsDirectAsync(
                    request.ConnectionString,
                    key,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                node.Status = AnalysisNodeStatus.Cancelled;
                throw;
            }
            catch (Exception exception)
            {
                node.AnalysisAttempts++;
                MarkFailed(node, exception, "메타데이터 수집");
                return;
            }

            if (definition is null)
            {
                node.AnalysisAttempts++;
                MarkFailed(
                    node,
                    new InvalidOperationException("코드 객체 메타데이터가 비어 있습니다."),
                    "메타데이터 수집");
                return;
            }

            execution.RegisterCanonicalKey(definition.ObjectKey);

            foreach (var dependency in GetDirectCodeObjectDependencies(definition, key))
            {
                var target = CreateDependencyKey(dependency, key.Database);
                if (target is null)
                {
                    continue;
                }

                execution.AddEdge(key, target, dependency.IsDynamicSqlCandidate);
                var child = execution.GetOrAddNode(target);
                var targetDepth = depth + 1;

                if (targetDepth > request.MaxDepth)
                {
                    if (!execution.HasDepthAtMost(target, request.MaxDepth))
                    {
                        Skip(child, AnalysisNodeStatus.SkippedDepth, $"최대 의존성 깊이({request.MaxDepth})를 초과했습니다.");
                    }
                    continue;
                }

                if (target.Type == CodeObjectType.Unresolved)
                {
                    Skip(child, AnalysisNodeStatus.SkippedExternal, "외부 코드 객체 유형을 추가 조회 없이 확인할 수 없습니다.");
                    continue;
                }

                if (!request.AllowExternalDatabaseConnections &&
                    !string.Equals(target.Database, execution.CurrentDatabase, StringComparison.OrdinalIgnoreCase))
                {
                    Skip(child, AnalysisNodeStatus.SkippedExternal, "외부 데이터베이스 연결이 허용되지 않았습니다.");
                    continue;
                }

                await DiscoverAsync(target, targetDepth, request, execution, cancellationToken);
            }

            node.Status = AnalysisNodeStatus.Queued;
            if (!execution.ExecutionOrder.Contains(key))
            {
                execution.ExecutionOrder.Add(key);
            }
        }
        catch (OperationCanceledException)
        {
            node.Status = AnalysisNodeStatus.Cancelled;
            throw;
        }
        catch (Exception exception)
        {
            node.AnalysisAttempts++;
            MarkFailed(node, exception, "의존성 그래프 처리");
        }
    }

    private async Task ExecuteDiscoveredNodesAsync(
        DependencyAnalysisRequest request,
        ExecutionState execution,
        CancellationToken cancellationToken)
    {
        foreach (var key in execution.ExecutionOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = execution.GetOrAddNode(key);
            if (node.Status != AnalysisNodeStatus.Queued)
            {
                continue;
            }

            node.Status = AnalysisNodeStatus.Running;
            node.AnalysisAttempts++;
            try
            {
                ReportProgress(request, execution, key);
                var pipelineResult = await _pipelineRunner(request, key, cancellationToken);
                if (pipelineResult.SpDef is null)
                {
                    MarkFailed(node, new InvalidOperationException("분석 파이프라인이 코드 객체 정의를 반환하지 않았습니다."), "분석 파이프라인");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(pipelineResult.SpecMarkdown))
                {
                    MarkFailed(node, new InvalidOperationException("분석 파이프라인이 유효한 명세서를 반환하지 않았습니다."), "분석 파이프라인");
                    continue;
                }

                node.Status = AnalysisNodeStatus.Succeeded;
                execution.AnalysisResults.Add(new CodeObjectAnalysisResult
                {
                    Key = key,
                    Definition = pipelineResult.SpDef,
                    FunctionReturn = pipelineResult.SpDef.FunctionReturn,
                    SpecMarkdown = pipelineResult.SpecMarkdown,
                    Review = pipelineResult.Review,
                    Outcome = pipelineResult.Outcome,
                    ThinkingText = pipelineResult.ThinkingText
                });
            }
            catch (OperationCanceledException)
            {
                node.Status = AnalysisNodeStatus.Cancelled;
                throw;
            }
            catch (Exception exception)
            {
                MarkFailed(node, exception, "분석 파이프라인");
            }
        }
    }

    private static IEnumerable<DependencyInfo> GetDirectCodeObjectDependencies(
        SpDefinition definition,
        CodeObjectKey sourceKey) =>
        definition.Dependencies.Where(dependency =>
            dependency.SourceObjectKey == sourceKey &&
            (TryParseCodeObjectType(dependency.Type, out _) ||
             IsUnresolvedExternalDependency(dependency, sourceKey.Database)));

    private static CodeObjectKey? CreateDependencyKey(
        DependencyInfo dependency,
        string currentDatabase)
    {
        if (string.IsNullOrWhiteSpace(dependency.Schema) ||
            string.IsNullOrWhiteSpace(dependency.Name))
        {
            return null;
        }

        var dependencyDatabase = string.IsNullOrWhiteSpace(dependency.Database)
            ? currentDatabase
            : dependency.Database;
        var type = TryParseCodeObjectType(dependency.Type, out var parsedType)
            ? parsedType
            : IsUnresolvedExternalDependency(dependency, currentDatabase)
                ? CodeObjectType.Unresolved
                : (CodeObjectType?)null;
        if (type is null)
        {
            return null;
        }

        return CodeObjectKey.Create(
            dependencyDatabase,
            dependency.Schema,
            dependency.Name,
            type.Value);
    }

    private static bool IsUnresolvedExternalDependency(
        DependencyInfo dependency,
        string currentDatabase) =>
        string.Equals(
            dependency.Type?.Trim(),
            "UNKNOWN",
            StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(dependency.Database) &&
        !string.Equals(
            dependency.Database,
            currentDatabase,
            StringComparison.OrdinalIgnoreCase);

    private static bool TryParseCodeObjectType(string? dependencyType, out CodeObjectType type)
    {
        switch (dependencyType?.Trim().ToUpperInvariant())
        {
            case "PROCEDURE":
            case "PROC":
            case "P":
            case "PC":
            case "SQL_STORED_PROCEDURE":
            case "CLR_STORED_PROCEDURE":
                type = CodeObjectType.Procedure;
                return true;
            case "FUNCTION":
            case "FN":
            case "IF":
            case "TF":
            case "FS":
            case "FT":
            case "SQL_SCALAR_FUNCTION":
            case "SQL_TABLE_VALUED_FUNCTION":
            case "SQL_INLINE_TABLE_VALUED_FUNCTION":
            case "CLR_SCALAR_FUNCTION":
            case "CLR_TABLE_VALUED_FUNCTION":
                type = CodeObjectType.Function;
                return true;
            default:
                type = default;
                return false;
        }
    }

    private static void Skip(AnalysisNode node, AnalysisNodeStatus status, string reason)
    {
        if (node.Status != AnalysisNodeStatus.Queued)
        {
            return;
        }

        node.Status = status;
        node.Error = reason;
    }

    private static void MarkFailed(AnalysisNode node, Exception exception, string stage)
    {
        node.Status = AnalysisNodeStatus.Failed;
        node.Error = exception.Message;
        Log.Warning(exception, "[의존성 분석] {Stage} 실패 - 코드 객체: {ObjectKey}", stage, node.Key.CanonicalName);
    }

    private static void ReportProgress(
        DependencyAnalysisRequest request,
        ExecutionState execution,
        CodeObjectKey key)
    {
        var progress = request.Progress;
        if (progress is null)
        {
            return;
        }

        try
        {
            progress(new DependencyAnalysisProgress(
                ++execution.PipelineStartCount,
                execution.ExecutionOrder.Count,
                key));
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "[의존성 분석] 진행 상태 보고 실패 (계속 진행): {ObjectKey}", key.CanonicalName);
        }
    }

    private async Task PersistArtifactsAsync(
        CodeObjectKey rootKey,
        DependencyAnalysisRequest request,
        CodeObjectPipelineResult graph,
        CancellationToken cancellationToken)
    {
        try
        {
            var paths = new OutputPathResolver(rootKey.Database, request.OutputDirectory);
            var linker = new SpecificationLinker(paths, _validator);
            foreach (var analysis in graph.AnalysisResults)
            {
                var node = graph.Nodes.SingleOrDefault(candidate => candidate.Key == analysis.Key);
                if (node is null)
                {
                    continue;
                }

                node.SpecPath = paths.ResolveSpecPath(analysis.Key);
                node.DdlPath = paths.ResolveCanonicalDdlPath(analysis.Key);
                analysis.SpecPath = node.SpecPath;
                analysis.DdlPath = node.DdlPath;
            }

            bool statusChanged;
            do
            {
                statusChanged = false;
                foreach (var analysis in graph.AnalysisResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = graph.Nodes.Single(candidate => candidate.Key == analysis.Key);
                    if (node.Status != AnalysisNodeStatus.Succeeded)
                    {
                        continue;
                    }

                    analysis.SpecMarkdown = await linker.UpdateReferencesAsync(
                        analysis.Key,
                        analysis.SpecMarkdown ?? string.Empty,
                        graph,
                        cancellationToken);

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(node.SpecPath!)!);
                        await File.WriteAllTextAsync(
                            node.SpecPath!,
                            BuildPersistedSpecification(analysis, request),
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        MarkFailed(node, ex, "명세서 파일 저장");
                        statusChanged = true;
                        Log.Warning(ex, "[의존성 분석] 명세서 파일 저장 실패 (계속 진행): {ObjectKey}", analysis.Key.CanonicalName);
                    }
                }
            }
            while (statusChanged);

            foreach (var analysis in graph.AnalysisResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = graph.Nodes.Single(candidate => candidate.Key == analysis.Key);
                if (node.Status != AnalysisNodeStatus.Succeeded)
                {
                    continue;
                }

                await PersistThinkingAsync(analysis, paths, cancellationToken);

                await _metadataExporter.ExportCodeObjectArtifactsAsync(
                    analysis.Definition,
                    analysis.Key,
                    graph,
                    request.DependencyArtifactMode,
                    paths,
                    cancellationToken: cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[의존성 분석] 객체 아티팩트 저장 중 오류가 발생했습니다 (계속 진행): {ObjectKey}", rootKey.CanonicalName);
        }
    }

    private static string BuildPersistedSpecification(
        CodeObjectAnalysisResult analysis,
        DependencyAnalysisRequest request) =>
        SpecificationDocumentFormatter.Format(
            analysis.SpecMarkdown ?? string.Empty,
            analysis.Review,
            analysis.Outcome,
            request.Provider,
            request.ModelName,
            request.ActorEffort,
            DateTime.Now);

    private static async Task PersistThinkingAsync(
        CodeObjectAnalysisResult analysis,
        OutputPathResolver paths,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(analysis.ThinkingText))
        {
            return;
        }

        try
        {
            var thinkingPath = Path.Combine(
                paths.ResolveDocsDirectory(analysis.Key),
                "Thinking.md");
            await File.WriteAllTextAsync(
                thinkingPath,
                "# AI 추론 과정 로그 (Thinking Process Log)\n\n---\n\n" +
                analysis.ThinkingText,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(
                ex,
                "[의존성 분석] 추론 로그 저장 실패 (계속 진행): {ObjectKey}",
                analysis.Key.CanonicalName);
        }
    }

    private sealed class ExecutionState
    {
        public ExecutionState(string currentDatabase) => CurrentDatabase = currentDatabase;

        private readonly Dictionary<CodeObjectKey, CodeObjectKey> _canonicalKeys = new();

        public string CurrentDatabase { get; }
        public Dictionary<CodeObjectKey, AnalysisNode> Nodes { get; } = new();
        public Dictionary<CodeObjectKey, int> MinimumDepths { get; } = new();
        public List<DependencyEdge> Edges { get; } = new();
        public List<CodeObjectKey> ExecutionOrder { get; } = new();
        public List<CodeObjectAnalysisResult> AnalysisResults { get; } = new();
        public int PipelineStartCount { get; set; }

        public bool TryRegisterDepth(CodeObjectKey key, int depth)
        {
            if (MinimumDepths.TryGetValue(key, out var minimumDepth) &&
                minimumDepth <= depth)
            {
                return false;
            }

            MinimumDepths[key] = depth;
            return true;
        }

        public bool HasDepthAtMost(CodeObjectKey key, int maxDepth) =>
            MinimumDepths.TryGetValue(key, out var depth) && depth <= maxDepth;

        /// <summary>
        /// 메타데이터 서비스가 확정한 실제 객체명을 해당 객체의 표준 표기로 등록한다.
        /// </summary>
        public void RegisterCanonicalKey(CodeObjectKey? resolvedKey)
        {
            if (resolvedKey is not null)
            {
                _canonicalKeys[resolvedKey] = resolvedKey;
            }
        }

        /// <summary>
        /// 등록된 표준 표기를 노드·간선·실행 순서·깊이 기록 전체에 반영한다.
        /// </summary>
        public void ApplyCanonicalKeys()
        {
            if (_canonicalKeys.Count == 0)
            {
                return;
            }

            foreach (var node in Nodes.Values)
            {
                node.Key = Canonicalize(node.Key);
            }

            // Dictionary는 동등한 키를 다시 넣어도 기존 키 인스턴스를 유지하므로,
            // 표기를 바꾸려면 항목을 비운 뒤 다시 채워야 한다.
            var nodes = Nodes.Values.ToList();
            Nodes.Clear();
            foreach (var node in nodes)
            {
                Nodes[node.Key] = node;
            }

            var depths = MinimumDepths.ToList();
            MinimumDepths.Clear();
            foreach (var depth in depths)
            {
                MinimumDepths[Canonicalize(depth.Key)] = depth.Value;
            }

            foreach (var edge in Edges)
            {
                edge.Source = Canonicalize(edge.Source);
                edge.Target = Canonicalize(edge.Target);
            }

            for (var index = 0; index < ExecutionOrder.Count; index++)
            {
                ExecutionOrder[index] = Canonicalize(ExecutionOrder[index]);
            }
        }

        private CodeObjectKey Canonicalize(CodeObjectKey key) =>
            _canonicalKeys.TryGetValue(key, out var canonicalKey) ? canonicalKey : key;

        public AnalysisNode GetOrAddNode(CodeObjectKey key)
        {
            if (!Nodes.TryGetValue(key, out var node))
            {
                node = new AnalysisNode(key);
                Nodes.Add(key, node);
            }

            return node;
        }

        public void AddEdge(CodeObjectKey source, CodeObjectKey target, bool isDynamicSqlCandidate)
        {
            if (Edges.Any(edge => edge.Source == source && edge.Target == target))
            {
                return;
            }

            Edges.Add(new DependencyEdge(source, target)
            {
                IsDynamicSqlCandidate = isDynamicSqlCandidate
            });
        }
    }
}
