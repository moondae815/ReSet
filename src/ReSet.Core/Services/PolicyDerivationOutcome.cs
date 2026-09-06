using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정산 정책 도출 한 회차의 결과.
    ///
    /// [왜 이 레코드가 필요한가] 옛 서비스는 rulebook 문자열 하나만 돌려주어 호출부가
    /// 결함 건수·코드값 번역 현황을 알 방법이 없었다. 배치·TUI 양쪽이 같은 요약을
    /// 화면에 내야 하므로, 그 요약을 여기 한 곳에 모은다.
    /// </summary>
    public sealed record PolicyDerivationOutcome(
        string PolicyPath,
        string CodebookPath,
        IReadOnlyList<PolicyDefect> Defects,
        int CodeValuesTranslated,
        int CodeValuesUnmatched,
        int CodeValuesSkippedShort,
        bool ProfilingRan);
}
