using System.IO;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 현행 판에서 침묵하는지만 남은 자리.
    ///
    /// [2026-09-07 — 이 파일이 무엇이었고 무엇을 잃었는가]
    /// 원래 이 파일의 계약은 <b>고정 오라클 두 판에 검사를 걸어 판정이 갈리는지</b>였다.
    /// 결함이 있다고 감사가 판정한 판에서 발화하고 현행 판에서 침묵해야 비로소 그 검사가
    /// 무언가를 가른다 - 발화 수·통과 수는 활동이지 효력이 아니기 때문이다.
    ///
    /// 사람이 과거 판 코퍼스 전부를 지우기로 결정하면서(2026-09-07) <b>발화하는 쪽 판이
    /// 사라졌다.</b> 지워진 재료와 그 재료가 잠그던 것:
    ///
    ///   • `output.bak-batch1-preregen-20260904`(감사가 🔴로 매긴 결함 판)
    ///       - `OmissionScanner_FiresExactlyOnAuditedDefectCount_S07` — S07.md 발화 **정확히 10건**.
    ///         그 10은 우리가 만든 수가 아니라 감사가 독립적으로 센 수였다
    ///         (`ConsistencyReport.md:138`의 「18개 갱신 중 10개」). 이 자리의 회귀를
    ///         다른 파일의 발화가 가리지 못하게 하던 유일한 못이다.
    ///       - `OmissionScanner_FiresAcrossDefectiveBundle` — 번들 전체 발화 &gt; 0(완전 실효 탐지).
    ///       - `SetExpressionCheck_FiresOnDefectiveBundle_AndIsSilentOnCurrentPlan` — 결함판 12 · 현행판 0.
    ///   • `output/Jobs/POQSettleProc1·3·9`(반복 생성 표본)
    ///       - `SetExpressionCheck_StaysSilentWhenGeneratedAliasDiffersFromSpec_*` — 별칭만 다르게
    ///         정확히 구현한 자리에서 침묵하는가(Fix Round 1 Critical 오탐 넷의 회귀 잠금).
    ///       - `SetExpressionCheck_FiresWhenHardFactTokensAreMissing_POQSettleProc9S06Ordinal10` —
    ///         축 −1(하드 사실 토큰이 빠졌는데 별칭.컬럼이 우연히 걸려 침묵하던 회귀).
    ///
    /// <b>대체 표본은 없다.</b> 결함 판은 그 시점의 산출물이라 재생성으로 안 나오고,
    /// Proc 표본은 다시 뽑으면 새 표본이지 그 표본이 아니다.
    ///
    /// <b>그래서 아래 하나만 남은 것을 「효력이 잠겨 있다」로 읽지 마라.</b> 검사가 통째로
    /// 죽어도 이 시험은 초록이다(죽은 검사는 아무 데서도 발화하지 않으므로 현행 판에서도
    /// 조용하다). 이 시험이 잠그는 것은 <b>새 소음이 생기지 않는 것</b>뿐이고, 갈리는지는
    /// 이제 아무도 안 잰다. 경위: docs/audit-reports/2026-09-07-과거판-코퍼스-폐기.md
    /// </summary>
    public class StepCheckOracleTests
    {
        [SkippableFact]
        public void OmissionScanner_IsSilentOnCurrentPlan()
        {
            var root = CorpusPaths.RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var planPath = Path.Combine(
                root, "output", "Jobs", "POQSettleBatch1", "docs", "BatchMigrationPlan.md");
            Skip.If(!File.Exists(planPath), CorpusSkip.Reason);

            var currentHits = OmissionCommentScanner.Scan(File.ReadAllText(planPath)).Count;

            Assert.Equal(0, currentHits);
        }
    }
}
