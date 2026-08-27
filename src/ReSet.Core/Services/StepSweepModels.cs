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
        int MissingStepFiles)
    {
        /// <summary>
        /// SweepCommand가 프로시저 디렉터리를 못 찾아 조용히 건너뛴 참조 수(CLI 쪽
        /// 미해결). 서비스 쪽 미해결(DdlByProcedure 조회 실패)과 합산돼
        /// <see cref="HarnessGaps.UnresolvedProcedureReferences"/>에 실린다 - 같은
        /// 실패 양식이 파일 시스템 경계 양쪽에 있어서다.
        /// </summary>
        public int UnresolvedProcedureDirectoryLookups { get; init; }

        /// <summary>
        /// 측정한 단계 지시서 파일의 mtime 범위. CLI가 파일을 읽으며 채운다 -
        /// 서비스는 디스크를 모르므로 여기로 받는다. 그대로
        /// <see cref="HarnessGaps.StepBundleOldest"/>로 넘어간다.
        /// </summary>
        public DateTimeOffset? StepBundleOldest { get; init; }

        /// <inheritdoc cref="StepBundleOldest"/>
        public DateTimeOffset? StepBundleNewest { get; init; }

        /// <summary>대조 기준인 명세서(Spec.md)의 mtime 범위. 같은 이유로 CLI가 채운다.</summary>
        public DateTimeOffset? SpecOldest { get; init; }

        /// <inheritdoc cref="SpecOldest"/>
        public DateTimeOffset? SpecNewest { get; init; }

        /// <summary>
        /// 목차 JSON은 정상 파싱되지만 BatchStepPlanParser.MaxSteps(40) 상한을 넘어
        /// 버려진 Job. PlanStructure.md가 진짜 파싱 실패인지 상한 초과인지
        /// TryParse의 반환값(null)만으로는 구분할 수 없다 - 둘 다 null이다. 라벨을
        /// 믿고 JSON을 디버깅하러 가면 헛수고한다(POQSettleProc4가 실제 사례 -
        /// JSON은 73단계로 정상 파싱되지만 상한 때문에 버려진다).
        /// </summary>
        public IReadOnlyList<string> StepCountCapExceededJobs { get; init; } = Array.Empty<string>();
    }

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
        bool KnownTableNamesWereEmpty)
    {
        /// <summary>
        /// 프로시저 참조를 못 찾아 카운터 없이 넘어간 두 자리의 합 -
        /// SweepCommand.cs(프로시저 디렉터리 색인 미스)와 StepSweepService의
        /// DdlByProcedure 조회 미스(코드 집합 지표 계산 중). 코퍼스 실측으로는
        /// 현재 0건이지만, 0이라고 말하는 것과 아무 말도 안 하는 것은 다르다 -
        /// 다음 사람이 같은 함정(조용한 continue)을 다시 못 보게 하는 것이 목적이다.
        /// </summary>
        public int UnresolvedProcedureReferences { get; init; }

        /// <summary>
        /// 측정한 단계 지시서 파일의 mtime 범위(가장 오래된 것 ~ 가장 새것).
        ///
        /// [왜 보고서가 이것을 알아야 하는가] 축 B의 기준값은 명세서다. 단계 번들이
        /// 명세서보다 낡았으면 이 스윕이 대조한 불일치는 이행 결함이 아니라 세대
        /// 차이일 수 있다 - docs/audit-defect-catalog.md 3절이 그 오염을 경고한다.
        /// 그런데 보고서는 캐시 FormatVersion만 싣고 번들 세대는 안 실어서, 보고서만
        /// 읽는 사람은 그 326쌍이 언제 만들어진 것인지 알 수 없었다.
        /// </summary>
        public DateTimeOffset? StepBundleOldest { get; init; }

        /// <inheritdoc cref="StepBundleOldest"/>
        public DateTimeOffset? StepBundleNewest { get; init; }

        /// <summary>대조 기준인 명세서(Spec.md)의 mtime 범위. 위와 나란히 놓아야 세대 차이가 읽힌다.</summary>
        public DateTimeOffset? SpecOldest { get; init; }

        /// <inheritdoc cref="SpecOldest"/>
        public DateTimeOffset? SpecNewest { get; init; }

        /// <summary>
        /// 목차 파싱은 성공했으나(Job이 input.Jobs에 들어옴) 측정 쌍이 0인 Job의
        /// 이름. StepSweepReportWriter의 클래스 주석이 스스로 경고하는 함정 -
        /// "대상 범위가 줄면 그 감소가 개선처럼 읽힌다" - 이 Job별 표(발화 0인 Job의
        /// 행을 생략한다)에서도 안 드러나므로 머리말에서 이름으로 열거해야 한다.
        /// </summary>
        public IReadOnlyList<string> JobsWithZeroMeasuredPairs { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Sweep 중 예외를 던진 Job. Job 단위 가드가 이 Job만 건너뛰고 나머지는
        /// 계속 측정한다 - 가드가 없으면 한 Job의 결함이 전체 측정 쌍(코퍼스
        /// 실측 326쌍)을 부분 보고 없이 죽인다.
        /// </summary>
        public IReadOnlyList<string> JobsThatThrew { get; init; } = Array.Empty<string>();

        /// <summary>
        /// <see cref="SweepInput.StepCountCapExceededJobs"/>를 그대로 실어 나른다 -
        /// "목차 파싱 실패"와 "상한 초과로 제외"를 같은 라벨로 뭉치면 라벨을 믿고
        /// JSON을 디버깅하러 가는 사람이 헛수고한다.
        /// </summary>
        public IReadOnlyList<string> StepCountCapExceededJobs { get; init; } = Array.Empty<string>();
    }

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

        /// <summary>
        /// 한 코드 앵커가 둘 이상의 문장에 붙은 단계 수 - 코드 축이 그 단계에서
        /// (부분적으로) 꺼진 곳이다.
        ///
        /// [왜 이 값이 보고서에 있어야 하는가] MechanicalValidator의 모호성 가드
        /// (ResolveAnchoredStatements)가 그런 서수를 침묵시킨다. 침묵은 옳지만
        /// 대가가 있다 - 그 단계에 진짜 결함이 있어도 함께 묻힌다. 이 값이 그
        /// 대가의 크기다. 크면 검사 B·C의 관할이 그만큼 좁다는 뜻이고, 근본
        /// 해결은 생성기가 문장마다 고유 코드를 쓰게 하는 것이다.
        ///
        /// [정확히 「침묵당한 건수」는 아니다] 명세서 사전에 실리지 않는 코드
        /// (예: 범용 catch-all `4000`)의 중복까지 세는 상위 근사다 - 그런 코드는
        /// 애초에 서수로 환산되지 않아 침묵시킬 것도 없다. 정확한 수치를 내려면
        /// 검증기에서 배선을 빼야 하고, 그 결합보다 이 근사가 낫다고 봤다.
        /// </summary>
        public int StepsWithReusedCodeAnchors { get; init; }

        /// <summary>
        /// [침묵 분모] 캐시 17 이 앵커를 정상화하면 발화가 켜지는 만큼 **가려져 있던
        /// 침묵도 함께 켜진다.** 좌표 차분으로는 그 부류가 보이지 않는다 - 가드가
        /// 조건 (A)에서도 (B)에서도 같은 좌표를 침묵시키면 차분이 정의상 0 이기 때문이다.
        ///
        /// 승격 전에는 앵커가 안 풀려 면제가 **도달 불가능**하다. 그래서 아래 계수들의
        /// 증가분이 곧 「이번에 새로 생긴 침묵」이다.
        ///
        /// [사유가 아니라 분모다] 어느 좌표가 어느 가드에 침묵당했는지는 세지 않는다 -
        /// 그러려면 검증기가 판정 사유를 내보내야 하고, 그 결합보다 이 분모가 낫다고
        /// 봤다(StepsWithReusedCodeAnchors 가 같은 판단을 적는다).
        ///
        /// [분모는 앵커를 보유한 문장만이다 - 2026-08-27 재리뷰 Important 1] 그
        /// 단계의 모든 DML 문장이 아니다. U-앵커도 CodeAnchor도 없는 문장은
        /// ResolveOrdinal의 후보가 된 적이 없으므로 AnchorsUnresolved에 넣지
        /// 않는다 - 넣으면 평범한 무앵커 문장 수가 "앵커 해결 실패"로 잘못
        /// 읽힌다(코퍼스 실측: 분모를 좁히기 전 AnchorsUnresolved=1641 vs
        /// AnchorsResolved+AnchorsDroppedForAmbiguity=940 - 1641은 진짜 해결
        /// 실패가 아니었다).
        /// </summary>
        public int AnchorsResolved { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int AnchorsUnresolved { get; init; }

        /// <summary>
        /// 서수로는 환산됐으나 (Kind, Ordinal) 모호성 가드가 버린 문장 수.
        /// ResolveOrdinal 이 값을 낸 문장 수에서 ResolveAnchoredStatements 가 돌려준
        /// 문장 수를 뺀 값이라 근사가 아니라 같은 재료다.
        /// </summary>
        public int AnchorsDroppedForAmbiguity { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StatementsWithLineage { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StatementsReadingOnlyStaging { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StatementsReadingOwnTarget { get; init; }

        /// <summary>
        /// 원천이 전부 스테이징인데 자기 대상을 함께 읽어 **면제가 취소된** 문장 수.
        ///
        /// [이 회차에서 가장 중요한 계수] 2026-08-27 최종 리뷰의 Critical 이 이 자리다 -
        /// 방어선 둘이 서로의 전제를 무너뜨려 검사 C 가 35 좌표에서 꺼졌는데 발견 당시
        /// **관측 변화가 0 이었다.** 승격 후에도 이 값이 0 이면 방어가 도달하지 못한
        /// 것이고, 그건 수정이 살아 있다는 증거가 아니라 재지 않았다는 증거다.
        /// </summary>
        public int StagingExemptionsCancelledByOwnTarget { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StatementsWithSubordinatePredicates { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int SubordinatePredicateColumnTotal { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StagingSourceTotal { get; init; }
    }

    public sealed record SweepReport(
        IReadOnlyList<SweepFinding> Findings,
        SweepIndicators Indicators,
        HarnessGaps Gaps);
}
