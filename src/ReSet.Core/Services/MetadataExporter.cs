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

                // 지시서 번들이 참조 테이블 스키마를 만들 때 쓰는 원천이다.
                // 매니페스트와 같은 디렉터리에 두어야 Spec.md 경로에서 규칙적으로 찾을 수 있다.
                var metadataPath = Path.Combine(
                    Path.GetDirectoryName(manifestPath)!,
                    "metadata.json");
                await File.WriteAllTextAsync(
                    metadataPath,
                    JsonSerializer.Serialize(definition, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8,
                    cancellationToken);
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

        public async Task<BundleResult> ExportConsolidatedMigrationInstructionsAsync(
            System.Collections.Generic.List<SpDefinition> spDefs,
            string consolidatedPlan,
            VerificationOutcome planOutcome,
            string jobName,
            string baseOutputDir,
            string targetLanguage,
            OutputPathResolver paths,
            PlanLayout? layout = null,
            CancellationToken cancellationToken = default)
        {
            Log.Information("통합 마이그레이션 지시서 번들 내보내기 시작 - JobName: {JobName}, OutputDir: {OutputDir}",
                jobName, baseOutputDir);

            // 아래 번들 쓰기와 진행 상태 저장은 일부러 try/catch로 감싸지 않는다. 옛
            // 메서드는 전체를 삼키고 로그만 남겼지만, 그 결과 지시서가 없거나 절반만
            // 쓰인 채로 "성공"이라고 호출자에게 보고되는 일이 있었다. 지시서를 못
            // 쓴 Job은 애초에 코딩 에이전트에게 넘길 수 없으므로, 예외를 삼켜 계속
            // 진행하는 것보다 여기서 그대로 올려 호출자가 실패를 알게 하는 편이
            // 낫다. 이후의 AbstractSettleTasklet 스텁 배치 블록만 격리된 채로 남아
            // 있는 것은 그것이 부가 산출물(스캐폴딩 예시 코드)이라 실패해도 Job
            // 자체는 여전히 쓸 수 있기 때문이다 - 여기 위쪽을 다시 감싸고 싶어지면
            // 그 차이를 먼저 확인할 것.
            var bundle = await new InstructionBundleWriter().WriteAsync(
                new BundleInputs(
                    jobName, targetLanguage, planOutcome, consolidatedPlan,
                    layout, spDefs, paths, baseOutputDir),
                cancellationToken);

            var agentFolder = Path.Combine(baseOutputDir, "agent");

            // 회차 목록은 번들이 실제로 쓴 task 파일에서 나온다. 두 곳이 각자
            // 회차를 세면 progress.json이 존재하지 않는 회차를 가리킬 수 있다.
            // 식별자·회차 종류 판별은 TaskFileComposer.ParseStageIdentity 하나로
            // 모아 둔다 - CodegenStagePlan(Task 12)도 같은 것을 쓴다.
            var stages = bundle.TaskFilePaths
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Select(name =>
                {
                    var identity = TaskFileComposer.ParseStageIdentity(name);
                    return new StageProgress(
                        Id: identity.Id,
                        StepCode: identity.StepCode,
                        TaskFileName: name + ".md",
                        Status: StageStatus.Pending,
                        Attempts: 0,
                        LastGapSummary: null);
                })
                .ToList();

            await AgentProgressStore.Create(agentFolder, jobName, stages).SaveAsync(cancellationToken);

            foreach (var warning in bundle.Warnings)
            {
                Log.Warning("지시서 번들 경고 - {Warning}", warning);
            }

            // (기존 AbstractSettleTasklet 스텁 배치 블록은 여기 이어서 그대로 둔다.)
            // agentSrcFolder는 경로 문자열 계산일 뿐이라 실패하지 않는다 - 디렉터리 생성과
            // 베이스 클래스 스텁 쓰기만 안쪽 try로 묶어, 그 실패가 tests/ 스텁 배치까지
            // 막지 않게 한다(디렉터리 생성 실패는 흔치 않지만, 실패해도 tests/*.cs는
            // 여전히 나가야 코딩 에이전트가 최소한의 뼈대는 받는다).
            var agentSrcFolder = Path.Combine(agentFolder, "src");

            try
            {
                try
                {
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
[[ORM_BOUNDARY]]
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
                        // 스텁은 System.Data만 참조하는 상태를 유지한다. ORM 패턴은 실행 코드가
                        // 아니라 주석으로만 넣어야 스텁이 특정 ORM 구현에 결합되지 않는다.
                        var stubWithBoundary = baseClassStub.Replace("[[ORM_BOUNDARY]]", DataAccessPolicy.TaskletOrmComment);
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "AbstractSettleTasklet.cs"), stubWithBoundary, Encoding.UTF8);
                    }
                    else if (targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase))
                    {
                        // ArchitectureTests.java(DataAccessPolicy.ArchitectureTestStub)가
                        // com.reset.batch.core.ISettleStep / AbstractSettleTasklet을 클래스
                        // 리터럴로 참조한다. C# 쪽처럼 그 타입을 실제로 내보내지 않으면
                        // javac가 ""cannot find symbol""로 즉시 죽는다 - 아키텍처 테스트가
                        // 아무것도 못 잡는 게 아니라 프로젝트 전체가 컴파일되지 않는다.
                        //
                        // SettleContext/StepResult/IDbConnectionFactory/ICheckpointRepository도
                        // 전부 public 파일로 낸다. 이들은 확장 표면(ISettleStep.execute,
                        // AbstractSettleTasklet의 preCheck/runBusinessSteps/
                        // onFailureCompensation)의 매개변수·반환 타입인데, 에이전트가 만드는
                        // Tasklet은 Hexagonal 레이아웃상 core가 아닌 다른 패키지(예:
                        // com.reset.batch.steps)에 있다. 이 타입들을 package-private으로
                        // 묶으면(C#의 internal 습관) 그 패키지에서는 오버라이드 시그니처
                        // 자체를 적을 수 없어 javac가 ""is not public"" / ""does not override
                        // abstract method""로 죽는다. Java는 파일당 public 최상위 타입
                        // 하나만 허용하므로 6개 파일로 나눈다.
                        var settleStepStub = @"package com.reset.batch.core;

/**
 * 코딩 에이전트가 만드는 모든 Step이 구현해야 하는 최소 계약. 이 인터페이스를 직접
 * 구현하고 AbstractSettleTasklet을 거치지 않으면 아키텍처 테스트가 잡아낸다 - 재시작
 * 스킵, 격리 수준 설정, 트랜잭션 경계 같은 공통 로직이 Step마다 새로 구현되는 것을
 * 막기 위해서다.
 */
public interface ISettleStep {
    String getStepName();
    StepResult execute(SettleContext context);
}
";
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "ISettleStep.java"), settleStepStub, Encoding.UTF8);

                        var settleContextStub = @"package com.reset.batch.core;

