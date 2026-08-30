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
        Task<AiResult> DraftBatchPlanStructureAsync(string brainstormingResult, string targetLanguage, string jobName, IReadOnlyList<string> sourceProcedures, string? effort = null, string? previousStructure = null, string? redraftFeedback = null, CancellationToken cancellationToken = default);
        Task<AiResult> GenerateConsolidatedBatchPlanAsync(string planStructure, System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, IReadOnlyList<StepInterface>? stepInterfaces = null, string? brainstorming = null, CancellationToken cancellationToken = default);
        Task<AiResult> GenerateBatchPlanSkeletonAsync(IReadOnlyList<BatchStepPlan> steps, string planStructure, System.Collections.Generic.List<(string FileName, string Content)> specs, string targetLanguage, string jobName, string? effort = null, string? brainstorming = null, IReadOnlyList<StepInterface>? stepInterfaces = null, CancellationToken cancellationToken = default);
        Task<AiResult> GenerateBatchStepSectionAsync(BatchStepPlan step, IReadOnlyList<BatchStepPlan> allSteps, string sharedConventions, System.Collections.Generic.List<(string FileName, string Content)> specs, IReadOnlyList<StepInterface> stepInterfaces, string targetLanguage, string jobName, string? effort = null, string? floorFeedback = null, CancellationToken cancellationToken = default);
        Task<ReviewResult> ReviewConsolidatedPlanAsync(System.Collections.Generic.List<(string FileName, string Content)> specs, string planMarkdown, string jobName, string? effort = null, CancellationToken cancellationToken = default);
        Task<AiResult> GenerateSettlementPolicyRulebookAsync(System.Collections.Generic.List<SpDefinition> spDefs, string profilingDataJson, CancellationToken cancellationToken = default);
    }

    public class ReviewResult
    {
        public bool HasDefects { get; set; }
        public string? FeedbackComment { get; set; }
        public string? ThinkingText { get; set; }

        /// <summary>
        /// 통합 배치 계획서에서 결함이 있는 단계 코드. 단일 SP 명세서 리뷰에서는 늘 빈 목록이다.
        ///
        /// 이 필드가 있어야 결함 하나 때문에 문서를 통째로 다시 만들지 않는다.
        /// FeedbackComment 산문에서 코드를 파싱하지 않는 이유는
        /// RegenerationScopeSelector의 클래스 주석에 기록되어 있다.
        /// </summary>
        public List<string> DefectiveSteps { get; set; } = new();

        /// <summary>
        /// 공통 규약(골격)과 단계 본문이 서로 모순되는가.
        ///
        /// 이 필드가 필요한 이유: 실측 「필수 수정 3」은 1.4절이 선언한 그룹 트랜잭션
        /// 계약과 S11~S13 의사코드의 모순이었다. 어느 한 단계의 결함이 아니므로 각
        /// 섹션은 자기 안에서 일관되고, 단계 재생성으로는 영원히 고쳐지지 않는다.
        /// 담을 자리가 없던 동안은 관련 단계들이 DefectiveSteps에 실렸고, 그 단계들이
        /// 각자 다시 쓰이며 모순이 재생산됐다.
        /// </summary>
        public bool SkeletonDefective { get; set; }

        /// <summary>
        /// 목차 자체가 결함인가 — 단계 누락, 단계 배치 오류, 청킹 불가 단계의 청킹 지정.
        /// StructureRedraftPolicy가 목차 재설계를 발동할 두 조건 중 하나다.
        /// </summary>
        public bool StructureDefective { get; set; }

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
