using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
    /// <param name="Kind">"BEGIN TRANSACTION" · "COMMIT TRANSACTION" · "ROLLBACK TRANSACTION" · "SAVE TRANSACTION".</param>
    /// <param name="Name">트랜잭션/세이브포인트 이름. 없으면 "(없음)".</param>
    public sealed record TransactionBoundaryFact(int Line, string Kind, string Name);

    /// <summary>
    /// 트랜잭션 경계 문장을 전수 뽑는다. 줄·종류·이름만 담고 추론하지 않는다.
    ///
    /// [왜 감싼 조건을 담지 않는가] `ROLLBACK`이 어느 `IF` 아래인지를 담으면 이행 가치가
    /// 높다. 그럼에도 담지 않는 이유는 귀속이 틀리기 쉬운 자리이기 때문이다 - `ELSE` 분기,
    /// 중첩 `IF`, `BEGIN/END` 없는 단문 `IF`, `TRY/CATCH` 안의 `ROLLBACK`. 틀린 조건이
    /// 달린 행은 조건이 없는 행보다 나쁘다. 이 저장소는 이미 그 실패를 겪었다 - 감사 🔴이
    /// "파서가 잘못 계산했고, 모델은 충실히 옮겼고, Critic은 같은 목록으로 대조해 일치를
    /// 확인했다"였다. 감싼 조건은 별도 회차에서 `IF` 술어 귀속을 제대로 설계해 붙인다.
    ///
    /// [왜 SAVE TRANSACTION까지 담는가] 실측 코퍼스에는 0건이다. 그래도 담는 이유는
    /// 세이브포인트가 하나라도 있으면 롤백 의미가 통째로 달라지기 때문이다(전체 취소가
    /// 아니라 지점 복귀). 빠뜨리면 이 표가 "트랜잭션 경계는 이게 전부"라고 거짓말을 한다.
    /// </summary>
    public static class TransactionBoundaryExtractor
    {
        public const string TableHeading =
            "### 트랜잭션 경계 " + MachineConfirmedTables.HeadingSuffix;

        /// <summary>
        /// 이 표의 헤더 셀. 프롬프트가 이 표를 렌더할 때와 L1이 명세서에서 이 표의
        /// 블록을 특정할 때 **같은 값**을 봐야 한다. 검사 쪽에 복사본을 두면 프롬프트가
        /// 바뀌는 날 조용히 낡고, 헤더 대조가 아무 블록도 못 찾아 관대한 폴백으로
        /// 후퇴하면서 결함이 소리 없이 되살아난다 - 그동안 테스트는 계속 초록이다.
        /// </summary>
        public static readonly string[] TableHeaderCells = { "라인", "종류", "이름" };

        public const string NoName = "(없음)";

        public static IReadOnlyList<TransactionBoundaryFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<TransactionBoundaryFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    // CaseBranchExtractor.Extract와 같은 정책 - 부분 파스 결과가 기계 확정
                    // 표에 섞이면 표 전체의 신뢰가 무너진다.
                    return Array.Empty<TransactionBoundaryFact>();
                }

                var visitor = new BoundaryVisitor();
                fragment.Accept(visitor);
                return visitor.Facts.OrderBy(f => f.Line).ToList();
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[TransactionBoundaryExtractor] 트랜잭션 경계 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<TransactionBoundaryFact>();
            }
        }

        private sealed class BoundaryVisitor : TSqlFragmentVisitor
        {
            public List<TransactionBoundaryFact> Facts { get; } = new();

            public override void Visit(BeginTransactionStatement node) =>
                Add(node.StartLine, "BEGIN TRANSACTION", node.Name);

            public override void Visit(CommitTransactionStatement node) =>
                Add(node.StartLine, "COMMIT TRANSACTION", node.Name);

            public override void Visit(RollbackTransactionStatement node) =>
                Add(node.StartLine, "ROLLBACK TRANSACTION", node.Name);

            public override void Visit(SaveTransactionStatement node) =>
                Add(node.StartLine, "SAVE TRANSACTION", node.Name);

            private void Add(int line, string kind, IdentifierOrValueExpression? name)
            {
                if (line <= 0) return;
                Facts.Add(new TransactionBoundaryFact(line, kind, NameOf(name)));
            }

            /// <summary>이름은 식별자일 수도 변수일 수도 있다. 둘 다 원문 그대로 싣는다.</summary>
            private static string NameOf(IdentifierOrValueExpression? name)
            {
                if (name == null) return NoName;
                if (!string.IsNullOrWhiteSpace(name.Identifier?.Value)) return name.Identifier!.Value;
                if (name.ValueExpression is VariableReference v
                    && !string.IsNullOrWhiteSpace(v.Name))
                {
                    return v.Name;
                }
                return NoName;
            }
        }
    }
}
