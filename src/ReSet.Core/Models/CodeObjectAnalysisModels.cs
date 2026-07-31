using ReSet.Core.Services;

namespace ReSet.Core.Models;

public sealed class FunctionReturnInfo
{
    public string DataType { get; set; } = string.Empty;
    public bool IsTableValued { get; set; }
    public List<ColumnInfo> Columns { get; set; } = new();
}

public enum AnalysisNodeStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    SkippedExternal,
    SkippedDepth,
    Cancelled
}

public sealed class AnalysisNode
{
    public AnalysisNode(CodeObjectKey key) => Key = key;

    public CodeObjectKey Key { get; }
    public AnalysisNodeStatus Status { get; set; } = AnalysisNodeStatus.Queued;
    public int AnalysisAttempts { get; set; }
    public string? Error { get; set; }
    public string? SpecPath { get; set; }
    public string? DdlPath { get; set; }
}

public sealed class DependencyEdge
{
    public DependencyEdge(CodeObjectKey source, CodeObjectKey target)
    {
        Source = source;
        Target = target;
    }

    public CodeObjectKey Source { get; }
    public CodeObjectKey Target { get; }
    public bool IsDynamicSqlCandidate { get; set; }
}

public sealed class CodeObjectPipelineResult
{
    // A single-object verification run result. The existing collections below are
    // retained for recursive graph analysis consumers.
    public SpDefinition? SpDef { get; set; }
    public string? SpecMarkdown { get; set; }
    public ReviewResult? Review { get; set; }
    public string? ThinkingText { get; set; }
    public List<AnalysisNode> Nodes { get; set; } = new();
    public List<DependencyEdge> DependencyEdges { get; set; } = new();
    public List<DependencyEdge> Edges
    {
        get => DependencyEdges;
        set => DependencyEdges = value ?? new();
    }
    public List<CodeObjectAnalysisResult> AnalysisResults { get; set; } = new();

    public AnalysisNode GetNode(CodeObjectKey key) =>
        Nodes.Single(node => node.Key == key);
}

public sealed class CodeObjectAnalysisResult
{
    public CodeObjectKey Key { get; set; } = null!;
    public SpDefinition Definition { get; set; } = new();
    public FunctionReturnInfo? FunctionReturn { get; set; }
    public string? SpecMarkdown { get; set; }
    public ReviewResult? Review { get; set; }
    public string? ThinkingText { get; set; }
    public string? SpecPath { get; set; }
    public string? DdlPath { get; set; }
}
