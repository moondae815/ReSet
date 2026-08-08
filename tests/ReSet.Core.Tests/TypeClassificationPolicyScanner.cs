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
/// null 조건부 호출(`dep.Type?.Contains("TABLE")`)도 잡는다. `a?.b()`는
/// `MemberAccessExpressionSyntax`가 아니라 `ConditionalAccessExpressionSyntax`/
/// `MemberBindingExpressionSyntax`로 파싱되므로 별도 경로가 필요하다 -
/// 정작 이 관용구를 쓰는 유일한 소스 파일이 `SqlObjectTypeClassifier.cs`
/// 자신이라, 그 파일을 참고해 옮겨 적는 것이 사각지대에 이르는 가장
/// 자연스러운 경로였다.
///
/// Trim/ToUpper/ToUpperInvariant/ToLower/ToLowerInvariant로 감싼 수신자는 풀어서
/// 안쪽 수신자로 다시 판정한다(`dep.Type.ToUpper().Contains(...)`,
/// `dep.Type.Trim().ToUpperInvariant().Contains(...)`처럼 여러 겹도 전부 푼다) -
/// 이 저장소가 타입 문자열을 정규화한 뒤 매칭하는 관용구를 이미 세 곳에서 쓰기
/// 때문이다(DependencyAnalysisOrchestrator.cs:329, MetadataExporter.cs:160,
/// DbMetadataService.cs:216). `Contains` 외에 `IndexOf` · `StartsWith` ·
/// `EndsWith`도 같은 판정으로 다룬다 - 넷 다 타입 문자열에 대한 부분 문자열/
/// 접두·접미 판정이고, 수신자 관문(조건 3)이 정밀도를 지켜 준다.
///
/// 이 언래핑은 널 조건부 체인 안에서도 작동한다
/// (`dependencyType?.Trim().ToUpperInvariant().Contains("TABLE")`) - 위 세
/// 근거 지점 중 두 곳(Orchestrator:329, Exporter:160)이 실제로 이 모양이다
/// (`dependencyType?.Trim().ToUpperInvariant()`). 정규화 호출의 *첫* 호출이
/// `?.`로 시작하면 그 호출은 `MemberBindingExpressionSyntax`로 파싱되고 더
/// 안쪽 표현식이 없으므로, 그 경우에는 감싸는
/// `ConditionalAccessExpressionSyntax.Expression`을 진짜 수신자로 본다
/// (`TryFindEnclosingConditionalAccess`).
///
/// 알려진 한계(전부 실험으로 확인 - 임의로 적지 않았다):
/// - `var t = dep.Type; t.Contains("TABLE")`처럼 타입 문자열을 이름이 다른
///   지역 변수로 옮겨 담으면 놓친다 - 수신자 이름만 보고 대입 체인을 추적하지
///   않기 때문이다.
/// - `dep?.Type?.Trim().Contains("TABLE")`처럼 조건부 접근이 두 번 이상
///   이어지면 가장 안쪽 조건부 접근식의 `Expression`(`MemberBindingExpressionSyntax`)
///   에서 멈추고 더 풀지 않는다 - 이 저장소의 실제 호출부는 한 단계 이상
///   이어지지 않으므로 실용적으로 충분하다.
/// - `Contains`/`IndexOf`/`StartsWith`/`EndsWith` 외의 문자열 메서드(`Equals`,
///   정규식 등)로 타입을 판정하는 형태는 놓친다 - 지금까지 이 저장소에서
///   관측된 결함은 전부 이 넷의 조합이었다.
/// - `ToString`/`Substring`처럼 정규화 목록(Trim/ToUpper류) 밖의 메서드로 감싼
///   수신자는 풀지 않는다 - 목록을 무제한으로 넓히면 임의의 변환 뒤에서도
///   게이트가 뚫려 정밀도가 무너진다.
/// </summary>
public static class TypeClassificationPolicyScanner
{
    private static readonly HashSet<string> SqlTypeLiterals = new(StringComparer.OrdinalIgnoreCase)
    {
        "TABLE", "VIEW", "FUNCTION", "PROCEDURE"
    };

