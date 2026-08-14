using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Serilog;

namespace ReSet.Core.Services
{
    public enum StageKind
    {
        Bootstrap,
        Step,
        Assembly
    }

    /// <param name="Id">"01-S01" 형태. task-*.md 파일명에서 "task-" 접두와 확장자를 뗀 것.
    /// progress.json의 회차 식별자와 같다.</param>
    /// <param name="StepCode">단계 회차면 그 코드(파일명에서 되짚어낸 정화된 값), Bootstrap/Assembly면 null.</param>
    public sealed record TaskStageIdentity(string Id, StageKind Kind, string? StepCode);

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

    /// <summary>
    /// 회차 하나의 작업 지시서를 조립한다.
    ///
    /// 회차 전환은 코딩 엔진에 <b>다른 지시서 경로를 넘기는 것</b>으로 끝난다.
    /// ICodingEngine이 이미 경로를 파라미터로 받으므로 인자 템플릿과
    /// ArgumentTemplateResolver는 손대지 않는다.
    ///
    /// 파일은 반드시 agent/ 직하에 놓는다. 하위 디렉터리에 두면
    /// ArgumentTemplateResolver.ResolveJobDirectory(두 단계 위 = Job 루트)가
    /// {jobDir}을 agent/로 해석해 --add-dir이 raw/ddl(Job 루트 직하)을 덮지 못한다.
    ///
    /// Spec.md는 이 근거에 포함되지 않는다 - 명세서는 &lt;outputRoot&gt;/Procedures/... 에
    /// 있어 Job 루트의 자손이 아니라 형제이며(지시서 링크가 ../../../Procedures/...로
    /// 시작한다), {jobDir}이 아니라 별도 자리표시자 {specRoot}로 스코프를 받는다.
    /// </summary>
    public static class TaskFileComposer
    {
        public static string FileName(StageKind kind, int ordinal, string? stepCode) => kind switch
        {
            StageKind.Bootstrap => "task-00-bootstrap.md",
            StageKind.Assembly => "task-99-assembly.md",
            _ => $"task-{ordinal:D2}-{SanitizeStepCode(stepCode)}.md",
        };

        /// <summary>
        /// FileName이 회차 종류·코드를 파일명으로 인코딩하는 것의 역변환이다.
        ///
        /// progress.json을 채우는 MetadataExporter와 회차 실행 계획을 세우는
        /// CodegenStagePlan(Task 12)은 둘 다 bundle.TaskFilePaths에서 같은 회차 목록을
        /// 다시 읽어낸다. 이 로직을 두 곳에서 각자 구현하면 서수 자릿수나 회차 종류가
        /// 하나 늘었을 때 둘만 조용히 어긋나 progress.json과 실행 계획이 다른 회차를
        /// 가리키게 된다 - 그래서 여기 하나로 모은다.
        /// </summary>
        public static TaskStageIdentity ParseStageIdentity(string taskFileBaseName)
        {
            var id = taskFileBaseName.StartsWith("task-", StringComparison.Ordinal)
                ? taskFileBaseName["task-".Length..]
                : taskFileBaseName;

            var parts = taskFileBaseName.Split('-');
            if (parts.Length < 3)
            {
                // task-<서수>-<코드> 형태를 벗어난 파일명이다. 조용히 Step으로 치부하면
                // 부트스트랩/조립 판별이 틀렸는지 알 방법이 없다.
                Log.Warning(
                    "작업 파일명이 예상된 회차 이름 형식(task-서수-코드)이 아닙니다 - 파일명: {FileName}",
                    taskFileBaseName);
                return new TaskStageIdentity(id, StageKind.Step, null);
            }

            var tail = string.Join("-", parts.Skip(2));

            return tail switch
            {
                "bootstrap" => new TaskStageIdentity(id, StageKind.Bootstrap, null),
                "assembly" => new TaskStageIdentity(id, StageKind.Assembly, null),
                _ => new TaskStageIdentity(id, StageKind.Step, tail),
            };
        }

