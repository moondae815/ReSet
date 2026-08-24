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

    // 헤딩과 표 사이의 빈 줄이 `|`로 시작하지 않는 줄만 표 행으로 보는 필터
    // (ReadTableInRange) 없이는 그 빈 줄이 1칸짜리 "헤더"가 되어 진짜 헤더 행이
    // 데이터로 밀린다 - DML 범위·SET 대상·지역 변수 표 전부가 이 필터에 기대므로
    // 그 필터를 되돌리면 4개 테스트가 전부 실패한다(리뷰 라운드 1 실측 확인).
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

    // 이 테스트 하나가 서로 다른 버그 두 개를 함께 지킨다(리뷰 라운드 1 실측 확인,
    // 각각 되돌리면 이 테스트만 단독으로 실패한다):
    // 1. IsSeparator가 SplitRow의 선두/말미 빈 칸을 무시하지 않으면 구분선
    //    (`| :--- | ... |`)이 데이터 행으로 들어와 Columns에 ":---"가 섞인다.
    // 2. iColumn을 헤더 이름("컬럼명")이 아니라 고정 인덱스로 집으면 그 앞 칸인
    //    "테이블명"을 컬럼으로 오독한다.
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

    // 실물 코퍼스 실측: INSERT·DELETE "대상 테이블" 제목은 서수 괄호를 쓰지 않는다
    // (`output/Procedures/*/docs/Spec.md` 전체에서 `(삽입 N`·`(삭제 N` 0건, 반면
    // 서수 없는 `### INSERT 대상 테이블:`·`### DELETE 대상 테이블:` 제목은
    // dbo.UP_Util_PG_Client_CMRate_Ins·dbo.UP_UTIL_SETTLE_CANCEL_INS·
    // dbo.UP_UTIL_STAT_PGCOLLECT_INS 등 11개 이상 파일에서 나온다). SetTargets는
    // UPDATE 갱신 절 전용 계약이므로 이런 제목에서는 아무것도 담지 않는다 -
    // "조용히 놓침"이 아니라 "설계상 범위 밖"임을 이 테스트가 못박는다.
    [Fact]
    public void SetTargets_IgnoreInsertHeadingsWithoutOrdinal_MatchingRealCorpusShape()
    {
        const string spec = """
            ### INSERT 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst

            | 테이블명 | 컬럼명 | 원천 표현식 | 설명 |
            | :--- | :--- | :--- | :--- |
            | SETTLE_POQ_DB.dbo.TSettleMst | CLINTCOMM | 1 | 설명 |
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_INSERT_ONLY", spec) })["dbo.UP_UTIL_INSERT_ONLY"];

        Assert.Empty(facts.SetTargets);
    }

    // 위 테스트는 실물 코퍼스 모양(서수 없음)이라 현재 정규식이 우연히도 이미
    // 통과시킨다 - 서수 괄호 자체가 없어서 UPDATE|INSERT|DELETE 대안과 무관하게
    // 매치가 안 되기 때문이다. 그 우연이 아니라 "UPDATE 갱신 절만 잡는다"는
    // 계약을 정규식 자체가 강제하는지는, 서수를 갖춘 가상의 INSERT 제목으로만
    // 가릴 수 있다 - 이 표는 실물에 없지만(위 참고), 있었다면 INSERT|삽입 대안이
    // 남아 있는 한 UPDATE와 다른 표 모양(원천 표현식 매핑이지 SET 아님)을
    // "SET 대상"으로 잘못 삼켰을 것이다.
    [Fact]
    public void SetTargets_IgnoreInsertHeadings_EvenWithAnOrdinalShapedParenthetical()
    {
        const string spec = """
            ### INSERT 대상 테이블: SETTLE_POQ_DB.dbo.TSettleMst (삽입 1 · 원본 DDL 라인 10 · 원문 표기: TSettleMst)

            | 테이블명 | 컬럼명 | 원천 표현식 | 설명 |
            | :--- | :--- | :--- | :--- |
            | SETTLE_POQ_DB.dbo.TSettleMst | CLINTCOMM | 1 | 설명 |
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_INSERT_ONLY", spec) })["dbo.UP_UTIL_INSERT_ONLY"];

        Assert.Empty(facts.SetTargets);
    }
}
