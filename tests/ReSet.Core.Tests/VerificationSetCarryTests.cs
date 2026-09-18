using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// [검증 세트 재사용] 골격이 이미 쓴 `## 통합 데이터 정합성 검증 SQL 세트` 절이 단계 섹션 요청의
    /// <b>공유 접두사</b>에 실리는가.
    ///
    /// B22 축 B 감사의 도달 1 위 가족이 「검증 SQL 이름 공간이 통째로 둘로 갈렸다」였다. 요청 본문
    /// 실측으로 단계 섹션 요청 20/20 에 세트 본문이 0 건이었고, 골격 응답(정의 24)은 첫 단계 요청보다
    /// 먼저 존재했다 - 재료는 있고 배선이 없었다. 요청 재생 18 호출 $3.38 로 세트 이름 호출
    /// 0/3 → 3/3, 신설 Error 검증 14·8·0 → 0·0·0 을 확인했다.
    /// 선언·판독: docs/audit-reports/2026-09-18-검증세트-단계요청-재생-{사전선언,판독}.md
    ///
    /// 여섯 벌로 잠근다 - ①발화 ②자리(접두사) ③없으면 안 나감 ④골격에는 안 실림 ⑤배선 ⑥경로 분리.
    /// 「발화를 재는 자」와 「배선을 재는 자」는 다른 축이다 - ⑤가 없으면 배선이 끊겨도 ①~④가 초록이다.
    /// </summary>
    public class VerificationSetCarryTests
    {
        private const string BlockHeader = "[Integrity Validation SQL Set — ALREADY WRITTEN in this document]";
        private const string Contract = "[Validation Reuse Contract]";
        private const string SetHeading = "## 통합 데이터 정합성 검증 SQL 세트";
        private const string SetBody = "-- SQL_VALIDATE_RUN_AND_LOCK";

        private const string Skeleton = @"# 통합 계획서

## 통합 배치 아키텍처 개요

개요 본문.

## 단계별 이행 상세 및 의사코드

공통 규약 본문.

## 통합 데이터 정합성 검증 SQL 세트

### V01 실행 제어 무결성

```sql
-- SQL_VALIDATE_RUN_AND_LOCK
SELECT 1;
```

## 부록

부록 본문은 검증 세트가 아니다.
";

        private static readonly BatchStepPlan Step =
            new("S18", "통합 정합성 검증", Array.Empty<string>(), new[] { "batch.BatchValidationIssue" },
                new[] { "-9180" }, false, Array.Empty<string>());

        private static IReadOnlyList<BatchStepPlan> Steps => new[] { Step };
        private static List<(string FileName, string Content)> Specs => new() { ("dbo.UP_Any", "본문") };

        private sealed record Captured(string System, string User, string? Suffix);

        private static (IAiService Service, Func<Captured> Last) Service()
        {
            Captured? captured = null;
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-test");
            client.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string?>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    captured = new Captured((string)call[0], (string)call[1], call[4] as string);
                    return Task.FromResult(new AiResult { Content = "### S18 통합 정합성 검증" });
                });
            return (new AiService(client, 0.2f), () => captured ?? throw new InvalidOperationException("호출되지 않았습니다."));
        }

        private static async Task<Captured> StepSection(string? set)
        {
            var (service, last) = Service();
            await service.GenerateBatchStepSectionAsync(
                Step, Steps, "공통 규약", Specs, Array.Empty<StepInterface>(), "C#", "Job_Test",
                verificationSet: set);
            return last();
        }

        // ---------- ① 재료 자르기 ----------

        [Fact]
        public void ExtractVerificationSet_TakesTheHeadingAndStopsAtTheNextH2()
        {
            var set = BatchPlanAssembler.ExtractVerificationSet(Skeleton);

            // 헤딩 줄을 포함한다 - 실은 것이 문서의 어느 절인지 모델이 알아야 한다.
            Assert.StartsWith(SetHeading, set);
            Assert.Contains(SetBody, set);
            // 다음 H2 에서 끊는다.
            Assert.DoesNotContain("부록 본문은 검증 세트가 아니다", set);
            // 앞 절을 끌어오지 않는다.
            Assert.DoesNotContain("공통 규약 본문", set);
        }

        [Fact]
        public void ExtractVerificationSet_ReturnsEmptyWhenTheSkeletonHasNoSuchSection()
        {
            Assert.Equal(string.Empty, BatchPlanAssembler.ExtractVerificationSet("# 제목\n\n## 개요\n\n본문.\n"));
            Assert.Equal(string.Empty, BatchPlanAssembler.ExtractVerificationSet(null));
        }

        // 골격 프롬프트는 이 H2 를 VERBATIM 으로 쓰라 하지만 모델은 꼬리표를 붙여 쓴다
        // (LocateStepDetailBlock 의 주석이 같은 재발을 적었다). 느슨하게 찾는 것을 잠근다.
        [Fact]
        public void ExtractVerificationSet_FindsTheSectionEvenWhenTheModelAddsASuffixToTheHeading()
        {
            var tagged = Skeleton.Replace(SetHeading, SetHeading + " (V01~V12)");

            var set = BatchPlanAssembler.ExtractVerificationSet(tagged);

            Assert.Contains(SetBody, set);
        }

        // ---------- ② 발화 ----------

        [Fact]
        public async Task StepSectionPrompt_CarriesTheVerificationSetAndTheReuseContract()
        {
            var c = await StepSection(BatchPlanAssembler.ExtractVerificationSet(Skeleton));
            var whole = c.System + c.User + c.Suffix;

            Assert.Contains(BlockHeader, whole);
            Assert.Contains(SetBody, whole);
            Assert.Contains(Contract, whole);
            // 세 조항 전부. 하나만 보면 다른 조항이 사라져도 초록이다.
            Assert.Contains("REFER TO IT BY ITS NAME", whole);
            Assert.Contains("Do NOT define a second check for the same thing", whole);
            Assert.Contains("follow the shared", whole);
        }

        // ---------- ③ 자리 (목차 요구와 반대 축이다) ----------

        [Fact]
        public async Task TheVerificationSetGoesIntoTheSharedPrefixNotTheVolatileSuffix()
        {
            var c = await StepSection(BatchPlanAssembler.ExtractVerificationSet(Skeleton));

            // 전 단계가 같은 값을 받으므로 접두사에 실어야 캐시 쓰기가 1 회로 끝난다.
            // 꼬리에 실으면 단계마다 27KB 를 새로 쓴다.
            Assert.Contains(BlockHeader, c.User);
            Assert.DoesNotContain(BlockHeader, c.Suffix ?? string.Empty);
            Assert.DoesNotContain(SetBody, c.Suffix ?? string.Empty);
        }

        // ---------- ④ 없으면 절 자체가 안 나간다 ----------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n  ")]
        public async Task StepSectionPrompt_OmitsTheBlockEntirelyWhenThereIsNoSet(string? set)
        {
            var c = await StepSection(set);
            var whole = c.System + c.User + c.Suffix;

            // 빈 머리글은 "이 문서에 검증 세트가 있다"는 거짓 전제를 준다.
            Assert.DoesNotContain(BlockHeader, whole);
            Assert.DoesNotContain(Contract, whole);
        }

        // ---------- ⑤ 이 절에 단계별 판정을 넣지 않는다 ----------

        // 「레거시 기원 없음」 금지는 단계마다 갈리는데 이 절은 전 단계가 공유한다. 무조건 실으면
        // 레거시 기원이 있는 단계에 거짓 금지를 싣게 된다 - S14 는 원본이 NOLOCK 을 4 회 쓴다.
        // 판독 부록 B. 그 조항의 거처는 AppendRequirementCoverageContract 다.
        [Fact]
        public async Task TheVerificationSetBlockDoesNotCarryTheNoLegacyOriginProhibition()
        {
            var c = await StepSection(BatchPlanAssembler.ExtractVerificationSet(Skeleton));
            var user = c.User;
            var start = user.IndexOf(BlockHeader, StringComparison.Ordinal);
            Assert.True(start >= 0);
            var block = user[start..];

            Assert.DoesNotContain("NO legacy origin", block);
        }

        // ---------- ⑥ 골격 요청에는 안 실린다 ----------

        // 골격이 이 절을 쓰는 쪽이다. 골격 요청에 실으면 자기가 쓸 것을 자기에게 주는 것이고,
        // 20 단계가 나눠 쓰는 접두사가 아니라 골격 접두사만 늘어난다.
        [Fact]
        public async Task SkeletonPrompt_DoesNotCarryTheVerificationSetBlock()
        {
            var (service, last) = Service();
            await service.GenerateBatchPlanSkeletonAsync(Steps, "{}", Specs, "C#", "Job_Test");
            var c = last();

            Assert.DoesNotContain(BlockHeader, c.System + c.User + c.Suffix);
        }

        // ---------- ⑦ 배선 (발화를 재는 자는 배선이 끊겨도 초록이다) ----------

        [Fact]
        public void OrchestratorSlicesTheSetFromTheSkeletonAndThreadsItToBothCallSites()
        {
            var source = File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));

            // 골격에서 한 번 자르고,
            Assert.Single(Regex.Matches(source, @"BatchPlanAssembler\.ExtractVerificationSet\(\s*skeleton\s*\)"));
            // 재시도 헬퍼로 넘기고, 헬퍼가 AI 호출로 넘긴다 - 자리 둘이다.
            Assert.Equal(2, Regex.Matches(source, @"verificationSet:\s*verificationSet").Count);
            // 빈 것은 null 로 내려보낸다 - 「골격이 그 절을 못 썼다」와 「실었다」를 호출 기록에서 가른다.
            Assert.Single(Regex.Matches(
                source, @"string\.IsNullOrWhiteSpace\(extractedVerificationSet\) \? null : extractedVerificationSet"));
        }
    }
}
