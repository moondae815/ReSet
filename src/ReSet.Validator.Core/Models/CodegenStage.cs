using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Validator.Core.Models
{
    /// <param name="Id">"01-S01" 형태. progress.json의 회차 식별자와 같다.</param>
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

            foreach (var taskPath in bundle.TaskFilePaths)
            {
                var baseName = Path.GetFileNameWithoutExtension(taskPath);
                var id = baseName.StartsWith("task-", StringComparison.Ordinal)
                    ? baseName["task-".Length..]
                    : baseName;

                var (kind, stepCode) = Classify(id);

                var specPath = kind == StageKind.Step && bundle.StepsSplit && stepCode != null
                    ? Path.Combine(agentDir, "steps", $"{stepCode}.md")
                    : null;

                stages.Add(new CodegenStage(id, kind, taskPath, stepCode, specPath));
            }

            return new CodegenStagePlan(stages);
        }

        private static (StageKind Kind, string? StepCode) Classify(string id)
        {
            var parts = id.Split('-');
            var tail = parts.Length > 1 ? string.Join("-", parts.Skip(1)) : id;

            return tail switch
            {
                "bootstrap" => (StageKind.Bootstrap, null),
                "assembly" => (StageKind.Assembly, null),
                _ => (StageKind.Step, tail),
            };
        }
    }
}
