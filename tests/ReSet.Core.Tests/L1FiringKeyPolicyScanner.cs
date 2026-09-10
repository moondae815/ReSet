using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReSet.Core.Tests;

/// <summary>키가 안 붙는 직접 발화 한 곳.</summary>
public sealed record ErrorsAddOffender(string RelativePath, int Line, string Member);

/// <summary>
/// <c>result.Errors.Add(...)</c> 직접 호출을 구문 트리로 찾는다. 그 자리는 검사 키가
/// 안 붙으므로 수렴 탐지기에 안 잡힌다 - <c>result.Report(...)</c> 를 써야 한다.
///
/// 시맨틱 모델(컴파일 필요)을 쓰지 않고 구문 트리만 본다.
/// <see cref="CancellationPolicyScanner"/> 와 같은 이유로 충분하다 - 이 저장소의
/// 발화 자리는 전부 <c>result</c> 라는 이름의 지역/파라미터를 쓴다.
/// </summary>
public static class L1FiringKeyPolicyScanner
{
    public static IReadOnlyList<ErrorsAddOffender> ScanFile(string absolutePath, string relativePath) =>
        ScanSource(File.ReadAllText(absolutePath), relativePath);

    public static IReadOnlyList<ErrorsAddOffender> ScanSource(string source, string relativePath)
    {
        var offenders = new List<ErrorsAddOffender>();
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // 찾는 모양: <무엇>.Errors.Add(...)
            if (invocation.Expression is not MemberAccessExpressionSyntax add) continue;
            if (add.Name.Identifier.ValueText != "Add") continue;
            if (add.Expression is not MemberAccessExpressionSyntax errors) continue;
            if (errors.Name.Identifier.ValueText != "Errors") continue;

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            offenders.Add(new ErrorsAddOffender(relativePath, line, EnclosingMember(invocation)));
        }

        return offenders;
    }

    /// <summary>
    /// 위반이 어느 멤버 안에 있는지. 지역 함수 안이면 지역 함수 이름을 낸다 -
    /// 그것이 <c>[CallerMemberName]</c> 이 채울 이름과 같은 단위다.
    /// </summary>
    private static string EnclosingMember(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case LocalFunctionStatementSyntax local: return local.Identifier.ValueText;
                case MethodDeclarationSyntax method: return method.Identifier.ValueText;
                case PropertyDeclarationSyntax property: return property.Identifier.ValueText;
                case ConstructorDeclarationSyntax ctor: return ctor.Identifier.ValueText;
            }
        }

        return "(알 수 없음)";
    }
}