        /// <summary>
        /// stepCode는 AI가 생성한 계획서 텍스트에서 뽑아낸 값이라 신뢰할 수 없다.
        /// 그대로 파일명에 꽂으면 "../"나 경로 구분자를 태운 코드가 agent/ 바깥에
        /// 파일을 쓰게 만들 수 있다. 파일명으로 안전한 문자만 남기고 나머지는 버린다.
        ///
        /// task-*.md와 steps/*.md가 <b>같은</b> 정화 결과를 파일명으로 써야 한다.
        /// 예전에는 steps/ 쪽만 원본 코드를 그대로 썼는데, 그 비대칭이 (1) agent/steps/
        /// 바깥으로 쓰는 경로 탈출, (2) 정화가 코드를 바꾸는 정상 번들이 재구동에서
        /// Broken으로 거부되는 막다른 길, (3) CodegenStagePlan.FromBundle과
        /// TryClassifyExistingInstructionsFile이 서로 다른 StepCode를 내는 분기를
        /// 한꺼번에 만들어 냈다. 그래서 이 메서드를 공개해 양쪽이 같이 쓴다.
        /// </summary>
        public static string SanitizeStepCode(string? stepCode)
        {
            if (string.IsNullOrEmpty(stepCode))
            {
                return "unknown";
            }

            var sb = new StringBuilder(stepCode.Length);
            foreach (var ch in stepCode)
            {
                if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
                {
                    sb.Append(ch);
                }
            }

            return sb.Length > 0 ? sb.ToString() : "unknown";
        }

        public static string Compose(TaskFileInputs inputs)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# {Title(inputs)}");
            sb.AppendLine();
            sb.AppendLine("## 먼저 읽을 것");
            sb.AppendLine();
            sb.AppendLine("1. [MigrationInstructions.md](MigrationInstructions.md) — 지침과 읽기 계약. **반드시 먼저 읽으십시오.**");
            sb.AppendLine("2. [common/00-architecture.md](common/00-architecture.md) — 아키텍처 개요");

            if (inputs.HasStepContract)
            {
                sb.AppendLine("3. [common/01-step-contract.md](common/01-step-contract.md) — 모든 단계가 공유하는 실행 계약");
            }

            sb.AppendLine($"{(inputs.HasStepContract ? 4 : 3)}. [common/02-data-access-boundary.md](common/02-data-access-boundary.md) — SQL/ORM 경계 규칙");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            switch (inputs.Kind)
            {
                case StageKind.Bootstrap:
                    AppendBootstrap(sb, inputs);
                    break;
                case StageKind.Assembly:
                    AppendAssembly(sb, inputs);
                    break;
                default:
                    AppendStep(sb, inputs);
                    break;
            }

            return sb.ToString();
        }

        private static string Title(TaskFileInputs inputs) => inputs.Kind switch
        {
            StageKind.Bootstrap => $"회차 0 — 공통 인프라 구성 ({inputs.JobName})",
            StageKind.Assembly => $"최종 회차 — Job 파이프라인 조립 ({inputs.JobName})",
            _ => $"회차 {inputs.StepCode} — {inputs.StepName} ({inputs.JobName})",
        };

        private static void AppendBootstrap(StringBuilder sb, TaskFileInputs inputs)
        {
            sb.AppendLine("## 이번 회차에서 구현할 것");
            sb.AppendLine();
            sb.AppendLine("이 회차는 **공통 인프라만** 만듭니다.");
            sb.AppendLine();
            sb.AppendLine("- 프로젝트 골격과 폴더 구조 (Hexagonal Architecture)");
            sb.AppendLine($"- 빌드 환경 구성 및 필수 패키지 설치 ({ToolingPackages(inputs.TargetLanguage)})");
            sb.AppendLine("- 의존성 주입 등록과 Worker 진입점");
            // 배치 호스팅과 멀티 DB 연결 문자열 구성은 스캐폴딩을 세우는 이 회차에서만
            // 필요하다. Step/Assembly 회차는 이 링크를 받지 않는다 - 이미 구성된
            // 호스팅/DI를 다시 참조할 이유가 없고, "단계 상세 문서를 읽지 마십시오"와
            // 같은 이유로 회차별 지시서는 그 회차가 읽어야 할 것만 가리켜야 한다.
            sb.AppendLine("- 커넥션 문자열 설정 파일과 `IDbConnectionFactory` 구현체: [common/03-hosting-and-config.md](common/03-hosting-and-config.md)의 호스팅/DI 및 멀티 DB 연결 문자열 안내를 따를 것");
            sb.AppendLine("- `ICheckpointRepository` 구현체");
            sb.AppendLine(BaseStubPlacementLine(inputs.TargetLanguage));
            sb.AppendLine(ArchitectureTestPlacementLine(inputs.TargetLanguage));
            sb.AppendLine();
            sb.AppendLine("## 하지 말 것");
            sb.AppendLine();
            sb.AppendLine("- **어떤 Tasklet을 구현하지 마십시오.** 단계 구현은 이후 회차의 일입니다.");
            sb.AppendLine("- 단계 상세 문서를 읽지 마십시오.");
            sb.AppendLine();
            AppendInfraObjects(sb, inputs);
            AppendDependencies(sb, inputs);
            sb.AppendLine("## 완료 조건");
            sb.AppendLine();
            sb.AppendLine("- 빌드가 성공한다.");
            sb.AppendLine("- 아키텍처 테스트가 통과한다. 이 시점에는 Tasklet이 없으므로 Tasklet 관련 규칙은 대상 0건으로 통과한다 — 그것을 검증 통과로 오해하지 마십시오. 이 회차에서 실제로 검사되는 것은 Domain/Infrastructure 의존 방향과 `SettleContext`의 커넥션 팩토리 주입 가능성(구현체 존재 포함) 두 가지뿐입니다.");
            sb.AppendLine();
        }

