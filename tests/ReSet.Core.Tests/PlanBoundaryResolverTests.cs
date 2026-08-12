using System;
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
        //
        // "Mermaid 기반 통합 흐름도" H2를 포함시킨다. MechanicalValidator.RequiredConsolidatedHeaders가
        // 요구하는 4개 H2를 이 픽스처가 모두 갖춰야 SkeletonSplit == true 경로(4개 전부 성공)를
        // 검증할 수 있다 - 하나라도 빠지면 모든 Resolve_* 성공 케이스가 실패 폴백으로 떨어진다.
        private const string FinalPlan = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

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
            new(code, $"{code} 단계", new[] { "UP_X" }, new[] { "dbo.T" }, new[] { "-1" }, false, Array.Empty<string>());

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

        [Fact]
        public void Resolve_ShouldPutOverviewAndMermaidIntoArchitecture()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.True(slices.SkeletonSplit);
            Assert.Contains("## 통합 배치 아키텍처 개요", slices.Architecture);
            Assert.Contains("개요 본문", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldNotLeakStepDetailIntoArchitecture()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.DoesNotContain("정제된 S01 본문", slices.Architecture);
            Assert.DoesNotContain("공통 규약 본문", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldExtractSharedConventionsIntoStepContract()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.NotNull(slices.StepContract);
            Assert.Contains("공통 규약 본문", slices.StepContract!);
            Assert.DoesNotContain("정제된 S01 본문", slices.StepContract!);
        }

        [Fact]
        public void Resolve_ShouldExtractVerificationSql()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.NotNull(slices.Verification);
            Assert.Contains("검증 SQL 본문", slices.Verification!);
            Assert.DoesNotContain("정제된 S02 본문", slices.Verification!);
        }

        [Fact]
        public void Resolve_ShouldCaptureBannerAsPreamble()
        {
            // L1Exhausted 배너는 문서 선두에 삽입된다. 첫 H2 앞의 내용을 버리면
            // 그 경고가 산출물에서 사라진다.
            var withBanner = "> 경고: 검증을 소진했습니다.\n\n" + FinalPlan;

            var slices = PlanBoundaryResolver.Resolve(withBanner, LayoutWithSections());

            Assert.Contains("검증을 소진했습니다", slices.Preamble);
            Assert.DoesNotContain("검증을 소진했습니다", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldKeepSkeletonWhole_WhenAnH2IsMissing()
        {
            var missingVerification = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문
""";

            var slices = PlanBoundaryResolver.Resolve(missingVerification, LayoutWithSections());

            Assert.False(slices.SkeletonSplit);
            Assert.Null(slices.Verification);
            Assert.Null(slices.StepContract);
            Assert.Contains("개요 본문", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldStillSplitSteps_WhenSkeletonSplitFails()
        {
            // 두 판정은 독립이다. 골격이 통짜로 남아도 회차당 입력에서 가장 큰 몫인
            // 단계 상세는 여전히 분리되어야 한다.
            var missingVerification = """
## 통합 배치 아키텍처 개요

개요 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문
""";

            var slices = PlanBoundaryResolver.Resolve(missingVerification, LayoutWithSections());

            Assert.False(slices.SkeletonSplit);
            Assert.True(slices.StepsSplit);
            Assert.Equal(2, slices.Steps.Count);
        }

        /// <summary>
        /// 골격 분할이 실패해도 <c>common/00-architecture.md</c>가 단계 구간을 삼키면 안 된다.
        /// 이 파일은 모든 회차가 무조건 읽는 항목이라(TaskFileComposer의 "먼저 읽을 것"),
        /// 문서 끝까지 담으면 이 작업이 없애려던 통짜 문서가 회차마다 한 번씩 되살아나고
        /// 단계 슬라이스가 그 안에 통째로 중복된다 - 단일 파일 폴백보다도 나쁘다.
        /// </summary>
        [Fact]
        public void Resolve_ShouldNotSwallowStepsIntoArchitecture_WhenSkeletonSplitFails()
        {
            var missingVerification = """
## 통합 배치 아키텍처 개요

개요 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문
""";

            var slices = PlanBoundaryResolver.Resolve(missingVerification, LayoutWithSections());

            Assert.False(slices.SkeletonSplit);
            Assert.True(slices.StepsSplit);
            Assert.Contains("개요 본문", slices.Architecture);
            Assert.DoesNotContain("정제된 S01 본문", slices.Architecture);
            Assert.DoesNotContain("정제된 S02 본문", slices.Architecture);

            // 잘라낸 구간은 steps/*.md가 그대로 덮는다 - 내용이 유실되지 않는다.
            Assert.Contains("정제된 S01 본문", slices.Steps["S01"]);
            Assert.Contains("정제된 S02 본문", slices.Steps["S02"]);
        }

        /// <summary>
        /// 마지막 단계 <b>뒤</b>에 오는 절이 어느 조각에도 담기지 않으면 문서에서 사라진다.
        /// 이 분기는 StepContract·Verification이 모두 null이고 StepsSplit이 true라 진입점에
        /// 계획서 전문 링크도 실리지 않으므로, 개요 조각이 유일한 통짜 바구니다.
        ///
        /// 골격 탐색이 실패하는 가장 흔한 원인은 헤딩 문구 변경이다 - 즉 그 절이 <b>다른
        /// 이름으로 실재</b>하기 때문에 실패한다. 그래서 픽스처의 마지막 절 이름을 일부러
        /// 필수 H2와 다르게 둔다. 앞선 테스트가 이 결함을 못 잡은 이유가 픽스처에 마지막
        /// 단계 뒤 내용이 아예 없었기 때문이다(Task 4에서 고친 것과 같은 부류의 결함).
        /// </summary>
        [Fact]
        public void Resolve_ShouldKeepContentAfterTheLastStep_WhenSkeletonSplitFails()
        {
            var renamedVerificationHeading = """
## 통합 배치 아키텍처 개요

개요 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 데이터 정합성 대조 쿼리 모음

검증 SQL 본문은 여기 있다
""";

            var slices = PlanBoundaryResolver.Resolve(renamedVerificationHeading, LayoutWithSections());

            Assert.False(slices.SkeletonSplit);
            Assert.True(slices.StepsSplit);
            Assert.Null(slices.Verification);
            Assert.Null(slices.StepContract);

            // 단계 본문은 steps/*.md가 덮으므로 개요에 중복되지 않는다.
            Assert.DoesNotContain("정제된 S01 본문", slices.Architecture);
            Assert.DoesNotContain("정제된 S02 본문", slices.Architecture);

            // 마지막 단계 뒤의 절은 아무도 덮지 않는다 - 개요가 흡수해야 한다.
            Assert.Contains("개요 본문", slices.Architecture);
            Assert.Contains("데이터 정합성 대조 쿼리 모음", slices.Architecture);
            Assert.Contains("검증 SQL 본문은 여기 있다", slices.Architecture);
        }

        /// <summary>
        /// 위 테스트를 절 이름 하나가 아니라 <b>문서 전체</b>로 일반화한다. 골격 분할이 실패한
        /// 분기에서는 조각이 개요와 단계뿐이므로, 원문의 모든 비어 있지 않은 줄이 그 둘(그리고
        /// 서문) 어딘가에 남아 있어야 한다. 앞으로 이 분기에 새 경계를 넣을 때 어느 구간이
        /// 조용히 빠지는 것을 이 단언이 막는다.
        /// </summary>
        [Fact]
        public void Resolve_ShouldNotLoseAnyLine_WhenSkeletonSplitFails()
        {
            var document = """
서문 한 줄

## 통합 배치 아키텍처 개요

개요 본문

## 단계별 이행 상세 및 의사코드

공통 규약 본문

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 데이터 정합성 대조 쿼리 모음

검증 SQL 본문은 여기 있다

## 부록 - 운영 메모

운영 메모 본문
""";

            var slices = PlanBoundaryResolver.Resolve(document, LayoutWithSections());

            Assert.False(slices.SkeletonSplit);
            Assert.True(slices.StepsSplit);

            var covered = string.Join(
                "\n",
                new[] { slices.Preamble, slices.Architecture }.Concat(slices.Steps.Values));

            foreach (var line in document.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                Assert.Contains(line, covered);
            }
        }

        /// <summary>
        /// 골격 분할이 성공한 경로에서도 모든 줄이 어느 조각엔가 담겨야 한다.
        /// 담기지 않은 줄은 코딩 에이전트가 읽을 방법이 아예 없다.
        ///
        /// 부록을 두 자리에 둔다 - 마지막 단계와 검증 SQL 사이, 그리고 검증 SQL 뒤.
        /// 두 자리 모두 종전에는 조각 사이로 샜다.
        /// </summary>
        [Fact]
        public void Resolve_SkeletonSplitSucceeded_ShouldPlaceEveryLineInSomeSlice()
        {
            var document = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

공통 규약 본문

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 성능 고려사항

성능 메모 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문

## 부록 - 운영 메모

운영 메모 본문
""";

            var slices = PlanBoundaryResolver.Resolve(document, LayoutWithSections());

            Assert.True(slices.SkeletonSplit);
            Assert.True(slices.StepsSplit);

            var covered = string.Join(
                "\n",
                new[] { slices.Preamble, slices.Architecture, slices.StepContract ?? "", slices.Verification ?? "" }
                    .Concat(slices.Steps.Values));

            foreach (var line in document.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                Assert.Contains(line, covered);
            }
        }

        /// <summary>
        /// 고아 구간은 개요로 간다. 개요(common/00-architecture.md)는 모든 회차가 무조건
        /// 읽는 유일한 파일이라, 어느 회차가 그것을 필요로 하는지 판별하지 못한 상태에서
        /// 고를 수 있는 유일한 자리다.
        /// </summary>
        [Fact]
        public void Resolve_OrphanRegions_ShouldBeAbsorbedIntoArchitecture()
        {
            var document = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

공통 규약 본문

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 성능 고려사항

성능 메모 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문

## 부록 - 운영 메모

운영 메모 본문
""";

            var slices = PlanBoundaryResolver.Resolve(document, LayoutWithSections());

            Assert.Contains("성능 메모 본문", slices.Architecture);
            Assert.Contains("운영 메모 본문", slices.Architecture);

            // 흡수는 담기지 않은 것만 담는다. 이미 다른 조각이 가진 내용을 개요에
            // 다시 실으면 회차마다 읽는 파일이 조용히 부푼다.
            Assert.DoesNotContain("정제된 S01 본문", slices.Architecture);
            Assert.DoesNotContain("검증 SQL 본문", slices.Architecture);
            Assert.DoesNotContain("공통 규약 본문", slices.Architecture);
        }

        /// <summary>
        /// 공통 규약 조각이 자기 제목을 갖는다. 종전에는 이 H2 줄이 개요의 끝과 공통 규약의
        /// 시작 사이에 끼어 어느 조각에도 없었다.
        /// </summary>
        [Fact]
        public void Resolve_StepContract_ShouldBeginWithItsOwnHeading()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.NotNull(slices.StepContract);
            Assert.StartsWith("## 단계별 이행 상세 및 의사코드", slices.StepContract!);
            Assert.Contains("공통 규약 본문", slices.StepContract!);
        }

        /// <summary>
        /// 산문이 없으면 공통 규약 조각을 만들지 않는다. 헤딩을 무조건 붙이면 헤딩 한 줄짜리
        /// 파일이 생기고 진입점이 그것을 링크한다(InstructionBundleWriter.cs:61) - 회차마다
        /// 읽히는 빈 파일이 하나 는다. 남겨진 헤딩 줄은 개요가 받는다.
        /// </summary>
        [Fact]
        public void Resolve_StepContractWithoutProse_ShouldStayNullAndLeaveHeadingToArchitecture()
        {
            var noProse = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문
""";

            var slices = PlanBoundaryResolver.Resolve(noProse, LayoutWithSections());

            Assert.True(slices.SkeletonSplit);
            Assert.Null(slices.StepContract);
            Assert.Contains("## 단계별 이행 상세 및 의사코드", slices.Architecture);
        }

        /// <summary>
        /// 고아 구간이 없는 정상 문서에서는 개요가 종전과 똑같아야 한다. 흡수 기계를 넣고
        /// 나서 정상 문서의 개요가 달라지면 회차 입력이 조용히 부푼 것이다.
        /// </summary>
        [Fact]
        public void Resolve_DocumentWithoutOrphans_ShouldLeaveArchitectureUnchanged()
        {
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, LayoutWithSections());

            Assert.Equal(
                """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문
""",
                slices.Architecture);
        }

        /// <summary>
        /// 골격 분할은 성공했지만 단계 분할이 실패한 경로도 흡수를 탄다. 이 경로는 단계
        /// 경계를 몰라 "단계별 이행 상세" 섹션 전체를 개요가 통짜로 삼키므로(위 주석 참고),
        /// covered 목록에 단계 구간이 빠진다 - 그래도 검증 SQL 뒤의 고아 구간은 여전히
        /// 계산되고 흡수돼야 한다. layout을 null로 줘 단계 경계를 못 찾게 하되, 골격 H2
        /// 넷은 모두 갖춰 SkeletonSplit은 여전히 성공하게 한다.
        /// </summary>
        [Fact]
        public void Resolve_SkeletonSplitSucceededButStepsSplitFailed_ShouldAbsorbTrailingOrphan()
        {
            var document = """
## 통합 배치 아키텍처 개요

개요 본문

## Mermaid 기반 통합 흐름도

흐름도 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문

### S02 원장 생성

정제된 S02 본문

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문

## 부록 - 운영 메모

운영 메모 본문
""";

            var slices = PlanBoundaryResolver.Resolve(document, null);

            Assert.True(slices.SkeletonSplit);
            Assert.False(slices.StepsSplit);
            Assert.Contains("운영 메모 본문", slices.Architecture);
        }

        /// <summary>
        /// 반대로 단계 분할까지 실패했다면 끊을 기준점이 없다. 그때는 문서 끝까지
        /// 남겨야 어느 조각에도 속하지 못한 구간이 사라지지 않는다.
        /// </summary>
        [Fact]
        public void Resolve_ShouldKeepWholeDocument_WhenBothSkeletonAndStepSplitFail()
        {
            var missingVerification = """
## 통합 배치 아키텍처 개요

개요 본문

## 단계별 이행 상세 및 의사코드

### S01 스냅샷 생성

정제된 S01 본문
""";

            var slices = PlanBoundaryResolver.Resolve(missingVerification, null);

            Assert.False(slices.SkeletonSplit);
            Assert.False(slices.StepsSplit);
            Assert.Contains("개요 본문", slices.Architecture);
            Assert.Contains("정제된 S01 본문", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldKeepSkeletonWhole_WhenStepSplitFails()
        {
            // 단계 경계를 못 찾으면 StepContract를 잘라낼 기준점도 없다.
            var slices = PlanBoundaryResolver.Resolve(FinalPlan, null);

            Assert.False(slices.StepsSplit);
            Assert.Null(slices.StepContract);
            Assert.Contains("정제된 S01 본문", slices.Architecture);
        }

        [Fact]
        public void Resolve_ShouldFallBackToWholeDocument_WhenBothSplitsFail()
        {
            // 골격 헤딩도 단계 헤딩도 없는 완전히 망가진 문서. 두 판정이 동시에 실패해도
            // 크래시 없이 문서 전체를 Architecture에 그대로 보존해야 한다.
            var garbled = """
그냥 평범한 텍스트입니다.

헤딩도 없고 단계도 없습니다.
""";

            var slices = PlanBoundaryResolver.Resolve(garbled, null);

            Assert.False(slices.SkeletonSplit);
            Assert.False(slices.StepsSplit);
            Assert.Equal(garbled.Trim(), slices.Architecture);
            Assert.Empty(slices.Steps);
        }

        /// <summary>
        /// 조각이 덮은 범위를 모으면 덮이지 않은 구간이 계산된다. Resolve는 이 결과를
        /// 개요에 흡수해 "모든 줄이 어느 조각엔가 담긴다"는 불변식을 지킨다.
        /// </summary>
        [Fact]
        public void FindUncoveredRanges_NothingCovered_ShouldReturnWholeDocument()
        {
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, Array.Empty<(int, int)>());

            Assert.Equal(new[] { (0, 10) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_FullyCovered_ShouldReturnNothing()
        {
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (0, 10) });

            Assert.Empty(gaps);
        }

        [Fact]
        public void FindUncoveredRanges_ShouldFindLeadingMiddleAndTrailingGaps()
        {
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(20, new[] { (3, 7), (12, 15) });

            Assert.Equal(new[] { (0, 3), (7, 12), (15, 20) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_UnorderedInput_ShouldGiveSameResult()
        {
            // Resolve는 조각을 만든 순서대로 범위를 넣는다. 그 순서가 문서 순서와
            // 같으리라고 기대하지 않는다.
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (7, 10), (0, 3) });

            Assert.Equal(new[] { (3, 7) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_OverlappingRanges_ShouldNotInventGaps()
        {
            // 문서가 기형이라 단계 구간이 검증 SQL 구간과 겹칠 수 있다. 겹침을 빈틈으로
            // 잘못 읽으면 이미 실린 내용이 개요에 한 번 더 실린다.
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (0, 6), (3, 10) });

            Assert.Empty(gaps);
        }

        [Fact]
        public void FindUncoveredRanges_RangeFullyContainedInEarlierRange_ShouldNotInventGaps()
        {
            // 뒤에 정렬된 범위가 앞선 범위 안에 완전히 포함되면 커서가 뒤로 물러나면 안 된다.
            // 커서가 물러나면 앞선 범위의 꼬리(여기서는 [5, 10))가 덮이지 않은 것처럼 보이는데,
            // 그 구간은 이미 (0, 10) 조각에 실려 있으므로 다시 실으면 개요에 중복으로 붙는다.
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (0, 10), (2, 5) });

            Assert.Empty(gaps);
        }

        [Fact]
        public void FindUncoveredRanges_ShouldIgnoreEmptyRanges()
        {
            // End <= Start는 "그 조각은 만들어지지 않았다"는 뜻이다. 덮은 것으로 세지 않는다.
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (0, 4), (6, 6), (6, 10) });

            Assert.Equal(new[] { (4, 6) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_ShouldClampOutOfBoundsRanges()
        {
            // (-5, 3)과 (8, 99)만으로는 클램핑을 증명하지 못한다 - 두 Math.Max/Math.Min
            // 호출을 지워도 이 두 범위에서는 우연히 같은 (3, 8)이 나온다. 완전히
            // 범위 밖에 있는 구간(12..20, lineCount=10)이라야 갈린다: 클램프하면
            // End(10)가 Start(12)보다 작아져 그 범위 자체가 사라져 문서 전체가
            // 빈틈([(0, 10)])이 되고, 클램프하지 않으면 [12, 20)이 덮은 것으로 잘못
            // 인정되어 빈틈이 [(0, 12)]로 좁아진다.
            var gaps = PlanBoundaryResolver.FindUncoveredRanges(10, new[] { (12, 20) });

            Assert.Equal(new[] { (0, 10) }, gaps);
        }

        [Fact]
        public void FindUncoveredRanges_EmptyDocument_ShouldReturnNothing()
        {
            Assert.Empty(PlanBoundaryResolver.FindUncoveredRanges(0, new[] { (0, 5) }));
        }
    }
}
