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

        // 자리표시자 뒤에 산문이 더 있는 골격. "자리표시자 위치에 치환" 구현은
        // 이 산문보다 앞에 단계 본문을 끼워 넣지만, 올바른 구현(블록 끝에
        // 결정적으로 덧붙이기)은 산문 뒤에 붙여야 한다.
        private const string SkeletonWithTrailingProse = @"# 계획서

## 단계별 이행 상세 및 의사코드

### 공통 SQL 오류 추적 패턴

공통 규약 본문.

<!-- STEP:S01 -->
<!-- STEP:S02 -->

여기부터는 자리표시자 뒤에 오는 후행 산문이다.

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문.";

        // 자리표시자 순서(S02, S01)와 전달되는 섹션 순서(S01, S02)가 어긋난 골격.
        // "자리표시자 위치에 치환" 구현은 출력 순서를 S02, S01로 뒤집지만, 목록
        // 순서를 따르는 구현은 S01, S02 순서를 지킨다.
        private const string SkeletonWithReversedPlaceholders = @"# 계획서

## 단계별 이행 상세 및 의사코드

### 공통 SQL 오류 추적 패턴

공통 규약 본문.

<!-- STEP:S02 -->
<!-- STEP:S01 -->

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문.";

        // 자리표시자가 하나뿐인데 단계 섹션은 둘인 골격. "자리표시자 위치에
        // 치환" 구현은 두 번째 섹션을 놓치지만, 목록 전체를 덧붙이는 구현은
        // 둘 다 포함한다.
        private const string SkeletonWithSinglePlaceholder = @"# 계획서

## 단계별 이행 상세 및 의사코드

### 공통 SQL 오류 추적 패턴

공통 규약 본문.

<!-- STEP:S01 -->

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문.";

        // 단계 상세 블록 안, 펜스(```) 코드 블록 내부에 "## "로 시작하는 줄이
        // 있는 골격. 펜스를 인식하지 못하는 스캐너는 이 줄을 다음 H2로 오인해
        // 블록을 조기 종료한다.
        private const string SkeletonWithFencedHeading = @"# 계획서

## 단계별 이행 상세 및 의사코드

### 공통 SQL 오류 추적 패턴

공통 규약 본문.

```sql
## 이것은 코드 블록 안의 주석이라 헤더가 아니다
SELECT 1;
```

<!-- STEP:S01 -->

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문.";

        // 진짜 헤더보다 앞, 펜스 코드 블록 안에 헤더와 똑같은 텍스트가 있는
        // 골격. 펜스를 인식하지 못하는 스캐너는 이 가짜 헤더를 진짜로 착각해
        // 블록 시작 지점을 잘못 잡는다.
        private const string SkeletonWithFencedHeaderLookalike = @"# 계획서

## 통합 배치 아키텍처 개요

```text
## 단계별 이행 상세 및 의사코드
```

## 단계별 이행 상세 및 의사코드

### 공통 SQL 오류 추적 패턴

공통 규약 본문.

