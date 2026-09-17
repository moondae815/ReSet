using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [실행 순서의 오라클은 목차다] 두 검사(RunId 게이트 · 발급 전 잠금)는 「무엇이 발급 절보다 앞인가」를 문서 순서로 짐작했다.
/// B19 배송본의 첫 `### Sxx` 헤딩이 Mermaid 흐름도 안의 `### S13～S16 요약 복합 트랜잭션` 이라 S13 이 발급 절(S02) 앞으로
/// 읽혔고, 첫 L1 회차 오류 11 중 아홉이 그 오탐이었다 — 수리 회차 하나를 태우고 모델이 멀쩡한 게이트에서 S13 을 지웠다.
///
/// 마크다운만으로 순서를 고치는 규칙 둘은 코퍼스 15 편을 다 못 맞췄다(「단계 상세 H2 안만」은 B7 을 깨고, 「범위 헤딩 제외」는
/// B14 의 `### S11 통합 범위` 를 못 거른다). 그래서 목차를 받으면 목차 순서를 쓴다.
/// 판독: docs/audit-reports/2026-09-17-단계순서-오라클-목차-사전선언.md
/// </summary>
public sealed class StepOrderOracleTests
{
    private static string Fixture(string directory, string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", directory, name));

    private static BatchStepPlan Plan(string code) =>
        new(code, code, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>());

    private static IReadOnlyList<BatchStepPlan> Toc(params string[] codes) => codes.Select(Plan).ToList();

    /// <summary>필수 H2 넷 사이에 흐름도 절 조각과 단계 절들을 둔다 - 흐름도 조각이 단계 절보다 먼저 나오는 것이 실물 모양이다.</summary>
    private static string Document(string? flowPiece, IEnumerable<string> stepSections)
    {
        var body = new StringBuilder();
        body.AppendLine("## 통합 배치 아키텍처 개요\n\n내용.\n");
        body.AppendLine("## Mermaid 기반 통합 흐름도\n");
        if (flowPiece != null) body.AppendLine(flowPiece.Trim() + "\n");
        body.AppendLine("## 단계별 이행 상세 및 의사코드\n");
        foreach (var section in stepSections) body.AppendLine(section.Trim() + "\n");
        body.AppendLine("## 통합 데이터 정합성 검증 SQL 세트\n\n내용.\n");
        return body.ToString();
    }

    private static DetailedError[] Errors(string document, ErrorType type, IReadOnlyList<BatchStepPlan>? toc) =>
        new MechanicalValidator().ValidateConsolidated(document, toc).DetailedErrors
            .Where(d => d.Type == type)
            .ToArray();

    /// <summary>
    /// B19 첫 회차의 모양 - 단계 절 셋은 **실행 로그의 첫 회차 응답 원문**이다(배송본은 수리가 게이트에서 S13 을 지워 오탐이 안 난다).
    /// 흐름도 조각은 배송본의 것이다(첫 회차 골격 원문은 로그 응답 본문에서 절 경계를 확정할 수 없어 배송본에서 오렸다).
    /// </summary>
    private static string Batch19FirstAttemptShape() =>
        Document(
            Fixture("step-order", "Batch19-flow-range-heading.md"),
            new[]
            {
                Fixture("step-order", "Batch19-attempt1-S01.md"),
                Fixture("step-order", "Batch19-attempt1-S02-issuer.md"),
                Fixture("step-order", "Batch19-attempt1-S16-gate.md"),
            });

    private static readonly IReadOnlyList<BatchStepPlan> Batch19Toc =
        Toc("S01", "S02", "S03", "S04", "S05", "S06", "S07", "S08", "S09", "S10", "S11", "S12", "S13", "S14", "S15", "S16");

    // R1 재현: 목차 없이(종전 문서 순서) 흐름도의 `### S13～S16` 헤딩이 S13 을 발급 절 앞으로 올려 게이트를 고발한다.
    [Fact]
    public void Batch19_WithoutTheToc_TheRangeHeadingMakesTheGateMisfire()
    {
        var errors = Errors(Batch19FirstAttemptShape(), ErrorType.GateRequiresStepBeforeRunId, toc: null);

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Message.Contains("S13", StringComparison.Ordinal));
    }

