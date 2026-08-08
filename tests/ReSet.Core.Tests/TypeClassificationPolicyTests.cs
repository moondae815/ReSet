using System.Linq;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class TypeClassificationPolicyTests
{
    // 규칙: SQL 객체 타입 문자열에 대한 원시 부분 문자열 판정은 위반이다.
    // "SQL_TABLE_VALUED_FUNCTION"이 "TABLE"을 포함하므로, 호출부마다 따로
    // 판정하면 TVF가 테이블로 오분류된다. 판정은 SqlObjectTypeClassifier
    // 한곳에서만 한다.

    [Fact]
    public void Scanner_FlagsAMemberAccessTypeCheck()
    {
        var source = @"
class C
{
    bool M(D dep) => dep.Type.Contains(""TABLE"");
}
class D { public string Type { get; set; } }";

        var offender = Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
        Assert.Equal("Fake.cs", offender.RelativePath);
        Assert.Contains("dep.Type.Contains", offender.Expression);
    }

    [Fact]
    public void Scanner_FlagsAnIdentifierNamedLikeAType()
    {
        // 변수명이 매번 달랐던 것이 이 결함이 네 번 반복된 이유다:
        // rawDep.Type, dep.Type, objectType, d.Type, type.
        var source = @"
class C
{
    bool M(string objectType) => objectType.Contains(""VIEW"");
}";

        Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_FlagsAnOrdinalIgnoreCaseCheck()
    {
        // 비교 옵션 인자가 붙어도 같은 결함이다.
        var source = @"
class C
{
    bool M(string type) =>
        type.Contains(""PROCEDURE"", System.StringComparison.OrdinalIgnoreCase);
}";

        Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagLogTextMatching()
    {
        // 실례: src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs.
        // AI가 돌려준 로그 텍스트에서 단어를 찾는 것이지 타입 분류가 아니다.
        // 여기서 거짓 양성을 내면 규칙이 버려진다.
        var source = @"
class C
{
    bool M(string logUpper) => logUpper.Contains(""TABLE"");
}";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagTheSameTextInsideAComment()
    {
        var source = @"
class C
{
    // 예전에는 dep.Type.Contains(""TABLE"")로 판정했다.
    bool M(D dep) => SqlObjectTypeClassifier.IsTableOrView(dep.Type);
}
class D { public string Type { get; set; } }
static class SqlObjectTypeClassifier { public static bool IsTableOrView(string t) => false; }";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagTheSameTextInsideAStringLiteral()
    {
        var source = @"
class C
{
    string M() => ""dep.Type.Contains(\""TABLE\"")"";
}";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagAContainsCallWithAnUnrelatedLiteral()
    {
        // 리터럴 집합이 실제로 관문 역할을 하는지 고정한다. 이것이 없으면
        // 조건 3(수신자)만으로 통과하는지 조건 2(리터럴)도 보는지 구분되지 않는다.
        var source = @"
class C
{
    bool M(D dep) => dep.Type.Contains(""SYNONYM"");
}
class D { public string Type { get; set; } }";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }
}
