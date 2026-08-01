# 통합 배치 마이그레이션 설계 경로 보강 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 통합 배치 설계(TUI 메뉴 2번) 경로의 프롬프트 지침 결함 5건과 코드 플로우 결함 4건을 해소한다.

**Architecture:** 서로 파일이 겹치지 않는 두 트랙으로 나눈다. 지침 트랙은 `AiService.cs`의 생성·Critic 시스템 프롬프트만 수정한다. 플로우 트랙은 `MetadataExporter`가 재귀 경로에서도 `metadata.json`을 내보내게 하고, 배치 스텝 후보 선별과 메타데이터 복원 로직을 `BatchStepCatalog`로 추출한 뒤 `Program.cs`를 배선하고, 오케스트레이터의 출력 경로와 L2 실패 표시를 고친다.

**Tech Stack:** .NET 10, C#, xUnit, NSubstitute, Spectre.Console

**설계 문서:** `docs/superpowers/specs/2026-08-01-batch-design-hardening-design.md`

## Global Constraints

- 모든 작업은 TDD로 한다. 테스트를 먼저 쓰고 **실패를 눈으로 확인한 뒤** 구현한다.
- `dotnet build` 경고 0 · 오류 0을 유지한다.
- 커밋은 **태스크 단위**로 한다. 스펙의 "트랙별 독립 커밋 2개"를 태스크 단위로 세분화한 것이며, 트랙 간 파일이 겹치지 않는다는 원칙은 그대로 유지된다.
- 생성 프롬프트(`AiService.cs:1871`)는 `$@"..."` **보간 축자 문자열**이다. 큰따옴표는 `""`로 이중화하고 중괄호는 `{{}}`로 이스케이프한다.
- Critic 프롬프트(`AiService.cs:1994`)는 `@"..."` **비보간 축자 문자열**이다. 큰따옴표만 `""`로 이중화하면 되고 중괄호는 그대로 둔다.
- 기존 프롬프트 규칙 라벨(`[NOLOCK Prohibition]`, `[INSERT-only Rollback]`, `[Chunk Key Validation]`, `[Output Parameters Interface]`)은 **바꾸지 않는다.** 기존 회귀 테스트가 이 라벨들을 잠그고 있다.
- Critic 점수 축 5개(`ScoreAccuracy`/`ScoreCrud`/`ScoreInterface`/`ScoreReadability`/`ScoreException`)와 `ReviewResult` 모델은 건드리지 않는다.
- `OperationCanceledException`은 어떤 소프트 페일 catch에서도 삼키지 않는다.
- 소스 파일 인코딩은 UTF-8, 한국어 주석 스타일을 따른다.

## File Structure

| 파일 | 책임 | 트랙 |
|---|---|---|
| `src/ReSet.Core/Services/AiService.cs` | 생성·Critic 시스템 프롬프트 (수정) | 지침 |
| `tests/ReSet.Core.Tests/AiServiceTests.cs` | 프롬프트 규칙 회귀 가드 (수정) | 지침 |
| `src/ReSet.Core/Services/MetadataExporter.cs` | 재귀 경로 산출물 내보내기 (수정) | 플로우 |
| `tests/ReSet.Core.Tests/MetadataExporterTests.cs` | 내보내기 검증 (수정) | 플로우 |
| `src/ReSet.Cli/BatchStepCatalog.cs` | 배치 스텝 후보 선별 + 메타데이터 복원 (신규) | 플로우 |
| `tests/ReSet.Core.Tests/BatchStepCatalogTests.cs` | 위 검증 (신규) | 플로우 |
| `src/ReSet.Cli/Program.cs` | 메뉴 2번 배선 (수정) | 플로우 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 출력 경로 주입 + L2 실패 표시 (수정) | 플로우 |
| `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` | 위 검증 (수정) | 플로우 |
| `AGENTS.md` | 테스트 개수 체크리스트 (수정) | 마무리 |

---

## Task 1: 생성 프롬프트 3건 교정 (⑥⑦⑧)

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs:1884` (rule 4 c), `:1887` (rule 6-1), `:1889` 뒤 (rule 8-1 신설)
- Test: `tests/ReSet.Core.Tests/AiServiceTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: 없음 (프롬프트 문자열만 변경). 기존 라벨 4개는 유지되므로 `GenerateConsolidatedBatchPlanAsync_Prompt_ContainsDomainConstraints`가 계속 통과해야 한다.

- [ ] **Step 1: 실패하는 테스트 3개를 작성한다**

`tests/ReSet.Core.Tests/AiServiceTests.cs`의 `GenerateConsolidatedBatchPlanAsync_Prompt_ContainsDomainConstraints` 테스트 바로 뒤에 추가한다.

```csharp
        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_Prompt_ShadowRestoreDeletesBeforeInsert()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 통합 배치 명세\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateConsolidatedBatchPlanAsync("Dummy Structure", specs, "C#", "Test_Job");

            // 선행 DELETE 없는 옛 복원 예시가 되살아나면 실패해야 한다.
            Assert.DoesNotContain("(e.g., `INSERT INTO Target SELECT * FROM Shadow`)", result.SystemPrompt);
            Assert.Contains("DELETEs the affected range FIRST", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_Prompt_ForbidsGotoErrorBranching()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 통합 배치 명세\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateConsolidatedBatchPlanAsync("Dummy Structure", specs, "C#", "Test_Job");

            Assert.Contains("GOTO", result.SystemPrompt);
        }

        [Fact]
        public async Task GenerateConsolidatedBatchPlanAsync_Prompt_ContainsChunkTransactionBoundaryRule()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 통합 배치 명세\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            var result = await service.GenerateConsolidatedBatchPlanAsync("Dummy Structure", specs, "C#", "Test_Job");

            Assert.Contains("[Chunk Transaction Boundary]", result.SystemPrompt);
        }
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~GenerateConsolidatedBatchPlanAsync_Prompt_Shadow|FullyQualifiedName~ForbidsGotoErrorBranching|FullyQualifiedName~ContainsChunkTransactionBoundaryRule"
```

기대 결과: 3건 모두 FAIL.
- `ShadowRestoreDeletesBeforeInsert` — `DoesNotContain` 단언에서 실패 (옛 예시가 현존)
- `ForbidsGotoErrorBranching` — `GOTO` 문자열 없음
- `ContainsChunkTransactionBoundaryRule` — 라벨 없음

- [ ] **Step 3: rule 4 (c)를 교정한다 (⑥)**

`src/ReSet.Core/Services/AiService.cs:1884`에서 아래 문자열을 찾아 바꾼다.

