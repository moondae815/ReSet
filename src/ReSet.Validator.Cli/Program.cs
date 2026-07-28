using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients;
using ReSet.Validator.Core.Abstractions;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Services;

namespace ReSet.Validator.Cli
{
    public class ValidatorCliArgs
    {
        public string? SpecDirectory { get; set; }
        public string? SourceCodeDirectory { get; set; }
        public string? TargetLanguage { get; set; }
        public bool IsBatchMode { get; set; }
        public bool GenInputs { get; set; }
        public bool GenMockData { get; set; }
        public bool ExecLegacy { get; set; }
        public bool ExecTarget { get; set; }
        public bool CompareData { get; set; }
        public string? ConnectionString { get; set; }
        public string? TargetSp { get; set; }
    }

    public class Program
    {
        private static CancellationTokenSource? _currentCts;

        public static ValidatorCliArgs ParseCommandLineArgs(string[] args)
        {
            var cliArgs = new ValidatorCliArgs();

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if ((arg.Equals("--spec", StringComparison.OrdinalIgnoreCase) || arg.Equals("--spec-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    cliArgs.SpecDirectory = args[++i];
                }
                else if ((arg.Equals("--code", StringComparison.OrdinalIgnoreCase) || arg.Equals("--code-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    cliArgs.SourceCodeDirectory = args[++i];
                }
                else if (arg.Equals("--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cliArgs.TargetLanguage = args[++i];
                }
                else if (arg.Equals("--batch", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.IsBatchMode = true;
                }
                else if (arg.Equals("--gen-inputs", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.GenInputs = true;
                }
                else if (arg.Equals("--gen-mock-data", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.GenMockData = true;
                }
                else if (arg.Equals("--exec-legacy", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.ExecLegacy = true;
                }
                else if (arg.Equals("--exec-target", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.ExecTarget = true;
                }
                else if (arg.Equals("--compare-data", StringComparison.OrdinalIgnoreCase))
                {
                    cliArgs.CompareData = true;
                }
                else if (arg.Equals("--conn", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cliArgs.ConnectionString = args[++i];
                }
                else if (arg.Equals("--sp", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    cliArgs.TargetSp = args[++i];
                }
            }

            return cliArgs;
        }

        static async Task Main(string[] args)
        {
            // Ctrl+C 처리
            Console.CancelKeyPress += (sender, e) =>
            {
                if (_currentCts != null && !_currentCts.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine("\n[red]사용자 요청에 의해 검증 작업이 취소되었습니다. 정리 중...[/]");
                    _currentCts.Cancel();
                    e.Cancel = true;
                }
            };

            using var globalCts = new CancellationTokenSource();
            _currentCts = globalCts;

            AnsiConsole.Write(new FigletText("ReSet Validator").Color(Color.Green));
            AnsiConsole.MarkupLine("[bold green]=== REverse engineering SETtlement Validator ===[/]");
            AnsiConsole.WriteLine();

            // 1. CLI 아규먼트 분석
            var cliArgs = ParseCommandLineArgs(args);

            // 2. 설정 로드
            var configuration = LoadConfiguration();

            // 3. 검증 환경 설정
            var validatorConfig = GetValidatorConfig(configuration, cliArgs);
            var ui = new ConsoleUserInteraction();

            // TUI 대화형 모드인 경우 유효하지 않은 디렉토리에 대해 사용자 재요청 및 Job 선택 지원
            if (!cliArgs.IsBatchMode)
            {
                var slnRoot = FindSolutionRoot();
                var jobsBaseDir = slnRoot != null ? Path.Combine(slnRoot, "output", "Jobs") : "./output/Jobs";

                if (Directory.Exists(jobsBaseDir))
                {
                    var jobs = Directory.GetDirectories(jobsBaseDir)
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrEmpty(name))
                        .Cast<string>()
                        .ToList();

                    if (jobs.Any())
                    {
                        var customOption = "<-- 사용자 지정 경로 직접 입력 -->";
                        var jobChoices = new List<string>(jobs) { customOption };

                        string selectedJob = customOption;
                        if (AnsiConsole.Profile.Capabilities.Interactive)
                        {
                            selectedJob = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title("검증할 [green]통합 배치 작업(Job)[/]을 선택해 주세요:")
                                    .UseConverter(name => Markup.Escape(name))
                                    .AddChoices(jobChoices)
                            );
                        }
                        else
                        {
                            selectedJob = jobs.First(); // 샌드박스에서 자동 선택
                        }

                        if (selectedJob != customOption)
                        {
                            validatorConfig.SpecDirectory = Path.Combine(jobsBaseDir, selectedJob, "docs");
                            validatorConfig.SourceCodeDirectory = Path.Combine(jobsBaseDir, selectedJob, "src");
                            validatorConfig.OutputDirectory = Path.Combine(jobsBaseDir, selectedJob, "validation");
                        }
                    }
                }

                if (!Directory.Exists(validatorConfig.SpecDirectory))
                {
                    AnsiConsole.MarkupLine($"[yellow]설정된 설계서 디렉토리가 존재하지 않습니다: {Markup.Escape(validatorConfig.SpecDirectory)}[/]");
                    var defaultSpecDir = slnRoot != null 
                        ? Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Combine(slnRoot, "output")) 
                        : "./output";
                    var choices = GetDirectoryChoices(slnRoot);
                    validatorConfig.SpecDirectory = ui.PromptDirectoryPath("설계서(Spec.md)가 위치한 올바른 디렉토리 경로를 입력해 주세요:", defaultSpecDir, choices);
                }

                if (!Directory.Exists(validatorConfig.SourceCodeDirectory))
                {
                    AnsiConsole.MarkupLine($"[yellow]설정된 소스코드 디렉토리가 존재하지 않습니다: {Markup.Escape(validatorConfig.SourceCodeDirectory)}[/]");
                    var defaultSrcDir = slnRoot != null 
                        ? Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Combine(slnRoot, "src")) 
                        : "./src";
                    var choices = GetDirectoryChoices(slnRoot);
                    validatorConfig.SourceCodeDirectory = ui.PromptDirectoryPath("구현된 소스코드가 위치한 올바른 디렉토리 경로를 입력해 주세요:", defaultSrcDir, choices);
                }
            }

            AnsiConsole.MarkupLine($"[grey]설계서 디렉토리: {Markup.Escape(validatorConfig.SpecDirectory)}[/]");
            AnsiConsole.MarkupLine($"[grey]소스코드 디렉토리: {Markup.Escape(validatorConfig.SourceCodeDirectory)}[/]");
            AnsiConsole.MarkupLine($"[grey]보고서 출력 경로: {Markup.Escape(validatorConfig.OutputDirectory)}[/]");
            AnsiConsole.MarkupLine($"[grey]설정 언어 모드: {Markup.Escape(validatorConfig.TargetLanguage)}[/]");

            // 4. AI 서비스 연동 설정
            var provider = configuration["AiSettings:Provider"] ?? "OpenAI";
            var modelName = configuration["AiSettings:ModelName"] ?? "gpt-4o";
            
            // 기존 ReSet.Cli appsettings.local.json이 있을 경우 API Key를 가져오기 위한 대체 탐색 적용
            var apiKey = LoadApiKeyWithFallback(configuration, provider);
            var endpoint = configuration[$"AiSettings:Providers:{provider}:Endpoint"] ?? string.Empty;

            if (string.IsNullOrEmpty(apiKey) && !provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[red]에러: {provider} AI 클라이언트를 구동하기 위한 API Key가 설정되어 있지 않습니다.[/]");
                AnsiConsole.MarkupLine("[yellow]src/ReSet.Validator.Cli/appsettings.local.json 에 ApiKey를 지정해 주세요.[/]");
                return;
            }

            var timeoutSeconds = 300;
            if (int.TryParse(configuration["AiSettings:TimeoutSeconds"], out int parsedTimeout) && parsedTimeout > 0)
            {
                timeoutSeconds = parsedTimeout;
            }
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

            IAiClient aiClient;
            try
            {
                aiClient = AiClientFactory.CreateClient(provider, modelName, apiKey, endpoint, httpClient);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]AI 클라이언트 생성 실패: {Markup.Escape(ex.Message)}[/]");
                return;
            }

            // 5. 오케스트레이터 및 개별 서비스 구성
            var orchestrator = new CodeVerificationOrchestrator(validatorConfig, aiClient, ui);
            var aiService = new ValidatorAiService(aiClient);
            var execService = new SpExecutionService();
            var compareService = new DataComparisonService();

            if (cliArgs.IsBatchMode)
            {
                try
                {
                    if (cliArgs.GenInputs)
                    {
                        await RunBatchGenInputs(validatorConfig, aiService, cliArgs.TargetSp, globalCts.Token);
                    }
                    else if (cliArgs.GenMockData)
                    {
                        await RunBatchGenMockData(validatorConfig, aiService, cliArgs.TargetSp, globalCts.Token);
                    }
                    else if (cliArgs.ExecLegacy)
                    {
                        var connStr = cliArgs.ConnectionString ?? Environment.GetEnvironmentVariable("SP_ANALYZER_CONN_STR") ?? LoadConnectionStringFromConfig(configuration);
                        await RunBatchExecLegacy(validatorConfig, execService, connStr, cliArgs.TargetSp, globalCts.Token);
                    }
                    else if (cliArgs.ExecTarget)
                    {
                        var connStr = cliArgs.ConnectionString ?? Environment.GetEnvironmentVariable("SP_ANALYZER_CONN_STR") ?? LoadConnectionStringFromConfig(configuration);
                        await RunBatchExecTarget(validatorConfig, connStr, cliArgs.TargetSp, globalCts.Token);
                    }
                    else if (cliArgs.CompareData)
                    {
                        await RunBatchCompareData(validatorConfig, compareService, cliArgs.TargetSp);
                    }
                    else
                    {
                        await orchestrator.RunVerificationAsync(true, globalCts.Token);
                        AnsiConsole.MarkupLine("\n[bold green]배치 검증 작업 완료![/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.WriteException(ex);
                }
            }
            else
            {
                // TUI 대화식 메뉴 루프
                while (true)
                {
                    AnsiConsole.WriteLine();
                    var choice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold white]원하시는 작업을 선택해 주세요:[/]")
                            .AddChoices(new[]
                            {
                                "1. 설계서 대비 소스코드 논리 일치성 검증 (Code Validation)",
                                "2. 데이터 정합성 대조용 테스트 파라미터 설계 (Test Design)",
                                "3. 테스트용 모의 데이터(Mock Data) 생성 및 적재 (Data Seeding)",
                                "4. 레거시 시스템(SQL Server) 실행 결과 수집 (Legacy Run)",
                                "5. 타겟 시스템(마이그레이션 소스) 실행 결과 수집 (Target Run)",
                                "6. 양단 간 데이터 정합성 1:1 대조 보고서 생성 (Data Compare)",
                                "7. 프로그램 종료 (Exit)"
                            }));

                    if (choice.StartsWith("7")) break;

                    try
                    {
                        if (choice.StartsWith("1"))
                        {
                            await orchestrator.RunVerificationAsync(false, globalCts.Token);
                        }
                        else if (choice.StartsWith("2"))
                        {
                            await RunInteractiveGenInputs(validatorConfig, aiService, globalCts.Token);
                        }
                        else if (choice.StartsWith("3"))
                        {
                            await RunInteractiveGenMockData(validatorConfig, aiService, globalCts.Token);
                        }
                        else if (choice.StartsWith("4"))
                        {
                            var connStr = cliArgs.ConnectionString ?? Environment.GetEnvironmentVariable("SP_ANALYZER_CONN_STR") ?? await PromptForConnectionStringAsync(configuration);
                            await RunInteractiveExecLegacy(validatorConfig, execService, connStr, globalCts.Token);
                        }
                        else if (choice.StartsWith("5"))
                        {
                            var connStr = cliArgs.ConnectionString ?? Environment.GetEnvironmentVariable("SP_ANALYZER_CONN_STR") ?? await PromptForConnectionStringAsync(configuration);
                            await RunInteractiveExecTarget(validatorConfig, connStr, globalCts.Token);
                        }
                        else if (choice.StartsWith("6"))
                        {
                            await RunInteractiveCompareData(validatorConfig, compareService);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        AnsiConsole.MarkupLine("\n[yellow]작업이 취소되었습니다.[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.WriteException(ex);
                    }
                }
            }
        }

        private static string? FindSolutionRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "ReSet.slnx")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir)!;
            }

            // Fallback: Current Working Directory
            dir = Directory.GetCurrentDirectory();
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "ReSet.slnx")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir)!;
            }

            return null;
        }

        private static IConfiguration LoadConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

            var slnRoot = FindSolutionRoot();
            if (slnRoot != null)
            {
                var candidates = new[]
                {
                    Path.Combine(slnRoot, "src", "ReSet.Cli", "appsettings.local.json"),
                    Path.Combine(slnRoot, "src", "ReSet.Cli", "bin", "Debug", "net10.0", "appsettings.local.json"),
                    Path.Combine(slnRoot, "appsettings.local.json")
                };

                foreach (var path in candidates)
                {
                    if (File.Exists(path))
                    {
                        builder.AddJsonFile(Path.GetFullPath(path), optional: true, reloadOnChange: true);
                    }
                }
            }

            return builder.AddEnvironmentVariables().Build();
        }

