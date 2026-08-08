# 타입 분류 판정 일원화와 정책 스캐너 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SQL 객체 타입 문자열에 대한 원시 부분 문자열 판정을 코드베이스에서 없애고, 다시 생기면 테스트가 막게 한다.

**Architecture:** Roslyn 구문 트리로 `src/` 전체를 훑어 위반을 찾는 `TypeClassificationPolicyScanner`를 만들고(기존 `CancellationPolicyScanner`와 같은 구성), 남은 다섯 곳의 원시 판정을 `SqlObjectTypeClassifier`로 위임한 뒤, 스캐너가 위반 0건을 보고하는 것을 정책 테스트로 고정한다. 함께 `StaticAnalysisNormalizer`의 `]` 손상을 고치고 스캐너가 대체하는 테스트 3건을 삭제한다.

**Tech Stack:** C# / .NET 10, xUnit, NSubstitute, `Microsoft.CodeAnalysis.CSharp` 5.6.0 (테스트 프로젝트에 이미 있음)

**설계 문서:** `docs/superpowers/specs/2026-08-09-type-classification-policy-design.md` (커밋 `42aaf65`)

## Global Constraints

- 기준선: `dotnet clean && dotnet build`에서 오류 0건, **경고 정확히 8건**. 이 8건은 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 CS8600/CS8602이며 이번 작업의 범위가 아니다. 경고 수는 clean 빌드에서만 의미가 있다 — `dotnet test` 직후의 증분 `dotnet build`는 0건을 보고한다.
- 기준선: `dotnet test`가 **1,135건 통과, 0건 실패**.
- 최종 기대 테스트 수: `1,135 − 3(삭제) + 신규분`.
- 새 예외 경로를 만들지 않는다. `IsTableOrView` · `IsCodeObject` · `ResolveCodeObjectType`은 모두 null 입력에 안전하다(각각 `false` · `false` · `CodeObjectType.Unresolved`).
- `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`의 `logUpper.Contains("TABLE")`(1277·1399행)은 **로그 텍스트 매칭이지 타입 분류가 아니다.** 건드리지 말고, 스캐너가 이것을 오탐하지 않아야 한다.
- 네 대상 파일(`SettlementPolicyService` · `AiService` · `SnapshotManager` · `MetadataExporter`)은 모두 `namespace ReSet.Core.Services`이고 `using ReSet.Core.Models;`를 이미 갖고 있다. **새 `using` 지시문이 필요 없다.**
- 문서는 한국어로 쓴다. 주석은 "무엇"이 아니라 "왜"를 적는다.

## File Structure

| 파일 | 책임 | 상태 |
|---|---|---|
| `tests/ReSet.Core.Tests/TypeClassificationPolicyScanner.cs` | 구문 트리로 원시 타입 판정을 찾아내는 스캐너 | 생성 (Task 1) |
| `tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs` | 스캐너의 양성·음성 고정 + `src/` 전체 정책 테스트 | 생성 (Task 1), 추가 (Task 2) |
| `src/ReSet.Core/Services/SettlementPolicyService.cs` | 정산 정책 프로파일링 — 위임 2곳 | 수정 (Task 2) |
| `src/ReSet.Core/Services/AiService.cs` | 프롬프트 조립 — 위임 1곳 | 수정 (Task 2) |
| `src/ReSet.Core/Services/SnapshotManager.cs` | 오프라인 스냅샷 수집 — 사본 메서드 삭제 | 수정 (Task 2) |
| `src/ReSet.Core/Services/MetadataExporter.cs` | 산출물 내보내기 — 위임 1곳 | 수정 (Task 2) |
| `tests/ReSet.Core.Tests/SettlementPolicyServiceTests.cs` | TVF 제외 동작 검증 2건 추가 | 수정 (Task 2) |
| `src/ReSet.Core/Services/StaticAnalysisNormalizer.cs` | 식별자 정규화 — `]` 손상 수정 | 수정 (Task 3) |
| `tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs` | `]` 보존 검증 추가 | 수정 (Task 3) |
| `tests/ReSet.Core.Tests/CacheManagerTests.cs` | 중복 테스트 1건 삭제 | 수정 (Task 4) |
| `tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs` | 중복 1건 + 대체된 가드 1건 + 헬퍼 삭제 | 수정 (Task 4) |
| `docs/architecture.md`, `AGENTS.md` | 규칙과 근거 기록 | 수정 (Task 4) |

## 태스크 의존성

```
Task 1 (스캐너)  ──┐
                   ├──> Task 2 (위임 5곳 + 정책 테스트) ──> Task 4 (삭제 + 문서)
Task 3 (] 수정) ───────────────────────────────────────────┘
```

Task 3은 다른 어느 태스크와도 파일이 겹치지 않으므로 언제 해도 된다. Task 2는 Task 1의 스캐너가 있어야 정책 테스트를 쓸 수 있다. Task 4의 가드 삭제는 Task 2의 정책 테스트가 그 자리를 대신한 뒤라야 안전하다.

---

## Task 1: TypeClassificationPolicyScanner

**Files:**
- Create: `tests/ReSet.Core.Tests/TypeClassificationPolicyScanner.cs`
- Create: `tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs`

**Interfaces:**
- Consumes: `RepoPaths.FindRepoRoot()` — `tests/ReSet.Core.Tests/CancellationPolicyScanner.cs:240`에 이미 있는 `public static class RepoPaths`의 메서드. `ReSet.slnx`를 찾을 때까지 상위로 거슬러 올라가 저장소 루트의 절대 경로를 돌려준다. 새로 만들지 말고 그대로 쓴다.
- Produces:
  - `public sealed record TypeClassificationOffender(string RelativePath, int Line, string Expression)`
  - `public static IReadOnlyList<TypeClassificationOffender> TypeClassificationPolicyScanner.ScanSource(string sourceText, string relativePath)`
  - `public static IReadOnlyList<TypeClassificationOffender> TypeClassificationPolicyScanner.ScanDirectory(string srcRoot)`

