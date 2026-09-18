using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [가드와 트랜잭션의 순서] 원본은 사전 가드에 걸리면 트랜잭션을 <b>아예 열지 않는다</b>. B21 산출물은 열고 롤백하고
/// (S10·S11 🟡), 공통 계약이 <c>begin</c> 을 <c>TRY</c> 안에 두라고 적는데 20 단계 중 9(레거시 8)가 앞에서 연다.
///
/// 재료는 이미 프롬프트에 둘 다 있었다 — 명세서의 「트랜잭션 경계 (기계 확정)」 표(첫 <c>BEGIN TRANSACTION</c> 줄)와
/// <c>[Original Guard Conditions]</c> 표(가드 줄). <b>없던 것은 두 표를 줄 번호로 맞춰 보라는 조항</b>이고,
/// Few-Shot 예시 구간에 <c>TRY</c> 가 0 건이었다.
///
/// 요청 재생 실험(30 호출 $3.93): <b>P1</b> 가드 표에 순서 칸을 더하니 양성 S10 이 B0 3/3 결함 → 3/3 고침이고
/// 음성 S05(가드 둘 — 25 줄 밖 · 39 줄 안)는 39 줄을 안에 그대로 둬 <b>오탐 0</b> 이었다. <b>P2</b> Few-Shot 에
/// <c>TRY</c> 골격 예시를 넣으니 결함 6/9 → 판정 가능 8/8 이 <c>TRY</c> 안이었다.
///
/// 선언·판독: docs/audit-reports/2026-09-18-가드-트랜잭션-순서-{사전선언,판독}.md
/// </summary>
public sealed class GuardTransactionOrderTests
{
    private const string OutsideHeader = "Original first BEGIN TRAN (DDL line)";
    private const string OutsideCol = "Guard is outside the transaction";
    private const string Clause = "open this step's transaction only";
    private const string Rule5Recheck = "does NOT make it skippable";

    // 가드가 첫 BEGIN TRAN 보다 앞인 원본(= 원본은 트랜잭션 없이 가드하고 그대로 반환한다).
    private const string GuardBeforeTranDdl = @"CREATE PROCEDURE dbo.UP_Outside @pi_strYMD CHAR(8) AS
BEGIN
    IF EXISTS (SELECT PLTID FROM TSettleMst WHERE YMD = @pi_strYMD AND OutState = 1)
    BEGIN
        SET @po_intRetVal = -9
        RETURN
    END

    BEGIN TRANSACTION
    DELETE FROM dbo.TTarget WHERE YMD = @pi_strYMD
    COMMIT TRANSACTION
END";

    // 가드가 첫 BEGIN TRAN 보다 뒤인 원본(= 원본이 트랜잭션 안에서 검사한다 — 빼면 오탐이다).
    private const string GuardAfterTranDdl = @"CREATE PROCEDURE dbo.UP_Inside @pi_strYMD CHAR(8) AS
BEGIN
    BEGIN TRANSACTION
    IF EXISTS (SELECT PLTID FROM TSettleMst WHERE YMD = @pi_strYMD AND OutState = 2)
    BEGIN
        ROLLBACK TRANSACTION
        SET @po_intRetVal = -3
        RETURN
    END
    COMMIT TRANSACTION
END";

    // 트랜잭션이 아예 없는 원본 — 판정할 기준이 없으므로 「모름」이어야 한다.
    private const string NoTranDdl = @"CREATE PROCEDURE dbo.UP_NoTran @pi_strYMD CHAR(8) AS
BEGIN
    IF EXISTS (SELECT PLTID FROM TSettleMst WHERE YMD = @pi_strYMD)
        SET @po_intRetVal = -9
END";

    private static IReadOnlyList<StepInterface> Build(params (string Step, string Proc, string Ddl)[] rows) =>
        StepInterfaceFacts.Build(
            rows.Select(r => new BatchStepPlan(r.Step, r.Step + " 이름", new[] { r.Proc }, new[] { "dbo.TTarget" }, new[] { "-9" }, false, Array.Empty<string>())).ToList(),
            rows.ToDictionary(r => r.Proc, r => (IReadOnlyList<string>)new[] { "@pi_strYMD char(8)" }, StringComparer.OrdinalIgnoreCase),
            rows.ToDictionary(r => r.Proc, r => r.Ddl, StringComparer.OrdinalIgnoreCase));

