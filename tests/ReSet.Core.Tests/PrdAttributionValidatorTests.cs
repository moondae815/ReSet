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
    }
}
