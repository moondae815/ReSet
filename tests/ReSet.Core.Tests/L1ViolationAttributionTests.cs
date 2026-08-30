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

        // PlanBoundaryResolver.TryLocateByCode가 이미 겪은 함정과 같다: 헤딩 제목이
        // 다른 단계의 코드를 언급하면("S02 (S01 이후)") 부분 문자열 대조는 먼저 걸리는
        // S01로 잘못 귀속한다. 헤딩이 스스로 선언하는 선행 코드만 인정해야 한다.
        [Fact]
        public void HeadingMentioningAnotherStepCode_AttributesToOwnLeadingCode()
        {
            const string doc = """
                ## 단계별 이행 상세 및 의사코드

                ### S01. 잠금과 RunId 발급

                ```sql
                SELECT 1;
                ```

                ### S02 (S01 이후)

                ```sql
                BEGIN TRY
                    INSERT INTO dbo.TS02 SELECT 1;
                END TRY
                BEGIN CATCH
                END CATCH
                ```
                """;
            var steps = new[] { Step("S01"), Step("S02") };

            var code = L1ViolationAttribution.AttributeByLexeme(doc, "END TRY", steps);

            Assert.Equal("S02", code);
        }

        // 코드 펜스 안의 `###`로 시작하는 줄(주석 등)은 헤딩이 아니다 - 실려 있는 것이
        // 실제 단계 코드(S99)라도 마찬가지다. 헤딩 탐지가 이를 존중하지 않으면 펜스 안
        // 텍스트가 단계 경계를 흔들어 currentStep이 엉뚱하게 바뀐다.
        [Fact]
        public void HeadingLineInsideFence_IsNotTreatedAsStepBoundary()
        {
            const string doc = """
                ## 단계별 이행 상세 및 의사코드

                ### S01. 첫 단계

                ```sql
                ### S99. 주석 안의 위조 헤딩
                END TRY
                ```
                """;
            var steps = new[] { Step("S01"), Step("S99") };

            var code = L1ViolationAttribution.AttributeByLexeme(doc, "END TRY", steps);

            Assert.Equal("S01", code);
        }

        // 펜스가 끝까지 닫히지 않으면 펜스 상태를 신뢰할 수 없다 - MarkdownSectionLocator가
        // 이미 세운 원칙대로 펜스를 무시하고 다시 스캔해야 한다. 그러지 않으면 이후 모든
        // 헤딩이 "펜스 안"으로 오인되어 단계 경계를 영영 못 찾고, currentStep이 첫 단계에
        // 멈춰 문서 나머지 전부를 그 단계 것으로 삼켜버린다(미탐).
        [Fact]
        public void UnclosedFence_DoesNotSwallowRestOfDocument()
        {
            const string doc = """
                ## 단계별 이행 상세 및 의사코드

                ### S01. 첫 단계

                ```sql
                SELECT 1;

                ### S02. 둘째 단계

                END TRY
                """;
            var steps = new[] { Step("S01"), Step("S02") };

            var code = L1ViolationAttribution.AttributeByLexeme(doc, "END TRY", steps);

            Assert.Equal("S02", code);
        }
    }
}
