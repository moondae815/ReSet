using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">CAST 식의 줄 번호.</param>
    /// <param name="Expression">CAST 식 원문.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record TypePathFact(int Line, string Expression, string Sentence);

    /// <summary>
    /// CAST(&lt;산술식&gt; AS INT)의 피연산자 타입 경로를 판정한다.
    ///
    /// [실행으로 확정한 사실 - 2026-08-22, SQL Server 2022 16.0.4255.1]
    /// decimal/numeric이 money보다 데이터 형식 우선순위가 높다. 리터럴 100.0은
    /// numeric(4,1)이므로, 식 안 어디에라도 numeric/decimal 피연산자가 하나라도
    /// 있으면 money 피연산자가 numeric으로 승격돼 결과가 0 방향으로 절사되고,
    /// 그런 피연산자가 전혀 없이 money만 있으면 money * money가 남아 0에서 먼
    /// 쪽으로 반올림된다. 같은 값(10050 x 1.50%)이 앞은 150, 뒤는 151이다.
    /// 이 두 문장만 실행으로 확인했다 - 그 외 조합(아래 참고)은 방향을 단정하지
    /// 않고 행을 생략한다.
    ///
    /// [범위 - CAST만, CONVERT는 다루지 않는다] 계획서 실측이 CAST(... AS INT)만
    /// 다뤘고, CONVERT는 스타일 인자(세 번째 인자)에 따라 동작이 갈릴 수 있는데
    /// 그 갈림을 실행으로 확인한 적이 없다. 범위를 넓힐 근거가 없으므로 CONVERT는
    /// 이 추출기가 보지 않는다 - 넓히려면 별도 실측이 먼저 있어야 한다.
    ///
    /// [우선순위 규칙을 어디까지 구현했는가] T-SQL 데이터 형식 우선순위 전체를
    /// 구현하면 좁은 실측을 일반 규칙으로 잘못 승격할 위험이 크다. 이 추출기가
    /// "안다"고 취급하는 타입은 다음 다섯 계열뿐이다: money/smallmoney,
    /// decimal/numeric, int/smallint/tinyint/bigint, float/real, 그리고 numeric
    /// 리터럴(소수점이 있는 리터럴)·int 리터럴. 이 목록 밖의 타입 이름
    /// (varchar/datetime/bit/uniqueidentifier 등, 그리고 <see cref="ExecutionSemanticsFacts.BuildColumnTypeMap"/>이
    /// 넣는 "(모호)" 표시)은 전부 "모르는 것"으로 처리한다.
    ///
    /// [컬럼 타입 문자열의 괄호] `DbMetadataService`가 만드는 컬럼 타입은
    /// decimal/numeric에 정밀도·자릿수를 괄호로 붙인다("decimal(18,2)") - money·
    /// smallmoney·int 계열·float/real은 괄호 없이 맨 이름 그대로 온다. 화이트리스트와
    /// 대조하기 전에 괄호 앞만 잘라 낸다(변수·파라미터 선언 타입은 애초에 괄호가
    /// 없어 이 자르기가 영향을 주지 않는다).
    ///
    /// float/real은 money·decimal/numeric보다 우선순위가 더 높지만, 그 조합이
    /// 최종적으로 어느 방향으로 반올림/절사되는지는 실행으로 확인한 적이 없다.
    /// 그래서 float/real은 "아는 타입"으로 인식은 하되, 식에 하나라도 섞이면 결과
    /// 방향을 단정하지 않고 행을 생략한다. int 계열끼리만 있는 경우(방향 갈림과
    /// 무관해 실을 사실이 없다)도 같은 자리에서 조용히 생략한다 - 이 둘은 잎 타입을
    /// "몰라서" 생략하는 것이 아니라 방향을 확정할 근거가 없어서/실을 내용이 없어서
    /// 생략하는 것이라 아래 로그(M-c)의 대상이 아니다.
    ///
    /// [왜 잎 타입을 하나라도 모르면 행을 내지 않는가] 기계 확정 표에 추측이
    /// 섞이면 표 전체의 신뢰가 무너진다. 컬럼·변수·파라미터·리터럴 중 하나라도
    /// 타입을 모르면(선언을 못 찾음, 함수 호출·서브쿼리 등 이 추출기가 다루지
    /// 않는 식 모양, 위 다섯 계열 밖의 타입, "(모호)" 컬럼, 한정자를 의존성
    /// 테이블에 묶을 수 없는 컬럼) 그 CAST는 침묵한다 - 실패 방향이 안전한
    /// 쪽이다. 이 경우는 `Log.Debug`를 남긴다(M-c) - 설계 문서(2026-08-22-
    /// spec-machine-facts-design.md:90-91)가 "하나라도 모르면 행을 생략하고
    /// 로그만 남긴다"고 정했다.
    /// </summary>
    public static class ExpressionTypePathExtractor
    {
        public const string MoneyRoundingSentence =
            "피연산자가 money로 유지되어 money → int 변환입니다. 0에서 먼 쪽으로 반올림합니다(12.5 → 13, -12.5 → -13).";

        /// <summary>M-a 수정 라운드(리뷰): smallmoney 식에 money 이름을 대면 안 된다 - 실측으로 smallmoney도 반올림 방향은 같지만 이름은 실제 타입을 대야 한다.</summary>
        public const string SmallMoneyRoundingSentence =
            "피연산자가 smallmoney로 유지되어 smallmoney → int 변환입니다. 0에서 먼 쪽으로 반올림합니다(12.5 → 13, -12.5 → -13).";

        public const string NumericTruncationSentence =
            "피연산자에 numeric/decimal이 있어 승격되어 numeric → int 변환입니다. 0 방향으로 절사합니다(12.5 → 12, -12.5 → -12).";

        /// <summary>M-b 수정 라운드(리뷰): money가 전혀 없이 애초에 numeric/decimal이면 "승격"이 일어난 적이 없다 - 방향은 같지만 원인절이 사실이어야 한다.</summary>
        public const string NumericNoPromotionSentence =
            "피연산자가 numeric/decimal입니다. numeric → int 변환입니다. 0 방향으로 절사합니다(12.5 → 12, -12.5 → -12).";

        /// <summary>
        /// 이 추출기가 "안다"고 취급하는 타입 이름 다섯 계열. 이 밖의 타입 이름은
        /// (그리고 "(모호)" 표시도) 전부 알 수 없는 잎으로 취급해 행을 생략한다.
        /// </summary>
        private static readonly HashSet<string> KnownTypeFamilies = new(StringComparer.Ordinal)
        {
            "money", "smallmoney",
            "decimal", "numeric",
            "int", "smallint", "tinyint", "bigint",
            "float", "real",
        };

        /// <summary>데이터 형식 우선순위(높은 것이 먼저) - 위 다섯 계열 안에서만 유효하다.</summary>
        private static readonly string[] PrecedenceOrder =
        {
            "float", "real",
            "decimal", "numeric",
            "money", "smallmoney",
            "bigint", "int", "smallint", "tinyint",
        };

        /// <summary>
        /// 파서 오류 정책: 오류가 하나라도 있으면 빈 목록(소프트 페일). 부분 파스
        /// 결과가 기계 확정 표에 섞이면 표 전체의 신뢰가 무너지기 때문이다.
        /// </summary>
        public static IReadOnlyList<TypePathFact> Extract(
            string? ddlText, IReadOnlyDictionary<string, string> columnTypes)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<TypePathFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<TypePathFact>();
                }

                var declared = new DeclaredTypeVisitor();
                fragment.Accept(declared);

                var cteNames = new CteNameCollector();
                fragment.Accept(cteNames);

                var tableSafety = new TableSafetyVisitor(cteNames.Names);
                fragment.Accept(tableSafety);

                var visitor = new CastVisitor(
                    declared.Types, columnTypes, tableSafety.SafeQualifiers, tableSafety.UnsafeQualifiers);
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[ExpressionTypePathExtractor] 타입 경로 판정 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<TypePathFact>();
            }
        }

        /// <summary>파라미터와 지역 변수의 선언 타입을 모은다.</summary>
        private sealed class DeclaredTypeVisitor : TSqlFragmentVisitor
        {
            public Dictionary<string, string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);

            // Visit(T)를 오버라이드해도 기본 ExplicitVisit이 AcceptChildren을 계속
            // 호출하므로(AggregateAssignmentExtractor·RowCountBoundaryExtractor와 같은
            // 근거로 이 배치에서 실측 확인) DECLARE/파라미터 목록 전부가 방문된다.
            public override void Visit(DeclareVariableElement node) => Record(node.VariableName?.Value, node.DataType);

            public override void Visit(ProcedureParameter node) => Record(node.VariableName?.Value, node.DataType);

            private void Record(string? name, DataTypeReference? type)
            {
                var typeName = (type as SqlDataTypeReference)?.SqlDataTypeOption.ToString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(typeName)) return;
                Types[name!] = typeName!.ToLowerInvariant();
            }
        }

        /// <summary>CTE 이름을 프래그먼트 전체에서 모은다(수집 순서와 무관하게 만들려고 별도 패스로 둔다).</summary>
        private sealed class CteNameCollector : TSqlFragmentVisitor
        {
            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(CommonTableExpression node)
            {
                if (!string.IsNullOrWhiteSpace(node.ExpressionName?.Value))
                {
                    Names.Add(node.ExpressionName!.Value);
                }
            }
        }

        /// <summary>
        /// I2 수정 라운드(리뷰): 컬럼 참조 `t.Amt`의 `t`가 진짜 영속 테이블을 가리키는지
        /// 판정한다. `columnTypes`(=`BuildColumnTypeMap`의 평면화된 컬럼명→타입 사전)는
        /// 어느 의존성 테이블 소속인지 정보를 잃는다 - 그래서 파생 테이블·CTE·임시
        /// 테이블·테이블 변수의 별칭이 의존성 테이블의 컬럼명과 우연히 같으면, 존재하지도
        /// 않는 컬럼에 대해 "기계 확정" 행이 날 수 있다(이 과제의 핵심 계약 위반).
        ///
        /// [왜 프래그먼트 전체를 스코프 구분 없이 훑는가] 별칭은 쿼리 스코프마다 다시
        /// 선언될 수 있어 원칙적으로는 스코프별로 풀어야 정확하다. 이 수정 라운드의 쓰기
        /// 집합 안에서 스코프 인식 별칭 해소를 새로 구현하는 대신, 프래그먼트 전체에서
        /// 한 번이라도 그 이름이 파생 테이블·CTE·임시 테이블·테이블 변수의 별칭으로
        /// 쓰였으면 그 이름 전체를 안전하지 않은 것으로 취급한다(<see cref="CastVisitor"/>가
        /// 안전/불안전 판정 시 "불안전이 하나라도 있으면 진다"로 병합한다). 실수 방향은
        /// 항상 "더 침묵한다"이지 "잘못 믿는다"가 아니다 - 코퍼스 실측(파생 테이블+CAST
        /// 4개 객체, 임시 테이블·테이블 변수+CAST 0개)에서 이 과잉 침묵의 비용은 낮다.
        ///
        /// [TableReferenceWithAlias 하나만 오버라이드하는 이유] `SqlStaticParser.
        /// AliasTargetFinder`와 같은 근거다 - ScriptDom의 `ExplicitVisit`은 노드 하나에
        /// 대해 그 상속 사슬에 있는 모든 타입의 `Visit` 오버로드를 호출하므로, 별칭을
        /// 가질 수 있는 모든 참조 타입(`NamedTableReference`·`QueryDerivedTable`·
        /// `VariableTableReference` 등)이 이 오버로드 하나로 잡힌다.
        /// </summary>
        private sealed class TableSafetyVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _cteNames;

            public TableSafetyVisitor(HashSet<string> cteNames) => _cteNames = cteNames;

            public HashSet<string> SafeQualifiers { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> UnsafeQualifiers { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(TableReferenceWithAlias node)
            {
                if (node is NamedTableReference named)
                {
                    var baseName = named.SchemaObject?.BaseIdentifier?.Value;
                    var qualifier = named.Alias?.Value ?? baseName;
                    if (string.IsNullOrWhiteSpace(qualifier)) return;

                    var isTemp = !string.IsNullOrEmpty(baseName)
                        && baseName!.StartsWith("#", StringComparison.Ordinal);
                    var isCte = !string.IsNullOrEmpty(baseName) && _cteNames.Contains(baseName!);

                    if (isTemp || isCte)
                    {
                        UnsafeQualifiers.Add(qualifier!);
                    }
                    else
                    {
                        SafeQualifiers.Add(qualifier!);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(node.Alias?.Value))
                {
                    // QueryDerivedTable(파생 테이블) · VariableTableReference(테이블
                    // 변수) · OpenQueryTableReference 등 - 전부 영속 테이블이 아니다.
                    UnsafeQualifiers.Add(node.Alias!.Value);
                }
            }
        }

        private sealed class CastVisitor : TSqlFragmentVisitor
        {
            private readonly IReadOnlyDictionary<string, string> _variables;
            private readonly IReadOnlyDictionary<string, string> _columns;
            private readonly HashSet<string> _safeQualifiers;
            private readonly HashSet<string> _unsafeQualifiers;

            public CastVisitor(
                IReadOnlyDictionary<string, string> variables,
                IReadOnlyDictionary<string, string> columns,
                HashSet<string> safeQualifiers,
                HashSet<string> unsafeQualifiers)
            {
                _variables = variables;
                _columns = columns;
                _safeQualifiers = safeQualifiers;
                _unsafeQualifiers = unsafeQualifiers;
            }

            public List<TypePathFact> Facts { get; } = new();

            // CastCall은 스칼라 식이지 컨테이너 문장 노드가 아니다. Visit(T)를
            // 오버라이드해도 자식 순회는 끊기지 않으므로(위와 같은 근거) SELECT 목록·
            // SET·RETURN·파생 테이블 서브쿼리 등 어느 자리의 CAST든, 그리고 CAST
            // 안에 중첩된 또 다른 CAST까지 전부 이 메서드로 들어온다.
            public override void Visit(CastCall node)
            {
                var target = (node.DataType as SqlDataTypeReference)?.SqlDataTypeOption;
                if (target != SqlDataTypeOption.Int) return;

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var resolved = ResolveType(node.Parameter, seen);
                if (resolved == null)
                {
                    // M-c 수정 라운드(리뷰): 설계 문서(2026-08-22-spec-machine-facts-design.md:
                    // 90-91)는 "하나라도 모르면 행을 생략하고 로그만 남긴다"고 정한다. 이
                    // 로그가 없으면 침묵한 CAST와 애초에 없던 CAST가 구분되지 않는다. SP
                    // 하나에 CAST가 27개인 곳도 있어 Debug로 둔다(운영 로그를 채우지 않되,
                    // 필요할 때 켜서 확인할 수 있게).
                    Log.Debug(
                        "[ExpressionTypePathExtractor] {Line}행의 CAST(... AS INT) 식 {Expression} - "
                        + "잎 타입 중 하나 이상을 알 수 없어 표에서 침묵합니다.",
                        node.StartLine, TargetText(node));
                    return;
                }

                string sentence;
                if (resolved == "decimal" || resolved == "numeric")
                {
                    // M-b: money가 실제로 섞여 있을 때만 "승격되어"가 사실이다 - 애초에
                    // numeric/decimal뿐이었으면(seen에 money/smallmoney가 없으면) 승격이
                    // 일어난 적이 없다.
                    var promotedFromMoney = seen.Contains("money") || seen.Contains("smallmoney");
                    sentence = promotedFromMoney ? NumericTruncationSentence : NumericNoPromotionSentence;
                }
                else if (resolved == "money")
                {
                    sentence = MoneyRoundingSentence;
                }
                else if (resolved == "smallmoney")
                {
                    // M-a: 식에 없는 타입 이름("money")을 대면 "기계 확정" 행을
                    // 감사자나 모델이 고치려 든다 - 실제 타입 이름을 대야 한다.
                    sentence = SmallMoneyRoundingSentence;
                }
                else
                {
                    // 여기 남는 것은 int 계열끼리만 있는 경우(실을 확정 사실이 없다)와
                    // float/real이 섞여 결과 타입이 된 경우(우선순위상 가장 높지만, 그 뒤
                    // int로 어느 방향으로 변환되는지는 실행으로 확인한 적이 없어 단정하지
                    // 않는다) 둘 다다.
                    return;
                }

                Facts.Add(new TypePathFact(node.StartLine, TargetText(node), sentence));
            }

            /// <summary>
            /// I3 수정 라운드(리뷰): CAST 식은 다섯 종류 중 유일하게 산술식 통째가
            /// `Target`에 실려 여러 줄에 걸칠 확률이 가장 높다. `MechanicalValidator.
            /// CheckExecutionSemantics`는 렌더되지 않은 이 `Target`을, 렌더 파이프라인
            /// (`AiService.EscapeTableCell` → `MarkdownTableCellCodec.Escape`)을 거친 뒤
            /// 다시 셀로 쪼갠(`MarkdownTableCellCodec.SplitRow`) 문자열과 `==`로 원문 그대로
            /// 비교한다(MechanicalValidator.cs:3494-3503). `Escape`가 접는 것은 `\r\n`·`\n`·
            /// `\r` 세 가지뿐이고 연속 공백은 접지 않으며, `SplitRow`가 되돌리는 것은 `\|`
            /// 뿐이다 - 그래서 여기서 접어야 할 것도 그 세 가지뿐이다(추측이 아니라
            /// MarkdownTableCellCodec.cs 실측). 이걸 접지 않으면 원본 그대로 베껴 옮겨도
            /// 영원히 일치하지 않는다.
            /// </summary>
            private static string TargetText(TSqlFragment node)
            {
                return CaseBranchExtractor.TextOf(node)
                    .Replace("\r\n", " ")
                    .Replace("\n", " ")
                    .Replace("\r", " ");
            }

            /// <summary>
            /// CAST 인자 식을 재귀로 내려가며 각 잎의 타입을 결정하고, 이항 연산에서는
            /// 데이터 형식 우선순위로 두 피연산자를 합친다. 리프가 다섯 계열 밖이거나
            /// (함수 호출·서브쿼리·문자열 리터럴 등) 이 함수가 다루지 않는 식 모양이면
            /// null(모른다)을 돌려주고, null은 상위로 그대로 전파된다.
            /// </summary>
            private string? ResolveType(ScalarExpression? expression, HashSet<string> seen)
            {
                switch (expression)
                {
                    case null:
                        return null;

                    case ParenthesisExpression paren:
                        return ResolveType(paren.Expression, seen);

                    case BinaryExpression binary:
                        var left = ResolveType(binary.FirstExpression, seen);
                        if (left == null) return null;
                        var right = ResolveType(binary.SecondExpression, seen);
                        if (right == null) return null;
                        return Combine(left, right);

                    case VariableReference variable:
                        return Record(KnownFamilyOrNull(variable.Name, _variables), seen);

                    case ColumnReferenceExpression column:
                        return Record(ResolveColumn(column), seen);

                    case NumericLiteral:
                        // 소수점이 있는 리터럴(예: 100.0)은 numeric(p,s)이다.
                        return Record("numeric", seen);

                    case IntegerLiteral:
                        return Record("int", seen);

                    default:
                        // FunctionCall·ScalarSubquery·StringLiteral·MoneyLiteral·
                        // UnaryExpression·CastCall 등 - 이 추출기가 방향을 단정할
                        // 근거가 없는 식 모양은 전부 모르는 것으로 취급한다.
                        return null;
                }
            }

            /// <summary>
            /// M-b 수정 라운드(리뷰): `Combine`이 반환하는 최종 결과 하나만으로는
            /// "money가 실제로 섞였는지"를 잃는다(예: money*numeric → numeric으로 이겨도
            /// 결과 문자열만 보면 처음부터 numeric이었는지 구분이 안 된다). 잎마다
            /// 실제로 관측한 계열을 `seen`에 그대로 남겨, 승격 여부를 사실대로 말할 수
            /// 있게 한다.
            /// </summary>
            private static string? Record(string? family, HashSet<string> seen)
            {
                if (family != null) seen.Add(family);
                return family;
            }

            /// <summary>
            /// I2 수정 라운드(리뷰): 한정자(별칭 또는 테이블명)가 있는 컬럼 참조는
            /// `TableSafetyVisitor`가 안전(진짜 영속 테이블)으로 확인한 경우에만
            /// `columnTypes`의 평면화된 맵을 신뢰한다. 안전/불안전 어느 쪽에도 없거나
            /// (스코프 무관 훑기가 그 이름을 전혀 못 봤다) 불안전으로 확인됐으면(파생
            /// 테이블·CTE·임시 테이블·테이블 변수) - 판단 기준은 "이 컬럼 참조를
            /// 의존성 테이블에 묶을 수 있는가" 하나다. 묶을 수 없으면 침묵한다.
            /// 한정자가 없는 컬럼(예: 단일 FROM의 `Amt`)은 이 게이트를 타지 않는다 -
            /// 그 경우의 모호성은 `BuildColumnTypeMap`의 "(모호)" 표시가 이미 감당한다.
            /// </summary>
            private string? ResolveColumn(ColumnReferenceExpression column)
            {
                var identifiers = column.MultiPartIdentifier?.Identifiers;
                var name = identifiers?.LastOrDefault()?.Value;
                if (string.IsNullOrWhiteSpace(name)) return null;

                if (identifiers!.Count >= 2)
                {
                    var qualifier = identifiers[identifiers.Count - 2]?.Value;
                    if (string.IsNullOrWhiteSpace(qualifier)) return null;

                    var isSafe = _safeQualifiers.Contains(qualifier!) && !_unsafeQualifiers.Contains(qualifier!);
                    if (!isSafe) return null;
                }

                return KnownFamilyOrNull(name, _columns);
            }

            private static string? KnownFamilyOrNull(string? name, IReadOnlyDictionary<string, string> dictionary)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                if (!dictionary.TryGetValue(name!, out var type)) return null;

                var lowered = StripSizeSuffix(type).ToLowerInvariant();
                // "(모호)"도 이 경로로 자연스럽게 걸러진다 - 괄호를 잘라 내면 빈
                // 문자열이 되고, 빈 문자열은 다섯 계열 어디에도 속하지 않기 때문이다.
                return KnownTypeFamilies.Contains(lowered) ? lowered : null;
            }

            /// <summary>
            /// I1 수정 라운드(리뷰): 변수·파라미터 선언 타입(DeclaredTypeVisitor)은
            /// `SqlDataTypeOption.ToString()`이라 항상 맨 이름("decimal")이지만, 컬럼
            /// 타입은 `DbMetadataService.GetTableColumnsAsync`(:898-907)와 스칼라
            /// 반환값·테이블 파라미터 조회(:334, :365)가 만든다 - 이 셋 모두 decimal/
            /// numeric에는 정밀도·자릿수를 괄호로 붙인다("decimal(18,2)"). 괄호 앞만
            /// 잘라 화이트리스트와 대조해야 그 경로가 열린다. money/smallmoney/int
            /// 계열/float/real은 이 조회가 `ELSE ''`라 원래도 맨 이름 그대로 온다 -
            /// 그 타입들에는 이 자르기가 아무 효과가 없다(괄호가 없으니 자를 것도 없다).
            /// </summary>
            private static string StripSizeSuffix(string type)
            {
                var parenIndex = type.IndexOf('(');
                return parenIndex < 0 ? type : type[..parenIndex];
            }

            private static string Combine(string a, string b)
            {
                var indexA = Array.IndexOf(PrecedenceOrder, a);
                var indexB = Array.IndexOf(PrecedenceOrder, b);
                return indexA <= indexB ? a : b;
            }
        }
    }
}
