using System;
using System.Collections.Generic;
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
    }
}
