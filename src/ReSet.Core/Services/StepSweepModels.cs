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
        int StepsWithUnknownCodes)
    {
        /// <summary>
        /// 단계 SQL 펜스 파싱 실패(StepSqlStatementReader의 lostStatementCount &gt; 0)로
        /// 코드 집합 대조(StepsMissingSpecCodes·StepsWithUnknownCodes)에서 통째로 뺀 단계 수.
        ///
        /// [왜 빼는가] 펜스가 파싱에 실패하면 stepCodes가 실제보다 적게(또는 비게) 나온다 -
        /// 이건 "코드 라벨이 소실됐다"는 신호가 아니라 "도구가 그 관용구를 못 읽는다"는
        /// 신호다. 코퍼스 실측으로는 891개 펜스 중 191개(21%), 326개 파일 중 119개(36%)가
        /// 최소 하나의 파싱 실패를 겪는다(StepSqlStatementReader.cs:70-77) - 이 신호를
        /// 무시하면 StepsMissingSpecCodes가 재는 것이 "코드 라벨 소실"이 아니라 "ScriptDom이
        /// 못 읽는 관용구의 분포"로 뒤바뀐다. 검사 A(CheckStatementCountAgainstSpec)가
        /// lostStatementCount &gt; 0일 때 개수 대조 전체를 접는 것과 같은 이유다.
        ///
        /// [이 값이 크면 무슨 뜻인가] 코드 집합 지표(StepsMissingSpecCodes·
        /// StepsWithUnknownCodes) 자체의 표본이 그만큼 줄었다는 뜻이다 - 두 지표의
        /// 분모가 전체 측정 단계 수가 아니라 "측정 단계 수 - 이 값"임을 항상 함께
        /// 읽어야 한다. 값이 크면 그 두 지표를 그대로 믿을 수 없다는 신호다.
        /// </summary>
        public int StepsSkippedForParseFailure { get; init; }
    }

    public sealed record SweepReport(
        IReadOnlyList<SweepFinding> Findings,
        SweepIndicators Indicators,
        HarnessGaps Gaps);
}
