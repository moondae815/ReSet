using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정적 분석과 스키마 메타데이터가 확정한 사실 중 L1이 명세서 본문과 기계적으로
    /// 대조할 것들.
    ///
    /// MechanicalValidator에 두지 않는 이유: 기대값 <b>생성</b>은 정적 분석과 의존성을
    /// 읽는 일이고 <b>소비</b>는 검증기의 일이다. 나눠 두면 검증기가 SpDefinition을
    /// 몰라도 된다.
    /// </summary>
    /// <param name="UpdateColumns">정적 파서가 확정한 UPDATE SET 대상 컬럼.</param>
    /// <param name="PromptSchemaColumns">
    /// 테이블별로 프롬프트 스키마 표에 실제로 실린 컬럼. 키는 canonical 3-part 이름이다.
    /// 이것이 거짓 부재 주장 대조의 기준이다 - DB 전체 컬럼이 아니다. 정당하게 필터에서
    /// 빠진 컬럼을 기준으로 삼으면 재생성으로 고칠 수 없는 오류가 생긴다.
    /// </param>
    /// <param name="ColumnlessDependencyTables">
    /// 컬럼이 0개라 PromptSchemaColumns에서 제외된 의존성들의 canonical 이름.
    /// 대조 기준(PromptSchemaColumns)에는 넣지 않는다 - 스키마 표 자체가 렌더링되지
    /// 않으므로 이 테이블에 대한 "제공되지 않았습니다" 진술은 참이다. 그러나
    /// MechanicalValidator.ResolveSchemaTableKey의 말단 이름 모호성 판정에는 넣어야
    /// 한다 - 그렇지 않으면 컬럼 0개 테이블을 가리킨 문장·표 행이, 같은 말단 이름을
    /// 가진 컬럼 있는 동명 테이블로 조용히 오귀속된다(리뷰 실측: DB1.dbo.TSettleMst와
    /// DB2.dbo.TSettleMst 중 DB2만 메타데이터가 수집되지 않은 경우).
    /// </param>
    /// <param name="InputDefects">
    /// 프롬프트가 진실을 담지 못한 경우의 서술. <b>L1 오류가 아니다</b> - 재생성이
    /// 고칠 수 없는 코드 버그이므로 호출부가 경고로 표면화한다.
    /// </param>
    public sealed record SpecExpectations(
        IReadOnlyList<UpdateColumnExpectation> UpdateColumns,
        IReadOnlyDictionary<string, IReadOnlySet<string>> PromptSchemaColumns,
        IReadOnlySet<string> ColumnlessDependencyTables,
        IReadOnlyList<string> InputDefects)
    {
        /// <summary>원본이 3부 이상으로 표기한 테이블 참조가 하나라도 있는가.</summary>
        public bool HasThreePartReference { get; init; }

        /// <summary>원본에 Linked Server(4부) 참조가 있는가.</summary>
        public bool HasLinkedServerReference { get; init; }

        /// <summary>명세서가 옮겨야 할 원본 주석. 앵커가 있는 항목만 L1이 대조한다.</summary>
        public IReadOnlyList<SourceCommentBlock> SourceComments { get; init; }
            = Array.Empty<SourceCommentBlock>();

        /// <summary>원본의 3인자 ROUND 호출. 값 매핑 기술 여부를 L1이 본다.</summary>
        public IReadOnlyList<RoundingCall> RoundingCalls { get; init; } = Array.Empty<RoundingCall>();

        /// <summary>프로시저 본문의 세션 옵션 이름. 배치 앞머리의 것은 담지 않는다.</summary>
        public IReadOnlyList<string> SessionOptions { get; init; } = Array.Empty<string>();

        /// <summary>DML 문장별 적용 범위. 명세서가 이 표를 그대로 옮겼는지 L1이 본다.</summary>
        public IReadOnlyList<DmlScopeFact> DmlScopeFacts { get; init; } = Array.Empty<DmlScopeFact>();

        /// <summary>
        /// DML 최상위 WHERE의 IN/NOT IN 리터럴 집합. CheckSetPredicates가 소비한다.
        /// </summary>
        public IReadOnlyList<SetPredicateFact> SetPredicates { get; init; } = Array.Empty<SetPredicateFact>();

        /// <summary>
        /// DML 문장이 부르는 사용자 함수 호출. CheckReferencedFunctions가 소비한다.
        ///
        /// 이름 집합을 Dependencies에서 뽑는 규칙은 AiService가 프롬프트 표를 만들 때
        /// 쓰는 것과 <b>같아야 한다</b> - 두 곳이 갈리면 모델이 표를 그대로 베껴도
        /// L1이 틀렸다고 하는 재현 불가능한 실패가 난다(기준일 파라미터가 같은 이유로
        /// 한 규칙을 공유하는 것과 같다).
        /// </summary>
        public IReadOnlyList<ReferencedFunctionCallFact> ReferencedFunctionCalls { get; init; }
            = Array.Empty<ReferencedFunctionCallFact>();

        /// <summary>
        /// DML 문장이 읽는 자리와 그 잠금 힌트. CheckLockHints가 소비한다. 참조 단위
        /// (문장 × 스캔 자리)라 DmlScopeFacts와 개수가 다를 수 있다 - LockHintFact 문서
        /// 참고.
        /// </summary>
        public IReadOnlyList<LockHintFact> LockHints { get; init; } = Array.Empty<LockHintFact>();

        /// <summary>
        /// 함수 선언부의 WITH 옵션. 프로시저에는 이 옵션 자체가 없으므로 항상 null이다.
        /// CheckObjectDeclaration이 소비한다.
        /// </summary>
        public ObjectDeclarationExtractor.ObjectDeclarationFact? ObjectDeclaration { get; init; }

        /// <summary>
        /// UPDATE/INSERT/DELETE FROM 절 파생 테이블의 컬럼 정의. SET(또는 SELECT)
        /// 우변이 별칭.컬럼 참조에서 멈추고 그 정의를 어디에도 적지 않는 것을
        /// 막는다 - 이번 감사의 유일한 축 A 🔴(EXCEPTION_PROC의 X.PGCOMM).
        /// </summary>
        public IReadOnlyList<DerivedColumnDefinition> DerivedColumns { get; init; }
            = Array.Empty<DerivedColumnDefinition>();

        /// <summary>
        /// 원본 DDL에 동적 SQL이 아닌, 이름이 고정된 저장 프로시저 EXEC 호출이 있는가.
        /// 헤더 주석이 "내부 SP 호출 없음"이라 선언했는데 실제로는 있는 모순을 잡는
        /// 판정에만 쓴다.
        /// </summary>
        public bool HasInternalProcedureCall { get; init; }

        /// <summary>
        /// 실행 의미 표의 행. CheckExecutionSemantics가 소비한다.
        ///
        /// 프롬프트(AiService)와 같은 Collect를 불러야 한다 - 두 곳이 갈리면 모델이
        /// 표를 그대로 베껴도 L1이 틀렸다고 하는 재현 불가능한 실패가 난다.
        /// </summary>
        public IReadOnlyList<ExecutionSemanticFact> ExecutionSemantics { get; init; }
            = Array.Empty<ExecutionSemanticFact>();

        /// <summary>CASE 분기 원문. CheckCaseBranches가 소비한다.</summary>
        public IReadOnlyList<CaseBranchFact> CaseBranches { get; init; }
            = Array.Empty<CaseBranchFact>();

        /// <summary>
        /// 파서가 확정한 INSERT 대상 테이블(canonical 표기). 매핑 표의 테이블명 칸이
        /// 이것과 표기까지 같은지 대조하는 기준이다.
        /// </summary>
        public IReadOnlyList<string> InsertTargetTables { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 테이블별로 의존성 스키마가 널 허용으로 확정한 컬럼의 말단 이름. 키는
        /// PromptSchemaColumns와 같은 canonical 3-part 이름이다. 명세서가 "널을
        /// 허용하지 않습니다"로 단정한 줄을 대조하는 기준이다.
        ///
        /// [Fix Round 1 - 왜 테이블 단위인가] 1라운드 구현은 컬럼 이름을 테이블 구분
        /// 없이 평평한 집합 하나로 모았고, 이름이 같은데 널 허용 여부가 테이블마다
        /// 갈리면 그 이름을 통째로 버렸다. 실측(UF_GET_COMM4PG4INTEREST)에서
        /// `TCardContractMgmt.UseState`는 NOT NULL, `TFreeInterestInstCommission.UseState`는
        /// 널 허용이라 이 감사가 잡아야 할 바로 그 결함(후자를 NOT NULL로 단정한 명세서
        /// 문장)이 조용히 버려졌다 - 리뷰 Critical로 실측. 테이블별로 나누면 두
        /// UseState가 서로를 가리지 않는다.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlySet<string>> NullableColumnsByTable { get; init; } =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 대조할 것이 하나도 없으면 null을 돌려준다. 호출부가 null 검사를 하지 않고
        /// 그대로 넘길 수 있게 하기 위해서다 - Validate는 null을 "종전 동작"으로 받는다.
        /// </summary>
        public static SpecExpectations? From(SpDefinition? spDef)
        {
            if (spDef == null) return null;

            var updateColumns = BuildUpdateColumns(spDef.StaticAnalysis);

            var promptSchemaColumns = new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.OrdinalIgnoreCase);
            var columnlessDependencyTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // CheckNullabilityClaims의 기준이다. promptSchemaColumns와 정확히 같은 루프,
            // 같은 canonical 계산, 같은 건너뛰기 조건으로 채운다 - 두 컬렉션의 키 집합이
            // 갈리면(예: 이 컬렉션만 별도 루프로 canonical을 다시 계산하면) 한쪽에서만
            // 해석되는 테이블이 생겨 CheckNullabilityClaims의 테이블 앵커 대조가
            // 예측 불가능해진다.
            var nullableColumnsByTable = new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var dep in spDef.Dependencies)
            {
                var canonical = StaticAnalysisNormalizer.CanonicalizeParts(
                    dep.Database, dep.Schema, dep.Name, spDef.ObjectKey?.Database, spDef.Schema);
                if (string.IsNullOrWhiteSpace(canonical)) continue;

                if (dep.Columns.Count == 0)
                {
                    // 컬럼이 없는 의존성은 스키마 표 자체가 렌더링되지 않는다
                    // (BuildSpMetadataTexts의 dep.Columns.Count > 0 조건). 대조 기준으로
                    // 삼으면 "스키마 정의는 제공되지 않았습니다"라는 참인 진술이 대조
                    // 대상으로 잘못 올라간다. 그러나 canonical 이름 자체는 별도 집합에
                    // 담아 둔다 - 위 ColumnlessDependencyTables 문서 참고.
                    columnlessDependencyTables.Add(canonical);
                    continue;
                }

                promptSchemaColumns[canonical] = SchemaPromptColumnSelector.Select(dep, spDef);

                var nullableInThisTable = new HashSet<string>(
                    dep.Columns
                        .Where(c => c.IsNullable && !string.IsNullOrWhiteSpace(c.ColumnName))
                        .Select(c => c.ColumnName),
                    StringComparer.OrdinalIgnoreCase);
                nullableColumnsByTable[canonical] = nullableInThisTable;
            }

            var inputDefects = SchemaPromptColumnSelector.DetectOrphanedColumnKeys(spDef);

            var analysis = spDef.StaticAnalysis;
            var hasThreePartReference = analysis.ThreePartObjectReferences.Count > 0;
            var hasLinkedServerReference = analysis.LinkedServerReferences.Count > 0;
            var sourceComments = SourceCommentExtractor.Extract(spDef.DdlText);
            var roundingCalls = RoundingSemanticsExtractor.Extract(spDef.DdlText);
            var sessionOptions = SessionOptionsExtractor.Extract(spDef.DdlText);
            var hasInternalProcedureCall = DetectInternalProcedureCall(spDef.DdlText);
            // 프롬프트(AiService)와 같은 기준일 파라미터 선택 규칙을 써야 한다 - 두 곳이
            // 다르게 고르면 프롬프트의 표와 여기 기대값이 갈라지고, 모델이 표를 그대로
            // 베껴도 L1이 틀렸다고 하는 재현 불가능한 실패가 생긴다.
            var dmlScopeFacts = DmlScopeExtractor.Extract(spDef.DdlText, ResolveDateParameter(analysis));
            var derivedColumns = DerivedTableColumnExtractor.Extract(spDef.DdlText);
            var setPredicates = DmlScopeExtractor.ExtractSetPredicates(spDef.DdlText);

            // AiService가 프롬프트 표를 만들 때 쓰는 것과 같은 규칙이다. 원시 SQL 타입
            // 문자열을 여기서 분류하지 않고 SqlObjectTypeClassifier에 위임하는 것까지
            // 같아야 한다 - 두 곳이 갈리면 표와 기대값이 갈라진다.
            var knownFunctionNames = (spDef.Dependencies ?? new List<DependencyInfo>())
                .Where(d => SqlObjectTypeClassifier.ResolveCodeObjectType(d.Type) == CodeObjectType.Function)
                .Select(d => d.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var referencedFunctionCalls =
                DmlScopeExtractor.ExtractFunctionCalls(spDef.DdlText, knownFunctionNames);

            // 잠금 힌트 표(CheckLockHints)와 객체 선언 표(CheckObjectDeclaration)의
            // 기대값이다. AiService가 프롬프트 표를 만들 때 부르는 것과 같은 진입점을
            // 쓴다 - 두 곳이 갈리면 모델이 표를 그대로 베껴도 L1이 틀렸다고 하는
            // 재현 불가능한 실패가 난다(기준일 파라미터·참조 함수와 같은 이유).
            var lockHints = DmlScopeExtractor.ExtractLockHints(spDef.DdlText);
            var objectDeclaration = ObjectDeclarationExtractor.Extract(spDef.DdlText);

            // 실행 의미 표(CheckExecutionSemantics)의 기대값이다. AiService가 프롬프트
            // 표를 만들 때 부르는 것과 같은 Collect 진입점을, 같은 인자로 부른다 -
            // 두 곳이 갈리면 모델이 표를 그대로 베껴도 L1이 틀렸다고 하는 재현 불가능한
            // 실패가 난다.
            var executionSemantics = ExecutionSemanticsFacts.Collect(
                spDef.DdlText,
                analysis,
                spDef.ObjectKey,
                ExecutionSemanticsFacts.BuildColumnTypeMap(spDef.Dependencies));

            // CASE 분기 표(CheckCaseBranches)의 기대값이다. AiService가 프롬프트 표를
            // 만들 때 부르는 것과 같은 Extract 진입점을 부른다 - 두 곳이 갈리면 모델이
            // 표를 그대로 베껴도 L1이 틀렸다고 하는 재현 불가능한 실패가 난다.
            var caseBranches = CaseBranchExtractor.Extract(spDef.DdlText);

            // INSERT 매핑 표의 테이블명 표기 대조(CheckInsertMappingTableNames)의 기대값이다.
            // 파서(SqlStaticParser)가 이미 확정해 둔 InsertTables를 그대로 옮긴다 - 별도
            // 재추출 경로를 두면 두 곳이 갈릴 수 있다.
            var insertTargetTables = spDef.StaticAnalysis?.InsertTables is { Count: > 0 } insertTables
                ? new List<string>(insertTables)
                : new List<string>();

            // 대조할 것이 하나도 없을 때만 null이다. 재료를 추가하는 태스크는 이 식에
            // 자기 항을 반드시 이어야 한다 - 빠뜨리면 그 검사가 한 번도 돌지 않고,
            // 스위트는 초록으로 남는다.
            //
            // hasInternalProcedureCall은 예외다 - 일부러 여기 잇지 않는다. 이 신호는
            // 헤더 주석이 "NONE"이라 선언했을 때만 의미가 있고, 헤더 주석이 있으면
            // sourceComments가 이미 비어 있지 않으므로(SourceCommentExtractor는 CREATE
            // 이전 모든 주석 줄을 Header로 담는다) 이 항이 없어도 null 판정은 이미
            // sourceComments 항이 넓혀 준다. 반대로 헤더 주석이 아예 없는 SP는 이
            // 신호가 true여도 대조할 헤더 계약이 없으므로 null로 남아도 정확하다 -
            // 여기 이었다면 "EXEC만 있고 주석은 하나도 없는" SP까지 대조 대상으로
            // 끌어들여, 대조할 게 없는데도 Validate가 CheckHeaderContractContradiction을
            // 도는 낭비가 생긴다(결과는 같지만 조기 반환의 취지 - "정말 대조할 것이
            // 있을 때만 확장한다" - 를 흐린다).
            //
            // [Fix Round 1 - 이 AND-체인을 쓰는 "재료 하나만" 테스트를 쓸 때 반드시
            // 지킬 것] 이 식은 순수 AND-체인이다. 항 하나가 실수로 빠져도 나머지
            // 항 중 단 하나만 true(공백/미충족)가 아니면 전체 판정은 바뀌지 않는다
            // - 즉 재료 하나만 있는 픽스처라도 다른 재료가 "우연히 같이" 잡히면
            // 그 우연한 재료의 항이 지워진 항을 대신 가려 버려, 지워진 항을 지키는
            // 테스트가 초록인 채로 아무것도 증명하지 못한다(Fix Round 1 실측:
            // roundingCalls/sourceComments 전용 테스트가 UPDATE 문을 픽스처에
            // 남겨 둔 탓에 DmlScopeExtractor(Task 9)가 무조건 사실을 하나 만들어
            // dmlScopeFacts.Count == 0 항이 이미 false였다 - roundingCalls·
            // sourceComments 항을 지워도 테스트가 계속 통과했다). DmlScopeExtractor는
            // UPDATE/DELETE 문이 하나라도 있으면 그 문장의 대상·술어가 비어 있어도
            // 무조건 사실을 만든다는 점이 특히 잘 숨는다 - "이 픽스처는 이 재료
            // 말고는 아무것도 안 담았다"고 눈으로 봐도, UPDATE라는 문장 형태 자체가
            // 이미 별도 재료다. "재료 하나만" 테스트를 새로 쓰거나 고칠 때는
            // (1) 그 재료를 남기는 최소 DDL을 쓰고 (2) UPDATE/DELETE를 다른 문장
            // (SELECT 등)으로 바꿀 수 있는지 먼저 확인하고 (3) 가능하면 결과의
            // 다른 컬렉션(예: DmlScopeFacts)이 실제로 비어 있는지도 함께 단언해
            // 격리를 코드로 못박는다.
            if (updateColumns.Count == 0
                && promptSchemaColumns.Count == 0
                && inputDefects.Count == 0
                && !hasThreePartReference
                && !hasLinkedServerReference
                && sourceComments.Count == 0
                && roundingCalls.Count == 0
                && sessionOptions.Count == 0
                && dmlScopeFacts.Count == 0
                && derivedColumns.Count == 0
                // 오늘은 중복항이다 - ExtractSetPredicates가 방문하는 세 문장
                // (UPDATE·DELETE·INSERT)이 Extract가 방문하는 네 문장의 부분집합이므로,
                // setPredicates가 비지 않으면 dmlScopeFacts도 비지 않는다. Task 8 이전에는
                // 이 자리가 "같은 세 문장만 방문하므로"라고 적혀 있었는데, 2026-08-22
                // 축 A 재감사 ③의 Task 1이 Extract에 독립 SELECT를 더해 그 근거가
                // 낡았다 - 결론은 그대로 서지만 이유가 부분집합 관계로 바뀌었다.
                // From_WithSetPredicates_ShouldExposeThemAndNeverReturnNull이 그 불변식을
                // 지키고, 깨지는 날 이 항이 실제로 필요해진다.
                && setPredicates.Count == 0
                // setPredicates와 같은 이유로 오늘은 중복항이다 - ExtractFunctionCalls도
                // 같은 세 문장만 방문하고, 그 셋은 Extract가 보는 네 문장의 부분집합이라
                // 호출이 있으면 dmlScopeFacts도 비지 않는다(부분집합으로 고쳐 적은 이유는
                // 위 항의 주석 참고).
                // 그래도 잇는다: 위 주석이 경고하듯 항을 빠뜨리면 그 검사가 한 번도
                // 돌지 않고 스위트는 초록으로 남는다.
                && referencedFunctionCalls.Count == 0
                // [중복항이 아니다 - 2026-08-22 축 A 재감사 ③ Task 8에서 고쳐 적는다]
                // 이 자리는 오래도록 "setPredicates·referencedFunctionCalls와 같은 이유로
                // 오늘은 중복항"이라고 적혀 있었다. 근거는 ExtractLockHints도
                // INSERT/UPDATE/DELETE만 방문한다는 것이었는데, 같은 재감사의 Task 2가
                // 그 전제를 깼다 - LockHintVisitor는 `IF` 술어 안의 스캔도 `IF n`으로
                // 담지만 DmlScopeVisitor는 IfStatement를 방문하지 않는다(그 방문자의
                // "SELECT만 더하고 IfStatement는 더하지 않는 이유" 문단 참고. 두 방문자의
                // 문장 집합이 갈리는 지점이 정확히 여기다). 그래서 스캔을 지닌 문장이
                // `IF EXISTS(SELECT ... FROM T)`뿐인 객체는 잠금 힌트가 나면서
                // dmlScopeFacts는 빈 채로 남는다 - EXISTS 안의 질의는 SelectStatement
                // 문장 노드가 아니라 QueryExpression이라 독립 SELECT로도 잡히지 않는다.
                // ExtractLockHints_ControlFlowPredicate_ShouldBeNumberedAsIf의 DDL이 정확히
                // 그 모양이고, 코퍼스 실물은 INS_EXTRA:31의 -9 차단 게이트 스캔이다.
                // 즉 이 항은 이제 진짜로 일한다 - 이웃 항들과 같은 단서가 붙는다:
                // 재료가 이것 하나뿐인 객체에서 이 항을 지우면 From이 null을 돌려주고
                // CheckLockHints가 한 번도 돌지 않는다(AND 사슬이므로 다른 재료가
                // 함께 비어 있을 때의 이야기다 - 위 INS_EXTRA는 다른 재료도 지녀
                // 그 자체로는 이 조건을 만족하지 않는다. 잠금 힌트만 남는 객체가
                // 성립한다는 것이 요점이다). "중복항이니 정리하자"는 리팩터가 이 항을
                // 지우는 것이 이 파일에서 가장 비싼 실수다.
                && lockHints.Count == 0
                // objectDeclaration과 같은 이유로 중복항이 아니다 - DB 배치 행은
                // DML이 하나도 없는 객체에서도 난다. 이 항을 빠뜨리면 재료가 이것
                // 하나뿐인 객체에서 From이 null을 돌려주고 CheckExecutionSemantics가
                // 한 번도 돌지 않는다.
                && executionSemantics.Count == 0
                // executionSemantics와 같은 이유로 중복항이 아니다 - DML이 하나도 없는
                // 스칼라 함수도 CASE를 가질 수 있다. 이 항을 빠뜨리면 재료가 이것
                // 하나뿐인 객체에서 From이 null을 돌려주고 CheckCaseBranches가 한 번도
                // 돌지 않는다.
                && caseBranches.Count == 0
                // insertTargetTables는 중복항이 아니다 - INSERT 매핑 표 대조(§4 D)는
                // dmlScopeFacts 등 다른 재료가 하나도 없는 SP에서도 필요할 수 있다
                // (예: 파서가 INSERT 대상만 잡고 다른 신호는 하나도 못 뽑은 경우).
                // 이 항을 빠뜨리면 재료가 이것 하나뿐인 픽스처에서 From이 null을
                // 돌려주고 CheckInsertMappingTableNames가 한 번도 돌지 않는다 - 위
                // 주석들이 경고하는 것과 같은 실패 모양이다.
                && insertTargetTables.Count == 0
                // objectDeclaration은 중복항이 아니다 - WITH 옵션이 없는 함수(RETURN
                // 하나뿐인 스칼라 함수 등)는 본문에 DML 문장이 전혀 없을 수 있어
                // dmlScopeFacts·lockHints 등 다른 재료가 하나도 없다. 이 항을 빠뜨리면
                // "WITH 절이 없다"는 사실 자체가 기대값에서 사라져, 그 사실을 대조할
                // From_ShouldExposeLockHintsAndObjectDeclaration 같은 재료 하나짜리
                // 픽스처에서 SpecExpectations.From이 null을 돌려주고 CheckObjectDeclaration이
                // 한 번도 돌지 않는다 - 2026-08-20 리뷰가 dmlScopeFacts 항에서 실측한
                // 것과 같은 실패 모양이다.
                && objectDeclaration == null
                // Fix Round 1 - nullableColumnsByTable은 promptSchemaColumns와 정확히
                // 같은 루프, 같은 canonical 계산, 같은 건너뛰기 조건에서 같은 키로
                // 채워지므로(위 foreach 참고) 오늘은 진짜 중복항이다 - 두 컬렉션의 키
                // 집합이 갈릴 길이 없다. setPredicates·referencedFunctionCalls·lockHints와
                // 같은 이유로 그래도 잇는다: 언젠가 두 컬렉션이 별도 루프로 갈라지면
                // 이 항이 없는 채 그 리팩터가 조용히 CheckNullabilityClaims를 죽인다.
                && nullableColumnsByTable.Count == 0)
            {
                return null;
            }

            return new SpecExpectations(
                updateColumns, promptSchemaColumns, columnlessDependencyTables, inputDefects)
            {
                HasThreePartReference = hasThreePartReference,
                HasLinkedServerReference = hasLinkedServerReference,
                SourceComments = sourceComments,
                RoundingCalls = roundingCalls,
                SessionOptions = sessionOptions,
                HasInternalProcedureCall = hasInternalProcedureCall,
                DmlScopeFacts = dmlScopeFacts,
                DerivedColumns = derivedColumns,
                SetPredicates = setPredicates,
                ReferencedFunctionCalls = referencedFunctionCalls,
                LockHints = lockHints,
                ObjectDeclaration = objectDeclaration,
                ExecutionSemantics = executionSemantics,
                CaseBranches = caseBranches,
                InsertTargetTables = insertTargetTables,
                NullableColumnsByTable = nullableColumnsByTable
            };
        }

        /// <summary>
        /// 기준일 파라미터를 고르는 단일 규칙. AiService의 프롬프트 렌더(두 호출부)도 이
        /// 메서드를 부른다 - 두 곳이 다르게 고르면 프롬프트의 표와 L1의 기대가 갈라지고,
        /// 그러면 모델이 옳게 옮겨도 L1이 틀렸다고 한다.
        ///
        /// [반드시 이름만 돌려준다] ProcedureParameters의 원소는 SqlStaticParser가
        /// $"{VariableName} {DataType}" 형태로 담은 <b>선언문</b>이다("@pi_strYMD varchar(8)").
        /// 반면 이 값을 받는 DmlScopeExtractor는 VariableReference.Name에서 온 <b>맨 이름</b>
        /// ("@pi_strYMD") 목록과 비교한다. 선언문을 그대로 넘기면 두 문자열은 어떤 SP에서도
        /// 같아질 수 없어 DateParameterApplied가 구조적으로 항상 false가 되고, 프롬프트의
        /// "DML 범위(기계 확정 - 수정 금지)" 표는 기준일 칸이 전 행 '아니오'인 채로 나간다.
        /// EXCEPTION_PROC 재생성 실측에서 L2 비평가가 이 칸을 '치명적 사실 오류'로 잡아
        /// 재시도 예산 3회를 모두 소진시켰다(최종 78/100 품질 미달 채택).
        /// </summary>
        public static string ResolveDateParameter(SpStaticAnalysisResult? analysis) =>
            analysis?.ProcedureParameters
                .Select(ParameterNameOf)
                .FirstOrDefault(name => name.Contains("YMD", StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        /// <summary>"@pi_strYMD varchar(8)" 같은 선언문에서 변수명만 떼어낸다.</summary>
        private static string ParameterNameOf(string declaration)
        {
            var trimmed = declaration.Trim();
            var cut = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            return cut < 0 ? trimmed : trimmed[..cut];
        }

        /// <summary>
        /// 원본 DDL에 동적 SQL이 아닌 이름 고정 EXEC 호출이 있는지 AST로 직접 훑는다.
        ///
        /// [ControlFlowSummary를 쓰지 않는 이유] SqlStaticParser.ExplicitVisit(ExecuteStatement)를
        /// 실측하면, sp_executesql·EXEC(@SQL) 같은 <b>동적 SQL</b> 실행만 경고로
        /// ControlFlowSummary에 남고 `EXEC dbo.OtherProc ...`처럼 이름이 고정된 정상
        /// 내부 SP 호출은 아무 흔적도 남기지 않는다. 그래서
        /// analysis.ControlFlowSummary.Any(s =&gt; s.Contains("EXEC"))로 이 신호를
        /// 판정하면 UP_Util_Settle_Summary(EXEC dbo.UP_Util_Settle_Summary_AcqManual 등,
        /// 둘 다 이름 고정 호출)에서도 항상 false가 되어 이 검사 전체가 죽은 채
        /// 테스트만 초록으로 남는다. 게다가 그 문자열의 "EXEC (@SQL) 동적 SQL 문자열
        /// 실행 감지됨" 메시지 자체가 우연히 "EXEC" 부분 문자열을 포함하므로, 같은
        /// 판정식은 진짜 내부 SP 호출이 아닌 동적 SQL 문자열 실행에서 반대로 오탐할
        /// 수도 있었다 - 두 방향 모두 잘못이다. 그래서 이 메서드가 AST를 직접 훑어
        /// ExecutableProcedureReference 노드만(동적 SQL 노드 제외) 본다.
        /// </summary>
        private static bool DetectInternalProcedureCall(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return false;

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null) return false;

                var visitor = new InternalProcedureCallVisitor();
                fragment.Accept(visitor);
                return visitor.Found;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[SpecExpectations] 내부 SP 호출 탐지 실패 - false로 진행합니다.");
                return false;
            }
        }

        /// <summary>
        /// EXEC 대상이 이름 고정 프로시저 참조(ExecutableProcedureReference)이고 그
        /// 이름이 sp_executesql이 아니면 내부 SP 호출로 본다. EXEC(@sql) 같은 문자열
        /// 실행(ExecutableStringList)은 여기 매치되지 않는다 - 그건 동적 SQL이지
        /// "내부 SP 호출"이 아니다.
        /// </summary>
        private sealed class InternalProcedureCallVisitor : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void Visit(ExecuteStatement node)
            {
                if (Found) return;

                if (node.ExecuteSpecification?.ExecutableEntity is not ExecutableProcedureReference procRef)
                {
                    return;
                }

                var name = procRef.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value;
                if (string.IsNullOrEmpty(name)) return;
                if (string.Equals(name, "sp_executesql", StringComparison.OrdinalIgnoreCase)) return;

                Found = true;
            }
        }

        /// <summary>
        /// 테이블 단위로 접는다. 대조가 테이블 합집합이므로 기대도 같은 단위여야 한다.
        /// </summary>
        private static List<UpdateColumnExpectation> BuildUpdateColumns(SpStaticAnalysisResult? analysis)
        {
            if (analysis == null || analysis.AstUpdateMappings.Count == 0)
            {
                return new List<UpdateColumnExpectation>();
            }

            var byTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in analysis.AstUpdateMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.TargetTable)) continue;

                if (!byTable.TryGetValue(mapping.TargetTable, out var columns))
                {
                    columns = new List<string>();
                    byTable[mapping.TargetTable] = columns;
                }

                foreach (var assignment in mapping.Assignments)
                {
                    if (string.IsNullOrWhiteSpace(assignment.Column)) continue;
                    if (columns.Contains(assignment.Column, StringComparer.OrdinalIgnoreCase)) continue;
                    columns.Add(assignment.Column);
                }
            }

            return byTable
                .Where(kvp => kvp.Value.Count > 0)
                .Select(kvp => new UpdateColumnExpectation(kvp.Key, kvp.Value))
                .ToList();
        }
    }

    /// <summary>한 테이블에 대해 명세서의 UPDATE 매핑 표에 반드시 있어야 하는 컬럼들.</summary>
    public sealed record UpdateColumnExpectation(string Table, IReadOnlyList<string> Columns);
}
