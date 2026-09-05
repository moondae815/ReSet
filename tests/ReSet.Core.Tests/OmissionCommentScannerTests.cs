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
        // "-- 나머지 컬럼도 같은 방식으로 유지한다" 케이스는 2026-09-05 에 제거했다.
        // PreservationMarkers 화이트리스트가 없어졌기 때문이며, 없앤 이유는
        // 그 화이트리스트가 감사 🔴(S07 - 갱신 10개 소실)의 문구를 면제했기 때문이다.
        // 오탐 경계는 이제 문구가 아니라 구조가 지킨다(ScanBlockComments 참고).
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

        [Fact]
        public void Scan_ShouldFlagBlockCommentStandingInForDml()
        {
            // 감사가 🔴로 매긴 실제 모양(S08.md:155-159). UPDATE 문이 서야 할 자리에
            // 블록 주석이 서 있다. `--`/`//`만 보던 종전 정규식은 이것을 못 봤다.
            var plan = string.Join("\n",
                "```sql",
                "        SET @v_currentStepId = -21;",
                "        /* UPDATE 13: CLVTType=1 금액 재배치.",
                "           WHERE YMD=@pi_strYMD",
                "             AND CLVTType=1 */",
                "```");

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldNotFlagBlockCommentThatOnlyAnnotatesRealDml()
        {
            // 앵커 주석(`/* U13: ... */`)은 뒤에 실제 DML 이 서 있으면 생략이 아니다.
            // 이 경계가 없으면 규칙 준수 문서가 통째로 발화한다.
            var plan = string.Join("\n",
                "```sql",
                "        /* U13: CLVTType=1 금액 재배치 */",
                "        UPDATE SETTLE_POQ_DB.dbo.TSettleMst",
                "        SET CLVT = 0",
                "        WHERE YMD = @pi_strYMD;",
                "```");

            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldFlagOmissionEvenWhenCommentSaysPreserve()
        {
            // 종전 PreservationMarkers 화이트리스트가 면제하던 자리다. "유지한다"가
            // 붙어 있어도, 그 주석이 선 자리에 실행 가능한 DML 이 없으면 생략이다.
            var plan = string.Join("\n",
                "```sql",
                "        /* UPDATE 4: 고객사 최저수수료. 원본 SET 산식을 그대로 유지한다. */",
                "```");

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldNotFlagRangeWordThatMerelyContainsTheCharacterMeaningAbove()
        {
            // 실제 산출물(BatchMigrationPlan.md)에서 발견한 오탐. "위\s.*동일" 패턴에
            // 단어 경계가 없어 "범위"의 "위"에도 걸려 "동일"과 짝지어졌다 - "위"가
            // "위(above)"를 가리키는 게 아니라 "범위(scope)"의 일부일 때도 발화했다.
            var plan = string.Join("\n",
                "```sql",
                "-- SQL_RESTORE2_DELETE (범위 삭제 후 복원 - 동일 YMD만)",
                "```");

            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }
    }
}
