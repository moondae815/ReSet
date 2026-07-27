using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
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
        /// 다중 SP와 통합 배치 전환 계획을 기반으로 통합 마이그레이션 지시서 번들을 저장합니다.
        /// </summary>
        Task ExportConsolidatedMigrationInstructionsAsync(
            System.Collections.Generic.List<SpDefinition> spDefs,
            string consolidatedPlan,
            string jobName,
            string baseOutputDir,
            string targetLanguage);

        /// <summary>
        /// 지시서 마크다운 파일 하단에 L1/L2 피드백 내용을 안전하게 덧붙이거나 교체합니다.
        /// </summary>
        Task AppendFeedbackToInstructionsAsync(string instructionsFilePath, string feedbackMarkdown);

        /// <summary>
        /// 생성된 단위 테스트 소스코드 코드를 타겟 디렉토리에 저장합니다.
        /// </summary>
        Task ExportUnitTestCodeAsync(string baseOutputDir, string procedureName, string targetLanguage, string testCodeContent);
    }
}
