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

            if (_snapshot.CodeObjects.TryGetValue(resolvedKey.CanonicalName, out var definition))
            {
                definition.ObjectKey = resolvedKey;
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
                definition.ObjectKey = resolvedKey;
                return Task.FromResult(definition);
            }

            throw new KeyNotFoundException(
                $"Code object '{resolvedKey.CanonicalName}' not found in the offline snapshot.");
        }

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
                .Select(dependency =>
                {
                    dependency.ReferencedDdlText = null;
                    return dependency;
                })
                .ToList();

            return directDefinition;
        }

        public Task<List<Dictionary<string, object>>> GetTableDataPreviewAsync(string connectionString, string? database, string schema, string tableName, int limit = 100, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("GetTableDataPreviewAsync is not supported in offline mode because table data is not cached in the snapshot.");
        }
    }
}
