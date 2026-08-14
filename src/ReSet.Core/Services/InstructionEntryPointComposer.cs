using System.Collections.Generic;
using System.Text;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>인덱스 한 줄. 표시 이름과 진입점 기준 상대 경로.</summary>
    public sealed record IndexEntry(string Label, string RelativePath);

    /// <param name="Preamble">최종 계획서의 첫 H2 앞 내용. L1Exhausted 배너가 여기 실린다.</param>
    /// <param name="SinglePlanRelativePath">분할 실패 시 계획서 전문의 상대 경로. 분할했으면 null.</param>
    /// <param name="Coverage">
    /// 이 산출물이 실제로 받은 기계 검증의 양(<see cref="VerificationCoverage"/>). PlanOutcome이
    /// Passed라도 <see cref="VerificationCoverage.NeedsHumanAttention"/>이 true면 §0은
    /// "모두 통과"만 말해서는 안 된다 - 분할 미실행·미검증 단계·문서 오류코드 누락·목차
    /// 프로시저 커버리지 누락 네 사유 중 실제로 해당하는 것만 그 아래에 밝힌다. null은
    /// 이 산출물에 커버리지 개념이 없다는 뜻(예: 단일 SP 명세서)이며 "검증됨"으로
    /// 해석하지 않는다.
    /// </param>
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
        string? SinglePlanRelativePath,
        VerificationCoverage? Coverage);

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
        /// <summary>
        /// 분할 여부와 무관하게 모든 진입점에 무조건 실리는 문장. 디스크에서 주운
        /// MigrationInstructions.md가 회차 번들인지 옛 단일 문서인지를 가르는 표식이며,
        /// CLI의 재구동 경로(메뉴 3)가 이 상수를 그대로 읽는다.
        ///
        /// 예전에는 이 문장의 부분 문자열이 Program.cs에 손으로 복사돼 있었다. 여기 문구를
        /// 다듬으면 판별이 조용히 멈추고, 회차용 번들이 "다른 Step을 읽지 마십시오"를
        /// 이해하지 못하는 전체 Job 경로로 다시 흘러간다(b336ee5가 막은 바로 그 오라우팅).
        /// ReSet.Cli에는 테스트 프로젝트가 없어 그 회귀를 잡아 줄 것도 없으므로,
        /// 상수 하나로 묶어 컴파일러가 대신 잡게 한다.
        /// </summary>
        public const string StagedBundleMarker = "배정된 작업 파일(`task-*.md`)이 지시하는 것만 읽고 구현하십시오";

        public static string Compose(EntryPointInputs inputs)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# 🚀 Consolidated Migration Instructions for Coding Agent ({inputs.JobName})");
            sb.AppendLine();
            sb.AppendLine("본 문서는 복수의 SQL Server Stored Procedure를 하나의 통합 배치로 마이그레이션하기 위한 **진입점**입니다.");
            sb.AppendLine($"이 파일을 끝까지 읽은 뒤, {StagedBundleMarker}.");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            // 검증 상태가 맨 앞에 온다. 계획을 소비한 뒤에 경고를 만나면 이미 늦다.
            sb.AppendLine(PlanVerificationSection(inputs.PlanOutcome, inputs.Coverage));

            if (!string.IsNullOrWhiteSpace(inputs.Preamble))
            {
                sb.AppendLine();
                sb.AppendLine(inputs.Preamble.Trim());
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            AppendGuidelines(sb, inputs);
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
        ///
        /// <paramref name="coverage"/>가 <see cref="VerificationCoverage.NeedsHumanAttention"/>이면
        /// Passed여도 "모두 통과"만 말하지 않는다. 종전에는 FloorViolations에 Unverifiable이
        /// 있는지만 봤는데, 단계가 아예 없으면 위반도 없어 플래그가 꺼지고 가장 적게
        /// 검증된 문서가 가장 깨끗한 배지를 달았다(실측 POQSettleProc7). 이제는 분할
        /// 미실행·미검증 단계·문서 오류코드 누락·목차 프로시저 커버리지 누락 네 사유를
        /// 모두 보고, 실제로 해당하는 것만 나열한다 - 해당 없는 사유를 적으면 읽는
        /// 사람이 실제 결함을 흘려보낸다.
        ///
        /// 네 번째 사유는 전체 브랜치 리뷰에서 발견됐다: §0이 ✅로 "모두 통과"를
        /// 말하는 바로 위에 [커버리지 누락]/[커버리지 검증 불가] 배너가 원본
        /// 프로시저 몇 개가 문서 어디에도 없다고 경고하는 문서가 나올 수 있었다 -
        /// 이 문서 내부의 두 표면이 서로 모순되는, 이 브랜치가 없애려던 것과
        /// 정확히 같은 모양의 결함이다.
        /// </summary>
        public static string PlanVerificationSection(VerificationOutcome planOutcome, VerificationCoverage? coverage)
        {
            var label = VerificationDocumentFormatter.StatusLabel(planOutcome);
            var sb = new StringBuilder();

            if (planOutcome == VerificationOutcome.Passed)
            {
                var attention = coverage?.NeedsHumanAttention ?? false;
                sb.AppendLine(attention ? "## ⚠️ 0. 이 계획서의 검증 상태" : "## ✅ 0. 이 계획서의 검증 상태");
                sb.AppendLine();
                sb.AppendLine($"**{label}** — L1 기계 검증과 L2 AI 교차 리뷰를 모두 통과한 계획입니다.");

                if (attention)
                {
                    // 참인 사유만 싣는다. 해당 없는 사유를 적으면 읽는 사람이 실제
                    // 결함을 흘려보낸다.
                    var reasons = new List<string>();
                    if (!coverage!.SplitRan)
                    {
                        reasons.Add("목차가 단계 목록을 내지 못해 단계 단위 기계 검증이 실행되지 않았고");
                    }
                    else if (coverage.HasUnverifiedSteps)
                    {
                        reasons.Add("대조할 재료가 목차에 없어 검증되지 못한 단계가 있고");
                    }

                    if (coverage!.HasDocumentCodeGap)
                    {
                        reasons.Add("원본 오류코드 일부가 문서에서 확인되지 않았고");
                    }

                    // 오케스트레이터의 CoverageUnverifiable(대조 자체를 못 돌림)과
                    // UncoveredProcedures(대조는 돌았지만 일부가 빠짐)가 이 플래그
                    // 하나로 합쳐져 들어온다 - 두 배너는 if/else if로 이미 상호
                    // 배타적이라 §0이 사유를 갈라 말할 필요는 없지만, 문구까지 아무렇게나
                    // 골라도 되는 것은 아니다. "나타나지 않았다"처럼 부재를 단정하면
                    // CoverageUnverifiable 쪽에서 거짓이 된다 - 그 배너 자체가 "문서가 그
                    // 프로시저들을 다루지 않았다는 뜻은 아닙니다"라고 명시한다
                    // (VerificationBanner.CoverageUnverifiable 참고). "확인되지 않았다"는
                    // 대조가 아예 안 돈 경우(확인할 방법이 없었다)와 대조는 돌았지만 특정
                    // 프로시저가 빠진 경우(확인해 봤지만 못 찾았다) 양쪽에서 그대로 참이므로
                    // 이 문구를 쓴다.
                    if (coverage.HasUncoveredProcedures)
                    {
                        reasons.Add("원본 프로시저 커버리지가 확인되지 않았고");
                    }

                    // 사유가 둘 이상이면 쉼표로 잇는다 - 공백만으로는 한 사유가
                    // 어디서 끝나고 다음이 시작하는지 눈으로 가르기 어렵다.
                    var joined = string.Join(", ", reasons);
                    if (joined.EndsWith("고"))
                    {
                        joined = joined[..^1] + "습니다.";
                    }

                    sb.AppendLine($"다만 {joined} 구현 전에 사람의 확인이 필요합니다.");
                }

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

        private static void AppendGuidelines(StringBuilder sb, EntryPointInputs inputs)
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
            sb.AppendLine(TaskletInheritanceGuideline(inputs.TargetLanguage));
            sb.AppendLine(ContractAuthorityGuideline(inputs.TargetLanguage));
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

        /// <summary>
        /// C#은 파일 하나(AbstractSettleTasklet.cs)만 언급하면 되지만, Java는 확장 표면의
        /// 타입들이 여러 public 파일로 나뉜다(MetadataExporter/TaskFileComposer 참고).
        /// 언어와 무관하게 ".cs" 파일명을 하드코딩하면 Java 에이전트가 C# 파일을
        /// 배치하라는, 존재하지도 않는 지시를 받는다.
        /// </summary>
        private static string TaskletInheritanceGuideline(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? "9. **[중요]** 모든 Tasklet 클래스는 사전에 제공된 `src/AbstractSettleTasklet.java`의 `AbstractSettleTasklet`을 강제로 상속받아 구현해야 합니다. 함께 제공되는 `src/ISettleStep.java`, `src/SettleContext.java`, `src/StepResult.java`, `src/IDbConnectionFactory.java`, `src/ICheckpointRepository.java`, `src/ISettleStepDescriptor.java`, `src/ISettleRepository.java`는 모두 `com.reset.batch.core` 패키지 소속이므로 `src/main/java/com/reset/batch/core/` 아래에 패키지 경로와 일치시켜 배치하십시오. 임의의 구조를 만들거나 에러코드를 자의적으로 변경하지 마십시오."
                : "9. **[중요]** 모든 Tasklet 클래스는 사전에 제공된 `src/AbstractSettleTasklet.cs`의 `AbstractSettleTasklet`을 강제로 상속받아 구현해야 합니다. 임의의 구조를 만들거나 에러코드를 자의적으로 변경하지 마십시오.";

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
