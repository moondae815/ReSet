using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReSet.Core.Services
{
    /// <summary>명부가 준비되지 않아 정책 도출을 시작할 수 없다.</summary>
    public sealed class PolicyRosterBlockedException : Exception
    {
        public PolicyRosterBlockedException(IReadOnlyList<RosterDefect> defects, string rosterPath)
            : base($"정산 프로세스 명부가 준비되지 않았습니다({defects.Count}건). {rosterPath}를 확인하십시오.")
        {
            Defects = defects;
            RosterPath = rosterPath;
        }

        public IReadOnlyList<RosterDefect> Defects { get; }

        public string RosterPath { get; }
    }

    /// <summary>
    /// 이미 쌓인 명세서에서 정산 업무 인수인계 문서를 만든다.
    ///
    /// [옛 계약과 다른 점] 옛 계약(GenerateSettlementPolicyRulebookAsync)은 DB 연결과
    /// SP 목록·maxDepth를 직접 받아 그 자리에서 DB를 훑었다. 새 계약은 이미 쌓인
    /// output/Procedures의 명세서만 읽고, DB는 코드값 우변 번역에만 선택적으로 쓰인다.
    /// 대상과 순서는 명부(settlement-process.md)가 정하므로 SP 목록을 인자로 받지 않는다.
    /// </summary>
    public interface ISettlementPolicyService
    {
        Task<PolicyDerivationOutcome> GenerateAsync(
            string outputRoot,
            ICodeTableProfiler? profiler,
            string? effort,
            CancellationToken cancellationToken = default);
    }
}
