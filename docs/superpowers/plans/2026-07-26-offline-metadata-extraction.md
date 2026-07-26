# Offline Metadata Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement offline metadata extraction allowing users to export DB schema to a JSON file and run the AI analyzer completely offline without Docker/SQL Server.

**Architecture:** We will introduce a `DbSnapshot` model, a `SnapshotManager` to serialize/deserialize the DB metadata to/from disk, and an `OfflineDbMetadataService` which serves as an in-memory repository for `IDbMetadataService`. The CLI will parse `--extract-snapshot` and `appsettings.json`'s `OfflineSnapshotPath` to coordinate this flow.

**Tech Stack:** C#, .NET 10, System.Text.Json, xUnit, Moq, Spectre.Console

## Global Constraints

- Must follow C# standard naming conventions (PascalCase for classes/methods, camelCase for variables).
- Target .NET 10 (as defined in `ReSet.Core` and `ReSet.Cli`).
- Error handling must use `Log.Warning` or `Log.Error` before throwing, keeping with the existing robust try-catch policy.
- Test projects use xUnit and Moq.

---

### Task 1: Create `DbSnapshot` Data Model

**Files:**
- Create: `src/ReSet.Core/Models/DbSnapshot.cs`

**Interfaces:**
- Consumes: `SpDefinition` from `ReSet.Core.Models`
- Produces: `DbSnapshot` class with `ExportedAt`, `Server`, `Database`, and `StoredProcedures` (a Dictionary mapping SP names to `SpDefinition`).

