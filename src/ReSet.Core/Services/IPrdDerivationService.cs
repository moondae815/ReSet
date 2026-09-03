using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReSet.Core.Services
{
    /// <summary>PRD 도출 한 건의 결과. Defects는 최종 저장본에 남은 결함이다.</summary>
    public sealed record PrdDerivationOutcome(
        string PrdPath,
        bool AttributionClean,
        IReadOnlyList<PrdDefect> Defects);

    public interface IPrdDerivationService
    {
        Task<PrdDerivationOutcome> DeriveAsync(
            string docsDirectory,
            string objectLabel,
            string? effort,
            CancellationToken cancellationToken = default);
    }
}
