using System;
using System.Collections.Generic;
using System.Linq;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Core.Services.Clients;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [N6 · 확인 요청으로 옮김] 원본의 한 트랜잭션이 여러 단계로 갈린 사실은 목차·원본 DDL 로 찾을 수 있지만, 원자성이 실제로
/// 깨졌는지는 섹션이 단계를 넘어 한 트랜잭션을 공유하는가에 달려 있다 — 배송 다섯 판 5/5 가 공유로 보존했고 종전 단계 배너는
/// 5/5 거짓이었다. 판정은 Critic 에 넘기고 이 재료는 사실만 낸다.
/// 판독: docs/audit-reports/2026-09-16-트랜잭션분할-Critic확인항목-사전선언.md
///
/// 판정 규칙(단일 구간 · 구간 안 호출 · 피호출자 자기 트랜잭션 없음 · DML 있음 · 다른 단계)은 옮기기만 했고, 아래 일곱은
/// 종전 <c>MechanicalValidatorTests</c> 의 N6 시험을 그대로 옮긴 것이다(픽스처는 실물 모양).
/// </summary>
public sealed class TransactionSpanSplitFactsTests
{
    private const string SummaryCallerDdl = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary @p CHAR(8)
AS
BEGIN
    BEGIN TRAN
    DELETE FROM dbo.TSettleByTX WHERE YMD = @p;
    INSERT INTO dbo.TSettleByTX (YMD, Amt) SELECT YMD, SUM(TxAmt) FROM dbo.TSettleMst WHERE YMD = @p GROUP BY YMD;
    EXEC dbo.UP_Util_Settle_Summary_AcqManual @p;
    COMMIT TRAN
END";

    // 호출자가 트랜잭션을 열기 **전에** 부른다 - 원자성이 애초에 없었다.
    private const string SummaryCallerDdlWithExecOutsideTransaction = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary @p CHAR(8)
AS
BEGIN
    EXEC dbo.UP_Util_Settle_Summary_AcqManual @p;
    BEGIN TRAN
    DELETE FROM dbo.TSettleByTX WHERE YMD = @p;
    COMMIT TRAN
END";

    // 명시적 트랜잭션이 없는 호출자 - 귀속할 구간이 없다.
    private const string SummaryCallerDdlWithoutTransaction = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary @p CHAR(8)
AS
BEGIN
    DELETE FROM dbo.TSettleByTX WHERE YMD = @p;
    EXEC dbo.UP_Util_Settle_Summary_AcqManual @p;
END";

    private const string AcqManualCalleeDdl = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary_AcqManual @p CHAR(8)
AS
BEGIN
    DELETE FROM dbo.TSettleByOUT WHERE YMD = @p;
    INSERT INTO dbo.TSettleByOUT (YMD, Amt) SELECT YMD, SUM(TxAmt) FROM dbo.TSettleMst WHERE YMD = @p GROUP BY YMD;
END";

    private const string AcqManualCalleeDdlWithOwnTransaction = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary_AcqManual @p CHAR(8)
AS
BEGIN
    BEGIN TRAN
    DELETE FROM dbo.TSettleByOUT WHERE YMD = @p;
    COMMIT TRAN
END";

    private static BatchStepPlan SummaryStep(string code) => new(
        code, $"{code} 단계", new[] { "dbo.UP_Util_Settle_Summary" },
        new[] { "SETTLE_POQ_DB.dbo.TSettleByTX" }, Array.Empty<string>(), false, Array.Empty<string>());

    private static BatchStepPlan CalleeStep(string code) => new(
        code, $"{code} 단계", new[] { "dbo.UP_Util_Settle_Summary_AcqManual" },
        new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" }, Array.Empty<string>(), false, Array.Empty<string>());

    private static IReadOnlyDictionary<string, string> DdlMap(
        string caller = SummaryCallerDdl, string callee = AcqManualCalleeDdl) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UP_Util_Settle_Summary"] = caller,
            ["UP_Util_Settle_Summary_AcqManual"] = callee,
        };

