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
    }
}
