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
        public bool PlanOnly { get; set; }
        public string? ExtractSnapshotPath { get; set; }

        /// <summary>--coverage-map의 대상. Job 이름이거나 객체 이름이다.
        /// DB·AI 없이 output/ 산출물만 읽는다.</summary>
        public string? CoverageMapTarget { get; set; }

        /// <summary>--sweep. DB·AI 없이 output/ 산출물만으로 단계 검사 A~E를 전수 스윕한다.</summary>
        public bool RunSweep { get; set; }

        /// <summary>
        /// PlanOnly가 여기 포함되는 이유: 이 경로는 DB에 붙지 않지만 AI는 무인으로 부른다.
        /// 배치 모드로 켜져야 CliProviderBatchGuard(구독 쿼터 소진·권한 프롬프트 정지 차단)를
        /// 그대로 받는다. --coverage-map/--sweep처럼 가드 앞으로 빼면 안 된다.
        /// </summary>
        public bool IsBatchMode => AnalyzeAll || TargetProcedures.Count > 0 || GeneratePolicy
            || PlanOnly
            || !string.IsNullOrEmpty(ExtractSnapshotPath)
            || !string.IsNullOrEmpty(CoverageMapTarget);
    }
}
