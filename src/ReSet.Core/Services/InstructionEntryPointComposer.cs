using System.Collections.Generic;
using System.Text;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>인덱스 한 줄. 표시 이름과 진입점 기준 상대 경로.</summary>
    public sealed record IndexEntry(string Label, string RelativePath);

    /// <param name="Preamble">최종 계획서의 첫 H2 앞 내용. L1Exhausted 배너가 여기 실린다.</param>
    /// <param name="SinglePlanRelativePath">분할 실패 시 계획서 전문의 상대 경로. 분할했으면 null.</param>
    public sealed record EntryPointInputs(
        string JobName,
        string TargetLanguage,
        VerificationOutcome PlanOutcome,
        string Preamble,
        bool StepsSplit,
        IReadOnlyList<IndexEntry> Steps,
        IReadOnlyList<IndexEntry> Dependencies,
        IReadOnlyList<IndexEntry> Specs,
        bool HasStepContract,
        bool HasVerification,
        string? SinglePlanRelativePath);

    /// <summary>
    /// 진입점 `MigrationInstructions.md`를 조립한다.
    ///
    /// 이 클래스가 존재하는 유일한 이유는 <b>순서</b>다. 이전 지시서는 실행 지침을
    /// 7,759줄, 경계 규칙을 7,773줄에 두었는데 코딩 에이전트의 Read는 2,000줄에서
    /// 잘린다. 즉 에이전트는 지침을 보지 못한 채 계획 본문만 읽고 작업을 시작했다.
    /// 지침과 경계 규칙은 어떤 계획 링크보다도 앞에 와야 한다.
    /// </summary>
    public static class InstructionEntryPointComposer
    {
        public static string Compose(EntryPointInputs inputs)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# 🚀 Consolidated Migration Instructions for Coding Agent ({inputs.JobName})");
            sb.AppendLine();
            sb.AppendLine("본 문서는 복수의 SQL Server Stored Procedure를 하나의 통합 배치로 마이그레이션하기 위한 **진입점**입니다.");
            sb.AppendLine("이 파일을 끝까지 읽은 뒤, 배정된 작업 파일(`task-*.md`)이 지시하는 것만 읽고 구현하십시오.");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            // 검증 상태가 맨 앞에 온다. 계획을 소비한 뒤에 경고를 만나면 이미 늦다.
            sb.AppendLine(PlanVerificationSection(inputs.PlanOutcome));

            if (!string.IsNullOrWhiteSpace(inputs.Preamble))
            {
                sb.AppendLine();
                sb.AppendLine(inputs.Preamble.Trim());
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            AppendGuidelines(sb);
            sb.AppendLine("---");
            sb.AppendLine();
            AppendReadingContract(sb, inputs);
            sb.AppendLine("---");
            sb.AppendLine();
            AppendTechStack(sb, inputs);
            sb.AppendLine("---");
            sb.AppendLine();
            AppendIndex(sb, inputs);

            return sb.ToString();
        }

        /// <summary>
        /// 계획서의 검증 상태 배너. `MetadataExporter.BuildPlanVerificationSection`을 그대로
        /// 옮겨 왔다. 통과일 때도 침묵하지 않는다 - "표기 부재 = 검증됨"이라는 추론이
        /// 이 계열 결함의 뿌리다.
        /// </summary>
        public static string PlanVerificationSection(VerificationOutcome planOutcome)
        {
            var label = VerificationDocumentFormatter.StatusLabel(planOutcome);
            var sb = new StringBuilder();

            if (planOutcome == VerificationOutcome.Passed)
            {
                sb.AppendLine("## ✅ 0. 이 계획서의 검증 상태");
                sb.AppendLine();
                sb.AppendLine($"**{label}** — L1 기계 검증과 L2 AI 교차 리뷰를 모두 통과한 계획입니다.");
                return sb.ToString();
            }

            var reason = planOutcome switch
            {
                VerificationOutcome.QualityRejected =>
                    "L2 AI 교차 리뷰의 품질 기준을 통과하지 못한 계획입니다.",
                VerificationOutcome.ReviewNotRun =>
                    "L2 AI 교차 리뷰를 거치지 않은 계획입니다.",
                VerificationOutcome.L1Exhausted =>
                    "L1 기계 검증을 통과하지 못한 채 확정된 계획입니다.",
                _ =>
                    "검증 상태를 확인할 수 없는 계획입니다."
            };

            sb.AppendLine("## ⚠️ 0. 이 계획서의 검증 상태");
            sb.AppendLine();
            sb.AppendLine($"**{label}** — {reason}");
            sb.AppendLine("아래 계획을 그대로 구현하기 전에 사람의 검토가 필요합니다.");
            return sb.ToString();
        }

        private static void AppendGuidelines(StringBuilder sb)
        {
            sb.AppendLine("## 🔑 1. 에이전트 핵심 수행 지침 (Agent Execution Guidelines)");
            sb.AppendLine();
            sb.AppendLine("당신은 전문 코딩 에이전트입니다. 아래 지침은 모든 회차에 예외 없이 적용됩니다.");
            sb.AppendLine();
            sb.AppendLine("1. 전환 계획의 배치 단계 및 공통 모듈 설계 규칙을 엄격히 준수할 일.");
            sb.AppendLine("2. 생성할 파일 경로는 타겟 프로젝트의 아키텍처 규칙에 맞춰 작성할 일.");
            sb.AppendLine("3. 데이터 액세스 계층(Repository/DAO 등)은 3장의 데이터 액세스 경계 규칙을 준수하며 타겟 언어 및 프레임워크의 권장 패턴을 따를 일.");
            sb.AppendLine("4. 의존성 역전 원칙(DIP) 등을 준수하여 비즈니스 로직과 인프라스트럭처 결합도를 낮출 일.");
            sb.AppendLine("5. 트랜잭션 단위와 예외 처리(Rollback 등)를 명확히 설계하여 데이터 정합성을 보장할 일.");
            sb.AppendLine("6. 제공된 자가 검증용 단위 테스트 및 아키텍처 검증 코드를 통과(PASS)시키고 빌드가 성공함을 자체 점검할 일.");
            sb.AppendLine("7. **[중요]** 어떠한 경우에도 `// implementation omitted`, `// TODO`, `/* Build SQL */` 등의 주석으로 코드를 생략(Placeholder)하지 마십시오. 3장의 경계 규칙에 따라 SQL 경로로 분류된 DML은 명세서에 있는 원본 로직(조건절·집계식·에러 코드)을 축약 없이 파라미터 바인딩 SQL로 100% 완전하게 작성해야 하며, ORM은 3장의 허용 목록에 한해 사용해야 합니다.");
            sb.AppendLine("8. **[중요]** Worker 구성 시 반드시 명세된 모든 DB Factory 의존성을 `SettleContext`에 할당해야 합니다. 누락 시 런타임 예외가 발생하여 검증을 통과할 수 없습니다.");
            sb.AppendLine("9. **[중요]** 모든 Tasklet 클래스는 사전에 제공된 `src/AbstractSettleTasklet.cs`의 `AbstractSettleTasklet`을 강제로 상속받아 구현해야 합니다. 임의의 구조를 만들거나 에러코드를 자의적으로 변경하지 마십시오.");
            sb.AppendLine();
            sb.AppendLine("**[경고] 원본 Stored Procedure(.sql) 파일은 레거시 코드이므로 절대 검색(find 명령어 등)하거나 직접 참조하지 마십시오. 모든 비즈니스 로직은 이미 분석 완료된 Spec.md 문서에 정의되어 있습니다.**");
            sb.AppendLine();
        }

        private static void AppendReadingContract(StringBuilder sb, EntryPointInputs inputs)
        {
            sb.AppendLine("## 📖 2. 읽기 계약 (Reading Contract)");
            sb.AppendLine();
            sb.AppendLine("이 프로젝트는 **회차 단위**로 구현합니다. 한 회차는 작업 파일 하나(`task-*.md`)에 대응합니다.");
            sb.AppendLine();
            sb.AppendLine("1. 배정된 `task-*.md`와 그 파일이 링크한 것만 읽으십시오.");

            if (inputs.StepsSplit)
            {
                sb.AppendLine("2. **다른 Step 파일을 읽지 마십시오.** 다른 Step의 코드를 작성하지도 마십시오.");
            }
            else
            {
                sb.AppendLine("2. 계획서가 단일 파일로 제공됩니다. **배정된 회차에 해당하는 단계 절만 읽고 구현하십시오.** 다른 Step의 코드를 작성하지 마십시오.");
            }

            sb.AppendLine("3. `common/`이 정의한 공통 계약에 해당하는 기존 파일은 수정하지 마십시오.");
            sb.AppendLine("4. 진행 상태는 도구가 검증 결과를 근거로 기록합니다. `todo.md`를 직접 편집하지 마십시오.");
            sb.AppendLine();
        }

        private static void AppendTechStack(StringBuilder sb, EntryPointInputs inputs)
        {
            sb.AppendLine("## 🛠️ 3. 기술 스택 및 데이터 액세스 경계");
            sb.AppendLine();
            sb.AppendLine(DataAccessPolicy.InstructionRules(inputs.TargetLanguage));
            sb.AppendLine();
            sb.AppendLine("전문은 [common/02-data-access-boundary.md](common/02-data-access-boundary.md)에 있습니다.");
            sb.AppendLine();
        }

        private static void AppendIndex(StringBuilder sb, EntryPointInputs inputs)
        {
            sb.AppendLine("## 📂 4. 파일 인덱스");
            sb.AppendLine();
            sb.AppendLine("### 공통 (모든 회차에서 읽습니다)");
            sb.AppendLine();
            sb.AppendLine("- [common/00-architecture.md](common/00-architecture.md) — 아키텍처 개요와 실행 흐름");

            if (inputs.HasStepContract)
            {
                sb.AppendLine("- [common/01-step-contract.md](common/01-step-contract.md) — 모든 단계가 공유하는 실행 계약");
            }

            sb.AppendLine("- [common/02-data-access-boundary.md](common/02-data-access-boundary.md) — SQL/ORM 경계 규칙");
            sb.AppendLine();

            if (inputs.StepsSplit && inputs.Steps.Count > 0)
            {
                sb.AppendLine("### 단계별 상세 (배정된 것만 읽습니다)");
                sb.AppendLine();
                foreach (var step in inputs.Steps)
                {
                    sb.AppendLine($"- [{step.Label}]({step.RelativePath})");
                }
                sb.AppendLine();
            }
            else if (inputs.SinglePlanRelativePath != null)
            {
                sb.AppendLine("### 통합 배치 전환 계획 (단일 파일)");
                sb.AppendLine();
                sb.AppendLine("계획서를 단계별로 분할하지 못했습니다. 아래 파일에서 배정된 단계 절만 찾아 읽으십시오.");
                sb.AppendLine();
                sb.AppendLine($"- [BatchMigrationPlan.md]({inputs.SinglePlanRelativePath})");
                sb.AppendLine();
            }

            if (inputs.HasVerification)
            {
                sb.AppendLine("### 정합성 검증 SQL");
                sb.AppendLine();
                sb.AppendLine("- [verification/integrity-sql.md](verification/integrity-sql.md)");
                sb.AppendLine();
            }

            sb.AppendLine("### 의존 테이블·함수 스키마");
            sb.AppendLine();
            sb.AppendLine("데이터 액세스 계층 구현 시 아래에서 컬럼과 데이터 타입을 확인하십시오. 핵심 비즈니스 로직은 계획서와 명세서만 따르며, 원본 SQL 코드를 조회하려 해서는 안 됩니다.");
            sb.AppendLine();
            foreach (var dep in inputs.Dependencies)
            {
                sb.AppendLine($"- **{dep.Label}**: [{dep.RelativePath}]({dep.RelativePath})");
            }
            sb.AppendLine();

            sb.AppendLine("### 원본 설계 명세서");
            sb.AppendLine();
            sb.AppendLine("개별 프로시저의 세부 로직(UPDATE 수식 등)이 필요할 때만 해당 회차의 것을 참조하십시오.");
            sb.AppendLine();
            foreach (var spec in inputs.Specs)
            {
                sb.AppendLine($"- **{spec.Label}**: [Spec.md]({spec.RelativePath})");
            }
            sb.AppendLine();

            sb.AppendLine("### 진행 상태");
            sb.AppendLine();
            sb.AppendLine("- [todo.md](todo.md) — 도구가 갱신합니다. 읽기 전용으로 참고하십시오.");
            sb.AppendLine();
        }
    }
}
