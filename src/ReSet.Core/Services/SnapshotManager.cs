using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
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
            var taskName = "extract_snapshot";
            progress.AddTask(taskName, "SP 목록 조회 중...");

            var connectionBuilder = new SqlConnectionStringBuilder(connectionString);
            var database = await dbService.GetCurrentDatabaseNameAsync(
                connectionString,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(database))
            {
                database = connectionBuilder.InitialCatalog;
            }
            if (string.IsNullOrWhiteSpace(database))
            {
                throw new InvalidOperationException(
                    "The current database could not be determined for snapshot export.");
            }

            var spNames = await dbService.GetStoredProcedureNamesAsync(connectionString, cancellationToken);
            var snapshot = new DbSnapshot
            {
                ExportedAt = DateTime.UtcNow,
                Server = connectionBuilder.DataSource,
                Database = database.Trim()
            };

            int current = 0;

            foreach (var name in spNames)
            {
                if (cancellationToken.IsCancellationRequested) break;

                current++;
                progress.UpdateTask(taskName, (double)current / spNames.Count, $"추출 중: {name}...");

                var parts = name.Split('.');
                var schema = parts.Length > 1 ? parts[0] : "dbo";
                var spName = parts.Length > 1 ? parts[1] : parts[0];

                try
                {
                    var rootKey = CodeObjectKey.Create(
                        snapshot.Database,
                        schema,
                        spName,
                        CodeObjectType.Procedure);
                    var spDetails = await dbService.GetCodeObjectDetailsAsync(
                        connectionString,
                        rootKey,
                        maxDepth,
                        cancellationToken);
                    snapshot.StoredProcedures[name] = spDetails;
                    snapshot.CodeObjects[rootKey.CanonicalName] = spDetails;

                    foreach (var dependency in spDetails.Dependencies)
                    {
                        var dependencyType = GetDependencyCodeObjectType(dependency.Type);
                        if (dependencyType == null)
                        {
                            continue;
                        }

                        var dependencyKey = CodeObjectKey.Create(
                            dependency.Database ??
                                dependency.SourceObjectKey?.Database ??
                                snapshot.Database,
                            dependency.Schema,
                            dependency.Name,
                            dependencyType.Value);
                        if (snapshot.CodeObjects.ContainsKey(dependencyKey.CanonicalName))
                        {
                            continue;
                        }

                        try
                        {
                            snapshot.CodeObjects[dependencyKey.CanonicalName] =
                                await dbService.GetCodeObjectDetailsAsync(
                                    connectionString,
                                    dependencyKey,
                                    maxDepth,
                                    cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(
                                ex,
                                "[SnapshotManager] Failed to extract code object dependency: {ObjectKey}",
                                dependencyKey.CanonicalName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[SnapshotManager] Failed to extract SP: {Name}", name);
                }
            }

            progress.UpdateTask(taskName, 1.0, "디스크에 스냅샷 저장 중...");
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            await File.WriteAllTextAsync(outputPath, json, cancellationToken);
            progress.CompleteTask(taskName);
            Log.Information(
                "[SnapshotManager] Offline snapshot saved to {Path} with {SpCount} SPs and {CodeObjectCount} code objects.",
                outputPath,
                snapshot.StoredProcedures.Count,
                snapshot.CodeObjects.Count);
        }

        public static async Task<DbSnapshot> ImportSnapshotAsync(string inputPath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputPath))
                throw new FileNotFoundException($"Snapshot file not found: {inputPath}");
            
            var json = await File.ReadAllTextAsync(inputPath, cancellationToken);
            var snapshot = JsonSerializer.Deserialize<DbSnapshot>(json);
            
            if (snapshot == null)
                throw new InvalidOperationException("Failed to deserialize the snapshot file.");

            snapshot.StoredProcedures = new(
                snapshot.StoredProcedures ?? new(),
                StringComparer.OrdinalIgnoreCase);
            snapshot.CodeObjects = new(
                snapshot.CodeObjects ?? new(),
                StringComparer.OrdinalIgnoreCase);

            return snapshot;
        }

        private static CodeObjectType? GetDependencyCodeObjectType(string type)
        {
            if (type.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase))
            {
                return CodeObjectType.Procedure;
            }

            if (type.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase))
            {
                return CodeObjectType.Function;
            }

            return null;
        }
    }
}
