using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients.Cli;
using ReSet.Validator.Core.Models;
using Serilog;
// ReSet.Core.Services에도 같은 이름의 형식이 있어 모호해진다. 이 파일이 다루는 것은
// 검증기가 돌려주는 쪽이므로 그것으로 고정한다(CodeVerificationOrchestrator와 같은 처방).
using ValidationResult = ReSet.Validator.Core.Models.ValidationResult;

namespace ReSet.Validator.Core.Services
{
    public class CodegenWorkflowOrchestrator
    {
        // 산출물 없는 재시도는 명령이 바뀌지 않으므로 다음 시도도 같은 실패를 낸다.
        // L1/L2 검증이 주던 페이싱이 이 경로에는 없어 초 단위로 프로세스를 계속 띄울 수
        // 있으므로, 연속 발생 횟수를 여기서 캡으로 막는다. MaxL2Attempts가 "unlimited"여도
        // 이 캡은 예외 없이 적용된다.
        private const int MaxConsecutiveNoArtifactRetries = 2;

        // 회차의 검증 대상을 찾지 못한 재시도에도 같은 성격의 캡이 필요하다. 이쪽은 산출물이
        // 나왔으므로 위 카운터가 리셋되어 걸리지 않는데, 지시서에 피드백을 붙여도 에이전트가
        // 계속 그 파일을 만들지 못하면 진전 없는 기동이 반복된다. MaxL2Attempts가
        // "unlimited"(= int.MaxValue)면 그것이 무인 배치에서 끝나지 않는 유료 기동이 된다.
        private const int MaxConsecutiveUnverifiedRetries = 2;

        // AbortReason은 콘솔에 그대로 찍힌다(Program.cs). 로그 파일에는 자르지 않은 원문을
        // 남기고, 콘솔에는 길이를 제한해 CLI stderr 원문이 화면을 뒤덮지 않게 한다.
        private const int ConsoleAbortReasonMaxLength = 800;

        private readonly ICodingEngine _codingEngine;
        private readonly CodeVerificationOrchestrator _verifier;
        private readonly IMetadataExporter _metadataExporter;
        private readonly int _maxL2Attempts;

        public CodegenWorkflowOrchestrator(
            ICodingEngine codingEngine,
            CodeVerificationOrchestrator verifier,
            IMetadataExporter metadataExporter,
            int maxL2Attempts)
        {
            _codingEngine = codingEngine;
            _verifier = verifier;
            _metadataExporter = metadataExporter;
            _maxL2Attempts = maxL2Attempts;
        }

