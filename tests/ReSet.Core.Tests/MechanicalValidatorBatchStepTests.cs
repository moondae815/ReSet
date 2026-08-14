using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class MechanicalValidatorBatchStepTests
    {
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

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

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

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

            Assert.DoesNotContain(result.Errors, e => e.Contains("POQSettleCheckpoint", StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateBatchStep_ShouldAcceptTablesThatAreInTheCatalog()
        {
            var markdown = Section("UPDATE dbo.TSettleMst SET OutState = 9;");

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

            Assert.DoesNotContain(result.Errors, e => e.Contains("존재하지", StringComparison.Ordinal));
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

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

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

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

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

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

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

            var result = new MechanicalValidator().ValidateBatchStep(markdown, Step("dbo.TSettleMst"), Catalog);

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
                markdown, Step("dbo.TSettleMst"), Array.Empty<string>());

            Assert.DoesNotContain(result.Errors, e => e.Contains("dbo.TSettleSummary", StringComparison.Ordinal));
        }
    }
}