찾을 문자열:
```
and (c) you MUST explicitly provide the Rollback/Restore pseudo-code (e.g., `INSERT INTO Target SELECT * FROM Shadow`) to revert committed chunks if a failure occurs mid-way.
```

바꿀 문자열:
```
and (c) you MUST explicitly provide Rollback/Restore pseudo-code that DELETEs the affected range FIRST and only then re-inserts from the Shadow table (e.g., `DELETE FROM Target WHERE BatchDate = @BatchDate;` followed by `INSERT INTO Target SELECT * FROM Shadow WHERE BatchDate = @BatchDate;`). Restoring without the preceding DELETE duplicates rows.
```

- [ ] **Step 4: rule 6-1에 GOTO 금지 절을 편입한다 (⑦)**

같은 파일 `:1887`에서 rule 6-1의 마지막 문장을 찾아 바꾼다.

찾을 문자열:
```
and return that variable in the CATCH block to preserve the exact point of failure.
```

바꿀 문자열:
```
and return that variable in the CATCH block to preserve the exact point of failure. Use structured `TRY...CATCH` exclusively for error handling; NEVER use legacy `GOTO`-based error branching.
```

- [ ] **Step 5: rule 8-1을 신설한다 (⑧)**

같은 파일에서 rule 8(`:1889`) 전체 줄을 찾아 그 뒤에 새 줄을 잇는다.

찾을 문자열 (rule 8의 끝):
```
and combine them with the chunking range using `AND`. Do not delete the original filters.
```

바꿀 문자열:
```
and combine them with the chunking range using `AND`. Do not delete the original filters.
8-1. [Chunk Transaction Boundary] Every iteration of a chunking `WHILE` loop MUST open and close its own explicit `BEGIN TRAN` / `COMMIT TRAN` boundary so that each chunk commits independently and a mid-run failure leaves earlier chunks durably committed. Do NOT wrap the entire loop in a single outer transaction.
```

- [ ] **Step 6: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~AiServiceTests"
```

기대 결과: 신규 3건 PASS. 기존 `GenerateConsolidatedBatchPlanAsync_Prompt_ContainsDomainConstraints`도 PASS (라벨 4개를 바꾸지 않았으므로).

- [ ] **Step 7: 빌드 경고를 확인하고 커밋한다**

```bash
dotnet build 2>&1 | tail -3
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/AiServiceTests.cs
git commit -m "fix(prompt): correct shadow restore example and add chunk transaction rules

The rule 4(c) restore example omitted the preceding DELETE, teaching a
duplicate-inserting pattern that contradicted both AGENTS.md and the few-shot
block further down the same prompt. Add the DELETE, forbid legacy GOTO error
branching alongside the existing TRY...CATCH mandate, and state the per-chunk
transaction boundary as a rule instead of leaving it to an example.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Critic 프롬프트 4건 보강 (⑤⑥짝⑨)

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs:1997-1998` (기준 1), `:2009` (기준 4)
- Test: `tests/ReSet.Core.Tests/AiServiceTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: 없음. `ReviewConsolidatedPlanAsync`의 시그니처와 `ReviewResult` 모델은 그대로다.

`ReviewConsolidatedPlanAsync`는 `ReviewResult`를 반환해 `SystemPrompt` 필드가 없다. 대신 `MockHttpMessageHandler.LastRequestBody`(`AiServiceTests.cs:661`)가 요청 본문을 캡처하므로 이것으로 검증한다. 본문은 JSON 이스케이프되어 있으므로 **따옴표·개행이 든 문자열은 단언에 쓰지 않는다.**

- [ ] **Step 1: 실패하는 테스트 2개를 작성한다**

`tests/ReSet.Core.Tests/AiServiceTests.cs`의 Task 1에서 추가한 테스트들 뒤에 추가한다.

```csharp
        [Fact]
        public async Task ReviewConsolidatedPlanAsync_Prompt_ChecksNolockAndInsertOnlyRollback()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": false}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "## 통합 배치 아키텍처 개요", "Test_Job");

            Assert.Contains("NOLOCK", mockHandler.LastRequestBody);
            Assert.Contains("INSERT-only", mockHandler.LastRequestBody);
        }

        [Fact]
        public async Task ReviewConsolidatedPlanAsync_Prompt_ChecksUnionAndJoinPreservation()
        {
            var specs = new System.Collections.Generic.List<(string FileName, string Content)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": false}\"}}]}";
            var mockHandler = new MockHttpMessageHandler(mockResponse);
            var httpClient = new HttpClient(mockHandler);
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            IAiService service = new AiService(client, 0.2f);

            await service.ReviewConsolidatedPlanAsync(specs, "## 통합 배치 아키텍처 개요", "Test_Job");

            Assert.Contains("UNION ALL", mockHandler.LastRequestBody);
            Assert.Contains("multi-table JOINs", mockHandler.LastRequestBody);
        }
```

테스트 데이터(`specs`, plan markdown)에 `NOLOCK`·`UNION`이 들어 있지 않아야 한다. 사용자 프롬프트에도 본문이 실리므로, 테스트 데이터에 그 단어가 있으면 시스템 프롬프트가 비어 있어도 통과해 버린다. 위 데이터는 그 조건을 만족한다.

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~ReviewConsolidatedPlanAsync_Prompt_Checks"
```

기대 결과: 2건 모두 FAIL. 현재 Critic 프롬프트에 `NOLOCK`·`UNION`이 0건이다.

- [ ] **Step 3: 기준 4에 3개 항목을 추가한다 (⑤, ⑥짝)**

`src/ReSet.Core/Services/AiService.cs:2009`의 아래 줄을 찾아 바꾼다.

찾을 문자열:
```
   - Check if Shadow Table strategies cover all target tables, define capacity/purge policies, and include explicit Rollback/Restore pseudo-code.
```

바꿀 문자열:
```
   - Check if Shadow Table strategies cover all target tables, define capacity/purge policies, and include explicit Rollback/Restore pseudo-code.
   - Check that no `WITH (NOLOCK)` or `NOLOCK` hints remain anywhere in the generated pseudocode. They force READ UNCOMMITTED and violate the SNAPSHOT isolation policy. Penalize heavily if any remain.
   - For INSERT-only steps, verify the rollback relies on `ROLLBACK TRAN` or an explicit `DELETE WHERE [ChunkKey]` compensation rather than a Shadow table.
   - Verify that Shadow restore logic DELETEs the affected target range before re-inserting from the Shadow table. Restoring without the preceding DELETE duplicates rows.
