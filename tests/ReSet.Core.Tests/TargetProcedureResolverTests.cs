using System.Collections.Generic;
using Xunit;
using ReSet.Cli;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// `--sp`로 지목한 이름을 DB 목록에 대조하는 규칙을 고정한다.
    ///
    /// 배경(2026-08-23): `--sp SETTLE_PROC_ETC`처럼 약칭을 넘기면 노란 경고 한 줄만 찍고
    /// 종료 코드 0으로 계속 진행해, 재생성이 "조용히" 건너뛰어졌다. 지목한 이름이 하나라도
    /// 안 맞으면 호출자가 그 사실을 구분할 수 있도록 미일치 목록을 따로 돌려준다.
    /// </summary>
    public class TargetProcedureResolverTests
    {
        private static readonly IReadOnlyList<string> Catalog = new[]
        {
            "dbo.UP_UTIL_SETTLE_PROC_ETC",
            "dbo.UP_UTIL_SETTLE_INS",
            "rpt.UP_UTIL_SETTLE_INS",
        };

        [Fact]
        public void Resolve_FullNameWithSchema_MatchesIgnoringCase()
        {
            var r = TargetProcedureResolver.Resolve(new[] { "DBO.up_util_settle_proc_etc" }, Catalog);

            Assert.Equal(new[] { "dbo.UP_UTIL_SETTLE_PROC_ETC" }, r.Matched);
            Assert.Empty(r.Unmatched);
        }

        [Fact]
        public void Resolve_NameOnly_MatchesFirstCatalogEntryWithThatName()
        {
            var r = TargetProcedureResolver.Resolve(new[] { "UP_UTIL_SETTLE_INS" }, Catalog);

            Assert.Equal(new[] { "dbo.UP_UTIL_SETTLE_INS" }, r.Matched);
            Assert.Empty(r.Unmatched);
        }

        [Fact]
        public void Resolve_Abbreviation_IsReportedUnmatched_NotSilentlyDropped()
        {
            // 실제로 건너뛰어진 모양 - 전체 이름의 꼬리만 넘긴 경우다.
            var r = TargetProcedureResolver.Resolve(new[] { "SETTLE_PROC_ETC" }, Catalog);

            Assert.Empty(r.Matched);
            Assert.Equal(new[] { "SETTLE_PROC_ETC" }, r.Unmatched);
        }

        [Fact]
        public void Resolve_MixedInput_KeepsOrderAndSeparatesUnmatched()
        {
            var r = TargetProcedureResolver.Resolve(
                new[] { "UP_UTIL_SETTLE_INS", "NOPE", "dbo.UP_UTIL_SETTLE_PROC_ETC", "ALSO_NOPE" }, Catalog);

            Assert.Equal(new[] { "dbo.UP_UTIL_SETTLE_INS", "dbo.UP_UTIL_SETTLE_PROC_ETC" }, r.Matched);
            Assert.Equal(new[] { "NOPE", "ALSO_NOPE" }, r.Unmatched);
        }

        [Fact]
        public void Resolve_UnmatchedSchemaQualifiedName_IsNotRescuedByNameOnlyMatch()
        {
            // 스키마까지 적었으면 그 스키마로만 찾는다 - 'xyz.UP_UTIL_SETTLE_INS'가
            // dbo 쪽에 조용히 붙으면 안 된다.
            var r = TargetProcedureResolver.Resolve(new[] { "xyz.UP_UTIL_SETTLE_INS" }, Catalog);

            Assert.Empty(r.Matched);
            Assert.Equal(new[] { "xyz.UP_UTIL_SETTLE_INS" }, r.Unmatched);
        }
    }
}
