using ReSet.Core.Models;

namespace ReSet.Core.Services;

public sealed record DependencyAnalysisProgress(int Current, int Total, CodeObjectKey Key);

public sealed class DependencyAnalysisRequest
{
    public string ConnectionString { get; init; } = string.Empty;
    public int MaxDepth { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string Instructions { get; init; } = string.Empty;
    public bool IsBatchMode { get; init; }
    public string OutputDirectory { get; init; } = "./output";
    public bool EnableCache { get; init; }
    public bool AllowExternalDatabaseConnections { get; init; }
    public DependencyArtifactMode DependencyArtifactMode { get; init; } = DependencyArtifactMode.Reference;
    public Action<DependencyAnalysisProgress>? Progress { get; init; }
}

public delegate Task<CodeObjectPipelineResult> DependencyAnalysisPipelineRunner(
    DependencyAnalysisRequest request,
    CodeObjectKey key,
    CancellationToken cancellationToken);

public interface IDependencyAnalysisOrchestrator
{
    Task<CodeObjectPipelineResult> AnalyzeAsync(
        CodeObjectKey rootKey,
        DependencyAnalysisRequest request,
        CancellationToken cancellationToken = default);
}