```

- [ ] **Step 4: 기준 1에 Anti-Shortcut 항목을 추가한다 (⑨)**

같은 파일 `:1998`의 아래 줄을 찾아 바꾼다.

찾을 문자열:
```
   - Assess if the business logic and rules of individual specifications are accurately preserved in the consolidated batch job.
```

바꿀 문자열:
```
   - Assess if the business logic and rules of individual specifications are accurately preserved in the consolidated batch job.
   - Verify that queries using `UNION`, `UNION ALL`, or multi-table JOINs are preserved in full. Penalize if source tables or aggregation formulas were simplified, merged, or omitted.
```

- [ ] **Step 5: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~AiServiceTests"
```

기대 결과: 신규 2건 PASS, 기존 전부 PASS.

- [ ] **Step 6: 빌드 경고를 확인하고 커밋한다**

```bash
dotnet build 2>&1 | tail -3
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/AiServiceTests.cs
git commit -m "fix(prompt): make the critic check all five batch constraints

The critic scored isolation, error tracking, restartability and shadow tables
but never looked for leftover NOLOCK hints or shadow-based rollback on
INSERT-only steps, so two of the five constraints the generator prompt
enforces passed L2 unchecked. Add those two, plus the DELETE-before-restore
check and an explicit anti-shortcut criterion for UNION and multi-table JOINs.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: 재귀 경로의 metadata.json 내보내기 (③ 근본)

**Files:**
- Modify: `src/ReSet.Core/Services/MetadataExporter.cs:74-79` (매니페스트 기록 직후)
- Test: `tests/ReSet.Core.Tests/MetadataExporterTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: 재귀 분석된 모든 코드 객체가 `<OutputRoot>/<타입디렉터리>/<schema>.<name>/raw/metadata.json`을 갖는다. Task 5의 `LoadDefinitionsAsync`가 이 파일을 읽는다.

**중요 — 어느 raw 디렉터리인가.** `ExportCodeObjectArtifactsAsync`는 두 개의 raw 디렉터리를 다룬다.

| 경로 | 내용 |
|---|---|
| `Objects/<schema>.<name>.<Type>/raw/` | `object_definition.sql`, `prompt-context.md` |
| `Procedures/<schema>.<name>/raw/` (= 매니페스트 위치) | `dependency-manifest.json` |

