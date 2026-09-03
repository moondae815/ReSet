using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdAttributionValidatorTests
    {
        private const string Spec = @"## 개요

일별 정산 마감을 수행한다.

## 파라미터 목록

| 이름 | 타입 |
| :--- | :--- |
| @BaseDate | char(8) |

## CRUD 분석

TB_SETTLE_DAILY에 INSERT 한다. 중복 검사를 먼저 수행한다.

## 로직 흐름 요약

1. 기준일자를 검증한다.
2. 미집계 건을 조회한다.
";

        private static string PrdWith(params string[] rows) =>
            "## 배경 및 목적\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-BG-01 | 일별 정산을 마감한다 | ## 개요 > \"일별 정산 마감\" | 도출 |\n\n"
            + "## 수행 조건 및 입력 계약\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-IN-01 | 기준일자를 받는다 | ## 파라미터 목록 > \"@BaseDate\" | 도출 |\n\n"
            + "## 데이터 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + string.Join("\n", rows) + "\n\n"
            + "## 기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 로직 흐름 요약 > \"기준일자를 검증한다\" | 도출 |\n\n"
            + "## 예외 및 비기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-NFR-01 | 중복 적재를 막는다 | ## CRUD 분석 > \"중복 검사\" | 추정 |\n";

        private const string GoodDataRow =
            "| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_DAILY에 INSERT\" | 도출 |";

        [Fact]
        public void Validate_ShouldPass_WhenEverySectionAndEvidenceIsSound()
        {
            var result = PrdAttributionValidator.Validate(PrdWith(GoodDataRow), Spec);

            Assert.True(result.IsValid, string.Join("; ", result.Defects.Select(d => d.Message)));
        }

        [Fact]
        public void Validate_ShouldReportSectionMissing_WhenASectionIsAbsent()
        {
            var prd = PrdWith(GoodDataRow).Replace("## 기능 요구사항", "## 기능 요구 사항");

            var result = PrdAttributionValidator.Validate(prd, Spec);

            Assert.Contains(result.Defects, d => d.Type == PrdDefectType.SectionMissing);
        }

        [Fact]
        public void Validate_ShouldReportConfidenceVocabulary_WhenValueIsNotDerivedOrInferred()
        {
            var row = "| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_DAILY에 INSERT\" | 높음 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.ConfidenceVocabulary && d.RequirementId == "REQ-DATA-01");
        }

        [Fact]
        public void Validate_ShouldReportEvidenceMissing_WhenEvidenceCellIsEmpty()
        {
            var row = "| REQ-DATA-01 | 미집계 건을 적재한다 |  | 추정 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceMissing && d.RequirementId == "REQ-DATA-01");
        }

        [Fact]
        public void Validate_ShouldReportEvidenceMissing_ForInferredRowsToo()
        {
            // 「추정」이라고 해서 근거가 면제되지 않는다 - 재구성의 출발점을 밝혀야 한다.
            var row = "| REQ-DATA-01 | 정산 정책이 바뀌면 재집계한다 |  | 추정 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(result.Defects, d => d.Type == PrdDefectType.EvidenceMissing);
        }

        [Fact]
        public void Validate_ShouldReportIdPrefixMismatch_WhenRowSitsInTheWrongSection()
        {
            var row = "| REQ-FUNC-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_DAILY에 INSERT\" | 도출 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.IdPrefixMismatch && d.RequirementId == "REQ-FUNC-01");
        }

        [Fact]
        public void Validate_ShouldReportEvidenceSourceNotAllowed_WhenCitingAnUnmappedSection()
        {
            // 기능 요구사항은 로직 흐름 요약에서만 파생한다. 파라미터 목록 인용은
            // 실재하더라도 파생 계약 위반이다.
            var prd = PrdWith(GoodDataRow).Replace(
                "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 로직 흐름 요약 > \"기준일자를 검증한다\" | 도출 |",
                "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 파라미터 목록 > \"@BaseDate\" | 도출 |");

            var result = PrdAttributionValidator.Validate(prd, Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceSourceNotAllowed && d.RequirementId == "REQ-FUNC-01");
        }

        [Fact]
        public void Validate_ShouldAllowEitherSource_ForTheNonFunctionalSection()
        {
            // 예외 및 비기능 요구사항만 원천이 둘이다. 둘 다 통과해야 한다.
            var prd = PrdWith(GoodDataRow).Replace(
                "| REQ-NFR-01 | 중복 적재를 막는다 | ## CRUD 분석 > \"중복 검사\" | 추정 |",
                "| REQ-NFR-01 | 중복 적재를 막는다 | ## 로직 흐름 요약 > \"미집계 건을 조회한다\" | 추정 |");

            var result = PrdAttributionValidator.Validate(prd, Spec);

            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceSourceNotAllowed);
        }

        [Fact]
        public void Validate_ShouldReportHeadingNotFound_WhenSpecHasNoSuchHeading()
        {
            var specWithoutCrud = Spec.Replace("## CRUD 분석", "## 데이터 조작 분석");
            var prd = PrdWith(GoodDataRow);

            var result = PrdAttributionValidator.Validate(prd, specWithoutCrud);

            Assert.Contains(result.Defects, d => d.Type == PrdDefectType.EvidenceHeadingNotFound);
        }

        [Fact]
        public void Validate_ShouldReportQuoteNotFound_WhenTheQuoteIsNotInThatSection()
        {
            // 인용 구절이 Spec 어디에도 없다.
            var row = "| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_MONTHLY에 INSERT\" | 도출 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceQuoteNotFound && d.RequirementId == "REQ-DATA-01");
        }

        [Fact]
        public void Validate_ShouldReportQuoteNotFound_WhenTheQuoteLivesInAnotherSection()
        {
            // 구절은 Spec에 있지만 인용한 헤딩 아래가 아니다. 문서 전체 검색으로
            // 대조하면 이것을 놓친다.
            var row = "| REQ-DATA-01 | 기준일자를 검증한다 | ## CRUD 분석 > \"기준일자를 검증한다\" | 도출 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceQuoteNotFound && d.RequirementId == "REQ-DATA-01");
        }

        [Fact]
        public void Validate_ShouldTolerateMarkdownEmphasisAndSpacingInTheSpec()
        {
            // Spec 본문의 강조 표기는 인용과 글자가 달라 보이게 만든다. 이것으로
            // 오탐이 나면 검사는 곧 꺼진다.
            var emphasised = Spec.Replace(
                "TB_SETTLE_DAILY에 INSERT 한다.",
                "**TB_SETTLE_DAILY**에 `INSERT` 한다.");

            var result = PrdAttributionValidator.Validate(PrdWith(GoodDataRow), emphasised);

            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceQuoteNotFound);
        }

        [Fact]
        public void Validate_ShouldFire_WhenASingleCharacterOfTheQuoteIsAltered()
        {
            // 결함 주입 회귀. 이것이 없으면 검사가 살아 있는지 알 수 없다.
            var tampered = PrdWith(GoodDataRow).Replace(
                "TB_SETTLE_DAILY에 INSERT",
                "TB_SETTLE_DAIL7에 INSERT");

            var result = PrdAttributionValidator.Validate(tampered, Spec);

            Assert.Contains(result.Defects, d => d.Type == PrdDefectType.EvidenceQuoteNotFound);
        }

        [Fact]
        public void Validate_ShouldToleratePrefixedHeading_WhenSpecHasNumberedHeading()
        {
            // Spec의 헤딩이 번호가 붙어 있으면(## 3. CRUD 분석) PRD의 일반 형태(## CRUD 분석)
            // 와 대조할 수 있어야 한다. 정확 일치를 먼저 시도하고 폴백한다.
            var numberedSpec = Spec.Replace("## CRUD 분석", "## 3. CRUD 분석");
            var result = PrdAttributionValidator.Validate(PrdWith(GoodDataRow), numberedSpec);

            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceHeadingNotFound);
            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceSourceNotAllowed);
            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceQuoteNotFound);
        }

        [Fact]
        public void Validate_ShouldToleratePrefixedHeading_WhenPrdCitesNumberedHeading()
        {
            // 거울 사례: PRD가 번호를 포함해 인용(## 3. CRUD 분석)하고 Spec이 없으면,
            // 정규화된 형태로 대조한다.
            var prd = PrdWith(GoodDataRow).Replace(
                "## CRUD 분석",
                "## 3. CRUD 분석");

            var result = PrdAttributionValidator.Validate(prd, Spec);

            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceHeadingNotFound);
            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceSourceNotAllowed);
            Assert.DoesNotContain(result.Defects, d => d.Type == PrdDefectType.EvidenceQuoteNotFound);
        }

        [Fact]
        public void Validate_ShouldStillReportWrongSource_AfterHeadingNormalization()
        {
            // 헤딩 정규화가 파생 계약 검사를 빼먹지 않는지 확인. 기능 요구사항이
            // 파라미터 목록을 인용하면 번호가 붙었어도 여전히 오류다.
            var prd = PrdWith(GoodDataRow).Replace(
                "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 로직 흐름 요약 > \"기준일자를 검증한다\" | 도출 |",
                "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 2. 파라미터 목록 > \"@BaseDate\" | 도출 |");

            var result = PrdAttributionValidator.Validate(prd, Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceSourceNotAllowed && d.RequirementId == "REQ-FUNC-01");
        }

        [Fact]
        public void Validate_ShouldStillReportQuoteInWrongSection_AfterHeadingFallback()
        {
            // 헤딩 폴백이 섹션 특정 검사를 약화시키지 않는지 확인. 구절이 다른 섹션에
            // 있으면 정확 일치든 폴백이든 여전히 실패해야 한다.
            var row = "| REQ-DATA-01 | 기준일자를 검증한다 | ## CRUD 분석 > \"기준일자를 검증한다\" | 도출 |";

            var result = PrdAttributionValidator.Validate(PrdWith(row), Spec);

            Assert.Contains(
                result.Defects,
                d => d.Type == PrdDefectType.EvidenceQuoteNotFound && d.RequirementId == "REQ-DATA-01");
        }
    }
}
