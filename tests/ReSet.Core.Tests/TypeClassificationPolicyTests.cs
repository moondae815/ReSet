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
    public void Scanner_FlagsANullConditionalMemberAccessTypeCheck()
    {
        // SqlObjectTypeClassifier.cs 자신이 ?.Contains(...)를 쓴다. 그 관용구를
        // 호출부로 복사해 오면 (파일명 제외가 없는 곳에서는) 사각지대가 된다 -
        // a?.b()는 MemberAccessExpressionSyntax가 아니라
        // ConditionalAccessExpressionSyntax/MemberBindingExpressionSyntax로
        // 파싱되기 때문이다.
        var source = @"
class C
{
    bool M(D dep) => dep.Type?.Contains(""TABLE"") == true;
}
class D { public string Type { get; set; } }";

        var offender = Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
        Assert.Contains("dep.Type?.Contains", offender.Expression);
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
    public void Scanner_DoesNotFlagANullConditionalLogTextMatch()
    {
        // 조건 3(수신자)은 null 조건부 경로에서도 여전히 관문이어야 한다.
        var source = @"
class C
{
    bool M(string logUpper) => logUpper?.Contains(""TABLE"") == true;
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

    [Fact]
    public void NoRawSqlTypeClassificationRemainsInSource()
    {
        // baseline 파일을 두지 않는 이유: 다섯 곳을 전부 고칠 수 있으므로 목표가
        // 0이다. 빈 baseline은 "0을 단언한다"를 돌려 말한 것에 불과하다. 정당한
        // 예외가 실제로 생기면 그때 도입한다.
        var repoRoot = RepoPaths.FindRepoRoot();
        var offenders = TypeClassificationPolicyScanner
            .ScanDirectory(System.IO.Path.Combine(repoRoot, "src"));

        var report = string.Join(
            "\n",
            offenders.Select(offender =>
                $"  {offender.RelativePath}:{offender.Line}  {offender.Expression}"));

        Assert.True(
            offenders.Count == 0,
            "SQL 객체 타입을 직접 부분 문자열로 판정하는 곳이 남아 있습니다. " +
            "SqlObjectTypeClassifier의 IsTableOrView / IsCodeObject / ResolveCodeObjectType로 " +
            "위임하십시오. \"SQL_TABLE_VALUED_FUNCTION\"이 \"TABLE\"을 포함하므로 " +
            $"직접 판정하면 TVF가 테이블로 오분류됩니다.\n\n{report}");
    }
}
