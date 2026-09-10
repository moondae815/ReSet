using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// <c>scripts/promote-l1-attempts.sh</c> 가 심링크된 <c>output/</c> 을 실제로
    /// 따라가는지 잠근다.
    ///
    /// [왜 필요한가 - 2026-09-10 최종 리뷰 MINOR 12] 그 스크립트를 고친 이유(BSD
    /// <c>find</c> 는 <c>-L</c> 없이는 심링크인 시작 디렉터리를 따라가지 않고도 종료
    /// 코드 0 을 낸다 - 진짜 데이터가 있어도 「승격한 객체 0 개」가 조용히 성공으로
    /// 찍힌다)를 스크립트 자신의 주석이 설명하지만, 그것을 지키는 시험이 없었다.
    /// 고침은 옳았지만 <c>-L</c> 을 실수로 다시 빼도 아무것도 안 빨개지는 상태였다 -
    /// 이 시험이 그 자리를 되돌림으로 확인하고 닫는다(-L 을 빼면 이 시험만 빨개진다).
    ///
    /// 실물 저장소 경로를 안 건드리려고, 스크립트를 임시 가짜 저장소 트리로 복사해
    /// 돌린다 - 스크립트는 자기 위치(<c>BASH_SOURCE</c>)를 기준으로 <c>repo_root</c> 를
    /// 계산하므로 이 방법으로 완전히 격리된다. 실물 코퍼스·output/ 은 손대지 않는다.
    /// </summary>
    public class PromoteL1AttemptsScriptTests
    {
        [SkippableFact]
        public void PromotesFromASymlinkedOutputDirectory()
        {
            Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                "bash·심링크 전용 시나리오다 - Windows 에서는 건너뛴다.");

            var repoRoot = RepoPaths.FindRepoRoot();
            var realScript = Path.Combine(repoRoot, "scripts", "promote-l1-attempts.sh");

            var fakeRepo = Path.Combine(Path.GetTempPath(), "promote-script-fake-repo-" + Guid.NewGuid().ToString("N"));
            var realOutputElsewhere = Path.Combine(Path.GetTempPath(), "promote-script-real-output-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(fakeRepo, "scripts"));
                Directory.CreateDirectory(Path.Combine(fakeRepo, "tests", "ReSet.Core.Tests", "Fixtures", "rejected-attempts"));

                var copiedScript = Path.Combine(fakeRepo, "scripts", "promote-l1-attempts.sh");
                File.Copy(realScript, copiedScript);
                // 위 Skip.If 가 Windows 를 이미 걸렀다 - CA1416 분석기는 그 사실을
                // 정적으로 못 보므로 여기서만 좁게 억제한다.
#pragma warning disable CA1416
                File.SetUnixFileMode(copiedScript,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416

                // 실물처럼 output/ 을 심링크로 만든다 - 표준 워크트리가 재생성 산출물을
                // 공유하려고 거는 것과 같은 모양이다.
                Directory.CreateDirectory(Path.Combine(realOutputElsewhere, "Procedures", "dbo.FAKE_PROC", "raw"));
                File.WriteAllText(
                    Path.Combine(realOutputElsewhere, "Procedures", "dbo.FAKE_PROC", "raw", "l1-attempts.json"),
                    "[]");
                Directory.CreateSymbolicLink(Path.Combine(fakeRepo, "output"), realOutputElsewhere);

                var (exitCode, stdout, stderr) = RunBash(copiedScript);

                Assert.True(exitCode == 0, $"종료 코드 {exitCode}, stderr: {stderr}");
                Assert.Contains("승격한 객체 1 개", stdout);
                Assert.True(File.Exists(Path.Combine(
                    fakeRepo, "tests", "ReSet.Core.Tests", "Fixtures", "rejected-attempts",
                    "dbo.FAKE_PROC", "attempts.json")),
                    "심링크를 따라간 산출물이 코퍼스로 승격되지 않았습니다 - find 가 -L 을 잃었을 수 있습니다.");
            }
            finally
            {
                TryDelete(fakeRepo);
                TryDelete(realOutputElsewhere);
            }
        }

        private static (int ExitCode, string Stdout, string Stderr) RunBash(string scriptPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(scriptPath);

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, stdout, stderr);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // 정리 실패는 시험 결과와 무관하다 - CliStubScript.Dispose 와 같은 관례.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
