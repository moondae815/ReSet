using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [규칙] <c>VerificationPipelineOrchestrator</c> 안의 <c>_validator.ValidateConsolidated(...)</c> 호출은 전부
/// <b>두 번째 인자(목차의 단계 목록)</b>를 받아야 한다.
///
/// 선택 인자라 빼도 컴파일된다 — 빠뜨린 경로에서만 「무엇이 RunId 발급 절보다 앞인가」가 문서 순서로 되돌아가고,
/// B19 에서 그 문서 순서가 흐름도의 범위 헤딩 하나로 아홉을 오탐했다. 카탈로그·원본 인터페이스 인자에서 이 저장소가
/// 이미 겪은 실패 모드라 같은 스캐너 관례(<see cref="KnownTableWiringPolicyScanner"/>)로 못박는다.
/// 판독: docs/audit-reports/2026-09-17-단계순서-오라클-목차-사전선언.md
/// </summary>
public sealed class StepOrderWiringPolicyTests
{
    private const string ReceiverName = "_validator";
    private const string MethodName = "ValidateConsolidated";
    private const int RequiredArgumentCount = 2;

    private static IReadOnlyList<(int Line, string Expression)> Scan(string sourceText) =>
        CSharpSyntaxTree.ParseText(sourceText).GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.Text == MethodName &&
                member.Expression is IdentifierNameSyntax receiver &&
                receiver.Identifier.Text == ReceiverName)
            .Where(invocation => invocation.ArgumentList.Arguments.Count < RequiredArgumentCount)
            .Select(invocation => (invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1, invocation.ToString()))
            .ToList();

    [Fact]
    public void Scanner_FlagsACallWithoutTheSteps()
    {
        var offender = Assert.Single(Scan("class C { MechanicalValidator _validator; void M(string p) => _validator.ValidateConsolidated(p); }"));
        Assert.Contains("_validator.ValidateConsolidated(p)", offender.Expression);
    }

    [Fact]
    public void Scanner_DoesNotFlagACallWithTheSteps() =>
        Assert.Empty(Scan("class C { MechanicalValidator _validator; void M(string p, object s) => _validator.ValidateConsolidated(p, s); }"));

    [Fact]
    public void Scanner_DoesNotFlagADifferentlyNamedReceiver() =>
        Assert.Empty(Scan("class C { void M(object validator, string p) => validator.ValidateConsolidated(p); }"));

    // 실물: 오케스트레이터의 호출부 둘(주 L1 · 재작성 경로)이 모두 목차를 넘긴다.
    [Fact]
    public void Orchestrator_PassesTheStepsAtEveryValidateConsolidatedCall()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs");
        var source = File.ReadAllText(path);

        var offenders = Scan(source);
        Assert.True(offenders.Count == 0,
            "목차 없이 ValidateConsolidated 를 부른 곳: " + string.Join(", ", offenders.Select(o => $"{o.Line}행 {o.Expression}")));

        // 호출이 사라져도 위 단언은 초록이다 - 호출부가 둘 다 살아 있는지도 본다.
        var calls = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(invocation => invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: MethodName });
        Assert.Equal(2, calls);
    }
}