/**
 * Step 실행 컨텍스트. Tasklet 서브클래스가 다른 패키지에 있으므로 public이어야 한다 -
 * package-private이면 그 패키지에서 execute/preCheck/runBusinessSteps의 시그니처
 * 자체를 적을 수 없다.
 */
public class SettleContext {
    private String ymd;
    private boolean bypassPreCheck;
    private IDbConnectionFactory mainDb;
    private IDbConnectionFactory paymentDb;
    private IDbConnectionFactory settleCardDb;
    private IDbConnectionFactory plCardDb;
    private ICheckpointRepository checkpoint;

    public String getYmd() { return ymd; }
    public void setYmd(String ymd) { this.ymd = ymd; }
    public boolean isBypassPreCheck() { return bypassPreCheck; }
    public void setBypassPreCheck(boolean bypassPreCheck) { this.bypassPreCheck = bypassPreCheck; }
    public IDbConnectionFactory getMainDb() { return mainDb; }
    public void setMainDb(IDbConnectionFactory mainDb) { this.mainDb = mainDb; }
    public IDbConnectionFactory getPaymentDb() { return paymentDb; }
    public void setPaymentDb(IDbConnectionFactory paymentDb) { this.paymentDb = paymentDb; }
    public IDbConnectionFactory getSettleCardDb() { return settleCardDb; }
    public void setSettleCardDb(IDbConnectionFactory settleCardDb) { this.settleCardDb = settleCardDb; }
    public IDbConnectionFactory getPlCardDb() { return plCardDb; }
    public void setPlCardDb(IDbConnectionFactory plCardDb) { this.plCardDb = plCardDb; }
    public ICheckpointRepository getCheckpoint() { return checkpoint; }
    public void setCheckpoint(ICheckpointRepository checkpoint) { this.checkpoint = checkpoint; }
}
";
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "SettleContext.java"), settleContextStub, Encoding.UTF8);

                        var stepResultStub = @"package com.reset.batch.core;

/**
 * preCheck/runBusinessSteps를 오버라이드하는 Tasklet 서브클래스(다른 패키지)가 이
 * 타입을 직접 생성해 반환해야 하므로 클래스와 생성자 모두 public이다.
 */
public class StepResult {
    private final int code;
    private final String message;
    private final String sourceProcName;
    private String poStrErrMsg;

