using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchPlanAssemblerTests
    {
        private const string Skeleton = @"# 계획서

## 통합 배치 아키텍처 개요

개요 본문.

## Mermaid 기반 통합 흐름도

흐름도 본문.

## 단계별 이행 상세 및 의사코드

### 공통 SQL 오류 추적 패턴

공통 규약 본문.

<!-- STEP:S01 -->
<!-- STEP:S02 -->

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문.";

        [Fact]
        public void Assemble_InsertsSectionsBeforeNextH2()
        {
            var result = BatchPlanAssembler.Assemble(
                Skeleton,
                new[] { "### S01 첫 단계\n\n본문1", "### S02 둘째 단계\n\n본문2" });

            var s01 = result.IndexOf("### S01 첫 단계");
            var s02 = result.IndexOf("### S02 둘째 단계");
            var validation = result.IndexOf("## 통합 데이터 정합성 검증 SQL 세트");
            var conventions = result.IndexOf("### 공통 SQL 오류 추적 패턴");

            Assert.True(conventions < s01, "공통 규약이 단계보다 앞에 와야 한다");
            Assert.True(s01 < s02, "단계는 목록 순서를 지켜야 한다");
            Assert.True(s02 < validation, "단계는 다음 H2 앞에 삽입돼야 한다");
        }

        [Fact]
        public void Assemble_StripsStepPlaceholders()
        {
            var result = BatchPlanAssembler.Assemble(Skeleton, new[] { "### S01 첫 단계\n\n본문1" });

            Assert.DoesNotContain("<!-- STEP:", result);
        }

        [Fact]
        public void Assemble_WithoutStepDetailHeader_AppendsHeaderAndSections()
        {
            var result = BatchPlanAssembler.Assemble(
                "# 계획서\n\n## 통합 배치 아키텍처 개요\n\n개요.",
                new[] { "### S01 첫 단계\n\n본문1" });

            Assert.Contains(BatchPlanAssembler.StepDetailHeader, result);
            Assert.Contains("### S01 첫 단계", result);
        }

        [Fact]
        public void Assemble_WithNoSections_ReturnsSkeletonWithoutPlaceholders()
        {
            var result = BatchPlanAssembler.Assemble(Skeleton, new string[0]);

            Assert.DoesNotContain("<!-- STEP:", result);
            Assert.Contains("### 공통 SQL 오류 추적 패턴", result);
            Assert.DoesNotContain("### S01", result);
        }

        [Fact]
        public void ExtractSharedConventions_ReturnsOnlyStepDetailBody()
        {
            var conventions = BatchPlanAssembler.ExtractSharedConventions(Skeleton);

            Assert.Contains("### 공통 SQL 오류 추적 패턴", conventions);
            Assert.Contains("공통 규약 본문.", conventions);
            Assert.DoesNotContain("검증 SQL 본문.", conventions);
            Assert.DoesNotContain("개요 본문.", conventions);
            Assert.DoesNotContain("<!-- STEP:", conventions);
        }

        [Fact]
        public void ExtractSharedConventions_WithoutHeader_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, BatchPlanAssembler.ExtractSharedConventions("# 계획서\n\n본문만."));
        }
    }
}
