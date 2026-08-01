namespace ReSet.Core.Models;

/// <summary>
/// 검증 파이프라인이 어떤 상태로 끝났는지를 나타낸다.
/// 네 값이 곧 루프의 네 종료 지점이며, 문서 헤더와 배너 표기의 기준이 된다.
/// </summary>
public enum VerificationOutcome
{
    /// <summary>L1 통과 + L2 결함 없음.</summary>
    Passed,

    /// <summary>L1 기계 검증 재시도를 모두 소진했다.</summary>
    L1Exhausted,

    /// <summary>L2 리뷰는 수행됐으나 점수 미달·결함이 남았다.</summary>
    QualityRejected,

    /// <summary>L2 리뷰 호출이 예외로 실패해 검증되지 않았다.</summary>
    ReviewNotRun
}
