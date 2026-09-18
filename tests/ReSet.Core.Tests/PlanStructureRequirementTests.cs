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
/// [목차 요구 대응 — 가족 (나-1)] B21 축 B 감사에서 가장 넓은 가족은 「목차가 건 요구에 대응 절·항목이 없다」였다
/// (12 건 / 9 단계). 원인은 요청 본문 실측으로 확정됐다 — <b>단계 섹션 요청 24 건 중 목차 요구 문구가 실린 것 0,
/// 골격 요청은 3/3</b>. 단계 작성자는 목차 산문을 받을 칸이 없었다.
///
/// 요청 재생 실험(45 호출 $7.33): 대응은 A0 0/9 → 불릿을 실은 팔 전부 9/9. 부작용 (러)「없던 원본 힌트를
/// 제거했다」 거짓 전제 문장이 A1·A2 각 3/9 였고, <b>「레거시 기원이 없는 단계에서는 원본 힌트를 제거·미사용했다고
/// 적지 마라」 한 줄이 0/9 로 껐다</b>(대응 9/9 유지).
///
/// 선언: docs/audit-reports/2026-09-18-목차요구-단계요청-재생-사전선언.md (부록 A 포함)
/// 판독: docs/audit-reports/2026-09-18-목차요구-단계요청-재생-판독.md
/// </summary>
public sealed class PlanStructureRequirementTests
{
    private const string Header = "[Approved Step Requirements";
    private const string Contract = "[Requirement Coverage Contract]";
    private const string Authority = "The authoritative source for original logic is the procedure specification";
    private const string NoLegacyBan = "This step has NO legacy origin.";

    // B21 의 목차 모양. `#### Snn — 이름` 아래 불릿이고 첫 줄은 메타(`- 레거시 기원: 없음`)다.
    private const string B21Shape = @"## 통합 배치 아키텍처 개요

### 실행 전 제어 단계

#### S03 — 실행 계획 저널 초기화

- 레거시 기원: 없음
- S04~S20 실행 계획을 `batch.BatchStepJournal`에 `Pending`으로 등록한다.
- 이미 성공한 단계가 있다면 원본 실행과 현재 실행의 업무일자 및 배포 버전 호환성을 확인한다.

#### S04 — 요율 스냅샷 구축

- 원본: `dbo.UP_Util_PG_Client_CMRate_Ins`
- 검증 항목은 대상별 건수, 자연 키 중복, 업무일자 불일치 및 계약 마스터 미매핑이다.

### 검증 SQL 세트

### V09 — PG 수납통계 대사

- 세 원천별 건수와 금액
";

    // B20 의 목차 모양. 단계 헤딩이 H3 이고 그 아래 H4 소제목이 불릿을 나눠 담는다 -
    // 소제목이 블록을 끊으면 요구가 통째로 사라진다.
    private const string B20Shape = @"### S01 입력 및 실행환경 검증

#### 목적과 검증 항목

- `BusinessYmd`가 정확한 `YYYYMMDD`이며 실제 달력 날짜인지 확인한다.
- 실행 전용 계정에 업무 테이블 DML 권한이 있는지 확인한다.

#### 실행 지침

- 문자열 길이와 숫자 형식을 확인한다.

### S02 실행 등록

- 잠금을 획득한다.
";

    // B5 의 목차 모양. 단계 블록이 산문이라 요구 불릿이 없다.
    private const string ProseShape = @"### S00. 배치 초기화 및 실행 잠금
#### 주요 처리 로직
`batch.BatchRun`에 실행 이력을 만들고 잠금을 획득한다.
";

    // ---------- ① 재료: 목차에서 무엇을 뽑는가 ----------

    [Fact]
    public void Read_TakesTheBulletsUnderEachStepHeadingAndDropsTheMetaLines()
    {
        var byStep = PlanStructureRequirementReader.Read(B21Shape);

        Assert.Equal(new[] { "S03", "S04", "V09" }, byStep.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(
            new[]
            {
                "- S04~S20 실행 계획을 `batch.BatchStepJournal`에 `Pending`으로 등록한다.",
                "- 이미 성공한 단계가 있다면 원본 실행과 현재 실행의 업무일자 및 배포 버전 호환성을 확인한다.",
            },
            byStep["S03"].ToArray());
        // `- 원본: …` 은 요구가 아니라 메타다.
        Assert.Equal(
            new[] { "- 검증 항목은 대상별 건수, 자연 키 중복, 업무일자 불일치 및 계약 마스터 미매핑이다." },
            byStep["S04"].ToArray());
    }

    [Fact]
    public void Read_DoesNotLetANestedSubheadingEndTheStepBlock()
    {
        var byStep = PlanStructureRequirementReader.Read(B20Shape);

        Assert.Equal(3, byStep["S01"].Count);
        Assert.Contains("- 실행 전용 계정에 업무 테이블 DML 권한이 있는지 확인한다.", byStep["S01"]);
        Assert.Contains("- 문자열 길이와 숫자 형식을 확인한다.", byStep["S01"]);
        Assert.Equal(new[] { "- 잠금을 획득한다." }, byStep["S02"].ToArray());
    }

    [Fact]
    public void Read_SkipsStepBlocksThatHaveNoBullets()
    {
        Assert.Empty(PlanStructureRequirementReader.Read(ProseShape));
        Assert.Empty(PlanStructureRequirementReader.Read(null));
        Assert.Empty(PlanStructureRequirementReader.Read("   "));
    }

    // 실물 목차 하나로 못박는다. 정박은 <b>커밋된 픽스처</b>다 - `output/` 아래 목차는
    // 재생성이 덮고, 그것을 전건으로 쓰면 이 시험이 자기 폭발 반경 안에 들어간다
    // (건너뜀이 늘지 않고 조용해진다). 픽스처는 B21 배송본을 <b>바이트 그대로 복사</b>한
    // 것이고 손으로 짓지 않았다 - 지어낸 픽스처는 내 오해를 검사가 확인해 준다.
    [Fact]
    public void Read_ReadsTheShippedB21Structure()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests",
            "Fixtures", "plan-structure", "POQSettleBatch21-PlanStructure.md");

