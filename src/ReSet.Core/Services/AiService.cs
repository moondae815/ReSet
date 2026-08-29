using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class AiService : IAiService
    {
        private readonly IAiClient _aiClient;
        private readonly float _temperature;
        private readonly bool _enableOllamaThinking;
        private readonly int _criticScoreThreshold;
        private readonly bool _enableAstChunking;

        public string ProviderName => _aiClient.ProviderName;
        public string ModelName => _aiClient.ModelName;

        public AiService(IAiClient aiClient, float temperature, bool enableOllamaThinking = false, int criticScoreThreshold = 8, bool enableAstChunking = true)
        {
            _aiClient = aiClient ?? throw new ArgumentNullException(nameof(aiClient));
            _temperature = temperature;
            _enableOllamaThinking = enableOllamaThinking;
            _criticScoreThreshold = criticScoreThreshold;
            _enableAstChunking = enableAstChunking;
        }

        private string FormatTableSchemaToMarkdown(DependencyInfo dep, SpDefinition spDef)
        {
            var sb = new System.Text.StringBuilder();
            // 바로 위 의존성 목록(dependenciesText)이 BuildDependencyQualifiedName으로
            // canonical 3-part 표기를 쓰므로 여기서도 같은 표기를 써야 한다. 한쪽만
            // 맞추면(예: 목록은 "SETTLE_POQ_DB.dbo.TCardAllotInterest", 이 헤더는
            // "dbo.TCardAllotInterest" 또는 "[DB].[Schema].[Name]") 모델이 같은 물리
            // 테이블을 서로 다른 두 테이블로 읽을 위험이 생긴다.
            var depFullName = BuildDependencyQualifiedName(dep, spDef);
            sb.AppendLine($"### 테이블: {depFullName} ({dep.Type}) - 발견 깊이: {dep.DiscoveryDepth}단계");
            if (!string.IsNullOrEmpty(dep.Description))
            {
                sb.AppendLine($"* 테이블 설명: {dep.Description}");
            }
            sb.AppendLine();
            sb.AppendLine("| 컬럼명 | 데이터 타입 | Null 허용 | Identity | 기본값 | 제약 조건 | 설명 |");
            sb.AppendLine("| :--- | :--- | :---: | :---: | :--- | :--- | :--- |");
            
            // 프롬프트에 어떤 컬럼이 실리는지는 SchemaPromptColumnSelector가 단독으로
            // 결정한다. L1(SpecExpectations)이 같은 함수를 불러 대조 기준을 만들므로,
            // 여기서 판정을 복제하면 두 권위가 가장자리에서 어긋난다.
            var shownColumns = SchemaPromptColumnSelector.Select(dep, spDef);

            foreach (var col in dep.Columns)
            {
                if (!shownColumns.Contains(col.ColumnName))
                {
                    continue;
                }

                var constraints = new System.Collections.Generic.List<string>();
                if (col.IsPrimaryKey) constraints.Add("PRIMARY KEY");
                if (col.IsForeignKey) constraints.Add("FOREIGN KEY");
                
                var constraintStr = string.Join(", ", constraints);
                var nullableStr = col.IsNullable ? "Yes" : "No";
                var identityStr = col.IsIdentity ? "Yes" : "No";
                var defaultStr = col.DefaultValue ?? "";
                var descStr = string.IsNullOrWhiteSpace(col.Description) ? "[설명 누락]" : col.Description;
                
                sb.AppendLine($"| {col.ColumnName} | {col.DataType} | {nullableStr} | {identityStr} | {defaultStr} | {constraintStr} | {descStr} |");
            }

            if (dep.Indexes != null && dep.Indexes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("#### 인덱스 정보");
                sb.AppendLine("| 인덱스명 | 타입 | Unique | PK 여부 | 구성 컬럼 |");
                sb.AppendLine("| :--- | :--- | :---: | :---: | :--- |");
                foreach (var idx in dep.Indexes)
                {
                    var uniqueStr = idx.IsUnique ? "Yes" : "No";
                    var pkStr = idx.IsPrimaryKey ? "Yes" : "No";
                    var colsStr = string.Join(", ", idx.Columns);
                    sb.AppendLine($"| {idx.IndexName} | {idx.IndexType} | {uniqueStr} | {pkStr} | {colsStr} |");
                }
            }
            
            return sb.ToString();
        }

        private static string BuildDependencyQualifiedName(DependencyInfo dep, SpDefinition spDef) =>
            StaticAnalysisNormalizer.CanonicalizeParts(
                dep.Database, dep.Schema, dep.Name, spDef.ObjectKey?.Database, spDef.Schema);

        private (string dependenciesText, string tableSchemasText, string referenceDdlsText, string staticAnalysisText) BuildSpMetadataTexts(SpDefinition spDef)
        {
            var dependenciesText = new StringBuilder();
            var tableSchemasText = new StringBuilder();
            var referenceDdlsText = new StringBuilder();

            foreach (var dep in spDef.Dependencies)
            {
                // 바로 아래 <referenced-table-schemas>가 3파트로 찍으므로 여기서도 DB를
                // 밝힌다. 안 그러면 PaymentDB.dbo.TTxMst와 dbo.TTxMst가 같은 줄로 보인다.
                var depQualifiedName = BuildDependencyQualifiedName(dep, spDef);
                dependenciesText.AppendLine($"- Name: {depQualifiedName}, Type: {dep.Type} (발견 깊이: {dep.DiscoveryDepth}단계)");

                if (dep.Columns.Count > 0)
                {
                    tableSchemasText.AppendLine(FormatTableSchemaToMarkdown(dep, spDef));
                    tableSchemasText.AppendLine();
                }

                if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                {
                    referenceDdlsText.AppendLine($"### 객체: {dep.Schema}.{dep.Name} ({dep.Type}) - 발견 깊이: {dep.DiscoveryDepth}단계");
                    referenceDdlsText.AppendLine("```sql");
                    referenceDdlsText.AppendLine(dep.ReferencedDdlText);
                    referenceDdlsText.AppendLine("```");
                    referenceDdlsText.AppendLine();
                }
                else if (SqlObjectTypeClassifier.IsCodeObject(dep.Type))
                {
                    referenceDdlsText.AppendLine($"### 객체: {dep.Schema}.{dep.Name} ({dep.Type}) [DDL 소스코드 수집 실패 / 미제공]");
                    referenceDdlsText.AppendLine("*이 객체의 정의 DDL이 시스템 상에서 수집되지 않았습니다. 내부 알고리즘 분석을 건너뛰고 호출 위치만 기록하십시오.*");
                    referenceDdlsText.AppendLine();
                }
            }

            if (tableSchemasText.Length > 0)
            {
                // A 검사(SchemaPromptColumnSelector.DetectOrphanedColumnKeys)가 이 문장을
                // 참으로 유지하고, L1의 CheckSchemaClaims가 위반을 잡는다. 부재 주장을
                // 적을 자리를 규정하지 않는 이유는 설계 문서에 있다 - 빈칸을 규정하는
                // 것 자체가 주장을 유도한다.
                tableSchemasText.AppendLine(
                    "> 이 표는 이 프로시저가 참조하는 컬럼에 대해 완전합니다. " +
                    "참조 컬럼이 스키마에 없다고 기술하지 마십시오.");
                tableSchemasText.AppendLine();
            }

            var staticAnalysisText = new StringBuilder();
            if (spDef.StaticAnalysis != null)
            {
                if (spDef.StaticAnalysis.IsParsedSuccessfully)
                {
                    staticAnalysisText.AppendLine("[Stored Procedure AST 정적 분석 정보 (AST Analysis Guidance)]");
                    staticAnalysisText.AppendLine($"- 식별된 참조 물리 테이블: {(spDef.StaticAnalysis.ReferencedTables.Count > 0 ? string.Join(", ", spDef.StaticAnalysis.ReferencedTables) : "없음")}");
                    
                    staticAnalysisText.AppendLine($"  * SELECT 대상 테이블: {(spDef.StaticAnalysis.SelectTables.Count > 0 ? string.Join(", ", spDef.StaticAnalysis.SelectTables) : "없음")}");
                    if (spDef.StaticAnalysis.SelectTables.Count > 0)
                    {
                        // [설명 칸이 술어를 담지 않는 이유 - 2026-08-23 ④ 진단]
                        // 옛 지시문은 "조건과 함께" 기술하라고 요구했고, 그러자 설명 칸이
                        // 여러 문장을 한 주장으로 묶었다(실측: `UPDATE 3 및 UPDATE 4에서
                        // YMD, CLIENTID, PGNAME, MALLID 조인` - UPDATE 4에는 MALLID 조인이
                        // 없다). Critic은 그 줄을 검토하고도 통과시켰다 - UPDATE 3에 근거가
                        // 있으니 "뒷받침된다"고 본 것이다(존재 검증과 전칭 검증의 바꿔치기).
                        //
                        // 술어와 조인 키는 DML 범위·집합 술어 표가 문장별로 확정하므로,
                        // 설명 칸이 그것을 나열할 자리를 없앤다 - 참조 함수 동작 서술 금지와
                        // 같은 계열이다. 틀릴 수 있는 주장의 부류 자체를 제거하는 편이
                        // "묶지 마라"는 지시보다 강하다.
                        //
                        // 이 문구는 BuildSpMetadataTexts를 부르는 여덟 호출부가 공유한다 -
                        // Actor·Critic·함수·분할 갈래가 같은 규칙을 보므로 한쪽만 바뀌어
                        // 교착이 나는 일이 구조적으로 없다.
                        staticAnalysisText.AppendLine(
                            "    (SELECT 대상 테이블은 CRUD 분석 표에 각각 독립적인 조회(SELECT) 참조 행으로 "
                            + "참조 컬럼과 함께 완전하게 기술되어야 합니다. 다만 설명 칸에 "
                            + "조인 키와 WHERE 술어를 나열하지 마십시오 - 그 사실은 "
                            + $"`{DmlScopeExtractor.DmlScopeTableHeading}`와 "
                            + $"`{DmlScopeExtractor.SetPredicateTableHeading}` 표가 문장별로 확정합니다. "
                            + "설명 칸에는 어느 문장에서 참조되는지와, 그 두 표가 담지 않는 사실만 적으십시오.)");
                    }
                    
                    staticAnalysisText.AppendLine($"  * INSERT 대상 테이블: {(spDef.StaticAnalysis.InsertTables.Count > 0 ? string.Join(", ", spDef.StaticAnalysis.InsertTables) : "없음")}");
                    if (spDef.StaticAnalysis.InsertTables.Count > 0)
                    {
                        staticAnalysisText.AppendLine("    (INSERT 대상 테이블은 삽입되는 모든 컬럼과 원천 데이터(SELECT 소스 컬럼, 하드코딩 상수, 함수 변환 등) 간의 1:1 대조 매핑 정보를 누락 없이 완전하게 표에 기술하십시오.)");
                    }
                    if (spDef.StaticAnalysis.AstInsertMappings != null && spDef.StaticAnalysis.AstInsertMappings.Count > 0)
                    {
                        staticAnalysisText.AppendLine();
                        staticAnalysisText.AppendLine("  [AST INSERT 타겟-소스 1:1 매핑 추출 데이터 (ABSOLUTE SOURCE OF TRUTH)]");
                        staticAnalysisText.AppendLine("  * L1 정적 파서(SqlScriptDom)가 INSERT 타겟 컬럼명과 소스 SELECT 쿼리 블록을 기계적으로 정확히 추출했습니다.");
                        staticAnalysisText.AppendLine("  * 아래 정보를 매핑 원천으로 절대적으로 신뢰하고 반영하십시오. 원본 쿼리에 없는 CAST 함수나 추가 논리를 임의로 지어내지(할루시네이션) 마십시오.");
                        foreach (var mapping in spDef.StaticAnalysis.AstInsertMappings)
                        {
                            staticAnalysisText.AppendLine($"    <insert-target table=\"{mapping.TargetTable}\">");
                            if (mapping.TargetColumns.Count > 0)
                            {
                                staticAnalysisText.AppendLine($"      <columns>{string.Join(", ", mapping.TargetColumns)}</columns>");
                            }
                            if (!string.IsNullOrEmpty(mapping.SourceQueryBlock))
                            {
                                staticAnalysisText.AppendLine($"      <source-query-block>");
                                staticAnalysisText.AppendLine(mapping.SourceQueryBlock);
                                staticAnalysisText.AppendLine($"      </source-query-block>");
                            }
                            staticAnalysisText.AppendLine($"    </insert-target>");
                        }
                        staticAnalysisText.AppendLine();
                    }
                    
                    staticAnalysisText.AppendLine($"  * UPDATE 대상 테이블: {(spDef.StaticAnalysis.UpdateTables.Count > 0 ? string.Join(", ", spDef.StaticAnalysis.UpdateTables) : "없음")}");
                    if (spDef.StaticAnalysis.AstUpdateMappings != null && spDef.StaticAnalysis.AstUpdateMappings.Count > 0)
                    {
                        staticAnalysisText.AppendLine();
                        staticAnalysisText.AppendLine("  [AST UPDATE 타겟-소스 1:1 매핑 추출 데이터 (ABSOLUTE SOURCE OF TRUTH)]");
                        staticAnalysisText.AppendLine("  * L1 정적 파서(SqlScriptDom)가 SET 절의 타겟 컬럼과 원천 표현식을 기계적으로 정확히 추출했습니다.");
                        staticAnalysisText.AppendLine("  * 아래 정보를 매핑 원천으로 절대적으로 신뢰하고 반영하십시오. 원본 쿼리에 없는 변환이나 추가 논리를 임의로 지어내지(할루시네이션) 마십시오.");
                        foreach (var mapping in spDef.StaticAnalysis.AstUpdateMappings)
                        {
                            staticAnalysisText.AppendLine($"    <update-target table=\"{mapping.TargetTable}\" statement=\"{mapping.StatementOrdinal}\" line=\"{mapping.SourceLine}\">");
                            foreach (var assignment in mapping.Assignments)
                            {
                                staticAnalysisText.AppendLine($"      <set column=\"{assignment.Column}\">{assignment.SourceExpression}</set>");
                            }
                            if (!string.IsNullOrEmpty(mapping.FromClauseText))
                            {
                                staticAnalysisText.AppendLine($"      <from-clause>{mapping.FromClauseText}</from-clause>");
                            }
                            if (mapping.SelfReferencedColumns.Count > 0)
                            {
                                staticAnalysisText.AppendLine($"      <self-referenced-columns>{string.Join(", ", mapping.SelfReferencedColumns)}</self-referenced-columns>");
                            }
                            staticAnalysisText.AppendLine("    </update-target>");
                        }
                        staticAnalysisText.AppendLine();
                    }
                    staticAnalysisText.AppendLine($"  * DELETE 대상 테이블: {(spDef.StaticAnalysis.DeleteTables.Count > 0 ? string.Join(", ", spDef.StaticAnalysis.DeleteTables) : "없음")}");
                    
                    if (spDef.StaticAnalysis.CreatedTempTables.Count > 0)
                    {
                        staticAnalysisText.AppendLine($"- 식별된 생성/사용 임시 테이블: {string.Join(", ", spDef.StaticAnalysis.CreatedTempTables)}");
                    }
                    else
                    {
                        staticAnalysisText.AppendLine("- 식별된 생성/사용 임시 테이블: 없음 (프로시저 내부에서 임시 테이블을 생성하거나 사용하지 않습니다. 이 사실을 ## CRUD 분석 섹션 등에 명시적으로 기재해 주십시오.)");
                    }
                    
                    if (spDef.StaticAnalysis.LinkedServerReferences.Count > 0)
                    {
                        staticAnalysisText.AppendLine($"- 식별된 Linked Server 원격 참조 목록: {string.Join(", ", spDef.StaticAnalysis.LinkedServerReferences)}");
                    }
                    else
                    {
                        staticAnalysisText.AppendLine("- 식별된 Linked Server 원격 참조 목록: 없음 (프로시저 내부에서 Linked Server 원격 참조를 사용하지 않습니다. 만약 다른 데이터베이스의 테이블을 3부 식별자(Database.Schema.Table) 형식으로 참조한다면, 이는 Linked Server가 아닌 동일 서버 인스턴스 내 크로스 데이터베이스(Cross-Database) 참조이므로 CRUD 분석 표 및 개요에 Linked Server가 아님을 사실 기반으로 정확하게 구분하여 설명하십시오.)");
                    }

                    // UPDATE 헤딩 원문 병기(위)만으로는 UPDATE 문이 없는 SP(예: INSERT 전용)에
                    // 닿지 않는다. 이 목록은 정적 분석 섹션 전체에 실리므로 모든 SP에
                    // 미치고, 비어 있을 때도 명시적으로 "없음"을 적어 규칙 6(원문 표기는
                    // <sp-source-ddl>만 근거로 삼으라)이 실제로 기댈 근거를 만든다.
                    if (spDef.StaticAnalysis.ThreePartObjectReferences.Count > 0)
                    {
                        staticAnalysisText.AppendLine($"- 원본이 3부 이상으로 표기한 오브젝트 참조(테이블/함수) 원문 목록: {string.Join(", ", spDef.StaticAnalysis.ThreePartObjectReferences)}");
                    }
                    else
                    {
                        staticAnalysisText.AppendLine("- 원본이 3부 이상으로 표기한 오브젝트 참조(테이블/함수) 원문 목록: 없음 (원본에 3부 식별자 또는 크로스 데이터베이스 참조가 존재하지 않습니다. 3부 식별자 기반 크로스 데이터베이스 참조라고 단언하지 마십시오.)");
                    }

                    if (spDef.StaticAnalysis.ReferencedFunctions.Count > 0)
                    {
                        staticAnalysisText.AppendLine($"- 식별된 UDF 사용자 정의 함수 호출 목록: {string.Join(", ", spDef.StaticAnalysis.ReferencedFunctions)}");
                    }
                    else
                    {
                        staticAnalysisText.AppendLine("- 식별된 UDF 사용자 정의 함수 호출 목록: 없음 (프로시저 내부에서 UDF를 호출하지 않습니다. 이 사실을 ## CRUD 분석 섹션 등에 명시적으로 기재해 주십시오.)");
                    }
                    
                    if (spDef.StaticAnalysis.ControlFlowSummary.Count > 0)
                    {
                        staticAnalysisText.AppendLine("- 식별된 제어 흐름 구조 요약 (IF/WHILE):");
                        foreach (var cf in spDef.StaticAnalysis.ControlFlowSummary)
                        {
                            staticAnalysisText.AppendLine($"  * {cf}");
                        }
                    }
                    if (spDef.StaticAnalysis.ReferencedColumnsPerTable != null && spDef.StaticAnalysis.ReferencedColumnsPerTable.Count > 0)
                    {
                        staticAnalysisText.AppendLine("- 식별된 테이블별 실제 쿼리 참조 컬럼 목록 (진실의 원천 - 이 컬럼들을 CRUD 및 파라미터 매핑에 반드시 축약 없이 기술하십시오):");
                        foreach (var kvp in spDef.StaticAnalysis.ReferencedColumnsPerTable)
                        {
                            staticAnalysisText.AppendLine($"  * 테이블: {kvp.Key} -> 참조 컬럼: {string.Join(", ", kvp.Value)}");
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(spDef.StaticAnalysis.ParserWarningMessage))
                {
                    staticAnalysisText.AppendLine("[Stored Procedure AST 정적 분석 정보 (AST Analysis Guidance)]");
                    staticAnalysisText.AppendLine($"- 정적 구문 분석 실패/경고:\n{spDef.StaticAnalysis.ParserWarningMessage}");
                }
            }

            return (dependenciesText.ToString(), tableSchemasText.ToString(), referenceDdlsText.ToString(), staticAnalysisText.ToString());
        }

        private (string SystemPrompt, string UserPrompt) BuildSpecificationPrompts(SpDefinition spDef, string userInstructions, string? feedbackLog)
        {
            if (spDef.ObjectType == CodeObjectType.Function)
            {
                return BuildFunctionSpecificationPrompts(spDef, userInstructions, feedbackLog);
            }

            // 동적 Pruning 조건 체크
            bool hasUdf = spDef.StaticAnalysis?.ReferencedFunctions?.Count > 0;
            bool hasLinkedServers = spDef.StaticAnalysis?.LinkedServerReferences?.Count > 0;
            bool hasDynamicSql = spDef.DdlText.Contains("EXEC", StringComparison.OrdinalIgnoreCase) || 
                                 spDef.DdlText.Contains("EXECUTE", StringComparison.OrdinalIgnoreCase) || 
                                 spDef.DdlText.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase);
            
            bool hasMissingDescription = false;
            if (spDef.Dependencies != null)
            {
                foreach (var dep in spDef.Dependencies)
                {
                    if (dep.Columns != null)
                    {
                        foreach (var col in dep.Columns)
                        {
                            if (col.IsDescriptionMissing)
                            {
                                hasMissingDescription = true;
                                break;
                            }
                        }
                    }
                    if (hasMissingDescription) break;
                }
            }

            var rules = new List<string>
            {
                "You are an expert SQL Server Stored Procedure analyzer. Analyze the provided stored procedure metadata and write a comprehensive reverse-engineered specification in Markdown.",
                "",
                "[Essential Rules]",
                "1. Analyze the target SP along with the provided schema definitions, dependencies, and referenced UDF/SP source codes. Write a precise report in Korean.",
                "2. Map the variables/parameters to the database columns affected (SELECT/INSERT/UPDATE/DELETE) and detail their relationships."
            };

            int ruleIndex = 3;
            if (hasUdf)
            {
                // [왜 "분석하라"에서 "서술하지 마라"로 뒤집었는가 - 2026-08-20 축 A 교차 대조]
                // 옛 지시는 "UDF 소스가 있으면 그 로직을 분석하라"였고, 함수 DDL 전문이
                // 실제로 프롬프트에 들어갔다. 그런데도 EXCEPTION_PROC의 UDF 요약 표
                // 10행 중 8행이 결함이었고 🔴이 5건이었다. 같은 함수를 SP마다 다르게
                // 썼다 - UF_GET_INCVTAXRATE를 다섯 SP가 "0이면 0.1…"부터 "계산에
                // 사용합니다"까지 제각각으로 서술했다. 요약을 정확하게 만드는 대신
                // 요약 자체를 없앤다.
                rules.Add($"{ruleIndex++}. Do NOT describe what any referenced User Defined Function (UDF) does - do NOT describe any function's behaviour: return value, branches, filters, defaults, rounding. That belongs only in that function's own Spec.md, which the machine-derived 참조 함수 table links to. State where each function is called and with which arguments; say nothing about what it returns.");
            }

            rules.Add($"{ruleIndex++}. Include a Mermaid Flowchart diagram visualizing the business logic flow: ");
            rules.Add("   - Always wrap the entire text of node labels in double quotes to prevent syntax errors (e.g., id1[\"Text (Extra)\"] --> id2[\"Return Result\"]).");
            rules.Add("   - Node IDs must be unique alphanumeric characters (e.g., Node1, Node2). Do not use parentheses alone or Mermaid reserved keywords (graph, flowchart, subgraph, end) as node IDs.");
            rules.Add("   - Node IDs must be strictly identical between definition and reference. Do not mix formats like using NPRECHECK in one place and N_PRECHECK (with underscore) in another. Keep node IDs simple, using only uppercase alphanumeric characters (e.g., START, PRECHECK, BEGINTRAN, DELPG, INSPG, FAIL9, COMMIT).");
            rules.Add("   - When writing labels on arrows (e.g., -->|Label|), NEVER use double quotes, parentheses, or special characters inside the label.");
            rules.Add("   - Node labels containing '@' (e.g. '@@ERROR', '@po_intRetVal') MUST be wrapped in double quotes. Write the identifier exactly as it appears in the source - never paraphrase or spell out '@' (writing 'at ERROR' for '@@ERROR' is a defect).");

            if (hasDynamicSql)
            {
                rules.Add($"{ruleIndex++}. If dynamic SQL (EXEC, EXECUTE, sp_executesql) is present, identify its business purpose and target tables, and reflect them in the CRUD analysis and logic summary.");
            }
            if (hasLinkedServers)
            {
                rules.Add($"{ruleIndex++}. If Linked Server references (4-part identifiers: Server.Database.Schema.Table) are found, clarify the external dependencies and integration purposes.");
            }

            rules.Add($"{ruleIndex++}. Do not wrap the entire output in a markdown code block (```markdown ... ```). Output the markdown directly.");
            rules.Add($"{ruleIndex++}. The specification H2 headers must strictly use these exact Korean titles: `## 개요`, `## 파라미터 목록`, `## CRUD 분석`, `## 로직 흐름 요약`, `## 비즈니스 흐름 시각화`. Do not change or add numbering to these headers.");
            rules.Add($"{ruleIndex++}. In the `## 개요` section, you MUST state the exact procedure name provided in the metadata. Do NOT misspell it (e.g., watch out for missing letters like 'E').");
            rules.Add($"{ruleIndex++}. In ## 파라미터 목록 and throughout the document, all table headers and column names must use correct and pure Korean (e.g., '매개변수 명칭', '파라미터명', '데이터 타입', 'Null 여부'). Do NOT mix foreign characters or Chinese/Japanese characters (e.g., do NOT use '매개参数' or '매개変数').");
            rules.Add($"{ruleIndex++}. In ## CRUD 분석, state all physical tables affected by SELECT, INSERT, UPDATE, DELETE in a clear Markdown Table format. Do NOT use bullet points or lists. You must separate SELECT tables, INSERT tables, UPDATE tables, and DELETE tables into their own respective sub-sections with separate Markdown tables. Do not mix them in a single table.");
            if (spDef.StaticAnalysis?.AstInsertMappings != null && spDef.StaticAnalysis.AstInsertMappings.Count > 0)
            {
                rules.Add($"{ruleIndex++}. [CRITICAL CRUD TEMPLATE (Fill-in-the-blanks)] For the INSERT tables in the `## CRUD 분석` section, you MUST use the following pre-filled markdown table template exactly as provided. Do NOT skip any rows, do NOT use '...', and do NOT alter the `컬럼명` names. Your ONLY job is to fill in the `원천 데이터 (Mapping)` and `설명` columns for each row based on the AST Analysis Guidance:");
                foreach (var mapping in spDef.StaticAnalysis.AstInsertMappings)
                {
                    rules.Add($"   ### INSERT 대상 테이블: {mapping.TargetTable}");
                    rules.Add($"   | 테이블명 | 컬럼명 | 원천 데이터 (Mapping) | 설명 |");
                    rules.Add($"   | :--- | :--- | :--- | :--- |");
                    if (mapping.TargetColumns.Count > 0)
                    {
                        foreach (var col in mapping.TargetColumns)
                        {
                            rules.Add($"   | {mapping.TargetTable} | {col} | (FILL_SOURCE_DATA_HERE) | (FILL_DESCRIPTION_HERE) |");
                        }
                    }
                    else 
                    {
                        rules.Add($"   | {mapping.TargetTable} | (COLUMN_NAME) | (FILL_SOURCE_DATA_HERE) | (FILL_DESCRIPTION_HERE) |");
                    }
                    rules.Add("");
                }
            }
            var updateMappings = spDef.StaticAnalysis?.AstUpdateMappings;
            if (updateMappings != null && updateMappings.Count > 0)
            {
                rules.Add($"{ruleIndex++}. {UpdateMappingTemplateIntroText}");
                rules.AddRange(BuildUpdateMappingTemplateLines(updateMappings));
            }

            // A1 결함 넷 중 셋(COMM_UPD 문장 7, EXCEPTION_PROC 실행순서 18·4)의 공통
            // 구조는 "Spec이 범위를 단언하는데 원본에는 그 필터가 없다"이다. 부재를
            // 서술했는지는 자연어 판정이라 앵커가 없으므로, 서술을 요구하지 않고 이
            // 표를 강제한다(설계 3.1). BuildDmlScopeTableLines 문서 참고.
            // SpecExpectations.From()의 DmlScopeFacts와 같은 규칙(SpecExpectations.ResolveDateParameter)
            // 을 써야 한다 - 두 곳이 다르게 고르면 이 표와 L1의 기대가 갈라진다.
            var dateParameter = SpecExpectations.ResolveDateParameter(spDef.StaticAnalysis);
            var dmlScopeFacts = DmlScopeExtractor.Extract(spDef.DdlText, dateParameter);
            if (dmlScopeFacts.Count > 0)
            {
                rules.Add($"{ruleIndex++}. {DmlScopeTableIntroText}");
                rules.AddRange(BuildDmlScopeTableLines(dmlScopeFacts, dateParameter));
            }

            // 집합의 크기와 원소는 컬럼 이름으로 추측할 수 없다(설계 §2) - DmlScopeFacts와
            // 같은 이유로 표를 강제한다. BuildSetPredicateTableLines 문서 참고.
            var setPredicates = DmlScopeExtractor.ExtractSetPredicates(spDef.DdlText);
            // [소프트 페일 전파 방지] Extract와 ExtractSetPredicates는 같은 DDL을 각자
            // 독립된 try/catch로 파싱한다(DmlScopeExtractor 문서 참고 - AGENTS.md 범주 2
            // 소프트 페일). DmlScopeVisitor가 SetPredicateVisitor보다 더 많은 일을 하므로
            // (JoinConditionCollector, TextOf(target) 등) 이론상 Extract만 실패해
            // dmlScopeFacts는 비고 setPredicates는 채워질 수 있다. 그 경우는 Extract만
            // 소프트 페일했다는 뜻이고, 두 재료는 같은 DDL에서 나오므로 한쪽이 실패하면
            // 재료 전체를 미덥지 않다고 봐야 한다 - 그래서 dmlScopeFacts가 비어 있으면
            // setPredicates가 채워져 있어도 집합 술어 표를 렌더하지 않는다.
            // DmlScopeExtractor.Extract가 그 소프트 페일을 로그로 남기므로 진단 흔적은
            // 남는다(BuildSetPredicateTableLines는 더 이상 dmlScopeFacts를 조회하지
            // 않는다 - FIX ROUND 3 이후 SetPredicateFact.StatementOrdinal을 직접 쓴다).
            if (setPredicates.Count > 0 && dmlScopeFacts.Count > 0)
            {
                rules.AddRange(BuildSetPredicateTableLines(setPredicates));
            }

            // 참조 함수 표도 같은 이유로 기계가 채운다 - 2026-08-20 축 A 교차 대조에서
            // 이 자리를 LLM이 쓰던 시절 10행 중 8행이 결함이었다(🔴 5건).
            var knownFunctionNames = (spDef.Dependencies ?? new List<DependencyInfo>())
                .Where(d => SqlObjectTypeClassifier.ResolveCodeObjectType(d.Type) == CodeObjectType.Function)
                .Select(d => d.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            var functionCalls = DmlScopeExtractor.ExtractFunctionCalls(spDef.DdlText, knownFunctionNames);
            if (functionCalls.Count > 0)
            {
                rules.AddRange(BuildReferencedFunctionTableLines(functionCalls, spDef));
            }

            // 잠금 힌트 표도 같은 이유로 기계가 채운다 - 2026-08-21 축 A 감사:
            // INS_EXTRA4PLCARD는 같은 TPGProperty가 별칭 P·Y에는 NOLOCK이 붙고 PG에는
            // 안 붙는데 명세서가 "5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉갰다.
            // 부재 서술은 자연어 판정이라 앵커가 없으므로 표를 강제한다.
            var lockHints = DmlScopeExtractor.ExtractLockHints(spDef.DdlText);
            if (lockHints.Count > 0)
            {
                rules.AddRange(BuildLockHintTableLines(lockHints));
            }

            // 이 갈래(SP 전체 명세서)는 위 "The specification H2 headers must strictly
            // use these exact Korean titles" 규칙이 `## CRUD 분석`·`## 로직 흐름 요약`
            // 둘 다 필수 H2로 요구한다 - 두 표 모두 표 그대로 준다.
            rules.AddRange(BuildMachineFactBlockLines(
                spDef,
                executionSemanticsPresentation: MachineFactPresentation.Table,
                caseBranchPresentation: MachineFactPresentation.Table,
                uncoveredNoticePresentation: MachineFactPresentation.Table,
                localVariablePresentation: MachineFactPresentation.Table));

            // 축 A 🔴(EXCEPTION_PROC): SET 우변이 X.PGCOMM에서 멈추면 그 정의(프로모션
            // 원가 기준금액 IIF 분기)가 소실된다. DmlScopeFacts와 같은 이유로 표를
            // 강제한다 - 부재 서술은 자연어 판정이라 앵커가 없다.
            var derivedColumns = DerivedTableColumnExtractor.Extract(spDef.DdlText);
            if (derivedColumns.Count > 0)
            {
                rules.Add($"{ruleIndex++}. {DerivedTableIntroText}");
                rules.AddRange(BuildDerivedTableColumnLines(derivedColumns));
            }

            rules.Add($"{ruleIndex++}. Do not append any conversational filler, polite greetings, or unrelated explanations at the end of the document. Terminate the output immediately after the required sections.");
            rules.Add($"{ruleIndex++}. Do not guess the meaning of status values or business codes (e.g., OutState) unless explicitly defined in metadata. Describe them factually as defined in code (e.g., 'when OutState is 1 or 5').");
            rules.Add($"{ruleIndex++}. If the return value or output parameter is not explicitly assigned, describe the calling responsibility or prerequisites.");
            // Critic이 ScoreInterface에서 이 항목을 채점한다(아래 채점 기준 3번).
            // 생성 규칙에 없으면 모델은 요구받지 않은 것을 쓰지 않고, 그대로 감점된다.
            rules.Add($"{ruleIndex++}. Explicitly state whether this procedure returns a result set (Rowset) to the caller. State it even when no result set is returned.");
            // 실측(CANCEL_INS): 별칭 B의 참조 컬럼 목록에 삽입 대상 컬럼과 상수로
            // 채워지는 컬럼, 그리고 다른 별칭에서 오는 값이 섞였다. 같은 문서의
            // INSERT 매핑 표는 정확했으므로 문서가 스스로와 어긋났다.
            rules.Add($"{ruleIndex++}. When you list the referenced columns of a table or alias, include only the columns that the query actually reads from that alias. Do not list insert-target columns, columns filled by constants, columns read through a different alias, or columns that merely exist in the schema.");

            rules.Add($"{ruleIndex++}. [CRITICAL ANTI-SHORTCUT RULE] NEVER use abbreviations, ellipses (...), or phrases like '이하 생략', '기타', 'etc'. You MUST map EVERY SINGLE COLUMN present in the DDL to the markdown table row by row, even if there are 100 columns. Failure to write every column will result in a fatal system crash and your output will be rejected. Do NOT use `dbo.TS[] (이하 생략 가능하나 매핑은 완벽히 수행됨)` or similar shortcut phrases.");
            rules.Add($"{ruleIndex++}. [CRITICAL: 비즈니스 로직 원형 보존 원칙 - 다중 소스 결합 금지 해제] 원본 SQL이 `UNION`, `UNION ALL`, `JOIN`을 통해 여러 테이블에서 데이터를 수집한다면, 의사코드 및 설명에서도 모든 소스 테이블과 분모/분자 집계 수식(SUM 등)을 절대 생략하지 마십시오. (단일 테이블로 단순화하는 것은 치명적 결함으로 간주됩니다.)");
            rules.Add($"{ruleIndex++}. [CRITICAL: 비즈니스 로직 원형 보존 원칙 - 청크 분할 시 조건 보존] 대상 테이블에 대용량 청킹(예: `ID BETWEEN @start AND @end`) 처리가 필요할 때, 원본의 WHERE 조건(자기조인, 커서 필터링, 상태값 검사 등)을 삭제하지 말고 청크 조건과 반드시 `AND` 구문으로 결합해야 합니다. 원본 필터를 누락하고 단순 `BETWEEN`만 남기는 것은 치명적 데이터 유실로 간주됩니다.");
            rules.Add($"{ruleIndex++}. If a column description contains the exact tag `[AI 추론 보완: Schema.Table.Column - Description]`, you MUST output this tag exactly as is in the description column of the Markdown tables. Do NOT alter or translate this tag, and do not let it break the table format.");
            rules.Add($"{ruleIndex++}. Do not arbitrarily assume columns/parameters are 'NOT NULL' unless defined in the DDL.");
            rules.Add($"{ruleIndex++}. If `WITH(NOLOCK)` or `NOLOCK` hints are used, analyze their transaction isolation implications (dirty read risk, data consistency impact) in the exception/constraint section.");
            rules.Add($"{ruleIndex++}. Prevent logical hallucinations when translating complex filters (e.g., NOT IN combined with ISNULL). Describe them factually.");

            // [Anti-Hallucination Constraints]
            rules.Add($"{ruleIndex++}. NEVER include columns in the CRUD table that do not exist in the provided schema metadata. If a column appears in the DDL but is missing from the schema, do not guess it as a normal column; mark it as a schema mismatch.");
            rules.Add($"{ruleIndex++}. Table names in the static analysis metadata are PARSER-NORMALIZED three-part names, not the source's own notation. When you describe how many parts the source identifier has (one-part, two-part, three-part, cross-database, Linked Server), base it ONLY on <sp-source-ddl>. Do not claim a cross-database or three-part reference that does not appear there.");
            rules.Add($"{ruleIndex++}. DO NOT invent arbitrary return codes (e.g., assuming -1, -2, etc. sequentially) if the RETURN statement in the DDL does not specify literal values. Map them factually (e.g., 'Returns error code on failure (actual value not specified in code)').");

            // [Output Language Requirement]
            rules.Add("");
            rules.Add("[Output Language Requirement]");
            rules.Add("- You MUST write the final markdown specification in Korean.");
            rules.Add("- Ensure natural Korean settlement terminology is used (e.g., '정산일' for settlement date, '수수료율' for commission rate, '임시 테이블' for temp table).");

            var systemPrompt = string.Join("\n", rules);
            systemPrompt += $"\n\n[USER INSTRUCTIONS]\n{userInstructions}";
            
            var (dependenciesText, tableSchemasText, referenceDdlsText, staticAnalysisText) = BuildSpMetadataTexts(spDef);

            // DDL 내 에러 반환코드 스캔 및 추출
            var errorAssignments = new List<string>();
            try
            {
                // [빈 줄을 버리지 않는 이유 - 2026-08-20 실측] 예전엔 RemoveEmptyEntries로
                // 잘라 세어, 빈 줄 개수만큼 번호가 밀린 채 프롬프트에 나갔다
                // (STAT_PGCOLLECT_INS: 실제 27·116 → 알린 값 20·104, 그 SP의 빈 줄이 14개).
                // LLM은 받은 번호를 충실히 옮기므로 명세서 앵커가 원본과 어긋났다.
                // 줄 번호를 매기는 스캔은 원본의 줄을 하나도 버리면 안 된다.
                var ddlLines = spDef.DdlText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                int lineNum = 1;
                foreach (var line in ddlLines)
                {
                    var trimmed = line.Trim();
                    // 에러 코드 대입이나 RETURN 음수 구문 검색
                    if (trimmed.Contains("@po_intRetVal", StringComparison.OrdinalIgnoreCase) && 
                        (trimmed.Contains("-") || trimmed.Contains("=")) && 
                        !trimmed.StartsWith("--"))
                    {
                        errorAssignments.Add($"Line {lineNum}: {trimmed}");
                    }
                    else if (trimmed.StartsWith("RETURN", StringComparison.OrdinalIgnoreCase) && 
                             trimmed.Contains("-") && 
                             !trimmed.StartsWith("--"))
                    {
                        errorAssignments.Add($"Line {lineNum}: {trimmed}");
                    }
                    lineNum++;
                }
            }
            catch { }

            var checklistSb = new StringBuilder();
            checklistSb.AppendLine();

            if (errorAssignments.Count > 0)
            {
                checklistSb.AppendLine("💡 [원본 DDL 소스코드 내 에러 반환 코드 감지 정보]");
                foreach (var err in errorAssignments)
                {
                    checklistSb.AppendLine($"  * {err}");
                }
                checklistSb.AppendLine("  (위 에러 코드들이 발생하는 제어 흐름 위치와 음수 반환값들의 매핑 정보를 다이어그램 및 로직 흐름 요약에 오차 없이 정확히 기술했는지 검증하십시오.)");
                checklistSb.AppendLine();
            }

            checklistSb.AppendLine("🎯 [최종 작성 전 필수 검증 체크리스트]");
            
            if (spDef.StaticAnalysis == null || spDef.StaticAnalysis.CreatedTempTables.Count == 0)
            {
                checklistSb.AppendLine("- [ ] ## CRUD 분석 섹션 하단에 '임시 테이블 사용 여부: 임시 테이블을 생성하거나 사용하지 않습니다.'를 명시적으로 기재하셨습니까?");
            }
            else
            {
                checklistSb.AppendLine($"- [ ] ## CRUD 분석 섹션에 생성/사용된 임시 테이블({string.Join(", ", spDef.StaticAnalysis.CreatedTempTables)})의 정의와 활용 목적을 기재하셨습니까?");
            }

            if (spDef.StaticAnalysis == null || spDef.StaticAnalysis.ReferencedFunctions.Count == 0)
            {
                checklistSb.AppendLine("- [ ] ## CRUD 분석 섹션 하단에 '사용자 정의 함수(UDF) 호출 여부: UDF 사용자 정의 함수를 호출하지 않습니다.'를 명시적으로 기재하셨습니까?");
            }
            else
            {
                checklistSb.AppendLine($"- [ ] ## CRUD 분석 섹션에 호출되는 UDF({string.Join(", ", spDef.StaticAnalysis.ReferencedFunctions)})의 호출 위치와 인자를 명확히 기재하셨습니까? (동작·반환값 서술은 금지됩니다 - 해당 함수의 Spec.md가 단일 진실의 원천입니다.)");
            }

            if (spDef.StaticAnalysis == null || spDef.StaticAnalysis.LinkedServerReferences.Count == 0)
            {
                checklistSb.AppendLine("- [ ] ## CRUD 분석 섹션 또는 ## 개요에 'Linked Server 원격 참조 여부: Linked Server를 통한 원격 참조를 사용하지 않습니다.'를 명시적으로 기재하셨습니까?");
            }

            if (spDef.StaticAnalysis != null && spDef.StaticAnalysis.SelectTables.Count > 0)
            {
                checklistSb.AppendLine($"- [ ] ## CRUD 분석 표에 SELECT 대상인 원천 테이블({string.Join(", ", spDef.StaticAnalysis.SelectTables)})이 각각 누락이나 '외 다수' 축약 없이 독립적인 행으로 기술되고, 참조 컬럼과 필터 조건이 정확히 작성되었습니까?");
            }

            if (spDef.StaticAnalysis != null && spDef.StaticAnalysis.InsertTables.Count > 0)
            {
                checklistSb.AppendLine($"- [ ] ## CRUD 분석 표에 INSERT 대상 테이블({string.Join(", ", spDef.StaticAnalysis.InsertTables)})의 각 컬럼별 원천 데이터 매핑 정보(상수값, 변수, ISNULL 변환 등)가 1:1 대조 표로 완전하게 기술되었습니까?");
            }

            checklistSb.AppendLine("- [ ] Mermaid 흐름도 내부 노드의 한글 텍스트를 큰따옴표 한 쌍으로 감싸고 문법적 예약어 충돌이 없도록 작성하셨습니까?");
            checklistSb.AppendLine("- [ ] SP 내부의 에러 처리 분기(예: DELETE/INSERT 실패 시 각각 @@ERROR 조건 분기 및 음수 반환 코드)와 트랜잭션 롤백 동작이 Mermaid 다이어그램 및 본문 설명에 충실히 반영되었습니까?");
            checklistSb.AppendLine("- [ ] 호출자에게 반환되는 결과셋(Rowset)의 유무를 명시하셨습니까? (반환하지 않는 경우에도 그 사실을 적어야 합니다.)");

            // 원본 DDL 전문은 이미 <sp-source-ddl>로 프롬프트에 들어가므로, 주석 결함은
            // 정보 부족이 아니라 요구 부재였다. SourceCommentExtractor 하나가 이 체크리스트와
            // MechanicalValidator.CheckSourceComments(L1)의 대조 기준을 함께 낸다 - 프롬프트
            // 요구와 대조 기준이 서로 다른 판단을 하지 않도록 단일 권위로 둔다.
            var sourceComments = SourceCommentExtractor.Extract(spDef.DdlText);
            if (sourceComments.Count > 0)
            {
                checklistSb.AppendLine(
                    $"- [ ] 원본 DDL의 주석 {sourceComments.Count}건(비실행 조건·코드 범례·헤더 선언)을 "
                    + "본문에 기록하셨습니까? 조건식 원문·도입 일자·사유를 그대로 옮기고, "
                    + "\"실행되지 않습니다\" 한 문장으로 대신하지 마십시오. 대조 대상:");
                foreach (var block in sourceComments)
                {
                    checklistSb.AppendLine($"      * (라인 {block.Line}) {block.Text}");
                }
            }

            // 세 번째 인자의 의미는 이 SP의 사정이 아니라 T-SQL 명세다 - 0이면 반올림,
            // 0이 아니면 절사. RoundingSemanticsExtractor 하나가 이 체크리스트와
            // MechanicalValidator.CheckRoundingSemantics(L1)의 대조 기준을 함께 낸다.
            var roundingCalls = RoundingSemanticsExtractor.Extract(spDef.DdlText);
            if (roundingCalls.Count > 0)
            {
                checklistSb.AppendLine(
                    $"- [ ] 원본의 3인자 ROUND 호출 {roundingCalls.Count}건에 대해 "
                    + $"{RoundingSemanticsExtractor.SemanticsSentence} "
                    + "이 값 매핑을 명세서에 기술하셨습니까? \"반올림 또는 절사\"처럼 "
                    + "어느 값이 어느 동작인지 흐리게 적지 마십시오.");
            }

            // SET NOCOUNT ON이 AS 직후 BEGIN TRAN 앞에 있는데 Spec 전체에 언급이
            // 없었던 것이 이 항목의 근거다(Util_Settle_Summary 실측). SessionOptionsExtractor
            // 하나가 이 체크리스트와 MechanicalValidator.CheckSessionOptions(L1)의 대조
            // 기준을 함께 낸다.
            var sessionOptions = SessionOptionsExtractor.Extract(spDef.DdlText);
            if (sessionOptions.Count > 0)
            {
                checklistSb.AppendLine(
                    $"- [ ] 프로시저 본문이 설정하는 세션 옵션({string.Join(", ", sessionOptions)})과 "
                    + "그것이 호출 계층에 미치는 영향을 기술하셨습니까?");
            }

            // Util_Settle_Summary 실측 - 헤더 주석이 내부 SP 호출을 NONE이라 선언하는데
            // 실제로는 EXEC가 둘 있었다. 명세서는 두 EXEC를 정확히 적으면서도 헤더가
            // 모순된다는 사실 자체는 빠뜨렸다. MechanicalValidator.CheckHeaderContractContradiction(L1)이
            // 대조하는 것은 "NONE 선언 + 실제 EXEC" 한 패턴뿐이지만, 이 체크리스트는
            // 헤더가 선언할 수 있는 다른 계약(반환값 규약 등)의 모순도 함께 상기시킨다 -
            // 헤더 주석이 있을 때만 의미가 있으므로 Header 종류가 있을 때만 낸다.
            if (sourceComments.Any(b => b.Kind == "Header"))
            {
                checklistSb.AppendLine(
                    "- [ ] 헤더 주석이 선언한 계약(반환값 규약, 내부 SP 호출 유무 등)이 "
                    + "실제 구현과 어긋나는 부분이 있다면, 그 모순 자체를 명세서에 "
                    + "기록하셨습니까? 구현만 옳게 적고 주석이 낡았다는 사실을 빠뜨리면 "
                    + "다음 사람이 같은 조사에 다시 들어갑니다.");
            }

            var userPrompt = $@"
<stored-procedure-context>
  <basic-info>
    <schema>{spDef.Schema}</schema>
    <name>{spDef.Name}</name>
  </basic-info>
  
  <dependencies>
{dependenciesText}  </dependencies>
  
  <referenced-table-schemas>
{tableSchemasText}  </referenced-table-schemas>
  
  <referenced-ddl-source-code>
{referenceDdlsText}  </referenced-ddl-source-code>
  
  <static-analysis-metadata>
{staticAnalysisText}  </static-analysis-metadata>
  
  <sp-source-ddl>
```sql
{spDef.DdlText}
```
  </sp-source-ddl>
</stored-procedure-context>

Based on the structured reference context above, reverse engineer the stored procedure and write a comprehensive markdown specification in Korean following the checklist below:
{checklistSb.ToString()}
";

            if (!string.IsNullOrEmpty(feedbackLog))
            {
                userPrompt += $"\n\n[이전 시도에 대한 검증 오류/수정 피드백 로그]:\n{feedbackLog}\n\n위 검토 및 수정 체크리스트의 모든 요건들을 전적으로 수용하여 명세서 내용을 정교하게 수정하고 오류를 바로잡아 다시 작성해 주십시오. 특히 이전 턴에서 정상적으로 분석되었던 다른 섹션이나 테이블 컬럼 목록이 이번 수정 과정에서 실수로 유실되거나 훼손되는 회귀 결함(Regression)이 절대 발생하지 않도록, 제공된 '진실의 원천' 메타데이터(참조 컬럼 목록 등)와 철저히 대조해 주십시오.";
            }

            return (systemPrompt, userPrompt);
        }

        /// <summary>
        /// 마크다운 표 셀 이스케이프의 별칭. 실제 구현은
        /// <see cref="MarkdownTableCellCodec.Escape"/>다 - 렌더(여기)와 대조
        /// (MechanicalValidator)가 같은 함수를 공유해야 이스케이프 왕복이 성립하므로,
        /// 어느 한쪽 클래스에 속한 메서드가 아니라 중립 헬퍼로 둔다(2026-08-21 최종
        /// 브랜치 리뷰 재라운드 Minor(설계) - MarkdownTableCellCodec 문서 참고).
        /// </summary>
        private static string EscapeTableCell(string expression) => MarkdownTableCellCodec.Escape(expression);

        /// <summary>
        /// UPDATE fill-in-the-blank 템플릿을 도입하는 규칙 문장. 번호 접두(`{ruleIndex}. `)만
        /// 호출부가 붙이고 문장 본문은 여기 한 곳에서만 관리한다 - 두 프롬프트 빌더에
        /// 복제되면 문구를 강화할 때 한쪽만 고쳐질 위험이 생긴다.
        /// </summary>
        private const string UpdateMappingTemplateIntroText =
            "[CRITICAL CRUD TEMPLATE (Fill-in-the-blanks)] For the UPDATE tables in the `## CRUD 분석` section, you MUST use the following pre-filled markdown table template exactly as provided. The `컬럼명` and `원천 표현식 (SET)` cells are already filled from the AST: do NOT alter, reorder, merge, or skip any row, and do NOT use '...'. Your ONLY job is to fill in the `설명` column for each row:";

        /// <summary>
        /// UPDATE 대상 테이블의 fill-in-the-blank 마크다운 템플릿 본문을 만든다.
        ///
        /// 헤딩 리터럴 `### UPDATE 대상 테이블:`은 MechanicalValidator.CheckUpdateMappings가
        /// 명세서 본문을 대조할 때 찾는 접두이자 L1(VerificationPipelineOrchestrator)이
        /// 지역 모델 경로에서도 강제하는 계약이다. BuildSpecificationPrompts(전체 명세서
        /// 1회 생성)와 BuildSpecSectionPrompts의 "CrudAnalysis" 분기(지역 모델의 최초
        /// 생성 경로)가 이 헬퍼를 공유해야 두 경로가 같은 헤딩을 내보낸다는 것이
        /// 코드로 보장된다 - 리터럴이 두 곳에 복제되면 한쪽만 고쳐질 위험이 생긴다.
        /// </summary>
        /// <summary>
        /// 프롬프트가 작성자(모델)에게 주는 지시문임을 못 박는 표지.
        ///
        /// [왜 필요한가] 아래 두 줄은 원래 한국어 2인칭 명령문("…추측하지 마십시오",
        /// "…명시적으로 기술하십시오")이었고, 그대로 베끼라고 지시한 UPDATE 매핑 표
        /// 블록 안에 섞여 있었다. 그래서 모델이 표와 함께 통째로 옮겨 적었다 -
        /// 2026-08-18 축 A 감사 실측: COMM_UPD 17곳, INS_EXTRA 5곳,
        /// INS_EXTRA4PLCARD 3곳. 특히 "`## CRUD 분석`에 기술하십시오"는 그 절 안에서
        /// 자기 자신을 가리켜 납품 문서가 미완성 초안처럼 읽혔다.
        ///
        /// 한국어 명세서 본문과 섞이지 않도록 지시문을 영어로 되돌리고 이 표지를
        /// 앞에 붙인다. MechanicalValidator.CheckPromptInstructionLeak이 이 표지를
        /// 명세서에서 찾아 유출을 기계로 막는다 - 규칙 하나에 검사 하나(설계 §0).
        /// </summary>
        internal const string PromptInstructionMarker = MechanicalValidator.PromptInstructionMarker;

        private static List<string> BuildUpdateMappingTemplateLines(IReadOnlyList<AstUpdateMapping> updateMappings)
        {
            var lines = new List<string>();
            foreach (var mapping in updateMappings)
            {
                var rawNotation = string.IsNullOrWhiteSpace(mapping.RawTargetText)
                    ? string.Empty
                    : $" · 원문 표기: {mapping.RawTargetText}";
                // 절 제목에 StatementOrdinal을 쓰지 않는다. 그 값은 <b>대상 테이블별</b>
                // 채번이라 같은 SP 안에서 리셋된다 - 대상 표기가 "TSettleMst"와
                // "dbo.TSettleMst"로 갈리면 카운터도 갈린다. 2026-08-18 축 A 감사 실측
                // (EXPECT_PROC): 라인 182와 245가 둘 다 "문장 1"이 되고, 같은 문서의
                // 본문·오류코드 매핑·UDF 표는 그것들을 "갱신 8"·"갱신 11"로 센다.
                // "갱신 8"을 찾아 절 제목 "문장 8"을 열면 라인 225(다른 UPDATE)가 나온다.
                // 파서가 대상 테이블과 무관하게 매긴 GlobalStatementOrdinal을 쓰고,
                // 본문이 쓰는 낱말과 같은 "갱신"으로 통일해 어휘 이원화까지 없앤다.
                // 목록 위치(i+1) 대신 파서 값을 쓰는 이유는, 호출부가 부분집합을
                // 넘기면 위치 기반 번호가 조용히 어긋나기 때문이다.
                lines.Add($"   ### UPDATE 대상 테이블: {mapping.TargetTable} (갱신 {mapping.GlobalStatementOrdinal} · 원본 DDL 라인 {mapping.SourceLine}{rawNotation})");
                lines.Add("   | 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |");
                lines.Add("   | :--- | :--- | :--- | :--- |");
                foreach (var assignment in mapping.Assignments)
                {
                    lines.Add($"   | {mapping.TargetTable} | {assignment.Column} | {EscapeTableCell(assignment.SourceExpression)} | (FILL_DESCRIPTION_HERE) |");
                }

                if (!string.IsNullOrEmpty(mapping.FromClauseText))
                {
                    lines.Add($"   {PromptInstructionMarker} This statement has a FROM clause: the update target is the aliased instance that appears in FROM. If the join matches several source rows to one target row, T-SQL does not define which value wins (non-deterministic). State that fact in Korean in the document body, and do NOT guess whether the join keys are unique.");
                }

                if (mapping.SelfReferencedColumns.Count > 0)
                {
                    lines.Add($"   {PromptInstructionMarker} These columns reference themselves on the SET right-hand side: {string.Join(", ", mapping.SelfReferencedColumns)}. SQL evaluates every right-hand side against the pre-update values simultaneously. State this fact in Korean under `## CRUD 분석`; sequential assignment during migration would change the result.");
                }

                lines.Add("");
            }

            return lines;
        }

        /// <summary>
        /// DML 범위 표를 도입하는 규칙 문장. UpdateMappingTemplateIntroText와 같은 이유로
        /// 두 프롬프트 빌더가 이 상수 하나를 공유한다 - 문구를 강화할 때 한쪽만 고쳐질
        /// 위험을 없앤다.
        ///
        /// [문장 칸에 `SELECT n`이, 대상·기준일 칸에 `—`가 실린 뒤 - 전체 브랜치 리뷰 I1]
        /// Task 4가 이 표에 독립 SELECT 행을 더하고 Task 7이 그 행의 대상·기준일 칸을
        /// `—`로 갈랐는데, 이 문구는 한 글자도 바뀌지 않아 그 두 값을 정의하지 않은 채
        /// "그대로 옮기라"고만 지시하고 있었다. 잠금 힌트 표는 정확히 같은 결함을
        /// <c>LockHintIntroText</c>에서 이미 고쳤고(범위 칸의 값 셋·`SELECT n`·`IF n`),
        /// 집합 술어 도입문도 `—` 설명을 받았다 - 셋 중 이 하나만 남아 있었다.
        /// 실측(PROC_ETC 재생성 표): 8행 중 6행이 `SELECT n`이고 그 6행의 대상·기준일
        /// 칸은 전부 `—`다. 표 제목이 "DML 범위"라 행의 다수가 DML이 아니라는 사실이
        /// 제목과 어긋나는데, 헤딩 리터럴은 L1이 대조하는 접두라 문구 쪽에서 가른다.
        /// 권위 있는 서술은 <c>DmlScopeFact.Operation</c>·<c>DmlScopeFact.Target</c>
        /// 문서에 있다 - 이 문구는 그것을 프롬프트 언어로 옮긴 것이므로, 그 문서가
        /// 바뀌면 여기도 바꾼다. `IF n`은 이 표에 실리지 않으므로(잠금 힌트 표와 다른
        /// 점 - Operation 문서 참고) 여기서 정의하지 않는다.
        /// </summary>
        private const string DmlScopeTableIntroText =
            "[CRITICAL SCOPE TABLE] The following table is MACHINE-DERIVED from the source DDL. " +
            "Copy it verbatim into `## CRUD 분석` under the exact heading shown, and make sure no " +
            "sentence in your document contradicts it. Do NOT change any cell. In particular: when a " +
            "row says the date parameter is NOT applied to the target, you must NOT write that the " +
            "statement is limited to the settlement date. The 문장 column names the statement the row " +
            "describes. Besides `INSERT n` / `UPDATE n` / `DELETE n` it can hold `SELECT n` - a " +
            "standalone read outside any DML (a variable assignment, a cursor source query, a function " +
            "body query). Such a row updates nothing, so describe it as a read; do not turn it into DML, " +
            "and do not assume every row is a write just because the heading says DML. Numbering runs " +
            "from 1 per statement kind. On those `SELECT n` rows the 대상 and 기준일 파라미터 적용 " +
            "columns hold `—`, which means NO such judgment exists for that row - there is no update " +
            "target to judge. `—` there is not `아니오`, not a negative finding, and not `unknown`: " +
            "never write that such a statement leaves the date parameter unapplied, and never name a " +
            "target for it.";

        /// <summary>
        /// DML 범위 표 본문을 만든다. 헤딩 리터럴 `DmlScopeExtractor.DmlScopeTableHeading`은
        /// Task 10의 L1(`MechanicalValidator`)이 명세서 본문을 대조할 때 찾는 접두다.
        /// BuildSpecificationPrompts와 BuildSpecSectionPrompts의 "CrudAnalysis" 분기(지역
        /// 모델의 최초 생성 경로)가 이 헬퍼를 공유해야 두 경로가 같은 표를 내보낸다는 것이
        /// 코드로 보장된다 - UPDATE fill-in 템플릿과 같은 이유다(BuildUpdateMappingTemplateLines
        /// 문서 참고).
        ///
        /// [헤더 문구 - "WHERE 술어 컬럼" 열] DmlScopeExtractor.TopLevelPredicateCollector는
        /// 최상위 WHERE의 컬럼을 한정자와 무관하게 전부 모은다(대상 테이블 소속인지
        /// 가리지 않는다) - 콤마로 나열한 옛 스타일 조인(FROM A, B WHERE A.X = B.Y)의
        /// 결합 조건이 ON절 없이 WHERE에 그대로 놓이기 때문에, 그 조인 컬럼도 이
        /// 칸에 함께 담겨야 하기 때문이다(DmlScopeExtractor 주석 참고). 그런데 예전
        /// 헤더 문구 "대상에 적용된 WHERE 술어 컬럼"은 이 칸의 모든 컬럼이 대상
        /// 범위를 좁힌다고 잘못 단언했다. 실측(COMM_UPD 223행 UPDATE): WHERE 전체가
        /// 콤마 조인 결합 조건뿐인데도(A.PLTID = B.PLTID 형태) 옛 헤더 아래
        /// "AYMD, YMD, CLIENTID, PGNAME, MALLID, PLTID, ID"가 그대로 찍혔다 - 이 표는
        /// "기계 확정 — 수정 금지"라 명세서가 이 거짓 단언을 그대로 베끼고, 그 결과
        /// 이 검사가 막으려는 바로 그 주장("정산 행은 YMD = @pi_strYMD로 좁혀진다")을
        /// 오히려 뒷받침하는 근거로 오독될 수 있었다.
        ///
        /// [필터링 대신 문구만 고친 이유] 대상 한정자로 필터링하는 안도 검토했으나
        /// 기각했다 - Task 9 리뷰가 이미 같은 모양의 트레이드오프를 실측으로
        /// 확인했다(JoinKeys 필터링에서 한정자 없는 비교를 "놓치는 쪽"으로 두기로
        /// 한 결정, DmlScopeExtractor의 HaveDifferentQualifiers 주석 참고): 여기서
        /// 대상 한정자만 남기도록 필터링하면, 대상 스스로의 컬럼이 조인을 거쳐
        /// 간접적으로(예: 서브쿼리 파생 값과의 비교) 범위를 좁히는 경우까지 함께
        /// 잘려 나가 "거짓 단언"을 "거짓 부재"로 바꿀 뿐이다 - 한쪽 오류를 반대쪽
        /// 오류로 맞바꾸는 셈이라 이득이 없다. 반면 헤더 문구를 "전체 컬럼, 대상
        /// 한정 아님"으로 바로잡으면 데이터 자체(어느 컬럼이 실렸는지)는 하나도
        /// 잃지 않으면서 거짓 단언만 없앤다 - `기준일 파라미터 적용` 칸이 이미
        /// "대상이 실제로 좁혀지는가"라는, 이 칸이 잘못 떠맡았던 질문에 대한 정답을
        /// 별도로 낸다.
        /// </summary>
        private static List<string> BuildDmlScopeTableLines(
            IReadOnlyList<DmlScopeFact> dmlScopeFacts, string dateParameter)
        {
            var lines = new List<string>
            {
                $"   {DmlScopeExtractor.DmlScopeTableHeading}",
                "   | 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼(조인 결합 포함 · 대상 한정 아님) | 기준일 파라미터 적용(최상위 WHERE 기준) | 조인 키 | GROUP BY | ORDER BY |",
                "   | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |"
            };

            // 채번은 BuildStatementOrdinals 하나가 유일한 출처다(문서 참고) - 이 표는
            // dmlScopeFacts를 자리 그대로 순회하므로 문장의 정체성으로 매긴 번호를
            // 쓴다.
            var ordinals = BuildStatementOrdinals(dmlScopeFacts);

            for (var i = 0; i < dmlScopeFacts.Count; i++)
            {
                var fact = dmlScopeFacts[i];

                // [독립 SELECT 행을 가르는 이유 - 2026-08-22 축 A 재감사 ③ Task 7]
                // 이 표의 대상 칸과 기준일 칸은 둘 다 "갱신 대상 범위"를 묻는데,
                // 독립 SELECT에는 갱신 대상이 없다(DmlScopeFact.Target·
                // DateParameterApplied 문서 - 그 재료는 표시 문자열을 담지 않고
                // 렌더러에 맡긴다). 가르지 않으면 대상은 빈 칸으로, 기준일은
                // "**아니오**(최상위 기준 · 하위 질의는 별도 확인)"로 나오는데
                // 후자는 아무것도 갱신하지 않는 문장에 대한 거짓 단언이다 -
                // 판정이 있었는데 부정으로 났다고 읽힌다.
                var isStandaloneSelect = fact.Operation == "SELECT";

                var predicates = fact.PredicateColumns.Count == 0
                    ? "(없음)" : string.Join(", ", fact.PredicateColumns);
                var joinKeys = fact.JoinKeys.Count == 0
                    ? "(없음)" : string.Join(", ", fact.JoinKeys);
                // 파라미터 자체가 없으면(dateParameter가 빈 문자열) 전부 false로만
                // 나오는 칸이 "적용 안 됨"이라는 거짓 신호로 읽힐 수 있다. 그래서
                // 이 경우엔 판정 자체가 없었다는 것을 명시한다.
                // "아니오"는 <b>최상위 WHERE에 없다</b>는 뜻이지 "이 문장이 기준일을
                // 전혀 쓰지 않는다"는 뜻이 아니다. 그 구분을 칸 안에 적어 둔다 -
                // 2026-08-18 축 A 감사 실측(COMM_UPD 문장 7, 223행): 파생 테이블 D의
                // 내부 WHERE가 A.YMD = @pi_strYMD로 후보를 당일로 한정하는데, 명세서가
                // 이 칸만 보고 "이 문장은 @pi_strYMD를 직접 조건으로 적용하지 않습니다"
                // 라고 단언해 🟠이 됐다. 표가 "기계 확정 — 수정 금지"라 모델이 이
                // 단언을 의심하지 않는다.
                //
                // 독립 SELECT 행은 이 세 갈래 어디에도 들지 않는다 - 판정 자체가
                // 없었으므로 "—"다(위 isStandaloneSelect 주석). DML 행의 문구는
                // 한 글자도 바뀌지 않는다.
                var applied = isStandaloneSelect
                    ? "—"
                    : dateParameter.Length == 0
                        ? "(기준일 파라미터 없음)"
                        : fact.DateParameterApplied ? "예" : "**아니오**(최상위 기준 · 하위 질의는 별도 확인)";

                var n = ordinals[i];

                // GROUP BY도 ORDER BY와 같은 규약을 쓴다(DmlScopeFact.GroupByColumns 문서 -
                // Task 8 제약 3): UPDATE·DELETE는 최상위 GROUP BY가 문법상 불가하므로 "—",
                // INSERT는 절이 없으면 "(없음)", 있으면 그룹화 키 목록. UP_Util_Settle_Summary·
                // UP_Util_Settle_Summary_AcqManual 실측 - GROUP BY 첫 키가 매핑 표의 설명
                // 칸에서만 언급되다 표에서 통째로 빠졌다.
                //
                // [SELECT 행도 같은 갈래에 넣는 이유 - Task 7] GROUP BY·ORDER BY는
                // "질의를 여는 문장"에서만 가능하고, 독립 SELECT가 바로 그 부류다
                // (DmlScopeFact.GroupByColumns 문서의 "—"/"(없음)" 규약 - INSERT·SELECT
                // 행의 빈 목록은 "(없음)", UPDATE·DELETE 행은 문법상 불가라 "—").
                // INSERT만 보던 동안 PROC_ETC:62 커서 원천의
                // `ORDER BY A.OutYMD, A.ClientID`는 Task 4가 추출까지 해 놓고도
                // 표에는 "—"로 찍혔다 - 추출됐으나 보이지 않는 상태였다.
                var opensAQuery = fact.Operation == "INSERT" || isStandaloneSelect;

                var groupBy = opensAQuery
                    ? (fact.GroupByColumns.Count == 0
                        ? "(없음)"
                        : EscapeTableCell(string.Join(", ", fact.GroupByColumns)))
                    : "—";

                // ORDER BY는 INSERT에만 문법상 가능하다(UPDATE·DELETE는 최상위 ORDER BY
                // 자체가 불가) - STAT_PGCOLLECT_INS:113 실측(2026-08-21 축 A 감사): 원본의
                // `ORDER BY INYMD, CLIENTID, PGNAME, MALLID`가 문서 어디에도 없었다.
                // 존재 여부가 아니라 목록을 싣는다 - 불리언이면 "있다"만 알고 무엇으로
                // 정렬하는지는 여전히 모른다(DmlScopeFact.OrderByExpressions 문서 참고).
                var orderBy = opensAQuery
                    ? (fact.OrderByExpressions.Count == 0
                        ? "(없음)"
                        : EscapeTableCell(string.Join(", ", fact.OrderByExpressions)))
                    : "—";

                var target = isStandaloneSelect ? "—" : EscapeTableCell(fact.Target);

                lines.Add(
                    $"   | {fact.Operation} {n} | {fact.Line} | {target} | "
                    + $"{EscapeTableCell(predicates)} | {applied} | {EscapeTableCell(joinKeys)} | {groupBy} | {orderBy} |");
            }

            lines.Add("");

            // [조건부 안내문 - 2026-08-23 9회차 ⚪ (A)] 이 문장은 모든 객체에 고정으로 붙어
            // 하위 질의가 없거나 기준일 파라미터 자체가 없는 4객체(COMM4PG4INTEREST·
            // PG_Client_CMRate_Ins·SUMMARY_ETC·AcqManual)에서 거짓이었다. `아니오` 행 중 실제로
            // 하위 질의·파생 테이블 안에서 기준일을 쓰는 문장이 있을 때만, 그 문장 번호와 함께 싣는다.
            // 조건이 DDL에서 나오므로 DDL이 바뀌면 캐시가 자동 무효화된다(캐시 15가 이 변경을 실었다).
            var hiddenDateStatements = new List<string>();
            for (var i = 0; i < dmlScopeFacts.Count; i++)
            {
                var f = dmlScopeFacts[i];
                if (!f.DateParameterApplied && f.DateParameterInNestedQuery
                    && !string.Equals(f.Operation, "SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    hiddenDateStatements.Add($"{f.Operation} {ordinals[i]}");
                }
            }
            if (hiddenDateStatements.Count > 0)
            {
                lines.Add("   > `기준일 파라미터 적용` 칸의 `아니오`는 **최상위 WHERE에 없다**는 뜻일 뿐이다. "
                    + $"하위 질의·파생 테이블 안에서 기준일을 쓰는 문장({string.Join(", ", hiddenDateStatements)})이 있으므로, "
                    + "이 칸을 근거로 \"이 문장은 기준일을 사용하지 않는다\"고 서술해서는 안 된다.");
                lines.Add("");
            }
            return lines;
        }

        /// <summary>
        /// DML 범위 표 문장에 "연산 종류별 · 목록 순서대로 1부터"라는 규칙으로
        /// 번호를 매긴다 - 문장의 정체성(dmlScopeFacts 안의 자리)으로 센다.
        ///
        /// [왜 헬퍼 하나로 뽑았는가 - FIX ROUND 1] BuildDmlScopeTableLines와
        /// BuildSetPredicateTableLines가 예전에는 채번을 각자 구현했다. 둘 다 "연산
        /// 종류별로 1부터"라는 같은 규칙을 말로는 지켰지만, 집합 술어 표 쪽은
        /// 자신이 실제로 받은 setPredicates 목록(집합 술어가 있는 문장만) 순서로
        /// 세었다 - 그래서 최상위가 서브쿼리 IN뿐이라 집합 사실을 하나도 못 내는
        /// 같은 연산의 문장이 두 문장 사이에 끼는 순간(실측:
        /// output/Objects/dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure/raw/object_definition.sql의
        /// 3번째 UPDATE, 98행) 그 뒤 모든 집합 술어 행의 번호가 DML 범위 표보다 하나씩
        /// 밀렸다.
        ///
        /// [왜 (Operation, Line)을 유일 키로 쓰지 않는가 - FIX ROUND 2] `e14a7a4`는 이
        /// 채번을 `Dictionary&lt;(Operation, Line), int&gt;` 하나로 통합했는데, 같은
        /// 물리 줄에 같은 연산 문장이 둘이면(예: 세미콜론으로 이어 쓴
        /// `UPDATE T1 ...; UPDATE T2 ...` 한 줄) 그 키가 충돌해 나중 문장이 앞 문장의
        /// 번호를 덮어썼다. 문장의 정체성(목록 안 자리)으로 세는 것으로 바꿔 고쳤다.
        ///
        /// [집합 술어 표는 이 헬퍼를 쓰지 않는다 - FIX ROUND 3] FIX ROUND 2는 집합
        /// 술어 표가 (연산, 라인) 키로 이 헬퍼(당시의 `FirstByKey`)에서 문장 번호를
        /// "빌려 쓰게" 했다. 그런데 같은 줄에 같은 연산 문장이 둘이고 <b>둘 다</b>
        /// 집합 술어를 가지면 그 키가 여전히 충돌해, 두 번째 문장의 집합 술어 행이
        /// 첫 문장의 번호를 빌려 쓰는 회귀가 났다(2026-08-18 재리뷰 실측 - DML 범위
        /// 표는 "UPDATE 1 dbo.T1 / UPDATE 2 dbo.T2"인데 옆 표의 dbo.T2 리터럴 행이
        /// "UPDATE 2"가 아니라 "UPDATE 1"로 찍혔다). 그 라운드의 주석은 "(연산,
        /// 라인)만으로는 원천적으로 구분할 수 없다"고 적었는데, 이는 틀렸다 - 그
        /// 정보는 SetPredicateFact의 <b>모양</b>에 대한 이야기였을 뿐, 실제로는
        /// SetPredicateVisitor가 DmlScopeVisitor와 같은 파싱 트리를 같은 네 오버라이드
        /// (DML 셋은 `Visit`, FROM이 있는 독립 SELECT는 `ExplicitVisit(SelectStatement)`)로
        /// 같은 순서로 방문하고 SELECT 판정도 `DmlScopeExtractor.HasFromClause` 하나를
        /// 공유하므로, 독자적으로 세어도 항상 같은 번호가 나온다. 독립 SELECT는
        /// 2026-08-23 축 A ③(b) Task 2가 셋에서 넷으로 넓힌 것이고, 그때도 두 방문자가
        /// 함께 넓어져 이 주장은 그대로 참이다
        /// (ExtractSetPredicates_StandaloneSelect_ShouldNotShiftDmlOrdinals가 못 박는다).
        /// 그래서 지금은 `SetPredicateFact.StatementOrdinal`이 그 번호를
        /// 직접 담고(문서 참고), 이 헬퍼는 DML 범위 표만 쓴다 - 사전 조회 자체를
        /// 없애 이 계열의 결함을 구조적으로 막는다.
        /// </summary>
        // [본문이 DmlScopeExtractor로 옮겨간 이유 - 2026-08-23 L1 문장 칸 대조]
        // MechanicalValidator.CheckDmlScopeTable이 같은 함수로 번호를 다시 매겨 문장 칸을
        // 대조한다. 채번 출처가 둘이면 렌더러와 L1이 어긋나 옳게 베낀 표가 거부되고,
        // 검증기는 조립기에 컴파일 의존하지 않는다는 관례(MarkdownTableCellCodec 문서)가
        // 있으므로 중립 자리에 하나만 두고 여기서는 위임한다.
        private static IReadOnlyList<int> BuildStatementOrdinals(IReadOnlyList<DmlScopeFact> dmlScopeFacts)
            => DmlScopeExtractor.BuildStatementOrdinals(dmlScopeFacts);

        /// <summary>
        /// 분해되지 않은 항의 원소 수·리터럴 목록 칸에 쓰는 표기.
        ///
        /// [왜 상수를 여기 또 두는가] 추출기 쪽 원본은
        /// <c>DmlScopeExtractor.TopLevelPredicateCollector.NotDecomposed</c>인데 그것이
        /// <b>private 중첩 클래스</b> 안에 있어 이 어셈블리의 다른 타입이 참조할 수 없다.
        /// 추출기 파일을 넓히지 않기로 한 이 작업의 범위 안에서는 같은 글자를 여기와
        /// <c>MechanicalValidator</c>에 각각 두는 수밖에 없다 - 셋이 갈리면 렌더된 칸을
        /// L1이 못 알아보므로, 고칠 때는 반드시 세 자리를 함께 고친다.
        /// </summary>
        private const string UndecomposedCell = "—";

        /// <summary>
        /// 기계 확정 집합 술어 표 본문을 만든다.
        ///
        /// [원소 수를 별도 칸으로 두는 이유] 2026-08-18 축 A 감사 실측: EXPECT_PROC의
        /// 9개짜리 집합 자리에 명세서가 5개짜리 다른 목록을 그럴듯한 대체물로 채워
        /// 넣었다. 목록만 있으면 눈으로 세어야 알지만, 수가 칸으로 있으면 어긋남이
        /// 즉시 보인다.
        ///
        /// [「술어 원문」 열 - 2026-08-22 축 A 재감사 ③ Task 7, 설계 §5]
        /// 행 단위가 원소에서 최상위 AND 항으로 올라가면서(Task 5·6) 열이 여덟이 됐다.
        /// 컬럼·연산·리터럴 칸은 분해된 결과라 분해가 담지 못한 항은 흔적도 없이
        /// 사라졌는데, 원문 칸이 그 항에 표의 자리를 준다. 분해되는 항도 원문을 함께
        /// 실어 독자가 분해를 원문과 대조할 수 있게 한다
        /// (SetPredicateFact.PredicateText 문서의 실측 근거).
        ///
        /// 이 렌더러와 MechanicalValidator.CheckSetPredicates는 <b>짝</b>이다. 한쪽만
        /// 바꾸면 모델이 표를 원문 그대로 옮겨도 L1이 틀렸다고 하는 실패 모양이 된다
        /// (ExtractSetPredicateLiteralCell 문서에 그 실물이 적혀 있다) - 열을 더하거나
        /// 자리를 옮길 때는 반드시 두 곳을 한 커밋에서 함께 고친다.
        ///
        /// [원문 칸의 길이를 가정하지 않는다] 최상위 항이 `EXISTS(...)`이면 그 하위
        /// 질의 전체가 한 줄로 접혀 이 칸에 들어간다. 어떤 항도 버리지 않는 것이
        /// 설계의 결정이므로(설계 §3 결정 3) 자르지 않는다.
        ///
        /// 헤딩 리터럴은 DmlScopeExtractor.SetPredicateTableHeading 하나가 유일한
        /// 출처다 - 프롬프트와 L1(CheckSetPredicates)이 같은 상수를 쓴다.
        ///
        /// [채번 - FIX ROUND 3] 문장 번호는 `fact.StatementOrdinal`을 그대로 쓴다 -
        /// DML 범위 표를 별도로 조회하지 않는다. SetPredicateVisitor가 그 값을
        /// DmlScopeVisitor와 <b>같은 규칙으로 독자적으로</b> 매긴다
        /// (SetPredicateFact.StatementOrdinal 문서 참고) - 두 방문자가 같은 파싱
        /// 트리를 같은 네 오버라이드(DML 셋은 `Visit`, FROM이 있는 독립 SELECT는
        /// `ExplicitVisit(SelectStatement)`)로 같은 순서로 방문하고 SELECT 판정도
        /// `DmlScopeExtractor.HasFromClause` 하나를 공유하므로 항상 일치한다.
        /// 예전엔(FIX ROUND 2) (연산, 라인) 키로 DML 범위 사실을 찾아 그 번호를
        /// "빌려 썼는데", 같은 줄에 같은 연산 문장이 둘이고 둘 다 집합 술어를 가지면
        /// 그 키가 여전히 충돌해 회귀가 났다(BuildStatementOrdinals 문서의 FIX
        /// ROUND 3 참고). 사전 조회 자체를 없애 이 계열의 결함을 구조적으로 막는다.
        /// </summary>
        private static List<string> BuildSetPredicateTableLines(IReadOnlyList<SetPredicateFact> setPredicates)
        {
            var lines = new List<string>
            {
                "   [CRITICAL SET PREDICATE TABLE] The following set predicates are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## CRUD 분석` under the exact heading shown. Do NOT drop, add, abbreviate, or summarize any literal - the membership of each set is what determines the target rows, and it cannot be inferred from the column name. The 범위 column says where the predicate sits - `최상위` is the statement's own WHERE, `파생 테이블 X` is the WHERE inside that derived table, and `조인 ON T` is a term of the ON clause of the JOIN that brings in table or alias T (`파생 테이블 X · 조인 ON T` when that JOIN sits inside derived table X). Column-to-column join-key equalities such as `A.PGName = PG.PGName` are NOT listed under `조인 ON` - they belong to the `DML 범위` table's 조인 키 column - so every `조인 ON` row is a condition that narrows which rows of T take part (a literal such as `PG.ExtraType IN (2,3)`, a parameter, or an expression) and must be described as a filter on the statement's row set exactly like a WHERE term. When the 범위 cell starts with `LEFT OUTER` / `RIGHT OUTER` / `FULL OUTER`, the term decides which rows of T MATCH, not which outer rows survive - describe it as a matching condition, never as a filter that removes target rows. A predicate inside a derived table narrows the target rows just as much as a top-level one, so it must be described as a filter, never softened into `조회합니다`. When the 문장 cell reads `SELECT n` the statement is a standalone SELECT - any SELECT statement of its own with a FROM clause, such as a cursor source query, a variable-assignment SELECT, a function-body SELECT, or a result set the object returns to its caller - and its predicate narrows the rows that statement reads, not rows that any INSERT, UPDATE or DELETE writes: state the narrowing explicitly as a filter on what is read (`... 인 행만`), and never present such a row as limiting the target rows of a write. One row is one top-level AND term of that WHERE. The last column, 술어 원문, carries that term exactly as written in the DDL. When 컬럼, 연산, 원소 수 and 리터럴 목록 all hold `—`, the term could not be decomposed into a column and a set of literals (a comparison whose right-hand side is a parameter, a column-to-column comparison, an `IN` whose right-hand side is a subquery, an OR-combined condition, an arithmetic right-hand side, an operator this table does not decompose), and 술어 원문 is then the ONLY record of that filter - copy it verbatim, character for character, and describe the filter from it. Never omit such a row because it looks unlike the others, and never replace 술어 원문 with a paraphrase, a translation, or a summary.",
                $"   {DmlScopeExtractor.SetPredicateTableHeading}",
                "   | 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |",
                "   | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |"
            };

            foreach (var fact in setPredicates)
            {
                // 분해 여부는 세 칸이 함께 움직인다(SetPredicateFact.Column 문서 -
                // "분해되면 셋 다 차고, 안 되면 셋 다 비운다"). 그래서 리터럴이
                // 비었는지 하나만 보면 되고, 원소 수·리터럴 목록도 같은 갈래에서
                // UndecomposedCell로 낸다 - 분해되지 않은 항의 "원소 0개"는 빈
                // 집합이라는 사실이 아니라 판정 자체가 없었다는 뜻이라, 0이나
                // 빈 칸으로 적으면 표가 거짓 집합을 단언한다.
                var decomposed = fact.Literals.Count > 0;
                var literals = decomposed ? string.Join(", ", fact.Literals) : UndecomposedCell;
                var count = decomposed ? fact.Literals.Count.ToString() : UndecomposedCell;

                lines.Add(
                    $"   | {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | "
                    + $"{EscapeTableCell(fact.Column)} | {EscapeTableCell(fact.Operator)} | "
                    + $"{EscapeTableCell(fact.Scope)} | {count} | {EscapeTableCell(literals)} | "
                    + $"{EscapeTableCell(fact.PredicateText)} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 「참조 함수」 표를 렌더한다. 이 절은 조립기가 채우고 LLM은 손대지 않는다.
        ///
        /// [왜 동작 서술 칸이 없는가 - 2026-08-20 축 A 교차 대조]
        /// 이 자리에 "실제 로직" 칸이 있던 시절 EXCEPTION_PROC의 10행 중 8행이
        /// 결함이었고 🔴이 5건이었다(USESTATE=0 술어 누락, IIF 분기 누락, 기본값 0
        /// 반환 누락). 함수 DDL 전문이 이미 프롬프트에 있었는데도 그랬다.
        /// 그래서 서술 칸 자체를 없애고 함수 Spec.md로 링크만 건다.
        /// </summary>
        private static List<string> BuildReferencedFunctionTableLines(
            IReadOnlyList<ReferencedFunctionCallFact> calls,
            SpDefinition spDef)
        {
            var functionDeps = (spDef.Dependencies ?? new List<DependencyInfo>())
                .Where(d => SqlObjectTypeClassifier.ResolveCodeObjectType(d.Type) == CodeObjectType.Function)
                .ToList();

            var lines = new List<string>
            {
                "   [CRITICAL REFERENCED FUNCTION TABLE] The following function calls are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## CRUD 분석` under the exact heading shown. Do NOT add a column describing what a function does, and do NOT describe the behaviour of any function listed in this table - its return value, branches, filters, or defaults - anywhere in the document: not in this section, not in CRUD 분석, not in 로직 흐름. When a SET expression calls a function, name the call and leave it at that. The single source of truth for a function's behaviour is that function's own Spec.md, which this table links to.",
                $"   {DmlScopeExtractor.ReferencedFunctionTableHeading}",
                "   | 함수 | 호출 위치 | 인자 | 명세서 |",
                "   | :--- | :--- | :--- | :--- |"
            };

            // [IF n 교차 대조 금지 - 2026-08-23 ③(b) 최종 리뷰 유예] 이 표의 IF n은 술어에
            // 알려진 함수 호출이 있는 IF만, 잠금 힌트 표의 IF n은 술어에 하위 질의가 있는
            // IF만 센다(ReferencedFunctionVisitor·LockHintVisitor의 ExplicitVisit(IfStatement)).
            // 같은 IF 1이 다른 문장일 수 있다 - 그 경고가 코드 주석·architecture.md·AGENTS.md에는
            // 있었지만 표를 읽는 모델의 프롬프트에는 없었다. 혼동은 두 표 모두에 IF n이 있을
            // 때만 생기므로 이 표에 IF 행이 있을 때만 싣는다 - 코퍼스에 그런 객체가 0이라
            // 기존 프롬프트 바이트는 불변이고(캐시 인상 없음), 조건이 DDL에서 나오므로 나중에
            // 어떤 객체가 IF dbo.UF_X(...)를 얻으면 DDL 해시가 바뀌어 자동 재분석된다.
            if (calls.Any(c => string.Equals(c.Operation, "IF", StringComparison.OrdinalIgnoreCase)))
            {
                lines.Insert(1,
                    "   The IF n in this table's 호출 위치 column numbers only IF statements whose predicate contains a known function call. "
                    + "It is NOT the same numbering as the IF n in the 잠금 힌트 table, which numbers IF statements whose predicate contains a subquery - "
                    + "the same label can point at different statements. Never equate or cross-reference IF n between the two tables; "
                    + "DML numbers (UPDATE/INSERT/DELETE n) and SELECT n ARE shared across the four machine-confirmed tables (DML 범위 · 잠금 힌트 · 집합 술어 · 참조 함수).");
            }

            foreach (var call in calls)
            {
                var dep = FindFunctionDependency(functionDeps, call.QualifiedName);

                var display = dep == null
                    ? call.QualifiedName
                    : string.IsNullOrWhiteSpace(dep.Database)
                        ? $"{dep.Schema}.{dep.Name}"
                        : $"{dep.Database}.{dep.Schema}.{dep.Name}";
                var link = dep != null ? BuildFunctionSpecRelativePath(dep, spDef) : "(명세서 없음)";

                lines.Add(
                    $"   | {EscapeTableCell(display)} | {call.Operation} {call.StatementOrdinal} (라인 {call.Line}) | "
                    + $"{EscapeTableCell(call.CallExpression)} | {link} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 잠금 힌트 표 본문을 만든다. 헤딩 리터럴은 추출기의 상수
        /// (<see cref="DmlScopeExtractor.LockHintTableHeading"/>)를 쓴다 - L1이 산출물을
        /// 대조할 때 찾는 접두와 같아야 하고, 문구를 고칠 때 한쪽만 바뀌는 일을 막는다.
        /// 세 배선 경로(SP 최초 생성·함수 명세서·CrudAnalysis 분기)가 이 헬퍼를 공유해야
        /// 같은 표가 나간다는 것이 코드로 보장된다 - BuildReferencedFunctionTableLines와
        /// 같은 이유다.
        ///
        /// [행 하나가 (문장 × 스캔 자리) 하나인 이유 - 2026-08-21 축 A 감사]
        /// INS_EXTRA4PLCARD 실측: 같은 TPGProperty가 별칭 P·Y에는 NOLOCK이 붙고 PG에는
        /// 안 붙는데 명세서가 "5개 테이블의 조회 또는 조인에 사용됩니다"로 뭉갰다.
        /// 문장당 한 칸으로는 이 결함을 담을 수 없어, LockHintFact를 참조 단위로 그대로
        /// 행에 옮긴다 - 힌트가 없으면 "(없음)"을 명시적으로 적어 "표에 없는 참조"와
        /// "표에는 있는데 힌트가 없는 참조"를 구분한다.
        /// </summary>
        private static List<string> BuildLockHintTableLines(IReadOnlyList<LockHintFact> facts)
        {
            var lines = new List<string>
            {
                $"   {LockHintIntroText}",
                $"   {DmlScopeExtractor.LockHintTableHeading}",
                "   | 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |",
                "   | :--- | :--- | :--- | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                var hints = fact.Hints.Count == 0 ? "(없음)" : string.Join(", ", fact.Hints);
                lines.Add(
                    $"   | {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | " +
                    $"{EscapeTableCell(fact.Table)} | {EscapeTableCell(fact.Alias)} | " +
                    $"{EscapeTableCell(fact.Scope)} | {EscapeTableCell(hints)} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 「실행 의미」 표를 렌더한다. 조립기가 채우고 LLM은 손대지 않는다.
        ///
        /// [왜 인트로가 렌더러 안에 있는가] 이 표는 갈래 2(함수 명세서 경로)에도
        /// 실리는데 그 갈래에는 ruleIndex 채번이 없다(규칙 1~7이 verbatim 문자열로
        /// 하드코딩돼 있다). 인트로를 번호 붙은 규칙으로 분리하면 갈래마다 모양이
        /// 갈리므로, 참조 함수·잠금 힌트 표와 같이 렌더러가 인트로를 진다.
        /// </summary>
        private static List<string> BuildExecutionSemanticsTableLines(
            IReadOnlyList<ExecutionSemanticFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL EXECUTION SEMANTICS TABLE] The following facts are MACHINE-DERIVED from the source DDL and static analysis. Copy this table verbatim into `## CRUD 분석` under the exact heading shown. These are settled values, not open questions - never restate any of them as unknown, unverifiable, or not provided.",
                $"   {ExecutionSemanticsFacts.TableHeading}",
                "   | 종류 | 라인 | 대상 | 확정 사실 |",
                "   | :--- | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add(
                    $"   | {EscapeTableCell(fact.Kind)} | {EscapeTableCell(fact.Line)} | "
                    + $"{EscapeTableCell(fact.Target)} | {EscapeTableCell(fact.Fact)} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 「CASE 분기」 표를 렌더한다. 조건·결과 모두 원문 그대로 실린다 -
        /// 요약이 곧 결함이었다(UIF_SettleYMD 🟠 3건).
        /// </summary>
        private static List<string> BuildCaseBranchTableLines(IReadOnlyList<CaseBranchFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL CASE BRANCH TABLE] The following CASE branches are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## 로직 흐름 요약` under the exact heading shown. Never merge branches, never paraphrase a comparison operator, and never summarise a result expression - the verbatim text is the contract.",
                $"   {CaseBranchExtractor.TableHeading}",
                "   | 라인 | 순서 | 조건 원문 | 결과 원문 |",
                "   | :--- | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add(
                    $"   | {fact.Line} | {EscapeTableCell(fact.Ordinal)} | "
                    + $"{EscapeTableCell(fact.Condition)} | {EscapeTableCell(fact.Result)} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 「트랜잭션 경계」 표를 렌더한다. 줄·종류·이름만 전사한다 - 감싼 조건은
        /// `TransactionBoundaryExtractor` 문서가 밝힌 이유로 담지 않는다.
        /// </summary>
        private static List<string> BuildTransactionBoundaryTableLines(
            IReadOnlyList<TransactionBoundaryFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL TRANSACTION BOUNDARY TABLE] The following transaction statements are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## 로직 흐름 요약` under the exact heading shown. Never merge rows, never omit a ROLLBACK, and never describe a boundary in prose instead of listing it - the batch implementation must reproduce every one of them.",
                $"   {TransactionBoundaryExtractor.TableHeading}",
                $"   | {string.Join(" | ", TransactionBoundaryExtractor.TableHeaderCells)} |",
                "   | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add($"   | {fact.Line} | {EscapeTableCell(fact.Kind)} | {EscapeTableCell(fact.Name)} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 「변수 대입」 표를 렌더한다. 대입식 원문을 그대로 싣는다 - 요약하면 그
        /// 값의 계약이 사라진다. `|` 같은 비트 연산자가 셀 경계로 읽히지 않도록
        /// <see cref="EscapeTableCell"/>을 반드시 거친다.
        /// </summary>
        private static List<string> BuildSetAssignmentTableLines(
            IReadOnlyList<SetAssignmentFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL VARIABLE ASSIGNMENT TABLE] The following SET assignments are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## 로직 흐름 요약` under the exact heading shown. Never summarise an assignment expression and never merge rows - the verbatim expression text is the contract.",
                $"   {SetAssignmentExtractor.TableHeading}",
                "   | 라인 | 변수 | 대입식 원문 |",
                "   | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add($"   | {fact.Line} | {EscapeTableCell(fact.Variable)} | {EscapeTableCell(fact.Expression)} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 「오류 코드」 표를 렌더한다. 코드 리터럴을 원문 그대로 싣는다 - 연속 범위로
        /// 접으면(`-1~-23`) 규약 9가 금지하는 바로 그 형태가 되고 갱신 번호와의 대응이
        /// 사라진다.
        /// </summary>
        private static List<string> BuildErrorCodeTableLines(
            IReadOnlyList<ErrorCodeFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL ERROR CODE TABLE] The following statement-to-error-code pairs are MACHINE-DERIVED from the source DDL. Copy this table verbatim under the exact heading shown. Never merge rows into ranges and never renumber - the pairing is the contract.",
                $"   {DmlScopeExtractor.ErrorCodeTableHeading}",
                "   | 문장 | 오류 코드 | 설정 대상 |",
                "   | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add($"   | {fact.Operation} {fact.StatementOrdinal} | {EscapeTableCell(fact.Code)} | {EscapeTableCell(fact.Variable)} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 「지역 변수」 표를 렌더한다. 헤딩 리터럴을 함께 실어 모델이 헤딩을 지어낼
        /// 자리를 없앤다.
        ///
        /// [왜 헤딩까지 싣는가 - 실측] 현 코퍼스의 EXCEPTION_PROC은 지역 변수 표를
        /// 실제로 썼는데 전용 헤딩을 안 붙였다(Spec.md:87-92, `## 파라미터 목록` 아래
        /// 산문 뒤에 표만). SpecStatementFactsExtractor.ReadLocalVariables는 헤딩으로만
        /// 구간을 잡으므로 그 표를 못 읽고 0을 낸다 - known-defects (5-3-7)의 소실
        /// 14건 중 1건이 그 원인이다. 헤딩을 프롬프트가 주면 그 실패 모드가 없다.
        ///
        /// [목적지가 `## 파라미터 목록`인 근거] 두 세대 실측 - 현 코퍼스의 유일한
        /// 잔존(UF_GET_OUTYMD4REFUND)도, 승격 전 스냅샷(output.bak-cache17-20260827)의
        /// 둘도 전부 그 절 아래에 있었다.
        /// </summary>
        private static List<string> BuildLocalVariableTableLines(
            IReadOnlyList<LocalVariableDeclarationFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL LOCAL VARIABLE TABLE] The following DECLARE'd local variables are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## 파라미터 목록` under the exact heading shown. Never rename a variable, never change or abbreviate a declared type, and never add a row for a procedure parameter - the declared type is the contract, and an implementer who guesses a type from the variable name will truncate money values.",
                $"   {LocalVariableDeclarationExtractor.TableHeading}",
                "   | 변수 명칭 | 데이터 타입 | 초기값 |",
                "   | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add(
                    $"   | {EscapeTableCell(fact.Name)} | {EscapeTableCell(fact.DataType)} | {EscapeTableCell(fact.InitialValue)} |");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 한 사실 종류(실행 의미 · CASE 분기)를 한 갈래의 프롬프트에 어떻게 실을지 -
        /// Task 17, 최종 브랜치 리뷰 2차(Important). 이전에는 bool 두 개
        /// (crudAnalysisSectionPresent · logicFlowSectionPresent)로 "표냐 참고
        /// 재료냐"만 표현했다. 그런데 `OverviewAndParameters` 갈래는 실행 의미는
        /// 참고 재료로 받되 CASE 분기는 아예 받지 않아야 하는 세 번째 값이 필요했고,
        /// bool로는 그 값을 표현할 수 없었다 - 그래서 이 갈래가 `BuildMachineFactBlockLines`를
        /// 부르지 않고 `BuildExecutionSemanticsReferenceMaterialLines`를 직접 불러
        /// 두 번째 진입점을 만들었다. 진입점이 둘이면 표 하나가 늘 때 이 갈래만
        /// 조용히 못 받는 회귀를 문서만으로는 못 막는다(설계 D5가 걱정한 바로 그
        /// 모양). 그래서 상태를 3상태로 넓혀 진입점을 다시 하나로 되돌린다.
        /// </summary>
        private enum MachineFactPresentation
        {
            /// <summary>이 갈래가 목적지 H2를 직접 쓴다 - 표 그대로 싣는다.</summary>
            Table,

            /// <summary>이 갈래는 목적지 H2를 쓰지 않지만 산문이 이 사실을 서술할 수
            /// 있다 - 표가 아니라 참고 재료로 싣는다(`BuildLockHintReferenceMaterialLines`
            /// 선례).</summary>
            Reference,

            /// <summary>이 갈래의 서술 대상이 아니다 - 아예 싣지 않는다. 예:
            /// `OverviewAndParameters`에 CASE 분기(`## 로직 흐름 요약` 소관, 개요와
            /// 무관).</summary>
            Omit
        }

        /// <summary>
        /// 새로 추가되는 기계 확정 표를 전부 모아 프롬프트에 붙일 줄 목록으로 돌려준다.
        ///
        /// [왜 갈래마다 Collect를 부르지 않는가 - 설계 D5]
        /// 프롬프트 빌더는 5갈래이고(SP 전체 · 함수 · 지역 모델 CRUD · 지역 모델 로직 ·
        /// 지역 모델 개요), 지역 모델 경로는 BuildSpecificationPrompts를 아예 호출하지
        /// 않는다. 표 하나를 늘릴 때마다 다섯 곳에 같은 조건문을 베끼면 "한 갈래만
        /// 고쳤다"는 이 코드베이스의 반복 사고가 그대로 재생산된다. 진입점을 하나로
        /// 두면 표를 늘려도 갈래는 바뀌지 않는다.
        ///
        /// 기존 표 6종은 이 함수로 옮기지 않는다 - 갈래별 렌더 조건에 미묘한 비대칭이
        /// 있어(집합 술어는 dmlScopeFacts가 비면 렌더하지 않는다) 잘못 통일하면 기존
        /// 표가 조용히 사라지거나 더해진다.
        ///
        /// [목적지 절 상태를 사실 종류마다 받는 이유 - Task 14 (Critical), 2026-08-22
        /// 최종 브랜치 리뷰 / Task 17, 최종 브랜치 리뷰 2차] 진입점을 하나로 둔 것
        /// 자체는 옳았지만, 표 두 종의 인트로가 서로 다른 목적지 H2("실행 의미"는
        /// `## CRUD 분석`, "CASE 분기"는 `## 로직 흐름 요약`)를 못 박는데, 다섯 갈래가
        /// 서로 다른 H2 부분집합만 쓴다 - `CrudAnalysis`는 `## CRUD 분석` 하나만
        /// 허용하고(H2 제약이 "오직 하나만"이라고 명시한다), `LogicAndVisualization`은
        /// `## 로직 흐름 요약`과 `## 비즈니스 흐름 시각화` 둘을 허용하며(H2 제약이
        /// "오직 하나만"이 아니다) 그 둘 중 어느 쪽에도 `## CRUD 분석`은 없고,
        /// `OverviewAndParameters`는 `## 개요`와 `## 파라미터 목록`만 허용해 `## CRUD
        /// 분석`도 `## 로직 흐름 요약`도 없다. 갈래 구분 없이 표를 함께 냈더니 "자신이
        /// 쓸 수 없는 절에 표를 넣으라"는 자기모순 지시가 됐다 - 모델이 그 H2 제약을
        /// 어기고 엉뚱한 헤딩까지 합성하거나(귀결: 문서에 같은 `###` 헤딩이 두 번
        /// 생기고 `MechanicalValidator.LocateHeadingSection`은 `FindIndexOutsideFence`로
        /// 첫 일치만 보므로 뒤 사본이 조용히 사라진다), 표 자체를 버려 그 갈래의
        /// 산출물에는 그 표가 아예 없는 둘 중 하나가 난다. 그래서 각 사실 종류마다
        /// 갈래가 <see cref="MachineFactPresentation"/> 중 무엇을 원하는지 호출부가
        /// 명시적으로 알려준다 - `Table`(목적지 H2를 직접 쓴다) · `Reference`(목적지
        /// H2는 안 쓰지만 산문이 서술할 수 있다 - `BuildLockHintReferenceMaterialLines`
        /// 선례) · `Omit`(이 갈래의 서술 대상이 아니다). 진입점은 하나로 남고, 표를
        /// 늘려도 다섯 갈래에 자동으로 실리는 이점은 그대로 유지되며, 각 갈래가 왜 그
        /// 값을 받는지가 호출부 코드에 남는다(문서만 읽는다는 가정에 기대지 않는다).
        /// </summary>
        private static List<string> BuildMachineFactBlockLines(
            SpDefinition spDef,
            MachineFactPresentation executionSemanticsPresentation,
            MachineFactPresentation caseBranchPresentation,
            MachineFactPresentation uncoveredNoticePresentation,
            MachineFactPresentation localVariablePresentation)
        {
            var lines = new List<string>();

            if (executionSemanticsPresentation != MachineFactPresentation.Omit)
            {
                var executionSemantics = ExecutionSemanticsFacts.Collect(
                    spDef.DdlText,
                    spDef.StaticAnalysis,
                    spDef.ObjectKey,
                    ExecutionSemanticsFacts.BuildColumnTypeMap(spDef.Dependencies));
                if (executionSemantics.Count > 0)
                {
                    lines.AddRange(executionSemanticsPresentation == MachineFactPresentation.Table
                        ? BuildExecutionSemanticsTableLines(executionSemantics)
                        : BuildExecutionSemanticsReferenceMaterialLines(executionSemantics));
                }
            }

            if (caseBranchPresentation != MachineFactPresentation.Omit)
            {
                var caseBranches = CaseBranchExtractor.Extract(spDef.DdlText);
                if (caseBranches.Count > 0)
                {
                    lines.AddRange(caseBranchPresentation == MachineFactPresentation.Table
                        ? BuildCaseBranchTableLines(caseBranches)
                        : BuildCaseBranchReferenceMaterialLines(caseBranches));
                }
            }

            // [트랜잭션 경계·변수 대입 - Task 4b, 2026-08-24] 둘 다 CASE 분기와 같은
            // `## 로직 흐름 요약` 소관이라 caseBranchPresentation을 그대로 재사용한다 -
            // 새 파라미터를 늘리지 않는다. 다만 CASE 분기와 달리 Reference 변형(참고
            // 재료 글머리 목록)은 만들지 않는다 - 두 사실은 `## CRUD 분석`이 요구하는
            // 소스값 매핑에 CASE 식처럼 끼어들 자리가 없어 그 분기(CrudAnalysis,
            // Reference)에서 참고 재료로 줄 근거가 없다. 그래서 Table일 때만 싣고,
            // Reference·Omit 둘 다 아무것도 싣지 않는다(Omit과 같은 결과).
            if (caseBranchPresentation == MachineFactPresentation.Table)
            {
                var transactionBoundaries = TransactionBoundaryExtractor.Extract(spDef.DdlText);
                if (transactionBoundaries.Count > 0)
                {
                    lines.AddRange(BuildTransactionBoundaryTableLines(transactionBoundaries));
                }

                var setAssignments = SetAssignmentExtractor.Extract(spDef.DdlText);
                if (setAssignments.Count > 0)
                {
                    lines.AddRange(BuildSetAssignmentTableLines(setAssignments));
                }

                // SpecExpectations.From()의 errorCodes와 같은 규칙(SpecExpectations.ResolveDateParameter)
                // 으로 기준일 파라미터를 고른다 - 두 곳이 갈리면 모델이 표를 그대로 베껴도
                // L1이 틀렸다고 하는 재현 불가능한 실패가 난다.
                var errorCodeDateParameter = SpecExpectations.ResolveDateParameter(spDef.StaticAnalysis);
                var errorCodes = DmlScopeExtractor.ExtractErrorCodes(spDef.DdlText, errorCodeDateParameter);
                if (errorCodes.Count > 0)
                {
                    lines.AddRange(BuildErrorCodeTableLines(errorCodes));
                }
            }

            // [네 표가 담지 않는 문장의 공지 - 2026-08-23 ③(b) 최종 리뷰 유예 "MERGE 무출발점"]
            // 네 기계 확정 표는 MergeStatement에 출발점이 없다(DmlScopeExtractor.
            // ExtractUncoveredStatements 문서). 그 객체의 표는 다른 문장 행으로 채워져
            // 완전해 보이는데 MERGE만 통째로 빠지므로, 거짓 행 대신 "이 문장은 표가 담지
            // 않는다"를 기계가 말한다. 다섯 갈래가 모두 이 함수를 부르므로 배선은 여기
            // 한 곳이다 - 한쪽만 바뀌어 교착이 나는 구조를 피한다.
            //
            // [캐시 버전을 올리지 않는 이유] 이 블록은 MERGE가 있는 객체에서만 나오고
            // 코퍼스에 그런 객체가 0이라 영향받는 기존 산출물이 없다(프롬프트 바이트 불변).
            // MERGE 객체가 처음 들어오는 날은 그 객체가 처음 생성되는 날이라 캐시가 없다.
            // 공지도 갈래별 presentation을 따른다 - 네 표를 실제로 받는 갈래(SP 전체·함수·
            // CrudAnalysis)에만 "표에 넣지 마라"를 주고, 표를 받지 않는 개요·로직 갈래에는
            // 참고형 한 줄만 준다. 자기가 쓸 수 없는 목적지에 대한 지시를 받은 모델은 H2를
            // 어기고 헤딩을 합성하거나 지시를 버린다(위 Task 14/17 실측과 같은 모양).
            // [지역 변수 표 - known-defects (5-3-7)의 강제, 2026-08-29]
            // caseBranchPresentation을 재사용하지 않는다 - 그 셋(CASE 분기·트랜잭션
            // 경계·변수 대입)의 목적지는 `## 로직 흐름 요약`인데 이 표의 목적지는
            // `## 파라미터 목록`이라 갈래별 값이 다르다. 자기 파라미터를 갖는 이유가
            // 그것이다.
            //
            // Reference 변형을 만들지 않는다 - 이 표를 못 쓰는 두 갈래(CrudAnalysis·
            // LogicAndVisualization)는 변수 선언 목록을 산문으로 서술할 자리도 없다.
            // 그래서 Table이 아니면 아무것도 싣지 않는다(Reference == Omit).
            if (localVariablePresentation == MachineFactPresentation.Table)
            {
                var localVariables = LocalVariableDeclarationExtractor.Extract(spDef.DdlText);
                if (localVariables.Count > 0)
                {
                    lines.AddRange(BuildLocalVariableTableLines(localVariables));
                }
            }

            lines.AddRange(BuildUncoveredStatementNoticeLines(spDef, uncoveredNoticePresentation));

            return lines;
        }

        /// <summary>
        /// 네 표가 담지 않는 문장(지금은 MERGE)이 있을 때만 내는 기계 공지. 산문으로 적되
        /// 표에 행을 만들지 말고, 그 서술이 기계 확정이 아님을 밝히라고 한다.
        /// </summary>
        private static List<string> BuildUncoveredStatementNoticeLines(
            SpDefinition spDef, MachineFactPresentation presentation)
        {
            if (presentation == MachineFactPresentation.Omit) return new List<string>();

            var uncovered = DmlScopeExtractor.ExtractUncoveredStatements(spDef.DdlText);
            if (uncovered.Count == 0) return new List<string>();

            // 문구의 "USING source · ON predicate · WHEN branch"는 MERGE 전용이다. Kind가
            // 둘째 종류(예: CTE 기반 UPDATE)를 얻는 날 이 문장을 종류별로 갈라야 한다 -
            // 지금은 ExtractUncoveredStatements가 MERGE만 세므로 참이다.
            var byKind = uncovered
                .GroupBy(u => u.Kind)
                .Select(g => $"{g.Key} statement(s) at line(s) {string.Join(", ", g.Select(u => u.Line))}");
            var what = "This object contains " + string.Join("; ", byKind)
                + " that the four machine-confirmed tables (DML 범위 · 잠금 힌트 · 집합 술어 · 참조 함수) do NOT cover.";

            var instruction = presentation == MachineFactPresentation.Table
                ? " Describe each such statement in prose - its target, its USING source, the ON predicate, each WHEN branch and its action - "
                  + "and state explicitly that this description is not machine-confirmed. "
                  + "Do NOT add rows for them to those tables, and do NOT treat their absence from the tables as an omission."
                : " If this section mentions such a statement, describe it only in prose and do not present that description as machine-confirmed.";

            return new List<string> { "   [MACHINE NOTICE] " + what + instruction, "" };
        }

        /// <summary>
        /// 실행 의미 사실을 <b>표 출력 지시가 아니라 근거 재료</b>로 싣는다 -
        /// `BuildLockHintReferenceMaterialLines`와 같은 선례를 따른다. 이 분기는
        /// `## CRUD 분석`을 쓰지 않으므로(예: 지역 모델의 `LogicAndVisualization`
        /// 절 분할 갈래) `BuildExecutionSemanticsTableLines`의 "Copy this table
        /// verbatim into `## CRUD 분석`" 지시를 그대로 주면 모델이 자신의 H2 제약을
        /// 어기고 `## CRUD 분석` 헤딩까지 합성할 위험이 있다. 그래서 표 형식·헤딩
        /// 리터럴을 피하고, 사실 자체를 참고 전용 글머리 목록으로 준다.
        /// </summary>
        private static List<string> BuildExecutionSemanticsReferenceMaterialLines(
            IReadOnlyList<ExecutionSemanticFact> facts)
        {
            var lines = new List<string>
            {
                "   [REFERENCE - execution semantics facts] MACHINE-DERIVED from the source DDL and " +
                "static analysis. Do NOT output a table or heading for this list in this section - " +
                "`## CRUD 분석` (a separate part of this same document) is responsible for rendering " +
                "it as a table. These are settled values, not open questions - never restate any of " +
                "them as unknown, unverifiable, or not provided. Use these facts only as ground truth:"
            };

            foreach (var fact in facts)
            {
                lines.Add(
                    $"   - {fact.Kind} (라인 {fact.Line}, 대상 {fact.Target}): {fact.Fact}");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// CASE 분기 사실을 <b>표 출력 지시가 아니라 근거 재료</b>로 싣는다 -
        /// `BuildLockHintReferenceMaterialLines`와 같은 선례를 따른다. 이 분기는
        /// `## 로직 흐름 요약`을 쓰지 않으므로(예: 지역 모델의 `CrudAnalysis` 절
        /// 분할 갈래) `BuildCaseBranchTableLines`의 "Copy this table verbatim into
        /// `## 로직 흐름 요약`" 지시를 그대로 주면 모델이 자신의 H2 제약을 어기고
        /// `## 로직 흐름 요약` 헤딩까지 합성할 위험이 있다. 그래서 표 형식·헤딩
        /// 리터럴을 피하고, 조건·결과 원문 그대로를 참고 전용 글머리 목록으로 준다.
        /// </summary>
        private static List<string> BuildCaseBranchReferenceMaterialLines(
            IReadOnlyList<CaseBranchFact> facts)
        {
            var lines = new List<string>
            {
                "   [REFERENCE - CASE branch facts] MACHINE-DERIVED from the source DDL. Do NOT " +
                "output a table or heading for this list in this section - `## 로직 흐름 요약` (a " +
                "separate part of this same document) is responsible for rendering it as a table. " +
                "Never merge branches, never paraphrase a comparison operator, and never summarise a " +
                "result expression when you use these facts elsewhere in this document - the verbatim " +
                "text below is the contract:"
            };

            foreach (var fact in facts)
            {
                lines.Add(
                    $"   - 라인 {fact.Line}, 순서 {fact.Ordinal}: 조건 원문 `{fact.Condition}` -> " +
                    $"결과 원문 `{fact.Result}`");
            }

            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 잠금 힌트 표의 도입문.
        ///
        /// [범위 칸의 값이 셋이 된 뒤 - 2026-08-22 축 A 재감사 ③ Task 7]
        /// Task 3이 `하위 질의`를 더하고 Task 1이 문장 칸에 `SELECT n`·`IF n`을 더했는데,
        /// 이 문구는 `최상위`·`파생` 둘만 정의하고 있었다. 표를 "그대로 옮기라"고
        /// 지시하면서 산문이 표보다 적은 값을 정의하면, 모델은 정의 밖의 행을 오해하거나
        /// 아는 라벨로 바꿔 적는다. 경계의 권위 있는 서술은
        /// <c>DmlScopeExtractor.LockHintVisitor.SubqueryScope</c>의 문서에 있다 -
        /// 이 문구는 그것을 프롬프트 언어로 옮긴 것이므로, 그 문서가 바뀌면 여기도 바꾼다.
        /// </summary>
        private const string LockHintIntroText =
            "[CRITICAL LOCK HINT TABLE] The following lock hints are MACHINE-DERIVED from the source DDL. " +
            "Copy this table verbatim into `## CRUD 분석` under the exact heading shown. " +
            "A row with `(없음)` means that scan carries NO hint - do not omit those rows and do not " +
            "generalise across statements: the same table may carry a hint in one statement and not another, " +
            "or in one alias and not another within the same statement. The 문장 column names the statement " +
            "that owns the scan. Besides `INSERT n` / `UPDATE n` / `DELETE n` it can hold `SELECT n` - a " +
            "standalone SELECT outside any DML (a variable assignment, a cursor source, a function body) - " +
            "and `IF n` - a query inside an IF predicate. Those two update nothing, so describe them as reads; " +
            "do not turn them into DML. Numbering runs from 1 per statement kind. The 범위 column says where " +
            "the scan sits, and it has exactly three values: `최상위` is a position the statement (or that IF " +
            "predicate) scans directly, `파생` is inside a derived table in that FROM, and `하위 질의` is inside " +
            "a query opened again within one of those positions - a subquery in a WHERE or in a JOIN ... ON, " +
            "or a scalar subquery. `하위 질의` wins when a position is both. A scan in any of the three narrows " +
            "or reads real rows, so none of them may be omitted or softened.";

        /// <summary>
        /// 잠금 힌트 사실을 <b>표 출력 지시가 아니라 근거 재료</b>로 싣는다.
        ///
        /// [왜 필요한가 - 2026-08-21 최종 브랜치 리뷰 재라운드 ①]
        /// `LogicAndVisualization`(로직 흐름 요약)과 `OverviewAndParameters`(개요) 두 분기가
        /// NOLOCK을 서술할 수 있는데(전자는 명시적 규칙이 있고, 후자는 감사 🟡이 실제로
        /// 난 자리다 - `Spec.md:33`, `## 개요` 절, `UP_Util_Settle_Summary_AcqManual`의
        /// `DELETE TSettleByOUT FROM … WITH(NOLOCK)` 누락), 둘 다 `BuildLockHintTableLines`를
        /// 부르지 않는다 - 그 표는 `## CRUD 분석` 절 소관이다. 라운드 1은 "CRUD 분석 절에
        /// 이미 실린 표를 근거로 쓰라"는 포인터 지시로 이 구멍을 메우려 했지만 틀렸다 -
        /// 구역 분할 경로(`VerificationPipelineOrchestrator`)는 세 절을 병렬로 생성하므로
        /// (`:1328-1340`, `:1450-1462`), 이 분기가 실행되는 시점에 CRUD 분석 절은 아직
        /// 존재하지 않는다. 존재하지 않는 표를 "이미 실렸다"고 가리키는 지시는 이행할 수
        /// 없고, 최악의 경우 모델이 "그 표가 담지 않는 자리(커서 등)의 힌트는 서술하면
        /// 안 된다"로 과잉 해석해 DDL에 뻔히 보이는 힌트마저 억제한다 - 그 억제 대상이
        /// 바로 이번에 `axis-a.md`가 "DDL 원문이 기준값이니 결함으로 집지 마라"고 못
        /// 박은 커서 `SELECT`의 `NOLOCK` 둘이다. 그래서 포인터 대신 사실 자체를
        /// 인라인으로 준다 - 이 분기가 다른 분기의 산출물을 볼 필요가 없어진다.
        ///
        /// [표가 아니라 글머리 목록인 이유] `BuildLockHintTableLines`(위)를 그대로
        /// 재사용하면 그 도입문(`LockHintIntroText`)이 "Copy this table verbatim into
        /// `## CRUD 분석`"이라고 지시한다 - 이 분기는 `## CRUD 분석`을 쓰지 않으므로
        /// 그 지시를 그대로 주면 모델이 자신의 H2 제약(로직 흐름 요약/개요만 쓰라)을
        /// 어기고 `## CRUD 분석` 헤딩까지 합성할 위험이 있다. 그리고 `### 잠금 힌트
        /// (기계 확정 — 수정 금지)` 헤딩 리터럴을 이 재료에 그대로 넣으면, 최종 문서에
        /// CRUD 분석 절이 <i>같은</i> 헤딩을 또 낼 때 `MechanicalValidator.CheckLockHints`가
        /// (`FindIndexOutsideFence`로 첫 일치만 찾는다) 엉뚱한 절의 헤딩을 표로 오인할
        /// 위험이 생긴다. 그래서 표 형식·헤딩 리터럴을 피하고, 이 재료가 <b>참고
        /// 전용이며 이 절에는 표로 출력하지 않는다</b>는 문장을 앞에 명시한다.
        ///
        /// [범위 한정을 함께 싣는 이유] `axis-a.md`와 반대 방향을 지시하지 않기 위해서다.
        /// 그 문서는 이 사실 목록(=잠금 힌트 표)이 INSERT/UPDATE/DELETE의 FROM·대상
        /// 노드만 담는다고 못 박았고, 커서 선언·독립 SELECT·제어 흐름 술어(`IF
        /// EXISTS(...)`) 안의 하위 질의·최상위 WHERE 하위 질의·CTE 본문은 표 밖이며
        /// DDL 원문이 근거라고 적었다. 이 재료가 그 한정 없이 "이 목록이 잠금 힌트의
        /// 전부"로 읽히면, 모델이 정확히 그 자리(커서 `NOLOCK`)의 서술을 억제한다 -
        /// 라운드 1이 만든 문제와 같은 모양이라 같은 문장으로 닫는다.
        /// </summary>
        private static List<string> BuildLockHintReferenceMaterialLines(IReadOnlyList<LockHintFact> facts)
        {
            var lines = new List<string>
            {
                "   [REFERENCE - lock hint facts] MACHINE-DERIVED from the source DDL. Do NOT output " +
                "a table or heading for this list in this section - `## CRUD 분석` (a separate part of " +
                "this same document) is responsible for rendering it as a table. Use these facts only " +
                "as ground truth for which statement/table/alias/scope carries which lock hint:"
            };

            foreach (var fact in facts)
            {
                var hints = fact.Hints.Count == 0 ? "(없음)" : string.Join(", ", fact.Hints);
                lines.Add(
                    $"   - {fact.Operation} {fact.StatementOrdinal} (라인 {fact.Line}): " +
                    $"{fact.Table} (별칭 {fact.Alias}, 범위 {fact.Scope}) -> {hints}");
            }

            lines.Add(
                "   This list covers ONLY INSERT/UPDATE/DELETE statements' FROM references and target " +
                "nodes - it does NOT cover cursor declarations, standalone SELECTs, subqueries inside " +
                "control-flow predicates (e.g. `IF EXISTS(SELECT ... WITH(NOLOCK))`), a statement's own " +
                "top-level WHERE subqueries, or CTE bodies. For those, the source DDL itself is the " +
                "ground truth - do NOT suppress or omit lock-hint statements for those scans just " +
                "because they are absent from this list, and do NOT assert or contradict which table " +
                "or scan carries a hint beyond what is stated here or directly visible in the DDL.");
            lines.Add("");
            return lines;
        }

        /// <summary>
        /// 객체 선언 표. 함수에만 실린다 - 프로시저에는 WITH 옵션 자체가 없으므로
        /// <see cref="ObjectDeclarationExtractor.Extract"/>가 항상 null을 내고, 호출부는
        /// null일 때 이 헬퍼를 부르지 않는다.
        ///
        /// [왜 필요한가 - 2026-08-21 축 A 감사] UF_GET_OUTYMD4REFUND·
        /// UF_GET_SETTLE_EXCHANGERATE 둘 다 WITH SCHEMABINDING이 없다는 것이 DDL에서
        /// 확정되는데 명세서가 "제공되지 않아 확인할 수 없음"으로 적었다. "(없음)"이 곧
        /// "스키마 바인딩 아님"이라 그 여지를 없앤다.
        /// </summary>
        private static List<string> BuildObjectDeclarationTableLines(
            ObjectDeclarationExtractor.ObjectDeclarationFact fact)
        {
            var options = fact.WithOptions.Count == 0
                ? "(없음)"
                : string.Join(", ", fact.WithOptions);

            return new List<string>
            {
                $"   {ObjectDeclarationIntroText}",
                $"   {ObjectDeclarationExtractor.ObjectDeclarationTableHeading}",
                "   | 객체 | WITH 옵션 |",
                "   | :--- | :--- |",
                $"   | {EscapeTableCell(fact.QualifiedName)} | {EscapeTableCell(options)} |",
                ""
            };
        }

        private const string ObjectDeclarationIntroText =
            "[CRITICAL OBJECT DECLARATION TABLE] The WITH options below are MACHINE-DERIVED from the " +
            "CREATE statement. Copy this table verbatim into `## 개요` under the exact heading shown. " +
            "`(없음)` settles the question: the object is NOT schema-bound. Never write that schema " +
            "binding could not be determined.";

        /// <summary>
        /// 「참조 함수」 표에서 호출문의 한정명(<paramref name="qualifiedName"/>)에 대응하는
        /// 의존성 항목을 고른다.
        ///
        /// [I2 동행 수정 - R7, 2026-08-20] 예전엔 마지막 조각(함수 이름)만 대조했다 -
        /// 크로스 DB 함수(예: `SETTLE_CARD_DB.dbo.UF_GET_COMM4PG`)와 로컬 동명 함수가
        /// 있으면 잘못 짝지어질 여지가 있었고, I2가 `dep.Database`를 표시에 쓰기
        /// 시작하면서 그 오짝이 표시 문구까지 잘못 낸다(로컬 함수를 크로스 DB로,
        /// 혹은 그 반대로). 그래서 호출문에 한정자가 있으면(2부/3부) 그 한정명을
        /// 의존성의 `Database.Schema.Name` 또는 `Schema.Name`과 먼저 정확히 대조하고,
        /// 한정자가 없을 때만(또는 정확한 대조가 실패했을 때만) 마지막 조각으로
        /// 대조한다.
        /// </summary>
        private static DependencyInfo? FindFunctionDependency(
            IReadOnlyList<DependencyInfo> functionDeps, string qualifiedName)
        {
            if (qualifiedName.Contains('.', StringComparison.Ordinal))
            {
                var qualifiedMatch = functionDeps.FirstOrDefault(d =>
                    (!string.IsNullOrWhiteSpace(d.Database)
                        && string.Equals($"{d.Database}.{d.Schema}.{d.Name}", qualifiedName, StringComparison.OrdinalIgnoreCase))
                    || string.Equals($"{d.Schema}.{d.Name}", qualifiedName, StringComparison.OrdinalIgnoreCase));
                if (qualifiedMatch != null)
                {
                    return qualifiedMatch;
                }
            }

            return functionDeps.FirstOrDefault(d =>
                string.Equals(LastSegment(d.Name), LastSegment(qualifiedName), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>한정명의 마지막 조각만 낸다(`SETTLE_CARD_DB.dbo.UF_X` → `UF_X`).</summary>
        private static string LastSegment(string? qualified) =>
            string.IsNullOrWhiteSpace(qualified)
                ? string.Empty
                : qualified.Split('.').Last();

        /// <summary>
        /// SP 명세서(`output/Procedures/[SP]/docs/Spec.md`)에서 함수 명세서로 가는
        /// 상대 경로를 만든다. 「참조 코드 객체」 절이 이미 쓰는 것과 같은 형태다.
        /// 로컬 함수는 `output/Functions/`, 다른 DB의 함수는
        /// `output/External/[DB]/Functions/` 아래에 있다.
        /// </summary>
        private static string BuildFunctionSpecRelativePath(DependencyInfo dep, SpDefinition spDef)
        {
            var isExternal =
                !string.IsNullOrWhiteSpace(dep.Database) &&
                !string.Equals(dep.Database, spDef.ObjectKey?.Database, StringComparison.OrdinalIgnoreCase);

            var folder = isExternal
                ? $"../../../External/{dep.Database}/Functions"
                : "../../../Functions";

            return $"[Spec]({folder}/{dep.Schema}.{dep.Name}/docs/Spec.md)";
        }

        /// <summary>
        /// 파생 테이블 정의 표를 도입하는 규칙 문장. DmlScopeTableIntroText와 같은 이유로
        /// 두 프롬프트 빌더가 이 상수 하나를 공유한다 - 문구를 강화할 때 한쪽만 고쳐질
        /// 위험을 없앤다.
        /// </summary>
        private const string DerivedTableIntroText =
            "[CRITICAL DERIVED TABLE TABLE] The following derived-table column definitions are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## CRUD 분석` under the exact heading shown. When a SET (or SELECT) expression references one of these aliases, you MUST NOT stop at the alias reference - the definition below is what determines the amount.";

        /// <summary>
        /// 파생 테이블 정의 표 본문을 만든다. 헤딩 리터럴
        /// `DerivedTableColumnExtractor.DerivedTableHeading`은 Task 11의 L1(`MechanicalValidator`)이
        /// 명세서 본문을 대조할 때 찾는 접두다. BuildSpecificationPrompts와
        /// BuildSpecSectionPrompts의 "CrudAnalysis" 분기(지역 모델의 최초 생성 경로)가 이
        /// 헬퍼를 공유해야 두 경로가 같은 표를 내보낸다는 것이 코드로 보장된다 -
        /// BuildDmlScopeTableLines와 같은 이유다.
        /// </summary>
        private static List<string> BuildDerivedTableColumnLines(
            IReadOnlyList<DerivedColumnDefinition> derivedColumns)
        {
            var lines = new List<string>
            {
                $"   {DerivedTableColumnExtractor.DerivedTableHeading}",
                "   | 별칭 | 컬럼 | 정의 표현식 |",
                "   | :--- | :--- | :--- |"
            };

            foreach (var definition in derivedColumns)
            {
                lines.Add(
                    $"   | {definition.Alias} | {definition.Column} | {EscapeTableCell(definition.Expression)} |");
            }

            lines.Add("");
            return lines;
        }

        private (string SystemPrompt, string UserPrompt) BuildFunctionSpecificationPrompts(SpDefinition functionDef, string userInstructions, string? feedbackLog)
        {
            var systemPrompt = @"You are an expert SQL Server User Defined Function analyzer. Analyze the provided function metadata and write a comprehensive reverse-engineered specification in Markdown.

[Essential Rules]
1. Analyze the return contract, including scalar return type or TVF result schema.
2. Determine and document determinism, observable side effects, and every business formula or transformation only from the supplied DDL and metadata.
3. Identify all referenced tables and functions, their factual roles, and missing metadata without inventing columns or behavior.
4. For a table-valued function, document every result column, data type, nullability when known, and derivation source.
5. Include a Mermaid flowchart with quoted node labels and safe alphanumeric node identifiers.
6. Do not use abbreviations, ellipses, or conversational filler. Do not invent undefined columns, error codes, or behavior.
7. The required H2 headers are exactly: `## 개요`, `## 파라미터 목록`, `## CRUD 분석`, `## 로직 흐름 요약`, `## 비즈니스 흐름 시각화`.

[Output Language Requirement]
- You MUST write the final markdown specification in Korean.";

            // SpecExpectations.From()은 객체 타입을 가리지 않고 모든 SP/함수에 이
            // 재료들을 무조건 만든다(L1의 CheckDerivedTableDefinitions 등도 무조건
            // 돈다) - 그런데 이 프롬프트는 지금까지 파생 테이블 표를 한 번도 낸 적이
            // 없었다. 실측(현재 코퍼스 26건 중 함수 12건을 harness로 직접 돌린 결과):
            // UF_GET_COLLECTYMD·UIF_SettleYMD 둘 다 DerivedColumns=4로 실제 파생 컬럼이
            // 있고 DerivedTableDefinitionMissing이 1건씩(헤딩 자체가 없음) 발생한다.
            // SELECT/파생 테이블(FROM 절 서브쿼리)은 함수 본문에도 얼마든지 나타나므로
            // 이 표는 프로시저와 똑같이 필요하다.
            var derivedColumns = DerivedTableColumnExtractor.Extract(functionDef.DdlText);
            if (derivedColumns.Count > 0)
            {
                var derivedTableLines = new List<string> { DerivedTableIntroText };
                derivedTableLines.AddRange(BuildDerivedTableColumnLines(derivedColumns));
                systemPrompt += "\n\n" + string.Join("\n", derivedTableLines);
            }

            // [Fix Round 2 - 리뷰 실측, 이전 주석의 오류를 바로잡음] DML 범위 표를
            // "함수는 구조적으로 UPDATE/DELETE를 담을 수 없다"는 이유로 제외했었는데,
            // 그 전제 자체가 틀렸다. 다중 문장 테이블 반환 함수(Multi-statement TVF)는
            // 자신의 반환 테이블 변수(@Result 등)를 채운 뒤 그 변수에 UPDATE/DELETE를
            // 거는 것이 합법적이고 문서화된 T-SQL이다 - 스칼라 함수·인라인 TVF만
            // UPDATE/DELETE를 못 낼 뿐이다. DmlScopeExtractor.Visit(UpdateSpecification)/
            // Visit(DeleteSpecification)은 SessionOptionsExtractor의 ProcedureBodyFinder와
            // 달리 CreateProcedureStatement로 방문 범위를 좁히지 않는다 - fragment 전체를
            // 훑으므로 CREATE FUNCTION 안의 UPDATE/DELETE도 그대로 잡힌다(리뷰가 이런
            // TVF를 직접 만들어 확인: 사실 2건 반환). 그래서 이 표는 절차와 똑같이
            // Count > 0으로 게이트만 걸고 무조건 렌더링한다 - "함수는 DML이 없다"는
            // 잘못된 불변식을 코드에 다시 심지 않기 위해서다. DmlScopeExtractor 쪽을
            // CreateProcedureStatement로 좁혀 이 문제를 "고치는" 것은 하지 않는다 -
            // TVF가 자신의 반환 테이블에 거는 UPDATE/DELETE는 진짜 범위 정보이고
            // 모델이 받아야 할 재료다; 추출기를 좁히면 계약 위반을 정보 손실로
            // 맞바꿀 뿐이다. 현재 저장된 코퍼스 12건은 전부 단순 스칼라/인라인 함수라
            // DmlScopeFacts=0으로 측정되므로(SessionOptions·HasInternalProcCall과
            // 달리, "이 코퍼스에는 없다"일 뿐 "이 문법이 불가능하다"가 아니다) 이
            // 가드는 지금 당장은 항상 거짓이지만, 다중 문장 TVF가 들어오는 순간
            // 표가 렌더링된다.
            //
            // 나머지 둘은 여전히 뺀다 - 다만 이유가 서로 다르다는 것이 이 라운드의
            // 요점이다. SessionOptionsExtractor는 방문자가 CreateProcedureStatement/
            // CreateOrAlterProcedureStatement 본문만 훑도록 코드로 직접 좁혀져 있다
            // (SessionOptionsExtractor.ProcedureBodyFinder) - CREATE FUNCTION 안의
            // SET 문은 애초에 방문자 시야에 들어오지 않는다. 이것은 진짜 구문적
            // 불가능성이다(SQL Server가 함수 본문의 SET 문 자체를 CREATE 시점에
            // 거부한다). 반면 HasInternalProcedureCall(이름 고정 EXEC 호출)은
            // ScriptDom 파서 자체는 함수 안의 EXEC도 문법적으로는 파싱한다 - 막는
            // 것은 문법이 아니라 이 도구의 입력 경로다. DbMetadataService는
            // DdlText를 항상 이미 배포된 객체의 sys.sql_modules에서만 읽는데, SQL
            // Server는 부작용이 있는 EXEC를 함수 본문에 CREATE 시점에 거부하므로
            // 그런 함수는 애초에 배포되어 있을 수 없다 - 그래서 이 도구가 실제로
            // 마주치는 입력 도메인 안에서는 도달 불가능하다(구문이 막는 게 아니라
            // 배포 가능한 객체의 집합이 막는다). 둘 다 실측(코퍼스 12건 전부
            // SessionOptions=0, HasInternalProcCall=False)은 같지만, "왜 항상
            // 비는가"의 근거는 서로 다르다 - 하나를 다른 하나의 이유로 뭉뚱그리면
            // (이번에 DML 범위 표에서 실제로 벌어졌듯) 다음 리뷰가 또 찾아낸다.
            var dateParameter = SpecExpectations.ResolveDateParameter(functionDef.StaticAnalysis);
            var dmlScopeFacts = DmlScopeExtractor.Extract(functionDef.DdlText, dateParameter);
            if (dmlScopeFacts.Count > 0)
            {
                var dmlScopeLines = new List<string> { DmlScopeTableIntroText };
                dmlScopeLines.AddRange(BuildDmlScopeTableLines(dmlScopeFacts, dateParameter));
                systemPrompt += "\n\n" + string.Join("\n", dmlScopeLines);
            }

            // 배선 지점 3/3(잠금 힌트) - 다중 문장 TVF는 자신의 반환 테이블 변수를
            // 채우는 DML 문에서 잠금 힌트를 가질 수 있다. 위 DML 범위 표 Fix Round 2가
            // 실측한 것과 같은 이유로 "함수는 DML이 없다"는 잘못된 불변식을 다시 심지
            // 않는다 - DmlScopeExtractor.ExtractLockHints는 fragment 전체를 훑으므로
            // CREATE FUNCTION 안의 UPDATE/DELETE/INSERT도 그대로 잡힌다.
            var lockHintsForFunctionDef = DmlScopeExtractor.ExtractLockHints(functionDef.DdlText);
            if (lockHintsForFunctionDef.Count > 0)
            {
                systemPrompt += "\n\n" + string.Join("\n", BuildLockHintTableLines(lockHintsForFunctionDef));
            }

            // 이 경로(함수 명세서 프롬프트)도 BuildSpecificationPrompts와 같은 집합
            // 술어 표를 받아야 한다 - 하나만 배선하면 Task 4의 Critical과 같은
            // 비대칭이 재발한다.
            var setPredicates = DmlScopeExtractor.ExtractSetPredicates(functionDef.DdlText);
            // [소프트 페일 전파 방지] BuildSpecificationPrompts의 같은 이름 조건 참고 -
            // dmlScopeFacts가 비었는데 setPredicates만 채워지면 Extract 쪽만 소프트
            // 페일했다는 뜻이라, 재료 전체를 미덥지 않다고 보고 렌더하지 않는다.
            if (setPredicates.Count > 0 && dmlScopeFacts.Count > 0)
            {
                systemPrompt += "\n\n" + string.Join("\n", BuildSetPredicateTableLines(setPredicates));
            }

            // 참조 함수 표도 같은 이유로 기계가 채운다 - 함수 명세서도 다른 함수를
            // 호출할 수 있다(예: 스칼라 함수가 헬퍼 함수를 부른다). 이 경로를 빠뜨리면
            // 함수 명세서에서만 함수 서술 금지 계약이 뚫린다.
            var knownFunctionNamesForFunctionDef = (functionDef.Dependencies ?? new List<DependencyInfo>())
                .Where(d => SqlObjectTypeClassifier.ResolveCodeObjectType(d.Type) == CodeObjectType.Function)
                .Select(d => d.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var functionCallsForFunctionDef = DmlScopeExtractor.ExtractFunctionCalls(functionDef.DdlText, knownFunctionNamesForFunctionDef);
            if (functionCallsForFunctionDef.Count > 0)
            {
                systemPrompt += "\n\n" + string.Join("\n", BuildReferencedFunctionTableLines(functionCallsForFunctionDef, functionDef));
            }

            // 이 갈래(함수 명세서)도 위 "The required H2 headers are exactly" 규칙(7번)이
            // `## CRUD 분석`·`## 로직 흐름 요약` 둘 다 필수 H2로 요구한다 - 두 표 모두
            // 표 그대로 준다.
            var machineFactLinesForFunctionDef = BuildMachineFactBlockLines(
                functionDef,
                executionSemanticsPresentation: MachineFactPresentation.Table,
                caseBranchPresentation: MachineFactPresentation.Table,
                uncoveredNoticePresentation: MachineFactPresentation.Table,
                localVariablePresentation: MachineFactPresentation.Table);
            if (machineFactLinesForFunctionDef.Count > 0)
            {
                systemPrompt += "\n\n" + string.Join("\n", machineFactLinesForFunctionDef);
            }

            // 배선 지점 1/2(객체 선언) - 프로시저에는 이 옵션 자체가 없으므로 Extract는
            // 함수가 아니면 항상 null을 낸다(ObjectDeclarationExtractor 문서 참고).
            // UF_GET_OUTYMD4REFUND·UF_GET_SETTLE_EXCHANGERATE 실측(2026-08-21 축 A 감사) -
            // WITH 절이 없다는 것이 DDL에서 확정되는데 명세서가 "확인할 수 없음"으로
            // 적었다. "(없음)"이 곧 "스키마 바인딩 아님"이라 그 여지를 없앤다.
            var objectDeclarationForFunctionDef = ObjectDeclarationExtractor.Extract(functionDef.DdlText);
            if (objectDeclarationForFunctionDef != null)
            {
                systemPrompt += "\n\n" + string.Join("\n", BuildObjectDeclarationTableLines(objectDeclarationForFunctionDef));
            }

            systemPrompt += $"\n\n[USER INSTRUCTIONS]\n{userInstructions}";

            var (dependenciesText, tableSchemasText, referenceDdlsText, staticAnalysisText) = BuildSpMetadataTexts(functionDef);
            var returnInfo = functionDef.FunctionReturn;
            var returnContract = returnInfo == null
                ? "Return metadata is not available. Derive only what is explicit in the DDL."
                : returnInfo.IsTableValued
                    ? $"Table-valued function. Result columns:\n{string.Join("\n", returnInfo.Columns.Select(FormatFunctionReturnColumn))}"
                    : $"Scalar function return type: {returnInfo.DataType}";

            // 같은 이유로 CheckSourceComments·CheckRoundingSemantics도 함수에 무조건
            // 돈다. 실측: 코퍼스 함수 12건 중 6건이 이미 SourceCommentMissing으로
            // 걸린다(주석 요구 문구가 프롬프트에 없었기 때문) - 요청받지 않은 것을
            // 모델이 자발적으로 다 옮기길 기대할 수 없다. ROUND 3인자 호출은 현재
            // 코퍼스 함수 12건 전부 0건이라 지금 당장 실패를 만들지는 않지만, 함수
            // 본문에서도 ROUND(x, n, 1) 호출은 문법적으로 가능하므로 재료가 생기는
            // 순간 같은 결함이 재현된다.
            //
            // 세션 옵션·헤더/EXEC 모순 체크리스트는 여기 넣지 않는다 - 그 둘이 함수에서
            // 왜 구조적으로/도메인상 항상 비는지는 위 DML 범위 표 블록의 주석에 근거와
            // 함께 적어 뒀다(이번 라운드에서 DML 범위 표 쪽 "구조적으로 불가능하다"는
            // 주장이 틀린 것으로 드러났으므로, 같은 판단을 두 곳에 따로 적어 두면 한쪽만
            // 고쳐질 위험이 생긴다 - 근거는 한 곳에만 둔다).
            var checklistSb = new StringBuilder();
            var sourceComments = SourceCommentExtractor.Extract(functionDef.DdlText);
            var roundingCalls = RoundingSemanticsExtractor.Extract(functionDef.DdlText);
            if (sourceComments.Count > 0 || roundingCalls.Count > 0)
            {
                checklistSb.AppendLine();
                checklistSb.AppendLine("🎯 [최종 작성 전 필수 검증 체크리스트]");

                if (sourceComments.Count > 0)
                {
                    checklistSb.AppendLine(
                        $"- [ ] 원본 DDL의 주석 {sourceComments.Count}건(비실행 조건·코드 범례·헤더 선언)을 "
                        + "본문에 기록하셨습니까? 조건식 원문·도입 일자·사유를 그대로 옮기고, "
                        + "\"실행되지 않습니다\" 한 문장으로 대신하지 마십시오. 대조 대상:");
                    foreach (var block in sourceComments)
                    {
                        checklistSb.AppendLine($"      * (라인 {block.Line}) {block.Text}");
                    }
                }

                if (roundingCalls.Count > 0)
                {
                    checklistSb.AppendLine(
                        $"- [ ] 원본의 3인자 ROUND 호출 {roundingCalls.Count}건에 대해 "
                        + $"{RoundingSemanticsExtractor.SemanticsSentence} "
                        + "이 값 매핑을 명세서에 기술하셨습니까? \"반올림 또는 절사\"처럼 "
                        + "어느 값이 어느 동작인지 흐리게 적지 마십시오.");
                }
            }

            var userPrompt = $@"
<user-defined-function-context>
  <basic-info>
    <schema>{functionDef.Schema}</schema>
    <name>{functionDef.Name}</name>
  </basic-info>
  <return-contract>
{returnContract}
  </return-contract>
  <dependencies>
{dependenciesText}  </dependencies>
  <referenced-table-schemas>
{tableSchemasText}  </referenced-table-schemas>
  <referenced-function-ddl-source-code>
{referenceDdlsText}  </referenced-function-ddl-source-code>
  <static-analysis-metadata>
{staticAnalysisText}  </static-analysis-metadata>
  <function-source-ddl>
```sql
{functionDef.DdlText}
```
  </function-source-ddl>
</user-defined-function-context>

Based on the reference context above, reverse engineer the user defined function and write the Korean markdown specification.
{checklistSb}";

            if (!string.IsNullOrWhiteSpace(feedbackLog))
            {
                userPrompt += $"\n\n[VALIDATION CORRECTION FEEDBACK]\n{feedbackLog}\nCorrect the documented function contract, formulas, referenced objects, and TVF schema without introducing regressions.";
            }

            return (systemPrompt, userPrompt);
        }

        private static string FormatFunctionReturnColumn(ColumnInfo column) =>
            $"- {column.ColumnName}: {column.DataType} ({(column.IsNullable ? "nullable" : "not nullable")})";

        /// <summary>
        /// 통합 배치 계획서의 SQL 안전성 규칙과 few-shot 예시.
        ///
        /// 골격 생성과 단계 본문 생성이 같은 규칙을 써야 한다. 문구가 갈라지면
        /// 단계마다 다른 오류 처리·트랜잭션 관례가 나오고, 그것이 정확히 이
        /// 파이프라인이 없애려는 결함이다.
        ///
        /// 보간 문자열이 아니다. 이 블록에는 치환할 값이 없고, 상수로 두어야
        /// SQL 예시의 중괄호를 이스케이프할 필요가 없다.
        ///
        /// 2026-08-18 수술 근거(감사 실측):
        /// - 규칙 5: 구 문구가 `@pi_bypassPreCheck`라는 우회 파라미터 발명을
        ///   명령했고, S02가 재시작 모드에서 그 값을 실행 컨텍스트 전체에 참으로
        ///   고정해 지급 확정 원장의 -9 하드 스톱이 통째로 사라졌다(🔴). 재시작
        ///   스킵을 오케스트레이터(`batch.BatchCheckpoint`)로 옮기고, 단계 인터페이스
        ///   확장과 사전 검증 가드의 조건부 비활성화를 둘 다 금지한다.
        /// - 규칙 4/11: 그림자 테이블을 기본값처럼 넓게 권하면서 생성 시점·복원
        ///   범위·EXEC() 스코프를 말하지 않아, 트랜잭션 하나로 끝나는 단계에서도
        ///   불필요한 그림자와 중복 DELETE가 나왔다. 마지막 수단으로 좁히고, 쓸 때는
        ///   (a) BEGIN TRAN 앞 생성 (b) 같은 범위만 복원 (c) EXEC() 안 외부 변수
        ///   참조 금지·sp_executesql 매개변수화 세 역학을 필수로 못박는다. 규칙 11은
        ///   규칙 4에 흡수했으므로 짧게 남긴다.
        /// - Few-Shot CATCH: `THROW;`로 끝나 규칙 6-1(상태 변수 반환)과 규칙 13
        ///   (출력 파라미터 매핑)을 무력화했다 - 모델은 산문 규칙보다 코드 예시를
        ///   따른다는 것이 실측 5건에서 재현됐다. 반환 경로로 바꾸고, 그림자 복원도
        ///   실제로 캡처됐을 때만 수행하도록 규칙 4와 맞췄다.
        /// - 규칙 2: 검증 SQL이 두 집계를 `CROSS JOIN`으로 비교하면 카티션 곱이
        ///   각 변을 상대 변의 행수만큼 부풀려 정상 데이터에서도 검증이 실패하고
        ///   기대/실제 금액이 그 배수만큼 과대 계상된다. 각 변을 독립 서브쿼리/CTE로
        ///   집계한 뒤 스칼라 두 개를 비교하도록 못박는다.
        ///
        /// 2026-08-18 수술 후속 보강(코드 리뷰 Important 2건 원상 복구):
        /// - 규칙 4 (d) 다중 테이블 커버리지: 위 수술이 "그림자 전략은 단계가
        ///   수정하는 모든 대상 테이블을 커버해야 한다"는 옛 (a) 지시를 대체 없이
        ///   지웠다. 계획서가 규칙 4를 다시 쓴 의도는 그림자를 마지막 수단으로
        ///   좁히는 것이었지 이 지시를 버리는 것이 아니었으므로, 그림자를 쓰기로
        ///   한 가지 안에 되살린다. 일부 테이블만 덮으면 복원이 반쪽짜리가 되어
        ///   롤백을 아예 안 한 것보다 더 나쁜 불일치 상태를 만든다.
        /// - 규칙 4 (e) 퍼지 정책: 같은 수술이 "저장 용량 전략과 퍼지 정책(예:
        ///   24시간 후 자동 삭제)을 정의해야 한다"는 옛 (b) 지시도 대체 없이
        ///   지웠다. 그림자 테이블은 batch_shadow 스키마에 계속 쌓이는 물리
        ///   객체이므로, 수명·정리 지시 없이 두면 저장 공간을 영구히 잠식한다.
        ///   같은 가지에 되살린다.
        /// - 둘 다 "그림자를 쓰기로 한 경우"에만 적용됨을 문장 끝에 명시해,
        ///   규칙 4의 새 기조(그림자는 마지막 수단)와 모순되지 않게 했다.
        /// - Few-Shot의 "Shadow Table Swap Pattern" 예시에 `DECLARE`와
        ///   `SET @v_shadowCaptured = 1`을 추가했다(코드 리뷰 Minor 원상 복구).
        ///   CATCH 블록 예시가 이 변수를 선언 없이 참조하고 있었는데, 이는 이
        ///   축이 정확히 잡으려는 결함(미선언 변수 참조)을 프롬프트 스스로
        ///   가르치는 꼴이었다. 그림자를 실제로 캡처한 직후(벌크 INSERT 다음)에
        ///   1로 세워, 두 Few-Shot 예시가 같은 변수를 일관되게 쓰도록 맞췄다.
        ///
        /// 2026-08-27 3단계 - 규칙 본문에서 T-SQL 철자를 벗긴다
        /// (설계서 `docs/superpowers/specs/2026-08-27-stage3-rule-rewrite-design.md` §2):
        /// - 새 규칙 3-1(SQL 거처)이 이 축의 본체다. 신규 저장 프로시저를 만들지 않고
        ///   트랜잭션·오류 처리를 앱이 소유한다. 실측 근거: 겨누는 C# 앱이 `src/`에
        ///   없는데도 계획서 20편이 신규 SP를 0~18개로 흩어 정의했다.
        /// - 규칙은 **의무만 정하고 API는 정하지 않는다.** `SqlTransaction`이든
        ///   `TransactionScope`든 고르지 않는다 - 고르면 존재하지 않는 계약을 지어내는
        ///   것이 된다. 그래서 생성물의 C# 모양은 계속 제각각이다(의도된 결과).
        /// - 규칙 4는 (a)(그림자를 롤백될 수 있는 트랜잭션 밖에서 먼저)와 (c)(값을
        ///   파라미터로, 문자열로 잇지 않는다)만 다시 썼다. `ALTER DATABASE …
        ///   READ_COMMITTED_SNAPSHOT ON` 금지는 DB 수준 지시라 그대로 둔다.
        /// - 규칙 6-1은 실패 지점 충실도만 남겼다. 원본 코드 재사용은 규칙 9와
        ///   `CheckLegacyReturnCodeBinding`이 이미 덮는다. **이 조항에는 L1 기계 강제가
        ///   없다** - 그것을 강제하던 `CheckStepIdInitialValue`가 T-SQL 구문
        ///   (`DECLARE @v_currentStepId INT = 0`)에 묶여 있어 언어 이전 뒤 침묵한다.
        ///   의도된 상태이며 "검사가 빠졌다"로 읽지 말 것(설계서 §2-2의 정정 상자).
        /// - 채점 기준(`ReviewConsolidatedPlanAsync`)을 같은 회차에 옮겼다. 규칙만
        ///   바꾸고 채점을 두면 자가 수정이 새 규칙을 따른 계획서를 옛 T-SQL 모양으로
        ///   되돌린다 - `CriticCriteriaCoverageTests`가 지키는 짝이다.
        /// - **Few-Shot 예시는 T-SQL 그대로 둔다.** 이관 대상 레거시가 T-SQL이고,
        ///   설계서 §2가 손대기로 한 것은 규칙 넷과 규칙 2 한 줄뿐이다. 그래서
        ///   "규칙에 `BEGIN TRAN`이 없다"는 가드는 시스템 프롬프트 전체가 아니라
        ///   규칙 블록만 잘라 판정한다(`AiServiceTests_Rich.RulesBlockAsync`).
        ///
        /// [4단계 1차 통제군의 회신 - 2026-08-29]
        /// - **`GOTO` 금지를 복원했다.** 3단계는 `TRY...CATCH`·`GOTO` 조항을 "C# 예외
        ///   처리로 자동 대체된다"고 보고 규칙 6-1과 채점 기준에서 함께 뺐다. 그 전제가
        ///   틀렸다 - 통제군 한 편(`POQSettleBatch2`)이 `IF @@ERROR <> 0 GOTO ERR_HANDLER`
        ///   를 21번 냈다(같은 축의 기준선 `POQSettleBatch1`은 1번). 전송된 프롬프트에
        ///   `GOTO`는 0건이었으므로 모델이 스스로 되돌린 것이다. **다만 Critic은 눈이
        ///   멀지 않았다** — 명시 조항이 없는 채로 규칙 3-1에서 파생해 "SQL 의사코드
        ///   안에 TRY/CATCH·트랜잭션 제어를 두어 정책과 충돌한다"를 지적했다. 못 고친
        ///   이유는 조항 부재가 아니라 그 판이 `claude-cli` 연속 실패로 죽어 4차 수정
        ///   회차가 못 돈 것이다(52/100 구제 채택). 그래도 이 조항을 두는 값은 남는다 —
        ///   파생에 기대던 것을 결정적으로 만든다. 이번에는 규칙 6-1이 아니라
        ///   **규칙 3-1(SQL 거처)**에
        ///   붙였다 - 이것은 오류 코드 충실도가 아니라 제어 흐름의 거처 문제다.
        /// - 규칙 4·11과 채점 기준의 `ROLLBACK TRAN`·`CATCH block` 철자를 걷어냈다.
        ///   3단계가 "§2의 표에 없어 남긴다"고 미뤄 둔 잔존이다. 이제 규칙 블록에
        ///   T-SQL 트랜잭션 철자가 하나도 남지 않는다(`_DropTheTsqlSpelling…`이 고정).
        /// - `SET TRANSACTION ISOLATION LEVEL`을 단계 SQL에 직접 쓰는 것에 채점 감점을
        ///   달았다. 규칙 4는 "거는 자리를 정하지 말라"고만 했고 채점은 그 반대편
        ///   (안 적었다고 감점하지 말라)만 막고 있어, 적어 버린 쪽이 무주공산이었다.
        /// - `DataAccessPolicy`의 `SET XACT_ABORT ON; SET TRANSACTION ISOLATION LEVEL
        ///   SNAPSHOT;`은 **이 경로가 아니다.** 그것은 `InstructionBundleWriter`·
        ///   `InstructionEntryPointComposer`(에이전트 번들)와 검증기가 쓴다. 계획서
        ///   프롬프트로 새지 않으므로 여기서 건드리지 않았다.
        /// - **Few-Shot 예시는 이번에도 그대로다.** 통제군 산출물이 여전히 T-SQL 스크립트
        ///   모양인 것(```sql 49 대 ```csharp 8)의 남은 용의자는 이쪽이다. 규칙만으로
        ///   모양이 바뀌는지 다음 통제군에서 먼저 보고 판단한다.
        ///
        /// [4단계 2차 통제군의 회신 - 2026-08-29] 설계서 §10
        /// - 제어 흐름 조항은 **먹었다.** 같은 Actor(`claude-sonnet-5`)로 규칙만 바꾸니
        ///   `GOTO` 20→0 · `IF @@ERROR` 18→0 · `BEGIN TRY`/`END CATCH` 2→0 ·
        ///   `sp_executesql` 6→0. 그 판은 완주했고 Critic이 통과시켰다.
        /// - **규칙 3-1의 API 금지만 안 지켜졌다.** 계획서가 `SqlConnection`·`SqlCommand`·
        ///   `IsolationLevel.Snapshot`을 그대로 썼고 **Critic이 보고도 통과시켰다** —
        ///   격리 감점을 「단계 SQL에 쓰지 마라」로만 적어 앱 코드 쪽이 무주공산이었다.
        ///   그래서 이번 회차에 셋을 함께 넣었다: (a) 3-1이 금지 실물을 열거하고
        ///   (.NET과 Java 양쪽 — 이 도구는 `targetLanguage`로 Java도 겨눈다),
        ///   (b) **일반 자리표시자는 옳다고 명시**하고(금지만 적으면 표현 수단이 없어져
        ///   T-SQL 철자로 후퇴한다 — 2차 통제군의 S13이 실제로 그 길로 갔다),
        ///   (c) **표기 일관성**을 새 의무로 세웠다. (c)는 규칙에 없던 것이라 채점에만
        ///   넣으면 Actor가 듣지도 못한 것으로 감점당한다 — 3단계가 겪은 짝 깨짐의
        ///   정반대 방향이라 규칙과 채점을 같은 회차에 옮겼다.
        /// </summary>
        /// <summary>
        /// 배치 전용 객체의 스키마 규약. 본문 생성 세 경로와 목차 생성이 같은 문장을
        /// 받도록 여기 한 번만 적는다.
        ///
        /// 목차에도 거는 이유: PlanStructureEnricher는 LegacyProcedures로 정적 분석
        /// 결과를 찾아 TargetTables를 교체하므로, 레거시 출신이 없는 신규 단계(잠금·저널·
        /// 대사·종료)에서는 교체가 일어나지 않는다. 그 단계들이 다루는 것이 정확히 배치
        /// 제어 객체라, 규칙이 가장 필요한 자리에 사후 교정 장치가 없다 - 프롬프트가
        /// 유일한 방어선이다(실측 POQSettleProc12: 목차가 dbo.TBatchRun을 선언하고
        /// 본문은 batch.BatchRun을 써 다섯 단계가 하한 미달로 걸렸다).
        /// </summary>
        private const string BatchObjectSchemaRule = @"[Batch Object Schema] Every NEW batch-only object you introduce (staging table, journal, checkpoint, control-total table) MUST live in the `batch` schema, and every shadow table MUST live in the `batch_shadow` schema. NEVER invent a job-named schema such as `poqbatch`, `poqsettlebatch`, or `<JobName>Batch`. The bootstrap round builds its list of objects to create by scanning for exactly these two schema names, so an object placed under any other schema is never created and every step referencing it breaks at runtime. Existing business tables keep their real schema (`dbo`, `PaymentDB.dbo`, ...) - this rule governs new objects only. This rule governs new TABLES; rule 3-1 forbids new stored procedures outright, so a `batch`-schema procedure is not an option this rule opens. The execution journal, run lock, and checkpoint tables are covered by this rule and are the ones most often gotten wrong: if one step writes them under `dbo` while other steps read them under `batch`, one logical table becomes two physical ones and restart silently skips or repeats work. Every step MUST spell these objects with the same schema. Because the shadow name carries the run identifier, assembling the shadow table name at runtime is expected and correct - build it as `N'batch_shadow.<Table>_' + <run id expression> + N'_<StepCode>'` and do NOT write a blanket statement such as `this step does not assemble table names dynamically`, which contradicts the naming rule. Business tables are the opposite: never assemble their names - spell them literally.";

        private const string ConsolidatedPlanRules = @"[Required Content & Rules]
1. Write the document in Korean Markdown format.
2. The document must use only 4 mandatory H2 headers:
   - ## 통합 배치 아키텍처 개요: Define how the individual stored procedures translate into steps (sequential chain, conditional branches, parallel processing) within the unified batch job.
   - ## Mermaid 기반 통합 흐름도: Draw a Mermaid flowchart diagram depicting the data pipeline and steps.
     * Wrap all node text labels in double quotes. Do not use double quotes or special characters in arrow labels. Node IDs must be unique alphanumeric words.
     * ALWAYS add a space between the 'subgraph' keyword, its ID, and the bracket label (e.g., `subgraph SP1 [""Label""]`). Do not write `subgraph SP1[""Label""]`. Do not use parentheses '()' or brackets '[]' in arrow labels (e.g., use `|OutState IN 1, 5|` instead of `|OutState IN (1,5)|`).
   - ## 단계별 이행 상세 및 의사코드: Design the classes/components, chunk paging pseudocode, locks/transaction controls, and error restartability/recovery strategies.
     * The pseudocode here is the batch application's code, not a stored procedure body. Write each step's SQL as the statements the application sends, and everything around them - control flow, transaction boundaries, error handling - as target-language code (see rule 3-1).
   - ## 통합 데이터 정합성 검증 SQL 세트: Include validation SQL templates checking data integrity.
     * NEVER compare two aggregates with `CROSS JOIN`. A cartesian product multiplies each side by the other side's row count, so the comparison fails on correct data and the recorded expected/actual amounts are inflated by that factor. Aggregate each side independently in its own subquery or CTE, then compare the two scalars.
3. [Concurrency & Execution Order] Strictly preserve the sequential execution order of the original stored procedures. Do NOT propose parallel execution for steps that perform DML on the same target table, as it causes data consistency conflicts.
3-1. [SQL Placement] The step logic belongs to the target-language batch application, not to the database. Do NOT define any NEW stored procedure, function, or trigger for this batch - SQL appears only as statements the application sends. Transaction boundaries and error handling are owned by the application code. The ONLY place `CREATE PROCEDURE` may appear in this document is where you quote the ORIGINAL legacy procedure being replaced. New batch-only TABLES are still expected and are governed by rule 4-1. State what the code must guarantee; do NOT prescribe a specific API, class, or framework type for transactions, connections, or error handling - the implementing round chooses the mechanism. Control flow belongs to the application, not to the statements it sends: a statement MUST NOT branch on its own outcome. Do NOT write `GOTO` error labels, `IF @@ERROR <> 0` checks, or a `BEGIN TRY`/`END CATCH` wrapper into the step's own SQL - the application observes the failure and decides what happens next. These may appear ONLY inside a quotation of the original legacy procedure. NEVER name a type from a real data-access framework - `SqlConnection`, `SqlCommand`, `SqlParameter`, `SqlTransaction`, `IsolationLevel.Snapshot`, `TransactionScope`, `DbContext`, `PreparedStatement`, `EntityManager` and their kin are all off limits, because the batch application they would belong to does not exist yet and naming one invents a contract nobody signed. Generic placeholder names ARE correct and expected - `conn.beginTransaction()`, `connectionFactory.open()`, `repository.execute(...)` show the shape without pinning a mechanism. Use ONE such notation for the WHOLE document: if the common design calls a thing by one invented name and a step calls the same thing by another, the implementing round must reconcile two fictional APIs instead of one.
4. [Transaction Isolation & Shadow Table] NEVER propose `ALTER DATABASE SET READ_COMMITTED_SNAPSHOT ON` as it is too risky. Every step MUST run under SNAPSHOT isolation - state that obligation for the step, and do NOT prescribe where or how the setting is issued. Shadow tables are a LAST RESORT, not a default: if the step's work fits in a single transaction, let that single transaction roll back and write NO shadow table and NO compensating DELETE in the failure path - the rollback has already restored those rows, so deleting them again afterwards destroys data that was never lost. Only when the step commits in chunks or rebuilds an aggregate (so a rollback cannot restore it) may you use a shadow, and then all of the following mechanics are mandatory: (a) create the shadow OUTSIDE the transaction that can roll back, and before that transaction begins - a shadow created inside the transaction disappears with the rollback and the restore then fails on a missing object; (b) the restore MUST delete exactly the same range the step deleted - NEVER `DELETE FROM Target` without a `WHERE`, which discards rows belonging to other business dates; (c) NEVER build a statement by pasting a value into its text - pass every value as a parameter. A value concatenated into the statement text makes a different statement on every run, so it can be neither bound to the specification nor checked, and a value that needs quoting silently changes what the statement does; (d) if the step modifies MULTIPLE target tables, the shadow strategy MUST cover ALL of them - restoring only some of the tables leaves the step half-rolled-back, a worse inconsistency than no restore at all; (e) define the shadow table's storage lifetime and purge policy (for example, auto-drop it after 24 hours) so it does not permanently consume storage. Mechanics (d) and (e) apply only to steps that actually use a shadow - a step that stays on the single-transaction rollback default above needs neither.
4-1. " + BatchObjectSchemaRule + @"
5. [Idempotency & Restartability] Restart skipping happens OUTSIDE the step. The orchestrator reads `batch.BatchCheckpoint` and simply does not call a step whose checkpoint is already `Succeeded`. Therefore a step MUST NOT add an input parameter for restart, skipping, or bypassing - its interface is exactly the parameter list given in the `[Original Procedure Interface]` table. The original pre-validation guards (for example a `-9` abort when a settled ledger row exists) MUST run unconditionally on every call; NEVER place them inside a conditional a caller can switch off. A step that is called is a step that does its full work, guards included.
6. [Data Modification & Error Handling] When chunking a DELETE-INSERT pattern, you MUST ensure the chunking key is added to the DELETE filter to prevent full-table deletion conflicts. If the step involves multi-table aggregations (`GROUP BY`) or complex cross-DB joins where chunking by a single Primary Key is mathematically impossible, explicitly declare that the step uses 'Single-Transaction Shadow Swap' instead of chunking, and DO NOT add fake chunk keys to the pseudo-code.
6-1. [Precise Error Tracking] The step MUST be able to name the exact statement that failed. Keep a step-local state variable, update it immediately BEFORE each DML statement with that statement's original error code, and record that variable when the step fails, so the failure point reaches `batch.BatchStepJournal.LegacyReturnCode` instead of a single generic failure value. A statement that fails MUST NOT leave a partial commit behind - the step either completes its unit of work or leaves the target untouched. Which codes to use is rule 9's subject, not this rule's.
6-2. " + ControlStepErrorCodes.PromptClause + @"
7. [Anti-Shortcut for Business Logic] You MUST NOT simplify queries that use UNION, UNION ALL, or complex JOINs across multiple tables. Preserve all source tables and aggregation formulas in the pseudocode and descriptions.
7-1. [UNION Branch Alignment] When you combine branches with `UNION ALL`, every branch MUST project the same column list in the same order - a set operator requires it and SQL Server rejects the statement otherwise. This includes the constant columns that distinguish the branches: if the source marks a full transaction with `0 AS USESTATE`, the partial-cancel and refund branches MUST each carry their own literal (`2 AS USESTATE`, `3 AS USESTATE`) rather than omitting the column. Copy every discriminator value from the specification; do not leave one branch to inherit another's.
8. [Preserve Chunking Filters] When chunking operations (e.g., `WHERE ID BETWEEN @start AND @end`), you MUST retain the original business logic filters (e.g., self-joins, cursor criteria, status checks) and combine them with the chunking range using `AND`. Do not delete the original filters.
8-1. [Chunk Transaction Boundary] Every iteration of a chunking loop MUST open and close its own transaction boundary so that each chunk commits independently and a mid-run failure leaves earlier chunks durably committed. Do NOT wrap the entire loop in a single outer transaction.
9. [Error Codes] A step that replaces a legacy procedure MUST strictly reuse the EXACT original error codes from the source SPs; for that step, DO NOT remap or invent new error codes (e.g., changing -1 to -11), and DO NOT use a continuous range (e.g., `-1~-23`). Steps with no legacy origin follow rule 6-2 instead, whose reserved block (e.g., `-9160..-9169`) is the one place in this document where a continuous range is correct.
10. [NOLOCK Prohibition] Since SNAPSHOT isolation is used, you MUST explicitly remove all `WITH (NOLOCK)` or `NOLOCK` hints from the generated pseudocode. `NOLOCK` forces READ UNCOMMITTED and completely violates the SNAPSHOT isolation policy.
11. [INSERT-only Rollback] For INSERT-only steps, rely on rolling back the single transaction, or on an explicit `DELETE WHERE [ChunkKey]` compensation for chunked ones. See rule 4 - no shadow table.
12. [Chunk Key Validation] You MUST CROSS-CHECK the provided DDL/Schema for the target table before writing the chunking key. Ensure the key column (e.g., `CLIENTID`) actually exists. If it doesn't exist, use an alternative primary key or composite hash (e.g., `PGNAME+MALLID`) that strictly exists in the target schema.
13. [Output Parameters Interface] All output parameters (e.g., `@po_strErrMsg`, `@po_intRetVal`) from the original procedures MUST be accurately mapped in the unified batch context interface and error code tables. Do not omit them.
14. Do not wrap the entire response in a markdown code block. However, you MUST use ```mermaid blocks for flowcharts.
15. Do not append any conversational filler, polite greetings, or unrelated explanations at the end. Terminate immediately.

[Few-Shot Examples for Modernization Patterns]
These examples have TWO layers. The OUTER layer is the batch application's code - loops,
transaction boundaries, error observation - written as language-neutral pseudocode because
rule 3-1 forbids pinning a specific API or framework type. The INNER layer is the SQL the
application sends, unchanged T-SQL. Keep every SQL statement in its own ```sql block and
have the application layer reference it by name: a statement buried inside application
pseudocode is invisible to the tools that read this document, and a step whose DML cannot
be read cannot be checked against its specification.

* Shadow Table Pattern (ONLY for a rebuild that commits in chunks - see rule 4):
```pseudocode
// Why this step may use a shadow at all: it rebuilds one business date and COMMITS the
// rebuild in chunks, so rolling back puts back only the failing chunk and the rows the
// earlier chunks destroyed are already gone. A step that finishes in ONE transaction must
// NOT use a shadow and must NOT compensate afterwards - the rollback already restored it,
// and deleting those rows again destroys data that was never lost.

// The run identifier is NOT a parameter of this step. A step's interface is exactly the
// original procedure's parameter list (rule 5), so read the identifier from the control
// table instead of adding an input. Spell the job name literally: it is the Unified Batch
// Job Name given at the top of this prompt, and it is a constant of this document.
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: <this document's job name>, p_ymd: batchYmd })

// (rule 4a) Create and fill the shadow BEFORE opening the transaction that can roll back.
// Created inside that transaction it would disappear with the rollback and the restore
// would then fail on a missing object.
// The shadow NAME is assembled inside the statement, not here - the bootstrap round scans
// the SQL for that assembled shape to learn which shadow tables to create (rule 4-1), and
// a name assembled anywhere else is never seen and never created.
execute(SQL_CREATE_AND_CAPTURE_SHADOW, { p_runId: runId, p_batchDate: batchYmd })
shadowCaptured = true

// (rule 4b) Destroy exactly the range this step owns, in its own transaction.
beginTransaction()
execute(SQL_DELETE_RANGE, { p_batchDate: batchYmd })
commit()

// Rebuild in chunks. Each chunk opens and closes its OWN transaction (rule 8-1) so a
// mid-run failure leaves the earlier chunks durably committed. The chunk key must be a
// column that actually exists in the target schema (rule 12).
FOR EACH chunk IN chunkRanges(SQL_CHUNK_BOUNDS, { p_batchDate: batchYmd }, size: 10000):
    beginTransaction()
    execute(SQL_INSERT_CHUNK, { p_batchDate: batchYmd, p_from: chunk.from, p_to: chunk.to })
    commit()

// (rule 4e) State the shadow's lifetime where you describe the step, e.g. the bootstrap
// purge job drops batch_shadow tables older than 24 hours.
```
The statements that loop sends. The shadow holds the rows this step is ABOUT TO DESTROY -
it is a backup, not a staging area for the new rows. Copy them whole (`SELECT *`), never a
subset of columns: a shadow row missing the range key cannot be found by that key when the
restore looks for it.
```sql
-- SQL_CURRENT_RUN_ID
SELECT RunId FROM batch.BatchRun
 WHERE JobName = @p_jobName AND BatchYmd = @p_ymd AND RunStatus = N'Running';

-- SQL_CREATE_AND_CAPTURE_SHADOW
-- Spell the shadow name in exactly this shape: a literal prefix ending in '_', the run id
-- expression, then a literal N'_<StepCode>'. The bootstrap round scans for this shape to
-- learn which shadow tables to create, and a name built any other way is never created.
-- NEVER write a placeholder token literally: a name spelled with _RunId_ in it creates a
-- table physically named that, which every run then shares.
DECLARE @v_shadow NVARCHAR(300) =
    N'batch_shadow.TargetTable_' + CAST(@p_runId AS NVARCHAR(20)) + N'_S13';
DECLARE @v_sql NVARCHAR(MAX);
-- (rule 4c) A dynamic batch is a separate scope, so VALUES are passed as parameters and
-- never referenced inside the dynamic text. Only the table NAME is interpolated - a table
-- name cannot be bound as a parameter.
SET @v_sql = N'SELECT * INTO ' + @v_shadow + N' FROM dbo.TargetTable WHERE 1 = 0;';
EXEC sp_executesql @v_sql;
SET @v_sql = N'INSERT INTO ' + @v_shadow + N' SELECT * FROM dbo.TargetTable WHERE BatchDate = @p_batchDate;';
EXEC sp_executesql @v_sql, N'@p_batchDate CHAR(8)', @p_batchDate = @p_batchDate;

-- SQL_DELETE_RANGE - never `DELETE FROM Target` without a WHERE, which discards rows
-- belonging to other business dates (rule 4b)
DELETE FROM dbo.TargetTable WHERE BatchDate = @p_batchDate;

-- SQL_CHUNK_BOUNDS
SELECT MIN(Col1), MAX(Col1) FROM dbo.SourceTable WHERE BatchDate = @p_batchDate;

-- SQL_INSERT_CHUNK
INSERT INTO dbo.TargetTable (BatchDate, Col1, Col2)
SELECT @p_batchDate, Col1, SUM(Col2) FROM dbo.SourceTable
 WHERE BatchDate = @p_batchDate
   AND Col1 >= @p_from AND Col1 < @p_to
 GROUP BY Col1;
```

* Chunking Pattern (Combining chunking keys with existing business filters):
```pseudocode
FOR EACH chunk IN chunkRanges(SQL_ID_BOUNDS, {}, size: 10000):
    beginTransaction()
    execute(SQL_COPY_CHUNK, { p_from: chunk.from, p_to: chunk.to })
    commit()
```
```sql
-- SQL_ID_BOUNDS - the original business filter belongs here too, or the bounds cover rows
-- the copy will never touch
SELECT MIN(ID), MAX(ID) FROM SourceTable WHERE Status = 'P';

-- SQL_COPY_CHUNK
INSERT INTO TargetTable (ID, Col1)
SELECT ID, Col1 FROM SourceTable
 WHERE Status = 'P'                          -- Preserve original filter!
   AND ID >= @p_from AND ID < @p_to;         -- Chunking condition
```

* Failure path for the chunk-committed rebuild above (NOT for a single-transaction step):
```pseudocode
ON FAILURE observed by the application:
    // Roll back the transaction that is still open. That rollback undid only the chunk that
    // failed. The chunks committed before it are durable, and the DELETE that emptied the
    // range is durable too - which is exactly why this step captured a shadow. A step that
    // runs in ONE transaction reaches this point fully restored and MUST NOT run any of the
    // following (rule 4).
    rollbackIfOpen()

    IF shadowCaptured:
        beginTransaction()
        // Restore is DELETE first, then INSERT. Re-inserting without the preceding delete
        // duplicates every row the earlier chunks already committed.
        execute(SQL_RESTORE_DELETE, { p_batchDate: batchYmd })
        // Same assembled name as the capture above - the shadow has no literal name to
        // spell. It holds only this step's range, so it is restored whole.
        execute(SQL_RESTORE_INSERT)
        commit()

    // Record the tracked failure code BEFORE the failure leaves this step (rules 6-1, 13).
    // Letting the exception propagate untouched loses the code the step worked to track,
    // and the journal then shows one generic failure for every statement in the step.
    writeStepJournal(runId, StepCode, status: ""Failed"", LegacyReturnCode: currentStepErrorCode)
    stop the pipeline
```
```sql
-- SQL_RESTORE_DELETE
DELETE FROM dbo.TargetTable WHERE BatchDate = @p_batchDate;

-- SQL_RESTORE_INSERT - same assembled name as the capture above
SET @v_sql = N'INSERT INTO dbo.TargetTable SELECT * FROM ' + @v_shadow + N';';
EXEC sp_executesql @v_sql;
```

* INSERT-only Compensation (No Shadow table needed for rollback):
```sql
-- If an INSERT-only chunked batch fails in the middle, roll back committed chunks using
-- business keys - no shadow, no restore:
DELETE FROM TargetTable WHERE BatchDate = @p_batchDate AND ProcessStatus = 'NEW';
```";

        private ReviewResult ParseReviewResult(string? responseContent, string contextName)
        {
            try
            {
                var jsonString = ExtractJson(responseContent ?? string.Empty);
                Log.Debug("[추출된 JSON 내용]: {JsonString}", jsonString);

                using (var resultDoc = JsonDocument.Parse(jsonString))
                {
                    var resultRoot = resultDoc.RootElement;
                    var hasDefects = resultRoot.GetProperty("HasDefects").GetBoolean();
                    var feedbackComment = resultRoot.TryGetProperty("FeedbackComment", out var commentProp) ? commentProp.GetString() : null;

                    var scoreAccuracy = resultRoot.TryGetProperty("ScoreAccuracy", out var accProp) ? accProp.GetInt32() : 0;
                    var scoreCrud = resultRoot.TryGetProperty("ScoreCrud", out var crudProp) ? crudProp.GetInt32() : 0;
                    var scoreInterface = resultRoot.TryGetProperty("ScoreInterface", out var intfProp) ? intfProp.GetInt32() : 0;
                    var scoreException = resultRoot.TryGetProperty("ScoreException", out var exProp) ? exProp.GetInt32() : 0;
                    var scoreReadability = resultRoot.TryGetProperty("ScoreReadability", out var readProp) ? readProp.GetInt32() : 0;

                    var defectiveSteps = new List<string>();
                    if (resultRoot.TryGetProperty("DefectiveSteps", out var stepsProp) &&
                        stepsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in stepsProp.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String) continue;
                            var code = item.GetString();
                            if (!string.IsNullOrWhiteSpace(code)) defectiveSteps.Add(code.Trim());
                        }
                    }

                    return new ReviewResult
                    {
                        HasDefects = hasDefects,
                        FeedbackComment = feedbackComment,
                        DefectiveSteps = defectiveSteps,
                        ScoreAccuracy = scoreAccuracy,
                        ScoreCrud = scoreCrud,
                        ScoreInterface = scoreInterface,
                        ScoreException = scoreException,
                        ScoreReadability = scoreReadability
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "JSON 검토 보고서 파싱 중 오류 발생 ({Context})", contextName);
                return new ReviewResult
                {
                    HasDefects = true,
                    FeedbackComment = $"JSON 검토 보고서 파싱 실패: {ex.Message}",
                    ScoreAccuracy = 0,
                    ScoreCrud = 0,
                    ScoreInterface = 0,
                    ScoreException = 0,
                    ScoreReadability = 0
                };
            }
        }

        public async Task<AiResult> GenerateSpecificationAsync(SpDefinition spDef, string userInstructions, string? feedbackLog = null, string? effort = null, CancellationToken cancellationToken = default)
        {
            var (systemPrompt, userPrompt) = BuildSpecificationPrompts(spDef, userInstructions, feedbackLog);

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                // Gemma 4 계열 모델의 추론(Thinking)을 강제 활성화하기 위해 시스템 프롬프트 첫 부분에 제어 토큰 삽입
                if (!string.IsNullOrEmpty(ModelName) && ModelName.IndexOf("gemma4", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    systemPrompt = "<|think|>" + systemPrompt;
                }
                systemPrompt += "\n\n[Ollama 추론 유도 규칙]\n- 최종 답변을 작성하기 전에, 반드시 분석 단계와 생각 흐름을 <think>와 </think> 태그 또는 Gemma 4 표준 출력 포맷으로 상세히 기술하십시오. 최종 분석 명세서는 반드시 해당 태그 바깥에 작성해야 합니다.";
            }

            Log.Information("AI 명세서 생성 요청 전송 - SP: {Schema}.{Name}, Effort: {Effort}", spDef.Schema, spDef.Name, effort ?? "Default");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, _temperature, effort, cancellationToken: cancellationToken);
            if (aiResult == null)
            {
                aiResult = new AiResult();
            }
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt;

            Log.Information("AI 명세서 생성 응답 수신 완료 - SP: {Schema}.{Name}, 응답 길이: {Length}", spDef.Schema, spDef.Name, aiResult.Content.Length);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult.Content);

            return aiResult;
        }

        public async Task<AiResult> DeconstructSpLogicAsync(SpDefinition spDef, string userInstructions, string? feedbackLog = null, string? effort = null, CancellationToken cancellationToken = default, Action<(int current, int total, string message)>? progressCallback = null)
        {
            if (_enableAstChunking && ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName))
            {
                return await DeconstructSpLogicWithChunkingAsync(spDef, userInstructions, feedbackLog, effort, cancellationToken, progressCallback);
            }

            return await DeconstructSpLogicMonolithicAsync(spDef, userInstructions, feedbackLog, effort, cancellationToken);
        }

        private async Task<AiResult> DeconstructSpLogicMonolithicAsync(SpDefinition spDef, string userInstructions, string? feedbackLog, string? effort, CancellationToken cancellationToken)
        {
            var (systemPrompt, userPrompt) = BuildDeconstructionPrompts(spDef, userInstructions, feedbackLog);

            // 로컬 Ollama 구동 시 1단계(추론/추출) 온도는 0.05로 고정하여 엄격하고 결정론적인 JSON 출력을 유도합니다.
            float temp = _temperature;
            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName))
            {
                temp = 0.05f;
            }

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                if (!string.IsNullOrEmpty(ModelName) && ModelName.IndexOf("gemma4", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    systemPrompt = "<|think|>" + systemPrompt;
                }
                systemPrompt += "\n\n[Ollama 추론 유도 규칙]\n- 최종 답변을 작성하기 전에, 반드시 분석 단계와 생각 흐름을 <think>와 </think> 태그 영역에 상세히 기술하십시오. 최종 JSON은 반드시 추론 태그 바깥에 작성해야 합니다.";
            }

            Log.Information("AI 명세 구조화 추론(Stage 1) 요청 전송 - SP: {Schema}.{Name}, Temperature: {Temp}", spDef.Schema, spDef.Name, temp);
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, temp, effort, cancellationToken: cancellationToken);
            if (aiResult == null)
            {
                aiResult = new AiResult();
            }
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt;

            Log.Information("AI 명세 구조화 추론(Stage 1) 응답 수신 완료 - SP: {Schema}.{Name}, 응답 길이: {Length}", spDef.Schema, spDef.Name, aiResult.Content.Length);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult.Content);

            return aiResult;
        }

        private async Task<AiResult> DeconstructSpLogicWithChunkingAsync(SpDefinition spDef, string userInstructions, string? feedbackLog, string? effort, CancellationToken cancellationToken, Action<(int current, int total, string message)>? progressCallback = null)
        {
            Log.Information("Ollama 로컬 LLM 전용 분할(AST Chunking) 파이프라인 진입 - SP: {Schema}.{Name}", spDef.Schema, spDef.Name);
            
            progressCallback?.Invoke((0, 0, "SQL 구문 분석 및 청크 분할 중..."));
            var parser = new SqlStaticParser();
            var chunks = parser.ExtractStatementChunks(spDef.DdlText);

            if (chunks.Count == 0)
            {
                Log.Warning("분할된 청크가 없습니다. Monolithic 파이프라인으로 폴백합니다.");
                return await DeconstructSpLogicMonolithicAsync(spDef, userInstructions, feedbackLog, effort, cancellationToken);
            }

            var chunkResults = new List<DeconstructedSpLogic>();
            var consolidatedThinking = new StringBuilder();

            // 1. Setup Chunk Cache Directory
            var chunkCacheDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "output", "Procedures", $"{spDef.Schema}.{spDef.Name}", "raw", "chunks");
            if (!System.IO.Directory.Exists(chunkCacheDir))
            {
                System.IO.Directory.CreateDirectory(chunkCacheDir);
            }

            // 2. Heuristic Matching (Extract keywords from feedbackLog)
            var uppercaseKeywords = new HashSet<string>();
            bool isAttempt2OrLater = !string.IsNullOrWhiteSpace(feedbackLog);
            if (isAttempt2OrLater)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(feedbackLog!, @"[A-Z0-9_]{3,}");
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    uppercaseKeywords.Add(match.Value);
                }
            }

            // 3. Determine which chunks need regeneration
            var chunksToRegen = new HashSet<int>();
            if (isAttempt2OrLater)
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    bool needsRegen = false;
                    foreach (var kw in uppercaseKeywords)
                    {
                        if (chunks[i].StatementText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        {
                            needsRegen = true;
                            break;
                        }
                    }
                    if (needsRegen) chunksToRegen.Add(i);
                }
                
                // Fallback: Regenerate all if 0 matches
                if (chunksToRegen.Count == 0)
                {
                    Log.Information("피드백과 매칭되는 청크가 없어 전체 청크 재생성으로 폴백합니다.");
                    for (int i = 0; i < chunks.Count; i++) chunksToRegen.Add(i);
                }
            }
            else
            {
                for (int i = 0; i < chunks.Count; i++) chunksToRegen.Add(i);
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var cacheFilePath = System.IO.Path.Combine(chunkCacheDir, $"chunk_{i}.json");

                // 4. Try loading from cache if this chunk doesn't need regeneration
                if (!chunksToRegen.Contains(i) && System.IO.File.Exists(cacheFilePath))
                {
                    progressCallback?.Invoke((i + 1, chunks.Count, $"[Sub-task] 청크 캐시 재사용 ({i + 1}/{chunks.Count})"));
                    try
                    {
                        var json = await System.IO.File.ReadAllTextAsync(cacheFilePath, cancellationToken);
                        var options = new System.Text.Json.JsonSerializerOptions 
                        { 
                            PropertyNameCaseInsensitive = true,
                            AllowTrailingCommas = true,
                            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip
                        };
                        var cachedLogic = System.Text.Json.JsonSerializer.Deserialize<DeconstructedSpLogic>(json, options);
                        if (cachedLogic != null)
                        {
                            chunkResults.Add(cachedLogic);
                            Log.Information("청크 {Index} 기존 분석 결과 캐시 재사용", i + 1);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "청크 {Index} 캐시 읽기 실패, 재생성으로 폴백합니다.", i + 1);
                    }
                }

                progressCallback?.Invoke((i + 1, chunks.Count, $"[Sub-task] 청크 분석 중 ({i + 1}/{chunks.Count})"));

                // RAG 필터링된 프롬프트 생성
                var (sys, usr) = BuildChunkDeconstructionPrompts(spDef, chunk, userInstructions, feedbackLog);
                
                float temp = 0.05f;
                if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
                {
                    if (!string.IsNullOrEmpty(ModelName) && ModelName.IndexOf("gemma4", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sys = "<|think|>" + sys;
                    }
                    sys += "\n\n[Ollama 추론 유도 규칙]\n- 최종 답변을 작성하기 전에, 반드시 분석 단계와 생각 흐름을 <think>와 </think> 태그 영역에 상세히 기술하십시오. 최종 JSON은 반드시 추론 태그 바깥에 작성해야 합니다.";
                }

                var aiResult = await _aiClient.ChatAsync(sys, usr, temp, effort, cancellationToken: cancellationToken);
                
                if (aiResult != null && !string.IsNullOrWhiteSpace(aiResult.Content))
                {
                    consolidatedThinking.AppendLine(aiResult.ThinkingText);
                    
                    try
                    {
                        string cleanJson = aiResult.Content.Trim();
                        // AI 응답에 서론/결론 등 불필요한 텍스트가 포함되어 있을 수 있으므로 JSON 블록 추출
                        int firstBrace = cleanJson.IndexOf('{');
                        int lastBrace = cleanJson.LastIndexOf('}');
                        if (firstBrace >= 0 && lastBrace > firstBrace)
                        {
                            cleanJson = cleanJson.Substring(firstBrace, lastBrace - firstBrace + 1).Trim();
                        }

                        var options = new System.Text.Json.JsonSerializerOptions 
                        { 
                            PropertyNameCaseInsensitive = true,
                            AllowTrailingCommas = true,
                            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip
                        };
                        var chunkLogic = System.Text.Json.JsonSerializer.Deserialize<DeconstructedSpLogic>(cleanJson, options);
                        if (chunkLogic != null) 
                        {
                            chunkResults.Add(chunkLogic);
                            
                            // 5. Save generated result to cache
                            try
                            {
                                await System.IO.File.WriteAllTextAsync(cacheFilePath, cleanJson, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "청크 {Index} 캐시 파일 저장 실패", i + 1);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "개별 청크 JSON 역직렬화 실패. 원본 내용: {Content}", aiResult.Content);
                    }
                }
            }

            var globalOverview = new SpOverviewInfo 
            { 
                SpName = spDef.Name, 
                Purpose = "AST 기반 분할 청크 분석 결과 병합본 (자동 추출)",
                BusinessRole = "세부 로직 흐름 파악용",
                ResultStyle = "내부 로직 참조"
            };
            
            var globalParams = new List<SpParameterInfo>();
            if (spDef.StaticAnalysis?.ProcedureParameters != null)
            {
                foreach (var p in spDef.StaticAnalysis.ProcedureParameters)
                {
                    var parts = p.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    string name = parts.Length > 0 ? parts[0] : p;
                    string type = parts.Length > 1 ? parts[1] : "Unknown";
                    
                    globalParams.Add(new SpParameterInfo
                    {
                        Name = name,
                        DataType = type,
                        Nullability = "확인불가(AST 추출)",
                        Purpose = "정적 파싱(AST)을 통해 자동 식별된 파라미터",
                        IsOutput = p.Contains("OUTPUT", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }

            var consolidator = new LocalAiConsolidator();
            var finalLogic = consolidator.Consolidate(chunkResults, globalOverview, globalParams);

            var finalJson = System.Text.Json.JsonSerializer.Serialize(finalLogic, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            return new AiResult 
            { 
                Content = finalJson,
                ThinkingText = consolidatedThinking.ToString(),
                SystemPrompt = "AST Chunking Pipeline Used",
                UserPrompt = "Multiple sequential calls were made."
            };
        }

        private (string SystemPrompt, string UserPrompt) BuildChunkDeconstructionPrompts(SpDefinition spDef, ChunkAnalysisResult chunk, string userInstructions, string? feedbackLog = null)
        {
            var (_, _, _, staticAnalysisText) = BuildSpMetadataTexts(spDef);

            var ragSchemas = new StringBuilder();
            if (chunk.ReferencedTables.Count > 0 && spDef.Dependencies != null)
            {
                foreach (var refTable in chunk.ReferencedTables)
                {
                    var cleanRefTable = refTable.Replace("[", "").Replace("]", "");
                    foreach (var dep in spDef.Dependencies)
                    {
                        var depFullName = string.IsNullOrEmpty(dep.Database)
                            ? $"{dep.Schema}.{dep.Name}"
                            : $"{dep.Database}.{dep.Schema}.{dep.Name}";

                        if (depFullName.Contains(cleanRefTable, StringComparison.OrdinalIgnoreCase) || 
                            cleanRefTable.Contains(dep.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            ragSchemas.AppendLine(FormatTableSchemaToMarkdown(dep, spDef));
                            ragSchemas.AppendLine();
                            break;
                        }
                    }
                }
            }
            
            var symbolContext = new StringBuilder();
            if (spDef.StaticAnalysis != null)
            {
                if (spDef.StaticAnalysis.ProcedureParameters.Count > 0)
                {
                    symbolContext.AppendLine("[Procedure Parameters]");
                    foreach (var p in spDef.StaticAnalysis.ProcedureParameters) symbolContext.AppendLine($"- {p}");
                    symbolContext.AppendLine();
                }
                if (spDef.StaticAnalysis.DeclaredVariables.Count > 0)
                {
                    symbolContext.AppendLine("[Declared Variables]");
                    foreach (var v in spDef.StaticAnalysis.DeclaredVariables) symbolContext.AppendLine($"- {v}");
                    symbolContext.AppendLine();
                }
            }

            var systemPrompt = @"You are a database architect. Analyze the provided single SQL statement and output a JSON object representing its logic.
[Rules]
1. You MUST respond ONLY with a strictly valid JSON object matching the JSON schema below.
2. Do NOT wrap the JSON output in markdown code blocks (e.g. do NOT use ```json ... ```). Output the raw JSON text directly.
3. Every target column in INSERT/UPDATE statements MUST be listed in the mapping lists without omission. For 'SourceExpression', you MUST extract the EXACT string/formula from the SQL or use the exact expression from `<static-analysis-metadata>`. Do NOT hallucinate, summarize, or invent values (e.g., do not replace a variable with `CAST(0 AS INT)` unless it explicitly exists in the SQL branch).
4. If there are UNION/UNION ALL blocks, or multiple IF branches updating/deleting the same table, separate them by setting a distinct 'BranchName' (e.g. '전체거래건', '부분취소건', '환불건') and detail their mappings separately.
5. Identify all referenced User Defined Functions (UDFs) by name and calling location only. Do NOT detail their formulas, return values, or internal logic - that belongs in each function's own specification, not here.
6. The output must be written in Korean for descriptive string fields (like Purpose, BusinessRole, Description, StepDescription).
7. Ensure all table names, column names, parameter names are spelled exactly as in the DDL. No abbreviations.
8. If multiple tables are joined in a single SELECT query, you MUST create a separate JSON object in the `SelectTables` array for EACH individual physical table. Do NOT group them together like ""TableA, TableB"".
9. In `Logic.Steps`, explicitly capture any critical branching conditions involving `@@ROWCOUNT` or `EXISTS` checks (e.g., updating existing records vs inserting new ones).
10. In `Visualization.Nodes`, do NOT use ""END"" as a Node Id. Use ""PROC_END"" instead to avoid Mermaid keyword conflicts.
11. Focus only on the provided SQL statement chunk.

[JSON Schema Structure]
{
  ""Overview"": {
    ""SpName"": ""string"",
    ""Purpose"": ""string (Korean)"",
    ""BusinessRole"": ""string (Korean)"",
    ""ResultStyle"": ""string (Korean, e.g. Rowset/Non-rowset return behavior)""
  },
  ""Parameters"": [
    {
      ""Name"": ""string (including @)"",
      ""DataType"": ""string"",
      ""Nullability"": ""string"",
      ""Purpose"": ""string (Korean)"",
      ""IsOutput"": false
    }
  ],
  ""Crud"": {
    ""SelectTables"": [
      {
        ""TableName"": ""string"",
        ""ReferencedColumns"": [""string""],
        ""JoinAndFilterConditions"": [""string""]
      }
    ],
    ""InsertTables"": [
      {
        ""TargetTable"": ""string"",
        ""BranchName"": ""string (Korean, e.g. 전체거래건)"",
        ""Mappings"": [
          {
            ""TargetColumn"": ""string"",
            ""SourceExpression"": ""string"",
            ""Description"": ""string (Korean)""
          }
        ]
      }
    ],
    ""UpdateTables"": [
      {
        ""TargetTable"": ""string"",
        ""BranchName"": ""string (Korean, e.g. 전체거래건)"",
        ""Mappings"": [
          {
            ""TargetColumn"": ""string"",
            ""SourceExpression"": ""string"",
            ""Description"": ""string (Korean)""
          }
        ]
      }
    ],
    ""DeleteTables"": [
      {
        ""TableName"": ""string"",
        ""BranchName"": ""string (Korean, e.g. 전체거래건)"",
        ""FilterConditions"": [""string""]
      }
    ],
    ""Udfs"": [
      {
        ""UdfName"": ""string"",
        ""CallingLocation"": ""string"",
        ""Purpose"": ""string (Korean)"",
        ""ComputationLogic"": ""string""
      }
    ],
    ""HasTempTables"": false,
    ""TempTablesUsage"": ""string"",
    ""HasLinkedServers"": false,
    ""LinkedServersUsage"": ""string""
  },
  ""Logic"": {
    ""TransactionControl"": ""string (Korean)"",
    ""Steps"": [
      {
        ""StepNumber"": 1,
        ""StepName"": ""string"",
        ""StepDescription"": ""string (Korean)""
      }
    ],
    ""ExceptionVulnerabilities"": [
      {
        ""VulnerabilityType"": ""string"",
        ""Details"": ""string (Korean)""
      }
    ],
    ""IsolationImplications"": [
      {
        ""RiskType"": ""string"",
        ""Details"": ""string (Korean)""
      }
    ],
    ""ReturnCodes"": [""string""],
    ""ParameterValidation"": [""string (Korean)""]
  },
  ""Visualization"": {
    ""Nodes"": [
      {
        ""Id"": ""string (unique uppercase)"",
        ""Label"": ""string (Korean, no @)""
      }
    ],
    ""Links"": [
      {
        ""FromId"": ""string"",
        ""ToId"": ""string"",
        ""Condition"": ""string (Korean, no quotes)""
      }
    ]
  }
}";
            
            var userPromptSb = new StringBuilder();
            userPromptSb.AppendLine("[Global Symbol Context (Read-Only)]");
            userPromptSb.AppendLine("Use this to understand the variables and parameters used in the chunk.");
            if (symbolContext.Length > 0)
            {
                userPromptSb.AppendLine(symbolContext.ToString());
            }
            else
            {
                userPromptSb.AppendLine("No parameters or variables detected.\n");
            }

            if (ragSchemas.Length > 0)
            {
                userPromptSb.AppendLine("[Referenced Table Schemas (RAG)]");
                userPromptSb.AppendLine(ragSchemas.ToString());
            }

            if (!string.IsNullOrEmpty(staticAnalysisText))
            {
                userPromptSb.AppendLine();
                userPromptSb.AppendLine("  <static-analysis-metadata>");
                userPromptSb.AppendLine(staticAnalysisText.Trim());
                userPromptSb.AppendLine("  </static-analysis-metadata>");
            }

            userPromptSb.AppendLine();
            userPromptSb.AppendLine("[SQL Statement Chunk]");
            userPromptSb.AppendLine("```sql");
            userPromptSb.AppendLine(chunk.StatementText);
            userPromptSb.AppendLine("```");
            userPromptSb.AppendLine();
            userPromptSb.AppendLine("[User Instructions]");
            userPromptSb.AppendLine(userInstructions);

            if (!string.IsNullOrWhiteSpace(feedbackLog))
            {
                userPromptSb.AppendLine();
                userPromptSb.AppendLine("[L2 Critic Feedback to Fix (Critical)]");
                userPromptSb.AppendLine(feedbackLog);
            }

            return (systemPrompt, userPromptSb.ToString());
        }

        private (string SystemPrompt, string UserPrompt) BuildDeconstructionPrompts(SpDefinition spDef, string userInstructions, string? feedbackLog = null)
        {
            var (dependenciesText, tableSchemasText, referenceDdlsText, staticAnalysisText) = BuildSpMetadataTexts(spDef);

            var systemPrompt = @"You are a principal database architect. Your task is to analyze the provided SQL Server Stored Procedure and output a structured JSON object representing its entire logical structure, schema mappings, and transaction flows.

[Rules]
1. You MUST respond ONLY with a strictly valid JSON object matching the JSON schema below.
2. Do NOT wrap the JSON output in markdown code blocks (e.g. do NOT use ```json ... ```). Output the raw JSON text directly.
3. Every target column in INSERT/UPDATE statements MUST be listed in the mapping lists without omission. For 'SourceExpression', you MUST extract the EXACT string/formula from the SQL or use the exact expression from `<static-analysis-metadata>`. Do NOT hallucinate, summarize, or invent values.
4. If there are UNION/UNION ALL blocks in the DML statements, or multiple IF branches updating/deleting the same table, separate them by setting a distinct 'BranchName' (e.g. '전체거래건', '부분취소건', '환불건') and detail their mappings separately.
5. Identify all referenced User Defined Functions (UDFs) by name and calling location only. Do NOT detail their formulas, return values, or internal logic - that belongs in each function's own specification, not here.
6. The output must be written in Korean for descriptive string fields (like Purpose, BusinessRole, Description, StepDescription).
7. Ensure all table names, column names, parameter names are spelled exactly as in the DDL. No abbreviations.
8. If multiple tables are joined in a single SELECT query, you MUST create a separate JSON object in the `SelectTables` array for EACH individual physical table. Do NOT group them together like ""TableA, TableB"".
9. In `Logic.Steps`, explicitly capture any critical branching conditions involving `@@ROWCOUNT` or `EXISTS` checks (e.g., updating existing records vs inserting new ones).
10. In `Visualization.Nodes`, do NOT use ""END"" as a Node Id. Use ""PROC_END"" instead to avoid Mermaid keyword conflicts.
11. Ensure the logical flow for prophylactic rollbacks (e.g., `IF @@TRANCOUNT <> 0 ROLLBACK` at the start) correctly routes to the next step (e.g., `BEGIN TRAN`), rather than pointing directly to the end of the procedure.

[JSON Schema Structure]
{
  ""Overview"": {
    ""SpName"": ""string"",
    ""Purpose"": ""string (Korean)"",
    ""BusinessRole"": ""string (Korean)"",
    ""ResultStyle"": ""string (Korean, e.g. Rowset/Non-rowset return behavior)""
  },
  ""Parameters"": [
    {
      ""Name"": ""string (including @)"",
      ""DataType"": ""string"",
      ""Nullability"": ""string"",
      ""Purpose"": ""string (Korean)"",
      ""IsOutput"": false
    }
  ],
  ""Crud"": {
    ""SelectTables"": [
      {
        ""TableName"": ""string"",
        ""ReferencedColumns"": [""string""],
        ""JoinAndFilterConditions"": [""string""]
      }
    ],
    ""InsertTables"": [
      {
        ""TargetTable"": ""string"",
        ""BranchName"": ""string (Korean, e.g. 전체거래건)"",
        ""Mappings"": [
          {
            ""TargetColumn"": ""string"",
            ""SourceExpression"": ""string"",
            ""Description"": ""string (Korean)""
          }
        ]
      }
    ],
    ""UpdateTables"": [
      {
        ""TargetTable"": ""string"",
        ""BranchName"": ""string (Korean, e.g. 전체거래건)"",
        ""Mappings"": [
          {
            ""TargetColumn"": ""string"",
            ""SourceExpression"": ""string"",
            ""Description"": ""string (Korean)""
          }
        ]
      }
    ],
    ""DeleteTables"": [
      {
        ""TableName"": ""string"",
        ""BranchName"": ""string (Korean, e.g. 전체거래건)"",
        ""FilterConditions"": [""string""]
      }
    ],
    ""Udfs"": [
      {
        ""UdfName"": ""string"",
        ""CallingLocation"": ""string"",
        ""Purpose"": ""string (Korean)"",
        ""ComputationLogic"": ""string""
      }
    ],
    ""HasTempTables"": false,
    ""TempTablesUsage"": ""string"",
    ""HasLinkedServers"": false,
    ""LinkedServersUsage"": ""string""
  },
  ""Logic"": {
    ""TransactionControl"": ""string (Korean)"",
    ""Steps"": [
      {
        ""StepNumber"": 1,
        ""StepName"": ""string"",
        ""StepDescription"": ""string (Korean)""
      }
    ],
    ""ExceptionVulnerabilities"": [
      {
        ""VulnerabilityType"": ""string"",
        ""Details"": ""string (Korean)""
      }
    ],
    ""IsolationImplications"": [
      {
        ""RiskType"": ""string"",
        ""Details"": ""string (Korean)""
      }
    ],
    ""ReturnCodes"": [""string""],
    ""ParameterValidation"": [""string (Korean)""]
  },
  ""Visualization"": {
    ""Nodes"": [
      {
        ""Id"": ""string (unique uppercase)"",
        ""Label"": ""string (Korean, no @)""
      }
    ],
    ""Links"": [
      {
        ""FromId"": ""string"",
        ""ToId"": ""string"",
        ""Condition"": ""string (Korean, no quotes)""
      }
    ]
  }
}";

            var promptSb = new StringBuilder();
            promptSb.AppendLine("<stored-procedure-context>");
            promptSb.AppendLine("  <basic-info>");
            promptSb.AppendLine($"    <schema>{spDef.Schema}</schema>");
            promptSb.AppendLine($"    <name>{spDef.Name}</name>");
            promptSb.AppendLine("  </basic-info>");
            promptSb.AppendLine();
            promptSb.AppendLine("  <dependencies>");
            promptSb.AppendLine(dependenciesText.Trim());
            promptSb.AppendLine("  </dependencies>");
            promptSb.AppendLine();
            promptSb.AppendLine("  <referenced-table-schemas>");
            promptSb.AppendLine(tableSchemasText.Trim());
            promptSb.AppendLine("  </referenced-table-schemas>");

            if (!string.IsNullOrEmpty(referenceDdlsText))
            {
                promptSb.AppendLine();
                promptSb.AppendLine("  <referenced-ddl-source-code>");
                promptSb.AppendLine(referenceDdlsText.Trim());
                promptSb.AppendLine("  </referenced-ddl-source-code>");
            }

            if (!string.IsNullOrEmpty(staticAnalysisText))
            {
                promptSb.AppendLine();
                promptSb.AppendLine("  <static-analysis-metadata>");
                promptSb.AppendLine(staticAnalysisText.Trim());
                promptSb.AppendLine("  </static-analysis-metadata>");
            }

            promptSb.AppendLine();
            promptSb.AppendLine("  <sp-source-ddl>");
            promptSb.AppendLine("```sql");
            promptSb.AppendLine(spDef.DdlText.Trim());
            promptSb.AppendLine("```");
            promptSb.AppendLine("  </sp-source-ddl>");
            promptSb.AppendLine("</stored-procedure-context>");
            promptSb.AppendLine();
            promptSb.AppendLine("Output strictly valid JSON according to the schema. Do not include markdown wraps.");

            if (!string.IsNullOrEmpty(feedbackLog))
            {
                promptSb.AppendLine();
                promptSb.AppendLine($"[CRITIC CORRECTION FEEDBACK LOG]:\n{feedbackLog}\n\nPlease fix the JSON data based on this feedback log. Make sure to keep the schema strictly valid.");
            }

            return (systemPrompt, promptSb.ToString());
        }

        public async Task<AiResult> GenerateSpecSectionAsync(SpDefinition spDef, string sectionType, string userInstructions, string? feedbackLog = null, string? effort = null, CancellationToken cancellationToken = default)
        {
            var (systemPrompt, userPrompt) = BuildSpecSectionPrompts(spDef, sectionType, userInstructions, feedbackLog);

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                if (!string.IsNullOrEmpty(ModelName) && ModelName.IndexOf("gemma4", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    systemPrompt = "<|think|>" + systemPrompt;
                }
                systemPrompt += "\n\n[Ollama 추론 유도 규칙]\n- 최종 답변을 작성하기 전에, 반드시 분석 단계와 생각 흐름을 <think>와 </think> 태그 또는 Gemma 4 표준 출력 포맷으로 상세히 기술하십시오. 최종 분석 명세서는 반드시 해당 태그 바깥에 작성해야 합니다.";
            }

            Log.Information("AI 명세서 구역 분할 생성 요청 전송 - SP: {Schema}.{Name}, Section: {Section}, Effort: {Effort}", spDef.Schema, spDef.Name, sectionType, effort ?? "Default");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, _temperature, effort, cancellationToken: cancellationToken);
            if (aiResult == null)
            {
                aiResult = new AiResult();
            }
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt;

            Log.Information("AI 명세서 구역 분할 생성 응답 수신 완료 - SP: {Schema}.{Name}, Section: {Section}, 응답 길이: {Length}", spDef.Schema, spDef.Name, sectionType, aiResult.Content.Length);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult.Content);

            return aiResult;
        }

        private (string SystemPrompt, string UserPrompt) BuildSpecSectionPrompts(SpDefinition spDef, string sectionType, string userInstructions, string? feedbackLog = null)
        {
            var (dependenciesText, tableSchemasText, referenceDdlsText, staticAnalysisText) = BuildSpMetadataTexts(spDef);

            // Pruning 정보 수집
            bool hasUdf = spDef.StaticAnalysis?.ReferencedFunctions?.Count > 0;
            bool hasLinkedServers = spDef.StaticAnalysis?.LinkedServerReferences?.Count > 0;
            bool hasDynamicSql = spDef.DdlText.Contains("EXEC", StringComparison.OrdinalIgnoreCase) || 
                                 spDef.DdlText.Contains("EXECUTE", StringComparison.OrdinalIgnoreCase) || 
                                 spDef.DdlText.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase);

            bool hasMissingDescription = false;
            if (spDef.Dependencies != null)
            {
                foreach (var dep in spDef.Dependencies)
                {
                    if (dep.Columns != null)
                    {
                        foreach (var col in dep.Columns)
                        {
                            if (col.IsDescriptionMissing)
                            {
                                hasMissingDescription = true;
                                break;
                            }
                        }
                    }
                    if (hasMissingDescription) break;
                }
            }

            string systemPrompt = "";
            string checklistText = "";

            if (sectionType == "OverviewAndParameters")
            {
                var sbRules = new List<string>
                {
                    "You are an expert SQL Server Stored Procedure analyzer. Write ONLY the [## 개요] and [## 파라미터 목록] sections of the markdown specification.",
                    "",
                    "[Rules]",
                    "1. The document must use only H2 headers: `## 개요` and `## 파라미터 목록` in this exact order. Terminate the output immediately after them. Do not include any other H2 headers."
                };

                int rIdx = 2;
                sbRules.Add($"{rIdx++}. In ## 파라미터 목록, detail all parameters defined in the DDL including their data type, nullability (state '명시 없음' if not defined), purpose, and whether they are OUTPUT parameters in a table format. Do not arbitrarily assume 'NOT NULL'.");
                sbRules.Add($"{rIdx++}. In ## 파라미터 목록 and throughout the document, all table headers and column names must use correct and pure Korean (e.g., '매개변수 명칭', '파라미터명', '데이터 타입', 'Null 여부'). Do NOT mix foreign characters or Chinese/Japanese characters (e.g., do NOT use '매개参数' or '매개変数').");
                sbRules.Add($"{rIdx++}. Clearly state whether this procedure returns a result set (Rowset). If the return behavior is unmanaged or depends on initial values, explicitly describe the caller's initialization responsibility or prerequisites.");
                sbRules.Add($"{rIdx++}. 소스코드 DDL 내에 명시적으로 상숫값(예: RETURN -5)이 지정되어 있지 않은 에러 반환 단계(예: IF @@ERROR <> 0 분기)에 대해 임의로 -1, -2 등 순차적인 숫자를 창작하여 단정적으로 기술하지 마십시오. 근거가 없는 값은 반드시 '실패 시 에러 코드 반환(값 정의 미비로 추정)' 등으로 서술하여 환각을 원천 배제하십시오.");
                // 배선 지점 2/2(객체 선언) - `## 개요` 소속이고 재료가 있을 때만
                // (fact != null) 싣는다. 프로시저에는 WITH 옵션 자체가 없으므로
                // ObjectDeclarationExtractor.Extract가 항상 null을 내고, 이 가드가
                // 자연히 표를 억제한다 - 이 분기의 실제 호출자
                // (VerificationPipelineOrchestrator)는 ObjectType == Procedure일 때만
                // 이 경로를 타지만(Extract가 null을 내는 이유와 별개로), 배선 자체는
                // 재료 유무만으로 판단해야 다른 경로(함수)가 이 분기를 타게 되어도
                // 조용히 깨지지 않는다.
                var objectDeclarationForOverview = ObjectDeclarationExtractor.Extract(spDef.DdlText);
                if (objectDeclarationForOverview != null)
                {
                    sbRules.AddRange(BuildObjectDeclarationTableLines(objectDeclarationForOverview));
                }

                // 배선 지점 3(잠금 힌트 참고 재료) - 2026-08-21 최종 브랜치 리뷰
                // 재라운드 ①. `## 개요`가 잠금 힌트를 서술할 의무를 지는 별도 규칙은
                // 없지만, 실제로 감사 🟡이 난 자리가 여기다(Spec.md:33, "UP_Util_
                // Settle_Summary_AcqManual"의 커서 `NOLOCK` 서술 - BuildLockHintReference
                // MaterialLines 문서 참고). 개요 산문이 스캔 방식을 요약하며 언급할 수
                // 있으므로, 이 절도 근거 재료를 받아야 한다.
                var lockHintsForOverview = DmlScopeExtractor.ExtractLockHints(spDef.DdlText);
                if (lockHintsForOverview.Count > 0)
                {
                    sbRules.AddRange(BuildLockHintReferenceMaterialLines(lockHintsForOverview));
                }

                // 배선 지점 4(실행 의미 참고 재료) - Task 17, 최종 브랜치 리뷰 2차.
                // 이 갈래를 고치기 전에는 `BuildMachineFactBlockLines`의 호출부가
                // 네 곳(SP 전체·함수·CrudAnalysis·LogicAndVisualization)뿐이었고
                // 그중 이 갈래(`OverviewAndParameters`)는 없었다. 결함 E(F1 무리 -
                // StaticAnalysis가 이미 확정한 "크로스 DB 참조 아님"을 명세서가
                // "단언할 수 없습니다"로 되짚은 사고)의 실제 앵커가 `## 개요` 절
                // 안에서 확인됐다(UF_Get_CLComm4MobileCo의 Spec.md,
                // DatabasePlacementExtractor 문서 참고) - 이 갈래도 근거 재료를
                // 받아야 한다. 지금은 이 갈래가 다섯 번째 호출부다.
                //
                // [단일 진입점을 통해서 준다 - Important, 최종 브랜치 리뷰 2차]
                // 처음에는 `BuildExecutionSemanticsReferenceMaterialLines`를 직접
                // 불러 이 갈래를 `BuildMachineFactBlockLines` 밖에서 배선했다 -
                // 그 함수가 CASE 분기 사실도 함께 실어서였다. 그런데 그러면 설계
                // D5가 경고한 그 모양대로 진입점이 둘이 되고, `BuildMachineFactBlockLines`의
                // 문서를 고치는 사람 눈에는 이 갈래가 안 보인다 - 다음에 여섯 번째
                // 사실 종류가 그 함수에 추가되면 이 갈래만 조용히 못 받는다.
                // `MachineFactPresentation`을 3상태(Table/Reference/Omit)로 넓혀
                // 그 문제를 구조로 막는다 - 실행 의미는 `Reference`(개요 산문이
                // 서술할 수 있으나 `## CRUD 분석`은 안 쓴다), CASE 분기는 `Omit`
                // (CASE는 `## 로직 흐름 요약` 소관이고 감사 🟡이 난 자리도 DB 배치뿐,
                // 재료를 늘리면 프롬프트만 길어지고 모델이 산만해질 뿐 대응하는 결함이
                // 없다).
                sbRules.AddRange(BuildMachineFactBlockLines(
                    spDef,
                    executionSemanticsPresentation: MachineFactPresentation.Reference,
                    caseBranchPresentation: MachineFactPresentation.Omit,
                    uncoveredNoticePresentation: MachineFactPresentation.Reference,
                    localVariablePresentation: MachineFactPresentation.Table));

                sbRules.Add($"{rIdx++}. Do not append any conversational filler, polite greetings, or unrelated explanations at the end. Terminate immediately.");
                sbRules.Add($"{rIdx++}. Do not wrap the entire response in a markdown code block.");
                sbRules.Add("");
                sbRules.Add("[Output Language Requirement]");
                sbRules.Add("- You MUST write the final markdown specification in Korean.");

                systemPrompt = string.Join("\n", sbRules);
                systemPrompt += $"\n\n[USER INSTRUCTIONS]\n{userInstructions}";

                checklistText = @"🎯 [필수 검증 체크리스트]
- [ ] '## 개요' 및 '## 파라미터 목록' 헤더가 명확하게 작성되었습니까?
- [ ] 출력 파라미터의 역할 및 결과셋 반환 여부를 명확히 명시하셨습니까?";
            }
            else if (sectionType == "CrudAnalysis")
            {
                var sbRules = new List<string>
                {
                    "You are an expert SQL Server Stored Procedure analyzer. Write ONLY the [## CRUD 분석] section of the markdown specification.",
                    "",
                    "[Rules]",
                    "1. The document must use only one H2 header: `## CRUD 분석`. Terminate immediately after writing this section. Do not include any other H2 headers.",
                    "2. State all physical tables affected by SELECT, INSERT, UPDATE, DELETE in a clear Markdown Table format. Do NOT use bullet points or lists.",
                    "   - You must NEVER skip or declare a referenced UDF as 'not called' or 'excluded from analysis' if it is present in the dependency list and used in the DDL. State its calling location and arguments (e.g., UF_GET_ROUND4VAT, UF_GET_INCVTAXRATE) - do NOT describe any function's behaviour or document its computation; that belongs only in the function's own Spec.md.",
                    "   - For INSERT/UPDATE operations, you must list EVERY single column mapped in the INSERT/UPDATE statement (e.g. CLVT, PGVT, CLTOTAL, etc.). Omission of any target column is considered a critical failure.",
                    "   - You must separate SELECT tables, INSERT tables, UPDATE tables, and DELETE tables into their own respective sub-sections with separate Markdown tables. Do not mix them in a single table.",
                    "   - You must separate SELECT tables into individual rows. If the source JSON groups multiple tables in one string, you MUST manually separate them into distinct rows in the Markdown table, mapping only the specific columns referenced for each respective table.",
                    "   - Detail the column names referenced and join/filter keys without abbreviation.",
                    "   - The 'referenced-columns-per-table' in the static analysis metadata is the Source of Truth. Map these columns exactly without omitting any. Double-check all table and column names to ensure there are no spelling typos or hallucinations (e.g., use 'SeperateRate' and 'COMMISSIONCANCELAMT' exactly as defined in the source schema/DDL instead of hallucinated forms like 'SerateRate' or 'COMMATIONCANCELAMT').",
                    "3. For target tables of INSERT/UPDATE operations, map all target columns to their source values (variables, constants, function results, etc.) in a 1:1 mapping table. Do not abbreviate with 'etc.' or '...'.",
                    "4. Factually state the use case of temp tables (#TempTable), User Defined Functions (UDF), and Linked Servers. If not used, explicitly write that they are not used.",
                    "5. NEVER include columns in the CRUD table that do not exist in the provided schema metadata. If a column appears in the DDL but is missing from the schema, do not guess it as a normal column; mark it as a schema mismatch.",
                    "6. Table names in the static analysis metadata are PARSER-NORMALIZED three-part names, not the source's own notation. When you describe how many parts the source identifier has (one-part, two-part, three-part, cross-database, Linked Server), base it ONLY on <sp-source-ddl>. Do not claim a cross-database or three-part reference that does not appear there."
                };

                int rIdx = 7;
                if (hasUdf)
                {
                    // A1의 「분석하라」를 여기서도 뒤집는다 - :328(BuildSpecificationPrompts)과
                    // 같은 이유(2026-08-20 축 A 교차 대조, 위 주석 참고)다. 이 분기가 지역
                    // 모델의 SP 명세서 최초 생성 경로라 여기를 놓치면 지역 모델은 함수
                    // 서술 금지 계약을 한 번도 받지 못한다.
                    sbRules.Add($"{rIdx++}. When a referenced User Defined Function (UDF) is called, state only its calling location and arguments, and do NOT describe any function's behaviour - return value, branches, filters, defaults, or rounding - anywhere in this document. That belongs only in the function's own Spec.md, which the machine-derived 참조 함수 table links to.");
                }
                if (hasDynamicSql)
                {
                    sbRules.Add($"{rIdx++}. If dynamic SQL is present, identify its purpose and target tables, and reflect them in the CRUD analysis.");
                }
                if (hasLinkedServers)
                {
                    sbRules.Add($"{rIdx++}. If Linked Server references (4-part identifier) are found, analyze the external DB dependencies. If it is a cross-database reference on the same server (3-part identifier), distinguish it clearly from a Linked Server.");
                }

                // 지역 모델 경로(이 분기)도 BuildSpecificationPrompts와 같은 UPDATE fill-in
                // 템플릿을 받아야 한다. L1(VerificationPipelineOrchestrator)이 `### UPDATE
                // 대상 테이블:` 접두 H3 헤딩의 존재를 강제하는데, 이 분기가 그 헤딩을 자발적으로
                // 쓰도록 요구하지 않으면 지역 모델의 1차 시도가 구조적으로 실패한다.
                var updateMappingsForCrud = spDef.StaticAnalysis?.AstUpdateMappings;
                if (updateMappingsForCrud != null && updateMappingsForCrud.Count > 0)
                {
                    sbRules.Add($"{rIdx++}. {UpdateMappingTemplateIntroText}");
                    sbRules.AddRange(BuildUpdateMappingTemplateLines(updateMappingsForCrud));
                }

                // 이 분기도 BuildSpecificationPrompts와 같은 DML 범위 표를 받아야 한다 -
                // VerificationPipelineOrchestrator의 지역 모델 흐름은 BuildSpecificationPrompts를
                // 전혀 호출하지 않으므로, 여기 빠뜨리면 지역 모델 경로는 A1 결함을 드러내는
                // 재료를 한 번도 받지 못한다(SourceComment·Rounding·SessionOption 체크리스트와
                // 같은 모양의 결함).
                var dateParameterForCrud = SpecExpectations.ResolveDateParameter(spDef.StaticAnalysis);
                var dmlScopeFactsForCrud = DmlScopeExtractor.Extract(spDef.DdlText, dateParameterForCrud);
                if (dmlScopeFactsForCrud.Count > 0)
                {
                    sbRules.Add($"{rIdx++}. {DmlScopeTableIntroText}");
                    sbRules.AddRange(BuildDmlScopeTableLines(dmlScopeFactsForCrud, dateParameterForCrud));
                }

                // 이 분기도 BuildSpecificationPrompts와 같은 집합 술어 표를 받아야 한다 -
                // VerificationPipelineOrchestrator의 지역 모델 흐름은 BuildSpecificationPrompts를
                // 전혀 호출하지 않으므로, 여기 빠뜨리면 지역 모델 경로는 집합 리터럴
                // 대체 결함을 드러내는 재료를 한 번도 받지 못한다(DmlScope 표와 같은
                // 모양의 결함 - Task 4의 Critical이 정확히 이 비대칭이었다).
                var setPredicatesForCrud = DmlScopeExtractor.ExtractSetPredicates(spDef.DdlText);
                // [소프트 페일 전파 방지] BuildSpecificationPrompts의 같은 이름 조건 참고 -
                // dmlScopeFactsForCrud가 비었는데 setPredicatesForCrud만 채워지면 Extract
                // 쪽만 소프트 페일했다는 뜻이라, 재료 전체를 미덥지 않다고 보고 렌더하지
                // 않는다.
                if (setPredicatesForCrud.Count > 0 && dmlScopeFactsForCrud.Count > 0)
                {
                    sbRules.AddRange(BuildSetPredicateTableLines(setPredicatesForCrud));
                }

                // 이 분기도 BuildSpecificationPrompts와 같은 참조 함수 표를 받아야 한다 -
                // VerificationPipelineOrchestrator의 지역 모델 흐름(IsLocalProvider &&
                // ObjectType == Procedure)은 BuildSpecificationPrompts를 전혀 호출하지
                // 않으므로, 여기 빠뜨리면 지역 모델로 만드는 SP 명세서는 함수 서술 금지
                // 계약을 한 번도 받지 못한다 - DmlScope·집합 술어 표와 같은 모양의 결함.
                var knownFunctionNamesForCrud = (spDef.Dependencies ?? new List<DependencyInfo>())
                    .Where(d => SqlObjectTypeClassifier.ResolveCodeObjectType(d.Type) == CodeObjectType.Function)
                    .Select(d => d.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();
                var functionCallsForCrud = DmlScopeExtractor.ExtractFunctionCalls(spDef.DdlText, knownFunctionNamesForCrud);
                if (functionCallsForCrud.Count > 0)
                {
                    sbRules.AddRange(BuildReferencedFunctionTableLines(functionCallsForCrud, spDef));
                }

                // 이 분기도 BuildSpecificationPrompts와 같은 잠금 힌트 표를 받아야 한다 -
                // VerificationPipelineOrchestrator의 지역 모델 흐름은
                // BuildSpecificationPrompts를 전혀 호출하지 않으므로, 여기 빠뜨리면 지역
                // 모델 경로는 INS_EXTRA4PLCARD류 결함(같은 테이블이 별칭마다 힌트 유무가
                // 갈리는데 뭉뚱그려 서술)을 드러내는 재료를 한 번도 받지 못한다.
                var lockHintsForCrud = DmlScopeExtractor.ExtractLockHints(spDef.DdlText);
                if (lockHintsForCrud.Count > 0)
                {
                    sbRules.AddRange(BuildLockHintTableLines(lockHintsForCrud));
                }

                // 이 분기(CrudAnalysis)는 위 규칙 1 "The document must use only one
                // H2 header: `## CRUD 분석`"이 `## CRUD 분석` 하나만 허용한다. CASE
                // 분기 표는 `## 로직 흐름 요약`이 목적지라 이 분기가 쓰지 않으므로 -
                // Task 14 (Critical) - 표가 아니라 참고 재료로만 준다.
                //
                // [`Omit`이 아니라 `Reference`인 이유 - Minor, 최종 브랜치 리뷰 2차]
                // 위 규칙 3("map all target columns to their source values ... in
                // a 1:1 mapping table")이 요구하는 소스값에는 CASE 식이 그대로 들어갈
                // 수 있다(예: `SET Col = CASE WHEN ... END`) - 이 표는 `## CRUD 분석`
                // 소관이지 `## 로직 흐름 요약` 소관이 아니다. `BuildCaseBranchReference
                // MaterialLines`의 계약 문구("Never merge branches, never paraphrase
                // a comparison operator, and never summarise a result expression
                // when you use these facts elsewhere in this document")가 정확히
                // 이 재사용을 겨냥한다 - CASE 식이 소스값 칸에 옮겨질 때도 조건·결과
                // 원문이 뭉개지지 않게 한다. 그래서 `Omit`(개요처럼 CASE가 이 갈래의
                // 서술 대상이 전혀 아닌 경우)이 아니라 `Reference`다.
                sbRules.AddRange(BuildMachineFactBlockLines(
                    spDef,
                    executionSemanticsPresentation: MachineFactPresentation.Table,
                    caseBranchPresentation: MachineFactPresentation.Reference,
                    uncoveredNoticePresentation: MachineFactPresentation.Table,
                    localVariablePresentation: MachineFactPresentation.Omit));

                // 이 분기도 BuildSpecificationPrompts와 같은 파생 테이블 정의 표를 받아야
                // 한다 - VerificationPipelineOrchestrator의 지역 모델 흐름은
                // BuildSpecificationPrompts를 전혀 호출하지 않으므로, 여기 빠뜨리면 지역
                // 모델 경로는 축 A 🔴를 드러내는 재료를 한 번도 받지 못한다(DmlScope 표와
                // 같은 모양의 결함).
                var derivedColumnsForCrud = DerivedTableColumnExtractor.Extract(spDef.DdlText);
                if (derivedColumnsForCrud.Count > 0)
                {
                    sbRules.Add($"{rIdx++}. {DerivedTableIntroText}");
                    sbRules.AddRange(BuildDerivedTableColumnLines(derivedColumnsForCrud));
                }

                sbRules.Add($"{rIdx++}. Do not append any conversational filler, polite greetings, or unrelated explanations at the end. Terminate immediately.");
                sbRules.Add($"{rIdx++}. Do not wrap the entire response in a markdown code block.");
                sbRules.Add("");
                sbRules.Add("[Output Language Requirement]");
                sbRules.Add("- You MUST write the final markdown specification in Korean.");

                systemPrompt = string.Join("\n", sbRules);
                systemPrompt += $"\n\n[USER INSTRUCTIONS]\n{userInstructions}";

                var checklistSb = new StringBuilder();
                checklistSb.AppendLine("🎯 [필수 검증 체크리스트]");
                if (spDef.StaticAnalysis == null || spDef.StaticAnalysis.CreatedTempTables.Count == 0)
                {
                    checklistSb.AppendLine("- [ ] '임시 테이블 사용 여부: 임시 테이블을 생성하거나 사용하지 않습니다.'를 명시적으로 기재하셨습니까?");
                }
                else
                {
                    checklistSb.AppendLine($"- [ ] 생성/사용된 임시 테이블({string.Join(", ", spDef.StaticAnalysis.CreatedTempTables)})의 활용 목적을 기재하셨습니까?");
                }
                if (spDef.StaticAnalysis == null || spDef.StaticAnalysis.ReferencedFunctions.Count == 0)
                {
                    checklistSb.AppendLine("- [ ] '사용자 정의 함수(UDF) 호출 여부: UDF 사용자 정의 함수를 호출하지 않습니다.'를 명시적으로 기재하셨습니까?");
                }
                else
                {
                    checklistSb.AppendLine($"- [ ] 호출되는 UDF({string.Join(", ", spDef.StaticAnalysis.ReferencedFunctions)})의 호출 위치와 인자를 명확히 기재하셨습니까? (동작·반환값 서술은 금지됩니다 - 해당 함수의 Spec.md가 단일 진실의 원천입니다.)");
                }
                if (spDef.StaticAnalysis == null || spDef.StaticAnalysis.LinkedServerReferences.Count == 0)
                {
                    checklistSb.AppendLine("- [ ] 'Linked Server 원격 참조 여부: Linked Server를 통한 원격 참조를 사용하지 않습니다.'를 명시적으로 기재하셨습니까?");
                }
                if (spDef.StaticAnalysis != null && spDef.StaticAnalysis.SelectTables.Count > 0)
                {
                    checklistSb.AppendLine($"- [ ] SELECT 대상 원천 테이블({string.Join(", ", spDef.StaticAnalysis.SelectTables)})이 각각 누락이나 축약 없이 독립적인 행으로 기술되고, 참조 컬럼과 필터 조건이 정확히 작성되었습니까?");
                }
                if (spDef.StaticAnalysis != null && spDef.StaticAnalysis.InsertTables.Count > 0)
                {
                    checklistSb.AppendLine($"- [ ] INSERT 대상 테이블({string.Join(", ", spDef.StaticAnalysis.InsertTables)})의 각 컬럼별 원천 데이터 매핑 정보(상수값, 변수, ISNULL 변환 등)가 1:1 대조 표로 완전하게 기술되었습니까?");
                }

                // 이 분기는 지역 모델의 최초 생성 경로이자 L3 재생성 경로다
                // (BuildUpdateMappingTemplateLines 문서 참고). BuildSpecificationPrompts에만
                // 이 항목을 두면 두 경로가 여기서 갈라진다 - Task 4의 Critical 지적(3부
                // 식별자 규칙이 이 분기에는 없었다)과 같은 모양의 결함이 재발한다.
                var sourceCommentsForCrud = SourceCommentExtractor.Extract(spDef.DdlText);
                if (sourceCommentsForCrud.Count > 0)
                {
                    checklistSb.AppendLine(
                        $"- [ ] 원본 DDL의 주석 {sourceCommentsForCrud.Count}건(비실행 조건·코드 범례·헤더 선언)을 "
                        + "본문에 기록하셨습니까? 조건식 원문·도입 일자·사유를 그대로 옮기고, "
                        + "\"실행되지 않습니다\" 한 문장으로 대신하지 마십시오. 대조 대상:");
                    foreach (var block in sourceCommentsForCrud)
                    {
                        checklistSb.AppendLine($"      * (라인 {block.Line}) {block.Text}");
                    }
                }

                // 이 분기도 BuildSpecificationPrompts와 같은 ROUND 값 매핑 요구를 받아야
                // 한다 - VerificationPipelineOrchestrator의 지역 모델 흐름은
                // BuildSpecificationPrompts를 전혀 호출하지 않으므로, 여기 빠뜨리면 지역
                // 모델 경로는 이 규칙을 한 번도 받지 못한다(SourceComment 체크리스트와
                // 같은 모양의 결함).
                var roundingCallsForCrud = RoundingSemanticsExtractor.Extract(spDef.DdlText);
                if (roundingCallsForCrud.Count > 0)
                {
                    checklistSb.AppendLine(
                        $"- [ ] 원본의 3인자 ROUND 호출 {roundingCallsForCrud.Count}건에 대해 "
                        + $"{RoundingSemanticsExtractor.SemanticsSentence} "
                        + "이 값 매핑을 명세서에 기술하셨습니까? \"반올림 또는 절사\"처럼 "
                        + "어느 값이 어느 동작인지 흐리게 적지 마십시오.");
                }

                // 이 분기도 BuildSpecificationPrompts와 같은 세션 옵션 요구를 받아야
                // 한다 - VerificationPipelineOrchestrator의 지역 모델 흐름은
                // BuildSpecificationPrompts를 전혀 호출하지 않으므로, 여기 빠뜨리면 지역
                // 모델 경로는 이 규칙을 한 번도 받지 못한다(SourceComment·Rounding
                // 체크리스트와 같은 모양의 결함).
                var sessionOptionsForCrud = SessionOptionsExtractor.Extract(spDef.DdlText);
                if (sessionOptionsForCrud.Count > 0)
                {
                    checklistSb.AppendLine(
                        $"- [ ] 프로시저 본문이 설정하는 세션 옵션({string.Join(", ", sessionOptionsForCrud)})과 "
                        + "그것이 호출 계층에 미치는 영향을 기술하셨습니까?");
                }

                // 이 분기도 BuildSpecificationPrompts와 같은 헤더/구현 모순 확인 요구를
                // 받아야 한다 - VerificationPipelineOrchestrator의 지역 모델 흐름은
                // BuildSpecificationPrompts를 전혀 호출하지 않으므로, 여기 빠뜨리면 지역
                // 모델 경로는 이 규칙을 한 번도 받지 못한다(SourceComment·Rounding·
                // SessionOption 체크리스트와 같은 모양의 결함).
                if (sourceCommentsForCrud.Any(b => b.Kind == "Header"))
                {
                    checklistSb.AppendLine(
                        "- [ ] 헤더 주석이 선언한 계약(반환값 규약, 내부 SP 호출 유무 등)이 "
                        + "실제 구현과 어긋나는 부분이 있다면, 그 모순 자체를 명세서에 "
                        + "기록하셨습니까? 구현만 옳게 적고 주석이 낡았다는 사실을 빠뜨리면 "
                        + "다음 사람이 같은 조사에 다시 들어갑니다.");
                }
                checklistText = checklistSb.ToString();
            }
            else // LogicAndVisualization
            {
                var sbRules = new List<string>
                {
                    "You are an expert SQL Server Stored Procedure analyzer. Write ONLY the [## 로직 흐름 요약] and [## 비즈니스 흐름 시각화] sections of the markdown specification.",
                    "",
                    "[Rules]",
                    "1. The document must use only H2 headers: `## 로직 흐름 요약` and `## 비즈니스 흐름 시각화` in this exact order. Terminate immediately after writing them. Do not include other H2 headers.",
                    "2. In ## 로직 흐름 요약, detail the transaction boundary, business operation steps, rollback patterns using @@ERROR, and failure return codes. If TRY-CATCH is NOT used for exception handling, explicitly mention its absence and the associated risks.",
                    "3. When describing success or failure return values (e.g., @po_intRetVal), state ONLY what is EXPLICITLY assigned in the SQL code. Do not assume it returns 0 on success based solely on header comments if there is no explicit `SET` statement."
                };

                int rIdx = 4;
                if (hasUdf)
                {
                    // I1(2026-08-20 축 A 감사, 최종 전체 브랜치 리뷰) - 이 분기(지역
                    // 모델 경로의 「로직 흐름 요약」 생성)는 hasUdf 분기가 아예 없었다.
                    // 그런데도 promptSb 조립부(아래)의
                    // `sectionType == "CrudAnalysis" ||
                    // sectionType == "LogicAndVisualization"` 조건 때문에
                    // <referenced-ddl-source-code>(UDF DDL 전문)를 그대로 받는다 -
                    // UDF 소스를 손에 쥔 채 아무 계약도 없이 「로직 흐름」 산문을 쓰는
                    // 셈이었다. CrudAnalysis 분기의 hasUdf 규칙(위, "state only its
                    // calling location and arguments" 문장)과 같은 취지의 문장을
                    // 여기도 추가해 두 분기 모두 계약을 받도록 닫는다.
                    sbRules.Add($"{rIdx++}. When a referenced User Defined Function (UDF) is called, do NOT describe any function's behaviour - return value, branches, filters, defaults, or rounding - anywhere in this document. That belongs only in the function's own Spec.md, which the machine-derived 참조 함수 table links to.");
                }
                if (hasDynamicSql)
                {
                    sbRules.Add($"{rIdx++}. If dynamic SQL is present, explain the execution and business flow purpose in the logic summary.");
                }
                if (hasLinkedServers)
                {
                    sbRules.Add($"{rIdx++}. If Linked Server references are used, describe the external DB transaction flow and distributed transaction characteristics if applicable.");
                }

                // ①(2026-08-21 최종 브랜치 리뷰 재라운드) - 라운드 1은 "CRUD 분석 절에
                // 이미 실린 표를 근거로 쓰라"는 포인터 지시를 여기 붙였는데, 조정자
                // 재검토로 그 지시 자체가 틀렸다고 판정됐다: 이 분기의 프롬프트에는
                // 그 표가 실리지 않고(BuildLockHintTableLines 호출부는 CrudAnalysis
                // 분기·SP 최초 생성·함수 명세서 셋뿐), 구역 분할 경로는 세 절을 병렬로
                // 생성해 이 분기 실행 시점에 CRUD 분석 절이 존재하지도 않는다
                // (VerificationPipelineOrchestrator.cs:1328-1340, :1450-1462). 포인터
                // 대신 사실 자체를 인라인 재료로 준다(BuildLockHintReferenceMaterialLines
                // 문서 참고) - 그 재료가 이 절 출력물이 아니라 참고임을 스스로 명시하므로
                // CrudAnalysis 분기와 표를 중복 출력할 위험이 없다.
                var lockHintsForLogic = DmlScopeExtractor.ExtractLockHints(spDef.DdlText);
                if (lockHintsForLogic.Count > 0)
                {
                    sbRules.AddRange(BuildLockHintReferenceMaterialLines(lockHintsForLogic));
                }
                // 이 분기(LogicAndVisualization)는 위 규칙 1 "The document must use
                // only H2 headers: `## 로직 흐름 요약` and `## 비즈니스 흐름 시각화`"가
                // 이 둘만 허용하고 `## CRUD 분석`은 그중에 없다. 실행 의미 표는
                // `## CRUD 분석`이 목적지라 이 분기가 쓰지 않으므로 - Task 14
                // (Critical) - 표가 아니라 참고 재료로만 준다.
                sbRules.AddRange(BuildMachineFactBlockLines(
                    spDef,
                    executionSemanticsPresentation: MachineFactPresentation.Reference,
                    caseBranchPresentation: MachineFactPresentation.Table,
                    uncoveredNoticePresentation: MachineFactPresentation.Reference,
                    localVariablePresentation: MachineFactPresentation.Omit));
                sbRules.Add($"{rIdx++}. If `WITH(NOLOCK)` or `NOLOCK` read hints are used, analyze their transaction isolation implications (dirty read risk, data consistency impact) in the exception/constraint section. Base this analysis on the reference lock-hint facts above (if provided) and on the source DDL directly for any scan those facts do not cover (e.g. cursor declarations, standalone SELECTs, subqueries inside control-flow predicates, a statement's own top-level WHERE subqueries, or CTE bodies) - do not suppress a hint you can see in the DDL just because it is outside those reference facts, and do not assert or contradict which table or scan carries a hint beyond what the reference facts or the DDL itself state.");
                sbRules.Add($"{rIdx++}. Visualize the business flow using a Mermaid flowchart TD diagram:");
                sbRules.Add("   - Node text labels must be wrapped in double quotes.");
                sbRules.Add("   - You MUST add explicit condition labels on all branching arrows (e.g., `-->|Success|`, `-->|Failed: -1|`). Do not use double quotes, parentheses, or special characters on arrow condition text labels.");
                sbRules.Add("   - Node IDs must be unique uppercase alphanumeric characters (e.g., START, PRECHECK, BEGINTRAN, DELPG). Do not use Mermaid reserved keywords (graph, flowchart, subgraph, end, END) as node IDs. You MUST use 'PROC_END' instead of 'END'.");
                sbRules.Add("   - For different failure return codes (e.g., -1, -2, -9), route them to distinct end nodes (e.g., `FAIL_1[\"Return -1\"]`, `FAIL_2[\"Return -2\"]`) instead of collapsing all failures into a single `PROC_END` node.");
                sbRules.Add("   - Node IDs must be strictly identical between definition and reference. Do not mix formats like using NPRECHECK in one place and N_PRECHECK (with underscore) in another.");
                sbRules.Add("   - Ensure prophylactic rollbacks (e.g., `IF @@TRANCOUNT <> 0 ROLLBACK` before the main logic) proceed to the main `BEGIN TRAN` node, not to the end of the procedure.");
                sbRules.Add($"{rIdx++}. 소스코드 DDL 내에 명시적으로 상숫값(예: RETURN -5)이 지정되어 있지 않은 에러 반환 단계(예: IF @@ERROR <> 0 분기)에 대해 임의로 -1, -2 등 순차적인 숫자를 창작하여 단정적으로 기술하지 마십시오. 근거가 없는 값은 반드시 '실패 시 에러 코드 반환(값 정의 미비로 추정)' 등으로 서술하여 환각을 원천 배제하십시오.");
                sbRules.Add($"{rIdx++}. Do not append any conversational filler, polite greetings, or unrelated explanations at the end. Terminate immediately.");
                sbRules.Add($"{rIdx++}. Do not wrap the entire response in a markdown code block. However, you MUST use ```mermaid blocks for flowcharts.");
                sbRules.Add("");
                sbRules.Add("[Output Language Requirement]");
                sbRules.Add("- You MUST write the final markdown specification in Korean.");

                systemPrompt = string.Join("\n", sbRules);
                systemPrompt += $"\n\n[USER INSTRUCTIONS]\n{userInstructions}";

                checklistText = @"🎯 [필수 검증 체크리스트]
- [ ] '## 로직 흐름 요약' 및 '## 비즈니스 흐름 시각화' 헤더가 명확하게 작성되었습니까?
- [ ] 예외 처리 분기와 트랜잭션 제어 방식, NOLOCK에 의한 격리 수준 영향이 요약에 포함되었습니까?
- [ ] Mermaid flowchart 다이어그램이 문법 오류 없이 flowchart TD 형태로 작성되었습니까?";
            }

            var promptSb = new StringBuilder();
            promptSb.AppendLine("<stored-procedure-context>");
            promptSb.AppendLine("  <basic-info>");
            promptSb.AppendLine($"    <schema>{spDef.Schema}</schema>");
            promptSb.AppendLine($"    <name>{spDef.Name}</name>");
            promptSb.AppendLine("  </basic-info>");
            promptSb.AppendLine();
            promptSb.AppendLine("  <dependencies>");
            promptSb.AppendLine(dependenciesText.Trim());
            promptSb.AppendLine("  </dependencies>");

            if (sectionType == "CrudAnalysis")
            {
                promptSb.AppendLine();
                promptSb.AppendLine("  <referenced-table-schemas>");
                promptSb.AppendLine(tableSchemasText.Trim());
                promptSb.AppendLine("  </referenced-table-schemas>");
            }

            if (sectionType == "CrudAnalysis" || sectionType == "LogicAndVisualization")
            {
                if (!string.IsNullOrEmpty(referenceDdlsText))
                {
                    promptSb.AppendLine();
                    promptSb.AppendLine("  <referenced-ddl-source-code>");
                    promptSb.AppendLine(referenceDdlsText.Trim());
                    promptSb.AppendLine("  </referenced-ddl-source-code>");
                }

                if (!string.IsNullOrEmpty(staticAnalysisText))
                {
                    promptSb.AppendLine();
                    promptSb.AppendLine("  <static-analysis-metadata>");
                    promptSb.AppendLine(staticAnalysisText.Trim());
                    promptSb.AppendLine("  </static-analysis-metadata>");
                }
            }

            promptSb.AppendLine();
            promptSb.AppendLine("  <sp-source-ddl>");
            promptSb.AppendLine("```sql");
            promptSb.AppendLine(spDef.DdlText.Trim());
            promptSb.AppendLine("```");
            promptSb.AppendLine("  </sp-source-ddl>");
            promptSb.AppendLine();
            promptSb.AppendLine("  <deconstructed-logic-source-of-truth>");
            promptSb.AppendLine("```json");
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                promptSb.AppendLine(JsonSerializer.Serialize(spDef.DeconstructedLogic, options));
            }
            catch (Exception ex)
            {
                promptSb.AppendLine($"{{ \"error\": \"Serialization failed: {ex.Message}\" }}");
            }
            promptSb.AppendLine("```");
            promptSb.AppendLine("  </deconstructed-logic-source-of-truth>");
            promptSb.AppendLine("</stored-procedure-context>");
            promptSb.AppendLine();
            promptSb.AppendLine("IMPORTANT: You are a markdown formatter (Stage 2). You MUST strictly format the target H2 markdown sections using only the factual data extracted in `<deconstructed-logic-source-of-truth>` above. Do not skip any columns, parameters, or tables listed in the JSON. Translate formulas and columns exactly. Do not abbreviate or write placeholders like '(복잡한 식)'.");
            promptSb.AppendLine();
            promptSb.AppendLine("Based on the structured reference context above, reverse engineer the designated section and write the markdown specification in Korean following the checklist below:");
            promptSb.AppendLine(checklistText);

            if (!string.IsNullOrEmpty(feedbackLog))
            {
                promptSb.AppendLine();
                promptSb.AppendLine($"[CRITIC CORRECTION FEEDBACK LOG]:\n{feedbackLog}\n\nPlease accommodate all feedback comments, refine the specification, and correct any defects in the designated section. Note: The `<deconstructed-logic-source-of-truth>` JSON has already been corrected based on this feedback, so use the JSON as your primary source of truth for logic and data. Focus on correcting markdown formatting, structure, diagram syntax, and accurately translating the corrected JSON into the requested specification sections. Make sure not to introduce any regression defects.");
            }

            return (systemPrompt, promptSb.ToString());
        }

        public async Task<ReviewResult> ReviewSpecificationAsync(SpDefinition spDef, string specMarkdown, string? effort = null, CancellationToken cancellationToken = default)
        {
            if (spDef.ObjectType == CodeObjectType.Function)
            {
                return await ReviewFunctionSpecificationAsync(spDef, specMarkdown, effort, cancellationToken);
            }

            // [기준 1의 순서와 주어 - 2026-08-23 ③(b) 최종 리뷰 에스컬레이션 2] ③(b)부터 집합
            // 술어 표에 독립 SELECT(`SELECT n`) 행이 실린다. 그 술어는 읽는 행을 가르므로
            // "…인 행만 조회합니다"가 옳은 서술인데, 옛 문구는 `조회합니다` 금지를 먼저 적고
            // 표 면제를 뒤에 붙여 리터럴한 Critic이 옳은 문장을 보고할 여지를 글자로 남겼다
            // (로그 17개에서 발현 0건 - 이론적 경로). 지금은 표 면제를 앞으로, 금지는 쓰기
            // 문장의 대상 행 술어로 한정하고, 독립 SELECT 읽기 필터를 명시한다.
            //
            // [캐시 버전을 올리지 않는 이유] 이 변경은 Critic을 느슨하게만 한다 - Actor 지시와
            // 출력 계약은 그대로라 옛 산출물이 새 기준에서 결함이 되지 않는다. 인상 규정의
            // 취지(옛 계약 산출물이 다음 감사에 결함으로 남는 것)에 해당하지 않는다. v12는
            // Critic 완화가 Actor 지시(CRUD 설명 칸 금지)와 함께 바뀌어 올렸다.
            var systemPrompt = @"You are a principal database architect and critic agent reviewing a generated stored procedure specification in Markdown. Evaluate the accuracy, completeness, and formatting of the document against the original metadata.

[Evaluation Criteria (Score 0-10 for each item)]
1. Business Logic and Flow Accuracy (ScoreAccuracy):
   - Check if the operations and branches of the source code are documented accurately without hallucination, arbitrary assumptions, or guesses.
   - Walk EVERY WHERE predicate of the source DDL - including predicates inside a derived table (`FROM (SELECT ... WHERE ...) X`) - and verify each one is described in the specification AS A FILTER. First, when a machine-confirmed table carries the predicate verbatim for that specific statement (the `집합 술어` table's 술어 원문 column, or the `DML 범위` table's row for that statement), that IS the filter description - do NOT additionally demand it in prose, do NOT report the prose for that predicate as missing or softened (prose that CONTRADICTS the table is still reportable), and do NOT lower any score because the CRUD description column omits join keys or predicates (that column is instructed not to carry them). For a write statement (INSERT source, UPDATE, DELETE) whose predicate is NOT carried by such a table, a predicate that only appears as a column name, or is softened into wording such as `조회합니다`/`참조합니다`/`사용됩니다` without saying which rows it narrows, is NOT described as a filter: report it. For a standalone SELECT statement (a `SELECT n` row - cursor source, variable assignment, result set returned to the caller) the predicate narrows the rows that statement reads, so wording like `…인 행만 조회합니다`/`…인 행을 읽습니다` IS a filter description - do not report it; report only when the predicate is absent from both the tables and the prose, or when the prose contradicts the table. Conversely, report any filter the specification claims that the source does not actually have.
   - A predicate commented out with `--` does not run. If the specification describes it as active logic, report it; if the specification states it is commented out and not applied, that is correct and must NOT be penalized.
2. Data Model and CRUD Completeness (ScoreCrud):
   - Verify if all SELECT/INSERT/UPDATE/DELETE tables and columns are documented 1:1 in a table format without shortcuts (e.g., no 'etc.').
   - Verify if temp tables and Linked Servers are factually detailed (or stated explicitly as not used). For a referenced UDF, verify only that its calling location and arguments are documented - do NOT require or reward a description of the UDF's own behaviour, return value, or computation; that belongs only in the function's own Spec.md. Conversely, if the specification describes a referenced UDF behaviour, return value, computation, or formula anywhere in the document, that is a contract violation - report it as a defect in FeedbackComment and lower ScoreCrud accordingly.
3. Integration and Interface Definition (ScoreInterface):
   - Verify if parameter names, types, nullability (use '명시 없음' if undefined), and descriptions are fully detailed in a table.
   - Check if result set (Rowset) return behavior is explicitly stated.
4. Exception Handling, Transaction and Isolation Policy (ScoreException):
   - Assess if transaction control (BEGIN/COMMIT/ROLLBACK), error checking (TRY-CATCH or @@ERROR), and isolation effects (NOLOCK dirty read risk) are analyzed in depth.
   - Check if return codes and prerequisites are fully analyzed.
5. Diagram Syntax and Readability (ScoreReadability):
   - Ensure the Mermaid flowchart TD diagram has no syntax errors.
   - Node text labels must be wrapped in double quotes. Arrow labels must NOT contain double quotes, parentheses, or special characters.
   - Node labels containing '@' must be wrapped in double quotes, with the identifier written exactly as in the source. Flag any paraphrased or spelled-out '@' (e.g. 'at ERROR' for '@@ERROR').
   - Do NOT penalize the presence of `[AI 추론 보완: ...]` tags in the descriptions. This is a REQUIRED system tag for metadata cleansing SQL generation and its presence is expected and correct.

"
                + MachineConfirmedTables.CriticExemptionBlock
                + @"

[Defect Judgment]
- If any of the 5 criteria scores less than 8 points, or if any of the 5 mandatory H2 headers (## 개요, ## 파라미터 목록, ## CRUD 분석, ## 로직 흐름 요약, ## 비즈니스 흐름 시각화) is missing, mark HasDefects as true.

[Output Format]
Output ONLY the final JSON payload. Do not include markdown block markers (```json) or conversational text. Output raw JSON:
{
  ""HasDefects"": true or false (boolean),
  ""FeedbackComment"": ""Detailed correction instructions if defects are found. Return empty string if HasDefects is false."",
  ""ScoreAccuracy"": 10,
  ""ScoreCrud"": 10,
  ""ScoreInterface"": 10,
  ""ScoreException"": 10,
  ""ScoreReadability"": 10
}";

            var (dependenciesText, tableSchemasText, referenceDdlsText, staticAnalysisText) = BuildSpMetadataTexts(spDef);

            var userPrompt = $@"
Target Stored Procedure:
- Schema: {spDef.Schema}
- Name: {spDef.Name}

[Target Stored Procedure Source DDL]
```sql
{spDef.DdlText}
```

[Dependencies List]
{dependenciesText}

[Referenced Table Schemas (Markdown Tables)]
{tableSchemasText}

[Referenced UDF/SP DDL Codes]
{referenceDdlsText}

{staticAnalysisText}

[Generated Specification Markdown]
{specMarkdown}

Review the generated specification markdown against the source metadata and source DDL, and output the review result in JSON.
";

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                systemPrompt += "\n\n[Ollama System Prompt Requirements]\n- Before writing the JSON payload, write your step-by-step thinking process inside <think> and </think> tags. The final JSON must be placed outside the think tags.";
            }

            Log.Information("AI 명세서 리뷰 요청 전송 - SP: {Schema}.{Name}, Effort: {Effort}", spDef.Schema, spDef.Name, effort ?? "Default");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.1f, effort, cancellationToken: cancellationToken);

            Log.Information("AI 명세서 리뷰 응답 수신 완료 - SP: {Schema}.{Name}, 응답 길이: {Length}", spDef.Schema, spDef.Name, aiResult?.Content?.Length ?? 0);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult?.Content);
            var reviewResult = ParseReviewResult(aiResult?.Content, $"{spDef.Schema}.{spDef.Name}");
            reviewResult.ThinkingText = aiResult?.ThinkingText;
            return reviewResult;
        }

        private async Task<ReviewResult> ReviewFunctionSpecificationAsync(SpDefinition functionDef, string specMarkdown, string? effort, CancellationToken cancellationToken)
        {
            var systemPrompt = @"You are a principal database architect and critic agent reviewing a generated SQL Server User Defined Function specification in Markdown. Evaluate it against the supplied source metadata.

[Evaluation Criteria (Score 0-10 for each item)]
1. Business Logic and Formula Accuracy (ScoreAccuracy): Verify branches, formulas, transformations, and determinism are factual.
2. Referenced Object Completeness (ScoreCrud): Verify every referenced table and function is documented without shortcuts or invented columns.
3. Return Contract (ScoreInterface): Verify scalar return type or TVF result schema, parameters, nullability when known, and output derivations.
4. Side Effects and Constraints (ScoreException): Verify observable side effects, data access constraints, and prerequisite assumptions are accurately described.
5. Diagram Syntax and Readability (ScoreReadability): Verify Mermaid syntax and readability.

"
                + MachineConfirmedTables.CriticExemptionBlock
                + @"

[Defect Judgment]
- Mark HasDefects true when any criterion is below 8 or a mandatory H2 header is missing.

[Output Format]
Output ONLY raw JSON with HasDefects, FeedbackComment, ScoreAccuracy, ScoreCrud, ScoreInterface, ScoreException, and ScoreReadability.";

            var (dependenciesText, tableSchemasText, referenceDdlsText, staticAnalysisText) = BuildSpMetadataTexts(functionDef);
            var returnInfo = functionDef.FunctionReturn;
            var returnContract = returnInfo == null
                ? "Return metadata unavailable"
                : returnInfo.IsTableValued
                    ? string.Join(", ", returnInfo.Columns.Select(column => $"{column.ColumnName} {column.DataType} ({(column.IsNullable ? "nullable" : "not nullable")})"))
                    : returnInfo.DataType;
            var userPrompt = $@"
Target User Defined Function:
- Schema: {functionDef.Schema}
- Name: {functionDef.Name}
- Return contract: {returnContract}

[Target Function Source DDL]
```sql
{functionDef.DdlText}
```

[Dependencies List]
{dependenciesText}

[Referenced Table Schemas]
{tableSchemasText}

[Referenced Function DDL Codes]
{referenceDdlsText}

{staticAnalysisText}

[Generated Specification Markdown]
{specMarkdown}

Review the generated function specification against the source metadata and DDL, and output the review result in JSON.";

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                systemPrompt += "\n\n[Ollama System Prompt Requirements]\n- Before writing the JSON payload, write your step-by-step thinking process inside <think> and </think> tags. The final JSON must be placed outside the think tags.";
            }

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.1f, effort, cancellationToken: cancellationToken);
            var reviewResult = ParseReviewResult(aiResult?.Content, $"{functionDef.Schema}.{functionDef.Name}");
            reviewResult.ThinkingText = aiResult?.ThinkingText;
            return reviewResult;
        }

        public async Task<AiResult> GenerateBatchMigrationPlanAsync(SpDefinition spDef, string targetLanguage, CancellationToken cancellationToken = default)
        {
            var systemPrompt = $@"You are an expert architecture modernization engineer transitioning legacy SQL Server Agent batch jobs into modern application batch frameworks.
Analyze the target Stored Procedure source code and table/UDF schemas, and write a 'Batch Migration Plan' in Markdown for porting to {targetLanguage}.

[Required Content & Rules]
1. Write the document in Korean Markdown format.
2. **배치 전환 아키텍처 개요**: Propose a new scheduler framework to replace the SQL Server Agent Job (e.g., Quartz.NET/Hangfire for C# Worker Service, Spring Batch + Quartz for Java).
3. **대량 데이터 청크(Chunk) 처리 전략**: Propose paging reader patterns and bulk write guidelines to prevent Out-Of-Memory (OOM) errors.
4. **비즈니스 전환 설계 및 의사코드(Pseudocode)**: Provide modern OOP pseudocode structural examples converting the stored procedure logic.
   - If dynamic SQL is present, provide modern parameterized queries or safe query builders to avoid SQL injection.
   - If Linked Server references (4-part identifiers) are found, provide distributed transaction strategies, API gateway alternatives, or multi-datasource configurations.
   - If `WITH(NOLOCK)` read hints are used, suggest transaction isolation controls (e.g., Read Uncommitted isolation or specific ORM read-only options) to align with performance and data integrity properties.
5. **로깅 및 실패 조치 계획**: Transition TRY...CATCH database logging into structured logging (e.g., Serilog) and notification integration (e.g., Slack).
6. **데이터 정합성 검증 SQL 세트**: Provide validation SQL templates comparing row counts and checksums before and after batch execution.
7. Do not wrap the entire output in markdown code blocks (```markdown ... ```). Output the markdown directly.
8. Do not append any conversational filler, polite greetings, or unrelated explanations at the end. Terminate immediately.";

            var dependenciesText = new StringBuilder();
            var tableSchemasText = new StringBuilder();
            var referenceDdlsText = new StringBuilder();

            foreach (var dep in spDef.Dependencies)
            {
                // BuildSpMetadataTexts와 같은 DB 한정 규칙을 쓴다. 안 그러면
                // PaymentDB.dbo.TTxMst와 dbo.TTxMst가 이 목록에서 구별되지 않는다.
                var depQualifiedName = BuildDependencyQualifiedName(dep, spDef);
                dependenciesText.AppendLine($"- Name: {depQualifiedName}, Type: {dep.Type} (발견 깊이: {dep.DiscoveryDepth}단계)");

                if (dep.Columns.Count > 0)
                {
                    tableSchemasText.AppendLine(FormatTableSchemaToMarkdown(dep, spDef));
                    tableSchemasText.AppendLine();
                }

                if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                {
                    referenceDdlsText.AppendLine($"### 객체: {dep.Schema}.{dep.Name} ({dep.Type})");
                    referenceDdlsText.AppendLine("```sql");
                    referenceDdlsText.AppendLine(dep.ReferencedDdlText);
                    referenceDdlsText.AppendLine("```");
                    referenceDdlsText.AppendLine();
                }
            }

            var userPrompt = $@"
Target Stored Procedure:
- Schema: {spDef.Schema}
- Name: {spDef.Name}

[Dependencies List]
{dependenciesText}

[Referenced Table Schemas]
{tableSchemasText}

[Referenced UDF/SP DDL Codes]
{referenceDdlsText}

[Stored Procedure DDL SQL Source]
```sql
{spDef.DdlText}
```

Write the Batch Migration Plan for {targetLanguage} based on the legacy SQL SP details.
";

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                systemPrompt += "\n\n[Ollama Thinking Requirements]\n- Detail your analytical thoughts inside <think> and </think> tags before writing the plan. The final markdown must be placed outside the think tags.";
            }

            Log.Information("AI 배치 전환 계획서 생성 요청 전송 - SP: {Schema}.{Name}, TargetLanguage: {TargetLanguage}", spDef.Schema, spDef.Name, targetLanguage);
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, _temperature, effort: null, cancellationToken: cancellationToken);
            if (aiResult == null)
            {
                aiResult = new AiResult();
            }
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt;

            Log.Information("AI 배치 전환 계획서 생성 응답 수신 완료 - SP: {Schema}.{Name}, 응답 길이: {Length}", spDef.Schema, spDef.Name, aiResult.Content.Length);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult.Content);

            return aiResult;
        }

        
        public async Task<AiResult> BrainstormBatchPlanAsync(System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, CancellationToken cancellationToken = default)
        {
            var systemPrompt = $@"You are a principal database modernization architect. Your task is to brainstorm and analyze the overarching architecture for consolidating multiple legacy stored procedure specifications into a single {targetLanguage} batch application named '{jobName}'.
DO NOT write code or detailed markdown plans. ONLY output your analysis.
Cover exactly these three things:
1. Common domain logic - what the procedures share (the same source tables, the same rounding rules, the same business date), and where consolidating would change results rather than only reorganize them.
2. Execution order and dependencies - which procedures must run before which, and which touch the same target table and therefore MUST NOT run in parallel.
3. For each procedure, whether its work can be split into committed chunks or must complete as one unit. State the reason: a step that aggregates with GROUP BY, joins across databases, or has no single key that partitions its target rows cannot be chunked. The downstream step list carries this as a per-step boolean, so decide it per procedure rather than for the job as a whole.
Describe the architecture in terms of the target stack ({targetLanguage}) and of the source semantics. Do NOT reach for a specific framework's vocabulary unless the target stack is that framework - naming a pattern from an unrelated framework does not tell the next stage anything it can act on.";

            var userPrompt = new System.Text.StringBuilder();
            userPrompt.AppendLine($"Unified Batch Job Name: {jobName}");
            userPrompt.AppendLine($"Target Language Stack: {targetLanguage}");
            var procedureSpecs = FeedbackSpec.OnlyProcedureSpecs(specs);
            userPrompt.AppendLine($"Total Legacy Stored Procedures to Consolidate: {procedureSpecs.Count} procedures");
            userPrompt.AppendLine();
            userPrompt.AppendLine("[Provided Stored Procedure Specifications]");
            foreach (var spec in procedureSpecs)
            {
                userPrompt.AppendLine($"---");
                userPrompt.AppendLine($"Filename: {spec.FileName}");
                userPrompt.AppendLine($"[Content Start]");
                userPrompt.AppendLine(spec.Content);
                userPrompt.AppendLine($"[Content End]");
                userPrompt.AppendLine();
            }

            AppendFeedbackSection(userPrompt, specs);
            userPrompt.AppendLine("Please analyze the specifications and provide your architectural brainstorming.");

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                systemPrompt += "\n\n[Ollama Thinking Requirements]\n- Detail your analytical thoughts inside <think> and </think> tags before writing the final analysis. The final text must be placed outside the think tags.";
            }

            Log.Information("AI 배치 계획 브레인스토밍 요청 전송 - JobName: {JobName}, TargetLanguage: {TargetLanguage}, Effort: {Effort}", jobName, targetLanguage, effort ?? "Default");

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), _temperature, effort, cancellationToken: cancellationToken);
            if (aiResult == null) aiResult = new AiResult();
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt.ToString();
            return aiResult;
        }

        public async Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, IReadOnlyList<string> sourceProcedures, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default)
        {
            var systemPrompt = $@"You are a principal database modernization architect. Based on the previous brainstorming, draft a detailed step-by-step structural plan (Table of Contents and execution flow) for the final '{jobName}' {targetLanguage} batch application document.
You MUST use exactly the following 4 mandatory H2 headers in Korean, and design the detailed sub-headers (H3, H4) beneath them:
1. ## 통합 배치 아키텍처 개요
2. ## Mermaid 기반 통합 흐름도
3. ## 단계별 이행 상세 및 의사코드
4. ## 통합 데이터 정합성 검증 SQL 세트

[Machine-Readable Step List — MANDATORY]
In ADDITION to the prose outline, you MUST emit exactly one fenced ```json block containing the ordered step list. The downstream pipeline generates one document section per entry, so an omitted step is never written at all.

```json
{{
  ""Steps"": [
    {{
      ""Code"": ""S01"",
      ""Name"": ""Short Korean step name"",
      ""LegacyProcedures"": [""UP_SOURCE_PROC""],
      ""TargetTables"": [""dbo.TargetTable""],
      ""ErrorCodes"": [""-1"", ""-2""],
      ""Chunkable"": false
    }}
  ]
}}
```

Rules for the step list:
- The list must contain AT MOST {BatchStepPlanParser.MaxSteps} entries. The pipeline discards a longer list whole and falls back to generating the document in a single call, losing every per-step section — so choose the step granularity to fit that budget. A cohesive phase of a source procedure is one step; do NOT emit one step per internal branch or per exception rule.
- One entry per executable step. NEVER collapse several steps into one entry (no `S01~S04` style ranges).
- `Code` must be unique and must also appear in the prose outline heading for that step.
- `LegacyProcedures` must be copied verbatim from the supplied Source Procedures list. It is how the pipeline links a step to its origin: the coverage check compares these names against that same list, and the enrichment pass uses them to fill `ErrorCodes` and `TargetTables`. Leave it empty only for a step with no legacy origin (input validation, locking, final publish).
- `TargetTables` should list, to the best of your judgment from the brainstorming analysis, every table the step creates or modifies. This stage receives no source specifications, only the brainstorming result above, so treat this as a working estimate, not a final answer: the pipeline replaces it afterward with the tables a static-analysis pass confirms against the actual source code. That replacement only happens for a step that HAS a legacy origin, because it is keyed by `LegacyProcedures`. For a step with **no legacy origin** - locking, journaling, reconciliation, final publish - whatever you write here IS the final answer, and those are exactly the steps whose tables are new batch control objects. Spell them under the batch schema (`batch.BatchRun`, not `dbo.TBatchRun`) and use the same name the step body will use.
- `ErrorCodes` should reproduce, to the best of your judgment from the brainstorming analysis, the original return codes of the source procedure. This stage receives no source specifications either, so this is also a working estimate: the pipeline unions it afterward with the codes a dedicated extraction pass finds in the specifications.
- Never emit an empty `Steps` list and never omit the JSON block, however incomplete the supplied analysis feels. A step list with imperfect `LegacyProcedures`, `TargetTables`, or `ErrorCodes` is recoverable — every one of those three is corrected downstream from the source specifications. An absent step list is not recoverable: it discards every per-step section and every per-step check before any correction can happen.
- `Chunkable` is false when the step is an aggregation or cross-DB join that cannot be chunked by a single key.
- Emit the block once. Do not wrap the whole answer in a code block.

" + BatchObjectSchemaRule;

            // 재수립 모드. 이전 구조로 만든 본문이 리뷰를 반복 통과하지 못했다는 뜻이므로
            // 같은 구조를 다시 내면 재시도 예산만 소진된다. 4개 H2 강제는 유지한다 —
            // MechanicalValidator가 같은 헤더를 요구하므로 여기서 풀면 L1이 깨진다.
            var isRedraft = !string.IsNullOrWhiteSpace(previousStructure);
            if (isRedraft)
            {
                systemPrompt += @"

[Redraft]
The previous structure below repeatedly failed cross-review. Do NOT reproduce it.
- Diagnose which structural decision caused the reported defects: a missing step, a step placed under the wrong architecture (e.g. chunking a GROUP BY aggregation that cannot be chunked), or an execution order that breaks data consistency.
- Change that decision. Reordering sub-headers without changing the underlying step design is not an acceptable redraft.
- Keep the 4 mandatory H2 headers exactly as specified above.";
            }

            var userPrompt = new System.Text.StringBuilder();
            userPrompt.AppendLine($"Unified Batch Job Name: {jobName}");
            userPrompt.AppendLine($"Target Language Stack: {targetLanguage}");
            userPrompt.AppendLine();
            userPrompt.AppendLine("[Brainstorming Analysis Result]");
            userPrompt.AppendLine(brainstormingResult);
            userPrompt.AppendLine();

            // 명단이 없으면 블록을 만들지 않는다. 빈 목록에 "아래 목록에서 고르라"고
            // 하면 모델이 고를 것이 없어 다시 거부를 택한다 - 이번 회귀의 원인이었다.
            if (sourceProcedures != null && sourceProcedures.Count > 0)
            {
                userPrompt.AppendLine("[Source Procedures — use these names verbatim in `LegacyProcedures`]");
                foreach (var procedure in sourceProcedures)
                {
                    userPrompt.AppendLine($"- {procedure}");
                }
                userPrompt.AppendLine();
            }

            if (isRedraft)
            {
                userPrompt.AppendLine("[Previous Structure That Failed Review]");
                userPrompt.AppendLine(previousStructure);
                userPrompt.AppendLine();

                if (!string.IsNullOrWhiteSpace(redraftFeedback))
                {
                    userPrompt.AppendLine("[Accumulated Review Feedback]");
                    userPrompt.AppendLine(redraftFeedback);
                    userPrompt.AppendLine();
                }
            }

            userPrompt.AppendLine("Please draft the detailed structural plan and step-by-step instructions for the final markdown document.");

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                systemPrompt += "\n\n[Ollama Thinking Requirements]\n- Detail your analytical thoughts inside <think> and </think> tags. The final text must be placed outside the think tags.";
            }

            Log.Information("AI 배치 계획 목차 수립 요청 전송 - JobName: {JobName}, TargetLanguage: {TargetLanguage}, Effort: {Effort}, Redraft: {IsRedraft}", jobName, targetLanguage, effort ?? "Default", isRedraft);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), _temperature, effort, cancellationToken: cancellationToken);
            if (aiResult == null) aiResult = new AiResult();
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt.ToString();
            return aiResult;
        }

        public async Task<AiResult> GenerateConsolidatedBatchPlanAsync(string planStructure, System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, IReadOnlyList<StepInterface>? stepInterfaces = null, string? brainstorming = null, CancellationToken cancellationToken = default)

        {
            var systemPrompt = $@"You are a principal database modernization architect consolidating multiple legacy stored procedure specifications into a single {targetLanguage} batch application and scheduler plan (Consolidated Batch Modernization Plan).
Consolidate the provided specifications into a single unified batch job named '{jobName}'.

" + ConsolidatedPlanRules;

            var userPrompt = new StringBuilder();

            // 분할 생성과 같은 컨텍스트를 싣는다. 이 경로는 분할이 실패했을 때만 도는
            // 폴백이지만 산출물도 받는 검사도 같다. 직접 조립하던 동안은 제어 계약 표와
            // 원본 인터페이스 표, 승인된 단계 목록이 빠져 있었고, 그래서 규칙 5가
            // "[Original Procedure Interface] 표에 적힌 파라미터가 전부다"라고 말하는데
            // 그 표가 프롬프트에 없는 상태가 됐다 - 없는 근거를 가리키는 규칙은
            // 지킬 방법이 없다.
            //
            // sharedConventions는 빈 문자열이다. 이 호출은 문서 전체를 지금 쓰므로
            // "이미 문서에 쓰인 규약"이라는 것이 존재하지 않는다.
            AppendSharedStepContext(
                userPrompt,
                BatchStepPlanParser.TryParse(planStructure) ?? new List<BatchStepPlan>(),
                sharedConventions: string.Empty,
                specs,
                stepInterfaces ?? new List<StepInterface>(),
                targetLanguage,
                jobName);

            userPrompt.AppendLine();
            AppendStatementAnchorRules(userPrompt);

            // 이 경로도 아키텍처 개요와 흐름도를 쓴다 - 분할 경로에서 골격이 하는 일을
            // 문서 전체와 함께 한 번에 한다. 브레인스토밍이 도달해야 하는 자리가 여기다.
            if (!string.IsNullOrWhiteSpace(brainstorming))
            {
                userPrompt.AppendLine();
                userPrompt.AppendLine("[Architecture Brainstorming — analysis that produced the structure below]");
                userPrompt.AppendLine("Treat this as reasoning to carry into the overview and the flowchart, not as text to copy.");
                userPrompt.AppendLine("Where it conflicts with the approved structure below, the approved structure wins.");
                userPrompt.AppendLine();
                userPrompt.AppendLine(brainstorming);
            }

            userPrompt.AppendLine();
            userPrompt.AppendLine("[Approved Document Structure & Plan]");
            userPrompt.AppendLine(planStructure);
            userPrompt.AppendLine();
            AppendFeedbackSection(userPrompt, specs);

            userPrompt.AppendLine("Please draft the Consolidated Batch Modernization Plan, STRICTLY adhering to the [Approved Document Structure & Plan] above.");

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                systemPrompt += "\n\n[Ollama Thinking Requirements]\n- Detail your analytical thoughts inside <think> and </think> tags before writing the plan. The final markdown must be placed outside the think tags.";
            }

            Log.Information("AI 통합 배치 계획서 생성 요청 전송 - JobName: {JobName}, TargetLanguage: {TargetLanguage}, Effort: {Effort}", jobName, targetLanguage, effort ?? "Default");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt.ToString());

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), _temperature, effort, cancellationToken: cancellationToken);
            if (aiResult == null)
            {
                aiResult = new AiResult();
            }
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt.ToString();

            Log.Information("AI 통합 배치 계획서 생성 응답 수신 완료 - JobName: {JobName}, 응답 길이: {Length}", jobName, aiResult.Content.Length);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult.Content);

            return aiResult;
        }

        /// <summary>
        /// 단계 본문을 뺀 골격을 만든다. H2 4개를 모두 쓰되, 단계 상세 H2 아래에는
        /// 모든 단계가 공유할 공통 규약 소절과 단계별 자리표시자만 남긴다.
        ///
        /// 공통 규약을 여기서 한 번 확정하는 이유: 단계별로 각자 쓰게 하면 13개
        /// 단계가 서로 다른 오류 처리·Shadow·Chunk 관례를 선언한다.
        /// </summary>
        public async Task<AiResult> GenerateBatchPlanSkeletonAsync(
            IReadOnlyList<BatchStepPlan> steps,
            string planStructure,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            string? effort = null,
            string? brainstorming = null,
            IReadOnlyList<StepInterface>? stepInterfaces = null,
            CancellationToken cancellationToken = default)
        {
            var placeholders = new StringBuilder();
            foreach (var step in steps)
            {
                placeholders.AppendLine($"<!-- STEP:{step.Code} -->");
            }

            var systemPrompt = $@"You are a principal database modernization architect writing the SKELETON of the '{jobName}' consolidated {targetLanguage} batch migration plan.
Consolidate the provided specifications into a single unified batch job named '{jobName}'.

[Skeleton Contract]
- Write ALL four mandatory H2 sections in full, EXCEPT for the individual step bodies.
- Under `{BatchPlanAssembler.StepDetailHeader}`, write ONLY the shared subsections that every step relies on: the common SQL error-tracking pattern, the Shadow Table and recovery policy, and the chunk-paging policy.
- After those shared subsections, emit the following placeholder lines VERBATIM, in this exact order, and write NOTHING else under that H2. Each step body is generated separately and will replace these lines.

{placeholders}
- Do NOT write any `###` step section under that H2 yourself.
- The Mermaid flowchart and the architecture overview MUST cover every step in the approved step list.

" + ConsolidatedPlanRules;

            var userPrompt = new StringBuilder();

            // 원본 인터페이스 표를 골격에도 싣는다. 골격은 단계 본문을 쓰지 않지만
            // ConsolidatedPlanRules를 통째로 받고, 그 규칙 5가 "[Original Procedure
            // Interface] 표에 적힌 파라미터가 전부"라고 말한다. 표가 없으면 규칙이
            // 프롬프트에 없는 근거를 가리키게 된다 - 지킬 방법이 없는 지시다.
            //
            // 캐시 접두사에 대해: 골격과 단계 호출의 프롬프트가 통째로 같아지지는 않는다.
            // 골격은 sharedConventions를 갖지 못하기 때문이다 - 그것은 골격의 *출력*에서
            // 나온다. 실제로 지켜야 하는 불변식은 "N개 단계 호출끼리 접두사가 같다"이고,
            // 두 경로가 공유하는 것은 그 앞의 명세서 전량(실측 481KB)이다.
            AppendSharedStepContext(
                userPrompt, steps, string.Empty, specs,
                stepInterfaces ?? Array.Empty<StepInterface>(), targetLanguage, jobName);

            // 1/3 브레인스토밍 원문. 골격이 아키텍처 개요와 흐름도를 쓰는 자리이고,
            // 그 판단이 나온 곳이 여기다. 전달하지 않던 동안은 목차 제목에 살아남은
            // 만큼만 본문에 도달했다 - 청크 가능 여부의 근거, 병렬 금지의 이유,
            // 통합이 결과를 바꾸는 지점 같은 서술은 제목이 담을 수 있는 것이 아니다.
            //
            // 공유 접두사 뒤에 붙인다. AppendSharedStepContext까지가 단계 호출과
            // 바이트가 같아야 하는 구간이고, 여기서부터는 골격 전용이라 어긋나도 된다.
            if (!string.IsNullOrWhiteSpace(brainstorming))
            {
                userPrompt.AppendLine("[Architecture Brainstorming — analysis that produced the structure below]");
                userPrompt.AppendLine("Treat this as reasoning to carry into the overview and the flowchart, not as text to copy.");
                userPrompt.AppendLine("Where it conflicts with the approved structure below, the approved structure wins.");
                userPrompt.AppendLine();
                userPrompt.AppendLine(brainstorming);
                userPrompt.AppendLine();
            }

            userPrompt.AppendLine("[Approved Document Structure & Plan]");
            userPrompt.AppendLine(planStructure);
            userPrompt.AppendLine();
            AppendFeedbackSection(userPrompt, specs);

            userPrompt.AppendLine("Please draft the skeleton, STRICTLY adhering to the [Skeleton Contract] and the [Approved Document Structure & Plan] above.");

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                systemPrompt += "\n\n[Ollama Thinking Requirements]\n- Detail your analytical thoughts inside <think> and </think> tags before writing the plan. The final markdown must be placed outside the think tags.";
            }

            Log.Information("AI 배치 계획 골격 생성 요청 전송 - JobName: {JobName}, 단계 수: {Count}개", jobName, steps.Count);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), _temperature, effort, cancellationToken: cancellationToken);
            if (aiResult == null) aiResult = new AiResult();
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt.ToString();

            Log.Information("AI 배치 계획 골격 생성 응답 수신 완료 - JobName: {JobName}, 응답 길이: {Length}", jobName, aiResult.Content.Length);
            return aiResult;
        }

        /// <summary>
        /// 단계 섹션 하나를 생성한다.
        ///
        /// 문서를 통째로 만드는 GenerateConsolidatedBatchPlanAsync를 플래그로
        /// 확장하지 않고 메서드를 나눈 이유: 반환 계약이 다르다. 저쪽은 H2 4개를
        /// 갖춘 완결 문서를, 이쪽은 H3 섹션 하나를 돌려준다. 같은 메서드에 두
        /// 계약을 겹치면 L1 검증 대상이 호출부마다 달라진다.
        ///
        /// floorFeedback은 반드시 프롬프트 말미에 붙는다. 앞에 끼우면 캐시
        /// 접두사가 깨져 분할의 비용 이점이 사라진다.
        /// </summary>
        public async Task<AiResult> GenerateBatchStepSectionAsync(
            BatchStepPlan step,
            IReadOnlyList<BatchStepPlan> allSteps,
            string sharedConventions,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            IReadOnlyList<StepInterface> stepInterfaces,
            string targetLanguage,
            string jobName,
            string? effort = null,
            string? floorFeedback = null,
            CancellationToken cancellationToken = default)
        {
            var systemPrompt = $@"You are a principal database modernization architect writing ONE step section of the '{jobName}' consolidated {targetLanguage} batch migration plan.

[Output Contract]
- Output ONLY the markdown for the single requested step section. Do NOT output any H2 header, any other step, or any conversational text.
- The section MUST begin with a level-3 heading that contains the step code given in the FINAL user message.
- The section MUST contain at least one fenced SQL or pseudocode block. A bullet list alone is not an implementation instruction.
- EVERY target table listed for this step MUST appear in the section.
- EVERY original error code listed for this step MUST appear verbatim in the section.
- Write the section body in Korean.
- The shared conventions below are ALREADY written elsewhere in the document. Follow them; do not restate them.

" + ConsolidatedPlanRules;

            var userPrompt = new StringBuilder();
            AppendSharedStepContext(
                userPrompt, allSteps, sharedConventions, specs, stepInterfaces, targetLanguage, jobName);

            AppendStatementAnchorRules(userPrompt);

            // 단계 지시와 재시도 피드백은 회차마다 달라지므로 공통 컨텍스트에 붙이지
            // 않는다. gpt-5.6 이후 모델은 암묵적 cache breakpoint를 마지막 메시지에 놓고
            // 그 지점의 접두사 전체를 비교하므로, 243KB 컨텍스트 뒤에 이 몇 줄이 붙으면
            // 12단계가 공유하던 캐시가 통째로 죽는다.
            var volatileSuffix = new StringBuilder();
            volatileSuffix.AppendLine($"Now write the section for step {step.Code} ({step.Name}) ONLY.");

            if (!string.IsNullOrWhiteSpace(floorFeedback))
            {
                volatileSuffix.AppendLine();
                volatileSuffix.AppendLine("[Previous Attempt Rejected]");
                volatileSuffix.AppendLine(floorFeedback);
            }

            // 검토 피드백도 회차마다 바뀌므로 여기 싣는다. 공유 컨텍스트에 두면 재시도
            // 회차마다 N개 단계 호출의 접두사가 전부 무효가 된다 - 명세서 전량(실측
            // 481KB)이 매 회차 다시 올라간다.
            volatileSuffix.AppendLine();
            AppendFeedbackSection(volatileSuffix, specs);

            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(ProviderName) && _enableOllamaThinking)
            {
                systemPrompt += "\n\n[Ollama Thinking Requirements]\n- Detail your analytical thoughts inside <think> and </think> tags. The final markdown must be placed outside the think tags.";
            }

            Log.Information("AI 배치 단계 섹션 생성 요청 전송 - JobName: {JobName}, Step: {Step}, 재시도 피드백: {HasFeedback}",
                jobName, step.Code, !string.IsNullOrWhiteSpace(floorFeedback));

            var aiResult = await _aiClient.ChatAsync(
                systemPrompt, userPrompt.ToString(), _temperature, effort,
                volatileUserSuffix: volatileSuffix.ToString(), cancellationToken: cancellationToken);
            if (aiResult == null) aiResult = new AiResult();
            aiResult.SystemPrompt = systemPrompt;
            // 기록은 병합본으로 남긴다 — raw/prompt-context.md는 모델이 실제로 받은 것을
            // 서술해야 하며, 전송이 두 메시지로 나뉘었다는 사정은 그 계약을 바꾸지 않는다.
            aiResult.UserPrompt = PromptComposition.MergeVolatileSuffix(
                userPrompt.ToString(), volatileSuffix.ToString());

            Log.Information("AI 배치 단계 섹션 생성 응답 수신 완료 - JobName: {JobName}, Step: {Step}, 응답 길이: {Length}",
                jobName, step.Code, aiResult.Content.Length);
            return aiResult;
        }

        /// <summary>
        /// 단계별 호출이 공유하는 프롬프트 접두사.
        ///
        /// 이 메서드가 만드는 부분은 단계마다 완전히 동일해야 한다. 여기에
        /// 단계별 값이 섞여 들어가면 프롬프트 캐시가 매 호출 미스가 되어,
        /// 분할 생성의 입력 비용이 1배에서 N배로 뛴다.
        /// </summary>
        /// <summary>
        /// 검토 피드백을 제 이름을 단 자리에 싣는다.
        ///
        /// 파이프라인은 피드백을 명세서 목록에 끼워 넣어 나른다. 그대로 두면
        /// "[Provided Stored Procedure Specifications]" 안에 프로시저 명세서인 것처럼
        /// 놓이고 프로시저 개수도 하나 부푼다. 명세서 자리에서는 걷어내고
        /// (<see cref="FeedbackSpec.OnlyProcedureSpecs"/>) 여기에서 따로 싣는다.
        ///
        /// 실을 피드백이 없으면 한 줄도 쓰지 않는다 - 1차 회차 프롬프트가 재시도
        /// 회차와 같은 바이트를 유지해야 접두사 캐시가 산다.
        /// </summary>
        /// <summary>
        /// 문장 앵커와 의미 보존 세 조항. 분할 생성과 단일 호출 폴백이 함께 쓴다.
        ///
        /// 폴백에도 실어야 하는 이유: 분할이 실패했을 때만 도는 경로지만, 그때 나오는
        /// 것도 같은 산출물이고 같은 검사를 받는다. 앵커가 없으면 단계 검사가 문장을
        /// 명세서의 갱신 N에 붙이지 못해 조인 키·술어 컬럼 대조가 통째로 꺼진다 -
        /// 검사가 실패하는 것이 아니라 실행되지 않는다. 한 곳에 두는 이유는 두 벌로
        /// 적어두면 한쪽만 고쳐져 경로에 따라 다른 문서가 나오기 때문이다.
        /// </summary>
        private static void AppendStatementAnchorRules(StringBuilder builder)
        {
            // [축 B 감사가 요구하는 세 가지 - POQSettleBatch1 2026-08-24]
            // 앵커가 없으면 단계 검사가 문장을 명세서의 갱신 N에 붙일 수 없어 조인 키·술어
            // 컬럼 대조가 통째로 꺼진다. 규약 두 조항은 실측에서 금액·행 집합을 바꾼 치환이다.
            // 이 블록은 floorFeedback보다 앞, 캐시 접두사(userPrompt) 안쪽에 둔다 - floorFeedback은
            // volatileSuffix에 실려 반드시 프롬프트 말미에 붙어야 한다(아래 참고).
            builder.AppendLine("### 문장 앵커와 의미 보존 (필수)");
            builder.AppendLine();
            builder.AppendLine("- **각 DML 문장 바로 앞에 명세서의 갱신 번호를 주석으로 답니다.** " +
                "`/* U13: 카드사 원가 반영 */` 형식입니다(`갱신 13`·`UPDATE 13`도 인정됩니다). " +
                "번호가 있어야 검증이 명세서 DML 범위 표의 조인 키·술어 컬럼과 문장 단위로 대조합니다. " +
                "앵커와 설명은 **하나의 주석에** 담으십시오. 주석을 둘로 나누면(`/* U13 */`와 " +
                "`/* 카드사 원가 반영 */`) 검증이 문장 바로 앞의 가장 가까운 주석 하나만 읽으므로 앵커를 놓칠 수 있습니다.");
            builder.AppendLine("- **스칼라 하위질의를 `CROSS APPLY`/`OUTER APPLY`로 바꾸지 마십시오.** " +
                "명세서가 대입 우변을 스칼라 하위질의로 적은 자리는 무결과일 때 `NULL`이 대입되는 자리입니다. " +
                "`CROSS APPLY`는 그 행을 갱신 대상에서 통째로 제외해, 같은 문장의 다른 컬럼 대입까지 사라집니다. " +
                "원본(명세서)이 이미 `CROSS APPLY`/`OUTER APPLY` 형태라면 이 조항은 해당하지 않습니다. " +
                "명세서와 다르게 바꿔야 할 이유가 있으면 그 사실과 이유를 단계 본문에 적으십시오.");
            builder.AppendLine("- **비집계 조회 여러 문장을 집계 한 문장으로 합치지 마십시오.** " +
                "명세서가 `SELECT @v = col` 뒤에 `@@ROWCOUNT > 1` 분기를 둔 자리는 \"없음\"과 \"여럿\"을 " +
                "가르는 자리입니다. `MAX(col)` 한 문장으로 합치면 \"없음\"의 표현이 `0`에서 `NULL`로 바뀌어 " +
                "분기가 역전됩니다. 원본(명세서)이 이미 집계 한 문장 형태라면 이 조항은 해당하지 않습니다. " +
                "명세서와 다르게 바꿔야 할 이유가 있으면 그 사실과 이유를 단계 본문에 적으십시오.");
            builder.AppendLine();
        }

        private static void AppendFeedbackSection(
            StringBuilder builder,
            System.Collections.Generic.List<(string FileName, string Content)> specs)
        {
            var feedback = FeedbackSpec.OnlyFeedback(specs);
            if (feedback.Count == 0)
            {
                return;
            }

            builder.AppendLine(FeedbackSpec.PromptHeader);
            foreach (var entry in feedback)
            {
                builder.AppendLine("---");
                builder.AppendLine($"Source: {entry.FileName}");
                builder.AppendLine(entry.Content);
                builder.AppendLine();
            }
        }

        private static void AppendSharedStepContext(
            StringBuilder builder,
            IReadOnlyList<BatchStepPlan> allSteps,
            string sharedConventions,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            IReadOnlyList<StepInterface> stepInterfaces,
            string targetLanguage,
            string jobName)
        {
            builder.AppendLine($"Unified Batch Job Name: {jobName}");
            builder.AppendLine($"Target Language Stack: {targetLanguage}");
            var procedureSpecs = FeedbackSpec.OnlyProcedureSpecs(specs);
            builder.AppendLine($"Total Legacy Stored Procedures to Consolidate: {procedureSpecs.Count} procedures");
            builder.AppendLine();
            builder.AppendLine("[Provided Stored Procedure Specifications]");

            foreach (var spec in procedureSpecs)
            {
                builder.AppendLine("---");
                builder.AppendLine($"Filename: {spec.FileName}");
                builder.AppendLine("[Content Start]");
                builder.AppendLine(spec.Content);
                builder.AppendLine("[Content End]");
                builder.AppendLine();
            }

            // 피드백은 여기서 싣지 않는다. 이 블록은 회차 간 바이트가 같아야 캐시가 사는
            // 접두사이고, 피드백은 회차마다 바뀐다. 각 호출부가 프롬프트 말미(단계 섹션은
            // volatileSuffix)에 싣는다.

            // 목록이 비면 머리글도 내지 않는다. 빈 머리글은 "승인된 단계가 하나도
            // 없다"는 뜻으로 읽혀, 목차를 못 읽은 것과 목차가 단계를 안 냈다는 것이
            // 구분되지 않는다.
            if (allSteps.Count > 0)
            {
                builder.AppendLine("[Approved Step List]");
                foreach (var candidate in allSteps)
                {
                    builder.AppendLine(
                        $"- {candidate.Code} | {candidate.Name} " +
                        $"| Legacy: {string.Join(", ", candidate.LegacyProcedures)} " +
                        $"| Tables: {string.Join(", ", candidate.TargetTables)} " +
                        $"| ErrorCodes: {string.Join(", ", candidate.ErrorCodes)} " +
                        $"| Chunkable: {candidate.Chunkable}");
                }

                builder.AppendLine();
            }

            // 골격 호출은 sharedConventions를 아직 갖고 있지 않다(자신이 그것을
            // 써야 하는 쪽이다). 그 호출에도 이 헤더를 무조건 찍으면 "규약이 이미
            // 문서에 있다"고 거짓 전제를 주게 되므로, 내용이 있을 때만 낸다.
            if (!string.IsNullOrWhiteSpace(sharedConventions))
            {
                builder.AppendLine("[Shared Conventions Already Written In The Document]");
                builder.AppendLine(sharedConventions);
                builder.AppendLine();
            }

            // 제어 계약 표는 고정 자산이라 재료 유무와 무관하게 항상 싣는다. 단계별
            // 어휘가 갈리면(StepStatus vs ExecutionStatus, Succeeded vs Completed) 하나의
            // DDL이 모든 단계를 만족시키지 못해 재시작이 매 실행 막힌다.
            builder.AppendLine();
            builder.AppendLine("[Batch Control Table Contract]");
            builder.AppendLine("These four tables are FIXED. Use exactly these column names and status values.");
            builder.AppendLine("Do NOT invent alternatives such as ExecutionStatus, StepState, CompletionStatus,");
            builder.AppendLine("BatchJobName, StartedAt, or DetailMessage. NEVER use the status value 'Completed' -");
            builder.AppendLine("success is 'Succeeded' everywhere. If two steps spell one logical table differently,");
            builder.AppendLine("no single DDL satisfies both and restart is blocked on every run.");
            builder.AppendLine();
            builder.Append(BatchControlContract.RenderPromptTable());

            // 재료가 비면 절 자체를 넣지 않는다. 빈 표를 실으면 모델이 "원본 파라미터가
            // 없다"로 읽어 있지도 않은 근거로 파라미터를 새로 지어낼 수 있다.
            var interfaceTable = StepInterfaceFacts.RenderPromptTable(stepInterfaces);
            if (interfaceTable.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("[Original Procedure Interface]");
                builder.AppendLine("The parameter list below is EXHAUSTIVE for each step. You MUST NOT add an input");
                builder.AppendLine("parameter that is not listed - not for restart, not for skipping, not for");
                builder.AppendLine("bypassing a guard. Steps whose code is absent from this table have no legacy");
                builder.AppendLine("origin, so design their interface from the plan structure instead.");
                builder.AppendLine();
                builder.Append(interfaceTable);
            }
        }

        public async Task<ReviewResult> ReviewConsolidatedPlanAsync(System.Collections.Generic.List<(string FileName, string Content)> specs, string planMarkdown, string jobName, string? effort = null, CancellationToken cancellationToken = default)
        {
            var systemPrompt = @"You are a principal database architect and critic agent reviewing a Consolidated Batch Modernization Plan. Assess if the plan accurately reflects the requirements and logic of the individual stored procedure specifications and meets modern technical criteria.

[Evaluation Criteria (Score 0-10 for each item)]
1. Business Logic and Flow Accuracy (ScoreAccuracy):
   - Assess if the business logic and rules of individual specifications are accurately preserved in the consolidated batch job.
   - Verify that queries using `UNION`, `UNION ALL`, or multi-table JOINs are preserved in full. Penalize if source tables or aggregation formulas were simplified, merged, or omitted.
   - Verify that every branch of a `UNION ALL` projects the same column list in the same order, including the constant discriminator columns (`0 AS USESTATE`, `2 AS USESTATE`, `3 AS USESTATE`). A branch that omits a discriminator the other branches carry makes the statement invalid and loses the value that told the branches apart.
   - Verify that each DML statement carries the specification's update number as a single comment immediately before it (`/* U13: ... */`). Without that anchor the mechanical check cannot bind the statement to the specification's DML scope table, so the join-key and predicate-column comparison is not run at all - it does not fail, it silently does not happen. Penalize a step whose DML statements carry no anchor, and penalize an anchor split across two comments.
   - Verify that a scalar subquery in an assignment was NOT rewritten as `CROSS APPLY`/`OUTER APPLY` unless the specification already used that form. A scalar subquery assigns `NULL` when it finds nothing; `CROSS APPLY` drops the row from the update entirely, taking the other column assignments in the same statement with it.
   - Verify that several non-aggregate lookups were NOT merged into one aggregate statement unless the specification already used that form. Where the specification writes `SELECT @v = col` followed by an `@@ROWCOUNT > 1` branch, it is separating 'none' from 'several'; `MAX(col)` changes 'none' from `0` to `NULL` and inverts the branch.
2. Data Model and CRUD Completeness (ScoreCrud):
   - Verify if table CRUD accesses are properly sequenced and chunked (Paging Reader) in the data pipeline.
   - For chunked DELETE-INSERT patterns, verify if chunking keys are added to the DELETE filter to prevent unintended full-table deletions.
   - Verify if original business filters (e.g., `WHERE Status = 'P'`) are strictly preserved alongside chunking ranges. Penalize if original filters are omitted.
3. Integration and Interface Definition (ScoreInterface):
   - Assess if parameter mapping, data exchange contracts, and API integration requirements are fully detailed.
   - Verify that NO step added an input parameter for restart, skipping, or bypassing a guard. A step's interface is exactly the parameter list of the original procedure it replaces; restart skipping happens outside the step, in the orchestrator. Penalize heavily if a guard was made switchable by a caller.
   - Verify that every NEW batch-only object (staging table, journal, checkpoint, control-total table) lives in the `batch` schema and every shadow table lives in `batch_shadow`. Penalize a job-named schema such as `poqbatch` or `<JobName>Batch`, and penalize any object placed under `dbo` that the plan itself creates - the bootstrap round only creates objects found under those two schema names.
   - Verify that all steps spell the control tables and their status values identically (`Succeeded`, never `Completed`; no `ExecutionStatus`/`StepState`/`CompletionStatus` variants). If two steps spell one logical table differently, no single DDL satisfies both and restart is blocked on every run.
   - Verify that a step with NO legacy origin returns only codes from its own reserved block (block start = -9000 - N*10 for `S<N>`, 10 codes), and never a non-numeric code such as `B161` - `DECLARE @v_currentStepId INT = B161` does not compile because B161 is an unresolved identifier. A step that replaces a legacy procedure must keep that procedure's original codes and must NOT use the reserved band.
   - Verify that a step with no legacy origin assigns only integers from its reserved block to its state variable, and never a string status code (`N'B120'`, `N'BATCH-LOCK-001'`). Two exceptions: a step code from this Job's step list (`N'S01'`) stays a string wherever it is used - every `StepCode` column in the control contract is `nvarchar(10)`, and a variable that names another step (the first incomplete step on restart) is identity, not a code. Checkpoint and execution status values (`Running`, `Succeeded`, `Failed`, `Skipped`, `Pending`, `Held`, `Released` and the other values the Batch Control Table Contract defines) are also not step error codes and stay strings.
4. Exception Handling, Transaction and Isolation Policy (ScoreException):
   - Check that every step runs under SNAPSHOT isolation (Penalize heavily if `ALTER DATABASE SET READ_COMMITTED_SNAPSHOT ON` is proposed). Do NOT penalize a step for not spelling out where the isolation level is set - that is the implementing round's choice - and penalize a step that writes the isolation statement into its own SQL instead of stating the obligation.
   - Check that a failing statement cannot leave a partial commit behind, and verify that the EXACT original error codes are preserved and recorded at the point of failure (Penalize if error codes are remapped, omitted, or collapsed into one generic failure value).
   - Check if Checkpoint-based Step Skip logic (Restartability) is clearly defined so completed steps do not block restarts with pre-validation errors.
   - Shadow tables are a LAST RESORT, not a requirement. A step whose work fits in one transaction and relies on rolling back that single transaction is CORRECT - do NOT penalize it for having no shadow table, and penalize it if it adds a compensating DELETE in the failure path on top of the rollback (that deletes rows the rollback already restored). ONLY for steps that actually use a shadow: check that the strategy covers ALL target tables the step modifies, defines a capacity/purge policy, and includes explicit Rollback/Restore pseudo-code.
   - Check that no `WITH (NOLOCK)` or `NOLOCK` hints remain anywhere in the generated pseudocode. They force READ UNCOMMITTED and violate the SNAPSHOT isolation policy. Penalize heavily if any remain.
   - For INSERT-only steps, verify the rollback relies on rolling back the transaction or on an explicit `DELETE WHERE [ChunkKey]` compensation rather than a Shadow table.
   - Verify that Shadow restore logic DELETEs the affected target range before re-inserting from the Shadow table. Restoring without the preceding DELETE duplicates rows.
   - Check that every iteration of a chunking loop commits its own work in its own transaction, rather than wrapping the entire loop in a single outer transaction. Penalize if chunks are not committed independently.
   - Check that the plan defines NO new stored procedure, function, or trigger, and that transaction boundaries and error handling live in the application code rather than in a database-side procedure body. Penalize a `CREATE PROCEDURE` that is not a quotation of the original legacy procedure being replaced.
   - Check that no statement the step sends branches on its own outcome. Penalize `GOTO` error labels, `IF @@ERROR <> 0` checks, and `BEGIN TRY`/`END CATCH` wrappers written into the step's own SQL - control flow and error handling belong to the application code. A quotation of the original legacy procedure is exempt.
   - Check that the pseudocode names NO type from a real data-access framework (`SqlConnection`, `SqlCommand`, `SqlParameter`, `IsolationLevel.Snapshot`, `TransactionScope`, `DbContext`, `PreparedStatement`, ...). The target application does not exist yet, so naming one of these pins a mechanism the implementing round is supposed to choose. Generic placeholder names that only show the shape (`conn.beginTransaction()`, `connectionFactory.open()`) are CORRECT - do NOT penalize them, and do NOT ask for a concrete type. Separately, penalize a document that calls one thing by two different invented names across steps - one notation must serve the whole document, or the implementing round inherits two fictional APIs where it needed none.
5. Diagram Syntax and Readability (ScoreReadability):
   - Ensure the Mermaid flowchart diagram has no syntax errors, wraps node labels in double quotes, and arrow labels are clean of special characters.
   - Ensure 'subgraph' keyword and its ID are separated by a space (e.g., `subgraph SHARED_DB`).

[Defect Judgment]
- If any of the 5 criteria scores less than 8 points, or if any of the 4 mandatory H2 headers (## 통합 배치 아키텍처 개요, ## Mermaid 기반 통합 흐름도, ## 단계별 이행 상세 및 의사코드, ## 통합 데이터 정합성 검증 SQL 세트) is missing, mark HasDefects as true.

[Defective Step Attribution]
- `DefectiveSteps` MUST list the step codes (e.g. `S08`) of the `###` sections under `## 단계별 이행 상세 및 의사코드` that caused the defects, using the exact codes as written in the document.
- Include a step ONLY when rewriting that one section would fix the defect. Leave the array EMPTY when the defect is document-wide (a missing H2, a broken flowchart, an inconsistency across steps).
- An empty array causes the whole document to be regenerated, so listing steps precisely is what makes the repair cheap.

[Output Format]
Output ONLY the final JSON payload. Do not include markdown block markers (```json) or conversational text. Output raw JSON:
{
  ""HasDefects"": true or false (boolean),
  ""FeedbackComment"": ""Detailed correction instructions if defects are found. Return empty string if HasDefects is false."",
  ""DefectiveSteps"": [""S08"", ""S10""],
  ""ScoreAccuracy"": 10,
  ""ScoreCrud"": 10,
  ""ScoreInterface"": 10,
  ""ScoreException"": 10,
  ""ScoreReadability"": 10
}";

            // 프롬프트를 캐시 접두사(고정)와 회차별 가변부로 나눈다.
            //
            // 명세서는 회차 간 바이트가 같고 실측 481KB로 가변부보다 크다. 잡 이름은
            // 잡마다 달라지므로 앞에 두면 그 한 줄이 뒤의 명세서 전량을 무효로 만든다 —
            // 캐시는 접두사 일치이기 때문이다. 계획서 본문은 회차마다 재생성되므로
            // 애초에 캐시 대상이 아니다.
            var stablePrompt = new StringBuilder();
            stablePrompt.AppendLine("[Provided Stored Procedure Specifications]");

            // 피드백은 접두사에 넣지 않는다 - 회차마다 내용이 바뀌므로 접두사 일치가
            // 매 회차 깨져 명세서 전량(실측 481KB)의 캐시가 통째로 무효가 된다.
            foreach (var spec in FeedbackSpec.OnlyProcedureSpecs(specs))
            {
                stablePrompt.AppendLine($"---");
                stablePrompt.AppendLine($"Filename: {spec.FileName}");
                stablePrompt.AppendLine(spec.Content);
                stablePrompt.AppendLine();
            }

            var volatileSuffix = new StringBuilder();
            AppendFeedbackSection(volatileSuffix, specs);
            volatileSuffix.AppendLine($"Unified Batch Job Name: {jobName}");
            volatileSuffix.AppendLine();
            volatileSuffix.AppendLine("[Consolidated Batch Modernization Plan Markdown]");
            volatileSuffix.AppendLine(planMarkdown);
            volatileSuffix.AppendLine();
            volatileSuffix.AppendLine("Please review the consolidated plan and output the JSON result.");

            Log.Information("AI 통합 배치 계획서 리뷰 요청 전송 - JobName: {JobName}, Effort: {Effort}", jobName, effort ?? "Default");
            var mergedPrompt = PromptComposition.MergeVolatileSuffix(
                stablePrompt.ToString(), volatileSuffix.ToString());
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, mergedPrompt);

            var aiResult = await _aiClient.ChatAsync(
                systemPrompt,
                stablePrompt.ToString(),
                0.1f,
                effort,
                volatileUserSuffix: volatileSuffix.ToString(),
                cancellationToken: cancellationToken);

            Log.Information("AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: {JobName}, 응답 길이: {Length}", jobName, aiResult?.Content?.Length ?? 0);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult?.Content);
            var reviewResult = ParseReviewResult(aiResult?.Content, jobName);
            reviewResult.ThinkingText = aiResult?.ThinkingText;
            return reviewResult;
        }

        private static string ExtractJson(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            content = content.Trim();

            // ```json ... ``` 블록 추출 시도
            int jsonStartIndex = content.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (jsonStartIndex != -1)
            {
                int start = jsonStartIndex + 7;
                int end = content.IndexOf("```", start);
                if (end != -1)
                {
                    return content.Substring(start, end - start).Trim();
                }
            }

            // ``` ... ``` 블록 추출 시도 (json 키워드가 없는 경우)
            int blockStartIndex = content.IndexOf("```");
            if (blockStartIndex != -1)
            {
                int start = blockStartIndex + 3;
                int end = content.IndexOf("```", start);
                if (end != -1)
                {
                    return content.Substring(start, end - start).Trim();
                }
            }

            // 가장 바깥쪽의 { } 짝 추출 시도
            int firstBrace = content.IndexOf('{');
            int lastBrace = content.LastIndexOf('}');
            if (firstBrace != -1 && lastBrace != -1 && lastBrace > firstBrace)
            {
                return content.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            return content;
        }

        public async Task<AiResult> GenerateSettlementPolicyRulebookAsync(System.Collections.Generic.List<SpDefinition> spDefs, string profilingDataJson, CancellationToken cancellationToken = default)
        {
            var systemPrompt = @"You are a principal settlement policy analyst consolidating database stored procedure logic (DDL) and code/configuration settings (Data Profiling) into a business-level 'Settlement Rulebook'.
Combine the conditional logic and data mappings of SPs with common code master data, and write a policy rulebook in natural Korean.

[Required Content & Rules]
1. Map constants/status variables in code (e.g., WHERE Status = 'S02') to their actual business meanings in common code tables (e.g., 'S02' = 'Settlement Pending').
2. Write the policy rulebook in Korean Markdown format using exactly these 5 H2 headers:
   ## 1. 개요 및 목적
   ## 2. 핵심 정산 비즈니스 규칙 정의
   ## 3. 코드값 및 마스터 데이터 매핑 정보
   ## 4. 프로그램별 정산 영향도 매핑
   ## 5. 예외 처리 및 제약 사항
3. Utilize tables and diagrams where possible to optimize readability.
4. Do not wrap the entire response in a markdown code block. However, you MUST use ```mermaid blocks for flowcharts.";

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine("[Stored Procedure DDL & Dependecy Info]");
            foreach (var sp in spDefs)
            {
                userPrompt.AppendLine($"### SP: {sp.Schema}.{sp.Name}");
                userPrompt.AppendLine("#### [DDL Source]");
                userPrompt.AppendLine("```sql");
                userPrompt.AppendLine(sp.DdlText);
                userPrompt.AppendLine("```");
                userPrompt.AppendLine("#### [Dependencies]");
                foreach (var dep in sp.Dependencies)
                {
                    userPrompt.AppendLine($"- Object: {dep.Schema}.{dep.Name} ({dep.Type})");
                    if (dep.Columns != null && dep.Columns.Count > 0)
                    {
                        userPrompt.AppendLine("  * Columns:");
                        foreach (var col in dep.Columns)
                        {
                            var desc = string.IsNullOrEmpty(col.Description) ? "No description" : col.Description;
                            userPrompt.AppendLine($"    - {col.ColumnName} ({col.DataType}): {desc}");
                        }
                    }
                }
                userPrompt.AppendLine();
            }

            userPrompt.AppendLine("[Master/Common Code Data Profiling Results (JSON)]");
            userPrompt.AppendLine("```json");
            userPrompt.AppendLine(profilingDataJson);
            userPrompt.AppendLine("```");
            userPrompt.AppendLine();
            Log.Information("AI 정산 정책서 생성 요청 전송");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt.ToString());

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), _temperature, effort: null, cancellationToken: cancellationToken);

            if (aiResult == null)
            {
                aiResult = new AiResult();
            }
            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt.ToString();

            Log.Information("AI 정산 정책서 생성 완료 - 응답 길이: {Length}", aiResult.Content?.Length ?? 0);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult.Content);

            return aiResult;
        }
    }
}
