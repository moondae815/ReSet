using System.Text;
using System.Text.RegularExpressions;
using ReSet.Core.Models;

namespace ReSet.Core.Services;

public sealed class SpecificationLinker
{
    private const string ReferenceHeader = "## 참조 코드 객체";
    private static readonly Regex ReferenceSectionRegex = new(
        @"(?ms)^## 참조 코드 객체(?:[ \t]*\r?\n|\z).*?(?=^##\s|\z)",
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
        AnalysisScope scope,
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
            // 빈 그래프는 두 가지 사실을 뜻할 수 있고, 둘은 같은 문장을 받으면 안 된다.
            // ON(Direct)에서는 발견이 직접 참조를 열거했고 하나도 없었다는 관측이다.
            // OFF(Transitive)에서는 발견이 애초에 그래프를 만들지 않았다는 뜻일 뿐인데
            // (DependencyAnalysisOrchestrator가 자식 탐색 자체를 건너뛴다), 여기에
            // ON 문장을 쓰면 문서가 객체에 대한 단언을 내놓는다 - 같은 폴더의
            // metadata.json이 피호출 객체를 나열하고 있는데도. "없다"와 "안 물어봤다"는
            // 산출물만 보고 구분되어야 한다(Thinking.md·raw/prompt-context.md가 받는
            // 것과 같은 규약). 침묵(절 생략)으로는 그 구분이 서지 않으므로 문장을 낸다.
            content.AppendLine(scope == AnalysisScope.Direct
                ? "- 직접 참조하는 코드 객체가 없습니다."
                : "- 참조분석을 끄고 분석해 직접 참조를 열거하지 않았습니다. "
                  + "참조가 없다는 뜻이 아니며, 이 회차가 수집한 의존성은 "
                  + "`../raw/metadata.json`에 있습니다.");
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
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Replace("#", "\\#", StringComparison.Ordinal)
            .Replace("<", "\\<", StringComparison.Ordinal)
            .Replace(">", "\\>", StringComparison.Ordinal);

    private static string EscapeMarkdownUrl(string value) =>
        string.Join(
            "/",
            value.Split('/', StringSplitOptions.None).Select(Uri.EscapeDataString));
}
