using ReSet.Core.Services;

namespace ReSet.Core.Models;

/// <summary>
/// 1단계 개별 SP 분석의 최종 결과. 호출부(CLI)는 이 레코드 하나만 보고
/// 저장 여부와 보고 내용을 결정한다. 필드 이름이 곧 계약이다.
/// </summary>
public sealed record SpAnalysisOutcome
{
    public string? SpecMarkdown { get; init; }
    public SpDefinition? Definition { get; init; }
    public ReviewResult? Review { get; init; }
    public string? ThinkingText { get; init; }
    public VerificationOutcome Outcome { get; init; }

    public AnalysisScope Scope { get; init; }
    public GraphCompletion Completion { get; init; }
    public bool FromCache { get; init; }
    public DateTime? AnalyzedAt { get; init; }
    public ArtifactPersistence Persistence { get; init; }
    public IReadOnlyList<string> PersistenceErrors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 참조분석 OFF 경로. 단일 객체 파이프라인 결과를 옮긴다.
    /// 저장은 호출부가 하므로 Persistence는 NotAttempted다.
    /// </summary>
    public static SpAnalysisOutcome FromSingleObjectPipeline(CodeObjectPipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SpAnalysisOutcome
        {
            SpecMarkdown = result.SpecMarkdown,
            Definition = result.SpDef,
            Review = result.Review,
            ThinkingText = result.ThinkingText,
            Outcome = result.Outcome,
            Scope = AnalysisScope.Transitive,
            Completion = GraphCompletion.Complete,
            FromCache = result.FromCache,
            AnalyzedAt = result.AnalyzedAt,
            Persistence = ArtifactPersistence.NotAttempted
        };
    }

    /// <summary>
    /// 참조분석 ON 경로. 그래프에서 루트 분석 결과를 찾아 옮긴다.
    /// 캐시 상태는 루트 노드의 것이다 — 노드마다 다른 값을 하나로 접으면
    /// 어느 쪽으로 접어도 거짓이 된다.
    /// </summary>
    public static SpAnalysisOutcome FromDependencyGraph(
        CodeObjectPipelineResult result,
        CodeObjectKey rootKey)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rootKey);

        var root = result.AnalysisResults.FirstOrDefault(analysis => analysis.Key == rootKey);

        return new SpAnalysisOutcome
        {
            SpecMarkdown = root?.SpecMarkdown,
            Definition = root?.Definition,
            Review = root?.Review,
            ThinkingText = root?.ThinkingText,
            Outcome = root?.Outcome ?? VerificationOutcome.ReviewNotRun,
            Scope = AnalysisScope.Direct,
            Completion = result.Completion,
            FromCache = root?.FromCache ?? false,
            AnalyzedAt = root?.AnalyzedAt,
            Persistence = result.Persistence,
            PersistenceErrors = result.PersistenceErrors.ToArray()
        };
    }
}
