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
    public async Task AnalyzeAsync_PreservesRootReviewAndThinkingForCliOutput()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var review = new ReviewResult { ScoreAccuracy = 9 };
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = "# Spec",
                Review = review,
                ThinkingText = "private reasoning"
            }));

        var result = await sut.AnalyzeAsync(root, Request(), CancellationToken.None);
        var rootAnalysis = Assert.Single(result.AnalysisResults);

        Assert.Same(review, rootAnalysis.Review);
        Assert.Equal("private reasoning", rootAnalysis.ThinkingText);
    }

    [Fact]
    public async Task AnalyzeAsync_ReportsEachCodeObjectBeforeItsPipelineStarts()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var progress = new List<DependencyAnalysisProgress>();
        var progressVisibleWhenPipelineStarts = new List<CodeObjectKey>();
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                if (progress.Any(item => item.Key == key))
                {
                    progressVisibleWhenPipelineStarts.Add(key);
                }

                return Task.FromResult(PipelineResult(key));
            });

        await sut.AnalyzeAsync(root, new DependencyAnalysisRequest
        {
            ConnectionString = "Server=(local);Database=PaymentDB",
            MaxDepth = 3,
            Provider = "OpenAI",
            Instructions = "rules",
            IsBatchMode = true,
            Progress = progress.Add
        });

        Assert.Collection(
            progress,
            item =>
            {
                Assert.Equal(child, item.Key);
                Assert.Equal(1, item.Current);
                Assert.Equal(2, item.Total);
            },
            item =>
            {
                Assert.Equal(root, item.Key);
                Assert.Equal(2, item.Current);
                Assert.Equal(2, item.Total);
            });
        Assert.Equal(new[] { child, root }, progressVisibleWhenPipelineStarts);
    }

    [Fact]
    public async Task AnalyzeAsync_ReportsFixedTotalAfterDiscoveringAllAnalysisTargets()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var firstChild = Key("FN_First", CodeObjectType.Function);
        var secondChild = Key("FN_Second", CodeObjectType.Function);
        var progress = new List<DependencyAnalysisProgress>();
        var metadata = CreateMetadataService(
            Definition(root, firstChild, secondChild),
            Definition(firstChild),
            Definition(secondChild));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        await sut.AnalyzeAsync(root, new DependencyAnalysisRequest
        {
            ConnectionString = "Server=(local);Database=PaymentDB",
            MaxDepth = 3,
            Provider = "OpenAI",
            Instructions = "rules",
            IsBatchMode = true,
            Progress = progress.Add
        });

        Assert.Equal(3, progress.Count);
        Assert.All(progress, item => Assert.Equal(3, item.Total));
        Assert.Equal(new[] { firstChild, secondChild, root }, progress.Select(item => item.Key));
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

    [Fact]
    public async Task AnalyzeAsync_PersistsArtifactsAndLinksAfterRecursiveAnalysisCompletes()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-RecursiveArtifacts-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)),
            new MetadataExporter(),
            new MechanicalValidator());
        var request = Request(outputDirectory: outputRoot);

        try
        {
            await sut.AnalyzeAsync(root, request, CancellationToken.None);

            var rootDirectory = Path.Combine(outputRoot, "Procedures", "dbo.USP_Root");
            Assert.True(File.Exists(Path.Combine(rootDirectory, "docs", "Spec.md")));
            Assert.True(File.Exists(Path.Combine(outputRoot, "Objects", "dbo.USP_Root.Procedure", "raw", "object_definition.sql")));
            Assert.True(File.Exists(Path.Combine(outputRoot, "Procedures", "dbo.USP_Root", "raw", "dependency-manifest.json")));
            var rootSpec = await File.ReadAllTextAsync(Path.Combine(rootDirectory, "docs", "Spec.md"));
            Assert.Contains("[dbo.FN\\_Child](../../../Functions/dbo.FN_Child/docs/Spec.md)", rootSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    private static DependencyAnalysisRequest Request(int maxDepth = 3, string outputDirectory = "/tmp/output") => new()
    {
        ConnectionString = "Server=(local);Database=PaymentDB",
        MaxDepth = maxDepth,
        Provider = "OpenAI",
        Instructions = "rules",
        IsBatchMode = true,
        OutputDirectory = outputDirectory
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
