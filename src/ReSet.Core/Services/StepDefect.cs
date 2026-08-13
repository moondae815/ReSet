namespace ReSet.Core.Services
{
    /// <summary>
    /// 단계 하나에 대해 하한 검사가 낸 판정의 종류.
    ///
    /// 둘을 가르는 이유: 실측에서 14개 단계 중 13개에 "품질 미달" 배너가 붙었는데,
    /// 그 13개는 섹션이 부실한 것이 아니라 대조할 재료가 목차에 없어 검사가 돌지
    /// 못한 것이었다. 두 사실을 같은 배너로 내면 읽는 사람이 어느 쪽인지 알 수
    /// 없고, 배너가 대부분의 단계에 붙어 변별력도 사라진다.
    /// </summary>
    public enum StepDefectKind
    {
        /// <summary>본문이 최소 요건을 못 채웠다. 재생성으로 고칠 수 있다.</summary>
        QualityFloor,

        /// <summary>대조할 재료가 목차에 없어 검사를 실행하지 못했다. 재생성으로 고쳐지지 않는다.</summary>
        Unverifiable,

        /// <summary>
        /// 모든 시도가 빈 응답을 돌려줘 섹션 본문 자체가 없다. 하한 검사가 돈 적이 없다 -
        /// 검사할 것이 애초에 없었기 때문이다.
        ///
        /// QualityFloor와 갈라 두는 이유는 Unverifiable을 갈라 둔 이유와 같다. 저것은
        /// "검사가 돌았고 떨어졌다"이고 이것은 "검사가 돌 수 없었다"인데, 합치면
        /// 검증률 집계에서 본문 없는 단계가 검증됨으로 세어진다 - `단계 검증: 19/19`
        /// 아래에 "이 단계는 생성에 실패했습니다"라고 적힌 섹션이 남는다.
        /// </summary>
        GenerationFailed,
    }

    /// <param name="Reason">"{Code} (사유)" 형식의 표시 문자열. 배너가 그대로 싣는다.</param>
    public sealed record StepDefect(StepDefectKind Kind, string Reason);
}
