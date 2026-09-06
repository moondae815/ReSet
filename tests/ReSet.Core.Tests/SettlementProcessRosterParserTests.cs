using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementProcessRosterParserTests
    {
        private const string Roster = @"# 정산 프로세스 명부
<!-- 도구는 이 파일이 없을 때만 초안을 만듭니다. -->

## 1. 수수료율 스냅샷 적재
<!-- [기계 확정] 5개 SP가 이 산출을 읽습니다 -->
- dbo.UP_Util_PG_Client_CMRate_Ins

## 2. 정산 원장 적재
- dbo.UP_UTIL_SETTLE_INS
- dbo.UP_UTIL_SETTLE_INS_EXTRA

## 제외
- dbo.UP_UTIL_STAT_PGCOLLECT_INS
";

        [Fact]
        public void 단계를_문서_등장_순서대로_읽는다()
        {
            var roster = SettlementProcessRosterParser.Parse(Roster);

            Assert.Equal(2, roster.Stages.Count);
            Assert.Equal("1. 수수료율 스냅샷 적재", roster.Stages[0].Title);
            Assert.Equal("2. 정산 원장 적재", roster.Stages[1].Title);
        }

        [Fact]
        public void 단계별_소속_SP를_읽는다()
        {
            var roster = SettlementProcessRosterParser.Parse(Roster);

            Assert.Equal(new[] { "dbo.UP_Util_PG_Client_CMRate_Ins" }, roster.Stages[0].Procedures);
            Assert.Equal(
                new[] { "dbo.UP_UTIL_SETTLE_INS", "dbo.UP_UTIL_SETTLE_INS_EXTRA" },
                roster.Stages[1].Procedures);
        }

        [Fact]
        public void 제외_섹션은_단계가_아니라_제외목록으로_읽는다()
        {
            var roster = SettlementProcessRosterParser.Parse(Roster);

            Assert.Equal(new[] { "dbo.UP_UTIL_STAT_PGCOLLECT_INS" }, roster.Excluded);
            Assert.DoesNotContain(roster.Stages, s => s.Title.Contains("제외"));
        }

        [Fact]
        public void 주석줄은_SP로_읽지_않는다()
        {
            var roster = SettlementProcessRosterParser.Parse(Roster);

            Assert.DoesNotContain(roster.Stages[0].Procedures, p => p.Contains("기계 확정"));
        }

        [Fact]
        public void 빈_문서는_단계도_제외도_없다()
        {
            var roster = SettlementProcessRosterParser.Parse(null);

            Assert.Empty(roster.Stages);
            Assert.Empty(roster.Excluded);
        }

        // 회귀: 사람이 단계를 통째로 주석으로 꺼두는 편집(여러 줄 <!-- ... --> 블록)을
        // 흉내낸다. 블록 상태가 없으면 블록 안의 "- " 줄이 살아 있는 프로시저로 읽힌다 -
        // 사람이 껐다고 믿는 것이 조용히 켜진 채로 명부에 들어가는 결함(리뷰 Important).
        [Fact]
        public void 여러줄_주석_블록_안의_항목은_프로시저로_읽지_않는다()
        {
            const string markdown = @"## 1. 단계
<!-- 이번 달은 안 돕니다
- dbo.UP_UTIL_SETTLE_INS
-->
- dbo.UP_A
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Single(roster.Stages);
            Assert.Equal(new[] { "dbo.UP_A" }, roster.Stages[0].Procedures);
        }

        // 주석 블록 안에 "## " 로 시작하는 줄이 있어도 헤딩 분기보다 주석 상태 검사가
        // 먼저 걸려야 한다 - 그렇지 않으면 주석 안에 적어 둔 메모용 헤딩이 진짜 단계로
        // 둔갑한다.
        [Fact]
        public void 주석_블록_안의_헤딩은_단계로_읽지_않는다()
        {
            const string markdown = @"## 1. 단계
- dbo.UP_A
<!-- 임시 보류
## 3. 임시 단계
- dbo.UP_TEMP
-->
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Single(roster.Stages);
            Assert.DoesNotContain(roster.Stages, s => s.Title.Contains("임시"));
        }

        // 음성 예: 한 줄 안에서 열고 닫히는 주석(`<!-- 메모 -->`)은 블록 상태를 켜면
        // 안 된다. 켜지면 그 뒤에 오는 정상 항목까지 통째로 삼켜지는 반대 결함이 생긴다.
        [Fact]
        public void 한줄_주석_뒤의_항목은_정상으로_읽힌다()
        {
            const string markdown = @"## 1. 단계
<!-- 메모 -->
- dbo.UP_A
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Equal(new[] { "dbo.UP_A" }, roster.Stages[0].Procedures);
        }

        // 첫 H2 이전에 등장한 "- " 항목은 소속될 단계가 없어 조용히 버려진다.
        // 이것은 버그가 아니라 의도된 동작임을 이름과 이 주석으로 못박는다(리뷰 Minor ①).
        [Fact]
        public void 첫_단계_이전의_항목은_조용히_버려진다()
        {
            const string markdown = @"# 정산 프로세스 명부
- dbo.UP_ORPHAN

## 1. 단계
- dbo.UP_A
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Single(roster.Stages);
            Assert.Equal(new[] { "dbo.UP_A" }, roster.Stages[0].Procedures);
            Assert.DoesNotContain("dbo.UP_ORPHAN", roster.AllStagedProcedures());
        }

        // `## 제외`가 문서 중간에 있고 그 뒤에 다른 단계가 더 있으면, 뒤에 오는 단계의
        // 항목이 제외 목록으로 새지 않고 정상적으로 자기 단계에 귀속되어야 한다
        // (리뷰 Minor ②).
        [Fact]
        public void 제외_섹션_뒤에_오는_단계는_제외로_새지_않는다()
        {
            const string markdown = @"## 1. 단계
- dbo.UP_A

## 제외
- dbo.UP_EXCLUDED

## 2. 단계
- dbo.UP_B
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Equal(2, roster.Stages.Count);
            Assert.Equal("2. 단계", roster.Stages[1].Title);
            Assert.Equal(new[] { "dbo.UP_B" }, roster.Stages[1].Procedures);
            Assert.Equal(new[] { "dbo.UP_EXCLUDED" }, roster.Excluded);
        }

        // 닫히지 않은 주석(`-->` 없이 문서가 끝남)은 MarkdownSectionLocator.ComputeFenceFlags와
        // 같은 판단을 따른다: 주석 상태를 신뢰할 수 없으므로 전부 무시한다. 오탐(주석
        // 아닌 것을 주석으로 오인)보다 미탐(문서 나머지 전부가 통째로 삼켜지는 것)이
        // 훨씬 나쁘기 때문이다.
        [Fact]
        public void 닫히지_않은_주석은_전체를_무시하고_이후_내용을_정상으로_읽는다()
        {
            const string markdown = @"<!-- 열렸지만 안 닫힘
## 1. 단계
- dbo.UP_A
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Single(roster.Stages);
            Assert.Equal("1. 단계", roster.Stages[0].Title);
            Assert.Equal(new[] { "dbo.UP_A" }, roster.Stages[0].Procedures);
        }

        // 목록 표식(`- `) 뒤에 숨은 한 줄짜리 주석 항목("- <!-- 메모 -->")은 프로시저로
        // 읽히지 않는다.
        //
        // [무엇을 보증하고 무엇을 보증하지 않는가] 이것은 두 장치의 겹침을 특성화하는
        // 테스트지 스캐너의 보증이 아니다. 이 입력은 본 루프의 item.StartsWith("<!--")
        // 가드와 ComputeCommentBlockFlags의 목록 표식 벗기기가 각각 독립으로 막는다.
        // 그래서 스캐너의 한 줄 처리가 회귀해도 이 테스트는 통과한다 - 스캐너 단독의
        // 회귀를 잡는 것은 아래 여러 줄 표본 두 건(...여는_여러줄_주석_블록_안의_항목...,
        // ...닫힌_뒤의_항목...)뿐이다. 이 테스트를 스캐너의 회귀 감시로 오해하지 마라.
        //
        // [정정] 라운드 2의 커밋 메시지(163c52f9)는 "목록 표식 벗기기를 되돌리면 신규
        // 테스트 3건이 전부 실패한다"고 기록했다. 라운드 3에서 가드를 되살린 뒤로 그
        // 수는 3이 아니라 2다 - 되살아난 가드가 이 테스트를 홀로 만족시켜 변이를 죽이지
        // 못하게 됐기 때문이다. 커밋 메시지는 고칠 수 없으므로 정정을 여기 남긴다.
        // 겹침 자체는 의도된 것이고 없애지 않는다 - 근거와 실측은 파서의 가드 주석에 있다.
        [Fact]
        public void 목록_표식_뒤의_한줄_주석_항목은_프로시저로_읽지_않는다()
        {
            const string markdown = @"## 1. 단계
- <!-- 메모 -->
- dbo.UP_A
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Equal(new[] { "dbo.UP_A" }, roster.Stages[0].Procedures);
        }

        // 회귀: "- <!-- 여러 줄" 처럼 목록 표식으로 시작하는 줄이 여러 줄 주석 블록을
        // 열 수도 있다. 이 열림을 인식하지 못하면 블록 안의 "- dbo.UP_X"가 살아 있는
        // 프로시저로 샌다(리뷰의 「연기된 관찰」).
        [Fact]
        public void 목록_표식으로_여는_여러줄_주석_블록_안의_항목은_프로시저로_읽지_않는다()
        {
            const string markdown = @"## 1. 단계
- <!-- 여러 줄
- dbo.UP_X
-->
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Empty(roster.Stages[0].Procedures);
        }

        // 음성 예: 위 블록이 "-->"로 제대로 닫힌 뒤에 오는 항목은 정상으로 읽혀야
        // 한다 - 블록이 안 꺼져 뒷부분을 통째로 삼키는 반대 결함을 막는다.
        [Fact]
        public void 목록_표식으로_연_주석_블록이_닫힌_뒤의_항목은_정상으로_읽힌다()
        {
            const string markdown = @"## 1. 단계
- <!-- 여러 줄
- dbo.UP_X
-->
- dbo.UP_Y
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Equal(new[] { "dbo.UP_Y" }, roster.Stages[0].Procedures);
        }

        // 회귀(라운드 3): 목록 표식으로 연 주석이 문서 끝까지 안 닫히면 닫힘-폴백이
        // 주석 플래그를 전부 지운다. 그러면 본 루프가 그 줄을 평범한 항목으로 다시
        // 읽어 "<!-- unclosed" 라는 문자열 자체가 프로시저로 명부에 들어간다.
        // item.StartsWith("<!--") 가드는 폴백과 독립적으로 이 자리를 막는다 - 줄에
        // "<!--"가 문자 그대로 있다는 사실은 폴백이 플래그를 지우든 말든 모호하지
        // 않기 때문이다. 2026-09-06 라운드 2 에서 이 가드를 잉여로 보고 지웠다가
        // 이 회귀를 만들었다 - 그때의 제거 증명(14건)에는 이 표본이 없었다.
        [Fact]
        public void 목록_표식으로_열고_안_닫힌_주석은_프로시저로_읽지_않는다()
        {
            const string markdown = @"## 1. 단계
- <!-- unclosed
- dbo.UP_A
";
            var roster = SettlementProcessRosterParser.Parse(markdown);

            Assert.Equal(new[] { "dbo.UP_A" }, roster.Stages[0].Procedures);
        }
    }
}
