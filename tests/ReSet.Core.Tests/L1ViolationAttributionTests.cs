using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class L1ViolationAttributionTests
    {
        private static BatchStepPlan Step(string code) =>
            new(code, $"{code} 단계",
                LegacyProcedures: new[] { $"dbo.UP_{code}" },
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        private const string Document = """
            ## 단계별 이행 상세 및 의사코드

            ### S01. 잠금과 RunId 발급

            ```sql
            INSERT INTO batch.BatchRun (JobName) VALUES (@JobName);
            ```

            ### S02. 수수료율 스냅샷

            ```sql
            BEGIN TRY
                INSERT INTO dbo.TS02 SELECT 1;
            END TRY
            BEGIN CATCH
            END CATCH
            ```

            ### S03. 정산 원장

            ```sql
            SELECT 1;
            ```
            """;

        // 실측(POQSettleBatch4 시도 3): 규칙 3-1 위반 `END TRY` 하나로 문서 전체를 다시 만들었다.
        [Fact]
        public void LexemeInsideStepSection_IsAttributedToThatStep()
        {
            var steps = new[] { Step("S01"), Step("S02"), Step("S03") };

            var code = L1ViolationAttribution.AttributeByLexeme(Document, "END TRY", steps);

            Assert.Equal("S02", code);
        }

        [Fact]
        public void LexemeInFirstStep_IsAttributedToFirstStep()
        {
            var steps = new[] { Step("S01"), Step("S02"), Step("S03") };

            var code = L1ViolationAttribution.AttributeByLexeme(Document, "batch.BatchRun", steps);

            Assert.Equal("S01", code);
        }

        // 어디에도 없으면 귀속하지 않는다. 억지로 붙이면 멀쩡한 단계를 다시 쓴다.
        [Fact]
        public void LexemeNotFound_ReturnsNull()
        {
            var steps = new[] { Step("S01"), Step("S02") };

            Assert.Null(L1ViolationAttribution.AttributeByLexeme(Document, "MERGE INTO", steps));
        }

        [Fact]
        public void NullSteps_ReturnsNull()
        {
            Assert.Null(L1ViolationAttribution.AttributeByLexeme(Document, "END TRY", steps: null));
        }

        // 단계 헤딩 앞(공통 규약 절)에 있는 어휘는 어느 단계의 것도 아니다.
        // 골격의 결함이므로 단계에 붙이면 안 된다.
        [Fact]
        public void LexemeBeforeAnyStepHeading_ReturnsNull()
        {
            var doc = "## 단계별 이행 상세 및 의사코드\n\n공통 규약에서 END TRY 를 금지한다.\n\n### S01. 첫 단계\n\n본문\n";
            var steps = new[] { Step("S01") };

            Assert.Null(L1ViolationAttribution.AttributeByLexeme(doc, "END TRY", steps));
        }

        // 목차에 없는 단계 헤딩 안에서 발견되면 귀속하지 않는다 -
        // 그 헤딩은 우리가 아는 단계가 아니다.
        [Fact]
        public void LexemeInUnknownStepSection_ReturnsNull()
        {
            var doc = "## 단계별 이행 상세 및 의사코드\n\n### S99. 모르는 단계\n\nEND TRY\n";
            var steps = new[] { Step("S01") };

            Assert.Null(L1ViolationAttribution.AttributeByLexeme(doc, "END TRY", steps));
        }
    }
}