        public async Task<CodegenWorkflowResult> RunSelfHealingWorkflowAsync(
            string jobOrSpName,
            string instructionsFilePath, // agent/MigrationInstructions.md
            string specDir,              // 설계서가 있는 폴더
            string codeDir,              // 에이전트가 코드를 생성할 워킹 디렉터리
            bool isBatchMode,
            CancellationToken cancellationToken)
        {
            int attempt = 1;
            int maxAttempts = _maxL2Attempts == -1 ? int.MaxValue : _maxL2Attempts;
            bool isSuccess = false;

            // Finding 3: 연속 무산출물 재시도 카운터. 산출물이 한 번이라도 나오면 리셋된다.
            int consecutiveNoArtifactRetries = 0;

            // Finding 4: 산출물을 한 번도 만들지 못한 채 maxAttempts를 소진했을 때 마지막
            // 실행 결과로 이유를 설명하기 위해 보관한다. 지금까지 산출물이 한 번이라도
            // 있었다면(검증까지 갔다면) 이 경로를 타지 않는다.
            CodegenRunResult? lastRun = null;
            bool everProducedArtifacts = false;

            while (attempt <= maxAttempts)
            {
                Log.Information("[SelfHealing] 자가 수정 루프 시작 - 시도: {Attempt}/{MaxAttempts}, 대상: {Target}", attempt, _maxL2Attempts == -1 ? "무제한" : maxAttempts.ToString(), jobOrSpName);

                // 1. External Coding Engine 기동 (Actor)
                var run = await _codingEngine.GenerateCodeAsync(null, instructionsFilePath, codeDir, cancellationToken);
                lastRun = run;

                var decision = CodegenLoopPolicy.Decide(run);

                if (decision == CodegenLoopDecision.Abort)
                {
                    return BuildAbortResult(run, "[SelfHealing] 재시도해도 결과가 같은 실패입니다. 루프를 중단합니다.");
                }

                if (decision == CodegenLoopDecision.RetryWithoutValidation)
                {
                    // 검증할 산출물이 없다. 지시서도 손대지 않고 그대로 재시도한다.
                    consecutiveNoArtifactRetries++;
                    Log.Warning(
                        "[SelfHealing] 코딩 에이전트가 산출물을 남기지 않았습니다. 검증을 건너뛰고 다음 시도를 준비합니다. " +
                        "(종료 코드: {ExitCode}, 분류: {FailureKind}, 연속 무산출물 재시도: {Consecutive}/{Cap})",
                        run.ExitCode, run.FailureKind, consecutiveNoArtifactRetries, MaxConsecutiveNoArtifactRetries);

                    if (consecutiveNoArtifactRetries >= MaxConsecutiveNoArtifactRetries)
                    {
                        // 지시서도 명령도 바뀌지 않았으니 더 반복해도 같은 실패다.
                        // L1/L2 검증이 주던 페이싱이 없는 경로이므로 여기서 직접 끊는다.
                        return BuildAbortResult(
                            run,
                            $"[SelfHealing] 산출물 없는 재시도가 {MaxConsecutiveNoArtifactRetries}회 연속 발생했습니다. " +
                            "명령이 바뀌지 않았으므로 더 반복해도 같은 결과입니다. 루프를 중단합니다.");
                    }

                    attempt++;
                    continue;
                }

                consecutiveNoArtifactRetries = 0; // 산출물이 나왔다 - 캡을 리셋한다.
                everProducedArtifacts = true;

                // 2. Validator Core 기동 (Critic) - L1/L2 검증
                Log.Information("[SelfHealing] 생성된 코드에 대해 검증기(Critic) 기동");
                var validationResults = await _verifier.RunVerificationAsync(isBatchMode, cancellationToken);

                // 3. 검증 결과 확인
                var failedResults = validationResults.Where(r => !r.L1Passed || !r.L2Passed).ToList();

                // 빈 목록에 대한 "실패 0건"은 공허하게 참이다. ResolveMappings(config)는
                // SpecDirectory에 BatchMigrationPlan.md가 없거나 소스 트리에서 짝을 찾지
                // 못하면 아무 예외 없이 빈 목록을 돌려주므로, 개수만 보면 코드가 한 줄도
                // 검증되지 않았는데 "모든 검증 통과"로 끝난다. 회차 경로는 이 구멍을
                // 세 곳에서 닫았고, 이 경로만 열려 있었다(메뉴 3에서 브랜치 이전의 모든
                // Job이 여전히 여기로 온다).
                bool nothingVerified = validationResults.Count == 0;
                bool allPassed = !nothingVerified && failedResults.Count == 0;

                if (allPassed)
                {
                    Log.Information("[SelfHealing] 모든 검증 통과 (MATCH)! 루프 종료.");
                    isSuccess = true;
                    break;
                }

                if (nothingVerified)
                {
                    // 에이전트에게 붙일 L1/L2 피드백이 없다(대조 자체를 못 했다).
                    // 조용히 재시도하면 무엇이 잘못됐는지 어디에도 남지 않는다.
                    Log.Error(
                        "[SelfHealing] 검증 대상을 하나도 찾지 못했습니다(통과 아님) - 설계서 디렉터리: {SpecDir}, 소스 디렉터리: {CodeDir}",
                        specDir, codeDir);
                }

                // 4. 실패 시 피드백을 지시서에 Append.
                // 대조 자체를 못 한 경우(failedResults가 비어 있는데 통과도 아닌 경우)는
                // 붙일 L1/L2 결과가 없다 - 머리글만 남는 빈 피드백을 쓰지 않는다.
                if (attempt < maxAttempts)
                {
                    if (failedResults.Count > 0)
                    {
                        Log.Information("[SelfHealing] 검증 실패. 피드백을 지시서에 추가하고 에이전트를 재기동합니다.");

                        if (File.Exists(instructionsFilePath))
                        {
                            await _metadataExporter.AppendFeedbackToInstructionsAsync(
                                instructionsFilePath,
                                BuildCriticFeedback($"## 🚨 [AI L1/L2 Critic Feedback - Attempt {attempt}] 🚨", failedResults),
                                cancellationToken);
                        }
                        else
                        {
                            Log.Warning("[SelfHealing] 지시서 파일을 찾을 수 없습니다: {Path}", instructionsFilePath);
                        }
                    }
                }
                else
                {
                    Log.Warning("[SelfHealing] 최대 재시도 횟수({MaxAttempts}) 도달. 자가 수정을 포기합니다.", maxAttempts);
                }

                attempt++;
            }

            // Finding 4: maxAttempts를 다 썼는데 산출물을 한 번도 만들지 못했다면 그 이유를
            // 반드시 보여준다. 이전에는 여기서 AbortReason이 항상 null이었고, 캡처해 둔
            // stderr(run.Diagnostic)는 버려졌다. 무인 배치에서 가장 흔한 실패 형태가
            // "종료 코드 0, 산출물 없음"이므로 이 경로가 이유 없이 조용히 끝나면 안 된다.
            if (!isSuccess && !everProducedArtifacts && lastRun != null)
            {
                return BuildAbortResult(
                    lastRun,
                    "[SelfHealing] 최대 시도 횟수를 모두 소진했지만 산출물을 단 한 번도 만들지 못했습니다.");
            }

            return new CodegenWorkflowResult(isSuccess, null);
        }

        /// <param name="AbortReason">회차 0 실패 등으로 루프를 끊은 이유. 끝까지 돌았으면 null.</param>
        public sealed record StagedWorkflowResult(
            bool AllPassed,
            IReadOnlyList<string> FailedStepCodes,
            string? AbortReason);

