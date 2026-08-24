using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Spectre.Console;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Cli
{
    /// <summary>
    /// DB·AI 없이 output/ 산출물만으로 커버리지 맵을 낸다.
    ///
    /// [왜 빠진 객체를 화면에 남기는가] 폐포 31개 중 몇 개가 조용히 빠지면 맵은
    /// 멀쩡해 보이는데 대조 범위가 줄어든 것을 아무도 모른다. 감사 축 A와 대상
    /// 정의를 맞춰 둔 이유가 무너진다.
    /// </summary>
    public static class CoverageMapCommand
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new() { PropertyNameCaseInsensitive = true };

        /// <summary>산출한 HTML 경로. 대상을 못 찾으면 null.</summary>
        public static string? Run(string outputDir, string target)
        {
            var jobDir = Path.Combine(outputDir, "Jobs", target);
            if (Directory.Exists(jobDir)) return RunJob(outputDir, jobDir, target);

            foreach (var kind in new[] { "Procedures", "Functions" })
            {
                var objectDir = Path.Combine(outputDir, kind, target);
                if (!Directory.Exists(objectDir)) continue;

                var coverage = LoadObject(objectDir, target);
                if (coverage == null) return null;

                var path = Path.Combine(objectDir, "docs", "CoverageMap.html");
                Write(path, new[] { coverage }, target);
                return path;
            }

            return null;
        }

        private static string? RunJob(string outputDir, string jobDir, string job)
        {
            var contextPath = Path.Combine(jobDir, "raw", "prompt-context.md");
            if (!File.Exists(contextPath))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(contextPath)}이 없어 소비 명세서 집합을 정할 수 없습니다.[/]");
                return null;
            }

            var consumed = File.ReadAllLines(contextPath)
                .Where(l => l.StartsWith("Filename:", StringComparison.Ordinal))
                .Select(l => l["Filename:".Length..].Trim())
                .Where(n => n.Length > 0 && !n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 참조 폐포 - 소비 명세서 각각의 Nodes[] 합집합. 감사 축 A와 같은 정의다.
            //
            // [왜 물리 디렉터리로 중복을 없애는가] 소비 SP 자신은 "dbo.X" 꼴 이름으로
            // 등록되는데, 다른 소비 SP의 dependency-manifest.json 안에서는 같은 객체가
            // "SETTLE_POQ_DB.dbo.X.Procedure" 꼴 DB 접두 Key로 다시 나온다(중첩 SP를
            // 서로 참조하는 경우). 문자열 키로만 dedupe하면 같은 객체가 두 키 아래 두 번
            // 실려 폐포가 부풀어 오른다 - 실측(POQSettlePrco20)에서 문자열 키 기준
            // 43개였는데 물리 경로 기준으로는 감사 축 A와 같은 31개였다.
            var objectDirs = new Dictionary<string, (string Name, string Dir)>(StringComparer.OrdinalIgnoreCase);

            void Register(string name, string dir)
            {
                var fullDir = Path.GetFullPath(dir);
                if (!objectDirs.ContainsKey(fullDir))
                {
                    objectDirs[fullDir] = (name, dir);
                }
            }

            foreach (var name in consumed)
            {
                var dir = Path.Combine(outputDir, "Procedures", name);
                if (!Directory.Exists(dir))
                {
                    AnsiConsole.MarkupLine($"[yellow]건너뜀: {Markup.Escape(name)} - 산출물 디렉터리가 없습니다.[/]");
                    continue;
                }

                Register(name, dir);
                foreach (var (key, nodeDir) in ClosureOf(dir))
                {
                    Register(key, nodeDir);
                }
            }

            var covered = new List<ObjectCoverage>();
            foreach (var (name, dir) in objectDirs.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                var coverage = LoadObject(dir, name);
                if (coverage == null) continue;
                covered.Add(coverage);
            }

            AnsiConsole.MarkupLine(
                $"[grey]소비 명세서 {consumed.Count}개 → 폐포 {objectDirs.Count}개 → 대조 {covered.Count}개[/]");

            var path = Path.Combine(jobDir, "coverage", "CoverageMap.html");
            Write(path, covered, job);
            return path;
        }

        private static IEnumerable<(string Key, string Dir)> ClosureOf(string objectDir)
        {
            var manifestPath = Path.Combine(objectDir, "raw", "dependency-manifest.json");
            if (!File.Exists(manifestPath)) yield break;

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("Nodes", out var nodes)) yield break;

            foreach (var node in nodes.EnumerateArray())
            {
                var status = node.TryGetProperty("Status", out var s) ? s.GetString() : null;
                var key = node.TryGetProperty("Key", out var k) ? k.GetString() : null;
                var specPath = node.TryGetProperty("SpecPath", out var p) ? p.GetString() : null;
                if (key == null) continue;

                if (!string.Equals(status, "Succeeded", StringComparison.Ordinal))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]건너뜀: {Markup.Escape(key)} - 상태가 {Markup.Escape(status ?? "없음")}입니다.[/]");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(specPath)) continue;

                // SpecPath는 객체 디렉터리 기준이고 .../docs/Spec.md로 끝난다.
                var specFull = Path.GetFullPath(Path.Combine(objectDir, specPath));
                var nodeDir = Path.GetDirectoryName(Path.GetDirectoryName(specFull));
                if (nodeDir != null && Directory.Exists(nodeDir)) yield return (key, nodeDir);
            }
        }

        private static ObjectCoverage? LoadObject(string objectDir, string displayName)
        {
            var metaPath = Path.Combine(objectDir, "raw", "metadata.json");
            var specPath = Path.Combine(objectDir, "docs", "Spec.md");

            if (!File.Exists(metaPath) || !File.Exists(specPath))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]건너뜀: {Markup.Escape(displayName)} - metadata.json 또는 Spec.md가 없습니다.[/]");
                return null;
            }

            // metadata.json에는 BOM이 붙어 있다. File.ReadAllText가 자동으로 벗긴다.
            var spDef = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(metaPath), JsonOptions);
            if (spDef == null)
            {
                AnsiConsole.MarkupLine($"[yellow]건너뜀: {Markup.Escape(displayName)} - metadata.json 역직렬화 실패.[/]");
                return null;
            }

            return CoverageMapComposer.Compose(displayName, spDef, File.ReadAllText(specPath));
        }

        private static void Write(string path, IReadOnlyList<ObjectCoverage> objects, string title)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, CoverageMapHtmlWriter.Render(objects, $"{title} 커버리지 맵"));
        }
    }
}
