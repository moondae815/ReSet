using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PlanBoundaryResolverTests
    {
        // 조각의 본문과 최종 문서의 본문을 일부러 다르게 둔다. 결과가 어느 쪽에서
        // 왔는지 구별할 수 없으면 이 설계의 핵심 성질을 검증할 수 없다.
        private const string FinalPlan = """
## 통합 배치 아키텍처 개요

개요 본문

## 단계별 이행 상세 및 의사코드

### 공통 Tasklet 실행 계약

공통 규약 본문

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

        private static PlanLayout LayoutWithSections() => new(
            "골격",
            new Dictionary<string, string>
            {
                ["S01"] = "### S01 스냅샷 생성\n\n조각에 남은 옛 S01 본문",
                ["S02"] = "### S02 원장 생성\n\n조각에 남은 옛 S02 본문",
            },
            new[] { Step("S01"), Step("S02") },
            null);

        private static BatchStepPlan Step(string code) =>
            new(code, $"{code} 단계", new[] { "UP_X" }, new[] { "dbo.T" }, new[] { "-1" }, false);

        [Fact]
        public void ResolveSteps_ShouldTakeBodiesFromFinalDocument_NotFromSections()
        {
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.True(result.Split);
            Assert.Contains("정제된 S01 본문", result.Steps["S01"]);
            Assert.DoesNotContain("옛 S01 본문", result.Steps["S01"]);
        }

        [Fact]
        public void ResolveSteps_ShouldStartEachSliceAtItsHeading()
        {
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.StartsWith("### S01 스냅샷 생성", result.Steps["S01"]);
            Assert.StartsWith("### S02 원장 생성", result.Steps["S02"]);
        }

        [Fact]
        public void ResolveSteps_ShouldNotLeakNextStepIntoPreviousSlice()
        {
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.DoesNotContain("S02", result.Steps["S01"]);
        }

        [Fact]
        public void ResolveSteps_ShouldEndLastSliceAtNextH2()
        {
            // 마지막 단계 뒤에는 검증 SQL H2가 온다. 그것까지 삼키면 안 된다.
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.DoesNotContain("검증 SQL 본문", result.Steps["S02"]);
            Assert.Contains("정제된 S02 본문", result.Steps["S02"]);
        }

        [Fact]
        public void ResolveSteps_ShouldNotIncludeSharedConventions()
        {
            // 공통 규약은 common/으로 가야지 특정 단계에 붙으면 안 된다.
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, LayoutWithSections());

            Assert.All(result.Steps.Values, body => Assert.DoesNotContain("공통 규약 본문", body));
        }

        [Fact]
        public void ResolveSteps_ShouldFallBackToStepCodes_WhenAnchorsDoNotMatch()
        {
            // 정제가 헤딩 문구를 바꾸면 앵커가 어긋난다. 목차의 Code로 복구한다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string>
                {
                    ["S01"] = "### S01 완전히 다른 제목\n본문",
                    ["S02"] = "### S02 완전히 다른 제목\n본문",
                },
                new[] { Step("S01"), Step("S02") },
                null);

            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, layout);

            Assert.True(result.Split);
            Assert.Contains("정제된 S01 본문", result.Steps["S01"]);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ResolveSteps_ShouldFailWholly_WhenOneStepCannotBeLocated()
        {
            // 부분 분할은 하지 않는다. 빈 steps/*.md가 조용히 생기는 것이 최악이다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string>
                {
                    ["S01"] = "### S01 스냅샷 생성\n본문",
                    ["S99"] = "### S99 문서에 없는 단계\n본문",
                },
                new[] { Step("S01"), Step("S99") },
                null);

            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, layout);

            Assert.False(result.Split);
            Assert.Empty(result.Steps);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ResolveSteps_ShouldReturnNotSplit_WhenLayoutIsNull()
        {
            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, null);

            Assert.False(result.Split);
            Assert.Empty(result.Steps);
            Assert.NotEmpty(result.Warnings);
        }

        [Fact]
        public void ResolveSteps_ShouldFail_WhenSectionHasNoHeading()
        {
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string> { ["S01"] = "헤딩 없는 조각 본문" },
                null,
                null);

            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, layout);

            Assert.False(result.Split);
        }

        [Fact]
        public void ResolveSteps_ShouldFail_WhenTwoSectionsShareOneHeading()
        {
            // 같은 헤딩을 가리키는 두 단계는 경계를 정할 수 없다.
            var layout = new PlanLayout(
                "골격",
                new Dictionary<string, string>
                {
                    ["S01"] = "### S01 스냅샷 생성\n본문",
                    ["S02"] = "### S01 스냅샷 생성\n본문",
                },
                null,
                null);

            var result = PlanBoundaryResolver.ResolveSteps(FinalPlan, layout);

            Assert.False(result.Split);
        }

        [Fact]
        public void ResolveSteps_ShouldIgnoreHeadingsInsideCodeFences()
        {
            var planWithFence = """
## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

```sql
-- ### S02 원장 생성
SELECT 1;
```

진짜 S01 본문

### S02 원장 생성

진짜 S02 본문
""";

            var result = PlanBoundaryResolver.ResolveSteps(planWithFence, LayoutWithSections());

            Assert.True(result.Split);
            Assert.Contains("진짜 S01 본문", result.Steps["S01"]);
            Assert.Contains("진짜 S02 본문", result.Steps["S02"]);
        }
    }
}
