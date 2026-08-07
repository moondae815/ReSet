using System;
using System.Collections.Generic;
using System.Text;

namespace ReSet.Core.Services
{
    public enum StageKind
    {
        Bootstrap,
        Step,
        Assembly
    }

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
        string? SinglePlanRelativePath);

    /// <summary>
    /// 회차 하나의 작업 지시서를 조립한다.
    ///
    /// 회차 전환은 코딩 엔진에 <b>다른 지시서 경로를 넘기는 것</b>으로 끝난다.
    /// ICodingEngine이 이미 경로를 파라미터로 받으므로 인자 템플릿과
    /// ArgumentTemplateResolver는 손대지 않는다.
    ///
    /// 파일은 반드시 agent/ 직하에 놓는다. 하위 디렉터리에 두면
    /// ArgumentTemplateResolver.ResolveJobDirectory(두 단계 위 = Job 루트)가
    /// {jobDir}을 agent/로 해석해 --add-dir이 raw/ddl과 Spec.md를 덮지 못한다.
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
        /// stepCode는 AI가 생성한 계획서 텍스트에서 뽑아낸 값이라 신뢰할 수 없다.
        /// 그대로 파일명에 꽂으면 "../"나 경로 구분자를 태운 코드가 agent/ 바깥에
        /// 파일을 쓰게 만들 수 있다(Task 7 리뷰에서 steps/ 쓰기 경로에 있던 것과
        /// 같은 결함). 파일명으로 안전한 문자만 남기고 나머지는 버린다.
        /// </summary>
        private static string SanitizeStepCode(string? stepCode)
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
            sb.AppendLine("- 커넥션 문자열 설정 파일과 `IDbConnectionFactory` 구현체");
            sb.AppendLine("- `ICheckpointRepository` 구현체");
            sb.AppendLine("- `src/AbstractSettleTasklet.cs`를 프로젝트에 배치 (내용은 수정 금지)");
            sb.AppendLine("- `tests/ArchitectureTests.cs`를 프로젝트에 배치하고 통과시킬 것");
            sb.AppendLine();
            sb.AppendLine("## 하지 말 것");
            sb.AppendLine();
            sb.AppendLine("- **어떤 Tasklet을 구현하지 마십시오.** 단계 구현은 이후 회차의 일입니다.");
            sb.AppendLine("- 단계 상세 문서를 읽지 마십시오.");
            sb.AppendLine();
            AppendDependencies(sb, inputs);
            sb.AppendLine("## 완료 조건");
            sb.AppendLine();
            sb.AppendLine("- 빌드가 성공한다.");
            sb.AppendLine("- 아키텍처 테스트가 통과한다. 이 시점에는 Tasklet이 없으므로 Tasklet 관련 규칙은 대상 0건으로 통과한다 — 그것을 검증 통과로 오해하지 마십시오.");
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

        private static string ToolingPackages(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? "MyBatis, Spring Data JPA, Mockito, ArchUnit"
                : "Dapper, EF Core, Moq, NetArchTest";
    }
}
