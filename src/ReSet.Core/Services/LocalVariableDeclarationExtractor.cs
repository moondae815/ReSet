using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <param name="Name">`@`를 포함한 변수 이름 원문.</param>
    /// <param name="DataType">선언 타입의 원문(`VARCHAR(20)`·`MONEY`). SqlDataTypeOption으로
    /// 접지 않는다 - 길이·정밀도가 사라지면 이 표의 존재 이유가 사라진다.</param>
    /// <param name="InitialValue">`DECLARE @x INT = 0`의 `0`. 초기값이 없으면 빈 문자열이다
    /// (널이 아니다) - 표의 빈 칸이 곧 "초기값 없음"이다.</param>
    public sealed record LocalVariableDeclarationFact(string Name, string DataType, string InitialValue);

    /// <summary>
    /// 원본 DDL의 `DECLARE` 지역 변수를 전수 뽑는다.
    ///
    /// [왜 이 추출기가 필요한가 - known-defects (5-3-7)]
    /// 명세서의 「지역 변수 표」는 기계 확정 카탈로그·L1 검사·프롬프트 문구 셋 중
    /// 어느 것도 요구하지 않는 표였다. 모델 교체(gpt-5.6-terra → deepseek-v4-pro-0813)
    /// 만으로 그 표가 코퍼스에서 통째로 사라졌고, 그 표를 재료로 쓰던 검사 D
    /// (CheckSpecLocalVariablesDeclared)가 18 → 0으로 조용히 꺼졌다. 잃은 18건은
    /// 진짜 결함이었다 - FETCH NEXT INTO 대상 변수에 DECLARE가 없어 컴파일 오류가 된다.
    ///
    /// [관할 경계] DeclareVariableElement만 본다.
    ///   - `DECLARE c CURSOR FOR ...`  → DeclareCursorStatement, 안 들어온다.
    ///   - `DECLARE @t TABLE (...)`    → DeclareTableVariableStatement, 안 들어온다.
    ///   - 프로시저 파라미터            → ProcedureParameter. [주의] ScriptDom에서
    ///     ProcedureParameter는 DeclareVariableElement의 하위 타입이라 저절로 빠지지
    ///     않는다 - Visit(DeclareVariableElement)에서 `node is ProcedureParameter`로
    ///     명시적으로 걸러야 한다(`## 파라미터 목록`의 매개변수 표가 담당하므로
    ///     관할이 겹치면 정본이 갈라진다).
    /// SpecMaterialCensus.DeclaredVariableVisitor가 같은 노드를 세어 DDL 사실 69를
    /// 냈다. 같은 노드를 쓰는 것이 그 값과의 대조를 성립시킨다
    /// (LocalVariableTableCorpusTests가 그 대조를 한다).
    /// </summary>
    public static class LocalVariableDeclarationExtractor
    {
        /// <summary>
        /// [이 문자열을 바꾸면 검사 D가 조용히 꺼진다]
        /// 앞부분 `### 지역 변수`가 SpecStatementFactsExtractor.LocalVariableHeadingPrefixes의
        /// 원소와 StartsWith로 일치해야 그 리더가 이 표를 읽는다.
        /// LocalVariableTableSeamTests가 그 이음매를 잠근다.
        /// </summary>
        public const string TableHeading =
            "### 지역 변수 " + MachineConfirmedTables.HeadingSuffix;

        public static IReadOnlyList<LocalVariableDeclarationFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<LocalVariableDeclarationFact>();

            TSqlFragment? fragment;
            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    // SetAssignmentExtractor.Extract와 같은 정책 - 부분 파스 결과가
                    // 기계 확정 표에 섞이면 표 전체의 신뢰가 무너진다.
                    return Array.Empty<LocalVariableDeclarationFact>();
                }
            }
            catch (Exception)
            {
                return Array.Empty<LocalVariableDeclarationFact>();
            }

            var visitor = new DeclarationVisitor();
            fragment.Accept(visitor);
            return visitor.Facts;
        }

        private sealed class DeclarationVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

            public List<LocalVariableDeclarationFact> Facts { get; } = new();

            public override void Visit(DeclareVariableElement node)
            {
                // [ScriptDom 실물과 설계 문서의 어긋남] ProcedureParameter는 ScriptDom
                // 계층에서 DeclareVariableElement의 하위 타입이라(리플렉션으로 확인:
                // ProcedureParameter.BaseType == DeclareVariableElement), 이 오버라이드
                // 하나로는 파라미터도 함께 걸린다. `TSqlFragmentVisitor`에
                // `Visit(ProcedureParameter)` 전용 오버로드는 실재한다(2026-08-29 리뷰
                // 재현: 리플렉션으로 확인) - 그런데도 이 메서드가 파라미터까지 받는
                // 이유는 `ProcedureParameter.Accept`가 자기 자신의 오버로드를 부르기
                // 전에 먼저 base(`DeclareVariableElement`)의 `Accept`로 연쇄해
                // `Visit(DeclareVariableElement)`를 함께 태우기 때문이다(실측: 파라미터
                // 하나를 담은 DDL을 Accept하면 `Visit(DeclareVariableElement)`가 먼저,
                // `Visit(ProcedureParameter)`가 그다음에 호출된다 - 이 클래스는 후자를
                // 오버라이드하지 않으므로 그쪽은 아무 일도 하지 않는 기반 구현으로
                // 간다). 관할 경계(파라미터는 `## 파라미터 목록`이 담는다)를 지키려면
                // 런타임 타입으로 걸러야 한다.
                if (node is ProcedureParameter) return;

                var name = node.VariableName?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;

                // 이름으로 접는다(첫 등장 유지) - SpecMaterialCensus가 HashSet으로
                // 세므로 접지 않으면 두 계수가 갈린다. IF/ELSE 두 갈래가 같은 이름을
                // 선언하면 원본에서 먼저 나온 타입이 정본이다.
                if (!_seen.Add(name!)) return;

                Facts.Add(new LocalVariableDeclarationFact(
                    name!, TextOf(node.DataType), TextOf(node.Value)));
            }
        }

        /// <summary>
        /// 원문 토큰을 그대로 이어 붙인 뒤 개행만 접는다.
        ///
        /// [자기 사본을 쓰는 이유] SetAssignmentExtractor.TextOf와 같다 -
        /// DmlScopeExtractor.TextOf가 private이라 부를 수 없고, 자기 사본을 두는 것이
        /// 이 코드베이스의 관례다(DerivedTableColumnExtractor.cs:165 선례).
        ///
        /// [왜 개행만 접는가] AiService가 이 값을 렌더할 때 MarkdownTableCellCodec.Escape를
        /// 거치는데 Escape는 개행만 공백으로 바꾼다. MechanicalValidator는 모델이 그
        /// 렌더된 값을 베낀 텍스트를 접히지 않은 원본 fact와 대조하므로, fact에 개행이
        /// 남으면 어떤 산출물도 만족시킬 수 없는 요구가 된다.
        /// </summary>
        private static string TextOf(TSqlFragment? fragment)
        {
            if (fragment == null || fragment.ScriptTokenStream == null) return string.Empty;

            var stream = fragment.ScriptTokenStream;
            var first = fragment.FirstTokenIndex;
            var last = fragment.LastTokenIndex;
            if (first < 0 || last < first || last >= stream.Count) return string.Empty;

            var sb = new StringBuilder();
            for (var i = first; i <= last; i++)
            {
                sb.Append(stream[i].Text);
            }

            return MarkdownTableCellCodec.CollapseNewlines(sb.ToString().Trim());
        }
    }
}
