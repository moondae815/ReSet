using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReSet.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 단계 본문의 <c>SELECT n:</c> 앵커 - 블록형 <c>/* SELECT n: … */</c> 와 대시형
    /// <c>-- SELECT n: … </c> 둘 다 - 가 <b>그 단계 레거시 SP 명세서의 DML 범위 표에
    /// 실재하는가</b>를 코퍼스 전수로 잰다.
    ///
    /// [왜 <see cref="AnchorKindOrdinalPairTests"/> 가 못 잡나] 그 자는 문장에 붙은 앵커만
    /// 본다. SELECT 는 리더가 문장으로 담지도, 앵커로 읽지도 않는다(설계서 §2 세 겹) - 불일치로는
    /// <b>절대</b> 드러나지 않는 조용한 공백이다.
    ///
    /// [한계 - 아는 채로 쓴다] 라벨이 명세서에 있는지만 본다. 그 라벨이 옳은 문장에
    /// 붙었는지는 못 본다. <b>그보다 큰 구멍</b>: 선언이 있는데 앵커가 아예 없는 단계는
    /// 이 단언으로 절대 드러나지 않는다(불일치 표는 「있는 앵커」만 검사하고, 「없는
    /// 앵커」는 대조할 대상 자체가 없다). 그 방향은 <see cref="LogsStepsWhereTheSpecDeclaresSelectRowsButNoAnchorWasWritten"/>
    /// 가 잰다. 설계서 §7·§9-4 참고.
    ///
    /// 설계: docs/superpowers/specs/2026-09-12-SELECT앵커-대조-설계.md
    /// </summary>
    public class SelectAnchorPairCorpusTests
    {
        private static readonly Regex DashFormLine = new(
            @"^[ \t]*--[ \t]*SELECT[ \t]*\d{1,2}[ \t]*:", RegexOptions.Multiline);

        private static readonly Regex BlockFormLine = new(
            @"^[ \t]*/\*[ \t]*SELECT[ \t]*\d{1,2}[ \t]*:", RegexOptions.Multiline);

        private readonly ITestOutputHelper _output;

        public SelectAnchorPairCorpusTests(ITestOutputHelper output) => _output = output;

        [SkippableFact]
        public void EverySelectAnchorPointsAtARowTheSpecActuallyDeclares()
        {
            var (facts, jobsDir) = LoadCorpus();

            var mismatches = new List<string>();
            var scanned = 0;
            var dashFormAnchors = 0;
            var blockFormAnchors = 0;
            var perJob = new List<(string Job, int Scanned, int Mismatches)>();

            foreach (var jobDir in Directory.EnumerateDirectories(jobsDir)
                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                var plan = Path.Combine(jobDir, "raw", "PlanStructure.md");
                var stepsDir = Path.Combine(jobDir, "agent", "steps");
                if (!File.Exists(plan) || !Directory.Exists(stepsDir)) continue;

                var steps = BatchStepPlanParser.TryParse(File.ReadAllText(plan));
                if (steps == null) continue;

                var jobScanned = 0;
                var jobMismatches = 0;

                foreach (var step in steps)
                {
                    var file = Path.Combine(stepsDir, step.Code + ".md");
                    if (!File.Exists(file)) continue;

                    var text = File.ReadAllText(file);
                    var declared = DeclaredSelectOrdinals(facts, step);
                    var anchors = StepSqlStatementReader.ReadSelectAnchors(text);

                    if (anchors.Count > 0)
                    {
                        // 표기 갈래는 리더가 실제로 앵커를 읽어낸 파일에서만 센다 - 리더의
                        // 정규식이 한쪽 표기를 못 읽게 좁혀지면(뮤테이션 검증 대상) 그 갈래의
                        // 파일은 anchors.Count == 0 이 되어 여기 도달하지 않고, 아래 바닥
                        // 단언이 그 갈래에서 빨개진다.
                        if (DashFormLine.IsMatch(text)) dashFormAnchors += anchors.Count;
                        if (BlockFormLine.IsMatch(text)) blockFormAnchors += anchors.Count;
                    }

                    foreach (var ordinal in anchors)
                    {
                        scanned++;
                        jobScanned++;

                        if (declared.Contains(ordinal)) continue;

                        jobMismatches++;
                        mismatches.Add(
                            $"{Path.GetFileName(jobDir)}/{step.Code} · SELECT 앵커 {ordinal} — " +
                            $"명세서에 그 행이 없다(그 단계 SP 가 선언한 SELECT 서수: " +
                            $"{(declared.Count == 0 ? "없음" : string.Join(", ", declared.OrderBy(x => x)))})");
                    }
                }

                if (jobScanned > 0) perJob.Add((Path.GetFileName(jobDir), jobScanned, jobMismatches));
            }

            _output.WriteLine($"SELECT 앵커 {scanned} · 불일치 {mismatches.Count}");
            _output.WriteLine($"  표기별: `/* … */` {blockFormAnchors} · `-- …` {dashFormAnchors}");
            foreach (var job in perJob)
            {
                _output.WriteLine($"  [Job] {job.Job} · SELECT 앵커 {job.Scanned} · 불일치 {job.Mismatches}");
            }
            foreach (var mismatch in mismatches) _output.WriteLine("  " + mismatch);

            // 재료가 0 이면 이 단언은 아무것도 안 지킨다 - 값 0 을 게이트 통과시키지 않는다.
            Skip.If(scanned == 0, CorpusSkip.Reason);

            Assert.Empty(mismatches);

            // 모양 불변식(바닥) - 코퍼스는 두 표기를 모두 쓴다. `SelectAnchorPattern`이
            // 한쪽 표기(예: `/\*`만)로 좁혀지면 그 갈래의 파일은 리더에서 anchors.Count == 0
            // 이 되어 위 표기별 합계 중 하나가 0으로 떨어진다 - 여기서 빨갛게 잡는다.
            // (되돌림으로 확인함: 설계서 §9-2 다섯째 행 참고.)
            Assert.True(blockFormAnchors > 0, "`/* SELECT n: */` 표기 앵커가 0 - 리더 정규식이 이 갈래를 놓치고 있다.");
            Assert.True(dashFormAnchors > 0, "`-- SELECT n:` 표기 앵커가 0 - 리더 정규식이 이 갈래를 놓치고 있다.");

            // 귀속 불변식 - 표기별 계수기는 "그 파일 텍스트 전체에 그 표기 줄이 있으면
            // anchors.Count 를 통째로 더한다"는 방식이라, 오늘은 어느 파일도 두 표기를
            // 섞어 쓰지 않기 때문에만 blockFormAnchors + dashFormAnchors == scanned 가
            // 성립한다. 표기를 섞은 파일이 하나라도 생기면 그 파일의 anchors.Count 가
            // 두 계수기 모두에 이중으로 더해져 합이 scanned 를 넘고, 위 두 `> 0` 단언은
            // 계속 초록이면서도 §9-2 다섯째 행의 좁히기 뮤테이션에서 한쪽 표기가 실제로는 0 인데도
            // 다른 파일의 이중 계산이 가려 바닥이 약해진다. 이 단언은 그 전제(파일당
            // 단일 표기)가 깨지는 순간 여기서 먼저 빨개져, 위 두 단언을 못 믿게 됐다는
            // 것을 알린다.
            Assert.True(
                scanned == blockFormAnchors + dashFormAnchors,
                $"표기별 합({blockFormAnchors} + {dashFormAnchors})이 scanned({scanned})와 다르다 - " +
                "어느 파일이 두 표기를 섞어 써서 이중으로 세었거나(위 문단 참고), 표기별 계수기가 " +
                "놓친 파일이 있다는 뜻이다. 표기 귀속을 더는 못 믿는다.");
        }

        /// <summary>
        /// 이 검사가 놓치는 방향(§7)을 매 실행마다 찍는다 - 단언하지 않는다. 명세서에
        /// SELECT 행이 선언돼 있는데 그 단계 본문에 SELECT 앵커가 하나도 없는 단계를
        /// 「도달 못 한 단계」로 센다. 오늘 값은 설계서 §7·§9-4 에 적은 값과 같아야 한다.
        ///
        /// [행 단위 지표 - §9-4 항목 2] <c>missedDeclarationRows</c> 는 「그 단계가
        /// 선언한 SELECT 서수 중 앵커가 안 붙은 것의 개수 합」이다 - <b>도달 못 한
        /// 단계뿐 아니라 도달한 단계도</b> 포함한다. 도달한 단계라도 선언 서수 중
        /// 일부에만 앵커를 달면 나머지는 여전히 놓친 것이다(§9-4 가 든 예: `SELECT
        /// 1~6` 을 선언한 단계가 앵커 하나만 달면 「도달」로는 세어지지만 놓친 다섯
        /// 행이 있다). 오늘은 도달한 여섯 단계가 전부 선언을 남김없이 덮어 그 기여가
        /// 0 이므로 이 값은 <c>unreachedSteps</c> 만 더한 것과 우연히 같다(24) -
        /// <b>부분 미도달이 하나라도 생기는 날 이 수는 커진다.</b>
        /// </summary>
        [SkippableFact]
        public void LogsStepsWhereTheSpecDeclaresSelectRowsButNoAnchorWasWritten()
        {
            var (facts, jobsDir) = LoadCorpus();

            var reached = 0;
            var unreachedSteps = new List<(string Job, string Step, IReadOnlyCollection<int> Declared)>();
            var missedDeclarationRows = 0;

            // [Job 별로 가르는 이유 - 2026-09-12]
            // 전역 도달률은 「계약 이전에 만들어진 Job 넷」과 「계약 이후에 만들어진
            // Job」을 한 분모에 섞는다. 계약이 완벽히 들어도 전역 값은 옛 Job 들에
            // 눌려 조금밖에 안 올라가므로, 그 수로는 계약의 효력을 판정할 수 없다.
            // 판정이 필요한 것은 언제나 「그 계약 뒤에 생성된 Job 하나」다.
            var perJobReached = new Dictionary<string, int>(StringComparer.Ordinal);
            var perJobTotal = new Dictionary<string, int>(StringComparer.Ordinal);
            var perJobMissedRows = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var jobDir in Directory.EnumerateDirectories(jobsDir)
                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                var plan = Path.Combine(jobDir, "raw", "PlanStructure.md");
                var stepsDir = Path.Combine(jobDir, "agent", "steps");
                if (!File.Exists(plan) || !Directory.Exists(stepsDir)) continue;

                var steps = BatchStepPlanParser.TryParse(File.ReadAllText(plan));
                if (steps == null) continue;

                foreach (var step in steps)
                {
                    var file = Path.Combine(stepsDir, step.Code + ".md");
                    if (!File.Exists(file)) continue;

                    var declared = DeclaredSelectOrdinals(facts, step);
                    if (declared.Count == 0) continue;

                    var anchors = StepSqlStatementReader.ReadSelectAnchors(File.ReadAllText(file));
                    var anchorOrdinals = new HashSet<int>(anchors);

                    // §9-4 항목 2 - 도달 여부와 무관하게, 그 단계가 선언한 서수 중
                    // 앵커가 안 붙은 것을 전부 센다. 도달 못 한 단계는 anchorOrdinals
                    // 가 비어 있으므로 declared 전체가 그대로 여기 잡힌다 - 예전
                    // `unreachedSteps.Sum(s => s.Declared.Count)` 와 그 부분에서는
                    // 같은 값을 낸다.
                    var missedHere = declared.Count(ordinal => !anchorOrdinals.Contains(ordinal));
                    missedDeclarationRows += missedHere;

                    var jobName = Path.GetFileName(jobDir);
                    perJobMissedRows[jobName] = perJobMissedRows.GetValueOrDefault(jobName) + missedHere;
                    perJobTotal[jobName] = perJobTotal.GetValueOrDefault(jobName) + 1;

                    if (anchors.Count > 0)
                    {
                        reached++;
                        perJobReached[jobName] = perJobReached.GetValueOrDefault(jobName) + 1;
                    }
                    else
                    {
                        unreachedSteps.Add((jobName, step.Code, declared));
                    }
                }
            }

            var total = reached + unreachedSteps.Count;

            Skip.If(total == 0, CorpusSkip.Reason);

            _output.WriteLine(
                $"[역방향 - §7] 선언 있음·앵커 0 인 단계 {unreachedSteps.Count} · " +
                $"놓친 선언 행 합계(부분 미도달 포함) {missedDeclarationRows} · 도달률 {reached}/{total}");
            foreach (var job in perJobTotal.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"  [Job] {job} · 도달률 {perJobReached.GetValueOrDefault(job)}/{perJobTotal[job]} · " +
                    $"놓친 선언 행 {perJobMissedRows.GetValueOrDefault(job)}");
            }
            foreach (var s in unreachedSteps.OrderBy(s => s.Job, StringComparer.Ordinal).ThenBy(s => s.Step, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"  [역방향] {s.Job}/{s.Step} · 명세서 선언 SELECT 서수 {string.Join(", ", s.Declared.OrderBy(x => x))} · 앵커 0");
            }
        }

        private static (IReadOnlyDictionary<string, SpecStatementFacts> Facts, string JobsDir) LoadCorpus()
        {
            var root = CorpusPaths.RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var outputRoot = Path.Combine(root, "output");
            var jobsDir = Path.Combine(outputRoot, "Jobs");
            Skip.IfNot(Directory.Exists(jobsDir), CorpusSkip.Reason);

            var facts = SpecStatementFactsExtractor.Extract(ReadSpecs(outputRoot));
            Skip.If(facts.Count == 0, CorpusSkip.Reason);

            return (facts, jobsDir);
        }

        private static HashSet<int> DeclaredSelectOrdinals(
            IReadOnlyDictionary<string, SpecStatementFacts> facts, BatchStepPlan step)
        {
            var declared = new HashSet<int>();
            foreach (var procedure in step.LegacyProcedures)
            {
                if (!facts.TryGetValue(BareName(procedure), out var procedureFacts)) continue;
                foreach (var row in procedureFacts.DmlRows)
                {
                    if (row.Kind.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                    {
                        declared.Add(row.Ordinal);
                    }
                }
            }

            return declared;
        }

        private static IReadOnlyList<(string FileName, string Content)> ReadSpecs(string outputRoot)
        {
            var specs = new List<(string, string)>();
            foreach (var file in Directory.EnumerateFiles(outputRoot, "Spec.md", SearchOption.AllDirectories))
            {
                var normalized = file.Replace('\\', '/');
                if (!normalized.Contains("/docs/") || normalized.Contains("/Jobs/")) continue;

                var objectDir = Path.GetDirectoryName(Path.GetDirectoryName(file));
                if (objectDir == null) continue;

                specs.Add((Path.GetFileName(objectDir), File.ReadAllText(file)));
            }

            return specs;
        }

        private static string BareName(string? qualified)
        {
            if (string.IsNullOrWhiteSpace(qualified)) return string.Empty;
            var dot = qualified.LastIndexOf('.');
            return dot < 0 ? qualified : qualified[(dot + 1)..];
        }
    }
}
