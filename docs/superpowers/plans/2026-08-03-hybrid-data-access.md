# 하이브리드 데이터 액세스 경계 규칙 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 외부 코딩 에이전트가 생성하는 통합 배치 코드에서 SQL과 ORM의 경계를 허용 목록법으로 규정하고, 위반이 Validator L2에서 결함으로 판정되어 자가 수정 루프를 재기동시킨다.

**Architecture:** 경계 규칙 문구를 `DataAccessPolicy` 한 클래스가 단독 소유하고, 지시서 생성부(`MetadataExporter`)와 L2 검증 프롬프트(`ValidatorAiService`)가 같은 문장을 참조한다. 계획서 의사코드는 T-SQL 정합성 기준선으로 보존하며, 채점은 생성 코드 단계에서만 한다. 설정 키를 도입하지 않으므로 기존 메서드 시그니처와 호출부는 변하지 않는다.

**Tech Stack:** .NET 10, xUnit, NSubstitute, Serilog

설계 문서: `docs/superpowers/specs/2026-08-03-hybrid-data-access-design.md`

## Global Constraints

- 대상 프레임워크는 .NET 10.0이며 빌드·테스트 명령은 `dotnet build`, `dotnet test`다.
- 에이전트에게 전달되는 산출물 문구는 한국어로 작성한다. `AiService`의 분석·계획 시스템 프롬프트는 영문 원칙이지만 이 계획은 그 파일의 프롬프트 본문을 재작성하지 않는다.
- 경계 규칙 문구는 `DataAccessPolicy`만 만든다. 다른 클래스에서 같은 규칙을 다시 문장으로 쓰지 않는다.
- 새로운 `catch` 블록을 도입하지 않는다. 도입하면 `CancellationPolicyTests`가 `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt` 기준선과 대조해 실패한다.
- C# verbatim 문자열(`@"..."`) 안에서 큰따옴표는 `""`로, 보간 문자열(`$"..."`, `$@"..."`) 안에서 중괄호는 `{{`/`}}`로 이스케이프한다. 이 계획의 새 문구는 큰따옴표를 포함하지 않도록 작성되어 있으므로 그대로 옮기면 문제가 없다.
- 작업 브랜치는 `feature/hybrid-data-access`다. AGENTS.md 범주 8에 따라 `superpowers:using-git-worktrees`로 격리 작업 공간을 만들어 구현한다.
- Task 1 → Task 2·3 순서 의존이 있다. Task 2와 3은 서로 독립이며, Task 4는 Task 3 이후여야 한다.

---

### Task 1: DataAccessPolicy 신설

경계 규칙 문구의 단독 소유자를 만든다. 의존성이 없는 순수 문자열 생성기이므로 소비자보다 먼저 만들고 테스트로 고정한다.

**Files:**
- Create: `src/ReSet.Core/Services/DataAccessPolicy.cs`
- Test: `tests/ReSet.Core.Tests/DataAccessPolicyTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public static class ReSet.Core.Services.DataAccessPolicy`
  - `public static string InstructionRules(string targetLanguage)` — 지시서 5장용 마크다운 블록
  - `public static string VerificationCriteria { get; }` — L2 프롬프트용 판정 기준 (문자열이 `5.`로 시작한다)
  - `public static string TaskletOrmComment { get; }` — 스텁 삽입용 주석 (모든 줄이 `//`로 시작하고 8칸 들여쓰기됨)

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/DataAccessPolicyTests.cs`를 만든다.

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class DataAccessPolicyTests
    {
        [Fact]
        public void InstructionRules_ForCSharp_NamesDapperAndEfCoreOnly()
        {
            var rules = DataAccessPolicy.InstructionRules("C#");

            Assert.Contains("Dapper", rules);
            Assert.Contains("EF Core", rules);
            Assert.DoesNotContain("MyBatis", rules);
            Assert.DoesNotContain("Spring Data JPA", rules);
        }

        [Fact]
        public void InstructionRules_ForJava_NamesMyBatisAndJpaOnly()
        {
            var rules = DataAccessPolicy.InstructionRules("Java");

            Assert.Contains("MyBatis", rules);
            Assert.Contains("Spring Data JPA", rules);
            Assert.DoesNotContain("Dapper", rules);
            Assert.DoesNotContain("EF Core", rules);
        }

        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        [InlineData("Kotlin")]
        [InlineData("")]
        public void InstructionRules_AlwaysCarriesAllowlistAndStandingClauses(string targetLanguage)
        {
            var rules = DataAccessPolicy.InstructionRules(targetLanguage);

            // 허용 목록 4항목
            Assert.Contains("엔티티/DTO 타입 정의", rules);
            Assert.Contains("마스터·공통코드", rules);
            Assert.Contains("체크포인트 상태 읽기/쓰기", rules);
            Assert.Contains("배치 실행 이력·로그의 단건 기록", rules);

            // SQL 필수 열거
            Assert.Contains("청킹", rules);
            Assert.Contains("Shadow 테이블", rules);
            Assert.Contains("SET TRANSACTION ISOLATION LEVEL SNAPSHOT", rules);

            // 항상 적용 조항 4개
            Assert.Contains("새 트랜잭션을 만들지 마십시오", rules);
            Assert.Contains("파라미터 바인딩을 사용하십시오", rules);
            Assert.Contains("지연 로딩", rules);
            Assert.Contains("상한을 예측할 수 없으면", rules);
        }

        [Fact]
        public void InstructionRules_ForUnknownLanguage_OmitsOnlyTheStackTable()
        {
            var rules = DataAccessPolicy.InstructionRules("Kotlin");

            Assert.DoesNotContain("Dapper", rules);
            Assert.DoesNotContain("MyBatis", rules);
            Assert.Contains("ORM은 아래 4가지 용도에만 허용합니다", rules);
        }

        [Fact]
        public void VerificationCriteria_DemandsPartialOnViolation()
        {
            var criteria = DataAccessPolicy.VerificationCriteria;

            Assert.StartsWith("5.", criteria);
            Assert.Contains("PARTIAL", criteria);
            Assert.Contains("DataAccessBoundaryGap", criteria);
        }

        [Fact]
        public void TaskletOrmComment_IsCommentOnlyAndShowsTransactionEnlistment()
        {
            var comment = DataAccessPolicy.TaskletOrmComment;

            Assert.Contains("UseTransaction", comment);
            foreach (var line in comment.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                Assert.StartsWith("//", line.Trim());
            }
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter DataAccessPolicyTests`
Expected: 컴파일 실패. `DataAccessPolicy` 형식을 찾을 수 없다는 오류(CS0103 또는 CS0234).

