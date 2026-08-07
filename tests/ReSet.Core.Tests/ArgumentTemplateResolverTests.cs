using System.IO;
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
        public void Resolve_ShouldReplaceInstructions_WithQuotedAbsolutePath()
        {
            var resolved = ArgumentTemplateResolver.Resolve("run {instructions}", InstructionsPath);

            Assert.Equal($"run \"{Path.GetFullPath(InstructionsPath)}\"", resolved);
        }

        [Fact]
        public void Resolve_ShouldReplaceJobDir_WithGrandparentOfInstructions()
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Jobs", "SettleJob"));

            var resolved = ArgumentTemplateResolver.Resolve("--add-dir {jobDir}", InstructionsPath);

            Assert.Equal($"--add-dir \"{expected}\"", resolved);
        }

        [Fact]
        public void Resolve_ShouldReplaceBothPlaceholders_InOneTemplate()
        {
            var jobDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Jobs", "SettleJob"));
            var instructions = Path.GetFullPath(InstructionsPath);

            var resolved = ArgumentTemplateResolver.Resolve(
                "--add-dir {jobDir} -p \"write code using {instructions}\"", InstructionsPath);

            Assert.Equal($"--add-dir \"{jobDir}\" -p \"write code using \"{instructions}\"\"", resolved);
        }

        [Fact]
        public void Resolve_ShouldQuotePaths_ContainingSpaces()
        {
            var spaced = Path.Combine(Path.GetTempPath(), "My Jobs", "Settle Job", "agent", "MigrationInstructions.md");

            var resolved = ArgumentTemplateResolver.Resolve("{instructions}", spaced);

            Assert.StartsWith("\"", resolved);
            Assert.EndsWith("\"", resolved);
            Assert.Contains("My Jobs", resolved);
        }

        [Fact]
        public void Resolve_ShouldLeaveTemplateUnchanged_WhenNoPlaceholderPresent()
        {
            var resolved = ArgumentTemplateResolver.Resolve("--version", InstructionsPath);

            Assert.Equal("--version", resolved);
        }

        [Fact]
        public void ResolveJobDirectory_ShouldReturnAgentParent_WhenPathIsShallow()
        {
            // 지시서가 관례 밖 위치에 있어도 예외를 던지지 않고 최선의 경로를 돌려준다.
            var shallow = Path.Combine(Path.GetTempPath(), "MigrationInstructions.md");

            var jobDir = ArgumentTemplateResolver.ResolveJobDirectory(shallow);

            Assert.False(string.IsNullOrEmpty(jobDir));
        }
    }
}
