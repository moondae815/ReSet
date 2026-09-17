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
