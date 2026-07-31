using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class SpecificationLinkerTests : IDisposable
{
    private readonly string _outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-SpecificationLinker-{Guid.NewGuid():N}");
    private readonly OutputPathResolver _paths;
    private readonly SpecificationLinker _linker;

    public SpecificationLinkerTests()
    {
        _paths = new OutputPathResolver("PaymentDB", _outputRoot);
        _linker = new SpecificationLinker(_paths, new MechanicalValidator());
    }

    [Fact]
    public async Task UpdateReferencesAsync_WritesRelativeLinkForSucceededChild()
    {
        var parentKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_PARENT", CodeObjectType.Procedure);
        var childKey = CodeObjectKey.Create("PaymentDB", "dbo", "FN_X", CodeObjectType.Function);
        var graph = CreateGraph(parentKey, childKey, AnalysisNodeStatus.Succeeded);

        var updated = await _linker.UpdateReferencesAsync(parentKey, "## 로직 흐름 요약\n본문", graph);

        Assert.Contains("## 참조 코드 객체", updated);
        Assert.Contains("[dbo.FN\\_X](../../../Functions/dbo.FN_X/docs/Spec.md)", updated);
    }

    [Fact]
    public async Task UpdateReferencesAsync_WritesReasonInsteadOfBrokenLink()
    {
        var parentKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_PARENT", CodeObjectType.Procedure);
        var childKey = CodeObjectKey.Create("PaymentDB", "dbo", "FN_X", CodeObjectType.Function);
        var graph = CreateGraph(parentKey, childKey, AnalysisNodeStatus.Failed, "DDL 수집 권한 없음");

        var updated = await _linker.UpdateReferencesAsync(parentKey, "# 명세", graph);

        Assert.Contains("분석 불가: DDL 수집 권한 없음", updated);
        Assert.DoesNotContain("](../../../Functions/dbo.FN_X/docs/Spec.md)", updated);
    }

    [Fact]
    public async Task UpdateReferencesAsync_ReplacesReferenceSectionAtEndOfFileAndEscapesMarkdown()
    {
        var parentKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_PARENT", CodeObjectType.Procedure);
        var childKey = CodeObjectKey.Create("PaymentDB", "dbo", "FN_[X]", CodeObjectType.Function);
        var graph = CreateGraph(parentKey, childKey, AnalysisNodeStatus.Failed, "권한 [없음] #1");

        var updated = await _linker.UpdateReferencesAsync(
            parentKey,
            "# 명세\n\n## 참조 코드 객체",
            graph);

        Assert.Equal(1, updated.Split("## 참조 코드 객체").Length - 1);
        Assert.Contains("dbo.FN\\_\\[X\\]", updated);
        Assert.Contains("권한 \\[없음\\] \\#1", updated);
    }

    [Fact]
    public async Task UpdateReferencesAsync_EncodesUnsafePathSegmentsInLinkUrl()
    {
        var parentKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_PARENT", CodeObjectType.Procedure);
        var childKey = CodeObjectKey.Create("PaymentDB", "dbo", "FN (X)#1", CodeObjectType.Function);
        var graph = CreateGraph(parentKey, childKey, AnalysisNodeStatus.Succeeded);

        var updated = await _linker.UpdateReferencesAsync(parentKey, "# 명세", graph);

        Assert.Contains("../../../Functions/dbo.FN%20%28X%29%231/docs/Spec.md", updated);
    }

    private CodeObjectPipelineResult CreateGraph(
        CodeObjectKey parentKey,
        CodeObjectKey childKey,
        AnalysisNodeStatus childStatus,
        string? error = null) =>
        new()
        {
            Nodes = new List<AnalysisNode>
            {
                new(parentKey) { Status = AnalysisNodeStatus.Succeeded, SpecPath = _paths.ResolveSpecPath(parentKey) },
                new(childKey) { Status = childStatus, SpecPath = _paths.ResolveSpecPath(childKey), Error = error }
            },
            DependencyEdges = new List<DependencyEdge> { new(parentKey, childKey) }
        };

    public void Dispose()
    {
        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, true);
        }
    }
}
