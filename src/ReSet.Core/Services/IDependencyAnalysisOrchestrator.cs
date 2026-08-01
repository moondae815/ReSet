using ReSet.Core.Models;

namespace ReSet.Core.Services;

public sealed record DependencyAnalysisProgress(int Current, int Total, CodeObjectKey Key);

public sealed record DependencyAnalysisRequest
{
    public string ConnectionString { get; init; } = string.Empty;
    public int MaxDepth { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string? ActorEffort { get; init; }
    public string Instructions { get; init; } = string.Empty;
    public bool IsBatchMode { get; init; }
    public string OutputDirectory { get; init; } = "./output";
    public bool EnableCache { get; init; }
    public bool AllowExternalDatabaseConnections { get; init; }

    /// <summary>
    /// 분석 기준 데이터베이스. <see cref="IDependencyAnalysisOrchestrator.AnalyzeAsync"/>가
    /// 루트 객체의 DB로 덮어쓰므로 호출자가 설정할 필요는 없다.
    /// 이 값이 <c>OutputPathResolver</c>의 "현재 DB" 기준이 되며,
    /// 이와 다른 DB의 객체는 <c>External/&lt;DB&gt;/</c> 아래로 배치된다.
    /// </summary>
    public string? AnalysisDatabase { get; init; }

    public DependencyArtifactMode DependencyArtifactMode { get; init; } = DependencyArtifactMode.Reference;
    public Action<DependencyAnalysisProgress>? Progress { get; init; }

    /// <summary>
    /// record가 자동 생성하는 <see cref="object.ToString"/>에 접속 문자열(자격 증명 포함)이
    /// 노출되지 않도록 <see cref="ConnectionString"/>만 마스킹한다. 나머지 속성은 그대로 출력한다.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("ConnectionString = ***");
        builder.Append(", MaxDepth = ").Append(MaxDepth);
        builder.Append(", Provider = ").Append(Provider);
        builder.Append(", ModelName = ").Append(ModelName);
        builder.Append(", ActorEffort = ").Append(ActorEffort);
        builder.Append(", Instructions = ").Append(Instructions);
        builder.Append(", IsBatchMode = ").Append(IsBatchMode);
        builder.Append(", OutputDirectory = ").Append(OutputDirectory);
        builder.Append(", EnableCache = ").Append(EnableCache);
        builder.Append(", AllowExternalDatabaseConnections = ").Append(AllowExternalDatabaseConnections);
        builder.Append(", AnalysisDatabase = ").Append(AnalysisDatabase);
        builder.Append(", DependencyArtifactMode = ").Append(DependencyArtifactMode);
        builder.Append(", Progress = ").Append(Progress);
        return true;
    }
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
