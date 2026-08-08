using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코딩 엔진 인자 템플릿의 자리표시자를 절대 경로로 치환한다.
    ///
    /// 프로세스를 띄우지 않고 검증할 수 있도록 ExternalCliCodingEngine에서 분리했다.
    ///
    /// 인용 계약: 치환값은 원문(raw) 그대로 들어간다. 따옴표는 템플릿이 직접 쥔다.
    /// 예전에는 여기서 치환값을 항상 쌍따옴표로 감쌌는데, appsettings.json의 템플릿들이
    /// 그 자리표시자를 자기 따옴표 "안에" 다시 넣어 두는 바람에(예: -p "...{instructions}...")
    /// 공백이 든 경로에서 따옴표가 중첩되어 argv 중간에 토큰이 끊겼다
    /// (-p "write code using "/tmp/My Output/.../MigrationInstructions.md""
    ///  -> argv: [-p] [write code using /tmp/My] [Output/.../MigrationInstructions.md]).
    /// 이제 템플릿이 "--add-dir {jobDir}"처럼 자리표시자만 감싸면 공백이 있어도 없어도
    /// 하나의 인자로 남는다.
    /// </summary>
    public static class ArgumentTemplateResolver
    {
        // 자리표시자를 한 번에 찾아 한 번만 치환한다(Regex.Replace의 MatchEvaluator는
        // 원본 문자열을 한 번만 스캔한다). Replace를 자리표시자별로 순차 호출하면, 먼저
        // 치환된 값이 우연히 "{jobDir}" 같은 리터럴 문자열을 담고 있을 때 그 값이 다음
        // Replace 호출에서 다시 치환 대상이 될 수 있다.
        private static readonly Regex PlaceholderPattern =
            new Regex("\\{instructions\\}|\\{jobDir\\}|\\{specRoot\\}", RegexOptions.Compiled);

        public static string Resolve(string argumentsTemplate, string instructionsFilePath)
        {
            var instructions = Path.GetFullPath(instructionsFilePath);
            var jobDir = ResolveJobDirectory(instructions);
            var specRoot = ResolveSpecRootDirectory(instructions);

            return PlaceholderPattern.Replace(argumentsTemplate, match => match.Value switch
            {
                "{instructions}" => instructions,
                "{specRoot}" => specRoot,
                _ => jobDir,
            });
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

        /// <summary>
        /// 원본 명세서(Spec.md)가 놓이는 루트. <c>{jobDir}</c>이 덮지 못하는 유일한 링크다.
        ///
        /// OutputPathResolver는 명세서를 &lt;outputRoot&gt;/Procedures/&lt;스키마.이름&gt;/docs/Spec.md에
        /// 두는데, Job 루트는 &lt;outputRoot&gt;/Jobs/&lt;job&gt;이다. 즉 <b>Spec.md는 Jobs/의
        /// 자손이 아니라 형제</b>다(회차 지시서의 링크가 실제로 ../../../Procedures/...로
        /// 시작한다). --add-dir이 Job 루트 하나만 주면, 회차마다 UPDATE/INSERT 매핑 수식의
        /// 유일한 출처인 그 파일이 무인 배치에서 스코프 밖에 있다.
        ///
        /// 출력 루트 전체가 아니라 Procedures/만 준다. 출력 루트를 통째로 주면 다른 Job의
        /// 번들과 진행 상태까지 쓰기 권한 안에 들어온다(배치는 acceptEdits로 돈다).
        ///
        /// 관례를 벗어난 경로(두 단계 위가 Jobs/가 아닌 경우)에서는 Job 루트를 그대로
        /// 돌려준다 - 짐작해서 엉뚱한 디렉터리를 여는 것보다 중복 부여가 낫다.
        /// </summary>
        public static string ResolveSpecRootDirectory(string instructionsFilePath)
        {
            var jobDir = ResolveJobDirectory(instructionsFilePath);
            var jobsDir = Path.GetDirectoryName(jobDir);

            if (string.IsNullOrEmpty(jobsDir) ||
                !string.Equals(Path.GetFileName(jobsDir), "Jobs", StringComparison.OrdinalIgnoreCase))
            {
                return jobDir;
            }

            var outputRoot = Path.GetDirectoryName(jobsDir);
            return string.IsNullOrEmpty(outputRoot) ? jobDir : Path.Combine(outputRoot, "Procedures");
        }
    }
}
