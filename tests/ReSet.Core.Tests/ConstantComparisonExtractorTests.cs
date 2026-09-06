using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ConstantComparisonExtractorTests
    {
        private const string Ddl = @"
CREATE PROCEDURE dbo.UP_Test AS
BEGIN
    DECLARE @v_strSql NVARCHAR(MAX)
    SELECT * FROM TSettleMst WHERE PayMethod = 'impaymobile'
    UPDATE TSettleMst SET OutState = 'B' WHERE PGName IN ('payco', 'INIBANK')
    SELECT * FROM TClient WHERE MallID LIKE 'LOLLETTER4'
    SET @v_strSql = 'SELECT * FROM T WHERE C = ''' + @v_strPGName + ''''
END";

        [Fact]
        public void 등호비교의_컬럼과_값을_함께_뽑는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.Contains(pairs, p => p.Column == "PayMethod" && p.Value == "impaymobile");
        }

        [Fact]
        public void IN_목록의_각_값을_같은_컬럼에_붙여_뽑는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.Contains(pairs, p => p.Column == "PGName" && p.Value == "payco");
            Assert.Contains(pairs, p => p.Column == "PGName" && p.Value == "INIBANK");
        }

        [Fact]
        public void LIKE_패턴도_뽑는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.Contains(pairs, p => p.Column == "MallID" && p.Value == "LOLLETTER4");
        }

        [Fact]
        public void SET절의_대입값은_비교가_아니므로_뽑지_않는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.DoesNotContain(pairs, p => p.Value == "B");
        }

        // 이 테스트가 이 추출기의 존재 이유다. 정규식판은 이 코퍼스에서
        // 고유 상수 82개 중 18개를 동적 SQL 조각으로 오염시켰다.
        [Fact]
        public void 동적SQL_문자열_조립_조각은_뽑지_않는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.DoesNotContain(pairs, p => p.Value.Contains("SELECT"));
            Assert.DoesNotContain(pairs, p => p.Value.Trim() == "'");
            Assert.DoesNotContain(pairs, p => p.Value.Contains("@"));
        }

        [Fact]
        public void 파스에_실패해도_빈_목록을_돌려준다()
        {
            Assert.Empty(ConstantComparisonExtractor.Extract("이건 SQL이 아니다 ((("));
            Assert.Empty(ConstantComparisonExtractor.Extract(null));
        }
    }
}