        var byStep = PlanStructureRequirementReader.Read(File.ReadAllText(path));

        // 감사가 짚은 네 자리가 전부 재료로 나온다.
        Assert.Contains("- 이미 성공한 단계가 있다면 원본 실행과 현재 실행의 업무일자 및 배포 버전 호환성을 확인한다.", byStep["S03"]);
        Assert.Contains("- 이후 단계에서 원장이 예기치 않게 변경됐는지 확인할 수 있도록 안정적인 그룹별 제어 합계를 저장한다.", byStep["S12"]);
        Assert.Contains("- 재시작 시 누적 결과의 기존 반영 여부를 먼저 판정한다.", byStep["S13"]);
        Assert.Contains("- 건수, 거래 금액, PG 수수료, 고객 수수료, VAT, 입출금 금액 및 취소 부호를 별도 항목으로 저장한다.", byStep["S19"]);
        Assert.Equal(20, Enumerable.Range(1, 20).Count(i => byStep.ContainsKey($"S{i:D2}")));
    }

    // ---------- ② 프롬프트: 실제로 도는 길 ----------

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Requirements =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["S03"] = new[] { "- 배포 버전 호환성을 확인한다." },
            ["S04"] = new[] { "- 자연 키 중복을 검증한다." },
        };

    private static readonly BatchStepPlan NewStep =
        new("S03", "저널 초기화", Array.Empty<string>(), new[] { "batch.BatchStepJournal" }, new[] { "-9030" }, false, Array.Empty<string>());

    private static readonly BatchStepPlan LegacyStep =
        new("S04", "요율 스냅샷", new[] { "dbo.UP_Util_PG_Client_CMRate_Ins" }, new[] { "dbo.TPGSettleRate" }, new[] { "-9" }, false, Array.Empty<string>());

    private static IReadOnlyList<BatchStepPlan> Steps => new[] { NewStep, LegacyStep };
    private static List<(string FileName, string Content)> Specs => new() { ("dbo.UP_Util_PG_Client_CMRate_Ins", "본문") };

    private sealed record Captured(string System, string User, string? Suffix);

    private static (IAiService Service, Func<Captured> Last) Service()
    {
        Captured? captured = null;
        var client = Substitute.For<IAiClient>();
        client.ProviderName.Returns("OpenAI");
        client.ModelName.Returns("gpt-test");
        client.ChatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = new Captured((string)call[0], (string)call[1], call[4] as string);
                return Task.FromResult(new AiResult { Content = "### S03 저널 초기화" });
            });
        return (new AiService(client, 0.2f), () => captured ?? throw new InvalidOperationException("호출되지 않았습니다."));
    }

    private static async Task<Captured> StepSection(BatchStepPlan step, IReadOnlyDictionary<string, IReadOnlyList<string>>? reqs)
    {
        var (service, last) = Service();
        await service.GenerateBatchStepSectionAsync(
            step, Steps, "공통 규약", Specs, Array.Empty<StepInterface>(), "C#", "Job_Test",
            requirementsByStep: reqs);
        return last();
    }

    [Fact]
    public async Task StepSectionPrompt_CarriesThisStepsRequirements()
    {
        var c = await StepSection(NewStep, Requirements);
        var whole = c.System + c.User + c.Suffix;

        Assert.Contains(Header, whole);
        Assert.Contains("- 배포 버전 호환성을 확인한다.", whole);
    }

    [Fact]
    public async Task StepSectionPrompt_DoesNotCarryAnotherStepsRequirements()
    {
        var c = await StepSection(NewStep, Requirements);
        Assert.DoesNotContain("- 자연 키 중복을 검증한다.", c.System + c.User + c.Suffix);
    }

    [Fact]
    public async Task StepSectionPrompt_CarriesTheCoverageContractWithTheAuthorityOrder()
    {
        var c = await StepSection(NewStep, Requirements);
        var whole = c.System + c.User + c.Suffix;

        Assert.Contains(Contract, whole);
        Assert.Contains(Authority, whole);
    }

    // 금지 한 줄은 「레거시 기원이 없는 단계」에만. 레거시 단계에 실으면 참인 제거 서술까지 막는다.
    [Fact]
    public async Task StepSectionPrompt_ForbidsRemovalClaimsOnlyForStepsWithNoLegacyOrigin()
    {
        Assert.Contains(NoLegacyBan, (await StepSection(NewStep, Requirements)).Suffix);
        Assert.DoesNotContain(NoLegacyBan, (await StepSection(LegacyStep, Requirements)).Suffix ?? "");
    }

    // 재료가 없으면 절 자체가 없다 - 빈 머리글은 「목차가 요구를 안 냈다」는 거짓 전제를 준다.
    [Fact]
    public async Task StepSectionPrompt_WithoutRequirements_HasNoBlockAtAll()
    {
        foreach (var reqs in new IReadOnlyDictionary<string, IReadOnlyList<string>>?[]
                 { null, new Dictionary<string, IReadOnlyList<string>>() })
        {
            var c = await StepSection(NewStep, reqs);
            var whole = c.System + c.User + c.Suffix;
            Assert.DoesNotContain(Header, whole);
            Assert.DoesNotContain(Contract, whole);
            Assert.DoesNotContain(NoLegacyBan, whole);
        }
    }

    // [캐시 불변식] 요구는 단계마다 다르다. 공유 접두사에 실으면 20 단계가 나눠 쓰는 캐시가
    // 통째로 죽는다. 꼬리(volatileUserSuffix)는 이미 단계마다 갈리므로 손해가 0 이다 -
    // 실험도 꼬리에 실어 측정했다(재생 27+18 호출 전량 캐시 적중).
    [Fact]
    public async Task StepSectionPrompt_PutsTheRequirementsInTheVolatileTailNotTheCachedPrefix()
    {
        var with = await StepSection(NewStep, Requirements);
        var without = await StepSection(NewStep, null);

        Assert.Equal(without.User, with.User);
        Assert.Equal(without.System, with.System);
        Assert.Contains(Header, with.Suffix);
        Assert.DoesNotContain(Header, with.User);
    }

    // ---------- ③ 경로마다 ----------

    private const string PlanStructureJson = @"## 목차

