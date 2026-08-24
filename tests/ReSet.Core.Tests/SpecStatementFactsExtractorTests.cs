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
            new[] { ("dbo.UP_UTIL_SETTLE_COMM_UPD", Spec) })["UP_UTIL_SETTLE_COMM_UPD"];

    // Task 17 C3 - 실측: `step.LegacyProcedures` 314개 중 134개(43%)가 스키마
    // 접두사 없는 이름이다(예: `UP_UTIL_SETTLE_EXCEPTION_PROC`). 그런데 명세서의
    // FileName은 언제나 `"{Schema}.{Name}"`이다(`ReSet.Cli/Program.cs:772-774`).
    // 추출기가 FileName 원문을 그대로 키로 쓰면 접두사 없는 이름은 영원히 못
    // 찾는다 - `CheckMissingConditionColumns`(`MechanicalValidator.cs:1514`)가
    // 이미 `BareObjectName(legacyProcedure)`로 조회하는 것과 같은 규약을
    // 이 추출기도 따라야 한다.
    [Fact]
    public void Extract_KeysByBareObjectName_SoSchemaLessLegacyProcedureNamesCanLookItUp()
    {
        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_SETTLE_COMM_UPD", Spec) });

        Assert.True(facts.ContainsKey("UP_UTIL_SETTLE_COMM_UPD"));
        Assert.False(facts.ContainsKey("dbo.UP_UTIL_SETTLE_COMM_UPD"));
    }

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
            new[] { ("dbo.UP_UTIL_INSERT_ONLY", spec) })["UP_UTIL_INSERT_ONLY"];

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
            new[] { ("dbo.UP_UTIL_INSERT_ONLY", spec) })["UP_UTIL_INSERT_ONLY"];

        Assert.Empty(facts.SetTargets);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 축 B 검사 D 픽스 라운드 1+2 - 지역/내부 변수 표 헤딩·타입 칸·시스템 값
    // 마커가 코퍼스에서 갈린다.
    //
    // [조사 범위 - 라운드 2, `grep -rn "^###.*변수" output/Procedures/*/docs/Spec.md
    // output/Functions/*/docs/Spec.md output/External/*/*/*/docs/Spec.md`]
    // 라운드 1은 `^#.*지역\s*변수` 패턴만 훑어 "지역 변수" 계열만 찾았고 "이 네
    // 가지가 전부다"라고 잘못 적었다(리뷰 Important로 지적됨) - "내부 변수" 계열은
    // 그 패턴에 아예 걸리지 않아 놓쳤다. 라운드 2는 "변수"가 들어간 `###` 헤딩
    // 전체를 훑었고, 그 결과 이 여섯 가지가 전부다:
    //   1. `### 지역 변수 및 시스템 값`(COMM_UPD·EXPECT_PROC) - 헤더 "데이터 타입
    //      또는 구분"(COMM_UPD) / "데이터 타입"(EXPECT_PROC), 시스템 값 마커
    //      "SQL Server 시스템 값"(COMM_UPD) / "시스템 정수 값"(EXPECT_PROC)
    //   2. `### 지역 변수 및 시스템 상태값`(AcqManual) - 헤더 "데이터 타입 또는
    //      종류", 시스템 값 마커 "시스템 상태값"
    //   3. `### 지역 변수와 컬럼 매핑`(PROC_ETC, S14의 원천) - 헤더 "데이터 타입"
    //      (구분 칸 없음), 시스템 값 행 자체가 없다
    //   4. `### 내부 변수`(INS_EXTRA, output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA
    //      /docs/Spec.md:93) - 헤더 "데이터 타입"(구분 칸 없음), 시스템 값 행 없음
    //   5. `### 내부 변수와 컬럼 관계`(SUMMARY_ETC,
    //      output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/docs/Spec.md:58) -
    //      헤더 "데이터 타입", 시스템 값 행 없음
    // Functions·External의 Spec.md는 "지역 변수"·"내부 변수"를 산문으로만
    // 언급할 뿐 `###` 표 절 자체가 없다(같은 grep으로 확인, 0건).
    //
    // [알려진 한계 - 이번 라운드에서 손대지 않음]
    //   6. `output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md:75`도
    //      같은 "내부 변수 명칭" 표 모양을 담지만, 그 표 앞에 `###`(또는 `##`)
    //      전용 헤딩이 아예 없다 - `## 파라미터 목록`의 매개변수 표 바로 다음 줄에
    //      빈 줄 하나만 두고 곧장 이어진다. 헤딩 문자열이 없으니 이 절의 시작을
    //      앵커할 수 없고, 절 경계 없이 표를 찾으면 `## 파라미터 목록` 표까지
    //      "지역/내부 변수 표"로 잘못 삼킬 위험이 있다(그 표의 첫 칸은 "매개변수
    //      명칭"이라 우연히 "명칭"을 포함해 iName 탐색에도 걸린다). 그래서 이
    //      파일은 SpecStatementFactsExtractor가 LocalVariables를 못 읽는 상태로
    //      남는다 - MechanicalValidator.CheckSpecLocalVariablesDeclared는 재료가
    //      없으면 침묵하므로 거짓 오류는 나지 않지만, 검사 D가 이 SP에는 여전히
    //      비활성이다. 헤딩 없는 절의 경계를 표 모양(헤더 칸 이름)만으로 판별하는
    //      별도 설계가 있어야 다음 라운드에서 닫을 수 있다.
    // ─────────────────────────────────────────────────────────────────────

    // PROC_ETC(S14 원천)의 실물 헤딩·표 모양을 그대로 오려 왔다. 헤딩 문자열이
    // 다르고("지역 변수와 컬럼 매핑"), 타입 헤더가 "데이터 타입"뿐이다(구분 칸 없음).
    [Fact]
    public void LocalVariables_RecognizeProcEtcHeadingAndTypeOnlyHeader()
    {
        const string spec = """
            ### 지역 변수와 컬럼 매핑

            | 변수 명칭 | 데이터 타입 | 초기값 또는 원천 | 연계 컬럼 및 사용 관계 |
            | :--- | :--- | :--- | :--- |
            | `@v_intCLTotal` | `MONEY` | `SUM(TSettleMst.CLTotal)` | 기존 행의 `TSettleMiss.CLSettleAmt`에 누적합니다. |
            | `@v_intIssueType` | `TINYINT` | 상수 `15` | 기존 행 조회 조건에 사용합니다. |

            ## CRUD 분석
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_SETTLE_PROC_ETC", spec) })["UP_UTIL_SETTLE_PROC_ETC"];

        var money = Assert.Single(facts.LocalVariables, v => v.Name == "@v_intCLTotal");
        Assert.Equal("MONEY", money.TypeOrKind);
        Assert.False(money.IsSystemValue);
    }

    // AcqManual의 실물 헤딩·표 모양. 헤딩이 "시스템 상태값"으로 끝나고, 타입
    // 헤더는 "데이터 타입 또는 종류"(원래 매칭 대상 "구분"이 아니라 "종류")이며,
    // 시스템 값 행의 구분 문구는 "시스템 상태값"이다("SQL Server 시스템 값"과 다르다).
    [Fact]
    public void LocalVariables_RecognizeAcqManualHeadingAndSystemStateMarker()
    {
        const string spec = """
            ### 지역 변수 및 시스템 상태값

            | 명칭 | 데이터 타입 또는 종류 | 원천 | 사용 관계 |
            | :--- | :--- | :--- | :--- |
            | @v_strOutYMD | varchar(8) | 커서의 `TSettleMst.OutYMD` | 삭제 및 삽입의 `OutYMD` 조건값입니다. |
            | @@FETCH_STATUS | 시스템 상태값 | `FETCH NEXT` 수행 결과 | 커서 반복 종료 판정에 사용합니다. |

            ## CRUD 분석
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_Util_Settle_Summary_AcqManual", spec) })["UP_Util_Settle_Summary_AcqManual"];

        var local = Assert.Single(facts.LocalVariables, v => v.Name == "@v_strOutYMD");
        Assert.Equal("varchar(8)", local.TypeOrKind);
        Assert.False(local.IsSystemValue);

        var system = Assert.Single(facts.LocalVariables, v => v.Name == "@@FETCH_STATUS");
        Assert.True(system.IsSystemValue);
    }

    // EXPECT_PROC의 실물 헤딩·표 모양. 헤딩은 COMM_UPD와 같지만("지역 변수 및
    // 시스템 값") 타입 헤더가 "데이터 타입"뿐이고(구분 칸 없음), 시스템 값
    // 구분 문구도 "시스템 정수 값"이라 COMM_UPD의 "SQL Server 시스템 값"과 다르다.
    [Fact]
    public void LocalVariables_RecognizeExpectProcTypeOnlyHeaderAndSystemIntegerMarker()
    {
        const string spec = """
            ### 지역 변수 및 시스템 값

            | 명칭 | 데이터 타입 | 원천 값 | 사용 관계 |
            | :--- | :--- | :--- | :--- |
            | @v_PLCardSettlePeriodPG | varchar(200) | `'PLCard,SamSungPay'` | 제외 조건에 사용됩니다. |
            | @@ERROR | 시스템 정수 값 | 직전 문장 실행 결과 | 오류 여부를 판정합니다. |

            ## CRUD 분석
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_SETTLE_EXPECT_PROC", spec) })["UP_UTIL_SETTLE_EXPECT_PROC"];

        var local = Assert.Single(facts.LocalVariables, v => v.Name == "@v_PLCardSettlePeriodPG");
        Assert.Equal("varchar(200)", local.TypeOrKind);

        var system = Assert.Single(facts.LocalVariables, v => v.Name == "@@ERROR");
        Assert.True(system.IsSystemValue);
    }

    // 헤딩을 접두사로 넓혀 잡을 때 "지역"만 공유할 뿐 지역 변수 표가 아닌 다른
    // 절("지역별 매출 요약" 같은)을 삼키면 안 된다 - 접두사가 반드시 "지역 변수"
    // 까지 포함해야 한다는 것을 지킨다.
    [Fact]
    public void LocalVariables_DoesNotSwallowUnrelatedHeadingThatOnlySharesTheWord지역()
    {
        const string spec = """
            ### 지역별 매출 요약

            | 지역 | 매출 | 비고 |
            | :--- | :--- | :--- |
            | 서울 | 100 | - |
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_UNRELATED", spec) })["UP_UTIL_UNRELATED"];

        Assert.Empty(facts.LocalVariables);
    }

    // 기존 COMM_UPD 모양(AND 조건 "데이터 타입 또는 구분" 헤더, "SQL Server
    // 시스템 값" 마커)이 새 매칭 방식에서도 그대로 유지되는지 - 이미
    // SystemValues_AreMarkedAndNotTreatedAsLocalVariables가 지키지만, 타입 칸
    // 탐색 방식이 바뀌므로 그 값 자체를 다시 못박는다.
    [Fact]
    public void LocalVariables_CommUpdShape_StillReadsAndConditionHeaderCorrectly()
    {
        var variables = Extract().LocalVariables;

        var local = Assert.Single(variables, v => v.Name == "@v_valIncVat");
        Assert.Equal("DECIMAL(2,1)", local.TypeOrKind);
    }

    // INS_EXTRA의 실물 헤딩·표 모양. 헤딩이 "지역"이 아니라 "내부"로 시작한다
    // ("### 내부 변수") - "### 지역 변수" 접두사만으로는 이 헤딩을 못 찾는다.
    [Fact]
    public void LocalVariables_RecognizeInsExtraInnerVariableHeading()
    {
        const string spec = """
            ### 내부 변수

            | 변수 명칭 | 데이터 타입 | 초기값 또는 대입식 | 관련 컬럼 및 사용 관계 |
            | :--- | :--- | :--- | :--- |
            | `@v_strReqYMD` | `VARCHAR(8)` | `''`, 이후 `MIN(ReqYMD)` | 집계 결과를 대입받습니다. |
            | `@v_valIncVat` | `DECIMAL(2,1)` | `1.1` | UPDATE 5에서 사용됩니다. |

            ## CRUD 분석
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_SETTLE_INS_EXTRA", spec) })["UP_UTIL_SETTLE_INS_EXTRA"];

        var local = Assert.Single(facts.LocalVariables, v => v.Name == "@v_strReqYMD");
        Assert.Equal("VARCHAR(8)", local.TypeOrKind);
        Assert.False(local.IsSystemValue);
    }

    // SUMMARY_ETC의 실물 헤딩·표 모양. 헤딩이 "### 내부 변수와 컬럼 관계"로,
    // INS_EXTRA와도 글자가 다르다 - 접두사가 "### 내부 변수"까지만이어야
    // 두 파일을 모두 잡으면서 뒤에 오는 꼬리말은 자유로워야 한다.
    [Fact]
    public void LocalVariables_RecognizeSummaryEtcInnerVariableAndColumnHeading()
    {
        const string spec = """
            ### 내부 변수와 컬럼 관계

            | 내부 변수 명칭 | 데이터 타입 | 값의 원천 | 삭제 조건에서 대응하는 컬럼 | 삽입 원천 조건 |
            | :--- | :--- | :--- | :--- | :--- |
            | `@v_strYMD` | `CHAR(8)` | 커서 `A.YMD` | `YMD` | `YMD` 조건 |

            ## CRUD 분석
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_SETTLE_SUMMARY_ETC", spec) })["UP_UTIL_SETTLE_SUMMARY_ETC"];

        var local = Assert.Single(facts.LocalVariables, v => v.Name == "@v_strYMD");
        Assert.Equal("CHAR(8)", local.TypeOrKind);
    }

    // "내부"로 넓혀도 지역/내부 변수 표가 아닌 절(가상의 "### 내부 통제 절차")을
    // 삼키면 안 된다 - 접두사가 반드시 "내부 변수"까지 포함해야 한다.
    [Fact]
    public void LocalVariables_DoesNotSwallowUnrelatedHeadingThatOnlySharesTheWord내부()
    {
        const string spec = """
            ### 내부 통제 절차

            | 절차 | 설명 |
            | :--- | :--- |
            | 1단계 | 검토 |
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_UNRELATED2", spec) })["UP_UTIL_UNRELATED2"];

        Assert.Empty(facts.LocalVariables);
    }

    // 알려진 한계(라운드 2에서 손대지 않음) - SUMMARY_EXTRA의 "내부 변수 명칭"
    // 표는 전용 `###`/`##` 헤딩이 없어 헤딩 앵커 자체가 없다. 절 경계를 표
    // 모양만으로 판별하지 않는 한, 이 표는 LocalVariables에 들어오지 않는다 -
    // 이 테스트는 그 부재를 못박아 다음 라운드가 "이미 닫혔다"고 착각하지
    // 않게 한다.
    [Fact]
    public void LocalVariables_KnownLimitation_HeadinglessInnerVariableTableIsNotRead()
    {
        const string spec = """
            ## 파라미터 목록

            | 매개변수 명칭 | 데이터 타입 | 방향 | Null 여부 | 기본값 | 사용처 및 영향 컬럼 |
            | :--- | :--- | :--- | :--- | :--- | :--- |
            | `@pi_strYMD` | `CHAR(8)` | 입력 | 없음 | 없음 | 조회 조건 |

            | 내부 변수 명칭 | 데이터 타입 | 초기값 | 산출 방식 | 영향 |
            | :--- | :--- | :--- | :--- | :--- |
            | `@v_strReqYMD` | `VARCHAR(8)` | `''` | `MIN(ReqYMD)` | DELETE·INSERT 조건에 사용됩니다. |

            ## CRUD 분석
            """;

        var facts = SpecStatementFactsExtractor.Extract(
            new[] { ("dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA", spec) })["UP_UTIL_SETTLE_SUMMARY_EXTRA"];

        Assert.Empty(facts.LocalVariables);
    }
}
