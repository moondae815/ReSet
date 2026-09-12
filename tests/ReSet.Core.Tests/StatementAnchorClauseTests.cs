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
    /// <summary>
    /// U-앵커 계약의 <b>표기</b>를 잠근다 - 「각 DML 문장에 앵커를 달아라」고 요구하면서
    /// 인정 표기를 UPDATE 용어로만 정의하면, 모델이 INSERT·DELETE 표기를 회차마다
    /// 발명한다. 실측 오답 넷:
    ///
    /// <list type="bullet">
    /// <item><c>Batch1</c> `-- SQL_INSERT1 … /* INSERT1: … */`</item>
    /// <item><c>Batch5/S13</c> `/* U13-DELETE 1: … */` → 리더가 <b>13</b> 으로 읽었다</item>
    /// <item><c>Batch6/S09</c> `/* U22: DELETE 1 … */` → <b>원본 라인 번호</b></item>
    /// <item><c>Batch6/S13</c> `/* U1: … (DELETE 1) */` → <b>단계 자체 순번</b></item>
    /// </list>
    ///
    /// [해가 왜 조용한가] 서수가 틀리면 명세서에 그 <c>(Kind,서수)</c> 행이 없어
    /// 앵커 계열 검사(B·C·D)가 대조할 행을 못 찾고 <b>그냥 지나간다.</b> 발화도 오류도
    /// 없이 커버리지가 사라지고 <b>앵커가 있어 보이기까지 한다.</b>
    /// 그 침묵을 재는 자는 <see cref="AnchorKindOrdinalPairTests"/> 이고, 이 시험은
    /// 그 자가 재는 대상을 <b>프롬프트 층에서</b> 잠근다.
    ///
    /// [왜 두 경로 다 보는가] 조항은 <c>AppendStatementAnchorRules</c> 한 곳에 있고
    /// 분할 생성(<c>GenerateBatchStepSectionAsync</c>)과 단일 호출 폴백
    /// (<c>GenerateConsolidatedBatchPlanAsync</c>)이 함께 쓴다. 「한 곳에 있으니 둘 다
    /// 실린다」는 <b>읽어서 아는 사실이었다</b> - 호출 하나가 빠져도 시험이 없었다.
    /// 여기서는 두 프롬프트를 각각 잡아 <b>둘 다</b> 단언한다.
    ///
    /// 설계: docs/superpowers/specs/2026-09-08-U앵커-계약-표기-설계.md
    /// </summary>
    public class StatementAnchorClauseTests
    {
        private const string PlanStructure = @"## 목차
```json
{ ""Steps"": [
  { ""Code"": ""S01"", ""Name"": ""날짜 검증"", ""LegacyProcedures"": [""dbo.UP_A""] },
  { ""Code"": ""S02"", ""Name"": ""정산 원장"", ""LegacyProcedures"": [""dbo.UP_B""] }
] }
```";

        private static IAiService NewService()
        {
            var client = Substitute.For<IAiClient>();
            client.ProviderName.Returns("OpenAI");
            client.ModelName.Returns("gpt-test");
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "ok" });

            return new AiService(client, 0.2f);
        }

        private static List<(string FileName, string Content)> Specs() =>
            new() { ("dbo.UP_A", "본문") };

        /// <summary>단일 호출 폴백이 실제로 보내는 사용자 프롬프트.</summary>
        private static async Task<string> FallbackPromptAsync()
        {
            var result = await NewService().GenerateConsolidatedBatchPlanAsync(
                PlanStructure, Specs(), "C#", "Job_Test", effort: null, stepInterfaces: null);
            return result.UserPrompt ?? string.Empty;
        }

        /// <summary>분할 생성이 단계 하나를 만들 때 실제로 보내는 사용자 프롬프트.</summary>
        private static async Task<string> StepSectionPromptAsync()
        {
            var steps = BatchStepPlanParser.TryParse(PlanStructure);
            Assert.NotNull(steps);

            var result = await NewService().GenerateBatchStepSectionAsync(
                steps![0], steps, "공통 규약", Specs(),
                Array.Empty<StepInterface>(), "C#", "Job_Test");
            return result.UserPrompt ?? string.Empty;
        }

        /// <summary>
        /// 두 경로의 프롬프트를 함께 돌려준다. 단언은 <b>둘 다</b>에 걸어야 한다 -
        /// 한쪽만 보면 다른 경로에서 조항이 빠져도 초록이다.
        /// </summary>
        private static async Task<IReadOnlyList<(string Path, string Prompt)>> BothPromptsAsync() =>
            new[]
            {
                ("분할 생성(GenerateBatchStepSectionAsync)", await StepSectionPromptAsync()),
                ("단일 호출 폴백(GenerateConsolidatedBatchPlanAsync)", await FallbackPromptAsync())
            };

        private static async Task AssertBothCarryAsync(string fragment, string why)
        {
            foreach (var (path, prompt) in await BothPromptsAsync())
            {
                Assert.True(
                    prompt.Contains(fragment, StringComparison.Ordinal),
                    $"{path} 프롬프트에 「{fragment}」가 없습니다. {why}");
            }
        }

        [Fact]
        public async Task AnchorClause_DefinesInsertNotation()
        {
            // 뿌리. INSERT 표기가 없어 Batch1 이 `/* INSERT1: … */` 를 발명했다.
            await AssertBothCarryAsync(
                "/* INSERT 1:",
                "INSERT 표기를 정의하지 않으면 모델이 회차마다 발명합니다.");
        }

        [Fact]
        public async Task AnchorClause_DefinesDeleteNotation()
        {
            // 뿌리. DELETE 표기가 없어 Batch5·Batch6 이 셋으로 갈라졌다.
            await AssertBothCarryAsync(
                "/* DELETE 1:",
                "DELETE 표기를 정의하지 않으면 모델이 회차마다 발명합니다.");
        }

        [Fact]
        public async Task AnchorClause_RequiresTheSpecTableOrdinal()
        {
            // Batch6/S09 의 `U22`(원본 라인) · Batch6/S13 의 `U1`(자체 순번)을 막는다.
            await AssertBothCarryAsync(
                "명세서 표의 서수를 그대로",
                "어느 번호를 쓰라는 말이 없으면 모델이 원본 라인 번호나 자체 순번을 씁니다.");
        }

        [Fact]
        public async Task AnchorClause_AllowsTheSameOrdinalAcrossKinds()
        {
            // 서수 충돌을 피하려 번호를 옮기는 것을 막는다 - Batch6/S01 의 다섯 건이
            // 종류를 넘어 6·7·8·9·10 으로 이어 붙인 자체 순번이었다.
            await AssertBothCarryAsync(
                "종류가 다르면 서수가 겹쳐도 됩니다",
                "겹침이 허용된다고 말하지 않으면 모델이 번호를 옮겨 서수를 어긋냅니다.");
        }

        [Fact]
        public async Task AnchorClause_ForbidsCompoundLabels()
        {
            // Batch5/S13 의 `/* U13-DELETE 1: … */` 를 막는다 - 리더가 U13 을 먼저 집었다.
            await AssertBothCarryAsync(
                "`U13-DELETE 1` 같은 복합 라벨은 쓰지 마십시오",
                "복합 라벨을 금지하지 않으면 리더가 앞쪽 번호를 집어 오귀속합니다.");
        }

        [Fact]
        public async Task AnchorClause_DefinesSelectNotation()
        {
            // 명세서 DML 범위 표(ReadDmlRows 가 읽는 기계 확정 표)는 SELECT 행을 10 개
            // (SP 다섯: PROC_ETC 6 · INS_EXTRA 1 · Summary_AcqManual 1 · SUMMARY_ETC 1 ·
            // SUMMARY_EXTRA 1) 갖는데 계약이 그 표기를 정하지 않았다(파일 전체를 grep
            // 하면 80 - 집합 술어 표 등 표 밖의 다른 셀도 걸린다. ReadDmlRows 가 보는
            // 것은 표 하나뿐이다). 모델은 이미 SELECT 앵커를 적고 있지만(코퍼스 16 —
            // `/* SELECT n: … */` 8 · `-- SELECT n: … ` 8, 전부 명세서와 일치) 계약이
            // 없으면 다음 회차에 다른 표기가 나온다.
            // 재는 자: SelectAnchorPairCorpusTests
            await AssertBothCarryAsync(
                "/* SELECT 1:",
                "SELECT 표기를 정의하지 않으면 모델이 회차마다 발명합니다.");
        }

        [Fact]
        public async Task AnchorClause_IsIdenticalOnBothPaths()
        {
            // 두 벌로 적히면 한쪽만 고쳐져 경로에 따라 다른 문서가 나온다.
            var prompts = await BothPromptsAsync();
            var sections = new List<string>();
            foreach (var (path, prompt) in prompts)
            {
                var section = AnchorSection(prompt);
                Assert.False(
                    string.IsNullOrWhiteSpace(section),
                    $"{path} 프롬프트에 앵커 절이 아예 없습니다.");
                sections.Add(section);
            }

            Assert.Equal(sections[0], sections[1]);
        }

        /// <summary>
        /// 「### 문장 앵커와 의미 보존 (필수)」 절만 오려낸다 - 머리글과 그 뒤의 불릿들까지다.
        ///
        /// [다음 「#」까지로 자르지 않는 이유] 두 경로는 앵커 절 <b>다음</b>에 오는 것이
        /// 서로 다르다(분할 생성은 머리글 없는 지시문, 폴백은 `[Approved Document St…]`).
        /// 그걸로 자르면 조항이 한 글자도 안 다른데도 빨개진다 - 실측으로 겪었다.
        /// </summary>
        private static string AnchorSection(string prompt)
        {
            const string header = "### 문장 앵커와 의미 보존 (필수)";
            var start = prompt.IndexOf(header, StringComparison.Ordinal);
            if (start < 0) return string.Empty;

            var kept = new List<string>();
            foreach (var line in prompt[start..].Split('\n'))
            {
                if (kept.Count > 0 && line.Trim().Length > 0 && !line.StartsWith("- ", StringComparison.Ordinal))
                {
                    break;
                }

                kept.Add(line.TrimEnd('\r'));
            }

            return string.Join("\n", kept).TrimEnd();
        }
    }
}
