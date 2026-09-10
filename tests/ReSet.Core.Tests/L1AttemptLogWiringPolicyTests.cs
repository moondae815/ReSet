using System.IO;
using System.Linq;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 이 저장소 전체에서 <c>L1AttemptLog.Append(...)</c> 을 잇는 유일한 배선을 잠근다.
    ///
    /// [왜 필요한가 - 2026-09-10 최종 리뷰 발견] 이 배선이 없으면(또는 가드가 깨지면)
    /// 거부된 시도 코퍼스가 다시는 안 자란다 - 그런데 그 사실을 알려 줄 시험이
    /// 하나도 없었다. <c>VerificationPipelineOrchestrator.cs</c> 의 그 <c>if</c> 블록
    /// 전체를 지워도 <c>dotnet build</c> 는 깨끗하고 4045 개 시험이 전부 초록이다
    /// (코퍼스 픽스처가 이미 커밋돼 있어 <c>SelfReinforcingCheckTests</c> 는 그
    /// 픽스처만 읽는다) - 이 클래스가 그 사각지대를 되돌림으로 확인하고 닫는다.
    /// </summary>
    public class L1AttemptLogWiringPolicyTests
    {
        // 실물 배선 - 정확히 하나여야 하고, L1 실패 가드 안이어야 한다.
        [Fact]
        public void ExactlyOneAppendSiteExistsAndItIsGuardedByL1Failure()
        {
            var repoRoot = RepoPaths.FindRepoRoot();
            var srcRoot = Path.Combine(repoRoot, "src");

            var sites = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .SelectMany(path => L1AttemptLogWiringScanner.ScanFile(
                    path, Path.GetRelativePath(repoRoot, path)))
                .ToList();

            Assert.True(sites.Count == 1,
                "L1AttemptLog.Append 호출이 정확히 1개여야 합니다 - 실제 " + sites.Count + "개.\n"
                + "0개라면 거부된 시도 코퍼스로 가는 유일한 배선이 사라진 것입니다.\n"
                + string.Join("\n", sites.Select(s => $"  {s.RelativePath}:{s.Line}")));

            var only = sites.Single();
            Assert.True(only.GuardedByIsValidNegation,
                $"{only.RelativePath}:{only.Line} 의 L1AttemptLog.Append 가 "
                + "if (!무엇.IsValid) 가드 안에 있지 않습니다 - L1 을 통과한 시도까지 "
                + "「거부된 시도」코퍼스로 새 나갈 수 있습니다.");
        }

        // 스캐너 양성 표본 - 가드 없는 호출은 GuardedByIsValidNegation = false 로 잡혀야 한다.
        [Fact]
        public void ScanSource_FindsAnUnguardedAppendCall()
        {
            const string source = @"
class C
{
    void M()
    {
        L1AttemptLog.Append(dir, 1, firings);
    }
}";
            var site = Assert.Single(L1AttemptLogWiringScanner.ScanSource(source, "Fake.cs"));
            Assert.False(site.GuardedByIsValidNegation);
        }

        // 스캐너 음성 표본 - 실물과 같은 모양(중첩 if 안, !l1Result.IsValid 가드)이면
        // GuardedByIsValidNegation = true 여야 한다.
        [Fact]
        public void ScanSource_RecognizesTheNestedIsValidNegationGuard()
        {
            const string source = @"
class C
{
    void M(Result l1Result, string? outputPaths)
    {
        if (!l1Result.IsValid)
        {
            if (outputPaths != null)
            {
                L1AttemptLog.Append(dir, 1, firings);
            }
        }
    }
}";
            var site = Assert.Single(L1AttemptLogWiringScanner.ScanSource(source, "Fake.cs"));
            Assert.True(site.GuardedByIsValidNegation);
        }

        // 되돌림 판정 - 가드가 사라지면(예: 조건 없이 늘 부르면) false 로 떨어져야
        // 이 잠금이 실제로 무는지 확인된다.
        [Fact]
        public void ScanSource_DoesNotTreatAnUnrelatedIfAsTheGuard()
        {
            const string source = @"
class C
{
    void M(bool somethingElse)
    {
        if (somethingElse)
        {
            L1AttemptLog.Append(dir, 1, firings);
        }
    }
}";
            var site = Assert.Single(L1AttemptLogWiringScanner.ScanSource(source, "Fake.cs"));
            Assert.False(site.GuardedByIsValidNegation);
        }
    }
}
