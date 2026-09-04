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

    public GraphCompletion Completion { get; init; }
    public bool FromCache { get; init; }
    public DateTime? AnalyzedAt { get; init; }
    public ArtifactPersistence Persistence { get; init; }
    public IReadOnlyList<string> PersistenceErrors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 오케스트레이터 경로. 그래프에서 루트 분석 결과를 찾아 옮긴다.
    /// 캐시 상태는 루트 노드의 것이다 — 노드마다 다른 값을 하나로 접으면
    /// 어느 쪽으로 접어도 거짓이 된다.
    /// 수집 범위(AnalysisScope)는 여기에 담지 않는다. 그 값을 쓰는 곳은
    /// Spec.md 헤더 하나뿐이고, 그것은 DependencyAnalysisOrchestrator가
    /// 요청을 보고 직접 정한다. 여기에 사본을 두면 아무도 안 읽는 채로
    /// 원본과 어긋날 수 있고, 어긋나도 산출물도 테스트도 변하지 않는다.
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
            Completion = result.Completion,
            FromCache = root?.FromCache ?? false,
            AnalyzedAt = root?.AnalyzedAt,
            Persistence = result.Persistence,
            PersistenceErrors = result.PersistenceErrors.ToArray()
        };
    }
}
