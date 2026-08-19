using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecConditionColumnExtractorTests
    {
        private static List<(string FileName, string Content)> Spec(string content) =>
            new() { ("dbo.UP_UTIL_SETTLE_EXPECT_PROC", content) };

        [Fact]
        public void Extract_ShouldAttributeConditionsUnderAUdfHeadingToThatUdf()
        {
            // 실측(POQSettleProc13): 첫 구현이 낸 15건이 거의 전부 이것이었다. 명세서는
            // 프로시저 본체 조건과 그 프로시저가 호출하는 UDF의 내부 조건을 같은 CRUD
            // 분석 섹션에 나란히 적는다. UDF 조건은 본문이 그 UDF를 호출하기만 하면
            // 옮겨 적을 이유가 없는데, 구별하지 않으면 전부 누락으로 잡힌다.
            var content = """
                #### `dbo.UIF_SettleYMD`

                | 항목 | 분석 내용 |
                | :--- | :--- |
                | 주기 조회 | `SettleState = 1`, `SettleTarget = 1` 인 행을 조회합니다. |
                """;

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            var conditions = result["UP_UTIL_SETTLE_EXPECT_PROC"];
            Assert.Empty(conditions.BodyColumns);
            Assert.Equal(
                new[] { "SettleState", "SettleTarget" },
                conditions.ByUdf["UIF_SettleYMD"].OrderBy(x => x).ToArray());
        }

        [Fact]
        public void Extract_ShouldAttributeConditionsOnARowNamingAUdfToThatUdf()
        {
            // 헤딩이 아니라 표 행 안에서 UDF를 밝히는 형태도 있다.
            var content =
                "| `dbo.UF_GET_COLLECTYMD` | `SETTLE_POQ_DB.dbo.THoliday` | `CollectFlag = 1` | 영업일 계산 |";

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            var conditions = result["UP_UTIL_SETTLE_EXPECT_PROC"];
            Assert.Empty(conditions.BodyColumns);
            Assert.Equal(new[] { "CollectFlag" }, conditions.ByUdf["UF_GET_COLLECTYMD"]);
        }

        [Fact]
        public void Extract_ShouldNotReassignARowToAUdfItMerelyMentionsAsACallee()
        {
            // 실측: `| 유동 일 주기 | HolidayProcFlag = 2 이면 ... 그렇지 않으면
            // dbo.UF_GET_WORKDAY2를 호출합니다. |` 행은 UF_GET_COLLECTYMD 절 안에 있고
            // 그 조건도 COLLECTYMD의 것인데, 문장 중간에 언급된 피호출 UDF에게 소유권이
            // 넘어가 단계가 호출하지도 않는 UDF의 조건으로 잡혔다 - 오탐 마지막 1건.
            var content = """
                #### `dbo.UF_GET_COLLECTYMD`

                | 항목 | 분석 내용 |
                | :--- | :--- |
                | 유동 일 주기 | `HolidayProcFlag = 2`이면 달력일을 더하고, 그렇지 않으면 `dbo.UF_GET_WORKDAY2`를 호출합니다. |
                """;

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            var conditions = result["UP_UTIL_SETTLE_EXPECT_PROC"];
            Assert.Equal(new[] { "HolidayProcFlag" }, conditions.ByUdf["UF_GET_COLLECTYMD"]);
            Assert.DoesNotContain("UF_GET_WORKDAY2", conditions.ByUdf.Keys);
        }

        [Fact]
        public void Extract_ShouldIgnoreInsertColumnMappingsWhichAreValuesNotFilters()
        {
            // 실측: `### INSERT 대상 테이블:` 절의 표는 대상 컬럼에 무엇을 넣는지를
            // 적는다. `X.PGINCVTAX = 1`은 "PGVTTYPE에 상수 1을 저장한다"는 뜻이지
            // 거르는 조건이 아닌데, 문법이 같아 조건으로 오인됐다.
            var content = """
                ### INSERT 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst

                | 대상 | 컬럼 | 원천 표현식 | 설명 |
                | :--- | :--- | :--- | :--- |
                | `TSettleMst` | PGVTTYPE | `X.PGINCVTAX = 1` | 상수 1을 저장합니다. |
                """;

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldTreatConditionsOutsideAnyUdfContextAsTheProcedureBody()
        {
            var content = """
                #### 본체 처리

                | 단계 | 내용 |
                | :--- | :--- |
                | 대상 선정 | `UseState = 0` 인 행만 갱신합니다. |
                """;

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            var conditions = result["UP_UTIL_SETTLE_EXPECT_PROC"];
            Assert.Equal(new[] { "UseState" }, conditions.BodyColumns);
            Assert.Empty(conditions.ByUdf);
        }

        [Fact]
        public void Extract_ShouldEndAUdfContextAtTheNextHeading()
        {
            // UDF 절이 끝났는데 컨텍스트가 남으면, 그 뒤의 본체 조건이 전부 면제된다.
            var content = """
                #### `dbo.UIF_SettleYMD`

                | 주기 조회 | `SettleState = 1` |

                #### 본체 처리

                | 대상 선정 | `UseState = 0` |
                """;

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            var conditions = result["UP_UTIL_SETTLE_EXPECT_PROC"];
            Assert.Equal(new[] { "UseState" }, conditions.BodyColumns);
            Assert.Equal(new[] { "SettleState" }, conditions.ByUdf["UIF_SettleYMD"]);
        }

        [Fact]
        public void Extract_ShouldNotTakeAnIndexHintAsAColumn()
        {
            // 실측: `INDEX=CIDX_TTxMst_YMD`는 조건이 아니라 인덱스 힌트다.
            var result = SpecConditionColumnExtractor.Extract(Spec("`INDEX=CIDX_TTxMst_YMD` 힌트를 붙입니다."));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldTakeTheColumnOfABacktickedCondition()
        {
            // 실측(POQSettleProc13): S09가 `SettleTarget = 1` 필터를 통째로 빠뜨렸는데
            // 기계 검증은 전부 통과했다. 대상 테이블도 오류코드도 맞았고, 아무도
            // "그 컬럼으로 거르는 로직이 있는가"를 묻지 않았기 때문이다.
            var result = SpecConditionColumnExtractor.Extract(
                Spec("정산 대상은 `SettleTarget = 1` 인 행만 선택합니다."));

            Assert.Equal(new[] { "SettleTarget" }, result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        [Fact]
        public void Extract_ShouldStripAnAliasQualifier()
        {
            // 명세서는 같은 컬럼을 별칭과 함께 쓰기도 한다. 별칭까지 이름으로 삼으면
            // 본문이 다른 별칭을 쓸 때 전부 누락으로 잡힌다 - 실측에서 이 하나로
            // 오탐이 27%까지 올라갔다.
            var result = SpecConditionColumnExtractor.Extract(
                Spec("`A.PGNAME IN ('easybank')` 조건을 적용합니다."));

            Assert.Equal(new[] { "PGNAME" }, result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        [Fact]
        public void Extract_ShouldIgnoreProcedureParameters()
        {
            // @pi_strYMD 같은 입력 파라미터는 모든 명세서에 나오고 단계 본문도 자기
            // 이름으로 부른다. 대조 대상으로 삼으면 신호가 아니라 배경이 된다.
            var result = SpecConditionColumnExtractor.Extract(
                Spec("`A.YMD = @pi_strYMD` 로 거릅니다."));

            Assert.Equal(new[] { "YMD" }, result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        [Fact]
        public void Extract_ShouldNotCreateAKeyWhenTheSpecHasNoCondition()
        {
            // 빈 목록과 "그런 프로시저 없음"이 같아지면, 대조 0건이 통과로 읽힌다.
            // SpecReturnCodeExtractor가 같은 이유로 키를 만들지 않는다.
            var result = SpecConditionColumnExtractor.Extract(Spec("조건 표기가 없는 산문입니다."));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldMergeCodesForTheSameProcedureInsteadOfOverwriting()
        {
            var specs = new List<(string FileName, string Content)>
            {
                ("dbo.UP_A", "`SettleState = 1`"),
                ("dbo.UP_A", "`SettleTarget = 1`")
            };

            var result = SpecConditionColumnExtractor.Extract(specs);

            Assert.Equal(new[] { "SettleState", "SettleTarget" }, result["UP_A"].BodyColumns.OrderBy(x => x).ToArray());
        }

        [Theory]
        [InlineData("`BEGIN TRAN`")]          // BEG + IN 으로 잘린다
        [InlineData("`TSettleByIN` 테이블")]   // TSettleBy + IN
        [InlineData("`WOWCOIN` 결제수단")]      // WOWCO + IN
        public void Extract_ShouldNotSplitAWordThatMerelyEndsWithIn(string content)
        {
            // 실측: 첫 구현을 Proc13에 돌리자 27건이 나왔는데 그중 15건이 이 한 가지
            // 버그였다. IN 앞에 단어 경계를 요구하지 않아 BEGIN·TSettleByIN 같은 낱말이
            // 컬럼과 연산자로 쪼개졌다. 오탐은 단계 재생성을 유발하므로 비용이 실재한다.
            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldNotSplitNotInIntoAColumnAndAnOperator()
        {
            // 실측(POQSettleProc14): S07이 `NOT`(으)로 거르는 로직이 없다는 결함을 받아
            // 재생성 1회를 태웠다. `NOT IN`의 NOT이 컬럼으로 잡힌 것이고, BEGIN이
            // BEG+IN으로 쪼개지던 것과 같은 뿌리다.
            var result = SpecConditionColumnExtractor.Extract(Spec("`NOT IN (0,1)` 인 행은 제외합니다."));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldReadTheColumnOfANotInCondition()
        {
            // 같은 뿌리의 반대편 손실. NOT을 연산자의 일부로 읽지 못하면 이 형태가
            // 통째로 추출되지 않아, 원본이 실제로 거르는 컬럼을 놓친다.
            var result = SpecConditionColumnExtractor.Extract(Spec("`A.UseState NOT IN (0)` 조건을 적용합니다."));

            Assert.Equal(new[] { "UseState" }, result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        [Fact]
        public void Extract_ShouldStillReadAGenuineInCondition()
        {
            // 위 가드가 IN 조건 자체를 죽이면 안 된다.
            var result = SpecConditionColumnExtractor.Extract(Spec("`UseState IN (0,2)`"));

            Assert.Equal(new[] { "UseState" }, result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        [Fact]
        public void Extract_ShouldDropVeryShortNames()
        {
            // 세 글자 이하는 별칭·약어와 구별되지 않아 대조에서 소음만 만든다.
            var result = SpecConditionColumnExtractor.Extract(Spec("`ID = 1` 과 `SettleState = 1`"));

            Assert.Equal(new[] { "SettleState" }, result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        // === 부정 문맥 배제 ==================================================
        //
        // 실측(POQSettleProc18): 원본 `UP_UTIL_SETTLE_PROC_ETC` 58행의
        // `AND C.ClientIDType <> 1`은 주석 처리되어 실행되지 않는다. 명세서도 그렇게
        // 적었는데, 추출기가 그 사실을 부정하는 문장 안의 인용까지 조건으로 수집했다.
        // 하한 검사가 "ClientIDType으로 거르는 로직이 없다"고 요구했고, 재생성된 S16이
        // 원본에 없는 필터를 활성 조건으로 넣어 내부테스트 고객사가 후취정산 대상에서
        // 빠졌다. 검사가 결함을 잡은 것이 아니라 만들어 낸 자리다.

        [Fact]
        public void Extract_ShouldIgnoreAConditionTheSentenceSaysIsNotApplied()
        {
            var content =
                "| 58 | `AND    C.ClientIDType       <> 1` | 원본에서는 줄 전체가 주석 처리되어 " +
                "실제 실행되지 않습니다. 따라서 `C.ClientIDType <> 1` 조건은 적용되지 않으며, " +
                "코드 범례는 `0:일반, 1:내부테스트용`으로 기록되어 있습니다. |";

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.False(result.ContainsKey("UP_UTIL_SETTLE_EXPECT_PROC"));
        }

        // 같은 셀 안에서 앞 문장이 "주석 처리되어 실행되지 않는다"고 말해도, 뒤 문장이
        // 밝히는 "현재 실행되는" 조건은 살아 있어야 한다(UP_UTIL_SETTLE_INS_EXTRA 296행
        // 실물). 줄 단위로 버리면 이 조건을 통째로 잃는다.
        [Fact]
        public void Extract_ShouldKeepTheLiveConditionStatedAfterADeadOne()
        {
            var content =
                "| 168 | `END,0) * IIF(C.ExtraCommFlag='Y',1,0) AS CLComm` | 이 조건식은 주석 처리되어 " +
                "실행되지 않는다. 현재 실행되는 식은 `C.ExtraSettleFlag='Y'`만 확인한다. |";

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.Equal(
                new[] { "ExtraSettleFlag" },
                result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        // 부정은 그 문장에만 걸린다. 같은 셀의 다른 문장이 적은 유효 조건은 남는다
        // (UP_UTIL_STAT_PGCOLLECT_INS 82행 실물).
        [Fact]
        public void Extract_ShouldKeepAConditionFromASiblingSentence()
        {
            var content =
                "| TStatPGCollect | AHEADSETTLEAMT | 일반 정산 거래에서 `OUTSTATE = 1`일 때의 합계를 " +
                "반영합니다. 주석 처리된 `CLIENTID IN ('PAYLETTER')` 조건은 실행 산식에 적용되지 않습니다. |";

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.Equal(
                new[] { "OUTSTATE" },
                result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        // "직접 사용되지 않으며 하위 질의에서만 사용됩니다"는 조건이 죽었다는 말이
        // 아니다(UP_UTIL_SETTLE_EXCEPTION_PROC 87행 실물). 부정 표현 목록을 넓히면
        // 여기서 살아 있는 조건을 잃는다 - `사용되지 않`을 목록에 넣지 않는 이유다.
        [Fact]
        public void Extract_ShouldKeepAConditionUsedOnlyInASubquery()
        {
            var content =
                "| `@pi_strYMD` | 처리 기준 정산일입니다. 마지막 `OutState = 9` 갱신은 최상위 " +
                "`WHERE`에 직접 사용되지 않으며 하위 질의에서만 사용됩니다. |";

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.Equal(
                new[] { "OutState" },
                result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        // 표의 코드 열과 해설 열은 서로 다른 문장이다. 셀 경계를 문장 경계로 보지
        // 않으면, 해설이 부정하는 조건과 코드 열의 인용이 한 문장으로 붙는다.
        [Fact]
        public void Extract_ShouldTreatATableCellBoundaryAsASentenceBoundary()
        {
            var content = "| `UseState = 0` | 이 조건은 적용되지 않습니다 |";

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.Equal(
                new[] { "UseState" },
                result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        // 백틱 안의 한정자 점은 문장 경계가 아니다. 경계로 삼으면 `dbo.TClient` 하나가
        // 문장을 둘로 쪼개, 뒤따르는 부정 서술이 앞 조건에 닿지 못한다.
        [Fact]
        public void Extract_ShouldNotSplitASentenceOnADottedIdentifier()
        {
            var content = "| `SETTLE_POQ_DB.dbo.TClient.UseState = 0` 조건은 적용되지 않습니다 |";

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.False(result.ContainsKey("UP_UTIL_SETTLE_EXPECT_PROC"));
        }

        // === 화살표 오인 =====================================================
        //
        // 실측(POQSettleProc19): 원본 UP_UTIL_SETTLE_CANCEL_INS 22~23행은 코드값 범례를
        // 주석으로 적는다 - `-- INCVTAX => 부가가치세(0:미포함, 1:포함)`. 명세서가 이를
        // 그대로 인용했고, 조건 정규식의 `=` 대안이 `=>`의 `=`에 매치해 범례를 필터
        // 조건으로 수집했다. 하한 검사가 "INCVTAX로 거르는 로직이 없다"고 요구해
        // S07이 두 번 재생성됐다. 원본에는 그 조건이 없다.

        [Fact]
        public void Extract_ShouldNotReadALegendArrowAsAnEqualsCondition()
        {
            var content = """
                - `INCVTAX => 부가가치세(0:미포함, 1:포함)`
                - `COMMISSIONTYPE => 정산율(0:정율, 1:정액)`
                """;

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.False(result.ContainsKey("UP_UTIL_SETTLE_EXPECT_PROC"));
        }

        // 넓히면서 진짜 비교 연산자를 잃으면 안 된다. `>=`는 `>` 다음에 `=`가 오므로
        // 화살표와 문자 순서가 반대다.
        [Theory]
        [InlineData("`UseState = 0`", "UseState")]
        [InlineData("`UseState >= 0`", "UseState")]
        [InlineData("`UseState <= 0`", "UseState")]
        [InlineData("`UseState <> 0`", "UseState")]
        public void Extract_ShouldStillReadEveryRealComparisonOperator(string quoted, string expected)
        {
            var result = SpecConditionColumnExtractor.Extract(Spec($"| 조건 | {quoted} 인 행 |"));

            Assert.Equal(new[] { expected }, result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        // === 절 단위 배제 =====================================================
        //
        // 실측(POQSettleProc19): 부정 배제가 문장 단위였을 때 `YMD`가 69회 배제 기록에
        // 올랐다. "커서 내부에서 `A.YMD = @pi_strYMD` 조건을 만족한 집계 결과로부터
        // 수신되지만, UPDATE 문 최상위 WHERE 조건에는 입력 기준일이 직접 포함되지
        // 않습니다" - 부정되는 것은 "입력 기준일이 최상위 WHERE에 있다"는 별개 사실이고
        // 인용된 조건은 앞 절에서 긍정된다. YMD는 다른 문장에서 복구되어 실피해는
        // 없었지만, 복구되지 않는 컬럼이라면 살아 있는 조건을 잃는다.

        [Fact]
        public void Extract_ShouldKeepAConditionAffirmedInTheClauseBeforeTheNegatedOne()
        {
            var content =
                "| `@v_strYMD`는 커서 내부에서 `A.YMD = @pi_strYMD` 조건을 만족한 집계 결과로부터 " +
                "수신되지만, UPDATE 문 최상위 WHERE 조건에는 입력 기준일이 직접 포함되지 않습니다. |";

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.Equal(new[] { "YMD" }, result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }

        // 같은 절 안에서 부정된 조건은 여전히 배제된다. 절을 잘게 나누면서 잡아야 할
        // 것까지 놓치면 이 검사가 유도한 유령 필터가 되돌아온다.
        [Fact]
        public void Extract_ShouldStillDropAConditionNegatedInsideItsOwnClause()
        {
            var content =
                "| 현재 실행되는 산식은 `OUTSTATE = 1` 조건을 만족할 때만 합산하며, " +
                "`CLIENTID IN ('PAYLETTER')` 조건은 현재 실행 산식에 포함되지 않습니다. |";

            var result = SpecConditionColumnExtractor.Extract(Spec(content));

            Assert.Equal(new[] { "OUTSTATE" }, result["UP_UTIL_SETTLE_EXPECT_PROC"].BodyColumns);
        }
    }
}