- [ ] **Step 3: DataAccessPolicy 구현**

`src/ReSet.Core/Services/DataAccessPolicy.cs`를 만든다.

```csharp
using System;

namespace ReSet.Core.Services
{
    /// <summary>
    /// SQL과 ORM의 경계 규칙 문구를 단독 소유한다. 지시서(MetadataExporter)와 L2 검증
    /// 프롬프트(ValidatorAiService)가 같은 규칙을 말해야 하므로, 다른 곳에서 이 문구를
    /// 새로 만들지 말고 이 클래스를 참조하십시오.
    /// </summary>
    public static class DataAccessPolicy
    {
        private const string CommonRules = @"### 데이터 액세스 경계 규칙 (Hybrid Data Access Boundary)

ORM은 아래 4가지 용도에만 허용합니다. 목록에 없는 모든 데이터 액세스는 파라미터 바인딩된 SQL로 작성하십시오. 판단이 애매하면 SQL을 택하십시오.

1. 엔티티/DTO 타입 정의 및 조회 결과 객체 매핑
2. 마스터·공통코드 등 참조 데이터의 단건/소량 조회
3. 체크포인트 상태 읽기/쓰기 (`ICheckpointRepository` 구현)
4. 배치 실행 이력·로그의 단건 기록

**다음은 반드시 SQL로 작성하십시오.**

* 정산 대상 테이블의 대량 SELECT/INSERT/UPDATE/DELETE
* 집계(`GROUP BY`), `UNION`/`UNION ALL`, 다중 테이블 JOIN
* 청킹 `WHILE` 루프와 그 내부 DML, 루프별 `BEGIN TRAN`/`COMMIT TRAN` 경계
* Shadow 테이블 생성·스왑·복원, 보상 트랜잭션 `DELETE`
* 세션 제어 (`SET XACT_ABORT ON`, `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`)
* 크로스 DB 3부 식별자 참조 쿼리

**아래 4개 조항은 경계와 무관하게 항상 적용됩니다.**

1. ORM은 반드시 `RunBusinessSteps`가 받은 `conn`/`tran`에 참여시키십시오. 새 커넥션이나 새 트랜잭션을 만들지 마십시오. 이를 어기면 검증기의 Rollback 격리가 깨져 정합성 대조 결과가 오염됩니다.
2. ORM 경로에서도 SQL 문자열 연결을 금지하고 파라미터 바인딩을 사용하십시오.
3. 지연 로딩(lazy loading)을 금지합니다. 배치에서 N+1을 유발하므로 명시적 조회만 사용하십시오.
4. 허용 목록 항목이라도 반환 행 수의 상한을 예측할 수 없으면 SQL로 작성하십시오.
";

        private const string CSharpStack = @"
| 경로 | 기술 |
| --- | --- |
| SQL | Dapper (ADO.NET) |
| ORM | EF Core |
";

        private const string JavaStack = @"
| 경로 | 기술 |
| --- | --- |
| SQL | MyBatis |
| ORM | Spring Data JPA |
";

        /// <summary>
        /// 지시서 5장에 실릴 경계 규칙 마크다운 블록.
        /// 알 수 없는 타겟 언어에는 스택 표만 생략하고 공통 규칙은 그대로 낸다.
        /// 언어를 모른다는 이유로 규칙 전체가 사라지면 에이전트가 규칙 없이 코드를 쓴다.
        /// </summary>
        public static string InstructionRules(string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(targetLanguage))
            {
                return CommonRules;
            }

            if (targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase))
            {
                return CommonRules + CSharpStack;
            }

            if (targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase))
            {
                return CommonRules + JavaStack;
            }

            return CommonRules;
        }

        /// <summary>
        /// L2 Gap 분석 프롬프트의 5번 검증 항목. 지시서 문구를 판정형으로 다시 쓴 것이며,
        /// 위반 시 OverallStatus를 MATCH로 두지 못하게 하는 지시가 핵심이다.
        /// CodeVerificationOrchestrator가 OverallStatus만으로 L2Passed를 정하기 때문이다.
        /// </summary>
        public static string VerificationCriteria => @"5. 데이터 액세스 경계 준수: 다음 위반이 있는지 확인하십시오.
   - ORM(EF Core, JPA/Hibernate 등)이 허용 목록 4가지(① 엔티티/DTO 정의 및 결과 매핑, ② 마스터·공통코드 단건/소량 조회, ③ 체크포인트 상태 읽기/쓰기, ④ 배치 실행 이력·로그 단건 기록) 밖에서 사용되었는가?
   - 정산 대상 대량 DML, 집계/UNION/다중 JOIN, 청킹 루프 내부 DML, Shadow 처리, 세션 제어가 SQL이 아니라 ORM으로 구현되었는가?
   - ORM이 전달받은 커넥션/트랜잭션에 참여하지 않고 새 커넥션이나 새 트랜잭션을 생성하는가?
   - SQL 문자열 연결로 쿼리를 조립하거나 파라미터 바인딩을 생략했는가?
   - 지연 로딩(lazy loading)에 의존하는가?
   위반이 하나라도 있으면 OverallStatus를 MATCH로 두지 말고 최소 PARTIAL로 판정하고, 위반 내용을 DataAccessBoundaryGap에 기술하십시오.
";

        /// <summary>
        /// AbstractSettleTasklet 스텁에 삽입할 주석. 스텁이 System.Data만 참조하는 상태를
        /// 유지해야 하므로 실행 코드가 아닌 주석으로만 패턴을 보여준다.
        /// 8칸 들여쓰기는 스텁의 멤버 들여쓰기와 맞춘 것이다.
        /// </summary>
        public static string TaskletOrmComment => @"        // [데이터 액세스 경계] ORM(EF Core)은 MigrationInstructions.md 5장의 허용 목록에 한해 사용한다.
        // 사용할 경우 반드시 아래 conn/tran에 참여시켜야 하며, 새 커넥션이나 새 트랜잭션을 만들면
        // 검증기의 Rollback 격리(CSharpReflectionRunner)가 깨져 정합성 대조 결과가 오염된다.
        //   var options = new DbContextOptionsBuilder<XxxContext>().UseSqlServer((SqlConnection)conn).Options;
        //   using var db = new XxxContext(options);
        //   db.Database.UseTransaction((SqlTransaction)tran);
        // 정산 대상 대량 DML, 집계, 청킹 루프, Shadow 처리, 세션 제어는 파라미터 바인딩 SQL로 작성한다.";
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter DataAccessPolicyTests`
Expected: PASS (9건 — Fact 4건 + Theory 4케이스 + Fact 1건)

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DataAccessPolicy.cs tests/ReSet.Core.Tests/DataAccessPolicyTests.cs
git commit -m "feat: own the hybrid data access boundary rules in one class"
```

---

### Task 2: 지시서와 스텁에 경계 규칙 반영

`MetadataExporter`가 만드는 지시서·체크리스트·스텁이 경계 규칙을 실어 보내게 한다. 시그니처는 바꾸지 않으므로 호출부 2곳(`Program.cs:819,1327`)과 기존 테스트 6곳은 손대지 않는다.

**Files:**
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs:528`, `:532`, `:537-569`, `:588`, `:590`, `:626`, `:705`
- Test: `tests/ReSet.Core.Tests/MetadataExporterTests.cs`

