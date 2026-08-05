using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class RegenerationScopeSelectorTests
    {
        // 모든 항목이 기준(8)을 넘는 만점 리뷰. 각 테스트가 필요한 항목만 끌어내린다.
        private static ReviewResult Perfect() => new()
        {
            HasDefects = true,
            ScoreAccuracy = 10,
            ScoreCrud = 10,
            ScoreInterface = 10,
            ScoreReadability = 10,
            ScoreException = 10
        };

        // 정합성은 비즈니스 로직 자체가 틀렸다는 뜻이라 구조화 데이터를 다시 뽑아야 한다.
        [Fact]
        public void FromReview_AccuracyBelowThreshold_RerunsStage1AndLogic()
        {
            var review = Perfect();
            review.ScoreAccuracy = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.True(scope.RunStage1);
            Assert.True(scope.Logic);
            Assert.False(scope.Overview);
            Assert.False(scope.Crud);
        }

        [Fact]
        public void FromReview_CrudBelowThreshold_RerunsStage1AndCrud()
        {
            var review = Perfect();
            review.ScoreCrud = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.True(scope.RunStage1);
            Assert.True(scope.Crud);
            Assert.False(scope.Overview);
            Assert.False(scope.Logic);
        }

        // 인터페이스는 파라미터·반환 정의라 개요 섹션의 문제다. 구조는 멀쩡하다.
        [Fact]
        public void FromReview_InterfaceBelowThreshold_RegeneratesOverviewOnly()
        {
            var review = Perfect();
            review.ScoreInterface = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Overview);
            Assert.False(scope.Crud);
            Assert.False(scope.Logic);
        }

        [Fact]
        public void FromReview_ReadabilityBelowThreshold_RegeneratesLogicOnly()
        {
            var review = Perfect();
            review.ScoreReadability = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Logic);
            Assert.False(scope.Overview);
            Assert.False(scope.Crud);
        }

        [Fact]
        public void FromReview_ExceptionBelowThreshold_RegeneratesLogicOnly()
        {
            var review = Perfect();
            review.ScoreException = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Logic);
            Assert.False(scope.Overview);
            Assert.False(scope.Crud);
        }

        [Fact]
        public void FromReview_MultipleBelowThreshold_TakesTheUnion()
        {
            var review = Perfect();
            review.ScoreCrud = 5;
            review.ScoreInterface = 5;

            var scope = RegenerationScopeSelector.FromReview(review, 8);

            Assert.True(scope.RunStage1);
            Assert.True(scope.Overview);
            Assert.True(scope.Crud);
            Assert.False(scope.Logic);
        }

        // 점수는 다 통과했는데 Critic이 결함을 지적한 경로가 있다.
        // 어느 섹션인지 지역화할 근거가 없으므로 전부 다시 만든다.
        [Fact]
        public void FromReview_NothingBelowThreshold_FallsBackToEverything()
        {
            var scope = RegenerationScopeSelector.FromReview(Perfect(), 8);

            Assert.Equal(RegenerationScope.Everything, scope);
        }

        // L1은 형식 검증이라 구조화 데이터에 영향이 없다. Stage 1은 언제나 건너뛴다.
        [Fact]
        public void FromL1Errors_OnlyMermaid_RegeneratesLogicWithoutStage1()
        {
            var errors = new List<DetailedError>
            {
                new() { Type = ErrorType.MermaidQuoteMissing, Message = "따옴표 누락" },
                new() { Type = ErrorType.MermaidCliError, Message = "파스 실패" }
            };

            var scope = RegenerationScopeSelector.FromL1Errors(errors);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Logic);
            Assert.False(scope.Overview);
            Assert.False(scope.Crud);
        }

        // 어느 헤더가 빠졌는지 메시지를 파싱해 추측하지 않는다. 보수적으로 전부 다시 만든다.
        [Fact]
        public void FromL1Errors_HeaderMissing_RegeneratesEverySectionWithoutStage1()
        {
            var errors = new List<DetailedError>
            {
                new() { Type = ErrorType.MermaidQuoteMissing, Message = "따옴표 누락" },
                new() { Type = ErrorType.HeaderMissing, Message = "## CRUD 분석 없음" }
            };

            var scope = RegenerationScopeSelector.FromL1Errors(errors);

            Assert.False(scope.RunStage1);
            Assert.True(scope.Overview);
            Assert.True(scope.Crud);
            Assert.True(scope.Logic);
        }

        [Fact]
        public void FromL1Errors_Empty_FallsBackToEverything()
        {
            var scope = RegenerationScopeSelector.FromL1Errors(new List<DetailedError>());

            Assert.Equal(RegenerationScope.Everything, scope);
        }
    }
}