        /// <summary>
        /// 회차를 순서대로 돌린다.
        ///
        /// 이전에는 Job 하나를 한 번의 기동으로 처리했다. 그러면 에이전트가 공통 인프라와
        /// 12개 단계와 조립을 한 세션에서 해야 하고, 중간에 컨텍스트 압축이 반드시 일어나
        /// 의사코드와 오류 코드가 요약으로 뭉개진다 - "축약 없이 100% 완전"이라는 지침을
        /// 구조적으로 지킬 수 없었다.
        ///
        /// 회차 전환은 코딩 엔진에 다른 지시서 경로를 넘기는 것으로 끝난다. 인자 템플릿은
        /// 손대지 않는다. 회차는 반드시 순차로 돈다 - 뒤 회차가 앞 회차의 공통 계약과
        /// 산출물 위에 쌓이므로 동시에 돌리면 서로의 전제를 무너뜨린다.
        ///
        /// 실패 정책: 회차 0(Bootstrap)이 실패하면 즉시 중단한다 - 공통 계약이 없으면
        /// 이후 회차가 성립하지 않는다. 단계 회차가 실패하면 Failed로 기록하고 다음으로
        /// 넘어간다 - 하나가 까다로워도 나머지를 건지고, 사람이 실패한 것만 손볼 수 있다.
        /// 단, 회차와 무관한 환경 실패(할당량 소진·인증 실패·도구 권한 거부)는 다음 회차에서도
        /// 똑같이 실패하므로 남은 회차를 돌리지 않고 끝낸다 - 이때 남은 회차는 Failed가 아니라
        /// Pending(미실행)으로 남아, 재시도할 때 무엇이 실제로 실패한 것인지 구별된다.
        /// </summary>
        public async Task<StagedWorkflowResult> RunStagedWorkflowAsync(
            string jobName,
            CodegenStagePlan stagePlan,
            string agentDir,
            string codeDir,
            bool isBatchMode,
            CancellationToken cancellationToken)
        {
            WarnPreviousProgressIsReplaced(agentDir, stagePlan);

            var progress = AgentProgressStore.Create(
                agentDir,
                jobName,
                stagePlan.Stages
                    .Select(stage => new StageProgress(
                        stage.Id, stage.StepCode, Path.GetFileName(stage.TaskFilePath),
                        StageStatus.Pending, 0, null))
                    .ToList());

            await progress.SaveAsync(cancellationToken);

            Log.Information(
                "[Staged] 회차 실행 시작 - Job: {JobName}, 회차 수: {Total}개 (순차 실행)",
                jobName, stagePlan.Stages.Count);

            foreach (var stage in stagePlan.Stages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 조립 회차 직전에 실패 목록을 확정해 작업 지시서에 실어 준다.
                // 번들 작성 시점에는 아직 아무 회차도 돌지 않아 이 목록이 비어 있었다.
                if (stage.Kind == StageKind.Assembly)
                {
                    await AppendFailedStepsToAssemblyTaskAsync(stage, progress.FailedStepCodes, cancellationToken);
                }

                // 조립 회차의 Job 전체 검증 조건이자, 건너뛸 때 사유에 적을 이름들이다.
                // 자기 자신은 지금 InProgress로 바뀌므로 제외한다.
                var unfinished = progress.Stages
                    .Where(s => s.Id != stage.Id && s.Status != StageStatus.Passed)
                    .Select(s => s.StepCode ?? s.Id)
                    .ToList();

                progress.Mark(stage.Id, StageStatus.InProgress, 0, null);
                await progress.SaveAsync(cancellationToken);

                var outcome = await RunStageAsync(stage, codeDir, isBatchMode, unfinished, cancellationToken);

                progress.Mark(
                    stage.Id,
                    outcome.Passed ? StageStatus.Passed : StageStatus.Failed,
                    outcome.Attempts,
                    outcome.Summary);
                await progress.SaveAsync(cancellationToken);

                if (!outcome.Passed)
                {
                    Log.Warning(
                        "[Staged] 회차 실패 - Id: {StageId}, 시도: {Attempts}회, 사유: {Reason}",
                        stage.Id, outcome.Attempts, outcome.Summary);
                }
                else
                {
                    Log.Information(
                        "[Staged] 회차 통과 - Id: {StageId}, 시도: {Attempts}회, 비고: {Note}",
                        stage.Id, outcome.Attempts, outcome.Summary ?? "-");
                }

                // 환경 실패는 회차의 문제가 아니라 실행 전체의 문제다. 남은 회차를 각각 같은
                // 벽에 부딪히게 두면 회차 수만큼 유료 기동을 낭비하고, 실패 목록도 진짜 원인을
                // 가린다. 여기서 끝내면 남은 회차는 Pending으로 남아 "실패"와 "미실행"이 구별된다.
                if (outcome.FatalFailureKind != null)
                {
                    var reason =
                        $"[Staged] 회차와 무관한 실패({outcome.FatalFailureKind})가 발생해 남은 회차를 진행하지 않습니다. " +
                        $"실행하지 않은 회차는 진행 상태에 미실행(Pending)으로 남습니다. {outcome.Summary}";
                    Log.Error("{Reason}", reason);
                    return new StagedWorkflowResult(false, progress.FailedStepCodes, reason);
                }

                if (!outcome.Passed && stage.Kind == StageKind.Bootstrap)
                {
                    var reason = $"[Staged] 회차 0(공통 인프라)이 실패해 이후 회차를 진행할 수 없습니다. {outcome.Summary}";
                    Log.Error("{Reason}", reason);
                    return new StagedWorkflowResult(false, progress.FailedStepCodes, reason);
                }

                if (!outcome.Passed)
                {
                    Log.Information("[Staged] 실패한 회차를 기록하고 다음 회차로 넘어갑니다 - Id: {StageId}", stage.Id);
                }
            }

            // AllPassed는 실패한 "단계 코드"가 아니라 실패한 "회차"로 판정한다. Bootstrap과
            // Assembly에는 StepCode가 없어 FailedStepCodes에 잡히지 않으므로, 그것으로 판정하면
            // 조립 회차가 실패했는데도 "모든 회차 통과"로 끝난다.
            var failedStageCount = progress.Stages.Count(s => s.Status != StageStatus.Passed);
            var failedSteps = progress.FailedStepCodes;

            Log.Information(
                "[Staged] 회차 실행 완료 - 전체: {Total}개, 실패 회차: {FailedStageCount}개, 실패 단계: {FailedStepCount}개",
                stagePlan.Stages.Count, failedStageCount, failedSteps.Count);

            return new StagedWorkflowResult(failedStageCount == 0, failedSteps, null);
        }

