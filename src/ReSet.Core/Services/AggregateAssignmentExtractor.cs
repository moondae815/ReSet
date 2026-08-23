using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">대입문의 원본 줄 번호.</param>
    /// <param name="Variable">대입 대상 변수명.</param>
    /// <param name="Aggregate">집계 함수 이름(대문자).</param>
    /// <param name="HasInitializer">DECLARE에 초기값이 있었는가.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record AggregateAssignmentFact(
        int Line, string Variable, string Aggregate, bool HasInitializer, string Sentence);

    /// <summary>
    /// `SELECT @v = AGG(...)` 형태의 변수 대입을 뽑는다.
    ///
    /// [왜 이것이 확정 사실인가] T-SQL의 집계 SELECT는 GROUP BY가 없으면 일치 행이
    /// 0건이어도 한 행을 돌려준다. 그래서 대입은 항상 일어나고, MIN/MAX/SUM/AVG는
    /// NULL을, COUNT는 0을 넣는다. 이 사실은 이 SP의 사정이 아니라 T-SQL 명세다.
    ///
    /// [GROUP BY가 있으면 정반대다] GROUP BY가 있으면 일치 행이 0건일 때 그룹 자체가
    /// 0개이므로 이 SELECT는 0행을 돌려준다. 그러면 대입 자체가 일어나지 않아 변수는
    /// 대입 전 값을 그대로 유지한다 - GROUP BY 없는 경우와 반대 방향이다. 이 사실도
    /// 애매하지 않고 T-SQL 명세로 확정되므로, 행을 생략하지 않고 이 반대 사실을 담아
    /// 낸다(수정 라운드 1 - 리뷰가 GROUP BY 없음을 전제한 문장을 GROUP BY 있는 절에도
    /// 잘못 씌우던 결함을 잡았다).
    ///
    /// [왜 비집계 대입은 담지 않는가] `SELECT @v = c FROM t`는 무결과면 대입 자체가
    /// 일어나지 않아 변수가 직전 값을 유지한다 - 정확히 반대 의미다. 담으면 거짓이 된다.
    ///
    /// [실측] UP_UTIL_SETTLE_INS_EXTRA:16,21-25와 UP_UTIL_SETTLE_SUMMARY_EXTRA:20,25-29.
    /// 둘 다 초기값 ''가 NULL로 덮이는 사실이 명세서 전체에 한 번도 없었고, 그 결과
    /// 후속 DML의 대상 행 집합이 "없음"과 "전부"로 뒤집히는 결함이 났다.
    /// </summary>
    public static class AggregateAssignmentExtractor
    {
        private static readonly string[] NullOnEmptyAggregates = { "MIN", "MAX", "SUM", "AVG" };

        public static IReadOnlyList<AggregateAssignmentFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<AggregateAssignmentFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<AggregateAssignmentFact>();
                }

                var declareVisitor = new DeclareVisitor();
                fragment.Accept(declareVisitor);

                var visitor = new AggregateAssignmentVisitor(declareVisitor.Initialized);
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[AggregateAssignmentExtractor] 집계 대입 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<AggregateAssignmentFact>();
            }
        }

        private sealed class DeclareVisitor : TSqlFragmentVisitor
        {
            public HashSet<string> Initialized { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(DeclareVariableElement node)
            {
                if (node.Value == null) return;
                var name = node.VariableName?.Value;
                if (!string.IsNullOrWhiteSpace(name)) Initialized.Add(name!);
            }
        }

        private sealed class AggregateAssignmentVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _initialized;

            public AggregateAssignmentVisitor(HashSet<string> initialized) => _initialized = initialized;

            public List<AggregateAssignmentFact> Facts { get; } = new();

            // SelectSetVariable 단독으로는 자신을 감싼 QuerySpecification의
            // GroupByClause를 알 수 없다(부모 포인터가 없다). GROUP BY 유무로 결론이
            // 정반대이므로(수정 라운드 1) QuerySpecification 단위로 훑어 SelectElements
            // 안의 SelectSetVariable을 직접 찾는다. ScriptDom은 Visit을 오버라이드해도
            // 자식 순회를 계속하므로(DmlScopeExtractor.FromTableCollector와 같은 근거),
            // 중첩된 파생 테이블/서브쿼리의 QuerySpecification도 그대로 방문된다.
            public override void Visit(QuerySpecification node)
            {
                var hasGroupBy = node.GroupByClause != null;

                foreach (var element in node.SelectElements)
                {
                    if (element is not SelectSetVariable setVariable) continue;

                    // `SELECT @v += MAX(x)`는 대상 칸이 `SELECT @v = MAX(x)`로 렌더돼
                    // 원문에 없는 문장이 된다. 형제 둘(LoopVariableResetExtractor ·
                    // NonAggregateAssignmentExtractor)과 같은 규칙으로 거른다.
                    if (setVariable.AssignmentKind != AssignmentKind.Equals) continue;

                    if (setVariable.Expression is not FunctionCall call) continue;

                    var name = call.FunctionName?.Value;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var upper = name!.ToUpperInvariant();
                    var isCount = upper == "COUNT" || upper == "COUNT_BIG";
                    if (!isCount && !NullOnEmptyAggregates.Contains(upper)) continue;

                    // m2(Task 17, 최종 브랜치 리뷰 2차) - Wave 10의 m2가
                    // DatabasePlacementExtractor에서 정확히 이 모양을 걷어냈다: "확정값입니다"로
                    // 끝나는 문장에 미상값이 섞이면 표의 어조와 어긋난다. 그런데 이 클래스의
                    // 행은 DB 배치와 달리 대상 칸(Target) 자체가 변수명이라, 변수명을 모르면
                    // "(미상)" 대신 다듬을 만한 문구가 없다 - 행 전체가 진술 불가능해진다.
                    // 그래서 문구를 다듬는 대신 침묵한다(AGENTS.md 범주 2와 같은 원칙 -
                    // 이 클래스의 파싱 실패 분기가 이미 그 선례다): 변수명이 없으면 이 행을
                    // 아예 내지 않는다. `SelectSetVariable`은 `SELECT @v = expr` 문법으로만
                    // 만들어지고 그 문법은 `@v` 없이 성립하지 않으므로, ScriptDom이 이
                    // 파서를 통해 Variable을 null로 채우는 입력은 실측되지 않았다 -
                    // 도달 불가에 가까운 방어 코드다.
                    if (setVariable.Variable?.Name is not { } variable
                        || string.IsNullOrWhiteSpace(variable))
                    {
                        continue;
                    }
                    var hasInitializer = _initialized.Contains(variable);

                    string sentence;
                    if (hasGroupBy)
                    {
                        // GROUP BY가 있으면 무결과 시 그룹이 0개이므로 이 SELECT 자체가
                        // 0행을 돌려주고 대입이 일어나지 않는다 - NULL/0이 아니라 변수가
                        // 대입 전 값을 그대로 유지한다.
                        sentence = "GROUP BY가 있어 무결과 시 그룹이 0개이므로 이 SELECT는 0행을 돌려줍니다. "
                                   + "대입이 일어나지 않습니다 — 변수는 이전 값을 그대로 유지합니다.";
                    }
                    else if (isCount)
                    {
                        sentence = "집계 SELECT는 무결과여도 한 행을 돌려주므로 대입이 항상 일어납니다. COUNT는 0을 넣습니다.";
                    }
                    else
                    {
                        sentence = "집계 SELECT는 무결과여도 한 행을 돌려주므로 대입이 항상 일어납니다. 무결과 시 NULL이 대입됩니다"
                                   + (hasInitializer ? " — DECLARE의 초기값은 유지되지 않습니다." : ".");
                    }

                    Facts.Add(new AggregateAssignmentFact(
                        setVariable.StartLine, variable, upper, hasInitializer, sentence));
                }
            }
        }
    }
}
