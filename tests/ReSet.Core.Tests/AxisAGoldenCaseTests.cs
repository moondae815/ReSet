using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 감사가 `정합`으로 판정한 세 SP를 회귀 안전판으로 고정한다.
    ///
    /// [규칙의 단서 — 2026-08-17] 설계 문서(§골든 케이스 "그 규칙이 적용되지 않는
    /// 경우")가 정정한 대로, "이 셋이 깨지면 검사가 틀린 것이다"는 조건 없이 성립하지
    /// 않는다. L1 검사가 요구하는 재료(DML 범위 표 등)가 그 Spec을 생성할 당시
    /// 프롬프트에 없었다면, 재생성 전 실패는 검사의 결함이 아니라 설계 0의 계약이
    /// 의도한 정상 동작이다 - 실측으로 `CheckDmlScopeTable`을 켜면 저장된 14개 프로시저
    /// 중 13개가 실패하고 그중 골든 둘(`INS_EXTRA4PLCARD`, `Util_Settle_Summary_AcqManual`)
    /// 이 포함된다. 이 규칙 하나를 이유로 <b>저장된 Spec.md에 L1을 걸지 않는다</b> -
    /// output/이 .gitignore 대상이라 CI에서 비결정적이 된다는 이유와, "재생성 전에는
    /// 실패가 정상"이라는 이유 둘 다 때문이다. 다음에 이 파일을 읽는 사람이 L1
    /// 어서션을 "복구"하지 않도록 여기 명시해 둔다.
    ///
    /// 대신 두 층을 고정한다.
    /// 1. 추출기가 실제 원본에서 폭발하거나 재료를 폭주시키지 않는다(하한).
    /// 2. 추출기가 뽑은 앵커가 과거에 L1을 불능으로 만들었던 모양(맨 숫자:숫자,
    ///    문자열 리터럴 값)을 다시는 내지 않는다(회귀 가드) - Task 5의 `17:37)`과
    ///    Task 11의 `dacomcard`/`tosscard`가 그 실물이다. 개수만 세는 것보다
    ///    이쪽이 실제 결함을 다시 잡을 가능성이 높다.
    /// </summary>
    public class AxisAGoldenCaseTests
    {
        private static readonly string[] GoldenProcedures =
        {
            "dbo.UP_UTIL_SETTLE_CANCEL_INS",
            "dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD",
            "dbo.UP_Util_Settle_Summary_AcqManual"
        };

        [SkippableTheory]
        [InlineData("dbo.UP_UTIL_SETTLE_CANCEL_INS")]
        [InlineData("dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD")]
        [InlineData("dbo.UP_Util_Settle_Summary_AcqManual")]
        public void Extractors_ShouldNotThrowOnGoldenProcedures(string procedureName)
        {
            var ddl = TryReadObjectDefinition(procedureName);
            Skip.If(ddl == null, CorpusSkip.Reason); // 산출물이 없는 환경(예: 갓 클론한 워크트리)에서는 건너뜀으로 표시한다.

            var comments = SourceCommentExtractor.Extract(ddl);
            var rounding = RoundingSemanticsExtractor.Extract(ddl);
            var options = SessionOptionsExtractor.Extract(ddl);
            var scopes = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");
            var derived = DerivedTableColumnExtractor.Extract(ddl);

            // 배너가 잦으면 사람이 읽지 않는다 - 재료가 폭주하지 않는지 본다.
            // 상한 40은 SourceCommentExtractor.MaxBlocks(Task 5)와 같은 값이다 - 그
            // 캡이 실제로 지켜지는지 원본 데이터로 다시 확인하는 것이지, 값을
            // 새로 정하는 것이 아니다.
            Assert.InRange(comments.Count, 0, 40);
            Assert.All(rounding, c => Assert.False(string.IsNullOrWhiteSpace(c.ThirdArgument)));
            Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o)));
            Assert.All(scopes, s => Assert.True(s.Line > 0));
            Assert.All(derived, d => Assert.False(string.IsNullOrWhiteSpace(d.Column)));
        }

        [Fact]
        public void GoldenProcedureList_ShouldMatchTheAuditVerdict()
        {
            // 감사 보고서 §3-A에서 `정합`으로 판정된 셋. 이 목록을 줄이려면
            // 감사를 다시 돌려 근거를 바꿔야 한다.
            Assert.Equal(3, GoldenProcedures.Length);
        }

        /// <summary>
        /// 맨 "숫자:숫자" 모양의 앵커. `UP_UTIL_SETTLE_COMM_UPD.Procedure:95`의
        /// "2019.06-10 17:37" 안 "17:37"이 정확히 이 모양이었다 - 시각이지 코드
        /// 범례가 아닌데, 라벨이 글자로 시작해야 한다는 판별자가 없던 시절에는
        /// 이것이 유일한 앵커가 되어 L1이 재생성으로 고칠 수 없는 요구를 냈다
        /// (SourceCommentExtractor.CodeLegendRegex 주석, Task 5). 이 정규식은 그
        /// 판별자가 실제 원본 전체에서 여전히 지켜지는지를 본다.
        /// </summary>
        private static readonly Regex BareDigitColonDigit = new(@"^\d+:\d+$", RegexOptions.Compiled);

        /// <summary>
        /// DerivedTableColumnExtractor가 앵커를 뽑기 전에 지우는 것과 같은 모양의
        /// SQL 작은따옴표 문자열 리터럴. 원본(정화 전) 표현식에서 리터럴 값을 따로
        /// 뽑아, 그 값이 앵커 목록에 새지 않았는지 독립적으로 확인하는 데 쓴다.
        /// </summary>
        private static readonly Regex StringLiteral = new(@"'(?:[^']|'')*'", RegexOptions.Compiled);

        [SkippableFact]
        public void SourceCommentAnchors_ShouldNeverBeBareDigitColonDigit()
        {
            var files = FindAllObjectDefinitions();
            Skip.If(files.Count == 0, CorpusSkip.Reason);

            var offenders = new List<string>();
            foreach (var file in files)
            {
                var ddl = File.ReadAllText(file);
                foreach (var block in SourceCommentExtractor.Extract(ddl))
                {
                    foreach (var anchor in block.Anchors)
                    {
                        if (BareDigitColonDigit.IsMatch(anchor))
                        {
                            offenders.Add($"{ObjectNameOf(file)}: \"{anchor}\" ({block.Line}행)");
                        }
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "시각을 코드 범례로 오분류한 앵커가 나왔습니다(Task 5가 고친 `17:37)` 재발):\n"
                + string.Join("\n", offenders));
        }

        [SkippableFact]
        public void DerivedTableAnchors_ShouldNeverBeStringLiteralValues()
        {
            var files = FindAllObjectDefinitions();
            Skip.If(files.Count == 0, CorpusSkip.Reason);

            var offenders = new List<string>();
            foreach (var file in files)
            {
                var ddl = File.ReadAllText(file);
                foreach (var definition in DerivedTableColumnExtractor.Extract(ddl))
                {
                    var literalValues = StringLiteral.Matches(definition.Expression)
                        .Select(m => m.Value.Trim('\''))
                        .Where(v => v.Length > 0)
                        .ToList();

                    foreach (var anchor in definition.Anchors)
                    {
                        if (literalValues.Any(v => string.Equals(v, anchor, StringComparison.OrdinalIgnoreCase)))
                        {
                            offenders.Add(
                                $"{ObjectNameOf(file)}: 별칭 {definition.Alias}.{definition.Column}의 "
                                + $"앵커 \"{anchor}\"가 문자열 리터럴 값입니다");
                        }
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "문자열 리터럴 값이 식별자 앵커로 샜습니다(Task 11이 고친 `dacomcard`/`tosscard` 재발):\n"
                + string.Join("\n", offenders));
        }

        [SkippableFact]
        public void ExtractSetPredicates_OnExpectProc_ShouldCarryTheNinePgLiterals()
        {
            // 2026-08-18 축 A 감사의 🟠. object_definition.sql:39의 9개 리터럴이
            // 명세서 어디에도 하나의 집합으로 제시되지 않아, 이관하면 4개 PG가
            // 자동회수 대상에 잘못 편입된다. 픽스처가 아니라 실물 DDL로 잡는 이유는
            // 최종 리뷰의 Critical이 "12개 태스크 리뷰가 전부 픽스처만 썼고 실물
            // 코퍼스를 안 봐서 감사의 그 문서가 통과했다"였기 때문이다.
            //
            // Column="A.PGName"은 좌변 원문 표기(한정자 포함) 계약이라 "PGName"이
            // 아니라 "A.PGName"이다.
            //
            // [Line이 27에서 39로 바뀐 이유 - 2026-08-22 축 A 재감사 ③ Task 5, 설계 §4 C]
            // 라인 칸이 문장 시작줄이 아니라 그 술어 항 자신의 줄이 됐다
            // (SetPredicateFact.Line 문서 참고). 실물 DDL을 직접 세어 확인했다:
            // object_definition.sql의 27행이 이 UPDATE의 시작이고,
            // `AND    A.PGName NOT IN ('PLCard',...)`은 39행이다.
            var ddl = TryReadObjectDefinition("dbo.UP_UTIL_SETTLE_EXPECT_PROC");
            Skip.If(ddl == null, CorpusSkip.Reason);

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);
            var pgName = Assert.Single(
                facts, f => f.Line == 39 && f.Column.Equals("A.PGName", StringComparison.OrdinalIgnoreCase));

            Assert.True(pgName.IsNegated);
            Assert.Equal(9, pgName.Literals.Count);
            Assert.Contains("'SSGPayCard'", pgName.Literals);
            Assert.Contains("'KakaoCard'", pgName.Literals);

            // 원문 칸이 NOT을 지고 있는지 실물로 못 박는다. 표는 "기계 확정, 있는 그대로
            // 베낄 것"이라 사람이 다시 검증하지 않는데, 원문에서 NOT이 빠지면 의미가
            // 정반대인 술어가 실리고 IsNegated 칸과 원문 칸이 서로 모순된 표가 된다 -
            // 제외 목록이 허용 목록으로 읽히면 9개 PG가 대상에서 빠지는 대신 그 9개만
            // 대상이 된다. 다른 새 픽스처는 IN·등호만 덮으므로 이 갈래는 여기서만 걸린다.
            Assert.Equal(
                "A.PGName NOT IN ('PLCard','SamSungPay','SSGPayCard','KakaoPay','KakaoCard',"
                    + "'impaymobile','NaverCard','ApplePay','TossCardAuth')",
                pgName.PredicateText);
        }

        [SkippableFact]
        public void ExtractSetPredicates_OnCommUpd_ShouldCarryTheSixPgWhitelist()
        {
            // 같은 감사의 두 번째 🟠. object_definition.sql:77의 6개 화이트리스트가
            // 명세서에 없어, 해외카드 수수료율이 국내건·타 PG건까지 적용될 수 있다.
            //
            // Column="A.PGNAME"은 좌변 원문 표기 계약이고, 나머지 14개 집합 술어 중
            // 원소 6개에 'DACOMCARD'를 담은 것은 이 행 하나뿐이라 Assert.Single이 안전하다.
            //
            // [Line이 58에서 77로 바뀐 이유 - 2026-08-22 축 A 재감사 ③ Task 5, 설계 §4 C]
            // 라인 칸이 그 술어 항 자신의 줄이 됐다. 실물 DDL을 직접 세어 확인했다:
            // 58행이 이 UPDATE의 시작이고 화이트리스트 IN은 77행이다(위 주석이 이미
            // "object_definition.sql:77의 6개 화이트리스트"라고 적은 그 줄이다).
            var ddl = TryReadObjectDefinition("dbo.UP_UTIL_SETTLE_COMM_UPD");
            Skip.If(ddl == null, CorpusSkip.Reason);

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);
            var whitelist = Assert.Single(
                facts, f => f.Line == 77 && f.Column.Equals("A.PGNAME", StringComparison.OrdinalIgnoreCase)
                    && f.Literals.Count == 6 && f.Literals.Contains("'DACOMCARD'"));

            Assert.False(whitelist.IsNegated);
            Assert.Contains("'INICARD'", whitelist.Literals);
            Assert.Contains("'TOSSCARD'", whitelist.Literals);

            // 원문 칸도 실물로 못 박는다. 77행 원문은 `A.PGNAME     IN (...)`처럼
            // 한정자와 IN 사이에 공백이 다섯 칸이라, 접지 않으면 원문 칸의 표기가
            // 원본의 정렬 공백에 좌우된다 - 코퍼스 전수 프로브(2026-08-22) 기준
            // 접기를 실제로 요구하는 것은 개행이 아니라 이 줄 안의 공백이다
            // (SetPredicateFact.PredicateText 문서 참고). 픽스처가 아니라 코퍼스가
            // 이 접기를 요구하는 자리다.
            Assert.Equal(
                "A.PGNAME IN ('ALLTHEGATE','DACOMCARD','UNIONPAY','INICARD','TOSSCARD','NICECARD')",
                whitelist.PredicateText);
        }

        // 2026-08-18 최종 브랜치 리뷰 실측(Minor 2) - 위 InRange(0, 40)와 두 All은
        // 셋 중 둘(CANCEL_INS, AcqManual)에서 공허하게 통과했다: 그 둘의 실제 집합
        // 술어 개수가 0이므로 Assert.All(빈 컬렉션)은 아무것도 검사하지 않고
        // 무조건 통과한다. "폭발하지 않는다"는 의도는 유지하되, 각 프로시저의
        // 정확한 기대 개수를 직접 실측해 단언한다 - 추정하지 않았다.
        //
        // [2026-08-19 재실측] 축 A 감사 후 수집 범위를 넓혔다(리터럴 우변 등호·부등호,
        // ISNULL 래핑 좌변, 파생 테이블 내부 술어). 기대 개수를 임시 프로브로 다시 재
        // 갱신한다: CANCEL_INS 0→2, INS_EXTRA4PLCARD 1→13, AcqManual 0→0(그대로).
        //
        // [윗 문단의 수치는 2026-08-19 시점 실측이고 지금은 유효하지 않다 - Task 6에서
        // 확인] 그때 이 자리에는 "코퍼스 전체로는 79 → 198행이고 … 최대치인
        // EXCEPTION_PROC이 정확히 40이다"라고 적혀 있었다. 두 수치 모두 현재형으로
        // 단언돼 있어 다음 사람이 실측치로 신뢰하기 쉬웠으므로 지운다. 아래 2026-08-22
        // 문단이 같은 값을 다시 실측해 적는다.
        //
        // 같은 문단이 "'폭발하지 않는다'는 의도는 아래 InRange 상한(40)이 계속 지킨다"고
        // 적었는데 그 단언은 이 Theory에 없다 - `InRange(0, 40)`은 같은 파일 :60의
        // Extractors_ShouldNotThrowOnGoldenProcedures가 `comments.Count`에 건 것이라
        // 집합 술어와 무관하다. 즉 집합 술어의 행 수 상한은 그동안 아무 단언도 지키지
        // 않았다. 아래 InlineData에 EXCEPTION_PROC(코퍼스 최대치)을 더해 그 천장을
        // 실제 단언으로 세운다.
        //
        // [2026-08-22 재실측 - 축 A 재감사 ③ Task 6] 행 단위가 "분해된 원소"에서
        // "최상위 AND 항"으로 올라갔다(설계 §3 결정 3). 기대값은 추정이 아니라 실물
        // DDL(raw/object_definition.sql)의 최상위 AND 항을 직접 세어 확정했고, 추출기의
        // 출력이 그 수와 일치했다.
        //
        // [총 행 수는 늘고, 분해 행 수는 줄 수 있다 - 일반 명제로 오해하지 말 것]
        // 아래 네 자리 중 앞의 셋은 분해 행이 그대로이고 원문 전용 행만 늘었다. 그러나
        // **분해 행이 줄어드는 것은 정상일 수 있다.** 실측: EXCEPTION_PROC은 총계가
        // 40 → 102로 늘면서 분해 행은 40 → 30으로 열 줄었다. 원인은 회귀가 아니라 이
        // 작업의 목적 그 자체다 - `(A.UseState <> 1 OR (A.UseState = 1 AND A.YMD =
        // A.AYMD))`가 220·239·280·302·375행에 정확히 다섯 번 있고, 예전에는 각각
        // 분해 행 둘(`<> 1`, `= 1`)을 냈다. 5 × 2 = 10이 한 항 한 행으로 합쳐진 것이라
        // 차이가 정확히 상쇄된다. 그 두 행은 나란히 실려 AND로 읽혔고 그렇게 읽으면
        // 공집합이었다(설계 §2.4) - 없애야 할 행이었다.
        //
        // 그러므로 "행이 줄었으니 회귀"라고 판단하면 오판이다. 가르는 기준은 개수의
        // 방향이 아니라 **최상위 AND 항 하나가 행 하나를 얻었는가**이다. 항이 아닌
        // 부분식(OR 갈래 안의 비교, 다른 항의 좌변을 이루는 CASE 안의 조건)이 독립
        // 행을 잃는 것은 설계된 동작이다.
        //
        // 코퍼스 전체 실측(2026-08-22, output/ 아래 object_definition.sql 전부):
        // 총계 200행 → 495행, 그중 분해 행 200행 → 181행(19행 감소, 위 기전).
        //
        // - CANCEL_INS 2 → 4: INSERT 원천 SELECT의 WHERE(49~52행) 항이 넷이다.
        //   분해되던 둘(`B.USESTATE = 0`, `ISNULL(B.CompanySalesType,4) NOT IN (0,1,2,3)`)은
        //   그대로고, `A.PLTID = B.PLTID`(컬럼 대 컬럼)와 `A.YMDCANCEL = @pi_strYMD`
        //   (파라미터)가 원문 전용 행으로 더해졌다.
        // - INS_EXTRA4PLCARD 13 → 20: 네 문장의 항이 5+4+6+5다.
        //   DELETE(38~42) 5항 중 3항 분해 + 2항 신규, INSERT 원천의 파생 테이블
        //   X(168~171) 4항 중 3항 분해 + 1항 신규, UPDATE(191~196) 6항 중 4항 분해
        //   + 2항 신규, UPDATE(207~211) 5항 중 3항 분해 + 2항 신규. 분해분 합계는
        //   3+3+4+3 = 13으로 옛 기대값 그대로다.
        // - AcqManual 0 → 6: DELETE(48~50)와 INSERT 원천 SELECT(70~72)가 각각
        //   `컬럼 = @지역변수` 3항씩이다. 전부 옮겨 적을 리터럴이 없어 예전에는
        //   0행이었고, 이제 원문 전용 6행이 된다.
        // - EXCEPTION_PROC 40 → 102(분해 40 → 30): 코퍼스 최대치라 여기 더한다.
        //   🟠 결함 넷이 이 SP에 있고(210·320·442행 등), 그동안 집합 술어 행 수의
        //   천장을 지키는 단언이 하나도 없었다. 총계와 분해 행을 함께 못 박아, 행이
        //   폭발하는 것과 분해가 소리 없이 무너지는 것을 양쪽에서 잡는다.
        //
        // [2026-08-23 축 A ③(b) Task 2 - 독립 SELECT가 실리면서 넷 중 하나만 움직였다]
        // SetPredicateVisitor가 DML 밖의 독립 SELECT도 방문한다. 새 기대값은 추출기
        // 출력이 아니라 raw/object_definition.sql을 열어 독립 SELECT의 WHERE 항을
        // 직접 세어 정했다.
        //
        // - AcqManual 6 → 9(분해 0 → 2): 이 SP의 유일한 독립 SELECT는 커서 원천
        //   (29~36행)이고, 그 WHERE의 최상위 AND 항이 정확히 셋이다 -
        //   `ISNULL(A.EDIReqYmd,'') = @pi_strYMD`(33행, 우변이 파라미터라 원문 전용),
        //   `B.AcqType = 1`(34행, 분해), `A.OutState IN (2,9)`(35행, 분해).
        //   앞 브랜치가 "이 방문자는 넓히지 않는다"는 이유로 0으로 두었던 자리다.
        //   32행의 `ON A.ClientID = B.ClientID`는 WHERE가 아니라 결합 조건이라
        //   이 표의 재료가 아니다(SetPredicateVisitor는 WhereClause만 읽는다).
        //   **세 행 전부 새로 더해진 것이고 잃은 행은 없다** - 앞의 여섯 행
        //   (DELETE 3 + INSERT 3)은 그대로다.
        // - 나머지 셋은 그대로다. 실물을 열어 확인했다: CANCEL_INS는 INSERT ... SELECT
        //   하나뿐이고, INS_EXTRA4PLCARD의 SELECT 넷(19·85·133·162행)은 각각
        //   `IF EXISTS` 술어 · INSERT 원천 · 파생 테이블 X · 스칼라 서브쿼리라
        //   문장 노드(SelectStatement)가 아니며, EXCEPTION_PROC의 SELECT 열둘
        //   (47·60·73·88·96·355·374·381·457·470·486·528행)도 전부 UPDATE 안의 파생
        //   테이블(485행 UNION ALL 갈래 포함)이거나 `IN (...)` 서브쿼리다. 셋 다
        //   독립 SELECT가 하나도 없으므로 이 작업으로 늘어날 행이 없다.
        [SkippableTheory]
        [InlineData("dbo.UP_UTIL_SETTLE_CANCEL_INS", 4, 2)]
        [InlineData("dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD", 20, 13)]
        [InlineData("dbo.UP_Util_Settle_Summary_AcqManual", 9, 2)]
        [InlineData("dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", 102, 30)]
        public void ExtractSetPredicates_ShouldNotExplodeOnGoldenProcedures(
            string procedureName, int expectedCount, int expectedDecomposedCount)
        {
            var ddl = TryReadObjectDefinition(procedureName);
            Skip.If(ddl == null, CorpusSkip.Reason);

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.Equal(expectedCount, facts.Count);

            // 분해되던 행이 원문 전용 행에 삼켜지지 않았는지를 총계와 별도로 잠근다.
            // 총계만 보면 "분해 하나를 잃고 원문 하나를 얻은" 회귀가 그대로 통과한다 -
            // 총계가 변하지 않기 때문이다(분해 둘을 잃고 원문 둘을 얻어도 마찬가지다).
            // 그 회귀는 표에서 리터럴 집합만 조용히 사라지는 모양이라 가장 위험하다.
            var decomposed = facts.Where(f => f.Column != "—").ToList();
            Assert.Equal(expectedDecomposedCount, decomposed.Count);

            // 빈 집합은 표에 쓸 것이 없다 - 추출기가 그런 사실을 내면 안 된다.
            // 행이 표에서 자리를 얻는 근거는 둘 중 하나다: 분해된 리터럴 집합이거나
            // 원문이거나. 어느 쪽도 없는 행은 독자에게 아무것도 말하지 않는다.
            Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Column)));
            Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.PredicateText)));
            Assert.All(decomposed, f => Assert.NotEmpty(f.Literals));

            // 원문 전용 행은 컬럼·연산·원소 셋 다 비어야 한다 - 반쪽 분해가 실리면
            // 표가 거짓 집합을 단언한다(MixedValues 갈래).
            Assert.All(
                facts.Where(f => f.Column == "—"),
                f =>
                {
                    Assert.Equal("—", f.Operator);
                    Assert.Empty(f.Literals);
                });
        }

        private static string? TryReadObjectDefinition(string procedureName)
        {
            var root = TryFindRepoRoot();
            if (root == null) return null;

            var path = Path.Combine(
                root, "output", "Objects", $"{procedureName}.Procedure", "raw", "object_definition.sql");

            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>
        /// output/ 전체(Objects·External 포함)에서 object_definition.sql을 전부
        /// 찾는다. 골든 셋 셋만이 아니라 감사 대상 코퍼스 전체를 훑어야, 코퍼스
        /// 어딘가(예: 골든 셋 밖의 COMM_UPD)에서 재발한 결함도 잡는다.
        /// </summary>
        private static IReadOnlyList<string> FindAllObjectDefinitions()
        {
            var root = TryFindRepoRoot();
            if (root == null) return Array.Empty<string>();

            var outputDir = Path.Combine(root, "output");
            if (!Directory.Exists(outputDir)) return Array.Empty<string>();

            return Directory.EnumerateFiles(outputDir, "object_definition.sql", SearchOption.AllDirectories)
                .ToList();
        }

        private static string ObjectNameOf(string objectDefinitionPath) =>
            // .../output/.../<객체이름>.<Procedure|Function>/raw/object_definition.sql
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(objectDefinitionPath))) ?? objectDefinitionPath;

        /// <summary>
        /// RepoPaths.FindRepoRoot()는 ReSet.slnx가 없으면 예외를 던진다 - 테스트
        /// 어셈블리가 도는 환경이라면 그 파일은 항상 있으므로(output/의 존재 여부와
        /// 무관하게 저장소 자체는 있다) 그 경로를 그대로 신뢰해도 된다. 이 메서드는
        /// output/이 없는 환경에서 이 클래스 전체가 조용히 건너뛸 수 있도록 null
        /// 허용 형태로만 감싼다.
        /// </summary>
        private static string? TryFindRepoRoot()
        {
            try
            {
                return RepoPaths.FindRepoRoot();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