        /// <summary>
        /// 회차 목록의 진실은 방금 쓰인 task 파일에서 파생된 <see cref="CodegenStagePlan"/>이지
        /// 지난 실행이 남긴 progress.json이 아니다. 그래서 재개하지 않고 항상 현재 계획으로
        /// 새로 시작한다 - <see cref="AgentProgressStore.Load"/>는 옛 회차 목록을 그대로 돌려주고
        /// <see cref="AgentProgressStore.Mark"/>는 모르는 식별자를 경고만 남기고 무시하므로,
        /// 계획이 다시 생성돼 단계 집합이 바뀐 뒤 옛 목록을 이어받으면 파일이 없는 회차의
        /// 상태를 들고 있게 되고 새 회차는 아예 기록되지 않는다.
        ///
        /// 다만 지난 기록을 덮어쓴다는 사실 자체는 조용히 넘기지 않는다 - 완료 기록이 소리 없이
        /// 사라지지 않게 하는 것이 진행 상태 파일의 존재 이유이기 때문이다. 회차 구성이 그대로인
        /// 재실행(크래시 후 재기동 - 가장 흔하고 가장 비싼 경우)에도 통과 기록 N개가 사라지고
        /// 코딩 에이전트가 전 회차에 대해 다시 기동되므로, 구성이 같든 다르든 항상 남긴다.
        /// </summary>
        private static void WarnPreviousProgressIsReplaced(string agentDir, CodegenStagePlan stagePlan)
        {
            var previous = AgentProgressStore.Load(agentDir);
            if (previous == null)
            {
                return;
            }

            // 식별자만 비교하면 같은 서수에 다른 작업 파일이 걸린 변경을 놓친다.
            var previousKeys = previous.Stages.Select(s => $"{s.Id}({s.TaskFileName})").ToList();
            var currentKeys = stagePlan.Stages
                .Select(s => $"{s.Id}({Path.GetFileName(s.TaskFilePath)})")
                .ToList();

            Log.Warning(
                "[Staged] 이전 진행 기록을 대체하고 처음부터 다시 돌립니다 - 이전 회차: {PreviousCount}개(통과 {PassedCount}개), " +
                "회차 구성 변경: {Diverged}, 이전: [{PreviousStages}], 현재: [{CurrentStages}]",
                previous.Stages.Count,
                previous.Stages.Count(s => s.Status == StageStatus.Passed),
                !previousKeys.SequenceEqual(currentKeys, StringComparer.Ordinal),
                string.Join(", ", previousKeys),
                string.Join(", ", currentKeys));
        }

        /// <param name="Summary">
        /// 진행 기록에 남길 한 줄. 실패 사유이거나, 통과했더라도 남길 비고다
        /// (예: 조립 회차가 Job 전체 검증을 건너뛴 사유).
        /// </param>
        /// <param name="FatalFailureKind">
        /// 회차가 아니라 실행 환경이 실패한 경우의 분류. 채워져 있으면 남은 회차를 돌리지 않는다.
        /// </param>
        private sealed record StageOutcome(
            bool Passed, int Attempts, string? Summary, CliFailureKind? FatalFailureKind);

        private enum StageGateResult
        {
            /// <summary>검증을 통과했거나, 대조할 것이 없어 산출물 생성만으로 통과한 회차.</summary>
            Passed,

            /// <summary>검증까지 갔고 떨어졌다. 피드백을 붙여 다시 시도할 값어치가 있다.</summary>
            VerificationFailed,

            /// <summary>검증 대상을 찾지 못해 판정 자체를 못 했다. 재시도하되 상한을 따로 센다.</summary>
            NotVerifiable,

            /// <summary>재시도로 달라질 것이 없는 실패. 그 자리에서 회차를 접는다.</summary>
            Unrecoverable,
        }

        /// <param name="Feedback">회차 지시서 끝에 붙일 교정 요청. 붙일 말이 없으면 null.</param>
        private sealed record StageGate(StageGateResult Result, string? Summary, string? Feedback);

