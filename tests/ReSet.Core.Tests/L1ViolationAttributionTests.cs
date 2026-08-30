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

        // 실측(BatchStepPlan.cs 주석): 한 헤딩이 여러 단계를 묶기도 한다("### P20~P23.").
        // 그 헤딩은 어느 코드로도 판정할 수 없으므로(P19도, P20~P23 중 어느 것도 정확히
        // 같지 않다) 경계를 "판정 불가"로 리셋해야 한다. 리셋하지 않으면 그 아래 어휘가
        // 직전의 무관한 단계(P19)로 새어 들어가 - 이 클래스가 막으려는 바로 그 오귀속이다.
        [Fact]
        public void LexemeUnderBundledMultiCodeHeading_ReturnsNull()
        {
            const string doc = """
                ## 단계별 이행 상세 및 의사코드

                ### P19. 정산 대사

                ```sql
                SELECT 1;
                ```

                ### P20~P23. 후처리 일괄

                ```sql
                BEGIN TRY
                    SELECT 1;
                END TRY
                BEGIN CATCH
                END CATCH
                ```
                """;
            var steps = new[] { Step("P19"), Step("P20"), Step("P21"), Step("P22"), Step("P23") };

            var code = L1ViolationAttribution.AttributeByLexeme(doc, "END TRY", steps);

            Assert.Null(code);
        }

        // 무관한 하위 헤딩("#### Phase 1.")은 어느 단계 코드로도 판정되지 않는다.
        // 판정 불가한 헤딩을 만나면 currentStep을 리셋해야 한다 - 그러지 않으면 그
        // 헤딩 아래 어휘가 직전 단계(S01)로 새어 들어간다.
        [Fact]
        public void UnresolvedHeadingAfterKnownStep_ResetsCurrentStep()
        {
            const string doc = """
                ### S01. 첫 단계

                ```sql
                SELECT 1;
                ```

                #### Phase 1. 무관한 하위 표시

                END TRY
                """;
            var steps = new[] { Step("S01") };

            var code = L1ViolationAttribution.AttributeByLexeme(doc, "END TRY", steps);

            Assert.Null(code);
        }
    }

    // MechanicalValidator.ViolationLexemes 테스트. L1ViolationAttribution.AttributeByLexeme가
    // 검색할 어휘를 이 메서드가 DetailedError에서 뽑는다 - 둘은 짝을 이룬다.
    //
    // [계획서 결함 정정] 계획서 원문의 테스트 스니펫은 `ValidationResult`(Errors 컬렉션)를
    // 넘겨 `MechanicalValidator.ViolationLexemes(result)`를 부르지만, 같은 계획서가
    // 선언한 메서드 시그니처는 `ViolationLexemes(DetailedError error)`다 - 오케스트레이터의
    // switch도 `l1Result.DetailedErrors`를 순회하며 `detail`(DetailedError) 하나씩 넘긴다.
    // 계획서의 두 조각이 서로 다른 타입을 가정해 테스트가 컴파일되지 않는다. 실제 배선인
    // DetailedError 시그니처를 기준으로 테스트를 맞춘다.
    public class MechanicalValidatorViolationLexemesTests
    {
        // 실측(POQSettleBatch4 시도 3): 규칙 3-1 위반 메시지가 어휘를 백틱으로 싣는다 -
        // "(발화 1건 · 어휘: `END TRY` · ...)". 산문까지 문서에서 찾으면 아무 단계에나 걸린다.
        [Fact]
        public void ViolationLexemes_ExtractsBacktickedTokensOnly()
        {
            var error = new DetailedError
            {
                Type = ErrorType.SqlSideControlFlow,
                Message = "계획서의 코드 블록에서 SQL 문장이 자기 실행 결과를 보고 분기합니다. `END TRY` 를 쓰지 마십시오."
            };

            Assert.Equal(new[] { "END TRY" }, MechanicalValidator.ViolationLexemes(error));
        }

        [Fact]
        public void ViolationLexemes_WithoutBackticks_ReturnsEmpty()
        {
            var error = new DetailedError { Type = ErrorType.General, Message = "문서 전역에 문제가 있습니다." };

            Assert.Empty(MechanicalValidator.ViolationLexemes(error));
        }

        [Fact]
        public void ViolationLexemes_Deduplicates()
        {
            var error = new DetailedError
            {
                Type = ErrorType.SqlSideControlFlow,
                Message = "`END TRY` 금지, `END TRY` 를 다시 지적한다"
            };

            Assert.Single(MechanicalValidator.ViolationLexemes(error));
        }

        [Fact]
        public void ViolationLexemes_NullError_ReturnsEmpty()
        {
            Assert.Empty(MechanicalValidator.ViolationLexemes(null!));
        }
    }
}