`metadata.json`은 **매니페스트와 같은 디렉터리**에 써야 한다. `Program.cs:1193`이 `Procedures/X/docs/Spec.md` → `Procedures/X/raw/metadata.json`으로 매핑하기 때문이다. `Objects/` 아래에 쓰면 소비자가 찾지 못한다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/MetadataExporterTests.cs`의 `ExportCodeObjectArtifactsAsync_WritesDefinitionPromptContextEvenWhenArgumentIsOmitted` 뒤에 추가한다.

```csharp
        [Fact]
        public async Task ExportCodeObjectArtifactsAsync_WritesMetadataJsonNextToManifest()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-MetadataExporter-{Guid.NewGuid():N}");
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Meta", CodeObjectType.Procedure);
            var definition = new SpDefinition
            {
                Schema = key.Schema,
                Name = key.Name,
                DdlText = "SELECT 1;",
                Dependencies = new System.Collections.Generic.List<DependencyInfo>
                {
                    new() { SourceObjectKey = key, Schema = "dbo", Name = "TOrder", Type = "TABLE" }
                }
            };

            try
            {
                await new MetadataExporter().ExportCodeObjectArtifactsAsync(
                    definition,
                    key,
                    new CodeObjectPipelineResult
                    {
                        Nodes = new System.Collections.Generic.List<AnalysisNode>
                        {
                            new(key) { Status = AnalysisNodeStatus.Succeeded }
                        }
                    },
                    DependencyArtifactMode.Reference,
                    outputRoot);

                var metadataPath = Path.Combine(
                    outputRoot, "Procedures", "dbo.USP_Meta", "raw", "metadata.json");
                Assert.True(File.Exists(metadataPath), $"metadata.json이 없습니다: {metadataPath}");

                // 지시서 번들이 실제로 쓰는 payload는 Dependencies다. 왕복이 되어야 한다.
                var restored = System.Text.Json.JsonSerializer.Deserialize<SpDefinition>(
                    await File.ReadAllTextAsync(metadataPath),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Assert.NotNull(restored);
                Assert.Equal("TOrder", Assert.Single(restored!.Dependencies).Name);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~WritesMetadataJsonNextToManifest"
```

기대 결과: FAIL — `metadata.json이 없습니다: ...` 메시지와 함께 `Assert.True` 실패.

- [ ] **Step 3: 최소 구현을 작성한다**

`src/ReSet.Core/Services/MetadataExporter.cs`에서 매니페스트를 쓰는 마지막 줄을 찾아 그 뒤에 추가한다.

찾을 문자열:
```csharp
                await File.WriteAllTextAsync(manifestPath, json, Encoding.UTF8, cancellationToken);
```

바꿀 문자열:
```csharp
                await File.WriteAllTextAsync(manifestPath, json, Encoding.UTF8, cancellationToken);

                // 지시서 번들이 참조 테이블 스키마를 만들 때 쓰는 원천이다.
                // 매니페스트와 같은 디렉터리에 두어야 Spec.md 경로에서 규칙적으로 찾을 수 있다.
                var metadataPath = Path.Combine(
                    Path.GetDirectoryName(manifestPath)!,
                    "metadata.json");
                await File.WriteAllTextAsync(
                    metadataPath,
                    JsonSerializer.Serialize(definition, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8,
                    cancellationToken);
```

이 코드는 기존 `try` 블록 안에 위치하므로 디스크 오류는 `:85`의 catch로 소프트 페일되고, `OperationCanceledException`은 `:81`에서 재던져진다.

- [ ] **Step 4: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MetadataExporterTests"
```

기대 결과: 신규 1건 PASS, 기존 전부 PASS. 특히 `ExportCodeObjectArtifactsAsync_ReferenceModeWritesCanonicalDdlOnly`가 계속 통과해야 한다 (그 테스트는 `Objects/` 아래 DDL 개수를 보므로 영향받지 않는다).

- [ ] **Step 5: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
git add src/ReSet.Core/Services/MetadataExporter.cs tests/ReSet.Core.Tests/MetadataExporterTests.cs
git commit -m "feat(export): emit metadata.json for recursively analyzed objects

Objects discovered through recursive analysis got a canonical DDL, a prompt
context and a manifest but no metadata.json, so the consolidated migration
bundle silently lost their dependency list -- the source it builds referenced
table schemas from. Write it alongside the manifest, where the Spec.md path
mapping already expects to find it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: BatchStepCatalog.FindStepCandidates (②)

**Files:**
- Create: `src/ReSet.Cli/BatchStepCatalog.cs`
- Test: `tests/ReSet.Core.Tests/BatchStepCatalogTests.cs` (신규)

**Interfaces:**
- Consumes: 없음
- Produces:
  ```csharp
  namespace ReSet.Cli;
  public static class BatchStepCatalog
  {
      public static IReadOnlyList<string> FindStepCandidates(string outputRoot);
  }
  ```
  반환값은 `outputRoot` 기준 상대 경로이며 **플랫폼 구분자를 그대로 유지**한다. 호출부가 `Path.Combine(outputRoot, relative)`로 바로 쓸 수 있어야 한다. Task 5가 같은 클래스에 `LoadDefinitionsAsync`를 추가하고, Task 6이 둘 다 사용한다.

**자격 판정 규칙** — 상대 경로를 `/`로 정규화했을 때 아래 두 형태 중 하나여야 한다.

```
Procedures/<객체>/docs/Spec.md
External/<DB>/Procedures/<객체>/docs/Spec.md
```

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/BatchStepCatalogTests.cs`를 새로 만든다.

```csharp
using System;
using System.IO;
using System.Linq;
using ReSet.Cli;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchStepCatalogTests
    {
        [Fact]
        public void FindStepCandidates_ReturnsProcedureSpecsFromCurrentAndExternalDatabases()
        {
            var root = CreateOutputTree();
            try
            {
                var candidates = BatchStepCatalog.FindStepCandidates(root)
                    .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();

                Assert.Equal(
                    new[]
                    {
                        "External/AuditDB/Procedures/dbo.USP_External/docs/Spec.md",
                        "Procedures/dbo.USP_Root/docs/Spec.md"
                    },
                    candidates);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void FindStepCandidates_ExcludesFunctionsAndJobArtifacts()
        {
            var root = CreateOutputTree();
            try
            {
                var candidates = BatchStepCatalog.FindStepCandidates(root)
                    .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
                    .ToList();

                Assert.DoesNotContain(candidates, path => path.Contains("/Functions/"));
                Assert.DoesNotContain(candidates, path => path.StartsWith("Functions/", StringComparison.Ordinal));
                Assert.DoesNotContain(candidates, path => path.StartsWith("Jobs/", StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void FindStepCandidates_ReturnsEmptyWhenOutputRootIsMissing()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"ReSet-Missing-{Guid.NewGuid():N}");

            Assert.Empty(BatchStepCatalog.FindStepCandidates(missing));
        }

        private static string CreateOutputTree()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchCatalog-{Guid.NewGuid():N}");
            WriteSpec(root, Path.Combine("Procedures", "dbo.USP_Root"));
            WriteSpec(root, Path.Combine("Functions", "dbo.UF_Helper"));
            WriteSpec(root, Path.Combine("External", "AuditDB", "Procedures", "dbo.USP_External"));
            WriteSpec(root, Path.Combine("External", "AuditDB", "Functions", "dbo.UF_ExternalHelper"));
            WriteSpec(root, Path.Combine("Jobs", "Nightly", "validation", "raw"));
            return root;
        }

        private static void WriteSpec(string root, string relativeObjectDirectory)
        {
            var docsDirectory = Path.Combine(root, relativeObjectDirectory, "docs");
            Directory.CreateDirectory(docsDirectory);
            File.WriteAllText(Path.Combine(docsDirectory, "Spec.md"), "# Spec");
        }
    }
}
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~BatchStepCatalogTests"
```

기대 결과: 컴파일 에러 — `BatchStepCatalog` 형식을 찾을 수 없음. 이는 정상적인 RED다.

- [ ] **Step 3: 최소 구현을 작성한다**

`src/ReSet.Cli/BatchStepCatalog.cs`를 새로 만든다.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace ReSet.Cli
{
    /// <summary>
    /// 통합 배치 설계의 스텝 후보를 선별하고 각 스텝의 분석 메타데이터를 복원한다.
    /// </summary>
    public static class BatchStepCatalog
    {
        private const string SpecFileName = "Spec.md";

        /// <summary>
        /// 배치 스텝 자격이 있는 명세서만 outputRoot 기준 상대 경로로 돌려준다.
        /// 배치 스텝은 프로시저이므로 UDF와 Job 검증 중간산출물은 제외한다.
        /// </summary>
        public static IReadOnlyList<string> FindStepCandidates(string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
            {
                return Array.Empty<string>();
            }

            try
            {
                return Directory
                    .GetFiles(outputRoot, SpecFileName, SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(outputRoot, path))
                    .Where(IsProcedureSpec)
                    .ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warning(
                    exception,
                    "[배치 설계] 스텝 후보 탐색 실패 (계속 진행): {OutputRoot}",
                    outputRoot);
                return Array.Empty<string>();
            }
        }

        // 객체 유형은 OutputPathResolver가 디렉터리 이름으로 인코딩하므로
        // 파일을 열지 않고 경로 형태만으로 판정할 수 있다.
        private static bool IsProcedureSpec(string relativePath)
        {
            var segments = relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            // Procedures/<객체>/docs/Spec.md
            if (segments.Length == 4 &&
                segments[0].Equals("Procedures", StringComparison.OrdinalIgnoreCase) &&
                segments[2].Equals("docs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // External/<DB>/Procedures/<객체>/docs/Spec.md
            return segments.Length == 6 &&
                   segments[0].Equals("External", StringComparison.OrdinalIgnoreCase) &&
                   segments[2].Equals("Procedures", StringComparison.OrdinalIgnoreCase) &&
                   segments[4].Equals("docs", StringComparison.OrdinalIgnoreCase);
        }
    }
}
```

- [ ] **Step 4: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~BatchStepCatalogTests"
```

기대 결과: 3건 PASS.

- [ ] **Step 5: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
git add src/ReSet.Cli/BatchStepCatalog.cs tests/ReSet.Core.Tests/BatchStepCatalogTests.cs
git commit -m "feat(cli): add BatchStepCatalog to select procedure step candidates

Recursive analysis grew the output tree to 33 Spec.md files of which only 14
are procedures, and the batch step picker offered all of them -- UDFs and job
validation intermediates included. Select by path shape, which already encodes
the object type, and keep the logic out of the TUI loop so it can be tested.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: BatchStepCatalog.LoadDefinitionsAsync (③ 표면)

**Files:**
- Modify: `src/ReSet.Cli/BatchStepCatalog.cs`
- Test: `tests/ReSet.Core.Tests/BatchStepCatalogTests.cs`

**Interfaces:**
- Consumes: Task 4의 `BatchStepCatalog` 클래스와 `FindStepCandidates`
- Produces:
  ```csharp
  namespace ReSet.Cli;
  public sealed record BatchStepLoadResult(
      IReadOnlyList<SpDefinition> Definitions,
      IReadOnlyList<string> MissingMetadata,
      IReadOnlyList<string> FailedToParse);

  public static class BatchStepCatalog
  {
      public static Task<BatchStepLoadResult> LoadDefinitionsAsync(
          string outputRoot,
          IEnumerable<string> specRelativePaths,
          CancellationToken cancellationToken = default);
  }
  ```
  `Definitions`는 **입력 경로 순서를 보존**한다. 이 순서가 곧 배치 스텝 실행 순서이고 지시서에 그대로 반영된다. `MissingMetadata`·`FailedToParse`에는 입력받은 상대 경로를 그대로 담는다. Task 6이 이 결과를 사용한다.

- [ ] **Step 1: 실패하는 테스트 3개를 작성한다**

`tests/ReSet.Core.Tests/BatchStepCatalogTests.cs`의 `FindStepCandidates_ReturnsEmptyWhenOutputRootIsMissing` 뒤에 추가한다. 파일 상단 `using`에 `System.Threading`, `System.Threading.Tasks`, `ReSet.Core.Models`를 더한다.

```csharp
        [Fact]
        public async Task LoadDefinitionsAsync_PreservesInputOrder()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchLoad-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_First", "USP_First");
            WriteProcedure(root, "dbo.USP_Second", "USP_Second");
            WriteProcedure(root, "dbo.USP_Third", "USP_Third");
            try
            {
                var ordered = new[]
                {
                    Path.Combine("Procedures", "dbo.USP_Third", "docs", "Spec.md"),
                    Path.Combine("Procedures", "dbo.USP_First", "docs", "Spec.md"),
                    Path.Combine("Procedures", "dbo.USP_Second", "docs", "Spec.md")
                };

                var result = await BatchStepCatalog.LoadDefinitionsAsync(root, ordered, CancellationToken.None);

                Assert.Equal(
                    new[] { "USP_Third", "USP_First", "USP_Second" },
                    result.Definitions.Select(definition => definition.Name));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task LoadDefinitionsAsync_ReportsMissingMetadataSeparately()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchLoad-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_Complete", "USP_Complete");
            WriteSpecOnly(root, "dbo.USP_NoMetadata");
            try
            {
                var selected = new[]
                {
                    Path.Combine("Procedures", "dbo.USP_Complete", "docs", "Spec.md"),
                    Path.Combine("Procedures", "dbo.USP_NoMetadata", "docs", "Spec.md")
                };

                var result = await BatchStepCatalog.LoadDefinitionsAsync(root, selected, CancellationToken.None);

                Assert.Equal("USP_Complete", Assert.Single(result.Definitions).Name);
                Assert.Equal(
                    Path.Combine("Procedures", "dbo.USP_NoMetadata", "docs", "Spec.md"),
                    Assert.Single(result.MissingMetadata));
                Assert.Empty(result.FailedToParse);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task LoadDefinitionsAsync_ReportsUnparsableMetadataSeparately()
        {
            var root = Path.Combine(Path.GetTempPath(), $"ReSet-BatchLoad-{Guid.NewGuid():N}");
            WriteProcedure(root, "dbo.USP_Broken", "USP_Broken");
            File.WriteAllText(
                Path.Combine(root, "Procedures", "dbo.USP_Broken", "raw", "metadata.json"),
                "{ this is not json");
            try
            {
                var selected = new[] { Path.Combine("Procedures", "dbo.USP_Broken", "docs", "Spec.md") };

                var result = await BatchStepCatalog.LoadDefinitionsAsync(root, selected, CancellationToken.None);

                Assert.Empty(result.Definitions);
                Assert.Empty(result.MissingMetadata);
                Assert.Equal(
                    Path.Combine("Procedures", "dbo.USP_Broken", "docs", "Spec.md"),
                    Assert.Single(result.FailedToParse));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void WriteProcedure(string root, string objectDirectory, string procedureName)
        {
            WriteSpecOnly(root, objectDirectory);
            var rawDirectory = Path.Combine(root, "Procedures", objectDirectory, "raw");
            Directory.CreateDirectory(rawDirectory);
            var definition = new SpDefinition { Schema = "dbo", Name = procedureName, DdlText = "SELECT 1;" };
            File.WriteAllText(
                Path.Combine(rawDirectory, "metadata.json"),
                System.Text.Json.JsonSerializer.Serialize(definition));
        }

        private static void WriteSpecOnly(string root, string objectDirectory)
        {
            var docsDirectory = Path.Combine(root, "Procedures", objectDirectory, "docs");
            Directory.CreateDirectory(docsDirectory);
            File.WriteAllText(Path.Combine(docsDirectory, "Spec.md"), "# Spec");
        }
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~LoadDefinitionsAsync"
```

기대 결과: 컴파일 에러 — `LoadDefinitionsAsync`와 `BatchStepLoadResult`가 없음.

- [ ] **Step 3: 최소 구현을 작성한다**

`src/ReSet.Cli/BatchStepCatalog.cs`의 `using` 목록에 아래를 더한다.

```csharp
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
```

같은 파일 `namespace ReSet.Cli` 블록 안, `BatchStepCatalog` 클래스 앞에 레코드를 추가한다.

```csharp
    /// <summary>
    /// 배치 스텝 메타데이터 복원 결과. 복원 실패를 원인별로 나누어 호출부가 사실대로 알릴 수 있게 한다.
    /// </summary>
    public sealed record BatchStepLoadResult(
        IReadOnlyList<SpDefinition> Definitions,
        IReadOnlyList<string> MissingMetadata,
        IReadOnlyList<string> FailedToParse);
```

`BatchStepCatalog` 클래스 안, `IsProcedureSpec` 앞에 메서드를 추가한다.

```csharp
        /// <summary>
        /// 선택된 명세서들의 분석 메타데이터를 입력 순서 그대로 복원한다.
        /// 입력 순서가 곧 배치 스텝 실행 순서이므로 순서를 흐트러뜨리면 안 된다.
        /// </summary>
        public static async Task<BatchStepLoadResult> LoadDefinitionsAsync(
            string outputRoot,
            IEnumerable<string> specRelativePaths,
            CancellationToken cancellationToken = default)
        {
            var definitions = new List<SpDefinition>();
            var missingMetadata = new List<string>();
            var failedToParse = new List<string>();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (var specRelativePath in specRelativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var metadataRelativePath = specRelativePath.Replace(
                    Path.Combine("docs", "Spec.md"),
                    Path.Combine("raw", "metadata.json"));
                var metadataPath = Path.Combine(outputRoot, metadataRelativePath);

                if (!File.Exists(metadataPath))
                {
                    missingMetadata.Add(specRelativePath);
                    continue;
                }

                try
                {
                    var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
                    var definition = JsonSerializer.Deserialize<SpDefinition>(json, options);
                    if (definition is null)
                    {
                        failedToParse.Add(specRelativePath);
                        continue;
                    }

                    definitions.Add(definition);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Log.Warning(
                        exception,
                        "[배치 설계] 스텝 메타데이터 복원 실패 (계속 진행): {SpecPath}",
                        specRelativePath);
                    failedToParse.Add(specRelativePath);
                }
            }

            return new BatchStepLoadResult(definitions, missingMetadata, failedToParse);
        }
```

- [ ] **Step 4: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~BatchStepCatalogTests"
```

기대 결과: 6건 모두 PASS.

- [ ] **Step 5: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
git add src/ReSet.Cli/BatchStepCatalog.cs tests/ReSet.Core.Tests/BatchStepCatalogTests.cs
git commit -m "feat(cli): report why batch step metadata could not be restored

The TUI skipped a step whose metadata.json was absent without a word, while a
step whose metadata.json failed to parse produced a warning -- an asymmetry
that hid missing schema context in the generated instructions. Separate the
two causes and preserve input order, which is the batch execution order.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Program.cs 배선 (②③)

**Files:**
- Modify: `src/ReSet.Cli/Program.cs:977-982` (후보 수집·경고), `:1190-1212` (spDefs 복원)

**Interfaces:**
- Consumes: `BatchStepCatalog.FindStepCandidates(string)`, `BatchStepCatalog.LoadDefinitionsAsync(string, IEnumerable<string>, CancellationToken)`, `BatchStepLoadResult` (Task 4·5)
- Produces: 없음

이 태스크는 TUI 배선이라 단위 테스트를 붙이지 않는다. 로직은 Task 4·5에서 이미 테스트로 잠겨 있고, 여기서는 호출과 렌더링만 바꾼다. 검증은 빌드와 전체 테스트 통과로 한다.

- [ ] **Step 1: 후보 수집을 교체한다**

`src/ReSet.Cli/Program.cs:977`에서 아래를 찾아 바꾼다.

찾을 문자열:
```csharp
                        var specFiles = Directory.GetFiles(outputDir, "Spec.md", SearchOption.AllDirectories);
                        if (specFiles.Length == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉터리에 기분석된 명세서(Spec.md)가 존재하지 않습니다.[/]");
                            continue;
                        }
```

바꿀 문자열:
```csharp
                        var specFiles = BatchStepCatalog.FindStepCandidates(outputDir);
                        if (specFiles.Count == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉터리에 기분석된 프로시저 명세서(Spec.md)가 존재하지 않습니다. UDF와 Job 산출물은 배치 스텝이 될 수 없습니다.[/]");
                            continue;
                        }
```

- [ ] **Step 2: 후보 목록 변환을 조정한다**

바로 아래 루프(`:986-989`)는 절대 경로를 상대 경로로 바꾸고 있었다. `FindStepCandidates`가 이미 상대 경로를 돌려주므로 변환을 없앤다.

찾을 문자열:
```csharp
                        foreach (var file in specFiles)
                        {
                            remainingFiles.Add(Path.GetRelativePath(outputDir, file));
                        }
```

바꿀 문자열:
```csharp
                        remainingFiles.AddRange(specFiles);
```

- [ ] **Step 3: spDefs 복원 루프를 교체한다**

`src/ReSet.Cli/Program.cs:1190-1212`의 블록 전체를 찾아 바꾼다.

찾을 문자열:
```csharp
                            var spDefs = new List<SpDefinition>();
                            foreach (var fileName in selectedFiles)
                            {
                                var rawFileName = fileName.Replace(Path.Combine("docs", "Spec.md"), Path.Combine("raw", "metadata.json"));
                                var rawPath = Path.Combine(outputDir, rawFileName);
                                if (File.Exists(rawPath))
                                {
                                    try
                                    {
                                        var jsonContent = await File.ReadAllTextAsync(rawPath);
                                        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                        var spDef = System.Text.Json.JsonSerializer.Deserialize<SpDefinition>(jsonContent, options);
                                        if (spDef != null)
                                        {
                                            spDefs.Add(spDef);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        AnsiConsole.MarkupLine($"[yellow]경고: {rawFileName} 파일에서 메타데이터 복원 중 오류:[/] {Markup.Escape(ex.Message)}");
                                    }
                                }
                            }
```

바꿀 문자열:
```csharp
                            var loadResult = await BatchStepCatalog.LoadDefinitionsAsync(
                                outputDir,
                                selectedFiles,
                                activeCts.Token);
                            var spDefs = loadResult.Definitions.ToList();

                            foreach (var missing in loadResult.MissingMetadata)
                            {
                                AnsiConsole.MarkupLine(
                                    $"[yellow]경고: {Markup.Escape(missing)} 의 메타데이터(raw/metadata.json)가 없어 참조 테이블 스키마 없이 지시서에 포함됩니다. 해당 SP를 1번 메뉴로 다시 분석하면 채워집니다.[/]");
                            }

                            foreach (var failed in loadResult.FailedToParse)
                            {
                                AnsiConsole.MarkupLine(
                                    $"[yellow]경고: {Markup.Escape(failed)} 의 메타데이터를 읽지 못했습니다. 참조 테이블 스키마 없이 지시서에 포함됩니다.[/]");
                            }
```

- [ ] **Step 4: 빌드하고 전체 테스트를 실행한다**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -3
```

기대 결과: 빌드 경고 0 · 오류 0, 전체 테스트 통과.

`ReSet.Cli.csproj`는 `ImplicitUsings`가 켜져 있으므로 `ToList()`를 위한 `using System.Linq;`를 따로 추가할 필요가 없다.

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Cli/Program.cs
git commit -m "fix(cli): wire batch step selection through BatchStepCatalog

The step picker listed every Spec.md under the output root and dropped
procedures with no metadata.json in silence. Route both through the catalog so
only procedure artifacts are offered and every restore failure is reported
with what it costs the generated instructions.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: 통합 파이프라인 출력 경로 주입 (①)

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1481-1528`, `src/ReSet.Cli/Program.cs:1146`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  ```csharp
  public Task<(string? Plan, AiResult? Result)> RunConsolidatedPipelineAsync(
      List<(string FileName, string Content)> specs,
      string targetLanguage,
      string jobName,
      string provider,
      string outputRoot,
      bool isBatchMode = false,
      CancellationToken cancellationToken = default);
  ```
  `outputRoot`는 필수이며 비어 있으면 `ArgumentException`을 던진다. Task 8이 같은 메서드를 이어서 수정한다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

`tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` 끝의 클래스 닫는 중괄호 앞에 추가한다.

```csharp
        [Fact]
        public async Task RunConsolidatedPipelineAsync_WritesIntermediateArtifactsUnderProvidedOutputRoot()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Consolidated-{Guid.NewGuid():N}");
            var jobName = $"Job_{Guid.NewGuid():N}";
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();

            aiService.BrainstormBatchPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "brainstorm body" }));
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "structure body" }));
            aiService.GenerateConsolidatedBatchPlanAsync(
                    Arg.Any<string>(), Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult
                {
                    Content = "## 통합 배치 아키텍처 개요\n\n## Mermaid 기반 통합 흐름도\n\n## 단계별 이행 상세 및 의사코드\n\n## 통합 데이터 정합성 검증 SQL 세트\n"
                }));
            aiService.ReviewConsolidatedPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false }));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-test");
            var strayDirectory = Path.Combine(Directory.GetCurrentDirectory(), "output", "Jobs", jobName);

            try
            {
                await orchestrator.RunConsolidatedPipelineAsync(
                    new List<(string FileName, string Content)> { ("dbo.USP_Test", "## 개요") },
                    "C#", jobName, "OpenAI", outputRoot, isBatchMode: true);

                Assert.True(File.Exists(Path.Combine(outputRoot, "Jobs", jobName, "raw", "Brainstorming.md")));
                Assert.True(File.Exists(Path.Combine(outputRoot, "Jobs", jobName, "raw", "PlanStructure.md")));

                // CWD 폴백이 살아 있으면 여기에도 생긴다. 생성 여부만 보면 버그를 놓친다.
                Assert.False(Directory.Exists(strayDirectory), $"CWD에 산출물이 생겼습니다: {strayDirectory}");
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
                if (Directory.Exists(strayDirectory)) Directory.Delete(strayDirectory, true);
            }
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_RejectsEmptyOutputRoot()
        {
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(),
                Substitute.For<IAiService>(),
                new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(),
                "1", "gpt-test");

            await Assert.ThrowsAsync<ArgumentException>(() =>
                orchestrator.RunConsolidatedPipelineAsync(
                    new List<(string FileName, string Content)> { ("dbo.USP_Test", "## 개요") },
                    "C#", "Job", "OpenAI", "   ", isBatchMode: true));
        }
```

이 파일에는 `System.IO`·`System.Threading`·`System.Collections.Generic`·`NSubstitute` `using`이 이미 있으므로 추가할 것이 없다.

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~RunConsolidatedPipelineAsync_WritesIntermediateArtifacts|FullyQualifiedName~RunConsolidatedPipelineAsync_RejectsEmptyOutputRoot"
```

기대 결과: 컴파일 에러 — `RunConsolidatedPipelineAsync`에 `outputRoot` 인자가 없음.

- [ ] **Step 3: 시그니처에 outputRoot를 추가하고 검증한다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1481`에서 찾아 바꾼다.

찾을 문자열:
```csharp
            string jobName,
            string provider,
            bool isBatchMode = false,
            CancellationToken cancellationToken = default)
        {
            string? feedbackLog = null;
```

바꿀 문자열:
```csharp
            string jobName,
            string provider,
            string outputRoot,
            bool isBatchMode = false,
            CancellationToken cancellationToken = default)
        {
            // 호출부 결함이므로 CWD로 조용히 폴백하지 않고 즉시 드러낸다.
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                throw new ArgumentException("출력 디렉터리가 필요합니다.", nameof(outputRoot));
            }

            string? feedbackLog = null;
```

- [ ] **Step 4: 하드코딩된 경로를 교체한다**

같은 파일 `:1520`에서 찾아 바꾼다.

찾을 문자열:
```csharp
                            var rawDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "output", "Jobs", jobName, "raw");
```

바꿀 문자열:
```csharp
                            var rawDir = System.IO.Path.Combine(outputRoot, "Jobs", jobName, "raw");
```

- [ ] **Step 5: 호출부를 갱신한다**

`src/ReSet.Cli/Program.cs:1146`에서 찾아 바꾼다.

찾을 문자열:
```csharp
                            var pipelineResult = await orchestrator.RunConsolidatedPipelineAsync(specsData, targetLanguage, jobName, provider, cancellationToken: activeCts.Token);
```

바꿀 문자열:
```csharp
                            var pipelineResult = await orchestrator.RunConsolidatedPipelineAsync(specsData, targetLanguage, jobName, provider, outputDir, cancellationToken: activeCts.Token);
```

다른 호출부가 남아 있는지 확인한다. 컴파일 에러가 곧 목록이다.

```bash
dotnet build 2>&1 | grep -E "error" | head
```

- [ ] **Step 6: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

기대 결과: 신규 2건 PASS, 기존 전부 PASS.

- [ ] **Step 7: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs src/ReSet.Cli/Program.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix(pipeline): write consolidated intermediates under the configured output root

Brainstorming.md and PlanStructure.md went to CWD/output while
BatchMigrationPlan.md went to OutputSettings:Directory, splitting one job's
artifacts across two trees whenever the output path was configured away from
the default. Take the root as a required argument so the consolidated path
matches the recursive one, which already receives it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: L2 미수행과 통과의 분리 (④)

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1568-1627`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 7의 `RunConsolidatedPipelineAsync(..., string outputRoot, ...)` 시그니처
- Produces: 없음. 반환 타입은 그대로다.

- [ ] **Step 1: 실패하는 테스트를 작성한다**

Task 7에서 추가한 테스트 뒤에 추가한다.

```csharp
        [Fact]
        public async Task RunConsolidatedPipelineAsync_MarksPlanWhenCriticReviewCouldNotRun()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Consolidated-{Guid.NewGuid():N}");
            var jobName = $"Job_{Guid.NewGuid():N}";
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();

            aiService.BrainstormBatchPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "brainstorm body" }));
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "structure body" }));
            aiService.GenerateConsolidatedBatchPlanAsync(
                    Arg.Any<string>(), Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult
                {
                    Content = "## 통합 배치 아키텍처 개요\n\n## Mermaid 기반 통합 흐름도\n\n## 단계별 이행 상세 및 의사코드\n\n## 통합 데이터 정합성 검증 SQL 세트\n"
                }));
            aiService.ReviewConsolidatedPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new InvalidOperationException("critic endpoint down")));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-test");

            try
            {
                var (plan, _) = await orchestrator.RunConsolidatedPipelineAsync(
                    new List<(string FileName, string Content)> { ("dbo.USP_Test", "## 개요") },
                    "C#", jobName, "OpenAI", outputRoot, isBatchMode: true);

                Assert.Contains("[!NOTE]", plan);
                Assert.Contains("L2 AI 교차 리뷰가 수행되지 않았습니다", plan);
                userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }
