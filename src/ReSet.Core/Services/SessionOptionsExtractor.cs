using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 프로시저 본문의 세션 옵션을 뽑는다.
    ///
    /// AS 이후의 본문에 있는 것만 담는다. CREATE 배치 앞머리의 SET ANSI_NULLS ON 같은
    /// 것은 관례적 노이즈이지 이 SP의 로직이 아니다 - 담으면 모든 명세서가 같은
    /// 결함을 하나씩 갖게 되고, 그러면 이 검사를 아무도 믿지 않는다.
    ///
    /// Util_Settle_Summary의 SET NOCOUNT ON이 AS 직후 BEGIN TRAN 앞에 있는데
    /// 명세서 전체에 언급이 없었던 것이 이 재료가 있는 이유다.
    ///
    /// [AST 파서를 쓰는 이유] Fix Round 1 리뷰 실측 - 정규식으로 "CREATE PROCEDURE ...
    /// AS"를 찾으면 지연 매치(lazy match)가 파라미터 기본값 문자열 리터럴 안의 "AS"에서
    /// 멈춰 진짜 본문 시작 앞에서 스캔이 시작된다. 그 결과 진짜 AS 앞의 블록 주석 속
    /// "SET ARITHABORT ON" 같은 텍스트가 본문 옵션으로 오탐된다 - L1이 실제로 설정되지
    /// 않은 옵션을 명세서가 기술하라고 요구하는, 재생성으로 고칠 수 없는 판정 불가
    /// 결함이다. ScriptDom은 실제 파스 트리를 보므로 문자열 리터럴이 AS 키워드가 되지
    /// 않고 주석이 문장이 되지 않는다 - RoundingSemanticsExtractor가 ROUND(에 대해 같은
    /// 이유로 AST를 쓰는 것과 동일한 판단이다.
    ///
    /// [본문 범위를 유지하는 방법] 프로시저 본체(CreateProcedureStatement /
    /// CreateOrAlterProcedureStatement)를 먼저 찾고, 그 StatementList만 별도로
    /// 순회한다. 배치 전체를 한 번에 순회하면 CREATE 앞 배치의 SET ANSI_NULLS ON도
    /// 같은 방문자가 잡아버린다 - 두 단계로 나눠야 배치 앞머리 노이즈가 구조적으로
    /// 배제된다.
    /// </summary>
    public static class SessionOptionsExtractor
    {
        /// <summary>
        /// ScriptDom의 SetOptions 플래그와 명세서 대조에 쓰는 이름의 대응. 순서는
        /// 기존 정규식 화이트리스트 순서를 유지한다 - 대조 기준이나 프롬프트 문구를
        /// 바꾸지 않기 위해서다.
        /// </summary>
        private static readonly IReadOnlyList<(SetOptions Flag, string Name)> FlagOptionNames = new[]
        {
            (SetOptions.NoCount, "NOCOUNT"),
            (SetOptions.XactAbort, "XACT_ABORT"),
            (SetOptions.ArithAbort, "ARITHABORT"),
            (SetOptions.AnsiWarnings, "ANSI_WARNINGS"),
            (SetOptions.AnsiNulls, "ANSI_NULLS"),
            (SetOptions.QuotedIdentifier, "QUOTED_IDENTIFIER"),
            (SetOptions.ConcatNullYieldsNull, "CONCAT_NULL_YIELDS_NULL"),
        };

        private const string TransactionIsolationLevelName = "TRANSACTION ISOLATION LEVEL";

        public static IReadOnlyList<string> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<string>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);

                // 구문 오류가 있어도 파싱된 조각으로 계속한다 - 파서 본체가
                // 소프트 페일하는 것과 같은 판단이다.
                if (fragment == null) return Array.Empty<string>();

                var finder = new ProcedureBodyFinder();
                fragment.Accept(finder);

                var options = new List<string>();
                var bodyVisitor = new SessionOptionVisitor(options);
                foreach (var body in finder.Bodies)
                {
                    // CLR 프로시저(EXTERNAL NAME)는 StatementList가 없다.
                    body.StatementList?.Accept(bodyVisitor);
                }

                return options;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[SessionOptionsExtractor] 세션 옵션 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<string>();
            }
        }

        /// <summary>배치 전체가 아니라 프로시저 본체 노드만 골라낸다. 자식(본문)까지
        /// 내려가지 않는다 - 본문 순회는 이 클래스의 책임이 아니라 호출부가 별도로 한다.</summary>
        private sealed class ProcedureBodyFinder : TSqlFragmentVisitor
        {
            public List<ProcedureStatementBodyBase> Bodies { get; } = new();

            public override void ExplicitVisit(CreateProcedureStatement node) => Bodies.Add(node);

            public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => Bodies.Add(node);
        }

        private sealed class SessionOptionVisitor : TSqlFragmentVisitor
        {
            private readonly List<string> _options;

            public SessionOptionVisitor(List<string> options)
            {
                _options = options;
            }

            public override void Visit(PredicateSetStatement node)
            {
                foreach (var (flag, name) in FlagOptionNames)
                {
                    if ((node.Options & flag) == 0) continue;
                    if (!_options.Contains(name, StringComparer.Ordinal)) _options.Add(name);
                }
            }

            public override void Visit(SetTransactionIsolationLevelStatement node)
            {
                if (!_options.Contains(TransactionIsolationLevelName, StringComparer.Ordinal))
                {
                    _options.Add(TransactionIsolationLevelName);
                }
            }
        }
    }
}