- [ ] **Step 1: Write the failing test** (Optional for pure DTO, but we can just implement it directly as it's a simple POCO)
Since it's a POCO, we will just write the model.

- [ ] **Step 2: Write minimal implementation**

```csharp
using System;
using System.Collections.Generic;

namespace ReSet.Core.Models
{
    public class DbSnapshot
    {
        public DateTime ExportedAt { get; set; }
        public string Server { get; set; } = string.Empty;
        public string Database { get; set; } = string.Empty;
        public Dictionary<string, SpDefinition> StoredProcedures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/ReSet.Core/Models/DbSnapshot.cs
git commit -m "feat: add DbSnapshot model for offline mode"
```

---

### Task 2: Create `OfflineDbMetadataService`

**Files:**
- Create: `src/ReSet.Core/Services/OfflineDbMetadataService.cs`
- Create: `tests/ReSet.Core.Tests/OfflineDbMetadataServiceTests.cs`

**Interfaces:**
- Consumes: `IDbMetadataService`, `DbSnapshot`
- Produces: `OfflineDbMetadataService` that reads from the provided snapshot.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class OfflineDbMetadataServiceTests
    {
        [Fact]
        public async Task GetStoredProcedureNamesAsync_ReturnsNamesFromSnapshot()
        {
            var snapshot = new DbSnapshot();
            snapshot.StoredProcedures.Add("dbo.TestSp", new SpDefinition { Name = "TestSp", Schema = "dbo" });
            
            var service = new OfflineDbMetadataService(snapshot);
            var names = await service.GetStoredProcedureNamesAsync("dummy_conn", CancellationToken.None);
            
            Assert.Single(names);
            Assert.Contains("dbo.TestSp", names);
        }

        [Fact]
        public async Task GetSpDetailsAsync_ReturnsSpDefinition()
        {
            var snapshot = new DbSnapshot();
            var expectedDef = new SpDefinition { Name = "TestSp", Schema = "dbo" };
            snapshot.StoredProcedures.Add("dbo.TestSp", expectedDef);
            
            var service = new OfflineDbMetadataService(snapshot);
            var sp = await service.GetSpDetailsAsync("dummy", "dbo", "TestSp", 1, CancellationToken.None);
            
            Assert.Equal(expectedDef, sp);
        }

        [Fact]
        public async Task GetTableDataPreviewAsync_ThrowsNotSupportedException()
        {
            var service = new OfflineDbMetadataService(new DbSnapshot());
            await Assert.ThrowsAsync<NotSupportedException>(() => 
                service.GetTableDataPreviewAsync("dummy", null, "dbo", "Table1", 100, CancellationToken.None));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "OfflineDbMetadataServiceTests"`
Expected: Compilation failure because `OfflineDbMetadataService` doesn't exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public class OfflineDbMetadataService : IDbMetadataService
    {
        private readonly DbSnapshot _snapshot;

        public OfflineDbMetadataService(DbSnapshot snapshot)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public Task<List<string>> GetStoredProcedureNamesAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_snapshot.StoredProcedures.Keys.ToList());
        }

        public Task<SpDefinition> GetSpDetailsAsync(string connectionString, string schema, string spName, int maxDepth, CancellationToken cancellationToken = default)
        {
            var key = $"{schema}.{spName}";
            if (_snapshot.StoredProcedures.TryGetValue(key, out var definition))
            {
                return Task.FromResult(definition);
            }
            throw new KeyNotFoundException($"Stored procedure '{key}' not found in the offline snapshot.");
        }

        public Task<List<Dictionary<string, object>>> GetTableDataPreviewAsync(string connectionString, string? database, string schema, string tableName, int limit = 100, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("GetTableDataPreviewAsync is not supported in offline mode because table data is not cached in the snapshot.");
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "OfflineDbMetadataServiceTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/ReSet.Core/Services/OfflineDbMetadataService.cs tests/ReSet.Core.Tests/OfflineDbMetadataServiceTests.cs
git commit -m "feat: implement OfflineDbMetadataService for reading snapshot"
```

---

### Task 3: Create `SnapshotManager`

**Files:**
- Create: `src/ReSet.Core/Services/SnapshotManager.cs`

**Interfaces:**
- Consumes: `IDbMetadataService`, `DbSnapshot`
- Produces: `SnapshotManager.ExportSnapshotAsync`, `SnapshotManager.ImportSnapshotAsync`

- [ ] **Step 1: Write minimal implementation**

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class SnapshotManager
    {
        public static async Task ExportSnapshotAsync(
            IDbMetadataService dbService, 
            string connectionString, 
            string outputPath, 
            int maxDepth, 
            IMultiProgressScope progress,
            CancellationToken cancellationToken = default)
        {
            var spNames = await dbService.GetStoredProcedureNamesAsync(connectionString, cancellationToken);
            var snapshot = new DbSnapshot
            {
                ExportedAt = DateTime.UtcNow,
                Server = "Extracted_from_online_DB",
            };

            progress.Total = spNames.Count;
            int current = 0;

            foreach (var name in spNames)
            {
                if (cancellationToken.IsCancellationRequested) break;

                current++;
                progress.UpdateProgress((double)current / spNames.Count, $"Extracting {name}...");

                var parts = name.Split('.');
                var schema = parts.Length > 1 ? parts[0] : "dbo";
                var spName = parts.Length > 1 ? parts[1] : parts[0];

                try
                {
                    var spDetails = await dbService.GetSpDetailsAsync(connectionString, schema, spName, maxDepth, cancellationToken);
                    snapshot.StoredProcedures[name] = spDetails;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[SnapshotManager] Failed to extract SP: {Name}", name);
                }
            }

            progress.UpdateProgress(1.0, "Saving snapshot to disk...");
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            await File.WriteAllTextAsync(outputPath, json, cancellationToken);
            Log.Information("[SnapshotManager] Offline snapshot saved to {Path} with {Count} SPs.", outputPath, snapshot.StoredProcedures.Count);
        }

        public static async Task<DbSnapshot> ImportSnapshotAsync(string inputPath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputPath))
                throw new FileNotFoundException($"Snapshot file not found: {inputPath}");
            
            var json = await File.ReadAllTextAsync(inputPath, cancellationToken);
            var snapshot = JsonSerializer.Deserialize<DbSnapshot>(json);
            
            if (snapshot == null)
                throw new InvalidOperationException("Failed to deserialize the snapshot file.");
                
            return snapshot;
        }
    }
}
```

- [ ] **Step 2: Compile to verify**

Run: `dotnet build src/ReSet.Core`
Expected: Success

- [ ] **Step 3: Commit**

```bash
git add src/ReSet.Core/Services/SnapshotManager.cs
git commit -m "feat: add SnapshotManager for exporting and importing DbSnapshot"
```

---

### Task 4: Configure CLI Args & AppSettings

**Files:**
- Modify: `src/ReSet.Cli/CliArgs.cs`
- Modify: `src/ReSet.Cli/appsettings.json`

**Interfaces:**
- Produces: `ExtractSnapshotPath` CLI property, `OfflineSnapshotPath` config property.

- [ ] **Step 1: Modify `appsettings.json`**

Edit `src/ReSet.Cli/appsettings.json`, under `DatabaseSettings`:

```json
  "DatabaseSettings": {
    "Server": "localhost",
    "Database": "master",
    "MaxDependencyDepth": 3,
    "OfflineSnapshotPath": ""
  },
