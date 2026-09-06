using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>읽어 온 테이블 하나. 값은 전부 문자열로 정규화해 담는다(매칭이 문자열 대조이므로).</summary>
    public sealed record ProfiledTable(
        string Table,
        IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

    /// <summary>
    /// 코드값의 우변을 채울 마스터 데이터를 읽는다.
    ///
    /// [왜 인터페이스인가] 정책 도출의 기본 경로는 DB 없이 완주한다. 프로파일러를
    /// 선택 의존으로 두면 서비스와 검사 전체가 DB 없이 테스트된다 - 종전
    /// SettlementPolicyService가 IDbMetadataService를 필수로 물어 사실상 테스트가
    /// 없었던 것이 이 분리의 이유다.
    /// </summary>
    public interface ICodeTableProfiler
    {
        Task<IReadOnlyList<ProfiledTable>> ProfileAsync(
            IReadOnlyList<DependencyInfo> dependencies,
            CancellationToken cancellationToken = default);
    }
}