**참고할 기존 코드:** `tests/ReSet.Core.Tests/CancellationPolicyScanner.cs`. 같은 저장소의 같은 목적을 가진 스캐너이며, 디렉터리 순회·`bin`/`obj` 제외·행 번호 계산 방식을 그대로 따른다. 두 스캐너는 서로 참조하지 않고 독립적으로 둔다.

**위반의 정의 (이 태스크의 핵심 사양):**

구문 트리에서 다음 세 조건을 **모두** 만족하는 `InvocationExpressionSyntax`가 위반이다.

1. 멤버 접근의 이름이 `Contains`
2. 인자 중에 문자열 리터럴 `TABLE` · `VIEW` · `FUNCTION` · `PROCEDURE` 중 하나가 있다 (대소문자 무시)
3. 수신자가 SQL 타입 표현식이다 — 이름이 `Type`으로 끝나는 멤버 접근(`dep.Type`)이거나, 이름이 `type`(대소문자 무시)이거나 `Type`으로 끝나는 식별자(`objectType`)

3번이 오탐을 막는 장치다. 이것이 없으면 `logUpper.Contains("TABLE")` 같은 로그 텍스트 매칭이 걸린다.

---

- [ ] **Step 1: 스캐너 테스트 파일을 만든다 (양성 3건)**

`tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs`를 새로 만든다.

```csharp
using System.Linq;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class TypeClassificationPolicyTests
{
    // 규칙: SQL 객체 타입 문자열에 대한 원시 부분 문자열 판정은 위반이다.
    // "SQL_TABLE_VALUED_FUNCTION"이 "TABLE"을 포함하므로, 호출부마다 따로
    // 판정하면 TVF가 테이블로 오분류된다. 판정은 SqlObjectTypeClassifier
    // 한곳에서만 한다.

    [Fact]
    public void Scanner_FlagsAMemberAccessTypeCheck()
    {
        var source = @"
class C
{
    bool M(D dep) => dep.Type.Contains(""TABLE"");
}
class D { public string Type { get; set; } }";

        var offender = Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
        Assert.Equal("Fake.cs", offender.RelativePath);
        Assert.Contains("dep.Type.Contains", offender.Expression);
    }

    [Fact]
    public void Scanner_FlagsAnIdentifierNamedLikeAType()
    {
        // 변수명이 매번 달랐던 것이 이 결함이 네 번 반복된 이유다:
        // rawDep.Type, dep.Type, objectType, d.Type, type.
        var source = @"
class C
{
    bool M(string objectType) => objectType.Contains(""VIEW"");
}";

        Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_FlagsAnOrdinalIgnoreCaseCheck()
    {
        // 비교 옵션 인자가 붙어도 같은 결함이다.
        var source = @"
class C
{
    bool M(string type) =>
        type.Contains(""PROCEDURE"", System.StringComparison.OrdinalIgnoreCase);
}";

        Assert.Single(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }
}
```

- [ ] **Step 2: 컴파일 실패를 확인한다**

Run: `dotnet build tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`
Expected: FAIL — `CS0103: 'TypeClassificationPolicyScanner' 이름이 현재 컨텍스트에 없습니다`

- [ ] **Step 3: 스캐너를 구현한다**

`tests/ReSet.Core.Tests/TypeClassificationPolicyScanner.cs`를 새로 만든다.

```csharp
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
```

파싱할 수 없는 파일에 대한 처리를 따로 넣지 않는 이유: `CSharpSyntaxTree.ParseText`는 구문 오류가 있어도 예외를 던지지 않고 오류 노드를 담은 트리를 돌려준다. 그런 트리에서는 위반 서명이 매칭되지 않아 자연히 건너뛴 것과 같은 결과가 된다. `try`/`catch`를 두면 절대 실행되지 않는 코드가 된다. 형제 규칙인 `CancellationPolicyScanner`도 같은 이유로 두지 않았다.

- [ ] **Step 4: 양성 3건이 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~TypeClassificationPolicyTests"`
Expected: PASS 3건

- [ ] **Step 5: 음성 테스트 4건을 추가한다**

`TypeClassificationPolicyTests` 클래스의 마지막 `}` 앞에 붙인다.

```csharp
    [Fact]
    public void Scanner_DoesNotFlagLogTextMatching()
    {
        // 실례: src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs.
        // AI가 돌려준 로그 텍스트에서 단어를 찾는 것이지 타입 분류가 아니다.
        // 여기서 거짓 양성을 내면 규칙이 버려진다.
        var source = @"
class C
{
    bool M(string logUpper) => logUpper.Contains(""TABLE"");
}";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagTheSameTextInsideAComment()
    {
        var source = @"
class C
{
    // 예전에는 dep.Type.Contains(""TABLE"")로 판정했다.
    bool M(D dep) => SqlObjectTypeClassifier.IsTableOrView(dep.Type);
}
class D { public string Type { get; set; } }
static class SqlObjectTypeClassifier { public static bool IsTableOrView(string t) => false; }";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagTheSameTextInsideAStringLiteral()
    {
        var source = @"
class C
{
    string M() => ""dep.Type.Contains(\""TABLE\"")"";
}";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagAContainsCallWithAnUnrelatedLiteral()
    {
        // 리터럴 집합이 실제로 관문 역할을 하는지 고정한다. 이것이 없으면
        // 조건 3(수신자)만으로 통과하는지 조건 2(리터럴)도 보는지 구분되지 않는다.
        var source = @"
class C
{
    bool M(D dep) => dep.Type.Contains(""SYNONYM"");
}
class D { public string Type { get; set; } }";

        Assert.Empty(TypeClassificationPolicyScanner.ScanSource(source, "Fake.cs"));
    }
```