<!-- STEP:S01 -->

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

        [Fact]
        public void Assemble_AppendsAfterTrailingProseNotAtPlaceholderPosition()
        {
            var result = BatchPlanAssembler.Assemble(
                SkeletonWithTrailingProse,
                new[] { "### S01 첫 단계\n\n본문1", "### S02 둘째 단계\n\n본문2" });

            var trailingProse = result.IndexOf("여기부터는 자리표시자 뒤에 오는 후행 산문이다.");
            var s01 = result.IndexOf("### S01 첫 단계");
            var s02 = result.IndexOf("### S02 둘째 단계");
            var validation = result.IndexOf("## 통합 데이터 정합성 검증 SQL 세트");

            Assert.True(trailingProse >= 0);
            Assert.True(trailingProse < s01, "부착된 단계는 자리표시자 위치가 아니라 후행 산문 뒤, 블록 끝에 와야 한다");
            Assert.True(s01 < s02, "단계는 목록 순서를 지켜야 한다");
            Assert.True(s02 < validation, "단계는 다음 H2 앞에 삽입돼야 한다");
        }

        [Fact]
        public void Assemble_FollowsSuppliedSectionOrderNotPlaceholderOrder()
        {
            var result = BatchPlanAssembler.Assemble(
                SkeletonWithReversedPlaceholders,
                new[] { "### S01 첫 단계\n\n본문1", "### S02 둘째 단계\n\n본문2" });

            var s01 = result.IndexOf("### S01 첫 단계");
            var s02 = result.IndexOf("### S02 둘째 단계");

            Assert.True(s01 < s02, "출력 순서는 자리표시자 순서(S02, S01)가 아니라 전달된 섹션 목록 순서(S01, S02)를 따라야 한다");
        }

        [Fact]
        public void Assemble_WithFewerPlaceholdersThanSections_StillIncludesAllSections()
        {
            var result = BatchPlanAssembler.Assemble(
                SkeletonWithSinglePlaceholder,
                new[] { "### S01 첫 단계\n\n본문1", "### S02 둘째 단계\n\n본문2" });

            Assert.Contains("### S01 첫 단계", result);
            Assert.Contains("### S02 둘째 단계", result);
        }

        [Fact]
        public void ExtractSharedConventions_IgnoresHeadingLookingLinesInsideFencedBlock()
        {
            var conventions = BatchPlanAssembler.ExtractSharedConventions(SkeletonWithFencedHeading);

            Assert.Contains("공통 규약 본문.", conventions);
            Assert.Contains("SELECT 1;", conventions);
            Assert.Contains("## 이것은 코드 블록 안의 주석이라 헤더가 아니다", conventions);
            Assert.DoesNotContain("검증 SQL 본문.", conventions);
        }

        [Fact]
        public void Assemble_IgnoresHeadingLookingLinesInsideFencedBlockWhenLocatingBlockEnd()
        {
            var result = BatchPlanAssembler.Assemble(
                SkeletonWithFencedHeading,
                new[] { "### S01 첫 단계\n\n본문1" });

            var fencedHeading = result.IndexOf("## 이것은 코드 블록 안의 주석이라 헤더가 아니다");
            var s01 = result.IndexOf("### S01 첫 단계");
            var validation = result.IndexOf("## 통합 데이터 정합성 검증 SQL 세트");

            Assert.True(fencedHeading >= 0);
            Assert.True(fencedHeading < s01, "펜스 안의 '## ' 유사 줄 때문에 블록이 조기 종료되면 안 된다");
            Assert.True(s01 < validation, "단계는 실제 다음 H2 앞에 삽입돼야 한다");
        }

        [Fact]
        public void ExtractSharedConventions_IgnoresHeaderLookalikeInsideFencedBlock()
        {
            var conventions = BatchPlanAssembler.ExtractSharedConventions(SkeletonWithFencedHeaderLookalike);

            Assert.Contains("공통 규약 본문.", conventions);
            Assert.DoesNotContain("검증 SQL 본문.", conventions);
        }

        // 공통 규약 소절 안, SQL 코드 블록의 여는 펜스(```)만 있고 닫는 펜스가
        // 없는 골격. 펜스 상태를 그대로 신뢰하는 스캐너는 이후 모든 줄(검증 SQL
        // H2 포함)을 "펜스 안"으로 오인해 다음 H2를 영영 못 찾는다.
        private const string SkeletonWithUnterminatedFence = @"# 계획서

## 단계별 이행 상세 및 의사코드

### 공통 SQL 오류 추적 패턴

공통 규약 본문.

