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
        /// SweepCommand가 접두사 제거 규칙을 다시 구현하지 않게 하는 창구.
        /// MechanicalValidator.BareObjectName이 internal이라 CLI에서 직접 못 부른다.
        /// 규칙이 두 곳에 생기면 조회가 미묘하게 어긋난다.
        /// </summary>
        public static string BareProcedureName(string qualifiedName) =>
            MechanicalValidator.BareObjectName(qualifiedName);

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
        ///
        /// [셀 이스케이프] AiService.cs:1329가 표를 렌더할 때 쓰는
        /// MarkdownTableCellCodec.Escape를 그대로 쓴다. 셀에 `|`가 섞이면(예:
        /// 설정 대상이 `FLAGS | 4`처럼 비트 연산 문자열이면) 셀 경계로 읽혀 표가
        /// 잘못 쪼개진다 - 지금은 도달 불가지만(ReadErrorCodeToOrdinal이 「설정 대상」
        /// 칸까지는 안 읽는다) 실패 양식이 나쁘다: 표가 깨지면 리더가 빈 사전이
        /// 아니라 틀린 사전을 낼 수 있고 그건 조용하다.
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
                var operation = MarkdownTableCellCodec.Escape(fact.Operation);
                var code = MarkdownTableCellCodec.Escape(fact.Code);
                var variable = MarkdownTableCellCodec.Escape(fact.Variable);
                builder.AppendLine($"| {operation} {fact.StatementOrdinal} | {code} | {variable} |");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 코퍼스 전수를 훑어 검사 A~E의 발화를 조건 (A)·(B) 양쪽으로 모으고, 캐시 17
        /// 선결 지표(코드 집합 대조)를 Job마다 같은 자리에서 함께 집계한다.
        ///
        /// [왜 두 조건을 함께 재는가] 고를 수 있으면 잘못 고를 수 있다. 실제로 한 번
        /// 그랬다 - 조건 (B)를 재야 할 자리에서 (A)를 재고 "코퍼스가 변했다"고 보고한
        /// 일이 있었다. 두 조건의 차이 자체가 캐시 17이 켜질 때의 변화량이라 어차피
        /// 둘 다 필요하다.
        ///
        /// [왜 Job 단위 try/catch인가] 한 Job이 던지면 이 가드가 없을 때 326쌍 전체가
        /// 부분 보고 없이 죽는다. 조용히 삼키지 않는다 - 던진 Job 이름을
        /// <see cref="HarnessGaps.JobsThatThrew"/>에 남긴다.
        /// </summary>
        public static SweepReport Sweep(SweepInput input)
        {
            var validator = CreateValidator();
            var findings = new List<SweepFinding>();
            var measuredPairs = 0;
            var measuredJobs = 0;
            var jobsWithZeroMeasuredPairs = new List<string>();
            var jobsThatThrew = new List<string>();

            var multiProcedureSteps = 0;
            var missingSpecCodes = 0;
            var unknownCodes = 0;
            var skippedForParseFailure = 0;
            var unresolvedProcedureReferences = 0;

            foreach (var job in input.Jobs)
            {
                try
                {
                    var conditionColumns = SpecConditionColumnExtractor.Extract(job.Specs);
                    var factsAsIs = SpecStatementFactsExtractor.Extract(job.Specs);
                    var factsSimulated = InjectSimulatedCodes(factsAsIs, job);

                    // step.LegacyProcedures는 원문이고 코퍼스의 43%(314개 참조 중
                    // 134개)가 스키마 접두사 없이 실린다. 반면 DdlByProcedure·
                    // DateParameterByProcedure는 SweepCommand.cs:97-98이 항상 디렉터리
                    // 이름("dbo.UP_X")으로 키잉한다. 원문 그대로 조회하면 접두사 없는
                    // 단계에서 조용히 빗나간다 - InjectSimulatedCodes(위 :171)가 이미
                    // BareObjectName으로 정규화해 푸는 규약을 여기서도 쓴다.
                    var ddlByBareName = ToBareNameKeyed(job.DdlByProcedure);
                    var dateParameterByBareName = ToBareNameKeyed(job.DateParameterByProcedure);

                    var measuredInThisJob = false;

                    foreach (var step in job.Steps)
                    {
                        if (step.LegacyProcedures.Count > 1) multiProcedureSteps++;

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
                            // 오케스트레이터(VerificationPipelineOrchestrator.cs:3238)의
                            // 호출을 그대로 본뜬다. 갈라지면 파이프라인이 실제로 하지
                            // 않는 판정을 재게 된다. stepInterfaces·runRowOwnedTables는
                            // DB 메타데이터가 필요해 로컬에서 만들 수 없다. A~E 어느
                            // 검사도 그 둘을 읽지 않는다 -
                            // CheckStepInterface(:600)·CheckFirstStepRowCreation(:1518)만
                            // 쓴다.
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

                        // 캐시 17 선결 지표(코드 집합 대조). 아래 세 문단의 근거는
                        // 클래스 하단 <see cref="ToBareNameKeyed"/> 및
                        // [코드 집합 어긋남]·[펜스 파싱 실패는 소실이 아니다] 절 참고.
                        var stepStatements =
                            StepSqlStatementReader.Read(markdown, out var lostStatementCount);

                        if (lostStatementCount > 0)
                        {
                            // 펜스 파싱 실패는 코드 라벨 소실이 아니라 "도구가 그
                            // 관용구를 못 읽는다"는 신호다 - 코드 집합 대조에서
                            // 통째로 뺀다(검사 A가 lostStatementCount > 0일 때 개수
                            // 대조를 접는 것과 같은 이유).
                            skippedForParseFailure++;
                        }
                        else
                        {
                            var stepCodes = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var statement in stepStatements)
                            {
                                if (!string.IsNullOrWhiteSpace(statement.CodeAnchor))
                                {
                                    stepCodes.Add(statement.CodeAnchor!);
                                }
                            }

                            var specCodes = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var procedure in step.LegacyProcedures)
                            {
                                var bareName = MechanicalValidator.BareObjectName(procedure);
                                if (!ddlByBareName.TryGetValue(bareName, out var ddl))
                                {
                                    // 프로시저 참조를 못 찾았다 - SweepCommand.cs:79와
                                    // 같은 실패 양식이다. 카운터 없이 continue하지
                                    // 않는다.
                                    unresolvedProcedureReferences++;
                                    continue;
                                }

                                dateParameterByBareName.TryGetValue(bareName, out var dateParameter);

                                foreach (var code in BuildSimulatedErrorCodeMap(
                                             ddl, dateParameter ?? string.Empty).Keys)
                                {
                                    specCodes.Add(code);
                                }
                            }

                            // 양쪽이 다 비면 무재료다 - 어긋남이 아니다. 현재
                            // 로직에서는 이 가드가 없어도 관찰 가능한 차이가 없다:
                            // 빈 집합끼리의 Except는 방향에 상관없이 항상 비어
                            // 있으므로 아래 두 if는 어차피 발화하지 않는다. 그래도
                            // 남겨 둔다 - 의도를 코드에 적어 두는 값이 있고, 카운팅
                            // 방식이 Except가 아닌 것으로 바뀌면(예: 코드별 개별
                            // 집계) 이 가드가 실제로 필요해진다.
                            if (!(stepCodes.Count == 0 && specCodes.Count == 0))
                            {
                                if (specCodes.Except(stepCodes, StringComparer.Ordinal).Any())
                                {
                                    missingSpecCodes++;
                                }

                                if (stepCodes.Except(specCodes, StringComparer.Ordinal).Any())
                                {
                                    unknownCodes++;
                                }
                            }
                        }
                    }

                    if (measuredInThisJob)
                    {
                        measuredJobs++;
                    }
                    else
                    {
                        jobsWithZeroMeasuredPairs.Add(job.JobName);
                    }
                }
                catch (Exception)
                {
                    // 한 Job의 결함이 전체 측정을 죽이지 않게 한다 - 조용히 삼키지 않고
                    // 어느 Job이 던졌는지 이름을 남긴다.
                    jobsThatThrew.Add(job.JobName);
                }
            }

            return new SweepReport(
                findings,
                new SweepIndicators(multiProcedureSteps, missingSpecCodes, unknownCodes)
                {
                    StepsSkippedForParseFailure = skippedForParseFailure,
                },
                new HarnessGaps(
                    input.PlanParseFailedJobs,
                    input.MissingStepFiles,
                    measuredPairs,
                    measuredJobs,
                    StepInterfacesWereNull: true,
                    RunRowOwnedTablesWereNull: true,
                    KnownTableNamesWereEmpty: true)
                {
                    UnresolvedProcedureReferences =
                        unresolvedProcedureReferences + input.UnresolvedProcedureDirectoryLookups,
                    JobsWithZeroMeasuredPairs = jobsWithZeroMeasuredPairs,
                    JobsThatThrew = jobsThatThrew,
                    StepCountCapExceededJobs = input.StepCountCapExceededJobs,
                    StepBundleOldest = input.StepBundleOldest,
                    StepBundleNewest = input.StepBundleNewest,
                    SpecOldest = input.SpecOldest,
                    SpecNewest = input.SpecNewest,
                });
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

        // 캐시 17 인상 전에 세야 할 노출량들 - Sweep() 안의 코드 집합 대조 블록이 실제로
        // 세는 것들의 근거.
        //
        // [다중 레거시 SP 단계] MergeErrorCodeMaps는 코드 문자열만을 키로 삼고 SP로
        // 스코프하지 않는다. SP A에만 있는 코드가 병합 사전에 남아, 실제로는 SP B에서
        // 온 문장을 A의 (Kind, Ordinal)로 환산할 수 있다. 하위 가드(후보 1개 판정 +
        // TargetTable 대조)는 두 SP가 같은 물리 테이블을 갱신하면 통과한다.
        //
        // [코드 집합 어긋남] 실측 사례가 있다 - UP_UTIL_SETTLE_COMM_UPD의 원본은
        // -9/-10/-11을 쓰는데 이행 코드는 같은 세 블록에 -10/-11/-12를 단다. -9가
        // 소실되고 이후 전체가 1씩 밀렸다. 밀림을 직접 보는 대신 밀림의 원인(라벨
        // 소실)을 본다 - 집합 단위라 값싸다.
        //
        // [펜스 파싱 실패는 소실이 아니다] StepSqlStatementReader의 실측(코퍼스
        // 891개 펜스 중 191개(21%), 326개 파일 중 119개(36%)가 최소 하나의 파싱
        // 실패를 겪는다 - StepSqlStatementReader.cs:70-77)이 보여주듯, 펜스가
        // 파싱에 실패하면 stepCodes가 실제보다 적게(또는 비게) 나온다. 그 상태로
        // specCodes와 대조하면 "코드 라벨이 소실됐다"가 아니라 "도구가 그 관용구를
        // 못 읽는다"를 재게 된다. 검사 A(CheckStatementCountAgainstSpec)가
        // lostStatementCount > 0일 때 개수 대조 전체를 접는 것과 같은 이유로, 이
        // 지표도 그 단계를 코드 집합 대조에서 통째로 빼고 StepsSkippedForParseFailure로
        // 그 사실을 드러낸다 - 줄어든 측정 범위를 숨기지 않는다(§6).

        /// <summary>
        /// SweepCommand가 디렉터리 이름("dbo.UP_X")으로 채운 사전을 맨이름 키로
        /// 다시 인덱싱한다 - <see cref="Sweep"/>이 step.LegacyProcedures의
        /// 원문(접두사 있을 수도 없을 수도)으로 조회할 수 있게 하기 위해서다.
        ///
        /// [맨이름 충돌 결정] 다른 스키마의 같은 오브젝트명이 충돌하면 나중에 순회된
        /// 값이 이긴다 - Dictionary 인덱서 대입이 그렇게 동작하고,
        /// <see cref="InjectSimulatedCodes"/>(위 :171 부근)도 이미 같은 규약으로
        /// 짜여 있다. 조용히 고른 것이 아니라 명시한다: 이 스윕은 스텝이 어느
        /// 스키마를 가리키는지 선언하지 않으므로(step.LegacyProcedures가 맨이름일 때
        /// 스키마 정보 자체가 없다) 둘을 구분해 조회할 근거가 없다. "드물게
        /// 다른 스키마의 코드 집합과 섞일 수 있음"을 감수하고 "맨이름 단계는 아예
        /// 못 잰다"를 피하는 쪽을 택했다.
        /// </summary>
        private static Dictionary<string, string> ToBareNameKeyed(
            IReadOnlyDictionary<string, string> byQualifiedName)
        {
            var byBareName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (qualifiedName, value) in byQualifiedName)
            {
                byBareName[MechanicalValidator.BareObjectName(qualifiedName)] = value;
            }

            return byBareName;
        }
    }
}