- [ ] **Step 6: 음성 4건을 실행한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~TypeClassificationPolicyTests"`
Expected: PASS 7건

주석·문자열 두 건은 파서를 쓰기 때문에 구현상 자동으로 통과한다. 통과 자체가 목적이 아니라 "정규식이 아니라 파서를 쓴다"는 선택을 고정하는 것이 목적이므로 남겨 둔다.

- [ ] **Step 7: 전체 테스트를 실행한다**

Run: `dotnet test`
Expected: 기존 1,135건 + 신규 7건 = 1,142건 통과, 0건 실패

- [ ] **Step 8: 커밋**

```bash
git add tests/ReSet.Core.Tests/TypeClassificationPolicyScanner.cs \
        tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs
git commit -m "test: add a syntax-tree scanner for raw SQL type classification

같은 결함이 네 번 발견됐고 매번 표기가 달랐다. 변수명에 의존하는
grep 가드는 다음 변수명을 못 잡는다. CancellationPolicyScanner와
같은 방식으로 구문 트리를 본다.

이 커밋은 스캐너와 그 자체 테스트만 담는다. src/ 전체를 검사하는
정책 테스트는 위반 다섯 곳을 고친 뒤에 추가한다."
```

---

## Task 2: 다섯 곳을 SqlObjectTypeClassifier로 위임하고 정책 테스트를 건다

**Files:**
- Modify: `src/ReSet.Core/Services/SettlementPolicyService.cs:46`, `:157`
- Modify: `src/ReSet.Core/Services/AiService.cs:221`
- Modify: `src/ReSet.Core/Services/SnapshotManager.cs:77`, `:89`, `:158-171`
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs:340`
- Modify: `tests/ReSet.Core.Tests/SettlementPolicyServiceTests.cs`
- Modify: `tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs`
- Test: `tests/ReSet.Core.Tests/SettlementPolicyServiceTests.cs`, `tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs`

**Interfaces:**
- Consumes: `TypeClassificationPolicyScanner.ScanDirectory(string srcRoot)` → `IReadOnlyList<TypeClassificationOffender>` (Task 1), `RepoPaths.FindRepoRoot()` → `string`
- Consumes: `ReSet.Core.Services.SqlObjectTypeClassifier`의 세 정적 메서드 —
  `bool IsCodeObject(string? sqlObjectType)`,
  `bool IsTableOrView(string? sqlObjectType)`,
  `CodeObjectType ResolveCodeObjectType(string? sqlObjectType)`.
  `CodeObjectType`은 `ReSet.Core.Models`의 열거형이며 `Procedure` · `Function` · `Unresolved` 값을 갖는다.
- Produces: 없음 (동작 변경만)

**왜 다섯 곳이 한 태스크인가:** 정책 테스트는 전부 아니면 전무다. 리뷰어가 "다섯 중 셋만 승인"할 수 없으므로 하나의 검증 관문을 공유한다.

**동작 변화 요약:**

| 위치 | 변경 | 동작 변화 |
|---|---|---|
| `SettlementPolicyService.cs:46` | `IsTableOrView(dep.Type)` | TVF가 프로파일링 대상에서 빠짐 |
| `SettlementPolicyService.cs:157` | `IsTableOrView(d.Type)` | TVF 참조가 테이블 경고 귀속에서 빠짐 |
| `AiService.cs:221` | `IsCodeObject(dep.Type)` | 대소문자 무시로 바뀜 |
| `SnapshotManager.cs:158` | private 메서드 삭제 | 없음 |
| `MetadataExporter.cs:340` | `ResolveCodeObjectType(...) == Procedure` | 없음 |

---

- [ ] **Step 1: 정책 테스트를 먼저 추가한다**

`tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs`의 마지막 `}` 앞에 붙인다.

```csharp
    [Fact]
    public void NoRawSqlTypeClassificationRemainsInSource()
    {
        // baseline 파일을 두지 않는 이유: 다섯 곳을 전부 고칠 수 있으므로 목표가
        // 0이다. 빈 baseline은 "0을 단언한다"를 돌려 말한 것에 불과하다. 정당한
        // 예외가 실제로 생기면 그때 도입한다.
        var repoRoot = RepoPaths.FindRepoRoot();
        var offenders = TypeClassificationPolicyScanner
            .ScanDirectory(System.IO.Path.Combine(repoRoot, "src"));

        var report = string.Join(
            "\n",
            offenders.Select(offender =>
                $"  {offender.RelativePath}:{offender.Line}  {offender.Expression}"));

        Assert.True(
            offenders.Count == 0,
            "SQL 객체 타입을 직접 부분 문자열로 판정하는 곳이 남아 있습니다. " +
            "SqlObjectTypeClassifier의 IsTableOrView / IsCodeObject / ResolveCodeObjectType로 " +
            "위임하십시오. \"SQL_TABLE_VALUED_FUNCTION\"이 \"TABLE\"을 포함하므로 " +
            $"직접 판정하면 TVF가 테이블로 오분류됩니다.\n\n{report}");
    }
```

- [ ] **Step 2: 정책 테스트가 실패하는 것을 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~NoRawSqlTypeClassificationRemainsInSource"`
Expected: FAIL — 다섯 곳이 나열된다.

