using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementCodebookMatchingTests
    {
        private static SettlementCodebook LeftSide(params CodebookEntry[] entries) =>
            new(entries, Array.Empty<string>());

        private static CodebookEntry Entry(string value, string? column, bool eligible = true) =>
            new(value, column, new[] { "dbo.A" }, eligible, Array.Empty<CodebookMatch>());

        // 프로덕션의 CodeTableProfiler.ToStringRow가 OrdinalIgnoreCase로 행 딕셔너리를
        // 만든다(ApplyMatches의 1단이 그 계약에 기댄다는 "주의" 주석이 있다). 이 대역이
        // 기본(대소문자 구분) 비교자를 쓰면 실물과 다른 행을 시험하는 것이라 계약이
        // 한 번도 실행되지 않는다.
        private static ProfiledTable Table(string name, params (string Col, string Val)[][] rows) =>
            new(name, rows
                .Select(r => (IReadOnlyDictionary<string, string>)r.ToDictionary(
                    c => c.Col, c => c.Val, StringComparer.OrdinalIgnoreCase))
                .ToList());

        [Fact]
        public void 컬럼을_아는_값은_같은_이름의_컬럼에서_찾는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("impaymobile", "PayMethod")),
                new[]
                {
                    Table("dbo.TCode",
                        new[] { ("PayMethod", "impaymobile"), ("Name", "간편결제") }),
                });

            var match = Assert.Single(Assert.Single(book.Entries).Matches);
            Assert.Equal("dbo.TCode", match.Table);
            Assert.Equal("간편결제", match.Row["Name"]);
        }

        [Fact]
        public void 컬럼을_모르는_값은_아무_문자열_칸에서나_찾는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("payco", column: null)),
                new[] { Table("dbo.TPg", new[] { ("PgCode", "payco"), ("PgName", "페이코") }) });

            Assert.Single(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 매칭_대상이_아닌_값은_아예_찾지_않는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("Y", "UseFlag", eligible: false)),
                new[] { Table("dbo.TAny", new[] { ("UseFlag", "Y"), ("Name", "사용") }) });

            Assert.Empty(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 대소문자를_무시하고_찾는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("NICECARD", column: null)),
                new[] { Table("dbo.TPg", new[] { ("PgCode", "nicecard") }) });

            Assert.Single(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 여러_테이블에서_나오면_전부_담고_출처를_남긴다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("payco", column: null)),
                new[]
                {
                    Table("dbo.TPg", new[] { ("PgCode", "payco") }),
                    Table("dbo.TMall", new[] { ("PgCode", "payco") }),
                });

            var matches = Assert.Single(book.Entries).Matches;
            Assert.Equal(2, matches.Count);
            Assert.Contains(matches, m => m.Table == "dbo.TPg");
            Assert.Contains(matches, m => m.Table == "dbo.TMall");
        }

        [Fact]
        public void 프로파일링_결과가_없으면_전량_미매칭으로_남는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("impaymobile", "PayMethod")), Array.Empty<ProfiledTable>());

            Assert.Empty(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 부분_문자열은_매칭이_아니다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("payco", column: null)),
                new[] { Table("dbo.TPg", new[] { ("PgCode", "payco_extra") }) });

            Assert.Empty(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 컬럼을_아는_값은_다른_컬럼에_같은_값이_있어도_그_행을_매칭으로_치지_않는다()
        {
            // 1단(컬럼 쌍)이 잠겨 있는지를 잰다. entry.Column("PayMethod")의 실제 값은
            // "kakaopay"인데, 같은 행의 다른 컬럼(Memo)에 우연히 "impaymobile"이
            // 적혀 있다. 값만으로 행 전체를 뒤지는 2단으로 새면 이 행이 걸리지만,
            // 컬럼을 아는 값은 그 컬럼만 봐야 하므로 걸리면 안 된다.
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("impaymobile", "PayMethod")),
                new[]
                {
                    Table("dbo.TCode",
                        new[] { ("PayMethod", "kakaopay"), ("Memo", "impaymobile") }),
                });

            Assert.Empty(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 컬럼_이름의_대소문자가_달라도_같은_컬럼으로_찾는다()
        {
            // entry.Column은 "PayMethod"인데 행 딕셔너리의 실제 키는 "paymethod"다.
            // 프로덕션 행 딕셔너리가 OrdinalIgnoreCase가 아니면 TryGetValue가 실패해
            // 이 값이 컬럼을 아는 값인데도 미매칭으로 빠진다.
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("impaymobile", "PayMethod")),
                new[] { Table("dbo.TCode", new[] { ("paymethod", "impaymobile") }) });

            Assert.Single(Assert.Single(book.Entries).Matches);
        }
    }
}
