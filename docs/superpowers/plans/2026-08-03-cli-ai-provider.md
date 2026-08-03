# CLI AI Provider 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `claude`, `codex`, `agy` CLI 코딩 에이전트를 `IAiClient` 구현체로 감싸 정액 구독으로 분석·리뷰 호출을 돌릴 수 있게 한다.

**Architecture:** provider별 전용 클라이언트 3개가 공통 `CliProcessRunner`로 CLI를 헤드리스 모드(`-p` / `exec`)로 기동하고, stdin으로 프롬프트를 넣어 stdout JSON 또는 결과 파일에서 응답을 읽는다. `AiClientFactory`에 케이스를 추가하는 것 외에 `AiService`와 `VerificationPipelineOrchestrator`는 건드리지 않는다.

**Tech Stack:** .NET 10, xUnit 2.9.3, NSubstitute 5.3.0, Serilog 4.3.1, `System.Diagnostics.Process`, `System.Text.Json`

**설계 문서:** `docs/superpowers/specs/2026-08-03-cli-ai-provider-design.md`

## Global Constraints

- **테스트 베이스라인은 430개 전부 통과다.** 각 태스크 종료 시 `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` 전체가 통과해야 한다. 계획에 적힌 숫자는 xUnit이 세는 테스트 **케이스** 수이며, `[Theory]`의 `InlineData` 한 줄이 1건이다. 테스트를 계획과 다르게 쪼개거나 합치면 숫자가 달라진다 — 그 자체는 문제가 아니지만, **줄어들면** 무언가 빠진 것이다.
- **실제 CLI를 호출하는 테스트를 만들지 않는다.** 비용·쿼터·네트워크·로그인 상태에 의존해 CI에서 재현되지 않는다. 프로세스 경로는 `echo`/`sh -c`/`cmd /c` 스텁으로 검증한다.
- **`OperationCanceledException`을 삼키거나 다른 예외 타입으로 감싸지 않는다.** `tests/ReSet.Core.Tests/CancellationPolicyTests.cs`가 이를 강제하며, `cancellation-policy-baseline.txt`에 없는 파일은 위반 0건이어야 한다. 새로 만드는 파일은 전부 여기에 해당한다.
- **넓은 `catch`(`Exception`, `SystemException`)를 취소 토큰이 넘어가는 `await`를 감싸는 `try`에 두지 않는다.** 필요하면 `when (ex is not OperationCanceledException)` 필터를 단다.
- **API 키를 소스나 `appsettings.json`에 하드코딩하지 않는다.** CLI provider는 키를 갖지 않는다.
- **Spectre.Console 출력에 들어가는 런타임 값은 `Markup.Escape`로 이스케이프한다.** stderr 원문과 파일 경로가 여기에 해당한다.
- **주석과 로그 메시지는 한국어로 쓴다.** 이 저장소의 관례다.
- **`Nullable`이 켜져 있다.** 널 허용 참조 경고를 새로 만들지 않는다.
- **커밋 메시지 본문은 영어로 쓴다.** 최근 커밋들의 관례다. 각 커밋 끝에 `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`를 붙인다.

### 실측으로 확정된 값

계획 전체가 이 수치에 의존한다. 추정이 아니라 이 저장소에서 직접 측정한 값이다.

| 항목 | 값 |
|---|---|
| ReSet의 최대 프롬프트 크기 | 191KB (`output/Jobs/Settle_Proc_Daily/raw/prompt-context.md`) |
| claude `--append-system-prompt` 오버헤드 | 10,186 토큰 |
| claude `--system-prompt-file` 오버헤드 | 1,451 토큰 |
| agy stdin 프롬프트 | **불가** (툴 권한 오류로 빈 응답) |
| Windows 명령행 한계 | 32,767자 |
| macOS ARG_MAX | 1,048,576자 |

---

## File Structure

**신규 (`src/ReSet.Core/Services/Clients/Cli/`)**

| 파일 | 책임 |
|---|---|
| `CliProcessResult.cs` | 프로세스 실행 결과 (exit code, stdout, stderr, 타임아웃 여부) |
| `CliProcessRunner.cs` | 프로세스 기동, stdin 주입, stdout/stderr 동시 수집, 타임아웃, 취소 |
| `CliWorkspace.cs` | 호출별 빈 임시 작업 디렉토리 생성·파일 쓰기·정리 |
| `CliEffort.cs` | ReSet effort → CLI effort 매핑과 클램프 |
| `CliPrompt.cs` | 시스템·사용자 프롬프트 결합 (codex/agy 공용) |
| `CliFailureClassifier.cs` | 실패 원인 분류(미설치/미인증/쿼터/타임아웃)와 메시지 조립 |
| `ClaudeCliClient.cs` | `claude -p --output-format json` |
| `CodexCliClient.cs` | `codex exec -` |
| `AntigravityCliClient.cs` | `agy -p` + 명령행 길이 사전 검사 |
| `CliProviderBatchGuard.cs` | 배치 모드에서 CLI provider 차단 판정 |

**수정**

| 파일 | 변경 |
|---|---|
| `src/ReSet.Core/Services/Clients/AiClientFactory.cs` | `IsCliProvider` 추가, `command` 매개변수 추가, CLI 3종 분기 |
| `src/ReSet.Cli/Program.cs` | 팩토리 호출 3곳(`:383`, `:406`, `:427`), 배치 가드, `Command` 설정 로드 |
| `src/ReSet.Validator.Cli/Program.cs` | 팩토리 호출(`:222`), API 키 가드(`:205`), `Command` 설정 로드 |
| `src/ReSet.Cli/appsettings.json` | CLI provider 3종 |
| `src/ReSet.Validator.Cli/appsettings.json` | CLI provider 3종 |

**테스트 (`tests/ReSet.Core.Tests/`)**

`CliProcessRunnerTests.cs`, `CliEffortTests.cs`, `CliFailureClassifierTests.cs`, `ClaudeCliClientTests.cs`, `CodexCliClientTests.cs`, `AntigravityCliClientTests.cs`, `CliProviderBatchGuardTests.cs`, 그리고 `AiClientFactoryTests.cs` 확장.

---

## Task 1: CliProcessRunner

프로세스 기동 계층. 나머지 태스크 전부가 이것 위에 얹힌다.

**Files:**
- Create: `src/ReSet.Core/Services/Clients/Cli/CliProcessResult.cs`
- Create: `src/ReSet.Core/Services/Clients/Cli/CliWorkspace.cs`
- Create: `src/ReSet.Core/Services/Clients/Cli/CliProcessRunner.cs`
- Test: `tests/ReSet.Core.Tests/CliProcessRunnerTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `ReSet.Core.Services.Clients.Cli.CliProcessResult` — `int ExitCode`, `string StandardOutput`, `string StandardError`, `bool TimedOut` (전부 `init`)
  - `CliProcessRunner.RunAsync(string command, IReadOnlyList<string> arguments, string? standardInput, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken) -> Task<CliProcessResult>`
  - `CliWorkspace` — `IDisposable`, `string Path { get; }`, `string WriteFile(string fileName, string content)`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/CliProcessRunnerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliProcessRunnerTests
    {
        // 실제 CLI 대신 셸 스텁을 쓴다. ExternalCliCodingEngineTests와 같은 방식이다.
        private static (string Command, IReadOnlyList<string> Arguments) Shell(string posixScript, string windowsScript)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ("cmd", new[] { "/c", windowsScript })
                : ("sh", new[] { "-c", posixScript });
        }

        private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task RunAsync_StdinIsDeliveredAndStdoutCaptured()
        {
            // 표준 입력을 읽어 표준 출력으로 흘리는 필터. Windows에서는 sort가
            // 안정적으로 stdin을 받는다(한 줄이면 그대로 나온다).
            var (command, arguments) = Shell("cat", "sort");

            var result = await CliProcessRunner.RunAsync(
                command, arguments, "안녕하세요", Path.GetTempPath(), Generous, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            // 한글이 깨지지 않고 왕복하는지까지 확인한다. ReSet의 프롬프트는 전부 한글이다.
            Assert.Contains("안녕하세요", result.StandardOutput);
        }

        [Fact]
        public async Task RunAsync_NonZeroExit_IsReportedNotThrown()
        {
            var (command, arguments) = Shell("exit 3", "exit 3");

            var result = await CliProcessRunner.RunAsync(
                command, arguments, null, Path.GetTempPath(), Generous, CancellationToken.None);

            Assert.Equal(3, result.ExitCode);
            Assert.False(result.TimedOut);
        }

        [Fact]
        public async Task RunAsync_StandardErrorIsCaptured()
        {
            var (command, arguments) = Shell("echo boom 1>&2; exit 1", "echo boom 1>&2 & exit 1");

            var result = await CliProcessRunner.RunAsync(
                command, arguments, null, Path.GetTempPath(), Generous, CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("boom", result.StandardError);
        }

        // stdout 파이프 버퍼(보통 64KB)보다 큰 출력을 흘려도 데드락에 빠지지 않아야 한다.
        // 명세서 응답은 실제로 수십 KB다.
        [Fact]
        public async Task RunAsync_LargeStdout_DoesNotDeadlock()
        {
            var payload = new string('x', 300_000);
            var tempFile = Path.Combine(Path.GetTempPath(), $"reset-cli-test-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempFile, payload);

            try
            {
                var (command, arguments) = Shell($"cat \"{tempFile}\"", $"type \"{tempFile}\"");

                var result = await CliProcessRunner.RunAsync(
                    command, arguments, null, Path.GetTempPath(), Generous, CancellationToken.None);

                Assert.Equal(0, result.ExitCode);
                Assert.True(result.StandardOutput.Length >= 300_000);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public async Task RunAsync_Timeout_ReturnsTimedOutResult()
        {
            var (command, arguments) = Shell("sleep 30", "ping -n 30 127.0.0.1 > nul");

            var result = await CliProcessRunner.RunAsync(
                command, arguments, null, Path.GetTempPath(),
                TimeSpan.FromSeconds(1), CancellationToken.None);

            Assert.True(result.TimedOut);
        }

        // 사용자 취소는 타임아웃과 구별되어야 하며, 다른 예외 타입으로 감싸이면 안 된다.
        [Fact]
        public async Task RunAsync_UserCancellation_ThrowsOperationCanceledException()
        {
            var (command, arguments) = Shell("sleep 30", "ping -n 30 127.0.0.1 > nul");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CliProcessRunner.RunAsync(
                    command, arguments, null, Path.GetTempPath(),
                    TimeSpan.FromSeconds(30), cts.Token));
        }

        [Fact]
        public async Task RunAsync_MissingCommand_ThrowsWin32Exception()
        {
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
                CliProcessRunner.RunAsync(
                    "reset_cli_command_does_not_exist_42", Array.Empty<string>(), null,
                    Path.GetTempPath(), Generous, CancellationToken.None));
        }

        [Fact]
        public void CliWorkspace_CreatesEmptyDirectoryAndCleansUp()
        {
            string path;
            using (var workspace = new CliWorkspace())
            {
                path = workspace.Path;
                Assert.True(Directory.Exists(path));
                Assert.Empty(Directory.GetFileSystemEntries(path));

                var written = workspace.WriteFile("system.txt", "규칙");
                Assert.Equal("규칙", File.ReadAllText(written));
            }

            Assert.False(Directory.Exists(path));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CliProcessRunnerTests"`
