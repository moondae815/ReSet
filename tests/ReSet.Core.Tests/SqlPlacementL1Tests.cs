using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// SQL 거처 축(규칙 3-1·10)의 L1 잠금.
    ///
    /// [무엇을 재는가] 물음은 「규칙이 있는가」가 아니라 「모델이 바뀌어도
    /// 지켜지는가」다. 전수 조사
    /// (docs/audit-reports/sweeps/2026-08-29-rule-enforcement-census.md §5)가
    /// 이 셋을 A급으로 골랐다 - 정규식 하나로 판정되고, 재료가 필요 없고,
    /// 실측된 위반이 있다.
    ///
    /// [스코프가 이 검사들의 전부다] 셋 다 문서 전수가 아니라 코드 펜스만 본다.
    /// 계획서 22편 실측이 그 이유다:
    ///   - `NOLOCK`: 산문 약 300건("원본의 `WITH(NOLOCK)`는 전부 제거한다") 대 코드 0건.
    ///     문서 전수 grep은 거의 전량이 이행 서술을 고발한다.
    ///   - `SqlConnection`: 산문 35 대 코드 26.
    /// mermaid 펜스도 제외한다 - 노드 라벨은 원본 흐름을 인용하는 그림 텍스트이지
    /// 앱이 보내는 문장이 아니다(원본 명세서의 mermaid가 실제로
    /// `IF @@ERROR &lt;&gt; 0`·`WITH(NOLOCK)`을 라벨에 담는다).
    ///
    /// 픽스처는 합성하지 않고 실제 코퍼스에서 옮겼다 - 좌표는 각 테스트에 적는다.
    /// </summary>
    public class SqlPlacementL1Tests
    {
        private static ValidationResult Validate(string markdown) =>
            new MechanicalValidator().ValidateConsolidated(markdown);

        private static bool Fires(string markdown, ErrorType type) =>
            Validate(markdown).DetailedErrors.Any(e => e.Type == type);

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

        // ── 규칙 10: NOLOCK 금지 ────────────────────────────────────────────────
        //
        // 규칙이 "explicitly remove ALL"이라 예외가 없다. 계획서 코퍼스 22편에는
        // 코드 안 발화가 0건이지만, 모델이 베끼는 재료 쪽에는 연료가 실재한다 -
        // 레거시 DDL 17개 파일에 43건, 프롬프트에 실리는 원본 명세서 3편의
        // 코드블록 안에 6건. 지금 0인 것은 모델이 지켜서일 뿐이고, 그것이
        // 조사 §6-(1)이 말한 「조용히 꺼지는」 자리다.

        [Fact]
        public void ValidateConsolidated_ReportsANoLockHintInsideASqlFence()
        {
            // 원본 명세서 dbo.UP_UTIL_SETTLE_INS_EXTRA가 인용한 실물 SELECT.
            var markdown = Plan("""
                ### S03 — 차액정산 요청일 조회

                ```sql
                SELECT @v_strReqYMD = MIN(ReqYMD)
                FROM   PaymentDB.dbo.TExtraSettleIn WITH(NOLOCK)
                WHERE  ResYMD = @pi_strYMD;
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.NoLockHintInCode));
        }

        [Fact]
        public void ValidateConsolidated_ReportsANoLockHintWrittenWithASpaceAndBrackets()
        {
            var markdown = Plan("""
                ```sql
                SELECT PLTID FROM SETTLE_POQ_DB.dbo.TSettleMst AS B WITH (NOLOCK)
                WHERE B.YMD = @pi_strYMD;
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.NoLockHintInCode));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheProseOnlySaysTheHintWasRemoved()
        {
            // 코퍼스에 약 300건 있는 모양이다. POQSettleBatch3:89·494·870 등.
            var markdown = Plan("""
                ### S04 — 정산원장 재적재

                이 단계는 SNAPSHOT 격리 수준 아래에서 실행되어야 하며, 원본 SQL의
                `WITH (NOLOCK)` 힌트는 SNAPSHOT 격리 정책과 충돌하므로 모든 단계의
                이행 SQL에서 제거한다. `NOLOCK` 힌트는 어떤 조회에도 사용하지 않는다.

                ```sql
                SELECT PLTID FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @pi_strYMD;
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.NoLockHintInCode));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheHintNameOnlyAppearsInABlockCommentContinuationLine()
        {
            // POQSettleBatch2:1372-1380 실물. 조사 §5가 「1차 통제군 코드 안 2건」의
            // 근거로 삼은 바로 그 줄이다 - 줄 단위 주석 필터가 `/* */` 블록의
            // 이어지는 줄을 못 걸러 위반으로 셌으나, 실제로는 NOLOCK을 제거했다는
            // 주석이다. 이 테스트가 그 오탐을 고정한다.
            var markdown = Plan("""
                ```sql
                /* I1: 취소 정산 데이터 등록 (원본 INSERT 1, 라인 29)
                   NOLOCK 힌트는 SNAPSHOT 격리 정책에 따라 전부 제거되었다(원본은 A, B 양쪽에 WITH(NOLOCK) 사용). */
                INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst (YMD, PLTID)
                SELECT A.YMD, A.PLTID FROM PaymentDB.dbo.TTxMst AS A WHERE A.YMDCANCEL = @pi_strYMD;
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.NoLockHintInCode));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenAMermaidNodeLabelQuotesTheOriginalHint()
        {
            // 원본 명세서 dbo.UP_UTIL_SETTLE_INS_EXTRA:458의 실물 노드 라벨.
            // 그림 텍스트는 앱이 보내는 문장이 아니다.
            var markdown = Plan("""
                ```mermaid
                flowchart TD
                SELREQ["SELECT @v_strReqYMD = MIN(ReqYMD) FROM PaymentDB.dbo.TExtraSettleIn WITH(NOLOCK)"]
                SELREQ --> DONE["끝"]
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.NoLockHintInCode));
        }

        // ── 규칙 3-1: 실존 프레임워크 타입 지정 금지 ────────────────────────────
        //
        // 2차 통제군에서 11건. Critic(glm-5.3)이 추론 로그에 그것을 적고도
        // 감점하지 않았다(설계서 §10-4). 프롬프트와 Critic 두 층이 다 흘린 자리다.

        [Fact]
        public void ValidateConsolidated_ReportsARealFrameworkTypeNamedInApplicationPseudocode()
        {
            // POQSettleBatch3:2056 실물(S08). 공통 설계는
            // `ISettleBatchConnection`/`connectionFactory.open()`으로 부르는데
            // 이 단계만 실존 타입으로 같은 것을 부른다 - 이행 라운드가 존재한 적
            // 없는 계약 둘을 화해시켜야 한다.
            var markdown = Plan("""
                ### S08 — 정산 예외 규칙 적용

                ```csharp
                public async Task<int> ExecuteAsync(long runId, string batchYmd, SqlConnection conn)
                {
                    using var tran = conn.BeginTransaction(IsolationLevel.Snapshot);
                }
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.FrameworkTypePrescribed));
        }

        [Fact]
        public void ValidateConsolidated_ReportsANonDotNetFrameworkTypeToo()
        {
            // 이 도구는 targetLanguage로 Java도 겨눈다. 규칙 3-1이 .NET만 들지
            // 않는 이유가 그것이다(설계서 §10-4).
            var markdown = Plan("""
                ```pseudocode
                PreparedStatement ps = conn.prepareStatement(sql);
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.FrameworkTypePrescribed));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheCodeUsesAGenericPlaceholder()
        {
            // 규칙 3-1이 옳다고 명시한 모양이다. 여기서 감점하면 표현 수단이
            // 없어져 T-SQL 철자로 후퇴한다 - `S13`이 실제로 그 길로 갔다.
            var markdown = Plan("""
                ```pseudocode
                conn = connectionFactory.open()
                tx = conn.beginTransaction()
                repository.execute(deleteStatement, params)
                tx.commit()
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.FrameworkTypePrescribed));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheProseOnlyForbidsTheType()
        {
            var markdown = Plan("""
                이 문서는 트랜잭션·커넥션·오류 처리에 특정 API를 지정하지 않는다.
                `SqlConnection`이나 `TransactionScope` 같은 실존 타입을 이름 대지 않고,
                일반 자리표시자만 쓴다.
                """);

            Assert.False(Fires(markdown, ErrorType.FrameworkTypePrescribed));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenOnlyAVariableIsNamedAfterTheType()
        {
            // POQSettleProc18:1567 실물. 규칙이 금지한 것은 타입을 이름 대는
            // 것이고, camelCase 변수명은 그 귀속이 서지 않는다(작성 계약 7).
            var markdown = Plan("""
                ```csharp
                await sqlConnection.ExecuteAsync(command, cancellationToken);
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.FrameworkTypePrescribed));
        }

        // ── 규칙 3-1: SQL 쪽 제어 흐름 금지 ─────────────────────────────────────
        //
        // 1차 통제군에서 `GOTO` 20 · `IF @@ERROR` 18. 3단계가 규칙에서도 채점에서도
        // 그 조항을 함께 지운 회귀였고(설계서 §9-3), 조항을 되살리자 2차 통제군에서
        // 0이 됐다. 지금 이 축은 프롬프트와 Critic 두 층뿐이다.

        [Fact]
        public void ValidateConsolidated_ReportsAGotoErrorLabelInStepSql()
        {
            // POQSettleBatch2:329-334 실물.
            var markdown = Plan("""
                ```sql
                IF @v_dupCnt > 0
                BEGIN
                    ROLLBACK TRAN;
                    GOTO HandleDuplicateRun;
                END
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        [Fact]
        public void ValidateConsolidated_ReportsAStatementBranchingOnItsOwnOutcome()
        {
            // POQSettleBatch2:1575 실물.
            var markdown = Plan("""
                ```sql
                UPDATE A SET A.PGComm = 0 FROM SETTLE_POQ_DB.dbo.TSettleMst AS A WHERE A.YMD = @pi_strYMD;
                IF @@ERROR <> 0 GOTO ERR_HANDLER;
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        /// <summary>
        /// 4단계 3차 통제군 채택본의 실물이다(`POQSettleBatch4:4119`,
        /// `SQL_S15_CHECKPOINT_UPSERT_SUCCEEDED`). Critic이 경미로 지적했으나 채택본에
        /// 살아남았다 - 규칙 3-1의 일반 조항("a statement MUST NOT branch on its own
        /// outcome")이 이미 덮는 모양인데 열거에 없어 아무도 강제하지 않았다.
        /// </summary>
        [Fact]
        public void ValidateConsolidated_ReportsAnUpsertBranchingOnItsOwnRowCount()
        {
            var markdown = Plan("""
                ```sql
                UPDATE batch.BatchCheckpoint SET Status = N'Succeeded' WHERE RunId = @p_runId;
                IF @@ROWCOUNT = 0
                    INSERT INTO batch.BatchCheckpoint (RunId, Status) VALUES (@p_runId, N'Succeeded');
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        /// <summary>
        /// 그 짝이다. <c>@@ROWCOUNT</c>를 <b>읽어 변수에 담는</b> 것은 분기가 아니다 -
        /// 몇 행이 바뀌었는지는 앱이 받아 판단할 사실이고, 규칙 3-1이 요구하는 모양이
        /// 정확히 그것이다. 코퍼스 실측에서 이 형태가 <b>28건</b>이라 함께 잡으면
        /// 정상 이행이 통째로 L1 실패가 된다(L1 실패는 되돌림이다).
        /// </summary>
        [Fact]
        public void ValidateConsolidated_IsSilentWhenRowCountIsOnlyRead()
        {
            var markdown = Plan("""
                ```sql
                DELETE FROM batch.BatchRunLock WHERE JobName = @p_jobName;
                SET @v_lockDeleted = @@ROWCOUNT;
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        [Fact]
        public void ValidateConsolidated_ReportsATryCatchWrapperAroundStepSql()
        {
            var markdown = Plan("""
                ```sql
                BEGIN TRY
                    DELETE FROM SETTLE_POQ_DB.dbo.TStatPGCollect WHERE INYMD = @pi_strYMD;
                END TRY
                BEGIN CATCH
                    ROLLBACK TRAN;
                END CATCH
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        [Fact]
        public void ValidateConsolidated_ReportsTheTsqlSpellingEvenInsideAnApplicationFence()
        {
            // 앱 펜스에 SQL을 문자열로 싣는 형태가 코퍼스에 실재한다
            // (POQSettleBatch1:429). 판정식이 T-SQL 철자라 앱의 진짜 try/catch와
            // 겹치지 않으므로, 펜스 언어로 봐주지 않는다.
            var markdown = Plan("""
                ```csharp
                var sql = @"BEGIN TRY DELETE FROM dbo.TStatPGCollect END TRY BEGIN CATCH ROLLBACK TRAN END CATCH";
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheApplicationOwnsTheFailurePath()
        {
            // 규칙 3-1이 요구하는 모양이다 - 앱이 실패를 관측하고 다음을 정한다.
            var markdown = Plan("""
                ```pseudocode
                tx = conn.beginTransaction()
                try:
                    repository.execute(updateStatement, params)
                    tx.commit()
                except StepFailure as failure:
                    tx.rollback()
                    journal.recordLegacyReturnCode(failure.legacyCode)
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheProseOnlyDeclaresTheProhibition()
        {
            // POQSettleBatch3:379 실물.
            var markdown = Plan("""
                이 단계는 신규 저장 프로시저를 정의하지 않으며, 아래 SQL은 모두
                애플리케이션이 전송하는 개별 문장이다. `GOTO` 라벨이나
                `IF @@ERROR <> 0` 검사, `BEGIN TRY`/`END CATCH` 감싸기를 단계 SQL에
                쓰지 않는다.
                """);

            Assert.False(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheTokenOnlyAppearsInASqlComment()
        {
            // POQSettleBatch3:1469 실물. 조사 §10-1의 줄 단위 필터는 이 모양을
            // 위반으로 셌다.
            var markdown = Plan("""
                ```sql
                /* 갱신 3(U3): 취소 거래건 부호 반전 - 원본에 전용 @@ERROR 검사 없음(전용 오류코드 없음) */
                UPDATE SETTLE_POQ_DB.dbo.TSettleMst SET CLCOMM = CLCOMM * (-1) WHERE YMD = @pi_strYMD;
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenAMermaidNodeLabelQuotesTheOriginalBranch()
        {
            // 원본 명세서 dbo.UP_Util_PG_Client_CMRate_Ins:390의 실물 노드 라벨.
            var markdown = Plan("""
                ```mermaid
                graph TD
                DELPG["DELETE FROM TPGSettleRate WHERE YMD = @pi_strYMD"]
                DELPG --> CHKDELPG["IF @@ERROR <> 0"]
                CHKDELPG -->|예| ROLL1["ROLLBACK TRAN"]
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.SqlSideControlFlow));
        }

        // ── 규칙 3-1: 신규 저장 프로시저·함수·트리거 금지 ───────────────────────
        //
        // 조사 §5 B급 4. 강제가 0건이던 마지막 조항이다. 코퍼스 실측: 계획서 22편에
        // `CREATE PROCEDURE` 113개가 있고 **전부 지어낸 이름**이다. 문서 검사인 이유도
        // 실측이다 - 109개는 단계 절 안이지만 나머지 4개가 전부 「공통 SQL 오류 추적
        // 패턴」 절의 Tasklet 래퍼이고, 그것이 배치 전체가 걸린 가장 무거운 신규 SP다.
        //
        // 원본 인용 예외는 두지 않는다. 조사 §4는 「이 이름이 레거시인가」를 물으려
        // 시그니처를 넓히라고 권했지만, 프롬프트에 원본 DDL이 실리지 않아 인용이
        // 도달 불가능하고, 로스터를 넣으면 오히려 약해진다 - 레거시명과 겹치는 유일한
        // 1건(POQSettlePrco20:1900)이 인용이 아니라 **재정의**라 로스터가 그 진짜
        // 위반을 통과시킨다.

        [Fact]
        public void ValidateConsolidated_ReportsANewStoredProcedure()
        {
            // POQSettleProc8/S11:39 실물.
            var markdown = Plan("""
                ```sql
                CREATE OR ALTER PROCEDURE dbo.usp_POQSettelProc8_S11
                    @pi_strYMD CHAR(8)
                AS
                BEGIN
                    SELECT 1;
                END
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.NewDatabaseObjectDefined));
        }

        [Fact]
        public void ValidateConsolidated_ReportsABracketQuotedName()
        {
            // POQSettleProc10 실물. 코퍼스 113건 중 2건이 이 표기다.
            var markdown = Plan("""
                ```sql
                CREATE OR ALTER PROCEDURE [batch].[ApplyS08CommonCommissionTax]
                AS
                BEGIN
                    SELECT 1;
                END
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.NewDatabaseObjectDefined));
        }

        [Fact]
        public void ValidateConsolidated_ReportsTheLegacySpellingWithManySpaces()
        {
            // 레거시 DDL의 실제 표기가 `CREATE                           PROCEDURE`다
            // (dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA). 리터럴로 세면 0으로 잘못 나온다.
            var markdown = Plan("""
                ```sql
                CREATE                           PROCEDURE dbo.usp_S05_Rebuild
                AS
                BEGIN
                    SELECT 1;
                END
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.NewDatabaseObjectDefined));
        }

        [Fact]
        public void ValidateConsolidated_ReportsANewFunctionAndTrigger()
        {
            // 규칙 3-1의 문면은 "any NEW stored procedure, function, or trigger"다.
            // 코퍼스 실측은 프로시저 113 · 함수 0 · 트리거 0이라, 이 둘은 아직
            // 실현되지 않은 축을 미리 못박는 트립와이어다.
            var fn = Plan("""
                ```sql
                CREATE FUNCTION dbo.ufn_CanonicalSource(@ymd CHAR(8)) RETURNS TABLE AS RETURN SELECT 1 AS x;
                ```
                """);
            var trigger = Plan("""
                ```sql
                CREATE TRIGGER dbo.trg_TSettleMst_Audit ON dbo.TSettleMst AFTER INSERT AS SELECT 1;
                ```
                """);

            Assert.True(Fires(fn, ErrorType.NewDatabaseObjectDefined));
            Assert.True(Fires(trigger, ErrorType.NewDatabaseObjectDefined));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheProseDeclaresTheProhibition()
        {
            // POQSettleBatch3:379 실물.
            var markdown = Plan("""
                이 단계는 신규 저장 프로시저를 정의하지 않으며, 아래 SQL은 모두
                애플리케이션이 전송하는 개별 문장이다. `CREATE PROCEDURE`는 원본
                프로시저를 인용할 때 외에는 이 문서에 나타나지 않는다.
                """);

            Assert.False(Fires(markdown, ErrorType.NewDatabaseObjectDefined));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheDefinitionIsOnlyQuotedInAComment()
        {
            var markdown = Plan("""
                ```sql
                /* 원본은 CREATE PROCEDURE dbo.UP_UTIL_SETTLE_INS로 시작하지만
                   본 이관은 신규 프로시저를 만들지 않는다. */
                DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @pi_strYMD;
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.NewDatabaseObjectDefined));
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentWhenAMermaidNodeLabelNamesTheOriginal()
        {
            var markdown = Plan("""
                ```mermaid
                flowchart TD
                STRT["CREATE PROCEDURE dbo.UP_UTIL_SETTLE_INS (원본)"] --> DONE["끝"]
                ```
                """);

            Assert.False(Fires(markdown, ErrorType.NewDatabaseObjectDefined));
        }

        [Fact]
        public void ValidateConsolidated_ReportsEveryNewObjectInOneError()
        {
            // POQSettleProc2가 한 문서에 18개를 정의한다. 정의마다 오류를 만들면
            // SuggestedPromptFix가 못 읽을 것이 된다.
            var markdown = Plan("""
                ```sql
                CREATE OR ALTER PROCEDURE batch.ExecuteS01Initialization AS BEGIN SELECT 1; END
                CREATE OR ALTER PROCEDURE batch.ExecuteS04BasicSettlementRebuild AS BEGIN SELECT 1; END
                CREATE OR ALTER PROCEDURE batch.ExecuteS06ExceptionRules AS BEGIN SELECT 1; END
                ```
                """);

            var fired = Validate(markdown).DetailedErrors
                .Where(e => e.Type == ErrorType.NewDatabaseObjectDefined)
                .ToList();

            Assert.Single(fired);
            Assert.Contains("ExecuteS01Initialization", fired[0].Message);
            Assert.Contains("ExecuteS06ExceptionRules", fired[0].Message);
        }

        [Fact]
        public void ValidateConsolidated_TellsTheModelToSendStatementsInsteadOfDefiningAProcedure()
        {
            var markdown = Plan("""
                ```sql
                CREATE OR ALTER PROCEDURE dbo.usp_POQSettelProc8_S11 AS BEGIN SELECT 1; END
                ```
                """);

            var fix = Validate(markdown).SuggestedPromptFix;

            Assert.NotNull(fix);
            Assert.Contains("SQL 거처 규칙 위반", fix);
            // 미지 테이블 검사가 하던 그 틀린 지시가 여기서 반복되면 안 된다.
            Assert.DoesNotContain("batch 스키마에 두십시오", fix);
        }

        // ── 펜스 짝이 안 맞으면 넷이 함께 침묵한다 ──────────────────────────────
        //
        // `CleanedAppCodeFences`는 ```와 ```를 **문서 순서대로 짝지어** 펜스를 뽑는다.
        // 열린 채 닫히지 않은 펜스가 하나 있으면 짝이 밀려, 펜스1의 닫는 자리부터
        // 펜스2의 여는 자리까지의 **산문이 코드로 읽힌다.**
        //
        // 그때 「이 토큰이 코드 안에 있다」는 귀속은 성립하지 않는다. 작성 계약 7이
        // 「귀속이 불가능하면 침묵하라」다 - 이 가드는 있을 법한 결함을 미리 막는 검사가
        // 아니라 **그 헬퍼의 성립 조건**이다.
        //
        // 노출량 실측(계획서 23편): 홀수 펜스 문서는 **0편**이라 아직 실현된 적이 없다.
        // 그러나 밀렸을 때 산문에서 걸리는 양은 `NOLOCK` 457 · 제어 흐름 68 · API 41이다.
        // `NOLOCK` 검사의 설계 근거 자체가 「산문 약 300 대 코드 0」이므로 그 근거가
        // 그대로 뒤집힌다.
        //
        // 형제 헬퍼 `CleanedSqlFences`·`CleanedCodeFences`는 건드리지 않는다 - 명세서
        // 경로의 검사들이 함께 쓰고 있어 판정 범위가 바뀐다.

        /// <summary>
        /// 필수 H2 넷을 갖추되 <b>여분의 펜스 마커 하나</b>를 앞에 둔 계획서.
        ///
        /// 마커가 홀수(5개)라 페어링이 한 칸 밀리고, 그 결과 <c>{body}</c>의 <b>산문이
        /// 코드로 잡힌다</b>. 실측으로 확인한 배치다 - 마커를 앞에 넣지 않으면
        /// 잡히는 구간이 어긋나기만 하고 본문은 계속 펜스 밖에 남는다.
        /// </summary>
        private static string PlanWithUnbalancedFence(string body) => $"""
            ## 통합 배치 아키텍처 개요

            ```

            내용.

            ```sql
            SELECT 1;
            ```

            ## 단계별 이행 상세 및 의사코드

            {body}

            ```mermaid
            flowchart TD
            A["시작"] --> B["끝"]
            ```

            ## Mermaid 기반 통합 흐름도

            ## 통합 데이터 정합성 검증 SQL 세트

            내용.
            """;

        [Fact]
        public void ValidateConsolidated_StaysSilentOnAllFourWhenTheFencesDoNotPair()
        {
            // 본문은 산문이다. 짝이 밀리면 이 산문이 「코드」로 읽혀 넷이 전부 발화한다.
            var markdown = PlanWithUnbalancedFence("""
                원본의 `WITH (NOLOCK)` 힌트는 전부 제거한다. `GOTO` 라벨과
                `IF @@ERROR <> 0` 검사, `BEGIN TRY`/`END CATCH` 감싸기를 쓰지 않는다.
                `SqlConnection`이나 `TransactionScope` 같은 실존 타입도 이름 대지 않으며,
                `CREATE PROCEDURE`로 새 저장 프로시저를 정의하지 않는다.
                """);

            var result = Validate(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e =>
                e.Type == ErrorType.NoLockHintInCode ||
                e.Type == ErrorType.FrameworkTypePrescribed ||
                e.Type == ErrorType.SqlSideControlFlow ||
                e.Type == ErrorType.NewDatabaseObjectDefined);
        }

        [Fact]
        public void ValidateConsolidated_PassesADocumentMarkdigConsidersWellFormed()
        {
            // ⚠️ 이 테스트를 처음 쓸 때의 전제가 틀렸다. 「짝이 안 맞으니 Markdig가
            // 뒤따르는 H2를 삼켜 필수 헤더 누락으로 어차피 반려될 것」이라고 적었는데,
            // 실행해 보니 **IsValid == true**였다.
            //
            // Markdig는 이 문서를 정상으로 본다 - 정보 문자열이 붙은 ```sql을 닫는
            // 자리로 인정하지 않으므로 마커 다섯이 블록 둘로 온전히 갈린다. 즉
            // **문서가 깨진 것이 아니라 옛 정규식이 깨져 있었다.**
            //
            // 그래서 「간접 방어」가 성립하지 않는다. 문서가 무효라서 유령이 함께
            // 떨어지는 것이 아니라, **L1이 초록인 채로 넷만 유령을 냈다.** 이 테스트가
            // 그 사실을 고정한다.
            var markdown = PlanWithUnbalancedFence("내용.");

            var result = Validate(markdown);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateConsolidated_StillFiresWhenTheFencesPair()
        {
            // 가드가 정상 문서를 끄지 않는다는 회귀 가드. 같은 위반을 짝이 맞는
            // 펜스에 담으면 그대로 잡혀야 한다.
            var markdown = Plan("""
                ```sql
                SELECT 1 FROM SETTLE_POQ_DB.dbo.TSettleMst WITH (NOLOCK);
                ```
                """);

            Assert.True(Fires(markdown, ErrorType.NoLockHintInCode));
        }

        // ── mermaid: CLI가 보고한 파스 오류는 강등하지 않는다 ───────────────────
        //
        // `ValidateMermaid`에는 Fallback으로 가는 갈래가 넷 있다 - CLI 종료 코드 != 0 ·
        // 시간 초과 · 예외 · CLI 비활성. 뒤 셋은 「도구가 답을 못 줬다」이고 첫째만
        // **「도구가 정상 실행되어 파스 오류를 보고했다」**, 즉 확정된 발견이다.
        //
        // 실측(2026-08-29 3차 통제군): `sequenceDiagram`의 `Settle--->Batch`가 CLI에
        // 두 번 잡히고도 채택본에 남았다. 코퍼스 23편의 mermaid 블록 60개를 mmdc로
        // 전수 검증하니 58 통과 · 2 실패이고 실패 둘이 전부 그 부류다.
        //
        // **원인 재귀속(2026-09-04) - 위 실측은 유효하되 읽는 법이 바뀌었다.**
        // 그 실패 둘은 모델이 쓴 것이 아니라 `CleanseMermaidCode`의 flowchart 전용
        // 화살표 보정이 sequenceDiagram의 **유효한** `-->>`를 `--->`로 부순 결과였다.
        // 정화를 flowchart 계열로 한정한 뒤 이 부류는 나지 않는다 - 그러므로
        // **「23편 중 2편」은 앞으로의 반려율을 예측하지 않는다.** 이 자리가 조용해지면
        // 「모델이 잘 쓰게 됐다」가 아니라 「도구가 부수기를 멈췄다」로 읽어라.
        // 강등하지 않는다는 판단 자체는 그대로다 - 도구가 정상 실행되어 보고한
        // 파스 오류는 여전히 확정된 발견이다.
        //
        // CLI가 필요한 갈래는 단위 테스트로 못 돌린다. 여기서는 **CLI를 끈 판정기**가
        // 종전대로 Fallback만 쓰는지(=강등 갈래가 안 바뀌었는지)를 고정한다.

        [Fact]
        public void ValidateConsolidated_WithoutTheMermaidCliDoesNotReportACompileError()
        {
            // 기본 생성자는 useMermaidCli: false다. 그 경로는 「도구가 답을 못 줬다」이므로
            // 컴파일 오류를 만들면 안 된다 - 만들면 CLI 없는 환경 전체가 반려된다.
            var markdown = Plan("""
                ```mermaid
                sequenceDiagram
                Settle--->Batch: S12 반환 코드 전달
                ```
                """);

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.MermaidCliError);
        }

        [Fact]
        public void SuggestedPromptFix_NamesTheMeasuredArrowClass()
        {
            // 시정 문구가 측정된 부류를 이름으로 짚어야 한다. 버킷 3의 체크리스트가
            // `->`·`- ->`만 들고 있어 `--->`(flowchart에는 유효)가 빠져 있었다.
            var result = new ValidationResult { IsConsolidated = true };
            result.Errors.Add("mermaid 컴파일 실패");
            result.DetailedErrors.Add(new DetailedError
            {
                Type = ErrorType.MermaidCliError,
                Message = "Parse error on line 20",
            });

            var fix = result.SuggestedPromptFix;

            Assert.NotNull(fix);
            Assert.Contains("sequenceDiagram", fix);
            Assert.Contains("->>", fix);
        }

        // ── 발화량과 시정 문구 ──────────────────────────────────────────────────

        [Fact]
        public void ValidateConsolidated_ReportsOneErrorPerCheckNoMatterHowManyTokensFire()
        {
            // 옛 코퍼스 한 편이 제어 흐름 토큰을 최대 280개 낸다. 토큰마다 오류를
            // 하나씩 만들면 SuggestedPromptFix가 못 읽을 것이 된다.
            var markdown = Plan("""
                ```sql
                IF @@ERROR <> 0 GOTO ERR1;
                IF @@ERROR <> 0 GOTO ERR2;
                IF @@ERROR <> 0 GOTO ERR3;
                BEGIN TRY
                    SELECT 1;
                END TRY
                BEGIN CATCH
                    SELECT 2;
                END CATCH
                ```
                """);

            var fired = Validate(markdown).DetailedErrors
                .Where(e => e.Type == ErrorType.SqlSideControlFlow)
                .ToList();

            Assert.Single(fired);
        }

        [Fact]
        public void ValidateConsolidated_TellsTheModelWhatToWriteInsteadOfTheFrameworkType()
        {
            // 시정 문구가 틀리면 재생성으로 고칠 수 없다. catch-all 버킷 8은
            // "기계 확정 표를 축자로 옮기십시오"라고 말하는데, 이 셋에는 그것이
            // 틀린 지시다.
            var markdown = Plan("""
                ```csharp
                using var tran = conn.BeginTransaction(IsolationLevel.Snapshot);
                ```
                """);

            var fix = Validate(markdown).SuggestedPromptFix;

            Assert.NotNull(fix);
            Assert.Contains("conn.beginTransaction()", fix);
            Assert.DoesNotContain("기계 확정 표를 문서가 그대로 담지 않았습니다", fix);
        }

        [Fact]
        public void ValidateConsolidated_MarksThePlanInvalidWhenAnySqlPlacementCheckFires()
        {
            var markdown = Plan("""
                ```sql
                SELECT 1 FROM SETTLE_POQ_DB.dbo.TSettleMst WITH (NOLOCK);
                ```
                """);

            Assert.False(Validate(markdown).IsValid);
        }

        [Fact]
        public void ValidateConsolidated_StaysSilentOnAPlanThatObeysAllThree()
        {
            var markdown = Plan("""
                ### S05 — 정산원장 재적재

                이 단계는 SNAPSHOT 격리 수준 아래에서 실행되어야 하며, 원본의
                `WITH(NOLOCK)` 힌트는 전부 제거한다. 트랜잭션 경계와 실패 판단은
                애플리케이션이 소유한다.

                ```pseudocode
                tx = conn.beginTransaction()
                for chunk in chunks(targetKeys):
                    repository.execute(deleteStatement, chunk)
                tx.commit()
                ```

                ```sql
                DELETE FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE YMD = @pi_strYMD AND PLTID BETWEEN @lo AND @hi;
                ```
                """);

            var result = Validate(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e =>
                e.Type == ErrorType.NoLockHintInCode ||
                e.Type == ErrorType.FrameworkTypePrescribed ||
                e.Type == ErrorType.SqlSideControlFlow);
        }
    }
}
