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

        var updated = await _linker.UpdateReferencesAsync(parentKey, "## 로직 흐름 요약\n본문", graph, AnalysisScope.Direct);

        Assert.Contains("## 참조 코드 객체", updated);
        Assert.Contains("[dbo.FN\\_X](../../../Functions/dbo.FN_X/docs/Spec.md)", updated);
    }

    [Fact]
    public async Task UpdateReferencesAsync_WritesReasonInsteadOfBrokenLink()
    {
        var parentKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_PARENT", CodeObjectType.Procedure);
        var childKey = CodeObjectKey.Create("PaymentDB", "dbo", "FN_X", CodeObjectType.Function);
        var graph = CreateGraph(parentKey, childKey, AnalysisNodeStatus.Failed, "DDL 수집 권한 없음");

        var updated = await _linker.UpdateReferencesAsync(parentKey, "# 명세", graph, AnalysisScope.Direct);

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
            graph,
            AnalysisScope.Direct);

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

        var updated = await _linker.UpdateReferencesAsync(parentKey, "# 명세", graph, AnalysisScope.Direct);

        Assert.Contains("../../../Functions/dbo.FN%20%28X%29%231/docs/Spec.md", updated);
    }

    /// <summary>
    /// 참조분석 OFF 회차는 그래프를 아예 만들지 않는다(DependencyAnalysisOrchestrator가
    /// 자식 발견 자체를 건너뛴다). 그 빈 그래프에 ON용 문장을 쓰면 문서가 "이 객체는
    /// 참조가 없다"고 단언하는데, 같은 폴더의 metadata.json은 피호출 객체를 나열한다 —
    /// 한 폴더 안에서 산출물이 서로를 부정한다. "없다"와 "안 물어봤다"는 갈려야 한다.
    /// </summary>
    [Fact]
    public async Task UpdateReferencesAsync_WhenScopeIsTransitive_DoesNotClaimTheObjectHasNoReferences()
    {
        var parentKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_PARENT", CodeObjectType.Procedure);

        var updated = await _linker.UpdateReferencesAsync(
            parentKey,
            "## 로직 흐름 요약\n본문",
            EmptyGraph(parentKey),
            AnalysisScope.Transitive);

        Assert.DoesNotContain("직접 참조하는 코드 객체가 없습니다", updated);
        Assert.Contains("참조분석을 끄고 분석해 직접 참조를 열거하지 않았습니다", updated);
    }

    /// <summary>
    /// ON에서 빈 그래프는 "열거해 봤고 하나도 없었다"는 사실이다. OFF 문장으로 덮으면
    /// 진짜 참조 없는 객체가 자기 사실을 잃는다 — 두 경우가 갈린다는 것이 요점이다.
    /// </summary>
    [Fact]
    public async Task UpdateReferencesAsync_WhenScopeIsDirectAndNoEdgesExist_StatesTheObjectHasNoReferences()
    {
        var parentKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_PARENT", CodeObjectType.Procedure);

        var updated = await _linker.UpdateReferencesAsync(
            parentKey,
            "## 로직 흐름 요약\n본문",
            EmptyGraph(parentKey),
            AnalysisScope.Direct);

        Assert.Contains("직접 참조하는 코드 객체가 없습니다", updated);
        Assert.DoesNotContain("참조분석을 끄고", updated);
    }

    private CodeObjectPipelineResult EmptyGraph(CodeObjectKey parentKey) =>
        new()
        {
            Nodes = new List<AnalysisNode>
            {
                new(parentKey) { Status = AnalysisNodeStatus.Succeeded, SpecPath = _paths.ResolveSpecPath(parentKey) }
            },
            DependencyEdges = new List<DependencyEdge>()
        };

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
