using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReSet.Core.Tests;

/// <summary>이스케이프 없이 마크업 싱크로 들어가는 보간 하나.</summary>
public sealed record RawMarkupHole(string RelativePath, string Member, string Expression, int Line);

/// <summary>
/// <c>NotifyStatus(...)</c>에 들어가는 보간 문자열의 구멍 중 <c>EscapeMarkup</c>을
/// 거치지 않는 것을 구문 트리로 찾는다.
///
/// <para>
/// 이 싱크만 보는 이유: <c>NotifyStatus</c>는 인자를 Spectre 마크업으로 파싱한다
/// (호출부가 <c>[yellow]…[/]</c>를 싣는 설계라 통째로 이스케이프할 수 없다).
/// <c>NotifyError</c>·<c>NotifyL1Errors</c>·<c>NotifyL2Defects</c>는 구현부에서
/// 이미 전량 이스케이프하므로 호출부가 무엇을 넣든 안전하다.
/// </para>
/// <para>
/// 시맨틱 모델(컴파일)을 쓰지 않고 구문 트리만 본다 -
/// <see cref="CancellationPolicyScanner"/>와 같은 이유다. 이 저장소의 마크업 싱크
/// 호출은 전부 <c>_userInteraction.NotifyStatus</c> 한 모양이다.
/// </para>
/// </summary>
public static class MarkupEscapePolicyScanner
{
    /// <summary>인자를 마크업으로 파싱하는 싱크.</summary>
    private const string Sink = "NotifyStatus";

    /// <summary>자유 문자열을 마크업에서 지우는 함수들.</summary>
    private static readonly HashSet<string> EscapeNames =
        new(StringComparer.Ordinal) { "EscapeMarkup", "Escape" };

    public static IReadOnlyList<RawMarkupHole> ScanDirectory(string srcRoot)
    {
        var holes = new List<RawMarkupHole>();
        foreach (var file in Directory
                     .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            // 빌드 산출물의 생성 코드는 우리 소유가 아니다.
            var relative = Path.GetRelativePath(srcRoot, file).Replace('\\', '/');
            if (relative.Split('/').Any(segment =>
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            holes.AddRange(ScanSource(File.ReadAllText(file), relative));
        }

        return holes;
    }

    public static IReadOnlyList<RawMarkupHole> ScanSource(string source, string relativePath)
    {
        var holes = new List<RawMarkupHole>();
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (MethodName(invocation.Expression) != Sink) continue;

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                // 인자가 문자열 이어 붙이기(`$"…" + $"…"`)여도 구멍은 전부 모은다.
                foreach (var interpolation in argument.DescendantNodes().OfType<InterpolationSyntax>())
                {
                    var expression = Unwrap(interpolation.Expression);
                    if (IsEscaped(expression) || IsInert(expression)) continue;

                    holes.Add(new RawMarkupHole(
                        relativePath,
                        MemberName(invocation),
                        Normalize(expression.ToString()),
                        interpolation.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                }
            }
        }

        return holes;
    }

    /// <summary>기준선 한 줄의 표기. 줄 번호는 넣지 않는다 - 편집마다 썩는다.</summary>
    public static string BaselineKey(RawMarkupHole hole) =>
        $"{hole.RelativePath}|{hole.Member}|{hole.Expression}";

    private static string? MethodName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => null,
    };

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) =>
        expression is ParenthesizedExpressionSyntax parenthesized
            ? Unwrap(parenthesized.Expression)
            : expression;

    /// <summary>
    /// 이스케이프를 거쳤는가. 삼항은 <b>두 갈래가 모두</b> 거쳐야 한다 - 한 갈래만
    /// 보면 나머지 갈래가 조용히 새고, 그 갈래가 드물수록 늦게 터진다.
    /// </summary>
    private static bool IsEscaped(ExpressionSyntax expression)
    {
        switch (Unwrap(expression))
        {
            case InvocationExpressionSyntax invocation:
                return EscapeNames.Contains(MethodName(invocation.Expression) ?? string.Empty);
            case ConditionalExpressionSyntax conditional:
                return IsEscaped(conditional.WhenTrue) && IsEscaped(conditional.WhenFalse);
            default:
                return false;
        }
    }

    /// <summary>
    /// 마크업을 만들 수 없는 값. 숫자·불리언 리터럴과 <c>.Count</c> 같은 정수 프로퍼티는
    /// 대괄호를 낼 수 없다. 문자열 리터럴은 소스에 적힌 그대로라 저자가 보고 쓴 것이다.
    /// </summary>
    private static bool IsInert(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax;

    private static string Normalize(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string MemberName(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method: return method.Identifier.ValueText;
                case LocalFunctionStatementSyntax local: return local.Identifier.ValueText;
                case ConstructorDeclarationSyntax ctor: return ctor.Identifier.ValueText;
                case PropertyDeclarationSyntax property: return property.Identifier.ValueText;
                case AccessorDeclarationSyntax accessor: return accessor.Keyword.ValueText;
            }
        }

        return "<top-level>";
    }
}
