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

    // 카탈로그가 알려주는 실제 객체명을 확보하면 그래프 소유자가 표기를 한 번 교체한다.
    public CodeObjectKey Key { get; internal set; }
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

    public CodeObjectKey Source { get; internal set; }
    public CodeObjectKey Target { get; internal set; }
    public bool IsDynamicSqlCandidate { get; set; }
}

public sealed class CodeObjectPipelineResult
{
    // A single-object verification run result. The existing collections below are
    // retained for recursive graph analysis consumers.
    public SpDefinition? SpDef { get; set; }
    public string? SpecMarkdown { get; set; }
    public ReviewResult? Review { get; set; }
    // 명시적 기본값을 두지 않는다: DependencyAnalysisOrchestrator가 재귀 그래프 결과를
    // 구성할 때 이 최상위 필드는 실제로 채워지지 않는 채 방치되어 있었다(그래프 결과의
    // 진짜 상태는 AnalysisResults[i].Outcome에 있다). enum의 0번 값이 ReviewNotRun이므로
    // 이 필드를 놓친 생성부는 이제 "통과"가 아니라 "검증되지 않음"으로 안전하게 대체된다.
    public VerificationOutcome Outcome { get; set; }
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
    // 유일한 프로덕션 생성부(DependencyAnalysisOrchestrator)는 항상 파이프라인 결과의
    // Outcome을 명시적으로 대입하므로 이 기본값은 실질적으로 쓰이지 않는다. 그럼에도
    // 명시적 Passed 기본값을 남겨두면 향후 생성부가 대입을 빠뜨렸을 때 조용히 "통과"를
    // 자칭하는 함정이 되므로, enum의 안전한 0번 값(ReviewNotRun)을 그대로 물려받게 한다.
    public VerificationOutcome Outcome { get; set; }
    public string? ThinkingText { get; set; }
    public string? SpecPath { get; set; }
    public string? DdlPath { get; set; }
}
