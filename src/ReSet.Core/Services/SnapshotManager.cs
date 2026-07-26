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
            var taskName = "extract_snapshot";
            progress.AddTask(taskName, "SP 목록 조회 중...");
            
            var spNames = await dbService.GetStoredProcedureNamesAsync(connectionString, cancellationToken);
            var snapshot = new DbSnapshot
            {
                ExportedAt = DateTime.UtcNow,
                Server = "Extracted_from_online_DB",
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
                    var spDetails = await dbService.GetSpDetailsAsync(connectionString, schema, spName, maxDepth, cancellationToken);
                    snapshot.StoredProcedures[name] = spDetails;
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
