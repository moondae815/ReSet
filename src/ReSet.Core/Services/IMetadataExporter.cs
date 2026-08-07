using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
namespace ReSet.Core.Services
{
    public enum DependencyArtifactMode
    {
        Reference,
        PortableBundle
    }

    public interface IMetadataExporter
    {
        /// <summary>
        /// 수집된 원천 DB 정보 및 프롬프트 컨텍스트를 디스크에 저장합니다.
        /// </summary>
        Task ExportRawMetadataAsync(
            SpDefinition spDef, 
            string rawPromptContext, 
            string baseOutputDir, 
            bool saveJson, 
            bool saveContext, 
            bool saveFiles);

        /// <summary>
        /// 코드 객체별 표준 DDL과 의존성 그래프 매니페스트를 저장합니다.
        /// </summary>
        Task ExportCodeObjectArtifactsAsync(
            SpDefinition definition,
            CodeObjectKey objectKey,
            CodeObjectPipelineResult graph,
            DependencyArtifactMode artifactMode,
            string outputRoot,
            string? rawPromptContext = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 호출자가 이미 계산한 객체 경로 정책을 사용하여 코드 객체 아티팩트를 저장합니다.
        /// </summary>
        Task ExportCodeObjectArtifactsAsync(
            SpDefinition definition,
            CodeObjectKey objectKey,
            CodeObjectPipelineResult graph,
            DependencyArtifactMode artifactMode,
            OutputPathResolver paths,
            string? rawPromptContext = null,
            CancellationToken cancellationToken = default);



        /// <summary>
        /// 다중 SP와 통합 배치 전환 계획을 기반으로 코딩 에이전트용 번들을 저장한다.
        /// layout이 null이면 계획서를 분할하지 않고 단일 파일로 남긴다.
        /// </summary>
        Task<BundleResult> ExportConsolidatedMigrationInstructionsAsync(
            System.Collections.Generic.List<SpDefinition> spDefs,
            string consolidatedPlan,
            VerificationOutcome planOutcome,
            string jobName,
            string baseOutputDir,
            string targetLanguage,
            OutputPathResolver paths,
            PlanLayout? layout = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 지시서 마크다운 파일 하단에 L1/L2 피드백 내용을 안전하게 덧붙이거나 교체합니다.
        /// </summary>
        Task AppendFeedbackToInstructionsAsync(string instructionsFilePath, string feedbackMarkdown, CancellationToken cancellationToken = default);

    }
}
