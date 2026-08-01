# Global Cache Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a global cache with file-copy reuse and auto-migration to prevent duplicate AI analysis of SPs/UDFs across different Root SP runs.

**Architecture:** 
- Add `OriginalSpecPath` to `CacheEntry`.
- Refactor `CacheManager` to use a single global cache file (`output/.sp_cache_index.json`) by reading from `IConfiguration`.
- On Cache Hit, `CacheManager` will copy the `Spec.md` from `OriginalSpecPath` to the current `OutputPathResolver` destination.
- Add an auto-migration method to `CacheManager` to scan existing isolated cache files and merge them into the global cache upon startup.

**Tech Stack:** C#, .NET 10, System.Text.Json

## Global Constraints

- Never crash the pipeline on file IO errors (use try-catch and log warnings).
- Keep existing isolated folder structures intact for artifacts.
- Fallback to Cache Miss (return false) if the original cached file was deleted by the user.

---

### Task 1: Update CacheEntry Model

**Files:**
- Modify: `src/ReSet.Core/Models/CacheEntry.cs`

**Interfaces:**
- Consumes: N/A
- Produces: `OriginalSpecPath` string property on `CacheEntry`.

- [ ] **Step 1: Write the failing test**

```csharp
// No explicit test needed for a simple property addition, but we verify compilation.
```

- [ ] **Step 2: Write minimal implementation**

Modify `src/ReSet.Core/Models/CacheEntry.cs` to add `OriginalSpecPath`:

```csharp
namespace ReSet.Core.Models
{
    public class CacheEntry
    {
        public string ProcedureName { get; set; } = string.Empty;
        public CodeObjectKey? ObjectKey { get; set; }
        public DateTime LastAnalyzed { get; set; }
        public string SourceHash { get; set; } = string.Empty;
        public Dictionary<string, string> DependencyHashes { get; set; } = new();
        public string CompositeHash { get; set; } = string.Empty;
        public string SpecContentHash { get; set; } = string.Empty;
        public int SpecContentLength { get; set; }
        public string OriginalSpecPath { get; set; } = string.Empty; // Added
    }

    public class CacheIndex
    {
        public Dictionary<string, CacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/ReSet.Core/Models/CacheEntry.cs
git commit -m "feat: add OriginalSpecPath to CacheEntry"
```

---

### Task 2: Refactor CacheManager for Global Cache & File Copy

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs`

**Interfaces:**
- Consumes: `IConfiguration` to resolve `OutputSettings:Directory`.
- Produces: `IsCacheValid` copies files on hit. `UpdateCache` stores `OriginalSpecPath`.

- [ ] **Step 1: Inject IConfiguration and determine Global Cache Path**

Modify `CacheManager` to accept `IConfiguration` in its constructor (if not already present) or use `AppContext.BaseDirectory` + `output` as a fallback. 
Actually, `CacheManager` doesn't currently take `IConfiguration`. Let's just use a hardcoded global resolution for now based on the known output pattern, OR we can safely find the global output dir by looking at `AppContext.BaseDirectory` + `"output"`.
Wait, looking at `CacheManager.cs`, `IsCacheValid` and `UpdateCache` take `OutputPathResolver outputPaths`. `outputPaths.OutputRoot` is the specific root (e.g. `output/dbo.UP_xxx`). We can resolve the global root by finding the parent directory of `outputPaths.OutputRoot` if it matches our pattern, OR simply use `Path.Combine(Directory.GetCurrentDirectory(), "output")` as the global cache directory, since `ReSet.Cli` always sets it to `./output` by default.

Let's modify `LoadCacheIndex` and `SaveCacheIndex` to use a global path:
```csharp
private string GetGlobalCacheDirectory(string outputRoot)
{
    // outputRoot is typically like: /path/to/output/dbo.UP_UTIL_SETTLE_INS
    // We want /path/to/output
    var parent = Directory.GetParent(outputRoot);
    if (parent != null && parent.Name.Equals("output", StringComparison.OrdinalIgnoreCase))
    {
        return parent.FullName;
    }
    // Fallback if structure is different
    var currDirOutput = Path.Combine(Directory.GetCurrentDirectory(), "output");
    return currDirOutput;
}
```

- [ ] **Step 2: Update CacheManager.IsCacheValid to perform File Copy**

In `IsCacheValid`, when `isValid` is true:
```csharp
if (isValid)
{
    // Copy the original file to the new destination if they differ
    if (!string.IsNullOrEmpty(entry.OriginalSpecPath) && 
        File.Exists(entry.OriginalSpecPath) &&
        !string.Equals(entry.OriginalSpecPath, specFilePath, StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var destDir = Path.GetDirectoryName(specFilePath);
            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(entry.OriginalSpecPath, specFilePath, overwrite: true);
            Log.Information("캐시 파일 복사 완료: {Src} -> {Dest}", entry.OriginalSpecPath, specFilePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "캐시 파일 복사 실패, Cache Miss로 간주합니다: {Dest}", specFilePath);
            return false;
        }
    }
    else if (!File.Exists(specFilePath))
    {
        // We hit the cache but the file doesn't exist AND we have no OriginalSpecPath to copy from
        Log.Debug("캐시 히트이나 원본 파일이 존재하지 않아 Cache Miss 처리");
        return false;
    }

    Log.Information("캐시 히트 - 코드 객체: {ObjectKey} (분석 생략 가능)", cacheKey);
}
```

- [ ] **Step 3: Update CacheManager.UpdateCache to save OriginalSpecPath**

In `UpdateCache`, after creating/updating `CacheEntry`, set `OriginalSpecPath`:
```csharp
entry.OriginalSpecPath = outputPaths.ResolveSpecPath(objectKey);
```

Update `LoadCacheIndex` and `SaveCacheIndex` to use `GetGlobalCacheDirectory(outputPaths.OutputRoot)` instead of `outputDirectory`.

- [ ] **Step 4: Commit**

```bash
git add src/ReSet.Core/Services/CacheManager.cs
git commit -m "feat: refactor CacheManager to use global cache and copy files on hit"
```

---

### Task 3: Implement Auto-Migration of Legacy Caches

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs`

