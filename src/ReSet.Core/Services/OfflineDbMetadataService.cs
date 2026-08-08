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

            RelinkCodeObjectDdl(directDefinition);
            RefreshStaticAnalysis(directDefinition, resolvedKey);

            return directDefinition;
        }

        /// <summary>
        /// 스냅샷의 의존성 항목은 코드 객체의 DDL 링크가 비어 있을 수 있다. 정작 DDL 자체는
        /// CodeObjects에 들어 있으므로 여기서 이어 붙인다. 이렇게 하지 않으면 UIF_SettleYMD
        /// 같은 함수가 프롬프트에서 "DDL 수집 실패"로 남는다.
        /// </summary>
        private void RelinkCodeObjectDdl(SpDefinition definition)
        {
            foreach (var dependency in definition.Dependencies)
            {
                if (!string.IsNullOrWhiteSpace(dependency.ReferencedDdlText)) continue;

                var codeObjectType = SqlObjectTypeClassifier.ResolveCodeObjectType(dependency.Type);
                if (codeObjectType == CodeObjectType.Unresolved) continue;

                var dependencyKey = CodeObjectKey.Create(
                    string.IsNullOrWhiteSpace(dependency.Database) ? _snapshot.Database : dependency.Database!,
                    dependency.Schema,
                    dependency.Name,
                    codeObjectType);

                if (_snapshot.CodeObjects.TryGetValue(dependencyKey.CanonicalName, out var stored) ||
                    _snapshot.CodeObjects.TryGetValue(dependencyKey.LegacyCanonicalName, out stored))
                {
                    dependency.ReferencedDdlText = stored.DdlText;
                }
            }
        }

        /// <summary>
        /// 저장된 파생 분석을 신뢰하지 않고 저장된 원본에서 다시 계산한다. 스냅샷은
        /// 데이터베이스의 스냅샷이지 분석 결과의 스냅샷이 아니다. 저장된 StaticAnalysis를
        /// 그대로 재생하면 파서를 아무리 고쳐도 오프라인 모드는 스냅샷을 뜬 시점의 옛
        /// 결과를 영원히 반복한다. 여기서 다시 파싱해야 파서 수정마다 스냅샷 재추출을
        /// 요구하지 않아도 된다.
        ///
        /// 스냅샷에는 호환성 수준이 저장되어 있지 않아 파서 기본값(160)을 쓴다 — 이 한계는
        /// 설계에서 이미 수용됐다. 재파싱이 실패하면(소프트 페일 원칙에 따라 새 예외를
        /// 만들지 않고) 저장본을 그대로 두어 오프라인 모드가 지금보다 나빠지지 않게 한다.
        /// 다만 표기 통일은 재파싱 성공 여부와 무관하게 항상 적용한다.
        /// </summary>
        private static void RefreshStaticAnalysis(SpDefinition definition, CodeObjectKey resolvedKey)
        {
            if (!string.IsNullOrWhiteSpace(definition.DdlText))
            {
                var tableColumnsMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var dependency in definition.Dependencies)
                {
                    if (!SqlObjectTypeClassifier.IsTableOrView(dependency.Type)) continue;
                    if (dependency.Columns == null || dependency.Columns.Count == 0) continue;

                    var dependencyName = string.IsNullOrEmpty(dependency.Database)
                        ? $"{dependency.Schema}.{dependency.Name}"
                        : $"[{dependency.Database}].[{dependency.Schema}].[{dependency.Name}]";

                    var columnNames = new List<string>();
                    foreach (var column in dependency.Columns)
                    {
                        columnNames.Add(column.ColumnName);
                    }
                    tableColumnsMap[dependencyName] = columnNames;
                }

                var reparsed = new SqlStaticParser().Analyze(
                    definition.DdlText,
                    tableColumnsMap: tableColumnsMap.Count > 0 ? tableColumnsMap : null);

                if (reparsed.IsParsedSuccessfully)
                {
                    definition.StaticAnalysis = reparsed;
                }
            }

            definition.StaticAnalysis = StaticAnalysisNormalizer.Normalize(
                definition.StaticAnalysis,
                resolvedKey.Database,
                definition.Schema);
        }

        public Task<List<Dictionary<string, object>>> GetTableDataPreviewAsync(string connectionString, string? database, string schema, string tableName, int limit = 100, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("GetTableDataPreviewAsync is not supported in offline mode because table data is not cached in the snapshot.");
        }
    }
}