        /// <summary>
        /// 회차 하나를 재시도와 함께 돌린다. 재시도 규율은 기존 자가 수정 루프와 같다 -
        /// 산출물이 없으면 검증을 건너뛰고, 연속 무산출물이 캡에 닿으면 그 회차를 접는다.
        /// </summary>
        /// <param name="unfinishedStages">
        /// 이 회차 시작 시점에 아직 통과하지 못한 다른 회차들. 조립 회차가 Job 전체 검증을
        /// 걸지 말지를 이것으로 정한다. 다른 종류의 회차는 쓰지 않는다.
        /// </param>
        private async Task<StageOutcome> RunStageAsync(
            CodegenStage stage,
            string codeDir,
            bool isBatchMode,
            IReadOnlyList<string> unfinishedStages,
            CancellationToken cancellationToken)
        {
            var maxAttempts = _maxL2Attempts == -1 ? int.MaxValue : _maxL2Attempts;
            var consecutiveNoArtifact = 0;
            var consecutiveUnverified = 0;
            string? lastGap = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Log.Information(
                    "[Staged] 회차 기동 - Id: {StageId}, 종류: {Kind}, 시도: {Attempt}/{MaxAttempts}, 지시서: {TaskFile}",
                    stage.Id, stage.Kind, attempt, _maxL2Attempts == -1 ? "무제한" : maxAttempts.ToString(),
                    Path.GetFileName(stage.TaskFilePath));

                var run = await _codingEngine.GenerateCodeAsync(
                    null, stage.TaskFilePath, codeDir, cancellationToken);

                var decision = CodegenLoopPolicy.Decide(run);

                if (decision == CodegenLoopDecision.Abort)
                {
                    // CodegenLoopPolicy가 Abort를 내는 것은 할당량 소진·인증 실패·도구 권한
                    // 거부뿐이다. 전부 이 회차가 아니라 실행 환경의 문제이므로 분류를 위로 올린다.
                    return new StageOutcome(false, attempt, ReportNoArtifact(stage, run), run.FailureKind);
                }

                if (decision == CodegenLoopDecision.RetryWithoutValidation)
                {
                    consecutiveNoArtifact++;
                    if (consecutiveNoArtifact >= MaxConsecutiveNoArtifactRetries)
                    {
                        return new StageOutcome(false, attempt, ReportNoArtifact(stage, run), null);
                    }

                    continue;
                }

                consecutiveNoArtifact = 0;

                var gate = await EvaluateStageAsync(
                    stage, attempt, isBatchMode, codeDir, unfinishedStages, cancellationToken);

                if (gate.Result == StageGateResult.Passed)
                {
                    return new StageOutcome(true, attempt, gate.Summary, null);
                }

                lastGap = gate.Summary;

                if (gate.Result == StageGateResult.Unrecoverable)
                {
                    return new StageOutcome(false, attempt, gate.Summary, null);
                }

                consecutiveUnverified = gate.Result == StageGateResult.NotVerifiable
                    ? consecutiveUnverified + 1
                    : 0;

                if (gate.Feedback != null && attempt < maxAttempts && File.Exists(stage.TaskFilePath))
                {
                    // 피드백은 회차 작업 파일에 붙는다. 80줄 안팎이라 파일 끝에 붙어도 읽힌다 -
                    // 이전에는 7,800줄 문서의 맨 끝, 가장 읽히지 않는 자리였다.
                    await _metadataExporter.AppendFeedbackToInstructionsAsync(
                        stage.TaskFilePath, gate.Feedback, cancellationToken);
                }

                if (consecutiveUnverified >= MaxConsecutiveUnverifiedRetries)
                {
                    // 피드백을 붙여도 검증 대상이 계속 나타나지 않는다. 여기서 접지 않으면
                    // MaxL2Attempts가 "unlimited"일 때 끝나지 않는다.
                    Log.Error(
                        "[Staged] 검증 대상을 찾지 못한 시도가 {Consecutive}회 연속입니다. 이 회차를 접습니다 - Id: {StageId}",
                        consecutiveUnverified, stage.Id);
                    return new StageOutcome(false, attempt, gate.Summary, null);
                }
            }

            return new StageOutcome(false, maxAttempts, lastGap, null);
        }

        /// <summary>
        /// 회차의 통과 여부를 판정한다.
        ///
        /// 매핑 0건을 통과로 읽지 않는 것이 이 판정의 핵심이다. FileMappingService는
        /// "쌍을 요청하지 않았다"와 "요청한 쌍이 전부 매칭에 실패했다"를 똑같이 빈 목록으로
        /// 돌려주므로(FileMappingService.cs의 마지막 경고 참조), 결과 개수만 보면 그 단계의
        /// 코드가 아예 만들어지지 않았는데도 회차가 초록으로 끝난다. 그래서 요청한 쌍이
        /// 전부 되돌아왔는지를 먼저 확인하고, 하나라도 빠졌으면 실패로 판정한다.
        /// </summary>
        private async Task<StageGate> EvaluateStageAsync(
            CodegenStage stage,
            int attempt,
            bool isBatchMode,
            string codeDir,
            IReadOnlyList<string> unfinishedStages,
            CancellationToken cancellationToken)
        {
            return stage.Kind switch
            {
                // Bootstrap에는 1:1로 대조할 설계서가 없다. 그렇다고 Job 전체 자동 탐색에
                // 맡기면 계획서 전문(모든 단계)을 상대로 공통 인프라만 있는 트리를 검증하게
                // 되어 반드시 MISMATCH가 나고, 그 실패가 하드 중단에 걸려 회차 1이 기동조차
                // 못 한다. 이 회차의 게이트는 "산출물을 남겼는가"까지다.
                StageKind.Bootstrap => PassWithoutVerification(stage),
                StageKind.Assembly => await EvaluateAssemblyAsync(
                    stage, attempt, isBatchMode, unfinishedStages, cancellationToken),
                _ => await EvaluateStepAsync(stage, attempt, isBatchMode, codeDir, cancellationToken),
            };
        }

        private static StageGate PassWithoutVerification(CodegenStage stage)
        {
            Log.Information(
                "[Staged] 대조할 회차 설계서가 없어 산출물 생성만으로 통과 처리합니다 - Id: {StageId}, 종류: {Kind}",
                stage.Id, stage.Kind);
            return new StageGate(StageGateResult.Passed, null, null);
        }

