using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class TypeClassificationPolicyTests
{
    /// <summary>
    /// 별도 스레드에서 스캔을 실행하고 제한 시간을 둔다. 재리뷰 Critical(무한
    /// 루프) 회귀 테스트 전용이다 - 버그가 재발해도 이 테스트가 실패로
    /// 끝나야지, CI를 영원히 멈추게 해서는 안 된다. `ScanSource`는 동기·CPU
    /// 바운드라 취소 토큰으로 중단시킬 수 없으므로, 시간이 다 되면 백그라운드
    /// 스레드는 계속 돌더라도(누수) 테스트 자체는 타임아웃으로 실패 처리한다 -
    /// 게이트가 무응답이 되는 것보다는 낫다.
    /// </summary>
    private static (bool Completed, IReadOnlyList<TypeClassificationOffender> Offenders) ScanWithTimeout(
        string source, TimeSpan timeout)
    {
        var task = Task.Run(() => TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
        var completed = task.Wait(timeout);
        return (completed, completed ? task.Result : Array.Empty<TypeClassificationOffender>());
    }

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
    public void Scanner_FlagsAContainsCallOnAnUpperCasedTypeReceiver()
    {
        // 최종 브랜치 리뷰 발견 1: 감싼 수신자가 게이트를 우회했다.
        // dep.Type.ToUpper()는 여전히 dep.Type을 판정하는 것이고, 정규화 호출이
        // 껴 있다는 이유로 놓치면 원래의 TVF 오분류 결함이 되살아난다.
        var source = @"
class C
{
    bool M(D dep) => dep.Type.ToUpper().Contains(""TABLE"");
}
class D { public string Type { get; set; } }";

        var offender = Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
        Assert.Contains("dep.Type.ToUpper().Contains", offender.Expression);
    }

    [Fact]
    public void Scanner_FlagsAContainsCallOnANestedlyNormalizedTypeReceiver()
    {
        // 정규화 호출이 여러 겹 이어져도(Trim().ToUpperInvariant()) 전부 풀려야
        // 한다. 이 저장소의 정규화 관용구가 실제로 이 모양이다
        // (DependencyAnalysisOrchestrator.cs:329, MetadataExporter.cs:160,
        // DbMetadataService.cs:216이 전부 Trim().ToUpperInvariant()를 쓴다).
        var source = @"
class C
{
    bool M(D dep) => dep.Type.Trim().ToUpperInvariant().Contains(""VIEW"");
}
class D { public string Type { get; set; } }";

        Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_FlagsAnIndexOfCallOnATypeReceiver()
    {
        // Contains 외에 IndexOf도 같은 판정이다 - 둘 다 타입 문자열에 대한
        // 부분 문자열 판정이며 수신자 관문이 정밀도를 지킨다.
        var source = @"
class C
{
    bool M(D dep) => dep.Type.IndexOf(""PROCEDURE"") >= 0;
}
class D { public string Type { get; set; } }";

        Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagAStartsWithCallWithALiteralOutsideTheSet()
    {
        // 리터럴 관문이 StartsWith 확장 이후에도 여전히 작동하는지 고정한다.
        // "SQL_"은 TABLE/VIEW/FUNCTION/PROCEDURE 집합에 없으므로 잡히면 안 된다.
        var source = @"
class C
{
    bool M(string objectType) => objectType.StartsWith(""SQL_"");
}";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagANormalizedLogTextMatch()
    {
        // 수신자 언래핑을 시작하면 logUpper.Trim().Contains("TABLE") 같은 형태도
        // 생각해야 한다. 언래핑 후 최종 수신자 이름이 여전히 logUpper이므로
        // (타입 이름이 아니므로) 잡히면 안 된다.
        var source = @"
class C
{
    bool M(string logUpper) => logUpper.Trim().Contains(""TABLE"");
}";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void ScanDirectory_ExemptsOnlyTheCanonicalClassifierRelativePath()
    {
        // 발견 3: 면제가 파일명 기준이면 src/ 아래 어디에 두든 같은 이름이면
        // 빠져나간다. 상대 경로 기준으로 바뀌었는지 확인한다.
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var classifierDir = Path.Combine(root, "ReSet.Core", "Services");
            Directory.CreateDirectory(classifierDir);
            File.WriteAllText(
                Path.Combine(classifierDir, "SqlObjectTypeClassifier.cs"),
                @"class C { bool M(D dep) => dep.Type.Contains(""TABLE""); } class D { public string Type { get; set; } }");

            var impostorDir = Path.Combine(root, "Somewhere", "Else");
            Directory.CreateDirectory(impostorDir);
            File.WriteAllText(
                Path.Combine(impostorDir, "SqlObjectTypeClassifier.cs"),
                @"class C { bool M(D dep) => dep.Type.Contains(""TABLE""); } class D { public string Type { get; set; } }");

            var offenders = TypeClassificationPolicyScanner.ScanDirectory(root);

            var offender = Assert.Single(offenders);
            Assert.Equal("Somewhere/Else/SqlObjectTypeClassifier.cs", offender.RelativePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("dependencyType?.Trim().ToUpperInvariant().Contains(\"TABLE\")")]
    [InlineData("dependencyType?.ToUpperInvariant().StartsWith(\"FUNCTION\")")]
    [InlineData("dependencyType?.Trim().Contains(\"VIEW\")")]
    public void Scanner_FlagsANullConditionalNormalizedIdentifierTypeCheck(string expression)
    {
        // 재리뷰 발견: 언래핑이 MemberBindingExpressionSyntax(널 조건부 체인의
        // 첫 호출, 예: `?.Trim`)를 만나면 거기서 멈췄다. 그 노드에는 더 안쪽
        // 표현식이 없기 때문이다 - 진짜 수신자는 감싸는
        // ConditionalAccessExpressionSyntax.Expression에 있다. 실제 재현 지점
        // (DependencyAnalysisOrchestrator.cs:329, MetadataExporter.cs:160)이
        // 정확히 이 모양(dependencyType?.Trim().ToUpperInvariant())이다.
        var source = $@"
class C
{{
    bool M(string dependencyType) => {expression};
}}";

        Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Theory]
    [InlineData("dep.Type?.Trim().ToUpperInvariant().Contains(\"TABLE\")")]
    [InlineData("dep.Type?.ToUpper().Contains(\"TABLE\")")]
    public void Scanner_FlagsANullConditionalNormalizedMemberAccessTypeCheck(string expression)
    {
        var source = $@"
class C
{{
    bool M(D dep) => {expression};
}}
class D {{ public string Type {{ get; set; }} }}";

        Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Theory]
    [InlineData("logUpper?.Trim().Contains(\"TABLE\")")]
    [InlineData("logUpper?.ToUpper().Contains(\"TABLE\")")]
    [InlineData("logUpper?.Trim().ToUpperInvariant().StartsWith(\"VIEW\")")]
    public void Scanner_DoesNotFlagANullConditionalNormalizedLogTextMatch(string expression)
    {
        // 언래핑이 조건부 접근 안으로 들어가도 수신자 관문(조건 3)은 여전히
        // 살아 있어야 한다 - 풀어낸 최종 수신자가 logUpper면 타입 이름이 아니다.
        var source = $@"
class C
{{
    bool M(string logUpper) => {expression} == true;
}}";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagANullConditionalNormalizedTypeCheckWithAnUnrelatedLiteral()
    {
        // 리터럴 관문(조건 2)도 널 조건부·정규화 경로에서 여전히 작동해야 한다.
        var source = @"
class C
{
    bool M(D dep) => dep.Type?.Trim().Contains(""SYNONYM"") == true;
}
class D { public string Type { get; set; } }";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Theory]
    [InlineData("dep.Type?.ToString().Contains(\"TABLE\")")]
    [InlineData("dep.Type?.Substring(0).Contains(\"TABLE\")")]
    public void Scanner_DoesNotFlagANullConditionalTypeReceiverWrappedByANonNormalizationMethod(string expression)
    {
        // ToString/Substring은 정규화 언래핑 목록 밖이다. 감싼 형태라고
        // 무조건 풀면 임의의 메서드 뒤에서도 게이트가 뚫려 정밀도가 무너진다.
        var source = $@"
class C
{{
    bool M(D dep) => {expression} == true;
}}
class D {{ public string Type {{ get; set; }} }}";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Theory]
    [InlineData("logUpper?.Trim()?.Contains(\"TABLE\")", false)]
    [InlineData("logUpper?.Trim()?.ToUpperInvariant().Contains(\"VIEW\")", false)]
    [InlineData("sb?.ToString()?.Trim()?.Contains(\"TABLE\")", false)]
    public void Scanner_TerminatesOnDoubleNullConditionalChainAndDoesNotFlagNonTypeReceivers(
        string expression, bool expectFlag)
    {
        // Critical(재리뷰): `?.`가 두 번 이상 이어지면(`a?.Trim()?.Contains(...)`)
        // 이전 조상 탐색이 안쪽 조건부 접근식을 소유자로 오인해 `conditional.Expression`이
        // 언래핑 대상 자기 자신을 돌려줬다 - IsSqlTypeExpression의 while 루프가
        // 무한히 반복됐다. 이 테스트는 수정 전에는 실패가 아니라 "멈춘다" -
        // ScanWithTimeout이 5초 제한을 두는 이유다.
        var source = $@"
class C
{{
    bool M(string logUpper, object sb) => {expression} == true;
}}";

        var (completed, offenders) = ScanWithTimeout(source, TimeSpan.FromSeconds(5));

        Assert.True(completed, $"5초 안에 끝나지 않았다 - 무한 루프 회귀: {expression}");
        Assert.Equal(expectFlag, offenders.Count > 0);
    }

    [Theory]
    [InlineData("dependencyType?.Trim()?.ToUpperInvariant().Contains(\"TABLE\")")]
    public void Scanner_TerminatesOnDoubleNullConditionalChainAndFlagsATypeReceiver(string expression)
    {
        // 조상 탐색을 바로잡으면(가장 안쪽이 아니라 실제 소유자를 찾으면) 이
        // 모양도 정상적으로 dependencyType까지 풀린다 - 무한 루프의 원인과
        // 판정 능력은 같은 수정으로 함께 고쳐진다. 5초 제한은 회귀 방지용이다.
        var source = $@"
class C
{{
    bool M(string dependencyType) => {expression} == true;
}}";

        var (completed, offenders) = ScanWithTimeout(source, TimeSpan.FromSeconds(5));

        Assert.True(completed, $"5초 안에 끝나지 않았다 - 무한 루프 회귀: {expression}");
        Assert.Single(offenders);
    }

    [Theory]
    [InlineData("a?.M(logUpper.Trim().Contains(\"TABLE\"))")]
    [InlineData("logType?.M(logUpper.Trim().Contains(\"TABLE\"))")]
    public void Scanner_DoesNotFlagAnUnrelatedContainsCallInsideAConditionalInvocationArgument(string expression)
    {
        // 조상 탐색이 과도하게 넓으면, 무관한 바깥 조건부 호출의 인자 안에 있는
        // Contains 호출을 그 바깥 조건부의 수신자(a, logType)에 잘못 결부시킬
        // 위험이 있다. 이 인자 안 Contains 호출은 애초에 `?.`를 전혀 안 쓰므로
        // MemberBindingExpressionSyntax 경로를 타지 않지만, 회귀 방지로 고정한다.
        var source = $@"
class C
{{
    void M(object logUpper) {{ }}
    bool M2(object a, object logType, string logUpper) => {expression} == true;
}}";

        var (completed, offenders) = ScanWithTimeout(source, TimeSpan.FromSeconds(5));

        Assert.True(completed, $"5초 안에 끝나지 않았다: {expression}");
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("dep?.Type.Contains(\"TABLE\")")]
    [InlineData("dep?.Type.Trim().ToUpperInvariant().Contains(\"TABLE\")")]
    public void Scanner_FlagsAMemberBindingTypeReceiverWithoutFurtherUnwrapping(string expression)
    {
        // Important B-2(재리뷰): dep?.Type.Contains(...)의 수신자는 언래핑을
        // 거치지 않고 곧장 MemberBindingExpressionSyntax(Name=Type)로 들어온다
        // (`.Type`이 dep에 대한 첫 널 조건부 접근 자체이기 때문). 조상 탐색
        // 없이 그 노드의 Name만 보면 되므로, 무한 루프 위험 없이 안전하게 고칠
        // 수 있는 형태였다.
        var source = $@"
class C
{{
    bool M(D dep) => {expression} == true;
}}
class D {{ public string Type {{ get; set; }} }}";

        var offenders = TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs");

        Assert.Single(offenders);
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
