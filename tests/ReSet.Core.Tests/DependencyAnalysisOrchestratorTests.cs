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
                // VerificationOutcome의 0번(기본) 값은 ReviewNotRun이므로, 이 픽스처가 실제로
                // 의도하는 "정상 통과"를 명시적으로 밝혀야 VerificationDocumentFormatter가
                // 점수를 숨기지 않는다.
                Outcome = VerificationOutcome.Passed,
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
    public async Task AnalyzeAsync_ArtifactRootUnwritable_ReportsPersistenceFailureInsteadOfLoggingSilently()
    {
        // 저장이 통째로 실패했는데 화면에 성공 패널이 뜨던 결함.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-PersistFail-{Guid.NewGuid():N}");
        // 출력 루트 자리에 파일을 만들어 하위 디렉터리 생성을 실패시킨다.
        await File.WriteAllTextAsync(outputRoot, "not a directory");

        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                CancellationToken.None);

            Assert.Equal(ArtifactPersistence.Failed, result.Persistence);
            Assert.NotEmpty(result.PersistenceErrors);
        }
        finally
        {
            if (File.Exists(outputRoot)) File.Delete(outputRoot);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_SuccessfulRun_ReportsPersisted()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Persisted-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                CancellationToken.None);

            Assert.Equal(ArtifactPersistence.Persisted, result.Persistence);
            Assert.Empty(result.PersistenceErrors);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_ThinkingLogCarriesTheAnalysisModelIdentity()
    {
        // 재귀 모드의 하위 객체 Thinking.md가 루트보다 정보가 적을 이유가 없다.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Thinking-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = "# Spec",
                ThinkingText = "private reasoning"
            }));

        try
        {
            await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot, modelName: "gpt-test", actorEffort: "high"),
                CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var thinkingPath = Path.Combine(paths.ResolveDocsDirectory(root), "Thinking.md");
            var thinking = await File.ReadAllTextAsync(thinkingPath);

            Assert.Contains("**기본 분석 AI 정보**: OpenAI (gpt-test, Effort: high)", thinking);
            Assert.Contains("**문서 작성일시**:", thinking);
            Assert.Contains("private reasoning", thinking);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    /// <summary>
    /// 캐시 히트 회차는 ThinkingText가 비어 있다(VerificationPipelineOrchestrator가
    /// AI를 호출한 회차에만 채운다). 그 빈 값으로 덮으면 앞선 회차의 추론 기록이
    /// 「추론 없음」 자리표시자와 오늘 날짜로 사라진다 — raw/prompt-context.md가
    /// MetadataExporter에서 보호받는 것과 같은 사건이다.
    /// 파일이 아예 없을 때 자리표시자 판본을 남기는 계약은 그대로 지킨다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_PreservesExistingThinkingLogWhenReasoningIsEmpty()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-ThinkingCache-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var paths = new OutputPathResolver(root.Database, outputRoot);
        var thinkingPath = Path.Combine(paths.ResolveDocsDirectory(root), "Thinking.md");

        try
        {
            // 1회차: 실제로 AI를 호출해 추론을 남겼다.
            var analyzing = new DependencyAnalysisOrchestrator(
                metadata,
                (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
                {
                    SpDef = Definition(key),
                    SpecMarkdown = "# Spec",
                    ThinkingText = "private reasoning from attempt 1"
                }));
            await analyzing.AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            // 2회차: 캐시 히트라 추론 본문이 없다.
            var cached = new DependencyAnalysisOrchestrator(
                metadata,
                (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
                {
                    SpDef = Definition(key),
                    SpecMarkdown = "# Spec",
                    ThinkingText = null,
                    FromCache = true
                }));
            await cached.AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var thinking = await File.ReadAllTextAsync(thinkingPath);
            Assert.Contains("private reasoning from attempt 1", thinking);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    /// <summary>
    /// 위 보존 규칙이 「파일이 없으면 반드시 만든다」를 깨뜨리지 않는지 함께 잠근다.
    /// 파일 없음과 추론 없음은 산출물만 보고 구분되어야 한다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WritesPlaceholderThinkingLogWhenFileIsAbsent()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-ThinkingEmpty-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = "# Spec",
                ThinkingText = null
            }));

        try
        {
            await sut.AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var thinkingPath = Path.Combine(paths.ResolveDocsDirectory(root), "Thinking.md");
            Assert.True(File.Exists(thinkingPath));
            Assert.Contains("# AI 추론 과정 로그", await File.ReadAllTextAsync(thinkingPath));
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    /// <summary>
    /// 보존 규칙의 세 번째 축: 추론이 <b>있는</b> 회차는 이미 있는 Thinking.md를 갱신해야 한다.
    /// 이 축이 없으면 가드를 <c>if (File.Exists(thinkingPath)) return;</c>로 바꾼 뮤턴트가
    /// 위 두 테스트를 포함해 전 테스트를 조용히 통과한다 — 두 테스트 모두 새 임시
    /// 디렉터리에서 시작해 「파일이 이미 있고 추론도 있는」 회차에 닿지 않기 때문이다.
    /// 그 뮤턴트는 Thinking.md를 첫 회차 이후 영구 동결시켜 새 추론이 절대 반영되지 않게
    /// 하는데, 같은 출력 디렉터리에 반복해 도는 것은 캐시 히트가 아닌 평범한 재분석의
    /// 정상 사용법이다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_OverwritesExistingThinkingLogWhenReasoningIsPresent()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-ThinkingRefresh-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var paths = new OutputPathResolver(root.Database, outputRoot);
        var thinkingPath = Path.Combine(paths.ResolveDocsDirectory(root), "Thinking.md");

        DependencyAnalysisOrchestrator Analyzing(string reasoning) => new(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = "# Spec",
                ThinkingText = reasoning
            }));

        try
        {
            // 1회차와 2회차 모두 AI를 호출해 서로 다른 추론을 남겼다.
            await Analyzing("private reasoning from attempt 1").AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);
            await Analyzing("private reasoning from attempt 2").AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var thinking = await File.ReadAllTextAsync(thinkingPath);
            Assert.Contains("private reasoning from attempt 2", thinking);
            Assert.DoesNotContain("private reasoning from attempt 1", thinking);
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

    [Fact]
    public void DependencyAnalysisRequest_ToString_MasksConnectionString()
    {
        // record 자동 생성 ToString에 자격 증명이 섞여 로그로 새는 것을 막는다.
        var request = Request() with
        {
            ConnectionString = "Server=(local);Database=PaymentDB;User Id=sa;Password=super-secret"
        };

        var text = request.ToString();

        Assert.DoesNotContain("super-secret", text);
        Assert.DoesNotContain("Password", text);
        Assert.Contains("ConnectionString = ***", text);
        Assert.Contains("MaxDepth = 3", text);
    }

    [Fact]
    public async Task AnalyzeAsync_ProductionPipelineWiring_ResolvesExternalObjectCacheUnderExternalDirectory()
    {
        // 델리게이트가 아닌 실제 VerificationPipelineOrchestrator를 사용해
        // AnalysisDatabase가 프로덕션 생성자를 통해 OutputPathResolver까지 도달하는지 확인한다.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-ExternalWiring-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var externalFunction = CodeObjectKey.Create(
            "AuditDB", "dbo", "FN_Audit", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(root, externalFunction),
            Definition(externalFunction));
        var cacheManager = Substitute.For<ICacheManager>();
        cacheManager.ComputeCompositeHash(Arg.Any<SpDefinition>(), Arg.Any<int>())
            .Returns("fake-hash");
        cacheManager.IsCacheValid(
                Arg.Any<CodeObjectKey>(),
                Arg.Any<string>(),
                Arg.Any<OutputPathResolver>())
            .Returns(true);

        var paths = new OutputPathResolver(root.Database, outputRoot);
        var rootSpecPath = paths.ResolveSpecPath(root);
        var externalSpecPath = paths.ResolveSpecPath(externalFunction);
        Directory.CreateDirectory(Path.GetDirectoryName(rootSpecPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(externalSpecPath)!);
        await File.WriteAllTextAsync(rootSpecPath, "# 루트 캐시 명세");
        await File.WriteAllTextAsync(externalSpecPath, "# 외부 캐시 명세");

        var pipeline = new VerificationPipelineOrchestrator(
            metadata,
            Substitute.For<IAiService>(),
            new MechanicalValidator(),
            Substitute.For<IVerificationUserInteraction>(),
            cacheManager: cacheManager);
        var sut = new DependencyAnalysisOrchestrator(metadata, pipeline);

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot, allowExternalDatabaseConnections: true)
                    with { EnableCache = true },
                CancellationToken.None);

            Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(externalFunction).Status);
            Assert.Equal(
                Path.Combine(
                    outputRoot, "External", "AuditDB", "Functions", "dbo.FN_Audit", "docs", "Spec.md"),
                externalSpecPath);
            cacheManager.Received(1).IsCacheValid(
                externalFunction,
                "fake-hash",
                Arg.Is<OutputPathResolver>(resolver =>
                    resolver.ResolveSpecPath(externalFunction) == externalSpecPath));
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_AllowingExternalDatabasesWritesSpecUnderExternalDirectory()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(), $"ReSet-ExternalDatabase-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var externalFunction = CodeObjectKey.Create(
            "AuditDB", "dbo", "FN_Audit", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(root, externalFunction),
            Definition(externalFunction));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)),
            new MetadataExporter(),
            new MechanicalValidator());

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot, allowExternalDatabaseConnections: true),
                CancellationToken.None);

            var node = result.GetNode(externalFunction);
            Assert.Equal(AnalysisNodeStatus.Succeeded, node.Status);
            Assert.Equal(
                Path.Combine(
                    outputRoot, "External", "AuditDB", "Functions", "dbo.FN_Audit", "docs", "Spec.md"),
                node.SpecPath);
            Assert.True(File.Exists(node.SpecPath));

            var rootSpec = await File.ReadAllTextAsync(
                Path.Combine(outputRoot, "Procedures", "dbo.USP_Root", "docs", "Spec.md"));
            Assert.Contains(
                "[dbo.FN\\_Audit](../../../External/AuditDB/Functions/dbo.FN_Audit/docs/Spec.md)",
                rootSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_ExternalMetadataFailureIsSurfacedAsFailedNode()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(), $"ReSet-ExternalDatabaseFailure-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var externalFunction = CodeObjectKey.Create(
            "AuditDB", "dbo", "FN_Audit", CodeObjectType.Function);
        var metadata = Substitute.For<IDbMetadataService>();
        metadata.GetCodeObjectDetailsDirectAsync(
                Arg.Any<string>(),
                Arg.Any<CodeObjectKey>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<CodeObjectKey>(1) == root
                ? Task.FromResult(Definition(root, externalFunction))
                : Task.FromException<SpDefinition>(new InvalidOperationException(
                    "'[AuditDB].[dbo].[FN_Audit]'의 SQL Server 객체 타입을 찾을 수 없습니다.")));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)),
            new MetadataExporter(),
            new MechanicalValidator());

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot, allowExternalDatabaseConnections: true),
                CancellationToken.None);

            Assert.Equal(AnalysisNodeStatus.Failed, result.GetNode(externalFunction).Status);
            Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(root).Status);

            var rootSpec = await File.ReadAllTextAsync(
                Path.Combine(outputRoot, "Procedures", "dbo.USP_Root", "docs", "Spec.md"));
            Assert.Contains("분석 불가", rootSpec);
            Assert.DoesNotContain("분석 생략", rootSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_NormalizesGraphKeysToCatalogObjectNameCasing()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var catalogKey = Key("UF_GET_WORKDAY2", CodeObjectType.Function);
        var callSiteKey = Key("UF_Get_WorkDay2", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(root, callSiteKey),
            Definition(catalogKey));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        var result = await sut.AnalyzeAsync(root, Request(), CancellationToken.None);

        Assert.Equal("UF_GET_WORKDAY2", result.GetNode(catalogKey).Key.Name);
        Assert.Equal(
            "UF_GET_WORKDAY2",
            result.Edges.Single(edge => edge.Source == root).Target.Name);
    }

    [Fact]
    public async Task AnalyzeAsync_CancelledMidGraph_PersistsCompletedObjectsAndReportsPartialCompletion()
    {
        // 완료된 객체의 AI 비용이 취소로 버려지면 안 된다.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Cancel-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var doneChild = Key("FN_Done", CodeObjectType.Function);
        var cancelledChild = Key("FN_Cancelled", CodeObjectType.Function);
        var metadata = CreateMetadataService(
            Definition(root, doneChild, cancelledChild),
            Definition(doneChild),
            Definition(cancelledChild));
        using var cts = new CancellationTokenSource();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                if (key == cancelledChild)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return Task.FromResult(PipelineResult(key));
            });

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                cts.Token);

            Assert.Equal(GraphCompletion.PartialCancelled, result.Completion);
            Assert.Equal(AnalysisNodeStatus.Succeeded, result.GetNode(doneChild).Status);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            Assert.True(File.Exists(paths.ResolveSpecPath(doneChild)));
            Assert.False(File.Exists(paths.ResolveSpecPath(cancelledChild)));
            Assert.False(File.Exists(paths.ResolveSpecPath(root)));
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_CompletedGraph_ReportsCompleteCompletion()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Complete-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                CancellationToken.None);

            Assert.Equal(GraphCompletion.Complete, result.Completion);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_CyclicGraphCancelled_AddsTheUnresolvedReferenceBannerToTheSurvivingDocument()
    {
        // 후위 순회라 보통은 부모가 자식보다 뒤에 실행되지만, 순환에서는
        // TryRegisterDepth가 재진입을 막아 자식이 부모보다 뒤에 온다.
        // 이때만 성공한 문서의 참조 목록에 미완료 항목이 남는다.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-CycleBanner-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var partner = Key("USP_Partner", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(
            Definition(root, partner),
            Definition(partner, root));
        using var cts = new CancellationTokenSource();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                if (key == root)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }

                return Task.FromResult(PipelineResult(key));
            });

        try
        {
            var result = await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot),
                cts.Token);

            Assert.Equal(GraphCompletion.PartialCancelled, result.Completion);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var partnerSpec = await File.ReadAllTextAsync(paths.ResolveSpecPath(partner));

            Assert.Contains("[참조 미완]", partnerSpec);
            Assert.Contains("dbo.USP_Root", partnerSpec);
            // 배너와 참조 섹션이 같은 사실을 말해야 한다.
            Assert.Contains("분석 취소", partnerSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_CompletedGraph_LeavesNoUnresolvedReferenceBanner()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-NoBanner-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            await sut.AnalyzeAsync(root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var rootSpec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));

            Assert.DoesNotContain("[참조 미완]", rootSpec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_WritesDirectAnalysisScopeIntoEverySpecification()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Scope-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            await sut.AnalyzeAsync(root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);

            Assert.Contains("분석 범위: 직접 의존성", await File.ReadAllTextAsync(paths.ResolveSpecPath(root)));
            Assert.Contains("분석 범위: 직접 의존성", await File.ReadAllTextAsync(paths.ResolveSpecPath(child)));
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_CachedNode_KeepsTheOriginalAnalysisTimestamp()
    {
        // 캐시 히트는 AI를 호출하지 않았다. 링크 갱신 때문에 파일은 다시
        // 써야 하지만 작성일시까지 새로 찍으면 거짓 주장이 된다.
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-CacheStampGraph-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var analyzedAt = new DateTime(2026, 8, 1, 14, 22, 3);
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key),
                SpecMarkdown = "# Spec",
                FromCache = true,
                AnalyzedAt = analyzedAt
            }));

        try
        {
            await sut.AnalyzeAsync(root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var spec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));

            Assert.Contains("**문서 작성일시**: 2026-08-01 14:22:03", spec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyRootDatabase_ThrowsInsteadOfSilentlySkippingAllArtifacts()
    {
        // 빈 DB명은 OutputPathResolver 생성을 막아 모든 산출물을 조용히
        // 사라지게 했다. 호출부 결함이므로 즉시 드러낸다.
        var root = CodeObjectKey.Create("", "dbo", "USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => sut.AnalyzeAsync(root, Request(), CancellationToken.None));

        Assert.Contains("데이터베이스", exception.Message);
    }

    /// <summary>
    /// 참조분석 OFF는 "깊이 0"이 아니라 "그래프를 만들지 않는다"이다. 자식을 발견해
    /// 실행 목록에 넣으면 OFF에서도 자식마다 AI 비용이 나가고, 자식 Spec.md가 생겨
    /// 사용자가 고른 것과 다른 산출물이 남는다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenReferencesAreDisabled_AnalyzesRootOnly()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var executed = new List<CodeObjectKey>();
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) =>
            {
                executed.Add(key);
                return Task.FromResult(PipelineResult(key));
            });

        var result = await sut.AnalyzeAsync(
            root,
            Request() with { AnalyzeReferencedCodeObjects = false },
            CancellationToken.None);

        Assert.Equal(new[] { root }, executed);
        Assert.Empty(result.Edges);
        Assert.Equal(root, Assert.Single(result.Nodes).Key);
    }

    /// <summary>
    /// OFF 회차의 명세서는 전이적으로 모은 메타데이터로 쓰인다. 머리에 "직접 의존성"이
    /// 박히면 문서가 자기 수집 범위를 거짓으로 신고한다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenReferencesAreDisabled_StampsTransitiveScope()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Scope-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot) with { AnalyzeReferencedCodeObjects = false },
                CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var spec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));
            Assert.Contains("분석 범위: 전이 의존성", spec);
            Assert.DoesNotContain("분석 범위: 직접 의존성", spec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    /// <summary>
    /// 머리의 「분석 범위」 도장만 고쳐서는 부족하다. 같은 문서의 「참조 코드 객체」 절이
    /// OFF의 빈 그래프를 보고 "직접 참조하는 코드 객체가 없습니다"라고 쓰면, 옆의
    /// metadata.json이 피호출 객체를 나열하는 동안 명세서 혼자 반대를 말한다. 그리고 그
    /// 문장은 여기서 끝나지 않는다 — PersistArtifactsAsync가 링커 결과를
    /// analysis.SpecMarkdown에 되쓰고, 그것이 지시서 번들을 거쳐 코딩 에이전트에게 간다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenReferencesAreDisabled_DoesNotClaimTheObjectHasNoReferences()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-RefClause-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            await sut.AnalyzeAsync(
                root,
                Request(outputDirectory: outputRoot) with { AnalyzeReferencedCodeObjects = false },
                CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var spec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));
            Assert.DoesNotContain("직접 참조하는 코드 객체가 없습니다", spec);
            Assert.Contains("참조분석을 끄고 분석해 직접 참조를 열거하지 않았습니다", spec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    /// <summary>
    /// ON에서 정말로 참조가 없는 객체는 그 사실을 계속 신고해야 한다. OFF 문구가
    /// 두 경우를 모두 덮으면 이번 수정은 거짓을 다른 거짓으로 바꾼 것에 지나지 않는다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenReferencesAreEnabledAndRootHasNone_StatesTheObjectHasNoReferences()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-RefClauseOn-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var metadata = CreateMetadataService(Definition(root));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            await sut.AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            var paths = new OutputPathResolver(root.Database, outputRoot);
            var spec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));
            Assert.Contains("직접 참조하는 코드 객체가 없습니다", spec);
            Assert.DoesNotContain("참조분석을 끄고", spec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
    }

    /// <summary>
    /// 결과가 담는 정의는 파이프라인이 수집한 그것이어야 한다 — 발견 단계가 쓴
    /// 직접 의존성 판본으로 바뀌면 안 된다. 이 정의의 Dependencies가 CLI의 spDefs를
    /// 거쳐 StepInterfaceFacts.BuildCallGraph로 흘러가고, 그것이 계획서 Narrow 모드의
    /// 1-hop 이웃을 고른다. 얇아져도 명세서는 멀쩡해 보이고 계획서 단계 본문만
    /// 조용히 나빠지므로, 여기서 잠근다(설계 §2.1).
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_WhenReferencesAreDisabled_CarriesPipelineCollectedDependencies()
    {
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var neighbour = Key("USP_Neighbour", CodeObjectType.Procedure);

        // 발견 단계가 보는 정의에는 의존성이 없다.
        var metadata = CreateMetadataService(Definition(root));

        // 파이프라인은 전이적으로 모아 이웃을 담아 돌려준다.
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(new CodeObjectPipelineResult
            {
                SpDef = Definition(key, neighbour),
                SpecMarkdown = "# Spec"
            }));

        var result = await sut.AnalyzeAsync(
            root,
            Request() with { AnalyzeReferencedCodeObjects = false },
            CancellationToken.None);

        var analysis = Assert.Single(result.AnalysisResults);
        var dependency = Assert.Single(analysis.Definition!.Dependencies);
        Assert.Equal("USP_Neighbour", dependency.Name);
    }

    /// <summary>
    /// 재귀 모드는 지금 그대로여야 한다. 플래그의 기본값이 뒤집히면 기존 사용자가
    /// 아무것도 바꾸지 않았는데 산출물이 줄어든다.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_ByDefault_StillAnalyzesReferencesAndStampsDirectScope()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-ScopeDirect-{Guid.NewGuid():N}");
        var root = Key("USP_Root", CodeObjectType.Procedure);
        var child = Key("FN_Child", CodeObjectType.Function);
        var metadata = CreateMetadataService(Definition(root, child), Definition(child));
        var sut = new DependencyAnalysisOrchestrator(
            metadata,
            (_, key, _) => Task.FromResult(PipelineResult(key)));

        try
        {
            var result = await sut.AnalyzeAsync(
                root, Request(outputDirectory: outputRoot), CancellationToken.None);

            Assert.Equal(2, result.Nodes.Count);
            var paths = new OutputPathResolver(root.Database, outputRoot);
            var spec = await File.ReadAllTextAsync(paths.ResolveSpecPath(root));
            Assert.Contains("분석 범위: 직접 의존성", spec);
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
        }
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