```

- [ ] **Step 2: 테스트를 실행해 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MarksPlanWhenCriticReviewCouldNotRun"
```

기대 결과: FAIL. 현재는 리뷰 예외를 통과로 처리해 `[!NOTE]` 배너가 없고 `NotifyValidationSuccess`가 호출된다.

- [ ] **Step 3: 판정 조건을 분리한다**

`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:1614`에서 찾아 바꾼다.

찾을 문자열:
```csharp
                // 검증을 통과한 경우 루프 탈출
                if (l1Result.IsValid && (l2Result == null || !l2Result.HasDefects))
                {
```

바꿀 문자열:
```csharp
                // 리뷰를 수행하지 못한 경우: 소프트 페일로 계속 진행하되 통과와 구분해 표시한다.
                if (!reviewSuccess)
                {
                    _userInteraction.NotifyError(
                        $"{jobName} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 계획서를 확정합니다.");
                    consolidatedPlan =
                        "> [!NOTE]\n> **L2 AI 교차 리뷰가 수행되지 않았습니다.** 리뷰 호출이 실패하여 정합성 검증 없이 확정된 문서입니다. 내용을 직접 검토하십시오.\n\n" +
                        consolidatedPlan;
                    break;
                }

                // 검증을 통과한 경우 루프 탈출
                if (l1Result.IsValid && (l2Result == null || !l2Result.HasDefects))
                {
```

