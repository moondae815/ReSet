using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class ExternalCliCodingEngine : ICodingEngine
    {
        private readonly string _command;
        private readonly string _argumentsTemplate;

        public string Name { get; }

        public ExternalCliCodingEngine(string name, string command, string argumentsTemplate)
        {
            Name = name;
            _command = command;
            _argumentsTemplate = argumentsTemplate;
        }

        public async Task<bool> GenerateCodeAsync(
            SpDefinition? spDef,
            string instructionsFilePath,
            string targetProjectDir,
            CancellationToken cancellationToken)
        {
            // {instructions} 자리표시자를 지시서 절대 경로로 치환
            var absoluteInstructionsPath = Path.GetFullPath(instructionsFilePath);
            var arguments = _argumentsTemplate.Replace("{instructions}", $"\"{absoluteInstructionsPath}\"");
            var workingDir = string.IsNullOrEmpty(targetProjectDir)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(targetProjectDir);

            Log.Information(
                "외부 코딩 에이전트 기동 요청 - Engine: {EngineName}, Command: {Command}, InstructionsFile: {InstructionsFile}, WorkingDir: {WorkingDir}",
                Name, _command, absoluteInstructionsPath, workingDir);
            Log.Debug("외부 코딩 에이전트 Arguments: {Arguments}", arguments);

            if (spDef != null)
            {
                Log.Debug("외부 코딩 에이전트 대상 SP: {SpSchema}.{SpName}", spDef.Schema, spDef.Name);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _command,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    Log.Debug("외부 코딩 에이전트 프로세스 시작됨 - PID: {Pid}", process.Id);

                    // 취소 토큰 감지 시 프로세스 강제 종료 등록
                    using (cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                Log.Warning("취소 신호 수신 - 외부 코딩 에이전트 프로세스 강제 종료 요청 (PID: {Pid})", process.Id);
                                process.Kill(true);
                                Log.Information("외부 코딩 에이전트 프로세스 트리 강제 종료 완료 (PID: {Pid})", process.Id);
                            }
                        }
                        catch (Exception killEx)
                        {
                            Log.Warning(killEx, "외부 코딩 에이전트 프로세스 강제 종료 중 예외 발생 (무시됨)");
                        }
                    }))
                    {
                        // 프로세스가 종료될 때까지 비동기적으로 대기
                        await process.WaitForExitAsync(cancellationToken);
                        var exitCode = process.ExitCode;
                        var success = exitCode == 0;

                        if (success)
                        {
                            Log.Information(
                                "외부 코딩 에이전트 정상 종료 - Engine: {EngineName}, ExitCode: {ExitCode}",
                                Name, exitCode);
                        }
                        else
                        {
                            Log.Warning(
                                "외부 코딩 에이전트 비정상 종료 - Engine: {EngineName}, ExitCode: {ExitCode}",
                                Name, exitCode);
                        }

                        return success;
                    }
                }
            }
            // 취소를 InvalidOperationException으로 감싸면 하류의 올바른 핸들러
            // (Program.cs의 catch (OperationCanceledException))가 전부 매칭에 실패한다.
            // 사용자가 Ctrl-C를 눌러도 "엔진 기동 오류"로 보고되고 작업이 계속된다.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "외부 코딩 에이전트 기동 중 예외 발생 - Engine: {EngineName}, Command: {Command}", Name, _command);
                throw new InvalidOperationException($"외부 코딩 엔진({Name}) 기동 중 오류가 발생했습니다. 명령어가 설치되어 있는지 확인해 주십시오. (오류: {ex.Message})", ex);
            }
        }
    }
}