**Interfaces:**
- Consumes: `DataAccessPolicy.InstructionRules(string)`, `DataAccessPolicy.TaskletOrmComment` (Task 1)
- Produces: 없음 (파일 산출물만 변한다)

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/MetadataExporterTests.cs`의 클래스 안에 아래 테스트를 추가한다. 기존 테스트는 수정하지 않는다.

```csharp
        [Fact]
        public async Task ExportConsolidatedMigrationInstructionsAsync_CarriesTheDataAccessBoundaryRules()
        {
            var testOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "test_output_exporter_boundary");
            if (Directory.Exists(testOutputDir))
            {
                Directory.Delete(testOutputDir, true);
            }

            try
            {
                var spDefs = new System.Collections.Generic.List<SpDefinition>
                {
                    new SpDefinition
                    {
                        Schema = "dbo",
                        Name = "USP_Sp1",
                        DdlText = "CREATE PROCEDURE dbo.USP_Sp1 AS SELECT 1;"
                    }
                };

                await new MetadataExporter().ExportConsolidatedMigrationInstructionsAsync(
                    spDefs,
                    "# Plan",
                    VerificationOutcome.Passed,
                    "BoundaryJob",
                    testOutputDir,
                    "C#",
                    new OutputPathResolver("TestDB", testOutputDir));

                var instructions = await File.ReadAllTextAsync(
                    Path.Combine(testOutputDir, "agent", "MigrationInstructions.md"));

                // 규칙 문구는 DataAccessPolicy가 단독 소유한다. 지시서는 그것을 그대로 싣는다.
                Assert.Contains(DataAccessPolicy.InstructionRules("C#"), instructions);
                // 지침 7번의 placeholder 금지는 경계 규칙 도입 후에도 유지되어야 한다.
                Assert.Contains("Placeholder", instructions);
                Assert.Contains("허용 목록", instructions);
                // 배치 호스팅과 멀티 DB 설정 안내는 그대로 남는다.
                Assert.Contains("Worker Service", instructions);
                Assert.Contains("ConnectionStrings", instructions);

                var todo = await File.ReadAllTextAsync(
                    Path.Combine(testOutputDir, "agent", "todo.md"));
                Assert.Contains("EF Core", todo);
                Assert.Contains("경계 규칙", todo);

                var stub = await File.ReadAllTextAsync(
                    Path.Combine(testOutputDir, "agent", "src", "AbstractSettleTasklet.cs"));
                Assert.Contains("UseTransaction", stub);
                Assert.DoesNotContain("[[ORM_BOUNDARY]]", stub);
            }
            finally
            {
                if (Directory.Exists(testOutputDir))
                {
                    Directory.Delete(testOutputDir, true);
                }
            }
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter ExportConsolidatedMigrationInstructionsAsync_CarriesTheDataAccessBoundaryRules`
Expected: FAIL. `Assert.Contains` 실패 — 지시서에 경계 규칙 블록이 없다.

- [ ] **Step 3: 지침 3번과 7번 문구 교체**

`MetadataExporter.cs:528`의 다음 줄을

```csharp
                sb.AppendLine("3. 데이터 엑세스 계층(Repository/DAO 등)은 타겟 언어 및 프레임워크의 권장 패턴을 따를 일.");
```

아래로 바꾼다.

```csharp
                sb.AppendLine("3. 데이터 엑세스 계층(Repository/DAO 등)은 5장의 데이터 액세스 경계 규칙을 준수하며 타겟 언어 및 프레임워크의 권장 패턴을 따를 일.");
