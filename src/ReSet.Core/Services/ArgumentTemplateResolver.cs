using System.IO;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코딩 엔진 인자 템플릿의 자리표시자를 절대 경로로 치환한다.
    ///
    /// 프로세스를 띄우지 않고 검증할 수 있도록 ExternalCliCodingEngine에서 분리했다.
    /// 경로에 공백이 있을 수 있으므로 치환값은 항상 쌍따옴표로 감싼다.
    /// </summary>
    public static class ArgumentTemplateResolver
    {
        public static string Resolve(string argumentsTemplate, string instructionsFilePath)
        {
            var instructions = Path.GetFullPath(instructionsFilePath);
            var jobDir = ResolveJobDirectory(instructions);

            return argumentsTemplate
                .Replace("{instructions}", Quote(instructions))
                .Replace("{jobDir}", Quote(jobDir));
        }

        /// <summary>
        /// 지시서는 &lt;job&gt;/agent/MigrationInstructions.md에 놓이므로 두 단계 위가 Job 루트다.
        /// 관례 밖 경로가 들어와도 던지지 않고 올라갈 수 있는 만큼만 올라간다.
        /// </summary>
        public static string ResolveJobDirectory(string instructionsFilePath)
        {
            var full = Path.GetFullPath(instructionsFilePath);

            var agentDir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(agentDir))
            {
                return full;
            }

            var jobDir = Path.GetDirectoryName(agentDir);
            return string.IsNullOrEmpty(jobDir) ? agentDir : jobDir;
        }

        private static string Quote(string path) => $"\"{path}\"";
    }
}
