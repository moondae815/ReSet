using System;
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <param name="Name">재료 이름. 코드에 실재하는 이름(레코드 속성명·타입명)을 쓴다.
    /// 별도 속성이 없는 재료(<see cref="SpecReturnCodeExtractor"/>·
    /// <see cref="SpecRoundingShapeExtractor"/>)는 저장소가 실제로 쓰는 변수명·속성명을
    /// 그대로 옮긴다(<c>specReturnCodes</c> → <c>SpecReturnCodes</c>,
    /// <c>SpecConditions.RoundingShapes</c> → <c>RoundingShapes</c>).</param>
    /// <param name="ReaderTypeName">이 재료를 명세서에서 읽는 Spec*Extractor의 타입 이름.</param>
    /// <param name="SectionHeadings">읽는 절의 헤딩 리터럴. 변형이 여럿이면 전부. 헤딩에
    /// 얽매이지 않고 문서 전체를 훑는 리더(<see cref="SpecConditionColumnExtractor"/>·
    /// <see cref="SpecReturnCodeExtractor"/>·<see cref="SpecRoundingShapeExtractor"/>)는
    /// 지어낼 헤딩이 없으므로 빈 목록이다 - 그 사실 자체가 판정이다.</param>
    /// <param name="Enforced">그 헤딩이 MachineConfirmedTables.All에 있는가.</param>
    /// <param name="DdlCounterpart">
    /// 원본 DDL에서 같은 사실을 뽑는 자리의 이름. null이면 「잴 수 없음」이다 -
    /// 빈 문자열이나 0으로 두지 마십시오. 빈칸은 0으로 읽히고 0은 정상으로 읽힙니다.
    /// </param>
    /// <param name="ConsumingChecks">이 재료가 0이면 죽는 MechanicalValidator의 검사 이름들.
    /// 여기 실리는 이름은 SpecMaterialsTests.EveryNamedCheck_ExistsOnMechanicalValidator가
    /// <c>GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)</c>로
    /// 대조하므로, <b>public</b> 메서드(예: FindMissingErrorCodes·ValidateSplitProcedureObligations)는
    /// 실제 소비자여도 여기 적을 수 없다 - 그 이름을 적으면 이 잠금 테스트가 거짓으로
    /// 시끄럽게 죽는다. 그런 public 소비자가 있으면 이 파일의 재료별 주석에 따로 적는다.</param>
    public sealed record SpecMaterial(
        string Name,
        string ReaderTypeName,
        IReadOnlyList<string> SectionHeadings,
        bool Enforced,
        string? DdlCounterpart,
        IReadOnlyList<string> ConsumingChecks);

    /// <summary>
    /// 명세서에서 읽는 재료 목록의 단일 출처다.
    ///
    /// [왜 손으로 적는가] MachineConfirmedTables와 같은 이유다 - 리플렉션으로 모으면
    /// 「무엇이 강제되는가」와 「무엇이 이 재료를 쓰는가」라는 판정이 코드에 안 남는다.
    /// 그 판정이 어디에도 없었던 것이 (5-3-7)의 결함 그 자체다.
    ///
    /// [테스트가 이 표를 잠근다] SpecMaterialsTests 참고 - 리더 누락·강제 표시의
    /// 거짓·존재하지 않는 검사 이름 셋을 각각 막는다.
    ///
    /// [2026-08-29 다섯 리더 전수 판정 - 판정 근거 요약]
    /// 리더는 다섯이지만 산출하는 재료는 여덟이다 - SpecStatementFactsExtractor 하나가
    /// DmlRows·SetTargets·LocalVariables·ErrorCodeToOrdinal 네 재료를 한 레코드
    /// (SpecStatementFacts)에 함께 담기 때문이다. 아래는 재료별 요약이고, 각 재료 항목의
    /// 주석에 더 자세한 근거가 있다.
    ///
    ///   - DmlRows·ErrorCodeToOrdinal: 강제됨(둘 다 MachineConfirmedTables.All에 헤딩이
    ///     있음 - DmlScopeExtractor.cs:490,494 직접 대조). DDL 대응물은 DmlScopeExtractor.
    ///   - SetTargets: 강제 아님(헤딩 리터럴 "### UPDATE 대상 테이블:"은
    ///     MachineConfirmedTables.All에 없음). DDL 대응물은 SqlStaticParser(AstUpdateMappings).
    ///     소비 검사가 <b>0개</b>다 - MechanicalValidator.cs 전체에서
    ///     "SpecStatementFacts.SetTargets"·"f.SetTargets"를 읽는 코드가 없다(grep 실측,
    ///     2026-08-29). CheckUpdateSetTargets라는 이름이 있지만 그것은 배치 제어 테이블
    ///     계약(BatchControlContract) 검사이고 완전히 다른 메커니즘이다 - 이 재료를 전혀
    ///     읽지 않는다. 이 재료는 추출되지만 한 번도 소비되지 않는다.
    ///   - LocalVariables: 강제 아님(§0). DDL 대응물은 이번 회차가 만드는
    ///     SpecMaterialCensus.CountDeclaredVariables(Task 2, DeclareVariableElement를
    ///     Visit하는 방문자) - 문자열 리터럴로 이름 댄다(아직 존재하지 않는 타입이라
    ///     nameof를 못 쓴다). 소비 검사는 CheckSpecLocalVariablesDeclared 하나.
    ///   - SpecConditions(BodyColumns·ByUdf): 강제 아님, 헤딩 없음(문서 전체를 훑는다 -
    ///     SpecConditionColumnExtractor.cs:142 HeadingRegex는 어떤 헤딩이든 UDF 소속
    ///     경계로만 쓰지 특정 헤딩을 요구하지 않는다). DDL 대응물은 null - 근접한 후보로
    ///     DmlScopeFact.PredicateColumns(같은 DmlScopeExtractor)가 있지만 범위가 다르다
    ///     (그쪽은 SP 자신의 DDL 문장 단위, 이쪽은 문서 전체 산문 + UDF 내부 조건까지
    ///     포함 - UDF의 DDL은애초에 DmlScopeExtractor에 안 들어간다). "같은 사실"이
    ///     아니므로 null로 둔다. 소비 검사는 CheckMissingConditionColumns.
    ///   - RoundingShapes: 강제 아님, 헤딩 없음(SpecRoundingShapeExtractor.cs:99
    ///     ReadShapes가 문서 전체 텍스트에서 중첩 ROUND(...)를 정규식으로 찾는다).
    ///     DDL 대응물은 null - MechanicalValidator.ReportMissingRoundingShapes(1798행)가
    ///     실제로 대조하는 상대는 원본 DDL이 아니라 계획서 단계 본문
    ///     (SpecRoundingShapeExtractor.ReadShapes(stepMarkdown), 1810행)이다. DDL 쪽에는
    ///     RoundingSemanticsExtractor(3인자 ROUND 호출을 개별로 뽑는 방문자)가 있지만,
    ///     그것은 낱개 ROUND 호출의 세 번째 인자를 줄 단위로 담을 뿐 SpecRoundingShapeExtractor가
    ///     만드는 "중첩 모양" 정규화를 만들지 않는다 - "같은 사실"이 아니다. 소비 검사는
    ///     ReportMissingRoundingShapes.
    ///   - StepTableSets: 이 재료는 다른 넷과 근본이 다르다. SpecTargetTableExtractor.Extract는
    ///     스펙 마크다운을 전혀 받지 않는다(시그니처가
    ///     `Extract(IEnumerable&lt;SpDefinition&gt;? definitions)`) - definition.StaticAnalysis는
    ///     그 자체로 원본 DDL을 SqlStaticParser가 파싱한 결과다(DbMetadataService.cs:515).
    ///     즉 이 "재료"는 명세서에서 읽는 것이 아니라 DDL 정적 분석 결과를 그대로
    ///     프로시저별 사전으로 접은 것이다 - 모델이 쓴 사본과 비교할 "명세서 쪽"이
    ///     애초에 없다. 그래서 SectionHeadings는 빈 목록이고(읽는 헤딩이 없다),
    ///     DdlCounterpart는 null로 둔다 - DDL 추출기가 없어서가 아니라(SqlStaticParser가
    ///     바로 그것이다) "DDL 사실 수 대 명세서 행 수"라는 소실 개념 자체가 이 재료에는
    ///     성립하지 않기 때문이다(Task 2가 이 재료를 census에 넣으려면 이 사실을 먼저
    ///     반영해야 한다). 소비자는 IsTableOwedOnlyBySplitProcedures(private static bool) -
    ///     다만 그 소비 형태도 다른 재료와 다르다: 이 재료가 비면 검사가 조용히 꺼지는
    ///     것이 아니라 분할-SP 면제가 사라져 검사가 오히려 더 엄격해진다(과탐 위험).
    ///     ValidateSplitProcedureObligations(public)도 같은 재료를 쓰지만 public이라
    ///     여기 이름을 못 싣는다(위 ConsumingChecks 문서 참고).
    ///   - SpecReturnCodes: 강제 아님, 헤딩 없음(SpecReturnCodeExtractor.cs:29
    ///     ReturnAssignmentRegex가 "@po_intRetVal = N"을 문서 전체에서 찾는다).
    ///     DDL 대응물은 DmlScopeExtractor - ExtractErrorCodes(767행)가 같은 변수
    ///     "@po_intRetVal"의 대입을 DDL AST에서 뽑는다(ErrorCodeFact.Variable, 180행).
    ///     소비 검사는 IsOwedOnlyBySplitProcedures(private static bool) - 같은 이유로
    ///     FindMissingErrorCodes(public static)는 실제 소비자이지만 여기 못 싣는다.
    /// </summary>
    public static class SpecMaterials
    {
        public static readonly IReadOnlyList<SpecMaterial> All = new[]
        {
            new SpecMaterial(
                "DmlRows",
                nameof(SpecStatementFactsExtractor),
                new[] { DmlScopeExtractor.DmlScopeTableHeading },
                Enforced: true,
                DdlCounterpart: nameof(DmlScopeExtractor),
                ConsumingChecks: new[]
                {
                    "CheckAnchoredStatementFacts",
                    "CheckAnchoredStatementExtras",
                    "CheckStatementCountAgainstSpec",
                }),
            new SpecMaterial(
                "ErrorCodeToOrdinal",
                nameof(SpecStatementFactsExtractor),
                new[] { DmlScopeExtractor.ErrorCodeTableHeading },
                Enforced: true,
                DdlCounterpart: nameof(DmlScopeExtractor),
                ConsumingChecks: new[]
                {
                    "CheckAnchoredStatementFacts",
                    "CheckAnchoredStatementExtras",
                }),
            // [강제 아님 - MachineConfirmedTables.cs 직접 대조] "### UPDATE 대상 테이블:"
            // (MechanicalValidator.UpdateHeadingPrefix, 2112행)은 MachineConfirmedTables.All의
            // 11개 헤딩 어디에도 없다.
            //
            // [소비 검사 0개 - grep 실측 2026-08-29] `grep -rn "SetTargets"
            // src/ReSet.Core/Services/MechanicalValidator.cs`에 걸리는 것은
            // CheckUpdateSetTargets(1121행)뿐인데, 그 메서드는 BatchControlContract(제어
            // 테이블 계약)를 stepMarkdown에서 정규식으로 직접 훑는 별개 검사이고
            // SpecStatementFacts.SetTargets를 인자로도 받지 않는다. 이 재료가 실제로
            // MechanicalValidator 어디에도 안 흘러간다 - (5-3-7)보다 더 나쁜 모양이다:
            // 저건 "쓰다가 죽은" 것이고 이건 "한 번도 안 쓰인" 것이다.
            new SpecMaterial(
                "SetTargets",
                nameof(SpecStatementFactsExtractor),
                new[] { "### UPDATE 대상 테이블:" },
                Enforced: false,
                DdlCounterpart: nameof(SqlStaticParser),
                ConsumingChecks: Array.Empty<string>()),
            new SpecMaterial(
                "LocalVariables",
                nameof(SpecStatementFactsExtractor),
                new[] { "### 지역 변수", "### 내부 변수" },
                Enforced: false,
                DdlCounterpart: "SpecMaterialCensus",
                ConsumingChecks: new[] { "CheckSpecLocalVariablesDeclared" }),
            // [강제 아님·헤딩 없음] SpecConditionColumnExtractor는 특정 "### " 헤딩을
            // 요구하지 않는다 - 문서 전체를 훑으며 만나는 아무 헤딩이든 UDF 소속
            // 경계로만 쓴다(CollectFrom, 129행).
            //
            // [DDL 대응물이 null인 이유] DmlScopeFact.PredicateColumns(DmlScopeExtractor)가
            // 근접한 후보이지만 "같은 사실"이 아니다 - SpecConditions는 문서 전체 산문 +
            // 호출된 UDF 내부 조건(ByUdf)까지 담는데, DmlScopeExtractor는 이 SP 자신의
            // DDL 문장 단위로만 술어를 뽑고 UDF의 DDL은 애초에 파싱 대상이 아니다.
            new SpecMaterial(
                "SpecConditions",
                nameof(SpecConditionColumnExtractor),
                Array.Empty<string>(),
                Enforced: false,
                DdlCounterpart: null,
                ConsumingChecks: new[] { "CheckMissingConditionColumns" }),
            // [강제 아님·헤딩 없음] ReadShapes(99행)는 문서 전체 텍스트에서 정규식
            // "ROUND\s*\("로 중첩 ROUND 식을 찾는다 - 특정 헤딩에 매이지 않는다.
            //
            // [DDL 대응물이 null인 이유] MechanicalValidator.ReportMissingRoundingShapes가
            // 실제로 대조하는 상대는 원본 DDL이 아니라 계획서 단계 본문이다
            // (ReadShapes(stepMarkdown), MechanicalValidator.cs:1810). DDL 쪽에는
            // RoundingSemanticsExtractor가 있지만 그것은 낱개 ROUND 호출의 세 번째 인자를
            // 줄 단위로 담을 뿐, 여기서 대조에 쓰는 "중첩 모양" 정규화를 만들지 않는다.
            new SpecMaterial(
                "RoundingShapes",
                nameof(SpecRoundingShapeExtractor),
                Array.Empty<string>(),
                Enforced: false,
                DdlCounterpart: null,
                ConsumingChecks: new[] { "ReportMissingRoundingShapes" }),
            // [근본이 다른 재료] SpecTargetTableExtractor.Extract는 스펙 마크다운을 전혀
            // 받지 않는다 - 시그니처가 `Extract(IEnumerable<SpDefinition>? definitions)`이고
            // definition.StaticAnalysis는 SqlStaticParser가 원본 DDL을 파싱한 결과 그
            // 자체다(DbMetadataService.cs:515). 모델이 쓴 사본과 비교할 "명세서 쪽"이
            // 애초에 없다. SectionHeadings가 빈 목록인 이유는 읽는 헤딩이 없어서이고,
            // DdlCounterpart가 null인 이유는 DDL 추출기가 없어서가 아니라(SqlStaticParser가
            // 바로 그것이다) "DDL 사실 수 대 명세서 행 수" 소실 개념 자체가 이 재료에는
            // 성립하지 않기 때문이다.
            new SpecMaterial(
                "StepTableSets",
                nameof(SpecTargetTableExtractor),
                Array.Empty<string>(),
                Enforced: false,
                DdlCounterpart: null,
                ConsumingChecks: new[] { "IsTableOwedOnlyBySplitProcedures" }),
            // [강제 아님·헤딩 없음] ReturnAssignmentRegex(29행)는 "@po_intRetVal = N"을
            // 문서 전체에서 찾는다 - 헤딩에 매이지 않는다.
            //
            // [DDL 대응물] DmlScopeExtractor.ExtractErrorCodes(767행)가 같은 변수
            // "@po_intRetVal"의 대입을 DDL AST에서 뽑는다(ErrorCodeFact.Variable, 180행).
            new SpecMaterial(
                "SpecReturnCodes",
                nameof(SpecReturnCodeExtractor),
                Array.Empty<string>(),
                Enforced: false,
                DdlCounterpart: nameof(DmlScopeExtractor),
                ConsumingChecks: new[] { "IsOwedOnlyBySplitProcedures" }),
        };
    }
}
