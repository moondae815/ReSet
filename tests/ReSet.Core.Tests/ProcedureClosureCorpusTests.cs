using System;
using System.IO;
using System.Linq;
using ReSet.Cli;
using Xunit;
using Xunit.Abstractions;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실물 매니페스트로 폐포를 잰다.
    ///
    /// [왜 단위 픽스처로 부족한가] 픽스처는 내가 만든 트리에서 동작한다는 것만 증명한다.
    /// 실제 SpecPath 는 `../dbo.X/docs/Spec.md` 이고 `Summary → EXTRA → Summary` 가
    /// 실제 순환이며, 매니페스트는 BOM 이 붙은 UTF-8 이다 - 셋 다 픽스처가 흉내 낸 것이지
    /// 실물이 아니다.
    ///
    /// [왜 이름까지 못박는가] 개수만 보면 「둘이 빠지고 다른 둘이 들어와도」 통과한다.
    /// 이 검사가 지키는 것은 개수가 아니라 <b>어느 프로시저가 재료가 되는가</b>다.
    /// </summary>
    public class ProcedureClosureCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public ProcedureClosureCorpusTests(ITestOutputHelper output) => _output = output;

        // ------------------------------------------------------------------
        // Batch4Roster_ClosesFromTwelveToFourteen — 2026-09-07 폐기
        // ------------------------------------------------------------------
        //
        // 진입점 12편이 폐포로 14편이 되는지, 그리고 **더해진 둘이 정확히 어느
        // 프로시저인지**(SUMMARY_EXTRA · Summary_AcqManual)를 못박던 자다. 개수만 보면
        // 「둘이 빠지고 다른 둘이 들어와도」 통과하므로 이름까지 잠갔었다.
        //
        // 12편 로스터는 `output.bak-stage4-control-20260828`(얼어붙은 통제군 입력)의
        // `Jobs/POQSettleBatch4/raw/prompt-context.md`에서 뽑았다. 사람이 그 판을 지우기로
        // 결정했다(2026-09-07).
        //
        // **대체 표본을 찾아봤고 없다.** 현행 `output/Jobs/POQSettleBatch4/raw/
        // prompt-context.md`는 `Filename:` 14행이고 그 안에 이미
        // `dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA`·`dbo.UP_Util_Settle_Summary_AcqManual`이
        // 들어 있다(실측) - 즉 현행 로스터는 **폐포를 이미 적용한 결과**라 12→14 전이를
        // 다시 잴 수 없다. 진입점 12편을 손으로 적어 넣는 것은 오라클을 우리가 만드는
        // 것이라 하지 않는다.
        //
        // 아래 `Closure_NeverAddsAFunctionSpec`은 현행 코퍼스만으로 서므로 남는다 -
        // 다만 그것은 **더하지 않는 것**을 잠글 뿐 **무엇을 더하는가**는 안 잠근다.
        // 경위: docs/audit-reports/2026-09-07-과거판-코퍼스-폐기.md

        /// <summary>
        /// 함수는 30건 참조되지만 하나도 더해지면 안 된다(설계서 §2).
        /// </summary>
        [SkippableFact]
        public void Closure_NeverAddsAFunctionSpec()
        {
            var repoRoot = TryFindRepoRoot();
            Skip.If(string.IsNullOrEmpty(repoRoot), CorpusSkip.Reason);

            var outputRoot = Path.Combine(repoRoot!, "output");
            var proceduresDirectory = Path.Combine(outputRoot, "Procedures");
            Skip.IfNot(Directory.Exists(proceduresDirectory), CorpusSkip.Reason);

            var everyProcedure = Directory.GetDirectories(proceduresDirectory)
                .Select(d => Path.Combine("Procedures", Path.GetFileName(d), "docs", "Spec.md"))
                .Where(relative => File.Exists(Path.Combine(outputRoot, relative)))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            var closure = BatchStepCatalog.CloseOverProcedureReferences(outputRoot, everyProcedure);

            Assert.DoesNotContain(
                closure.SpecPaths,
                p => p.Replace(Path.DirectorySeparatorChar, '/').Contains("/Functions/", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// RepoPaths.FindRepoRoot()는 ReSet.slnx가 없으면 null이 아니라 예외를
        /// 던진다(CancellationPolicyScanner.cs:242-256) - 이 클래스가 예전에 쓰던
        /// `Skip.If(string.IsNullOrEmpty(repoRoot), …)` 가드는 그래서 절대 발동하지
        /// 않았다. AxisAGoldenCaseTests.TryFindRepoRoot()의 관용을 그대로 따른다 -
        /// 테스트 어셈블리가 도는 환경이면 ReSet.slnx는 항상 있으므로(코퍼스 폴더의
        /// 존재 여부와 무관하게 저장소 자체는 있다) 예외를 null로만 감싼다.
        /// </summary>
        private static string? TryFindRepoRoot()
        {
            try
            {
                return RepoPaths.FindRepoRoot();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