    // R1 소멸: 목차를 넘기면 S13 은 발급 절(S02) 뒤다 - 오탐이 사라진다.
    [Fact]
    public void Batch19_WithTheToc_TheGateIsSilent()
    {
        Assert.Empty(Errors(Batch19FirstAttemptShape(), ErrorType.GateRequiresStepBeforeRunId, Batch19Toc));
    }

    // R2 진짜는 남는다: B11 의 S21 게이트는 목차로 봐도 S01(발급 전)을 요구한다.
    [Fact]
    public void Batch11_ARealGate_StillFiresWithTheToc()
    {
        var document = Document(null, new[]
        {
            Fixture("run-gate", "Batch11-S01.md"), Fixture("run-gate", "Batch11-S02.md"), Fixture("run-gate", "Batch11-S21.md"),
        });

        var error = Assert.Single(Errors(document, ErrorType.GateRequiresStepBeforeRunId, Toc("S01", "S02", "S21")));
        Assert.Equal("S21", error.OwnerStepCode);
    }

    // R2 진짜는 남는다: B17 의 발급 전 잠금(S02 가 잠금, S03 이 발급)은 목차로 봐도 발화한다.
    [Fact]
    public void Batch17_ARealPreRunIdLockWrite_StillFiresWithTheToc()
    {
        var document = Document(null, new[]
        {
            Fixture("prerunid-lock", "Batch17-S02-lock-before-issue.md"), Fixture("prerunid-lock", "Batch17-S03-issuer.md"),
        });

        var error = Assert.Single(Errors(document, ErrorType.PreRunIdRunIdWrite, Toc("S02", "S03")));
        Assert.Equal("S02", error.OwnerStepCode);
    }

    // 발급 전 잠금 검사도 목차를 쓴다 - 문서에서 잠금 절(S03)이 발급 절(S02) 앞에 적혀 있어도 목차가 S02 → S03 이면 합법이다.
    // 재료는 B11 실물 두 절이고 **적힌 순서만** 바꿨다(발급 뒤 잠금이 정상인 판이다).
    [Fact]
    public void Batch11_LockSectionWrittenFirstButRunAfterIssuance_IsSilentWithTheToc()
    {
        var document = Document(null, new[]
        {
            Fixture("prerunid-lock", "Batch11-S03-lock-after-issue.md"), Fixture("prerunid-lock", "Batch11-S02-issuer.md"),
        });

        Assert.NotEmpty(Errors(document, ErrorType.PreRunIdRunIdWrite, toc: null));             // 종전: 문서 순서라 고발한다
        Assert.Empty(Errors(document, ErrorType.PreRunIdRunIdWrite, Toc("S01", "S02", "S03")));  // 목차: 발급 뒤라 조용하다
    }

    // R3 폴백: 목차를 모르면 종전과 같다 - 순서가 맞는 문서에서는 목차 유무가 결과를 바꾸지 않는다.
    [Fact]
    public void WhenDocumentOrderAgreesWithTheToc_TheTocChangesNothing()
    {
        var document = Document(null, new[]
        {
            Fixture("run-gate", "Batch11-S01.md"), Fixture("run-gate", "Batch11-S02.md"), Fixture("run-gate", "Batch11-S21.md"),
        });

        Assert.Equal(
            Errors(document, ErrorType.GateRequiresStepBeforeRunId, toc: null).Length,
            Errors(document, ErrorType.GateRequiresStepBeforeRunId, Toc("S01", "S02", "S21")).Length);
    }

