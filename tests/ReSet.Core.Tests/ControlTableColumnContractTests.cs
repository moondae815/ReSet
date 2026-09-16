using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [계약 밖 batch 표의 컬럼 계약] 문서가 스스로 만든 표를 스스로 다른 컬럼으로 읽으면 배포하면 컬럼 없음 오류다.
/// 실물 `POQSettleBatch13`: S18 이 `batch.BatchReconciliation` 을 `ReconciliationName`·`IsMatched` 로 만들고 검증 세트 V15 가
/// `CheckCode`·`ResultStatus`·`DifferenceCount` 를 읽는다(다섯 중 하나도 정의에 없다). Critic 1 차가 잡았으나 채택이 그 수정을 버렸다.
/// 판독: docs/audit-reports/2026-09-16-제어표-컬럼계약-검사-사전선언.md
/// </summary>
public sealed class ControlTableColumnContractTests
{
    private const string Marker = "표 정의에 없는 컬럼";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "control-table-columns", name));

    private const string Headers =
        "## 통합 배치 아키텍처 개요\n개요.\n\n## Mermaid 기반 통합 흐름도\n```mermaid\nflowchart TD\nA[\"시작\"] --> B[\"끝\"]\n```\n\n";

    private static string Document(string stepBody, string verificationBody) =>
        Headers +
        "## 단계별 이행 상세 및 의사코드\n### S18 정합성 검증\n\n" + stepBody + "\n\n" +
        "## 통합 데이터 정합성 검증 SQL 세트\n\n" + verificationBody + "\n";

    private static List<string> Errors(string markdown) =>
        new MechanicalValidator().ValidateConsolidated(markdown).Errors.Where(e => e.Contains(Marker)).ToList();

    // C1: 실물 - 정의와 조회가 서로 다른 컬럼 집합이다.
    [Fact]
    public void AQueryReadingColumnsTheDocumentNeverDefined_IsReported()
    {
        var errors = Errors(Document(Fixture("Batch13-create-table.sql.md"), Fixture("Batch13-verification-query.sql.md")));

        var error = Assert.Single(errors);
        Assert.Contains("batch.BatchReconciliation", error);
        foreach (var column in new[] { "CheckCode", "CheckName", "ResultStatus", "DifferenceCount", "DetailMessage" })
        {
            Assert.Contains(column, error);
        }
        Assert.Contains("ReconciliationName", error); // 문서가 정의한 컬럼을 함께 적는다
    }

    // 정의와 조회가 맞으면 침묵한다.
    [Fact]
    public void AQueryMatchingTheDefinition_StaysSilent() =>
        Assert.Empty(Errors(Document(
            Fixture("Batch13-create-table.sql.md"),
            "```sql\nSELECT ReconciliationName, IsMatched FROM batch.BatchReconciliation WHERE RunId = @p_runId;\n```")));

    // C2: 계약 표는 이 검사의 관할이 아니다 - 컬럼은 계약이 정하고 어휘 검사가 본다.
    [Fact]
    public void ContractTables_AreNotJudgedHere() =>
        Assert.Empty(Errors(Document(
            "```sql\nCREATE TABLE batch.BatchControlTotal (RunId BIGINT NOT NULL);\n```",
            "```sql\nSELECT ControlName, ControlValue FROM batch.BatchControlTotal WHERE RunId = @p_runId;\n```")));

    // C3: 정의가 없으면 판정하지 않는다(부트스트랩이 만드는 표일 수 있다 - 코퍼스에 여덟 자리).
    [Fact]
    public void WithoutACreateTable_NothingIsReported() =>
        Assert.Empty(Errors(Document(
            "```sql\nINSERT INTO batch.BatchPublishLog (RunId, PublishedAtUtc) VALUES (@p_runId, SYSUTCDATETIME());\n```",
            "```sql\nSELECT RunId, PublishedAtUtc FROM batch.BatchPublishLog WHERE RunId = @p_runId;\n```")));

    // C4: 두 테이블을 조인한 질의의 비한정 컬럼은 소속을 말할 수 없다 - 침묵한다.
    [Fact]
    public void ColumnsOfAMultiTableQuery_StaySilent() =>
        Assert.Empty(Errors(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL, IsMatched BIT NOT NULL);\n```",
            "```sql\nSELECT r.RunId, j.StepStatus FROM batch.BatchReconciliation r JOIN batch.BatchStepJournal j ON j.RunId = r.RunId;\n```")));

    // C4(가르는 입력): 조인 질의의 **비한정** 컬럼은 소속을 말할 수 없다 - 정의에 없어도 침묵해야 한다.
    // (앞 시험은 컬럼을 모두 한정해 이 가지를 못 갈랐다 - 되돌림 m4 가 그것을 드러냈다.)
    [Fact]
    public void AnUnqualifiedColumnInAJoinQuery_StaysSilent() =>
        Assert.Empty(Errors(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL, IsMatched BIT NOT NULL);\n```",
            "```sql\nSELECT RunId, StepStatus FROM batch.BatchReconciliation r JOIN batch.BatchStepJournal j ON j.RunId = r.RunId;\n```")));

    // 한정된 참조는 조인 질의에서도 본다 - 소속이 분명하다.
    [Fact]
    public void AQualifiedColumnInAJoinQuery_IsStillJudged()
    {
        var errors = Errors(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL, IsMatched BIT NOT NULL);\n```",
            "```sql\nSELECT r.ResultStatus, j.StepStatus FROM batch.BatchReconciliation r JOIN batch.BatchStepJournal j ON j.RunId = r.RunId;\n```"));

        Assert.Contains("ResultStatus", Assert.Single(errors));
    }

    // INSERT 컬럼 목록도 본다 - 조회만 보면 쓰기 쪽 분열이 조용하다.
    [Fact]
    public void AnInsertColumnListIsJudgedToo()
    {
        var errors = Errors(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL, IsMatched BIT NOT NULL);\n```",
            "```sql\nINSERT INTO batch.BatchReconciliation (RunId, ResultStatus) VALUES (@p_runId, N'PASS');\n```"));

        Assert.Contains("ResultStatus", Assert.Single(errors));
    }

    // C5: ALTER TABLE ADD 로 더한 컬럼도 정의다 - 합집합으로 본다.
    [Fact]
    public void ColumnsAddedByAlterTable_CountAsDefined() =>
        Assert.Empty(Errors(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL);\nALTER TABLE batch.BatchReconciliation ADD ResultStatus NVARCHAR(20) NULL;\n```",
            "```sql\nSELECT RunId, ResultStatus FROM batch.BatchReconciliation WHERE RunId = @p_runId;\n```")));

    // [최종 리뷰 C1] 서브질의의 컬럼은 남의 표 것이다 - 종전에는 이 절 하위 전부를 훑어 유효 SQL 을 고발했다.
    [Theory]
    [InlineData("SELECT RunId, IsMatched FROM batch.BatchReconciliation WHERE NOT EXISTS (SELECT 1 FROM dbo.SettleLog WHERE LogSeq = 1);")]
    [InlineData("SELECT RunId FROM batch.BatchReconciliation WHERE RunId IN (SELECT ParentRunId FROM dbo.SettleLog);")]
    [InlineData("SELECT RunId, (SELECT MAX(LogSeq) FROM dbo.SettleLog) AS LastSeq FROM batch.BatchReconciliation;")]
    public void ColumnsInsideASubquery_AreNotThisTables(string query) =>
        Assert.Empty(Errors(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL, IsMatched BIT NOT NULL);\n```",
            "```sql\n" + query + "\n```")));

    // [최종 리뷰 C1] ORDER BY 가 가리키는 SELECT 별칭은 컬럼이 아니다.
    [Fact]
    public void ASelectAliasUsedInOrderBy_IsNotAColumn() =>
        Assert.Empty(Errors(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL, IsMatched BIT NOT NULL);\n```",
            "```sql\nSELECT IsMatched AS MatchFlag FROM batch.BatchReconciliation ORDER BY MatchFlag;\n```")));

    // [최종 리뷰 I3] 파생 테이블이 섞이면 비한정 컬럼의 소속을 말할 수 없다 - `unresolved` 가 그것을 막는 유일한 가지다.
    [Fact]
    public void AnUnqualifiedColumnBesideADerivedTable_StaysSilent() =>
        Assert.Empty(Errors(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL, IsMatched BIT NOT NULL);\n```",
            "```sql\nSELECT RunId, Total FROM batch.BatchReconciliation CROSS JOIN (SELECT SUM(1) AS Total FROM dbo.SettleLog) x;\n```")));

    // [최종 리뷰 I3] 정의된 표와 정의 없는 표가 함께 있는 문서 - 정의 없는 쪽을 조회 대상에서 거르지 않으면 검사가 통째로 죽는다.
    [Fact]
    public void ADocumentMixingDefinedAndUndefinedTables_ReportsOnlyTheDefinedOne()
    {
        var result = new MechanicalValidator().ValidateConsolidated(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL, IsMatched BIT NOT NULL);\nINSERT INTO batch.BatchPublishLog (RunId, PublishedAtUtc) VALUES (@p_runId, SYSUTCDATETIME());\n```",
            "```sql\nSELECT RunId, ResultStatus FROM batch.BatchReconciliation WHERE RunId = @p_runId;\n```"));

        Assert.Contains("ResultStatus", Assert.Single(result.Errors, e => e.Contains(Marker)));
        Assert.DoesNotContain(result.Errors, e => e.Contains("검증기 자체 오류"));
    }

    // C6: 귀속 - 참조가 단계 절 안이면 그 단계, 밖이면 원문 줄을 싣는다.
    [Fact]
    public void AttributionNamesTheStepOrTheLine()
    {
        var outside = new MechanicalValidator().ValidateConsolidated(Document(
            Fixture("Batch13-create-table.sql.md"), Fixture("Batch13-verification-query.sql.md")));
        var outsideError = Assert.Single(outside.DetailedErrors, e => e.Message.Contains(Marker));
        Assert.Null(outsideError.OwnerStepCode);
        Assert.Contains(outsideError.Lexemes ?? new List<string>(), line => line.Contains("ResultStatus"));

        var inside = new MechanicalValidator().ValidateConsolidated(Document(
            Fixture("Batch13-create-table.sql.md") + "\n\n" + Fixture("Batch13-verification-query.sql.md"),
            "```sql\nSELECT 1;\n```"));
        var insideError = Assert.Single(inside.DetailedErrors, e => e.Message.Contains(Marker));
        Assert.Equal("S18", insideError.OwnerStepCode);
        // [최종 리뷰 I2] owner 가 있어도 줄 어휘를 싣는다 - 메시지 백틱만 남기면 귀속 기본 경로가 「문서가 정의한 컬럼」 목록을
        // 어휘로 삼아 멀쩡한 단계를 연다(작성 계약 9).
        Assert.Contains(insideError.Lexemes ?? new List<string>(), line => line.Contains("ResultStatus"));
    }

    // [최종 리뷰 I2] 미정의 참조가 단계 절과 절 밖에 함께 있으면 자리마다 따로 열려야 한다 - 하나로 묶으면 오케스트레이터가
    // OwnerStepCode 자리만 고치고 절 밖 자리는 영영 안 열린 채 재시도를 태운다.
    [Fact]
    public void ReferencesInAStepAndOutsideIt_AreReportedSeparately()
    {
        var result = new MechanicalValidator().ValidateConsolidated(Document(
            "```sql\nCREATE TABLE batch.BatchReconciliation (RunId BIGINT NOT NULL, IsMatched BIT NOT NULL);\nINSERT INTO batch.BatchReconciliation (RunId, ResultStatus) VALUES (@p_runId, N'PASS');\n```",
            "```sql\nSELECT RunId, DifferenceCount FROM batch.BatchReconciliation WHERE RunId = @p_runId;\n```"));

        var errors = result.DetailedErrors.Where(e => e.Message.Contains(Marker)).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.OwnerStepCode == "S18" && e.Message.Contains("ResultStatus"));
        Assert.Contains(errors, e => e.OwnerStepCode == null && e.Message.Contains("DifferenceCount"));
    }
}
