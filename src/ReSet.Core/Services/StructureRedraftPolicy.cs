namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도가 계획서를 개선하지 못할 때 목차(PlanStructure)를 다시 세울지 결정한다.
    ///
    /// 이 클래스가 존재하는 이유: 통합 배치 경로는 목차를 재시도 루프 밖에 고정한다.
    /// Actor가 회차마다 백지에서 다시 쓰기 때문에(CriticFeedbackLog 참조) 목차가 없으면
    /// 회차마다 문서 뼈대가 달라져 누적 피드백이 엉뚱한 자리에 붙는다. 그 대가로,
    /// 목차 자체가 원인인 결함 — 스텝 누락, 청킹 불가 스텝을 청킹으로 배치 — 은
    /// 몇 번을 재시도해도 고쳐지지 않고 재시도 예산만 소진했다. 이 클래스가 그
    /// 상태를 관측해 탈출구를 연다.
    ///
    /// 정체의 정의를 새로 만들지 않는다. BestAttempt.TryRecord가 이미 "최고점을
    /// 갱신했는가"를 엄격 부등호로 판정해 소유하므로 그 반환값을 그대로 받는다.
    ///
    /// L3 사용자 지시는 이 정책을 거치지 않는다. 사용자가 구조 변경을 명시적으로
    /// 요청하면 상한과 무관하게 수행한다 — 사용자의 지시를 자동화 예산으로 막지 않는다.
    /// </summary>
    public sealed class StructureRedraftPolicy
    {
        /// <summary>이미 재수립을 1회 소비했는가.</summary>
        public bool Consumed { get; private set; }

        /// <summary>
        /// 이번 회차가 최고점을 갱신하지 못했고 아직 재수립을 쓰지 않았다면 true를
        /// 돌려주고 소비를 기록한다.
        ///
        /// 미갱신 1회로 발동한다. 2회 연속을 요구하면 기본 예산(MaxL2Attempts=2 →
        /// 총 3회)에서 발동할 자리가 없다. 1차는 후보가 없어 항상 갱신되므로,
        /// 2차의 갱신 실패가 "재시도가 개선을 못 냈다"의 첫 증거다.
        /// </summary>
        public bool TryConsume(bool improvedThisAttempt)
        {
            if (Consumed || improvedThisAttempt)
            {
                return false;
            }

            Consumed = true;
            return true;
        }
    }
}
