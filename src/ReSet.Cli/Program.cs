using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// OpenRouter의 백엔드 라우팅 선호를 읽는다. provider가 OpenRouter가 아니면
        /// 이 설정 구획이 없어 자연히 null이 되고, 팩토리도 다른 provider에서는
        /// 이 인자를 쓰지 않는다.
        /// </summary>
        public static ReSet.Core.Services.Clients.OpenRouterRoutingOptions? ReadOpenRouterRouting(
            IConfiguration configuration, string provider)
        {
            var section = configuration.GetSection($"AiSettings:Providers:{provider}:Routing");
            if (!section.Exists())
            {
                return null;
            }

            var order = section.GetSection("Order").GetChildren()
                .Select(child => child.Value ?? string.Empty)
                .ToArray();

            return ReSet.Core.Services.Clients.OpenRouterRoutingOptions.Parse(
                order, section["AllowFallbacks"], section["RequireParameters"]);
        }

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
                else if (arg.Equals("--coverage-map", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cliArgs.CoverageMapTarget = args[++i];
                }
                else if (arg.Equals("--sweep", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.RunSweep = true;
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
                if (_currentCts == null)
                {
                    return;
                }

                if (!_currentCts.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine("\n[red]사용자에 의해 작업 취소 요청이 발생했습니다. 안전하게 정리 중...[/]");
                    _currentCts.Cancel();
                }
                else
                {
                    // 취소 이후 구간은 최대 30초짜리 산출물 저장(AnalyzeAsync의 grace CTS)이고
                    // 그동안 화면은 조용하다. 여기서 e.Cancel을 설정하지 않으면 .NET 기본 동작이
                    // 프로세스를 즉시 죽여 쓰던 Spec.md가 truncate된 채 남고, 재링크가 성공 노드를
                    // 다시 쓰므로 이전 실행의 멀쩡한 명세서까지 손상된다.
                    AnsiConsole.MarkupLine("\n[yellow]이미 취소를 요청했습니다. 완료된 산출물을 저장 중입니다. 잠시만 기다려 주세요...[/]");
                }

                e.Cancel = true; // 프로세스 즉시 종료 방지 및 OperationCanceledException 유도
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

            // 커버리지 맵 모드 - DB·AI 없이 output/ 산출물만 읽는다. 그래서 로그인
            // 흐름보다도, 배치 모드 AI provider 가드(IsBatchMode 분기)보다도 앞에
            // 둔다. [I2 - 2026-08-24 리뷰] 이 분기가 가드보다 뒤에 있으면, CLI
            // provider + AllowCliProviderInBatch=false(배포 기본값) 조합에서 AI를
            // 한 번도 안 부르는 이 실행이 그 가드에 걸려 ExitCode=1로 막힌다 - 가드
            // 자체는 무인 배치 안전 장치이므로 건드리지 않고, 이 분기를 그 앞으로
            // 옮겨 애초에 가드에 안 걸리게 한다. outputDir 계산도 이 분기가
            // 필요로 하는 것뿐이라 함께 끌어올린다.
            var outputDir = configuration["OutputSettings:Directory"] ?? "./output";
            if (!Path.IsPathRooted(outputDir))
            {
                outputDir = Path.Combine(Directory.GetCurrentDirectory(), outputDir);
            }

            if (!string.IsNullOrEmpty(cliArgs.CoverageMapTarget))
            {
                var written = CoverageMapCommand.Run(outputDir, cliArgs.CoverageMapTarget);
                if (written == null)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]커버리지 맵 대상을 찾지 못했습니다: {Markup.Escape(cliArgs.CoverageMapTarget)}[/]");
                    // [I3 - 2026-08-24 리뷰] 같은 메서드 아래쪽(배치 가드)의 규약과
                    // 같다 - 종료 코드 0으로 끝나면 아무것도 만들지 않았는데도
                    // 파이프라인이 초록으로 통과한다.
                    Environment.ExitCode = 1;
                    return;
                }

                AnsiConsole.MarkupLine($"[green]커버리지 맵 생성 완료:[/] {Markup.Escape(written)}");
                return;
            }

            // 스윕 모드 - 커버리지 맵과 같은 이유로 배치 가드보다 앞에 둔다: DB·AI 없이
            // output/ 산출물만 읽는 실행이 CLI provider 배치 가드에 걸려서는 안 된다.
            if (cliArgs.RunSweep)
            {
                var written = SweepCommand.Run(outputDir, Directory.GetCurrentDirectory());
                if (written == null)
                {
                    AnsiConsole.MarkupLine("[red]스윕할 대상을 찾지 못했습니다.[/]");
                    // 커버리지 맵 분기와 같은 규약이다 - 종료 코드 0으로 끝나면
                    // 아무것도 만들지 않았는데도 파이프라인이 초록으로 통과한다.
                    Environment.ExitCode = 1;
                    return;
                }

                AnsiConsole.MarkupLine($"[green]스윕 보고서: {Markup.Escape(written)}[/]");
                return;
            }

            // 세션에서 이전 연결 정보 복원
            var session = SessionManager.LoadSession();
            var server = !string.IsNullOrEmpty(session.LastUsedServer) ? session.LastUsedServer : (configuration["DatabaseSettings:Server"] ?? "localhost");
            string database = !string.IsNullOrEmpty(session.LastUsedDatabase) ? session.LastUsedDatabase : (configuration["DatabaseSettings:Database"] ?? "master");

            // 3. 서비스 구성 변수 준비
            var provider = configuration["AiSettings:Provider"] ?? "OpenAI";
            var modelName = configuration["AiSettings:ModelName"] ?? "gpt-4o";
            
            // 프로바이더별 ApiKey와 Endpoint 로드
            var apiKey = configuration[$"AiSettings:Providers:{provider}:ApiKey"] ?? string.Empty;
            var endpoint = configuration[$"AiSettings:Providers:{provider}:Endpoint"] ?? string.Empty;
            var cliCommand = configuration[$"AiSettings:Providers:{provider}:Command"];

            // 무인 배치 도중 구독 쿼터가 소진되거나 권한 프롬프트에서 멈추면 장시간
            // 실행이 통째로 날아간다. 시작 직후에 막는다. 그 손실을 감수하고 구독 계정으로
            // 돌려야 하는 사정이 있으면 AllowCliProviderInBatch로 연다(기본 false).
            bool.TryParse(
                configuration["AiSettings:AllowCliProviderInBatch"] ?? "false",
                out bool allowCliProviderInBatch);

            if (cliArgs.IsBatchMode)
            {
                var guardCriticProvider = configuration["AiSettings:Critic:Provider"];
                var guardConsolidatorProvider = configuration["AiSettings:Consolidator:Provider"];

                var blockedRole = ReSet.Core.Services.Clients.Cli.CliProviderBatchGuard.FindBlockedRole(
                    provider,
                    guardCriticProvider,
                    guardConsolidatorProvider,
                    allowCliProviderInBatch);

                if (blockedRole != null)
                {
                    // 옵트인을 켰는데도 막혔다면 원인은 agy-cli 하나뿐이다(가드가 그것만 남긴다).
                    // 사유를 갈라 놓지 않으면 "허용했는데 왜 막히나"에서 사용자가 멈춘다.
                    if (allowCliProviderInBatch)
                    {
                        AnsiConsole.MarkupLine(
                            $"[red]에러: agy-cli는 배치 모드 허용 설정으로도 분석 역할에 사용할 수 없습니다. ({Markup.Escape(blockedRole)} 역할)[/]");
                        AnsiConsole.MarkupLine(
                            "[yellow]agy-cli는 툴을 끌 수단이 없어 헤드리스에서 권한을 자동 거부하고 빈 응답만 남깁니다. claude-cli 또는 codex-cli를 사용해 주십시오.[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine(
                            $"[red]에러: 배치 모드에서는 CLI provider를 사용할 수 없습니다. ({Markup.Escape(blockedRole)} 역할)[/]");
                        AnsiConsole.MarkupLine(
                            "[yellow]CLI provider는 구독 쿼터 소진이나 권한 프롬프트로 무인 실행 도중 중단될 수 있습니다. appsettings.json에서 API provider로 변경하거나, 위험을 감수한다면 AiSettings:AllowCliProviderInBatch를 true로 설정해 주십시오.[/]");
                    }

                    // 이 가드의 존재 이유는 무인 CI 실행을 세우는 것이다. 종료 코드 0으로
                    // 끝나면 아무것도 만들지 않았는데도 파이프라인이 초록으로 통과한다.
                    Environment.ExitCode = 1;
                    return;
                }

                // 통과시킨 것이지 안전해진 것이 아니다. 실제로 CLI provider를 쓰는 실행에만
                // 경고를 남긴다 - 설정만 켜 두고 API provider로 도는 실행까지 시끄러우면
                // 경고가 배경 소음이 되어 정작 위험한 실행에서 읽히지 않는다.
                var cliRole = ReSet.Core.Services.Clients.Cli.CliProviderBatchGuard.FindCliRole(
                    provider, guardCriticProvider, guardConsolidatorProvider);

                if (cliRole != null)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]경고: 배치 모드에서 CLI provider를 사용합니다. ({Markup.Escape(cliRole)} 역할)[/]");
                    AnsiConsole.MarkupLine(
                        "[yellow]권한 프롬프트 정지나 구독 쿼터 소진이 발생하면 이번 실행 전체가 소실될 수 있습니다.[/]");
                }
            }

            var tempStr = configuration["AiSettings:Temperature"] ?? "0.2";
            float.TryParse(tempStr, out float temp);

            var depthStr = configuration["DatabaseSettings:MaxDependencyDepth"] ?? "3";
            int.TryParse(depthStr, out int maxDepth);

            var allowExternalDbStr =
                configuration["DatabaseSettings:AllowExternalDatabaseConnections"] ?? "false";
            bool.TryParse(allowExternalDbStr, out bool allowExternalDatabaseConnections);

            var instructionsFile = configuration["OutputSettings:InstructionsFile"] ?? "instructions.md";
            if (!Path.IsPathRooted(instructionsFile))
            {
                instructionsFile = Path.Combine(Directory.GetCurrentDirectory(), instructionsFile);
            }

            bool.TryParse(configuration["OutputSettings:SaveRawJson"] ?? "false", out bool saveRawJson);
            bool.TryParse(configuration["OutputSettings:SaveRawContext"] ?? "false", out bool saveRawContext);
            bool.TryParse(configuration["OutputSettings:SaveRawFiles"] ?? "false", out bool saveRawFiles);
            bool.TryParse(configuration["OutputSettings:EnableCache"] ?? "false", out bool enableCache);
            bool.TryParse(configuration["AnalysisSettings:AnalyzeReferencedCodeObjects"] ?? "false", out bool analyzeReferencedCodeObjects);
            var dependencyArtifactMode = Enum.TryParse<DependencyArtifactMode>(
                configuration["OutputSettings:DependencyArtifactMode"],
                ignoreCase: true,
                out var parsedDependencyArtifactMode)
                ? parsedDependencyArtifactMode
                : DependencyArtifactMode.Reference;

            bool.TryParse(configuration["MigrationSettings:Enabled"] ?? "true", out bool migrationEnabled);
            var targetLanguage = configuration["MigrationSettings:TargetLanguage"] ?? "C#";

            bool.TryParse(configuration["CodegenSettings:Enabled"] ?? "false", out bool codegenEnabled);
            var codegenEngine = configuration["CodegenSettings:Engine"] ?? "claude";

            var isCodegenEnabled = cliArgs.EnableCodegen || codegenEnabled;
            var selectedEngine = cliArgs.Engine ?? codegenEngine;

            // 무인 배치에서 codegen이 켜져 있는데 선택된 엔진에 BatchArguments가 없으면
            // (예: agy) CodingEngineFactory.CreateEngine이 던진다. 이전에는 그 실패 지점이
            // RunCodegenEngineAsync 안쪽이라, 분석 파이프라인 전체를 다 돌리고 지시서까지
            // 쓴 뒤에야 드러났다. 같은 검증을 시작 시점으로 앞당긴다 - 검증 로직 자체는
            // CodingEngineFactory 한 곳에만 있고 여기서 다시 적지 않는다.
            if (cliArgs.IsBatchMode && isCodegenEnabled)
            {
                try
                {
                    new CodingEngineFactory(configuration).CreateEngine(selectedEngine, isBatchMode: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AnsiConsole.MarkupLine($"[red]에러: 무인 배치용 코딩 엔진 설정이 올바르지 않습니다.[/] {Markup.Escape(ex.Message)}");
                    Environment.ExitCode = 1;
                    return;
                }
            }

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
                            catch (Exception ex) when (ex is not OperationCanceledException)
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
                        ) ?? database;

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
                                catch (Exception ex) when (ex is not OperationCanceledException)
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

            // database는 위 오프라인/대화형/배치 분기 전체에서 항상 비어있지 않은 값으로
            // 결정된다. async 상태 머신이 await 지점을 넘나들며 지역 변수를 호이스팅하면 널
            // 가능성 흐름 분석의 정밀도가 떨어지므로, 분기가 합류하는 이 지점에서 한 번
            // 명시적으로 단언해 이후의 모든 사용처(예: OutputPathResolver 생성)가 non-null로
            // 흐르도록 한다.
            ArgumentException.ThrowIfNullOrEmpty(database);
            var resolvedDatabase = database;

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

            var openRouterRouting = ReadOpenRouterRouting(configuration, provider);

            IAiClient aiClient = ReSet.Core.Services.Clients.AiClientFactory.CreateClient(provider, modelName, apiKey, endpoint, httpClient, numCtx, cliCommand, openRouterRouting);
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

                var criticCommand = configuration[$"AiSettings:Providers:{criticProvider}:Command"];
                var criticRouting = ReadOpenRouterRouting(configuration, criticProvider);
                var criticClient = ReSet.Core.Services.Clients.AiClientFactory.CreateClient(criticProvider, criticModel, criticApiKey, criticEndpoint, httpClient, criticNumCtx, criticCommand, criticRouting);
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

                var consolidatorCommand = configuration[$"AiSettings:Providers:{consolidatorProvider}:Command"];
                var consolidatorRouting = ReadOpenRouterRouting(configuration, consolidatorProvider);
                var consolidatorClient = ReSet.Core.Services.Clients.AiClientFactory.CreateClient(consolidatorProvider, consolidatorModel, consolidatorApiKey, consolidatorEndpoint, httpClient, consolidatorNumCtx, consolidatorCommand, consolidatorRouting);
                consolidatorService = new AiService(consolidatorClient, temp, consolidatorEnableThinking, criticThresholdScore, enableLocalChunking);
            }

            IMetadataExporter metadataExporter = new MetadataExporter();
            bool.TryParse(configuration["ValidationSettings:UseMermaidCli"] ?? "false", out bool useMermaidCli);
            var validator = new MechanicalValidator(useMermaidCli);
            var userInteraction = new ConsoleUserInteraction();
            var maxL2Attempts = configuration["AiSettings:MaxL2Attempts"] ?? "1";
            // 설정 키가 없거나 숫자가 아니면 실사용 기본값 4. 생성자 기본값(1)과 다른 것은
            // 의도된 것이다 — 자세한 근거는 설계 문서 §4를 보라.
            var stepConcurrencyRaw = configuration["AiSettings:StepConcurrency"];
            if (!int.TryParse(stepConcurrencyRaw, out int stepConcurrency))
            {
                // 키가 아예 없으면 정상적인 기본값 사용이므로 조용히 넘어간다. 키는
                // 있는데 숫자가 아니면(예: MaxL2Attempts를 따라 "unlimited"를 적었을 때)
                // 값이 말없이 4로 바뀌는 것이므로 알린다.
                if (!string.IsNullOrEmpty(stepConcurrencyRaw))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]경고: AiSettings:StepConcurrency 값('{Markup.Escape(stepConcurrencyRaw)}')이 숫자가 아니어서 기본값 4를 사용합니다.[/]");
                }

                stepConcurrency = 4;
            }

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
                criticThresholdScore,
                stepConcurrency
            );
            var recursiveOrchestrator = new VerificationPipelineOrchestrator(
                dbService,
                aiService,
                validator,
                new RecursiveAnalysisUserInteraction(userInteraction),
                maxL2Attempts,
                modelName,
                null,
                criticService,
                consolidatorService,
                actorEffort,
                criticEffort,
                consolidatorEffort,
                criticThresholdScore,
                stepConcurrency
            );
            IDependencyAnalysisOrchestrator dependencyAnalysisOrchestrator = new DependencyAnalysisOrchestrator(
                dbService,
                recursiveOrchestrator);
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
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
                        var resolution = TargetProcedureResolver.Resolve(cliArgs.PolicyProcedures, spNames);
                        if (!ReportUnmatchedTargets(resolution.Unmatched)) return;
                        policyTargetSps.AddRange(resolution.Matched);
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

                        // 이 문서는 SettlementPolicyService가 AI 결과를 그대로 반환한 것이며
                        // L1도 L2도 거치지 않는다. 검증 파이프라인 산출물과 같은 형식의
                        // 헤더를 쓰되, 검증되지 않았다는 사실을 명시한다.
                        await File.WriteAllTextAsync(
                            rulebookPath,
                            VerificationDocumentFormatter.FormatUnverifiedDocument(
                                rulebook, null, provider, modelName, actorEffort, DateTime.Now));
                        AnsiConsole.MarkupLine($"[green]성공: 정산 정책 문서 생성 완료![/] {Markup.Escape(rulebookPath)}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
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
                    var resolution = TargetProcedureResolver.Resolve(cliArgs.TargetProcedures, spNames);
                    if (!ReportUnmatchedTargets(resolution.Unmatched)) return;
                    targetSps.AddRange(resolution.Matched);
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

                        var result = await RunConfiguredAnalysisAsync(
                            analyzeReferencedCodeObjects,
                            dependencyAnalysisOrchestrator,
                            orchestrator,
                            dbService,
                            connectionString,
                            database ?? string.Empty,
                            schema,
                            name,
                            maxDepth,
                            provider,
                            modelName,
                            actorEffort,
                            instructions,
                            isBatchMode: true,
                            outputDir,
                            enableCache,
                            allowExternalDatabaseConnections,
                            dependencyArtifactMode,
                            globalCts.Token);

                        // 취소 판정을 명세서 검사보다 먼저 한다. ExecutionOrder는 후위 순회라
                        // 루트가 마지막에 실행되므로, 취소 시 루트 명세서는 대개 비어 있다.
                        // 순서를 뒤집으면 사용자 취소가 "명세서 획득 실패"라는 분석 오류로 오보된다.
                        // 완료분은 오케스트레이터가 이미 저장했으므로 여기서 빠져나가도 잃는 산출물이 없다.
                        if (result.Completion == GraphCompletion.PartialCancelled)
                        {
                            AnsiConsole.MarkupLine("\n[red]사용자에 의해 배치 분석 작업이 중단되었습니다. 프로세스를 종료합니다.[/]");
                            break;
                        }

                        var specMarkdown = result.SpecMarkdown;
                        if (string.IsNullOrEmpty(specMarkdown))
                        {
                            throw new Exception("검증 파이프라인을 통과한 명세서 획득 실패");
                        }

                        // 수집된 사양서 데이터를 메모리에 보관. FileName은 "docs/Spec.md"
                        // 같은 고정 문자열이면 안 된다 — 통합 배치 파이프라인의 목차
                        // 커버리지 검사(VerificationPipelineOrchestrator)와 AI 프롬프트의
                        // "Filename:" 레이블이 둘 다 이 값으로 명세서를 구분하는데,
                        // 고정 문자열은 N개 명세서를 전부 한 항목으로 뭉갠다(실측된 결함).
                        // result.Definition이 있으면 그 스키마.이름을 쓰고, 없으면(드묾)
                        // 이미 파싱해 둔 schema/name으로 대체한다 — 두 값 모두 항상
                        // 채워져 있으므로 "docs/Spec.md" 같은 모호한 자리표시자로
                        // 떨어질 일이 없고, 따라서 커버리지 검사가 이 항목을 가짜
                        // 누락으로 잘못 보고할 일도 없다.
                        var specFileName = result.Definition != null
                            ? $"{result.Definition.Schema}.{result.Definition.Name}"
                            : $"{schema}.{name}";
                        specsData.Add((specFileName, specMarkdown));
                        if (result.Definition != null)
                        {
                            spDefs.Add(result.Definition);
                        }

                        var thinkingText = result.ThinkingText;
                        string? migrationPlan = null;
                        if (migrationEnabled && result.Definition != null)
                        {
                            AnsiConsole.MarkupLine($"[yellow]{schema}.{name}[/] - 배치 전환 계획 설계서 작성 중 ({targetLanguage})...");
                            var migrationResult = await aiService.GenerateBatchMigrationPlanAsync(result.Definition, targetLanguage, globalCts.Token);
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

                        // 재귀 경로는 오케스트레이터가 이미 저장했다(Persistence != NotAttempted).
                        if (result.Persistence == ArtifactPersistence.NotAttempted)
                        {
                            await SaveRawArtifactsAsync(
                                result.Definition, outputDir, instructionsFile, metadataExporter,
                                saveRawJson, saveRawContext, saveRawFiles, schema, name);

                            // 캐시 게이트는 캐시에서 나온 문서(Spec.md/Thinking.md)만 덮는다.
                            // 그것을 다시 쓰면 분석하지 않은 날짜가 찍히기 때문이다.
                            if (!result.FromCache)
                            {
                                await SaveDocumentsAsync(
                                    specMarkdown, outputDir, schema, name,
                                    provider, modelName, result.Review, result.Outcome,
                                    thinkingText, actorEffort, result.Scope);
                            }
                        }

                        // 계획서는 이번 실행이 방금 만든 산출물이라 캐시에서 나올 수 없다.
                        // 명세서의 캐시 상태에 묶으면 AI 비용만 내고 파일을 버리게 된다.
                        // Persistence 게이트 밖에 둔다 — GenerateBatchMigrationPlanAsync는
                        // Persistence와 무관하게 호출되므로, 게이트 안에 두면 재귀 경로에서
                        // AI 비용을 내고 결과를 버린 뒤 "분석 완료 및 저장!"이라고 보고하게 된다.
                        // 저장 경로({outputDir}/Procedures/{schema}.{name}/docs/BatchMigrationPlan.md)는
                        // OutputPathResolver.ResolveDocsDirectory와 같고 오케스트레이터는 이 파일을
                        // 쓰지 않으므로 소유권이 겹치지 않는다.
                        if (!string.IsNullOrEmpty(migrationPlan))
                        {
                            await SaveMigrationPlanAsync(
                                migrationPlan, outputDir, schema, name,
                                provider, modelName, result.Outcome, actorEffort);
                        }

                        // 저장이 실패했는데 "저장!"이라고 말하면 배치 로그가 거짓이 된다.
                        // 상세 사유는 RenderAnalysisDiagnostics가 이미 냈다.
                        if (result.Persistence == ArtifactPersistence.Failed)
                        {
                            AnsiConsole.MarkupLine($"[red]실패:[/] {selectedOption} 산출물 저장에 실패했습니다.");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[green]성공:[/] {selectedOption} 분석 완료 및 저장!");
                        }
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
                        var pipelineResult = await orchestrator.RunConsolidatedPipelineAsync(specsData, targetLanguage, cliArgs.JobName, provider, outputDir, isBatchMode: true, definitions: spDefs, cancellationToken: activeCts.Token);
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
                            await File.WriteAllTextAsync(
                                planFileName,
                                VerificationDocumentFormatter.FormatVerifiedDocument(
                                    consolidatedPlan,
                                    pipelineResult.Review,
                                    pipelineResult.Outcome,
                                    provider,
                                    modelName,
                                    consolidatorEffort,
                                    DateTime.Now,
                                    scope: null,
                                    coverage: pipelineResult.Coverage));

                            if (aiResult != null)
                            {
                                // 추론 본문이 비어도 쓴다. 두 산출물은 한 쌍이라, 한쪽만 나가면
                                // 채택된 시도가 무엇을 사고했는지 되짚을 길이 사라진다.
                                await File.WriteAllTextAsync(
                                    Path.Combine(docsDir, "Thinking.md"),
                                    ThinkingLogDocument.Compose(
                                        aiResult.ThinkingText, provider, modelName, consolidatorEffort, DateTime.Now));
                                var rawContext = $"=== [System Prompt] ===\n{aiResult.SystemPrompt}\n\n=== [User Prompt] ===\n{aiResult.UserPrompt}";
                                await File.WriteAllTextAsync(Path.Combine(rawDir, "prompt-context.md"), rawContext);
                            }

                            AnsiConsole.MarkupLine($"[green]성공: 통합 배치 설계서 생성 완료![/] {Markup.Escape(planFileName)}");

                            // 통합 마이그레이션 지시서 생성
                            AnsiConsole.MarkupLine($"[yellow]{cliArgs.JobName}[/] - 통합 마이그레이션 지시서 생성 중...");
                            var bundle = await metadataExporter.ExportConsolidatedMigrationInstructionsAsync(
                                spDefs,
                                consolidatedPlan,
                                pipelineResult.Outcome,
                                cliArgs.JobName,
                                jobsOutputDir,
                                targetLanguage,
                                new OutputPathResolver(resolvedDatabase, outputDir),
                                pipelineResult.Layout,
                                pipelineResult.Coverage,
                                activeCts.Token);

                            foreach (var warning in bundle.Warnings)
                            {
                                AnsiConsole.MarkupLine($"[yellow]경고: {Markup.Escape(warning)}[/]");
                            }

                            AnsiConsole.MarkupLine(
                                $"[green]성공: 통합 마이그레이션 지시서 번들 생성 완료![/] {Markup.Escape(bundle.EntryPointPath)}");

                            // 외부 코딩 에이전트(Codegen) 기동
                            var jobSpecificSrcDir = Path.Combine(jobsOutputDir, "src");
                            await RunCodegenEngineAsync(
                                bundle,
                                isBatchMode: true,
                                enableCodegen: isCodegenEnabled,
                                engineName: selectedEngine,
                                targetProjectDir: jobSpecificSrcDir,
                                configuration: configuration,
                                aiClient: aiClient,
                                cancellationToken: activeCts.Token);
                        }
                    }
                    // 이 안쪽 catch가 RunCodegenEngineAsync가 다시 던진 취소를 먼저 삼키면,
                    // "코딩 에이전트 실행 중 오류"로 둔갑해 보고되고 사용자의 Ctrl-C가
                    // 무시된 채 배치 작업이 그대로 끝난다(가로챌 상위 OCE 핸들러가 없다).
                    catch (Exception ex) when (ex is not OperationCanceledException)
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

                            // 고른 문서가 새 번들(회차별)인지 옛 단일 문서인지 먼저 판정한다.
                            // 새 번들을 예전 전체 Job 경로로 돌리면, 그 경로는 "배정된
                            // task-*.md만 읽고 다른 Step은 읽지 마십시오"라는 지시를 이해하지
                            // 못한 채 회차 본문이 빠진 진입점만 떠먹게 된다 - 절대 만들면 안 되는
                            // 조합이라 여기서 분기부터 한다.
                            var (kind, stagePlan) = TryClassifyExistingInstructionsFile(selectedInstruction);

                            switch (kind)
                            {
                                case ExistingInstructionsKind.Staged:
                                    await RunStagedCodegenAsync(
                                        selectedInstruction,
                                        () => stagePlan!,
                                        isBatchMode: false,
                                        enableCodegen: true, // 스탠드얼론 메뉴이므로 강제 활성화
                                        engineName: selectedEngine,
                                        targetProjectDir: jobSpecificSrcDir,
                                        configuration: configuration,
                                        aiClient: aiClient,
                                        cancellationToken: activeCts.Token);
                                    break;

                                case ExistingInstructionsKind.Broken:
                                    AnsiConsole.MarkupLine(
                                        "[red]에러: 회차별 번들로 보이지만 회차 파일 집합이 불완전하거나 일관되지 " +
                                        "않습니다(조립 회차 파일 없음, steps/*.md와 짝이 되는 task-*.md 없음, 또는 " +
                                        "부트스트랩 파일만 빠진 중단된 쓰기). 이 문서는 전체 Job 경로로 실행할 수 " +
                                        "없습니다 - 그 경로는 회차별 지시를 이해하지 못합니다. 통합 배치 마이그레이션 " +
                                        "설계를 다시 실행해 번들을 재생성하십시오.[/]");
                                    break;

                                default: // Legacy
                                    await RunLegacyWholeJobCodegenAsync(
                                        selectedInstruction,
                                        isBatchMode: false,
                                        enableCodegen: true, // 스탠드얼론 메뉴이므로 강제 활성화
                                        engineName: selectedEngine,
                                        targetProjectDir: jobSpecificSrcDir,
                                        configuration: configuration,
                                        aiClient: aiClient,
                                        cancellationToken: activeCts.Token);
                                    break;
                            }

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

                        AnsiConsole.MarkupLine(
                            "[grey]참조 분석을 켜면 참조 객체마다 별도 명세서와 승인 화면이 생기고,[/]");
                        AnsiConsole.MarkupLine(
                            "[grey]루트 SP는 직접 의존성만으로 분석됩니다(하위 SP가 쓰는 테이블 스키마는 루트 컨텍스트에서 제외).[/]");
                        var analyzeSelectedReferences = AnsiConsole.Confirm(
                            "선택한 SP가 참조하는 SP/UDF도 함께 분석하시겠습니까?",
                            analyzeReferencedCodeObjects);

                        var parts = selectedOption.Split('.', 2);
                        var schema = parts[0];
                        var name = parts[1];

                        using var activeCts = new CancellationTokenSource();
                        _currentCts = activeCts;

                        try
                        {
                            var result = await RunConfiguredAnalysisAsync(
                                analyzeSelectedReferences,
                                dependencyAnalysisOrchestrator,
                                orchestrator,
                                dbService,
                                connectionString,
                                database ?? string.Empty,
                                schema,
                                name,
                                maxDepth,
                                provider,
                                modelName,
                                actorEffort,
                                instructions,
                                isBatchMode: false,
                                outputDir,
                                enableCache,
                                allowExternalDatabaseConnections,
                                dependencyArtifactMode,
                                activeCts.Token);

                            // 취소 판정을 명세서 검사보다 먼저 한다(배치 블록과 같은 순서).
                            // ExecutionOrder는 후위 순회라 루트가 마지막에 실행되므로 재귀 취소 시
                            // 루트 명세서는 거의 항상 비어 있고, 순서를 뒤집으면 사용자 Ctrl+C가
                            // "명세서 생성 실패"라는 분석 오류로 오보된다.
                            // 완료분은 오케스트레이터가 이미 저장했으므로(PartialCancelled ⟹ 재귀 ⟹
                            // Persistence != NotAttempted) 여기서 빠져나가도 잃는 산출물이 없다.
                            // 정지 없이 continue하면 메뉴와 SP 목록이 방금 낸 부분 완료 패널을 밀어낸다.
                            if (result.Completion == GraphCompletion.PartialCancelled)
                            {
                                AnsiConsole.MarkupLine("\n[yellow]분석 작업이 사용자에 의해 중단되었습니다. 메인 메뉴로 돌아갑니다.[/]");
                                AnsiConsole.WriteLine();
                                AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 계속합니다...[/]");
                                Console.ReadKey(true);
                                continue;
                            }

                            var specMarkdown = result.SpecMarkdown;
                            if (string.IsNullOrEmpty(specMarkdown))
                            {
                                AnsiConsole.MarkupLine("[red]분석이 중단되었거나 명세서 생성에 실패했습니다.[/]");
                                continue;
                            }

                            if (!Directory.Exists(outputDir))
                            {
                                Directory.CreateDirectory(outputDir);
                            }

                            // 재귀 경로는 오케스트레이터가 이미 저장했다(Persistence != NotAttempted).
                            if (result.Persistence == ArtifactPersistence.NotAttempted)
                            {
                                await SaveRawArtifactsAsync(
                                    result.Definition, outputDir, instructionsFile, metadataExporter,
                                    saveRawJson, saveRawContext, saveRawFiles, schema, name);

                                if (!result.FromCache)
                                {
                                    // 분석과 전환 분리 요구에 따라, 개별 분석 시에는 배치 전환 설계서를 생성하지 않음
                                    // (SaveMigrationPlanAsync를 부르지 않는다)
                                    await SaveDocumentsAsync(
                                        specMarkdown, outputDir, schema, name,
                                        provider, modelName, result.Review, result.Outcome,
                                        result.ThinkingText, actorEffort, result.Scope);
                                }
                            }

                            RenderAnalysisResultPanel(selectedOption, outputDir, schema, name, result);
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

                        var specFiles = BatchStepCatalog.FindStepCandidates(outputDir);
                        if (specFiles.Count == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉터리에 기분석된 프로시저 명세서(Spec.md)가 존재하지 않습니다. UDF와 Job 산출물은 배치 스텝이 될 수 없습니다.[/]");
                            continue;
                        }

                        var selectedFiles = new List<string>();
                        var remainingFiles = new List<string>();
                        remainingFiles.AddRange(specFiles);

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
                            // fileName은 outputDir 기준 상대 경로(예:
                            // "Procedures/dbo.USP_X/docs/Spec.md")라서 마지막 세그먼트가
                            // 항상 "Spec.md"다. 그대로 쓰면 목차 커버리지 검사와 AI
                            // 프롬프트의 "Filename:" 레이블이 명세서를 구분하지 못한다.
                            // 식별자는 selectedFiles를 만든 BatchStepCatalog.FindStepCandidates와
                            // 같은 판정(ExtractProcedureIdentifier)으로 뽑는다 — selectedFiles가
                            // 바로 그 메서드의 결과에서 골라졌으므로 여기서 null이 나올 수
                            // 없지만, 방어적으로 fileName 자체로 대체한다.
                            var specFileName = BatchStepCatalog.ExtractProcedureIdentifier(fileName) ?? fileName;
                            specsData.Add((specFileName, content));
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
                            // 정의를 계획 수립 앞에서 읽는다. 목차 보강이 이 정적 분석을
                            // 쓰기 때문이고, 부수 효과로 메타데이터 누락 경고가 수십 분짜리
                            // 계획 수립 전에 뜬다 - 종전에는 계획이 다 끝난 뒤에야 그 SP가
                            // 지시서에서 빠진다는 사실을 알렸다.
                            var loadResult = await BatchStepCatalog.LoadDefinitionsAsync(
                                outputDir, selectedFiles, activeCts.Token);
                            var spDefs = loadResult.Definitions.ToList();

                            foreach (var missing in loadResult.MissingMetadata)
                            {
                                AnsiConsole.MarkupLine(
                                    $"[yellow]경고: {Markup.Escape(missing)} 의 메타데이터(raw/metadata.json)가 없어 지시서에서 제외됩니다(참조 테이블 스키마와 Spec.md 링크 모두 누락되며, 해당 배치 스텝은 구현 대상에서 빠집니다). 해당 SP를 1번 메뉴로 다시 분석하면 채워집니다.[/]");
                            }

                            foreach (var failed in loadResult.FailedToParse)
                            {
                                AnsiConsole.MarkupLine(
                                    $"[yellow]경고: {Markup.Escape(failed)} 의 메타데이터를 읽지 못해 지시서에서 제외됩니다(참조 테이블 스키마와 Spec.md 링크 모두 누락되며, 해당 배치 스텝은 구현 대상에서 빠집니다).[/]");
                            }

                            var pipelineResult = await orchestrator.RunConsolidatedPipelineAsync(specsData, targetLanguage, jobName, provider, outputDir, definitions: spDefs, cancellationToken: activeCts.Token);
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
                            await File.WriteAllTextAsync(
                                planFileName,
                                VerificationDocumentFormatter.FormatVerifiedDocument(
                                    consolidatedPlan,
                                    pipelineResult.Review,
                                    pipelineResult.Outcome,
                                    provider,
                                    modelName,
                                    consolidatorEffort,
                                    DateTime.Now,
                                    scope: null,
                                    coverage: pipelineResult.Coverage));

                            if (aiResult != null)
                            {
                                // 추론 본문이 비어도 쓴다. 두 산출물은 한 쌍이라, 한쪽만 나가면
                                // 채택된 시도가 무엇을 사고했는지 되짚을 길이 사라진다.
                                await File.WriteAllTextAsync(
                                    Path.Combine(docsDir, "Thinking.md"),
                                    ThinkingLogDocument.Compose(
                                        aiResult.ThinkingText, provider, modelName, consolidatorEffort, DateTime.Now));
                                var rawContext = $"=== [System Prompt] ===\n{aiResult.SystemPrompt}\n\n=== [User Prompt] ===\n{aiResult.UserPrompt}";
                                await File.WriteAllTextAsync(Path.Combine(rawDir, "prompt-context.md"), rawContext);
                            }
                            AnsiConsole.Write(new Panel(new Markup($"[green]통합 배치 설계서가 성공적으로 생성되었습니다![/]\n[bold]저장 경로:[/] {Markup.Escape(planFileName)}"))
                            {
                                Border = BoxBorder.Rounded,
                                Header = new PanelHeader($" {jobName} 통합 마이그레이션 완료 ")
                            });


                            // SpDefinition들은 위에서 계획 수립 전에 이미 로드했다. 여기서
                            // 재사용해 같은 파일을 두 번 읽지 않는다.
                            try
                            {
                                AnsiConsole.MarkupLine($"\n[yellow]{jobName}[/] - 통합 마이그레이션 지시서 생성 중...");
                                var bundle = await metadataExporter.ExportConsolidatedMigrationInstructionsAsync(
                                    spDefs,
                                    consolidatedPlan,
                                    pipelineResult.Outcome,
                                    jobName,
                                    jobsOutputDir,
                                    targetLanguage,
                                    new OutputPathResolver(resolvedDatabase, outputDir),
                                    pipelineResult.Layout,
                                    pipelineResult.Coverage,
                                    activeCts.Token);

                                foreach (var warning in bundle.Warnings)
                                {
                                    AnsiConsole.MarkupLine($"[yellow]경고: {Markup.Escape(warning)}[/]");
                                }

                                AnsiConsole.MarkupLine(
                                    $"[green]통합 마이그레이션 지시서 번들이 성공적으로 생성되었습니다![/]\n[bold]저장 경로:[/] {Markup.Escape(bundle.EntryPointPath)}");

                                // 외부 코딩 에이전트(Codegen) 기동
                                var jobSpecificSrcDir = Path.Combine(jobsOutputDir, "src");
                                await RunCodegenEngineAsync(
                                    bundle,
                                    isBatchMode: false,
                                    enableCodegen: isCodegenEnabled,
                                    engineName: selectedEngine,
                                    targetProjectDir: jobSpecificSrcDir,
                                    configuration: configuration,
                                    aiClient: aiClient,
                                    cancellationToken: activeCts.Token);
                            }
                            // 이 안쪽 catch가 취소를 먼저 소비하면, 바깥 try의
                            // catch (OperationCanceledException)가 영영 도달하지 못한다.
                            // 사용자의 Ctrl-C가 무시되고 흐름이 메인 메뉴로 그냥 떨어진다.
                            catch (Exception ex) when (ex is not OperationCanceledException)
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

                            // 이 문서는 SettlementPolicyService가 AI 결과를 그대로 반환한 것이며
                            // L1도 L2도 거치지 않는다. 검증 파이프라인 산출물과 같은 형식의
                            // 헤더를 쓰되, 검증되지 않았다는 사실을 명시한다.
                            await File.WriteAllTextAsync(
                                rulebookPath,
                                VerificationDocumentFormatter.FormatUnverifiedDocument(
                                    rulebook, null, provider, modelName, actorEffort, DateTime.Now));
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
            // 이 안쪽의 어떤 catch도 취소를 가로채지 못했을 때의 마지막 정류장이다.
            // 이게 없으면 배치 모드처럼 돌아갈 메뉴가 없는 경로에서 Ctrl-C가
            // .NET의 날것 스택 트레이스와 의도치 않은 종료 코드로 끝나 버린다.
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[yellow]작업이 사용자에 의해 취소되었습니다.[/]");
            }
            finally
            {
                Serilog.Log.CloseAndFlush();
            }
        }


        public static async Task<SpAnalysisOutcome> RunConfiguredAnalysisAsync(
            bool analyzeReferencedCodeObjects,
            IDependencyAnalysisOrchestrator dependencyAnalysisOrchestrator,
            VerificationPipelineOrchestrator verificationPipelineOrchestrator,
            IDbMetadataService metadataService,
            string connectionString,
            string configuredDatabase,
            string schema,
            string name,
            int maxDepth,
            string provider,
            string modelName,
            string? actorEffort,
            string instructions,
            bool isBatchMode,
            string outputDirectory,
            bool enableCache,
            bool allowExternalDatabaseConnections,
            DependencyArtifactMode dependencyArtifactMode,
            CancellationToken cancellationToken)
        {
            if (!analyzeReferencedCodeObjects)
            {
                // 참조분석 OFF 경로. 단일 객체 파이프라인은 저장을 하지 않으므로
                // 결과의 Persistence는 NotAttempted이고, 저장 책임은 호출부에 남는다.
                // 키 조립은 VerificationPipelineOrchestrator의 단일 헬퍼에 둔다.
                // 여기와 테스트가 각자 사본을 가지면 테스트가 프로덕션 경로를 지키지 못한다.
                var singleObjectKey = VerificationPipelineOrchestrator.CreateProcedureKey(
                    connectionString, schema, name);
                var pipelineResult = await verificationPipelineOrchestrator.RunCodeObjectPipelineAsync(
                    connectionString,
                    singleObjectKey,
                    maxDepth,
                    provider,
                    instructions,
                    isBatchMode,
                    outputDirectory,
                    enableCache,
                    cancellationToken);

                return SpAnalysisOutcome.FromSingleObjectPipeline(pipelineResult);
            }

            var database = await ResolveAnalysisDatabaseAsync(
                connectionString,
                configuredDatabase,
                metadataService,
                cancellationToken);
            var rootKey = CodeObjectKey.Create(database, schema, name, CodeObjectType.Procedure);
            var result = await dependencyAnalysisOrchestrator.AnalyzeAsync(
                rootKey,
                new DependencyAnalysisRequest
                {
                    ConnectionString = connectionString,
                    MaxDepth = maxDepth,
                    Provider = provider,
                    ModelName = modelName,
                    ActorEffort = actorEffort,
                    Instructions = instructions,
                    IsBatchMode = isBatchMode,
                    OutputDirectory = outputDirectory,
                    EnableCache = enableCache,
                    AllowExternalDatabaseConnections = allowExternalDatabaseConnections,
                    DependencyArtifactMode = dependencyArtifactMode
                },
                cancellationToken);

            RenderAnalysisDiagnostics(result);

            return SpAnalysisOutcome.FromDependencyGraph(result, rootKey);
        }

        private static async Task<string> ResolveAnalysisDatabaseAsync(
            string connectionString,
            string configuredDatabase,
            IDbMetadataService metadataService,
            CancellationToken cancellationToken)
        {
            try
            {
                var initialCatalog = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
                if (!string.IsNullOrWhiteSpace(initialCatalog))
                {
                    return initialCatalog;
                }
            }
            catch (ArgumentException)
            {
                // 오프라인 모드는 연결 문자열이 없을 수 있습니다.
            }

            try
            {
                var metadataDatabase = await metadataService.GetCurrentDatabaseNameAsync(
                    connectionString,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(metadataDatabase))
                {
                    return metadataDatabase;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(
                    ex,
                    "분석 대상 데이터베이스 조회 실패 - 설정값으로 폴백: {ConfiguredDatabase}",
                    configuredDatabase);
            }

            return configuredDatabase;
        }

        /// <summary>
        /// `--sp`·`--policy`로 지목한 이름 중 DB에 없는 것이 하나라도 있으면 오류로 끝낸다.
        /// 이름을 찍어 넘긴 것은 명백한 의도이므로 "경고 후 나머지만 진행"은 맞지 않다 -
        /// 실측(2026-08-23)에서 약칭 하나가 노란 경고 한 줄과 종료 코드 0으로 건너뛰어져
        /// 재생성이 조용히 빠졌다. 계속 진행해도 되면 true, 종료해야 하면 false.
        /// </summary>
        private static bool ReportUnmatchedTargets(IReadOnlyList<string> unmatched)
        {
            if (unmatched.Count == 0) return true;

            AnsiConsole.MarkupLine(
                $"[red]에러: 지목한 SP {unmatched.Count}개를 DB에서 찾을 수 없습니다: " +
                $"{Markup.Escape(string.Join(", ", unmatched))}[/]");
            AnsiConsole.MarkupLine("[red]약칭·부분 이름은 맞추지 않습니다. 스키마를 뺀 전체 이름(예: UP_UTIL_SETTLE_PROC_ETC) 또는 스키마.이름으로 다시 지정하십시오.[/]");
            Environment.ExitCode = 1;
            return false;
        }

        /// <summary>
        /// 그래프 분석 중 사용자가 알아야 할 사실을 모두 화면에 낸다.
        /// 실패 노드만 보여주던 기존 렌더러는 스킵·부분 완료·저장 실패를 놓쳤다.
        /// </summary>
        private static void RenderAnalysisDiagnostics(CodeObjectPipelineResult result)
        {
            foreach (var node in result.Nodes.Where(node => node.Status == AnalysisNodeStatus.Failed))
            {
                var objectName = $"{node.Key.Schema}.{node.Key.Name}";
                var error = string.IsNullOrWhiteSpace(node.Error) ? "알 수 없는 오류" : node.Error;
                AnsiConsole.MarkupLine($"[yellow]경고:[/] {Markup.Escape(objectName)} 분석 실패 - {Markup.Escape(error)}");
                AnsiConsole.WriteLine();
            }

            foreach (var group in result.Nodes
                .Where(node => node.Status is AnalysisNodeStatus.SkippedDepth or AnalysisNodeStatus.SkippedExternal)
                .GroupBy(node => node.Status))
            {
                // 조사는 라벨에 포함시킨다. "외부 객체"는 받침이 없어 "으로"가 붙으면 어색하다.
                var label = group.Key == AnalysisNodeStatus.SkippedDepth ? "깊이 제한으로" : "외부 객체로";
                AnsiConsole.MarkupLine($"[grey]안내:[/] {label} {group.Count()}개 객체를 분석하지 않았습니다.");
            }

            if (result.Completion == GraphCompletion.PartialCancelled)
            {
                var succeeded = result.Nodes.Count(node => node.Status == AnalysisNodeStatus.Succeeded);
                var unpersisted = result.Nodes
                    .Where(node => node.Status != AnalysisNodeStatus.Succeeded)
                    .Select(node => $"{node.Key.Schema}.{node.Key.Name}")
                    .OrderBy(objectName => objectName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var shown = string.Join(", ", unpersisted.Take(10));
                var suffix = unpersisted.Count > 10 ? $" 외 {unpersisted.Count - 10}건" : string.Empty;

                AnsiConsole.Write(new Panel(new Markup(
                    "[yellow]사용자 취소로 분석이 중단되었습니다.[/]\n" +
                    $"[bold]완료:[/] {succeeded} / [bold]발견:[/] {result.Nodes.Count} 객체\n" +
                    $"[bold]저장되지 않은 객체:[/] {Markup.Escape(shown)}{suffix}"))
                {
                    Border = BoxBorder.Rounded,
                    Header = new PanelHeader(" 부분 완료 ")
                });
            }

            if (result.Persistence == ArtifactPersistence.Failed)
            {
                foreach (var error in result.PersistenceErrors)
                {
                    AnsiConsole.MarkupLine($"[red]저장 실패:[/] {Markup.Escape(error)}");
                }

                AnsiConsole.WriteLine();
            }
        }

        /// <summary>
        /// 원천 산출물(raw/*)을 저장한다. 캐시 히트에도 실행한다 — raw는 타임스탬프를
        /// 담지 않아 거짓 주장을 만들 수 없고, SaveRawJson을 뒤늦게 켠 사용자에게
        /// metadata.json이 영영 생기지 않는 함정을 막는다.
        /// </summary>
        private static async Task SaveRawArtifactsAsync(
            ReSet.Core.Models.SpDefinition? spDef,
            string outputDir,
            string instructionsFile,
            IMetadataExporter metadataExporter,
            bool saveRawJson,
            bool saveRawContext,
            bool saveRawFiles,
            string schema,
            string name)
        {
            if (spDef == null)
            {
                return;
            }

            var spOutputDir = Path.Combine(outputDir, "Procedures", $"{schema}.{name}");
            Directory.CreateDirectory(spOutputDir);

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

        /// <summary>
        /// 캐시에서 나올 수 있는 문서(Spec.md/Thinking.md)를 저장한다. 캐시 히트면 호출하지 않는다 —
        /// 파일이 이미 그 내용이고, 다시 쓰면 분석하지 않은 날짜가 찍힌다.
        /// 배치 전환 계획 설계서는 이 게이트에 걸리지 않는다(SaveMigrationPlanAsync 참조).
        /// </summary>
        private static async Task SaveDocumentsAsync(
            string specMarkdown,
            string outputDir,
            string schema,
            string name,
            string provider,
            string modelName,
            ReviewResult? review,
            VerificationOutcome outcome,
            string? thinkingText,
            string? effort,
            AnalysisScope scope)
        {
            var docsDir = Path.Combine(outputDir, "Procedures", $"{schema}.{name}", "docs");
            Directory.CreateDirectory(docsDir);

            await File.WriteAllTextAsync(
                Path.Combine(docsDir, "Spec.md"),
                VerificationDocumentFormatter.FormatVerifiedDocument(
                    specMarkdown,
                    review,
                    outcome,
                    provider,
                    modelName,
                    effort,
                    DateTime.Now,
                    scope));

            try
            {
                // 기존 .txt 파일이 있다면 삭제 처리
                var oldTxtFile = Path.Combine(docsDir, "Thinking.txt");
                if (File.Exists(oldTxtFile))
                {
                    try { File.Delete(oldTxtFile); } catch {}
                }

                // 추론 본문이 비어도 쓴다 — 본문이 없다는 사실 자체가 기록할 정보다.
                await File.WriteAllTextAsync(
                    Path.Combine(docsDir, "Thinking.md"),
                    ThinkingLogDocument.Compose(thinkingText, provider, modelName, effort, DateTime.Now));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]추론 로그(Thinking Log) 저장 중 경고:[/] {Markup.Escape(ex.Message)}");
            }
        }

        /// <summary>
        /// 배치 전환 계획 설계서를 저장한다. 캐시 게이트 밖에서 호출한다 —
        /// 계획서는 이번 실행이 방금 만든 산출물이라 캐시에서 나올 수 없고,
        /// 명세서의 캐시 상태에 묶으면 만들어 놓고 버리는 결과가 된다.
        /// </summary>
        private static async Task SaveMigrationPlanAsync(
            string migrationPlan,
            string outputDir,
            string schema,
            string name,
            string provider,
            string modelName,
            VerificationOutcome sourceOutcome,
            string? effort)
        {
            var docsDir = Path.Combine(outputDir, "Procedures", $"{schema}.{name}", "docs");
            Directory.CreateDirectory(docsDir);

            // 이 계획서는 GenerateBatchMigrationPlanAsync가 만든 그대로이며 L1도 L2도
            // 거치지 않는다. 명세서의 점수를 여기에 실으면 계획서가 그 점수를 받은
            // 것처럼 읽히므로, 검증 없음을 밝히고 근거 명세서의 상태만 전달한다.
            await File.WriteAllTextAsync(
                Path.Combine(docsDir, "BatchMigrationPlan.md"),
                VerificationDocumentFormatter.FormatUnverifiedDocument(
                    migrationPlan, sourceOutcome, provider, modelName, effort, DateTime.Now));
        }

        /// <summary>
        /// 분석 종료 후 사용자에게 낼 최종 패널. 저장이 실패했으면 성공을 주장하지 않는다.
        /// </summary>
        private static void RenderAnalysisResultPanel(
            string selectedOption,
            string outputDir,
            string schema,
            string name,
            SpAnalysisOutcome result)
        {
            if (result.Persistence == ArtifactPersistence.Failed)
            {
                var detail = result.PersistenceErrors.Count > 0
                    ? string.Join("\n", result.PersistenceErrors)
                    : "상세 사유가 기록되지 않았습니다.";
                AnsiConsole.Write(new Panel(new Markup(
                    $"[red]산출물 저장에 실패했습니다.[/]\n{Markup.Escape(detail)}"))
                {
                    Border = BoxBorder.Rounded,
                    Header = new PanelHeader($" {Markup.Escape(selectedOption)} 저장 실패 ")
                });
                return;
            }

            // 부분 완료 패널은 RenderAnalysisDiagnostics가 이미 냈다.
            if (result.Completion == GraphCompletion.PartialCancelled)
            {
                return;
            }

            var specPath = Path.Combine(outputDir, "Procedures", $"{schema}.{name}", "docs", "Spec.md");
            var cacheNote = result.FromCache
                ? result.AnalyzedAt is { } analyzedAt
                    ? $"\n[grey]캐시 재사용 (원본 분석: {analyzedAt:yyyy-MM-dd HH:mm:ss})[/]"
                    : "\n[grey]캐시 재사용 (원본 분석 시각 불명)[/]"
                : string.Empty;

            AnsiConsole.Write(new Panel(new Markup(
                $"[green]성공적으로 파일이 생성되었습니다![/]\n[bold]저장 경로:[/] {Markup.Escape(specPath)}{cacheNote}"))
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader($" {Markup.Escape(selectedOption)} 분석 완료 ")
            });
        }

        /// <summary>
        /// 회차별 경로(RunStagedWorkflowAsync)와 예전 전체 Job 경로(RunSelfHealingWorkflowAsync)가
        /// 공유하는 준비 단계. 활성화 여부 확인 → 코딩 엔진 생성 → 지시서 파일 존재 확인 →
        /// MaxL2Attempts 파싱 → Validator/Orchestrator 구성까지는 두 경로가 완전히 같고,
        /// 실제로 갈라지는 지점은 "어느 Run*Async를 부르고 결과를 어떻게 보고하는가" 하나뿐이다.
        /// 그 지점만 <paramref name="executeAsync"/>로 넘겨, 셋업을 두 번 유지보수하지 않는다.
        /// </summary>
        private static async Task RunCodegenAsync(
            string entryPointPath,
            bool isBatchMode,
            bool enableCodegen,
            string? engineName,
            string targetProjectDir,
            IConfiguration configuration,
            IAiClient aiClient,
            CancellationToken cancellationToken,
            Func<CodegenWorkflowOrchestrator, string, string, string, CancellationToken, Task> executeAsync)
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
                var engine = factory.CreateEngine(engineName ?? "claude", isBatchMode);

                if (!File.Exists(entryPointPath))
                {
                    AnsiConsole.MarkupLine($"[red]에러: 마이그레이션 지시서 파일({Path.GetFileName(entryPointPath)})을 찾을 수 없습니다.[/]");
                    return;
                }

                var agentDirInfo = Directory.GetParent(entryPointPath);
                var agentDir = agentDirInfo?.FullName ?? "";
                var jobName = agentDirInfo?.Parent?.Name ?? "Unknown";
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

                // 총 시도 상한. MaxL2Attempts가 "unlimited"여도 넘지 못하는 바닥이므로
                // "unlimited"를 받지 않는다 - 받으면 이 설정의 존재 이유가 사라진다.
                var maxTotalAttempts = 20;
                if (int.TryParse(configuration["AiSettings:MaxTotalAttempts"], out int parsedTotal) && parsedTotal >= 1)
                {
                    maxTotalAttempts = parsedTotal;
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
                var orchestrator = new CodegenWorkflowOrchestrator(engine, codeVerificationOrchestrator, metadataExporter, maxL2Attempts, maxTotalAttempts);

                AnsiConsole.MarkupLine($"[grey]지시서 경로: {entryPointPath}[/]");
                AnsiConsole.MarkupLine($"[grey]타겟 프로젝트 디렉터리: {targetProjectDir}[/]");

                await executeAsync(orchestrator, jobName, agentDir, specDir, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // 상위에서 잡도록 던짐
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"\n[red]외부 코딩 에이전트 실행 중 오류 발생:[/] {Markup.Escape(ex.Message)}");

                // 팩토리의 배치 거부(예: agy의 빈 BatchArguments)도 이 catch로 떨어진다 -
                // 화면에는 "보이지만" 종료 코드는 여전히 0이었다. 무인 배치에서는 그 코드가
                // CI가 보는 유일한 신호다.
                if (isBatchMode)
                {
                    Environment.ExitCode = 1;
                }
            }
        }

        /// <summary>
        /// 지시서 번들을 방금 새로 쓴 두 호출부(배치/대화형 통합 지시서 생성 직후)가 부르는
        /// 회차별 실행 경로다.
        ///
        /// bundle.TaskFilePaths가 회차 목록의 유일한 근거다 - agentDir을 다시 뒤져
        /// task-*.md를 찾거나 순서를 추측하지 않는다(CodegenStagePlan.FromBundle 참고).
        /// </summary>
        private static async Task RunCodegenEngineAsync(
            BundleResult bundle,
            bool isBatchMode,
            bool enableCodegen,
            string? engineName,
            string targetProjectDir,
            IConfiguration configuration,
            IAiClient aiClient,
            CancellationToken cancellationToken)
        {
            // CodegenStagePlan.FromBundle 호출을 여기서 미리 해 버리면 공유 try(RunCodegenAsync)
            // 밖에서 예외가 날 수 있다 - 배치 모드에서 ExitCode=1을 세우는 지점을 우회하게
            // 되므로(리뷰에서 지적된 결함), 회차 계획 구성 자체를 지연 실행 델리게이트로
            // 넘겨 RunCodegenAsync의 try 안에서 계산되게 한다.
            await RunStagedCodegenAsync(
                bundle.EntryPointPath,
                () => CodegenStagePlan.FromBundle(bundle, Path.GetDirectoryName(bundle.EntryPointPath)!),
                isBatchMode, enableCodegen, engineName,
                targetProjectDir, configuration, aiClient, cancellationToken);
        }

        /// <summary>
        /// 회차 계획(방금 쓴 번들에서 왔든, 메뉴 3이 디스크에서 되짚었든)을 순서대로 순차
        /// 기동하고 결과를 보고한다. <see cref="RunCodegenEngineAsync"/>와
        /// <see cref="TryClassifyExistingInstructionsFile"/> 양쪽이 이 메서드로 모인다 -
        /// 회차 계획을 어떻게 얻었는지와 무관하게 실행·보고 로직은 하나여야 한다.
        ///
        /// <paramref name="buildStagePlan"/>은 값이 아니라 지연 실행 델리게이트다 - 계획을
        /// 만드는 과정에서 예외가 나도 RunCodegenAsync의 공유 try 안에서 잡혀 배치 모드
        /// ExitCode=1이 정상적으로 세팅되게 하기 위해서다.
        /// </summary>
        private static async Task RunStagedCodegenAsync(
            string entryPointPath,
            Func<CodegenStagePlan> buildStagePlan,
            bool isBatchMode,
            bool enableCodegen,
            string? engineName,
            string targetProjectDir,
            IConfiguration configuration,
            IAiClient aiClient,
            CancellationToken cancellationToken)
        {
            await RunCodegenAsync(
                entryPointPath, isBatchMode, enableCodegen, engineName, targetProjectDir,
                configuration, aiClient, cancellationToken,
                async (orchestrator, jobName, agentDir, _, ct) =>
                {
                    var stagePlan = buildStagePlan();

                    AnsiConsole.MarkupLine("[yellow]외부 프로세스 기동 중... (회차별 순차 실행)[/]\n");

                    var staged = await orchestrator.RunStagedWorkflowAsync(
                        jobName, stagePlan, agentDir, targetProjectDir, isBatchMode, ct);

                    if (staged.AbortReason != null)
                    {
                        AnsiConsole.MarkupLine($"[red]코드 생성 중단: {Markup.Escape(staged.AbortReason)}[/]");

                        // 무인 배치에서 종료 코드 0으로 끝나면 CI가 이 중단을 초록으로 읽는다.
                        // RunLegacyWholeJobCodegenAsync가 실패 시 이미 지키던 원칙과 같다.
                        if (isBatchMode)
                        {
                            Environment.ExitCode = 1;
                        }
                    }
                    // AllPassed로 성패를 가른다(FailedStepCodes.Count만 보지 않는다). FailedStepCodes는
                    // StepCode가 있는 회차(단계)만 담는다(AgentProgressStore) - 조립 회차만 실패하면
                    // (StepCode 없음) 그 카운트가 0인 채로 AllPassed만 false가 되어, 개수만 보면
                    // "모든 회차 통과"로 잘못 보고된다.
                    else if (!staged.AllPassed)
                    {
                        if (staged.FailedStepCodes.Count > 0)
                        {
                            AnsiConsole.MarkupLine(
                                $"[yellow]코드 생성 완료 — 검증을 통과하지 못한 단계 {staged.FailedStepCodes.Count}개: " +
                                $"{Markup.Escape(string.Join(", ", staged.FailedStepCodes))}[/]");

                            // 실패 사유는 둘로 갈린다: (a) 대조할 단계 설계서(steps/{code}.md)가
                            // 있었는데 검증에서 걸러진 것 - 계획대로 "제외"된 게 맞다. (b) 애초에
                            // StepSpecPath가 없어(계획이 단일 파일로 폴백했거나, 메뉴 3이 디스크에서
                            // 회차를 되짚었는데 steps/*.md가 없어) 검증을 시도조차 못 한 것 - 이건
                            // "제외"가 아니다. 하나로 뭉뚱그리면 (b)를 (a)로 오해하게 만든다.
                            var noSpecCodes = stagePlan.Stages
                                .Where(s => s.Kind == StageKind.Step && s.StepSpecPath == null
                                    && s.StepCode != null && staged.FailedStepCodes.Contains(s.StepCode))
                                .Select(s => s.StepCode!)
                                .ToList();
                            var excludedCodes = staged.FailedStepCodes.Except(noSpecCodes).ToList();

                            if (excludedCodes.Count > 0)
                            {
                                AnsiConsole.MarkupLine(
                                    $"[yellow]이 중 검증에서 걸러진 {excludedCodes.Count}개는 파이프라인에서 제외되었으므로 " +
                                    $"최종 빌드가 깨져 있을 수 있습니다: {Markup.Escape(string.Join(", ", excludedCodes))}[/]");
                            }

                            if (noSpecCodes.Count > 0)
                            {
                                AnsiConsole.MarkupLine(
                                    $"[yellow]이 중 {noSpecCodes.Count}개는 대조할 단계 설계서가 없어 애초에 검증을 시도하지 " +
                                    $"못했습니다(계획 분할 실패 또는 회차 파일 누락) - 최종 산출물을 직접 확인하세요: " +
                                    $"{Markup.Escape(string.Join(", ", noSpecCodes))}[/]");
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine(
                                "[yellow]코드 생성 완료 — 조립 회차가 검증을 통과하지 못했습니다. 최종 산출물을 확인하세요.[/]");
                        }

                        if (isBatchMode)
                        {
                            Environment.ExitCode = 1;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[green]코드 생성 완료 — 모든 회차가 검증을 통과했습니다.[/]");
                    }
                });
        }

        /// <summary>
        /// 대화형 메뉴 3번("기작성된 지시서 재구동")이 쓰는 예전 전체 Job 단일 기동 경로다.
        ///
        /// 골라낸 지시서가 새 번들 형식이 아닐 때만(<see cref="TryClassifyExistingInstructionsFile"/>가
        /// Legacy로 판정했을 때만) 이 경로를 탄다. 이 함수의 동작은 이 작업(Task 15)에서
        /// 바꾸지 않는다 - 기존 전체 Job 워크플로 경로는 그대로 둔다.
        /// </summary>
        private static async Task RunLegacyWholeJobCodegenAsync(
            string instructionsPath,
            bool isBatchMode,
            bool enableCodegen,
            string? engineName,
            string targetProjectDir,
            IConfiguration configuration,
            IAiClient aiClient,
            CancellationToken cancellationToken)
        {
            await RunCodegenAsync(
                instructionsPath, isBatchMode, enableCodegen, engineName, targetProjectDir,
                configuration, aiClient, cancellationToken,
                async (orchestrator, jobName, _, specDir, ct) =>
                {
                    AnsiConsole.MarkupLine("[yellow]외부 프로세스 기동 중... (TDD 로컬 빌드 및 자가수정 루프)[/]\n");

                    var workflowResult = await orchestrator.RunSelfHealingWorkflowAsync(
                        jobOrSpName: jobName,
                        instructionsFilePath: instructionsPath,
                        specDir: specDir,
                        codeDir: targetProjectDir,
                        isBatchMode: isBatchMode,
                        cancellationToken: ct);

                    if (workflowResult.Succeeded)
                    {
                        AnsiConsole.MarkupLine("\n[bold green]✔ 코딩 에이전트 자가 수정 루프 통과 (MATCH)[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("\n[bold red]❌ 코딩 에이전트 검증 완전 통과 실패. (최종 결과 확인 요망)[/]");

                        // 무인 배치에서는 화면이 유일한 창구다. 중단 사유를 로그에만 두지 않는다.
                        if (!string.IsNullOrWhiteSpace(workflowResult.AbortReason))
                        {
                            AnsiConsole.MarkupLine($"[red]중단 사유:[/] {Markup.Escape(workflowResult.AbortReason)}");
                        }

                        // 무인 배치에서 종료 코드 0으로 끝나면 CI가 이 실패를 초록으로 읽는다.
                        // 이 함수는 원래 실패를 삼키고 화면에만 찍었다 - Program.cs:166-168의
                        // 원칙(빈 산출물이 종료 코드 0으로 성공 취급되면 안 된다)이 여기도 적용된다.
                        // 대화형 모드는 사용자가 화면을 직접 보므로 건드리지 않는다.
                        if (isBatchMode)
                        {
                            Environment.ExitCode = 1;
                        }
                    }
                });
        }

        /// <summary>메뉴 3이 디스크에서 고른 지시서 파일이 새 번들 형식인지 판정한 결과.</summary>
        private enum ExistingInstructionsKind
        {
            /// <summary>Task 8 이전 형식의 단일 문서. RunLegacyWholeJobCodegenAsync 대상.</summary>
            Legacy,

            /// <summary>새 번들이고 회차 계획을 디스크에서 온전히 되짚었다. RunStagedCodegenAsync 대상.</summary>
            Staged,

            /// <summary>새 번들로 보이는데(task-00-bootstrap.md 있음) 회차 파일 집합이 불완전하다.
            /// 전체 Job 경로로 떨어뜨리면 안 된다 - 이 문서는 "다른 Step을 읽지 마십시오"라고
            /// 지시하므로, 그 지시를 이해하지 못하는 전체 Job 경로에 먹이면 안 된다.</summary>
            Broken,
        }

        /// <summary>
        /// InstructionEntryPointComposer가 분할 여부와 무관하게 모든 진입점에 무조건 쓰는
        /// 문구. task-00-bootstrap.md의 유무만으로 Legacy를 판정하면, 번들 쓰기가 진입점과
        /// 부트스트랩 작업 사이에서 끊기거나 사용자가 그 파일 하나만 지웠을 때도
        /// Legacy로 떨어져 전체 Job 경로로 잘못 보내진다 - 이 문구가 있으면 그 반례를 잡아
        /// Broken으로 돌린다(Legacy로 잘못 판정하는 것보다 거부하는 쪽이 안전하다).
        ///
        /// 문구를 여기 다시 적지 않고 작성기의 상수를 그대로 읽는다. 손으로 복사해 두면
        /// 작성기 쪽 문장을 다듬는 순간 판별이 조용히 멈추고 b336ee5가 막은 오라우팅이
        /// 되살아나는데, ReSet.Cli에는 그것을 잡아 줄 테스트 프로젝트가 없다.
        /// </summary>
        private const string StagedEntryPointMarker = InstructionEntryPointComposer.StagedBundleMarker;

        /// <summary>
        /// 메뉴 3("기작성된 지시서 재구동")이 디스크에서 고른 MigrationInstructions.md를 분류하고,
        /// 새 번들이면 회차 계획을 함께 되짚는다.
        ///
        /// 새 번들과 옛 단일 문서는 파일명이 같아 내용을 보지 않고는 구분되지 않는다.
        /// agent/ 직하에 task-00-bootstrap.md가 있는지를 1차 판정으로 쓴다 -
        /// InstructionBundleWriter가 항상 함께 쓰는 파일이라 존재만으로 새 번들임을 확정할
        /// 수 있다. 없을 때는 곧바로 Legacy로 단정하지 않고 진입점 내용을 본다
        /// (<see cref="StagedEntryPointMarker"/> 참고) - 새 번들 표식이 있는데 부트스트랩
        /// 파일만 없으면 Legacy가 아니라 Broken이다.
        ///
        /// 새 번들로 판정됐는데 조립 회차 파일(task-99-assembly.md)이 없으면 Broken이다 -
        /// 이 경우 호출자는 예전 전체 Job 경로로 떨어지면 안 된다. 그 경로는 이미 회차별로
        /// 쪼개져 "배정된 task-*.md만 읽고 다른 Step은 읽지 마십시오"라고 지시하는 문서를
        /// 이해하지 못한 채 통째로 떠먹여지기 때문이다(리뷰에서 지적된 결함 - 회차용 문서를
        /// 전체 Job 경로에 먹이는 조합 자체를 만들지 않는다).
        ///
        /// <b>회차 집합은 agent/ 직하의 task-*.md를 통째로 훑어 세지 않고, steps/*.md를
        /// 기준으로 거슬러 올라간다.</b> InstructionBundleWriter는 번들을 다시 쓸 때
        /// agent/ 직하의 task-*.md를 정리하지 않지만(progress.json·todo.md·에이전트
        /// 산출물이 그 자리에 살아, Task 7에서 의도적으로 남겨 두기로 정했다 - 그 결정은
        /// 여기서 건드리지 않는다), steps/ 아래는 매번 지금 목차에 없는 파일을 지운다
        /// (CleanupStaleStepFiles, 폴백 전환 시 통째 삭제). 그래서 이전 실행보다 단계가
        /// 줄었거나(예: 10단계 → 3단계) 이번이 폴백(0단계)이면, agent/ 직하에는 예전 회차의
        /// task-*.md가 그대로 남아 있어도 steps/에는 그 흔적이 없다. task-*.md를 통째로
        /// 훑어 회차를 세면 이 낡은 파일까지 진짜 회차로 세어, 유료 코딩 에이전트를 이미
        /// 지워진 단계 지시서로 기동하고 조립 회차 지시서에 가짜 "제외 목록"까지 얹게
        /// 된다(리뷰에서 지적된 결함). steps/를 기준으로 삼으면 이 문제가 구조적으로
        /// 성립하지 않는다 - steps/에 없는 코드는 애초에 후보에 오르지 않는다.
        ///
        /// steps/{코드}.md와 짝이 되는 task-*.md를 찾지 못하면(쓰기 도중 중단 등으로 번들이
        /// 일관되지 않은 상태) Broken이다 - 낡은 파일로 대충 짝짓지 않는다. 회차 순서는
        /// task-*.md 파일명에 박힌 서수({ordinal:D2})를 숫자로 파싱해 정렬한다 - 문자열
        /// 정렬(task-00- vs task-100-)은 두 자리를 벗어나는 순간 사전순과 회차순이
        /// 어긋나므로 쓰지 않는다. 서수를 못 읽거나(task-<서수>-<코드> 형식을 벗어난 이름)
        /// StepCode가 없는 파일은 TaskFileComposer.ParseStageIdentity가 이미 걸러
        /// 색인에서 자동으로 빠진다 - 우연한 phantom 회차가 생기지 않는다.
        ///
        /// steps/{코드}.md와 task-NN-{코드}.md는 같은 정화 결과를 파일명으로 쓴다
        /// (InstructionBundleWriter가 TaskFileComposer.SanitizeStepCode를 함께 쓴다).
        /// 그래서 두 이름을 그대로 대조하면 되고, 정화가 코드를 바꾸는 정상 번들이
        /// 이 대조에서 Broken으로 거부되던 막다른 길도 함께 사라진다. steps/에서 읽은
        /// 코드를 StepCode로 쓰는 것은 CodegenStagePlan.FromBundle과 같은 값이다.
        /// </summary>
        private static (ExistingInstructionsKind Kind, CodegenStagePlan? StagePlan) TryClassifyExistingInstructionsFile(
            string entryPointPath)
        {
            var agentDir = Path.GetDirectoryName(entryPointPath);
            if (agentDir == null)
            {
                return (ExistingInstructionsKind.Legacy, null);
            }

            var bootstrapPath = Path.Combine(agentDir, "task-00-bootstrap.md");
            if (!File.Exists(bootstrapPath))
            {
                if (File.Exists(entryPointPath) &&
                    File.ReadAllText(entryPointPath).Contains(StagedEntryPointMarker, StringComparison.Ordinal))
                {
                    return (ExistingInstructionsKind.Broken, null);
                }

                return (ExistingInstructionsKind.Legacy, null);
            }

            var assemblyPath = Path.Combine(agentDir, "task-99-assembly.md");
            if (!File.Exists(assemblyPath))
            {
                return (ExistingInstructionsKind.Broken, null);
            }

            // Step 회차 task-*.md만 정화 코드로 색인한다. 이름이 회차 규약을 벗어나거나
            // (task-<서수>-<코드> 형태가 아니거나) 서수를 숫자로 못 읽는 파일은 후보에서
            // 자동으로 빠진다.
            var stepTaskFilesByCode = new List<(string SanitizedCode, int Ordinal, string TaskPath)>();
            foreach (var taskPath in Directory.GetFiles(agentDir, "task-*.md"))
            {
                var baseName = Path.GetFileNameWithoutExtension(taskPath);
                var identity = TaskFileComposer.ParseStageIdentity(baseName);
                if (identity.Kind != StageKind.Step || identity.StepCode == null)
                {
                    continue;
                }

                var parts = baseName.Split('-');
                if (parts.Length < 2 || !int.TryParse(parts[1], out var ordinal))
                {
                    continue; // 서수를 못 읽는 이름 - 색인하지 않는다(=phantom 회차 방지).
                }

                stepTaskFilesByCode.Add((identity.StepCode, ordinal, taskPath));
            }

            var stepsDir = Path.Combine(agentDir, "steps");
            var stepSpecFiles = Directory.Exists(stepsDir) ? Directory.GetFiles(stepsDir, "*.md") : Array.Empty<string>();

            var matchedSteps = new List<(int Ordinal, string Code, string TaskPath, string SpecPath)>();
            foreach (var specPath in stepSpecFiles)
            {
                var code = Path.GetFileNameWithoutExtension(specPath);
                var match = stepTaskFilesByCode.FirstOrDefault(e =>
                    string.Equals(e.SanitizedCode, code, StringComparison.OrdinalIgnoreCase));

                if (match.TaskPath == null)
                {
                    // steps/에는 있는데 짝이 되는 task-*.md가 없다 - 번들이 일관되지 않다.
                    return (ExistingInstructionsKind.Broken, null);
                }

                matchedSteps.Add((match.Ordinal, code, match.TaskPath, specPath));
            }

            var bootstrapIdentity = TaskFileComposer.ParseStageIdentity(Path.GetFileNameWithoutExtension(bootstrapPath));
            var assemblyIdentity = TaskFileComposer.ParseStageIdentity(Path.GetFileNameWithoutExtension(assemblyPath));

            var stages = new List<CodegenStage>
            {
                new(bootstrapIdentity.Id, StageKind.Bootstrap, bootstrapPath, null, null),
            };

            foreach (var step in matchedSteps.OrderBy(s => s.Ordinal))
            {
                var identity = TaskFileComposer.ParseStageIdentity(Path.GetFileNameWithoutExtension(step.TaskPath));
                stages.Add(new CodegenStage(identity.Id, StageKind.Step, step.TaskPath, step.Code, step.SpecPath));
            }

            stages.Add(new CodegenStage(assemblyIdentity.Id, StageKind.Assembly, assemblyPath, null, null));

            return (ExistingInstructionsKind.Staged, new CodegenStagePlan(stages));
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

    /// <summary>
    /// 재귀 분석 하위 파이프라인의 진행 상태를 원래 대화형 UI에 위임합니다.
    /// 사람 검토가 필요한 대화형 L3 단계도 원래 UI에 위임합니다.
    /// </summary>
    internal sealed class RecursiveAnalysisUserInteraction : IVerificationUserInteraction
    {
        private readonly IVerificationUserInteraction _interactiveUserInteraction;

        public RecursiveAnalysisUserInteraction(IVerificationUserInteraction interactiveUserInteraction)
        {
            _interactiveUserInteraction = interactiveUserInteraction;
        }

        public void NotifyStatus(string message) => _interactiveUserInteraction.NotifyStatus(message);
        public void NotifyError(string message) => _interactiveUserInteraction.NotifyError(message);
        public void NotifyWarnings(string selectedOption, List<string> warnings) => _interactiveUserInteraction.NotifyWarnings(selectedOption, warnings);
        public void NotifyCatalogMismatches(string jobName, List<string> mismatches) => _interactiveUserInteraction.NotifyCatalogMismatches(jobName, mismatches);
        public void NotifyL1Errors(string selectedOption, int attempt, int maxAttempts, List<string> errors) => _interactiveUserInteraction.NotifyL1Errors(selectedOption, attempt, maxAttempts, errors);
        public void NotifyL2Defects(string selectedOption, int attempt, int maxAttempts, string feedbackComment) => _interactiveUserInteraction.NotifyL2Defects(selectedOption, attempt, maxAttempts, feedbackComment);
        public void NotifyValidationSuccess(string selectedOption) => _interactiveUserInteraction.NotifyValidationSuccess(selectedOption);

        public Task<HumanReviewResult> RequestHumanReviewAsync(
            string selectedOption,
            string specificationMarkdown,
            VerificationOutcome outcome,
            bool structureRedraftSupported = false,
            IReadOnlyList<BatchStepPlan>? steps = null) =>
            _interactiveUserInteraction.RequestHumanReviewAsync(
                selectedOption, specificationMarkdown, outcome, structureRedraftSupported, steps);

        public Task<bool> ConfirmMetadataSyncAsync(string selectedOption) =>
            _interactiveUserInteraction.ConfirmMetadataSyncAsync(selectedOption);

        public IMultiProgressScope CreateProgressScope(string title) =>
            _interactiveUserInteraction.CreateProgressScope(title);
    }

    }
}
