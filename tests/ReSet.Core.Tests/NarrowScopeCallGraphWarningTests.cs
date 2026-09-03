using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NSubstitute;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Narrow 모드에서 호출 그래프가 비면 실행당 한 번 알린다.
    ///
    /// [왜 알려야 하는가] Narrow 모드의 요점은 「자기 프로시저 + 1-hop 이웃」이고,
    /// <see cref="PromptContextScope.NarrowSpecs"/>가 적은 대로 이웃을 빼면 이 유형의
    /// 결함이 <b>오히려 늘어난다</b> - 실측 「필수 수정 1·2」가 정확히 그 관계였다
    /// (S13/S12가 S11 명세가 규정한 오류 코드를 지켜야 했다). 그런데 호출 그래프가
    /// 비면 이웃이 0이 되어 각 단계가 자기 명세서만 받는데, 지금은 그 축소가 <b>아무
    /// 신호도 없이</b> 일어난다. Narrow는 CLI 제공자의 기본값이라 이것이 흔한 경로다.
    ///
    /// [왜 값을 뒤집지 않고 알리기만 하는가] 빈 그래프는 결함일 수도 있고 사실일 수도
    /// 있다 - <see cref="StepInterfaceFacts.BuildCallGraph"/>는 어느 프로시저도 다른
    /// 코드 객체를 부르지 않으면 정당하게 빈 사전을 낸다. 기계가 둘을 가를 수 없으므로
    /// 사람에게 넘긴다. 같은 자리의 StepConcurrency 경고가 이미 그 관례다 - "사용자가
    /// 명시한 설정을 말없이 무시하는 것보다 이유를 말하고 그대로 두는 편이 정직하다".
    /// </summary>
    public class NarrowScopeCallGraphWarningTests : IDisposable
    {
        private readonly string _outputRoot =
            Path.Combine(Path.GetTempPath(), "reset-narrow-warning-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_outputRoot)) Directory.Delete(_outputRoot, true);
            GC.SuppressFinalize(this);
        }

        /// <summary>경고 문구를 가려내는 판정식. 문구가 바뀌어도 이 한 자리만 고친다.</summary>
        private static bool IsTheWarning(string message) =>
            message.Contains("1-hop") && message.Contains("Narrow");

        private static SpDefinition Caller()
        {
            var def = new SpDefinition { Schema = "dbo", Name = "UP_UTIL_SETTLE_SUMMARY" };
            def.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo", Name = "UP_UTIL_SETTLE_SUMMARY_EXTRA", Type = "SQL_STORED_PROCEDURE"
            });
            return def;
        }

        private static SpDefinition Loner()
        {
            var def = new SpDefinition { Schema = "dbo", Name = "UP_UTIL_SETTLE_INS" };
            def.Dependencies.Add(new DependencyInfo { Schema = "dbo", Name = "TSettleMst", Type = "USER_TABLE" });
            return def;
        }

        /// <summary>
        /// 파이프라인을 한 번 돌리고 <c>NotifyStatus</c>를 받은 대역을 돌려준다.
        /// 경고 자리는 AI 호출보다 앞이므로 대역이 아무것도 못 만들어도 상관없다 -
        /// 이 테스트가 재는 것은 산출물이 아니라 그 한 줄이다.
        /// </summary>
        private async Task<IVerificationUserInteraction> RunAsync(
            ContextScopeMode scope, IReadOnlyList<SpDefinition>? definitions)
        {
            var aiService = Substitute.For<IAiService>();
            aiService.ProviderName.Returns("claude-cli");
            aiService.ContextScope.Returns(scope);

            var ui = Substitute.For<IVerificationUserInteraction>();
            var orchestrator = new VerificationPipelineOrchestrator(
                Substitute.For<IDbMetadataService>(), aiService, new MechanicalValidator(), ui,
                "0", "gpt-4", null, aiService, aiService, "high", "high", "default", 8,
                stepConcurrency: 1, maxL1RepairAttempts: 2);

            await orchestrator.RunConsolidatedPipelineAsync(
                new List<(string, string)> { ("dbo.UP_UTIL_SETTLE_INS", "명세서 본문") },
                "C#", "Job_Test", "OpenAI", _outputRoot, isBatchMode: true, definitions: definitions);

            return ui;
        }

        [Fact]
        public async Task NarrowScope_WithAnEmptyCallGraph_WarnsOncePerRun()
        {
            var ui = await RunAsync(ContextScopeMode.Narrow, new[] { Loner() });

            ui.Received(1).NotifyStatus(Arg.Is<string>(s => IsTheWarning(s)));
        }

        /// <summary>
        /// definitions 자체가 없으면 그래프도 비지만, 그때는 이웃 손실이 아니라 재료
        /// 부재이므로 같은 경고가 정당하다 - 사람이 확인해야 하는 사실은 같다.
        /// </summary>
        [Fact]
        public async Task NarrowScope_WithoutDefinitions_WarnsToo()
        {
            var ui = await RunAsync(ContextScopeMode.Narrow, null);

            ui.Received(1).NotifyStatus(Arg.Is<string>(s => IsTheWarning(s)));
        }

        [Fact]
        public async Task NarrowScope_WithANonEmptyCallGraph_StaysSilent()
        {
            var ui = await RunAsync(ContextScopeMode.Narrow, new[] { Caller() });

            ui.DidNotReceive().NotifyStatus(Arg.Is<string>(s => IsTheWarning(s)));
        }

        /// <summary>
        /// Full 모드는 명세서 전량을 실어 호출 그래프를 아예 쓰지 않는다 - 빈 그래프가
        /// 아무것도 잃게 하지 않으므로 경고는 순수한 소음이다.
        /// </summary>
        [Fact]
        public async Task FullScope_WithAnEmptyCallGraph_StaysSilent()
        {
            var ui = await RunAsync(ContextScopeMode.Full, new[] { Loner() });

            ui.DidNotReceive().NotifyStatus(Arg.Is<string>(s => IsTheWarning(s)));
        }
    }
}
