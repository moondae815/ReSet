using System;
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

namespace ReSet.Validator.Core.Services
{
    public class CodegenWorkflowOrchestrator
    {
        // 산출물 없는 재시도는 명령이 바뀌지 않으므로 다음 시도도 같은 실패를 낸다.
        // L1/L2 검증이 주던 페이싱이 이 경로에는 없어 초 단위로 프로세스를 계속 띄울 수
        // 있으므로, 연속 발생 횟수를 여기서 캡으로 막는다. MaxL2Attempts가 "unlimited"여도
        // 이 캡은 예외 없이 적용된다.
        private const int MaxConsecutiveNoArtifactRetries = 2;

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
                bool allPassed = true;
                var feedbackBuilder = new StringBuilder();
                feedbackBuilder.AppendLine();
                feedbackBuilder.AppendLine($"## 🚨 [AI L1/L2 Critic Feedback - Attempt {attempt}] 🚨");
                feedbackBuilder.AppendLine("다음은 방금 작성한 코드에 대한 자동 검증(Critic) 결과입니다. 이 피드백을 바탕으로 코드를 수정하십시오.");
                feedbackBuilder.AppendLine();

                foreach (var fileResult in validationResults)
                {
                    if (!fileResult.L1Passed || !fileResult.L2Passed)
                    {
                        allPassed = false;
                        feedbackBuilder.AppendLine($"### 결함 발견 파일: {fileResult.MappedName}");
                        
                        if (!fileResult.L1Passed)
                        {
                            feedbackBuilder.AppendLine($"**[L1 정적 검증 실패]**");
                            feedbackBuilder.AppendLine($"- 에러 메시지: {fileResult.L1Message}");
                        }

                        if (!fileResult.L2Passed && fileResult.GapReport != null)
                        {
                            var gap = fileResult.GapReport;
                            feedbackBuilder.AppendLine($"**[L2 AI 의미론적 검증 실패]**");
                            feedbackBuilder.AppendLine($"- 종합 상태: {gap.OverallStatus}");
                            feedbackBuilder.AppendLine($"- 입력 파라미터 불일치: {gap.InputParametersGap}");
                            feedbackBuilder.AppendLine($"- 출력 데이터셋 불일치: {gap.OutputResultSetsGap}");
                            feedbackBuilder.AppendLine($"- 비즈니스 로직 불일치: {gap.BusinessLogicGap}");
                            feedbackBuilder.AppendLine($"- 예외 및 트랜잭션 불일치: {gap.ExceptionHandlingGap}");
                            feedbackBuilder.AppendLine($"- 데이터 액세스 경계 위반: {gap.DataAccessBoundaryGap} (지시서 5장의 SQL/ORM 경계 규칙 참조)");
                            feedbackBuilder.AppendLine($"- 💡 **수정 제안**: {gap.Suggestions}");
                        }
                        feedbackBuilder.AppendLine();
                    }
                }

                if (allPassed)
                {
                    Log.Information("[SelfHealing] 모든 검증 통과 (MATCH)! 루프 종료.");
                    isSuccess = true;
                    break;
                }

                // 4. 실패 시 피드백을 지시서에 Append
                if (attempt < maxAttempts)
                {
                    Log.Information("[SelfHealing] 검증 실패. 피드백을 지시서에 추가하고 에이전트를 재기동합니다.");
                    
                    if (File.Exists(instructionsFilePath))
                    {
                        await _metadataExporter.AppendFeedbackToInstructionsAsync(instructionsFilePath, feedbackBuilder.ToString(), cancellationToken);
                    }
                    else
                    {
                        Log.Warning("[SelfHealing] 지시서 파일을 찾을 수 없습니다: {Path}", instructionsFilePath);
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
