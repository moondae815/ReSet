using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class SpecStatementFactsExtractorTests
{
    // COMM_UPD 명세서의 실물 모양을 그대로 오려 왔다. 열 순서에 기대지 않고
    // 헤더 이름으로 찾는지, `(없음)`·`—`를 빈 목록으로 읽는지를 함께 본다.
    private const string Spec = """
        ### 지역 변수 및 시스템 값

        | 명칭 | 데이터 타입 또는 구분 | 사용 위치 | 관계 |
        | :--- | :--- | :--- | :--- |
        | `@v_valIncVat` | `DECIMAL(2,1)` | UPDATE 13 | 값 `1.1`로 선언됩니다. |
        | `@@ERROR` | SQL Server 시스템 값 | UPDATE 1부터 15 | 오류 여부를 검사합니다. |

        ## CRUD 분석

        ### UPDATE 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst (갱신 1 · 원본 DDL 라인 30 · 원문 표기: TSettleMst)

        | 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
        | :--- | :--- | :--- | :--- |
        | SETTLE_POQ_DB.dbo.TSettleMst | CLINTCOMM | CAST(B.TXAMT AS INT) | 설명 |
        | SETTLE_POQ_DB.dbo.TSettleMst | CLVT | dbo.UF_GET_ROUND4VAT(1) | 설명 |

        ### DML 범위 (기계 확정 — 수정 금지)

        | 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼(조인 결합 포함 · 대상 한정 아님) | 기준일 파라미터 적용(최상위 WHERE 기준) | 조인 키 | GROUP BY | ORDER BY |
        | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
        | UPDATE 1 | 30 | TSettleMst | PLTID, YMD, USESTATE | 예 | PLTID | — | — |
        | UPDATE 3 | 122 | TSettleMst | YMD, USESTATE, PLTID | 예 | (없음) | — | — |
        """;

    private static SpecStatementFacts Extract() =>
        SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_SETTLE_COMM_UPD", Spec) })["dbo.UP_UTIL_SETTLE_COMM_UPD"];

    [Fact]
    public void DmlRows_AreReadWithOrdinalAndColumns()
    {
        var rows = Extract().DmlRows;

        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal("UPDATE", first.Kind);
        Assert.Equal(1, first.Ordinal);
        Assert.Equal(30, first.SourceLine);
        Assert.Equal("TSettleMst", first.TargetTable);
        Assert.Equal(new[] { "PLTID", "YMD", "USESTATE" }, first.PredicateColumns);
        Assert.Equal(new[] { "PLTID" }, first.JoinKeys);
        Assert.Empty(first.GroupBy);
    }

    [Fact]
    public void NoneAndDashCells_BecomeEmptyLists()
    {
        var third = Extract().DmlRows.Single(r => r.Ordinal == 3);

        Assert.Empty(third.JoinKeys);     // "(없음)"
        Assert.Empty(third.OrderBy);      // "—"
    }

    [Fact]
    public void SetTargets_AreReadPerUpdateSection()
    {
        var target = Assert.Single(Extract().SetTargets);

        Assert.Equal(1, target.Ordinal);
        Assert.Equal("TSettleMst", target.TargetTable);
        Assert.Equal(new[] { "CLINTCOMM", "CLVT" }, target.Columns);
    }

    [Fact]
    public void SystemValues_AreMarkedAndNotTreatedAsLocalVariables()
    {
        var variables = Extract().LocalVariables;

        var local = Assert.Single(variables, v => v.Name == "@v_valIncVat");
        Assert.False(local.IsSystemValue);
        Assert.Equal("DECIMAL(2,1)", local.TypeOrKind);

        var system = Assert.Single(variables, v => v.Name == "@@ERROR");
        Assert.True(system.IsSystemValue);
    }
}
