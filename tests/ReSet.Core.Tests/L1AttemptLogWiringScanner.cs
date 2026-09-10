using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReSet.Core.Tests;

/// <summary><c>L1AttemptLog.Append(...)</c> 호출 한 곳과, 그것이 「!무엇.IsValid」 가드 안인지.</summary>
public sealed record L1AttemptLogAppendSite(string RelativePath, int Line, bool GuardedByIsValidNegation);

/// <summary>
/// 이 저장소 전체에서 <c>L1AttemptLog.Append(...)</c> 호출을 구문 트리로 찾는다.
///
/// [왜 필요한가 - 2026-09-10 최종 리뷰 발견] <c>VerificationPipelineOrchestrator.cs</c>
/// 의 이 한 자리가 거부된 시도 코퍼스와 파이프라인을 잇는 <b>유일한</b> 배선이다.
/// 시험이 하나도 이 자리를 참조하지 않는다 - 코퍼스 픽스처가 이미 커밋돼 있어
/// <c>SelfReinforcingCheckTests</c> 는 그 픽스처만 읽지 이 배선을 거치지 않는다.
/// 그래서 이 <c>if</c> 블록을 통째로 지워도 빌드가 깨끗하고 기존 시험 전부가
/// 영구히 초록이다. 파이프라인 전체를 돌리는 통합 시험은 비싸다 - 구문 트리로
/// 「정확히 하나, 그리고 L1 실패 가드 안」을 잠그는 쪽이 <see cref="L1FiringKeyPolicyScanner"/>
/// ·<see cref="CancellationPolicyScanner"/> 와 같은 관례로 싸게 선다.
///
/// 시맨틱 모델(컴파일 필요)을 쓰지 않고 구문 트리만 본다 - 같은 이유로 충분하다.
/// </summary>
public static class L1AttemptLogWiringScanner
{
    public static IReadOnlyList<L1AttemptLogAppendSite> ScanFile(string absolutePath, string relativePath) =>
        ScanSource(File.ReadAllText(absolutePath), relativePath);

    public static IReadOnlyList<L1AttemptLogAppendSite> ScanSource(string source, string relativePath)
    {
        var sites = new List<L1AttemptLogAppendSite>();
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // 찾는 모양: L1AttemptLog.Append(...)
            if (invocation.Expression is not MemberAccessExpressionSyntax append) continue;
            if (append.Name.Identifier.ValueText != "Append") continue;
            if (append.Expression is not IdentifierNameSyntax type) continue;
            if (type.Identifier.ValueText != "L1AttemptLog") continue;

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            sites.Add(new L1AttemptLogAppendSite(relativePath, line, IsGuardedByIsValidNegation(invocation)));
        }

        return sites;
    }

    /// <summary>
    /// 호출을 감싸는 조상 중 <c>if (!무엇.IsValid)</c> 모양의 조건이 있는지 본다.
    /// 중첩된 안쪽 <c>if</c>(예: null 가드)를 지나 바깥까지 전부 본다 - 실물이
    /// 두 겹 <c>if</c> 안에 있다(안쪽은 outputPaths null 가드).
    /// </summary>
    private static bool IsGuardedByIsValidNegation(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is IfStatementSyntax ifStatement && IsNegatedIsValid(ifStatement.Condition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>!무엇.IsValid</c> 모양만 받는다. <c>&amp;&amp;</c> 로 묶인 복합 조건까지
    /// 넓히면 이 잠금이 막으려던 「아무 조건이나 통과」가 다시 열린다 - 실물 모양이
    /// 정확히 이것 하나이므로 좁게 잡는다.
    /// </summary>
    private static bool IsNegatedIsValid(ExpressionSyntax condition) =>
        condition is PrefixUnaryExpressionSyntax { OperatorToken.Text: "!" } prefix &&
        prefix.Operand is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "IsValid" };
}