```
  ReSet.Core/Services/AiService.cs:221   dep.Type.Contains("FUNCTION")
  ReSet.Core/Services/AiService.cs:221   dep.Type.Contains("PROCEDURE")
  ReSet.Core/Services/MetadataExporter.cs:340   dep.Type.Contains("PROCEDURE")
  ReSet.Core/Services/SettlementPolicyService.cs:46   dep.Type.Contains("TABLE")
  ...
```

실제 목록이 이와 다르면 멈추고 보고한다. 스캐너가 예상 밖의 곳을 잡았거나(오탐) 예상한 곳을 놓쳤다는 뜻이다.

- [ ] **Step 3: SettlementPolicyService의 실패 테스트 2건을 쓴다**

`tests/ReSet.Core.Tests/SettlementPolicyServiceTests.cs`의 클래스 마지막 `}` 앞에 붙인다.

```csharp
        [Fact]
        public async Task GenerateSettlementPolicyRulebookAsync_ShouldNotProfileATableValuedFunction()
        {
            // "SQL_TABLE_VALUED_FUNCTION"이 "TABLE"을 포함하므로 원시 판정은 TVF를
            // 프로파일링 대상으로 넣는다. 그러면 인자가 필요한 함수를 인자 없이
            // SELECT ... FROM 으로 읽으려 해 실패한다. 이름이 코드성 키워드에
            // 걸릴 때만 대상이 되므로 여기서는 "Rate"와 "Map"을 모두 가진
            // 이름을 쓴다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var service = new SettlementPolicyService(dbService, aiService);

            var connectionString = "Server=localhost;Database=Northwind;Integrated Security=true;TrustServerCertificate=true;";
            var spList = new List<string> { "dbo.sp_UsesFunction" };

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "sp_UsesFunction",
                DdlText = "SELECT * FROM dbo.UIF_RateMap(@d)",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo
                    {
                        Schema = "dbo",
                        Name = "UIF_RateMap",
                        Type = "SQL_TABLE_VALUED_FUNCTION"
                    }
                }
            };

            dbService.GetSpDetailsAsync(connectionString, "dbo", "sp_UsesFunction", 3, Arg.Any<CancellationToken>())
                .Returns(spDef);
            aiService.GenerateSettlementPolicyRulebookAsync(Arg.Any<List<SpDefinition>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "Generated Policy Document" }));

            await service.GenerateSettlementPolicyRulebookAsync(connectionString, spList, 3, CancellationToken.None);

            await dbService.DidNotReceive().GetTableDataPreviewAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GenerateSettlementPolicyRulebookAsync_ShouldNotWarnAnSpThatOnlyReferencesASameNamedFunction()
        {
            // 테이블 프로파일링 경고는 그 테이블을 참조하는 SP에만 붙어야 한다.
            // 원시 판정은 같은 스키마·이름의 TVF를 참조하는 SP에도 경고를 붙인다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var service = new SettlementPolicyService(dbService, aiService);

            var connectionString = "Server=localhost;Database=Northwind;Integrated Security=true;TrustServerCertificate=true;";
            var spList = new List<string> { "dbo.sp_UsesTable", "dbo.sp_UsesFunction" };

            var tableSp = new SpDefinition
            {
                Schema = "dbo",
                Name = "sp_UsesTable",
                DdlText = "SELECT * FROM dbo.RateMap",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo { Schema = "dbo", Name = "RateMap", Type = "USER_TABLE" }
                }
            };

            var functionSp = new SpDefinition
            {
                Schema = "dbo",
                Name = "sp_UsesFunction",
                DdlText = "SELECT * FROM dbo.RateMap(@d)",
                Dependencies = new List<DependencyInfo>
                {
                    new DependencyInfo { Schema = "dbo", Name = "RateMap", Type = "SQL_TABLE_VALUED_FUNCTION" }
                }
            };

            dbService.GetSpDetailsAsync(connectionString, "dbo", "sp_UsesTable", 3, Arg.Any<CancellationToken>())
                .Returns(tableSp);
            dbService.GetSpDetailsAsync(connectionString, "dbo", "sp_UsesFunction", 3, Arg.Any<CancellationToken>())
                .Returns(functionSp);

            // 빈 결과 -> "데이터가 비어있습니다" 경고 경로를 탄다.
            dbService.GetTableDataPreviewAsync(connectionString, null, "dbo", "RateMap", 100, Arg.Any<CancellationToken>())
                .Returns(new List<Dictionary<string, object>>());

            aiService.GenerateSettlementPolicyRulebookAsync(Arg.Any<List<SpDefinition>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "Generated Policy Document" }));

            await service.GenerateSettlementPolicyRulebookAsync(connectionString, spList, 3, CancellationToken.None);

            Assert.Single(tableSp.Warnings);
            Assert.Empty(functionSp.Warnings);
        }
```

- [ ] **Step 4: 두 테스트가 실패하는 것을 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~SettlementPolicyServiceTests"`
Expected: 신규 2건 FAIL

- `ShouldNotProfileATableValuedFunction` — `GetTableDataPreviewAsync`가 1회 호출되어 `DidNotReceive`가 깨진다
- `ShouldNotWarnAnSpThatOnlyReferencesASameNamedFunction` — `functionSp.Warnings`에 1건이 들어가 `Assert.Empty`가 깨진다

- [ ] **Step 5: SettlementPolicyService 두 곳을 위임한다**

`src/ReSet.Core/Services/SettlementPolicyService.cs:46`

```csharp
                    if (SqlObjectTypeClassifier.IsTableOrView(dep.Type))
```

`src/ReSet.Core/Services/SettlementPolicyService.cs:157`