    private static IReadOnlyList<TransactionSpanSplit> Find(
        IReadOnlyList<BatchStepPlan>? steps, IReadOnlyDictionary<string, string>? ddl) =>
        TransactionSpanSplitFacts.Find(steps, ddl);

    // X1: 피호출자가 다른 단계로 승격됐다 - 사실 한 줄.
    [Fact]
    public void ACalleeHoistedToItsOwnStep_IsFound()
    {
        var splits = Find(new[] { SummaryStep("S11"), CalleeStep("S12") }, DdlMap());

        var split = Assert.Single(splits);
        Assert.Equal("S11", split.CallerStepCode);
        Assert.Equal("S12", split.CalleeStepCode);
        Assert.Equal("UP_Util_Settle_Summary", split.CallerProcedure);
        Assert.Equal("UP_Util_Settle_Summary_AcqManual", split.CalleeProcedure);
        Assert.True(split.SpanFrom < split.SpanTo);
    }

    // 피호출자가 원래 자기 트랜잭션을 가졌다면 갈라도 원자성이 안 바뀐다. 코퍼스 14 편 중 12 편이 이 갈래다.
    [Fact]
    public void ACalleeWithItsOwnTransaction_IsNotFound() =>
        Assert.Empty(Find(
            new[] { SummaryStep("S11"), CalleeStep("S12") },
            DdlMap(callee: AcqManualCalleeDdlWithOwnTransaction)));

    // 같은 단계가 둘 다 맡으면 보존된 것이다(실물 B14 가 이 모양이다).
    [Fact]
    public void ACalleeInTheSameStep_IsNotFound()
    {
        var merged = new BatchStepPlan(
            "S11", "S11 단계",
            new[] { "dbo.UP_Util_Settle_Summary", "dbo.UP_Util_Settle_Summary_AcqManual" },
            new[] { "SETTLE_POQ_DB.dbo.TSettleByTX" }, Array.Empty<string>(), false, Array.Empty<string>());

        Assert.Empty(Find(new[] { merged }, DdlMap()));
    }

    // 트랜잭션 밖에서 부르던 것은 원자성이 애초에 없었다.
    [Fact]
    public void AnExecOutsideTheTransaction_IsNotFound() =>
        Assert.Empty(Find(
            new[] { SummaryStep("S11"), CalleeStep("S12") },
            DdlMap(caller: SummaryCallerDdlWithExecOutsideTransaction)));

    // 귀속할 구간이 없으면 내지 않는다(작성 계약 7).
    [Fact]
    public void ACallerWithoutAnExplicitTransaction_IsNotFound() =>
        Assert.Empty(Find(
            new[] { SummaryStep("S11"), CalleeStep("S12") },
            DdlMap(caller: SummaryCallerDdlWithoutTransaction)));

    // 재료가 없으면 빈 목록이다(던지지 않는다).
    [Fact]
    public void WithoutTheOriginalDdl_NothingIsFound() =>
        Assert.Empty(Find(new[] { SummaryStep("S11"), CalleeStep("S12") }, null));

    // 목차가 없으면 「다른 단계인가」를 판정할 수 없다.
    [Fact]
    public void WithoutSteps_NothingIsFound() => Assert.Empty(Find(null, DdlMap()));

    // X2: 단계 하한 검사는 이 축을 더 이상 들지 않는다 - 배너가 사라진다.
    [Fact]
    public void TheStepFloorCheckNoLongerReportsIt()
    {
        var result = new MechanicalValidator().ValidateBatchStep(
            "### S11 단계\n\n```sql\nDELETE FROM dbo.TSettleByTX WHERE YMD = @p;\n```\n",
            SummaryStep("S11"), Array.Empty<string>(), new Dictionary<string, SpecConditions>(),
            null, null, null, new[] { SummaryStep("S11"), CalleeStep("S12") }, null, null, DdlMap());

        Assert.DoesNotContain(result.Errors, e => e.Contains("트랜잭션이 단계로 갈렸습니다"));
        Assert.DoesNotContain(result.PlanDefects, e => e.Contains("트랜잭션"));
    }

