using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [발급 전 잠금 쓰기] RunId 를 발급하는 절보다 먼저 도는 절이 제어 계약의 run id 자리(`NOT NULL`)에 쓰면 그 단계는
/// 어떤 실행에서도 값을 채울 수 없다.
///
/// 실물 둘(배송본 13 편 중 둘, 나머지 11 편은 발급 절이나 그 뒤에서 잠금을 잡는다):
/// `POQSettleBatch17` 은 S02 가 `batch.BatchRunLock.OwnerRunId` 를 써야 하는데 발급이 S03 이라 본문 첫 줄이
/// `if (RunId == null) throw -9020` 이 됐다 — 배송된 계획서가 S02 에서 항상 죽는다.
/// `POQSettleBatch16` 은 같은 자리에서 예약값 `CAST(0 AS BIGINT)` 를 지어냈고 Critic 이 S03 에 승계 UPDATE 를 넣게 했다.
/// 판독: docs/audit-reports/2026-09-16-발급전-잠금-계약-사전선언.md
/// </summary>
public sealed class PreRunIdLockWriteTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "prerunid-lock", name));

    private const string Headers =
        "## 통합 배치 아키텍처 개요\n개요.\n\n## Mermaid 기반 통합 흐름도\n```mermaid\nflowchart TD\nA[\"시작\"] --> B[\"끝\"]\n```\n\n";

    /// <summary>단계 절을 목차 순서대로 이어 문서 하나로 만든다 - 절 사이에 상위 헤딩을 넣지 않는다(절 경계는 `### Sxx` 다).</summary>
    private static string Document(params string[] sections) =>
        Headers + "## 단계별 이행 상세 및 의사코드\n\n" + string.Join("\n\n", sections) + "\n";

    private static List<DetailedError> Firings(string markdown) =>
        new MechanicalValidator().ValidateConsolidated(markdown).DetailedErrors
            .Where(e => e.Type == ErrorType.PreRunIdRunIdWrite)
            .ToList();

    // R1: 실물 - 발급(S03) 전 절이 OwnerRunId 를 INSERT·UPDATE 한다.
    [Fact]
    public void AStepWritingTheLockOwnerBeforeIssuance_IsReported()
    {
        var markdown = Document(
            Fixture("Batch17-S01-mentions-lock-table.md"),
            Fixture("Batch17-S02-lock-before-issue.md"),
            Fixture("Batch17-S03-issuer.md"));

        var firing = Assert.Single(Firings(markdown));

        Assert.Equal("S02", firing.OwnerStepCode);
        Assert.Contains("S03", firing.Message);
        Assert.Contains("OwnerRunId", firing.Message);
        Assert.NotNull(firing.Lexemes);
        Assert.Contains(firing.Lexemes!, line => line.Contains("OwnerRunId"));
    }

    // R1 - 게이트가 읽는 문자열 목록에도 실린다(오케스트레이터는 Errors 로 재생성을 태운다).
    [Fact]
    public void TheFiring_AlsoReachesTheErrorList()
    {
        var markdown = Document(
            Fixture("Batch17-S02-lock-before-issue.md"),
            Fixture("Batch17-S03-issuer.md"));

        var result = new MechanicalValidator().ValidateConsolidated(markdown);

        Assert.Contains(result.Errors, e => e.Contains("OwnerRunId") && e.Contains("S03"));
    }

    // R4: 예약값을 지어낸 것도 면제가 아니다 - B16 실물.
    [Fact]
    public void AReservedZeroOwner_IsStillReported()
    {
        var markdown = Document(
            Fixture("Batch16-S02-lock-reserved-zero.md"),
            Fixture("Batch16-S03-issuer.md"));

        var firing = Assert.Single(Firings(markdown));

        Assert.Equal("S02", firing.OwnerStepCode);
    }

    // R2: 발급 뒤에 잠금을 잡는 모양은 침묵한다 - B11 실물.
    [Fact]
    public void ALockAcquiredAfterIssuance_StaysSilent()
    {
        var markdown = Document(
            Fixture("Batch11-S02-issuer.md"),
            Fixture("Batch11-S03-lock-after-issue.md"));

        Assert.Empty(Firings(markdown));
    }

    // R3: 발급 절이 같은 절에서 잠금까지 잡는 모양은 침묵한다 - B12 실물(배송본 11/13 의 모양).
    [Fact]
    public void TheIssuingStepAcquiringTheLockItself_StaysSilent()
    {
        var markdown = Document(Fixture("Batch12-S02-issuer-and-lock.md"));

        Assert.Empty(Firings(markdown));
    }

    // 발급 절을 못 찾으면 무엇이 앞인지 모른다 - 침묵한다.
    [Fact]
    public void ADocumentWithoutARunRowInsert_StaysSilent()
    {
        var markdown = Document(Fixture("Batch17-S02-lock-before-issue.md"));

        Assert.Empty(Firings(markdown));
    }

    // 표 이름을 리터럴로 언급만 한 절(OBJECT_ID 존재 확인)은 쓰기가 아니다 - B17 S01 실물.
    [Fact]
    public void AStepOnlyNamingTheLockTable_StaysSilent()
    {
        var markdown = Document(
            Fixture("Batch17-S01-mentions-lock-table.md"),
            Fixture("Batch17-S03-issuer.md"));

        Assert.Empty(Firings(markdown));
    }

    // WHERE 의 컬럼은 쓰기가 아니다 - B17 S02 의 셋째 UPDATE(SQL_REFRESH_OWN_LOCK)가 그 모양이다.
    // 그 절에는 SET 쓰기도 있어 발화 자체는 나므로, 어휘가 WHERE 줄만 담지 않는지로 가른다.
    [Fact]
    public void ColumnsOnlyInAWhereClause_AreNotWrites()
    {
        var markdown = Document(
            Fixture("Batch17-S02-lock-before-issue.md"),
            Fixture("Batch17-S03-issuer.md"));

        var firing = Assert.Single(Firings(markdown));

        Assert.All(firing.Lexemes!, line => Assert.DoesNotContain("AND OwnerRunId", line));
    }

    // WHERE 에만 있는 컬럼은 쓰기가 아니다 - 그 펜스만 있는 절은 통째로 침묵한다(발화 수로 가른다).
    [Fact]
    public void AStepThatOnlyReadsTheOwnerInAWhereClause_StaysSilent()
    {
        var markdown = Document(
            Fixture("Batch17-S02-refresh-only.md"),
            Fixture("Batch17-S03-issuer.md"));

        Assert.Empty(Firings(markdown));
    }

    // SET 과 WHERE 에 같은 컬럼이 있는 쓰기 - 어휘는 SET 줄만 싣는다(WHERE 줄을 실으면 조건을 고치라는 지목이 된다).
    [Fact]
    public void AWriteThatAlsoFiltersOnTheOwner_PutsOnlyTheSetLineInLexemes()
    {
        var markdown = Document(
            Fixture("Batch16-claim-moved-before-issue.md"),
            Fixture("Batch16-S03-issuer.md"));

        var firing = Assert.Single(Firings(markdown));

        Assert.Contains(firing.Lexemes!, line => line.StartsWith("SET OwnerRunId", StringComparison.Ordinal));
        Assert.DoesNotContain(firing.Lexemes!, line => line.Contains("AND OwnerRunId", StringComparison.Ordinal));
    }

    // 계약이 run id 자리로 정하지 않은 `NOT NULL` 컬럼(LockStatus)만 쓰는 절은 침묵한다.
    // [이 시험이 있는 이유 - 2026-09-16 리뷰 Important 1] 축을 「NOT NULL·비-IDENTITY」 광의로 갈아도
    // 기존 시험 열둘이 전부 초록이었다 - 계약이 축을 말한다는 이 설계의 핵심이 시험 없는 주장이었다.
    [Fact]
    public void AStepWritingOnlyNonRunIdColumnsBeforeIssuance_StaysSilent()
    {
        var markdown = Document(
            Fixture("Batch17-release-moved-before-issue.md"),
            Fixture("Batch17-S03-issuer.md"));

        Assert.Empty(Firings(markdown));
    }

    // 계약이 run id 자리를 스스로 말한다 - 이름으로 짐작하지 않는다(이 저장소가 두 번 실패한 축).
    [Fact]
    public void TheContractNamesItsRunIdColumns()
    {
        var bearing = BatchControlContract.Tables
            .SelectMany(t => t.Columns.Where(c => c.ReferencesRunId).Select(c => $"{t.Name}.{c.Name}"))
            .ToList();

        Assert.Contains("batch.BatchRunLock.OwnerRunId", bearing);
        Assert.Contains("batch.BatchStepJournal.RunId", bearing);
        // 발급 자리 자신은 run id 를 참조하는 것이 아니라 발급한다.
        Assert.DoesNotContain("batch.BatchRun.RunId", bearing);
        Assert.All(bearing, name => Assert.False(
            BatchControlContract.Tables
                .SelectMany(t => t.Columns.Where(c => c.ReferencesRunId && $"{t.Name}.{c.Name}" == name))
                .Single().Nullable,
            $"{name} 이 nullable 이면 발급 전에도 쓸 수 있어 이 검사의 전제가 깨진다"));
    }

    // 계약 문구가 잠금 자리를 말한다 - 검사만 있으면 모델에 합법 모양이 없어 재시도만 태운다.
    [Fact]
    public void ThePromptContractTellsWhereTheLockRowBelongs()
    {
        var table = BatchControlContract.RenderPromptTable();

        Assert.Contains("batch.BatchRunLock", table);
        Assert.Contains("INSERTs the run lock row", table);
        Assert.Contains("never invent a placeholder", table);
    }
}
