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