- [ ] **Step 4: 테스트를 실행해 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests"
```

기대 결과: 신규 1건 PASS, 기존 전부 PASS.

- [ ] **Step 5: 커밋한다**

```bash
dotnet build 2>&1 | tail -3
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "fix(pipeline): distinguish an unrun critic review from a passing one

A critic exception left l2Result null, which the success branch read as no
defects, so a review that never ran announced validation success and left no
trace in the document. Report it separately and stamp the plan with a NOTE
banner so a reader who only has the file knows it was never cross-checked.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: 최종 검증과 문서 동기화

**Files:**
- Modify: `AGENTS.md:236` (단위 테스트 개수 체크리스트)

**Interfaces:**
- Consumes: Task 1~8 전부
- Produces: 없음

- [ ] **Step 1: 전체 빌드와 테스트를 실행한다**

```bash
dotnet build 2>&1 | tail -3
dotnet test 2>&1 | tail -3
```

기대 결과: 경고 0 · 오류 0, 실패 0. 통과 개수는 313 + 13 = 326 근처여야 한다. 정확한 숫자를 기록한다.

- [ ] **Step 2: AGENTS.md의 테스트 개수를 실제 값으로 갱신한다**

`AGENTS.md`에서 찾아 바꾼다. `<실제개수>`는 Step 1의 출력에서 읽은 숫자로 대체한다.

