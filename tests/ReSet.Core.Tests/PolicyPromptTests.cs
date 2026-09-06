using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyPromptTests
    {
        private static (AiService Service, IAiClient Client) Build()
        {
            var client = Substitute.For<IAiClient>();
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    effort: Arg.Any<string?>(), cancellationToken: Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "본문" }));

            // 생성자 인자는 PrdPromptTests.Build()와 같은 순서다 - temperature는 float,
            // contextScope는 string?(설정 값).
            var service = new AiService(client, 0.2f, false, 8, true, null);
            return (service, client);
        }

        private static IReadOnlyList<(string Label, string SpecMarkdown)> Sources() =>
            new[] { ("dbo.UP_A", "## 개요\n\n본문\n") };

        private static IReadOnlyList<CodebookEntry> CodeValues() =>
            new[]
            {
                new CodebookEntry("impaymobile", "PayMethod", new[] { "dbo.UP_A" }, true,
                    new[] { new CodebookMatch("dbo.TCode", new Dictionary<string, string> { ["Name"] = "간편결제" }) }),
                new CodebookEntry("payco", null, new[] { "dbo.UP_A" }, true, Array.Empty<CodebookMatch>()),
            };

        [Fact]
        public async Task 단계_프롬프트는_계약의_표머리를_그대로_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "수수료율 스냅샷 적재", Sources(), CodeValues());

            Assert.NotNull(result.SystemPrompt);
            Assert.Contains(PolicySectionContract.TableHeader, result.SystemPrompt);
        }

        [Fact]
        public async Task 단계_프롬프트는_그_단계의_ID_접두사를_지시한다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                3, "정산 집계", Sources(), CodeValues());

            Assert.NotNull(result.SystemPrompt);
            Assert.Contains(PolicySectionContract.IdPrefixFor(3) + "-", result.SystemPrompt);
        }

        [Fact]
        public async Task 매칭된_코드값은_의미와_함께_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "단계", Sources(), CodeValues());

            Assert.NotNull(result.UserPrompt);
            Assert.Contains("impaymobile", result.UserPrompt);
            Assert.Contains("간편결제", result.UserPrompt);
        }

        [Fact]
        public async Task 미매칭_코드값은_의미_미상으로_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "단계", Sources(), CodeValues());

            Assert.NotNull(result.UserPrompt);
            Assert.Contains("payco", result.UserPrompt);
            Assert.Contains("의미 미상", result.UserPrompt);
        }

        [Fact]
        public async Task 그_단계의_명세서만_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "단계",
                new[] { ("dbo.UP_A", "## 개요\n\nA의 본문\n") },
                CodeValues());

            Assert.NotNull(result.UserPrompt);
            Assert.Contains("A의 본문", result.UserPrompt);
            Assert.DoesNotContain("B의 본문", result.UserPrompt);
        }

        [Fact]
        public async Task 교정_피드백을_주면_프롬프트에_실린다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "단계", Sources(), CodeValues(), attributionFeedback: "S1-01의 인용이 원문에 없습니다");

            Assert.NotNull(result.UserPrompt);
            Assert.Contains("S1-01의 인용이 원문에 없습니다", result.UserPrompt);
        }

        [Fact]
        public async Task 개요_프롬프트는_단계_제목을_전부_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyOverviewAsync(
                new[] { "1. 요율 적재", "2. 원장 적재" }, "조립된 단계 본문");

            Assert.NotNull(result.UserPrompt);
            Assert.Contains("1. 요율 적재", result.UserPrompt);
            Assert.Contains("2. 원장 적재", result.UserPrompt);
        }
    }
}
