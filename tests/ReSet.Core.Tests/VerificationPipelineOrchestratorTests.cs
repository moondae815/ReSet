using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class VerificationPipelineOrchestratorTests : IDisposable
    {
        private readonly IDbMetadataService _dbService;
        private readonly IAiService _aiService;
        private readonly MechanicalValidator _validator;
        private readonly IVerificationUserInteraction _userInteraction;
        private readonly VerificationPipelineOrchestrator _orchestrator;

        // RunConsolidatedPipelineAsync 호출부가 산출물을 기록할 임시 출력 루트.
        // 테스트마다 고유 경로를 쓰고 Dispose에서 정리한다.
        private readonly string _consolidatedOutputRoot =
            Path.Combine(Path.GetTempPath(), $"ReSet-ConsolidatedTest-{Guid.NewGuid():N}");

        public VerificationPipelineOrchestratorTests()
        {
            _dbService = Substitute.For<IDbMetadataService>();
            _aiService = Substitute.For<IAiService>();
            _validator = new MechanicalValidator();
            _userInteraction = Substitute.For<IVerificationUserInteraction>();
            _orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "1", "gpt-4");

            // Existing SP-focused tests retain their metadata fixtures while the
            // production path now enters through the common code-object API.
            _dbService.GetCodeObjectDetailsAsync(
                    Arg.Any<string>(),
                    Arg.Any<CodeObjectKey>(),
                    Arg.Any<int>(),
                    Arg.Any<System.Threading.CancellationToken>())
                .Returns(callInfo =>
                {
                    var key = callInfo.ArgAt<CodeObjectKey>(1);
                    return _dbService.GetSpDetailsAsync(
                        callInfo.ArgAt<string>(0),
                        key.Schema,
                        key.Name,
                        callInfo.ArgAt<int>(2));
                });
        }

        public void Dispose()
        {
            if (Directory.Exists(_consolidatedOutputRoot)) Directory.Delete(_consolidatedOutputRoot, true);
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_UsesFunctionMetadata()
        {
            // This fails if functions are fetched through the legacy SP-only metadata entry point.
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Calc", CodeObjectType.Function);
            var functionDef = new SpDefinition
            {
                ObjectKey = key,
                Schema = "dbo",
                Name = "FN_Calc",
                // Simulates a legacy metadata adapter that omits the object type.
                ObjectType = CodeObjectType.Procedure,
                DdlText = "CREATE FUNCTION dbo.FN_Calc() RETURNS int AS BEGIN RETURN 1 END"
            };
            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";

            _dbService.GetCodeObjectDetailsAsync(Arg.Any<string>(), key, Arg.Any<int>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(functionDef));
            _aiService.GenerateSpecificationAsync(
                    Arg.Is<SpDefinition>(x => x.ObjectType == CodeObjectType.Function),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));
            _aiService.ReviewSpecificationAsync(functionDef, specMarkdown)
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = false,
                    ScoreAccuracy = 10,
                    ScoreCrud = 10,
                    ScoreInterface = 10,
                    ScoreException = 10,
                    ScoreReadability = 10
                }));

            var result = await _orchestrator.RunCodeObjectPipelineAsync(
                "conn", key, 2, "OpenAI", "rules", true, "/tmp/out");

            Assert.NotNull(result.SpecMarkdown);
            Assert.Equal(functionDef, result.SpDef);
            Assert.Equal(CodeObjectType.Function, result.SpDef!.ObjectType);
            await _aiService.Received().GenerateSpecificationAsync(
                Arg.Is<SpDefinition>(x => x.ObjectType == CodeObjectType.Function),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<System.Threading.CancellationToken>());
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_DynamicFunctionConsolidation_ExcludesProcedureInstructions()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var actorService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var consolidatorService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService,
                actorService,
                new MechanicalValidator(),
                userInteraction,
                "1",
                "gpt-4",
                null,
                criticService,
                consolidatorService,
                "dynamic",
                "high",
                "medium",
                8);
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "FN_Calc", CodeObjectType.Function);
            var functionDef = new SpDefinition
            {
                ObjectKey = key,
                Schema = "dbo",
                Name = "FN_Calc",
                ObjectType = CodeObjectType.Function,
                DdlText = "CREATE FUNCTION dbo.FN_Calc() RETURNS int AS BEGIN RETURN 1 END"
            };
            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            var candidateReview = new ReviewResult { HasDefects = true, FeedbackComment = "Needs formula detail", ScoreAccuracy = 7, ScoreCrud = 7, ScoreInterface = 7, ScoreException = 7, ScoreReadability = 7 };
            var finalReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };

            dbService.GetCodeObjectDetailsAsync(Arg.Any<string>(), key, Arg.Any<int>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(functionDef));
            actorService.GenerateSpecificationAsync(functionDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));
            criticService.ReviewSpecificationAsync(functionDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(
                    Task.FromResult(candidateReview),
                    Task.FromResult(candidateReview),
                    Task.FromResult(candidateReview),
                    Task.FromResult(finalReview));
            consolidatorService.GenerateSpecificationAsync(functionDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "conn", key, 2, "OpenAI", "rules", true, "/tmp/out");

            Assert.NotNull(result.SpecMarkdown);
            await consolidatorService.Received().GenerateSpecificationAsync(
                functionDef,
                Arg.Is<string>(instructions =>
                    !instructions.Contains("Stored Procedure", StringComparison.OrdinalIgnoreCase) &&
                    !instructions.Contains("transaction", StringComparison.OrdinalIgnoreCase) &&
                    !instructions.Contains("isolation", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<System.Threading.CancellationToken>());
        }

        [Fact]
        public async Task RunPipelineAsync_SuccessOnFirstTry_ReturnsSpecification()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            // 올바른 마크다운 명세서 형식 (MechanicalValidator 검증 필수 헤더 포함)
            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```\n\n[AI 추론 보완: dbo.Users.Status - 상태를 나타냄]";
            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewSpecificationAsync(spDef, specMarkdown)
                .Returns(Task.FromResult(reviewResult));

            // Act
            var (resultSpec, resultDef, _, _, _) = await _orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            Assert.Equal(specMarkdown, resultSpec);
            Assert.Equal(spDef, resultDef);
            _userInteraction.Received(1).NotifyValidationSuccess("dbo.USP_Test");
        }

        [Fact]
        public async Task RunPipelineAsync_WithWarnings_CallsNotifyWarnings()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            spDef.Warnings.Add("테이블 dbo.User의 컬럼/설정 정보 수집 실패: 권한 없음");
            
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewSpecificationAsync(spDef, specMarkdown)
                .Returns(Task.FromResult(reviewResult));

            // Act
            var (resultSpec, resultDef, _, _, _) = await _orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            _userInteraction.Received(1).NotifyWarnings("dbo.USP_Test", spDef.Warnings);
            _userInteraction.Received(1).NotifyValidationSuccess("dbo.USP_Test");
        }

        [Fact]
        public async Task RunPipelineAsync_L1ValidationError_AttemptsSelfCorrection()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            // 1차 생성: 잘못된 형식 (헤더 누락) -> L1 실패 유발
            var badSpec = "잘못된 문서";
            // 2차 생성: 올바른 형식 -> L1 성공
            var goodSpec = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = badSpec }),   // 1차 호출
                    _ => Task.FromResult(new AiResult { Content = goodSpec })  // 2차 호출
                );

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewSpecificationAsync(spDef, goodSpec)
                .Returns(Task.FromResult(reviewResult));

            // Act
            var (resultSpec, resultDef, _, _, _) = await _orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            Assert.Equal(goodSpec, resultSpec);
            _userInteraction.Received(1).NotifyL1Errors("dbo.USP_Test", 1, Arg.Any<int>(), Arg.Any<List<string>>());
            _userInteraction.Received(1).NotifyValidationSuccess("dbo.USP_Test");
        }

        [Fact]
        public async Task RunPipelineAsync_L3HumanFeedbackLoop_ApproveWorkflow()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewSpecificationAsync(spDef, specMarkdown)
                .Returns(Task.FromResult(reviewResult));

            // L3 상호작용: 1차 피드백 -> 2차 승인
            _userInteraction.RequestHumanReviewAsync("dbo.USP_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    _ => Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "수정 의견" }),
                    _ => Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var (resultSpec, resultDef, _, _, _) = await _orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: false);

            // Assert
            Assert.NotNull(resultSpec);
            await _userInteraction.Received(2).RequestHumanReviewAsync("dbo.USP_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>());
        }

        [Fact]
        public async Task RunPipelineAsync_DbServiceThrowsException_ReturnsNulls()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            dbService.GetSpDetailsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromException<SpDefinition>(new Exception("DB Connection Failed")));

            // Act
            var (resultSpec, resultDef, resultRev, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Exception", 3, "OpenAI", "instructions", isBatchMode: false);

            // Assert
            Assert.Null(resultSpec);
            Assert.Null(resultDef);
            Assert.Null(resultRev);
            userInteraction.Received(1).NotifyError(Arg.Is<string>(s => s.Contains("DB 조회 실패")));
        }

        [Fact]
        public async Task RunPipelineAsync_WithOllamaProvider_UsesSequentialPipeline()
        {
            // Arrange
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_OllamaTest", DdlText = "CREATE PROCEDURE USP_OllamaTest AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_OllamaTest", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            // Setup AI mock for 3 parts
            _aiService.DeconstructSpLogicAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>(), Arg.Any<Action<(int, int, string)>?>())
                .Returns(Task.FromResult(new AiResult { Content = "{\"Logic\":{}}" }));

            _aiService.GenerateSpecSectionAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```" }));

            // Final consolidation
            _aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            // Act
            var (resultSpec, resultDef, _, _, _) = await _orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_OllamaTest", 3, "Ollama", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            _userInteraction.Received(1).NotifyValidationSuccess("dbo.USP_OllamaTest");
        }

        [Fact]
        public async Task RunPipelineAsync_WithDynamicEffort_UsesParallelGenerationAndCritic()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "dynamic", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_DynamicTest", DdlText = "CREATE PROCEDURE USP_DynamicTest AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_DynamicTest", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            // Dynamic effort calls GenerateSpecificationAsync 3 times (low, medium, high)
            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            // Critic Review (called for each candidate)
            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Consolidator uses GenerateSpecificationAsync which is already mocked above.
                
            // Final review
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>())
                .Returns(Task.FromResult(reviewResult));

            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_DynamicTest", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            userInteraction.Received(1).NotifyValidationSuccess("dbo.USP_DynamicTest");
        }
        [Fact]
        public async Task RunPipelineAsync_InteractiveMode_L3ReviewApprove_ReturnsSpec()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_L3Test", DdlText = "CREATE PROCEDURE USP_L3Test AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_L3Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Setup L3 Human Review: First Provide Feedback, then Approve
            var feedbackDecision = new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Make it better" };
            var approveDecision = new HumanReviewResult { Decision = UserDecision.Approve };
            userInteraction.RequestHumanReviewAsync("dbo.USP_L3Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(feedbackDecision), Task.FromResult(approveDecision));

            // For the feedback iteration, AI should return a slightly different markdown
            var fixedSpecMarkdown = "## 개요\nFixed Content\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Is<string>(s => s != null && s.Contains("Make it better")), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = fixedSpecMarkdown }));

            // Act
            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_L3Test", 3, "OpenAI", "instructions", isBatchMode: false);

            // Assert
            Assert.NotNull(resultSpec);
            Assert.Equal(fixedSpecMarkdown, resultSpec);
            await userInteraction.Received(2).RequestHumanReviewAsync("dbo.USP_L3Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>());
        }

        [Fact]
        public async Task RunPipelineAsync_CacheManagerThrowsException_ContinuesPipeline()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var cacheManager = Substitute.For<ICacheManager>();

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", cacheManager, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "PaymentDB",
                    "dbo",
                    "USP_CacheThrow",
                    CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "USP_CacheThrow",
                DdlText = "CREATE PROCEDURE USP_CacheThrow AS SELECT 1"
            };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_CacheThrow", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            cacheManager.ComputeCompositeHash(Arg.Any<SpDefinition>(), Arg.Any<int>())
                .Returns(x => { throw new Exception("Cache failure"); });

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            // Act
            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_CacheThrow", 3, "OpenAI", "instructions", isBatchMode: true, enableCache: true);

            // Assert
            Assert.NotNull(resultSpec); // It should continue and generate spec despite cache exception
            Assert.Equal(specMarkdown, resultSpec);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RunPipelineAsync_InvalidCacheOutputDirectory_ContinuesPipeline(
            bool enableCache)
        {
            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "PaymentDB",
                    "dbo",
                    "USP_NoCacheOutput",
                    CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "USP_NoCacheOutput",
                DdlText = "CREATE PROCEDURE dbo.USP_NoCacheOutput AS SELECT 1;"
            };
            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            _dbService.GetSpDetailsAsync(
                    Arg.Any<string>(),
                    spDef.Schema,
                    spDef.Name,
                    Arg.Any<int>())
                .Returns(spDef);
            _aiService.GenerateSpecificationAsync(
                    spDef,
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = specMarkdown });
            _aiService.ReviewSpecificationAsync(
                    spDef,
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(new ReviewResult
                {
                    HasDefects = false,
                    ScoreAccuracy = 10,
                    ScoreCrud = 10,
                    ScoreInterface = 10,
                    ScoreException = 10,
                    ScoreReadability = 10
                });

            var result = await _orchestrator.RunPipelineAsync(
                "connection_string",
                spDef.Schema,
                spDef.Name,
                3,
                "OpenAI",
                "instructions",
                isBatchMode: true,
                outputDirectory: " ",
                enableCache: enableCache);

            Assert.Equal(specMarkdown, result.SpecMarkdown);
        }

        [Fact]
        public async Task RunPipelineAsync_CacheHit_ReturnsCachedSpec()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var cacheManager = Substitute.For<ICacheManager>();
            
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", cacheManager, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "PaymentDB",
                    "dbo",
                    "USP_CacheTest",
                    CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "USP_CacheTest"
            };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_CacheTest", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            cacheManager.ComputeCompositeHash(spDef, 3).Returns("fake-hash");
            cacheManager.IsCacheValid(
                    Arg.Is<CodeObjectKey>(key =>
                        key.Schema == "dbo" &&
                        key.Name == "USP_CacheTest" &&
                        key.Type == CodeObjectType.Procedure),
                    "fake-hash",
                    Arg.Any<OutputPathResolver>())
                .Returns(true);

            var outputDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ReSet_CacheTest_" + Guid.NewGuid().ToString());
            var docsDir = System.IO.Path.Combine(outputDir, "Procedures", "dbo.USP_CacheTest", "docs");
            System.IO.Directory.CreateDirectory(docsDir);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(docsDir, "Spec.md"), "## Cached Spec");

            try
            {
                // Act
                var (resultSpec, resultDef, review, _, _) = await orchestrator.RunPipelineAsync(
                    "connection_string", "dbo", "USP_CacheTest", 3, "OpenAI", "instructions", true, outputDir, true);

                // Assert
                Assert.Equal("## Cached Spec", resultSpec);
                await aiService.DidNotReceiveWithAnyArgs().GenerateSpecificationAsync(default!, default!, default!, default!, default!);
            }
            finally
            {
                if (System.IO.Directory.Exists(outputDir)) System.IO.Directory.Delete(outputDir, true);
            }
        }

        [Fact]
        public async Task RunPipelineAsync_CacheHit_ReturnsUndecoratedBodyAndPreservesReviewScores()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService,
                aiService,
                new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(),
                "1",
                "gpt-4",
                cacheManager,
                aiService,
                aiService);
            var key = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "USP_CachedDecorated",
                CodeObjectType.Procedure);
            var definition = new SpDefinition
            {
                ObjectKey = key,
                Schema = key.Schema,
                Name = key.Name
            };
            dbService.GetSpDetailsAsync(
                    Arg.Any<string>(),
                    key.Schema,
                    key.Name,
                    Arg.Any<int>())
                .Returns(definition);
            cacheManager.ComputeCompositeHash(definition, 3).Returns("hash");
            cacheManager.IsCacheValid(
                    key,
                    "hash",
                    Arg.Any<OutputPathResolver>())
                .Returns(true);
            var outputRoot = Path.Combine(
                Path.GetTempPath(),
                $"ReSet-CachedDecorated-{Guid.NewGuid():N}");
            var specPath = new OutputPathResolver(key.Database, outputRoot)
                .ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            await File.WriteAllTextAsync(
                specPath,
                """
                ---
                종합 신뢰도: 78
                정합성 점수: 7/10
                CRUD 점수: 8/10
                인터페이스 점수: 9/10
                가독성 점수: 8/10
                예외처리 점수: 7/10
                ---

                > [!NOTE]
                > **문서 작성일시**: 2026-07-31
                > **분석 AI 정보**: OpenAI

                ## 개요
                cached body

                ## 참조 코드 객체

                - 직접 참조하는 코드 객체가 없습니다.
                """);

            try
            {
                var result = await orchestrator.RunPipelineAsync(
                    "connection_string",
                    key.Schema,
                    key.Name,
                    3,
                    "OpenAI",
                    "rules",
                    isBatchMode: true,
                    outputRoot,
                    enableCache: true);

                Assert.StartsWith("## 개요", result.SpecMarkdown);
                Assert.DoesNotContain("종합 신뢰도", result.SpecMarkdown);
                Assert.DoesNotContain("[!NOTE]", result.SpecMarkdown);
                Assert.Equal(7, result.Review?.ScoreAccuracy);
                Assert.Equal(8, result.Review?.ScoreCrud);
                Assert.Equal(9, result.Review?.ScoreInterface);
                Assert.Contains("## 참조 코드 객체", result.SpecMarkdown);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_CacheHit_ReportsCacheReuseAndTheOriginalAnalysisTimestamp()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService,
                aiService,
                new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(),
                "1",
                "gpt-4",
                cacheManager,
                aiService,
                aiService);
            var key = CodeObjectKey.Create(
                "PaymentDB", "dbo", "USP_CacheStamp", CodeObjectType.Procedure);
            var definition = new SpDefinition
            {
                ObjectKey = key,
                Schema = key.Schema,
                Name = key.Name
            };
            dbService.GetCodeObjectDetailsAsync(
                    Arg.Any<string>(), key, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(definition);
            cacheManager.ComputeCompositeHash(definition, 3).Returns("hash");
            cacheManager.IsCacheValid(key, "hash", Arg.Any<OutputPathResolver>()).Returns(true);

            var outputRoot = Path.Combine(
                Path.GetTempPath(), $"ReSet-CacheStamp-{Guid.NewGuid():N}");
            var specPath = new OutputPathResolver(key.Database, outputRoot).ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            await File.WriteAllTextAsync(
                specPath,
                """
                ---
                검증 상태: 통과
                종합 신뢰도: 78
                ---

                > [!NOTE]
                > **문서 작성일시**: 2026-08-01 14:22:03
                > **분석 AI 정보**: OpenAI

                ## 개요
                cached body
                """);

            try
            {
                var result = await orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB",
                    key,
                    3,
                    "OpenAI",
                    "rules",
                    isBatchMode: true,
                    outputRoot,
                    enableCache: true);

                Assert.True(result.FromCache);
                Assert.Equal(new DateTime(2026, 8, 1, 14, 22, 3), result.AnalyzedAt);
                Assert.StartsWith("## 개요", result.SpecMarkdown);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_CacheHitWithUnparsableTimestamp_LeavesAnalyzedAtNull()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService,
                aiService,
                new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(),
                "1",
                "gpt-4",
                cacheManager,
                aiService,
                aiService);
            var key = CodeObjectKey.Create(
                "PaymentDB", "dbo", "USP_CacheNoStamp", CodeObjectType.Procedure);
            var definition = new SpDefinition
            {
                ObjectKey = key,
                Schema = key.Schema,
                Name = key.Name
            };
            dbService.GetCodeObjectDetailsAsync(
                    Arg.Any<string>(), key, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(definition);
            cacheManager.ComputeCompositeHash(definition, 3).Returns("hash");
            cacheManager.IsCacheValid(key, "hash", Arg.Any<OutputPathResolver>()).Returns(true);

            var outputRoot = Path.Combine(
                Path.GetTempPath(), $"ReSet-CacheNoStamp-{Guid.NewGuid():N}");
            var specPath = new OutputPathResolver(key.Database, outputRoot).ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            await File.WriteAllTextAsync(
                specPath,
                """
                ---
                검증 상태: 통과
                ---

                > [!NOTE]
                > **분석 AI 정보**: OpenAI

                ## 개요
                cached body
                """);

            try
            {
                var result = await orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB",
                    key,
                    3,
                    "OpenAI",
                    "rules",
                    isBatchMode: true,
                    outputRoot,
                    enableCache: true);

                Assert.True(result.FromCache);
                Assert.Null(result.AnalyzedAt);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_CacheMiss_ReportsNoCacheReuseAndNoAnalysisTimestamp()
        {
            // FromCache는 이제 "Spec.md를 쓸 것인가"를 결정하는 게이트다(Program.cs의 !FromCache 분기).
            // 캐시 미스인데 참이 되면 방금 AI가 만든 명세서가 디스크에 한 번도 쓰이지 않은 채
            // "성공적으로 파일이 생성되었습니다!"가 뜬다. 위 캐시 히트 테스트의 짝이다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService,
                aiService,
                new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(),
                "1",
                "gpt-4",
                cacheManager,
                aiService,
                aiService);
            var key = CodeObjectKey.Create(
                "PaymentDB", "dbo", "USP_CacheMiss", CodeObjectType.Procedure);
            var definition = new SpDefinition
            {
                ObjectKey = key,
                Schema = key.Schema,
                Name = key.Name,
                DdlText = "CREATE PROCEDURE USP_CacheMiss AS SELECT 1"
            };
            dbService.GetCodeObjectDetailsAsync(
                    Arg.Any<string>(), key, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(definition);
            cacheManager.ComputeCompositeHash(definition, 3).Returns("hash");

            // 해시는 계산되지만 캐시는 무효다. 실행 경로는 캐시 판정을 거친 뒤 실제 분석으로 간다.
            cacheManager.IsCacheValid(key, "hash", Arg.Any<OutputPathResolver>()).Returns(false);

            var freshSpec = ValidSpecificationMarkdown();
            aiService.GenerateSpecificationAsync(
                    definition,
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = freshSpec }));
            aiService.ReviewSpecificationAsync(
                    definition, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = false,
                    ScoreAccuracy = 9,
                    ScoreCrud = 9,
                    ScoreInterface = 9,
                    ScoreException = 9,
                    ScoreReadability = 9
                }));

            var outputRoot = Path.Combine(
                Path.GetTempPath(), $"ReSet-CacheMiss-{Guid.NewGuid():N}");

            // 이전 실행이 남긴 명세서가 디스크에 그대로 있다. 캐시가 무효인데도 이 파일의
            // 타임스탬프를 읽어오면 하지도 않은 분석 날짜를 사용자에게 보고하게 된다.
            var specPath = new OutputPathResolver(key.Database, outputRoot).ResolveSpecPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            await File.WriteAllTextAsync(
                specPath,
                """
                ---
                검증 상태: 통과
                종합 신뢰도: 78
                ---

                > [!NOTE]
                > **문서 작성일시**: 2026-08-01 14:22:03
                > **분석 AI 정보**: OpenAI

                ## 개요
                stale body
                """);

            try
            {
                var result = await orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB",
                    key,
                    3,
                    "OpenAI",
                    "rules",
                    isBatchMode: true,
                    outputRoot,
                    enableCache: true);

                Assert.False(result.FromCache);
                Assert.Null(result.AnalyzedAt);
                Assert.Equal(freshSpec, result.SpecMarkdown);
                await aiService.Received(1).GenerateSpecificationAsync(
                    definition,
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>());
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task RunPipelineAsync_CacheUsesMetadataObjectKeyWhenInitialCatalogIsMissing()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var objectKey = CodeObjectKey.Create(
                "PaymentDB",
                "dbo",
                "USP_CacheDatabase",
                CodeObjectType.Procedure);
            var spDef = new SpDefinition
            {
                ObjectKey = objectKey,
                Schema = objectKey.Schema,
                Name = objectKey.Name
            };
            dbService.GetSpDetailsAsync(
                    Arg.Any<string>(),
                    objectKey.Schema,
                    objectKey.Name,
                    3)
                .Returns(spDef);
            cacheManager.ComputeCompositeHash(spDef, 3).Returns("fake-hash");
            cacheManager.IsCacheValid(
                    Arg.Any<CodeObjectKey>(),
                    "fake-hash",
                    Arg.Any<OutputPathResolver>())
                .Returns(true);

            var outputDirectory = Path.Combine(
                Path.GetTempPath(),
                "ReSet_CacheDatabase_" + Guid.NewGuid().ToString("N"));
            var paths = new OutputPathResolver(objectKey.Database, outputDirectory);
            var specPath = paths.ResolveSpecPath(objectKey);
            Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
            await File.WriteAllTextAsync(specPath, "## Cached Spec");

            try
            {
                var orchestrator = new VerificationPipelineOrchestrator(
                    dbService,
                    aiService,
                    new MechanicalValidator(),
                    Substitute.For<IVerificationUserInteraction>(),
                    cacheManager: cacheManager);

                var result = await orchestrator.RunPipelineAsync(
                    "Server=.;Integrated Security=true",
                    objectKey.Schema,
                    objectKey.Name,
                    3,
                    "OpenAI",
                    "instructions",
                    isBatchMode: true,
                    outputDirectory,
                    enableCache: true);

                Assert.Equal("## Cached Spec", result.SpecMarkdown);
                cacheManager.Received(1).IsCacheValid(
                    objectKey,
                    "fake-hash",
                    Arg.Is<OutputPathResolver>(resolver =>
                        resolver.ResolveSpecPath(objectKey) == specPath));
            }
            finally
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_AnalysisDatabaseDiffersFromObjectDatabase_ResolvesCacheUnderExternalDirectory()
        {
            // analysisDatabase가 실제로 소비되지 않으면(예: 인자 누락) 캐시 경로가 External/ 아래가 아니라
            // 루트(Procedures/) 아래로 계산되어 캐시 히트가 발생하지 않는다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var externalKey = CodeObjectKey.Create(
                "AuditDB",
                "dbo",
                "USP_ExternalCache",
                CodeObjectType.Procedure);
            var spDef = new SpDefinition
            {
                ObjectKey = externalKey,
                Schema = externalKey.Schema,
                Name = externalKey.Name,
                ObjectType = CodeObjectType.Procedure
            };
            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(),
                    externalKey,
                    Arg.Any<CancellationToken>(),
                    Arg.Any<bool>())
                .Returns(Task.FromResult(spDef));
            cacheManager.ComputeCompositeHash(spDef, 2).Returns("fake-hash");
            cacheManager.IsCacheValid(
                    Arg.Any<CodeObjectKey>(),
                    "fake-hash",
                    Arg.Any<OutputPathResolver>())
                .Returns(true);

            var outputDirectory = Path.Combine(
                Path.GetTempPath(),
                "ReSet_ExternalAnalysisDatabase_" + Guid.NewGuid().ToString("N"));
            var expectedSpecPath = Path.Combine(
                outputDirectory,
                "External",
                "AuditDB",
                "Procedures",
                "dbo.USP_ExternalCache",
                "docs",
                "Spec.md");
            Directory.CreateDirectory(Path.GetDirectoryName(expectedSpecPath)!);
            await File.WriteAllTextAsync(expectedSpecPath, "## 외부 DB 캐시 명세");

            try
            {
                var orchestrator = new VerificationPipelineOrchestrator(
                    dbService,
                    aiService,
                    new MechanicalValidator(),
                    Substitute.For<IVerificationUserInteraction>(),
                    cacheManager: cacheManager);

                var result = await orchestrator.RunCodeObjectPipelineAsync(
                    "Server=.;Database=PaymentDB;Integrated Security=true",
                    externalKey,
                    2,
                    "OpenAI",
                    "instructions",
                    isBatchMode: true,
                    outputDirectory,
                    enableCache: true,
                    cancellationToken: CancellationToken.None,
                    directDependenciesOnly: true,
                    includeExternalCodeObjects: true,
                    analysisDatabase: "PaymentDB");

                Assert.Equal("## 외부 DB 캐시 명세", result.SpecMarkdown);
                cacheManager.Received(1).IsCacheValid(
                    externalKey,
                    "fake-hash",
                    Arg.Is<OutputPathResolver>(resolver =>
                        resolver.ResolveSpecPath(externalKey) == expectedSpecPath));
                await aiService.DidNotReceive().GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>());
            }
            finally
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task RunPipelineAsync_L2Fails_TriggersRetry()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_L2FailTest", DdlText = "CREATE PROCEDURE USP_L2FailTest AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_L2FailTest", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            // Return defective review first, successful review second
            var defectiveReview = new ReviewResult { HasDefects = true, FeedbackComment = "Need more details", ScoreAccuracy = 5 };
            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(defectiveReview), Task.FromResult(goodReview));

            // Act
            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_L2FailTest", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            userInteraction.Received(1).NotifyValidationSuccess("dbo.USP_L2FailTest");
            await aiService.Received(2).GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>());
            await aiService.Received(2).ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>());
        }

        [Fact]
        public async Task RunPipelineAsync_L1Fails_TriggersRetry()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_L1FailTest", DdlText = "CREATE PROCEDURE USP_L1FailTest AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_L1FailTest", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            // Return bad markdown first (missing 로직 흐름 요약 header), good markdown second
            var badMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```"; 
            var goodMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = badMarkdown }), Task.FromResult(new AiResult { Content = goodMarkdown }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_L1FailTest", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            userInteraction.Received(1).NotifyValidationSuccess("dbo.USP_L1FailTest");
            // Check that GenerateSpecificationAsync was called twice
            await aiService.Received(2).GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>());
        }

        [Fact]
        public async Task RunPipelineAsync_ExhaustsRetries_ReturnsFailedSpec()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            // MaxL2Attempts = 2
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_FailTest", DdlText = "CREATE PROCEDURE USP_FailTest AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_FailTest", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            // Critic Review always returns HasDefects = true
            var reviewResult = new ReviewResult { HasDefects = true, ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5, ScoreException = 5, ScoreReadability = 5, FeedbackComment = "Bad spec" };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_FailTest", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            // In batch mode with exhausted retries, it might adopt it with caution or just fail.
            // But we just want coverage of the exhaustion path.
            userInteraction.DidNotReceive().NotifyValidationSuccess("dbo.USP_FailTest");
        }

        [Fact]
        public async Task RunPipelineAsync_WithDynamicEffort_FastPass_TriggersWhenScoreIsHigh()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "dynamic", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_DynamicFastPass", DdlText = "CREATE PROCEDURE USP_DynamicFastPass AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_DynamicFastPass", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            // One candidate gets a 95 score to trigger fast pass
            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Consolidate uses GenerateSpecificationAsync which is mocked above.

            // Final review should NOT be called again inside the consolidator block, but fast pass adopts the review.
            
            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_DynamicFastPass", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            Assert.Equal(specMarkdown, resultSpec);
            userInteraction.Received(1).NotifyValidationSuccess("dbo.USP_DynamicFastPass");
        }

        [Fact]
        public async Task RunPipelineAsync_WithDynamicEffort_FailsFinalReview_AttemptsFinalFix()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "dynamic", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_DynamicFinalFix", DdlText = "CREATE PROCEDURE USP_DynamicFinalFix AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_DynamicFinalFix", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            // Critic Review (fails on final review)
            var reviewResultFail = new ReviewResult { HasDefects = true, ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5, ScoreException = 5, ScoreReadability = 5, FeedbackComment = "Need fix" };
            var reviewResultPass = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            
            // Candidate scores below 90 to avoid fast-pass
            var reviewResultLow = new ReviewResult { HasDefects = true, ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 8, ScoreException = 8, ScoreReadability = 8, FeedbackComment = "Minor issue" };

            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(
                    Task.FromResult(reviewResultLow), // Candidate 1
                    Task.FromResult(reviewResultLow), // Candidate 2
                    Task.FromResult(reviewResultLow), // Candidate 3
                    Task.FromResult(reviewResultFail), // Final review
                    Task.FromResult(reviewResultPass)  // Re-final review after fix
                );

            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_DynamicFinalFix", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            userInteraction.Received().NotifyStatus(Arg.Is<string>(s => s.Contains("최종 합성본에서 일부 결함 감지")));
            userInteraction.Received().NotifyStatus(Arg.Is<string>(s => s.Contains("보완된 최종 합성본 L2 재검토 중")));
        }

        [Fact]
        public async Task RunPipelineAsync_InteractiveMode_Approves_And_Syncs_Db()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Interactive", DdlText = "CREATE PROCEDURE USP_Interactive AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Interactive", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Setup L3 interaction
            userInteraction.RequestHumanReviewAsync("dbo.USP_Interactive", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));
            userInteraction.ConfirmMetadataSyncAsync("dbo.USP_Interactive")
                .Returns(Task.FromResult(false)); // skip DB sync for unit test
                
            // Create dummy SQL file so ConfirmMetadataSyncAsync is triggered
            var cleansingDir = Path.Combine("./output", "cleansing");
            Directory.CreateDirectory(cleansingDir);
            File.WriteAllText(Path.Combine(cleansingDir, "dbo.USP_Interactive_MetadataCleansing.sql"), "DUMMY");

            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Interactive", 3, "OpenAI", "instructions", isBatchMode: false);

            // Assert
            Assert.NotNull(resultSpec);
            await userInteraction.Received(1).ConfirmMetadataSyncAsync("dbo.USP_Interactive");
        }

        [Fact]
        public async Task RunPipelineAsync_InteractiveMode_L3ProvideFeedback_RegenerationFailsL1_Retries_OllamaMode()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_InteractiveL1Ollama", DdlText = "CREATE PROCEDURE USP_InteractiveL1Ollama AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_InteractiveL1Ollama", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));


            
            // Step 1: Initial generation (Success)
            aiService.DeconstructSpLogicAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>(), Arg.Any<Action<(int current, int total, string message)>>())
                .Returns(Task.FromResult(new AiResult { Content = "{}" }));
            aiService.GenerateSpecSectionAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = "## 개요\n## 파라미터 목록", ThinkingText = "Think" }), // Initial part 1
                    Task.FromResult(new AiResult { Content = "## CRUD 분석", ThinkingText = "Think" }), // Initial part 2
                    Task.FromResult(new AiResult { Content = "## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```", ThinkingText = "Think" }), // Initial part 3
                    // L3 Feedback regeneration part 1,2,3 - part 3 will fail L1
                    Task.FromResult(new AiResult { Content = "## 개요\n## 파라미터 목록", ThinkingText = "Think" }),
                    Task.FromResult(new AiResult { Content = "## CRUD 분석", ThinkingText = "Think" }),
                    Task.FromResult(new AiResult { Content = "## 로직 흐름 요약\nJust Missing Visualization Header\n```mermaid\ngraph TD\nA-->B\n```", ThinkingText = "Think" }), // Missing header!
                    // L1 retry generation part 1,2,3
                    Task.FromResult(new AiResult { Content = "## 개요\n## 파라미터 목록", ThinkingText = "Think" }),
                    Task.FromResult(new AiResult { Content = "## CRUD 분석", ThinkingText = "Think" }),
                    Task.FromResult(new AiResult { Content = "## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```", ThinkingText = "Think" })
                );

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // L3 Interaction: Feedback -> L1 Fail -> Retry -> Success
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveL1Ollama", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Fix something" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_InteractiveL1Ollama", 3, "Ollama", "instructions", isBatchMode: false);

            // Assert
            Assert.NotNull(resultSpec);
            userInteraction.Received().NotifyStatus(Arg.Is<string>(s => s.Contains("피드백 적용본에서 정적 에러가 검출되어 AI 자가 수정 1회 더 진행합니다")));
        }

        [Fact]
        public async Task RunPipelineAsync_InteractiveMode_L3ProvideFeedback_RegenerationFailsL1_Retries_OpenAIMode()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_InteractiveL1OpenAI", DdlText = "CREATE PROCEDURE USP_InteractiveL1OpenAI AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_InteractiveL1OpenAI", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var validMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            var invalidMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\nJust Missing Visualization Header\n```mermaid\ngraph TD\nA-->B\n```";
            
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = validMarkdown, ThinkingText = "Think" }), // initial
                    Task.FromResult(new AiResult { Content = invalidMarkdown, ThinkingText = "Think" }), // L3 feedback regen (fails L1)
                    Task.FromResult(new AiResult { Content = validMarkdown, ThinkingText = "Think" })    // L1 retry
                );

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // L3 Interaction
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveL1OpenAI", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Change it" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_InteractiveL1OpenAI", 3, "OpenAI", "instructions", isBatchMode: false);

            // Assert
            Assert.NotNull(resultSpec);
            userInteraction.Received().NotifyStatus(Arg.Is<string>(s => s.Contains("피드백 적용본에서 정적 에러가 검출되어 AI 자가 수정 1회 더 진행합니다")));
        }

        [Fact]
        public async Task RunPipelineAsync_InteractiveMode_SyncsDb_CatchesSqlException()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_InteractiveSqlException", DdlText = "CREATE PROCEDURE USP_InteractiveSqlException AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_InteractiveSqlException", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Setup L3 interaction
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveSqlException", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));
            userInteraction.ConfirmMetadataSyncAsync("dbo.USP_InteractiveSqlException")
                .Returns(Task.FromResult(true)); // Allow DB sync!

            // Create fake cleansing SQL to trigger ApplyMetadataCleansingSqlAsync
            var currentDir = System.IO.Directory.GetCurrentDirectory();
            var testDir = System.IO.Path.Combine(currentDir, "cleansing");
            if (!System.IO.Directory.Exists(testDir)) System.IO.Directory.CreateDirectory(testDir);
            var fakeSqlFile = System.IO.Path.Combine(testDir, "dbo.USP_InteractiveSqlException_MetadataCleansing.sql");
            await System.IO.File.WriteAllTextAsync(fakeSqlFile, "SELECT 1;\nGO\n");

            // Act
            // The connection_string "invalid_connection" will cause a SqlException when it tries to run the cleansing sql
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "invalid_connection", "dbo", "USP_InteractiveSqlException", 3, "OpenAI", "instructions", isBatchMode: false, currentDir);

            // Assert
            Assert.NotNull(resultSpec);
            userInteraction.Received(1).NotifyError(Arg.Is<string>(s => s.Contains("DB 메타데이터 설명 역반영 중 오류 발생")));

            // Cleanup
            if (System.IO.File.Exists(fakeSqlFile)) System.IO.File.Delete(fakeSqlFile);
        }

        [Fact]
        public async Task RunPipelineAsync_InteractiveMode_ProvidesFeedback_And_Regenerates()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_InteractiveFeedback", DdlText = "CREATE PROCEDURE USP_InteractiveFeedback AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_InteractiveFeedback", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            var specMarkdown2 = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\nC-->D\n```";

            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }), Task.FromResult(new AiResult { Content = specMarkdown2 }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Setup L3 interaction: 1st Feedback, 2nd Approve
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveFeedback", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "다이어그램 추가해줘" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            userInteraction.ConfirmMetadataSyncAsync("dbo.USP_InteractiveFeedback")
                .Returns(Task.FromResult(false)); // no sync

            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_InteractiveFeedback", 3, "OpenAI", "instructions", isBatchMode: false);

            // Assert
            Assert.NotNull(resultSpec);
            Assert.Contains("C-->D", resultSpec); // should contain regenerated content
        }

        [Fact]
        public async Task RunPipelineAsync_SingleGeneration_WithL2Defects_Regenerates()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_SingleGen", DdlText = "CREATE PROCEDURE USP_SingleGen AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_SingleGen", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var spec1 = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            var spec2 = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\nC-->D\n```";

            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = spec1 }), Task.FromResult(new AiResult { Content = spec2 }));

            // 1st review fails, 2nd review passes
            var review1 = new ReviewResult { HasDefects = true, ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5, ScoreException = 5, ScoreReadability = 5, FeedbackComment = "Bad" };
            var review2 = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(review1), Task.FromResult(review2));

            // Act
            var (resultSpec, resultDef, finalRev, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_SingleGen", 3, "OpenAI", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            Assert.Equal(spec2, resultSpec); // should return the second generated spec
            userInteraction.Received(1).NotifyL2Defects("dbo.USP_SingleGen", 1, 2, "Bad");
            userInteraction.Received(1).NotifyValidationSuccess("dbo.USP_SingleGen");
        }

        [Fact]
        public async Task RunPipelineAsync_OllamaMode_WithL2Defects_PerformsPartialRegeneration()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "llama3", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_OllamaRetry", DdlText = "CREATE PROCEDURE USP_OllamaRetry AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_OllamaRetry", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            aiService.DeconstructSpLogicAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>(), Arg.Any<Action<(int, int, string)>?>())
                .Returns(Task.FromResult(new AiResult { Content = "{\"Logic\":{}}" }));

            // 1st attempt generation (3 sections)
            aiService.GenerateSpecSectionAsync(spDef, "OverviewAndParameters", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 개요\n## 파라미터 목록\n" }));
            aiService.GenerateSpecSectionAsync(spDef, "CrudAnalysis", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## CRUD 분석\n" }));
            aiService.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```" }));

            // 1st review fails (mentions CRUD)
            var review1 = new ReviewResult { HasDefects = true, ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5, ScoreException = 5, ScoreReadability = 5, FeedbackComment = "CRUD 분석 쪽에 테이블 누락됨" };
            
            // 2nd review passes
            var review2 = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(review1), Task.FromResult(review2));

            // Final review skipped in Ollama path? No, ReviewSpecificationAsync is called twice in retry loop.
            
            // Act
            var (resultSpec, resultDef, finalRev, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_OllamaRetry", 3, "Ollama", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);
            userInteraction.Received(1).NotifyL2Defects("dbo.USP_OllamaRetry", 1, 2, "CRUD 분석 쪽에 테이블 누락됨");
            
        // Since feedback was "CRUD 분석 쪽에 테이블 누락됨", it should have regenerated CrudAnalysis.
        // We can assert it called GenerateSpecSectionAsync more than 3 times (3 for first attempt, and at least 1 for retry).
        await aiService.Received().GenerateSpecSectionAsync(spDef, "CrudAnalysis", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>());
    }

    [Fact]
    public async Task RunPipelineAsync_OllamaMode_WithL3Feedback_PerformsPartialRegeneration()
    {
        // Arrange
        var dbService = Substitute.For<IDbMetadataService>();
        var aiService = Substitute.For<IAiService>();
        var validator = new MechanicalValidator();
        var userInteraction = Substitute.For<IVerificationUserInteraction>();
        var orchestrator = new VerificationPipelineOrchestrator(
            dbService, aiService, validator, userInteraction, "1", "llama3", null, aiService, aiService, "high", "high", "default", 8);

        var spDef = new SpDefinition { Schema = "dbo", Name = "USP_OllamaL3Retry", DdlText = "CREATE PROCEDURE USP_OllamaL3Retry AS SELECT 1" };
        dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_OllamaL3Retry", Arg.Any<int>())
            .Returns(Task.FromResult(spDef));

        aiService.DeconstructSpLogicAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>(), Arg.Any<Action<(int, int, string)>?>())
            .Returns(Task.FromResult(new AiResult { Content = "{\"Logic\":{}}" }));

        aiService.GenerateSpecSectionAsync(spDef, "OverviewAndParameters", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(new AiResult { Content = "## 개요\n## 파라미터 목록\n" }));
        aiService.GenerateSpecSectionAsync(spDef, "CrudAnalysis", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(new AiResult { Content = "## CRUD 분석\n" }));
        aiService.GenerateSpecSectionAsync(spDef, "LogicAndVisualization", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(new AiResult { Content = "## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```" }));

        var reviewPass = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
        aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(reviewPass));

        // Simulate interactive mode: First attempt returns false (requesting changes), second attempt returns true.
        // And the feedback targets the '로직 흐름 요약' (LogicAndVisualization) part.
        userInteraction.ConfirmMetadataSyncAsync(Arg.Any<string>())
            .Returns(Task.FromResult(true));
        
        userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
            .Returns(
                Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "로직 흐름 시각화에 내용 추가해주세요" }),
                Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
            );

        // Act
        var (resultSpec, resultDef, finalRev, _, _) = await orchestrator.RunPipelineAsync(
            "connection_string", "dbo", "USP_OllamaL3Retry", 3, "Ollama", "instructions", isBatchMode: false);

        // Assert
        Assert.NotNull(resultSpec);
        
        // It should have called GenerateSpecSectionAsync for LogicAndVisualization again due to feedback keywords
        await aiService.Received().GenerateSpecSectionAsync(spDef, "LogicAndVisualization", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>());
    }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_SuccessOnFirstTry_ReturnsPlan()
        {
            // Arrange
            var specs = new List<(string, string)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1"),
                ("dbo.USP_Test2", "## 개요\n내용2")
            };
            var consolidatedPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = consolidatedPlan }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), consolidatedPlan, "Job_Test")
                .Returns(Task.FromResult(reviewResult));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            // Act
            var result = await _orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            // Assert
            Assert.NotNull(result.Plan);
            Assert.Equal(consolidatedPlan, result.Plan);
            _userInteraction.Received(1).NotifyValidationSuccess("Job_Test");
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L1ValidationError_AttemptsSelfCorrection()
        {
            // Arrange
            var specs = new List<(string, string)> { ("dbo.USP_Test1", "내용") };
            var badPlan = "잘못된 문서";
            var goodPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = badPlan }),
                    _ => Task.FromResult(new AiResult { Content = goodPlan })
                );

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), goodPlan, "Job_Test")
                .Returns(Task.FromResult(reviewResult));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            // Act
            var result = await _orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            // Assert
            Assert.NotNull(result.Plan);
            Assert.Equal(goodPlan, result.Plan);
            _userInteraction.Received(1).NotifyL1Errors("Job_Test", 1, Arg.Any<int>(), Arg.Any<List<string>>());
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L2ValidationError_AttemptsSelfCorrection()
        {
            // Arrange
            var specs = new List<(string, string)> { ("dbo.USP_Test1", "내용") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), plan, "Job_Test")
                .Returns(
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "L2 결함" }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 })
                );

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            // Act
            var result = await _orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            // Assert
            Assert.NotNull(result.Plan);
            _userInteraction.Received(1).NotifyL2Defects("Job_Test", 1, Arg.Any<int>(), "L2 결함");
        }

        [Fact]
        public async Task RunPipelineAsync_ExportsMetadataCleansingSql_CreatesSqlFile()
        {
            // Arrange
            var tempOutDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            // AI가 유추 주석 패턴을 포함한 명세서 반환
            var specMarkdown = "## 개요\n이것은 테스트입니다. [AI 추론 보완: dbo.Orders.TotAmt - 순 결제액]\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewSpecificationAsync(spDef, specMarkdown)
                .Returns(Task.FromResult(reviewResult));

            // Act
            var (resultSpec, resultDef, _, _, _) = await _orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true, outputDirectory: tempOutDir);

            // Assert
            Assert.NotNull(resultSpec);
            
            var expectedSqlPath = System.IO.Path.Combine(tempOutDir, "cleansing", "dbo.USP_Test_MetadataCleansing.sql");
            Assert.True(System.IO.File.Exists(expectedSqlPath), $"SQL 스크립트 파일이 존재해야 합니다: {expectedSqlPath}");

            var sqlContent = await System.IO.File.ReadAllTextAsync(expectedSqlPath);
            Assert.Contains("sp_addextendedproperty", sqlContent);
            Assert.Contains("sp_updateextendedproperty", sqlContent);
            Assert.Contains("dbo.Orders.TotAmt", sqlContent);
            Assert.Contains("순 결제액", sqlContent);

            // Clean up
            try { System.IO.Directory.Delete(tempOutDir, true); } catch {}
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_Success_ReturnsConsolidatedPlan()
        {
            // Arrange
            var specs = new List<(string, string)> { ("dbo.Test", "## 명세") };
            
            var aiResult = new AiResult { Content = "## 통합 계획서\n## 통합 배치 아키텍처 개요\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n## Mermaid 기반 통합 흐름도\n```mermaid\ngraph TD\nA-->B\n```" };
            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(aiResult));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Act
            var result = await _orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // Assert
            Assert.NotNull(result.Plan);
            Assert.Contains("## 통합 계획서", result.Plan);
            _userInteraction.Received(1).NotifyValidationSuccess("TestJob");
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L1Fails_TriggersRetry()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };

            // Return bad markdown first (missing Mermaid 기반 통합 흐름도 header), good markdown second
            var badMarkdown = "## 통합 배치 아키텍처 개요\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```"; 
            var goodMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = badMarkdown }), Task.FromResult(new AiResult { Content = goodMarkdown }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(reviewResult));

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // Assert
            Assert.NotNull(result.Plan);
            userInteraction.Received(1).NotifyValidationSuccess("TestJob");
            await aiService.Received(2).GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<System.Collections.Generic.List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L2Fails_TriggersRetry()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };

            var goodMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = goodMarkdown }));

            // Return defective review first, successful review second
            var defectiveReview = new ReviewResult { HasDefects = true, FeedbackComment = "Need more details", ScoreAccuracy = 5 };
            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(defectiveReview), Task.FromResult(goodReview));

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // Assert
            Assert.NotNull(result.Plan);
            userInteraction.Received(1).NotifyValidationSuccess("TestJob");
            await aiService.Received(2).GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<System.Collections.Generic.List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await aiService.Received(2).ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3Interactive_Cancel_ReturnsNull()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var goodMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = goodMarkdown }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Cancel }));

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJobCancel", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            // Assert
            Assert.Null(result.Plan);
            await userInteraction.Received(1).RequestHumanReviewAsync("TestJobCancel", Arg.Any<string>(), Arg.Any<VerificationOutcome>());
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3Interactive_ProvideFeedback_Regenerates()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var initialMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            var regeneratedMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\nC-->D\n```";
            
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = initialMarkdown }), Task.FromResult(new AiResult { Content = regeneratedMarkdown }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10, FeedbackComment = "Minor tip" };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Add C to D" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJobFeedback", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            // Assert
            Assert.NotNull(result.Plan);
            Assert.Equal(regeneratedMarkdown, result.Plan);
            await userInteraction.Received(2).RequestHumanReviewAsync("TestJobFeedback", Arg.Any<string>(), Arg.Any<VerificationOutcome>());
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3Interactive_ProvideFeedback_RegenerationThrows_ContinuesToAsk()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var initialMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            
            // First call succeeds, second call throws exception
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = initialMarkdown }),
                    Task.FromException<AiResult>(new Exception("Generation Error"))
                );

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Try this" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJobException", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            // Assert
            Assert.NotNull(result.Plan);
            Assert.Equal(initialMarkdown, result.Plan); // It reverted to initial markdown because regeneration failed
            userInteraction.Received(1).NotifyError(Arg.Is<string>(s => s.Contains("재생성 실패")));
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3Interactive_ProvideFeedback_RegenerationFailsL1_Retries()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var initialMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            
            // Re-generated markdown is missing required sections, so it fails L1 validation
            var invalidMarkdown = "Just some random text\n```mermaid\ngraph TD\n```";
            var fixedMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = initialMarkdown }), // initial
                    Task.FromResult(new AiResult { Content = invalidMarkdown }), // regeneration 1
                    Task.FromResult(new AiResult { Content = fixedMarkdown })    // regeneration 2 (after L1 failure)
                );

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Try this" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJobL1Fail", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            // Assert
            Assert.NotNull(result.Plan);
            Assert.Equal(fixedMarkdown, result.Plan); // Returns fixed markdown
            userInteraction.Received(1).NotifyStatus(Arg.Is<string>(s => s.Contains("정적 에러가 검출되어 AI 자가 수정 1회 더 진행합니다")));
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L2WarningComment_LogsComment()
        {
            // Arrange
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var goodMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = goodMarkdown }));

            var warningReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10, FeedbackComment = "This is a warning\nSecond line" };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(warningReview));

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJobWarning", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // Assert
            Assert.NotNull(result.Plan);
            userInteraction.Received(1).NotifyStatus(Arg.Is<string>(s => s.Contains("This is a warning")));
            userInteraction.Received(1).NotifyStatus(Arg.Is<string>(s => s.Contains("Second line")));
        }

        [Theory]
        [InlineData("unlimited", -1)]
        [InlineData("검증 완료까지", -1)]
        [InlineData("-1", -1)]
        [InlineData("3", 3)]
        [InlineData("invalid_string", 1)]
        public void Orchestrator_Constructor_ParsesMaxL2AttemptsCorrectly(string input, int expectedL2Attempts)
        {
            // Act
            var orchestrator = new VerificationPipelineOrchestrator(_dbService, _aiService, _validator, _userInteraction, maxL2Attempts: input);

            // Use reflection to inspect private field value
            var fieldInfo = typeof(VerificationPipelineOrchestrator).GetField("_maxL2Attempts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(fieldInfo);
            var actual = (int)fieldInfo!.GetValue(orchestrator)!;

            // Assert
            Assert.Equal(expectedL2Attempts, actual);
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_WritesIntermediateArtifactsUnderProvidedOutputRoot()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Consolidated-{Guid.NewGuid():N}");
            var jobName = $"Job_{Guid.NewGuid():N}";
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();

            aiService.BrainstormBatchPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "brainstorm body" }));
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "structure body" }));
            aiService.GenerateConsolidatedBatchPlanAsync(
                    Arg.Any<string>(), Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult
                {
                    Content = "## 통합 배치 아키텍처 개요\n\n## Mermaid 기반 통합 흐름도\n\n## 단계별 이행 상세 및 의사코드\n\n## 통합 데이터 정합성 검증 SQL 세트\n"
                }));
            aiService.ReviewConsolidatedPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false }));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-test");
            var strayDirectory = Path.Combine(Directory.GetCurrentDirectory(), "output", "Jobs", jobName);

            try
            {
                await orchestrator.RunConsolidatedPipelineAsync(
                    new List<(string FileName, string Content)> { ("dbo.USP_Test", "## 개요") },
                    "C#", jobName, "OpenAI", outputRoot, isBatchMode: true);

                Assert.True(File.Exists(Path.Combine(outputRoot, "Jobs", jobName, "raw", "Brainstorming.md")));
                Assert.True(File.Exists(Path.Combine(outputRoot, "Jobs", jobName, "raw", "PlanStructure.md")));

                // CWD 폴백이 살아 있으면 여기에도 생긴다. 생성 여부만 보면 버그를 놓친다.
                Assert.False(Directory.Exists(strayDirectory), $"CWD에 산출물이 생겼습니다: {strayDirectory}");
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
                if (Directory.Exists(strayDirectory)) Directory.Delete(strayDirectory, true);
            }
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_RejectsEmptyOutputRoot()
        {
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(),
                Substitute.For<IAiService>(),
                new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(),
                "1", "gpt-test");

            await Assert.ThrowsAsync<ArgumentException>(() =>
                orchestrator.RunConsolidatedPipelineAsync(
                    new List<(string FileName, string Content)> { ("dbo.USP_Test", "## 개요") },
                    "C#", "Job", "OpenAI", "   ", isBatchMode: true));
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_MarksPlanWhenL1RetriesAreExhausted()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Consolidated-{Guid.NewGuid():N}");
            var jobName = $"Job_{Guid.NewGuid():N}";
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();

            aiService.BrainstormBatchPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "brainstorm body" }));
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "structure body" }));
            // 필수 H2 헤더가 없어 L1이 항상 실패한다.
            aiService.GenerateConsolidatedBatchPlanAsync(
                    Arg.Any<string>(), Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 엉뚱한 헤더\n\n내용" }));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-test");

            try
            {
                var plan = (await orchestrator.RunConsolidatedPipelineAsync(
                    new List<(string FileName, string Content)> { ("dbo.USP_Test", "## 개요") },
                    "C#", jobName, "OpenAI", outputRoot, isBatchMode: true)).Plan;

                Assert.Contains("L1 기계 검증을 통과하지 못했습니다", plan);
                userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_MarksPlanWhenCriticReviewCouldNotRun()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), $"ReSet-Consolidated-{Guid.NewGuid():N}");
            var jobName = $"Job_{Guid.NewGuid():N}";
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();

            aiService.BrainstormBatchPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "brainstorm body" }));
            aiService.DraftBatchPlanStructureAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "structure body" }));
            aiService.GenerateConsolidatedBatchPlanAsync(
                    Arg.Any<string>(), Arg.Any<List<(string FileName, string Content)>>(),
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult
                {
                    Content = "## 통합 배치 아키텍처 개요\n\n## Mermaid 기반 통합 흐름도\n\n## 단계별 이행 상세 및 의사코드\n\n## 통합 데이터 정합성 검증 SQL 세트\n"
                }));
            aiService.ReviewConsolidatedPlanAsync(
                    Arg.Any<List<(string FileName, string Content)>>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new InvalidOperationException("critic endpoint down")));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-test");

            try
            {
                var plan = (await orchestrator.RunConsolidatedPipelineAsync(
                    new List<(string FileName, string Content)> { ("dbo.USP_Test", "## 개요") },
                    "C#", jobName, "OpenAI", outputRoot, isBatchMode: true)).Plan;

                Assert.Contains("[!NOTE]", plan);
                Assert.Contains("L2 AI 교차 리뷰가 수행되지 않았습니다", plan);
                userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_MarksSpecWhenCriticReviewCouldNotRun()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_NoReview", CodeObjectType.Procedure);

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_NoReview", DdlText = "SELECT 1;"
                }));
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = ValidSpecificationMarkdown() }));
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new InvalidOperationException("critic endpoint down")));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-test", criticService: criticService);

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "OpenAI", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-Outcome-{Guid.NewGuid():N}"), false,
                cancellationToken: CancellationToken.None, directDependenciesOnly: true);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            Assert.Contains("L2 AI 교차 리뷰가 수행되지 않았습니다", result.SpecMarkdown);
            userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_SectionalPath_MarksSpecWhenFinalReviewCouldNotRun()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_Sectional", CodeObjectType.Procedure);

            // 구역별(하이브리드 다중 후보군) 경로 진입 조건은 actorEffort == "dynamic" 이다.
            aiService.ProviderName.Returns("Ollama");
            criticService.ProviderName.Returns("Ollama");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_Sectional", DdlText = "SELECT 1;"
                }));

            // Low/Medium/High 후보 생성과 합성(Consolidator) 생성 모두 이 대역 하나로 충분하다.
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = ValidSpecificationMarkdown() }));

            // 후보 채점 3회는 성공시키되 Fast-pass 임계치(90점) 미만으로 유지해 합성 단계까지 도달시키고,
            // 네 번째 호출(최종 합성본 L2 검토)에서 예외를 던지게 한다.
            var candidateReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 7,
                ScoreCrud = 7,
                ScoreInterface = 7,
                ScoreException = 7,
                ScoreReadability = 7
            };
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(candidateReview),
                    Task.FromResult(candidateReview),
                    Task.FromResult(candidateReview),
                    Task.FromException<ReviewResult>(new InvalidOperationException("critic endpoint down")));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "ollama-test", criticService: criticService, actorEffort: "dynamic");

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "Ollama", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-Outcome-{Guid.NewGuid():N}"), false,
                cancellationToken: CancellationToken.None, directDependenciesOnly: true);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            Assert.Contains("L2 AI 교차 리뷰가 수행되지 않았습니다", result.SpecMarkdown);
            userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_SectionalPath_MarksSpecWhenFinalReviewIsQualityRejected()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_SectionalRejected", CodeObjectType.Procedure);

            // 구역별(하이브리드 다중 후보군) 경로 진입 조건은 actorEffort == "dynamic" 이다.
            aiService.ProviderName.Returns("Ollama");
            criticService.ProviderName.Returns("Ollama");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_SectionalRejected", DdlText = "SELECT 1;"
                }));

            // Low/Medium/High 후보 생성과 합성(Consolidator) 생성, 그리고 결함 보완 재합성 모두
            // 이 대역 하나로 충분하다(항상 유효한 마크다운을 반환).
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = ValidSpecificationMarkdown() }));

            // 후보 채점 3회는 성공시키되 Fast-pass 임계치(90점) 미만으로 유지해 합성 단계까지 도달시키고,
            // 네 번째 호출(최종 합성본 L2 검토)부터는 결함(HasDefects=true, 저점수)이 있는 리뷰를 반환한다.
            // NSubstitute는 지정된 값 목록을 넘어서는 호출에 대해 마지막 값을 계속 반환하므로,
            // 결함 보완 후 재검토(다섯 번째 호출)에서도 동일하게 결함 있는 리뷰가 반환된다.
            var candidateReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 7,
                ScoreCrud = 7,
                ScoreInterface = 7,
                ScoreException = 7,
                ScoreReadability = 7
            };
            var lowScoreReview = new ReviewResult
            {
                HasDefects = true,
                FeedbackComment = "정합성 및 CRUD 매핑이 기준에 미달합니다.",
                ScoreAccuracy = 3,
                ScoreCrud = 3,
                ScoreInterface = 3,
                ScoreException = 3,
                ScoreReadability = 3
            };
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(candidateReview),
                    Task.FromResult(candidateReview),
                    Task.FromResult(candidateReview),
                    Task.FromResult(lowScoreReview));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "ollama-test", criticService: criticService, actorEffort: "dynamic");

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "Ollama", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-Outcome-{Guid.NewGuid():N}"), false,
                cancellationToken: CancellationToken.None, directDependenciesOnly: true);

            Assert.Equal(VerificationOutcome.QualityRejected, result.Outcome);
            Assert.Contains("[품질 불합격]", result.SpecMarkdown);
            userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_MarksSpecWhenL1RetriesAreExhausted()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_BadL1", CodeObjectType.Procedure);

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_BadL1", DdlText = "SELECT 1;"
                }));
            // 필수 H2 헤더가 없어 L1이 항상 실패한다.
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "# 헤더가 없는 본문" }));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-test", criticService: criticService);

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "OpenAI", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-Outcome-{Guid.NewGuid():N}"), false,
                cancellationToken: CancellationToken.None, directDependenciesOnly: true);

            Assert.Equal(VerificationOutcome.L1Exhausted, result.Outcome);
            Assert.Contains("L1 기계 검증을 통과하지 못했습니다", result.SpecMarkdown);
            userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
        }

        // ===== 최종 리뷰(final code review)에서 지적된 결함에 대한 회귀 테스트 =====

        [Fact]
        public async Task RunPipelineAsync_InteractiveMode_CriticReviewThrows_ApprovalScreenReceivesNonPassedOutcome()
        {
            // C1: 승인 화면(RequestHumanReviewAsync)은 문서 헤더를 파싱해서가 아니라
            // 파이프라인이 실제로 도달한 종료 상태를 명시적으로 전달받아야 한다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_ReviewDown", DdlText = "CREATE PROCEDURE USP_ReviewDown AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_ReviewDown", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = ValidSpecificationMarkdown();
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new InvalidOperationException("critic endpoint down")));

            userInteraction.RequestHumanReviewAsync("dbo.USP_ReviewDown", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var result = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_ReviewDown", 3, "OpenAI", "instructions", isBatchMode: false);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            await userInteraction.Received(1).RequestHumanReviewAsync(
                "dbo.USP_ReviewDown",
                Arg.Any<string>(),
                VerificationOutcome.ReviewNotRun);
        }

        [Fact]
        public async Task RunPipelineAsync_CacheGuard_UnverifiedOutcomeIsNotCached_NextRunReanalyzesForReal()
        {
            // C2: 검증되지 않은(ReviewNotRun) 문서를 캐시에 쓰면 이후 실행이 캐시 히트로
            // 그 문서를 "통과"로 재포장해 재사용한다. 캐시 기록 자체를 막아야 한다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticServiceRun1 = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var cacheManager = Substitute.For<ICacheManager>();
            cacheManager.ComputeCompositeHash(Arg.Any<SpDefinition>(), Arg.Any<int>()).Returns("hash-1");

            // OutputPathResolver는 현재 DB명이 있어야 생성된다. ObjectKey를 명시하지 않으면
            // 캐시 경로 계산 자체가 조용히 건너뛰어져 이 테스트가 검증하려는 코드 경로에
            // 아예 도달하지 못한다.
            var objectKey = CodeObjectKey.Create("PaymentDB", "dbo", "USP_CacheGuard", CodeObjectType.Procedure);
            var spDef = new SpDefinition { ObjectKey = objectKey, Schema = "dbo", Name = "USP_CacheGuard", DdlText = "CREATE PROCEDURE USP_CacheGuard AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_CacheGuard", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = ValidSpecificationMarkdown();
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            // Run 1: critic가 예외를 던진다 -> ReviewNotRun.
            criticServiceRun1.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new InvalidOperationException("critic endpoint down")));

            var orchestrator1 = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", cacheManager, criticServiceRun1, aiService, "high", "high", "default", 8);

            var outputDirectory = Path.Combine(Path.GetTempPath(), "ReSet_CacheGuard_" + Guid.NewGuid().ToString("N"));
            try
            {
                var run1 = await orchestrator1.RunPipelineAsync(
                    "connection_string", "dbo", "USP_CacheGuard", 3, "OpenAI", "instructions",
                    isBatchMode: true, outputDirectory, enableCache: true);

                Assert.Equal(VerificationOutcome.ReviewNotRun, run1.Outcome);
                cacheManager.DidNotReceive().UpdateCache(
                    Arg.Any<CodeObjectKey>(), Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<OutputPathResolver>(), Arg.Any<string>());

                // Run 2: critic가 이번엔 정상적으로 성공한다. 캐시에 아무것도 쓰이지 않았으므로
                // (여전히 캐시 미스) 실제로 재분석해야 하며, 캐시 히트로 위장된 완벽한 점수를
                // 재사용해서는 안 된다.
                var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 };
                var criticServiceRun2 = Substitute.For<IAiService>();
                criticServiceRun2.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(goodReview));

                var orchestrator2 = new VerificationPipelineOrchestrator(
                    dbService, aiService, new MechanicalValidator(), userInteraction,
                    "1", "gpt-4", cacheManager, criticServiceRun2, aiService, "high", "high", "default", 8);

                var run2 = await orchestrator2.RunPipelineAsync(
                    "connection_string", "dbo", "USP_CacheGuard", 3, "OpenAI", "instructions",
                    isBatchMode: true, outputDirectory, enableCache: true);

                Assert.Equal(VerificationOutcome.Passed, run2.Outcome);
                Assert.Equal(9, run2.Review?.ScoreAccuracy);
                await criticServiceRun2.Received(1).ReviewSpecificationAsync(
                    spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
                cacheManager.Received(1).UpdateCache(
                    Arg.Any<CodeObjectKey>(), spDef, "hash-1", Arg.Any<OutputPathResolver>(), Arg.Any<string>());
            }
            finally
            {
                if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, true);
            }
        }

        [Fact]
        public async Task RunPipelineAsync_L2ReviewThrowsOperationCanceledException_PropagatesInsteadOfMarkingReviewNotRun()
        {
            // I3: L2 리뷰 도중 취소되면 "리뷰 미수행" 문서를 완성해 정상 반환할 게 아니라
            // 취소를 그대로 전파해서 배치 루프의 OperationCanceledException 처리가 잡도록 해야 한다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Cancelled", DdlText = "CREATE PROCEDURE USP_Cancelled AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Cancelled", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = ValidSpecificationMarkdown();
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new OperationCanceledException()));

            await Assert.ThrowsAsync<OperationCanceledException>(() => orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Cancelled", 3, "OpenAI", "instructions", isBatchMode: true));

            userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
        }

        [Fact]
        public async Task RunPipelineAsync_L3ProvideFeedback_RegeneratedSpecClearsStaleReviewAndMarksReviewNotRun()
        {
            // I4: 피드백 반영 재생성은 본문 전체를 새로 만들고 L1만 재검사할 뿐 L2는
            // 재수행하지 않는다. 이전 검토 결과와 통과 판정을 그대로 들고 가면 "한 번도
            // 리뷰받지 않은 새 문서가 이전 문서의 점수로 통과를 자칭"하게 된다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_FeedbackStale", DdlText = "CREATE PROCEDURE USP_FeedbackStale AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_FeedbackStale", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = ValidSpecificationMarkdown();
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            var feedbackDecision = new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "수정해주세요" };
            var approveDecision = new HumanReviewResult { Decision = UserDecision.Approve };
            userInteraction.RequestHumanReviewAsync("dbo.USP_FeedbackStale", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(feedbackDecision), Task.FromResult(approveDecision));

            var regeneratedSpec = "## 개요\nRegenerated\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Is<string?>(s => s != null && s.Contains("수정해주세요")), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = regeneratedSpec }));

            var result = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_FeedbackStale", 3, "OpenAI", "instructions", isBatchMode: false);

            Assert.Equal(regeneratedSpec, result.SpecMarkdown);
            Assert.Null(result.Review);
            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            await userInteraction.Received(1).RequestHumanReviewAsync(
                "dbo.USP_FeedbackStale", regeneratedSpec, VerificationOutcome.ReviewNotRun);
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_SectionalPath_SelfFixStillFailsL1_MarksL1ExhaustedAndSkipsSuccessNotification()
        {
            // I5: 구역별(하이브리드) 경로에서 합성본이 L1에 실패하고 자가 수정 1회 후에도
            // 여전히 실패하면, 표준 재시도 루프와 동일하게 L1Exhausted로 확정해야 한다.
            // 이 시점 이전에는 L1 무결성 확인 없이 그대로 다음 단계로 넘어가
            // 깨진 문서가 Passed로 끝나는 경우가 있었다.
            //
            // 이 테스트는 L2 결함 보완 블록(:682~)의 변경에 영향을 받지 않는다. 이유는
            // "fixL1Result.IsValid가 false로 남아서"가 아니라 - fixL1Result는 여기서 아예
            // 계산되지 않는다 - 아래 candidateReview가 HasDefects = false이므로 :682의
            // `if (finalL2Result != null && finalL2Result.HasDefects)` 가드에서 단락되어
            // 보완 블록 전체가 실행되지 않기 때문이다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_SectionalL1Exhausted", CodeObjectType.Procedure);

            // 구역별(하이브리드 다중 후보군) 경로 진입 조건은 actorEffort == "dynamic" 이다.
            aiService.ProviderName.Returns("Ollama");
            criticService.ProviderName.Returns("Ollama");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_SectionalL1Exhausted", DdlText = "SELECT 1;"
                }));

            // 필수 H2 헤더가 없는 본문을 항상 반환한다: 후보 3개, 합성본, 자가 수정본 모두
            // L1을 통과하지 못한다.
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "# 헤더가 없는 본문" }));

            var candidateReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 7,
                ScoreCrud = 7,
                ScoreInterface = 7,
                ScoreException = 7,
                ScoreReadability = 7
            };
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(candidateReview));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "ollama-test", criticService: criticService, actorEffort: "dynamic");

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "Ollama", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-Outcome-{Guid.NewGuid():N}"), false,
                cancellationToken: CancellationToken.None, directDependenciesOnly: true);

            Assert.Equal(VerificationOutcome.L1Exhausted, result.Outcome);
            Assert.Contains("L1 기계 검증을 통과하지 못했습니다", result.SpecMarkdown);
            userInteraction.DidNotReceive().NotifyValidationSuccess(Arg.Any<string>());
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_Sectional_L2FixThatPassesL1_DropsTheL1ExhaustedBanner()
        {
            // consolidatedL1Valid는 자가 수정 직후까지만 갱신되고, L2 결함 보완 재생성본이
            // L1을 통과해도 그대로 false로 남아 있었다. 그 결과 L1을 통과한 최종 문서에
            // "L1 미통과" 배너가 붙는다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_SectionalL2Recovers", CodeObjectType.Procedure);

            aiService.ProviderName.Returns("Ollama");
            criticService.ProviderName.Returns("Ollama");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_SectionalL2Recovers", DdlText = "SELECT 1;"
                }));

            // 필수 H2 헤더 5종과 mermaid 블록이 전부 있는 본문만 L1을 통과한다.
            var l1Invalid = "# 헤더가 없는 본문";
            var l1Valid =
                "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";

            // 이 경로에서 GenerateSpecificationAsync는 순서대로: 후보 3개, 합성본,
            // 자가 수정본, 그리고 L2 결함 보완본으로 호출된다. 마지막만 L1을 통과시킨다.
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 후보 1
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 후보 2
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 후보 3
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 합성본
                    Task.FromResult(new AiResult { Content = l1Invalid }),   // 자가 수정본 (여전히 실패)
                    Task.FromResult(new AiResult { Content = l1Valid }));    // L2 결함 보완본 (통과)

            var defectiveReview = new ReviewResult
            {
                HasDefects = true, FeedbackComment = "결함이 있습니다",
                ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5, ScoreException = 5, ScoreReadability = 5
            };
            var cleanReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
            };

            // 후보 채점 3회 → 최종 합성본 검토(결함) → 보완본 재검토(통과) 순으로 호출된다.
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(defectiveReview),   // 후보 1 채점
                    Task.FromResult(defectiveReview),   // 후보 2 채점
                    Task.FromResult(defectiveReview),   // 후보 3 채점
                    Task.FromResult(defectiveReview),   // 최종 합성본 L2 검토 (결함 → 보완 유발)
                    Task.FromResult(cleanReview));      // 보완본 L2 재검토 (통과)

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "ollama-test", criticService: criticService, actorEffort: "dynamic");

            var result = await orchestrator.RunCodeObjectPipelineAsync(
                "Server=(local);Database=PaymentDB", key, 1, "Ollama", "rules", true,
                Path.Combine(Path.GetTempPath(), $"ReSet-L2Recovers-{Guid.NewGuid():N}"), false,
                cancellationToken: CancellationToken.None, directDependenciesOnly: true);

            // 최종 문서는 L1을 통과했다. L1 미통과 배너가 붙어서는 안 된다.
            Assert.DoesNotContain("L1 기계 검증을 통과하지 못했습니다", result.SpecMarkdown);
            Assert.NotEqual(VerificationOutcome.L1Exhausted, result.Outcome);
            // 위 두 단언만으로는 :718의 `finalReview = reFinalReview;` 재리뷰 할당이 고정되지
            // 않는다. 그 줄을 지우면 보완본(=최종 문서)이 아니라 보완 전 문서를 평가한
            // defectiveReview가 그대로 최종 리뷰로 남아 Outcome이 조용히 QualityRejected로
            // 뒤집히는데, 배너 문구도 L1Exhausted도 아니므로 두 단언은 여전히 통과한다.
            // 이 줄이 "재리뷰한 문서의 판정만 최종 판정이 된다"는 불변식을 고정한다.
            Assert.Equal(VerificationOutcome.Passed, result.Outcome);
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3Interactive_ProvideFeedback_MarksRegeneratedPlanReviewNotRun()
        {
            // 재검토(scoped re-review) 잔여 항목: RunConsolidatedPipelineAsync의 피드백 경로도
            // I4와 동일한 결함을 갖고 있었다 - 계획서 전체를 재생성하고 L1만 재검사할 뿐
            // L2는 재수행하지 않는데, planOutcome(이번 웨이브에서 C1을 위해 추가된 변수)은
            // 그대로 남아 있었다. 재생성된, 한 번도 리뷰받지 않은 계획서가 이전 계획서의
            // 통과 판정을 그대로 물려받아 승인 화면에 표시되면 안 된다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var initialMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            var regeneratedMarkdown = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\nC-->D\n```";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = initialMarkdown }), Task.FromResult(new AiResult { Content = regeneratedMarkdown }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Add C to D" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "TestJobFeedbackOutcome", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            Assert.NotNull(result.Plan);
            Assert.Equal(regeneratedMarkdown, result.Plan);
            // 1차 호출: 표준 루프에서 L1+L2 모두 통과한 초안 -> Passed.
            await userInteraction.Received(1).RequestHumanReviewAsync(
                "TestJobFeedbackOutcome", initialMarkdown, VerificationOutcome.Passed);
            // 2차 호출: 피드백으로 전면 재생성된, 한 번도 리뷰받지 않은 계획서 -> ReviewNotRun.
            await userInteraction.Received(1).RequestHumanReviewAsync(
                "TestJobFeedbackOutcome", regeneratedMarkdown, VerificationOutcome.ReviewNotRun);
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_ReportsTheOutcomeAndReviewToTheCaller()
        {
            // planOutcome은 :1584부터 정확히 추적되지만 반환 튜플에 없어서 호출부가
            // 알 수 없었다(:1581-1583 주석). 그 때문에 BatchMigrationPlan.md에 검증
            // 상태가 전혀 기록되지 않았다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var validPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = validPlan }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "TestJobOutcomeReported", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Equal(VerificationOutcome.Passed, result.Outcome);
            Assert.Same(goodReview, result.Review);
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_QualityRejected_ReportsTheDefectiveReviewToTheCaller()
        {
            // 설계의 outcome/review 짝 표에서 QualityRejected 행만 테스트로 고정되어 있지
            // 않았다. :1709의 `planReview = l2Result;`를 지워도 오케스트레이터 테스트가 전부
            // 통과했다 - 즉 품질 불합격으로 확정된 계획서가 review == null인 채 반환되어도
            // 아무도 잡지 못했다. Outcome은 QualityRejected인데 근거가 되는 리뷰가 없으면
            // 호출부는 "왜 불합격인지" 기록할 수 없다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            // maxL2Attempts = "1" 이므로 첫 결함 리뷰에서 재시도 여지가 없어 곧바로 확정된다.
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            // 본문 자체는 L1을 통과해야 L2 단계까지 도달한다.
            var validPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = validPlan }));

            var defectiveReview = new ReviewResult
            {
                HasDefects = true,
                FeedbackComment = "정합성 검증 SQL이 비어 있습니다",
                ScoreAccuracy = 4, ScoreCrud = 4, ScoreInterface = 4, ScoreException = 4, ScoreReadability = 4
            };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(defectiveReview));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "TestJobQualityRejected", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Equal(VerificationOutcome.QualityRejected, result.Outcome);
            Assert.Same(defectiveReview, result.Review);
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_WhenTheReviewCallFails_ReportsReviewNotRunWithNoScores()
        {
            // 리뷰를 수행하지 못한 계획서에 이전 점수가 실리면 안 된다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var validPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = validPlan }));

            // await 시점에 예외가 던져져 :1677의 catch로 들어간다.
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<ReviewResult>(new InvalidOperationException("리뷰 서비스 장애")));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "TestJobReviewFailed", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            Assert.Null(result.Review);
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3Feedback_ClearsTheReviewAlongWithTheOutcome()
        {
            // 명세서 경로(:1451-1453)는 재생성 시 finalReview를 null로 비운다.
            // 계획서 경로도 같아야 한다 - 재생성된 계획서에 이전 계획서의 점수가
            // 남으면 "한 번도 리뷰받지 않은 문서가 이전 점수를 자칭"하게 된다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var initial = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            var regenerated = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\nC-->D\n```";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = initial }), Task.FromResult(new AiResult { Content = regenerated }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Add C to D" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "TestJobFeedbackClearsReview", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            Assert.Null(result.Review);
        }

        // 아래 세 테스트는 취소가 삼켜지지 않는지 확인한다. 세 지점 모두 cancellationToken을
        // 받는 AI 호출을 catch { }로 감싸고 있어, 사용자가 Ctrl-C를 눌러도 작업이 계속되고
        // 승인 화면까지 도달했다. 상위 호출부(Program.cs:968, :1262)는 이미
        // OperationCanceledException을 받아 메뉴로 돌아가므로 전파만 하면 된다.

        [Fact]
        public async Task RunCodeObjectPipelineAsync_Sectional_CancelDuringSelfFix_Propagates()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var criticService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_CancelSelfFix", CodeObjectType.Procedure);

            aiService.ProviderName.Returns("Ollama");
            criticService.ProviderName.Returns("Ollama");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_CancelSelfFix", DdlText = "SELECT 1;"
                }));

            // 필수 H2 헤더가 없어 L1을 통과하지 못한다 - 이것이 자가 수정 경로의 진입 조건이다.
            var l1Invalid = "# 헤더가 없는 본문";

            // 호출 순서: 후보 3개, 합성본, 자가 수정. 다섯 번째에서 취소한다.
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromException<AiResult>(new OperationCanceledException()));

            var candidateReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 7, ScoreCrud = 7, ScoreInterface = 7, ScoreException = 7, ScoreReadability = 7
            };
            criticService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(candidateReview));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "ollama-test", criticService: criticService, actorEffort: "dynamic");

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB", key, 1, "Ollama", "rules", true,
                    Path.Combine(Path.GetTempPath(), $"ReSet-CancelSelfFix-{Guid.NewGuid():N}"), false,
                    cancellationToken: CancellationToken.None, directDependenciesOnly: true));
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_CancelDuringL3FeedbackSelfFix_Propagates()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_CancelL3", CodeObjectType.Procedure);

            aiService.ProviderName.Returns("OpenAI");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_CancelL3", DdlText = "SELECT 1;"
                }));

            var l1Valid =
                "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            var l1Invalid = "# 헤더가 없는 본문";

            // 호출 순서: 1차 생성(L1 통과), L3 피드백 재생성(L1 실패), L3 자가 수정(취소).
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = l1Valid }),
                    Task.FromResult(new AiResult { Content = l1Invalid }),
                    Task.FromException<AiResult>(new OperationCanceledException()));

            var cleanReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
            };
            aiService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(cleanReview));

            // 두 번째 응답이 승인인 이유: 취소가 삼켜지던 시절의 증상이 바로 "같은 승인
            // 화면을 다시 받는다"이므로, 승인으로 루프를 끊어야 테스트가 끝난다. 수정 후에는
            // 두 번째 질문 자체가 오지 않는다.
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback, UserFeedback = "보완해 주세요"
                    }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-4");

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB", key, 1, "OpenAI", "rules", false,
                    Path.Combine(Path.GetTempPath(), $"ReSet-CancelL3-{Guid.NewGuid():N}"), false,
                    cancellationToken: CancellationToken.None, directDependenciesOnly: true));
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_CancelDuringL3FeedbackL1Refix_Propagates()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var validPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";
            var l1InvalidPlan = "# 헤더가 없는 계획서";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });

            // 호출 순서: 1차 생성(L1 통과), L3 피드백 재생성(L1 실패), L1 재보완(취소).
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = validPlan }),
                    Task.FromResult(new AiResult { Content = l1InvalidPlan }),
                    Task.FromException<AiResult>(new OperationCanceledException()));

            var goodReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
            };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            // 명세서 쪽과 같은 이유로 두 번째 응답은 승인이다. 취소를 삼키면 승인 화면이
            // 한 번 더 뜨고 - 이것이 이 결함의 증상이다 - 테스트는 예외 없이 끝난다.
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback, UserFeedback = "보완해 주세요"
                    }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunConsolidatedPipelineAsync(
                    specs, "C#", "TestJobCancelRefix", "OpenAI", _consolidatedOutputRoot, isBatchMode: false));
        }

        // 위 세 테스트가 자가 수정(두 번째) 재생성을 고정한다면, 아래 두 테스트는 그보다 한 번
        // 앞선 L3 첫 피드백 재생성을 고정한다. 이쪽 catch는 bare catch가 아니라 NotifyError를
        // 동반해 눈에 덜 띄었을 뿐, 취소를 삼키고 continue로 같은 승인 화면을 다시 띄우는
        // 동일한 결함이었다.

        [Fact]
        public async Task RunCodeObjectPipelineAsync_CancelDuringL3FirstFeedbackRegeneration_Propagates()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_CancelL3First", CodeObjectType.Procedure);

            aiService.ProviderName.Returns("OpenAI");

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_CancelL3First", DdlText = "SELECT 1;"
                }));

            var l1Valid =
                "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";

            // 호출 순서: 1차 생성(L1 통과), L3 첫 피드백 재생성(취소).
            aiService.GenerateSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = l1Valid }),
                    Task.FromException<AiResult>(new OperationCanceledException()));

            var cleanReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
            };
            aiService.ReviewSpecificationAsync(
                    Arg.Any<SpDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(cleanReview));

            // 두 번째 응답이 승인인 이유는 위 자가 수정 테스트와 같다. 취소를 삼키면 승인 화면이
            // 한 번 더 뜨고 테스트는 예외 없이 끝난다.
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback, UserFeedback = "보완해 주세요"
                    }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-4");

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB", key, 1, "OpenAI", "rules", false,
                    Path.Combine(Path.GetTempPath(), $"ReSet-CancelL3First-{Guid.NewGuid():N}"), false,
                    cancellationToken: CancellationToken.None, directDependenciesOnly: true));
        }

        [Fact]
        public async Task RunConsolidatedPipelineAsync_CancelDuringL3FirstFeedbackRegeneration_Propagates()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var validPlan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n```mermaid\ngraph TD\nA-->B\n```";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });

            // 호출 순서: 1차 생성(L1 통과), L3 첫 피드백 재생성(취소).
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = validPlan }),
                    Task.FromException<AiResult>(new OperationCanceledException()));

            var goodReview = new ReviewResult
            {
                HasDefects = false,
                ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
            };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(
                    Task.FromResult(new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback, UserFeedback = "보완해 주세요"
                    }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunConsolidatedPipelineAsync(
                    specs, "C#", "TestJobCancelFirstRegen", "OpenAI", _consolidatedOutputRoot, isBatchMode: false));
        }

        [Fact]
        public async Task RunCodeObjectPipelineAsync_CancelDuringCacheCheck_Propagates()
        {
            // 캐시 확인 중 취소가 삼켜지면 파이프라인이 전체 AI 분석으로 진행한다.
            // 사용자가 멈추라고 한 직후에 가장 긴 작업이 시작되는 셈이다.
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var cacheManager = Substitute.For<ICacheManager>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP_CancelCache", CodeObjectType.Procedure);

            dbService.GetCodeObjectDetailsDirectAsync(
                    Arg.Any<string>(), Arg.Any<CodeObjectKey>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new SpDefinition
                {
                    ObjectKey = key, Schema = "dbo", Name = "USP_CancelCache", DdlText = "SELECT 1;"
                }));

            cacheManager.ComputeCompositeHash(Arg.Any<SpDefinition>(), Arg.Any<int>()).Returns("hash");
            cacheManager
                .When(manager => manager.IsCacheValid(
                    Arg.Any<CodeObjectKey>(), Arg.Any<string>(), Arg.Any<OutputPathResolver>()))
                .Do(_ => throw new OperationCanceledException());

            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", cacheManager);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                orchestrator.RunCodeObjectPipelineAsync(
                    "Server=(local);Database=PaymentDB", key, 1, "OpenAI", "rules", true,
                    Path.Combine(Path.GetTempPath(), $"ReSet-CancelCache-{Guid.NewGuid():N}"), true,
                    cancellationToken: CancellationToken.None, directDependenciesOnly: true));
        }

        // 2026-08-04 사고 재현. 시도 1=70점, 시도 2=90점, 시도 3=78점이었고
        // 마지막인 78점이 채택됐다. 90점짜리를 채택해야 한다.
        [Fact]
        public async Task RunPipelineAsync_RetriesExhausted_AdoptsHighestScoringAttemptNotTheLast()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            const string body = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```\n\n";
            var spec1 = body + "시도1고유표시";
            var spec2 = body + "시도2고유표시";
            var spec3 = body + "시도3고유표시";

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = spec1 }),
                    _ => Task.FromResult(new AiResult { Content = spec2 }),
                    _ => Task.FromResult(new AiResult { Content = spec3 }));

            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도1고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 7, ScoreReadability = 5, ScoreException = 7 }));
            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도2고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 10, ScoreCrud = 9, ScoreInterface = 9, ScoreReadability = 10, ScoreException = 7 }));
            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도3고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 8, ScoreCrud = 9, ScoreInterface = 6, ScoreReadability = 7, ScoreException = 9 }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("시도2고유표시", resultSpec);
            Assert.DoesNotContain("시도3고유표시", resultSpec);
            Assert.Contains("90/100", resultSpec);
        }

        // 후보가 하나도 없으면(리뷰 자체가 전부 실패) 현행 경로를 유지한다.
        [Fact]
        public async Task RunPipelineAsync_AllReviewsFail_KeepsReviewNotRunPath()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var spec = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = spec }));
            _aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>())
                .Returns<Task<ReviewResult>>(_ => throw new InvalidOperationException("critic down"));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("L2 AI 교차 리뷰가 수행되지 않았습니다", resultSpec);
        }

        // 배치 계획 루프도 같은 결함을 갖는다. 한쪽만 고치면 증상이 이쪽에 남는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_RetriesExhausted_AdoptsHighestScoringAttempt()
        {
            var specs = new List<(string, string)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };

            const string body = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n\n";
            var plan1 = body + "계획1고유표시";
            var plan2 = body + "계획2고유표시";
            var plan3 = body + "계획3고유표시";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = plan1 }),
                    _ => Task.FromResult(new AiResult { Content = plan2 }),
                    _ => Task.FromResult(new AiResult { Content = plan3 }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획1고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 7, ScoreReadability = 5, ScoreException = 7 }));
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획2고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 10, ScoreCrud = 9, ScoreInterface = 9, ScoreReadability = 10, ScoreException = 7 }));
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획3고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 8, ScoreCrud = 9, ScoreInterface = 6, ScoreReadability = 7, ScoreException = 9 }));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            Assert.Contains("계획2고유표시", result.Plan);
            Assert.DoesNotContain("계획3고유표시", result.Plan);
        }

        // 3차 시도의 생성 호출이 죽으면 2차가 만든 검증된 문서까지 함께 사라졌다.
        // 변수에는 그 내용이 그대로 남아 있는데 genSuccess가 false라 버려졌다.
        [Fact]
        public async Task RunPipelineAsync_LastGenerationThrows_AdoptsTheBestScoredAttemptInsteadOfReturningNull()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            const string body = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```\n\n";
            var spec1 = body + "시도1고유표시";
            var spec2 = body + "시도2고유표시";

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = spec1 }),
                    _ => Task.FromResult(new AiResult { Content = spec2 }),
                    _ => throw new InvalidOperationException("generation timed out"));

            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도1고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 7, ScoreReadability = 5, ScoreException = 7 }));
            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도2고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("시도2고유표시", resultSpec);
            Assert.Contains("3차 시도가 AI 생성 호출 실패로 중단되어", resultSpec);
            Assert.Contains("88/100", resultSpec);
        }

        // 후보가 하나도 없으면 구제할 것이 없다. 현행대로 전체 실패다.
        [Fact]
        public async Task RunPipelineAsync_FirstGenerationThrows_StillReturnsNull()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task<AiResult>>(_ => throw new InvalidOperationException("generation timed out"));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.Null(resultSpec);
        }

        // 배치 계획 루프도 같은 결함을 갖는다. 한쪽만 고치면 증상이 이쪽에 남는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_LastGenerationThrows_AdoptsTheBestScoredAttempt()
        {
            var specs = new List<(string, string)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };

            const string body = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n\n";
            var plan1 = body + "계획1고유표시";
            var plan2 = body + "계획2고유표시";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = plan1 }),
                    _ => Task.FromResult(new AiResult { Content = plan2 }),
                    _ => throw new InvalidOperationException("generation timed out"));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획1고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 7, ScoreReadability = 5, ScoreException = 7 }));
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획2고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            Assert.Contains("계획2고유표시", result.Plan);
            Assert.Contains("3차 시도가 AI 생성 호출 실패로 중단되어", result.Plan);
        }

        // 3번째 시도의 프롬프트에 1·2차 지적이 모두 살아 있어야 한다.
        [Fact]
        public async Task RunPipelineAsync_CarriesEveryPriorRoundFeedbackIntoTheNextPrompt()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var spec = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            var capturedFeedback = new List<string?>();

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    capturedFeedback.Add(callInfo.ArgAt<string>(2));
                    return Task.FromResult(new AiResult { Content = spec });
                });

            var round = 0;
            _aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>())
                .Returns(_ =>
                {
                    round++;
                    return Task.FromResult(new ReviewResult
                    {
                        HasDefects = true,
                        FeedbackComment = $"{round}차 고유지적",
                        ScoreAccuracy = 7, ScoreCrud = 7, ScoreInterface = 7, ScoreReadability = 7, ScoreException = 7
                    });
                });

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.Equal(3, capturedFeedback.Count);
            var thirdPrompt = capturedFeedback[2];
            Assert.NotNull(thirdPrompt);
            Assert.Contains("1차 고유지적", thirdPrompt);
            Assert.Contains("2차 고유지적", thirdPrompt);
            Assert.Contains("정합성 7", thirdPrompt);
            Assert.DoesNotContain("잔재에 영향을 받지", thirdPrompt);
        }

        private static readonly string[] RequiredSpecHeaderNames =
        {
            "개요",
            "파라미터 목록",
            "CRUD 분석",
            "로직 흐름 요약",
            "비즈니스 흐름 시각화"
        };

        private static string ValidSpecificationMarkdown() =>
            string.Join("\n", RequiredSpecHeaders().Select(header => header + "\n\n내용"));

        private static IEnumerable<string> RequiredSpecHeaders() =>
            RequiredSpecHeaderNames.Select(h => "## " + h);
    }
}
