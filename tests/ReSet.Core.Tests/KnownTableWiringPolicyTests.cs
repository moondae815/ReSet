using System.IO;
using System.Linq;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class KnownTableWiringPolicyTests
{
    // 규칙: VerificationPipelineOrchestrator 안의 _validator.ValidateBatchStep(...)
    // 호출은 전부 세 번째 인자(카탈로그)를 받아야 한다. 하나라도 2인자로 떨어지면
    // 그 경로에서만 미지 테이블 검사가 조용히 꺼진다 - 이 저장소가 _validator.Validate
    // 에서 이미 겪은 실패 모드다(SpecExpectationsWiringPolicyScanner 참고).

    [Fact]
    public void Scanner_FlagsATwoArgumentValidateBatchStepCall()
    {
        var source = @"
class C
{
    private readonly MechanicalValidator _validator;
    void M(string content, object step) => _validator.ValidateBatchStep(content, step);
}";

        var offender = Assert.Single(KnownTableWiringPolicyScanner.ScanSource(source));
        Assert.Contains("_validator.ValidateBatchStep(content, step)", offender.Expression);
    }

    [Fact]
    public void Scanner_DoesNotFlagAThreeArgumentCall()
    {
        var source = @"
class C
{
    private readonly MechanicalValidator _validator;
    void M(string content, object step, object catalog)
        => _validator.ValidateBatchStep(content, step, catalog);
}";

        Assert.Empty(KnownTableWiringPolicyScanner.ScanSource(source));
    }

    [Fact]
    public void Scanner_DoesNotFlagADifferentlyNamedReceiver()
    {
        var source = @"
class C
{
    void M(object validator, string content, object step)
        => validator.ValidateBatchStep(content, step);
}";

        Assert.Empty(KnownTableWiringPolicyScanner.ScanSource(source));
    }

    [Fact]
    public void Orchestrator_PassesTheCatalogAtEveryCallSite()
    {
        // 저장소 루트 탐색은 RepoPaths.FindRepoRoot()를 쓴다 - CancellationPolicyScanner.cs:240에
        // 이미 있고 SpecExpectationsWiringPolicyTests가 그것을 쓴다. 두 스캐너 테스트가
        // 서로 다른 경로 규칙을 쓰면 한쪽이 CI에서만 깨진다.
        var orchestratorPath = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs");

        var offenders = KnownTableWiringPolicyScanner.ScanFile(orchestratorPath);

        Assert.True(
            offenders.Count == 0,
            "카탈로그 인자 없이 ValidateBatchStep을 호출한 곳: " +
            string.Join(", ", offenders.Select(o => $"{o.Line}행 {o.Expression}")));
    }
}