        private static ValidatorConfig GetValidatorConfig(IConfiguration configuration, ValidatorCliArgs cliArgs)
        {
            var config = new ValidatorConfig();

            // 기본값 설정 (TUI 메뉴에서 덮어쓰여지거나 CLI 인자로 입력됨)
            config.SpecDirectory = "";
            config.SourceCodeDirectory = "";
            config.OutputDirectory = "";
            config.TargetLanguage = configuration["ValidationSettings:TargetLanguage"] ?? "Auto";

            var maxL2AttemptsRaw = configuration["AiSettings:MaxL2Attempts"] ?? "2";
            int maxL2Attempts = 2;
            if (string.Equals(maxL2AttemptsRaw, "unlimited", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(maxL2AttemptsRaw, "검증 완료까지", StringComparison.OrdinalIgnoreCase) ||
                maxL2AttemptsRaw == "-1")
            {
                maxL2Attempts = -1;
            }
            else if (int.TryParse(maxL2AttemptsRaw, out int parsed))
            {
                maxL2Attempts = parsed;
            }
            config.MaxL2Attempts = maxL2Attempts;

            // CLI 인자가 있으면 오버라이드
            if (!string.IsNullOrEmpty(cliArgs.SpecDirectory))
            {
                config.SpecDirectory = cliArgs.SpecDirectory;
            }
            if (!string.IsNullOrEmpty(cliArgs.SourceCodeDirectory))
            {
                config.SourceCodeDirectory = cliArgs.SourceCodeDirectory;
            }
            if (!string.IsNullOrEmpty(cliArgs.TargetLanguage))
            {
                config.TargetLanguage = cliArgs.TargetLanguage;
            }

            // 상대경로 절대경로 보정
            if (!Path.IsPathRooted(config.SpecDirectory))
                config.SpecDirectory = Path.Combine(Directory.GetCurrentDirectory(), config.SpecDirectory);
            if (!Path.IsPathRooted(config.SourceCodeDirectory))
                config.SourceCodeDirectory = Path.Combine(Directory.GetCurrentDirectory(), config.SourceCodeDirectory);
            if (!Path.IsPathRooted(config.OutputDirectory))
                config.OutputDirectory = Path.Combine(Directory.GetCurrentDirectory(), config.OutputDirectory);

            return config;
        }

        private static string LoadApiKeyWithFallback(IConfiguration configuration, string provider)
        {
            // 1. 직접 지정된 API Key
            var key = configuration[$"AiSettings:Providers:{provider}:ApiKey"];
            if (!string.IsNullOrEmpty(key)) return key;

            // 2. 솔루션 루트 기반 동적 탐색
            var slnRoot = FindSolutionRoot();
            if (slnRoot != null)
            {
                var candidates = new[]
                {
                    Path.Combine(slnRoot, "src", "ReSet.Cli", "appsettings.local.json"),
                    Path.Combine(slnRoot, "src", "ReSet.Cli", "bin", "Debug", "net10.0", "appsettings.local.json"),
                    Path.Combine(slnRoot, "appsettings.local.json")
                };

                foreach (var path in candidates)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            var tempConfig = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build();
                            var tempKey = tempConfig[$"AiSettings:Providers:{provider}:ApiKey"];
                            if (!string.IsNullOrEmpty(tempKey)) return tempKey;
                        }
                    }
                    catch { }
                }
            }

