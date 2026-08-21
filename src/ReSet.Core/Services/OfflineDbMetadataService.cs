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

            if (!TryResolveStoredDefinition(resolvedKey, out var stored, out var normalizedKey))
            {
                throw NotFoundException(resolvedKey);
            }

            // AnalyzeReferencedCodeObjects=false(기본값)일 때 실제로 타는 경로가 바로 이곳이다.
            // 스냅샷 딕셔너리의 인스턴스를 그대로 반환하고 그 위에서 재분석하면 다음 조회가
            // 오염되므로, 여기서도 Direct 경로와 같은 이유로 먼저 복제한다.
            var definition = CloneDefinition(stored, normalizedKey);
            definition.ObjectKey = normalizedKey;
            RelinkCodeObjectDdl(definition);
            NormalizeCodeObjectDependencyNames(definition);
            RefreshStaticAnalysis(definition, normalizedKey);
            return Task.FromResult(definition);
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

        /// <summary>
        /// 스냅샷에서 원본 정의를 찾는다. 반환하는 인스턴스는 스냅샷 딕셔너리가 소유한
        /// 바로 그 객체이므로, 호출부는 반드시 <see cref="CloneDefinition"/>으로 복제한
        /// 뒤에만 손대야 한다. 여기서 직접 변형하면 공유 상태가 오염된다.
        /// </summary>
        private bool TryResolveStoredDefinition(
            CodeObjectKey resolvedKey,
            out SpDefinition definition,
            out CodeObjectKey normalizedKey)
        {
            if (_snapshot.CodeObjects.TryGetValue(resolvedKey.CanonicalName, out var found) ||
                _snapshot.CodeObjects.TryGetValue(resolvedKey.LegacyCanonicalName, out found))
            {
                definition = found;
                normalizedKey = NormalizeToStoredName(resolvedKey, found);
                return true;
            }

            var legacyKey = $"{resolvedKey.Schema}.{resolvedKey.Name}";
            if (resolvedKey.Type == CodeObjectType.Procedure &&
                string.Equals(
                    resolvedKey.Database,
                    _snapshot.Database,
                    StringComparison.OrdinalIgnoreCase) &&
                _snapshot.StoredProcedures.TryGetValue(legacyKey, out found))
            {
                definition = found;
                normalizedKey = NormalizeToStoredName(resolvedKey, found);
                return true;
            }

            definition = null!;
            normalizedKey = resolvedKey;
            return false;
        }

        private static KeyNotFoundException NotFoundException(CodeObjectKey resolvedKey) =>
            new($"Code object '{resolvedKey.CanonicalName}' not found in the offline snapshot.");

        /// <summary>
        /// 스냅샷이 들고 있는 인스턴스를 JSON 왕복으로 깊은 복제한다. 이후의 재링크·재파싱은
        /// 전부 이 복제본 위에서만 일어나야 스냅샷을 두 번 조회했을 때 결과가 갈라지거나
        /// 첫 조회가 두 번째 조회를 오염시키는 일이 없다.
        /// </summary>
        private static SpDefinition CloneDefinition(SpDefinition definition, CodeObjectKey resolvedKey) =>
            JsonSerializer.Deserialize<SpDefinition>(JsonSerializer.Serialize(definition)) ??
            throw new InvalidOperationException(
                $"Code object '{resolvedKey.CanonicalName}' could not be copied from the offline snapshot.");

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

        private Task<SpDefinition> GetDirectDefinitionAsync(
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

            if (!TryResolveStoredDefinition(resolvedKey, out var stored, out var normalizedKey))
            {
                throw NotFoundException(resolvedKey);
            }

            var directDefinition = CloneDefinition(stored, normalizedKey);
            directDefinition.ObjectKey = normalizedKey;
            directDefinition.RawPromptContext = null;
            // 직접 의존성만 남기는 필터는 Direct 경로에만 있는 의미다. 재귀 경로
            // (GetCodeObjectDetailsAsync)는 이 필터를 적용하면 안 되므로 공유 헬퍼에
            // 넣지 않고 여기 호출부에 남긴다.
            directDefinition.Dependencies = directDefinition.Dependencies
                .Where(dependency =>
                    dependency.SourceObjectKey == normalizedKey &&
                    (includeExternalCodeObjects ||
                     string.IsNullOrWhiteSpace(dependency.Database) ||
                     string.Equals(
                         dependency.Database,
                         normalizedKey.Database,
                         StringComparison.OrdinalIgnoreCase)))
                .ToList();

            RelinkCodeObjectDdl(directDefinition);
            NormalizeCodeObjectDependencyNames(directDefinition);
            RefreshStaticAnalysis(directDefinition, normalizedKey);

            return Task.FromResult(directDefinition);
        }

        /// <summary>
        /// 스냅샷의 의존성 항목은 코드 객체의 DDL 링크가 비어 있을 수 있다. 정작 DDL 자체는
        /// CodeObjects에 들어 있으므로 여기서 이어 붙인다. 이렇게 하지 않으면 UIF_SettleYMD
        /// 같은 함수가 프롬프트에서 "DDL 수집 실패"로 남는다.
        /// </summary>
        /// <summary>
        /// 의존성 이름을 스냅샷에 저장된 객체의 표기에 맞춘다.
        ///
        /// [왜 별도 순회인가 - 2026-08-20 리뷰 Critical]
        /// 처음에는 이것을 RelinkCodeObjectDdl 안에 넣었다. 그 메서드는
        /// `ReferencedDdlText가 이미 있으면 continue`로 시작하는데, 온라인 추출기는
        /// 코드 객체 의존성의 DDL을 항상 채우고 스냅샷은 그 객체를 그대로 저장한다.
        /// 그래서 정규화가 <b>감사에서 실제로 어긋난 경로에서는 한 번도 돌지 않았다</b> —
        /// 고쳤다고 적힌 수정이 문제가 난 자리를 비껴간 것이다.
        ///
        /// 두 일은 조건이 다르다. DDL 재연결은 "비어 있을 때만" 하는 보충이고,
        /// 이름 정규화는 "저장된 객체를 찾을 수 있으면 언제나" 해야 하는 정정이다.
        /// 한 루프에 묶으면 앞의 조건이 뒤의 것까지 가둔다.
        ///
        /// [왜 이름 쪽을 고치는가]
        /// sys.sql_expression_dependencies의 referenced_entity_name은 카탈로그 표기가
        /// 아니라 호출식에 쓰인 표기를 돌려준다. T-SQL이 대소문자를 안 가리므로 원본이
        /// dbo.UF_Get_WorkDay2로 부르면 의존성 이름도 그 표기로 들어온다. 반면 산출물
        /// 디렉터리는 그 함수를 직접 분석할 때 쓴 카탈로그 표기로 만들어진다. 그
        /// 어긋남이 「참조 함수 (기계 확정 — 수정 금지)」 표의 명세서 링크에서 드러났고,
        /// 같은 문서의 「참조 코드 객체」 링크는 그래프 키를 써서 정본이라 문서가 자기
        /// 안에서 서로 달랐다. 사전을 OrdinalIgnoreCase로 열어 둔 덕에 조회는 되지만,
        /// 조회가 된다고 이름이 같은 것은 아니다.
        ///
        /// 코드 객체만 다룬다. 테이블·뷰 이름은 이 정정을 받지 않으므로 그쪽 표기
        /// 흔들림은 그대로 남는다 - 링크를 만드는 소비자가 코드 객체뿐이라 범위를
        /// 좁혔다.
        /// </summary>
        private void NormalizeCodeObjectDependencyNames(SpDefinition definition)
        {
            foreach (var dependency in definition.Dependencies)
            {
                var codeObjectType = SqlObjectTypeClassifier.ResolveCodeObjectType(dependency.Type);
                if (codeObjectType == CodeObjectType.Unresolved) continue;

                var dependencyKey = CodeObjectKey.Create(
                    string.IsNullOrWhiteSpace(dependency.Database) ? _snapshot.Database : dependency.Database!,
                    dependency.Schema,
                    dependency.Name,
                    codeObjectType);

                if (!_snapshot.CodeObjects.TryGetValue(dependencyKey.CanonicalName, out var stored) &&
                    !_snapshot.CodeObjects.TryGetValue(dependencyKey.LegacyCanonicalName, out stored))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(stored.Name)) dependency.Name = stored.Name;
                if (!string.IsNullOrWhiteSpace(stored.Schema)) dependency.Schema = stored.Schema;
            }
        }

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
