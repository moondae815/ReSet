using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

        public Task<string> GetCurrentDatabaseNameAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_snapshot.Database);
        }

        public Task<List<string>> GetStoredProcedureNamesAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_snapshot.StoredProcedures.Keys.ToList());
        }

        public Task<SpDefinition> GetSpDetailsAsync(
            string connectionString,
            string schema,
            string spName,
            int maxDepth,
            CancellationToken cancellationToken = default)
        {
            var objectKey = CodeObjectKey.Create(_snapshot.Database, schema, spName, CodeObjectType.Procedure);
            return GetCodeObjectDetailsAsync(connectionString, objectKey, maxDepth, cancellationToken);
        }

        public Task<SpDefinition> GetCodeObjectDetailsAsync(
            string connectionString,
            CodeObjectKey objectKey,
            int maxDepth,
            CancellationToken cancellationToken = default)
        {
            var resolvedKey = CodeObjectKey.Create(
                string.IsNullOrWhiteSpace(objectKey.Database)
                    ? _snapshot.Database
                    : objectKey.Database,
                objectKey.Schema,
                objectKey.Name,
                objectKey.Type);

            if ((_snapshot.CodeObjects.TryGetValue(resolvedKey.CanonicalName, out var definition) ||
                 _snapshot.CodeObjects.TryGetValue(resolvedKey.LegacyCanonicalName, out definition)))
            {
                definition.ObjectKey = NormalizeToStoredName(resolvedKey, definition);
                return Task.FromResult(definition);
            }

            var legacyKey = $"{resolvedKey.Schema}.{resolvedKey.Name}";
            if (resolvedKey.Type == CodeObjectType.Procedure &&
                string.Equals(
                    resolvedKey.Database,
                    _snapshot.Database,
                    StringComparison.OrdinalIgnoreCase) &&
                _snapshot.StoredProcedures.TryGetValue(legacyKey, out definition))
            {
                definition.ObjectKey = NormalizeToStoredName(resolvedKey, definition);
                return Task.FromResult(definition);
            }

            throw new KeyNotFoundException(
                $"Code object '{resolvedKey.CanonicalName}' not found in the offline snapshot.");
        }

        /// <summary>
        /// 스냅샷 조회는 대소문자를 무시하므로, 호출부 표기 대신 스냅샷에 기록된 실제
        /// 스키마·객체명을 키로 되돌려 산출물 경로와 캐시 키가 갈라지지 않게 한다.
        /// </summary>
        private static CodeObjectKey NormalizeToStoredName(
            CodeObjectKey resolvedKey,
            SpDefinition definition) =>
            CodeObjectKey.Create(
                resolvedKey.Database,
                string.IsNullOrWhiteSpace(definition.Schema) ? resolvedKey.Schema : definition.Schema,
                string.IsNullOrWhiteSpace(definition.Name) ? resolvedKey.Name : definition.Name,
                resolvedKey.Type);

        public Task<SpDefinition> GetCodeObjectDetailsDirectAsync(
            string connectionString,
            CodeObjectKey objectKey,
            CancellationToken cancellationToken = default,
            bool includeExternalCodeObjects = true)
        {
            return GetDirectDefinitionAsync(
                connectionString,
                objectKey,
                cancellationToken,
                includeExternalCodeObjects);
        }

        private async Task<SpDefinition> GetDirectDefinitionAsync(
            string connectionString,
            CodeObjectKey objectKey,
            CancellationToken cancellationToken,
            bool includeExternalCodeObjects)
        {
            var resolvedKey = CodeObjectKey.Create(
                string.IsNullOrWhiteSpace(objectKey.Database)
                    ? _snapshot.Database
                    : objectKey.Database,
                objectKey.Schema,
                objectKey.Name,
                objectKey.Type);
            var definition = await GetCodeObjectDetailsAsync(
                connectionString,
                resolvedKey,
                0,
                cancellationToken);
            var directDefinition = JsonSerializer.Deserialize<SpDefinition>(
                JsonSerializer.Serialize(definition)) ??
                throw new InvalidOperationException(
                    $"Code object '{resolvedKey.CanonicalName}' could not be copied from the offline snapshot.");

            resolvedKey = definition.ObjectKey ?? resolvedKey;
            directDefinition.ObjectKey = resolvedKey;
            directDefinition.RawPromptContext = null;
            directDefinition.Dependencies = directDefinition.Dependencies
                .Where(dependency =>
                    dependency.SourceObjectKey == resolvedKey &&
                    (includeExternalCodeObjects ||
                     string.IsNullOrWhiteSpace(dependency.Database) ||
                     string.Equals(
                         dependency.Database,
                         resolvedKey.Database,
                         StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return directDefinition;
        }

        public Task<List<Dictionary<string, object>>> GetTableDataPreviewAsync(string connectionString, string? database, string schema, string tableName, int limit = 100, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("GetTableDataPreviewAsync is not supported in offline mode because table data is not cached in the snapshot.");
        }
    }
}
