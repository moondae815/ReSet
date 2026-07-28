using System;

namespace ReSet.Validator.Core.Models
{
    public class ValidationResult
    {
        public string SpecFilePath { get; set; } = string.Empty;
        public string SourceCodePath { get; set; } = string.Empty;
        public string MappedName { get; set; } = string.Empty; // e.g. dbo.CustOrderHist
        
        // Level 1: Static Check
        public bool L1Passed { get; set; }
        public string L1Message { get; set; } = string.Empty;
        
        // Level 2: AI Logic Check
        public bool L2Passed { get; set; }
        public GapReport? GapReport { get; set; }
        
        public string SpecContent { get; set; } = string.Empty;
        public string CodeContent { get; set; } = string.Empty;
        
        // Level 3: Human Review
        public bool IsApproved { get; set; }
        public string HumanFeedback { get; set; } = string.Empty;

        public string DisplayStatus
        {
            get
            {
                if (IsApproved) return "Approved (L3)";
                if (L2Passed) return "Semantic Verified (L2)";
                if (L1Passed) return "Static Checked (L1)";
                return "Failed";
            }
        }
    }
}
