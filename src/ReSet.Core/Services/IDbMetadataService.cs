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
        Task<SpDefinition> GetCodeObjectDetailsDirectAsync(string connectionString, CodeObjectKey objectKey, CancellationToken cancellationToken = default, bool includeExternalCodeObjects = true);
        Task<List<Dictionary<string, object>>> GetTableDataPreviewAsync(string connectionString, string? database, string schema, string tableName, int limit = 100, CancellationToken cancellationToken = default);

        /// <summary>테이블의 행 수. 코드 테이블은 작다는 성질로 프로파일링 대상을 고르기 위한 것이다.</summary>
        Task<int> GetTableRowCountAsync(string connectionString, string? database, string schema, string tableName, CancellationToken cancellationToken = default);
    }
}
