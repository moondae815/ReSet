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
        /// 원문을 복원하고 같은 개행·탭 정규화가 필요하다(아래 참고). 원래 주석은
        /// "세 번째 소비자가 생기면 그때 중립 헬퍼 클래스로 옮긴다"고 적었었다 - 지금이
        /// 그 시점이지만, Task 13의 쓰기 집합이 새 파일을 허용하지 않아 이번에는 이
        /// 메서드를 그대로 두고 재사용하는 쪽을 택했다(같은 어셈블리 안에서 `internal`로
        /// 이미 접근 가능하다). 네 번째 소비자가 생기거나 쓰기 집합이 넓어지면 그때
        /// MarkdownTableCellCodec 같은 중립 클래스로 옮긴다.
        ///
        /// [왜 여기서 개행·탭을 공백으로 접는가 - Task 13, 최종 브랜치 리뷰 Critical]
        /// 이 값은 결국 세 기계 확정 표(CASE 분기 · 실행 의미의 @@ROWCOUNT/식 타입 경로)의
        /// 셀에 실리고, 렌더 시점에 `AiService.EscapeTableCell` → `MarkdownTableCellCodec.
        /// Escape`를 거친다. `Escape`는 `\r\n`·`\n`·`\r`을 공백으로 접고 표 셀은 한 줄에서
        /// 잘라낸 것이라 애초에 개행을 담을 수 없다 - 접지 않으면 원문을 한 글자도 안
        /// 틀리고 옮겨도 L1(MechanicalValidator)의 `==` 대조가 영원히 실패한다. 탭도
        /// 같은 자리에 접는다 - `Escape`는 탭을 건드리지 않아 기술적으로는 표 셀 안에
        /// 리터럴 탭이 살아남을 수 있지만, 마크다운 표 셀 안의 리터럴 탭을 모델이 그대로
        /// 재현하리라 기대하기 어렵다(실행 확인: `CAST(Amt\t* 100.0 AS INT)`류). 연속
        /// 공백은 접지 않는다 - 이미 관측된 실패 두 유형(개행이 만드는 구조적 불일치,
        /// 탭이 만드는 비가시 문자 재현 실패)에 대한 최소 대응이고, 공백 뭉치까지
        /// 접으면 원문에서 한 걸음 더 멀어져 축A 감사가 원본 DDL과 글자 단위로 대조할
        /// 때 표 값과 원문이 갈리는 폭이 커진다.
        /// </summary>
        internal static string TextOf(TSqlFragment? fragment)
        {
            if (fragment == null) return string.Empty;
            var raw = string.Concat(
                fragment.ScriptTokenStream
                    .Skip(fragment.FirstTokenIndex)
                    .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                    .Select(t => t.Text));
            return raw
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Replace("\t", " ")
                .Trim();
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
