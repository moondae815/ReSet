using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    public class VerificationPipelineOrchestrator
    {
        private readonly IDbMetadataService _dbService;
        private readonly IAiService _aiService;
        private readonly MechanicalValidator _validator;
        private readonly IVerificationUserInteraction _userInteraction;
        private readonly int _maxL2Attempts;
        private readonly int _maxAttempts;
        private readonly string _modelName;
        private readonly ICacheManager _cacheManager;
        private readonly IAiService _criticService;
        private readonly IAiService _consolidatorService;
        private readonly string? _actorEffort;
        private readonly string? _criticEffort;
        private readonly string? _consolidatorEffort;
        private readonly int _criticScoreThreshold;

        public VerificationPipelineOrchestrator(
            IDbMetadataService dbService,
            IAiService aiService,
            MechanicalValidator validator,
            IVerificationUserInteraction userInteraction,
            string maxL2Attempts = "1",
            string modelName = "",
            ICacheManager? cacheManager = null,
            IAiService? criticService = null,
            IAiService? consolidatorService = null,
            string? actorEffort = null,
            string? criticEffort = null,
            string? consolidatorEffort = null,
            int criticScoreThreshold = 8)
        {
            _dbService = dbService;
            _aiService = aiService;
            _validator = validator;
            _userInteraction = userInteraction;
            _modelName = modelName;
            _cacheManager = cacheManager ?? new CacheManager();
            _criticService = criticService ?? aiService;
            _consolidatorService = consolidatorService ?? aiService;
            _actorEffort = actorEffort;
            _criticEffort = criticEffort;
            _consolidatorEffort = consolidatorEffort;
            _criticScoreThreshold = criticScoreThreshold;

            if (string.Equals(maxL2Attempts, "unlimited", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(maxL2Attempts, "검증 완료까지", StringComparison.OrdinalIgnoreCase) ||
                maxL2Attempts == "-1")
            {
                _maxL2Attempts = -1;
            }
            else if (int.TryParse(maxL2Attempts, out int parsed))
            {
                _maxL2Attempts = parsed;
            }
            else
            {
                _maxL2Attempts = 1; // 기본값
            }

            // 이 예산은 L1 실패와 L2 실패가 공유한다. 설정 이름(MaxL2Attempts)과 달리
            // L2 전용이 아니다 — L1에서 소진되면 채점된 후보 수가 설정값보다 적어진다.
            // 2026-08-05 실행에서 3회 예산 중 1회를 L1 실패가 가져가 채점된 시도가 2회뿐이었다.
            // 예산을 나누지 않기로 한 이유는 RetryRescue가 최고점 후보를 구제하므로
            // 남는 손해가 "좋은 문서 상실"이 아니라 "개선 기회 1회 상실"이기 때문이다.
            _maxAttempts = _maxL2Attempts == -1 ? -1 : 1 + _maxL2Attempts;
        }

        public async Task<CodeObjectPipelineResult> RunCodeObjectPipelineAsync(
            string connectionString,
            CodeObjectKey key,
            int maxDepth,
            string provider,
            string instructions,
            bool isBatchMode,
            string outputDirectory,
            bool enableCache = false,
            CancellationToken cancellationToken = default,
            bool directDependenciesOnly = false,
            bool includeExternalCodeObjects = true,
            string? analysisDatabase = null) =>
            await RunCodeObjectPipelineCoreAsync(
                connectionString,
                key,
                maxDepth,
                provider,
                instructions,
                isBatchMode,
                outputDirectory,
                enableCache,
                cancellationToken,
                directDependenciesOnly,
                includeExternalCodeObjects,
                analysisDatabase);

        private async Task<CodeObjectPipelineResult> RunCodeObjectPipelineCoreAsync(
            string connectionString,
            CodeObjectKey key,
            int maxDepth,
            string provider,
            string instructions,
            bool isBatchMode,
            string outputDirectory,
            bool enableCache,
            CancellationToken cancellationToken,
            bool directDependenciesOnly,
            bool includeExternalCodeObjects,
            string? analysisDatabase = null)
        {
            var selectedOption = $"{key.Schema}.{key.Name}";
            // 정제 SQL 파일명은 분석 기준 DB와 다른 DB의 객체끼리 서로 덮어쓰지 않도록 DB 성분을 포함한다.
            var cleansingFileBaseName = ResolveCleansingFileBaseName(key, analysisDatabase);
            var objectKind = key.Type == CodeObjectType.Function ? "UDF" : "SP";
            var objectStatus = $"{objectKind}: {key.CanonicalName}";
            SpDefinition? spDef = null;
            ReviewResult? finalReview = null;
            var verificationOutcome = VerificationOutcome.Passed;

            // 9곳의 반환 지점이 같은 형태를 쓰도록 모은다. verificationOutcome은
            // 호출 시점 값이 읽히므로 각 지점에서 따로 넘기지 않는다.
            CodeObjectPipelineResult Result(
                string? spec,
                SpDefinition? definition,
                ReviewResult? review,
                string? thinking,
                bool fromCache = false,
                DateTime? analyzedAt = null) => new()
            {
                SpecMarkdown = spec,
                SpDef = definition,
                Review = review,
                ThinkingText = thinking,
                Outcome = verificationOutcome,
                FromCache = fromCache,
                AnalyzedAt = analyzedAt
            };
            var accumulatedThinking = new StringBuilder();
            AiResult? ollamaPart1 = null;
            AiResult? ollamaPart2 = null;
            AiResult? ollamaPart3 = null;

            Log.Information("[파이프라인] 코드 객체 분석 시작 - Type: {ObjectType}, Key: {ObjectKey}, Provider: {Provider}, MaxDepth: {MaxDepth}, BatchMode: {IsBatchMode}",
                objectKind, key.CanonicalName, provider, maxDepth, isBatchMode);

            _userInteraction.NotifyStatus($"[yellow]{objectStatus}[/] - DB 메타데이터 및 의존성 분석 중 (최대 깊이: {maxDepth}단계)...");
            try
            {
                spDef = directDependenciesOnly
                    ? await _dbService.GetCodeObjectDetailsDirectAsync(
                        connectionString,
                        key,
                        cancellationToken,
                        includeExternalCodeObjects)
                    : await _dbService.GetCodeObjectDetailsAsync(connectionString, key, maxDepth, cancellationToken);
                if (spDef == null && !directDependenciesOnly && key.Type == CodeObjectType.Procedure)
                {
                    // Preserve compatibility with legacy metadata adapters while the
                    // common code-object query remains the primary entry point.
                    Log.Warning("[파이프라인] 공통 코드 객체 조회가 빈 결과를 반환해 레거시 SP 조회로 보완합니다 - SP: {SpName}", selectedOption);
                    spDef = await _dbService.GetSpDetailsAsync(connectionString, key.Schema, key.Name, maxDepth, cancellationToken);
                }

                if (spDef == null)
                {
                    throw new InvalidOperationException($"코드 객체 메타데이터가 비어 있습니다: {key.CanonicalName}");
                }

                if (spDef.ObjectType != key.Type)
                {
                    Log.Warning(
                        "[파이프라인] 메타데이터 객체 유형과 요청 키가 불일치해 요청 유형을 적용합니다 - Key: {ObjectKey}, MetadataType: {MetadataType}, RequestedType: {RequestedType}",
                        key.CanonicalName,
                        spDef.ObjectType,
                        key.Type);
                    spDef.ObjectType = key.Type;
                    spDef.ObjectKey = key;
                }
                else
                {
                    spDef.ObjectKey ??= key;
                }
                Log.Debug("[파이프라인] DB 메타데이터 수집 완료 - 코드 객체: {ObjectKey}, 의존성 수: {DepCount}, 경고 수: {WarningCount}",
                    key.CanonicalName,
                    spDef?.Dependencies?.Count ?? 0, spDef?.Warnings?.Count ?? 0);
            }
            // 취소를 삼키면 spDef가 null로 남아 파이프라인이 "메타데이터 없음"으로
            // 조용히 종료된다 - 사용자가 멈추라고 한 것이 아니라 실패로 보고된다.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "[파이프라인] DB 메타데이터 수집 실패 - SP: {SpName}", selectedOption);
                _userInteraction.NotifyError($"{selectedOption} - DB 조회 실패: {ex.Message}");
            }

            if (spDef == null)
            {
                Log.Warning("[파이프라인] SP 정의를 가져오지 못해 파이프라인을 중단합니다 - SP: {SpName}", selectedOption);
                return Result(null, null, null, null);
            }

            var cacheObjectKey = ResolveCacheObjectKey(
                spDef,
                key);
            OutputPathResolver? outputPaths = null;
            if (enableCache && cacheObjectKey != null)
            {
                try
                {
                    outputPaths = new OutputPathResolver(
                        // 빈 문자열·공백도 미지정으로 간주해야 캐시가 조용히 비활성화되지 않는다.
                        string.IsNullOrWhiteSpace(analysisDatabase)
                            ? cacheObjectKey.Database
                            : analysisDatabase,
                        outputDirectory);
                }
                catch (Exception ex)
                {
                    Log.Warning(
                        ex,
                        "[파이프라인] 캐시 출력 경로를 확인할 수 없어 캐시를 건너뜁니다 - SP: {SpName}",
                        selectedOption);
                }
            }

            if (spDef.Warnings.Count > 0)
            {
                _userInteraction.NotifyWarnings(selectedOption, spDef.Warnings);
            }

            // 캐시 유효성 확인
            string? compositeHash = null;
            if (enableCache && cacheObjectKey != null && outputPaths != null)
            {
                try
                {
                    compositeHash = _cacheManager.ComputeCompositeHash(spDef, maxDepth);
                    if (_cacheManager.IsCacheValid(
                            cacheObjectKey,
                            compositeHash,
                            outputPaths))
                    {
                        Log.Information("[파이프라인] 캐시 히트 - AI 분석 건너뜀 - SP: {SpName}", selectedOption);
                        _userInteraction.NotifyStatus($"[green]{objectStatus}[/] - 캐시가 유효합니다. AI 분석을 건너뛰고 기존 보고서를 사용합니다. (Cache Hit)");
                        var specFilePath = outputPaths.ResolveSpecPath(cacheObjectKey);
                        if (System.IO.File.Exists(specFilePath))
                        {
                            var cachedArtifact = await System.IO.File.ReadAllTextAsync(
                                specFilePath,
                                cancellationToken);
                            var (cachedSpec, cachedReview, cachedAnalyzedAt) =
                                ParseCachedSpecification(cachedArtifact);
                            return Result(
                                cachedSpec, spDef, cachedReview, null,
                                fromCache: true, analyzedAt: cachedAnalyzedAt);
                        }
                    }
                    else
                    {
                        Log.Debug("[파이프라인] 캐시 미스 - AI 분석 진행 - SP: {SpName}", selectedOption);
                    }
                }
                // 캐시 확인이 취소되었는데 삼키면 파이프라인이 전체 AI 분석으로 진행한다.
                // 사용자가 멈추라고 한 직후에 가장 비싼 작업이 시작되는 셈이다.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "[파이프라인] 캐시 확인 중 예외 발생 (무시됨) - SP: {SpName}", selectedOption);
                    _userInteraction.NotifyStatus($"[yellow]경고: 캐시 확인 중 오류가 발생하여 무시하고 분석을 진행합니다. ({ex.Message})[/]");
                }
            }
            else if (enableCache)
            {
                Log.Warning(
                    "[파이프라인] 실제 데이터베이스를 확인할 수 없어 캐시를 건너뜁니다 - SP: {SpName}",
                    selectedOption);
            }

            var feedbackHistory = new System.Collections.Generic.List<string>();
            string? feedbackLog = null;
            // 재생성 범위는 feedbackLog와 같은 자리에서 정해진다. 둘 다 "직전 회차가
            // 무엇을 지적당했나"를 표현하지만, 이쪽은 산문이 아니라 구조화된 값이다.
            RegenerationScope? regenScope = null;
            string specificationMarkdown = string.Empty;

            if (string.Equals(_actorEffort, "dynamic", StringComparison.OrdinalIgnoreCase))
            {
                string[] candidates;
                AiResult[]? candidatesResult = null;
                var actorInfo = $"Actor: {_aiService.ProviderName} - {_aiService.ModelName}(dynamic effort)";
                var criticInfo = $"Critic: {_criticService.ProviderName} - {_criticService.ModelName}({_criticEffort ?? "high"} effort)";
                _userInteraction.NotifyStatus($"[yellow]{objectStatus}[/] - 하이브리드 다중 후보군 병렬 생성 및 검토 중... ({actorInfo} / {criticInfo})");
                
                using (var progressScope = _userInteraction.CreateProgressScope("하이브리드 다중 후보군 생성") ?? NullProgressScope.Instance)
                {
                    progressScope.AddTask("Low Effort Spec 생성", "Low Effort Spec 생성");

                    var tasks = new System.Collections.Generic.List<Task<AiResult>>();
                    tasks.Add(WrapWithProgress(_aiService.GenerateSpecificationAsync(spDef, instructions, feedbackLog, "low", cancellationToken), progressScope, "Low Effort Spec 생성"));

                    await Task.Delay(1000, cancellationToken);
                    progressScope.AddTask("Medium Effort Spec 생성", "Medium Effort Spec 생성");
                    tasks.Add(WrapWithProgress(_aiService.GenerateSpecificationAsync(spDef, instructions, feedbackLog, "medium", cancellationToken), progressScope, "Medium Effort Spec 생성"));

                    await Task.Delay(1000, cancellationToken);
                    progressScope.AddTask("High Effort Spec 생성", "High Effort Spec 생성");
                    tasks.Add(WrapWithProgress(_aiService.GenerateSpecificationAsync(spDef, instructions, feedbackLog, "high", cancellationToken), progressScope, "High Effort Spec 생성"));

                    try
                    {
                        var candidatesResultResult = await Task.WhenAll(tasks);
                        candidatesResult = candidatesResultResult;
                        candidates = candidatesResult.Select(x => x.Content).ToArray();

                        accumulatedThinking.AppendLine("### [Actor] Low Spec Generation Thinking");
                        accumulatedThinking.AppendLine($"- **AI Provider**: {_aiService.ProviderName}");
                        accumulatedThinking.AppendLine($"- **AI Model**: {_aiService.ModelName} (Effort: low)");
                        accumulatedThinking.AppendLine();
                        if (candidatesResult != null && candidatesResult.Length > 0 && !string.IsNullOrWhiteSpace(candidatesResult[0]?.ThinkingText))
                        {
                            accumulatedThinking.AppendLine(candidatesResult[0].ThinkingText);
                        }
                        else
                        {
                            accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_aiService.ProviderName));
                        }
                        accumulatedThinking.AppendLine();

                        accumulatedThinking.AppendLine("### [Actor] Medium Spec Generation Thinking");
                        accumulatedThinking.AppendLine($"- **AI Provider**: {_aiService.ProviderName}");
                        accumulatedThinking.AppendLine($"- **AI Model**: {_aiService.ModelName} (Effort: medium)");
                        accumulatedThinking.AppendLine();
                        if (candidatesResult != null && candidatesResult.Length > 1 && !string.IsNullOrWhiteSpace(candidatesResult[1]?.ThinkingText))
                        {
                            accumulatedThinking.AppendLine(candidatesResult[1].ThinkingText);
                        }
                        else
                        {
                            accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_aiService.ProviderName));
                        }
                        accumulatedThinking.AppendLine();

                        accumulatedThinking.AppendLine("### [Actor] High Spec Generation Thinking");
                        accumulatedThinking.AppendLine($"- **AI Provider**: {_aiService.ProviderName}");
                        accumulatedThinking.AppendLine($"- **AI Model**: {_aiService.ModelName} (Effort: high)");
                        accumulatedThinking.AppendLine();
                        if (candidatesResult != null && candidatesResult.Length > 2 && !string.IsNullOrWhiteSpace(candidatesResult[2]?.ThinkingText))
                        {
                            accumulatedThinking.AppendLine(candidatesResult[2].ThinkingText);
                        }
                        else
                        {
                            accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_aiService.ProviderName));
                        }
                        accumulatedThinking.AppendLine();
                    }
                    // 토큰이 await에 직접 들어가지 않고 위에서 만든 tasks를 거쳐
                    // Task.WhenAll로 간접 전달된다. 아키텍처 테스트는 await 인자에서
                    // 토큰 모양을 찾으므로 이 자리를 보지 못한다 - 필터는 사람이 지켜야 한다.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _userInteraction.NotifyError($"{selectedOption} - 하이브리드 후보 생성 중 실패: {ex.Message}");
                        return Result(null, spDef, null, null);
                    }
                }

                // 각 후보에 대한 L2 검증 및 채점 수행
                ReviewResult[] reviews;
                using (var progressScope = _userInteraction.CreateProgressScope("Critic 검토") ?? NullProgressScope.Instance)
                {
                    var reviewTasks = new System.Collections.Generic.List<Task<ReviewResult>>();
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        if (i > 0)
                        {
                            await Task.Delay(1000, cancellationToken);
                        }

                        var taskName = i switch
                        {
                            0 => "Low Effort Spec 검토",
                            1 => "Medium Effort Spec 검토",
                            2 => "High Effort Spec 검토",
                            _ => $"후보군 {i+1} Spec 검토"
                        };
                        var taskKey = taskName;
                        progressScope.AddTask(taskKey, taskName);
                        reviewTasks.Add(WrapWithProgress(_criticService.ReviewSpecificationAsync(spDef, candidates[i], _criticEffort, cancellationToken), progressScope, taskKey));
                    }

                    try
                    {
                        reviews = await Task.WhenAll(reviewTasks);
                        if (reviews != null)
                        {
                            for (int i = 0; i < reviews.Length; i++)
                            {
                                var candidateLabel = i switch
                                {
                                    0 => "Low",
                                    1 => "Medium",
                                    2 => "High",
                                    _ => (i + 1).ToString()
                                };
                                accumulatedThinking.AppendLine($"### [Critic] {candidateLabel} Spec Review Thinking");
                                accumulatedThinking.AppendLine($"- **AI Provider**: {_criticService.ProviderName}");
                                accumulatedThinking.AppendLine($"- **AI Model**: {_criticService.ModelName} (Effort: {_criticEffort ?? "default"})");
                                accumulatedThinking.AppendLine();
                                if (reviews[i] != null && !string.IsNullOrWhiteSpace(reviews[i]!.ThinkingText))
                                {
                                    accumulatedThinking.AppendLine(reviews[i]!.ThinkingText);
                                }
                                else
                                {
                                    accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_criticService.ProviderName));
                                }
                                accumulatedThinking.AppendLine();
                            }
                        }
                    }
                    // 위와 같다 - 토큰은 reviewTasks를 거쳐 Task.WhenAll로 간접 전달되므로
                    // 아키텍처 테스트가 이 자리를 세지 못한다. 필터를 지우지 마십시오.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _userInteraction.NotifyError($"{selectedOption} - Critic 검토 중 실패: {ex.Message}");
                        return Result(null, spDef, null, null);
                    }
                }

                if (reviews != null && reviews.Length >= 3 && reviews[0] != null && reviews[1] != null && reviews[2] != null)
                {
                    _userInteraction.NotifyStatus($"[green]{objectStatus}[/] - Effort별 Spec 검토 완료:");
                    _userInteraction.NotifyStatus($"  - Low Spec: [bold]{reviews[0]!.NormalizedScore}[/]점 (정합성:{reviews[0]!.ScoreAccuracy}, CRUD:{reviews[0]!.ScoreCrud}, 연동:{reviews[0]!.ScoreInterface}, 예외:{reviews[0]!.ScoreException}, 시각화:{reviews[0]!.ScoreReadability})");
                    if (!string.IsNullOrWhiteSpace(reviews[0]!.FeedbackComment))
                    {
                        var commentLines = reviews[0]!.FeedbackComment!.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in commentLines)
                        {
                            _userInteraction.NotifyStatus($"    [grey]* Low Spec Critic 피드백: {EscapeMarkup(line)}[/]");
                        }
                    }
                    _userInteraction.NotifyStatus($"  - Medium Spec: [bold]{reviews[1]!.NormalizedScore}[/]점 (정합성:{reviews[1]!.ScoreAccuracy}, CRUD:{reviews[1]!.ScoreCrud}, 연동:{reviews[1]!.ScoreInterface}, 예외:{reviews[1]!.ScoreException}, 시각화:{reviews[1]!.ScoreReadability})");
                    if (!string.IsNullOrWhiteSpace(reviews[1]!.FeedbackComment))
                    {
                        var commentLines = reviews[1]!.FeedbackComment!.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in commentLines)
                        {
                            _userInteraction.NotifyStatus($"    [grey]* Medium Spec Critic 피드백: {EscapeMarkup(line)}[/]");
                        }
                    }
                    _userInteraction.NotifyStatus($"  - High Spec: [bold]{reviews[2]!.NormalizedScore}[/]점 (정합성:{reviews[2]!.ScoreAccuracy}, CRUD:{reviews[2]!.ScoreCrud}, 연동:{reviews[2]!.ScoreInterface}, 예외:{reviews[2]!.ScoreException}, 시각화:{reviews[2]!.ScoreReadability})");
                    if (!string.IsNullOrWhiteSpace(reviews[2]!.FeedbackComment))
                    {
                        var commentLines = reviews[2]!.FeedbackComment!.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in commentLines)
                        {
                            _userInteraction.NotifyStatus($"    [grey]* High Spec Critic 피드백: {EscapeMarkup(line)}[/]");
                        }
                    }
                }

                // 완벽한 후보(L1 & L2 무결 & 신뢰도 90점 이상) 발견 시 Fast-pass 즉시 채택
                bool fastPassTriggered = false;
                int bestCandidateIndex = -1;
                int highestScore = -1;

                for (int i = 0; i < candidates.Length; i++)
                {
                    var l1Check = _validator.Validate(candidates[i]);
                    if (l1Check.IsValid)
                    {
                        candidates[i] = l1Check.CleansedMarkdown ?? candidates[i];
                    }
                    if (l1Check.IsValid && reviews![i] != null && !reviews![i]!.HasDefects && reviews![i]!.NormalizedScore >= 90)
                    {
                        if (reviews![i]!.NormalizedScore > highestScore)
                        {
                            highestScore = reviews![i]!.NormalizedScore;
                            bestCandidateIndex = i;
                        }
                    }
                }

                if (bestCandidateIndex != -1)
                {
                    string scoreSummary = (reviews != null && reviews.Length >= 3 && reviews[0] != null && reviews[1] != null && reviews[2] != null) 
                        ? $" (Low: {reviews[0].NormalizedScore}점, Medium: {reviews[1].NormalizedScore}점, High: {reviews[2].NormalizedScore}점)"
                        : string.Empty;
                    _userInteraction.NotifyStatus($"[green]{objectStatus}[/] - 완벽한 후보군(후보 {bestCandidateIndex + 1}, AI 신뢰도: [bold green]{highestScore}[/]/100점)이 발견되어 즉시 채택합니다.{scoreSummary}");
                    specificationMarkdown = candidates[bestCandidateIndex];
                    finalReview = reviews![bestCandidateIndex];
                    fastPassTriggered = true;

                    if (candidatesResult != null && bestCandidateIndex < candidatesResult.Length)
                    {
                        var bestResult = candidatesResult[bestCandidateIndex];
                        spDef.RawPromptContext = $"=== [System Prompt] ===\n{bestResult.SystemPrompt}\n\n=== [User Prompt] ===\n{bestResult.UserPrompt}";
                    }
                }

                if (!fastPassTriggered)
                {
                    // 영역별 합성 가이드 및 프롬프트 조립
                    var sbConsolidation = new StringBuilder();
                    var isFunction = spDef.ObjectType == CodeObjectType.Function;
                    sbConsolidation.AppendLine(isFunction
                        ? "You are a specialist consolidator for SQL Server User Defined Function specifications. Combine the strongest candidate evidence into one accurate function specification."
                        : "당신은 제공된 여러 개의 Stored Procedure 분석 명세서 후보를 종합하여, 각 후보의 우수 영역을 취합하고 결점을 개선하여 단일한 완벽한 명세서로 합성(Consolidation)하는 전문 조립 아키텍트입니다.");
                    sbConsolidation.AppendLine();
                    sbConsolidation.AppendLine("[제공된 명세서 후보 목록 및 평가 점수]");
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        var rev = reviews![i];
                        if (rev == null) continue;
                        sbConsolidation.AppendLine($"--- [후보 {i+1}] ---");
                        sbConsolidation.AppendLine($"- 종합 평가 점수: {rev.NormalizedScore}점 / 100점 (50점 만점 기준 {rev.TotalScore}점)");
                        if (isFunction)
                        {
                            sbConsolidation.AppendLine($"  * Formula and business logic accuracy (ScoreAccuracy): {rev.ScoreAccuracy}/10");
                            sbConsolidation.AppendLine($"  * Referenced tables/functions completeness (ScoreCrud): {rev.ScoreCrud}/10");
                            sbConsolidation.AppendLine($"  * Return contract and TVF schema completeness (ScoreInterface): {rev.ScoreInterface}/10");
                            sbConsolidation.AppendLine($"  * Determinism and observable side effects accuracy (ScoreException): {rev.ScoreException}/10");
                            sbConsolidation.AppendLine($"  * Diagram and specification readability (ScoreReadability): {rev.ScoreReadability}/10");
                        }
                        else
                        {
                            sbConsolidation.AppendLine($"  * 비즈니스 로직 및 제어 흐름 정합성 (ScoreAccuracy): {rev.ScoreAccuracy}/10점");
                            sbConsolidation.AppendLine($"  * 데이터 모델 및 CRUD 완전성 (ScoreCrud): {rev.ScoreCrud}/10점");
                            sbConsolidation.AppendLine($"  * 연동 인터페이스 구체성 (ScoreInterface): {rev.ScoreInterface}/10점");
                            sbConsolidation.AppendLine($"  * 예외 및 트랜잭션/격리성 정책 (ScoreException): {rev.ScoreException}/10점");
                            sbConsolidation.AppendLine($"  * 다이어그램 및 시각화 가독성 (ScoreReadability): {rev.ScoreReadability}/10점");
                        }
                        sbConsolidation.AppendLine($"- Critic 결함 피드백: {rev.FeedbackComment ?? "결함 없음"}");
                        sbConsolidation.AppendLine();
                        sbConsolidation.AppendLine("[본문 내용]");
                        sbConsolidation.AppendLine(candidates[i]);
                        sbConsolidation.AppendLine();
                    }
                    sbConsolidation.AppendLine();
                    sbConsolidation.AppendLine("[합성 및 병합 지침]");
                    if (isFunction)
                    {
                        sbConsolidation.AppendLine("1. Preserve the exact function return contract, including every TVF column, data type, and nullability when supplied.");
                        sbConsolidation.AppendLine("2. Select the most factual candidate evidence for formulas, determinism, side effects, and referenced tables/functions; correct every critic defect without inventing behavior.");
                        sbConsolidation.AppendLine("3. Keep the required Korean H2 headers: ## 개요, ## 파라미터 목록, ## CRUD 분석, ## 로직 흐름 요약, ## 비즈니스 흐름 시각화.");
                        sbConsolidation.AppendLine("4. Output only the final Korean Markdown specification without conversational filler.");
                    }
                    else
                    {
                        sbConsolidation.AppendLine("1. 각 카테고리별 세부 평가 점수를 바탕으로, 해당 부문에서 가장 높은 점수(만점에 가까운 점수)를 받은 후보의 내용을 '진실의 원천(Source of Truth)'으로 채택하여 조립하십시오.");
                        sbConsolidation.AppendLine("   - 예: ScoreAccuracy(정합성)가 가장 높은 후보의 로직 설명을 바탕으로 삼고, ScoreReadability(다이어그램)가 가장 높은 후보의 Mermaid 다이어그램을 병합합니다.");
                        sbConsolidation.AppendLine("2. 각 후보에 지적된 Critic 결함 피드백(Critic Feedback) 내용을 명밀히 분석하여 최종 합성 명세서에서 완전히 수정 및 보완하십시오.");
                        sbConsolidation.AppendLine("3. 5대 필수 대분류 헤더 명칭(## 개요, ## 파라미터 목록, ## CRUD 분석, ## 로직 흐름 요약, ## 비즈니스 흐름 시각화)을 그대로 사용하여 문서를 구성하십시오.");
                        sbConsolidation.AppendLine("4. 최종 결과물만 다듬어 마크다운으로 직접 출력하십시오. 추가적인 사족이나 인사말은 절대 포함하지 마십시오.");
                    }

                    string scoreSummary = (reviews != null && reviews.Length >= 3 && reviews[0] != null && reviews[1] != null && reviews[2] != null) 
                        ? $" (Low: {reviews[0].NormalizedScore}점, Medium: {reviews[1].NormalizedScore}점, High: {reviews[2].NormalizedScore}점)"
                        : string.Empty;
                    _userInteraction.NotifyStatus($"[yellow]{objectStatus}[/] - 이종 모델 합성 에이전트(Consolidator) 구동 중 ({_consolidatorService.ProviderName} - {_consolidatorService.ModelName}, {_consolidatorEffort ?? "medium"} effort)...{scoreSummary}");
                    try
                    {
                        AiResult consolidatorResult;
                        using (var progressScope = _userInteraction.CreateProgressScope("Consolidator 합성") ?? NullProgressScope.Instance)
                        {
                            progressScope.AddTask("cons", $"{_consolidatorService.ModelName} 합성 중...");
                            consolidatorResult = await WrapWithProgress(_consolidatorService.GenerateSpecificationAsync(spDef, sbConsolidation.ToString(), null, _consolidatorEffort ?? "medium", cancellationToken), progressScope, "cons");
                        }
                        specificationMarkdown = consolidatorResult.Content;
                        spDef.RawPromptContext = $"=== [System Prompt] ===\n{consolidatorResult.SystemPrompt}\n\n=== [User Prompt] ===\n{consolidatorResult.UserPrompt}";

                        accumulatedThinking.AppendLine("### [Consolidator] Synthesis Thinking");
                        accumulatedThinking.AppendLine($"- **AI Provider**: {_consolidatorService.ProviderName}");
                        accumulatedThinking.AppendLine($"- **AI Model**: {_consolidatorService.ModelName} (Effort: {_consolidatorEffort ?? "medium"})");
                        accumulatedThinking.AppendLine();
                        if (consolidatorResult != null && !string.IsNullOrWhiteSpace(consolidatorResult.ThinkingText))
                        {
                            accumulatedThinking.AppendLine(consolidatorResult.ThinkingText);
                        }
                        else
                        {
                            accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_consolidatorService.ProviderName));
                        }
                        accumulatedThinking.AppendLine();
                    }
                    // 취소를 삼키면 실패로 위장한 정상 반환이 되어 취소 사실이 사라진다.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _userInteraction.NotifyError($"{selectedOption} - 최종 합성 생성 실패: {ex.Message}");
                        return Result(null, spDef, null, null);
                    }

                    // 합성본 기계적 검증 (L1) 1회 수행
                    var finalL1 = _validator.Validate(specificationMarkdown);
                    specificationMarkdown = finalL1.CleansedMarkdown ?? specificationMarkdown;
                    var consolidatedL1Valid = finalL1.IsValid;
                    var consolidatedL1Errors = finalL1.Errors;
                    if (!finalL1.IsValid)
                    {
                        _userInteraction.NotifyStatus("합성본에서 정적 에러가 검출되어 AI 자가 수정 1회 진행합니다.");
                        try
                        {
                            AiResult consolidatorSelfFixResult;
                            using (var progressScope = _userInteraction.CreateProgressScope("Consolidator 자가 수정") ?? NullProgressScope.Instance)
                            {
                                progressScope.AddTask("selffix", $"{_consolidatorService.ModelName} 자가 수정 중...");
                                consolidatorSelfFixResult = await WrapWithProgress(_consolidatorService.GenerateSpecificationAsync(spDef, sbConsolidation.ToString(), finalL1.SuggestedPromptFix, _consolidatorEffort ?? "medium", cancellationToken), progressScope, "selffix");
                            }
                            var postFixL1 = _validator.Validate(consolidatorSelfFixResult.Content);
                            specificationMarkdown = postFixL1.CleansedMarkdown ?? consolidatorSelfFixResult.Content;
                            // 자가 수정 1회 후에도 여전히 L1을 통과하지 못하면 표준 재시도 루프와
                            // 동일하게 L1Exhausted로 확정한다. 이후의 L2 재검토 결과가 이를
                            // Passed로 덮어써서는 안 되므로 최종 배너 삽입부에서 최우선으로 처리한다.
                            consolidatedL1Valid = postFixL1.IsValid;
                            consolidatedL1Errors = postFixL1.Errors;
                            spDef.RawPromptContext = $"=== [System Prompt] ===\n{consolidatorSelfFixResult.SystemPrompt}\n\n=== [User Prompt] ===\n{consolidatorSelfFixResult.UserPrompt}";

                            accumulatedThinking.AppendLine("### [Consolidator] Self-Correction Thinking");
                            accumulatedThinking.AppendLine($"- **AI Provider**: {_consolidatorService.ProviderName}");
                            accumulatedThinking.AppendLine($"- **AI Model**: {_consolidatorService.ModelName} (Effort: {_consolidatorEffort ?? "medium"})");
                            accumulatedThinking.AppendLine();
                            if (consolidatorSelfFixResult != null && !string.IsNullOrWhiteSpace(consolidatorSelfFixResult.ThinkingText))
                            {
                                accumulatedThinking.AppendLine(consolidatorSelfFixResult.ThinkingText);
                            }
                            else
                            {
                                accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_consolidatorService.ProviderName));
                            }
                            accumulatedThinking.AppendLine();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Warning(ex, "합성본 자가 수정 실패 (이전 버전 유지)");
                        }
                    }

                    // [추가] 합성본 L2 최종 Critic 검토 및 최대 1회 보완
                    _userInteraction.NotifyStatus($"[yellow]{objectStatus}[/] - 최종 합성본 L2 정성 검토 중 ({_criticService.ProviderName} - {_criticService.ModelName})...");
                    ReviewResult? finalL2Result = null;
                    string? finalReviewFailureReason = null;
                    try
                    {
                        using (var progressScope = _userInteraction.CreateProgressScope("최종 L2 검토") ?? NullProgressScope.Instance)
                        {
                            progressScope.AddTask("final_review", "합성본 최종 L2 검토 중...");
                            finalL2Result = await WrapWithProgress(_criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken), progressScope, "final_review");
                        }
                        
                        accumulatedThinking.AppendLine("### [Critic] Final Consolidated Spec Review Thinking");
                        accumulatedThinking.AppendLine($"- **AI Provider**: {_criticService.ProviderName}");
                        accumulatedThinking.AppendLine($"- **AI Model**: {_criticService.ModelName} (Effort: {_criticEffort ?? "default"})");
                        accumulatedThinking.AppendLine();
                        if (finalL2Result != null && !string.IsNullOrWhiteSpace(finalL2Result.ThinkingText))
                        {
                            accumulatedThinking.AppendLine(finalL2Result.ThinkingText);
                        }
                        else
                        {
                            accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_criticService.ProviderName));
                        }
                        accumulatedThinking.AppendLine();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warning(ex, "최종 합성본 L2 Critic 검토 중 실패 (검증 미수행으로 표시하고 계속 진행)");
                        finalReviewFailureReason = ex.Message;
                    }

                    if (finalL2Result != null)
                    {
                        finalReview = finalL2Result; // 우선 1차 검토 점수를 기본값으로 할당
                    }

                    if (finalL2Result != null && finalL2Result.HasDefects)
                    {
                        _userInteraction.NotifyStatus($"[yellow]최종 합성본에서 일부 결함 감지:[/] {EscapeMarkup(finalL2Result.FeedbackComment ?? "")}");
                        _userInteraction.NotifyStatus($"결함을 반영한 1회 보완 최종 합성본을 생성합니다...");
                        try
                        {
                            string finalFeedbackLog = $"[최종 합성본 L2 리뷰 피드백]: 합성본을 평가한 결과 다음 결함이 발견되었습니다. 이를 완벽하게 수정하여 최종 명세서를 보완해 주십시오:\n\n{finalL2Result.FeedbackComment}";
                            
                            AiResult finalConsolidatedFixResult;
                            using (var progressScope = _userInteraction.CreateProgressScope("합성본 최종 보완") ?? NullProgressScope.Instance)
                            {
                                progressScope.AddTask("finalfix", "최종 보완 합성 중...");
                                finalConsolidatedFixResult = await WrapWithProgress(_consolidatorService.GenerateSpecificationAsync(spDef, sbConsolidation.ToString(), finalFeedbackLog, _consolidatorEffort ?? "medium", cancellationToken), progressScope, "finalfix");
                            }
                            
                            // 수정본 문법 L1 검증 진행
                            var fixL1Result = _validator.Validate(finalConsolidatedFixResult.Content);
                            if (fixL1Result.IsValid)
                            {
                                specificationMarkdown = fixL1Result.CleansedMarkdown ?? finalConsolidatedFixResult.Content;
                                // 보완본이 L1을 통과했으므로 이전 시도의 L1 판정을 그대로 들고
                                // 가면 안 된다. 최종 배너 삽입부가 이 플래그를 본다.
                                consolidatedL1Valid = true;
                                consolidatedL1Errors = fixL1Result.Errors;
                                spDef.RawPromptContext = $"=== [System Prompt] ===\n{finalConsolidatedFixResult.SystemPrompt}\n\n=== [User Prompt] ===\n{finalConsolidatedFixResult.UserPrompt}";

                                // 보완된 최종 합성본에 대해 L2 재리뷰를 받아 최종 점수를 갱신
                                _userInteraction.NotifyStatus($"[yellow]{objectStatus}[/] - 보완된 최종 합성본 L2 재검토 중...");
                                try
                                {
                                    using (var progressScope = _userInteraction.CreateProgressScope("보완본 재검토") ?? NullProgressScope.Instance)
                                    {
                                        progressScope.AddTask("refinal", "보완본 L2 재검토 중...");
                                        var reFinalReview = await WrapWithProgress(_criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken), progressScope, "refinal");
                                        if (reFinalReview != null)
                                        {
                                            finalReview = reFinalReview;
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException) { }
                            }
                            else
                            {
                                _userInteraction.NotifyStatus("최종 보완본에서 정적 에러가 검출되어 이전 버전을 최종본으로 유지합니다.");
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Warning(ex, "최종 보완 합성 생성 실패 (기존 합성본 유지)");
                        }
                    }

                    // [추가] 최종 배너 삽입. L1 미통과가 최우선 순위이며, 뒤이은 L2 재검토
                    // 결과(QualityRejected/ReviewNotRun/Passed)가 이를 덮어써서는 안 된다.
                    if (!consolidatedL1Valid)
                    {
                        verificationOutcome = VerificationOutcome.L1Exhausted;
                        specificationMarkdown =
                            VerificationBanner.L1Exhausted(consolidatedL1Errors ?? new System.Collections.Generic.List<string>()) + specificationMarkdown;
                    }
                    else if (finalReview != null && finalReview.HasDefects)
                    {
                        verificationOutcome = VerificationOutcome.QualityRejected;
                        specificationMarkdown =
                            VerificationBanner.QualityRejected(finalReview, _criticScoreThreshold) + specificationMarkdown;
                    }
                    else if (finalReview == null)
                    {
                        // 최종 L2 검토를 수행하지 못했다. 통과로 표시하지 않는다.
                        verificationOutcome = VerificationOutcome.ReviewNotRun;
                        specificationMarkdown =
                            VerificationBanner.ReviewNotRun(finalReviewFailureReason ?? "사유가 기록되지 않았습니다.")
                            + specificationMarkdown;
                    }
                }
                
                if (finalReview != null && !string.IsNullOrWhiteSpace(finalReview.FeedbackComment))
                {
                    var commentLines = finalReview.FeedbackComment.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in commentLines)
                    {
                        _userInteraction.NotifyStatus($"  [grey]* Critic 피드백: {EscapeMarkup(line)}[/]");
                    }
                }

                if (verificationOutcome == VerificationOutcome.L1Exhausted)
                {
                    // 표준 재시도 루프와 동일하게, L1을 통과하지 못한 명세서는
                    // 통과로 알리지 않는다.
                    _userInteraction.NotifyError($"{selectedOption} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                }
                else if (verificationOutcome == VerificationOutcome.ReviewNotRun)
                {
                    _userInteraction.NotifyError(
                        $"{selectedOption} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 명세서를 확정합니다.");
                }
                else if (verificationOutcome == VerificationOutcome.QualityRejected)
                {
                    // 표준 재시도 루프와 동일하게, 품질 기준을 통과하지 못한 명세서는
                    // 통과로 알리지 않는다.
                    _userInteraction.NotifyError($"{selectedOption} - [[L2 AI 리뷰]] 최종 보완 실패. 마지막 리뷰 반영 버전을 사용합니다.");
                }
                else
                {
                    _userInteraction.NotifyValidationSuccess(selectedOption);
                }
            }
            else
            {
                // 기존 단일 생성 루프
                int attempt = 1;
                var bestAttempt = new BestAttempt();

                while (true)
                {
                    var attemptText = attempt == 1 ? "1차 분석" : $"자가 수정 보완 ({attempt}회째)";
                    bool genSuccess = false;

                    Log.Information("[파이프라인] AI 명세서 생성 시작 - SP: {SpName}, 시도: {Attempt}, Provider: {Provider}, Model: {Model}",
                        selectedOption, attempt, provider, _modelName);
                    var effortText = !string.IsNullOrWhiteSpace(_actorEffort) ? $", Effort: {_actorEffort}" : "";
                    _userInteraction.NotifyStatus($"[yellow]{objectStatus}[/] - AI 리버스 엔지니어링 수행 중 ({_aiService.ProviderName} - {_aiService.ModelName}{effortText}) [[{attemptText}]]...");
                    try
                    {
                        if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(provider) && spDef.ObjectType == CodeObjectType.Procedure)
                        {
                            var scope = regenScope ?? RegenerationScope.Everything;
                            if (!scope.RunStage1)
                            {
                                Log.Information("[파이프라인] 재생성 범위가 표현 계층에 한정되어 1단계(추론)를 건너뛰고 기존 구조화 데이터 재사용");
                            }

                            string combinedTitle = attempt == 1 ? "로컬 LLM 명세서 분석 및 빌드 (Stage 1 & 2)" : "로컬 LLM 명세서 수정 (Stage 1 & 2)";
                            using (var progressScope = _userInteraction.CreateProgressScope(combinedTitle) ?? NullProgressScope.Instance)
                            {
                                if (scope.RunStage1)
                                {
                                    AiResult deconstructResult;
                                    Log.Information("[파이프라인] 1단계: 명세 구조화 추론(Stage 1 JSON 추출) {Action}", attempt == 1 ? "시작" : "수정 시작");
                                    
                                    string stage1Desc = attempt == 1 ? "1/4. 저장 프로시저 논리 구조 분석 중..." : "1/4. 저장 프로시저 논리 구조 수정 중...";
                                    progressScope.AddTask("deconstruct", stage1Desc);

                                    Action<(int current, int total, string message)> progressCallback = info => 
                                    {
                                        var newDesc = info.total > 0 
                                            ? $"{stage1Desc} (청크 {info.current}/{info.total})" 
                                            : $"{stage1Desc} ({info.message})";
                                        double percentage = info.total > 0 ? (double)(info.current - 1) / info.total * 100 : 0;
                                        progressScope.UpdateTask("deconstruct", percentage, newDesc);
                                    };

                                    deconstructResult = await WrapWithProgress(_aiService.DeconstructSpLogicAsync(spDef, instructions, feedbackLog, _actorEffort, cancellationToken, progressCallback), progressScope, "deconstruct");

                                    accumulatedThinking.AppendLine($"### [Actor] Attempt {attempt} Stage 1 (Deconstruct) Thinking");
                                    accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(deconstructResult.ThinkingText) ? "*(추론 없음)*" : deconstructResult.ThinkingText);
                                    accumulatedThinking.AppendLine();

                                    spDef.DeconstructedLogic = ParseDeconstructedLogic(deconstructResult.Content);

                                    // 중간 구조화 JSON 파일 백업 보존
                                    try
                                    {
                                        var rawFolder = System.IO.Path.Combine(outputDirectory, "Procedures", selectedOption, "raw");
                                        if (!System.IO.Directory.Exists(rawFolder))
                                        {
                                            System.IO.Directory.CreateDirectory(rawFolder);
                                        }
                                        var deconstructedPath = System.IO.Path.Combine(rawFolder, "deconstructed_logic.json");
                                        var options = new System.Text.Json.JsonSerializerOptions 
                                        { 
                                            WriteIndented = true, 
                                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
                                        };
                                        await System.IO.File.WriteAllTextAsync(deconstructedPath, System.Text.Json.JsonSerializer.Serialize(spDef.DeconstructedLogic, options), Encoding.UTF8);
                                        Log.Information("[파이프라인] 1단계 결과 JSON 디스크 보존 완료: {Path}", deconstructedPath);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Warning(ex, "[파이프라인] 1단계 결과 JSON 디스크 보존 중 예외 발생 (격리됨)");
                                    }
                                }

                                // 2단계: 각 H2 섹션 명세서 작성 (Stage 2 Markdown 포맷터)
                                // 셋이 모두 false가 되는 경우는 RegenerationScopeSelector가
                                // 막는다(어떤 팩터리도 빈 범위를 돌려주지 않는다). 그 불변식이
                                // 깨지면 이전 회차 섹션을 그대로 재제출해 재시도만 소모한다.
                                bool regenPart1 = scope.Overview;
                                bool regenPart2 = scope.Crud;
                                bool regenPart3 = scope.Logic;

                                // 이전 결과 누락 시 전체 재생성. 이 조건만 호출부의 지역
                                // 상태에 달려 있어 RegenerationScopeSelector가 알 수 없다.
                                if (ollamaPart1 == null || ollamaPart2 == null || ollamaPart3 == null)
                                {
                                    regenPart1 = regenPart2 = regenPart3 = true;
                                }

                                string actWord = attempt == 1 ? "빌드" : "수정";
                                if (regenPart1)
                                {
                                    progressScope.AddTask("part1", $"2/4. 개요 및 파라미터 {actWord} 중...");
                                    ollamaPart1 = await WrapWithProgress(_aiService.GenerateSpecSectionAsync(spDef, "OverviewAndParameters", instructions, feedbackLog, _actorEffort, cancellationToken), progressScope, "part1");
                                }
                                if (regenPart2)
                                {
                                    progressScope.AddTask("part2", $"3/4. CRUD 상세 명세 {actWord} 중...");
                                    ollamaPart2 = await WrapWithProgress(_aiService.GenerateSpecSectionAsync(spDef, "CrudAnalysis", instructions, feedbackLog, _actorEffort, cancellationToken), progressScope, "part2");
                                }
                                if (regenPart3)
                                {
                                    progressScope.AddTask("part3", $"4/4. 로직 요약 및 시각화 {actWord} 중...");
                                    ollamaPart3 = await WrapWithProgress(_aiService.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", instructions, feedbackLog, _actorEffort, cancellationToken), progressScope, "part3");
                                }
                            }

                            specificationMarkdown = $"{ollamaPart1!.Content.Trim()}\n\n{ollamaPart2!.Content.Trim()}\n\n{ollamaPart3!.Content.Trim()}";
                            spDef.RawPromptContext = $"=== [Part 1: System Prompt] ===\n{ollamaPart1.SystemPrompt}\n\n=== [Part 2: System Prompt] ===\n{ollamaPart2.SystemPrompt}\n\n=== [Part 3: System Prompt] ===\n{ollamaPart3.SystemPrompt}";

                            accumulatedThinking.AppendLine($"### [Actor] Attempt {attempt} Part 1 (Overview & Parameters) Thinking");
                            accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(ollamaPart1.ThinkingText) ? "*(추론 없음)*" : ollamaPart1.ThinkingText);
                            accumulatedThinking.AppendLine();
                            accumulatedThinking.AppendLine($"### [Actor] Attempt {attempt} Part 2 (CRUD Analysis) Thinking");
                            accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(ollamaPart2.ThinkingText) ? "*(추론 없음)*" : ollamaPart2.ThinkingText);
                            accumulatedThinking.AppendLine();
                            accumulatedThinking.AppendLine($"### [Actor] Attempt {attempt} Part 3 (Logic & Visualization) Thinking");
                            accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(ollamaPart3.ThinkingText) ? "*(추론 없음)*" : ollamaPart3.ThinkingText);
                            accumulatedThinking.AppendLine();
                        }
                        else
                        {
                            AiResult aiResult;
                            string scopeTitle = attempt == 1 ? "명세서 분석 및 빌드" : "명세서 수정 (피드백 반영)";
                            using (var progressScope = _userInteraction.CreateProgressScope(scopeTitle) ?? NullProgressScope.Instance)
                            {
                                string taskDesc = attempt == 1 ? $"{_aiService.ModelName} 분석 및 초안 작성 중..." : $"{_aiService.ModelName} 분석 수정 중...";
                                progressScope.AddTask("gen", taskDesc);
                                aiResult = await WrapWithProgress(_aiService.GenerateSpecificationAsync(spDef, instructions, feedbackLog, _actorEffort, cancellationToken), progressScope, "gen");
                            }
                            specificationMarkdown = aiResult.Content;
                            spDef.RawPromptContext = $"=== [System Prompt] ===\n{aiResult.SystemPrompt}\n\n=== [User Prompt] ===\n{aiResult.UserPrompt}";

                            accumulatedThinking.AppendLine($"### [Actor] Attempt {attempt} Generation Thinking");
                            accumulatedThinking.AppendLine($"- **AI Provider**: {_aiService.ProviderName}");
                            accumulatedThinking.AppendLine($"- **AI Model**: {_aiService.ModelName} (Effort: {_actorEffort ?? "default"})");
                            accumulatedThinking.AppendLine();
                            if (aiResult != null && !string.IsNullOrWhiteSpace(aiResult.ThinkingText))
                            {
                                accumulatedThinking.AppendLine(aiResult.ThinkingText);
                            }
                            else
                            {
                                accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_aiService.ProviderName));
                            }
                            accumulatedThinking.AppendLine();
                        }
                        genSuccess = true;


                        Log.Debug("[파이프라인] AI 명세서 생성 성공 - SP: {SpName}, 시도: {Attempt}, 응답 길이: {Length}자",
                            selectedOption, attempt, specificationMarkdown.Length);
                    }
                    // 취소를 삼키면 genSuccess가 false로 남아 실패로 위장한 정상
                    // 반환(Result(null,...))이 되어 취소 사실이 사라진다.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Error(ex, "[파이프라인] AI 명세서 생성 실패 - SP: {SpName}, 시도: {Attempt}", selectedOption, attempt);
                        _userInteraction.NotifyError($"{selectedOption} - AI 분석 실패 (시도 {attempt}): {ex.Message}");
                    }

                    if (!genSuccess || string.IsNullOrEmpty(specificationMarkdown))
                    {
                        // 여기서 그냥 돌아가면 앞선 시도가 만든 검증된 문서까지 함께 사라진다.
                        // 이것이 이 파일에서 가장 큰 손실이었다 — 나쁜 문서가 아니라 무(無)가 나갔다.
                        var rescued = RetryRescue.TryRescue(
                            bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.GenerationFailed);
                        if (rescued == null)
                        {
                            return Result(null, spDef, null, null);
                        }

                        _userInteraction.NotifyError(
                            $"{selectedOption} - AI 생성이 중단되어 가장 높은 점수를 받은 " +
                            $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                        finalReview = rescued.Review;
                        verificationOutcome = VerificationOutcome.QualityRejected;
                        specificationMarkdown = rescued.Markdown;
                        break;
                    }

                    // L1: 기계적 무결성 검사
                    var l1Result = _validator.Validate(specificationMarkdown);
                    specificationMarkdown = l1Result.CleansedMarkdown ?? specificationMarkdown;
                    if (!l1Result.IsValid)
                    {
                        Log.Warning("[파이프라인] L1 기계 검증 실패 - SP: {SpName}, 시도: {Attempt}, 오류 수: {ErrorCount}",
                            selectedOption, attempt, l1Result.Errors?.Count ?? 0);
                        _userInteraction.NotifyL1Errors(selectedOption, attempt, _maxAttempts, l1Result.Errors ?? new System.Collections.Generic.List<string>());

                        bool canRetry = _maxAttempts == -1 || attempt < _maxAttempts;
                        if (canRetry)
                        {
                            feedbackLog = CriticFeedbackLog.ComposeAfterL1Failure(l1Result.SuggestedPromptFix, feedbackHistory);
                            regenScope = RegenerationScopeSelector.FromL1Errors(l1Result.DetailedErrors);
                            attempt++;
                            continue;
                        }
                        else
                        {
                            Log.Error("[파이프라인] L1 기계 검증 최종 실패 - SP: {SpName}", selectedOption);

                            // 앞선 시도가 이미 L1을 통과하고 채점까지 받았다면, L1이 깨진
                            // 마지막 시도보다 그쪽이 낫다. 후보가 없을 때만 현행 경로로 간다.
                            var rescued = RetryRescue.TryRescue(
                                bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.L1Exhausted);
                            if (rescued != null)
                            {
                                _userInteraction.NotifyError(
                                    $"{selectedOption} - [[L1 기계 검증]] 최종 보완 실패. 가장 높은 점수를 받은 " +
                                    $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                                finalReview = rescued.Review;
                                verificationOutcome = VerificationOutcome.QualityRejected;
                                specificationMarkdown = rescued.Markdown;
                                break;
                            }

                            _userInteraction.NotifyError($"{selectedOption} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                            verificationOutcome = VerificationOutcome.L1Exhausted;
                            specificationMarkdown = VerificationBanner.L1Exhausted(l1Result.Errors ?? new System.Collections.Generic.List<string>()) + specificationMarkdown;
                            break;
                        }
                    }
                    else
                    {
                        Log.Debug("[파이프라인] L1 기계 검증 통과 - SP: {SpName}, 시도: {Attempt}", selectedOption, attempt);
                    }

                    // L2: AI 교차 리뷰
                    ReviewResult? l2Result = null;
                    bool reviewSuccess = false;
                    string? reviewFailureReason = null;

                    Log.Information("[파이프라인] L2 AI 교차 리뷰 시작 - SP: {SpName}, 시도: {Attempt}", selectedOption, attempt);
                    var criticEffortText = !string.IsNullOrWhiteSpace(_criticEffort) ? $", Effort: {_criticEffort}" : "";
                    _userInteraction.NotifyStatus($"[yellow]{objectStatus}[/] - AI 교차 리뷰 분석 중 ({_criticService.ProviderName} - {_criticService.ModelName}{criticEffortText})...");
                    try
                    {
                        using (var progressScope = _userInteraction.CreateProgressScope("L2 교차 리뷰") ?? NullProgressScope.Instance)
                        {
                            progressScope.AddTask("review", $"{_criticService.ModelName} 리뷰 중...");
                            l2Result = await WrapWithProgress(_criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken), progressScope, "review");
                        }
                        reviewSuccess = true;
                        accumulatedThinking.AppendLine($"### [Critic] Attempt {attempt} Review Thinking");
                        accumulatedThinking.AppendLine($"- **AI Provider**: {_criticService.ProviderName}");
                        accumulatedThinking.AppendLine($"- **AI Model**: {_criticService.ModelName} (Effort: {_criticEffort ?? "default"})");
                        accumulatedThinking.AppendLine();
                        if (l2Result != null && !string.IsNullOrWhiteSpace(l2Result.ThinkingText))
                        {
                            accumulatedThinking.AppendLine(l2Result.ThinkingText);
                        }
                        else
                        {
                            accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_criticService.ProviderName));
                        }
                        accumulatedThinking.AppendLine();
                        Log.Debug("[파이프라인] L2 AI 교차 리뷰 완료 - SP: {SpName}, 결함 감지: {HasDefects}",
                            selectedOption, l2Result?.HasDefects);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Error(ex, "[파이프라인] L2 AI 교차 리뷰 예외 - SP: {SpName}, 시도: {Attempt}", selectedOption, attempt);
                        reviewFailureReason = ex.Message;
                        _userInteraction.NotifyError($"{selectedOption} - AI 교차 리뷰 실패 (시도 {attempt}): {ex.Message}");
                    }

                    if (reviewSuccess && l2Result != null)
                    {
                        // [수정] 감쇄 임계치(Decay)를 전면 비활성화하여 항상 설정된 기준 점수(_criticScoreThreshold)를 강제
                        bool overriddenHasDefects = false;
                        
                        if (l2Result.ScoreAccuracy < _criticScoreThreshold ||
                            l2Result.ScoreCrud < _criticScoreThreshold ||
                            l2Result.ScoreInterface < _criticScoreThreshold ||
                            l2Result.ScoreException < _criticScoreThreshold ||
                            l2Result.ScoreReadability < _criticScoreThreshold)
                        {
                            overriddenHasDefects = true;
                        }

                        if (overriddenHasDefects)
                        {
                            l2Result.HasDefects = true;
                        }
                    }

                    // 불합격 여부와 무관하게 후보로 등록한다. 재시도가 소진됐을 때
                    // 마지막이 아니라 가장 좋은 것을 채택하기 위해서다.
                    // specificationMarkdown은 이 시점에 L1 정화가 끝난 값이다.
                    if (reviewSuccess && l2Result != null)
                    {
                        bestAttempt.TryRecord(attempt, specificationMarkdown, l2Result, null);
                    }

                    if (reviewSuccess && l2Result != null && l2Result.HasDefects)
                    {
                        Log.Warning("[파이프라인] L2 AI 교차 리뷰 결함 발견 - SP: {SpName}, 시도: {Attempt}", selectedOption, attempt);
                        _userInteraction.NotifyL2Defects(selectedOption, attempt, _maxAttempts, l2Result.FeedbackComment ?? string.Empty);

                        bool canRetry = _maxAttempts == -1 || attempt < _maxAttempts;
                        if (canRetry)
                        {
                            regenScope = RegenerationScopeSelector.FromReview(l2Result, _criticScoreThreshold);
                            CriticFeedbackLog.Record(feedbackHistory, attempt, l2Result, _criticScoreThreshold);
                            feedbackLog = CriticFeedbackLog.Compose(
                                feedbackHistory,
                                "※ 지시사항: 위 지적사항을 모두 반영하여 본문을 수정하십시오. " +
                                "이전 라운드에서 이미 기준 점수를 통과한 항목의 서술 수준을 낮추지 마십시오. " +
                                "원본 DDL과 위 피드백을 절대적 기준으로 삼으십시오.");
                            attempt++;
                            continue;
                        }
                        else
                        {
                            Log.Error("[파이프라인] L2 AI 교차 리뷰 최종 실패 - SP: {SpName}", selectedOption);

                            // 마지막이 아니라 최고점을 채택한다. 채택 규칙은 RetryRescue가
                            // 단독으로 소유한다. 정상 소진이므로 중단 사유는 null이다.
                            // 이 분기에 도달했다는 것은 직전 시도의 리뷰가 성공했다는 뜻이라
                            // 후보는 반드시 존재하지만, 루프가 바뀌어도 깨지지 않도록 폴백을 둔다.
                            var rescued = RetryRescue.TryRescue(bestAttempt, _criticScoreThreshold, attempt, null);
                            var adoptedReview = rescued?.Review ?? l2Result;
                            var adoptedNumber = rescued?.AttemptNumber ?? attempt;

                            _userInteraction.NotifyError(
                                $"{selectedOption} - [[L2 AI 리뷰]] 최종 보완 실패. " +
                                $"가장 높은 점수를 받은 {adoptedNumber}차 시도({adoptedReview.NormalizedScore}/100)를 채택합니다.");

                            finalReview = adoptedReview;
                            verificationOutcome = VerificationOutcome.QualityRejected;
                            specificationMarkdown = rescued?.Markdown
                                ?? VerificationBanner.QualityRejected(adoptedReview, _criticScoreThreshold) + specificationMarkdown;
                            break;
                        }
                    }

                    // 리뷰를 수행하지 못한 경우: 소프트 페일로 계속 진행하되 통과와 구분해 표시한다.
                    if (!reviewSuccess)
                    {
                        // 앞선 시도가 리뷰를 마쳤다면 미검토 문서보다 그쪽이 낫다.
                        var rescued = RetryRescue.TryRescue(
                            bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.ReviewFailed);
                        if (rescued != null)
                        {
                            _userInteraction.NotifyError(
                                $"{selectedOption} - [[L2 AI 리뷰]] 를 수행하지 못해 가장 높은 점수를 받은 " +
                                $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                            finalReview = rescued.Review;
                            verificationOutcome = VerificationOutcome.QualityRejected;
                            specificationMarkdown = rescued.Markdown;
                            break;
                        }

                        _userInteraction.NotifyError(
                            $"{selectedOption} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 명세서를 확정합니다.");
                        verificationOutcome = VerificationOutcome.ReviewNotRun;
                        specificationMarkdown =
                            VerificationBanner.ReviewNotRun(reviewFailureReason ?? "사유가 기록되지 않았습니다.") + specificationMarkdown;
                        break;
                    }

                    // 검증을 통과한 경우 루프 탈출
                    if (l1Result.IsValid && (l2Result == null || !l2Result.HasDefects))
                    {
                        Log.Information("[파이프라인] L1+L2 검증 최종 통과 - SP: {SpName}, 최종 시도 횟수: {Attempt}", selectedOption, attempt);
                        finalReview = l2Result;
                        if (l2Result != null && !string.IsNullOrWhiteSpace(l2Result.FeedbackComment))
                        {
                            var commentLines = l2Result.FeedbackComment.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in commentLines)
                            {
                                _userInteraction.NotifyStatus($"  [grey]* Critic 피드백: {EscapeMarkup(line)}[/]");
                            }
                        }
                        _userInteraction.NotifyValidationSuccess(selectedOption);
                        break;
                    }
                }
            }
            // 배치 모드 성공 완료 시 캐시 업데이트.
            // 검증되지 않은 문서(L1 미통과/품질 미달/리뷰 미수행)를 캐시하면 다음 실행이
            // 캐시 히트로 그 문서를 그대로 재사용하면서 "통과"로 재포장하게 된다.
            // 재분석 비용은 감수해도 거짓 통과는 안 되므로 통과 상태에서만 캐시를 쓴다.
            if (isBatchMode &&
                enableCache &&
                cacheObjectKey != null &&
                outputPaths != null &&
                !string.IsNullOrEmpty(compositeHash) &&
                verificationOutcome == VerificationOutcome.Passed)
            {
                Log.Debug("[파이프라인] 배치 모드 캐시 업데이트 - SP: {SpName}", selectedOption);
                _cacheManager.UpdateCache(
                    cacheObjectKey,
                    spDef,
                    compositeHash,
                    outputPaths,
                    specificationMarkdown);
            }

            // DB 역반영 여부 선택과 관계없이 항상 파일로 스크립트 저장
            ExportMetadataCleansingSql(specificationMarkdown, selectedOption, cleansingFileBaseName, outputDirectory);

            // L3: 인간 개입형 승인 (TUI 모드 한정)
            if (!isBatchMode)
            {
                while (true)
                {
                    var reviewResult = await _userInteraction.RequestHumanReviewAsync(selectedOption, specificationMarkdown, verificationOutcome);

                    if (reviewResult.Decision == UserDecision.Approve)
                    {
                        // 최종 승인 시 캐시 업데이트. 검증되지 않은(통과가 아닌) 문서를
                        // 통과처럼 캐시하지 않는다 — 재분석 비용은 감수해도 거짓 통과는 안 된다.
                        if (enableCache &&
                            cacheObjectKey != null &&
                            outputPaths != null &&
                            !string.IsNullOrEmpty(compositeHash) &&
                            verificationOutcome == VerificationOutcome.Passed)
                        {
                            _cacheManager.UpdateCache(
                                cacheObjectKey,
                                spDef,
                                compositeHash,
                                outputPaths,
                                specificationMarkdown);
                        }

                        // 생성된 DB 역반영 쿼리가 존재할 경우에만 동기화 수행 여부 묻기
                        var sqlPath = System.IO.Path.Combine(outputDirectory, "cleansing", $"{cleansingFileBaseName}_MetadataCleansing.sql");
                        if (System.IO.File.Exists(sqlPath))
                        {
                            var syncApproved = await _userInteraction.ConfirmMetadataSyncAsync(selectedOption);
                            if (syncApproved)
                            {
                                await ApplyMetadataCleansingSqlAsync(
                                    connectionString,
                                    key,
                                    selectedOption,
                                    cleansingFileBaseName,
                                    outputDirectory,
                                    cancellationToken);
                            }
                        }

                        return Result(specificationMarkdown, spDef, finalReview, accumulatedThinking.ToString());
                    }
                    else if (reviewResult.Decision == UserDecision.Cancel)
                    {
                        return Result(null, spDef, null, null);
                    }
                    else if (reviewResult.Decision == UserDecision.ProvideFeedback)
                    {
                        if (string.IsNullOrWhiteSpace(reviewResult.UserFeedback))
                        {
                            continue;
                        }

                        _userInteraction.NotifyStatus($"[yellow]{objectStatus}[/] - 피드백 반영 재생성 중...");
                        var humanFeedbackLog = $"[L3 사용자 보완 피드백 로그]:\n{reviewResult.UserFeedback}";

                        string reSpec = string.Empty;
                        try
                        {
                            if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(provider) && spDef.ObjectType == CodeObjectType.Procedure)
                            {
                                bool regenPart1 = false;
                                bool regenPart2 = false;
                                bool regenPart3 = false;

                                var logUpper = humanFeedbackLog.ToUpper();
                                if (logUpper.Contains("## 개요") || logUpper.Contains("## 파라미터 목록") || logUpper.Contains("개요") || logUpper.Contains("파라미터") || logUpper.Contains("PARAMETER") || logUpper.Contains("OVERVIEW"))
                                {
                                    regenPart1 = true;
                                }
                                if (logUpper.Contains("## CRUD 분석") || logUpper.Contains("CRUD") || logUpper.Contains("테이블") || logUpper.Contains("컬럼") || logUpper.Contains("매핑") || logUpper.Contains("TABLE") || logUpper.Contains("COLUMN") || logUpper.Contains("MAPPING"))
                                {
                                    regenPart2 = true;
                                }
                                if (logUpper.Contains("## 로직 흐름 요약") || logUpper.Contains("## 비즈니스 흐름 시각화") || logUpper.Contains("로직 흐름") || logUpper.Contains("시각화") || logUpper.Contains("MERMAID") || logUpper.Contains("다이어그램") || logUpper.Contains("FLOWCHART") || logUpper.Contains("DIAGRAM") || logUpper.Contains("LOGIC") || logUpper.Contains("VISUALIZATION"))
                                {
                                    regenPart3 = true;
                                }

                                if (ollamaPart1 == null || ollamaPart2 == null || ollamaPart3 == null)
                                {
                                    regenPart1 = regenPart2 = regenPart3 = true;
                                }

                                if (!regenPart1 && !regenPart2 && !regenPart3)
                                {
                                    regenPart1 = regenPart2 = regenPart3 = true;
                                }

                                var tasksList = new List<Task<AiResult>>();
                                var taskOrder = new List<string>();

                                using (var progressScope = _userInteraction.CreateProgressScope("구역별 L3 피드백 재생성") ?? NullProgressScope.Instance)
                                {
                                    if (regenPart1)
                                    {
                                        progressScope.AddTask("part1", "2/4. 개요 및 파라미터 피드백 반영 중...");
                                        tasksList.Add(WrapWithProgress(_aiService.GenerateSpecSectionAsync(spDef, "OverviewAndParameters", instructions, humanFeedbackLog, _actorEffort, cancellationToken), progressScope, "part1"));
                                        taskOrder.Add("part1");
                                    }
                                    if (regenPart2)
                                    {
                                        progressScope.AddTask("part2", "3/4. CRUD 상세 피드백 반영 중...");
                                        tasksList.Add(WrapWithProgress(_aiService.GenerateSpecSectionAsync(spDef, "CrudAnalysis", instructions, humanFeedbackLog, _actorEffort, cancellationToken), progressScope, "part2"));
                                        taskOrder.Add("part2");
                                    }
                                    if (regenPart3)
                                    {
                                        progressScope.AddTask("part3", "4/4. 로직 요약 및 시각화 피드백 반영 중...");
                                        tasksList.Add(WrapWithProgress(_aiService.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", instructions, humanFeedbackLog, _actorEffort, cancellationToken), progressScope, "part3"));
                                        taskOrder.Add("part3");
                                    }

                                    var results = await Task.WhenAll(tasksList);
                                    for (int i = 0; i < taskOrder.Count; i++)
                                    {
                                        if (taskOrder[i] == "part1") ollamaPart1 = results[i];
                                        else if (taskOrder[i] == "part2") ollamaPart2 = results[i];
                                        else if (taskOrder[i] == "part3") ollamaPart3 = results[i];
                                    }
                                }

                                reSpec = $"{ollamaPart1!.Content.Trim()}\n\n{ollamaPart2!.Content.Trim()}\n\n{ollamaPart3!.Content.Trim()}";
                                spDef.RawPromptContext = $"=== [Part 1: System Prompt] ===\n{ollamaPart1.SystemPrompt}\n\n=== [Part 2: System Prompt] ===\n{ollamaPart2.SystemPrompt}\n\n=== [Part 3: System Prompt] ===\n{ollamaPart3.SystemPrompt}";

                                accumulatedThinking.AppendLine("### [Actor] Human Feedback Refinement Part 1 Thinking");
                                accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(ollamaPart1.ThinkingText) ? "*(추론 없음)*" : ollamaPart1.ThinkingText);
                                accumulatedThinking.AppendLine();
                                accumulatedThinking.AppendLine("### [Actor] Human Feedback Refinement Part 2 Thinking");
                                accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(ollamaPart2.ThinkingText) ? "*(추론 없음)*" : ollamaPart2.ThinkingText);
                                accumulatedThinking.AppendLine();
                                accumulatedThinking.AppendLine("### [Actor] Human Feedback Refinement Part 3 Thinking");
                                accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(ollamaPart3.ThinkingText) ? "*(추론 없음)*" : ollamaPart3.ThinkingText);
                                accumulatedThinking.AppendLine();
                            }
                            else
                            {
                                AiResult aiResult;
                                using (var progressScope = _userInteraction.CreateProgressScope("L3 피드백 재생성") ?? NullProgressScope.Instance)
                                {
                                    progressScope.AddTask("l3gen", $"{_aiService.ModelName} 피드백 반영 중...");
                                    aiResult = await WrapWithProgress(_aiService.GenerateSpecificationAsync(spDef, instructions, humanFeedbackLog, _actorEffort, cancellationToken), progressScope, "l3gen");
                                }
                                reSpec = aiResult.Content;
                                spDef.RawPromptContext = $"=== [System Prompt] ===\n{aiResult.SystemPrompt}\n\n=== [User Prompt] ===\n{aiResult.UserPrompt}";

                                accumulatedThinking.AppendLine("### [Actor] Human Feedback Refinement Thinking");
                                accumulatedThinking.AppendLine($"- **AI Provider**: {_aiService.ProviderName}");
                                accumulatedThinking.AppendLine($"- **AI Model**: {_aiService.ModelName} (Effort: {_actorEffort ?? "default"})");
                                accumulatedThinking.AppendLine();
                                if (aiResult != null && !string.IsNullOrWhiteSpace(aiResult.ThinkingText))
                                {
                                    accumulatedThinking.AppendLine(aiResult.ThinkingText);
                                }
                                else
                                {
                                    accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_aiService.ProviderName));
                                }
                                accumulatedThinking.AppendLine();
                            }
                        }
                        // 여기서 취소를 삼키면 아래 continue가 돌아 같은 승인 화면을 다시 띄운다.
                        // 사용자의 Ctrl-C가 무시되고 같은 질문을 다시 받는 것이므로 취소는 전파한다.
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _userInteraction.NotifyError($"피드백 반영 재생성 실패: {ex.Message}");
                        }

                        if (string.IsNullOrEmpty(reSpec))
                        {
                            continue;
                        }

                        // 피드백 반영본에 대한 L1 정적 검사 1회 수행
                        var l1Re = _validator.Validate(reSpec);
                        reSpec = l1Re.CleansedMarkdown ?? reSpec;
                        if (!l1Re.IsValid)
                        {
                            _userInteraction.NotifyStatus("피드백 적용본에서 정적 에러가 검출되어 AI 자가 수정 1회 더 진행합니다.");
                            try
                            {
                                if (ReSet.Core.Services.Clients.AiClientFactory.IsLocalProvider(provider) && spDef.ObjectType == CodeObjectType.Procedure)
                                {
                                    bool regenPart1 = false;
                                    bool regenPart2 = false;
                                    bool regenPart3 = false;

                                    var logUpper = (l1Re.SuggestedPromptFix ?? "").ToUpper();
                                    if (logUpper.Contains("## 개요") || logUpper.Contains("## 파라미터 목록") || logUpper.Contains("개요") || logUpper.Contains("파라미터") || logUpper.Contains("PARAMETER") || logUpper.Contains("OVERVIEW"))
                                    {
                                        regenPart1 = true;
                                    }
                                    if (logUpper.Contains("## CRUD 분석") || logUpper.Contains("CRUD") || logUpper.Contains("테이블") || logUpper.Contains("컬럼") || logUpper.Contains("매핑") || logUpper.Contains("TABLE") || logUpper.Contains("COLUMN") || logUpper.Contains("MAPPING"))
                                    {
                                        regenPart2 = true;
                                    }
                                    if (logUpper.Contains("## 로직 흐름 요약") || logUpper.Contains("## 비즈니스 흐름 시각화") || logUpper.Contains("로직 흐름") || logUpper.Contains("시각화") || logUpper.Contains("MERMAID") || logUpper.Contains("다이어그램") || logUpper.Contains("FLOWCHART") || logUpper.Contains("DIAGRAM") || logUpper.Contains("LOGIC") || logUpper.Contains("VISUALIZATION"))
                                    {
                                        regenPart3 = true;
                                    }

                                    if (ollamaPart1 == null || ollamaPart2 == null || ollamaPart3 == null)
                                    {
                                        regenPart1 = regenPart2 = regenPart3 = true;
                                    }

                                    if (!regenPart1 && !regenPart2 && !regenPart3)
                                    {
                                        regenPart1 = regenPart2 = regenPart3 = true;
                                    }

                                    var tasksList = new List<Task<AiResult>>();
                                    var taskOrder = new List<string>();

                                    using (var progressScope = _userInteraction.CreateProgressScope("구역별 L3 자가 수정") ?? NullProgressScope.Instance)
                                    {
                                        if (regenPart1)
                                        {
                                            progressScope.AddTask("part1", "2/4. 개요 및 파라미터 L1 수정 중...");
                                            tasksList.Add(WrapWithProgress(_aiService.GenerateSpecSectionAsync(spDef, "OverviewAndParameters", instructions, l1Re.SuggestedPromptFix, _actorEffort, cancellationToken), progressScope, "part1"));
                                            taskOrder.Add("part1");
                                        }
                                        if (regenPart2)
                                        {
                                            progressScope.AddTask("part2", "3/4. CRUD 상세 L1 수정 중...");
                                            tasksList.Add(WrapWithProgress(_aiService.GenerateSpecSectionAsync(spDef, "CrudAnalysis", instructions, l1Re.SuggestedPromptFix, _actorEffort, cancellationToken), progressScope, "part2"));
                                            taskOrder.Add("part2");
                                        }
                                        if (regenPart3)
                                        {
                                            progressScope.AddTask("part3", "4/4. 로직 요약 및 시각화 L1 수정 중...");
                                            tasksList.Add(WrapWithProgress(_aiService.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", instructions, l1Re.SuggestedPromptFix, _actorEffort, cancellationToken), progressScope, "part3"));
                                            taskOrder.Add("part3");
                                        }

                                        var results = await Task.WhenAll(tasksList);
                                        for (int i = 0; i < taskOrder.Count; i++)
                                        {
                                            if (taskOrder[i] == "part1") ollamaPart1 = results[i];
                                            else if (taskOrder[i] == "part2") ollamaPart2 = results[i];
                                            else if (taskOrder[i] == "part3") ollamaPart3 = results[i];
                                        }
                                    }

                                    reSpec = $"{ollamaPart1!.Content.Trim()}\n\n{ollamaPart2!.Content.Trim()}\n\n{ollamaPart3!.Content.Trim()}";
                                    spDef.RawPromptContext = $"=== [Part 1: System Prompt] ===\n{ollamaPart1.SystemPrompt}\n\n=== [Part 2: System Prompt] ===\n{ollamaPart2.SystemPrompt}\n\n=== [Part 3: System Prompt] ===\n{ollamaPart3.SystemPrompt}";

                                    accumulatedThinking.AppendLine("### [Actor] Human Feedback L1 Correction Part 1 Thinking");
                                    accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(ollamaPart1.ThinkingText) ? "*(추론 없음)*" : ollamaPart1.ThinkingText);
                                    accumulatedThinking.AppendLine();
                                    accumulatedThinking.AppendLine("### [Actor] Human Feedback L1 Correction Part 2 Thinking");
                                    accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(ollamaPart2.ThinkingText) ? "*(추론 없음)*" : ollamaPart2.ThinkingText);
                                    accumulatedThinking.AppendLine();
                                    accumulatedThinking.AppendLine("### [Actor] Human Feedback L1 Correction Part 3 Thinking");
                                    accumulatedThinking.AppendLine(string.IsNullOrWhiteSpace(ollamaPart3.ThinkingText) ? "*(추론 없음)*" : ollamaPart3.ThinkingText);
                                    accumulatedThinking.AppendLine();
                                }
                                else
                                {
                                    AiResult aiResult;
                                    using (var progressScope = _userInteraction.CreateProgressScope("L3 자가 수정") ?? NullProgressScope.Instance)
                                    {
                                        progressScope.AddTask("l3fix", $"{_aiService.ModelName} L1 자가 수정 중...");
                                        aiResult = await WrapWithProgress(_aiService.GenerateSpecificationAsync(spDef, instructions, l1Re.SuggestedPromptFix, _actorEffort, cancellationToken), progressScope, "l3fix");
                                    }
                                    reSpec = aiResult.Content;
                                    spDef.RawPromptContext = $"=== [System Prompt] ===\n{aiResult.SystemPrompt}\n\n=== [User Prompt] ===\n{aiResult.UserPrompt}";

                                    accumulatedThinking.AppendLine("### [Actor] Human Feedback Self-Correction Thinking");
                                    accumulatedThinking.AppendLine($"- **AI Provider**: {_aiService.ProviderName}");
                                    accumulatedThinking.AppendLine($"- **AI Model**: {_aiService.ModelName} (Effort: {_actorEffort ?? "default"})");
                                    accumulatedThinking.AppendLine();
                                    if (aiResult != null && !string.IsNullOrWhiteSpace(aiResult.ThinkingText))
                                    {
                                        accumulatedThinking.AppendLine(aiResult.ThinkingText);
                                    }
                                    else
                                    {
                                        accumulatedThinking.AppendLine(ThinkingLogPlaceholder.For(_aiService.ProviderName));
                                    }
                                    accumulatedThinking.AppendLine();
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                Log.Warning(ex, "명세서 L3 피드백 반영 재생성 실패");
                            }
                        }

                        // 피드백 반영본은 전체가 재생성되어 이전 배너/본문이 사라지고,
                        // L1만 다시 확인했을 뿐 L2는 재수행되지 않는다. 이전 검토 결과(finalReview)와
                        // 통과 판정(verificationOutcome)을 그대로 들고 가면 "재생성된, 한 번도
                        // 리뷰받지 않은 문서가 이전 문서의 점수로 통과를 자칭"하는 새 거짓 주장이
                        // 된다. 리뷰 미수행으로 명시하고 점수를 비운다.
                        specificationMarkdown = reSpec;
                        finalReview = null;
                        verificationOutcome = VerificationOutcome.ReviewNotRun;
                    }
                }
            }

            return Result(specificationMarkdown, spDef, finalReview, accumulatedThinking.ToString());
        }

        private static CodeObjectKey ResolveCacheObjectKey(
            SpDefinition spDefinition,
            CodeObjectKey requestedKey)
        {
            if (spDefinition.ObjectKey != null)
            {
                return spDefinition.ObjectKey;
            }

            return requestedKey;
        }

        private static (string Specification, ReviewResult Review, DateTime? AnalyzedAt)
            ParseCachedSpecification(string cachedArtifact)
        {
            // 캐시는 통과(Passed) 문서만 쓰이므로(위 캐시 저장부 참고) 정상적으로는 이
            // 폴백이 쓰일 일이 없다. 그럼에도 필드가 비어 있는 경우 만점(10)을 지어내지
            // 않는다 — 검증되지 않은 사실을 완벽한 점수로 둔갑시키는 것이 바로 이 결함의
            // 본질이었다. 0으로 안전하게 폴백한다.
            var review = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 0,
                ScoreCrud = 0,
                ScoreInterface = 0,
                ScoreException = 0,
                ScoreReadability = 0
            };
            var specification = cachedArtifact ?? string.Empty;
            var yaml = Regex.Match(
                specification,
                @"\A---\r?\n(?<content>.*?)\r?\n---(?:\r?\n|\z)",
                RegexOptions.Singleline);
            if (yaml.Success)
            {
                var yamlContent = yaml.Groups["content"].Value;
                review.ScoreAccuracy = ParseCachedScore(
                    yamlContent,
                    "정합성 점수",
                    review.ScoreAccuracy);
                review.ScoreCrud = ParseCachedScore(
                    yamlContent,
                    "CRUD 점수",
                    review.ScoreCrud);
                review.ScoreInterface = ParseCachedScore(
                    yamlContent,
                    "인터페이스 점수",
                    review.ScoreInterface);
                review.ScoreReadability = ParseCachedScore(
                    yamlContent,
                    "가독성 점수",
                    review.ScoreReadability);
                review.ScoreException = ParseCachedScore(
                    yamlContent,
                    "예외처리 점수",
                    review.ScoreException);
                specification = specification[yaml.Length..];
            }

            // NOTE 블록을 지우기 전에 원본 분석 시각을 확보한다. 캐시 히트는 AI를
            // 호출하지 않았으므로 이 값을 그대로 다시 써야 새 날짜가 찍히지 않는다.
            DateTime? analyzedAt = null;
            var stampMatch = Regex.Match(
                specification,
                @"(?m)^>\s*\*\*문서 작성일시\*\*:\s*(?<stamp>[^\r\n]+?)\s*$");
            if (stampMatch.Success &&
                DateTime.TryParse(
                    stampMatch.Groups["stamp"].Value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsedStamp))
            {
                analyzedAt = parsedStamp;
            }
            else
            {
                // A가 레거시 캐시를 전량 무효화하므로 히트하는 문서는 반드시 신형
                // 포맷이다. 여기 도달했다면 포매터 출력이 깨졌다는 뜻이고, 그 사실이
                // 날짜보다 중요하다.
                Log.Warning("[파이프라인] 캐시 문서에서 작성일시를 읽지 못했습니다.");
            }

            specification = Regex.Replace(
                specification.TrimStart('\r', '\n'),
                @"\A> \[!NOTE\][^\r\n]*(?:\r?\n)(?:>[^\r\n]*(?:\r?\n|$))*\s*",
                string.Empty);
            specification = Regex.Replace(
                specification,
                @"\A> \*\*AI 최종 신뢰도\*\*:[^\r\n]*(?:\r?\n|\z)\s*",
                string.Empty);
            return (specification.TrimStart('\r', '\n'), review, analyzedAt);
        }

        private static int ParseCachedScore(
            string yamlContent,
            string label,
            int fallback)
        {
            var match = Regex.Match(
                yamlContent,
                $@"(?m)^{Regex.Escape(label)}:\s*(?<score>\d+)/10\b");
            return match.Success &&
                   int.TryParse(match.Groups["score"].Value, out var score)
                ? score
                : fallback;
        }

        /// <summary>연결 문자열의 InitialCatalog를 꺼낸다. 없거나 파싱 불가면 null.</summary>
        public static string? ResolveCurrentDatabase(string connectionString)
        {
            try
            {
                var database = new SqlConnectionStringBuilder(connectionString)
                    .InitialCatalog;
                return string.IsNullOrWhiteSpace(database)
                    ? null
                    : database;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// 비재귀(참조분석 OFF) 경로가 <see cref="RunCodeObjectPipelineAsync"/>에 넘길
        /// 프로시저 키를 만든다. 연결 문자열에서 DB명을 못 얻으면 빈 문자열을 쓴다.
        /// </summary>
        /// <remarks>
        /// 프로덕션 호출부와 테스트가 각자 같은 조립 로직을 갖고 있으면 테스트가
        /// 프로덕션이 아니라 사본을 검증하게 된다. 그 사본을 없애기 위한 단일 지점이다.
        /// </remarks>
        public static CodeObjectKey CreateProcedureKey(
            string connectionString,
            string schema,
            string name)
        {
            var database = ResolveCurrentDatabase(connectionString) ?? string.Empty;
            return CodeObjectKey.Create(database, schema, name, CodeObjectType.Procedure);
        }

        public async Task<ConsolidatedPipelineResult> RunConsolidatedPipelineAsync(
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            string provider,
            string outputRoot,
            bool isBatchMode = false,
            CancellationToken cancellationToken = default)
        {
            // 호출부 결함이므로 CWD로 조용히 폴백하지 않고 즉시 드러낸다.
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                throw new ArgumentException("출력 디렉터리가 필요합니다.", nameof(outputRoot));
            }

            string? feedbackLog = null;
            var feedbackHistory = new System.Collections.Generic.List<string>();
            string consolidatedPlan = string.Empty;
            AiResult? finalAiResult = null;
            string currentPlanStructure = string.Empty;
            // 재수립 시점에 필요한데 기존에는 if 블록 안에서만 살아 있었다.
            // 목차가 있으면 브레인스토밍도 반드시 있다(둘은 한 몸으로만 실행된다).
            string currentBrainstorming = string.Empty;
            // 최고점 후보(BestAttempt.Current)를 실제로 만들어 낸 목차. 후보와 한 몸으로
            // 움직여야 한다. 목차 재수립 뒤 회차가 더 낮은 점수를 내면 RetryRescue가
            // 재수립 이전 후보를 채택하는데, 그때 PlanStructure.md에 최신 목차가 남아
            // 있으면 산출된 문서를 한 번도 만든 적 없는 목차를 가리키게 된다.
            string bestAttemptStructure = string.Empty;
            // 정체 판정과 1회 상한은 이 정책이 단독으로 소유한다.
            var redraftPolicy = new StructureRedraftPolicy();
            // 계획서의 종료 상태와 그 근거 리뷰. 반환 레코드로 호출부까지 전달되어
            // 산출물 헤더(VerificationDocumentFormatter.FormatVerifiedDocument)와
            // 승인 화면(RequestHumanReviewAsync)이 같은 사실을 쓴다.
            var planOutcome = VerificationOutcome.Passed;
            ReviewResult? planReview = null;

            // 분할 생성 상태. 지목 재생성이 골격과 통과한 단계를 재사용하려면
            // 회차를 넘어 살아 있어야 한다.
            IReadOnlyList<BatchStepPlan>? currentSteps = null;
            string? lastSkeleton = null;
            Dictionary<string, string>? lastStepSections = null;
            // Code -> "{Code} (하한 미달)" 형식의 표시 문자열. 사전으로 두는 이유는
            // 지목 재생성이 건드리지 않은 단계의 항목을 그대로 보존해야 하기
            // 때문이다 — 목록을 통째로 교체하면 재생성되지 않은 단계의 하한 미달
            // 기록이 조용히 사라진다.
            var stepFloorViolations = new Dictionary<string, string>();
            var pendingDefectiveSteps = new List<string>();

            // 설정에 따른 최대 시도 횟수 적용 (N회 또는 검증 완료까지)
            int attempt = 1;
            var bestAttempt = new BestAttempt();
            while (true)
            {
                var attemptText = attempt == 1 ? "1차 분석" : $"자가 수정 보완 ({attempt}회째)";
                bool genSuccess = false;

                var consolidatorEffortText = !string.IsNullOrWhiteSpace(_consolidatorEffort) ? $", Effort: {_consolidatorEffort}" : "";
                _userInteraction.NotifyStatus($"[yellow]{jobName}[/] - AI 통합 배치 전환 계획 수립 중 ({_consolidatorService.ProviderName} - {_consolidatorService.ModelName}{consolidatorEffortText}) [[{attemptText}]]...");
                try
                {
                    var specsCopy = new System.Collections.Generic.List<(string FileName, string Content)>(specs);
                    if (!string.IsNullOrEmpty(feedbackLog))
                    {
                        specsCopy.Add(("Feedback_Log.txt", $"[이전 시도에 대한 검토 피드백]:\n{feedbackLog}\n위 에러/피드백 사항을 전적으로 수용하여 통합 설계서를 완성해 주세요."));
                    }

                    AiResult aiResult = new AiResult();
                    string? splitMarkdown = null;
                    using (var progressScope = _userInteraction.CreateProgressScope("배치 계획 수립") ?? NullProgressScope.Instance)
                    {
                        if (string.IsNullOrEmpty(currentPlanStructure))
                        {
                            progressScope.AddTask("phase1", "1/3. 브레인스토밍 중...");
                            var brainstormResult = await WrapWithProgress(_consolidatorService.BrainstormBatchPlanAsync(specsCopy, targetLanguage, jobName, _consolidatorEffort, cancellationToken), progressScope, "phase1");

                            var rawDir = System.IO.Path.Combine(outputRoot, "Jobs", jobName, "raw");
                            if (!System.IO.Directory.Exists(rawDir)) System.IO.Directory.CreateDirectory(rawDir);
                            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(rawDir, "Brainstorming.md"), brainstormResult.Content);
                            currentBrainstorming = brainstormResult.Content;

                            progressScope.AddTask("phase2", "2/3. 목차 설계 중...");
                            var planResult = await WrapWithProgress(_consolidatorService.DraftBatchPlanStructureAsync(brainstormResult.Content, targetLanguage, jobName, _consolidatorEffort, cancellationToken: cancellationToken), progressScope, "phase2");
                            currentPlanStructure = planResult.Content;
                            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(rawDir, "PlanStructure.md"), currentPlanStructure);
                        }

                        progressScope.AddTask("phase3", "3/3. 최종 생성 중...");

                        // 목차가 단계 목록을 냈을 때만 분할한다. 못 냈으면 조용히
                        // 현행 단일 호출로 폴백한다 — 분할은 개선이지 필수가 아니다.
                        currentSteps = BatchStepPlanParser.TryParse(currentPlanStructure);
                        if (currentSteps != null)
                        {
                            var split = await GenerateBySplitAsync(
                                currentPlanStructure, currentSteps, specsCopy, targetLanguage, jobName,
                                progressScope, lastSkeleton, lastStepSections, stepFloorViolations,
                                pendingDefectiveSteps, cancellationToken);

                            if (split != null)
                            {
                                splitMarkdown = split.Markdown;
                                aiResult = split.Generation;
                                lastSkeleton = split.Skeleton;
                                lastStepSections = split.Sections;
                                stepFloorViolations = split.FloorViolations;
                            }
                            else
                            {
                                _userInteraction.NotifyError($"{jobName} - 골격 생성에 실패하여 단일 호출로 계획서를 생성합니다.");
                            }
                        }

                        pendingDefectiveSteps.Clear();

                        if (splitMarkdown == null)
                        {
                            aiResult = await WrapWithProgress(_consolidatorService.GenerateConsolidatedBatchPlanAsync(currentPlanStructure, specsCopy, targetLanguage, jobName, _consolidatorEffort, cancellationToken), progressScope, "phase3");
                        }
                    }
                    consolidatedPlan = splitMarkdown ?? aiResult.Content;
                    finalAiResult = aiResult;
                    genSuccess = true;
                }
                // 취소를 삼키면 실패로 위장한 정상 반환이 되어 취소 사실이 사라진다.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _userInteraction.NotifyError($"{jobName} - AI 통합 계획 생성 실패 (시도 {attempt}): {ex.Message}");
                }

                if (!genSuccess || string.IsNullOrEmpty(consolidatedPlan))
                {
                    // 여기서 그냥 돌아가면 앞선 시도가 만든 검증된 계획서까지 함께 사라진다.
                    var rescued = RetryRescue.TryRescue(
                        bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.GenerationFailed);
                    if (rescued == null)
                    {
                        return new ConsolidatedPipelineResult(null, null, null, planOutcome);
                    }

                    _userInteraction.NotifyError(
                        $"{jobName} - AI 생성이 중단되어 가장 높은 점수를 받은 " +
                        $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                    currentPlanStructure = await AdoptPlanStructureForRescueAsync(
                        outputRoot, jobName, currentPlanStructure, bestAttemptStructure, cancellationToken);
                    finalAiResult = rescued.Generation ?? finalAiResult;
                    planReview = rescued.Review;
                    planOutcome = VerificationOutcome.QualityRejected;
                    consolidatedPlan = rescued.Markdown;
                    break;
                }

                // L1: 기계적 무결성 검사
                var l1Result = _validator.ValidateConsolidated(consolidatedPlan);
                consolidatedPlan = l1Result.CleansedMarkdown ?? consolidatedPlan;
                if (!l1Result.IsValid)
                {
                    _userInteraction.NotifyL1Errors(jobName, attempt, _maxAttempts, l1Result.Errors);

                    bool canRetry = _maxAttempts == -1 || attempt < _maxAttempts;
                    if (canRetry)
                    {
                        feedbackLog = CriticFeedbackLog.ComposeAfterL1Failure(l1Result.SuggestedPromptFix, feedbackHistory);
                        attempt++;
                        continue;
                    }
                    else
                    {
                        var rescued = RetryRescue.TryRescue(
                            bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.L1Exhausted);
                        if (rescued != null)
                        {
                            _userInteraction.NotifyError(
                                $"{jobName} - [[L1 기계 검증]] 최종 보완 실패. 가장 높은 점수를 받은 " +
                                $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                            currentPlanStructure = await AdoptPlanStructureForRescueAsync(
                                outputRoot, jobName, currentPlanStructure, bestAttemptStructure, cancellationToken);
                            finalAiResult = rescued.Generation ?? finalAiResult;
                            planReview = rescued.Review;
                            planOutcome = VerificationOutcome.QualityRejected;
                            consolidatedPlan = rescued.Markdown;
                            break;
                        }

                        _userInteraction.NotifyError($"{jobName} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                        planOutcome = VerificationOutcome.L1Exhausted;
                        consolidatedPlan = VerificationBanner.L1Exhausted(l1Result.Errors) + consolidatedPlan;
                        break;
                    }
                }

                // L2: AI 교차 리뷰
                ReviewResult? l2Result = null;
                bool reviewSuccess = false;
                string? reviewFailureReason = null;

                var criticEffortText = !string.IsNullOrWhiteSpace(_criticEffort) ? $", Effort: {_criticEffort}" : "";
                _userInteraction.NotifyStatus($"[yellow]{jobName}[/] - AI 통합 계획 교차 리뷰 분석 중 ({_criticService.ProviderName} - {_criticService.ModelName}{criticEffortText})...");
                try
                {
                    using (var progressScope = _userInteraction.CreateProgressScope("배치 계획 L2 리뷰") ?? NullProgressScope.Instance)
                    {
                        progressScope.AddTask("batchreview", $"{_criticService.ModelName} 통합 계획 리뷰 중...");
                        l2Result = await WrapWithProgress(_criticService.ReviewConsolidatedPlanAsync(specs, consolidatedPlan, jobName, _criticEffort, cancellationToken), progressScope, "batchreview");
                    }
                    reviewSuccess = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _userInteraction.NotifyError($"{jobName} - AI 교차 리뷰 실패 (시도 {attempt}): {ex.Message}");
                    reviewFailureReason = ex.Message;
                }

                // 불합격 여부와 무관하게 후보로 등록한다.
                // 반환값은 "이번 회차가 최고점을 갱신했는가"이며, 그것이 곧 정체 신호다.
                bool improvedThisAttempt = false;
                if (reviewSuccess && l2Result != null)
                {
                    improvedThisAttempt = bestAttempt.TryRecord(attempt, consolidatedPlan, l2Result, finalAiResult);
                    if (improvedThisAttempt)
                    {
                        // 후보가 교체되는 바로 그 자리에서 목차도 함께 붙잡는다.
                        // 다른 곳에서 갱신하면 둘이 어긋나는 순간이 생긴다.
                        bestAttemptStructure = currentPlanStructure;
                    }
                }

                if (reviewSuccess && l2Result != null && l2Result.HasDefects)
                {
                    _userInteraction.NotifyL2Defects(jobName, attempt, _maxAttempts, l2Result.FeedbackComment ?? string.Empty);

                    bool canRetry = _maxAttempts == -1 || attempt < _maxAttempts;
                    if (canRetry)
                    {
                        CriticFeedbackLog.Record(feedbackHistory, attempt, l2Result, _criticScoreThreshold);
                        feedbackLog = CriticFeedbackLog.Compose(
                            feedbackHistory,
                            "※ 지시사항: 위 지적사항을 모두 반영하여 본문을 수정하십시오. " +
                            "이전 라운드에서 이미 기준 점수를 통과한 항목의 서술 수준을 낮추지 마십시오. " +
                            "제공된 '원본 명세서(Specifications)'와 위 피드백을 절대적 기준으로 삼으십시오. " +
                            "특히 비즈니스 로직 누락이 지적된 경우, 원본 명세서의 해당 Step(프로시저) 내용을 다시 " +
                            "주의 깊게 정독하여 누락된 비즈니스 로직(UNION, 커서, JOIN, 필터 조건 등)을 완벽히 복원하십시오.");

                        // 재시도가 점수를 못 올리면 원인은 본문이 아니라 목차일 수 있다.
                        // 3/3만 반복해서는 구조가 원인인 결함이 영원히 고쳐지지 않는다.
                        if (redraftPolicy.TryConsume(improvedThisAttempt))
                        {
                            var redrafted = await DraftReplacementPlanStructureAsync(
                                "재시도가 점수를 개선하지 못해 목차를 다시 설계합니다",
                                currentPlanStructure, currentBrainstorming, feedbackLog,
                                targetLanguage, jobName, cancellationToken);

                            // 이 경로는 새 목차를 바로 다음 회차가 소비하므로 여기서 확정
                            // 기록한다. 기록에 실패하면 재수립을 없었던 일로 되돌려
                            // PlanStructure.md와 실제로 쓰이는 목차를 어긋나게 두지 않는다.
                            if (redrafted != null &&
                                await TryCommitPlanStructureAsync(
                                    "목차 재설계 결과", outputRoot, jobName, currentPlanStructure, redrafted, cancellationToken))
                            {
                                currentPlanStructure = redrafted;
                                // 목차가 바뀌면 단계 목록도 바뀐다. 낡은 골격·섹션을
                                // 재사용하면 새 목차가 없는 단계를 계속 실어 나른다.
                                ClearSplitGenerationCacheAfterRedraft(
                                    out lastSkeleton, out lastStepSections, out currentSteps,
                                    out stepFloorViolations, pendingDefectiveSteps);
                            }
                        }

                        // 어느 단계가 문제인지 Critic이 구조화 신호로 알려줬다면
                        // 골격과 통과한 단계를 재사용하고 그 단계만 다시 뽑는다.
                        // FeedbackComment 산문에서 코드를 파싱하지 않는다 —
                        // RegenerationScopeSelector가 그 방식의 실패를 이미 기록했다.
                        pendingDefectiveSteps.Clear();
                        if (currentSteps != null && l2Result.DefectiveSteps.Count > 0)
                        {
                            pendingDefectiveSteps.AddRange(
                                l2Result.DefectiveSteps.Where(code =>
                                    currentSteps.Any(step =>
                                        string.Equals(step.Code, code, StringComparison.OrdinalIgnoreCase))));
                        }

                        attempt++;
                        continue;
                    }
                    else
                    {
                        // 마지막이 아니라 최고점을 채택한다. 채택 규칙은 RetryRescue가
                        // 단독으로 소유한다. 정상 소진이므로 중단 사유는 null이다.
                        var rescued = RetryRescue.TryRescue(bestAttempt, _criticScoreThreshold, attempt, null);
                        var adoptedReview = rescued?.Review ?? l2Result;
                        var adoptedNumber = rescued?.AttemptNumber ?? attempt;

                        _userInteraction.NotifyError(
                            $"{jobName} - [[L2 AI 리뷰]] 최종 보완 실패. " +
                            $"가장 높은 점수를 받은 {adoptedNumber}차 시도({adoptedReview.NormalizedScore}/100)를 채택합니다.");

                        if (rescued != null)
                        {
                            currentPlanStructure = await AdoptPlanStructureForRescueAsync(
                                outputRoot, jobName, currentPlanStructure, bestAttemptStructure, cancellationToken);
                        }

                        finalAiResult = rescued?.Generation ?? finalAiResult;
                        planOutcome = VerificationOutcome.QualityRejected;
                        planReview = adoptedReview;
                        consolidatedPlan = rescued?.Markdown
                            ?? VerificationBanner.QualityRejected(adoptedReview, _criticScoreThreshold) + consolidatedPlan;
                        break;
                    }
                }

                // 리뷰를 수행하지 못한 경우: 소프트 페일로 계속 진행하되 통과와 구분해 표시한다.
                if (!reviewSuccess)
                {
                    var rescued = RetryRescue.TryRescue(
                        bestAttempt, _criticScoreThreshold, attempt, RetryAbortReason.ReviewFailed);
                    if (rescued != null)
                    {
                        _userInteraction.NotifyError(
                            $"{jobName} - [[L2 AI 리뷰]] 를 수행하지 못해 가장 높은 점수를 받은 " +
                            $"{rescued.AttemptNumber}차 시도({rescued.Review.NormalizedScore}/100)를 채택합니다.");

                        currentPlanStructure = await AdoptPlanStructureForRescueAsync(
                            outputRoot, jobName, currentPlanStructure, bestAttemptStructure, cancellationToken);
                        finalAiResult = rescued.Generation ?? finalAiResult;
                        planReview = rescued.Review;
                        planOutcome = VerificationOutcome.QualityRejected;
                        consolidatedPlan = rescued.Markdown;
                        break;
                    }

                    _userInteraction.NotifyError(
                        $"{jobName} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 계획서를 확정합니다.");
                    planOutcome = VerificationOutcome.ReviewNotRun;
                    consolidatedPlan =
                        VerificationBanner.ReviewNotRun(reviewFailureReason ?? "사유가 기록되지 않았습니다.") + consolidatedPlan;
                    break;
                }

                // 검증을 통과한 경우 루프 탈출
                if (l1Result.IsValid && (l2Result == null || !l2Result.HasDefects))
                {
                    if (l2Result != null && !string.IsNullOrWhiteSpace(l2Result.FeedbackComment))
                    {
                        var commentLines = l2Result.FeedbackComment.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in commentLines)
                        {
                            _userInteraction.NotifyStatus($"  [grey]* Critic 피드백: {EscapeMarkup(line)}[/]");
                        }
                    }
                    planReview = l2Result;
                    _userInteraction.NotifyValidationSuccess(jobName);
                    break;
                }
            }

            // L3: 인간 개입형 승인 (TUI 모드 전용, 배치 모드 시 즉시 승인 및 반환)
            if (isBatchMode)
            {
                _userInteraction.NotifyStatus($"[green]{jobName}[/] - 배치 모드로 인해 통합 계획서가 자동으로 최종 승인되었습니다.");
                return new ConsolidatedPipelineResult(consolidatedPlan, finalAiResult, planReview, planOutcome);
            }

            while (true)
            {
                // 이 경로에만 다시 세울 목차가 있으므로 구조 변경 질문을 여기서만 허용한다.
                var reviewResult = await _userInteraction.RequestHumanReviewAsync(
                    jobName, consolidatedPlan, planOutcome, structureRedraftSupported: true);

                if (reviewResult.Decision == UserDecision.Approve)
                {
                    return new ConsolidatedPipelineResult(consolidatedPlan, finalAiResult, planReview, planOutcome);
                }
                else if (reviewResult.Decision == UserDecision.Cancel)
                {
                    return new ConsolidatedPipelineResult(null, null, null, planOutcome);
                }
                else if (reviewResult.Decision == UserDecision.ProvideFeedback)
                {
                    if (string.IsNullOrWhiteSpace(reviewResult.UserFeedback))
                    {
                        continue;
                    }

                    _userInteraction.NotifyStatus($"[yellow]{jobName}[/] - 피드백 반영 재생성 중...");

                    // 사용자가 구조까지 바꾸라고 했다면 목차부터 다시 세운다.
                    // 목차를 고정한 채로는 3/3의 "STRICTLY adhering to the [Approved
                    // Document Structure & Plan]" 지시와 사용자 피드백이 충돌하고,
                    // STRICTLY가 붙은 쪽이 이겨 사용자 요구가 조용히 무시된다.
                    //
                    // 이 경로는 StructureRedraftPolicy를 거치지 않는다. 사용자의 명시적
                    // 지시를 자동화 예산으로 막지 않는다.
                    //
                    // 재수립 결과는 여기서 디스크에 쓰지 않는다. 아래 재생성이 실패하면
                    // 사용자에게는 직전 문서가 그대로 다시 보이는데, 그 시점에
                    // PlanStructure.md가 새 목차를 가리키면 화면의 문서를 만든 적 없는
                    // 목차가 기록으로 남는다. 새 목차로 본문이 실제로 나온 뒤에 쓴다.
                    string? pendingPlanStructure = null;
                    var structureForRegeneration = currentPlanStructure;
                    if (reviewResult.RedraftStructure)
                    {
                        pendingPlanStructure = await DraftReplacementPlanStructureAsync(
                            "사용자가 문서 구조 변경을 요청하여 목차를 다시 설계합니다",
                            currentPlanStructure, currentBrainstorming, reviewResult.UserFeedback,
                            targetLanguage, jobName, cancellationToken);
                        if (pendingPlanStructure != null)
                        {
                            structureForRegeneration = pendingPlanStructure;
                        }
                    }

                    var specsCopy = new System.Collections.Generic.List<(string FileName, string Content)>(specs);
                    specsCopy.Add(("User_Feedback_Log.txt", $"[L3 사용자 보완 피드백 로그]:\n{reviewResult.UserFeedback}\n사용자 의견을 수용하여 설계 내용을 수정 및 보완해 주십시오."));

                    string rePlan = string.Empty;
                    try
                    {
                        var aiResult = await _consolidatorService.GenerateConsolidatedBatchPlanAsync(structureForRegeneration, specsCopy, targetLanguage, jobName, _consolidatorEffort, cancellationToken);
                        rePlan = aiResult.Content;
                    }
                    // 명세서 경로와 같은 이유로 취소는 전파한다. 삼키면 아래 continue가 돌아
                    // 취소한 사용자에게 같은 승인 화면을 한 번 더 내민다.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _userInteraction.NotifyError($"피드백 반영 재생성 실패: {ex.Message}");
                    }

                    if (string.IsNullOrEmpty(rePlan))
                    {
                        // 재생성이 실패했으므로 새 목차는 아무 문서도 만들지 않았다.
                        // 기록하지 않은 채 되돌아가면 화면의 문서와 PlanStructure.md가
                        // 계속 같은 목차를 가리킨다.
                        continue;
                    }

                    if (pendingPlanStructure != null)
                    {
                        // 새 목차가 실제로 본문을 만들어 냈으니 이제 기록을 확정한다.
                        if (!await TryCommitPlanStructureAsync(
                                "목차 재설계 결과", outputRoot, jobName, currentPlanStructure, pendingPlanStructure, cancellationToken))
                        {
                            // 기록에 실패한 재수립은 없었던 일로 친다. 그 목차에서 나온
                            // 본문까지 함께 버려야 산출물과 기록이 어긋나지 않는다.
                            // 사용자에게는 직전 문서가 다시 보이고 다시 피드백할 수 있다.
                            continue;
                        }

                        currentPlanStructure = pendingPlanStructure;
                    }

                    // 피드백 반영본에 대한 L1 정적 검사 1회 수행
                    var l1Re = _validator.ValidateConsolidated(rePlan);
                    rePlan = l1Re.CleansedMarkdown ?? rePlan;
                    if (!l1Re.IsValid)
                    {
                        _userInteraction.NotifyStatus("피드백 적용본에서 정적 에러가 검출되어 AI 자가 수정 1회 더 진행합니다.");
                        try
                        {
                            var specsRe = new System.Collections.Generic.List<(string FileName, string Content)>(specsCopy);
                            specsRe.Add(("L1_Re_Fix.txt", l1Re.SuggestedPromptFix ?? string.Empty));
                            var aiResult = await _consolidatorService.GenerateConsolidatedBatchPlanAsync(structureForRegeneration, specsRe, targetLanguage, jobName, _consolidatorEffort, cancellationToken);
                            rePlan = aiResult.Content;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Warning(ex, "통합 계획서 L1 재보완 실패 (직전 버전 유지)");
                        }
                    }

                    // 이 계획서도 전체가 재생성되어 L1만 재검사할 뿐 L2는 재수행되지 않는다.
                    // 이전 판정과 점수를 그대로 들고 가면 재생성된, 한 번도 리뷰받지 않은
                    // 계획서가 이전 계획서의 통과 판정을 자칭하게 된다. 명세서 경로
                    // (:1451-1453)와 동일하게 리뷰를 비우고 미수행으로 명시한다.
                    consolidatedPlan = rePlan;
                    planReview = null;
                    planOutcome = VerificationOutcome.ReviewNotRun;
                }
            }
        }

        /// <summary>
        /// 새 목차를 만들어 내용만 돌려준다. 디스크에는 쓰지 않는다 — 기록(commit)을
        /// 분리한 이유는, 그 목차가 실제로 본문을 만들어 냈는지 아는 쪽이 호출부뿐이기
        /// 때문이다. 쓰지도 않은 목차를 PlanStructure.md에 먼저 올리면 재생성이 실패한
        /// 순간 파일이 어떤 산출물도 만든 적 없는 목차를 가리킨다.
        ///
        /// 실패하면 null을 돌려준다. 재수립은 개선 시도이지 필수 단계가 아니므로
        /// 여기서 파이프라인을 죽이지 않는다.
        ///
        /// reason은 재설계를 하게 된 경위다. L2 정체와 L3 사용자 요청은 원인이 전혀
        /// 다른데 한 문장을 공유하면 사용자에게 사실과 다른 안내가 나간다.
        /// </summary>
        private async Task<string?> DraftReplacementPlanStructureAsync(
            string reason,
            string currentStructure,
            string brainstorming,
            string? redraftFeedback,
            string targetLanguage,
            string jobName,
            CancellationToken cancellationToken)
        {
            _userInteraction.NotifyStatus($"[yellow]{jobName}[/] - {reason}...");

            string redrafted;
            try
            {
                using (var progressScope = _userInteraction.CreateProgressScope("목차 재설계") ?? NullProgressScope.Instance)
                {
                    // 3단계 중 하나가 아니므로 n/3. 순번을 붙이지 않는다.
                    progressScope.AddTask("redraft", "목차 재설계 중...");
                    var result = await WrapWithProgress(
                        _consolidatorService.DraftBatchPlanStructureAsync(
                            brainstorming, targetLanguage, jobName, _consolidatorEffort,
                            currentStructure, redraftFeedback, cancellationToken),
                        progressScope, "redraft");
                    redrafted = result.Content;
                }
            }
            // 취소는 실패가 아니라 사용자의 지시이므로 전파한다.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _userInteraction.NotifyError($"{jobName} - 목차 재설계 실패 (기존 목차 유지): {ex.Message}");
                return null;
            }

            // 빈 목차로 본문을 만들면 3/3이 아무 구조 없이 생성된다.
            if (string.IsNullOrWhiteSpace(redrafted))
            {
                _userInteraction.NotifyError($"{jobName} - 목차 재설계 응답이 비어 있어 기존 목차를 유지합니다.");
                return null;
            }

            return redrafted;
        }

        /// <summary>
        /// 분할 생성 1회분의 결과. 골격과 단계 섹션을 함께 들고 나오는 이유는
        /// 다음 회차의 지목 재생성이 그 둘을 재사용하기 때문이다.
        ///
        /// FloorViolations를 Code로 키를 잡는 이유: 지목 재생성이 건드리지 않은
        /// 단계의 위반 기록은 조립된 문서에 여전히 그 단계의 저질 본문이 실려
        /// 있는 한 함께 살아 있어야 한다. 목록이었다면 통째 교체 시 그 기록이
        /// 사라져 배너가 조용히 과소 보고했을 것이다.
        /// </summary>
        private sealed record SplitGeneration(
            string Markdown,
            AiResult Generation,
            string Skeleton,
            Dictionary<string, string> Sections,
            Dictionary<string, string> FloorViolations);

        /// <summary>
        /// 목차 재수립 직후 분할 생성 캐시를 통째로 무효화한다.
        ///
        /// 네 항목을 한곳에서 지우는 이유: 목차가 바뀌면 단계 코드도 바뀐다. 골격·섹션·
        /// 지목 목록·하한 위반 기록 중 하나라도 남겨두면 새 목차에 없는 옛 단계 코드를
        /// 계속 실어 나른다. stepFloorViolations를 빠뜨리면 GenerateBySplitAsync는 이번
        /// 회차에 다시 만드는 단계(pending)의 기록만 지우므로, 더 이상 존재하지 않는
        /// 옛 코드의 기록은 절대 지워지지 않고 영원히 남는다 — 실제로 그런 실수가 있었다.
        /// </summary>
        private static void ClearSplitGenerationCacheAfterRedraft(
            out string? lastSkeleton,
            out Dictionary<string, string>? lastStepSections,
            out IReadOnlyList<BatchStepPlan>? currentSteps,
            out Dictionary<string, string> stepFloorViolations,
            List<string> pendingDefectiveSteps)
        {
            lastSkeleton = null;
            lastStepSections = null;
            currentSteps = null;
            stepFloorViolations = new Dictionary<string, string>();
            pendingDefectiveSteps.Clear();
        }

        /// <summary>
        /// 골격 1회 + 단계 N회로 계획서를 만든다.
        ///
        /// 이 경로가 존재하는 이유: 단일 호출은 모델이 하나의 출력 예산 안에서
        /// 앞 단계에 66%를 쓰고 뒤를 굶겼다(실측). 단계마다 독립 호출이면 그
        /// 경쟁 자체가 사라진다.
        ///
        /// defectiveSteps가 비어 있지 않고 이전 골격·섹션이 남아 있으면 지목된
        /// 단계만 다시 뽑는다. 골격 호출은 하지 않는다.
        ///
        /// 골격을 얻지 못하면 null을 돌려주고 호출부가 단일 호출로 폴백한다.
        /// </summary>
        private async Task<SplitGeneration?> GenerateBySplitAsync(
            string planStructure,
            IReadOnlyList<BatchStepPlan> steps,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            IMultiProgressScope progressScope,
            string? previousSkeleton,
            Dictionary<string, string>? previousSections,
            Dictionary<string, string> previousViolations,
            IReadOnlyList<string> defectiveSteps,
            CancellationToken cancellationToken)
        {
            var targeted = previousSkeleton != null && previousSections != null && defectiveSteps.Count > 0;

            string skeleton;
            AiResult generation;

            if (targeted)
            {
                skeleton = previousSkeleton!;
                generation = new AiResult { Content = skeleton };
                // 골격 호출을 건너뛰므로 WrapWithProgress가 이 태스크를 완료 처리할
                // 기회가 없다. 여기서 직접 완료하지 않으면 화면에 미완료 행이 남는다.
                progressScope.CompleteTask("phase3");
            }
            else
            {
                try
                {
                    var skeletonResult = await WrapWithProgress(
                        _consolidatorService.GenerateBatchPlanSkeletonAsync(
                            steps, planStructure, specs, targetLanguage, jobName, _consolidatorEffort, cancellationToken),
                        progressScope, "phase3");

                    if (skeletonResult == null || string.IsNullOrWhiteSpace(skeletonResult.Content))
                    {
                        return null;
                    }

                    skeleton = skeletonResult.Content;
                    generation = skeletonResult;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _userInteraction.NotifyError($"{jobName} - 배치 계획 골격 생성 실패: {ex.Message}");
                    return null;
                }
            }

            var conventions = BatchPlanAssembler.ExtractSharedConventions(skeleton);
            var sections = previousSections != null
                ? new Dictionary<string, string>(previousSections)
                : new Dictionary<string, string>();
            // 이전 회차의 위반 기록을 그대로 이어받는다. 지금 회차에서 다시 만들
            // 단계의 기록은 새로 만들기 직전에 지운다 — 통과하면 조용히 사라지고,
            // 다시 미달하면 아래에서 새로 채워진다. 손대지 않는 단계의 기록은
            // 절대 건드리지 않는다.
            var floorViolations = new Dictionary<string, string>(previousViolations);

            // 지목 재생성이면 지목된 단계만, 아니면 전부 만든다.
            // 지목 코드가 목록에 없으면(모델이 지어낸 코드) 무시한다.
            var pending = targeted
                ? steps.Where(step => defectiveSteps.Contains(step.Code, StringComparer.OrdinalIgnoreCase)).ToList()
                : steps.ToList();

            foreach (var step in pending)
            {
                floorViolations.Remove(step.Code);
            }

            for (int index = 0; index < pending.Count; index++)
            {
                var step = pending[index];
                var taskKey = $"step_{step.Code}";
                progressScope.AddTask(taskKey, $"3/3. 최종 생성 중 ({step.Code} · {index + 1}/{pending.Count})...");

                sections[step.Code] = await GenerateStepSectionWithFloorRetryAsync(
                    step, steps, conventions, specs, targetLanguage, jobName, floorViolations, cancellationToken);

                progressScope.CompleteTask(taskKey);
            }

            // 목록 순서대로 조립한다. 사전의 삽입 순서가 아니라 목차의 순서가 기준이다.
            var ordered = steps
                .Select(step => sections.TryGetValue(step.Code, out var markdown) ? markdown : string.Empty)
                .Where(markdown => !string.IsNullOrWhiteSpace(markdown))
                .ToList();

            return new SplitGeneration(
                BatchPlanAssembler.Assemble(skeleton, ordered),
                generation,
                skeleton,
                sections,
                floorViolations);
        }

        /// <summary>
        /// 단계 섹션 하나를 만들고 하한을 검사한다. 미달이면 그 단계만 1회 재시도한다.
        ///
        /// 이 재시도는 MaxL2Attempts를 소모하지 않는다. 그 예산은 Actor-Critic 문서
        /// 레벨의 것이고, 이 보수는 리뷰 호출이 0인 국소 작업이라 성격이 다르다.
        /// 대신 단계당 1회로 하드 캡해 폭주를 막는다.
        ///
        /// 재시도 후에도 미달이면 채택하고 기록만 한다. 여기서 문서 L1을 실패시키면
        /// 같은 결함으로 골격+단계 전체 재생성을 유발해 비용만 태운다.
        /// </summary>
        private async Task<string> GenerateStepSectionWithFloorRetryAsync(
            BatchStepPlan step,
            IReadOnlyList<BatchStepPlan> steps,
            string conventions,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            Dictionary<string, string> floorViolations,
            CancellationToken cancellationToken)
        {
            const int maxTries = 2;   // 최초 1회 + 재시도 1회
            string? adopted = null;
            string? floorFeedback = null;

            for (int tries = 0; tries < maxTries; tries++)
            {
                string? content = null;
                try
                {
                    var result = await _consolidatorService.GenerateBatchStepSectionAsync(
                        step, steps, conventions, specs, targetLanguage, jobName,
                        _consolidatorEffort, floorFeedback, cancellationToken);
                    content = result?.Content;
                }
                // 취소를 삼키면 실패로 위장한 정상 반환이 되어 취소 사실이 사라진다.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _userInteraction.NotifyError($"{jobName} - {step.Code} 단계 섹션 생성 실패: {ex.Message}");
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    floorFeedback = null;
                    continue;
                }

                adopted = content;

                var stepResult = _validator.ValidateBatchStep(content, step);
                if (stepResult.IsValid)
                {
                    return content;
                }

                _userInteraction.NotifyStatus(
                    $"  [grey]* {step.Code} 단계가 하한 검사를 통과하지 못해 다시 생성합니다: {string.Join(" / ", stepResult.Errors)}[/]");
                floorFeedback = stepResult.SuggestedPromptFix;
            }

            if (adopted == null)
            {
                floorViolations[step.Code] = $"{step.Code} (생성 실패)";
                return $"### {step.Code} {step.Name}\n\n> [!WARNING]\n> 이 단계는 생성에 실패했습니다. 원본 프로시저를 직접 확인하십시오.\n";
            }

            floorViolations[step.Code] = $"{step.Code} (하한 미달)";
            return adopted;
        }

        /// <summary>
        /// 목차 교체를 기록으로 확정한다. 성공하면 true.
        ///
        /// 기록에 실패하면 false를 돌려주고 호출부는 재수립을 없었던 일로 되돌린다.
        /// superseded 파일을 먼저 쓰는 순서이므로, 어느 쓰기가 실패하든 PlanStructure.md는
        /// 여전히 이전 목차를 가리킨다 — 되돌리기만 하면 파일과 실제 목차가 일치한다.
        /// 기록 실패로 파이프라인을 죽이지는 않는다.
        /// </summary>
        private async Task<bool> TryCommitPlanStructureAsync(
            string operationLabel,
            string outputRoot,
            string jobName,
            string supersededStructure,
            string finalStructure,
            CancellationToken cancellationToken)
        {
            try
            {
                await WritePlanStructureFilesAsync(
                    outputRoot, jobName, supersededStructure, finalStructure, cancellationToken);
                return true;
            }
            // 취소는 실패가 아니라 사용자의 지시이므로 전파한다.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 재설계 기록과 구제 채택 되돌리기는 경위가 다르므로 문구를 공유하지 않는다.
                _userInteraction.NotifyError(
                    $"{jobName} - {operationLabel} 기록 실패 (변경 폐기, 기존 목차 유지): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 구제(RetryRescue)로 이전 회차 문서를 채택할 때, 그 문서를 만든 목차를 다시
        /// 현행으로 되돌린다. 재수립 이후 회차가 더 낮은 점수를 내면 산출물은 재수립
        /// 이전 목차에서 나온 것인데 PlanStructure.md에는 새 목차가 남아 있어, 파일이
        /// 어떤 산출물도 만든 적 없는 목차를 가리키게 된다.
        ///
        /// 되돌린 뒤 실제로 쓰이는 목차를 돌려준다. 이어지는 L3 재생성도 이 목차를
        /// 써야 화면의 문서와 기록이 계속 일치한다.
        /// </summary>
        private async Task<string> AdoptPlanStructureForRescueAsync(
            string outputRoot,
            string jobName,
            string currentStructure,
            string adoptedStructure,
            CancellationToken cancellationToken)
        {
            // 재수립이 없었거나 채택본이 현행 목차에서 나왔으면 되돌릴 것이 없다.
            if (string.IsNullOrEmpty(adoptedStructure) || adoptedStructure == currentStructure)
            {
                return currentStructure;
            }

            // 버려지는 목차도 superseded로 남긴다. 어떤 목차가 시도됐고 왜 채택되지
            // 않았는지가 raw/ 디렉터리만 보고 재구성되어야 한다.
            return await TryCommitPlanStructureAsync(
                "채택된 시도의 목차", outputRoot, jobName, currentStructure, adoptedStructure, cancellationToken)
                ? adoptedStructure
                : currentStructure;
        }

        /// <summary>
        /// 교체되는 직전 목차를 superseded 파일로 남기고 PlanStructure.md를 최종본으로
        /// 갱신한다. PlanStructure.md는 항상 본문을 실제로 만든 목차를 가리켜야 하므로
        /// 이전 목차를 그 자리에 남기지 않는다.
        /// </summary>
        private static async Task WritePlanStructureFilesAsync(
            string outputRoot,
            string jobName,
            string previousStructure,
            string redrafted,
            CancellationToken cancellationToken)
        {
            var rawDir = System.IO.Path.Combine(outputRoot, "Jobs", jobName, "raw");
            if (!System.IO.Directory.Exists(rawDir))
            {
                System.IO.Directory.CreateDirectory(rawDir);
            }

            // L2 정체로 1회, L3 사용자 요청으로 n회가 가능하므로 번호를 이어 붙인다.
            var index = 1;
            while (System.IO.File.Exists(System.IO.Path.Combine(rawDir, $"PlanStructure.superseded-{index}.md")))
            {
                index++;
            }

            await System.IO.File.WriteAllTextAsync(
                System.IO.Path.Combine(rawDir, $"PlanStructure.superseded-{index}.md"), previousStructure, cancellationToken);
            await System.IO.File.WriteAllTextAsync(
                System.IO.Path.Combine(rawDir, "PlanStructure.md"), redrafted, cancellationToken);
        }

        private static string ResolveCleansingFileBaseName(CodeObjectKey key, string? analysisDatabase)
        {
            var localName = $"{key.Schema}.{key.Name}";
            if (string.IsNullOrWhiteSpace(analysisDatabase) ||
                string.IsNullOrWhiteSpace(key.Database) ||
                string.Equals(
                    key.Database.Trim(),
                    analysisDatabase.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return localName;
            }

            return $"{key.Database.Trim()}.{localName}";
        }

        private void ExportMetadataCleansingSql(
            string specificationMarkdown,
            string selectedOption,
            string cleansingFileBaseName,
            string outputDirectory)
        {
            if (string.IsNullOrEmpty(specificationMarkdown)) return;

            // 정규식을 사용하여 [AI 추론 보완: Schema.Table.Column - 설명] 패턴 추출
            var regex = new System.Text.RegularExpressions.Regex(@"\[AI 추론 보완:\s*([a-zA-Z0-9_]+)\.([a-zA-Z0-9_]+)\.([a-zA-Z0-9_]+)\s*-\s*([^\]]+)\]");
            var matches = regex.Matches(specificationMarkdown);

            Log.Debug("[파이프라인] 메타데이터 보완 SQL 패턴 탐지 - SP: {SpName}, 탐지된 패턴 수: {MatchCount}", selectedOption, matches.Count);
            if (matches.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("-- ==========================================================================");
            sb.AppendLine($"-- AI Generated Metadata Cleansing Script for {selectedOption}");
            sb.AppendLine($"-- Created At: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("-- ==========================================================================");
            sb.AppendLine();

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var schema = match.Groups[1].Value;
                var table = match.Groups[2].Value;
                var column = match.Groups[3].Value;
                var value = match.Groups[4].Value.Trim();

                sb.AppendLine($"-- Column: {schema}.{table}.{column}");
                sb.AppendLine($"IF NOT EXISTS (");
                sb.AppendLine($"    SELECT 1 FROM sys.extended_properties ep");
                sb.AppendLine($"    INNER JOIN sys.columns c ON ep.major_id = c.object_id AND ep.minor_id = c.column_id");
                sb.AppendLine($"    INNER JOIN sys.objects o ON c.object_id = o.object_id");
                sb.AppendLine($"    INNER JOIN sys.schemas s ON o.schema_id = s.schema_id");
                sb.AppendLine($"    WHERE s.name = '{schema}' AND o.name = '{table}' AND c.name = '{column}' AND ep.name = 'MS_Description'");
                sb.AppendLine($")");
                sb.AppendLine($"BEGIN");
                sb.AppendLine($"    EXEC sp_addextendedproperty ");
                sb.AppendLine($"         @name = N'MS_Description', @value = N'{value.Replace("'", "''")}',");
                sb.AppendLine($"         @level0type = N'SCHEMA', @level0name = '{schema}',");
                sb.AppendLine($"         @level1type = N'TABLE',  @level1name = '{table}',");
                sb.AppendLine($"         @level2type = N'COLUMN', @level2name = '{column}';");
                sb.AppendLine($"END");
                sb.AppendLine($"ELSE");
                sb.AppendLine($"BEGIN");
                sb.AppendLine($"    EXEC sp_updateextendedproperty ");
                sb.AppendLine($"         @name = N'MS_Description', @value = N'{value.Replace("'", "''")}',");
                sb.AppendLine($"         @level0type = N'SCHEMA', @level0name = '{schema}',");
                sb.AppendLine($"         @level1type = N'TABLE',  @level1name = '{table}',");
                sb.AppendLine($"         @level2type = N'COLUMN', @level2name = '{column}';");
                sb.AppendLine($"END");
                sb.AppendLine($"GO");
                sb.AppendLine();
            }

            try
            {
                var cleansingDir = System.IO.Path.Combine(outputDirectory, "cleansing");
                if (!System.IO.Directory.Exists(cleansingDir))
                {
                    System.IO.Directory.CreateDirectory(cleansingDir);
                }

                var sqlPath = System.IO.Path.Combine(cleansingDir, $"{cleansingFileBaseName}_MetadataCleansing.sql");
                System.IO.File.WriteAllText(sqlPath, sb.ToString(), System.Text.Encoding.UTF8);
                Log.Debug("[파이프라인] 메타데이터 보완 SQL 스크립트 저장 성공 - SP: {SpName}, 경로: {SqlPath}", selectedOption, sqlPath);
                _userInteraction.NotifyStatus($"[green]{selectedOption}[/] - 메타데이터 보완 SQL 스크립트가 저장되었습니다: [blue]{sqlPath}[/]");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[파이프라인] 메타데이터 보완 스크립트 저장 실패 - SP: {SpName}", selectedOption);
                _userInteraction.NotifyError($"메타데이터 보완 스크립트 저장 중 오류 발생: {ex.Message}");
            }
        }

        private async Task ApplyMetadataCleansingSqlAsync(
            string connectionString,
            CodeObjectKey key,
            string selectedOption,
            string cleansingFileBaseName,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            var sqlPath = System.IO.Path.Combine(outputDirectory, "cleansing", $"{cleansingFileBaseName}_MetadataCleansing.sql");
            if (!System.IO.File.Exists(sqlPath)) return;

            // 정제 SQL은 DB 한정자가 없으므로 접속 DB(Initial Catalog)에 그대로 적용된다.
            // 객체가 다른 DB 소속이면 루트 DB의 동명 객체에 잘못 기록될 수 있어 적용 자체를 건너뛴다.
            var currentDatabase = ResolveCurrentDatabase(connectionString);
            if (!string.IsNullOrWhiteSpace(key.Database) &&
                !string.IsNullOrWhiteSpace(currentDatabase) &&
                !string.Equals(
                    key.Database.Trim(),
                    currentDatabase.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning(
                    "[파이프라인] 객체 소속 DB와 접속 DB가 달라 메타데이터 역반영을 건너뜁니다 - 객체: {ObjectKey}, 객체 DB: {ObjectDatabase}, 접속 DB: {ConnectionDatabase}, SqlPath: {SqlPath}",
                    key.CanonicalName,
                    key.Database,
                    currentDatabase,
                    sqlPath);
                return;
            }

            Log.Information("[파이프라인] DB 메타데이터 역반영 SQL 실행 시작 - SP: {SpName}, SqlPath: {SqlPath}", selectedOption, sqlPath);

            try
            {
                var sqlText = await System.IO.File.ReadAllTextAsync(sqlPath, cancellationToken);
                if (string.IsNullOrWhiteSpace(sqlText)) return;

                _userInteraction.NotifyStatus($"[yellow]{selectedOption}[/] - DB 메타데이터 설명 역반영 중...");

                var batches = sqlText.Split(new[] { "GO\r\n", "GO\n", "go\r\n", "go\n" }, StringSplitOptions.RemoveEmptyEntries);
                Log.Debug("[파이프라인] 실행할 SQL 배치 수: {BatchCount} - SP: {SpName}", batches.Length, selectedOption);

                using (var conn = new SqlConnection(connectionString))
                {
                    await conn.OpenAsync(cancellationToken);
                    foreach (var batch in batches)
                    {
                        var cleanBatch = batch.Trim();
                        if (string.IsNullOrEmpty(cleanBatch)) continue;

                        using (var cmd = new SqlCommand(cleanBatch, conn))
                        {
                            await cmd.ExecuteNonQueryAsync(cancellationToken);
                        }
                    }
                }
                Log.Information("[파이프라인] DB 메타데이터 역반영 완료 - SP: {SpName}", selectedOption);
                _userInteraction.NotifyStatus($"[green]{selectedOption}[/] - DB 메타데이터 설명 역반영 완료!");
            }
            // 취소를 삼키면 DB 역반영이 중단됐다는 사실이 감춰지고 호출부는
            // 정상 완료(Result)로만 본다.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "[파이프라인] DB 메타데이터 역반영 중 예외 발생 - SP: {SpName}", selectedOption);
                _userInteraction.NotifyError($"DB 메타데이터 설명 역반영 중 오류 발생: {ex.Message}");
            }
        }

        private async Task<T> WrapWithProgress<T>(Task<T> underlyingTask, IMultiProgressScope scope, string taskKey)
        {
            scope.UpdateTask(taskKey, 10);
            try
            {
                var result = await underlyingTask;
                scope.CompleteTask(taskKey);
                return result;
            }
            catch
            {
                scope.FailTask(taskKey);
                throw;
            }
        }



        private DeconstructedSpLogic ParseDeconstructedLogic(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return new DeconstructedSpLogic();

            string json = content.Trim();

            // AI 응답에 서론/결론 등 불필요한 텍스트가 포함되어 있을 수 있으므로 JSON 블록 추출
            int firstBrace = json.IndexOf('{');
            int lastBrace = json.LastIndexOf('}');

            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                json = json.Substring(firstBrace, lastBrace - firstBrace + 1).Trim();
            }

            try
            {
                var options = new System.Text.Json.JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                };
                return System.Text.Json.JsonSerializer.Deserialize<DeconstructedSpLogic>(json, options) ?? new DeconstructedSpLogic();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Deconstructed JSON 역직렬화 실패. 원본 내용: {Content}", content);
                return new DeconstructedSpLogic();
            }
        }

        private string EscapeMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("[", "[[").Replace("]", "]]");
        }
    }
}