            return string.Empty;
        }

        private static List<string> GetDirectoryChoices(string? slnRoot)
        {
            var choices = new List<string>();
            var currentDir = Directory.GetCurrentDirectory();

            if (slnRoot != null)
            {
                // 1. output 및 하위 폴더들 추가
                var outputDir = Path.Combine(slnRoot, "output");
                if (Directory.Exists(outputDir))
                {
                    choices.Add(Path.GetRelativePath(currentDir, outputDir));
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(outputDir))
                        {
                            choices.Add(Path.GetRelativePath(currentDir, sub));
                        }
                    }
                    catch {}
                }

                // 2. src 및 하위 프로젝트 폴더들 추가
                var srcDir = Path.Combine(slnRoot, "src");
                if (Directory.Exists(srcDir))
                {
                    choices.Add(Path.GetRelativePath(currentDir, srcDir));
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(srcDir))
                        {
                            choices.Add(Path.GetRelativePath(currentDir, sub));
                        }
                    }
                    catch {}
                }

                // 3. 솔루션 루트 자체 추가
                choices.Add(Path.GetRelativePath(currentDir, slnRoot));
            }

            choices.Add("./output");
            choices.Add("./src");

            // 중복 제거 및 디렉토리 유효성 검사
            var result = new List<string>();
            foreach (var choice in choices)
            {
                var fullPath = Path.IsPathRooted(choice) ? choice : Path.Combine(currentDir, choice);
                if (Directory.Exists(fullPath))
                {
                    result.Add(choice);
                }
            }

            return result.Distinct().ToList();
        }

        private static string LoadConnectionStringFromConfig(IConfiguration configuration)
        {
            return configuration.GetConnectionString("DefaultConnection") 
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? configuration["DbSettings:ConnectionString"] 
                ?? string.Empty;
        }

        private static async Task<string> PromptForConnectionStringAsync(IConfiguration configuration)
        {
            var defaultConn = LoadConnectionStringFromConfig(configuration);
            
            AnsiConsole.MarkupLine("[yellow]DB 연결 문자열이 설정되지 않았거나 새로 입력해야 합니다.[/]");
            if (!string.IsNullOrEmpty(defaultConn))
            {
                AnsiConsole.MarkupLine($"기본 연결 문자열: [grey]{Markup.Escape(defaultConn)}[/]");
            }

            var prompt = new TextPrompt<string>("SQL Server 연결 문자열을 입력하세요:")
                .PromptStyle("green");

            if (!string.IsNullOrEmpty(defaultConn))
            {
                prompt.DefaultValue(defaultConn);
            }

            return await Task.Run(() => AnsiConsole.Prompt(prompt));
        }

        // --- Interactive Gen Inputs ---
        private static async Task RunInteractiveGenInputs(ValidatorConfig config, ValidatorAiService aiService, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("\n[bold blue]=== 2. 데이터 정합성 검증용 테스트 파라미터 설계 (AI) ===[/]");
            
            var specFiles = Directory.GetFiles(config.SpecDirectory, "Spec.md", SearchOption.AllDirectories);
            if (specFiles.Length == 0)
            {
                AnsiConsole.MarkupLine($"[red]에러: 설계서 디렉토리({Markup.Escape(config.SpecDirectory)})에 'Spec.md' 파일이 존재하지 않습니다.[/]");
                return;
            }

            var fileChoices = specFiles.Select(f => Directory.GetParent(f)?.Parent?.Name ?? Path.GetFileName(f)).ToList();
            var selectedSpName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("테스트 파라미터를 설계할 설계서(Spec.md) 파일을 선택하세요:")
                    .PageSize(10)
                    .AddChoices(fileChoices));

            var fullPath = specFiles.First(f => (Directory.GetParent(f)?.Parent?.Name ?? Path.GetFileName(f)) == selectedSpName);
            var spName = selectedSpName;
            var rawJsonPath = Path.Combine(config.SpecDirectory, "Procedures", spName, "raw", "metadata.json");

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"AI가 '{spName}' 설계서를 분석하여 테스트 파라미터를 설계 중입니다...", async ctx =>
                {
                    var specContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
                    var jsonResult = await aiService.GenerateTestParametersAsync(specContent, spName, cancellationToken);

                    if (!Directory.Exists(config.OutputDirectory))
                    {
                        Directory.CreateDirectory(config.OutputDirectory);
                    }

                    var outputPath = Path.Combine(config.OutputDirectory, $"{spName}_test_inputs.json");
                    await File.WriteAllTextAsync(outputPath, jsonResult, System.Text.Encoding.UTF8, cancellationToken);
                    
                    AnsiConsole.MarkupLine($"[green]테스트 파라미터 JSON 생성 완료:[/] {Markup.Escape(outputPath)}");
                });
        }

        // --- Batch Gen Inputs ---
        private static async Task RunBatchGenInputs(ValidatorConfig config, ValidatorAiService aiService, string? targetSp, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[bold blue]=== [Batch] 테스트 파라미터 설계 시작 ===[/]");

            var specFiles = Directory.GetFiles(config.SpecDirectory, "Spec.md", SearchOption.AllDirectories);
            if (!string.IsNullOrEmpty(targetSp))
            {
                specFiles = specFiles.Where(f => (Directory.GetParent(f)?.Parent?.Name ?? Path.GetFileName(f)).StartsWith(targetSp, StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            if (specFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]경고: 대상 설계서 파일을 찾을 수 없습니다.[/]");
                return;
            }

            foreach (var file in specFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var spName = Directory.GetParent(file)?.Parent?.Name ?? Path.GetFileName(file).Replace(".md", "");
                AnsiConsole.MarkupLine($"설계 분석 중: {Markup.Escape(spName)}");
                
                try
                {
                    var specContent = await File.ReadAllTextAsync(file, cancellationToken);
                    var jsonResult = await aiService.GenerateTestParametersAsync(specContent, spName, cancellationToken);

                    if (!Directory.Exists(config.OutputDirectory))
                    {
                        Directory.CreateDirectory(config.OutputDirectory);
                    }

                    var outputPath = Path.Combine(config.OutputDirectory, $"{spName}_test_inputs.json");
                    await File.WriteAllTextAsync(outputPath, jsonResult, System.Text.Encoding.UTF8, cancellationToken);
                    AnsiConsole.MarkupLine($"[green]완료:[/] {Markup.Escape(outputPath)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]오류 ({Markup.Escape(spName)}): {Markup.Escape(ex.Message)}[/]");
                }
            }
        }

        // --- Interactive Exec Legacy ---
        private static async Task RunInteractiveExecLegacy(ValidatorConfig config, SpExecutionService execService, string connectionString, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("\n[bold blue]=== 4. 원본 Stored Procedure 실행 데이터 수집 (Legacy DB) ===[/]");
            
            if (!Directory.Exists(config.OutputDirectory))
            {
                AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉토리가 존재하지 않습니다. 먼저 테스트 파라미터를 생성해 주세요.[/]");
                return;
            }

            var inputFiles = Directory.GetFiles(config.OutputDirectory, "*_test_inputs.json");
            if (inputFiles.Length == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]경고: '{Markup.Escape(config.OutputDirectory)}'에 '*_test_inputs.json' 파일이 없습니다. 2번 메뉴를 통해 먼저 파라미터를 생성하세요.[/]");
                return;
            }

            var fileChoices = inputFiles.Select(f => Path.GetFileName(f)).ToList();
            var selectedFile = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("실행할 테스트 파라미터 JSON 파일을 선택하세요:")
                    .PageSize(10)
                    .AddChoices(fileChoices));

            var fullPath = Path.Combine(config.OutputDirectory, selectedFile);
            var spName = selectedFile.Replace("_test_inputs.json", "");

            var mockPath = Path.Combine(config.OutputDirectory, "mock", $"{spName}_mock_data.json");
            MockDataDto? mockData = null;
            var seedingService = new SandboxSeedingService();

            if (File.Exists(mockPath))
            {
                try
                {
                    var mockJson = await File.ReadAllTextAsync(mockPath, cancellationToken);
                    mockData = JsonSerializer.Deserialize<MockDataDto>(mockJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (mockData != null)
                    {
                        AnsiConsole.MarkupLine("[grey]모의 테이블 데이터(Mock Data)를 데이터베이스에 적재 중...[/]");
                        await seedingService.SeedMockDataAsync(connectionString, mockData);
                        AnsiConsole.MarkupLine("[green]모의 데이터 적재 완료.[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]경고: 모의 데이터 적재 실패 (테스트가 실패하거나 데이터가 부정합할 수 있음): {Markup.Escape(ex.Message)}[/]");
                }
            }

            try
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Legacy DB에서 '{spName}' 프로시저를 테스트 입력값으로 실행 중...", async ctx =>
                    {
                        var testInputsJson = await File.ReadAllTextAsync(fullPath, cancellationToken);
                        var rawResultsJson = await execService.ExecuteStoredProcedureAsync(connectionString, testInputsJson, cancellationToken);

                        var outputPath = Path.Combine(config.OutputDirectory, $"{spName}_legacy_results.json");
                        await File.WriteAllTextAsync(outputPath, rawResultsJson, System.Text.Encoding.UTF8, cancellationToken);
                        
                        AnsiConsole.MarkupLine($"[green]Legacy 결과 수집 완료:[/] {Markup.Escape(outputPath)}");
                    });
            }
            finally
            {
                if (mockData != null)
                {
                    try
                    {
                        AnsiConsole.MarkupLine("[grey]모의 테이블 데이터(Mock Data)를 데이터베이스에서 제거 중...[/]");
                        await seedingService.CleanupMockDataAsync(connectionString, mockData);
                        AnsiConsole.MarkupLine("[green]모의 데이터 제거 완료.[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]오류: 모의 데이터 제거 실패 (수동 정리가 필요할 수 있음): {Markup.Escape(ex.Message)}[/]");
                    }
                }
            }
        }

        // --- Batch Exec Legacy ---
        private static async Task RunBatchExecLegacy(ValidatorConfig config, SpExecutionService execService, string connectionString, string? targetSp, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[bold blue]=== [Batch] Legacy DB 실행 데이터 수집 시작 ===[/]");

            if (!Directory.Exists(config.OutputDirectory))
            {
                AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉토리가 존재하지 않습니다.[/]");
                return;
            }

            var inputFiles = Directory.GetFiles(config.OutputDirectory, "*_test_inputs.json");
            if (!string.IsNullOrEmpty(targetSp))
            {
                inputFiles = inputFiles.Where(f => Path.GetFileName(f).StartsWith(targetSp, StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            if (inputFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]경고: 대상 '*_test_inputs.json' 파일을 찾을 수 없습니다.[/]");
                return;
            }

            foreach (var file in inputFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var spName = Path.GetFileName(file).Replace("_test_inputs.json", "");
                AnsiConsole.MarkupLine($"Legacy 실행 중: {Markup.Escape(spName)}");

                var mockPath = Path.Combine(config.OutputDirectory, "mock", $"{spName}_mock_data.json");
                MockDataDto? mockData = null;
                var seedingService = new SandboxSeedingService();

                if (File.Exists(mockPath))
                {
                    try
                    {
                        var mockJson = await File.ReadAllTextAsync(mockPath, cancellationToken);
                        mockData = JsonSerializer.Deserialize<MockDataDto>(mockJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (mockData != null)
                        {
                            await seedingService.SeedMockDataAsync(connectionString, mockData);
                            AnsiConsole.MarkupLine("[green]모의 데이터 적재 완료.[/]");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]경고: 모의 데이터 적재 실패 ({spName}): {Markup.Escape(ex.Message)}[/]");
                    }
                }

                try
                {
                    var testInputsJson = await File.ReadAllTextAsync(file, cancellationToken);
                    var rawResultsJson = await execService.ExecuteStoredProcedureAsync(connectionString, testInputsJson, cancellationToken);

                    var outputPath = Path.Combine(config.OutputDirectory, $"{spName}_legacy_results.json");
                    await File.WriteAllTextAsync(outputPath, rawResultsJson, System.Text.Encoding.UTF8, cancellationToken);
                    AnsiConsole.MarkupLine($"[green]완료:[/] {Markup.Escape(outputPath)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]오류 ({Markup.Escape(spName)}): {Markup.Escape(ex.Message)}[/]");
                }
                finally
                {
                    if (mockData != null)
                    {
                        try
                        {
                            await seedingService.CleanupMockDataAsync(connectionString, mockData);
                            AnsiConsole.MarkupLine("[green]모의 데이터 제거 완료.[/]");
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]오류 ({spName}): 모의 데이터 제거 실패: {Markup.Escape(ex.Message)}[/]");
                        }
                    }
                }
            }
        }

        // --- Interactive Compare Data ---
        private static async Task RunInteractiveCompareData(ValidatorConfig config, DataComparisonService compareService)
        {
            AnsiConsole.MarkupLine("\n[bold blue]=== 6. 실행 결과 데이터 정합성 1:1 대조 및 보고서 생성 (Compare) ===[/]");

            if (!Directory.Exists(config.OutputDirectory))
            {
                AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉토리가 존재하지 않습니다.[/]");
                return;
            }

            var legacyFiles = Directory.GetFiles(config.OutputDirectory, "*_legacy_results.json");
            if (legacyFiles.Length == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]경고: '{Markup.Escape(config.OutputDirectory)}'에 '*_legacy_results.json' 파일이 없습니다. Legacy 결과를 수집해 주세요.[/]");
                return;
            }

            var choices = new List<string>();
            foreach (var file in legacyFiles)
            {
                var name = Path.GetFileName(file);
                var spName = name.Replace("_legacy_results.json", "");
                
                // 매칭 타겟 파일이 있는지 검사 (보통 *_target_results.json 혹은 *_new_results.json 로 명명)
                var targetFile1 = Path.Combine(config.OutputDirectory, $"{spName}_target_results.json");
                var targetFile2 = Path.Combine(config.OutputDirectory, $"{spName}_new_results.json");

                if (File.Exists(targetFile1) || File.Exists(targetFile2))
                {
                    choices.Add(spName);
                }
                else
                {
                    choices.Add($"{spName} (타겟 결과 파일 없음)");
                }
            }

            var selectedSp = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("대조할 Stored Procedure를 선택해 주세요:")
                    .PageSize(10)
                    .AddChoices(choices));

            if (selectedSp.Contains("(타겟 결과 파일 없음)"))
            {
                AnsiConsole.MarkupLine("[red]에러: 타겟 실행 결과 JSON 파일이 있어야 정합성 비교를 할 수 있습니다.[/]");
                AnsiConsole.MarkupLine($"[grey]주의: '{Markup.Escape(selectedSp.Split(' ')[0])}_target_results.json' 또는 '{Markup.Escape(selectedSp.Split(' ')[0])}_new_results.json' 형식의 파일이 필요합니다.[/]");
                return;
            }

            var spNameClean = selectedSp.Trim();
            var legacyPath = Path.Combine(config.OutputDirectory, $"{spNameClean}_legacy_results.json");
            
            var targetPath = Path.Combine(config.OutputDirectory, $"{spNameClean}_target_results.json");
            if (!File.Exists(targetPath))
            {
                targetPath = Path.Combine(config.OutputDirectory, $"{spNameClean}_new_results.json");
            }

            var legacyJson = await File.ReadAllTextAsync(legacyPath);
            var targetJson = await File.ReadAllTextAsync(targetPath);

            var reportMarkdown = compareService.CompareOutputs(legacyJson, targetJson);

            var reportPath = Path.Combine(config.OutputDirectory, $"{spNameClean}_CompareReport.md");
            await File.WriteAllTextAsync(reportPath, reportMarkdown, System.Text.Encoding.UTF8);

            AnsiConsole.MarkupLine($"[green]정합성 비교 완료! 보고서가 저장되었습니다:[/] {Markup.Escape(reportPath)}");

            // TUI에 요약 표시
            var summaryLines = reportMarkdown.Split('\n');
            var summaryTableLines = summaryLines.SkipWhile(l => !l.Contains("종합 비교 요약")).Skip(2).Take(5);
            
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold white]--- 검증 요약 ---[/]");
            foreach (var line in summaryTableLines)
            {
                if (line.Trim().StartsWith("|"))
                {
                    AnsiConsole.WriteLine(line);
                }
            }
        }

        // --- Batch Compare Data ---
        private static async Task RunBatchCompareData(ValidatorConfig config, DataComparisonService compareService, string? targetSp)
        {
            AnsiConsole.MarkupLine("[bold blue]=== [Batch] 데이터 정합성 1:1 대조 및 보고서 생성 시작 ===[/]");

            if (!Directory.Exists(config.OutputDirectory))
            {
                AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉토리가 존재하지 않습니다.[/]");
                return;
            }

            var legacyFiles = Directory.GetFiles(config.OutputDirectory, "*_legacy_results.json");
            if (!string.IsNullOrEmpty(targetSp))
            {
                legacyFiles = legacyFiles.Where(f => Path.GetFileName(f).StartsWith(targetSp, StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            if (legacyFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]경고: 대상 '*_legacy_results.json' 파일을 찾을 수 없습니다.[/]");
                return;
            }

            foreach (var file in legacyFiles)
            {
                var spName = Path.GetFileName(file).Replace("_legacy_results.json", "");
                var targetPath = Path.Combine(config.OutputDirectory, $"{spName}_target_results.json");
                if (!File.Exists(targetPath))
                {
                    targetPath = Path.Combine(config.OutputDirectory, $"{spName}_new_results.json");
                }

                if (!File.Exists(targetPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]경고: '{Markup.Escape(spName)}'에 대한 타겟 실행 결과 JSON 파일이 없습니다. 스킵합니다.[/]");
                    continue;
                }

                try
                {
                    var legacyJson = await File.ReadAllTextAsync(file);
                    var targetJson = await File.ReadAllTextAsync(targetPath);

                    var reportMarkdown = compareService.CompareOutputs(legacyJson, targetJson);

                    var reportPath = Path.Combine(config.OutputDirectory, $"{spName}_CompareReport.md");
                    await File.WriteAllTextAsync(reportPath, reportMarkdown, System.Text.Encoding.UTF8);

                    AnsiConsole.MarkupLine($"[green]정합성 비교 및 보고서 작성 완료:[/] {Markup.Escape(reportPath)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]오류 ({Markup.Escape(spName)}): {Markup.Escape(ex.Message)}[/]");
                }
            }
        }

        // --- Interactive Exec Target ---
        private static async Task RunInteractiveExecTarget(ValidatorConfig config, string connectionString, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("\n[bold blue]=== 5. 신규 마이그레이션 타겟 소스코드 실행 데이터 수집 (Target System) ===[/]");
            
            if (!Directory.Exists(config.OutputDirectory))
            {
                AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉토리가 존재하지 않습니다. 먼저 테스트 파라미터를 생성해 주세요.[/]");
                return;
            }

            var inputFiles = Directory.GetFiles(config.OutputDirectory, "*_test_inputs.json");
            if (inputFiles.Length == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]경고: '{Markup.Escape(config.OutputDirectory)}'에 '*_test_inputs.json' 파일이 없습니다. 2번 메뉴를 통해 먼저 파라미터를 생성하세요.[/]");
                return;
            }

            var fileChoices = inputFiles.Select(f => Path.GetFileName(f)).ToList();
            var selectedFile = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("실행할 테스트 파라미터 JSON 파일을 선택하세요:")
                    .PageSize(10)
                    .AddChoices(fileChoices));

            var fullPath = Path.Combine(config.OutputDirectory, selectedFile);
            var spName = selectedFile.Replace("_test_inputs.json", "");

            // 매핑되는 소스코드 파일 찾기
            var mappingService = new FileMappingService();
            var mappedPairs = mappingService.ResolveMappings(config);
            var matchedPair = mappedPairs.FirstOrDefault(p => p.MappedName.Equals(spName, StringComparison.OrdinalIgnoreCase));

            if (matchedPair == null || string.IsNullOrEmpty(matchedPair.SourceCodePath))
            {
                AnsiConsole.MarkupLine($"[red]에러: '{spName}'에 대칭되는 소스코드 파일을 찾을 수 없습니다. 경로와 파일명을 확인해 주십시오.[/]");
                return;
            }

            var extension = Path.GetExtension(matchedPair.SourceCodePath).ToLower();
            IRuntimeRunner runner = extension == ".cs" 
                ? new CSharpReflectionRunner() 
                : (IRuntimeRunner)new JavaProcessRunner();

            var mockPath = Path.Combine(config.OutputDirectory, "mock", $"{spName}_mock_data.json");
            MockDataDto? mockData = null;
            var seedingService = new SandboxSeedingService();

            if (File.Exists(mockPath))
            {
                try
                {
                    var mockJson = await File.ReadAllTextAsync(mockPath, cancellationToken);
                    mockData = JsonSerializer.Deserialize<MockDataDto>(mockJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (mockData != null)
                    {
                        AnsiConsole.MarkupLine("[grey]모의 테이블 데이터(Mock Data)를 데이터베이스에 적재 중...[/]");
                        await seedingService.SeedMockDataAsync(connectionString, mockData);
                        AnsiConsole.MarkupLine("[green]모의 데이터 적재 완료.[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]경고: 모의 데이터 적재 실패 (테스트가 실패할 수 있음): {Markup.Escape(ex.Message)}[/]");
                }
            }

            try
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"신규 {runner.SupportedLanguage} 타겟 런타임에서 '{spName}' 코드를 실행 중...", async ctx =>
                    {
                        var testInputsJson = await File.ReadAllTextAsync(fullPath, cancellationToken);
                        var rawResultsJson = await runner.ExecuteAsync(matchedPair.SourceCodePath, testInputsJson, connectionString, cancellationToken);

                        var outputPath = Path.Combine(config.OutputDirectory, $"{spName}_target_results.json");
                        await File.WriteAllTextAsync(outputPath, rawResultsJson, System.Text.Encoding.UTF8, cancellationToken);
                        
                        AnsiConsole.MarkupLine($"[green]Target 결과 수집 완료:[/] {Markup.Escape(outputPath)}");
                    });
            }
            finally
            {
                if (mockData != null)
                {
                    try
                    {
                        AnsiConsole.MarkupLine("[grey]모의 테이블 데이터(Mock Data)를 데이터베이스에서 제거 중...[/]");
                        await seedingService.CleanupMockDataAsync(connectionString, mockData);
                        AnsiConsole.MarkupLine("[green]모의 데이터 제거 완료.[/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]오류: 모의 데이터 제거 실패 (수동 정리가 필요할 수 있음): {Markup.Escape(ex.Message)}[/]");
                    }
                }
            }
        }

        // --- Batch Exec Target ---
        private static async Task RunBatchExecTarget(ValidatorConfig config, string connectionString, string? targetSp, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[bold blue]=== [Batch] 신규 타겟 소스코드 실행 데이터 수집 시작 ===[/]");

            if (!Directory.Exists(config.OutputDirectory))
            {
                AnsiConsole.MarkupLine("[yellow]경고: 출력 디렉토리가 존재하지 않습니다.[/]");
                return;
            }

            var inputFiles = Directory.GetFiles(config.OutputDirectory, "*_test_inputs.json");
            if (!string.IsNullOrEmpty(targetSp))
            {
                inputFiles = inputFiles.Where(f => Path.GetFileName(f).StartsWith(targetSp, StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            if (inputFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]경고: 대상 '*_test_inputs.json' 파일을 찾을 수 없습니다.[/]");
                return;
            }

            var mappingService = new FileMappingService();
            var mappedPairs = mappingService.ResolveMappings(config);

            foreach (var file in inputFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var spName = Path.GetFileName(file).Replace("_test_inputs.json", "");
                AnsiConsole.MarkupLine($"Target 실행 중: {Markup.Escape(spName)}");

                var matchedPair = mappedPairs.FirstOrDefault(p => p.MappedName.Equals(spName, StringComparison.OrdinalIgnoreCase));
                if (matchedPair == null || string.IsNullOrEmpty(matchedPair.SourceCodePath))
                {
                    AnsiConsole.MarkupLine($"[yellow]경고: '{spName}'에 대칭되는 소스코드 파일을 찾을 수 없어 건너뜁니다.[/]");
                    continue;
                }

                var mockPath = Path.Combine(config.OutputDirectory, "mock", $"{spName}_mock_data.json");
                MockDataDto? mockData = null;
                var seedingService = new SandboxSeedingService();

                if (File.Exists(mockPath))
                {
                    try
                    {
                        var mockJson = await File.ReadAllTextAsync(mockPath, cancellationToken);
                        mockData = JsonSerializer.Deserialize<MockDataDto>(mockJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (mockData != null)
                        {
                            await seedingService.SeedMockDataAsync(connectionString, mockData);
                            AnsiConsole.MarkupLine("[green]모의 데이터 적재 완료.[/]");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]경고: 모의 데이터 적재 실패 ({spName}): {Markup.Escape(ex.Message)}[/]");
                    }
                }

                try
                {
                    var extension = Path.GetExtension(matchedPair.SourceCodePath).ToLower();
                    IRuntimeRunner runner = extension == ".cs" 
                        ? new CSharpReflectionRunner() 
                        : (IRuntimeRunner)new JavaProcessRunner();

                    var testInputsJson = await File.ReadAllTextAsync(file, cancellationToken);
                    var rawResultsJson = await runner.ExecuteAsync(matchedPair.SourceCodePath, testInputsJson, connectionString, cancellationToken);

                    var outputPath = Path.Combine(config.OutputDirectory, $"{spName}_target_results.json");
                    await File.WriteAllTextAsync(outputPath, rawResultsJson, System.Text.Encoding.UTF8, cancellationToken);
                    AnsiConsole.MarkupLine($"[green]완료:[/] {Markup.Escape(outputPath)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]오류 ({Markup.Escape(spName)}): {Markup.Escape(ex.Message)}[/]");
                }
                finally
                {
                    if (mockData != null)
                    {
                        try
                        {
                            await seedingService.CleanupMockDataAsync(connectionString, mockData);
                            AnsiConsole.MarkupLine("[green]모의 데이터 제거 완료.[/]");
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]오류 ({spName}): 모의 데이터 제거 실패: {Markup.Escape(ex.Message)}[/]");
                        }
                    }
                }
            }
        }

        // --- Interactive Gen Mock Data ---
        private static async Task RunInteractiveGenMockData(ValidatorConfig config, ValidatorAiService aiService, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("\n[bold blue]=== 3. 검증용 모의 테이블 데이터(Mock Data) 자동 생성 및 캐싱 (AI) ===[/]");
            
            var specFiles = Directory.GetFiles(config.SpecDirectory, "Spec.md", SearchOption.AllDirectories);
            if (specFiles.Length == 0)
            {
                AnsiConsole.MarkupLine($"[red]에러: 설계서 디렉토리({Markup.Escape(config.SpecDirectory)})에 'Spec.md' 파일이 존재하지 않습니다.[/]");
                return;
            }

            var fileChoices = specFiles.Select(f => Directory.GetParent(f)?.Parent?.Name ?? Path.GetFileName(f)).ToList();
            var selectedSpName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("모의 데이터를 생성할 대상 설계서(Spec.md) 파일을 선택하세요:")
                    .PageSize(10)
                    .AddChoices(fileChoices));

            var fullPath = specFiles.First(f => (Directory.GetParent(f)?.Parent?.Name ?? Path.GetFileName(f)) == selectedSpName);
            var spName = selectedSpName;
            
            var rawJsonPath = Path.Combine(config.SpecDirectory, "Procedures", spName, "raw", "metadata.json");
            if (!File.Exists(rawJsonPath))
            {
                AnsiConsole.MarkupLine($"[red]에러: '{spName}'의 원본 메타데이터 JSON 파일({Markup.Escape(rawJsonPath)})이 존재하지 않습니다. 먼저 SP 분석을 수행해 주십시오.[/]");
                return;
            }

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"AI가 '{spName}'의 구조와 의존성을 분석하여 모의 테이블 데이터를 생성 중입니다...", async ctx =>
                {
                    var specContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
                    var rawJsonContent = await File.ReadAllTextAsync(rawJsonPath, cancellationToken);
                    
                    using var doc = JsonDocument.Parse(rawJsonContent);
                    var root = doc.RootElement;
                    var procedureDdl = root.GetProperty("DdlText").GetString() ?? "";
                    
                    string dependenciesJson = "";
                    if (root.TryGetProperty("Dependencies", out var depsProp))
                    {
                        dependenciesJson = depsProp.GetRawText();
                    }

                    var mockDataJson = await aiService.GenerateMockTableDataAsync(specContent, procedureDdl, dependenciesJson, cancellationToken);

                    var mockOutputDir = Path.Combine(config.OutputDirectory, "mock");
                    if (!Directory.Exists(mockOutputDir))
                    {
                        Directory.CreateDirectory(mockOutputDir);
                    }

                    var outputPath = Path.Combine(mockOutputDir, $"{spName}_mock_data.json");
                    await File.WriteAllTextAsync(outputPath, mockDataJson, System.Text.Encoding.UTF8, cancellationToken);
                    
                    AnsiConsole.MarkupLine($"[green]모의 테이블 데이터(Mock Data) 캐시 완료:[/] {Markup.Escape(outputPath)}");
                });
        }

        // --- Batch Gen Mock Data ---
        private static async Task RunBatchGenMockData(ValidatorConfig config, ValidatorAiService aiService, string? targetSp, CancellationToken cancellationToken)
        {
            AnsiConsole.MarkupLine("[bold blue]=== [Batch] 검증용 모의 테이블 데이터(Mock Data) 자동 생성 시작 ===[/]");

            var specFiles = Directory.GetFiles(config.SpecDirectory, "Spec.md", SearchOption.AllDirectories);
            if (!string.IsNullOrEmpty(targetSp))
            {
                specFiles = specFiles.Where(f => (Directory.GetParent(f)?.Parent?.Name ?? Path.GetFileName(f)).StartsWith(targetSp, StringComparison.OrdinalIgnoreCase)).ToArray();
            }

            if (specFiles.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]경고: 대상 설계서 파일을 찾을 수 없습니다.[/]");
                return;
            }

            foreach (var file in specFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var spName = Directory.GetParent(file)?.Parent?.Name ?? Path.GetFileName(file).Replace(".md", "");
                var rawJsonPath = Path.Combine(config.SpecDirectory, "Procedures", spName, "raw", "metadata.json");

                if (!File.Exists(rawJsonPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]경고 ({spName}): 원본 메타데이터 JSON 파일이 없어 모의 데이터 생성을 건너뜁니다.[/]");
                    continue;
                }

                AnsiConsole.MarkupLine($"모의 데이터 기획 중: {Markup.Escape(spName)}");
                
                try
                {
                    var specContent = await File.ReadAllTextAsync(file, cancellationToken);
                    var rawJsonContent = await File.ReadAllTextAsync(rawJsonPath, cancellationToken);
                    
                    using var doc = JsonDocument.Parse(rawJsonContent);
                    var root = doc.RootElement;
                    var procedureDdl = root.GetProperty("DdlText").GetString() ?? "";
                    
                    string dependenciesJson = "";
                    if (root.TryGetProperty("Dependencies", out var depsProp))
                    {
                        dependenciesJson = depsProp.GetRawText();
                    }

                    var mockDataJson = await aiService.GenerateMockTableDataAsync(specContent, procedureDdl, dependenciesJson, cancellationToken);

                    var mockOutputDir = Path.Combine(config.OutputDirectory, "mock");
                    if (!Directory.Exists(mockOutputDir))
                    {
                        Directory.CreateDirectory(mockOutputDir);
                    }

                    var outputPath = Path.Combine(mockOutputDir, $"{spName}_mock_data.json");
                    await File.WriteAllTextAsync(outputPath, mockDataJson, System.Text.Encoding.UTF8, cancellationToken);
                    AnsiConsole.MarkupLine($"[green]완료:[/] {Markup.Escape(outputPath)}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]오류 ({Markup.Escape(spName)}): {Markup.Escape(ex.Message)}[/]");
                }
            }
        }
    }
}
