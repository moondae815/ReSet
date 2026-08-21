using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 함수 선언부(CREATE FUNCTION / ALTER FUNCTION / CREATE OR ALTER FUNCTION)의
    /// WITH 옵션을 뽑는다.
    ///
    /// [왜 별도 파일인가]
    /// DmlScopeExtractor는 DML 문장 단위 사실을 다루는데 이것은 객체 선언부의 사실이라
    /// 행 단위도 방문 대상도 다르다. 한 파일에 넣으면 "DML 범위 추출기"라는 이름이
    /// 거짓이 된다.
    ///
    /// [왜 필요한가 - 2026-08-21 축 A 감사]
    /// UF_GET_OUTYMD4REFUND와 UF_GET_SETTLE_EXCHANGERATE 둘 다 WITH 절이 없다는 것이
    /// DDL 원문에서 확정되는데, 명세서가 "제공되지 않아 확인할 수 없음"으로 적었다.
    /// 같은 자리가 재생성마다 다른 답을 냈다 - 8/20 판에는 언급이 아예 없었고 8/21 판에서
    /// "확인할 수 없음"이 새로 생겼다. 재료로 확정하면 이 흔들림이 닫힌다.
    /// </summary>
    public static class ObjectDeclarationExtractor
    {
        public const string ObjectDeclarationTableHeading =
            "### 객체 선언 (기계 확정 — 수정 금지)";

        /// <param name="WithOptions">
        /// 빈 목록이 곧 "스키마 바인딩 아님"이다. 표에서는 "(없음)"으로 렌더된다.
        /// </param>
        public sealed record ObjectDeclarationFact(
            string QualifiedName,
            IReadOnlyList<string> WithOptions);

        /// <summary>
        /// 함수가 아니거나 파싱에 실패하면 null. 프로시저에는 이 옵션 자체가 없으므로
        /// 표를 싣지 않는 것이 맞다.
        /// </summary>
        public static ObjectDeclarationFact? Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return null;

            var parser = new TSql160Parser(true);
            using var reader = new StringReader(ddlText);
            var fragment = parser.Parse(reader, out var errors);
            if (fragment == null || (errors != null && errors.Count > 0)) return null;

            var visitor = new CreateFunctionVisitor();
            fragment.Accept(visitor);
            return visitor.Fact;
        }

        private sealed class CreateFunctionVisitor : TSqlFragmentVisitor
        {
            public ObjectDeclarationFact? Fact { get; private set; }

            /// <summary>
            /// Fix Round 1 리뷰 실측(180.37.3 프로브): CreateFunctionStatement·
            /// AlterFunctionStatement·CreateOrAlterFunctionStatement 셋 다
            /// FunctionStatementBody를 상속하고 Name·Options를 그 기반 클래스에서
            /// 그대로 물려받는다. TSqlFragmentVisitor의 기반 오버로드
            /// (Visit(CreateFunctionStatement) 등)의 기본 구현이 Visit(FunctionStatementBody)로
            /// 위임하는 것도 프로브로 확인했으므로, 여기 하나만 오버라이드하면 셋을
            /// 한 번에 잡는다 - SessionOptionsExtractor.ProcedureBodyFinder처럼
            /// 구체 타입마다 따로 오버라이드를 늘어놓을 필요가 없다.
            ///
            /// DbMetadataService.cs가 읽는 sys.sql_modules.definition은 마지막 배포에
            /// 실제로 쓰인 CREATE/ALTER 키워드를 그대로 보존한다 - 지금 코퍼스(31개
            /// 객체)에는 ALTER FUNCTION·CREATE OR ALTER FUNCTION 형태가 0건이라 오늘
            /// 물지는 않지만, CreateFunctionStatement만 잡으면 그 형태로 마지막
            /// 배포된 함수에서 이 표가 조용히 빠진다 - 정확히 이 작업이 닫으려는
            /// "🟡 확인할 수 없음" 결함과 같은 실패 모양이라 선제로 막는다.
            ///
            /// [Visit vs ExplicitVisit] SessionOptionsExtractor.ProcedureBodyFinder는
            /// ExplicitVisit을 써서 본문(StatementList) 자동 하강을 끊는다 - 본문
            /// 순회를 호출부의 별도 방문자에게 넘기기 위해서다. 여기는 그 이유가
            /// 없다: Options는 이 노드 자체에서 바로 읽히고, T-SQL 문법상 함수
            /// 본문 안에 또 다른 CREATE/ALTER FUNCTION이 중첩될 수 없어 Fact가
            /// 잘못 덮어써질 위험이 없다. 그래서 Visit을 그대로 두어 본문까지
            /// 하강해도(아무 것도 하지 않으므로) 무해하다 - 다만 비용은 약간 더
            /// 든다. 정확성 문제가 아니라 이후 이 클래스가 본문 안의 무언가를
            /// 추가로 봐야 할 때 실수로 이중 방문이 생기지 않도록 남겨 두는 기록이다.
            /// </summary>
            public override void Visit(FunctionStatementBody node)
            {
                if (Fact != null) return;

                var name = string.Join(".", node.Name.Identifiers.Select(i => i.Value));
                var options = node.Options
                    .Select(Render)
                    .ToList();

                Fact = new ObjectDeclarationFact(name, options);
            }

            /// <summary>
            /// ScriptDom 노드를 T-SQL 원문 표기로 옮긴다. 명세서 독자가 원본 DDL에서
            /// 찾을 수 있는 형태여야 한다.
            ///
            /// Fix Round 1 리뷰 실측(180.37.3 프로브): ExecuteAs·Inline은 OptionKind만으로는
            /// 부가 값이 드러나지 않는다 - ExecuteAsFunctionOption.ExecuteAs(CALLER/SELF/
            /// OWNER/'user' 구분과 리터럴)와 InlineFunctionOption.OptionState(ON/OFF)가
            /// 따로 있다. 옛 구현은 kind.ToString()만 대문자로 바꿔 "EXECUTEAS"·"INLINE"을
            /// 냈는데 - 원문에 없는 표기이고 principal·ON/OFF 정보가 통째로 사라진다.
            /// 두 타입은 먼저 구체 타입으로 매칭해 부가 값을 담고, 나머지는 OptionKind로
            /// 판정한다.
            /// </summary>
            private static string Render(FunctionOption option) => option switch
            {
                ExecuteAsFunctionOption executeAs => RenderExecuteAs(executeAs.ExecuteAs),
                InlineFunctionOption inline => "INLINE = " + inline.OptionState.ToString().ToUpperInvariant(),
                _ => RenderKind(option.OptionKind)
            };

            /// <summary>
            /// CALLER/SELF/OWNER는 리터럴이 없다. 'user_name' 형(ExecuteAsOption.String)은
            /// 실제 이름이 Literal.Value에 따옴표 없이 담기므로 원문 표기(따옴표 포함)로
            /// 되돌려 싣는다. Login/User는 함수 WITH EXECUTE AS 절 문법상 도달하지
            /// 않는다(그 형태는 별개인 EXECUTE AS 문에서만 쓰인다) - 방어적으로만 남긴다.
            /// </summary>
            private static string RenderExecuteAs(ExecuteAsClause clause) => clause.ExecuteAsOption switch
            {
                ExecuteAsOption.Caller => "EXECUTE AS CALLER",
                ExecuteAsOption.Self => "EXECUTE AS SELF",
                ExecuteAsOption.Owner => "EXECUTE AS OWNER",
                ExecuteAsOption.String => $"EXECUTE AS '{clause.Literal?.Value}'",
                _ => "EXECUTE AS " + clause.ExecuteAsOption.ToString().ToUpperInvariant()
            };

            /// <summary>
            /// ScriptDom의 열거 이름(SchemaBinding)을 T-SQL 표기(SCHEMABINDING)로 옮긴다.
            /// 멤버 이름은 180.37.3을 프로브로 실측해 확인했다 - Encryption, SchemaBinding,
            /// ReturnsNullOnNullInput, CalledOnNullInput, ExecuteAs, NativeCompilation,
            /// Inline(뒤 둘은 위 Render(FunctionOption)가 먼저 가로챈다).
            ///
            /// [fallback 경고] 나머지 kind는 알려진 종류가 아니라는 뜻이라 kind.ToString()을
            /// 대문자로만 바꿔 낸다 - NativeCompilation에서 이미 실측했듯 ScriptDom 열거
            /// 이름과 실제 T-SQL 키워드 표기가 항상 같지는 않다(밑줄 유무, 띄어쓰기).
            /// 이 분기에 걸리는 값이 나오면 원문과 다를 수 있으니 프로브로 실제 표기를
            /// 확인하고 케이스를 추가해야 한다 - 이 표는 "기계 확정 — 수정 금지"라
            /// 잘못된 표기를 실으면 독자가 원문에서 못 찾는다.
            /// </summary>
            private static string RenderKind(FunctionOptionKind kind) => kind switch
            {
                FunctionOptionKind.SchemaBinding => "SCHEMABINDING",
                FunctionOptionKind.Encryption => "ENCRYPTION",
                FunctionOptionKind.ReturnsNullOnNullInput => "RETURNS NULL ON NULL INPUT",
                FunctionOptionKind.CalledOnNullInput => "CALLED ON NULL INPUT",
                FunctionOptionKind.NativeCompilation => "NATIVE_COMPILATION",
                _ => kind.ToString().ToUpperInvariant()
            };
        }
    }
}