    /// <summary>
    /// 타입 문자열에 대한 부분 문자열/접두·접미 판정으로 취급하는 메서드.
    /// 넷 다 리터럴 하나를 인자로 받아 문자열 안에서 위치나 존재를 묻는다.
    /// </summary>
    private static readonly HashSet<string> SqlTypeMatchMethods = new(StringComparer.Ordinal)
    {
        "Contains", "IndexOf", "StartsWith", "EndsWith"
    };

    /// <summary>
    /// 수신자를 감싸는 정규화 호출. 타입 문자열 자체는 바꾸지 않고 대소문자/공백만
    /// 다듬으므로, 이 호출들 안쪽의 진짜 수신자를 계속 판정 대상으로 본다.
    /// </summary>
    private static readonly HashSet<string> ReceiverNormalizationMethods = new(StringComparer.Ordinal)
    {
        "Trim", "ToUpper", "ToUpperInvariant", "ToLower", "ToLowerInvariant"
    };

    /// <summary>이 파일이 정책의 구현체다. 여기서는 부분 문자열 판정이 임무다.</summary>
    private const string ClassifierRelativePath = "ReSet.Core/Services/SqlObjectTypeClassifier.cs";

    public static IReadOnlyList<TypeClassificationOffender> ScanDirectory(string srcRoot)
    {
        var offenders = new List<TypeClassificationOffender>();
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

            // 파일명이 아니라 상대 경로 전체로 면제를 판정한다 - 파일명만 보면
            // src/ 아래 어디에 같은 이름의 파일을 두든 게이트를 빠져나간다.
            if (relative.Equals(ClassifierRelativePath, StringComparison.OrdinalIgnoreCase))
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
            if (!TryGetMatchReceiver(invocation, out var receiver, out var reportNode)) continue;
            if (!HasSqlTypeLiteralArgument(invocation)) continue;
            if (!IsSqlTypeExpression(receiver)) continue;

            var line = tree.GetLineSpan(reportNode.Span).StartLinePosition.Line + 1;
            offenders.Add(new TypeClassificationOffender(relativePath, line, reportNode.ToString()));
        }