    // ---------- ① 재료: 원본 DDL 에서 첫 BEGIN TRAN 을 뽑아 가드마다 붙인다 ----------

    [Fact]
    public void Build_AttachesTheOriginalsFirstBeginTranLineToEachGuard()
    {
        var outside = Assert.Single(Assert.Single(Build(("S10", "dbo.UP_Outside", GuardBeforeTranDdl))).Guards!);
        var inside = Assert.Single(Assert.Single(Build(("S11", "dbo.UP_Inside", GuardAfterTranDdl))).Guards!);

        Assert.Equal(9, outside.FirstBeginTranLine);
        Assert.True(outside.Line < outside.FirstBeginTranLine);
        Assert.Equal(3, inside.FirstBeginTranLine);
        Assert.True(inside.Line > inside.FirstBeginTranLine);
    }

    // 트랜잭션이 없는 원본은 「모름」이다 - 0 이나 int.MaxValue 를 넣으면 표가 거짓을 적는다.
    [Fact]
    public void Build_LeavesTheLineUnknownWhenTheOriginalOpensNoTransaction()
    {
        var guard = Assert.Single(Assert.Single(Build(("S09", "dbo.UP_NoTran", NoTranDdl))).Guards!);
        Assert.Null(guard.FirstBeginTranLine);
    }

    // ---------- ② 표: 칸 둘과 조항 ----------

    [Fact]
    public void RenderGuardTable_SaysWhetherEachGuardRanOutsideTheTransaction()
    {
        var table = StepInterfaceFacts.RenderGuardTable(Build(
            ("S10", "dbo.UP_Outside", GuardBeforeTranDdl),
            ("S11", "dbo.UP_Inside", GuardAfterTranDdl),
            ("S09", "dbo.UP_NoTran", NoTranDdl)));

        Assert.Contains(OutsideHeader, table);
        Assert.Contains(OutsideCol, table);
        var byStep = table.Split('\n').Where(l => l.StartsWith("| S")).ToDictionary(l => l.Split('|')[1].Trim());
        Assert.EndsWith("| 9 | YES |", byStep["S10"].TrimEnd());
        Assert.EndsWith("| 3 | NO |", byStep["S11"].TrimEnd());
        // 「모름」은 YES/NO 를 쓰지 않는다.
        Assert.EndsWith("| - | UNKNOWN |", byStep["S09"].TrimEnd());
    }

    // 가드가 하나도 없으면 표 자체가 없다 - 종전 동작이고, 칸 추가가 그것을 깨면 안 된다.
    [Fact]
    public void RenderGuardTable_WithoutGuards_IsStillEmpty() =>
        Assert.Equal(string.Empty, StepInterfaceFacts.RenderGuardTable(Array.Empty<StepInterface>()));

    // ---------- ③ 프롬프트: 경로마다 ----------

    private static (IAiService Service, Func<string> Whole) Service()
    {
        string? system = null, user = null, suffix = null;
        var client = Substitute.For<IAiClient>();
        client.ProviderName.Returns("OpenAI");
        client.ModelName.Returns("gpt-test");
        client.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                system = (string)call[0]; user = (string)call[1]; suffix = call[4] as string;
                return Task.FromResult(new AiResult { Content = "### S10 이름" });
            });
        return (new AiService(client, 0.2f), () => (system ?? "") + (user ?? "") + (suffix ?? ""));
    }

    private static List<(string FileName, string Content)> Specs => new() { ("dbo.UP_Outside", "본문") };
    private static IReadOnlyList<BatchStepPlan> Steps => new[]
    {
        new BatchStepPlan("S10", "Extra 정산", new[] { "dbo.UP_Outside" }, new[] { "dbo.TTarget" }, new[] { "-9" }, false, Array.Empty<string>()),
    };
    private const string PlanStructure = @"## 목차
