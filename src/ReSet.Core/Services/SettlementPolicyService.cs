using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public class SettlementPolicyService : ISettlementPolicyService
    {
        private readonly IDbMetadataService _dbService;
        private readonly IAiService _aiService;

        public SettlementPolicyService(IDbMetadataService dbService, IAiService aiService)
        {
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));
        }

        public async Task<string> GenerateSettlementPolicyRulebookAsync(
            string connectionString, 
            List<string> spFullNames, 
            int maxDepth, 
            CancellationToken cancellationToken = default)
        {
            var spDefs = new List<SpDefinition>();
            var tablesToProfile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tableToDbMap = new Dictionary<string, (string? Database, string Schema, string Table)>(StringComparer.OrdinalIgnoreCase);

            // 1. SP 메타데이터 및 의존성 수집
            foreach (var spFullName in spFullNames)
            {
                var parts = spFullName.Split('.', 2);
                if (parts.Length < 2) continue;

                var schema = parts[0];
                var name = parts[1];

                var spDef = await _dbService.GetSpDetailsAsync(connectionString, schema, name, maxDepth, cancellationToken);
                spDefs.Add(spDef);

                foreach (var dep in spDef.Dependencies)
                {
                    if (SqlObjectTypeClassifier.IsTableOrView(dep.Type))
                    {
                        var key = string.IsNullOrEmpty(dep.Database) 
                            ? $"{dep.Schema}.{dep.Name}" 
                            : $"[{dep.Database}].[{dep.Schema}].[{dep.Name}]";

                        // 코드성/마스터성 테이블 패턴 식별
                        if (dep.Name.Contains("Code", StringComparison.OrdinalIgnoreCase) || 
                            dep.Name.Contains("Master", StringComparison.OrdinalIgnoreCase) || 
                            dep.Name.Contains("Policy", StringComparison.OrdinalIgnoreCase) || 
                            dep.Name.Contains("Setting", StringComparison.OrdinalIgnoreCase) ||
                            dep.Name.Contains("Map", StringComparison.OrdinalIgnoreCase) ||
                            dep.Name.Contains("Type", StringComparison.OrdinalIgnoreCase) ||
                            dep.Name.Contains("Group", StringComparison.OrdinalIgnoreCase) ||
                            dep.Name.Contains("Rate", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!tablesToProfile.Contains(key))
                            {
                                tablesToProfile.Add(key);
                                tableToDbMap[key] = (dep.Database, dep.Schema, dep.Name);
                            }
                        }
                    }
                }
            }

            // 2. 동적 스캔 (Data Profiling)
            var profilingResults = new Dictionary<string, List<Dictionary<string, object>>>();
            foreach (var tableKey in tablesToProfile)
            {
                var dbInfo = tableToDbMap[tableKey];
                try
                {
                    var data = await _dbService.GetTableDataPreviewAsync(connectionString, dbInfo.Database, dbInfo.Schema, dbInfo.Table, 100, cancellationToken);
                    profilingResults[tableKey] = data;

                    if (data.Count == 0)
                    {
                        var warnMsg = $"[정산 데이터 경고] {tableKey} 테이블의 실제 설정/공통코드 데이터가 비어있습니다. (0건)";
                        AddWarningToReferencingSps(spDefs, dbInfo.Schema, dbInfo.Table, warnMsg);
                    }
                }
                catch (Exception ex)
                {
                    profilingResults[tableKey] = new List<Dictionary<string, object>>();
                    var warnMsg = $"[정산 데이터 경고] {tableKey} 테이블이 데이터베이스에 존재하지 않거나 액세스할 수 없습니다. (오류: {ex.Message})";
                    AddWarningToReferencingSps(spDefs, dbInfo.Schema, dbInfo.Table, warnMsg);
                }
            }

            // 3. AI 정책 추론 및 병합
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var profilingDataJson = JsonSerializer.Serialize(profilingResults, options);

            var aiResult = await _aiService.GenerateSettlementPolicyRulebookAsync(spDefs, profilingDataJson, cancellationToken);
            var rulebook = aiResult.Content;

            // 4. 프로파일링 경고 수집 및 접두사 병합 (화면 고지 및 문서화 통합)
            var collectedDataWarnings = new List<string>();
            foreach (var spDef in spDefs)
            {
                foreach (var warn in spDef.Warnings)
                {
                    if (warn.StartsWith("[정산 데이터 경고]", StringComparison.OrdinalIgnoreCase) && !collectedDataWarnings.Contains(warn))
                    {
                        collectedDataWarnings.Add(warn);
                    }
                }
            }

            if (collectedDataWarnings.Count > 0)
            {
                // 화면 고지 (TUI용 콘솔 알림)
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n⚠️ [경고] 정산 정책 분석용 마스터 데이터 누락 감지:");
                foreach (var warn in collectedDataWarnings)
                {
                    Console.WriteLine($"  - {warn.Replace("[정산 데이터 경고] ", "")}");
                }
                Console.ResetColor();
                Console.WriteLine();

                // 문서화 (Alert Block 생성 및 파일 병합)
                var sbWarn = new StringBuilder();
                sbWarn.AppendLine("> [!WARNING]");
                sbWarn.AppendLine("> ### ⚠️ 정산 정책 분석 경고 (데이터 및 객체 누락)");
                sbWarn.AppendLine("> 로컬 환경에서 정산 정책을 도출하는 동안 일부 설정/코드 테이블의 데이터가 없거나 객체가 존재하지 않는 현상이 감지되었습니다.");
                sbWarn.AppendLine("> 정확한 비즈니스 로직 역추론을 위해 다음 누락된 정보를 로컬 DB에 보완해 주십시오.");
                foreach (var warn in collectedDataWarnings)
                {
                    sbWarn.AppendLine($"> - {warn.Replace("[정산 데이터 경고] ", "")}");
                }
                sbWarn.AppendLine();

                rulebook = sbWarn.ToString() + rulebook;
            }

            return rulebook;
        }

        private void AddWarningToReferencingSps(List<SpDefinition> spDefs, string schema, string tableName, string warningMessage)
        {
            foreach (var spDef in spDefs)
            {
                var isReferencing = spDef.Dependencies.Exists(d => 
                    d.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) && 
                    d.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
                    SqlObjectTypeClassifier.IsTableOrView(d.Type));

                if (isReferencing)
                {
                    if (!spDef.Warnings.Contains(warningMessage))
                    {
                        spDef.Warnings.Add(warningMessage);
                    }
                }
            }
        }
    }
}
