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
            var (resultSpec, resultDef, _, _) = await _orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, _, _) = await _orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, _, _) = await _orchestrator.RunPipelineAsync(
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
            _userInteraction.RequestHumanReviewAsync("dbo.USP_Test", Arg.Any<string>())
                .Returns(
                    _ => Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "수정 의견" }),
                    _ => Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var (resultSpec, resultDef, _, _) = await _orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: false);

            // Assert
            Assert.NotNull(resultSpec);
            await _userInteraction.Received(2).RequestHumanReviewAsync("dbo.USP_Test", Arg.Any<string>());
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
            var (resultSpec, resultDef, resultRev, _) = await orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, _, _) = await _orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_L3Test", Arg.Any<string>())
                .Returns(Task.FromResult(feedbackDecision), Task.FromResult(approveDecision));

            // For the feedback iteration, AI should return a slightly different markdown
            var fixedSpecMarkdown = "## 개요\nFixed Content\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Is<string>(s => s != null && s.Contains("Make it better")), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = fixedSpecMarkdown }));

            // Act
            var (resultSpec, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_L3Test", 3, "OpenAI", "instructions", isBatchMode: false);

            // Assert
            Assert.NotNull(resultSpec);
            Assert.Equal(fixedSpecMarkdown, resultSpec);
            await userInteraction.Received(2).RequestHumanReviewAsync("dbo.USP_L3Test", Arg.Any<string>());
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
            var (resultSpec, _, _, _) = await orchestrator.RunPipelineAsync(
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
                var (resultSpec, resultDef, review, _) = await orchestrator.RunPipelineAsync(
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
            var (resultSpec, _, _, _) = await orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_Interactive", Arg.Any<string>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));
            userInteraction.ConfirmMetadataSyncAsync("dbo.USP_Interactive")
                .Returns(Task.FromResult(false)); // skip DB sync for unit test
                
            // Create dummy SQL file so ConfirmMetadataSyncAsync is triggered
            var cleansingDir = Path.Combine("./output", "cleansing");
            Directory.CreateDirectory(cleansingDir);
            File.WriteAllText(Path.Combine(cleansingDir, "dbo.USP_Interactive_MetadataCleansing.sql"), "DUMMY");

            // Act
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveL1Ollama", Arg.Any<string>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Fix something" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveL1OpenAI", Arg.Any<string>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Change it" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveSqlException", Arg.Any<string>())
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
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveFeedback", Arg.Any<string>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "다이어그램 추가해줘" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            userInteraction.ConfirmMetadataSyncAsync("dbo.USP_InteractiveFeedback")
                .Returns(Task.FromResult(false)); // no sync

            // Act
            var (resultSpec, resultDef, _, _) = await orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, finalRev, _) = await orchestrator.RunPipelineAsync(
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
            var (resultSpec, resultDef, finalRev, _) = await orchestrator.RunPipelineAsync(
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
        
        userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(
                Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "로직 흐름 시각화에 내용 추가해주세요" }),
                Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
            );

        // Act
        var (resultSpec, resultDef, finalRev, _) = await orchestrator.RunPipelineAsync(
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

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>())
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

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>())
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

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>())
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
            var (resultSpec, resultDef, _, _) = await _orchestrator.RunPipelineAsync(
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

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Cancel }));

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJobCancel", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            // Assert
            Assert.Null(result.Plan);
            await userInteraction.Received(1).RequestHumanReviewAsync("TestJobCancel", Arg.Any<string>());
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

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Add C to D" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJobFeedback", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            // Assert
            Assert.NotNull(result.Plan);
            Assert.Equal(regeneratedMarkdown, result.Plan);
            await userInteraction.Received(2).RequestHumanReviewAsync("TestJobFeedback", Arg.Any<string>());
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

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>())
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

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>())
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
                var (plan, _) = await orchestrator.RunConsolidatedPipelineAsync(
                    new List<(string FileName, string Content)> { ("dbo.USP_Test", "## 개요") },
                    "C#", jobName, "OpenAI", outputRoot, isBatchMode: true);

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
