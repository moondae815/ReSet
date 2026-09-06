using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementCodebookBuilderTests
    {
        private static PolicySource Source(string label, string ddl, string spec) =>
            new(label, spec, ddl, new SpStaticAnalysisResult(), Array.Empty<DependencyInfo>());

        private const string Ddl = @"
CREATE PROCEDURE dbo.UP_Test AS
BEGIN
    SELECT * FROM T WHERE PayMethod = 'impaymobile'
    SELECT * FROM T WHERE UseFlag = 'Y'
    SELECT * FROM T WHERE PGName = 'onlyInDdl'
END";

        private const string Spec = @"## 개요

결제수단이 'impaymobile'인 건을 대상으로 한다. 사용 여부는 'Y'로 판정한다.
";

        [Fact]
        public void 명세서에_등장하는_상수만_채택한다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.Contains(book.Entries, e => e.Value == "impaymobile");
            Assert.DoesNotContain(book.Entries, e => e.Value == "onlyInDdl");
        }

        [Fact]
        public void 명세서에_없는_상수는_버리지_않고_별도로_기록한다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.Contains("onlyInDdl", book.SpecUnlistedConstants);
        }

        [Fact]
        public void 좌변_컬럼을_함께_담는다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.Equal("PayMethod", Assert.Single(book.Entries, e => e.Value == "impaymobile").Column);
        }

        // 이 조건이 없으면 'Y'가 아무 테이블에서나 걸려 매칭이 잡음이 된다.
        // 실측: 실질 상수 64개 중 13개가 길이 2 이하.
        [Fact]
        public void 길이_2_이하는_매칭_대상에서_뺀다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.False(Assert.Single(book.Entries, e => e.Value == "Y").MatchEligible);
            Assert.True(Assert.Single(book.Entries, e => e.Value == "impaymobile").MatchEligible);
        }

        // MinimumMatchableLength(3)의 경계 그 자체를 잠근다. 위 테스트의 'Y'(길이 1)와
        // 'impaymobile'(길이 11)만으로는 임계값이 3이든 2든 결과가 똑같아 임계값의
        // 값 자체는 안 잠긴다(실측: MinimumMatchableLength를 2로 낮춰도 위 테스트는
        // 전부 초록이었다). 길이 2인 값을 별도로 두어 3에서는 배제, 2로 낮추면
        // 포함되도록 경계 위에 표본을 세운다. 실물 코퍼스에도 길이 2 값이 실재한다
        // (RecordGB = 'DX', 2026-09-06 Step 6 실측).
        [Fact]
        public void 길이_2인_값은_임계값_3에서_매칭_대상이_아니다()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.UP_Boundary AS
BEGIN
    SELECT * FROM T WHERE RecordGB = 'DX'
END";
            const string spec = @"## 개요

레코드 구분은 'DX'로 표기한다.
";

            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", ddl, spec) });

            Assert.False(Assert.Single(book.Entries, e => e.Value == "DX").MatchEligible);
        }

        [Fact]
        public void 여러_SP에_나오는_상수는_한_항목에_출처를_모은다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[]
            {
                Source("dbo.A", Ddl, Spec),
                Source("dbo.B", Ddl, Spec),
            });

            var entry = Assert.Single(book.Entries, e => e.Value == "impaymobile");
            Assert.Equal(new[] { "dbo.A", "dbo.B" }, entry.Procedures);
        }

        [Fact]
        public void 좌변만_만든_사전은_매칭이_비어_있다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.All(book.Entries, e => Assert.Empty(e.Matches));
        }
    }
}
