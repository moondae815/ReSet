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
    /// <summary>
    /// 루트 객체에서 시작해 의존 코드 객체를 탐색·분석하고, 완료된 객체의 산출물을 저장한다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>취소 계약(중요):</b> <paramref name="cancellationToken"/>이 취소되어도 이 메서드는
    /// <see cref="OperationCanceledException"/>을 <b>던지지 않는다.</b> 취소는 예외가 아니라
    /// 반환값의 상태로 전달된다 — 결과의 <c>Completion</c>이
    /// <see cref="GraphCompletion.PartialCancelled"/>가 되고, 취소 시점까지 성공한 객체의
    /// 산출물은 별도의 유예 토큰(최대 30초)으로 <b>저장이 완료된 뒤</b> 반환된다.
    /// 따라서 <c>PartialCancelled</c> 결과의 <c>Persistence</c>는 <c>NotAttempted</c>가 아니며,
    /// 호출부는 저장을 다시 시도하면 안 된다.
    /// </para>
    /// <para>
    /// 예외로 취소를 감지하려는 호출부(<c>catch (OperationCanceledException)</c>)는 이 경로에서
    /// 절대 발동하지 않는다. 취소 여부는 반드시 <c>Completion</c>으로 판정하고,
    /// 그 판정을 명세서 존재 여부 검사보다 <b>앞에</b> 두어야 한다. 실행 순서가 후위 순회라
    /// 루트가 마지막에 실행되므로, 취소 시 루트 명세서는 거의 항상 비어 있기 때문이다.
    /// </para>
    /// <para>
    /// 취소가 아닌 개별 객체의 분석 실패는 예외가 아니라 노드 상태
    /// (<c>AnalysisNodeStatus.Failed</c>)로 기록되며 그래프 실행은 계속된다.
    /// </para>
    /// <para>
    /// 유일한 예외는 <b>진입 시점에 이미 취소된 토큰</b>이다. 이 경우에는 그래프도 산출물도
    /// 없으므로 <see cref="OperationCanceledException"/>을 그대로 던진다.
    /// </para>
    /// </remarks>
    /// <param name="rootKey">분석 시작 객체. <c>Database</c>가 분석 기준 DB가 된다.</param>
    /// <param name="request">분석 옵션. <c>AnalysisDatabase</c>는 <paramref name="rootKey"/>의 DB로 덮어써진다.</param>
    /// <param name="cancellationToken">사용자 취소 토큰. 위 취소 계약을 참조.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="rootKey"/> 또는 <paramref name="request"/>가 null인 경우.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="rootKey"/>의 <c>Database</c>가 비어 있는 경우. 빈 DB명은 산출물 경로
    /// 계산을 막아 모든 저장을 조용히 무산시키므로, 폴백하지 않고 즉시 드러낸다.
    /// </exception>
    Task<CodeObjectPipelineResult> AnalyzeAsync(
        CodeObjectKey rootKey,
        DependencyAnalysisRequest request,
        CancellationToken cancellationToken = default);
}
