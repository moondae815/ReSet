using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services.Clients.Cli;
using Serilog;

namespace ReSet.Core.Services
{
    public class ExternalCliCodingEngine : ICodingEngine
    {
        private readonly string _command;
        private readonly string _argumentsTemplate;
        private readonly bool _isHeadless;

        public string Name { get; }

        public string Command => _command;

        /// <summary>팩토리가 모드에 맞게 골라 넣은 인자 템플릿. 로깅과 테스트가 읽는다.</summary>
        public string ArgumentsTemplate => _argumentsTemplate;

        /// <summary>무인 배치로 기동하는가. 스트림 처리 방식이 갈린다.</summary>
        public bool IsHeadless => _isHeadless;

        public ExternalCliCodingEngine(string name, string command, string argumentsTemplate, bool isHeadless)
        {
            Name = name;
            _command = command;
            _argumentsTemplate = argumentsTemplate;
            _isHeadless = isHeadless;
        }

        public async Task<CodegenRunResult> GenerateCodeAsync(
            SpDefinition? spDef,
            string instructionsFilePath,
            string targetProjectDir,
            CancellationToken cancellationToken)
        {
            var absoluteInstructionsPath = Path.GetFullPath(instructionsFilePath);
            var arguments = ArgumentTemplateResolver.Resolve(_argumentsTemplate, absoluteInstructionsPath);

            var workingDir = string.IsNullOrEmpty(targetProjectDir)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(targetProjectDir);

            // 없는 디렉터리를 WorkingDirectory로 주면 Process.Start가 던진다.
            // 산출물 스냅샷도 이 디렉터리를 전제한다.
            Directory.CreateDirectory(workingDir);

            Log.Information(
                "외부 코딩 에이전트 기동 요청 - Engine: {EngineName}, Command: {Command}, Headless: {Headless}, InstructionsFile: {InstructionsFile}, WorkingDir: {WorkingDir}",
                Name, _command, _isHeadless, absoluteInstructionsPath, workingDir);
            Log.Debug("외부 코딩 에이전트 Arguments: {Arguments}", arguments);

            if (spDef != null)
            {
                Log.Debug("외부 코딩 에이전트 대상 SP: {SpSchema}.{SpName}", spDef.Schema, spDef.Name);
            }

            var before = ArtifactChangeDetector.Snapshot(workingDir);

            var startInfo = new ProcessStartInfo
            {
                FileName = _command,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                // 대화형은 부모 콘솔을 그대로 상속한다(AGENTS.md 범주 6 "프로세스 양방향 제어").
                // 무인 배치에서만 stdin을 끊고 stderr를 캡처한다. stdout은 양쪽 다 상속해
                // CI 로그에 진행 상황이 보이게 둔다.
                RedirectStandardInput = _isHeadless,
                RedirectStandardOutput = false,
                RedirectStandardError = _isHeadless
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                // "명령을 못 찾았다"는 안내는 Process.Start()가 실제로 실패했을 때만 맞는
                // 말이다. 이 try는 Start() 하나만 감싼다 - 예전에는 프로세스 종료 뒤
                // 산출물 스냅샷(ArtifactChangeDetector.Snapshot)까지 같은 catch 안에
                // 있어서, 떠도는 자식 프로세스가 파일을 지우는 바람에 스냅샷 도중
                // FileNotFoundException이 나도 "PATH에 등록되어 있는지 확인하십시오"라는
                // 틀린 안내가 나갔다.
                try
                {
                    process.Start();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error(ex, "외부 코딩 에이전트 프로세스 시작 실패 - Engine: {EngineName}, Command: {Command}", Name, _command);
                    throw new InvalidOperationException(
                        $"외부 코딩 엔진({Name}) 기동 중 오류가 발생했습니다. " +
                        $"'{_command}' 명령이 설치되어 PATH에 등록되어 있는지 확인하거나, " +
                        $"appsettings.json의 CodegenSettings:Engines:{Name}:Command에 절대 경로를 지정하십시오. " +
                        $"(오류: {ex.Message})",
                        ex);
                }

                Log.Debug("외부 코딩 에이전트 프로세스 시작됨 - PID: {Pid}", process.Id);

                Task<string>? stderrTask = null;

                if (_isHeadless)
                {
                    // 상속된 TTY를 남겨두면 CLI가 대화형으로 오인한다.
                    // 실측상 정상 동작한 조건이 stdin이 닫힌 상태였다.
                    process.StandardInput.Close();

                    // WaitForExit보다 먼저 읽기를 시작해야 한다. 순서를 바꾸면
                    // 파이프 버퍼가 차는 순간 교착한다.
                    stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                }

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
                    await process.WaitForExitAsync(cancellationToken);

                    var exitCode = process.ExitCode;
                    var standardError = stderrTask is null ? string.Empty : await stderrTask;

                    IReadOnlyDictionary<string, string> after;
                    try
                    {
                        after = ArtifactChangeDetector.Snapshot(workingDir);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 프로세스는 이미 정상 종료했다 - "명령을 못 찾았다"는 안내는
                        // 틀린 진단이므로 붙이지 않는다. 떠도는 자식이 파일을 지웠거나
                        // 권한 문제로 훑기가 실패한 경우가 실제 원인이다.
                        Log.Error(ex,
                            "외부 코딩 에이전트 종료 후 산출물 스냅샷 처리 중 예외 발생 - Engine: {EngineName}, WorkingDir: {WorkingDir}",
                            Name, workingDir);
                        throw new InvalidOperationException(
                            $"외부 코딩 엔진({Name})이 종료된 뒤 작업 디렉터리({workingDir})의 변경 사항을 " +
                            $"확인하는 중 오류가 발생했습니다. 프로세스 자체는 정상적으로 종료했습니다. " +
                            $"(오류: {ex.Message})",
                            ex);
                    }

                    var producedArtifacts = ArtifactChangeDetector.HasChanged(before, after);

                    // 분류기는 stdout을 의도적으로 보지 않는다(CliFailureClassifier.cs:61-68).
                    // 여기서도 stdout은 캡처하지 않고 콘솔로 흘려보낸다.
                    var probe = new CliProcessResult
                    {
                        ExitCode = exitCode,
                        StandardError = standardError
                    };
                    var failureKind = CliFailureClassifier.Classify(probe, extraDetail: null);

                    Log.Information(
                        "외부 코딩 에이전트 종료 - Engine: {EngineName}, ExitCode: {ExitCode}, 산출물 변화: {Produced}, 분류: {FailureKind}",
                        Name, exitCode, producedArtifacts, failureKind);

                    return new CodegenRunResult(
                        producedArtifacts,
                        exitCode,
                        failureKind,
                        string.IsNullOrWhiteSpace(standardError) ? null : standardError);
                }
            }
        }
    }
}