Expected: 컴파일 실패 — `CliProcessRunner`, `CliWorkspace`, `CliProcessResult` 형식을 찾을 수 없음

- [ ] **Step 3: CliProcessResult를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/CliProcessResult.cs`:

```csharp
namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// CLI 프로세스 1회 실행의 결과. 실패를 예외로 던지지 않고 값으로 돌려주므로
    /// 호출자가 원인을 분류할 수 있다.
    /// </summary>
    public sealed class CliProcessResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;

        /// <summary>타임아웃으로 프로세스를 강제 종료했는가. 사용자 취소와는 다르다.</summary>
        public bool TimedOut { get; init; }

        public bool Succeeded => !TimedOut && ExitCode == 0;
    }
}
```

- [ ] **Step 4: CliWorkspace를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/CliWorkspace.cs`:

```csharp
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
```

- [ ] **Step 5: CliProcessRunner를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/CliProcessRunner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
                if (standardInput != null)
                {
                    await process.StandardInput.WriteAsync(
                        standardInput.AsMemory(), linkedCts.Token);
                }

                // 항상 닫는다. 닫지 않으면 자식이 입력을 계속 기다린다.
                process.StandardInput.Close();

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
```

- [ ] **Step 6: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CliProcessRunnerTests"`
Expected: PASS (8건)

- [ ] **Step 7: 취소 정책 테스트를 포함해 전체를 돌린다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`
Expected: PASS, 438건

`CancellationPolicyTests`가 실패하면 새 파일에 넓은 catch가 들어간 것이다. `cancellation-policy-baseline.txt`를 늘리지 말고 코드를 고친다.

- [ ] **Step 8: 커밋한다**

```bash
git add src/ReSet.Core/Services/Clients/Cli/ tests/ReSet.Core.Tests/CliProcessRunnerTests.cs
git commit -F - <<'EOF'
feat(cli-provider): add process runner for headless CLI agents

Start the stdout/stderr readers before writing stdin. ReSet's largest real
prompt is 191KB, and a child that starts writing output while nobody drains
the pipe deadlocks both sides.

Timeouts return a result; user cancellation throws OperationCanceledException
unwrapped, so the existing handlers downstream still match it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 2: effort 매핑, 프롬프트 결합, 실패 분류

순수 함수 세 묶음. 세 클라이언트가 공유한다.

**Files:**
- Create: `src/ReSet.Core/Services/Clients/Cli/CliEffort.cs`
- Create: `src/ReSet.Core/Services/Clients/Cli/CliPrompt.cs`
- Create: `src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs`
- Test: `tests/ReSet.Core.Tests/CliEffortTests.cs`
- Test: `tests/ReSet.Core.Tests/CliPromptTests.cs`
- Test: `tests/ReSet.Core.Tests/CliFailureClassifierTests.cs`

**Interfaces:**
- Consumes: `CliProcessResult` (Task 1)
- Produces:
  - `CliEffort.ForClaude(string? effort) -> string?`
  - `CliPrompt.Combine(string systemPrompt, string userPrompt) -> string`
  - `CliEffort.ForThreeLevel(string? effort, out bool clamped) -> string?`
  - `CliFailureKind` — `enum { NotAuthenticated, QuotaExhausted, Timeout, Unknown }`
  - `CliFailureClassifier.Classify(CliProcessResult result, string? extraDetail) -> CliFailureKind`
  - `CliFailureClassifier.ToException(string providerName, string command, CliProcessResult result, string? extraDetail) -> InvalidOperationException`
  - `CliFailureClassifier.CommandNotFound(string providerName, string command, Exception inner) -> InvalidOperationException`

- [ ] **Step 1: effort 매핑의 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/CliEffortTests.cs`:

```csharp
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliEffortTests
    {
        [Theory]
        [InlineData("low", "low")]
        [InlineData("medium", "medium")]
        [InlineData("high", "high")]
        [InlineData("xhigh", "xhigh")]
        [InlineData("HIGH", "high")]
        public void ForClaude_KnownLevels_PassThrough(string input, string expected)
        {
            Assert.Equal(expected, CliEffort.ForClaude(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("dynamic")]
        public void ForClaude_UnknownOrBlank_ReturnsNull(string? input)
        {
            // null이면 호출자가 --effort 플래그를 붙이지 않고 CLI 기본값을 따른다.
            Assert.Null(CliEffort.ForClaude(input));
        }

        [Theory]
        [InlineData("low", "low")]
        [InlineData("medium", "medium")]
        [InlineData("high", "high")]
        public void ForThreeLevel_WithinRange_NotClamped(string input, string expected)
        {
            var result = CliEffort.ForThreeLevel(input, out var clamped);
            Assert.Equal(expected, result);
            Assert.False(clamped);
        }

        [Theory]
        [InlineData("xhigh")]
        [InlineData("max")]
        public void ForThreeLevel_AboveRange_ClampsToHigh(string input)
        {
            var result = CliEffort.ForThreeLevel(input, out var clamped);
            Assert.Equal("high", result);
            Assert.True(clamped);
        }

        [Fact]
        public void ForThreeLevel_Unknown_ReturnsNullAndNotClamped()
        {
            var result = CliEffort.ForThreeLevel("dynamic", out var clamped);
            Assert.Null(result);
            Assert.False(clamped);
        }
    }
}
```

- [ ] **Step 2: 프롬프트 결합의 실패하는 테스트를 작성한다**

`codex`와 `agy`는 둘 다 시스템 프롬프트를 따로 받지 않아 하나로 합쳐야 한다. 두 클라이언트 중 한쪽에 두면 다른 쪽이 그것을 참조하게 되므로 공용으로 둔다.

`tests/ReSet.Core.Tests/CliPromptTests.cs`:

```csharp
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliPromptTests
    {
        [Fact]
        public void Combine_JoinsSystemAndUserPrompt()
        {
            var combined = CliPrompt.Combine("규칙입니다", "본문입니다");

            Assert.StartsWith("규칙입니다", combined);
            Assert.EndsWith("본문입니다", combined);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Combine_BlankSystemPrompt_ReturnsUserPromptOnly(string systemPrompt)
        {
            Assert.Equal("본문입니다", CliPrompt.Combine(systemPrompt, "본문입니다"));
        }
    }
}
```

- [ ] **Step 3: 실패 분류의 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/CliFailureClassifierTests.cs`:

```csharp
using System;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliFailureClassifierTests
    {
        private static CliProcessResult Failed(string standardError) => new()
        {
            ExitCode = 1,
            StandardError = standardError,
            TimedOut = false
        };

        [Fact]
        public void Classify_TimedOut_ReturnsTimeout()
        {
            var result = new CliProcessResult { ExitCode = -1, TimedOut = true };
            Assert.Equal(CliFailureKind.Timeout, CliFailureClassifier.Classify(result, null));
        }

        [Theory]
        [InlineData("Claude usage limit reached. Your limit will reset at 3pm.")]
        [InlineData("You have exceeded your quota for this month")]
        [InlineData("rate_limit_error: too many requests")]
        [InlineData("HTTP 429 Too Many Requests")]
        public void Classify_QuotaMessages_ReturnsQuotaExhausted(string standardError)
        {
            Assert.Equal(CliFailureKind.QuotaExhausted,
                CliFailureClassifier.Classify(Failed(standardError), null));
        }

        [Theory]
        [InlineData("Not logged in. Please run `claude login`.")]
        [InlineData("401 Unauthorized")]
        [InlineData("Authentication failed")]
        [InlineData("No credentials found")]
        public void Classify_AuthMessages_ReturnsNotAuthenticated(string standardError)
        {
            Assert.Equal(CliFailureKind.NotAuthenticated,
                CliFailureClassifier.Classify(Failed(standardError), null));
        }

        [Fact]
        public void Classify_QuotaWinsOverAuth_WhenBothPresent()
        {
            // 쿼터 소진 안내문에 "login" 같은 단어가 섞이는 경우가 있다.
            // 쿼터가 더 구체적인 진단이므로 먼저 본다.
            var result = Failed("usage limit reached; please login again later");
            Assert.Equal(CliFailureKind.QuotaExhausted, CliFailureClassifier.Classify(result, null));
        }

        [Fact]
        public void Classify_ExtraDetailIsInspected()
        {
            // claude는 종료 코드 0으로 끝내면서 JSON 안에만 오류를 담을 수 있다.
            var result = new CliProcessResult { ExitCode = 0 };
            Assert.Equal(CliFailureKind.QuotaExhausted,
                CliFailureClassifier.Classify(result, "rate_limit_error"));
        }

        [Fact]
        public void Classify_UnrecognizedMessage_ReturnsUnknown()
        {
            Assert.Equal(CliFailureKind.Unknown,
                CliFailureClassifier.Classify(Failed("segmentation fault"), null));
        }

        [Fact]
        public void ToException_QuotaExhausted_MentionsProviderSwitch()
        {
            var exception = CliFailureClassifier.ToException(
                "claude-cli", "claude", Failed("usage limit reached"), null);

            Assert.Contains("claude-cli", exception.Message);
            Assert.Contains("구독", exception.Message);
        }

        // 분류를 못 맞혔을 때도 진단이 가능해야 한다. stderr 원문을 자르지 않는다.
        [Fact]
        public void ToException_AlwaysIncludesRawStandardError()
        {
            var exception = CliFailureClassifier.ToException(
                "codex-cli", "codex", Failed("something nobody predicted"), null);

            Assert.Contains("something nobody predicted", exception.Message);
        }

        [Fact]
        public void CommandNotFound_MentionsCommandAndPath()
        {
            var exception = CliFailureClassifier.CommandNotFound(
                "agy-cli", "agy", new InvalidOperationException("no such file"));

            Assert.Contains("agy", exception.Message);
            Assert.Contains("PATH", exception.Message);
            Assert.NotNull(exception.InnerException);
        }
    }
}
```

- [ ] **Step 4: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CliEffortTests|FullyQualifiedName~CliPromptTests|FullyQualifiedName~CliFailureClassifierTests"`
Expected: 컴파일 실패 — `CliEffort`, `CliPrompt`, `CliFailureKind`, `CliFailureClassifier` 형식을 찾을 수 없음

- [ ] **Step 5: CliEffort를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/CliEffort.cs`:

```csharp
namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// ReSet의 effort(low|medium|high|xhigh)를 각 CLI가 받는 값으로 옮긴다.
    /// 알 수 없는 값이나 빈 값에는 null을 돌려주고, 호출자는 플래그를 아예 붙이지
    /// 않아 CLI 기본값을 따르게 한다.
    /// </summary>
    public static class CliEffort
    {
        /// <summary>claude는 low|medium|high|xhigh|max를 받는다. ReSet의 값이 그대로 통한다.</summary>
        public static string? ForClaude(string? effort)
        {
            return Normalize(effort) switch
            {
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "xhigh" => "xhigh",
                "max" => "max",
                _ => null
            };
        }

        /// <summary>
        /// codex와 agy는 low|medium|high 세 단계만 받는다. 그 위는 high로 낮춘다.
        /// 낮췄다는 사실을 호출자가 로그에 남길 수 있도록 clamped로 알린다 —
        /// 요청한 추론 강도가 조용히 떨어지면 품질 차이의 원인을 찾을 수 없다.
        /// </summary>
        public static string? ForThreeLevel(string? effort, out bool clamped)
        {
            clamped = false;

            switch (Normalize(effort))
            {
                case "low":
                    return "low";
                case "medium":
                    return "medium";
                case "high":
                    return "high";
                case "xhigh":
                case "max":
                    clamped = true;
                    return "high";
                default:
                    return null;
            }
        }

        private static string? Normalize(string? effort) =>
            string.IsNullOrWhiteSpace(effort) ? null : effort.Trim().ToLowerInvariant();
    }
}
```

- [ ] **Step 6: CliPrompt를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/CliPrompt.cs`:

