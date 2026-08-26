using System;
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>단계 검사 다섯 개. 미분류는 조용히 접지 않고 따로 센다.</summary>
    public enum SweepCheck { A, B, C, D, E, Unclassified }

    /// <summary>
    /// AsIs = 오늘 그대로(캐시 16, 「오류 코드」 표 없음).
    /// SimulatedCache17 = 원본 DDL에서 만든 코드→서수 사전을 주입한 상태.
    /// </summary>
    public enum SweepCondition { AsIs, SimulatedCache17 }

    /// <summary>발화 하나. Kind·Ordinal·Items는 검사 B·C에서만 채워진다.</summary>
    public sealed record SweepFinding(
        string JobName,
        string StepCode,
        SweepCheck Check,
        SweepCondition Condition,
        string Message)
    {
        public string? Kind { get; init; }
        public int? Ordinal { get; init; }
        public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Job 하나의 측정 재료. 전부 메모리에 올라온 값이다 - 서비스는 파일을 모른다.
    /// </summary>
    /// <param name="Specs">키 규약이 중요하다. FileName은 프로시저 이름
    /// ("dbo.UP_X")이지 파일 경로가 아니다 - SpecStatementFactsExtractor가
    /// MechanicalValidator.BareObjectName(FileName)으로 키를 만들므로
    /// "dbo.UP_X.md"를 넘기면 키가 "md"가 되어 조회가 전부 빗나간다.</param>
    public sealed record SweepJob(
        string JobName,
        IReadOnlyList<BatchStepPlan> Steps,
        IReadOnlyDictionary<string, string> StepMarkdownByCode,
        IReadOnlyList<(string FileName, string Content)> Specs,
        IReadOnlyDictionary<string, string> DdlByProcedure,
        IReadOnlyDictionary<string, string> DateParameterByProcedure);

    /// <param name="PlanParseFailedJobs">PlanStructure.md에서 단계 목록을 못 읽은 Job.</param>
    /// <param name="MissingStepFiles">목차가 선언했으나 agent/steps/에 실물이 없는 단계 수.</param>
    public sealed record SweepInput(
        IReadOnlyList<SweepJob> Jobs,
        IReadOnlyList<string> PlanParseFailedJobs,
        int MissingStepFiles);

    /// <summary>
    /// 대상 범위가 줄어든 것이 개선처럼 보이지 않게 매번 보고서에 싣는 값들.
    /// </summary>
    public sealed record HarnessGaps(
        IReadOnlyList<string> PlanParseFailedJobs,
        int MissingStepFiles,
        int MeasuredPairs,
        int MeasuredJobs,
        bool StepInterfacesWereNull,
        bool RunRowOwnedTablesWereNull,
        bool KnownTableNamesWereEmpty);

    /// <param name="MultiProcedureSteps">참조 원본 SP가 2개 이상인 단계 수.</param>
    /// <param name="StepsMissingSpecCodes">SP 표에는 있는데 단계 SQL에 없는 코드가 있는 단계 수.</param>
    /// <param name="StepsWithUnknownCodes">단계 SQL에는 있는데 SP 표에 없는 코드가 있는 단계 수.</param>
    public sealed record SweepIndicators(
        int MultiProcedureSteps,
        int StepsMissingSpecCodes,
        int StepsWithUnknownCodes);

    public sealed record SweepReport(
        IReadOnlyList<SweepFinding> Findings,
        SweepIndicators Indicators,
        HarnessGaps Gaps);
}
