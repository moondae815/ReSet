using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실행 가능한 CLI 스텁 스크립트를 임시 디렉터리에 만든다.
    ///
    /// 세 CLI 클라이언트는 생성자로 받은 command를 그대로 프로세스로 띄운다. 그래서
    /// 스텁을 "직접 실행 가능한 파일"로 만들어 command 자리에 넘기면 인자 조립, 결과
    /// 파일 읽기, 실패 분류까지 실제 경로를 그대로 통과시킬 수 있다.
    ///
    /// 플랫폼 분기는 CliProcessRunnerTests / ExternalCliCodingEngineTests와 같은 방식이다
    /// (POSIX는 sh 스크립트, Windows는 .cmd). 진짜 claude/codex/agy 바이너리는 절대 부르지 않는다.
    /// </summary>
    internal sealed class CliStubScript : IDisposable
    {
        private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _directory;

        /// <summary>클라이언트의 command 인자로 넘길 스텁 실행 파일 경로.</summary>
        public string Path { get; }

        private CliStubScript(string directory, string path)
        {
            _directory = directory;
            Path = path;
        }

        public static CliStubScript Create(string posixBody, string windowsBody)
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"reset-cli-stub-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var script = System.IO.Path.Combine(directory, "stub.cmd");
                File.WriteAllText(script, "@echo off\r\n" + windowsBody.ReplaceLineEndings("\r\n"), NoBom);
                return new CliStubScript(directory, script);
            }

            var shellScript = System.IO.Path.Combine(directory, "stub.sh");
            File.WriteAllText(shellScript, "#!/bin/sh\n" + posixBody.ReplaceLineEndings("\n"), NoBom);

            // 실행 비트가 없으면 execve가 EACCES로 거절한다.
            File.SetUnixFileMode(
                shellScript,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            return new CliStubScript(directory, shellScript);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // 정리 실패는 테스트 결과와 무관하다.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
