using System.Collections.Generic;
using System.IO;
using ReSet.Core.Services;
using Serilog;

namespace ReSet.Validator.Core.Models
{
    /// <param name="Id">"01-S01" 형태. progress.json의 회차 식별자와 같다.</param>
    /// <param name="StepCode">단계 회차면 파일명에서 되짚어낸 정화된 코드(progress.json의 StepCode와 같은 값), 아니면 null.
    /// 이 값이 곧 검증기가 소스 파일 이름과 대조하는 접두사이며, 회차 지시서가 에이전트에게 알려 주는 접두사와 같다.</param>
    /// <param name="StepSpecPath">이 회차의 검증 대상 설계서. 단계 회차이고 분할에 성공했을 때만 값이 있다.</param>
    public sealed record CodegenStage(
        string Id,
        StageKind Kind,
        string TaskFilePath,
        string? StepCode,
        string? StepSpecPath);

    /// <summary>
    /// 회차 실행 순서. 번들이 실제로 쓴 task 파일에서 파생한다.
    ///
    /// 회차 수를 두 곳에서 각자 세지 않는다 - 파일이 없는 회차를 실행하거나
    /// 파일이 있는데 실행하지 않는 어긋남을 구조적으로 막는다.
    /// </summary>
    public sealed record CodegenStagePlan(IReadOnlyList<CodegenStage> Stages)
    {
        public static CodegenStagePlan FromBundle(BundleResult bundle, string agentDir)
        {
            var stages = new List<CodegenStage>();

            // steps/{코드}.md와 task-NN-{코드}.md는 이제 같은 정화 결과를 파일명으로
            // 쓴다(TaskFileComposer.SanitizeStepCode를 양쪽이 함께 쓴다). 그래서
            // bundle.StepCodes[i]와 파일명에서 되짚어낸 identity.StepCode는 같은 값이다.
            // 위치 짝짓기는 그 등식이 깨졌을 때(회차 파일 수와 코드 수가 어긋났을 때)를
            // 잡아내는 가드로 남긴다.
            //
            // bundle.TaskFilePaths와 bundle.StepCodes는 InstructionBundleWriter.WriteAsync가
            // 부트스트랩 → (같은 foreach로) 단계별 → 조립 순서로 함께 채워 넣으므로,
            // TaskFilePaths에 나타나는 Step 회차의 순서는 StepCodes의 순서와 위치로
            // 정렬되어 있다. 그래서 파일명을 다시 파싱하지 않고 위치로 짝짓는다.
            var stepIndex = 0;

            foreach (var taskPath in bundle.TaskFilePaths)
            {
                var baseName = Path.GetFileNameWithoutExtension(taskPath);
                var identity = TaskFileComposer.ParseStageIdentity(baseName);

                string? specPath = null;

                if (identity.Kind == StageKind.Step)
                {
                    string? stepFileCode;
                    if (stepIndex < bundle.StepCodes.Count)
                    {
                        stepFileCode = bundle.StepCodes[stepIndex];
                    }
                    else
                    {
                        // 계약 위반이다 - Step 회차 파일 수가 단계 코드 목록보다 많다.
                        // 위치로 못 짝지으므로 파일명에서 되짚어낸 값으로 물러서되,
                        // 조용히 넘어가지 않고 남긴다.
                        Log.Warning(
                            "회차 목록과 단계 코드 개수가 어긋났습니다 - StepIndex: {StepIndex}, StepCodesCount: {StepCodesCount}, TaskFile: {TaskFile}",
                            stepIndex, bundle.StepCodes.Count, taskPath);
                        stepFileCode = identity.StepCode;
                    }

                    specPath = bundle.StepsSplit && stepFileCode != null
                        ? Path.Combine(agentDir, "steps", $"{stepFileCode}.md")
                        : null;

                    stepIndex++;
                }

                stages.Add(new CodegenStage(identity.Id, identity.Kind, taskPath, identity.StepCode, specPath));
            }

            return new CodegenStagePlan(stages);
        }
    }
}
