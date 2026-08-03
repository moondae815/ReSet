using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReSet.Core.Tests;

/// <summary>취소를 삼킬 수 있는 catch 한 곳.</summary>
public sealed record CancellationOffender(string RelativePath, int Line, string Member);

/// <summary>
/// 취소를 삼키는 catch를 구문 트리로 찾아낸다.
///
/// 세 사이클 연속으로 같은 결함이 새 모양으로 나타났고 매번 사람이 새 grep 패턴을
/// 만들어 찾았다. 네 모양(빈 catch, 알림 후 계속, 바깥 핸들러 가리기, 타입 세탁)은
/// 결과가 다를 뿐 구문 서명이 같다 - OCE를 잡을 수 있으면서 거르지도 다시 던지지도
/// 않는 넓은 catch. grep이 놓친 것은 패턴이 달라서가 아니라 C# 구조를 못 읽어서다.
///
/// 시맨틱 모델(컴파일 필요)을 쓰지 않고 구문 트리만 본다. 빠르고 프로젝트 참조가
/// 필요 없으며, 이 저장소의 명명 규약이 일관되어 실용적으로 충분하다.
/// </summary>
public static class CancellationPolicyScanner
{
    private static readonly HashSet<string> BroadCatchTypes = new(StringComparer.Ordinal)
    {
        "Exception", "System.Exception", "SystemException", "System.SystemException"
    };

    private static readonly HashSet<string> CancellationTypes = new(StringComparer.Ordinal)
    {
        "OperationCanceledException", "System.OperationCanceledException",
        "TaskCanceledException", "System.Threading.Tasks.TaskCanceledException"
    };

