using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 코퍼스 설정이 "전부 아니면 전무"인지 지킨다 - **반쯤 설정된 상태**를 막는 가드다.
    ///
    /// [실측 - 코퍼스 재료는 셋이고, 계열마다 다르게 반응한다 (2026-08-26 · 2026-09-04)]
    /// 재료는 `output/`, <see cref="CorpusPaths.PriorEdition"/>, <see cref="CorpusPaths.ControlEdition"/>
    /// 셋이다. 아래 표는 앞의 둘로 잰 것이고(2026-08-26 전수 실측), 셋째는 그때 이 가드
    /// 밖에 있었다 - 그래서 빠뜨려도 빨간불이 없고 <c>ProcedureClosureCorpusTests</c>가
    /// **조용히 건너뛰었다**(2026-09-04에 닫았다. 아래 두 번째 시험이 그것이다).
    /// **메인 저장소 안에 만든** 워크트리에 무엇을 거느냐에 따라 이렇게 갈린다. 저장소
    /// 밖에 만든 워크트리는 조상 탐색도 실패하므로 건너뜀이 더 늘어난다.
    ///
    ///   링크 없음  : AxisAGoldenCaseTests 계열 15건 건너뜀 · CoverageMapGolden 요구 2·3 통과
    ///   output만   : 그 15건 통과 · **요구 2·3 건너뜀**  → 총 건너뜀 2
    ///   둘 다      : 전부 통과                            → 총 건너뜀 0
    ///
    /// 계열이 갈리는 이유는 루트 해석 방식이 다르기 때문이다. 15건은 워크트리 안만 보므로
    /// `output/`이 없으면 건너뛴다. 요구 2·3은 <see cref="CorpusPaths.RepoRoot"/>처럼 조상을
    /// 거슬러 오르므로, 워크트리에 아무것도 없으면 **메인 저장소까지 올라가** 재료 둘을 다
    /// 찾는다. 그런데 `output/`만 심링크하면 그 탐색이 워크트리에서 멈추고, 거기엔
    /// <see cref="CorpusPaths.PriorEdition"/>이 없어 그때부터 건너뛴다.
    ///
    /// **즉 `output/`만 거는 것은 15건을 고치면서 다른 2건을 망가뜨린다.** 총 건너뜀이
    /// 15에서 2로 줄어드니 진전처럼 보이지만, 실제로는 과거 판 대조를 조용히 끈 것이다.
    ///
    /// [실제 사고 둘 - 두 세션이 각각 다른 칸에서 당했다]
    ///   • 한 세션은 링크를 아예 안 걸어 건너뜀 15로 브랜치 전체를 돌았고, 그 15를 계획서에
    ///     "환경 조건이지 결함이 아니다 · 워커의 합격선은 실패 0"이라고 명시해 배포했다
    ///     (docs/superpowers/plans/2026-08-26-control-step-code-type.md). 워커들은 지시대로
    ///     확인했고 전부 통과했다 - 관측은 정확했고 판정만 틀렸다. (그 15건은 추출기·
    ///     골든 케이스 계열이라 그 브랜치의 변경 자리와 직접 겹치지는 않았다. 겹치지
    ///     않은 것은 운이지 절차가 아니다 - 틀린 기준선을 배포했다는 사실은 다음번에
    ///     겹칠 때를 막아 주지 못한다.)
    ///   • 다른 세션은 `output/`만 걸어 건너뜀 2를 보고 "코퍼스 연결 정상"으로 판정했고,
    ///     과거 판 대조 요구 둘이 안 도는 채로 브랜치 전체를 통과시켰다
    ///     (docs/known-defects.md의 "건너뜀 2건은 output.bak-2026-08-22 스냅샷 부재로
    ///     인한 사전 존재 스킵" 기록). 그 세션이 따른 계획서가 위 네 곳 중 하나를
    ///     복사해 만들어져 같은 만족 불가능한 조합을 지시하고 있었다.
    ///
    /// 둘 중 **2가 더 악질이다.** 15는 크게 튀어 눈에 띄지만 2는 안 띈다. 게다가 그때까지
    /// 저장소의 지시 **네 곳**(<see cref="CorpusSkip"/>, AGENTS.md의 워크트리 코퍼스 절,
    /// reset-l1-check의 SKILL.md와 corpus-sweep.md)이 `output` **하나만** 걸라 하면서
    /// "건너뜀 0"을 요구했다 - 지시를 정확히 따르면 반드시 2가 나오고, 그 2를 합리화하게
    /// 되는 조합이었다. 개인 부주의가 아니라 지시가 설계한 결과다. 다섯 번째 자리인
    /// AGENTS.md 체크리스트와 여섯 번째인 reset-doc-sync 스킬은 재료를 아예 언급하지 않은
    /// 채 "건너뜀 0"만 요구했다 - 만족 불가능하진 않았지만 방법을 말해 주지 않았다.
    ///
    /// [왜 "아예 없음"은 실패시키지 않는가]
    /// `output/`은 gitignore 대상이고 941M이라 갓 클론한 저장소·CI에는 없는 것이 정상이다.
    /// 없으면 전부 빨개지는 가드는 그 환경을 죽인다(<see cref="CorpusSkip"/> 문서 참고).
    /// 그래서 전건("코퍼스 루트를 찾았다")이 거짓이면 이 가드도 함께 건너뛴다 - 막는 것은
    /// "반쯤"뿐이다. 다만 **메인 저장소 안에 중첩된 워크트리에서는 그 건너뜀 가지에 닿지
    /// 않는다** - 조상 탐색이 메인 저장소를 찾아내기 때문이다. 그 가지는 중첩되지 않은
    /// 체크아웃(CI·새 클론)에서만 실제로 쓰인다.
    /// </summary>
    public class CorpusSetupGuardTests
    {
        [SkippableFact]
        public void CorpusSetup_WhenOutputIsPresent_PriorEditionMustAlsoBePresent()
        {
            var root = CorpusPaths.RepoRoot();

            // 「아예 없음」 - 정직한 상태다. 건너뜀 수가 크게 튀어 눈에 띈다.
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var hasSnapshot = CorpusPaths.PriorEditionExists(root);

            // 스냅샷이 **있으면** 어디서든 단언한다 - 통과할 것이므로 게이트를 깨지 않는다.
            // 건너뛰는 것은 오직 "스냅샷이 없고 그리고 연결된 워크트리도 아닐 때"뿐이다.
            // 그 조합만이 고칠 방법이 없는 환경이다(새 기계에 클론해 output/만 다시 뽑은
            // 체크아웃 - 스냅샷은 과거 판이라 재생성할 수 없다).
            //
            // [왜 "연결된 워크트리가 아니면 건너뛴다"로 하면 안 되는가 - 재리뷰 실측]
            // 그렇게 하면 코퍼스가 완비된 **메인 저장소**에서도 건너뛴다. 그 순간
            // AGENTS.md와 reset-l1-check가 요구하는 "건너뜀 0"이 영구히 불가능해지고,
            // 이 클래스가 막으려는 바로 그 병리 - 고칠 수 없는 상시 건너뜀이 합리화를
            // 길들이는 것 - 를 다른 자리에 새로 만든다.
            Skip.If(
                !hasSnapshot && !CorpusPaths.IsLinkedWorktree(root),
                "연결된 워크트리가 아니고 과거 판 스냅샷도 없다 - 스냅샷은 재생성할 수 " +
                "없으므로 독립 체크아웃에서는 요구하지 않는다.");

            // 「반쯤 있음」 - 성공처럼 보이는 상태다. 여기서 끊는다.
            Assert.True(
                hasSnapshot,
                $"코퍼스가 반쯤 설정됐다 - `output/`은 닿는데 `{CorpusPaths.PriorEdition}/`이 없다. " +
                $"이 상태에서는 과거 판 대조 요구 둘(CoverageMapGoldenTests의 요구 2·3)이 조용히 " +
                "건너뛰는데 총 건너뜀 수는 오히려 줄어 성공처럼 보인다. 심링크를 **둘 다** 걸어라:\n" +
                "  ln -s <메인 저장소>/output output\n" +
                $"  ln -s <메인 저장소>/{CorpusPaths.PriorEdition} {CorpusPaths.PriorEdition}\n" +
                "둘 다 걸면 건너뜀 0이다. 세 단계 표는 AGENTS.md의 워크트리 코퍼스 절에 있다.");
        }

        /// <summary>
        /// 셋째 재료(<see cref="CorpusPaths.ControlEdition"/>)에 대한 같은 시험.
        ///
        /// [왜 별건인가 - 2026-09-04]
        /// 이 재료는 2026-09-03까지 가드 **밖**에 있었다. 빠뜨리면
        /// <c>ProcedureClosureCorpusTests</c>가 조용히 건너뛰는데, 건너뜀 수가 1 늘 뿐이라
        /// 위쪽 「반쯤」보다도 눈에 안 띈다. 그 사이 두 세션이 각각 이 자리에서 서로에게
        /// 틀린 안내를 주고받았다(한쪽은 「재료는 둘」, 다른 쪽은 「셋째가 빠지면 가드가
        /// 막는다」 - 둘 다 틀렸다). 가드가 모르는 재료를 문서만으로 지키게 두면 그런
        /// 일이 반복된다.
        ///
        /// 위 시험과 합치지 않는 이유: 실패 메시지가 「무엇을 걸어야 하는가」를 정확히
        /// 대야 한다. 둘을 한 단언으로 묶으면 어느 재료가 빠졌는지 메시지가 흐려지고,
        /// 그 흐린 메시지가 바로 이 재료를 놓치게 만든 조건이다.
        /// </summary>
        [SkippableFact]
        public void CorpusSetup_WhenOutputIsPresent_ControlEditionMustAlsoBePresent()
        {
            var root = CorpusPaths.RepoRoot();

            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var hasControl = CorpusPaths.ControlEditionExists(root);

            // 위 시험과 같은 규약이다 - 있으면 어디서든 단언하고, 없으면서 연결된
            // 워크트리도 아닐 때만 건너뛴다. 통제군 트리는 그때 그 실행의 산출이라
            // 스냅샷과 마찬가지로 재생성할 수 없다.
            Skip.If(
                !hasControl && !CorpusPaths.IsLinkedWorktree(root),
                "연결된 워크트리가 아니고 통제군 입력 트리도 없다 - 그 트리는 재생성할 수 " +
                "없으므로 독립 체크아웃에서는 요구하지 않는다.");

            Assert.True(
                hasControl,
                $"코퍼스가 반쯤 설정됐다 - `output/`은 닿는데 `{CorpusPaths.ControlEdition}/`이 없다. " +
                "이 상태에서는 `ProcedureClosureCorpusTests`가 조용히 건너뛴다(건너뜀 수는 1만 늘어 " +
                "눈에 띄지 않는다). 심링크를 **셋 다** 걸어라:\n" +
                "  ln -s <메인 저장소>/output output\n" +
                $"  ln -s <메인 저장소>/{CorpusPaths.PriorEdition} {CorpusPaths.PriorEdition}\n" +
                $"  ln -s <메인 저장소>/{CorpusPaths.ControlEdition} {CorpusPaths.ControlEdition}\n" +
                "셋 다 걸면 건너뜀 0이다. 표는 AGENTS.md의 워크트리 코퍼스 절에 있다.");
        }

        [SkippableFact]
        public void CorpusSetup_WhenOutputIsPresent_DefectiveEditionMustAlsoBePresent()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);
            Skip.If(!CorpusPaths.IsLinkedWorktree(root),
                "연결된 워크트리가 아니다 - 가드가 막으려는 것은 워크트리 설정 실수다.");

            Assert.True(
                CorpusPaths.DefectiveEditionExists(root),
                $"`output/`은 있는데 {CorpusPaths.DefectiveEdition}이 없다. " +
                "StepCheckOracleTests가 「결함 판에서 발화한다」를 확인하지 못한 채 " +
                "초록이 된다 - 반쯤 설정된 상태다. " +
                $"ln -s <main>/{CorpusPaths.DefectiveEdition} {CorpusPaths.DefectiveEdition}");
        }
    }
}
