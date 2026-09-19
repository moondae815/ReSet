using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// SQL 펜스 안에서 예약어를 대괄호 없이 별칭으로 쓴 자리의 L1 잠금.
    ///
    /// [왜 이 검사가 있는가] B23 배송본이 `AS RowCount` 를 <b>48 자리</b>에 달고
    /// 게이트를 통과했다. 그 SQL 은 실행 오류가 아니라 <b>컴파일 오류</b>로 죽는다 -
    /// 로컬 SQL Server 2022 실측:
    /// <code>Msg 156, Incorrect syntax near the keyword 'RowCount'.</code>
    /// `[RowCount]` 대괄호는 통과한다(132 반환). 데이터도 권한도 필요 없이 그 전에
    /// 죽으므로 이 결함은 데이터 의존이 아니고 등급이 갈리지 않는다.
    ///
    /// 48 중 12 가 <b>검증 SQL 세트</b>(`agent/verification/integrity-sql.md`)에 있다 -
    /// 다른 단계들이 부르는 정본이다.
    ///
    /// [오라클은 파서다] 예약어 목록을 손으로 적지 않는다. 손으로 적은 목록은
    /// 빠뜨려 못 잡거나 넓어서 오탐한다. 대신 `SELECT 1 AS &lt;별칭&gt;` 을 ScriptDom 에
    /// 물어 파싱되면 식별자, 안 되면 예약어로 판정한다 - SQL Server 와 같은 문법
    /// 계열이라 판정이 실물과 같아진다.
    ///
    /// [스코프] ```sql 펜스 안만 본다. 배송본 실측에서 `AS RowCount` 48/48 이 전부
    /// 펜스 안이었고 산문·다른 펜스에는 0 이다.
    ///
    /// 픽스처는 합성하지 않고 배송본에서 옮겼다 - 좌표는 각 테스트에 적는다.
    /// 선언: docs/audit-reports/2026-09-19-예약어-별칭-L1검사-사전선언.md
    /// </summary>
    public class ReservedWordAliasL1Tests
    {
        private static ValidationResult Validate(string markdown) =>
            new MechanicalValidator().ValidateConsolidated(markdown);

        private static bool Fires(string markdown) =>
            Validate(markdown).DetailedErrors.Any(e => e.Type == ErrorType.ReservedWordAliasInSql);

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

        // ── 양성 ────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateConsolidated_ReportsReservedWordAliasInsideASqlFence()
        {
            // B23 배송본 agent/verification/integrity-sql.md:265 의 실물 CTE.
            var markdown = Plan("""
                ### S16 — 정산 집계 대조

                ```sql
                WITH ExpectedCounts AS
                (
                    SELECT USESTATE, COUNT_BIG(*) AS RowCount
                      FROM ExpectedBranches
                     GROUP BY USESTATE
                )
                SELECT * FROM ExpectedCounts;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ValidateConsolidated_ReportsOtherReservedWordsToo()
        {
            // 이 검사는 `RowCount` 한 낱말이 아니라 예약어 전체를 본다.
            var markdown = Plan("""
                ```sql
                SELECT COUNT(*) AS Group, SUM(Amt) AS Order FROM T;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ValidateConsolidated_NamesTheOffendingWordAndCarriesTheSourceLineForAttribution()
        {
            var markdown = Plan("""
                ```sql
                SELECT COUNT_BIG(*) AS RowCount FROM T;
                ```
                """);

            var error = Validate(markdown).DetailedErrors
                .Single(e => e.Type == ErrorType.ReservedWordAliasInSql);

            // 지목된 낱말이 메시지에 있어야 사람과 모델이 무엇을 고칠지 안다.
            Assert.Contains("RowCount", error.Message);

            // 작성 계약 9 - 귀속 어휘는 토큰이 아니라 발화가 실제로 있던 원문 줄이다.
            // 토큰을 실으면 산문 인용에도 걸려 위반 없는 단계까지 연다.
            Assert.NotNull(error.Lexemes);
            Assert.Contains(error.Lexemes!, line => line.Contains("COUNT_BIG(*) AS RowCount"));
        }

        // ── 음성 ────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheAliasIsBracketed()
        {
            // 이것이 정상 표기다 - 로컬 실측에서 `[RowCount]` 는 통과한다.
            var markdown = Plan("""
                ```sql
                SELECT COUNT_BIG(*) AS [RowCount] FROM T;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheAliasIsNotReserved()
        {
            var markdown = Plan("""
                ```sql
                SELECT COUNT_BIG(*) AS RowCnt, SUM(Amt) AS TotalAmount FROM T;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentOnTypeNamesAfterAs()
        {
            // `AS` 뒤가 별칭이 아니라 타입인 자리. 배송본에 CAST 가 흔하다.
            var markdown = Plan("""
                ```sql
                SELECT CAST(Amt AS INT) AS Amount,
                       CONVERT(VARCHAR(8), YMD, 112) AS Ymd
                  FROM T;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentOnOrdinaryTableAliases()
        {
            var markdown = Plan("""
                ```sql
                SELECT A.ID FROM SETTLE_POQ_DB.dbo.TSettleMst AS A
                  JOIN SETTLE_POQ_DB.dbo.TClientSettleRate AS B ON B.YMD = A.AYMD;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheWordOnlyAppearsInProse()
        {
            // 산문은 실행되지 않는다. 배송본 실측에서 이런 자리는 0 이었지만,
            // 스코프를 문서 전수로 넓히면 곧바로 생기는 모양이라 잠근다.
            var markdown = Plan("""
                ### S16 — 정산 집계 대조

                집계 결과의 행 수는 `AS RowCount` 별칭으로 받지 말고 대괄호를 씌우십시오.
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentOnCteNamesWhichAreFollowedByAParenthesis()
        {
            // `WITH Foo AS (` 의 `AS` 뒤는 식별자가 아니라 여는 괄호다.
            var markdown = Plan("""
                ```sql
                WITH Stored AS (SELECT 1 AS c)
                SELECT * FROM Stored;
                ```
                """);

            Assert.False(Fires(markdown));
        }
    }
}
