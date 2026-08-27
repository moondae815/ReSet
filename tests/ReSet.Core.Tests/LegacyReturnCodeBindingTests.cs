using System;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 레거시 반환 코드가 계약이 정한 저널 컬럼에 결속되는지 보는 검사의 잠금.
    ///
    /// [왜 이름이 아니라 결속인가] 코퍼스 20개 전부가 `@po_intRetVal`을 보존하지만
    /// 운반체 이름은 최소 넷으로 갈린다(LegacyReturnCode·LegacyRetVal·LegacyErrorCode·
    /// ErrorCode). 이름으로 재면 의무를 이행한 계획서가 실패로 잡히고, 반대로
    /// 자기가 만든 표에 그 이름을 65회 쓴 계획서(POQSettleProc12)가 통과한다.
    /// 판정 기준은 값이 계약 표의 계약 컬럼에 <b>쓰기 자리로</b> 닿는가 하나다.
    ///
    /// 픽스처는 실제 코퍼스에서 옮겼다 - 좌표는 각 테스트에 적는다.
    /// </summary>
    public class LegacyReturnCodeBindingTests
    {
        private static ValidationResult Validate(string markdown) =>
            new MechanicalValidator().ValidateConsolidated(markdown);

        private static bool Fires(string markdown) =>
            Validate(markdown).DetailedErrors.Any(
                e => e.Type == ErrorType.LegacyReturnCodeNeverBound);

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

        // ── 판정 1: 레거시 반환 코드를 보존하는데 결속이 없다 ────────────────────
        //
        // 코퍼스 실패 14건 중 13건이 이 모양이다 - 계약 표를 문서 어디에서도
        // 부르지 않는다(POQSettleProc1·2·3·4·6·7·8·9·10·11·12·13·14). 계약 표가
        // 없을 때 조용히 넘어가면(CheckBatchRunRowCreation의 "언급되지 않으면
        // 소프트 스킵" 관례) 이 13건이 전부 통과한다 - 그래서 이 검사는 그
        // 소프트 스킵을 두지 않는다.
        [Fact]
        public void ValidateConsolidated_ReportsWhenTheLegacyCodeIsPreservedButNeverBound()
        {
            // POQSettleProc1의 모양: 값이 dbo.POQSettleSqlErrorLog로 간다.
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                INSERT INTO dbo.POQSettleSqlErrorLog
                    (RunId, StepCode, LegacyRetVal, ErrorMessage, RecordedAt)
                VALUES
                    (@v_runId, N'S01', @v_currentStepId, @v_sqlErrorMessage, SYSUTCDATETIME());
                ```
                """);

            Assert.True(Fires(markdown));
        }

        /// <summary>
        /// 오류 문구는 계약이 가진 이름을 그대로 말해야 한다. 문구가 컬럼을 지목하지
        /// 않으면 이 오류는 SuggestedPromptFix를 타고 재생성 프롬프트에 실려도 어디에
        /// 무엇을 쓰라는 지시가 되지 않는다.
        /// </summary>
        [Fact]
        public void ValidateConsolidated_NamesTheContractTableAndColumnInTheMessage()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                UPDATE dbo.POQBatchRun SET LegacyRetVal = @v_currentStepId WHERE RunId = @v_runId;
                ```
                """);

            var message = Validate(markdown).DetailedErrors
                .Single(e => e.Type == ErrorType.LegacyReturnCodeNeverBound).Message;

            Assert.Contains("batch.BatchStepJournal", message);
            Assert.Contains("LegacyReturnCode", message);
        }

        // ── 판정 2: 결속이 있으면 침묵한다 ───────────────────────────────────────
        //
        // POQSettleProc18:363의 모양. C# 운반체 이름이 무엇이든(여기서는
        // `@v_legacyRetVal`) 값이 계약 컬럼에 닿으면 이행이다.
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheValueReachesTheColumnUnderAnyFieldName()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 `LegacyRetVal`로 보존한다.

                ```sql
                UPDATE batch.BatchStepJournal
                   SET StepStatus = N'Failed',
                       LegacyReturnCode = @v_legacyRetVal,
                       CompletedAtUtc = SYSUTCDATETIME()
                 WHERE RunId = @p_RunId
                   AND StepCode = @p_StepCode;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 판정 3: 보존할 레거시 코드가 없으면 침묵한다 ─────────────────────────
        //
        // 레거시 출신이 아닌 제어 단계는 물려받을 반환 코드가 없다. 이 가지가
        // 없으면 배치 골격만 있는 계획서가 전부 발화한다.
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenNoLegacyReturnValueIsPreserved()
        {
            var markdown = Plan("""
                이 단계는 레거시 출신 프로시저가 없다.

                ```sql
                INSERT INTO batch.BatchStepJournal
                    (RunId, StepCode, StepStatus, StartedAtUtc, CompletedAtUtc, ErrorMessage)
                VALUES
                    (@RunId, N'S01', N'Running', SYSUTCDATETIME(), NULL, NULL);
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 판정 4: 컬럼 이름이 계약과 다르면 발화한다 ───────────────────────────
        //
        // POQSettleProc15:306-325의 실물. 계약 표에 실제로 쓰고 값도 싣지만 컬럼이
        // `LegacyErrorCode`다. 표만 보는 검사는 이 한 건을 놓친다 - 코퍼스에서
        // 유일하게 계약 표를 부르면서 실패한 건이다.
        [Fact]
        public void ValidateConsolidated_ReportsWhenTheJournalWriteNamesADifferentColumn()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                INSERT INTO batch.BatchStepJournal
                (
                    RunId,
                    StepCode,
                    Status,
                    LegacyErrorCode,
                    SqlErrorNumber,
                    ErrorMessage,
                    RecordedAt
                )
                VALUES
                (
                    @p_RunId,
                    @p_StepCode,
                    N'Failed',
                    @v_currentStepId,
                    @v_sqlErrorNumber,
                    @v_sqlErrorMessage,
                    SYSUTCDATETIME()
                );
                ```
                """);

            Assert.True(Fires(markdown));
        }

        // ── 판정 5: 표가 계약 표가 아니면 발화한다 ───────────────────────────────
        //
        // POQSettleProc12:239의 실물. 컬럼 이름은 계약과 같지만 자기가 새로 만든
        // batch.BatchTaskRun 위에 있다. 이름으로 grep하면 가장 성실해 보이는
        // 계획서가 실패다.
        [Fact]
        public void ValidateConsolidated_ReportsWhenTheColumnBelongsToAnotherTable()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 `@v_legacyRetVal`로 보존한다.

                ```sql
                UPDATE batch.BatchTaskRun
                   SET Status = N'Committed',
                       LegacyReturnCode = @v_legacyRetVal,
                       CompletedAt = sysdatetime()
                 WHERE RunId = @RunId
                   AND StepCode = @StepCode;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        // ── 판정 6: 읽기는 결속이 아니다 ─────────────────────────────────────────
        //
        // 같은 펜스에 표 이름과 컬럼 이름이 함께 있기만 하면 참으로 보면, 값을
        // 회수해 읽기만 하는 질의(POQSettleProc9:4365가 그 모양이다)가 결속으로
        // 통과한다. 쓰기 자리(INSERT 컬럼 목록·UPDATE SET 대상)로 좁혀야 한다.
        [Fact]
        public void ValidateConsolidated_ReportsWhenTheColumnOnlyAppearsInAReadingQuery()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                SELECT @po_intRetVal = LegacyReturnCode
                  FROM batch.BatchStepJournal
                 WHERE RunId = @RunId
                   AND StepCode = @StepCode;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        // ── 판정 7: 산문의 약속은 결속이 아니다 ──────────────────────────────────
        //
        // POQSettleProc15:1043의 매핑 표가 컬럼 이름을 적지만 SQL은 다른 컬럼에
        // 쓴다. 산문에 쓰기 문장을 <b>문장 모양 그대로</b> 인용하는 것도 마찬가지다 -
        // 코드 블록 밖의 문장은 계획이지 구현 지시가 아니다. 이 픽스처가 인용문을
        // 담는 이유는 그것이다: 담지 않으면 "문서 전체를 훑는다"는 변이가 살아남는다
        // (쓰기 자리 좁힘만으로는 산문에 걸릴 것이 없어 판정이 안 갈린다).
        [Fact]
        public void ValidateConsolidated_ReportsWhenTheBindingIsOnlyClaimedInProse()
        {
            var markdown = Plan("""
                | 업무 오류 코드 | `LegacyReturnCode` | `@po_intRetVal INT OUTPUT` | 원본 코드를 그대로 기록한다. |

                모든 단계의 `@po_intRetVal`은 `batch.BatchStepJournal.LegacyReturnCode`에 기록한다.
                예를 들어 `UPDATE batch.BatchStepJournal SET LegacyReturnCode = @v_currentStepId`
                형태로 기록할 예정이다.

                ```sql
                UPDATE batch.BatchStepJournal
                   SET StepStatus = N'Failed',
                       ErrorMessage = @v_sqlErrorMessage
                 WHERE RunId = @RunId
                   AND StepCode = @StepCode;
                ```
                """);

            Assert.True(Fires(markdown));
        }

        // ── 판정 11: 결속은 SQL 펜스에만 사는 것이 아니다 ────────────────────────
        //
        // POQSettleBatch1:429-497의 실물. 언어 이전 뒤의 코드를 ```pseudocode
        // 펜스에 C# 모양으로 적고 SQL은 그 안의 문자열로 싣는다. ```sql 펜스만
        // 보면 이 형태로만 결속한 계획서가 실패로 잡힌다 - 오탐은 L1 재시도를
        // 소진시키므로 코드 블록이면 언어를 가리지 않는다.
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheBindingLivesInAPseudocodeFence()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```pseudocode
                async RunStepAsync(runId, cancellationToken):
                    await connection.ExecuteAsync(
                        "UPDATE batch.BatchStepJournal
                            SET StepStatus = 'Failed',
                                LegacyReturnCode = @LegacyCode,
                                CompletedAtUtc = SYSUTCDATETIME()
                          WHERE RunId = @RunId
                            AND StepCode = @StepCode;",
                        { RunId = runId, LegacyCode = legacyCode })
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 판정 8: 별칭으로 한 UPDATE도 결속이다 ────────────────────────────────
        //
        // docs/architecture.md:433-434가 표준 관용으로 명시한 형태다. 이 가지가
        // 없으면 정상 결속이 실패로 잡힌다(오탐).
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheUpdateBindsThroughAnAlias()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                UPDATE bsj
                   SET bsj.StepStatus = N'Failed',
                       bsj.LegacyReturnCode = @v_currentStepId
                  FROM batch.BatchStepJournal bsj
                 WHERE bsj.RunId = @RunId;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 판정 9: 표 이름의 대괄호 인용도 계약 표다 ────────────────────────────
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheTableNameIsBracketQuoted()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                UPDATE [batch].[BatchStepJournal]
                   SET LegacyReturnCode = @v_currentStepId
                 WHERE RunId = @RunId;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 판정 10: 컬럼 이름의 대괄호 인용도 계약 컬럼이다 ─────────────────────
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheColumnNameIsBracketQuoted()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                INSERT INTO batch.BatchStepJournal
                    (RunId, StepCode, StepStatus, [LegacyReturnCode], StartedAtUtc)
                VALUES
                    (@RunId, N'S01', N'Running', @v_currentStepId, SYSUTCDATETIME());
                ```
                """);

            Assert.False(Fires(markdown));
        }

        /// <summary>
        /// 이 검사는 표 이름과 컬럼 이름을 <see cref="BatchControlContract"/>에서
        /// 해석한다. 계약이 그 이름을 바꾸면 해석이 실패해 검사가 <b>조용히</b>
        /// 아무것도 잡지 않게 된다 - 이 테스트가 그 순간을 빨간불로 만든다.
        /// 검사 본문의 조회 키를 바꾸는 것이 그때 해야 할 일이다.
        /// </summary>
        [Fact]
        public void BatchControlContract_StillDeclaresTheJournalColumnThisCheckResolves()
        {
            var table = BatchControlContract.Find("batch.BatchStepJournal");

            Assert.NotNull(table);
            Assert.Contains(table!.Columns,
                c => string.Equals(c.Name, "LegacyReturnCode", StringComparison.Ordinal)
                     && c.SqlType == "int");
        }

        // ── 판정 12: MERGE의 WHEN MATCHED UPDATE SET도 쓰기 자리다 ────────────────
        //
        // 코퍼스 20건 중 7건이 이미 MERGE로 제어 표를 갱신한다(Proc3·6·7·10·12·13·15,
        // 실물 모양은 POQSettleProc12:247-258). Task 1 §3-3도 "MERGE가 쓰기 문장인데
        // 초판 스캔이 놓쳤다"고 경고했고, 형제 헬퍼 CreatesRowIn(:958)은 같은 계약
        // 표들에 대해 이미 MERGE를 행 생성으로 인정한다. 인정하지 않으면 이 형태로
        // 결속한 계획서를 거짓 고발한다.
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheMergeMatchedBranchBindsTheColumn()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                MERGE batch.BatchStepJournal AS target
                USING
                (
                    SELECT @RunId AS RunId, @StepCode AS StepCode
                ) AS source
                   ON target.RunId = source.RunId
                  AND target.StepCode = source.StepCode
                WHEN MATCHED THEN
                    UPDATE SET
                        StepStatus = N'Succeeded',
                        LegacyReturnCode = @v_currentStepId,
                        CompletedAtUtc = SYSUTCDATETIME();
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 판정 13: MERGE의 WHEN NOT MATCHED INSERT 컬럼 목록도 쓰기 자리다 ──────
        //
        // 같은 MERGE의 반대 가지다. 갱신 가지만 인정하면 행을 새로 만들며 결속하는
        // 계획서가 거짓 고발된다(POQSettleProc12:256-258이 그 모양이다).
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheMergeNotMatchedBranchBindsTheColumn()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                MERGE batch.BatchStepJournal AS target
                USING
                (
                    SELECT @RunId AS RunId, @StepCode AS StepCode
                ) AS source
                   ON target.RunId = source.RunId
                  AND target.StepCode = source.StepCode
                WHEN NOT MATCHED THEN
                    INSERT (RunId, StepCode, StepStatus, LegacyReturnCode, StartedAtUtc)
                    VALUES (@RunId, @StepCode, N'Running', @v_currentStepId, SYSUTCDATETIME());
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 판정 14: MERGE 헤더의 별칭도 이 표에 묶인다 ───────────────────────────
        //
        // 별칭이 FROM/JOIN이 아니라 MERGE 헤더에서 묶이므로
        // ResolveControlTableAliases만으로는 잡히지 않는다. 못 잡으면 판정 12의
        // `target.LegacyReturnCode`가 "다른 표의 컬럼"으로 읽혀 결속이 무시된다.
        // 대괄호 표기(`MERGE INTO [batch].[BatchStepJournal] AS j`)도 같은 자리다.
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheMergeAliasIsBracketQuoted()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                MERGE INTO [batch].[BatchStepJournal] AS j
                USING (SELECT @RunId AS RunId) AS s
                   ON j.RunId = s.RunId
                WHEN MATCHED THEN
                    UPDATE SET j.LegacyReturnCode = @v_currentStepId;
                ```
                """);

            Assert.False(Fires(markdown));
        }

        // ── 판정 15: 오류는 ValidationResult.Errors에도 실려야 한다 ───────────────
        //
        // DetailedErrors만 보는 단언은 이 검사가 생산에서 도는지를 재지 않는다.
        // IsValid는 Errors.Count로만 정해지고, 그 IsValid가
        // VerificationPipelineOrchestrator의 재생성 분기와 SuggestedPromptFix를 켠다.
        // Errors.Add 한 줄이 빠지면 검사는 아무 일도 안 하면서 테스트 전부와 코퍼스
        // 대조가 초록으로 남는다 - 이 브랜치가 M6b에서 본 "일치가 판정을 보증하지
        // 않는다"의 가장 나쁜 형태다.
        [Fact]
        public void ValidateConsolidated_MarksThePlanInvalidWhenTheBindingIsMissing()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                INSERT INTO dbo.POQSettleSqlErrorLog
                    (RunId, StepCode, LegacyRetVal, ErrorMessage, RecordedAt)
                VALUES
                    (@v_runId, N'S01', @v_currentStepId, @v_sqlErrorMessage, SYSUTCDATETIME());
                ```
                """);

            var result = Validate(markdown);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("LegacyReturnCode"));
        }

        // ── 판정 16: 한정자는 대조 대상이 아니다 ─────────────────────────────────
        //
        // BatchControlContract.Find가 "한정자가 있든 없든" 찾는 것과 같은 관례다
        // (BatchControlContract.cs:252). 형제 검사들도 QualifiedTableNameFragment로
        // 스키마를 무시하고 맨이름만 본다 - 단계 문서가 같은 표를 batch.X로도 X로도
        // 쓰기 때문이다. 그래서 `dbo.BatchStepJournal`에 대한 결속도 인정한다.
        //
        // 이 인정은 판정 5(다른 표에 쓰면 발화)와 충돌하지 않는다 - 저기서 갈리는
        // 것은 맨이름(BatchTaskRun ≠ BatchStepJournal)이고, 여기서 무시되는 것은
        // 한정자뿐이다. 이 결정은 코퍼스로 변별되지 않으므로(스키마를 요구해도 발화
        // 집합 14/6 불변, 최종 리뷰 실측) 이 테스트가 유일한 잠금이다.
        [Fact]
        public void ValidateConsolidated_StaysSilentWhenTheBindingUsesAnotherSchemaQualifier()
        {
            var markdown = Plan("""
                원본 출력 `@po_intRetVal`을 그대로 보존한다.

                ```sql
                UPDATE dbo.BatchStepJournal
                   SET StepStatus = N'Failed',
                       LegacyReturnCode = @v_currentStepId
                 WHERE RunId = @RunId
                   AND StepCode = @StepCode;
                ```
                """);

            Assert.False(Fires(markdown));
        }
    }
}
