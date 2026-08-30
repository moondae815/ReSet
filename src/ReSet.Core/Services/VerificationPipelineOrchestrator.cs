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
        private readonly int _stepConcurrency;

        /// <summary>
        /// L1 위반 수리 시도의 상한. 채점 예산(_maxAttempts)과 분리돼 있다.
        ///
        /// 나눈 이유: 실측(POQSettleBatch4 2026-08-29)에서 6회 중 2회가 L1에서 소진되어
        /// 채점조차 받지 못했다. L1 위반은 결정적 결함이라 자리를 특정할 수 있고, 그 자리만
        /// 고치면 되는데도 채점 회차를 통째로 먹었다.
        ///
        /// RunCodeObjectPipelineAsync는 아직 이 예산을 쓰지 않는다 - _maxAttempts가
        /// 그 경로의 L1·L2를 여전히 함께 센다. 이 필드는 RunConsolidatedPipelineAsync의
        /// L1 분기에만 배선된다.
        /// </summary>
        private readonly int _maxL1RepairAttempts;

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
            int criticScoreThreshold = 8,
            int stepConcurrency = 1,     // 기본값 1 = 종전 순차. 실사용 값은 appsettings.json이 4로 넘긴다.
            int maxL1RepairAttempts = 2) // L1 위반 수리 전용 예산. 채점 예산과 분리한다.
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
            // 0·음수는 1로 절상한다. 상한은 두지 않는다 — 사용자가 12를 원하면 12를 쓴다.
            _stepConcurrency = Math.Max(1, stepConcurrency);
            _maxL1RepairAttempts = Math.Max(1, maxL1RepairAttempts);

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

            // [RunCodeObjectPipelineAsync] 이 예산은 아직 L1 실패와 L2 실패가 공유한다.
            // 설정 이름(MaxL2Attempts)과 달리 L2 전용이 아니다 — L1에서 소진되면 채점된
            // 후보 수가 설정값보다 적어진다. 2026-08-05 실행에서 3회 예산 중 1회를 L1
            // 실패가 가져가 채점된 시도가 2회뿐이었다. 예산을 나누지 않기로 한 이유는
            // RetryRescue가 최고점 후보를 구제하므로 남는 손해가 "좋은 문서 상실"이 아니라
            // "개선 기회 1회 상실"이기 때문이다.
            //
            // [RunConsolidatedPipelineAsync] 이 경로는 위와 다르다 - L1 실패는
            // _maxL1RepairAttempts(자기 예산)를 쓰고 이 _maxAttempts는 건드리지 않는다.
            // 실측(POQSettleBatch4 2026-08-29)에서 6회 중 2회가 L1에서 소진돼 채점조차
            // 못 받았기 때문이다.
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

            // 정적 분석이 확정한 UPDATE 매핑을 L1 기계 검증의 기대값으로 접어 둔다.
            // 이 SP/함수에 UPDATE 매핑이 없으면(비 Procedure거나 정적 분석 실패 등)
            // null이 되고, Validate는 그 경우 종전과 동일하게 UPDATE 매핑 대조를
            // 건너뛴다 - 아래 6개 호출부 전부에 넘겨도 회귀가 없는 이유다.
            // spDef는 위 null 검사를 통과했으므로 null 조건부 없이 그대로 넘긴다 -
            // `spDef?.`를 쓰면 컴파일러의 널 흐름 추적이 재설정되어 아래
            // ResolveCacheObjectKey(spDef, key) 호출에 CS8604 경고가 새로 생긴다.
            var specExpectations = SpecExpectations.From(spDef);

            // A 위반(프롬프트가 진실을 담지 못함)은 재생성으로 고칠 수 없는 코드 버그다.
            // L1 오류로 만들면 무한 재시도가 된다. 아래 NotifyWarnings가 이미 사용자에게
            // 보여 주는 채널이므로 새 채널을 만들지 않는다.
            if (specExpectations != null && specExpectations.InputDefects.Count > 0)
            {
                foreach (var defect in specExpectations.InputDefects)
                {
                    Log.Warning("[파이프라인] {Defect}", defect);
                    if (!spDef.Warnings.Contains(defect))
                    {
                        spDef.Warnings.Add(defect);
                    }
                }
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
                        var candidate = candidates[i];   // 클로저가 루프 변수를 붙잡지 않게 지역으로 고정한다
                        reviewTasks.Add(WrapWithProgress(
                            AiCallRetry.ExecuteAsync(
                                () => _criticService.ReviewSpecificationAsync(spDef, candidate, _criticEffort, cancellationToken),
                                cancellationToken),
                            progressScope, taskKey));
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
                    var l1Check = _validator.Validate(candidates[i], specExpectations);
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
                    var finalL1 = _validator.Validate(specificationMarkdown, specExpectations);
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
                            var postFixL1 = _validator.Validate(consolidatorSelfFixResult.Content, specExpectations);
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
                            finalL2Result = await WrapWithProgress(
                                AiCallRetry.ExecuteAsync(
                                    () => _criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken),
                                    cancellationToken),
                                progressScope, "final_review");
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
                            var fixL1Result = _validator.Validate(finalConsolidatedFixResult.Content, specExpectations);
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
                                        var reFinalReview = await WrapWithProgress(
                                            AiCallRetry.ExecuteAsync(
                                                () => _criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken),
                                                cancellationToken),
                                            progressScope, "refinal");
                                        if (reFinalReview != null)
                                        {
                                            finalReview = reFinalReview;
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    // 재검토 실패는 이전 finalReview를 유지하므로 치명적이지 않다.
                                    // 다만 조용히 삼키면 재시도까지 다 쓰고 실패한 사실이 어디에도 남지 않는다.
                                    Log.Warning("[파이프라인] 보완본 L2 재검토 실패 - 이전 최종 리뷰를 유지합니다: {Reason}", ex.Message);
                                }
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
                    var l1Result = _validator.Validate(specificationMarkdown, specExpectations);
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
                            l2Result = await WrapWithProgress(
                                AiCallRetry.ExecuteAsync(
                                    () => _criticService.ReviewSpecificationAsync(spDef, specificationMarkdown, _criticEffort, cancellationToken),
                                    cancellationToken),
                                progressScope, "review");
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
                        // 감쇄 임계치(Decay)를 쓰지 않고 항상 설정된 기준 점수를 강제한다.
                        // Critic의 HasDefects 자기 신고는 참고일 뿐이고 게이트는 코드가 잡는다.
                        EnforceScoreThreshold(l2Result, selectedOption, attempt);
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
                        var l1Re = _validator.Validate(reSpec, specExpectations);
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
            IReadOnlyList<SpDefinition>? definitions = null,
            CancellationToken cancellationToken = default)
        {
            // 호출부 결함이므로 CWD로 조용히 폴백하지 않고 즉시 드러낸다.
            if (string.IsNullOrWhiteSpace(outputRoot))
            {
                throw new ArgumentException("출력 디렉터리가 필요합니다.", nameof(outputRoot));
            }

            // 미지 테이블 검사의 재료. definitions가 없으면 빈 집합이 되고,
            // 검증기는 그때 검사를 건너뛴다(소프트 스킵). 조립 근거는
            // StepInterfaceFacts.CollectSchemaCatalog에 있다 - 의존 대상뿐 아니라
            // 이 Job이 대체하는 원본 프로시저 자신도 카탈로그다. 이 값은 검증
            // 경로에만 흐르고 AI 프롬프트로는 가지 않으므로 캐시 계약과 무관하다.
            var knownTableNames = StepInterfaceFacts.CollectSchemaCatalog(definitions);

            if (knownTableNames.Count == 0)
            {
                Log.Information(
                    "스키마 카탈로그가 비어 있어 단계별 미지 테이블 검사를 실행하지 않습니다 - JobName: {JobName}",
                    jobName);
            }

            // 원본 인터페이스 재료. knownTableNames와 같은 자리에서 만드는 이유는
            // 둘 다 definitions에서 오고 같은 경로로 단계 검증까지 흘러가기 때문이다.
            // 단계 목록은 여기서 아직 없으므로 프로시저별 사전까지만 만들고,
            // 단계별 조립은 steps를 가진 GenerateBySplitAsync가 한다.
            var parametersByProcedure = StepInterfaceFacts.CollectParameters(definitions);

            // 단일 호출 경로(폴백·L3 재생성)도 원본 인터페이스 표를 받아야 한다 -
            // 규칙 5가 그 표를 가리키며 "여기 적힌 파라미터가 전부"라고 말하기 때문이다.
            // 목차를 못 읽으면 빈 목록을 주고, 그때는 AiService가 절 자체를 싣지 않는다
            // (빈 표는 "원본 파라미터가 없다"로 읽혀 없는 근거가 된다).
            List<StepInterface> InterfacesFor(string? structure)
            {
                var parsed = BatchStepPlanParser.TryParse(structure ?? string.Empty);
                return parsed == null
                    ? new List<StepInterface>()
                    : StepInterfaceFacts.Build(parsed, parametersByProcedure).ToList();
            }

            // 이 경고는 분할 생성 진입 여부와 무관하게 실행당 한 번 뜬다. 목차 JSON
            // 파싱에 실패해 단일 호출로 폴백하는 회차에서도 뜨지만, 설정이 로컬
            // 공급자와 함께 쓰이고 있다는 사실 자체는 여전히 참이고 조치도 같다.
            //
            // 로컬 모델은 보통 단일 GPU를 공유하므로 동시 실행이 순차보다 느리거나
            // 메모리가 터진다. 값을 조용히 1로 뒤집지 않는 이유: 사용자가 명시한
            // 설정을 말없이 무시하는 것보다 이유를 말하고 그대로 두는 편이 정직하고,
            // 증상이 "그냥 느림"이라 경고가 없으면 원인을 찾을 길이 없다.
            //
            // provider 매개변수(Actor)가 아니라 Consolidator를 보는 이유: 단계 본문을
            // 실제로 만드는 것은 _consolidatorService다.
            if (_stepConcurrency > 1 &&
                ReSet.Core.Services.Clients.AiClientFactory.IsSingleGpuLocalProvider(_consolidatorService.ProviderName))
            {
                _userInteraction.NotifyStatus(
                    $"[yellow]{jobName}[/] - StepConcurrency={_stepConcurrency}이지만 Consolidator가 로컬 공급자({_consolidatorService.ProviderName})입니다. " +
                    "단일 GPU에서는 동시 실행이 순차보다 느리거나 메모리가 부족할 수 있습니다 — appsettings.json의 AiSettings:StepConcurrency를 1로 낮추는 것을 권장합니다.");
            }

            string? feedbackLog = null;
            var feedbackHistory = new System.Collections.Generic.List<string>();
            string consolidatedPlan = string.Empty;
            // consolidatedPlan과 별도로 배너 없는 원본 본문만 담는다. L1Exhausted/
            // QualityRejected/ReviewNotRun 배너는 실패 사유(Critic 피드백 등)를 그대로
            // 인용하므로, 그 문구에 우연히 오류코드 숫자가 섞이면 본문을 훑어야 할
            // AttachPipelineBanners의 오류코드 누락 검사가 배너 텍스트에서 "존재"를
            // 오판한다 - QualityRejected 배너가 Critic 코멘트를 그대로 옮기는 것이
            // 실제로 이 사고를 낸 자리다. 루프의 각 종료 지점에서 배너를 붙이기
            // 직전의 값을 여기에 담아 둔다.
            string documentBodyForChecks = string.Empty;
            AiResult? finalAiResult = null;
            string currentPlanStructure = string.Empty;
            // 재수립 시점에 필요한데 기존에는 if 블록 안에서만 살아 있었다.
            // 목차가 있으면 브레인스토밍도 반드시 있다(둘은 한 몸으로만 실행된다).
            string currentBrainstorming = string.Empty;
            // 최고점 후보(BestAttempt.Current)를 실제로 만들어 낸 상태 일체.
            // 목차·골격·골격 AiResult·단계 섹션·하한 위반이 한 몸으로 움직인다.
            //
            // 목차가 어긋나면 산출된 문서를 한 번도 만든 적 없는 목차가 기록으로
            // 남고, 하한 위반이 어긋나면 배너가 과다·과소 보고하며, 골격과 섹션이
            // 어긋나면 L3 지목 재생성이 화면의 문서가 아닌 폐기된 회차 위에 얹힌다.
            // 셋 다 실제로 발생했던 결함이라 개별 변수로 두지 않는다.
            var adoptedState = new AdoptedGenerationState(
                string.Empty, null, null, null, new Dictionary<string, StepDefect>());
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
            // 골격 호출 자체의 AiResult. lastSkeleton과 한 몸으로 움직여야 한다 —
            // 지목 재생성은 골격 호출을 건너뛰므로, 이 값이 없으면 그 회차의
            // finalAiResult가 SystemPrompt/UserPrompt/ThinkingText 없는 빈 스텁이
            // 되어 raw/prompt-context.md와 docs/Thinking.md가 텅 빈 채 나간다.
            AiResult? lastSkeletonResult = null;
            Dictionary<string, string>? lastStepSections = null;
            // Code -> StepDefect(Kind, "{Code} (사유)" 형식의 표시 문자열). Kind가
            // QualityFloor/Unverifiable을 가른다. 사전으로 두는 이유는 지목
            // 재생성이 건드리지 않은 단계의 항목을 그대로 보존해야 하기 때문이다
            // — 목록을 통째로 교체하면 재생성되지 않은 단계의 판정 기록이
            // 조용히 사라진다.
            var stepFloorViolations = new Dictionary<string, StepDefect>();
            var pendingDefectiveSteps = new List<string>();
            // 어느 단계도 선언하지 않은 원본 오류 코드의 누락 - 본문이 아니라 목차의
            // 결함이다(설계서 §3-5(b)). 회차를 넘어 살아 있어야 다음 회차의 재설계
            // 조건(Task 8)이 이번 회차에 기계가 찾은 사실을 읽을 수 있다.
            var machineFoundStructureDefect = false;

            // 목차가 낼 ErrorCodes는 하한 검사의 유일한 대조 기준인데, 실측 두 회차에서
            // 26개 단계 중 25개가 빈 배열이었다. 명세서에서 뽑아 채운다. 명세서는 루프
            // 안에서 바뀌지 않으므로 한 번만 뽑는다.
            //
            // specsCopy가 아니라 specs를 넘긴다 - specsCopy에는 Feedback_Log.txt가
            // 붙는데 그것은 명세서가 아니다.
            var specReturnCodes = SpecReturnCodeExtractor.Extract(specs);

            // 목차 단계는 명세서를 받지 않으므로 이름을 알 방법이 이 명단뿐이다.
            // 반드시 원본 specs를 쓴다 - specsCopy는 재시도 회차마다 Feedback_Log.txt가
            // 덧붙어, 존재하지 않는 프로시저가 명단에 섞인다.
            var sourceProcedureRoster = specs.Select(s => s.FileName).ToList();

            // 목차의 TargetTables도 같은 문제를 갖는다 - 같은 12개 SP를 두 제공자로
            // 돌린 실측에서 7개와 17개가 나왔고, 두 회차 모두 같은 단계를 빈 배열로
            // 냈다. 오류코드와 달리 명세서 산문이 아니라 정적 분석에서 뽑는다.
            // definitions가 null이면 빈 사전이라 보강이 일어나지 않는다.
            var specTargetTables = SpecTargetTableExtractor.Extract(definitions);

            // 조각을 호출부로 내보낸다. 산출물 분할이 이 값들을 경계 앵커로 쓴다.
            // splitMarkdown이 null이면 단일 호출 경로였다는 뜻이고, 그때는 조각이
            // 아예 없으므로 null을 그대로 내보내 호출부가 폴백을 취하게 한다.
            //
            // steps는 매개변수로 받는다 — currentSteps를 여기서 직접 읽으면 안 된다.
            // 구제 채택(RestoreAdoptedGenerationState)은 lastSkeleton/lastStepSections/
            // stepFloorViolations를 채택된 회차의 스냅샷으로 되돌리지만 currentSteps는
            // 되돌리지 않는다 — 매 회차 갱신되고 목차 재수립 시 지워지는 살아있는 루프
            // 변수라서다. 구제 채택 뒤에는 currentSteps가 폐기된 회차(또는 null)를
            // 가리킬 수 있어, 그걸 쓰면 Sections는 채택된 회차의 것인데 Steps는 폐기된
            // 회차를 서술하는 내부 모순이 생긴다. 호출부는 항상 currentPlanStructure
            // 하나에서 다시 파싱한 adoptedSteps를 넘겨야 한다.
            PlanLayout? BuildLayout(IReadOnlyList<BatchStepPlan>? steps) =>
                lastSkeleton == null || lastStepSections == null
                    ? null
                    : new PlanLayout(
                        lastSkeleton,
                        new Dictionary<string, string>(lastStepSections),
                        steps,
                        new Dictionary<string, StepDefect>(stepFloorViolations));

            // 설정에 따른 최대 시도 횟수 적용 (N회 또는 검증 완료까지)
            int attempt = 1;
            // L1 위반 수리 시도. attempt(채점 예산)와 분리한다 - 채점을 못 받은
            // 회차를 "시도했다"로 세면 실측(POQSettleBatch4)처럼 6회 중 2회가
            // 조용히 사라진다.
            int l1RepairAttempt = 0;
            // 루프가 계산한 오류 코드 누락을 AttachPipelineBanners로 그대로 넘기기 위한
            // 자리. 메서드 밖에서 다시 계산하면 같은 사실이 두 곳에 생겨 갈라진다
            // (설계서 §3-5(a)).
            IReadOnlyDictionary<string, IReadOnlyList<string>> missingErrorCodes =
                new Dictionary<string, IReadOnlyList<string>>();
            var bestAttempt = new BestAttempt();
            // 결함이 있다면서 자리를 못 대는 리뷰의 재호출 상한(회차당 1회) 플래그.
            // 회차(attempt) 단위로 살아 있어야 한다 - while(true) 몸통 안에서
            // 선언하면 재호출 자체가 만드는 continue(attempt 불변)에도 매번
            // 초기화되어 상한이 걸리지 않는다. attempt가 실제로 올라갈 때만
            // 아래에서 명시적으로 되돌린다.
            bool reviewRetriedThisAttempt = false;
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
                        specsCopy.Add((FeedbackSpec.CriticFileName, $"[이전 시도에 대한 검토 피드백]:\n{feedbackLog}\n위 에러/피드백 사항을 전적으로 수용하여 통합 설계서를 완성해 주세요."));
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
                            var planResult = await WrapWithProgress(_consolidatorService.DraftBatchPlanStructureAsync(brainstormResult.Content, targetLanguage, jobName, sourceProcedureRoster, _consolidatorEffort, cancellationToken: cancellationToken), progressScope, "phase2");
                            var planEnrichment = PlanStructureEnricher.Enrich(
                                planResult.Content, specReturnCodes, specTargetTables);
                            currentPlanStructure = planEnrichment.Markdown;
                            NotifyDroppedTableDeclarations(jobName, planEnrichment);
                            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(rawDir, "PlanStructure.md"), currentPlanStructure);
                        }

                        // 목차가 단계 목록을 냈을 때만 분할한다. 못 냈으면 단일 호출로
                        // 폴백한다 — 분할은 개선이지 필수가 아니다.
                        currentSteps = BatchStepPlanParser.TryParse(currentPlanStructure);
                        if (currentSteps == null)
                        {
                            // 화면에 알린다. 폴백은 단계마다 확보한 본문을 한 번에
                            // 무너뜨리는 경로라(:2311 참고) 대가가 크고, 원인은 대개
                            // 목차 JSON의 사소한 흠(Code 중복·Name 누락)이다. 조용히
                            // 강등되면 사용자는 왜 이 회차만 품질이 다른지 알 수 없다.
                            // 상세 사유는 BatchStepPlanParser가 경고 로그에 남긴다.
                            _userInteraction.NotifyStatus(
                                $"[yellow]{jobName}[/] - 목차에서 단계 목록을 읽지 못해 단일 호출로 생성합니다 " +
                                "(로그의 단계 목록 경고를 확인하십시오).");
                        }

                        if (currentSteps != null)
                        {
                            // 골격은 개요·흐름도·검증 SQL을 완성하고, 단계 상세 H2에는
                            // 모든 단계가 공유할 공통 규약만 남긴다. 이 행을 "최종 생성"으로
                            // 표기하면 뒤따르는 단계별 행과 겹쳐 읽혀, 무엇이 이미 끝났고
                            // 무엇이 남았는지 화면만으로는 알 수 없다.
                            progressScope.AddTask("phase3", "3/3. 골격 생성 중 (공통 규약·흐름도)...");

                            var split = await GenerateBySplitAsync(
                                currentPlanStructure, currentSteps, specsCopy, targetLanguage, jobName,
                                progressScope, lastSkeleton, lastSkeletonResult, lastStepSections, stepFloorViolations,
                                pendingDefectiveSteps, knownTableNames, parametersByProcedure, currentBrainstorming,
                                specReturnCodes, specTargetTables, cancellationToken);

                            if (split != null)
                            {
                                splitMarkdown = split.Markdown;
                                aiResult = split.Generation;
                                lastSkeleton = split.Skeleton;
                                lastSkeletonResult = split.Generation;
                                lastStepSections = split.Sections;
                                stepFloorViolations = split.FloorViolations;
                            }
                            else
                            {
                                _userInteraction.NotifyError($"{jobName} - 골격 생성에 실패하여 단일 호출로 계획서를 생성합니다.");
                                // 골격 재시도가 실패해 이번 회차는 완전히 다른 구조(단일
                                // 호출 문서)로 만들어진다. stepFloorViolations만 지우고
                                // lastSkeleton/lastStepSections를 남겨두면, 이번 회차와
                                // 무관한 나중 회차의 지목 재생성이 그 캐시를 재사용해
                                // 하한 미달 기록이 없는 옛 섹션을 조용히 되살릴 수 있다
                                // (실제로 재현됨: 위반 기록 없는 하한 미달 섹션이 배너
                                // 없이 최종 문서에 실렸다). 캐시 전체를 한 번에 지워
                                // 기록과 그 기록이 가리키던 섹션이 함께 죽게 한다.
                                ClearSplitGenerationCacheAfterRedraft(
                                    out lastSkeleton, out lastSkeletonResult, out lastStepSections, out currentSteps,
                                    out stepFloorViolations, pendingDefectiveSteps);
                            }
                        }

                        pendingDefectiveSteps.Clear();

                        if (splitMarkdown == null)
                        {
                            // 분할하지 못했거나 골격이 실패한 경로. 이때만 문서 전체를 한 번에
                            // 만든다. 골격 행과 키를 나눠 두 행이 나란히 남게 한다 — 분할을
                            // 시도했다가 폴백했다는 사실이 화면에 보여야 한다.
                            progressScope.AddTask("phase3single", "3/3. 최종 생성 중 (단일 호출)...");

                            // 폴백도 원본 인터페이스 표를 받아야 한다 - 규칙 5가 그 표를
                            // 가리키며 "여기 적힌 파라미터가 전부"라고 말하기 때문이다.
                            // 목차를 못 읽어 폴백한 회차라면 단계 목록이 없어 표도 비는데,
                            // 그때는 AiService가 절 자체를 싣지 않는다(빈 표가 "원본
                            // 파라미터가 없다"로 읽히는 것을 막는다).
                            //
                            // 분할 SP 문서 단위 검사(Task 5)도 이 경로에서는 돌리지 않는다 -
                            // 이유는 LogSplitProcedureObligationSkipped 문서 참고.
                            LogSplitProcedureObligationSkipped(jobName);

                            aiResult = await WrapWithProgress(_consolidatorService.GenerateConsolidatedBatchPlanAsync(currentPlanStructure, specsCopy, targetLanguage, jobName, _consolidatorEffort, InterfacesFor(currentPlanStructure), currentBrainstorming, cancellationToken), progressScope, "phase3single");
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
                        outputRoot, jobName, currentPlanStructure, adoptedState.PlanStructure, cancellationToken);
                    RestoreAdoptedGenerationState(
                        adoptedState, out lastSkeleton, out lastSkeletonResult, out lastStepSections, out stepFloorViolations);
                    finalAiResult = rescued.Generation ?? finalAiResult;
                    planReview = rescued.Review;
                    planOutcome = VerificationOutcome.QualityRejected;
                    // rescued.Markdown에는 이미 QualityRejected 배너가 붙어 있다(RetryRescue
                    // 참조). 코드 대조는 배너 없는 원본이 필요하므로 bestAttempt.Current가
                    // 들고 있는 채점 시점의 원본을 쓴다.
                    documentBodyForChecks = bestAttempt.Current!.Markdown;
                    consolidatedPlan = rescued.Markdown;
                    break;
                }

                // L1: 기계적 무결성 검사
                var l1Result = _validator.ValidateConsolidated(consolidatedPlan);
                consolidatedPlan = l1Result.CleansedMarkdown ?? consolidatedPlan;

                // 원본 오류 코드 누락은 결정적으로 판정된다. 루프 밖 배너로만 내보내면
                // 재시도에 한 번도 먹이지 못한다 - 실측에서 이 축이 미달 5편 중 3편의
                // 유일한 불합격 사유였다.
                //
                // 계산 결과는 아래 AttachPipelineBanners로 넘긴다. 같은 사실을 두 곳에서
                // 각자 계산하면 갈라진다.
                missingErrorCodes = MechanicalValidator.FindMissingErrorCodes(consolidatedPlan, specReturnCodes);
                if (missingErrorCodes.Count > 0)
                {
                    foreach (var (procedure, codes) in missingErrorCodes)
                    {
                        l1Result.Errors.Add(
                            $"원본 프로시저 `{procedure}`의 반환 코드 {string.Join(", ", codes)}이(가) " +
                            "계획서 어디에도 없습니다. 레거시 호출자가 읽던 코드가 사라지므로, " +
                            "해당 단계 본문에 원본 코드를 그대로 실으십시오.");
                    }

                    l1Result.IsValid = false;
                }

                // 누락 코드를 단계로 귀속한다(설계서 §3-5(b)). missingErrorCodes는
                // 방금 위에서 이번 회차 값으로 채워졌다 - 오류 코드 검사는 L1에서
                // 판정되므로 귀속도 여기서 일어나야 한다(FIX ROUND 2 리뷰 지적:
                // L2 지목 수집부에 있던 자리는 L1 게이트가 이미 막아 항상 빈 입력만
                // 받아 §3-5(b)가 구조적으로 도달 불가능했다).
                //
                // L1이 통과한 회차(missingErrorCodes 비었음)에도 매번 다시 계산한다 -
                // if(!l1Result.IsValid) 안에서만 계산하면 그 회차에 결함이 없어 이
                // 블록을 건너뛸 때 machineFoundStructureDefect가 직전 회차의 "참"
                // 값을 그대로 들고 있다가 결함이 고쳐진 뒤에도 영원히 목차 결함으로
                // 잘못 보고한다.
                var codeAttribution = ErrorCodeAttribution.Attribute(missingErrorCodes, currentSteps);
                var previouslyFoundStructureDefect = machineFoundStructureDefect;
                machineFoundStructureDefect = codeAttribution.HasUnattributed;
                // 상태가 새로 참이 될 때만 알린다 - 결함이 지속되는 매 회차마다 같은
                // 배너를 반복하면 화면에서 진짜 변화를 가린다(FIX ROUND 1).
                if (machineFoundStructureDefect && !previouslyFoundStructureDefect)
                {
                    _userInteraction.NotifyStatus(
                        $"[yellow]{jobName}[/] - 어느 단계도 맡지 않은 원본 오류 코드가 누락되어 " +
                        "목차 결함으로 기록합니다.");
                }

                if (!l1Result.IsValid)
                {
                    _userInteraction.NotifyL1Errors(jobName, attempt, _maxAttempts, l1Result.Errors);

                    // 예산을 고르기 *전에* 귀속을 먼저 시도한다. 순서가 핵심이다 -
                    // 귀속 성공(지목 재생성)과 귀속 실패(전량 재생성)는 서로 다른
                    // 예산을 쓴다(설계서 §3-3 마지막 문단·§5-4 불변식 #5-1·#10).
                    // 세기 전에 세면(귀속과 무관하게 l1RepairAttempt만 올리면) 문서
                    // 전역 위반(귀속 불가 - 헤더 누락 등)이 채점 예산 대신 L1 자기
                    // 예산만 받아, 이 태스크가 없애려는 L1 소진을 그 부류에 한해
                    // 오히려 쉽게 만드는 회귀가 된다.
                    //
                    // 위반을 단계에 귀속해 그 단계만 다시 뽑는다. 실측(POQSettleBatch4
                    // 시도 3)의 L1 실패는 `END TRY` 하나였는데 문서 전체를 다시 만들었다.
                    //
                    // 귀속하지 못하면 pendingDefectiveSteps가 비고, 그러면 종전대로
                    // 전량 재생성이 된다 - 억지로 아무 단계에나 붙이면 멀쩡한 단계를
                    // 다시 쓰게 되어 회귀 롤백이 막으려는 회귀를 다시 들인다.
                    //
                    // 귀속은 두 갈래다. 위반 유형 자체가 자리를 아는 것은 그 규칙으로
                    // 바로 귀속하고, 나머지만 어휘 검색으로 넘긴다.
                    //
                    // 나누는 이유: BatchRunRowNeverCreated와 LegacyReturnCodeNeverBound는
                    // **없는 것이 위반**이라 문서에서 어휘를 찾을 수 없다. 어휘 검색에만
                    // 맡기면 영원히 귀속 실패로 떨어져 전량 재생성을 부른다 - 설계서
                    // §3-5(c) 표가 이 둘을 하드 귀속으로 규정한 이유가 그것이다.
                    pendingDefectiveSteps.Clear();

                    void AddOwner(string? code)
                    {
                        if (!string.IsNullOrEmpty(code) &&
                            !pendingDefectiveSteps.Contains(code, StringComparer.OrdinalIgnoreCase))
                        {
                            pendingDefectiveSteps.Add(code);
                        }
                    }

                    foreach (var detail in l1Result.DetailedErrors)
                    {
                        switch (detail.Type)
                        {
                            case ErrorType.BatchRunRowNeverCreated:
                                // RunId 발급 계약은 단계 목록의 첫 단계가 진다.
                                if (currentSteps is { Count: > 0 }) AddOwner(currentSteps[0].Code);
                                break;

                            case ErrorType.LegacyReturnCodeNeverBound:
                                // 이 값의 거처는 오류 코드를 선언한 단계들이다.
                                foreach (var step in currentSteps ?? Enumerable.Empty<BatchStepPlan>())
                                {
                                    if (step.ErrorCodes.Count > 0) AddOwner(step.Code);
                                }
                                break;

                            default:
                                foreach (var lexeme in MechanicalValidator.ViolationLexemes(detail))
                                {
                                    AddOwner(L1ViolationAttribution.AttributeByLexeme(
                                        consolidatedPlan, lexeme, currentSteps));
                                }
                                break;
                        }
                    }

                    // 오류 코드 누락은 DetailedErrors에 실리지 않아(Errors 문자열로만
                    // 실린다) 위 switch 순회로는 닿지 못한다. codeAttribution은 이
                    // if 블록 진입 전에 이미 이번 회차 값으로 계산해 뒀다(위 참고) -
                    // 여기서 다시 계산하지 않고 그 결과만 반영한다.
                    foreach (var code in codeAttribution.StepCodes) AddOwner(code);

                    // 귀속 결과로 예산을 고른다. 지목 재생성(귀속 성공)은 L1 자기
                    // 예산을 쓰고 채점 예산(attempt)을 건드리지 않는다. 전량 재생성
                    // (귀속 실패)은 채점 대상 문서를 새로 만드는 일이므로 채점 예산을
                    // 쓴다 - 예산 분리 이전(단일 _maxAttempts) 동작과 같아 회귀가 아니다.
                    bool attributedToSteps = pendingDefectiveSteps.Count > 0;
                    bool canRetry = attributedToSteps
                        ? l1RepairAttempt + 1 <= _maxL1RepairAttempts
                        : _maxAttempts == -1 || attempt < _maxAttempts;

                    if (canRetry)
                    {
                        if (attributedToSteps)
                        {
                            l1RepairAttempt++;
                            _userInteraction.NotifyStatus(
                                $"[yellow]{jobName}[/] - L1 위반을 {string.Join(", ", pendingDefectiveSteps)} 단계로 " +
                                "좁혀 그 단계만 다시 만듭니다.");
                        }
                        else
                        {
                            attempt++;
                            // 새 회차가 시작된다 - 리뷰 재호출 상한도 새로 받는다.
                            reviewRetriedThisAttempt = false;
                        }

                        feedbackLog = CriticFeedbackLog.ComposeAfterL1Failure(l1Result.SuggestedPromptFix, feedbackHistory);
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
                                outputRoot, jobName, currentPlanStructure, adoptedState.PlanStructure, cancellationToken);
                            RestoreAdoptedGenerationState(
                                adoptedState, out lastSkeleton, out lastSkeletonResult, out lastStepSections, out stepFloorViolations);
                            finalAiResult = rescued.Generation ?? finalAiResult;
                            planReview = rescued.Review;
                            planOutcome = VerificationOutcome.QualityRejected;
                            // rescued.Markdown은 이미 배너가 붙은 문자열이다 - 원본은
                            // bestAttempt.Current에서 가져온다.
                            documentBodyForChecks = bestAttempt.Current!.Markdown;
                            consolidatedPlan = rescued.Markdown;
                            break;
                        }

                        _userInteraction.NotifyError($"{jobName} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                        planOutcome = VerificationOutcome.L1Exhausted;
                        // 배너를 붙이기 직전의 값이 배너 없는 원본이다.
                        documentBodyForChecks = consolidatedPlan;
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
                        l2Result = await WrapWithProgress(
                            AiCallRetry.ExecuteAsync(
                                () => _criticService.ReviewConsolidatedPlanAsync(specs, consolidatedPlan, jobName, _criticEffort, cancellationToken),
                                cancellationToken),
                            progressScope, "batchreview");
                    }
                    reviewSuccess = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _userInteraction.NotifyError($"{jobName} - AI 교차 리뷰 실패 (시도 {attempt}): {ex.Message}");
                    reviewFailureReason = ex.Message;
                }

                if (reviewSuccess && l2Result != null)
                {
                    // 단일 객체 루프와 같은 순서 - 후보 등록 전에 게이트를 통과시킨다.
                    // TryRecord는 NormalizedScore만 읽으므로 HasDefects를 덮어써도
                    // 최고점 판정은 흔들리지 않는다.
                    EnforceScoreThreshold(l2Result, jobName, attempt);
                }

                // 불합격 여부와 무관하게 후보로 등록한다.
                // 반환값은 "이번 회차가 최고점을 갱신했는가"이며, 그것이 곧 정체 신호다.
                bool improvedThisAttempt = false;
                if (reviewSuccess && l2Result != null)
                {
                    improvedThisAttempt = bestAttempt.TryRecord(attempt, consolidatedPlan, l2Result, finalAiResult);
                    if (improvedThisAttempt)
                    {
                        // 후보가 교체되는 바로 그 자리에서 그 후보를 만든 상태를
                        // 통째로 붙잡는다. 다른 곳에서 갱신하면 어긋나는 순간이 생긴다.
                        adoptedState = new AdoptedGenerationState(
                            currentPlanStructure,
                            lastSkeleton,
                            lastSkeletonResult,
                            lastStepSections == null ? null : new Dictionary<string, string>(lastStepSections),
                            new Dictionary<string, StepDefect>(stepFloorViolations));
                    }
                    else if (bestAttempt.Current != null)
                    {
                        // 회귀 롤백. 이번 회차는 최고 후보보다 나쁘므로 산출물을 버리고
                        // 최고 후보 상태로 되감는다 - 다음 회차가 나쁜 문서 위에서
                        // 시작하면 회차를 늘려도 품질이 오른다는 보장이 없다.
                        // 실측(POQSettleBatch4): 78 -> 76 -> 84 -> 74. 마지막이 첫 회차보다 낮았다.
                        //
                        // 피드백 누적(feedbackHistory)은 되돌리지 않는다. 버린 회차에서
                        // 얻은 지적도 정보이고, 그것까지 버리면 같은 결함을 다시 만든다.
                        //
                        // 단순 대입이 아니라 AdoptPlanStructureForRescueAsync를 거친다 -
                        // 이 회차 사이에 목차 재수립(StructureRedraftPolicy)이 있었다면
                        // PlanStructure.md가 이미 그 재수립본으로 갱신돼 있다. 여기서
                        // 메모리만 되돌리고 파일을 그대로 두면, 나중에 예산 소진으로
                        // 도달하는 구제 경로(AdoptPlanStructureForRescueAsync 재호출)가
                        // "현재==채택본"으로 보고 아무 일도 하지 않아 파일에는 채택되지
                        // 않은 재수립 목차가 그대로 남는다(실측: 재현된 회귀).
                        currentPlanStructure = await AdoptPlanStructureForRescueAsync(
                            outputRoot, jobName, currentPlanStructure, adoptedState.PlanStructure, cancellationToken);
                        RestoreAdoptedGenerationState(
                            adoptedState, out lastSkeleton, out lastSkeletonResult, out lastStepSections, out stepFloorViolations);

                        // currentSteps는 RestoreAdoptedGenerationState가 되돌리지 않는다
                        // (살아있는 루프 변수라서다 - :1827의 주석 참조). 여기서 채택된
                        // 목차 하나에서 다시 파싱하지 않으면 Sections는 채택본인데
                        // Steps는 폐기본을 서술하는 모순이 생긴다.
                        currentSteps = BatchStepPlanParser.TryParse(currentPlanStructure);

                        _userInteraction.NotifyStatus(
                            $"[yellow]{jobName}[/] - {attempt}차 시도({l2Result.NormalizedScore}/100)가 " +
                            $"최고 후보({bestAttempt.Current.AttemptNumber}차, {bestAttempt.Current.Review.NormalizedScore}/100)를 " +
                            "넘지 못해 최고 후보 상태로 되돌립니다.");
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
                        if (redraftPolicy.TryConsume(
                                improvedThisAttempt,
                                l2Result.StructureDefective || machineFoundStructureDefect))
                        {
                            var redrafted = await DraftReplacementPlanStructureAsync(
                                "재시도가 점수를 개선하지 못해 목차를 다시 설계합니다",
                                specReturnCodes,
                                specTargetTables,
                                currentPlanStructure, currentBrainstorming, feedbackLog,
                                targetLanguage, jobName, sourceProcedureRoster, cancellationToken);

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
                                    out lastSkeleton, out lastSkeletonResult, out lastStepSections, out currentSteps,
                                    out stepFloorViolations, pendingDefectiveSteps);
                            }
                        }

                        // 어느 단계가 문제인지 세 신호를 합쳐 정한다. Critic 지목만 쓰면
                        // 기계가 아는 결함(하한 미달)이 있는 단계가 동결된다.
                        //
                        // 오류 코드 신호는 여기서 항상 빈 목록이다 - 오류 코드 누락은
                        // L1에서 판정되고 위쪽 L1 블록이 이미 귀속까지 마친다(FIX ROUND 2).
                        // missingErrorCodes가 비어 있지 않았다면 l1Result.IsValid가
                        // false가 되어 이 L2 코드에 도달하기 전에 위 L1 분기가 continue나
                        // break로 처리했을 것이다 - 즉 여기 도달했다는 사실 자체가 이번
                        // 회차에 오류 코드 누락이 없었다는 증거다. OpenSteps의 시그니처
                        // (세 신호의 AND)는 그대로 둔다 - L1 게이트가 앞으로 느슨해지면
                        // 이 자리에 다시 실제 신호를 넘겨야 하고, 그때도 이 함수는 안
                        // 바뀐다(설계서 §3-2(b)).
                        pendingDefectiveSteps.Clear();
                        var openSteps = StepFreezeState.OpenSteps(
                            currentSteps, l2Result.DefectiveSteps, stepFloorViolations, Array.Empty<string>());

                        if (openSteps != null)
                        {
                            pendingDefectiveSteps.AddRange(openSteps);
                        }

                        // 결함이 있다면서 자리를 못 대는 리뷰는 재생성의 근거가 될 수 없다.
                        // 종전에는 이 경우 골격까지 새로 만들어 전량 재생성을 불렀다.
                        //
                        // 재호출은 한 회차당 1회다. 상한이 없으면 Critic이 계속 자리를
                        // 못 대는 동안 유료 호출이 무한히 돈다. 두 번째도 못 대면 통과가
                        // 아니라 "리뷰 무효"로 확정한다 - 자리를 못 대는 리뷰를 통과로
                        // 읽는 것이 이 설계가 막으려는 침묵이다.
                        //
                        // AxisThresholdForced는 이 게이트에서 제외한다 - EnforceScoreThreshold가
                        // 축 미달로 강제한 결함은 Critic 자신의 신고가 아니라 점수의 문제라
                        // 애초에 지목할 문서 자리가 없다. 그것까지 상한에 걸면 축 미달
                        // 재시도가 채점 예산을 다 쓰기도 전에 리뷰 무효로 잘못 끝난다.
                        if (!l2Result.AxisThresholdForced &&
                            pendingDefectiveSteps.Count == 0 &&
                            !l2Result.SkeletonDefective &&
                            !l2Result.StructureDefective)
                        {
                            if (!reviewRetriedThisAttempt)
                            {
                                reviewRetriedThisAttempt = true;
                                _userInteraction.NotifyStatus(
                                    $"[yellow]{jobName}[/] - Critic이 결함을 신고했으나 자리를 대지 못해 리뷰를 다시 요청합니다.");
                                continue;   // attempt 를 올리지 않는다
                            }

                            _userInteraction.NotifyError(
                                $"{jobName} - Critic이 두 번 연속 결함의 자리를 대지 못했습니다. 리뷰 무효로 확정합니다.");
                            planOutcome = VerificationOutcome.ReviewNotRun;
                            documentBodyForChecks = consolidatedPlan;
                            consolidatedPlan =
                                VerificationBanner.ReviewNotRun("Critic이 결함의 자리를 대지 못했습니다.") + consolidatedPlan;
                            break;
                        }

                        if (l2Result.SkeletonDefective)
                        {
                            // 골격만 버린다. 섹션은 동결 상태로 남겨 다음 회차가
                            // 새 골격 아래에 그대로 조립한다. 성립하지 않으면
                            // 회귀 롤백이 그 회차를 되감는다.
                            //
                            // 골격만 지운다 - lastStepSections는 그대로 둔다.
                            // GenerateBySplitAsync는 "골격 재사용"과 "지목 단계만
                            // 재생성"을 독립으로 판정하므로(§3-6), 골격이 없어도
                            // 섹션 캐시가 살아 있으면 다음 회차는 pendingDefectiveSteps로
                            // 지목된 단계만 다시 만들고 나머지는 캐시된 바이트를
                            // 그대로 쓴다.
                            lastSkeleton = null;
                            lastSkeletonResult = null;
                            _userInteraction.NotifyStatus(
                                $"[yellow]{jobName}[/] - 공통 규약과 단계 본문의 모순이 지적되어 골격만 다시 만듭니다.");
                        }

                        attempt++;
                        // 새 회차가 시작된다 - 리뷰 재호출 상한도 새로 받는다.
                        reviewRetriedThisAttempt = false;
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
                                outputRoot, jobName, currentPlanStructure, adoptedState.PlanStructure, cancellationToken);
                            RestoreAdoptedGenerationState(
                                adoptedState, out lastSkeleton, out lastSkeletonResult, out lastStepSections, out stepFloorViolations);
                        }

                        finalAiResult = rescued?.Generation ?? finalAiResult;
                        planOutcome = VerificationOutcome.QualityRejected;
                        planReview = adoptedReview;
                        // rescued가 있으면 배너 없는 원본은 bestAttempt.Current에 있다.
                        // 없으면(구제할 후보가 없음) 지금 값이 곧 배너를 붙이기 직전의
                        // 원본이다 - 아래에서 그 값을 읽은 뒤에 덮어쓴다.
                        documentBodyForChecks = rescued != null ? bestAttempt.Current!.Markdown : consolidatedPlan;
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
                            outputRoot, jobName, currentPlanStructure, adoptedState.PlanStructure, cancellationToken);
                        RestoreAdoptedGenerationState(
                            adoptedState, out lastSkeleton, out lastSkeletonResult, out lastStepSections, out stepFloorViolations);
                        finalAiResult = rescued.Generation ?? finalAiResult;
                        planReview = rescued.Review;
                        planOutcome = VerificationOutcome.QualityRejected;
                        documentBodyForChecks = bestAttempt.Current!.Markdown;
                        consolidatedPlan = rescued.Markdown;
                        break;
                    }

                    _userInteraction.NotifyError(
                        $"{jobName} - [[L2 AI 리뷰]] 를 수행하지 못해 교차 검증 없이 계획서를 확정합니다.");
                    planOutcome = VerificationOutcome.ReviewNotRun;
                    documentBodyForChecks = consolidatedPlan;
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
                    // 통과 경로는 배너가 붙지 않으므로 지금 값이 곧 원본이다.
                    documentBodyForChecks = consolidatedPlan;
                    break;
                }
            }

            // 목차 파싱을 호출부에 두는 이유: L3(Task 4)가 같은 결과를 승인 화면의
            // 단계 선택 목록에도 넘겨야 한다. 메서드 안에서 파싱하면 두 번 파싱하거나
            // 시그니처를 다시 바꾸게 된다.
            //
            // 유지보수 불변식(재검토 시 반드시 지킬 것): (1) 이 재계산은 재시도
            // 루프가 완전히 끝난 뒤, currentPlanStructure 하나에서만 파싱해야
            // 한다 — 루프 중간의 어느 지역 변수(특히 currentSteps)도 다시 쓰지
            // 않는다. (2) 앞으로 채택 문서를 이전 회차로 되돌리는 새 종료 경로를
            // 추가한다면, 그 경로는 반드시 currentPlanStructure를 그 회차의 목차로
            // 되감아야 한다 — 안 그러면 이 재계산이 채택되지 않은 문서를 서술한다.
            // (3) 루프에 새 종료 경로(break/return)를 추가한다면 documentBodyForChecks도
            // 그 자리에서 함께 채워야 한다 - 빠뜨리면 오류코드 누락 검사가 직전 회차의
            // 원본을 보게 된다.
            var adoptedSteps = BatchStepPlanParser.TryParse(currentPlanStructure);
            VerificationCoverage? coverage;
            (consolidatedPlan, coverage) = AttachPipelineBanners(
                consolidatedPlan, documentBodyForChecks, stepFloorViolations, adoptedSteps, specs, jobName,
                missingErrorCodes);

            // L3: 인간 개입형 승인 (TUI 모드 전용, 배치 모드 시 즉시 승인 및 반환)
            if (isBatchMode)
            {
                _userInteraction.NotifyStatus($"[green]{jobName}[/] - 배치 모드로 인해 통합 계획서가 자동으로 최종 승인되었습니다.");
                return new ConsolidatedPipelineResult(consolidatedPlan, finalAiResult, planReview, planOutcome, BuildLayout(adoptedSteps), coverage);
            }

            while (true)
            {
                // 이 경로에만 다시 세울 목차가 있으므로 구조 변경 질문을 여기서만 허용한다.
                // 단계 목록도 함께 넘긴다 — 사용자가 피드백 대상을 고를 수 있어야 한다.
                // adoptedSteps는 채택된 문서를 만든 목차에서 파싱한 것이다(AttachPipelineBanners
                // 앞에서 계산됨). 살아있는 currentSteps를 쓰면 폐기된 회차의 목록을 보여줄 수 있다.
                var reviewResult = await _userInteraction.RequestHumanReviewAsync(
                    jobName, consolidatedPlan, planOutcome, structureRedraftSupported: true, steps: adoptedSteps);

                if (reviewResult.Decision == UserDecision.Approve)
                {
                    return new ConsolidatedPipelineResult(consolidatedPlan, finalAiResult, planReview, planOutcome, BuildLayout(adoptedSteps), coverage);
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
                            specReturnCodes,
                            specTargetTables,
                            currentPlanStructure, currentBrainstorming, reviewResult.UserFeedback,
                            targetLanguage, jobName, sourceProcedureRoster, cancellationToken);
                        if (pendingPlanStructure != null)
                        {
                            structureForRegeneration = pendingPlanStructure;
                        }
                    }

                    var specsCopy = new System.Collections.Generic.List<(string FileName, string Content)>(specs);
                    specsCopy.Add((FeedbackSpec.UserFileName, $"[L3 사용자 보완 피드백 로그]:\n{reviewResult.UserFeedback}\n사용자 의견을 수용하여 설계 내용을 수정 및 보완해 주십시오."));

                    // 분할 상태가 있으면 분할로 재생성한다. 통짜 단일 호출은 단계마다
                    // 확보한 본문을 한 번에 무너뜨린다 — 이 경로가 존재하는 이유다.
                    string rePlan = string.Empty;
                    Dictionary<string, StepDefect> reViolations = stepFloorViolations;

                    var stepsForRegeneration = BatchStepPlanParser.TryParse(structureForRegeneration);
                    if (stepsForRegeneration != null)
                    {
                        // 구조 재수립·골격 지목이면 골격부터 다시 만든다. 그 외에는
                        // 캐시된 골격을 재사용하되, 지목이 없으면 전 단계를 다시 만든다.
                        var reuseSkeleton =
                            !reviewResult.RedraftStructure &&
                            !reviewResult.RegenerateSkeleton &&
                            lastSkeleton != null &&
                            lastStepSections != null;

                        // GenerateBySplitAsync는 "골격 재사용"과 "지목된 단계만 재생성"을
                        // defectiveSteps 하나로 함께 결정한다(재시도 루프가 요구하는 계약 —
                        // 지목이 비면 골격까지 새로 만든다, 아래 테스트가 고정: WithoutDefectiveSteps_
                        // RegeneratesTheWholeDocument). L3의 "지목 없음"은 뜻이 다르다 — 목차·골격은
                        // 그대로 두고 전 단계만 다시 쓰라는 것이다. 그래서 지목이 비어 있으면 목차의
                        // 전체 코드를 defectiveSteps로 넘긴다: targeted 판정(Count>0)은 참이 되어
                        // 골격 호출을 건너뛰고, pending 필터는 모든 코드를 통과시켜 전 단계가
                        // 재생성된다.
                        var stepsToRegenerate = reviewResult.TargetStepCodes.Count > 0
                            ? reviewResult.TargetStepCodes
                            : stepsForRegeneration.Select(step => step.Code).ToList();

                        // 골격을 재사용하지 않는 경우(구조 재수립·골격 지목·캐시 없음) 목차의
                        // 단계 코드 자체가 바뀔 수 있다. 그때 살아있는 stepFloorViolations를
                        // 그대로 넘기면, GenerateBySplitAsync는 이번에 다시 만드는 코드의
                        // 기록만 지우고 나머지는 그대로 복사한다 — 새 목차에 없는 옛 코드의
                        // "하한 미달" 기록이 살아남아 아래 배너 재부착에서 문서에 없는 단계를
                        // 지목하게 된다. 재시도 루프의 ClearSplitGenerationCacheAfterRedraft가
                        // 같은 부류의 결함을 이미 한 번 겪고 막아 둔 자리다.
                        var violationsForRegeneration = reuseSkeleton
                            ? stepFloorViolations
                            : new Dictionary<string, StepDefect>();

                        using var progressScopeForL3 = _userInteraction.CreateProgressScope("피드백 반영 재생성") ?? NullProgressScope.Instance;
                        // GenerateBySplitAsync는 "phase3" 키로 진행률을 기록한다(재사용이면
                        // 즉시 완료 처리, 재생성이면 실제 골격 호출 진행률). 호출 전에 등록해
                        // 두지 않으면 IMultiProgressScope 구현체가 원시 키 문자열 "phase3"을
                        // 그대로 화면에 찍는다 — 재시도 루프의 골격 행(:1752)과 같은 실수다.
                        progressScopeForL3.AddTask("phase3", "피드백 반영: 골격 확인/생성 중 (공통 규약·흐름도)...");
                        var split = await GenerateBySplitAsync(
                            structureForRegeneration, stepsForRegeneration, specsCopy, targetLanguage, jobName,
                            progressScopeForL3,
                            reuseSkeleton ? lastSkeleton : null,
                            reuseSkeleton ? lastSkeletonResult : null,
                            reuseSkeleton ? lastStepSections : null,
                            violationsForRegeneration,
                            reuseSkeleton ? stepsToRegenerate : new List<string>(),
                            knownTableNames,
                            parametersByProcedure,
                            currentBrainstorming,
                            specReturnCodes,
                            specTargetTables,
                            cancellationToken);

                        if (split != null)
                        {
                            rePlan = split.Markdown;
                            reViolations = split.FloorViolations;
                            lastSkeleton = split.Skeleton;
                            lastSkeletonResult = split.Generation;
                            lastStepSections = split.Sections;
                            currentSteps = stepsForRegeneration;
                            finalAiResult = split.Generation;
                        }
                    }
                    else
                    {
                        // 목차가 단계 목록을 못 냈다. 분할 자체가 불가능하므로
                        // 기존 단일 호출로 간다 — 이 경로의 문서는 애초에 분할로
                        // 만들어지지 않았다.
                        if (pendingPlanStructure != null)
                        {
                            // 구조 재수립이 성공했지만 새 목차는 단계 목록을 못 냈다.
                            // reViolations는 아직 초기화(:2126)에서 물려받은 stepFloorViolations,
                            // 즉 옛 목차의 살아있는 기록이다. 그대로 두면 아래 배너 재부착이
                            // 새 목차에 없는 단계 코드를 지목한다 — 분할 분기(:2158-2160)가
                            // 이미 막아 둔 것과 같은 부류의 결함이다.
                            reViolations = new Dictionary<string, StepDefect>();
                        }

                        // 이 경로도 단일 호출이라 분할 SP 문서 단위 검사(Task 5)를 돌리지
                        // 않는다 - 이유는 LogSplitProcedureObligationSkipped 문서 참고.
                        LogSplitProcedureObligationSkipped(jobName);

                        try
                        {
                            var aiResult = await _consolidatorService.GenerateConsolidatedBatchPlanAsync(
                                structureForRegeneration, specsCopy, targetLanguage, jobName, _consolidatorEffort, InterfacesFor(structureForRegeneration), currentBrainstorming, cancellationToken);
                            rePlan = aiResult.Content;
                            finalAiResult = aiResult;

                            // 이 경로는 문서를 통짜로 다시 만든다. 그런데 lastSkeleton/
                            // lastStepSections는 아직 이전 회차의 조각을 들고 있어,
                            // BuildLayout이 "이미 존재하지 않는 문서"를 서술하는 Sections와
                            // null인 Steps를 함께 내보낸다. PlanBoundaryResolver는
                            // IsSplitAvailable이 참이라 1순위(앵커)를 돌리고, 같은 목차에서
                            // 나온 헤딩들이라 대체로 새 문서에서도 발견된다 - 그러면 재생성이
                            // 추가한 단계가 앞 슬라이스에 조용히 흡수된 채 분할이 성사된다.
                            // 재시도 루프의 ClearSplitGenerationCacheAfterRedraft가 같은
                            // 부류의 결함을 이미 한 번 겪고 막아 둔 자리다.
                            //
                            // 재생성이 실패하면(rePlan이 비면) 아래에서 continue로 돌아가
                            // 직전 문서가 그대로 남으므로, 성공한 뒤에만 비운다.
                            ClearSplitGenerationCacheAfterRedraft(
                                out lastSkeleton, out lastSkeletonResult, out lastStepSections, out currentSteps,
                                out stepFloorViolations, pendingDefectiveSteps);
                        }
                        // 취소는 전파한다. 삼키면 아래 continue가 돌아 취소한
                        // 사용자에게 같은 승인 화면을 한 번 더 내민다.
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _userInteraction.NotifyError($"피드백 반영 재생성 실패: {ex.Message}");
                        }
                    }

                    if (string.IsNullOrEmpty(rePlan))
                    {
                        // 재생성이 실패했으므로 새 목차는 아무 문서도 만들지 않았다.
                        // 기록하지 않은 채 되돌아가면 화면의 문서와 PlanStructure.md가
                        // 계속 같은 목차를 가리킨다. 분할이 null을 돌려준 경우에도
                        // 여기로 들어온다 — 통짜 폴백을 추가하지 않는다. L3에는 이미
                        // 승인 대기 중인 좋은 문서가 있고, 그것을 통짜로 갈아엎는 것은
                        // 개선이 아니다.
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
                    if (!l1Re.IsValid && stepsForRegeneration == null)
                    {
                        // 분할로 만든 문서에는 이 보완을 적용하지 않는다. 문서 전체를
                        // 한 번에 다시 써서 단계마다 확보한 본문을 무너뜨리기 때문이다.
                        // 분할 경로에서 L1이 실패하는 원인(H2 누락·Mermaid 문법)은
                        // 골격이 만드는 것이므로, 사용자가 골격을 지목해 다시 시도하면 된다.
                        _userInteraction.NotifyStatus("피드백 적용본에서 정적 에러가 검출되어 AI 자가 수정 1회 더 진행합니다.");
                        try
                        {
                            var specsRe = new System.Collections.Generic.List<(string FileName, string Content)>(specsCopy);
                            specsRe.Add((FeedbackSpec.L1FixFileName, l1Re.SuggestedPromptFix ?? string.Empty));
                            var aiResult = await _consolidatorService.GenerateConsolidatedBatchPlanAsync(structureForRegeneration, specsRe, targetLanguage, jobName, _consolidatorEffort, InterfacesFor(structureForRegeneration), currentBrainstorming, cancellationToken);
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
                    stepFloorViolations = reViolations;
                    // reSteps로 그림자 지역 변수를 새로 두지 않고 바깥 adoptedSteps에
                    // 그대로 대입한다 — 다음 루프 회차의 RequestHumanReviewAsync(:2074)가
                    // 이 값을 그대로 읽는다. 그림자로 두면 화면의 다중 선택 목록이 이번
                    // 회차에서 사라졌거나 새로 생긴 단계 코드를 반영하지 못한 채 낡은
                    // 목록을 계속 보여준다.
                    adoptedSteps = BatchStepPlanParser.TryParse(currentPlanStructure);
                    // 이 경로의 consolidatedPlan은 방금 rePlan에서 받은 배너 없는 원본이다
                    // (아래 L1Exhausted 배너는 이 호출 다음에 붙는다).
                    //
                    // coverage도 여기서 다시 대입한다: 재생성이 단계 구성을 바꿨다면
                    // 재시도 루프 종료 직후 계산한 커버리지는 더 이상 이 문서를
                    // 서술하지 않는다.
                    (consolidatedPlan, coverage) = AttachPipelineBanners(
                        consolidatedPlan, consolidatedPlan, stepFloorViolations, adoptedSteps, specs, jobName);

                    // 분할 경로(stepsForRegeneration != null)에서는 위의 L1 자가 수정을
                    // 일부러 건너뛴다 — 통짜 재작성이 단계별로 확보한 본문을 무너뜨리기
                    // 때문이다. 하지만 건너뛴 채로 L1이 여전히 실패 중이라면, 그 사실이
                    // 화면 어디에도 남지 않은 채 승인 화면에 도달한다. 재시도 루프가
                    // 예산을 소진했을 때 붙이는 것과 같은 배너로 그 사실을 알린다 —
                    // planOutcome은 ReviewNotRun을 그대로 유지한다(L2 리뷰 미수행이라는
                    // 별개의 사실이다).
                    if (!l1Re.IsValid && stepsForRegeneration != null)
                    {
                        consolidatedPlan = VerificationBanner.L1Exhausted(l1Re.Errors) + consolidatedPlan;
                    }
                }
            }
        }

        /// <summary>
        /// 파이프라인이 문서를 사용자에게 건네기 직전에 붙는 배너를 모두 부착한다.
        ///
        /// 재시도 루프 종료 직후와 L3 피드백 재생성 직후, 두 자리에서 호출된다.
        /// 두 벌로 두면 한쪽만 고쳐지는 날이 온다.
        ///
        /// <paramref name="documentBody"/>는 <paramref name="consolidatedPlan"/>과 달리
        /// 배너가 전혀 붙지 않은 원본이다. L1Exhausted·QualityRejected·ReviewNotRun
        /// 배너는 실패 사유(L1 오류 목록, Critic 코멘트 등)를 그대로 인용하는데, 그
        /// 문구에 우연히 숫자 오류코드가 섞이면 오류코드 누락 검사가 배너의 인용문을
        /// "문서에 존재"로 오판한다 - Critic이 "오류코드 -7 반환 경로가 누락되었습니다"라고
        /// 쓴 QualityRejected 배너가 실제로 이 사고를 냈다. 본문만 훑어야 하는 검사는
        /// 반드시 이 매개변수를 쓴다. 이 메서드가 자체로 앞서 붙이는 배너들
        /// (UnverifiableSteps·StepFloorViolations·SplitGenerationSkipped)도 같은 이유로
        /// consolidatedPlan을 오염시키므로, documentBody는 그 배너들의 영향도 받지 않는다.
        /// </summary>
        private (string Plan, VerificationCoverage Coverage) AttachPipelineBanners(
            string consolidatedPlan,
            string documentBody,
            IReadOnlyDictionary<string, StepDefect> stepFloorViolations,
            IReadOnlyList<BatchStepPlan>? adoptedSteps,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string jobName,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? precomputedMissingCodes = null)
        {
            // 문서 헤더와 지시서 §0 양쪽이 소비할 커버리지 사실. 이 메서드 안에서
            // 만들어지는 세 재료(adoptedSteps, stepFloorViolations, missingCodes)를
            // 그대로 이어받아 계산한다 - 메서드 밖에서 missingCodes를 다시 계산하면
            // 같은 사실이 두 곳에 생겨 갈라진다.
            var hasDocumentCodeGap = false;
            // 하한 미달·검증 불가는 파이프라인을 막지 않지만, 조용히 넘어가지도
            // 않는다. 12줄짜리 S10이 아무 신호 없이 나온 것이 이 배너가 필요한
            // 이유다.
            //
            // Kind별로 다른 배너를 붙인다. 실측에서 14개 단계 중 13개가
            // "섹션은 멀쩡한데 대조할 재료가 없어 검사가 못 돈" 경우였는데도
            // "품질 미달" 배너 하나로 뭉뚱그려 나갔다 - 진입점의 "모두 통과"와
            // 정면으로 모순됐다.
            //
            // 사전 값은 이미 "{Code} (사유)" 형식으로 완성된 표시 문자열이다
            // (GenerateStepSectionWithFloorRetryAsync 참조). 배너 시그니처를
            // Dictionary로 바꾸지 않고 표시 문자열 목록으로 투영하는 이유는 둘이다.
            // 다른 배너 메서드(L1Exhausted, UnresolvedReferences)가 모두
            // IReadOnlyList<string>을 받아 계약이 일관되고, Dictionary의 열거
            // 순서는 Remove/재삽입(지목 재생성)을 거치며 보장되지 않으므로 Key
            // 기준으로 정렬해 읽는 순서를 결정적으로 고정한다.
            var byKind = stepFloorViolations
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .ToLookup(kvp => kvp.Value.Kind, kvp => kvp.Value.Reason);

            // 붙이는 순서와 읽히는 순서는 반대다 - 나중에 붙일수록 문서 위로 간다.
            // 검증 불가를 먼저 붙이고 하한 미달을 그다음에 붙여, 하한 미달이 검증
            // 불가보다 위에 오게 한다(더 심각한 쪽을 먼저 읽게 하려는 의도). 이
            // 아래로 분할 미실행 → 오류코드 누락 → 커버리지(누락/검증 불가) 배너가
            // 순서대로 더 붙는데, 나중에 붙는 배너일수록 앞서 붙은 배너 위로
            // 다시 얹힌다. 그래서 최종 문서는 위에서부터 커버리지 → 오류코드 누락
            // → 분할 미실행 → 하한 미달 → 검증 불가 순으로 읽힌다 - 목차·명세서
            // 수준의 결함일수록 위로, 단계 하나의 결함일수록 아래로 간다.
            // 배너는 나중에 붙을수록 위로 얹힌다. 생략 주석은 이 자리의 결함 중
            // 가장 가벼우므로 가장 먼저 붙여 맨 아래에서 읽히게 한다.
            //
            // 스캔 실패가 나머지 배너까지 막지 않도록 격리한다(AGENTS.md 범주 2).
            // 취소 필터는 달지 않는다 - Scan은 문자열 위의 동기 정규식이라 취소
            // 토큰을 넘기는 await를 감싸지 않는다.
            IReadOnlyList<string> omissionComments = Array.Empty<string>();
            try
            {
                omissionComments = OmissionCommentScanner.Scan(consolidatedPlan);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "생략 주석 스캔 중 오류가 발생했습니다. 배너 없이 진행합니다.");
            }

            if (omissionComments.Count > 0 && !string.IsNullOrEmpty(consolidatedPlan))
            {
                consolidatedPlan = VerificationBanner.OmissionComments(omissionComments) + consolidatedPlan;
            }

            var unverifiableSteps = byKind[StepDefectKind.Unverifiable].ToList();
            if (unverifiableSteps.Count > 0 && !string.IsNullOrEmpty(consolidatedPlan))
            {
                consolidatedPlan = VerificationBanner.UnverifiableSteps(unverifiableSteps) + consolidatedPlan;
            }

            var stepFloorViolationMessages = byKind[StepDefectKind.QualityFloor].ToList();
            if (stepFloorViolationMessages.Count > 0 && !string.IsNullOrEmpty(consolidatedPlan))
            {
                consolidatedPlan = VerificationBanner.StepFloorViolations(stepFloorViolationMessages) + consolidatedPlan;
            }

            // 하한 미달 다음에 붙여 그 위로 오게 한다. 본문이 없는 것이 부실한
            // 것보다 심각하므로 먼저 읽혀야 한다.
            var generationFailedSteps = byKind[StepDefectKind.GenerationFailed].ToList();
            if (generationFailedSteps.Count > 0 && !string.IsNullOrEmpty(consolidatedPlan))
            {
                consolidatedPlan = VerificationBanner.GenerationFailedSteps(generationFailedSteps) + consolidatedPlan;
            }

            // 목차 커버리지 검사: 스텝의 내용이 부실한 것과 별개로, 애초에 어느
            // 스텝도 그 프로시저를 다루겠다고 선언하지 않았을 수 있다. 3개
            // 스텝짜리 목차가 12개 프로시저를 받으면 분할은 3개의 통통하고
            // 하한을 통과하는 섹션을 만들고 문서는 Passed로 끝나지만, 9개
            // 프로시저는 최종 문서 어디에도 흔적이 없다 — 부실 섹션보다 더
            // 나쁘다. 부실 섹션은 최소한 존재를 알리기라도 한다.
            //
            // 라이브 루프 변수 currentSteps를 재사용하지 않고 currentPlanStructure에서
            // 새로 파싱하는 이유: 구제(RetryRescue)가 채택 문서를 이전 회차로
            // 되돌릴 때 currentPlanStructure는 AdoptPlanStructureForRescueAsync를
            // 거쳐 adoptedState.PlanStructure로 정확히 갈아타지만, currentSteps는 그
            // 시점에 다시 파싱되지 않는다 — 실패한 마지막 회차의 목차를 여전히
            // 가리킬 수 있다(stepFloorViolations가 겪었던 것과 같은 종류의
            // 문제). currentPlanStructure는 이미 모든 재수립·구제 채택 지점에서
            // "이 문서를 실제로 만든 목차"로 정확히 유지되므로(라인 1810 부근
            // 구제 채택, 라인 1938 재수립 등), 거기서 매번 새로 파싱하면 별도의
            // 스냅샷 변수 없이도 항상 채택된 문서의 목차와 일치한다. stepFloorViolations가
            // adoptedState.FloorViolations라는 스냅샷을 따로 두는 이유(단계
            // 본문의 실제 생성 품질은 회차마다 달라 어느 회차의 것인지가 중요)와
            // 달리, 이 값은 목차(currentSteps의 LegacyProcedures)와 불변 인자
            // specs에만 좌우되고 어느 회차가 그 목차로 무엇을 생성했는지와는
            // 무관하므로 별도 스냅샷이 필요 없다.
            //
            // TryParse가 null이면(목차가 유효한 단계 목록을 못 냈으면) 검사를 그냥
            // 건너뛴다 — 의도적이다. 분할 경로 자체가 "개선이지 필수 단계가 아니다"
            // 라는 계약을 이 검사도 그대로 물려받는다. 목차가 망가진 바로 그 순간이
            // 커버리지가 가장 의심스러운 순간이지만, 더는 사각지대가 아니다 - 바로
            // 아래에서 SplitGenerationSkipped 배너가 "분할이 무산되어 커버리지 검사와
            // 하한 검사가 둘 다 실행되지 않았다"는 사실 자체를 문서에 남긴다. 이
            // 배너가 생기기 전에는 문서에 아무 흔적이 남지 않아 가장 적게 검증된
            // 문서가 가장 깨끗해 보였다(POQSettleProc7, 92점).
            if (adoptedSteps == null && !string.IsNullOrEmpty(consolidatedPlan))
            {
                Log.Warning(
                    "[파이프라인] 목차가 유효한 단계 목록을 내지 못해 분할 생성이 실행되지 않았습니다 - Job: {JobName}",
                    jobName);
                consolidatedPlan = VerificationBanner.SplitGenerationSkipped() + consolidatedPlan;
            }

            var specReturnCodes = SpecReturnCodeExtractor.Extract(specs);

            // 분할 여부와 무관하게 항상 돈다. 폴백 경로에만 걸면 "분할은 됐으나 목차
            // 메타데이터가 비어 단계별 대조가 무실행"인 회차를 놓친다 - 실측에서 그쪽이
            // 먼저 일어났다. 목차를 전혀 쓰지 않는다는 것이 이 검사의 존재 이유다.
            if (!string.IsNullOrEmpty(consolidatedPlan))
            {
                // 루프가 이미 계산했으면 그 값을 쓴다. 같은 사실을 두 번 계산하면
                // 한쪽만 고쳐지는 사고가 난다 - 이 저장소가 이미 겪었다.
                //
                // consolidatedPlan이 아니라 documentBody를 스캔한다 - 위 요약 참조.
                var missingCodes = precomputedMissingCodes
                    ?? MechanicalValidator.FindMissingErrorCodes(documentBody, specReturnCodes);
                if (missingCodes.Count > 0)
                {
                    hasDocumentCodeGap = true;

                    Log.Warning(
                        "[파이프라인] 원본 오류코드가 최종 문서에서 확인되지 않았습니다 - Job: {JobName}, 프로시저: {Count}개",
                        jobName, missingCodes.Count);

                    consolidatedPlan =
                        VerificationBanner.MissingErrorCodes(missingCodes, specReturnCodes) + consolidatedPlan;
                }
            }

            var uncoveredProcedures = adoptedSteps != null
                ? FindUncoveredProcedures(adoptedSteps, specs)
                : Array.Empty<string>();

            // 어느 단계도 출신을 밝히지 않았다면 이 검사는 근거가 0이다. 그 상태에서
            // 나오는 "전부 누락"은 계산 결과가 아니라 재료 없음의 부작용이므로,
            // 누락으로 단정하지 않고 검사가 돌지 못했다고 보고한다.
            var unlabelledSteps = adoptedSteps?.Count(step => step.LegacyProcedures.Count == 0) ?? 0;
            var noOriginsAtAll = adoptedSteps is { Count: > 0 } && unlabelledSteps == adoptedSteps.Count;

            if (noOriginsAtAll)
            {
                Log.Warning(
                    "[파이프라인] 목차의 모든 단계가 LegacyProcedures를 비워 커버리지 대조를 실행하지 못했습니다 " +
                    "- Job: {JobName}, 단계: {Steps}개, 명세서: {Specs}개",
                    jobName, adoptedSteps!.Count, specs.Count);

                if (!string.IsNullOrEmpty(consolidatedPlan))
                {
                    consolidatedPlan =
                        VerificationBanner.CoverageUnverifiable(adoptedSteps.Count, specs.Count)
                        + consolidatedPlan;
                }
            }
            else if (uncoveredProcedures.Count > 0)
            {
                Log.Warning(
                    "[파이프라인] 목차가 커버하지 못한 원본 프로시저가 있습니다 - Job: {JobName}, 개수: {Count}개, 목록: {Procedures}",
                    jobName, uncoveredProcedures.Count, string.Join(", ", uncoveredProcedures));

                if (!string.IsNullOrEmpty(consolidatedPlan))
                {
                    consolidatedPlan =
                        VerificationBanner.UncoveredProcedures(uncoveredProcedures, unlabelledSteps)
                        + consolidatedPlan;
                }
            }

            // noOriginsAtAll(CoverageUnverifiable)과 uncoveredProcedures.Count > 0
            // (UncoveredProcedures)은 바로 위 if/else if로 이미 상호 배타적이다 - 커버리지
            // 대조를 아예 못 돌린 상태와 돌렸는데 일부가 빠진 상태는 같은 순간에 참일 수
            // 없다. 그래서 배너 두 개를 필드 하나(HasUncoveredProcedures)로 합쳐 넘긴다.
            return (consolidatedPlan,
                VerificationCoverage.From(
                    adoptedSteps, stepFloorViolations, hasDocumentCodeGap,
                    hasUncoveredProcedures: noOriginsAtAll || uncoveredProcedures.Count > 0));
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
            IReadOnlyDictionary<string, IReadOnlyList<string>> returnCodes,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> targetTables,
            string currentStructure,
            string brainstorming,
            string? redraftFeedback,
            string targetLanguage,
            string jobName,
            IReadOnlyList<string> sourceProcedures,
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
                            brainstorming, targetLanguage, jobName, sourceProcedures, _consolidatorEffort,
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

            // 재수립 경로와 L3 사용자 요청 경로가 이 헬퍼를 공유한다. 여기서 한 번
            // 보강하면 두 경로가 함께 덮인다 - 호출부마다 따로 걸면 하나를 빠뜨린다.
            var enrichment = PlanStructureEnricher.Enrich(redrafted, returnCodes, targetTables);
            NotifyDroppedTableDeclarations(jobName, enrichment);
            return enrichment.Markdown;
        }

        /// <summary>
        /// 목차가 선언했으나 정적 분석에 없는 대상 테이블을 사용자에게 알린다.
        ///
        /// 배너나 StepDefect로 올리지 않는 이유: 이 사실은 목차가 확정된 뒤에
        /// 관측되므로 재생성으로 고칠 수 없다. 고칠 수 없는 것을 재시도 루프에
        /// 넣는 것이 이 저장소가 이미 두 번 물린 실패 모드다. 그렇다고 침묵하지도
        /// 않는다 - 그 이름들은 계획서 본문에도 들어가 있다.
        /// </summary>
        private void NotifyDroppedTableDeclarations(string jobName, PlanStructureEnricher.PlanStructureEnrichment enrichment)
        {
            if (enrichment.DroppedTableDeclarations.Count == 0)
            {
                return;
            }

            foreach (var message in enrichment.DroppedTableDeclarations)
            {
                Log.Warning("목차 선언과 정적 분석이 어긋납니다 - {Message}", message);
            }

            _userInteraction.NotifyCatalogMismatches(jobName, new List<string>(enrichment.DroppedTableDeclarations));
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
            Dictionary<string, StepDefect> FloorViolations);

        /// <summary>
        /// 채택 후보(BestAttempt.Current)를 실제로 만들어 낸 상태 일체.
        /// 후보가 교체되는 그 자리에서 통째로 붙잡고, 구제 채택 시 통째로 되돌린다.
        ///
        /// 다섯 값을 개별 변수로 두면 "함께 움직여야 한다"가 규율이 되고, 규율은
        /// 깨진다 — 이 파이프라인에서 이미 세 번 깨졌다. 레코드로 묶으면 구조가 된다.
        ///
        /// 유지보수 불변식: 채택 문서를 이전 회차로 되돌리는 종료 경로를 새로
        /// 추가한다면 반드시 이 레코드를 통째로 되돌려야 한다. 개별 필드만 되돌리는
        /// 코드를 쓰지 말 것 — 그러려고 묶었다.
        ///
        /// 테스트 커버리지 상태: PlanStructure·FloorViolations의 되감기는
        /// VerificationPipelineOrchestratorTests에서 관찰 가능하다(구제 문서와
        /// 배너가 채택 회차를 서술하는지 검증하는 기존 테스트들). Skeleton·
        /// SkeletonResult·StepSections의 되감기도 이제
        /// RunConsolidatedPipeline_L3FeedbackAfterRescue_ReusesTheAdoptedAttemptsStepSections가
        /// 관찰한다 — L3 지목 재생성이 이 값들을 읽기 시작하면서(그 값을 읽는
        /// 캐시 재사용 로직) 이 세 값도 더 이상 블랙박스가 아니게 됐다.
        /// </summary>
        private sealed record AdoptedGenerationState(
            string PlanStructure,
            string? Skeleton,
            AiResult? SkeletonResult,
            IReadOnlyDictionary<string, string>? StepSections,
            IReadOnlyDictionary<string, StepDefect> FloorViolations);

        /// <summary>
        /// 채택 상태를 살아있는 지역 변수들로 되돌린다. 사전은 복사해서 넘긴다 —
        /// 스냅샷을 그대로 참조시키면 이후 변형이 스냅샷을 오염시킨다.
        /// </summary>
        private static void RestoreAdoptedGenerationState(
            AdoptedGenerationState adopted,
            out string? skeleton,
            out AiResult? skeletonResult,
            out Dictionary<string, string>? stepSections,
            out Dictionary<string, StepDefect> floorViolations)
        {
            skeleton = adopted.Skeleton;
            skeletonResult = adopted.SkeletonResult;
            stepSections = adopted.StepSections == null
                ? null
                : new Dictionary<string, string>(adopted.StepSections);
            floorViolations = new Dictionary<string, StepDefect>(adopted.FloorViolations);
        }

        /// <summary>
        /// 분할 생성 캐시를 통째로 무효화한다.
        ///
        /// 호출 자리가 둘이다: (1) 목차 재수립 직후 — 목차가 바뀌면 단계 코드도
        /// 바뀐다. (2) 골격 재시도가 실패해 단일 호출로 폴백할 때 — 이번 회차의
        /// 문서가 분할 문서와 완전히 다른 구조가 된다. 두 경우 모두 골격·섹션·
        /// 지목 목록·하한 위반 기록 중 하나라도 남겨두면, 이번 회차나 나중 회차의
        /// 지목 재생성이 더 이상 유효하지 않은 옛 단계 코드·섹션을 계속 실어
        /// 나른다. stepFloorViolations만 지우고 lastSkeleton/lastStepSections를
        /// 남겨두면, 그 위반 기록이 사라진 뒤에도 캐시된 섹션 자체는 살아남아
        /// 나중 회차의 지목 재생성이 하한 미달 섹션을 위반 기록 없이(=배너 없이)
        /// 그대로 재조립할 수 있다 — 실제로 그런 실수가 있었다.
        /// </summary>
        private static void ClearSplitGenerationCacheAfterRedraft(
            out string? lastSkeleton,
            out AiResult? lastSkeletonResult,
            out Dictionary<string, string>? lastStepSections,
            out IReadOnlyList<BatchStepPlan>? currentSteps,
            out Dictionary<string, StepDefect> stepFloorViolations,
            List<string> pendingDefectiveSteps)
        {
            lastSkeleton = null;
            lastSkeletonResult = null;
            lastStepSections = null;
            currentSteps = null;
            stepFloorViolations = new Dictionary<string, StepDefect>();
            pendingDefectiveSteps.Clear();
        }

        /// <summary>
        /// 단일 호출 폴백에서는 분할 SP 문서 단위 검사(<see cref="MechanicalValidator.
        /// ValidateSplitProcedureObligations"/>, Task 5)를 돌리지 않는다 - 그 검사는
        /// 단계 코드 → 본문 사전(sections)을 요구하는데 이 경로에는 그런 사전이
        /// 애초에 없다. 문서 전체에서 "어디든 등장하면 통과"로 약화시킬 수는
        /// 있으나, 그러면 무관한 단계에 적힌 코드로도 통과해 버린다.
        ///
        /// 실행하지 못했다는 사실을 로그로 남기지 않으면 "검사해서 깨끗함"과
        /// 구별되지 않는다 - 검사를 안 돌린 것과 돌려서 결함이 없었던 것은 다른
        /// 사실이다. 호출 자리가 둘(최초 생성 폴백·L3 피드백 재생성 폴백)이라
        /// 인라인 로그 대신 헬퍼로 묶는다 - 그래야 세 번째 폴백이 생겨도 이
        /// 사실이 빠지지 않는다(리뷰에서 실제로 한 자리가 빠졌던 적이 있다).
        /// </summary>
        private static void LogSplitProcedureObligationSkipped(string jobName)
        {
            Log.Information(
                "단일 호출 경로라 분할 SP 문서 단위 검사를 실행하지 않았습니다 - Job: {JobName}", jobName);
        }

        /// <summary>
        /// 목차의 스텝이 원본 명세서 전부를 커버하는지 검사해, 어느 스텝의
        /// LegacyProcedures에도 등장하지 않는 명세서를 돌려준다.
        ///
        /// 이 검사가 필요한 이유: 분할 생성의 전체 계약이 목차의 스텝 목록에
        /// 기대고 있다. 12개 프로시저에 목차가 3개 스텝만 냈다면, 분할은 3개의
        /// 통통하고 하한을 통과하는 섹션을 만들고 문서는 Passed로 끝나지만
        /// 나머지 9개는 최종 문서 어디에도 없다 — 아무 신호도 없이.
        ///
        /// 비교는 "맨 이름"(스키마·DB 접두사를 뗀 이름, 대소문자 무시) 기준이다.
        /// MechanicalValidator.BareObjectName이 그 규칙을 이미 갖고 있어 그대로
        /// 재사용한다 — 별도로 다시 구현하면 두 로직이 미묘하게 갈라질 수 있다.
        ///
        /// FileName에서 확장자를 따로 떼지 않는다. 이 메서드의 두 호출부
        /// (Program.cs의 --batch 경로와 TUI 경로)는 모두 확장자 없는 "스키마.이름"
        /// 식별자(OutputPathResolver가 쓰는 것과 같은 형태, 예: "dbo.UP_UTIL_SETTLE_INS")
        /// 를 넘긴다 — 코드 리뷰에서 실측된 결함(두 호출부 모두 모든 명세서에
        /// "docs/Spec.md"라는 같은 파일명 또는 파일명의 마지막 경로 세그먼트를
        /// 넘겨 N개 명세서가 전부 한 항목으로 뭉개졌다)의 수정이다. 앞으로 확장자
        /// 있는 FileName을 넘기는 호출부가 생기면 이 메서드에 확장자 제거 단계를
        /// 다시 추가하십시오 — BareObjectName은 스키마 접두사(마지막 '.' 앞)만
        /// 떼도록 설계됐으므로 "dbo.UP_X.sql"에 그대로 적용하면 "sql"을
        /// 프로시저명으로 착각한다.
        /// </summary>
        /// <summary>
        /// 명세서에서 뽑은 두 대조 재료를 프로시저별로 합친다. 조건 컬럼과 반올림 모양은
        /// 추출기가 다르지만 같은 검사가 함께 받아야 하고, 한쪽에만 있는 프로시저도
        /// 있으므로(조건은 있는데 반올림은 없는 경우) 양쪽 키를 모두 살린다.
        /// </summary>
        private static IReadOnlyDictionary<string, SpecConditions> MergeSpecMaterials(
            IReadOnlyDictionary<string, SpecConditions> conditions,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> roundingShapes)
        {
            var merged = new Dictionary<string, SpecConditions>(conditions, StringComparer.OrdinalIgnoreCase);

            foreach (var pair in roundingShapes)
            {
                merged[pair.Key] = merged.TryGetValue(pair.Key, out var existing)
                    ? existing with { RoundingShapes = pair.Value }
                    : new SpecConditions(
                        Array.Empty<string>(),
                        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                        pair.Value);
            }

            return merged;
        }

        private static IReadOnlyList<string> FindUncoveredProcedures(
            IReadOnlyList<BatchStepPlan> steps,
            List<(string FileName, string Content)> specs)
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in steps)
            {
                foreach (var legacyProcedure in step.LegacyProcedures)
                {
                    var bareName = MechanicalValidator.BareObjectName(legacyProcedure);
                    if (bareName.Length > 0)
                    {
                        covered.Add(bareName);
                    }
                }
            }

            var uncovered = new List<string>();
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in specs)
            {
                var bareName = MechanicalValidator.BareObjectName(spec.FileName);
                if (bareName.Length == 0 || covered.Contains(bareName) || !reported.Add(bareName))
                {
                    continue;
                }

                uncovered.Add(spec.FileName);
            }

            return uncovered;
        }

        /// <summary>
        /// 단계 하나의 생성 결과. 병렬 실행 중에는 공유 컬렉션을 만지지 않고 이
        /// 레코드로 돌려주며, 병합은 Task.WhenAll 이후 단일 스레드에서 한다.
        /// </summary>
        private sealed record StepSectionResult(string Code, string Markdown, StepDefect? FloorViolation);

        /// <summary>
        /// 골격 1회 + 단계 N회로 계획서를 만든다.
        ///
        /// 이 경로가 존재하는 이유: 단일 호출은 모델이 하나의 출력 예산 안에서
        /// 앞 단계에 66%를 쓰고 뒤를 굶겼다(실측). 단계마다 독립 호출이면 그
        /// 경쟁 자체가 사라진다.
        ///
        /// 이전 섹션 캐시가 남아 있으면 defectiveSteps로 지목된 단계만 다시 뽑고
        /// 나머지는 캐시된 바이트를 그대로 쓴다. 이전 골격이 남아 있으면 골격
        /// 호출도 건너뛴다 - 두 판단은 독립이다(reuseSkeleton/canTargetSections
        /// 참고, §3-6). previousSkeleton이 없는데 previousSections는 있는 경우
        /// (SkeletonDefective로 골격만 지운 경우)가 그 독립성이 필요한 이유다.
        ///
        /// 골격을 얻지 못하면 null을 돌려주고 호출부가 단일 호출로 폴백한다.
        ///
        /// 골격을 재사용할 때(reuseSkeleton)는 골격 호출을 건너뛰므로 그 자신의
        /// AiResult가 없다 — previousSkeletonResult로 넘겨받은, 골격을 실제로
        /// 생성했던 회차의 AiResult를 그대로 재사용한다. 빈 스텁을 새로 만들지
        /// 않는 이유: AGENTS.md가 문서화한 계약대로 raw/prompt-context.md와
        /// docs/Thinking.md는 채택된 시도가 실제로 무엇을 프롬프트/사고했는지를
        /// 서술해야 하고, SystemPrompt·UserPrompt·ThinkingText가 전부 null인
        /// 스텁은 그 계약을 어긴다.
        /// </summary>
        private async Task<SplitGeneration?> GenerateBySplitAsync(
            string planStructure,
            IReadOnlyList<BatchStepPlan> steps,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            IMultiProgressScope progressScope,
            string? previousSkeleton,
            AiResult? previousSkeletonResult,
            Dictionary<string, string>? previousSections,
            Dictionary<string, StepDefect> previousViolations,
            IReadOnlyList<string> defectiveSteps,
            IReadOnlyList<string> knownTableNames,
            IReadOnlyDictionary<string, IReadOnlyList<string>> parametersByProcedure,
            string? brainstorming,
            // [분할 SP 귀속 배선] 분할 SP의 코드·테이블을 단계마다 요구하지 않으려면
            // 프로시저 단위 귀속 재료가 GenerateStepSectionWithFloorRetryAsync까지
            // 내려가야 한다 - step.ErrorCodes는 평평한 목록이라 이 메서드가 직접
            // 만들 수 없다.
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure,
            CancellationToken cancellationToken)
        {
            // 골격 재사용 여부와 "지목 단계만 재생성" 여부는 독립이다. 예전에는
            // 하나의 targeted 판정으로 묶여 있어서, previousSkeleton이 없으면(예:
            // SkeletonDefective로 골격만 지운 경우) 지목과 무관하게 전 단계가
            // 다시 만들어졌다 - "골격만 다시 만든다"는 상태 메시지가 거짓이 되는
            // 결함이었다(§3-6).
            //
            // reuseSkeleton: 골격 호출 자체를 건너뛸지. previousSkeleton이 있어야만
            // 가능하다.
            var reuseSkeleton = previousSkeleton != null && previousSkeletonResult != null;
            // canTargetSections: 섹션을 통째로 다시 만들지, 지목된 것만 만들지.
            // 재사용할 섹션 캐시(previousSections)가 있어야만 가능하다 - 캐시가
            // 없으면(첫 생성이거나 재설계 직후 캐시가 통째로 지워진 경우) 지목이
            // 있어도 비교할 대상이 없어 전부 새로 만든다.
            var canTargetSections = previousSections != null;

            // 단계별 인터페이스. steps가 이미 확정돼 있으므로 골격 호출보다 앞에서 만든다 -
            // 골격도 이 표를 받아야 한다(규칙 5가 그 표를 가리킨다). 재시도 루프 밖이라
            // 단계마다 뽑아도 결과가 같다.
            var stepInterfaces = StepInterfaceFacts.Build(steps, parametersByProcedure);

            string skeleton;
            AiResult generation;

            if (reuseSkeleton)
            {
                skeleton = previousSkeleton!;
                generation = previousSkeletonResult!;
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
                            steps, planStructure, specs, targetLanguage, jobName, _consolidatorEffort, brainstorming, stepInterfaces, cancellationToken),
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
            var floorViolations = new Dictionary<string, StepDefect>(previousViolations);

            // 섹션 캐시가 있으면 지목된 단계만, 없으면 전부 만든다. 골격을 다시
            // 만들었는지는 이 판단과 무관하다(위 canTargetSections 주석 참고).
            // 지목 코드가 목록에 없으면(모델이 지어낸 코드) 무시한다.
            var pending = canTargetSections
                ? steps.Where(step => defectiveSteps.Contains(step.Code, StringComparer.OrdinalIgnoreCase)).ToList()
                : steps.ToList();

            foreach (var step in pending)
            {
                floorViolations.Remove(step.Code);
            }

            // 동시 실행 수 제한. Dispose하지 않는다 — SemaphoreSlim이 Dispose로 놓는
            // 자원은 지연 할당되는 AvailableWaitHandle뿐이고 이 코드는 그것을 쓰지
            // 않으므로, 놓을 것이 애초에 없다.
            //
            // (Dispose가 위험해서가 아니다. 아래 Task.WhenAll은 넘긴 태스크가 전부
            // 끝난 뒤에야 반환하거나 던지므로 — 조기 이탈 경로가 없다 — 그 시점에
            // Release를 호출할 태스크는 남아 있지 않다. 할 일이 없는 using은 그것이
            // 필요하다는 인상만 남긴다.)
            var gate = new SemaphoreSlim(_stepConcurrency);

            async Task<StepSectionResult> RunStepAsync(BatchStepPlan step, int index)
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    // 진행률 행은 슬롯을 잡은 뒤에 추가한다. 먼저 추가하면 대기 중인
                    // 단계까지 전부 "생성 중"으로 떠서, 실제로는 넷만 돌고 있다는
                    // 사실이 화면에서 사라진다.
                    var taskKey = $"step_{step.Code}";
                    progressScope.AddTask(taskKey, $"3/3. 단계 본문 생성 중 ({step.Code} · {index + 1}/{pending.Count})...");

                    var (markdown, violation) = await GenerateStepSectionWithFloorRetryAsync(
                        step, steps, conventions, specs, targetLanguage, jobName,
                        knownTableNames, stepInterfaces, codesByProcedure, tablesByProcedure, cancellationToken);

                    progressScope.CompleteTask(taskKey);
                    return new StepSectionResult(step.Code, markdown, violation);
                }
                finally
                {
                    // 슬롯은 단계당 재시도 2회를 모두 감싼 채 유지했다가 여기서 놓는다.
                    // 재시도 사이에 놓으면 다른 단계가 끼어들어 동시 요청 수가 설정값을 넘는다.
                    gate.Release();
                }
            }

            // 워밍: 첫 단계를 끝까지 기다린 뒤에야 나머지를 띄운다. 프롬프트 접두사
            // 캐시는 요청이 "완료돼야" 채워지므로, N개를 동시에 쏘면 N개 전부 미스다.
            // 13단계·동시 4 기준으로 워밍이 있든 없든 4라운드로 같지만, 미스는
            // 4회에서 1회로 준다 — 벽시계를 쓰지 않고 얻는 이득이다.
            //
            // 이 await가 워밍의 유일한 보증이다. 세마포어가 아니다 — 슬롯이 여러
            // 개여도 두 번째 호출은 여기서 시작조차 하지 않는다. 지우지 말 것.
            var stepResults = new List<StepSectionResult>(pending.Count);
            if (pending.Count > 0)
            {
                stepResults.Add(await RunStepAsync(pending[0], 0));
            }

            if (pending.Count > 1)
            {
                var rest = pending.Skip(1).Select((step, offset) => RunStepAsync(step, offset + 1)).ToList();
                stepResults.AddRange(await Task.WhenAll(rest));
            }

            // 병합은 단일 스레드에서 목록 순서대로. Task.WhenAll은 완료 순서가 아니라
            // 넘긴 순서로 결과를 돌려주므로, 사전에 들어가는 순서가 결정적이다.
            foreach (var stepResult in stepResults)
            {
                sections[stepResult.Code] = stepResult.Markdown;
                if (stepResult.FloorViolation != null)
                {
                    floorViolations[stepResult.Code] = stepResult.FloorViolation;
                }
            }

            // 분할된 SP의 의무는 단계가 아니라 문서가 진다. 단계 검사에서 뺀 것을
            // 여기서 합쳐 본다 - 여기가 sections와 steps를 함께 가진 유일한 지점이다.
            //
            // [최종 리뷰 픽스] 예전에는 이 대입이 위 루프에서 이미 다른 사유로 얻은
            // StepDefect를 무조건 덮어썼다 - 그 사유가 배너·회차 파일에 남는 유일한
            // 문구인데도. MechanicalValidator.ValidateSplitProcedureObligations 자신은
            // 같은 코드가 두 분할 SP에 걸릴 때 `already with { Reason = ... }`로 이어
            // 붙여 보존하는데(§617 이하), 그 대칭을 여기서는 지키지 않았다 - 아래
            // MergeFloorViolation이 그 대칭을 맞춘다.
            foreach (var (code, defect) in _validator.ValidateSplitProcedureObligations(
                         sections, steps, codesByProcedure, tablesByProcedure))
            {
                floorViolations[code] = floorViolations.TryGetValue(code, out var prior)
                    ? MergeFloorViolation(prior, defect)
                    : defect;
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
        /// 한 단계에 대해 이미 있던 StepDefect(prior)와 문서 단위 분할 SP 검사가 새로
        /// 낸 StepDefect(defect)를 합친다. 사유는 항상 이어 붙인다 - 어느 쪽도 지우지
        /// 않는다.
        ///
        /// [Kind는 왜 항상 defect를 따르지 않고 "더 심각한 쪽"을 고르는가]
        /// 배너 조립부(§2586 이하 주석)가 이미 세 Kind의 심각도 순서를 정해 뒀다 -
        /// 나중에 붙는 배너일수록 문서 위로 얹혀 먼저 읽히므로, 최종 순서(위→아래)는
        /// GenerationFailed(생성 실패) → QualityFloor(하한 미달) → Unverifiable(검증
        /// 불가)다. 이 메서드가 받는 defect는 <see cref="MechanicalValidator.ValidateSplitProcedureObligations"/>가
        /// 낸 것이라 Kind가 항상 QualityFloor다.
        ///
        /// - prior가 Unverifiable(단계 자신은 대조할 재료가 없어 검사를 못 돌림)이고
        ///   defect가 QualityFloor(문서 단위로는 실제 결함을 확인함)이면, 후자가 더
        ///   확실하고 더 심각한 진단이므로 QualityFloor로 승격한다 - "검증 불가"
        ///   배너 아래에 실려 덜 읽히게 두면 안 된다.
        /// - prior가 GenerationFailed(본문 자체가 없음)이면 그대로 유지한다 - 본문이
        ///   아예 없다는 사실이 "합쳐도 코드가 없다"는 사실보다 더 심각하고, 이미
        ///   GenerationFailed가 QualityFloor보다 위에 얹히도록 정해져 있다.
        /// - prior가 QualityFloor면 Kind는 바뀌지 않는다(둘 다 QualityFloor).
        ///
        /// 이 판단은 리뷰가 제시한 "prior의 Kind를 그대로 유지"안과 다르다 - 그 안은
        /// Unverifiable+QualityFloor 조합에서 실제로 확인된 결함을 "검증 불가"
        /// 배너 밑에 묻어 버린다. 근거와 판단은 이 메서드에 국한된 것이라, 여기
        /// 말고 다른 병합 지점(예: MechanicalValidator 내부의 같은 문서 검사 안에서의
        /// 병합, §617 이하)에는 영향이 없다 - 그곳은 항상 Kind가 QualityFloor끼리라
        /// 이 판단 자체가 필요 없다.
        ///
        /// [최종 리뷰 재검증 — 파급은 배너만이 아니다] 이 Kind는
        /// <see cref="VerificationCoverage.From"/>이 "검증됨" 단계 수를 세는
        /// 유일한 재료이기도 하다(`StepsVerified`가 Unverifiable·GenerationFailed만
        /// 빼고 QualityFloor는 검증됨에 포함시킨다 - 그 클래스 docstring 참고).
        /// GenerationFailed+QualityFloor 조합에서 GenerationFailed를 지키는
        /// 판단은 배너 순서뿐 아니라 이 집계에도 영향을 준다: 예전의 무조건
        /// 덮어쓰기 버그 아래서는 Kind가 QualityFloor로 강등돼 본문이 아예
        /// 생성된 적 없는 단계가 `StepsVerified`에 포함되고 `HasUnverifiedSteps`가
        /// 그 단계를 놓쳤다 - "단계 검증: N/N" 아래에 "이 단계는 생성에
        /// 실패했습니다"라고 적힌 섹션이 숨는, `StepDefectKind`를 애초에 가르게
        /// 만든 바로 그 사고(위 클래스 docstring, `StepDefect.cs`의 `GenerationFailed`
        /// 주석)가 되살아난다. 지금 판단은 그 사고를 막는다 - 문서 단위 검사가
        /// 같은 코드에 또 결함을 얹었다는 사실이 "본문이 생성됐다"는 사실을
        /// 만들어 내지 않기 때문이다. `VerificationCoverageTests.
        /// MergeFloorViolation_GenerationFailedPlusQualityFloor_StaysExcludedFromVerifiedCount`가
        /// 이 결합(이 메서드의 반환값 → `VerificationCoverage.From`의 집계)을
        /// 리플렉션으로 직접 고정한다.
        /// </summary>
        private static StepDefect MergeFloorViolation(StepDefect prior, StepDefect defect)
        {
            var reason = prior.Reason + " " + defect.Reason;
            var kind = FloorViolationSeverity(prior.Kind) >= FloorViolationSeverity(defect.Kind)
                ? prior.Kind
                : defect.Kind;
            return new StepDefect(kind, reason);
        }

        /// <summary>
        /// §2586 이하의 배너 조립 순서(위로 갈수록 심각)를 그대로 숫자로 옮긴 것.
        /// 이 숫자 자체에는 의미가 없다 - 오직 <see cref="MergeFloorViolation"/>의
        /// 비교에만 쓰인다.
        /// </summary>
        private static int FloorViolationSeverity(StepDefectKind kind) => kind switch
        {
            StepDefectKind.GenerationFailed => 2,
            StepDefectKind.QualityFloor => 1,
            StepDefectKind.Unverifiable => 0,
            _ => 0,
        };

        /// <summary>
        /// 단계 섹션 하나를 만들고 하한을 검사한다. 미달이면 그 단계만 재시도한다 —
        /// 단계당 최대 5회(최초 1회 + 재시도 4회). 2에서 5로 올린 이유: 축 B 감사로
        /// 검사가 5개 늘어(문장 개수·조인 키·추가 술어·지역 변수·상태 변수 초기값),
        /// 2회로는 첫 시도에서 2건 이상 걸린 단계가 재시도 1회 안에 다 못 고쳐 하한
        /// 미달로 확정된다. 축 A(통합 문서 하한 검사)는 6회다.
        ///
        /// 이 재시도는 MaxL2Attempts를 소모하지 않는다. 그 예산은 Actor-Critic 문서
        /// 레벨의 것이고, 이 보수는 리뷰 호출이 0인 국소 작업이라 성격이 다르다.
        /// 대신 이 예산은 MaxL2Attempts와 완전히 독립이고, 이 메서드 자체가 문서
        /// 레벨 시도마다(즉 MaxL2Attempts번) 처음부터 다시 돈다 — 그래서 최악 비용은
        /// 단일 5회가 아니라 "단계 수 × 5 × 문서 레벨 시도 수"다. 그럼에도 별도의
        /// 총량 상한(예: 잡 전체 호출 수 캡)은 두지 않기로 했다 — 실측상 대부분의
        /// 단계가 1~2회 안에 통과하고, 6회를 다 채운 사례는 지금까지 PROC_ETC
        /// 하나뿐이었다. 다음에 이 자리를 읽을 때 "상한이 빠졌다"가 아니라
        /// "상한을 두지 않기로 결정했다"로 읽어야 한다.
        ///
        /// 재시도 후에도 미달이면 채택하고 기록만 한다. 여기서 문서 L1을 실패시키면
        /// 같은 결함으로 골격+단계 전체 재생성을 유발해 비용만 태운다.
        ///
        /// 위반 기록을 바깥 사전에 쓰지 않고 돌려주는 이유: 이 메서드는 여러 단계에
        /// 대해 동시에 돈다. 공유 사전에 쓰면 잠금이 필요하고, 잠금이 있어도 기록이
        /// 들어가는 순서는 완료 순서를 따라 비결정적이 된다. 호출부가 Task.WhenAll
        /// 이후 단일 스레드에서 목록 순서대로 병합한다.
        /// </summary>
        private async Task<(string Markdown, StepDefect? Defect)> GenerateStepSectionWithFloorRetryAsync(
            BatchStepPlan step,
            IReadOnlyList<BatchStepPlan> steps,
            string conventions,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            IReadOnlyList<string> knownTableNames,
            IReadOnlyList<StepInterface> stepInterfaces,
            // [분할 SP 귀속 배선] GenerateBySplitAsync가 받은 귀속 재료를 그대로 내려받아
            // ValidateBatchStep에 넘긴다.
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure,
            CancellationToken cancellationToken)
        {
            const int maxTries = 5;   // 최초 1회 + 재시도 4회 - 근거는 위 docstring 참고

            // 원본이 무엇으로 거르고 어떤 순서로 반올림하는지는 명세서에만 있다.
            // 단계마다 뽑아도 결과가 같으므로 재시도 루프 밖에서 한 번만 만든다.
            //
            // 피드백 항목을 걷어내고 뽑는다 - 재시도 회차의 specs에는 Feedback_Log.txt가
            // 섞여 있는데 그것은 명세서가 아니다. 걷어내지 않으면 지적문이 인용한 SQL이
            // 존재하지 않는 프로시저(BareObjectName이 "txt"로 읽는다)의 재료로 등록된다.
            var procedureSpecs = FeedbackSpec.OnlyProcedureSpecs(specs);
            var conditionColumns = MergeSpecMaterials(
                SpecConditionColumnExtractor.Extract(procedureSpecs),
                SpecRoundingShapeExtractor.Extract(procedureSpecs));

            // 명세서가 문장 단위로 확정한 사실(DML 범위 표·갱신 절·지역 변수 표). 조건 컬럼과
            // 같은 이유로 재시도 루프 밖에서 한 번만 만든다 - 단계마다 뽑아도 결과가 같다.
            var statementFacts = SpecStatementFactsExtractor.Extract(procedureSpecs);

            // 실행 행을 만들 책임이 이 단계에 있는가. 단계 검사는 단계 하나만 보므로
            // 스스로 알 수 없고, 목록 전체를 가진 여기가 판정해 넘긴다. 이 배선이
            // 없으면 그 계약은 문서 전체를 보는 통합 검사에만 남는데, 통합 검사는
            // 어느 단계가 고쳐야 하는지 지목하지 못해 요구가 재생성 프롬프트에
            // 실리지 않는다 - 실측에서 자가 수정 3회가 전부 같은 오류로 끝났다.
            BatchControlContract.ResolveRowCreators(steps).TryGetValue(step.Code, out var runRowOwnedTables);

            string? adopted = null;
            string? floorFeedback = null;
            // 직전 시도가 예외로 끝났는가. 하한 미달과 구분한다 — 지연이 필요한 것은
            // rate limit 쪽뿐이다.
            bool previousTryThrew = false;

            for (int tries = 0; tries < maxTries; tries++)
            {
                if (previousTryThrew)
                {
                    // 동시 실행 중에는 429가 여러 단계를 같은 창에서 때린다. 무지연으로
                    // 재시도하면 네 단계가 두 번의 시도를 모두 그 창 안에 쏟아붓고 함께
                    // 강등된다. 무작위 지연이 상관된 폭풍을 흩트러진 재시도로 바꾼다.
                    // 13분짜리 구간에서 1초는 무시할 만하다.
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(Random.Shared.Next(500, 1500)), cancellationToken);
                }

                previousTryThrew = false;
                string? content = null;
                try
                {
                    // 원본 인터페이스 표. GenerateBySplitAsync가 steps 확정 시점에
                    // StepInterfaceFacts.Build로 한 번 만들어 여기까지 그대로 넘긴다.
                    var result = await _consolidatorService.GenerateBatchStepSectionAsync(
                        step, steps, conventions, specs, stepInterfaces, targetLanguage, jobName,
                        _consolidatorEffort, floorFeedback, cancellationToken);
                    content = result?.Content;
                }
                // 취소를 삼키면 실패로 위장한 정상 반환이 되어 취소 사실이 사라진다.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    previousTryThrew = true;
                    _userInteraction.NotifyError($"{jobName} - {step.Code} 단계 섹션 생성 실패: {ex.Message}");
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    floorFeedback = null;
                    continue;
                }

                adopted = content;

                var stepResult = _validator.ValidateBatchStep(
                    content, step, knownTableNames, conditionColumns,
                    stepInterfaces: stepInterfaces,
                    runRowOwnedTables: runRowOwnedTables,
                    statementFactsByProcedure: statementFacts,
                    // [Task 18 I2 배선] steps는 이 메서드의 매개변수로, 이 Job의 단계
                    // 목록 전체다(이 단계 자신 포함) - ValidateBatchStep이 "같은 레거시
                    // SP가 다른 단계에도 나뉘어 있는가"를 판정하는 데 그대로 쓴다. 이
                    // 인자가 빠지면 그 판정을 못 해 분할된 SP를 담당하는 모든 단계가
                    // 영구히 만족 불가능한 개수 요구를 받는다(위 클래스 docstring 및
                    // MechanicalValidator.IsLegacyProcedureSplitAcrossSteps 참고).
                    allSteps: steps,
                    // [분할 SP 귀속 배선] 분할된 SP에서만 유래한 코드·테이블은 이 단계
                    // 하나가 전량을 요구받지 않는다 - 문서 단위 검사(Task 5)가 그 의무를
                    // 회수한다.
                    codesByProcedure: codesByProcedure,
                    tablesByProcedure: tablesByProcedure);
                if (stepResult.IsValid)
                {
                    return (content, null);
                }

                // 목차가 대조할 재료를 내지 않은 경우다. 본문을 다시 써도 프롬프트에
                // 넣을 코드·테이블이 여전히 없어 같은 자리로 돌아오므로, 재시도로
                // 호출을 버리지 않고 기록만 남긴다. 이 기록이 배너가 되어 "검사가
                // 실행되지 않았다"는 사실이 문서와 회차 파일에 남는다.
                if (!stepResult.RegenerationCanFix)
                {
                    var reason = string.Join(" / ", stepResult.Errors);
                    _userInteraction.NotifyStatus(
                        $"  [yellow]* {step.Code} 단계는 목차 결함으로 하한 검사를 실행할 수 없습니다 - 재생성으로 고쳐지지 않아 건너뜁니다: {reason}[/]");
                    Log.Warning(
                        "단계 하한 검사를 실행하지 못했습니다 - Step: {StepCode}, 사유: {Reason}", step.Code, reason);
                    return (content, new StepDefect(StepDefectKind.Unverifiable, $"{step.Code} ({reason})"));
                }

                _userInteraction.NotifyStatus(
                    $"  [grey]* {step.Code} 단계가 하한 검사를 통과하지 못해 다시 생성합니다: {string.Join(" / ", stepResult.Errors)}[/]");
                floorFeedback = stepResult.SuggestedPromptFix;
            }

            if (adopted == null)
            {
                return ($"### {step.Code} {step.Name}\n\n> [!WARNING]\n> 이 단계는 생성에 실패했습니다. 원본 프로시저를 직접 확인하십시오.\n",
                    new StepDefect(StepDefectKind.GenerationFailed, $"{step.Code} (생성 실패)"));
            }

            return (adopted, new StepDefect(StepDefectKind.QualityFloor, $"{step.Code} (하한 미달)"));
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
        /// Critic의 HasDefects 자기 신고를 5축 점수로 덮어쓴다. 두 루프가 같은 규칙을 쓰도록
        /// 한 자리에 둔다 - 통합 계획서 루프에는 이 검사 자체가 없어서 낮은 점수와 함께
        /// "통과"가 찍혔다.
        /// </summary>
        private void EnforceScoreThreshold(ReviewResult review, string target, int attempt)
        {
            var failedAxes = CriticScoreGate.FailedAxes(review, _criticScoreThreshold);
            if (failedAxes.Count == 0)
            {
                return;
            }

            if (!review.HasDefects)
            {
                // 모델이 "결함 없음"이라고 했는데 점수가 기준에 못 미친 경우다.
                // 조용히 덮어쓰면 나중에 왜 재시도가 돌았는지 알 수 없다.
                Log.Warning(
                    "[파이프라인] Critic이 결함 없음으로 신고했으나 기준({Threshold}) 미만 축이 있어 결함으로 덮어씁니다 - " +
                    "대상: {Target}, 시도: {Attempt}, 미달 축: {FailedAxes}",
                    _criticScoreThreshold, target, attempt, string.Join(", ", failedAxes));

                // 이 결함은 모델의 자기 신고가 아니라 축 게이트가 강제한 것이다 -
                // 문서 어느 한 자리를 지목할 수 없는 게 정상이므로, 지목 없는
                // 리뷰의 재호출 상한(§3-2(a))을 태우면 안 된다.
                review.AxisThresholdForced = true;
            }

            review.HasDefects = true;
        }

        /// <summary>
        /// 구제(RetryRescue)로 이전 회차 문서를 채택할 때, 그 문서를 만든 목차를 다시
        /// 현행으로 되돌린다. 재수립 이후 회차가 더 낮은 점수를 내면 산출물은 재수립
        /// 이전 목차에서 나온 것인데 PlanStructure.md에는 새 목차가 남아 있어, 파일이
        /// 어떤 산출물도 만든 적 없는 목차를 가리키게 된다.
        ///
        /// 되돌린 뒤 실제로 쓰이는 목차를 돌려준다. 이어지는 L3 재생성도 이 목차를
        /// 써야 화면의 문서와 기록이 계속 일치한다.
        ///
        /// 호출자는 다섯이다. 넷(AI 생성 실패·L1 소진·L2 소진·L2 리뷰 호출 실패)은
        /// 모두 "예산이 다 떨어진 뒤" 회차당 최대 한 번만 돈다. 나머지 하나는 후보
        /// 등록 블록의 회귀 롤백이다 - 계기가 예산 소진이 아니라 "이번 회차가
        /// 최고점을 갱신하지 못함"이라 훨씬 자주 돈다(회귀가 이어지면 회차마다 한
        /// 번씩). 이 메서드의 부작용(파일 쓰기·superseded 처리)도 그만큼 자주
        /// 반복될 수 있다는 뜻이다 - 여기에 비싸거나 되돌릴 수 없는 부작용을 더할
        /// 때는 그 빈도를 먼저 가정해야 한다.
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
