# 코딩 에이전트 브릿지 헤드리스 배치 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 무인 배치(`--job-name`) 실행에서 외부 코딩 에이전트가 실제로 코드를 생성하게 하고, 아무것도 못 한 실행이 종료 코드 0으로 성공을 위장하지 못하게 막는다.

**Architecture:** 엔진별 인자를 대화형(`Arguments`)과 무인(`BatchArguments`)으로 분리하고, 모드 선택을 팩토리 생성 시점에 끝낸다. 성공 판정은 종료 코드 대신 "작업 디렉터리에 산출물 변화가 있었는가"로 바꾸고, 배치에서만 stderr를 캡처해 기존 `CliFailureClassifier`로 원인을 분류한다. 루프 계속 여부는 순수 함수 `CodegenLoopPolicy.Decide`가 결정해 프로세스 없이 테스트한다.

**Tech Stack:** C# / .NET 10, xUnit, NSubstitute, Serilog, Spectre.Console

**Spec:** `docs/superpowers/specs/2026-08-07-codegen-headless-design.md`

## Global Constraints

- 대상 프레임워크 `net10.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`.
- 코드 주석과 사용자 노출 문자열은 **한국어**.
- 취소 가능한 `await`를 감싸는 모든 넓은 `catch`에 `when (ex is not OperationCanceledException)` 필터를 단다. `CancellationPolicyTests`가 Roslyn 구문 트리로 자동 검사하며, `ExternalCliCodingEngine`과 `CodegenWorkflowOrchestrator`는 현재 `cancellation-policy-baseline.txt`에 **없다**(위반 없음). 이 상태를 유지한다.
- **새 설정 키는 `CodegenSettings:Engines:<name>:BatchArguments` 하나뿐이다.** 다른 키를 추가하지 않는다.
- **agy의 `BatchArguments`는 빈 문자열로 둔다.** `--dangerously-skip-permissions`는 무인 배치에서 임의 명령 실행을 허용하고 설계 시점에 실측 검증도 하지 못했다. 검증되지 않은 위험 플래그를 기본 설정으로 배포하지 않는다.
- `BatchArguments`가 비었을 때 `Arguments`로 **폴백하지 않는다.** 대화형 인자로 무인 실행하면 TTY 오류로 조용히 실패한다.
- 대화형 모드의 스트림 상속(`RedirectStandard* = false`)을 바꾸지 않는다. AGENTS.md 범주 6 "프로세스 양방향 제어" 규칙이다.
- 타임아웃을 추가하지 않는다. 코딩 에이전트가 수십 분 도는 것은 정상이다.
- 착수 시점 실측값(워크트리 `worktree-codegen-headless`, 베이스 `origin/main` 9e13c04): `dotnet test` **746건 통과**, 빌드 경고 **8건**(`DbMetadataServiceTests`의 CS8600/CS8602). 경고를 늘리지 않는다.

---

## File Structure

**신규 파일**

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/ArgumentTemplateResolver.cs` | 인자 템플릿 자리표시자 치환 (순수 함수) |
| `src/ReSet.Core/Services/ArtifactChangeDetector.cs` | 작업 디렉터리 스냅샷 및 변화 비교 |
| `src/ReSet.Core/Models/CodegenRunResult.cs` | 엔진 1회 기동 결과 |
| `src/ReSet.Validator.Core/Services/CodegenLoopPolicy.cs` | 루프 계속 여부 판단 (순수 함수) + `CodegenLoopDecision` |
| `src/ReSet.Validator.Core/Models/CodegenWorkflowResult.cs` | 자가 수정 워크플로우 결과 |
| `tests/ReSet.Core.Tests/ArgumentTemplateResolverTests.cs` | 치환 규칙 |
| `tests/ReSet.Core.Tests/ArtifactChangeDetectorTests.cs` | 스냅샷·비교·제외 규칙 |
| `tests/ReSet.Core.Tests/CodegenLoopPolicyTests.cs` | 루프 판단 전 조합 |

**수정 파일**

| 파일 | 변경 |
|---|---|
| `src/ReSet.Core/Services/ICodingEngine.cs` | 반환형 `bool` → `CodegenRunResult`, `Command` 속성 추가 |
| `src/ReSet.Core/Services/ExternalCliCodingEngine.cs` | 헤드리스 생성자 인자, 모드별 스트림, 스냅샷, 분류, 작업 디렉터리 보장, 예외 문구 |
| `src/ReSet.Cli/CodingEngineFactory.cs` | `CreateEngine(engineName, isBatchMode)` |
| `src/ReSet.Cli/Program.cs` | 팩토리 호출(1999행), 워크플로우 결과 출력(2045-2060행) |
| `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs` | 루프 제어, 반환형 |
| `src/ReSet.Cli/appsettings.json` | 세 엔진 `BatchArguments` |
| `tests/ReSet.Core.Tests/CodingEngineTests.cs` | 생성자·팩토리 시그니처 반영, 배치 선택 테스트 |
| `README.md`, `AGENTS.md` | 문서 동기화 |

---

## Task 1: 인자 템플릿 치환기 분리

**Files:**
- Create: `src/ReSet.Core/Services/ArgumentTemplateResolver.cs`
- Test: `tests/ReSet.Core.Tests/ArgumentTemplateResolverTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public static string ArgumentTemplateResolver.Resolve(string argumentsTemplate, string instructionsFilePath)`
  - `public static string ArgumentTemplateResolver.ResolveJobDirectory(string instructionsFilePath)`

### 배경 (구현자가 알아야 할 것)

지금 치환은 `ExternalCliCodingEngine.GenerateCodeAsync` 33행 한 줄에 묻혀 있어 프로세스를 띄우지 않고는 검증할 수 없다.

`{jobDir}`가 필요한 이유: 브릿지는 작업 디렉터리를 `<job>/src`로 주는데 지시서는 `<job>/agent/MigrationInstructions.md`에 있다. claude는 cwd 바깥 파일을 읽을 때 권한을 요구하고 헤드리스에서는 물을 수 없어 자동 거부한다. `--add-dir <job>`으로 풀리지만 그 경로를 설정에서 표현할 방법이 지금은 없다.

지시서 경로가 `<job>/agent/MigrationInstructions.md` 형태이므로 **두 단계 위**가 Job 루트다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

Create `tests/ReSet.Core.Tests/ArgumentTemplateResolverTests.cs`:

```csharp
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
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ArgumentTemplateResolverTests"`
Expected: 컴파일 실패 — `ArgumentTemplateResolver` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 최소 구현을 작성한다**

