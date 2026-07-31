using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public interface IDbMetadataService
    {
        Task<string> GetCurrentDatabaseNameAsync(string connectionString, CancellationToken cancellationToken = default);
        Task<List<string>> GetStoredProcedureNamesAsync(string connectionString, CancellationToken cancellationToken = default);
        Task<SpDefinition> GetSpDetailsAsync(string connectionString, string schema, string spName, int maxDepth, CancellationToken cancellationToken = default);
        Task<SpDefinition> GetCodeObjectDetailsAsync(string connectionString, CodeObjectKey objectKey, int maxDepth, CancellationToken cancellationToken = default);
        Task<List<Dictionary<string, object>>> GetTableDataPreviewAsync(string connectionString, string? database, string schema, string tableName, int limit = 100, CancellationToken cancellationToken = default);
    }
}
