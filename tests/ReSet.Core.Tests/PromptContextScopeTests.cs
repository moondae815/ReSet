using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PromptContextScopeTests
    {
        private static BatchStepPlan Step(string code, params string[] procedures) =>
            new(code, $"{code} 단계",
                LegacyProcedures: procedures,
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        private static readonly List<(string FileName, string Content)> AllSpecs = new()
        {
            ("dbo.UP_Util_Settle_Summary", "S11 명세서"),
            ("dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA", "S13 명세서 — 오류 시 4000~4008"),
            ("dbo.UP_UTIL_SETTLE_INS", "S03 명세서"),
        };

        // CLI 제공자는 프롬프트를 단일 텍스트로만 받아 cache_control을 찍을 자리가 없다.
        // 실측 재사용률 3.1%. 접두사를 부풀린 대가만 남는다.
        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        [InlineData("agy-cli")]
        public void CliProviders_DefaultToNarrow(string provider)
        {
            Assert.Equal(ContextScopeMode.Narrow, PromptContextScope.ResolveMode(provider, configured: null));
        }

        [Theory]
        [InlineData("OpenAI")]
        [InlineData("Claude")]
        [InlineData("OpenRouter")]
        public void ApiProviders_DefaultToFull(string provider)
        {
            Assert.Equal(ContextScopeMode.Full, PromptContextScope.ResolveMode(provider, configured: null));
        }

        [Fact]
        public void ConfiguredValue_OverridesTheProviderDefault()
        {
            Assert.Equal(ContextScopeMode.Full, PromptContextScope.ResolveMode("claude-cli", "Full"));
            Assert.Equal(ContextScopeMode.Narrow, PromptContextScope.ResolveMode("OpenAI", "Narrow"));
        }

        [Fact]
        public void UnknownConfiguredValue_FallsBackToProviderDefault()
        {
            Assert.Equal(ContextScopeMode.Narrow, PromptContextScope.ResolveMode("claude-cli", "쓰레기값"));
        }

        // 제공자 분류는 AiClientFactory.IsCliProvider가 정본이다(정확 일치 허용목록).
        // ResolveMode가 "-cli" 접미사 자체를 다시 판정하면, 그 허용목록에 없으면서
        // 이름만 우연히 "-cli"로 끝나는 제공자가 잘못 Narrow로 분류된다 - 두 곳이
        // 같은 사실을 따로 판정할 때 나는 바로 그 결함이다.
        [Fact]
        public void UnknownProviderEndingInCliSuffix_DoesNotDefaultToNarrow()
        {
            Assert.Equal(ContextScopeMode.Full, PromptContextScope.ResolveMode("fake-vendor-cli", configured: null));
        }

        [Fact]
        public void NarrowSpecs_KeepsTheStepsOwnProcedure()
        {
            var narrowed = PromptContextScope.NarrowSpecs(
                AllSpecs, Step("S03", "dbo.UP_UTIL_SETTLE_INS"),
                callGraph: new Dictionary<string, IReadOnlyList<string>>());

            Assert.Single(narrowed);
            Assert.Equal("dbo.UP_UTIL_SETTLE_INS", narrowed[0].FileName);
        }

        // 실측 「필수 수정 1·2」가 이 관계였다: S13이 S11 명세가 규정한 오류 코드
        // 4000~4008을 지켜야 했다. 이웃을 빼면 이 유형의 결함이 오히려 늘어난다.
        [Fact]
        public void NarrowSpecs_IncludesOneHopCallees()
        {
            var callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo.UP_Util_Settle_Summary"] = new[] { "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA" }
            };

            var narrowed = PromptContextScope.NarrowSpecs(
                AllSpecs, Step("S11", "dbo.UP_Util_Settle_Summary"), callGraph);

            Assert.Equal(2, narrowed.Count);
            Assert.Contains(narrowed, s => s.FileName == "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA");
        }

        // 1-hop만 넣는다. 2-hop까지 끌면 전량으로 되돌아간다.
        [Fact]
        public void NarrowSpecs_DoesNotFollowTwoHops()
        {
            var specs = new List<(string FileName, string Content)>
            {
                ("A", "a"), ("B", "b"), ("C", "c")
            };
            var callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = new[] { "B" },
                ["B"] = new[] { "C" }
            };

            var narrowed = PromptContextScope.NarrowSpecs(specs, Step("S01", "A"), callGraph);

            Assert.Equal(2, narrowed.Count);
            Assert.DoesNotContain(narrowed, s => s.FileName == "C");
        }

        // 좁힐 근거가 없으면 좁히지 않는다. 빈 목록을 보내면 모델이 "원본이 없다"로
        // 읽고 지어낸다.
        [Fact]
        public void NarrowSpecs_WhenNothingMatches_ReturnsEverything()
        {
            var narrowed = PromptContextScope.NarrowSpecs(
                AllSpecs, Step("S99", "dbo.UP_Unknown"),
                callGraph: new Dictionary<string, IReadOnlyList<string>>());

            Assert.Equal(AllSpecs.Count, narrowed.Count);
        }

        // 순서는 원본 목록 순서를 지킨다. 순서가 흔들리면 같은 재료라도
        // 접두사가 달라져 캐시가 죽고, 회차 간 대조도 불가능해진다.
        [Fact]
        public void NarrowSpecs_PreservesSourceOrder()
        {
            var callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo.UP_UTIL_SETTLE_INS"] = new[] { "dbo.UP_Util_Settle_Summary" }
            };

            var narrowed = PromptContextScope.NarrowSpecs(
                AllSpecs, Step("S03", "dbo.UP_UTIL_SETTLE_INS"), callGraph);

            Assert.Equal(
                new[] { "dbo.UP_Util_Settle_Summary", "dbo.UP_UTIL_SETTLE_INS" },
                narrowed.Select(s => s.FileName).ToArray());
        }
    }
}
