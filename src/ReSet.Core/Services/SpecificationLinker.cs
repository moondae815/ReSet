using System.Text;
using System.Text.RegularExpressions;
using ReSet.Core.Models;

namespace ReSet.Core.Services;

public sealed class SpecificationLinker
{
    private const string ReferenceHeader = "## 참조 코드 객체";
    private static readonly Regex ReferenceSectionRegex = new(
        @"(?ms)^## 참조 코드 객체\s*\r?\n.*?(?=^##\s|\z)",
        RegexOptions.Compiled);

    private readonly OutputPathResolver _paths;
    private readonly MechanicalValidator _validator;

    public SpecificationLinker(OutputPathResolver paths, MechanicalValidator validator)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public Task<string> UpdateReferencesAsync(
        CodeObjectKey parentKey,
        string markdown,
        CodeObjectPipelineResult graph,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentKey);
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(graph);
        cancellationToken.ThrowIfCancellationRequested();

        var parentSpecDirectory = Path.GetDirectoryName(_paths.ResolveSpecPath(parentKey))
            ?? throw new InvalidOperationException("상위 명세서 디렉터리를 계산할 수 없습니다.");
        var nodesByKey = graph.Nodes.ToDictionary(node => node.Key);
        var content = new StringBuilder();
        content.AppendLine(ReferenceHeader);
        content.AppendLine();

        var childEdges = graph.DependencyEdges
            .Where(edge => edge.Source.Equals(parentKey))
            .GroupBy(edge => edge.Target)
            .Select(group => group.First())
            .OrderBy(edge => edge.Target.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (childEdges.Count == 0)
        {
            content.AppendLine("- 직접 참조하는 코드 객체가 없습니다.");
        }
        else
        {
            foreach (var edge in childEdges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = EscapeMarkdownText($"{edge.Target.Schema}.{edge.Target.Name}");
                if (nodesByKey.TryGetValue(edge.Target, out var child) &&
                    child.Status == AnalysisNodeStatus.Succeeded)
                {
                    var childSpecPath = child.SpecPath ?? _paths.ResolveSpecPath(edge.Target);
                    var relativePath = Path.GetRelativePath(parentSpecDirectory, childSpecPath)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');
                    content.AppendLine($"- [{label}]({EscapeMarkdownUrl(relativePath)})");
                }
                else
                {
                    var reason = child is null
                        ? "분석 그래프에 객체 상태가 없습니다."
                        : GetFailureReason(child);
                    content.AppendLine($"- {label} — {EscapeMarkdownText(reason)}");
                }
            }
        }

        var section = content.ToString().TrimEnd() + Environment.NewLine;
        var updated = ReferenceSectionRegex.IsMatch(markdown)
            ? ReferenceSectionRegex.Replace(markdown, section)
            : markdown.TrimEnd() + Environment.NewLine + Environment.NewLine + section;
        var validation = _validator.Validate(updated);
        return Task.FromResult(validation.CleansedMarkdown ?? updated);
    }

    private static string GetFailureReason(AnalysisNode node)
    {
        var detail = string.IsNullOrWhiteSpace(node.Error) ? "상세 사유가 없습니다." : node.Error;
        return node.Status switch
        {
            AnalysisNodeStatus.Failed => $"분석 불가: {detail}",
            AnalysisNodeStatus.SkippedDepth => $"분석 생략(깊이 제한): {detail}",
            AnalysisNodeStatus.SkippedExternal => $"분석 생략(외부 객체): {detail}",
            AnalysisNodeStatus.Cancelled => $"분석 취소: {detail}",
            _ => $"분석 대기 중: {detail}"
        };
    }

    private static string EscapeMarkdownText(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private static string EscapeMarkdownUrl(string value) =>
        value.Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
}