```

`MetadataExporter.cs:532`의 다음 줄을

```csharp
                sb.AppendLine("7. [중요] 어떠한 경우에도 `// implementation omitted`, `// TODO`, `/* Build SQL */` 등의 주석으로 코드를 생략(Placeholder)하지 마십시오. 반드시 명세서에 있는 원본 DML(SELECT/INSERT/UPDATE/DELETE) 로직을 모두 프로그래밍 언어(C# 등)의 텍스트 쿼리로 풀어서 100% 완전하게 작성해야 합니다.");
```

아래로 바꾼다.

```csharp
                sb.AppendLine("7. [중요] 어떠한 경우에도 `// implementation omitted`, `// TODO`, `/* Build SQL */` 등의 주석으로 코드를 생략(Placeholder)하지 마십시오. 5장의 경계 규칙에 따라 SQL 경로로 분류된 DML은 명세서에 있는 원본 로직(조건절·집계식·에러 코드)을 축약 없이 파라미터 바인딩 SQL로 100% 완전하게 작성해야 하며, ORM은 5장의 허용 목록에 한해 사용해야 합니다.");
```

- [ ] **Step 4: 5장에 경계 규칙 블록 삽입**

`MetadataExporter.cs:537-569`의 5장 블록에서, 데이터 액세스 안내 한 줄을 정책 블록으로 대체하고 배치 호스팅·멀티 DB 안내는 남긴다. 헤더 다음 줄에 정책 블록을 넣는다.

```csharp
                sb.AppendLine("## 🛠️ 5. 기술 스택 및 인프라 설정 가이드 (Tech Stack & Configuration)");
                sb.AppendLine(DataAccessPolicy.InstructionRules(targetLanguage));
                if (targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("* **배치 호스팅 및 DI**: 배치 호스팅은 .NET 10 Worker Service 기반으로 작성하며, Microsoft.Extensions.DependencyInjection을 통해 의존성을 주입하십시오.");
                    sb.AppendLine("* **멀티 DB 커넥션 설정**: `appsettings.json` 내에 다음과 같은 `ConnectionStrings` 구조를 구성하고, `RetryableSqlExecutor`에서 분기 처리하여 주입받을 수 있도록 모델링하십시오.");
```

즉 기존 C# 분기의 첫 줄(`* **Data Access 및 프레임워크**: 데이터베이스 접근은 ADO.NET(또는 Dapper)을 사용하고, 배치 호스팅은 ...`)을 위의 `* **배치 호스팅 및 DI**: ...` 줄로 교체한다. 이어지는 `ConnectionStrings` JSON 예시 블록은 손대지 않는다.

Java 분기(`:555`)에서도 같은 방식으로 첫 줄을 교체한다.

```csharp
                    sb.AppendLine("* **배치 호스팅 및 DI**: 배치 호스팅은 Spring Batch (Spring Boot 기반)로 작성하며, 의존성 주입을 활용하십시오.");
```

이어지는 `application.yml` YAML 예시 블록은 손대지 않는다.

- [ ] **Step 5: 체크리스트(todo.md) 항목 교체**

`MetadataExporter.cs:588`의 다음 줄을

```csharp
                todoSb.AppendLine("- [ ] 0. 프로젝트 빌드 환경 구성 및 필수 패키지/라이브러리 설치 (예: Dapper, Moq, MyBatis, ArchUnit 등)");
```

아래로 바꾼다.

```csharp
                todoSb.AppendLine("- [ ] 0. 프로젝트 빌드 환경 구성 및 필수 패키지/라이브러리 설치 (C#: Dapper, EF Core, Moq, NetArchTest / Java: MyBatis, Spring Data JPA, ArchUnit)");
```

`MetadataExporter.cs:590`의 다음 줄을

```csharp
                todoSb.AppendLine("- [ ] 2. 설계서에 명시된 대상 테이블 DDL 파악 및 데이터 액세스(Repository/DAO/Adapter) 계층 구현");
```

아래로 바꾼다.

```csharp
                todoSb.AppendLine("- [ ] 2. 설계서에 명시된 대상 테이블 DDL 파악 및 데이터 액세스(Repository/DAO/Adapter) 계층 구현 (지시서 5장의 SQL/ORM 경계 규칙 준수)");
```

- [ ] **Step 6: 스텁에 경계 주석 삽입**

`MetadataExporter.cs:673-674`(스텁 verbatim 문자열 내부)의 `RunBusinessSteps` 선언 위에 토큰 줄을 넣는다. 스텁 문자열 안의 해당 부분이 다음처럼 되게 한다.

```csharp
        protected abstract StepResult PreCheck(IDbConnection conn, SettleContext context, ref int stateCode);
[[ORM_BOUNDARY]]
        protected abstract void RunBusinessSteps(IDbConnection conn, IDbTransaction tran, SettleContext context, ref int stateCode);
```

그리고 `MetadataExporter.cs:705`의 스텁 쓰기 호출에서 토큰을 치환한다. 기존 줄

```csharp
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "AbstractSettleTasklet.cs"), baseClassStub, Encoding.UTF8);
```

을 아래로 바꾼다.

```csharp
                        // 스텁은 System.Data만 참조하는 상태를 유지한다. ORM 패턴은 실행 코드가
                        // 아니라 주석으로만 넣어야 스텁이 특정 ORM 구현에 결합되지 않는다.
                        var stubWithBoundary = baseClassStub.Replace("[[ORM_BOUNDARY]]", DataAccessPolicy.TaskletOrmComment);
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "AbstractSettleTasklet.cs"), stubWithBoundary, Encoding.UTF8);
```

- [ ] **Step 7: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter MetadataExporterTests`
Expected: PASS. 신규 1건과 기존 `MetadataExporterTests` 전량이 통과해야 한다.

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/MetadataExporter.cs tests/ReSet.Core.Tests/MetadataExporterTests.cs
git commit -m "feat: carry the data access boundary rules into the agent instructions"
```

---

### Task 3: GapReport 경계 항목과 L2 프롬프트 판정 규칙

L2가 경계 위반을 판정하고, 그 판정이 `OverallStatus`를 통해 자가 수정 루프를 재기동시키게 한다.

`CodeVerificationOrchestrator.cs:151`이 `L2Passed = gapReport.OverallStatus == "MATCH"`로만 판정하므로, 필드 추가만으로는 위반이 기록되고도 통과한다. 프롬프트의 `PARTIAL` 판정 지시가 게이트의 핵심이다.

**Files:**
- Modify: `src/ReSet.Validator.Core/Models/GapReport.cs`
- Modify: `src/ReSet.Validator.Core/Services/ValidatorAiService.cs:26-40`
- Test: `tests/ReSet.Core.Tests/ValidatorAiServiceTests.cs`, `tests/ReSet.Core.Tests/ValidatorTests.cs`

**Interfaces:**
- Consumes: `DataAccessPolicy.VerificationCriteria` (Task 1)
- Produces: `GapReport.DataAccessBoundaryGap` (`string`, 기본값 `string.Empty`) — Task 4가 4곳에서 읽는다

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/ValidatorAiServiceTests.cs`의 클래스 안에 추가한다.

