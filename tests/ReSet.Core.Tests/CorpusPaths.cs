using System;
using System.IO;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실제 산출물 코퍼스의 경로 판정을 한 곳에 모은다.
    ///
    /// [왜 모으는가 - 2026-08-26 실측]
    /// 이 판정이 <see cref="CoverageMapGoldenTests"/>·<c>CoverageMapProbeTests</c>·
    /// <c>MachineTableExpansionCorpusTests</c> 세 곳에 글자 그대로 복제돼 있었다. 셋 중
    /// 하나는 주석에 "새로 짜지 않고 재사용한다"고 적어 두고도 복제였다.
    ///
    /// [2026-09-07 - 정박을 재생성 산출물에서 커밋된 표지로 옮겼다]
    /// 옛 판은 루트를 <c>output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/raw/
    /// metadata.json</c> 하나가 있는 조상으로 잡았다. <b>그 파일은 재생성 산출물이다.</b>
    /// 재생성 창에 테스트가 돌면 루트가 빈 문자열이 되고, 그것을 전건으로 쓰는
    /// <c>Skip.If</c> 17 자리가 통째로 조용해진다. 저장소 밖 워크트리에서 그 파일 하나만
    /// 없애고 재니 <b>건너뜀 20 · 실패 0 · 「통과!」</b>였다 - 남은 탐지기가 「건너뜀 0」
    /// 하나뿐인데 이 고장이 만드는 것이 바로 그 건너뜀이라, 탐지기가 제 폭발 반경 안에
    /// 있었다. 판독은 docs/audit-reports/2026-09-07-실행의미-표-카나리아-판독.md §8-1,
    /// 조건과 되돌림은 같은 날 사전선언에 있다.
    ///
    /// 그래서 판정을 <b>두 단계로 가른다</b>:
    /// <list type="number">
    ///   <item>루트는 <c>ReSet.slnx</c>(추적되는 커밋된 파일)로 잡는다 - 새로 짜지 않고
    ///         <see cref="RepoPaths.FindRepoRoot()"/>에 위임한다.</item>
    ///   <item>「코퍼스가 있는가」는 그 루트 <b>아래에서 따로</b> 판정한다
    ///         (<see cref="HasCorpus"/>).</item>
    /// </list>
    /// 판정도 셋으로 갈린다 - <b>코퍼스가 없으면 건너뜀, 코퍼스는 있는데 재료 하나가
    /// 없으면 실패.</b> 옛 판은 둘째를 첫째로 접어 놓았다.
    ///
    /// 부수 효과로 중첩 워크트리 탈출도 막힌다. 옛 판은 워크트리에 자기 <c>output</c>이
    /// 없으면 메인 체크아웃까지 올라갔다(2026-09-06 실측,
    /// <c>SettlementPolicyServiceTests</c> 주석). <c>ReSet.slnx</c>는 모든 워크트리에
    /// 있으므로 탐색이 자기 워크트리에서 멈춘다.
    ///
    /// 테스트 프로젝트에는 루트를 조금씩 다르게 잡는 헬퍼가 더 있다
    /// (<c>CorpusRoot</c>·<c>UncoveredCorpusRoot</c>·<c>FindRepositoryRoot</c>·두 대입
    /// 추출기 시험의 사설 <c>RepoRoot</c>). 그것들은 <c>src/ReSet.Core</c>나
    /// <c>ReSet.slnx</c>를 이미 자로 쓰고 있어 <b>재생성이 덮지 않는다</b> - 같은 고장이
    /// 아니라서 이번에 합치지 않았다.
    /// </summary>
    public static class CorpusPaths
    {
        /// <summary>① 코퍼스가 사는 저장소 루트. 커밋된 표지로만 잡는다.</summary>
        public static string RepoRoot() => RepoPaths.FindRepoRoot();

        /// <summary>
        /// ① 의 시작 디렉터리를 받는 갈래. 임시 트리로 고정하는 시험이 쓴다
        /// (<see cref="CorpusPathsTests"/>).
        /// </summary>
        public static string RepoRoot(string startDirectory) =>
            RepoPaths.FindRepoRoot(startDirectory);

        /// <summary>
        /// ② 그 루트 아래에 코퍼스가 있는가. 자는 <c>output/Procedures</c> 디렉터리다 -
        /// 재생성은 객체별 파일을 다시 쓸 뿐 이 디렉터리를 지우지 않는다. 개별 재료가
        /// 없는 것은 <b>건너뜀이 아니라 실패</b>로 두어야 하므로 여기서 보지 않는다.
        /// </summary>
        public static bool HasCorpus(string repoRoot) =>
            !string.IsNullOrEmpty(repoRoot)
            && Directory.Exists(Path.Combine(repoRoot, "output", "Procedures"));

        /// <summary>
        /// 코퍼스 단언이 쓰는 자리. 코퍼스가 없으면 빈 문자열이라 <c>Skip.If</c>가 건다.
        /// 코퍼스가 있으면 루트를 준다 - 그 아래 재료가 없으면 시험이 실패해야 한다.
        /// </summary>
        public static string RepoRootIfCorpusPresent() =>
            RepoRootIfCorpusPresent(AppContext.BaseDirectory);

        /// <inheritdoc cref="RepoRootIfCorpusPresent()"/>
        public static string RepoRootIfCorpusPresent(string startDirectory)
        {
            var root = RepoRoot(startDirectory);
            return HasCorpus(root) ? root : string.Empty;
        }
    }
}
