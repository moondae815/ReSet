using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Services;
using ReSet.Validator.Core.Models;
using Serilog;

namespace ReSet.Validator.Core.Services
{
    public class CodegenWorkflowOrchestrator
    {
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

        public async Task<bool> RunSelfHealingWorkflowAsync(
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

            while (attempt <= maxAttempts)
            {
                Log.Information("[SelfHealing] 자가 수정 루프 시작 - 시도: {Attempt}/{MaxAttempts}, 대상: {Target}", attempt, _maxL2Attempts == -1 ? "무제한" : maxAttempts.ToString(), jobOrSpName);

                // 1. External Coding Engine 기동 (Actor)
                bool engineSuccess = await _codingEngine.GenerateCodeAsync(null, instructionsFilePath, codeDir, cancellationToken);
                
                if (!engineSuccess)
                {
                    Log.Warning("[SelfHealing] 코딩 에이전트 비정상 종료. 검증을 건너뛰고 다음 시도를 준비하거나 종료합니다.");
                }

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
                            feedbackBuilder.AppendLine($"- 데이터 액세스 경계 위반: {gap.DataAccessBoundaryGap}");
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

            return isSuccess;
        }
    }
}