```csharp
        [Fact]
        public async Task VerifyCodeAsync_SendsTheDataAccessBoundaryCriteria()
        {
            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"{
  ""OverallStatus"": ""MATCH"",
  ""Suggestions"": ""ok""
}";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var service = new ValidatorAiService(mockAiClient);
            var report = await service.VerifyCodeAsync("spec", "code", "C#");

            // 규칙 문구는 DataAccessPolicy가 단독 소유한다. 프롬프트는 그것을 그대로 싣는다.
            Assert.Contains(DataAccessPolicy.VerificationCriteria, report.SystemPrompt);
            Assert.Contains("DataAccessBoundaryGap", report.SystemPrompt);
        }

        [Fact]
        public async Task VerifyCodeAsync_ParsesTheBoundaryGapField()
        {
            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""OverallStatus"": ""PARTIAL"",
  ""InputParametersGap"": """",
  ""OutputResultSetsGap"": """",
  ""BusinessLogicGap"": """",
  ""ExceptionHandlingGap"": """",
  ""DataAccessBoundaryGap"": ""정산 집계 UPDATE가 EF Core ExecuteUpdate로 구현됨"",
  ""Suggestions"": ""집계 UPDATE를 파라미터 바인딩 SQL로 되돌리십시오.""
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var service = new ValidatorAiService(mockAiClient);
            var report = await service.VerifyCodeAsync("spec", "code", "C#");

            Assert.Equal("PARTIAL", report.OverallStatus);
            Assert.Contains("EF Core ExecuteUpdate", report.DataAccessBoundaryGap);
            Assert.True(report.HasGaps);
        }
```

`tests/ReSet.Core.Tests/ValidatorTests.cs`의 클래스 안에 추가한다.

```csharp
        [Fact]
        public void GapReport_WithOnlyBoundaryGap_ReportsHasGaps()
        {
            // 경계 위반만 있는 상태를 "차이 없음"으로 보고하면, 이후 HasGaps를 게이트로
            // 쓰는 코드가 생겼을 때 경계 위반만 조용히 누락된다.
            var report = new GapReport
            {
                OverallStatus = "MATCH",
                DataAccessBoundaryGap = "체크포인트 외 경로에서 ORM 사용"
            };

            Assert.True(report.HasGaps);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "VerifyCodeAsync_SendsTheDataAccessBoundaryCriteria|VerifyCodeAsync_ParsesTheBoundaryGapField|GapReport_WithOnlyBoundaryGap_ReportsHasGaps"`
Expected: 컴파일 실패. `GapReport.DataAccessBoundaryGap` 멤버가 없다는 오류(CS0117).

- [ ] **Step 3: GapReport에 필드 추가**

`src/ReSet.Validator.Core/Models/GapReport.cs`에서 `ExceptionHandlingGap` 다음에 필드를 추가하고 `HasGaps`에 반영한다.

```csharp
        public string ExceptionHandlingGap { get; set; } = string.Empty;
        public string DataAccessBoundaryGap { get; set; } = string.Empty;
        public string Suggestions { get; set; } = string.Empty;
```

```csharp
        public bool HasGaps => OverallStatus != "MATCH" || 
                              !string.IsNullOrEmpty(InputParametersGap) || 
                              !string.IsNullOrEmpty(OutputResultSetsGap) || 
                              !string.IsNullOrEmpty(BusinessLogicGap) || 
                              !string.IsNullOrEmpty(ExceptionHandlingGap) ||
                              !string.IsNullOrEmpty(DataAccessBoundaryGap);
```

- [ ] **Step 4: L2 프롬프트를 머리/기준/꼬리로 분리**

`src/ReSet.Validator.Core/Services/ValidatorAiService.cs`에 `using ReSet.Core.Services;`가 이미 있는지 확인하고 없으면 추가한다. 그다음 클래스 안에 상수 두 개를 선언한다.

