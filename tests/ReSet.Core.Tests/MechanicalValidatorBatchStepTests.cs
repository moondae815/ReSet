using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class MechanicalValidatorBatchStepTests
    {
        // 조건 컬럼 대조를 쓰지 않는 테스트용 빈 재료. 비어 있으면 검사가
        // 소프트 스킵하므로 이 테스트들이 보는 동작은 달라지지 않는다.
        private static readonly System.Collections.Generic.IReadOnlyDictionary<string, SpecConditions> NoConditions =
            new System.Collections.Generic.Dictionary<string, SpecConditions>();

        /// <summary>본체 조건만 가진 재료.</summary>
        private static IReadOnlyDictionary<string, SpecConditions> Body(string procedure, params string[] columns) =>
            new Dictionary<string, SpecConditions>(StringComparer.OrdinalIgnoreCase)
            {
                [procedure] = new SpecConditions(
                    columns,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase))
            };

        /// <summary>UDF 하나에 딸린 조건만 가진 재료.</summary>
        private static IReadOnlyDictionary<string, SpecConditions> Udf(
            string procedure, string udf, params string[] columns) =>
            new Dictionary<string, SpecConditions>(StringComparer.OrdinalIgnoreCase)
            {
                [procedure] = new SpecConditions(
                    Array.Empty<string>(),
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [udf] = columns
                    })
            };

        private static BatchStepPlan Step(params string[] targetTables) => new(
            Code: "S17",
            Name: "완료 파티션 원자적 게시",
            LegacyProcedures: Array.Empty<string>(),
            TargetTables: targetTables,
            ErrorCodes: Array.Empty<string>(),
            Chunkable: false,
            SchemaTables: Array.Empty<string>());

        private static readonly string[] Catalog = { "dbo.TSettleMst", "dbo.TStatPGCollect", "dbo.TSettleMiss" };

        private static StepValidationResult Validate(string markdown, BatchStepPlan step) =>
            new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>());

        /// <summary>
        /// 실물 코퍼스 모양의 갱신 절 하나. 헤더 문구·칸 순서는
        /// `output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md`에서 옮겼다.
        /// </summary>
        private static IReadOnlyDictionary<string, SpecStatementFacts> SetFacts(string procedure, string spec) =>
            SpecStatementFactsExtractor.Extract(new[] { (procedure, spec) });

        private const string ExceptionProcSpec = """
            ### UPDATE 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst (갱신 10 · 원본 DDL 라인 301 · 원문 표기: TSettleMst)

            | 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
            | :--- | :--- | :--- | :--- |
            | SETTLE_POQ_DB.dbo.TSettleMst | PGVT | CAST(ROUND(A.PGCOMM * 0.1, 0, dbo.UF_GET_PGCommOption(A.PGNAME, 5)) AS INT) | 반올림 옵션을 UDF로 받습니다. |

            ### UPDATE 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst (갱신 12 · 원본 DDL 라인 340 · 원문 표기: TSettleMst)

            | 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
            | :--- | :--- | :--- | :--- |
            | SETTLE_POQ_DB.dbo.TSettleMst | CLTotal | 1 | 한 자리 상수뿐이라 판별 토큰이 없습니다. |
            """;

        private static StepValidationResult ValidateWithSetFacts(string markdown, string spec)
        {
            var step = new BatchStepPlan(
                Code: "S07", Name: "예외 정책 적용",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(), Chunkable: false,
                SchemaTables: Array.Empty<string>());

            return new MechanicalValidator().ValidateBatchStep(
                markdown, step, Catalog, NoConditions,
                statementFactsByProcedure: SetFacts("dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", spec),
                allSteps: new[] { step });
        }

        /// <summary>
        /// 축 B 감사 🔴(POQSettleBatch1/S07)의 실물 모양. 명세서가 갱신의 SET 우변 산식을
        /// 전문으로 갖는데 단계 본문은 컬럼 이름과 한 줄 주석만 남기고 산식을 버렸다 —
        /// 상수·계수·UDF 인자가 없으면 이 절만으로 구현할 때 금액이 원본과 달라진다.
        ///
        /// 실측(2026-09-05): 감사가 지목한 갱신 10·11 에서 빠진 것이 정확히
        /// `UF_GET_PGCommOption`·`0.1` 이었고, 코퍼스 전수에서 이 규칙의 발화는 400 건 중
        /// 6 건 · 전건 감사 결과 거짓 0 건이다.
        /// </summary>
        [Fact]
        public void SetExpression_WhenStepDropsEveryDistinctiveToken_IsReported()
        {
            var markdown = Section("""
                /* U10: PG 부가세 재계산 */
                UPDATE A SET PGVT = @v_recalculated FROM dbo.TSettleMst AS A;
                """);

            var result = ValidateWithSetFacts(markdown, ExceptionProcSpec);

            Assert.Contains(result.Errors, e => e.Contains("갱신 10", StringComparison.Ordinal));
            Assert.Contains(result.Errors, e => e.Contains("UF_GET_PGCommOption", StringComparison.Ordinal));
        }

        /// <summary>
        /// 산식을 실은 단계는 침묵해야 한다. 재생성본(현행 S08)이 이 모양이고, 그것이
        /// 이 검사가 <b>만족 가능</b>하다는 증거다 — 모델이 실제로 쓸 수 있는 것을 요구한다.
        /// </summary>
        [Fact]
        public void SetExpression_WhenStepCarriesTheTokens_IsSilent()
        {
            var markdown = Section("""
                /* U10: PG 부가세 재계산 */
                UPDATE A SET PGVT = CAST(ROUND(A.PGCOMM * 0.1, 0, dbo.UF_GET_PGCommOption(A.PGNAME, 5)) AS INT)
                FROM dbo.TSettleMst AS A;
                """);

            var result = ValidateWithSetFacts(markdown, ExceptionProcSpec);

            Assert.DoesNotContain(result.Errors, e => e.Contains("갱신 10", StringComparison.Ordinal));
        }

        /// <summary>
        /// 판별 토큰이 없는 갱신(산식이 한 자리 상수뿐)은 <b>대조가 성립하지 않는다.</b>
        /// 그런 자리를 발화시키면 정상 문서가 결함이 된다 — 작성 계약 7(귀속이 불가능하면
        /// 침묵하라).
        ///
        /// [2026-09-05 개정 - A-2] 원래 이 시험은 「컬럼 참조뿐인 산식(`A.CLCOMM`)」으로
        /// 이 침묵 경계를 보였다. 그런데 그 부류(순수 컬럼 산술)가 정확히 A-2가 닫은
        /// 사각지대다 - `DistinctiveExpressionTokens`가 이제 별칭.컬럼 토큰을 내므로
        /// `A.CLCOMM`은 더 이상 토큰 0이 아니다(그 발화는 결함이 아니라 이 회차가 의도한
        /// 개선이다 - <c>MechanicalValidatorTests.
        /// ValidateBatchStep_CheckSetExpressions_FiresWhenOnlyPlainColumnArithmeticIsMissing</c>
        /// 참고). 그래서 이 시험이 지키려는 「진짜 토큰 0」 경계는 한 자리 상수로 옮겼다 -
        /// 한 자리 수는 여전히 어느 후보에도 안 걸린다(코퍼스 실측, 판독 문서
        /// docs/audit-reports/2026-09-05-set-expression-token-readout-b1.md).
        /// </summary>
        [Fact]
        public void SetExpression_WhenExpressionHasNoDistinctiveToken_IsNotJudged()
        {
            // 갱신 12 의 산식은 한 자리 상수 `1` 뿐이라 토큰이 없다. 본문이 그것을 안 담아도
            // 발화하지 않아야 한다.
            var markdown = Section("UPDATE A SET CLTotal = 0 FROM dbo.TSettleMst AS A;");

            var result = ValidateWithSetFacts(markdown, ExceptionProcSpec);

            Assert.DoesNotContain(result.Errors, e => e.Contains("갱신 12", StringComparison.Ordinal));
        }

        private static IReadOnlyList<StepInterface> Interfaces(string code, params string[] parameters) =>
            new[] { new StepInterface(code, new[] { "dbo.X" }, parameters) };

        private static string Section(string body) => $"""
            ### S17 완료 파티션 원자적 게시

            ```sql
            {body}
            ```
            """;

        private static string CSharpSection(string body) => $"""
            ### S17 완료 파티션 원자적 게시

            ```csharp
            {body}
            ```
            """;

        [Fact]
        public void ValidateBatchStep_ShouldRejectATableThatIsInNoCatalog()
        {
            // 실측: S17이 dbo.TSettleSummary를 게시 대상으로 지목했는데 그 테이블은
            // 이 작업의 DDL 55종 어디에도 없다. 구현 자체가 불가능한 지시다.
            var markdown = Section("EXEC batch.SwitchPublishedPartition @TargetTable = N'dbo.TSettleSummary';");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("dbo.TSettleSummary", StringComparison.Ordinal));
            // 본문 결함이므로 재생성으로 고칠 수 있어야 한다 - PlanDefects가 아니다.
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptTheBatchSchemaObjectsThePlanCreates()
        {
            // batch.*는 카탈로그에 없는 것이 정상이다. 이것을 결함으로 들면
            // 모든 단계가 전부 오탐으로 걸린다.
            var markdown = Section("INSERT INTO batch.POQSettleCheckpoint SELECT * FROM dbo.TSettleMst;");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("POQSettleCheckpoint", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptTablesThatAreInTheCatalog()
        {
            var markdown = Section("UPDATE dbo.TSettleMst SET OutState = 9;");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            // 이전 단언 `e.Contains("존재하지")`는 실제 오류 메시지("...없습니다.")에
            // 그 부분 문자열이 아예 없어, 구현이 무엇을 하든 항상 통과했다 - 이름이
            // 주장하는 "카탈로그에 있는 테이블은 받아들인다"를 이 테스트가 실제로는
            // 전혀 검증하지 못하고 있었다. 오류가 하나도 없어야 한다는 실제 의도로 바꾼다.
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateBatchStep_ShouldIgnoreTableNamesThatAppearOnlyInProse()
        {
            // 추출 범위를 백틱과 SQL 펜스로 제한한 것을 고정한다. 산문까지 훑으면
            // "요약 테이블" 같은 서술이 식별자로 오인된다.
            var markdown = """
                ### S17 완료 파티션 원자적 게시

                게시 대상은 dbo.TSettleSummary 계열이다.

                ```sql
                SELECT 1;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("dbo.TSettleSummary", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldIgnoreAliasQualifiedColumnsInSql()
        {
            // 리뷰어 재현: 별칭 a로 접근한 컬럼(a.YMD, a.AMT)은 미지 테이블 검사의
            // 대상이 아니다. 별칭 a는 카탈로그가 아는 한정자가 아니므로 후보에서
            // 애초에 제외되어야 한다 - 이걸 놓치면 별칭을 쓰는 모든 정상 SQL이 걸린다.
            var markdown = Section("""
                SELECT a.YMD, a.AMT
                FROM dbo.TSettleMst AS a
                WHERE a.YMD = @Ymd;
                """);

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateBatchStep_ShouldIgnoreMemberAccessInCSharpPseudocodeFences()
        {
            // 리뷰어 재현: context.RunId, conn.Execute 같은 멤버 접근이 SQL이 아닌
            // 의사코드 펜스에서도 미지 테이블로 오탐되면, T2가 SettleContext에 붙인
            // RunId조차 매 회차 오탐으로 걸린다. csharp 펜스는 언어 무관 검사
            // 대상이지만, 한정자 화이트리스트가 context·conn을 걸러내야 한다.
            var markdown = CSharpSection("""
                var runId = context.RunId;
                conn.Execute("UPDATE dbo.TSettleMst SET OutState = 9", new { runId });
                """);

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateBatchStep_ShouldLeaveALocallyDefinedRoutineToTheNewObjectCheck()
        {
            // 소유권 이전. 미지 테이블 검사가 이 이름을 "실재하지 않는 테이블"로 잡고
            // "신규 객체라면 batch 스키마에 두십시오"라고 답하는데, 규칙 3-1은 신규
            // 저장 프로시저를 **아예** 금지하므로 틀린 지시다. 정의는 문서 검사
            // (CheckNewDatabaseObjectDefinition)가 규칙 3-1의 문구로 답한다.
            //
            // 실측(2026-08-29): ① 발화 190건 중 15건이 이 모양이다 - 같은 단계 본문이
            // 그 이름을 CREATE로 정의한다. 관례는 CheckNonCanonicalBatchSchema와 같다
            // ("여기서 다시 들면 같은 참조가 두 개의 다른 이름으로 걸린다").
            var markdown = Section("""
                CREATE OR ALTER PROCEDURE dbo.usp_POQSettelProc8_S11
                AS
                BEGIN
                    SELECT 1 FROM dbo.TSettleMst;
                END
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(
                result.Errors,
                e => e.Contains("usp_POQSettelProc8_S11", StringComparison.Ordinal)
                     && e.Contains("스키마 카탈로그에도", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldStillFlagARoutineThisStepOnlyCalls()
        {
            // 소유권은 **정의**를 따라간다. 이 단계가 부르기만 하고 정의는 다른 절에
            // 있으면 문서 검사가 그 정의를 잡지만, 이 단계에서는 여전히 카탈로그에
            // 없는 참조다 - 침묵시키면 아무 데도 정의가 없는 호출까지 함께 사라진다.
            var markdown = Section("EXEC dbo.WritePOQBatchError @Code = -1;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(
                result.Errors,
                e => e.Contains("WritePOQBatchError", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotAdviseCreatingAProcedureInTheBatchSchema()
        {
            // 남는 발화의 시정 문구가 규칙 3-1과 부딪히면 안 된다. 규칙 4-1도
            // "이 규칙은 신규 TABLE을 다스린다; 3-1이 신규 프로시저를 통째로
            // 금지하므로 batch 스키마 프로시저는 이 규칙이 여는 선택지가 아니다"
            // 라고 이미 못박아 두었다.
            var markdown = Section("EXEC dbo.WritePOQBatchError @Code = -1;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            var reported = Assert.Single(
                result.Errors, e => e.Contains("WritePOQBatchError", StringComparison.Ordinal));

            Assert.DoesNotContain("신규 객체라면 batch 스키마에 두십시오", reported);
            Assert.Contains("저장 프로시저", reported);
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotFlagAnotherStepsLegacyProcedure()
        {
            // 목차가 이 Job의 원본이라고 선언한 것은 **어느 단계 몫이든** 실재하는
            // 객체다. 화이트리스트가 `step.LegacyProcedures`, 즉 자기 단계 것만 보면
            // 다른 단계가 대체하는 원본을 언급하는 단계에서 그 이름이 유령이 된다.
            //
            // ⚠️ 이 확대가 코퍼스에서 고친 오탐은 0건이다(측정함) - 실측 29건의
            // 원인은 목차가 아니라 카탈로그였다(원본 SP 자신이 knownTableNames에
            // 없었다). 남기는 이유는 두 재료의 출처가 달라서다: 목차가 선언했는데
            // 정적 분석 대상이 아닌 원본은 카탈로그로 닫히지 않는다.
            var owner = new BatchStepPlan(
                Code: "S02", Name: "정산 등록",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_INS" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(), Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var orchestrator = Step("dbo.TSettleMst");

            var markdown = Section("SELECT 1 FROM dbo.TSettleMst;")
                + "\n\nS02가 대체하는 `dbo.UP_UTIL_SETTLE_INS`의 순서를 보존한다.\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, orchestrator, Catalog, NoConditions,
                allSteps: new[] { owner, orchestrator });

            Assert.DoesNotContain(
                result.Errors,
                e => e.Contains("UP_UTIL_SETTLE_INS", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldStillFlagAProcedureNoStepDeclares()
        {
            // 넓힌 화이트리스트가 판정을 약하게 만들지 않는다는 가드. 목차가 어느
            // 단계에서도 선언하지 않은 이름은 여전히 유령이다 - 실측 E-1 30건
            // (`dbo.usp_POQSettelProc8_S11` 등 계획서가 스스로 지어낸 프로시저)이
            // 계속 잡혀야 한다.
            var owner = new BatchStepPlan(
                Code: "S02", Name: "정산 등록",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_INS" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(), Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var orchestrator = Step("dbo.TSettleMst");
            var markdown = Section("EXEC dbo.usp_POQSettleProc8_S11 @Ymd;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, orchestrator, Catalog, NoConditions,
                allSteps: new[] { owner, orchestrator });

            Assert.Contains(
                result.Errors,
                e => e.Contains("usp_POQSettleProc8_S11", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_WithoutTheStepRoster_StillTrustsItsOwnLegacyProcedure()
        {
            // allSteps는 선택 인자다. 넘기지 않는 호출부(기존 테스트 다수)에서 종전
            // 동작이 그대로여야 한다 - 자기 단계가 선언한 원본은 여전히 실재로 친다.
            var owner = new BatchStepPlan(
                Code: "S02", Name: "정산 등록",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_INS" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(), Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var markdown = Section("SELECT 1 FROM dbo.TSettleMst;")
                + "\n\n`dbo.UP_UTIL_SETTLE_INS`를 이관한다.\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, owner, Catalog, NoConditions);

            Assert.DoesNotContain(
                result.Errors,
                e => e.Contains("UP_UTIL_SETTLE_INS", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldStillFlagExactlyOneUnknownDboTable()
        {
            // 회귀 가드: dbo가 카탈로그에서 알려진 한정자라도, 객체명 자체가
            // 카탈로그에 없으면 여전히 정확히 한 건 걸려야 한다 - 이것이 이 검사가
            // 존재하는 이유(S17 → dbo.TSettleSummary 실측)다.
            var markdown = Section("""
                SELECT 1 FROM dbo.TSettleMst;
                EXEC batch.SwitchPublishedPartition @TargetTable = N'dbo.TSettleSummary';
                """);

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Single(result.Errors);
            Assert.Contains(result.Errors, e => e.Contains("dbo.TSettleSummary", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldSkipTheCheckWhenTheCatalogIsEmpty()
        {
            // definitions가 null인 경로(오프라인 스냅숏 등)의 소프트 스킵.
            // 카탈로그가 없다는 이유로 모든 테이블을 유령으로 몰면 안 된다.
            var markdown = Section("EXEC batch.SwitchPublishedPartition @TargetTable = N'dbo.TSettleSummary';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Array.Empty<string>(), NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("dbo.TSettleSummary", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptTheLegacyProcedureTheStepDeclaresAsItsOrigin()
        {
            // 실측(POQSettleProc10): S04 본문이 "dbo.UP_Util_PG_Client_CMRate_Ins의 업무
            // 규칙을 이관한다"고 썼는데, 그 프로시저는 목차가 이 단계의 LegacyProcedures로
            // 선언한 바로 그것이다. known 집합이 TargetTables·SchemaTables만 담아
            // 출신 프로시저가 유령 테이블로 몰렸고, 9개 단계가 이 오탐 하나로
            // 하한 미달 배너를 받았다 - 단계마다 재생성 1회씩을 함께 태웠다.
            var step = new BatchStepPlan(
                Code: "S04",
                Name: "일별 요율 스냅샷 생성",
                LegacyProcedures: new[] { "dbo.UP_Util_PG_Client_CMRate_Ins" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: new[] { "-1" },
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var markdown = $"""
                ### S04 일별 요율 스냅샷 생성

                `dbo.UP_Util_PG_Client_CMRate_Ins`의 업무 규칙을 이관한다. 실패 시 `-1`.

                ```sql
                INSERT INTO dbo.TSettleMst (Ymd) VALUES (@Ymd);
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, NoConditions);

            Assert.DoesNotContain(
                result.Errors, e => e.Contains("UP_Util_PG_Client_CMRate_Ins", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectABatchSchemaThatIsNotTheCanonicalOne()
        {
            // 실측(POQSettleProc10): 배치 전용 스키마가 batch·poqbatch·poqsettlebatch로
            // 갈라졌고, 수집기는 batch만 알아 bootstrap이 나머지 객체를 만들지 않았다.
            // 미지 테이블 검사는 이것을 잡지 못한다 - poqbatch는 카탈로그가 아는
            // 한정자가 아니라 HasKnownQualifier에서 후보 단계부터 걸러지기 때문이다.
            var markdown = Section("""
                INSERT INTO dbo.TSettleMst (Ymd) VALUES (@Ymd);
                EXEC poqbatch.usp_S04_DailyRateSnapshot @Ymd = @Ymd;
                """);

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Single(result.Errors);
            Assert.Contains(result.Errors, e => e.Contains("poqbatch", StringComparison.Ordinal));
            // 본문 결함이므로 재생성 피드백에 실려야 한다 - 목차를 고칠 일이 아니다.
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_ShouldStillRejectANonCanonicalBatchSchemaWhenTheCatalogIsEmpty()
        {
            // 미지 테이블 검사는 카탈로그가 없으면 소프트 스킵한다 - 대조할 목록이
            // 없으니 당연하다. 스키마 이름 규칙은 다르다. batch·batch_shadow만 쓴다는
            // 것은 카탈로그와 무관한 이 도구의 규약이므로, 오프라인 스냅숏 경로에서도
            // 갈라진 스키마가 조용히 통과해서는 안 된다.
            var markdown = Section("""
                INSERT INTO dbo.TSettleMst (Ymd) VALUES (@Ymd);
                EXEC poqbatch.usp_S04_DailyRateSnapshot @Ymd = @Ymd;
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Array.Empty<string>(), NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("poqbatch", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectANewObjectTheTocDeclaresOutsideTheBatchSchema()
        {
            // 실측(POQSettleProc11): 계획서가 배치 제어 객체를 S02·S16·S17에서는
            // `dbo.BatchExecution`으로, 나머지 단계에서는 `batch.BatchExecution`으로
            // 썼다. 회차 0은 `batch.` 쪽만 만들므로 S02가 기록하는 체크포인트 테이블은
            // 아무도 만들지 않는다 - 재시작이 깨진다.
            //
            // 검사가 이것을 통과시킨 이유는 목차가 그렇게 선언했기 때문이다. known
            // 집합이 목차의 TargetTables를 무조건 받아들여, 규약을 어긴 선언이 오히려
            // 면죄부가 됐다. 카탈로그에도 없고 batch 계열도 아닌 선언은 신뢰하지 않는다.
            var step = new BatchStepPlan(
                Code: "S02",
                Name: "실행 잠금 및 저널 개시",
                LegacyProcedures: Array.Empty<string>(),
                TargetTables: new[] { "dbo.BatchExecution" },
                ErrorCodes: Array.Empty<string>(),
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var markdown = $"""
                ### S02 실행 잠금 및 저널 개시

                ```sql
                INSERT INTO dbo.BatchExecution (RunId, Ymd) VALUES (@RunId, @Ymd);
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("dbo.BatchExecution", StringComparison.Ordinal));
            // 본문을 batch 스키마로 옮기면 해결되므로 재생성 피드백에 실려야 한다.
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectAStepThatDropsAConditionColumnItsOriginFiltersOn()
        {
            // 실측(POQSettleProc13): S09가 `SettleTarget = 1`, `SettleState = 1`,
            // `HolidayPayFlag = 2` 등 다섯 컬럼으로 거르는 로직을 통째로 빠뜨렸는데
            // 배너는 무결점이었다. 대상 테이블도 오류코드도 다 맞았고, 아무도 "그
            // 컬럼으로 거르는가"를 묻지 않았기 때문이다. 대상 집합이 달라지면
            // 금액이 달라진다.
            var step = new BatchStepPlan(
                Code: "S09",
                Name: "수납 지급 예정일 산정",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_EXPECT_PROC" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: new[] { "-1" },
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var conditions = Body("UP_UTIL_SETTLE_EXPECT_PROC", "SettleTarget");

            var markdown = Section("UPDATE dbo.TSettleMst SET OutYMD = @Ymd; -- 실패 시 -1");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, conditions);

            Assert.Contains(result.Errors, e => e.Contains("SettleTarget", StringComparison.Ordinal));
            // 본문을 다시 쓰면 고칠 수 있으므로 재생성 피드백에 실려야 한다.
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptAConditionColumnWrittenWithADifferentOperator()
        {
            // 명세서는 `UseState IN (0)`, 계획서는 `UseState = 0`으로 쓴다. 값까지
            // 대조하면 실측에서 미검출의 27%가 이런 동등 표현이었고 전부 오탐이었다.
            var step = new BatchStepPlan(
                Code: "S09",
                Name: "수납 지급 예정일 산정",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_EXPECT_PROC" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(),
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var conditions = Body("UP_UTIL_SETTLE_EXPECT_PROC", "UseState");

            var markdown = Section("UPDATE dbo.TSettleMst SET OutYMD = @Ymd WHERE UseState = 0;");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, conditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("UseState", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotDemandConditionColumnsFromAStepWithNoLegacyOrigin()
        {
            // 잠금·저널 같은 신규 단계에는 물려받을 원본 조건이 없다. 여기에 대조를
            // 걸면 규칙이 없는 곳에서 결함이 생긴다.
            var step = new BatchStepPlan(
                Code: "S02",
                Name: "실행 잠금",
                LegacyProcedures: Array.Empty<string>(),
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(),
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var conditions = Body("UP_UTIL_SETTLE_EXPECT_PROC", "SettleTarget");

            var markdown = """
                ### S02 실행 잠금

                ```sql
                INSERT INTO dbo.TSettleMst (Ymd) VALUES (@Ymd);
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, conditions);

            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateBatchStep_ShouldExcuseAUdfConditionWhenTheStepCallsThatUdf()
        {
            // 실측(POQSettleProc13): S09가 UIF_SettleYMD를 7회 호출하는데도 그 안의
            // SettleTarget·SettleState를 누락으로 보고했다. 검출 15건 중 14건이 이
            // 오인이었다 - 계획서가 UDF를 그대로 부르면 그 안의 조건을 옮겨 적을
            // 이유가 없다.
            var step = new BatchStepPlan(
                Code: "S09",
                Name: "수납 지급 예정일 산정",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_EXPECT_PROC" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(),
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var conditions = Udf("UP_UTIL_SETTLE_EXPECT_PROC", "UIF_SettleYMD", "SettleTarget");

            var markdown = """
                ### S09 수납 지급 예정일 산정

                ```sql
                UPDATE dbo.TSettleMst
                SET OutYMD = (SELECT OutYMD FROM dbo.UIF_SettleYMD(@Ymd, @PeriodId));
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, conditions);

            // 카탈로그에 UDF를 넣지 않아 미지 테이블 검사가 따로 울지만, 이 테스트가
            // 보는 것은 조건 대조뿐이다. 실제 산출물에서는 UDF도 DDL 카탈로그에 있다.
            Assert.DoesNotContain(result.Errors, e => e.Contains("거르는 로직이 없습니다", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldDemandAUdfConditionWhenTheStepNeitherCallsItNorInlinesIt()
        {
            // 면제의 반대편. UDF를 부르지도 않고 그 판단 기준도 본문에 없으면, 로직을
            // 옮기겠다고 해 놓고 무엇으로 거르는지를 빠뜨린 것이다. 이 갈래가 없으면
            // UDF 조건은 검사에서 통째로 사라져 검출력이 0이 된다.
            var step = new BatchStepPlan(
                Code: "S09",
                Name: "수납 지급 예정일 산정",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_EXPECT_PROC" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(),
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var conditions = Udf("UP_UTIL_SETTLE_EXPECT_PROC", "UIF_SettleYMD", "SettleTarget");

            var markdown = """
                ### S09 수납 지급 예정일 산정

                지급 예정일 계산은 C#으로 이관한다.

                ```sql
                UPDATE dbo.TSettleMst SET OutYMD = @Calculated;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, conditions);

            Assert.Contains(result.Errors, e => e.Contains("SettleTarget", StringComparison.Ordinal));
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectAShortcutInsideTheStepBody()
        {
            // 실측(POQSettleProc14): 축약어 '위와 동일' 하나 때문에 L1이 두 번 연속
            // 실패했고, 그때마다 골격과 17개 단계가 통째로 재생성됐다. 3회 재시도
            // 예산 중 2회를 그 한 줄이 먹어 L2 채점은 한 번뿐이었고, 개선 기회 없이
            // 84점 불합격이 채택됐다 - Critic이 지적한 결함들이 고쳐질 자리가 없었다.
            //
            // 단계 단위로 잡으면 그 단계만 다시 만들면 되고, 지적도 기존 재생성
            // 피드백 경로를 그대로 탄다.
            var markdown = Section("""
                | 컬럼 | 매핑 |
                | :--- | :--- |
                | CLTOTAL | 위와 동일 |
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("위와 동일", StringComparison.Ordinal));
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_ShouldIgnoreAShortcutInsideAQuotedLine()
        {
            // 배너가 잔존 오류를 인용하면 그 메시지 자체가 금지 토큰을 담는다. 인용문을
            // 검사하면 배너 붙은 문서가 스스로를 오류로 만들어 어떤 재생성으로도
            // 통과할 수 없다 - 문서 레벨에서 겪은 자기 오염이 단계에서도 똑같이 난다.
            var markdown = """
                ### S17 완료 파티션 원자적 게시

                > 이전 시도는 축약어('위와 동일')로 반려되었습니다.

                ```sql
                UPDATE dbo.TSettleMst SET OutState = 9;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Empty(result.Errors);
        }

        [Theory]
        [InlineData("SELECT CLEtc. FROM dbo.TSettleMst;", false)]   // 낱말의 일부는 축약어가 아니다
        [InlineData("나머지 컬럼은 etc. 로 줄인다", true)]
        public void ValidateBatchStep_ShouldApplyTheSameEtcRuleAsTheDocumentCheck(string body, bool expectError)
        {
            // 축약어 정의를 두 검사가 나눠 가지면 목록이 갈라진다. 문서 검사가 이미
            // 가진 예외(CLEtc.는 축약어가 아니라 사실이다)가 단계에서도 그대로여야 한다.
            var markdown = Section(body);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Equal(expectError, result.Errors.Any(e => e.Contains("etc.", StringComparison.Ordinal)));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotTakeADatabaseAndSchemaPrefixAsAnObject()
        {
            // 실측(POQSettleProc15): S11이 `SETTLE_POQ_DB.dbo`를 미지 객체로 두 번 잡혀
            // 재생성을 두 번 태웠다. 2부로 매칭되면 맨이름이 `dbo`가 되는데, `dbo`는
            // 테이블이 아니라 이 검사가 이미 한정자로 알고 있는 이름이다.
            // 실제 카탈로그처럼 3부 이름을 담아야 SETTLE_POQ_DB가 한정자로 등록된다.
            var catalog = new[] { "SETTLE_POQ_DB.dbo.TSettleMst", "dbo.TStatPGCollect" };
            var markdown = Section("""
                -- 대상은 SETTLE_POQ_DB.dbo 스키마에 있다.
                UPDATE SETTLE_POQ_DB.dbo.TSettleMst SET OutState = 9;
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("SETTLE_POQ_DB.dbo", StringComparison.Ordinal));
        }

        /// <summary>반올림 모양만 담은 재료.</summary>
        private static IReadOnlyDictionary<string, SpecConditions> Rounding(string procedure, params string[] shapes) =>
            new Dictionary<string, SpecConditions>(StringComparer.OrdinalIgnoreCase)
            {
                [procedure] = new SpecConditions(
                    Array.Empty<string>(),
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                    shapes)
            };

        [Fact]
        public void ValidateBatchStep_ShouldRejectAStepMissingTheOriginalRoundingShape()
        {
            // 정산 금액은 반올림 순서에 따라 달라진다. 원본이 합계를 먼저 반올림하고
            // 다시 반올림하는데 계획서가 한 번만 하면 결과가 어긋나는데, 그것을 보는
            // 검사가 어디에도 없었다 - 대상 테이블·오류코드·조건 컬럼이 다 맞아도
            // 이 축은 비어 있었다.
            var step = new BatchStepPlan(
                Code: "S05",
                Name: "기본 정산 원장 생성",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_INS" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(),
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var conditions = Rounding(
                "UP_UTIL_SETTLE_INS", "round(round(?,0,commsumroundflag)/1.1,0,commroundflag)");

            var markdown = """
                ### S05 기본 정산 원장 생성

                ```sql
                INSERT INTO dbo.TSettleMst (Amt)
                SELECT ROUND(X.RawPgComm4Sum, 0, P.CommRoundFlag) FROM X;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, conditions);

            Assert.Contains(result.Errors, e => e.Contains("commsumroundflag", StringComparison.Ordinal));
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptTheSameShapeWrittenWithDifferentColumnNames()
        {
            // 이 검사의 핵심. 계획서는 원본의 X.PGCOMM4SUM을 X.RawPgComm4Sum으로 바꿔
            // 부르는데, 이름까지 대조하면 정상 이행이 전부 걸린다.
            var step = new BatchStepPlan(
                Code: "S05",
                Name: "기본 정산 원장 생성",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_INS" },
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(),
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var conditions = Rounding(
                "UP_UTIL_SETTLE_INS", "round(round(?,0,commsumroundflag)/1.1,0,commroundflag)");

            var markdown = """
                ### S05 기본 정산 원장 생성

                ```sql
                INSERT INTO dbo.TSettleMst (Amt)
                SELECT ROUND(ROUND(S.RawPGComm4Sum, 0, P.CommSumRoundFlag) / 1.1, 0, P.CommRoundFlag) FROM S;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, conditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("반올림", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotDemandRoundingFromAStepWithNoLegacyOrigin()
        {
            var step = new BatchStepPlan(
                Code: "S02",
                Name: "실행 잠금",
                LegacyProcedures: Array.Empty<string>(),
                TargetTables: new[] { "dbo.TSettleMst" },
                ErrorCodes: Array.Empty<string>(),
                Chunkable: false,
                SchemaTables: Array.Empty<string>());

            var conditions = Rounding("UP_UTIL_SETTLE_INS", "round(round(?,0,commsumroundflag),0,commroundflag)");

            var markdown = """
                ### S02 실행 잠금

                ```sql
                INSERT INTO dbo.TSettleMst (Ymd) VALUES (@Ymd);
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(markdown, step, Catalog, conditions);

            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateBatchStep_ShouldStillTrustATocDeclarationThatExistsInTheCatalog()
        {
            // 회귀 가드: 목차 신뢰를 좁히는 것은 "카탈로그에도 batch에도 없는" 선언에
            // 한정한다. 카탈로그가 아는 테이블 선언까지 의심하면 정상 단계가 전부 걸린다.
            var markdown = Section("INSERT INTO dbo.TSettleMst (Ymd) VALUES (@Ymd);");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateBatchStep_RejectsAParameterThatTheOriginalDoesNotHave()
        {
            var markdown = Section("CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8), @pi_bypassPreCheck bit AS");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        [Fact]
        public void ValidateBatchStep_AcceptsExactlyTheOriginalParameters()
        {
            var markdown = Section("CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8), @po_intRetVal int OUTPUT AS");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT"));

            Assert.DoesNotContain(result.Errors, e => e.Contains("파라미터"));
        }

        // 지역 변수는 파라미터가 아니다. DECLARE된 이름을 결함으로 들면
        // 모든 단계가 상시 실패한다.
        [Fact]
        public void ValidateBatchStep_IgnoresDeclaredLocalVariables()
        {
            var markdown = Section(@"CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8) AS
DECLARE @v_currentStepId INT = 0;
SET @v_currentStepId = -101;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
        }

        // 최종 리뷰 B-2(오탐): 산문이 원본 SP를 `CREATE PROCEDURE dbo.UP_X`로 언급하면
        // (자기 AS가 없다) 그 뒤 SQL의 테이블 별칭 `AS t`가 정규식의 게으른 매치를
        // 끝맺는 첫 AS가 되어, 산문과 별칭 사이의 DECLARE된 지역 변수까지 "원본에
        // 없는 입력 파라미터"로 잘못 보고됐다(실행 재현). 규칙 6-1이 필수로 요구하는
        // 상태 변수가 이 함정에 걸리면 재생성 프롬프트가 모델에게 존재하지 않는
        // 파라미터를 지우라고 지시하는 오탐이 된다.
        [Fact]
        public void ValidateBatchStep_DoesNotTreatALocalVariableAsInventedWhenAProseMentionPrecedesATableAliasAs()
        {
            var markdown = """
                ### S17 완료 파티션 원자적 게시

                이 단계는 원본 `CREATE PROCEDURE dbo.UP_X`의 사전 검증 로직을 그대로 옮긴다.

                ```sql
                DECLARE @v_currentStepId INT = 0;
                SELECT * FROM dbo.TSettleMst AS t WHERE t.Ymd = @pi_strYMD;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
        }

        // 최종 리뷰 B-2(미탐, 함께 고칠 것): `CREATE OR ALTER PROCEDURE`는 SQL Server
        // 2016 SP1 이후 표준 관용인데, 옛 정규식이 `CREATE\s+PROC(?:EDURE)?`만 찾아
        // 이 형태를 아예 매치하지 못했다 - 인터페이스 검사가 통째로 꺼진 채 발명
        // 파라미터가 통과했다.
        [Fact]
        public void ValidateBatchStep_RejectsAnInventedParameterInACreateOrAlterProcedure()
        {
            var markdown = Section(
                "CREATE OR ALTER PROCEDURE batch.usp_S17 @pi_strYMD varchar(8), @pi_bypassPreCheck bit AS");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 위와 같은 이유로 `ALTER PROCEDURE`(CREATE 없이)도 매치되어야 한다.
        [Fact]
        public void ValidateBatchStep_RejectsAnInventedParameterInAnAlterProcedure()
        {
            var markdown = Section(
                "ALTER PROCEDURE batch.usp_S17 @pi_strYMD varchar(8), @pi_bypassPreCheck bit AS");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 소프트 스킵: 재료가 없으면 검사하지 않는다. 신설 단계에는 원본이 없다.
        [Fact]
        public void ValidateBatchStep_SkipsTheInterfaceCheckWhenTheStepHasNoOrigin()
        {
            var markdown = Section("CREATE PROCEDURE batch.usp_S17 @pi_anything bit AS");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S99", "@pi_strYMD varchar(8)"));

            Assert.DoesNotContain(result.Errors, e => e.Contains("@pi_anything"));
        }

        // 최종 리뷰 실측: 산문의 CREATE PROCEDURE 언급이 게으른 .*?의 출발점이 되어
        // 진짜 선언의 AS를 지나 소비하고, 키워드 폐기 조건에 걸려 매치가 버려진다.
        // Regex.Matches는 소비한 구간 뒤부터 재개하므로 진짜 선언이 영영 검사되지 않는다.
        [Fact]
        public void ValidateBatchStep_StillChecksARealDeclarationAfterAProseMention()
        {
            var markdown = $$"""
                ### S17 완료 파티션 원자적 게시

                원본 `CREATE PROCEDURE dbo.UP_UTIL_SETTLE_INS`를 SELECT ... FROM 기준으로 옮긴다.

                ```sql
                CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8), @pi_bypassPreCheck bit AS
                SELECT 1 FROM dbo.TSettleMst AS t;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 파라미터 목록 안 주석에 FROM이 있어도 검사가 꺼지면 안 된다.
        [Fact]
        public void ValidateBatchStep_StillChecksWhenTheParamListHasACommentContainingAKeyword()
        {
            var markdown = Section(@"
CREATE PROCEDURE batch.usp_S17
    @pi_strYMD varchar(8), -- 원본 SELECT ... FROM 기준일
    @pi_bypassPreCheck bit
AS
SELECT 1;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 기본값 문자열 리터럴에 FROM이 있어도 검사가 꺼지면 안 된다.
        [Fact]
        public void ValidateBatchStep_StillChecksWhenADefaultLiteralContainsAKeyword()
        {
            var markdown = Section(@"
CREATE PROCEDURE batch.usp_S17 @pi_mode nvarchar(10) = 'FROM', @pi_bypassPreCheck bit AS
SELECT 1;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_mode nvarchar(10)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 파라미터 이름이 정확히 @From이어도 검사가 꺼지면 안 된다.
        [Fact]
        public void ValidateBatchStep_StillChecksWhenAParameterIsNamedExactlyFrom()
        {
            var markdown = Section(@"
CREATE PROCEDURE batch.usp_S17 @From varchar(8), @pi_bypassPreCheck bit AS
SELECT 1;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@From varchar(8)"));

            Assert.Contains(result.Errors, e => e.Contains("@pi_bypassPreCheck"));
        }

        // 본문의 테이블 별칭 AS가 파라미터 구간으로 새어 들어오면 안 된다.
        // 선언의 AS는 언제나 본문의 별칭 AS보다 앞이다.
        [Fact]
        public void ValidateBatchStep_DoesNotTreatABodyTableAliasAsThePartOfTheParamList()
        {
            var markdown = Section(@"
CREATE PROCEDURE batch.usp_S17 @pi_strYMD varchar(8) AS
DECLARE @v_currentStepId INT = 0;
SELECT 1 FROM dbo.TSettleMst AS t WHERE t.YMD = @pi_strYMD;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                Interfaces("S17", "@pi_strYMD varchar(8)"));

            Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("@pi_strYMD"));
        }

        [Fact]
        public void ValidateBatchStep_RejectsAColumnNameOutsideTheControlContract()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET ExecutionStatus = N'Completed' WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        [Fact]
        public void ValidateBatchStep_RejectsTheCompletedStatusValue()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET StepStatus = N'Completed' WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        [Fact]
        public void ValidateBatchStep_AcceptsTheCanonicalVocabulary()
        {
            var markdown = Section(@"
INSERT INTO batch.BatchStepJournal (RunId, StepCode, StepStatus, StartedAtUtc)
VALUES (@RunId, N'S17', N'Running', SYSUTCDATETIME());
UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded', CompletedAtUtc = SYSUTCDATETIME()
WHERE RunId = @RunId AND StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("제어 테이블"));
        }

        // B3: UPDATE만 있고 INSERT가 없으면 0행 갱신이다. @@ROWCOUNT 검사가
        // 있는 곳은 정상 실행에서도 상시 실패하고, 없는 곳은 조용히 지나간다.
        [Fact]
        public void ValidateBatchStep_RejectsUpdatingAJournalRowItNeverInserts()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded' WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("INSERT") && e.Contains("BatchStepJournal"));
        }

        // 읽기만 하는 단계는 대상이 아니다. 다른 단계의 저널을 조회하는 것은 정상이다.
        [Fact]
        public void ValidateBatchStep_DoesNotRequireAnInsertWhenItOnlyReadsTheTable()
        {
            var markdown = Section(
                "SELECT StepStatus FROM batch.BatchStepJournal WHERE RunId = @RunId AND StepCode = N'S16';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("INSERT"));
        }

        // 리뷰 재현 1: 제어 테이블과 업무 테이블을 같은 UPDATE 문에서 FROM/JOIN으로
        // 엮고, 업무 컬럼을 별칭 없이 WHERE에 쓰면 그 이름이 "쓰는 컬럼"으로 오인됐다.
        // 계약 위반은 제어 테이블에 값을 쓸 때만 성립한다 - WHERE는 읽기다.
        [Fact]
        public void ValidateBatchStep_DoesNotFlagAnUnaliasedBusinessColumnInAMixedUpdateWhereClause()
        {
            var markdown = Section("""
                UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded'
                FROM batch.BatchStepJournal bsj
                JOIN dbo.TSettleMst ON dbo.TSettleMst.RunId = bsj.RunId
                WHERE SourceRunId = @RunId AND bsj.StepCode = N'S17';
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("SourceRunId"));
        }

        // 리뷰 재현 2: 세미콜론이 없는 제어 테이블 UPDATE 뒤에 무관한 업무 SQL이 오면
        // `(?=;|$)` 경계가 문서 끝까지 흡수해 뒤쪽 업무 컬럼까지 후보로 섞였다.
        [Fact]
        public void ValidateBatchStep_DoesNotAbsorbUnrelatedSqlAfterAMissingSemicolon()
        {
            var markdown = Section("""
                UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded' WHERE StepCode = N'S17'

                SELECT SettleState, ExecutionStatus FROM dbo.TSettleMst WHERE RunId = @RunId;
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("SettleState"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        // 리뷰 재현 3: 업무 테이블 자신의 상태 컬럼 필터(별칭 있음, 제어 테이블과 무관)가
        // 같은 문 안에 있으면 그 리터럴이 "계약 밖 상태값"으로 오인됐다. 상태값 검사는
        // 제어 테이블의 StatusColumn에 실제로 대입되는 값만 봐야 한다.
        [Fact]
        public void ValidateBatchStep_DoesNotFlagABusinessStatusLiteralInTheSameStatement()
        {
            var markdown = Section("""
                UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded'
                FROM batch.BatchStepJournal bsj
                JOIN dbo.TSettleMst t ON t.RunId = bsj.RunId
                WHERE t.SettleStatus = N'Pending' AND bsj.StepCode = N'S17';
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("Pending"));
        }

        // 미탐 회귀 1(2라운드 리뷰 재현): INSERT VALUES의 문자열 리터럴 안에 쉼표가
        // 있으면(N'a,b error message') 위치 기반 분리가 그 쉼표를 항목 경계로
        // 오인해 뒤따르는 항목들의 색인이 밀린다 - StepStatus 위치에 엉뚱한 값이
        // 걸려 계약 밖 상태값 'Completed'를 놓쳤다. SplitTopLevelSegments가 인용
        // 상태를 추적해야 한다.
        [Fact]
        public void ValidateBatchStep_RejectsADisallowedStatusValueWhenAnEarlierInsertValueContainsAComma()
        {
            var markdown = Section(
                "INSERT INTO batch.BatchStepJournal (RunId, StepCode, ErrorMessage, StepStatus, StartedAtUtc) " +
                "VALUES (@RunId, N'S17', N'a,b error message', N'Completed', SYSUTCDATETIME());");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        // 미탐 회귀 2a(2라운드 리뷰 재현): SET 대입식 안에 FROM을 가진 서브쿼리가 있으면
        // 괄호를 무시하는 경계 탐색이 그 안의 FROM에서 SET 절 전체를 잘라, 서브쿼리
        // 뒤에 오는 계약 밖 컬럼 대입이 통째로 검사에서 사라졌다. 경계는 괄호 깊이
        // 0에서만 성립해야 한다.
        [Fact]
        public void ValidateBatchStep_RejectsAnOutOfContractColumnAfterAFromSubqueryInTheSetClause()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET StepStatus = " +
                "(SELECT TOP 1 Status FROM dbo.TSettleMst WHERE RunId = @RunId), " +
                "ExecutionStatus = N'Completed' WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        // 미탐 회귀 2b: 서브쿼리 대입 뒤에 오는 것이 계약 밖 컬럼이 아니라 계약 밖
        // 상태값일 때도 같은 절단 결함이 그 위반을 숨긴다.
        [Fact]
        public void ValidateBatchStep_RejectsADisallowedStatusValueAfterAFromSubqueryInTheSetClause()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET ErrorMessage = " +
                "(SELECT TOP 1 Msg FROM dbo.TSettleMst WHERE RunId = @RunId), " +
                "StepStatus = N'Completed' WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        // 미탐 회귀 3: 다중 대입에서 첫 항목이 아니라 두 번째·세 번째 대입 대상이
        // 계약 밖이어도 잡혀야 한다 - 위치가 아니라 이름으로 대조하므로 순서와
        // 무관해야 한다.
        [Fact]
        public void ValidateBatchStep_RejectsOutOfContractColumnsInTheSecondAndThirdAssignments()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded', FooBar = 1, BazQux = 2 " +
                "WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("FooBar"));
            Assert.Contains(result.Errors, e => e.Contains("BazQux"));
        }

        // 미탐 회귀 4: VALUES 목록에 괄호 중첩 함수(CONVERT(varchar(8), ..., 112))가
        // 섞여도 위치 대응이 깨지지 않아야 한다 - 그 함수 인자의 내부 쉼표가 항목
        // 경계로 오인되면 뒤 항목들의 색인이 밀린다.
        [Fact]
        public void ValidateBatchStep_KeepsPositionalAlignmentAcrossANestedParenFunctionInValues()
        {
            var markdown = Section(
                "INSERT INTO batch.BatchStepJournal (RunId, StepCode, StartedAtUtc, StepStatus) " +
                "VALUES (@RunId, N'S17', CONVERT(varchar(8), SYSUTCDATETIME(), 112), N'Completed');");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        // 미탐 회귀 5(3라운드 리뷰 재현): 주석 안의 아포스트로피(don't)가 문자열
        // 인용 시작으로 오인되면 그 뒤의 진짜 대입과 진짜 절 경계(WHERE/;)까지
        // 문자열 내용물로 삼켜진다 - -- 줄 주석 형태.
        [Fact]
        public void ValidateBatchStep_RejectsAnOutOfContractColumnAfterALineCommentContainingAnApostrophe()
        {
            var markdown = Section("""
                UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded' -- don't panic
                , FooBarBaz = 1
                WHERE StepCode = N'S17';
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("FooBarBaz"));
        }

        // 미탐 회귀 5(3라운드 리뷰 재현): 같은 결함의 /* */ 블록 주석 형태.
        [Fact]
        public void ValidateBatchStep_RejectsAnOutOfContractColumnAfterABlockCommentContainingAnApostrophe()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded' /* don't panic */" +
                ", FooBarBaz = 1 WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("FooBarBaz"));
        }

        // 주석 뒤에 오는 것이 계약 밖 컬럼이 아니라 계약 밖 상태값이어도 같은 결함이
        // 그 위반을 숨긴다 - -- 줄 주석 형태.
        [Fact]
        public void ValidateBatchStep_RejectsADisallowedStatusValueAfterALineCommentContainingAnApostrophe()
        {
            var markdown = Section("""
                UPDATE batch.BatchStepJournal SET ErrorMessage = N'ok' -- don't panic
                , StepStatus = N'Completed'
                WHERE StepCode = N'S17';
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        // 상태값 버전의 /* */ 블록 주석 형태.
        [Fact]
        public void ValidateBatchStep_RejectsADisallowedStatusValueAfterABlockCommentContainingAnApostrophe()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET ErrorMessage = N'ok' /* don't panic */" +
                ", StepStatus = N'Completed' WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        // 문자열 리터럴 안의 --는 주석이 아니다 - 순서를 뒤집으면(주석 검사를 인용
        // 상태보다 먼저 하면) 이 값 자체가 삼켜져 뒤따르는 진짜 위반을 놓친다.
        [Fact]
        public void ValidateBatchStep_DoesNotTreatALineCommentMarkerInsideAStringLiteralAsAComment()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET ErrorMessage = N'a--b', FooBarBaz = 1 " +
                "WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("FooBarBaz"));
        }

        // 3순위(대괄호 정규화): [ExecutionStatus] 같은 대괄호 인용 대입 대상은
        // ^[A-Za-z_]\w*$가 걸러내 검사를 통째로 우회했다 - 대괄호를 벗기고 대조해야
        // 한다.
        [Fact]
        public void ValidateBatchStep_RejectsABracketQuotedOutOfContractColumnInAnUpdateSetClause()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET [ExecutionStatus] = N'Completed' WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        // INSERT 컬럼 목록에서도 대괄호를 벗겨야 계약 밖 상태값이 올바른 위치에서
        // 대조된다.
        [Fact]
        public void ValidateBatchStep_RejectsADisallowedStatusValueWithBracketQuotedInsertColumns()
        {
            var markdown = Section(
                "INSERT INTO batch.BatchStepJournal ([RunId], [StepCode], [StepStatus], [StartedAtUtc]) " +
                "VALUES (@RunId, N'S17', N'Completed', SYSUTCDATETIME());");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        // 병적인 대괄호 이름([Order,Column])의 내부 쉼표가 항목 경계로 오인되면
        // 뒤따르는 진짜 위반의 위치가 밀린다.
        [Fact]
        public void ValidateBatchStep_DoesNotSplitOnACommaInsideABracketQuotedName()
        {
            var markdown = Section(
                "UPDATE batch.BatchStepJournal SET [Order,Column] = 1, FooBarBaz = 2 WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("FooBarBaz"));
        }

        // === B6·B7: 그림자 계약·반환 경로 (Task 7) ===================================

        // 감사 🔴(S04): BEGIN TRAN 안에서 만든 SELECT INTO 그림자는 롤백과 함께
        // 사라진다. CATCH의 DELETE는 자동 커밋이라 이미 복원된 행을 다시 지우고
        // 복원 INSERT는 객체 없음 오류로 실패한다.
        [Fact]
        public void ValidateBatchStep_RejectsAShadowCreatedInsideTheTransaction()
        {
            var markdown = Section(@"
BEGIN TRAN;
SELECT * INTO batch_shadow.TClientSettleRate_RunId_S04 FROM dbo.TClientSettleRate WHERE YMD = @pi_strYMD;
DELETE FROM dbo.TClientSettleRate WHERE YMD = @pi_strYMD;
COMMIT TRAN;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("BEGIN TRAN") && e.Contains("그림자"));
        }

        [Fact]
        public void ValidateBatchStep_AcceptsAShadowCreatedBeforeTheTransaction()
        {
            var markdown = Section(@"
SELECT * INTO batch_shadow.TClientSettleRate_RunId_S04 FROM dbo.TClientSettleRate WHERE YMD = @pi_strYMD;
BEGIN TRAN;
DELETE FROM dbo.TClientSettleRate WHERE YMD = @pi_strYMD;
COMMIT TRAN;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("그림자"));
        }

        // 위험: 문서에 트랜잭션이 둘 이상이면 첫 BEGIN TRAN/COMMIT TRAN 쌍만 보는
        // 순진한 구현은 두 번째 블록 안의 위반을 놓친다(미탐).
        [Fact]
        public void ValidateBatchStep_RejectsAShadowCreatedInsideASecondTransactionBlock()
        {
            var markdown = Section(@"
BEGIN TRAN;
DELETE FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
COMMIT TRAN;
BEGIN TRAN;
SELECT * INTO batch_shadow.TSettleMst_RunId_S17 FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
DELETE FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
COMMIT TRAN;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("그림자"));
        }

        // 위험: COMMIT TRAN이 아니라 ROLLBACK TRAN으로 끝나는 구간도 트랜잭션
        // 안이다 - "첫 COMMIT TRAN까지만" 찾는 구현은 이 구간을 놓친다.
        [Fact]
        public void ValidateBatchStep_RejectsAShadowCreatedInsideATransactionThatEndsWithRollback()
        {
            var markdown = Section(@"
BEGIN TRAN;
SELECT * INTO batch_shadow.TSettleMst_RunId_S17 FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
DELETE FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
ROLLBACK TRAN;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("그림자"));
        }

        // 위험: 중첩된 BEGIN TRAN. 그림자가 안쪽 트랜잭션이 열려 있는 동안 만들어져도
        // 바깥 트랜잭션이 여전히 열려 있으므로 위반이다. 그림자 구문을 안쪽 COMMIT
        // *뒤*·바깥 COMMIT *앞*에 둔다 - 첫 BEGIN/첫 COMMIT 쌍만 보는 얕은 구현도
        // (그림자가 첫 COMMIT 앞에 있으면) 우연히 잡아내므로, 그 우연을 배제하려면
        // 그림자가 첫(=안쪽) COMMIT을 지난 뒤에 나와야 한다 - 리뷰에서 실측된
        // 미탐 재현과 같은 모양이다.
        [Fact]
        public void ValidateBatchStep_RejectsAShadowCreatedInsideANestedTransaction()
        {
            var markdown = Section(@"
BEGIN TRAN;
BEGIN TRAN;
DELETE FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
COMMIT TRAN;
SELECT * INTO batch_shadow.TSettleMst_RunId_S17 FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
COMMIT TRAN;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("그림자"));
        }

        // 리뷰 재현(미탐): 안쪽 트랜잭션이 COMMIT TRAN으로 닫혀도 바깥 트랜잭션은
        // 아직 열려 있다. "첫 종료문을 그 트랜잭션의 종료로 소비"하는 얕은 구현은
        // 안쪽 COMMIT을 바깥 트랜잭션의 종료로 오인해, 그 뒤의 그림자를 트랜잭션
        // 밖으로 잘못 분류한다 - 트랜잭션 깊이를 세지 않으면 재현되는 미탐이다.
        [Fact]
        public void ValidateBatchStep_RejectsAShadowCreatedBetweenAnInnerCommitAndTheStillOpenOuterCommit()
        {
            var markdown = Section(@"
BEGIN TRAN;
BEGIN TRAN;
COMMIT TRAN;
SELECT * INTO batch_shadow.TSettleMst_RunId_S17 FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
COMMIT TRAN;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("그림자"));
        }

        // 위험(오탐 방지): 주석 안의 "BEGIN TRAN" 문자열은 실제 트랜잭션을 열지
        // 않는다 - 이 텍스트를 진짜 BEGIN TRAN으로 오인하면, 트랜잭션이 전혀 없는
        // 단계에서 정상 SELECT INTO가 그림자 위반으로 오탐된다.
        [Fact]
        public void ValidateBatchStep_DoesNotTreatACommentedBeginTranAsOpeningATransaction()
        {
            var markdown = Section(@"
-- BEGIN TRAN은 이 단계에서 쓰지 않는다
SELECT * INTO batch_shadow.TSettleMst_RunId_S17 FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
DELETE FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("그림자"));
        }

        // 감사 🟠(S12): WHERE 없는 전량 삭제 후 전체 스냅샷 재삽입은 당일 외
        // 거래일 행까지 실행 시작 시점으로 되돌린다.
        [Fact]
        public void ValidateBatchStep_RejectsARestoreThatDeletesWithoutARange()
        {
            var markdown = Section(@"
BEGIN CATCH
    DELETE FROM dbo.TSettleByTX;
    INSERT INTO dbo.TSettleByTX SELECT * FROM batch_shadow.TSettleByTX_RunId_S12;
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("WHERE"));
        }

        // 위험(오탐 방지): DELETE가 WHERE로 범위를 좁혔다면 정상 복원이다 - 이
        // 규칙이 걸어서는 안 된다.
        [Fact]
        public void ValidateBatchStep_AcceptsARestoreThatDeletesWithARange()
        {
            var markdown = Section(@"
BEGIN CATCH
    DELETE FROM dbo.TSettleByTX WHERE YMD = @pi_strYMD;
    INSERT INTO dbo.TSettleByTX SELECT * FROM batch_shadow.TSettleByTX_RunId_S12 WHERE YMD = @pi_strYMD;
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("전량 삭제"));
        }

        // 리뷰 재현(오탐): 이 규칙이 말하려는 것은 "그림자에서 복원할 때 원래 지운
        // 범위와 같은 범위만 지워야 한다"이지, WHERE 없는 전량 삭제 자체가 아니다.
        // 그림자와 무관한 일반 ETL 전량 갱신(INSERT ... VALUES)까지 잡으면, 이
        // 규칙과 아무 관계가 없는 정상 배치 SQL이 걸린다.
        [Fact]
        public void ValidateBatchStep_AcceptsAWhereLessDeleteFollowedByANonShadowInsert()
        {
            var markdown = Section(@"
BEGIN CATCH
    DELETE FROM dbo.TSettleByTX;
    INSERT INTO dbo.TSettleByTX (Col1) VALUES (@v1);
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("전량 삭제"));
        }

        // 재리뷰 재현(미탐): `ShadowSourcePattern`이 `batch_shadow` 바로 뒤에 리터럴
        // `.`을 요구해, SQL Server에서 흔한 대괄호 인용 스키마(`[batch_shadow].[X]`)를
        // 빗나갔다 - 대괄호 인용은 이 코드베이스가 이미 별도로 다뤄온 스타일이라 정상적인
        // AI 생성 배치 SQL에서 충분히 나올 수 있다.
        [Fact]
        public void ValidateBatchStep_RejectsARestoreThatDeletesWithoutARangeUsingABracketQuotedShadowSource()
        {
            var markdown = Section(@"
BEGIN CATCH
    DELETE FROM dbo.TSettleByTX;
    INSERT INTO dbo.TSettleByTX SELECT * FROM [batch_shadow].[TSettleByTX_RunId_S12];
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("전량 삭제"));
        }

        // 위험(오탐 방지): `batch_shadow`를 대괄호까지 허용하도록 완화하더라도, 업무
        // 테이블 이름이 우연히 `batch_shadow`로 시작한다는 이유만으로(`batch_shadow_archive`
        // 같은) 걸리면 안 된다 - 패턴은 `batch_shadow` 바로 뒤에 점을 요구하므로 이런
        // 이름에는 애초에 걸리지 않아야 한다.
        [Fact]
        public void ValidateBatchStep_AcceptsAWhereLessDeleteFollowedByAnInsertFromATableThatMerelyStartsWithBatchShadow()
        {
            var markdown = Section(@"
BEGIN CATCH
    DELETE FROM dbo.TSettleByTX;
    INSERT INTO dbo.TSettleByTX SELECT * FROM dbo.batch_shadow_archive;
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("전량 삭제"));
        }

        // 최종 리뷰 B-1(오탐, 실행 확인): 프롬프트 Few-Shot "Shadow Table Swap
        // Pattern"의 정방향 스왑(`BEGIN TRAN; DELETE FROM T; INSERT INTO T SELECT *
        // FROM batch_shadow...; COMMIT TRAN;`)이 (b)의 "WHERE 없는 전량 삭제 복원"
        // 모양과 구조가 같아 걸렸다. (b)가 겨냥하는 것은 CATCH의 *복원*(롤백 뒤
        // 자동 커밋 구간)이지, BEGIN TRAN 안에서 끝나는 정방향 교체가 아니다 - 이
        // 스왑은 실패하면 트랜잭션 전체가 롤백되어 DELETE 자체가 무효가 되므로
        // "다른 거래일 행이 실행 시작 시점으로 되돌아가는" (b)의 위험이 없다.
        [Fact]
        public void ValidateBatchStep_AcceptsAShadowSwapDeleteAndInsertInsideAnOpenTransaction()
        {
            var markdown = Section(@"
DECLARE @v_shadowCaptured BIT = 0;
SELECT * INTO batch_shadow.TargetTable_RunId_S13 FROM dbo.TargetTable WHERE 1=0;
INSERT INTO batch_shadow.TargetTable_RunId_S13 (Col1, Col2)
SELECT Col1, SUM(Col2) FROM SourceTable GROUP BY Col1;
SET @v_shadowCaptured = 1;
BEGIN TRAN;
  DELETE FROM dbo.TargetTable;
  INSERT INTO dbo.TargetTable SELECT * FROM batch_shadow.TargetTable_RunId_S13;
COMMIT TRAN;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TargetTable"), Array.Empty<string>(), NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("전량 삭제"));
        }

        // 위험(미탐 방지): (b)를 "BEGIN TRAN 안이면 제외"로 좁히더라도, 트랜잭션 밖
        // (CATCH에서 롤백 뒤 자동 커밋 구간)의 WHERE 없는 전량 삭제 복원은 여전히
        // 잡혀야 한다 - 감사 S12 원본 위반과 같은 모양이다.
        [Fact]
        public void ValidateBatchStep_StillRejectsAWhereLessRestoreOutsideAnyTransaction()
        {
            var markdown = Section(@"
BEGIN TRAN;
  DELETE FROM dbo.TSettleMst WHERE YMD = @pi_strYMD;
  INSERT INTO dbo.TSettleMst SELECT * FROM batch_shadow.TSettleMst_RunId_S12 WHERE YMD = @pi_strYMD;
COMMIT TRAN;
BEGIN CATCH
    DELETE FROM dbo.TSettleByTX;
    INSERT INTO dbo.TSettleByTX SELECT * FROM batch_shadow.TSettleByTX_RunId_S12;
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("전량 삭제"));
        }

        // 최종 리뷰 실측: 보상 복원을 자기 트랜잭션으로 감싸면 (b)가 통째로 제외한다.
        // 래퍼는 다른 거래일의 행을 되돌려주지 않고 피해를 원자적으로 커밋할 뿐이다.
        [Fact]
        public void ValidateBatchStep_RejectsAWhereLessRestoreWrappedInItsOwnTransactionInsideCatch()
        {
            var markdown = Section(@"
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    BEGIN TRAN;
        DELETE FROM dbo.TSettleByTX;
        INSERT INTO dbo.TSettleByTX SELECT * FROM batch_shadow.TSettleByTX_RunId_S12;
    COMMIT TRAN;
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("전량 삭제"));
        }

        // 정방향 스왑은 CATCH 밖이므로 계속 제외되어야 한다.
        // 프롬프트의 Few-Shot이 가르치는 형태다 - 잡으면 지배 계약 위반이다.
        [Fact]
        public void ValidateBatchStep_StillAcceptsTheForwardSwapOutsideCatch()
        {
            var markdown = Section(@"
BEGIN TRAN;
    DELETE FROM dbo.TargetTable;
    INSERT INTO dbo.TargetTable SELECT * FROM batch_shadow.TargetTable_RunId_S13;
COMMIT TRAN;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("전량 삭제"));
        }

        // 감사 🟠(S11): EXEC() 동적 배치는 바깥 배치의 변수를 볼 수 없다.
        [Fact]
        public void ValidateBatchStep_RejectsAnOuterVariableInsideExec()
        {
            var markdown = Section(
                "EXEC(N'INSERT INTO ' + @v_shadowTableName + N' SELECT A.* FROM dbo.T A WHERE A.ProcYMD = @pi_strYMD');");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("sp_executesql"));
        }

        // 위험(오탐 방지): sp_executesql 자체를 직접 부르는 정상 호출은 EXEC() 동적
        // 배치가 아니다 - "EXEC" 뒤에 괄호가 없으므로 걸리면 안 된다.
        [Fact]
        public void ValidateBatchStep_AcceptsADirectSpExecutesqlCall()
        {
            var markdown = Section(
                "EXEC sp_executesql @sql, N'@p int', @p = @pi_intValue;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("동적 배치"));
        }

        // 위험(오탐 방지): 괄호 없는 프로시저 호출(`EXEC dbo.usp_Foo @a, @b`)도
        // EXEC() 동적 배치가 아니다.
        [Fact]
        public void ValidateBatchStep_AcceptsAProcedureCallWithoutParens()
        {
            var markdown = Section(
                "EXEC dbo.usp_Foo @pi_strYMD, @pi_intValue;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("동적 배치"));
        }

        // B7: CATCH가 THROW로 끝나면 호출부의 OUTPUT 대입을 지나쳐 원본 반환 코드가 사라진다.
        [Fact]
        public void ValidateBatchStep_RejectsACatchThatOnlyRethrows()
        {
            var markdown = Section(@"
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    THROW;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("THROW"));
        }

        [Fact]
        public void ValidateBatchStep_AcceptsACatchThatSetsTheOutputAndReturns()
        {
            var markdown = Section(@"
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("THROW"));
        }

        // 위험(미탐 방지): RETURN이 주석 안에만 있으면 반환 경로가 없는 것과 같다 -
        // 주석의 RETURN 문자열을 실제 코드로 오인하면 THROW-only 위반을 놓친다.
        [Fact]
        public void ValidateBatchStep_StillFlagsAThrowOnlyCatchWhenReturnAppearsOnlyInAComment()
        {
            var markdown = Section(@"
BEGIN CATCH
    -- RETURN @v_currentStepId; (참고용 예시, 실제로 쓰지 않는다)
    THROW;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("THROW"));
        }

        // 위험(오탐 방지): THROW가 주석 안에만 있으면 실제로 다시 던지지 않는다 -
        // 주석의 THROW 문자열을 실제 코드로 오인하면 정상 CATCH가 오탐된다.
        [Fact]
        public void ValidateBatchStep_DoesNotFlagACatchWhoseThrowMentionIsOnlyInAComment()
        {
            var markdown = Section(@"
BEGIN CATCH
    -- THROW를 쓰면 반환 코드가 사라지므로 여기서는 쓰지 않는다
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("THROW"));
        }

        // 위험: 여러 CATCH 블록이 있으면 각각 독립적으로 판정되어야 한다 - 하나가
        // 정상이라고 나머지 위반이 가려지거나, 하나가 위반이라고 정상까지 걸리면
        // 안 된다.
        [Fact]
        public void ValidateBatchStep_EvaluatesEachCatchBlockIndependently()
        {
            var markdown = Section(@"
BEGIN CATCH
    THROW;
END CATCH

BEGIN CATCH
    SET @po_intRetVal = @v_currentStepId;
    RETURN @v_currentStepId;
END CATCH");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Equal(1, result.Errors.Count(e => e.Contains("THROW")));
        }

        // 최종 리뷰가 못박은 지배 계약: "재료 하나가 사실을 내고 프롬프트와 L1이
        // 같은 사실을 소비한다." 프롬프트의 Few-Shot 모범 예시 네 개가 L1을 통과
        // 못하면 정상 산출물이 재시도 예산을 태우고 QualityFloor 배너를 단다 -
        // 재생성으로 고칠 수 없는 결함이다. `ConsolidatedPlanRules`에서 ```sql
        // 블록을 직접 뽑아 각각 실제 ValidateBatchStep에 넣어 확인한다 - 지금까지
        // 이 계약을 지키는 테스트가 없었다.
        [Fact]
        public void FewShotExamples_InConsolidatedPlanRules_AllValidateWithoutErrors()
        {
            var field = typeof(AiService).GetField(
                "ConsolidatedPlanRules",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(field);

            var rules = (string)field!.GetValue(null)!;

            var blocks = System.Text.RegularExpressions.Regex
                .Matches(rules, @"```sql\r?\n(?<body>.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(m => m.Groups["body"].Value)
                .ToList();

            // 블록 개수 자체를 못박는다 - Few-Shot이 늘거나 줄면 아래 인덱스별
            // TargetTables 매핑도 같이 검토해야 한다는 신호다.
            Assert.Equal(4, blocks.Count);

            var targetTablesByBlock = new[]
            {
                new[] { "dbo.TargetTable" }, // 0: Shadow Table Swap Pattern
                new[] { "TargetTable" },     // 1: Chunking Pattern
                new[] { "dbo.TargetTable" }, // 2: Shadow Table Restore in CATCH block
                new[] { "TargetTable" },     // 3: INSERT-only Compensation
            };

            for (var i = 0; i < blocks.Count; i++)
            {
                var markdown = Section(blocks[i]);
                var step = new BatchStepPlan(
                    Code: "S17",
                    Name: "완료 파티션 원자적 게시",
                    LegacyProcedures: Array.Empty<string>(),
                    TargetTables: targetTablesByBlock[i],
                    ErrorCodes: Array.Empty<string>(),
                    Chunkable: false,
                    SchemaTables: Array.Empty<string>());

                var result = new MechanicalValidator().ValidateBatchStep(
                    markdown, step, Array.Empty<string>(), NoConditions);

                Assert.True(
                    result.Errors.Count == 0,
                    $"Few-Shot 블록 {i}이 L1을 통과하지 못했습니다: {string.Join(" | ", result.Errors)}\n---\n{blocks[i]}");
            }
        }

        // 최종 리뷰 실측: UPDATE <별칭> SET ... FROM <제어테이블> <별칭> 형태를
        // 어휘 검사가 아예 인식하지 못해 B2 9건이 초록 게이트 아래 남는다.
        // docs/architecture.md:433-434가 이 형태를 표준 관용으로 명시한다.
        [Fact]
        public void ValidateBatchStep_RejectsAnOutOfContractColumnInAnAliasedUpdate()
        {
            var markdown = Section(@"
UPDATE bsj SET bsj.ExecutionStatus = N'Succeeded'
FROM batch.BatchStepJournal bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        [Fact]
        public void ValidateBatchStep_RejectsADisallowedStatusValueInAnAliasedUpdate()
        {
            var markdown = Section(@"
UPDATE bsj SET bsj.StepStatus = N'Completed'
FROM batch.BatchStepJournal bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        // 별칭 형태로 UPDATE만 하고 INSERT가 없으면 B3도 우회된다.
        [Fact]
        public void ValidateBatchStep_RejectsAnAliasedUpdateOfAJournalRowItNeverInserts()
        {
            var markdown = Section(@"
UPDATE bsj SET bsj.StepStatus = N'Succeeded'
FROM batch.BatchStepJournal bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("INSERT") && e.Contains("BatchStepJournal"));
        }

        // 별칭 형태의 정상 어휘는 잡히면 안 된다. 넓히면서 오탐을 들이지 않았는지 잠근다.
        [Fact]
        public void ValidateBatchStep_AcceptsTheCanonicalVocabularyInAnAliasedUpdate()
        {
            var markdown = Section(@"
INSERT INTO batch.BatchStepJournal (RunId, StepCode, StepStatus, StartedAtUtc)
VALUES (@RunId, N'S17', N'Running', SYSUTCDATETIME());
UPDATE bsj SET bsj.StepStatus = N'Succeeded', bsj.CompletedAtUtc = SYSUTCDATETIME()
FROM batch.BatchStepJournal bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("제어 테이블"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("UPDATE만"));
        }

        // 별칭이 제어 테이블에 묶이지 않았으면 그 UPDATE는 대상이 아니다.
        // 업무 테이블을 별칭으로 갱신하는 것은 정상이다.
        [Fact]
        public void ValidateBatchStep_IgnoresAnAliasedUpdateBoundToABusinessTable()
        {
            var markdown = Section(@"
UPDATE m SET m.SettleState = 9, m.ExecutionStatus = N'Completed'
FROM dbo.TSettleMst m
WHERE m.YMD = @pi_strYMD;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("제어 테이블"));
        }

        // 한정자가 제어 테이블 이름 자체인 형태도 벗겨야 한다.
        [Fact]
        public void ValidateBatchStep_StripsAQualifierThatIsTheControlTableNameItself()
        {
            var markdown = Section(@"
UPDATE batch.BatchStepJournal SET batch.BatchStepJournal.ExecutionStatus = N'Succeeded'
WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        // 리뷰 1라운드 실측: 대괄호로 인용한 제어 테이블명(FROM [batch].[BatchStepJournal]
        // bsj 등)이 별칭 정규식에 전혀 잡히지 않아 이 태스크가 닫으려던 구멍이
        // 표기 형태 하나로 다시 열렸다. 네 혼합 형태(양쪽 대괄호/한쪽만 대괄호)를
        // 전부 잠근다.
        [Theory]
        [InlineData("[batch].[BatchStepJournal]")]
        [InlineData("[dbo].[BatchStepJournal]")]
        [InlineData("batch.[BatchStepJournal]")]
        [InlineData("[batch].BatchStepJournal")]
        public void ValidateBatchStep_RejectsAnOutOfContractColumnInABracketQuotedAliasedUpdate(
            string qualifiedTable)
        {
            var markdown = Section($@"
UPDATE bsj SET bsj.ExecutionStatus = N'Succeeded'
FROM {qualifiedTable} bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        // 같은 형태로 UPDATE만 하고 INSERT가 없으면 행 출처 검사도 잡아야 한다.
        [Fact]
        public void ValidateBatchStep_RejectsABracketQuotedAliasedUpdateOfAJournalRowItNeverInserts()
        {
            var markdown = Section(@"
UPDATE bsj SET bsj.StepStatus = N'Succeeded'
FROM [batch].[BatchStepJournal] bsj
WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("INSERT") && e.Contains("BatchStepJournal"));
        }

        // 이름이 접두사로만 겹치는 다른 테이블(BatchStepJournalArchive)의 대괄호 인용
        // 별칭은 진짜 제어 테이블(BatchStepJournal)에 묶이면 안 된다 - 넓히면서
        // 오탐을 들이지 않았는지 잠근다.
        [Fact]
        public void ValidateBatchStep_IgnoresAPrefixOverlappingBracketQuotedTable()
        {
            var markdown = Section(@"
UPDATE arc SET arc.SomeArchiveColumn = 1
FROM [batch].[BatchStepJournalArchive] arc
WHERE arc.RunId = @RunId;

INSERT INTO batch.BatchStepJournal (RunId, StepCode, StepStatus, StartedAtUtc)
VALUES (@RunId, N'S17', N'Running', SYSUTCDATETIME());");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("SomeArchiveColumn"));
        }

        // === 최종 전체 브랜치 리뷰 후속: B-1·B-2·Important 1·Important 3 =============

        // BLOCKING B-1 회귀 방어: 대괄호 인용 INSERT + 대괄호/맨이름 혼합 별칭 UPDATE
        // 네 조합 모두 오류 없이 통과해야 한다(리뷰 실행 재현 재구성). 수정 전에는
        // CheckBatchControlRowOrigin의 INSERT 쪽 판정이 대괄호를 몰라 이 정상
        // 문서를 "UPDATE만 하고 자기 행을 만드는 지점이 없다"고 반려했다.
        [Theory]
        [InlineData(
            "INSERT INTO [batch].[BatchStepJournal] (RunId, StepCode, StepStatus, StartedAtUtc) " +
            "VALUES (@RunId, N'S17', N'Running', SYSUTCDATETIME());",
            "UPDATE bsj SET bsj.StepStatus = N'Succeeded' FROM [batch].[BatchStepJournal] bsj " +
            "WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';")]
        [InlineData(
            "INSERT INTO [batch].[BatchStepJournal] (RunId, StepCode, StepStatus, StartedAtUtc) " +
            "VALUES (@RunId, N'S17', N'Running', SYSUTCDATETIME());",
            "UPDATE bsj SET bsj.StepStatus = N'Succeeded' FROM batch.BatchStepJournal bsj " +
            "WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';")]
        [InlineData(
            "INSERT INTO batch.[BatchStepJournal] (RunId, StepCode, StepStatus, StartedAtUtc) " +
            "VALUES (@RunId, N'S17', N'Running', SYSUTCDATETIME());",
            "UPDATE bsj SET bsj.StepStatus = N'Succeeded' FROM batch.[BatchStepJournal] bsj " +
            "WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';")]
        public void ValidateBatchStep_AcceptsABracketQuotedInsertPairedWithAnAliasedUpdate(
            string insertStatement, string updateStatement)
        {
            var markdown = Section(insertStatement + "\n" + updateStatement);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("UPDATE만"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("제어 테이블"));
        }

        // 같은 회귀를 batch.BatchCheckpoint에서도 확인한다 - BatchStepJournal 하나만
        // 고치고 다른 EachStepInserts 테이블을 놓치면 검사가 테이블마다 다르게 군다.
        [Fact]
        public void ValidateBatchStep_AcceptsABracketQuotedInsertPairedWithAnAliasedUpdateOnBatchCheckpoint()
        {
            var markdown = Section("""
                INSERT INTO [batch].[BatchCheckpoint] (RunId, StepCode, CheckpointStatus)
                VALUES (@RunId, N'S17', N'Pending');
                UPDATE bcp SET bcp.CheckpointStatus = N'Succeeded'
                FROM [batch].[BatchCheckpoint] bcp
                WHERE bcp.RunId = @RunId AND bcp.StepCode = N'S17';
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("UPDATE만"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("제어 테이블"));
        }

        // B-1 미탐 방어: 대괄호 인용 직접 이름 형태로 UPDATE만 하고 INSERT가 전혀
        // 없으면 여전히 잡혀야 한다 - 수정이 대괄호를 인식하게 하면서 반대로
        // 있어야 할 위반까지 놓치지 않았는지 잠근다.
        [Fact]
        public void ValidateBatchStep_RejectsABracketQuotedDirectNameUpdateOfAJournalRowItNeverInserts()
        {
            var markdown = Section(
                "UPDATE [batch].[BatchStepJournal] SET StepStatus = N'Succeeded' WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("INSERT") && e.Contains("BatchStepJournal"));
        }

        // BLOCKING B-2 회귀 방어: 산문 속 짝 없는 아포스트로피(영어 소유격) 뒤에 오는
        // SQL 펜스의 위반이 잡혀야 한다. 수정 전에는 BlankCommentsAndStrings가 문서
        // 전체를 한 번에 지워, 이 아포스트로피부터 문서 끝까지가 "문자열 안"이 되어
        // 펜스 전체가 공백으로 지워지고 검사가 아무 신호 없이 꺼졌다.
        [Fact]
        public void ValidateBatchStep_CatchesAControlColumnViolationAfterAProseApostrophe()
        {
            var markdown = """
                ### S17 완료 파티션 원자적 게시

                이 단계는 the orchestrator's checkpoint 값을 읽어 재시작 여부를 판단한다.

                ```sql
                UPDATE batch.BatchStepJournal SET ExecutionStatus = N'Completed' WHERE StepCode = N'S17';
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        // 같은 회귀를 인라인 코드 안의 아포스트로피(`don't`)로도 확인한다.
        [Fact]
        public void ValidateBatchStep_CatchesAControlColumnViolationAfterInlineCodeWithAnApostrophe()
        {
            var markdown = """
                ### S17 완료 파티션 원자적 게시

                이 로직은 `don't`처럼 예외적인 경우를 별도로 다루지 않는다.

                ```sql
                UPDATE batch.BatchStepJournal SET ExecutionStatus = N'Completed' WHERE StepCode = N'S17';
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        // B-2가 끈 두 검사 중 B3(행 출처)도 산문 아포스트로피 뒤에서 잡혀야 한다 -
        // 감사 실측 결함(INSERT 없는 UPDATE)이 정확히 이 경로로 사라졌었다.
        [Fact]
        public void ValidateBatchStep_CatchesAMissingInsertAfterAProseApostrophe()
        {
            var markdown = """
                ### S17 완료 파티션 원자적 게시

                이 단계는 the orchestrator's checkpoint를 갱신한다.

                ```sql
                UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded' WHERE StepCode = N'S17';
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("INSERT") && e.Contains("BatchStepJournal"));
        }

        // B-2 오탐 방어(사전 판정이 막으려던 것): 펜스 안 주석에만 나오는
        // `UPDATE bsj SET ...`는 헤더로 잡히면 안 된다 - 펜스 단위로 바꿔도
        // 주석·문자열은 여전히 지워진 사본에서 판정해야 한다.
        [Fact]
        public void ValidateBatchStep_DoesNotFlagAControlTableUpdateMentionedOnlyInAFenceComment()
        {
            var markdown = Section("""
                -- UPDATE bsj SET bsj.ExecutionStatus = N'Completed' FROM batch.BatchStepJournal bsj;
                SELECT 1;
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("ExecutionStatus"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("UPDATE만"));
        }

        // Important 1 회귀 방어: 직접 이름 대괄호 인용 UPDATE 헤더가 계약 밖 컬럼과
        // 금지 상태값을 둘 다 잡아야 한다 - CheckUpdateSetTargets의 headerAlternatives가
        // QualifiedTableNameFragment 이전에는 맨이름만 봐서 이 형태가 통째로 새어나갔다.
        [Fact]
        public void ValidateBatchStep_RejectsABracketQuotedDirectNameUpdateHeaderViolation()
        {
            var markdown = Section(
                "UPDATE [batch].[BatchStepJournal] SET [ExecutionStatus] = 1, [StepStatus] = N'Completed' " +
                "WHERE StepCode = N'S17';");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
            Assert.Contains(result.Errors, e => e.Contains("Completed") && e.Contains("Succeeded"));
        }

        // Important 1 회귀 방어: 대괄호 인용 INSERT 테이블명도 컬럼 목록 검사를
        // 우회하지 못해야 한다.
        [Fact]
        public void ValidateBatchStep_RejectsABracketQuotedTableNameInAnInsertColumnList()
        {
            var markdown = Section(
                "INSERT INTO [batch].[BatchStepJournal] (RunId, StepCode, [ExecutionStatus], StartedAtUtc) " +
                "VALUES (@RunId, N'S17', N'Completed', SYSUTCDATETIME());");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.Contains(result.Errors, e => e.Contains("ExecutionStatus"));
        }

        // Important 3: 대괄호 인용 별칭 형태의 정상 어휘(INSERT + UPDATE 짝)는 잡히면
        // 안 된다 - 지금까지 이 긍정 테스트가 없어서 B-1이 초록으로 통과했다.
        [Fact]
        public void ValidateBatchStep_AcceptsTheCanonicalVocabularyInABracketQuotedAliasedUpdate()
        {
            var markdown = Section("""
                INSERT INTO [batch].[BatchStepJournal] ([RunId], [StepCode], [StepStatus], [StartedAtUtc])
                VALUES (@RunId, N'S17', N'Running', SYSUTCDATETIME());
                UPDATE bsj SET bsj.StepStatus = N'Succeeded', bsj.CompletedAtUtc = SYSUTCDATETIME()
                FROM [batch].[BatchStepJournal] bsj
                WHERE bsj.RunId = @RunId AND bsj.StepCode = N'S17';
                """);

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("제어 테이블"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("UPDATE만"));
        }

        // === 실행 행 생성 담당 단계 검사 =====================================
        //
        // 실측(POQSettleProc17): 계획서 전체에 INSERT INTO batch.BatchRun이 0건인
        // 채로 L1 통합 검사에 세 번 연속 반려됐다. 통합 검사는 문서 전체를 보므로
        // "어느 단계가 고쳐야 하는가"를 말하지 못하고, 그 요구가 단계 재생성
        // 프롬프트(floorFeedback)에 실릴 경로가 없어 재시도가 수렴하지 못했다.
        // 담당 단계에 직접 요구를 걸어 그 경로를 잇는다.

        private static readonly string[] OwnsBatchRun = { "batch.BatchRun" };

        [Fact]
        public void ValidateBatchStep_RejectsAnOwnerThatOnlyUpdatesTheRunRow()
        {
            var markdown = Section(@"
UPDATE batch.BatchRun
   SET RunStatus = N'Running'
 WHERE RunId = @RunId;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                runRowOwnedTables: OwnsBatchRun);

            Assert.Contains(result.Errors, e => e.Contains("INSERT") && e.Contains("batch.BatchRun"));
        }

        // 이 결함은 본문을 다시 써서 고칠 수 있다. PlanDefects로 새면 재생성 없이
        // 배너만 붙고 끝나 - 잇고자 한 피드백 경로가 그대로 끊긴다.
        [Fact]
        public void ValidateBatchStep_LetsRegenerationFixAMissingRunRowInsert()
        {
            var markdown = Section("UPDATE batch.BatchRun SET RunStatus = N'Running' WHERE RunId = @RunId;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                runRowOwnedTables: OwnsBatchRun);

            Assert.True(result.RegenerationCanFix);
            Assert.Contains("INSERT", result.SuggestedPromptFix);
        }

        [Fact]
        public void ValidateBatchStep_AcceptsAnOwnerThatInsertsTheRunRow()
        {
            var markdown = Section(@"
INSERT INTO batch.BatchRun (JobName, BatchYmd, RunStatus, StartedAtUtc)
VALUES (@JobName, @BatchYmd, N'Running', SYSUTCDATETIME());
SET @RunId = SCOPE_IDENTITY();");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                runRowOwnedTables: OwnsBatchRun);

            Assert.DoesNotContain(result.Errors, e => e.Contains("실행 단위"));
        }

        // 대괄호 인용은 T-SQL에서 매우 흔하다. 통합 검사가 같은 이유로 한 번
        // 반려 사고를 냈으므로(QualifiedTableNameFragment 도입 경위) 단계 검사도
        // 같은 조각을 쓰는지 잠근다.
        [Theory]
        [InlineData("INSERT INTO [batch].[BatchRun] (JobName) VALUES (@JobName);")]
        [InlineData("INSERT INTO batch.[BatchRun] (JobName) VALUES (@JobName);")]
        [InlineData("MERGE batch.BatchRun AS T USING (SELECT @JobName AS JobName) AS S ON 1 = 0 " +
                    "WHEN NOT MATCHED THEN INSERT (JobName) VALUES (S.JobName);")]
        public void ValidateBatchStep_AcceptsEveryQuotingAndMergeFormOfTheRunRowInsert(string statement)
        {
            var result = new MechanicalValidator().ValidateBatchStep(
                Section(statement), Step("dbo.TSettleMst"), Catalog, NoConditions,
                runRowOwnedTables: OwnsBatchRun);

            Assert.DoesNotContain(result.Errors, e => e.Contains("실행 단위"));
        }

        // 담당이 아닌 단계는 같은 행을 UPDATE만 해도 정상이다 - 뒤 단계가 상태를
        // 갱신하는 것이 계약이다. 여기서 요구하면 실행 단위가 여러 번 생긴다.
        [Fact]
        public void ValidateBatchStep_DoesNotRequireTheRunRowInsertFromAStepThatDoesNotOwnIt()
        {
            var markdown = Section("UPDATE batch.BatchRun SET RunStatus = N'Succeeded' WHERE RunId = @RunId;");

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions);

            Assert.DoesNotContain(result.Errors, e => e.Contains("실행 단위"));
        }

        // 산문 속 영어 소유격 아포스트로피 하나가 뒤따르는 펜스를 통째로 지워
        // 검사를 조용히 끄는 사고가 통합 검사에서 재현된 적이 있다(CleanedSqlFences
        // 도입 경위). 단계 검사도 펜스 단위로 지우는지 잠근다.
        [Fact]
        public void ValidateBatchStep_StillSeesAViolationAfterAProseApostrophe()
        {
            var markdown = """
                ### S17 완료 파티션 원자적 게시

                This step reuses the orchestrator's run row instead of creating one.

                ```sql
                UPDATE batch.BatchRun SET RunStatus = N'Running' WHERE RunId = @RunId;
                ```
                """;

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, Step("dbo.TSettleMst"), Catalog, NoConditions,
                runRowOwnedTables: OwnsBatchRun);

            Assert.Contains(result.Errors, e => e.Contains("INSERT") && e.Contains("batch.BatchRun"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectANonNumericControlCode()
        {
            // 실측(reset-20260824.log, 4회): `DECLARE @v_currentStepId INT = B161`.
            // B161은 해석되지 않는 식별자라 이 SQL은 컴파일되지 않는다. 기존
            // CheckStepIdInitialValue는 DECLARE 정규식이 `-?\d+`만 읽어 이것을 놓친다.
            var step = new BatchStepPlan(
                "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9160" }, false, new string[0]);

            var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = B161;
SELECT 1 FROM dbo.TSettleMst;
SET @po_intRetVal = @v_currentStepId;
```
-9160
";

            var result = Validate(markdown, step);

            Assert.Contains(result.Errors, e => e.Contains("B161"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectAControlCodeOutsideTheStepsBlock()
        {
            // 대역만 맞고 블록이 틀리면 반환값으로 단계를 특정할 수 없다.
            var step = new BatchStepPlan(
                "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9160" }, false, new string[0]);

            var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = -9160;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = -9010;
SET @po_intRetVal = @v_currentStepId;
```
";

            var result = Validate(markdown, step);

            Assert.Contains(result.Errors, e => e.Contains("-9010"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptCodesInsideTheStepsBlock()
        {
            var step = new BatchStepPlan(
                "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9160" }, false, new string[0]);

            var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = -9160;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = -9161;
SET @po_intRetVal = @v_currentStepId;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("-9161"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotApplyTheBandRuleToAStepWithALegacyOrigin()
        {
            // 레거시 출신이 있는 단계의 -9는 원본 코드다. 대역 검사를 적용하면
            // 정상 단계가 전부 걸린다.
            var step = new BatchStepPlan(
                "S05", "원장 생성", new[] { "dbo.UP_UTIL_SETTLE_INS" },
                new[] { "dbo.TSettleMst" }, new[] { "-9" }, false, new string[0]);

            var markdown = @"### S05 원장 생성

```sql
DECLARE @v_currentStepId INT = 0;
SET @v_currentStepId = -9;
SELECT 1 FROM dbo.TSettleMst;
SET @po_intRetVal = @v_currentStepId;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("예약 블록"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotFlagTheZeroInitialValueAsOutsideTheBlock()
        {
            // 0은 "아직 실패 지점을 지나지 않았다"는 초기값이지 블록 밖 코드가 아니다.
            // 레거시 출신이 없는 단계라도 이 초기화 자체는 정상이어야 한다.
            var step = new BatchStepPlan(
                "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9160" }, false, new string[0]);

            var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = 0;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = -9160;
SET @po_intRetVal = @v_currentStepId;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("예약 블록"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotClaimAStringControlCodeDoesNotCompile()
        {
            // 실측(POQSettleBatch1/S03, 설계서가 인용하는 바로 그 예): 이 SP는
            // `@po_strRetCode NVARCHAR(10) OUTPUT`으로 문자열 코드를 돌려준다.
            // `N'B120'`은 유효한 T-SQL이다 - 블랭크 사본에서는 문자열 내용이
            // 공백으로 지워져 값이 'N'으로 잘리고, 그 잘린 값을 근거로
            // "컴파일되지 않는다"는 거짓 주장을 했다. 문자열 코드 제어 단계를
            // 허용할지 금지할지는 이 검사가 결정할 문제가 아니다 - 침묵해야 한다.
            var step = new BatchStepPlan(
                "S03", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new string[0], false, new string[0]);

            var markdown = @"### S03 통합 검증

```sql
DECLARE @v_currentStepCode NVARCHAR(10) = N'B120';
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepCode = N'B120';
SET @po_strRetCode = @v_currentStepCode;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("'N'"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotClaimALongStringControlCodeDoesNotCompile()
        {
            // 실측(POQSettleProc18/S01): `@v_currentStepId nvarchar(64)`에
            // `N'BATCH-VAL-001'`을 대입한다. NVARCHAR로 선언된 변수는 이
            // 검사(INT 예약 블록)의 대상이 아니다.
            var step = new BatchStepPlan(
                "S01", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new string[0], false, new string[0]);

            var markdown = @"### S01 통합 검증

```sql
DECLARE @v_currentStepId nvarchar(64) = N'BATCH-VAL-001';
SELECT 1 FROM dbo.TSettleMst;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotClaimANullInitialValueDoesNotCompile()
        {
            // 실측(POQSettleProc8/S18,S19 등 코퍼스 전역의 부트스트랩 관용구):
            // `DECLARE @v_currentStepId INT = NULL;`은 유효한 T-SQL이다.
            // 이 규약 아래 NULL이 바람직한 초기값인지는 별개 문제이지만,
            // "컴파일되지 않는다"는 주장만은 거짓이므로 절대 내면 안 된다.
            var step = new BatchStepPlan(
                "S18", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new string[0], false, new string[0]);

            var markdown = @"### S18 통합 검증

```sql
DECLARE @v_currentStepId INT = NULL;
SELECT 1 FROM dbo.TSettleMst;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotApplyTheBandRuleToAVariableNeverDeclaredAsInt()
        {
            // 문자열/NULL 스킵과는 별개로, 같은 펜스에서 INT로 선언된 적이 없는
            // 변수의 SET은 값이 (따옴표 없는) 숫자처럼 보여도 정수 축(파싱 실패·
            // 블록 밖)으로 판정하지 않는다 - 그 변수가 실제로 INT인지 모르기
            // 때문이다(예: NVARCHAR 변수에 42를 SET하면 SQL이 문자열 "42"로 암묵
            // 변환할 뿐이다). 이 테스트는 INT 선언 게이트가 무력화되면(따옴표·NULL
            // 스킵과 무관하게) 실패한다.
            //
            // [Task 2] DECLARE 초기값은 자기 코드(N'S03')로 둔다 - Task 2 이후
            // `NVARCHAR(10) = N'B120'` 자체가 이 검사의 별개 축(문자열 코드 축)에서
            // 정당하게 발화하므로, 자기 코드가 아닌 리터럴을 쓰면 이 테스트가
            // 검증하려는 "정수 축 게이트"와 무관한 이유로 실패한다.
            var step = new BatchStepPlan(
                "S03", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new string[0], false, new string[0]);

            var markdown = @"### S03 통합 검증

```sql
DECLARE @v_currentStepCode NVARCHAR(10) = N'S03';
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepCode = 42;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("예약 블록"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotClaimAVariableReferenceControlCodeDoesNotCompile()
        {
            // 실측(POQSettleProc6/S22): `SET @v_currentStepId = @LegacyCode;`.
            // `@LegacyCode`는 선언된 변수를 가리키는 유효한 식별자다 - `SET @a = @b`는
            // 컴파일된다. B161과 달리 이 값은 실행 시점에 정해지므로 이 검사가
            // 리터럴로 판정할 수 없다 - 침묵해야 한다.
            var step = new BatchStepPlan(
                "S22", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new string[0], false, new string[0]);

            var markdown = @"### S22 통합 검증

```sql
DECLARE @v_currentStepId INT = 0;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = @LegacyCode;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotClaimACaseExpressionControlCodeDoesNotCompile()
        {
            // `CASE WHEN ... THEN -9221 ELSE -9222 END`은 실행 시점에 갈리는 식이다 -
            // 값 자리 첫 토큰 `CASE`만으로는 이 검사가 리터럴을 판정할 수 없다.
            var step = new BatchStepPlan(
                "S22", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new string[0], false, new string[0]);

            var markdown = @"### S22 통합 검증

```sql
DECLARE @v_currentStepId INT = 0;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = CASE WHEN @x = 1 THEN -9221 ELSE -9222 END;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotClaimAFunctionCallControlCodeDoesNotCompile()
        {
            // `ERROR_NUMBER()`, `@@ERROR` 같은 함수 호출·시스템 변수도 실행 시점
            // 값이다 - 리터럴이 아니므로 이 검사가 판정할 수 없다.
            var step = new BatchStepPlan(
                "S22", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new string[0], false, new string[0]);

            var markdown = @"### S22 통합 검증

```sql
DECLARE @v_currentStepId INT = 0;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = ERROR_NUMBER();
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotDemandACodeThatOnlyASplitProcedureOwes()
        {
            // 실측(POQSettleProc4): UP_UTIL_SETTLE_EXCEPTION_PROC이 18개 단계에 나뉘어
            // 있다. 단계마다 그 SP의 코드 전량을 요구하면 18개 단계가 만족 불가능한
            // 요구를 받는다 - 문장 개수 대조가 이미 같은 이유로 면제받는다.
            var s10 = new BatchStepPlan(
                "S10", "예외 정책 1", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
                new[] { "-1", "-2" }, false, new string[0]);
            var s11 = new BatchStepPlan(
                "S11", "예외 정책 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
                new[] { "-1", "-2" }, false, new string[0]);

            var markdown = @"### S10 예외 정책 1

```sql
SET @v_currentStepId = -1;
DELETE FROM dbo.T1 WHERE YMD = @pi_strYMD;
```
";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, s10, new[] { "dbo.T1" },
                new Dictionary<string, SpecConditions>(),
                allSteps: new[] { s10, s11 },
                codesByProcedure: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UP_X"] = new[] { "-1", "-2" }
                });

            Assert.DoesNotContain(result.Errors, e => e.Contains("'-2'"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldStillDemandACodeANonSplitProcedureOwes()
        {
            // 같은 단계가 분할되지 않은 SP도 맡고 있고 그 SP가 그 코드를 가지면
            // 귀속이 확실하므로 계속 요구한다.
            var s10 = new BatchStepPlan(
                "S10", "예외 정책", new[] { "dbo.UP_X", "dbo.UP_Y" }, new[] { "dbo.T1" },
                new[] { "-1", "-2" }, false, new string[0]);
            var s11 = new BatchStepPlan(
                "S11", "예외 정책 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
                new[] { "-1" }, false, new string[0]);

            var markdown = @"### S10 예외 정책

```sql
SET @v_currentStepId = -1;
DELETE FROM dbo.T1 WHERE YMD = @pi_strYMD;
```
";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, s10, new[] { "dbo.T1" },
                new Dictionary<string, SpecConditions>(),
                allSteps: new[] { s10, s11 },
                codesByProcedure: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UP_X"] = new[] { "-1" },
                    ["UP_Y"] = new[] { "-2" }
                });

            Assert.Contains(result.Errors, e => e.Contains("'-2'"));
        }

        [Fact]
        public void ValidateBatchStep_WithoutAttributionMaterial_ShouldKeepTheOldBehaviour()
        {
            // 재료가 없다는 사실을 결함 없음으로 바꾸지 않는다 - allSteps == null일
            // 때의 하위 호환과 같은 태도다.
            var s10 = new BatchStepPlan(
                "S10", "예외 정책", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
                new[] { "-1", "-2" }, false, new string[0]);
            var s11 = new BatchStepPlan(
                "S11", "예외 정책 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
                new[] { "-1", "-2" }, false, new string[0]);

            var markdown = @"### S10 예외 정책

```sql
SET @v_currentStepId = -1;
DELETE FROM dbo.T1 WHERE YMD = @pi_strYMD;
```
";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, s10, new[] { "dbo.T1" },
                new Dictionary<string, SpecConditions>(),
                allSteps: new[] { s10, s11 });

            Assert.Contains(result.Errors, e => e.Contains("'-2'"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldStillDemandACodeWithNoKnownOwner()
        {
            // codesByProcedure 어디에도 이 코드를 가진 SP가 없으면(owners.Count == 0)
            // 귀속을 확정할 수 없다. "분할 SP에서만 유래" 판정은 소유자가 있어야
            // 성립하므로, 소유자가 아예 없을 때 면제로 뒤집으면 안 된다 - 그것은
            // "누가 빚졌는지 모른다"를 "아무도 안 빚졌다"로 오독하는 것이다.
            var s10 = new BatchStepPlan(
                "S10", "예외 정책", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
                new[] { "-1", "-99" }, false, new string[0]);
            var s11 = new BatchStepPlan(
                "S11", "예외 정책 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
                new[] { "-1" }, false, new string[0]);

            var markdown = @"### S10 예외 정책

```sql
SET @v_currentStepId = -1;
DELETE FROM dbo.T1 WHERE YMD = @pi_strYMD;
```
";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, s10, new[] { "dbo.T1" },
                new Dictionary<string, SpecConditions>(),
                allSteps: new[] { s10, s11 },
                codesByProcedure: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UP_X"] = new[] { "-1" }
                });

            Assert.Contains(result.Errors, e => e.Contains("'-99'"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldStillDemandASharedCodeWhenAStepOwnsBothASplitAndANonSplitProcedure()
        {
            // 같은 단계가 분할된 SP(UP_X)와 분할되지 않은 SP(UP_Y)를 함께 맡고, 두 SP가
            // 같은 코드를 가지는 경우. 소유자 중 하나(UP_Y)라도 분할되지 않았으면
            // 귀속이 확실하므로 계속 요구한다 - 분할 SP가 섞여 있다는 이유로
            // 비분할 SP의 의무까지 면제하면 안 된다.
            var s10 = new BatchStepPlan(
                "S10", "예외 정책", new[] { "dbo.UP_X", "dbo.UP_Y" }, new[] { "dbo.T1" },
                new[] { "-5" }, false, new string[0]);
            var s11 = new BatchStepPlan(
                "S11", "예외 정책 2", new[] { "dbo.UP_X" }, new[] { "dbo.T1" },
                new[] { "-5" }, false, new string[0]);

            var markdown = @"### S10 예외 정책

```sql
SET @v_currentStepId = 0;
DELETE FROM dbo.T1 WHERE YMD = @pi_strYMD;
```
";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, s10, new[] { "dbo.T1" },
                new Dictionary<string, SpecConditions>(),
                allSteps: new[] { s10, s11 },
                codesByProcedure: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UP_X"] = new[] { "-5" },
                    ["UP_Y"] = new[] { "-5" }
                });

            Assert.Contains(result.Errors, e => e.Contains("'-5'"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotDemandATableThatOnlyASplitProcedureOwes()
        {
            // 테이블 축의 같은 판정. UP_X가 S10·S11에 나뉘어 있고 정적 분석이 그 SP의
            // 쓰기 대상으로 dbo.T2를 낸다면, 이 단계 하나가 dbo.T2 전체를 언급할
            // 의무는 없다 - 문서 단위 검사(Task 5)가 그 의무를 회수한다.
            var s10 = new BatchStepPlan(
                "S10", "예외 정책", new[] { "dbo.UP_X" }, new[] { "dbo.T2" },
                new string[0], false, new string[0]);
            var s11 = new BatchStepPlan(
                "S11", "예외 정책 2", new[] { "dbo.UP_X" }, new[] { "dbo.T2" },
                new string[0], false, new string[0]);

            var markdown = @"### S10 예외 정책

```sql
SET @v_currentStepId = 0;
SELECT 1;
```
";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, s10, new[] { "dbo.T2" },
                new Dictionary<string, SpecConditions>(),
                allSteps: new[] { s10, s11 },
                tablesByProcedure: new Dictionary<string, SpecTargetTableExtractor.StepTableSets>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UP_X"] = new SpecTargetTableExtractor.StepTableSets(
                        new[] { "dbo.T2" }, Array.Empty<string>())
                });

            Assert.DoesNotContain(result.Errors, e => e.Contains("'dbo.T2'"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectAStringErrorCodeInAControlStep()
        {
            // 실측(POQSettleBatch1/S03, POQSettleProc13 등 17단계): 레거시 출신이 없는
            // 단계가 N'B120'·N'BATCH-LOCK-001' 같은 문자열 코드를 쓴다. B1xx를 INT 축에서
            // 몰아낸 뒤에도 문자열 자리에는 지어낸 어휘가 그대로 남아 있었다.
            var step = new BatchStepPlan(
                "S03", "입력 기준시점 고정", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9030" }, false, new string[0]);

            var markdown = @"### S03 입력 기준시점 고정

```sql
DECLARE @v_currentStepCode NVARCHAR(10) = N'B120';
SELECT 1 FROM dbo.TSettleMst;
```
-9030
";

            var result = Validate(markdown, step);

            Assert.Contains(result.Errors, e => e.Contains("B120"));
            // 이 값은 컴파일된다. 거짓 진술을 하면 안 된다.
            Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일되지 않습니다"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptAStepIdentifierStringInAControlStep()
        {
            // 실측 12단계: `DECLARE @v_stepCode nvarchar(10) = N'S01'`은 정당하다.
            // BatchControlContract가 batch.BatchStepJournal.StepCode를 nvarchar(10)으로
            // 규정하므로, 자기 신원을 저널에 쓰려면 문자열이어야 한다. 이것을 위반으로
            // 잡으면 제어 계약을 어기라고 요구하는 셈이다. allSteps를 넘기지 않으므로
            // (null) 자기 코드만 예외로 두는 경로를 탄다.
            var step = new BatchStepPlan(
                "S01", "실행 등록", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9010" }, false, new string[0]);

            var markdown = @"### S01 실행 등록

```sql
DECLARE @v_stepCode nvarchar(10) = N'S01';
SELECT 1 FROM dbo.TSettleMst;
```
-9010
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("S01'") || e.Contains("문자열 코드"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectAnotherStepsIdentifierWhenAllStepsIsNull()
        {
            // allSteps가 없으면(재료 없음) 종전대로 자기 코드만 예외다 - 재료가 없다는
            // 사실을 결함 없음으로 바꾸지 않는다. Validate 헬퍼는 allSteps를 넘기지
            // 않으므로 이 단계 목록에 S02가 실재하더라도 이 호출에서는 알 길이 없고,
            // 자기 코드가 아닌 문자열은 여전히 발화해야 한다.
            var step = new BatchStepPlan(
                "S01", "실행 등록", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9010" }, false, new string[0]);

            var markdown = @"### S01 실행 등록

```sql
DECLARE @v_stepCode nvarchar(10) = N'S02';
SELECT 1 FROM dbo.TSettleMst;
```
-9010
";

            var result = Validate(markdown, step);

            Assert.Contains(result.Errors, e => e.Contains("S02"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectAnotherStepsIdentifier()
        {
            // 예외는 "이 Job의 단계 목록에 있는 코드"에 걸린다. 목록에 없는 단계
            // (N'S99')는 이 Job에 실재하지 않는 신원이므로 예외 없이 발화해야 한다 -
            // 예외를 "단계 코드 형태이면"으로 넓히면 이 없는 단계까지 통과한다.
            var s01 = new BatchStepPlan(
                "S01", "실행 등록", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9010" }, false, new string[0]);
            var s02 = new BatchStepPlan(
                "S02", "선행 확인", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9020" }, false, new string[0]);

            var markdown = @"### S01 실행 등록

```sql
DECLARE @v_stepCode nvarchar(10) = N'S99';
SELECT 1 FROM dbo.TSettleMst;
```
-9010
";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, s01, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(),
                allSteps: new[] { s01, s02 });

            Assert.Contains(result.Errors, e => e.Contains("S99"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptAnotherKnownStepsIdentifier()
        {
            // 예외 1을 자기 코드에서 이 Job의 단계 목록 전체로 넓힌 것은 **방어적
            // 예외**다. 앞서 이 자리에 적혀 있던 "실측(POQSettleProc16/S02):
            // `@v_firstIncompleteStepCode`"는 근거가 되지 않는다 - 그 변수는
            // `DECLARE @v_firstIncompleteStepCode CHAR(3);`(초기값 없음)이고 값은
            // `SELECT`로 들어와, 이 검사가 보는 리터럴 대입 경로에 도달하지 않는다.
            // 타 단계 코드를 리터럴로 대입하는 자리는 코퍼스 전수 실측 **0건**이다
            // (자기 코드 대입만 20건). 그래도 예외를 두는 근거는 실측이 아니라
            // 정의다: 이 Job의 단계 목록에 있는 코드는 정의상 지어낸 어휘가 아니므로
            // "지어낸 오류 코드"라고 발화하면 거짓 진술이 된다. 이 테스트가 고정하는
            // 것은 그 정의다(변수 이름은 이 검사와 무관하다 - `...StepCode`로 끝나기만
            // 하면 같은 경로를 탄다).
            var s01 = new BatchStepPlan(
                "S01", "실행 등록", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9010" }, false, new string[0]);
            var s02 = new BatchStepPlan(
                "S02", "선행 확인", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9020" }, false, new string[0]);

            var markdown = @"### S01 실행 등록

```sql
DECLARE @v_firstIncompleteStepCode nvarchar(10) = N'S02';
SELECT 1 FROM dbo.TSettleMst;
```
-9010
";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, s01, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(),
                allSteps: new[] { s01, s02 });

            Assert.DoesNotContain(result.Errors, e => e.Contains("문자열 코드"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotFlagATimestampOrFlagStateVariable()
        {
            // 실측 2건: `@v_stepStartedAtUtc DATETIME2(3) = SYSUTCDATETIME()`와
            // `@v_isStepCompleted BIT = 0`. 규칙이 "타입이 비INT면 위반"이었다면 이
            // 둘이 걸렸을 것이다 - 값으로 가르는 이유가 이것이다.
            //
            // [최종 픽스(리뷰 Important 1)] 이 둘은 이제 이름 패턴에서도 빠진다
            // (`Step` 바로 뒤가 `Code`/`Id`/`Status`가 아니다). 이 테스트는 그
            // 좁히기 이전부터 지키던 침묵을 그대로 고정한다 - 이름 패턴을 되돌려도,
            // 타입 규칙을 되돌려도 여기서 죽는다.
            var step = new BatchStepPlan(
                "S03", "입력 기준시점 고정", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9030" }, false, new string[0]);

            var markdown = @"### S03 입력 기준시점 고정

```sql
DECLARE @v_stepStartedAtUtc DATETIME2(3) = SYSUTCDATETIME();
DECLARE @v_isStepCompleted BIT = 0;
SELECT 1 FROM dbo.TSettleMst;
```
-9030
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("문자열 코드"));
        }

        [Theory]
        [InlineData("DECLARE @v_stepName NVARCHAR(100) = N'날짜 검증';", "날짜 검증")]
        [InlineData("DECLARE @v_stepErrorMessage NVARCHAR(200) = N'선행 단계가 완료되지 않았습니다.';", "선행 단계가")]
        [InlineData("DECLARE @v_stepTargetTable SYSNAME = N'batch.BatchStepJournal';", "batch.BatchStepJournal")]
        [InlineData("DECLARE @v_expectedPriorStepCount int = 15;", "15")]
        [InlineData("DECLARE @v_isStepValid NVARCHAR(5) = N'Y';", "Y")]
        public void ValidateBatchStep_ShouldNotFlagADescriptiveStepNamedVariable(
            string declaration, string valueFragment)
        {
            // [최종 픽스(리뷰 Important 1)] 이름 패턴이 `@\w*[Ss]tep\w*`이던 동안에는
            // "이름에 step이 든 모든 변수"가 판정 대상이었다. 타입 자리가 INT 고정이던
            // 동안에는 그 폭이 안전했지만(서술 변수는 INT로 선언되지 않는다), 문자열
            // 축이 그 게이트를 걷어내면서 서술용 문자열이 곧바로 오류 코드로 발화했다.
            // 리뷰어가 실행으로 재현한 발화:
            //   "S03 섹션이 상태 변수에 문자열 코드 '날짜 검증'을 대입합니다.
            //    레거시 출신이 없는 단계는 예약 블록(-9030부터 10개)의 음수 정수를 씁니다"
            // 거짓 진술(단계 이름은 오류 코드가 아니다) 위에 해로운 지시(이름을
            // -9030으로 바꾸라)를 얹는 발화다.
            //
            // 이름을 `...Step(_)?Code|Id|Status`로 좁혀 닫았다. 이 테스트가 그 좁히기를
            // 고정한다 - 다음 재작성이 이름 패턴을 다시 넓히면 여기서 죽는다.
            // `@v_expectedPriorStepCount`는 덤으로 닫힌 기존 오탐이고(숫자 축,
            // known-defects.md의 `Ruling 4`가 범위 밖으로 미뤄 둔 실측 1건),
            // `@v_isStepValid`는 "이름이 Code/Id로 끝나기만 하면"으로 좁혔을 때 다시
            // 들어오는 형태다(`Valid`가 `id`로 끝난다) - 접미사를 `Step` 바로 뒤로
            // 묶은 이유가 이것이다.
            var step = new BatchStepPlan(
                "S03", "입력 기준시점 고정", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9030" }, false, new string[0]);

            var markdown = $@"### S03 입력 기준시점 고정

```sql
{declaration}
SELECT 1 FROM dbo.TSettleMst;
```
-9030
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains(valueFragment));
            Assert.DoesNotContain(result.Errors, e => e.Contains("문자열 코드"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("예약 블록"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldFlagAStepCodeNamedVariableAfterTheNarrowing()
        {
            // 좁히기가 진짜 발화까지 죽이지 않았음을 고정한다. 코퍼스가 실제로
            // 발화시키는 이름은 `@v_currentStepCode`·`@v_currentStepId` 둘뿐이고
            // (문자열 축 26건/17단계, 숫자 축 160건/43단계) 둘 다 좁힌 패턴에 들어온다.
            // 위 오탐 테스트만 있으면 "이름 패턴을 아무것도 매치하지 않게" 만드는
            // 변이가 조용히 통과한다.
            var step = new BatchStepPlan(
                "S03", "입력 기준시점 고정", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9030" }, false, new string[0]);

            var markdown = @"### S03 입력 기준시점 고정

```sql
DECLARE @v_currentStepCode NVARCHAR(10) = N'B120';
DECLARE @v_currentStepId INT = 1;
SELECT 1 FROM dbo.TSettleMst;
```
-9030
";

            var result = Validate(markdown, step);

            Assert.Contains(result.Errors, e => e.Contains("문자열 코드") && e.Contains("B120"));
            Assert.Contains(result.Errors, e => e.Contains("예약 블록 밖의 제어 코드 '1'"));
        }

        [Theory]
        // 같은 두 선언, 순서만 다르다. 판정이 갈리면 회귀다.
        [InlineData(@"DECLARE @v_stepCode NVARCHAR(10) = 1;
DECLARE @v_currentStepId INT = 1;")]
        [InlineData(@"DECLARE @v_currentStepId INT = 1;
DECLARE @v_stepCode NVARCHAR(10) = 1;")]
        public void ValidateBatchStep_ShouldNotDependOnDeclarationOrder(string declarations)
        {
            // [최종 픽스(리뷰 Important 2)] `reported`가 값(`raw`)만으로 키잉하는 동안
            // 판정이 선언 순서에 의존했다. 타입 자리를 `\w+`로 넓히면서 비INT 선언의
            // 값도 이 집합을 선점하게 됐고, 같은 값을 쓰는 비INT 선언이 앞에 오면
            // 뒤따르는 정수 축 위반이 통째로 사라졌다 - 직전 회차에 병합돼 이미
            // 43단계를 발화시키고 있는 숫자 축 검사에 이 브랜치가 넣은 미탐이다.
            // 미탐은 조용해서 다음 회차가 발견하지 못하므로 회귀 테스트로 고정한다.
            //
            // `NVARCHAR(10) = 1`은 T-SQL에서 정상 컴파일되고(암묵 변환) 이 검사는
            // 그래서 침묵한다 - 침묵하는 쪽이 발화하는 쪽을 삼키면 안 된다는 것이
            // 이 테스트의 요지다.
            var step = new BatchStepPlan(
                "S03", "입력 기준시점 고정", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9030" }, false, new string[0]);

            var markdown = $@"### S03 입력 기준시점 고정

```sql
{declarations}
SELECT 1 FROM dbo.TSettleMst;
```
-9030
";

            var result = Validate(markdown, step);

            Assert.Contains(result.Errors, e => e.Contains("예약 블록 밖의 제어 코드 '1'"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldReportTheSameValueOnceForEachVariable()
        {
            // dedupe의 원래 취지(같은 변수의 같은 값을 여러 번 발화하지 않음)는
            // 그대로 두고, 서로 다른 변수는 각각 발화한다. 값만으로 키잉하면 뒤에 온
            // 변수의 위반이 앞선 변수에 가려진다.
            var step = new BatchStepPlan(
                "S03", "입력 기준시점 고정", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9030" }, false, new string[0]);

            var markdown = @"### S03 입력 기준시점 고정

```sql
DECLARE @v_currentStepCode NVARCHAR(10) = N'B120';
DECLARE @v_stepCode NVARCHAR(10) = N'B120';
SET @v_stepCode = N'B120';
SELECT 1 FROM dbo.TSettleMst;
```
-9030
";

            var result = Validate(markdown, step);

            // 변수 둘 - 발화도 둘. 같은 변수의 재대입(SET)은 한 번만 발화한다.
            Assert.Equal(2, result.Errors.Count(e => e.Contains("문자열 코드") && e.Contains("B120")));
        }

        [Fact]
        public void ValidateBatchStep_ShouldStillBeSilentOnNonLiteralValues()
        {
            // 오탐 두 라운드를 들여 걷어낸 침묵이다. 이번 변경이 되살리면 회귀다.
            var step = new BatchStepPlan(
                "S22", "정리", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9220" }, false, new string[0]);

            var markdown = @"### S22 정리

```sql
DECLARE @v_currentStepId INT = NULL;
SET @v_currentStepId = @LegacyCode;
SET @v_currentStepId = CASE WHEN 1 = 1 THEN -9221 ELSE -9222 END;
SET @v_currentStepId = ERROR_NUMBER();
SELECT 1 FROM dbo.TSettleMst;
```
-9220
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("컴파일되지 않습니다"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("문자열 코드"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotApplyTheStringRuleToAStepWithALegacyOrigin()
        {
            // 레거시 출신이 있으면 원본 규약을 따른다. 이 검사의 관할이 아니다.
            var step = new BatchStepPlan(
                "S05", "원장 생성", new[] { "dbo.UP_UTIL_SETTLE_INS" },
                new[] { "dbo.TSettleMst" }, new[] { "-9" }, false, new string[0]);

            var markdown = @"### S05 원장 생성

```sql
DECLARE @v_currentStepCode NVARCHAR(10) = N'B120';
SELECT 1 FROM dbo.TSettleMst;
```
-9
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("문자열 코드"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectAStringErrorCodeContainingParentheses()
        {
            // [픽스 라운드 1(리뷰 I1)] 문자열 리터럴 블록은 `raw.Contains('(')` 검사
            // (함수 호출 판정)보다 앞에 있어야 한다 - 안 그러면 괄호가 든 문자열
            // (`N'B(1)'`)이 `ERROR_NUMBER()`류 함수 호출로 오인돼 조용히 넘어간다.
            // 이 테스트가 그 위치 불변식을 고정한다: 블록을 아래로 옮기는 변이는
            // 이 테스트를 죽여야 한다.
            var step = new BatchStepPlan(
                "S03", "입력 기준시점 고정", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9030" }, false, new string[0]);

            var markdown = @"### S03 입력 기준시점 고정

```sql
DECLARE @v_currentStepCode NVARCHAR(10) = N'B(1)';
SELECT 1 FROM dbo.TSettleMst;
```
-9030
";

            var result = Validate(markdown, step);

            Assert.Contains(result.Errors, e => e.Contains("B(1)"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectAnUnprefixedStringErrorCode()
        {
            // [픽스 라운드 1(리뷰 I2)] 실측(POQSettleProc19/S02):
            // `DECLARE @v_currentStepCode varchar(64) = 'B011'`. N 접두사 없는 문자열
            // 리터럴도 TryReadStringLiteral의 첫 갈래(`raw[0] == '\''`)를 타야 한다 -
            // N 접두사가 있는 형태만 테스트하면 이 갈래를 지우는 변이가 살아남는다.
            var step = new BatchStepPlan(
                "S02", "선행 확인", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9020" }, false, new string[0]);

            var markdown = @"### S02 선행 확인

```sql
DECLARE @v_currentStepCode varchar(64) = 'B011';
SELECT 1 FROM dbo.TSettleMst;
```
-9020
";

            var result = Validate(markdown, step);

            Assert.Contains(result.Errors, e => e.Contains("B011"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptAContractStatusValueAssignment()
        {
            // [픽스 라운드 1(리뷰 I3, 컨트롤러 판정)] `Running`은
            // BatchControlContract(batch.BatchStepJournal.StepStatus 등)가 정의한
            // 체크포인트 상태값이지 오류 코드가 아니다. 이름으로 "상태 변수 같다"고
            // 추정하지 않고 BatchControlContract.AllowedStatusValues를 그대로
            // 조회해서 침묵해야 한다 - 코퍼스 전수 grep에서 대입 형태로는 0건이지만
            // 같은 어휘가 변수 이름 옆에서 실제로 쓰이고 있어 다음 생성분의 대입
            // 형태 발화를 미리 막는다.
            var step = new BatchStepPlan(
                "S07", "진행 갱신", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9070" }, false, new string[0]);

            var markdown = @"### S07 진행 갱신

```sql
DECLARE @v_stepStatus NVARCHAR(20) = NULL;
SET @v_stepStatus = N'Running';
SELECT 1 FROM dbo.TSettleMst;
```
-9070
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("문자열 코드"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldRejectAnUnknownStringEvenOnAStatusNamedVariable()
        {
            // 계약 조회이지 이름 추정이 아니라는 것을 고정한다 - 변수 이름이
            // Status로 끝나도, 그 값이 계약의 상태 어휘에 없으면(`B120`) 여전히
            // 지어낸 오류 코드로 발화해야 한다.
            var step = new BatchStepPlan(
                "S07", "진행 갱신", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9070" }, false, new string[0]);

            var markdown = @"### S07 진행 갱신

```sql
DECLARE @v_stepStatus NVARCHAR(20) = NULL;
SET @v_stepStatus = N'B120';
SELECT 1 FROM dbo.TSettleMst;
```
-9070
";

            var result = Validate(markdown, step);

            Assert.Contains(result.Errors, e => e.Contains("B120"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldSkipABlockCommentBeforeReadingTheValue()
        {
            // [픽스 라운드 2 - 새 Important] 실측(POQSettleProc8/agent/common/
            // 01-step-contract.md:34,37, docs/BatchMigrationPlan.md:319,322):
            // `SET @v_currentStepId = /* 이 DML의 정확한 레거시 오류 코드 */ -9161;`.
            // 값 위치를 원문에서 직접 찾도록 바꾸면서(픽스 라운드 1, 리뷰 I2) 주석을
            // 건너뛰지 않으면 `raw`가 "/*"가 되어 "숫자가 아닌 값 '/*'을 대입합니다"라는
            // 거짓 발화가 나간다 - 주석을 건너뛰고 그 뒤의 실제 값(-9161, 블록 안)을
            // 읽어야 침묵한다.
            var step = new BatchStepPlan(
                "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9160" }, false, new string[0]);

            var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = -9160;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = /* 이 DML의 정확한 레거시 오류 코드 */ -9161;
SET @po_intRetVal = @v_currentStepId;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("/*"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("숫자가 아닌 값"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("예약 블록"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotFlagAValueThatIsOnlyAComment()
        {
            // 값 자리가 주석뿐이면(`= /* ... */;`) 공백·주석을 다 걷어낸 뒤 남는
            // 토큰이 없다 - 대입할 값 자체가 없으므로 판정할 재료가 없다. 침묵이
            // 맞다(빈 값을 근거로 "숫자가 아닌 값 ''을 대입합니다"라고 말하면 그
            // 자체가 거짓 주장이다 - 귀속할 수 없으면 침묵한다는 이 검사의 원칙과
            // 같다).
            var step = new BatchStepPlan(
                "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9160" }, false, new string[0]);

            var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = -9160;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = /* 이 DML의 정확한 레거시 오류 코드 */;
SET @po_intRetVal = @v_currentStepId;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("/*"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("숫자가 아닌 값"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldSkipALineCommentBeforeReadingTheValue()
        {
            // 한 줄 주석(`--`) 형태도 블록 주석과 같이 건너뛰어야 한다.
            var step = new BatchStepPlan(
                "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9160" }, false, new string[0]);

            var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT = -9160;
SELECT 1 FROM dbo.TSettleMst;
SET @v_currentStepId = -- 이 DML의 정확한 레거시 오류 코드
    -9161;
SET @po_intRetVal = @v_currentStepId;
```
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("숫자가 아닌 값"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("예약 블록"));
        }

        [Fact]
        public void ValidateBatchStep_ShouldNotReadPastTheClosingFenceWhenEqualsIsTheLastToken()
        {
            // [픽스 라운드 2 - 부수 문제] `=`가 펜스의 마지막 비공백 내용이면, 값을
            // 원문에서 찾는 루프의 상한이 펜스 끝(offset + cleaned.Length)에 묶여
            // 있어야 한다 - 문서 전체 끝까지로 두면 닫는 ``` 와 그 뒤 산문(여기서는
            // "-9999")까지 값으로 읽는다. 펜스 안에 값 토큰이 없으므로 판정 없이
            // 침묵해야 한다.
            var step = new BatchStepPlan(
                "S16", "통합 검증", new string[0], new[] { "dbo.TSettleMst" },
                new[] { "-9160" }, false, new string[0]);

            var markdown = @"### S16 통합 검증

```sql
DECLARE @v_currentStepId INT =
```
그다음 설명 -9999
";

            var result = Validate(markdown, step);

            Assert.DoesNotContain(result.Errors, e => e.Contains("```"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("-9999"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("숫자가 아닌 값"));
        }
    }
}
