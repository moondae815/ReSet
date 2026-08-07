using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// 규칙: Thinking.md를 쓰는 자리는 전부 ThinkingLogDocument.Compose를 거쳐야 한다.
///
/// 이 규칙이 없던 동안 호출부 네 곳이 각자 "본문이 비었으면 쓰지 않는다"를 구현했고,
/// gpt-5 Responses API가 빈 summary를 돌려준 회차에서 산출물이 통째로 사라졌다.
/// 헤더 문구도 두 곳은 손으로 조립하고 두 곳은 아예 없어 서로 어긋나 있었다.
/// </summary>
public sealed class ThinkingLogPolicyTests
{
    public sealed record Offender(string RelativePath, int Line);

    [Fact]
    public void Scanner_FlagsAWriteThatBypassesTheComposer()
    {
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(string docsDir, string thinking)
    {
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(docsDir, ""Thinking.md""), thinking);
    }
}";

        var offender = Assert.Single(Scan(source, "Fake.cs"));
        Assert.Equal("Fake.cs", offender.RelativePath);
    }

    // 경로를 변수에 담고 쓰는 모양도 같은 규칙을 받는다. 호출 인자만 들여다보면
    // 이 모양이 빠져나가고, 실제로 DependencyAnalysisOrchestrator가 그 모양이었다.
    [Fact]
    public void Scanner_FlagsAWriteWhosePathWasStoredInAVariable()
    {
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(string docsDir, string thinking)
    {
        var thinkingPath = System.IO.Path.Combine(docsDir, ""Thinking.md"");
        await System.IO.File.WriteAllTextAsync(thinkingPath, ""header"" + thinking);
    }
}";

        var offender = Assert.Single(Scan(source, "Fake.cs"));
        Assert.Equal("Fake.cs", offender.RelativePath);
    }

    // 같은 파일의 무관한 쓰기까지 싸잡으면 규칙이 버려진다. 판정 단위는 메서드다.
    [Fact]
    public void Scanner_DoesNotFlagAnUnrelatedWriteInAFileThatAlsoWritesTheThinkingLog()
    {
        var source = @"
class C
{
    async System.Threading.Tasks.Task WritesThinking(string docsDir, string thinking)
    {
        var thinkingPath = System.IO.Path.Combine(docsDir, ""Thinking.md"");
        await System.IO.File.WriteAllTextAsync(
            thinkingPath,
            ThinkingLogDocument.Compose(thinking, ""OpenAI"", ""m"", ""high"", System.DateTime.Now));
    }

    async System.Threading.Tasks.Task WritesSomethingElse(string docsDir, string spec)
    {
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(docsDir, ""Spec.md""), spec);
    }
}";

        Assert.Empty(Scan(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagAWriteThatGoesThroughTheComposer()
    {
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(string docsDir, string thinking)
    {
        await System.IO.File.WriteAllTextAsync(
            System.IO.Path.Combine(docsDir, ""Thinking.md""),
            ThinkingLogDocument.Compose(thinking, ""OpenAI"", ""m"", ""high"", System.DateTime.Now));
    }
}";

        Assert.Empty(Scan(source, "Fake.cs"));
    }

    [Fact]
    public void EveryThinkingLogWriteInTheCodebaseGoesThroughTheComposer()
    {
        var srcRoot = Path.Combine(RepoPaths.FindRepoRoot(), "src");

        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(file => Scan(
                File.ReadAllText(file),
                Path.GetRelativePath(srcRoot, file).Replace('\\', '/')))
            .ToList();

        Assert.Empty(offenders);
    }

    private static IReadOnlyList<Offender> Scan(string sourceText, string relativePath)
    {
        var root = CSharpSyntaxTree.ParseText(sourceText).GetRoot();
        var offenders = new List<Offender>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (MethodName(invocation) is not ("WriteAllText" or "WriteAllTextAsync"))
            {
                continue;
            }

            var arguments = invocation.ArgumentList;
            if (!WritesThinkingLog(invocation, arguments))
            {
                continue;
            }

            var usesComposer = arguments
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(inner => inner.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Compose",
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "ThinkingLogDocument" }
                });

            if (!usesComposer)
            {
                var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                offenders.Add(new Offender(relativePath, line));
            }
        }

        return offenders;
    }

    /// <summary>
    /// 경로 인자가 Thinking.md를 가리키는가. 인자에 리터럴이 직접 박힌 모양과,
    /// 경로를 지역 변수에 담아 두고 그 변수를 넘기는 모양을 모두 본다.
    /// 판정은 호출 단위로 유지한다 — 메서드 단위로 넓히면 Main처럼 큰 메서드 안에서
    /// 한 자리만 고쳐도 나머지가 통과해 버린다.
    /// </summary>
    private static bool WritesThinkingLog(
        InvocationExpressionSyntax invocation,
        ArgumentListSyntax arguments)
    {
        if (ContainsThinkingLogLiteral(arguments))
        {
            return true;
        }

        if (arguments.Arguments.FirstOrDefault()?.Expression is not IdentifierNameSyntax pathIdentifier)
        {
            return false;
        }

        var scope = invocation.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault()
            ?? (SyntaxNode)invocation.SyntaxTree.GetRoot();

        return scope
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(declarator => declarator.Identifier.ValueText == pathIdentifier.Identifier.ValueText)
            .Any(declarator => declarator.Initializer is not null &&
                               ContainsThinkingLogLiteral(declarator.Initializer));
    }

    private static bool ContainsThinkingLogLiteral(SyntaxNode node) =>
        node.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Any(literal => literal.Token.ValueText == "Thinking.md");

    private static string? MethodName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null
        };
}
