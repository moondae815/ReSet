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
                        staticAnalysisText.AppendLine("    (SELECT 대상 테이블은 CRUD 분석 표에 각각 독립적인 조회(SELECT) 참조 행으로 조건/참조 컬럼과 함께 완전하게 기술되어야 합니다.)");
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
                            staticAnalysisText.AppendLine($"    <update-target table=\"{mapping.TargetTable}\" statement=\"{mapping.StatementOrdinal}\">");
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

            bool hasComments = spDef.DdlText.Contains("--") || spDef.DdlText.Contains("/*");

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
                rules.Add($"{ruleIndex++}. If the source code of a referenced User Defined Function (UDF) is provided, analyze its logic. If the UDF DDL is missing, output 'UDF definition not provided; detailed logic excluded from analysis' and state its calling location and purpose based on facts.");
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
                var ddlLines = spDef.DdlText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
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
                checklistSb.AppendLine($"- [ ] ## CRUD 분석 섹션에 호출되는 UDF({string.Join(", ", spDef.StaticAnalysis.ReferencedFunctions)})의 활용 비즈니스 규칙을 명확히 기재하셨습니까?");
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
        /// 마크다운 표 셀에 넣을 수 있게 다듬는다. SET 우변에 비트 연산자 `|`가 들어가면
        /// (예: FLAGS | 4) 셀 경계로 읽혀 표가 통째로 어긋난다. 개행도 같은 이유로 접는다.
        /// </summary>
        private static string EscapeTableCell(string expression)
        {
            if (string.IsNullOrEmpty(expression)) return string.Empty;

            return expression
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Replace("|", "\\|");
        }

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
        private static List<string> BuildUpdateMappingTemplateLines(IReadOnlyList<AstUpdateMapping> updateMappings)
        {
            var lines = new List<string>();
            foreach (var mapping in updateMappings)
            {
                lines.Add($"   ### UPDATE 대상 테이블: {mapping.TargetTable} (문장 {mapping.StatementOrdinal})");
                lines.Add("   | 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |");
                lines.Add("   | :--- | :--- | :--- | :--- |");
                foreach (var assignment in mapping.Assignments)
                {
                    lines.Add($"   | {mapping.TargetTable} | {assignment.Column} | {EscapeTableCell(assignment.SourceExpression)} | (FILL_DESCRIPTION_HERE) |");
                }

                if (!string.IsNullOrEmpty(mapping.FromClauseText))
                {
                    lines.Add("   위 문장은 FROM 절을 동반합니다. 갱신 대상은 FROM 절에 등장하는 해당 별칭의 인스턴스입니다. 조인이 대상 행 하나에 여러 소스 행을 매칭시킬 경우 T-SQL은 어느 값이 반영될지 정의하지 않습니다(비결정적). 조인 키의 유일성이 보장되는지 판단할 수 없으면 \"보장되지 않으면 결과가 비결정적\"이라는 사실만 기술하고, 유일성 여부를 추측하지 마십시오.");
                }

                if (mapping.SelfReferencedColumns.Count > 0)
                {
                    lines.Add($"   다음 컬럼은 SET 우변에서 자기 자신을 참조합니다: {string.Join(", ", mapping.SelfReferencedColumns)}. SQL의 SET 절은 우변을 모두 **갱신 전 값**으로 동시에 평가합니다. 절차형 언어로 이행할 때 순차 대입하면 계산 결과가 달라지므로, 이 사실을 `## CRUD 분석`에 명시적으로 기술하십시오.");
                }

                lines.Add("");
            }

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
            systemPrompt += $"\n\n[USER INSTRUCTIONS]\n{userInstructions}";

            var (dependenciesText, tableSchemasText, referenceDdlsText, staticAnalysisText) = BuildSpMetadataTexts(functionDef);
            var returnInfo = functionDef.FunctionReturn;
            var returnContract = returnInfo == null
                ? "Return metadata is not available. Derive only what is explicit in the DDL."
                : returnInfo.IsTableValued
                    ? $"Table-valued function. Result columns:\n{string.Join("\n", returnInfo.Columns.Select(FormatFunctionReturnColumn))}"
                    : $"Scalar function return type: {returnInfo.DataType}";

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

Based on the reference context above, reverse engineer the user defined function and write the Korean markdown specification.";

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
        /// </summary>
        private const string ConsolidatedPlanRules = @"[Required Content & Rules]
1. Write the document in Korean Markdown format.
2. The document must use only 4 mandatory H2 headers:
   - ## 통합 배치 아키텍처 개요: Define how the individual stored procedures translate into steps (sequential chain, conditional branches, parallel processing) within the unified batch job.
   - ## Mermaid 기반 통합 흐름도: Draw a Mermaid flowchart diagram depicting the data pipeline and steps.
     * Wrap all node text labels in double quotes. Do not use double quotes or special characters in arrow labels. Node IDs must be unique alphanumeric words.
     * ALWAYS add a space between the 'subgraph' keyword, its ID, and the bracket label (e.g., `subgraph SP1 [""Label""]`). Do not write `subgraph SP1[""Label""]`. Do not use parentheses '()' or brackets '[]' in arrow labels (e.g., use `|OutState IN 1, 5|` instead of `|OutState IN (1,5)|`).
   - ## 단계별 이행 상세 및 의사코드: Design the classes/components, chunk paging pseudocode, locks/transaction controls, and error restartability/recovery strategies.
   - ## 통합 데이터 정합성 검증 SQL 세트: Include validation SQL templates checking data integrity.
3. [Concurrency & Execution Order] Strictly preserve the sequential execution order of the original stored procedures. Do NOT propose parallel execution for steps that perform DML on the same target table, as it causes data consistency conflicts.
4. [Transaction Isolation & Shadow Table] When converting single bulk transactions into chunked commits, you MUST define an isolation/visibility strategy. NEVER propose `ALTER DATABASE SET READ_COMMITTED_SNAPSHOT ON` as it is too risky. Instead, use Session-level `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`. If you propose Shadow tables or Before-Image capturing for rollback: (a) The Shadow strategy MUST cover ALL target tables modified by the step, (b) you MUST define the storage capacity strategy and a purge policy (e.g., auto-drop after 24 hours), and (c) you MUST explicitly provide Rollback/Restore pseudo-code that DELETEs the affected range FIRST and only then re-inserts from the Shadow table (e.g., `DELETE FROM Target WHERE BatchDate = @BatchDate;` followed by `INSERT INTO Target SELECT * FROM Shadow WHERE BatchDate = @BatchDate;`). Restoring without the preceding DELETE duplicates rows.
5. [Idempotency & Restartability] You MUST design a Checkpoint-based Step Skip logic. If the batch fails at Step 06 and restarts, previous steps (like Step 01) that were already completed MUST NOT abort the entire batch with pre-validation errors (e.g., -9 error). Provide a `@pi_bypassPreCheck` parameter or explicit skip logic in your pseudocode so that completed steps are safely skipped upon restart.
6. [Data Modification & Error Handling] When chunking a DELETE-INSERT pattern, you MUST ensure the chunking key is added to the DELETE filter to prevent full-table deletion conflicts. If the step involves multi-table aggregations (`GROUP BY`) or complex cross-DB joins where chunking by a single Primary Key is mathematically impossible, explicitly declare that the step uses 'Single-Transaction Shadow Swap' instead of chunking, and DO NOT add fake chunk keys to the pseudo-code.
6-1. [Precise Error Tracking] If the original SP lacked `@@ERROR` checking, you MUST resolve it using `TRY...CATCH` and `XACT_ABORT ON`. However, DO NOT just return a generic `-1` in the `CATCH` block. You MUST declare a state variable at the top (e.g., `DECLARE @v_currentStepId INT = 0;`), update it before each DML with the exact original error code (e.g., `SET @v_currentStepId = -101;`), and return that variable in the CATCH block to preserve the exact point of failure. Use structured `TRY...CATCH` exclusively for error handling; NEVER use legacy `GOTO`-based error branching.
7. [Anti-Shortcut for Business Logic] You MUST NOT simplify queries that use UNION, UNION ALL, or complex JOINs across multiple tables. Preserve all source tables and aggregation formulas in the pseudocode and descriptions.
8. [Preserve Chunking Filters] When chunking operations (e.g., `WHERE ID BETWEEN @start AND @end`), you MUST retain the original business logic filters (e.g., self-joins, cursor criteria, status checks) and combine them with the chunking range using `AND`. Do not delete the original filters.
8-1. [Chunk Transaction Boundary] Every iteration of a chunking `WHILE` loop MUST open and close its own explicit `BEGIN TRAN` / `COMMIT TRAN` boundary so that each chunk commits independently and a mid-run failure leaves earlier chunks durably committed. Do NOT wrap the entire loop in a single outer transaction.
9. [Error Codes] You MUST strictly reuse the EXACT original error codes from the source SPs. DO NOT remap or invent new error codes (e.g., changing -1 to -11). DO NOT use continuous ranges (e.g., `-1~-23`).
10. [NOLOCK Prohibition] Since SNAPSHOT isolation is used, you MUST explicitly remove all `WITH (NOLOCK)` or `NOLOCK` hints from the generated pseudocode. `NOLOCK` forces READ UNCOMMITTED and completely violates the SNAPSHOT isolation policy.
11. [INSERT-only Rollback] For INSERT-only steps, do not use a Shadow table for rollback. Instead, rely solely on `ROLLBACK TRAN` for single transactions, or provide an explicit `DELETE WHERE [ChunkKey]` compensation logic. Do NOT perform `DELETE` immediately after `ROLLBACK TRAN` inside a CATCH block for a single transaction as it is redundant.
12. [Chunk Key Validation] You MUST CROSS-CHECK the provided DDL/Schema for the target table before writing the chunking key. Ensure the key column (e.g., `CLIENTID`) actually exists. If it doesn't exist, use an alternative primary key or composite hash (e.g., `PGNAME+MALLID`) that strictly exists in the target schema.
13. [Output Parameters Interface] All output parameters (e.g., `@po_strErrMsg`, `@po_intRetVal`) from the original procedures MUST be accurately mapped in the unified batch context interface and error code tables. Do not omit them.
14. Do not wrap the entire response in a markdown code block. However, you MUST use ```mermaid blocks for flowcharts.
15. Do not append any conversational filler, polite greetings, or unrelated explanations at the end. Terminate immediately.

[Few-Shot Examples for Modernization Patterns]
* Shadow Table Swap Pattern (For complex aggregations where chunking is impossible):
```sql
-- Create Shadow Table
SELECT * INTO TargetTable_Shadow_YYYYMMDD FROM TargetTable WHERE 1=0;
-- Bulk Insert into Shadow
INSERT INTO TargetTable_Shadow_YYYYMMDD (Col1, Col2)
SELECT Col1, SUM(Col2) FROM SourceTable GROUP BY Col1;
-- Single Transaction Swap
BEGIN TRAN;
  DELETE FROM TargetTable; -- Original target data purge
  INSERT INTO TargetTable SELECT * FROM TargetTable_Shadow_YYYYMMDD;
COMMIT TRAN;
```

* Chunking Pattern (Combining chunking keys with existing business filters):
```sql
DECLARE @MinID INT, @MaxID INT, @CurrentID INT, @ChunkSize INT = 10000;
SELECT @MinID = MIN(ID), @MaxID = MAX(ID) FROM SourceTable WHERE Status = 'P'; -- Existing filter
SET @CurrentID = @MinID;
WHILE @CurrentID <= @MaxID
BEGIN
    BEGIN TRAN;
    INSERT INTO TargetTable (ID, Col1)
    SELECT ID, Col1 FROM SourceTable
    WHERE Status = 'P' -- Preserve original filter!
      AND ID >= @CurrentID AND ID < @CurrentID + @ChunkSize; -- Chunking condition
    COMMIT TRAN;
    SET @CurrentID = @CurrentID + @ChunkSize;
END
```

* Shadow Table Restore in CATCH block for DELETE-INSERT patterns:
```sql
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    -- MUST DELETE target data for the chunk range or specific YMD BEFORE inserting from shadow to avoid duplicates!
    DELETE FROM TargetTable WHERE BatchDate = @BatchDate; 
    INSERT INTO TargetTable SELECT * FROM TargetTable_Shadow_YYYYMMDD WHERE BatchDate = @BatchDate;
    THROW;
END CATCH
```

* INSERT-only Compensation (No Shadow table needed for rollback):
```sql
-- If an INSERT-only chunked batch fails in the middle, rollback committed chunks using business keys:
DELETE FROM TargetTable WHERE BatchDate = @BatchDate AND ProcessStatus = 'NEW';
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
5. Identify all referenced User Defined Functions (UDFs) and detail their formulas, especially for calculations like CLVT and PGVT.
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
5. Identify all referenced User Defined Functions (UDFs) and detail their formulas, especially for calculations like CLVT and PGVT.
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

            bool hasComments = spDef.DdlText.Contains("--") || spDef.DdlText.Contains("/*");

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
                    "   - You must NEVER skip or declare a referenced UDF as 'not called' or 'excluded from analysis' if it is present in the dependency list and used in the DDL. Analyze the exact computation (e.g., UF_GET_ROUND4VAT, UF_GET_INCVTAXRATE) and document it fully.",
                    "   - For INSERT/UPDATE operations, you must list EVERY single column mapped in the INSERT/UPDATE statement (e.g. CLVT, PGVT, CLTOTAL, etc.). Omission of any target column is considered a critical failure.",
                    "   - You must separate SELECT tables, INSERT tables, UPDATE tables, and DELETE tables into their own respective sub-sections with separate Markdown tables. Do not mix them in a single table.",
                    "   - You must separate SELECT tables into individual rows. If the source JSON groups multiple tables in one string, you MUST manually separate them into distinct rows in the Markdown table, mapping only the specific columns referenced for each respective table.",
                    "   - Detail the column names referenced and join/filter keys without abbreviation.",
                    "   - The 'referenced-columns-per-table' in the static analysis metadata is the Source of Truth. Map these columns exactly without omitting any. Double-check all table and column names to ensure there are no spelling typos or hallucinations (e.g., use 'SeperateRate' and 'COMMISSIONCANCELAMT' exactly as defined in the source schema/DDL instead of hallucinated forms like 'SerateRate' or 'COMMATIONCANCELAMT').",
                    "3. For target tables of INSERT/UPDATE operations, map all target columns to their source values (variables, constants, function results, etc.) in a 1:1 mapping table. Do not abbreviate with 'etc.' or '...'.",
                    "4. Factually state the use case of temp tables (#TempTable), User Defined Functions (UDF), and Linked Servers. If not used, explicitly write that they are not used.",
                    "5. NEVER include columns in the CRUD table that do not exist in the provided schema metadata. If a column appears in the DDL but is missing from the schema, do not guess it as a normal column; mark it as a schema mismatch."
                };

                int rIdx = 6;
                if (hasUdf)
                {
                    sbRules.Add($"{rIdx++}. If the DDL of the referenced UDF is provided, analyze its operation. If missing, output 'UDF definition not provided; detailed logic excluded from analysis' along with its calling location and purpose.");
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
                    checklistSb.AppendLine($"- [ ] 호출되는 UDF({string.Join(", ", spDef.StaticAnalysis.ReferencedFunctions)})의 활용 비즈니스 규칙을 명확히 기재하셨습니까?");
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
                if (hasDynamicSql)
                {
                    sbRules.Add($"{rIdx++}. If dynamic SQL is present, explain the execution and business flow purpose in the logic summary.");
                }
                if (hasLinkedServers)
                {
                    sbRules.Add($"{rIdx++}. If Linked Server references are used, describe the external DB transaction flow and distributed transaction characteristics if applicable.");
                }

                sbRules.Add($"{rIdx++}. If `WITH(NOLOCK)` or `NOLOCK` read hints are used, analyze their transaction isolation implications (dirty read risk, data consistency impact) in the exception/constraint section.");
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

            var systemPrompt = @"You are a principal database architect and critic agent reviewing a generated stored procedure specification in Markdown. Evaluate the accuracy, completeness, and formatting of the document against the original metadata.

[Evaluation Criteria (Score 0-10 for each item)]
1. Business Logic and Flow Accuracy (ScoreAccuracy):
   - Check if the operations and branches of the source code are documented accurately without hallucination, arbitrary assumptions, or guesses.
2. Data Model and CRUD Completeness (ScoreCrud):
   - Verify if all SELECT/INSERT/UPDATE/DELETE tables and columns are documented 1:1 in a table format without shortcuts (e.g., no 'etc.').
   - Verify if temp tables, UDFs, and Linked Servers are factually detailed (or stated explicitly as not used).
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
DO NOT write code or detailed markdown plans. ONLY output your analysis: identify common domain logic, dependencies, and propose whether a Tasklet or Chunk-based architecture is more suitable.";

            var userPrompt = new System.Text.StringBuilder();
            userPrompt.AppendLine($"Unified Batch Job Name: {jobName}");
            userPrompt.AppendLine($"Target Language Stack: {targetLanguage}");
            userPrompt.AppendLine($"Total Legacy Stored Procedures to Consolidate: {specs.Count} procedures");
            userPrompt.AppendLine();
            userPrompt.AppendLine("[Provided Stored Procedure Specifications]");
            foreach (var spec in specs)
            {
                userPrompt.AppendLine($"---");
                userPrompt.AppendLine($"Filename: {spec.FileName}");
                userPrompt.AppendLine($"[Content Start]");
                userPrompt.AppendLine(spec.Content);
                userPrompt.AppendLine($"[Content End]");
                userPrompt.AppendLine();
            }
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

        public async Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default)
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
- `TargetTables` must list every table the step creates or modifies, as written in the source specifications.
- `ErrorCodes` must reproduce the EXACT original return codes of the source procedure. Do not invent, remap, or compress them into ranges.
- `Chunkable` is false when the step is an aggregation or cross-DB join that cannot be chunked by a single key.
- Emit the block once. Do not wrap the whole answer in a code block.";

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

        public async Task<AiResult> GenerateConsolidatedBatchPlanAsync(string planStructure, System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, CancellationToken cancellationToken = default)

        {
            var systemPrompt = $@"You are a principal database modernization architect consolidating multiple legacy stored procedure specifications into a single {targetLanguage} batch application and scheduler plan (Consolidated Batch Modernization Plan).
Consolidate the provided specifications into a single unified batch job named '{jobName}'.

" + ConsolidatedPlanRules;

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"Unified Batch Job Name: {jobName}");
            userPrompt.AppendLine($"Target Language Stack: {targetLanguage}");
            userPrompt.AppendLine($"Total Legacy Stored Procedures to Consolidate: {specs.Count} procedures");
            userPrompt.AppendLine();
            userPrompt.AppendLine("[Provided Stored Procedure Specifications]");

            foreach (var spec in specs)
            {
                userPrompt.AppendLine($"---");
                userPrompt.AppendLine($"Filename: {spec.FileName}");
                userPrompt.AppendLine($"[Content Start]");
                userPrompt.AppendLine(spec.Content);
                userPrompt.AppendLine($"[Content End]");
                userPrompt.AppendLine();
            }

            userPrompt.AppendLine();
            userPrompt.AppendLine("[Approved Document Structure & Plan]");
            userPrompt.AppendLine(planStructure);
            userPrompt.AppendLine();
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
            AppendSharedStepContext(userPrompt, steps, string.Empty, specs, targetLanguage, jobName);
            userPrompt.AppendLine("[Approved Document Structure & Plan]");
            userPrompt.AppendLine(planStructure);
            userPrompt.AppendLine();
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
            AppendSharedStepContext(userPrompt, allSteps, sharedConventions, specs, targetLanguage, jobName);

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
        private static void AppendSharedStepContext(
            StringBuilder builder,
            IReadOnlyList<BatchStepPlan> allSteps,
            string sharedConventions,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName)
        {
            builder.AppendLine($"Unified Batch Job Name: {jobName}");
            builder.AppendLine($"Target Language Stack: {targetLanguage}");
            builder.AppendLine($"Total Legacy Stored Procedures to Consolidate: {specs.Count} procedures");
            builder.AppendLine();
            builder.AppendLine("[Provided Stored Procedure Specifications]");

            foreach (var spec in specs)
            {
                builder.AppendLine("---");
                builder.AppendLine($"Filename: {spec.FileName}");
                builder.AppendLine("[Content Start]");
                builder.AppendLine(spec.Content);
                builder.AppendLine("[Content End]");
                builder.AppendLine();
            }

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

            // 골격 호출은 sharedConventions를 아직 갖고 있지 않다(자신이 그것을
            // 써야 하는 쪽이다). 그 호출에도 이 헤더를 무조건 찍으면 "규약이 이미
            // 문서에 있다"고 거짓 전제를 주게 되므로, 내용이 있을 때만 낸다.
            if (!string.IsNullOrWhiteSpace(sharedConventions))
            {
                builder.AppendLine("[Shared Conventions Already Written In The Document]");
                builder.AppendLine(sharedConventions);
                builder.AppendLine();
            }
        }

        public async Task<ReviewResult> ReviewConsolidatedPlanAsync(System.Collections.Generic.List<(string FileName, string Content)> specs, string planMarkdown, string jobName, string? effort = null, CancellationToken cancellationToken = default)
        {
            var systemPrompt = @"You are a principal database architect and critic agent reviewing a Consolidated Batch Modernization Plan. Assess if the plan accurately reflects the requirements and logic of the individual stored procedure specifications and meets modern technical criteria.

[Evaluation Criteria (Score 0-10 for each item)]
1. Business Logic and Flow Accuracy (ScoreAccuracy):
   - Assess if the business logic and rules of individual specifications are accurately preserved in the consolidated batch job.
   - Verify that queries using `UNION`, `UNION ALL`, or multi-table JOINs are preserved in full. Penalize if source tables or aggregation formulas were simplified, merged, or omitted.
2. Data Model and CRUD Completeness (ScoreCrud):
   - Verify if table CRUD accesses are properly sequenced and chunked (Paging Reader) in the data pipeline.
   - For chunked DELETE-INSERT patterns, verify if chunking keys are added to the DELETE filter to prevent unintended full-table deletions.
   - Verify if original business filters (e.g., `WHERE Status = 'P'`) are strictly preserved alongside chunking ranges. Penalize if original filters are omitted.
3. Integration and Interface Definition (ScoreInterface):
   - Assess if parameter mapping, data exchange contracts, and API integration requirements are fully detailed.
4. Exception Handling, Transaction and Isolation Policy (ScoreException):
   - Check if Session-level `SET TRANSACTION ISOLATION LEVEL SNAPSHOT` is used (Penalize heavily if `ALTER DATABASE SET READ_COMMITTED_SNAPSHOT ON` is proposed).
   - Check if `XACT_ABORT ON` is explicitly used with `TRY...CATCH`, and verify that the EXACT original error codes are preserved and returned (Penalize if error codes are remapped or omitted).
   - Check if Checkpoint-based Step Skip logic (Restartability) is clearly defined so completed steps do not block restarts with pre-validation errors.
   - Check if Shadow Table strategies cover all target tables, define capacity/purge policies, and include explicit Rollback/Restore pseudo-code.
   - Check that no `WITH (NOLOCK)` or `NOLOCK` hints remain anywhere in the generated pseudocode. They force READ UNCOMMITTED and violate the SNAPSHOT isolation policy. Penalize heavily if any remain.
   - For INSERT-only steps, verify the rollback relies on `ROLLBACK TRAN` or an explicit `DELETE WHERE [ChunkKey]` compensation rather than a Shadow table.
   - Verify that Shadow restore logic DELETEs the affected target range before re-inserting from the Shadow table. Restoring without the preceding DELETE duplicates rows.
   - Check that every iteration of a chunking `WHILE` loop opens and closes its own explicit `BEGIN TRAN` / `COMMIT TRAN` boundary, rather than wrapping the entire loop in a single outer transaction. Penalize if chunks are not committed independently.
   - Check that error handling relies exclusively on structured `TRY...CATCH`. Penalize if legacy `GOTO`-based error branching is used anywhere in the pseudocode.
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

            foreach (var spec in specs)
            {
                stablePrompt.AppendLine($"---");
                stablePrompt.AppendLine($"Filename: {spec.FileName}");
                stablePrompt.AppendLine(spec.Content);
                stablePrompt.AppendLine();
            }

            var volatileSuffix = new StringBuilder();
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