```csharp
namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// codex와 agy는 시스템 프롬프트를 별도로 받지 않는다. 둘 다 하나로 합쳐
    /// 넘겨야 하므로, 어느 한 클라이언트에 두지 않고 공용으로 둔다.
    /// </summary>
    public static class CliPrompt
    {
        public static string Combine(string systemPrompt, string userPrompt)
        {
            return string.IsNullOrWhiteSpace(systemPrompt)
                ? userPrompt
                : $"{systemPrompt}\n\n{userPrompt}";
        }
    }
}
```

- [ ] **Step 7: CliFailureClassifier를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs`:

```csharp
using System;

namespace ReSet.Core.Services.Clients.Cli
{
    public enum CliFailureKind
    {
        NotAuthenticated,
        QuotaExhausted,
        Timeout,
        Unknown
    }

    /// <summary>
    /// CLI 실패의 원인을 분류한다.
    ///
    /// 자동 폴백을 만들지 않기로 했으므로, 사람이 로그만 보고 "다른 CLI로 갈지
    /// API로 갈지"를 판단할 수 있어야 한다. 분류가 이 설계의 핵심 산출물이다.
    /// </summary>
    public static class CliFailureClassifier
    {
        // 쿼터를 먼저 본다. 쿼터 안내문에 "login" 같은 단어가 섞이는 경우가 있고,
        // 그때 인증 문제로 오진하면 사용자가 엉뚱한 조치를 한다.
        private static readonly string[] QuotaMarkers =
        {
            "usage limit", "rate limit", "rate_limit", "quota", "limit reached",
            "429", "out of credit", "insufficient_quota", "too many requests",
            "사용량", "한도"
        };

        private static readonly string[] AuthMarkers =
        {
            "not logged in", "unauthorized", "401", "authentication",
            "invalid api key", "credential", "please log in", "please login",
            "로그인", "인증"
        };

        public static CliFailureKind Classify(CliProcessResult result, string? extraDetail)
        {
            if (result.TimedOut)
            {
                return CliFailureKind.Timeout;
            }

            var haystack = $"{result.StandardError}\n{result.StandardOutput}\n{extraDetail}"
                .ToLowerInvariant();

            if (ContainsAny(haystack, QuotaMarkers))
            {
                return CliFailureKind.QuotaExhausted;
            }

            if (ContainsAny(haystack, AuthMarkers))
            {
                return CliFailureKind.NotAuthenticated;
            }

            return CliFailureKind.Unknown;
        }

        public static InvalidOperationException ToException(
            string providerName,
            string command,
            CliProcessResult result,
            string? extraDetail)
        {
            var kind = Classify(result, extraDetail);

            var summary = kind switch
            {
                CliFailureKind.Timeout =>
                    $"{providerName} 호출이 제한 시간을 초과해 프로세스를 강제 종료했습니다. " +
                    "AiSettings:TimeoutSeconds 값을 늘리거나 더 작은 대상으로 나누어 실행하십시오.",
                CliFailureKind.QuotaExhausted =>
                    $"{providerName}의 구독 사용 한도가 소진되었습니다. " +
                    "appsettings.json에서 다른 CLI provider 또는 API provider로 변경한 뒤 다시 실행하십시오.",
                CliFailureKind.NotAuthenticated =>
                    $"{providerName}이(가) 로그인되어 있지 않습니다. " +
                    $"터미널에서 '{command}'를 직접 실행해 로그인을 완료하십시오.",
                _ =>
                    $"{providerName} 호출이 실패했습니다 (종료 코드: {result.ExitCode})."
            };

            // 분류를 못 맞힌 경우에도 진단이 가능해야 한다. 원문을 자르지 않는다.
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? extraDetail
                : result.StandardError;

            var message = string.IsNullOrWhiteSpace(detail)
                ? summary
                : $"{summary}\n[CLI 출력]\n{detail}";

            return new InvalidOperationException(message);
        }

        public static InvalidOperationException CommandNotFound(
            string providerName,
            string command,
            Exception inner)
        {
            return new InvalidOperationException(
                $"{providerName}을(를) 실행할 수 없습니다. '{command}' 명령을 찾지 못했습니다. " +
                $"CLI가 설치되어 있는지, PATH에 등록되어 있는지 확인하거나 " +
                $"appsettings.json의 AiSettings:Providers:{providerName}:Command에 절대 경로를 지정하십시오. " +
                $"(원인: {inner.Message})",
                inner);
        }

