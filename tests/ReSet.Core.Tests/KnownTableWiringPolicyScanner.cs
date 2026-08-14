using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReSet.Core.Tests;

public sealed record KnownTableCallOffender(int Line, string Expression);

/// <summary>
/// `_validator.ValidateBatchStep(...)` 호출이 카탈로그 인자를 받는지 구문 트리로 본다.
///
/// 수신자 이름(_validator)까지 확인한다 - 다른 이름의 지역 변수에 같은 메서드가
/// 있을 수 있고, 이 규칙이 지키려는 것은 오케스트레이터의 그 필드 하나다.
/// </summary>
public static class KnownTableWiringPolicyScanner
{
    private const string ReceiverName = "_validator";
    private const string MethodName = "ValidateBatchStep";
    private const int RequiredArgumentCount = 3;

    public static IReadOnlyList<KnownTableCallOffender> ScanSource(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);

        return tree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.Text == MethodName &&
                member.Expression is IdentifierNameSyntax receiver &&
                receiver.Identifier.Text == ReceiverName)
            .Where(invocation => invocation.ArgumentList.Arguments.Count < RequiredArgumentCount)
            .Select(invocation => new KnownTableCallOffender(
                invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                invocation.ToString()))
            .ToList();
    }

    public static IReadOnlyList<KnownTableCallOffender> ScanFile(string filePath) =>
        ScanSource(File.ReadAllText(filePath));
}