```csharp
        // 프롬프트를 보간($@"...")으로 조립하면 JSON 예시의 중괄호를 전부 {{ }}로
        // 이스케이프해야 한다(AGENTS.md 범주 7). 연결 방식으로 그 함정을 피한다.
        private const string VerifyPromptHead = @"당신은 데이터베이스 Stored Procedure 역공학 명세서(Spec.md)와 이를 마이그레이션하여 구현한 프로그램 코드(C# 또는 Java)를 일대일 비교하여 기능적으로 완벽히 동일하게 구현되었는지 정밀 검증하는 전문 QA 에이전트입니다.

비교 검증 시 다음 항목들에 주목하십시오:
1. 입력 파라미터 매핑: 설계서에 명시된 파라미터들이 코드의 입력 인자나 객체 필드로 정확히 전달되는가?
2. 출력 데이터셋/반환값: 쿼리 조회 결과나 DTO 반환 필드가 누락 없이 매핑되는가?
3. 핵심 비즈니스 로직: 조건문 분기, 연산 로직, 주요 쿼리 실행 등이 설계서와 의미론적으로 완벽히 동일한가?
4. 예외 처리 및 트랜잭션: 오류 제어 구조 및 트랜잭션 제어 여부가 설계 사양서와 부합하는가?
";

        private const string VerifyPromptTail = @"
당신의 분석 결과는 반드시 다음 JSON 형식으로만 응답해야 합니다. 다른 텍스트나 서론, 결론은 절대 포함하지 마십시오.

{
  ""OverallStatus"": ""MATCH"" | ""MISMATCH"" | ""PARTIAL"" (반드시 세 값 중 하나로만 지정하며, 다른 텍스트 추가 금지),
  ""InputParametersGap"": ""입력 인자 불일치 내용 기술 (없으면 빈 문자열)"",
  ""OutputResultSetsGap"": ""출력 컬럼/DTO 필드 불일치 내용 기술 (없으면 빈 문자열)"",
  ""BusinessLogicGap"": ""비즈니스 로직 및 쿼리 조건 불일치 내용 기술 (없으면 빈 문자열)"",
  ""ExceptionHandlingGap"": ""예외 및 트랜잭션 처리 불일치 내용 기술 (없으면 빈 문자열)"",
  ""DataAccessBoundaryGap"": ""데이터 액세스 경계 규칙 위반 내용 기술 (없으면 빈 문자열)"",
  ""Suggestions"": ""불일치 해결을 위한 구체적인 코드 수정 가이드라인""
}";
```

그리고 `VerifyCodeAsync`(`:26`) 안의 기존 `var systemPrompt = @"..."` 리터럴 전체를 아래 한 줄로 바꾼다.

```csharp
            var systemPrompt = VerifyPromptHead + DataAccessPolicy.VerificationCriteria + VerifyPromptTail;
```

`ParseGapReport`는 `JsonSerializer.Deserialize<GapReport>`를 쓰므로 새 필드가 자동 파싱된다. 파싱 코드는 손대지 않는다.

- [ ] **Step 5: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter "ValidatorAiServiceTests|ValidatorTests"`
Expected: PASS. 신규 3건과 기존 전량이 통과해야 한다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Validator.Core/Models/GapReport.cs src/ReSet.Validator.Core/Services/ValidatorAiService.cs tests/ReSet.Core.Tests/ValidatorAiServiceTests.cs tests/ReSet.Core.Tests/ValidatorTests.cs
git commit -m "feat: judge data access boundary violations in the L2 gap analysis"
```

---

### Task 4: 경계 Gap 표기와 피드백 축적

새 Gap 항목을 사람이 읽는 3곳과 자가 수정 루프 피드백 1곳에 실어 보낸다. 이 태스크를 빠뜨리면 AI가 판정한 위반 내용이 화면과 지시서에서 사라져, 에이전트가 무엇을 고쳐야 하는지 알 수 없게 된다.

**Files:**
- Modify: `src/ReSet.Cli/ValidationUiProxy.cs:81`
- Modify: `src/ReSet.Validator.Cli/ConsoleUserInteraction.cs:46`
- Modify: `src/ReSet.Validator.Core/Services/CodeVerificationOrchestrator.cs:260-261`
- Modify: `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs:89`
- Test: `tests/ReSet.Core.Tests/CodeVerificationOrchestratorTests.cs`

