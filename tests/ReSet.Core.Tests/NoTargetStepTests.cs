using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [대상 표가 없는 단계] 목차가 `TargetTables` 를 비운 단계는 지금까지 「목차 결함」으로 건너뛰어져 배송본에
/// 「검증 불가」 배너를 남겼다. 실측(2026-09-17): 그 자리는 일곱 판(B11~B18) 전부 S01 하나이고,
/// 일곱 다 본문에 쓰기 키워드·`INTO`·`EXEC`·동적 SQL 이 **하나도 없다**(SQL 펜스 18). 즉 읽기 전용 사전 점검이
/// 자기 설계대로 아무것도 안 쓴다고 선언한 것을 결함처럼 보고해 왔다.
///
/// 그래서 판정을 옮긴다 - **본문이 쓸 수 있을 때만** 결함이다. 「쓸 수 있다」는 보수적으로 넓고(파싱 실패도 포함),
/// 쓰는데 선언이 없으면 종전대로 결함이다(어느 표를 빠뜨렸는지는 T36 이 따로 말한다).
/// 판독: docs/audit-reports/2026-09-17-대상없는-단계-배너-사전선언.md
/// </summary>
public sealed class NoTargetStepTests
{
    private const string Marker = "목차 TargetTables가 비어 있어";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "no-target-step", name));

    private static BatchStepPlan NoTargetStep() => new(
        Code: "S01",
        Name: "입력 및 실행환경 검증",
        LegacyProcedures: Array.Empty<string>(),
        TargetTables: Array.Empty<string>(),
        ErrorCodes: new[] { "-9001" },
        Chunkable: false,
        SchemaTables: Array.Empty<string>());

    private static List<string> PlanDefects(string markdown) =>
        new MechanicalValidator()
            .ValidateBatchStep(markdown, NoTargetStep(), new[] { "dbo.TSettleMst" }, new Dictionary<string, SpecConditions>())
            .PlanDefects
            .Where(d => d.Contains(Marker, StringComparison.Ordinal))
            .ToList();

    // R1 실물: B18 S01 은 아무것도 쓰지 않는다 - 결함이 아니다.
    [Fact]
    public void AReadOnlyStepDeclaringNoTargets_IsNotAPlanDefect()
    {
        Assert.Empty(PlanDefects(Fixture("Batch18-S01-read-only.md")));
    }

    // R1 짝: 그 단계가 하한 검사를 실제로 통과한다(건너뜀이 아니다) - 배너가 붙는 조건이 사라진다.
    [Fact]
    public void ThatStep_PassesTheFloorChecksInsteadOfBeingSkipped()
    {
        var result = new MechanicalValidator().ValidateBatchStep(
            Fixture("Batch18-S01-read-only.md"), NoTargetStep(),
            new[] { "dbo.TSettleMst" }, new Dictionary<string, SpecConditions>());

        Assert.Empty(result.PlanDefects);
        Assert.True(result.RegenerationCanFix || result.Errors.Count == 0,
            "결함이 없는데 재생성으로 고칠 수 없다고 판정되면 오케스트레이터가 이 단계를 건너뛴다");
    }

    // R2: 같은 본문에 실물 쓰기 펜스를 붙이면(자리만 옮겼다) 종전대로 결함이다.
    [Fact]
    public void AStepThatWritesButDeclaresNothing_IsStillAPlanDefect()
    {
        var markdown = Fixture("Batch18-S01-read-only.md") + "\n" + Fixture("Batch18-S02-lock-insert-fence.md");

        var defect = Assert.Single(PlanDefects(markdown));

        Assert.Contains("S01", defect);
    }

    // R3: `EXEC`·동적 SQL 은 무엇을 쓰는지 모른다 - 쓰기로 세어 결함을 유지한다(실물 펜스).
    [Fact]
    public void AStepRunningDynamicSql_IsStillAPlanDefect()
    {
        var markdown = Fixture("Batch18-S01-read-only.md") + "\n" + Fixture("exec-fence.md");

        Assert.Single(PlanDefects(markdown));
    }

    // R4: 파싱되지 않는 펜스는 「모른다」다 - 보수적으로 결함을 유지한다.
    [Fact]
    public void AStepWithAnUnparsableFence_IsStillAPlanDefect()
    {
        var markdown = Fixture("Batch18-S01-read-only.md") + "\n```sql\nUPDATE ;;; SET\n```\n";

        Assert.Single(PlanDefects(markdown));
    }

    // [합성 픽스처임을 밝힌다 - 2026-09-17 리뷰 Important 2] 아래 셋은 실물이 없다. ScriptDom 으로 코퍼스
    // SQL 펜스 **5,514 개**를 전수로 읽어 `SELECT … INTO`·`TRUNCATE`·`EXECUTE AS` 를 세니 **전부 0** 이었다
    // (B11 의 `INTO batch.ReconciliationResult` 는 `INSERT … INTO` 다). 그래도 가지를 남기는 이유는 방향이다 -
    // 없으면 그 모양의 단계가 조용히 통과해 **놓침**이 된다. 실물이 없으므로 최소 SQL 을 직접 썼고, 그 사실을
    // 여기 적는다(리뷰가 세 갈래를 동시에 걷어내도 시험 여섯이 초록이던 것을 잡았다).
    [Theory]
    [InlineData("TRUNCATE TABLE batch.BatchStepJournal;", "TRUNCATE")]
    [InlineData("SELECT RunId INTO batch.RunSnapshot FROM batch.BatchRun;", "SELECT … INTO")]
    [InlineData("EXECUTE AS USER = 'batchWriter';", "EXECUTE AS")]
    public void EachWriteShapeWithoutRealCorpusMaterial_IsStillAPlanDefect(string sql, string shape)
    {
        var markdown = Fixture("Batch18-S01-read-only.md") + "\n```sql\n" + sql + "\n```\n";

        Assert.Single(PlanDefects(markdown));
        Assert.False(string.IsNullOrEmpty(shape));   // 모양 이름은 실패 메시지에만 쓴다
    }

    // [알려진 한계를 못박는다 - 2026-09-17 리뷰 Important 1] 이 판정은 **SQL 펜스만** 본다.
    // 의사코드 펜스 안에서 리포지터리 호출로만 쓰는 단계는 「쓸 수 없다」로 읽혀 조용해진다 - 놓침이다.
    // 코퍼스 실측(리뷰): 펜스 태그는 ```sql 5,487 · ```pseudocode 671 · ```csharp 374 이고 일곱 자리는 전부
    // SQL 펜스를 갖고 있어 이 한계에 걸리지 않았다. 이 시험은 그 한계가 **바뀌면 알려 주는** 자다 -
    // 언젠가 의사코드까지 읽게 되면 이 시험이 빨개지고, 그때 기대를 바꾸는 것이 의도된 절차다.
    [Fact]
    public void KnownLimit_AWriteOnlyInAPseudocodeFence_IsNotSeen()
    {
        var markdown = Fixture("Batch18-S01-read-only.md")
            + "\n```csharp\nrepository.execute(\"INSERT INTO batch.BatchStepJournal (RunId) VALUES (@runId)\");\n```\n";

        Assert.Empty(PlanDefects(markdown));
    }

    // 선언이 있는 단계는 이 축과 무관하다 - 종전과 같다.
    [Fact]
    public void AStepDeclaringTargets_IsUnaffected()
    {
        var step = new BatchStepPlan(
            Code: "S01", Name: "입력 및 실행환경 검증",
            LegacyProcedures: Array.Empty<string>(),
            TargetTables: new[] { "dbo.TSettleMst" },
            ErrorCodes: new[] { "-9001" }, Chunkable: false,
            SchemaTables: Array.Empty<string>());

        var result = new MechanicalValidator().ValidateBatchStep(
            Fixture("Batch18-S01-read-only.md"), step,
            new[] { "dbo.TSettleMst" }, new Dictionary<string, SpecConditions>());

        Assert.DoesNotContain(result.PlanDefects, d => d.Contains(Marker, StringComparison.Ordinal));
    }
}
