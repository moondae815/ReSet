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

        [SkippableFact]
        public void Batch4Roster_ClosesFromTwelveToFourteen()
        {
            var repoRoot = TryFindRepoRoot();
            Skip.If(string.IsNullOrEmpty(repoRoot), CorpusSkip.Reason);

            var outputRoot = Path.Combine(repoRoot!, "output");
            Skip.IfNot(Directory.Exists(Path.Combine(outputRoot, "Procedures")), CorpusSkip.Reason);

            var promptContext = Path.Combine(
                repoRoot!, "output.bak-stage4-control-20260828",
                "Jobs", "POQSettleBatch4", "raw", "prompt-context.md");
            Skip.IfNot(File.Exists(promptContext), CorpusSkip.Reason);

            var roster = File.ReadLines(promptContext)
                .Where(line => line.StartsWith("Filename: ", StringComparison.Ordinal))
                .Select(line => line["Filename: ".Length..].Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => Path.Combine("Procedures", name, "docs", "Spec.md"))
                .Where(relative => File.Exists(Path.Combine(outputRoot, relative)))
                .ToList();

            Skip.IfNot(roster.Count == 12, $"로스터가 12편이 아니라 {roster.Count}편이다 - 코퍼스가 바뀌었다.");

            var closure = BatchStepCatalog.CloseOverProcedureReferences(outputRoot, roster);

            _output.WriteLine($"진입점 {roster.Count} → 폐포 {closure.SpecPaths.Count} · 더해짐 {closure.Added.Count}");
            foreach (var added in closure.Added) _output.WriteLine("  + " + added);

            Assert.False(closure.CapExceeded);
            Assert.Equal(14, closure.SpecPaths.Count);
            Assert.Equal(
                new[]
                {
                    "Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md",
                    "Procedures/dbo.UP_Util_Settle_Summary_AcqManual/docs/Spec.md"
                },
                closure.Added
                    .Select(p => p.Replace(Path.DirectorySeparatorChar, '/'))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList());
        }

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