```csharp
                    SqlObjectTypeClassifier.IsTableOrView(d.Type));
```

- [ ] **Step 6: 두 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~SettlementPolicyServiceTests"`
Expected: PASS 전부 (기존 3건 + 신규 2건)

- [ ] **Step 7: AiService를 위임한다**

`src/ReSet.Core/Services/AiService.cs:221`

```csharp
                else if (SqlObjectTypeClassifier.IsCodeObject(dep.Type))
```

- [ ] **Step 8: SnapshotManager의 사본 메서드를 없앤다**

`src/ReSet.Core/Services/SnapshotManager.cs:77-89`의 호출부를 바꾼다.

```csharp
                        var dependencyType = SqlObjectTypeClassifier.ResolveCodeObjectType(dependency.Type);
                        if (dependencyType == CodeObjectType.Unresolved)
                        {
                            continue;
                        }

                        var dependencyKey = CodeObjectKey.Create(
                            dependency.Database ??
                                dependency.SourceObjectKey?.Database ??
                                snapshot.Database,
                            dependency.Schema,
                            dependency.Name,
                            dependencyType);
```

마지막 인자가 `dependencyType.Value`에서 `dependencyType`으로 바뀐 것에 유의한다. `ResolveCodeObjectType`은 `CodeObjectType?`이 아니라 `CodeObjectType`을 돌려주므로 `.Value`가 없다.

그리고 `:158-171`의 private 메서드를 통째로 삭제한다.

```csharp
        private static CodeObjectType? GetDependencyCodeObjectType(string type)
        {
            if (type.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase))
            {
                return CodeObjectType.Procedure;
            }

            if (type.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase))
            {
                return CodeObjectType.Function;
            }

            return null;
        }
```

얇은 어댑터로 남기지 않는다. 남기면 사본이 그대로 살아 있는 것이다. 판정 순서가 다르지만(PROCEDURE 먼저 vs FUNCTION 먼저) 두 리터럴을 동시에 포함하는 sys 카탈로그 타입 문자열이 없으므로 결과는 같다.

- [ ] **Step 9: MetadataExporter를 위임한다**

`src/ReSet.Core/Services/MetadataExporter.cs:340`

```csharp
                            var subFolderType =
                                SqlObjectTypeClassifier.ResolveCodeObjectType(dep.Type) == CodeObjectType.Procedure
                                    ? "procedures"
                                    : "functions";
```

`Unresolved`일 때 `functions`로 가며, 이는 현재의 거짓 분기와 같다. 동작이 바뀌지 않는다.

- [ ] **Step 10: 정책 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~TypeClassificationPolicyTests"`
Expected: PASS 8건 (Task 1의 7건 + 정책 테스트 1건)

- [ ] **Step 11: clean 빌드로 경고 수를 확인한다**

```bash
dotnet clean && dotnet build 2>&1 | tail -5
```

Expected: 오류 0건, 경고 8건. 증분 빌드는 경고를 다시 보고하지 않으므로 반드시 `clean`을 먼저 한다.

- [ ] **Step 12: 전체 테스트를 실행한다**

Run: `dotnet test`
Expected: 1,145건 통과 (1,135 + Task 1의 7 + 정책 1 + SettlementPolicy 2), 0건 실패

- [ ] **Step 13: 커밋**

```bash
git add src/ReSet.Core/Services/SettlementPolicyService.cs \
        src/ReSet.Core/Services/AiService.cs \
        src/ReSet.Core/Services/SnapshotManager.cs \
        src/ReSet.Core/Services/MetadataExporter.cs \
        tests/ReSet.Core.Tests/SettlementPolicyServiceTests.cs \
        tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs
git commit -m "fix: route the last five SQL type checks through the classifier

정산 정책 두 곳이 유일한 실질 변화다. TVF가 프로파일링 대상과
테이블 경고 귀속에서 빠진다. 인자가 필요한 함수를 인자 없이
SELECT ... FROM 으로 읽으려다 실패하던 경로다.

SnapshotManager의 GetDependencyCodeObjectType은 얇은 어댑터로
남기지 않고 삭제했다. 남기면 사본이 그대로 살아 있는 것이다.

정책 테스트가 이제 src/ 전체에서 0건을 단언한다."
```

---

## Task 3: SplitIdentifier의 `]` 손상 수정

**Files:**
- Modify: `src/ReSet.Core/Services/StaticAnalysisNormalizer.cs:182-203`
- Test: `tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs`

**Interfaces:**
- Consumes: `StaticAnalysisNormalizer.Canonicalize(string? writtenName, string? database, string? defaultSchema)` → `string`. 이미 있는 공개 메서드이며 시그니처를 바꾸지 않는다.
- Produces: 없음 (private 헬퍼의 동작 수정)

**문제:** `SplitIdentifier`가 `]`를 무조건 버려서 `my]table`이 `mytable`이 된다. 미지원이 아니라 손상이다. 호출부를 전부 추적한 결과 대괄호 이름은 `Canonicalize`에 도달하지 않으므로 오늘 눈에 보이는 결함은 없다. 방어 코드가 손상 경로를 만든 셈이다.

**고치는 방식:** 분리할 때는 대괄호로 구분자 판단만 하고 문자는 보존한 뒤, 조각 단위로 `[`로 시작하고 `]`로 끝날 때만 양 끝을 벗긴다.

---