찾을 문자열:
```
- [ ] `dotnet test` 명령어를 실행하여 313개의 단위 테스트가 모두 예외 없이 100% 통과(Passed)하였는가?
```

바꿀 문자열:
```
- [ ] `dotnet test` 명령어를 실행하여 <실제개수>개의 단위 테스트가 모두 예외 없이 100% 통과(Passed)하였는가?
```

- [ ] **Step 3: 문서 링크 유효성을 확인한다**

```bash
grep -o '](\.\./src/[^)]*)' docs/architecture.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "docs/$p" ] || echo "BROKEN architecture.md: $p"; done
grep -ho '](\./[^)]*)' AGENTS.md README.md | sed 's/](\(.*\))/\1/' \
  | while read -r p; do [ -e "$p" ] || echo "BROKEN: $p"; done
```

기대 결과: 빈 출력.

- [ ] **Step 4: 커밋한다**

```bash
git add AGENTS.md
git commit -m "docs: update unit test count after batch design hardening

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 5: 작업 트리가 깨끗한지 확인한다**

```bash
git status --short
git log --oneline -9
```

기대 결과: 변경 없음. Task 1~9의 커밋 8개가 보인다 (Task 6은 코드만이라 커밋 1개, 나머지 각 1개).

---

## 자체 검토 결과

**스펙 커버리지** — 9건 전부 태스크에 대응된다.

| 결함 | 태스크 |
|---|---|
| ⑤ Critic NOLOCK·INSERT-only 미검사 | Task 2 |
| ⑥ Shadow 복원 예시 | Task 1 (생성), Task 2 (검증) |
| ⑦ GOTO 차단 | Task 1 |
| ⑧ 청킹 트랜잭션 경계 | Task 1 |
| ⑨ Anti-Shortcut | Task 2 |
| ① 출력 경로 | Task 7 |
| ② 후보 필터 | Task 4, Task 6 |
| ③ 메타데이터 | Task 3 (근본), Task 5·6 (표면) |
| ④ L2 실패 표시 | Task 8 |

스펙의 "검증 시나리오" 4개 항목은 Task 9가 담당한다.

**스펙에서 명확해진 점** — 스펙의 "`<객체>/raw/metadata.json`"은 `Objects/<이름>.<Type>/raw/`와 `Procedures/<이름>/raw/` 사이에서 모호했다. Task 3에서 **매니페스트와 같은 디렉터리**(후자)로 확정했다. `Program.cs:1193`의 경로 매핑이 그것을 요구한다.

**타입 일관성** — `BatchStepCatalog.FindStepCandidates`(Task 4) → `IReadOnlyList<string>`을 Task 6이 `.Count`와 `AddRange`로 소비한다. `LoadDefinitionsAsync`(Task 5) → `BatchStepLoadResult`의 세 속성명(`Definitions`/`MissingMetadata`/`FailedToParse`)을 Task 6이 그대로 쓴다. `RunConsolidatedPipelineAsync`의 `outputRoot` 위치(Task 7)를 Task 8의 테스트가 동일한 인자 순서로 호출한다.
