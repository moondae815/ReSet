using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests;

public sealed class DependencyAnalysisOrchestratorTests
{
    [Fact]
    public async Task AnalyzeAsync_AnalyzesSharedFunctionOnlyOnceAndLinksBothCallers()
    {
        var rootA = Key("USP_A", CodeObjectType.Procedure);
        var rootB = Key("USP_B", CodeObjectType.Procedure);
        var functionX = Key("FN_X", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(rootA, rootB, functionX),
            Definition(rootB, functionX),
            Definition(functionX));
        var executionOrder = new List<CodeObjectKey>();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (request, key, _) =>
            {
                executionOrder.Add(key);
                return Task.FromResult(PipelineResult(key));
            });

        var result = await sut.AnalyzeAsync(rootA, Request(), CancellationToken.None);

        Assert.Equal(1, result.Nodes.Single(node => node.Key == functionX).AnalysisAttempts);
        Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(functionX).Status);
        Assert.Contains(result.Edges, edge => edge.Source == rootA && edge.Target == functionX);
        Assert.Contains(result.Edges, edge => edge.Source == rootB && edge.Target == functionX);
        Assert.True(executionOrder.IndexOf(functionX) < executionOrder.IndexOf(rootB));
        Assert.True(executionOrder.IndexOf(rootB) < executionOrder.IndexOf(rootA));
    }

    [Fact]
    public async Task AnalyzeAsync_CycleDoesNotRequeueRunningObject()
    {
        var cyclicA = Key("USP_A", CodeObjectType.Procedure);
        var cyclicB = Key("USP_B", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(
            Definition(cyclicA, cyclicB),
            Definition(cyclicB, cyclicA));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        var result = await sut.AnalyzeAsync(cyclicA, Request(), CancellationToken.None);

        Assert.Equal(1, result.GetNode(cyclicA).AnalysisAttempts);
        Assert.Equal(1, result.GetNode(cyclicB).AnalysisAttempts);
        Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(cyclicA).Status);
        Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(cyclicB).Status);
    }

    [Fact]
    public async Task AnalyzeAsync_ChildFailureDoesNotFailRoot()
    {
        var rootA = Key("USP_A", CodeObjectType.Procedure);
        var failingChild = Key("FN_Fail", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(rootA, failingChild),
            Definition(failingChild));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => key == failingChild
                ? Task.FromException<CodeObjectPipelineResult>(new InvalidOperationException("AI request failed"))
                : Task.FromResult(PipelineResult(key)));

        var result = await sut.AnalyzeAsync(rootA, Request(), CancellationToken.None);

        Assert.Equal(AnalysisNodeStatus.Failed, result.GetNode(failingChild).Status);
        Assert.Equal("AI request failed", result.GetNode(failingChild).Error);
        Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(rootA).Status);
    }

    private static DependencyAnalysisRequest Request() => new()
    {
        ConnectionString = "Server=(local);Database=PaymentDB",
        MaxDepth = 3,
        Provider = "OpenAI",
        Instructions = "rules",
        IsBatchMode = true,
        OutputDirectory = "/tmp/output"
    };

    private static CodeObjectKey Key(string name, CodeObjectType type) =>
        CodeObjectKey.Create("PaymentDB", "dbo", name, type);

    private static SpDefinition Definition(CodeObjectKey key, params CodeObjectKey[] dependencies) => new()
    {
        ObjectKey = key,
        ObjectType = key.Type,
        Schema = key.Schema,
        Name = key.Name,
        DdlText = $"CREATE {key.Type} {key.Schema}.{key.Name}",
        Dependencies = dependencies.Select(dependency => new DependencyInfo
        {
            SourceObjectKey = key,
            Database = dependency.Database,
            Schema = dependency.Schema,
            Name = dependency.Name,
            Type = dependency.Type == CodeObjectType.Procedure ? "PROCEDURE" : "FUNCTION",
            DiscoveryDepth = 1
        }).ToList()
    };

    private static IDbMetadataService CreateMetadataService(params SpDefinition[] definitions)
    {
        var definitionsByKey = definitions.ToDictionary(definition => definition.ObjectKey!);
        var metadata = Substitute.For<IDbMetadataService>();
        metadata.GetCodeObjectDetailsAsync(
                Arg.Any<string>(),
                Arg.Any<CodeObjectKey>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(definitionsByKey[callInfo.ArgAt<CodeObjectKey>(1)]));
        return metadata;
    }

    private static CodeObjectPipelineResult PipelineResult(CodeObjectKey key) => new()
    {
        SpDef = new SpDefinition
        {
            ObjectKey = key,
            ObjectType = key.Type,
            Schema = key.Schema,
            Name = key.Name
        },
        SpecMarkdown = "# Spec"
    };
}