        /// <summary>
        /// 조립 회차는 모든 단계가 통과했을 때만 Job 전체 의미 검증을 건다.
        ///
        /// 회차별 L2의 합이 Job 전체 검증을 대신한다고 해도, 단계들이 하나의 파이프라인으로
        /// 엮였는지는 아무 회차도 보지 않는다. 그래서 마지막에 한 번은 전체를 본다. 다만
        /// 미완성 단계가 하나라도 있으면 그 대조는 성립하지 않는다 - 계획서는 전 단계를
        /// 요구하는데 트리에는 일부가 없으므로 반드시 MISMATCH가 나고, 재시도해도 조립 회차가
        /// 만들 수 없는 것을 요구하는 헛도는 루프가 된다. 그때는 건너뛰되 그 사실을 통과
        /// 기록에 남긴다 - 로그만으로는 전체 검증이 돌았다고 오해하게 된다.
        /// </summary>
        private async Task<StageGate> EvaluateAssemblyAsync(
            CodegenStage stage,
            int attempt,
            bool isBatchMode,
            IReadOnlyList<string> unfinishedStages,
            CancellationToken cancellationToken)
        {
            if (unfinishedStages.Count > 0)
            {
                var skipped =
                    $"Job 전체 검증 건너뜀 - 미완성 회차가 있어 전체 대조가 성립하지 않습니다: {string.Join(", ", unfinishedStages)}";
                Log.Warning("[Staged] {Reason} - Id: {StageId}", skipped, stage.Id);
                return new StageGate(StageGateResult.Passed, skipped, null);
            }

            // 여기서만 자동 탐색(설계서 = BatchMigrationPlan.md)을 쓴다.
            var results = await _verifier.RunVerificationAsync(isBatchMode, null, cancellationToken);

            if (results.Count == 0)
            {
                // 자동 탐색이 0건이면 "검증할 게 없어 통과"와 구별되지 않는다. 단계 회차와
                // 같은 규율을 적용해 통과로 읽지 않는다.
                const string summary = "Job 전체 검증 대상을 찾지 못했습니다 - 계획서와 소스 트리의 매핑을 확인하십시오";
                Log.Error("[Staged] {Reason} - Id: {StageId}", summary, stage.Id);
                return new StageGate(StageGateResult.Unrecoverable, summary, null);
            }

            var failures = results.Where(r => !r.L1Passed || !r.L2Passed).ToList();
            if (failures.Count == 0)
            {
                Log.Information(
                    "[Staged] Job 전체 검증 통과 - Id: {StageId}, 검증한 쌍: {Verified}개", stage.Id, results.Count);
                return new StageGate(StageGateResult.Passed, null, null);
            }

            return new StageGate(
                StageGateResult.VerificationFailed,
                SummarizeGaps(failures),
                BuildCriticFeedback(StageFeedbackHeader(stage, attempt), failures));
        }

        /// <summary>
        /// 단계 회차의 판정.
        ///
        /// 매핑 0건을 통과로 읽지 않는 것이 핵심이다. FileMappingService는 "쌍을 요청하지
        /// 않았다"와 "요청한 쌍이 전부 매칭에 실패했다"를 똑같이 빈 목록으로 돌려주므로,
        /// 결과 개수만 보면 그 단계의 코드가 아예 만들어지지 않았는데도 회차가 초록으로
        /// 끝난다. 요청한 쌍이 전부 되돌아왔는지를 이름으로 대조해 먼저 확인한다.
        /// </summary>
        private async Task<StageGate> EvaluateStepAsync(
            CodegenStage stage, int attempt, bool isBatchMode, string codeDir, CancellationToken cancellationToken)
        {
            if (stage.StepSpecPath == null || stage.StepCode == null)
            {
                // 단계 분할에 실패한 번들이다. 좁힐 범위가 없다고 Job 전체 검증으로 넓히면
                // 회차 분할의 이득이 사라지고, 검증을 건너뛰고 통과시키면 이 태스크가 막으려는
                // 구멍이 방향만 뒤집힌 채 되살아난다. 검증하지 못했으므로 통과가 아니며,
                // 재시도해도 번들이 다시 만들어지지 않는 한 달라지지 않는다.
                var summary =
                    $"{stage.StepCode ?? stage.Id}: 대조할 설계서 경로가 없어 이 회차를 검증할 수 없었습니다";
                Log.Error("[Staged] {Reason} - Id: {StageId}", summary, stage.Id);
                return new StageGate(StageGateResult.Unrecoverable, summary, null);
            }

            // MappedName에는 정화된 코드(StepCode)를 쓴다 - 소스 파일 이름과 대조되는 값이라
            // 파일명에 못 쓰는 문자가 섞이면 안 된다. SpecFilePath도 같은 정화 코드로 조립된
            // 실제 경로(StepSpecPath)를 그대로 쓴다 - steps/{코드}.md와 task-NN-{코드}.md가
            // 이제 같은 정화 결과를 파일명으로 쓰기 때문이다(CodegenStage.cs 참고).
            // 예전에는 steps/만 원본 코드를 써서 두 값이 갈라져 있었고, 그 비대칭이
            // 회차 지시서가 알려 주는 접두사와 게이트가 대조하는 접두사를 어긋나게 했다.
            var pairs = new[] { new ExplicitPair(stage.StepSpecPath, stage.StepCode, null) };

            var results = await _verifier.RunVerificationAsync(isBatchMode, pairs, cancellationToken);

            var unverified = pairs
                .Where(p => !results.Any(r => string.Equals(r.MappedName, p.MappedName, StringComparison.Ordinal)))
                .ToList();

            if (unverified.Count > 0)
            {
                var summary = string.Join(" / ", unverified.Select(DescribeUnverifiedPair));
                Log.Error(
                    "[Staged] 회차 검증을 수행하지 못했습니다(통과 아님) - Id: {StageId}, 요청 쌍: {Requested}개, 검증된 쌍: {Verified}개, 사유: {Reason}",
                    stage.Id, pairs.Length, results.Count, summary);

                // 설계서가 없는 것은 도구 쪽 문제라 에이전트에게 할 말이 없지만, 소스를 찾지
                // 못한 것은 에이전트가 고칠 수 있다. 아무 말 없이 같은 지시서로 다시 기동하면
                // 바이트 단위로 같은 실행이라 결과도 같다.
                var missingSource = unverified.Where(p => File.Exists(p.SpecFilePath)).ToList();

                return new StageGate(
                    StageGateResult.NotVerifiable,
                    summary,
                    missingSource.Count > 0 ? BuildMissingSourceFeedback(stage, attempt, missingSource, codeDir) : null);
            }

            var failures = results.Where(r => !r.L1Passed || !r.L2Passed).ToList();
            if (failures.Count == 0)
            {
                Log.Information(
                    "[Staged] 회차 검증 통과 - Id: {StageId}, 검증한 쌍: {Verified}개", stage.Id, results.Count);
                return new StageGate(StageGateResult.Passed, null, null);
            }

            return new StageGate(
                StageGateResult.VerificationFailed,
                SummarizeGaps(failures),
                BuildCriticFeedback(StageFeedbackHeader(stage, attempt), failures));
        }

