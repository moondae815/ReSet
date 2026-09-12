using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 단계 본문의 <c>/* SELECT n: … */</c> 앵커가 <b>그 단계 레거시 SP 명세서의 DML 범위
    /// 표에 실재하는가</b>를 코퍼스 전수로 잰다.
    ///
    /// [왜 <see cref="AnchorKindOrdinalPairTests"/> 가 못 잡나] 그 자는 문장에 붙은 앵커만
    /// 본다. SELECT 는 리더가 문장으로 담지도, 앵커로 읽지도 않는다(설계서 §2 세 겹) - 불일치로는
    /// <b>절대</b> 드러나지 않는 조용한 공백이다.
    ///
    /// [한계 - 아는 채로 쓴다] 라벨이 명세서에 있는지만 본다. 그 라벨이 옳은 문장에
    /// 붙었는지는 못 본다. 설계서 §7 참고.
    ///
    /// 설계: docs/superpowers/specs/2026-09-12-SELECT앵커-대조-설계.md
    /// </summary>
    public class SelectAnchorPairCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public SelectAnchorPairCorpusTests(ITestOutputHelper output) => _output = output;

        [SkippableFact]
        public void EverySelectAnchorPointsAtARowTheSpecActuallyDeclares()
        {
            var root = CorpusPaths.RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var outputRoot = Path.Combine(root, "output");
            var jobsDir = Path.Combine(outputRoot, "Jobs");
            Skip.IfNot(Directory.Exists(jobsDir), CorpusSkip.Reason);

            var facts = SpecStatementFactsExtractor.Extract(ReadSpecs(outputRoot));
            Skip.If(facts.Count == 0, CorpusSkip.Reason);

            var mismatches = new List<string>();
            var scanned = 0;
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

                    foreach (var ordinal in StepSqlStatementReader.ReadSelectAnchors(
                                 File.ReadAllText(file)))
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
            foreach (var job in perJob)
            {
                _output.WriteLine($"  [Job] {job.Job} · SELECT 앵커 {job.Scanned} · 불일치 {job.Mismatches}");
            }
            foreach (var mismatch in mismatches) _output.WriteLine("  " + mismatch);

            // 재료가 0 이면 이 단언은 아무것도 안 지킨다 - 값 0 을 게이트 통과시키지 않는다.
            Skip.If(scanned == 0, CorpusSkip.Reason);

            Assert.Empty(mismatches);
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
