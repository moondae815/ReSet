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
    /// </summary>
    public class StepCheckOracleTests
    {
        [SkippableFact]
        public void OmissionScanner_FiresOnDefectiveBundle_AndIsSilentOnCurrentPlan()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);
            Skip.If(!CorpusPaths.DefectiveEditionExists(root),
                $"{CorpusPaths.DefectiveEdition}이 없어 건너뜀 - " +
                $"ln -s <main>/{CorpusPaths.DefectiveEdition} {CorpusPaths.DefectiveEdition}");

            var bundleDir = Path.Combine(
                root, CorpusPaths.DefectiveEdition, "Jobs", "POQSettleBatch1", "agent", "steps");
            var planPath = Path.Combine(
                root, "output", "Jobs", "POQSettleBatch1", "docs", "BatchMigrationPlan.md");
            Skip.If(!File.Exists(planPath), CorpusSkip.Reason);

            var defectiveHits = Directory.GetFiles(bundleDir, "*.md")
                .Sum(f => OmissionCommentScanner.Scan(File.ReadAllText(f)).Count);
            var currentHits = OmissionCommentScanner.Scan(File.ReadAllText(planPath)).Count;

            // 실측(2026-09-05, 커밋 8c00813e): 결함 판 7건 · 현행 판 0건.
            // 발화 수 자체를 못 박지 않는 이유: 스캐너를 넓히면 결함 판 수가 늘 수 있고
            // 그것은 개선이다. 잠그는 것은 「갈린다」이지 특정 수가 아니다.
            Assert.True(defectiveHits > 0,
                $"결함 판에서 발화하지 않았다 - 검사가 무엇도 가르지 못한다 (발화 {defectiveHits})");
            Assert.Equal(0, currentHits);
        }
    }
}
