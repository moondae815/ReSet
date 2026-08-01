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
    public async Task AnalyzeAsync_NullSpecificationMarksNodeFailedAndDoesNotPublishAnalysisResult()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = null
            }));

        var result = await sut.AnalyzeAsync(root, Request(), CancellationToken.None);

        Assert.Equal(AnalysisNodeStatus.Failed, result.GetNode(root).Status);
        Assert.Contains("명세서", result.GetNode(root).Error);
        Assert.Empty(result.AnalysisResults);
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
    public async Task AnalyzeAsync_PersistsChildReviewScoreAndThinkingArtifacts()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-RecursiveReview-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = "# Spec",
                Review = key == child
                    ? new ReviewResult
                    {
                        ScoreAccuracy = 9,
                        ScoreCrud = 8,
                        ScoreInterface = 7,
                        ScoreReadability = 9,
                        ScoreException = 8
                    }
                    : null,
                ThinkingText = key == child ? "child private reasoning" : null
            }));

        try
        {
            await sut.AnalyzeAsync(
                root,
                Request(
                    outputDirectory: outputRoot,
                    modelName: "gpt-test",
                    actorEffort: "high"),
                CancellationToken.None);

            var docsDirectory = new OutputPathResolver(root.Database, outputRoot)
                .ResolveDocsDirectory(child);
            var childSpec = await File.ReadAllTextAsync(Path.Combine(docsDirectory, "Spec.md"));
            Assert.Contains("종합 신뢰도:", childSpec);
            Assert.Contains("정합성 점수: 9/10", childSpec);
            Assert.Contains("> [!NOTE]", childSpec);
            Assert.Contains("> **분석 AI 정보**: OpenAI (gpt-test, Effort: high)", childSpec);
            Assert.Contains(
                "> **AI 최종 신뢰도**: 82/100점 (정합성: 9, CRUD: 8, 연동: 7, 가독성: 9, 예외: 8)",
                childSpec);
            var thinking = await File.ReadAllTextAsync(Path.Combine(docsDirectory, "Thinking.md"));
            Assert.Contains("child private reasoning", thinking);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
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
    public async Task AnalyzeAsync_ShallowDiscoveryWinsOverLaterDepthExceededPath()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var branch = Key("USP_Branch", CodeObjectType.Procedure);
        var nested = Key("USP_Nested", CodeObjectType.Procedure);
        var shared = Key("FN_Shared", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(root, shared, branch),
            Definition(branch, nested),
            Definition(nested, shared),
            Definition(shared));
        var pipelineRequests = new List<CodeObjectKey>();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                pipelineRequests.Add(key);
                return Task.FromResult(PipelineResult(key));
            });

        var result = await sut.AnalyzeAsync(root, Request(maxDepth: 2), CancellationToken.None);

        Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(shared).Status);
        Assert.Equal(1, result.GetNode(shared).AnalysisAttempts);
        Assert.Equal(1, pipelineRequests.Count(key => key == shared));
        Assert.Contains(result.Edges, edge => edge.Source == root && edge.Target == shared);
        Assert.Contains(result.Edges, edge => edge.Source == nested && edge.Target == shared);
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
    public async Task AnalyzeAsync_UnknownExternalCodeObjectCreatesSkippedNodeWithoutMetadataLookup()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var rootDefinition = Definition(root);
        rootDefinition.Dependencies.Add(new DependencyInfo
        {
            SourceObjectKey = root,
            Database = "AuditDB",
            Schema = "dbo",
            Name = "FN_Audit",
            Type = "UNKNOWN"
        });
        var metadataRequests = new List<CodeObjectKey>();
        var metadata = Substitute.For<IDbMetadataService>();
        metadata.GetCodeObjectDetailsDirectAsync(
                Arg.Any<string>(),
                Arg.Any<CodeObjectKey>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                metadataRequests.Add(callInfo.ArgAt<CodeObjectKey>(1));
                return Task.FromResult(rootDefinition);
            });
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        var result = await sut.AnalyzeAsync(
            root,
            Request(allowExternalDatabaseConnections: true),
            CancellationToken.None);

        var skipped = Assert.Single(
            result.Nodes,
            node => node.Key.Database == "AuditDB" && node.Key.Name == "FN_Audit");
        Assert.Equal(CodeObjectType.Unresolved, skipped.Key.Type);
        Assert.Equal(AnalysisNodeStatus.SkippedExternal, skipped.Status);
        Assert.Equal(new[] { root }, metadataRequests);
    }

    [Theory]
    [InlineData("SQL_INLINE_TABLE_VALUED_FUNCTION", CodeObjectType.Function)]
    [InlineData("CLR_STORED_PROCEDURE", CodeObjectType.Procedure)]
    [InlineData("CLR_SCALAR_FUNCTION", CodeObjectType.Function)]
    [InlineData("CLR_TABLE_VALUED_FUNCTION", CodeObjectType.Function)]
    public async Task AnalyzeAsync_RecognizesSqlServerTypeDescriptions(
        string dependencyType,
        CodeObjectType expectedType)
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("CodeChild", expectedType);
        var rootDefinition = Definition(root);
        rootDefinition.Dependencies.Add(new DependencyInfo
        {
            SourceObjectKey = root,
            Schema = child.Schema,
            Name = child.Name,
            Type = dependencyType
        });
        var metadata = CreateMetadataService(rootDefinition, Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        var result = await sut.AnalyzeAsync(root, Request(), CancellationToken.None);

        Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(child).Status);
        Assert.Contains(result.Edges, edge => edge.Source == root && edge.Target == child);
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

    [Fact]
    public async Task AnalyzeAsync_SpecWriteFailureMarksChildFailedAndParentDoesNotLinkIt()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-RecursiveWriteFailure-{Guid.NewGuid():N}");
        var root = Key("ZZZ_Root", CodeObjectType.Procedure);
        var child = Key("AAA_Child", CodeObjectType.Function);
        var childSpecPath = new OutputPathResolver(root.Database, outputRoot).ResolveSpecPath(child);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)),
            new MetadataExporter(),
            new MechanicalValidator());

        try
        {
            Directory.CreateDirectory(childSpecPath);

            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                CancellationToken.None);

            Assert.Equal(AnalysisNodeStatus.Failed, result.GetNode(child).Status);
            Assert.False(string.IsNullOrWhiteSpace(result.GetNode(child).Error));
            var rootSpec = await File.ReadAllTextAsync(
                new OutputPathResolver(root.Database, outputRoot).ResolveSpecPath(root));
            Assert.Contains("분석 불가", rootSpec);
            Assert.DoesNotContain("](../../../Functions/dbo.AAA_Child/docs/Spec.md)", rootSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_PropagatesRootDatabaseAsAnalysisDatabaseToPipeline()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var externalFunction = CodeObjectKey.Create(
            "AuditDB", "dbo", "FN_Audit", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(root, externalFunction),
            Definition(externalFunction));
        var analysisDatabases = new List<string?>();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (request, key, _) =>
            {
                analysisDatabases.Add(request.AnalysisDatabase);
                return Task.FromResult(PipelineResult(key));
            });

        await sut.AnalyzeAsync(
            root,
            Request(allowExternalDatabaseConnections: true),
            CancellationToken.None);

        Assert.Equal(2, analysisDatabases.Count);
        Assert.All(analysisDatabases, database => Assert.Equal("PaymentDB", database));
    }

    private static DependencyAnalysisRequest Request(
        int maxDepth = 3,
        string outputDirectory = "/tmp/output",
        bool allowExternalDatabaseConnections = false,
        string modelName = "",
        string? actorEffort = null) => new()
    {
        ConnectionString = "Server=(local);Database=PaymentDB",
        MaxDepth = maxDepth,
        Provider = "OpenAI",
        Instructions = "rules",
        IsBatchMode = true,
        OutputDirectory = outputDirectory,
        ModelName = modelName,
        ActorEffort = actorEffort,
        AllowExternalDatabaseConnections = allowExternalDatabaseConnections
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
