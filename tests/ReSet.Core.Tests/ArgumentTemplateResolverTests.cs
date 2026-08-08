using System;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ArgumentTemplateResolverTests
    {
        // 실제 파일이 필요 없다. Path 연산만 쓴다.
        private static string InstructionsPath =>
            Path.Combine(Path.GetTempPath(), "Jobs", "SettleJob", "agent", "MigrationInstructions.md");

        [Fact]
        public void Resolve_ShouldReplaceInstructions_WithRawAbsolutePath()
        {
            // 인용 계약: Resolve는 원문 경로를 그대로 넣는다. 따옴표는 템플릿의 몫이다.
            var resolved = ArgumentTemplateResolver.Resolve("run {instructions}", InstructionsPath);

            Assert.Equal($"run {Path.GetFullPath(InstructionsPath)}", resolved);
        }

        [Fact]
        public void Resolve_ShouldReplaceJobDir_WithRawGrandparentPath()
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Jobs", "SettleJob"));

            var resolved = ArgumentTemplateResolver.Resolve("--add-dir {jobDir}", InstructionsPath);

            Assert.Equal($"--add-dir {expected}", resolved);
        }

        [Fact]
        public void Resolve_ShouldReplaceBothPlaceholders_InOneTemplate()
        {
            var jobDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Jobs", "SettleJob"));
            var instructions = Path.GetFullPath(InstructionsPath);

            var resolved = ArgumentTemplateResolver.Resolve(
                "--add-dir {jobDir} -p \"write code using {instructions}\"", InstructionsPath);

            // 템플릿이 이미 -p 뒤 구절 전체를 따옴표로 감싸고 있으므로, Resolve는 그 안의
            // 자리표시자만 원문으로 바꿔 넣는다. 따옴표는 정확히 한 쌍만 남는다.
            Assert.Equal($"--add-dir {jobDir} -p \"write code using {instructions}\"", resolved);
        }

        [Fact]
        public void Resolve_ShouldNotQuotePaths_ContainingSpaces()
        {
            var spaced = Path.Combine(Path.GetTempPath(), "My Jobs", "Settle Job", "agent", "MigrationInstructions.md");

            var resolved = ArgumentTemplateResolver.Resolve("{instructions}", spaced);

            // Resolve 자신은 따옴표를 절대 씌우지 않는다 - 안 씌우면 템플릿이 자기 따옴표
            // 안에 자리표시자를 넣어도 중첩되지 않는다.
            Assert.DoesNotContain("\"", resolved);
            Assert.Contains("My Jobs", resolved);
        }

        [Fact]
        public void Resolve_ShouldLeaveTemplateUnchanged_WhenNoPlaceholderPresent()
        {
            var resolved = ArgumentTemplateResolver.Resolve("--version", InstructionsPath);

            Assert.Equal("--version", resolved);
        }

        [Fact]
        public void Resolve_ShouldBeSinglePass_WhenSubstitutedPathContainsPlaceholderLiteral()
        {
            // 회귀 방지: 치환값(경로) 안에 우연히 "{jobDir}"라는 글자 그대로가 들어있어도
            // 그 자리를 다시 치환하면 안 된다. 순차 Replace 호출이었다면 이 값이
            // 두 번째 Replace("{jobDir}", ...) 호출에서 다시 걸렸을 것이다.
            var trickyDir = Path.Combine(Path.GetTempPath(), "{jobDir}", "agent", "MigrationInstructions.md");

            var resolved = ArgumentTemplateResolver.Resolve("{instructions} --add-dir {jobDir}", trickyDir);

            var expectedInstructions = Path.GetFullPath(trickyDir);
            var expectedJobDir = ArgumentTemplateResolver.ResolveJobDirectory(trickyDir);
            Assert.Equal($"{expectedInstructions} --add-dir {expectedJobDir}", resolved);
        }

        [Fact]
        public void Resolve_TemplateOwnsQuotes_PathWithSpaceStaysOneArgvToken()
        {
            // 회귀 테스트(Finding 1): 템플릿이 자리표시자를 자기 따옴표로 직접 감싸는 경우
            // (--add-dir "{jobDir}"), 공백이 든 경로가 들어와도 따옴표가 중첩되면 안 된다.
            //
            // 예전 버그: Resolve가 치환값을 또 따옴표로 감싸 --add-dir ""/tmp/My Jobs/Settle Job""
            // 같은 결과가 나왔다. ProcessStartInfo.Arguments(양 OS 모두 Windows 스타일 파싱)는
            // 이를 argv = [--add-dir] [] ["/tmp/My] [Jobs/Settle] [Job"] 로 쪼갠다 -
            // 빈 토큰 하나, 공백마다 끊어진 조각들, 그리고 남은 따옴표까지 뒤섞인 잘못된 결과다.
            //
            // 고쳐진 계약에서는 따옴표가 템플릿이 쓴 단 한 쌍만 남고, argv는
            // [--add-dir] ["/tmp/My Jobs/Settle Job"] -> 파싱하면 하나의 토큰
            // "/tmp/My Jobs/Settle Job" 이 된다.
            var spaced = Path.Combine(Path.GetTempPath(), "My Jobs", "Settle Job", "agent", "MigrationInstructions.md");
            var expectedJobDir = ArgumentTemplateResolver.ResolveJobDirectory(spaced);

            var resolved = ArgumentTemplateResolver.Resolve("--add-dir \"{jobDir}\"", spaced);

            var expected = $"--add-dir \"{expectedJobDir}\"";
            Assert.Equal(expected, resolved);

            // 따옴표는 템플릿이 준비한 여는 것 1개, 닫는 것 1개 - 정확히 2개, 중첩 없음.
            Assert.Equal(2, resolved.Count(c => c == '"'));
        }

        [Fact]
        public void ResolveJobDirectory_ShouldReturnAgentParent_WhenPathIsShallow()
        {
            // 지시서가 관례 밖 위치에 있어도 예외를 던지지 않고 최선의 경로를 돌려준다.
            var shallow = Path.Combine(Path.GetTempPath(), "MigrationInstructions.md");

            var jobDir = ArgumentTemplateResolver.ResolveJobDirectory(shallow);

            Assert.False(string.IsNullOrEmpty(jobDir));
        }

        /// <summary>
        /// Spec.md는 Job 루트의 자손이 아니라 형제다(&lt;outputRoot&gt;/Procedures/... vs
        /// &lt;outputRoot&gt;/Jobs/&lt;job&gt;). {jobDir} 하나만 주는 동안에는 회차마다
        /// UPDATE/INSERT 매핑 수식의 유일한 출처가 무인 배치에서 스코프 밖에 있었다.
        /// </summary>
        [Fact]
        public void ResolveSpecRoot_ShouldPointAtProceduresSiblingOfJobs()
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Procedures"));

            Assert.Equal(expected, ArgumentTemplateResolver.ResolveSpecRootDirectory(InstructionsPath));
        }

        [Fact]
        public void ResolveSpecRoot_ShouldCoverTheSpecLinkTheStepTaskFileEmits()
        {
            // 회차 지시서의 링크는 agent/ 기준 ../../../Procedures/<스키마.이름>/docs/Spec.md다.
            // 그 링크를 실제로 해석한 절대 경로가 부여된 스코프 안에 있어야 한다.
            var agentDir = Path.GetDirectoryName(Path.GetFullPath(InstructionsPath))!;
            var linked = Path.GetFullPath(
                Path.Combine(agentDir, "..", "..", "..", "Procedures", "dbo.UP_A", "docs", "Spec.md"));

            var specRoot = ArgumentTemplateResolver.ResolveSpecRootDirectory(InstructionsPath);

            Assert.StartsWith(specRoot + Path.DirectorySeparatorChar, linked);
        }

        [Fact]
        public void ResolveSpecRoot_ShouldNotGrantTheWholeOutputRoot()
        {
            // 출력 루트를 통째로 주면 다른 Job의 번들과 진행 상태까지
            // --permission-mode acceptEdits의 쓰기 범위에 들어온다.
            var specRoot = ArgumentTemplateResolver.ResolveSpecRootDirectory(InstructionsPath);
            var otherJob = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Jobs", "OtherJob"));

            Assert.False(otherJob.StartsWith(specRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }

        [Fact]
        public void ResolveSpecRoot_ShouldFallBackToJobDir_WhenLayoutIsNotConventional()
        {
            // 관례 밖 경로에서 짐작해 엉뚱한 디렉터리를 여는 것보다 중복 부여가 낫다.
            var unconventional = Path.Combine(Path.GetTempPath(), "Somewhere", "agent", "MigrationInstructions.md");

            Assert.Equal(
                ArgumentTemplateResolver.ResolveJobDirectory(unconventional),
                ArgumentTemplateResolver.ResolveSpecRootDirectory(unconventional));
        }

        [Fact]
        public void Resolve_ShouldReplaceSpecRootPlaceholder_AlongsideJobDir()
        {
            var jobDir = ArgumentTemplateResolver.ResolveJobDirectory(InstructionsPath);
            var specRoot = ArgumentTemplateResolver.ResolveSpecRootDirectory(InstructionsPath);

            var resolved = ArgumentTemplateResolver.Resolve(
                "--add-dir \"{jobDir}\" --add-dir \"{specRoot}\"", InstructionsPath);

            Assert.Equal($"--add-dir \"{jobDir}\" --add-dir \"{specRoot}\"", resolved);
        }
    }
}
