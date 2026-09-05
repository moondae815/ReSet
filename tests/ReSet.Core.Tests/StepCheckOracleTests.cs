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
    /// [왜 번들 합계가 아니라 S07.md 를 직접 잠그는가 - 회귀 2026-09-05 라운드 1]
    /// 최초 판은 `defectiveHits > 0`(번들 14개 파일의 발화 합계)만 잠갔다. 이 조건은
    /// 너무 느슨해서 실제 회귀를 가렸다 - 리뷰어가 파일별로 재보니 S07.md(감사가
    /// 🔴로 매긴 그 자리, `ConsistencyReport.md:138`)는 **0건**이었는데도 S08.md가
    /// 1건을 내 합계가 0보다 커서 초록이 됐다. 그래서 대표 자리(S07.md)를 직접
    /// 못박는다 - 다른 파일의 발화가 이 자리의 회귀를 가리지 못하게 한다.
    ///
    /// [왜 `> 0`이 아니라 `== 10`인가 - 라운드 2]
    /// 라운드 1은 S07 에서 17건을 냈지만, 리뷰어가 하나씩 갈라보니 **7건이 무관한
    /// 오탐**이었다(U1·U2·U3~U6·U12·U13·U17·U18 - 실제로는 자기 자리에 진짜 DML 이
    /// 있는 정상 완료 구간). 진짜 결함 10건(갱신 4·5·6·7·8·9·10·11·14·15)이
    /// `ConsistencyReport.md:138`의 "18개 갱신 중 10개의 상수·계수·부호·반올림
    /// 자릿수와 UDF 인자가 지시서에 없다"와 **정확히 일치한다** - 이 10은 우리가
    /// 만든 수가 아니라 감사가 독립적으로 센 수다. `> 0`은 S08 하나만으로도
    /// 통과하므로 S07 자신의 회귀(예: 10건 중 일부가 다시 죽는 것)를 잡지 못한다 -
    /// 그래서 정확한 수를 못박는다. 이 수가 흔들리면 코드가 아니라
    /// `ConsistencyReport.md:138`(감사)을 먼저 재확인해야 한다는 뜻이다.
    ///
    /// 실측(2026-09-05, 이 파일이 가진 알고리즘 그대로 <c>OmissionCommentScanner.Scan</c>을
    /// 번들의 각 `*.md`와 현행 계획서에 파일 단위로 직접 돌려 잰 값. S08 은 감사가
    /// 별도로 낸 기준값이 없어 `> 0`만 둔다 - 총합 수치도 코드에 잠그지 않는다.
    /// 스캐너를 넓히면 늘 수 있고 그것은 개선이다):
    ///
    ///   S01~S06, S09~S16 → 0
    ///   S07               → 10   (감사 🔴 · ConsistencyReport.md:138의 「18개 중 10개」와 정확히 대응)
    ///   S08               → 11   (기준값 없음 - U1·U2·U4·U5·U7·U8·U9·U10·U13·U14·U15)
    ///   번들 합계          → 21
    ///   현행 판            → 0
    /// </summary>
    public class StepCheckOracleTests
    {
        [SkippableFact]
        public void OmissionScanner_FiresExactlyOnAuditedDefectCount_S07()
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

            // 10은 우리가 만든 수가 아니라 감사가 독립적으로 센 수다
            // (ConsistencyReport.md:138, "18개 갱신 중 10개"). 이 값이 어긋나면
            // 먼저 그 감사 기록을 다시 확인하라 - 코드를 임의로 조정하지 마라.
            Assert.Equal(10, s07Hits);
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