```sql
SELECT 1;
-- 닫는 펜스가 없다

<!-- STEP:S01 -->

## 통합 데이터 정합성 검증 SQL 세트

검증 SQL 본문.";

        [Fact]
        public void ExtractSharedConventions_WithUnterminatedFence_StopsAtNextRealHeader()
        {
            var conventions = BatchPlanAssembler.ExtractSharedConventions(SkeletonWithUnterminatedFence);

            Assert.Contains("공통 규약 본문.", conventions);
            // 펜스가 닫히지 않아도 검증 SQL H2 이후 내용을 공통 규약으로 오인해
            // 끌어오면 안 된다 — 오인하면 그 내용이 N개 단계 프롬프트 전부에
            // 잘못 복사된다.
            Assert.DoesNotContain("검증 SQL 본문.", conventions);
            Assert.DoesNotContain("## 통합 데이터 정합성 검증 SQL 세트", conventions);
        }

        [Fact]
        public void Assemble_WithUnterminatedFence_InsertsBeforeValidationHeaderNotAtDocumentEnd()
        {
            var result = BatchPlanAssembler.Assemble(
                SkeletonWithUnterminatedFence,
                new[] { "### S01 첫 단계\n\n본문1" });

            var s01 = result.IndexOf("### S01 첫 단계");
            var validation = result.IndexOf("## 통합 데이터 정합성 검증 SQL 세트");

            Assert.True(s01 >= 0);
            Assert.True(validation >= 0);
            Assert.True(s01 < validation,
                "펜스가 닫히지 않아도 단계 본문은 검증 SQL 헤더보다 앞, 올바른 블록 안에 들어가야 한다 — " +
                "문서 맨 끝(검증 SQL 아래)에 붙으면 안 된다");
        }

        [Fact]
        public void Assemble_WithNullSkeleton_AppendsHeaderAndSections()
        {
            var result = BatchPlanAssembler.Assemble(null, new[] { "### S01 첫 단계\n\n본문1" });

            Assert.Contains(BatchPlanAssembler.StepDetailHeader, result);
            Assert.Contains("### S01 첫 단계", result);
        }

        [Fact]
        public void ExtractSharedConventions_WithNullSkeleton_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, BatchPlanAssembler.ExtractSharedConventions(null));
        }

        [Fact]
        public void Assemble_IgnoresNullAndWhitespaceOnlySections()
        {
            var result = BatchPlanAssembler.Assemble(
                Skeleton,
                new[] { "### S01 첫 단계\n\n본문1", null!, "   \n  ", "### S02 둘째 단계\n\n본문2" });

            Assert.Contains("### S01 첫 단계", result);
            Assert.Contains("### S02 둘째 단계", result);
            Assert.DoesNotContain("<!-- STEP:", result);
        }

        [Fact]
        public void Assemble_HandlesCrlfLineEndings()
        {
            var crlfSkeleton = Skeleton.Replace("\n", "\r\n");

            var result = BatchPlanAssembler.Assemble(
                crlfSkeleton,
                new[] { "### S01 첫 단계\n\n본문1" });

            Assert.Contains("### S01 첫 단계", result);
            Assert.DoesNotContain("<!-- STEP:", result);
            Assert.Contains("### 공통 SQL 오류 추적 패턴", result);
        }

        // === 헤딩 변형 =======================================================
        //
        // 실측(POQSettleProc17·18 연속): 골격이 계약의 VERBATIM 헤더 대신
        // `## 단계별 이행 상세 및 의사코드:`처럼 콜론을 붙여 썼다. 정확 일치 탐색이
        // 못 찾아 단계 본문이 문서 끝에 새 H2와 함께 붙었고, 계획서에 같은 H2가 둘이
        // 되어 공통 규약 절이 단계 본문과 갈라졌다. MechanicalValidator는 Contains로
        // 보므로 이 문서를 통과시켰다.

        private const string SkeletonWithColonHeading = @"# 계획서

## 통합 배치 아키텍처 개요:

개요 본문.

## 단계별 이행 상세 및 의사코드:

### 공통 SQL 오류 추적 패턴

공통 규약 본문.

<!-- STEP:S01 -->

## 통합 데이터 정합성 검증 SQL 세트:

검증 SQL 본문.";

        [Fact]
        public void Assemble_ShouldInsertStepsUnderAHeadingThatCarriesATrailingColon()
        {
            var result = BatchPlanAssembler.Assemble(
                SkeletonWithColonHeading, new[] { "### S01 단계\n\n본문." });

            var lines = MarkdownSectionLocator.SplitLines(result);
            var stepDetailHeadings = lines.Count(
                l => l.TrimStart().StartsWith("## ", System.StringComparison.Ordinal) &&
                     l.Contains("단계별 이행 상세 및 의사코드"));

            Assert.Equal(1, stepDetailHeadings);
        }

        // 단계 본문은 공통 규약 뒤, 다음 H2 앞에 들어가야 한다. 헤딩을 찾았다는 것만으로는
        // 삽입 위치가 맞는지 알 수 없다.
        [Fact]
        public void Assemble_ShouldPlaceStepsBeforeTheNextHeadingWhenTheHeadingHasAColon()
        {
            var result = BatchPlanAssembler.Assemble(
                SkeletonWithColonHeading, new[] { "### S01 단계\n\n본문." });

            var stepIndex = result.IndexOf("### S01 단계", System.StringComparison.Ordinal);
            var conventionsIndex = result.IndexOf("공통 규약 본문", System.StringComparison.Ordinal);
            var nextHeadingIndex = result.IndexOf(
                "## 통합 데이터 정합성 검증 SQL 세트", System.StringComparison.Ordinal);

            Assert.True(conventionsIndex < stepIndex, "단계 본문이 공통 규약보다 앞에 놓였습니다.");
            Assert.True(stepIndex < nextHeadingIndex, "단계 본문이 다음 H2를 넘어갔습니다.");
        }
    }
}
