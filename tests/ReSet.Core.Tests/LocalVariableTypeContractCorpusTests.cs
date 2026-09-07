using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// `CheckLocalVariableTypeContract`가 실물 코퍼스에서 <b>발화하는지</b>와
    /// <b>오탐이 다수가 아닌지</b>를 잰다. 단위 시험 통과는 효력이 아니다.
    ///
    /// [왜 <c>--sweep</c> 보고서로 판정하지 않는가] 그 보고서는 검사별·Job별 <b>발화 수</b>만
    /// 싣고 메시지를 안 실어 이 검사가 「미분류」에 합산된다. 그래서 여기서는
    /// <see cref="MechanicalValidator.ValidateBatchStep"/>를 직접 돌려 메시지를 걸러낸다.
    ///
    /// [왜 하한·상한으로 단언하는가] 숫자로 못박으면 코퍼스에 Job이 하나 늘 때마다 빨개지고
    /// 다음 사람이 관측을 읽는 대신 기대값을 고친다
    /// (<see cref="LocalVariableTableCorpusTests"/>·<c>ErrorCodeTableCorpusTests</c>와 같은 근거).
    /// 다만 <b>알려진 네 자리는 이름으로 못박는다</b> - 그것이 이 검사의 효력 자체이고,
    /// 자리가 하나라도 조용해지면 그것은 코퍼스가 자란 것이 아니라 검사가 죽은 것이다.
    ///
    /// [측정 기록] `docs/audit-reports/2026-09-07-지역변수-타입계약-효력측정.md`.
    /// 관측값은 그 문서 한 곳에만 적는다 - 여기에 다시 적으면 둘이 갈라진다.
    /// </summary>
    public class LocalVariableTypeContractCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public LocalVariableTypeContractCorpusTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// 이 검사의 발화 문구를 가리는 자. 두 조각을 <b>함께</b> 요구한다 - 「타입 계약」만으로는
        /// 이름 기반 검사(<c>CheckSpecLocalVariablesDeclared</c>)의 문구와 섞인다.
        /// </summary>
        private static bool IsTypeContractFiring(string message)
            => message.Contains("타입 계약", StringComparison.Ordinal)
               && message.Contains("드라이버 바인딩", StringComparison.Ordinal);

        [SkippableFact]
        public void TypedConstantBoundToDriver_FiresOnEveryKnownLeak()
        {
            var corpus = CorpusRun.Measure();
            Skip.If(corpus == null, CorpusSkip.Reason);

            foreach (var f in corpus!.Firings) _output.WriteLine(f);
            _output.WriteLine($"[잰 단계 쌍] {corpus.MeasuredPairs} · [목차 미파싱 Job] " +
                              string.Join(", ", corpus.PlanParseFailures));

            // [도달 가능성 - 값 0을 게이트 통과시키지 않기 위한 바닥]
            // 이 자리가 막는 것은 「로더가 조용히 망가져 단계를 하나도 안 돌았는데 아래
            // 발화 단언만 빨개져 원인이 『검사가 죽었다』로 오독되는 것」이다.
            //
            // [2026-09-07 - 자를 크기에서 모양으로 바꿨다]
            // 원래 이 자리는 `MeasuredPairs >= 300`이었다. 그 300은 Job 21편 코퍼스에서
            // 나온 수인데, 사람이 반복 생성 표본 21편을 지우기로 결정해 코퍼스가 4편이
            // 됐다(실측 76쌍). **그 하한은 이제 코퍼스의 크기를 재지 로더의 건강을 재지
            // 않는다.** 낮춰 잡으면 다음에 코퍼스가 또 바뀔 때 같은 일이 반복되고, 그때마다
            // 「초록을 만들려고 하한을 내렸다」와 구별되지 않는다.
            //
            // 그래서 크기 대신 **모양**을 못박는다. 아래 셋은 코퍼스가 4편이든 40편이든
            // 같은 것을 주장하고, 로더가 죽으면 크기 하한보다 먼저·정확하게 빨개진다.
            //   ① 목차를 못 읽은 Job이 없다      ② 모든 Job이 최소 한 쌍을 냈다
            //   ③ Job 하나도 없는 상태를 통과시키지 않는다
            // 경위: docs/audit-reports/2026-09-07-과거판-코퍼스-폐기.md
            var jobsOnDisk = Directory.GetDirectories(corpus.JobsDir)
                .Select(d => Path.GetFileName(d) ?? string.Empty)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            var jobsMeasured = corpus.VisitedSteps
                .Select(rel => rel.Split('/')[2])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.True(corpus.PlanParseFailures.Count == 0,
                "목차를 못 읽은 Job이 있다 - 그 Job의 단계는 검사가 아예 안 돌아 자동으로 조용하다: " +
                string.Join(", ", corpus.PlanParseFailures));
            Assert.True(jobsOnDisk.Count > 0, "output/Jobs 가 비어 있다 - 코퍼스나 로더가 바뀌었다.");
            Assert.Equal(jobsOnDisk, jobsMeasured);
            Assert.True(corpus.MeasuredPairs >= jobsMeasured.Count,
                $"Job {jobsMeasured.Count}편인데 잰 단계 쌍이 {corpus.MeasuredPairs}뿐이다.");

            // [뒤 층 4: 발화한다] 알려진 네 자리. 넷째(`Batch1/S05`)는 **한 겹 간접**이라
            // 바인딩 값 자리에 리터럴이 없다 - 「리터럴이 있는가」로 재면 이 자리는 안 잡힌다.
            Assert.Contains(corpus.Firings, f => f.Contains("POQSettleBatch1/agent/steps/S05.md"));
            Assert.Contains(corpus.Firings, f => f.Contains("POQSettleBatch4/agent/steps/S06.md"));
            Assert.Contains(corpus.Firings, f => f.Contains("POQSettleBatch5/agent/steps/S05.md"));
            Assert.Contains(corpus.Firings, f => f.Contains("POQSettleBatch5/agent/steps/S07.md"));

            // [뒤 층 5: 오탐이 다수가 아니다]
            // **이 상한이 세는 것은 오류 메시지 하나하나다** - 파일도 변수도 아니다.
            // 한 단계가 두 변수를 유출하면 2로 센다. 오늘 관측은 메시지 4 · 파일 4로 같지만
            // 같은 자가 아니다(수가 우연히 같은 것을 「같은 자」로 읽는 것이 이 저장소가
            // 반복해 물린 자리다). 상한을 넘으면 초과분을 **전부 열어 보고** 진짜인지
            // 판정한 뒤에 이 수를 올려라 - 열어 보지 않고 올리는 것이 오탐 배송이다.
            Assert.True(corpus.Firings.Count <= 8,
                $"발화가 {corpus.Firings.Count}건이다. 전부 열어 보고 진짜인지 판정한 뒤 상한을 갱신하라:\n" +
                string.Join("\n", corpus.Firings));
        }

        [SkippableFact]
        public void PreservedSites_StaySilent()
        {
            var corpus = CorpusRun.Measure();
            Skip.If(corpus == null, CorpusSkip.Reason);

            // [왜 `DECLARE @v_valIncVat` 이름 grep을 쓰지 않는가]
            // 이 설계가 스스로 금지한 자다 - 모델이 개명하면 눈이 먼다. 실측:
            // 이름 grep은 `Proc15/S10`의 `DECLARE @v_incVat DECIMAL(2,1) = 1.1;`을 **놓친다.**
            // 그래서 보존 자리는 **이름 눈먼 자**로 뽑는다 -
            // 리터럴 오라클도 하드코딩하지 않고 원본 DDL에서 뽑은 것을 그대로 쓴다.
            var preserved = CorpusRun.PreservedSites(corpus!);
            foreach (var p in preserved) _output.WriteLine($"[보존] {p}");

            // 하한. 잡으려는 것은 **추출기나 오라클이 조용히 비는 회귀**다.
            //
            // [2026-09-07 - 자를 크기에서 모양으로 바꿨다]
            // 원래는 `preserved.Count >= 20`이었다. 그 20도 Job 21편에서 나온 수이고,
            // 코퍼스가 4편이 되면서 실측이 2가 됐다(위 `TypedConstantBoundToDriver…`의
            // 같은 주석 참고). 크기를 낮춰 다시 못박는 대신, 이 하한이 실제로 노리던
            // 두 가지를 각각 직접 단언한다 - 둘 다 코퍼스 크기와 무관하다.
            //   ① 오라클이 비지 않았다(원본 DDL에서 관할 상수를 하나도 못 뽑으면
            //      보존 자리는 정의상 0이 되고 아래 침묵 단언이 공허해진다)
            //   ② 이름 눈먼 추출기가 비지 않았다
            // 「침묵이 공허하지 않은가」의 진짜 바닥은 별개 시험
            // (`PreservedSilence_IsNotVacuous…`의 `reach.Reached >= 2`)이 맡는다.
            Assert.True(corpus.OracleLiterals.Count > 0,
                "원본 DDL에서 관할 상수를 하나도 못 뽑았다 - 오라클이 비었다.");
            Assert.True(preserved.Count > 0,
                $"이름 눈먼 자로 센 보존 자리가 하나도 없다. " +
                $"오라클 리터럴 = [{string.Join(", ", corpus.OracleLiterals)}]");

            // [「침묵했다」를 공허하게 만들지 않는 자]
            // 목차가 파싱 안 되는 Job의 단계는 검사가 애초에 안 돌아 자동으로 조용하다.
            // 그것을 「침묵」에 합산하면 값 0을 게이트 통과시키는 바로 그 실패 양식이 된다.
            // 그래서 보존 자리가 **전부 실제로 검사를 통과했는지**를 먼저 못박는다.
            var unvisited = preserved.Where(p => !corpus.VisitedSteps.Contains(p)).ToList();
            Assert.True(unvisited.Count == 0,
                "보존 자리인데 검사가 아예 안 돈 단계가 있다 - 침묵이 공허하다:\n" +
                string.Join("\n", unvisited));

            var noisy = preserved.Where(p => corpus.FiredSteps.Contains(p)).ToList();
            Assert.True(noisy.Count == 0,
                "`DECLARE`로 보존한 자리인데 이 검사가 발화했다:\n" + string.Join("\n", noisy));
        }

        /// <summary>
        /// 보존 자리의 침묵이 <b>공허하지 않은지</b>를 따로 잰다.
        ///
        /// [왜 별개의 시험인가 - 실측으로 드러난 것]
        /// 「보존 자리 전량 침묵」은 오늘 참이지만, 그 침묵의 대부분은 <b>검사의 판단이
        /// 아니다.</b> 실측하면 보존 자리의 절대다수가 <see cref="CheckReach"/>가 세는
        /// 세 가지 조기 반환 중 하나에 걸려 대조까지 가지도 못한다 - 목차의
        /// `LegacyProcedures`가 비었거나, 단계에 바인딩이 하나도 없거나, 그 프로시저
        /// DDL에 관할 상수가 없다.
        ///
        /// <b>「바인딩 0」을 「의사코드 펜스가 없다」로 읽지 마라.</b> 그 둘은 다른
        /// 주장이고 뒤엣것은 거짓이다(실측: 미도달 자리 중 `pseudocode` 펜스를 가진
        /// 반례가 둘 있다). 참인 기전은 <b>`sql`이 아닌 펜스에 일곱 호출 이름이 하나도
        /// 없다</b>는 것이다 - 펜스의 유무가 아니라 <b>호출의 유무</b>다. `csharp` 펜스도
        /// <see cref="StepBindingExtractor"/>가 읽으므로, 재생성이 그런 펜스에
        /// `execute(...)`를 넣는 순간 그 자리들이 곧바로 도달권에 들어온다.
        ///
        /// 그런 자리는 검사를 <b>지워도</b> 똑같이 조용하다. 그러므로 그것을 오탐 증거로
        /// 세면 「없앴을 때로 잰다」를 어긴 것이고, 이 저장소가 반복해 물린 값 0의
        /// 게이트 통과가 된다. 실제 오탐 증거는 <b>대조까지 간 자리</b>뿐이므로, 그 수가
        /// 0으로 떨어지면 위 침묵 시험은 아무것도 안 잠그게 된다 - 이 시험이 그 바닥이다.
        ///
        /// 관측값은 `docs/audit-reports/2026-09-07-지역변수-타입계약-효력측정.md`에 있다.
        /// </summary>
        [SkippableFact]
        public void PreservedSilence_IsNotVacuous_SomeSitesActuallyReachTheComparison()
        {
            var corpus = CorpusRun.Measure();
            Skip.If(corpus == null, CorpusSkip.Reason);

            var preserved = CorpusRun.PreservedSites(corpus!);
            var reach = CorpusRun.CheckReach(corpus!, preserved);

            foreach (var line in reach.Explanations) _output.WriteLine(line);

            // 이 자가 세는 것은 **검사가 조기 반환에 걸리지 않고 리터럴 대조까지 간
            // 보존 단계 파일의 수**다. 「보존 자리 수」도 「침묵 수」도 아니다.
            Assert.True(reach.Reached >= 2,
                $"보존 자리 {preserved.Count} 중 대조까지 간 것이 {reach.Reached}뿐이다 - " +
                "침묵 시험이 공허해진다. 아래 사유별 내역을 보고 코퍼스가 무엇이 바뀌었는지 판정하라:\n" +
                string.Join("\n", reach.Explanations));
        }

        /// <summary>
        /// 단계 하나가 <see cref="MechanicalValidator"/>의 타입 계약 검사에서
        /// <b>리터럴 대조까지 가는지</b>를 가르는 세 재료.
        ///
        /// <b>이것은 검사의 조기 반환을 본뜬 별개의 자다</b> - 검사 자신이 「어디까지
        /// 갔는지」를 밖으로 내지 않아서, 그것을 알려면 같은 조건을 여기서 다시 세는
        /// 수밖에 없다. 두 자가 갈라질 수 있다는 사실을 숨기지 않는다. 검사의 조기 반환이
        /// 바뀌면 <b>여기도 함께 고쳐야 한다</b>. 다만 이 자는 판정(발화·침묵)에는 안 쓰이고
        /// <b>도달 가능성</b>에만 쓰이므로, 갈라져도 결함을 놓치는 쪽이 아니라 도달 수가
        /// 어긋나는 쪽으로 틀린다.
        /// </summary>
        private sealed record StepReach(int LegacyProcedures, int Bindings, int QualifyingConstants)
        {
            public static StepReach Of(
                BatchStepPlan step, string markdown, IReadOnlyDictionary<string, string> ddl)
            {
                var constants = 0;
                foreach (var procedure in step.LegacyProcedures)
                {
                    var bare = StepSweepService.BareProcedureName(procedure);
                    if (!ddl.TryGetValue(bare, out var text)) continue;

                    var literals = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    CorpusRun.CollectOracleLiteralsOf(text, literals);
                    constants += literals.Count;
                }

                return new StepReach(
                    step.LegacyProcedures?.Count ?? 0,
                    StepBindingExtractor.Extract(markdown).Count,
                    constants);
            }
        }

        /// <summary>
        /// 코퍼스 한 판. <c>SweepCommand.Run</c>의 적재 규약을 그대로 본뜬다 - 갈라지면
        /// 파이프라인이 실제로 하지 않는 판정을 재게 된다.
        ///
        /// [키잉이 이 하네스의 유일한 함정이었다 - 실측 기록]
        /// `ddlByProcedure`를 디렉터리 이름(`dbo.UP_X`)으로 키잉하면 검사가
        /// <c>BareObjectName</c>으로 조회해 **전건 빗나가고 발화가 0**이 된다(이 자리에서
        /// 실제로 한 번 났다). 스윕이 <c>ToBareNameKeyed</c>로 맞추는 것과 같은 규약으로
        /// **맨이름**을 깐다. 발화 0은 「결함이 없다」가 아니라 대개 여기가 어긋난 것이다.
        /// </summary>
        private sealed record CorpusRun(
            IReadOnlyList<string> Firings,
            ISet<string> FiredSteps,
            ISet<string> VisitedSteps,
            ISet<string> OracleLiterals,
            IReadOnlyList<string> PlanParseFailures,
            IReadOnlyDictionary<string, StepReach> ReachByStep,
            int MeasuredPairs,
            string JobsDir,
            string Root)
        {
            private static readonly JsonSerializerOptions JsonOptions =
                new() { PropertyNameCaseInsensitive = true };

            private static readonly IReadOnlyDictionary<string, SpecConditions> NoConditions =
                new Dictionary<string, SpecConditions>(StringComparer.OrdinalIgnoreCase);

            /// <summary>코퍼스가 없으면 null.</summary>
            public static CorpusRun? Measure()
            {
                var root = CorpusPaths.RepoRoot();
                if (string.IsNullOrEmpty(root)) return null;

                var outputDir = Path.Combine(root, "output");
                var jobsDir = Path.Combine(outputDir, "Jobs");
                var proceduresDir = Path.Combine(outputDir, "Procedures");
                if (!Directory.Exists(jobsDir) || !Directory.Exists(proceduresDir)) return null;

                var procedureDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in Directory.GetDirectories(proceduresDir))
                {
                    var bare = StepSweepService.BareProcedureName(Path.GetFileName(dir));
                    if (bare.Length > 0) procedureDirs[bare] = dir;
                }

                var validator = new MechanicalValidator();
                var firings = new List<string>();
                var firedSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var literals = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var planParseFailures = new List<string>();
                var reachByStep = new Dictionary<string, StepReach>(StringComparer.OrdinalIgnoreCase);
                var pairs = 0;

                foreach (var jobDir in Directory.GetDirectories(jobsDir)
                             .OrderBy(d => d, StringComparer.Ordinal))
                {
                    var jobName = Path.GetFileName(jobDir);
                    var planPath = Path.Combine(jobDir, "raw", "PlanStructure.md");
                    var steps = File.Exists(planPath)
                        ? BatchStepPlanParser.TryParse(File.ReadAllText(planPath))
                        : null;
                    if (steps == null || steps.Count == 0)
                    {
                        planParseFailures.Add(jobName);
                        continue;
                    }

                    var ddl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var procedure in steps.SelectMany(s => s.LegacyProcedures)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var bare = StepSweepService.BareProcedureName(procedure);
                        if (!procedureDirs.TryGetValue(bare, out var dir)) continue;

                        var metaPath = Path.Combine(dir, "raw", "metadata.json");
                        if (!File.Exists(metaPath)) continue;

                        var definition = JsonSerializer.Deserialize<SpDefinition>(
                            File.ReadAllText(metaPath), JsonOptions);
                        if (definition == null) continue;

                        ddl[bare] = definition.DdlText ?? string.Empty;
                        CollectOracleLiteralsOf(definition.DdlText, literals);
                    }

                    foreach (var step in steps)
                    {
                        var stepPath = Path.Combine(jobDir, "agent", "steps", step.Code + ".md");
                        if (!File.Exists(stepPath)) continue;

                        var markdown = File.ReadAllText(stepPath);
                        if (string.IsNullOrWhiteSpace(markdown)) continue;

                        var relative = Path.GetRelativePath(root, stepPath).Replace('\\', '/');
                        pairs++;
                        visited.Add(relative);
                        reachByStep[relative] = StepReach.Of(step, markdown, ddl);

                        var result = validator.ValidateBatchStep(
                            markdown, step,
                            Array.Empty<string>(),
                            NoConditions,
                            stepInterfaces: null,
                            runRowOwnedTables: null,
                            statementFactsByProcedure: null,
                            allSteps: steps,
                            ddlByProcedure: ddl);

                        foreach (var error in result.Errors.Where(IsTypeContractFiring))
                        {
                            firings.Add($"{relative} :: {error}");
                            firedSteps.Add(relative);
                        }
                    }
                }

                return new CorpusRun(
                    firings, firedSteps, visited, literals, planParseFailures, reachByStep,
                    pairs, jobsDir, root);
            }

            /// <summary>
            /// 보존 자리 중 리터럴 대조까지 실제로 간 것과, 못 간 것의 <b>사유별</b> 내역.
            /// </summary>
            public static (int Reached, IReadOnlyList<string> Explanations) CheckReach(
                CorpusRun run, IReadOnlyList<string> preserved)
            {
                var reached = 0;
                var lines = new List<string>();

                foreach (var site in preserved)
                {
                    if (!run.ReachByStep.TryGetValue(site, out var reach))
                    {
                        lines.Add($"[미도달] {site} - 검사가 아예 안 돈 단계다(목차 미파싱 등)");
                        continue;
                    }

                    if (reach.LegacyProcedures == 0)
                    {
                        lines.Add($"[미도달] {site} - 목차의 LegacyProcedures 가 비었다(오라클이 안 붙는다)");
                    }
                    else if (reach.Bindings == 0)
                    {
                        lines.Add($"[미도달] {site} - 바인딩 0(의사코드 펜스에 호출이 없다)");
                    }
                    else if (reach.QualifyingConstants == 0)
                    {
                        lines.Add($"[미도달] {site} - 이 단계 프로시저 DDL 에 관할 상수가 없다");
                    }
                    else
                    {
                        reached++;
                        lines.Add($"[도달] {site} - 바인딩 {reach.Bindings} · 관할 상수 {reach.QualifyingConstants}");
                    }
                }

                return (reached, lines);
            }

            /// <summary>
            /// 이 검사가 실제로 맞대는 리터럴. 하드코딩하지 않고 원본 DDL에서 뽑는다 -
            /// 보존 자를 상수 `1.1`로 짜면 코퍼스가 바뀌는 날 자와 검사가 갈라진다.
            /// 필터는 검사(<c>IsPrecisionBearingNumeric</c>·<c>IsZeroLiteral</c>)와 같은 축이다.
            /// </summary>
            public static void CollectOracleLiteralsOf(string? ddlText, ISet<string> into)
            {
                foreach (var fact in LocalVariableDeclarationExtractor.ExtractConstants(ddlText))
                {
                    var type = (fact.DataType ?? string.Empty).ToUpperInvariant();
                    var precise = type.StartsWith("DECIMAL", StringComparison.Ordinal)
                                  || type.StartsWith("NUMERIC", StringComparison.Ordinal)
                                  || type.StartsWith("FLOAT", StringComparison.Ordinal)
                                  || type.StartsWith("REAL", StringComparison.Ordinal)
                                  || type.StartsWith("MONEY", StringComparison.Ordinal)
                                  || type.StartsWith("SMALLMONEY", StringComparison.Ordinal);
                    if (!precise) continue;

                    var value = (fact.InitialValue ?? string.Empty).Trim();
                    if (value.Length == 0) continue;
                    if (decimal.TryParse(value, out var number) && number == 0m) continue;

                    into.Add(value);
                }
            }

            private static readonly Regex FencePattern = new(
                @"```(?<lang>[a-zA-Z]*)\r?\n(?<body>.*?)```", RegexOptions.Singleline);

            /// <summary>
            /// 이름 눈먼 보존 자: `DECLARE @&lt;아무이름&gt; &lt;정밀도타입&gt; = &lt;오라클 리터럴&gt;`.
            /// 변수 이름을 쓰지 않는 것이 요점이다.
            /// </summary>
            private static readonly Regex PreservedDeclarationPattern = new(
                @"DECLARE\s+@[A-Za-z_0-9]+\s+(DECIMAL|NUMERIC|FLOAT|REAL|MONEY|SMALLMONEY)\b[^=\r\n]*=\s*(?<literal>[^\s;,\r\n]+)",
                RegexOptions.IgnoreCase);

            /// <summary>
            /// 리터럴을 `DECLARE`로 <b>보존한</b> 단계 파일. 발화 자리와 이 집합이 오늘
            /// 서로소인 것은 우연이 아니라 결함의 정의다(유출은 `DECLARE`를 안 둔 것이니까).
            /// <b>고치는 중에는 그 서로소가 깨진다</b>(설계 §8 「반쪽 이행」).
            /// </summary>
            public static IReadOnlyList<string> PreservedSites(CorpusRun run)
            {
                var sites = new List<string>();

                foreach (var file in Directory
                             .EnumerateFiles(run.JobsDir, "*.md", SearchOption.AllDirectories)
                             .Where(p => p.Replace('\\', '/').Contains("/agent/steps/"))
                             .OrderBy(p => p, StringComparer.Ordinal))
                {
                    var text = File.ReadAllText(file);
                    var hit = FencePattern.Matches(text)
                        .SelectMany(fence => PreservedDeclarationPattern
                            .Matches(fence.Groups["body"].Value)
                            .Select(m => m.Groups["literal"].Value.Trim()))
                        .Any(run.OracleLiterals.Contains);

                    if (hit) sites.Add(Path.GetRelativePath(run.Root, file).Replace('\\', '/'));
                }

                return sites;
            }
        }
    }
}
