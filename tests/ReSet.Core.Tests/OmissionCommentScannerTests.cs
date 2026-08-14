using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class OmissionCommentScannerTests
    {
        [Theory]
        [InlineData("    -- 나머지 실제 컬럼도 원본 순서가 아닌 명시적 이름으로 모두 기술")]
        [InlineData("    -- 나머지 S03 대상도 같은 DELETE 후 INSERT 순서를 적용")]
        [InlineData("        -- 위 INSERT 목록과 동일한 전체 컬럼")]
        public void Scan_ShouldFlagCommentsThatStandInForOmittedCode(string comment)
        {
            var plan = $"```sql\nSELECT 1;\n{comment}\n```";

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Theory]
        [InlineData("    -- 원본 필터 YMD = @pi_strYMD AND USESTATE = 2를 모두 유지한다.")]
        [InlineData("    -- 원본 선행 보호 조건을 그대로 보존한다.")]
        public void Scan_ShouldNotFlagInstructionCommentsThatDemandPreservation(string comment)
        {
            // 오탐 경계를 고정한다. 배너가 잦으면 사람이 읽지 않게 되므로,
            // "유지하라"는 지시는 생략 지시가 아니다.
            var plan = $"```sql\nSELECT 1;\n{comment}\n```";

            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldIgnoreProseOutsideCodeFences()
        {
            // 산문에서 "나머지 단계도 같은 방식으로 적용한다"는 정상적인 설명이다.
            var plan = "나머지 단계도 같은 방식을 적용한다.\n\n```sql\nSELECT 1;\n```";

            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldDeduplicateIdenticalComments()
        {
            var line = "    -- 나머지 실제 컬럼도 모두 기술";
            var plan = $"```sql\n{line}\n{line}\n```";

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Scan_ShouldReturnEmptyForBlankInput(string? plan)
        {
            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }
    }
}