    public StepResult(int code, String message, String sourceProcName) {
        this.code = code;
        this.message = message;
        this.sourceProcName = sourceProcName;
    }

    public int getCode() { return code; }
    public String getMessage() { return message; }
    public String getSourceProcName() { return sourceProcName; }
    public String getPoStrErrMsg() { return poStrErrMsg; }
    public void setPoStrErrMsg(String poStrErrMsg) { this.poStrErrMsg = poStrErrMsg; }
    public boolean isSuccess() { return code == 0; }
}
";
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "StepResult.java"), stepResultStub, Encoding.UTF8);

                        var dbConnectionFactoryStub = @"package com.reset.batch.core;

import java.sql.Connection;
import java.sql.SQLException;

/** 회차 0의 부트스트랩이 DB별로 구현한다(멀티 DB 연결 문자열 설정에서 주입). */
public interface IDbConnectionFactory {
    Connection createConnection() throws SQLException;
}
";
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "IDbConnectionFactory.java"), dbConnectionFactoryStub, Encoding.UTF8);

                        var checkpointRepositoryStub = @"package com.reset.batch.core;

/** 회차 0의 부트스트랩이 구현한다. */
public interface ICheckpointRepository {
    boolean isStepCompleted(String stepName, String ymd);
    void markStepCompleted(String stepName, String ymd);
}
";
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "ICheckpointRepository.java"), checkpointRepositoryStub, Encoding.UTF8);

                        var abstractTaskletStub = @"package com.reset.batch.core;

import java.sql.Connection;
import java.sql.SQLException;
import java.sql.Statement;

/**
 * C# 쪽 AbstractSettleTasklet과 같은 책임을 진다: 재시작 스킵 확인, 격리 수준 설정,
 * 트랜잭션 경계, 실패 시 보상 호출을 여기서 한 번만 구현하고 Step 저자는 preCheck·
 * runBusinessSteps만 채운다.
 *
 * JDBC에는 IDbTransaction에 대응하는 별도 타입이 없다 - Connection의 autoCommit을
 * 끄고 commit()/rollback()으로 경계를 표시하므로, C# 쪽 conn/tran 두 인자가 여기서는
 * Connection 하나로 합쳐진다. ref int stateCode도 Java에는 대응이 없어 out 매개변수
 * 대신 보호된 필드로 옮겼다 - preCheck/runBusinessSteps 구현체가 실패 분류 코드를
 * 남기고 싶으면 setStateCode를 호출한다.
 */
public abstract class AbstractSettleTasklet implements ISettleStep {

    private int stateCode = 0;

    protected abstract String getSourceProcName();

    @Override
    public StepResult execute(SettleContext context) {
        if (context.getCheckpoint() != null
                && context.getCheckpoint().isStepCompleted(getStepName(), context.getYmd())) {
            return new StepResult(0, ""이미 완료된 Step 재시작 스킵"", getSourceProcName());
        }

        try (Connection conn = context.getMainDb().createConnection()) {
            try (Statement isolationStmt = conn.createStatement()) {
                isolationStmt.execute(""SET XACT_ABORT ON; SET TRANSACTION ISOLATION LEVEL SNAPSHOT;"");
            }

            StepResult preCheckFail = preCheck(conn, context);
            if (preCheckFail != null) {
                return preCheckFail;
            }

            conn.setAutoCommit(false);
            try {
                runBusinessSteps(conn, context);
                conn.commit();
                context.getCheckpoint().markStepCompleted(getStepName(), context.getYmd());
                return new StepResult(0, ""정상 완료"", getSourceProcName());
            } catch (Exception ex) {
                conn.rollback();
                onFailureCompensation(context, stateCode);
                throw ex;
            }
        } catch (Exception ex) {
            return new StepResult(stateCode, ex.getMessage(), getSourceProcName());
        }
    }

    /** preCheck/runBusinessSteps 구현체가 실패 분류 코드를 남기고 싶으면 이 메서드로 갱신한다. */
    protected void setStateCode(int stateCode) {
        this.stateCode = stateCode;
    }

    protected abstract StepResult preCheck(Connection conn, SettleContext context) throws SQLException;

[[ORM_BOUNDARY_JAVA]]
    protected abstract void runBusinessSteps(Connection conn, SettleContext context) throws SQLException;

