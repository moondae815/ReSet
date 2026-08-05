namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도 루프가 비정상으로 끝난 이유. 정상 소진에는 해당하는 값이 없다 —
    /// 그 경우 호출부가 null을 넘긴다.
    /// </summary>
    public enum RetryAbortReason
    {
        /// <summary>AI 생성 호출이 예외를 던졌거나 빈 응답을 반환했다.</summary>
        GenerationFailed,

        /// <summary>L1 기계 검증 재시도를 모두 소진했다.</summary>
        L1Exhausted,

        /// <summary>L2 리뷰 호출이 실패했다.</summary>
        ReviewFailed
    }

    /// <summary>
    /// 구제가 일어난 경위. 세 값은 항상 함께 움직이므로 하나로 묶는다 —
    /// 선택 인자 셋으로 흩어 놓으면 한둘만 넘기는 호출부가 생긴다.
    /// </summary>
    public sealed record RescueContext(RetryAbortReason Reason, int AbortedAttempt, int AdoptedAttempt);
}
