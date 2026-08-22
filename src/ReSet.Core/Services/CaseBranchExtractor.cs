using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">이 분기의 줄 번호.</param>
    /// <param name="Ordinal">"WHEN n" 또는 "ELSE".</param>
    /// <param name="Condition">조건 원문. ELSE는 "(그 외 전부)".</param>
    /// <param name="Result">결과 원문.</param>
    public sealed record CaseBranchFact(int Line, string Ordinal, string Condition, string Result);

    /// <summary>
    /// CASE 식의 분기를 순서대로 전수 뽑는다. 조건과 결과 모두 원문 그대로다.
    ///
    /// [왜 원문 그대로인가 - 2026-08-22 축 A 감사]
    /// UIF_SettleYMD에서 🟠 3건이 났고 셋 다 요약이 원인이었다. (1) SettleCount = 2와
    /// ELSE를 하나로 뭉개 제3 구간 판정이 잘못된 분기에 붙었다. (2) 엄격 초과(&gt;)를
    /// "비교해"로 적어 경계에서 오프셋이 일주일 어긋났다. (3) 결과식의
    /// RIGHT('0' + CONVERT(VARCHAR(2), SettleDayN), 2) 영 채움이 "결합합니다"로
    /// 요약돼 한 자리 일자에서 7자 문자열이 됐다. 셋 다 원문을 그대로 실으면 닫힌다.
    ///
    /// [왜 표를 따로 두는가] 실행 의미 표와 행 수의 자릿수가 다르다 - 한 함수에서
    /// WHEN이 24개 나는 실측(UF_GET_COMM4CLIENT4INTEREST)이 있다. 한 표에 섞으면
    /// 다른 종류가 묻힌다.
    ///
    /// [SimpleCase 조건 칸은 재구성이다] `CASE @p WHEN '02' THEN ...`의 WHEN 절 자체는
    /// 비교 연산자를 담지 않는다 - `@p = '02'`라는 등식은 T-SQL 의미가 확정하는
    /// 귀결이지 원문 토큰이 아니다. 그래서 이 표의 "원문 그대로" 원칙은 SearchedCase의
    /// 조건(WHEN 뒤 BooleanExpression)과 모든 결과식(THEN/ELSE)에는 글자 그대로
    /// 성립하지만, SimpleCase의 조건 칸은 `{input} = {whenValue}` 형태로 두 원문
    /// 조각을 이어 붙인 재구성이다 - 두 조각(input, whenValue) 각각은 원문 그대로이되
    /// 그 사이의 " = "는 추출기가 T-SQL 의미로부터 삽입한 것이다.
    /// </summary>
    public static class CaseBranchExtractor
    {
        public const string TableHeading = "### CASE 분기 (기계 확정 — 수정 금지)";

        public const string ElseConditionText = "(그 외 전부)";

        public static IReadOnlyList<CaseBranchFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<CaseBranchFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    // 새 추출기의 파서 오류 정책 - 오류가 하나라도 있으면 빈 목록
                    // (DmlScopeExtractor.ExtractLockHints와 같은 정책). 기계 확정
                    // 표에 부분 파스 결과가 섞이면 표 전체의 신뢰가 무너진다.
                    return Array.Empty<CaseBranchFact>();
                }

                var visitor = new CaseVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[CaseBranchExtractor] CASE 분기 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<CaseBranchFact>();
            }
        }

        /// <summary>
        /// 조각의 원문 텍스트를 토큰 스트림에서 복원한다.
        ///
        /// `internal`인 이유는 이제 소비자가 셋이기 때문이다 - Task 9의
        /// ExpressionTypePathExtractor(CAST 식 원문), 그리고 Task 13의
        /// RowCountBoundaryExtractor(`@@ROWCOUNT` 술어 원문)가 CASE 분기와 같은 방식으로
        /// 원문을 복원하고 같은 공백 정규화가 필요하다(아래 참고). 원래 주석은
        /// "세 번째 소비자가 생기면 그때 중립 헬퍼 클래스로 옮긴다"고 적었었다 - 지금이
        /// 그 시점이지만, Task 13의 쓰기 집합이 새 파일을 허용하지 않아 이번에는 이
        /// 메서드를 그대로 두고 재사용하는 쪽을 택했다(같은 어셈블리 안에서 `internal`로
        /// 이미 접근 가능하다). 네 번째 소비자가 생기거나 쓰기 집합이 넓어지면 그때
        /// MarkdownTableCellCodec 같은 중립 클래스로 옮긴다.
        ///
        /// [왜 연속 공백류 전부를 공백 하나로 접는가 - Task 13 Fix Round 1]
        /// 이 값은 결국 세 기계 확정 표(CASE 분기 · 실행 의미의 @@ROWCOUNT/식 타입 경로)의
        /// 셀에 실리고, 렌더 시점에 `AiService.EscapeTableCell` → `MarkdownTableCellCodec.
        /// Escape`를 거친다. 최초 수정(Task 13 원 라운드)은 `\r\n`·`\n`·`\r`·`\t`만
        /// 개별로 공백 하나씩으로 바꿨는데, 그러면 원본의 들여쓰기가 그 자리에 그대로
        /// 남아 긴 연속 공백 런이 생긴다(실측: 개행 하나를 접은 자리에 다음 줄의 들여쓰기
        /// 스무 칸 남짓이 붙어 남는다). `Escape`는 그 런을 건드리지 않고, `SplitRow`도
        /// 셀 양끝만 `Trim`한다(MarkdownTableCellCodec.cs) - 그런데 표는 코드 펜스가
        /// 아니라 들여쓰기된 평문 마크다운으로 프롬프트에 실려 "표를 그대로 옮겨라"라고만
        /// 지시된다(AiService.cs) - 렌더링에서 그 공백 런은 눈에 보이지도 않으므로 모델이
        /// 정규화하는 바로 그 종류의 공백이다. 그래서 개행·탭 각각을 따로 접는 대신
        /// `\s+`(공백·탭·개행 등 모든 공백류의 연속)를 공백 하나로 접는다 - 이것은 이
        /// 저장소의 새 규칙이 아니라 이미 있던 것을 뒤늦게 따르는 것이다:
        /// `DmlScopeExtractor.CollapseWhitespace`(2026-08-20 리뷰 Important, 같은 이유로
        /// 도입됨 - "개행이 있는 값은 어떤 산출물도 만족시킬 수 없는 요구")와
        /// `DerivedTableColumnExtractor`의 private `TextOf`가 이미 같은 정규화를 한다.
        /// 세 번째 추출기(CASE 분기 표)만 다르게 접을 근거가 없다.
        ///
        /// [Fix Round 2 - "토큰 내용은 안 바뀐다"는 과잉 주장이었다] `\s+`는 토큰
        /// 경계를 모른다 - 이어붙인 원문 전체에서 매치하므로, 문자열 리터럴이나
        /// 대괄호 식별자 **안**의 연속 공백도 접는다(`'a  b'` → `'a b'`,
        /// `[my  col]` → `[my col]`). 이런 자리는 접히는 게 토큰 *사이* 공백이 아니라
        /// 토큰 자신의 내용이므로, "토큰 내용은 안 바뀐다"는 일반적으로는 거짓이다.
        /// L1 계약에는 영향이 없다(기대값·렌더값 양쪽이 같은 `TextOf` 출력을 쓰므로
        /// 여전히 같다) - 다만 "기계 확정 — 수정 금지" 표에 그 리터럴이 원본과 한
        /// 글자 다르게 실릴 수 있다는 뜻이다. 이것이 안전하다고 **보증**하지는 않는다
        /// - **관측**이다: 조정자가 코퍼스를 쟀고, 연속 공백이 걸린 자리 6건은 전부
        /// 동적 SQL 조립(`' + @v_strINYMD + '`처럼 리터럴을 잘라 이어 붙이는 자리)이라
        /// 실제 리터럴 값 안이 아니었고, 이 세 추출기가 뽑는 대상(`CASE` 조건·
        /// `@@ROWCOUNT` 술어·`CAST` 인자)에도 없었다. 대괄호 식별자 안 연속 공백은
        /// 0건이었다. 제로폭·서식 문자(U+200B·U+FEFF 같은 유니코드 Cf 범주)는 `\s`에
        /// 걸리지 않아 셀에 남을 수 있는데, 이 저장소 DDL에서 관측된 적이 없어 손대지
        /// 않는다. **이 전제는 코퍼스가 바뀌면 깨질 수 있다** - 다음 사람은 이것을
        /// "안전하다"가 아니라 "코퍼스 관측에 기댄 조건부 전제가 있다"로 읽어야 한다.
        /// </summary>
        internal static string TextOf(TSqlFragment? fragment)
        {
            if (fragment == null) return string.Empty;
            var raw = string.Concat(
                fragment.ScriptTokenStream
                    .Skip(fragment.FirstTokenIndex)
                    .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                    .Select(t => t.Text));
            return string.IsNullOrWhiteSpace(raw)
                ? string.Empty
                : System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
        }

        // SearchedCaseExpression/SimpleCaseExpression은 리프 문장이 아니라 스칼라 식이고,
        // 컨테이너 문장 노드(StatementList 등)가 아니다 - Visit(T)를 오버라이드해도
        // ScriptDom은 자식으로의 하강을 계속한다(이 배치에서 실측 확인: SELECT 목록·SET·
        // WHERE(최상위·서브쿼리 안)·UPDATE SET 어디의 CASE도, 그리고 바깥 CASE의 THEN
        // 안에 중첩된 CASE도 모두 개별 Visit 호출로 도달한다). 그래서 별도의
        // ExplicitVisit/base 호출이나 StatementList 오버라이드가 필요 없다.
        //
        // [Task 13 수정 - 아래 문장이 전에 거짓이었다] 이전 버전은 "이 결론은 이 두 식
        // 노드에만 성립을 확인했다 - 컨테이너 노드의 Visit(T)를 ExplicitVisit/base 호출
        // 없이 오버라이드하면 그 자식으로의 하강이 실제로 끊긴다"고 적었다. 이건 틀렸다.
        // RowCountBoundaryExtractor.BlockVisitor가 정확히 그것을 한다 - 컨테이너 노드인
        // Visit(StatementList)를 ExplicitVisit/base 호출 없이 오버라이드하는데, 중첩된
        // BEGIN…END 안의 StatementList까지 정상 방문된다(RowCountBoundaryExtractorTests.
        // Extract_NestedInsideIfBeginEndBlock_IsCovered가 통과하는 테스트로 못박는다).
        // 즉 ScriptDom의 각 TSqlFragment 구현이 자신의 ExplicitVisit 안에서 방문자의
        // Visit(T) 호출 여부와 무관하게 자식으로 하강하므로(다른 추출기 주석의 근거:
        // "기본 ExplicitVisit이 AcceptChildren을 호출"), 리프 노드든 컨테이너 노드든
        // Visit(T) 오버라이드만으로는 하강이 끊기지 않는다 - 적어도 이 저장소가 실측
        // 확인한 모든 사례에서. 다음 사람이 이 자리를 근거로 불필요한 ExplicitVisit/base
        // 호출이나 StatementList 오버라이드를 추가하지 마라 - 필요 없다.
        //
        // [Fix Round 1 m-a - 참인 규칙도 적어 둔다] 위 문단은 무엇이 하강을 끊지
        // "않는지"만 말한다. 실제로 하강을 끊는 것은 따로 있다 - ExplicitVisit(T)를
        // 오버라이드하면서 base.ExplicitVisit(node)나 node.AcceptChildren(this)를
        // 부르지 않는 경우다(별도 프로브로 확인: TSql160Parser로 IF 안에 중첩된
        // BEGIN…END를 파싱한 뒤, ExplicitVisit(StatementList)를 base 호출 없이
        // 오버라이드한 방문자는 최상위 StatementList 1개만 보고 IF 안의 것은 보지
        // 못했다 - 같은 스크립트에서 Visit(StatementList)만 오버라이드한 방문자는
        // 3개 모두 봤다). Visit(T)와 ExplicitVisit(T)는 이름이 비슷해도 계약이
        // 다르다 - 전자는 방문 훅일 뿐이고 하강은 프레임워크가 각 TSqlFragment의
        // ExplicitVisit 구현 안에서 하지만, 후자를 재정의하면 그 하강 책임까지
        // 통째로 떠맡는다.
        //
        // 방문 지점 커버리지는 CaseBranchExtractorTests의
        // Extract_NestedCase_ShouldAttributeEachBranchToItsOwnCaseOnly ·
        // Extract_CaseInsideWhereClause_ShouldStillBeVisited ·
        // Extract_CaseInsideSelectList_ShouldStillBeVisited 가 못박는다.
        private sealed class CaseVisitor : TSqlFragmentVisitor
        {
            public List<CaseBranchFact> Facts { get; } = new();

            public override void Visit(SearchedCaseExpression node)
            {
                var ordinal = 1;
                foreach (var clause in node.WhenClauses)
                {
                    Facts.Add(new CaseBranchFact(
                        clause.StartLine,
                        $"WHEN {ordinal++}",
                        TextOf(clause.WhenExpression),
                        TextOf(clause.ThenExpression)));
                }

                AddElse(node.ElseExpression, node.StartLine);
            }

            public override void Visit(SimpleCaseExpression node)
            {
                var input = TextOf(node.InputExpression);
                var ordinal = 1;
                foreach (var clause in node.WhenClauses)
                {
                    Facts.Add(new CaseBranchFact(
                        clause.StartLine,
                        $"WHEN {ordinal++}",
                        $"{input} = {TextOf(clause.WhenExpression)}",
                        TextOf(clause.ThenExpression)));
                }

                AddElse(node.ElseExpression, node.StartLine);
            }

            private void AddElse(ScalarExpression? elseExpression, int fallbackLine)
            {
                if (elseExpression == null) return;
                Facts.Add(new CaseBranchFact(
                    elseExpression.StartLine > 0 ? elseExpression.StartLine : fallbackLine,
                    "ELSE",
                    ElseConditionText,
                    TextOf(elseExpression)));
            }
        }
    }
}