    // 확인 요청 문장은 결함 단정이 아니다 - 공유 트랜잭션이면 보고하지 말라고 못박는다.
    [Fact]
    public void TheConfirmationItemAsksRatherThanAsserts()
    {
        var item = Assert.Single(TransactionSpanSplitFacts.ConfirmationItems(
            Find(new[] { SummaryStep("S11"), CalleeStep("S12") }, DdlMap())));

        Assert.Contains("CONFIRM", item);
        Assert.Contains("S11", item);
        Assert.Contains("S12", item);
        Assert.Contains("do NOT report it", item);
    }

    // 최종 리뷰 Important 2: 같은 호출자 SP 를 여러 단계가 나눠 맡으면 같은 트랜잭션을 여러 번 묻지 않는다
    // (실물 B8 은 S15~S18 이 같은 SP 를 물어 항목 8 개였다). 한 항목에 단계 코드를 나열한다.
    [Fact]
    public void SplitsOfTheSameProcedurePairBecomeOneConfirmationItem()
    {
        var splits = Find(
            new[] { SummaryStep("S11"), SummaryStep("S13"), CalleeStep("S12") }, DdlMap());
        Assert.Equal(2, splits.Count);

        var item = Assert.Single(TransactionSpanSplitFacts.ConfirmationItems(splits));

        Assert.Contains("S11, S13", item);
        Assert.Contains("S12", item);
    }

    // 최종 리뷰 Minor 4 - 침묵 셋. 다만 되돌림으로 재 보니 <b>결과를 만드는 가지가 하나뿐</b>이다:
    // DML 0 면제는 되돌리면 빨개지고(m7), 구간 유효성(spanTo <= spanFrom)과 자기호출 면제는 되돌려도 초록이다
    // (m8·m9) — 구간 밖 호출 범위 검사와 「피호출자가 자기 트랜잭션을 가졌다」 면제가 먼저 거르기 때문이다.
    // 그 둘은 main 에서 물려받은 방어 가지이고, 아래 두 시험은 가지가 아니라 <b>결과</b>(그 입력에서 침묵한다)를 못박는다.
    [Fact]
    public void ACalleeWithoutDml_IsNotFound()
    {
        // 쓰기가 없으면 잃을 원자성이 없다. `SELECT` 는 DmlScopeExtractor 가 세므로 쓰기 없는 실물 모양은
        // 변수 대입뿐인 절차다(로그·반환만 하는 래퍼).
        const string calleeWithoutDml = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary_AcqManual @p CHAR(8), @po_intRetVal INT OUTPUT
AS
BEGIN
    SET @po_intRetVal = 0;
END";

        Assert.Empty(Find(new[] { SummaryStep("S11"), CalleeStep("S12") }, DdlMap(callee: calleeWithoutDml)));
    }

    [Fact]
    public void ACommitBeforeTheBegin_IsNotFound()
    {
        // COMMIT 이 BEGIN 앞에 오면 구간이 성립하지 않는다 - 「그 안」을 말할 수 없다.
        const string callerWithCommitFirst = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary @p CHAR(8)
AS
BEGIN
    COMMIT TRAN
    EXEC dbo.UP_Util_Settle_Summary_AcqManual @p;
    BEGIN TRAN
END";

        Assert.Empty(Find(
            new[] { SummaryStep("S11"), CalleeStep("S12") },
            DdlMap(caller: callerWithCommitFirst)));
    }

