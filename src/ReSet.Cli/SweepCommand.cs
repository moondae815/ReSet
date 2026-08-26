using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Cli
{
    /// <summary>
    /// DB·AI 없이 output/ 산출물만으로 단계 검사 A~E를 전수 스윕한다.
    ///
    /// 로직은 전부 StepSweepService에 있다. 여기 남는 것은 파일 읽기와 쓰기뿐이다 -
    /// 그래야 스윕 로직을 코퍼스 없이 테스트할 수 있다(CoverageMapCommand가 반대로
    /// 해서 그 테스트가 코퍼스 없으면 Skip으로 조용히 통과한다).
    /// </summary>
    public static class SweepCommand
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new() { PropertyNameCaseInsensitive = true };

        /// <summary>쓴 보고서 경로. 잰 것이 하나도 없으면 null.</summary>
        public static string? Run(string outputDir, string repoRoot)
        {
            var jobsDir = Path.Combine(outputDir, "Jobs");
            if (!Directory.Exists(jobsDir)) return null;

            // 프로시저 디렉터리 색인. step.LegacyProcedures는 스키마 접두사가 있을 때도
            // 없을 때도 있다(실측 314개 중 134개가 접두사 없음). 맨이름으로 찾는다.
            var procedureDirs = IndexProcedureDirectories(outputDir);

            var jobs = new List<SweepJob>();
            var parseFailed = new List<string>();
            var stepCountCapExceeded = new List<string>();
            var missingStepFiles = 0;

            // [번들 세대] 축 B의 기준값은 명세서다. 단계 번들이 명세서보다 낡았으면 이
            // 스윕이 잡은 불일치는 이행 결함이 아니라 세대 차이일 수 있다 -
            // docs/audit-defect-catalog.md 3절이 그 오염을 경고한다. 보고서가 그 판단
            // 재료를 스스로 싣게 파일 mtime 범위를 여기서 모은다(서비스는 디스크를 모른다).
            var stepMtimes = new List<DateTimeOffset>();
            var specMtimes = new List<DateTimeOffset>();
            var unresolvedProcedureDirectoryLookups = 0;

            foreach (var jobDir in Directory
                         .GetDirectories(jobsDir)
                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                var jobName = Path.GetFileName(jobDir);

                var planPath = Path.Combine(jobDir, "raw", "PlanStructure.md");
                var planMarkdown = File.Exists(planPath) ? File.ReadAllText(planPath) : null;
                var steps = planMarkdown != null ? BatchStepPlanParser.TryParse(planMarkdown) : null;

                if (steps == null || steps.Count == 0)
                {
                    // 목차 JSON은 정상 파싱되지만 BatchStepPlanParser.MaxSteps(40) 상한을
                    // 넘어 버려지는 경우와 진짜 파싱 실패를 TryParse의 반환값(null)만으로는
                    // 구분할 수 없다 - 둘 다 null이다. 라벨을 믿고 JSON을 디버깅하러 가면
                    // 헛수고한다(POQSettleProc4가 실제 사례 - JSON은 73단계로 정상
                    // 파싱되지만 상한 때문에 버려진다).
                    if (planMarkdown != null && ExceedsStepCountCap(planMarkdown, out var declaredCount))
                    {
                        stepCountCapExceeded.Add($"{jobName} (선언 {declaredCount}단계)");
                    }
                    else
                    {
                        parseFailed.Add(jobName);
                    }

                    continue;
                }

                var markdownByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var step in steps)
                {
                    var stepPath = Path.Combine(jobDir, "agent", "steps", $"{step.Code}.md");
                    if (!File.Exists(stepPath))
                    {
                        missingStepFiles++;
                        continue;
                    }

                    markdownByCode[step.Code] = File.ReadAllText(stepPath);
                    stepMtimes.Add(File.GetLastWriteTime(stepPath));
                }

                var specs = new List<(string FileName, string Content)>();
                var ddl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var dateParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var procedures = steps
                    .SelectMany(s => s.LegacyProcedures)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var procedure in procedures)
                {
                    var bare = StepSweepService.BareProcedureName(procedure);
                    if (!procedureDirs.TryGetValue(bare, out var dir))
                    {
                        // 프로시저 참조를 못 찾았다 - 카운터 없이 continue하지 않는다.
                        // StepSweepService의 DdlByProcedure 조회 미스와 합산돼
                        // HarnessGaps.UnresolvedProcedureReferences에 실린다.
                        unresolvedProcedureDirectoryLookups++;
                        continue;
                    }

                    var specPath = Path.Combine(dir, "docs", "Spec.md");
                    var metaPath = Path.Combine(dir, "raw", "metadata.json");
                    if (!File.Exists(specPath) || !File.Exists(metaPath)) continue;

                    // FileName은 프로시저 이름이지 파일 경로가 아니다.
                    // SpecStatementFactsExtractor가 BareObjectName(FileName)으로 키를 만들어
                    // "...md"를 넘기면 키가 "md"가 되어 모든 조회가 빗나간다.
                    var name = Path.GetFileName(dir);
                    specs.Add((name, File.ReadAllText(specPath)));
                    specMtimes.Add(File.GetLastWriteTime(specPath));

                    // metadata.json에는 BOM이 붙어 있다. File.ReadAllText가 자동으로 벗긴다
                    // (CoverageMapCommand.cs:203과 같은 규약).
                    var spDef = JsonSerializer.Deserialize<SpDefinition>(
                        File.ReadAllText(metaPath), JsonOptions);
                    if (spDef == null) continue;

                    ddl[name] = spDef.DdlText ?? string.Empty;
                    dateParameters[name] = SpecExpectations.ResolveDateParameter(spDef.StaticAnalysis);
                }

                jobs.Add(new SweepJob(jobName, steps, markdownByCode, specs, ddl, dateParameters));
            }

            var report = StepSweepService.Sweep(
                new SweepInput(jobs, parseFailed, missingStepFiles)
                {
                    UnresolvedProcedureDirectoryLookups = unresolvedProcedureDirectoryLookups,
                    StepCountCapExceededJobs = stepCountCapExceeded,
                    StepBundleOldest = stepMtimes.Count == 0 ? null : stepMtimes.Min(),
                    StepBundleNewest = stepMtimes.Count == 0 ? null : stepMtimes.Max(),
                    SpecOldest = specMtimes.Count == 0 ? null : specMtimes.Min(),
                    SpecNewest = specMtimes.Count == 0 ? null : specMtimes.Max(),
                });

            // 아무것도 재지 못했는데 0으로 끝나면 파이프라인이 초록으로 통과한다.
            if (report.Gaps.MeasuredPairs == 0) return null;

            var markdown = StepSweepReportWriter.Render(
                report, ShortCommitHash(repoRoot), CacheFormatVersions(outputDir));

            var path = NextAvailablePath(repoRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, markdown);
            return path;
        }

        /// <summary>
        /// 목차 JSON이 정상 파싱되지만 BatchStepPlanParser.MaxSteps(40) 상한을 넘어
        /// 버려졌는지를 판정한다. TryParse의 반환값(null)만으로는 진짜 파싱 실패와
        /// 구분할 수 없다 - 둘 다 null이다(BatchStepPlan.cs:154-159). 라벨을 믿고
        /// JSON을 디버깅하러 가면 헛수고한다(POQSettleProc4가 실제 사례 - JSON은
        /// 73단계로 정상 파싱되지만 상한 때문에 버려진다).
        ///
        /// PlanStructureEnricherTests.JsonBlockRegexLiteral_ShouldExistExactlyOnceInSourceTree가
        /// BatchStepPlanParser의 펜스 정규식 리터럴이 소스 트리에 단 한 곳(그 파일)만
        /// 있어야 한다고 지킨다 - 다시 구현하면 두 곳이 어긋날 수 있다는 뜻이다.
        /// BatchStepPlanParser는 읽기 전용(수정 금지)이라 내부 판정을 밖으로 못 내므로,
        /// 여기서는 그 정규식을 베끼지 않고 문자열 검색(IndexOf)만으로 펜스를 찾는다 -
        /// 진단 전용이라 정확한 재구현이 아니어도 된다(닫는 펜스를 못 찾으면 그냥
        /// 상한 초과가 아니라고 본다 - 어차피 TryParse도 이미 null을 반환한 뒤에만
        /// 호출되므로 "블록을 못 찾음"과 "상한 초과 아님"을 구분 못 해도 결과는
        /// 같다, 둘 다 parseFailed로 간다).
        /// </summary>
        private static bool ExceedsStepCountCap(string planStructureMarkdown, out int declaredStepCount)
        {
            declaredStepCount = 0;
            var searchFrom = 0;

            while (true)
            {
                var fenceStart = planStructureMarkdown.IndexOf("```json", searchFrom, StringComparison.Ordinal);
                if (fenceStart < 0) return false;

                var bodyStart = planStructureMarkdown.IndexOf('\n', fenceStart);
                if (bodyStart < 0) return false;
                bodyStart++;

                var fenceEnd = planStructureMarkdown.IndexOf("```", bodyStart, StringComparison.Ordinal);
                if (fenceEnd < 0) return false;

                var body = planStructureMarkdown[bodyStart..fenceEnd];
                searchFrom = fenceEnd + 3;

                try
                {
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("Steps", out var stepsProperty) &&
                        stepsProperty.ValueKind == JsonValueKind.Array)
                    {
                        declaredStepCount = stepsProperty.GetArrayLength();
                        if (declaredStepCount > BatchStepPlanParser.MaxSteps)
                        {
                            return true;
                        }
                    }
                }
                catch (Exception)
                {
                    // 이 블록은 진짜 못 읽는다 - 상한 초과 판정과 무관하다. 다음
                    // 블록(있다면)을 본다.
                }
            }
        }

        private static Dictionary<string, string> IndexProcedureDirectories(string outputDir)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var root = Path.Combine(outputDir, "Procedures");
            if (!Directory.Exists(root)) return index;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var bare = StepSweepService.BareProcedureName(Path.GetFileName(dir));
                if (bare.Length > 0) index[bare] = dir;
            }

            return index;
        }

        /// <summary>
        /// 캐시 인덱스에 실제로 실린 FormatVersion 값들. 코드 상수
        /// (CacheManager.CurrentCacheFormatVersion)는 private이라 읽을 수 없고,
        /// 어차피 측정에 영향을 주는 것은 산출물이 어느 버전으로 만들어졌는가다.
        ///
        /// [실물과 계획의 차이] 계획서 예시는 최상위 키가 "entries" 배열이라고
        /// 가정했지만, 실물 .sp_cache_index.json의 최상위 키는 "Entries"이고 값은
        /// 배열이 아니라 CanonicalName을 키로 하는 객체다 - 그래서 EnumerateObject로
        /// 순회한다.
        /// </summary>
        private static string CacheFormatVersions(string outputDir)
        {
            var path = Path.Combine(outputDir, ".sp_cache_index.json");
            if (!File.Exists(path)) return "알 수 없음(캐시 인덱스 없음)";

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var versions = new SortedSet<int>();
                var entryCount = 0;

                if (!doc.RootElement.TryGetProperty("Entries", out var entriesElement))
                {
                    return "알 수 없음(Entries 없음)";
                }

                foreach (var entry in entriesElement.EnumerateObject())
                {
                    entryCount++;
                    if (entry.Value.TryGetProperty("FormatVersion", out var v) && v.TryGetInt32(out var n))
                    {
                        versions.Add(n);
                    }
                }

                return versions.Count == 0
                    ? "알 수 없음(항목 없음)"
                    : $"{{{string.Join(", ", versions)}}} — 항목 {entryCount}개";
            }
            catch (Exception ex)
            {
                return $"알 수 없음({ex.GetType().Name})";
            }
        }

        private static string ShortCommitHash(string repoRoot)
        {
            try
            {
                var info = new ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };

                using var process = Process.Start(info);
                if (process == null) return "unknown";

                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return output.Length == 0 ? "unknown" : output;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>
        /// 같은 날 두 번 돌려도 앞 보고서를 덮지 않는다. 이름 고정이 보고서를 잃게 한
        /// 전례가 있다 - ConsistencyReport.md가 그래서 5회차 판을 잃을 뻔했다.
        /// </summary>
        private static string NextAvailablePath(string repoRoot)
        {
            var dir = Path.Combine(repoRoot, "docs", "audit-reports", "sweeps");
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            var path = Path.Combine(dir, $"{today}-step-sweep.md");
            if (!File.Exists(path)) return path;

            for (var suffix = 'b'; suffix <= 'z'; suffix++)
            {
                var candidate = Path.Combine(dir, $"{today}-step-sweep-{suffix}.md");
                if (!File.Exists(candidate)) return candidate;
            }

            throw new InvalidOperationException("같은 날 보고서가 26개를 넘었습니다.");
        }
    }
}
