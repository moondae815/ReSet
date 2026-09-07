using System;
using System.IO;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실제 산출물 코퍼스의 경로 판정을 한 곳에 모은다.
    ///
    /// [왜 모으는가 - 2026-08-26 실측]
    /// 이 판정("output/ 디렉터리가 아니라 이 게이트가 아는 실물 SP 하나가 실제로 있는가")이
    /// <see cref="CoverageMapGoldenTests"/>·<c>CoverageMapProbeTests</c>·
    /// <c>MachineTableExpansionCorpusTests</c> 세 곳에 글자 그대로 복제돼 있었다. 셋 중
    /// 하나는 주석에 "새로 짜지 않고 재사용한다"고 적어 두고도 복제였다.
    ///
    /// [2026-09-07 — 재료가 하나만 남았다]
    /// 사람이 과거 판 코퍼스(`output.bak-*` 여섯)를 지우기로 결정해 `PriorEdition`·
    /// `ControlEdition`·`DefectiveEdition` 상수와 그 존재 판정, 그리고 그것들을 쓰던
    /// 시험들을 함께 걷어냈다. `IsLinkedWorktree`도 코퍼스 설정 가드 전용이라 같이 갔다.
    /// 무엇을 잃었는지는 docs/audit-reports/2026-09-07-과거판-코퍼스-폐기.md 에 있다.
    ///
    /// 테스트 프로젝트에는 이 판정을 조금씩 다르게 하는 헬퍼가 더 있다(널 허용 반환,
    /// 다른 기준 파일 등 - `TryFindRepoRoot`·`CorpusRoot`·`UncoveredCorpusRoot`·
    /// `FindRepositoryRoot`). 그것들은 판정 기준 자체가 달라 이번에 합치지 않았다.
    /// </summary>
    public static class CorpusPaths
    {
        /// <summary>
        /// 코퍼스가 사는 저장소 루트. 못 찾으면 빈 문자열.
        ///
        /// [왜 "output/이 있다"로 판정하지 않는가 - 2026-08-24·08-25 실측]
        /// "output/ 디렉터리를 가진 조상"까지만 올라가면 <c>tests/ReSet.Core.Tests/bin/Debug/
        /// net10.0/output/</c>이 먼저 걸린다. 거기에는 다른 테스트(<c>DependencyAnalysisOrchestratorTests</c>
        /// 류)가 CWD 상대경로로 남긴 스크래치 산출물 <c>dbo.USP_Root</c> 1건이 있다. 그 얕은
        /// 자리에서 멈추면 실물 14 SP 코퍼스 대신 그 스크래치를 재고 "객체 1 · 트랜잭션 합 0 ·
        /// SET 합 0"을 초록으로 찍는다 - **건너뜀도 실패도 아닌 조용한 오측이라 더 위험하다.**
        /// 그래서 이 게이트가 아는 실물 SP 하나가 실제로 있는지로 판정을 좁힌다.
        /// </summary>
        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(
                       dir.FullName, "output", "Procedures",
                       "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", "raw", "metadata.json")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? string.Empty;
        }
    }
}
