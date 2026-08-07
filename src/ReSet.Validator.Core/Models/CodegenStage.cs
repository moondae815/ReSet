using System.Collections.Generic;
using System.IO;
using ReSet.Core.Services;
using Serilog;

namespace ReSet.Validator.Core.Models
{
    /// <param name="Id">"01-S01" 형태. progress.json의 회차 식별자와 같다.</param>
    /// <param name="StepCode">단계 회차면 파일명에서 되짚어낸 정화된 코드(progress.json의 StepCode와 같은 값), 아니면 null.</param>
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

            // task-*.md 파일명에는 정화된 단계 코드가 들어가지만, steps/{코드}.md
            // 자체는 InstructionBundleWriter가 원본(비정화) 코드로 쓴다(Task 8에서
            // 알려진 대로 그 경로는 정화하지 않는다). StepSpecPath를 파일명에서
            // 되짚어낸 정화된 코드로 조합하면 실제로는 없는 파일을 가리킨다 -
            // 그래서 원본 코드가 필요한 곳(StepSpecPath)에는 bundle.StepCodes를 쓴다.
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
                    string? rawStepCode;
                    if (stepIndex < bundle.StepCodes.Count)
                    {
                        rawStepCode = bundle.StepCodes[stepIndex];
                    }
                    else
                    {
                        // 계약 위반이다 - Step 회차 파일 수가 원본 코드 목록보다 많다.
                        // 위치로 못 짝지으므로 파일명에서 되짚어낸 값으로 물러서되,
                        // 조용히 넘어가지 않고 남긴다.
                        Log.Warning(
                            "회차 목록과 원본 단계 코드 개수가 어긋났습니다 - StepIndex: {StepIndex}, StepCodesCount: {StepCodesCount}, TaskFile: {TaskFile}",
                            stepIndex, bundle.StepCodes.Count, taskPath);
                        rawStepCode = identity.StepCode;
                    }

                    specPath = bundle.StepsSplit && rawStepCode != null
                        ? Path.Combine(agentDir, "steps", $"{rawStepCode}.md")
                        : null;

                    stepIndex++;
                }

                stages.Add(new CodegenStage(identity.Id, identity.Kind, taskPath, identity.StepCode, specPath));
            }

            return new CodegenStagePlan(stages);
        }
    }
}
