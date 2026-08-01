namespace ReSet.Core.Models;

/// <summary>
/// 검증 파이프라인이 어떤 상태로 끝났는지를 나타낸다.
/// 네 값이 곧 루프의 네 종료 지점이며, 문서 헤더와 배너 표기의 기준이 된다.
/// ReviewNotRun을 0번 값(기본값)으로 둔다: default(VerificationOutcome)이거나
/// 역직렬화·생성 시점에 값을 빠뜨린 모든 지점이 "통과"를 자칭하지 않고
/// "검증되지 않음"으로 안전하게 실패하도록 하기 위함이다.
/// </summary>
public enum VerificationOutcome
{
    /// <summary>L2 리뷰 호출이 예외로 실패해 검증되지 않았다. (기본값)</summary>
    ReviewNotRun,

    /// <summary>L1 기계 검증 재시도를 모두 소진했다.</summary>
    L1Exhausted,

    /// <summary>L2 리뷰는 수행됐으나 점수 미달·결함이 남았다.</summary>
    QualityRejected,

    /// <summary>L1 통과 + L2 결함 없음.</summary>
    Passed
}
