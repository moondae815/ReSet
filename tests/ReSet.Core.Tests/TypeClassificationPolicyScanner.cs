using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReSet.Core.Tests;

/// <summary>SQL 객체 타입 문자열을 직접 부분 문자열로 판정하는 곳 한 군데.</summary>
public sealed record TypeClassificationOffender(string RelativePath, int Line, string Expression);

/// <summary>
/// SQL 객체 타입에 대한 원시 부분 문자열 판정을 구문 트리로 찾아낸다.
///
/// 같은 결함이 네 번에 걸쳐 발견됐고 매번 사람이 새 grep 패턴을 만들어 찾았다.
/// 표기가 매번 달랐기 때문이다 - rawDep.Type, dep.Type, objectType, d.Type, type.
/// 다섯 번째는 설계 문서를 쓰는 도중에 나왔다(MetadataExporter.cs). 변수명에
/// 의존하는 가드는 다음 변수명을 못 잡는다.
///
/// 형제 규칙인 CancellationPolicyScanner와 같은 방식이다. 시맨틱 모델(컴파일
/// 필요)을 쓰지 않고 구문 트리만 본다. 빠르고 프로젝트 참조가 필요 없으며,
/// 이 저장소의 명명 규약이 일관되어 실용적으로 충분하다.
///
/// 알려진 한계: `var t = dep.Type; t.Contains("TABLE")`처럼 타입 문자열을 이름이
/// 다른 지역 변수로 옮겨 담으면 놓친다. 이 형태는 자연스러운 리팩터링에서
/// 나오지 않으므로 거짓 음성을 감수한다.
/// </summary>
public static class TypeClassificationPolicyScanner
{
    private static readonly HashSet<string> SqlTypeLiterals = new(StringComparer.OrdinalIgnoreCase)
    {
        "TABLE", "VIEW", "FUNCTION", "PROCEDURE"
    };

    /// <summary>이 파일이 정책의 구현체다. 여기서는 부분 문자열 판정이 임무다.</summary>
    private const string ClassifierFileName = "SqlObjectTypeClassifier.cs";

    public static IReadOnlyList<TypeClassificationOffender> ScanDirectory(string srcRoot)
    {
        var offenders = new List<TypeClassificationOffender>();
        foreach (var file in Directory
                     .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).Equals(ClassifierFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 빌드 산출물의 생성 코드는 우리 소유가 아니다.
            // 부분 문자열이 아니라 경로 세그먼트로 판정한다 - "/obj/" 검사는
            // 최상위 obj/ 디렉터리를 놓치고, "Robot/" 같은 이름을 오탐할 수 있다.
            var relative = Path.GetRelativePath(srcRoot, file).Replace('\\', '/');
            if (relative.Split('/').Any(segment =>
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            offenders.AddRange(ScanSource(File.ReadAllText(file), relative));
        }

        return offenders;
    }

    public static IReadOnlyList<TypeClassificationOffender> ScanSource(string sourceText, string relativePath)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetRoot();
        var offenders = new List<TypeClassificationOffender>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member) continue;
            if (member.Name.Identifier.ValueText != "Contains") continue;
            if (!HasSqlTypeLiteralArgument(invocation)) continue;
            if (!IsSqlTypeExpression(member.Expression)) continue;

            var line = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
            offenders.Add(new TypeClassificationOffender(relativePath, line, invocation.ToString()));
        }

        return offenders;
    }

    private static bool HasSqlTypeLiteralArgument(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments.Any(argument =>
            argument.Expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression) &&
            SqlTypeLiterals.Contains(literal.Token.ValueText));

    /// <summary>
    /// 수신자가 SQL 타입 문자열인가. 이 조건이 정밀도의 핵심이다 -
    /// logUpper.Contains("TABLE") 같은 로그 텍스트 매칭은 타입 분류가 아니다.
    /// </summary>
    private static bool IsSqlTypeExpression(ExpressionSyntax receiver) =>
        receiver switch
        {
            // type, objectType, dependencyType
            IdentifierNameSyntax identifier => IsTypeName(identifier.Identifier.ValueText),
            // dep.Type, d.Type, rawDep.Type
            MemberAccessExpressionSyntax member => IsTypeName(member.Name.Identifier.ValueText),
            _ => false
        };

    private static bool IsTypeName(string name) =>
        name.Equals("type", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Type", StringComparison.Ordinal);
}
