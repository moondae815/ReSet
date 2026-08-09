using System.IO;
using System.Linq;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class SpecExpectationsWiringPolicyTests
{
    // 규칙: VerificationPipelineOrchestrator 안의 _validator.Validate(...) 호출은
    // 전부 두 번째 인자(specExpectations)를 받아야 한다. 하나라도 1인자로
    // 떨어지면 그 경로에서만 조용히 UPDATE 매핑 대조가 꺼진다.

    [Fact]
    public void Scanner_FlagsAOneArgumentValidatorCall()
    {
        var source = @"
class C
{
    private readonly MechanicalValidator _validator;
    void M(string markdown) => _validator.Validate(markdown);
}";

        var offender = Assert.Single(SpecExpectationsWiringPolicyScanner.ScanSource(source));
        Assert.Contains("_validator.Validate(markdown)", offender.Expression);
    }

    [Fact]
    public void Scanner_DoesNotFlagATwoArgumentValidatorCall()
    {
        var source = @"
class C
{
    private readonly MechanicalValidator _validator;
    void M(string markdown, object specExpectations) => _validator.Validate(markdown, specExpectations);
}";

        Assert.Empty(SpecExpectationsWiringPolicyScanner.ScanSource(source));
    }

    [Fact]
    public void Scanner_DoesNotFlagAOneArgumentCallOnADifferentlyNamedReceiver()
    {
        // SpecificationLinker.cs가 실제로 이 모양이다 - 같은 필드/메서드 이름이지만
        // 의도적으로 1인자만 넘긴다(참조 섹션 덧붙인 뒤 정화 목적, IsValid 미확인).
        // 이 스캐너는 파일 단위로 스코프를 좁혀 호출부가 대상 파일을 고르므로,
        // 이 테스트는 "이름만으로는 오탐하지 않는다"는 것이 아니라 스캐너 자체가
        // 수신자 이름(_validator)까지 확인함을 고정한다 - 수신자 이름이 다르면
        // (예: 지역 변수 validator) 잡지 않는다.
        var source = @"
class C
{
    private readonly MechanicalValidator validator;
    void M(string markdown) => validator.Validate(markdown);
}";

        Assert.Empty(SpecExpectationsWiringPolicyScanner.ScanSource(source));
    }

    [Fact]
    public void Scanner_DoesNotFlagAOneArgumentCallToADifferentlyNamedMethod()
    {
        var source = @"
class C
{
    private readonly MechanicalValidator _validator;
    void M(string markdown) => _validator.ValidateConsolidated(markdown);
}";

        Assert.Empty(SpecExpectationsWiringPolicyScanner.ScanSource(source));
    }

    [Fact]
    public void NoUnwiredValidatorCallRemainsInTheOrchestrator()
    {
        // Task 5의 실제 배선 증거: 계획서에는 이 배선을 지키는 새 단위 테스트가
        // 없다 - 기존 오케스트레이터 테스트는 UPDATE 매핑이 없는 SpDefinition만
        // 쓰므로 specExpectations가 항상 null이라 넘기든 안 넘기든 결과가 같다.
        // 이 테스트가 그 사각지대를 메운다: 6개 호출부가 실제로 두 번째 인자를
        // 받는지 소스를 직접 읽어 확인한다.
        var repoRoot = RepoPaths.FindRepoRoot();
        var orchestratorPath = Path.Combine(
            repoRoot, "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs");

        var offenders = SpecExpectationsWiringPolicyScanner.ScanFile(orchestratorPath);

        var report = string.Join(
            "\n",
            offenders.Select(offender => $"  line {offender.Line}: {offender.Expression}"));

        Assert.True(
            offenders.Count == 0,
            "_validator.Validate(...) 호출 중 specExpectations를 넘기지 않는 곳이 있습니다. " +
            "정적 분석이 확정한 UPDATE 매핑 대조가 그 경로에서 조용히 꺼집니다.\n\n" + report);
    }
}
