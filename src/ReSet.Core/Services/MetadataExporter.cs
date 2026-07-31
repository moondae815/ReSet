using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class MetadataExporter : IMetadataExporter
    {
        public Task ExportCodeObjectArtifactsAsync(
            SpDefinition definition,
            CodeObjectKey objectKey,
            CodeObjectPipelineResult graph,
            DependencyArtifactMode artifactMode,
            string outputRoot,
            string? rawPromptContext = null,
            CancellationToken cancellationToken = default) =>
            ExportCodeObjectArtifactsAsync(
                definition,
                objectKey,
                graph,
                artifactMode,
                new OutputPathResolver(objectKey.Database, outputRoot),
                rawPromptContext,
                cancellationToken);

        public async Task ExportCodeObjectArtifactsAsync(
            SpDefinition definition,
            CodeObjectKey objectKey,
            CodeObjectPipelineResult graph,
            DependencyArtifactMode artifactMode,
            OutputPathResolver paths,
            string? rawPromptContext = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(objectKey);
            ArgumentNullException.ThrowIfNull(graph);
            ArgumentNullException.ThrowIfNull(paths);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                PopulateArtifactPaths(graph, paths);

                var canonicalDdlPath = paths.ResolveCanonicalDdlPath(objectKey);
                Directory.CreateDirectory(Path.GetDirectoryName(canonicalDdlPath)!);
                await File.WriteAllTextAsync(
                    canonicalDdlPath,
                    definition.DdlText ?? string.Empty,
                    Encoding.UTF8,
                    cancellationToken);

                var rawDirectory = Path.GetDirectoryName(canonicalDdlPath)!;
                var promptContext = rawPromptContext ?? definition.RawPromptContext ?? string.Empty;
                await File.WriteAllTextAsync(
                    Path.Combine(rawDirectory, "prompt-context.md"),
                    promptContext,
                    Encoding.UTF8,
                    cancellationToken);

                if (artifactMode == DependencyArtifactMode.PortableBundle)
                {
                    await ExportReferencedCodeDdlsAsync(definition, rawDirectory, cancellationToken);
                }

                var manifestPath = paths.ResolveManifestPath(objectKey);
                Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
                var objectDirectoryForManifest = Path.GetDirectoryName(Path.GetDirectoryName(manifestPath)!)!;
                var manifest = BuildManifest(definition, objectKey, graph, paths, objectDirectoryForManifest);
                var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(manifestPath, json, Encoding.UTF8, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "코드 객체 아티팩트 저장 중 오류가 발생했습니다 (격리됨): {ObjectKey}", objectKey.CanonicalName);
            }
        }

        private static void PopulateArtifactPaths(CodeObjectPipelineResult graph, OutputPathResolver paths)
        {
            foreach (var node in graph.Nodes)
            {
                node.SpecPath ??= paths.ResolveSpecPath(node.Key);
                node.DdlPath ??= paths.ResolveCanonicalDdlPath(node.Key);
            }

            foreach (var result in graph.AnalysisResults)
            {
                result.SpecPath ??= paths.ResolveSpecPath(result.Key);
                result.DdlPath ??= paths.ResolveCanonicalDdlPath(result.Key);
            }
        }

        private static async Task ExportReferencedCodeDdlsAsync(
            SpDefinition definition,
            string rawDirectory,
            CancellationToken cancellationToken)
        {
            foreach (var dependency in definition.Dependencies.Where(dependency =>
                         !string.IsNullOrWhiteSpace(dependency.ReferencedDdlText)))
            {
                var folderName = NormalizeCodeObjectDdlFolder(dependency.Type);
                if (folderName is null)
                {
                    continue;
                }

                var dependencyName = string.Join(
                    ".",
                    new[]
                    {
                        dependency.Database,
                        dependency.Schema,
                        dependency.Name
                    }
                    .Where(segment => !string.IsNullOrWhiteSpace(segment))
                    .Select(segment => OutputPathResolver.EncodePathSegment(segment!)));
                var folder = Path.Combine(rawDirectory, "ddl", folderName);
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(
                    Path.Combine(folder, $"{dependencyName}.sql"),
                    dependency.ReferencedDdlText!,
                    Encoding.UTF8,
                    cancellationToken);
            }
        }

        private static string? NormalizeCodeObjectDdlFolder(string? dependencyType) =>
            dependencyType?.Trim().ToUpperInvariant() switch
            {
                "PROCEDURE" or "PROC" or "P" or "PC" or "SQL_STORED_PROCEDURE" or "CLR_STORED_PROCEDURE" => "procedures",
                "FUNCTION" or "FN" or "IF" or "TF" or "FS" or "FT" or
                    "SQL_SCALAR_FUNCTION" or "SQL_TABLE_VALUED_FUNCTION" or "SQL_INLINE_TABLE_VALUED_FUNCTION" or
                    "CLR_SCALAR_FUNCTION" or "CLR_TABLE_VALUED_FUNCTION" => "functions",
                _ => null
            };

        private static DependencyManifest BuildManifest(
            SpDefinition definition,
            CodeObjectKey objectKey,
            CodeObjectPipelineResult graph,
            OutputPathResolver paths,
            string objectDirectory)
        {
            var definitions = graph.AnalysisResults
                .Where(result => result.Key is not null)
                .ToDictionary(result => result.Key, result => result.Definition);
            definitions[objectKey] = definition;

            return new DependencyManifest
            {
                Key = objectKey.CanonicalName,
                Nodes = graph.Nodes
                    .OrderBy(node => node.Key.CanonicalName, StringComparer.OrdinalIgnoreCase)
                    .Select(node => new DependencyManifestNode
                    {
                        Key = node.Key.CanonicalName,
                        Sha256 = definitions.TryGetValue(node.Key, out var nodeDefinition)
                            ? ComputeSha256(nodeDefinition.DdlText)
                            : null,
                        Status = node.Status.ToString(),
                        Error = node.Error,
                        SpecPath = ToRelativePath(objectDirectory, node.SpecPath ?? paths.ResolveSpecPath(node.Key)),
                        DdlPath = ToRelativePath(objectDirectory, node.DdlPath ?? paths.ResolveCanonicalDdlPath(node.Key))
                    })
                    .ToList(),
                Calls = graph.DependencyEdges
                    .OrderBy(edge => edge.Source.CanonicalName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(edge => edge.Target.CanonicalName, StringComparer.OrdinalIgnoreCase)
                    .Select(edge => new DependencyManifestEdge
                    {
                        Source = edge.Source.CanonicalName,
                        Target = edge.Target.CanonicalName,
                        IsDynamicSqlCandidate = edge.IsDynamicSqlCandidate
                    })
                    .ToList()
            };
        }

        private static string ComputeSha256(string? value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

        private static string ToRelativePath(string baseDirectory, string path) =>
            Path.GetRelativePath(baseDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

        private sealed class DependencyManifest
        {
            public string Key { get; init; } = string.Empty;
            public List<DependencyManifestNode> Nodes { get; init; } = new();
            public List<DependencyManifestEdge> Calls { get; init; } = new();
        }

        private sealed class DependencyManifestNode
        {
            public string Key { get; init; } = string.Empty;
            public string? Sha256 { get; init; }
            public string Status { get; init; } = string.Empty;
            public string? Error { get; init; }
            public string SpecPath { get; init; } = string.Empty;
            public string DdlPath { get; init; } = string.Empty;
        }

        private sealed class DependencyManifestEdge
        {
            public string Source { get; init; } = string.Empty;
            public string Target { get; init; } = string.Empty;
            public bool IsDynamicSqlCandidate { get; init; }
        }

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

        public async Task ExportConsolidatedMigrationInstructionsAsync(
            System.Collections.Generic.List<SpDefinition> spDefs,
            string consolidatedPlan,
            string jobName,
            string baseOutputDir,
            string targetLanguage)
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
                var rawDdlDir = Path.Combine(baseOutputDir, "raw", "ddl");
                if (!Directory.Exists(rawDdlDir))
                {
                    Directory.CreateDirectory(rawDdlDir);
                }

                sb.AppendLine("## 📋 2. 대상 Stored Procedure 및 테이블 스키마 참조 링크");
                sb.AppendLine("아래 분리된 파일들을 읽어(Read) 데이터 엑세스 계층 구현 시 테이블 스키마와 데이터 타입을 확인하십시오. 핵심 비즈니스 로직 구현은 오직 1번 항목의 '통합 배치 전환 계획서(Plan)' 내용과 의사코드만을 엄격히 따라야 하며, 원본 SQL 코드를 조회하려고 시도해서는 안 됩니다.");
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
                    var contextFilePath = Path.Combine(rawDdlDir, contextFileName);
                    
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
                    sb.AppendLine($"- **{cleanDepName}**: [raw/ddl/{contextFileName}](raw/ddl/{contextFileName})");
                }

                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## 📚 3. 원본 Stored Procedure 설계 명세서");
                sb.AppendLine("개별 프로시저의 세부적인 비즈니스 로직(예: UPDATE 수식 등)을 확인해야 할 경우 아래 링크된 개별 설계서(Spec.md)를 참조하십시오.");
                sb.AppendLine();
                foreach (var spDef in spDefs)
                {
                    var spCleanName = $"{spDef.Schema}.{spDef.Name}";
                    var specPath = $"../../../Procedures/{spDef.Schema}.{spDef.Name}/docs/Spec.md";
                    sb.AppendLine($"- **{spCleanName}**:");
                    sb.AppendLine($"  - [Spec.md]({specPath}) (UPDATE/INSERT 상세 매핑 수식 포함)");
                }

                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## 🔑 4. 에이전트 핵심 수행 지침 (Agent Execution Guidelines)");
                sb.AppendLine("당신은 전문 코딩 에이전트입니다. 이 파일(`MigrationInstructions.md`)에 기술된 통합 배치 전환 계획과 `raw/ddl/` 디렉토리에 정의된 의존성 스키마, 그리고 원본 명세서(Spec.md)만을 참조하여 현대화된 통합 배치 소스 코드를 생성하십시오.");
                sb.AppendLine("**[경고] 원본 Stored Procedure(.sql) 파일은 레거시 코드이므로 절대 검색(find 명령어 등)하거나 직접 참조하지 마십시오. 모든 비즈니스 로직은 이미 분석 완료된 Spec.md 문서에 정의되어 있습니다.**");
                sb.AppendLine("단, 한 번에 모든 코드를 작성하려고 시도하지 말고, 함께 제공된 체크리스트 파일(`todo.md`)의 각 단계를 점진적으로 이행하면서 완료될 때마다 상태를 `[x]`로 업데이트하십시오.");
                sb.AppendLine("1. 전환 계획의 배치 단계 및 공통 모듈 설계 규칙을 엄격히 준수할 일.");
                sb.AppendLine("2. 생성할 파일 경로는 타겟 프로젝트의 아키텍처 규칙에 맞춰 작성할 일.");
                sb.AppendLine("3. 데이터 엑세스 계층(Repository/DAO 등)은 타겟 언어 및 프레임워크의 권장 패턴을 따를 일.");
                sb.AppendLine("4. 의존성 역전 원칙(DIP) 등을 준수하여 비즈니스 로직과 인프라스트럭처 결합도를 낮출 일.");
                sb.AppendLine("5. 트랜잭션 단위와 예외 처리(Rollback 등)를 명확히 설계하여 데이터 정합성을 보장할 일.");
                sb.AppendLine("6. 제공된 자가 검증용 단위 테스트 및 아키텍처 검증 코드를 통과(PASS)시키고 빌드가 성공함을 자체 점검할 일.");
                sb.AppendLine("7. [중요] 어떠한 경우에도 `// implementation omitted`, `// TODO`, `/* Build SQL */` 등의 주석으로 코드를 생략(Placeholder)하지 마십시오. 반드시 명세서에 있는 원본 DML(SELECT/INSERT/UPDATE/DELETE) 로직을 모두 프로그래밍 언어(C# 등)의 텍스트 쿼리로 풀어서 100% 완전하게 작성해야 합니다.");
                sb.AppendLine("8. [중요] Worker.cs 구성 시 반드시 IConfiguration 등을 통해 명세된 모든 DB Factory 의존성(예: `MainDb`, `PaymentDb`, `SettleCardDb`, `PlCardDb` 등)을 `SettleContext`에 할당해야 합니다. 누락 시 런타임 예외가 발생하여 검증을 통과할 수 없습니다.");
                sb.AppendLine("9. [중요] 모든 Tasklet 클래스는 사전에 제공된 `src/AbstractSettleTasklet.cs`의 `AbstractSettleTasklet`을 강제로 상속받아 구현해야 합니다. 임의의 구조를 만들거나 에러코드를 자의적으로 변경하지 마십시오.");
                sb.AppendLine();
                
                sb.AppendLine("## 🛠️ 5. 기술 스택 및 인프라 설정 가이드 (Tech Stack & Configuration)");
                if (targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("* **Data Access 및 프레임워크**: 데이터베이스 접근은 ADO.NET(또는 Dapper)을 사용하고, 배치 호스팅은 .NET 10 Worker Service 기반으로 작성하며, Microsoft.Extensions.DependencyInjection을 통해 의존성을 주입하십시오.");
                    sb.AppendLine("* **멀티 DB 커넥션 설정**: `appsettings.json` 내에 다음과 같은 `ConnectionStrings` 구조를 구성하고, `RetryableSqlExecutor`에서 분기 처리하여 주입받을 수 있도록 모델링하십시오.");
                    sb.AppendLine("  ```json");
                    sb.AppendLine("  {");
                    sb.AppendLine("    \"ConnectionStrings\": {");
                    sb.AppendLine("      \"PaymentDB\": \"Server=...;Database=PaymentDB;...\",");
                    sb.AppendLine("      \"SettleCardDB\": \"Server=...;Database=SETTLE_CARD_DB;...\",");
                    sb.AppendLine("      \"PLCardDB\": \"Server=...;Database=PLCardDB;...\",");
                    sb.AppendLine("      \"SettlePoqDB\": \"Server=...;Database=SETTLE_POQ_DB;...\"");
                    sb.AppendLine("    }");
                    sb.AppendLine("  }");
                    sb.AppendLine("  ```");
                }
                else if (targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("* **Data Access 및 프레임워크**: 데이터베이스 접근은 MyBatis(또는 Spring Data JDBC)를 사용하고, 배치 호스팅은 Spring Batch (Spring Boot 기반)로 작성하며, 의존성 주입을 활용하십시오.");
                    sb.AppendLine("* **멀티 DB 커넥션 설정**: `application.yml` 내에 다음과 같은 다중 DataSource 구조를 구성하고, 각 Step이 알맞은 TransactionManager와 JdbcTemplate을 주입받을 수 있도록 모델링하십시오.");
                    sb.AppendLine("  ```yaml");
                    sb.AppendLine("  spring:");
                    sb.AppendLine("    datasource:");
                    sb.AppendLine("      payment:");
                    sb.AppendLine("        jdbc-url: jdbc:sqlserver://...;databaseName=PaymentDB");
                    sb.AppendLine("      settle-card:");
                    sb.AppendLine("        jdbc-url: jdbc:sqlserver://...;databaseName=SETTLE_CARD_DB");
                    sb.AppendLine("      pl-card:");
                    sb.AppendLine("        jdbc-url: jdbc:sqlserver://...;databaseName=PLCardDB");
                    sb.AppendLine("      settle-poq:");
                    sb.AppendLine("        jdbc-url: jdbc:sqlserver://...;databaseName=SETTLE_POQ_DB");
                    sb.AppendLine("  ```");
                }
                sb.AppendLine();

                await File.WriteAllTextAsync(instructionsPath, sb.ToString(), Encoding.UTF8);
                Log.Debug("통합 마이그레이션 지시서 파일 쓰기 성공: {InstructionsPath}", instructionsPath);

                // _todo.md 생성
                var todoSb = new StringBuilder();
                todoSb.AppendLine($"# 📋 {jobName} 통합 배치 마이그레이션 구현 체크리스트");
                todoSb.AppendLine();
                todoSb.AppendLine("AI 코딩 에이전트는 아래 체크박스를 한 번에 하나씩 확인하여 상태를 `[x]`로 변경해가며 점진적으로 구현하십시오.");
                todoSb.AppendLine();
                todoSb.AppendLine("## ⚠️ [필수 행동 수칙: Agentic Workflow 루프]");
                todoSb.AppendLine("각 Step(`SP_NAME`)을 구현할 때, 반드시 아래의 **Superpowers Skills** 워크플로우를 활용하십시오.");
                todoSb.AppendLine("1. **Subagent-Driven Development**: 복잡한 Phase(Tasklet) 구현 시, 주 에이전트가 직접 모든 코드를 작성하지 말고 `invoke_subagent` 도구를 사용해 서브에이전트에게 구현을 위임하십시오.");
                todoSb.AppendLine("2. **Test-Driven Development (TDD)**: 서브에이전트는 반드시 비즈니스 로직(예: PreCheck)을 작성하기 전에 실패하는 XUnit 테스트를 먼저 작성하고 통과시켜야 합니다.");
                todoSb.AppendLine("3. **Requesting Code Review**: 서브에이전트가 구현을 완료하면, 주 에이전트는 코드 리뷰를 수행하여 Spec.md의 모든 예외 처리 및 쿼리 조건이 누락 없이 반영되었는지 검증하십시오.");
                todoSb.AppendLine();
                
                todoSb.AppendLine("- [ ] 0. 프로젝트 빌드 환경 구성 및 필수 패키지/라이브러리 설치 (예: Dapper, Moq, MyBatis, ArchUnit 등)");
                todoSb.AppendLine("- [ ] 1. 통합 배치 프로젝트 폴더 구조 및 뼈대 코드 생성 (Hexagonal Architecture 적용)");
                todoSb.AppendLine("- [ ] 2. 설계서에 명시된 대상 테이블 DDL 파악 및 데이터 액세스(Repository/DAO/Adapter) 계층 구현");
                todoSb.AppendLine("- [ ] 3. 계획서의 [통합 배치 아키텍처 개요]에 정의된 공통 초기화(사전 검증 등) 로직 구현");
                
                int stepCounter = 4;
                foreach (var sp in spDefs)
                {
                    todoSb.AppendLine($"- [ ] {stepCounter}. Step: `{sp.Name}` 기반 비즈니스 로직 구현 (Agentic Workflow 루프 완료 포함)");
                    stepCounter++;
                }
                
                todoSb.AppendLine($"- [ ] {stepCounter}. 모든 Step이 통합된 최종 Job 파이프라인 조립 및 예외/트랜잭션 롤백 처리 보완");
                todoSb.AppendLine($"- [ ] {stepCounter + 1}. 최종 Job 파이프라인 End-to-End 빌드 및 정적 검증(ArchUnit) 통과 확인");
                await File.WriteAllTextAsync(todoPath, todoSb.ToString(), Encoding.UTF8);
                Log.Debug("통합 마이그레이션 Todo 파일 쓰기 성공: {TodoPath}", todoPath);

                try
                {
                    var agentSrcFolder = Path.Combine(agentFolder, "src");
                    if (!Directory.Exists(agentSrcFolder))
                    {
                        Directory.CreateDirectory(agentSrcFolder);
                    }

                    if (targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseClassStub = @"using System;
using System.Data;

namespace ReSet.Batch.Core
{
    public interface ISettleStep
    {
        string StepName { get; }
        StepResult Execute(SettleContext context);
    }

    public abstract class AbstractSettleTasklet : ISettleStep
    {
        public abstract string StepName { get; }
        protected abstract string SourceProcName { get; }

        public StepResult Execute(SettleContext context)
        {
            if (context.Checkpoint?.IsStepCompleted(StepName, context.Ymd) == true)
            {
                return new StepResult { Code = 0, Message = ""이미 완료된 Step 재시작 스킵"", SourceProcName = SourceProcName };
            }

            int stateCode = 0;
            using var conn = context.MainDb.CreateConnection();
            conn.Open();
            using (var cmdIso = conn.CreateCommand())
            {
                cmdIso.CommandText = ""SET XACT_ABORT ON; SET TRANSACTION ISOLATION LEVEL SNAPSHOT;"";
                cmdIso.ExecuteNonQuery();
            }

            try
            {
                var preCheckFail = PreCheck(conn, context, ref stateCode);
                if (preCheckFail != null) return preCheckFail;

                using var tran = conn.BeginTransaction();
                try
                {
                    RunBusinessSteps(conn, tran, context, ref stateCode);
                    tran.Commit();
                    context.Checkpoint.MarkStepCompleted(StepName, context.Ymd);
                    return new StepResult { Code = 0, Message = ""정상 완료"", SourceProcName = SourceProcName };
                }
                catch
                {
                    if (tran.Connection != null) tran.Rollback();
                    OnFailureCompensation(context, stateCode);
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new StepResult { Code = stateCode, Message = ex.Message, SourceProcName = SourceProcName };
            }
        }

        protected abstract StepResult PreCheck(IDbConnection conn, SettleContext context, ref int stateCode);
        protected abstract void RunBusinessSteps(IDbConnection conn, IDbTransaction tran, SettleContext context, ref int stateCode);
        protected virtual void OnFailureCompensation(SettleContext context, int failedStateCode) { }
    }

    public class SettleContext
    {
        public string Ymd { get; set; }
        public bool BypassPreCheck { get; set; }
        public IDbConnectionFactory MainDb { get; set; }
        public IDbConnectionFactory PaymentDb { get; set; }
        public IDbConnectionFactory SettleCardDb { get; set; }
        public IDbConnectionFactory PlCardDb { get; set; }
        public ICheckpointRepository Checkpoint { get; set; }
    }

    public class StepResult
    {
        public int Code { get; set; }
        public string Message { get; set; }
        public string SourceProcName { get; set; }
        public string PoStrErrMsg { get; set; }
        public bool IsSuccess => Code == 0;
    }

    public interface IDbConnectionFactory { IDbConnection CreateConnection(); }
    public interface ICheckpointRepository 
    { 
        bool IsStepCompleted(string stepName, string ymd);
        void MarkStepCompleted(string stepName, string ymd);
    }
}";
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "AbstractSettleTasklet.cs"), baseClassStub, Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "AbstractSettleTasklet.cs 템플릿 생성 중 오류가 발생했습니다. 진행은 계속합니다.");
                }

                // 테스트 뼈대 및 NetArchTest 더미 생성
                var agentTestsFolder = Path.Combine(agentFolder, "tests");
                if (!Directory.Exists(agentTestsFolder))
                {
                    Directory.CreateDirectory(agentTestsFolder);
                }
                if (targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase))
                {
                    var xUnitStub = @"using Xunit;
using Moq;
using System.Threading.Tasks;

namespace ReSet.Batch.Tests
{
    public class StepLogicTests
    {
        [Fact]
        public async Task Step_ShouldExecuteDml_WhenPreCheckPasses()
        {
            // Arrange
            
            // Act
            
            // Assert
        }
    }
}";
                    var archUnitStub = @"using NetArchTest.Rules;
using Xunit;

namespace ReSet.Batch.Tests.Architecture
{
    public class ArchitectureTests
    {
        [Fact]
        public void DomainLayer_ShouldNotDependOn_InfrastructureLayer()
        {
            // Arrange
            // var result = Types.InCurrentDomain()
            //     .That().ResideInNamespace(""ReSet.Batch.Domain"")
            //     .ShouldNot().HaveDependencyOn(""ReSet.Batch.Infrastructure"")
            //     .GetResult();
            
            // Assert
            // Assert.True(result.IsSuccessful);
        }
    }
}";
                    await File.WriteAllTextAsync(Path.Combine(agentTestsFolder, "StepLogicTests.cs"), xUnitStub, Encoding.UTF8);
                    await File.WriteAllTextAsync(Path.Combine(agentTestsFolder, "ArchitectureTests.cs"), archUnitStub, Encoding.UTF8);
                }
                else if (targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase))
                {
                    var jUnitStub = @"package com.reset.batch.tests;

import org.junit.jupiter.api.Test;
import org.mockito.Mock;
import static org.mockito.Mockito.*;

public class StepLogicTests {
    @Test
    public void step_ShouldExecuteDml_WhenPreCheckPasses() {
        // Arrange
        
        // Act
        
        // Assert
    }
}";
                    var archUnitStub = @"package com.reset.batch.tests.architecture;

import com.tngtech.archunit.junit.AnalyzeClasses;
import com.tngtech.archunit.junit.ArchTest;
import com.tngtech.archunit.lang.ArchRule;
import static com.tngtech.archunit.lang.syntax.ArchRuleDefinition.classes;

@AnalyzeClasses(packages = ""com.reset.batch"")
public class ArchitectureTests {
    @ArchTest
    public static final ArchRule domainLayer_ShouldNotDependOn_InfrastructureLayer = 
        classes()
            .that().resideInAPackage(""..domain.."")
            .should().onlyDependOnClassesThat().resideInAnyPackage(""..domain.."", ""java.."");
}";
                    await File.WriteAllTextAsync(Path.Combine(agentTestsFolder, "StepLogicTests.java"), jUnitStub, Encoding.UTF8);
                    await File.WriteAllTextAsync(Path.Combine(agentTestsFolder, "ArchitectureTests.java"), archUnitStub, Encoding.UTF8);
                }

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

        public async Task AppendFeedbackToInstructionsAsync(string instructionsFilePath, string feedbackMarkdown, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(instructionsFilePath))
            {
                Log.Warning("지시서 파일이 존재하지 않아 피드백을 추가할 수 없습니다 - Path: {Path}", instructionsFilePath);
                return;
            }

            try
            {
                var content = await File.ReadAllTextAsync(instructionsFilePath, Encoding.UTF8, cancellationToken);

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

                await File.WriteAllTextAsync(instructionsFilePath, sb.ToString(), Encoding.UTF8, cancellationToken);
                Log.Information("지시서에 L1/L2 검증 피드백 영역을 업데이트 완료 - Path: {Path}", instructionsFilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "지시서 피드백 추가 중 오류 발생 - Path: {Path}", instructionsFilePath);
            }
        }
    }
}
