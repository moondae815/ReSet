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
        public async Task 단계_프롬프트는_계약의_표구분자를_그대로_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "수수료율 스냅샷 적재", Sources(), CodeValues());

            Assert.NotNull(result.SystemPrompt);
            Assert.Contains(PolicySectionContract.TableSeparator, result.SystemPrompt);
        }

        [Fact]
        public async Task 단계_프롬프트는_근거_칸의_구분자를_계약에서_읽는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "수수료율 스냅샷 적재", Sources(), CodeValues());

            Assert.NotNull(result.SystemPrompt);
            // 구분자 하나만 재면(" · ") 다른 문맥에서 우연히 같은 글자를 쓴 경우와
            // 계약을 실제로 읽는 경우를 못 가른다 - 프롬프트가 근거 칸 형식을
            // 설명하는 문장 안에서 그 자리에 쓰였는지까지 재야 한다.
            Assert.Contains(
                $"<procedure> {PolicySectionContract.LabelSeparator}## <specification heading>",
                result.SystemPrompt);
        }

        [Fact]
        public async Task 단계_프롬프트는_코드값_없음_표기를_계약에서_읽는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "수수료율 스냅샷 적재", Sources(), CodeValues());

            Assert.NotNull(result.SystemPrompt);
            // NoCodeValue는 "-" 한 글자라 프롬프트 어디서나 우연히 나타난다
            // (예: "S1-01"의 하이픈). 코드값 규칙을 설명하는 문장 안에서 그
            // 자리에 쓰였는지까지 재야 계약을 실제로 읽는지 가려진다.
            Assert.Contains(
                $"MUST be either `{PolicySectionContract.NoCodeValue}` or",
                result.SystemPrompt);
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
        public async Task 넘겨받은_명세서가_프롬프트에_그대로_실린다()
        {
            // 이 테스트는 "넘겨받은 sources가 프롬프트에 실리는지"만 잰다.
            // GeneratePolicyStageAsync는 넘겨받은 sources만 그대로 도므로,
            // 하나만 넘기고 넘기지 않은 문자열의 부재를 확인하는 것은 그
            // 사실의 재확인일 뿐 교차 단계 유출을 막는 방어를 재는 것이 아니다.
            // 단계별로 무엇을 이 메서드에 넘길지 고르는 일(선별)은 호출부의
            // 책임이고, 그 선별은 T10(SettlementPolicyService)이 잠근다.
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