    [Fact]
    public void ARecursiveSelfCall_IsNotFound()
    {
        // 자기 자신을 부르는 것은 「다른 단계로 갈렸다」가 아니다.
        const string selfCallingCaller = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary @p CHAR(8)
AS
BEGIN
    BEGIN TRAN
    DELETE FROM dbo.TSettleByTX WHERE YMD = @p;
    EXEC dbo.UP_Util_Settle_Summary @p;
    COMMIT TRAN
END";

        Assert.Empty(Find(
            new[] { SummaryStep("S11"), SummaryStep("S12") },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_Util_Settle_Summary"] = selfCallingCaller,
            }));
    }

    // 최종 리뷰 Important 1: 확인 항목은 후치에만 실려야 한다 - 접두사로 올라가면 Critic 호출 전량이 캐시
    // 미스인데 요청 본문만 보는 시험은 초록이다(합쳐 보내므로). 클라이언트 인자로 두 조각을 갈라 본다.
    [Fact]
    public async System.Threading.Tasks.Task TheConfirmationBlockNeverEntersTheCachePrefix()
    {
        var specs = new List<(string FileName, string Content)> { ("dbo.USP_Test1", "명세서 내용") };
        var client = Substitute.For<IAiClient>();
        client.ProviderName.Returns("OpenAI");
        client.ModelName.Returns("gpt-4o");
        client.ChatAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new AiResult { Content = "{\"HasDefects\": false}" });
        IAiService service = new AiService(client, 0.2f);
        var items = TransactionSpanSplitFacts.ConfirmationItems(
            Find(new[] { SummaryStep("S11"), CalleeStep("S12") }, DdlMap()));

        await service.ReviewConsolidatedPlanAsync(specs, "## 통합 배치 아키텍처 개요", "Test_Job", confirmations: items);

        await client.Received(1).ChatAsync(
            Arg.Any<string>(),
            Arg.Is<string>(stable => !stable.Contains("Confirm These")
                                                 && !stable.Contains("UP_Util_Settle_Summary_AcqManual")),
            Arg.Any<float>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(suffix => suffix != null && suffix.Contains("Confirm These")),
            Arg.Any<System.Threading.CancellationToken>());
    }

    // X3: 확인 요청 항목은 Critic 프롬프트 후치에만 실린다(없으면 절 자체가 없다).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async System.Threading.Tasks.Task TheCriticPromptCarriesTheConfirmationBlockOnlyWhenThereAreItems(bool withItems)
    {
        var specs = new List<(string FileName, string Content)> { ("dbo.USP_Test1", "## 개요\n내용1") };
        var handler = new MockHttpMessageHandler("{\"choices\":[{\"message\":{\"content\":\"{\\\"HasDefects\\\": false}\"}}]}");
        var client = new OpenAiClient(new System.Net.Http.HttpClient(handler), "test_key", "https://api.openai.com/v1", "gpt-4o");
        IAiService service = new AiService(client, 0.2f);
        var items = withItems
            ? TransactionSpanSplitFacts.ConfirmationItems(Find(new[] { SummaryStep("S11"), CalleeStep("S12") }, DdlMap()))
            : Array.Empty<string>();

        await service.ReviewConsolidatedPlanAsync(specs, "## 통합 배치 아키텍처 개요", "Test_Job", confirmations: items);

        if (withItems)
        {
            Assert.Contains("Confirm These", handler.LastRequestBody);
            Assert.Contains("UP_Util_Settle_Summary_AcqManual", handler.LastRequestBody);
        }
        else
        {
            Assert.DoesNotContain("Confirm These", handler.LastRequestBody);
        }
    }

    // X4: 오케스트레이터가 이 재료를 Critic 호출에 넘긴다(소스 잠금 — 주석은 지우고 본다).
    [Fact]
    public void TheOrchestratorPassesTheFactsToTheCritic()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));
        var code = System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*", string.Empty);

        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"TransactionSpanSplitFacts\.Find\(\s*currentSteps,\s*ddlByProcedure\s*\)"), code);
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"ReviewConsolidatedPlanAsync\([^;]*confirmations:\s*transactionSpanConfirmations\.Count > 0 \? transactionSpanConfirmations : null", System.Text.RegularExpressions.RegexOptions.Singleline), code);
    }
}
