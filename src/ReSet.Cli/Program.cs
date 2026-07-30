using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Serilog;
using ReSet.Validator.Core.Services;
using ReSet.Validator.Core.Plugins;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Abstractions;

namespace ReSet.Cli
{
    public class Program
    {
        private static CancellationTokenSource? _currentCts;

        public static CliArgs ParseCommandLineArgs(string[] args)
        {
            var cliArgs = new CliArgs();
            cliArgs.ConnectionString = Environment.GetEnvironmentVariable("SP_ANALYZER_CONN_STR");

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg.Equals("--conn", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cliArgs.ConnectionString = args[++i];
                }
                else if (arg.Equals("--all", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.AnalyzeAll = true;
                }
                else if (arg.Equals("--sp", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var sps = args[++i].Split(',');
                    foreach (var sp in sps)
                    {
                        var trimmed = sp.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            cliArgs.TargetProcedures.Add(trimmed);
                        }
                    }
                }
                else if (arg.Equals("--codegen", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.EnableCodegen = true;
                }
                else if (arg.Equals("--engine", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cliArgs.Engine = args[++i];
                }
                else if (arg.Equals("--job-name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cliArgs.JobName = args[++i];
                }
                else if (arg.Equals("--policy", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.GeneratePolicy = true;
                }
                else if (arg.Equals("--policy-sps", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var sps = args[++i].Split(',');
                    foreach (var sp in sps)
                    {
                        var trimmed = sp.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            cliArgs.PolicyProcedures.Add(trimmed);
                        }
                    }
                }
                else if (arg.Equals("--extract-snapshot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cliArgs.ExtractSnapshotPath = args[++i];
                }
            }

            return cliArgs;
        }

        static async Task Main(string[] args)
        {
            try
            {
            // 0. Ctrl+C 이벤트 바인딩 (활성화된 CancellationTokenSource를 취소하도록 함)
            Console.CancelKeyPress += (sender, e) =>
            {
                if (_currentCts != null && !_currentCts.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine("\n[red]사용자에 의해 작업 취소 요청이 발생했습니다. 안전하게 정리 중...[/]");
                    _currentCts.Cancel();
                    e.Cancel = true; // 프로세스 즉시 종료 방지 및 OperationCanceledException 유도
                }
            };

            // 초기 전역 CancellationTokenSource 생성
            using var globalCts = new CancellationTokenSource();
            _currentCts = globalCts;

            // 1. 커맨드라인 아규먼트 및 환경 변수 파싱
            var cliArgs = ParseCommandLineArgs(args);

            // 2. 설정 로드
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // 2.5 로깅 초기화
            ConfigureLogging(configuration);

            // 세션에서 이전 연결 정보 복원
            var session = SessionManager.LoadSession();
            var server = !string.IsNullOrEmpty(session.LastUsedServer) ? session.LastUsedServer : (configuration["DatabaseSettings:Server"] ?? "localhost");
            var database = !string.IsNullOrEmpty(session.LastUsedDatabase) ? session.LastUsedDatabase : (configuration["DatabaseSettings:Database"] ?? "master");

            // 3. 서비스 구성 변수 준비
            var provider = configuration["AiSettings:Provider"] ?? "OpenAI";
            var modelName = configuration["AiSettings:ModelName"] ?? "gpt-4o";
            
            // 프로바이더별 ApiKey와 Endpoint 로드
            var apiKey = configuration[$"AiSettings:Providers:{provider}:ApiKey"] ?? string.Empty;
            var endpoint = configuration[$"AiSettings:Providers:{provider}:Endpoint"] ?? string.Empty;
            
            var tempStr = configuration["AiSettings:Temperature"] ?? "0.2";
            float.TryParse(tempStr, out float temp);

            var depthStr = configuration["DatabaseSettings:MaxDependencyDepth"] ?? "3";
            int.TryParse(depthStr, out int maxDepth);

            var outputDir = configuration["OutputSettings:Directory"] ?? "./output";
            if (!Path.IsPathRooted(outputDir))
            {
                outputDir = Path.Combine(Directory.GetCurrentDirectory(), outputDir);
            }

            var instructionsFile = configuration["OutputSettings:InstructionsFile"] ?? "instructions.md";
            if (!Path.IsPathRooted(instructionsFile))
            {
                instructionsFile = Path.Combine(Directory.GetCurrentDirectory(), instructionsFile);
            }

            bool.TryParse(configuration["OutputSettings:SaveRawJson"] ?? "false", out bool saveRawJson);
            bool.TryParse(configuration["OutputSettings:SaveRawContext"] ?? "false", out bool saveRawContext);
            bool.TryParse(configuration["OutputSettings:SaveRawFiles"] ?? "false", out bool saveRawFiles);
            bool.TryParse(configuration["OutputSettings:EnableCache"] ?? "false", out bool enableCache);

            bool.TryParse(configuration["MigrationSettings:Enabled"] ?? "true", out bool migrationEnabled);
            var targetLanguage = configuration["MigrationSettings:TargetLanguage"] ?? "C#";

            bool.TryParse(configuration["CodegenSettings:Enabled"] ?? "false", out bool codegenEnabled);
            var codegenEngine = configuration["CodegenSettings:Engine"] ?? "claude";

            var isCodegenEnabled = cliArgs.EnableCodegen || codegenEnabled;
            var selectedEngine = cliArgs.Engine ?? codegenEngine;

            string connectionString = string.Empty;
            string? userId = null;

            bool connectionSuccess = false;

            var offlinePath = configuration["DatabaseSettings:OfflineSnapshotPath"];
            bool isOfflineMode = !string.IsNullOrWhiteSpace(offlinePath);
            IDbMetadataService dbService;

            if (isOfflineMode)
            {
                if (!File.Exists(offlinePath))
                {
                    AnsiConsole.MarkupLine($"[red]에러: 설정된 오프라인 스냅샷 파일('{offlinePath}')을 찾을 수 없습니다. 경로를 확인해주세요.[/]");
                    return;
                }
                AnsiConsole.MarkupLine($"[blue]오프라인 모드로 동작합니다. 스냅샷 로드 중: {offlinePath}[/]");
                var snapshot = await SnapshotManager.ImportSnapshotAsync(offlinePath!, globalCts.Token);
                dbService = new OfflineDbMetadataService(snapshot);
            }
            else
            {
                if (cliArgs.IsBatchMode)
                {
                    // 배치 모드
                    AnsiConsole.MarkupLine("[bold blue]=== 배치 모드 자동 분석 시작 ===[/]");
                    if (string.IsNullOrEmpty(cliArgs.ConnectionString))
                    {
                        AnsiConsole.MarkupLine("[red]에러: 배치 모드 실행 시 연결 문자열(--conn 또는 SP_ANALYZER_CONN_STR 환경 변수)은 필수입니다.[/]");
                        return;
                    }
                    connectionString = cliArgs.ConnectionString;

                    // 연결 테스트
                    await AnsiConsole.Status()
                        .StartAsync("데이터베이스 연결 시도 중...", async ctx =>
                        {
                            try
                            {
                                using (var conn = new SqlConnection(connectionString))
                                {
                                    await conn.OpenAsync(globalCts.Token);
                                    connectionSuccess = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.WriteException(ex);
                            }
                        });

                    if (!connectionSuccess)
                    {
                        AnsiConsole.MarkupLine("[red]데이터베이스 연결에 실패하였습니다. 종료합니다.[/]");
                        return;
                    }
                }
                else
                {
                    // 대화형 TUI 모드 - 로그인 성공 시까지 루프
                    while (true)
                    {
                        AnsiConsole.Clear();
                        AnsiConsole.Write(new FigletText("ReSet Analyzer").Color(Color.Green));
                        AnsiConsole.MarkupLine("[bold green]=== REverse engineering SETtlement Analyzer ===[/]");
                        AnsiConsole.WriteLine();

                        // 대화형 DB 서버 및 이름 변경 지원
                        server = AnsiConsole.Prompt(
                            new TextPrompt<string>("DB 서버 주소를 입력하세요:")
                                .DefaultValue(server)
                        );

                        database = AnsiConsole.Prompt(
                            new TextPrompt<string>("데이터베이스 이름을 입력하세요:")
                                .DefaultValue(database)
                        );

                        // 대화형 ID/비밀번호 로그인 처리
                        var lastUserId = SessionManager.LoadLastUsedUserId();
                        userId = AnsiConsole.Prompt(
                            new TextPrompt<string>("DB 계정을 입력하세요:")
                                .DefaultValue(string.IsNullOrEmpty(lastUserId) ? "sa" : lastUserId)
                        );

                        var password = AnsiConsole.Prompt(
                            new TextPrompt<string>("DB 비밀번호를 입력하세요:")
                                .Secret()
                        );

                        // Connection String 빌드
                        var connStrBuilder = new SqlConnectionStringBuilder
                        {
                            DataSource = server,
                            InitialCatalog = database,
                            UserID = userId,
                            Password = password,
                            TrustServerCertificate = true,
                            ConnectTimeout = 5
                        };
                        connectionString = connStrBuilder.ConnectionString;

                        // 연결 테스트
                        string? loginError = null;
                        await AnsiConsole.Status()
                            .StartAsync("데이터베이스 연결 시도 중...", async ctx =>
                            {
                                try
                                {
                                    using (var conn = new SqlConnection(connectionString))
                                    {
                                        await conn.OpenAsync(globalCts.Token);
                                        connectionSuccess = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    loginError = ex.Message;
                                }
                            });

                        if (connectionSuccess)
                        {
                            if (userId != null && server != null && database != null)
                            {
                                SessionManager.SaveSession(userId, server, database);
                            }
                            break;
                        }

                        AnsiConsole.MarkupLine("[red]로그인에 실패하였습니다. 계정 정보 또는 비밀번호를 확인해 주세요.[/]");
                        if (!string.IsNullOrEmpty(loginError))
                        {
                            AnsiConsole.MarkupLine($"[grey](오류 상세: {Markup.Escape(loginError)})[/]");
                        }
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 로그인 화면으로 돌아갑니다...[/]");
                        Console.ReadKey(true);
                    }
                }

                AnsiConsole.MarkupLine("[green]데이터베이스 연결 성공![/]");
                dbService = new DbMetadataService();

                // 추출 모드(Extract Snapshot) 처리
                if (!string.IsNullOrEmpty(cliArgs.ExtractSnapshotPath))
                {
                    AnsiConsole.MarkupLine("[yellow]오프라인 스냅샷 추출 모드를 시작합니다...[/]");
                    using (var progressScope = new ConsoleProgressScope("스냅샷 추출"))
                    {
                        await SnapshotManager.ExportSnapshotAsync(dbService, connectionString, cliArgs.ExtractSnapshotPath, maxDepth, progressScope, globalCts.Token);
                    }
                    
                    AnsiConsole.MarkupLine("[green]스냅샷 추출이 완료되었습니다. 프로그램을 종료합니다.[/]");
                    return;
                }
            }
            var timeoutSeconds = 300;
            if (int.TryParse(configuration["AiSettings:TimeoutSeconds"], out int parsedTimeout) && parsedTimeout > 0)
            {
                timeoutSeconds = parsedTimeout;
            }
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

            // Provider별 추가 옵션 로드
            int? numCtx = null;
            if (int.TryParse(configuration[$"AiSettings:Providers:{provider}:NumCtx"], out int parsedNumCtx) && parsedNumCtx > 0)
            {
                numCtx = parsedNumCtx;
            }
            bool.TryParse(configuration[$"AiSettings:Providers:{provider}:EnableThinking"] ?? "false", out bool enableOllamaThinking);

            // Critic Threshold Score 로드
            int criticThresholdScore = 8;
            if (int.TryParse(configuration["AiSettings:Critic:ThresholdScore"], out int parsedThresholdScore) && parsedThresholdScore >= 0)
            {
                criticThresholdScore = parsedThresholdScore;
            }

            // Local Chunking 활성화 여부
            bool.TryParse(configuration["AiSettings:EnableLocalChunking"] ?? "true", out bool enableLocalChunking);

            IAiClient aiClient = ReSet.Core.Services.Clients.AiClientFactory.CreateClient(provider, modelName, apiKey, endpoint, httpClient, numCtx);
            IAiService aiService = new AiService(aiClient, temp, enableOllamaThinking, criticThresholdScore, enableLocalChunking);

            // 하이브리드 아키텍처: ActorEffort 파싱
            var actorEffort = configuration["AiSettings:ActorEffort"];

            // 하이브리드 아키텍처: Critic 서비스 구성
            IAiService criticService = aiService;
            var criticEffort = configuration["AiSettings:Critic:Effort"];
            var criticProvider = configuration["AiSettings:Critic:Provider"] ?? provider;
            var criticModel = configuration["AiSettings:Critic:ModelName"] ?? modelName;
            if (configuration["AiSettings:Critic:Provider"] != null || configuration["AiSettings:Critic:ModelName"] != null)
            {
                var criticApiKey = configuration[$"AiSettings:Providers:{criticProvider}:ApiKey"] ?? string.Empty;
                var criticEndpoint = configuration[$"AiSettings:Providers:{criticProvider}:Endpoint"] ?? string.Empty;

                int? criticNumCtx = null;
                if (int.TryParse(configuration[$"AiSettings:Providers:{criticProvider}:NumCtx"], out int parsedCriticNumCtx) && parsedCriticNumCtx > 0)
                {
                    criticNumCtx = parsedCriticNumCtx;
                }
                bool.TryParse(configuration[$"AiSettings:Providers:{criticProvider}:EnableThinking"] ?? "false", out bool criticEnableThinking);

                var criticClient = ReSet.Core.Services.Clients.AiClientFactory.CreateClient(criticProvider, criticModel, criticApiKey, criticEndpoint, httpClient, criticNumCtx);
                criticService = new AiService(criticClient, temp, criticEnableThinking, criticThresholdScore, enableLocalChunking);
            }

            // 하이브리드 아키텍처: Consolidator 서비스 구성
            IAiService consolidatorService = aiService;
            var consolidatorEffort = configuration["AiSettings:Consolidator:Effort"];
            var consolidatorProvider = configuration["AiSettings:Consolidator:Provider"] ?? provider;
            var consolidatorModel = configuration["AiSettings:Consolidator:ModelName"] ?? modelName;
            if (configuration["AiSettings:Consolidator:Provider"] != null || configuration["AiSettings:Consolidator:ModelName"] != null)
            {
                var consolidatorApiKey = configuration[$"AiSettings:Providers:{consolidatorProvider}:ApiKey"] ?? string.Empty;
                var consolidatorEndpoint = configuration[$"AiSettings:Providers:{consolidatorProvider}:Endpoint"] ?? string.Empty;

                int? consolidatorNumCtx = null;
                if (int.TryParse(configuration[$"AiSettings:Providers:{consolidatorProvider}:NumCtx"], out int parsedConsolNumCtx) && parsedConsolNumCtx > 0)
                {
                    consolidatorNumCtx = parsedConsolNumCtx;
                }
                bool.TryParse(configuration[$"AiSettings:Providers:{consolidatorProvider}:EnableThinking"] ?? "false", out bool consolidatorEnableThinking);

                var consolidatorClient = ReSet.Core.Services.Clients.AiClientFactory.CreateClient(consolidatorProvider, consolidatorModel, consolidatorApiKey, consolidatorEndpoint, httpClient, consolidatorNumCtx);
                consolidatorService = new AiService(consolidatorClient, temp, consolidatorEnableThinking, criticThresholdScore, enableLocalChunking);
            }

            IMetadataExporter metadataExporter = new MetadataExporter();
            bool.TryParse(configuration["ValidationSettings:UseMermaidCli"] ?? "false", out bool useMermaidCli);
            var validator = new MechanicalValidator(useMermaidCli);
            var userInteraction = new ConsoleUserInteraction();
            var maxL2Attempts = configuration["AiSettings:MaxL2Attempts"] ?? "1";
            
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, 
                aiService, 
                validator, 
                userInteraction, 
                maxL2Attempts, 
                modelName, 
                null,
                criticService,
                consolidatorService,
                actorEffort,
                criticEffort,
                consolidatorEffort,
                criticThresholdScore
            );
            ISettlementPolicyService policyService = new SettlementPolicyService(dbService, aiService);

            string instructions = "기본 마크다운 규칙을 적용하여 분석해 주세요.";
            if (File.Exists(instructionsFile))
            {
                instructions = await File.ReadAllTextAsync(instructionsFile);
            }

            // 5. Stored Procedure 목록 로드
            List<string> spNames = new();
            await AnsiConsole.Status()
                .StartAsync("Stored Procedure 목록 로드 중...", async ctx =>
                {
                    try
                    {
                        spNames = await dbService.GetStoredProcedureNamesAsync(connectionString, globalCts.Token);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine("[red]SP 목록 조회 중 오류 발생:[/]");
                        AnsiConsole.WriteException(ex);
                    }
                });

            if (spNames.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]조회된 Stored Procedure가 없습니다. 종료합니다.[/]");
                return;
            }

            if (cliArgs.IsBatchMode)
            {
                if (cliArgs.GeneratePolicy)
                {
                    AnsiConsole.MarkupLine("[bold blue]=== 정산 정책 문서 자동 도출 배치 프로세스 시작 ===[/]");
                    var policyTargetSps = new List<string>();
                    if (cliArgs.PolicyProcedures.Count > 0)
                    {
                        foreach (var target in cliArgs.PolicyProcedures)
                        {
                            string? matchedSp = null;
                            if (target.Contains('.'))
                            {
                                matchedSp = spNames.Find(x => x.Equals(target, StringComparison.OrdinalIgnoreCase));
                            }
                            else
                            {
                                matchedSp = spNames.Find(x =>
                                {
                                    var parts = x.Split('.', 2);
                                    var nameOnly = parts.Length > 1 ? parts[1] : parts[0];
                                    return nameOnly.Equals(target, StringComparison.OrdinalIgnoreCase);
                                });
                            }

                            if (matchedSp != null)
                            {
                                policyTargetSps.Add(matchedSp);
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"[yellow]경고: 입력된 SP '{target}'를 DB에서 찾을 수 없어 건너뜁니다.[/]");
                            }
                        }
                    }
                    else
                    {
                        policyTargetSps.AddRange(spNames);
                    }

                    if (policyTargetSps.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]정책 분석 대상 Stored Procedure가 없습니다. 종료합니다.[/]");
                        return;
                    }

                    AnsiConsole.MarkupLine($"[bold blue]총 {policyTargetSps.Count}개의 Stored Procedure에 대해 정산 정책 분석 시작...[/]");
                    
                    try
                    {
                        string? rulebook = null;
                        await AnsiConsole.Status()
                            .StartAsync("정산 정책 문서 생성 중...", async ctx =>
                            {
                                rulebook = await policyService.GenerateSettlementPolicyRulebookAsync(connectionString, policyTargetSps, maxDepth, globalCts.Token);
                            });

                        if (string.IsNullOrEmpty(rulebook))
                        {
                            throw new Exception("정산 정책 문서 생성 실패");
                        }

                        if (!Directory.Exists(outputDir))
                        {
                            Directory.CreateDirectory(outputDir);
                        }

                        var rulebookName = string.IsNullOrEmpty(cliArgs.JobName) ? "Settlement_Policy_Rulebook.md" : $"{cliArgs.JobName}_Settlement_Policy_Rulebook.md";
                        var rulebookPath = Path.Combine(outputDir, rulebookName);
                        var effortSuffix = string.IsNullOrWhiteSpace(actorEffort) ? "" : $", Effort: {actorEffort}";
                        var metadataHeader = $"> [!NOTE]\n> **문서 작성일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n\n";

                        await File.WriteAllTextAsync(rulebookPath, metadataHeader + rulebook);
                        AnsiConsole.MarkupLine($"[green]성공: 정산 정책 문서 생성 완료![/] {Markup.Escape(rulebookPath)}");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]에러: 정산 정책 문서 도출 실패:[/] {Markup.Escape(ex.Message)}");
                    }
                    return;
                }

                // 배치 모드 실행 흐름
                List<string> targetSps = new();
                if (cliArgs.AnalyzeAll)
                {
                    targetSps.AddRange(spNames);
                }
                else
                {
                    foreach (var target in cliArgs.TargetProcedures)
                    {
                        string? matchedSp = null;
                        if (target.Contains('.'))
                        {
                            matchedSp = spNames.Find(x => x.Equals(target, StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            matchedSp = spNames.Find(x =>
                            {
                                var parts = x.Split('.', 2);
                                var nameOnly = parts.Length > 1 ? parts[1] : parts[0];
                                return nameOnly.Equals(target, StringComparison.OrdinalIgnoreCase);
                            });
                        }

                        if (matchedSp != null)
                        {
                            targetSps.Add(matchedSp);
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[yellow]경고: 입력된 SP '{target}'를 DB에서 찾을 수 없어 건너뜁니다.[/]");
                        }
                    }
                }

                if (targetSps.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]분석 대상 Stored Procedure가 없습니다. 종료합니다.[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[bold blue]총 {targetSps.Count}개의 Stored Procedure 분석 시작...[/]");

                var specsData = new List<(string FileName, string Content)>();
                var spDefs = new List<SpDefinition>();

                foreach (var selectedOption in targetSps)
                {
                    // 각 SP 처리 단위 예외 격리
                    try
                    {
                        var parts = selectedOption.Split('.', 2);
                        var schema = parts[0];
                        var name = parts[1];

                        var (specMarkdown, spDef, reviewResult, thinkingText) = await orchestrator.RunPipelineAsync(
                            connectionString, schema, name, maxDepth, provider, instructions, isBatchMode: true, outputDir, enableCache, globalCts.Token);

                        if (string.IsNullOrEmpty(specMarkdown))
                        {
                            throw new Exception("검증 파이프라인을 통과한 명세서 획득 실패");
                        }

                        // 수집된 사양서 데이터를 메모리에 보관
                        var specFileName = "docs/Spec.md";
                        specsData.Add((specFileName, specMarkdown));
                        if (spDef != null)
                        {
                            spDefs.Add(spDef);
                        }

                        string? migrationPlan = null;
                        if (migrationEnabled && spDef != null)
                        {
                            AnsiConsole.MarkupLine($"[yellow]{schema}.{name}[/] - 배치 전환 계획 설계서 작성 중 ({targetLanguage})...");
                            var migrationResult = await aiService.GenerateBatchMigrationPlanAsync(spDef, targetLanguage, globalCts.Token);
                            migrationPlan = migrationResult.Content;
                            if (!string.IsNullOrWhiteSpace(migrationResult.ThinkingText))
                            {
                                thinkingText = (thinkingText ?? "") + "\n=== Batch Migration Plan Thinking ===\n" + migrationResult.ThinkingText + "\n";
                            }
                        }

                        if (!Directory.Exists(outputDir))
                        {
                            Directory.CreateDirectory(outputDir);
                        }

                        await SaveOutputsAsync(
                            spDef, specMarkdown, migrationPlan, outputDir, instructionsFile,
                            metadataExporter, saveRawJson, saveRawContext, saveRawFiles,
                            schema, name, provider, modelName, reviewResult, thinkingText, actorEffort, configuration);

                        AnsiConsole.MarkupLine($"[green]성공:[/] {selectedOption} 분석 완료 및 저장!");
                    }
                    catch (OperationCanceledException)
                    {
                        AnsiConsole.MarkupLine("\n[red]사용자에 의해 배치 분석 작업이 중단되었습니다. 프로세스를 종료합니다.[/]");
                        break;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]실패:[/] {selectedOption} 분석 중 오류 발생: {ex.Message}");
                    }
                }

                AnsiConsole.MarkupLine("[bold green]=== 배치 모드 자동 분석 완료 ===[/]");

                // 배치 통합 배치 전환 계획 자동 수립 실행
                if (!string.IsNullOrEmpty(cliArgs.JobName) && specsData.Count > 0)
                {
                    AnsiConsole.MarkupLine($"\n[bold blue]=== 배치 통합 배치 전환 계획 수립 시작 ({cliArgs.JobName}) ===[/]");
                    using var activeCts = new CancellationTokenSource();
                    _currentCts = activeCts;

                    try
                    {
                        var pipelineResult = await orchestrator.RunConsolidatedPipelineAsync(specsData, targetLanguage, cliArgs.JobName, provider, isBatchMode: true, activeCts.Token);
                        var consolidatedPlan = pipelineResult.Plan;
                        var aiResult = pipelineResult.Result;
                        if (string.IsNullOrEmpty(consolidatedPlan))
                        {
                            AnsiConsole.MarkupLine("[red]에러: 통합 배치 설계서 작성이 중단되었거나 실패했습니다.[/]");
                        }
                        else
                        {
                            var jobsOutputDir = Path.Combine(outputDir, "Jobs", cliArgs.JobName);
                            var docsDir = Path.Combine(jobsOutputDir, "docs");
                            var rawDir = Path.Combine(jobsOutputDir, "raw");

                            if (!Directory.Exists(docsDir))
                            {
                                Directory.CreateDirectory(docsDir);
                            }
                            if (!Directory.Exists(rawDir))
                            {
                                Directory.CreateDirectory(rawDir);
                            }

                            var planFileName = Path.Combine(docsDir, "BatchMigrationPlan.md");
                            var effortSuffix = string.IsNullOrWhiteSpace(consolidatorEffort) ? "" : $", Effort: {consolidatorEffort}";
                            var metadataHeader = $"> [!NOTE]\n> **문서 작성일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n\n";
                            await File.WriteAllTextAsync(planFileName, metadataHeader + consolidatedPlan);
                            
                            if (aiResult != null)
                            {
                                if (!string.IsNullOrWhiteSpace(aiResult.ThinkingText))
                                {
                                    await File.WriteAllTextAsync(Path.Combine(docsDir, "Thinking.md"), aiResult.ThinkingText);
                                }
                                var rawContext = $"=== [System Prompt] ===\n{aiResult.SystemPrompt}\n\n=== [User Prompt] ===\n{aiResult.UserPrompt}";
                                await File.WriteAllTextAsync(Path.Combine(rawDir, "prompt-context.md"), rawContext);
                            }

                            AnsiConsole.MarkupLine($"[green]성공: 통합 배치 설계서 생성 완료![/] {Markup.Escape(planFileName)}");

                            // 통합 마이그레이션 지시서 생성
                            AnsiConsole.MarkupLine($"[yellow]{cliArgs.JobName}[/] - 통합 마이그레이션 지시서 생성 중...");
                            await metadataExporter.ExportConsolidatedMigrationInstructionsAsync(
                                spDefs,
                                consolidatedPlan,
                                cliArgs.JobName,
                                jobsOutputDir,
                                targetLanguage);

                            var instructionsPath = Path.Combine(jobsOutputDir, "agent", "MigrationInstructions.md");
                            AnsiConsole.MarkupLine($"[green]성공: 통합 마이그레이션 지시서 번들 생성 완료![/] {Markup.Escape(instructionsPath)}");

                            // 외부 코딩 에이전트(Codegen) 기동
                            var jobSpecificSrcDir = Path.Combine(jobsOutputDir, "src");
                            await RunCodegenEngineAsync(
                                instructionsPath,
                                isBatchMode: true,
                                enableCodegen: isCodegenEnabled,
                                engineName: selectedEngine,
                                targetProjectDir: jobSpecificSrcDir,
                                configuration: configuration,
                                aiClient: aiClient,
                                cancellationToken: activeCts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]에러: 배치 통합 설계서 작성 또는 코딩 에이전트 실행 중 오류 발생: {Markup.Escape(ex.Message)}[/]");
                    }
                    finally
                    {
                        _currentCts = globalCts; // 전역 CTS 복원
                    }
                }
            }
            else
            {
                // 대화형 TUI 모드 실행
                while (true)
                {
                    var choicesMenu = new[]
                    {
                        "1. 개별 Stored Procedure 역공학 분석 (SP Analysis)",
                        "2. 통합 배치 마이그레이션 설계 (Batch Design)",
                        "3. 마이그레이션 코딩 에이전트 구동 (Code Generation)",
                        "4. 통합 정산 정책 문서 도출 (Policy Extraction)",
                        "5. 프로그램 종료 (Exit)"
                    };

                    var selectedMenu = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold green]=== SP Analyzer 메인 메뉴 ===[/]")
                            .AddChoices(choicesMenu)
                    );

                    if (selectedMenu.StartsWith("5"))
                    {
                        AnsiConsole.MarkupLine("[blue]도구를 종료합니다.[/]");
                        break;
                    }
                    else if (selectedMenu.StartsWith("3"))
                    {
                        var jobsDir = Path.Combine(outputDir, "Jobs");
                        if (!Directory.Exists(jobsDir))
                        {
                            AnsiConsole.MarkupLine("[yellow]경고: Jobs 디렉터리가 존재하지 않습니다. 통합 배치 전환 계획을 먼저 수립하세요.[/]");
                            continue;
                        }

                        var instructionFiles = Directory.GetFiles(jobsDir, "MigrationInstructions.md", SearchOption.AllDirectories);
                        if (instructionFiles.Length == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]경고: 기작성된 마이그레이션 지시서(MigrationInstructions.md)를 찾을 수 없습니다.[/]");
                            continue;
                        }

                        var cancelOption = "[-- 메인 메뉴로 돌아가기 --]";
                        var choices = new List<string> { cancelOption };
                        choices.AddRange(instructionFiles);

                        var selectedInstruction = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("\n구동할 [green]마이그레이션 지시서(Job)[/]를 선택하세요:")
                                .PageSize(12)
                                .MoreChoicesText("[grey](더 많은 목록은 방향키를 누르세요)[/]")
                                .UseConverter(x => x == cancelOption ? Markup.Escape(x) : Markup.Escape(Path.GetRelativePath(jobsDir, x)))
                                .AddChoices(choices)
                                .EnableSearch()
                        );

                        if (selectedInstruction == cancelOption)
                        {
                            continue;
                        }

                        using var activeCts = new CancellationTokenSource();
                        _currentCts = activeCts;

                        try
                        {
                            var jobSpecificOutputDir = Directory.GetParent(Directory.GetParent(selectedInstruction)!.FullName)!.FullName;
                            var jobSpecificSrcDir = Path.Combine(jobSpecificOutputDir, "src");

                            await RunCodegenEngineAsync(
                                selectedInstruction,
                                isBatchMode: false,
                                enableCodegen: true, // 스탠드얼론 메뉴이므로 강제 활성화
                                engineName: selectedEngine,
                                targetProjectDir: jobSpecificSrcDir,
                                configuration: configuration,
                                aiClient: aiClient,
                                cancellationToken: activeCts.Token);
                            
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 메인 메뉴로 돌아갑니다...[/]");
                            Console.ReadKey(true);
                        }
                        catch (OperationCanceledException)
                        {
                            AnsiConsole.MarkupLine("\n[yellow]외부 코딩 에이전트 구동이 중단되었습니다. 메인 메뉴로 돌아갑니다.[/]");
                            AnsiConsole.WriteLine();
                            Console.ReadKey(true);
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]에러:[/] 외부 코딩 에이전트 구동 중 오류 발생: {Markup.Escape(ex.Message)}");
                            AnsiConsole.WriteLine();
                            Console.ReadKey(true);
                        }
                        finally
                        {
                            _currentCts = globalCts; // 전역 CTS 복원
                        }
                    }
                    else if (selectedMenu.StartsWith("1"))
                    {
                        var exitOption = "-- 메인 메뉴로 돌아가기 --";
                        var choices = new List<string>(spNames) { exitOption };

                        var selectedOption = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("\n분석할 [green]Stored Procedure[/]를 선택하거나 검색하세요:")
                                .PageSize(12)
                                .MoreChoicesText("[grey](더 많은 목록은 방향키를 누르세요)[/]")
                                .UseConverter(x => Markup.Escape(x))
                                .AddChoices(choices)
                                .EnableSearch()
                        );

                        if (selectedOption == exitOption)
                        {
                            continue;
                        }

                        var parts = selectedOption.Split('.', 2);
                        var schema = parts[0];
                        var name = parts[1];

                        using var activeCts = new CancellationTokenSource();
                        _currentCts = activeCts;

                        try
                        {
                            var (specMarkdown, spDef, reviewResult, thinkingText) = await orchestrator.RunPipelineAsync(
                                connectionString, schema, name, maxDepth, provider, instructions, isBatchMode: false, outputDir, enableCache, activeCts.Token);

                            if (string.IsNullOrEmpty(specMarkdown))
                            {
                                AnsiConsole.MarkupLine("[red]분석이 중단되었거나 명세서 생성에 실패했습니다.[/]");
                                continue;
                            }

                            // 분석과 전환 분리 요구에 따라, 개별 분석 시에는 배치 전환 설계서를 생성하지 않음 (null 지정)
                            string? migrationPlan = null;

                            if (!Directory.Exists(outputDir))
                            {
                                Directory.CreateDirectory(outputDir);
                            }

                            await SaveOutputsAsync(
                                spDef, specMarkdown, migrationPlan, outputDir, instructionsFile,
                                metadataExporter, saveRawJson, saveRawContext, saveRawFiles,
                                schema, name, provider, modelName, reviewResult, thinkingText, actorEffort, configuration);

                            var outputFileName = Path.Combine(outputDir, "Procedures", $"{schema}.{name}", "docs", "Spec.md");
                            AnsiConsole.Write(new Panel(new Markup($"[green]성공적으로 파일이 생성되었습니다![/]\n[bold]저장 경로:[/] {Markup.Escape(outputFileName)}"))
                            {
                                Border = BoxBorder.Rounded,
                                Header = new PanelHeader($" {selectedOption} 분석 완료 ")
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            AnsiConsole.MarkupLine("\n[yellow]분석 작업이 사용자에 의해 중단되었습니다. 메인 메뉴로 돌아갑니다.[/]");
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 계속합니다...[/]");
                            Console.ReadKey(true);
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]에러:[/] {selectedOption} 분석 또는 저장 중 오류 발생: {Markup.Escape(ex.Message)}");
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 계속합니다...[/]");
                            Console.ReadKey(true);
                        }
                        finally
                        {
                            _currentCts = globalCts; // 전역 CTS 복원
                        }
                    }
                    else if (selectedMenu.StartsWith("2"))
                    {
                        if (!Directory.Exists(outputDir))
                        {
                            AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉터리가 존재하지 않거나 분석서가 없습니다. 먼저 1번 메뉴로 분석을 진행하세요.[/]");
                            continue;
                        }

                        var specFiles = Directory.GetFiles(outputDir, "Spec.md", SearchOption.AllDirectories);
                        if (specFiles.Length == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉터리에 기분석된 명세서(Spec.md)가 존재하지 않습니다.[/]");
                            continue;
                        }

                        var selectedFiles = new List<string>();
                        var remainingFiles = new List<string>();
                        foreach (var file in specFiles)
                        {
                            remainingFiles.Add(Path.GetRelativePath(outputDir, file));
                        }

                        var defaultSpOrder = new[]
                        {
                            "UP_UTIL_PG_CLIENT_CMRATE_INS",
                            "UP_UTIL_SETTLE_INS",
                            "UP_UTIL_SETTLE_CANCEL_INS",
                            "UP_UTIL_SETTLE_EXCEPTION_PROC",
                            "UP_UTIL_SETTLE_COMM_UPD",
                            "UP_UTIL_SETTLE_EXPECT_PROC",
                            "UP_UTIL_SETTLE_INS_EXTRA",
                            "UP_Util_Settle_Ins_Extra4PLCard",
                            "UP_Util_Stat_PGCollect_Ins",
                            "UP_Util_Settle_Summary",
                            "UP_Util_Settle_Summary_Etc",
                            "UP_Util_Settle_Proc_Etc"
                        };

                        var defaultFilesToSelect = new List<string>();
                        foreach (var sp in defaultSpOrder)
                        {
                            var match = remainingFiles.FirstOrDefault(f => f.Replace('\\', '/').Contains($".{sp}/", StringComparison.OrdinalIgnoreCase));
                            if (match != null)
                            {
                                defaultFilesToSelect.Add(match);
                            }
                        }

                        if (defaultFilesToSelect.Count > 0)
                        {
                            bool useDefault = AnsiConsole.Confirm($"[green]ReSet 정산 배치 기본 순서({defaultFilesToSelect.Count}개 SP)로 자동 구성하시겠습니까?[/]\n(선택 완료 후 개별적으로 취소/초기화하여 변경할 수 있습니다.)", true);
                            if (useDefault)
                            {
                                foreach (var df in defaultFilesToSelect)
                                {
                                    selectedFiles.Add(df);
                                    remainingFiles.Remove(df);
                                }
                            }
                        }

                        var isCompleted = false;
                        var isCancelled = false;

                        // 순차적 단일 선택 루프
                        while (!isCompleted && !isCancelled)
                        {
                            AnsiConsole.Clear();
                            AnsiConsole.Write(new FigletText("ReSet Analyzer").Color(Color.Green));
                            AnsiConsole.MarkupLine("[bold green]=== SQL Server Stored Procedure Reverse Engineering Tool ===[/]");
                            AnsiConsole.WriteLine();

                            // 현재 구성된 배치 순서 시각화
                            if (selectedFiles.Count > 0)
                            {
                                var sequenceStr = string.Join(Environment.NewLine, selectedFiles.Select((f, index) => $"[bold green]{index + 1}.[/] [yellow]{Markup.Escape(Directory.GetParent(f)?.Parent?.Name ?? Path.GetFileName(f))}[/]"));
                                AnsiConsole.Write(new Panel(new Markup(sequenceStr))
                                {
                                    Header = new PanelHeader(" [bold cyan]현재 구성된 배치 Job 실행 순서[/] "),
                                    Border = BoxBorder.Rounded
                                });
                                AnsiConsole.WriteLine();
                            }
                            else
                            {
                                AnsiConsole.MarkupLine("[grey](현재 선택된 배치 스텝이 없습니다. 첫 번째로 실행할 SP 명세서를 선택하세요.)[/]");
                                AnsiConsole.WriteLine();
                            }

                            // 선택지 빌드
                            var choices = new List<string>();
                            var completeOption = "[-- 선택 완료 및 계획 생성 --]";
                            var undoOption = "[-- 마지막 선택 취소 (Undo) --]";
                            var clearOption = "[-- 전체 선택 초기화 (Clear) --]";
                            var cancelOption = "[-- 메인 메뉴로 돌아가기 --]";

                            if (selectedFiles.Count > 0)
                            {
                                choices.Add(completeOption);
                                choices.Add(undoOption);
                                choices.Add(clearOption);
                            }
                            choices.Add(cancelOption);
                            choices.AddRange(remainingFiles);

                            var selectedChoice = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title($"[green]배치 스텝 #{selectedFiles.Count + 1}[/]로 추가할 명세서를 선택하거나 검색하세요:")
                                    .PageSize(12)
                                    .MoreChoicesText("[grey](더 많은 목록은 방향키를 누르세요)[/]")
                                    .UseConverter(x => x.StartsWith("[--") ? Markup.Escape(x) : Markup.Escape(Directory.GetParent(x)?.Parent?.Name ?? Path.GetFileName(x)))
                                    .AddChoices(choices)
                                    .EnableSearch()
                            );

                            if (selectedChoice == cancelOption)
                            {
                                isCancelled = true;
                            }
                            else if (selectedChoice == completeOption)
                            {
                                isCompleted = true;
                            }
                            else if (selectedChoice == undoOption)
                            {
                                var last = selectedFiles.Last();
                                selectedFiles.RemoveAt(selectedFiles.Count - 1);
                                remainingFiles.Add(last);
                            }
                            else if (selectedChoice == clearOption)
                            {
                                remainingFiles.AddRange(selectedFiles);
                                selectedFiles.Clear();
                            }
                            else
                            {
                                selectedFiles.Add(selectedChoice);
                                remainingFiles.Remove(selectedChoice);
                            }
                        }

                        if (isCancelled || selectedFiles.Count == 0)
                        {
                            continue;
                        }

                        AnsiConsole.Clear();
                        AnsiConsole.Write(new FigletText("ReSet Analyzer").Color(Color.Green));
                        AnsiConsole.MarkupLine("[bold green]=== SQL Server Stored Procedure Reverse Engineering Tool ===[/]");
                        AnsiConsole.WriteLine();
                        var finalSeqStr = string.Join(Environment.NewLine, selectedFiles.Select((f, index) => $"[bold green]{index + 1}.[/] [yellow]{Markup.Escape(Directory.GetParent(f)?.Parent?.Name ?? Path.GetFileName(f))}[/]"));
                        AnsiConsole.Write(new Panel(new Markup(finalSeqStr))
                        {
                            Header = new PanelHeader(" [bold cyan]최종 구성된 배치 Job 실행 순서[/] "),
                            Border = BoxBorder.Rounded
                        });
                        AnsiConsole.WriteLine();

                        var specsData = new List<(string FileName, string Content)>();
                        foreach (var fileName in selectedFiles)
                        {
                            var fullPath = Path.Combine(outputDir, fileName);
                            var content = await File.ReadAllTextAsync(fullPath);
                            specsData.Add((fileName, content));
                        }

                        var jobName = AnsiConsole.Prompt(
                            new TextPrompt<string>("생성할 통합 배치 Job의 이름을 입력하세요:")
                                .DefaultValue("Consolidated_Batch_Job")
                        );

                        string? consolidatedPlan = null;
                        using var activeCts = new CancellationTokenSource();
                        _currentCts = activeCts;

                        try
                        {
                            var pipelineResult = await orchestrator.RunConsolidatedPipelineAsync(specsData, targetLanguage, jobName, provider, cancellationToken: activeCts.Token);
                            consolidatedPlan = pipelineResult.Plan;
                            var aiResult = pipelineResult.Result;
                            if (string.IsNullOrEmpty(consolidatedPlan))
                            {
                                AnsiConsole.MarkupLine("[red]통합 배치 설계서 작성이 중단되었거나 실패했습니다.[/]");
                                continue;
                            }

                            var jobsOutputDir = Path.Combine(outputDir, "Jobs", jobName);
                            var docsDir = Path.Combine(jobsOutputDir, "docs");
                            var rawDir = Path.Combine(jobsOutputDir, "raw");

                            if (!Directory.Exists(docsDir))
                            {
                                Directory.CreateDirectory(docsDir);
                            }
                            if (!Directory.Exists(rawDir))
                            {
                                Directory.CreateDirectory(rawDir);
                            }

                            var planFileName = Path.Combine(docsDir, "BatchMigrationPlan.md");
                            var effortSuffix = string.IsNullOrWhiteSpace(consolidatorEffort) ? "" : $", Effort: {consolidatorEffort}";
                            var metadataHeader = $"> [!NOTE]\n> **문서 작성일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n\n";
                            await File.WriteAllTextAsync(planFileName, metadataHeader + consolidatedPlan);

                            if (aiResult != null)
                            {
                                if (!string.IsNullOrWhiteSpace(aiResult.ThinkingText))
                                {
                                    await File.WriteAllTextAsync(Path.Combine(docsDir, "Thinking.md"), aiResult.ThinkingText);
                                }
                                var rawContext = $"=== [System Prompt] ===\n{aiResult.SystemPrompt}\n\n=== [User Prompt] ===\n{aiResult.UserPrompt}";
                                await File.WriteAllTextAsync(Path.Combine(rawDir, "prompt-context.md"), rawContext);
                            }
                            AnsiConsole.Write(new Panel(new Markup($"[green]통합 배치 설계서가 성공적으로 생성되었습니다![/]\n[bold]저장 경로:[/] {Markup.Escape(planFileName)}"))
                            {
                                Border = BoxBorder.Rounded,
                                Header = new PanelHeader($" {jobName} 통합 마이그레이션 완료 ")
                            });


                            // SpDefinition들 복원 및 통합 마이그레이션 지시서 생성
                            var spDefs = new List<SpDefinition>();
                            foreach (var fileName in selectedFiles)
                            {
                                var rawFileName = fileName.Replace(Path.Combine("docs", "Spec.md"), Path.Combine("raw", "metadata.json"));
                                var rawPath = Path.Combine(outputDir, rawFileName);
                                if (File.Exists(rawPath))
                                {
                                    try
                                    {
                                        var jsonContent = await File.ReadAllTextAsync(rawPath);
                                        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                        var spDef = System.Text.Json.JsonSerializer.Deserialize<SpDefinition>(jsonContent, options);
                                        if (spDef != null)
                                        {
                                            spDefs.Add(spDef);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        AnsiConsole.MarkupLine($"[yellow]경고: {rawFileName} 파일에서 메타데이터 복원 중 오류:[/] {Markup.Escape(ex.Message)}");
                                    }
                                }
                            }

                            try
                            {
                                AnsiConsole.MarkupLine($"\n[yellow]{jobName}[/] - 통합 마이그레이션 지시서 생성 중...");
                                await metadataExporter.ExportConsolidatedMigrationInstructionsAsync(
                                    spDefs,
                                    consolidatedPlan,
                                    jobName,
                                    jobsOutputDir,
                                    targetLanguage);

                                var instructionsPath = Path.Combine(jobsOutputDir, "agent", "MigrationInstructions.md");
                                AnsiConsole.MarkupLine($"[green]통합 마이그레이션 지시서 번들이 성공적으로 생성되었습니다![/]\n[bold]저장 경로:[/] {Markup.Escape(instructionsPath)}");

                                // 외부 코딩 에이전트(Codegen) 기동
                                var jobSpecificSrcDir = Path.Combine(jobsOutputDir, "src");
                                await RunCodegenEngineAsync(
                                    instructionsPath,
                                    isBatchMode: false,
                                    enableCodegen: isCodegenEnabled,
                                    engineName: selectedEngine,
                                    targetProjectDir: jobSpecificSrcDir,
                                    configuration: configuration,
                                    aiClient: aiClient,
                                    cancellationToken: activeCts.Token);
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]에러:[/] 통합 마이그레이션 지시서 생성 또는 코딩 에이전트 실행 중 오류 발생: {Markup.Escape(ex.Message)}");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            AnsiConsole.MarkupLine("\n[yellow]통합 설계서 수립 작업이 사용자에 의해 중단되었습니다. 메인 메뉴로 돌아갑니다.[/]");
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 계속합니다...[/]");
                            Console.ReadKey(true);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]에러:[/] 통합 설계서 작성 또는 저장 중 오류 발생: {Markup.Escape(ex.Message)}");
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 계속합니다...[/]");
                            Console.ReadKey(true);
                        }
                        finally
                        {
                            _currentCts = globalCts; // 전역 CTS 복원
                        }
                    }
                    else if (selectedMenu.StartsWith("4"))
                    {
                        var remainingFiles = new List<string>(spNames);
                        var selectedFiles = new List<string>();
                        var isCompleted = false;
                        var isCancelled = false;

                        // 순차적 단일 선택 루프
                        while (!isCompleted && !isCancelled)
                        {
                            AnsiConsole.Clear();
                            AnsiConsole.Write(new FigletText("ReSet Policy").Color(Color.Green));
                            AnsiConsole.MarkupLine("[bold green]=== 정산 정책 문서 도출 대상 선택 ===[/]");
                            AnsiConsole.WriteLine();

                            if (selectedFiles.Count > 0)
                            {
                                var sequenceStr = string.Join(" [bold green], [/] ", selectedFiles.Select(f => $"[yellow]{Markup.Escape(f)}[/]"));
                                AnsiConsole.Write(new Panel(new Markup(sequenceStr))
                                {
                                    Header = new PanelHeader(" [bold cyan]선택된 분석 대상 SP 목록[/] "),
                                    Border = BoxBorder.Rounded
                                });
                                AnsiConsole.WriteLine();
                            }

                            var choices = new List<string>();
                            var completeOption = "[-- 선택 완료 및 정책 문서 생성 --]";
                            var cancelOption = "[-- 메인 메뉴로 돌아가기 --]";

                            if (selectedFiles.Count > 0)
                            {
                                choices.Add(completeOption);
                            }
                            choices.Add(cancelOption);
                            choices.AddRange(remainingFiles);

                            var selectedChoice = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title($"[green]분석 대상 SP #{selectedFiles.Count + 1}[/]를 선택하거나 검색하세요:")
                                    .PageSize(12)
                                    .MoreChoicesText("[grey](더 많은 목록은 방향키를 누르세요)[/]")
                                    .UseConverter(x => Markup.Escape(x))
                                    .AddChoices(choices)
                                    .EnableSearch()
                            );

                            if (selectedChoice == cancelOption)
                            {
                                isCancelled = true;
                            }
                            else if (selectedChoice == completeOption)
                            {
                                isCompleted = true;
                            }
                            else
                            {
                                selectedFiles.Add(selectedChoice);
                                remainingFiles.Remove(selectedChoice);

                                if (remainingFiles.Count == 0)
                                {
                                    isCompleted = true;
                                }
                            }
                        }

                        if (isCancelled || selectedFiles.Count == 0)
                        {
                            continue;
                        }

                        var jobName = AnsiConsole.Prompt(
                            new TextPrompt<string>("생성할 정산 정책서의 작업(Job) 이름을 입력하세요:")
                                .DefaultValue("Consolidated_Settlement_Policy")
                        );

                        using var activeCts = new CancellationTokenSource();
                        _currentCts = activeCts;

                        try
                        {
                            string? rulebook = null;
                            await AnsiConsole.Status()
                                .StartAsync("정산 정책 문서 생성 중...", async ctx =>
                                {
                                    rulebook = await policyService.GenerateSettlementPolicyRulebookAsync(connectionString, selectedFiles, maxDepth, activeCts.Token);
                                });

                            if (string.IsNullOrEmpty(rulebook))
                            {
                                AnsiConsole.MarkupLine("[red]정산 정책 문서 생성에 실패했습니다.[/]");
                                continue;
                            }

                            if (!Directory.Exists(outputDir))
                            {
                                Directory.CreateDirectory(outputDir);
                            }

                            var rulebookName = $"{jobName}_Settlement_Policy_Rulebook.md";
                            var rulebookPath = Path.Combine(outputDir, rulebookName);
                            var effortSuffix = string.IsNullOrWhiteSpace(actorEffort) ? "" : $", Effort: {actorEffort}";
                            var metadataHeader = $"> [!NOTE]\n> **문서 작성일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n\n";

                            await File.WriteAllTextAsync(rulebookPath, metadataHeader + rulebook);
                            AnsiConsole.Write(new Panel(new Markup($"[green]정산 정책 문서가 성공적으로 생성되었습니다![/]\n[bold]저장 경로:[/] {Markup.Escape(rulebookPath)}"))
                            {
                                Border = BoxBorder.Rounded,
                                Header = new PanelHeader($" {jobName} 정책 분석 완료 ")
                            });

                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 메인 메뉴로 돌아갑니다...[/]");
                            Console.ReadKey(true);
                        }
                        catch (OperationCanceledException)
                        {
                            AnsiConsole.MarkupLine("\n[yellow]정책 문서 도출 작업이 중단되었습니다. 메인 메뉴로 돌아갑니다.[/]");
                            AnsiConsole.WriteLine();
                            Console.ReadKey(true);
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]에러:[/] 정책 문서 도출 중 오류 발생: {Markup.Escape(ex.Message)}");
                            AnsiConsole.WriteLine();
                            Console.ReadKey(true);
                        }
                        finally
                        {
                            _currentCts = globalCts;
                        }
                    }
                }
            }
            }
            finally
            {
                Serilog.Log.CloseAndFlush();
            }
        }


        private static async Task SaveOutputsAsync(
            ReSet.Core.Models.SpDefinition? spDef,
            string specMarkdown,
            string? migrationPlan,
            string outputDir,
            string instructionsFile,
            IMetadataExporter metadataExporter,
            bool saveRawJson,
            bool saveRawContext,
            bool saveRawFiles,
            string schema,
            string name,
            string provider,
            string modelName,
            ReviewResult? review,
            string? thinkingText = null,
            string? effort = null,
            IConfiguration? configuration = null)
        {
            var spOutputDir = Path.Combine(outputDir, "Procedures", $"{schema}.{name}");
            if (!Directory.Exists(spOutputDir))
            {
                Directory.CreateDirectory(spOutputDir);
            }
            
            var docsDir = Path.Combine(spOutputDir, "docs");
            if (!Directory.Exists(docsDir))
            {
                Directory.CreateDirectory(docsDir);
            }

            if (spDef != null)
            {
                try
                {
                    var dependenciesText = new System.Text.StringBuilder();
                    var tableSchemasText = new System.Text.StringBuilder();
                    var referenceDdlsText = new System.Text.StringBuilder();
                    var warningsText = new System.Text.StringBuilder();

                    if (spDef.Warnings.Count > 0)
                    {
                        warningsText.AppendLine("[DB 메타데이터 수집 중 발생한 경고/오류 목록]");
                        foreach (var warn in spDef.Warnings)
                        {
                            warningsText.AppendLine($"- {warn}");
                        }
                        warningsText.AppendLine();
                    }

                    foreach (var dep in spDef.Dependencies)
                    {
                        dependenciesText.AppendLine($"- Schema: {dep.Schema}, Name: {dep.Name}, Type: {dep.Type} (발견 깊이: {dep.DiscoveryDepth}단계)");
                        if (dep.Columns.Count > 0)
                        {
                            tableSchemasText.AppendLine($"### 테이블: {dep.Schema}.{dep.Name} ({dep.Type})");
                            foreach (var col in dep.Columns)
                            {
                                tableSchemasText.AppendLine($"| {col.ColumnName} | {col.DataType} | {(col.IsNullable ? "Yes" : "No")} |");
                            }
                        }
                        if (!string.IsNullOrEmpty(dep.ReferencedDdlText))
                        {
                            referenceDdlsText.AppendLine($"### {dep.Type}: {dep.Schema}.{dep.Name}");
                            referenceDdlsText.AppendLine(dep.ReferencedDdlText);
                        }
                    }

                    var rawPromptContext = $@"
[시스템 규칙 지침]
{(File.Exists(instructionsFile) ? await File.ReadAllTextAsync(instructionsFile) : "기본 마크다운 규칙을 적용하여 분석해 주세요.")}

{warningsText}
[수집된 DB 메타데이터 의존관계 목록]
{dependenciesText}

[의존하는 참조 테이블 상세 스키마 정보]
{tableSchemasText}

[의존하는 참조 UDF/SP 소스 코드]
{referenceDdlsText}

[Stored Procedure DDL SQL 원본]
{spDef.DdlText}
";
                    await metadataExporter.ExportRawMetadataAsync(
                        spDef,
                        spDef.RawPromptContext ?? rawPromptContext,
                        spOutputDir,
                        saveRawJson,
                        saveRawContext,
                        saveRawFiles);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]원천 산출물(Raw Metadata) 저장 중 경고:[/] {Markup.Escape(ex.Message)}");
                }
            }

            string yamlFrontMatter = "";
            string scoreHeader = "";
            if (review != null)
            {
                yamlFrontMatter = $@"---
종합 신뢰도: {review.NormalizedScore} # 100점 만점 기준 AI 최종 신뢰도
정합성 점수: {review.ScoreAccuracy}/10 # SQL 대비 기능 정합성
CRUD 점수: {review.ScoreCrud}/10 # 데이터 변경 및 조회 검증
인터페이스 점수: {review.ScoreInterface}/10 # 파라미터 및 반환셋 정합성
가독성 점수: {review.ScoreReadability}/10 # 코드 가독성 및 표준 준수
예외처리 점수: {review.ScoreException}/10 # 트랜잭션 격리 및 에러 처리
---

";
                scoreHeader = $"> **AI 최종 신뢰도**: {review.NormalizedScore}/100점 (정합성: {review.ScoreAccuracy}, CRUD: {review.ScoreCrud}, 연동: {review.ScoreInterface}, 가독성: {review.ScoreReadability}, 예외: {review.ScoreException})\n";
            }

            var effortSuffix = string.IsNullOrWhiteSpace(effort) ? "" : $", Effort: {effort}";
            var metadataHeader = $"> [!NOTE]\n> **문서 작성일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n> **분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n" + scoreHeader + "\n";

            var outputFileName = Path.Combine(docsDir, "Spec.md");
            await File.WriteAllTextAsync(outputFileName, yamlFrontMatter + metadataHeader + specMarkdown);

            if (!string.IsNullOrEmpty(migrationPlan))
            {
                var planFileName = Path.Combine(docsDir, "BatchMigrationPlan.md");
                await File.WriteAllTextAsync(planFileName, metadataHeader + migrationPlan);
            }



            if (!string.IsNullOrWhiteSpace(thinkingText))
            {
                try
                {
                    var thinkingFileName = Path.Combine(docsDir, "Thinking.md");
                    
                    // 기존 .txt 파일이 있다면 삭제 처리
                    var oldTxtFile = Path.Combine(docsDir, "Thinking.txt");
                    if (File.Exists(oldTxtFile))
                    {
                        try { File.Delete(oldTxtFile); } catch {}
                    }

                    var thinkingHeader = $"# AI 추론 과정 로그 (Thinking Process Log)\n\n" +
                                         $"- **기본 분석 AI 정보**: {provider} ({modelName}{effortSuffix})\n" +
                                         $"- **문서 작성일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" +
                                         "본 문서는 저장 프로시저 역공학 및 검증 파이프라인 수행 중 사용된 AI 모델들의 추론 과정(Thinking Process)을 기록한 마크다운 문서입니다.\n\n" +
                                         "---\n\n";
                    await File.WriteAllTextAsync(thinkingFileName, thinkingHeader + thinkingText);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]추론 로그(Thinking Log) 저장 중 경고:[/] {Markup.Escape(ex.Message)}");
                }
            }

        }

        private static async Task RunCodegenEngineAsync(
            string instructionsPath,
            bool isBatchMode,
            bool enableCodegen,
            string? engineName,
            string targetProjectDir,
            IConfiguration configuration,
            IAiClient aiClient,
            CancellationToken cancellationToken)
        {
            // CLI 옵션이나 설정파일 중 하나라도 codegen이 활성화되어 있어야 함
            if (!enableCodegen && isBatchMode)
            {
                return; // 배치 모드이고 비활성화 상태면 스킵
            }

            // 대화형 모드인 경우, codegen 옵션이 꺼져 있어도 사용자에게 기동 여부를 질문할 수 있음
            if (!isBatchMode)
            {
                var runConfirm = AnsiConsole.Confirm($"[yellow]마이그레이션된 소스 코드를 자동 생성하기 위해 외부 코딩 에이전트({engineName})를 기동하시겠습니까?[/]");
                if (!runConfirm)
                {
                    return;
                }
            }

            try
            {
                AnsiConsole.MarkupLine($"\n[bold blue]=== 외부 코딩 에이전트 기동 ({engineName}) ===[/]");
                var factory = new CodingEngineFactory(configuration);
                var engine = factory.CreateEngine(engineName ?? "claude");

                if (!File.Exists(instructionsPath))
                {
                    AnsiConsole.MarkupLine($"[red]에러: 마이그레이션 지시서 파일({Path.GetFileName(instructionsPath)})을 찾을 수 없습니다.[/]");
                    return;
                }

                var fileName = Path.GetFileName(instructionsPath);
                var agentDirInfo = Directory.GetParent(instructionsPath);
                var spName = agentDirInfo?.Parent?.Name ?? "Unknown";

                var targetLanguage = configuration["MigrationSettings:TargetLanguage"] ?? "C#";

                var baseDir = agentDirInfo?.Parent?.FullName ?? "";
                var specDir = Path.Combine(baseDir, "docs");

                // 최대 시도 횟수 설정 로드
                var maxL2AttemptsRaw = configuration["AiSettings:MaxL2Attempts"] ?? "2";
                int maxL2Attempts = 2;
                if (string.Equals(maxL2AttemptsRaw, "unlimited", StringComparison.OrdinalIgnoreCase) || maxL2AttemptsRaw == "-1")
                {
                    maxL2Attempts = -1;
                }
                else if (int.TryParse(maxL2AttemptsRaw, out int parsed))
                {
                    maxL2Attempts = parsed;
                }

                // Validator 및 Codegen Workflow Orchestrator 설정
                var validatorConfig = new ValidatorConfig
                {
                    MaxL2Attempts = maxL2Attempts, // 단방향 검증용 설정 (이제 내부적으로 반복하지 않음)
                    SpecDirectory = specDir,
                    SourceCodeDirectory = targetProjectDir,
                    OutputDirectory = Path.Combine(baseDir, "validation")
                };

                var metadataExporter = new ReSet.Core.Services.MetadataExporter();
                var codeVerificationOrchestrator = new CodeVerificationOrchestrator(validatorConfig, aiClient, null, new ValidationUiProxy());
                var codegenWorkflowOrchestrator = new CodegenWorkflowOrchestrator(engine, codeVerificationOrchestrator, metadataExporter, maxL2Attempts);

                AnsiConsole.MarkupLine($"[grey]지시서 경로: {instructionsPath}[/]");
                AnsiConsole.MarkupLine($"[grey]타겟 프로젝트 디렉터리: {targetProjectDir}[/]");
                AnsiConsole.MarkupLine("[yellow]외부 프로세스 기동 중... (TDD 로컬 빌드 및 자가수정 루프)[/]\n");

                bool isSuccess = await codegenWorkflowOrchestrator.RunSelfHealingWorkflowAsync(
                    jobOrSpName: spName,
                    instructionsFilePath: instructionsPath,
                    specDir: specDir,
                    codeDir: targetProjectDir,
                    isBatchMode: isBatchMode,
                    cancellationToken: cancellationToken);

                if (isSuccess)
                {
                    AnsiConsole.MarkupLine("\n[bold green]✔ 코딩 에이전트 자가 수정 루프 통과 (MATCH)[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("\n[bold red]❌ 코딩 에이전트 검증 완전 통과 실패. (최종 결과 확인 요망)[/]");
                }
            }
            catch (OperationCanceledException)
            {
                throw; // 상위에서 잡도록 던짐
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"\n[red]외부 코딩 에이전트 실행 중 오류 발생:[/] {Markup.Escape(ex.Message)}");
            }
        }

        private static void ConfigureLogging(IConfiguration configuration)
        {
            var rawLogDirectory = configuration["LoggingSettings:LogDirectory"] ?? "./output/logs";
            var logDirectory = Path.GetFullPath(rawLogDirectory);
            var minLevelStr = configuration["LoggingSettings:MinimumLevel"] ?? "Information";
            var retainedFileCountLimitStr = configuration["LoggingSettings:RetainedFileCountLimit"] ?? "31";

            try
            {
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]경고: 로그 디렉터리 생성 실패 ({logDirectory}): {Markup.Escape(ex.Message)}[/]");
            }

            var logEventLevel = minLevelStr.ToLowerInvariant() switch
            {
                "verbose" => Serilog.Events.LogEventLevel.Verbose,
                "debug" => Serilog.Events.LogEventLevel.Debug,
                "information" => Serilog.Events.LogEventLevel.Information,
                "warning" => Serilog.Events.LogEventLevel.Warning,
                "error" => Serilog.Events.LogEventLevel.Error,
                "fatal" => Serilog.Events.LogEventLevel.Fatal,
                _ => Serilog.Events.LogEventLevel.Information
            };

            int.TryParse(retainedFileCountLimitStr, out int retainedFileCountLimit);
            if (retainedFileCountLimit <= 0) retainedFileCountLimit = 31;

            var logFilePath = Path.Combine(logDirectory, "reset-.log");

            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Is(logEventLevel)
                .WriteTo.File(
                    path: logFilePath,
                    rollingInterval: Serilog.RollingInterval.Day,
                    retainedFileCountLimit: retainedFileCountLimit,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    encoding: System.Text.Encoding.UTF8)
                .CreateLogger();

            Serilog.Log.Information("=== ReSet CLI 실행 로거 시작 ===");
        }
    }
}