- [ ] **Step 1: 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs`의 클래스 마지막 `}` 앞에 붙인다.

```csharp
        [Theory]
        // 감싼 대괄호는 여전히 벗겨져야 한다 - 이것이 원래 기능이다.
        [InlineData("[PaymentDB].[dbo].[TTxMst]", "PaymentDB.dbo.TTxMst")]
        [InlineData("[dbo].[TTxMst]", "SETTLE_POQ_DB.dbo.TTxMst")]
        // 대괄호 안의 점은 구분자가 아니다.
        [InlineData("[my.table]", "SETTLE_POQ_DB.dbo.my.table")]
        // 이름의 일부인 ']'는 보존되어야 한다. 예전 구현은 이것을 버려
        // my]table을 mytable로 손상시켰다.
        [InlineData("my]table", "SETTLE_POQ_DB.dbo.my]table")]
        [InlineData("dbo.my]table", "SETTLE_POQ_DB.dbo.my]table")]
        public void Canonicalize_PreservesBracketCharactersThatAreNotWrappers(
            string writtenName,
            string expected)
        {
            Assert.Equal(expected, StaticAnalysisNormalizer.Canonicalize(writtenName, "SETTLE_POQ_DB", "dbo"));
        }
```

- [ ] **Step 2: 손상 케이스가 실패하는 것을 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~Canonicalize_PreservesBracketCharactersThatAreNotWrappers"`
Expected: 5건 중 2건 FAIL

- `my]table` → 실제 `SETTLE_POQ_DB.dbo.mytable`
- `dbo.my]table` → 실제 `SETTLE_POQ_DB.dbo.mytable`

나머지 3건은 기존 동작이므로 통과한다. 통과하는 3건이 회귀 방지선이다.

- [ ] **Step 3: SplitIdentifier를 고친다**

`src/ReSet.Core/Services/StaticAnalysisNormalizer.cs:179-203`을 통째로 바꾼다.

```csharp
        /// <summary>
        /// 대괄호 안의 점은 구분자가 아니다. [my.table] 같은 이름을 쪼개지 않는다.
        ///
        /// 대괄호는 구분자 판단에만 쓰고 문자는 보존한다. 예전 구현은 ']'를
        /// 무조건 버려서 my]table을 mytable로 손상시켰다 - 미지원이 아니라 손상이다.
        /// ScriptDom이 이미 ']]' 이스케이프를 해제하므로 입력에 ']]'는 오지 않고,
        /// 따라서 T-SQL 이스케이프 파싱은 구현하지 않는다.
        /// </summary>
        private static List<string> SplitIdentifier(string name)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            var inBracket = false;

            foreach (var ch in name)
            {
                if (ch == '[') inBracket = true;
                else if (ch == ']') inBracket = false;
                else if (ch == '.' && !inBracket)
                {
                    parts.Add(UnwrapBrackets(current.ToString()));
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            parts.Add(UnwrapBrackets(current.ToString()));
            return parts;
        }

        /// <summary>조각 전체를 감싼 대괄호만 벗긴다. 이름 속의 대괄호는 남긴다.</summary>
        private static string UnwrapBrackets(string part)
        {
            var trimmed = part.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
                ? trimmed[1..^1].Trim()
                : trimmed;
        }
```

- [ ] **Step 4: 5건이 모두 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~StaticAnalysisNormalizerTests"`
Expected: PASS 전부

- [ ] **Step 5: 전체 테스트를 실행한다**

Run: `dotnet test`
Expected: **1,150건 통과**, 0건 실패 (Task 2 이후 1,145건 + 이번 `[Theory]`의 `InlineData` 5건. xUnit은 `InlineData` 하나를 테스트 한 건으로 센다.)

`SplitIdentifier`는 `Canonicalize` · `CanonicalizeParts` · `NormalizeList` · `MergeColumnsByTable`이 모두 쓰므로 정규화 관련 기존 테스트가 회귀 감지선이 된다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/StaticAnalysisNormalizer.cs \
        tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs
git commit -m "fix: stop dropping ']' from identifiers that are not bracket-wrapped

방어 코드가 손상 경로를 만들었다. 대괄호 이름은 Canonicalize에
도달하지 않지만, 도달했다면 my]table이 mytable이 됐다.

방어를 없애지 않고 손상만 멈춘다 - 구분자 판단에만 대괄호를 쓰고
문자는 보존한 뒤, 조각을 통째로 감쌌을 때만 양 끝을 벗긴다."
```

---

## Task 4: 대체된 테스트 3건 삭제와 문서 동기화

**Files:**
- Modify: `tests/ReSet.Core.Tests/CacheManagerTests.cs:606-616`
- Modify: `tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs:179-230`, `:252-279`
- Modify: `docs/architecture.md:380`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: `TypeClassificationPolicyScanner` / `NoRawSqlTypeClassificationRemainsInSource` (Task 1·2) — 삭제하는 가드를 대체하는 것이 이것이다.
- Produces: 없음

**왜 삭제 근거가 코드보다 중요한가:** 테스트를 지우는 커밋은 나중에 "왜 커버리지가 사라졌나"를 묻게 만든다. 세 건 모두 무엇이 그 자리를 대신하는지 커밋 메시지에 남긴다.

---

- [ ] **Step 1: CacheManagerTests의 중복 1건을 삭제한다**

`tests/ReSet.Core.Tests/CacheManagerTests.cs:606-616`의 다음 테스트를 통째로 지운다.

```csharp
        [Fact]
        public void CacheFormatVersion_ShouldBeTwoSoPreNormalizationArtifactsAreRebuilt()
        {
            // 복합 해시는 DDL만 본다. 원본 SP가 안 바뀌었으므로 버전을 올리지 않으면
            // 정규화 이전에 만들어진 잘못된 Spec.md가 그대로 복원된다.
            var source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/CacheManager.cs"));

            Assert.Contains("CurrentCacheFormatVersion = 2", source);
        }
