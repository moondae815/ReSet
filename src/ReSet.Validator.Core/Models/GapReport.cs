using System;

namespace ReSet.Validator.Core.Models
{
    public class GapReport
    {
        public string OverallStatus { get; set; } = "MATCH"; // MATCH, MISMATCH, PARTIAL
        public string InputParametersGap { get; set; } = string.Empty;
        public string OutputResultSetsGap { get; set; } = string.Empty;
        public string BusinessLogicGap { get; set; } = string.Empty;
        public string ExceptionHandlingGap { get; set; } = string.Empty;
        public string DataAccessBoundaryGap { get; set; } = string.Empty;
        public string Suggestions { get; set; } = string.Empty;

        // --- 원본 프롬프트 및 응답 (raw 저장용) ---
        public string SystemPrompt { get; set; } = string.Empty;
        public string UserPrompt { get; set; } = string.Empty;
        public string AiThinking { get; set; } = string.Empty;
        public string AiRawResponse { get; set; } = string.Empty;

        // --- AI 메타 정보 (문서 상단 표기용) ---
        public string AiProviderName { get; set; } = string.Empty;
        public string AiModelName { get; set; } = string.Empty;
        public string AiEffort { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        public bool HasGaps => OverallStatus != "MATCH" ||
                              !string.IsNullOrEmpty(InputParametersGap) ||
                              !string.IsNullOrEmpty(OutputResultSetsGap) ||
                              !string.IsNullOrEmpty(BusinessLogicGap) ||
                              !string.IsNullOrEmpty(ExceptionHandlingGap) ||
                              !string.IsNullOrEmpty(DataAccessBoundaryGap);
    }
}
