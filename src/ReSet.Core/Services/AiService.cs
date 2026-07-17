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

        public string ProviderName => _aiClient.ProviderName;
        public string ModelName => _aiClient.ModelName;

        public AiService(IAiClient aiClient, float temperature, bool enableOllamaThinking = false, int criticScoreThreshold = 8)
        {
            _aiClient = aiClient ?? throw new ArgumentNullException(nameof(aiClient));
            _temperature = temperature;
            _enableOllamaThinking = enableOllamaThinking;
            _criticScoreThreshold = criticScoreThreshold;
        }

        private string FormatTableSchemaToMarkdown(DependencyInfo dep, SpDefinition spDef)
        {
            var sb = new System.Text.StringBuilder();
            var depFullName = string.IsNullOrEmpty(dep.Database)
                 ? $"{dep.Schema}.{dep.Name}"
                 : $"[{dep.Database}].[{dep.Schema}].[{dep.Name}]";
            sb.AppendLine($"### 테이블: {depFullName} ({dep.Type}) - 발견 깊이: {dep.DiscoveryDepth}단계");
            if (!string.IsNullOrEmpty(dep.Description))
            {
                sb.AppendLine($"* 테이블 설명: {dep.Description}");
            }
            sb.AppendLine();
            sb.AppendLine("| 컬럼명 | 데이터 타입 | Null 허용 | Identity | 기본값 | 제약 조건 | 설명 |");
            sb.AppendLine("| :--- | :--- | :---: | :---: | :--- | :--- | :--- |");
            
            // 엄격한 필터링 대상 컬럼 식별
            var keepCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            // 1) AST에서 감지한 실제 참조 컬럼 추가
            if (spDef.StaticAnalysis != null && spDef.StaticAnalysis.ReferencedColumnsPerTable != null)
            {
                foreach (var kvp in spDef.StaticAnalysis.ReferencedColumnsPerTable)
                {
                    if (kvp.Key.Contains(dep.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var c in kvp.Value) keepCols.Add(c);
                        break;
                    }
                }
            }
            
            // 2) PK / FK 컬럼 추가
            foreach (var col in dep.Columns)
            {
                if (col.IsPrimaryKey || col.IsForeignKey)
                {
                    keepCols.Add(col.ColumnName);
                }
            }

            // 3) 인덱스 구성 컬럼 추가
            if (dep.Indexes != null)
            {
                foreach (var idx in dep.Indexes)
                {
                    foreach (var c in idx.Columns) keepCols.Add(c);
                }
            }

            foreach (var col in dep.Columns)
            {
                // 필터링 적용 (keepCols가 비어있는 경우는 정적 분석 정보가 없는 것으로 보고 폴백으로 모든 컬럼 출력)
                if (keepCols.Count > 0 && !keepCols.Contains(col.ColumnName))
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

        private (string dependenciesText, string tableSchemasText, string referenceDdlsText, string staticAnalysisText) BuildSpMetadataTexts(SpDefinition spDef)
        {
            var dependenciesText = new StringBuilder();
            var tableSchemasText = new StringBuilder();
            var referenceDdlsText = new StringBuilder();

            foreach (var dep in spDef.Dependencies)
            {
                dependenciesText.AppendLine($"- Schema: {dep.Schema}, Name: {dep.Name}, Type: {dep.Type} (발견 깊이: {dep.DiscoveryDepth}단계)");
                
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
                else if (dep.Type.Contains("FUNCTION") || dep.Type.Contains("PROCEDURE"))
                {
                    referenceDdlsText.AppendLine($"### 객체: {dep.Schema}.{dep.Name} ({dep.Type}) [DDL 소스코드 수집 실패 / 미제공]");
                    referenceDdlsText.AppendLine("*이 객체의 정의 DDL이 시스템 상에서 수집되지 않았습니다. 내부 알고리즘 분석을 건너뛰고 호출 위치만 기록하십시오.*");
                    referenceDdlsText.AppendLine();
                }
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
                    
                    staticAnalysisText.AppendLine($"  * UPDATE 대상 테이블: {(spDef.StaticAnalysis.UpdateTables.Count > 0 ? string.Join(", ", spDef.StaticAnalysis.UpdateTables) : "없음")}");
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
                "당신은 SQL Server Stored Procedure 분석 전문가입니다. 다음 규칙을 준수하여 마크다운 기능 명세서를 작성하십시오.",
                "",
                "[분석 기본 규칙]",
                "1. 분석 대상 SP 뿐만 아니라 제공된 참조 테이블 스키마 컬럼 정보 및 참조 UDF/SP 소스코드를 모두 참고하여 분석 보고서를 한글로 성실히 작성하십시오.",
                "2. SP 내부에서 참조 테이블의 어떤 컬럼 값을 제어/수정하고 조건식에 쓰는지 파라미터 구조와 매핑하여 작성하십시오."
            };

            int ruleIndex = 3;
            if (hasUdf)
            {
                rules.Add($"{ruleIndex++}. SP에서 호출하는 사용자 정의 함수(UDF)의 정의(소스코드)가 제공된 경우에 한해 연산 알고리즘을 분석하여 포함시키십시오. 만약 UDF 소스코드 DDL이 제공되지 않았다면, 임의로 내부 알고리즘을 추정하여 단정하지 말고 'UDF 정의 미제공으로 상세 로직 분석 제외' 및 '호출 위치 및 매개변수 사용 목적'만을 사실에 기반하여 기록하십시오.");
            }

            rules.Add($"{ruleIndex++}. 비즈니스 흐름을 직관적으로 이해할 수 있는 Mermaid Flowchart 다이어그램을 필수로 포함해 마크다운으로 구성해 주십시오. ");
            rules.Add("   - 노드 정의 시 특수문자나 괄호가 들어가 린팅 에러가 발생하지 않도록 텍스트 전체를 반드시 이중 큰따옴표로 감싸십시오. (예: id1[\"\"사용자 조회 (ID 체크)\"\"] --> id2[\"\"결과 반환\"\"])");
            rules.Add("   - 노드 ID는 반드시 영문자/숫자 조합의 고유 식별자(예: Node1, Node2)로 정의하고, 보여줄 한글 텍스트는 이중 큰따옴표 안에 기술하십시오. 괄호만으로 노드를 구성하거나 Mermaid 예약어(graph, flowchart, subgraph, end 등)를 노드 ID로 사용해서는 안 됩니다.");
            rules.Add("   - 연결선(화살표) 위에 조건 텍스트를 적을 때(예: -->|텍스트|), 텍스트 부분에 절대 큰따옴표 기호(쌍따옴표)나 괄호, 특수기호를 사용하지 마십시오. (예: 화살표 중간에 '존재' 또는 '성공'을 표시하려면, 기호 없이 반드시 -->|존재| 또는 -->|성공| 과 같이 순수 텍스트만 적어야 하며, -->|\"\"성공\"\"| 이나 -- \"\"성공\"\" --> 와 같이 따옴표를 쓰면 절대 안 됩니다.)");
            rules.Add("   - 노드 내부 텍스트에는 골뱅이(@) 변수명 기호를 절대 포함하지 마십시오. (예: @po_intRetVal 대신 \"\"출력 실패 코드 설정\"\" 또는 \"\"실패 코드 반환\"\"과 같이 자연어로 순화하여 기술) 단, '@@ERROR' 등 SQL 내장 시스템 에러 코드에 한해서는 가독성을 위해 예외적으로 기입을 허용하되, 특수문자로 인해 린팅이 깨지지 않도록 노드 정의 전체를 반드시 이중 큰따옴표로 감싸십시오.");

            if (hasDynamicSql)
            {
                rules.Add($"{ruleIndex++}. SP 내에 동적 SQL(예: EXEC, EXECUTE, sp_executesql을 통한 문자열 쿼리 실행)이 존재하는 경우, 동적으로 구성되어 실행되는 SQL의 목적과 대상 테이블을 코드 흐름 상에서 최대한 식별하여 CRUD 분석 및 비즈니스 로직 요약에 누락 없이 반영하십시오.");
            }
            if (hasLinkedServers)
            {
                rules.Add($"{ruleIndex++}. SP 내에서 Linked Server를 통한 원격 참조(4파트 식별자: Server.Database.Schema.Table 형식을 사용하는 참조)가 발견되면, 해당 외부 DB/테이블 의존성과 데이터 연동 목적을 명확히 분석하여 포함하십시오.");
            }

            rules.Add($"{ruleIndex++}. 응답 전체를 백틱(```markdown ... ```) 코드 블록으로 감싸지 마십시오. 반드시 마크다운 헤더(예: ## 개요)로 시작하는 텍스트 형태로 직접 출력을 수행해야 합니다.");
            rules.Add($"{ruleIndex++}. 최종 작성된 마크다운 문서의 대분류(H2) 헤더는 반드시 다음 5가지 명칭을 정확히 그대로 사용해야 합니다: `## 개요`, `## 파라미터 목록`, `## CRUD 분석`, `## 로직 흐름 요약`, `## 비즈니스 흐름 시각화`. 임의로 영어 명칭을 혼용하거나(예: `## 비즈니스 흐름 시각화 (Mermaid Diagram)`), 순번을 매기지 마십시오. (이를 준수하지 않을 시 기계적 린팅 오류가 발생합니다.)");
            rules.Add($"{ruleIndex++}. 문서 작성이 완료되면 추가 지원 제안, 인사말, 또는 향후 추가 분석 가능성에 대한 설명 등 본문 요건과 관련 없는 사족이나 안내 문구를 문서 끝에 절대 작성하지 마십시오. 문서의 정해진 필수 섹션 작성이 끝나는 즉시 깔끔하게 출력을 마쳐야 합니다.");
            rules.Add($"{ruleIndex++}. 테이블 컬럼의 상태값(예: OutState 등)이나 비즈니스 코드의 구체적인 의미가 메타데이터나 주석에 명시적으로 주어지지 않았다면, 임의로 업무 명칭(예: '지급완료' 등)을 단정하여 해석하지 말고 코드에 작성된 값 조건(예: 'OutState가 1, 5인 경우') 그대로 사실 기반으로 서술하십시오.");
            rules.Add($"{ruleIndex++}. 저장 프로시저의 최종 반환값이나 출력 파라미터가 소스코드 내에서 명시적으로 제어되지 않거나 초기값에 의존하는 경우, 호출부의 초기화 책임이나 전제 조건을 설계 주석으로 정확하게 명세화하십시오.");

            if (hasMissingDescription)
            {
                rules.Add($"{ruleIndex++}. 제공된 스키마 정보에서 `[설명 누락]`으로 표시된 컬럼이 있는 경우, SP 소스코드 내에서 사용되는 연산식 및 대입 방식을 분석하여 의미를 유추하십시오. 그리고 작성할 기능 명세서 본문에 해당 컬럼이 언급될 때 반드시 `[AI 추론 보완: {{Schema}}.{{Table}}.{{Column}} - {{유추된설명}}]` 형태로 그 결과를 누락 없이 함께 표기하십시오.");
                rules.Add("   * 올바른 기재 예시: `[AI 추론 보완: dbo.Orders.TotAmt - 주문 건의 할인 적용 후 최종 결제 금액]`");
            }
            if (hasComments)
            {
                rules.Add($"{ruleIndex++}. SP 소스코드 내부의 자연어 개발 주석과 실제 쿼리 실행 연산식 사이에 모순(불일치)이 감지되면, 실제 쿼리 코드를 최우선 기준으로 판정해 명세서를 작성하고, `## 개요` 섹션 하단에 `[🚨 주석 불일치 경고] {{모순내용}}` 형식으로 구체적인 경고 문구를 포함시키십시오.");
                rules.Add("   * 올바른 기재 예시: `[🚨 주석 불일치 경고] 수정이력 주석에는 '정상정산예정일 사용'으로 적혀있으나, 실제 WHERE 조건절에서는 취소거래예정일(CancelDate)을 기준으로 삼아 모순됨.` (특히 DDL의 메인 INSERT/UPDATE/SELECT 바로 직전에 쓰여 있는 설명 주석의 기준일자나 필터 범위 조건이 실제 WHERE 절과 어떻게 다른지 엄밀히 대조하십시오.)");
            }

            rules.Add($"{ruleIndex++}. 명세서에 CRUD 분석 및 데이터 컬럼 매핑 표를 작성할 때, '외 다수' 또는 '등'과 같이 컬럼 목록이나 매핑 관계를 임의로 축약하거나 생략하지 마십시오. 실제 쿼리에서 INSERT/UPDATE/SELECT의 대상이 되는 모든 물리 컬럼과 이에 매핑되는 원천값(SELECT 소스 컬럼, 하드코딩 상수, 함수 연산식 등)을 누락 없이 정확한 개수로 1:1 대조 표에 완전하게 기술하십시오.");
            rules.Add($"{ruleIndex++}. 프로시저 파라미터나 컬럼 제약 조건에 대해 임의로 'NOT NULL' 또는 'NULL 미허용'과 같은 주관적 단정을 짓지 마십시오. 오직 DDL 소스코드에 명시되어 있는 타입 제약 및 기본값 정의를 기반으로만 사실적으로 기술하십시오.");
            rules.Add($"{ruleIndex++}. 레거시 쿼리 내에서 `WITH(NOLOCK)` 또는 `NOLOCK` 등의 테이블 읽기 힌트가 사용된 경우, 그에 따른 더티 리드(Dirty Read) 가능성과 같은 데이터 격리 및 정합성 특성을 명세서 내 예외 처리/제약 사항 또는 트랜잭션 설명부에 반드시 반영하십시오.");
            rules.Add($"{ruleIndex++}. `NOT IN`, `ISNULL` 등이 결합된 복합 필터/분기 조건(예: `ISNULL(Col, 4) NOT IN (0,1,2,3)`)을 해석할 때 논리적 환각을 철저히 배제하고 정확하게 기술하십시오. (예: '특정 값만 포함'이 아니라 '제외된 값 외의 모든 값 및 NULL 치환값 포함'으로 정확히 서술)");

            // [로컬 LLM 환각 방지 엄격 네거티브 규칙 추가]
            rules.Add($"{ruleIndex++}. 제시된 정적 분석 정보(AST 분석 메타데이터) 및 테이블 스키마에 실제로 존재하지 않는 컬럼은 CRUD 분석 표에 절대 임의로 상상하여 기재하지 마십시오. 스키마에는 없으나 DDL 쿼리에 등장하는 불일치 현상이 발견되면, 임의 테이블 컬럼으로 단정 짓지 말고 스키마 불일치 사실을 기록하거나 규격 포맷인 `[AI 추론 보완: Schema.Table.Column - 설명]`으로 사실에 근거해 서술하십시오.");
            rules.Add($"{ruleIndex++}. 소스코드 DDL 내에 명시적으로 상숫값(예: RETURN -5)이 지정되어 있지 않은 에러 반환 단계(예: IF @@ERROR <> 0 분기)에 대해 임의로 -1, -2 등 순차적인 숫자를 창작하여 단정적으로 기술하지 마십시오. 근거가 없는 값은 반드시 '실패 시 에러 코드 반환(값 정의 미비로 추정)' 등으로 서술하여 환각을 원천 배제하십시오.");

            var systemPrompt = string.Join("\n", rules);
            systemPrompt += $"\n\n[사용자 지침]\n{userInstructions}";
            
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

            checklistSb.AppendLine("- [ ] Mermaid 흐름도 내부 노드의 한글 텍스트에 큰따옴표(\"\")를 사용하고 문법적 예약어 충돌이 없도록 작성하셨습니까?");
            checklistSb.AppendLine("- [ ] SP 내부의 에러 처리 분기(예: DELETE/INSERT 실패 시 각각 @@ERROR 조건 분기 및 음수 반환 코드)와 트랜잭션 롤백 동작이 Mermaid 다이어그램 및 본문 설명에 충실히 반영되었습니까?");

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

위 구조화된 참조 정보를 바탕으로 지침에 맞게 리버스 엔지니어링하여 마크다운 형식의 기능 명세서를 완성하십시오.
{checklistSb.ToString()}
";

            if (!string.IsNullOrEmpty(feedbackLog))
            {
                userPrompt += $"\n\n[이전 시도에 대한 검증 오류/수정 피드백 로그]:\n{feedbackLog}\n\n위 검토 및 수정 체크리스트의 모든 요건들을 전적으로 수용하여 명세서 내용을 정교하게 수정하고 오류를 바로잡아 다시 작성해 주십시오. 특히 이전 턴에서 정상적으로 분석되었던 다른 섹션이나 테이블 컬럼 목록이 이번 수정 과정에서 실수로 유실되거나 훼손되는 회귀 결함(Regression)이 절대 발생하지 않도록, 제공된 '진실의 원천' 메타데이터(참조 컬럼 목록 등)와 철저히 대조해 주십시오.";
            }

            return (systemPrompt, userPrompt);
        }

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

                    return new ReviewResult
                    {
                        HasDefects = hasDefects,
                        FeedbackComment = feedbackComment,
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

            if (string.Equals(ProviderName, "Ollama", StringComparison.OrdinalIgnoreCase) && _enableOllamaThinking)
            {
                // Gemma 4 계열 모델의 추론(Thinking)을 강제 활성화하기 위해 시스템 프롬프트 첫 부분에 제어 토큰 삽입
                systemPrompt = "<|think|>" + systemPrompt;
                systemPrompt += "\n\n[Ollama 추론 유도 규칙]\n- 최종 답변을 작성하기 전에, 반드시 분석 단계와 생각 흐름을 <think>와 </think> 태그 또는 Gemma 4 표준 출력 포맷으로 상세히 기술하십시오. 최종 분석 명세서는 반드시 해당 태그 바깥에 작성해야 합니다.";
            }

            Log.Information("AI 명세서 생성 요청 전송 - SP: {Schema}.{Name}, Effort: {Effort}", spDef.Schema, spDef.Name, effort ?? "Default");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, _temperature, effort, cancellationToken);
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

        public async Task<AiResult> GenerateSpecSectionAsync(SpDefinition spDef, string sectionType, string userInstructions, string? feedbackLog = null, string? effort = null, CancellationToken cancellationToken = default)
        {
            var (systemPrompt, userPrompt) = BuildSpecSectionPrompts(spDef, sectionType, userInstructions, feedbackLog);

            if (string.Equals(ProviderName, "Ollama", StringComparison.OrdinalIgnoreCase) && _enableOllamaThinking)
            {
                systemPrompt = "<|think|>" + systemPrompt;
                systemPrompt += "\n\n[Ollama 추론 유도 규칙]\n- 최종 답변을 작성하기 전에, 반드시 분석 단계와 생각 흐름을 <think>와 </think> 태그 또는 Gemma 4 표준 출력 포맷으로 상세히 기술하십시오. 최종 분석 명세서는 반드시 해당 태그 바깥에 작성해야 합니다.";
            }

            Log.Information("AI 명세서 구역 분할 생성 요청 전송 - SP: {Schema}.{Name}, Section: {Section}, Effort: {Effort}", spDef.Schema, spDef.Name, sectionType, effort ?? "Default");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, _temperature, effort, cancellationToken);
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
                    "당신은 SQL Server Stored Procedure 분석 전문가입니다. 다음 규칙을 준수하여 마크다운 기능 명세서의 [## 개요] 및 [## 파라미터 목록] 섹션만을 작성하십시오.",
                    "",
                    "[작성 규칙]",
                    "1. 기능 명세서의 대헤더는 오직 `## 개요`와 `## 파라미터 목록` 두 가지만 사용해야 하며, 이 순서대로 작성한 뒤 즉시 출력을 종료하십시오. 다른 H2 대헤더는 절대 포함하지 마십시오."
                };

                int rIdx = 2;
                if (hasComments)
                {
                    sbRules.Add($"{rIdx++}. SP 헤더 주석과 실제 구현 쿼리 사이에 모순이 감지되는 경우, 실제 코드를 최우선 기준으로 작성하고 `## 개요` 하단에 반드시 `[🚨 주석 불일치 경고] {{모순내용}}` 형식의 구체적 경고 문구를 포함하십시오.");
                    sbRules.Add("   * 올바른 기재 예시: `[🚨 주석 불일치 경고] 수정이력 주석에는 '정상정산예정일 사용'으로 적혀있으나, 실제 WHERE 조건절에서는 취소거래예정일(CancelDate)을 기준으로 삼아 모순됨.`");
                }

                sbRules.Add($"{rIdx++}. 파라미터 목록에는 DDL에 정의된 모든 매개변수의 데이터 타입, Null 허용 여부(DDL에 없으면 '명시 없음'), 용도 및 OUTPUT 파라미터 여부를 표(Table)로 기술하십시오. 임의로 'NOT NULL'을 단정해선 안 됩니다.");
                sbRules.Add($"{rIdx++}. 본 프로시저가 결과 셋(Rowset)을 반환하는지 여부를 명시하십시오. 만약 반환값이 명시적으로 제어되지 않거나 초기값에 의존하는 경우, 호출부의 초기화 책임이나 전제 조건을 정확히 서술하십시오.");
                sbRules.Add($"{rIdx++}. 소스코드 DDL 내에 명시적으로 상숫값(예: RETURN -5)이 지정되어 있지 않은 에러 반환 단계(예: IF @@ERROR <> 0 분기)에 대해 임의로 -1, -2 등 순차적인 숫자를 창작하여 단정적으로 기술하지 마십시오. 근거가 없는 값은 반드시 '실패 시 에러 코드 반환(값 정의 미비로 추정)' 등으로 서술하여 환각을 원천 배제하십시오.");
                sbRules.Add($"{rIdx++}. 최종 작성 완료 후 사족이나 인사말은 절대 작성하지 마십시오.");
                sbRules.Add($"{rIdx++}. 응답 전체를 백틱(```markdown ... ```)으로 감싸지 마십시오.");

                systemPrompt = string.Join("\n", sbRules);
                systemPrompt += $"\n\n[사용자 지침]\n{userInstructions}";

                checklistText = @"🎯 [필수 검증 체크리스트]
- [ ] '## 개요' 및 '## 파라미터 목록' 헤더가 명확하게 작성되었습니까?
- [ ] SP 헤더 주석과 실제 로직의 모순이 있을 시 `[🚨 주석 불일치 경고] ...`가 본문에 포함되었습니까?
- [ ] 출력 파라미터의 역할 및 결과셋 반환 여부를 명확히 명시하셨습니까?";
            }
            else if (sectionType == "CrudAnalysis")
            {
                var sbRules = new List<string>
                {
                    "당신은 SQL Server Stored Procedure 분석 전문가입니다. 다음 규칙을 준수하여 마크다운 기능 명세서의 [## CRUD 분석] 섹션만을 작성하십시오.",
                    "",
                    "[작성 규칙]",
                    "1. 기능 명세서의 대헤더는 오직 `## CRUD 분석` 하나만 사용해야 하며, CRUD 분석이 끝나면 즉시 출력을 종료하십시오. 다른 H2 대헤더는 절대 포함하지 마십시오.",
                    "2. SELECT, INSERT, UPDATE, DELETE 대상 물리 테이블들을 CRUD별로 명시하십시오. 각 테이블에 대해 실제 쿼리에서 조회/수정하는 컬럼명과 조건/조인 키를 누락 없이 완전하게 기재하십시오.",
                    "   - 제공된 [Stored Procedure AST 정적 분석 정보]의 '식별된 테이블별 실제 쿼리 참조 컬럼 목록'은 이 명세서 CRUD 분석 표의 진실의 원천(Source of Truth)입니다. 이 컬럼 목록을 누락 없이 그대로 매핑하여 작성하십시오.",
                    "3. INSERT/UPDATE 대상 테이블의 모든 컬럼에 대해 매핑되는 원천 데이터(변수, 상수, 함수 등)를 축약 ('외 다수' 또는 '...') 없이 1:1 대조 표로 완전하게 기술하십시오.",
                    "4. 임시 테이블(#TempTable) 생성/사용 여부, UDF 사용자 정의 함수 호출 여부, Linked Server 원격 참조 여부에 대해 각각 사용 목적을 기술하거나, 미사용 시 미사용 사실을 명시적으로 기재하십시오."
                };

                int rIdx = 5;
                if (hasUdf)
                {
                    sbRules.Add($"{rIdx++}. SP에서 호출하는 UDF의 DDL이 제공된 경우에 한해 연산 알고리즘을 분석하여 포함시키고, 제공되지 않은 경우 'UDF 정의 미제공으로 상세 로직 분석 제외' 및 '호출 위치 및 사용 목적'만을 사실 기반으로 기록하십시오.");
                }
                if (hasDynamicSql)
                {
                    sbRules.Add($"{rIdx++}. SP 내에 동적 SQL이 존재하면, 동적으로 구성되어 실행되는 SQL의 목적과 대상 테이블을 식별하여 CRUD 분석에 누락 없이 반영하십시오.");
                }
                if (hasLinkedServers)
                {
                    sbRules.Add($"{rIdx++}. Linked Server를 통한 원격 참조(4파트 식별자 사용)가 발견되면 외부 DB/테이블 의존성과 연동 목적을 명확히 분석하여 포함하십시오. 동일 서버 내 크로스 데이터베이스 참조인 경우는 Linked Server가 아님을 사실적으로 구분해 표기하십시오.");
                }
                if (hasMissingDescription)
                {
                    sbRules.Add($"{rIdx++}. 제공된 스키마 정보에서 `[설명 누락]`인 컬럼이 SP 소스코드 내에서 사용된다면 연산식 및 대입 방식을 분석하여 의미를 유추하십시오. 그리고 기능 명세서 본문에 언급될 때 반드시 `[AI 추론 보완: {{Schema}}.{{Table}}.{{Column}} - {{유추된설명}}]` 형태로 기재하십시오.");
                    sbRules.Add("   * 올바른 기재 예시: `[AI 추론 보완: dbo.Orders.TotAmt - 주문 건의 할인 적용 후 최종 결제 금액]`");
                }

                sbRules.Add($"{rIdx++}. 제시된 정적 분석 정보(AST 분석 메타데이터) 및 테이블 스키마에 실제로 존재하지 않는 컬럼은 CRUD 분석 표에 절대 임의로 상상하여 기재하지 마십시오. 스키마에는 없으나 DDL 쿼리에 등장하는 불일치 현상이 발견되면, 임의 테이블 컬럼으로 단정 짓지 말고 스키마 불일치 사실을 기록하거나 규격 포맷인 `[AI 추론 보완: Schema.Table.Column - 설명]`으로 사실에 근거해 서술하십시오.");
                sbRules.Add($"{rIdx++}. 최종 작성 완료 후 사족이나 인사말은 절대 작성하지 마십시오.");
                sbRules.Add($"{rIdx++}. 응답 전체를 백틱(```markdown ... ```)으로 감싸지 마십시오.");

                systemPrompt = string.Join("\n", sbRules);
                systemPrompt += $"\n\n[사용자 지침]\n{userInstructions}";

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
                    checklistSb.AppendLine($"- [ ] INSERT 대상 테이블({string.Join(", ", spDef.StaticAnalysis.InsertTables)})의 각 컬럼별 원천 데이터 매핑 정보가 1:1 대조 표로 완전하게 기술되었습니까?");
                }
                checklistText = checklistSb.ToString();
            }
            else // LogicAndVisualization
            {
                var sbRules = new List<string>
                {
                    "당신은 SQL Server Stored Procedure 분석 전문가입니다. 다음 규칙을 준수하여 마크다운 기능 명세서의 [## 로직 흐름 요약] 및 [## 비즈니스 흐름 시각화] 섹션만을 작성하십시오.",
                    "",
                    "[작성 규칙]",
                    "1. 기능 명세서의 대헤더는 오직 `## 로직 흐름 요약`과 `## 비즈니스 흐름 시각화` 두 가지만 사용해야 하며, 이 순서대로 작성한 뒤 즉시 출력을 종료하십시오. 다른 H2 대헤더는 절대 포함하지 마십시오.",
                    "2. 로직 흐름 요약에서는 트랜잭션 범위, 비즈니스 연산 단계, @@ERROR 등을 활용한 단계별 롤백 구조 및 실패 시 반환 코드를 상세히 서술하십시오."
                };

                int rIdx = 3;
                if (hasDynamicSql)
                {
                    sbRules.Add($"{rIdx++}. SP 내에 동적 SQL이 존재하면 문자열로 빌드되는 쿼리의 실행 목적과 흐름상의 비즈니스 목적을 로직 요약에 누락 없이 반영하십시오.");
                }
                if (hasLinkedServers)
                {
                    sbRules.Add($"{rIdx++}. Linked Server 원격 참조가 사용된 경우 해당 외부 DB와의 연동 흐름 및 분산 트랜잭션의 특성(있는 경우)을 로직 요약에 포함시키십시오.");
                }

                sbRules.Add($"{rIdx++}. 소스 SELECT 등에 `WITH(NOLOCK)` 또는 `NOLOCK` 등의 테이블 읽기 힌트가 사용된 경우, 그에 따른 더티 리드(Dirty Read) 가능성과 같은 데이터 격리 및 정합성 특성을 명세서 내 예외 처리/제약 사항 또는 트랜잭션 설명부에 반드시 반영하십시오.");
                sbRules.Add($"{rIdx++}. 비즈니스 흐름 시각화에는 비즈니스 흐름을 묘사하는 Mermaid flowchart 다이어그램을 필수로 포함해 주십시오. 노드 텍스트 전체는 이중 큰따옴표로 감싸 구문 에러를 방지하고, 화살표 위에 조건 텍스트를 적을 때 기호/따옴표/괄호를 배제하십시오. 노드 ID는 영문/숫자 고유 ID를 사용하십시오.");
                sbRules.Add($"{rIdx++}. 소스코드 DDL 내에 명시적으로 상숫값(예: RETURN -5)이 지정되어 있지 않은 에러 반환 단계(예: IF @@ERROR <> 0 분기)에 대해 임의로 -1, -2 등 순차적인 숫자를 창작하여 단정적으로 기술하지 마십시오. 근거가 없는 값은 반드시 '실패 시 에러 코드 반환(값 정의 미비로 추정)' 등으로 서술하여 환각을 원천 배제하십시오.");
                sbRules.Add($"{rIdx++}. 최종 작성 완료 후 사족이나 인사말은 절대 작성하지 마십시오.");
                sbRules.Add($"{rIdx++}. 응답 전체를 백틱(```markdown ... ```)으로 감싸지 마십시오.");

                systemPrompt = string.Join("\n", sbRules);
                systemPrompt += $"\n\n[사용자 지침]\n{userInstructions}";

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
            promptSb.AppendLine("</stored-procedure-context>");
            promptSb.AppendLine();
            promptSb.AppendLine("위 구조화된 참조 정보를 바탕으로 지침에 맞게 해당 섹션을 리버스 엔정니어링하여 작성하십시오.");
            promptSb.AppendLine(checklistText);

            if (!string.IsNullOrEmpty(feedbackLog))
            {
                promptSb.AppendLine();
                promptSb.AppendLine($"[이전 시도에 대한 검증 오류/수정 피드백 로그]:\n{feedbackLog}\n\n위 검토 및 수정 체크리스트의 모든 요건들을 전적으로 수용하여 명세서 내용을 정교하게 수정하고 오류를 바로잡아 다시 작성해 주십시오.");
            }

            return (systemPrompt, promptSb.ToString());
        }

        public async Task<ReviewResult> ReviewSpecificationAsync(SpDefinition spDef, string specMarkdown, string? effort = null, CancellationToken cancellationToken = default)
        {
            var systemPrompt = @"당신은 SQL Server Stored Procedure 기능 명세서의 완성도를 검증하는 수석 아키텍트이자 리뷰어 에이전트입니다.
제시된 마크다운 형식의 기능 명세서가 제공된 레거시 원본 메타데이터(참조 테이블 스키마, DDL, 의존성 관계 등)를 정확히 반영하여 왜곡 없이 잘 해석되었는지 검증하고 채점하십시오.

[검토 및 채점 기준 (각 항목 0~10점 정수 채점)]
1. 비즈니스 로직 및 제어 흐름 정합성 (ScoreAccuracy):
   - 실제 소스코드의 연산 및 분기 조건이 기능 명세서에 왜곡이나 임의 단정, 환각(Hallucination) 없이 사실 그대로 정확히 요약되었는가?
   - 수정이력 주석과의 모순이 있을 시 `[🚨 주석 불일치 경고]`가 누락 없이 올바르게 기술되었는가?
2. 데이터 모델 및 CRUD 완전성 (ScoreCrud):
   - SP 내부에서 접근하는 SELECT/INSERT/UPDATE/DELETE 테이블과 컬럼 관계가 1:1 대조 표에 누락(외 다수 축약 등) 없이 온전히 기술되었는가?
   - 스키마 설명 누락 컬럼에 대한 `[AI 추론 보완: Schema.Table.Column - 설명]`이 규칙 포맷대로 적확하게 기재되었는가?
   - 임시 테이블 생성/사용, UDF 호출, Linked Server 원격 참조가 사용되었을 경우 각각 목적이 기술되었거나, 미사용 시 미사용 사실이 명시되었는가?
3. 연동 인터페이스 구체성 (ScoreInterface):
   - 입력/출력 매개변수의 명칭, 타입, Null 허용 여부(명시 없을 시 '명시 없음'), 용도 등이 표 형태로 축약 없이 완전하게 기재되었는가?
   - 프로시저가 결과 셋(Rowset)을 반환하는지 여부가 구체적으로 명세화되었는가?
4. 예외 및 트랜잭션/격리성 정책 (ScoreException):
   - SP 내부 에러 처리 방식(TRY...CATCH 유무, @@ERROR 분기 흐름), 트랜잭션 제어(BEGIN/COMMIT/ROLLBACK), NOLOCK 사용에 따른 정합성 리스크 및 비즈니스 영향도가 심층 분석되어 기술되었는가?
   - RETURN 문에 구체적인 값(성공 0, 실패 음수 등)이 지정되어 있는지, 호출부의 초기화 책임 등이 분석되어 있는가?
5. 다이어그램 및 시각화 가독성 (ScoreReadability):
   - 비즈니스 흐름을 묘사하는 Mermaid flowchart 다이어그램이 문법 오류 없이 flowchart TD 형태로 작성되었는가?
   - 노드 한글 라벨은 이중 큰따옴표로 감쌌으며, 화살표 위의 텍스트 라벨에 큰따옴표나 괄호 등의 문법 오류 유발 요소가 배제되었는가?
   - 노드 텍스트에 골뱅이(@) 변수명 기호가 포함되지 않고 자연어 또는 순화된 명칭(@@ERROR 제외)으로 구성되었는가?

[결함(Defect) 판단 조건]
- 5대 평가 기준 중 단 하나라도 8점 미만인 항목이 존재하거나, 명세서 필수 5대 헤더(## 개요, ## 파라미터 목록, ## CRUD 분석, ## 로직 흐름 요약, ## 비즈니스 흐름 시각화) 중 누락된 섹션이 있는 경우 HasDefects를 true로 판단하십시오.

[답변 작성 형식]
반드시 아래 JSON 형식으로만 최종 답변을 출력해야 합니다. 다른 텍스트나 설명, 마크다운 백틱 코드 블록(```json ... ```)을 절대 포함하지 마십시오. 오직 순수 JSON만 반환해야 합니다:
{
  ""HasDefects"": true 또는 false (불리언 타입),
  ""FeedbackComment"": ""결함이 있는 경우 무엇이 누락되었거나 어떻게 수정해야 하는지 구체적인 피드백 내용 기술 (HasDefects가 false인 경우 반드시 빈 문자열 반환)"",
  ""ScoreAccuracy"": 10,
  ""ScoreCrud"": 10,
  ""ScoreInterface"": 10,
  ""ScoreException"": 10,
  ""ScoreReadability"": 10
}";

            var (dependenciesText, tableSchemasText, referenceDdlsText, staticAnalysisText) = BuildSpMetadataTexts(spDef);

            var userPrompt = $@"
분석 대상 Stored Procedure 정보:
- Schema: {spDef.Schema}
- Name: {spDef.Name}

[DB에서 추출된 기계적 의존 관계 목록]
{dependenciesText}

[의존하는 참조 테이블 상세 스키마 정보 (Markdown Tables)]
{tableSchemasText}

[의존하는 참조 함수 및 Stored Procedure DDL 코드 목록]
{referenceDdlsText}

{staticAnalysisText}

[작성된 기능 명세서 마크다운]
{specMarkdown}

위 정보들을 바탕으로 기능 명세서의 완성도 및 정확성을 엄격히 대조 검토하여 JSON 포맷으로 답해주십시오.
";

            if (string.Equals(ProviderName, "Ollama", StringComparison.OrdinalIgnoreCase) && _enableOllamaThinking)
            {
                systemPrompt = "<|think|>" + systemPrompt;
                systemPrompt += "\n\n[Ollama 추론 유도 규칙]\n- 최종 답변을 작성하기 전에, 반드시 분석 단계와 생각 흐름을 <think>와 </think> 태그 또는 Gemma 4 표준 출력 포맷으로 상세히 기술하십시오. 최종 분석 명세서는 반드시 해당 태그 바깥에 작성해야 합니다.";
            }

            Log.Information("AI 명세서 리뷰 요청 전송 - SP: {Schema}.{Name}, Effort: {Effort}", spDef.Schema, spDef.Name, effort ?? "Default");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt);

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.1f, effort, cancellationToken);

            Log.Information("AI 명세서 리뷰 응답 수신 완료 - SP: {Schema}.{Name}, 응답 길이: {Length}", spDef.Schema, spDef.Name, aiResult?.Content?.Length ?? 0);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult?.Content);
            var reviewResult = ParseReviewResult(aiResult?.Content, $"{spDef.Schema}.{spDef.Name}");
            reviewResult.ThinkingText = aiResult?.ThinkingText;
            return reviewResult;
        }

        public async Task<AiResult> GenerateBatchMigrationPlanAsync(SpDefinition spDef, string targetLanguage, CancellationToken cancellationToken = default)
        {
            var systemPrompt = $@"당신은 SQL Server Agent의 스케줄러 배치 작업을 현대적인 애플리케이션 기반 배치 프레임워크로 전환하는 최적화 설계 전문가입니다.
대상 Stored Procedure 소스 코드와 의존 테이블/UDF 구조를 분석하여, {targetLanguage} 기반의 현대적인 백그라운드 배치 컴포넌트로 포팅하기 위한 '배치 전환 계획 설계서'를 작성해 주십시오.

[설계서 작성 규칙 및 내용 필수 조건]
1. 문서는 한글 마크다운 양식으로 작성하십시오.
2. **배치 전환 아키텍처 개요**: SQL Server Agent Job 역할을 대체할 신규 스케줄러 프레임워크 제안 (예: C#인 경우 Quartz.NET / Hangfire 기반 Worker Service, Java인 경우 Spring Batch + Quartz).
3. **대량 데이터 청크(Chunk) 처리 전략**: OOM 방지를 위한 Paging Reader 패턴 및 벌크 연산(Bulk Write) 가이드라인 제안.
4. **비즈니스 전환 설계 및 의사코드(Pseudocode)**: SP 내부의 주요 비즈니스 로직(분기, 루프, 데이터 처리 등)을 {targetLanguage}의 OOP 문법 및 ORM(EF Core / JPA)으로 전환하는 구체적 의사코드(코드 구조 예시) 제공.
   - 특히 SP 내에 동적 SQL(EXEC/sp_executesql 등)이 사용된 경우, 이를 컴파일 타임에 검증 가능하며 SQL 인젝션 위험이 없는 {targetLanguage}의 안전한 쿼리 빌더나 파라미터화된 ORM 쿼리 또는 조건부 분기 로직으로 안전하게 포팅하기 위한 가이드를 제시하십시오.
   - Linked Server 참조(4파트 식별자)가 사용된 경우, 멀티 데이터소스 구성, 분산 트랜잭션 처리(필요 시), 또는 API/DB Link 대체 인터페이스 설계 등 {targetLanguage} 환경에 맞춘 구체적인 연동 설계 방향을 제시하십시오.
   - 쿼리 내에 WITH(NOLOCK) 등 테이블 읽기 힌트가 사용된 경우, 타겟 {targetLanguage} 프레임워크(ORM) 상에서 이에 대칭되는 조회 트랜잭션 격리 수준(예: Read Uncommitted 격리 제어 또는 전용 조회 힌트 설정 등)을 적용해 성능 및 데이터 정합성 특성을 안전하게 포팅하기 위한 가이드라인을 제시하십시오.
5. **로깅 및 실패 조치 계획**: 기존 TRY...CATCH 에러 로깅을 구조화된 로그(Serilog 등)로 전환하고 알림(Slack 등) 발송 방안 매핑.
6. **데이터 정합성 검증 SQL 세트**: 신규 배치 코드가 레거시 SP와 동일한 데이터를 생성/수정했는지 검증하기 위한 실행 전후 카운트, 해시 검증용 SQL 쿼리 템플릿 포함.
7. **응답 전체를 백틱(```markdown ... ```) 코드 블록으로 감싸지 마십시오. 반드시 마크다운 헤더로 시작하는 텍스트 형태로 직접 출력을 수행해야 합니다.**
8. 문서 작성이 완료되면 추가 지원 제안, 인사말, 또는 향후 추가 분석 가능성에 대한 설명 등 본문 요건과 관련 없는 사족이나 안내 문구를 문서 끝에 절대 작성하지 마십시오. 문서의 정해진 필수 섹션 작성이 끝나는 즉시 깔끔하게 출력을 마쳐야 합니다.";

            var dependenciesText = new StringBuilder();
            var tableSchemasText = new StringBuilder();
            var referenceDdlsText = new StringBuilder();

            foreach (var dep in spDef.Dependencies)
            {
                dependenciesText.AppendLine($"- Schema: {dep.Schema}, Name: {dep.Name}, Type: {dep.Type} (발견 깊이: {dep.DiscoveryDepth}단계)");
                
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
분석 대상 Stored Procedure 정보:
- Schema: {spDef.Schema}
- Name: {spDef.Name}

[DB에서 추출된 기계적 의존 관계 목록]
{dependenciesText}

[의존하는 참조 테이블 상세 스키마 정보 (테이블 및 컬럼 주석 설명 포함)]
{tableSchemasText}

[의존하는 참조 함수 및 Stored Procedure DDL 코드 목록]
{referenceDdlsText}

[Stored Procedure DDL SQL 원본]
```sql
{spDef.DdlText}
```

위 레거시 배치 SP 정보를 바탕으로 {targetLanguage} 기준의 '배치 전환 계획 설계서'를 작성해 주십시오.
";

            if (string.Equals(ProviderName, "Ollama", StringComparison.OrdinalIgnoreCase) && _enableOllamaThinking)
            {
                systemPrompt = "<|think|>" + systemPrompt;
                systemPrompt += "\n\n[Ollama 추론 유도 규칙]\n- 최종 답변을 작성하기 전에, 반드시 분석 단계와 생각 흐름을 <think>와 </think> 태그 또는 Gemma 4 표준 출력 포맷으로 상세히 기술하십시오. 최종 분석 명세서는 반드시 해당 태그 바깥에 작성해야 합니다.";
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

        public async Task<AiResult> GenerateConsolidatedBatchPlanAsync(System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, CancellationToken cancellationToken = default)
        {
            var systemPrompt = $@"당신은 여러 개의 레거시 Stored Procedure 분석 명세서(마크다운)를 바탕으로, 이를 최신 {targetLanguage} 기반의 단일 배치 애플리케이션 및 스케줄러 전환 설계도(Consolidated Batch Modernization Plan)로 작성하는 전문 수석 배치 아키텍트입니다.
제공된 개별 SP 분석서들의 비즈니스 요약과 테이블 CRUD 맵을 종합적으로 설계하여, '{jobName}'이라는 단일 통합 배치 Job으로 전환하는 계획서를 기안해 주십시오.

[설계서 작성 규칙 및 내용 필수 조건]
1. 문서는 한글 마크다운 양식으로 작성하십시오.
2. 아래 4가지 필수 대헤더(##) 구조를 반드시 준수하여 문서를 구성해야 하며, 그 외의 다른 대헤더는 추가하지 마십시오.
   - ## 통합 배치 아키텍처 개요: 제공된 여러 분석서 파일들이 어떤 순서(순차 체인, 조건 분기, 병렬 처리 등)로 구성되어 하나의 배치 Job 내의 Step들로 설계되는지 기술하십시오.
   - ## Mermaid 기반 통합 흐름도: 전체 배치 Job의 데이터 파이프라인 및 수행 단계를 묘사하는 Mermaid flowchart 다이어그램을 작성하십시오.
     * 노드 정의 시 특수문자나 괄호가 들어가 린팅 에러가 발생하지 않도록 텍스트 전체를 반드시 이중 큰따옴표로 감싸십시오. (예: id1[""Step 1: 데이터 정제""] --> id2[""Step 2: 적재 수행""])
     * 괄호만으로 노드를 구성하거나 Mermaid 예약어(graph, flowchart, subgraph 등)를 노드 ID로 사용해서는 안 됩니다.
   - ## 단계별 이행 상세 및 의사코드: 각 단계를 처리하는 {targetLanguage} 클래스/컴포넌트 설계, 대용량 청크(Chunk) 페이징 의사코드, 그리고 공통 의존성에 대한 락/트랜잭션 설계 및 실패 시 재시작(Restartability)/복구 계획을 이 섹션 하위에 포함하여 제시하십시오.
   - ## 통합 데이터 정합성 검증 SQL 세트: 배치 실행 전후 데이터 무결성을 검증할 수 있는 통합 SQL 쿼리 세트를 포함하십시오.
3. 응답 전체를 백틱(```markdown ... ```) 코드 블록으로 감싸지 마십시오. 반드시 마크다운 헤더로 시작하는 텍스트 형태로 직접 출력을 수행해야 합니다.
4. 문서 작성이 완료되면 추가 지원 제안, 인사말, 또는 향후 추가 분석 가능성에 대한 설명 등 본문 요건과 관련 없는 사족이나 안내 문구를 문서 끝에 절대 작성하지 마십시오. 문서의 정해진 필수 섹션 작성이 끝나는 즉시 깔끔하게 출력을 마쳐야 합니다.";

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"통합 배치 Job 명칭: {jobName}");
            userPrompt.AppendLine($"대상 기술 스택: {targetLanguage}");
            userPrompt.AppendLine();
            userPrompt.AppendLine("[제공된 개별 Stored Procedure 분석 명세서 목록]");

            foreach (var spec in specs)
            {
                userPrompt.AppendLine($"---");
                userPrompt.AppendLine($"파일명: {spec.FileName}");
                userPrompt.AppendLine($"[본문 시작]");
                userPrompt.AppendLine(spec.Content);
                userPrompt.AppendLine($"[본문 끝]");
                userPrompt.AppendLine();
            }

            userPrompt.AppendLine("위 개별 명세서들의 정보를 완벽히 분석하여, 지침에 맞추어 단일 통합 배치 전환 계획서를 구성해 주십시오.");

            if (string.Equals(ProviderName, "Ollama", StringComparison.OrdinalIgnoreCase) && _enableOllamaThinking)
            {
                systemPrompt = "<|think|>" + systemPrompt;
                systemPrompt += "\n\n[Ollama 추론 유도 규칙]\n- 최종 답변을 작성하기 전에, 반드시 분석 단계와 생각 흐름을 <think>와 </think> 태그 또는 Gemma 4 표준 출력 포맷으로 상세히 기술하십시오. 최종 분석 명세서는 반드시 해당 태그 바깥에 작성해야 합니다.";
            }

            Log.Information("AI 통합 배치 계획서 생성 요청 전송 - JobName: {JobName}, TargetLanguage: {TargetLanguage}, Effort: {Effort}", jobName, targetLanguage, effort ?? "Default");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt.ToString());

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), _temperature, effort, cancellationToken);
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

        public async Task<ReviewResult> ReviewConsolidatedPlanAsync(System.Collections.Generic.List<(string FileName, string Content)> specs, string planMarkdown, string jobName, string? effort = null, CancellationToken cancellationToken = default)
        {
            var systemPrompt = @"당신은 여러 레거시 SP 분석 명세서들을 종합하여 설계된 통합 배치 전환 계획서(Markdown)의 완성도를 검증하는 수석 배치 아키텍트이자 리뷰어 에이전트입니다.
제시된 통합 계획서가 제공된 레거시 명세서들의 기능 설명 및 요구사항을 왜곡 없이 잘 반영하였는지, 배치 아키텍처로서의 기술적 타당성을 갖추었는지 엄격하게 검증하고 채점하십시오.

[검토 및 채점 기준 (각 항목 0~10점 정수 채점)]
1. 비즈니스 로직 및 제어 흐름 정합성 (ScoreAccuracy):
   - 개별 SP 분석서의 비즈니스 로직 및 정산 규칙이 통합 배치 흐름 내에서 누락, 왜곡, 환각 없이 충실히 설계에 반영되었는가?
2. 데이터 모델 및 CRUD 완전성 (ScoreCrud):
   - 각 SP가 참조하던 테이블의 CRUD 작업이 통합 데이터 파이프라인에서 적합한 순서 및 배치 청크(Paging) 매핑으로 올바르게 설계되었는가?
3. 연동 인터페이스 구체성 (ScoreInterface):
   - 배치 컴포넌트 간의 입출력 데이터 규격, 파라미터 매핑 및 API 연동 정의가 축약 없이 상세하고 완전하게 도출되어 있는가?
4. 예외 및 트랜잭션/격리성 정책 (ScoreException):
   - 통합 배치 수준에서의 실패 지점 재시작(Restartability), 벌크 트랜잭션 격리, 복구 전략이 견고하게 정의되어 있는가?
5. 다이어그램 및 시각화 가독성 (ScoreReadability):
   - 통합 배치 흐름도를 묘사하는 Mermaid flowchart가 문법 오류 없이 완전하고, 시각적 가독성이 우수한가?

[결함(Defect) 판단 조건]
- 5대 평가 기준 중 단 하나라도 8점 미만인 항목이 존재하거나, 계획서 필수 4대 헤더(## 통합 배치 아키텍처 개요, ## Mermaid 기반 통합 흐름도, ## 단계별 이행 상세 및 의사코드, ## 통합 데이터 정합성 검증 SQL 세트) 중 누락된 섹션이 있는 경우 HasDefects를 true로 판단하십시오.

[답변 작성 형식]
반드시 아래 JSON 형식으로만 최종 답변을 출력해야 합니다. 다른 텍스트나 설명, 마크다운 백틱 코드 블록(```json ... ```)을 절대 포함하지 마십시오. 오직 순수 JSON만 반환해야 합니다:
{
  ""HasDefects"": true 또는 false (불리언 타입),
  ""FeedbackComment"": ""결함이 있는 경우 무엇이 누락되었거나 어떻게 수정해야 하는지 구체적인 피드백 내용 기술 (HasDefects가 false인 경우 반드시 빈 문자열 반환)"",
  ""ScoreAccuracy"": 10,
  ""ScoreCrud"": 10,
  ""ScoreInterface"": 10,
  ""ScoreException"": 10,
  ""ScoreReadability"": 10
}";

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine($"통합 배치 Job 명칭: {jobName}");
            userPrompt.AppendLine();
            userPrompt.AppendLine("[제공된 개별 Stored Procedure 분석 명세서 목록]");

            foreach (var spec in specs)
            {
                userPrompt.AppendLine($"---");
                userPrompt.AppendLine($"파일명: {spec.FileName}");
                userPrompt.AppendLine(spec.Content);
                userPrompt.AppendLine();
            }

            userPrompt.AppendLine("[작성된 통합 배치 전환 계획서 마크다운]");
            userPrompt.AppendLine(planMarkdown);
            userPrompt.AppendLine();
            userPrompt.AppendLine("위 계획서의 완결성 및 정확성을 검토 기준에 맞게 성실히 분석한 뒤 JSON 포맷으로 답해주십시오.");

            Log.Information("AI 통합 배치 계획서 리뷰 요청 전송 - JobName: {JobName}, Effort: {Effort}", jobName, effort ?? "Default");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt.ToString());

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), 0.1f, effort, cancellationToken);

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
            var systemPrompt = @"당신은 레거시 DB 내 Stored Procedure 코드(DDL) 및 실제 코드값/설정 데이터(Data Profiling)를 종합하여, 비즈니스 관점의 통합 '정산 정책 문서(Settlement Rulebook)'를 도출해내는 수석 정산 정책 분석가입니다.
제시된 SP들의 SQL 조건문 분기, 매핑 관계와 실제 적재된 마스터 데이터(코드값 등)를 결합하여, 실무자가 바로 읽고 이해할 수 있는 자연어 정책 정책서를 작성하십시오.

[작성 규칙]
1. 정적 코드(DDL) 상에 존재하는 하드코딩된 상수 분기 조건(예: WHERE Status = 'S02', WHERE Type = 'A10' 등)이, 함께 제공된 실제 공통 코드/마스터 데이터 상에서 어떤 의미(예: 'S02' = '정산보류', 'A10' = '신용카드 대행사')를 가지는지 1:1로 매핑하여 설명하십시오.
2. 정책서는 마크다운 형식으로 작성하며, 반드시 다음 5가지 대분류(H2) 헤더를 사용해야 합니다:
   ## 1. 개요 및 목적
   ## 2. 핵심 정산 비즈니스 규칙 정의
   ## 3. 코드값 및 마스터 데이터 매핑 정보
   ## 4. 프로그램별 정산 영향도 매핑
   ## 5. 예외 처리 및 제약 사항
3. 다이어그램이나 도표를 적극적으로 활용하여 가독성을 높여 주십시오.
4. 응답 전체를 백틱(```markdown ... ```)으로 감싸지 마십시오.";

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine("[Stored Procedure 분석 대상 목록 및 DDL 정보]");
            foreach (var sp in spDefs)
            {
                userPrompt.AppendLine($"### SP: {sp.Schema}.{sp.Name}");
                userPrompt.AppendLine("#### [DDL 소스코드]");
                userPrompt.AppendLine("```sql");
                userPrompt.AppendLine(sp.DdlText);
                userPrompt.AppendLine("```");
                userPrompt.AppendLine("#### [의존성 정보]");
                foreach (var dep in sp.Dependencies)
                {
                    userPrompt.AppendLine($"- 의존 객체: {dep.Schema}.{dep.Name} ({dep.Type})");
                    if (dep.Columns != null && dep.Columns.Count > 0)
                    {
                        userPrompt.AppendLine("  * 컬럼 정보:");
                        foreach (var col in dep.Columns)
                        {
                            var desc = string.IsNullOrEmpty(col.Description) ? "설명 없음" : col.Description;
                            userPrompt.AppendLine($"    - {col.ColumnName} ({col.DataType}): {desc}");
                        }
                    }
                }
                userPrompt.AppendLine();
            }

            userPrompt.AppendLine("[실제 마스터/공통코드 데이터 프로파일링 결과 (JSON)]");
            userPrompt.AppendLine("```json");
            userPrompt.AppendLine(profilingDataJson);
            userPrompt.AppendLine("```");
            userPrompt.AppendLine();
            Log.Information("AI 정산 정책서 생성 요청 전송");
            Log.Debug("[AI 요청 System Prompt]:\n{SystemPrompt}\n[AI 요청 User Prompt]:\n{UserPrompt}", systemPrompt, userPrompt.ToString());

            var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt.ToString(), _temperature, effort: null, cancellationToken: cancellationToken);

            Log.Information("AI 정산 정책서 생성 완료 - 응답 길이: {Length}", aiResult?.Content?.Length ?? 0);
            Log.Debug("[AI 응답 내용]:\n{Response}", aiResult?.Content);

            return aiResult ?? new AiResult();
        }
    }
}
