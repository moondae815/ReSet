# 취소 처리 정책과 재발 방지 장치 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `OperationCanceledException`을 삼키는 `catch`를 구문 규칙으로 잡아내는 아키텍처 테스트를 만들고, 해악이 실증된 네 파일을 비운다.

**Architecture:** 테스트 프로젝트에 Roslyn 구문 파서를 더해 `src/` 아래 모든 `.cs`를 스캔한다. 규칙은 "취소 가능한 `await`를 감싸면서 OCE를 거르지도 다시 던지지도 않는 넓은 catch"이며, 이 하나가 지금까지 발견된 네 모양을 모두 잡는다. 기준선은 파일별 개수 래칫으로, 새 위반이 생기거나 고치고도 기준선을 안 내리면 양쪽 모두 실패한다.

**Tech Stack:** .NET 10 / C#, xUnit, NSubstitute, **Microsoft.CodeAnalysis.CSharp 5.6.0** (테스트 프로젝트에만)

## Global Constraints

- 대상 저장소: `/Users/payletter/git-root/ReSet`, 기준 브랜치 `main` (설계 커밋 `69488d0`)
- 설계 문서: `docs/superpowers/specs/2026-08-03-cancellation-policy-design.md`
- API 키를 소스나 `appsettings.json`에 하드코딩하지 않는다
- Spectre.Console 출력에 들어가는 런타임 값은 `Markup.Escape()`로 감싼다
- `OperationCanceledException`은 절대 삼키지 않는다. IO/DB/AI 오류는 soft-fail한다
- 모든 신규 주석과 사용자 노출 문자열은 한국어로 작성한다
- 작업 시작 시점의 테스트는 **396개 전부 통과**. 클린 빌드 경고는 **고유 8건**(전부 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 CS8600/CS8602). 이 수를 늘리지 않는다
- **경고 개수를 셀 때 중복을 제거한다.** `dotnet build`는 각 경고를 두 번 출력하므로 `grep -c`는 16을 반환한다. `grep -E "warning CS" | sort -u | wc -l`로 고유 개수를 확인한다
- 경고 확인은 반드시 클린 빌드(`dotnet clean && dotnet build`)로 한다
- 솔루션 파일은 `ReSet.slnx`다 (`.sln`은 없다)

## 스펙 교정 두 건

계획 수립 중 실제 코드를 읽어 스펙의 규칙에 **거짓 양성 두 종류**를 발견했다. 스펙 본문은 "거짓 양성은 개발을 막으므로 거짓 음성보다 비싸다"고 적어 두었으므로 규칙을 교정한다.

**교정 1 — 명시적 OCE 절은 대상이 아니다.** 스펙의 조건 1은 대상 타입에 `OperationCanceledException`과 `TaskCanceledException`을 포함했다. 그러나 명시적으로 그 타입을 잡는 절은 사고가 아니라 의도다. 실례가 `src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs:86`에 있다.

```csharp
catch (OperationCanceledException)
{
    // 취소를 예외로 흘려보내면 "완료분은 저장됐다"는 사실이 호출부에
    // 도달하지 못한다. 결과 레코드가 계약이므로 상태로 바꾼다.
    completion = GraphCompletion.PartialCancelled;
```

**대상은 넓은 catch뿐이다** — 타입이 `Exception`/`SystemException`이거나 타입이 생략된 `catch`.

**교정 2 — 앞선 형제 절이 OCE를 잡으면 뒤의 넓은 catch는 안전하다.** 실례가 `src/ReSet.Core/Services/MetadataExporter.cs:92`에 있다.

```csharp
catch (OperationCanceledException)
{
    throw;
}
catch (Exception ex)
{
    Log.Error(ex, "코드 객체 아티팩트 저장 중 오류가 발생했습니다 (격리됨): {ObjectKey}", …);
}
```

C#은 `catch` 절을 위에서부터 매칭하므로 뒤의 넓은 catch는 OCE를 볼 수 없다. 이를 위반으로 찍으면 올바른 코드를 고치라고 요구하게 된다.

## 파일 구조

