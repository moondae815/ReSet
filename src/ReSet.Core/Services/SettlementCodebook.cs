using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>코드 상수 하나가 마스터 데이터에서 발견된 자리. 행 전체를 담는다 - 설명 컬럼을 추정하지 않는다.</summary>
    public sealed record CodebookMatch(string Table, IReadOnlyDictionary<string, string> Row);

    /// <summary>사전의 한 항목. Matches가 비면 「의미 미상」으로 문서에 나간다.</summary>
    public sealed record CodebookEntry(
        string Value,
        string? Column,
        IReadOnlyList<string> Procedures,
        bool MatchEligible,
        IReadOnlyList<CodebookMatch> Matches);

    /// <summary>
    /// 정책서가 실을 수 있는 코드값의 전부.
    ///
    /// [왜 사전이 문서를 구속하는가] 정책서에는 이 사전에 있는 번역만 실린다. 그러면
    /// 완성된 문서의 매핑을 사전과 대조해 지어낸 번역을 잡을 수 있고, 검사가 보는 파일
    /// (정책서)과 기준이 되는 파일(사전)이 달라 오라클이 순환하지 않는다.
    /// </summary>
    public sealed record SettlementCodebook(
        IReadOnlyList<CodebookEntry> Entries,
        IReadOnlyList<string> SpecUnlistedConstants);
}