    private static readonly HashSet<string> TokenIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "cancellationToken", "token", "ct"
    };

    public static IReadOnlyList<CancellationOffender> ScanDirectory(string srcRoot)
    {
        var offenders = new List<CancellationOffender>();
        foreach (var file in Directory
                     .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
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

    public static IReadOnlyList<CancellationOffender> ScanSource(string sourceText, string relativePath)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetRoot();
        var offenders = new List<CancellationOffender>();

        foreach (var tryStatement in root.DescendantNodes().OfType<TryStatementSyntax>())
        {
            if (!ContainsCancellableAwait(tryStatement.Block))
            {
                continue;
            }

            for (var index = 0; index < tryStatement.Catches.Count; index++)
            {
                var clause = tryStatement.Catches[index];

                if (!IsBroadCatch(clause)) continue;
                if (FiltersCancellation(clause)) continue;
                if (RethrowsEverything(clause)) continue;
                if (EarlierClauseHandlesCancellation(tryStatement, index)) continue;

                var line = tree.GetLineSpan(clause.Span).StartLinePosition.Line + 1;
                offenders.Add(new CancellationOffender(relativePath, line, MemberName(clause)));
            }
        }

        return offenders;
    }

    /// <summary>
    /// try 블록 안에 CancellationToken을 넘기는 await가 있는가.
    /// 이 조건이 정밀도의 핵심이다 - 동기 IO를 감싸는 soft-fail은 취소와 무관하다.
    /// </summary>
    private static bool ContainsCancellableAwait(SyntaxNode tryBlock) =>
        tryBlock.DescendantNodes()
            .OfType<AwaitExpressionSyntax>()
            .SelectMany(await => await.DescendantNodes().OfType<ArgumentSyntax>())
            .Any(argument => LooksLikeCancellationToken(argument.Expression));

    private static bool LooksLikeCancellationToken(ExpressionSyntax expression) =>
        expression switch
        {
            // cancellationToken, token, ct
            IdentifierNameSyntax identifier => TokenIdentifiers.Contains(identifier.Identifier.ValueText),
            // activeCts.Token, globalCts.Token — 단 CancellationToken.None은 취소될 수 없다
            MemberAccessExpressionSyntax member =>
                member.Name.Identifier.ValueText == "Token" &&
                member.Expression.ToString() != "CancellationToken",
            _ => false
        };

    private static bool IsBroadCatch(CatchClauseSyntax clause)
    {
        // catch { } — 타입 생략
        if (clause.Declaration is null) return true;

        var typeName = clause.Declaration.Type.ToString();
        return BroadCatchTypes.Contains(typeName);
    }

    /// <summary>
    /// 필터가 취소를 실제로 "제외"할 때만 면제한다.
    ///
    /// when (ex is not OperationCanceledException) → 취소를 빼고 잡는다. 면제.
    /// when (ex is OperationCanceledException)     → 오히려 취소만 골라 삼킨다. 위반.
    ///
    /// 이전 구현은 필터 문자열에 취소 타입 이름이 들어 있는지만 봤기 때문에 두
    /// 형태를 구분하지 못했다. not 하나를 빠뜨려도 조용히 통과했다는 뜻이다.
    /// 이 저장소에는 올바른 형태가 40곳 넘게 있어 복사 대상이 되므로, 방향이
    /// 뒤집힌 복사본은 반드시 잡혀야 한다.
    ///
    /// 문자열 대신 구문 트리를 본다. `is not X`는 UnaryPatternSyntax(not)이고,
    /// `!(x is X)`는 논리 부정 아래의 is 식이다. `not A and not B` 같은 조합도
    /// 하위 노드를 훑으므로 함께 인식된다.
    /// </summary>
    private static bool FiltersCancellation(CatchClauseSyntax clause)
    {
        if (clause.Filter is null) return false;

        return clause.Filter.FilterExpression
            .DescendantNodesAndSelf()
            .Any(ExcludesCancellation);
    }

    private static bool ExcludesCancellation(SyntaxNode node) =>
        node switch
        {
            // ex is not OperationCanceledException
            UnaryPatternSyntax unary when unary.OperatorToken.IsKind(SyntaxKind.NotKeyword) =>
                IsCancellationPattern(unary.Pattern),
            // !(ex is OperationCanceledException) — 같은 뜻의 드문 표기
            PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression) =>
                NegatedOperandTestsCancellation(Unparenthesize(prefix.Operand)),
            _ => false
        };

    private static bool NegatedOperandTestsCancellation(ExpressionSyntax operand) =>
        operand switch
        {
            // ex is OperationCanceledException — 타입 검사는 is 이항식으로 파싱된다
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression) =>
                IsCancellationTypeName(binary.Right),
            // ex is OperationCanceledException oce
            IsPatternExpressionSyntax pattern => IsCancellationPattern(pattern.Pattern),
            _ => false
        };

    private static ExpressionSyntax Unparenthesize(ExpressionSyntax expression) =>
        expression is ParenthesizedExpressionSyntax parenthesized
            ? Unparenthesize(parenthesized.Expression)
            : expression;

    private static bool IsCancellationPattern(PatternSyntax pattern) =>
        pattern switch
        {
            TypePatternSyntax type => IsCancellationTypeName(type.Type),
            DeclarationPatternSyntax declaration => IsCancellationTypeName(declaration.Type),
            ConstantPatternSyntax constant => IsCancellationTypeName(constant.Expression),
            _ => false
        };

    private static bool IsCancellationTypeName(SyntaxNode node) =>
        CancellationTypes.Contains(node.ToString());

    /// <summary>
    /// 본문에 맨 throw(rethrow)가 있으면 취소를 끝내지 않는다.
    /// 조건부 rethrow도 안전으로 본다 - 그 조건은 사람이 의도해 쓴 것이고,
    /// 이 규칙의 임무는 사고를 잡는 것이다. 거짓 음성 방향이므로 안전하다.
    /// </summary>
    private static bool RethrowsEverything(CatchClauseSyntax clause) =>
        clause.Block.DescendantNodes()
            .OfType<ThrowStatementSyntax>()
            .Any(statement => statement.Expression is null);

    /// <summary>
    /// C#은 catch 절을 위에서부터 매칭한다. 앞선 절이 OCE를 잡으면
    /// 뒤의 넓은 catch는 그것을 볼 수 없다.
    /// </summary>
    private static bool EarlierClauseHandlesCancellation(TryStatementSyntax tryStatement, int index)
    {
        for (var earlier = 0; earlier < index; earlier++)
        {
            var declaration = tryStatement.Catches[earlier].Declaration;
            if (declaration is not null && CancellationTypes.Contains(declaration.Type.ToString()))
            {
                return true;
            }
        }

        return false;
    }

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

        // 최상위 문(Program.cs)에는 감싸는 멤버가 없다.
        return "<top-level>";
    }
}

/// <summary>테스트가 bin 아래에서 실행되므로 저장소 루트를 거슬러 올라가 찾는다.</summary>
public static class RepoPaths
{
    public static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReSet.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"ReSet.slnx를 찾지 못해 저장소 루트를 결정할 수 없습니다. 시작 위치: {AppContext.BaseDirectory}");
    }
}
