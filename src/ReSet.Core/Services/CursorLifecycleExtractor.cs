using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">DECLARE CURSOR의 줄 번호.</param>
    /// <param name="CursorName">커서 이름.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record CursorLifecycleFact(int Line, string CursorName, string Sentence);

    /// <summary>
    /// 커서의 수명 주기에서 렉시컬로 확정할 수 있는 두 가지를 뽑는다 - OPEN과 CLOSE
    /// 사이의 RETURN, 그리고 LOCAL도 GLOBAL도 지정되지 않은 경우.
    ///
    /// [왜 렉시컬 관측에 그치는가] "OPEN과 CLOSE 사이에 RETURN이 있다"까지는 원문을
    /// 읽으면 확정되는 사실이다. 하지만 그 RETURN이 실제로 실행되는 경로인지(도달
    /// 가능성), 그 경로가 "오류 처리"인지는 런타임 성질이거나 호출자의 의도라서 정적
    /// 분석으로 확정할 수 없다. 그래서 문장은 "그 경로로 나가면 CLOSE/DEALLOCATE에
    /// 도달하지 않는다"는 관측의 직접 귀결만 담고, 도달 가능성이나 오류 여부는 단정하지
    /// 않는다. 실측 대상: UP_UTIL_SETTLE_SUMMARY_ETC:74-79,126-131과
    /// UP_UTIL_SETTLE_PROC_ETC:137-141 - 두 경우 모두 OPEN 뒤 여러 RETURN이 CLOSE보다
    /// 앞선 모양이었다.
    ///
    /// [왜 LOCAL 미지정이 사실인가] DECLARE CURSOR에 LOCAL도 GLOBAL도 없으면 이 구문
    /// 자체가 커서 범위를 정하지 않은 것이다 - 범위는 DB의 default_to_local_cursor
    /// 설정에 달려 있다. 이 설정값과 재호출 여부는 이 SP 밖의 사정이므로, 그 결과로
    /// 무슨 일이 일어나는지(예: 재호출 시 오류)는 단정하지 않고 "범위가 설정에 달려
    /// 있다"는 귀결까지만 싣는다.
    ///
    /// [왜 GLOBAL 명시는 침묵하는가 - I1, 2026-08-22 최종 브랜치 리뷰] GLOBAL이 명시되면
    /// 범위는 default_to_local_cursor 설정과 무관하게 전역으로 확정된다. 예전 코드는
    /// LOCAL 유무만 보고 이 경우에도 "범위가 설정에 달려 있다"는 문장을 냈는데, 이는
    /// 거짓이다. GLOBAL에 대해 참인 새 문장을 지어내는 대신 아예 내지 않는다 - 이
    /// 클래스가 다른 모든 미확정 상황에서 쓰는 침묵 계약과 같다.
    ///
    /// [어긋난 순서에서 침묵하는 이유] 같은 커서 이름이 여러 번 OPEN/CLOSE되거나 CLOSE가
    /// OPEN보다 앞서거나 CLOSE 없이 DEALLOCATE만 있으면 "OPEN과 CLOSE 사이"라는 관측
    /// 자체가 모호해지거나 성립하지 않는다. 그런 경우 첫 OPEN·첫 CLOSE만 기준으로 삼고
    /// 그 구간 밖의 RETURN은 세지 않는다 - 과소 포착(Minor)이 거짓 행(Critical)보다 낫다.
    /// </summary>
    public static class CursorLifecycleExtractor
    {
        public static IReadOnlyList<CursorLifecycleFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<CursorLifecycleFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<CursorLifecycleFact>();
                }

                var visitor = new CursorVisitor();
                fragment.Accept(visitor);

                var facts = new List<CursorLifecycleFact>();
                foreach (var declaration in visitor.Declarations)
                {
                    var openLine = visitor.OpenLineOf(declaration.Name);
                    var closeLine = visitor.CloseLineOf(declaration.Name);

                    // "OPEN과 CLOSE 사이"는 첫 OPEN보다 뒤에 오는 첫 CLOSE가 있을 때만
                    // 성립한다. CLOSE가 없거나(closeLine == 0) OPEN보다 앞서면(어긋난
                    // 순서) 이 관측 자체를 하지 않는다.
                    var unclosed = openLine > 0
                        && closeLine > openLine
                        && visitor.ReturnLines.Any(l => l > openLine && l < closeLine);

                    // 범위 문장은 LOCAL도 GLOBAL도 명시되지 않은 경우에만 낼 수 있다 - 그때만
                    // 범위가 실제로 DB의 default_to_local_cursor 설정에 달려 있다. GLOBAL이
                    // 명시되면 범위는 그 설정과 무관하게 전역으로 확정되므로, 이 문장은 거짓이
                    // 된다. GLOBAL에 대해 새 문장을 지어내는 대신 침묵한다 - 이 추출기의
                    // 다른 네 자리와 같은 침묵 계약(I1, 2026-08-22 최종 브랜치 리뷰).
                    var needsScopeSentence = !declaration.IsLocal && !declaration.IsGlobal;

                    if (!unclosed && !needsScopeSentence) continue;

                    var parts = new List<string>();
                    if (unclosed)
                    {
                        parts.Add("OPEN과 CLOSE 사이에 RETURN이 있어 이 경로로 실행이 종료되면 "
                            + "CLOSE/DEALLOCATE에 도달하지 않습니다");
                    }
                    if (needsScopeSentence)
                    {
                        parts.Add("CURSOR 선언에 LOCAL이 지정되지 않아 커서 범위가 데이터베이스의 "
                            + "default_to_local_cursor 설정에 달려 있습니다");
                    }

                    facts.Add(new CursorLifecycleFact(
                        declaration.Line, declaration.Name, string.Join(". ", parts) + "."));
                }

                return facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[CursorLifecycleExtractor] 커서 수명 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<CursorLifecycleFact>();
            }
        }

        private sealed record CursorDeclaration(int Line, string Name, bool IsLocal, bool IsGlobal);

        // DeclareCursorStatement/OpenCursorStatement/CloseCursorStatement/ReturnStatement를
        // 직접 방문한다. 최상위 StatementList뿐 아니라 BEGIN…END 안, IF/WHILE 블록 안에
        // 있는 문장도 그대로 방문된다. 별도의 StatementList 오버라이드가 필요 없다 -
        // Visit(StatementList)를 오버라이드해도 기본 ExplicitVisit이 AcceptChildren을
        // 호출하므로(RowCountBoundaryExtractor와 같은 근거) 자식 방문은 그대로 계속된다.
        //
        // [정정 - I2 Fix Round 1, 2026-08-22 최종 브랜치 리뷰] 이전 버전이 "Accept(visitor)는
        // visitor.Visit(this)와 this.AcceptChildren(visitor)을 둘 다 무조건 수행한다 -
        // 소비자가 Visit(T)를 어떻게 오버라이드하든 하강을 끊을 방법이 없다"고 적었는데,
        // 이것도 틀렸다 - 방향만 반대로 틀렸다. 실제 관계는 Accept(visitor) →
        // visitor.ExplicitVisit(node)(가상 메서드) → 오버라이드하지 않으면 프레임워크가
        // 만들어 둔 기본 ExplicitVisit이 Visit(this)(순회 알림, 자식 방문에 관여하지
        // 않음)와 AcceptChildren(visitor)(실제 하강)을 둘 다 부른다. 즉 하강을 실제로
        // 제어하는 지점은 Visit(T)가 아니라 ExplicitVisit(T)다 - ExplicitVisit(T)를
        // base(또는 AcceptChildren) 호출 없이 오버라이드하면 하강이 실제로 끊긴다. 이
        // 저장소의 SqlStaticParser.NamedTableCollector.ExplicitVisit(QueryDerivedTable)와
        // ColumnReferenceCollector.ExplicitVisit(ScalarSubquery)가 정확히 그 용도로 base
        // 없이 오버라이드돼 있고("Visit이 아니라 ExplicitVisit을 비워야 한다 - Visit은
        // 순회 중 호출되는 알림일 뿐이라 비워도 자식으로 계속 내려간다"), 그 차단에 의존하는
        // SqlStaticParserTests.Analyze_SetRightHandScalarSubquery_IsNotASelfReference가
        // 통과 중인 테스트로 못박는다. 이 리포에서 세 갈래로 직접 실험해도 같은 결과다 -
        // Visit(StatementList)만 빈 본문으로 오버라이드하면(ExplicitVisit은 그대로 두면)
        // 하강이 계속되고, ExplicitVisit(StatementList)를 base 호출 없이 오버라이드하면
        // 하강이 끊기며, 그 안에서 base.ExplicitVisit(node)를 부르면 다시 하강한다. 이
        // CursorVisitor는 ExplicitVisit을 어디도 오버라이드하지 않으므로(Visit(T)만
        // 쓴다) 이 클래스의 원래 결론 - 별도 StatementList 처리가 필요 없다 - 은 여전히
        // 유효하다.
        private sealed class CursorVisitor : TSqlFragmentVisitor
        {
            private readonly Dictionary<string, int> _opens = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _closes = new(StringComparer.OrdinalIgnoreCase);

            public List<CursorDeclaration> Declarations { get; } = new();
            public List<int> ReturnLines { get; } = new();

            // 같은 이름이 여러 번 OPEN/CLOSE되면 첫 발생만 기준으로 삼는다 - 이후
            // 재오픈 구간의 RETURN을 첫 구간에 속한다고 잘못 판정하지 않기 위함이다.
            public int OpenLineOf(string name) => _opens.TryGetValue(name, out var l) ? l : 0;
            public int CloseLineOf(string name) => _closes.TryGetValue(name, out var l) ? l : 0;

            public override void Visit(DeclareCursorStatement node)
            {
                var name = node.Name?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;

                var options = node.CursorDefinition?.Options;
                var isLocal = options?.Any(o => o.OptionKind == CursorOptionKind.Local) == true;
                var isGlobal = options?.Any(o => o.OptionKind == CursorOptionKind.Global) == true;

                Declarations.Add(new CursorDeclaration(node.StartLine, name!, isLocal, isGlobal));
            }

            public override void Visit(OpenCursorStatement node)
            {
                var name = node.Cursor?.Name?.Value;
                if (!string.IsNullOrWhiteSpace(name) && !_opens.ContainsKey(name!))
                {
                    _opens[name!] = node.StartLine;
                }
            }

            public override void Visit(CloseCursorStatement node)
            {
                var name = node.Cursor?.Name?.Value;
                if (!string.IsNullOrWhiteSpace(name) && !_closes.ContainsKey(name!))
                {
                    _closes[name!] = node.StartLine;
                }
            }

            public override void Visit(ReturnStatement node) => ReturnLines.Add(node.StartLine);
        }
    }
}
