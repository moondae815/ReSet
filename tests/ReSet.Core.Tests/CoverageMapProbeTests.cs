using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 설계서 §「위험」의 게이트. 🟧 비율을 14개 SP 전수(및 함수·외부 함수)로 실측해
    /// 출력에 남긴다. output/이 .gitignore 대상이라 CI에서는 건너뛴다 - 건너뛴 사실이
    /// 조용히 사라지면 게이트 자체가 없는 것과 같으므로 매 테스트가 건너뛴 사유를
    /// 출력에 남긴다.
    ///
    /// [코드가 아니라 숫자가 산출물이다] 이 파일은 판정 로직을 바꾸지 않는다.
    /// CoverageMapComposer.Compose가 낸 결과를 세고 늘어놓을 뿐이다.
    /// </summary>
    public class CoverageMapProbeTests
    {
        private readonly ITestOutputHelper _output;

        public CoverageMapProbeTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// [2026-08-24 실측 정정] 플랜 원안은 "output/ 디렉터리를 가진 조상"을 찾을 때까지
        /// 올라가는데, 실측에서 <c>tests/ReSet.Core.Tests/bin/Debug/net10.0/output/</c>이
        /// 이미 존재하는 것이 확인됐다 - 다른 테스트(`DependencyAnalysisOrchestratorTests` 류)가
        /// CWD 상대경로로 남긴 `dbo.USP_Root` 1건짜리 스크래치 산출물이다. 원안대로면 그
        /// 얕은 자리에서 멈춰 실물 14 SP 코퍼스 대신 그 스크래치를 "실측"하고, 게이트가
        /// 조용히 틀린 숫자를 낸다(Skip도 아니고 실패도 아니라 더 위험하다). 그래서 "output/이
        /// 있다"가 아니라 "이 게이트가 아는 실물 SP 하나가 실제로 있다"로 판정 기준을 좁힌다.
        /// </summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(
                       dir.FullName, "output", "Procedures",
                       "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", "raw", "metadata.json")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? string.Empty;
        }

        private static SpDefinition? LoadSpDef(string metaPath)
        {
            if (!File.Exists(metaPath)) return null;
            return JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(metaPath));
        }

        /// <summary>(객체명, metadata.json 경로, Spec.md 경로)를 낸다. 산출물이 없으면 스킵 대상.</summary>
        private static IEnumerable<(string Name, string MetaPath, string SpecPath)> Objects(string baseDir)
        {
            if (!Directory.Exists(baseDir)) yield break;
            foreach (var dir in Directory.GetDirectories(baseDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                yield return (name, Path.Combine(dir, "raw", "metadata.json"), Path.Combine(dir, "docs", "Spec.md"));
            }
        }

        private static ObjectCoverage? TryCompose(string name, string metaPath, string specPath)
        {
            if (!File.Exists(metaPath) || !File.Exists(specPath)) return null;
            var spDef = LoadSpDef(metaPath);
            if (spDef == null) return null;
            return CoverageMapComposer.Compose(name, spDef, File.ReadAllText(specPath));
        }

        // ------------------------------------------------------------------
        // 1) 14개 SP 전수 - 4상태 분포, 🟧 유형별 집계, 🟥 전 건 자리, IsKnownUncovered
        // ------------------------------------------------------------------

        [SkippableFact]
        public void Probe_AllProcedures_ShouldReportFullStateDistribution()
        {
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/ 디렉터리를 찾지 못했다 - 실측 건너뜀 (게이트 무효)");

            var procDir = Path.Combine(root, "output", "Procedures");
            var all = Objects(procDir).ToList();
            Skip.If(all.Count == 0, "output/Procedures/가 비어 있다 - 실측 건너뜀 (게이트 무효)");

            var coverages = new List<(string Name, ObjectCoverage Coverage)>();
            foreach (var (name, metaPath, specPath) in all)
            {
                var c = TryCompose(name, metaPath, specPath);
                if (c == null)
                {
                    _output.WriteLine($"[건너뜀] {name} - metadata.json 또는 Spec.md 없음");
                    continue;
                }
                coverages.Add((name, c));
            }

            Skip.If(coverages.Count == 0, "산출물을 하나도 못 읽었다 - 실측 건너뜀 (게이트 무효)");

            _output.WriteLine($"실측 대상: {coverages.Count}/{all.Count} SP (output/Procedures/)");
            _output.WriteLine("");

            int totalLeaf = 0, totalC = 0, totalMissing = 0, totalProse = 0, totalOos = 0;
            var oosBreakdownAll = new Dictionary<string, int>(StringComparer.Ordinal);
            var missingPositions = new List<(string Sp, int Start, int End, string Type)>();
            int mergeKnownUncoveredTotal = 0;

            foreach (var (name, coverage) in coverages)
            {
                var ddlLines = (coverage.DdlText).Split('\n').Length;
                var cConsistent = coverage.Count(CoverageState.Consistent);
                var cMissing = coverage.Count(CoverageState.SpecMissing);
                var cProse = coverage.Count(CoverageState.ProseOnly);
                var cOos = coverage.Count(CoverageState.OutOfScope);

                totalLeaf += coverage.LeafCount;
                totalC += cConsistent;
                totalMissing += cMissing;
                totalProse += cProse;
                totalOos += cOos;

                _output.WriteLine($"=== {name} ===");
                _output.WriteLine($"DDL 줄수      : {ddlLines}");
                _output.WriteLine($"읽은 표 종수  : {coverage.TableKindsRead}");
                _output.WriteLine($"잎 문장       : {coverage.LeafCount}");
                _output.WriteLine($"🟩 Consistent : {cConsistent}");
                _output.WriteLine($"🟥 SpecMissing: {cMissing}");
                _output.WriteLine($"🟦 ProseOnly  : {cProse}");
                _output.WriteLine($"🟧 OutOfScope : {cOos}");

                var oosBySp = coverage.Statements
                    .Where(s => s.State == CoverageState.OutOfScope)
                    .GroupBy(s => s.Statement.StatementType)
                    .OrderByDescending(g => g.Count())
                    .ToList();

                if (oosBySp.Count > 0)
                {
                    _output.WriteLine("🟧 유형별(이 SP):");
                    foreach (var g in oosBySp)
                    {
                        _output.WriteLine($"  {g.Count(),4}  {g.Key}");
                        oosBreakdownAll[g.Key] = oosBreakdownAll.GetValueOrDefault(g.Key) + g.Count();
                    }
                }

                foreach (var s in coverage.Statements.Where(s => s.State == CoverageState.SpecMissing))
                {
                    missingPositions.Add((name, s.Statement.StartLine, s.Statement.EndLine, s.Statement.StatementType));
                }

                var mergeKnown = coverage.Statements.Count(s => s.IsKnownUncovered);
                mergeKnownUncoveredTotal += mergeKnown;
                if (mergeKnown > 0)
                {
                    var mergeKnownByState = coverage.Statements.Where(s => s.IsKnownUncovered)
                        .GroupBy(s => s.State);
                    _output.WriteLine($"IsKnownUncovered(MERGE): {mergeKnown}건 - "
                        + string.Join(", ", mergeKnownByState.Select(g => $"{g.Key}={g.Count()}")));
                }

                _output.WriteLine("");
            }

            _output.WriteLine("======================================================");
            _output.WriteLine("전체 합계 (14 SP 전수, 잎 문장 단위)");
            _output.WriteLine("======================================================");
            _output.WriteLine($"잎 문장 총계  : {totalLeaf}");
            double Pct(int n) => totalLeaf == 0 ? 0 : 100.0 * n / totalLeaf;
            _output.WriteLine($"🟩 Consistent : {totalC,4}  ({Pct(totalC):F1}%)");
            _output.WriteLine($"🟥 SpecMissing: {totalMissing,4}  ({Pct(totalMissing):F1}%)");
            _output.WriteLine($"🟦 ProseOnly  : {totalProse,4}  ({Pct(totalProse):F1}%)");
            _output.WriteLine($"🟧 OutOfScope : {totalOos,4}  ({Pct(totalOos):F1}%)");
            _output.WriteLine("");

            _output.WriteLine("🟧 유형별 전체 합산 (내림차순):");
            foreach (var kv in oosBreakdownAll.OrderByDescending(kv => kv.Value))
            {
                _output.WriteLine($"  {kv.Value,4}  {kv.Key}");
            }
            _output.WriteLine("");

            _output.WriteLine($"IsKnownUncovered(MERGE) 총건수: {mergeKnownUncoveredTotal}");
            _output.WriteLine("");

            _output.WriteLine($"🟥 SpecMissing 전 건의 자리 ({missingPositions.Count}건) - 감사 10회차(🔴0·🟠0)와 대조:");
            if (missingPositions.Count == 0)
            {
                _output.WriteLine("  (없음)");
            }
            else
            {
                foreach (var (sp, start, end, type) in missingPositions)
                {
                    _output.WriteLine($"  {sp}  줄 {start}-{end}  {type}");
                }
            }

            Assert.True(totalLeaf > 0, "잎 문장이 하나도 없다 - 좌표계가 무너졌다");
        }

        // ------------------------------------------------------------------
        // 2) TableKindsRead 실측 교차확인 - "라인" 정확일치가 놓치는 표가 몇 종인가
        // ------------------------------------------------------------------

        private static readonly Regex MachineConfirmedHeading =
            new(@"^#{2,6}\s+(.*?)\s*\(기계 확정 — 수정 금지\)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// (기계 확정 — 수정 금지) 절 제목을 전부 걷고, 그 절의 첫 표 헤더에 정확히
        /// '라인' 칸이 있는지를 관찰한다. SpecAnchorIndex.CountLineBearingTables와
        /// <b>같은 판정 로직을 여기서 독립적으로 재현</b>해서 교차확인한다 - 프로덕션
        /// 코드를 고치지 않고 관측만 한다는 지시를 지키기 위해서다.
        /// </summary>
        private static List<(string Heading, bool HasExactLineColumn, string? FirstHeaderRow)>
            InspectMachineConfirmedTables(string specMarkdown)
        {
            var lines = MarkdownSectionLocator.SplitLines(specMarkdown);
            var result = new List<(string, bool, string?)>();
            var headingRegex = new Regex(@"^#{2,6}\s+(.*)$");
            var separatorRegex = new Regex(@"^\|[\s:|-]+\|\s*$");

            for (var i = 0; i < lines.Count; i++)
            {
                var m = headingRegex.Match(lines[i]);
                if (!m.Success) continue;
                var headingText = m.Groups[1].Value.Trim();
                if (!headingText.Contains("기계 확정")) continue;

                // 이 절 제목 다음, 다음 '###' 제목 전까지에서 첫 표 헤더 행을 찾는다.
                string? firstHeaderRow = null;
                var hasLine = false;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (headingRegex.IsMatch(lines[j]) && lines[j].TrimStart().StartsWith("#")) break;
                    if (!lines[j].StartsWith("|", StringComparison.Ordinal)) continue;
                    if (j + 1 >= lines.Count || !separatorRegex.IsMatch(lines[j + 1])) continue;

                    firstHeaderRow = lines[j];
                    var header = MarkdownTableCellCodec.SplitRow(lines[j]);
                    hasLine = header.Any(c => string.Equals(c.Trim(), "라인", StringComparison.Ordinal));
                    break;
                }

                result.Add((headingText, hasLine, firstHeaderRow));
            }

            return result;
        }

        [SkippableFact]
        public void Probe_TableKindsRead_ShouldReportGapAgainstActualMachineConfirmedTables()
        {
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/ 디렉터리를 찾지 못했다 - 실측 건너뜀");

            var procDir = Path.Combine(root, "output", "Procedures");
            var all = Objects(procDir).Where(o => File.Exists(o.SpecPath)).ToList();
            Skip.If(all.Count == 0, "output/Procedures/의 Spec.md를 하나도 못 찾았다 - 실측 건너뜀");

            _output.WriteLine("SP별 (기계 확정 — 수정 금지) 표 종수 vs TableKindsRead:");
            _output.WriteLine("");

            int totalActual = 0, totalReported = 0;
            var missedHeadingCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var (name, _, specPath) in all)
            {
                var md = File.ReadAllText(specPath);
                var inspected = InspectMachineConfirmedTables(md);
                var actualCount = inspected.Count;
                var reported = SpecAnchorIndex.CountLineBearingTables(md);

                totalActual += actualCount;
                totalReported += reported;

                var missed = inspected.Where(t => !t.HasExactLineColumn).ToList();

                _output.WriteLine($"{name}: 기계확정 절 {actualCount}종 / TableKindsRead(라인 칸 보유) {reported}종"
                    + (actualCount - reported != 0 ? $"  <- 차이 {actualCount - reported}" : ""));

                foreach (var (heading, _, headerRow) in missed)
                {
                    _output.WriteLine($"    라인 칸 없음: {heading}  (헤더: {headerRow})");
                    missedHeadingCounts[heading] = missedHeadingCounts.GetValueOrDefault(heading) + 1;
                }
            }

            _output.WriteLine("");
            _output.WriteLine($"합계: 기계확정 절 {totalActual}종 / TableKindsRead 합 {totalReported}종 (차이 {totalActual - totalReported})");
            _output.WriteLine("");
            _output.WriteLine("'라인' 칸이 없는(정확일치 실패) 절 제목별 SP 수:");
            foreach (var kv in missedHeadingCounts.OrderByDescending(kv => kv.Value))
            {
                _output.WriteLine($"  {kv.Value,2}개 SP  {kv.Key}");
            }
        }

        // ------------------------------------------------------------------
        // 3) SET 대입의 관할 경계 실측 (미확정 사항 2)
        // ------------------------------------------------------------------

        [SkippableFact]
        public void Probe_SetVariableStatement_ShouldReportExtractorBoundary()
        {
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/ 디렉터리를 찾지 못했다 - 실측 건너뜀");

            var procDir = Path.Combine(root, "output", "Procedures");
            var all = Objects(procDir).ToList();
            Skip.If(all.Count == 0, "output/Procedures/가 비어 있다 - 실측 건너뜀");

            int totalSetLeaves = 0, totalSetCovered = 0;
            int totalSelectLeaves = 0, totalSelectAggregateOrNonAggregateHit = 0;
            var offTargetHits = new List<string>();

            foreach (var (name, metaPath, specPath) in all)
            {
                var coverage = TryCompose(name, metaPath, specPath);
                if (coverage == null) continue;

                var spDef = LoadSpDef(metaPath)!;
                var expectations = SpecExpectations.From(spDef);

                var setLeaves = coverage.Statements
                    .Where(s => s.Statement.StatementType == "SetVariableStatement").ToList();
                totalSetLeaves += setLeaves.Count;
                var covered = setLeaves.Where(s => s.ExtractorLines.Count > 0).ToList();
                totalSetCovered += covered.Count;

                if (covered.Count > 0)
                {
                    _output.WriteLine($"{name}: SET 대입 {setLeaves.Count}건 중 재료 있음 {covered.Count}건 "
                        + $"(줄 {string.Join(",", covered.Select(c => c.Statement.StartLine))})");
                }

                if (expectations == null) continue;

                // AggregateAssignment/NonAggregateAssignment 라인이 실제로 SelectStatement에
                // 떨어지는지(=SetVariableStatement가 아닌지) 교차확인한다.
                var allStatements = DdlStatementEnumerator.Enumerate(spDef.DdlText);
                bool InRangeOfLeaf(int line, out DdlStatement? leaf)
                {
                    leaf = allStatements.FirstOrDefault(s => !s.IsContainer && line >= s.StartLine && line <= s.EndLine);
                    return leaf != null;
                }

                var aggLines = expectations.ExecutionSemantics
                    .Where(f => f.Kind == ExecutionSemanticsFacts.AggregateAssignmentKind)
                    .Select(f => f.Line);
                var nonAggLines = expectations.ExecutionSemantics
                    .Where(f => f.Kind == ExecutionSemanticsFacts.NonAggregateAssignmentKind)
                    .Select(f => f.Line);

                foreach (var lineStr in aggLines.Concat(nonAggLines))
                {
                    if (!int.TryParse(lineStr, out var line)) continue;
                    totalSelectLeaves++;
                    if (InRangeOfLeaf(line, out var leaf) && leaf!.StatementType == "SelectStatement")
                    {
                        totalSelectAggregateOrNonAggregateHit++;
                    }
                    else
                    {
                        offTargetHits.Add($"{name}:{line} -> {leaf?.StatementType ?? "(못 찾음)"}");
                    }
                }
            }

            _output.WriteLine("");
            _output.WriteLine("=== SET 대입 관할 경계 (미확정 사항 2) ===");
            _output.WriteLine($"잎 SetVariableStatement 총계: {totalSetLeaves}");
            _output.WriteLine($"  그중 추출기 재료가 붙은 것 : {totalSetCovered}"
                + $" ({(totalSetLeaves == 0 ? 0 : 100.0 * totalSetCovered / totalSetLeaves):F1}%) - LoopVariableResetExtractor만 이 유형에 닿는다");
            _output.WriteLine($"  관할 밖(재료 없음)        : {totalSetLeaves - totalSetCovered}");
            _output.WriteLine("");
            _output.WriteLine($"AggregateAssignment+NonAggregateAssignment 재료 {totalSelectLeaves}건 중 "
                + $"SelectStatement 잎에 떨어진 것: {totalSelectAggregateOrNonAggregateHit}건 "
                + "(이 두 추출기는 'SELECT @v = ...' 형태만 다뤄 SetVariableStatement에는 원리적으로 닿지 않는다)");
            if (offTargetHits.Count > 0)
            {
                _output.WriteLine("예상 밖 착지(SelectStatement가 아님):");
                foreach (var h in offTargetHits) _output.WriteLine($"  {h}");
            }
        }

        // ------------------------------------------------------------------
        // 4) 파생 테이블 정의 표의 '라인' 칸 부재가 실제 손해인가 (미확정 사항 5)
        // ------------------------------------------------------------------

        private static readonly Regex DerivedTableAliasSite =
            new(@"\)\s*(?:AS\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*(?:,|\r?\n|\)|$|ON\b|WHERE\b|LEFT\b|RIGHT\b|INNER\b|JOIN\b|GROUP\b|ORDER\b)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> DmlCapableTypes = new(StringComparer.Ordinal)
        {
            "SelectStatement", "InsertStatement", "UpdateStatement", "DeleteStatement", "MergeStatement"
        };

        [SkippableFact]
        public void Probe_DerivedTableOnlySupport_ShouldCountUnsupportedStatements()
        {
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/ 디렉터리를 찾지 못했다 - 실측 건너뜀");

            var procDir = Path.Combine(root, "output", "Procedures");
            var all = Objects(procDir).ToList();
            Skip.If(all.Count == 0, "output/Procedures/가 비어 있다 - 실측 건너뜀");

            var candidates = new List<string>();

            foreach (var (name, metaPath, specPath) in all)
            {
                if (!File.Exists(specPath)) continue;
                var md = File.ReadAllText(specPath);
                if (!md.Contains("파생 테이블 정의")) continue; // 이 SP에 파생 테이블 정의 표가 없으면 대상이 아니다.

                var coverage = TryCompose(name, metaPath, specPath);
                if (coverage == null) continue;
                var ddlLines = coverage.DdlText.Split('\n');

                foreach (var s in coverage.Statements.Where(s =>
                             (s.State == CoverageState.OutOfScope || s.State == CoverageState.ProseOnly)
                             && DmlCapableTypes.Contains(s.Statement.StatementType)))
                {
                    var start = Math.Max(1, s.Statement.StartLine);
                    var end = Math.Min(ddlLines.Length, s.Statement.EndLine);
                    if (start > end) continue;
                    var text = string.Join('\n', ddlLines.Skip(start - 1).Take(end - start + 1));
                    if (DerivedTableAliasSite.IsMatch(text))
                    {
                        candidates.Add($"{name}  줄 {s.Statement.StartLine}-{s.Statement.EndLine}  "
                            + $"{s.Statement.StatementType}  상태={s.State}");
                    }
                }
            }

            _output.WriteLine("=== 파생 테이블 정의 표 '라인' 칸 부재의 실손해 (미확정 사항 5) ===");
            _output.WriteLine("방법: 'Spec.md에 파생 테이블 정의 표가 있는 SP'의 OutOfScope/ProseOnly 문장 중 "
                + "DML 계열(SELECT/INSERT/UPDATE/DELETE/MERGE)이면서 원본에 괄호닫힘+별칭(파생 테이블 자리) "
                + "패턴이 걸리는 것을 후보로 센다. 정규식 근사치이므로 후보 각각을 육안 확인 대상으로 남긴다.");
            _output.WriteLine($"후보 {candidates.Count}건:");
            foreach (var c in candidates) _output.WriteLine($"  {c}");
            if (candidates.Count == 0)
            {
                _output.WriteLine("  (없음 - '파생 테이블 정의' 표에 '라인' 칸이 없는 것은 이 코퍼스에서 손해로 " +
                    "이어지지 않았다. 파생 테이블이 걸린 문장은 그 문장 자체의 DML 범위·집합 술어 재료로 이미 " +
                    "커버되고 있었다.)");
            }
        }

        // ------------------------------------------------------------------
        // 5) 함수(UDF)에도 같은 좌표계가 서는가 - 인라인 TVF는 무의미한가 (미확정 사항 4)
        // ------------------------------------------------------------------

        [SkippableFact]
        public void Probe_FunctionsAndExternal_ShouldReportLeafCounts()
        {
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/ 디렉터리를 찾지 못했다 - 실측 건너뜀");

            var targets = new List<(string Bucket, string Name, string MetaPath, string SpecPath)>();

            foreach (var (name, metaPath, specPath) in Objects(Path.Combine(root, "output", "Functions")))
            {
                targets.Add(("Functions", name, metaPath, specPath));
            }

            var externalRoot = Path.Combine(root, "output", "External");
            if (Directory.Exists(externalRoot))
            {
                foreach (var dbDir in Directory.GetDirectories(externalRoot))
                {
                    var fnDir = Path.Combine(dbDir, "Functions");
                    foreach (var (name, metaPath, specPath) in Objects(fnDir))
                    {
                        targets.Add(($"External/{Path.GetFileName(dbDir)}", name, metaPath, specPath));
                    }
                }
            }

            Skip.If(targets.Count == 0, "output/Functions·output/External에 함수 산출물이 없다 - 실측 건너뜀");

            int inlineTvfCount = 0, inlineTvfLeafOne = 0;
            int scalarCount = 0;
            int multiStatementTvfCount = 0;

            foreach (var (bucket, name, metaPath, specPath) in targets)
            {
                var coverage = TryCompose(name, metaPath, specPath);
                if (coverage == null)
                {
                    _output.WriteLine($"[건너뜀] {bucket}/{name} - metadata.json 또는 Spec.md 없음");
                    continue;
                }
                var spDef = LoadSpDef(metaPath)!;
                var isTableValued = spDef.FunctionReturn?.IsTableValued ?? false;

                // 인라인 TVF 판정 근사: 테이블 값 반환이면서 잎 문장이 1개뿐이다
                // (본문이 대개 'RETURN (SELECT ...)' 하나라 SELECT가 컨테이너 안에
                // 별도 잎으로 갈라지지 않는다). 다중 문장 TVF는 BEGIN...END 본문에
                // INSERT INTO @t 등 여러 잎을 갖는다.
                var isLikelyInline = isTableValued && coverage.LeafCount <= 1;

                if (isTableValued)
                {
                    if (isLikelyInline) { inlineTvfCount++; if (coverage.LeafCount == 1) inlineTvfLeafOne++; }
                    else multiStatementTvfCount++;
                }
                else
                {
                    scalarCount++;
                }

                _output.WriteLine($"{bucket}/{name}: TVF={isTableValued} 잎={coverage.LeafCount} "
                    + $"🟩{coverage.Count(CoverageState.Consistent)} 🟥{coverage.Count(CoverageState.SpecMissing)} "
                    + $"🟦{coverage.Count(CoverageState.ProseOnly)} 🟧{coverage.Count(CoverageState.OutOfScope)}");
            }

            _output.WriteLine("");
            _output.WriteLine("=== 함수 좌표계 요약 (미확정 사항 4) ===");
            _output.WriteLine($"스칼라 함수         : {scalarCount}개");
            _output.WriteLine($"인라인 TVF(추정)    : {inlineTvfCount}개  (그중 잎=1인 것 {inlineTvfLeafOne}개)");
            _output.WriteLine($"다중 문장 TVF(추정) : {multiStatementTvfCount}개");
        }
    }
}
