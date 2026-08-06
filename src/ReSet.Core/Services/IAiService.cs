using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public interface IAiService
    {
        string ProviderName { get; }
        string ModelName { get; }
        Task<AiResult> GenerateSpecificationAsync(SpDefinition spDef, string userInstructions, string? feedbackLog = null, string? effort = null, CancellationToken cancellationToken = default);
        Task<AiResult> DeconstructSpLogicAsync(SpDefinition spDef, string userInstructions, string? feedbackLog = null, string? effort = null, CancellationToken cancellationToken = default, Action<(int current, int total, string message)>? progressCallback = null);
        Task<AiResult> GenerateSpecSectionAsync(SpDefinition spDef, string sectionType, string userInstructions, string? feedbackLog = null, string? effort = null, CancellationToken cancellationToken = default);
        Task<ReviewResult> ReviewSpecificationAsync(SpDefinition spDef, string specMarkdown, string? effort = null, CancellationToken cancellationToken = default);
        Task<AiResult> GenerateBatchMigrationPlanAsync(SpDefinition spDef, string targetLanguage, CancellationToken cancellationToken = default);
        Task<AiResult> BrainstormBatchPlanAsync(System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, CancellationToken cancellationToken = default);
        Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default);
        Task<AiResult> GenerateConsolidatedBatchPlanAsync(string planStructure, System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, CancellationToken cancellationToken = default);
        Task<AiResult> GenerateBatchPlanSkeletonAsync(IReadOnlyList<BatchStepPlan> steps, string planStructure, System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, CancellationToken cancellationToken = default);
        Task<AiResult> GenerateBatchStepSectionAsync(BatchStepPlan step, IReadOnlyList<BatchStepPlan> allSteps, string sharedConventions, System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, string? floorFeedback = null, CancellationToken cancellationToken = default);
        Task<ReviewResult> ReviewConsolidatedPlanAsync(System.Collections.Generic.List<(string FileName, string Content)> specs, string planMarkdown, string jobName, string? effort = null, CancellationToken cancellationToken = default);
        Task<AiResult> GenerateSettlementPolicyRulebookAsync(System.Collections.Generic.List<SpDefinition> spDefs, string profilingDataJson, CancellationToken cancellationToken = default);
    }

    public class ReviewResult
    {
        public bool HasDefects { get; set; }
        public string? FeedbackComment { get; set; }
        public string? ThinkingText { get; set; }
        
        // 5대 기준별 정량적 평가 점수 (각 0~10점)
        public int ScoreAccuracy { get; set; }     // 비즈니스 정합성
        public int ScoreCrud { get; set; }         // CRUD 및 데이터 매핑
        public int ScoreInterface { get; set; }    // 연동 인터페이스 구체성
        public int ScoreException { get; set; }    // 예외 및 트랜잭션/격리성
        public int ScoreReadability { get; set; }  // 다이어그램 및 시각화 가독성

        // 종합 점수 계산 (50점 만점)
        public int TotalScore => ScoreAccuracy + ScoreCrud + ScoreInterface + ScoreException + ScoreReadability;

        // 100점 만점 환산 점수
        public int NormalizedScore => (int)System.Math.Round((TotalScore * 100.0) / 50.0);
    }
}
