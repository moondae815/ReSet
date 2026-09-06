using System;
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>정책서의 한 단계 - 사람이 붙인 업무 이름과 그 단계에 속한 프로시저들.</summary>
    public sealed record PolicyStage(string Title, IReadOnlyList<string> Procedures);

    /// <summary>
    /// 사람이 소유하는 정산 프로세스 명부.
    ///
    /// [왜 파일인가] 업무 순서는 코드 안에 없다 - 코퍼스 전체의 EXEC 간선은 2개뿐이고
    /// 11개 SP가 호출 그래프 밖이다(2026-09-06 실측). 쓰기→읽기 그래프는 허브 테이블
    /// TSettleMst 하나 때문에 간선 99개의 거의 완전 그래프가 되어 순서를 주지 못한다.
    /// 순서는 외부 스케줄러가 쥔 지식이므로 사람에게 받아 파일에 남긴다.
    ///
    /// [왜 H2가 목차인가] 인수인계 문서의 독자는 SP 이름을 모른다. 사람이 붙인 업무
    /// 이름이 그대로 목차가 되어야 읽힌다.
    /// </summary>
    public sealed record SettlementProcessRoster(
        IReadOnlyList<PolicyStage> Stages,
        IReadOnlyList<string> Excluded)
    {
        /// <summary>초안이 아직 사람 손을 안 탔음을 알리는 표식. 이게 남아 있으면 생성을 중단한다.</summary>
        public const string PlaceholderMarker = "단계 이름을 붙여 주세요";

        /// <summary>의도한 제외를 적는 섹션. 이 섹션이 있어야 「조용한 누락」과 「명시적 제외」가 갈린다.</summary>
        public const string ExcludedHeading = "## 제외";

        public static SettlementProcessRoster Empty { get; } =
            new(Array.Empty<PolicyStage>(), Array.Empty<string>());

        /// <summary>단계에 실린 프로시저 전량(제외 목록은 빼고).</summary>
        public IEnumerable<string> AllStagedProcedures()
        {
            foreach (var stage in Stages)
            {
                foreach (var procedure in stage.Procedures)
                {
                    yield return procedure;
                }
            }
        }
    }
}
