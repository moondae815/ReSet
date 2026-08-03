using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// CLI 코딩 에이전트를 헤드리스로 1회 기동한다.
    ///
    /// 취소 처리는 ExternalCliCodingEngine의 검증된 패턴을 따르되, 한 가지가 다르다.
    /// OperationCanceledException을 다른 타입으로 감싸지 않는다. 감싸면 하류의 올바른
    /// 핸들러가 전부 매칭에 실패한다 (2026-08-03-cancellation-policy-design.md).
    /// </summary>
    public static class CliProcessRunner
    {
        public static async Task<CliProcessResult> RunAsync(
            string command,
            IReadOnlyList<string> arguments,
            string? standardInput,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 인코딩을 명시하지 않으면 콘솔 기본 인코딩을 따른다. ReSet의 프롬프트와
                // 산출물은 전부 한글이므로 세 방향 모두 UTF-8로 고정해야 한다.
                // BOM을 붙이면 프롬프트 첫 글자 앞에 보이지 않는 문자가 들어간다.
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };

            // ArgumentList를 쓰면 .NET이 플랫폼별 인용 규칙을 처리한다.
            // 프롬프트에 따옴표와 개행이 섞여 있으므로 직접 조립하면 안 된다.
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };

            // 명령어가 없으면 여기서 Win32Exception이 난다. 호출자가 분류하도록 그대로 올린다.
            process.Start();
            Log.Debug("CLI 프로세스 시작 - Command: {Command}, PID: {Pid}", command, process.Id);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            // stdin에 쓰기 전에 읽기를 먼저 걸어야 한다. 191KB짜리 프롬프트를 밀어넣는
            // 동안 자식이 stdout에 쓰기 시작하면, 읽는 쪽이 없을 때 양쪽이 서로를
            // 기다리며 멈춘다.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                try
                {
                    if (standardInput != null)
                    {
                        await process.StandardInput.WriteAsync(
                            standardInput.AsMemory(), linkedCts.Token);
                    }

                    // 항상 닫는다. 닫지 않으면 자식이 입력을 계속 기다린다.
                    process.StandardInput.Close();
                }
                catch (IOException pipeException)
                {
                    // 자식이 stdin을 다 읽기 전에 끝나면 파이프가 끊겨 EPIPE(IOException)가 난다.
                    // 잘못된 플래그, 미로그인, 즉시 거절 같은 경우가 여기에 해당한다.
                    // ReSet의 실제 프롬프트는 191KB로 파이프 버퍼(보통 64KB)를 넘기므로
                    // 이 경로가 실제로 밟힌다.
                    //
                    // 여기서 예외를 올리면 호출자는 Win32Exception만 잡으므로 "Broken pipe"라는
                    // 원시 메시지가 그대로 사용자에게 간다. 진짜 원인인 종료 코드와 stderr는
                    // 아직 회수되지 않았다. 따라서 판정을 중단하지 않고 WaitForExitAsync로
                    // 내려가 종료 코드와 stderr를 확보해 CliFailureClassifier에 넘긴다.
                    Log.Debug(pipeException,
                        "CLI 표준 입력 파이프가 끊겼습니다. 자식이 먼저 종료한 것으로 보고 종료 코드를 기다립니다 - Command: {Command}",
                        command);

                    TryCloseStandardInput(process, command);
                }

                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKillTree(process, command);

                // 사용자 취소라면 그대로 전파한다. 감싸지 않는다.
                cancellationToken.ThrowIfCancellationRequested();

                Log.Warning("CLI 프로세스 타임아웃 - Command: {Command}, Timeout: {Timeout}초",
                    command, timeout.TotalSeconds);

                return new CliProcessResult
                {
                    ExitCode = -1,
                    StandardOutput = await ReadBestEffortAsync(stdoutTask),
                    StandardError = await ReadBestEffortAsync(stderrTask),
                    TimedOut = true
                };
            }

            var standardOutput = await stdoutTask;
            var standardError = await stderrTask;

            Log.Debug("CLI 프로세스 종료 - Command: {Command}, ExitCode: {ExitCode}",
                command, process.ExitCode);

            return new CliProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = standardOutput,
                StandardError = standardError,
                TimedOut = false
            };
        }

        /// <summary>
        /// 파이프가 이미 끊긴 뒤 표준 입력을 닫는다. 닫기(flush)에서 같은 IOException이
        /// 다시 날 수 있는데, 이 시점에는 닫는 것 자체가 목적이므로 무시해도 안전하다.
        /// </summary>
        private static void TryCloseStandardInput(Process process, string command)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException closeException)
            {
                Log.Debug(closeException,
                    "CLI 표준 입력 닫기 실패 (무시됨) - Command: {Command}", command);
            }
            catch (ObjectDisposedException)
            {
                // 이미 닫힘
            }
        }

        private static void TryKillTree(Process process, string command)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    Log.Debug("CLI 프로세스 트리 강제 종료 - Command: {Command}", command);
                }
            }
            catch (InvalidOperationException)
            {
                // 이미 종료됨
            }
            catch (System.ComponentModel.Win32Exception killException)
            {
                Log.Warning(killException, "CLI 프로세스 강제 종료 실패 (무시됨) - Command: {Command}", command);
            }
        }

        /// <summary>
        /// 타임아웃 경로에서 지금까지 받은 출력만 최선을 다해 회수한다.
        /// 진단용이므로 실패해도 빈 문자열로 넘어간다.
        /// </summary>
        private static async Task<string> ReadBestEffortAsync(Task<string> readTask)
        {
            try
            {
                return await readTask;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return string.Empty;
            }
        }
    }
}
