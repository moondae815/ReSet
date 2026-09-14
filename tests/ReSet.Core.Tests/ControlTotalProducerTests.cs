using System;
using System.IO;
using System.Linq;
using System.Text;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [D1 제어합계 생산자 · 2026-09-14 수정] 자기 제외(<c>StepCode &lt;&gt; N'Sxx'</c>)를 <b>그 표를 읽는 문장</b>의 <b>그 표 조건</b>으로만 인정하고,
/// 위반을 그 단계로 귀속한다.
///
/// POQSettleBatch8 6 차 시도: S22 가 한 펜스 안에서 <c>batch.BatchStepJournal</c> 을 <c>StepCode &lt;&gt; N'S22'</c> 로 읽고
/// <c>batch.BatchControlTotal</c> 에는 INSERT 만 했는데, 펜스 단위로 짝지어 거짓 발화했다. 그 검사는 <c>DetailedError</c> 를 내지 않아
/// 귀속되지 못했고, 채점 예산이 소진돼 3~6 차(2 시간 51 분)가 버려졌다.
/// 판독: <c>docs/audit-reports/2026-09-14-L1-귀속실패-측정.md</c>. 픽스처는 코퍼스 바이트 그대로다.
/// </summary>
public sealed class ControlTotalProducerTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "control-total", name));

    private static string Document(params string[] stepSections)
    {
        var body = new StringBuilder();
        body.AppendLine("## 통합 배치 아키텍처 개요\n\n내용.\n");
        body.AppendLine("## Mermaid 기반 통합 흐름도\n\n```mermaid\nflowchart TD\nA[\"시작\"] --> B[\"끝\"]\n```\n");
        body.AppendLine("## 단계별 이행 상세 및 의사코드\n");
        foreach (var section in stepSections) body.AppendLine(section.Trim() + "\n");
        body.AppendLine("## 통합 데이터 정합성 검증 SQL 세트\n\n내용.\n");
        return body.ToString();
    }

    private static string Step(string code, string sql) => $"### {code}. 단계\n\n```sql\n{sql.Trim()}\n```";

    private static DetailedError[] ProducerErrors(string document) =>
        new MechanicalValidator().ValidateConsolidated(document).DetailedErrors
            .Where(d => d.Type == ErrorType.ControlTotalWithoutOtherProducer)
            .ToArray();

    // D1: 이 검사를 만든 실물(Batch4 S16) - 발화하고 그 단계로 귀속된다.
    [Fact]
    public void Batch4_S16_IsReportedAndOwnedByTheStep()
    {
        var error = Assert.Single(ProducerErrors(Document(Fixture("Batch4-S16.md"))));

        Assert.Equal("S16", error.OwnerStepCode);
        Assert.Contains("batch.BatchControlTotal", error.Message);
    }

    // D2: 이번 오탐(Batch8 S22 6 차본) - 자기 제외는 BatchStepJournal 을 읽는 문장의 조건이고 통제표에는 INSERT 만 한다.
    [Fact]
    public void Batch8_S22Attempt6_JournalSelfExclusionBesideAControlTotalInsert_IsSilent()
    {
        Assert.Empty(ProducerErrors(Document(Fixture("Batch8-S22-attempt6.md"))));
    }

    // D3: 양성 대조 짝 - 같은 S22 에 통제표를 자기 제외로 **읽는** 문장 하나를 더하면 발화한다(위 침묵이 「못 읽는다」가 아니다).
    [Fact]
    public void Batch8_S22Attempt6_WithAControlTotalReadExcludingItself_IsReported()
    {
        var original = Fixture("Batch8-S22-attempt6.md");
        var mutated = original.Replace(
            "-- SQL_INSERT_CONTROL_TOTALS\n",
            "SELECT ControlName, ControlValue FROM batch.BatchControlTotal WHERE RunId = @p_runId AND StepCode <> N'S22';\n\n-- SQL_INSERT_CONTROL_TOTALS\n");
        Assert.NotEqual(original, mutated);

        Assert.Equal("S22", Assert.Single(ProducerErrors(Document(mutated))).OwnerStepCode);
    }

    // D4: 같은 문장에서 조인한 **다른 표**의 StepCode 조건은 통제표의 자기 제외가 아니다 - 한정자가 통제표면 발화(짝).
    [Theory]
    [InlineData("J.StepCode <> N'S16'", false)]
    [InlineData("C.StepCode <> N'S16'", true)]
    public void OnlyTheControlTablesOwnStepCodeConditionCounts(string condition, bool expectReported)
    {
        var document = Document(Step("S16", $@"
SELECT C.ControlName, C.ControlValue
  FROM batch.BatchControlTotal AS C
  JOIN batch.BatchStepJournal AS J ON J.RunId = C.RunId
 WHERE C.RunId = @RunId
   AND {condition};

INSERT INTO batch.BatchControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)
VALUES (@RunId, N'S16', N'TSettleMst_Sum_TXAMT', @v_actual, SYSUTCDATETIME());"));

        Assert.Equal(expectReported, ProducerErrors(document).Length == 1);
    }

    // D4b: 통제표가 INSERT 대상이고 SELECT 는 다른 표를 읽는 한 문장(INSERT … SELECT … FROM Journal WHERE StepCode <> …)은 통제표를 읽지 않는다.
    [Fact]
    public void InsertIntoTheControlTableSelectingFromAnotherTable_IsNotAControlTableRead()
    {
        var document = Document(Step("S16", @"
INSERT INTO batch.BatchControlTotal (RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)
SELECT @RunId, N'S16', N'PriorStepCount', COUNT(*), SYSUTCDATETIME()
  FROM batch.BatchStepJournal
 WHERE RunId = @RunId
   AND StepCode <> N'S16';"));

        Assert.Empty(ProducerErrors(document));
    }
}
