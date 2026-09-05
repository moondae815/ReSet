using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 고정 오라클 두 판에 검사를 걸어 <b>판정이 갈리는지</b> 잠근다.
    ///
    /// [왜 발화 수가 아니라 두 판인가] 발화 수와 통과 수는 활동이지 효력이 아니다. 결함이
    /// 있다고 감사가 판정한 판에서 발화하고, 현행 판에서 침묵해야 비로소 그 검사가
    /// 무언가를 가른다고 말할 수 있다.
    ///
    /// [왜 조용히 통과시키지 않는가] `if (없으면) return;`으로 두면 코퍼스가 없는
    /// 워크트리에서 단언이 한 줄도 안 돌면서 초록이 된다 - <see cref="CorpusSkip"/>가
    /// 기록한 2026-08-23 사고가 정확히 그것이고, 다른 세션의 parallel-sdd 실행이
    /// 그 통과를 믿었다. 그래서 Skip 으로 드러낸다. 완료 기준이 「건너뜀 0」이므로
    /// 심링크를 빠뜨리면 기준이 자동으로 실패한다.
    ///
    /// [왜 번들 합계가 아니라 S07.md 를 직접 잠그는가 - 회귀 2026-09-05]
    /// 최초 판은 `defectiveHits > 0`(번들 14개 파일의 발화 합계)만 잠갔다. 이 조건은
    /// 너무 느슨해서 실제 회귀를 가렸다 - 리뷰어가 파일별로 재보니 S07.md(감사가
    /// 🔴로 매긴 그 자리, `ConsistencyReport.md:138`)는 **0건**이었는데도 S08.md가
    /// 1건을 내 합계가 0보다 커서 초록이 됐다. 그래서 대표 자리(S07.md)를 직접
    /// 못박는다 - 다른 파일의 발화가 이 자리의 회귀를 가리지 못하게 한다.
    ///
    /// 실측(2026-09-05, 커밋 0623a25c 기준 픽스 라운드 1 - 이 클래스가 가진 정확한
    /// 알고리즘으로 <c>OmissionCommentScanner.Scan</c>을 번들의 각 `*.md`와 현행
    /// 계획서에 파일 단위로 직접 돌려 잰 값. 총합 수치를 코드에 잠그지는 않는다 -
    /// 스캐너를 넓히면 늘 수 있고 그것은 개선이다):
    ///
    ///   S01~S06, S09~S16 → 0
    ///   S07               → 17   (감사 🔴 · ConsistencyReport.md:138)
    ///   S08               → 11   (U1·U2·U4·U5·U7·U8·U9·U10·U13·U14·U15 연쇄 복원)
    ///   번들 합계          → 28
    ///   현행 판            → 0
    /// </summary>
    public class StepCheckOracleTests
    {
        [SkippableFact]
        public void OmissionScanner_FiresOnAuditedDefect_S07()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);
            Skip.If(!CorpusPaths.DefectiveEditionExists(root),
                $"{CorpusPaths.DefectiveEdition}이 없어 건너뜀 - " +
                $"ln -s <main>/{CorpusPaths.DefectiveEdition} {CorpusPaths.DefectiveEdition}");

            var s07Path = Path.Combine(
                root, CorpusPaths.DefectiveEdition, "Jobs", "POQSettleBatch1", "agent", "steps", "S07.md");
            Skip.If(!File.Exists(s07Path), CorpusSkip.Reason);

            var s07Hits = OmissionCommentScanner.Scan(File.ReadAllText(s07Path)).Count;

            // 번들 합계가 아니라 이 파일 하나를 직접 잠근다 - 감사가 🔴로 매긴
            // 대표 결함(갱신 18개 중 10개 소실)이 정확히 이 자리다.
            Assert.True(s07Hits > 0,
                "S07.md(감사 🔴 · ConsistencyReport.md:138)에서 발화하지 않았다 - " +
                $"검사가 그 대표 결함을 가르지 못한다 (발화 {s07Hits})");
        }

        [SkippableFact]
        public void OmissionScanner_IsSilentOnCurrentPlan()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var planPath = Path.Combine(
                root, "output", "Jobs", "POQSettleBatch1", "docs", "BatchMigrationPlan.md");
            Skip.If(!File.Exists(planPath), CorpusSkip.Reason);

            var currentHits = OmissionCommentScanner.Scan(File.ReadAllText(planPath)).Count;

            Assert.Equal(0, currentHits);
        }

        [SkippableFact]
        public void OmissionScanner_FiresAcrossDefectiveBundle()
        {
            // 번들 전체 합계는 대표 자리(S07)를 대신하지 못한다(위 클래스 주석 참고) -
            // 그래도 "검사가 결함 판 전체에서 무엇도 못 찾는" 완전 실효는 이 시험이
            // 잡는다.
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);
            Skip.If(!CorpusPaths.DefectiveEditionExists(root),
                $"{CorpusPaths.DefectiveEdition}이 없어 건너뜀 - " +
                $"ln -s <main>/{CorpusPaths.DefectiveEdition} {CorpusPaths.DefectiveEdition}");

            var bundleDir = Path.Combine(
                root, CorpusPaths.DefectiveEdition, "Jobs", "POQSettleBatch1", "agent", "steps");

            var defectiveHits = Directory.GetFiles(bundleDir, "*.md")
                .Sum(f => OmissionCommentScanner.Scan(File.ReadAllText(f)).Count);

            Assert.True(defectiveHits > 0,
                $"결함 판 전체에서 발화하지 않았다 - 검사가 무엇도 가르지 못한다 (발화 {defectiveHits})");
        }
    }
}