**Interfaces:**
- Consumes: `GapReport.DataAccessBoundaryGap` (Task 3)
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/CodeVerificationOrchestratorTests.cs`의 클래스 안에 추가한다. 리포트 경로는 `{OutputDirectory}/docs/{MappedName}/ValidationReport.md`다(`CodeVerificationOrchestrator.cs:207,218,223`).

```csharp
        [Fact]
        public async Task RunVerificationAsync_WritesTheBoundaryGapIntoTheReport()
        {
            var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var specDir = Path.Combine(tempBase, "output");
            var codeDir = Path.Combine(tempBase, "src");
            var outDir = Path.Combine(tempBase, "reports");

            Directory.CreateDirectory(specDir);
            Directory.CreateDirectory(codeDir);

            var specPath = Path.Combine(specDir, "Jobs", "Consolidated_Batch_Job", "docs", "BatchMigrationPlan.md");
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            File.WriteAllText(specPath, "# Spec");
            File.WriteAllText(Path.Combine(codeDir, "Consolidated_Batch_Job.cs"), "public class Consolidated_Batch_Job {}");

            var config = new ValidatorConfig
            {
                SpecDirectory = specDir,
                SourceCodeDirectory = codeDir,
                OutputDirectory = outDir,
                MaxL2Attempts = 1
            };

            var mockAiClient = Substitute.For<IAiClient>();
            var jsonResponse = @"```json
{
  ""OverallStatus"": ""PARTIAL"",
  ""InputParametersGap"": """",
  ""OutputResultSetsGap"": """",
  ""BusinessLogicGap"": """",
  ""ExceptionHandlingGap"": """",
  ""DataAccessBoundaryGap"": ""청킹 루프 내부 INSERT가 EF Core SaveChanges로 구현됨"",
  ""Suggestions"": ""청킹 INSERT를 파라미터 바인딩 SQL로 되돌리십시오.""
}
```";
            mockAiClient.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = jsonResponse }));

            var mockUi = Substitute.For<IValidationUserInterface>();
            var orchestrator = new CodeVerificationOrchestrator(config, mockAiClient, ui: mockUi);

            try
            {
                var results = await orchestrator.RunVerificationAsync(isBatchMode: true, CancellationToken.None);

                Assert.Single(results);
                Assert.False(results[0].L2Passed);

                var reportPath = Path.Combine(outDir, "docs", results[0].MappedName, "ValidationReport.md");
                Assert.True(File.Exists(reportPath));

                var report = await File.ReadAllTextAsync(reportPath);
                Assert.Contains("데이터 액세스 경계", report);
                Assert.Contains("EF Core SaveChanges", report);
            }
            finally
            {
                if (Directory.Exists(tempBase))
                {
                    Directory.Delete(tempBase, true);
                }
            }
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter RunVerificationAsync_WritesTheBoundaryGapIntoTheReport`
Expected: FAIL. `Assert.Contains("데이터 액세스 경계", report)` 실패 — 리포트에 5번 섹션이 없다.

- [ ] **Step 3: ValidationReport.md에 5번 섹션 추가**

`src/ReSet.Validator.Core/Services/CodeVerificationOrchestrator.cs:260-261`의 다음 두 줄 뒤에

```
### 4. 예외 및 트랜잭션 처리 Gap
{(string.IsNullOrEmpty(res.GapReport.ExceptionHandlingGap) ? "일치함 (차이점 없음)" : res.GapReport.ExceptionHandlingGap)}
```

아래 두 줄을 이어 넣는다(보간 문자열 안이므로 형식을 그대로 따른다).

```
### 5. 데이터 액세스 경계 Gap
{(string.IsNullOrEmpty(res.GapReport.DataAccessBoundaryGap) ? "일치함 (차이점 없음)" : res.GapReport.DataAccessBoundaryGap)}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter RunVerificationAsync_WritesTheBoundaryGapIntoTheReport`
Expected: PASS

- [ ] **Step 5: TUI 패널 2곳에 항목 추가**

`src/ReSet.Cli/ValidationUiProxy.cs:81`의 다음 줄을

```csharp
                    $"[bold]4. 예외/트랜잭션 Gap:[/] {Markup.Escape(string.IsNullOrEmpty(report.ExceptionHandlingGap) ? "일치" : report.ExceptionHandlingGap)}\n\n" +
```

아래 두 줄로 바꾼다(4번의 줄바꿈이 `\n\n`에서 `\n`으로 줄고, 새 5번이 `\n\n`을 갖는다).

```csharp
                    $"[bold]4. 예외/트랜잭션 Gap:[/] {Markup.Escape(string.IsNullOrEmpty(report.ExceptionHandlingGap) ? "일치" : report.ExceptionHandlingGap)}\n" +
                    $"[bold]5. 데이터 액세스 경계 Gap:[/] {Markup.Escape(string.IsNullOrEmpty(report.DataAccessBoundaryGap) ? "일치" : report.DataAccessBoundaryGap)}\n\n" +
```

`src/ReSet.Validator.Cli/ConsoleUserInteraction.cs:46`에 완전히 동일한 형태의 줄이 있다. 같은 방식으로 바꾼다.

```csharp
                    $"[bold]4. 예외/트랜잭션 Gap:[/] {Markup.Escape(string.IsNullOrEmpty(report.ExceptionHandlingGap) ? "일치" : report.ExceptionHandlingGap)}\n" +
                    $"[bold]5. 데이터 액세스 경계 Gap:[/] {Markup.Escape(string.IsNullOrEmpty(report.DataAccessBoundaryGap) ? "일치" : report.DataAccessBoundaryGap)}\n\n" +
```

- [ ] **Step 6: 자가 수정 루프 피드백에 항목 추가**

`src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs:89`의 다음 줄 뒤에

```csharp
                            feedbackBuilder.AppendLine($"- 예외 및 트랜잭션 불일치: {gap.ExceptionHandlingGap}");
```

아래 줄을 이어 넣는다.

```csharp
                            feedbackBuilder.AppendLine($"- 데이터 액세스 경계 위반: {gap.DataAccessBoundaryGap}");
```

- [ ] **Step 7: 기존 JSON 픽스처에 새 필드 명시**

`JsonSerializer`가 누락 필드를 기본값으로 채우므로 픽스처를 고치지 않아도 테스트는 통과한다. 그래도 픽스처가 실제 AI 응답 형태와 어긋나면, 나중에 이 픽스처를 복사해 새 테스트를 쓰는 사람이 필드를 빠뜨린 응답을 정상으로 오인한다. 아래 3곳의 JSON에 `""ExceptionHandlingGap""` 다음 줄로 필드를 추가한다.

```
  ""DataAccessBoundaryGap"": """",
```

| 파일 | 위치 |
|---|---|
| `tests/ReSet.Core.Tests/ValidatorAiServiceTests.cs` | `:24` 부근의 MATCH 픽스처 |
| `tests/ReSet.Core.Tests/ValidatorTests.cs` | `:140` 부근의 픽스처 |
| `tests/ReSet.Core.Tests/CodeVerificationOrchestratorTests.cs` | `:50`, `:116` 부근의 두 픽스처 |

- [ ] **Step 8: 전체 테스트 통과 확인**

Run: `dotnet build && dotnet test`
Expected: 전량 PASS. `CancellationPolicyTests`도 통과해야 한다(새 `catch`를 넣지 않았으므로 기준선 변동 없음).

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Cli/ValidationUiProxy.cs src/ReSet.Validator.Cli/ConsoleUserInteraction.cs src/ReSet.Validator.Core/Services/CodeVerificationOrchestrator.cs src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs tests/ReSet.Core.Tests/CodeVerificationOrchestratorTests.cs tests/ReSet.Core.Tests/ValidatorAiServiceTests.cs tests/ReSet.Core.Tests/ValidatorTests.cs
git commit -m "feat: surface the boundary gap in reports, TUI panels, and agent feedback"
```

---

### Task 5: 단일 SP 계획 프롬프트 표현 정합성

지시서가 ORM을 4가지 용도로 제한하게 되었으므로, 계획 프롬프트가 `OOP/ORM 의사코드`를 요구하는 표현과 어긋난다. 한 단어를 제거한다.

이 경로의 실제 상태는 다음과 같다. TUI 메뉴 1은 계획서를 만들지 않고(`Program.cs:1051-1052`), CLI 배치 모드(`--all`/`--sp`)에서만 생성되며(`Program.cs:689,692,733`), 그 산출물은 코딩 에이전트에 전달되지 않는다(AGENTS.md 범주 6). 따라서 생성 코드에는 영향이 없는 문서 정합성 수정이다.

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs:1722`
- Test: `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`

**Interfaces:**
- Consumes: 없음
- Produces: 없음

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`의 `GenerateBatchMigrationPlanAsync_WithRichSpDef_CoversBranch`(`:133`) 아래에 추가한다. 같은 파일의 기존 테스트와 동일한 `MockHttpMessageHandler` + `OpenAiClient` 구성을 쓴다. `AiService`의 생성자는 `AiService(IAiClient aiClient, float temperature, ...)`다(`AiService.cs:23`).

```csharp
        [Fact]
        public async Task GenerateBatchMigrationPlanAsync_DoesNotAskForOrmPseudocode()
        {
            // 지시서가 ORM을 허용 목록 4가지로 제한하므로, 계획 프롬프트가 ORM 의사코드를
            // 요구하면 두 문서가 서로 다른 기준을 말하게 된다.
            // Arrange
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "USP_Plan",
                DdlText = "SELECT 1;",
                StaticAnalysis = new SpStaticAnalysisResult()
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 배치 전환 계획\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            // Act
            var result = await service.GenerateBatchMigrationPlanAsync(spDef, "C#");

            // Assert
            Assert.DoesNotContain("ORM pseudocode", result.SystemPrompt);
            Assert.Contains("OOP pseudocode", result.SystemPrompt);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter GenerateBatchMigrationPlanAsync_DoesNotAskForOrmPseudocode`
Expected: FAIL. `Assert.DoesNotContain("ORM pseudocode", ...)` 실패.

- [ ] **Step 3: 프롬프트 문구 수정**

`src/ReSet.Core/Services/AiService.cs:1722`의 다음 줄에서

```
4. **비즈니스 전환 설계 및 의사코드(Pseudocode)**: Provide modern OOP/ORM pseudocode structural examples converting the stored procedure logic.
```

`OOP/ORM`을 `OOP`로 바꾼다.

```
4. **비즈니스 전환 설계 및 의사코드(Pseudocode)**: Provide modern OOP pseudocode structural examples converting the stored procedure logic.
```

같은 프롬프트의 `:1725`에 있는 `specific ORM read-only options` 표현은 격리 수준 대안을 설명하는 문맥이므로 손대지 않는다.

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test tests/ReSet.Core.Tests --filter AiServiceTests`
Expected: PASS. 신규 1건과 기존 `AiServiceTests`·`AiServiceTests_Rich` 전량 통과.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -m "fix: stop asking the single-SP plan for ORM pseudocode"
```

---

### Task 6: 문서 동기화

세 핵심 문서를 코드와 맞춘다.

**Files:**
- Modify: `AGENTS.md`, `README.md`, `docs/architecture.md`

**Interfaces:**
- Consumes: Task 1~5의 최종 코드 상태
- Produces: 없음

- [ ] **Step 1: 전체 빌드와 테스트를 먼저 통과시킨다**

Run: `dotnet build && dotnet test`
Expected: 전량 PASS. 문서에 사실을 쓰기 전에 코드가 실제로 그 상태임을 확인한다.

- [ ] **Step 2: reset-doc-sync 스킬로 문서를 갱신한다**

`reset-doc-sync` 스킬을 호출하고, 아래 내용이 반영되게 한다.

`AGENTS.md`
- Core 서비스 목록에 `DataAccessPolicy.cs` 항목 추가: SQL/ORM 경계 규칙 문구를 단독 소유하며 지시서와 L2 프롬프트가 이 클래스를 참조한다는 점, 다른 곳에서 같은 규칙을 새로 쓰지 말라는 지시
- 범주 6(외부 코딩 에이전트)에 지시서가 경계 규칙을 포함해야 한다는 규칙 추가
- Validator 목록의 `ValidatorAiService`·`GapReport` 설명에 5번째 Gap 항목과 위반 시 최소 `PARTIAL` 판정 규칙 추가

`README.md`
- 3장 "코딩 에이전트 자동 기동 브릿지" 항목에 하이브리드 데이터 액세스 정책(ORM 허용 목록 4가지, 그 외 SQL) 언급
- 4장 Validator 항목에 데이터 액세스 경계 위반 검출 언급

`docs/architecture.md`
- 4.6절(소스코드 정합성 검증 엔진)에 Gap 5항목 구성과 `OverallStatus` 판정 규칙
- 5.3절(지시서 번들링)에 경계 규칙 삽입과 스텁 주석

- [ ] **Step 3: 문서에 쓴 내용이 코드와 맞는지 확인**

Run: `grep -n "DataAccessPolicy" AGENTS.md README.md docs/architecture.md src/ReSet.Core/Services/*.cs src/ReSet.Validator.Core/Services/*.cs`
Expected: 문서 측 언급과 소스 측 정의·소비 지점이 모두 나온다. 문서에만 있고 코드에 없는 이름이 없어야 한다.

- [ ] **Step 4: 커밋**

```bash
git add AGENTS.md README.md docs/architecture.md
git commit -m "docs: document the hybrid data access boundary"
```

---

## 최종 검증

- [ ] `dotnet build` 성공
- [ ] `dotnet test` 전량 통과
- [ ] `git log --oneline` 에 Task 1~6의 커밋 6건이 순서대로 있다
- [ ] `superpowers:finishing-a-development-branch`로 병합 방식을 결정한다