        private static bool ContainsAny(string haystack, string[] markers)
        {
            foreach (var marker in markers)
            {
                if (haystack.Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
```

- [ ] **Step 8: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CliEffortTests|FullyQualifiedName~CliPromptTests|FullyQualifiedName~CliFailureClassifierTests"`
Expected: PASS (33건)

- [ ] **Step 9: 전체 테스트를 돌린다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`
Expected: PASS, 471건

- [ ] **Step 10: 커밋한다**

```bash
git add src/ReSet.Core/Services/Clients/Cli/CliEffort.cs \
        src/ReSet.Core/Services/Clients/Cli/CliPrompt.cs \
        src/ReSet.Core/Services/Clients/Cli/CliFailureClassifier.cs \
        tests/ReSet.Core.Tests/CliEffortTests.cs \
        tests/ReSet.Core.Tests/CliPromptTests.cs \
        tests/ReSet.Core.Tests/CliFailureClassifierTests.cs
git commit -F - <<'EOF'
feat(cli-provider): classify CLI failures and map effort levels

There is no automatic fallback by design, so a human reads the error and
decides whether to switch CLIs or go back to the API. That makes the
classification the deliverable: quota, auth, timeout, or unknown — and the
raw stderr is always included so an unclassified failure is still diagnosable.

Quota markers are checked before auth markers because quota notices sometimes
mention logging in, and misdiagnosing that sends the user to fix the wrong thing.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 3: ClaudeCliClient

**Files:**
- Create: `src/ReSet.Core/Services/Clients/Cli/ClaudeCliClient.cs`
- Test: `tests/ReSet.Core.Tests/ClaudeCliClientTests.cs`

**Interfaces:**
- Consumes: `CliProcessRunner.RunAsync`, `CliWorkspace`, `CliEffort.ForClaude`, `CliFailureClassifier` (Task 1·2), `IAiClient`, `AiResult`
- Produces:
  - `ClaudeCliClient(string command, string modelName, TimeSpan timeout)` — `IAiClient` 구현, `TimeSpan Timeout { get; }` 노출
  - `ClaudeCliClient.BuildArguments(string modelName, string? effort, string systemPromptFilePath) -> IReadOnlyList<string>`
  - `ClaudeCliResponse` — `bool IsError`, `string? Result`, `string? Subtype`, `string? ApiErrorStatus`
  - `ClaudeCliClient.ParseResponse(string standardOutput) -> ClaudeCliResponse`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/ClaudeCliClientTests.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class ClaudeCliClientTests
    {
        // 2026-08-03에 `claude -p --output-format json`을 실제로 호출해 받은 응답을 줄인 것.
        private const string SuccessJson =
            "{\"is_error\":false,\"num_turns\":1,\"session_id\":\"abc\",\"total_cost_usd\":0.042," +
            "\"subtype\":\"success\",\"api_error_status\":null,\"result\":\"PONG\",\"type\":\"result\"}";

        [Fact]
        public void BuildArguments_AlwaysDisablesToolsAndUsesJsonOutput()
        {
            var arguments = ClaudeCliClient.BuildArguments("sonnet", "high", "/tmp/sys.txt");

            Assert.Contains("-p", arguments);
            Assert.Contains("--output-format", arguments);
            Assert.Contains("json", arguments);
            Assert.Contains("--disable-slash-commands", arguments);
            Assert.Contains("--no-session-persistence", arguments);

            // 순수 LLM처럼 쓰기 위해 모든 툴을 끈다. --tools 다음 인자는 빈 문자열이다.
            var toolsIndex = arguments.ToList().IndexOf("--tools");
            Assert.True(toolsIndex >= 0);
            Assert.Equal(string.Empty, arguments[toolsIndex + 1]);
        }

        // 기본 시스템 프롬프트를 '추가'가 아니라 '교체'해야 한다.
        // 실측: append는 호출당 10,186 토큰, 교체는 1,451 토큰.
        [Fact]
        public void BuildArguments_ReplacesSystemPromptViaFile()
        {
            var arguments = ClaudeCliClient.BuildArguments("sonnet", null, "/tmp/sys.txt");

            var index = arguments.ToList().IndexOf("--system-prompt-file");
            Assert.True(index >= 0);
            Assert.Equal("/tmp/sys.txt", arguments[index + 1]);
            Assert.DoesNotContain("--append-system-prompt", arguments);
        }

        [Fact]
        public void BuildArguments_WithEffort_AppendsEffortFlag()
        {
            var arguments = ClaudeCliClient.BuildArguments("sonnet", "xhigh", "/tmp/sys.txt");

            var index = arguments.ToList().IndexOf("--effort");
            Assert.True(index >= 0);
            Assert.Equal("xhigh", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_WithoutEffort_OmitsEffortFlag()
        {
            var arguments = ClaudeCliClient.BuildArguments("sonnet", null, "/tmp/sys.txt");
            Assert.DoesNotContain("--effort", arguments);
        }

        [Fact]
        public void BuildArguments_WithBlankModel_OmitsModelFlag()
        {
            var arguments = ClaudeCliClient.BuildArguments("", null, "/tmp/sys.txt");
            Assert.DoesNotContain("--model", arguments);
        }

        [Fact]
        public void ParseResponse_Success_ExtractsResultText()
        {
            var response = ClaudeCliClient.ParseResponse(SuccessJson);

            Assert.False(response.IsError);
            Assert.Equal("PONG", response.Result);
        }

        [Fact]
        public void ParseResponse_ErrorPayload_ExposesSubtypeAndStatus()
        {
            const string errorJson =
                "{\"is_error\":true,\"subtype\":\"error_max_turns\"," +
                "\"api_error_status\":\"rate_limit_error\",\"result\":null,\"type\":\"result\"}";

            var response = ClaudeCliClient.ParseResponse(errorJson);

            Assert.True(response.IsError);
            Assert.Equal("error_max_turns", response.Subtype);
            Assert.Equal("rate_limit_error", response.ApiErrorStatus);
        }

        [Fact]
        public void ParseResponse_NotJson_ThrowsInvalidOperationException()
        {
            Assert.Throws<InvalidOperationException>(() =>
                ClaudeCliClient.ParseResponse("이건 JSON이 아니다"));
        }

        [Fact]
        public void ProviderNameModelNameAndTimeout_AreExposed()
        {
            var client = new ClaudeCliClient("claude", "sonnet", TimeSpan.FromSeconds(30));

            Assert.Equal("claude-cli", client.ProviderName);
            Assert.Equal("sonnet", client.ModelName);
            Assert.Equal(30, client.Timeout.TotalSeconds);
        }

        // ProviderName이 로컬 프로바이더로 오인되면 AiService가 로컬 분할 파이프라인을
        // 켠다. CLI provider는 그 대상이 아니다.
        [Fact]
        public void ProviderName_IsNotTreatedAsLocalProvider()
        {
            var client = new ClaudeCliClient("claude", "sonnet", TimeSpan.FromSeconds(30));

            Assert.False(ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(client.ProviderName));
        }

        [Fact]
        public async Task ChatAsync_MissingCommand_ThrowsWithInstallGuidance()
        {
            var client = new ClaudeCliClient(
                "reset_claude_does_not_exist_42", "sonnet", TimeSpan.FromSeconds(10));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("PATH", exception.Message);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~ClaudeCliClientTests"`
Expected: 컴파일 실패 — `ClaudeCliClient` 형식을 찾을 수 없음

- [ ] **Step 3: ClaudeCliClient를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/ClaudeCliClient.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients.Cli
{
    public sealed class ClaudeCliResponse
    {
        public bool IsError { get; init; }
        public string? Result { get; init; }
        public string? Subtype { get; init; }
        public string? ApiErrorStatus { get; init; }
    }

    /// <summary>
    /// Claude Code CLI를 헤드리스로 기동해 순수 LLM처럼 사용한다.
    /// API 키 대신 CLI에 로그인된 구독 계정을 쓴다.
    /// </summary>
    public sealed class ClaudeCliClient : IAiClient
    {
        private readonly string _command;
        private readonly string _modelName;
        private readonly TimeSpan _timeout;

        public string ProviderName => "claude-cli";
        public string ModelName => _modelName;

        /// <summary>팩토리가 HttpClient에서 읽어 넘긴 제한 시간. 배선이 끊기면 테스트가 잡는다.</summary>
        public TimeSpan Timeout => _timeout;

        public ClaudeCliClient(string command, string modelName, TimeSpan timeout)
        {
            _command = string.IsNullOrWhiteSpace(command) ? "claude" : command;
            _modelName = modelName ?? string.Empty;
            _timeout = timeout;

            // CLI는 temperature를 노출하지 않는다. 조용히 무시하면 Critic 채점이
            // 왜 흔들리는지 알 수 없게 되므로, 생성 시 한 번 알린다.
            Log.Warning("{Provider}는 temperature를 지원하지 않습니다. 설정값은 무시됩니다.", ProviderName);
        }

        public static IReadOnlyList<string> BuildArguments(
            string modelName, string? effort, string systemPromptFilePath)
        {
            var arguments = new List<string>
            {
                "-p",
                "--output-format", "json",
                // 순수 LLM으로 쓴다. 툴을 켜두면 에이전트가 파일 시스템을 돌아다닌다.
                "--tools", string.Empty,
                "--disable-slash-commands",
                "--no-session-persistence",
                // 기본 시스템 프롬프트를 '교체'한다. 추가(append)하면 코딩 에이전트
                // 프롬프트가 그대로 얹혀 호출당 오버헤드가 7배가 된다.
                "--system-prompt-file", systemPromptFilePath
            };

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                arguments.Add("--model");
                arguments.Add(modelName);
            }

            var mappedEffort = CliEffort.ForClaude(effort);
            if (mappedEffort != null)
            {
                arguments.Add("--effort");
                arguments.Add(mappedEffort);
            }

            return arguments;
        }

        public static ClaudeCliResponse ParseResponse(string standardOutput)
        {
            try
            {
                using var document = JsonDocument.Parse(standardOutput);
                var root = document.RootElement;

                return new ClaudeCliResponse
                {
                    IsError = root.TryGetProperty("is_error", out var isError)
                              && isError.ValueKind == JsonValueKind.True,
                    Result = ReadString(root, "result"),
                    Subtype = ReadString(root, "subtype"),
                    ApiErrorStatus = ReadString(root, "api_error_status")
                };
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"claude-cli 응답을 JSON으로 해석할 수 없습니다.\n[출력]\n{standardOutput}", ex);
            }
        }

        public async Task<AiResult> ChatAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            string? effort = null,
            CancellationToken cancellationToken = default)
        {
            using var workspace = new CliWorkspace();
            var systemPromptFile = workspace.WriteFile("system-prompt.txt", systemPrompt ?? string.Empty);
            var arguments = BuildArguments(_modelName, effort, systemPromptFile);

            CliProcessResult processResult;
            try
            {
                processResult = await CliProcessRunner.RunAsync(
                    _command, arguments, userPrompt, workspace.Path, _timeout, cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw CliFailureClassifier.CommandNotFound(ProviderName, _command, ex);
            }

            if (!processResult.Succeeded)
            {
                throw CliFailureClassifier.ToException(ProviderName, _command, processResult, null);
            }

            var response = ParseResponse(processResult.StandardOutput);

            // 종료 코드가 0이어도 JSON 안에만 오류가 담기는 경우가 있다.
            if (response.IsError || response.Result == null)
            {
                var detail = $"{response.Subtype} {response.ApiErrorStatus}".Trim();
                throw CliFailureClassifier.ToException(ProviderName, _command, processResult, detail);
            }

            return new AiResult { Content = response.Result };
        }

        private static string? ReadString(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~ClaudeCliClientTests"`
Expected: PASS (11건)

- [ ] **Step 5: 전체 테스트를 돌린다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`
Expected: PASS, 482건

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/Clients/Cli/ClaudeCliClient.cs \
        tests/ReSet.Core.Tests/ClaudeCliClientTests.cs
git commit -F - <<'EOF'
feat(cli-provider): add ClaudeCliClient

Replace the default system prompt rather than appending to it. Measured on
2026-08-03: appending costs 10,186 tokens per call because the coding-agent
prompt rides along, replacing costs 1,451. On a subscription that is quota,
not dollars, so the 7x matters.

Tools are disabled outright so the agent behaves as a plain LLM instead of
wandering the filesystem.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 4: CodexCliClient

**Files:**
- Create: `src/ReSet.Core/Services/Clients/Cli/CodexCliClient.cs`
- Test: `tests/ReSet.Core.Tests/CodexCliClientTests.cs`

**Interfaces:**
- Consumes: Task 1·2의 전부
- Produces:
  - `CodexCliClient(string command, string modelName, TimeSpan timeout)` — `IAiClient` 구현, `TimeSpan Timeout { get; }` 노출
  - `CodexCliClient.BuildArguments(string modelName, string? effort, string outputFilePath) -> IReadOnlyList<string>`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/CodexCliClientTests.cs`:

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CodexCliClientTests
    {
        [Fact]
        public void BuildArguments_UsesNonInteractiveExecWithStdinAndReadOnlySandbox()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", "high", "/tmp/out.txt");

            Assert.Equal("exec", arguments[0]);
            // "-" 는 프롬프트를 stdin에서 읽으라는 뜻이다.
            Assert.Equal("-", arguments[1]);
            Assert.Contains("--skip-git-repo-check", arguments);
            Assert.Contains("--ephemeral", arguments);

            var sandboxIndex = arguments.ToList().IndexOf("--sandbox");
            Assert.True(sandboxIndex >= 0);
            Assert.Equal("read-only", arguments[sandboxIndex + 1]);
        }

        // stdout에는 진행 로그가 섞이므로 결과는 파일로 받는다.
        [Fact]
        public void BuildArguments_WritesLastMessageToFile()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", null, "/tmp/out.txt");

            var index = arguments.ToList().IndexOf("-o");
            Assert.True(index >= 0);
            Assert.Equal("/tmp/out.txt", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_EffortIsPassedAsTomlConfigOverride()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", "medium", "/tmp/out.txt");

            var index = arguments.ToList().IndexOf("-c");
            Assert.True(index >= 0);
            // 값은 TOML로 파싱되므로 문자열은 따옴표로 감싼다.
            Assert.Equal("model_reasoning_effort=\"medium\"", arguments[index + 1]);
        }

        // codex는 low|medium|high만 받는다. ReSet의 xhigh는 낮춰야 한다.
        [Fact]
        public void BuildArguments_XhighEffort_IsClampedToHigh()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", "xhigh", "/tmp/out.txt");

            var index = arguments.ToList().IndexOf("-c");
            Assert.Equal("model_reasoning_effort=\"high\"", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_WithoutEffort_OmitsConfigOverride()
        {
            var arguments = CodexCliClient.BuildArguments("gpt-5.6-terra", null, "/tmp/out.txt");
            Assert.DoesNotContain("-c", arguments);
        }

        [Fact]
        public void BuildArguments_WithBlankModel_OmitsModelFlag()
        {
            var arguments = CodexCliClient.BuildArguments("", null, "/tmp/out.txt");
            Assert.DoesNotContain("-m", arguments);
        }

        [Fact]
        public void ProviderNameModelNameAndTimeout_AreExposed()
        {
            var client = new CodexCliClient("codex", "gpt-5.6-terra", TimeSpan.FromSeconds(30));

            Assert.Equal("codex-cli", client.ProviderName);
            Assert.Equal("gpt-5.6-terra", client.ModelName);
            Assert.Equal(30, client.Timeout.TotalSeconds);
        }

        [Fact]
        public async Task ChatAsync_MissingCommand_ThrowsWithInstallGuidance()
        {
            var client = new CodexCliClient(
                "reset_codex_does_not_exist_42", "gpt-5.6-terra", TimeSpan.FromSeconds(10));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("PATH", exception.Message);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CodexCliClientTests"`
Expected: 컴파일 실패 — `CodexCliClient` 형식을 찾을 수 없음

- [ ] **Step 3: CodexCliClient를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/CodexCliClient.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// Codex CLI를 비대화형(exec)으로 기동한다.
    /// 시스템 프롬프트 분리 개념이 없어 사용자 프롬프트와 합쳐 stdin으로 넣고,
    /// 최종 응답은 stdout이 아니라 -o 파일에서 읽는다(stdout에는 진행 로그가 섞인다).
    /// </summary>
    public sealed class CodexCliClient : IAiClient
    {
        private const string ResultFileName = "codex-result.txt";

        private readonly string _command;
        private readonly string _modelName;
        private readonly TimeSpan _timeout;

        public string ProviderName => "codex-cli";
        public string ModelName => _modelName;

        /// <summary>팩토리가 HttpClient에서 읽어 넘긴 제한 시간. 배선이 끊기면 테스트가 잡는다.</summary>
        public TimeSpan Timeout => _timeout;

        public CodexCliClient(string command, string modelName, TimeSpan timeout)
        {
            _command = string.IsNullOrWhiteSpace(command) ? "codex" : command;
            _modelName = modelName ?? string.Empty;
            _timeout = timeout;

            Log.Warning("{Provider}는 temperature를 지원하지 않습니다. 설정값은 무시됩니다.", ProviderName);
        }

        public static IReadOnlyList<string> BuildArguments(
            string modelName, string? effort, string outputFilePath)
        {
            var arguments = new List<string>
            {
                "exec",
                // 프롬프트를 stdin에서 읽는다. 191KB짜리 프롬프트를 argv로 넘길 수 없다.
                "-",
                "--sandbox", "read-only",
                "--skip-git-repo-check",
                "--ephemeral",
                "-o", outputFilePath
            };

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                arguments.Add("-m");
                arguments.Add(modelName);
            }

            var mappedEffort = CliEffort.ForThreeLevel(effort, out var clamped);
            if (mappedEffort != null)
            {
                if (clamped)
                {
                    Log.Warning(
                        "codex-cli는 low|medium|high만 지원합니다. 요청한 effort '{Requested}'를 '{Applied}'로 낮춥니다.",
                        effort, mappedEffort);
                }

                arguments.Add("-c");
                // 값은 TOML로 파싱된다. 문자열은 따옴표로 감싸야 안전하다.
                arguments.Add($"model_reasoning_effort=\"{mappedEffort}\"");
            }

            return arguments;
        }

        public async Task<AiResult> ChatAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            string? effort = null,
            CancellationToken cancellationToken = default)
        {
            using var workspace = new CliWorkspace();
            var outputFilePath = Path.Combine(workspace.Path, ResultFileName);
            var arguments = BuildArguments(_modelName, effort, outputFilePath);
            var prompt = CliPrompt.Combine(systemPrompt ?? string.Empty, userPrompt ?? string.Empty);

            CliProcessResult processResult;
            try
            {
                processResult = await CliProcessRunner.RunAsync(
                    _command, arguments, prompt, workspace.Path, _timeout, cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw CliFailureClassifier.CommandNotFound(ProviderName, _command, ex);
            }

            if (!processResult.Succeeded)
            {
                throw CliFailureClassifier.ToException(ProviderName, _command, processResult, null);
            }

            if (!File.Exists(outputFilePath))
            {
                throw CliFailureClassifier.ToException(
                    ProviderName, _command, processResult,
                    "codex가 결과 파일을 남기지 않았습니다.");
            }

            var content = await File.ReadAllTextAsync(outputFilePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw CliFailureClassifier.ToException(
                    ProviderName, _command, processResult,
                    "codex가 빈 응답을 반환했습니다.");
            }

            return new AiResult { Content = content };
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CodexCliClientTests"`
Expected: PASS (8건)

- [ ] **Step 5: 전체 테스트를 돌린다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`
Expected: PASS, 490건

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/Clients/Cli/CodexCliClient.cs \
        tests/ReSet.Core.Tests/CodexCliClientTests.cs
git commit -F - <<'EOF'
feat(cli-provider): add CodexCliClient

Read the final answer from the -o file rather than stdout, which carries
progress logs. Codex has no separate system prompt, so the two prompts are
concatenated, and effort above high is clamped with a warning so a silently
weaker run does not go unexplained.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 5: AntigravityCliClient

세 클라이언트 중 유일하게 stdin을 못 받는다. 명령행 길이 검사가 이 태스크의 핵심이다.

**Files:**
- Create: `src/ReSet.Core/Services/Clients/Cli/AntigravityCliClient.cs`
- Test: `tests/ReSet.Core.Tests/AntigravityCliClientTests.cs`

**Interfaces:**
- Consumes: Task 1·2의 전부
- Produces:
  - `AntigravityCliClient(string command, string modelName, TimeSpan timeout)` — `IAiClient` 구현, `TimeSpan Timeout { get; }` 노출
  - `AntigravityCliClient.BuildArguments(string prompt, string modelName, string? effort, TimeSpan timeout) -> IReadOnlyList<string>`
  - `AntigravityCliClient.MaxCommandLineLength -> int` (정적 속성, 플랫폼별)
  - `AntigravityCliClient.EnsureCommandLineFits(string command, IReadOnlyList<string> arguments)` — 초과 시 `InvalidOperationException`
  - `AntigravityCliClient.ParseResult(string standardOutput) -> string`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/AntigravityCliClientTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class AntigravityCliClientTests
    {
        // 2026-08-03에 `agy -p --output-format json`을 실제로 호출해 받은 응답.
        private const string SuccessJson =
            "{\"conversation_id\":\"7d1a7000\",\"status\":\"SUCCESS\",\"response\":\"PONG\\n\"," +
            "\"duration_seconds\":3.32,\"num_turns\":1}";

        [Fact]
        public void BuildArguments_PassesPromptAsArgumentNotStdin()
        {
            // agy는 stdin으로 프롬프트를 받지 못한다 (실측: 툴 권한 오류로 빈 응답).
            var arguments = AntigravityCliClient.BuildArguments(
                "프롬프트 본문", "gemini", "high", TimeSpan.FromSeconds(600));

            var index = arguments.ToList().IndexOf("-p");
            Assert.True(index >= 0);
            Assert.Equal("프롬프트 본문", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_RequestsJsonOutputAndPassesTimeout()
        {
            var arguments = AntigravityCliClient.BuildArguments(
                "본문", "gemini", null, TimeSpan.FromSeconds(600));

            var formatIndex = arguments.ToList().IndexOf("--output-format");
            Assert.True(formatIndex >= 0);
            Assert.Equal("json", arguments[formatIndex + 1]);

            var timeoutIndex = arguments.ToList().IndexOf("--print-timeout");
            Assert.True(timeoutIndex >= 0);
            Assert.Equal("600s", arguments[timeoutIndex + 1]);
        }

        [Fact]
        public void BuildArguments_XhighEffort_IsClampedToHigh()
        {
            var arguments = AntigravityCliClient.BuildArguments(
                "본문", "gemini", "xhigh", TimeSpan.FromSeconds(600));

            var index = arguments.ToList().IndexOf("--effort");
            Assert.Equal("high", arguments[index + 1]);
        }

        [Fact]
        public void BuildArguments_WithBlankModel_OmitsModelFlag()
        {
            var arguments = AntigravityCliClient.BuildArguments(
                "본문", "", null, TimeSpan.FromSeconds(600));

            Assert.DoesNotContain("--model", arguments);
        }

        [Fact]
        public void MaxCommandLineLength_MatchesPlatformLimit()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Equal(32_767, AntigravityCliClient.MaxCommandLineLength);
            }
            else
            {
                Assert.True(AntigravityCliClient.MaxCommandLineLength > 100_000);
            }
        }

        [Fact]
        public void EnsureCommandLineFits_ShortPrompt_DoesNotThrow()
        {
            var arguments = AntigravityCliClient.BuildArguments(
                "짧은 본문", "gemini", null, TimeSpan.FromSeconds(600));

            AntigravityCliClient.EnsureCommandLineFits("agy", arguments);
        }

        // ReSet의 실제 최대 프롬프트는 191KB다. Windows 32KB 한계를 넘는다.
        [Fact]
        public void EnsureCommandLineFits_OverLimit_ThrowsWithActionableMessage()
        {
            var huge = new string('가', AntigravityCliClient.MaxCommandLineLength + 1000);
            var arguments = new List<string> { "-p", huge };

            var exception = Assert.Throws<InvalidOperationException>(() =>
                AntigravityCliClient.EnsureCommandLineFits("agy", arguments));

            Assert.Contains("agy-cli", exception.Message);
            Assert.Contains("claude-cli", exception.Message);
        }

        [Fact]
        public void ParseResult_Success_ExtractsResponseText()
        {
            Assert.Equal("PONG", AntigravityCliClient.ParseResult(SuccessJson).Trim());
        }

        [Fact]
        public void ParseResult_NonSuccessStatus_Throws()
        {
            const string failureJson =
                "{\"conversation_id\":\"x\",\"status\":\"ERROR\",\"response\":\"\"}";

            Assert.Throws<InvalidOperationException>(() =>
                AntigravityCliClient.ParseResult(failureJson));
        }

        [Fact]
        public void ParseResult_NotJson_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                AntigravityCliClient.ParseResult("이건 JSON이 아니다"));
        }

        [Fact]
        public void ProviderNameModelNameAndTimeout_AreExposed()
        {
            var client = new AntigravityCliClient("agy", "gemini", TimeSpan.FromSeconds(30));

            Assert.Equal("agy-cli", client.ProviderName);
            Assert.Equal("gemini", client.ModelName);
            Assert.Equal(30, client.Timeout.TotalSeconds);
        }

        [Fact]
        public async Task ChatAsync_MissingCommand_ThrowsWithInstallGuidance()
        {
            var client = new AntigravityCliClient(
                "reset_agy_does_not_exist_42", "gemini", TimeSpan.FromSeconds(10));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.ChatAsync("시스템 규칙", "사용자 프롬프트", 0.2f));

            Assert.Contains("PATH", exception.Message);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~AntigravityCliClientTests"`
Expected: 컴파일 실패 — `AntigravityCliClient` 형식을 찾을 수 없음

- [ ] **Step 3: AntigravityCliClient를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/AntigravityCliClient.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// Antigravity CLI를 print 모드로 기동한다.
    ///
    /// 세 CLI 중 유일하게 stdin으로 프롬프트를 받지 못한다(실측: 파이프로 주면 툴
    /// 권한 오류로 빈 응답). argv로 넘겨야 하는데 ReSet의 실제 최대 프롬프트는
    /// 191KB이고 Windows 명령행 한계는 32,767자다. 우회로가 없으므로 호출 전에
    /// 검사해 명확히 실패시킨다.
    /// </summary>
    public sealed class AntigravityCliClient : IAiClient
    {
        // Windows CreateProcess의 명령행 한계. 그 외 플랫폼은 ARG_MAX(리눅스·macOS
        // 공통 하한 수준)에서 환경 변수 몫을 빼고 보수적으로 잡는다.
        private const int WindowsCommandLineLimit = 32_767;
        private const int PosixCommandLineLimit = 1_000_000;

        private readonly string _command;
        private readonly string _modelName;
        private readonly TimeSpan _timeout;

        public string ProviderName => "agy-cli";
        public string ModelName => _modelName;

        /// <summary>팩토리가 HttpClient에서 읽어 넘긴 제한 시간. 배선이 끊기면 테스트가 잡는다.</summary>
        public TimeSpan Timeout => _timeout;

        public static int MaxCommandLineLength =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? WindowsCommandLineLimit
                : PosixCommandLineLimit;

        public AntigravityCliClient(string command, string modelName, TimeSpan timeout)
        {
            _command = string.IsNullOrWhiteSpace(command) ? "agy" : command;
            _modelName = modelName ?? string.Empty;
            _timeout = timeout;

            Log.Warning("{Provider}는 temperature를 지원하지 않습니다. 설정값은 무시됩니다.", ProviderName);
        }

        public static IReadOnlyList<string> BuildArguments(
            string prompt, string modelName, string? effort, TimeSpan timeout)
        {
            var arguments = new List<string>
            {
                "-p", prompt,
                "--output-format", "json",
                "--print-timeout", $"{(int)timeout.TotalSeconds}s"
            };

            if (!string.IsNullOrWhiteSpace(modelName))
            {
                arguments.Add("--model");
                arguments.Add(modelName);
            }

            var mappedEffort = CliEffort.ForThreeLevel(effort, out var clamped);
            if (mappedEffort != null)
            {
                if (clamped)
                {
                    Log.Warning(
                        "agy-cli는 low|medium|high만 지원합니다. 요청한 effort '{Requested}'를 '{Applied}'로 낮춥니다.",
                        effort, mappedEffort);
                }

                arguments.Add("--effort");
                arguments.Add(mappedEffort);
            }

            return arguments;
        }

        public static void EnsureCommandLineFits(string command, IReadOnlyList<string> arguments)
        {
            // 인용부호와 구분 공백을 감안해 인자마다 여유를 더한다.
            var length = command.Length;
            foreach (var argument in arguments)
            {
                length += argument.Length + 3;
            }

            if (length <= MaxCommandLineLength)
            {
                return;
            }

            throw new InvalidOperationException(
                $"이 프롬프트는 agy-cli로 처리할 수 없습니다 " +
                $"(명령행 {length:N0}자, 플랫폼 한계 {MaxCommandLineLength:N0}자). " +
                "agy는 프롬프트를 표준 입력으로 받지 못해 명령행으로 넘겨야 하며, 우회로가 없습니다. " +
                "claude-cli 또는 API provider를 사용하십시오.");
        }

        public static string ParseResult(string standardOutput)
        {
            try
            {
                using var document = JsonDocument.Parse(standardOutput);
                var root = document.RootElement;

                var status = root.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString()
                    : null;

                if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"agy-cli가 실패 상태를 반환했습니다 (status: {status}).\n[출력]\n{standardOutput}");
                }

                var response = root.TryGetProperty("response", out var responseElement)
                    ? responseElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(response))
                {
                    throw new InvalidOperationException(
                        $"agy-cli 응답에 response 속성이 없거나 비어 있습니다.\n[출력]\n{standardOutput}");
                }

                return response;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"agy-cli 응답을 JSON으로 해석할 수 없습니다.\n[출력]\n{standardOutput}", ex);
            }
        }

        public async Task<AiResult> ChatAsync(
            string systemPrompt,
            string userPrompt,
            float temperature,
            string? effort = null,
            CancellationToken cancellationToken = default)
        {
            // agy도 시스템 프롬프트를 따로 받지 않으므로 합친다.
            var prompt = CliPrompt.Combine(systemPrompt ?? string.Empty, userPrompt ?? string.Empty);

            var arguments = BuildArguments(prompt, _modelName, effort, _timeout);

            // 프로세스를 띄우기 전에 막는다. 조용히 잘리거나 알 수 없는 오류로 죽는 것보다 낫다.
            EnsureCommandLineFits(_command, arguments);

            using var workspace = new CliWorkspace();

            CliProcessResult processResult;
            try
            {
                processResult = await CliProcessRunner.RunAsync(
                    _command, arguments, null, workspace.Path, _timeout, cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw CliFailureClassifier.CommandNotFound(ProviderName, _command, ex);
            }

            if (!processResult.Succeeded)
            {
                throw CliFailureClassifier.ToException(ProviderName, _command, processResult, null);
            }

            return new AiResult { Content = ParseResult(processResult.StandardOutput) };
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~AntigravityCliClientTests"`
Expected: PASS (12건)

- [ ] **Step 5: 전체 테스트를 돌린다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`
Expected: PASS, 502건

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/Clients/Cli/AntigravityCliClient.cs \
        tests/ReSet.Core.Tests/AntigravityCliClientTests.cs
git commit -F - <<'EOF'
feat(cli-provider): add AntigravityCliClient with command-line size guard

agy is the only one of the three that cannot take the prompt on stdin, so it
has to go through argv. ReSet's largest real prompt is 191KB and Windows caps
a command line at 32,767 characters, so this combination genuinely cannot work
there. Check before spawning and say so plainly, naming a provider that can
handle it — a silent truncation would corrupt the analysis instead.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 6: 팩토리 배선과 설정 스키마

**Files:**
- Modify: `src/ReSet.Core/Services/Clients/AiClientFactory.cs`
- Modify: `src/ReSet.Cli/appsettings.json`
- Modify: `src/ReSet.Validator.Cli/appsettings.json`
- Test: `tests/ReSet.Core.Tests/AiClientFactoryTests.cs` (확장)

**Interfaces:**
- Consumes: `ClaudeCliClient`, `CodexCliClient`, `AntigravityCliClient` (Task 3·4·5)
- Produces:
  - `AiClientFactory.IsCliProvider(string provider) -> bool`
  - `AiClientFactory.CreateClient(string provider, string modelName, string apiKey, string endpoint, HttpClient? httpClient = null, int? numCtx = null, string? command = null) -> IAiClient`

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/AiClientFactoryTests.cs`의 클래스 본문 끝에 다음을 추가한다:

```csharp
        [Theory]
        [InlineData("claude-cli", typeof(ReSet.Core.Services.Clients.Cli.ClaudeCliClient))]
        [InlineData("Claude-CLI", typeof(ReSet.Core.Services.Clients.Cli.ClaudeCliClient))]
        [InlineData("codex-cli", typeof(ReSet.Core.Services.Clients.Cli.CodexCliClient))]
        [InlineData("agy-cli", typeof(ReSet.Core.Services.Clients.Cli.AntigravityCliClient))]
        public void CreateClient_WithCliProviders_ShouldReturnCorrectClientType(string provider, Type expectedType)
        {
            var client = AiClientFactory.CreateClient(provider, "model", "", "");
            Assert.IsType(expectedType, client);
        }

        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        [InlineData("agy-cli")]
        public void IsCliProvider_WithCliProviders_ReturnsTrue(string provider)
        {
            Assert.True(AiClientFactory.IsCliProvider(provider));
        }

        [Theory]
        [InlineData("claude")]
        [InlineData("openai")]
        [InlineData("ollama")]
        [InlineData("")]
        [InlineData(null)]
        public void IsCliProvider_WithNonCliProviders_ReturnsFalse(string? provider)
        {
            Assert.False(AiClientFactory.IsCliProvider(provider!));
        }

        // CLI provider는 로컬 LLM 분할 파이프라인의 대상이 아니다.
        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        [InlineData("agy-cli")]
        public void IsLocalProvider_WithCliProviders_ReturnsFalse(string provider)
        {
            Assert.False(AiClientFactory.IsLocalProvider(provider));
        }

        // 타임아웃은 새 매개변수가 아니라 이미 넘어오는 HttpClient에서 읽는다.
        // 설정이 한 곳에서만 관리되고 API 경로와 값이 어긋나지 않는다.
        [Fact]
        public void CreateClient_CliProvider_UsesHttpClientTimeout()
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1234) };
            var client = AiClientFactory.CreateClient("claude-cli", "sonnet", "", "", httpClient);

            var cliClient = Assert.IsType<ReSet.Core.Services.Clients.Cli.ClaudeCliClient>(client);
            Assert.Equal(1234, cliClient.Timeout.TotalSeconds);
        }

        // HttpClient를 주지 않으면 팩토리 기본값 300초를 쓴다.
        [Fact]
        public void CreateClient_CliProvider_WithoutHttpClient_FallsBackToDefaultTimeout()
        {
            var client = AiClientFactory.CreateClient("codex-cli", "gpt-5.6-terra", "", "");

            var cliClient = Assert.IsType<ReSet.Core.Services.Clients.Cli.CodexCliClient>(client);
            Assert.Equal(300, cliClient.Timeout.TotalSeconds);
        }

        [Fact]
        public void CreateClient_CliProvider_WithoutApiKey_DoesNotThrow()
        {
            // CLI provider는 API 키를 갖지 않는다.
            var client = AiClientFactory.CreateClient("claude-cli", "sonnet", "", "");
            Assert.NotNull(client);
        }

        [Fact]
        public void CreateClient_CliProvider_CustomCommandIsAccepted()
        {
            var client = AiClientFactory.CreateClient(
                "claude-cli", "sonnet", "", "", null, null, "/opt/homebrew/bin/claude");

            Assert.IsType<ReSet.Core.Services.Clients.Cli.ClaudeCliClient>(client);
        }
```

파일 상단의 `using`에 `using System;`이 이미 있는지 확인하고, 없으면 추가한다.

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~AiClientFactoryTests"`
Expected: 컴파일 실패 — `IsCliProvider`가 없고 `CreateClient`의 인수 개수가 맞지 않음

- [ ] **Step 3: AiClientFactory를 수정한다**

`src/ReSet.Core/Services/Clients/AiClientFactory.cs` 전체를 다음으로 교체한다:

```csharp
using System;
using System.Net.Http;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Services.Clients
{
    public static class AiClientFactory
    {
        public static bool IsLocalProvider(string provider)
        {
            var p = provider?.ToLowerInvariant();
            return p == "ollama" || p == "local-openai" || p == "mlx" || p == "vllm";
        }

        /// <summary>
        /// 로컬에 설치된 CLI 코딩 에이전트를 백엔드로 쓰는 provider인가.
        /// API 키가 필요 없고, 무인 배치 모드에서는 사용할 수 없다.
        /// </summary>
        public static bool IsCliProvider(string provider)
        {
            var p = provider?.ToLowerInvariant();
            return p == "claude-cli" || p == "codex-cli" || p == "agy-cli";
        }

        public static IAiClient CreateClient(
            string provider,
            string modelName,
            string apiKey,
            string endpoint,
            HttpClient? httpClient = null,
            int? numCtx = null,
            string? command = null)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                throw new ArgumentException("AI Provider가 지정되지 않았습니다.", nameof(provider));
            }

            var normalizedProvider = provider.ToLowerInvariant();

            // CLI provider는 HttpClient를 쓰지 않는다. 아래에서 새로 만들기 전에 분기한다.
            // 타임아웃은 호출부가 AiSettings:TimeoutSeconds로 구성한 HttpClient에서
            // 읽어, 설정이 한 곳에서만 관리되도록 한다.
            if (IsCliProvider(normalizedProvider))
            {
                var timeout = httpClient?.Timeout ?? TimeSpan.FromSeconds(300);

                return normalizedProvider switch
                {
                    "claude-cli" => new ClaudeCliClient(command ?? "claude", modelName, timeout),
                    "codex-cli" => new CodexCliClient(command ?? "codex", modelName, timeout),
                    "agy-cli" => new AntigravityCliClient(command ?? "agy", modelName, timeout),
                    _ => throw new NotSupportedException($"지원되지 않는 AI Provider입니다: {provider}")
                };
            }

            var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(300) };

            return normalizedProvider switch
            {
                "openai" => new OpenAiClient(client, apiKey, endpoint, modelName, numCtx, "OpenAI"),
                "local-openai" => new OpenAiClient(client, apiKey, endpoint, modelName, numCtx, "local-openai"),
                "mlx" => new OpenAiClient(client, apiKey, endpoint, modelName, numCtx, "mlx"),
                "vllm" => new OpenAiClient(client, apiKey, endpoint, modelName, numCtx, "vllm"),
                "ollama" => new OllamaClient(client, endpoint, modelName, numCtx),
                "claude" => new ClaudeClient(client, apiKey, endpoint, modelName),
                "anthropic" => new ClaudeClient(client, apiKey, endpoint, modelName),
                "google" => new GoogleClient(client, apiKey, endpoint, modelName),
                "z.ai" => new ZaiClient(client, apiKey, endpoint, modelName),
                "zai" => new ZaiClient(client, apiKey, endpoint, modelName),
                _ => throw new NotSupportedException($"지원되지 않는 AI Provider입니다: {provider}")
            };
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~AiClientFactoryTests"`
Expected: PASS

- [ ] **Step 5: 설정 파일에 대한 실패하는 테스트를 작성한다**

설정 키가 실제로 해석되는지 확인하는 테스트다. `ReSet.Cli`는 `--help`를 지원하지 않아 실행으로는 검증할 수 없다.

`tests/ReSet.Core.Tests/CliProviderSettingsTests.cs`:

```csharp
using System.IO;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CliProviderSettingsTests
    {
        private static IConfiguration Load(string relativePath)
        {
            var fullPath = Path.Combine(RepoPaths.FindRepoRoot(), relativePath);
            Assert.True(File.Exists(fullPath), $"설정 파일을 찾을 수 없습니다: {fullPath}");

            // 기존 appsettings.json은 주석을 쓴다. IConfiguration의 JSON 공급자는 이를 허용한다.
            return new ConfigurationBuilder()
                .AddJsonFile(fullPath, optional: false)
                .Build();
        }

        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_DeclareAllThreeCliProviders(string relativePath)
        {
            var configuration = Load(relativePath);

            Assert.Equal("claude", configuration["AiSettings:Providers:claude-cli:Command"]);
            Assert.Equal("codex", configuration["AiSettings:Providers:codex-cli:Command"]);
            Assert.Equal("agy", configuration["AiSettings:Providers:agy-cli:Command"]);
        }

        // CLI provider는 API 키를 갖지 않는다. 빈 키라도 넣어두면 다른 곳의
        // "키가 있으니 API provider겠지" 판단을 흐린다.
        [Theory]
        [InlineData("src/ReSet.Cli/appsettings.json")]
        [InlineData("src/ReSet.Validator.Cli/appsettings.json")]
        public void AppSettings_CliProvidersDeclareNoApiKey(string relativePath)
        {
            var configuration = Load(relativePath);

            Assert.Null(configuration["AiSettings:Providers:claude-cli:ApiKey"]);
            Assert.Null(configuration["AiSettings:Providers:codex-cli:ApiKey"]);
            Assert.Null(configuration["AiSettings:Providers:agy-cli:ApiKey"]);
        }
    }
}
```

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CliProviderSettingsTests"`
Expected: FAIL — `claude-cli:Command`가 null

- [ ] **Step 6: appsettings.json에 CLI provider를 추가한다**

`src/ReSet.Cli/appsettings.json`의 `AiSettings:Provider` 주석과 `Providers` 블록을 수정한다.

`Provider` 줄의 주석을 다음으로 바꾼다:

```json
    "Provider": "Claude",              // 활성화할 AI 제공자 ("OpenAI" | "Google" | "Claude" | "Ollama" | "mlx" | "local-openai" | "Z.ai" | "claude-cli" | "codex-cli" | "agy-cli")
```

`Providers` 블록의 `"Z.ai"` 항목 뒤(닫는 중괄호 앞)에 다음을 추가한다:

```json
      },
      "claude-cli": {
        "Command": "claude"            // Claude Code CLI 명령어. PATH에 없으면 절대 경로 지정. API 키 불필요(CLI 로그인 계정 사용)
      },
      "codex-cli": {
        "Command": "codex"             // Codex CLI 명령어
      },
      "agy-cli": {
        "Command": "agy"               // Antigravity CLI 명령어. 프롬프트를 명령행으로 넘기므로 Windows에서 32KB를 넘는 대형 SP는 처리할 수 없음
      }
```

`src/ReSet.Validator.Cli/appsettings.json`에도 동일한 세 항목을 `AiSettings:Providers` 아래에 추가한다.

- [ ] **Step 7: 설정 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CliProviderSettingsTests"`
Expected: PASS (4건)

실패하면 JSON 문법이 깨진 것이다. 두 파일 모두 주석을 쓰므로 쉼표 위치를 확인한다.

- [ ] **Step 8: 빌드하고 전체 테스트를 돌린다**

Run: `dotnet build ReSet.slnx`
Expected: 빌드 성공

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`
Expected: PASS, 525건

- [ ] **Step 9: 커밋한다**

```bash
git add src/ReSet.Core/Services/Clients/AiClientFactory.cs \
        src/ReSet.Cli/appsettings.json \
        src/ReSet.Validator.Cli/appsettings.json \
        tests/ReSet.Core.Tests/AiClientFactoryTests.cs \
        tests/ReSet.Core.Tests/CliProviderSettingsTests.cs
git commit -F - <<'EOF'
feat(cli-provider): register the three CLI providers in the factory

Branch to the CLI clients before the HttpClient is constructed, since they
never make an HTTP request. The timeout still comes from the caller's
HttpClient so AiSettings:TimeoutSeconds stays the single source and cannot
drift from the API path.

Validator.Cli goes through the same factory, so its gap analysis and mock data
generation pick this up with no further wiring.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## Task 7: 배치 모드 가드, 호출부 배선, 문서

마지막 태스크. CLI provider를 실제로 쓸 수 있게 하고, 무인 배치에서는 못 쓰게 막는다.

**Files:**
- Create: `src/ReSet.Core/Services/Clients/Cli/CliProviderBatchGuard.cs`
- Modify: `src/ReSet.Cli/Program.cs` (`:146` 부근, `:383`, `:406`, `:427`)
- Modify: `src/ReSet.Validator.Cli/Program.cs` (`:202`, `:205`, `:222`)
- Test: `tests/ReSet.Core.Tests/CliProviderBatchGuardTests.cs`

**Interfaces:**
- Consumes: `AiClientFactory.IsCliProvider` (Task 6)
- Produces: `CliProviderBatchGuard.FindBlockedRole(string actorProvider, string? criticProvider, string? consolidatorProvider) -> string?` — 차단 대상이 없으면 `null`, 있으면 역할 이름(`"Actor"` / `"Critic"` / `"Consolidator"`)

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/CliProviderBatchGuardTests.cs`:

```csharp
using Xunit;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Tests
{
    public class CliProviderBatchGuardTests
    {
        [Fact]
        public void FindBlockedRole_AllApiProviders_ReturnsNull()
        {
            Assert.Null(CliProviderBatchGuard.FindBlockedRole("Claude", "OpenAI", "Claude"));
        }

        [Fact]
        public void FindBlockedRole_ActorIsCli_ReturnsActor()
        {
            Assert.Equal("Actor",
                CliProviderBatchGuard.FindBlockedRole("claude-cli", "OpenAI", "Claude"));
        }

        // Actor가 API여도 Critic이 CLI면 같은 사고가 난다. 세 역할을 모두 봐야 한다.
        [Fact]
        public void FindBlockedRole_CriticIsCli_ReturnsCritic()
        {
            Assert.Equal("Critic",
                CliProviderBatchGuard.FindBlockedRole("Claude", "codex-cli", "Claude"));
        }

        [Fact]
        public void FindBlockedRole_ConsolidatorIsCli_ReturnsConsolidator()
        {
            Assert.Equal("Consolidator",
                CliProviderBatchGuard.FindBlockedRole("Claude", "OpenAI", "agy-cli"));
        }

        // Critic/Consolidator를 지정하지 않으면 Actor 설정을 물려받는다.
        [Fact]
        public void FindBlockedRole_NullRoleProviders_FallBackToActor()
        {
            Assert.Equal("Actor",
                CliProviderBatchGuard.FindBlockedRole("claude-cli", null, null));
        }

        [Fact]
        public void FindBlockedRole_NullRoleProvidersWithApiActor_ReturnsNull()
        {
            Assert.Null(CliProviderBatchGuard.FindBlockedRole("Claude", null, null));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CliProviderBatchGuardTests"`
Expected: 컴파일 실패 — `CliProviderBatchGuard` 형식을 찾을 수 없음

- [ ] **Step 3: CliProviderBatchGuard를 구현한다**

`src/ReSet.Core/Services/Clients/Cli/CliProviderBatchGuard.cs`:

```csharp
namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// 무인 배치 모드에서 CLI provider 사용을 차단할지 판정한다.
    ///
    /// 배치 도중 구독 쿼터가 소진되거나 CLI가 권한 프롬프트에서 멈추면 수십 분에서
    /// 수 시간짜리 실행이 통째로 날아간다. 시작 5초 만에 실패하는 편이 낫다.
    /// </summary>
    public static class CliProviderBatchGuard
    {
        /// <summary>
        /// 차단해야 할 역할 이름을 돌려준다. 문제가 없으면 null.
        /// criticProvider와 consolidatorProvider가 null이면 Actor 설정을 물려받는다.
        /// </summary>
        public static string? FindBlockedRole(
            string actorProvider,
            string? criticProvider,
            string? consolidatorProvider)
        {
            // 이 네임스페이스는 ReSet.Core.Services.Clients 안에 있으므로
            // AiClientFactory가 using 없이 그대로 보인다.
            if (AiClientFactory.IsCliProvider(actorProvider))
            {
                return "Actor";
            }

            if (AiClientFactory.IsCliProvider(criticProvider ?? actorProvider))
            {
                return "Critic";
            }

            if (AiClientFactory.IsCliProvider(consolidatorProvider ?? actorProvider))
            {
                return "Consolidator";
            }

            return null;
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~CliProviderBatchGuardTests"`
Expected: PASS (6건)

- [ ] **Step 5: ReSet.Cli/Program.cs에 가드와 Command 로드를 넣는다**

`src/ReSet.Cli/Program.cs:146` 부근, `endpoint`를 읽는 줄 바로 다음에 추가한다:

```csharp
            var endpoint = configuration[$"AiSettings:Providers:{provider}:Endpoint"] ?? string.Empty;
            var cliCommand = configuration[$"AiSettings:Providers:{provider}:Command"];

            // 무인 배치 도중 구독 쿼터가 소진되거나 권한 프롬프트에서 멈추면 장시간
            // 실행이 통째로 날아간다. 시작 직후에 막는다.
            if (cliArgs.IsBatchMode)
            {
                var blockedRole = ReSet.Core.Services.Clients.Cli.CliProviderBatchGuard.FindBlockedRole(
                    provider,
                    configuration["AiSettings:Critic:Provider"],
                    configuration["AiSettings:Consolidator:Provider"]);

                if (blockedRole != null)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]에러: 배치 모드에서는 CLI provider를 사용할 수 없습니다. ({Markup.Escape(blockedRole)} 역할)[/]");
                    AnsiConsole.MarkupLine(
                        "[yellow]CLI provider는 구독 쿼터 소진이나 권한 프롬프트로 무인 실행 도중 중단될 수 있습니다. appsettings.json에서 API provider로 변경해 주십시오.[/]");
                    return;
                }
            }
```

`:383`의 팩토리 호출을 수정한다:

```csharp
            IAiClient aiClient = ReSet.Core.Services.Clients.AiClientFactory.CreateClient(provider, modelName, apiKey, endpoint, httpClient, numCtx, cliCommand);
```

`:406`의 Critic 호출: 그 위에 Command 로드를 추가하고 호출을 수정한다.

```csharp
                var criticCommand = configuration[$"AiSettings:Providers:{criticProvider}:Command"];
                var criticClient = ReSet.Core.Services.Clients.AiClientFactory.CreateClient(criticProvider, criticModel, criticApiKey, criticEndpoint, httpClient, criticNumCtx, criticCommand);
```

`:427`의 Consolidator 호출도 같은 방식으로 수정한다.

```csharp
                var consolidatorCommand = configuration[$"AiSettings:Providers:{consolidatorProvider}:Command"];
                var consolidatorClient = ReSet.Core.Services.Clients.AiClientFactory.CreateClient(consolidatorProvider, consolidatorModel, consolidatorApiKey, consolidatorEndpoint, httpClient, consolidatorNumCtx, consolidatorCommand);
```

- [ ] **Step 6: ReSet.Validator.Cli/Program.cs의 API 키 가드를 고친다**

`:205`의 가드는 CLI provider를 API 키 누락으로 오판해 실행을 막는다. CLI provider는 키를 갖지 않는다.

`:202`~`:210`을 다음으로 교체한다:

```csharp
            var apiKey = LoadApiKeyWithFallback(configuration, provider);
            var endpoint = configuration[$"AiSettings:Providers:{provider}:Endpoint"] ?? string.Empty;
            var cliCommand = configuration[$"AiSettings:Providers:{provider}:Command"];

            // CLI provider는 CLI에 로그인된 구독 계정을 쓰므로 API 키가 없다.
            if (string.IsNullOrEmpty(apiKey)
                && !provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
                && !AiClientFactory.IsCliProvider(provider))
            {
                AnsiConsole.MarkupLine($"[red]에러: {provider} AI 클라이언트를 구동하기 위한 API Key가 설정되어 있지 않습니다.[/]");
                AnsiConsole.MarkupLine("[yellow]src/ReSet.Validator.Cli/appsettings.local.json 에 ApiKey를 지정해 주세요.[/]");
                return;
            }
```

`:222`의 팩토리 호출을 수정한다:

```csharp
                aiClient = AiClientFactory.CreateClient(provider, modelName, apiKey, endpoint, httpClient, null, cliCommand);
```

- [ ] **Step 7: 빌드하고 전체 테스트를 돌린다**

Run: `dotnet build ReSet.slnx`
Expected: 빌드 성공, 새 경고 없음

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj`
Expected: PASS, 531건

- [ ] **Step 8: 배치 가드가 실제로 동작하는지 확인한다**

`src/ReSet.Cli/appsettings.local.json`이 없다면 만들지 말고, 임시로 `src/ReSet.Cli/appsettings.json`의 `Provider`를 `"claude-cli"`로 바꾼 뒤 배치 모드로 실행한다.

Run: `dotnet run --project src/ReSet.Cli -- --batch --sp dbo.AnySp --conn "Server=localhost;Database=x;"`
Expected: `에러: 배치 모드에서는 CLI provider를 사용할 수 없습니다. (Actor 역할)` 이 출력되고 DB 연결을 시도하지 않은 채 즉시 종료

확인 후 `Provider`를 원래 값(`"Claude"`)으로 되돌린다.

- [ ] **Step 9: 문서를 동기화한다**

`reset-doc-sync` 스킬을 호출해 `README.md`, `AGENTS.md`, `docs/architecture.md`를 갱신한다.

Run: 스킬 `reset-doc-sync` 호출

반영해야 할 내용:
- AI Provider 목록에 `claude-cli`, `codex-cli`, `agy-cli` 추가 (README 상단 배지 포함)
- CLI provider는 대화형 TUI 전용이며 배치 모드에서 차단된다는 점
- temperature가 지원되지 않는다는 점
- `agy-cli`는 Windows에서 32KB를 넘는 프롬프트를 처리할 수 없다는 점
- 실패 시 자동 폴백이 없으며 사용자가 provider를 바꿔 재실행한다는 점

- [ ] **Step 10: 커밋한다**

```bash
git add src/ReSet.Core/Services/Clients/Cli/CliProviderBatchGuard.cs \
        src/ReSet.Cli/Program.cs \
        src/ReSet.Validator.Cli/Program.cs \
        tests/ReSet.Core.Tests/CliProviderBatchGuardTests.cs \
        README.md AGENTS.md docs/architecture.md
git commit -F - <<'EOF'
feat(cli-provider): wire CLI providers into both entry points

Block CLI providers in unattended batch mode across all three roles. Actor
being an API provider is not enough — a CLI Critic hits the same wall, and
losing a multi-hour batch run to an exhausted quota costs far more than
failing five seconds in.

Validator.Cli rejected empty API keys for everything but Ollama, which would
have locked out CLI providers that legitimately have no key.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## 완료 기준

- [ ] `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj` — 531건 전부 통과
- [ ] `dotnet build ReSet.slnx` — 새 경고 없음
- [ ] `CancellationPolicyTests` 통과, `cancellation-policy-baseline.txt` 변경 없음
- [ ] `appsettings.json`의 `Provider`를 `claude-cli`로 두고 TUI로 SP 하나를 실제 분석해 명세서가 나오는지 확인
- [ ] 같은 설정으로 배치 모드 실행 시 즉시 차단되는지 확인
- [ ] README·AGENTS·architecture 문서가 CLI provider를 반영

## 수동 확인이 필요한 것

자동 테스트가 닿지 않는 영역이다. 정직하게 남긴다.

| 항목 | 이유 |
|---|---|
| 실제 CLI 호출의 응답 품질 | 구독 로그인과 네트워크가 필요하다. API 경로와 산출물을 비교해 확인해야 한다 |
| 쿼터 소진 메시지의 실제 문구 | 소진시켜야 확인된다. 분류 마커가 빗나가면 `Unknown`으로 떨어지지만 stderr 원문이 남으므로 진단은 가능하다 |
| Windows에서 agy 길이 초과 | 현재 개발 환경은 macOS다. 한계값 상수는 테스트로 고정했으나 실제 Windows 실행은 확인되지 않았다 |
| 191KB 프롬프트의 stdin 전달 | 대형 SP를 실제로 분석해야 확인된다. `CliProcessRunnerTests`가 300KB stdout으로 데드락 부재를 검증했지만 stdin 대용량은 실제 호출로 확인해야 한다 |