```

- [ ] **Step 2: Modify `CliArgs.cs`**

Edit `src/ReSet.Cli/CliArgs.cs`:
Add a property and modify `Program.ParseCommandLineArgs`.
Wait, `CliArgs` doesn't exist as its own file, it is declared inside `Program.cs` typically. Let's check: Yes, `Program.cs` has `public class CliArgs`. Let's create `CliArgs.cs` if it's there or just update `Program.cs`. Actually, `ParseCommandLineArgs` is in `Program.cs` and `CliArgs` is either at the bottom of `Program.cs` or in another file. I will use `Program.cs`. Wait, no, `CliArgs` is in `Program.cs`? Oh wait, my `grep` showed `CliArgs` in `Program.cs`. So Task 4 and 5 will modify `Program.cs`.

---

### Task 5: Integrate CLI Arguments & Offline Execution Flow

**Files:**
- Modify: `src/ReSet.Cli/Program.cs`

**Interfaces:**
- Consumes: `SnapshotManager`, `OfflineDbMetadataService`, `CliArgs`

- [ ] **Step 1: Update `CliArgs` in `Program.cs`**

Inside `src/ReSet.Cli/Program.cs` (or wherever `CliArgs` is defined):
```csharp
public string? ExtractSnapshotPath { get; set; }
```

In `ParseCommandLineArgs`:
```csharp
else if (arg.Equals("--extract-snapshot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
{
    cliArgs.ExtractSnapshotPath = args[++i];
}
```

- [ ] **Step 2: Implement Extraction Flow in `Main`**

After reading `appsettings.json` in `Program.cs`, check for extraction mode:
```csharp
            if (!string.IsNullOrEmpty(cliArgs.ExtractSnapshotPath))
            {
                AnsiConsole.MarkupLine("[yellow]오프라인 스냅샷 추출 모드를 시작합니다...[/]");
                var dbService = new DbMetadataService();
                await AnsiConsole.Progress()
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("[green]스냅샷 추출 중[/]");
                        var progressScope = new MultiProgressScopeWrapper(task);
                        await SnapshotManager.ExportSnapshotAsync(dbService, cliArgs.ConnectionString ?? $"Server={server};Database={database};Integrated Security=True;TrustServerCertificate=True;", cliArgs.ExtractSnapshotPath, maxDepth, progressScope, _currentCts.Token);
                    });
                
                AnsiConsole.MarkupLine("[green]스냅샷 추출이 완료되었습니다. 프로그램을 종료합니다.[/]");
                return;
            }
```

- [ ] **Step 3: Implement Offline Execution Flow in `Main`**

Before registering `DbMetadataService`, check `OfflineSnapshotPath`:
```csharp
            var offlinePath = configuration["DatabaseSettings:OfflineSnapshotPath"];
            IDbMetadataService metadataService;
            if (!string.IsNullOrWhiteSpace(offlinePath) && File.Exists(offlinePath))
            {
                AnsiConsole.MarkupLine($"[blue]오프라인 모드로 동작합니다. 스냅샷 로드 중: {offlinePath}[/]");
                var snapshot = await SnapshotManager.ImportSnapshotAsync(offlinePath, _currentCts.Token);
                metadataService = new OfflineDbMetadataService(snapshot);
            }
            else
            {
                metadataService = new DbMetadataService();
            }
```
*Note: Make sure to replace any direct instantiation of `new DbMetadataService()` in the main loop to use this `metadataService` instance instead. e.g. `services.AddSingleton<IDbMetadataService>(metadataService);` if DI is used, or passing it directly.*

- [ ] **Step 4: Build and test**
Run: `dotnet build src/ReSet.Cli`

- [ ] **Step 5: Commit**

```bash
git add src/ReSet.Cli/Program.cs src/ReSet.Cli/appsettings.json
git commit -m "feat: integrate offline extraction and execution flows in CLI"
```
