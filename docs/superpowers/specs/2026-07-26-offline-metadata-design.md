# Offline Metadata Extraction Design

## Goal
Provide a mechanism for users to extract all database metadata (Stored Procedures, Tables, UDFs, Columns, Indexes, and their recursive dependencies) into a single local JSON file (`db_snapshot.json`). This allows users to completely shut down resource-heavy database containers (e.g., SQL Server Docker) and perform ReSet AI analysis entirely offline.

## Constraints & Context
- Target environment: Local development on constrained resources (MacBook running local LLMs alongside Docker).
- DB Size: Typically under 100 Stored Procedures.
- Memory: A single JSON file for <100 SPs is perfectly fine for in-memory caching.
- Limitation: Features requiring dynamic runtime data sampling (like Settlement Policy Generation via `GetTableDataPreviewAsync`) will not be supported in Offline Mode.

## Architecture & Components

### 1. `DbSnapshot` DTO Model
A simple wrapper model to store the snapshot.
- `ExportedAt`: Timestamp of extraction.
- `Server` & `Database`: Source metadata.
- `StoredProcedures`: A dictionary mapping SP names (`[schema].[name]`) to their fully loaded `SpDefinition`.

### 2. `SnapshotManager`
A new service responsible for extraction and file I/O.
- **ExportSnapshotAsync**: Takes an `IDbMetadataService`, fetches all SP names, iterates and fetches all `SpDefinition` details, and serializes the result to `snapshot.json`.
- **LoadSnapshot**: Deserializes the file from disk.

### 3. `OfflineDbMetadataService`
An implementation of `IDbMetadataService` for offline usage.
- Injected with a populated `DbSnapshot` instance.
- **GetStoredProcedureNamesAsync**: Returns `_snapshot.StoredProcedures.Keys`.
- **GetSpDetailsAsync**: Performs a dictionary lookup and returns the cached `SpDefinition`.
- **GetTableDataPreviewAsync**: Throws `NotSupportedException` (or returns empty list) as runtime data profiling is impossible offline.

### 4. CLI Entry Point (`Program.cs`)
- Added `--extract-snapshot <path>` to `CliArgs.cs`.
- When `--extract-snapshot` is provided:
  - Initializes `DbMetadataService` normally.
  - Calls `SnapshotManager.ExportSnapshotAsync`.
  - Exits cleanly.
- Added `DatabaseSettings:OfflineSnapshotPath` to `appsettings.json`.
- When `OfflineSnapshotPath` is provided and exists:
  - Reads the file via `SnapshotManager`.
  - Registers `OfflineDbMetadataService` as the implementation for `IDbMetadataService`.
  - Bypasses DB connection checks in `ConsoleUserInteraction.cs`.

## Testing & Validation
- Unit Tests: Verify `OfflineDbMetadataService` correct lookups.
- Unit Tests: Verify `SnapshotManager` serialization/deserialization integrity (checking nested dependency structures).
- Fast-Fail: The system must fail fast with a clear error message if the snapshot JSON is malformed or if `GetTableDataPreviewAsync` is invoked during offline mode.
