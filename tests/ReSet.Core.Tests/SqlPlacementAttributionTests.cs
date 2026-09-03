using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// SQL 거처 축 검사 넷(규칙 3-1·10)의 <b>귀속</b>을 잠근다 - 발화 여부는
    /// <see cref="SqlPlacementL1Tests"/>가 잠그고, 여기서는 그 발화가 <b>어느 단계를
    /// 여는가</b>를 잠근다.
    ///
    /// [왜 이 넷만 따로 재는가] 이 넷의 메시지는 규칙 설명 안에 금지 어휘를 백틱으로
    /// 싣는다 - SqlSideControlFlow 하나가 `GOTO`·`IF @@ERROR &lt;&gt; 0`·`IF @@ROWCOUNT`·
    /// `BEGIN TRY`·`END CATCH`·`SET @v = @@ROWCOUNT` 여섯을 고정 문구로 담고,
    /// NoLockHintInCode는 `NOLOCK`을 담는다. <see cref="MechanicalValidator.ViolationLexemes"/>가
    /// 메시지의 백틱 토큰을 훑으므로 그 여섯이 그대로 귀속 어휘가 되고,
    /// <see cref="L1ViolationAttribution.AttributeByLexeme"/>는 <b>코드 펜스를 가리지
    /// 않으므로</b> 그 어휘를 산문에서 찾아 위반이 없는 단계까지 연다.
    ///
    /// 실측(2026-09-03): S01만 코드에서 위반한 문서에서 S01·S02·S03이 전부 열렸다 -
    /// S02는 "원본의 `BEGIN TRY` 감싸기는 앱으로 옮겼습니다", S03은 "`SET @v = @@ROWCOUNT`
    /// 표기는 앱이 읽는 것으로 바꿨습니다"라는 <b>이행 서술</b>만 갖고 있었다.
    /// 규칙 10의 `NOLOCK`은 계획서 22편 실측에서 산문 약 300건 대 코드 0건이므로
    /// 이 새기는 한 발화가 Job의 모든 단계를 여는 데까지 간다.
    ///
    /// 억지 귀속의 대가는 <see cref="L1ViolationAttribution"/>의 주석이 적은 그대로다 -
    /// 멀쩡한 단계를 다시 쓰게 되어 회귀 롤백이 막으려는 회귀를 다시 들인다.
    /// </summary>
    public class SqlPlacementAttributionTests
    {
        private static BatchStepPlan Step(string code) =>
            new(code, $"{code} 단계",
                LegacyProcedures: new[] { $"dbo.UP_{code}" },
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        private static string Plan(string body) => $"""
            ## 통합 배치 아키텍처 개요

            내용.

            ## Mermaid 기반 통합 흐름도

            ```mermaid
            flowchart TD
            A["시작"] --> B["끝"]
            ```

            ## 단계별 이행 상세 및 의사코드

            {body}

            ## 통합 데이터 정합성 검증 SQL 세트

            내용.
            """;

        /// <summary>그 문서에서 그 유형의 위반이 여는 단계 코드 전부(오케스트레이터 배선과 같은 순서).</summary>
        private static IReadOnlyList<string> OpenedSteps(
            string markdown, ErrorType type, IReadOnlyList<BatchStepPlan> steps)
        {
            var opened = new List<string>();
            var errors = new MechanicalValidator()
                .ValidateConsolidated(markdown).DetailedErrors.Where(e => e.Type == type);

            foreach (var error in errors)
            {
                foreach (var lexeme in MechanicalValidator.ViolationLexemes(error))
                {
                    foreach (var code in L1ViolationAttribution.AttributeByLexeme(markdown, lexeme, steps))
                    {
                        if (!opened.Contains(code)) opened.Add(code);
                    }
                }
            }

            return opened;
        }

        // ── 새는 것을 막는다 ────────────────────────────────────────────────────

        /// <summary>
        /// S01만 코드에서 분기하고, S02·S03은 규칙을 지켰다고 <b>산문으로 서술만</b> 한다.
        /// 고정 문구가 어휘로 새면 S02·S03도 열린다(고치기 전 실측 동작).
        /// </summary>
        [Fact]
        public void SqlSideControlFlow_OpensOnlyTheStepWhoseCodeActuallyBranches()
        {
            var markdown = Plan("""
                ### S01. 정산 삽입

                ```sql
                UPDATE dbo.TS01 SET A = 1;
                IF @@ROWCOUNT = 0
                    GOTO ERR_HANDLER;
                ```

                ### S02. 마감

                원본의 `BEGIN TRY` 감싸기는 앱의 실패 경로로 옮겼습니다.

                ```sql
                UPDATE dbo.TS02 SET B = 2;
                ```

                ### S03. 집계

                원본이 쓰던 `SET @v = @@ROWCOUNT` 표기는 앱이 행 수를 읽는 것으로 바꿨습니다.

                ```sql
                SELECT COUNT(*) FROM dbo.TS03;
                ```
                """);

            var opened = OpenedSteps(
                markdown, ErrorType.SqlSideControlFlow,
                new[] { Step("S01"), Step("S02"), Step("S03") });

            Assert.Equal(new[] { "S01" }, opened);
        }

        /// <summary>
        /// 규칙 10이 이 새김의 최악이다 - 계획서 22편에서 `NOLOCK`은 산문 약 300건 ·
        /// 코드 0건이므로, 한 단계의 코드 발화 하나가 이행 서술을 가진 모든 단계를 연다.
        /// </summary>
        [Fact]
        public void NoLockHint_DoesNotOpenAStepThatOnlyRecordsHavingRemovedIt()
        {
            var markdown = Plan("""
                ### S01. 차액정산 요청일 조회

                ```sql
                SELECT @v_strReqYMD = MIN(ReqYMD)
                FROM   PaymentDB.dbo.TExtraSettleIn WITH(NOLOCK)
                WHERE  ResYMD = @pi_strYMD;
                ```

                ### S02. 마감

                원본의 `WITH(NOLOCK)` 힌트는 전부 제거했습니다 - 격리는 SNAPSHOT 의무로만 말합니다.

                ```sql
                UPDATE dbo.TS02 SET B = 2;
                ```
                """);

            var opened = OpenedSteps(
                markdown, ErrorType.NoLockHintInCode, new[] { Step("S01"), Step("S02") });

            Assert.Equal(new[] { "S01" }, opened);
        }

        /// <summary>
        /// 프레임워크 타입도 같다 - 산문 35 대 코드 26이 실측이므로 산문 언급이 흔하다.
        /// </summary>
        [Fact]
        public void FrameworkTypePrescribed_DoesNotOpenAStepThatOnlyNamesItInProse()
        {
            var markdown = Plan("""
                ### S01. 트랜잭션 시작

                ```csharp
                using var tran = conn.BeginTransaction(IsolationLevel.Snapshot);
                ```

                ### S02. 마감

                `SqlConnection` 같은 실존 타입은 이름 대지 않고 자리표시자로만 씁니다.

                ```csharp
                tx = conn.beginTransaction()
                ```
                """);

            var opened = OpenedSteps(
                markdown, ErrorType.FrameworkTypePrescribed, new[] { Step("S01"), Step("S02") });

            Assert.Equal(new[] { "S01" }, opened);
        }

        // ── 좁히다가 잃으면 안 되는 것 ──────────────────────────────────────────

        /// <summary>
        /// 체계적 위반(한 어휘가 여러 단계에)은 <b>그 단계 전부</b>가 열려야 한다 -
        /// 하나라도 얼어붙으면 다음 회차에 같은 위반으로 L1이 다시 실패하면서 Job 전체
        /// 예산인 <c>l1RepairAttempt</c>만 태운다(최종 whole-branch 리뷰 Important 5).
        /// 새는 것을 막다가 이쪽을 잃으면 고친 것이 아니다.
        /// </summary>
        [Fact]
        public void SqlSideControlFlow_StillOpensEveryStepThatActuallyViolates()
        {
            var markdown = Plan("""
                ### S01. 정산 삽입

                ```sql
                BEGIN TRY
                    INSERT INTO dbo.TS01 SELECT 1;
                END TRY
                BEGIN CATCH
                END CATCH
                ```

                ### S02. 마감

                ```sql
                BEGIN TRY
                    UPDATE dbo.TS02 SET B = 2;
                END TRY
                BEGIN CATCH
                END CATCH
                ```

                ### S03. 집계

                ```sql
                SELECT COUNT(*) FROM dbo.TS03;
                ```
                """);

            var opened = OpenedSteps(
                markdown, ErrorType.SqlSideControlFlow,
                new[] { Step("S01"), Step("S02"), Step("S03") });

            Assert.Equal(new[] { "S01", "S02" }, opened);
        }

        /// <summary>
        /// 이 넷 밖의 검사는 메시지 백틱 스캔이 그대로 유일한 재료다 - 그쪽 메시지는
        /// 규칙 설명이 아니라 <b>지목된 식별자</b>를 백틱에 담으므로 새지 않는다.
        /// 좁히는 변경이 그 폴백까지 끄면 대부분의 검사가 귀속을 잃는다.
        /// </summary>
        [Fact]
        public void ViolationLexemes_WithoutExplicitLexemes_StillScansTheMessage()
        {
            var error = new DetailedError
            {
                Type = ErrorType.General,
                Message = "`dbo.TGhost` 테이블은 스키마 카탈로그에 없습니다."
            };

            Assert.Equal(new[] { "dbo.TGhost" }, MechanicalValidator.ViolationLexemes(error));
        }
    }
}