| 파일 | 책임 | 작업 |
|---|---|---|
| `tests/ReSet.Core.Tests/CancellationPolicyScanner.cs` | 구문 스캔 규칙 (순수 함수) | 신규 (Task 1) |
| `tests/ReSet.Core.Tests/CancellationPolicyTests.cs` | 규칙 자체 검증 + 기준선 게이트 | 신규 (Task 1) |
| `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt` | 파일별 허용 개수 | 신규 (Task 1) |
| `tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` | Roslyn 패키지 참조 | 수정 (Task 1) |
| `src/ReSet.Cli/Program.cs` | 코드젠 호출부 | 가리는 catch 수정 (Task 2) |
| `src/ReSet.Core/Services/ExternalCliCodingEngine.cs` | 외부 에이전트 기동 | 타입 세탁 수정 (Task 2) |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 검증 파이프라인 | 캐시 경로 수정 (Task 3) |
| `src/ReSet.Core/Services/DbMetadataService.cs` | 메타데이터 수집 | DFS 루프 수정 (Task 4) |
| `AGENTS.md` | 문서 | 테스트 수·정책 (Task 5) |

스캐너를 테스트 파일과 분리하는 이유: 규칙은 순수 함수이고 인라인 소스 조각으로 단독 검증할 수 있어야 한다. 저장소를 변조해 규칙을 시험하면 느리고 실패 시 트리가 더러워진다.

---

### Task 1: 스캐너와 기준선 게이트

