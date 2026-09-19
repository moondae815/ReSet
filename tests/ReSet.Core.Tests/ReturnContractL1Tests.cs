using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 명세서가 <b>무결과·NULL 경로의 최종 귀착</b>을 말하는가의 L1 잠금.
    ///
    /// [왜] 축 A 에서 가장 넓은 가족이다 - 23 자리(함수 11 · SP 8 · 교차 4)이고
    /// <b>🔴 3 이 전부 여기</b> 있다. 모양이 한결같다: 실행 의미 표(기계 확정)가
    /// 「무결과 시 NULL이 그대로 남습니다」까지 정확히 적는데, <b>반환 계약 절은
    /// 「계산된 금액을 반환합니다」에서 멈춘다.</b> 거짓 서술이 아니라 <b>결론 한
    /// 문장이 빠진 것</b>이다.
    ///
    /// [전건이 「대입 행이 있다」인 이유] 실측(명세서 31 편): 실행 의미 표의 비집계
    /// 대입 52 · 집계 대입 10 이고 대입 행을 가진 편이 <b>16</b> 이다. 순수 연산
    /// 함수(`UF_GET_ROUND4VAT`)는 대입 행이 0 이라 전건이 거짓이다 - 그 편의 NULL
    /// 문제는 <b>인자 NULL → 반환 NULL</b> 이라 재료가 다르고 이 검사의 축이 아니다.
    /// 전건을 넓히면 순수 연산 함수 전량이 고발된다.
    ///
    /// [문서 전수로 NULL 을 세면 안 잡힌다] 🔴 `UF_GET_EXTRACOMM4CLIENT` 는 산문에
    /// NULL 을 <b>5 회</b> 쓴다. 안 쓴 곳이 반환 계약 절이다. <b>실행 의미 표 자신이
    /// 「NULL이 그대로 남습니다」를 담고 있으므로</b> 문서 전수 대조는 구조적으로
    /// 0 을 낸다 - 스코프를 그 절로 좁혀야 한다(작성 계약 2).
    ///
    /// [절 이름을 하나로 못박는다] 대입 행을 가진 16 편의 반환 절 이름이 여섯
    /// 가지다 - `반환 계약` 3 · `반환값` 5 · `반환 값` 1 · `반환코드(…) 매핑` 1 ·
    /// `3. 결과 반환 (라인 160)` 1 · 없음 7.
    ///
    /// 픽스처는 합성하지 않고 코퍼스에서 옮겼다 - 좌표는 각 테스트에 적는다.
    /// 선언: docs/audit-reports/2026-09-19-반환귀착-계약-사전선언.md
    /// </summary>
    public class ReturnContractL1Tests
    {
        private static ValidationResult Validate(string markdown) =>
            new MechanicalValidator().Validate(markdown);

        private static bool Fires(string markdown) =>
            Validate(markdown).DetailedErrors.Any(e => e.Type == ErrorType.ReturnOutcomeNotStated);

        /// <summary>
        /// 필수 H2 다섯을 갖춘 최소 명세서. 개요 본문만 갈아 끼운다.
        /// `MechanicalValidatorTests.WrapSpec` 과 같은 모양이되 이 검사가 보는 것은
        /// CRUD 절이 아니라 개요 아래 절들이라 그 자리를 연다.
        /// </summary>
        private static string Spec(string overviewBody) => string.Join("\n", new[]
        {
            "## 개요", "내용", overviewBody,
            "## 파라미터 목록", "내용",
            "## CRUD 분석", "내용",
            "## 로직 흐름 요약", "내용",
            "## 비즈니스 흐름 시각화",
            "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
        });

        /// <summary>
        /// 코퍼스 실물의 실행 의미 표.
        /// `output/External/SETTLE_CARD_DB/Functions/dbo.UF_GET_EXTRACOMM4CLIENT/docs/Spec.md`
        /// — 대입 행이 자기 입으로 「무결과 시 NULL이 그대로 남습니다」를 말한다.
        /// </summary>
        private const string ExecutionSemanticsWithAssignment = """
            ### 실행 의미 (기계 확정 — 수정 금지)
            | 종류 | 라인 | 대상 | 확정 사실 |
            | :--- | :--- | :--- | :--- |
            | DB 배치 | - | (객체 전체) | 소속 DB는 `SETTLE_CARD_DB`입니다. |
            | 비집계 대입 | 31 | SELECT @v_intExtraType = ExtraType | 비집계 SELECT는 결과가 없으면 대입 자체가 일어나지 않습니다. 이 변수는 DECLARE에 초기값이 없고 이 문장 앞에서 대입되지 않으므로, 무결과 시 NULL이 그대로 남습니다. |
            """;

        /// <summary>
        /// `output/Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md` — 순수 연산 함수라
        /// 대입 행이 0 이다. 이 검사의 전건이 거짓인 자리.
        /// </summary>
        private const string ExecutionSemanticsWithoutAssignment = """
            ### 실행 의미 (기계 확정 — 수정 금지)
            | 종류 | 라인 | 대상 | 확정 사실 |
            | :--- | :--- | :--- | :--- |
            | DB 배치 | - | (객체 전체) | 참조 객체는 전부 `SETTLE_POQ_DB` 로컬입니다. 3부 식별자 참조 0건, 연결 서버 참조 0건 — 확정값입니다. |
            """;

        // ── 양성 ────────────────────────────────────────────────────────────────

        [Fact]
        public void ReportsAnAssignmentBearingSpecThatHasNoReturnContractSection()
        {
            // 코퍼스 실측: 대입 행을 가진 16 편 중 반환 절이 아예 없는 편이 7 이다
            // (`UF_GET_COMM4CLIENT` 는 대입 15 인데 반환 절이 없다).
            var markdown = Spec(ExecutionSemanticsWithAssignment);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ReportsAReturnContractThatStopsAtTheHappyPath()
        {
            // 🔴 의 실물. `UF_GET_EXTRACOMM4CLIENT` 의 반환 계약 절 원문을 옮겼다 -
            // 절은 있는데 무결과 귀착을 말하지 않는다. **「절을 신설하라」만으로는
            // 안 닫히는 자리가 이것이다.**
            var markdown = Spec(ExecutionSemanticsWithAssignment + """


                ### 반환 계약
                함수는 결제금액(`@pi_intTxAmt`)에 대해 계산된 차액정산 수수료 금액을 `MONEY` 타입 스칼라 값으로 반환합니다. 결정론성은 참조 테이블의 현재 데이터 상태에 의존하므로 비결정적(non-deterministic)입니다. 함수 내부에서 데이터를 변경하는 쓰기 연산은 없으며, 모든 문장은 읽기 전용 SELECT입니다.
                """);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ReportsEvenThoughTheDocumentMentionsNullElsewhere()
        {
            // 선언 §1-3 - 문서 전수로 NULL 을 세면 이 가족은 **구조적으로** 안 걸린다.
            // 실행 의미 표 자신이 「무결과 시 NULL이 그대로 남습니다」를 담기 때문이다.
            // 위 픽스처가 이미 그 문장을 담고 있고, 산문에도 NULL 을 더 넣어 못박는다.
            var markdown = Spec(ExecutionSemanticsWithAssignment + """


                본 함수의 입력 파라미터는 NULL 을 허용하지 않으며, 참조 테이블의 NULL 값은 `ISNULL` 로 감쌉니다.

                ### 반환 계약
                계산된 차액정산 수수료 금액을 `MONEY` 타입 스칼라 값으로 반환합니다.
                """);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ReportsASectionNamedSomethingElseAsMissing()
        {
            // 코퍼스에서 이름이 여섯 가지로 갈렸다. 계약은 `### 반환 계약` 하나다 -
            // `반환값`(5 편)은 「절 없음」으로 본다. 이름을 통일하지 않으면 이 검사도
            // 하류 도구도 그 절을 못 찾는다.
            var markdown = Spec(ExecutionSemanticsWithAssignment + """


                ### 반환값
                무결과 시 NULL 이 반환됩니다.
                """);

            Assert.True(Fires(markdown));
        }

        // ── 음성 ────────────────────────────────────────────────────────────────

        [Fact]
        public void StaysSilentWhenTheReturnContractStatesTheNullOutcome()
        {
            var markdown = Spec(ExecutionSemanticsWithAssignment + """


                ### 반환 계약
                계산된 차액정산 수수료 금액을 `MONEY` 타입 스칼라 값으로 반환합니다. 다만 라인 31 의 비집계 SELECT 가 무결과이면 `@v_intExtraType` 이 NULL 로 남고, 그 NULL 이 이후 계산을 통과해 **함수의 최종 반환값이 NULL 이 됩니다** — 0 이 아닙니다.
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void StaysSilentOnAPureComputationFunctionWhichHasNoAssignmentRow()
        {
            // `UF_GET_ROUND4VAT`. 이 편도 실제로는 NULL 을 낸다(인자 NULL → 반환 NULL,
            // 실행 프로브로 확정). 그러나 그것은 **대입 경로가 아니라 식 경로**라
            // 재료가 다르다 - 전건을 넓혀 잡으려 하면 순수 연산 함수 전량이 고발된다.
            var markdown = Spec(ExecutionSemanticsWithoutAssignment);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void StaysSilentWhenThereIsNoExecutionSemanticsTableAtAll()
        {
            // 재료가 없으면 조용히 물러난다(작성 계약 1 의 이웃 - 자기 재료가 빌 때
            // early-return 하는지 확인하라).
            var markdown = Spec("내용만 있고 기계 확정 표가 없다.");

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void StaysSilentWhenTheOutcomeIsStatedAsSomethingOtherThanNull()
        {
            // 귀착이 NULL 이 아닐 수도 있다 - 「무결과 시 0 을 반환합니다」도 결론이다.
            // 이 검사는 **결론이 있는가**를 보지 그 결론이 참인지는 안 본다(선언 §7).
            var markdown = Spec(ExecutionSemanticsWithAssignment + """


                ### 반환 계약
                계산된 금액을 반환합니다. 라인 31 이 무결과이면 이후 분기가 ELSE 로 가서 **최종 반환값은 0 입니다.**
                """);

            Assert.False(Fires(markdown));
        }

        // ── 귀속 ────────────────────────────────────────────────────────────────

        [Fact]
        public void CarriesTheAssignmentRowSoTheViolationCanBeAttributed()
        {
            var markdown = Spec(ExecutionSemanticsWithAssignment);

            var error = Validate(markdown).DetailedErrors
                .Single(e => e.Type == ErrorType.ReturnOutcomeNotStated);

            // 작성 계약 9 - 토큰이 아니라 발화가 있던 원문 줄을 싣는다.
            Assert.NotNull(error.Lexemes);
            Assert.Contains(error.Lexemes!, line => line.Contains("SELECT @v_intExtraType = ExtraType"));
        }
    }
}