```

소스 문자열만 단언한다. 같은 파일의 `IsCacheValid_ReturnsFalse_ForEntriesFromFormatVersionOne`이 실제 캐시 항목을 찍고 JSON 인덱스의 `FormatVersion`을 1로 되돌린 뒤 `IsCacheValid`가 false를 반환하는지 확인한다. 후자가 더 강하고 전자를 완전히 포함한다.

- [ ] **Step 2: DbMetadataServiceDetailsTests의 가드 1건과 헬퍼를 삭제한다**

`tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs:179-230`의 `DbMetadataService_DelegatesClassificationToSqlObjectTypeClassifier` 메서드 전체(그 위의 주석 블록 포함)와, 바로 뒤의 헬퍼를 지운다.

```csharp
        private static int CountOccurrences(string source, string literal) =>
            (source.Length - source.Replace(literal, string.Empty).Length) / literal.Length;
```

헬퍼는 이 가드에서만 쓰인다(파일 내 사용처는 199·202·226행뿐). 남기면 죽은 코드다.

이 가드는 파일 하나에서 리터럴 하나만 봤다. `NoRawSqlTypeClassificationRemainsInSource`가 `src/` 전체에서 네 리터럴을 보므로 좁은 쪽이 넓은 쪽의 부분집합이 된다.

`>= 2` 횟수 단언도 함께 사라진다. 그 단언은 파일 단위 검사가 단일 호출부 되돌리기를 못 잡는 약점을 메우려던 우회였고, 스캐너가 원시 판정 자체를 금지하면 필요가 없다. 다만 "위임이 통째로 사라지는" 경우는 스캐너가 잡지 못하므로(없앨 원시 판정도 함께 사라지므로) 각 지점의 동작 테스트가 그 몫을 맡는다.

- [ ] **Step 3: 남은 테스트의 주석에서 삭제된 가드 참조를 고친다**

`tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs`의 `SqlObjectTypeClassifier_TreatsTableValuedFunctionsAsCodeObjects` 주석이 삭제된 가드 이름을 가리킨다. 마지막 두 줄을 바꾼다.

바꾸기 전:

```csharp
            // 분류 판정은 SqlObjectTypeClassifier로 이전되었다(두 private 메서드는
            // 삭제됨). 여기서는 DbMetadataService가 아니라 그 분류기 자체를
            // 공개 API로 확인한다. DbMetadataService가 실제로 이 분류기에
            // 위임하는지는 별도 가드 테스트(DbMetadataService_DelegatesClassificationToSqlObjectTypeClassifier)가 확인한다.
```

바꾼 뒤:

```csharp
            // 분류 판정은 SqlObjectTypeClassifier로 이전되었다(두 private 메서드는
            // 삭제됨). 여기서는 DbMetadataService가 아니라 그 분류기 자체를
            // 공개 API로 확인한다. 호출부가 실제로 이 분류기에 위임하는지는
            // TypeClassificationPolicyTests가 src/ 전체를 구문 트리로 훑어 확인한다.
```

- [ ] **Step 4: DbMetadataServiceDetailsTests의 중복 1건을 삭제한다**

`tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs:252-279`의 `NormalizeStaticAnalysisForDefinition_ShouldCanonicaliseAgainstTheObjectKey` 메서드 전체를 지운다.

본문이 `StaticAnalysisNormalizer.Normalize`를 직접 호출하고 `DbMetadataService`를 전혀 건드리지 않는다. `StaticAnalysisNormalizerTests`의 복제이며 이름이 서비스 커버리지를 암시한다. 실제 배선은 바로 다음 테스트 `DbMetadataService_ShouldNormaliseStaticAnalysisBeforeReturning`이 덮는다 — 그 테스트는 지우지 않는다.

- [ ] **Step 5: 전체 테스트를 실행한다**

Run: `dotnet test`
Expected: **1,147건 통과**, 0건 실패

산식은 `1,135 − 3(삭제) + 15(신규)`다. 신규 15건의 내역은 Task 1의 7건, Task 2의 3건(정책 1 + SettlementPolicy 2), Task 3의 5건(`[Theory]`의 `InlineData` 5개)이다.

**계획 순서와 다르게 실행했다면 이 숫자가 달라진다.** Task 3을 아직 하지 않았다면 1,142건이다. 실측치가 어느 산식과도 맞지 않으면 의도치 않은 테스트 증감이 있었다는 뜻이므로 멈추고 확인한다.

- [ ] **Step 6: 사용하지 않는 using이 생기지 않았는지 clean 빌드로 확인한다**

```bash
dotnet clean && dotnet build 2>&1 | tail -5
```

Expected: 오류 0건, 경고 8건

- [ ] **Step 7: architecture.md를 갱신한다**

`docs/architecture.md:380`의 문단 맨 끝(마지막 문장 `...두 경로가 다시 갈라지지 못하게 합니다.` 뒤)에 이어 붙인다.

```markdown
 이 판정이 호출부로 다시 새어 나가지 않도록 `TypeClassificationPolicyTests`가 Roslyn 구문 트리로 `src/` 전체를 훑어, SQL 타입 문자열에 대한 원시 `Contains("TABLE"/"VIEW"/"FUNCTION"/"PROCEDURE")` 판정이 한 곳도 남아 있지 않은지 검사합니다. 같은 결함이 네 번에 걸쳐 발견되는 동안 변수명이 매번 달라(`rawDep.Type` → `dep.Type` → `objectType` → `d.Type` → `type`) 사람이 만든 grep 패턴이 매번 그때 눈에 띈 표기만 잡았기 때문입니다.
