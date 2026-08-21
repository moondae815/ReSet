using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// CREATE FUNCTION 선언부의 WITH 옵션을 뽑는다.
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

            public override void Visit(CreateFunctionStatement node)
            {
                if (Fact != null) return;

                var name = string.Join(".", node.Name.Identifiers.Select(i => i.Value));
                var options = node.Options
                    .Select(o => Render(o.OptionKind))
                    .ToList();

                Fact = new ObjectDeclarationFact(name, options);
            }

            /// <summary>
            /// ScriptDom의 열거 이름(SchemaBinding)을 T-SQL 표기(SCHEMABINDING)로 옮긴다.
            /// 명세서 독자가 원본 DDL에서 찾을 수 있는 형태여야 한다.
            /// 멤버 이름은 180.37.3을 프로브로 실측해 확인했다 - Encryption,
            /// SchemaBinding, ReturnsNullOnNullInput, CalledOnNullInput,
            /// ExecuteAs, NativeCompilation, Inline.
            /// </summary>
            private static string Render(FunctionOptionKind kind) => kind switch
            {
                FunctionOptionKind.SchemaBinding => "SCHEMABINDING",
                FunctionOptionKind.Encryption => "ENCRYPTION",
                FunctionOptionKind.ReturnsNullOnNullInput => "RETURNS NULL ON NULL INPUT",
                FunctionOptionKind.CalledOnNullInput => "CALLED ON NULL INPUT",
                _ => kind.ToString().ToUpperInvariant()
            };
        }
    }
}
