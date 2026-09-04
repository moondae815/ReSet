using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests;

public sealed class SpAnalysisOutcomeTests
{
    [Fact]
    public void DefaultValues_AreTheSafeSideOfEachEnum()
    {
        // 대입을 빠뜨린 생성부가 "저장했다"거나 "끝까지 돌았다"고 자칭하지 않아야 한다.
        var outcome = new SpAnalysisOutcome();

        Assert.Equal(GraphCompletion.Complete, outcome.Completion);
        Assert.Equal(ArtifactPersistence.NotAttempted, outcome.Persistence);
        Assert.Empty(outcome.PersistenceErrors);
    }

    [Fact]
    public void FromDependencyGraph_CarriesTheRootAnalysisAndGraphPersistence()
    {
        var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Root", CodeObjectType.Procedure);
        var definition = new SpDefinition { ObjectKey = rootKey, Schema = "dbo", Name = "USP_Root" };
        var analyzedAt = new DateTime(2026, 8, 1, 9, 0, 0);
        var result = new CodeObjectPipelineResult
        {
            Completion = GraphCompletion.PartialCancelled,
            Persistence = ArtifactPersistence.Failed,
            PersistenceErrors = { "디스크 쓰기 거부" },
            AnalysisResults =
            {
                new CodeObjectAnalysisResult
                {
                    Key = rootKey,
                    Definition = definition,
                    SpecMarkdown = "# 루트",
                    Outcome = VerificationOutcome.Passed,
                    FromCache = true,
                    AnalyzedAt = analyzedAt
                }
            }
        };

        var outcome = SpAnalysisOutcome.FromDependencyGraph(result, rootKey);

        Assert.Equal("# 루트", outcome.SpecMarkdown);
        Assert.Equal(GraphCompletion.PartialCancelled, outcome.Completion);
        Assert.Equal(ArtifactPersistence.Failed, outcome.Persistence);
        Assert.Equal(new[] { "디스크 쓰기 거부" }, outcome.PersistenceErrors);
        Assert.True(outcome.FromCache);
        Assert.Equal(analyzedAt, outcome.AnalyzedAt);
    }

    [Fact]
    public void FromDependencyGraph_UsesTheRootNodeCacheStateNotAChildsState()
    {
        // 자식이 캐시였다고 루트까지 캐시였다고 말하면 안 된다.
        var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Root", CodeObjectType.Procedure);
        var childKey = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Child", CodeObjectType.Function);
        var result = new CodeObjectPipelineResult
        {
            AnalysisResults =
            {
                new CodeObjectAnalysisResult
                {
                    Key = childKey,
                    SpecMarkdown = "# 자식",
                    FromCache = true,
                    AnalyzedAt = new DateTime(2026, 7, 1, 0, 0, 0)
                },
                new CodeObjectAnalysisResult
                {
                    Key = rootKey,
                    SpecMarkdown = "# 루트",
                    FromCache = false
                }
            }
        };

        var outcome = SpAnalysisOutcome.FromDependencyGraph(result, rootKey);

        Assert.False(outcome.FromCache);
        Assert.Null(outcome.AnalyzedAt);
    }

    [Fact]
    public void FromDependencyGraph_MissingRoot_ReportsNoSpecificationAndNoReview()
    {
        var rootKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Root", CodeObjectType.Procedure);
        var result = new CodeObjectPipelineResult();

        var outcome = SpAnalysisOutcome.FromDependencyGraph(result, rootKey);

        Assert.Null(outcome.SpecMarkdown);
        Assert.Null(outcome.Definition);
        Assert.Null(outcome.Review);
        Assert.Equal(VerificationOutcome.ReviewNotRun, outcome.Outcome);
    }
}
