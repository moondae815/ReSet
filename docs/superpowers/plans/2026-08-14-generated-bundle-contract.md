# 생성 번들의 계약 정합성 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ReSet이 생성하는 `output/Jobs/<Job>/agent/` 번들이 외부 코딩 에이전트에게 그대로 넘길 수 있도록, 계약 불일치·무주인 인프라 객체·유령 테이블·빈 테스트를 만들어 내는 코드 지점을 고친다.

**Architecture:** 두 축으로 나뉜다. (1) 고정 자산 — 스텁·회차 지시문 문자열을 고쳐 다음 실행부터 다른 산출물이 나오게 한다. (2) L1 검사 — 조립된 계획서를 대상으로 카탈로그에 없는 테이블은 차단(단계 재생성)하고, 생략 지시 주석은 배너로 남긴다. AI 프롬프트는 건드리지 않는다.

**Tech Stack:** .NET 10, C# 13, xUnit, Serilog, NetArchTest.Rules(생성 산출물 쪽), Roslyn(`Microsoft.CodeAnalysis.CSharp` — 기존 정책 스캐너가 이미 사용)

**Spec:** `docs/superpowers/specs/2026-08-14-generated-bundle-contract-design.md`

## Global Constraints

- **AI 프롬프트 수정 금지.** `AiService.cs`는 이 계획에서 한 줄도 바꾸지 않는다. 골격·단계 섹션·목차 프롬프트가 전부 여기 있다.
- **소프트 페일(AGENTS.md 범주 2).** 새로 추가하는 모든 검사·수집은 자체 예외를 try-catch로 격리하고 파이프라인을 죽이지 않는다. 취소 토큰을 넘기는 `await`를 감싸는 광범위 catch에는 반드시 `when (ex is not OperationCanceledException)` 필터를 단다 — `CancellationPolicyTests`가 Roslyn으로 자동 검사한다.
- **두 언어 동수.** 스텁을 바꿀 때 C#과 Java 양쪽을 함께 바꾼다. `AgentContractStubTests.ArchitectureTestStub_ShouldExposeTheSameRuleCount_ForBothLanguages`가 이 원칙을 이미 고정하고 있다.
- **생성 코드의 이스케이프.** 에이전트에게 나가는 C#/Java 코드는 `DataAccessPolicy`/`MetadataExporter`의 **verbatim 문자열(`@"..."`) 안에** 들어간다. 코드 안의 모든 `"`는 `""`로 이스케이프해야 한다. 이 계획의 코드 블록은 **최종 산출물 형태**로 적었으므로, 문자열에 넣을 때 이스케이프를 적용할 것.
- **회차별 테스트 파일명은 단계 코드로 시작하지 않는다.** `FileMappingService.cs:72`가 `name.StartsWith(MappedName)`로 회차 산출물을 찾으므로, `S08LogicTests.cs`는 Tasklet 없이도 이름 게이트를 통과시킨다. 접미사 형태 `LogicTests_S08.cs`를 쓴다.
- **테스트 실행**: `dotnet test ReSet.slnx --nologo`. 착수 시점 베이스라인은 **1451개 통과 / 0 실패**다.
- **커밋**: 각 Task 끝에서 한 번. 메시지는 한국어 본문 + `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

---

### Task 1: `BatchInfraObjectCollector`

계획서에서 `batch.*` / `batch_shadow.*` 객체를 수집한다. Task 4(회차 0 목록)와 Task 5(③의 오탐 제외)가 **같은 접두사 정의**를 공유하기 위한 단일 출처다.

**Files:**
- Create: `src/ReSet.Core/Services/BatchInfraObjectCollector.cs`
- Test: `tests/ReSet.Core.Tests/BatchInfraObjectCollectorTests.cs`

**Interfaces:**
- Consumes: 없음(순수 함수)
- Produces:
  - `public sealed record BatchInfraObjects(IReadOnlyList<string> Names, IReadOnlyList<string> CollapsedRunIdVariants)`
  - `public static BatchInfraObjects BatchInfraObjects.Empty { get; }` — Task 4의 소프트 페일 경로가 쓴다
  - `public static BatchInfraObjects BatchInfraObjectCollector.Collect(string? planMarkdown)`
  - `public static bool BatchInfraObjectCollector.IsInfraObject(string? qualifiedName)`
  - `public const string BatchInfraObjectCollector.RunIdPlaceholder = "_<RunId>_"`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/BatchInfraObjectCollectorTests.cs`:

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchInfraObjectCollectorTests
    {
        [Fact]
        public void Collect_ShouldFindObjectsInsideAndOutsideCodeFences()
        {
            // 실측: EXEC는 펜스 안에(steps/S13.md), 산문 언급은 펜스 밖에(steps/S17.md:17)
            // 있다. 한쪽만 보면 목록이 조용히 짧아진다.
            var plan = """
                `batch.SwitchPublishedPartition`은 대상 테이블을 제한한다.

                ```sql
                EXEC batch.BuildS13InSummary @RunId = @pi_runId;
                INSERT INTO batch_shadow.TSettleByOUT_Run_S13 SELECT * FROM x;
                ```
                """;

            var result = BatchInfraObjectCollector.Collect(plan);

            Assert.Contains("batch.SwitchPublishedPartition", result.Names);
            Assert.Contains("batch.BuildS13InSummary", result.Names);
            Assert.Contains("batch_shadow.TSettleByOUT_<RunId>_S13", result.Names);
        }

        [Fact]
        public void Collect_ShouldCollapseRunIdLiteralVariantsIntoOneEntry()
        {
            // 실측: 같은 규칙(batch_shadow.<Table>_<RunId>_<StepCode>)의 자리표시자가
            // _RunId_ 와 _Run_ 두 리터럴로 굳었다. 접지 않으면 목록이 부풀고,
            // 회차 0이 존재하지 않는 테이블 두 개를 만든다.
            var plan = "batch_shadow.TSettleMst_RunId_S06 와 batch_shadow.TSettleMst_Run_S06";

            var result = BatchInfraObjectCollector.Collect(plan);

            Assert.Equal(new[] { "batch_shadow.TSettleMst_<RunId>_S06" }, result.Names);
            Assert.Equal(2, result.CollapsedRunIdVariants.Count);
        }

        [Fact]
        public void Collect_ShouldIgnoreTheEnglishWordBatch()
        {
            var result = BatchInfraObjectCollector.Collect("the batch job runs nightly. batch processing.");

            Assert.Empty(result.Names);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Collect_ShouldReturnEmptyForBlankInput(string? plan)
        {
            var result = BatchInfraObjectCollector.Collect(plan);

            Assert.Empty(result.Names);
            Assert.Empty(result.CollapsedRunIdVariants);
        }

        [Theory]
        [InlineData("batch.POQSettleRun", true)]
        [InlineData("batch_shadow.TSettleMst_Run_S03", true)]
        [InlineData("SETTLE_POQ_DB.batch.POQSettleCheckpoint", true)]
        [InlineData("dbo.TSettleMst", false)]
        [InlineData("PaymentDB.dbo.TTxMst", false)]
        [InlineData("TSettleMst", false)]
        [InlineData(null, false)]
        public void IsInfraObject_ShouldRecognizeOnlyTheBatchSchemas(string? name, bool expected)
        {
            Assert.Equal(expected, BatchInfraObjectCollector.IsInfraObject(name));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~BatchInfraObjectCollectorTests`
예상: 컴파일 실패 — `BatchInfraObjectCollector` 형식을 찾을 수 없음

- [ ] **Step 3: 구현한다**

`src/ReSet.Core/Services/BatchInfraObjectCollector.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 계획서가 참조하는 신규 인프라 스키마 객체의 목록.
    /// </summary>
    /// <param name="Names">정규화·중복 제거·정렬된 객체명.</param>
    /// <param name="CollapsedRunIdVariants">자리표시자가 리터럴로 굳어 접힌 원문.
    /// 사람이 규칙 위반을 볼 수 있게 함께 낸다 - 접기만 하고 숨기면 계획서가
    /// 규칙을 어겼다는 사실이 어디에도 남지 않는다.</param>
    public sealed record BatchInfraObjects(
        IReadOnlyList<string> Names,
        IReadOnlyList<string> CollapsedRunIdVariants)
    {
        public static BatchInfraObjects Empty { get; } =
            new(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// 계획서에서 batch·batch_shadow 스키마 객체를 수집한다.
    ///
    /// 이 클래스가 접두사 정의를 <b>단독 소유</b>한다. 회차 0 지시서(TaskFileComposer)와
    /// 미지 테이블 검사(MechanicalValidator)가 같은 판단을 해야 하기 때문이다 - 두 곳이
    /// 각자 접두사를 알면 한쪽이 신규 접두사를 놓쳤을 때 다른 쪽이 그 객체를 전부
    /// "존재하지 않는 테이블"로 오탐한다.
    /// </summary>
    public static class BatchInfraObjectCollector
    {
        /// <summary>Shadow 이름 규칙의 실행 식별자 자리표시자.</summary>
        public const string RunIdPlaceholder = "_<RunId>_";

        // batch_shadow를 먼저 시도한다. batch가 먼저면 "batch_shadow.X"에서 batch를
        // 먹고 '.'을 못 찾아 백트래킹한다 - 동작은 같지만 의도가 드러나지 않는다.
        private static readonly Regex ObjectRegex = new(
            @"\b(batch_shadow|batch)\.([A-Za-z_][A-Za-z_0-9]*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RunIdLiteralRegex = new(
            @"_(?:RunId|Run)_",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] Schemas = { "batch", "batch_shadow" };

        public static BatchInfraObjects Collect(string? planMarkdown)
        {
            if (string.IsNullOrWhiteSpace(planMarkdown))
            {
                return BatchInfraObjects.Empty;
            }

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var collapsed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in ObjectRegex.Matches(planMarkdown))
            {
                var schema = match.Groups[1].Value.ToLowerInvariant();
                var rawName = match.Groups[2].Value;
                var normalized = RunIdLiteralRegex.Replace(rawName, RunIdPlaceholder);

                if (!string.Equals(normalized, rawName, StringComparison.Ordinal))
                {
                    collapsed.Add($"{schema}.{rawName}");
                }

                names.Add($"{schema}.{normalized}");
            }

            return new BatchInfraObjects(names.ToList(), collapsed.ToList());
        }

        /// <summary>
        /// 이 이름이 계획서가 새로 만드는 인프라 객체인가. 카탈로그에 없는 것이
        /// 정상이므로 미지 테이블 검사에서 제외해야 한다.
        /// </summary>
        public static bool IsInfraObject(string? qualifiedName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedName))
            {
                return false;
            }

            var parts = qualifiedName.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            // 마지막 조각은 객체명이다. 그 앞의 어느 조각이든 batch 계열이면 인프라다
            // (3부 식별자 SETTLE_POQ_DB.batch.X도 인정한다).
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (Schemas.Contains(parts[i], StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~BatchInfraObjectCollectorTests`
예상: PASS (테스트 9개 — Fact 3 + Theory 3케이스 + Theory 7케이스)

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/BatchInfraObjectCollector.cs tests/ReSet.Core.Tests/BatchInfraObjectCollectorTests.cs
git commit -m "$(cat <<'EOF'
feat: collect the batch schema objects a plan references

회차 0 지시서와 미지 테이블 검사가 같은 접두사 정의를 공유하도록 수집기를
단일 출처로 둔다. Shadow 이름의 _RunId_/_Run_ 리터럴 변형은 한 항목으로 접되
접힌 원문을 함께 보고해, 계획서가 규칙을 어겼다는 사실이 사라지지 않게 한다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 스텁의 거처 이전과 실행 식별자 세 개

`AbstractSettleTasklet` 스텁은 지금 `MetadataExporter.cs`에 인라인 문자열로 박혀 있어 **테스트가 없는 유일한 계약 자산**이다. `DataAccessPolicy`로 옮겨 나머지 스텁과 한자리에 둔 뒤, `SettleContext`에 `RunId`·`InputHash`·`SourceSnapshotId`를 더한다.

**Files:**
- Modify: `src/ReSet.Core/Services/DataAccessPolicy.cs` (`RepositoryContractStub` 정의부 뒤, 파일 끝 `JavaRepositoryInterfaceStub` 앞)
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs:438-532` (C# 스텁 문자열 제거 후 호출로 대체), `:567-599` (Java SettleContext), `:651-733` (Java AbstractSettleTasklet), `:726` (`javaOrmBoundaryComment` 상수)
- Test: `tests/ReSet.Core.Tests/AgentContractStubTests.cs`

**Interfaces:**
- Consumes: `DataAccessPolicy.TaskletOrmComment` (기존, C#용)
- Produces:
  - `public static string DataAccessPolicy.AbstractTaskletStub(string targetLanguage)` — **양쪽 언어 모두 자리표시자가 치환된 최종 문자열**을 돌려준다. C#은 `[[ORM_BOUNDARY]]`, Java는 `[[ORM_BOUNDARY_JAVA]]`다. Java에도 치환이 있다는 사실이 이 이전의 함정이다 — `MetadataExporter.cs:732`가 `abstractTaskletStub.Replace("[[ORM_BOUNDARY_JAVA]]", javaOrmBoundaryComment)`를 하고 있고, 그 상수는 `:726`의 지역 `const`다. 문자열만 옮기고 치환을 두고 오면 Java 산출물에 자리표시자가 그대로 나가 컴파일이 깨진다.
  - `public static string DataAccessPolicy.JavaTaskletOrmComment` — `:726`의 지역 상수를 옮긴 것. 기존 `TaskletOrmComment`(C#)와 짝이다.
  - `public static string DataAccessPolicy.SettleContextStub(string targetLanguage)` — Java 전용 `SettleContext.java` 본문. C#은 `SettleContext`가 `AbstractTaskletStub` 안에 들어 있으므로 `AbstractTaskletStub`과 같은 문자열을 돌려주지 않고 **`NotSupportedException`을 던진다**(호출부가 언어를 착각한 것이므로 조용히 넘기지 않는다).

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AgentContractStubTests.cs`의 클래스 안에 추가:

```csharp
        /// <summary>
        /// 계획서는 Shadow 이름(batch_shadow.&lt;Table&gt;_&lt;RunId&gt;_&lt;StepCode&gt;),
        /// 체크포인트 키, 오류 로그, 게시 Manifest를 전부 RunId 기반으로 설계한다.
        /// 스텁이 그 값을 주지 않으면 18개 회차가 각자 다르게 우회한다.
        /// </summary>
        [Fact]
        public void AbstractTaskletStub_ShouldExposeExecutionIdentifiers_ForCSharp()
        {
            var stub = DataAccessPolicy.AbstractTaskletStub("C#");

            Assert.Contains("public Guid RunId { get; set; }", stub);
            Assert.Contains("public string InputHash { get; set; }", stub);
            Assert.Contains("public string SourceSnapshotId { get; set; }", stub);
        }

        [Fact]
        public void SettleContextStub_ShouldExposeExecutionIdentifiers_ForJava()
        {
            var stub = DataAccessPolicy.SettleContextStub("Java");

            Assert.Contains("getRunId", stub);
            Assert.Contains("setRunId", stub);
            Assert.Contains("getInputHash", stub);
            Assert.Contains("getSourceSnapshotId", stub);
        }

        /// <summary>
        /// 설계 1.1의 "최소 확장" 결정을 고정한다. 계획서 본문에는 ExecuteAsync와
        /// SettlementStepResult가 가득하지만, 실행 계약은 동기 Execute 하나다.
        /// 나중에 계획서를 보고 비동기를 끼워 넣으려는 사람에게 이 테스트가
        /// 결정을 상기시킨다.
        /// </summary>
        [Fact]
        public void AbstractTaskletStub_ShouldNotDeclareAsyncExecution_ForCSharp()
        {
            var stub = DataAccessPolicy.AbstractTaskletStub("C#");

            Assert.DoesNotContain("ExecuteAsync", stub);
            Assert.DoesNotContain("SettlementStepResult", stub);
            Assert.Contains("public StepResult Execute(SettleContext context)", stub);
        }

        /// <summary>
        /// C#의 SettleContext는 AbstractTaskletStub 안에 들어 있다. 언어를 착각한
        /// 호출을 조용히 통과시키면 SettleContext.cs라는 중복 파일이 나간다.
        /// </summary>
        [Fact]
        public void SettleContextStub_ShouldRejectCSharp()
        {
            Assert.Throws<NotSupportedException>(() => DataAccessPolicy.SettleContextStub("C#"));
        }

        /// <summary>
        /// 치환 책임을 DataAccessPolicy가 가진다. 두 언어 모두 자리표시자를 쓰고
        /// (C#은 [[ORM_BOUNDARY]], Java는 [[ORM_BOUNDARY_JAVA]]), 그대로 나가면
        /// 에이전트 프로젝트가 컴파일되지 않는다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void AbstractTaskletStub_ShouldAlreadySubstituteTheOrmBoundaryComment(string targetLanguage)
        {
            var stub = DataAccessPolicy.AbstractTaskletStub(targetLanguage);

            Assert.DoesNotContain("[[ORM_BOUNDARY", stub);
            Assert.Contains("[데이터 액세스 경계]", stub);
        }
```

파일 상단 `using`에 `System`이 없으면 추가한다(`NotSupportedException` 때문).

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~AgentContractStubTests`
예상: 컴파일 실패 — `DataAccessPolicy`에 `AbstractTaskletStub`/`SettleContextStub` 정의가 없음

- [ ] **Step 3: 스텁을 옮긴다**

`MetadataExporter.cs:438`의 `var baseClassStub = @"using System; ... }";` 문자열 **전체를 그대로** 잘라내 `DataAccessPolicy.cs`의 `RepositoryContractStub` 정의 뒤에 `private const string CSharpAbstractTasklet = @"...";` 로 붙인다. 문자열 내용은 한 글자도 바꾸지 않는다(이 단계에서는 이동만 한다).

같은 방식으로 `MetadataExporter.cs:567`의 `var settleContextStub = @"package com.reset.batch.core; ... }";` 를 `private const string JavaSettleContext = @"...";` 로 옮긴다.

`MetadataExporter.cs:651`의 `var abstractTaskletStub = @"package com.reset.batch.core; ...";` 를 `private const string JavaAbstractTasklet = @"...";` 로 옮긴다. **같이 옮겨야 할 것이 하나 더 있다** — `:726`의 `const string javaOrmBoundaryComment`를 `DataAccessPolicy`의 `public static string JavaTaskletOrmComment` 로 옮긴다(기존 `TaskletOrmComment`가 C# 짝이다). `:732`의 `Replace("[[ORM_BOUNDARY_JAVA]]", ...)`는 아래 접근자 안으로 들어간다.

그리고 접근자를 추가한다:

```csharp
        /// <summary>
        /// 코딩 에이전트가 강제로 상속해야 하는 베이스 클래스 스텁.
        ///
        /// MetadataExporter의 인라인 문자열에서 여기로 옮겼다. 나머지 계약 자산
        /// (ArchitectureTests·SettleContracts)은 이미 이 클래스에 있어 테스트가
        /// 붙어 있었는데, 정작 "반드시 상속하라"고 지시받는 이 파일만 테스트가
        /// 없었다 - 지시서가 가장 강하게 요구하는 것이 가장 검사되지 않았다.
        ///
        /// ORM 경계 주석 치환까지 마친 최종 문자열을 돌려준다. 두 언어가 서로 다른
        /// 자리표시자를 쓰므로 치환도 언어별로 다르다 - 치환을 호출부에 남기면
        /// 호출부가 하나 늘 때마다 자리표시자가 그대로 나갈 위험이 생긴다.
        /// </summary>
        public static string AbstractTaskletStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaAbstractTasklet.Replace("[[ORM_BOUNDARY_JAVA]]", JavaTaskletOrmComment)
                : CSharpAbstractTasklet.Replace("[[ORM_BOUNDARY]]", TaskletOrmComment);

        /// <summary>
        /// Java 전용 SettleContext.java. C#의 SettleContext는
        /// <see cref="AbstractTaskletStub"/> 문자열 안에 들어 있으므로 이 메서드로
        /// 얻을 수 없다 - 언어를 착각한 호출은 중복 파일을 산출물로 내보내므로
        /// 조용히 통과시키지 않고 던진다.
        /// </summary>
        public static string SettleContextStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaSettleContext
                : throw new NotSupportedException(
                    "C#의 SettleContext는 AbstractTaskletStub 안에 포함되어 있습니다.");
```

`MetadataExporter.cs`의 호출부를 바꾼다:

```csharp
                    if (targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase))
                    {
                        // 스텁은 System.Data만 참조하는 상태를 유지한다. ORM 패턴은 실행 코드가
                        // 아니라 주석으로만 넣어야 스텁이 특정 ORM 구현에 결합되지 않는다.
                        // 치환은 DataAccessPolicy가 마친 뒤 넘어온다.
                        await File.WriteAllTextAsync(
                            Path.Combine(agentSrcFolder, "AbstractSettleTasklet.cs"),
                            DataAccessPolicy.AbstractTaskletStub(targetLanguage),
                            Encoding.UTF8);
                    }
```

Java 분기의 두 쓰기도 같은 형태로 바꾼다:

```csharp
                        await File.WriteAllTextAsync(
                            Path.Combine(agentSrcFolder, "SettleContext.java"),
                            DataAccessPolicy.SettleContextStub(targetLanguage),
                            Utf8NoBom);
```

```csharp
                        await File.WriteAllTextAsync(
                            Path.Combine(agentSrcFolder, "AbstractSettleTasklet.java"),
                            DataAccessPolicy.AbstractTaskletStub(targetLanguage),
                            Utf8NoBom);
```

`:726`의 `javaOrmBoundaryComment` 지역 상수와 `:732`의 `abstractTaskletStubWithBoundary` 지역 변수는 이 시점에 쓰이는 곳이 없으므로 함께 지운다.

- [ ] **Step 4: 이동만으로 빌드와 기존 테스트가 통과하는지 확인한다**

실행: `dotnet test ReSet.slnx --nologo`
예상: 새로 쓴 5개 Fact는 여전히 FAIL(필드가 아직 없음), 나머지 1451개는 PASS. 이동이 문자열을 바꾸지 않았음을 여기서 확인한다.

- [ ] **Step 5: 세 필드를 더한다**

`DataAccessPolicy.CSharpAbstractTasklet` 안의 `SettleContext`를 다음으로 바꾼다(verbatim 문자열이므로 이스케이프 주의 — 이 블록에는 `"`가 없다):

```csharp
    public class SettleContext
    {
        public string Ymd { get; set; }
        public bool BypassPreCheck { get; set; }
        // 계획서는 Shadow 이름(batch_shadow.<Table>_<RunId>_<StepCode>), 체크포인트 키,
        // 오류 로그, 게시 Manifest를 전부 아래 값으로 짓는다. 스텁이 주지 않으면
        // 회차마다 다른 우회가 생겨 회차 간 코드가 어긋난다.
        //
        // 계획서 본문의 비동기 실행 계약·확장 결과 타입은 설계 의도 설명이다.
        // 실행 계약은 여기 있는 동기 Execute 하나다.
        public Guid RunId { get; set; }
        public string InputHash { get; set; }
        public string SourceSnapshotId { get; set; }
        public IDbConnectionFactory MainDb { get; set; }
        public IDbConnectionFactory PaymentDb { get; set; }
        public IDbConnectionFactory SettleCardDb { get; set; }
        public IDbConnectionFactory PlCardDb { get; set; }
        public ICheckpointRepository Checkpoint { get; set; }
    }
```

`Guid`는 스텁 첫 줄의 `using System;`으로 이미 해결된다.

> **이 주석에 `ExecuteAsync`·`SettlementStepResult`를 문자 그대로 쓰지 마십시오.** Step 1의
> `AbstractTaskletStub_ShouldNotDeclareAsyncExecution_ForCSharp`가 스텁 문자열 전체에 대해
> `Assert.DoesNotContain`으로 그 토큰을 금지합니다. 이 계획의 초판이 실제로 그렇게 적어
> 자기 테스트를 깨뜨렸고, 실행 중에 발견돼 위 문구로 고쳤습니다. 단언을 "선언 여부만
> 본다"로 좁히는 대신 문구를 바꾼 이유는, 그 넓은 금지가 바로 이 테스트의 목적이기
> 때문입니다 — 나중에 누군가 비동기를 되살리려 할 때 어휘 자체가 스텁에 없어야 합니다.
> 에이전트에게 그 타입 이름을 알려 주는 일은 Task 3의 규칙 10이 이미 합니다.

`DataAccessPolicy.JavaSettleContext`의 필드와 접근자에 다음을 더한다:

```java
    private java.util.UUID runId;
    private String inputHash;
    private String sourceSnapshotId;
```

```java
    public java.util.UUID getRunId() { return runId; }
    public void setRunId(java.util.UUID runId) { this.runId = runId; }
    public String getInputHash() { return inputHash; }
    public void setInputHash(String inputHash) { this.inputHash = inputHash; }
    public String getSourceSnapshotId() { return sourceSnapshotId; }
    public void setSourceSnapshotId(String sourceSnapshotId) { this.sourceSnapshotId = sourceSnapshotId; }
```

`java.util.UUID`를 완전 수식명으로 쓴다 — 이 스텁에는 import 블록이 없고, import를 새로 만들면 패키지 선언과 클래스 주석 사이에 줄을 넣어야 해서 diff가 커진다.

- [ ] **Step 6: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --nologo`
예상: 전부 PASS (1451 + 신규 5)

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/DataAccessPolicy.cs src/ReSet.Core/Services/MetadataExporter.cs tests/ReSet.Core.Tests/AgentContractStubTests.cs
git commit -m "$(cat <<'EOF'
feat: give the tasklet stub the execution identifiers the plan writes with

계획서는 Shadow 이름과 체크포인트 키를 RunId로 짓는데 SettleContext에 RunId가
없었다. 회차마다 다른 우회가 생기는 자리다. RunId·InputHash·SourceSnapshotId만
최소로 더하고, 비동기 실행 계약은 도입하지 않는다.

베이스 클래스 스텁을 MetadataExporter의 인라인 문자열에서 DataAccessPolicy로
옮긴다. 지시서가 "반드시 상속하라"고 요구하는 파일이 정작 테스트가 없는 유일한
계약 자산이었다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 진입점 규칙 10 — 계약의 권위 순서

**Files:**
- Modify: `src/ReSet.Core/Services/InstructionEntryPointComposer.cs:208` 인근(`TaskletInheritanceGuideline` 호출 뒤), `:242-245`(가이드라인 메서드 옆)
- Test: `tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`

**Interfaces:**
- Consumes: `EntryPointInputs.TargetLanguage` (기존)
- Produces: 진입점 §1에 규칙 10 한 줄. 새 public API 없음.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`에 추가:

```csharp
        [Fact]
        public async Task WriteAsync_ShouldDeclareTheStubAsTheOnlyBindingContract()
        {
            // 계획서 본문 18개 단계가 ExecuteAsync·SettlementStepResult를 쓰는데
            // 스텁은 동기 Execute다. 어느 쪽이 이기는지 지시서가 말하지 않으면
            // 회차마다 다른 결론이 난다.
            await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            var entryPoint = await File.ReadAllTextAsync(Path.Combine(_agentDir, "MigrationInstructions.md"));

            Assert.Contains("설계 의도", entryPoint);
            Assert.Contains("src/AbstractSettleTasklet.cs", entryPoint);
            Assert.Contains("스텁이 이깁니다", entryPoint);
            // 규칙 9 바로 뒤여야 한다 - 상속 강제와 권위 순서는 같이 읽혀야 한다.
            Assert.True(
                entryPoint.IndexOf("9. **[중요]**", StringComparison.Ordinal)
                    < entryPoint.IndexOf("10. **[중요]**", StringComparison.Ordinal),
                "규칙 10이 규칙 9보다 앞에 있습니다.");
        }
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~WriteAsync_ShouldDeclareTheStubAsTheOnlyBindingContract`
예상: FAIL — `Assert.Contains() Failure`, 진입점에 "설계 의도"가 없음

- [ ] **Step 3: 구현한다**

`InstructionEntryPointComposer.cs:208`의 `sb.AppendLine(TaskletInheritanceGuideline(inputs.TargetLanguage));` 바로 뒤에 한 줄 추가:

```csharp
            sb.AppendLine(ContractAuthorityGuideline(inputs.TargetLanguage));
```

`TaskletInheritanceGuideline` 메서드 정의 뒤에 새 메서드를 둔다:

```csharp
        /// <summary>
        /// 계획서와 스텁이 충돌할 때 어느 쪽이 이기는지 못 박는다.
        ///
        /// 실측: 한 Job의 단계 문서 18개가 전부 비동기 ExecuteAsync와 15필드짜리
        /// SettlementStepResult를 전제로 쓰였는데, 같은 번들의 스텁은 동기 Execute와
        /// 3필드 StepResult였다. 단계 문서 어느 것도 스텁을 언급하지 않았다 -
        /// 에이전트는 두 계약 사이에서 회차마다 다르게 선택할 수밖에 없었다.
        ///
        /// 스텁을 계획서에 맞춰 키우지 않고 이 문장을 넣는 이유: 계획서의 타입은
        /// Job마다 다르게 생성되므로 스텁이 따라갈 수 없다. 고정된 쪽이 권위여야 한다.
        /// </summary>
        private static string ContractAuthorityGuideline(string targetLanguage)
        {
            var stubPath = targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? "src/AbstractSettleTasklet.java"
                : "src/AbstractSettleTasklet.cs";

            return "10. **[중요]** 계획서 본문에 등장하는 `ExecuteAsync`, `SettlementStepResult`, " +
                "`StepExecutionStatus`, `SettlementExecutionContext` 등의 타입은 **설계 의도**를 설명하는 " +
                $"서술입니다. 실제 구현 계약은 `{stubPath}`이며, 둘이 충돌하면 **스텁이 이깁니다.** " +
                "스텁에 없는 타입이 필요하면 Tasklet 내부에 두고, `common/`이 정의한 공통 계약 파일은 " +
                "수정하지 마십시오. 실행 식별자(`RunId`, `InputHash`, `SourceSnapshotId`)는 `SettleContext`가 " +
                "제공하므로 Shadow 테이블 이름과 체크포인트 키는 그 값으로 지으십시오.";
        }
```

- [ ] **Step 4: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --nologo`
예상: 전부 PASS

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/InstructionEntryPointComposer.cs tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs
git commit -m "$(cat <<'EOF'
feat: state which contract wins when the plan and the stub disagree

단계 문서 18개가 비동기 계약을 전제로 쓰였는데 같은 번들의 스텁은 동기였고,
어느 쪽이 이기는지 아무 데도 적혀 있지 않았다. 규칙 10으로 스텁을 권위로 못 박는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: 회차 0에 인프라 객체 목록 싣기

**Files:**
- Modify: `src/ReSet.Core/Services/TaskFileComposer.cs:21-33`(`TaskFileInputs`), `:170-199`(`AppendBootstrap`)
- Modify: `src/ReSet.Core/Services/InstructionBundleWriter.cs:213-235`(`WriteTaskAsync`의 `TaskFileInputs` 생성부)
- Test: `tests/ReSet.Core.Tests/TaskFileComposerTests.cs`, `tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`

**Interfaces:**
- Consumes: `BatchInfraObjectCollector.Collect` (Task 1), `BundleInputs.FinalPlanMarkdown` (기존)
- Produces: `TaskFileInputs`에 마지막 위치로 `IReadOnlyList<string> InfraObjects` 추가. **기본값을 주지 않는다** — `BundleInputs.Coverage`가 기본값 없이 선언된 것과 같은 이유로, 배선을 빠뜨리면 컴파일이 깨져야 한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/TaskFileComposerTests.cs`에 추가(기존 `StepInputs()` 헬퍼 옆에 부트스트랩 헬퍼를 함께 둔다):

```csharp
        private static TaskFileInputs BootstrapInputs(IReadOnlyList<string> infraObjects) => new(
            Kind: StageKind.Bootstrap,
            JobName: "TestJob",
            TargetLanguage: "C#",
            StepCode: null,
            StepName: null,
            StepRelativePath: null,
            SpecRelativePath: null,
            Dependencies: Array.Empty<IndexEntry>(),
            HasStepContract: true,
            HasVerification: true,
            FailedStepCodes: Array.Empty<string>(),
            SinglePlanRelativePath: null,
            InfraObjects: infraObjects);

        [Fact]
        public void Compose_ShouldListInfraObjectsInTheBootstrapRound()
        {
            // 회차 0은 읽기 계약상 step 파일을 읽을 수 없다. 목록을 여기 박아 주지
            // 않으면 "계획서가 참조하는 객체를 만들라"는 문장을 지킬 방법이 없다.
            var markdown = TaskFileComposer.Compose(
                BootstrapInputs(new[] { "batch.POQSettleRun", "batch_shadow.TSettleMst_<RunId>_S06" }));

            Assert.Contains("인프라 스키마 객체", markdown);
            Assert.Contains("`batch.POQSettleRun`", markdown);
            Assert.Contains("`batch_shadow.TSettleMst_<RunId>_S06`", markdown);
        }

        [Fact]
        public void Compose_ShouldOmitTheInfraSectionEntirelyWhenThereIsNothingToBuild()
        {
            // 빈 제목만 남으면 "만들 것이 없다"가 아니라 "수집이 실패했다"로도 읽힌다.
            var markdown = TaskFileComposer.Compose(BootstrapInputs(Array.Empty<string>()));

            Assert.DoesNotContain("인프라 스키마 객체", markdown);
        }

        [Fact]
        public void Compose_ShouldNotListInfraObjectsInStepRounds()
        {
            // 인프라 DDL은 회차 0의 일이다. 단계 회차에 목록을 실으면 같은 객체를
            // 여러 회차가 만든다.
            var markdown = TaskFileComposer.Compose(StepInputs());

            Assert.DoesNotContain("인프라 스키마 객체", markdown);
        }
```

기존 `StepInputs()` 헬퍼에도 `InfraObjects: Array.Empty<string>()` 인자를 더한다.

`tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs`에 배선 테스트를 추가한다. 먼저 `FinalPlan` 상수에 인프라 참조가 담기도록 단계 본문 한 줄을 더한 뒤:

```csharp
        [Fact]
        public async Task WriteAsync_ShouldFeedCollectedInfraObjectsIntoTheBootstrapTask()
        {
            await new InstructionBundleWriter().WriteAsync(Inputs(Layout()), CancellationToken.None);

            var bootstrap = await File.ReadAllTextAsync(Path.Combine(_agentDir, "task-00-bootstrap.md"));

            Assert.Contains("`batch.POQSettleCheckpoint`", bootstrap);
        }
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~TaskFileComposerTests`
예상: 컴파일 실패 — `TaskFileInputs`에 `InfraObjects` 인자가 없음

- [ ] **Step 3: `TaskFileInputs`를 확장한다**

`TaskFileComposer.cs:21-33`:

```csharp
    /// <param name="InfraObjects">계획서가 참조하는 batch·batch_shadow 스키마 객체.
    /// 회차 0만 사용한다. 기본값을 두지 않는 이유는 BundleInputs.Coverage와 같다 -
    /// 배선을 빠뜨리면 조용히 빈 목록이 되는 대신 컴파일이 깨져야 한다.</param>
    public sealed record TaskFileInputs(
        StageKind Kind,
        string JobName,
        string TargetLanguage,
        string? StepCode,
        string? StepName,
        string? StepRelativePath,
        string? SpecRelativePath,
        IReadOnlyList<IndexEntry> Dependencies,
        bool HasStepContract,
        bool HasVerification,
        IReadOnlyList<string> FailedStepCodes,
        string? SinglePlanRelativePath,
        IReadOnlyList<string> InfraObjects);
```

- [ ] **Step 4: 부트스트랩 본문에 절을 렌더한다**

`AppendBootstrap`의 `AppendDependencies(sb, inputs);` **앞에** 호출을 넣고:

```csharp
            AppendInfraObjects(sb, inputs);
            AppendDependencies(sb, inputs);
```

`AppendDependencies` 옆에 메서드를 추가한다:

```csharp
        /// <summary>
        /// 계획서의 SQL이 EXEC하거나 참조하는 신규 스키마 객체를 회차 0에 실명으로 싣는다.
        ///
        /// 문장만 주고 목록을 주지 않으면 지킬 수 없는 지시가 된다 - 회차 0은
        /// "단계 상세 문서를 읽지 마십시오"를 함께 받으므로 목록을 스스로 모을 방법이 없다.
        ///
        /// 목록이 비면 절 자체를 내지 않는다. 빈 제목은 "만들 것이 없다"와
        /// "수집이 실패했다"를 구별해 주지 못한다.
        /// </summary>
        private static void AppendInfraObjects(StringBuilder sb, TaskFileInputs inputs)
        {
            if (inputs.InfraObjects.Count == 0)
            {
                return;
            }

            sb.AppendLine("## 이번 회차에서 만들 인프라 스키마 객체");
            sb.AppendLine();
            sb.AppendLine("계획서의 SQL이 아래 객체를 참조합니다. 이 회차에서 DDL과 모듈의 골격을 만드십시오.");
            sb.AppendLine("단계별 모듈의 업무 로직 본문은 해당 단계 회차가 채웁니다.");
            sb.AppendLine();
            sb.AppendLine($"`{BatchInfraObjectCollector.RunIdPlaceholder}`는 실행 식별자 자리표시자입니다. " +
                "`SettleContext.RunId` 값으로 치환해 이름을 지으십시오.");
            sb.AppendLine();

            foreach (var name in inputs.InfraObjects)
            {
                sb.AppendLine($"- `{name}`");
            }

            sb.AppendLine();
        }
```

- [ ] **Step 5: 배선한다**

`InstructionBundleWriter.WriteAsync`에서 `var dependencies = await WriteDependencySchemasAsync(...)` 근처에 수집을 추가한다:

```csharp
            // 계획서와 카탈로그가 둘 다 손에 있는 유일한 지점이다.
            //
            // 수집 실패가 번들 작성을 죽이지 않게 격리한다(AGENTS.md 범주 2). 취소
            // 필터를 달지 않는 이유: Collect는 문자열 위의 동기 정규식이라 취소 토큰을
            // 넘기는 await를 감싸지 않는다 - CancellationPolicyTests가 보는 형태가 아니다.
            var infra = BatchInfraObjects.Empty;
            try
            {
                infra = BatchInfraObjectCollector.Collect(inputs.FinalPlanMarkdown);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "인프라 스키마 객체 수집 중 오류가 발생했습니다. 목록 없이 진행합니다.");
                warnings.Add("인프라 스키마 객체 수집에 실패해 회차 0에 목록을 싣지 못했습니다.");
            }

            foreach (var variant in infra.CollapsedRunIdVariants)
            {
                warnings.Add(
                    $"Shadow 이름 규칙의 자리표시자가 리터럴로 굳었습니다: {variant} " +
                    $"(→ {BatchInfraObjectCollector.RunIdPlaceholder}로 접어 한 항목으로 처리했습니다)");
            }
```

`warnings`는 `InstructionBundleWriter.cs:59`의 `var warnings = new List<string>(slices.Warnings);`이며 `:264`에서 `BundleResult.Warnings`가 된다. `MetadataExporter.cs:415-418`이 그것을 로그로 흘린다.

`BatchInfraObjects.Empty`는 Task 1의 record에 정적 속성으로 더한다:

```csharp
        public static BatchInfraObjects Empty { get; } =
            new(Array.Empty<string>(), Array.Empty<string>());
```

`Collect`의 빈 입력 반환도 이 속성을 쓰도록 바꾼다.

`WriteTaskAsync` 안의 `new TaskFileInputs(...)` 생성부 마지막에 인자를 더한다:

```csharp
                    SinglePlanRelativePath: singlePlanRelative,
                    // 인프라 목록은 회차 0만 받는다. 단계 회차에 실으면 같은 객체를
                    // 여러 회차가 만든다.
                    InfraObjects: kind == StageKind.Bootstrap ? infra.Names : Array.Empty<string>());
```

- [ ] **Step 6: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --nologo`
예상: 전부 PASS

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/TaskFileComposer.cs src/ReSet.Core/Services/InstructionBundleWriter.cs tests/ReSet.Core.Tests/TaskFileComposerTests.cs tests/ReSet.Core.Tests/InstructionBundleWriterTests.cs
git commit -m "$(cat <<'EOF'
feat: name the batch schema objects the bootstrap round must build

실측 산출물은 batch·batch_shadow 객체 67종을 EXEC하는데 그것을 만드는 회차가
없었다. 회차 0은 읽기 계약상 step 파일을 볼 수 없어 목록을 스스로 모을 수도
없다. 수집한 실명을 회차 0 지시서에 싣는다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: 카탈로그에 없는 테이블을 결함으로 판정

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs:174`(`ValidateBatchStep` 시그니처와 본문)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs` (신규. 기존 검증기 테스트 파일이 이미 있으면 그쪽에 붙인다)

**Interfaces:**
- Consumes: `BatchInfraObjectCollector.IsInfraObject` (Task 1), `MechanicalValidator.BareObjectName` (기존 `internal static`)
- Produces: `public StepValidationResult ValidateBatchStep(string? stepMarkdown, BatchStepPlan step, IReadOnlyCollection<string> knownTableNames)` — **인자 3개.** 기존 2인자 오버로드를 남기지 않는다. 남기면 배선을 빠뜨린 호출부가 조용히 검사를 끄기 때문이다(Task 6의 스캐너가 그것을 막지만, 애초에 존재하지 않는 편이 낫다).

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class MechanicalValidatorBatchStepTests
    {
        private static BatchStepPlan Step(params string[] targetTables) => new(
            Code: "S17",
            Name: "완료 파티션 원자적 게시",
            LegacyProcedures: Array.Empty<string>(),
            TargetTables: targetTables,
            ErrorCodes: Array.Empty<string>(),
            Chunkable: false,
            SchemaTables: Array.Empty<string>());

        private static readonly string[] Catalog = { "dbo.TSettleMst", "dbo.TStatPGCollect", "dbo.TSettleMiss" };

        private static string Section(string body) => $"""
            ### S17 완료 파티션 원자적 게시

            ```sql
            {body}
            ```
            """;

        [Fact]
        public void ValidateBatchStep_ShouldRejectATableThatIsInNoCatalog()
        {
            // 실측: S17이 dbo.TSettleSummary를 게시 대상으로 지목했는데 그 테이블은
            // 이 작업의 DDL 55종 어디에도 없다. 구현 자체가 불가능한 지시다.
            var markdown = Section("EXEC batch.SwitchPublishedPartition @TargetTable = N'dbo.TSettleSummary';");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

            Assert.Contains(result.Errors, e => e.Contains("dbo.TSettleSummary", StringComparison.Ordinal));
            // 본문 결함이므로 재생성으로 고칠 수 있어야 한다 - PlanDefects가 아니다.
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptTheBatchSchemaObjectsThePlanCreates()
        {
            // batch.*는 카탈로그에 없는 것이 정상이다. 이것을 결함으로 들면
            // 모든 단계가 전부 오탐으로 걸린다.
            var markdown = Section("INSERT INTO batch.POQSettleCheckpoint SELECT * FROM dbo.TSettleMst;");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

            Assert.DoesNotContain(result.Errors, e => e.Contains("POQSettleCheckpoint", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptTablesThatAreInTheCatalog()
        {
            var markdown = Section("UPDATE dbo.TSettleMst SET OutState = 9;");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

            Assert.DoesNotContain(result.Errors, e => e.Contains("존재하지", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldIgnoreTableNamesThatAppearOnlyInProse()
        {
            // 추출 범위를 백틱과 SQL 펜스로 제한한 것을 고정한다. 산문까지 훑으면
            // "요약 테이블" 같은 서술이 식별자로 오인된다.
            var markdown = """
                ### S17 완료 파티션 원자적 게시

                게시 대상은 dbo.TSettleSummary 계열이다.

                ```sql
                SELECT 1;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

            Assert.DoesNotContain(result.Errors, e => e.Contains("dbo.TSettleSummary", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldSkipTheCheckWhenTheCatalogIsEmpty()
        {
            // definitions가 null인 경로(오프라인 스냅숏 등)의 소프트 스킵.
            // 카탈로그가 없다는 이유로 모든 테이블을 유령으로 몰면 안 된다.
            var markdown = Section("EXEC batch.SwitchPublishedPartition @TargetTable = N'dbo.TSettleSummary';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Array.Empty<string>());

            Assert.DoesNotContain(result.Errors, e => e.Contains("dbo.TSettleSummary", StringComparison.Ordinal));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~MechanicalValidatorBatchStepTests`
예상: 컴파일 실패 — `ValidateBatchStep`이 인자 2개만 받음

- [ ] **Step 3: 구현한다**

`MechanicalValidator.cs`의 시그니처를 바꾸고:

```csharp
        /// <param name="knownTableNames">이 작업의 스키마 카탈로그(SpDefinition.Dependencies).
        /// 비어 있으면 미지 테이블 검사를 실행하지 않는다 - 카탈로그가 없다는 사실을
        /// 모든 테이블이 유령이라는 판정으로 바꾸지 않기 위한 소프트 스킵이다.</param>
        public StepValidationResult ValidateBatchStep(
            string? stepMarkdown, BatchStepPlan step, IReadOnlyCollection<string> knownTableNames)
```

`result.Errors.AddRange(result.PlanDefects);` **앞에** 검사를 넣는다:

```csharp
            CheckUnknownTableReferences(stepMarkdown, step, knownTableNames, result);
```

같은 클래스에 메서드를 추가한다:

```csharp
        // 백틱 인용과 SQL 펜스 안의 2부·3부 식별자만 본다. 산문까지 훑으면 서술이
        // 식별자로 오인되고, 그 오탐은 단계 재생성을 유발해 비용이 실재한다.
        private static readonly Regex QualifiedTableRegex = new(
            @"\b([A-Za-z_][A-Za-z_0-9]*)\.([A-Za-z_][A-Za-z_0-9]*)(?:\.([A-Za-z_][A-Za-z_0-9]*))?\b",
            RegexOptions.Compiled);

        /// <summary>
        /// 계획서가 쓰겠다고 적은 테이블이 실재하는지 본다.
        ///
        /// 실측: S17이 dbo.TSettleSummary로 파티션을 교체하라고 지시했는데 그 테이블은
        /// 카탈로그 55종에 없고, S13이 만드는 요약 테이블 4개와 이름도 다르다. 문서 레벨
        /// L1은 헤더·축약어·Mermaid만 보므로 그것을 잡을 곳이 아무 데도 없었다.
        ///
        /// batch·batch_shadow는 제외한다. 계획서가 새로 만드는 객체라 카탈로그에 없는
        /// 것이 정상이며, 그 판단은 BatchInfraObjectCollector가 단독 소유한다.
        /// </summary>
        private static void CheckUnknownTableReferences(
            string stepMarkdown,
            BatchStepPlan step,
            IReadOnlyCollection<string> knownTableNames,
            StepValidationResult result)
        {
            if (knownTableNames.Count == 0)
            {
                Log.Information(
                    "{Code}: 스키마 카탈로그가 비어 있어 미지 테이블 검사를 건너뜁니다.", step.Code);
                return;
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in knownTableNames)
            {
                var bare = BareObjectName(name);
                if (bare.Length > 0)
                {
                    known.Add(bare);
                }
            }

            // 목차가 선언한 대상 테이블도 알려진 것으로 친다. 카탈로그 수집이
            // 놓친 대상 테이블 때문에 정상 단계가 걸리는 일을 막는다.
            foreach (var declared in step.TargetTables.Concat(step.SchemaTables))
            {
                var bare = BareObjectName(declared);
                if (bare.Length > 0)
                {
                    known.Add(bare);
                }
            }

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in ExtractQuotedIdentifiers(stepMarkdown))
            {
                if (BatchInfraObjectCollector.IsInfraObject(candidate))
                {
                    continue;
                }

                var bare = BareObjectName(candidate);
                if (bare.Length == 0 || known.Contains(bare) || !reported.Add(bare))
                {
                    continue;
                }

                result.Errors.Add(
                    $"{step.Code} 섹션이 `{candidate}`를 참조하지만 이 작업의 스키마 카탈로그에도, " +
                    "이 계획서가 만드는 batch 스키마 객체에도 없습니다. 실재하는 대상으로 바꾸거나, " +
                    "신규 객체라면 batch 스키마에 두십시오.");
            }
        }

        /// <summary>
        /// 백틱 인용과 코드 펜스 안에서만 수식 식별자를 뽑는다.
        /// </summary>
        private static IEnumerable<string> ExtractQuotedIdentifiers(string markdown)
        {
            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var fenceFlags = ComputeFenceLineFlags(lines);
            var found = new List<string>();

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (fenceFlags[i])
                {
                    // 펜스 줄 자체(```sql)는 식별자를 담지 않는다.
                    if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (Match m in QualifiedTableRegex.Matches(line))
                    {
                        found.Add(m.Value);
                    }
                    continue;
                }

                foreach (Match backtick in BacktickIdentifierRegex.Matches(line))
                {
                    var inner = backtick.Groups[1].Value.Trim();
                    foreach (Match m in QualifiedTableRegex.Matches(inner))
                    {
                        found.Add(m.Value);
                    }
                }
            }

            return found;
        }
```

> `ComputeFenceLineFlags`와 `BacktickIdentifierRegex`는 `CheckSchemaClaims`가 이미 쓰는 이 클래스의 기존 멤버다. 새로 만들지 말 것. `BacktickIdentifierRegex`의 그룹 1이 백틱 안 문자열인 것도 그 메서드에서 확인된다.

`ExtractQuotedIdentifiers`가 SQL 예약어 조합(`N'dbo.TSettleSummary'` 안의 값 등)을 어떻게 다루는지는 Step 1의 테스트가 고정한다. 정규식이 문자열 리터럴 안의 `dbo.TSettleSummary`도 잡는 것은 **의도된 동작**이다 — 실측 결함이 정확히 그 형태(`@TargetTable = N'dbo.TSettleSummary'`)였다.

- [ ] **Step 4: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~MechanicalValidatorBatchStepTests`
예상: PASS 5개. 이 시점에 `VerificationPipelineOrchestrator`는 아직 2인자로 호출하므로 **솔루션 전체 빌드는 깨진다.** Task 6에서 고친다.

- [ ] **Step 5: 커밋하지 않는다**

Task 6과 한 커밋으로 묶는다. 시그니처 변경과 호출부 수정이 분리된 커밋이면 중간 커밋이 빌드되지 않는다.

---

### Task 6: 카탈로그 배선과 Roslyn 스캐너

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1676-1684`(`RunConsolidatedPipelineAsync`에서 카탈로그 도출), `:2890-2902`(`GenerateBySplitAsync` 시그니처), `:1854`·`:2293`(두 호출부), `:2973-2996`(`RunStepAsync`), `:3057-3064`(`GenerateStepSectionWithFloorRetryAsync` 시그니처), `:3109`(검증기 호출)
- Create: `tests/ReSet.Core.Tests/KnownTableWiringPolicyScanner.cs`
- Create: `tests/ReSet.Core.Tests/KnownTableWiringPolicyTests.cs`

**Interfaces:**
- Consumes: `MechanicalValidator.ValidateBatchStep(markdown, step, knownTableNames)` (Task 5), `RunConsolidatedPipelineAsync`의 `IReadOnlyList<SpDefinition>? definitions` (기존)
- Produces: `KnownTableWiringPolicyScanner.ScanSource(string source)` / `.ScanFile(string path)` → `IReadOnlyList<KnownTableCallOffender>`. `KnownTableCallOffender(int Line, string Expression)`는 네임스페이스 직하의 `public sealed record`다 — `SpecExpectationsWiringPolicyScanner.cs:12`의 `ValidatorCallExpectationsOffender`와 같은 형태로 맞춘다.

- [ ] **Step 1: 배선 스캐너 테스트를 쓴다**

`tests/ReSet.Core.Tests/KnownTableWiringPolicyTests.cs`:

```csharp
using System.IO;
using System.Linq;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class KnownTableWiringPolicyTests
{
    // 규칙: VerificationPipelineOrchestrator 안의 _validator.ValidateBatchStep(...)
    // 호출은 전부 세 번째 인자(카탈로그)를 받아야 한다. 하나라도 2인자로 떨어지면
    // 그 경로에서만 미지 테이블 검사가 조용히 꺼진다 - 이 저장소가 _validator.Validate
    // 에서 이미 겪은 실패 모드다(SpecExpectationsWiringPolicyScanner 참고).

    [Fact]
    public void Scanner_FlagsATwoArgumentValidateBatchStepCall()
    {
        var source = @"
class C
{
    private readonly MechanicalValidator _validator;
    void M(string content, object step) => _validator.ValidateBatchStep(content, step);
}";

        var offender = Assert.Single(KnownTableWiringPolicyScanner.ScanSource(source));
        Assert.Contains("_validator.ValidateBatchStep(content, step)", offender.Expression);
    }

    [Fact]
    public void Scanner_DoesNotFlagAThreeArgumentCall()
    {
        var source = @"
class C
{
    private readonly MechanicalValidator _validator;
    void M(string content, object step, object catalog)
        => _validator.ValidateBatchStep(content, step, catalog);
}";

        Assert.Empty(KnownTableWiringPolicyScanner.ScanSource(source));
    }

    [Fact]
    public void Scanner_DoesNotFlagADifferentlyNamedReceiver()
    {
        var source = @"
class C
{
    void M(object validator, string content, object step)
        => validator.ValidateBatchStep(content, step);
}";

        Assert.Empty(KnownTableWiringPolicyScanner.ScanSource(source));
    }

    [Fact]
    public void Orchestrator_PassesTheCatalogAtEveryCallSite()
    {
        // 저장소 루트 탐색은 RepoPaths.FindRepoRoot()를 쓴다 - CancellationPolicyScanner.cs:240에
        // 이미 있고 SpecExpectationsWiringPolicyTests가 그것을 쓴다. 두 스캐너 테스트가
        // 서로 다른 경로 규칙을 쓰면 한쪽이 CI에서만 깨진다.
        var orchestratorPath = Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs");

        var offenders = KnownTableWiringPolicyScanner.ScanFile(orchestratorPath);

        Assert.True(
            offenders.Count == 0,
            "카탈로그 인자 없이 ValidateBatchStep을 호출한 곳: " +
            string.Join(", ", offenders.Select(o => $"{o.Line}행 {o.Expression}")));
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~KnownTableWiringPolicyTests`
예상: 컴파일 실패 — `KnownTableWiringPolicyScanner`가 없음

- [ ] **Step 3: 스캐너를 구현한다**

`tests/ReSet.Core.Tests/KnownTableWiringPolicyScanner.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReSet.Core.Tests;

public sealed record KnownTableCallOffender(int Line, string Expression);

/// <summary>
/// `_validator.ValidateBatchStep(...)` 호출이 카탈로그 인자를 받는지 구문 트리로 본다.
///
/// 수신자 이름(_validator)까지 확인한다 - 다른 이름의 지역 변수에 같은 메서드가
/// 있을 수 있고, 이 규칙이 지키려는 것은 오케스트레이터의 그 필드 하나다.
/// </summary>
public static class KnownTableWiringPolicyScanner
{
    private const string ReceiverName = "_validator";
    private const string MethodName = "ValidateBatchStep";
    private const int RequiredArgumentCount = 3;

    public static IReadOnlyList<KnownTableCallOffender> ScanSource(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);

        return tree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax member &&
                member.Name.Identifier.Text == MethodName &&
                member.Expression is IdentifierNameSyntax receiver &&
                receiver.Identifier.Text == ReceiverName)
            .Where(invocation => invocation.ArgumentList.Arguments.Count < RequiredArgumentCount)
            .Select(invocation => new KnownTableCallOffender(
                invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                invocation.ToString()))
            .ToList();
    }

    public static IReadOnlyList<KnownTableCallOffender> ScanFile(string filePath) =>
        ScanSource(File.ReadAllText(filePath));
}
```

> `Microsoft.CodeAnalysis.CSharp` 패키지 참조는 `ReSet.Core.Tests.csproj`에 이미 있다(`CancellationPolicyScanner`·`SpecExpectationsWiringPolicyScanner`가 쓴다). 없으면 추가한다.

- [ ] **Step 4: 오케스트레이터를 배선한다**

`RunConsolidatedPipelineAsync` 안, 파이프라인이 시작되는 지점에서 카탈로그를 만든다:

```csharp
            // 미지 테이블 검사의 재료. definitions가 없으면 빈 집합이 되고,
            // 검증기는 그때 검사를 건너뛴다(소프트 스킵).
            var knownTableNames = (definitions ?? Array.Empty<SpDefinition>())
                .SelectMany(sp => sp.Dependencies)
                .Select(dep => string.IsNullOrEmpty(dep.Database)
                    ? $"{dep.Schema}.{dep.Name}"
                    : $"{dep.Database}.{dep.Schema}.{dep.Name}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (knownTableNames.Count == 0)
            {
                Log.Information(
                    "스키마 카탈로그가 비어 있어 단계별 미지 테이블 검사를 실행하지 않습니다 - JobName: {JobName}",
                    jobName);
            }
```

> `dep.Database`/`dep.Schema`/`dep.Name`은 `InstructionBundleWriter.WriteDependencySchemasAsync`가 쓰는 것과 같은 속성이다(`InstructionBundleWriter.cs:503` 인근 참고). 그쪽과 **같은 조합 규칙**을 써야 두 소비자가 같은 이름을 본다.

`GenerateBySplitAsync`의 시그니처에 매개변수를 더한다(마지막 `CancellationToken` 앞):

```csharp
            IReadOnlyList<string> knownTableNames,
            CancellationToken cancellationToken)
```

두 호출부(`:1854`, `:2293`)에 `knownTableNames`를 넘긴다.

`RunStepAsync` 안의 호출:

```csharp
                    var (markdown, violation) = await GenerateStepSectionWithFloorRetryAsync(
                        step, steps, conventions, specs, targetLanguage, jobName,
                        knownTableNames, cancellationToken);
```

`GenerateStepSectionWithFloorRetryAsync`의 시그니처에도 같은 매개변수를 더하고, `:3109`를 바꾼다:

```csharp
                var stepResult = _validator.ValidateBatchStep(content, step, knownTableNames);
```

- [ ] **Step 5: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --nologo`
예상: 전부 PASS (Task 5의 5개 + 스캐너 4개 포함)

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs tests/ReSet.Core.Tests/KnownTableWiringPolicyScanner.cs tests/ReSet.Core.Tests/KnownTableWiringPolicyTests.cs
git commit -m "$(cat <<'EOF'
feat: block a step that publishes into a table nobody has

실측 산출물의 S17은 dbo.TSettleSummary로 파티션을 교체하라고 지시했는데 그
테이블은 DDL 55종 어디에도 없다. 문서 레벨 L1은 헤더와 축약어만 보므로 잡을
곳이 없었다. 단계 단위로 잡아 그 섹션만 재생성한다.

카탈로그는 RunConsolidatedPipelineAsync가 이미 받고 있는 definitions에서 나온다.
호출부가 인자를 빠뜨리면 그 경로에서만 검사가 꺼지므로 Roslyn 스캐너로 고정한다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: 생략 지시 주석 배너

**Files:**
- Create: `src/ReSet.Core/Services/OmissionCommentScanner.cs`
- Modify: `src/ReSet.Core/Services/VerificationBanner.cs` (`UnverifiableSteps` 앞에 새 메서드)
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:2504` 앞(배너 부착)
- Test: `tests/ReSet.Core.Tests/OmissionCommentScannerTests.cs`, `tests/ReSet.Core.Tests/VerificationBannerTests.cs`

**Interfaces:**
- Consumes: 없음(순수 함수)
- Produces:
  - `public static IReadOnlyList<string> OmissionCommentScanner.Scan(string? planMarkdown)` — 적발된 주석 줄(trim된 원문), 중복 제거, 최대 20개
  - `public static string VerificationBanner.OmissionComments(IReadOnlyList<string> comments)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/OmissionCommentScannerTests.cs`:

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class OmissionCommentScannerTests
    {
        [Theory]
        [InlineData("    -- 나머지 실제 컬럼도 원본 순서가 아닌 명시적 이름으로 모두 기술")]
        [InlineData("    -- 나머지 S03 대상도 같은 DELETE 후 INSERT 순서를 적용")]
        [InlineData("        -- 위 INSERT 목록과 동일한 전체 컬럼")]
        public void Scan_ShouldFlagCommentsThatStandInForOmittedCode(string comment)
        {
            var plan = $"```sql\nSELECT 1;\n{comment}\n```";

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Theory]
        [InlineData("    -- 원본 필터 YMD = @pi_strYMD AND USESTATE = 2를 모두 유지한다.")]
        [InlineData("    -- 원본 선행 보호 조건을 그대로 보존한다.")]
        public void Scan_ShouldNotFlagInstructionCommentsThatDemandPreservation(string comment)
        {
            // 오탐 경계를 고정한다. 배너가 잦으면 사람이 읽지 않게 되므로,
            // "유지하라"는 지시는 생략 지시가 아니다.
            var plan = $"```sql\nSELECT 1;\n{comment}\n```";

            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldIgnoreProseOutsideCodeFences()
        {
            // 산문에서 "나머지 단계도 같은 방식으로 적용한다"는 정상적인 설명이다.
            var plan = "나머지 단계도 같은 방식을 적용한다.\n\n```sql\nSELECT 1;\n```";

            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldDeduplicateIdenticalComments()
        {
            var line = "    -- 나머지 실제 컬럼도 모두 기술";
            var plan = $"```sql\n{line}\n{line}\n```";

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Scan_ShouldReturnEmptyForBlankInput(string? plan)
        {
            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }
    }
}
```

`tests/ReSet.Core.Tests/VerificationBannerTests.cs`에 추가:

```csharp
    [Fact]
    public void OmissionComments_ShouldNameTheOffendingLines()
    {
        var banner = VerificationBanner.OmissionComments(new[] { "-- 나머지 실제 컬럼도 모두 기술" });

        Assert.Contains("[!WARNING]", banner);
        Assert.Contains("나머지 실제 컬럼도 모두 기술", banner);
        // 규칙 7이 에이전트에게 금지한 형태를 계획서가 시범 보이고 있다는 사실을 말해야 한다.
        Assert.Contains("생략", banner);
    }
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~OmissionCommentScannerTests`
예상: 컴파일 실패 — `OmissionCommentScanner`가 없음

- [ ] **Step 3: 스캐너를 구현한다**

`src/ReSet.Core/Services/OmissionCommentScanner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코드 자리에 대신 서 있는 주석을 찾는다.
    ///
    /// 지시서 규칙 7은 에이전트에게 `// TODO` 같은 자리표시자를 금지하는데, 실측
    /// 계획서 자신이 그 형태를 시범 보였다 - `-- 나머지 실제 컬럼도 ... 모두 기술`.
    /// 에이전트는 계획서를 본보기로 삼으므로 그대로 복사한다.
    ///
    /// 차단이 아니라 배너인 이유: 같은 자리에 `-- 원본 필터 ...를 모두 유지한다`처럼
    /// 생략이 아니라 지시인 주석도 있다. 기계가 둘을 완벽히 가르지 못하고, 재생성을
    /// 걸면 모델이 표현만 바꿔 우회하며 재시도만 소모한다.
    ///
    /// 패턴을 좁게 유지한다. 배너가 잦으면 사람이 읽지 않는다.
    /// </summary>
    public static class OmissionCommentScanner
    {
        private const int MaxReported = 20;

        private static readonly Regex CommentLineRegex = new(
            @"^\s*(?:--|//)\s*(?<body>.+)$",
            RegexOptions.Compiled);

        private static readonly Regex[] OmissionPatterns =
        {
            new(@"나머지.*?(기술|적용|같은)", RegexOptions.Compiled),
            new(@"모두\s*기술", RegexOptions.Compiled),
            new(@"위\s.*동일", RegexOptions.Compiled),
        };

        // "유지하라/보존하라"는 생략 지시가 아니라 보존 지시다. 원본 로직을 지키라는
        // 요구를 결함으로 들면 배너의 변별력이 사라진다.
        private static readonly string[] PreservationMarkers = { "유지한다", "보존한다", "유지하십시오", "보존하십시오" };

        public static IReadOnlyList<string> Scan(string? planMarkdown)
        {
            if (string.IsNullOrWhiteSpace(planMarkdown))
            {
                return Array.Empty<string>();
            }

            var hits = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var insideFence = false;

            foreach (var line in MarkdownSectionLocator.SplitLines(planMarkdown))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    insideFence = !insideFence;
                    continue;
                }

                if (!insideFence)
                {
                    continue;
                }

                var comment = CommentLineRegex.Match(line);
                if (!comment.Success)
                {
                    continue;
                }

                var body = comment.Groups["body"].Value;

                if (PreservationMarkers.Any(marker => body.Contains(marker, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!OmissionPatterns.Any(pattern => pattern.IsMatch(body)))
                {
                    continue;
                }

                var trimmed = line.Trim();
                if (seen.Add(trimmed) && hits.Count < MaxReported)
                {
                    hits.Add(trimmed);
                }
            }

            return hits;
        }
    }
}
```

- [ ] **Step 4: 배너를 추가한다**

`VerificationBanner.cs`의 `UnverifiableSteps` 앞에:

```csharp
    /// <summary>
    /// 계획서 자신이 코드 자리에 주석을 세워 둔 곳을 알린다.
    ///
    /// 재생성을 걸지 않는다 - 지시 주석과 생략 주석을 기계가 완벽히 가르지 못해
    /// 모델이 표현만 바꿔 우회할 위험이 크다. 사람이 판단하도록 사실만 남긴다.
    /// </summary>
    public static string OmissionComments(IReadOnlyList<string> comments)
    {
        var lines = RenderBulletList(comments, "(주석이 기록되지 않았습니다.)");

        return "\n> [!WARNING]\n> **[생략 주석] 계획서의 코드 블록에 구현 대신 주석이 서 있는 곳이 있습니다.**"
            + " 지시서 규칙 7은 코딩 에이전트에게 이 형태를 금지하는데, 계획서가 그것을 본보기로 보이고"
            + " 있습니다. 아래 자리는 에이전트가 그대로 복사할 수 있으니 구현 전에 사람이 확인하십시오.\n"
            + lines
            + "\n\n";
    }
```

> `RenderBulletList`는 이 클래스의 기존 private 헬퍼다(`UnverifiableSteps`가 쓴다).

- [ ] **Step 5: 오케스트레이터에 부착한다**

`VerificationPipelineOrchestrator.cs`의 `var unverifiableSteps = byKind[StepDefectKind.Unverifiable].ToList();` **앞에** 넣는다. 가장 먼저 붙여 최종 문서에서 가장 아래에 오게 한다 — 이 배너가 그 자리의 결함 중 가장 가볍다.

```csharp
            // 배너는 나중에 붙을수록 위로 얹힌다. 생략 주석은 이 자리의 결함 중
            // 가장 가벼우므로 가장 먼저 붙여 맨 아래에서 읽히게 한다.
            //
            // 스캔 실패가 나머지 배너까지 막지 않도록 격리한다(AGENTS.md 범주 2).
            // 취소 필터는 달지 않는다 - Scan은 문자열 위의 동기 정규식이라 취소
            // 토큰을 넘기는 await를 감싸지 않는다.
            IReadOnlyList<string> omissionComments = Array.Empty<string>();
            try
            {
                omissionComments = OmissionCommentScanner.Scan(consolidatedPlan);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "생략 주석 스캔 중 오류가 발생했습니다. 배너 없이 진행합니다.");
            }

            if (omissionComments.Count > 0 && !string.IsNullOrEmpty(consolidatedPlan))
            {
                consolidatedPlan = VerificationBanner.OmissionComments(omissionComments) + consolidatedPlan;
            }
```

- [ ] **Step 6: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --nologo`
예상: 전부 PASS

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/OmissionCommentScanner.cs src/ReSet.Core/Services/VerificationBanner.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/OmissionCommentScannerTests.cs tests/ReSet.Core.Tests/VerificationBannerTests.cs
git commit -m "$(cat <<'EOF'
feat: surface the places where the plan itself omits code

지시서 규칙 7은 에이전트에게 자리표시자 주석을 금지하는데, 계획서가 코드 자리에
주석을 세워 그 형태를 본보기로 보이고 있었다. 차단하면 모델이 표현만 바꿔
우회하므로 배너로 남겨 사람이 판단하게 한다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: `StepLogicTests` 스캐폴드

**Files:**
- Modify: `src/ReSet.Core/Services/DataAccessPolicy.cs` (스텁 추가), `src/ReSet.Core/Services/MetadataExporter.cs:749-767`(C# 인라인 스텁 제거), `:802-817`(Java 인라인 스텁 제거)
- Modify: `src/ReSet.Core/Services/TaskFileComposer.cs:239-243`(Step 회차 완료 조건)
- Test: `tests/ReSet.Core.Tests/AgentContractStubTests.cs`, `tests/ReSet.Core.Tests/TaskFileComposerTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `public static string DataAccessPolicy.StepLogicTestStub(string targetLanguage)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`AgentContractStubTests`에 추가:

```csharp
        /// <summary>
        /// ArchitectureTestStub_ShouldNotBeEntirelyCommentedOut의 짝이다.
        /// 그 결함(빈 테스트를 방어로 착각)은 한 번 고쳐졌는데 StepLogicTests에만
        /// 적용되지 않아 본문이 주석 세 줄인 채로 남아 있었다 - 그런데 지시서 규칙 6은
        /// "제공된 자가 검증용 단위 테스트를 통과시키라"고 말한다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void StepLogicTestStub_ShouldFailUntilTheRoundWritesARealTest(string targetLanguage)
        {
            var stub = DataAccessPolicy.StepLogicTestStub(targetLanguage);

            var failMarker = targetLanguage == "Java" ? "fail(" : "Assert.Fail(";
            Assert.Contains(failMarker, stub);
            Assert.DoesNotContain("// Arrange\n\n            // Act", stub);
        }

        /// <summary>
        /// FileMappingService가 name.StartsWith(단계코드)로 회차 산출물을 찾는다.
        /// 테스트 파일을 S08LogicTests.cs로 만들면 Tasklet 없이도 이름 게이트가
        /// 통과해, 구현을 빼먹은 회차가 초록으로 보인다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void StepLogicTestStub_ShouldDemandASuffixedFileName(string targetLanguage)
        {
            var stub = DataAccessPolicy.StepLogicTestStub(targetLanguage);

            Assert.Contains("LogicTests_", stub);
        }
```

`TaskFileComposerTests`에 추가:

```csharp
        [Fact]
        public void Compose_ShouldRequireABehaviourTestInStepRounds()
        {
            var markdown = TaskFileComposer.Compose(StepInputs());

            Assert.Contains("동작 테스트", markdown);
            Assert.Contains("LogicTests_S01", markdown);
        }

        [Fact]
        public void Compose_ShouldNotRequireABehaviourTestInTheBootstrapRound()
        {
            // 회차 0에는 단계가 없다. 요구하면 부트스트랩이 부당하게 실패한다.
            var markdown = TaskFileComposer.Compose(BootstrapInputs(Array.Empty<string>()));

            Assert.DoesNotContain("동작 테스트", markdown);
        }
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~AgentContractStubTests`
예상: 컴파일 실패 — `StepLogicTestStub`이 없음

- [ ] **Step 3: 스텁을 구현한다**

`DataAccessPolicy.cs`에 추가:

```csharp
        private const string CSharpStepLogicTests = @"using Xunit;
using Moq;

namespace ReSet.Batch.Tests
{
    /// <summary>
    /// 이 회차가 구현한 단계의 동작을 검증하는 테스트를 여기에 쓰십시오.
    ///
    /// 파일명은 반드시 <c>LogicTests_&lt;단계코드&gt;.cs</c> 형태로 만드십시오
    /// (예: LogicTests_S08.cs). 단계 코드로 <b>시작하는</b> 이름(S08LogicTests.cs)은
    /// 쓰지 마십시오 - 검증기가 파일명 접두사로 그 회차의 산출물을 찾기 때문에,
    /// 테스트 파일이 Tasklet 자리를 차지해 구현을 빼먹어도 통과한 것처럼 보입니다.
    ///
    /// 최소 한 개: PreCheck 차단 경로 또는 RunBusinessSteps의 대표 분기.
    /// </summary>
    public class StepLogicTests
    {
        [Fact]
        public void Step_ShouldHaveAtLeastOneBehaviourTest()
        {
            Assert.Fail(
                ""이 회차의 단계 동작 테스트가 아직 없습니다. 이 Fact를 실제 테스트로 교체하십시오."");
        }
    }
}
";

        private const string JavaStepLogicTests = @"package com.reset.batch.tests;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.fail;

/**
 * 이 회차가 구현한 단계의 동작을 검증하는 테스트를 여기에 쓰십시오.
 *
 * 파일명은 반드시 LogicTests_<단계코드>.java 형태로 만드십시오(예: LogicTests_S08.java).
 * 단계 코드로 시작하는 이름(S08LogicTests.java)은 쓰지 마십시오 - 검증기가 파일명
 * 접두사로 그 회차의 산출물을 찾기 때문에, 테스트 파일이 Tasklet 자리를 차지해
 * 구현을 빼먹어도 통과한 것처럼 보입니다.
 *
 * 최소 한 개: preCheck 차단 경로 또는 runBusinessSteps의 대표 분기.
 */
public class StepLogicTests {
    @Test
    public void step_ShouldHaveAtLeastOneBehaviourTest() {
        fail(""이 회차의 단계 동작 테스트가 아직 없습니다. 이 테스트를 실제 테스트로 교체하십시오."");
    }
}
";

        /// <summary>
        /// 회차가 채워야 하는 단계 동작 테스트의 스캐폴드.
        ///
        /// 이전 스텁은 본문이 주석 세 줄이라 통과해도 아무것도 보장하지 않았는데,
        /// 지시서 규칙 6은 "제공된 자가 검증용 단위 테스트를 통과시키라"고 말한다 -
        /// 빈 테스트를 방어로 착각하는 구조였다. 미구현 상태가 실패로 드러나게 한다.
        /// </summary>
        public static string StepLogicTestStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaStepLogicTests
                : CSharpStepLogicTests;
```

`MetadataExporter.cs`의 두 인라인 스텁(`xUnitStub`, `jUnitStub`)을 제거하고 호출로 대체한다:

```csharp
                        await File.WriteAllTextAsync(
                            Path.Combine(agentTestsFolder, "StepLogicTests.cs"),
                            DataAccessPolicy.StepLogicTestStub(targetLanguage),
                            Encoding.UTF8);
```

Java 분기도 같은 형태(`StepLogicTests.java`, `Utf8NoBom`)로 바꾼다. Moq/Mockito import가 스텁에서 빠졌으므로 `using Moq;`만 남긴 위 문자열을 그대로 쓴다 — 회차가 목을 쓸 때 필요하고, 사용하지 않아도 C#은 경고 없이 컴파일된다. Java 쪽은 미사용 import가 경고를 낼 수 있어 Mockito import를 넣지 않았다.

- [ ] **Step 4: Step 회차 완료 조건을 더한다**

`TaskFileComposer.AppendStep`의 완료 조건에 한 줄 추가:

```csharp
            sb.AppendLine("## 완료 조건");
            sb.AppendLine();
            sb.AppendLine("- 빌드가 성공한다.");
            sb.AppendLine("- 이 단계의 조건절·집계식·오류 코드가 명세서와 축약 없이 일치한다.");
            sb.AppendLine(
                $"- 이 단계의 **동작 테스트**가 최소 한 개 통과한다. `tests/`의 스캐폴드를 " +
                $"`LogicTests_{SanitizeStepCode(inputs.StepCode)}{TestFileExtension(inputs.TargetLanguage)}`로 " +
                "복사해 채우십시오. 파일명이 단계 코드로 **시작하면** 검증기가 그 파일을 이 회차의 " +
                "구현 산출물로 오인하므로 반드시 접미사 형태를 쓰십시오.");
            sb.AppendLine();
```

같은 클래스에 헬퍼를 추가한다:

```csharp
        private static string TestFileExtension(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase) ? ".java" : ".cs";
```

- [ ] **Step 5: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --nologo`
예상: 전부 PASS

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/DataAccessPolicy.cs src/ReSet.Core/Services/MetadataExporter.cs src/ReSet.Core/Services/TaskFileComposer.cs tests/ReSet.Core.Tests/AgentContractStubTests.cs tests/ReSet.Core.Tests/TaskFileComposerTests.cs
git commit -m "$(cat <<'EOF'
fix: stop shipping an empty test that counts as self-verification

StepLogicTests는 본문이 주석 세 줄인데 지시서 규칙 6은 그것을 "통과시키라"고
말했다. 빈 테스트를 방어로 착각하는 구조다. 미구현 상태가 실패로 드러나는
스캐폴드로 바꾸고 Step 회차 완료 조건에 동작 테스트를 넣는다.

회차별 테스트 파일명은 접미사 형태를 지시한다. 단계 코드로 시작하면
FileMappingService가 그 파일을 회차 산출물로 오인해, 구현을 빼먹은 회차가
이름 게이트를 통과한다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: 아키텍처 테스트의 스캔 범위와 조립 회차 전용 검사

**Files:**
- Modify: `src/ReSet.Core/Services/DataAccessPolicy.cs:104-273`(`CSharpArchitectureTests`), `:275-...`(`JavaArchitectureTests`), 스텁 추가
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs`(tests 폴더 쓰기부)
- Modify: `src/ReSet.Core/Services/TaskFileComposer.cs:246-283`(`AppendAssembly`)
- Test: `tests/ReSet.Core.Tests/AgentContractStubTests.cs`, `tests/ReSet.Core.Tests/TaskFileComposerTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `public static string DataAccessPolicy.AssemblyCompletenessTestStub(string targetLanguage)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`AgentContractStubTests`에 추가:

```csharp
        /// <summary>
        /// 회차 0이 지시받은 헥사고날 구조를 다중 프로젝트로 만들면 Tasklet과 Domain
        /// 타입이 코어와 다른 어셈블리에 놓인다. 단일 어셈블리만 스캔하면 규칙 1·2·3·4가
        /// 대상 0건으로 조용히 통과한다 - 아키텍처 지시와 검사 방식이 서로를 무력화한다.
        /// </summary>
        [Fact]
        public void ArchitectureTestStub_ShouldScanEveryBatchAssembly_ForCSharp()
        {
            var stub = DataAccessPolicy.ArchitectureTestStub("C#");

            Assert.DoesNotContain("private static Assembly Target =>", stub);
            Assert.Contains("GetReferencedAssemblies", stub);
            Assert.Contains("ReSet.Batch", stub);
        }

        /// <summary>
        /// 0건 판정은 조립 회차에서만 켠다. 회차 0에는 Tasklet이 0개인 것이 정상이다.
        /// 스텁은 자신이 몇 회차에 놓이는지 알 수 없으므로 파일을 나누고, 배치 지시를
        /// 조립 회차에만 둔다 - 배치 지시가 곧 활성화 스위치다.
        /// </summary>
        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        public void AssemblyCompletenessTestStub_ShouldFailWhenNoTaskletExists(string targetLanguage)
        {
            var stub = DataAccessPolicy.AssemblyCompletenessTestStub(targetLanguage);

            Assert.Contains("AbstractSettleTasklet", stub);
            // 실패 메시지가 "왜 0건이 위험한가"를 말해야 한다 - 개수만 세고 끝나면
            // 읽는 사람이 대상 0건 통과라는 함정을 모른 채 넘어간다.
            Assert.Contains("Tasklet이 0개입니다", stub);
            Assert.Contains("대상 0건으로 통과", stub);
            // 회차 0의 아키텍처 테스트와 다른 파일이어야 활성화 스위치가 성립한다.
            Assert.Contains("AssemblyCompletenessTests", stub);
        }
```

`TaskFileComposerTests`에 추가:

```csharp
        [Fact]
        public void Compose_ShouldPlaceTheCompletenessTestOnlyInTheAssemblyRound()
        {
            var assembly = TaskFileComposer.Compose(new TaskFileInputs(
                Kind: StageKind.Assembly,
                JobName: "TestJob",
                TargetLanguage: "C#",
                StepCode: null,
                StepName: null,
                StepRelativePath: null,
                SpecRelativePath: null,
                Dependencies: Array.Empty<IndexEntry>(),
                HasStepContract: true,
                HasVerification: true,
                FailedStepCodes: Array.Empty<string>(),
                SinglePlanRelativePath: null,
                InfraObjects: Array.Empty<string>()));

            Assert.Contains("AssemblyCompletenessTests", assembly);

            // 회차 0에 새면 Tasklet이 0개인 부트스트랩이 부당하게 실패한다.
            var bootstrap = TaskFileComposer.Compose(BootstrapInputs(Array.Empty<string>()));
            Assert.DoesNotContain("AssemblyCompletenessTests", bootstrap);
        }
```

- [ ] **Step 2: 실패를 확인한다**

실행: `dotnet test ReSet.slnx --nologo --filter FullyQualifiedName~AgentContractStubTests`
예상: 컴파일 실패 — `AssemblyCompletenessTestStub`이 없음

- [ ] **Step 3: C# 아키텍처 테스트의 스캔 범위를 넓힌다**

`DataAccessPolicy.CSharpArchitectureTests` 안의 `private static Assembly Target => ...`(생성 산출물 기준 26행)을 다음으로 바꾼다. **이 코드는 verbatim 문자열 안에 들어가므로 모든 `"`를 `""`로 이스케이프할 것.**

```csharp
        // 코어 한 어셈블리만 보면, 회차 0이 지시받은 헥사고날 구조를 다중 프로젝트로
        // 만든 순간 Tasklet과 Domain 타입이 시야에서 사라져 규칙들이 대상 0건으로
        // 조용히 통과한다. 테스트 어셈블리가 참조하는 ReSet.Batch.* 를 전부 훑는다.
        private static IReadOnlyList<Assembly> Targets
        {
            get
            {
                foreach (var reference in Assembly.GetExecutingAssembly().GetReferencedAssemblies())
                {
                    if ((reference.Name ?? string.Empty).StartsWith("ReSet.Batch", StringComparison.Ordinal))
                    {
                        // 아직 로드되지 않은 참조는 AppDomain에 나타나지 않는다.
                        try { Assembly.Load(reference); } catch { /* 로드 실패는 아래 필터가 흡수한다 */ }
                    }
                }

                return AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .Where(a => (a.GetName().Name ?? string.Empty).StartsWith("ReSet.Batch", StringComparison.Ordinal))
                    .Distinct()
                    .ToList();
            }
        }

        private static IEnumerable<Type> TargetTypes =>
            Targets.SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 일부 타입이 로드되지 않아도 나머지는 검사한다.
                    return ex.Types.Where(t => t != null)!;
                }
            });
```

리플렉션을 쓰는 두 규칙(`EverySettleStep_MustInherit_AbstractSettleTasklet`, `EveryTasklet_MustDeclare_StepNameAndSourceProcName`, `SettleContext_MustExposeInjectableConnectionFactories`의 구현체 탐색)에서 `Target.GetTypes()`를 `TargetTypes`로 바꾼다.

NetArchTest를 쓰는 두 규칙은 어셈블리별로 돌려 결과를 모은다:

```csharp
        [Fact]
        public void Tasklets_MustNotCreate_TheirOwnConnection()
        {
            // 새 커넥션을 만들면 검증기의 Rollback 격리가 깨져 정합성 대조가 오염된다.
            var offenders = new List<string>();

            foreach (var assembly in Targets)
            {
                var result = Types.InAssembly(assembly)
                    .That().Inherit(typeof(ReSet.Batch.Core.AbstractSettleTasklet))
                    .ShouldNot().HaveDependencyOn("Microsoft.Data.SqlClient.SqlConnection")
                    .GetResult();

                if (!result.IsSuccessful)
                {
                    offenders.AddRange(result.FailingTypeNames ?? Array.Empty<string>());
                }
            }

            Assert.True(offenders.Count == 0,
                "SqlConnection을 직접 생성한 Tasklet: " + string.Join(", ", offenders));
        }
```

`Domain_MustNotDependOn_Infrastructure`도 같은 루프 형태로 바꾼다.

`[Fact]`를 더하거나 빼지 않는다 — 스캔 방식만 바꾼다. 따라서 기존 `ArchitectureTestStub_ShouldExposeTheSameRuleCount_ForBothLanguages`(양 언어 5개)는 **그대로 통과해야 한다.** 그 테스트가 깨지면 규칙을 의도치 않게 늘렸거나 지운 것이므로 되돌린다. `AssemblyCompletenessTests`는 별도 파일이라 이 개수에 들어가지 않는다.

Java 쪽(`JavaArchitectureTests`)은 이미 `new ClassFileImporter().importPackages("com.reset.batch")`로 패키지 전체를 훑으므로 어셈블리 문제가 없다. 변경하지 않는다. 이 사실을 `JavaArchitectureTests` 상단 주석에 한 줄로 남긴다:

```java
 * (C#과 달리 여기는 어셈블리 경계 문제가 없다 - importPackages가 com.reset.batch 전체를
 *  훑으므로 Tasklet이 어느 모듈에 있든 시야에 들어온다.)
```

- [ ] **Step 4: 조립 회차 전용 스텁을 만든다**

`DataAccessPolicy.cs`에 추가(verbatim 이스케이프 주의):

```csharp
        private const string CSharpAssemblyCompletenessTests = @"using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ReSet.Batch.Tests.Architecture
{
    /// <summary>
    /// 조립 회차 전용. 이 파일은 조립 회차의 지시서만 배치를 요구한다.
    ///
    /// 아키텍처 규칙들은 대상이 0건이면 통과한다 - 회차 0에는 Tasklet이 없으므로
    /// 그것이 정상이다. 그래서 ""하나도 없다""를 실패로 보는 판정은 모든 단계가
    /// 구현된 뒤에만 켤 수 있고, 스텁은 자신이 몇 회차에 놓이는지 알 수 없다.
    /// 파일을 나누고 배치 지시를 조립 회차에만 두어 그 스위치를 만든다.
    /// </summary>
    public class AssemblyCompletenessTests
    {
        [Fact]
        public void Assembly_MustContainAtLeastOneTasklet()
        {
            var taskletCount = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Where(a => (a.GetName().Name ?? string.Empty).StartsWith(""ReSet.Batch"", StringComparison.Ordinal))
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
                })
                .Count(t => t != null
                    && t.IsClass
                    && !t.IsAbstract
                    && typeof(ReSet.Batch.Core.AbstractSettleTasklet).IsAssignableFrom(t));

            Assert.True(taskletCount > 0,
                ""Tasklet이 0개입니다. 아키텍처 규칙들이 대상 0건으로 통과했을 뿐 아무것도 검사하지 않았습니다."");
        }
    }
}
";

        private const string JavaAssemblyCompletenessTests = @"package com.reset.batch.tests.architecture;

import com.tngtech.archunit.core.domain.JavaClass;
import com.tngtech.archunit.core.domain.JavaClasses;
import com.tngtech.archunit.core.domain.JavaModifier;
import com.tngtech.archunit.core.importer.ClassFileImporter;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * 조립 회차 전용. 이 파일은 조립 회차의 지시서만 배치를 요구한다.
 *
 * 아키텍처 규칙들은 대상이 0건이면 통과한다 - 부트스트랩 회차에는 Tasklet이 없으므로
 * 그것이 정상이다. ""하나도 없다""를 실패로 보는 판정은 모든 단계가 구현된 뒤에만
 * 켤 수 있다.
 */
class AssemblyCompletenessTests {

    private final JavaClasses classes = new ClassFileImporter().importPackages(""com.reset.batch"");

    @Test
    void assemblyMustContainAtLeastOneTasklet() {
        long taskletCount = classes.stream()
            .filter(c -> !c.getModifiers().contains(JavaModifier.ABSTRACT))
            .filter(c -> c.isAssignableTo(""com.reset.batch.core.AbstractSettleTasklet""))
            .count();

        assertTrue(taskletCount > 0,
            ""Tasklet이 0개입니다. 아키텍처 규칙들이 대상 0건으로 통과했을 뿐 아무것도 검사하지 않았습니다."");
    }
}
";

        /// <summary>
        /// 조립 회차에서만 켜지는 0건 판정. 배치 지시가 활성화 스위치다.
        /// </summary>
        public static string AssemblyCompletenessTestStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaAssemblyCompletenessTests
                : CSharpAssemblyCompletenessTests;
```

`MetadataExporter.cs`의 tests 쓰기 블록에 파일 하나를 더한다(C#·Java 각각):

```csharp
                        await File.WriteAllTextAsync(
                            Path.Combine(agentTestsFolder, "AssemblyCompletenessTests.cs"),
                            DataAccessPolicy.AssemblyCompletenessTestStub(targetLanguage),
                            Encoding.UTF8);
```

- [ ] **Step 5: 조립 회차 지시서에 배치를 지시한다**

`TaskFileComposer.AppendAssembly`의 `sb.AppendLine("- 전체 빌드와 아키텍처 테스트 통과");` 뒤에:

```csharp
            sb.AppendLine(AssemblyCompletenessPlacementLine(inputs.TargetLanguage));
```

메서드를 추가한다:

```csharp
        /// <summary>
        /// 0건 판정을 켜는 스위치. 회차 0에는 Tasklet이 없어 이 검사가 부당하게
        /// 실패하므로, 배치 지시를 조립 회차에만 둔다.
        /// </summary>
        private static string AssemblyCompletenessPlacementLine(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? "- `tests/AssemblyCompletenessTests.java`를 프로젝트의 `src/test/java/com/reset/batch/tests/architecture/` 아래로 배치하고 통과시킬 것 (Tasklet이 하나도 없으면 실패하는 검사입니다 — 이 회차에서만 켭니다)"
                : "- `tests/AssemblyCompletenessTests.cs`를 프로젝트에 배치하고 통과시킬 것 (Tasklet이 하나도 없으면 실패하는 검사입니다 — 이 회차에서만 켭니다)";
```

- [ ] **Step 6: 통과를 확인한다**

실행: `dotnet test ReSet.slnx --nologo`
예상: 전부 PASS

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/DataAccessPolicy.cs src/ReSet.Core/Services/MetadataExporter.cs src/ReSet.Core/Services/TaskFileComposer.cs tests/ReSet.Core.Tests/AgentContractStubTests.cs tests/ReSet.Core.Tests/TaskFileComposerTests.cs
git commit -m "$(cat <<'EOF'
fix: keep the architecture gate from passing on an empty type set

C# 아키텍처 테스트가 코어 한 어셈블리만 스캔해, 회차 0이 지시받은 헥사고날
구조를 다중 프로젝트로 만들면 규칙 넷이 대상 0건으로 조용히 통과했다. 참조된
ReSet.Batch.* 를 전부 훑는다.

"하나도 없다"를 실패로 보는 판정은 조립 회차에만 켠다. 스텁은 자신이 몇 회차에
놓이는지 알 수 없으므로 파일을 나누고 배치 지시를 스위치로 쓴다.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## 마무리 확인 (모든 Task 이후)

- [ ] **전체 테스트**

실행: `dotnet test ReSet.slnx --nologo`
예상: 1451 + 신규 약 40개 전부 통과, 0 실패

- [ ] **신규 Job 생성으로 산출물을 눈으로 확인**

새 Job을 한 번 돌린 뒤 `output/Jobs/<새Job>/agent/`에서 확인한다.

| 확인 대상 | 기대 |
| :--- | :--- |
| `src/AbstractSettleTasklet.cs` | `SettleContext`에 `RunId`·`InputHash`·`SourceSnapshotId` |
| `MigrationInstructions.md` | 규칙 9 뒤에 규칙 10, "스텁이 이깁니다" |
| `task-00-bootstrap.md` | 인프라 객체 실명 목록(그 Job이 batch 객체를 쓴다면) |
| `task-99-assembly.md` | `AssemblyCompletenessTests` 배치 지시 |
| `task-01-*.md` | 동작 테스트 완료 조건과 `LogicTests_S01` 파일명 |
| `tests/StepLogicTests.cs` | `Assert.Fail`이 있는 스캐폴드 |

- [ ] **`POQSettleProc9` 재생성으로 ③이 실제로 고쳐지는지 확인**

재생성 후 `agent/steps/S17.md`에서 `dbo.TSettleSummary`를 찾는다. 남아 있으면 추출 범위(백틱·펜스 한정)가 좁았다는 뜻이므로 `MechanicalValidator.ExtractQuotedIdentifiers`의 범위를 재검토한다. 이름만 바뀌고 다른 유령이 생겼다면 결함 메시지의 구체성을 재검토한다.

- [ ] **문서 동기화**

`reset-doc-sync` 스킬로 `AGENTS.md`·`docs/architecture.md`를 갱신한다. 새로 생긴 것: `BatchInfraObjectCollector`, `OmissionCommentScanner`, `KnownTableWiringPolicyScanner`, `DataAccessPolicy`로 옮겨온 세 스텁, `ValidateBatchStep`의 3인자 계약.
