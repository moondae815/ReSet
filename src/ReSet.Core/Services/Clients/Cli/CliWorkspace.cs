using System;
using System.IO;
using System.Text;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// CLI 호출 1회분의 빈 임시 작업 디렉토리.
    ///
    /// CLI를 ReSet 프로젝트 디렉토리에서 그냥 띄우면 CLAUDE.md와 AGENTS.md(53KB)를
    /// 자동으로 읽어 컨텍스트에 얹는다. 분석 품질을 오염시키고 구독 쿼터를 낭비한다.
    /// 호출마다 빈 디렉토리를 만들어 그곳을 작업 디렉토리로 준다.
    /// </summary>
    public sealed class CliWorkspace : IDisposable
    {
        public string Path { get; }

        public CliWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"reset-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string WriteFile(string fileName, string content)
        {
            var filePath = System.IO.Path.Combine(Path, fileName);
            // Encoding.UTF8은 BOM을 붙인다. 시스템 프롬프트 파일 맨 앞에 보이지 않는
            // 문자가 들어가면 모델이 그것까지 지시로 읽는다.
            File.WriteAllText(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return filePath;
        }

        public void Dispose()
        {
            // 정리 실패가 분석 결과를 무효화해서는 안 된다. 임시 디렉토리는
            // OS가 언젠가 회수한다. 넓은 catch를 쓰지 않도록 타입을 좁게 잡는다.
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
