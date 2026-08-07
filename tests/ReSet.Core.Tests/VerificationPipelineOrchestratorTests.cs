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
            _userInteraction.RequestHumanReviewAsync("dbo.USP_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
                .Returns(
                    _ => Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "수정 의견" }),
                    _ => Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var (resultSpec, resultDef, _, _, _) = await _orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: false);

            // Assert
            Assert.NotNull(resultSpec);
            await _userInteraction.Received(2).RequestHumanReviewAsync("dbo.USP_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>());
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
        public async Task RunPipelineAsync_LocalProvider_ReadabilityOnlyDefect_RegeneratesOnlyTheLogicSection()
        {
            // 가독성만 미달인 리뷰는 표현 계층 결함이지 구조 결함이 아니다. 옛 구현은
            // CriticFeedbackLog가 매 회차 앞에 붙이는 항목별 점수 줄("CRUD ...")의
            // "CRUD"라는 글자에 걸려, 지적 대상이 아닌 CRUD 섹션까지 재생성했다.
            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_ReadabilityOnly", DdlText = "CREATE PROCEDURE USP_ReadabilityOnly AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_ReadabilityOnly", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            _aiService.DeconstructSpLogicAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>(), Arg.Any<Action<(int, int, string)>?>())
                .Returns(Task.FromResult(new AiResult { Content = "{\"Logic\":{}}" }));

            _aiService.GenerateSpecSectionAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```" }));

            // 1차 리뷰: 가독성만 기준(8) 미달. 나머지 네 항목은 만점.
            var readabilityOnlyDefect = new ReviewResult
            {
                HasDefects = true,
                ScoreAccuracy = 10,
                ScoreCrud = 10,
                ScoreInterface = 10,
                ScoreException = 10,
                ScoreReadability = 5,
                FeedbackComment = "다이어그램 가독성을 높이십시오."
            };
            // 2차 리뷰: 전부 만점이라 루프가 2회 시도로 종료된다.
            var noDefects = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>())
                .Returns(Task.FromResult(readabilityOnlyDefect), Task.FromResult(noDefects));

            // Act
            var (resultSpec, resultDef, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_ReadabilityOnly", 3, "Ollama", "instructions", isBatchMode: true);

            // Assert
            Assert.NotNull(resultSpec);

            // 가독성만 미달이면 2차 시도는 part3만 다시 만들어야 한다.
            // 옛 구현은 CriticFeedbackLog의 점수 줄에 들어 있는 "CRUD"라는 글자에 걸려
            // CRUD 섹션을 무조건 재생성했다.
            await _aiService.Received(1).GenerateSpecSectionAsync(
                spDef, "CrudAnalysis", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _aiService.Received(1).GenerateSpecSectionAsync(
                spDef, "OverviewAndParameters", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _aiService.Received(2).GenerateSpecSectionAsync(
                spDef, "LogicAndVisualization", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

            // 표현 계층 결함(가독성)은 구조화 데이터 자체를 다시 뽑을 이유가 없다 —
            // Stage 1(추론)은 1차 시도에서 한 번만 돌아야 한다.
            await _aiService.Received(1).DeconstructSpLogicAsync(
                spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Action<(int, int, string)>?>());
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_L3Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
            await userInteraction.Received(2).RequestHumanReviewAsync("dbo.USP_L3Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>());
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_Interactive", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveL1Ollama", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveL1OpenAI", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveSqlException", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_InteractiveFeedback", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
        
        userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = consolidatedPlan }));

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), consolidatedPlan, "Job_Test")
                .Returns(Task.FromResult(reviewResult));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = badPlan }),
                    _ => Task.FromResult(new AiResult { Content = goodPlan })
                );

            var reviewResult = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), goodPlan, "Job_Test")
                .Returns(Task.FromResult(reviewResult));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), plan, "Job_Test")
                .Returns(
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "L2 결함" }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 })
                );

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = goodMarkdown }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Cancel }));

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJobCancel", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            // Assert
            Assert.Null(result.Plan);
            await userInteraction.Received(1).RequestHumanReviewAsync("TestJobCancel", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>());
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = initialMarkdown }), Task.FromResult(new AiResult { Content = regeneratedMarkdown }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10, FeedbackComment = "Minor tip" };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Add C to D" }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve })
                );

            // Act
            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "TestJobFeedback", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            // Assert
            Assert.NotNull(result.Plan);
            Assert.Equal(regeneratedMarkdown, result.Plan);
            await userInteraction.Received(2).RequestHumanReviewAsync("TestJobFeedback", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>());
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = initialMarkdown }),
                    Task.FromException<AiResult>(new Exception("Generation Error"))
                );

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(new AiResult { Content = initialMarkdown }), // initial
                    Task.FromResult(new AiResult { Content = invalidMarkdown }), // regeneration 1
                    Task.FromResult(new AiResult { Content = fixedMarkdown })    // regeneration 2 (after L1 failure)
                );

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
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
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

            userInteraction.RequestHumanReviewAsync("dbo.USP_ReviewDown", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var result = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_ReviewDown", 3, "OpenAI", "instructions", isBatchMode: false);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            await userInteraction.Received(1).RequestHumanReviewAsync(
                "dbo.USP_ReviewDown",
                Arg.Any<string>(),
                VerificationOutcome.ReviewNotRun, Arg.Any<bool>());
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
            userInteraction.RequestHumanReviewAsync("dbo.USP_FeedbackStale", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
                "dbo.USP_FeedbackStale", regeneratedSpec, VerificationOutcome.ReviewNotRun, Arg.Any<bool>());
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Plan Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = initialMarkdown }), Task.FromResult(new AiResult { Content = regeneratedMarkdown }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
                "TestJobFeedbackOutcome", initialMarkdown, VerificationOutcome.Passed, Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>());
            // 2차 호출: 피드백으로 전면 재생성된, 한 번도 리뷰받지 않은 계획서 -> ReviewNotRun.
            await userInteraction.Received(1).RequestHumanReviewAsync(
                "TestJobFeedbackOutcome", regeneratedMarkdown, VerificationOutcome.ReviewNotRun, Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>());
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = initial }), Task.FromResult(new AiResult { Content = regenerated }));

            var goodReview = new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(goodReview));

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });

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
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
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
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new AiResult { Content = "Structure" });

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

            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
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
            // RetryRescue가 이미 배너를 붙여 돌려주므로 호출부가 또 붙이면 배너가 둘이 된다.
            // 기존 Assert.Contains는 그 경우에도 통과하므로 개수를 직접 센다.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(resultSpec!, @"\[품질 불합격\]"));
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
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            Assert.Contains("계획2고유표시", result.Plan);
            Assert.Contains("3차 시도가 AI 생성 호출 실패로 중단되어", result.Plan);
            // 배치 쌍둥이도 같은 함정을 갖는다.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Plan!, @"\[품질 불합격\]"));
        }

        // 후보가 하나도 없으면 구제할 것이 없다. 현행대로 잡 전체 실패다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_FirstGenerationThrows_StillReturnsNull()
        {
            var specs = new List<(string, string)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task<AiResult>>(_ => throw new InvalidOperationException("generation timed out"));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.Null(result.Plan);
        }

        // finalAiResult는 생성이 성공할 때만 갱신되므로 채택본과 어긋날 수 있었다.
        // 1차가 최고점인데 2차 생성이 성공(점수는 더 낮음)하고 3차가 죽으면,
        // 채택본은 1차인데 Thinking.md/prompt-context.md는 2차를 서술했다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_RescuedPlan_CarriesTheAdoptedAttemptsAiResult()
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
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = plan1, ThinkingText = "생각1", SystemPrompt = "시스템1", UserPrompt = "사용자1" }),
                    _ => Task.FromResult(new AiResult { Content = plan2, ThinkingText = "생각2", SystemPrompt = "시스템2", UserPrompt = "사용자2" }),
                    _ => throw new InvalidOperationException("generation timed out"));

            // 1차 88점이 최고, 2차는 생성에 성공하지만 64점.
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획1고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획2고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 6, ScoreCrud = 5, ScoreInterface = 7, ScoreReadability = 7, ScoreException = 7 }));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            Assert.Contains("계획1고유표시", result.Plan);

            // 산출물이 서술하는 시도와 채택된 시도가 같아야 한다.
            Assert.NotNull(result.Result);
            Assert.Equal("생각1", result.Result!.ThinkingText);
            Assert.Equal("시스템1", result.Result.SystemPrompt);
        }

        // 1차가 채점을 마쳤는데 2·3차가 L1에서 깨지면, 검증된 1차를 버리고
        // L1이 깨진 3차에 "통과 못 함" 경고를 붙여 내보냈다.
        [Fact]
        public async Task RunPipelineAsync_L1Exhausted_AdoptsTheEarlierScoredAttempt()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            const string body = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```\n\n";
            var goodSpec = body + "시도1고유표시";

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = goodSpec }),
                    _ => Task.FromResult(new AiResult { Content = "헤더가 없는 잘못된 문서" }),
                    _ => Task.FromResult(new AiResult { Content = "헤더가 없는 잘못된 문서" }));

            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도1고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("시도1고유표시", resultSpec);
            Assert.Contains("3차 시도가 L1 기계 검증 실패로 중단되어", resultSpec);
            Assert.DoesNotContain("L1 기계 검증을 통과하지 못했습니다", resultSpec);
        }

        // 채점된 시도가 하나도 없으면 순위를 매길 수 없다. 현행 L1 소진 경로를 유지한다.
        [Fact]
        public async Task RunPipelineAsync_L1ExhaustedWithNoScoredAttempt_KeepsTheL1ExhaustedBanner()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_Test", DdlText = "CREATE PROCEDURE USP_Test AS SELECT 1" };
            _dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_Test", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            _aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "헤더가 없는 잘못된 문서" }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("L1 기계 검증을 통과하지 못했습니다", resultSpec);
        }

        // 배치 계획 루프도 같은 결함을 갖는다. 한쪽만 고치면 증상이 이쪽에 남는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_L1Exhausted_AdoptsTheEarlierScoredAttempt()
        {
            var specs = new List<(string, string)>
            {
                ("dbo.USP_Test1", "## 개요\n내용1")
            };

            const string body = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트\n\n";
            var goodPlan = body + "계획1고유표시";

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = goodPlan }),
                    _ => Task.FromResult(new AiResult { Content = "헤더가 없는 잘못된 계획" }),
                    _ => Task.FromResult(new AiResult { Content = "헤더가 없는 잘못된 계획" }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획1고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            Assert.Contains("계획1고유표시", result.Plan);
            Assert.Contains("3차 시도가 L1 기계 검증 실패로 중단되어", result.Plan);
            Assert.DoesNotContain("L1 기계 검증을 통과하지 못했습니다", result.Plan);
        }

        // 1차가 채점을 마쳤는데 2차 리뷰 호출이 죽으면, 검증된 1차를 버리고
        // 미검토 상태인 2차를 "리뷰 안 됨" 경고와 함께 내보냈다.
        [Fact]
        public async Task RunPipelineAsync_ReviewCallFails_AdoptsTheEarlierScoredAttempt()
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
                    _ => Task.FromResult(new AiResult { Content = spec2 }));

            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도1고유표시")))
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));
            _aiService.ReviewSpecificationAsync(spDef, Arg.Is<string>(s => s.Contains("시도2고유표시")))
                .Returns<Task<ReviewResult>>(_ => throw new InvalidOperationException("critic down"));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var (resultSpec, _, _, _, _) = await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_Test", 3, "OpenAI", "instructions", isBatchMode: true);

            Assert.NotNull(resultSpec);
            Assert.Contains("시도1고유표시", resultSpec);
            Assert.Contains("2차 시도가 L2 리뷰 호출 실패로 중단되어", resultSpec);
            Assert.DoesNotContain("L2 AI 교차 리뷰가 수행되지 않았습니다", resultSpec);
        }

        // 배치 계획 루프도 같은 결함을 갖는다. 한쪽만 고치면 증상이 이쪽에 남는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_ReviewCallFails_AdoptsTheEarlierScoredAttempt()
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
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = plan1 }),
                    _ => Task.FromResult(new AiResult { Content = plan2 }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획1고유표시")), "Job_Test")
                .Returns(Task.FromResult(new ReviewResult
                { HasDefects = true, ScoreAccuracy = 9, ScoreCrud = 10, ScoreInterface = 9, ScoreReadability = 9, ScoreException = 7 }));
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Is<string>(s => s.Contains("계획2고유표시")), "Job_Test")
                .Returns<Task<ReviewResult>>(_ => throw new InvalidOperationException("critic down"));

            _userInteraction.RequestHumanReviewAsync("Job_Test", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot);

            Assert.NotNull(result.Plan);
            Assert.Contains("계획1고유표시", result.Plan);
            Assert.Contains("2차 시도가 L2 리뷰 호출 실패로 중단되어", result.Plan);
            Assert.DoesNotContain("L2 AI 교차 리뷰가 수행되지 않았습니다", result.Plan);
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

        // 총 3회 예산에서 1·2차가 같은 점수로 미달하면 3차는 새 목차로 생성돼야 한다.
        // 목차가 원인인 결함은 3/3만 반복해서는 절대 고쳐지지 않는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_ScoreStalls_RedraftsPlanStructure()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "재설계 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            // 세 회차 모두 60점 미달. 최고점이 갱신되지 않으므로 2차에서 정체가 잡힌다.
            var stalled = new ReviewResult { HasDefects = true, FeedbackComment = "구조 결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 };
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(stalled));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "StallJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 1회차 설계 + 정체 후 재설계 = 2회. Job당 1회 상한이므로 3회가 되면 안 된다.
            await aiService.Received(2).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            // 재설계 호출에는 이전 목차와 누적 피드백이 실린다.
            await aiService.Received(1).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                "첫 목차", Arg.Is<string?>(f => f != null && f.Contains("구조 결함")), Arg.Any<CancellationToken>());
            // 마지막 회차는 재설계된 목차로 본문을 만든다.
            await aiService.Received().GenerateConsolidatedBatchPlanAsync(
                "재설계 목차", Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        // 점수가 오르는 중이면 목차는 원인이 아니다. 멀쩡한 구조를 갈아엎지 않는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_ScoreImproves_KeepsPlanStructure()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            // 60점 → 70점. 최고점이 갱신되므로 정체가 아니다.
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 7, ScoreCrud = 7, ScoreInterface = 7, ScoreException = 7, ScoreReadability = 7 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 8, ScoreException = 8, ScoreReadability = 8 }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "ImproveJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            await aiService.Received(1).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        // PlanStructure.md는 언제나 본문을 실제로 만든 목차를 가리켜야 하고,
        // 교체된 목차는 왜 바뀌었는지 추적할 수 있게 남아야 한다.
        //
        // 재설계 이후 회차가 최고점을 갱신해 그 회차가 산출물이 되는 경우다. 재설계
        // 이후 회차가 더 나빠 구제 채택이 이전 목차를 되살리는 반대 경우는
        // RunConsolidatedPipelineAsync_RescueAdoptsPreRedraftAttempt_... 가 덮는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_Redraft_PreservesSupersededStructure()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "재설계 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            // 1차 60, 2차 60(정체 -> 재설계), 3차는 재설계 목차로 통과.
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "PreserveJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            var rawDir = Path.Combine(_consolidatedOutputRoot, "Jobs", "PreserveJob", "raw");
            Assert.Equal("재설계 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.md")));
            Assert.Equal("첫 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.superseded-1.md")));
        }

        // 재수립은 개선 시도이지 필수 단계가 아니다. 실패해도 파이프라인을 죽이지 않는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_RedraftThrows_KeepsExistingStructureAndCompletes()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = "첫 목차" }),
                    _ => throw new InvalidOperationException("재설계 호출 실패"));
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "RedraftFailJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 재설계가 죽어도 계획서는 나온다. 목차는 첫 것을 그대로 쓴다.
            Assert.NotNull(result.Plan);
            await aiService.Received().GenerateConsolidatedBatchPlanAsync(
                "첫 목차", Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        // 재설계 응답이 공백뿐이면 빈 목차로 본문을 만들 수 없다. 기존 목차를 유지하고
        // superseded 파일도 남기지 않는다 — 아무 교체도 일어나지 않았기 때문이다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_RedraftReturnsBlank_KeepsExistingStructureAndSkipsSupersededFile()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "  " });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "RedraftBlankJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            await aiService.Received().GenerateConsolidatedBatchPlanAsync(
                "첫 목차", Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());

            var rawDir = Path.Combine(_consolidatedOutputRoot, "Jobs", "RedraftBlankJob", "raw");
            Assert.False(File.Exists(Path.Combine(rawDir, "PlanStructure.superseded-1.md")));
        }

        // 사용자가 구조를 바꾸라고 하면 목차부터 다시 세운다. 목차를 고정한 채
        // 피드백만 넣으면 "STRICTLY adhering to the Approved Structure"와 충돌한다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3StructuralFeedback_RedraftsBeforeRegenerating()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "1", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "사용자 반영 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            userInteraction.RequestHumanReviewAsync("L3StructJob", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Step 3을 둘로 쪼개라", RedraftStructure = true }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "L3StructJob", "OpenAI", _consolidatedOutputRoot);

            // 사용자 피드백이 재수립 입력으로 실린다.
            await aiService.Received(1).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                "첫 목차", Arg.Is<string?>(f => f != null && f.Contains("Step 3을 둘로 쪼개라")), Arg.Any<CancellationToken>());
            // 재생성 본문은 새 목차를 받는다.
            await aiService.Received().GenerateConsolidatedBatchPlanAsync(
                "사용자 반영 목차", Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        // 오타 수정 같은 피드백에까지 재수립 비용을 물리지 않는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3NonStructuralFeedback_KeepsPlanStructure()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "1", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            userInteraction.RequestHumanReviewAsync("L3PlainJob", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "오타 수정", RedraftStructure = false }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "L3PlainJob", "OpenAI", _consolidatedOutputRoot);

            await aiService.Received(1).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        // 승인 화면은 두 파이프라인이 공유한다. 목차를 가진 통합 배치 경로만
        // 구조 변경 질문을 띄울 자격이 있다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_HumanReview_AdvertisesStructureRedraft()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "1", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            userInteraction.RequestHumanReviewAsync("L3FlagJob", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "L3FlagJob", "OpenAI", _consolidatedOutputRoot);

            await userInteraction.Received(1).RequestHumanReviewAsync(
                "L3FlagJob", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), true, Arg.Any<IReadOnlyList<BatchStepPlan>?>());
        }

        // 단일 SP 명세서 경로에는 다시 세울 목차가 없다. 여기서 구조 변경을 물으면
        // 사용자가 답해도 그 답을 쓸 곳이 없어 "답했는데 아무 일도 없는" 상태가 된다.
        [Fact]
        public async Task RunPipelineAsync_HumanReview_DoesNotAdvertiseStructureRedraft()
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, new MechanicalValidator(), userInteraction, "1", "gpt-4",
                null, aiService, aiService, null, null, null, 8);

            var spDef = new SpDefinition { Schema = "dbo", Name = "USP_NoRedraft", DdlText = "CREATE PROCEDURE USP_NoRedraft AS SELECT 1" };
            dbService.GetSpDetailsAsync(Arg.Any<string>(), "dbo", "USP_NoRedraft", Arg.Any<int>())
                .Returns(Task.FromResult(spDef));

            var specMarkdown = "## 개요\n## 파라미터 목록\n## CRUD 분석\n## 로직 흐름 요약\n## 비즈니스 흐름 시각화\n```mermaid\ngraph TD\nA-->B\n```";
            aiService.GenerateSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = specMarkdown }));
            aiService.ReviewSpecificationAsync(spDef, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            userInteraction.RequestHumanReviewAsync("dbo.USP_NoRedraft", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>())
                .Returns(Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            await orchestrator.RunPipelineAsync(
                "connection_string", "dbo", "USP_NoRedraft", 3, "OpenAI", "instructions", isBatchMode: false);

            await userInteraction.Received(1).RequestHumanReviewAsync(
                "dbo.USP_NoRedraft", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), false);
        }

        // PlanStructure.md는 "산출된 문서를 실제로 만든 목차"를 가리켜야 한다.
        // 2차 정체로 목차를 갈아엎었는데 3차가 더 나쁜 점수를 내면 RetryRescue가
        // 1차를 채택한다. 그때 파일에 재설계 목차가 남아 있으면, 어떤 산출물도
        // 만든 적 없는 목차가 최종 목차로 기록된다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_RescueAdoptsPreRedraftAttempt_PlanStructureNamesThatStructure()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "재설계 목차" });
            // 본문에 출처 목차를 새겨 어느 목차가 만든 문서가 채택됐는지 확인한다.
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(new AiResult { Content = plan + $"\n본문 출처: {ci.ArgAt<string>(0)}" }));

            // 70 -> 60 -> 50. 2차가 최고점을 못 넘겨 재설계가 발동하고, 3차는 더 나빠진다.
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 7, ScoreCrud = 7, ScoreInterface = 7, ScoreException = 7, ScoreReadability = 7 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5, ScoreException = 5, ScoreReadability = 5 }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "RescueStructureJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 채택된 산출물은 1차 시도, 즉 "첫 목차"가 만든 본문이다.
            Assert.NotNull(result.Plan);
            Assert.Contains("본문 출처: 첫 목차", result.Plan);
            Assert.DoesNotContain("본문 출처: 재설계 목차", result.Plan);

            var rawDir = Path.Combine(_consolidatedOutputRoot, "Jobs", "RescueStructureJob", "raw");
            // 파일이 그 목차를 가리켜야 한다.
            Assert.Equal("첫 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.md")));
            // 시도됐다가 버려진 재설계 목차도 추적 가능하게 남는다.
            Assert.Equal("첫 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.superseded-1.md")));
            Assert.Equal("재설계 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.superseded-2.md")));
        }

        // L2 정체로 1회, 이어서 L3 사용자 요청으로 1회. L2가 상한을 소진했어도 L3는
        // 상한을 보지 않으므로 재수립이 한 번 더 일어나고 superseded 번호는 이어진다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_L2ThenL3Redraft_ChainsSupersededIndex()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "2", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    new AiResult { Content = "첫 목차" },
                    new AiResult { Content = "L2 재설계 목차" },
                    new AiResult { Content = "L3 재설계 목차" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            // 1차 70, 2차 60(정체 -> L2 재설계), 3차는 통과. 통과 회차가 최고점이므로
            // 구제 채택이 끼어들지 않고 L2 재설계 목차가 그대로 현행으로 남는다.
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 7, ScoreCrud = 7, ScoreInterface = 7, ScoreException = 7, ScoreReadability = 7 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }),
                    _ => Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 }));

            userInteraction.RequestHumanReviewAsync("L2L3RedraftJob", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Step 2를 둘로 쪼개라", RedraftStructure = true }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            await orchestrator.RunConsolidatedPipelineAsync(specs, "C#", "L2L3RedraftJob", "OpenAI", _consolidatedOutputRoot);

            // 최초 설계 1회 + L2 재설계 1회 + L3 재설계 1회. L3는 1회 상한을 보지 않는다.
            await aiService.Received(3).DraftBatchPlanStructureAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

            var rawDir = Path.Combine(_consolidatedOutputRoot, "Jobs", "L2L3RedraftJob", "raw");
            Assert.Equal("L3 재설계 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.md")));
            Assert.Equal("첫 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.superseded-1.md")));
            Assert.Equal("L2 재설계 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.superseded-2.md")));
        }

        // L3 재설계는 성공했는데 그 목차로 돌린 재생성이 실패하면, 사용자에게는 옛
        // 문서가 그대로 다시 보인다. 그 시점에 PlanStructure.md가 새 목차를 가리키면
        // 사용자가 승인한 문서를 만든 적 없는 목차가 기록으로 남는다.
        [Fact]
        public async Task RunConsolidatedPipelineAsync_L3RedraftButRegenerationFails_KeepsPlanStructureOnDisk()
        {
            var aiService = Substitute.For<IAiService>();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(),
                userInteraction, "1", "gpt-4", null, aiService, aiService, null, null, null, 8);

            var specs = new List<(string, string)> { ("spec1.md", "content1") };
            var plan = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";

            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "첫 목차" }, new AiResult { Content = "사용자 반영 목차" });
            // 재생성만 실패한다. API 타임아웃 한 번이면 재현되는 상황이다.
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = plan }),
                    _ => throw new InvalidOperationException("재생성 타임아웃"));
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 }));

            userInteraction.RequestHumanReviewAsync("L3RegenFailJob", Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "Step 3을 둘로 쪼개라", RedraftStructure = true }),
                    Task.FromResult(new HumanReviewResult { Decision = UserDecision.Approve }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "L3RegenFailJob", "OpenAI", _consolidatedOutputRoot);

            // 사용자가 승인한 문서는 여전히 "첫 목차"가 만든 최초 계획서다.
            Assert.Equal(plan, result.Plan);

            var rawDir = Path.Combine(_consolidatedOutputRoot, "Jobs", "L3RegenFailJob", "raw");
            Assert.Equal("첫 목차", await File.ReadAllTextAsync(Path.Combine(rawDir, "PlanStructure.md")));
            // 아무 교체도 확정되지 않았으므로 superseded 파일도 남지 않는다.
            Assert.False(File.Exists(Path.Combine(rawDir, "PlanStructure.superseded-1.md")));
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

        // 분할 생성 배선(Task 8) 테스트가 공유하는 픽스처와 도우미.

        // LegacyProcedures: 이 클래스 대부분의 배치 테스트가 쓰는 명세서
        // ("dbo.USP_Spec1")를 S01이 커버하도록 선언한다 — 목차 커버리지 검사(Task 11)가
        // 도입된 뒤, 커버리지를 의도적으로 검사하지 않는 기존 테스트에서 예기치
        // 않은 "[커버리지 누락]" 배너가 섞여 나오지 않게 하기 위함이다.
        private const string StepsJson = @"```json
{
  ""Steps"": [
    { ""Code"": ""S01"", ""Name"": ""첫 단계"", ""LegacyProcedures"": [""USP_Spec1""], ""TargetTables"": [""dbo.T1""], ""ErrorCodes"": [""-1""] },
    { ""Code"": ""S02"", ""Name"": ""둘째 단계"", ""TargetTables"": [""dbo.T2""], ""ErrorCodes"": [""-2""] }
  ]
}
```";

        private const string SkeletonMarkdown = @"## 통합 배치 아키텍처 개요
개요.

## Mermaid 기반 통합 흐름도
```mermaid
flowchart TD
A[""시작""] --> B[""끝""]
```

## 단계별 이행 상세 및 의사코드
### 공통 SQL 오류 추적 패턴
공통 규약.

<!-- STEP:S01 -->
<!-- STEP:S02 -->

## 통합 데이터 정합성 검증 SQL 세트
```sql
SELECT 1;
```";

        private static string HealthyStepSection(string code, string table, string errorCode) =>
            $"### {code} 단계\n\n대상은 {table}이고 오류코드는 {errorCode}이다.\n\n```sql\nSELECT 1;\n```";

        /// <summary>
        /// 배치 파이프라인을 세우는 반복 패턴(오케스트레이터 생성 + RunConsolidatedPipelineAsync
        /// 호출 + 임시 출력 디렉터리)을 뽑은 도우미. isBatchMode: true가 필수다 —
        /// 아니면 L3 인간 개입 루프로 들어가 테스트가 멈추거나 의도치 않은 경로를 탄다.
        /// </summary>
        private async Task<ConsolidatedPipelineResult> RunBatchPipeline(IAiService aiService)
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };
            return await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);
        }

        /// <summary>
        /// RunBatchPipeline과 같되 IVerificationUserInteraction을 인자로 받고
        /// isBatchMode를 그대로 넘긴다. L3 인간 개입 루프에 닿아야 하는 테스트
        /// (L3 피드백의 단계 지목 배선 등)가 쓴다. 기존 RunBatchPipeline은 isBatchMode:
        /// true로 고정돼 있어 그 루프에 닿지 못한다 — 94개 호출부가 걸려 있어 건드리지
        /// 않는다.
        /// </summary>
        private async Task<ConsolidatedPipelineResult> RunBatchPipelineWithUi(
            IAiService aiService, IVerificationUserInteraction userInteraction, bool isBatchMode)
        {
            var dbService = Substitute.For<IDbMetadataService>();
            var validator = new MechanicalValidator();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };
            return await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: isBatchMode);
        }

        /// <summary>
        /// 기존 테스트들이 반복해 온 분할 생성 fake 설정(브레인스토밍, 단계 JSON을
        /// 낸 목차, 골격, 정상 단계 섹션, 결함 없는 리뷰)을 한 곳으로 뽑은 도우미.
        /// </summary>
        private static IAiService SplitCapableAiService()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });
            return aiService;
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WithStepList_GeneratesOneSectionPerStep()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var result = await RunBatchPipeline(aiService);

            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await aiService.DidNotReceive().GenerateConsolidatedBatchPlanAsync(
                Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

            Assert.Contains("### S01 단계", result.Plan);
            Assert.Contains("### S02 단계", result.Plan);
            Assert.DoesNotContain("<!-- STEP:", result.Plan);
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WithoutStepList_FallsBackToSingleCall()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            // 목차에 JSON 블록이 없다.
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차 산문만 있다" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown.Replace("<!-- STEP:S01 -->\n<!-- STEP:S02 -->", "### S01 단계\n본문") });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            await RunBatchPipeline(aiService);

            await aiService.Received(1).GenerateConsolidatedBatchPlanAsync(
                Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await aiService.DidNotReceive().GenerateBatchStepSectionAsync(
                Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WhenSkeletonReturnsBlank_FallsBackToSingleCall()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "" });
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            await RunBatchPipeline(aiService);

            await aiService.Received(1).GenerateConsolidatedBatchPlanAsync(
                Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WhenStepMissesFloor_RetriesThatStepExactlyOnce()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });

            // S01은 코드 블록이 없어 하한 미달, S02는 정상.
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return step.Code == "S01"
                        ? new AiResult { Content = "### S01 단계\n\ndbo.T1과 -1만 적고 코드 블록은 없다." }
                        : new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            await RunBatchPipeline(aiService);

            // S01은 2회(최초 + 재시도 1회), S02는 1회. 3회 이상이면 재시도 상한이 깨진 것이다.
            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S01"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await aiService.Received(1).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S02"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WhenStepMissesFloor_SendsFloorFeedbackOnRetry()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return step.Code == "S01"
                        ? new AiResult { Content = "### S01 단계\n\ndbo.T1과 -1만 적고 코드 블록은 없다." }
                        : new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            await RunBatchPipeline(aiService);

            await aiService.Received(1).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S01"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Is<string?>(f => f != null && f.Contains("의사코드 블록이 없습니다")),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WhenStepGenerationThrows_InsertsWarningAndKeepsGoing()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    if (step.Code == "S01") throw new InvalidOperationException("쿼터 초과");
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var result = await RunBatchPipeline(aiService);

            Assert.Contains("이 단계는 생성에 실패했습니다", result.Plan);
            Assert.Contains("### S02 단계", result.Plan);
        }

        /// <summary>
        /// GenerateBySplitAsync는 private이고, 지목 재생성 분기(targeted branch)는
        /// Task 9의 L2 배선이 붙기 전까지 공개 진입점(RunConsolidatedPipelineAsync)에서는
        /// 도달할 수 없다 — pendingDefectiveSteps가 매 시도 뒤 무조건 비워지기 때문이다.
        /// 그래서 이 도우미는 리플렉션으로 그 분기를 직접 두 번 호출해, 손대지 않은
        /// 단계의 하한 위반 기록이 살아남는지를 지금 시점에서 고정한다.
        /// </summary>
        private static async Task<object?> InvokeGenerateBySplitAsync(
            VerificationPipelineOrchestrator orchestrator,
            string planStructure,
            IReadOnlyList<BatchStepPlan> steps,
            List<(string FileName, string Content)> specs,
            string targetLanguage,
            string jobName,
            IMultiProgressScope progressScope,
            string? previousSkeleton,
            AiResult? previousSkeletonResult,
            Dictionary<string, string>? previousSections,
            Dictionary<string, string> previousViolations,
            IReadOnlyList<string> defectiveSteps,
            CancellationToken cancellationToken)
        {
            var method = typeof(VerificationPipelineOrchestrator).GetMethod(
                "GenerateBySplitAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(orchestrator, new object?[]
            {
                planStructure, steps, specs, targetLanguage, jobName, progressScope,
                previousSkeleton, previousSkeletonResult, previousSections, previousViolations, defectiveSteps, cancellationToken
            })!;

            await task;

            var resultProperty = task.GetType().GetProperty("Result");
            return resultProperty!.GetValue(task);
        }

        private static Dictionary<string, string> GetFloorViolations(object splitGeneration) =>
            (Dictionary<string, string>)splitGeneration.GetType().GetProperty("FloorViolations")!.GetValue(splitGeneration)!;

        private static Dictionary<string, string> GetSections(object splitGeneration) =>
            (Dictionary<string, string>)splitGeneration.GetType().GetProperty("Sections")!.GetValue(splitGeneration)!;

        private static AiResult GetGeneration(object splitGeneration) =>
            (AiResult)splitGeneration.GetType().GetProperty("Generation")!.GetValue(splitGeneration)!;

        private static string GetSkeleton(object splitGeneration) =>
            (string)splitGeneration.GetType().GetProperty("Skeleton")!.GetValue(splitGeneration)!;

        [Fact]
        public async Task GenerateBySplitAsync_TargetedRegeneration_PreservesUntouchedStepFloorViolation()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            // S01은 몇 번을 다시 만들어도 코드 블록이 없어 하한 미달. S02는 항상 정상.
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return step.Code == "S01"
                        ? new AiResult { Content = "### S01 단계\n\ndbo.T1과 -1만 적고 코드 블록은 없다." }
                        : new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            var dbService = Substitute.For<IDbMetadataService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);

            var steps = BatchStepPlanParser.TryParse(StepsJson);
            Assert.NotNull(steps);
            var specs = new List<(string FileName, string Content)> { ("dbo.USP_Spec1", "content1") };

            // 1회차: 지목 없이 전부 생성한다. S01은 재시도해도 하한 미달로 남는다.
            var first = await InvokeGenerateBySplitAsync(
                orchestrator, "목차", steps!, specs, "C#", "Job_Test",
                NullProgressScope.Instance, null, null, null, new Dictionary<string, string>(),
                Array.Empty<string>(), CancellationToken.None);
            Assert.NotNull(first);

            var firstViolations = GetFloorViolations(first!);
            Assert.True(firstViolations.ContainsKey("S01"));
            Assert.False(firstViolations.ContainsKey("S02"));

            // 2회차: S02만 지목해 재생성한다. S01은 이 회차에서 전혀 건드리지 않는다.
            var second = await InvokeGenerateBySplitAsync(
                orchestrator, "목차", steps!, specs, "C#", "Job_Test",
                NullProgressScope.Instance, GetSkeleton(first!), GetGeneration(first!), GetSections(first!), firstViolations,
                new[] { "S02" }, CancellationToken.None);
            Assert.NotNull(second);

            var secondViolations = GetFloorViolations(second!);

            // 불변식: 지목 재생성이 건드리지 않은 S01의 하한 미달 기록이 여전히 살아 있어야 한다.
            Assert.True(secondViolations.ContainsKey("S01"));
            Assert.False(secondViolations.ContainsKey("S02"));

            // 지목 재생성이 실제로 골격 호출 없이 일어났는지 확인해, 이 테스트가
            // targeted 분기를 정말로 탔는지(가짜 커버리지가 아닌지) 검증한다.
            await aiService.Received(1).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// 코드 리뷰 지적 사항(Finding 1) 픽스: 지목 재생성(targeted) 분기는 골격
        /// 호출을 건너뛰므로 예전에는 `new AiResult { Content = skeleton }`이라는
        /// 스텁을 만들어 그 회차의 finalAiResult로 실어 보냈다. 그 스텁은
        /// SystemPrompt·UserPrompt·ThinkingText가 전부 null이라, Program.cs가
        /// raw/prompt-context.md를 빈 껍데기로 쓰고 docs/Thinking.md를 이전
        /// 회차 것으로 방치했다 — AGENTS.md가 문서화한 "채택본의 AiResult가
        /// 함께 실린다" 계약과 정반대다.
        ///
        /// 이 테스트는 지목 재생성 회차가 실제로 골격을 만들어 낸 회차의 진짜
        /// AiResult(SystemPrompt/UserPrompt/ThinkingText 포함)를 재사용하는지를
        /// 고정한다. 픽스 전에는 골격 호출의 AiResult에 값을 채워도 지목 재생성
        /// 결과의 Generation이 빈 스텁이라 이 단언이 실패했다(직접 확인함).
        /// </summary>
        [Fact]
        public async Task GenerateBySplitAsync_TargetedRegeneration_ReusesTheSkeletonsRealAiResult()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult
                {
                    Content = SkeletonMarkdown,
                    SystemPrompt = "골격 시스템 프롬프트",
                    UserPrompt = "골격 사용자 프롬프트",
                    ThinkingText = "골격 사고 과정"
                });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            var dbService = Substitute.For<IDbMetadataService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);

            var steps = BatchStepPlanParser.TryParse(StepsJson);
            Assert.NotNull(steps);
            var specs = new List<(string FileName, string Content)> { ("dbo.USP_Spec1", "content1") };

            // 1회차: 골격을 실제로 만든다.
            var first = await InvokeGenerateBySplitAsync(
                orchestrator, "목차", steps!, specs, "C#", "Job_Test",
                NullProgressScope.Instance, null, null, null, new Dictionary<string, string>(),
                Array.Empty<string>(), CancellationToken.None);
            Assert.NotNull(first);

            var firstGeneration = GetGeneration(first!);
            Assert.Equal("골격 시스템 프롬프트", firstGeneration.SystemPrompt);

            // 2회차: S02만 지목한다 — 골격 호출을 건너뛰는 targeted 분기를 탄다.
            var second = await InvokeGenerateBySplitAsync(
                orchestrator, "목차", steps!, specs, "C#", "Job_Test",
                NullProgressScope.Instance, GetSkeleton(first!), firstGeneration, GetSections(first!),
                GetFloorViolations(first!), new[] { "S02" }, CancellationToken.None);
            Assert.NotNull(second);

            var secondGeneration = GetGeneration(second!);

            // 핵심 불변식: 지목 재생성의 Generation은 SystemPrompt/UserPrompt/
            // ThinkingText가 모두 채워진 골격의 진짜 AiResult여야 한다 — 전부
            // null인 빈 스텁이면 안 된다.
            Assert.Equal("골격 시스템 프롬프트", secondGeneration.SystemPrompt);
            Assert.Equal("골격 사용자 프롬프트", secondGeneration.UserPrompt);
            Assert.Equal("골격 사고 과정", secondGeneration.ThinkingText);

            // targeted 분기를 정말로 탔는지(골격을 다시 만들지 않았는지) 확인해
            // 이 단언이 우연히 통과한 게 아님을 증명한다.
            await aiService.Received(1).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WithDefectiveSteps_RegeneratesOnlyThoseSteps()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 1회차는 S02를 지목해 결함, 2회차는 통과.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => reviewCall++ == 0
                    ? new ReviewResult { HasDefects = true, FeedbackComment = "S02 결함", DefectiveSteps = { "S02" }, ScoreAccuracy = 6, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 }
                    : new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            await RunBatchPipeline(aiService);

            // 골격은 1회만. 지목 재생성은 골격을 다시 만들지 않는다.
            await aiService.Received(1).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            // S01은 1회차에만, S02는 1회차 + 지목 재생성으로 2회.
            await aiService.Received(1).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S01"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S02"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WithoutDefectiveSteps_RegeneratesTheWholeDocument()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => reviewCall++ == 0
                    ? new ReviewResult { HasDefects = true, FeedbackComment = "문서 전반 결함", ScoreAccuracy = 6, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 }
                    : new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            await RunBatchPipeline(aiService);

            // 지목이 없으면 골격부터 다시 만든다.
            await aiService.Received(2).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// C3(Task 9 보정)를 고정하는 테스트.
        ///
        /// stepFloorViolations는 아직(Task 10 이전) 최종 문서나 어떤 AI 호출 인자에도
        /// 반영되지 않는다 — RunConsolidatedPipelineAsync 안의 지역 변수로만 존재하고
        /// 파이프라인이 끝나면 버려진다. 그래서 공개 진입점을 통해서는 "재수립 후 옛
        /// 코드의 위반 기록이 안 남는다"를 직접 관찰할 방법이 없다 — GenerateBySplitAsync
        /// 자신은 pending(이번 회차에 다시 만들 단계) 목록에 없는 코드는 원래 절대
        /// 건드리지 않으므로, previousViolations로 무엇을 넘기든 그 경계만으로는 이
        /// 픽스의 유무를 구분할 수 없다(직접 확인함 — 이 테스트를 오케스트레이터 픽스
        /// 없이 돌려도 통과한다).
        ///
        /// 그래서 픽스 자체를 ClearSplitGenerationCacheAfterRedraft라는 이름의 private
        /// static 메서드로 뽑아 리플렉션으로 직접 고정한다. 동작을 바꾸지 않는 순수한
        /// 추출이며, 이 테스트는 그 메서드가 다섯 항목(골격/골격 AiResult/섹션/단계
        /// 목록/하한 위반 기록)을 전부 지우는지 — 특히 이전에 빠뜨렸던
        /// stepFloorViolations를 지우는지 — 를 고정한다. lastSkeletonResult는
        /// 최종 픽스(지목 재생성이 스텁 대신 실제 골격 AiResult를 재사용하는 것)에서
        /// 추가됐다 — 재수립 시 이 값을 안 지우면 새 목차의 첫 회차(지목 없이 전부
        /// 재생성)가 여전히 targeted 조건(previousSkeletonResult != null)을 만족해
        /// 옛 골격의 AiResult를 잘못 재사용할 뻔했다. 다만 RunConsolidatedPipelineAsync
        /// 루프가 재수립 시점에 실제로 이 메서드를 호출하는지는 이 테스트가 아니라
        /// 코드 리뷰로 확인해야 한다.
        /// </summary>
        [Fact]
        public void ClearSplitGenerationCacheAfterRedraft_ClearsAllFiveCachedItemsIncludingFloorViolations()
        {
            var method = typeof(VerificationPipelineOrchestrator).GetMethod(
                "ClearSplitGenerationCacheAfterRedraft",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var pendingDefectiveSteps = new List<string> { "S02" };
            var args = new object?[]
            {
                "골격 텍스트",                                                    // lastSkeleton (out)
                new AiResult { Content = "골격 텍스트" },                         // lastSkeletonResult (out)
                new Dictionary<string, string> { ["S01"] = "섹션" },              // lastStepSections (out)
                new List<BatchStepPlan>                                            // currentSteps (out)
                {
                    new("S01", "첫 단계", Array.Empty<string>(), new[] { "dbo.T1" }, new[] { "-1" }, false),
                },
                new Dictionary<string, string> { ["S01"] = "S01 (하한 미달)" },   // stepFloorViolations (out)
                pendingDefectiveSteps,
            };

            method!.Invoke(null, args);

            Assert.Null(args[0]);
            Assert.Null(args[1]);
            Assert.Null(args[2]);
            Assert.Null(args[3]);
            // 이전에 빠뜨렸던 바로 그 항목: 재수립 후 stepFloorViolations는 통째로
            // 새 빈 사전이어야 하고, 옛 코드("S01")를 담고 있으면 안 된다.
            var clearedViolations = Assert.IsType<Dictionary<string, string>>(args[4]);
            Assert.Empty(clearedViolations);
            Assert.Empty(pendingDefectiveSteps);
        }

        // Task 10: 하한 미달 단계가 배너로 노출된다. 지금까지 stepFloorViolations는
        // 기록만 됐지 어디에도 읽히지 않았다 — 이 테스트가 그 소비자다.
        [Fact]
        public async Task RunConsolidatedPipeline_WhenAStepStaysBelowFloor_PrependsWarningBanner()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            // S01은 재시도해도 코드 블록이 없다.
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return step.Code == "S01"
                        ? new AiResult { Content = "### S01 단계\n\ndbo.T1과 -1만 있다." }
                        : new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var result = await RunBatchPipeline(aiService);

            Assert.Contains("하한 미달", result.Plan);
            Assert.Contains("S01", result.Plan);
            // 하한 미달은 VerificationOutcome에 새 상태를 만들지 않는다. L2를
            // 통과한 문서는 Passed로 남고, 미달 사실은 배너만 나른다.
            Assert.Equal(VerificationOutcome.Passed, result.Outcome);
        }

        // 배너는 건강한 경로에서 절대 나타나면 안 된다. 부재를 확인하는 테스트가
        // 존재를 확인하는 테스트만큼 중요하다 — 그래야 향후 리팩터링이 조건을
        // 뒤집어 배너가 늘 붙는 사고를 잡는다.
        [Fact]
        public async Task RunConsolidatedPipeline_WhenAllStepsPassFloor_DoesNotPrependWarningBanner()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var result = await RunBatchPipeline(aiService);

            Assert.DoesNotContain("하한 미달", result.Plan);
            Assert.DoesNotContain("[!WARNING]", result.Plan);
        }

        /// <summary>
        /// 코드 리뷰 지적 사항(Finding 3) 픽스: 1회차는 분할 경로로 S01이 하한
        /// 미달로 기록된다. 2회차는 (Critic이 특정 단계를 지목하지 않아) 골격부터
        /// 다시 만들어야 하는데, 그 골격 호출이 실패한다(빈 응답) — GenerateBySplitAsync가
        /// null을 돌려주고 호출부가 단일 호출(GenerateConsolidatedBatchPlanAsync)로
        /// 폴백한다.
        ///
        /// 그 단일 호출 문서는 분할 문서와 완전히 다른 구조라 S01이라는 섹션 자체가
        /// 없다. 픽스 전에는 stepFloorViolations가 이 폴백 경로에서 지워지지 않아,
        /// 1회차가 남긴 "S01 (하한 미달)" 기록이 그대로 살아남아 존재하지도 않는
        /// 단계를 가리키는 배너가 최종(단일 호출) 문서에 붙었다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WhenSplitFallsBackAfterSkeletonRetryFails_ClearsStaleFloorViolations()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });

            // 1회차 골격은 성공, 2회차 골격은 빈 응답(실패)으로 분할이 무산된다.
            var skeletonCall = 0;
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => skeletonCall++ == 0
                    ? new AiResult { Content = SkeletonMarkdown }
                    : new AiResult { Content = "" });

            // 1회차: S01은 코드 블록이 없어 하한 미달로 기록된다. S02는 정상.
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return step.Code == "S01"
                        ? new AiResult { Content = "### S01 단계\n\ndbo.T1과 -1만 있다." }
                        : new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 2회차: 골격이 실패해 폴백하는 단일 호출 문서. S01/S02와 무관한
            // 완전히 다른 구조라 하한 미달을 겪은 옛 코드가 어디에도 없다.
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult
                {
                    Content = SkeletonMarkdown.Replace(
                        "<!-- STEP:S01 -->\n<!-- STEP:S02 -->",
                        "### 전체 단계\n\n단일 호출로 만든 본문.\n\n```sql\nSELECT 1;\n```")
                });

            // 1회차는 (특정 단계 지목 없이) 문서 전반 결함으로 실패, 2회차는 통과.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => reviewCall++ == 0
                    ? new ReviewResult { HasDefects = true, FeedbackComment = "문서 전반 결함", ScoreAccuracy = 6, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 }
                    : new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var result = await RunBatchPipeline(aiService);

            // 실제로 단일 호출 폴백을 탔는지 먼저 확인한다 — 아니면 이 테스트가
            // 의도한 경로를 검증하지 못한다.
            await aiService.Received(1).GenerateConsolidatedBatchPlanAsync(
                Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            Assert.Contains("전체 단계", result.Plan);

            // 핵심 불변식: 더 이상 존재하지 않는 S01의 하한 미달 기록이 완전히
            // 다른 구조의 최종 문서에 남으면 안 된다.
            Assert.DoesNotContain("하한 미달", result.Plan);
            Assert.DoesNotContain("S01", result.Plan);
        }

        /// <summary>
        /// 회귀 재현: 위 테스트가 고친 폴백 분기(1775줄 부근)는 stepFloorViolations만
        /// 지우고 lastSkeleton/lastSkeletonResult/lastStepSections는 남겨뒀다. 3회차
        /// 시나리오로 재현한다 — 1회차는 분할로 S01(하한 미달, 기록됨)·S02(정상)를
        /// 만든다. 2회차는 (1회차가 어떤 단계도 지목하지 않아) 골격부터 다시 만들어야
        /// 하는데 그 골격 호출이 빈 응답을 돌려줘 단일 호출로 폴백한다 — 여기서
        /// stepFloorViolations는 비워지지만 lastSkeleton과 lastStepSections(S01의
        /// 하한 미달 본문 포함)는 버그 있는 코드에서 살아남는다. 2회차는 점수가
        /// 올라 최고점 후보를 갱신하므로(개선) 목차 재수립이 발동하지 않고, S02를
        /// 결함으로 지목한다. 3회차는 지목 재생성(targeted)으로 들어가는데, 버그
        /// 있는 코드에서는 previousSkeleton/previousSections가 여전히 non-null이라
        /// targeted 조건을 충족해 S02만 새로 만들고 S01은 1회차의 캐시된 하한 미달
        /// 본문을 위반 기록 없이 그대로 재조립한다. 3회차 리뷰가 통과하면 최종
        /// 문서는 Passed로 끝나면서도 하한 미달 S01 본문을 배너 없이 실어 나른다 —
        /// 이 기능이 막으려는 바로 그 과소 보고다.
        ///
        /// 픽스 전에는 이 테스트가 다음 셋 다 실패한다: 골격 호출이 2회에 그친다
        /// (3회여야 한다 — 캐시가 지워졌다면 3회차도 골격부터 다시 만들어야 한다),
        /// 최종 문서에 "하한 미달" 배너가 없다, S01이 배너 없이 결함 본문 그대로
        /// 실린다. 픽스 전 상태에서 직접 실행해 세 단언이 모두 실패하는 것을
        /// 확인했다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WhenSplitFallsBackMidRetryLoop_ClearsCacheSoALaterTargetedRegenCannotResurrectTheStaleFloorViolation()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });

            // 1회차 골격은 성공, 2회차 골격은 빈 응답(폴백 유발), 3회차는 픽스가
            // 캐시를 지웠다면 다시 골격부터 만들어야 하므로 성공을 돌려준다.
            var skeletonCall = 0;
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var call = skeletonCall++;
                    return call == 1
                        ? new AiResult { Content = "" }
                        : new AiResult { Content = SkeletonMarkdown };
                });

            // S01은 몇 번을 다시 만들어도 코드 블록이 없어 하한 미달. S02는 항상 정상.
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return step.Code == "S01"
                        ? new AiResult { Content = "### S01 단계\n\ndbo.T1과 -1만 있다." }
                        : new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 2회차의 골격 실패로 인한 단일 호출 폴백 문서.
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult
                {
                    Content = SkeletonMarkdown.Replace(
                        "<!-- STEP:S01 -->\n<!-- STEP:S02 -->",
                        "### 전체 단계\n\n단일 호출로 만든 본문.\n\n```sql\nSELECT 1;\n```")
                });

            // 1회차: 결함이 있으나 특정 단계를 지목하지 않는다(문서 전반 결함) —
            // 그래야 2회차가 지목 재생성이 아니라 전체 재생성(골격부터)으로 간다.
            // 2회차: 점수가 올라(최고점 후보 갱신, 재수립 미발동) S02를 지목한다.
            // 3회차: 통과.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var call = reviewCall++;
                    return call switch
                    {
                        0 => new ReviewResult { HasDefects = true, FeedbackComment = "문서 전반 결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 },
                        1 => new ReviewResult { HasDefects = true, FeedbackComment = "S02 결함", DefectiveSteps = { "S02" }, ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 },
                        _ => new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 },
                    };
                });

            var result = await RunBatchPipeline(aiService);

            Assert.Equal(VerificationOutcome.Passed, result.Outcome);

            // 캐시가 실제로 통째로 지워졌다는 증거: 3회차도 골격부터 다시 만들어야
            // 한다. 버그 있는 코드에서는 지목 재생성이 캐시를 재사용해 이 호출이
            // 일어나지 않는다(2회에 그친다).
            await aiService.Received(3).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

            // 핵심 불변식: S01이 하한 미달 본문 그대로 최종 문서에 실린다면, 반드시
            // 그 사실을 배너가 알려야 한다 — 침묵해서는 안 된다.
            Assert.Contains("dbo.T1과 -1만 있다", result.Plan!);
            Assert.Contains("하한 미달", result.Plan!);
            Assert.Contains("S01", result.Plan!);
        }

        /// <summary>
        /// 코드 리뷰 지적 사항(Finding 2, 과소 보고 방향) 픽스: 1회차는 S01이
        /// 하한 미달로 기록된 채 최고점 후보가 된다. 2회차는 지목 재생성으로
        /// S01만 고치지만(그 회차 자신의 라이브 stepFloorViolations에서 S01이
        /// 사라진다) 점수가 1회차보다 낮아 후보를 갱신하지 못한다. 재시도
        /// 예산이 소진되면 RetryRescue는 최고점(1회차, 여전히 S01이 미달인
        /// 문서)을 채택한다.
        ///
        /// 픽스 전에는 배너가 루프 종료 시점의 살아있는 stepFloorViolations(2회차
        /// 것, S01 없음)를 읽어 채택된 1회차 문서에 대해 침묵했다 — 그 문서에는
        /// 여전히 하한 미달 S01 본문이 실려 있는데도. 이것이 finding이 지적한
        /// "과소 보고"다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WhenRescuedEarlierAttemptIsAdopted_BannerReflectsThatAttemptsFloorViolation()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });

            // S01: 1회차(초기 + 재시도, 2회 호출)는 하한 미달. 3회 이후(2회차의
            // 지목 재생성)는 정상.
            var s01Call = 0;
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    if (step.Code != "S01")
                    {
                        return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                    }

                    s01Call++;
                    return s01Call <= 2
                        ? new AiResult { Content = "### S01 단계\n\ndbo.T1과 -1만 있다." }
                        : new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var call = reviewCall++;
                    // 1회차: 88점, S01을 지목해 지목 재생성을 유도한다.
                    // 2회차: 60점 — 1회차보다 낮아 최고점 후보를 갱신하지 못한다.
                    return call == 0
                        ? new ReviewResult { HasDefects = true, FeedbackComment = "S01 결함", DefectiveSteps = { "S01" }, ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 8 }
                        : new ReviewResult { HasDefects = true, FeedbackComment = "여전히 결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 };
                });

            var dbService = Substitute.For<IDbMetadataService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            // maxL2Attempts="1" → 총 시도 2회. 2회차에서 재시도 예산이 소진돼 구제가 발동한다.
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Equal(VerificationOutcome.QualityRejected, result.Outcome);
            // 채택된 문서가 실제로 1회차(S01이 여전히 하한 미달 본문)라는 증거.
            Assert.Contains("dbo.T1과 -1만 있다", result.Plan!);
            // 핵심 불변식: 배너는 채택된 1회차 문서의 위반 기록을 반영해야 한다.
            Assert.Contains("하한 미달", result.Plan!);
            Assert.Contains("S01", result.Plan!);
        }

        /// <summary>
        /// 코드 리뷰 지적 사항(Finding 2, 과다 보고 방향) 픽스: 1회차는 모든 단계가
        /// 건강해(하한 위반 없음) 최고점 후보가 된다. 2회차는(1회차가 특정 단계를
        /// 지목하지 않아) 골격부터 전체를 다시 만드는데, 이번엔 S01이 하한 미달이
        /// 된다. 2회차 점수가 1회차보다 낮아 후보를 갱신하지 못한다. 재시도 예산이
        /// 소진되면 RetryRescue는 최고점(1회차, 하한 위반이 전혀 없는 문서)을
        /// 채택한다.
        ///
        /// 픽스 전에는 배너가 루프 종료 시점의 살아있는 stepFloorViolations(2회차
        /// 것, S01 있음)를 읽어, 하한 위반이 전혀 없는 1회차 문서 위에 존재하지
        /// 않는 결함을 보고했다 — finding이 지적한 "과다 보고"다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WhenRescuedEarlierAttemptIsAdopted_BannerOmitsALaterAttemptsFloorViolation()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });

            // S01: 1회차(첫 호출)는 정상. 2회차(전체 재생성, 초기 + 재시도)는 하한 미달.
            var s01Call = 0;
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    if (step.Code != "S01")
                    {
                        return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                    }

                    s01Call++;
                    return s01Call == 1
                        ? new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) }
                        : new AiResult { Content = "### S01 단계\n\ndbo.T1과 -1만 있다." };
                });

            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var call = reviewCall++;
                    // 1회차: 88점, 특정 단계를 지목하지 않아(문서 전반 결함) 2회차는
                    // 전체 재생성으로 간다. 2회차: 60점 — 최고점 후보를 갱신 못 한다.
                    return call == 0
                        ? new ReviewResult { HasDefects = true, FeedbackComment = "문서 전반 결함", ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 8 }
                        : new ReviewResult { HasDefects = true, FeedbackComment = "여전히 결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 };
                });

            var dbService = Substitute.For<IDbMetadataService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "1", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Equal(VerificationOutcome.QualityRejected, result.Outcome);
            // 2회차가 실제로 골격부터 전체 재생성을 했는지(의도한 경로인지) 확인한다.
            await aiService.Received(2).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            // 핵심 불변식: 채택된 1회차 문서는 하한 위반이 전혀 없다. 2회차(다른
            // 회차)의 위반 기록이 배너로 새어 나오면 안 된다.
            Assert.DoesNotContain("하한 미달", result.Plan!);
        }

        // R2 (Task 9 리뷰에서 이월): Critic이 대소문자가 다른 유효 코드("s01")와
        // 목차에 없는 코드("S99")를 함께 지목해도, 실제 존재하는 단계만 대소문자
        // 무시 매칭으로 재생성되고 지어낸 코드는 조용히 버려져야 한다.
        // 리뷰어가 이 필터를 제거하는 버그를 재현해 세 테스트가 모두 초록으로
        // 남는 것을 확인했다 — 오케스트레이터 레벨에서 이를 고정하는 테스트가
        // 없었기 때문이다.
        [Fact]
        public async Task RunConsolidatedPipeline_WithDefectiveSteps_MatchesCaseInsensitivelyAndDropsInventedCode()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 1회차는 "s01"(대소문자 다름)과 "S99"(존재하지 않는 코드)를 함께 지목,
            // 2회차는 통과.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => reviewCall++ == 0
                    ? new ReviewResult { HasDefects = true, FeedbackComment = "S01 결함", DefectiveSteps = { "s01", "S99" }, ScoreAccuracy = 6, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 }
                    : new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            await RunBatchPipeline(aiService);

            // 골격은 1회만. 지목 재생성이 실제로 targeted 분기를 탔다는 증거이며,
            // 그것은 "s01"이 "S01"과 대소문자 무시로 매칭됐을 때만 일어난다.
            await aiService.Received(1).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            // S01(지목, 대소문자 다름)은 1회차 + 지목 재생성 = 2회.
            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S01"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            // S02는 지목되지 않아 1회차에만 생성되고, 지목 재생성에서는 캐시를
            // 재사용한다. "S99"는 목차에 없는 코드라 애초에 어떤 BatchStepPlan과도
            // 매칭될 수 없다 — 재생성 호출이 일어나지 않는다는 사실 자체가
            // "조용히 버려짐"의 증거다.
            await aiService.Received(1).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S02"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        /// <summary>
        /// 코드 리뷰 지적 사항 픽스: 오케스트레이터 레벨 필터
        /// (RunConsolidatedPipelineAsync ~1922-1928)와 GenerateBySplitAsync 내부
        /// 필터(~2318)는 서로 다른 질문에 답한다 — 겉보기 동작이 겹쳐 보였을 뿐
        /// 대체 관계가 아니다. 안쪽 필터는 "이미 targeted 모드에 들어간 뒤 어느
        /// 단계를 건드릴지"를 정한다. 바깥쪽(오케스트레이터) 필터는 "지목된 코드
        /// 중 유효한 것이 하나라도 있어 targeted 모드에 들어갈 자격이 있는지"를
        /// 정한다. Critic이 지목한 코드가 전부 지어낸 것이면 바깥쪽 필터가
        /// pendingDefectiveSteps를 비워 다음 회차가 targeted가 아니라 전체
        /// 재생성으로 폴백해야 한다. 바깥쪽 필터가 없으면 pendingDefectiveSteps에
        /// 지어낸 코드("S99")가 그대로 남아 defectiveSteps.Count &gt; 0이 되고,
        /// GenerateBySplitAsync는 targeted 모드로 들어가지만 그 안쪽 필터가
        /// "S99"와 매칭되는 단계를 하나도 찾지 못해 pending이 빈 목록이 된다 —
        /// 즉 단 하나의 단계도 다시 만들지 않고 결함 있는 본문을 그대로 Critic에게
        /// 다시 제출하는 조용한 무동작(no-op)이 발생한다.
        ///
        /// 리뷰어가 실제로 이 버그를 재현해 두 단계 모두 생성 호출이 2회에서
        /// 1회로 줄어드는 것을 확인했다. 이 테스트가 그 사실을 고정한다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WithOnlyInventedDefectiveSteps_FallsBackToFullRegeneration()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 1회차는 "S99"(목차에 없는, 오직 지어낸 코드) 하나만 지목, 2회차는 통과.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => reviewCall++ == 0
                    ? new ReviewResult { HasDefects = true, FeedbackComment = "정체불명 결함", DefectiveSteps = { "S99" }, ScoreAccuracy = 6, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 }
                    : new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            await RunBatchPipeline(aiService);

            // 지목 코드가 전부 지어낸 것이면 targeted 모드에 들어갈 자격이 없다.
            // 골격을 다시 만들어야 한다(1회차 최초 생성 + 2회차 전체 재생성 = 2회).
            await aiService.Received(2).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            // 두 단계 모두 전체 재생성 대상이다. 하나라도 1회면 그 단계가 조용히
            // 재사용되며 결함 있는 본문이 그대로 다시 제출된 것이다.
            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S01"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S02"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        // 재수립된 목차용 골격. 코드가 다른 목차로 문서가 다시 조립될 때 배너가
        // 무엇을 실어야/실으면 안 되는지를 R1이 검증한다.
        private static string SkeletonMarkdownFor(params string[] codes) =>
            SkeletonMarkdown.Replace(
                "<!-- STEP:S01 -->\n<!-- STEP:S02 -->",
                string.Join("\n", codes.Select(c => $"<!-- STEP:{c} -->")));

        // 재수립 후 새 목차. S01/S02와 겹치지 않는 코드(T01/T02)를 써서, 옛 코드의
        // 흔적이 남아 있으면 즉시 드러나게 한다.
        private const string StepsJsonRedrafted = @"```json
{
  ""Steps"": [
    { ""Code"": ""T01"", ""Name"": ""새 첫 단계"", ""LegacyProcedures"": [""USP_Spec1""], ""TargetTables"": [""dbo.T1""], ""ErrorCodes"": [""-1""] },
    { ""Code"": ""T02"", ""Name"": ""새 둘째 단계"", ""TargetTables"": [""dbo.T2""], ""ErrorCodes"": [""-2""] }
  ]
}
```";

        /// <summary>
        /// R1 (Task 9 리뷰에서 이월): Task 9는 ClearSplitGenerationCacheAfterRedraft의
        /// 본문만 리플렉션으로 고정했다. RunConsolidatedPipelineAsync가 재수립 지점에서
        /// 그 메서드를 실제로 호출하고 네 out 값(특히 stepFloorViolations)을 살아있는
        /// 지역 변수에 진짜로 되묻는지는 아무 테스트도 지키지 않았다 — 리뷰어가
        /// "메서드는 옳지만 호출부에서 stepFloorViolations의 out 값을 버린다"는 배선
        /// 버그를 재현했을 때 기존 세 테스트가 전부 초록으로 남았다.
        ///
        /// 이제 배너가 그 사전을 렌더링하므로 공개 진입점을 통해 관찰할 수 있다.
        /// 첫 목차(S01/S02)에서 S01이 하한 미달로 기록된 뒤, 정체로 인해 목차가
        /// 재수립되어 코드가 겹치지 않는 새 목차(T01/T02)로 바뀐다. 배선이 옳다면
        /// stepFloorViolations는 재수립 시점에 통째로 비워지고, T01/T02는 둘 다
        /// 건강하므로 최종 문서에는 하한 미달 기록이 전혀 남지 않는다. 배선이 버그가
        /// 있다면(사전을 그대로 들고 넘어가면) 문서에 더 이상 존재하지 않는 S01을
        /// 가리키는 배너가 남는다 — 그것이 바로 이 픽스가 막아야 할 사용자 가시
        /// 증상이다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_AfterStructureRedraft_BannerOmitsStepCodeFromSupersededOutline()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            // 최초 목차(S01/S02), 재수립 목차(T01/T02) 순서로 반환된다.
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    new AiResult { Content = "## 목차\n" + StepsJson },
                    new AiResult { Content = "## 목차\n" + StepsJsonRedrafted });
            // 골격은 그때그때 요청받은 단계 코드로 만든다 — 첫 목차든 재수립된
            // 목차든 같은 목으로 처리한다.
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var steps = call.Arg<IReadOnlyList<BatchStepPlan>>();
                    return new AiResult { Content = SkeletonMarkdownFor(steps.Select(s => s.Code).ToArray()) };
                });
            // S01은 몇 번을 다시 만들어도 하한 미달. 그 외 코드(S02, T01, T02)는 건강하다.
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return step.Code == "S01"
                        ? new AiResult { Content = "### S01 단계\n\ndbo.T1과 -1만 적고 코드 블록은 없다." }
                        : new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 1·2회차는 60점으로 정체(최고점 갱신 없음) → 2회차에서 목차 재수립이
            // 발동한다(StructureRedraftPolicy: 미갱신 1회로 발동). 3회차는 새 목차로
            // 통과.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var call = reviewCall++;
                    return call < 2
                        ? new ReviewResult { HasDefects = true, FeedbackComment = "구조 결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }
                        : new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 };
                });

            var result = await RunBatchPipeline(aiService);

            // 목차가 실제로 재수립되어 새 코드로 문서가 조립됐는지 먼저 확인한다.
            Assert.Contains("T01", result.Plan);
            Assert.Contains("T02", result.Plan);
            // 핵심 불변식: 더 이상 존재하지 않는 옛 목차의 코드가 배너에 남으면
            // 안 된다. 배선이 옳다면 하한 미달 기록 자체가 재수립 시점에 비워지고
            // T01/T02는 둘 다 건강하므로 배너가 아예 붙지 않는다.
            Assert.DoesNotContain("S01", result.Plan);
            Assert.DoesNotContain("하한 미달", result.Plan);
        }

        // 목차 스텝이 선언한 LegacyProcedures가 원본 명세서 전부를 커버하는지
        // 검사한다(Task 11). 하한 미달(StepFloorViolations)이 "스텝은 있는데
        // 내용이 부실하다"라면 이 검사는 "그 프로시저를 다룰 스텝 자체가 없다"를
        // 잡는다 — 목차가 3개 스텝만 내고 명세서가 12개면 부실 스텝보다 더
        // 나쁜, 아무 흔적 없는 누락이 생긴다.
        //
        // S01만 "USP_Spec1"(dbo.USP_Spec1)을 커버하는 목차. S02는 다른 프로시저를 다룬다는 설정으로
        // 아무것도 선언하지 않는다.
        private const string StepsJsonPartialCoverage = @"```json
{
  ""Steps"": [
    { ""Code"": ""S01"", ""Name"": ""첫 단계"", ""LegacyProcedures"": [""USP_Spec1""], ""TargetTables"": [""dbo.T1""], ""ErrorCodes"": [""-1""] },
    { ""Code"": ""S02"", ""Name"": ""둘째 단계"", ""TargetTables"": [""dbo.T2""], ""ErrorCodes"": [""-2""] }
  ]
}
```";

        [Fact]
        public async Task RunConsolidatedPipeline_WhenOutlineOmitsAProcedure_PrependsUncoveredProceduresBanner()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJsonPartialCoverage });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var dbService = Substitute.For<IDbMetadataService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            // dbo.USP_Spec2는 어느 스텝의 LegacyProcedures에도 등장하지 않는다.
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1"), ("dbo.USP_Spec2", "content2") };

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 하한 미달이나 리뷰 결함이 전혀 없어도(문서 자체는 Passed) 커버리지
            // 누락은 그와 별개로 배너가 붙어야 한다.
            Assert.Equal(VerificationOutcome.Passed, result.Outcome);
            Assert.Contains("[커버리지 누락]", result.Plan);
            Assert.Contains("dbo.USP_Spec2", result.Plan);
            // 커버된 dbo.USP_Spec1은 이 배너에 이름이 오르면 안 된다.
            var bannerLine = result.Plan!.Split('\n').First(line => line.Contains("dbo.USP_Spec2"));
            Assert.DoesNotContain("dbo.USP_Spec1", bannerLine);
        }

        [Fact]
        public async Task RunConsolidatedPipeline_WhenOutlineCoversEveryProcedure_OmitsUncoveredProceduresBanner()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            // S01은 "USP_Spec1", S02는 "USP_Spec2"를 커버한다 — 둘 다 커버됨.
            var stepsJsonFullCoverage = @"```json
{
  ""Steps"": [
    { ""Code"": ""S01"", ""Name"": ""첫 단계"", ""LegacyProcedures"": [""USP_Spec1""], ""TargetTables"": [""dbo.T1""], ""ErrorCodes"": [""-1""] },
    { ""Code"": ""S02"", ""Name"": ""둘째 단계"", ""LegacyProcedures"": [""USP_Spec2""], ""TargetTables"": [""dbo.T2""], ""ErrorCodes"": [""-2""] }
  ]
}
```";
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + stepsJsonFullCoverage });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var dbService = Substitute.For<IDbMetadataService>();
            var validator = new MechanicalValidator();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                dbService, aiService, validator, userInteraction, "2", "gpt-4", null,
                aiService, aiService, "high", "high", "default", 8);
            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1"), ("dbo.USP_Spec2", "content2") };

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 부재를 확인하는 테스트가 존재를 확인하는 테스트만큼 중요하다 —
            // 그래야 조건이 뒤집혀 배너가 늘 붙는 사고를 잡는다.
            Assert.DoesNotContain("[커버리지 누락]", result.Plan);
        }

        /// <summary>
        /// Feedback_Log.txt 제외 픽스: 오케스트레이터는 재시도 회차마다 원본
        /// specs의 작업 사본(specsCopy)에 "Feedback_Log.txt"를 덧붙여 AI 호출에
        /// 넘긴다. 커버리지 검사가 그 사본과 대조하면 이 항목이 목차의 어느
        /// LegacyProcedures에도 없으니 매 재시도 회차마다 "커버되지 않음"으로
        /// 잘못 보고된다. 검사는 반드시 원본 specs 인자와 대조해야 한다.
        ///
        /// 1회차를 결함으로 실패시켜 feedbackLog를 만들고, 2회차의 골격 호출에
        /// 실제로 Feedback_Log.txt가 섞여 들어갔는지 먼저 확인해(안 그러면 아래
        /// 부재 단언이 이 경로를 지나지 않고 우연히 통과했을 수 있다) 그 경로가
        /// 정말 실행됐음을 증명한 뒤, 최종 문서에 그 파일명이 커버리지 누락으로
        /// 보고되지 않았음을 확인한다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_AfterFeedbackRetry_DoesNotReportFeedbackLogAsUncoveredProcedure()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 1회차는 결함으로 실패시켜 feedbackLog를 만든다. 2회차는 점수가
            // 올라 통과한다.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => reviewCall++ == 0
                    ? new ReviewResult { HasDefects = true, FeedbackComment = "결함", ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6 }
                    : new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var result = await RunBatchPipeline(aiService);

            Assert.Equal(VerificationOutcome.Passed, result.Outcome);

            // 이 테스트가 실제로 노리는 경로를 먼저 확인한다: 2회차 골격 호출이
            // 진짜로 Feedback_Log.txt를 포함한 specsCopy를 받았는가.
            await aiService.Received(1).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Is<List<(string, string)>>(specs => specs.Any(s => s.Item1 == "Feedback_Log.txt")),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

            // 핵심 불변식: Feedback_Log.txt는 프로시저 명세서가 아니므로 커버리지
            // 검사 대상이 아니다.
            Assert.DoesNotContain("Feedback_Log.txt", result.Plan!);
            Assert.DoesNotContain("[커버리지 누락]", result.Plan!);
        }

        /// <summary>
        /// 커버리지 누락 수명주기: stepFloorViolations는 실제 생성 품질(회차마다
        /// 달라짐)에 좌우되므로 bestAttemptStepFloorViolations라는 스냅샷을 따로
        /// 둔다. uncoveredProcedures는 다르다 — 목차(LegacyProcedures)와 불변
        /// 인자 specs에만 좌우되고 "어느 회차가 무엇을 생성했는가"와는 무관하므로,
        /// 루프 종료 후 currentPlanStructure에서 매번 새로 파싱해도 항상 옳다.
        /// 이 테스트가 그 결정을 검증한다.
        ///
        /// 1회차(목차 A, S01이 USP_Spec1을 커버)가 최고점 후보가 된다. 2회차(같은
        /// 목차 A, 점수가 오르지 못함)가 목차 재수립을 유발해 목차 B(T01, USP_Spec1을
        /// 커버하지 않음 — 의도적 커버리지 공백)로 바뀐다. 3회차(목차 B)도 결함이
        /// 있고 재시도 예산이 소진되어, 구제(RetryRescue)가 여전히 최고점인
        /// 1회차(목차 A, 완전히 커버됨)를 채택한다.
        ///
        /// 배선이 옳다면: 채택 문서를 실제로 만든 목차는 A이므로 최종 문서에는
        /// 커버리지 누락 배너가 전혀 없어야 한다. 배선에 결함이 있어(예: 재수립
        /// 도중의 목차 B를 잘못 기억하거나 캐시된 값을 그대로 쓰면) 채택되지도
        /// 않은 목차 B의 커버리지 공백이 A의 채택 문서 위에 잘못 보고될 수 있다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipeline_WhenRescueAdoptsEarlierOutline_CoverageBannerDescribesTheAdoptedOutlineNotTheRedraftedOne()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });

            // 목차 B(재수립본)는 T01 하나뿐이고, USP_Spec1이 아닌 다른 프로시저를
            // 다룬다는 설정으로 LegacyProcedures를 선언하지 않는다 — 의도적
            // 커버리지 공백.
            const string outlineBJson = @"```json
{
  ""Steps"": [
    { ""Code"": ""T01"", ""Name"": ""새 단계"", ""LegacyProcedures"": [""OTHER_PROC""], ""TargetTables"": [""dbo.T1""], ""ErrorCodes"": [""-1""] }
  ]
}
```";
            // 최초 목차(A: StepsJson, USP_Spec1을 S01이 커버), 재수립 목차(B: 커버 공백).
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    new AiResult { Content = "## 목차\n" + StepsJson },
                    new AiResult { Content = "## 목차\n" + outlineBJson });
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var steps = call.Arg<IReadOnlyList<BatchStepPlan>>();
                    return new AiResult { Content = SkeletonMarkdownFor(steps.Select(s => s.Code).ToArray()) };
                });
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 1회차(목차 A): 결함은 있지만 최고점(8점대)으로 기록된다.
            // 2회차(목차 A): 점수가 못 올라(5점대) 목차 재수립이 발동한다.
            // 3회차(목차 B): 여전히 결함이 있고(4점대) 재시도 예산이 소진돼
            // 구제가 1회차(목차 A)를 채택한다.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var call = reviewCall++;
                    return call switch
                    {
                        0 => new ReviewResult { HasDefects = true, FeedbackComment = "구조 결함", ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 8, ScoreException = 8, ScoreReadability = 8 },
                        1 => new ReviewResult { HasDefects = true, FeedbackComment = "여전히 결함", ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5, ScoreException = 5, ScoreReadability = 5 },
                        _ => new ReviewResult { HasDefects = true, FeedbackComment = "목차 B도 결함", ScoreAccuracy = 4, ScoreCrud = 4, ScoreInterface = 4, ScoreException = 4, ScoreReadability = 4 },
                    };
                });

            var result = await RunBatchPipeline(aiService);

            // 실제로 구제가 발동해 목차 A(1회차)를 채택했는지 먼저 확인한다.
            Assert.Equal(VerificationOutcome.QualityRejected, result.Outcome);
            Assert.Contains("S01", result.Plan);

            // 핵심 불변식: 채택된 문서는 목차 A(완전히 커버됨)로 만들어졌으므로,
            // 채택되지도 않은 목차 B의 커버리지 공백이 배너로 새어 나오면 안 된다.
            Assert.DoesNotContain("[커버리지 누락]", result.Plan);
        }

        // 구제(RetryRescue)가 이전 회차를 채택하면 반환되는 AiResult도 그 회차의
        // 것이어야 한다 — BestAttempt가 후보 등록 시점에 finalAiResult를 함께
        // 스냅샷하므로, 마지막으로 생성된 회차가 아니라 채택된 회차의 AiResult가
        // 나온다. 주의: 이 테스트는 lastSkeleton·lastSkeletonResult·lastStepSections
        // 캐시가 채택 회차로 되감기는지는 검증하지 않는다 — 그 값들은 오늘 아무
        // 데도 읽히지 않는 지역 변수라 관찰할 방법이 없다(AdoptedGenerationState
        // 선언부의 설명 참고).
        [Fact]
        public async Task RunConsolidatedPipeline_WhenRescueAdoptsEarlierAttempt_ReportsThatAttemptsAiResult()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });

            // 1차 골격과 3차 골격을 구분 가능하게 만든다.
            var skeletonCall = 0;
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => new AiResult
                {
                    Content = SkeletonMarkdown,
                    SystemPrompt = $"골격 시스템 프롬프트 #{++skeletonCall}"
                });

            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 1차는 통과 점수, 2차는 더 낮은 점수(재수립 유발), 3차는 결함 →
            // 예산 소진 시 RetryRescue가 최고점인 1차를 채택한다.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => ++reviewCall switch
                {
                    1 => new ReviewResult { HasDefects = true, FeedbackComment = "보완", ScoreAccuracy = 7, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 },
                    _ => new ReviewResult { HasDefects = true, FeedbackComment = "여전히 보완", ScoreAccuracy = 6, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 }
                });

            var result = await RunBatchPipeline(aiService);

            // 채택된 것은 1차이므로 반환되는 AiResult도 1차 골격 호출의 것이어야 한다.
            Assert.NotNull(result.Result);
            Assert.Equal("골격 시스템 프롬프트 #1", result.Result!.SystemPrompt);
        }

        // L3 피드백이 통짜 단일 호출로 가면 분할이 확보한 단계 본문이 무너진다.
        // 지목이 있으면 그 단계만, 골격은 재사용해야 한다.
        [Fact]
        public async Task RunConsolidatedPipeline_L3FeedbackWithTargetedSteps_RegeneratesOnlyThoseSteps()
        {
            var aiService = SplitCapableAiService();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);

            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "S02의 트랜잭션 경계를 명시해줘",
                        RedraftStructure = false,
                        TargetStepCodes = new List<string> { "S02" }
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            var result = await RunBatchPipelineWithUi(aiService, userInteraction, isBatchMode: false);

            Assert.NotNull(result.Plan);
            // 통짜 호출은 한 번도 일어나지 않아야 한다.
            await aiService.DidNotReceive().GenerateConsolidatedBatchPlanAsync(
                Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            // 골격은 최초 1회뿐 — 지목 재생성은 재사용한다.
            await aiService.Received(1).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            // S02는 최초 1회 + 피드백 1회 = 2회, S01은 1회.
            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S02"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await aiService.Received(1).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S01"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_L3FeedbackWithNoTargets_RegeneratesEveryStepButReusesSkeleton()
        {
            var aiService = SplitCapableAiService();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);

            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "전반적으로 트랜잭션 서술을 강화해줘",
                        RedraftStructure = false
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            await RunBatchPipelineWithUi(aiService, userInteraction, isBatchMode: false);

            await aiService.Received(1).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await aiService.Received(2).GenerateBatchStepSectionAsync(
                Arg.Is<BatchStepPlan>(s => s.Code == "S01"), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(),
                Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RunConsolidatedPipeline_L3FeedbackWithSkeletonSelected_RegeneratesSkeletonAndEveryStep()
        {
            var aiService = SplitCapableAiService();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);

            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "검증 SQL 세트에 제어합계를 추가해줘",
                        RedraftStructure = false,
                        RegenerateSkeleton = true
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            await RunBatchPipelineWithUi(aiService, userInteraction, isBatchMode: false);

            await aiService.Received(2).GenerateBatchPlanSkeletonAsync(
                Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        // 단계 목록은 사용자에게 전달돼야 고를 수 있다.
        [Fact]
        public async Task RunConsolidatedPipeline_PassesStepListToTheReviewPrompt()
        {
            var aiService = SplitCapableAiService();
            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(new HumanReviewResult { Decision = UserDecision.Approve });

            await RunBatchPipelineWithUi(aiService, userInteraction, isBatchMode: false);

            await userInteraction.Received(1).RequestHumanReviewAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(),
                Arg.Is<bool>(b => b),
                Arg.Is<IReadOnlyList<BatchStepPlan>?>(s => s != null && s.Count == 2));
        }

        // Task 1이 AdoptedGenerationState에 Skeleton·SkeletonResult·StepSections를 묶으면서
        // 남긴 커버리지 공백을 메운다(선언부 주석 참고): 그 세 값은 이 배선(Task 4)이
        // 생기기 전까지는 아무 데도 읽히지 않는 지역 변수라 관찰할 방법이 없었다.
        //
        // 구제(RetryRescue)가 더 이른 회차를 채택하는데, 그 회차 이후에 캐시된
        // lastSkeleton/lastStepSections는 더 나중(폐기된) 회차의 것으로 이미 덮어써져
        // 있다. RestoreAdoptedGenerationState가 그 되감기를 하지 않으면, 이어지는 L3
        // 지목 재생성이 폐기된 회차의 섹션을 "재사용"해 화면에도 없던 본문이 새어
        // 나온다.
        [Fact]
        public async Task RunConsolidatedPipeline_L3FeedbackAfterRescue_ReusesTheAdoptedAttemptsStepSections()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 목차\n" + StepsJson });

            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });

            // 코드별 생성 회차를 센다. 1차 시도는 두 단계 모두 회차 1을 낸다. 정체로
            // 인한 2차 시도는 (지목 없이 전면 재생성이므로) 두 단계 모두 회차 2를
            // 내지만 점수가 더 낮아 채택되지 않는다. 구제는 1차를 채택해야 하므로,
            // 뒤이은 L3 지목 재생성에서 지목되지 않은 S01은 "회차 1"을 그대로 실어야
            // 한다 — 캐시가 되감기지 않으면 "회차 2"가 새어 나온다.
            var sectionCallCounts = new Dictionary<string, int>();
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    sectionCallCounts.TryGetValue(step.Code, out var n);
                    n++;
                    sectionCallCounts[step.Code] = n;
                    return new AiResult
                    {
                        Content = $"### {step.Code} 단계\n\n대상은 {step.TargetTables[0]}이고 오류코드는 {step.ErrorCodes[0]}이다. 생성 회차 {n}.\n\n```sql\nSELECT 1;\n```"
                    };
                });

            // 1차는 결함이 있지만 최고점, 2차는 결함이 있고 더 낮은 점수 → maxL2Attempts="1"
            // (=_maxAttempts 2)이므로 2차에서 예산이 소진돼 RetryRescue가 1차를 채택한다.
            var reviewCall = 0;
            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => ++reviewCall switch
                {
                    1 => new ReviewResult { HasDefects = true, FeedbackComment = "보완", ScoreAccuracy = 9, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 9, ScoreReadability = 9 },
                    _ => new ReviewResult { HasDefects = true, FeedbackComment = "여전히 보완", ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5, ScoreException = 5, ScoreReadability = 5 }
                });

            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);
            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "S02만 보완해줘",
                        RedraftStructure = false,
                        TargetStepCodes = new List<string> { "S02" }
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(), userInteraction,
                "1", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };
            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "RescueThenL3Job", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            Assert.NotNull(result.Plan);
            // 지목되지 않은 S01은 채택(1차)의 섹션을 그대로 실어야 한다.
            Assert.Contains("### S01 단계", result.Plan);
            Assert.Contains("생성 회차 1.", result.Plan);
            // 폐기된 2차 회차의 내용은 어디에도 새어 나오면 안 된다.
            Assert.DoesNotContain("생성 회차 2.", result.Plan);
            // 지목된 S02는 L3에서 다시 만들어져 3번째 회차 표식을 실어야 한다.
            Assert.Contains("생성 회차 3.", result.Plan);
        }

        // L3에서 구조 재수립(RedraftStructure=true)에 응하면 단계 코드 자체가
        // 바뀔 수 있다. 재수립 전 목차에서 하한 미달로 기록된 단계 코드가 새
        // 목차에는 없는데도 그 기록이 살아남아 배너로 새어 나오면, 사용자는
        // 문서 어디에도 없는 단계를 찾아 헤매게 된다. 재시도 루프의
        // ClearSplitGenerationCacheAfterRedraft가 막아 둔 것과 같은 부류의
        // 결함을 L3 재수립 경로에서도 막는다.
        [Fact]
        public async Task RunConsolidatedPipeline_L3StructureRedraft_DropsFloorViolationsFromTheOldOutline()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });

            // 재수립 후 목차는 S01/S02 대신 완전히 다른 코드 S05를 낸다.
            const string redraftedStepsJson = @"```json
{
  ""Steps"": [
    { ""Code"": ""S05"", ""Name"": ""재설계 단계"", ""LegacyProcedures"": [""USP_Spec1""], ""TargetTables"": [""dbo.T5""], ""ErrorCodes"": [""-5""] }
  ]
}
```";
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    new AiResult { Content = "## 목차\n" + StepsJson },
                    new AiResult { Content = "## 재설계 목차\n" + redraftedStepsJson });

            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });

            // S01은 대상 테이블·오류코드를 일부러 빠뜨려 하한 검사를 통과하지
            // 못하게 한다 — 최초 문서에 "S01 (하한 미달)" 기록을 남기기 위해서다.
            // S02와 재수립 후의 S05는 정상적으로 하한을 통과한다.
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    if (step.Code == "S01")
                    {
                        return new AiResult { Content = "### S01 단계\n\n본문.\n\n```sql\nSELECT 1;\n```" };
                    }
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);
            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "구조를 다시 짜줘",
                        RedraftStructure = true
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(), userInteraction,
                "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };
            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "RedraftDropsFloorJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            Assert.NotNull(result.Plan);
            // 새 목차(S05)의 문서에는 하한 미달 배너 자체가 없어야 한다 — S01의
            // 옛 기록이 새어 나오면 여기서 잡힌다.
            Assert.DoesNotContain("하한 미달", result.Plan);
            Assert.DoesNotContain("S01", result.Plan);
            Assert.Contains("### S05 단계", result.Plan);
        }

        // Finding 2 회귀: 재수립 이후 다음 회차의 RequestHumanReviewAsync가 새 목차의
        // 단계 코드를 받아야 한다. adoptedSteps를 그림자 지역 변수로 덮으면(수정 전
        // 코드가 그랬듯) 다음 회차의 다중 선택 목록이 옛 코드(S01/S02)를 계속
        // 보여준다 — 리뷰어가 더 이상 존재하지 않는 코드를 골라도 pending 필터가
        // 조용히 빈 결과를 내고 어떤 AI 호출도 일어나지 않는데, rePlan은 비어 있지
        // 않으므로 빈-가드도 발동하지 않아 아무 메시지 없이 같은 문서를 다시 보여준다.
        [Fact]
        public async Task RunConsolidatedPipeline_L3SecondRoundAfterRedraft_OffersTheNewStepCodes()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });

            // 재수립 후 목차는 S01/S02 대신 완전히 다른 코드 S05를 낸다.
            const string redraftedStepsJson = @"```json
{
  ""Steps"": [
    { ""Code"": ""S05"", ""Name"": ""재설계 단계"", ""LegacyProcedures"": [""USP_Spec1""], ""TargetTables"": [""dbo.T5""], ""ErrorCodes"": [""-5""] }
  ]
}
```";
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    new AiResult { Content = "## 목차\n" + StepsJson },
                    new AiResult { Content = "## 재설계 목차\n" + redraftedStepsJson });

            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });

            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);

            var reviewCount = 0;
            IReadOnlyList<BatchStepPlan>? secondRoundSteps = null;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(call =>
                {
                    reviewCount++;
                    if (reviewCount == 2)
                    {
                        secondRoundSteps = call.ArgAt<IReadOnlyList<BatchStepPlan>?>(4);
                    }
                    return reviewCount == 1
                        ? new HumanReviewResult { Decision = UserDecision.ProvideFeedback, UserFeedback = "구조를 다시 짜줘", RedraftStructure = true }
                        : new HumanReviewResult { Decision = UserDecision.Approve };
                });

            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(), userInteraction,
                "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };
            await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "L3SecondRoundOffersNewCodesJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            Assert.NotNull(secondRoundSteps);
            Assert.Contains(secondRoundSteps!, s => s.Code == "S05");
            Assert.DoesNotContain(secondRoundSteps!, s => s.Code == "S01");
            Assert.DoesNotContain(secondRoundSteps!, s => s.Code == "S02");
        }

        // Finding 3 회귀: 분할 경로에서는 통짜 재작성으로 L1을 되살리는 자가 수정을
        // 일부러 건너뛴다(:2248 `stepsForRegeneration == null` 가드) — 그 재작성이
        // 단계별로 확보한 본문을 무너뜨리기 때문이다. 하지만 건너뛴 채로 L1이 여전히
        // 실패 중이면, 그 사실이 승인 화면 어디에도 적히지 않은 채로 문서가 나간다.
        //
        // L1(ValidateConsolidated)은 문서 레벨 필수 헤더 4종만 본다(단계 섹션의 '### '
        // 헤딩은 보지 않는다). 그래서 이 테스트는 L3에서 골격을 지목(RegenerateSkeleton
        // =true)해 골격이 실제로 다시 생성되게 하고, 그 두 번째 골격 호출이 필수 헤더
        // 하나가 빠진 골격을 내도록 만든다 — 조립된 문서가 L1을 어기게 하는 유일한 길이다.
        [Fact]
        public async Task RunConsolidatedPipeline_L3SplitRegenerationStillFailsL1_AttachesL1ExhaustedBanner()
        {
            var aiService = SplitCapableAiService();

            // 1차(최초 생성)는 정상 골격, 2차(L3 골격 지목 재생성)는 "통합 데이터
            // 정합성 검증 SQL 세트" 헤더가 통째로 빠진 골격을 낸다.
            var malformedSkeleton = SkeletonMarkdown.Substring(
                0, SkeletonMarkdown.IndexOf("## 통합 데이터 정합성 검증 SQL 세트", StringComparison.Ordinal));
            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                    new AiResult { Content = SkeletonMarkdown },
                    new AiResult { Content = malformedSkeleton });

            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);

            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "검증 SQL 세트를 보완해줘",
                        RedraftStructure = false,
                        RegenerateSkeleton = true
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            var result = await RunBatchPipelineWithUi(aiService, userInteraction, isBatchMode: false);

            Assert.NotNull(result.Plan);
            Assert.Contains("L1 기계 검증을 통과하지 못했습니다", result.Plan);
            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
        }

        // Finding 4 회귀: 구조 재수립이 성공했지만 새 목차가 단계 목록을 파싱하지
        // 못해(산문만 있는 목차 등) 통짜 단일 호출로 폴백하는 경우, reViolations가
        // 옛 목차의 살아있는 stepFloorViolations를 그대로 물려받으면 안 된다. 그대로
        // 두면 배너가 새 문서에 없는 옛 단계 코드(S01)를 지목한다.
        [Fact]
        public async Task RunConsolidatedPipeline_L3RedraftFallsBackToSingleCall_DropsStaleFloorViolations()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm" });

            // 최초 목차는 파싱 가능(S01/S02). 재수립 후 목차는 산문만 있어 단계
            // 목록을 파싱할 수 없다 — 통짜 단일 호출 폴백을 강제한다.
            aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    new AiResult { Content = "## 목차\n" + StepsJson },
                    new AiResult { Content = "## 재설계 목차 산문만 있다" });

            aiService.GenerateBatchPlanSkeletonAsync(Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = SkeletonMarkdown });

            // S01은 대상 테이블·오류코드를 빠뜨려 하한 검사를 통과하지 못하게 한다 —
            // 최초 문서에 "S01 (하한 미달)" 기록을 남기기 위해서다. S02는 정상.
            aiService.GenerateBatchStepSectionAsync(Arg.Any<BatchStepPlan>(), Arg.Any<IReadOnlyList<BatchStepPlan>>(), Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var step = call.Arg<BatchStepPlan>();
                    if (step.Code == "S01")
                    {
                        return new AiResult { Content = "### S01 단계\n\n본문.\n\n```sql\nSELECT 1;\n```" };
                    }
                    return new AiResult { Content = HealthyStepSection(step.Code, step.TargetTables[0], step.ErrorCodes[0]) };
                });

            // 통짜 재생성 호출(폴백)이 반환하는 본문에는 S01/S02 어느 코드도
            // 등장하지 않는다 — 완전히 새로 쓴 문서라는 뜻이다.
            aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "## 재작성된 통짜 문서\n\n본문에는 옛 코드가 없다." });

            aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewResult { HasDefects = false, ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10 });

            var userInteraction = Substitute.For<IVerificationUserInteraction>();
            userInteraction.CreateProgressScope(Arg.Any<string>()).Returns((IMultiProgressScope?)null);
            var reviewCount = 0;
            userInteraction.RequestHumanReviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<VerificationOutcome>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<BatchStepPlan>?>())
                .Returns(_ => ++reviewCount == 1
                    ? new HumanReviewResult
                    {
                        Decision = UserDecision.ProvideFeedback,
                        UserFeedback = "구조를 다시 짜줘",
                        RedraftStructure = true
                    }
                    : new HumanReviewResult { Decision = UserDecision.Approve });

            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(), userInteraction,
                "2", "gpt-4", null, aiService, aiService, "high", "high", "default", 8);

            var specs = new List<(string, string)> { ("dbo.USP_Spec1", "content1") };
            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "RedraftFallbackDropsFloorJob", "OpenAI", _consolidatedOutputRoot, isBatchMode: false);

            Assert.NotNull(result.Plan);
            // 새 통짜 문서에는 하한 미달 배너 자체가 없어야 한다 — 옛 S01의
            // 기록이 새어 나오면 여기서 잡힌다.
            Assert.DoesNotContain("하한 미달", result.Plan);
            Assert.DoesNotContain("S01", result.Plan);
        }
    }
}
