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
                directDependenciesOnly: request.AnalyzeReferencedCodeObjects,
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

        // 빈 DB명은 OutputPathResolver 생성을 막아 모든 산출물 저장을 조용히
        // 무산시킨다. 호출부 결함이므로 폴백하지 않고 즉시 드러낸다.
        if (string.IsNullOrWhiteSpace(rootKey.Database))
        {
            throw new ArgumentException(
                "분석 기준 데이터베이스를 확인할 수 없어 산출물 경로를 계산할 수 없습니다.",
                nameof(rootKey));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 호출자가 무엇을 넣었든 루트 객체의 DB가 분석 기준이 된다.
        // 캐시 판정(VerificationPipelineOrchestrator)과 최종 저장(PersistArtifactsAsync)이
        // 같은 OutputPathResolver 기준을 쓰도록 보장하는 지점이다.
        var effectiveRequest = request with { AnalysisDatabase = rootKey.Database };

        var execution = new ExecutionState(rootKey.Database);
        var completion = GraphCompletion.Complete;

        try
        {
            await DiscoverAsync(rootKey, 0, effectiveRequest, execution, cancellationToken);

            // 호출부 표기(sys.sql_expression_dependencies·AST)가 아니라 카탈로그의 실제 객체명을
            // 그래프의 단일 표기로 확정한다. 파이프라인 실행 전에 적용해야 캐시 키와 산출물 경로가
            // 호출한 SP마다 갈라지지 않는다.
            execution.ApplyCanonicalKeys();
            await ExecuteDiscoveredNodesAsync(effectiveRequest, execution, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 취소를 예외로 흘려보내면 "완료분은 저장됐다"는 사실이 호출부에
            // 도달하지 못한다. 결과 레코드가 계약이므로 상태로 바꾼다.
            completion = GraphCompletion.PartialCancelled;
            Log.Information(
                "[의존성 분석] 사용자 취소 - 완료된 객체만 저장합니다: {ObjectKey}",
                rootKey.CanonicalName);
        }

        var result = new CodeObjectPipelineResult
        {
            Nodes = execution.Nodes.Values.ToList(),
            DependencyEdges = execution.Edges,
            AnalysisResults = execution.AnalysisResults,
            Completion = completion
        };

        // 취소된 토큰을 그대로 넘기면 저장부의 ThrowIfCancellationRequested가
        // 즉시 던져 아무것도 쓰지 못한다. CancellationToken.None은 네트워크
        // 드라이브에서 무한정 매달릴 수 있으므로 상한을 둔다.
        using var persistCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await PersistArtifactsAsync(rootKey, effectiveRequest, result, persistCts.Token);
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

            // OFF는 "깊이 0"이 아니라 "그래프를 만들지 않는다"이다. 자식을 발견해
            // 실행 목록에 넣으면 OFF에서도 자식마다 AI 비용이 나가고, 사용자가 고르지
            // 않은 Spec.md가 생긴다. 루트가 잃는 정보는 파이프라인이 전이적 메타데이터를
            // 대신 실어 메운다(DependencyAnalysisRequest.AnalyzeReferencedCodeObjects).
            if (request.AnalyzeReferencedCodeObjects)
            {
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
                    ThinkingText = pipelineResult.ThinkingText,
                    FromCache = pipelineResult.FromCache,
                    AnalyzedAt = pipelineResult.AnalyzedAt
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
                        ResolveScope(request),
                        cancellationToken);

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(node.SpecPath!)!);
                        await File.WriteAllTextAsync(
                            node.SpecPath!,
                            BuildPersistedSpecification(analysis, request, graph),
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        MarkFailed(node, ex, "명세서 파일 저장");
                        graph.PersistenceErrors.Add($"{analysis.Key.Schema}.{analysis.Key.Name}: {ex.Message}");
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

                await PersistThinkingAsync(analysis, paths, request, cancellationToken);

                await _metadataExporter.ExportCodeObjectArtifactsAsync(
                    analysis.Definition,
                    analysis.Key,
                    graph,
                    request.DependencyArtifactMode,
                    paths,
                    cancellationToken: cancellationToken);
            }

            // 노드 하나라도 저장에 실패했으면 전체를 Failed로 부른다. 사용자가
            // 알아야 하는 것은 "일부가 디스크에 없다"는 사실이고, 어느 노드인지는
            // PersistenceErrors와 노드 Status가 말해 준다.
            graph.Persistence = graph.PersistenceErrors.Count > 0
                ? ArtifactPersistence.Failed
                : ArtifactPersistence.Persisted;
        }
        catch (OperationCanceledException)
        {
            // AnalyzeAsync가 30초 grace 토큰을 넘기므로 이 취소는 사용자 Ctrl+C가
            // 아니라 저장 제한 시간 초과다. 다시 던지면 호출부가 결과를 못 받아
            // "저장에 실패했다"는 사실조차 전달되지 않으므로 상태로 바꾼다.
            graph.Persistence = ArtifactPersistence.Failed;
            graph.PersistenceErrors.Add("저장 제한 시간(30초)을 초과했습니다.");
            Log.Warning("[의존성 분석] 저장 제한 시간 초과: {ObjectKey}", rootKey.CanonicalName);
        }
        catch (Exception ex)
        {
            graph.Persistence = ArtifactPersistence.Failed;
            graph.PersistenceErrors.Add(ex.Message);
            Log.Warning(ex, "[의존성 분석] 객체 아티팩트 저장 중 오류가 발생했습니다: {ObjectKey}", rootKey.CanonicalName);
        }
    }

    private static string BuildPersistedSpecification(
        CodeObjectAnalysisResult analysis,
        DependencyAnalysisRequest request,
        CodeObjectPipelineResult graph)
    {
        var body = analysis.SpecMarkdown ?? string.Empty;

        // 배너는 analysis.SpecMarkdown에 되쓰지 않는다. 재링크 루프가 이 메서드를
        // 여러 번 부를 수 있는데, 되쓰면 배너가 겹겹이 쌓인다.
        var unresolved = CollectUnresolvedReferences(analysis.Key, graph);
        if (unresolved.Count > 0)
        {
            body = VerificationBanner.UnresolvedReferences(unresolved) + body;
        }

        return VerificationDocumentFormatter.FormatVerifiedDocument(
            body,
            analysis.Review,
            analysis.Outcome,
            request.Provider,
            request.ModelName,
            request.ActorEffort,
            analysis.AnalyzedAt ?? DateTime.Now,
            ResolveScope(request));
    }

    /// <summary>
    /// 이 회차가 실제로 수집한 의존성의 범위. 헤더의 「분석 범위」 도장과 「참조 코드 객체」
    /// 절이 같은 값을 읽어야 한 문서가 자기 수집 범위를 두 가지로 신고하지 않는다.
    /// </summary>
    private static AnalysisScope ResolveScope(DependencyAnalysisRequest request) =>
        request.AnalyzeReferencedCodeObjects
            ? AnalysisScope.Direct
            : AnalysisScope.Transitive;

    /// <summary>
    /// 이 문서가 참조하는 객체 중 분석이 끝나지 않은 것들의 이름을 모은다.
    /// 참조 섹션 생성과 같은 상태(자식 노드 Status)를 보므로 두 표기가 어긋나지 않는다.
    /// </summary>
    private static IReadOnlyList<string> CollectUnresolvedReferences(
        CodeObjectKey parentKey,
        CodeObjectPipelineResult graph)
    {
        var nodesByKey = graph.Nodes.ToDictionary(node => node.Key);

        return graph.DependencyEdges
            .Where(edge => edge.Source.Equals(parentKey))
            .Select(edge => edge.Target)
            .Distinct()
            .Where(target =>
                nodesByKey.TryGetValue(target, out var node) &&
                node.Status is AnalysisNodeStatus.Cancelled or AnalysisNodeStatus.Queued)
            .Select(target => $"{target.Schema}.{target.Name}")
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task PersistThinkingAsync(
        CodeObjectAnalysisResult analysis,
        OutputPathResolver paths,
        DependencyAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var thinkingPath = Path.Combine(
                paths.ResolveDocsDirectory(analysis.Key),
                "Thinking.md");

            // 캐시 히트 회차는 ThinkingText가 비어 있다 — 파이프라인이 AI를 호출한
            // 회차에만 그 값을 채우기 때문이다. 그 빈 값으로 덮으면 앞선 회차의 추론
            // 기록이 자리표시자와 오늘 날짜로 사라진다. 남길 것이 없으면 이미 있는
            // 기록을 지키고, 파일이 아예 없을 때만 자리표시자 판본을 만든다 —
            // "파일 없음"과 "추론 없음"은 산출물만 보고 구분되어야 한다.
            // (raw/prompt-context.md가 MetadataExporter에서 받는 보호와 같은 규약)
            if (string.IsNullOrWhiteSpace(analysis.ThinkingText) && File.Exists(thinkingPath))
            {
                return;
            }

            await File.WriteAllTextAsync(
                thinkingPath,
                ThinkingLogDocument.Compose(
                    analysis.ThinkingText,
                    request.Provider,
                    request.ModelName,
                    request.ActorEffort,
                    DateTime.Now),
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
