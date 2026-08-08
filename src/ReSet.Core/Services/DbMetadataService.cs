using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class DbMetadataService : IDbMetadataService
    {
        public async Task<string> GetCurrentDatabaseNameAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            var configuredDatabase =
                new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            if (!string.IsNullOrWhiteSpace(configuredDatabase))
            {
                return configuredDatabase.Trim();
            }

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return ResolveCurrentDatabase(
                configuredDatabase,
                connection.Database);
        }

        public async Task<List<string>> GetStoredProcedureNamesAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            Log.Information("[DbMetadata] SP 목록 조회 시작");
            var spList = new List<string>();
            var query = @"
                SELECT ROUTINE_SCHEMA + '.' + ROUTINE_NAME 
                FROM INFORMATION_SCHEMA.ROUTINES 
                WHERE ROUTINE_TYPE = 'PROCEDURE' 
                ORDER BY ROUTINE_SCHEMA, ROUTINE_NAME;";

            using (var conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync(cancellationToken);
                using (var cmd = new SqlCommand(query, conn))
                {
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            spList.Add(reader.GetString(0));
                        }
                    }
                }
            }
            Log.Information("[DbMetadata] SP 목록 조회 완료 - 발견 개수: {Count}개", spList.Count);
            return spList;
        }

        // 헬퍼 메서드: 특정 객체의 DDL 원본 텍스트 조회
        private async Task<string> GetObjectDdlAsync(string connectionString, string? database, string schema, string objectName, CancellationToken cancellationToken)
        {
            var fullName = string.IsNullOrEmpty(database) ? $"{schema}.{objectName}" : $"[{database}].[{schema}].[{objectName}]";
            Log.Debug("[DbMetadata] 객체 DDL 조회 시작: {FullName}", fullName);
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";
            var query = $@"
                SELECT sm.definition 
                FROM {cleanDb}sys.sql_modules sm
                INNER JOIN {cleanDb}sys.objects o ON sm.object_id = o.object_id
                INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @ObjectName AND s.name = @Schema;";

            using (var conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync(cancellationToken);
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@ObjectName", objectName);
                    cmd.Parameters.AddWithValue("@Schema", schema);
                    var result = await cmd.ExecuteScalarAsync(cancellationToken);
                    if (result != null && result != DBNull.Value)
                    {
                        Log.Debug("[DbMetadata] 객체 DDL 조회 성공 - 길이: {Length}자 ({FullName})",
                            result.ToString()?.Length ?? 0, fullName);
                        return result.ToString() ?? string.Empty;
                    }
                }
            }

            // Fallback: 스키마가 dbo인 상태로 실패한 경우, 스키마 조건을 완화하여 다른 스키마에 존재하는지 재조회
            if (schema == "dbo")
            {
                var fallbackQuery = $@"
                    SELECT TOP 1 sm.definition 
                    FROM {cleanDb}sys.sql_modules sm
                    INNER JOIN {cleanDb}sys.objects o ON sm.object_id = o.object_id
                    WHERE o.name = @ObjectName;";
                try
                {
                    using (var conn = new SqlConnection(connectionString))
                    {
                        await conn.OpenAsync(cancellationToken);
                        using (var cmd = new SqlCommand(fallbackQuery, conn))
                        {
                            cmd.Parameters.AddWithValue("@ObjectName", objectName);
                            var result = await cmd.ExecuteScalarAsync(cancellationToken);
                            if (result != null && result != DBNull.Value)
                            {
                                return result.ToString() ?? string.Empty;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) {}
            }

            Log.Warning("[DbMetadata] 객체 DDL 조회 실패 - 대상 객체가 존재하지 않습니다: {FullName}", fullName);
            throw new InvalidOperationException($"'{fullName}'의 DDL 코드를 찾을 수 없습니다.");
        }

        // 헬퍼 메서드: 특정 객체의 1차 의존 정보 목록 수집
        private async Task<List<DependencyInfo>> GetRawDependenciesAsync(string connectionString, string? database, string schema, string objectName, CancellationToken cancellationToken)
        {
            var targetName = string.IsNullOrEmpty(database) ? $"{schema}.{objectName}" : $"[{database}].[{schema}].[{objectName}]";
            Log.Debug("[DbMetadata] 의존성 조회 시작: {TargetName}", targetName);
            var rawDeps = new List<DependencyInfo>();
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";
            var query = $@"
                SELECT 
                    d.referenced_database_name AS ReferencedDatabase,
                    COALESCE(d.referenced_schema_name, 'dbo') AS ReferencedSchema,
                    d.referenced_entity_name AS ReferencedEntityName,
                    COALESCE(o2.type_desc, 'UNKNOWN') AS ReferencedType
                FROM {cleanDb}sys.sql_expression_dependencies d
                INNER JOIN {cleanDb}sys.objects o ON d.referencing_id = o.object_id
                INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                LEFT JOIN {cleanDb}sys.objects o2 ON d.referenced_id = o2.object_id
                WHERE o.name = @ObjectName AND s.name = @Schema;";

            using (var conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync(cancellationToken);
                var sourceDatabase = database ?? conn.Database;
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@ObjectName", objectName);
                    cmd.Parameters.AddWithValue("@Schema", schema);
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var rawDb = reader.IsDBNull(0) ? null : reader.GetString(0);
                            if (rawDb != null &&
                                string.Equals(rawDb, sourceDatabase, StringComparison.OrdinalIgnoreCase))
                            {
                                rawDb = null;
                            }

                            rawDeps.Add(new DependencyInfo
                            {
                                Database = rawDb,
                                Schema = reader.GetString(1),
                                Name = reader.GetString(2),
                                Type = reader.GetString(3)
                            });
                        }
                    }
                }
            }
            Log.Debug("[DbMetadata] 의존성 조회 완료 - {Count}개 의존 관계 발견 ({TargetName})", rawDeps.Count, targetName);
            return rawDeps;
        }

        private async Task<int> GetDatabaseCompatibilityLevelAsync(
            string connectionString,
            string? database,
            CancellationToken cancellationToken)
        {
            try
            {
                using (var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString))
                {
                    await conn.OpenAsync(cancellationToken);
                    var sql = string.IsNullOrWhiteSpace(database)
                        ? "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME();"
                        : "SELECT compatibility_level FROM sys.databases WHERE name = @Database;";
                    using (var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn))
                    {
                        if (!string.IsNullOrWhiteSpace(database))
                        {
                            cmd.Parameters.AddWithValue("@Database", database);
                        }

                        var result = await cmd.ExecuteScalarAsync(cancellationToken);
                        if (result != null && result != DBNull.Value)
                        {
                            return Convert.ToInt32(result);
                        }

                        // sys.databases.name은 서버 콜레이션을 따르므로 대소문자 구분 인스턴스에서는
                        // 설정된 DB 이름의 casing이 다르면 예외 없이 빈 결과가 나온다.
                        Log.Warning(
                            "[DbMetadata] 데이터베이스 호환성 수준 조회 결과가 비어 있습니다 - 대상 DB: {Database} (이름 표기/권한 확인 필요). 기본값 160으로 폴백합니다.",
                            string.IsNullOrWhiteSpace(database) ? "(연결의 기본 DB)" : database);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[DbMetadata] 데이터베이스 호환성 수준 조회 실패 (Soft Fail) - 기본값 160으로 폴백합니다.");
            }
            return 160;
        }

        private static CodeObjectType NormalizeCodeObjectType(string sqlServerType)
        {
            return sqlServerType.Trim().ToUpperInvariant() switch
            {
                "P" or "PC" => CodeObjectType.Procedure,
                "FN" or "IF" or "TF" or "FS" or "FT" => CodeObjectType.Function,
                _ => throw new InvalidOperationException(
                    $"SQL Server object type '{sqlServerType}' is not a supported procedure or function type.")
            };
        }

        private static string BuildVisitedObjectName(
            string database,
            string schema,
            string objectName) =>
            string.Join(
                ".",
                CodeObjectKey.EncodeCanonicalSegment(database),
                CodeObjectKey.EncodeCanonicalSegment(schema),
                CodeObjectKey.EncodeCanonicalSegment(objectName));

        private static string ResolveDependencyDatabase(
            string? dependencyDatabase,
            CodeObjectKey sourceObjectKey) =>
            dependencyDatabase ?? sourceObjectKey.Database;

        private static (string LookupDatabase, string? StoredDatabase)
            ResolveDynamicDependencyDatabases(
                string? dependencyDatabase,
                CodeObjectKey sourceObjectKey)
        {
            var lookupDatabase = ResolveDependencyDatabase(
                dependencyDatabase,
                sourceObjectKey);
            var storedDatabase = string.Equals(
                lookupDatabase,
                sourceObjectKey.Database,
                StringComparison.OrdinalIgnoreCase)
                ? null
                : lookupDatabase;
            return (lookupDatabase, storedDatabase);
        }

        /// <summary>
        /// SQL Server 식별자 비교는 대소문자를 구분하지 않으므로, 호출부 표기 대신
        /// 카탈로그에 등록된 실제 스키마·객체명을 키의 표준 표기로 채택한다.
        /// </summary>
        private static CodeObjectKey ResolveCatalogKey(
            CodeObjectKey requestedKey,
            string? catalogSchema,
            string? catalogName) =>
            CodeObjectKey.Create(
                requestedKey.Database,
                string.IsNullOrWhiteSpace(catalogSchema) ? requestedKey.Schema : catalogSchema,
                string.IsNullOrWhiteSpace(catalogName) ? requestedKey.Name : catalogName,
                requestedKey.Type);

        private async Task<(string TypeCode, string? CatalogSchema, string? CatalogName)>
            GetCodeObjectCatalogEntryAsync(
                string connectionString,
                string? database,
                string schema,
                string objectName,
                CancellationToken cancellationToken)
        {
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";
            var query = $@"
                SELECT o.type, s.name AS SchemaName, o.name AS ObjectName
                FROM {cleanDb}sys.objects o
                INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @ObjectName AND s.name = @Schema;";

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);
            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@ObjectName", objectName);
            cmd.Parameters.AddWithValue("@Schema", schema);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken) || await reader.IsDBNullAsync(0, cancellationToken))
            {
                var fullName = string.IsNullOrEmpty(database)
                    ? $"{schema}.{objectName}"
                    : $"[{database}].[{schema}].[{objectName}]";
                throw new InvalidOperationException($"'{fullName}'의 SQL Server 객체 타입을 찾을 수 없습니다.");
            }

            return (
                reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2));
        }

        private async Task<FunctionReturnInfo> GetFunctionReturnInfoAsync(
            string connectionString,
            string? database,
            string schema,
            string objectName,
            string typeCode,
            CancellationToken cancellationToken)
        {
            var returnInfo = new FunctionReturnInfo
            {
                IsTableValued = typeCode is "IF" or "TF" or "FT"
            };
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);

            if (!returnInfo.IsTableValued)
            {
                var scalarQuery = $@"
                    SELECT
                        t.name +
                        CASE
                            WHEN t.name IN ('char', 'varchar', 'binary', 'varbinary') THEN
                                '(' + CASE WHEN p.max_length = -1 THEN 'MAX' ELSE CAST(p.max_length AS VARCHAR(10)) END + ')'
                            WHEN t.name IN ('nchar', 'nvarchar') THEN
                                '(' + CASE WHEN p.max_length = -1 THEN 'MAX' ELSE CAST(p.max_length / 2 AS VARCHAR(10)) END + ')'
                            WHEN t.name IN ('decimal', 'numeric') THEN
                                '(' + CAST(p.precision AS VARCHAR(10)) + ',' + CAST(p.scale AS VARCHAR(10)) + ')'
                            ELSE ''
                        END
                    FROM {cleanDb}sys.parameters p
                    INNER JOIN {cleanDb}sys.objects o ON p.object_id = o.object_id
                    INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                    INNER JOIN {cleanDb}sys.types t ON p.user_type_id = t.user_type_id
                    WHERE o.name = @ObjectName
                      AND s.name = @Schema
                      AND p.parameter_id = 0;";

                using var cmd = new SqlCommand(scalarQuery, conn);
                cmd.Parameters.AddWithValue("@ObjectName", objectName);
                cmd.Parameters.AddWithValue("@Schema", schema);
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                returnInfo.DataType = result == null || result == DBNull.Value
                    ? string.Empty
                    : result.ToString() ?? string.Empty;
                return returnInfo;
            }

            var tableQuery = $@"
                SELECT
                    c.name,
                    t.name +
                    CASE
                        WHEN t.name IN ('char', 'varchar', 'binary', 'varbinary') THEN
                            '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length AS VARCHAR(10)) END + ')'
                        WHEN t.name IN ('nchar', 'nvarchar') THEN
                            '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length / 2 AS VARCHAR(10)) END + ')'
                        WHEN t.name IN ('decimal', 'numeric') THEN
                            '(' + CAST(c.precision AS VARCHAR(10)) + ',' + CAST(c.scale AS VARCHAR(10)) + ')'
                        ELSE ''
                    END,
                    CAST(c.is_nullable AS BIT)
                FROM {cleanDb}sys.columns c
                INNER JOIN {cleanDb}sys.objects o ON c.object_id = o.object_id
                INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                INNER JOIN {cleanDb}sys.types t ON c.user_type_id = t.user_type_id
                WHERE o.name = @ObjectName AND s.name = @Schema
                ORDER BY c.column_id;";

            using (var cmd = new SqlCommand(tableQuery, conn))
            {
                cmd.Parameters.AddWithValue("@ObjectName", objectName);
                cmd.Parameters.AddWithValue("@Schema", schema);
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    returnInfo.Columns.Add(new ColumnInfo
                    {
                        ColumnName = reader.GetString(0),
                        DataType = reader.GetString(1),
                        IsNullable = reader.GetBoolean(2)
                    });
                }
            }

            return returnInfo;
        }

        public Task<SpDefinition> GetSpDetailsAsync(
            string connectionString,
            string schema,
            string spName,
            int maxDepth,
            CancellationToken cancellationToken = default)
        {
            var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            var objectKey = CodeObjectKey.Create(database, schema, spName, CodeObjectType.Procedure);
            return GetCodeObjectDetailsAsync(connectionString, objectKey, maxDepth, cancellationToken);
        }

        // 메인 재귀 탐색 진입점
        public async Task<SpDefinition> GetCodeObjectDetailsAsync(
            string connectionString,
            CodeObjectKey objectKey,
            int maxDepth,
            CancellationToken cancellationToken = default)
        {
            return await GetCodeObjectDetailsCoreAsync(
                connectionString,
                objectKey,
                maxDepth,
                includeTransitiveDependencies: true,
                includeExternalCodeObjects: true,
                cancellationToken);
        }

        public Task<SpDefinition> GetCodeObjectDetailsDirectAsync(
            string connectionString,
            CodeObjectKey objectKey,
            CancellationToken cancellationToken = default,
            bool includeExternalCodeObjects = true) =>
            GetCodeObjectDetailsCoreAsync(
                connectionString,
                objectKey,
                maxDepth: 1,
                includeTransitiveDependencies: false,
                includeExternalCodeObjects,
                cancellationToken);

        private async Task<SpDefinition> GetCodeObjectDetailsCoreAsync(
            string connectionString,
            CodeObjectKey objectKey,
            int maxDepth,
            bool includeTransitiveDependencies,
            bool includeExternalCodeObjects,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(objectKey.Database))
            {
                objectKey = CodeObjectKey.Create(
                    await GetCurrentDatabaseNameAsync(
                        connectionString,
                        cancellationToken),
                    objectKey.Schema,
                    objectKey.Name,
                    objectKey.Type);
            }

            var database = string.IsNullOrWhiteSpace(objectKey.Database) ? null : objectKey.Database;
            var catalogEntry = await GetCodeObjectCatalogEntryAsync(
                connectionString, database, objectKey.Schema, objectKey.Name, cancellationToken);
            var typeCode = catalogEntry.TypeCode;
            var objectType = NormalizeCodeObjectType(typeCode);

            // 이후 모든 조회·산출물 경로·캐시 키가 카탈로그 표기 하나만 쓰도록 여기서 확정한다.
            objectKey = ResolveCatalogKey(objectKey, catalogEntry.CatalogSchema, catalogEntry.CatalogName);
            var objectDefinition = new SpDefinition
            {
                ObjectKey = objectKey,
                Schema = objectKey.Schema,
                Name = objectKey.Name,
                ObjectType = objectType
            };
            var objectFullName = objectKey.CanonicalName;
            Log.Information(
                "[DbMetadata] 코드 객체 상세 메타데이터 수집 시작 - 객체: {ObjectFullName}, MaxDepth: {MaxDepth}",
                objectFullName,
                maxDepth);

            // 1. 최상위 코드 객체의 DDL 조회
            try
            {
                objectDefinition.DdlText = await GetObjectDdlAsync(
                    connectionString, database, objectKey.Schema, objectKey.Name, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DbMetadata] 최상위 코드 객체 DDL 수집 실패 - 객체: {ObjectFullName}", objectFullName);
                objectDefinition.Warnings.Add($"[{objectFullName}] 최상위 코드 객체 DDL 수집 실패: {ex.Message}");
                throw;
            }

            if (objectType == CodeObjectType.Function)
            {
                try
                {
                    objectDefinition.FunctionReturn = await GetFunctionReturnInfoAsync(
                        connectionString,
                        database,
                        objectKey.Schema,
                        objectKey.Name,
                        typeCode,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "[DbMetadata] UDF 반환 메타데이터 수집 실패 (Soft Fail) - 객체: {ObjectFullName}", objectFullName);
                    objectDefinition.Warnings.Add($"[{objectFullName}] UDF 반환 메타데이터 수집 실패: {ex.Message}");
                }
            }

            // T-SQL 정적 분석 구동 (AST 기반 메타데이터 추출, 호환성 수준 적용)
            try
            {
                int compatLevel = await GetDatabaseCompatibilityLevelAsync(
                    connectionString, database, cancellationToken);
                var staticParser = new SqlStaticParser();
                objectDefinition.StaticAnalysis = staticParser.Analyze(objectDefinition.DdlText, compatLevel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[DbMetadata] SQL 정적 분석 구동 중 예외 발생 (Soft Fail)");
                objectDefinition.StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = false,
                    ParserWarningMessage = $"정적 분석기 기동 예외: {ex.Message}"
                };
            }

            // 2. 중복 방지 방문 해시셋 및 재귀 리스트 생성
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                BuildVisitedObjectName(
                    objectKey.Database,
                    objectKey.Schema,
                    objectKey.Name)
            };

            if (includeTransitiveDependencies)
            {
                // 2.5 최상위 코드 객체 내 동적 SQL 의존성 선행 분석
                await ResolveDynamicSqlDependenciesAsync(
                    connectionString,
                    database,
                    objectKey,
                    objectDefinition.DdlText,
                    1,
                    visited,
                    objectDefinition.Dependencies,
                    objectDefinition.Warnings,
                    cancellationToken);

                // 3. 재귀 수집 시작
                Log.Information("[DbMetadata] 재귀 의존성 탐색(DFS) 시작 - 객체: {ObjectFullName}", objectFullName);
                await GatherDependenciesRecursiveAsync(
                    connectionString,
                    database,
                    objectKey.Schema,
                    objectKey.Name,
                    objectKey,
                    1,
                    maxDepth,
                    visited,
                    objectDefinition.Dependencies,
                    objectDefinition.Warnings,
                    cancellationToken);
            }
            else
            {
                await GatherDirectDependenciesAsync(
                    connectionString,
                    database,
                    objectKey,
                    objectDefinition.Dependencies,
                    objectDefinition.Warnings,
                    includeExternalCodeObjects,
                    cancellationToken);
            }

            // 2차 정밀 정적 분석 재구동 (수집 완료된 실제 테이블 스키마 연동)
            try
            {
                var tableColumnsMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var dep in objectDefinition.Dependencies)
                {
                    // "SQL_TABLE_VALUED_FUNCTION"이 부분 문자열 "TABLE"을 포함하므로
                    // 인라인 Contains 판정을 쓰면 TVF가 테이블로 오분류된다.
                    // SqlObjectTypeClassifier.IsTableOrView로 위임한다.
                    if (SqlObjectTypeClassifier.IsTableOrView(dep.Type) && dep.Columns != null && dep.Columns.Count > 0)
                    {
                        var depFullName = string.IsNullOrEmpty(dep.Database)
                            ? $"{dep.Schema}.{dep.Name}"
                            : $"[{dep.Database}].[{dep.Schema}].[{dep.Name}]";

                        var colNames = new List<string>();
                        foreach (var col in dep.Columns)
                        {
                            colNames.Add(col.ColumnName);
                        }
                        tableColumnsMap[depFullName] = colNames;
                    }
                }

                if (tableColumnsMap.Count > 0)
                {
                    Log.Information("[DbMetadata] 의존 테이블 스키마 메타데이터 기반 2차 정밀 정적 분석 재구동 시작 - 객체: {ObjectFullName}", objectFullName);
                    int compatLevel = await GetDatabaseCompatibilityLevelAsync(
                        connectionString, database, cancellationToken);
                    var staticParser = new SqlStaticParser();
                    var refinedAnalysis = staticParser.Analyze(objectDefinition.DdlText, compatLevel, tableColumnsMap);
                    if (refinedAnalysis.IsParsedSuccessfully)
                    {
                        objectDefinition.StaticAnalysis = refinedAnalysis;
                        Log.Information("[DbMetadata] 2차 정밀 정적 분석 재구동 성공 및 교체 완료 - 객체: {ObjectFullName}", objectFullName);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[DbMetadata] 2차 정밀 정적 분석 재구동 중 예외 발생 (기존 결과 유지)");
            }

            // 정적 분석은 SQL에 적힌 표기를 그대로 남긴다. 여기서 canonical 3-part로
            // 통일해 두면 metadata.json·스냅샷·프롬프트가 같은 이름을 쓰게 된다.
            objectDefinition.StaticAnalysis = StaticAnalysisNormalizer.Normalize(
                objectDefinition.StaticAnalysis,
                objectDefinition.ObjectKey?.Database,
                objectDefinition.Schema);

            Log.Information(
                "[DbMetadata] 코드 객체 메타데이터 수집 완료 - 객체: {ObjectFullName}, 의존 객체: {DepCount}개, 경고: {WarnCount}개",
                objectFullName,
                objectDefinition.Dependencies.Count,
                objectDefinition.Warnings.Count);
            return objectDefinition;
        }

        private static string ResolveCurrentDatabase(
            string configuredDatabase,
            string connectedDatabase)
        {
            var database = string.IsNullOrWhiteSpace(configuredDatabase)
                ? connectedDatabase
                : configuredDatabase;
            if (string.IsNullOrWhiteSpace(database))
            {
                throw new InvalidOperationException(
                    "The current SQL Server database could not be determined.");
            }

            return database.Trim();
        }

        private async Task GatherDirectDependenciesAsync(
            string connectionString,
            string? database,
            CodeObjectKey sourceObjectKey,
            List<DependencyInfo> dependencies,
            List<string> warnings,
            bool includeExternalCodeObjects,
            CancellationToken cancellationToken)
        {
            try
            {
                var directDependencies = await GetRawDependenciesAsync(
                    connectionString,
                    database,
                    sourceObjectKey.Schema,
                    sourceObjectKey.Name,
                    cancellationToken);

                foreach (var dependency in directDependencies)
                {
                    var dependencyDatabase = ResolveDependencyDatabase(
                        dependency.Database,
                        sourceObjectKey);
                    var isExternalDependency =
                        !string.IsNullOrWhiteSpace(dependency.Database) &&
                        !string.Equals(
                            dependency.Database,
                            sourceObjectKey.Database,
                            StringComparison.OrdinalIgnoreCase);
                    if (!includeExternalCodeObjects && isExternalDependency)
                    {
                        continue;
                    }

                    // SQL Server는 크로스 DB 참조의 referenced_id를 NULL로 두므로 카탈로그 조인만으로는
                    // 외부 개체의 타입을 알 수 없다. 외부 의존성도 UNKNOWN이면 3-part 조회로 타입을 확정한다.
                    if (string.Equals(
                            dependency.Type,
                            "UNKNOWN",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        dependency.Type = await GetObjectTypeAsync(
                            connectionString,
                            dependencyDatabase,
                            dependency.Schema,
                            dependency.Name,
                            cancellationToken);
                    }

                    var directDependency = new DependencyInfo
                    {
                        SourceObjectKey = sourceObjectKey,
                        Database = dependency.Database,
                        Schema = dependency.Schema,
                        Name = dependency.Name,
                        Type = dependency.Type,
                        DiscoveryDepth = 1
                    };

                    try
                    {
                        if (!isExternalDependency &&
                            SqlObjectTypeClassifier.IsTableOrView(directDependency.Type))
                        {
                            directDependency.Columns = await GetTableColumnsAsync(
                                connectionString,
                                dependencyDatabase,
                                directDependency.Schema,
                                directDependency.Name,
                                cancellationToken);
                            directDependency.Description = await GetTableDescriptionAsync(
                                connectionString,
                                dependencyDatabase,
                                directDependency.Schema,
                                directDependency.Name,
                                cancellationToken);
                            directDependency.Indexes = await GetTableIndexesAsync(
                                connectionString,
                                dependencyDatabase,
                                directDependency.Schema,
                                directDependency.Name,
                                cancellationToken);
                        }
                        // 외부 DB 코드 객체의 DDL은 부모 프롬프트 컨텍스트에 필요하므로 함께 수집한다.
                        // (바로 위 테이블/뷰 분기는 외부 DB 스키마 수집이 범위 밖이라 기존 가드를 유지한다.)
                        else if (SqlObjectTypeClassifier.IsCodeObject(directDependency.Type))
                        {
                            directDependency.ReferencedDdlText = await GetObjectDdlAsync(
                                connectionString,
                                dependencyDatabase,
                                directDependency.Schema,
                                directDependency.Name,
                                cancellationToken);
                        }
                    }
                    // 취소를 삼키면 그래프 순회가 계속된다. 사용자가 멈추라고 한 뒤에도
                    // 남은 의존성을 전부 걷고 나서야 반환된다.
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        warnings.Add(
                            $"[{dependencyDatabase}.{directDependency.Schema}.{directDependency.Name}] 직접 의존 메타데이터 수집 실패: {exception.Message}");
                    }

                    dependencies.Add(directDependency);
                }
            }
            // 취소를 삼키면 그래프 순회가 계속된다. 사용자가 멈추라고 한 뒤에도
            // 남은 의존성을 전부 걷고 나서야 반환된다.
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Warning(exception, "[DbMetadata] 직접 의존성 수집 실패 (Soft Fail) - 객체: {ObjectKey}", sourceObjectKey.CanonicalName);
                warnings.Add($"[{sourceObjectKey.CanonicalName}] 직접 의존성 정보 수집 실패: {exception.Message}");
            }
        }

        // 재귀 호출 메서드 (DFS)
        private async Task GatherDependenciesRecursiveAsync(
            string connectionString, string? database, string schema, string name,
            CodeObjectKey sourceObjectKey,
            int currentDepth, int maxDepth, 
            HashSet<string> visited, List<DependencyInfo> dependencies,
            List<string> warnings, CancellationToken cancellationToken)
        {
            if (currentDepth > maxDepth) return;

            var targetName = string.IsNullOrEmpty(database) ? $"{schema}.{name}" : $"[{database}].[{schema}].[{name}]";
            Log.Debug("[DbMetadata] DFS 재귀 탐색 - Target: {TargetName}, Depth: {CurrentDepth}/{MaxDepth}",
                targetName, currentDepth, maxDepth);

            List<DependencyInfo> rawDeps;
            try
            {
                rawDeps = await GetRawDependenciesAsync(connectionString, database, schema, name, cancellationToken);
            }
            // 취소를 삼키면 그래프 순회가 계속된다. 사용자가 멈추라고 한 뒤에도
            // 남은 의존성을 전부 걷고 나서야 반환된다.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[DbMetadata] 의존 관계 수집 실패 (Soft Fail) - Target: {TargetName}", targetName);
                warnings.Add($"[{targetName}] 의존 관계 정보 수집 실패: {ex.Message}");
                return; // 수집 실패 시 조용히 스킵 (Soft Fail)
            }

            foreach (var rawDep in rawDeps)
            {
                var depFullName = string.IsNullOrEmpty(rawDep.Database) 
                    ? $"{rawDep.Schema}.{rawDep.Name}" 
                    : $"[{rawDep.Database}].[{rawDep.Schema}].[{rawDep.Name}]";
                var dependencyDatabase = ResolveDependencyDatabase(
                    rawDep.Database,
                    sourceObjectKey);
                var visitedName = BuildVisitedObjectName(
                    dependencyDatabase,
                    rawDep.Schema,
                    rawDep.Name);

                if (visited.Contains(visitedName)) continue;

                visited.Add(visitedName);

                var depInfo = new DependencyInfo
                {
                    SourceObjectKey = sourceObjectKey,
                    Database = rawDep.Database,
                    Schema = rawDep.Schema,
                    Name = rawDep.Name,
                    Type = rawDep.Type,
                    DiscoveryDepth = currentDepth
                };

                // 동일 DB 또는 타 DB 개체의 타입을 알 수 없는 경우 동적 확인
                if (rawDep.Type == "UNKNOWN")
                {
                    rawDep.Type = await GetObjectTypeAsync(connectionString, dependencyDatabase, rawDep.Schema, rawDep.Name, cancellationToken);
                    depInfo.Type = rawDep.Type;
                }

                // 스키마 조회 분기 (테이블, 뷰)
                if (SqlObjectTypeClassifier.IsTableOrView(rawDep.Type))
                {
                    try
                    {
                        depInfo.Columns = await GetTableColumnsAsync(connectionString, dependencyDatabase, rawDep.Schema, rawDep.Name, cancellationToken);
                        depInfo.Description = await GetTableDescriptionAsync(connectionString, dependencyDatabase, rawDep.Schema, rawDep.Name, cancellationToken);
                        depInfo.Indexes = await GetTableIndexesAsync(connectionString, dependencyDatabase, rawDep.Schema, rawDep.Name, cancellationToken);
                    }
                    // 취소를 삼키면 그래프 순회가 계속된다. 사용자가 멈추라고 한 뒤에도
                    // 남은 의존성을 전부 걷고 나서야 반환된다.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        warnings.Add($"[{depFullName}] 테이블 스키마, 코멘트 및 인덱스 정보 수집 실패: {ex.Message}");
                    }
                }
                // 코드 수집 및 하위 재귀 분기 (UDF, SP)
                else if (SqlObjectTypeClassifier.IsCodeObject(rawDep.Type))
                {
                    try
                    {
                        depInfo.ReferencedDdlText = await GetObjectDdlAsync(connectionString, dependencyDatabase, rawDep.Schema, rawDep.Name, cancellationToken);

                        var childType = SqlObjectTypeClassifier.ResolveCodeObjectType(rawDep.Type);
                        var childKey = CodeObjectKey.Create(
                            dependencyDatabase,
                            rawDep.Schema,
                            rawDep.Name,
                            childType);

                        // 참조 DDL 내 동적 SQL 의존성 분석 수행
                        await ResolveDynamicSqlDependenciesAsync(
                            connectionString,
                            dependencyDatabase,
                            childKey,
                            depInfo.ReferencedDdlText,
                            currentDepth,
                            visited,
                            dependencies,
                            warnings,
                            cancellationToken);

                        // 하위 재귀 수집 호출
                        await GatherDependenciesRecursiveAsync(
                            connectionString,
                            dependencyDatabase,
                            rawDep.Schema,
                            rawDep.Name,
                            childKey,
                            currentDepth + 1, maxDepth, visited, dependencies, warnings, cancellationToken);
                    }
                    // 취소를 삼키면 그래프 순회가 계속된다. 사용자가 멈추라고 한 뒤에도
                    // 남은 의존성을 전부 걷고 나서야 반환된다.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        warnings.Add($"[{depFullName}] 참조 객체 DDL 수집 실패: {ex.Message}");
                    }
                }

                dependencies.Add(depInfo);
            }
        }

        public async Task<List<ColumnInfo>> GetTableColumnsAsync(string connectionString, string? database, string schema, string tableName, CancellationToken cancellationToken = default)
        {
            var columns = new List<ColumnInfo>();
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";
            var query = $@"
                SELECT 
                    c.name AS ColumnName,
                    t.name + 
                    CASE 
                        WHEN t.name IN ('char', 'varchar', 'binary', 'varbinary') THEN 
                            '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length AS VARCHAR(10)) END + ')'
                        WHEN t.name IN ('nchar', 'nvarchar') THEN 
                            '(' + CASE WHEN c.max_length = -1 THEN 'MAX' ELSE CAST(c.max_length / 2 AS VARCHAR(10)) END + ')'
                        WHEN t.name IN ('decimal', 'numeric') THEN 
                            '(' + CAST(c.precision AS VARCHAR(10)) + ',' + CAST(c.scale AS VARCHAR(10)) + ')'
                        ELSE ''
                    END AS DataType,
                    CAST(c.is_nullable AS INT) AS IsNullable,
                    ISNULL((
                        SELECT 1 
                        FROM {cleanDb}sys.index_columns ic
                        INNER JOIN {cleanDb}sys.indexes idx ON ic.object_id = idx.object_id AND ic.index_id = idx.index_id
                        WHERE ic.object_id = o.object_id AND ic.column_id = c.column_id AND idx.is_primary_key = 1
                    ), 0) AS IsPrimaryKey,
                    ISNULL((
                        SELECT 1 
                        FROM {cleanDb}sys.foreign_key_columns fkc
                        WHERE fkc.parent_object_id = o.object_id AND fkc.parent_column_id = c.column_id
                    ), 0) AS IsForeignKey,
                    ISNULL((
                        SELECT CAST(value AS NVARCHAR(1000))
                        FROM {cleanDb}sys.extended_properties ep
                        WHERE ep.major_id = o.object_id AND ep.minor_id = c.column_id AND ep.class = 1 AND ep.name = 'MS_Description'
                    ), '') AS Description,
                    CAST(c.is_identity AS INT) AS IsIdentity,
                    ISNULL(dc.definition, '') AS DefaultValue
                FROM {cleanDb}sys.columns c
                INNER JOIN {cleanDb}sys.objects o ON c.object_id = o.object_id
                INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                INNER JOIN {cleanDb}sys.types t ON c.user_type_id = t.user_type_id
                LEFT JOIN {cleanDb}sys.default_constraints dc ON c.default_object_id = dc.object_id AND c.object_id = dc.parent_object_id
                WHERE s.name = @Schema AND o.name = @TableName
                ORDER BY c.column_id;";

            using (var conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync(cancellationToken);
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Schema", schema);
                    cmd.Parameters.AddWithValue("@TableName", tableName);
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var desc = reader.GetString(5);
                            columns.Add(new ColumnInfo
                            {
                                ColumnName = reader.GetString(0),
                                DataType = reader.GetString(1),
                                IsNullable = reader.GetInt32(2) == 1,
                                IsPrimaryKey = reader.GetInt32(3) == 1,
                                IsForeignKey = reader.GetInt32(4) == 1,
                                Description = desc,
                                IsDescriptionMissing = string.IsNullOrWhiteSpace(desc),
                                IsIdentity = reader.GetInt32(6) == 1,
                                DefaultValue = reader.IsDBNull(7) ? null : (string.IsNullOrWhiteSpace(reader.GetString(7)) ? null : reader.GetString(7))
                            });
                        }
                    }
                }
            }
            return columns;
        }

        public async Task<List<TableIndexInfo>> GetTableIndexesAsync(string connectionString, string? database, string schema, string tableName, CancellationToken cancellationToken = default)
        {
            var indexes = new Dictionary<string, TableIndexInfo>(StringComparer.OrdinalIgnoreCase);
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";
            var query = $@"
                SELECT 
                    i.name AS IndexName,
                    i.type_desc AS IndexType,
                    CAST(i.is_unique AS INT) AS IsUnique,
                    CAST(i.is_primary_key AS INT) AS IsPrimaryKey,
                    c.name AS ColumnName
                FROM {cleanDb}sys.indexes i
                INNER JOIN {cleanDb}sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                INNER JOIN {cleanDb}sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                INNER JOIN {cleanDb}sys.objects o ON i.object_id = o.object_id
                INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                WHERE s.name = @Schema AND o.name = @TableName AND i.name IS NOT NULL
                ORDER BY i.name, ic.key_ordinal;";

            try
            {
                using (var conn = new SqlConnection(connectionString))
                {
                    await conn.OpenAsync(cancellationToken);
                    using (var cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@Schema", schema);
                        cmd.Parameters.AddWithValue("@TableName", tableName);
                        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                        {
                            while (await reader.ReadAsync(cancellationToken))
                            {
                                var idxName = reader.GetString(0);
                                if (!indexes.TryGetValue(idxName, out var idxInfo))
                                {
                                    idxInfo = new TableIndexInfo
                                    {
                                        IndexName = idxName,
                                        IndexType = reader.GetString(1),
                                        IsUnique = reader.GetInt32(2) == 1,
                                        IsPrimaryKey = reader.GetInt32(3) == 1
                                    };
                                    indexes[idxName] = idxInfo;
                                }
                                idxInfo.Columns.Add(reader.GetString(4));
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[DbMetadata] 인덱스 정보 수집 실패 (Soft Fail) - Table: {Schema}.{Table}", schema, tableName);
            }
            return new List<TableIndexInfo>(indexes.Values);
        }

        private async Task<string> GetTableDescriptionAsync(string connectionString, string? database, string schema, string tableName, CancellationToken cancellationToken)
        {
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";
            var query = $@"
                SELECT CAST(ep.value AS NVARCHAR(MAX)) 
                FROM {cleanDb}sys.extended_properties ep
                INNER JOIN {cleanDb}sys.objects o ON ep.major_id = o.object_id
                INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @TableName 
                  AND s.name = @Schema 
                  AND ep.minor_id = 0 
                  AND ep.class = 1
                  AND ep.name = 'MS_Description';";

            try
            {
                using (var conn = new SqlConnection(connectionString))
                {
                    await conn.OpenAsync(cancellationToken);
                    using (var cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@TableName", tableName);
                        cmd.Parameters.AddWithValue("@Schema", schema);
                        var result = await cmd.ExecuteScalarAsync(cancellationToken);
                        if (result != null && result != DBNull.Value)
                        {
                            return result.ToString() ?? string.Empty;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 권한 오류 등 무시
            }
            return string.Empty;
        }

        private async Task<string> GetObjectTypeAsync(string connectionString, string? database, string schema, string objectName, CancellationToken cancellationToken)
        {
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";
            var query = $@"
                SELECT o.type_desc 
                FROM {cleanDb}sys.objects o
                INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @ObjectName AND s.name = @SchemaName;";

            try
            {
                using (var conn = new SqlConnection(connectionString))
                {
                    await conn.OpenAsync(cancellationToken);
                    using (var cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@ObjectName", objectName);
                        cmd.Parameters.AddWithValue("@SchemaName", schema);
                        var result = await cmd.ExecuteScalarAsync(cancellationToken);
                        if (result != null && result != DBNull.Value)
                        {
                            return result.ToString() ?? "UNKNOWN";
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 권한 오류 시 소프트 스킵
            }

            // Fallback: 스키마가 dbo인 상태로 실패한 경우, 스키마 조건을 완화하여 이름만으로 객체 타입 조회
            if (schema == "dbo")
            {
                var fallbackQuery = $@"
                    SELECT TOP 1 o.type_desc 
                    FROM {cleanDb}sys.objects o
                    WHERE o.name = @ObjectName;";
                try
                {
                    using (var conn = new SqlConnection(connectionString))
                    {
                        await conn.OpenAsync(cancellationToken);
                        using (var cmd = new SqlCommand(fallbackQuery, conn))
                        {
                            cmd.Parameters.AddWithValue("@ObjectName", objectName);
                            var result = await cmd.ExecuteScalarAsync(cancellationToken);
                            if (result != null && result != DBNull.Value)
                            {
                                return result.ToString() ?? "UNKNOWN";
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) {}
            }

            return "UNKNOWN";
        }

        // 동적 SQL DDL 텍스트 분석 및 누락된 의존 테이블 수집 헬퍼
        private async Task ResolveDynamicSqlDependenciesAsync(
            string connectionString,
            string? database,
            CodeObjectKey sourceObjectKey,
            string ddlText,
            int currentDepth,
            HashSet<string> visited, List<DependencyInfo> dependencies,
            List<string> warnings, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return;

            // 동적 SQL 감지 여부 파악 (EXEC, EXECUTE, sp_executesql)
            bool hasDynamicSql = ddlText.Contains("EXEC", StringComparison.OrdinalIgnoreCase) || 
                                 ddlText.Contains("EXECUTE", StringComparison.OrdinalIgnoreCase) || 
                                 ddlText.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase);

            if (!hasDynamicSql) return;

            var tablePatterns = new[]
            {
                @"FROM\s+([a-zA-Z0-9_\.\[\]]+)",
                @"JOIN\s+([a-zA-Z0-9_\.\[\]]+)",
                @"INSERT\s+(?:INTO\s+)?([a-zA-Z0-9_\.\[\]]+)",
                @"UPDATE\s+([a-zA-Z0-9_\.\[\]]+)",
                @"MERGE\s+(?:INTO\s+)?([a-zA-Z0-9_\.\[\]]+)"
            };

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in tablePatterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(ddlText, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    if (m.Groups.Count > 1 && !string.IsNullOrEmpty(m.Groups[1].Value))
                    {
                        var rawName = m.Groups[1].Value.Trim().Replace("[", "").Replace("]", "");
                        if (!string.IsNullOrEmpty(rawName) && 
                            !rawName.Equals("SELECT", StringComparison.OrdinalIgnoreCase) && 
                            !rawName.Equals("INSERT", StringComparison.OrdinalIgnoreCase) && 
                            !rawName.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(rawName);
                        }
                    }
                }
            }

            foreach (var candidate in candidates)
            {
                string? depDb = database;
                var schema = "dbo";
                var name = candidate;
                if (candidate.Contains('.'))
                {
                    var parts = candidate.Split('.');
                    if (parts.Length == 3)
                    {
                        depDb = parts[0];
                        schema = parts[1];
                        name = parts[2];
                    }
                    else if (parts.Length == 2)
                    {
                        schema = parts[0];
                        name = parts[1];
                    }
                }

                var databaseResolution = ResolveDynamicDependencyDatabases(
                    depDb,
                    sourceObjectKey);
                var lookupDatabase = databaseResolution.LookupDatabase;
                var storedDatabase = databaseResolution.StoredDatabase;
                var depFullName = string.IsNullOrEmpty(storedDatabase)
                    ? $"{schema}.{name}" 
                    : $"[{storedDatabase}].[{schema}].[{name}]";
                var visitedName = BuildVisitedObjectName(
                    lookupDatabase,
                    schema,
                    name);

                if (visited.Contains(visitedName)) continue;

                // 데이터베이스 실제 개체 여부 및 타입 조회
                string? objectType = null;
                var cleanDb = string.IsNullOrEmpty(lookupDatabase)
                    ? ""
                    : $"[{lookupDatabase.Replace("]", "]]")}].";
                var checkQuery = $@"
                    SELECT o.type_desc 
                    FROM {cleanDb}sys.objects o
                    INNER JOIN {cleanDb}sys.schemas s ON o.schema_id = s.schema_id
                    WHERE o.name = @ObjectName AND s.name = @Schema;";

                try
                {
                    using (var conn = new SqlConnection(connectionString))
                    {
                        await conn.OpenAsync(cancellationToken);
                        using (var cmd = new SqlCommand(checkQuery, conn))
                        {
                            cmd.Parameters.AddWithValue("@ObjectName", name);
                            cmd.Parameters.AddWithValue("@Schema", schema);
                            var result = await cmd.ExecuteScalarAsync(cancellationToken);
                            if (result != null && result != DBNull.Value)
                            {
                                objectType = result.ToString();
                            }
                        }
                    }
                }
                // 취소를 삼키면 그래프 순회가 계속된다. 사용자가 멈추라고 한 뒤에도
                // 남은 의존성을 전부 걷고 나서야 반환된다.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 조회 에러 시 소프트 페일로 스킵
                }

                // objectType != null 검사는 IsTableOrView가 null에 false를 돌려주므로
                // 기능적으로는 중복이지만, "조회 실패 시 스킵한다"는 의도를 코드에서
                // 바로 드러내므로 남겨 둔다. 실제 판정은 SqlObjectTypeClassifier에
                // 위임한다 - 여기 가드가 없으면 동적 SQL로 발견된 TVF가 그대로
                // 테이블/뷰 의존성으로 등록되어 DDL이 수집되지 않는다.
                if (objectType != null && SqlObjectTypeClassifier.IsTableOrView(objectType))
                {
                    visited.Add(visitedName);

                    var depInfo = new DependencyInfo
                    {
                        SourceObjectKey = sourceObjectKey,
                        IsDynamicSqlCandidate = true,
                        Database = storedDatabase,
                        Schema = schema,
                        Name = name,
                        Type = objectType,
                        DiscoveryDepth = currentDepth,
                        Description = "Dynamic SQL Analysis"
                    };

                    try
                    {
                        depInfo.Columns = await GetTableColumnsAsync(connectionString, lookupDatabase, schema, name, cancellationToken);
                        depInfo.Description = await GetTableDescriptionAsync(connectionString, lookupDatabase, schema, name, cancellationToken);
                        depInfo.Indexes = await GetTableIndexesAsync(connectionString, lookupDatabase, schema, name, cancellationToken);
                        if (string.IsNullOrEmpty(depInfo.Description))
                        {
                            depInfo.Description = "Dynamic SQL에 의해 동적 감지된 테이블";
                        }
                    }
                    // 취소를 삼키면 그래프 순회가 계속된다. 사용자가 멈추라고 한 뒤에도
                    // 남은 의존성을 전부 걷고 나서야 반환된다.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        warnings.Add($"[Dynamic SQL: {depFullName}] 테이블 스키마 수집 실패: {ex.Message}");
                    }

                    dependencies.Add(depInfo);
                }
            }
        }

        public async Task<List<Dictionary<string, object>>> GetTableDataPreviewAsync(
            string connectionString, string? database, string schema, string tableName, int limit = 100, CancellationToken cancellationToken = default)
        {
            var dataList = new List<Dictionary<string, object>>();
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";
            var escapedSchema = $"[{schema.Replace("]", "]]")}]";
            var escapedTable = $"[{tableName.Replace("]", "]]")}]";
            
            var query = $"SELECT TOP (@Limit) * FROM {cleanDb}{escapedSchema}.{escapedTable};";

            using (var conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync(cancellationToken);
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Limit", limit);
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                    {
                        var fieldCount = reader.FieldCount;
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var row = new Dictionary<string, object>();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                var name = reader.GetName(i);
                                var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                                row[name] = val ?? DBNull.Value;
                            }
                            dataList.Add(row);
                        }
                    }
                }
            }

            return dataList;
        }
    }
}
