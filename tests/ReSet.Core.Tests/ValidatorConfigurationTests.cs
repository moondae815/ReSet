using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// ReSet.Validator.Cli에는 테스트 프로젝트가 없다. 설정 로드 순서는 실행하지
    /// 않으면 드러나지 않고 조용히 잘못된 provider를 쓰게 만드는 종류라서,
    /// 최소한 소스 수준의 그물은 걸어 둔다(AiServiceTests의 프롬프트 검사와 같은 관례).
    /// </summary>
    public class ValidatorConfigurationTests
    {
        private static string ReadValidatorProgram()
        {
            var fullPath = System.IO.Path.Combine(
                RepoPaths.FindRepoRoot(), "src/ReSet.Validator.Cli/Program.cs");
            return System.IO.File.ReadAllText(fullPath);
        }

        /// <summary>
        /// LoadConfiguration이 ReSet.Cli의 appsettings.local.json을 통째로 병합하면
        /// 안 된다. .NET 구성은 나중에 추가한 소스가 이기므로, 그 파일이 Validator
        /// 자신의 appsettings.json과 appsettings.local.json을 모두 덮는다.
        ///
        /// 실제 피해: ReSet.Cli 쪽에 AiSettings:Provider를 CLI provider로 두면
        /// Validator의 provider까지 바뀌고, 배치 모드에서 CliProviderBatchGuard에
        /// 걸려 ExitCode 1로 죽는다. Validator 전용 local 파일로 되돌리려 해도
        /// 로드 순서상 ReSet.Cli 쪽이 더 나중이라 이길 수 없다.
        ///
        /// 주석이 선언한 의도는 "API Key를 가져오기 위한 대체 탐색"이고,
        /// 그 일은 LoadApiKeyWithFallback이 이미 전담한다.
        /// </summary>
        [Fact]
        public void LoadConfiguration_DoesNotMergeTheCliProjectsLocalSettings()
        {
            var source = ReadValidatorProgram();

            Assert.DoesNotContain("builder.AddJsonFile(Path.GetFullPath(path)", source);
        }

        /// <summary>
        /// 위 제거가 API Key 공유까지 끊으면 안 된다. 그 경로는 남아 있어야 하고,
        /// 가져오는 값은 ApiKey 하나로 한정되어야 한다.
        /// </summary>
        [Fact]
        public void ApiKeyFallback_StillReadsOnlyTheApiKeyFromTheCliProject()
        {
            var source = ReadValidatorProgram();

            Assert.Contains("LoadApiKeyWithFallback", source);
            Assert.Contains("var tempKey = tempConfig[$\"AiSettings:Providers:{provider}:ApiKey\"]", source);
        }
    }
}
