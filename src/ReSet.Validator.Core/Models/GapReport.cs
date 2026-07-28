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
        public string Suggestions { get; set; } = string.Empty;

        // --- 원본 프롬프트 및 응답 (raw 저장용) ---
        public string SystemPrompt { get; set; } = string.Empty;
        public string UserPrompt { get; set; } = string.Empty;
        public string AiThinking { get; set; } = string.Empty;
        public string AiRawResponse { get; set; } = string.Empty;

        public bool HasGaps => OverallStatus != "MATCH" || 
                              !string.IsNullOrEmpty(InputParametersGap) || 
                              !string.IsNullOrEmpty(OutputResultSetsGap) || 
                              !string.IsNullOrEmpty(BusinessLogicGap) || 
                              !string.IsNullOrEmpty(ExceptionHandlingGap);
    }
}
