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
    /// 단계 본문의 U-앵커가 가리키는 <c>(Kind, 서수)</c> 가 <b>그 단계의 레거시 SP 명세서
    /// DML 범위 표에 실재하는가</b>를 코퍼스 전수로 잰다.
    ///
    /// 사전 선언: docs/audit-reports/2026-09-08-앵커쌍-탐지기-사전선언.md
    ///
    /// [★ 왜 필요한가 - 실물로 겪었다]
    /// 없는 쌍을 가리키는 앵커는 <b>발화도 오류도 안 낸다</b> - 앵커 계열 검사(B·C·D)가
    /// 대조할 행을 못 찾고 그냥 지나가므로 <b>커버리지가 조용히 사라진다.</b> 앵커가 아예
    /// 없어 검사가 꺼진 것보다 나쁘다 - <b>있어 보이기까지 한다.</b>
    ///
    /// 2026-09-06 판독 §9 가 이 수를 「코퍼스 전체 0」이라 적고 지나갔는데 2026-09-08 에
    /// 재니 <b>2</b> 였고(<c>POQSettleBatch5/S13</c> 이 `/* U13-DELETE 1: … */` 로 적어
    /// <c>\bU</c> 가 <c>U13</c> 에 먼저 맞았다), 그 「0」이 결함을 나흘 숨겼다.
    /// <b>「0 이다」를 근거로 축을 닫지 마라 - 그 자를 자동으로 돌려라.</b>
    ///
    /// [자 - 두 제품 리더의 교차 확인이다]
    /// 기대값을 이 시험이 재구현하지 않는다. <see cref="StepSqlStatementReader"/> 가 내는
    /// 앵커와 <see cref="SpecStatementFactsExtractor"/> 가 내는 DML 행을 맞대므로
    /// <b>둘이 서로를 검산</b>한다.
    ///
    /// [크기가 아니라 모양을 못박는다]
    /// 「앵커가 262 다」로 박으면 코퍼스가 자랄 때마다 빨개지고 다음 사람이 관측 대신
    /// 기대값을 고친다. 여기서는 <b>불일치 0</b> 만 요구한다 - 코퍼스가 자라도 0 이면 초록이고,
    /// 빨개지는 것은 <b>앵커가 엉뚱한 행을 가리킬 때뿐</b>이다.
    /// </summary>
    public class AnchorKindOrdinalPairTests
    {
        private readonly ITestOutputHelper _output;

        public AnchorKindOrdinalPairTests(ITestOutputHelper output) => _output = output;

        [SkippableFact]
        public void EveryUAnchorPointsAtAStatementTheSpecActuallyDeclares()
        {
            var root = CorpusPaths.RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var outputRoot = Path.Combine(root, "output");
            var jobsDir = Path.Combine(outputRoot, "Jobs");
            Skip.IfNot(Directory.Exists(jobsDir), CorpusSkip.Reason);

            var facts = SpecStatementFactsExtractor.Extract(ReadSpecs(outputRoot));
            Skip.If(facts.Count == 0, CorpusSkip.Reason);

            var mismatches = new List<string>();
            var anchored = 0;
            var comparedSteps = 0;

            // Job 별 내역 - 되돌림 조건 R2(「앵커 보유 문장 수가 줄면 되돌린다」)는 합계로는
            // 못 잰다. 새 이름으로 Job 을 하나 더 돌리면 합계는 무조건 늘기 때문이다.
            // 대조 대상 Job 마다 (단계 수 · 앵커 보유 문장 · 불일치) 를 따로 찍어 두면
            // 재생성 전후를 같은 자로 맞댈 수 있다.
            var perJob = new List<(string Job, int Steps, int Anchored, int Mismatches)>();

            foreach (var jobDir in Directory.EnumerateDirectories(jobsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var plan = Path.Combine(jobDir, "raw", "PlanStructure.md");
                var stepsDir = Path.Combine(jobDir, "agent", "steps");
                if (!File.Exists(plan) || !Directory.Exists(stepsDir)) continue;

                var steps = BatchStepPlanParser.TryParse(File.ReadAllText(plan));
                if (steps == null) continue;

                var jobSteps = 0;
                var jobAnchored = 0;
                var jobMismatches = 0;

                foreach (var step in steps)
                {
                    var file = Path.Combine(stepsDir, step.Code + ".md");
                    if (!File.Exists(file)) continue;

                    // 레거시 출신이 없거나 그 명세서가 없으면 대조할 오라클이 없다 - 침묵한다.
                    var declared = new HashSet<(string Kind, int Ordinal)>();
                    foreach (var procedure in step.LegacyProcedures)
                    {
                        if (!facts.TryGetValue(BareName(procedure), out var procedureFacts)) continue;
                        foreach (var row in procedureFacts.DmlRows)
                        {
                            declared.Add((row.Kind.ToUpperInvariant(), row.Ordinal));
                        }
                    }
                    if (declared.Count == 0) continue;
                    comparedSteps++;
                    jobSteps++;

                    foreach (var statement in StepSqlStatementReader.Read(File.ReadAllText(file), out _))
                    {
                        if (statement.Anchor == null) continue;
                        anchored++;
                        jobAnchored++;

                        var pair = (statement.Kind.ToUpperInvariant(), statement.Anchor.Value);
                        if (declared.Contains(pair)) continue;

                        jobMismatches++;
                        mismatches.Add(
                            $"{Path.GetFileName(jobDir)}/{step.Code} · {statement.Kind} 앵커 " +
                            $"{statement.Anchor} · 대상 {statement.TargetTable} — 명세서에 그 쌍이 없다");
                    }
                }

                if (jobSteps > 0)
                {
                    perJob.Add((Path.GetFileName(jobDir), jobSteps, jobAnchored, jobMismatches));
                }
            }

            _output.WriteLine($"대조한 단계 {comparedSteps} · 앵커 보유 문장 {anchored} · 불일치 {mismatches.Count}");
            foreach (var job in perJob)
            {
                _output.WriteLine(
                    $"  [Job] {job.Job} · 단계 {job.Steps} · 앵커 보유 문장 {job.Anchored} · 불일치 {job.Mismatches}");
            }
            foreach (var mismatch in mismatches) _output.WriteLine("  " + mismatch);

            // 재료가 0 이면 이 단언은 아무것도 안 지킨다 - 값 0 을 게이트 통과시키지 않는다.
            Skip.If(anchored == 0, CorpusSkip.Reason);

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