    protected void onFailureCompensation(SettleContext context, int failedStateCode) {
    }
}
";
                        // C# 쪽 TaskletOrmComment와 같은 위치(runBusinessSteps 바로 위)에
                        // JPA 버전 경계 주석을 심는다. DataAccessPolicy.TaskletOrmComment는
                        // EF Core/SqlConnection 전용 C# 구문이라 그대로 재사용할 수 없다 -
                        // 별도의 공유 상수로 뽑을 만큼 이 태스크의 범위가 넓지 않아 여기
                        // 인라인으로 둔다.
                        const string javaOrmBoundaryComment = @"    // [데이터 액세스 경계] ORM(Spring Data JPA)은 MigrationInstructions.md 5장의 허용 목록에
    // 한해 사용한다. 사용할 경우 반드시 이 메서드가 받은 conn에 참여시켜야 하며, 새
    // 커넥션이나 새 트랜잭션을 만들면 검증기의 Rollback 격리가 깨져 정합성 대조 결과가
    // 오염된다. Spring 관리 트랜잭션(JpaTransactionManager)을 쓰더라도 그 트랜잭션이
    // 이 conn 위에서 열려야 한다. 정산 대상 대량 DML, 집계, 청킹 루프, Shadow 처리,
    // 세션 제어는 파라미터 바인딩 SQL(MyBatis)로 작성한다.";
                        var abstractTaskletStubWithBoundary = abstractTaskletStub.Replace("[[ORM_BOUNDARY_JAVA]]", javaOrmBoundaryComment);
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "AbstractSettleTasklet.java"), abstractTaskletStubWithBoundary, Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "AbstractSettleTasklet 템플릿 생성 중 오류가 발생했습니다. 진행은 계속합니다.");
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
                    var archUnitStub = DataAccessPolicy.ArchitectureTestStub(targetLanguage);
                    await File.WriteAllTextAsync(Path.Combine(agentTestsFolder, "StepLogicTests.cs"), xUnitStub, Encoding.UTF8);
                    await File.WriteAllTextAsync(Path.Combine(agentTestsFolder, "ArchitectureTests.cs"), archUnitStub, Encoding.UTF8);

                    // agentSrcFolder 생성이 위 inner try에서 이미 실패해 경고로 삼켜졌을 수
                    // 있다. 그 경우 여기서 다시 시도하고, 그래도 실패하면 이 파일만 건너뛴다 -
                    // 두 tests/*.cs 스텁은 이미 디스크에 쓰였으니 여기서 예외가 outer catch로
                    // 튀어 완료 로그를 삼키게 두지 않는다.
                    try
                    {
                        if (!Directory.Exists(agentSrcFolder))
                        {
                            Directory.CreateDirectory(agentSrcFolder);
                        }
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "SettleContracts.cs"), DataAccessPolicy.RepositoryContractStub(targetLanguage), Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "SettleContracts.cs 템플릿 생성 중 오류가 발생했습니다. 진행은 계속합니다.");
                    }
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
                    var archUnitStub = DataAccessPolicy.ArchitectureTestStub(targetLanguage);
                    await File.WriteAllTextAsync(Path.Combine(agentTestsFolder, "StepLogicTests.java"), jUnitStub, Encoding.UTF8);
                    await File.WriteAllTextAsync(Path.Combine(agentTestsFolder, "ArchitectureTests.java"), archUnitStub, Encoding.UTF8);

                    // C# 쪽과 같은 이유로 격리한다 - src/ 생성 실패가 이미 쓰인 tests/*.java를
                    // 무의미하게 만들며 outer catch로 튀어 완료 로그를 삼키게 두지 않는다.
                    try
                    {
                        if (!Directory.Exists(agentSrcFolder))
                        {
                            Directory.CreateDirectory(agentSrcFolder);
                        }
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "ISettleStepDescriptor.java"), DataAccessPolicy.RepositoryContractStub(targetLanguage), Encoding.UTF8);
                        await File.WriteAllTextAsync(Path.Combine(agentSrcFolder, "ISettleRepository.java"), DataAccessPolicy.JavaRepositoryInterfaceStub, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "계약 스텁(ISettleStepDescriptor.java/ISettleRepository.java) 생성 중 오류가 발생했습니다. 진행은 계속합니다.");
                    }
                }

                Log.Information("통합 마이그레이션 지시서 번들 내보내기 완료 - JobName: {JobName}", jobName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "통합 마이그레이션 지시서 내보내기 중 예외 발생 (격리됨) - JobName: {JobName}", jobName);
            }

            return bundle;
        }

        /// <summary>
        /// InstructionBundleWriter가 같은 스키마 표를 써야 한다. 표 형식이 두 벌이 되면
        /// 지시서와 다른 산출물이 같은 테이블을 다르게 보여준다.
        /// </summary>
        internal static string FormatTableSchemaToMarkdown(DependencyInfo dep)
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