```

- [ ] **Step 8: AGENTS.md의 파일 바로가기를 갱신한다**

`AGENTS.md:37`의 `SqlObjectTypeClassifier.cs` 항목 문장 끝에 이어 붙인다.

```markdown
 이 규칙은 `TypeClassificationPolicyTests`가 자동 검사합니다.
```

그리고 `AGENTS.md:138`의 `CancellationPolicyTests.cs` 항목 바로 아래에 같은 형식으로 한 줄 추가한다.

```markdown
    *   [TypeClassificationPolicyTests.cs](./tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs): Roslyn 구문 트리로 `src/` 전체를 훑어 SQL 객체 타입 문자열에 대한 원시 부분 문자열 판정을 찾아내는 아키텍처 게이트. `SqlObjectTypeClassifier.cs` 자신만 예외이며, 그 밖에서는 위반 0건이어야 합니다. 로그 텍스트 매칭(`logUpper.Contains("TABLE")`)은 수신자 이름으로 구분해 오탐하지 않습니다.
```

- [ ] **Step 9: AGENTS.md의 준수 규칙을 갱신한다**

`AGENTS.md:159`의 "취소는 소프트 페일 대상이 아님" 항목 바로 아래에 같은 형식으로 추가한다.

```markdown
    *   **SQL 객체 타입 판정은 반드시 분류기를 거칠 것**: `sys` 카탈로그의 타입 문자열을 `Contains("TABLE")` 같은 부분 문자열로 직접 판정하지 마십시오. `SQL_TABLE_VALUED_FUNCTION`이 `TABLE`을 포함하므로 TVF가 테이블로 오분류되고, 그 함수의 DDL이 수집되지 않은 채 이를 호출하는 SP의 명세서가 로직을 블랙박스로 남긴 채 작성됩니다. 실제로 정산일을 계산하는 `UIF_SettleYMD`가 그렇게 누락됐습니다. [SqlObjectTypeClassifier](./src/ReSet.Core/Services/SqlObjectTypeClassifier.cs)의 `IsTableOrView` / `IsCodeObject` / `ResolveCodeObjectType`을 쓰고, 사본을 만들지 마십시오. 이 규칙은 `TypeClassificationPolicyTests`가 Roslyn 구문 트리로 자동 검사합니다.
```

- [ ] **Step 10: AGENTS.md의 완료 체크리스트를 갱신한다**

`AGENTS.md:296`의 취소 필터 체크 항목 바로 아래에 추가한다.

```markdown
- [ ] SQL 객체 타입을 `Contains("TABLE"/"VIEW"/"FUNCTION"/"PROCEDURE")`로 직접 판정한 곳이 없는가? (`SqlObjectTypeClassifier`에 위임해야 하며 `TypeClassificationPolicyTests`가 자동 검사한다)
```

- [ ] **Step 11: 문서 링크가 실제 파일을 가리키는지 확인한다**

```bash
ls tests/ReSet.Core.Tests/TypeClassificationPolicyTests.cs \
   tests/ReSet.Core.Tests/TypeClassificationPolicyScanner.cs \
   src/ReSet.Core/Services/SqlObjectTypeClassifier.cs
```

Expected: 세 경로 모두 존재

- [ ] **Step 12: 커밋**

```bash
git add tests/ReSet.Core.Tests/CacheManagerTests.cs \
        tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs \
        docs/architecture.md AGENTS.md
git commit -m "test: drop three checks the policy scanner and siblings now cover

CacheFormatVersion_ShouldBeTwoSo... 는 소스 문자열만 단언했다.
IsCacheValid_ReturnsFalse_ForEntriesFromFormatVersionOne이 실제
캐시 항목으로 같은 것을 더 강하게 확인한다.

NormalizeStaticAnalysisForDefinition_... 은 본문이 정규화기를 직접
호출하고 DbMetadataService를 건드리지 않았다. 배선은
DbMetadataService_ShouldNormaliseStaticAnalysisBeforeReturning이 덮는다.

DbMetadataService_DelegatesClassificationToSqlObjectTypeClassifier는
파일 하나에서 리터럴 하나만 봤다. TypeClassificationPolicyTests가
src/ 전체에서 네 리터럴을 보므로 부분집합이 됐다. 전용 헬퍼
CountOccurrences도 함께 삭제했다."
```

---

## 완료 기준

전체 작업이 끝난 뒤 다음을 모두 확인한다.

- [ ] `dotnet clean && dotnet build` — 오류 0건, 경고 **정확히 8건**
- [ ] `dotnet test` — 0건 실패. 총수가 `1,135 − 3 + 신규분` 산식과 어긋나면 의도치 않은 테스트 증감이 있었다는 뜻이다
- [ ] `TypeClassificationPolicyScanner`가 `src/` 전체에서 위반 0건 보고 (`NoRawSqlTypeClassificationRemainsInSource` 통과가 곧 이 확인이다)
- [ ] `git status --short`가 비어 있음
- [ ] `docs/architecture.md`와 `AGENTS.md`가 새 규칙과 그 근거를 담고 있음

## 이번 범위 밖 (설계 문서 §후속)

1. `DependencyInfo.Type`의 타입화 — 문자열 가드가 아니라 타입 시스템으로 원시 판정을 차단한다. 근본적이지만 직렬화·스냅샷 호환성까지 번진다.
2. 프롬프트 계약 강화 3건 — UPDATE 컬럼 매핑표, `UPDATE ... FROM` 자기참조 의미, `SET` 절 동시평가.
3. 명세서 재발 방지 검증 게이트 — 남은 항목 중 가장 시급하다는 것이 선행 브랜치 최종 리뷰어의 평가다.