#### S03 — 저널 초기화

- 레거시 기원: 없음
- 배포 버전 호환성을 확인한다.

```json
{ ""Steps"": [ { ""Code"": ""S03"", ""Name"": ""저널 초기화"" } ] }
```";

    // 단일 호출 폴백은 목차 전문을 이미 싣는다(불릿이 그 안에 있다). 없는 것은 계약이라
    // 계약만 더한다 - 조항이 한쪽 경로에만 있으면 안 도는 쪽만 보고 있을 수 있다.
    [Fact]
    public async Task SingleCallFallbackPrompt_CarriesTheCoverageContract()
    {
        var (service, last) = Service();
        await service.GenerateConsolidatedBatchPlanAsync(PlanStructureJson, Specs, "C#", "Job_Test");
        var whole = last().System + last().User + last().Suffix;

        Assert.Contains(Contract, whole);
        Assert.Contains(Authority, whole);
    }

    // 골격은 단계 본문을 쓰지 않는다(공통 규약과 V 절만 쓴다). 계약을 여기 실으면 20 단계가
    // 나눠 쓰는 골격 접두사만 늘어난다 - 의도적으로 싣지 않으며, 그 의도를 잠근다.
    [Fact]
    public async Task SkeletonPrompt_DoesNotCarryTheStepRequirementContract()
    {
        var (service, last) = Service();
        await service.GenerateBatchPlanSkeletonAsync(Steps, PlanStructureJson, Specs, "C#", "Job_Test");
        Assert.DoesNotContain(Header, last().System + last().User + last().Suffix);
    }

    // ---------- ④ 배선 (발화를 재는 자는 배선이 끊겨도 초록이다) ----------

    [Fact]
    public void OrchestratorReadsTheRequirementsFromThePlanStructureAndThreadsThemToTheCall()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(), "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs"));

        // 목차에서 한 번 읽고,
        Assert.Single(Regex.Matches(source, @"PlanStructureRequirementReader\.Read\(\s*planStructure\s*\)"));
        // 재시도 헬퍼로 넘기고, 헬퍼가 AI 호출로 넘긴다 - 자리 둘이다.
        Assert.Equal(2, Regex.Matches(source, @"requirementsByStep:\s*requirementsByStep").Count);
        // 빈 것은 null 로 내려보낸다 - 「요구 없음」과 「요구 실림」을 호출 기록에서 가른다.
        Assert.Single(Regex.Matches(source, @"parsedRequirements\.Count > 0 \? parsedRequirements : null"));
    }
}
