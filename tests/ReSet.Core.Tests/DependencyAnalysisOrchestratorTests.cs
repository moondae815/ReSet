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

    [Fact]
    public async Task AnalyzeAsync_UsesTraversalDepthToSkipGrandchildBeyondMaximum()
    {
        var rootA = Key("USP_A", CodeObjectType.Procedure);
        var childB = Key("USP_B", CodeObjectType.Procedure);
        var grandchildC = Key("FN_C", CodeObjectType.Function);
        var definitions = new Dictionary<CodeObjectKey, SpDefinition>
        {
            [rootA] = Definition(rootA, childB),
            [childB] = Definition(childB, grandchildC),
            [grandchildC] = Definition(grandchildC)
        };
        var metadataRequests = new List<CodeObjectKey>();
        var pipelineRequests = new List<CodeObjectKey>();
        var metadata = Substitute.For<IDbMetadataService>();
        metadata.GetCodeObjectDetailsAsync(
                Arg.Any<string>(),
                Arg.Any<CodeObjectKey>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.ArgAt<CodeObjectKey>(1);
                metadataRequests.Add(key);
                return Task.FromResult(definitions[key]);
            });
        metadata.GetCodeObjectDetailsDirectAsync(
                Arg.Any<string>(),
                Arg.Any<CodeObjectKey>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.ArgAt<CodeObjectKey>(1);
                metadataRequests.Add(key);
                return Task.FromResult(definitions[key]);
            });
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                pipelineRequests.Add(key);
                return Task.FromResult(PipelineResult(key));
            });

        var result = await sut.AnalyzeAsync(rootA, Request(maxDepth: 1), CancellationToken.None);

        var skipped = result.GetNode(grandchildC);
        Assert.Equal(AnalysisNodeStatus.SkippedDepth, skipped.Status);
        Assert.Contains("최대 의존성 깊이(1)", skipped.Error);
        Assert.DoesNotContain(grandchildC, metadataRequests);
        Assert.DoesNotContain(grandchildC, pipelineRequests);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesDirectMetadataAndSkipsExternalObjectBeforeAdditionalLookup()
    {
        var rootA = Key("USP_A", CodeObjectType.Procedure);
        var externalFunction = CodeObjectKey.Create("AuditDB", "dbo", "FN_Audit", CodeObjectType.Function);
        var directMetadataRequests = new List<CodeObjectKey>();
        var pipelineRequests = new List<CodeObjectKey>();
        var metadata = Substitute.For<IDbMetadataService>();
        metadata.GetCodeObjectDetailsAsync(
                Arg.Any<string>(),
                Arg.Any<CodeObjectKey>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SpDefinition>(
                new InvalidOperationException("재귀 메타데이터 조회를 사용하면 안 됩니다.")));
        metadata.GetCodeObjectDetailsDirectAsync(
                Arg.Any<string>(),
                Arg.Any<CodeObjectKey>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.ArgAt<CodeObjectKey>(1);
                directMetadataRequests.Add(key);
                return Task.FromResult(Definition(rootA, externalFunction));
            });
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                pipelineRequests.Add(key);
                return Task.FromResult(PipelineResult(key));
            });

        var result = await sut.AnalyzeAsync(rootA, Request(), CancellationToken.None);

        var skipped = result.GetNode(externalFunction);
        Assert.Equal(AnalysisNodeStatus.SkippedExternal, skipped.Status);
        Assert.Contains("외부 데이터베이스 연결", skipped.Error);
        Assert.Equal(new[] { rootA }, directMetadataRequests);
        Assert.DoesNotContain(externalFunction, pipelineRequests);
    }

    private static DependencyAnalysisRequest Request(int maxDepth = 3) => new()
    {
        ConnectionString = "Server=(local);Database=PaymentDB",
        MaxDepth = maxDepth,
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
        metadata.GetCodeObjectDetailsDirectAsync(
                Arg.Any<string>(),
                Arg.Any<CodeObjectKey>(),
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