        return offenders;
    }

    /// <summary>
    /// `Contains`/`IndexOf`/`StartsWith`/`EndsWith` 호출의 수신자를 두 형태에서
    /// 뽑아낸다 - 일반 멤버 접근(`dep.Type.Contains(...)`)과 null 조건부 접근
    /// (`dep.Type?.Contains(...)`). 후자는 `invocation.Expression`이
    /// `MemberBindingExpressionSyntax`이고, 진짜 수신자는 감싸는
    /// `ConditionalAccessExpressionSyntax.Expression`에 있다. `reportNode`는
    /// 위반 메시지에 담을 전체 식이다 - 조건부 접근 경로에서는 `invocation`
    /// 자체가 `.Contains(...)` 부분만 가리키므로 감싸는
    /// `ConditionalAccessExpressionSyntax` 전체(`dep.Type?.Contains(...)`)를 쓴다.
    /// </summary>
    private static bool TryGetMatchReceiver(
        InvocationExpressionSyntax invocation,
        out ExpressionSyntax receiver,
        out SyntaxNode reportNode)
    {
        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax member when SqlTypeMatchMethods.Contains(member.Name.Identifier.ValueText):
                receiver = member.Expression;
                reportNode = invocation;
                return true;

            case MemberBindingExpressionSyntax binding
                when SqlTypeMatchMethods.Contains(binding.Name.Identifier.ValueText) &&
                     invocation.Parent is ConditionalAccessExpressionSyntax conditional:
                receiver = conditional.Expression;
                reportNode = conditional;
                return true;

            default:
                receiver = null!;
                reportNode = null!;
                return false;
        }
    }

    private static bool HasSqlTypeLiteralArgument(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments.Any(argument =>
            argument.Expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression) &&
            SqlTypeLiterals.Contains(literal.Token.ValueText));

    /// <summary>
    /// 수신자가 SQL 타입 문자열인가. 이 조건이 정밀도의 핵심이다 -
    /// logUpper.Contains("TABLE") 같은 로그 텍스트 매칭은 타입 분류가 아니다.
    /// 판정에 앞서 Trim/ToUpper류로 감싼 겹을 전부 벗겨 안쪽의 진짜 수신자를 본다.
    /// </summary>
    private static bool IsSqlTypeExpression(ExpressionSyntax receiver)
    {
        while (TryUnwrapNormalizationCall(receiver, out var inner))
        {
            receiver = inner;
        }

        return receiver switch
        {
            // type, objectType, dependencyType
            IdentifierNameSyntax identifier => IsTypeName(identifier.Identifier.ValueText),
            // dep.Type, d.Type, rawDep.Type
            MemberAccessExpressionSyntax member => IsTypeName(member.Name.Identifier.ValueText),
            _ => false
        };
    }

    /// <summary>
    /// `expression`이 `x.Trim()`/`x.ToUpper()`/... 형태의 정규화 호출이면 안쪽
    /// 수신자 `x`를 꺼낸다. 여러 겹(`x.Trim().ToUpperInvariant()`)은 호출부가
    /// 반복 호출해 겹을 하나씩 벗긴다.
    ///
    /// 두 형태를 구분해야 한다. 일반 멤버 접근(`x.Trim()`)은
    /// `invocation.Expression`이 `MemberAccessExpressionSyntax`이고, 안쪽
    /// 수신자가 `member.Expression`에 그대로 있다. 그런데 널 조건부 체인의
    /// *첫* 호출(`dependencyType?.Trim()`의 `?.Trim` 부분)은
    /// `MemberBindingExpressionSyntax`로 파싱되고, 그 노드에는 더 안쪽
    /// 표현식이 없다 - 진짜 수신자(`dependencyType`)는 감싸는
    /// `ConditionalAccessExpressionSyntax.Expression`에 있다. 이 갈래를
    /// 놓치면 `dependencyType?.Trim().ToUpperInvariant().Contains("TABLE")`
    /// 처럼 이 저장소의 실제 정규화 관용구(DependencyAnalysisOrchestrator.cs:329,
    /// MetadataExporter.cs:160)와 같은 모양이 통째로 사각지대가 된다.
    /// </summary>
    private static bool TryUnwrapNormalizationCall(ExpressionSyntax expression, out ExpressionSyntax inner)
    {
        if (expression is InvocationExpressionSyntax invocation)
        {
            switch (invocation.Expression)
            {
                case MemberAccessExpressionSyntax member
                    when ReceiverNormalizationMethods.Contains(member.Name.Identifier.ValueText):
                    inner = member.Expression;
                    return true;

                case MemberBindingExpressionSyntax binding
                    when ReceiverNormalizationMethods.Contains(binding.Name.Identifier.ValueText) &&
                         TryFindEnclosingConditionalAccess(invocation, out var conditional):
                    inner = conditional.Expression;
                    return true;
            }
        }

        inner = null!;
        return false;
    }

    /// <summary>
    /// `node`를 감싸는 가장 가까운 `ConditionalAccessExpressionSyntax`를 찾는다.
    /// `MemberBindingExpressionSyntax`는 항상 그 조건부 접근식의 `WhenNotNull`
    /// 안에서만 나타나므로, 부모를 따라 올라가면 반드시 만난다.
    /// </summary>
    private static bool TryFindEnclosingConditionalAccess(
        SyntaxNode node,
        out ConditionalAccessExpressionSyntax conditional)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (current is ConditionalAccessExpressionSyntax candidate)
            {
                conditional = candidate;
                return true;
            }
        }

        conditional = null!;
        return false;
    }

    private static bool IsTypeName(string name) =>
        name.Equals("type", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Type", StringComparison.Ordinal);
}