        private static void AppendStep(StringBuilder sb, TaskFileInputs inputs)
        {
            sb.AppendLine("## 이번 회차에서 구현할 것");
            sb.AppendLine();
            sb.AppendLine($"단계 **{inputs.StepCode} {inputs.StepName}** 하나만 구현합니다.");
            sb.AppendLine();

            if (inputs.StepRelativePath != null)
            {
                sb.AppendLine($"- 단계 상세: [{inputs.StepRelativePath}]({inputs.StepRelativePath})");
            }
            else if (inputs.SinglePlanRelativePath != null)
            {
                sb.AppendLine(
                    $"- 단계 상세: [BatchMigrationPlan.md]({inputs.SinglePlanRelativePath}) 안에서 `{inputs.StepCode}` 절을 찾아 그 절만 읽으십시오.");
            }

            if (inputs.SpecRelativePath != null)
            {
                sb.AppendLine($"- 원본 명세서: [Spec.md]({inputs.SpecRelativePath}) — UPDATE/INSERT 상세 매핑 수식이 필요할 때만 봅니다.");
            }

            // 이름 규약을 실패한 뒤의 피드백으로만 알려 주면 유료 기동 한 번을 규약
            // 하나 때문에 버린다. 규약은 처음부터 지시서에 실려 있어야 한다.
            sb.AppendLine(CodegenArtifactNaming.DescribeStepArtifactNaming(
                SanitizeStepCode(inputs.StepCode), inputs.TargetLanguage));

            sb.AppendLine();
            sb.AppendLine("`AbstractSettleTasklet`을 상속한 Tasklet 클래스 하나와, 그 단계가 필요로 하는 데이터 액세스 코드를 작성하십시오.");
            sb.AppendLine();
            sb.AppendLine("## 하지 말 것");
            sb.AppendLine();
            sb.AppendLine("- **다른 Step 파일을 읽지 마십시오.**");
            sb.AppendLine("- **다른 Step의 코드를 작성하지 마십시오.**");
            sb.AppendLine("- `common/`이 정의한 공통 계약 파일을 수정하지 마십시오.");
            sb.AppendLine("- Placeholder 주석(`// TODO`, `// implementation omitted`)을 남기지 마십시오.");
            sb.AppendLine();
            AppendDependencies(sb, inputs);
            sb.AppendLine("## 완료 조건");
            sb.AppendLine();
            sb.AppendLine("- 빌드가 성공한다.");
            sb.AppendLine("- 이 단계의 조건절·집계식·오류 코드가 명세서와 축약 없이 일치한다.");
            sb.AppendLine();
        }

        private static void AppendAssembly(StringBuilder sb, TaskFileInputs inputs)
        {
            sb.AppendLine("## 이번 회차에서 구현할 것");
            sb.AppendLine();
            sb.AppendLine("구현된 단계들을 하나의 Job 파이프라인으로 조립합니다.");
            sb.AppendLine();
            sb.AppendLine("- 단계 실행 순서와 선행 조건 검증");
            sb.AppendLine("- 단계 간 예외 전파와 트랜잭션 롤백 처리");
            sb.AppendLine("- 전체 빌드와 아키텍처 테스트 통과");
            // 이 회차의 게이트는 Job 전체 검증이고, 그 검증은 계획서와 소스 트리를
            // Job 이름으로 짝짓는다. 이름 규약이 어디에도 적혀 있지 않던 동안에는
            // 모든 회차가 통과한 실행조차 매핑 0건으로 실패 처리됐다.
            sb.AppendLine(CodegenArtifactNaming.DescribeJobArtifactNaming(inputs.JobName, inputs.TargetLanguage));
            sb.AppendLine();

            if (inputs.FailedStepCodes.Count > 0)
            {
                sb.AppendLine("## 미완성 단계");
                sb.AppendLine();
                sb.AppendLine("아래 단계는 검증을 통과하지 못했습니다. **손대지 마십시오.** 파이프라인에서 제외하고 조립하십시오.");
                sb.AppendLine();
                foreach (var code in inputs.FailedStepCodes)
                {
                    sb.AppendLine($"- `{code}`");
                }
                sb.AppendLine();
                sb.AppendLine("이 단계들이 빠졌으므로 최종 빌드가 깨질 수 있습니다. 그 사실을 숨기지 말고 그대로 두십시오.");
                sb.AppendLine();
            }

            if (inputs.HasVerification)
            {
                sb.AppendLine("## 정합성 검증");
                sb.AppendLine();
                sb.AppendLine("- [verification/integrity-sql.md](verification/integrity-sql.md)의 검증 SQL을 실행 가능한 형태로 배치하십시오.");
                sb.AppendLine();
            }
        }

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