Create `src/ReSet.Core/Services/ArgumentTemplateResolver.cs`:

```csharp
using System.IO;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코딩 엔진 인자 템플릿의 자리표시자를 절대 경로로 치환한다.
    ///
    /// 프로세스를 띄우지 않고 검증할 수 있도록 ExternalCliCodingEngine에서 분리했다.
    /// 경로에 공백이 있을 수 있으므로 치환값은 항상 쌍따옴표로 감싼다.
    /// </summary>
    public static class ArgumentTemplateResolver
    {
        public static string Resolve(string argumentsTemplate, string instructionsFilePath)
        {
            var instructions = Path.GetFullPath(instructionsFilePath);
            var jobDir = ResolveJobDirectory(instructions);

            return argumentsTemplate
                .Replace("{instructions}", Quote(instructions))
                .Replace("{jobDir}", Quote(jobDir));
        }

        /// <summary>
        /// 지시서는 &lt;job&gt;/agent/MigrationInstructions.md에 놓이므로 두 단계 위가 Job 루트다.
        /// 관례 밖 경로가 들어와도 던지지 않고 올라갈 수 있는 만큼만 올라간다.
        /// </summary>
        public static string ResolveJobDirectory(string instructionsFilePath)
        {
            var full = Path.GetFullPath(instructionsFilePath);

            var agentDir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(agentDir))
            {
                return full;
            }

            var jobDir = Path.GetDirectoryName(agentDir);
            return string.IsNullOrEmpty(jobDir) ? agentDir : jobDir;
        }

        private static string Quote(string path) => $"\"{path}\"";
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ArgumentTemplateResolverTests"`
Expected: PASS 6건

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/ArgumentTemplateResolver.cs tests/ReSet.Core.Tests/ArgumentTemplateResolverTests.cs
git commit -m "feat: extract argument template resolution with a {jobDir} placeholder"
```

---

## Task 2: 산출물 변화 감지기

**Files:**
- Create: `src/ReSet.Core/Services/ArtifactChangeDetector.cs`
- Test: `tests/ReSet.Core.Tests/ArtifactChangeDetectorTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public static IReadOnlyDictionary<string, string> ArtifactChangeDetector.Snapshot(string directory)`
  - `public static bool ArtifactChangeDetector.HasChanged(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)`

### 배경 (구현자가 알아야 할 것)

claude와 agy는 아무 파일도 못 만들고도 종료 코드 0을 돌려준다(설계 문서 실측표). 종료 코드를 믿을 수 없으므로 "작업 디렉터리가 실제로 달라졌는가"로 판정한다.

`bin`·`obj` 제외가 핵심이다. 이게 없으면 에이전트가 코드는 안 쓰고 `dotnet build`만 돌려도 "산출물 생성"으로 잡혀 감지가 무력해진다.

**테스트 작성 시 주의:** 파일 수정 감지를 시험할 때 같은 길이의 내용으로 덮어쓰면 파일시스템 타임스탬프 정밀도에 따라 스냅샷 값이 같을 수 있다. **길이가 다른 내용**으로 바꿔 검증한다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

Create `tests/ReSet.Core.Tests/ArtifactChangeDetectorTests.cs`:

```csharp
using System;
using System.IO;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ArtifactChangeDetectorTests : IDisposable
    {
        private readonly string _root;

        public ArtifactChangeDetectorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "reset-artifact-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private void WriteFile(string relativePath, string content)
        {
            var full = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        [Fact]
        public void Snapshot_ShouldReturnEmpty_WhenDirectoryDoesNotExist()
        {
            var snapshot = ArtifactChangeDetector.Snapshot(Path.Combine(_root, "없는폴더"));

            Assert.Empty(snapshot);
        }

        [Fact]
        public void HasChanged_ShouldBeFalse_WhenNothingHappened()
        {
            WriteFile("Program.cs", "class C {}");

            var before = ArtifactChangeDetector.Snapshot(_root);
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.False(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void HasChanged_ShouldBeTrue_WhenFileAdded()
        {
            var before = ArtifactChangeDetector.Snapshot(_root);
            WriteFile("Step1.cs", "class Step1 {}");
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.True(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void HasChanged_ShouldBeTrue_WhenFileModified()
        {
            WriteFile("Step1.cs", "class Step1 {}");
            var before = ArtifactChangeDetector.Snapshot(_root);

            // 길이가 달라지도록 고쳐야 타임스탬프 정밀도에 의존하지 않는다.
            WriteFile("Step1.cs", "class Step1 { public void Run() {} }");
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.True(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void HasChanged_ShouldBeTrue_WhenFileDeleted()
        {
            WriteFile("Step1.cs", "class Step1 {}");
            var before = ArtifactChangeDetector.Snapshot(_root);

            File.Delete(Path.Combine(_root, "Step1.cs"));
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.True(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void HasChanged_ShouldBeFalse_WhenOnlyBuildOutputChanged()
        {
            WriteFile("Program.cs", "class C {}");
            var before = ArtifactChangeDetector.Snapshot(_root);

            // 에이전트가 코드는 안 쓰고 빌드만 돌린 상황
            WriteFile(Path.Combine("bin", "Debug", "app.dll"), "binary");
            WriteFile(Path.Combine("obj", "project.assets.json"), "{}");
            var after = ArtifactChangeDetector.Snapshot(_root);

            Assert.False(ArtifactChangeDetector.HasChanged(before, after));
        }

        [Fact]
        public void Snapshot_ShouldIncludeNestedSourceFiles()
        {
            WriteFile(Path.Combine("Steps", "Step1.cs"), "class Step1 {}");

            var snapshot = ArtifactChangeDetector.Snapshot(_root);

            Assert.Single(snapshot);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ArtifactChangeDetectorTests"`
Expected: 컴파일 실패 — `ArtifactChangeDetector` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 최소 구현을 작성한다**

Create `src/ReSet.Core/Services/ArtifactChangeDetector.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코딩 에이전트가 작업 디렉터리에 실제로 무언가를 남겼는지 판정한다.
    ///
    /// 종료 코드는 믿을 수 없다. claude와 agy는 권한 자동 거부로 아무것도 못 하고도
    /// 0을 반환한다. 그래서 파일시스템 변화를 직접 본다.
    /// </summary>
    public static class ArtifactChangeDetector
    {
        // 빌드 부산물을 세면 에이전트가 코드는 안 쓰고 빌드만 돌려도 "산출물 생성"으로
        // 잡혀 이 감지 자체가 무력해진다.
        private static readonly string[] ExcludedDirectories =
        {
            "bin", "obj", ".git", "node_modules", ".vs", "target"
        };

        public static IReadOnlyDictionary<string, string> Snapshot(string directory)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return snapshot;
            }

            var root = Path.GetFullPath(directory);

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (IsExcluded(relative))
                {
                    continue;
                }

                var info = new FileInfo(file);
                snapshot[relative] = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }

            return snapshot;
        }

        public static bool HasChanged(
            IReadOnlyDictionary<string, string> before,
            IReadOnlyDictionary<string, string> after)
        {
            if (before.Count != after.Count)
            {
                return true;
            }

            foreach (var entry in after)
            {
                if (!before.TryGetValue(entry.Key, out var previous) || previous != entry.Value)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsExcluded(string relativePath)
        {
            var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 마지막 세그먼트는 파일명이므로 디렉터리 세그먼트만 본다.
            for (var i = 0; i < segments.Length - 1; i++)
            {
                foreach (var excluded in ExcludedDirectories)
                {
                    if (string.Equals(excluded, segments[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ArtifactChangeDetectorTests"`
Expected: PASS 7건

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/ArtifactChangeDetector.cs tests/ReSet.Core.Tests/ArtifactChangeDetectorTests.cs
git commit -m "feat: detect whether a codegen run actually touched the working tree"
```

---

## Task 3: 엔진 결과 모델과 헤드리스 실행

**Files:**
- Create: `src/ReSet.Core/Models/CodegenRunResult.cs`
- Modify: `src/ReSet.Core/Services/ICodingEngine.cs` — 전체
- Modify: `src/ReSet.Core/Services/ExternalCliCodingEngine.cs` — 전체
- Modify: `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs:49` — 호출부 최소 적응
- Modify: `tests/ReSet.Core.Tests/CodingEngineTests.cs:56` — 생성자 인자 추가

**Interfaces:**
- Consumes:
  - `ArgumentTemplateResolver.Resolve(string, string)` (Task 1)
  - `ArtifactChangeDetector.Snapshot(string)` / `HasChanged(...)` (Task 2)
- Produces:
  - `public sealed record CodegenRunResult(bool ProducedArtifacts, int ExitCode, CliFailureKind FailureKind, string? Diagnostic)` — `ReSet.Core.Models`
  - `Task<CodegenRunResult> ICodingEngine.GenerateCodeAsync(SpDefinition?, string, string, CancellationToken)`
  - `string ICodingEngine.Command { get; }`
  - `public ExternalCliCodingEngine(string name, string command, string argumentsTemplate, bool isHeadless)`
  - `public string ExternalCliCodingEngine.ArgumentsTemplate { get; }`
  - `public bool ExternalCliCodingEngine.IsHeadless { get; }`

### 배경 (구현자가 알아야 할 것)

**모드별 스트림 처리가 이 태스크의 핵심이다.**

| 스트림 | 대화형 | 배치 |
|---|---|---|
| stdin | 상속 | 리다이렉트 후 즉시 닫음 |
| stdout | 상속 | 상속 (CI 로그에 진행 상황이 보여야 함) |
| stderr | 상속 | 캡처 (분류용) |

stdin을 닫는 근거는 실측이다. CLI가 정상 동작한 조건이 정확히 `< /dev/null`이었다. 상속된 TTY를 그대로 두면 CLI가 대화형으로 오인한다.

**stderr는 반드시 비동기로 읽으면서 `WaitForExit`을 건다.** `WaitForExit`을 먼저 걸고 나중에 읽으면 파이프 버퍼가 차는 순간 교착한다.

작업 디렉터리(`<job>/src`)는 **아무도 생성하지 않는다.** `MetadataExporter.cs:616`이 만드는 것은 `<job>/agent/src`다. 없는 디렉터리를 `WorkingDirectory`에 주면 `Process.Start`가 던지고, 현재 코드는 그것을 "명령어가 설치되어 있는지 확인하십시오"라는 무관한 메시지로 감싼다. 스냅샷도 이 디렉터리를 전제하므로 기동 직전에 보장 생성한다.

`ICodingEngine`에 `Command`를 추가하는 이유는 Task 5다. `CliFailureClassifier.ToException`의 미인증 안내문이 "터미널에서 '{command}'를 직접 실행해 로그인을 완료하십시오"라서 실제 명령어가 필요하다.

**이 태스크에서 루프 동작은 바꾸지 않는다.** 오케스트레이터는 `run.ExitCode == 0`으로 기존과 동일하게 동작시켜 커밋을 초록으로 유지한다. 루프 제어는 Task 5다.

- [ ] **Step 1: 결과 모델을 만든다**

Create `src/ReSet.Core/Models/CodegenRunResult.cs`:

```csharp
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Core.Models
{
    /// <summary>
    /// 코딩 엔진 1회 기동의 결과.
    ///
    /// 성공 여부를 나타내는 편의 속성을 일부러 두지 않는다. 루프 판단은
    /// ProducedArtifacts와 FailureKind의 조합으로 이뤄지고 종료 코드 단독으로는
    /// 아무것도 결정하지 않는다. Succeeded 같은 속성을 두면 이 설계가 고치려는
    /// 착각("0이면 성공")을 그대로 되살린다.
    /// </summary>
    /// <param name="ProducedArtifacts">작업 디렉터리에 실제 변화가 있었는가</param>
    /// <param name="ExitCode">프로세스 종료 코드</param>
    /// <param name="FailureKind">stderr로 분류한 실패 원인. 대화형에서는 항상 Unknown</param>
    /// <param name="Diagnostic">배치에서 캡처한 stderr 원문. 대화형에서는 null</param>
    public sealed record CodegenRunResult(
        bool ProducedArtifacts,
        int ExitCode,
        CliFailureKind FailureKind,
        string? Diagnostic);
}
```

- [ ] **Step 2: 인터페이스를 바꾼다**

Modify `src/ReSet.Core/Services/ICodingEngine.cs` — 전체를 다음으로 교체:

```csharp
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public interface ICodingEngine
    {
        string Name { get; }

        /// <summary>실행 파일명 또는 절대 경로. 실패 안내문이 사용자에게 되짚어 줄 명령어다.</summary>
        string Command { get; }

        /// <summary>
        /// 외부 코딩 에이전트를 프로세스로 기동하여 마이그레이션 코드를 작성하도록 지시합니다.
        /// </summary>
        /// <param name="spDef">SP 정의 메타데이터</param>
        /// <param name="instructionsFilePath">마이그레이션 지시서 번들 경로 (*_MigrationInstructions.md)</param>
        /// <param name="targetProjectDir">코드가 구현될 대상 프로젝트 디렉터리</param>
        /// <param name="cancellationToken">작업 취소 토큰</param>
        /// <returns>기동 결과. 성공/실패 판단은 호출자가 한다.</returns>
        Task<CodegenRunResult> GenerateCodeAsync(
            SpDefinition? spDef,
            string instructionsFilePath,
            string targetProjectDir,
            CancellationToken cancellationToken);
    }
}
```

- [ ] **Step 3: 엔진을 다시 쓴다**

Modify `src/ReSet.Core/Services/ExternalCliCodingEngine.cs` — 전체를 다음으로 교체:

```csharp
using System;
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

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
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

                        var after = ArtifactChangeDetector.Snapshot(workingDir);
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
            // 취소를 InvalidOperationException으로 감싸면 하류의 올바른 핸들러
            // (Program.cs의 catch (OperationCanceledException))가 전부 매칭에 실패한다.
            // 사용자가 Ctrl-C를 눌러도 "엔진 기동 오류"로 보고되고 작업이 계속된다.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "외부 코딩 에이전트 기동 중 예외 발생 - Engine: {EngineName}, Command: {Command}", Name, _command);
                throw new InvalidOperationException(
                    $"외부 코딩 엔진({Name}) 기동 중 오류가 발생했습니다. " +
                    $"'{_command}' 명령이 설치되어 PATH에 등록되어 있는지 확인하거나, " +
                    $"appsettings.json의 CodegenSettings:Engines:{Name}:Command에 절대 경로를 지정하십시오. " +
                    $"(오류: {ex.Message})",
                    ex);
            }
        }
    }
}
```

- [ ] **Step 4: 호출부를 최소로 적응시킨다**

Modify `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs:49` — 다음 한 줄을

```csharp
                bool engineSuccess = await _codingEngine.GenerateCodeAsync(null, instructionsFilePath, codeDir, cancellationToken);
```

이렇게 바꾼다 (동작은 그대로 유지 — 루프 제어는 Task 5):

```csharp
                var run = await _codingEngine.GenerateCodeAsync(null, instructionsFilePath, codeDir, cancellationToken);

                // 이 태스크에서는 기존 판정을 그대로 유지한다. 산출물 유무와 실패 분류를
                // 쓰는 루프 제어는 Task 5에서 이 자리를 대체한다.
                bool engineSuccess = run.ExitCode == 0;
```

Modify `tests/ReSet.Core.Tests/CodingEngineTests.cs:56` — 생성자에 인자를 추가한다:

```csharp
            var engine = new ExternalCliCodingEngine("test-engine", "non-existent-command-12345", "--help", isHeadless: false);
```

- [ ] **Step 5: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 759건 통과, 경고 8건

내역: 착수 746 + Task 1의 6 + Task 2의 7 = 759. 이 태스크 자체는 테스트를 추가하지 않고 기존 3건을 새 시그니처에 맞추기만 한다.

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Models/CodegenRunResult.cs src/ReSet.Core/Services/ICodingEngine.cs src/ReSet.Core/Services/ExternalCliCodingEngine.cs src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs tests/ReSet.Core.Tests/CodingEngineTests.cs
git commit -m "feat: report what a codegen run actually did instead of just its exit code"
```

---

## Task 4: 배치 인자 분리

**Files:**
- Modify: `src/ReSet.Cli/CodingEngineFactory.cs` — 전체
- Modify: `src/ReSet.Cli/Program.cs:1999` — 팩토리 호출
- Modify: `src/ReSet.Cli/appsettings.json` — `CodegenSettings:Engines` 세 엔진
- Test: `tests/ReSet.Core.Tests/CodingEngineTests.cs` — 테스트 추가

**Interfaces:**
- Consumes: `ExternalCliCodingEngine(string, string, string, bool)`, `ArgumentsTemplate`, `IsHeadless` (Task 3)
- Produces: `ICodingEngine CodingEngineFactory.CreateEngine(string engineName, bool isBatchMode)`

### 배경 (구현자가 알아야 할 것)

설계 문서의 실측표대로, 지금 `Arguments`에 적힌 인자는 전부 대화형 TUI 형식이라 무인 실행에서 세 엔진 모두 실패한다. 대화형 인자로 폴백하면 그 실패가 그대로 돌아오므로 **폴백하지 않는다.**

배치용 인자는 설계 시점에 실제 파일 생성까지 확인한 조합이다:

- claude: `--permission-mode acceptEdits -p`가 있어야 파일을 쓴다. `--add-dir`는 **가변 인자라 프롬프트보다 앞**에 와야 한다 (뒤에 두면 프롬프트를 디렉터리로 먹고 `Input must be provided…`로 실패).
- codex: `--full-auto`가 없으면 git 저장소가 아닌 job 디렉터리에서 읽기 전용 샌드박스로 떨어져 쓰기가 막힌다.
- agy: `BatchArguments`를 빈 문자열로 둔다. Global Constraints 참조.

`CreateEngine`이 던지는 예외는 `RunCodegenEngineAsync`의 바깥 `catch (Exception ex)`(`Program.cs:2066` 부근)가 받아 "외부 코딩 에이전트 실행 중 오류 발생: …"으로 콘솔에 출력한다. 별도 처리가 필요 없다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

Modify `tests/ReSet.Core.Tests/CodingEngineTests.cs` — 기존 두 팩토리 테스트를 새 시그니처에 맞추고 세 건을 추가한다. 클래스 본문을 다음으로 교체:

```csharp
        [Fact]
        public void CodingEngineFactory_ShouldCreateEngineFromConfiguration()
        {
            var inMemorySettings = new Dictionary<string, string?> {
                {"CodegenSettings:Engines:test-claude:Command", "claude-cli"},
                {"CodegenSettings:Engines:test-claude:Arguments", "run {instructions}"}
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var factory = new ReSet.Cli.CodingEngineFactory(configuration);

            var engine = factory.CreateEngine("test-claude", isBatchMode: false);

            Assert.NotNull(engine);
            Assert.Equal("test-claude", engine.Name);
        }

        [Fact]
        public void CodingEngineFactory_ShouldThrowException_WhenEngineConfigDoesNotExist()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

            var factory = new ReSet.Cli.CodingEngineFactory(configuration);

            Assert.Throws<InvalidOperationException>(() => factory.CreateEngine("non-existent", isBatchMode: false));
        }

        [Fact]
        public void CodingEngineFactory_ShouldUseInteractiveArguments_WhenNotBatchMode()
        {
            var factory = new ReSet.Cli.CodingEngineFactory(BuildBothArgumentsConfig());

            var engine = Assert.IsType<ExternalCliCodingEngine>(factory.CreateEngine("test-claude", isBatchMode: false));

            Assert.Equal("run {instructions}", engine.ArgumentsTemplate);
            Assert.False(engine.IsHeadless);
        }

        [Fact]
        public void CodingEngineFactory_ShouldUseBatchArguments_WhenBatchMode()
        {
            var factory = new ReSet.Cli.CodingEngineFactory(BuildBothArgumentsConfig());

            var engine = Assert.IsType<ExternalCliCodingEngine>(factory.CreateEngine("test-claude", isBatchMode: true));

            Assert.Equal("-p run {instructions}", engine.ArgumentsTemplate);
            Assert.True(engine.IsHeadless);
        }

        [Fact]
        public void CodingEngineFactory_ShouldThrow_WhenBatchModeAndBatchArgumentsMissing()
        {
            // 대화형 인자로 폴백하면 TTY 오류로 조용히 실패한다. 명시적으로 막는다.
            var inMemorySettings = new Dictionary<string, string?> {
                {"CodegenSettings:Engines:test-agy:Command", "agy"},
                {"CodegenSettings:Engines:test-agy:Arguments", "--prompt-interactive {instructions}"},
                {"CodegenSettings:Engines:test-agy:BatchArguments", ""}
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var factory = new ReSet.Cli.CodingEngineFactory(configuration);

            var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateEngine("test-agy", isBatchMode: true));

            Assert.Contains("BatchArguments", ex.Message);
        }

        private static IConfiguration BuildBothArgumentsConfig()
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    {"CodegenSettings:Engines:test-claude:Command", "claude"},
                    {"CodegenSettings:Engines:test-claude:Arguments", "run {instructions}"},
                    {"CodegenSettings:Engines:test-claude:BatchArguments", "-p run {instructions}"}
                })
                .Build();
        }

        [Fact]
        public async Task ExternalCliCodingEngine_ShouldThrow_WhenCommandDoesNotExist()
        {
            var engine = new ExternalCliCodingEngine("test-engine", "non-existent-command-12345", "--help", isHeadless: false);
            var spDef = new SpDefinition { Schema = "dbo", Name = "TestSp" };

            var tempFile = Path.GetTempFileName();

            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await engine.GenerateCodeAsync(spDef, tempFile, Directory.GetCurrentDirectory(), CancellationToken.None);
                });
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CodingEngineTests"`
Expected: 컴파일 실패 — `CreateEngine`이 인자 2개를 받지 않음 (CS1501)

- [ ] **Step 3: 팩토리를 고친다**

Modify `src/ReSet.Cli/CodingEngineFactory.cs` — `CreateEngine` 메서드를 다음으로 교체:

```csharp
        public ICodingEngine CreateEngine(string engineName, bool isBatchMode)
        {
            if (string.IsNullOrEmpty(engineName))
            {
                throw new ArgumentException("코딩 엔진명이 지정되지 않았습니다.", nameof(engineName));
            }

            var section = _configuration.GetSection($"CodegenSettings:Engines:{engineName}");
            if (!section.Exists())
            {
                throw new InvalidOperationException($"설정 파일에서 코딩 엔진 '{engineName}'의 구성을 찾을 수 없습니다.");
            }

            var command = section["Command"];
            if (string.IsNullOrEmpty(command))
            {
                throw new InvalidOperationException($"코딩 엔진 '{engineName}'의 실행 파일명(Command)이 누락되었습니다.");
            }

            var interactiveArguments = section["Arguments"] ?? string.Empty;
            var batchArguments = section["BatchArguments"] ?? string.Empty;

            // 대화형 인자로 폴백하지 않는다. 대화형 형식은 무인 실행에서 TTY를 열지 못해
            // 종료 코드 0인 채로 조용히 실패한다.
            if (isBatchMode && string.IsNullOrWhiteSpace(batchArguments))
            {
                throw new InvalidOperationException(
                    $"'{engineName}' 엔진은 무인 배치 모드를 지원하지 않습니다(BatchArguments 미지정). " +
                    $"CodegenSettings:Engine을 배치를 지원하는 엔진으로 변경하거나, " +
                    $"CodegenSettings:Engines:{engineName}:BatchArguments를 채우십시오.");
            }

            var arguments = isBatchMode ? batchArguments : interactiveArguments;

            return new ExternalCliCodingEngine(engineName, command, arguments, isHeadless: isBatchMode);
        }
```

Modify `src/ReSet.Cli/Program.cs:1999`:

```csharp
                var engine = factory.CreateEngine(engineName ?? "claude", isBatchMode);
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CodingEngineTests"`
Expected: PASS 6건

- [ ] **Step 5: 설정 파일에 배치 인자를 넣는다**

Modify `src/ReSet.Cli/appsettings.json` — `CodegenSettings:Engines` 블록 전체를 다음으로 교체:

```jsonc
    "Engines": {
      "claude": {
        "Command": "claude",                // 실행할 Claude CLI 명령어
        "Arguments": "--model claude-sonnet-5 \"write code using {instructions}\"", // 대화형 기동 인자
        // 무인 배치 인자. {jobDir}은 지시서가 cwd(<job>/src) 바깥에 있어 필요하다.
        // [주의] --add-dir는 가변 인자라 반드시 프롬프트보다 앞에 두어야 한다.
        "BatchArguments": "--add-dir {jobDir} --model claude-sonnet-5 --permission-mode acceptEdits -p \"write code using {instructions}\""
      },
      "agy": {
        "Command": "agy",                   // Antigravity CLI 명령어. gemini-3.1-pro는 --effort(low|high) 동반 필수
        "Arguments": "--model gemini-3.1-pro --effort high --prompt-interactive \"{instructions} 파일을 읽고 지시사항과 체크리스트에 따라 점진적으로 통합 배치 코드를 작성해줘.\"",
        // [무인 배치 미지원] agy에는 claude의 acceptEdits나 codex의 샌드박스에 해당하는
        // 중간 단계가 없다. --dangerously-skip-permissions는 툴 22종(run_command 포함)을
        // 무조건 승인해 무인 배치에서 임의 명령 실행을 허용하므로 기본값으로 두지 않는다.
        "BatchArguments": ""
      },
      "codex": {
        "Command": "codex",                 // Codex CLI 명령어
        "Arguments": "-m gpt-5.6-terra \"{instructions}\"", // 대화형 기동 인자
        // --full-auto가 없으면 git 저장소가 아닌 job 디렉터리에서 읽기 전용 샌드박스로
        // 떨어져 파일을 쓰지 못한다.
        "BatchArguments": "exec -m gpt-5.6-terra --skip-git-repo-check --full-auto \"{instructions}\""
      }
    }
```

- [ ] **Step 6: 설정이 실제로 로드되는지 확인한다**

Run: `dotnet build`
Expected: 빌드 성공, 경고 8건

`appsettings.json`은 주석이 있는 JSONC지만 `JsonConfigurationProvider`가 주석을 건너뛰므로 문제없다. 문법 오류가 있으면 런타임에 터지므로 다음 단계에서 확인한다.

Run: `dotnet run --project src/ReSet.Cli -- --help 2>&1 | head -5`
Expected: 설정 파싱 예외 없이 실행

- [ ] **Step 7: 커밋한다**

```bash
git add src/ReSet.Cli/CodingEngineFactory.cs src/ReSet.Cli/Program.cs src/ReSet.Cli/appsettings.json tests/ReSet.Core.Tests/CodingEngineTests.cs
git commit -m "feat: give each codegen engine a separate headless argument form"
```

---

## Task 5: 루프 제어와 중단 사유 노출

**Files:**
- Create: `src/ReSet.Validator.Core/Services/CodegenLoopPolicy.cs`
- Create: `src/ReSet.Validator.Core/Models/CodegenWorkflowResult.cs`
- Create: `tests/ReSet.Core.Tests/CodegenLoopPolicyTests.cs`
- Modify: `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs` — 반환형과 루프
- Modify: `src/ReSet.Cli/Program.cs:2045-2060` — 결과 출력

**Interfaces:**
- Consumes: `CodegenRunResult`, `ICodingEngine.Command` (Task 3)
- Produces:
  - `public enum CodegenLoopDecision { Validate, RetryWithoutValidation, Abort }`
  - `public static CodegenLoopDecision CodegenLoopPolicy.Decide(CodegenRunResult run)`
  - `public sealed record CodegenWorkflowResult(bool Succeeded, string? AbortReason)`
  - `Task<CodegenWorkflowResult> CodegenWorkflowOrchestrator.RunSelfHealingWorkflowAsync(string, string, string, string, bool, CancellationToken)`

### 배경 (구현자가 알아야 할 것)

지금 오케스트레이터는 엔진이 실패해도 경고만 남기고 검증으로 진행한다. 기동 자체가 불가능한 상황(세션 만료, 쿼터 소진)에서도 빈 `src/`를 상대로 L2 AI 검증을 `MaxL2Attempts`만큼 반복하며 토큰을 태운다.

판단 규칙:

| 상태 | 결정 | 이유 |
|---|---|---|
| 산출물 있음 | `Validate` | 부분 산출물도 L1/L2가 볼 가치가 있다. 종료 코드는 보지 않는다 |
| 산출물 없음 + Quota/Auth/ToolPerm | `Abort` | 재시도해도 결과가 같다 |
| 산출물 없음 + 그 외 | `RetryWithoutValidation` | 일시적일 수 있다. 검증만 건너뛴다 |

**검증을 건너뛴 시도에서는 피드백을 지시서에 추가하지 않는다.** 붙일 검증 결과가 없고, 지시서를 손대지 않은 채 재시도하는 것이 맞다.

`CodeVerificationOrchestrator`는 인터페이스가 없는 구상 클래스라 루프 전체를 목으로 감쌀 수 없다. `Decide`를 순수 함수로 분리했으므로 그 리팩터링 없이 판단 로직을 전부 검증한다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

Create `tests/ReSet.Core.Tests/CodegenLoopPolicyTests.cs`:

```csharp
using ReSet.Core.Models;
using ReSet.Core.Services.Clients.Cli;
using ReSet.Validator.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CodegenLoopPolicyTests
    {
        [Theory]
        [InlineData(CliFailureKind.QuotaExhausted)]
        [InlineData(CliFailureKind.NotAuthenticated)]
        [InlineData(CliFailureKind.ToolPermissionDenied)]
        [InlineData(CliFailureKind.Unknown)]
        public void Decide_ShouldValidate_WheneverArtifactsExist(CliFailureKind kind)
        {
            // 산출물이 있으면 종료 코드와 분류를 보지 않는다. 부분 산출물도 검증 대상이다.
            var run = new CodegenRunResult(ProducedArtifacts: true, ExitCode: 1, FailureKind: kind, Diagnostic: null);

            Assert.Equal(CodegenLoopDecision.Validate, CodegenLoopPolicy.Decide(run));
        }

        [Theory]
        [InlineData(CliFailureKind.QuotaExhausted)]
        [InlineData(CliFailureKind.NotAuthenticated)]
        [InlineData(CliFailureKind.ToolPermissionDenied)]
        public void Decide_ShouldAbort_WhenNoArtifactsAndFailureIsPermanent(CliFailureKind kind)
        {
            var run = new CodegenRunResult(ProducedArtifacts: false, ExitCode: 0, FailureKind: kind, Diagnostic: "…");

            Assert.Equal(CodegenLoopDecision.Abort, CodegenLoopPolicy.Decide(run));
        }

        [Theory]
        [InlineData(CliFailureKind.Unknown)]
        [InlineData(CliFailureKind.Timeout)]
        public void Decide_ShouldRetryWithoutValidation_WhenNoArtifactsAndFailureMayBeTransient(CliFailureKind kind)
        {
            var run = new CodegenRunResult(ProducedArtifacts: false, ExitCode: 0, FailureKind: kind, Diagnostic: null);

            Assert.Equal(CodegenLoopDecision.RetryWithoutValidation, CodegenLoopPolicy.Decide(run));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CodegenLoopPolicyTests"`
Expected: 컴파일 실패 — `CodegenLoopPolicy` 형식을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 판단 함수를 만든다**

Create `src/ReSet.Validator.Core/Services/CodegenLoopPolicy.cs`:

```csharp
using ReSet.Core.Models;
using ReSet.Core.Services.Clients.Cli;

namespace ReSet.Validator.Core.Services
{
    public enum CodegenLoopDecision
    {
        /// <summary>산출물이 있다. Critic 검증으로 넘긴다.</summary>
        Validate,

        /// <summary>산출물이 없지만 일시적 실패일 수 있다. 검증을 건너뛰고 다시 기동한다.</summary>
        RetryWithoutValidation,

        /// <summary>재시도해도 결과가 같은 실패다. 루프를 끝낸다.</summary>
        Abort
    }

    /// <summary>
    /// 코딩 에이전트 1회 기동 결과로 자가 수정 루프를 계속할지 판단한다.
    ///
    /// 프로세스도 검증기도 끼지 않는 순수 함수라 조합을 전부 테스트할 수 있다.
    /// CodeVerificationOrchestrator가 구상 클래스라 루프 전체를 목으로 감쌀 수 없기에
    /// 판단만 따로 떼어냈다.
    /// </summary>
    public static class CodegenLoopPolicy
    {
        public static CodegenLoopDecision Decide(CodegenRunResult run)
        {
            // 종료 코드는 보지 않는다. 부분 산출물도 L1/L2가 볼 가치가 있다.
            if (run.ProducedArtifacts)
            {
                return CodegenLoopDecision.Validate;
            }

            return run.FailureKind switch
            {
                CliFailureKind.QuotaExhausted => CodegenLoopDecision.Abort,
                CliFailureKind.NotAuthenticated => CodegenLoopDecision.Abort,
                CliFailureKind.ToolPermissionDenied => CodegenLoopDecision.Abort,
                _ => CodegenLoopDecision.RetryWithoutValidation
            };
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CodegenLoopPolicyTests"`
Expected: PASS 9건

- [ ] **Step 5: 워크플로우 결과 모델을 만든다**

Create `src/ReSet.Validator.Core/Models/CodegenWorkflowResult.cs`:

```csharp
namespace ReSet.Validator.Core.Models
{
    /// <summary>
    /// 자가 수정 워크플로우의 최종 결과.
    ///
    /// bool 하나만 돌려주면 호출부가 "검증 실패"와 "에이전트가 아예 못 돌았음"을
    /// 구분하지 못한다. 무인 배치에서 가장 알아야 할 정보가 로그 파일에만 남는다.
    /// </summary>
    /// <param name="Succeeded">모든 검증을 통과했는가</param>
    /// <param name="AbortReason">재시도 불가 실패로 루프를 끊었을 때의 안내문. 그 외에는 null</param>
    public sealed record CodegenWorkflowResult(bool Succeeded, string? AbortReason);
}
```

- [ ] **Step 6: 오케스트레이터 루프를 바꾼다**

Modify `src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs`:

using 추가:

```csharp
using ReSet.Core.Models;
using ReSet.Core.Services.Clients.Cli;
```

메서드 시그니처를 바꾼다:

```csharp
        public async Task<CodegenWorkflowResult> RunSelfHealingWorkflowAsync(
```

Task 3에서 넣은 임시 코드(49-54행 부근)를 다음으로 교체:

```csharp
                // 1. External Coding Engine 기동 (Actor)
                var run = await _codingEngine.GenerateCodeAsync(null, instructionsFilePath, codeDir, cancellationToken);

                var decision = CodegenLoopPolicy.Decide(run);

                if (decision == CodegenLoopDecision.Abort)
                {
                    var abortReason = BuildAbortReason(run);
                    Log.Error("[SelfHealing] 재시도해도 결과가 같은 실패입니다. 루프를 중단합니다. {AbortReason}", abortReason);
                    return new CodegenWorkflowResult(false, abortReason);
                }

                if (decision == CodegenLoopDecision.RetryWithoutValidation)
                {
                    // 검증할 산출물이 없다. 지시서도 손대지 않고 그대로 재시도한다.
                    Log.Warning(
                        "[SelfHealing] 코딩 에이전트가 산출물을 남기지 않았습니다. 검증을 건너뛰고 다음 시도를 준비합니다. (종료 코드: {ExitCode}, 분류: {FailureKind})",
                        run.ExitCode, run.FailureKind);
                    attempt++;
                    continue;
                }
```

반환은 메서드 끝 한 곳(`return isSuccess;`)뿐이다. `allPassed` 분기(97-102행 부근)의 `isSuccess = true; break;`는 그대로 두고, 마지막 반환만 바꾼다:

```csharp
            return new CodegenWorkflowResult(isSuccess, null);
```

private 헬퍼를 클래스 끝에 추가한다:

```csharp
        /// <summary>
        /// 중단 안내문은 CliFailureClassifier가 이미 분류별로 갖고 있다.
        /// 같은 말을 두 곳에서 다르게 쓰지 않기 위해 그것을 그대로 가져온다.
        /// </summary>
        private string BuildAbortReason(CodegenRunResult run)
        {
            var probe = new CliProcessResult
            {
                ExitCode = run.ExitCode,
                StandardError = run.Diagnostic ?? string.Empty
            };

            return CliFailureClassifier
                .ToException(_codingEngine.Name, _codingEngine.Command, probe, extraDetail: null)
                .Message;
        }
```

- [ ] **Step 7: 호출부를 고친다**

Modify `src/ReSet.Cli/Program.cs:2045-2060` — 다음 블록을

```csharp
                bool isSuccess = await codegenWorkflowOrchestrator.RunSelfHealingWorkflowAsync(
```

부터 `else` 블록 끝까지 다음으로 교체:

```csharp
                var workflowResult = await codegenWorkflowOrchestrator.RunSelfHealingWorkflowAsync(
                    jobOrSpName: spName,
                    instructionsFilePath: instructionsPath,
                    specDir: specDir,
                    codeDir: targetProjectDir,
                    isBatchMode: isBatchMode,
                    cancellationToken: cancellationToken);

                if (workflowResult.Succeeded)
                {
                    AnsiConsole.MarkupLine("\n[bold green]✔ 코딩 에이전트 자가 수정 루프 통과 (MATCH)[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("\n[bold red]❌ 코딩 에이전트 검증 완전 통과 실패. (최종 결과 확인 요망)[/]");

                    // 무인 배치에서는 화면이 유일한 창구다. 중단 사유를 로그에만 두지 않는다.
                    if (!string.IsNullOrWhiteSpace(workflowResult.AbortReason))
                    {
                        AnsiConsole.MarkupLine($"[red]중단 사유:[/] {Markup.Escape(workflowResult.AbortReason)}");
                    }
                }
```

- [ ] **Step 8: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: **771건 통과**, 경고 8건

내역: 착수 746 + Task 1의 6 + Task 2의 7 + Task 4의 3(기존 3건이 6건으로) + Task 5의 9 = 771.

판정 기준은 **실패 0건**과 **경고 8건 유지**다.

- [ ] **Step 9: 취소 정책 위반이 없는지 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CancellationPolicyTests"`
Expected: PASS. 실패하면 새로 추가한 넓은 `catch`에 `when (ex is not OperationCanceledException)` 필터를 단다.

- [ ] **Step 10: 커밋한다**

```bash
git add src/ReSet.Validator.Core/Services/CodegenLoopPolicy.cs src/ReSet.Validator.Core/Models/CodegenWorkflowResult.cs src/ReSet.Validator.Core/Services/CodegenWorkflowOrchestrator.cs src/ReSet.Cli/Program.cs tests/ReSet.Core.Tests/CodegenLoopPolicyTests.cs
git commit -m "fix: stop burning L2 verification on runs that produced nothing"
```

---

## Task 6: 실측 재확인과 문서 동기화

**Files:**
- Modify: `README.md:238` 부근 — `CodegenSettings` 예시
- Modify: `AGENTS.md:63` — agy 제약 정정
- Modify: `AGENTS.md` 범주 6 — 대화형/배치 인자 분리 규칙

**Interfaces:**
- Consumes: Task 4의 `appsettings.json` 값
- Produces: 없음

### 배경 (구현자가 알아야 할 것)

`AGENTS.md:63`은 "`CodegenSettings:Engines:agy`는 툴이 켜져 있는 것이 정상이므로 이 제약과 무관합니다"라고 적고 있다. **사실과 다르다.** agy는 브릿지 경로에서도 헤드리스 툴 자동 거부에 걸린다 (실측: `no output produced — … auto-denied`, 종료 코드 0).

- [ ] **Step 1: 조립된 인자로 claude 배치 경로를 실측한다**

**치환 결과의 따옴표 중첩에 놀라지 말 것.** 템플릿 `"write code using {instructions}"`에 `{instructions}` → `"경로"`가 들어가면 `"write code using "/abs/path""`가 된다. .NET의 인자 파서는 따옴표를 토글로 처리하므로 공백 없이 이어 붙어 결국 인자 하나가 된다. 기존 동작 그대로이며 회귀가 아니다. 아래 수동 검증은 플래그 조합을 확인하는 것이 목적이므로 따옴표를 단순화해 실행한다.

임시 Job 구조를 만들어 실제 설정값이 동작하는지 확인한다:

```bash
D="$(mktemp -d)/Jobs/TestJob"
mkdir -p "$D/agent" "$D/src"
printf '# 테스트\n\n아무 파일도 만들지 말고 DONE만 출력하라.\n' > "$D/agent/MigrationInstructions.md"
cd "$D/src"
claude --add-dir "$D" --model claude-sonnet-5 --permission-mode acceptEdits -p "write code using $D/agent/MigrationInstructions.md" < /dev/null
echo "EXIT=$?"
```

Expected: 종료 코드 0, 지시서 내용을 읽은 응답. 권한 거부 메시지가 나오면 실패다.

- [ ] **Step 2: 조립된 인자로 codex 배치 경로를 실측한다**

```bash
cd "$D/src"
codex exec -m gpt-5.6-terra --skip-git-repo-check --full-auto "$D/agent/MigrationInstructions.md 파일을 읽고 지시대로 하라" < /dev/null
echo "EXIT=$?"
```

Expected: 종료 코드 0, 지시서를 읽은 응답. `읽기 전용` 메시지가 나오면 실패다.

- [ ] **Step 3: agy가 배치에서 명확히 거부되는지 확인한다**

`appsettings.json`의 `CodegenSettings:Engine`을 임시로 `"agy"`로 바꾸고 배치 모드로 실행한다. 팩토리 예외 메시지가 콘솔에 출력되어야 한다.

Expected: `'agy' 엔진은 무인 배치 모드를 지원하지 않습니다(BatchArguments 미지정). …`

확인 후 `CodegenSettings:Engine`을 `"claude"`로 되돌린다.

- [ ] **Step 4: README를 갱신한다**

Modify `README.md` — `CodegenSettings` 예시 블록을 Task 4에서 확정한 `appsettings.json` 내용과 일치시킨다. 세 엔진의 `BatchArguments`와 주석을 그대로 반영한다.

- [ ] **Step 5: AGENTS.md를 정정한다**

Modify `AGENTS.md:63` — 마지막 문장을 다음으로 교체:

```
`CodegenSettings:Engines:agy`(코딩 에이전트 브릿지)도 헤드리스에서는 같은 자동 거부에 걸리므로 무인 배치 대상이 아닙니다. 대화형 기동만 지원하며 `BatchArguments`를 비워 둡니다.
```

Modify `AGENTS.md` 범주 6 — "프로세스 양방향 제어" 항목 뒤에 다음 항목을 추가:

```
*   **대화형/배치 인자 분리**: 코딩 엔진 인자는 `Arguments`(대화형)와 `BatchArguments`(무인)로 나뉩니다. 대화형 TUI 형식은 무인 실행에서 TTY를 열지 못해 종료 코드 0인 채 조용히 실패하므로 **폴백하지 마십시오**. `BatchArguments`가 비면 그 엔진은 무인 배치 미지원이며 `CodingEngineFactory`가 명시적으로 거부합니다. 지시서가 작업 디렉터리(`<job>/src`) 바깥에 있으므로 `{jobDir}` 자리표시자로 접근 범위를 열어 주어야 합니다.
```

- [ ] **Step 6: 최종 확인**

Run: `dotnet test`
Expected: 실패 0건, 경고 8건

- [ ] **Step 7: 커밋한다**

```bash
git add README.md AGENTS.md
git commit -m "docs: record the interactive/batch argument split and correct the agy claim"
```

---

## 완료 기준

- `dotnet test` 실패 0건, 빌드 경고 8건 유지
- 무인 배치에서 claude·codex가 실제로 파일을 생성한다 (Task 6 Step 1-2 실측)
- 배치에서 agy 선택 시 실행 전에 명확한 안내와 함께 거부된다 (Task 6 Step 3)
- 산출물 없이 종료 코드 0으로 끝난 기동이 성공으로 집계되지 않는다 (`CodegenLoopPolicyTests`)
- 재시도 불가 실패의 사유가 콘솔에 출력된다
