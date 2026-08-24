using System.Collections.Generic;

namespace ReSet.Cli
{
    public class CliArgs
    {
        public string? ConnectionString { get; set; }
        public bool AnalyzeAll { get; set; }
        public List<string> TargetProcedures { get; set; } = new();
        public bool EnableCodegen { get; set; }
        public string? Engine { get; set; }
        public string? JobName { get; set; }
        public bool GeneratePolicy { get; set; }
        public List<string> PolicyProcedures { get; set; } = new();
        public string? ExtractSnapshotPath { get; set; }

        /// <summary>--coverage-map의 대상. Job 이름이거나 객체 이름이다.
        /// DB·AI 없이 output/ 산출물만 읽는다.</summary>
        public string? CoverageMapTarget { get; set; }

        public bool IsBatchMode => AnalyzeAll || TargetProcedures.Count > 0 || GeneratePolicy
            || !string.IsNullOrEmpty(ExtractSnapshotPath)
            || !string.IsNullOrEmpty(CoverageMapTarget);
    }
}
