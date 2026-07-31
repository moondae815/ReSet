using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services;

public sealed class DependencyAnalysisOrchestrator : IDependencyAnalysisOrchestrator
{
    private readonly IDbMetadataService _metadataService;
    private readonly DependencyAnalysisPipelineRunner _pipelineRunner;

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
                directDependenciesOnly: true))
    {
        ArgumentNullException.ThrowIfNull(pipelineOrchestrator);
    }

    public DependencyAnalysisOrchestrator(
        IDbMetadataService metadataService,
        DependencyAnalysisPipelineRunner pipelineRunner)
    {
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _pipelineRunner = pipelineRunner ?? throw new ArgumentNullException(nameof(pipelineRunner));
    }

    public async Task<CodeObjectPipelineResult> AnalyzeAsync(
        CodeObjectKey rootKey,
        DependencyAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var execution = new ExecutionState(rootKey.Database);
        await QueueOrReuseAsync(rootKey, 0, request, execution, cancellationToken);

        return new CodeObjectPipelineResult
        {
            Nodes = execution.Nodes.Values.ToList(),
            DependencyEdges = execution.Edges,
            AnalysisResults = execution.AnalysisResults
        };
    }

    private Task<AnalysisNode> QueueOrReuseAsync(
        CodeObjectKey key,
        int depth,
        DependencyAnalysisRequest request,
        ExecutionState execution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var node = execution.GetOrAddNode(key);

        if (execution.Visiting.Contains(key))
        {
            return Task.FromResult(node);
        }

        if (execution.Tasks.TryGetValue(key, out var existingTask))
        {
            return existingTask;
        }

        var task = AnalyzeNodeAsync(key, depth, request, execution, cancellationToken);
        execution.Tasks[key] = task;
        return task;
    }

    private async Task<AnalysisNode> AnalyzeNodeAsync(
        CodeObjectKey key,
        int depth,
        DependencyAnalysisRequest request,
        ExecutionState execution,
        CancellationToken cancellationToken)
    {
        // The task must be registered before a synchronously-completed metadata
        // lookup can discover a cycle back to this key.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var node = execution.GetOrAddNode(key);
        node.Status = AnalysisNodeStatus.Running;
        node.AnalysisAttempts++;
        execution.Visiting.Add(key);

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
                MarkFailed(node, exception, "메타데이터 수집");
                return node;
            }

            if (definition is null)
            {
                MarkFailed(
                    node,
                    new InvalidOperationException("코드 객체 메타데이터가 비어 있습니다."),
                    "메타데이터 수집");
                return node;
            }

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
                    Skip(child, AnalysisNodeStatus.SkippedDepth, $"최대 의존성 깊이({request.MaxDepth})를 초과했습니다.");
                    continue;
                }

                if (!request.AllowExternalDatabaseConnections &&
                    !string.Equals(target.Database, execution.CurrentDatabase, StringComparison.OrdinalIgnoreCase))
                {
                    Skip(child, AnalysisNodeStatus.SkippedExternal, "외부 데이터베이스 연결이 허용되지 않았습니다.");
                    continue;
                }

                await QueueOrReuseAsync(target, targetDepth, request, execution, cancellationToken);
            }

            try
            {
                var pipelineResult = await _pipelineRunner(request, key, cancellationToken);
                if (pipelineResult.SpDef is null)
                {
                    MarkFailed(node, new InvalidOperationException("분석 파이프라인이 코드 객체 정의를 반환하지 않았습니다."), "분석 파이프라인");
                    return node;
                }

                node.Status = AnalysisNodeStatus.Succeeded;
                execution.AnalysisResults.Add(new CodeObjectAnalysisResult
                {
                    Key = key,
                    Definition = pipelineResult.SpDef,
                    FunctionReturn = pipelineResult.SpDef.FunctionReturn
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

            return node;
        }
        catch (OperationCanceledException)
        {
            node.Status = AnalysisNodeStatus.Cancelled;
            throw;
        }
        catch (Exception exception)
        {
            MarkFailed(node, exception, "의존성 그래프 처리");
            return node;
        }
        finally
        {
            execution.Visiting.Remove(key);
        }
    }

    private static IEnumerable<DependencyInfo> GetDirectCodeObjectDependencies(
        SpDefinition definition,
        CodeObjectKey sourceKey) =>
        definition.Dependencies.Where(dependency =>
            dependency.SourceObjectKey == sourceKey &&
            TryParseCodeObjectType(dependency.Type, out _));

    private static CodeObjectKey? CreateDependencyKey(
        DependencyInfo dependency,
        string currentDatabase)
    {
        if (!TryParseCodeObjectType(dependency.Type, out var type) ||
            string.IsNullOrWhiteSpace(dependency.Schema) ||
            string.IsNullOrWhiteSpace(dependency.Name))
        {
            return null;
        }

        return CodeObjectKey.Create(
            string.IsNullOrWhiteSpace(dependency.Database) ? currentDatabase : dependency.Database,
            dependency.Schema,
            dependency.Name,
            type);
    }

    private static bool TryParseCodeObjectType(string? dependencyType, out CodeObjectType type)
    {
        switch (dependencyType?.Trim().ToUpperInvariant())
        {
            case "PROCEDURE":
            case "PROC":
            case "P":
            case "PC":
            case "SQL_STORED_PROCEDURE":
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

    private sealed class ExecutionState
    {
        public ExecutionState(string currentDatabase) => CurrentDatabase = currentDatabase;

        public string CurrentDatabase { get; }
        public Dictionary<CodeObjectKey, AnalysisNode> Nodes { get; } = new();
        public Dictionary<CodeObjectKey, Task<AnalysisNode>> Tasks { get; } = new();
        public HashSet<CodeObjectKey> Visiting { get; } = new();
        public List<DependencyEdge> Edges { get; } = new();
        public List<CodeObjectAnalysisResult> AnalysisResults { get; } = new();

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