    // [리뷰 Important 1] 발급 절 탐색은 **같은 코드 절의 본문을 합쳐서** 본다. 발급 절과 같은 코드의 장식 헤딩(흐름도 조각)이
    // 진짜 발급 절보다 먼저 나오면, 합치지 않고 첫 절만 보는 구현은 장식 본문(mermaid)만 보고 발급 절을 못 찾아 진짜 잠금 결함을 놓친다.
    // 재료: B19 흐름도 조각의 **헤딩 코드만** S03 으로 바꿨고(본문은 실물), 그 뒤에 B17 실물 S02(잠금)·S03(발급)을 둔다.
    [Fact]
    public void ADecorativeHeadingWithTheIssuersCode_DoesNotHideTheIssuer()
    {
        var flow = Fixture("step-order", "Batch19-flow-range-heading.md");
        var decorated = flow.Replace("### S13～S16", "### S03～S04");
        Assert.NotEqual(flow, decorated);

        var document = Document(decorated, new[]
        {
            Fixture("prerunid-lock", "Batch17-S02-lock-before-issue.md"), Fixture("prerunid-lock", "Batch17-S03-issuer.md"),
        });

        var error = Assert.Single(Errors(document, ErrorType.PreRunIdRunIdWrite, Toc("S01", "S02", "S03")));
        Assert.Equal("S02", error.OwnerStepCode);
    }

    // [리뷰 Minor 1] 빈 목차는 목차가 없는 것과 같다 - 파서가 빈 단계 목록을 null 로 돌려주지만(도달 불가) 불변식을 잠근다.
    [Fact]
    public void AnEmptyToc_BehavesExactlyLikeNoToc()
    {
        var document = Document(null, new[]
        {
            Fixture("prerunid-lock", "Batch11-S03-lock-after-issue.md"), Fixture("prerunid-lock", "Batch11-S02-issuer.md"),
        });

        Assert.Equal(
            Errors(document, ErrorType.PreRunIdRunIdWrite, toc: null).Length,
            Errors(document, ErrorType.PreRunIdRunIdWrite, new List<BatchStepPlan>()).Length);
    }

    // [리뷰 미확인 → 재현] 게이트 검사의 코드 목록은 절 코드와 목차 코드의 합집합이라 BETWEEN 이 절 없는 목차 코드까지 넓힌다.
    // 그래도 「발급 전」은 목차 순서로만 정해지므로, 넓어진 코드가 발급 **뒤**면 고발되지 않는다. 발급 **앞**의 절 없는 코드는
    // 고발되는데 그것은 오탐이 아니다 - 발급 전 단계는 이 실행의 저널 행을 가질 수 없으니 게이트가 요구하면 늘 실패한다.
    [Fact]
    public void ABetweenGateWidenedByTocCodes_OnlyReportsStepsBeforeTheIssuer()
    {
        const string gate = """
            ### S12. 게시 전 확인

            ```sql
            SELECT COUNT(*)
              FROM batch.BatchCheckpoint
             WHERE RunId = @p_runId
               AND StepCode BETWEEN N'S03' AND N'S11'
               AND CheckpointStatus = N'Succeeded';
            ```
            """;
        var document = Document(null, new[]
        {
            Fixture("run-gate", "Batch11-S01.md"), Fixture("run-gate", "Batch11-S02.md"), gate,
        });

        // 목차: S01 · S02(발급) · S03~S11(절 없음) · S12 — BETWEEN S03..S11 은 전부 발급 뒤라 고발할 것이 없다.
        var toc = Toc("S01", "S02", "S03", "S04", "S05", "S06", "S07", "S08", "S09", "S10", "S11", "S12");
        Assert.Empty(Errors(document, ErrorType.GateRequiresStepBeforeRunId, toc));

        // 양성 대조 - 위 침묵이 「게이트로 인식조차 안 됐다」가 아님을 보인다: 절 없는 S03 을 목차에서 발급 앞으로 옮기면 고발된다.
        var tocWithS03First = Toc("S01", "S03", "S02", "S04", "S05", "S06", "S07", "S08", "S09", "S10", "S11", "S12");
        var error = Assert.Single(Errors(document, ErrorType.GateRequiresStepBeforeRunId, tocWithS03First));
        Assert.Contains("S03", error.Message, StringComparison.Ordinal);
    }
}
