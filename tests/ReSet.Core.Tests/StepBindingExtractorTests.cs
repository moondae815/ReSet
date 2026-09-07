using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepBindingExtractorTests
    {
        // Batch5/S05:71 · S07:57 실물의 모양이다.
        private const string Section = @"### S05 수수료 갱신

```pseudocode
runId = queryScalar(SQL_CURRENT_RUN_ID, { p_jobName: ""J"", p_ymd: batchYmd })
execute(SQL_UPDATE_13, { p_YMD: p_YMD, p_v_valIncVat: 1.1 })   // 지역 변수 계약 DECIMAL(2,1)
```
```sql
-- SQL_UPDATE_13
UPDATE dbo.TSettleMst SET CLComm = CAST(CLComm / @p_v_valIncVat AS INT);
```";

        [Fact]
        public void Extract_ReadsEveryBindingInPseudocodeFences()
        {
            var facts = StepBindingExtractor.Extract(Section);

            Assert.Equal(4, facts.Count);
            Assert.Contains(facts, f =>
                f.CallName == "execute" &&
                f.StatementName == "SQL_UPDATE_13" &&
                f.Key == "p_v_valIncVat" &&
                f.Value == "1.1");
        }

        [Fact]
        public void Extract_KeepsQueryScalarBindings()
        {
            var facts = StepBindingExtractor.Extract(Section);

            Assert.Contains(facts, f => f.CallName == "queryScalar" && f.Key == "p_ymd");
        }

        [Fact]
        public void Extract_IgnoresSqlFences()
        {
            // SQL 펜스에 execute( 문자열이 있어도 바인딩이 아니다 - 그것을 세면
            // 검사가 SQL 주석에 반응한다.
            var markdown = "### S01\n\n```sql\n-- execute(SQL_X, { p_a: 1.1 })\nSELECT 1;\n```";

            Assert.Empty(StepBindingExtractor.Extract(markdown));
        }

        // 실측(2026-09-07, 단계 본문 386 편): 여러 줄에 걸친 바인딩 객체가 20 자리 있고
        // 그 안에 사실 157 개가 산다 - 전체 872 의 18%. 줄 단위로 앵커한 구현은 이
        // 157 에 눈이 먼다. Batch5/S14:52 실물의 모양이다.
        [Fact]
        public void Extract_ReadsBindingsSpanningSeveralLines()
        {
            var markdown = "### S14\n\n```pseudocode\n" +
                           "execute(SQL_INSERT_MISS, {\n" +
                           "        p_id: id,\n" +
                           "        p_clTotal: clTotal,\n" +
                           "        p_comment: N'수수료 후취'\n" +
                           "    })\n```";

            var facts = StepBindingExtractor.Extract(markdown);

            Assert.Equal(3, facts.Count);
            Assert.All(facts, f => Assert.Equal("SQL_INSERT_MISS", f.StatementName));
            Assert.Contains(facts, f => f.Key == "p_comment" && f.Value == "N'수수료 후취'");
        }

        // 실측: 바인딩 객체가 빈 호출(`queryScalar(SQL_X, {})`)이 코퍼스에 6 자리 있다.
        // 사실을 0 개 내는 것이 옳다 - 바인딩된 값이 없기 때문이다. 그리고 그것이
        // **뒤따르는 호출의 파싱을 삼키면 안 된다**(중괄호를 탐욕적으로 물면 삼킨다).
        [Fact]
        public void Extract_OnEmptyBindingObject_YieldsNothingAndKeepsReadingLaterCalls()
        {
            var markdown = "### S01\n\n```pseudocode\n" +
                           "runId = queryScalar(SQL_SCOPE_IDENTITY, {})\n" +
                           "execute(SQL_UPDATE_13, { p_v_valIncVat: 1.1 })\n```";

            var facts = StepBindingExtractor.Extract(markdown);

            var fact = Assert.Single(facts);
            Assert.Equal("execute", fact.CallName);
            Assert.Equal("SQL_UPDATE_13", fact.StatementName);
            Assert.Equal("1.1", fact.Value);
        }

        [Fact]
        public void Extract_OnEmptyInput_IsEmpty()
        {
            Assert.Empty(StepBindingExtractor.Extract(null));
            Assert.Empty(StepBindingExtractor.Extract("   "));
        }
    }
}
