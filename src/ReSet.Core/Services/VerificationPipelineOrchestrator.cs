using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
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
            string? consolidatorEffort = null)
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

            _maxAttempts = _maxL2Attempts == -1 ? -1 : 1 + _maxL2Attempts;
        }

        public async Task<(string? SpecMarkdown, SpDefinition? SpDef, ReviewResult? Review, string? ThinkingText)> RunPipelineAsync(
            string connectionString,
            string schema,
            string name,
            int maxDepth,
            string provider,
            string instructions,
            bool isBatchMode,
            string outputDirectory = "./output",
            bool enableCache = false,
            CancellationToken cancellationToken = default)
        {
            var selectedOption = $"{schema}.{name}";
            SpDefinition? spDef = null;
            ReviewResult? finalReview = null;
            var accumulatedThinking = new StringBuilder();

            Log.Information("[파이프라인] SP 분석 시작 - SP: {SpName}, Provider: {Provider}, MaxDepth: {MaxDepth}, BatchMode: {IsBatchMode}",
                selectedOption, provider, maxDepth, isBatchMode);

            _userInteraction.NotifyStatus($"[yellow]{selectedOption}[/] - DB 메타데이터 및 의존성 분석 중 (최대 깊이: {maxDepth}단계)...");
            try
            {
                spDef = await _dbService.GetSpDetailsAsync(connectionString, schema, name, maxDepth, cancellationToken);
                Log.Debug("[파이프라인] DB 메타데이터 수집 완료 - SP: {SpName}, 의존성 수: {DepCount}, 경고 수: {WarningCount}",
                    selectedOption, spDef?.Dependencies?.Count ?? 0, spDef?.Warnings?.Count ?? 0);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[파이프라인] DB 메타데이터 수집 실패 - SP: {SpName}", selectedOption);
                _userInteraction.NotifyError($"{selectedOption} - DB 조회 실패: {ex.Message}");
            }

            if (spDef == null)
            {
                Log.Warning("[파이프라인] SP 정의를 가져오지 못해 파이프라인을 중단합니다 - SP: {SpName}", selectedOption);
                return (null, null, null, null);
            }

            if (spDef.Warnings.Count > 0)
            {
                _userInteraction.NotifyWarnings(selectedOption, spDef.Warnings);
            }

            // 캐시 유효성 확인
            string? compositeHash = null;
            if (enableCache)
            {
                try
                {
                    compositeHash = _cacheManager.ComputeCompositeHash(spDef);
                    if (_cacheManager.IsCacheValid(selectedOption, compositeHash, outputDirectory))
                    {
                        Log.Information("[파이프라인] 캐시 히트 - AI 분석 건너뜀 - SP: {SpName}", selectedOption);
                        _userInteraction.NotifyStatus($"[green]{selectedOption}[/] - 캐시가 유효합니다. AI 분석을 건너뛰고 기존 보고서를 사용합니다. (Cache Hit)");
                        var specFilePath = System.IO.Path.Combine(outputDirectory, $"{selectedOption}_Spec.md");
                        if (System.IO.File.Exists(specFilePath))
                        {
                            var cachedSpec = await System.IO.File.ReadAllTextAsync(specFilePath, cancellationToken);
                            var mockReview = new ReviewResult
                            {
                                HasDefects = false,
                                ScoreAccuracy = 10,
                                ScoreCrud = 10,
                                ScoreInterface = 10,
                                ScoreException = 10,
                                ScoreReadability = 10
                            };
                            return (cachedSpec, spDef, mockReview, null);
                        }
                    }
                    else
                    {
                        Log.Debug("[파이프라인] 캐시 미스 - AI 분석 진행 - SP: {SpName}", selectedOption);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[파이프라인] 캐시 확인 중 예외 발생 (무시됨) - SP: {SpName}", selectedOption);
                    _userInteraction.NotifyStatus($"[yellow]경고: 캐시 확인 중 오류가 발생하여 무시하고 분석을 진행합니다. ({ex.Message})[/]");
                }
            }

            var feedbackHistory = new System.Collections.Generic.List<string>();
            string? feedbackLog = null;
            string specificationMarkdown = string.Empty;

            if (string.Equals(_actorEffort, "dynamic", StringComparison.OrdinalIgnoreCase))
            {
                string[] candidates;
                AiResult[]? candidatesResult = null;
                var actorInfo = $"Actor: {_aiService.ProviderName} - {_aiService.ModelName}(dynamic effort)";
                var criticInfo = $"Critic: {_criticService.ProviderName} - {_criticService.ModelName}({_criticEffort ?? "high"} effort)";
                _userInteraction.NotifyStatus($"[yellow]{selectedOption}[/] - 하이브리드 다중 후보군 병렬 생성 및 검토 중... ({actorInfo} / {criticInfo})");
                
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
                            accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
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
                            accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
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
                            accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
                        }
                        accumulatedThinking.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        _userInteraction.NotifyError($"{selectedOption} - 하이브리드 후보 생성 중 실패: {ex.Message}");
                        return (null, spDef, null, null);
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
                                    accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
                                }
                                accumulatedThinking.AppendLine();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _userInteraction.NotifyError($"{selectedOption} - Critic 검토 중 실패: {ex.Message}");
                        return (null, spDef, null, null);
                    }
                }

                if (reviews != null && reviews.Length >= 3 && reviews[0] != null && reviews[1] != null && reviews[2] != null)
                {
                    _userInteraction.NotifyStatus($"[green]{selectedOption}[/] - Effort별 Spec 검토 완료:");
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
                    _userInteraction.NotifyStatus($"[green]{selectedOption}[/] - 완벽한 후보군(후보 {bestCandidateIndex + 1}, AI 신뢰도: [bold green]{highestScore}[/]/100점)이 발견되어 즉시 채택합니다.{scoreSummary}");
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
                    sbConsolidation.AppendLine("당신은 제공된 여러 개의 Stored Procedure 분석 명세서 후보를 종합하여, 각 후보의 우수 영역을 취합하고 결점을 개선하여 단일한 완벽한 명세서로 합성(Consolidation)하는 전문 조립 아키텍트입니다.");
                    sbConsolidation.AppendLine();
                    sbConsolidation.AppendLine("[제공된 명세서 후보 목록 및 평가 점수]");
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        var rev = reviews![i];
                        if (rev == null) continue;
                        sbConsolidation.AppendLine($"--- [후보 {i+1}] ---");
                        sbConsolidation.AppendLine($"- 종합 평가 점수: {rev.NormalizedScore}점 / 100점 (50점 만점 기준 {rev.TotalScore}점)");
                        sbConsolidation.AppendLine($"  * 비즈니스 로직 및 제어 흐름 정합성 (ScoreAccuracy): {rev.ScoreAccuracy}/10점");
                        sbConsolidation.AppendLine($"  * 데이터 모델 및 CRUD 완전성 (ScoreCrud): {rev.ScoreCrud}/10점");
                        sbConsolidation.AppendLine($"  * 연동 인터페이스 구체성 (ScoreInterface): {rev.ScoreInterface}/10점");
                        sbConsolidation.AppendLine($"  * 예외 및 트랜잭션/격리성 정책 (ScoreException): {rev.ScoreException}/10점");
                        sbConsolidation.AppendLine($"  * 다이어그램 및 시각화 가독성 (ScoreReadability): {rev.ScoreReadability}/10점");
                        sbConsolidation.AppendLine($"- Critic 결함 피드백: {rev.FeedbackComment ?? "결함 없음"}");
                        sbConsolidation.AppendLine();
                        sbConsolidation.AppendLine("[본문 내용]");
                        sbConsolidation.AppendLine(candidates[i]);
                        sbConsolidation.AppendLine();
                    }
                    sbConsolidation.AppendLine();
                    sbConsolidation.AppendLine("[합성 및 병합 지침]");
                    sbConsolidation.AppendLine("1. 각 카테고리별 세부 평가 점수를 바탕으로, 해당 부문에서 가장 높은 점수(만점에 가까운 점수)를 받은 후보의 내용을 '진실의 원천(Source of Truth)'으로 채택하여 조립하십시오.");
                    sbConsolidation.AppendLine("   - 예: ScoreAccuracy(정합성)가 가장 높은 후보의 로직 설명을 바탕으로 삼고, ScoreReadability(다이어그램)가 가장 높은 후보의 Mermaid 다이어그램을 병합합니다.");
                    sbConsolidation.AppendLine("2. 각 후보에 지적된 Critic 결함 피드백(Critic Feedback) 내용을 명밀히 분석하여 최종 합성 명세서에서 완전히 수정 및 보완하십시오.");
                    sbConsolidation.AppendLine("3. 5대 필수 대분류 헤더 명칭(## 개요, ## 파라미터 목록, ## CRUD 분석, ## 로직 흐름 요약, ## 비즈니스 흐름 시각화)을 그대로 사용하여 문서를 구성하십시오.");
                    sbConsolidation.AppendLine("4. 최종 결과물만 다듬어 마크다운으로 직접 출력하십시오. 추가적인 사족이나 인사말은 절대 포함하지 마십시오.");

                    string scoreSummary = (reviews != null && reviews.Length >= 3 && reviews[0] != null && reviews[1] != null && reviews[2] != null) 
                        ? $" (Low: {reviews[0].NormalizedScore}점, Medium: {reviews[1].NormalizedScore}점, High: {reviews[2].NormalizedScore}점)"
                        : string.Empty;
                    _userInteraction.NotifyStatus($"[yellow]{selectedOption}[/] - 이종 모델 합성 에이전트(Consolidator) 구동 중 ({_consolidatorService.ProviderName} - {_consolidatorService.ModelName}, {_consolidatorEffort ?? "medium"} effort)...{scoreSummary}");
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
                            accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
                        }
                        accumulatedThinking.AppendLine();
                    }
                    catch (Exception ex)
                    {
                        _userInteraction.NotifyError($"{selectedOption} - 최종 합성 생성 실패: {ex.Message}");
                        return (null, spDef, null, null);
                    }

                    // 합성본 기계적 검증 (L1) 1회 수행
                    var finalL1 = _validator.Validate(specificationMarkdown);
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
                            specificationMarkdown = consolidatorSelfFixResult.Content;
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
                                accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
                            }
                            accumulatedThinking.AppendLine();
                        }
                        catch { }
                    }

                    _userInteraction.NotifyValidationSuccess(selectedOption);
                }
            }
            else
            {
                // 기존 단일 생성 루프
                int attempt = 1;
                while (true)
                {
                    var attemptText = attempt == 1 ? "1차 분석" : $"자가 수정 보완 ({attempt}회째)";
                    bool genSuccess = false;

                    Log.Information("[파이프라인] AI 명세서 생성 시작 - SP: {SpName}, 시도: {Attempt}, Provider: {Provider}, Model: {Model}",
                        selectedOption, attempt, provider, _modelName);
                    var effortText = !string.IsNullOrWhiteSpace(_actorEffort) ? $", Effort: {_actorEffort}" : "";
                    _userInteraction.NotifyStatus($"[yellow]{selectedOption}[/] - AI 리버스 엔지니어링 수행 중 ({_aiService.ProviderName} - {_aiService.ModelName}{effortText}) [[{attemptText}]]...");
                    try
                    {
                        AiResult aiResult;
                        using (var progressScope = _userInteraction.CreateProgressScope("명세서 생성") ?? NullProgressScope.Instance)
                        {
                            progressScope.AddTask("gen", $"{_aiService.ModelName} 분석 중...");
                            aiResult = await WrapWithProgress(_aiService.GenerateSpecificationAsync(spDef, instructions, feedbackLog, _actorEffort, cancellationToken), progressScope, "gen");
                        }
                        specificationMarkdown = aiResult.Content;
                        spDef.RawPromptContext = $"=== [System Prompt] ===\n{aiResult.SystemPrompt}\n\n=== [User Prompt] ===\n{aiResult.UserPrompt}";
                        genSuccess = true;
                        
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
                            accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
                        }
                        accumulatedThinking.AppendLine();

                        Log.Debug("[파이프라인] AI 명세서 생성 성공 - SP: {SpName}, 시도: {Attempt}, 응답 길이: {Length}자",
                            selectedOption, attempt, specificationMarkdown.Length);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[파이프라인] AI 명세서 생성 실패 - SP: {SpName}, 시도: {Attempt}", selectedOption, attempt);
                        _userInteraction.NotifyError($"{selectedOption} - AI 분석 실패 (시도 {attempt}): {ex.Message}");
                    }

                    if (!genSuccess || string.IsNullOrEmpty(specificationMarkdown))
                    {
                        return (null, spDef, null, null);
                    }

                    // L1: 기계적 무결성 검사
                    var l1Result = _validator.Validate(specificationMarkdown);
                    if (!l1Result.IsValid)
                    {
                        Log.Warning("[파이프라인] L1 기계 검증 실패 - SP: {SpName}, 시도: {Attempt}, 오류 수: {ErrorCount}",
                            selectedOption, attempt, l1Result.Errors?.Count ?? 0);
                        _userInteraction.NotifyL1Errors(selectedOption, attempt, _maxAttempts, l1Result.Errors ?? new System.Collections.Generic.List<string>());

                        bool canRetry = _maxAttempts == -1 || attempt < _maxAttempts;
                        if (canRetry)
                        {
                            feedbackLog = l1Result.SuggestedPromptFix;
                            attempt++;
                            continue;
                        }
                        else
                        {
                            Log.Error("[파이프라인] L1 기계 검증 최종 실패 - SP: {SpName}", selectedOption);
                            _userInteraction.NotifyError($"{selectedOption} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
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

                    Log.Information("[파이프라인] L2 AI 교차 리뷰 시작 - SP: {SpName}, 시도: {Attempt}", selectedOption, attempt);
                    var criticEffortText = !string.IsNullOrWhiteSpace(_criticEffort) ? $", Effort: {_criticEffort}" : "";
                    _userInteraction.NotifyStatus($"[yellow]{selectedOption}[/] - AI 교차 리뷰 분석 중 ({_criticService.ProviderName} - {_criticService.ModelName}{criticEffortText})...");
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
                            accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
                        }
                        accumulatedThinking.AppendLine();
                        Log.Debug("[파이프라인] L2 AI 교차 리뷰 완료 - SP: {SpName}, 결함 감지: {HasDefects}",
                            selectedOption, l2Result?.HasDefects);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[파이프라인] L2 AI 교차 리뷰 예외 - SP: {SpName}, 시도: {Attempt}", selectedOption, attempt);
                        _userInteraction.NotifyError($"{selectedOption} - AI 교차 리뷰 실패 (시도 {attempt}): {ex.Message}");
                    }

                    if (reviewSuccess && l2Result != null && l2Result.HasDefects)
                    {
                        Log.Warning("[파이프라인] L2 AI 교차 리뷰 결함 발견 - SP: {SpName}, 시도: {Attempt}", selectedOption, attempt);
                        _userInteraction.NotifyL2Defects(selectedOption, attempt, _maxAttempts, l2Result.FeedbackComment ?? string.Empty);

                        bool canRetry = _maxAttempts == -1 || attempt < _maxAttempts;
                        if (canRetry)
                        {
                            feedbackHistory.Add($"[시도 {attempt} L2 피드백]:\n{l2Result.FeedbackComment}");
                            feedbackLog = "[L2 AI 리뷰 피드백 히스토리]: 다음 누적 결함/누락사항이 지적되었습니다. 이전의 실수를 반복하지 말고 지적 사항을 전면 반영해서 수정해 주십시오.\n\n" + 
                                          string.Join("\n\n", feedbackHistory);
                            attempt++;
                            continue;
                        }
                        else
                        {
                            Log.Error("[파이프라인] L2 AI 교차 리뷰 최종 실패 - SP: {SpName}", selectedOption);
                            _userInteraction.NotifyError($"{selectedOption} - [[L2 AI 리뷰]] 최종 보완 실패. 마지막 리뷰 반영 버전을 사용합니다.");
                            break;
                        }
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

            // 배치 모드 성공 완료 시 캐시 업데이트
            if (isBatchMode && enableCache && !string.IsNullOrEmpty(compositeHash))
            {
                Log.Debug("[파이프라인] 배치 모드 캐시 업데이트 - SP: {SpName}", selectedOption);
                _cacheManager.UpdateCache(selectedOption, spDef, compositeHash, outputDirectory);
            }

            // DB 역반영 여부 선택과 관계없이 항상 파일로 스크립트 저장
            ExportMetadataCleansingSql(specificationMarkdown, selectedOption, outputDirectory);

            // L3: 인간 개입형 승인 (TUI 모드 한정)
            if (!isBatchMode)
            {
                while (true)
                {
                    var reviewResult = await _userInteraction.RequestHumanReviewAsync(selectedOption, specificationMarkdown);

                    if (reviewResult.Decision == UserDecision.Approve)
                    {
                        // 최종 승인 시 캐시 업데이트
                        if (enableCache && !string.IsNullOrEmpty(compositeHash))
                        {
                            _cacheManager.UpdateCache(selectedOption, spDef, compositeHash, outputDirectory);
                        }

                        // 사용자가 승인하면 DB 역반영 동기화 수행
                        var syncApproved = await _userInteraction.ConfirmMetadataSyncAsync(selectedOption);
                        if (syncApproved)
                        {
                            await ApplyMetadataCleansingSqlAsync(connectionString, selectedOption, outputDirectory, cancellationToken);
                        }

                        return (specificationMarkdown, spDef, finalReview, accumulatedThinking.ToString());
                    }
                    else if (reviewResult.Decision == UserDecision.Cancel)
                    {
                        return (null, spDef, null, null);
                    }
                    else if (reviewResult.Decision == UserDecision.ProvideFeedback)
                    {
                        if (string.IsNullOrWhiteSpace(reviewResult.UserFeedback))
                        {
                            continue;
                        }

                        _userInteraction.NotifyStatus($"[yellow]{selectedOption}[/] - 피드백 반영 재생성 중...");
                        var humanFeedbackLog = $"[L3 사용자 보완 피드백 로그]:\n{reviewResult.UserFeedback}";

                        string reSpec = string.Empty;
                        try
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
                                accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
                            }
                            accumulatedThinking.AppendLine();
                        }
                        catch (Exception ex)
                        {
                            _userInteraction.NotifyError($"피드백 반영 재생성 실패: {ex.Message}");
                        }

                        if (string.IsNullOrEmpty(reSpec))
                        {
                            continue;
                        }

                        // 피드백 반영본에 대한 L1 정적 검사 1회 수행
                        var l1Re = _validator.Validate(reSpec);
                        if (!l1Re.IsValid)
                        {
                            _userInteraction.NotifyStatus("피드백 적용본에서 정적 에러가 검출되어 AI 자가 수정 1회 더 진행합니다.");
                            try
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
                                    accumulatedThinking.AppendLine("*(추론 비활성화 또는 추론 기능을 지원하지 않는 모델입니다.)*");
                                }
                                accumulatedThinking.AppendLine();
                            }
                            catch { }
                        }

                        specificationMarkdown = reSpec;
                    }
                }
            }

            return (specificationMarkdown, spDef, finalReview, accumulatedThinking.ToString());
        }

        public async Task<string?> RunConsolidatedPipelineAsync(
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            string provider,
            bool isBatchMode = false,
            CancellationToken cancellationToken = default)
        {
            string? feedbackLog = null;
            string consolidatedPlan = string.Empty;

            // 설정에 따른 최대 시도 횟수 적용 (N회 또는 검증 완료까지)
            int attempt = 1;
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

                    AiResult aiResult;
                    using (var progressScope = _userInteraction.CreateProgressScope("배치 계획 수립") ?? NullProgressScope.Instance)
                    {
                        progressScope.AddTask("batch", $"{_consolidatorService.ModelName} 계획 수립 중...");
                        aiResult = await WrapWithProgress(_consolidatorService.GenerateConsolidatedBatchPlanAsync(specsCopy, targetLanguage, jobName, _consolidatorEffort, cancellationToken), progressScope, "batch");
                    }
                    consolidatedPlan = aiResult.Content;
                    genSuccess = true;
                }
                catch (Exception ex)
                {
                    _userInteraction.NotifyError($"{jobName} - AI 통합 계획 생성 실패 (시도 {attempt}): {ex.Message}");
                }

                if (!genSuccess || string.IsNullOrEmpty(consolidatedPlan))
                {
                    return null;
                }

                // L1: 기계적 무결성 검사
                var l1Result = _validator.ValidateConsolidated(consolidatedPlan);
                if (!l1Result.IsValid)
                {
                    _userInteraction.NotifyL1Errors(jobName, attempt, _maxAttempts, l1Result.Errors);

                    bool canRetry = _maxAttempts == -1 || attempt < _maxAttempts;
                    if (canRetry)
                    {
                        feedbackLog = l1Result.SuggestedPromptFix;
                        attempt++;
                        continue;
                    }
                    else
                    {
                        _userInteraction.NotifyError($"{jobName} - [[L1 기계 검증]] 최종 보완 실패. 마지막 작성 버전을 사용합니다.");
                        break;
                    }
                }

                // L2: AI 교차 리뷰
                ReviewResult? l2Result = null;
                bool reviewSuccess = false;

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
                catch (Exception ex)
                {
                    _userInteraction.NotifyError($"{jobName} - AI 교차 리뷰 실패 (시도 {attempt}): {ex.Message}");
                }

                if (reviewSuccess && l2Result != null && l2Result.HasDefects)
                {
                    _userInteraction.NotifyL2Defects(jobName, attempt, _maxAttempts, l2Result.FeedbackComment ?? string.Empty);

                    bool canRetry = _maxAttempts == -1 || attempt < _maxAttempts;
                    if (canRetry)
                    {
                        feedbackLog = $"[L2 AI 리뷰 피드백]: 다음 결함/누락사항이 지적되었습니다. 전면 반영해서 수정해 주십시오.\n{l2Result.FeedbackComment}";
                        attempt++;
                        continue;
                    }
                    else
                    {
                        _userInteraction.NotifyError($"{jobName} - [[L2 AI 리뷰]] 최종 보완 실패. 마지막 리뷰 반영 버전을 사용합니다.");
                        break;
                    }
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
                    _userInteraction.NotifyValidationSuccess(jobName);
                    break;
                }
            }

            // L3: 인간 개입형 승인 (TUI 모드 전용, 배치 모드 시 즉시 승인 및 반환)
            if (isBatchMode)
            {
                _userInteraction.NotifyStatus($"[green]{jobName}[/] - 배치 모드로 인해 통합 계획서가 자동으로 최종 승인되었습니다.");
                return consolidatedPlan;
            }

            while (true)
            {
                var reviewResult = await _userInteraction.RequestHumanReviewAsync(jobName, consolidatedPlan);

                if (reviewResult.Decision == UserDecision.Approve)
                {
                    return consolidatedPlan;
                }
                else if (reviewResult.Decision == UserDecision.Cancel)
                {
                    return null;
                }
                else if (reviewResult.Decision == UserDecision.ProvideFeedback)
                {
                    if (string.IsNullOrWhiteSpace(reviewResult.UserFeedback))
                    {
                        continue;
                    }

                    _userInteraction.NotifyStatus($"[yellow]{jobName}[/] - 피드백 반영 재생성 중...");
                    var specsCopy = new System.Collections.Generic.List<(string FileName, string Content)>(specs);
                    specsCopy.Add(("User_Feedback_Log.txt", $"[L3 사용자 보완 피드백 로그]:\n{reviewResult.UserFeedback}\n사용자 의견을 수용하여 설계 내용을 수정 및 보완해 주십시오."));

                    string rePlan = string.Empty;
                    try
                    {
                        var aiResult = await _consolidatorService.GenerateConsolidatedBatchPlanAsync(specsCopy, targetLanguage, jobName, _consolidatorEffort, cancellationToken);
                        rePlan = aiResult.Content;
                    }
                    catch (Exception ex)
                    {
                        _userInteraction.NotifyError($"피드백 반영 재생성 실패: {ex.Message}");
                    }

                    if (string.IsNullOrEmpty(rePlan))
                    {
                        continue;
                    }

                    // 피드백 반영본에 대한 L1 정적 검사 1회 수행
                    var l1Re = _validator.ValidateConsolidated(rePlan);
                    if (!l1Re.IsValid)
                    {
                        _userInteraction.NotifyStatus("피드백 적용본에서 정적 에러가 검출되어 AI 자가 수정 1회 더 진행합니다.");
                        try
                        {
                            var specsRe = new System.Collections.Generic.List<(string FileName, string Content)>(specsCopy);
                            specsRe.Add(("L1_Re_Fix.txt", l1Re.SuggestedPromptFix ?? string.Empty));
                            var aiResult = await _consolidatorService.GenerateConsolidatedBatchPlanAsync(specsRe, targetLanguage, jobName, _consolidatorEffort, cancellationToken);
                            rePlan = aiResult.Content;
                        }
                        catch { }
                    }

                    consolidatedPlan = rePlan;
                }
            }
        }

        private void ExportMetadataCleansingSql(string specificationMarkdown, string selectedOption, string outputDirectory)
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

                var sqlPath = System.IO.Path.Combine(cleansingDir, $"{selectedOption}_MetadataCleansing.sql");
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

        private async Task ApplyMetadataCleansingSqlAsync(string connectionString, string selectedOption, string outputDirectory, CancellationToken cancellationToken)
        {
            var sqlPath = System.IO.Path.Combine(outputDirectory, "cleansing", $"{selectedOption}_MetadataCleansing.sql");
            if (!System.IO.File.Exists(sqlPath)) return;

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
            catch (Exception ex)
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



        private string EscapeMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("[", "[[").Replace("]", "]]");
        }
    }
}
