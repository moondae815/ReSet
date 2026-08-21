using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">@@ROWCOUNT를 읽는 문장의 줄 번호.</param>
    /// <param name="Predicate">그 문장의 조건 원문.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record RowCountBoundaryFact(int Line, string Predicate, string Sentence);

    /// <summary>
    /// 직전 형제 문장이 IF인 자리에서 @@ROWCOUNT를 읽는 문장을 뽑는다.
    ///
    /// [실행으로 확정한 사실 - 2026-08-22, SQL Server 2022 16.0.4255.1]
    /// 원본 구조(SELECT → IF @@ROWCOUNT&lt;1 BEGIN…END → IF @@ROWCOUNT&lt;1 BEGIN…END)를
    /// 그대로 재현한 결과, 앞의 IF가 조건 거짓으로 블록을 건너뛰면 그 IF 문 자체가
    /// @@ROWCOUNT를 0으로 만든다. 실측 대상: UF_GET_COMM4CLIENT.Function:52,68 - 명세서
    /// mermaid는 1차 성공 시 3차를 건너뛰는 것으로 그려 금액 결정 규칙 자체가 달랐다(🔴).
    ///
    /// [Fix Round 1 - "항상 참"은 CASE Y에서 거짓이다] 리뷰의 Cannot-Verify를 조정자가
    /// 로컬 Docker SQL Server 2022 16.0.4255.1에서 2026-08-22에 실행으로 확정했다.
    /// <code>
    /// DECLARE @t TABLE(c INT); INSERT INTO @t VALUES(1),(2);
    /// DECLARE @e TABLE(c INT);
    /// DECLARE @x INT;
    ///
    /// -- CASE X: 앞 IF의 분기가 건너뛰어짐
    /// SELECT @x = c FROM @t;                                             -- 2행
    /// IF @@ROWCOUNT &lt; 1 BEGIN SELECT TOP 1 @x = c FROM @e ORDER BY c END -- 건너뜀
    /// IF @@ROWCOUNT &lt; 1 SELECT 'RESET_TO_0' ELSE SELECT 'NOT_RESET';     -- RESET_TO_0
    ///
    /// -- CASE Y: 앞 IF의 분기가 실행되고 행에 영향을 주는 문장으로 끝남
    /// SELECT @x = c FROM @e;                                             -- 0행
    /// IF @@ROWCOUNT &lt; 1 BEGIN SELECT TOP 1 @x = c FROM @t ORDER BY c END -- 실행됨, 1행
    /// IF @@ROWCOUNT &lt; 1 SELECT 'RESET_TO_0' ELSE SELECT 'NOT_RESET';     -- NOT_RESET
    /// </code>
    /// CASE X는 RESET_TO_0, CASE Y는 NOT_RESET이다. 즉 직전 IF의 분기가 실행되고 그 안
    /// 마지막 문장이 행에 영향을 주면 @@ROWCOUNT는 0으로 리셋되지 않고 그 문장의 행
    /// 수가 남는다 - "이 조건은 항상 참이다"는 CASE Y에서 거짓이다. 분기가 실행됐는지는
    /// 런타임 성질이라 이 정적 추출기는 알 수 없으므로, 어느 쪽으로도 단정하지 않고
    /// 두 경우를 모두 참으로 서술한다(<see cref="SemanticsSentence"/>).
    ///
    /// [왜 이 모양에만 한정하는가] T-SQL에서 어떤 문장이 @@ROWCOUNT를 보존하고
    /// 어떤 문장이 0으로 만드는지의 일반 규칙을 전부 구현하려 들면 틀릴 여지가 크다.
    /// 기계 확정 표에 추측이 섞이면 표 전체의 신뢰가 무너진다. 실측으로 닫은 모양만
    /// 싣고 나머지는 침묵한다 - 실패 방향이 안전한 쪽이다.
    /// </summary>
    public static class RowCountBoundaryExtractor
    {
        /// <summary>
        /// 직전 IF의 분기가 건너뛰어지는 경우(CASE X)와 실행되는 경우(CASE Y)를 모두
        /// 참으로 담는다 - "이 조건은 항상 참이다"라고 단정하지 않는다. 분기가 실행됐는지는
        /// 런타임 성질이라 정적 분석으로 알 수 없기 때문이다.
        /// </summary>
        public const string SemanticsSentence =
            "직전 문장이 IF입니다. 그 IF의 분기가 건너뛰어지면 @@ROWCOUNT가 0으로 리셋되어 "
            + "이 조건이 참이 됩니다. 분기가 실행되고 그 안 마지막 문장이 행에 영향을 주면 "
            + "@@ROWCOUNT는 그 문장의 행 수로 남아, 이 조건의 참·거짓은 그 값에 달려 있습니다.";

        public static IReadOnlyList<RowCountBoundaryFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<RowCountBoundaryFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<RowCountBoundaryFact>();
                }

                var visitor = new BlockVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[RowCountBoundaryExtractor] @@ROWCOUNT 경계 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<RowCountBoundaryFact>();
            }
        }

        private sealed class BlockVisitor : TSqlFragmentVisitor
        {
            public List<RowCountBoundaryFact> Facts { get; } = new();

            // Visit(StatementList)를 오버라이드해도 기본 ExplicitVisit이 AcceptChildren을
            // 호출하므로(RoundingSemanticsExtractor와 같은 근거) 프로시저 본문 최상위
            // 문장 목록뿐 아니라 BEGIN…END 블록 안, IF의 THEN 절이 BEGIN…END인 경우의
            // 내부 문장 목록까지 모두 이 메서드로 들어온다 - 별도 처리가 필요 없다.
            public override void Visit(StatementList node)
            {
                var statements = node.Statements;
                if (statements == null) return;

                for (var i = 1; i < statements.Count; i++)
                {
                    if (statements[i - 1] is not IfStatement) continue;
                    if (statements[i] is not IfStatement current) continue;
                    if (current.Predicate == null) continue;

                    // Task 13 (최종 브랜치 리뷰 Critical): 이전에는 이 메서드 안에
                    // CaseBranchExtractor.TextOf와 같은 모양의 복원 로직을 따로 두고,
                    // 개행을 접지 않았다 - 여러 줄 IF 술어(드물지만 코드 모양상 가능)를
                    // 원문 그대로 옮겨도 L1(MechanicalValidator.CheckExecutionSemantics)의
                    // `==` 대조가 영원히 실패했다. `CaseBranchExtractor.TextOf`가 같은
                    // 결함을 이미 고쳤으므로(개행·탭을 공백으로 접음) 별도 구현을 지우고
                    // 그 메서드를 재사용한다 - 두 파일에서 같은 정규화를 유지·보수하면
                    // 한쪽만 고쳐졌을 때 다시 갈라진다.
                    var predicate = CaseBranchExtractor.TextOf(current.Predicate);
                    if (predicate.IndexOf("@@ROWCOUNT", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    Facts.Add(new RowCountBoundaryFact(
                        current.StartLine, predicate, SemanticsSentence));
                }
            }
        }
    }
}