**Files:**
- Create: `tests/ReSet.Core.Tests/CancellationPolicyScanner.cs`
- Create: `tests/ReSet.Core.Tests/CancellationPolicyTests.cs`
- Create: `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`
- Modify: `tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `ReSet.Core.Tests.CancellationOffender(string RelativePath, int Line, string Member)` — record
  - `ReSet.Core.Tests.CancellationPolicyScanner.ScanSource(string sourceText, string relativePath) → IReadOnlyList<CancellationOffender>`
  - `ReSet.Core.Tests.CancellationPolicyScanner.ScanDirectory(string srcRoot) → IReadOnlyList<CancellationOffender>`
  - `ReSet.Core.Tests.RepoPaths.FindRepoRoot() → string`

- [ ] **Step 1: Roslyn 패키지를 추가한다**

`tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`의 첫 `<ItemGroup>`(PackageReference들)에 추가한다.

```xml
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.6.0" />
```

Run: `dotnet restore`
Expected: 성공

- [ ] **Step 2: 규칙의 실패 테스트를 작성한다**

`tests/ReSet.Core.Tests/CancellationPolicyTests.cs`를 만든다. 이 다섯 개가 규칙 자체를 검증한다 — 저장소를 건드리지 않고 인라인 소스로만 판정한다.

```csharp
using System.Linq;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class CancellationPolicyTests
{
    // 규칙: 취소 가능한 await를 감싸면서 OperationCanceledException을 거르지도
    // 다시 던지지도 않는 넓은 catch는 위반이다. 지금까지 발견된 네 모양
    // (빈 catch, 알림 후 계속, 바깥 핸들러 가리기, 타입 세탁)이 모두 이 서명을 갖는다.

    [Fact]
    public void Scanner_FlagsABroadCatchAroundACancellableAwait()
    {
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(System.Threading.CancellationToken cancellationToken)
    {
        try { await Work(cancellationToken); }
        catch (System.Exception ex) { System.Console.WriteLine(ex.Message); }
    }
    async System.Threading.Tasks.Task Work(System.Threading.CancellationToken ct) { }
}";

        var offenders = CancellationPolicyScanner.ScanSource(source, "Fake.cs");

        var offender = Assert.Single(offenders);
        Assert.Equal("Fake.cs", offender.RelativePath);
        Assert.Equal("M", offender.Member);
    }

    [Fact]
    public void Scanner_DoesNotFlagACatchWithNoCancellableAwait()
    {
        // 동기 IO의 soft-fail은 취소와 무관하다. 이 코드베이스에 넓은 catch가
        // 100곳 넘게 있는 정당한 이유이며, 여기서 거짓 양성을 내면 규칙이 버려진다.
        var source = @"
class C
{
    void M()
    {
        try { System.IO.File.Delete(""x""); }
        catch (System.Exception ex) { System.Console.WriteLine(ex.Message); }
    }
}";

        Assert.Empty(CancellationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagACatchThatFiltersCancellation()
    {
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(System.Threading.CancellationToken cancellationToken)
    {
        try { await Work(cancellationToken); }
        catch (System.Exception ex) when (ex is not System.OperationCanceledException) { }
    }
    async System.Threading.Tasks.Task Work(System.Threading.CancellationToken ct) { }
}";

        Assert.Empty(CancellationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagABroadCatchPrecededByAnOperationCanceledClause()
    {
        // C#은 catch 절을 위에서부터 매칭하므로 뒤의 넓은 catch는 OCE를 볼 수 없다.
        // 실례: src/ReSet.Core/Services/MetadataExporter.cs
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(System.Threading.CancellationToken cancellationToken)
    {
        try { await Work(cancellationToken); }
        catch (System.OperationCanceledException) { throw; }
        catch (System.Exception ex) { System.Console.WriteLine(ex.Message); }
    }
    async System.Threading.Tasks.Task Work(System.Threading.CancellationToken ct) { }
}";

        Assert.Empty(CancellationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagAnExplicitOperationCanceledClause()
    {
        // 명시적으로 OCE를 잡는 것은 사고가 아니라 의도다.
        // 실례: src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs — 취소를
        // 예외가 아니라 결과 상태로 바꾸는 것이 그 메서드의 계약이다.
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(System.Threading.CancellationToken cancellationToken)
    {
        try { await Work(cancellationToken); }
        catch (System.OperationCanceledException) { System.Console.WriteLine(""부분 취소""); }
    }
    async System.Threading.Tasks.Task Work(System.Threading.CancellationToken ct) { }
}";

        Assert.Empty(CancellationPolicyScanner.ScanSource(source, "Fake.cs"));
    }
}
```

- [ ] **Step 3: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CancellationPolicyTests"`
Expected: 컴파일 실패 — `CancellationPolicyScanner` 형식을 찾을 수 없음 (CS0103)

- [ ] **Step 4: 스캐너를 작성한다**

`tests/ReSet.Core.Tests/CancellationPolicyScanner.cs`를 만든다.

```csharp
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

    private static bool FiltersCancellation(CatchClauseSyntax clause) =>
        clause.Filter is not null &&
        CancellationTypes.Any(type => clause.Filter.FilterExpression.ToString().Contains(type, StringComparison.Ordinal));

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
```

- [ ] **Step 5: 규칙 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CancellationPolicyTests"`
Expected: 5개 통과

**하나라도 실패하면 규칙이 틀린 것이다.** 기대값을 실제 출력에 맞춰 고치지 말고, 스캐너를 고쳐라. 특히 마지막 두 개(형제 절, 명시적 OCE 절)는 거짓 양성을 막는 방어선이며, 이것이 무너지면 규칙이 올바른 코드를 고치라고 요구하게 된다.

- [ ] **Step 6: 저장소 루트를 찾는 헬퍼를 추가한다**

`tests/ReSet.Core.Tests/CancellationPolicyScanner.cs` 파일 끝(같은 네임스페이스 안)에 추가한다.

```csharp
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
```

- [ ] **Step 7: 기준선 초기값을 생성한다**

기준선 파일을 **손으로 쓰지 않는다.** 스캐너를 돌려 나온 실제 수치를 쓴다.

임시 테스트를 하나 추가해 현재 위반 분포를 출력한다.

```csharp
    [Fact]
    public void TEMP_PrintCurrentOffenders()
    {
        // xUnit은 Console 출력을 삼킬 수 있으므로 단언 메시지에 실어 확실히 드러낸다.
        var srcRoot = System.IO.Path.Combine(RepoPaths.FindRepoRoot(), "src");
        var lines = CancellationPolicyScanner.ScanDirectory(srcRoot)
            .GroupBy(offender => offender.RelativePath)
            .OrderBy(group => group.Key, System.StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}");

        Assert.True(false, "기준선 초기값:\n" + string.Join("\n", lines));
    }
```

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~TEMP_PrintCurrentOffenders"`

실패 메시지의 `경로=개수` 줄들을 그대로 `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`에 옮기고, 파일 앞에 다음 머리말을 붙인다.

```
# 취소를 삼킬 수 있는 catch의 파일별 허용 개수.
# 목록에 없는 파일은 0건을 뜻한다.
#
# 이 숫자는 부채다. 고칠 때마다 함께 내려야 하며, 내리지 않으면 테스트가 실패한다.
# 새 위반이 생겨도 실패한다. 양방향으로 잠겨 있어야 목록이 썩지 않는다.
```

**그런 다음 임시 테스트를 삭제한다.** 보고서에 출력된 분포를 그대로 기록한다.

- [ ] **Step 8: 기준선 게이트 테스트를 작성한다**

`CancellationPolicyTests.cs`에 추가한다.

```csharp
    [Fact]
    public void NoFileExceedsItsCancellationBaseline()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var actual = CancellationPolicyScanner
            .ScanDirectory(System.IO.Path.Combine(repoRoot, "src"))
            .GroupBy(offender => offender.RelativePath)
            .ToDictionary(group => group.Key, group => group.ToList(), System.StringComparer.Ordinal);

        var baselinePath = System.IO.Path.Combine(
            repoRoot, "tests", "ReSet.Core.Tests", "cancellation-policy-baseline.txt");
        var allowed = ReadBaseline(baselinePath);

        var failures = new System.Text.StringBuilder();

        foreach (var path in actual.Keys.Union(allowed.Keys).OrderBy(key => key, System.StringComparer.Ordinal))
        {
            var actualOffenders = actual.TryGetValue(path, out var list) ? list : new System.Collections.Generic.List<CancellationOffender>();
            var allowedCount = allowed.TryGetValue(path, out var count) ? count : 0;

            if (actualOffenders.Count == allowedCount) continue;

            failures.AppendLine($"{path}: 허용 {allowedCount}건, 실제 {actualOffenders.Count}건");
            foreach (var offender in actualOffenders.OrderBy(item => item.Line))
            {
                failures.AppendLine($"  {offender.RelativePath}:{offender.Line} ({offender.Member})");
            }

            failures.AppendLine(actualOffenders.Count > allowedCount
                ? "  → 새 위반입니다. 위 목록에서 방금 편집한 줄을 찾으십시오."
                : $"  → 고쳤다면 기준선을 {actualOffenders.Count}로 내리십시오.");
            failures.AppendLine();
        }

        Assert.True(
            failures.Length == 0,
            "취소를 삼킬 수 있는 catch의 개수가 기준선과 다릅니다.\n\n" + failures);
    }

    private static System.Collections.Generic.Dictionary<string, int> ReadBaseline(string path)
    {
        var result = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var raw in System.IO.File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.LastIndexOf('=');
            Assert.True(separator > 0, $"기준선 파일의 형식이 잘못되었습니다: {raw}");
            result[line[..separator].Trim()] = int.Parse(line[(separator + 1)..].Trim());
        }

        return result;
    }
```

- [ ] **Step 9: 게이트가 양방향으로 잠기는지 확인한다**

먼저 통과를 확인한다.

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~NoFileExceedsItsCancellationBaseline"`
Expected: PASS

**그다음 양방향을 각각 실증한다.**

방향 1 — 새 위반. `src/ReSet.Core/Services/SnapshotManager.cs`의 아무 `async` 메서드 안에 임시로 다음을 넣는다.

```csharp
        try { await System.Threading.Tasks.Task.Delay(1, cancellationToken); }
        catch (System.Exception) { }
```

Run: 위와 같은 명령
Expected: FAIL. 메시지에 `SnapshotManager.cs: 허용 N건, 실제 N+1건`과 해당 줄 목록이 나온다. 실패 메시지를 보고서에 기록하고 임시 코드를 되돌린다.

방향 2 — 고치고 기준선을 안 내림. 기준선 파일에서 아무 항목의 숫자를 1 올린다.

Run: 위와 같은 명령
Expected: FAIL. 메시지에 `→ 고쳤다면 기준선을 N으로 내리십시오.`가 나온다. 실패 메시지를 기록하고 되돌린다.

두 방향이 모두 실패하지 않으면 래칫이 작동하지 않는 것이다. 그 경우 멈추고 보고하라.

- [ ] **Step 10: 클린 빌드와 전체 테스트**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```
Expected: 고유 경고 8건, 전체 통과 (396 + 6 = 402)

Roslyn 패키지가 새 경고를 유발하면 그 사실을 보고하라. 경고를 억제하지 말고 먼저 보고할 것.

- [ ] **Step 11: 커밋**

```bash
git add -A
git commit -m "test: gate catches that can swallow cancellation

Three cycles running, the same defect surfaced in a new shape and a human
found it with a fresh grep pattern each time. The last two shapes - an inner
catch shadowing the correct handler five lines below, and cancellation
laundered into InvalidOperationException - are invisible to any catch-pattern
search, because grep cannot read C# structure.

All four shapes share one signature: a broad catch around a cancellable await
that neither filters nor rethrows OperationCanceledException. One rule covers
them, and it needs a parser.

The baseline is a per-file count that fails in both directions, so a fixed
site must lower the number rather than leaving a list that quietly rots.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: 코드젠 경로의 가리는 catch와 타입 세탁

**Files:**
- Modify: `src/ReSet.Cli/Program.cs` (코드젠 호출을 감싼 catch들)
- Modify: `src/ReSet.Core/Services/ExternalCliCodingEngine.cs:106`
- Modify: `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`

**Interfaces:**
- Consumes: Task 1의 `NoFileExceedsItsCancellationBaseline` 게이트
- Produces: 없음

**이 둘은 반드시 함께 착지해야 한다.** `ExternalCliCodingEngine`만 고치면 OCE가 날것으로 올라오지만 `Program.cs`의 가리는 catch가 여전히 삼킨다. 반대로 `Program.cs`만 고치면 `InvalidOperationException`이 올라와 OCE 핸들러에 걸리지 않는다. 어느 한쪽만으로는 사용자가 보는 증상이 그대로다.

**테스트에 관한 정직한 한계:** 이 태스크에는 행동 테스트가 없다. `Program.cs`의 해당 구간은 최상위 문 안의 지역 흐름이고 `ExternalCliCodingEngine`은 외부 프로세스를 기동한다. 둘 다 단위 테스트로 격리할 수 없다. 검증은 (a) Task 1의 구문 게이트, (b) 클린 빌드, (c) 아래 Step 1의 사전 확인 세 가지다. 새 행동 테스트를 만들지 않으며, 만든 척하지 않는다.

- [ ] **Step 1: 현재 위반 목록을 확인한다**

기준선 파일에서 두 파일의 현재 허용 개수를 확인하고, 게이트를 임시로 깨뜨려 위반 위치를 출력시킨다. 기준선 파일에서 `ReSet.Cli/Program.cs`의 숫자를 0으로 바꾼 뒤:

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~NoFileExceedsItsCancellationBaseline"`

실패 메시지에 나온 `ReSet.Cli/Program.cs:<줄> (<멤버>)` 목록을 전부 기록한다. `ExternalCliCodingEngine.cs`도 같은 방법으로 확인한다. 그런 다음 기준선을 원래대로 되돌린다.

이 목록이 이 태스크의 작업 대상이다. 계획이 숫자를 미리 지어내지 않는다.

- [ ] **Step 2: `ExternalCliCodingEngine`의 타입 세탁을 고친다**

`src/ReSet.Core/Services/ExternalCliCodingEngine.cs:106`의 catch에 필터를 단다. **본문은 그대로 둔다.**

```csharp
            // 취소를 InvalidOperationException으로 감싸면 하류의 올바른 핸들러
            // (Program.cs의 catch (OperationCanceledException))가 전부 매칭에 실패한다.
            // 사용자가 Ctrl-C를 눌러도 "엔진 기동 오류"로 보고되고 작업이 계속된다.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "외부 코딩 에이전트 기동 중 예외 발생 - Engine: {EngineName}, Command: {Command}", Name, _command);
                throw new InvalidOperationException($"외부 코딩 엔진({Name}) 기동 중 오류가 발생했습니다. 명령어가 설치되어 있는지 확인해 주십시오. (오류: {ex.Message})", ex);
            }
```

- [ ] **Step 3: `Program.cs`의 위반을 전부 고친다**

Step 1에서 얻은 목록의 각 지점에 필터를 단다. 형태는 파일 안에 이미 존재하는 것을 따른다.

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
```

**가리는 catch 두 곳에는 한국어 주석을 단다** — 코드젠 호출을 감싸는 것들이다. 왜 이 필터가 필요한지가 다섯 줄 아래를 봐야 알 수 있으므로, 그 사실을 적어 둔다.

```csharp
                            // 이 안쪽 catch가 취소를 먼저 소비하면, 바깥 try의
                            // catch (OperationCanceledException)가 영영 도달하지 못한다.
                            // 사용자의 Ctrl-C가 무시되고 흐름이 메인 메뉴로 그냥 떨어진다.
                            catch (Exception ex) when (ex is not OperationCanceledException)
```

목록에 있는 다른 지점들은 주석 없이 필터만 단다 — 이유가 자명하다.

**주의:** 이미 `catch (OperationCanceledException)`가 앞선 형제 절로 있는 곳은 스캐너가 애초에 지적하지 않는다. 목록에 없는 catch는 건드리지 마라.

- [ ] **Step 4: 기준선을 내린다**

`tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`에서 두 파일의 항목을 지운다(0이 되므로 목록에서 제거한다).

- [ ] **Step 5: 게이트와 전체 테스트를 확인한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```
Expected: 고유 경고 8건, 전체 통과

게이트가 실패하면 두 방향 중 하나다 — 남은 위반이 있거나(실제 > 허용), 기준선을 너무 많이 내렸다(실제 < 허용). 실패 메시지가 어느 쪽인지 알려준다.

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "fix(cli): stop the codegen paths from eating the user's cancellation

The inner catch around each RunCodegenEngineAsync call consumed
OperationCanceledException five lines before the outer try's correct handler
could see it, so Ctrl-C during codegen fell through to the main menu as if
nothing had happened.

ExternalCliCodingEngine made it worse from the other side: it wrapped every
exception, cancellation included, into InvalidOperationException, which no
downstream OperationCanceledException handler can match. Either fix alone
leaves the symptom intact, so both land together.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: 캐시 확인 경로

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (캐시 유효성 확인을 감싼 catch)
- Modify: `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 1의 게이트
- Produces: 없음

**배경:** 캐시 유효성 확인이 취소되면 "캐시 확인 중 오류"로 기록되고 파이프라인이 **전체 AI 분석으로 진행한다.** 창은 좁지만(로컬 파일 읽기) 결과는 가장 비싸다 — 사용자가 멈추라고 했는데 가장 긴 작업이 시작된다.

이 태스크는 다른 셋과 달리 **행동 테스트가 가능하다.** `ICacheManager`가 인터페이스라 NSubstitute로 대체된다.

- [ ] **Step 1: 실패 테스트를 작성한다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` 끝에 추가한다.

```csharp
        [Fact]
        public async Task RunCodeObjectPipelineAsync_CancelDuringCacheCheck_Propagates()
        {
            // 캐시 확인 중 취소가 삼켜지면 파이프라인이 전체 AI 분석으로 진행한다.
            // 사용자가 멈추라고 한 직후에 가장 긴 작업이 시작되는 셈이다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_CancelCache", CodeObjectType.Procedure);

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_CancelCache", DdlText = "SELECT 1;"
                }));

            cacheManager.ComputeCompositeHash(Arg.Any<SpDefinition>(), Arg.Any<int>()).Returns("hash");
            cacheManager
                .When(manager => manager.IsCacheValid(
                    Arg.Any<CodeObjectKey>(), Arg.Any<string>(), Arg.Any<OutputPathResolver>()))
                .Do(_ => throw new OperationCanceledException());

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", cacheManager);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB", key, 1, "OpenAI", "rules", true,
                    Path.Combine(Path.GetTempPath(), $"ReSet-CancelCache-{Guid.NewGuid():N}"), true,
                    cancellationToken: CancellationToken.None, directDependenciesOnly: true));
        }
```

**`enableCache`가 켜져 있어야 캐시 경로에 진입한다.** `RunCodeObjectPipelineAsync`의 인수 중 어느 것이 `enableCache`인지 시그니처를 읽어 확인하고, 위 호출의 위치 인수를 그에 맞춰라. 위 코드의 `true` 두 개 중 하나가 `isBatchMode`이고 다른 하나가 `enableCache`일 수 있으니 반드시 시그니처로 대조하라.

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CancelDuringCacheCheck"`
Expected: FAIL — `Assert.ThrowsAsync() Failure: No exception was thrown` (취소가 catch에 삼켜지므로)

**다른 이유로 실패하면** 캐시 경로에 도달하지 못한 것이다. `enableCache` 인수와 `cacheObjectKey`/`outputPaths`가 null이 아닌지 확인하라 — 셋 중 하나라도 어긋나면 캐시 블록 자체를 건너뛴다.

- [ ] **Step 3: 필터를 단다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs`의 캐시 확인을 감싼 catch(로그 메시지가 `"[파이프라인] 캐시 확인 중 예외 발생 (무시됨) - SP: {SpName}"`인 것)에 필터를 단다. **본문은 그대로 둔다.**

```csharp
                // 캐시 확인이 취소되었는데 삼키면 파이프라인이 전체 AI 분석으로 진행한다.
                // 사용자가 멈추라고 한 직후에 가장 비싼 작업이 시작되는 셈이다.
                catch (Exception ex) when (ex is not OperationCanceledException)
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"`
Expected: 전부 통과. 기존 테스트가 깨지면 취소 외 예외의 흐름을 바꾼 것이므로 되돌아가 확인하라

- [ ] **Step 5: 이 파일의 남은 위반을 정리하고 기준선을 내린다**

기준선에서 `ReSet.Core/Services/VerificationPipelineOrchestrator.cs`의 숫자를 0으로 바꿔 게이트를 깨뜨리고, 남은 위반 목록을 확인한다.

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~NoFileExceedsItsCancellationBaseline"`

목록의 각 지점에 필터를 단다. 그런 다음 기준선에서 이 파일 항목을 지운다(0이 되므로).

**남은 위반 중 필터를 달면 안 되는 것이 있다면** — 취소를 일부러 상태로 바꾸는 곳 같은 — 필터 대신 그 사실을 보고하고 기준선에 개수를 남겨라. 근거를 보고서에 적어라.

- [ ] **Step 6: 클린 빌드와 전체 테스트**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```
Expected: 고유 경고 8건, 전체 통과

- [ ] **Step 7: 커밋**

```bash
git add -A
git commit -m "fix(verification): propagate cancellation from the cache check

A cancellation during the cache lookup was logged as a cache error and the
pipeline continued into a full AI analysis run - the longest job in the tool
starting immediately after the user asked it to stop.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: 메타데이터 DFS 순회

**Files:**
- Modify: `src/ReSet.Core/Services/DbMetadataService.cs`
- Modify: `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`

**Interfaces:**
- Consumes: Task 1의 게이트
- Produces: 없음

**배경:** 의존성 그래프를 DFS로 걷는 루프들이 예외를 삼키고 계속 걷는다. 취소해도 그래프 전체를 다 순회한 뒤에야 멈춘다.

**테스트에 관한 정직한 한계:** 행동 테스트가 없다. 이 서비스는 실제 SQL 연결을 요구하며 단위 테스트로 취소 경로를 구동할 수 없다. 검증은 Task 1의 구문 게이트와 클린 빌드뿐이다. 새 테스트를 만들지 않으며, 만든 척하지 않는다.

- [ ] **Step 1: 위반 목록을 확인한다**

기준선에서 `ReSet.Core/Services/DbMetadataService.cs`의 숫자를 0으로 바꾼다.

Run: `dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~NoFileExceedsItsCancellationBaseline"`

실패 메시지의 `줄 (멤버)` 목록을 전부 기록한다. 이것이 작업 대상이다.

- [ ] **Step 2: 각 지점에 필터를 단다**

목록의 각 catch에 필터를 단다. 본문은 그대로 둔다.

```csharp
            catch (Exception ex) when (ex is not OperationCanceledException)
```

DFS 루프 안의 것들에는 한국어 주석을 단다.

```csharp
            // 취소를 삼키면 그래프 순회가 계속된다. 사용자가 멈추라고 한 뒤에도
            // 남은 의존성을 전부 걷고 나서야 반환된다.
            catch (Exception ex) when (ex is not OperationCanceledException)
```

**목록에 없는 catch는 건드리지 마라.** 동기 IO를 감싸는 soft-fail은 취소와 무관하며, 스캐너가 지적하지 않은 것이 그 근거다.

- [ ] **Step 3: 기준선을 내린다**

기준선에서 `ReSet.Core/Services/DbMetadataService.cs` 항목을 지운다.

- [ ] **Step 4: 클린 빌드와 전체 테스트**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```
Expected: 고유 경고 8건, 전체 통과

`DbMetadataServiceTests.cs`의 기존 CS8600/CS8602 8건이 그대로인지 확인하라. 이 파일을 건드리므로 경고가 늘어날 여지가 있다.

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "fix(metadata): stop walking the dependency graph after cancellation

The DFS loops swallowed every exception and kept walking, so a cancelled
analysis still traversed the remaining graph before returning.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: 문서 갱신

**Files:**
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: Task 1~4의 최종 상태
- Produces: 없음

- [ ] **Step 1: 실제 테스트 수를 확인한다**

```bash
dotnet test 2>&1 | tail -3
```

출력에 나온 실제 통과 수를 기록한다. **예상치를 적지 말고 실제 실행 결과를 쓴다.** 최근 두 사이클에서 이 숫자가 연속으로 어긋났다 — 한 번은 추정으로 6만큼, 그다음은 수정 웨이브가 테스트를 추가하고 문서를 안 고쳐 1만큼.

- [ ] **Step 2: 테스트 수를 갱신한다**

```bash
grep -n "개의 단위 테스트" AGENTS.md
```

찾은 줄의 숫자를 Step 1의 실제값으로 바꾼다.

- [ ] **Step 3: 취소 정책을 문서에 남긴다**

`AGENTS.md`의 검증 체크리스트(테스트 수를 언급하는 줄 근처)에 한 줄을 더한다.

```markdown
- [ ] 취소 가능한 `await`를 감싸는 `catch`에 `when (ex is not OperationCanceledException)` 필터를 달았는가? (`CancellationPolicyTests`가 자동 검사하며, 기준선 파일 `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`의 숫자는 고칠 때마다 함께 내려야 한다)
```

**범위 규율:** 이 한 줄과 테스트 수 외에는 손대지 않는다. 새 절을 만들거나 스캐너의 내부 동작을 서술하지 않는다. `docs/superpowers/` 아래의 과거 문서는 작성 시점의 기록이므로 고치지 않는다.

- [ ] **Step 4: 최종 확인**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```
Expected: 고유 경고 8건, 전체 통과

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "docs: record the cancellation policy and the new test count

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## 실행 순서와 의존 관계

```
Task 1 (스캐너 + 기준선) ──┬─→ Task 2 (코드젠 경로)   ─┐
                          ├─→ Task 3 (캐시 경로)     ─┼─→ Task 5 (문서)
                          └─→ Task 4 (DFS 순회)      ─┘
```

Task 2·3·4는 서로 독립이며 각각 다른 파일을 건드린다. 셋 다 Task 1의 게이트가 있어야 대상 목록을 얻을 수 있으므로 Task 1을 기다린다.

## 자체 검토 결과

**스펙 커버리지**

| 스펙 요구사항 | 담당 |
|---|---|
| 규칙 조건 1~4 | Task 1 Step 4 (`IsBroadCatch`, `ContainsCancellableAwait`, `FiltersCancellation`, `RethrowsEverything`) |
| 교정 1 — 명시적 OCE 절 제외 | Task 1 Step 4 `IsBroadCatch`, Step 2 다섯 번째 테스트 |
| 교정 2 — 앞선 형제 절 | Task 1 Step 4 `EarlierClauseHandlesCancellation`, Step 2 네 번째 테스트 |
| 구문 트리만 사용 | Task 1 Step 4 (`CSharpSyntaxTree.ParseText`, 시맨틱 모델 없음) |
| 기준선 파일별 개수 래칫 | Task 1 Step 8 |
| 양방향 실패 | Task 1 Step 8·9 |
| 실패 메시지에 전체 지점 목록 | Task 1 Step 8 |
| 기준선 초기값은 도구가 채움 | Task 1 Step 7 |
| `Program.cs` + `ExternalCliCodingEngine` (함께) | Task 2 |
| `VerificationPipelineOrchestrator` 캐시 경로 | Task 3 |
| `DbMetadataService` DFS | Task 4 |
| 행동 테스트 가능/불가 구분 | Task 2·4 서두(불가), Task 3 Step 1(가능) |
| Validator 프로젝트 등은 기준선에 남김 | Task 2~4가 해당 파일을 건드리지 않음 |

누락 없음.

**타입 일관성**

`CancellationOffender(RelativePath, Line, Member)`, `CancellationPolicyScanner.ScanSource`/`ScanDirectory`, `RepoPaths.FindRepoRoot` — Task 1에서 정의한 이름이 Task 2~4의 절차 설명에서 그대로 쓰인다.

**계획 수립 중 확인한 사실 세 가지**

1. `Microsoft.CodeAnalysis.CSharp`의 최신 안정 버전은 **5.6.0**이다(`dotnet package search`로 확인).
2. 저장소의 기존 필터는 `catch (Exception ex) when (ex is not OperationCanceledException)` 한 가지 형태로 통일되어 있다. 스캐너의 필터 탐지가 문자열 포함 검사로 충분한 근거다.
3. 스펙의 규칙에 거짓 양성 두 종류가 있었고 실례를 각각 찾았다(`DependencyAnalysisOrchestrator.cs:86`, `MetadataExporter.cs:92`). 위 "스펙 교정 두 건"에 기록했다.

**계획이 미리 정하지 않는 것**

Task 2·3·4의 수정 대상 개수는 계획에 없다. Task 1의 게이트가 알려주며, 각 태스크의 Step 1이 그 목록을 얻는 절차다. 넓은 catch 118곳 중 몇 곳이 실제 위반인지는 도구를 만들기 전에는 알 수 없고, 그것이 이 도구를 만드는 이유다.