**Interfaces:**
- Consumes: Legacy `.sp_cache_index.json` files scattered in `output/*`
- Produces: Merged `output/.sp_cache_index.json`

- [ ] **Step 1: Write migration logic**

Add a method to `CacheManager`:
```csharp
public void MigrateLegacyCaches(string outputRoot)
{
    var globalDir = GetGlobalCacheDirectory(outputRoot);
    if (!Directory.Exists(globalDir)) return;

    var globalIndexPath = Path.Combine(globalDir, CacheIndexFileName);
    var globalIndex = LoadCacheIndex(outputRoot) ?? new CacheIndex();
    bool migratedAny = false;

    // Search for all .sp_cache_index.json files in subdirectories
    var legacyFiles = Directory.GetFiles(globalDir, CacheIndexFileName, SearchOption.AllDirectories);
    foreach (var file in legacyFiles)
    {
        if (string.Equals(file, globalIndexPath, StringComparison.OrdinalIgnoreCase)) continue;

        try
        {
            var json = File.ReadAllText(file);
            var legacyIndex = JsonSerializer.Deserialize<CacheIndex>(json, JsonOptions);
            if (legacyIndex?.Entries != null)
            {
                var legacyDir = Path.GetDirectoryName(file);
                var legacyResolver = new OutputPathResolver("legacy", legacyDir!); // Used just to resolve SpecPaths if needed

                foreach (var kvp in legacyIndex.Entries)
                {
                    // Update OriginalSpecPath if it was missing in legacy
                    if (string.IsNullOrEmpty(kvp.Value.OriginalSpecPath) && kvp.Value.ObjectKey != null)
                    {
                        var expectedPath = legacyResolver.ResolveSpecPath(kvp.Value.ObjectKey);
                        if (File.Exists(expectedPath))
                        {
                            kvp.Value.OriginalSpecPath = expectedPath;
                        }
                    }

                    // Only merge if the file actually exists
                    if (!string.IsNullOrEmpty(kvp.Value.OriginalSpecPath) && File.Exists(kvp.Value.OriginalSpecPath))
                    {
                        globalIndex.Entries[kvp.Key] = kvp.Value;
                        migratedAny = true;
                    }
                }
            }
            
            // Optionally delete or rename the legacy file to prevent re-migration
            File.Move(file, file + ".migrated", overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "레거시 캐시 마이그레이션 실패: {File}", file);
        }
    }

    if (migratedAny)
    {
        SaveCacheIndex(outputRoot, globalIndex);
        Log.Information("레거시 캐시 마이그레이션 완료 (통합 캐시에 병합됨)");
    }
}
```

- [ ] **Step 2: Trigger Migration on Startup**

In `ReSet.Cli/Program.cs` or simply the first time `CacheManager.IsCacheValid` or `UpdateCache` is called, trigger `MigrateLegacyCaches`.
To keep it contained in `CacheManager.cs`, add a static flag `_hasMigrated` and call it at the beginning of `IsCacheValid` and `UpdateCache`:

```csharp
private static bool _hasMigrated = false;
private static readonly object _migrationLock = new object();

private void EnsureMigrated(string outputRoot)
{
    if (_hasMigrated) return;
    lock (_migrationLock)
    {
        if (_hasMigrated) return;
        MigrateLegacyCaches(outputRoot);
        _hasMigrated = true;
    }
}
```
Call `EnsureMigrated(outputPaths.OutputRoot);` at the top of `IsCacheValid` and `UpdateCache`.

- [ ] **Step 3: Commit**

```bash
git add src/ReSet.Core/Services/CacheManager.cs
git commit -m "feat: add auto-migration for legacy isolated caches"
```