        private static void AppendDependencies(StringBuilder sb, TaskFileInputs inputs)
        {
            if (inputs.Dependencies.Count == 0)
            {
                return;
            }

            sb.AppendLine("## 참조할 스키마");
            sb.AppendLine();
            foreach (var dep in inputs.Dependencies)
            {
                sb.AppendLine($"- **{dep.Label}**: [{dep.RelativePath}]({dep.RelativePath})");
            }
            sb.AppendLine();
        }

        /// <summary>
        /// 최소 버전을 함께 적는다. 버전 없이 이름만 적으면 에이전트가 물어 오는 릴리스가
        /// 무엇일지 정해지지 않는데, 부트스트랩이 통과시켜야 하는 아키텍처 테스트는 특정
        /// 버전 이상에서만 컴파일된다 - 특히 Java 쪽 <c>ArchRule.allowEmptyShould(boolean)</c>는
        /// ArchUnit 0.23.1에서 추가됐다(0.23.0이 빈 should를 실패로 바꾸고, 0.23.1이 규칙별
        /// 예외 메서드를 넣었다). 그보다 낮은 릴리스를 물어 오면 부트스트랩이 통과시켜야 할
        /// 바로 그 파일이 컴파일되지 않고, 부트스트랩 실패는 하드 중단이다.
        /// 테스트 러너(xUnit/JUnit 5)도 함께 적는다 - 아키텍처 테스트 스텁이 그 어노테이션을
        /// 쓰는데 목록에 없어 에이전트가 러너 없이 프로젝트를 세울 수 있었다.
        /// </summary>
        private static string ToolingPackages(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? "MyBatis 3.5+, Spring Data JPA 3.2+, JUnit 5.10+, Mockito 5.0+, ArchUnit 0.23.1+ (`allowEmptyShould` 도입 버전 — 그 아래는 컴파일되지 않음)"
                : "Dapper 2.1+, EF Core 10.0+, xUnit 2.9+, Moq 4.20+, NetArchTest.Rules 1.3.2+";

        /// <summary>
        /// C#은 두 파일(AbstractSettleTasklet.cs·SettleContracts.cs), Java는 확장 표면의
        /// 타입들을 public 파일 하나당 하나씩 낸다(MetadataExporter 참고). 언어와 무관하게
        /// ".cs" 파일명 하나만 하드코딩해 두면 Java 에이전트가 존재하지 않는 지시를 받는다.
        ///
        /// C# 쪽에서 SettleContracts.cs가 빠져 있었다. 그 파일이 담은 ISettleStepDescriptor·
        /// ISettleRepository는 회차 간 단계 등록 순서를 결정론적으로 고정하는 계약인데,
        /// 배치 지시가 없으니 C# 프로젝트에는 조용히 도달하지 않았다 - 컴파일을 깨지
        /// 않는 종류의 누락이라 아무것도 잡지 못했다. 목록은 MetadataExporter가 실제로
        /// agent/src/에 쓰는 파일 전부와 일치해야 한다.
        /// </summary>
        private static string BaseStubPlacementLine(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? "- `src/ISettleStep.java`, `src/AbstractSettleTasklet.java`, `src/SettleContext.java`, `src/StepResult.java`, `src/IDbConnectionFactory.java`, `src/ICheckpointRepository.java`, `src/ISettleStepDescriptor.java`, `src/ISettleRepository.java`를 프로젝트의 `src/main/java/com/reset/batch/core/` 아래로 배치 (패키지 경로와 반드시 일치시킬 것, 내용은 수정 금지)"
                : "- `src/AbstractSettleTasklet.cs`, `src/SettleContracts.cs`를 프로젝트에 배치 (내용은 수정 금지)";

        /// <summary>Java의 소스 루트는 패키지 경로와 일치해야 하므로 대상 경로까지 지시한다.</summary>
        private static string ArchitectureTestPlacementLine(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? "- `tests/ArchitectureTests.java`를 프로젝트의 `src/test/java/com/reset/batch/tests/architecture/` 아래로 배치하고 통과시킬 것"
                : "- `tests/ArchitectureTests.cs`를 프로젝트에 배치하고 통과시킬 것";
    }
}