        /// <summary>
        /// 검증에 닿지도 못한 쌍의 사유. 둘은 손볼 곳이 서로 다르므로 문구를 나눈다 -
        /// 앞은 에이전트가 이 단계의 코드를 만들지 않은 것이고(다른 파일은 만들었을 수 있다),
        /// 뒤는 도구가 쓰지 않은 설계서를 가리킨 것이라 재시도로 고쳐지지 않는다.
        /// </summary>
        private static string DescribeUnverifiedPair(ExplicitPair pair) =>
            File.Exists(pair.SpecFilePath)
                ? $"{pair.MappedName}: 이 단계의 소스 파일을 찾지 못해 검증할 수 없었습니다"
                : $"{pair.MappedName}: 대조할 설계서가 없습니다 ({pair.SpecFilePath})";

        /// <summary>
        /// "이 회차의 코드를 못 찾았다"는 검증 결과가 아니라 파일 이름 규약의 문제다.
        /// L1/L2 피드백 형식으로는 전달할 내용이 없으므로 별도 문구를 쓴다 - 무엇이 없었고
        /// 어떤 이름이어야 매칭되는지를 그대로 적어 다음 시도가 진전을 낼 수 있게 한다.
        /// </summary>
        private static string BuildMissingSourceFeedback(
            CodegenStage stage, int attempt, IReadOnlyList<ExplicitPair> missing, string codeDir)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"## 🚨 [회차 산출물 확인 실패 - {stage.Id} 시도 {attempt}] 🚨");
            sb.AppendLine("이 회차의 검증 대상 소스 파일을 찾지 못해 검증을 수행하지 못했습니다.");
            sb.AppendLine("아래 규약에 맞는 파일을 만들어야 이 회차가 통과 판정을 받을 수 있습니다.");
            sb.AppendLine();

            foreach (var pair in missing)
            {
                sb.AppendLine($"- 단계 `{pair.MappedName}`: 파일 이름이 `{pair.MappedName}`로 시작해야 합니다 " +
                              $"(예: `{pair.MappedName}Tasklet.cs`).");
            }

            sb.AppendLine();
            sb.AppendLine($"탐색한 디렉터리(하위 폴더 포함): `{codeDir}`");
            sb.AppendLine("대상 확장자는 `.cs` 또는 `.java`입니다.");
            sb.AppendLine();

            return sb.ToString();
        }

        private static string StageFeedbackHeader(CodegenStage stage, int attempt) =>
            $"## 🚨 [AI L1/L2 Critic Feedback - {stage.Id} 시도 {attempt}] 🚨";

        /// <summary>
        /// 산출물을 아예 남기지 않은 회차의 사유. "만들었는데 검증에서 떨어졌다"와 구별되어야
        /// 사람이 어디를 손볼지 알 수 있으므로 문구를 다르게 쓴다. 진행 기록은 todo.md의 한
        /// 줄로 렌더링되므로 짧은 요약만 담고, 구제 안내가 붙은 원문은 로그에 남긴다.
        /// </summary>
        private string ReportNoArtifact(CodegenStage stage, CodegenRunResult run)
        {
            var summary =
                "산출물 없음 - 코딩 에이전트가 이 회차에서 파일을 남기지 않았습니다 " +
                $"(종료 코드 {run.ExitCode}, 분류 {run.FailureKind})";

            Log.Error("[Staged] {Summary} - Id: {StageId}. {AbortReason}", summary, stage.Id, BuildAbortReason(run));

            return summary;
        }

        private static string SummarizeGaps(IReadOnlyList<ValidationResult> failures) =>
            string.Join(" / ", failures.Select(f =>
                !f.L1Passed
                    ? $"{f.MappedName}: L1 {f.L1Message}"
                    : $"{f.MappedName}: L2 {f.GapReport?.OverallStatus}"));

        /// <summary>
        /// L1/L2 실패를 에이전트가 읽을 교정 요청으로 조립한다. 전체 Job 경로와 회차 경로가
        /// 같은 형식을 쓴다 - 두 벌로 두면 한쪽만 고쳐져 같은 결함에 서로 다른 안내가 나간다
        /// (실제로 데이터 액세스 경계 안내의 참조 문구가 한쪽에서만 빠져 있었다).
        /// 회차마다 다른 것은 머리글 한 줄뿐이므로 그것만 인자로 받는다.
        /// </summary>
        private static string BuildCriticFeedback(string header, IReadOnlyList<ValidationResult> failures)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(header);
            sb.AppendLine("다음은 방금 작성한 코드에 대한 자동 검증(Critic) 결과입니다. 이 피드백을 바탕으로 코드를 수정하십시오.");
            sb.AppendLine();

            foreach (var result in failures)
            {
                sb.AppendLine($"### 결함 발견 파일: {result.MappedName}");

                if (!result.L1Passed)
                {
                    sb.AppendLine("**[L1 정적 검증 실패]**");
                    sb.AppendLine($"- 에러 메시지: {result.L1Message}");
                }

                if (!result.L2Passed && result.GapReport != null)
                {
                    var gap = result.GapReport;
                    sb.AppendLine("**[L2 AI 의미론적 검증 실패]**");
                    sb.AppendLine($"- 종합 상태: {gap.OverallStatus}");
                    sb.AppendLine($"- 입력 파라미터 불일치: {gap.InputParametersGap}");
                    sb.AppendLine($"- 출력 데이터셋 불일치: {gap.OutputResultSetsGap}");
                    sb.AppendLine($"- 비즈니스 로직 불일치: {gap.BusinessLogicGap}");
                    sb.AppendLine($"- 예외 및 트랜잭션 불일치: {gap.ExceptionHandlingGap}");
                    sb.AppendLine($"- 데이터 액세스 경계 위반: {gap.DataAccessBoundaryGap} (지시서 5장의 SQL/ORM 경계 규칙 참조)");
                    sb.AppendLine($"- 💡 **수정 제안**: {gap.Suggestions}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// 조립 회차의 작업 지시서 끝에 실패 단계 목록을 붙인다. 파일 전체를 다시 쓰지 않고
        /// 덧붙이기만 하는 이유는 같은 파일에 회차 피드백도 붙기 때문이다 - 다시 쓰면 그것을
        /// 지운다. 한 번의 실행에서 조립 회차 직전에 딱 한 번만 호출되므로 중복될 일이 없고,
        /// 다음 실행에서는 번들 작성이 이 파일을 새로 쓴다.
        /// </summary>
        private static async Task AppendFailedStepsToAssemblyTaskAsync(
            CodegenStage stage, IReadOnlyList<string> failedStepCodes, CancellationToken cancellationToken)
        {
            if (failedStepCodes.Count == 0 || !File.Exists(stage.TaskFilePath))
            {
                return;
            }

            Log.Information(
                "[Staged] 조립 회차 지시서에 미완성 단계를 실었습니다 - Id: {StageId}, 제외할 단계: {FailedSteps}",
                stage.Id, string.Join(", ", failedStepCodes));

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("## 미완성 단계");
            sb.AppendLine();
            sb.AppendLine("아래 단계는 검증을 통과하지 못했습니다. **손대지 마십시오.** 파이프라인에서 제외하고 조립하십시오.");
            sb.AppendLine();
            foreach (var code in failedStepCodes)
            {
                sb.AppendLine($"- `{code}`");
            }
            sb.AppendLine();
            sb.AppendLine("이 단계들이 빠졌으므로 최종 빌드가 깨질 수 있습니다. 그 사실을 숨기지 말고 그대로 두십시오.");
            sb.AppendLine();

            await File.AppendAllTextAsync(stage.TaskFilePath, sb.ToString(), Encoding.UTF8, cancellationToken);
        }

        /// <summary>
        /// 중단 사유를 만들고, 로그에는 원문 그대로, 반환값(콘솔용)에는 잘라서 담는다.
        /// </summary>
        private CodegenWorkflowResult BuildAbortResult(CodegenRunResult run, string logMessage)
        {
            var abortReason = BuildAbortReason(run);

            // 로그 파일에는 자르지 않는다 - 사후 분석의 유일한 전체 진단일 수 있다.
            Log.Error("{LogMessage} {AbortReason}", logMessage, abortReason);

            return new CodegenWorkflowResult(false, TruncateForConsole(abortReason));
        }

        /// <summary>
        /// 중단 안내문은 CliFailureClassifier가 이미 분류별로 갖고 있다.
        /// 같은 말을 두 곳에서 다르게 쓰지 않기 위해 그것을 그대로 가져온다.
        ///
        /// run.FailureKind는 ExternalCliCodingEngine이 실행 직후 이미 분류해 둔 값이라
        /// 여기서 다시 Classify하지 않는다 - 대화형 실행에서는 stderr를 캡처하지 않으므로
        /// (run.Diagnostic이 null) 재분류하면 근거 없이 Unknown으로 되돌아간다.
        /// CliFailureContext.Codegen을 써서 구제책이 CodegenSettings:Engine을 가리키게 한다
        /// (분석 경로용 안내문은 AiSettings:Providers:*를 가리켜 여기서는 틀린 설정 키다).
        /// </summary>
        private string BuildAbortReason(CodegenRunResult run)
        {
            return CliFailureClassifier
                .ToCodegenAbortException(_codingEngine.Name, _codingEngine.Command, run.FailureKind, run.ExitCode, run.Diagnostic)
                .Message;
        }

        /// <summary>
        /// 콘솔은 화면이다. CLI stderr 원문이 수 KB일 수 있으므로 표시 길이를 제한한다.
        /// 잘리지 않은 원문은 이 메서드 호출 전에 이미 Log.Error로 남는다.
        /// </summary>
        private static string TruncateForConsole(string text)
        {
            if (text.Length <= ConsoleAbortReasonMaxLength)
            {
                return text;
            }

            return text.Substring(0, ConsoleAbortReasonMaxLength) +
                $"\n... (콘솔 출력은 {ConsoleAbortReasonMaxLength}자로 잘렸습니다. 전체 내용은 로그 파일을 확인하십시오.)";
        }
    }
}
