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
        public void Scan_ShouldFlagTheCaseThePreservationWhitelistUsedToExempt()
        {
            // PreservationMarkers 화이트리스트를 지운 직접 회귀 가드다. 지우기 전에는
            // "유지한다"가 붙어 있다는 이유만으로 이 줄이 면제됐다("나머지...같은"
            // 패턴과도 동시에 일치하는데도). 이제는 발화해야 한다 - 위
            // Scan_ShouldNotFlagInstructionCommentsThatDemandPreservation 의 세 번째
            // [InlineData] 로 있다가 2026-09-05 에 제거된 바로 그 사례다.
            var plan = "```sql\nSELECT 1;\n    -- 나머지 컬럼도 같은 방식으로 유지한다\n```";

            Assert.Single(OmissionCommentScanner.Scan(plan));
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

        [Fact]
        public void Scan_ShouldFlagAbbreviatedUpdateLabelWithNoDmlVerbOrClauseWords()
        {
            // 실제 산출물(S07.md:143-144)의 실제 모양. "UPDATE"·"WHERE"/"SET" 같은
            // DML 낱말이 전혀 없고 갱신 번호 약칭(`U4:`)과 한글 산문뿐이다.
            // DmlVerbRegex && DmlClauseRegex 만 요구하던 종전 판별자는 이것을 건너뛴다.
            var plan = string.Join("\n",
                "```sql",
                "        SET @v_currentStepId = -1;",
                "        /* U4: KFTC, YELOPAY, INIBANK, settlevacct, inivacct의 고객사 최저수수료 */",
                "```");

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldFlagAbbreviatedRangeLabelLikeU7ThroughU11()
        {
            // 실제 산출물(S07.md:148)의 모양. "U7~U11:" 처럼 범위 표기도 있다.
            var plan = string.Join("\n",
                "```sql",
                "        /* U7~U11: CheckPay, Toss, TossPoint, EasyBank, kakaopay, KakaoMoney, inivacct 예외 */",
                "```");

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldFlagEachOmittedCommentInAConsecutiveChain()
        {
            // 실제 산출물(S08.md:24-52)의 모양. 생략 주석 뒤에 곧바로 다음 블록
            // 주석이 서 있으면(그 사이 실제 DML 이 없으면), 앞 주석의 꼬리 검사가
            // "다음 주석 안의 UPDATE 낱말"을 보고 실제 문장이 뒤따른다고 오판해
            // 발화를 죽였다 - UPDATE 1 이 그 자리다.
            //
            // UPDATE 2 도 발화해야 한다. UPDATE 2 는 이미 자신의 단계 표식(-2)을
            // 달고 있으므로, 그 뒤에 또 다른 표식(-4)이 나오고서야 진짜 UPDATE 가
            // 나타난다면 그 UPDATE 는 새로 시작한 다음 단계(원본 문서상 "UPDATE
            // 3")에 속한다 - UPDATE 2 자신의 문장이 아니다(OmissionCommentScanner의
            // PrecededByStepIdMarker 참고). 이 구분이 없으면 [라운드 2]가 그랬듯
            // 표식을 무조건 건너뛰어 UPDATE 2를 다시 놓치거나, [라운드 1]이 그랬듯
            // 표식을 무조건 멈춤으로 삼아 S07 의 정상 완료 자리(주석; 자기 표식;
            // 진짜 DML)를 오탐한다.
            var plan = string.Join("\n",
                "```sql",
                "        SET @v_currentStepId = -1;",
                "        /* UPDATE 1: ALLTHEGATE, NICECARD 할부이자. */",
                "",
                "        SET @v_currentStepId = -2;",
                "        /* UPDATE 2: 해외카드 수수료. */",
                "",
                "        SET @v_currentStepId = -4;",
                "        UPDATE SETTLE_POQ_DB.dbo.TSettleMst",
                "        SET TXAMT = TXAMT * (-1)",
                "        WHERE YMD = @pi_strYMD;",
                "```");

            Assert.Equal(2, OmissionCommentScanner.Scan(plan).Count);
        }

        [Fact]
        public void Scan_ShouldNotFlagOmissionWhenRealDmlFollowsAfterStepIdMarker()
        {
            // 실제 산출물(S07.md)에서 리뷰어가 최소 재현한 오탐. 라운드 1 이
            // "SET @v_currentStepId = N;"을 정지 신호(연쇄 억제 버그를 고치려고)로
            // 바꿨는데, 그 표식은 사실 원본이 "실행 DML 직전에" 남기는 오류 추적
            // 관용구다(S08.md 서두) - 정지 신호로 삼으면 정상 완료된 자리를 전부
            // 생략으로 고발한다. 이 표식은 건너뛰고 계속 봐야 한다("--"처럼).
            var plan = string.Join("\n",
                "```sql",
                "        /* U1: 비원천 PG 프로모션 할인 */",
                "        SET @v_currentStepId = -101;",
                "        UPDATE A SET A.DiscountFlag = 'Y' FROM T A WHERE A.YMD = @pi_strYMD;",
                "```");

            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldFlagOmissionWhenAnotherStepMarkerInterruptsBeforeRealDml()
        {
            // 실제 산출물(S07.md:240-244, U15)의 모양. 이 주석은 <b>자신의</b>
            // 단계 표식(-21)을 이미 앞에 달고 있다. 그 뒤에 <b>또 다른</b> 표식(-27)이
            // 나오고서야 진짜 DML 이 나타난다면, 그 DML 은 새로 시작한 다음 단계에
            // 속한다 - 이 주석 자신의 문장이 아니다. 표식 하나만 건너뛰는 것만으로는
            // 이 자리를 못 잡는다("리뷰어 최소 재현"과 모양이 같아 보이지만, 그
            // 재현은 주석이 자기 표식을 앞에 달고 있지 않다는 점이 다르다).
            var plan = string.Join("\n",
                "```sql",
                "        SET @v_currentStepId = -21;",
                "        /* U15: impaymobile과 TClientSettleRate4MobileCo 결합 수수료 */",
                "",
                "        SET @v_currentStepId = -27;",
                "        UPDATE SETTLE_POQ_DB.dbo.TSettleMst",
                "        SET CardAmt = TxAmt",
                "        WHERE YMD = @pi_strYMD AND PGName = 'payco';",
                "```");

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldNotFlagInstructionCommentAnchoredByRealSelect()
        {
            // 실제 산출물(POQSettleProc19/BatchMigrationPlan.md:7247-7252)의 오탐.
            // 이 주석은 규칙 준수를 설명하는 산문("활성 DataReader 상태에서 UPDATE
            // 또는 INSERT를 발행하지 않는다")이라 DmlVerbRegex·DmlClauseRegex(SELECT)
            // 를 우연히 만족시킨다. 그 뒤에는 실제로 생략된 DML 이 아니라 진짜 SELECT
            // 문이 그대로 서 있다 - 종전에는 UPDATE/INSERT/DELETE 만 앵커로 인정해
            // SELECT 로 시작하는 문장은 영원히 앵커를 못 찾았다.
            var plan = string.Join("\n",
                "```sql",
                "    /*",
                "      C#은 아래 SELECT 결과를 List<Group>으로 먼저 적재한 뒤,",
                "      같은 연결 및 같은 트랜잭션에서 각 그룹을 순차 처리한다.",
                "      활성 DataReader 상태에서 UPDATE 또는 INSERT를 발행하지 않는다.",
                "    */",
                "    SELECT",
                "        A.ClientID, A.YMD",
                "    FROM SETTLE_POQ_DB.dbo.TSettleMst AS A",
                "    WHERE A.YMD = @pi_strYMD;",
                "```");

            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }
    }
}
