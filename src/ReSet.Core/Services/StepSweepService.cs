using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코퍼스 단계 지시서를 전수로 훑어 단계 검사 A~E의 발화량을 잰다.
    ///
    /// [왜 디스크를 모르는가] 로직이 CLI에 있으면 테스트가 코퍼스 의존 골든이 되고,
    /// 코퍼스가 없을 때 Skip으로 조용히 통과한다(CoverageMapGoldenTests가 그렇다).
    /// 측정을 재현 가능하게 만드는 것이 이 도구의 목적인데 그 도구의 회귀가 초록으로
    /// 숨으면 목적을 스스로 배반한다. 파일 읽기는 SweepCommand에만 있다.
    /// </summary>
    public static class StepSweepService
    {
        /// <summary>
        /// 원본 DDL에서 캐시 17 이후의 코드→서수 사전을 만든다.
        ///
        /// [왜 표로 렌더링해서 리더에 먹이는가] ExtractErrorCodes의 결과를 직접 사전으로
        /// 접으면 중복 코드 처리 규칙이 두 곳에 생긴다. 제품의 규칙
        /// (SpecStatementFactsExtractor.ReadErrorCodeToOrdinal:299 - 중복이면 덮어쓰지 않고
        /// 아예 빼고, dropped로 세 번째 등장도 막는다)과 조금만 달라도 실제 파이프라인이
        /// 결코 만들지 않을 사전으로 측정하게 된다. 읽는 쪽을 제품 코드 그대로 쓴다.
        /// </summary>
        public static IReadOnlyDictionary<string, (string Kind, int Ordinal)>
            BuildSimulatedErrorCodeMap(string? ddl, string dateParameterName)
        {
            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, dateParameterName);
            if (facts.Count == 0)
            {
                return new Dictionary<string, (string, int)>(StringComparer.Ordinal);
            }

            var synthesized = SpecStatementFactsExtractor.Extract(
                new List<(string FileName, string Content)>
                {
                    ("sweep.synthetic", RenderErrorCodeTable(facts)),
                });

            return synthesized.TryGetValue("synthetic", out var parsed)
                ? parsed.ErrorCodeToOrdinal
                : new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        }

        /// <summary>
        /// ExtractErrorCodes의 결과를 명세서에 실리는 표 모양으로 되돌린다.
        ///
        /// 헤딩과 열 이름은 AiService가 프롬프트에 싣는 것과 같아야 한다 - 어긋나면
        /// ReadErrorCodeToOrdinal이 표를 못 찾아 빈 사전이 나온다. 그 실패는 조용하지
        /// 않다: 조건 (B)의 발화가 통째로 0이 되어 보고서에 드러난다.
        /// </summary>
        public static string RenderErrorCodeTable(IReadOnlyList<ErrorCodeFact> facts)
        {
            var builder = new StringBuilder();
            builder.AppendLine(DmlScopeExtractor.ErrorCodeTableHeading);
            builder.AppendLine();
            builder.AppendLine("| 문장 | 오류 코드 | 설정 대상 |");
            builder.AppendLine("| :--- | :--- | :--- |");

            foreach (var fact in facts)
            {
                builder.AppendLine(
                    $"| {fact.Operation} {fact.StatementOrdinal} | {fact.Code} | {fact.Variable} |");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 코퍼스 전수를 훑어 검사 A~E의 발화를 조건 (A)·(B) 양쪽으로 모은다.
        ///
        /// [왜 두 조건을 함께 재는가] 고를 수 있으면 잘못 고를 수 있다. 실제로 한 번
        /// 그랬다 - 조건 (B)를 재야 할 자리에서 (A)를 재고 "코퍼스가 변했다"고 보고한
        /// 일이 있었다. 두 조건의 차이 자체가 캐시 17이 켜질 때의 변화량이라 어차피
        /// 둘 다 필요하다.
        /// </summary>
        public static SweepReport Sweep(SweepInput input)
        {
            var validator = CreateValidator();
            var findings = new List<SweepFinding>();
            var measuredPairs = 0;
            var measuredJobs = 0;

            foreach (var job in input.Jobs)
            {
                var conditionColumns = SpecConditionColumnExtractor.Extract(job.Specs);
                var factsAsIs = SpecStatementFactsExtractor.Extract(job.Specs);
                var factsSimulated = InjectSimulatedCodes(factsAsIs, job);

                var measuredInThisJob = false;

                foreach (var step in job.Steps)
                {
                    // 목차가 선언했으나 실물이 없는 단계다. 빈 문자열을 넘기면
                    // "섹션 내용이 비어있습니다"가 발화해 결손이 결함으로 둔갑한다.
                    if (!job.StepMarkdownByCode.TryGetValue(step.Code, out var markdown)
                        || string.IsNullOrWhiteSpace(markdown))
                    {
                        continue;
                    }

                    measuredPairs++;
                    measuredInThisJob = true;

                    Collect(SweepCondition.AsIs, factsAsIs);
                    Collect(SweepCondition.SimulatedCache17, factsSimulated);

                    void Collect(
                        SweepCondition condition,
                        IReadOnlyDictionary<string, SpecStatementFacts> facts)
                    {
                        // 오케스트레이터(VerificationPipelineOrchestrator.cs:3238)의 호출을
                        // 그대로 본뜬다. 갈라지면 파이프라인이 실제로 하지 않는 판정을 재게 된다.
                        // stepInterfaces·runRowOwnedTables는 DB 메타데이터가 필요해 로컬에서
                        // 만들 수 없다. A~E 어느 검사도 그 둘을 읽지 않는다 -
                        // CheckStepInterface(:600)·CheckFirstStepRowCreation(:1518)만 쓴다.
                        var result = validator.ValidateBatchStep(
                            markdown, step,
                            Array.Empty<string>(),
                            conditionColumns,
                            stepInterfaces: null,
                            runRowOwnedTables: null,
                            statementFactsByProcedure: facts,
                            allSteps: job.Steps);

                        foreach (var message in result.Errors)
                        {
                            var check = StepSweepClassifier.Classify(message);
                            findings.Add(
                                StepSweepClassifier.Describe(
                                    job.JobName, step.Code, check, condition, message));
                        }
                    }
                }

                if (measuredInThisJob) measuredJobs++;
            }

            return new SweepReport(
                findings,
                ComputeIndicators(input),
                new HarnessGaps(
                    input.PlanParseFailedJobs,
                    input.MissingStepFiles,
                    measuredPairs,
                    measuredJobs,
                    StepInterfacesWereNull: true,
                    RunRowOwnedTablesWereNull: true,
                    KnownTableNamesWereEmpty: true));
        }

        /// <summary>SP별 재료에 조건 (B)의 코드 사전을 갈아 끼운다. 제품 코드는 안 바뀐다 - init 속성이다.</summary>
        private static IReadOnlyDictionary<string, SpecStatementFacts> InjectSimulatedCodes(
            IReadOnlyDictionary<string, SpecStatementFacts> facts, SweepJob job)
        {
            var injected = new Dictionary<string, SpecStatementFacts>(
                facts, StringComparer.OrdinalIgnoreCase);

            foreach (var (procedure, ddl) in job.DdlByProcedure)
            {
                var key = MechanicalValidator.BareObjectName(procedure);
                if (!injected.TryGetValue(key, out var existing)) continue;

                job.DateParameterByProcedure.TryGetValue(procedure, out var dateParameter);
                injected[key] = existing with
                {
                    ErrorCodeToOrdinal = BuildSimulatedErrorCodeMap(ddl, dateParameter ?? string.Empty),
                };
            }

            return injected;
        }

        /// <summary>
        /// 스윕에 필요한 검증기를 만든다. 생성자의 유일한 매개변수(useMermaidCli)는
        /// 다이어그램 검증에만 쓰이고 A~E 어느 검사도 건드리지 않으므로 기본값을 쓴다.
        /// </summary>
        private static MechanicalValidator CreateValidator() => new();

        /// <summary>
        /// 캐시 17 인상 전에 세야 할 노출량 둘.
        ///
        /// [다중 레거시 SP 단계] MergeErrorCodeMaps는 코드 문자열만을 키로 삼고 SP로
        /// 스코프하지 않는다. SP A에만 있는 코드가 병합 사전에 남아, 실제로는 SP B에서
        /// 온 문장을 A의 (Kind, Ordinal)로 환산할 수 있다. 하위 가드(후보 1개 판정 +
        /// TargetTable 대조)는 두 SP가 같은 물리 테이블을 갱신하면 통과한다.
        ///
        /// [코드 집합 어긋남] 실측 사례가 있다 - UP_UTIL_SETTLE_COMM_UPD의 원본은
        /// -9/-10/-11을 쓰는데 이행 코드는 같은 세 블록에 -10/-11/-12를 단다. -9가
        /// 소실되고 이후 전체가 1씩 밀렸다. 밀림을 직접 보는 대신 밀림의 원인(라벨
        /// 소실)을 본다 - 집합 단위라 값싸다.
        /// </summary>
        private static SweepIndicators ComputeIndicators(SweepInput input)
        {
            var multiProcedureSteps = 0;
            var missingSpecCodes = 0;
            var unknownCodes = 0;

            foreach (var job in input.Jobs)
            {
                foreach (var step in job.Steps)
                {
                    if (step.LegacyProcedures.Count > 1) multiProcedureSteps++;

                    if (!job.StepMarkdownByCode.TryGetValue(step.Code, out var markdown)
                        || string.IsNullOrWhiteSpace(markdown))
                    {
                        continue;
                    }

                    var stepCodes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var statement in StepSqlStatementReader.Read(markdown))
                    {
                        if (!string.IsNullOrWhiteSpace(statement.CodeAnchor))
                        {
                            stepCodes.Add(statement.CodeAnchor!);
                        }
                    }

                    var specCodes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var procedure in step.LegacyProcedures)
                    {
                        if (!job.DdlByProcedure.TryGetValue(procedure, out var ddl)) continue;
                        job.DateParameterByProcedure.TryGetValue(procedure, out var dateParameter);

                        foreach (var code in BuildSimulatedErrorCodeMap(
                                     ddl, dateParameter ?? string.Empty).Keys)
                        {
                            specCodes.Add(code);
                        }
                    }

                    // 양쪽이 다 비면 대조할 것이 없다 - 어긋남이 아니라 무재료다.
                    if (stepCodes.Count == 0 && specCodes.Count == 0) continue;

                    if (specCodes.Except(stepCodes, StringComparer.Ordinal).Any()) missingSpecCodes++;
                    if (stepCodes.Except(specCodes, StringComparer.Ordinal).Any()) unknownCodes++;
                }
            }

            return new SweepIndicators(multiProcedureSteps, missingSpecCodes, unknownCodes);
        }
    }
}
