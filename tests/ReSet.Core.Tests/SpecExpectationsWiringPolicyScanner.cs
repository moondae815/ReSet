using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReSet.Core.Tests;

/// <summary>두 번째 인자 없이 호출된 `_validator.Validate(...)` 한 곳.</summary>
public sealed record ValidatorCallExpectationsOffender(int Line, string Expression);

/// <summary>
/// `VerificationPipelineOrchestrator.RunCodeObjectPipelineCoreAsync` 안의
/// `_validator.Validate(...)` 호출 6곳이 전부 `specExpectations`를 두 번째 인자로
/// 넘기는지 구문 트리로 확인한다.
///
/// 이 검사가 필요한 이유: 배선(Task 5)에는 새 단위 테스트가 없다 - 계획서에
/// 명시된 검증은 "6개 호출부가 두 번째 인자를 받는다"는 정적 사실뿐이다.
/// 기존 오케스트레이터 테스트는 전부 UPDATE 매핑이 없는 SpDefinition을 쓰므로
/// FromStaticAnalysis가 항상 null을 돌려주고, null을 넘기든 아예 안 넘기든
/// MechanicalValidator.Validate의 동작이 똑같다 - 배선을 통째로 지워도 스위트가
/// 초록으로 남는다. 이 스캐너는 그 사각지대를 인자 개수라는 구문적 사실로 메운다.
///
/// 파일 하나로 스코프를 좁힌 이유: `_validator`·`Validate`라는 이름 자체는
/// 이 저장소에 유일하지 않다 - `SpecificationLinker.cs`도 같은 이름의 필드와
/// 메서드를 갖고 있고, 거기서는 1인자 호출이 의도된 정상 동작이다(참조 섹션을
/// 덧붙인 뒤 정화 목적으로만 부르고 IsValid를 보지 않는다). 이름만 보고 전체
/// src를 스캔하면 그 정상 호출을 오탐으로 잡는다. 그래서 이 스캐너는 처음부터
/// 특정 파일의 소스 텍스트만 받는다 - 호출부가 파일을 골라 넘긴다.
///
/// 시맨틱 모델(컴파일 필요)을 쓰지 않고 구문 트리만 본다. 같은 계열의
/// CancellationPolicyScanner·TypeClassificationPolicyScanner와 동일한 선택이다.
/// </summary>
public static class SpecExpectationsWiringPolicyScanner
{
    public static IReadOnlyList<ValidatorCallExpectationsOffender> ScanSource(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetRoot();
        var offenders = new List<ValidatorCallExpectationsOffender>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member) continue;
            if (member.Name.Identifier.ValueText != "Validate") continue;
            if (member.Expression is not IdentifierNameSyntax receiver) continue;
            if (receiver.Identifier.ValueText != "_validator") continue;

            // MechanicalValidator.Validate(markdown, expectations = null)는 정확히
            // 2개 인자여야 배선이 살아 있다는 뜻이다. 1개면 기본값 null로 조용히
            // 떨어져 컴파일은 통과하지만 UPDATE 매핑 대조가 통째로 꺼진다.
            if (invocation.ArgumentList.Arguments.Count == 2) continue;

            var line = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
            offenders.Add(new ValidatorCallExpectationsOffender(line, invocation.ToString()));
        }

        return offenders;
    }

    public static IReadOnlyList<ValidatorCallExpectationsOffender> ScanFile(string filePath) =>
        ScanSource(File.ReadAllText(filePath));
}
