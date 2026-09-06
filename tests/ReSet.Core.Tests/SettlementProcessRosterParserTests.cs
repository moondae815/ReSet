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
    }
}
