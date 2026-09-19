using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 계획서의 SQL 펜스가 <b>파싱되는가</b>의 L1 잠금.
    ///
    /// [왜 예약어 별칭 검사로 모자란가] 앞 검사(<c>CheckReservedWordAliasInSql</c>)는
    /// B23 한 편 325 펜스를 재고 만들었다. 계획서 <b>19 편 2628 펜스</b>로 넓혀 재니
    /// 구문 오류 가족이 <b>다섯</b>이었다 - `AS &lt;예약어&gt;` 67 · 예약어를 컬럼 참조로 1 ·
    /// 예약어를 CTE 이름으로 1 · `EXEC` 인자에 식 2 · 미닫힌 문자열 1. 앞 검사가 잡는
    /// 것은 67 뿐이다.
    ///
    /// 다섯 가족 전부 실물 SQL Server 2022 로 교차 확인했고, 고친 형태(대괄호·
    /// `EXEC` 인자를 변수로)는 전부 통과한다.
    ///
    /// [두 검사를 다 두는 이유] 이 검사는 「어디가 깨졌는지」만 말하고 앞 검사는
    /// 「무엇을 고칠지」를 말한다. 실패의 67/72 가 예약어 별칭이라 그 자리만큼은
    /// 정확한 시정 문구가 있어야 한다. <b>같은 펜스가 둘 다에 걸리는 것은 중복이
    /// 아니라 진단과 처방이다.</b>
    ///
    /// [제외 둘] 정당하게 파싱되지 않는 펜스가 코퍼스에 넷 있다 - 자리표시자 2 ·
    /// 의도적 조각 2. 산문의 「교정본」·「참조용」을 읽지 않는다(문구가 바뀌면 조용히
    /// 꺼진다 - 작성 계약 8). 실측으로 이 둘이 넷을 정확히 걷고 진짜 72 를 하나도
    /// 안 먹는다.
    ///
    /// 픽스처는 합성하지 않고 계획서 코퍼스에서 옮겼다 - 좌표는 각 테스트에 적는다.
    /// 선언: docs/audit-reports/2026-09-19-SQL-파싱-L1검사-사전선언.md
    /// </summary>
    public class SqlFenceParseL1Tests
    {
        private static ValidationResult Validate(string markdown) =>
            new MechanicalValidator().ValidateConsolidated(markdown);

        private static bool Fires(string markdown) =>
            Validate(markdown).DetailedErrors.Any(e => e.Type == ErrorType.SqlFenceDoesNotParse);

        /// <summary>필수 H2 넷을 갖춘 최소 통합 계획서. 본문만 갈아 끼운다.</summary>
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

        // ── 양성: 코퍼스에서 실측된 다섯 가족 ──────────────────────────────────

        [Fact]
        public void ReportsReservedWordUsedAsAnAlias()
        {
            // POQSettleBatch23 agent/verification/integrity-sql.md:265. 코퍼스 67 자리.
            var markdown = Plan("""
                ```sql
                SELECT USESTATE, COUNT_BIG(*) AS RowCount
                  FROM ExpectedBranches
                 GROUP BY USESTATE;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ReportsReservedWordUsedAsAColumnReference()
        {
            // POQSettleBatch15 BatchMigrationPlan.md:9666. 앞 검사가 못 잡는 모양 -
            // `AS` 가 없고 별칭도 아니다. 컬럼 이름 자체가 예약어다.
            var markdown = Plan("""
                ```sql
                SELECT
                    N'V10' AS ValidationCode,
                    TableName,
                    RowCount,
                    CASE WHEN RowCount > 0 THEN 1 ELSE 0 END AS IsPassed
                FROM RateCount;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ReportsReservedWordUsedAsACteName()
        {
            // POQSettleBatch1 BatchMigrationPlan.md:4447. `CURRENT` 가 예약어다.
            // 앞 검사는 `AS` 뒤가 여는 괄호인 자리를 설계상 제외하므로 못 잡는다.
            var markdown = Plan("""
                ```sql
                WITH Captured AS (
                    SELECT COUNT(*) AS CapturedCnt FROM batch.BatchControlTotal
                ),
                Current AS (
                    SELECT COUNT(*) AS CurrentCnt FROM SETTLE_POQ_DB.dbo.TPGSettleRate
                )
                SELECT * FROM Captured, Current;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ReportsAnExpressionPassedAsAnExecArgument()
        {
            // POQSettleBatch13 BatchMigrationPlan.md:1094. T-SQL 은 EXEC 인자 자리에
            // 식을 허용하지 않는다 - 변수나 리터럴이어야 한다. 실물 확인: 변수로
            // 빼면 통과한다.
            var markdown = Plan("""
                ```sql
                DECLARE @v_lockResult INT;

                EXEC @v_lockResult = sys.sp_getapplock
                    @Resource = CONCAT(N'POQSettleBatch13:', @p_batchYmd),
                    @LockMode = N'Exclusive',
                    @LockTimeout = 0;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ReportsAnUnterminatedStringLiteral()
        {
            // POQSettleBatch14 BatchMigrationPlan.md:8512 - `N'Restarting)` 에 닫는
            // 따옴표가 빠져 뒤쪽 전부가 문자열로 빨려 들어간다.
            var markdown = Plan("""
                ```sql
                SELECT RunId, RunStatus
                FROM batch.BatchRun
                WHERE RunStatus NOT IN (N'Running', N'Succeeded', N'Restarting)
                ```
                """);

            Assert.True(Fires(markdown));
        }

        // ── 음성: 정상 ──────────────────────────────────────────────────────────

        [Fact]
        public void StaysSilentOnAFenceThatParses()
        {
            var markdown = Plan("""
                ```sql
                SELECT COUNT_BIG(*) AS [RowCount], SUM(Amt) AS TotalAmount
                  FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
                 WHERE A.YMD = @p_ymd;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void StaysSilentOnUndeclaredVariablesBecauseParsingIsNotBinding()
        {
            // 계획서의 SQL 은 앱이 파라미터를 넣는다. 미선언 변수는 구문 오류가
            // 아니다 - 이것을 오류로 세면 2628 펜스 대부분이 고발된다.
            var markdown = Plan("""
                ```sql
                SELECT * FROM batch.BatchRun WHERE RunId = @p_runId AND YMD = @p_batchYmd;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 음성: 제외 둘 ───────────────────────────────────────────────────────
        //
        // 이 둘을 한 시험에 합치지 않는다 - 합치면 제외 하나가 죽어도 초록이다.

        [Fact]
        public void StaysSilentOnATemplateFenceThatCarriesAngleBracketPlaceholders()
        {
            // POQSettleBatch1 BatchMigrationPlan.md - 청크 루프의 서식 템플릿.
            // 코퍼스에서 이 식에 걸리는 펜스가 12 이고 꺾쇠 안에 공백이 든 것은
            // 0 이다(비교 연산자 `a<b` 오인 없음 - 실측).
            var markdown = Plan("""
                ```sql
                SELECT MIN(<ChunkKey>), MAX(<ChunkKey>) FROM <SourceTable>
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void StaysSilentOnAFragmentThatCannotBeginAStatement()
        {
            // POQSettleBatch23 agent/steps/S11.md:492 - 「SQL-09 의 Stored CTE 에
            // 추가할 필터」라 WHERE 절 조각만 실은 자리다. 완전한 문이 아니므로
            // 파싱 실패가 정상이다.
            var markdown = Plan("""
                ```sql
                AND ControlName IN
                (
                    N'LEDGER_ROW_COUNT',
                    N'LEDGER_TXAMT'
                )
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void StaysSilentOnAnExcerptThatOpensWithACaseExpression()
        {
            // POQSettleBatch8 BatchMigrationPlan.md:766 - 「원본 발췌: 참조용 -
            // 이 단계는 실행하지 않음」. 같은 제외이되 첫 토큰이 다르다.
            var markdown = Plan("""
                ```sql
                -- 원본 발췌: dbo.UP_UTIL_SETTLE_INS, INSERT 1 (실행 위치: S05)
                CASE WHEN Y.CommMethod = 0
                     THEN CAST(ROUND(X.PGCOMM, 0, Y.CommRoundFlag) AS INT)
                     ELSE CAST(ROUND(X.PGCOMM4SUM / 1.1, 0, Y.CommRoundFlag) AS INT)
                END AS PGCOMM,
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 귀속 ────────────────────────────────────────────────────────────────

        [Fact]
        public void CarriesTheSourceLineSoTheViolationCanBeAttributedToItsStep()
        {
            var markdown = Plan("""
                ### S16 — 정산 집계 대조

                ```sql
                SELECT COUNT_BIG(*) AS RowCount FROM T;
                ```
                """);

            var error = Validate(markdown).DetailedErrors
                .Single(e => e.Type == ErrorType.SqlFenceDoesNotParse);

            // 작성 계약 9 - 토큰이 아니라 발화가 있던 원문 줄을 싣는다.
            Assert.NotNull(error.Lexemes);
            Assert.Contains(error.Lexemes!, line => line.Contains("COUNT_BIG(*) AS RowCount"));
        }
    }
}
