using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 검증 SQL 세트의 블록이 <b>자기 역할을 선언하는가</b>의 L1 잠금.
    ///
    /// [왜] B23 축 B 감사의 🔴 18 중 <b>15</b> 가 한 가족이다 - 세트를 부르게 만든
    /// 처방 뒤, 단계 10 곳이 같은 블록을 자기 트랜잭션 안에서 커밋 전에 돌리고
    /// `ViolationCount != 0` 이면 전량 롤백했다. 세트 공통 규약은 「검증 실패는 S18 이
    /// `batch.BatchValidationIssue` 에 <b>기록한다</b>」인데(`integrity-sql.md:10`),
    /// 그 한 줄이 블록마다 붙지 않아 238 블록을 덮지 못했다.
    ///
    /// <b>이 검사는 술어를 고치지 않는다 - 틀린 술어의 대가를 낮춘다.</b> 헐거운 술어가
    /// 「배치 전면 중단」이 아니라 「이슈 한 줄」이 되게 한다.
    ///
    /// [거처가 SQL 주석인 이유는 사본이다] B23 의 실패 모양은 「단계가 세트 SQL 을
    /// 자기 안에 복사해 고쳤다」이고, 산문과 제목은 복사에 안 따라간다. 역할이 SQL
    /// 안에 있으면 사본에도 실린다. 그리고 코퍼스 펜스 240/379(63%)가 이미 `--` 로 연다.
    ///
    /// [블록을 이름으로 잡지 않는다] 세트 블록의 이름 공간이 편마다 다르다 -
    /// `SQL-01`(B23) · `V01`(B15) · `4.1`(B1) · 제목뿐(B19). <b>H3 구간 중 ```sql 펜스를
    /// 가진 것</b>이 블록이고, 실측 238 개다(계획서 19 편).
    ///
    /// [어휘를 좁게 못박지 않는다] 이 검사의 음성 표본은 <b>코퍼스에 없다</b> - 역할
    /// 표기가 0 이기 때문이다. 재생성에서 모델이 쓰는 말은 스윕에 없던 모양을 내므로
    /// 결합을 넓게 잡는다(`게이트`·`기록`·`차단`·`사후`).
    ///
    /// 픽스처는 합성하지 않고 코퍼스에서 옮겼다 - 좌표는 각 테스트에 적는다.
    /// 선언: docs/audit-reports/2026-09-19-검증세트-역할표기-사전선언.md
    /// </summary>
    public class ValidationSetBlockRoleL1Tests
    {
        private static ValidationResult Validate(string markdown) =>
            new MechanicalValidator().ValidateConsolidated(markdown);

        private static bool Fires(string markdown) =>
            Validate(markdown).DetailedErrors.Any(e => e.Type == ErrorType.ValidationSetBlockRoleMissing);

        /// <summary>
        /// 필수 H2 넷을 갖춘 최소 통합 계획서. 세트 절의 본문만 갈아 끼운다.
        /// 다른 세 H2 는 이 검사의 스코프 밖이어야 한다(작성 계약 2).
        /// </summary>
        private static string Plan(string verificationBody, string stepBody = "내용.") => $"""
            ## 통합 배치 아키텍처 개요

            내용.

            ## Mermaid 기반 통합 흐름도

            ```mermaid
            flowchart TD
            A["시작"] --> B["끝"]
            ```

            ## 단계별 이행 상세 및 의사코드

            {stepBody}

            ## 통합 데이터 정합성 검증 SQL 세트

            {verificationBody}
            """;

        // ── 양성 ────────────────────────────────────────────────────────────────

        [Fact]
        public void ReportsABlockThatDoesNotDeclareItsRole()
        {
            // POQSettleBatch23 agent/verification/integrity-sql.md:400 의 실물.
            var markdown = Plan("""
                ### SQL-07 지급·회수 상태 및 예정일 검증

                ```sql
                WITH Violations AS
                (
                    SELECT ID
                      FROM SETTLE_POQ_DB.dbo.TSettleMst
                     WHERE YMD = @BusinessDate
                       AND (OutState IN (1,2,5) AND NULLIF(OutYMD, '') IS NULL)
                )
                SELECT N'SETTLEMENT_STATE_DATE' AS CheckCode,
                       COUNT(*) AS ViolationCount
                  FROM Violations;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        [Fact]
        public void ReportsTheBlockThatIsMissingEvenWhenItsSiblingDeclaresOne()
        {
            // 한 블록만 빠져도 잡아야 한다 - 「하나라도 있으면 통과」면 238 중 1 만
            // 적어도 초록이 된다.
            var markdown = Plan("""
                ### SQL-01 실행 행 및 잠금 무결성

                ```sql
                -- SQL-01 실행 행 및 잠금 무결성 · 역할: 게이트
                SELECT COUNT(*) AS ViolationCount FROM batch.BatchRunLock WHERE RunId = @RunId;
                ```

                ### SQL-07 지급·회수 상태 및 예정일 검증

                ```sql
                SELECT COUNT(*) AS ViolationCount FROM SETTLE_POQ_DB.dbo.TSettleMst
                 WHERE YMD = @BusinessDate;
                ```
                """);

            var error = Validate(markdown).DetailedErrors
                .Single(e => e.Type == ErrorType.ValidationSetBlockRoleMissing);

            // 지목된 것이 역할을 가진 블록이면 안 된다.
            Assert.Contains("SQL-07", error.Message);
            Assert.DoesNotContain("SQL-01", error.Message);
        }

        [Fact]
        public void ReportsBlocksWhoseNamingConventionIsNotSqlNn()
        {
            // 블록 이름 공간이 편마다 다르다. B15 는 `V01`, B1 은 `4.1` 이다 -
            // 이름으로 잡으면 19 편 중 2 편만 잡힌다(실측).
            var b15 = Plan("""
                ### V01 배치 제어 객체와 권한 검증

                ```sql
                SELECT COUNT(*) AS ViolationCount FROM sys.objects WHERE name = N'BatchRun';
                ```
                """);
            var b1 = Plan("""
                ### 4.1 레이트 스냅샷 정합성 검증

                S02가 적재한 레이트 스냅샷이 변경되지 않았는지 확인한다.

                ```sql
                -- 스냅샷 이후 TPGSettleRate 행 수 변동 여부 확인
                SELECT COUNT(*) AS CurrentCnt FROM SETTLE_POQ_DB.dbo.TPGSettleRate;
                ```
                """);

            Assert.True(Fires(b15));
            Assert.True(Fires(b1));
        }

        // ── 음성: 역할을 선언한 블록 ────────────────────────────────────────────

        [Fact]
        public void StaysSilentOnABlockDeclaredAsAGate()
        {
            var markdown = Plan("""
                ### SQL-02 단계 저널 및 체크포인트 완전성

                ```sql
                -- SQL-02 단계 저널 및 체크포인트 완전성 · 역할: 게이트
                SELECT COUNT(*) AS ViolationCount FROM batch.BatchCheckpoint WHERE RunId = @RunId;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void StaysSilentOnABlockDeclaredAsARecordOnlyCheck()
        {
            // 「게이트」만 받으면 다른 절반이 통째로 고발된다. 두 어휘를 따로 잠근다.
            var markdown = Plan("""
                ### SQL-07 지급·회수 상태 및 예정일 검증

                ```sql
                -- SQL-07 지급·회수 상태 및 예정일 검증 · 역할: 기록
                SELECT COUNT(*) AS ViolationCount FROM SETTLE_POQ_DB.dbo.TSettleMst;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void StaysSilentOnTheWiderVocabularyBecauseTheNegativeSampleDoesNotExistYet()
        {
            // 선언 §5-1 - 이 검사의 음성은 코퍼스에 없다(역할 표기 0). 재생성에서
            // 모델이 쓰는 말은 스윕에 없던 모양을 내므로 결합을 넓게 잡았다.
            // 이 시험이 그 폭 자체를 잠근다.
            foreach (var word in new[] { "차단", "사후" })
            {
                var markdown = Plan($"""
                    ### SQL-09 원장 동결 제어 합계 검증

                    ```sql
                    -- SQL-09 원장 동결 제어 합계 검증 · 역할: {word}
                    SELECT COUNT(*) AS ViolationCount FROM batch.BatchControlTotal;
                    ```
                    """);

                Assert.False(Fires(markdown));
            }
        }

        // ── 음성: 스코프 ────────────────────────────────────────────────────────

        [Fact]
        public void StaysSilentOnSqlBlocksOutsideTheVerificationSection()
        {
            // 작성 계약 2 - 문서 전체를 훑으면 단계별 이행 절의 SQL 을 자기 결함으로
            // 오귀속한다. 계획서에서 그 절의 sql 펜스가 훨씬 많다.
            var markdown = Plan(
                verificationBody: """
                    ### SQL-01 실행 행 및 잠금 무결성

                    ```sql
                    -- SQL-01 실행 행 및 잠금 무결성 · 역할: 게이트
                    SELECT COUNT(*) AS ViolationCount FROM batch.BatchRunLock;
                    ```
                    """,
                stepBody: """
                    ### S05 — 일반 원장 생성

                    ```sql
                    INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst (YMD, PLTID)
                    SELECT YMD, PLTID FROM SETTLE_POQ_DB.dbo.TTxMst WHERE YMD = @p_ymd;
                    ```
                    """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void StaysSilentOnAHeadingThatCarriesNoSqlFence()
        {
            // 세트 절의 H3 중에는 산문만 있는 것이 있다(「공통 실행 규약」·「검증 실행
            // 계약」). 실측: H3 합계보다 블록이 적은 편이 여럿이다. 그것은 블록이 아니다.
            var markdown = Plan("""
                ### 공통 실행 규약

                - 공통 입력은 `@RunId BIGINT`, `@BusinessDate CHAR(8)`이다.
                - `ViolationCount = 0`이면 통과다.

                ### SQL-01 실행 행 및 잠금 무결성

                ```sql
                -- SQL-01 실행 행 및 잠금 무결성 · 역할: 게이트
                SELECT COUNT(*) AS ViolationCount FROM batch.BatchRunLock;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        [Fact]
        public void FindsTheSectionEvenWhenTheHeadingIsIndented()
        {
            // 작성 계약 5 - 프롬프트는 헤딩을 3칸 들여써서 렌더한다. 모델이 지금은
            // 떨어뜨리지만 어느 회차에 보존하면 절을 못 찾고 검사가 조용히 죽는다.
            var markdown = """
                ## 통합 배치 아키텍처 개요

                내용.

                ## Mermaid 기반 통합 흐름도

                ```mermaid
                flowchart TD
                A["시작"] --> B["끝"]
                ```

                ## 단계별 이행 상세 및 의사코드

                내용.

                   ## 통합 데이터 정합성 검증 SQL 세트

                   ### SQL-07 지급·회수 상태 및 예정일 검증

                ```sql
                SELECT COUNT(*) AS ViolationCount FROM SETTLE_POQ_DB.dbo.TSettleMst;
                ```
                """;

            Assert.True(Fires(markdown));
        }

        // ── 귀속 ────────────────────────────────────────────────────────────────

        [Fact]
        public void CarriesTheHeadingLineSoTheViolationCanBeAttributedToItsBlock()
        {
            var markdown = Plan("""
                ### SQL-07 지급·회수 상태 및 예정일 검증

                ```sql
                SELECT COUNT(*) AS ViolationCount FROM SETTLE_POQ_DB.dbo.TSettleMst;
                ```
                """);

            var error = Validate(markdown).DetailedErrors
                .Single(e => e.Type == ErrorType.ValidationSetBlockRoleMissing);

            // 작성 계약 9 - 토큰이 아니라 발화가 있던 원문 줄을 싣는다.
            Assert.NotNull(error.Lexemes);
            Assert.Contains(error.Lexemes!, line => line.Contains("SQL-07 지급·회수 상태 및 예정일 검증"));
        }
    }
}
