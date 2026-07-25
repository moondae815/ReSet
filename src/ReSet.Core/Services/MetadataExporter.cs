using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class MetadataExporter : IMetadataExporter
    {
        public async Task ExportRawMetadataAsync(
            SpDefinition spDef, 
            string rawPromptContext, 
            string baseOutputDir, 
            bool saveJson, 
            bool saveContext, 
            bool saveFiles)
        {
            var cleanSpName = $"{spDef.Schema}.{spDef.Name}";
            Log.Information("Raw 메타데이터 디스크 내보내기 시작 - SP: {SpName}, OutputDir: {OutputDir}", cleanSpName, baseOutputDir);

            try
            {
                // 1. 출력 기본 디렉터리 생성 보장
                if (!Directory.Exists(baseOutputDir))
                {
                    Directory.CreateDirectory(baseOutputDir);
                }

                // 2. 단일 JSON 덤프 저장
                var rawFolder = Path.Combine(baseOutputDir, "raw");
                if (!Directory.Exists(rawFolder))
                {
                    Directory.CreateDirectory(rawFolder);
                }

                if (saveJson)
                {
                    var jsonPath = Path.Combine(rawFolder, "metadata.json");
                    Log.Debug("Raw JSON 덤프 작성 중: {JsonPath}", jsonPath);
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var jsonContent = JsonSerializer.Serialize(spDef, options);
                    await File.WriteAllTextAsync(jsonPath, jsonContent, Encoding.UTF8);
                }

                // 3. 프롬프트 컨텍스트 저장
                if (saveContext)
                {
                    var contextPath = Path.Combine(rawFolder, "prompt-context.md");
                    Log.Debug("Raw 프롬프트 컨텍스트 파일 작성 중: {ContextPath}", contextPath);

                    // 기존 .txt 파일이 있다면 삭제 처리
                    var oldTxtFile = Path.Combine(rawFolder, "prompt-context.txt");
                    if (File.Exists(oldTxtFile))
                    {
                        try { File.Delete(oldTxtFile); } catch {}
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine("# AI 입력 프롬프트 원천 콘텍스트 (Raw Prompt Context)");
                    sb.AppendLine();
                    sb.AppendLine("본 문서는 저장 프로시저 역공학 분석을 위해 AI 모델에 실제 전송된 조립 완료 프롬프트 원문입니다.");
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.AppendLine(rawPromptContext);

                    await File.WriteAllTextAsync(contextPath, sb.ToString(), Encoding.UTF8);
                }

                // 4. 개별 파일/폴더 분산 저장
                if (saveFiles)
                {
                    var ddlFolder = Path.Combine(rawFolder, "ddl");
                    Log.Debug("개별 DDL/MD 분산 덤프 작성 중: {DdlFolder}", ddlFolder);
                    if (!Directory.Exists(ddlFolder))
                    {
                        Directory.CreateDirectory(ddlFolder);
                    }

                    // 메인 SP DDL 저장
                    var spDdlPath = Path.Combine(ddlFolder, "sp_definition.sql");
                    await File.WriteAllTextAsync(spDdlPath, spDef.DdlText, Encoding.UTF8);

                    // 의존성 순회하여 개별 덤프
                    foreach (var dep in spDef.Dependencies)
                    {
                        var depFileName = string.IsNullOrEmpty(dep.Database)
                            ? $"{dep.Schema}.{dep.Name}"
                            : $"{dep.Database}.{dep.Schema}.{dep.Name}";

                        // 테이블 스키마 md 저장
                        if (dep.Columns.Count > 0)
                        {
                            var tablesFolder = Path.Combine(ddlFolder, "tables");
                            if (!Directory.Exists(tablesFolder))
                            {
                                Directory.CreateDirectory(tablesFolder);
                            }

                            var mdTableContent = FormatTableSchemaToMarkdown(dep);
                            await File.WriteAllTextAsync(Path.Combine(tablesFolder, $"{depFileName}.md"), mdTableContent, Encoding.UTF8);
                        }

                        // 코드형 객체 DDL 저장
                        if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                        {
                            var subFolderType = dep.Type.Contains("PROCEDURE") ? "procedures" : "functions";
                            var codeFolder = Path.Combine(ddlFolder, subFolderType);
                            if (!Directory.Exists(codeFolder))
                            {
                                Directory.CreateDirectory(codeFolder);
                            }

                            await File.WriteAllTextAsync(Path.Combine(codeFolder, $"{depFileName}.sql"), dep.ReferencedDdlText, Encoding.UTF8);
                        }
                    }
                }
                Log.Information("Raw 메타데이터 디스크 내보내기 성공 - SP: {SpName}", cleanSpName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Raw 메타데이터 내보내기 중 예외가 발생했습니다 (격리됨) - SP: {SpName}", cleanSpName);
            }
        }

        public async Task ExportMigrationInstructionsAsync(
            SpDefinition spDef,
            string specMarkdown,
            string migrationPlan,
            string baseOutputDir)
        {
            var cleanSpName = $"{spDef.Schema}.{spDef.Name}";
            
            var agentFolder = Path.Combine(baseOutputDir, "agent");
            if (!Directory.Exists(agentFolder))
            {
                Directory.CreateDirectory(agentFolder);
            }

            var instructionsPath = Path.Combine(agentFolder, "MigrationInstructions.md");
            var todoPath = Path.Combine(agentFolder, "todo.md");

            Log.Information("마이그레이션 지시서 번들 내보내기 시작 - SP: {SpName}, OutputDir: {OutputDir}", cleanSpName, baseOutputDir);

            try
            {

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# 🚀 Migration Instructions for Coding Agent ({cleanSpName})");
                sb.AppendLine();
                sb.AppendLine("본 문서는 SQL Server Stored Procedure에서 현대화된 배치 소스 코드로의 마이그레이션을 자동 수행하기 위해 코딩 에이전트(Claude Code, Antigravity CLI 등)에 제공되는 지시서 및 컨텍스트입니다.");
                sb.AppendLine("아래 설계 문서(Specification)와 레거시 SQL DDL 소스코드를 읽고 지침에 따라 현대화된 타겟 소스코드를 작성해 주십시오.");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## 📋 1. 마이그레이션 기본 정보");
                sb.AppendLine($"- **레거시 SP 개체**: `{cleanSpName}`");
                sb.AppendLine($"- **수집된 의존성 개체 개수**: {spDef.Dependencies.Count}개");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## 📄 2. 현대화 비즈니스 설계서 (Business Specification)");
                sb.AppendLine("이 설계서는 레거시 SP의 핵심 비즈니스 로직, 입출력 스펙, 예외 처리 규칙을 명시합니다. 구현 시 반드시 이 동작 방식을 1:1로 만족해야 합니다.");
                sb.AppendLine();
                sb.AppendLine(specMarkdown);
                sb.AppendLine();
                
                if (!string.IsNullOrEmpty(migrationPlan))
                {
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.AppendLine("## 🗺️ 3. 마이그레이션 전환 계획 (Migration Plan)");
                    sb.AppendLine("배치 마이그레이션을 구성하기 위한 실행 계획입니다.");
                    sb.AppendLine();
                    sb.AppendLine(migrationPlan);
                    sb.AppendLine();
                }

                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## 💻 4. 레거시 SQL Server Stored Procedure DDL 원본");
                sb.AppendLine("마이그레이션의 최종 로직 정합성을 유지하기 위해 참고해야 할 오리지널 SQL 코드입니다.");
                sb.AppendLine();
                sb.AppendLine("```sql");
                sb.AppendLine(spDef.DdlText);
                sb.AppendLine("```");
                sb.AppendLine();

                if (spDef.Dependencies.Count > 0)
                {
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.AppendLine("## 📦 5. 의존하는 주요 참조 스키마 및 소스코드");
                    sb.AppendLine("데이터 엑세스 계층(Repository/DAO) 구현에 참고해야 할 테이블 스키마 파일들의 링크입니다.");
                    sb.AppendLine();

                    var tableSchemasDir = Path.Combine(baseOutputDir, "TableSchemas");
                    if (!Directory.Exists(tableSchemasDir))
                    {
                        Directory.CreateDirectory(tableSchemasDir);
                    }

                    foreach (var dep in spDef.Dependencies)
                    {
                        var cleanDepName = string.IsNullOrEmpty(dep.Database)
                            ? $"{dep.Schema}.{dep.Name}"
                            : $"{dep.Database}.{dep.Schema}.{dep.Name}";
                        
                        var contextFileName = $"{cleanDepName}.md";
                        var contextFilePath = Path.Combine(tableSchemasDir, contextFileName);
                        
                        var contextSb = new System.Text.StringBuilder();
                        contextSb.AppendLine($"# {dep.Type}: {cleanDepName}");
                        contextSb.AppendLine();
                        
                        if (dep.Columns.Count > 0)
                        {
                            contextSb.AppendLine(FormatTableSchemaToMarkdown(dep));
                        }
                        
                        if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                        {
                            contextSb.AppendLine("## Referenced SQL DDL:");
                            contextSb.AppendLine("```sql");
                            contextSb.AppendLine(dep.ReferencedDdlText);
                            contextSb.AppendLine("```");
                        }
                        
                        File.WriteAllText(contextFilePath, contextSb.ToString());
                        sb.AppendLine($"- **{cleanDepName}**: [TableSchemas/{contextFileName}](TableSchemas/{contextFileName})");
                    }
                }

                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## 🔑 6. 에이전트 핵심 수행 지침 (Agent Execution Guidelines)");
                sb.AppendLine("당신은 전문 코딩 에이전트입니다. 이 파일(`MigrationInstructions.md`)에 기술된 비즈니스 명세와 하단의 레거시 SQL DDL을 분석하여 현대화된 배치 소스 코드를 생성하십시오.");
                sb.AppendLine("단, 한 번에 모든 코드를 작성하려고 시도하지 말고, 함께 제공된 체크리스트 파일(`todo.md`)의 각 단계를 점진적으로 이행하면서 완료될 때마다 상태를 `[x]`로 업데이트하십시오.");
                sb.AppendLine("1. 설계서의 입출력 규격 및 비즈니스 로직 단계를 완벽히 만족할 것.");
                sb.AppendLine("2. 생성할 파일 경로는 프로젝트 아키텍처 규칙에 맞춰 작성할 것.");
                sb.AppendLine("3. Hexagonal Architecture를 적용하여 핵심 비즈니스 도메인과 DB 액세스 계층(Port/Adapter)을 엄격히 분리할 것.");
                sb.AppendLine("4. Design by Contract를 준수하여 입력 인자 유효성 검증(가드 구문)을 확실히 반영할 것.");
                sb.AppendLine("5. 배치의 재실행 가능성을 위해 Upsert(Merge) 패턴을 활용한 멱등성(Idempotency)을 보장할 것.");
                sb.AppendLine("6. 제공된 자가 검증용 단위 테스트 코드(tests 폴더 내 위치)를 100% 통과(PASS)시키고 빌드가 성공함을 자체 검증할 것.");
                sb.AppendLine();

                await File.WriteAllTextAsync(instructionsPath, sb.ToString(), Encoding.UTF8);
                Log.Debug("마이그레이션 지시서 파일 쓰기 성공: {InstructionsPath}", instructionsPath);

                // _todo.md 생성
                var todoSb = new StringBuilder();
                todoSb.AppendLine($"# 📋 {cleanSpName} 마이그레이션 구현 체크리스트");
                todoSb.AppendLine();
                todoSb.AppendLine("AI 코딩 에이전트는 아래 체크박스를 한 번에 하나씩 확인하여 상태를 `[x]`로 변경해가며 점진적으로 구현하십시오.");
                todoSb.AppendLine();
                todoSb.AppendLine("- [ ] 1. 프로젝트 폴더 구조 및 뼈대 코드 생성 (Hexagonal Architecture 적용)");
                todoSb.AppendLine("- [ ] 2. 관련 DDL 반영 및 데이터 액세스(Repository/DAO/Adapter) 레이어 구현");
                todoSb.AppendLine("- [ ] 3. DbC(Design by Contract)에 따른 파라미터 사전 검증 및 비즈니스 로직 단계별 구현");
                todoSb.AppendLine("- [ ] 4. 예외 처리, 트랜잭션 격리 및 멱등성(Upsert 패턴) 처리 보완");
                todoSb.AppendLine("- [ ] 5. 공급된 단위 테스트 코드를 100% 통과(PASS)시키고 전체 로컬 빌드 성공 확인");
                await File.WriteAllTextAsync(todoPath, todoSb.ToString(), Encoding.UTF8);
                Log.Debug("마이그레이션 Todo 파일 쓰기 성공: {TodoPath}", todoPath);

                Log.Information("마이그레이션 지시서 번들 내보내기 완료 - SP: {SpName}", cleanSpName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "마이그레이션 지시서 내보내기 중 예외 발생 (격리됨) - SP: {SpName}", cleanSpName);
            }
        }

        public async Task ExportConsolidatedMigrationInstructionsAsync(
            System.Collections.Generic.List<SpDefinition> spDefs,
            string consolidatedPlan,
            string jobName,
            string baseOutputDir)
        {
            var agentFolder = Path.Combine(baseOutputDir, "agent");
            if (!Directory.Exists(agentFolder))
            {
                Directory.CreateDirectory(agentFolder);
            }

            var instructionsPath = Path.Combine(agentFolder, "MigrationInstructions.md");
            var todoPath = Path.Combine(agentFolder, "todo.md");

            Log.Information("통합 마이그레이션 지시서 번들 내보내기 시작 - JobName: {JobName}, OutputDir: {OutputDir}", jobName, baseOutputDir);

            try
            {
                if (!Directory.Exists(baseOutputDir))
                {
                    Directory.CreateDirectory(baseOutputDir);
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# 🚀 Consolidated Migration Instructions for Coding Agent ({jobName})");
                sb.AppendLine();
                sb.AppendLine("본 문서는 복수의 SQL Server Stored Procedure들을 하나의 통합 배치 작업으로 마이그레이션하기 위해 코딩 에이전트(Claude Code, Antigravity CLI 등)에 제공되는 지시서 및 컨텍스트입니다.");
                sb.AppendLine("아래 통합 배치 전환 계획서(Consolidated Migration Plan)와 개별 의존성 테이블 스키마들을 분석하여 현대화된 배치 소스 코드를 작성해 주십시오.");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## 🗺️ 1. 통합 배치 전환 계획 (Consolidated Migration Plan)");
                sb.AppendLine("이 계획은 전체 배치의 흐름과 마이그레이션 전략을 다룹니다.");
                sb.AppendLine();
                sb.AppendLine(consolidatedPlan);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                var tableSchemasDir = Path.Combine(baseOutputDir, "TableSchemas");
                if (!Directory.Exists(tableSchemasDir))
                {
                    Directory.CreateDirectory(tableSchemasDir);
                }

                sb.AppendLine("## 📋 2. 대상 Stored Procedure 및 테이블 스키마 참조 링크");
                sb.AppendLine("아래 분리된 파일들을 읽어(Read) 데이터 엑세스 계층 구현 시 테이블 스키마와 데이터 타입을 확인하십시오. 핵심 비즈니스 로직 구현은 오직 1번 항목의 '통합 배치 전환 계획서(Plan)' 내용과 의사코드만을 엄격히 따라야 합니다.");
                sb.AppendLine();

                var distinctDependencies = spDefs
                    .SelectMany(sp => sp.Dependencies)
                    .GroupBy(d => $"{d.Database}.{d.Schema}.{d.Name}")
                    .Select(g => g.First())
                    .ToList();

                foreach (var dep in distinctDependencies)
                {
                    var cleanDepName = string.IsNullOrEmpty(dep.Database) 
                        ? $"{dep.Schema}.{dep.Name}" 
                        : $"{dep.Database}.{dep.Schema}.{dep.Name}";
                    
                    var contextFileName = $"{cleanDepName}.md";
                    var contextFilePath = Path.Combine(tableSchemasDir, contextFileName);
                    
                    var contextSb = new System.Text.StringBuilder();
                    contextSb.AppendLine($"# {dep.Type}: {cleanDepName}");
                    contextSb.AppendLine();
                    
                    if (dep.Columns.Count > 0)
                    {
                        contextSb.AppendLine(FormatTableSchemaToMarkdown(dep));
                    }
                    
                    if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                    {
                        contextSb.AppendLine("## Referenced SQL DDL:");
                        contextSb.AppendLine("```sql");
                        contextSb.AppendLine(dep.ReferencedDdlText);
                        contextSb.AppendLine("```");
                    }
                    
                    File.WriteAllText(contextFilePath, contextSb.ToString());
                    sb.AppendLine($"- **{cleanDepName}**: [TableSchemas/{contextFileName}](TableSchemas/{contextFileName})");
                }

                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## 🔑 3. 에이전트 핵심 수행 지침 (Agent Execution Guidelines)");
                sb.AppendLine("당신은 전문 코딩 에이전트입니다. 이 파일(`MigrationInstructions.md`)에 기술된 통합 배치 전환 계획과 `TableSchemas/` 디렉토리에 정의된 의존성 스키마들을 분석하여 현대화된 통합 배치 소스 코드를 생성하십시오.");
                sb.AppendLine("단, 한 번에 모든 코드를 작성하려고 시도하지 말고, 함께 제공된 체크리스트 파일(`todo.md`)의 각 단계를 점진적으로 이행하면서 완료될 때마다 상태를 `[x]`로 업데이트하십시오.");
                sb.AppendLine("1. 전환 계획의 배치 단계 및 공통 모듈 설계 규칙을 엄격히 준수할 것.");
                sb.AppendLine("2. 생성할 파일 경로는 프로젝트 아키텍처 규칙에 맞춰 작성할 것.");
                sb.AppendLine("3. Hexagonal Architecture를 적용하여 핵심 비즈니스 도메인과 DB 액세스 계층(Port/Adapter)을 엄격히 분리할 것.");
                sb.AppendLine("4. Design by Contract를 준수하여 입력 인자 유효성 검증(가드 구문)을 확실히 반영할 것.");
                sb.AppendLine("5. 배치의 재실행 가능성을 위해 Upsert(Merge) 패턴을 활용한 멱등성(Idempotency)을 보장할 것.");
                sb.AppendLine("6. 제공된 자가 검증용 단위 테스트 및 ArchUnit 아키텍처 검증 코드(tests 폴더 내 위치)를 100% 통과(PASS)시키고 빌드가 성공함을 자체 검증할 것.");
                sb.AppendLine();

                await File.WriteAllTextAsync(instructionsPath, sb.ToString(), Encoding.UTF8);
                Log.Debug("통합 마이그레이션 지시서 파일 쓰기 성공: {InstructionsPath}", instructionsPath);

                // _todo.md 생성
                var todoSb = new StringBuilder();
                todoSb.AppendLine($"# 📋 {jobName} 통합 배치 마이그레이션 구현 체크리스트");
                todoSb.AppendLine();
                todoSb.AppendLine("AI 코딩 에이전트는 아래 체크박스를 한 번에 하나씩 확인하여 상태를 `[x]`로 변경해가며 점진적으로 구현하십시오.");
                todoSb.AppendLine();
                todoSb.AppendLine("- [ ] 1. 통합 배치 프로젝트 폴더 구조 및 뼈대 코드 생성 (Hexagonal Architecture 적용)");
                todoSb.AppendLine("- [ ] 2. 설계서에 명시된 대상 테이블 DDL 파악 및 데이터 액세스(Repository/DAO/Adapter) 계층 구현");
                todoSb.AppendLine("- [ ] 3. 계획서의 [통합 배치 아키텍처 개요]에 정의된 공통 초기화(사전 검증 등) 로직 구현");
                
                int stepCounter = 4;
                foreach (var sp in spDefs)
                {
                    todoSb.AppendLine($"- [ ] {stepCounter}. Step: `{sp.Name}` 기반의 비즈니스 로직(Chunk/Tasklet) 및 멱등성 구현 완료");
                    stepCounter++;
                }
                
                todoSb.AppendLine($"- [ ] {stepCounter}. 모든 Step이 통합된 최종 Job 파이프라인 조립 및 예외/트랜잭션 롤백 처리 보완");
                todoSb.AppendLine($"- [ ] {stepCounter + 1}. 공급된 단위 테스트 및 ArchUnit 정적 검증 통과, 솔루션 빌드 성공 확인");
                await File.WriteAllTextAsync(todoPath, todoSb.ToString(), Encoding.UTF8);
                Log.Debug("통합 마이그레이션 Todo 파일 쓰기 성공: {TodoPath}", todoPath);

                Log.Information("통합 마이그레이션 지시서 번들 내보내기 완료 - JobName: {JobName}", jobName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "통합 마이그레이션 지시서 내보내기 중 예외 발생 (격리됨) - JobName: {JobName}", jobName);
            }
        }

        private string FormatTableSchemaToMarkdown(DependencyInfo dep)
        {
            var sb = new StringBuilder();
            var depFullName = string.IsNullOrEmpty(dep.Database)
                ? $"{dep.Schema}.{dep.Name}"
                : $"[{dep.Database}].[{dep.Schema}].[{dep.Name}]";
            sb.AppendLine($"# 테이블 스키마: {depFullName}");
            sb.AppendLine($"* 객체 타입: {dep.Type}");
            sb.AppendLine($"* 발견 깊이: {dep.DiscoveryDepth}단계");
            if (!string.IsNullOrEmpty(dep.Description))
            {
                sb.AppendLine($"* 테이블 설명: {dep.Description}");
            }
            sb.AppendLine();
            sb.AppendLine("| 컬럼명 | 데이터 타입 | Null 허용 | Identity | 기본값 | 제약 조건 | 설명 |");
            sb.AppendLine("| :--- | :--- | :---: | :---: | :--- | :--- | :--- |");
            
            foreach (var col in dep.Columns)
            {
                var constraints = new System.Collections.Generic.List<string>();
                if (col.IsPrimaryKey) constraints.Add("PRIMARY KEY");
                if (col.IsForeignKey) constraints.Add("FOREIGN KEY");
                
                var constraintStr = string.Join(", ", constraints);
                var nullableStr = col.IsNullable ? "Yes" : "No";
                var identityStr = col.IsIdentity ? "Yes" : "No";
                var defaultStr = col.DefaultValue ?? "";
                
                sb.AppendLine($"| {col.ColumnName} | {col.DataType} | {nullableStr} | {identityStr} | {defaultStr} | {constraintStr} | {col.Description} |");
            }

            if (dep.Indexes != null && dep.Indexes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 인덱스 정보");
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

        public async Task AppendFeedbackToInstructionsAsync(string instructionsFilePath, string feedbackMarkdown)
        {
            if (!File.Exists(instructionsFilePath))
            {
                Log.Warning("지시서 파일이 존재하지 않아 피드백을 추가할 수 없습니다 - Path: {Path}", instructionsFilePath);
                return;
            }

            try
            {
                var content = await File.ReadAllTextAsync(instructionsFilePath, Encoding.UTF8);

                // 기존 피드백 마커가 있으면 제거
                var startMarker = "<!-- FEEDBACK_START -->";
                var endMarker = "<!-- FEEDBACK_END -->";
                var startIndex = content.IndexOf(startMarker);
                var endIndex = content.IndexOf(endMarker);

                if (startIndex >= 0 && endIndex > startIndex)
                {
                    var before = content.Substring(0, startIndex).TrimEnd();
                    var after = content.Substring(endIndex + endMarker.Length).TrimStart();
                    content = before + "\n\n" + after;
                }

                var sb = new StringBuilder(content.TrimEnd());
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine(startMarker);
                sb.AppendLine("## 🔍 검증 피드백 및 자가 수정 가이드");
                sb.AppendLine("이전 빌드/테스트 또는 L1/L2 일치성 분석 결과, 다음 불일치 사항이 발견되었습니다. 이 문제를 최우선으로 해결하여 소스코드를 수정해 주십시오.");
                sb.AppendLine();
                sb.AppendLine(feedbackMarkdown);
                sb.AppendLine(endMarker);

                await File.WriteAllTextAsync(instructionsFilePath, sb.ToString(), Encoding.UTF8);
                Log.Information("지시서에 L1/L2 검증 피드백 영역을 업데이트 완료 - Path: {Path}", instructionsFilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "지시서 피드백 추가 중 오류 발생 - Path: {Path}", instructionsFilePath);
            }
        }

        public async Task ExportUnitTestCodeAsync(string baseOutputDir, string procedureName, string targetLanguage, string testCodeContent)
        {
            try
            {
                var isCSharp = targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase);
                var ext = isCSharp ? ".cs" : ".java";
                var fileName = isCSharp ? $"{procedureName}Tests{ext}" : $"{procedureName}Test{ext}";

                var testsFolder = Path.Combine(baseOutputDir, "tests");
                if (!Directory.Exists(testsFolder))
                {
                    Directory.CreateDirectory(testsFolder);
                }

                var targetPath = Path.Combine(testsFolder, fileName);
                await File.WriteAllTextAsync(targetPath, testCodeContent, Encoding.UTF8);
                Log.Information("자가 검증용 단위 테스트 파일 내보내기 완료 - Path: {Path}", targetPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "단위 테스트 파일 저장 중 오류 발생 - SpName: {SpName}", procedureName);
            }
        }
    }
}
