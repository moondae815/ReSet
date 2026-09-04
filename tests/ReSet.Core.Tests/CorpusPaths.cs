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
    /// 하나는 주석에 "새로 짜지 않고 재사용한다"고 적어 두고도 복제였다. 코퍼스 설정 가드
    /// (<see cref="CorpusSetupGuardTests"/>)가 네 번째 사본이 될 자리라 여기서 끊는다.
    ///
    /// 테스트 프로젝트에는 이 판정을 조금씩 다르게 하는 헬퍼가 더 있다(널 허용 반환,
    /// 다른 기준 파일 등 - `TryFindRepoRoot`·`CorpusRoot`·`UncoveredCorpusRoot`·
    /// `FindRepositoryRoot`). 그것들은 판정 기준 자체가 달라 이번에 합치지 않았다.
    /// </summary>
    public static class CorpusPaths
    {
        /// <summary>
        /// 과거 판 스냅샷 디렉터리 이름. <c>CoverageMapGoldenTests</c>의 요구 2·3
        /// (과거 판 대비 결함이 늘지 않았는가)이 이것을 읽는다 - 그 두 요구에게는
        /// 스냅샷이 있고 없고가 곧 시험의 의미다.
        ///
        /// `.git/info/exclude`에 `output.bak-*`로 등록돼 있어 `output/`과는 **별개로**
        /// 없을 수 있다. 그 "반쯤 있음" 상태를 <see cref="CorpusSetupGuardTests"/>가 막는다.
        /// </summary>
        public const string PriorEdition = "output.bak-2026-08-22";

        /// <summary>
        /// 통제군 입력 트리 이름. <c>ProcedureClosureCorpusTests</c>가 이 트리의
        /// `Jobs/POQSettleBatch4/raw/prompt-context.md`에서 로스터 12편을 읽고,
        /// <c>LegacyErrorCodeInventionCorpusTests</c>가 <c>RESET_SWEEP_ROOT</c>로
        /// 같은 트리에 자를 대 볼 수 있다.
        ///
        /// <see cref="PriorEdition"/>과 마찬가지로 `.git/info/exclude`의 `output.bak-*`에
        /// 걸려 `output/`과는 <b>별개로</b> 없을 수 있고, 통제군 실행의 산출이라
        /// 재생성할 수 없다.
        ///
        /// [왜 상수로 드는가 - 2026-09-04]
        /// 이 이름은 테스트 두 곳에 문자열로 흩어져 있었고 가드는 이것을 아예 몰랐다.
        /// 그래서 셋째 재료를 빠뜨린 워크트리는 빨간불 없이 <b>조용히 건너뛰었다</b> -
        /// 가드가 막는다는 <see cref="PriorEdition"/>의 보증이 이 재료에는 없었다.
        /// 2026-09-03에 두 세션이 각각 이 자리에서 서로에게 틀린 안내를 주고받았다
        /// (한쪽은 「재료는 둘」, 다른 쪽은 「셋째가 빠지면 가드가 막는다」 - 둘 다 틀렸다).
        /// </summary>
        public const string ControlEdition = "output.bak-stage4-control-20260828";

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

        /// <summary>
        /// <paramref name="root"/>가 **연결된 워크트리**인가(메인 체크아웃이 아니라).
        ///
        /// git은 연결된 워크트리의 `.git`을 디렉터리가 아니라 `gitdir: …` 한 줄짜리
        /// **파일**로 만든다. 그 차이 하나로 판정한다 - 테스트에서 git을 실행하지 않는다.
        ///
        /// [왜 이 구분이 필요한가]
        /// <see cref="PriorEdition"/>은 과거 판 스냅샷이라 **재생성할 수 없다**. `output/`은
        /// CLI를 다시 돌리면 만들어지지만 스냅샷은 그때 그 시점의 산출물이다. 그래서 새 기계에
        /// 클론해 코퍼스를 다시 뽑은 체크아웃은 `output/`만 갖고 스냅샷은 영영 못 갖는다 -
        /// 거기서 <see cref="CorpusSetupGuardTests"/>가 실패하면 고칠 방법이 없는 빨간불이
        /// 되고, 실패 메시지는 존재하지도 않는 "메인 저장소"에서 심링크하라고 안내하게 된다.
        ///
        /// 가드가 막으려는 것은 **워크트리 설정 실수**이지 그 환경이 아니다. 그래서 판정을
        /// 연결된 워크트리로 좁힌다.
        /// </summary>
        public static bool IsLinkedWorktree(string root) =>
            !string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, ".git"));

        /// <summary>
        /// 과거 판 스냅샷이 실제로 닿는가. <paramref name="root"/>는
        /// <see cref="RepoRoot"/>의 결과다.
        /// </summary>
        public static bool PriorEditionExists(string root) =>
            !string.IsNullOrEmpty(root) &&
            Directory.Exists(Path.Combine(root, PriorEdition, "Procedures"));

        /// <summary>
        /// 통제군 입력 트리가 실제로 닿는가.
        ///
        /// 디렉터리 존재가 아니라 <b>소비자가 실제로 읽는 파일</b>로 판정한다 -
        /// <c>ProcedureClosureCorpusTests</c>가 이 파일 하나로 로스터를 만들고, 없으면
        /// 건너뛴다. 가드가 디렉터리만 보면 "링크는 걸렸는데 안이 비었다"를 통과시켜,
        /// 가드는 초록인데 테스트는 건너뛰는 조합이 생긴다 - 이 클래스가 막으려는
        /// 「성공처럼 보이는 반쯤」의 또 다른 모양이다.
        /// </summary>
        public static bool ControlEditionExists(string root) =>
            !string.IsNullOrEmpty(root) &&
            File.Exists(Path.Combine(
                root, ControlEdition, "Jobs", "POQSettleBatch4", "raw", "prompt-context.md"));
    }
}