```json
{ ""Steps"": [ { ""Code"": ""S10"", ""Name"": ""Extra 정산"", ""LegacyProcedures"": [""dbo.UP_Outside""] } ] }
```";

    private static void AssertCarriesTheOrderClause(string whole)
    {
        Assert.Contains(OutsideHeader, whole);
        Assert.Contains(Clause, whole);
        // 규칙 5 재확인이 같이 실려야 한다 - 밖으로 빼는 것이 「건너뛸 수 있다」로 읽히면 안 된다.
        Assert.Contains(Rule5Recheck, whole);
    }

    [Fact]
    public async Task StepSectionPrompt_CarriesTheGuardOrderColumnAndClause()
    {
        var (service, whole) = Service();
        await service.GenerateBatchStepSectionAsync(Steps[0], Steps, "공통 규약", Specs,
            Build(("S10", "dbo.UP_Outside", GuardBeforeTranDdl)), "C#", "Job_Test");
        AssertCarriesTheOrderClause(whole());
    }

    [Fact]
    public async Task SkeletonPrompt_CarriesTheGuardOrderColumnAndClause()
    {
        var (service, whole) = Service();
        await service.GenerateBatchPlanSkeletonAsync(Steps, PlanStructure, Specs, "C#", "Job_Test",
            stepInterfaces: Build(("S10", "dbo.UP_Outside", GuardBeforeTranDdl)));
        AssertCarriesTheOrderClause(whole());
    }

    [Fact]
    public async Task SingleCallFallbackPrompt_CarriesTheGuardOrderColumnAndClause()
    {
        var (service, whole) = Service();
        await service.GenerateConsolidatedBatchPlanAsync(PlanStructure, Specs, "C#", "Job_Test",
            stepInterfaces: Build(("S10", "dbo.UP_Outside", GuardBeforeTranDdl)));
        AssertCarriesTheOrderClause(whole());
    }

    // ---------- ④ P2: Few-Shot 에 TRY 골격 ----------

    // 예시 구간에 TRY 가 0 건이었다. 생성물은 예시를 베끼고 프롬프트 주석을 베끼지 않는다.
    [Fact]
    public async Task Prompt_ShowsAGuardThenTransactionInsideTryExample()
    {
        var (service, whole) = Service();
        await service.GenerateBatchStepSectionAsync(Steps[0], Steps, "공통 규약", Specs,
            Array.Empty<StepInterface>(), "C#", "Job_Test");
        var text = whole();

        Assert.Contains("[Few-Shot Examples", text);
        // 가드가 먼저, 그 뒤 TRY, 그 안에서 beginTransaction. 자리는 **이 예시 블록 안에서** 찾는다 -
        // `beginTransaction()` 의 첫 등장으로 재면 기존 청킹 예시를 집어 순서가 뒤집혀 보인다(처음에 그렇게 틀렸다).
        var guard = text.IndexOf("SQL_GUARD_ALREADY_SETTLED", StringComparison.Ordinal);
        Assert.True(guard > 0, "가드 예시가 없다");
        var tryAt = text.IndexOf("\nTRY:", guard, StringComparison.Ordinal);
        var begin = text.IndexOf("beginTransaction()", tryAt < 0 ? guard : tryAt, StringComparison.Ordinal);
        Assert.True(tryAt > guard && begin > tryAt,
            $"가드({guard}) < TRY({tryAt}) < beginTransaction({begin}) 순서여야 한다");
        Assert.Contains("no transaction was ever opened", text);
        // 이 예시가 TRY 를 처음 들여온다 - 전에는 예시 구간에 TRY 가 0 건이었다.
        Assert.Single(Regex.Matches(text, @"(?m)^TRY:$"));
    }

    // ---------- ⑤ 배선 (발화를 재는 자는 배선이 끊겨도 초록이다) ----------

    [Fact]
    public void BuildComputesTheLineFromTheProductExtractorNotAHandWrittenScan()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "StepInterfaceFacts.cs"));
        Assert.Single(Regex.Matches(source, @"TransactionBoundaryExtractor\.Extract\("));
        // 원본 DDL 이 없으면 계산하지 않는다(가드 자체도 그 조건 아래에서만 만든다).
        Assert.DoesNotContain("BEGIN TRANSACTION\"", source);
    }
}
