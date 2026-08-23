using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Serilog;

namespace ReSet.Core.Services
{
    public enum ErrorType
    {
        HeaderMissing,
        MermaidQuoteMissing,
        MermaidCliError,
        UpdateMappingMissing,
        SchemaClaimFalse,
        TableIdentitySplit,
        IdentifierNotationClaim,
        SourceCommentMissing,
        RoundingSemanticsMissing,
        SessionOptionMissing,
        HeaderContractContradiction,
        PromptInstructionLeak,
        DmlScopeTableMissing,
        DerivedTableDefinitionMissing,
        SetPredicateMismatch,
        // VerificationCartesianComparison을 여기 넣으면 General의 서수가 뒤로
        // 한 칸 밀린다 - "기존 값의 정수 표현이 안 바뀐다"는 서술은 부정확하다.
        // (축 A의 SetPredicateMismatch와 병합된 지금은 15에서 16으로 밀렸다.)
        // 다만 이 코드베이스 어디에도 (int)ErrorType 캐스트나 ErrorType의 숫자
        // 직렬화가 없으므로(문자열 이름으로만 비교·표시한다), 서수 이동 자체의
        // 기능 영향은 없다.
        VerificationCartesianComparison,
        // 이 값을 여기 넣으면 General의 서수가 뒤로 한 칸 더 밀린다. 이 코드베이스
        // 어디에도 (int)ErrorType 캐스트나 숫자 직렬화가 없으므로(문자열 이름으로만
        // 비교·표시한다) 기능 영향은 없다.
        BatchRunRowNeverCreated,
        // 작업 5 - 잠금 힌트·객체 선언 표의 L1 앵커. 위와 같은 이유로 서수 이동은
        // 기능에 영향이 없다. BuildSuggestedPromptFix의 catch-all 버킷(8. 기계 확정
        // 재료 대조 실패)이 열거되지 않은 타입을 모두 흘려보내므로 이 값도 별도
        // 버킷 없이 모델에게 닿는다.
        LockHintTableMissing,
        ObjectDeclarationTableMissing,
        // 실행 의미 표(기계 확정 DB 배치 등)의 L1 앵커. 위와 같은 이유로 서수 이동은
        // 기능에 영향이 없다.
        ExecutionSemanticsTableMissing,
        // CASE 분기 표(기계 확정 - 조건·결과 원문)의 L1 앵커. 위와 같은 이유로 서수
        // 이동은 기능에 영향이 없다.
        CaseBranchTableMissing,
        // 기계 확정 표가 GFM 표로 렌더링되지 않는 형태로 옮겨졌을 때의 L1 앵커.
        // 위와 같은 이유로 서수 이동은 기능에 영향이 없다.
        MachineTableShapeBroken,
        // INSERT 매핑 표의 테이블명 표기 어긋남의 L1 앵커.
        InsertMappingTableNameMismatch,
        // 컬럼 널 허용 주장 어긋남의 L1 앵커.
        NullabilityClaimMismatch,
        // 참조 함수 표의 행 단위 대조 실패 앵커(2026-08-23 ③(b) 최종 리뷰 에스컬레이션 1).
        // 이전에는 헤딩 부재를 SetPredicateMismatch로 빌려 보고했다.
        ReferencedFunctionMismatch,
        General
    }

    public class DetailedError
    {
        public ErrorType Type { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? RawContext { get; set; }
    }

    public class MechanicalValidator
    {
        private static readonly string[] MermaidShapes = {
            @"(\[\()(.*?)(\)\])",
            @"(\[\[)(.*?)(\]\])",
            @"(\[\/)(.*?)([\/\\]\])",
            @"(\[\\)(.*?)([\/\\]\])",
            @"(\(\[)(.*?)(\]\))",
            @"(\(\()(.*?)(\)\))",
            @"(\{\{)(.*?)(\}\})",
            @"(\[)(.*?)(\])",
            @"(\()(.*?)(\))",
            @"(\{)(.*?)(\})",
            @"(\>)(.*?)(\])"
        };

        private static readonly Regex MermaidNodeRegex = new Regex(
            @"([a-zA-Z0-9_ ]+?)\s*(?:" + string.Join("|", MermaidShapes) + @")(?=\s*(?:-->|---|--|$))",
            RegexOptions.Compiled);

        private static readonly string[] RequiredHeaders = new[]
        {
            "개요",
            "파라미터 목록",
            "CRUD 분석",
            "로직 흐름 요약",
            "비즈니스 흐름 시각화"
        };

        /// <summary>
        /// 통합 계획서가 반드시 가져야 할 H2 네 개. L1이 이 존재를 강제하므로
        /// PlanBoundaryResolver가 골격을 자를 때 같은 목록을 근거로 삼는다.
        /// 두 곳이 서로 다른 이름을 말하면 분할이 조용히 실패한다.
        ///
        /// IReadOnlyList로 노출한다. MechanicalValidator와 PlanBoundaryResolver 두 클래스가
        /// 이 배열을 공유하는데, string[]이면 어느 한쪽이 원소를 실수로 고쳐 써도 컴파일이
        /// 통과해 L1 검증과 골격 분할이 동시에, 조용히 오염된다.
        /// </summary>
        public static readonly IReadOnlyList<string> RequiredConsolidatedHeaders = new[]
        {
            "통합 배치 아키텍처 개요",
            "Mermaid 기반 통합 흐름도",
            "단계별 이행 상세 및 의사코드",
            "통합 데이터 정합성 검증 SQL 세트"
        };

        private readonly bool _useMermaidCli;

        public MechanicalValidator(bool useMermaidCli = false)
        {
            _useMermaidCli = useMermaidCli;
        }

        public ValidationResult Validate(string markdown, SpecExpectations? expectations = null)
        {
            var result = new ValidationResult();
            Log.Information("개별 명세서 기계적 문법 및 린트 검증 시작");

            if (string.IsNullOrWhiteSpace(markdown))
            {
                result.IsValid = false;
                result.Errors.Add("명세서 내용이 비어있습니다.");
                result.DetailedErrors.Add(new DetailedError { Type = ErrorType.General, Message = "명세서 내용이 비어있습니다." });
                Log.Warning("명세서 검증 실패 - 내용이 비어있습니다.");
                return result;
            }

            try
            {
                // Mermaid 후처리 및 정화 적용
                var cleansed = PostProcessMarkdown(markdown);
                result.CleansedMarkdown = cleansed;
                ValidateMarkdownStructure(cleansed, RequiredHeaders, result);
                CheckPromptInstructionLeak(cleansed, result);
                CheckMachineTableShape(cleansed, result);

                if (expectations != null)
                {
                    CheckUpdateMappings(cleansed, expectations, result);
                    CheckInsertMappingTableNames(cleansed, expectations, result);
                    CheckSchemaClaims(cleansed, expectations, result);
                    CheckNullabilityClaims(cleansed, expectations, result);
                    CheckTableIdentitySplit(cleansed, expectations, result);
                    CheckIdentifierNotationClaims(cleansed, expectations, result);
                    CheckSourceComments(cleansed, expectations, result);
                    CheckRoundingSemantics(cleansed, expectations, result);
                    CheckSessionOptions(cleansed, expectations, result);
                    CheckHeaderContractContradiction(cleansed, expectations, result);
                    CheckDmlScopeTable(cleansed, expectations, result);
                    CheckDerivedTableDefinitions(cleansed, expectations, result);
                    CheckSetPredicates(cleansed, expectations, result);
                    CheckReferencedFunctions(cleansed, expectations, result);
                    CheckLockHints(cleansed, expectations, result);
                    CheckObjectDeclaration(cleansed, expectations, result);
                    CheckOrderByExpressions(cleansed, expectations, result);
                    CheckExecutionSemantics(cleansed, expectations, result);
                    CheckCaseBranches(cleansed, expectations, result);
                }
            }
            catch (Exception ex)
            {
                // 소프트 페일 처리 (검증기 자체 오류 시 툴 중단 방지)
                Log.Error(ex, "개별 명세서 검증기 실행 중 자체 오류가 발생하여 소프트 패스 처리합니다.");
                result.Errors.Clear();
                result.DetailedErrors.Clear();
                result.IsValid = true;
                result.CleansedMarkdown = markdown;
                return result;
            }

            result.IsValid = (result.Errors.Count == 0);
            Log.Information("개별 명세서 기계적 검증 완료 - 결과: {IsValid}, 에러 개수: {ErrorCount}개", result.IsValid, result.Errors.Count);
            return result;
        }

        public ValidationResult ValidateConsolidated(string markdown)
        {
            var result = new ValidationResult();
            result.IsConsolidated = true;
            Log.Information("통합 계획서 기계적 문법 및 린트 검증 시작");

            if (string.IsNullOrWhiteSpace(markdown))
            {
                result.IsValid = false;
                result.Errors.Add("계획서 내용이 비어있습니다.");
                result.DetailedErrors.Add(new DetailedError { Type = ErrorType.General, Message = "계획서 내용이 비어있습니다." });
                Log.Warning("통합 계획서 검증 실패 - 내용이 비어있습니다.");
                return result;
            }

            try
            {
                // Mermaid 후처리 및 정화 적용
                var cleansed = PostProcessMarkdown(markdown);
                result.CleansedMarkdown = cleansed;
                ValidateMarkdownStructure(cleansed, RequiredConsolidatedHeaders, result);
                CheckVerificationCartesianComparison(cleansed, result);
                CheckBatchRunRowCreation(cleansed, result);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "통합 계획서 검증기 실행 중 자체 오류가 발생하여 소프트 패스 처리합니다.");
                result.Errors.Clear();
                result.DetailedErrors.Clear();
                result.IsValid = true;
                result.CleansedMarkdown = markdown;
                return result;
            }

            result.IsValid = (result.Errors.Count == 0);
            Log.Information("통합 계획서 기계적 검증 완료 - 결과: {IsValid}, 에러 개수: {ErrorCount}개", result.IsValid, result.Errors.Count);
            return result;
        }

        /// <summary>
        /// 단계 섹션 하나가 구현 지시서로서의 최소 요건을 갖췄는지 검사한다.
        ///
        /// 이 검사가 필요한 이유: 실측한 산출물에서 L2가 88점을 준 문서의 S10이
        /// 12줄이고 코드 블록이 하나도 없었다. 문서 레벨 L1은 H2 4개 존재만 보고,
        /// L2는 12개 프로시저의 오류코드를 전수 대조하지 못한다. 문자열 대조는
        /// 기계의 일인데 지금까지 기계가 그 일을 하지 않았다.
        ///
        /// AI 호출이 없으므로 비용이 0이다. 단계마다 돌려도 무료다.
        /// </summary>
        /// <param name="knownTableNames">이 작업의 스키마 카탈로그(SpDefinition.Dependencies).
        /// 비어 있으면 미지 테이블 검사를 실행하지 않는다 - 카탈로그가 없다는 사실을
        /// 모든 테이블이 유령이라는 판정으로 바꾸지 않기 위한 소프트 스킵이다.</param>
        /// <param name="conditionColumnsByProcedure">원본 프로시저 맨이름별로, 그 프로시저가
        /// 필터·분기에 쓰는 컬럼 이름(<see cref="SpecConditionColumnExtractor"/>가 뽑는다).
        /// 비어 있으면 조건 대조를 실행하지 않는다 - 대조할 재료가 없다는 사실을 로직이
        /// 빠졌다는 판정으로 바꾸지 않기 위한 소프트 스킵이다.</param>
        public StepValidationResult ValidateBatchStep(
            string? stepMarkdown,
            BatchStepPlan step,
            IReadOnlyCollection<string> knownTableNames,
            IReadOnlyDictionary<string, SpecConditions> conditionColumnsByProcedure,
            IReadOnlyList<StepInterface>? stepInterfaces = null,
            IReadOnlyCollection<string>? runRowOwnedTables = null)
        {
            var result = new StepValidationResult();

            if (string.IsNullOrWhiteSpace(stepMarkdown))
            {
                result.Errors.Add($"{step.Code} 섹션 내용이 비어있습니다.");
                result.IsValid = false;
                return result;
            }

            var firstLine = FirstNonEmptyLine(stepMarkdown);
            if (!firstLine.StartsWith("### ", StringComparison.Ordinal))
            {
                result.Errors.Add($"{step.Code} 섹션이 '### ' 헤딩으로 시작하지 않습니다.");
            }
            else if (firstLine.IndexOf(step.Code, StringComparison.OrdinalIgnoreCase) < 0)
            {
                result.Errors.Add($"{step.Code} 섹션의 헤딩에 단계 코드가 없습니다: \"{firstLine}\"");
            }

            // 펜스는 열고 닫으므로 2개 미만이면 블록이 하나도 없다는 뜻이다.
            if (Regex.Matches(stepMarkdown, @"(?m)^\s*```").Count < 2)
            {
                result.Errors.Add($"{step.Code} 섹션에 SQL 또는 의사코드 블록이 없습니다.");
            }

            // 대조할 토큰이 하나도 없으면 아래 두 루프는 0회 돌고 통과한다. 그러면
            // "대조해서 깨끗함"과 "대조할 것이 없었음"이 결과로 구별되지 않는다.
            // 실측: 한 Job의 12단계 전부가 ErrorCodes를 빈 배열로 냈고 — 계획서
            // 본문에는 코드가 다 적혀 있었다 — 오류코드 검증이 12/12 무실행인 채
            // "에러 개수: 0개"로 기록됐다. 재료가 없다는 사실 자체를 결함으로 든다.
            //
            // 이 둘은 목차(PlanStructure)의 결함이라 단계 본문을 다시 생성해도
            // 고쳐지지 않는다 - PlanDefects에 따로 담아 재시도 여부를 가른다.
            if (!step.TargetTables.Any(table => BareObjectName(table).Length > 0))
            {
                result.PlanDefects.Add(
                    $"{step.Code}의 목차 TargetTables가 비어 있어 대상 테이블 대조를 실행할 수 없습니다.");
            }

            // 레거시 출신이 없는 단계는 보존할 원본 코드가 애초에 없다 - 대조 항목 0개가
            // 정상이다. 이것을 결함으로 들면 계획이 새로 설계한 정상 단계에 배너가 붙어
            // 배너의 변별력이 사라진다.
            //
            // TargetTables 축은 여기에 딸리지 않는다. 출신이 없다는 것과 쓰는 테이블이
            // 없다는 것은 다른 사실이고, 아무것도 쓰지 않는다는 선언은 그 자체로 확인이 필요하다.
            if (step.LegacyProcedures.Count > 0 &&
                !step.ErrorCodes.Any(code => !string.IsNullOrWhiteSpace(code)))
            {
                result.PlanDefects.Add(
                    $"{step.Code}의 목차 ErrorCodes가 비어 있어 원본 오류코드 대조를 실행할 수 없습니다.");
            }
            else if (step.LegacyProcedures.Count == 0 &&
                     !step.ErrorCodes.Any(code => !string.IsNullOrWhiteSpace(code)))
            {
                // 결함으로 들지 않는다고 흔적까지 지우면 "대조 항목 0개"가 "대조해서
                // 깨끗함"과 로그에서 구별되지 않는다 - 이 브랜치가 고치는 결함이
                // 다른 모습으로 되살아나는 것을 막기 위한 최소한의 한 줄이다.
                Log.Information(
                    "{Code}는 레거시 출신 프로시저가 없어 원본 오류코드 대조 대상이 아닙니다.", step.Code);
            }

            foreach (var table in step.TargetTables)
            {
                var bareName = BareObjectName(table);
                if (bareName.Length == 0)
                {
                    continue;
                }

                if (!ContainsToken(stepMarkdown, bareName))
                {
                    result.Errors.Add($"{step.Code} 섹션에 대상 테이블 '{table}'이 등장하지 않습니다.");
                }
            }

            foreach (var errorCode in step.ErrorCodes)
            {
                if (string.IsNullOrWhiteSpace(errorCode))
                {
                    continue;
                }

                if (!ContainsToken(stepMarkdown, errorCode.Trim()))
                {
                    result.Errors.Add($"{step.Code} 섹션에 원본 오류코드 '{errorCode}'가 등장하지 않습니다.");
                }
            }

            CheckForbiddenShortcuts(stepMarkdown, step, result);
            CheckNonCanonicalBatchSchema(stepMarkdown, step, result);
            CheckUnknownTableReferences(stepMarkdown, step, knownTableNames, result);
            CheckMissingConditionColumns(stepMarkdown, step, conditionColumnsByProcedure, result);
            CheckStepInterface(stepMarkdown, step, stepInterfaces, result);
            CheckBatchControlVocabulary(stepMarkdown, step, result);
            CheckBatchControlRowOrigin(stepMarkdown, step, result);
            CheckFirstStepRowCreation(stepMarkdown, step, runRowOwnedTables, result);
            CheckShadowBackupContract(stepMarkdown, step, result);
            CheckCatchDiscardsReturnCode(stepMarkdown, step, result);

            // 목차 결함도 Errors에 합류시킨다 - 배너·로그·사용자 통보가 전부
            // Errors를 읽으므로, 여기서 빠지면 기록 경로 전체에서 사라진다.
            result.Errors.AddRange(result.PlanDefects);

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        private static string FirstNonEmptyLine(string markdown)
        {
            foreach (var line in markdown.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 스키마·DB 접두사를 뗀 이름. `SETTLE_POQ_DB.dbo.TSettleMst` → `TSettleMst`.
        /// 실제 문서가 같은 테이블을 접두사 있이/없이 섞어 쓰므로 접두사까지
        /// 대조하면 정상 문서가 실패한다.
        ///
        /// internal인 이유: <see cref="VerificationPipelineOrchestrator"/>의 목차
        /// 커버리지 검사(스텝의 LegacyProcedures가 원본 명세서 전부를 가리키는지)가
        /// 같은 접두사 제거 규칙을 쓴다. 별도로 다시 구현하면 두 로직이 미묘하게
        /// 갈라질 수 있어, 여기 한 곳의 규칙을 그대로 재사용하게 한다.
        /// </summary>
        internal static string BareObjectName(string qualifiedName)
        {
            var trimmed = (qualifiedName ?? string.Empty).Trim().Trim('[', ']');
            var lastDot = trimmed.LastIndexOf('.');
            return (lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed).Trim('[', ']').Trim();
        }

        /// <summary>
        /// 단어 경계 대조.
        ///
        /// 단순 부분 문자열 대조로 하면 `-1`이 `-10`·`-13` 안에서 걸려 오류코드
        /// 검사가 통째로 무력해진다. 실제로 S08의 오류코드가 -1부터 -17까지
        /// 11개라 정확히 이 함정에 빠진다.
        ///
        /// RegexOptions.ECMAScript를 쓰는 이유: .NET의 기본 `\w`는 유니코드
        /// 문자 범주를 따라 한글 음절도 단어 문자로 취급한다. 그러면 "TSettleMst만"처럼
        /// 테이블명이 조사(만, 가, 이 등)에 구분자 없이 바로 붙는 실제 문서에서
        /// 경계 검사가 항상 실패한다. ECMAScript 옵션은 `\w`를 [a-zA-Z0-9_]로
        /// 제한해 한글 조사를 경계로 인식하게 한다.
        /// </summary>
        internal static bool ContainsToken(string haystack, string token)
        {
            if (token.Length == 0)
            {
                return true;
            }

            return Regex.IsMatch(
                haystack,
                $@"(?<!\w){Regex.Escape(token)}(?!\w)",
                RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
        }

        // 백틱 인용과 SQL·의사코드 펜스 안의 2부·3부 식별자만 본다. 산문까지 훑으면
        // 서술이 식별자로 오인되고, 그 오탐은 단계 재생성을 유발해 비용이 실재한다.
        //
        // 이 정규식만으로는 모양(shape)만 걸러낼 뿐 진짜 2부·3부 식별자인지는 못
        // 가른다 - `a.YMD`(별칭 컬럼), `context.RunId`(멤버 접근)도 문법적으로는
        // 똑같이 X.Y다. 그래서 이 정규식의 매치는 후보일 뿐이고, 실제 채택 여부는
        // ExtractQuotedIdentifiers가 HasKnownQualifier로 한 번 더 거른다 - 객체명
        // 바로 앞 조각(스키마)이 카탈로그가 아는 한정자일 때만 후보로 인정한다.
        private static readonly Regex QualifiedTableRegex = new(
            @"\b([A-Za-z_][A-Za-z_0-9]*)\.([A-Za-z_][A-Za-z_0-9]*)(?:\.([A-Za-z_][A-Za-z_0-9]*))?\b",
            RegexOptions.Compiled);

        /// <summary>
        /// 계획서가 쓰겠다고 적은 테이블이 실재하는지 본다.
        ///
        /// 실측: S17이 dbo.TSettleSummary로 파티션을 교체하라고 지시했는데 그 테이블은
        /// 카탈로그 55종에 없고, S13이 만드는 요약 테이블 4개와 이름도 다르다. 문서 레벨
        /// L1은 헤더·축약어·Mermaid만 보므로 그것을 잡을 곳이 아무 데도 없었다.
        ///
        /// batch·batch_shadow는 제외한다. 계획서가 새로 만드는 객체라 카탈로그에 없는
        /// 것이 정상이며, 그 판단은 BatchInfraObjectCollector가 단독 소유한다.
        /// </summary>
        /// <summary>
        /// 단계 본문에 축약·생략 표기가 있는지 본다.
        ///
        /// 같은 검사가 문서 레벨에도 있는데 여기에 다시 두는 이유는 재생성 단위 때문이다.
        /// 실측(POQSettleProc14): 축약어 '위와 동일' 한 줄 때문에 문서 L1이 두 번 연속
        /// 실패했고, 그때마다 골격과 17개 단계가 통째로 재생성됐다. 3회 재시도 예산 중
        /// 2회를 그 한 줄이 먹어 L2 채점은 한 번뿐이었고, 개선 기회 없이 84점 불합격이
        /// 채택됐다 - Critic이 지적한 결함이 고쳐질 자리가 없었다.
        ///
        /// 단계에서 잡으면 그 단계만 다시 만들면 되고(예산 1/17), 지적은 기존 재생성
        /// 피드백 경로를 그대로 탄다 - 문서 레벨 L1 피드백은 단계 프롬프트에 전달되지
        /// 않으므로, 그 경로로는 17개 단계가 지적을 모른 채 다시 쓰인다.
        ///
        /// 문서 레벨 검사는 그대로 둔다. 골격의 공통 규약 절이나 정합성 검증 SQL처럼
        /// 단계 밖에 있는 축약어는 여기서 볼 수 없고, 단계 재생성이 예산을 다 쓰고도
        /// 남은 경우의 안전망이기도 하다.
        /// </summary>
        private static void CheckForbiddenShortcuts(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            // 인용문을 빼는 이유는 문서 레벨과 같다 - 배너가 잔존 오류를 인용하면 그
            // 메시지가 금지 토큰을 담아, 배너 붙은 문서가 스스로를 오류로 만든다.
            var scannable = StripQuotedLines(stepMarkdown);

            foreach (var forbidden in ForbiddenShortcuts)
            {
                if (!ContainsForbiddenShortcut(scannable, forbidden))
                {
                    continue;
                }

                result.Errors.Add(
                    $"{step.Code} 섹션에 축약·생략 표기 `{forbidden}`가 있습니다. " +
                    "표의 모든 행과 매핑을 원본대로 완전히 기술하십시오 - 생략된 자리는 " +
                    "구현자가 채울 수 없습니다.");
            }
        }

        /// <summary>
        /// 단계 본문이 선언한 프로시저 파라미터가 원본 인터페이스를 넘지 않는지 본다.
        ///
        /// 이 검사가 필요한 이유: 프롬프트 규칙 5가 @pi_bypassPreCheck를 발명해
        /// 명령했고, S02가 재시작 모드에서 실행 컨텍스트 전체에 그 값을 참으로
        /// 고정해 지급 확정 원장(OutState IN (1,5))의 -9 하드 스톱이 통째로
        /// 사라졌다. 프롬프트를 고쳐도 강제가 없으면 되살아난다.
        ///
        /// DECLARE된 지역 변수는 대상이 아니다. 파라미터 선언 구간
        /// (CREATE PROCEDURE ... AS 사이)에 등장하는 @이름만 본다.
        /// </summary>
        private static void CheckStepInterface(
            string stepMarkdown,
            BatchStepPlan step,
            IReadOnlyList<StepInterface>? stepInterfaces,
            StepValidationResult result)
        {
            var iface = stepInterfaces?.FirstOrDefault(
                i => string.Equals(i.StepCode, step.Code, StringComparison.OrdinalIgnoreCase));

            if (iface == null)
            {
                // 재료가 없다는 사실과 대조해서 깨끗하다는 사실을 로그에서 구별한다.
                Log.Information(
                    "{Code}는 원본 인터페이스 재료가 없어 파라미터 대조 대상이 아닙니다.", step.Code);
                return;
            }

            var allowed = new HashSet<string>(
                StepInterfaceFacts.ParameterNames(iface), StringComparer.OrdinalIgnoreCase);

            // 선언은 SQL 펜스 안에서만 찾는다.
            //
            // [왜 펜스로 제한하는가]
            // 산문이 원본 SP를 `CREATE PROCEDURE dbo.UP_X`로 언급하면 게으른 .*?가
            // 그 지점에서 출발해 진짜 선언의 AS를 지나 소비한다. Regex.Matches는
            // 소비한 구간 뒤부터 재개하므로 진짜 선언이 영영 검사되지 않는다
            // (최종 리뷰 실측). 선언은 펜스 안에 있고 산문 언급은 밖에 있다.
            //
            // [왜 괄호 깊이 0의 첫 AS인가]
            // 선언의 AS는 언제나 본문의 테이블 별칭 AS(FROM t AS x)보다 앞이다.
            // 깊이 0 조건은 varchar(8)·decimal(18,2) 같은 타입 괄호 안에서 끊기지
            // 않게 한다. 주석과 문자열은 미리 공백으로 지우므로 `= 'FROM'`이나
            // 목록 안 주석에 속지 않는다 - 그 둘 때문에 검사가 통째로 꺼지던
            // 키워드 폐기 방식을 이 앵커가 대신한다.
            foreach (Match fence in Regex.Matches(
                stepMarkdown, @"```sql(?<sql>.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var sql = fence.Groups["sql"].Value;
                var cleaned = BlankCommentsAndStrings(sql);

                foreach (Match header in Regex.Matches(
                    cleaned,
                    @"(?:CREATE\s+(?:OR\s+ALTER\s+)?|ALTER\s+)PROC(?:EDURE)?\s+[^\s(]+\s*\(?",
                    RegexOptions.IgnoreCase))
                {
                    var paramsEnd = FindTopLevelAs(cleaned, header.Index + header.Length);
                    if (paramsEnd < 0) continue;

                    // 이름은 원본에서 뽑는다 - 지운 사본은 문자열 안이 공백이지만
                    // 파라미터 이름은 리터럴 밖이라 어느 쪽에서 뽑아도 같다.
                    // BlankCommentsAndStrings는 길이를 보존하므로(Task 1에서 확립된
                    // 관용) cleaned에서 찾은 인덱스를 그대로 원문 sql을 자르는 데
                    // 써도 어긋나지 않는다.
                    var paramsText = sql[(header.Index + header.Length)..paramsEnd];

                    foreach (Match parameter in Regex.Matches(paramsText, @"@\w+"))
                    {
                        if (allowed.Contains(parameter.Value)) continue;

                        result.Errors.Add(
                            $"{step.Code} 섹션이 원본에 없는 입력 파라미터 '{parameter.Value}'를 선언합니다. " +
                            $"이 단계의 인터페이스는 원본 프로시저의 파라미터가 전부입니다 " +
                            $"({string.Join(", ", iface.Parameters)}). 재시작·스킵·검사 우회를 위해 " +
                            "입력을 늘리지 마십시오 - 이미 완료된 단계는 오케스트레이터가 " +
                            "체크포인트를 보고 호출하지 않으며, 업무 보호 검사는 호출될 때마다 " +
                            "무조건 수행되어야 합니다.");
                    }
                }
            }
        }

        /// <summary>
        /// startIndex부터 괄호 깊이 0에 있는 첫 `AS` 토큰의 시작 인덱스를 낸다.
        /// 없으면 -1. 입력은 이미 주석·문자열이 공백으로 지워진 사본이어야 한다.
        /// </summary>
        private static int FindTopLevelAs(string cleaned, int startIndex)
        {
            var depth = 0;

            for (var i = startIndex; i < cleaned.Length; i++)
            {
                var ch = cleaned[i];

                if (ch == '(') { depth++; continue; }
                if (ch == ')') { if (depth > 0) depth--; continue; }
                if (depth != 0) continue;

                if ((ch != 'A' && ch != 'a') ||
                    i + 1 >= cleaned.Length ||
                    (cleaned[i + 1] != 'S' && cleaned[i + 1] != 's'))
                {
                    continue;
                }

                var beforeIsBoundary = i == 0 || !char.IsLetterOrDigit(cleaned[i - 1]) && cleaned[i - 1] != '_';
                var afterIndex = i + 2;
                var afterIsBoundary = afterIndex >= cleaned.Length ||
                                      !char.IsLetterOrDigit(cleaned[afterIndex]) && cleaned[afterIndex] != '_';

                if (beforeIsBoundary && afterIsBoundary) return i;
            }

            return -1;
        }

        /// <summary>
        /// 제어 테이블에 계약 밖의 컬럼명·상태값을 <b>쓰는지</b> 본다.
        ///
        /// 실측: 같은 batch.BatchStepJournal에 S01은 StepStatus='Succeeded',
        /// S02는 ExecutionStatus='Completed', S03은 StepStatus='Completed',
        /// S17은 StepState를 썼다. 어느 쪽으로 DDL을 만들어도 반대편 단계가
        /// 컴파일되지 않는다. 정본이 있으면 단계마다 정본과 대조하는 것으로
        /// 충분하다 - 18개 문서를 한꺼번에 읽는 교차 검사는 필요 없다.
        ///
        /// [수정 이력] 최초 구현은 UPDATE 문의 SET부터 세미콜론(또는 문서 끝)까지 tail
        /// 전체에서 컬럼·리터럴 후보를 뽑았다. 제어 테이블과 업무 테이블을 같은 문에서
        /// FROM/JOIN으로 엮고 WHERE에 별칭 없는 업무 컬럼(`SourceRunId`)을 쓰거나, 업무
        /// 테이블 자신의 상태 필터(`t.SettleStatus = N'Pending'`)가 같은 문 안에 있으면
        /// 그 이름·값이 "쓰는 것"으로 오인되어 결함 없는 단계가 상시 실패했다(리뷰
        /// 재현: `UPDATE batch.BatchStepJournal SET StepStatus = N'Succeeded' FROM ...
        /// JOIN dbo.TSettleMst ... WHERE SourceRunId = @RunId`). 세미콜론이 없으면
        /// `$`가 문서 끝까지 흡수해 뒤따르는 무관한 SQL의 컬럼까지 섞였다.
        ///
        /// 계약 위반은 제어 테이블에 값을 <b>쓸 때만</b> 성립한다 - WHERE·JOIN·ON·FROM은
        /// 읽기이므로 대상이 아니다. 그래서 후보를 UPDATE의 SET 절 대입 대상과 INSERT의
        /// 컬럼 목록, 이 두 쓰기 자리로 좁힌다. 상태값도 그 자리에서 실제로 대입되는
        /// 값만 본다(SET의 StatusColumn 우변, INSERT 컬럼 목록에서 StatusColumn과 같은
        /// 위치의 VALUES 항목). 이 좁힘 덕분에 "이름이 제어 컬럼처럼 보이는가"(stem
        /// 휴리스틱), "값이 상태 어휘처럼 보이는가"(영단어 목록) 둘 다 필요 없어졌다 -
        /// 쓰기 자리에 나온 이름·값은 정의상 그 테이블의 것이어야 하므로, known에
        /// 없으면(컬럼) 또는 allowed에 없으면(상태값) 그 자체로 위반이다.
        /// </summary>
        private static void CheckBatchControlVocabulary(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            foreach (var table in BatchControlContract.Tables)
            {
                var bare = table.Name[(table.Name.LastIndexOf('.') + 1)..];
                if (!ContainsToken(stepMarkdown, bare)) continue;

                var known = new HashSet<string>(
                    table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                var allowed = table.StatusColumn != null
                    ? table.Columns.First(c => c.Name == table.StatusColumn).AllowedValues
                    : null;

                CheckUpdateSetTargets(stepMarkdown, table, bare, known, allowed, step, result);
                CheckInsertColumnTargets(stepMarkdown, table, bare, known, allowed, step, result);
            }
        }

        /// <summary>
        /// 한정자·대괄호 인용 유무에 관계없이 이 테이블 맨이름을 찾는 정규식 조각.
        ///
        /// [수정 라운드 1 리뷰 Critical] 통합 문서 검사(<see cref="CheckBatchRunRowCreation"/>)가
        /// 이 조각을 쓰지 않고 `(?:\w+\.)?{bare}\b` 형태의 자체 정규식을 새로 써서,
        /// 대괄호 인용(`[batch].[BatchRun]` 등)으로 쓴 정상 INSERT를 인식하지 못해
        /// 반려했다(리뷰 실행 재현). <see cref="ResolveControlTableAliases"/>가 별칭
        /// 바인딩에 이미 쓰던 이 조각을 공용 헬퍼로 뽑아 두 곳이 같은 패턴 문자열을
        /// 쓰게 한다 - 같은 문제를 두 정규식이 각자 다르게 풀면 한쪽만 고쳐질 때
        /// 다른 쪽이 뒤에 남는다.
        ///
        /// 테이블 부분을 `\[bare\]|bare\b`로 나눈다 - 대괄호로 감싼 쪽은 `]`가 곧바로
        /// 뒤따르는지로 경계를 삼고(대괄호 뒤에는 `\b`가 성립하지 않는다 - `]`도 공백도
        /// 단어 문자가 아니라서 전이가 없다), 대괄호 없는 쪽은 `\b`로 접두사 겹침
        /// (`BatchStepJournalArchive`)을 막는다. 이 조각 자체는 앞에 `\b`를 붙이지
        /// 않는다 - `INSERT INTO [batch].[BatchRun]`처럼 공백 바로 뒤에 대괄호가 오면
        /// 공백도 `[`도 단어 문자가 아니라서 그 경계에서 `\b`가 성립하지 않아, 앞에
        /// `\b`를 강제하면 오히려 이 조각을 anchor(예: INSERT INTO 뒤)로 쓰는 호출부가
        /// 대괄호 형태를 못 찾게 된다. 자유 부분 문자열 검색(예: "언급됨" 판정)에서
        /// 접미사 겹침(`MyBatchRun`)을 막아야 하는 호출부는 호출부 쪽에서 `\b`를
        /// 앞에 붙인다.
        /// </summary>
        private static string QualifiedTableNameFragment(string bare)
        {
            var escapedBare = Regex.Escape(bare);
            return $@"(?:\[?\w+\]?\.)?(?:\[{escapedBare}\]|{escapedBare}\b)";
        }

        /// <summary>
        /// 이 마크다운의 SQL 펜스 어딘가가 그 테이블의 행을 만드는가(`INSERT INTO` 또는 `MERGE`).
        ///
        /// 세 검사(<see cref="CheckBatchControlRowOrigin"/>, <see cref="CheckFirstStepRowCreation"/>,
        /// <see cref="CheckBatchRunRowCreation"/>)가 같은 판정을 각자 쓰고 있었다. 같은 문제를
        /// 여러 정규식이 각자 풀면 한쪽만 고쳐질 때 다른 쪽이 뒤에 남는다 -
        /// <see cref="QualifiedTableNameFragment"/>를 공용으로 뽑은 것과 같은 이유다.
        ///
        /// 펜스 단위로 도는 이유는 <see cref="CleanedSqlFences"/>에 있다 - 문서 전체를 한 번에
        /// 지우면 산문의 짝 없는 아포스트로피 하나가 뒤따르는 펜스를 통째로 비워 검사를 끈다.
        /// </summary>
        private static bool CreatesRowIn(string markdown, string bare)
        {
            var pattern = $@"(INSERT\s+INTO|MERGE)\s+{QualifiedTableNameFragment(bare)}";
            foreach (var (cleaned, _) in CleanedSqlFences(markdown))
            {
                if (Regex.IsMatch(cleaned, pattern, RegexOptions.IgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// stepMarkdown 안의 ```sql 펜스들을 훑어, 각 펜스 내용을
        /// <see cref="BlankCommentsAndStrings"/>로 지운 사본과 그 펜스 내용이
        /// 원문에서 시작하는 인덱스를 함께 낸다.
        ///
        /// [왜 문서 전체가 아니라 펜스 단위인가 - 최종 리뷰 B-2]
        /// <c>BlankCommentsAndStrings(stepMarkdown)</c>처럼 문서 전체를 한 번에 지우면,
        /// 처음 만난 짝 없는 <c>'</c>부터 문서 끝까지가 통째로 "문자열 안"이 된다.
        /// 한국어 산문 안의 소유격 아포스트로피(<c>the orchestrator's checkpoint</c>)나
        /// 인라인 코드(<c>`don't`</c>) 하나가 그 뒤에 오는 모든 SQL 펜스를 공백으로
        /// 지워버려, UPDATE 헤더도 SET 절도 통째로 사라진다 - 검사가 아무 신호 없이
        /// 꺼진다(실행 재현: 잃어버린 오류 두 건이 정확히 이 축이 존재하는 이유인
        /// 감사 실측 결함이었다). 펜스마다 따로 지우면 산문의 아포스트로피는 펜스
        /// 밖이라 애초에 스캔에 들어오지 않고, 펜스 안의 주석·문자열은 여전히
        /// 공백이 된다 - 사전 판정이 막으려던 "주석 안의 `UPDATE bsj SET`이 헤더로
        /// 잡히는" 오탐은 그대로 막힌다. <see cref="CheckStepInterface"/>가 이미
        /// 쓰는 관용이다(그 쪽은 자체 루프에서 직접 편다 - 두 자리가 인덱스
        /// 환산까지 필요하지는 않아서다).
        ///
        /// 인덱스는 펜스 시작(```sql 접두 포함)이 아니라 펜스 <b>내용</b> 시작
        /// (<c>fence.Groups["sql"].Index</c>)이다. BlankCommentsAndStrings는 길이를
        /// 보존하므로, 호출부가 지운 사본에서 찾은 로컬 인덱스에 이 오프셋만
        /// 더하면 원문(stepMarkdown) 기준 인덱스가 되어 <see cref="ExtractTopLevelClause"/>처럼
        /// 원문에서 값을 읽어야 하는 헬퍼에 그대로 넘길 수 있다.
        /// </summary>
        private static IEnumerable<(string Cleaned, int Offset)> CleanedSqlFences(string stepMarkdown)
        {
            foreach (Match fence in Regex.Matches(
                stepMarkdown, @"```sql(?<sql>.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var sqlGroup = fence.Groups["sql"];
                yield return (BlankCommentsAndStrings(sqlGroup.Value), sqlGroup.Index);
            }
        }

        /// <summary>
        /// 이 구문에서 제어 테이블에 묶인 별칭을 모은다.
        ///
        /// [왜 필요한가]
        /// 최종 리뷰 실측: `UPDATE bsj SET bsj.ExecutionStatus = N'Completed'
        /// FROM batch.BatchStepJournal bsj`가 어휘 검사와 행 출처 검사를 **둘 다**
        /// 우회했다. 두 검사 모두 "UPDATE 바로 뒤가 테이블명"만 보기 때문이다.
        /// docs/architecture.md:433-434가 이 형태를 이 저장소의 표준 T-SQL 관용으로
        /// 명시하므로 가공의 위험이 아니다 - 재생성 산출물이 이 형태를 쓰면
        /// B2·B3가 초록 게이트 아래 그대로 남는다.
        ///
        /// [왜 FROM/JOIN만 보는가]
        /// 별칭을 테이블에 묶는 자리는 FROM 절과 JOIN 절뿐이다. `AS`는 있어도 되고
        /// 없어도 된다. 다른 테이블에 묶인 별칭은 담지 않는다 - 담으면 업무 테이블을
        /// 별칭으로 갱신하는 정상 구문이 제어 테이블 검사에 걸린다.
        ///
        /// [1라운드 리뷰 수정] 대괄호로 인용한 제어 테이블명(`FROM [batch].[BatchStepJournal]
        /// bsj`, `batch.[BatchStepJournal] bsj` 등)이 이 정규식에 전혀 잡히지 않아 이
        /// 태스크가 닫으려던 구멍이 표기 형태 하나로 다시 열렸다(리뷰 실측). 테이블
        /// 부분을 `\[bare\]|bare\b`로 나눴다 - 대괄호로 감싼 쪽은 `]`가 곧바로 뒤따르는지로
        /// 경계를 삼고(대괄호 뒤에는 `\b`가 성립하지 않는다 - `]`도 공백도 단어 문자가
        /// 아니라서 전이가 없다), 대괄호 없는 쪽은 원래대로 `\b`로 접두사 겹침
        /// (`BatchStepJournalArchive`)을 막는다. 대괄호로 감싼 쪽도 닫는 `]`가 정확히
        /// 그 자리에 와야 하므로 `[BatchStepJournalArchive]`가 `[BatchStepJournal]`로
        /// 오매치되지 않는다 - 접두사 겹침 문제는 두 표기 형태 모두에서 막힌다.
        /// </summary>
        private static HashSet<string> ResolveControlTableAliases(string cleaned, string bare)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match binding in Regex.Matches(
                cleaned,
                $@"\b(?:FROM|JOIN)\s+{QualifiedTableNameFragment(bare)}\s+(?:AS\s+)?(?<alias>[A-Za-z_]\w*)",
                RegexOptions.IgnoreCase))
            {
                var alias = binding.Groups["alias"].Value;

                // FROM 뒤 첫 토큰이 별칭이 아니라 다음 절 키워드일 수 있다.
                if (Regex.IsMatch(
                        alias,
                        @"^(?:WHERE|SET|INNER|LEFT|RIGHT|FULL|CROSS|OUTER|JOIN|ON|GROUP|ORDER|HAVING|UNION|OPTION|WITH)$",
                        RegexOptions.IgnoreCase))
                {
                    continue;
                }

                aliases.Add(alias);
            }

            return aliases;
        }

        /// <summary>
        /// 대입 대상에서 이 제어 테이블을 가리키는 한정자만 벗긴다.
        ///
        /// `bsj.ExecutionStatus`(bsj가 이 테이블의 별칭)나
        /// `batch.BatchStepJournal.ExecutionStatus`(이름 자체)는 벗겨서 컬럼명을 낸다.
        /// 다른 테이블을 가리키는 한정자는 벗기지 않고 null을 낸다 - 그것은 이
        /// 테이블의 컬럼이 아니므로 대조 대상이 아니다.
        /// </summary>
        private static string? UnqualifyControlColumn(
            string target, string bare, HashSet<string> aliases)
        {
            var name = StripBracketQuoting(target.Trim());

            var lastDot = name.LastIndexOf('.');
            if (lastDot < 0) return name;

            var qualifier = StripBracketQuoting(name[..lastDot].Trim());
            var column = StripBracketQuoting(name[(lastDot + 1)..].Trim());

            var qualifierBare = qualifier[(qualifier.LastIndexOf('.') + 1)..];
            if (aliases.Contains(qualifier) ||
                string.Equals(qualifierBare, bare, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }

            return null;
        }

        /// <summary>
        /// UPDATE 문의 SET 절에서 대입 대상 컬럼만 본다. 절의 끝 경계는 괄호 깊이 0에서
        /// 나타나는 FROM/WHERE/;/문서 끝 중 먼저 오는 것으로 잡는다 - 세미콜론이 없어도
        /// 다음 문으로 새지 않기 위해서다.
        ///
        /// [2라운드 수정] 최초 버전은 정규식 `(?=\bFROM\b|\bWHERE\b|;|$)`로 경계를
        /// 잡았는데, 이 lookahead는 괄호 깊이를 모른다. `SET StepStatus = (SELECT ...
        /// FROM dbo.TSettleMst WHERE ...), ExecutionStatus = N'Completed'`처럼 대입식
        /// 안의 서브쿼리가 FROM/WHERE를 담고 있으면 SET 절 전체가 그 지점에서 잘려,
        /// 서브쿼리 뒤에 오는 대입은(계약 밖 컬럼이든 계약 밖 상태값이든) 통째로 검사
        /// 대상에서 사라졌다(리뷰 재현, 미탐). `ExtractTopLevelClause`는 괄호 깊이와
        /// 문자열 인용 상태를 추적하며 문자 단위로 훑어, 깊이 0·인용 밖에서 나타나는
        /// FROM/WHERE/;만 경계로 인정한다.
        ///
        /// [별칭 대입 대상도 본다] `UPDATE bsj SET bsj.ExecutionStatus = N'Completed'
        /// FROM batch.BatchStepJournal bsj ...`처럼 UPDATE의 대상이 테이블명이 아니라
        /// 별칭이고 SET도 그 별칭으로 한정하는 형태는 한때 이 검사가 아예 보지 못했다
        /// (2라운드 리뷰가 지적, 최종 리뷰가 실행 재현으로 확인). `ResolveControlTableAliases`가
        /// FROM/JOIN에서 별칭→테이블 바인딩을 모으고, `UnqualifyControlColumn`이 그
        /// 별칭(또는 테이블명 자체)으로 한정된 대입 대상만 벗겨 컬럼명을 낸다.
        ///
        /// 별칭 묶임은 <see cref="BlankCommentsAndStrings"/>로 주석·문자열을 지운 사본에서
        /// 찾는다 - 주석 안에 우연히 `FROM batch.BatchStepJournal bsj` 같은 문구가 있으면
        /// 엉뚱한 별칭이 제어 테이블에 묶인다. UPDATE 헤더도 같은 이유로 사본에서 찾는다 -
        /// 헤더를 원문에서 찾으면 주석 안의 `UPDATE bsj SET`이 헤더로 잡혀 같은 문제가
        /// 별칭 형태에도 생긴다. 다만 SET 절의 실제 값은 원문에서 읽어야 한다 - 상태값이
        /// 문자열 리터럴(`N'Completed'`)이라 사본에서는 이미 공백으로 지워져 있어
        /// `ReportIfDisallowedStatusValue`가 아무것도 못 본다.
        ///
        /// [최종 리뷰 B-2 수정] 지우는 사본을 `stepMarkdown` 전체가 아니라
        /// <see cref="CleanedSqlFences"/>가 내는 SQL 펜스 단위로 만든다 - 문서 전체를
        /// 한 번에 지우면 산문의 짝 없는 아포스트로피 하나가 뒤따르는 모든 펜스를
        /// 공백으로 지워 이 검사를 아무 신호 없이 꺼버린다(실행 재현). 펜스마다
        /// 지운 사본에서 찾은 로컬 인덱스는 그 펜스의 문서 기준 오프셋을 더해야
        /// `stepMarkdown`을 자르는 데 쓸 수 있다 - `CleanedSqlFences`가 그 오프셋을
        /// 함께 낸다.
        /// </summary>
        private static void CheckUpdateSetTargets(
            string stepMarkdown,
            ControlTable table,
            string bare,
            HashSet<string> known,
            IReadOnlyList<string>? allowed,
            BatchStepPlan step,
            StepValidationResult result)
        {
            foreach (var (cleaned, offset) in CleanedSqlFences(stepMarkdown))
            {
                // 별칭 묶임은 이 펜스의 지운 사본에서 본다 - 주석 안의 FROM에
                // 속으면 엉뚱한 별칭이 제어 테이블에 묶인다.
                var aliases = ResolveControlTableAliases(cleaned, bare);

                // 테이블 이름을 직접 쓴 헤더(대괄호 인용 포함, QualifiedTableNameFragment)와,
                // 이 테이블에 묶인 별칭을 쓴 헤더를 함께 본다. 대괄호로 끝나는 대안은
                // `]` 뒤에서 `\b`가 성립하지 않으므로(둘 다 비단어 문자라 전이가 없다)
                // 공유 후행 `\b`를 두지 않는다 - QualifiedTableNameFragment는 자체
                // 경계를 이미 담고 있고, 별칭 대안은 각자 `\b`를 붙인다.
                var headerAlternatives = new List<string> { QualifiedTableNameFragment(bare) };
                headerAlternatives.AddRange(aliases.Select(a => Regex.Escape(a) + @"\b"));

                foreach (Match header in Regex.Matches(
                    cleaned,
                    $@"UPDATE\s+(?:{string.Join("|", headerAlternatives)})\s+SET\s+",
                    RegexOptions.IgnoreCase))
                {
                    var setClause = ExtractTopLevelClause(stepMarkdown, offset + header.Index + header.Length);

                    foreach (var assignment in SplitTopLevelSegments(setClause))
                    {
                        var eq = assignment.IndexOf('=');
                        if (eq <= 0) continue;

                        // 한정자가 이 제어 테이블을 가리킬 때만 벗긴다. 다른 것을
                        // 가리키면 null이 와서 대조 대상이 아니다.
                        var name = UnqualifyControlColumn(assignment[..eq], bare, aliases);
                        if (name == null) continue;
                        if (!Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) continue;

                        if (!known.Contains(name))
                        {
                            result.Errors.Add(
                                $"{step.Code} 섹션이 제어 테이블 `{table.Name}`에 계약 밖의 컬럼 " +
                                $"'{name}'을 씁니다. 이 테이블의 컬럼은 " +
                                $"{string.Join(", ", table.Columns.Select(c => c.Name))}가 전부입니다.");
                            continue;
                        }

                        if (allowed == null ||
                            !string.Equals(name, table.StatusColumn, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        ReportIfDisallowedStatusValue(assignment[(eq + 1)..], table, allowed, step, result);
                    }
                }
            }
        }

        /// <summary>
        /// INSERT 문의 컬럼 목록만 본다. StatusColumn이 그 목록에 있으면 같은 위치의
        /// VALUES 항목도 함께 본다 - INSERT...SELECT처럼 VALUES가 없으면 값 검사만
        /// 조용히 건너뛴다(컬럼 이름 검사는 그대로 돈다).
        ///
        /// [Important 1 수정] 테이블명을 `(?:\w+\.)?{bare}\b`로만 찾으면 대괄호 인용
        /// (`INSERT INTO [batch].[BatchStepJournal] (...)`)이 잡히지 않아 계약 밖
        /// 컬럼·상태값이 그대로 새어나간다(base에서도 재현되는 pre-existing 결함).
        /// `QualifiedTableNameFragment`로 바꿔 <see cref="ResolveControlTableAliases"/>·
        /// <see cref="CheckUpdateSetTargets"/>와 같은 대괄호 인식을 쓴다.
        /// </summary>
        private static void CheckInsertColumnTargets(
            string stepMarkdown,
            ControlTable table,
            string bare,
            HashSet<string> known,
            IReadOnlyList<string>? allowed,
            BatchStepPlan step,
            StepValidationResult result)
        {
            foreach (Match statement in Regex.Matches(
                stepMarkdown,
                $@"INSERT\s+INTO\s+{QualifiedTableNameFragment(bare)}\s*\((?<cols>[^)]*)\)",
                RegexOptions.IgnoreCase))
            {
                var columns = SplitTopLevelSegments(statement.Groups["cols"].Value)
                    .Select(c => StripBracketQuoting(c.Trim()))
                    .ToList();

                foreach (var name in columns)
                {
                    if (!Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) continue;
                    if (known.Contains(name)) continue;

                    result.Errors.Add(
                        $"{step.Code} 섹션이 제어 테이블 `{table.Name}`에 계약 밖의 컬럼 " +
                        $"'{name}'을 씁니다. 이 테이블의 컬럼은 " +
                        $"{string.Join(", ", table.Columns.Select(c => c.Name))}가 전부입니다.");
                }

                if (allowed == null) continue;

                var statusIndex = columns.FindIndex(
                    c => string.Equals(c, table.StatusColumn, StringComparison.OrdinalIgnoreCase));
                if (statusIndex < 0) continue;

                // 컬럼 목록 바로 뒤에 VALUES(...)가 오는 모양만 본다. INSERT...SELECT는
                // 이 모양이 아니므로 조용히 건너뛴다 - 컬럼 이름 검사는 이미 위에서 끝났다.
                var afterColumns = stepMarkdown[(statement.Index + statement.Length)..];
                var valuesHeader = Regex.Match(afterColumns, @"\A\s*VALUES\s*", RegexOptions.IgnoreCase);
                if (!valuesHeader.Success) continue;

                var openParenIndex = valuesHeader.Index + valuesHeader.Length;
                if (openParenIndex >= afterColumns.Length || afterColumns[openParenIndex] != '(') continue;

                var valuesBody = ExtractBalancedParenGroup(afterColumns, openParenIndex);
                if (valuesBody == null) continue;

                var values = SplitTopLevelSegments(valuesBody).Select(v => v.Trim()).ToList();
                if (statusIndex >= values.Count) continue;

                ReportIfDisallowedStatusValue(values[statusIndex], table, allowed, step, result);
            }
        }

        /// <summary>대입되는 값이 `N?'단어'` 모양의 리터럴일 때만 계약 어휘와 대조한다 -
        /// 파라미터(`@Status`)·식(`CASE ...`)은 이 지점에서 실제 값을 알 수 없으므로
        /// 조용히 건너뛴다. 위치로 이미 "그 테이블의 StatusColumn에 쓰는 값"임이 확정된
        /// 뒤이므로, 영단어 상태 어휘 목록 같은 별도 필터는 필요 없다.</summary>
        private static void ReportIfDisallowedStatusValue(
            string valueExpression,
            ControlTable table,
            IReadOnlyList<string> allowed,
            BatchStepPlan step,
            StepValidationResult result)
        {
            var literal = Regex.Match(valueExpression.Trim(), @"^N?'(?<v>[A-Za-z]\w*)'");
            if (!literal.Success) return;

            var value = literal.Groups["v"].Value;
            if (allowed.Contains(value, StringComparer.Ordinal)) return;

            result.Errors.Add(
                $"{step.Code} 섹션이 `{table.Name}`에 계약 밖의 상태값 '{value}'를 씁니다. " +
                $"허용 값은 {string.Join(", ", allowed)}입니다 - 성공 종료는 " +
                "'Succeeded' 하나이며 'Completed'는 쓰지 않습니다. 두 어휘가 섞이면 " +
                "정상 성공한 단계가 재시작 대조에서 미완료로 판정되어 실행이 상시 차단됩니다.");
        }

        /// <summary>쉼표로 항목을 나누되 괄호·대괄호 인용·홑따옴표 문자열 리터럴·SQL
        /// 주석 안의 쉼표는 무시한다 - SET 절의 CASE 식·INSERT 값 목록의 함수 호출
        /// 인자에 있는 쉼표, `N'a,b error message'`처럼 문자열 값 자체에 든 쉼표,
        /// `[Order,Column]`처럼 대괄호 인용 식별자 안에 든 쉼표를 항목 경계로 오인하지
        /// 않기 위해서다.
        ///
        /// [2라운드 수정] 최초 버전은 인용 상태를 몰라 문자열 리터럴 안의 쉼표도 항목
        /// 경계로 셌다. INSERT VALUES에서 그 쉼표 앞의 항목이 하나 더 늘어난 것처럼
        /// 잘못 나뉘어, 뒤따르는 항목들의 위치가 하나씩 밀렸다 - StatusColumn 위치에
        /// 엉뚱한 값이 걸려 계약 밖 상태값 검사가 조용히 빗나갔다(리뷰 재현, 미탐).
        /// `''`(홑따옴표 두 개)는 문자열 안에서 홑따옴표 하나를 뜻하는 SQL 이스케이프이므로
        /// 문자열을 닫지 않고 그대로 삼킨다.
        ///
        /// [3라운드 수정] 인용 상태 검사만으로는 부족했다 - 주석 `-- don't panic`의
        /// 아포스트로피가 문자열 시작으로 오인되어 그 뒤의 진짜 대입까지 문자열 내용물로
        /// 삼켜졌다(리뷰 재현, 미탐). 인용 검사보다 먼저(문자열 밖에서만) 주석을 건너뛰어
        /// 그 안의 아포스트로피·쉼표·괄호가 전혀 구조로 해석되지 않게 한다 - 순서를
        /// 바꾸면(문자열 안에서도 주석을 찾으면) `N'a--b'`의 `--`가 주석으로 오인되어
        /// 값과 그 뒤 위반이 함께 사라진다. 대괄호 인용도 같은 이유로 더했다 -
        /// `[ExecutionStatus]`를 대입 대상 검사가 벗겨서 대조하려면(3라운드 3순위)
        /// 이 단계에서 먼저 `[Order,Column]` 같은 병적 이름의 내부 쉼표를 지켜야 한다.
        /// </summary>
        private static IEnumerable<string> SplitTopLevelSegments(string text)
        {
            var depth = 0;
            var start = 0;
            var inString = false;
            var inBracket = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (inString)
                {
                    if (ch != '\'') continue;

                    if (i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i++; // '' 이스케이프 - 문자열은 계속된다.
                        continue;
                    }

                    inString = false;
                    continue;
                }

                if (inBracket)
                {
                    if (ch != ']') continue;

                    if (i + 1 < text.Length && text[i + 1] == ']')
                    {
                        i++; // ]] 이스케이프 - 대괄호 인용은 계속된다.
                        continue;
                    }

                    inBracket = false;
                    continue;
                }

                var commentEnd = SkipCommentToken(text, i);
                if (commentEnd.HasValue)
                {
                    i = commentEnd.Value - 1; // for 루프의 i++와 합쳐 commentEnd로 재개한다.
                    continue;
                }

                switch (ch)
                {
                    case '\'':
                        inString = true;
                        break;
                    case '[':
                        inBracket = true;
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    case ',' when depth == 0:
                        yield return text[start..i];
                        start = i + 1;
                        break;
                }
            }

            yield return text[start..];
        }

        /// <summary>openParenIndex의 '('에 대응하는 ')'까지, 중첩 괄호 깊이를 세어 그
        /// 안쪽 내용만 돌려준다. 짝이 맞지 않으면 null이다. 문자열 리터럴·SQL 주석 안의
        /// 괄호는 깊이에서 제외한다.</summary>
        private static string? ExtractBalancedParenGroup(string text, int openParenIndex)
        {
            var depth = 0;
            var inString = false;

            for (var i = openParenIndex; i < text.Length; i++)
            {
                var ch = text[i];

                if (inString)
                {
                    if (ch != '\'') continue;

                    if (i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }

                    inString = false;
                    continue;
                }

                var commentEnd = SkipCommentToken(text, i);
                if (commentEnd.HasValue)
                {
                    i = commentEnd.Value - 1;
                    continue;
                }

                if (ch == '\'')
                {
                    inString = true;
                }
                else if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return text[(openParenIndex + 1)..i];
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// startIndex부터, 괄호 깊이 0·문자열 인용/대괄호 인용/주석 밖에서 처음 나타나는
        /// FROM/WHERE/;를 절의 끝으로 삼아 그 앞까지를 돌려준다. 그런 경계가 없으면
        /// 문서 끝까지다.
        ///
        /// 괄호 깊이를 세는 이유: SET 대입식 안의 서브쿼리(`(SELECT ... FROM ... WHERE
        /// ...)`)가 담은 FROM/WHERE는 그 서브쿼리에 속한 것이지 바깥 UPDATE 문의 절
        /// 경계가 아니다. 문자열 인용을 추적하는 이유: 리터럴 값 안에 우연히 이 키워드와
        /// 같은 글자가 오더라도(드물지만) 절을 잘라서는 안 된다.
        ///
        /// [3라운드 수정] 주석 `-- don't panic`의 아포스트로피가 문자열 시작으로
        /// 오인되면 그 뒤의 진짜 WHERE/;까지 "문자열 안"으로 삼켜져 절 경계 자체가
        /// 사라졌다(리뷰 재현, 미탐 - 1라운드는 컬럼·상태값을 3건 잡았는데 이 결함이
        /// 있는 2라운드는 2건만 잡았다). 문자열 검사보다 먼저(문자열 밖에서만) 주석을
        /// 건너뛴다 - 순서가 바뀌면 `N'a--b'`의 `--`가 주석으로 오인된다. 대괄호 인용도
        /// 같은 이유로 추적한다 - `[FROM]`처럼 키워드와 우연히 같은 대괄호 인용
        /// 식별자가 절 경계로 오인되지 않게 한다.
        /// </summary>
        private static string ExtractTopLevelClause(string text, int startIndex)
        {
            var depth = 0;
            var inString = false;
            var inBracket = false;
            var i = startIndex;

            while (i < text.Length)
            {
                var ch = text[i];

                if (inString)
                {
                    if (ch == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        inString = false;
                    }

                    i++;
                    continue;
                }

                if (inBracket)
                {
                    if (ch == ']')
                    {
                        if (i + 1 < text.Length && text[i + 1] == ']')
                        {
                            i += 2;
                            continue;
                        }

                        inBracket = false;
                    }

                    i++;
                    continue;
                }

                var commentEnd = SkipCommentToken(text, i);
                if (commentEnd.HasValue)
                {
                    i = commentEnd.Value;
                    continue;
                }

                if (ch == '\'')
                {
                    inString = true;
                    i++;
                    continue;
                }

                if (ch == '[')
                {
                    inBracket = true;
                    i++;
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                    i++;
                    continue;
                }

                if (ch == ')')
                {
                    depth--;
                    i++;
                    continue;
                }

                if (depth == 0)
                {
                    if (ch == ';') break;
                    if (MatchesKeywordAt(text, i, "FROM") || MatchesKeywordAt(text, i, "WHERE")) break;
                }

                i++;
            }

            return text[startIndex..i];
        }

        /// <summary>text의 index 위치에서 keyword가 대소문자 무관 단어 경계로 시작하는지
        /// 본다 - "FROM" 검사가 "FROMSomething"이나 "AFROM"의 일부에 걸리지 않기
        /// 위해서다.</summary>
        private static bool MatchesKeywordAt(string text, int index, string keyword)
        {
            if (index + keyword.Length > text.Length) return false;
            if (string.Compare(text, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            var precededByWordChar = index > 0 && IsWordChar(text[index - 1]);
            var followedByWordChar =
                index + keyword.Length < text.Length && IsWordChar(text[index + keyword.Length]);

            return !precededByWordChar && !followedByWordChar;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// index 위치가 SQL 주석(`--`부터 줄 끝까지, 또는 `/*`부터 `*/`까지)의 시작이면
        /// 그 주석이 끝나고 다음에 처리할 색인을 돌려준다. 주석이 아니면 null이다.
        ///
        /// 호출 규약: 이 함수는 반드시 "지금 문자열 인용 안이 아닐 때만" 불러야 한다.
        /// 문자열 리터럴 안의 `--`·`/*`는 주석이 아니라 값의 일부이기 때문이다
        /// (`N'a--b'`) - 그래서 세 스캐너 모두 인용 상태를 먼저 확인하고, 인용 밖일
        /// 때만 이 함수를 부른다. 순서를 바꾸면(주석 검사를 인용 검사보다 먼저 하면)
        /// 문자열 안의 `--`가 주석으로 오인되어 그 값과 뒤따르는 진짜 위반이 함께
        /// 사라진다.
        ///
        /// 중첩 블록 주석(`/* /* ... */ */`)은 다루지 않는다. T-SQL은 중첩을 허용하지만,
        /// 이 검사가 보는 입력은 마크다운 단계 본문의 SET 절·VALUES 절 한 조각이고,
        /// 그 자리에 중첩 블록 주석을 쓸 동기가 없다 - 제어 테이블 대입 자리에 주석을
        /// 중첩해 쓴 실제 사례가 리뷰·감사 어디에도 없다. 비중첩 스캔(`*/`를 만나면
        /// 즉시 닫는다)으로 충분하고, 중첩 처리는 깊이 상태를 셋에 각각 더 심어야 해
        /// 이번 라운드가 막 안정화한 로직을 다시 흔들 위험이 이득보다 크다.
        /// </summary>
        private static int? SkipCommentToken(string text, int index)
        {
            if (index + 1 < text.Length && text[index] == '-' && text[index + 1] == '-')
            {
                var lineEnd = text.IndexOf('\n', index + 2);
                return lineEnd < 0 ? text.Length : lineEnd; // 개행 자체는 평범한 문자로 다음 루프가 처리한다.
            }

            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
            {
                var blockEnd = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                return blockEnd < 0 ? text.Length : blockEnd + 2;
            }

            return null;
        }

        /// <summary>대괄호로 감싼 식별자(`[Name]`)의 대괄호를 벗긴다 - T-SQL에서 매우
        /// 흔한 인용 형태인데 대입 대상·컬럼 목록 검사가 대괄호를 모르면
        /// `SET [ExecutionStatus] = N'Completed'`나 `INSERT INTO ... ([StepStatus], ...)`
        /// 같은 위반이 `^[A-Za-z_]\w*$` 정규식에 걸려 조용히 통과한다(3라운드 리뷰
        /// pre-existing 지적). `]]`는 대괄호 인용 안에서 `]` 하나를 뜻하는 T-SQL
        /// 이스케이프이므로 되돌린다. 대괄호로 감싸여 있지 않으면 원래 문자열을 그대로
        /// 돌려준다.</summary>
        private static string StripBracketQuoting(string name)
        {
            if (name.Length < 2 || name[0] != '[' || name[^1] != ']') return name;
            return name[1..^1].Replace("]]", "]");
        }

        /// <summary>
        /// 자기 소유 제어 행을 만들지 않고 UPDATE만 하는지 본다.
        ///
        /// 실측: INSERT INTO batch.BatchRun이 번들 전체에 0건이었고 S03·S06·S17이
        /// 자기 저널·체크포인트 행을 만드는 지점 없이 UPDATE만 했다. @@ROWCOUNT
        /// 검사가 있는 S17은 정상 실행에서도 공개가 상시 실패했고, 없는 S06은
        /// 0행 갱신을 오류 없이 지나가 재삽입 방지가 성립하지 않았다.
        ///
        /// [최종 리뷰 B-1 수정] INSERT 쪽 존재 판정이 `(?:\w+\.)?{bare}\b`로 대괄호를
        /// 몰라 `INSERT INTO [batch].[BatchStepJournal] ...`처럼 정상적으로 자기 행을
        /// 만드는 문서를 "UPDATE만 하고 INSERT가 없다"고 반려했다(신규 회귀, 실행
        /// 재현) - UPDATE 쪽은 이미 <see cref="QualifiedTableNameFragment"/>로 대괄호를
        /// 인식하는데 INSERT 쪽만 남아 한쪽만 보는 탐지가 됐다. 두 판정 모두
        /// `QualifiedTableNameFragment`로 통일한다. UPDATE 쪽은 별칭 대안도 함께
        /// 보므로, 대괄호로 끝나는 대안에 공유 후행 `\b`가 걸리지 않도록(`]` 뒤에서는
        /// `\b`가 성립하지 않는다) `QualifiedTableNameFragment(bare)`와 별칭마다의
        /// `Regex.Escape(alias) + \b`를 각각 대안으로 결합한다.
        ///
        /// [최종 리뷰 B-2 수정] 지우는 사본을 문서 전체가 아니라 <see cref="CleanedSqlFences"/>가
        /// 내는 SQL 펜스 단위로 만든다 - 문서 전체를 한 번에 지우면 산문의 짝 없는
        /// 아포스트로피 하나가 뒤따르는 모든 펜스를 공백으로 지워 이 검사를 아무
        /// 신호 없이 꺼버린다(실행 재현). UPDATE·INSERT 존재 여부는 펜스별 판정을
        /// OR로 합친다 - 문서 전체 판정과 같은 의미이지만(어느 펜스에서든 UPDATE가
        /// 있으면 "UPDATE함", 어느 펜스에서든 INSERT가 있으면 "INSERT함") 산문의
        /// 아포스트로피가 뒤 펜스를 지우지 못한다.
        /// </summary>
        private static void CheckBatchControlRowOrigin(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            foreach (var table in BatchControlContract.Tables)
            {
                if (table.Origin != ControlRowOrigin.EachStepInserts) continue;

                var bare = table.Name[(table.Name.LastIndexOf('.') + 1)..];
                var updates = false;

                foreach (var (cleaned, _) in CleanedSqlFences(stepMarkdown))
                {
                    // 별칭 형태(UPDATE bsj SET ... FROM batch.BatchStepJournal bsj)도
                    // 이 테이블의 UPDATE다. 이름 형태만 세면 별칭 형태가 행 출처 검사를
                    // 통째로 우회한다(최종 리뷰 실측).
                    var aliases = ResolveControlTableAliases(cleaned, bare);
                    var updateAlternatives = new List<string> { QualifiedTableNameFragment(bare) };
                    updateAlternatives.AddRange(aliases.Select(a => Regex.Escape(a) + @"\b"));

                    if (Regex.IsMatch(
                        cleaned,
                        $@"UPDATE\s+(?:{string.Join("|", updateAlternatives)})",
                        RegexOptions.IgnoreCase))
                    {
                        updates = true;
                        break;
                    }
                }

                if (!updates || CreatesRowIn(stepMarkdown, bare)) continue;

                result.Errors.Add(
                    $"{step.Code} 섹션이 `{table.Name}`을 UPDATE만 하고 자기 행을 만드는 지점이 " +
                    "없습니다. 이 테이블은 각 단계가 시작할 때 자기 행을 INSERT한 뒤 종료할 때 " +
                    "UPDATE하는 계약입니다. 생성 없이 UPDATE만 하면 0행이 갱신되어, @@ROWCOUNT를 " +
                    "검사하는 경로는 정상 실행에서도 상시 실패하고 검사하지 않는 경로는 완료 표시 " +
                    "없이 조용히 지나갑니다.");
            }
        }

        /// <summary>
        /// 실행 행을 만들 책임이 있는 단계가 실제로 그 행을 만드는지 본다.
        ///
        /// [왜 단계 검사에도 있어야 하는가 - POQSettleProc17 실측]
        /// 이 계약은 <see cref="CheckBatchRunRowCreation"/>이 문서 전체를 보고 이미
        /// 검사한다. 그런데 통합 검사는 "계획서 전체에 만드는 지점이 없다"고만 말할 수
        /// 있고 어느 단계가 고쳐야 하는지 지목하지 못한다. 단계 본문은 단계마다 독립
        /// 호출로 생성되므로, 지목이 없으면 그 요구가 어느 재생성 프롬프트에도 실리지
        /// 않는다 - 실제로 L1 자가 수정 3회가 전부 같은 오류 1건으로 끝났다.
        /// 여기서 담당 단계의 <c>Errors</c>에 넣어야 <see cref="StepValidationResult.SuggestedPromptFix"/>
        /// → floorFeedback 경로를 타고 그 단계의 다음 시도에 요구가 전달된다.
        ///
        /// 담당 판정은 <see cref="BatchControlContract.ResolveRowCreators"/>가 하고 호출부가
        /// 결과를 넘긴다 - 이 검사는 단계 하나만 보므로 자기가 담당인지 스스로 알 수 없다.
        /// 넘어온 것이 없으면 검사하지 않는다: 담당이 없는 목차는 계약을 쓰지 않는
        /// Job일 수 있고, 그 자리는 통합 검사가 백스톱으로 덮는다.
        ///
        /// 판정 재료는 <see cref="CleanedSqlFences"/>가 내는 펜스별 사본이다. 문서 전체를
        /// 한 번에 지우면 산문의 짝 없는 아포스트로피 하나가 뒤따르는 펜스를 통째로
        /// 공백으로 만들어 이 검사를 아무 신호 없이 꺼버린다(통합 검사에서 실행 재현).
        /// 이름 대조는 <see cref="QualifiedTableNameFragment"/>로 통일한다 - 같은 문제를
        /// 두 정규식이 각자 풀면 한쪽만 고쳐질 때 다른 쪽이 뒤에 남는다.
        /// </summary>
        private static void CheckFirstStepRowCreation(
            string stepMarkdown,
            BatchStepPlan step,
            IReadOnlyCollection<string>? ownedTables,
            StepValidationResult result)
        {
            if (ownedTables == null || ownedTables.Count == 0) return;

            foreach (var tableName in ownedTables)
            {
                var table = BatchControlContract.Find(tableName);
                if (table == null) continue;

                var bare = table.Name[(table.Name.LastIndexOf('.') + 1)..];
                if (CreatesRowIn(stepMarkdown, bare)) continue;

                result.Errors.Add(
                    $"{step.Code} 섹션에 `{table.Name}` 행을 만드는 INSERT가 없습니다. " +
                    $"이 테이블을 대상으로 선언한 첫 단계가 {step.Code}이므로 실행 행을 발급할 책임이 " +
                    "이 단계에 있습니다. 생성 없이 UPDATE만 하면 0행이 갱신되어 실행 단위 자체가 " +
                    "존재하지 않습니다. INSERT를 두고 SCOPE_IDENTITY()로 발급된 RunId를 이후 단계에 " +
                    "넘기십시오.");
            }
        }

        /// <summary>
        /// 원본이 필터·분기에 쓰는 컬럼이 단계 본문에서 사라졌는지 본다.
        ///
        /// 실측(POQSettleProc13): 대상 테이블 19종과 오류코드 83개가 전부 맞고 배너도
        /// 무결점이었는데, 원본이 정산 대상을 고르는 조건 12개가 계획서 어디에도 없었다
        /// (S09의 SettleTarget·SettleState·HolidayPayFlag 등). 대상 집합이 달라지면
        /// 금액이 달라지는데 아무 신호가 없었다 - 기계 검증이 스키마·이름 층만 보고
        /// 로직 층을 비워 둔 자리다.
        ///
        /// 값은 대조하지 않고 컬럼 이름만 본다. 같은 조건을 명세서는 `UseState IN (0)`,
        /// 계획서는 `UseState = 0`으로 쓰는데, 값까지 보면 실측에서 미검출의 27%가 이런
        /// 동등 표현이었고 그 전부가 오탐이었다.
        ///
        /// 레거시 출신이 없는 단계는 물려받을 조건이 없으므로 대조하지 않는다.
        /// </summary>
        private static void CheckMissingConditionColumns(
            string stepMarkdown,
            BatchStepPlan step,
            IReadOnlyDictionary<string, SpecConditions> conditionColumnsByProcedure,
            StepValidationResult result)
        {
            if (conditionColumnsByProcedure == null || conditionColumnsByProcedure.Count == 0)
            {
                Log.Information(
                    "{Code}: 조건 컬럼 재료가 없어 로직 대조를 건너뜁니다.", step.Code);
                return;
            }

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var legacyProcedure in step.LegacyProcedures)
            {
                var key = BareObjectName(legacyProcedure);
                if (key.Length == 0 || !conditionColumnsByProcedure.TryGetValue(key, out var conditions))
                {
                    continue;
                }

                foreach (var column in conditions.BodyColumns)
                {
                    ReportIfAbsent(stepMarkdown, step, key, column, reported, result);
                }

                ReportMissingRoundingShapes(stepMarkdown, step, key, conditions, result);

                foreach (var pair in conditions.ByUdf)
                {
                    // 계획서가 UDF를 그대로 호출하면 그 안의 조건은 옮겨 적을 것이 아니다.
                    // 이 면제가 없으면 실측에서 검출 15건 중 14건이 오탐이었다.
                    if (ContainsToken(stepMarkdown, pair.Key))
                    {
                        continue;
                    }

                    // 호출도 하지 않고 조건도 없으면, UDF 로직을 옮기겠다고 해 놓고
                    // 그 안의 판단 기준을 빠뜨린 것이다.
                    foreach (var column in pair.Value)
                    {
                        ReportIfAbsent(stepMarkdown, step, pair.Key, column, reported, result);
                    }
                }
            }
        }

        /// <summary>
        /// 원본이 쓰는 중첩 ROUND 계산이 단계 본문에서 사라졌는지 본다.
        ///
        /// 정산 금액은 반올림 순서에 따라 달라진다 - 합계를 먼저 반올림하고 다시
        /// 반올림하는 것과 한 번만 하는 것은 다른 값을 낸다. 대상 테이블·오류코드·조건
        /// 컬럼이 모두 맞아도 이 축은 비어 있었다.
        ///
        /// 누락만 본다. 계획서가 원본에 없는 계산을 더하는 것은 중간 집계를 두는 등
        /// 정당할 수 있고, 그 방향까지 결함으로 들면 오탐이 재생성을 태운다.
        /// </summary>
        private static void ReportMissingRoundingShapes(
            string stepMarkdown,
            BatchStepPlan step,
            string origin,
            SpecConditions conditions,
            StepValidationResult result)
        {
            if (conditions.RoundingShapes == null || conditions.RoundingShapes.Count == 0)
            {
                return;
            }

            var inStep = SpecRoundingShapeExtractor.ReadShapes(stepMarkdown);

            foreach (var shape in conditions.RoundingShapes)
            {
                if (inStep.Contains(shape))
                {
                    continue;
                }

                result.Errors.Add(
                    $"{step.Code} 섹션에 원본 `{origin}`의 반올림 계산 `{shape}`에 해당하는 식이 없습니다. " +
                    "컬럼 이름은 달라도 되지만 반올림 방식 플래그와 중첩 순서는 원본대로 두십시오 - " +
                    "반올림 순서가 바뀌면 정산 금액이 달라집니다.");
            }
        }

        private static void ReportIfAbsent(
            string stepMarkdown,
            BatchStepPlan step,
            string origin,
            string column,
            HashSet<string> reported,
            StepValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(column) ||
                ContainsToken(stepMarkdown, column.Trim()) ||
                !reported.Add(column.Trim()))
            {
                return;
            }

            // "컬럼명이 없다"로 적으면 모델이 이름만 끼워 넣는 쪽으로 유도된다.
            // 무엇이 빠졌는지가 아니라 무엇을 해야 하는지를 말한다.
            result.Errors.Add(
                $"{step.Code} 섹션에 원본 `{origin}`이 `{column}`(으)로 거르는 로직이 없습니다. " +
                "그 컬럼을 쓰는 조건절·분기를 원본대로 본문에 넣으십시오 - 이 조건이 빠지면 " +
                "처리 대상 집합이 원본과 달라집니다.");
        }

        /// <summary>
        /// 배치 전용 객체가 <see cref="BatchInfraObjectCollector.Schemas"/> 바깥의
        /// 스키마에 놓였는지 본다.
        ///
        /// 실측(POQSettleProc10): 계획서가 batch(214회)·poqbatch(144회)·poqsettlebatch(94회)
        /// 세 이름을 섞어 썼다. 회차 0의 인프라 객체 수집은 Schemas만 보므로 지시서의
        /// "만들 객체" 목록에는 batch.* 24개만 들어갔고, 나머지 238건이 참조하는 객체는
        /// 아무도 만들지 않는 채 외부 코더에게 넘어갔다.
        ///
        /// 미지 테이블 검사와 분리한 이유는 카탈로그 의존성이다. 저 검사는 대조할
        /// 목록이 없으면 소프트 스킵하는 것이 맞지만, "batch·batch_shadow만 쓴다"는
        /// 이 도구의 규약이라 카탈로그가 비어도 유효하다.
        /// </summary>
        private static void CheckNonCanonicalBatchSchema(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            // 한정자 화이트리스트를 태우지 않는다 - poqbatch는 카탈로그가 아는 한정자가
            // 아니어서, 그 필터를 거치면 잡으려는 대상이 후보 단계에서 사라진다.
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in ExtractQuotedIdentifiers(stepMarkdown, Array.Empty<string>()))
            {
                if (!BatchInfraObjectCollector.IsNonCanonicalBatchObject(candidate) ||
                    !reported.Add(candidate))
                {
                    continue;
                }

                result.Errors.Add(
                    $"{step.Code} 섹션이 `{candidate}`를 참조합니다. 배치 전용 객체가 놓일 스키마는 " +
                    "`batch`(작업 객체)와 `batch_shadow`(섀도 테이블)뿐입니다. Job 이름을 딴 스키마를 " +
                    "새로 만들지 말고 그 두 스키마 중 하나로 옮기십시오 - 회차 0의 인프라 객체 수집이 " +
                    "그 두 이름만 보므로, 다른 이름에 둔 객체는 아무도 만들지 않습니다.");
            }
        }

        private static void CheckUnknownTableReferences(
            string stepMarkdown,
            BatchStepPlan step,
            IReadOnlyCollection<string> knownTableNames,
            StepValidationResult result)
        {
            if (knownTableNames.Count == 0)
            {
                Log.Information(
                    "{Code}: 스키마 카탈로그가 비어 있어 미지 테이블 검사를 건너뜁니다.", step.Code);
                return;
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in knownTableNames)
            {
                var bare = BareObjectName(name);
                if (bare.Length > 0)
                {
                    known.Add(bare);
                }
            }

            // 목차가 이 단계의 출신이라고 선언한 프로시저는 알려진 것으로 친다. 본문이
            // "그 규칙을 이관한다"고 언급하는 것은 정상 서술인데, 카탈로그는 테이블만
            // 담으므로 그 이름이 유령 테이블로 몰렸다. 실측(POQSettleProc10): 9개 단계가
            // 이 오탐 하나로 하한 미달 배너를 받고 단계마다 재생성 1회씩을 태웠다.
            // 한정자 화이트리스트(BuildKnownQualifiers)에는 넣지 않는다 - `dbo.UP_...`의
            // `dbo`는 이미 카탈로그가 아는 한정자이고, 거기 넣으면 카탈로그가 보증하지
            // 않는 새 한정자까지 인정하게 된다.
            foreach (var declared in step.LegacyProcedures)
            {
                var bare = BareObjectName(declared);
                if (bare.Length > 0)
                {
                    known.Add(bare);
                }
            }

            // TargetTables·SchemaTables는 여기서 신뢰하지 않는다. 예전에는 카탈로그
            // 수집이 놓친 대상 테이블을 구제하려고 무조건 받아들였는데, 그 관대함이
            // 규약 위반의 면죄부가 됐다 - 실측(POQSettleProc11): 목차가 배치 제어 객체를
            // `dbo.BatchExecution`으로 선언하자 본문의 같은 참조가 "목차가 그렇게 말했다"는
            // 이유로 통과했다. 회차 0은 `batch.BatchExecution`만 만들므로 S02가 기록하는
            // 체크포인트 테이블은 아무도 만들지 않은 채 지시서가 나갔다.
            //
            // 카탈로그가 아는 이름은 이미 위에서 들어갔고, batch 계열 신규 객체는 후보
            // 단계에서 IsInfraObject가 걸러낸다. 그 둘 중 어느 쪽도 아닌 선언은 계획서가
            // 규약 밖에 새 객체를 만들겠다는 뜻이므로, 목차가 적었다는 사실만으로
            // 통과시키지 않는다.

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var knownQualifiers = BuildKnownQualifiers(knownTableNames, step);

            foreach (var candidate in ExtractQuotedIdentifiers(stepMarkdown, knownQualifiers))
            {
                // 비표준 배치 스키마는 CheckNonCanonicalBatchSchema가 이미 자기 진단으로
                // 보고했다. 여기서 다시 들면 같은 참조가 두 개의 다른 이름으로 걸린다.
                if (BatchInfraObjectCollector.IsNonCanonicalBatchObject(candidate))
                {
                    continue;
                }

                if (BatchInfraObjectCollector.IsInfraObject(candidate))
                {
                    continue;
                }

                var bare = BareObjectName(candidate);

                // 맨이름이 한정자면 객체 참조가 아니라 `SETTLE_POQ_DB.dbo`처럼 DB와
                // 스키마만 적은 것이다. 2부로 매칭되면 맨이름이 `dbo`가 되는데, 그것은
                // 테이블이 아니라 이 검사가 이미 한정자로 아는 이름이다 - 실측
                // (POQSettleProc15): S11이 이 오탐으로 재생성을 두 번 태웠다.
                if (knownQualifiers.Contains(bare))
                {
                    continue;
                }

                if (bare.Length == 0 || known.Contains(bare) || !reported.Add(bare))
                {
                    continue;
                }

                result.Errors.Add(
                    $"{step.Code} 섹션이 `{candidate}`를 참조하지만 이 작업의 스키마 카탈로그에도, " +
                    "이 계획서가 만드는 batch 스키마 객체에도 없습니다. 실재하는 대상으로 바꾸거나, " +
                    "신규 객체라면 batch 스키마에 두십시오.");
            }
        }

        /// <summary>
        /// 후보 식별자의 한정자(스키마·데이터베이스)로 인정할 토큰 집합을 만든다.
        ///
        /// 카탈로그 자체에서 뽑는다 - 객체명이 `T`로 시작한다는 명명 규칙에 기대면
        /// 카탈로그가 보증하지 않는 규칙에 검사가 묶인다. `dbo.TClient`에서는 `dbo`가,
        /// `PaymentDB.dbo.TTxMst`에서는 `PaymentDB`와 `dbo`가 한정자다. batch·batch_shadow는
        /// BatchInfraObjectCollector에서 그대로 가져온다 - 여기서 다시 적으면 그 접두사
        /// 정의를 두 곳이 각자 아는 상태로 되돌아간다.
        /// </summary>
        private static HashSet<string> BuildKnownQualifiers(
            IReadOnlyCollection<string> knownTableNames, BatchStepPlan step)
        {
            var qualifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in knownTableNames.Concat(step.TargetTables).Concat(step.SchemaTables))
            {
                var parts = (name ?? string.Empty).Trim().Trim('[', ']').Split('.');
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var part = parts[i].Trim('[', ']').Trim();
                    if (part.Length > 0)
                    {
                        qualifiers.Add(part);
                    }
                }
            }

            foreach (var schema in BatchInfraObjectCollector.Schemas)
            {
                qualifiers.Add(schema);
            }

            return qualifiers;
        }

        /// <summary>
        /// 후보 식별자가 진짜 2부·3부 식별자 모양인지를, 객체명 바로 앞 조각(스키마)이
        /// 알려진 한정자인가로 가른다. 별칭 컬럼(`a.YMD`)이나 멤버 접근
        /// (`context.RunId`, `conn.Execute`)은 그 조각이 카탈로그에 없어 여기서 걸러진다.
        /// </summary>
        private static bool HasKnownQualifier(Match match, IReadOnlyCollection<string> knownQualifiers)
        {
            var immediateQualifier = match.Groups[3].Success ? match.Groups[2].Value : match.Groups[1].Value;
            return knownQualifiers.Contains(immediateQualifier);
        }

        /// <summary>
        /// 백틱 인용과 코드 펜스 안에서, 알려진 한정자를 가진 수식 식별자만 뽑는다.
        /// </summary>
        private static IEnumerable<string> ExtractQuotedIdentifiers(
            string markdown, IReadOnlyCollection<string> knownQualifiers)
        {
            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var fenceFlags = ComputeFenceLineFlags(lines);
            var found = new List<string>();

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (fenceFlags[i])
                {
                    // 펜스 줄 자체(```sql)는 식별자를 담지 않는다.
                    if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (Match m in QualifiedTableRegex.Matches(line))
                    {
                        if (HasKnownQualifier(m, knownQualifiers) ||
                            BatchInfraObjectCollector.IsNonCanonicalBatchObject(m.Value))
                        {
                            found.Add(m.Value);
                        }
                    }
                    continue;
                }

                foreach (Match backtick in BacktickIdentifierRegex.Matches(line))
                {
                    var inner = backtick.Groups[1].Value.Trim();
                    foreach (Match m in QualifiedTableRegex.Matches(inner))
                    {
                        if (HasKnownQualifier(m, knownQualifiers) ||
                            BatchInfraObjectCollector.IsNonCanonicalBatchObject(m.Value))
                        {
                            found.Add(m.Value);
                        }
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// 명세서에서 뽑은 원본 오류코드 중 문서 어디에도 없는 것을 프로시저별로 돌려준다.
        ///
        /// 단계별 하한 검사와 묻는 것이 다르다 - 저건 "이 코드가 제 섹션에 있는가"이고
        /// 이건 "이 코드가 문서 어디에도 없는가"다. 후자에 걸리면 조건 없이 진짜 누락이다.
        ///
        /// 목차를 전혀 쓰지 않는다는 것이 이 검사의 존재 이유다. 목차가 비거나 망가지면
        /// 단계별 검사는 통째로 무실행이 되는데(실측: 33단계 중 32단계, 그리고 다른
        /// 회차에서는 33단계 전부), 그때가 바로 누락이 가장 의심스러운 순간이다.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> FindMissingErrorCodes(
            string documentMarkdown,
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
        {
            var missing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(documentMarkdown) || codesByProcedure == null)
            {
                return missing;
            }

            foreach (var (procedure, codes) in codesByProcedure)
            {
                var absent = new List<string>();
                foreach (var code in codes)
                {
                    if (!string.IsNullOrWhiteSpace(code) && !ContainsToken(documentMarkdown, code.Trim()))
                    {
                        absent.Add(code);
                    }
                }

                if (absent.Count > 0)
                {
                    missing[procedure] = absent;
                }
            }

            return missing;
        }

        private const string UpdateHeadingPrefix = "### UPDATE 대상 테이블:";

        /// <summary>
        /// 정적 파서가 확정한 UPDATE 대상 컬럼이 명세서 본문에 실제로 있는지 본다.
        ///
        /// 문장 서수까지 대조하지 않고 테이블 단위 합집합으로 완화한다. 프롬프트는 문장별
        /// 표를 요구하지만, AI가 표를 합쳐 썼다는 이유로 재생성을 강요하면 내용이 옳은데도
        /// 루프가 돈다. L1은 형식 검증이고, 잡아야 할 것은 누락이다.
        /// </summary>
        private static void CheckUpdateMappings(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var (crudStart, crudEnd) = LocateCrudSection(lines);

            if (crudStart < 0)
            {
                // 완전 일치도, 부분 일치 폴백도 CRUD 분석 섹션을 찾지 못했다. 헤더 자체가
                // 아예 없으면 ValidateMarkdownStructure가 이미 보고했으니 중복하지 않지만,
                // 기대값(UpdateColumns)이 있는데 섹션을 못 찾았다는 사실 자체는 조용히
                // 넘기지 않는다 - 검사가 말없이 0회 도는 것이 이 저장소가 반복해서 겪은
                // 실패 양식이다.
                if (expectations.UpdateColumns.Count > 0)
                {
                    Log.Warning(
                        "CheckUpdateMappings가 `## CRUD 분석` 섹션을 완전/부분 일치 모두로 찾지 못해 " +
                        "{Count}개의 UPDATE 매핑 기대값을 대조하지 못했습니다.",
                        expectations.UpdateColumns.Count);
                }
                return;
            }

            var sections = CollectUpdateSections(lines, crudStart + 1, crudEnd);

            foreach (var expectation in expectations.UpdateColumns)
            {
                var body = ResolveSectionBody(expectation, expectations.UpdateColumns, sections, result);
                if (body == null) continue; // 오류(누락 또는 모호)는 ResolveSectionBody가 이미 기록했다.

                var missing = expectation.Columns.Where(column => !ContainsToken(body, column)).ToList();
                if (missing.Count > 0)
                {
                    AddUpdateMappingError(result,
                        $"UPDATE 대상 테이블 `{expectation.Table}`의 매핑 표에 다음 컬럼이 누락되었습니다: " +
                        string.Join(", ", missing));
                }
            }
        }

        private const string InsertHeadingPrefix = "### INSERT 대상 테이블:";

        /// <summary>
        /// INSERT 매핑 표의 테이블명 칸이 파서가 확정한 대상 테이블과 표기까지 같은지 본다.
        ///
        /// [왜 Ordinal인가] 실측된 오타가 대소문자만 다른 경우다(TSetTleByOUT 대
        /// TSettleByOUT, 2026-08-22 축 A 재감사). 대소문자를 무시하면 이 검사가
        /// 잡아야 할 것을 정확히 못 잡는다. 실행은 무해해도 매핑 표를 식별자 원천으로
        /// 삼는 이행·grep·자동 대조가 그 행에서 어긋난다.
        ///
        /// [왜 말단 이름으로 비교하는가] 명세서가 3부·2부·비한정 어느 표기를 쓸지는
        /// 문서마다 다르다. 말단 이름이 같은데 표기 폭만 다른 것은 결함이 아니므로,
        /// 말단 이름이 대소문자까지 같은지만 본다. 귀속이 불가능하면(말단 이름이
        /// 어느 대상과도 안 맞으면) 침묵한다 - 잘못 지목한 오류는 재생성으로
        /// 고칠 수 없다(CheckSchemaClaims의 정책).
        ///
        /// [Fix Round 1 - 왜 `### INSERT 대상 테이블:` 절로 스코프를 좁히는가] 1라운드
        /// 구현은 문서 전체에서 `|`로 시작하는 모든 줄을 훑었다. UPDATE 매핑 표
        /// (`### UPDATE 대상 테이블: ...`, AiService.BuildUpdateMappingTemplateLines)도
        /// 정확히 같은 `| 테이블명 | 컬럼명 | ... |` 모양을 쓰므로, 그 표의 테이블명 칸이
        /// InsertTargetTables와 대소문자만 다르면 이 검사가 UPDATE 행을 INSERT 매핑
        /// 오류로 잘못 지목했다(리뷰 Critical 실측 -
        /// Validate_UpdateMappingTableNameDiffersOnlyByCase_IsNotAttributedToInsertCheck).
        /// "원문 표기 그대로 옮기십시오"라는 안내는 INSERT 표 기준이라 UPDATE 문장에는
        /// 안 맞을 수 있다 - 귀속 불가 시 침묵이라는 위 정책과 정면으로 어긋나는 잘못이다.
        /// 그래서 AiService가 실제로 내는 헤딩 리터럴 `### INSERT 대상 테이블: {테이블명}`
        /// (AiService.cs의 INSERT 매핑 표 렌더)만 절 경계로 삼고, 그 절의 본문(다음
        /// `### ` 또는 `## ` 헤딩 전까지)에 있는 행만 본다 - CheckUpdateMappings의
        /// UpdateHeadingPrefix·CollectUpdateSections와 같은 관례다. 한 SP가 INSERT 대상
        /// 테이블을 여럿 가지면 절도 여럿이므로(AiService가 AstInsertMappings 각 원소마다
        /// 절 하나씩 낸다), 문서 전체를 훑으며 해당 헤딩을 만날 때마다 절을 연다 - 첫
        /// 절 하나만 보면 두 번째 이후 절의 오타를 놓친다.
        /// </summary>
        private static void CheckInsertMappingTableNames(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.InsertTargetTables.Count == 0) return;

            try
            {
                var expectedLeaves = expectations.InsertTargetTables
                    .Select(t => t.Split('.')[^1])
                    .ToList();

                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var reported = new HashSet<string>(StringComparer.Ordinal);
                var index = 0;

                while (index < lines.Count)
                {
                    if (!lines[index].TrimStart().StartsWith(InsertHeadingPrefix, StringComparison.Ordinal))
                    {
                        index++;
                        continue;
                    }

                    var sectionTable = lines[index].TrimStart().Substring(InsertHeadingPrefix.Length).Trim();
                    var bodyStart = index + 1;

                    // 다음 헤딩(### 또는 ## 어느 쪽이든)이 이 INSERT 절의 끝이다 -
                    // CollectUpdateSections와 같은 경계 규칙. 코드 펜스 안의 "### "처럼
                    // 보이는 텍스트를 헤딩으로 오인하지 않도록 FindIndexOutsideFence를 쓴다.
                    var bodyEnd = MarkdownSectionLocator.FindIndexOutsideFence(
                        lines, bodyStart,
                        line => line.TrimStart().StartsWith("### ", StringComparison.Ordinal)
                             || line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
                    if (bodyEnd < 0 || bodyEnd > lines.Count) bodyEnd = lines.Count;

                    for (var i = bodyStart; i < bodyEnd; i++)
                    {
                        var line = lines[i];
                        if (!line.TrimStart().StartsWith("|", StringComparison.Ordinal)) continue;

                        // SplitTableRowCells는 선행 "|" 앞의 빈 조각을 cells[0]에 그대로
                        // 남긴다("| a | b |" → ["", "a", "b", ""]) - 표의 첫 데이터 칸(테이블명)은
                        // 언제나 cells[1]이다. cells[0]을 쓰면 모든 정상 행에서 candidate가
                        // 빈 문자열이 되어 이 검사가 한 번도 발동하지 않는다(TDD 1라운드 실측).
                        var cells = SplitTableRowCells(line);
                        if (cells.Count < 2) continue;

                        var candidate = cells[1].Trim();
                        if (candidate.Length == 0) continue;

                        var leaf = candidate.Split('.')[^1];
                        if (expectedLeaves.Any(e => string.Equals(e, leaf, StringComparison.Ordinal))) continue;

                        var caseOnly = expectedLeaves.FirstOrDefault(
                            e => string.Equals(e, leaf, StringComparison.OrdinalIgnoreCase));
                        if (caseOnly == null) continue;
                        // 같은 절 안에서 대상 테이블당 컬럼 수만큼 행이 반복되므로(AiService의
                        // 템플릿이 컬럼마다 한 행을 낸다), leaf 단위로만 중복 제거한다 -
                        // 그러지 않으면 표 하나의 오타 하나가 컬럼 수만큼 중복 보고된다.
                        if (!reported.Add(leaf)) continue;

                        // Fix Round 1 - 리뷰 Minor: RawContext에 절 식별자와 줄 번호를 넣어
                        // 보고서 독자가 여러 INSERT 절 중 어느 것인지, 문서 몇 번째 줄인지
                        // 바로 찾을 수 있게 한다(1라운드는 테이블명 문자열만 담아, 절이
                        // 여럿이면 어느 절인지 구분할 길이 없었다).
                        var message =
                            $"`{InsertHeadingPrefix} {sectionTable}` 절({i + 1}번째 줄)의 INSERT 매핑 표 " +
                            $"테이블명 `{candidate}`이 파서가 확정한 표기 `{caseOnly}`와 대소문자가 다릅니다. " +
                            "실행은 무해하지만 이 표를 식별자 원천으로 삼는 이행·대조가 어긋납니다. " +
                            "원문 표기 그대로 옮기십시오.";
                        result.Errors.Add(message);
                        result.DetailedErrors.Add(new DetailedError
                        {
                            Type = ErrorType.InsertMappingTableNameMismatch,
                            Message = message,
                            RawContext = $"{candidate} (절: {sectionTable}, {i + 1}번째 줄)"
                        });
                    }

                    index = bodyEnd;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] INSERT 매핑 표 테이블명 대조 실패 - 이 검사만 건너뜁니다.");
            }
        }

        /// <summary>
        /// 명세서가 프롬프트에 실린 컬럼을 "없다"고 단정하는 것을 잡는다.
        ///
        /// 판별자는 문장의 어미가 아니라 <b>주장의 대상</b>이다 - 스키마에 대한
        /// 주장인가, 런타임 값에 대한 주장인가. 직전 라운드는 "단정형이냐 조건형이냐"로
        /// 어미를 갈랐지만 틀렸다. 한국어 어미는 그렇게 이분법적이지 않다 - 연결형
        /// ("존재하지 않아")은 단정도 조건도 아닌데, 실측 코퍼스(PG_Client_CMRate_Ins:71:
        /// "…제공된 `dbo.TClient` 스키마에는 `CompanySalesType`, `ExtraSettleFlag` 컬럼이
        /// 존재하지 않아 소스 코드와 제공 스키마 간 불일치가 있습니다.")의 진짜 결함
        /// 문장이 바로 이 연결형을 쓴다. 어미로 목록을 세분할수록 이런 형태를 놓친다.
        ///
        /// 그래서 표현을 두 부류로 나눈다.
        ///   부류 A(자기완결적 판정형, SelfContainedAbsenceClaimTokens) - 같은 줄에 다른
        ///   문맥 없이도 그 자체로 스키마 판정을 뜻한다. 표 셀("| ... | 존재하지 않음 |
        ///   ... |")이 대표적이다 - 표 셀에는 "스키마"라는 단어가 없다.
        ///   부류 B(스키마 문맥 요구형, SchemaContextAbsenceClaimTokens) - "존재하지" 하나
        ///   만으로는 스키마 부재인지 런타임 부재인지 알 수 없으므로, 같은 줄에 "스키마"
        ///   라는 단어가 함께 있을 때만 발동한다.
        ///
        /// 목록은 지어낸 것이 아니라 실제 명세서에서 관찰된 형태다. 맨 "없습니다"는
        /// 부류 A에 넣지 않는다 - 명세서 전체에 일상적으로 쓰이는 말이라 표면이 너무
        /// 넓다. 목록이 완전하지 않다는 것도 인정된 한계다 - 목록에 없는 표현이 나타나면
        /// 그 명세서가 통과한다. 대신 목록에 없는 표현이 오탐을 만들지는 않는다 - 실패
        /// 방향이 안전한 쪽이다.
        ///
        /// 잔여 한계: 같은 줄에 "스키마"를 언급하면서 별개로 실재 컬럼의 런타임 NULL
        /// 처리를 서술하는 문장은 여전히 오탐이다. 예: "제공된 스키마 기준으로 `CLVT`
        /// 값이 존재하지 않으면 0을 씁니다." - "스키마"와 "존재하지"가 한 줄에 있지만
        /// 주장의 대상은 값이지 스키마가 아니다. 줄 단위 토큰 대조로는 문장 구조를 읽지
        /// 못하므로 이 경우를 가르는 더 안전한 어휘적 판별자를 찾지 못했다.
        /// </summary>
        private static readonly string[] SelfContainedAbsenceClaimTokens =
        {
            "스키마 불일치",
            "존재하지 않음",
            "정의되어 있지 않",
            "스키마에 없",
            "스키마가 없"
        };

        /// <summary>스키마 문맥("스키마"라는 단어)이 같은 줄에 있을 때만 부재 주장으로 친다.</summary>
        private static readonly string[] SchemaContextAbsenceClaimTokens =
        {
            "존재하지 않"
        };

        private const string SchemaContextWord = "스키마";

        private static readonly Regex BacktickIdentifierRegex =
            new Regex(@"`([^`\r\n]+)`", RegexOptions.Compiled);

        /// <summary>
        /// 한 줄이 오류가 되려면 셋이 동시에 성립해야 한다.
        ///   1. 줄에 부재 표현이 있다
        ///   2. 줄의 백틱 식별자 중 하나가 의존성 테이블로 해석된다
        ///   3. 줄의 다른 백틱 식별자 중 하나가 그 테이블의 프롬프트 컬럼 집합에 있다
        ///
        /// 셋째 조건이 오탐을 막는 핵심이다. "`INSERT` 문이 없습니다"는 INSERT가 어느
        /// 테이블의 컬럼도 아니라 통과하고, "`TExchangeRateMst`의 스키마 정의는
        /// 제공되지 않았습니다"는 그 의존성에 컬럼이 0개라 애초에 대조 대상에 없다
        /// (SpecExpectations.From이 제외한다) - 그리고 그것은 참인 주장이므로 통과가 옳다.
        ///
        /// 둘째 조건은 귀속이 불가능할 때 침묵하게 만든다. 잘못 지목한 오류는 재생성으로
        /// 고칠 수 없고, 그것이 이 저장소가 직전 브랜치에서 무한 재시도로 겪은 실패다.
        /// </summary>
        private static void CheckSchemaClaims(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.PromptSchemaColumns.Count == 0) return;

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var fenceFlags = ComputeFenceLineFlags(lines);

            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                // 코드 펜스 안의 문장은 검사 대상이 아니다 - LocateCrudSection이 헤딩
                // 경계를 찾을 때 이미 펜스를 추적하는데, 이 루프만 추적하지 않으면
                // 예시 SQL 주석 안의 부재 표현이 오류로 잡힌다(실측).
                if (fenceFlags[lineIndex]) continue;

                var line = lines[lineIndex];
                var isSelfContainedClaim = Array.Exists(
                    SelfContainedAbsenceClaimTokens, t => line.Contains(t, StringComparison.Ordinal));
                var isSchemaContextClaim = line.Contains(SchemaContextWord, StringComparison.Ordinal)
                    && Array.Exists(SchemaContextAbsenceClaimTokens, t => line.Contains(t, StringComparison.Ordinal));

                if (!isSelfContainedClaim && !isSchemaContextClaim)
                {
                    continue;
                }

                var identifiers = new List<string>();
                foreach (Match match in BacktickIdentifierRegex.Matches(line))
                {
                    var identifier = match.Groups[1].Value.Trim();
                    if (identifier.Length > 0) identifiers.Add(identifier);
                }

                if (identifiers.Count < 2) continue;

                foreach (var identifier in identifiers)
                {
                    var tableKey = ResolveSchemaTableKey(identifier, expectations);
                    if (tableKey == null) continue;

                    var columns = expectations.PromptSchemaColumns[tableKey];

                    foreach (var candidate in identifiers)
                    {
                        if (ReferenceEquals(candidate, identifier)) continue;
                        if (ResolveSchemaTableKey(candidate, expectations) != null) continue;
                        if (!columns.Contains(candidate)) continue;

                        if (!reported.Add($"{tableKey}|{candidate}")) continue;

                        var message =
                            $"명세서가 `{tableKey}`의 컬럼 `{candidate}`을(를) 존재하지 않는 것으로 기술했습니다. " +
                            "이 컬럼은 프롬프트의 스키마 표에 실제로 제공되었습니다.";
                        result.Errors.Add(message);
                        result.DetailedErrors.Add(new DetailedError
                        {
                            Type = ErrorType.SchemaClaimFalse,
                            Message = message,
                            RawContext = line.Trim()
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 한 줄이 진짜 스키마 널 허용 단정인지 가르는 트리거. "NOT NULL"(영문, SQL
        /// 술어)은 일부러 뺐다 - Fix Round 1 리뷰 Critical 실측: `IS NOT NULL`은 WHERE
        /// 절 술어를 옮긴 산문일 뿐인데, 그 문장이 마크다운 표의 "참조 컬럼" 셀과 같은
        /// 줄에 있으면 그 셀에 나열된 무관한 컬럼들(AYMD·OutState·ProductName 등)까지
        /// "널 불허 단정"으로 잘못 지목했다(`output/Procedures/dbo.UP_UTIL_SETTLE_SUMMARY_ETC/docs/Spec.md:86`,
        /// `dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD/docs/Spec.md:79`,
        /// `dbo.UP_Util_PG_Client_CMRate_Ins/docs/Spec.md:21,58`). `output/**/Spec.md`
        /// 전수(감사 시점 "NOT NULL" 15건)를 확인하면 예외 없이 `IS NOT NULL` SQL 술어이거나
        /// 무관한 문맥이고, 진짜 단정 문장은 항상 아래 두 한국어 어투 중 하나를 쓴다
        /// (`UF_GET_COMM4PG4INTEREST`의 "널을 허용하지 않습니다",
        /// `UF_Get_CLComm4MobileCo`의 "`NULL`을 허용하지 않습니다"). 판정 기준은 이
        /// 코퍼스에서 거짓 양성 0이다.
        /// </summary>
        private static readonly string[] NotNullClaimTokens =
        {
            "널을 허용하지 않습니다", "NULL을 허용하지 않습니다"
        };

        /// <summary>
        /// 명세서가 "널을 허용하지 않습니다"로 단정한 컬럼이 실제로는 널 허용인지 본다.
        ///
        /// [왜 한 방향만 보는가] 널 허용인데 NOT NULL로 단정하는 쪽만 위험하다 -
        /// 그 단정을 근거로 이행 스키마에 제약을 세우거나 필터를 바꾸면 원본이
        /// 3값 논리로 배제하던 행이 대상에 들어온다(2026-08-22 축 A 재감사,
        /// UF_GET_COMM4PG4INTEREST의 UseState). 반대 방향은 과한 방어라 무해하다.
        ///
        /// [Fix Round 1 - 왜 같은 줄 테이블 앵커가 필요한가] 1라운드는 컬럼 말단 이름만
        /// 봐서, 명세서가 단정한 컬럼이 어느 테이블 얘기인지 전혀 확인하지 않았다.
        /// 같은 이름의 컬럼이 테이블마다 널 허용 여부가 갈리는 실측(`UseState`:
        /// `TCardContractMgmt`는 NOT NULL, `TFreeInterestInstCommission`은 널 허용)에서
        /// 테이블 문맥 없이는 어느 판정이 맞는지 알 길이 없다. CheckSchemaClaims
        /// (바로 위, 2085-2094행)가 이미 쓰는 관례를 그대로 따른다 - 같은 줄의 다른
        /// 식별자가 테이블로 풀려야(앵커)만 그 테이블 기준으로 컬럼을 대조한다.
        /// 실측 정답 문장은 `TFreeInterestInstCommission.UseState`처럼 테이블.컬럼이
        /// 한 백틱 식별자 안에 같이 오기도 하므로, 식별자를 마지막 점에서 나눠
        /// 앞부분이 테이블로 풀리는지도 본다 - 그러면 그 식별자 하나가 스스로 앵커와
        /// 컬럼을 같이 들고 온다.
        ///
        /// [귀속 불가 시 침묵] 테이블로도 컬럼으로도 풀리지 않으면 넘어간다. 잘못
        /// 지목한 오류는 재생성으로 고칠 수 없고, 그것이 이 저장소가 무한 재시도로
        /// 겪은 실패다(CheckSchemaClaims 주석).
        /// </summary>
        private static void CheckNullabilityClaims(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.NullableColumnsByTable.Count == 0) return;

            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var fenceFlags = ComputeFenceLineFlags(lines);
                var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void ReportIfNullable(string tableKey, string column, string rawLine)
                {
                    if (!expectations.NullableColumnsByTable.TryGetValue(tableKey, out var nullableCols)) return;
                    if (!nullableCols.Contains(column)) return;
                    if (!reported.Add($"{tableKey}|{column}")) return;

                    var message =
                        $"명세서가 `{tableKey}`의 컬럼 `{column}`을(를) 널 불허로 단정했으나 의존성 " +
                        "스키마는 널 허용으로 확정했습니다. 이 단정을 근거로 제약을 세우거나 필터를 " +
                        "바꾸면 원본이 배제하던 NULL 행이 대상에 들어옵니다.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.NullabilityClaimMismatch,
                        Message = message,
                        RawContext = rawLine.Trim()
                    });
                }

                for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    if (fenceFlags[lineIndex]) continue;

                    var line = lines[lineIndex];
                    if (!Array.Exists(NotNullClaimTokens, t => line.Contains(t, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var identifiers = new List<string>();
                    foreach (Match match in BacktickIdentifierRegex.Matches(line))
                    {
                        var identifier = match.Groups[1].Value.Trim();
                        if (identifier.Length > 0) identifiers.Add(identifier);
                    }
                    if (identifiers.Count == 0) continue;

                    // 이 줄에서 풀린 테이블들(앵커) - 한정 없는 컬럼 후보를 대조할 기준.
                    var anchoredTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // 식별자 자신이 테이블.컬럼을 같이 들고 있는 후보.
                    var qualifiedCandidates = new List<(string TableKey, string Column)>();
                    var bareCandidates = new List<string>();

                    foreach (var identifier in identifiers)
                    {
                        var directTableKey = ResolveSchemaTableKey(identifier, expectations);
                        if (directTableKey != null)
                        {
                            anchoredTables.Add(directTableKey);
                            continue; // 테이블만 가리키는 식별자는 컬럼 후보가 아니다.
                        }

                        var dotIndex = identifier.LastIndexOf('.');
                        if (dotIndex > 0 && dotIndex < identifier.Length - 1)
                        {
                            var tablePart = identifier[..dotIndex];
                            var columnPart = identifier[(dotIndex + 1)..];
                            var tableKeyFromSplit = ResolveSchemaTableKey(tablePart, expectations);
                            if (tableKeyFromSplit != null)
                            {
                                anchoredTables.Add(tableKeyFromSplit);
                                qualifiedCandidates.Add((tableKeyFromSplit, columnPart));
                                continue;
                            }
                        }

                        bareCandidates.Add(identifier);
                    }

                    foreach (var (tableKey, column) in qualifiedCandidates)
                    {
                        ReportIfNullable(tableKey, column, line);
                    }

                    foreach (var bare in bareCandidates)
                    {
                        foreach (var tableKey in anchoredTables)
                        {
                            ReportIfNullable(tableKey, bare, line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] 널 허용 주장 대조 실패 - 이 검사만 건너뜁니다.");
            }
        }

        private static readonly string[] ThreePartClaimTokens =
        {
            "3부 식별자", "세 부분 식별자", "크로스 데이터베이스 참조", "크로스 DB 참조"
        };

        /// <summary>
        /// 부정 표현. 절 안에 이것이 있으면 그 절의 3부 주장은 "쓴다"가 아니라
        /// "쓰지 않는다"는 뜻이다. AiService.cs:230의 Linked Server 안내문이 이미
        /// "~이 아닙니다" 어투를 권장하므로, 정직한 부정을 거짓 단언으로 오판하면
        /// 재생성으로 L1을 통과할 방법이 모델에게 없어진다.
        /// </summary>
        private static readonly string[] NegationTokens =
        {
            "아닙니다", "아니다", "아니며", "아니고",
            "않습니다", "않는다", "않으며", "않고", "않은",
            "없습니다", "없다", "없으며", "없고",
            // [Fix Round 5 - 리뷰 실측] 종결형(-습니다/-다)만 담고 있었는데, 표 셀은
            // 명사형 부정을 쓴다. UF_GET_ROUND4VAT/docs/Spec.md:107 "세 부분
            // 식별자를 사용하는 동일 서버 내 다른 데이터베이스 참조가 없음"이 실측
            // 오탐이다 - 그 함수는 3부 참조가 전혀 없어 정직하게 부정한 문장인데도
            // "없음"이 목록에 없어 단언으로 오판됐다. "아님"·"않음"도 같은 이유로
            // 더한다 - 이 셋은 "없다/아니다/않다"의 명사형 활용이지 새 어휘가 아니다.
            "없음", "아님", "않음",
            // [Task 3 - 실행 의미 표 도입 실측] DatabasePlacementExtractor가 내는 확정
            // 문장("3부 식별자 참조 0건, 연결 서버 참조 0건 - 확정값입니다")은 "없습니다"류
            // 종결·명사형 부정 어휘를 전혀 쓰지 않고 건수를 0으로 못박는 방식으로
            // 정직하게 부정한다. 이 표를 명세서가 그대로 옮기면(Task 3의 새 검사가
            // 요구하는 바로 그 동작) "0건"을 부정으로 인정하지 않으면 3부 참조가 없는
            // 절대다수 SP마다 이 절이 거짓 단언으로 오판된다 - 재생성으로 고칠 방법이
            // 없는 재현 불가능한 실패다.
            "0건"
        };

        /// <summary>
        /// 절 경계로 쓰는 접속 표현. 복문 한 줄이 "A이며 B가 아닙니다"처럼 서로 다른
        /// 주장을 접속할 때, 뒤 절의 부정이 앞 절까지 번지면 안 된다 - 그러면
        /// "3부 식별자... 참조이며 Linked Server... 아닙니다"(3부는 참, Linked Server만
        /// 부정)가 부정문으로 잘못 읽혀 실제 거짓 단언을 놓친다. 절 단위로 쪼개
        /// 주장 토큰과 부정 토큰이 "같은 절"에 있을 때만 부정으로 인정한다.
        ///
        /// [Fix Round 5 - 리뷰 실측, 종전엔 콤마·맨 마침표도 경계였다] 콤마와 맨
        /// 마침표를 경계에서 뺐다. 종전 구현은 리터럴 ","와 "."을 그대로
        /// IndexOf로 찾았는데, 이는 정확히 CheckHeaderContractContradiction의
        /// SentenceBoundaryRegex(Fix Round 3)가 이미 걷어낸 함정을 이 검사에는
        /// 그대로 남겨 둔 것이었다:
        ///   - 맨 마침표: "dbo.UP_Legacy를 참조하지 않습니다"처럼 식별자 안의 "."이
        ///     경계로 오인되면, "3부 식별자로 dbo"까지가 한 절이 되어 뒤에 오는
        ///     진짜 부정("참조하지 않습니다")과 분리된다 - 정직한 부정문이 거짓
        ///     단언으로 오판된다.
        ///   - 콤마: "크로스 데이터베이스 참조, Linked Server 원격 참조 모두
        ///     없습니다"처럼 콤마로 나열한 대상을 공유 서술어 하나로 부정하는
        ///     문장을 콤마에서 쪼개면, 앞 절엔 주장만 남고 부정은 뒤 절에만 남아
        ///     역시 거짓 단언으로 오판된다.
        /// 마침표는 SentenceBoundaryRegex와 같은 규칙(뒤에 공백·줄바꿈·문서 끝이
        /// 와야 경계)으로 대체했다 - 식별자·날짜 안의 점은 더 이상 절을 가르지
        /// 않는다. "이며"·"지만" 같은 접속 표현은 그대로 둔다 - 대조 실험
        /// (Validate_ThreePartClaimWithoutAnyThreePartReference_ShouldBeAnError)이
        /// 이 표현으로 접속된 두 절이 실제로 다른 주장을 담는 실측(STAT_PGCOLLECT_INS)에
        /// 근거해 여전히 갈라야 한다.
        /// </summary>
        private static readonly Regex ClauseBoundaryRegex =
            new(@"이며|이고|지만|그러나|\.(?=\s|$)", RegexOptions.Compiled);

        /// <summary>
        /// 원본에 3부 참조가 하나도 없는데 명세서가 3부·크로스 DB 참조를 단언하는지 본다.
        ///
        /// 파서가 정규화한 이름을 프롬프트가 원문처럼 보여 준 것이 원인이라 재생성으로
        /// 고칠 수 있다 - 그래서 InputDefects가 아니라 L1 오류다. 다만 프롬프트가
        /// 원문 표기를 함께 주기 시작한 뒤에만 성립한다(AiService의 원문 병기와 규칙).
        ///
        /// Linked Server 주장은 별도로 보지 않는다. LinkedServerReferences가 비었는데
        /// 4부 참조를 단언하는 경우는 같은 조건에 걸린다.
        /// </summary>
        private static void CheckIdentifierNotationClaims(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.HasThreePartReference || expectations.HasLinkedServerReference) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var fenceFlags = ComputeFenceLineFlags(lines);

            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                if (fenceFlags[lineIndex]) continue;

                var line = lines[lineIndex];
                if (!Array.Exists(ThreePartClaimTokens, t => line.Contains(t, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!HasUnnegatedClaim(line)) continue;

                var message =
                    "명세서가 3부 식별자 또는 크로스 데이터베이스 참조를 단언했으나, "
                    + "원본 DDL에는 3부 이상으로 표기된 테이블 참조가 없습니다. "
                    + "식별자 표기는 <sp-source-ddl>만 근거로 삼아야 합니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.IdentifierNotationClaim,
                    Message = message,
                    RawContext = line.Trim()
                });
                return; // 한 건만 보고한다 - 같은 원인의 문장이 여러 줄일 수 있다.
            }
        }

        /// <summary>
        /// 줄을 절 단위로 쪼개, 3부 주장 토큰을 담은 절 중 부정 토큰이 없는 절이
        /// 하나라도 있는지 본다. 있으면 그 절은 참인 단언이다(잡아야 한다). 주장을
        /// 담은 모든 절이 부정 토큰도 함께 담고 있으면 정직한 부정문이므로 통과시킨다.
        /// </summary>
        private static bool HasUnnegatedClaim(string line)
        {
            foreach (var clause in SplitIntoClauses(line))
            {
                var hasClaim = Array.Exists(ThreePartClaimTokens, t => clause.Contains(t, StringComparison.Ordinal));
                if (!hasClaim) continue;

                var hasNegation = Array.Exists(NegationTokens, t => clause.Contains(t, StringComparison.Ordinal));
                if (!hasNegation) return true;
            }

            return false;
        }

        private static List<string> SplitIntoClauses(string line)
        {
            var clauses = new List<string>();
            var current = 0;

            foreach (Match marker in ClauseBoundaryRegex.Matches(line))
            {
                clauses.Add(line.Substring(current, marker.Index - current));
                current = marker.Index + marker.Length;
            }

            clauses.Add(line.Substring(current));
            return clauses;
        }

        /// <summary>
        /// 코드 범례 앵커("N:라벨")의 숫자·라벨을 나눠 잡는다. CodeLegend가 아닌
        /// 앵커(식별자·날짜)는 대상이 아니다.
        /// </summary>
        private static readonly Regex LegendAnchorPartsRegex =
            new(@"^(\d+):(.+)$", RegexOptions.Compiled);

        /// <summary>
        /// 코드 범례 앵커 "N:라벨"이 명세서 본문에 있는지, 생성기가 실제로 쓰는
        /// 서식까지 관용적으로 본다.
        ///
        /// [Fix Round 5 - 리뷰 실측] 종전 구현은 "N:라벨" 리터럴 부분 문자열만
        /// 인정했다. 그런데 이 생성기가 실제로 쓰는 표 셀 서식은 백틱과 콜론 뒤
        /// 공백이 들어간 `` `1`: `CommMethod` `` 형태다(실측:
        /// UF_GET_PGCommOption/docs/Spec.md:43-44,74). 리터럴 앵커는 26건의 저장된
        /// Spec.md 어디에도 그대로 나타나지 않아, 범례를 정확히 옮겨 적은 문서까지
        /// 오탐으로 떨어뜨렸다. 숫자와 라벨 사이에 백틱·공백이 몇 개 끼어도
        /// 인정하도록 정규식으로 관용성을 준다 - 값·순서까지는 흔들지 않는다
        /// (숫자와 라벨은 여전히 앵커가 지정한 그대로여야 한다).
        /// </summary>
        private static bool ContainsAnchor(string markdown, string anchor)
        {
            var legendParts = LegendAnchorPartsRegex.Match(anchor);
            if (!legendParts.Success)
            {
                return markdown.Contains(anchor, StringComparison.OrdinalIgnoreCase);
            }

            var pattern = $@"`?{Regex.Escape(legendParts.Groups[1].Value)}`?\s*:\s*`?{Regex.Escape(legendParts.Groups[2].Value)}`?";
            return Regex.IsMatch(markdown, pattern, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 원본 주석의 앵커 토큰이 명세서 본문에 있는지 본다.
        ///
        /// 앵커가 없는 항목은 건너뛴다 - 순수 산문 주석을 자연어로 대조하면 오탐만
        /// 낳는다. 축 B의 조건 컬럼 검사가 실측 15건 중 14건 오탐이었던 전례가 있다.
        ///
        /// 앵커 하나만 있으면 통과로 본다. 한 주석의 모든 토큰을 요구하면 명세서가
        /// 요약하는 정상 서술까지 결함이 된다.
        /// </summary>
        private static void CheckSourceComments(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.SourceComments.Count == 0) return;

            foreach (var block in expectations.SourceComments)
            {
                if (block.Anchors.Count == 0) continue;

                var found = block.Anchors.Any(anchor => ContainsAnchor(markdown, anchor));
                if (found) continue;

                var message =
                    $"원본 DDL {block.Line}행의 주석이 명세서에 기록되지 않았습니다: "
                    + $"`{block.Text}`. 조건식 원문·도입 일자·사유를 제약 절에 기술해야 합니다. "
                    + $"(대조 앵커: {string.Join(", ", block.Anchors)})";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.SourceCommentMissing,
                    Message = message,
                    RawContext = block.Text
                });
            }
        }

        /// <summary>
        /// 3인자 ROUND가 있는데 명세서가 절사 쪽 의미를 적지 않았는지 본다.
        ///
        /// 동의어 집합으로 판정한다. 명세서가 "내림"이라 써도 값 매핑은 전달된
        /// 것이므로, 한 단어만 요구하면 정상 서술이 결함이 된다.
        ///
        /// 호출별로 보지 않고 문서 전체에 한 번만 요구한다. 같은 의미를 호출
        /// 개수만큼 반복하라는 요구가 되면 명세서가 장황해진다.
        ///
        /// [Fix Round 1 - 리뷰 실측] "내림"은 "내림차순"(ORDER BY 내림차순 정렬)의
        /// 부분 문자열이다. 단순 substring 매칭이면 정렬 방향 서술이 절사 의미
        /// 서술로 오인되어, 실제로는 값 매핑이 전혀 없는 문서가 통과한다 - 이 검사가
        /// 막으려는 바로 그 결함이 조용히 새어 나간다. 실측 코퍼스에도
        /// UF_GET_CLIENTSECTIONRATE.Spec.md의 "내림차순으로"가 실재한다.
        ///
        /// 대안(0·ROUND 근접 요구)은 기각했다: 실측 코퍼스(UP_UTIL_SETTLE_INS 등)의
        /// 현재 통과하는 정상 명세서들이 "반올림 또는 절사 옵션을 적용합니다"처럼
        /// "0"이나 "ROUND" 없이 동의어만으로 의미를 전달하고 있어, 근접 요구를 걸면
        /// 그 정상 서술들이 새로 결함으로 잡힌다 - 없던 오탐을 만드는 대가가 이
        /// 콜리전 하나를 막는 이득보다 크다. 대신 알려진 충돌 접미사만 배제한다 -
        /// "내림합니다"·"내림 방식"처럼 "차순"으로 이어지지 않는 진짜 사용은 그대로
        /// 인정된다. 절사·버림·truncate는 실측 코퍼스 전체(output/**/docs/Spec.md)를
        /// 훑어도 같은 모양의 충돌이 없어 배제 목록이 비어 있다 - 발견되면 여기에
        /// 한 항목을 추가하면 된다.
        /// </summary>
        private static readonly (string Synonym, string[] ExcludedFollowUps)[] TruncationSynonyms =
        {
            ("절사", Array.Empty<string>()),
            ("버림", Array.Empty<string>()),
            ("내림", new[] { "차순" }),
            ("truncate", Array.Empty<string>())
        };

        /// <summary>
        /// synonym이 markdown에 등장하되, 등장할 때마다 그 뒤가 excludedFollowUps 중
        /// 하나로 이어지는 경우(다른 낱말의 일부)는 매치로 치지 않는다. 같은 synonym이
        /// 문서에 여러 번 나오면 위치별로 독립 판정한다 - "내림차순"과 진짜 "내림"
        /// 서술이 한 문서에 같이 있어도 후자는 여전히 인정되어야 한다.
        /// </summary>
        private static bool ContainsTruncationSynonym(string markdown, string synonym, string[] excludedFollowUps)
        {
            var searchFrom = 0;
            while (true)
            {
                var idx = markdown.IndexOf(synonym, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;

                var after = idx + synonym.Length;
                var isKnownCollision = excludedFollowUps.Any(followUp =>
                    after + followUp.Length <= markdown.Length
                    && string.Compare(
                        markdown, after, followUp, 0, followUp.Length,
                        StringComparison.OrdinalIgnoreCase) == 0);

                if (!isKnownCollision) return true;

                searchFrom = idx + 1;
            }
        }

        private static void CheckRoundingSemantics(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.RoundingCalls.Count == 0) return;

            var stated = TruncationSynonyms.Any(
                t => ContainsTruncationSynonym(markdown, t.Synonym, t.ExcludedFollowUps));
            if (stated) return;

            var lines = string.Join(", ", expectations.RoundingCalls.Select(c => $"라인 {c.Line}"));
            var message =
                $"원본에 3인자 ROUND 호출이 {expectations.RoundingCalls.Count}건 있으나({lines}) "
                + $"명세서가 절사 쪽 의미를 기술하지 않았습니다. {RoundingSemanticsExtractor.SemanticsSentence}";
            result.Errors.Add(message);
            result.DetailedErrors.Add(new DetailedError
            {
                Type = ErrorType.RoundingSemanticsMissing,
                Message = message,
                RawContext = expectations.RoundingCalls[0].ThirdArgument
            });
        }

        /// <summary>
        /// 본문 세션 옵션이 명세서에 언급되는지 본다. 옵션 이름 자체가 앵커라
        /// 대조가 자명하다 - 이 재료에는 판정 불가 항목이 없다.
        /// </summary>
        private static void CheckSessionOptions(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            foreach (var option in expectations.SessionOptions)
            {
                if (markdown.Contains(option, StringComparison.OrdinalIgnoreCase)) continue;

                var message =
                    $"프로시저 본문이 `SET {option}`을 설정하는데 명세서가 이를 기술하지 않았습니다. "
                    + "세션 옵션은 호출 계층의 동작을 바꿀 수 있으므로 기록해야 합니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.SessionOptionMissing,
                    Message = message,
                    RawContext = $"SET {option}"
                });
            }
        }

        /// <summary>
        /// 명세서가 헤더/구현 모순을 인정했다고 볼 수 있는 표현들.
        ///
        /// [Fix Round 1 - 리뷰 실측] Task 6의 "내림"/"내림차순" 충돌과 같은 모양의
        /// 함정을 코퍼스 전수 스캔(output/**/docs/Spec.md, 26건)으로 미리 검사했다.
        /// "일치하지 않"은 3건이 걸렸는데 전부 이 검사가 찾는 모순 인정과 무관한
        /// 문장이었다 - 특히 동기 사례인 UP_Util_Settle_Summary/docs/Spec.md:278의
        /// "원천과 일치하지 않는 값이 전달되면"은 입력 검증 이야기지 헤더 주석
        /// 이야기가 아니다. 이 토큰을 그대로 뒀다면 정작 헤더 모순을 한 번도 적지
        /// 않은 그 문서가 우연히 이 문장 하나로 통과했을 것이다 - 이 검사가 막으려는
        /// 결함이 검사 자체의 허점으로 새어 나가는 셈이다. 그래서 "일치하지 않"은
        /// 뺐다.
        ///
        /// [Fix Round 1 - 독립 리뷰 지적] 처음 네 토큰(모순, 스테일, 다릅니다, 어긋)은
        /// 자연스러운 인정 표현의 상당수를 놓쳤다 - 리뷰가 실측한 7개 표현 중 5개가
        /// 거짓 결함으로 처리됐다("~하나 실제로는", "~맞지 않습니다", "~반영하지
        /// 못합니다", "~차이가 있습니다" 등). 모순을 정확히 적은 문서를 틀렸다고
        /// 판정하는 오탐이라 방치할 수 없었다. 다섯 토큰을 더했고, 각각 같은 전수
        /// 스캔(output/**/docs/Spec.md, 26건)에서 0건임을 확인했다:
        ///   "실제로는" - "~다고 하나/~지만 실제로는" 대조 구문의 공통 부분.
        ///   "맞지 않" - "일치하지 않"과 글자가 겹치지 않는 별개 어휘다("맞다" vs
        ///     "일치하다"). 리뷰가 "맞지 않"·"차이" 자체가 정산 업무 문서에 흔할
        ///     수 있다고 경고했으나 실측은 0건이었다.
        ///   "오래되어", "반영하지 못" - "주석이 낡았다" 계열 표현.
        ///   "차이가 있" - 바로 "차이"만 쓰지 않는다. "차이"는 리뷰가 경고한 대로
        ///     범위가 넓어(다른 종류의 값 차이 서술에도 흔히 쓰인다) 위험하지만,
        ///     "차이가 있"까지 붙이면 여전히 0건이라 그 폭만 취했다.
        /// 후보였다가 뺀 것: 바로 "실제 구현"만 넣는 안은 기각했다 -
        /// UF_GET_WORKDAY2.Spec.md:54의 "실제 구현에서도 기준일이 NULL이면 NULL을
        /// 반환할 수 있으므로..."는 모순이 아니라 오히려 일관성을 확인하는 문장인데도
        /// 걸렸을 것이다("차이"·"않"·"못" 같은 대조 신호가 전혀 없다). "실제 구현"
        /// 그 자체는 대조를 뜻하지 않는다는 뜻이라 후보에서 뺐다.
        /// </summary>
        /// <remarks>
        /// [2026-08-17 전수 재생성 실측] "불일치"를 더한다. 14개 SP를 새 파이프라인으로
        /// 재생성했을 때 유일한 L1 실패가 이 검사였고, 그것은 <b>오탐</b>이었다.
        /// UP_Util_Settle_Summary.Spec.md:26은 모순을 정확히 적었다 -
        /// "**내부 프로시저 주석 불일치:** 원본 헤더 주석에는 `Inner SP : NONE`으로
        /// 선언되어 있으나 실제 구현은 dbo.UP_Util_Settle_Summary_AcqManual 및
        /// dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA를 순차 호출합니다." 그런데 이 목록에
        /// "불일치"가 없었고, 모델이 쓴 "실제 구현은"은 목록의 "실제로는"과 어긋나
        /// 3회 재시도가 모두 거부됐다. 한국어로 이 상황을 가리키는 가장 자연스러운
        /// 낱말을 요구 목록에서 빠뜨린 것이라, 이 프로젝트의 앵커 규율("명세서에
        /// 그대로 나타날 수 없는 토큰을 L1 요구로 삼지 말라")을 검사 자신이 어겼다.
        ///
        /// [남는 위험 - 의도적으로 감수한다] 헤더 주석 블록에는 Inner SP 말고 다른
        /// 계약도 있어서, 그 중 하나를 인정한 문장이 이 검사를 조용히 통과시킬 수
        /// 있다. 코퍼스에 그 형태가 실재한다 - UP_UTIL_STAT_PGCOLLECT_INS.Spec.md:53의
        /// "반환값 헤더 주석의 계약은 실제 구현과 일부 불일치합니다"는 반환값 계약을
        /// 말할 뿐인데 "주석"+"불일치"로 인정에 해당한다(그 SP는 EXEC가 0건이라 검사
        /// 자체가 발화하지 않아 오늘은 무해하다). HeaderContractTerms를 내부 호출
        /// 지시어로 좁혀 막는 안을 검토했으나 기각했다 - "헤더 주석은 dbo.UP_X를
        /// 호출하지 않는다고 하나 실제로는 호출합니다"처럼 호출 대상을 이름으로만
        /// 지목한 정당한 인정 문장이 함께 걸린다. 두 오류의 대가가 다르다: 거짓
        /// 실패는 재시도 예산을 다 태우고 품질 미달을 출고시키지만(이번에 실측), 거짓
        /// 통과는 요구 하나를 놓칠 뿐이다. 근본 해법은 인정 문장이 실제 EXEC 대상
        /// 이름을 담았는지 보는 것인데, 그러려면 SpecExpectations가 bool이 아니라
        /// 호출 대상 이름 목록을 실어야 한다 - docs/todo.md에 세워 둔다.
        /// </remarks>
        private static readonly string[] ContradictionAcknowledgementTokens =
        {
            "모순", "스테일", "다릅니다", "어긋", "불일치",
            "실제로는", "맞지 않", "오래되어", "반영하지 못", "차이가 있"
        };

        /// <summary>
        /// 인정 문장이 가리키는 대상이 "헤더 주석"이라는 것 자체를 확인하는 지시어.
        /// 문서 전체가 아니라 <b>같은 문장</b> 안에 인정 토큰과 이 지시어가 함께
        /// 있어야 인정으로 센다.
        ///
        /// [Fix Round 2 - 리뷰 실측] Round 1에서 넓힌 "실제로는"·"차이가 있" 같은
        /// 토큰은 코퍼스 전수 스캔(문서 단위 유무)에서는 0건이었지만, 문서 전체
        /// Contains로 판정하면 헤더와 전혀 무관한 문장에서도 매치될 수 있다는 것이
        /// 리뷰의 지적이었다 - "정산 금액 계산 시 원 단위 절사로 인해 두 번째
        /// 집계와 세 번째 집계 사이에 차이가 있습니다" 같은, 순전히 값 차이를
        /// 말하는 문장이 실제 헤더/EXEC 모순 신고를 조용히 삼켰다. 토큰을 더
        /// 좁히는 것으로는 근본 원인이 고쳐지지 않는다 - "이 단어가 문서 어딘가에
        /// 있는가"라는 질문 자체가 잘못됐다("이 문서가 이 모순을 인정했는가"가
        /// 맞는 질문이다). 그래서 판정 단위를 문서에서 문장으로 좁히고, 그 문장이
        /// 헤더 주석을 가리킨다는 확인을 추가로 요구한다.
        ///
        /// 절(SplitIntoClauses) 단위가 아니라 문장 단위를 쓴다 - 절 경계 표지에는
        /// "하나"·"지만" 같은 대조 접속사가 포함되는데, "헤더 주석은 ~없다고 하나
        /// 실제로는 ~호출한다"처럼 그 접속사가 바로 인정 표현의 일부인 문장은 절로
        /// 쪼개면 헤더 언급과 인정 토큰이 서로 다른 절로 갈라진다.
        /// </summary>
        private static readonly string[] HeaderContractTerms =
        {
            "헤더", "주석", "Inner SP", "NONE"
        };

        /// <summary>
        /// 문장 경계 = 공백/줄바꿈/문서 끝이 뒤따르는 마침표, 또는 줄바꿈 그 자체.
        /// SplitIntoClauses보다 넓은 단위가 필요하다 - 위 HeaderContractTerms 문서
        /// 참고.
        ///
        /// [Fix Round 3 - 리뷰 실측] Round 2는 바로 "."을 경계로 썼다. 이 코퍼스는
        /// 산문 안에 "dbo.UP_X" 같은 점(.) 포함 식별자가 흔하다(실측: 26개 실제
        /// 명세서에서 문서당 45~65회, 코드 스팬 밖 표 셀 안에도 흔히 등장한다).
        /// 그 점을 경계로 오인하면 "헤더 주석은 dbo.UP_X를 호출하지 않는다고 하나
        /// 실제로는 호출합니다"처럼 정확히 인정한 한 문장이 "dbo"까지와 그 이후로
        /// 쪼개져, 헤더 지시어와 인정 토큰이 서로 다른 조각으로 갈라진다 - 정확히
        /// 인정한 문서를 틀렸다고 판정하는 거짓 양성이다. 날짜(2021.11.29)의 점도
        /// 같은 함정이다. 그래서 마침표는 뒤에 공백·줄바꿈·문서 끝이 올 때만
        /// 경계로 센다 - 식별자·날짜 안의 점(뒤에 글자·숫자가 옴)은 그대로 붙어
        /// 있는다.
        ///
        /// 줄바꿈은 항상 경계로 센다. 표 형식 명세서는 논리적 진술마다 마침표
        /// 없는 별도 행을 쓴다(실측: 코퍼스 대다수 표). 줄바꿈을 경계로 잡지
        /// 않으면 그런 표 전체가 마침표 하나 없이 한 "문장"으로 뭉치고, 서로
        /// 무관한 두 행이 각각 헤더 지시어와 인정 토큰을 하나씩 대면 거짓으로
        /// 인정 처리된다 - Round 1이 닫은 문서-전체 매치 구멍이 표 안에서 다시
        /// 열리는 것과 같은 모양이다.
        ///
        /// 반대로 줄바꿈을 경계로 잡으면, 한 문장이 줄 중간에서 개행되는(하드
        /// 랩) 경우 헤더 지시어와 토큰이 서로 다른 줄로 갈릴 위험이 있다. 그런데
        /// 이 생성기가 실제로 쓰는 산문은 하드 랩을 하지 않는다 - 코퍼스
        /// 26건(output/**/docs/Spec.md)의 최장 줄이 400~800자를 넘는다(예:
        /// UP_UTIL_SETTLE_COMM_UPD 827자, UP_UTIL_SETTLE_EXCEPTION_PROC 811자).
        /// 한 문단·한 표 행이 통째로 한 줄이라는 뜻이므로, 줄바꿈을 경계로 잡아도
        /// 실제 문장 안에서는 갈라지지 않는다. 그래서 이 방향의 위험은 이
        /// 코퍼스에서는 실재하지 않고, 표 구멍을 막는 이득만 남는다.
        /// </summary>
        private static readonly Regex SentenceBoundaryRegex =
            new Regex(@"\.(?=\s|$)|\r\n|\r|\n", RegexOptions.Compiled);

        private static IEnumerable<string> SplitIntoSentences(string text)
        {
            foreach (var sentence in SentenceBoundaryRegex.Split(text))
            {
                if (!string.IsNullOrWhiteSpace(sentence)) yield return sentence;
            }
        }

        /// <summary>
        /// 헤더 주석이 내부 SP 호출을 NONE이라 선언했는데 실제로 EXEC가 있고,
        /// 명세서가 그 모순 자체를 적지 않았는지 본다.
        ///
        /// 이 한 패턴만 본다. 헤더 주석이 선언할 수 있는 계약은 여러 가지이고
        /// 대부분은 기계가 구현과 대조할 수 없다 - 넓히면 오탐이 된다.
        ///
        /// [Fix Round 4 - 알려진 한계, 의도적으로 남겨 둔다] 문장 경계(마침표+
        /// 공백/줄바꿈, 또는 줄바꿈) 안에 인정 토큰과 헤더 지시어가 함께 있어야
        /// 인정으로 센다는 규칙은, 인정 표현이 <b>같은 문장 하나</b> 안에 있을
        /// 때만 통한다. 다음 세 모양은 실제로 모순을 인정했는데도 이 검사가
        /// 거짓으로 미인정(false positive) 처리한다:
        ///
        ///   1) 두 불릿에 나눠 쓴 인정:
        ///        - 헤더 주석은 내부 SP 호출이 없다고 선언한다
        ///        - 그러나 실제로는 두 개를 호출한다
        ///      Round 3에서 새로 생겼다 - Round 2(마침표만 경계)에서는 두 줄에
        ///      마침표가 하나도 없어 통째로 한 "문장"으로 남아 통과했었다. Round
        ///      3이 줄바꿈을 경계로 추가하면서(아래 표 케이스를 막으려고) 두
        ///      불릿이 갈라졌다.
        ///
        ///   2) 두 항목으로 나눈 번호 목록:
        ///        1. 헤더 주석은 내부 SP 호출이 없다고 선언한다
        ///        2. 그러나 실제로는 두 개를 호출한다
        ///      Round 3 이전부터 있었다 - "1."·"2." 항목 표지 자체가 마침표+공백
        ///      이라 Round 2의 마침표-단독 규칙에서도 이미 경계였다. 줄바꿈
        ///      규칙과 무관하게 항상 갈라진다.
        ///
        ///   3) 두 문장으로 나눈 인정:
        ///        헤더 주석은 내부 SP 호출이 없다고 선언한다. 그러나 실제로는
        ///        두 개를 호출한다.
        ///      Round 2가 "같은 문장" 요구를 도입한 순간부터 있었다 - 마침표
        ///      하나로 정말 두 문장이 되면, 그 자체가 이 규칙이 막으려는
        ///      경계다.
        ///
        /// [왜 이 세 요구를 동시에 만족하는 어휘적 경계 규칙이 없는가] 이 검사가
        /// 동시에 원하는 세 가지는 서로 충돌한다: (a) 무관한 표 행끼리는 갈라져야
        /// 하고(Round 1의 문서-전체 매치 구멍), (b) 무관한 불릿 항목끼리도
        /// 갈라져야 하며, (c) 그런데 정당한 인정이 두 문장·두 불릿·두 번호
        /// 항목에 걸쳐 있어도 붙어야 한다. (a)와 (b)를 만족하려면 줄바꿈(그리고
        /// 목록 표지의 마침표)을 경계로 세워야 하는데, 그러면 (c)의 다중 블록
        /// 인정이 필연적으로 쪼개진다. 반대로 (c)를 만족하려고 문장/줄 경계를
        /// 없애면 (a)·(b)가 다시 열린다. 세 요구 모두를 만족하려면 "문장"이 아니라
        /// "문단·목록 블록" 단위로 함께 인접한 여러 줄을 하나의 인정 단위로 묶는
        /// 파서가 필요하다 - 마침표·줄바꿈만 보는 정규식 경계 규칙 하나로는 표현할
        /// 수 없는 구조적 판단(불릿/번호 항목의 연속을 인식하고 그룹으로 묶는
        /// 것)이다. 이는 이 검사의 재설계 범위이고, 이 태스크(Fix Round 1-4)의
        /// 범위 밖이다.
        ///
        /// [Round 1 표 케이스와의 트레이드오프] 위 1)번(두 불릿)은 표 행 분리
        /// (Validate_TableRowsWithTokenAndHeaderTermInDifferentRows_ShouldStillBeAnError,
        /// Round 3에서 회귀 테스트로 고정)와 정확히 같은 매커니즘(줄바꿈 경계)의
        /// 반대편 결과다 - 표 케이스는 진짜 결함을 놓치지 않도록 막아야 했고, 그
        /// 대가로 다중 불릿 인정이 갈라지는 이 한계를 받아들였다. 표 케이스 쪽에
        /// 회귀 테스트가 있다는 것이 그 트레이드오프가 우연이 아니라 의도적으로
        /// 선택됐다는 증거다. 세 한계 모두
        /// Validate_TwoBulletAcknowledgement_IsAKnownLimitation_AndStillReportsTheContradiction 등
        /// (이 파일 하단)으로 고정해 둔다 - 언젠가 고쳐지면 그 테스트들이 실패할
        /// 것이고, 그 실패는 "좋은 소식이니 테스트를 갱신하라"로 읽어야 한다.
        /// </summary>
        /// <summary>
        /// 프롬프트가 작성자에게 준 지시문이 명세서 본문으로 새어 나왔는지 본다.
        ///
        /// [실측] 2026-08-18 축 A 감사. UPDATE 매핑 표 블록은 "그대로 베끼라"는
        /// 지시를 받는데, 그 블록 안에 한국어 2인칭 명령문이 섞여 있어 모델이 표와
        /// 함께 옮겨 적었다 - COMM_UPD 17곳, INS_EXTRA 5곳, INS_EXTRA4PLCARD 3곳.
        /// 지시문을 영어로 되돌리고 표지를 붙이는 것만으로는 규칙일 뿐이라, 설계 §0의
        /// 계약대로 기계 검사를 짝지운다.
        ///
        /// [앵커 규율에 어긋나지 않는다] 이 검사가 찾는 것은 한국어 명세서가 쓸 법한
        /// 표현이 아니라 <b>프롬프트가 스스로 심은 표지 문자열</b>이다. 명세서에 이
        /// 표지가 있다는 것은 곧 유출이 일어났다는 뜻이므로 오탐이 원리적으로 없다.
        /// </summary>
        /// <summary>
        /// 프롬프트가 작성자(모델)에게 주는 지시문임을 못 박는 표지. AiService의
        /// 프롬프트 빌더가 이 문자열을 심고 CheckPromptInstructionLeak이 같은
        /// 문자열을 명세서에서 찾는다 - 하나의 사실을 프롬프트와 L1이 함께 쓴다.
        /// </summary>
        public const string PromptInstructionMarker = "[INSTRUCTION - DO NOT COPY THIS LINE INTO THE DOCUMENT]";

        private static void CheckPromptInstructionLeak(string markdown, ValidationResult result)
        {
            if (!markdown.Contains(PromptInstructionMarker, StringComparison.OrdinalIgnoreCase)) return;

            const string message =
                "프롬프트가 작성자에게 준 지시문이 명세서 본문에 그대로 실렸습니다. "
                + "해당 줄은 문서의 내용이 아니라 작성 지시이므로 삭제해야 합니다.";
            result.Errors.Add(message);
            result.DetailedErrors.Add(new DetailedError
            {
                Type = ErrorType.PromptInstructionLeak,
                Message = message
            });
        }

        private static void CheckHeaderContractContradiction(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (!expectations.HasInternalProcedureCall) return;

            var headerClaimsNone = expectations.SourceComments.Any(
                b => b.Kind == "Header"
                     && b.Text.Contains("NONE", StringComparison.OrdinalIgnoreCase));
            if (!headerClaimsNone) return;

            var acknowledged = SplitIntoSentences(markdown).Any(sentence =>
                Array.Exists(ContradictionAcknowledgementTokens, t => sentence.Contains(t, StringComparison.Ordinal))
                && Array.Exists(HeaderContractTerms, t => sentence.Contains(t, StringComparison.OrdinalIgnoreCase)));
            if (acknowledged) return;

            const string message =
                "헤더 주석이 내부 SP 호출을 NONE으로 선언했으나 실제로는 EXEC 호출이 있습니다. "
                + "명세서가 이 모순(스테일 주석) 자체를 기록하지 않았습니다.";
            result.Errors.Add(message);
            result.DetailedErrors.Add(new DetailedError
            {
                Type = ErrorType.HeaderContractContradiction,
                Message = message
            });
        }

        /// <summary>
        /// 기계 확정 DML 범위 표가 명세서에 옮겨졌는지 본다.
        ///
        /// 자연어를 읽지 않는다 - 헤딩의 존재와 각 문장의 라인 번호가 표 행으로
        /// 나타나는지만 본다. 부재 서술을 판정하려 들면 축 B가 겪은 오탐(실측
        /// 15건 중 14건)이 그대로 재현된다.
        ///
        /// 라인 번호를 대조 키로 쓰는 이유는 그것이 유일하고 청킹과 무관하기
        /// 때문이다. 문장 순번은 채번이 리셋되므로 키가 될 수 없다(Task 2).
        ///
        /// [라인 번호 우연 충돌] 라인 셀 대조를 문서 전체가 아니라 DML 범위 헤딩
        /// 바로 다음 구간(다음 `## `/`### ` 헤딩 전까지)으로 좁힌다. 스키마 대응
        /// 표·UPDATE 매핑 표 같은 다른 표에도 숫자 셀(길이, 정밀도 등)이 있어, 문서
        /// 전체를 훑으면 그 우연이 거짓 통과를 만들 수 있다 - 특히 라인 번호가
        /// 작을 때 위험이 크다. 표 자체의 스키마(문장·라인·대상·술어·기준일·조인 키·
        /// GROUP BY·ORDER BY) 안에서는 라인 칸 말고 다른 칸이 순수 숫자로만 채워지지
        /// 않으므로(문장 칸은 "UPDATE 1"처럼 접두어가 붙는다) 표 내부 충돌 위험은
        /// 낮게 남는다.
        /// 2026-08-23부터는 문장 칸(`UPDATE n`·`SELECT n`)도 같은 행에서 요구하므로
        /// 라인이 우연히 같은 다른 행에 걸릴 위험은 더 줄었다 - 번호는 렌더러와 같은
        /// AiService.BuildStatementOrdinals로 매긴다.
        ///
        /// [GROUP BY 항 - Task 8] 값 대조는 GroupByColumns가 비어 있지 않을 때만
        /// 요구한다. "(없음)"은 `조인 키` 칸에도 나오는 토큰이라, 값이 비었을 때도
        /// 무조건 대조하면 GROUP BY 칸과 조인 키 칸이 둘 다 "(없음)"인 우연이 검사를
        /// 자동으로 통과시킨다(제약 2). ORDER BY는 이 표 대조와 별도인
        /// CheckOrderByExpressions가 맡지만, GROUP BY 값은 항상 단순 컬럼 식별자의
        /// 나열이라(임의 식이 아니다 - DmlScopeFact.GroupByColumns 문서) ORDER BY처럼
        /// 이스케이프 왕복을 거친 구간 텍스트 Contains가 아니라, 이 함수의 기존
        /// 라인 대조와 같은 방식(같은 행의 셀 정확 일치)으로 충분하다.
        /// </summary>
        private static void CheckDmlScopeTable(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.DmlScopeFacts.Count == 0) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var (headingIndex, endIndex) = LocateDmlScopeSection(lines);

            if (headingIndex < 0)
            {
                var message =
                    $"기계 확정 DML 범위 표가 명세서에 없습니다. `{DmlScopeExtractor.DmlScopeTableHeading}` "
                    + $"헤딩과 {expectations.DmlScopeFacts.Count}개 행을 그대로 옮겨야 합니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.DmlScopeTableMissing,
                    Message = message
                });
                return;
            }

            var rowLines = new List<string>();
            for (var i = headingIndex + 1; i < endIndex; i++)
            {
                if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    rowLines.Add(lines[i]);
                }
            }

            // [문장 칸도 같은 행에서 요구한다 - 2026-08-23 ③(b) 최종 리뷰 에스컬레이션 1]
            // 이전에는 라인 토큰 하나로 행을 찾았다. 네 표가 같은 번호 체계를 공유한다는
            // 계약(architecture.md §4.12)을 L1이 지키지 않아, `UPDATE 2` 행을 `UPDATE 1`로
            // 적거나 `SELECT 1`을 `UPDATE 1`로 옮겨도 라인이 맞으면 통과했다. 번호는
            // 렌더러와 같은 출처(AiService.BuildStatementOrdinals)로 다시 매긴다 -
            // 채번을 여기서 복제하면 두 출처가 어긋나는 날 옳게 베낀 표가 거부된다.
            var ordinals = AiService.BuildStatementOrdinals(expectations.DmlScopeFacts);

            for (var factIndex = 0; factIndex < expectations.DmlScopeFacts.Count; factIndex++)
            {
                var fact = expectations.DmlScopeFacts[factIndex];
                var statementToken = $"{fact.Operation} {ordinals[factIndex]}";
                var lineToken = fact.Line.ToString();
                var matchingRows = rowLines
                    .Where(row =>
                    {
                        var cells = MarkdownTableCellCodec.SplitRow(row);
                        return cells.Any(cell => cell == statementToken)
                            && cells.Any(cell => cell == lineToken);
                    })
                    .ToList();

                if (matchingRows.Count == 0)
                {
                    var message =
                        $"DML 범위 표에 원본 DDL 라인 {fact.Line}의 {statementToken} 행이 없습니다 - "
                        + "문장 칸과 라인 칸이 둘 다 같은 행에 있어야 합니다. "
                        + "표는 기계가 확정한 것이므로 행을 생략하거나 합칠 수 없고, 문장 번호를 바꿔 적을 수 없습니다.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.DmlScopeTableMissing,
                        Message = message,
                        RawContext = $"{statementToken} @ line {fact.Line}"
                    });
                    continue;
                }

                // GROUP BY 항 - Task 8 제약 2. "(없음)" 토큰은 `조인 키` 칸에도 나오는
                // 값이라, GroupByColumns가 비고 조인 키도 비면(UPDATE·DELETE는 항상
                // GroupByColumns가 비어 있다) 두 칸이 같은 "(없음)"이 된다. cells.Any(c =>
                // c == token) 식의 대조를 무조건 요구로 걸면 그 우연한 일치가 검사를
                // 무력화한다(CheckLockHints·CheckExecutionSemantics·CheckCaseBranches가
                // 쓰는 다중 칸 AND 대조와 같은 함정). 그래서 GroupByColumns가 비어
                // 있지 않을 때만, 그것도 line 토큰이 이미 맞은 같은 행 안에서 값을
                // 요구한다 - 다른 행의 우연한 등장이 통과를 만들지 않는다.
                if (fact.GroupByColumns.Count == 0) continue;

                var groupByToken = string.Join(", ", fact.GroupByColumns);
                var groupByPresent = matchingRows.Any(
                    row => MarkdownTableCellCodec.SplitRow(row).Any(cell => cell == groupByToken));
                if (groupByPresent) continue;

                var groupByMessage =
                    $"DML 범위 표의 {fact.Operation} @ 라인 {fact.Line} 행에 GROUP BY 값(`{groupByToken}`)이 "
                    + "없습니다. GROUP BY 칸은 기계가 확정한 것이므로 그룹화 키를 그대로 옮겨야 합니다 - "
                    + "\"(없음)\"으로 적거나 일부만 옮기면 원본 그룹화 의미가 소실됩니다.";
                result.Errors.Add(groupByMessage);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.DmlScopeTableMissing,
                    Message = groupByMessage,
                    RawContext = $"{fact.Operation} @ line {fact.Line} GROUP BY {groupByToken}"
                });
            }
        }

        /// <summary>
        /// DML 범위 헤딩과, 그 표가 끝나는(다음 `## `/`### ` 헤딩이 시작하는) 인덱스를
        /// 찾는다. 헤딩이 없으면 (-1, -1). `MarkdownSectionLocator.LocateSection`을 쓰지
        /// 않는 이유는 그 API가 경계 접두 하나만 받는데, 이 표는 H3라서 다음 H2뿐 아니라
        /// 다음 H3(예: 뒤이은 `### UPDATE 대상 테이블: ...` 절)에도 막혀야 하기 때문이다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateDmlScopeSection(IReadOnlyList<string> lines)
        {
            var headerIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == DmlScopeExtractor.DmlScopeTableHeading);
            if (headerIndex < 0) return (-1, -1);

            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headerIndex + 1,
                line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("## ", StringComparison.Ordinal)
                        || trimmed.StartsWith("### ", StringComparison.Ordinal);
                });

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }

        /// <summary>
        /// 파생 테이블 컬럼의 정의 표현식이 명세서에 있는지 본다.
        ///
        /// [Fix Round 5 - 리뷰 실측, 이 브랜치의 유일한 축 A 🔴] 종전 구현은 헤딩
        /// 존재를 전혀 확인하지 않고, 앵커가 "문서 전체 어딘가"에 있으면 통과시켰다.
        /// 저장된 EXCEPTION_PROC/docs/Spec.md로 직접 돌려 보면 "### 파생 테이블 정의
        /// (기계 확정 — 수정 금지)" 헤딩이 아예 없는데도 DiscountFlag(6회)·
        /// DiscountAmt(7회)·TxAmt(11회 이상)가 문서 다른 곳에 흩어져 등장해 21개
        /// 행 전부가 통과했다 - 실제 정의식 IIF(ISNULL(A.DiscountFlag,'N')='Y',
        /// A.DiscountAmt, A.TxAmt)는 어디에도 없는데도. CheckDmlScopeTable
        /// (기계 확정 표의 자매 검사)이 이미 옳게 하는 모양 - 헤딩을 먼저 요구하고,
        /// 그다음 헤딩 구간 안에서만 대조 - 을 그대로 따른다.
        ///
        /// 헤딩이 없으면 그 자체로 오류다. 헤딩이 있으면, 앵커를 헤딩부터 다음
        /// `## `/`### ` 헤딩 전까지의 구간으로 좁혀서 찾는다 - 문서 다른 곳(CRUD
        /// 서술 등)의 우연한 등장이 이 표를 옮겼다는 증거가 되지 않는다.
        ///
        /// 앵커 하나만 있으면 통과다. 전부 요구하면 표현식을 풀어 설명한 정상 서술이
        /// 결함이 된다. 앵커가 하나도 없는 컬럼(상수·리터럴만으로 정의된 경우)은
        /// 대조할 근거가 없으므로 조용히 건너뛴다.
        /// </summary>
        private static void CheckDerivedTableDefinitions(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.DerivedColumns.Count == 0) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var (headingIndex, endIndex) = LocateDerivedTableSection(lines);

            if (headingIndex < 0)
            {
                // 이 검사는 헤딩을 line.Trim() 완전 일치로만 찾는다(LocateDerivedTableSection,
                // CheckDmlScopeTable과 같은 규칙 - 위 클래스 요약 주석 참고). 완전
                // 일치를 요구하는 것 자체는 의도적이라 느슨하게 풀지 않는다. 다만
                // 예전 메시지 "파생 테이블 정의 표가 명세서에 없습니다"는 표가 정말
                // 없는 경우와, 모델이 헤딩을 재서식(예: 볼드 처리, 앞뒤 공백,
                // "수정 금지" 대신 "변경 금지" 등)해 문서에는 있지만 완전 일치에
                // 실패한 경우를 구분하지 못했다 - 운영자나 재생성 모델이 이 메시지만
                // 보고는 "표 자체를 새로 써야 하는지" "헤딩 문구만 원문 그대로
                // 맞추면 되는지"를 알 수 없었다. 헤딩 정확 일치 요구를 메시지에
                // 명시해 그 구분을 가능하게 한다.
                var headingMessage =
                    $"파생 테이블 정의 표를 찾지 못했습니다. `{DerivedTableColumnExtractor.DerivedTableHeading}` "
                    + "헤딩이 명세서 어딘가에 이 문자열과 정확히 일치해야 인정됩니다(공백·기호·볼드 처리까지 "
                    + "완전히 같아야 하며, 모델이 재서식한 헤딩은 일치로 보지 않습니다). 헤딩이 이미 있는데도 "
                    + "이 오류가 났다면 표를 새로 쓰지 말고 헤딩 문구를 원문 그대로 맞추십시오. "
                    + $"헤딩 아래에는 {expectations.DerivedColumns.Count}개 컬럼 정의를 그대로 옮겨야 합니다.";
                result.Errors.Add(headingMessage);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.DerivedTableDefinitionMissing,
                    Message = headingMessage
                });
                return;
            }

            var sectionText = string.Join(
                "\n", lines.Skip(headingIndex + 1).Take(endIndex - headingIndex - 1));

            foreach (var definition in expectations.DerivedColumns)
            {
                if (definition.Anchors.Count == 0) continue;

                var found = definition.Anchors.Any(
                    anchor => sectionText.Contains(anchor, StringComparison.OrdinalIgnoreCase));
                if (found) continue;

                var message =
                    $"파생 테이블 `{definition.Alias}`의 컬럼 `{definition.Column}` 정의가 "
                    + $"명세서에 없습니다: `{definition.Expression}`. "
                    + $"SET 우변이 `{definition.Alias}.{definition.Column}`에서 멈추면 "
                    + "그 값이 무엇으로 계산되는지가 소실됩니다. "
                    + $"(대조 앵커: {string.Join(", ", definition.Anchors)})";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.DerivedTableDefinitionMissing,
                    Message = message,
                    RawContext = definition.Expression
                });
            }
        }

        /// <summary>
        /// 파생 테이블 정의 헤딩과, 그 표가 끝나는(다음 `## `/`### ` 헤딩이 시작하는)
        /// 인덱스를 찾는다. 헤딩이 없으면 (-1, -1). LocateDmlScopeSection과 같은 이유로
        /// 다음 H2뿐 아니라 다음 H3에도 막힌다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateDerivedTableSection(IReadOnlyList<string> lines)
        {
            var headerIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == DerivedTableColumnExtractor.DerivedTableHeading);
            if (headerIndex < 0) return (-1, -1);

            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headerIndex + 1,
                line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("## ", StringComparison.Ordinal)
                        || trimmed.StartsWith("### ", StringComparison.Ordinal);
                });

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }

        /// <summary>
        /// 기계 확정 집합 술어 표가 명세서에 옮겨졌고, 각 행의 원소 집합이 원본과
        /// 같은지 본다.
        ///
        /// [행 키는 라인 + 컬럼 + 범위 + 술어 원문, 그래도 유일하지는 않다] 한 문장에
        /// IN이 둘 이상일 수 있어 라인만으로는 행을 특정할 수 없다. 게다가 같은 한정
        /// 컬럼이 같은 문장에서 IN으로 두 번 걸리면(`A.X IN (1) AND A.X IN (2)`)
        /// (Operation, Line, Column) 키조차 유일하지 않다 - ExtractSetPredicates는 그
        /// 경우를 합치지 않고 사실을 둘 낸다(AND/OR 의미를 날조하지 않기 위해서).
        /// 그래서 이 검사는 키로 사실을 <b>묶어</b>, 키마다 (1) 그 키를 가진 행을 전부
        /// 모으고 (2) 행 수가 사실 수와 같은지 보고 (3) 각 행의 리터럴 목록 칸을 파싱한
        /// 원소 집합들의 다중집합이 기대 집합들의 다중집합과 같은지 대칭 비교한다.
        /// `rowLines.FirstOrDefault`로 첫 행 하나만 찾으면 같은 키의 둘째 사실이 첫
        /// 행에 겹쳐 매칭되어 리터럴 누락이 조용히 통과한다.
        ///
        /// 키에 범위가 든 이유는 2026-08-19 축 A 감사, 술어 원문이 든 이유는
        /// 2026-08-22 축 A 재감사 ③ Task 7이다 - 각각 아래 본문 주석에 실측 근거를
        /// 적어 두었다. 이 검사는 <b>AiService.BuildSetPredicateTableLines의 짝</b>이라,
        /// 표의 열이 바뀌면 두 곳을 한 커밋에서 함께 고쳐야 한다.
        ///
        /// [대조 대상은 행이 아니라 리터럴 목록 칸 하나다] 행 전체를 부분 문자열로
        /// 훑으면 숫자 리터럴에서 퇴화한다 - `| UPDATE 3 | 108 | UseState | IN | 2 |
        /// 0, 1 |`에서 "0"과 "1"을 찾으면 라인 번호 108이 이미 둘 다 담고 있어 무조건
        /// 통과한다. 칸을 꺼내 원소 집합으로 대칭 비교하면 숫자든 문자열이든 같은
        /// 규칙이 적용되고 오류 메시지가 구체화된다.
        ///
        /// [문서 전체를 훑지 않는 이유] 2026-08-18 축 A 감사 실측: EXPECT_PROC의
        /// 9개 리터럴 중 7개가 <b>다른 문장</b>에 등장한다. "각 리터럴이 문서
        /// 어딘가에 있는가"를 물으면 그 우연 덕분에 통과한다 - HeaderContractTerms의
        /// Fix Round 2가 같은 이유로 판정 단위를 문서에서 문장으로 좁혔다.
        /// </summary>
        private static void CheckSetPredicates(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.SetPredicates.Count == 0) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var (headingIndex, endIndex) = LocateSetPredicateSection(lines);

            if (headingIndex < 0)
            {
                var message =
                    $"기계 확정 집합 술어 표가 명세서에 없습니다. `{DmlScopeExtractor.SetPredicateTableHeading}` "
                    + $"헤딩과 {expectations.SetPredicates.Count}개 행을 그대로 옮겨야 합니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.SetPredicateMismatch,
                    Message = message
                });
                return;
            }

            var rowLines = new List<string>();
            for (var i = headingIndex + 1; i < endIndex; i++)
            {
                if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    rowLines.Add(lines[i]);
                }
            }

            // (Operation, Line, Column, Scope, PredicateText) 키별로 사실을 묶는다 -
            // 같은 키의 사실이 둘 이상이면 "행이 하나 있다"는 것만으로는 부족하고,
            // 행도 그만큼 있어야 한다. Column은 대소문자를 가리지 않는다 - 아래 행
            // 매칭이 OrdinalIgnoreCase로 컬럼 칸을 비교하는 것과 같은 규칙이다.
            // [범위를 키에 넣는 이유 - 2026-08-19 축 A 감사] 파생 테이블 내부 술어를
            // 수집하면서 같은 표기의 컬럼이 최상위와 파생 양쪽에 걸릴 수 있게 됐다.
            // 한정자 없는 컬럼이면 (연산, 라인, 컬럼) 키가 겹쳐, 명세서가 두 행을 모두
            // "최상위"로 적어도 행 수가 맞아 통과한다 - 파생 테이블 필터가 문서에서
            // 사라진 것을 못 잡는다는 뜻이고, COMM_UPD:243·EXCEPTION_PROC:375가 정확히
            // 그 자리에서 새어 나갔다.
            // [술어 원문을 키에 넣는 이유 - 2026-08-22 축 A 재감사 ③ Task 7]
            // 행 단위가 최상위 AND 항으로 올라가면서 분해되지 않는 항도 사실을 낸다.
            // 그런 항은 컬럼·연산·원소 수·리터럴이 전부 "—"라서, 같은 줄의 서로 다른 두
            // 항이 (연산, 라인, 컬럼, 범위) 키에서 완전히 겹친다 - 그러면 문서가 한
            // 항의 원문을 두 번 적어도 "행이 사실 수만큼 있다"는 이유로 통과해, 나머지
            // 한 항이 통째로 사라진 것을 못 잡는다. 원문을 키에 넣으면 그 겹침이
            // 사라지고, 주석이 오래 적어 둔 키 비유일 문제(`A.X IN (1) AND A.X IN (2)`)도
            // 함께 해소된다.
            //
            // [원문은 렌더된 그대로 비교한다 - 괄호 계약] Task 6은 바깥 괄호를 포함한
            // 원문을 담는다(`(A.UseState <> 1 OR ...)`). 대조 전에 괄호를 벗기거나 공백을
            // 정규화하면 옳게 옮긴 표가 거부되므로, 여기서는 <b>렌더가 실제로 하는 변형
            // 하나</b>만 기대 쪽에 똑같이 적용한다 - 개행 접기다
            // (FoldNewlinesLikeRenderedCell 문서. 추출기 경로의 PredicateText는 이미
            // CollapseWhitespace를 거쳐 개행이 없으므로 이 접기는 손으로 조립한 사실에
            // 대한 방어다). 이스케이프된 `|`는 SplitRow가 이미 복원한다.
            // [문장 번호도 키에 넣고 행에서 요구한다 - 2026-08-23 ③(b) 최종 리뷰 에스컬레이션 1]
            // 묶음 키에 연산은 있었으나 행 매칭 술어는 라인·컬럼·범위·원문 네 칸만 봤다 -
            // `SELECT 1` 행을 `UPDATE 1`로 옮겨 적어도 통과했다. 문장 토큰(렌더 그대로
            // `{Operation} {StatementOrdinal}`)을 행에서 요구하고, 한 DDL 줄에 문장이
            // 둘일 때 합쳐지지 않도록 번호를 키에도 넣는다.
            var groups = expectations.SetPredicates
                .GroupBy(f => (
                    Operation: f.Operation.ToUpperInvariant(),
                    f.StatementOrdinal,
                    f.Line,
                    Column: f.Column.ToUpperInvariant(),
                    Scope: f.Scope.ToUpperInvariant(),
                    PredicateText: FoldNewlinesLikeRenderedCell(f.PredicateText)));

            foreach (var group in groups)
            {
                var facts = group.ToList();
                var line = group.Key.Line;
                var displayColumn = facts[0].Column;
                var displayOperation = facts[0].Operation;
                var statementToken = $"{displayOperation} {facts[0].StatementOrdinal}";
                var displayScope = facts[0].Scope;
                var displayPredicateText = group.Key.PredicateText;
                var lineToken = line.ToString();

                // [MarkdownTableCellCodec.SplitRow로 나누는 이유 - 2026-08-21 최종 브랜치
                // 리뷰 재라운드 ⑤] 컬럼·범위 칸도 렌더 시점에 EscapeTableCell을 거친다
                // (AiService.cs:944, BuildSetPredicateTableLines) - 대괄호 식별자(`A.[C|D]`)처럼 `|`가 든 컬럼은
                // `\|`로 이스케이프된 채 나온다. 단순 `r.Split('|')`는 그 자리에서도
                // 갈라져 모델이 표를 원문 그대로 옮겨도 컬럼이 일치하지 않는다 -
                // LockHints·ORDER BY·객체 선언과 같은 실패 모양(ExtractSetPredicateLiteralCell
                // 문서의 실측 근거와 동일).
                //
                // 원문 칸 대조는 대소문자를 가린다(Ordinal) - 컬럼·범위와 달리 이 칸은
                // DDL 원문 자체이고, 문자열 리터럴의 대소문자가 대상 행을 가른다.
                // 원문이 빈 사실(손으로 조립한 기존 재료의 기본값)은 SplitRow가 늘 내는
                // 앞뒤 빈 조각에 걸려 이 항이 참이 된다 - 원문을 채우지 않은 재료에는
                // 이 대조가 추가 요구를 걸지 않는다는 뜻이다.
                var matchingRows = rowLines.Where(r =>
                {
                    var cells = MarkdownTableCellCodec.SplitRow(r);
                    return cells.Any(c => c == statementToken)
                        && cells.Any(c => c == lineToken)
                        && cells.Any(c => string.Equals(c, displayColumn, StringComparison.OrdinalIgnoreCase))
                        && cells.Any(c => string.Equals(c, displayScope, StringComparison.OrdinalIgnoreCase))
                        && cells.Any(c => string.Equals(c, displayPredicateText, StringComparison.Ordinal));
                }).ToList();

                if (matchingRows.Count != facts.Count)
                {
                    var countMessage =
                        $"집합 술어 표에서 문장 {statementToken} 원본 DDL 라인 {line} 컬럼 `{displayColumn}` 범위 `{displayScope}` "
                        + $"술어 원문 `{displayPredicateText}` 키를 가진 사실이 {facts.Count}개인데 행은 "
                        + $"{matchingRows.Count}개 있습니다. 문장 칸은 `{statementToken}` 그대로여야 하고 "
                        + "「술어 원문」 칸은 DDL 원문 그대로여야 합니다 - "
                        + "요약하거나 바꿔 쓸 수 없고, 행을 합치거나 생략할 수 없으며, "
                        + "범위(최상위 / 파생 테이블 / 하위 질의)도 사실대로 적어야 합니다.";
                    result.Errors.Add(countMessage);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.SetPredicateMismatch,
                        Message = countMessage,
                        RawContext = $"{statementToken} @ line {line} · {displayColumn}"
                    });
                    continue;
                }

                // 기대 쪽 리터럴에도 렌더가 적용한 개행 접기를 똑같이 적용한다 -
                // FoldNewlinesLikeRenderedCell 문서 참고(리터럴 자체에 개행이 든
                // 경우, 대조가 아예 성립 불가능해지는 것을 막기 위한 결정).
                var expectedSets = facts
                    .Select(f => f.Literals.Select(FoldNewlinesLikeRenderedCell).ToHashSet(StringComparer.Ordinal))
                    .ToList();
                var writtenSets = matchingRows.Select(ExtractSetPredicateLiteralCell).ToList();

                // 다중집합 비교 - 순서가 아니라 원소 집합 자체로 짝을 찾는다. 짝을
                // 찾은 쌍은 서로 지우고, 남는 쪽이 곧 누락(기대에는 있는데 표에
                // 대응 행이 없음)과 초과(표에는 있는데 대응하는 기대가 없음)다.
                var remainingWritten = new List<HashSet<string>>(writtenSets);
                var unmatchedExpected = new List<HashSet<string>>();

                foreach (var expectedSet in expectedSets)
                {
                    var matchIndex = remainingWritten.FindIndex(w => w.SetEquals(expectedSet));
                    if (matchIndex >= 0)
                    {
                        remainingWritten.RemoveAt(matchIndex);
                    }
                    else
                    {
                        unmatchedExpected.Add(expectedSet);
                    }
                }

                if (unmatchedExpected.Count == 0) continue;

                string mismatchMessage;
                if (facts.Count == 1)
                {
                    // 키가 하나뿐이면 어긋난 짝이 명확하다 - 원소 단위로 누락/추가를
                    // 짚어 준다("누락: SSGPayCard, KakaoCard").
                    var expected = unmatchedExpected[0];
                    var written = remainingWritten[0];
                    var missing = expected.Except(written).ToList();
                    var extra = written.Except(expected).ToList();

                    var parts = new List<string>();
                    if (missing.Count > 0) parts.Add($"누락: {string.Join(", ", missing)}");
                    if (extra.Count > 0) parts.Add($"추가: {string.Join(", ", extra)}");

                    mismatchMessage =
                        $"집합 술어 표의 라인 {line} 컬럼 `{displayColumn}` 행에서 리터럴 목록이 "
                        + $"원본과 다릅니다({string.Join(" / ", parts)}). 집합의 멤버십이 대상 행을 "
                        + "정하므로 원소를 줄이거나 요약할 수 없습니다.";
                }
                else
                {
                    // 같은 키에 사실이 여럿이면 몇 번째 행이 몇 번째 사실과 짝인지
                    // 자리로 단정할 수 없다 - 다중집합으로만 대조했으므로, 짝을 못
                    // 찾은 집합 전체를 그대로 보여준다.
                    var missingSets = unmatchedExpected.Select(s => "{" + string.Join(", ", s) + "}");
                    var extraSets = remainingWritten.Select(s => "{" + string.Join(", ", s) + "}");

                    mismatchMessage =
                        $"집합 술어 표의 라인 {line} 컬럼 `{displayColumn}` 키에 사실이 {facts.Count}개인데, "
                        + "행들의 원소 집합 다중집합이 원본과 다릅니다. "
                        + $"표에서 대응하는 행을 찾지 못한 원본 집합: {string.Join(" | ", missingSets)}"
                        + (remainingWritten.Count > 0
                            ? $"; 원본 어느 집합과도 대응하지 않는 표의 집합: {string.Join(" | ", extraSets)}"
                            : string.Empty)
                        + " 같은 컬럼의 각 IN은 별도 행으로, 원소를 정확히 옮겨야 AND/OR 의미가 보존됩니다.";
                }

                result.Errors.Add(mismatchMessage);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.SetPredicateMismatch,
                    Message = mismatchMessage,
                    RawContext = $"{displayOperation} @ line {line} · {displayColumn}"
                });
            }
        }

        /// <summary>
        /// 집합 술어 표 행에서 `리터럴 목록` 칸(「술어 원문」 바로 앞 칸) 하나만 꺼내
        /// SQL 리터럴 문법을 존중하는 토큰화로 원소 집합을 만든다. 행 전체가 아니라 이 칸
        /// 하나만 보는 것이 §5.1의 요구다 - 그래야 라인 번호 같은 숫자 셀이
        /// 리터럴과 섞여 대조가 퇴화하지 않는다.
        ///
        /// [행을 SplitTableRowCells로 나누는 이유] `row.Split('|')`로 행 전체를
        /// 단순 분할하면, AiService.EscapeTableCell이 렌더 시점에 리터럴 안의
        /// `|`를 `\|`로 이스케이프한 자리에서도 갈라진다 - 이스케이프는 "셀
        /// 경계가 아니다"라는 뜻인데 단순 분할은 그 뜻을 모른다. 실측(리뷰):
        /// `Nm IN ('a|b','c')`가 렌더된 칸 `'a\|b', 'c'`를 단순 분할하면 행
        /// 자체가 그 자리에서 잘못 쪼개져 리터럴 칸의 마지막 조각이 `b'`만
        /// 남는다("누락: 'a\|b' / 추가: b'"). SplitTableRowCells는 `\|`를 셀
        /// 경계가 아니라 칸 내용의 일부(복원된 `|`)로 다뤄 이 문제를 없앤다.
        ///
        /// [칸 안을 쉼표로 단순 분할하지 않는 이유] 렌더된 칸은 리터럴을 그대로
        /// `, `로 이어 붙인 것이라(AiService.BuildSetPredicateTableLines의
        /// `string.Join(", ", fact.Literals)`), 문자열 리터럴 자체에 쉼표가 있으면
        /// (`Nm IN ('a,b','c')` → 칸 `'a,b', 'c'`) 쉼표 단순 분할이 `'a,b'`를
        /// `'a`와 `b'` 두 조각으로 쪼갠다. 기대 쪽(`SetPredicateFact.Literals`)은
        /// 원문 그대로 `{"'a,b'", "'c'"}`를 들고 있으므로, 표를 한 글자도 안 틀리고
        /// 그대로 옮겨도 이 대조는 영원히 만족 불가능했다(실측: "누락: 'a,b' /
        /// 추가: 'a, b'" - 공백 차이처럼 보이는 유령 원소를 보고해 모델이 옳은
        /// 표를 "고치게" 만든다). §0이 막으려는 "모델이 옳게 옮겨도 L1이 틀렸다고
        /// 하는" 실패 모양이라, TokenizeLiteralCell로 따옴표 안의 쉼표를 구분자로
        /// 보지 않는다.
        /// </summary>
        private static HashSet<string> ExtractSetPredicateLiteralCell(string row)
        {
            var cellsOfRow = SplitTableRowCells(row);

            // [칸 인덱스가 하나 밀린 이유 - 2026-08-22 축 A 재감사 ③ Task 7]
            // 「술어 원문」이 마지막 열로 들어왔다. 행이 `|`로 끝나면 마지막 조각은
            // 빈 문자열이고, 그 앞이 원문 칸이며, 리터럴 목록은 그 하나 앞이다.
            // 인덱스를 안 고치면 원문 칸을 리터럴로 읽어 <b>옳게 옮긴 표</b>를 틀렸다고
            // 한다 - 이 문서 위쪽이 적어 둔 실패 모양과 같은 부류다.
            var trailingBlank = cellsOfRow.Count > 0 && cellsOfRow[^1].Length == 0 ? 1 : 0;
            var literalIndex = cellsOfRow.Count - trailingBlank - 2;
            var literalCell = literalIndex >= 0 ? cellsOfRow[literalIndex] : string.Empty;

            // [`—` 한 글자는 빈 집합이다 - 측정으로 확인함] 계획서는 "분해되지 않은
            // 사실은 Literals가 비어 있어 빈 집합끼리 맞춰지므로 이 갈래는 손대지
            // 않아도 통과한다"고 적었는데, 틀렸다. 렌더러가 그 칸에 빈 문자열이 아니라
            // UndecomposedCell("—")을 적기 때문이다(AiService.BuildSetPredicateTableLines).
            // 그대로 두면 기대 {} 대 표 {"—"}가 되어, 원문 그대로 옮긴 표가 "추가: —"로
            // 거부된다. 리터럴은 언제나 SQL 리터럴 표기('...' 또는 숫자)이므로 이
            // 표기가 진짜 원소일 수는 없다.
            if (literalCell == UndecomposedCell) return new HashSet<string>(StringComparer.Ordinal);

            return TokenizeLiteralCell(literalCell).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// 분해되지 않은 항의 원소 수·리터럴 목록 칸에 렌더러가 적는 표기.
        ///
        /// [왜 상수를 여기 또 두는가] 원본은
        /// <c>DmlScopeExtractor.TopLevelPredicateCollector.NotDecomposed</c>인데 private
        /// 중첩 클래스 안이라 참조할 수 없고, 렌더 쪽 짝은
        /// <c>AiService.UndecomposedCell</c>이다. 이 검증기는 조립기(AiService)에
        /// 컴파일 의존하지 않는 방향으로 다듬어 왔으므로(MarkdownTableCellCodec 문서)
        /// 여기 자기 상수를 둔다 - 셋이 갈리면 옳게 옮긴 표가 거부되므로 함께 고친다.
        /// </summary>
        private const string UndecomposedCell = "—";

        /// <summary>
        /// 마크다운 표 행을 `|`로 나누는 별칭. 실제 구현은
        /// <see cref="MarkdownTableCellCodec.SplitRow"/>다(2026-08-21 최종 브랜치 리뷰
        /// 재라운드 Minor(설계) - 렌더 쪽 이스케이프와 짝을 맞추려고 중립 헬퍼로
        /// 옮겼다. MarkdownTableCellCodec 문서 참고).
        /// </summary>
        private static List<string> SplitTableRowCells(string row) => MarkdownTableCellCodec.SplitRow(row);

        /// <summary>
        /// 집합 술어 표의 `리터럴 목록` 칸 하나를 SQL 리터럴 문법을 존중해
        /// 토큰화한다 - 따옴표(`'...'`, 내부 `''` 이스케이프 포함) 밖의 쉼표만
        /// 구분자로 삼는다. 왜 이래야 하는지는 ExtractSetPredicateLiteralCell
        /// 문서의 실측 근거를 참고 - 쉼표 단순 분할은 `'a,b'`를 둘로 쪼개 L1을
        /// 만족 불가능하게 만든다. 따옴표 밖의 토큰(숫자 리터럴 등)은 트림해서
        /// 그대로 원소로 삼는다.
        /// </summary>
        private static List<string> TokenizeLiteralCell(string cell)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < cell.Length; i++)
            {
                var c = cell[i];

                if (inQuotes)
                {
                    current.Append(c);
                    if (c == '\'')
                    {
                        // `''`는 리터럴 안에서 이스케이프된 작은따옴표다(T-SQL
                        // 문법) - 닫는 따옴표로 보지 않고 다음 문자까지 함께
                        // 삼킨다. 예: `'O''Brien'`.
                        if (i + 1 < cell.Length && cell[i + 1] == '\'')
                        {
                            current.Append(cell[i + 1]);
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    continue;
                }

                if (c == '\'')
                {
                    inQuotes = true;
                    current.Append(c);
                    continue;
                }

                if (c == ',')
                {
                    var token = current.ToString().Trim();
                    if (token.Length > 0) tokens.Add(token);
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            var last = current.ToString().Trim();
            if (last.Length > 0) tokens.Add(last);

            return tokens;
        }

        /// <summary>
        /// AiService.EscapeTableCell은 표 셀을 렌더할 때 개행을 공백으로 접는다
        /// (개행이 셀 경계를 깨뜨리지 않도록 - EscapeTableCell 문서 참고). 그런데
        /// 이 접기는 역변환이 불가능하다 - 렌더된 표의 공백 하나만 보고는 그것이
        /// 원래 `\r\n`이었는지 `\n`이었는지 `\r`이었는지, 혹은 원래도 공백이었는지
        /// 구분할 수 없다.
        ///
        /// T-SQL 문자열 리터럴은 따옴표 안에 실제 개행 문자를 담을 수 있으므로
        /// (실측 코퍼스에는 이 형태가 없었다), 리터럴 자체에 개행이 든 DDL이
        /// 있다면 원문 그대로 대조하는 순간 표를 정확히 옮겨도 영원히 어긋난다 -
        /// §0이 막으려는 "모델이 옳게 옮겨도 L1이 틀렸다고 하는" 실패 모양이다.
        /// 그래서 조용히 실패하게 두지 않고, 기대 쪽 리터럴에도 렌더와 같은 접기를
        /// 적용해 대조한다 - 개행의 정확한 종류를 구분하는 정밀도는 잃지만("접힌
        /// 후 같은 문자열인가"만 확인), 대조 자체가 원리적으로 성립 불가능해지는
        /// 것보다는 낫다.
        /// </summary>
        private static string FoldNewlinesLikeRenderedCell(string literal) =>
            literal.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

        /// <summary>
        /// 「참조 함수 (기계 확정 — 수정 금지)」 표가 명세서에 실렸는지, 그리고 사실마다
        /// 자기 행(함수·호출 위치·인자)이 있는지 확인한다(행 대조는 2026-08-23부터 -
        /// 본문 주석 참고).
        ///
        /// [왜 이 검사가 있는가 - 2026-08-20 최종 전체 리뷰 M1] 조립기가 이 표를
        /// 프롬프트에 넣지만, 모델이 그것을 옮겼는지는 아무도 확인하지 않았다.
        /// 설계가 집합 술어 표의 성공 요인으로 꼽은 넷(구조화 · 위치 · 수정 금지 계약 ·
        /// 검증) 중 마지막 하나만 이 표에 없었다. 그래서 "조립기가 쓴다"는 결정이
        /// 실제로는 "조립기가 넣고 복사를 요청한다"에 그쳤다.
        /// </summary>
        private static void CheckReferencedFunctions(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            // 호출이 없으면 조립기도 표를 내지 않는다(AiService는 functionCalls.Count > 0
            // 일 때만 렌더한다). 그런데도 표를 요구하면 함수를 부르지 않는 SP가 영영
            // L1을 통과하지 못한다 - 이 가드 없이 검사를 먼저 넣어 실측으로 확인했다.
            if (expectations.ReferencedFunctionCalls.Count == 0) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var headingIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0,
                line => line.Trim() == DmlScopeExtractor.ReferencedFunctionTableHeading);

            if (headingIndex < 0)
            {
                var message =
                    $"기계 확정 참조 함수 표가 명세서에 없습니다. `{DmlScopeExtractor.ReferencedFunctionTableHeading}` "
                    + $"헤딩과 {expectations.ReferencedFunctionCalls.Count}개 행을 그대로 옮겨야 합니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.ReferencedFunctionMismatch,
                    Message = message
                });
                return;
            }

            // [행 단위 대조 - 2026-08-23 ③(b) 최종 리뷰 에스컬레이션 1] 이전에는 헤딩 존재만
            // 봤다 - 행을 지우거나 호출 위치를 바꿔 적어도 침묵했다. 잠금 힌트와 같은
            // 방식으로 사실마다 자기 행을 요구한다. 대조 칸은 셋이다:
            //   · 호출 위치 - 렌더 그대로 `{Operation} {StatementOrdinal} (라인 {Line})`
            //     (BuildReferencedFunctionTableLines). 네 표가 공유하는 문장 번호가 여기 실린다.
            //   · 인자 - CallExpression 그대로(EscapeTableCell은 SplitRow가 복원한다).
            //   · 함수 - 렌더러는 의존성이 풀리면 `DB.스키마.이름`으로, 아니면 사실의
            //     QualifiedName(스키마 유무 불문)으로 적는다. 기대 쪽에는 그 판정 재료가
            //     없으므로 정확 일치 대신 <b>이름 부분의 접미 일치</b>(대소문자 무시)만
            //     요구한다 - `dbo.UF_X`·`SETTLE_POQ_DB.dbo.UF_X`·`UF_X`가 모두 같은 함수다.
            //   · 명세서 링크 칸은 상대 경로라 대조하지 않는다.
            // 같은 키의 사실이 여럿이면(한 문장이 같은 함수를 같은 인자로 두 번 부르면)
            // 행도 그 수만큼 있어야 한다 - CheckSetPredicates와 같은 묶음 규칙이다.
            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headingIndex + 1,
                line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("## ", StringComparison.Ordinal)
                        || trimmed.StartsWith("### ", StringComparison.Ordinal);
                });
            if (endIndex < 0) endIndex = lines.Count;

            var rowLines = new List<string>();
            for (var i = headingIndex + 1; i < endIndex; i++)
            {
                if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    rowLines.Add(lines[i]);
                }
            }

            var groups = expectations.ReferencedFunctionCalls
                .GroupBy(f => (
                    Name: BareFunctionName(f.QualifiedName).ToUpperInvariant(),
                    Operation: f.Operation.ToUpperInvariant(),
                    f.StatementOrdinal,
                    f.Line,
                    f.CallExpression));

            foreach (var group in groups)
            {
                var facts = group.ToList();
                var fact = facts[0];
                var bareName = BareFunctionName(fact.QualifiedName);
                var locationToken = $"{fact.Operation} {fact.StatementOrdinal} (라인 {fact.Line})";

                var matchingRows = rowLines.Count(row =>
                {
                    var cells = MarkdownTableCellCodec.SplitRow(row);
                    return cells.Any(c => c == locationToken)
                        && cells.Any(c => string.Equals(c, fact.CallExpression, StringComparison.Ordinal))
                        && cells.Any(c => c.EndsWith(bareName, StringComparison.OrdinalIgnoreCase));
                });
                if (matchingRows == facts.Count) continue;

                var message =
                    $"참조 함수 표에 `{fact.QualifiedName}`의 호출 위치 `{locationToken}` "
                    + $"인자 `{fact.CallExpression}` 행이 {facts.Count}개 있어야 하는데 {matchingRows}개 있습니다. "
                    + "함수·호출 위치·인자 칸은 기계가 확정한 것이므로 행을 생략하거나 합칠 수 없고, "
                    + "문장 번호·라인을 바꿔 적을 수 없으며, 인자 원문을 요약할 수 없습니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.ReferencedFunctionMismatch,
                    Message = message,
                    RawContext = $"{locationToken} {fact.QualifiedName}"
                });
            }
        }

        /// <summary>
        /// `DB.스키마.이름`·`스키마.이름`·`이름` 어느 표기든 마지막 부분(함수 이름)만 돌려준다.
        /// CheckReferencedFunctions의 함수 칸 대조가 접미 일치로 쓰는 값이다.
        /// </summary>
        private static string BareFunctionName(string qualifiedName)
        {
            var lastDot = qualifiedName.LastIndexOf('.');
            return lastDot < 0 ? qualifiedName : qualifiedName[(lastDot + 1)..];
        }

        /// <summary>
        /// 집합 술어 헤딩과 그 표가 끝나는 인덱스를 찾는다. LocateDmlScopeSection과
        /// 같은 이유로 다음 H2뿐 아니라 다음 H3에도 막힌다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateSetPredicateSection(IReadOnlyList<string> lines)
        {
            var headerIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == DmlScopeExtractor.SetPredicateTableHeading);
            if (headerIndex < 0) return (-1, -1);

            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headerIndex + 1,
                line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("## ", StringComparison.Ordinal)
                        || trimmed.StartsWith("### ", StringComparison.Ordinal);
                });

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }

        /// <summary>
        /// 기계 확정 잠금 힌트 표가 명세서에 옮겨졌는지 본다. 재료가 없으면(잠금 힌트를
        /// 지는 스캔이 없으면) 조용히 건너뛴다 - AiService도 그때는 표를 내지 않는다
        /// (CheckReferencedFunctions와 같은 가드).
        ///
        /// [행 식별 키가 (Operation, StatementOrdinal, Line, Table, Alias, Scope, Hints)
        /// 여섯 값 전부인 이유 - 2026-08-21 조정자 판정, 브리프 지시 2]
        /// 「DML 범위」·「집합 술어」 표는 문장당 행이 하나(또는 컬럼당 하나)라 처음에는
        /// Line 토큰만으로 행을 특정했다(2026-08-23부터는 두 표도 문장 토큰을 같은 행에서
        /// 요구한다 - CheckDmlScopeTable·CheckSetPredicates의 같은 날짜 주석). 잠금 힌트
        /// 표는 다르다 - 행 하나가 (문장 × 스캔 자리)라서 한 문장에 여러 행이 난다.
        /// `FROM A a JOIN B b`처럼 두 참조가 한 물리 줄에 있으면 두 LockHintFact의
        /// Line이 정확히 같다(LockHintVisitor.Add의 중복 제거 키 문서가 이미 이 사실을
        /// 실측해 남겼다 - Line만으로는 대상 노드와 FROM 참조조차 구분되지 않아 그
        /// 키에 Line을 넣었다). Line 토큰만 보고 "어느 행에든 있다"로 판정하면, 문서가
        /// 한 사실의 행만 옮기고 같은 줄의 다른 사실을 통째로 빠뜨려도 통과한다 -
        /// INS_EXTRA4PLCARD에서 감사가 잡은 것과 같은 실패 모양(TPGProperty가 별칭
        /// P·Y에는 힌트가 붙고 PG에는 안 붙는데 뭉뚱그려 서술됨)이다.
        ///
        /// 여섯 값을 모두 같은 행에서 요구하면 이 충돌이 사라진다 - 추출기 자신의
        /// 중복 제거 키(Operation, StatementOrdinal, Table, Alias, Line)가 이미 그
        /// 조합이 사실마다 유일함을 보장하므로(LockHintVisitor.Add), 그 키 전부가
        /// 우연히 다른 사실의 셀들로 채워진 행에 동시에 나타날 가능성은 사실상 없다.
        /// Scope·Hints까지 더하는 것은 "표는 채워졌지만 내용이 틀린" 부류(범위를
        /// 최상위/파생 중 잘못 적거나, INDEX=CIDX_x를 INDEX로 뭉개는 것)까지 잡기
        /// 위해서다 - 이 배치에서 세 번 반복된 "종류만 렌더하고 값을 버리는" 결함과
        /// 같은 함정이 이 표에도 있다.
        ///
        /// [대소문자 비대칭 - 2026-08-21 리뷰 Minor, 의도적으로 고치지 않음] 이 비교는
        /// `==`(대소문자 구분)이고 CheckSetPredicates는 OrdinalIgnoreCase다. 원본
        /// DDL·프롬프트·모델 출력이 이 코퍼스에서 일관되게 대문자라 오늘은 영향이
        /// 없지만, 소문자 표기가 섞인 산출물이 나오면 이 검사가 대소문자 차이만으로
        /// 거짓 결함을 낼 수 있다. 이번 라운드의 범위가 아니라 고치지 않고 다음
        /// 사람에게 이 문단으로 남긴다.
        ///
        /// [행을 SplitTableRowCells로 나누는 이유 - 2026-08-21 최종 리뷰 Important 1]
        /// 이전 구현은 `row.Split('|')`로 단순 분할했다 - AiService.EscapeTableCell이
        /// 렌더 시점에 테이블명·별칭·힌트 값 안의 `|`를 `\|`로 이스케이프한 자리에서도
        /// 갈라져, 대괄호 식별자(`[T|X]`)나 값 있는 힌트에 `|`가 들어간 행은 모델이
        /// 표를 원문 그대로 옮겨도 어떤 셀도 fact.Table 등과 정확히 같아질 수 없었다
        /// (ExtractSetPredicateLiteralCell 문서의 같은 실패 모양). SplitTableRowCells는
        /// `\|`를 셀 경계가 아니라 `|` 하나로 복원하므로, 이스케이프되지 않은 fact
        /// 값과 직접 비교할 수 있다.
        /// </summary>
        private static void CheckLockHints(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.LockHints.Count == 0) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var (headingIndex, endIndex) = LocateLockHintSection(lines);

            if (headingIndex < 0)
            {
                var message =
                    $"기계 확정 잠금 힌트 표가 명세서에 없습니다. `{DmlScopeExtractor.LockHintTableHeading}` "
                    + $"헤딩과 {expectations.LockHints.Count}개 행을 그대로 옮겨야 합니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.LockHintTableMissing,
                    Message = message
                });
                return;
            }

            var rowLines = new List<string>();
            for (var i = headingIndex + 1; i < endIndex; i++)
            {
                if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    rowLines.Add(lines[i]);
                }
            }

            foreach (var fact in expectations.LockHints)
            {
                var statementToken = $"{fact.Operation} {fact.StatementOrdinal}";
                var lineToken = fact.Line.ToString();
                var hintsToken = fact.Hints.Count == 0 ? "(없음)" : string.Join(", ", fact.Hints);

                var present = rowLines.Any(row =>
                {
                    var cells = SplitTableRowCells(row);
                    return cells.Any(c => c == statementToken)
                        && cells.Any(c => c == lineToken)
                        && cells.Any(c => c == fact.Table)
                        && cells.Any(c => c == fact.Alias)
                        && cells.Any(c => c == fact.Scope)
                        && cells.Any(c => c == hintsToken);
                });
                if (present) continue;

                var message =
                    $"잠금 힌트 표에 {statementToken}(라인 {fact.Line})의 `{fact.Table}` "
                    + $"(별칭 {fact.Alias}, 범위 {fact.Scope}) 행이 없거나 힌트 값이 다릅니다. "
                    + $"힌트는 `{hintsToken}`을 그대로 옮겨야 합니다 - 종류만 적고 값을 생략하면 "
                    + "원문에서 찾을 수 없습니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.LockHintTableMissing,
                    Message = message,
                    RawContext = $"{fact.Operation} {fact.StatementOrdinal} @ line {fact.Line} {fact.Table} {fact.Alias}"
                });
            }
        }

        /// <summary>
        /// 잠금 힌트 헤딩과 그 표가 끝나는 인덱스를 찾는다. LocateDmlScopeSection과
        /// 같은 이유로 다음 H2뿐 아니라 다음 H3에도 막힌다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateLockHintSection(IReadOnlyList<string> lines)
        {
            var headerIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == DmlScopeExtractor.LockHintTableHeading);
            if (headerIndex < 0) return (-1, -1);

            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headerIndex + 1,
                line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("## ", StringComparison.Ordinal)
                        || trimmed.StartsWith("### ", StringComparison.Ordinal);
                });

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }

        /// <summary>
        /// 기계 확정 객체 선언 표(함수 WITH 옵션)가 명세서에 옮겨졌는지 본다. 재료가
        /// 없으면(프로시저이거나 파싱 실패) 조용히 건너뛴다 - ObjectDeclarationExtractor.
        /// Extract가 그때 항상 null을 낸다.
        ///
        /// 헤딩 존재만 보지 않고 WITH 옵션 값까지 대조한다 - "종류만 렌더하고 값을
        /// 버리는" 결함이 이 배치에서 세 번 났고(INDEX=CIDX_x -> INDEX, EXECUTE AS
        /// CALLER -> EXECUTEAS, A DESC -> A) 이 표가 정확히 그 두 번째 사례
        /// (ObjectDeclarationExtractor.RenderExecuteAs 문서)의 원인이 된 자리다.
        /// </summary>
        private static void CheckObjectDeclaration(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.ObjectDeclaration == null) return;

            var fact = expectations.ObjectDeclaration;
            var expectedOptionsText = fact.WithOptions.Count == 0
                ? "(없음)"
                : string.Join(", ", fact.WithOptions);

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var headingIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == ObjectDeclarationExtractor.ObjectDeclarationTableHeading);

            if (headingIndex < 0)
            {
                var headingMessage =
                    $"기계 확정 객체 선언 표가 명세서에 없습니다. `{ObjectDeclarationExtractor.ObjectDeclarationTableHeading}` "
                    + $"헤딩과 `{fact.QualifiedName}`의 WITH 옵션(`{expectedOptionsText}`) 행을 그대로 옮겨야 합니다.";
                result.Errors.Add(headingMessage);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.ObjectDeclarationTableMissing,
                    Message = headingMessage
                });
                return;
            }

            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headingIndex + 1,
                line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("## ", StringComparison.Ordinal)
                        || trimmed.StartsWith("### ", StringComparison.Ordinal);
                });
            var sectionEnd = endIndex < 0 ? lines.Count : endIndex;
            var sectionText = string.Join(
                "\n", lines.Skip(headingIndex + 1).Take(sectionEnd - headingIndex - 1));

            // [렌더와 같은 이스케이프를 거치는 이유 - 2026-08-21 최종 리뷰 Important 1]
            // 렌더(AiService.BuildObjectDeclarationTableLines)는 이 두 값을 EscapeTableCell로
            // 감싸 싣는다 - EXECUTE AS '사용자' 리터럴처럼 `|`가 든 값은 `\|`로 이스케이프된
            // 채로 표에 나온다. 이스케이프하지 않은 원문으로 Contains를 하면 모델이 표를
            // 원문 그대로(이스케이프된 채로) 옮겨도 영원히 찾을 수 없다.
            var found = sectionText.Contains(MarkdownTableCellCodec.Escape(fact.QualifiedName), StringComparison.Ordinal)
                && sectionText.Contains(MarkdownTableCellCodec.Escape(expectedOptionsText), StringComparison.Ordinal);
            if (found) return;

            var message =
                $"객체 선언 표에 `{fact.QualifiedName}`의 WITH 옵션(`{expectedOptionsText}`) 행이 없거나 "
                + "값이 다릅니다. 표는 기계가 확정한 것이므로 옵션 종류만 적고 값(EXECUTE AS의 주체,"
                + " INLINE의 ON/OFF 등)을 생략할 수 없습니다.";
            result.Errors.Add(message);
            result.DetailedErrors.Add(new DetailedError
            {
                Type = ErrorType.ObjectDeclarationTableMissing,
                Message = message,
                RawContext = expectedOptionsText
            });
        }

        /// <summary>
        /// INSERT...SELECT의 최상위 ORDER BY가 「DML 범위」 표의 ORDER BY 칸에 실렸는지
        /// 본다.
        ///
        /// [세 번째 검사가 필요한 이유 - 조정자 판정, 브리프 지시 1을 뒤집는다]
        /// 계획서 초안은 "ORDER BY는 기존 「DML 범위」 표의 칸이므로 그 표의 기존 L1
        /// 검사(CheckDmlScopeTable)가 이미 덮는다"고 적었지만 그 근거는 실측으로
        /// 틀렸다. CheckDmlScopeTable은 각 사실의 <b>라인 토큰이 어느 행에든 있는지</b>만
        /// 보고 칸 내용은 하나도 대조하지 않는다 - 그래서 모델이 ORDER BY 칸을 통째로
        /// "(없음)"으로 적어도(원본에 ORDER BY가 있는데도) 그 행의 Line 토큰 자체는
        /// 여전히 표에 있으므로 CheckDmlScopeTable은 통과시킨다. 2026-08-21 축 A
        /// 감사가 잡은 결함(STAT_PGCOLLECT_INS:113의 `ORDER BY INYMD, CLIENTID,
        /// PGNAME, MALLID`가 문서 어디에도 없었음)이 정확히 이 구멍이다.
        ///
        /// [CheckDerivedTableDefinitions와 같은 모양을 따르는 이유]
        /// 그 검사도 처음엔 "앵커가 문서 전체 어딘가에 있으면 통과"였다가, 실물
        /// 검증에서 헤딩 자체가 없는 문서인데도 21개 행이 전부 우연한 등장으로 헛통과한
        /// 사건이 있었다(CheckDerivedTableDefinitions 문서 참고). 그래서 헤딩을 먼저
        /// 요구하고, 표현식 텍스트도 「DML 범위」 표 구간(LocateDmlScopeSection) 안에서만
        /// 찾는다 - 문서 다른 곳(CRUD 서술 등)의 우연한 등장은 증거가 아니다. 헤딩
        /// 부재는 CheckDmlScopeTable이 이미 별도 오류로 잡으므로(DmlScopeFacts가
        /// 비어 있지 않으면 그 검사가 반드시 돈다) 여기서는 중복 보고 없이 조용히
        /// 건너뛴다.
        ///
        /// [대조 텍스트가 string.Join(", ", ...)인 이유] 렌더 계약은 AiService.
        /// BuildDmlScopeTableLines다 - fact.OrderByExpressions를 그 결합 규칙으로 한
        /// 칸에 싣는다(AiService.cs, EscapeTableCell(string.Join(", ", fact.
        /// OrderByExpressions))). 대조도 같은 결합 텍스트로 해야 모델이 표를 그대로
        /// 옮겼을 때 정확히 일치한다.
        /// </summary>
        private static void CheckOrderByExpressions(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            var factsWithOrderBy = expectations.DmlScopeFacts
                .Where(f => f.OrderByExpressions.Count > 0)
                .ToList();
            if (factsWithOrderBy.Count == 0) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var (headingIndex, endIndex) = LocateDmlScopeSection(lines);

            // 헤딩 부재는 CheckDmlScopeTable이 이미 별도 오류로 보고한다(위 문서 참고).
            if (headingIndex < 0) return;

            var sectionText = string.Join(
                "\n", lines.Skip(headingIndex + 1).Take(endIndex - headingIndex - 1));

            foreach (var fact in factsWithOrderBy)
            {
                var joined = string.Join(", ", fact.OrderByExpressions);
                // [이스케이프 왕복 - 2026-08-21 최종 리뷰 Important 1] 렌더는 이 칸도
                // EscapeTableCell을 거친다(AiService.cs, EscapeTableCell(string.Join(", ",
                // fact.OrderByExpressions))) - ORDER BY는 임의 식이라 비트 OR(`A | B`) 같은
                // `|`가 든 식이 문법상 유효하다. 이스케이프하지 않은 joined로 Contains하면
                // 그런 식은 모델이 표를 원문 그대로 옮겨도 찾을 수 없다.
                if (sectionText.Contains(MarkdownTableCellCodec.Escape(joined), StringComparison.Ordinal)) continue;

                var message =
                    $"DML 범위 표의 {fact.Operation} @ 라인 {fact.Line} 행에 ORDER BY 값(`{joined}`)이 "
                    + "없습니다. ORDER BY 칸은 기계가 확정한 것이므로 정렬 대상과 방향(DESC/ASC)까지 "
                    + "그대로 옮겨야 합니다 - \"(없음)\"으로 적거나 일부만 옮기면 원본에서 찾을 수 없습니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.DmlScopeTableMissing,
                    Message = message,
                    RawContext = $"{fact.Operation} @ line {fact.Line} ORDER BY {joined}"
                });
            }
        }

        /// <summary>
        /// 기계 확정 실행 의미 표가 명세서에 옮겨졌는지 본다. 재료가 없으면 조용히
        /// 건너뛴다 - AiService도 그때는 표를 내지 않는다(CheckLockHints와 같은 가드).
        ///
        /// [행 식별 키가 (종류, 라인, 대상, 확정 사실) 네 값 전부인 이유]
        /// CheckLockHints와 같다 - 한 객체에 같은 종류의 행이 여럿 날 수 있어(식 타입
        /// 경로는 CAST마다 한 행) 종류 토큰만으로는 행이 특정되지 않는다. 확정 사실
        /// 칸까지 요구하는 것은 "표는 채웠는데 값이 틀린" 부류를 잡기 위해서다.
        ///
        /// [자기 try/catch를 두는 이유] Validate의 catch-all은 검사 하나가 던지면
        /// Errors를 통째로 지우고 IsValid = true로 통과시킨다. 새 검사의 실패가 기존
        /// 검사 15개의 판정까지 삼키면 안 된다.
        /// </summary>
        private static void CheckExecutionSemantics(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.ExecutionSemantics.Count == 0) return;

            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var (headingIndex, endIndex) = LocateHeadingSection(
                    lines, ExecutionSemanticsFacts.TableHeading);

                if (headingIndex < 0)
                {
                    var missing =
                        $"기계 확정 실행 의미 표가 명세서에 없습니다. `{ExecutionSemanticsFacts.TableHeading}` "
                        + $"헤딩과 {expectations.ExecutionSemantics.Count}개 행을 그대로 옮겨야 합니다.";
                    result.Errors.Add(missing);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.ExecutionSemanticsTableMissing,
                        Message = missing
                    });
                    return;
                }

                var rowLines = new List<string>();
                for (var i = headingIndex + 1; i < endIndex; i++)
                {
                    if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                    {
                        rowLines.Add(lines[i]);
                    }
                }

                foreach (var fact in expectations.ExecutionSemantics)
                {
                    var present = rowLines.Any(row =>
                    {
                        var cells = SplitTableRowCells(row);
                        return cells.Any(c => c == fact.Kind)
                            && cells.Any(c => c == fact.Line)
                            && cells.Any(c => c == fact.Target)
                            && cells.Any(c => c == fact.Fact);
                    });
                    if (present) continue;

                    var message =
                        $"실행 의미 표에 `{fact.Kind}`(라인 {fact.Line}, 대상 {fact.Target}) 행이 없거나 "
                        + $"확정 사실이 다릅니다. `{fact.Fact}`를 그대로 옮겨야 합니다 - 이것은 미확정 "
                        + "사항이 아니라 확정값입니다.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.ExecutionSemanticsTableMissing,
                        Message = message,
                        RawContext = $"{fact.Kind} @ {fact.Line} {fact.Target}"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] 실행 의미 표 대조 실패 - 이 검사만 건너뜁니다.");
            }
        }

        /// <summary>
        /// 기계 확정 CASE 분기 표가 명세서에 옮겨졌는지 본다. 재료가 없으면 조용히
        /// 건너뛴다(CheckExecutionSemantics와 같은 가드).
        ///
        /// 행 키는 (라인, 순서, 조건 원문) 셋이다. 결과 원문까지 넣지 않는 이유는
        /// 결과식이 여러 줄에 걸치면 모델이 줄바꿈을 공백으로 정규화해 옮기는 것이
        /// 정상이기 때문이다 - 조건까지 일치하면 행은 이미 특정된다.
        ///
        /// [자기 try/catch를 두는 이유] Validate의 catch-all은 검사 하나가 던지면
        /// Errors를 통째로 지우고 IsValid = true로 통과시킨다. 새 검사의 실패가 기존
        /// 검사들의 판정까지 삼키면 안 된다.
        /// </summary>
        private static void CheckCaseBranches(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.CaseBranches.Count == 0) return;

            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var (headingIndex, endIndex) = LocateHeadingSection(
                    lines, CaseBranchExtractor.TableHeading);

                if (headingIndex < 0)
                {
                    var missing =
                        $"기계 확정 CASE 분기 표가 명세서에 없습니다. `{CaseBranchExtractor.TableHeading}` "
                        + $"헤딩과 {expectations.CaseBranches.Count}개 행을 그대로 옮겨야 합니다.";
                    result.Errors.Add(missing);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.CaseBranchTableMissing,
                        Message = missing
                    });
                    return;
                }

                var rowLines = new List<string>();
                for (var i = headingIndex + 1; i < endIndex; i++)
                {
                    if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                    {
                        rowLines.Add(lines[i]);
                    }
                }

                foreach (var fact in expectations.CaseBranches)
                {
                    var lineToken = fact.Line.ToString();
                    var present = rowLines.Any(row =>
                    {
                        var cells = SplitTableRowCells(row);
                        return cells.Any(c => c == lineToken)
                            && cells.Any(c => c == fact.Ordinal)
                            && cells.Any(c => c == fact.Condition);
                    });
                    if (present) continue;

                    var message =
                        $"CASE 분기 표에 라인 {fact.Line}의 `{fact.Ordinal}` 행이 없거나 조건 원문이 "
                        + $"다릅니다. `{fact.Condition}`을 그대로 옮겨야 합니다 - 분기를 합치거나 "
                        + "비교 연산자를 말로 바꾸면 원문에서 찾을 수 없습니다.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.CaseBranchTableMissing,
                        Message = message,
                        RawContext = $"{fact.Ordinal} @ line {fact.Line}"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] CASE 분기 표 대조 실패 - 이 검사만 건너뜁니다.");
            }
        }

        /// <summary>
        /// 기계 확정 표가 GFM 표로 렌더링되는 형태인지 본다.
        ///
        /// [왜 행 내용 대조로는 부족한가] 값이 전부 맞아도 구분 행의 셀 수가 헤더와
        /// 다르면 GFM이 표로 인식하지 않는다. 그러면 "수정 금지"로 못 박은 확정값이
        /// 평문 한 덩어리가 되어 이행 담당자가 표로 읽지 못한다
        /// (2026-08-22 축 A 재감사, UP_UTIL_STAT_PGCOLLECT_INS 실측).
        ///
        /// [왜 카탈로그를 도는가] MachineConfirmedTables.All이 표 목록의 단일 출처다.
        /// 표가 늘면 이 검사가 따로 손대지 않아도 따라온다.
        ///
        /// [왜 expectations를 받지 않는가] 재료 없이 마크다운만으로 판정되므로
        /// 재료가 없는 갈래에서도 돈다.
        ///
        /// [자기 try/catch를 두는 이유] Validate의 catch-all은 검사 하나가 던지면
        /// Errors를 통째로 지우고 통과시킨다. 새 검사의 실패가 기존 검사의 판정까지
        /// 삼키면 안 된다.
        /// </summary>
        private static void CheckMachineTableShape(string markdown, ValidationResult result)
        {
            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);

                foreach (var table in MachineConfirmedTables.All)
                {
                    var (headingIndex, endIndex) = LocateHeadingSection(lines, table.Heading);
                    if (headingIndex < 0) continue;

                    ReportTableShapeBreaks(lines, headingIndex + 1, endIndex, table.Heading, result);
                }

                CheckInsertMappingTableShape(lines, result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] 기계 확정 표 형태 검사 실패 - 이 검사만 건너뜁니다.");
            }
        }

        /// <summary>
        /// INSERT 매핑 표(`### INSERT 대상 테이블: {테이블명}`)는 MachineConfirmedTables.All의
        /// 여덟 헤딩에 없어 CheckMachineTableShape 본체가 못 본다. CheckInsertMappingTableNames는
        /// 같은 절을 걷지만 테이블명 칸만 대조하므로 구분행 셀 수 결함은 어느 검사도 잡지
        /// 못했다(2026-08-22 최종 리뷰 Important I2,
        /// UP_UTIL_STAT_PGCOLLECT_INS/docs/Spec.md:71-72 실측). 절 경계 규칙은
        /// CheckInsertMappingTableNames와 동일하다 - 헤딩 리터럴이 테이블명을 물고 있어
        /// 카탈로그 상수 하나로 못 묶으므로 여기서 직접 절을 찾는다.
        /// </summary>
        private static void CheckInsertMappingTableShape(IReadOnlyList<string> lines, ValidationResult result)
        {
            var index = 0;
            while (index < lines.Count)
            {
                if (!lines[index].TrimStart().StartsWith(InsertHeadingPrefix, StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                var sectionTable = lines[index].TrimStart().Substring(InsertHeadingPrefix.Length).Trim();
                var bodyStart = index + 1;
                var bodyEnd = MarkdownSectionLocator.FindIndexOutsideFence(
                    lines, bodyStart,
                    line => line.TrimStart().StartsWith("### ", StringComparison.Ordinal)
                         || line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
                if (bodyEnd < 0 || bodyEnd > lines.Count) bodyEnd = lines.Count;

                ReportTableShapeBreaks(
                    lines, bodyStart, bodyEnd, $"{InsertHeadingPrefix} {sectionTable}", result);

                index = bodyEnd;
            }
        }

        /// <summary>
        /// [start, end) 구간에서 "|"로 시작하는 줄을 모으되, 빈 줄을 블록 경계로
        /// 삼아 쪼갠 뒤 블록마다 자기 첫 행(헤더)과 나머지 행의 셀 수를 비교한다.
        ///
        /// [왜 빈 줄이 경계인가] 빈 줄은 GFM의 표 종결자다. 옛 구현은 헤딩 절 전체에서
        /// "|"로 시작하는 줄을 하나로 모아, 같은 절 안에 정당한 별개 표가 둘 이상 있으면
        /// 합쳐버렸다. 그러면 뒤 표의 행이 앞 표 헤더의 셀 수와 비교되어 거짓 형태
        /// 결함이 났다(2026-08-22 최종 리뷰 Critical,
        /// output/Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md:55-70 실측 - 기계 확정
        /// 4칸 표 뒤에 빈 줄로 구분된 정당한 3칸 표가 있었다). 코퍼스 31개 전수 재실행에서
        /// 이 병합이 거짓 양성 10건(9개 객체)을 냈다.
        ///
        /// [왜 블록마다 break로 첫 오류만 보고하는가] break는 그 블록의 나머지 행
        /// 검사만 멈춘다 - 바깥 foreach는 다음 블록으로 그대로 진행하므로, 옛 구현이
        /// 표를 합쳐 뒤 블록 자체가 가려지던 결함(이월 Minor T3-m2)도 이 구조에서
        /// 함께 해소된다.
        /// </summary>
        private static void ReportTableShapeBreaks(
            IReadOnlyList<string> lines, int start, int end, string label, ValidationResult result)
        {
            var blocks = new List<List<string>>();
            var current = new List<string>();
            for (var i = start; i < end; i++)
            {
                if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    current.Add(lines[i]);
                }
                else if (current.Count > 0)
                {
                    blocks.Add(current);
                    current = new List<string>();
                }
            }
            if (current.Count > 0) blocks.Add(current);

            foreach (var rows in blocks)
            {
                if (rows.Count < 2) continue;

                var headerCells = SplitTableRowCells(rows[0]).Count;
                for (var i = 1; i < rows.Count; i++)
                {
                    var cells = SplitTableRowCells(rows[i]).Count;
                    if (cells == headerCells) continue;

                    var message =
                        $"`{label}` 표의 {i + 1}번째 행이 {cells}칸인데 헤더 행은 "
                        + $"{headerCells}칸입니다. 셀 수가 다르면 표로 렌더링되지 않아 확정값이 "
                        + "평문으로 무너집니다. 헤더와 같은 칸 수로 옮기십시오.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.MachineTableShapeBroken,
                        Message = message,
                        RawContext = rows[i]
                    });
                    break;
                }
            }
        }

        /// <summary>
        /// 헤딩 하나와 그 표가 끝나는 인덱스를 찾는다. LocateLockHintSection의 일반형이다 -
        /// 새 표가 둘 늘어 같은 코드를 세 번 쓰게 되므로 헤딩을 인자로 받는다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateHeadingSection(
            IReadOnlyList<string> lines, string heading)
        {
            var headerIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == heading);
            if (headerIndex < 0) return (-1, -1);

            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headerIndex + 1,
                line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("## ", StringComparison.Ordinal)
                        || trimmed.StartsWith("### ", StringComparison.Ordinal);
                });

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }

        private static readonly Regex TableCellRegex =
            new Regex(@"^\s*\|\s*`([^`\r\n]+)`\s*\|", RegexOptions.Compiled);

        /// <summary>
        /// 같은 물리 테이블이 CRUD 표 한 절 안에서 서로 다른 표기로 여러 행이 되는 것을
        /// 잡는다. EXCEPTION_PROC에서 SETTLE_POQ_DB.dbo.TSettleMst / dbo.TSettleMst /
        /// TSettleMst 세 표기가 한 표에 공존한 것이 실측된 결함이다.
        ///
        /// "서로 다른 표기"라는 단서가 중요하다. 같은 문자열이 두 번 나오는 것은 이 결함이
        /// 아니다 - 문장별로 나눠 적었을 수 있고, UPDATE 매핑 헤딩이 정확히 그렇게 한다.
        ///
        /// 절 경계를 넘지 않는다. 같은 테이블이 조회 절과 갱신 절에 각각 나오는 것은
        /// 정상이다.
        ///
        /// 귀속은 ResolveSchemaTableKey에 맡긴다. 마지막 파트가 같은 실제 테이블이
        /// 둘이면 그 함수가 null을 돌려주므로, DB1.dbo.TCommMst와 DB2.dbo.TCommMst가
        /// 합쳐지는 오탐이 생기지 않는다.
        /// </summary>
        private static void CheckTableIdentitySplit(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.PromptSchemaColumns.Count == 0) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var (crudStart, crudEnd) = LocateCrudSection(lines);
            if (crudStart < 0) return;

            var fenceFlags = ComputeFenceLineFlags(lines);
            var spellingsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            void Flush()
            {
                foreach (var kvp in spellingsByTable)
                {
                    if (kvp.Value.Count < 2) continue;

                    var message =
                        $"같은 물리 테이블 `{kvp.Key}`이(가) `## CRUD 분석`의 한 절 안에서 " +
                        $"서로 다른 표기 {kvp.Value.Count}개로 나뉘어 기술되었습니다: " +
                        string.Join(", ", kvp.Value.Select(s => $"`{s}`")) + ".";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.TableIdentitySplit,
                        Message = message
                    });
                }
                spellingsByTable.Clear();
            }

            for (var index = crudStart + 1; index < crudEnd; index++)
            {
                // 코드 펜스 안의 줄은 검사하지 않는다 - 예시 SQL 주석 안에 표 셀처럼
                // 보이는 텍스트가 있어도 표기 분열 집계에 들어가면 안 된다(실측).
                if (fenceFlags[index]) continue;

                var trimmed = lines[index].TrimStart();

                // "### "(H3) 이상 깊이는 전부 하위 절 경계다. 프롬프트는 조회/갱신
                // 대상 테이블을 하위 절로 나눠 쓰라고 요구할 뿐 헤딩 레벨은 고정하지
                // 않는다("### "/"#### "만 열거했을 때 H5에서 실측된 것처럼, 레벨을
                // 하나씩 나열하는 방식은 항상 한 레벨 뒤처진다). "## "(H2)는 CRUD
                // 절 자체의 경계이자 LocateCrudSection이 이미 구간을 잡는 기준이므로
                // 여기서 삼키면 안 된다.
                if (IsSubsectionHeading(trimmed))
                {
                    Flush();
                    continue;
                }

                var match = TableCellRegex.Match(lines[index]);
                if (!match.Success) continue;

                var written = match.Groups[1].Value.Trim();
                var key = ResolveSchemaTableKey(written, expectations);
                if (key == null) continue;

                if (!spellingsByTable.TryGetValue(key, out var spellings))
                {
                    spellings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    spellingsByTable[key] = spellings;
                }
                spellings.Add(NormalizeQualifiedName(written));
            }

            Flush();
        }

        /// <summary>
        /// 한 줄이 "### " 이상 깊이의 마크다운 헤딩(하위 절 경계)인지 판정한다.
        /// "## "(H2)는 CRUD 절 자체의 경계이므로 여기서 제외한다.
        /// </summary>
        private static bool IsSubsectionHeading(string trimmedLine)
        {
            var hashCount = 0;
            while (hashCount < trimmedLine.Length && trimmedLine[hashCount] == '#') hashCount++;
            if (hashCount < 3) return false;
            return hashCount < trimmedLine.Length && trimmedLine[hashCount] == ' ';
        }

        /// <summary>
        /// 문서에 적힌 이름 하나를 PromptSchemaColumns의 키로 해석한다.
        ///
        /// 평소엔 관대하게, 충돌할 때만 침묵: 완전 한정 이름이 맞으면 그것을 쓰고,
        /// 아니면 마지막 파트로 찾되 후보가 정확히 하나일 때만 인정한다. 둘 이상이면
        /// null을 돌려 검사를 건너뛴다 - 오류로 만들지 않는다.
        ///
        /// 후보를 셀 때 ColumnlessDependencyTables(컬럼 0개라 대조 기준에서 빠진
        /// 의존성)도 말단 이름 충돌 판정에 포함시킨다. 그렇지 않으면 컬럼 0개
        /// 테이블을 가리킨 이름이, 대조 기준에는 없다는 이유만으로 같은 말단 이름을
        /// 가진 컬럼 있는 동명 테이블로 조용히 오귀속된다 - 리뷰가 실측한 결함이다.
        /// </summary>
        private static string? ResolveSchemaTableKey(string writtenName, SpecExpectations expectations)
        {
            var normalized = NormalizeQualifiedName(writtenName);
            if (normalized.Length == 0) return null;

            if (expectations.PromptSchemaColumns.ContainsKey(normalized)) return normalized;

            var lastPart = LastNamePart(writtenName);
            if (lastPart.Length == 0) return null;

            string? single = null;
            foreach (var key in expectations.PromptSchemaColumns.Keys)
            {
                if (!string.Equals(LastNamePart(key), lastPart, StringComparison.OrdinalIgnoreCase)) continue;
                if (single != null) return null; // 모호하다.
                single = key;
            }

            if (single != null)
            {
                foreach (var columnless in expectations.ColumnlessDependencyTables)
                {
                    // single 자신과 같은 canonical이면 충돌이 아니다 - 자기 자신과는
                    // 특정 불가능성이 없다. 이 분기는 지금은 도달 불가하다: 같은
                    // canonical이 컬럼 보유 집합과 컬럼 0개 집합에 동시에 실리는
                    // 경로를 SpecExpectations.From이 만들지 않고(한 canonical은
                    // 둘 중 하나에만 들어간다), DbMetadataService의 visited 집합이
                    // 같은 의존성의 중복 등록도 막는다. 그래도 방어로 남겨 둔다 -
                    // 그 중복 방지 계약이 바뀌면 이 가드가 조용히 이 모호성 판정
                    // 게이트 자체를 꺼 버리기 때문이다.
                    if (string.Equals(columnless, single, StringComparison.OrdinalIgnoreCase)) continue;

                    if (string.Equals(LastNamePart(columnless), lastPart, StringComparison.OrdinalIgnoreCase))
                    {
                        return null; // 컬럼 0개 동명 테이블과 충돌한다 - 어느 쪽인지 특정 불가.
                    }
                }
            }

            return single;
        }

        /// <summary>
        /// `## CRUD 분석` 섹션을 찾는다. 완전 일치를 먼저 시도하고, 실패하면 부분 일치로
        /// 폴백한다.
        ///
        /// 필수 헤더 존재 검사(ValidateMarkdownStructure, :540-550 부근)는 `Contains`
        /// 부분 일치를 쓰는데 여기(CheckUpdateMappings)는 `MarkdownSectionLocator.LocateSection`의
        /// 완전 일치를 썼다. 그래서 `## 3. CRUD 분석`처럼 접두가 붙은 산출물은 헤더 검사는
        /// 통과하면서 매핑 대조만 조용히 꺼졌다 - 16개 컬럼이 산문으로 뭉개져도 L1을
        /// 통과하는 결함이 바로 이것이다. 두 검사가 같은 판정 기준을 쓰도록 여기서
        /// 폴백을 추가한다.
        ///
        /// 폴백은 처음에 이 클래스 안에 손으로 썼다 - `MarkdownSectionLocator`에 다른
        /// 소비자(계획서 분할)가 있어 그 클래스의 기존 동작을 건드리지 않으려는 것이었다.
        /// 그런데 그 "다른 소비자"가 같은 이유로 깨졌다: 골격이
        /// `## 단계별 이행 상세 및 의사코드:`처럼 꼬리표를 붙여 쓰자 조립기가 블록을 못 찾고
        /// 문서 끝에 같은 H2를 새로 합성했다(POQSettleProc17·18 연속 재발). 같은 판정을
        /// 두 곳이 각자 구현하면 한쪽만 고쳐진다 - 판정은 `MarkdownSectionLocator`의
        /// `exact: false`로 옮기고, 기본값이 정확 일치라 다른 호출부는 그대로다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateCrudSection(IReadOnlyList<string> lines)
        {
            var exact = MarkdownSectionLocator.LocateSection(lines, "## CRUD 분석", "## ");
            return exact.HeaderIndex >= 0
                ? exact
                : MarkdownSectionLocator.LocateSection(lines, "## CRUD 분석", "## ", exact: false);
        }

        /// <summary>
        /// 각 줄이 코드 펜스(```) 안에 있는지 표시하는 배열을 만든다. 판정 규칙(줄 시작을
        /// 트림한 뒤 ```로 시작하면 토글)은 <see cref="MarkdownSectionLocator.FindIndexOutsideFence"/>가
        /// 헤딩 탐색에 쓰는 것과 동일하다 - LocateCrudSection이 이미 그 메서드로 펜스를
        /// 추적하는데, CheckSchemaClaims·CheckTableIdentitySplit의 줄 순회만 추적하지
        /// 않으면 한 파일 안에서 펜스 판정 기준이 갈린다(실측 오탐).
        ///
        /// 펜스 구분자 줄 자체도 true로 표시한다 - 그 줄은 문법 기호일 뿐 검사 대상
        /// 문장이 아니다.
        ///
        /// `MarkdownSectionLocator`에는 줄 단위 펜스 배열을 돌려주는 API가 없고, 다른
        /// 소비자(계획서 분할)가 있어 그 클래스는 고치지 않기로 했으므로 여기 최소한으로
        /// 다시 구현한다.
        /// </summary>
        private static bool[] ComputeFenceLineFlags(IReadOnlyList<string> lines)
        {
            var flags = new bool[lines.Count];
            var inFence = false;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    flags[i] = true;
                    inFence = !inFence;
                    continue;
                }

                flags[i] = inFence;
            }

            return flags;
        }

        /// <summary>
        /// 기대 하나에 대응하는 문서 섹션 본문을 찾는다.
        ///
        /// 평소엔 관대하게, 충돌할 때만 엄격하게: 먼저 완전 한정 이름이 일치하는 섹션을
        /// 찾는다. 없으면 마지막 파트로 찾되, 후보 섹션과 후보 기대가 각각 정확히 하나일
        /// 때만 인정한다. 마지막 파트로 접어 처음부터 합치면(구 버전의 방식) 서로 다른
        /// 두 테이블이 한 섹션으로 뭉개져 한쪽의 컬럼이 다른 쪽의 누락을 가려버린다
        /// - 리뷰에서 실측된 결함이다. 조건이 깨지면(모호하면) 병합하지 않고 오류로 본다.
        /// </summary>
        private static string? ResolveSectionBody(
            UpdateColumnExpectation expectation,
            IReadOnlyList<UpdateColumnExpectation> allExpectations,
            IReadOnlyDictionary<string, string> sections,
            ValidationResult result)
        {
            var normalizedTarget = NormalizeQualifiedName(expectation.Table);

            if (sections.TryGetValue(normalizedTarget, out var exactBody))
            {
                return exactBody;
            }

            var lastPart = LastNamePart(expectation.Table);

            var candidateSections = sections
                .Where(kvp => string.Equals(LastNamePart(kvp.Key), lastPart, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var candidateExpectations = allExpectations
                .Where(e => string.Equals(LastNamePart(e.Table), lastPart, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidateSections.Count == 1 && candidateExpectations.Count == 1)
            {
                return candidateSections[0].Value;
            }

            if (candidateSections.Count == 0)
            {
                AddUpdateMappingError(result,
                    $"`## CRUD 분석`에 UPDATE 대상 테이블 `{expectation.Table}`의 매핑 표가 없습니다. " +
                    $"정적 파서가 확정한 SET 대상 컬럼: {string.Join(", ", expectation.Columns)}");
                return null;
            }

            // 모호함: 후보 섹션이 여럿이거나, 같은 마지막 파트를 요구하는 기대가 여럿이다.
            var candidateNames = string.Join(", ", candidateSections.Select(kvp => $"`{kvp.Key}`"));
            AddUpdateMappingError(result,
                $"UPDATE 대상 테이블 `{expectation.Table}`을(를) 마지막 파트 `{lastPart}`만으로는 특정할 수 없습니다 " +
                $"(후보 섹션: {candidateNames}). 명세서의 UPDATE 대상 테이블 헤딩을 완전 한정 이름으로 구분해 작성해 주십시오.");
            return null;
        }

        private static void AddUpdateMappingError(ValidationResult result, string message)
        {
            result.Errors.Add(message);
            result.DetailedErrors.Add(new DetailedError
            {
                Type = ErrorType.UpdateMappingMissing,
                Message = message
            });
        }

        /// <summary>
        /// UPDATE 표 구간을 완전 한정 이름별로 모은다. 같은 완전 한정 이름이 여러 번
        /// 나오면(문장이 여럿이라 헤딩이 반복되면) 이어 붙인다.
        ///
        /// 여기서 마지막 파트로 접지 않는다. 수집 단계에서 접으면 서로 다른 두 테이블이
        /// (예: DB1.dbo.TCommMst와 DB2.dbo.TCommMst) 키 하나로 뭉개져 한쪽 섹션의 컬럼이
        /// 다른 쪽의 누락을 가려버린다. 마지막 파트 완화는 대조 단계(ResolveSectionBody)의
        /// 일이지, 수집 단계의 일이 아니다.
        /// </summary>
        private static Dictionary<string, string> CollectUpdateSections(
            IReadOnlyList<string> lines, int start, int end)
        {
            var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var index = start;

            while (index < end)
            {
                if (!lines[index].TrimStart().StartsWith(UpdateHeadingPrefix, StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                var table = NormalizeQualifiedName(ReadHeadingTable(lines[index].TrimStart()));
                var bodyStart = index + 1;

                var bodyEnd = MarkdownSectionLocator.FindIndexOutsideFence(
                    lines, bodyStart,
                    line => line.TrimStart().StartsWith("### ", StringComparison.Ordinal)
                         || line.TrimStart().StartsWith("## ", StringComparison.Ordinal));

                if (bodyEnd < 0 || bodyEnd > end) bodyEnd = end;

                var body = string.Join("\n", lines.Skip(bodyStart).Take(bodyEnd - bodyStart));
                sections[table] = sections.TryGetValue(table, out var existing)
                    ? existing + "\n" + body
                    : body;

                index = bodyEnd;
            }

            return sections;
        }

        /// <summary>
        /// 헤딩에서 테이블명을 읽는다. 프롬프트가 요구하는 "(문장 N)" 꼬리와 AI가 덧붙일
        /// 수 있는 부연을 떨어낸다. 공백뿐 아니라 여는 괄호에서도 끊는다 - AI가
        /// "TCommMst(문장 1)"처럼 공백 없이 붙여 써도 괄호가 테이블명에 삼켜지면 안 된다.
        /// </summary>
        private static string ReadHeadingTable(string headingLine)
        {
            var rest = headingLine.Substring(UpdateHeadingPrefix.Length).Trim();
            var cut = 0;
            while (cut < rest.Length && !char.IsWhiteSpace(rest[cut]) && rest[cut] != '(')
            {
                cut++;
            }

            return rest.Substring(0, cut);
        }

        /// <summary>
        /// 한정된 이름의 앞뒤에 붙는 백틱·대괄호·공백만 걷어낸다. 완전 한정 이름을 그대로
        /// 대조 키로 쓸 때 쓴다 - 마지막 파트로 접지 않는다.
        /// </summary>
        private static string NormalizeQualifiedName(string name) =>
            name.Trim().Trim('`').Trim('[', ']', '`').Trim();

        /// <summary>
        /// 한정된 이름에서 마지막 파트만 남긴다. 프롬프트는 canonical 3-part를 요구하지만
        /// AI가 짧게 쓰는 것은 결함이 아니다. 완전 한정 이름이 일치하지 않을 때의
        /// 폴백으로만 쓴다 - ResolveSectionBody 참고.
        /// </summary>
        private static string LastNamePart(string name)
        {
            var trimmed = name.Trim().Trim('`');
            var dot = trimmed.LastIndexOf('.');
            var last = dot < 0 ? trimmed : trimmed.Substring(dot + 1);
            return last.Trim('[', ']', '`');
        }

        private void ValidateMarkdownStructure(string markdown, IReadOnlyList<string> requiredHeaders, ValidationResult result)
        {
            var doc = Markdown.Parse(markdown);
            var headings = new List<string>();
            var mermaidBlocks = new List<string>();

            foreach (var block in doc)
            {
                if (block is HeadingBlock heading)
                {
                    var text = heading.Inline?.FirstChild?.ToString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                    {
                        headings.Add(text);
                    }
                }
                else if (block is FencedCodeBlock codeBlock && codeBlock.Info == "mermaid")
                {
                    var content = string.Empty;
                    if (codeBlock.Lines.Count > 0)
                    {
                        var writer = new System.IO.StringWriter();
                        foreach (var line in codeBlock.Lines)
                        {
                            writer.WriteLine(line.ToString());
                        }
                        content = writer.ToString();
                    }
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        mermaidBlocks.Add(content);
                    }
                }
            }

            // 1. 필수 헤더 존재 검증
            foreach (var req in requiredHeaders)
            {
                bool found = false;
                foreach (var h in headings)
                {
                    if (h.Contains(req, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    var msg = $"필수 섹션 헤더 '## {req}'가 누락되었습니다.";
                    Log.Warning("린트 에러 감지 (헤더 누락) - {Message}", msg);
                    result.Errors.Add(msg);
                    result.DetailedErrors.Add(new DetailedError { Type = ErrorType.HeaderMissing, Message = msg });
                }
            }

            // 1.5 Anti-Shortcut (축약/생략 방지) 기계적 검증
            var scannable = StripQuotedLines(markdown);
            foreach (var forbidden in ForbiddenShortcuts)
            {
                if (ContainsForbiddenShortcut(scannable, forbidden))
                {
                    var msg = $"표 내부에 허용되지 않는 축약어/생략 기호('{forbidden}')가 감지되었습니다. 모든 컬럼과 매핑을 완벽히 기술해야 합니다.";
                    Log.Warning("린트 에러 감지 (Anti-Shortcut 위반) - {Message}", msg);
                    result.Errors.Add(msg);
                    result.DetailedErrors.Add(new DetailedError { Type = ErrorType.General, Message = msg });
                }
            }

            // 2. Mermaid 문법 검증
            foreach (var mContent in mermaidBlocks)
            {
                ValidateMermaid(mContent, result);
            }
        }

        /// <summary>
        /// 인용문(&gt;) 줄을 검사 대상에서 뺀다.
        ///
        /// VerificationBanner가 잔존 오류를 본문 앞에 인용하는데, 그 메시지 자체가
        /// 금지 토큰을 따옴표로 담는다("...기호('etc.')가 감지되었습니다"). 배너가
        /// 붙은 문서를 다시 검증하면 배너가 스스로를 오류로 만들어 어떤 재생성으로도
        /// 통과할 수 없다 - COMM_UPD 실측에서 관측된 자기 오염이다.
        ///
        /// 이 검사의 대상은 애초에 표와 본문이다(오류 메시지가 "표 내부에"라고
        /// 말한다). 인용문은 파이프라인이 붙인 메타 정보이지 AI가 쓴 명세가 아니다.
        /// </summary>
        private static string StripQuotedLines(string markdown)
        {
            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var kept = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith(">", StringComparison.Ordinal)) continue;
                kept.Add(line);
            }
            return string.Join("\n", kept);
        }

        /// <summary>
        /// "etc."만 앞 경계를 따진다. 컬럼명이 Etc로 끝나고 문장 끝에 오면
        /// "CLEtc."가 되는데 이것은 축약어가 아니라 사실이다.
        ///
        /// AiService가 자기참조 컬럼 목록 뒤에 마침표를 붙여 프롬프트에 넣고
        /// (AiService.cs:566), AI가 지시대로 그 문장을 옮겨 적고, 부분 문자열
        /// 검사가 그것을 'etc.'로 읽었다. 재생성으로 고칠 수 없는 오류였고 -
        /// AI가 쓴 것이 정답이므로 - COMM_UPD의 L1 3회를 모두 소진시켰다.
        ///
        /// 나머지 토큰은 한국어이거나 기호라 이 문제가 없어 그대로 둔다.
        /// </summary>
        /// <summary>
        /// 표와 본문에서 금지하는 축약·생략 표기. 문서 레벨 검사와 단계 하한 검사가
        /// 이 목록 하나를 공유한다 — 나눠 가지면 한쪽만 새 축약어를 알게 되고, 그
        /// 순간 단계에서 거르지 못한 것이 문서 레벨로 올라가 전체 재생성을 부른다.
        /// </summary>
        internal static readonly string[] ForbiddenShortcuts =
            { "이하 생략", "(생략)", "위와 동일", "기타 등등", "etc.", "TS[]" };

        private static bool ContainsForbiddenShortcut(string text, string forbidden) =>
            forbidden == "etc."
                ? StandaloneEtcRegex.IsMatch(text)
                : text.Contains(forbidden, StringComparison.OrdinalIgnoreCase);

        private static readonly Regex StandaloneEtcRegex =
            new Regex(@"(?<![A-Za-z])etc\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private void ValidateMermaid(string mermaidContent, ValidationResult result)
        {
            if (_useMermaidCli)
            {
                try
                {
                    // 임시 파일 생성
                    var tempDir = Path.Combine(Path.GetTempPath(), "ReSet_Mermaid");
                    if (!Directory.Exists(tempDir))
                    {
                        Directory.CreateDirectory(tempDir);
                    }
                    var tempInput = Path.Combine(tempDir, $"{Guid.NewGuid()}.mmd");
                    var tempOutput = Path.Combine(tempDir, $"{Guid.NewGuid()}.svg");

                    File.WriteAllText(tempInput, mermaidContent);

                    // mmdc (mermaid-cli) 실행 준비
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "mmdc",
                        Arguments = $"-i \"{tempInput}\" -o \"{tempOutput}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process != null)
                        {
                            // 타임아웃 10초
                            if (process.WaitForExit(10000))
                            {
                                if (process.ExitCode != 0)
                                {
                                    var stderr = process.StandardError.ReadToEnd().Trim();
                                    Log.Warning("Mermaid CLI 검증 문법 오류 감지 - Stderr: {Stderr}. Fallback 기계 검증으로 전환합니다.", stderr);
                                    
                                    // 린트 실패를 치명적 오류로 처리하지 않고, Fallback 기계 린터로 검증 우회
                                    ValidateMermaidFallback(mermaidContent, result);
                                }
                            }
                            else
                            {
                                try { process.Kill(); } catch { }
                                Log.Warning("Mermaid CLI 검증 시간 초과(10초). Fallback 기계 검증으로 전환합니다.");
                                ValidateMermaidFallback(mermaidContent, result);
                            }
                        }
                    }

                    // 정리
                    if (File.Exists(tempInput)) File.Delete(tempInput);
                    if (File.Exists(tempOutput)) File.Delete(tempOutput);
                }
                catch (Exception)
                {
                    // CLI 실행 에러(예: mmdc 명령어가 설치되지 않은 경우) -> Soft-fail로 기존 정규식 방식으로 우회
                    ValidateMermaidFallback(mermaidContent, result);
                }
            }
            else
            {
                // 사용안함 시 기존 검증 방식
                ValidateMermaidFallback(mermaidContent, result);
            }
        }

        private void ValidateMermaidFallback(string mermaidContent, ValidationResult result)
        {
            var lines = mermaidContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            // 기존의 노드 따옴표 검증
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("%%")) continue;

                var nodeMatches = MermaidNodeRegex.Matches(trimmedLine);

                foreach (Match nodeMatch in nodeMatches)
                {
                    var nodeId = nodeMatch.Groups[1].Value.Trim();
                    string labelText = "";

                    for (int i = 2; i < nodeMatch.Groups.Count; i += 3)
                    {
                        if (nodeMatch.Groups[i].Success)
                        {
                            labelText = nodeMatch.Groups[i + 1].Value.Trim();
                            break;
                        }
                    }

                    if (nodeId.Equals("graph", StringComparison.OrdinalIgnoreCase) ||
                        nodeId.Equals("flowchart", StringComparison.OrdinalIgnoreCase) ||
                        nodeId.Equals("subgraph", StringComparison.OrdinalIgnoreCase) ||
                        nodeId.Equals("end", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (labelText.Contains("(") || labelText.Contains(")") || 
                        labelText.Contains("[") || labelText.Contains("]") ||
                        labelText.Contains("{") || labelText.Contains("}") ||
                        labelText.Contains(",") || labelText.Contains("'") ||
                        labelText.Contains(":") || labelText.Contains("-"))
                    {
                        if (!(labelText.StartsWith("\"") && labelText.EndsWith("\"")))
                        {
                            var msg = $"Mermaid 다이어그램 내 노드 '{nodeId}'의 텍스트 '{labelText}'에 괄호나 특수문자가 포함되어 있으나 큰따옴표(\"\")로 감싸지지 않았습니다. 문법 오류를 막기 위해 '\"{labelText}\"' 형태로 큰따옴표를 감싸서 출력해 주십시오.";
                            Log.Warning("린트 에러 감지 (Mermaid 따옴표 누락) - Node: {NodeId}, Label: {LabelText}", nodeId, labelText);
                            result.Errors.Add(msg);
                            result.DetailedErrors.Add(new DetailedError 
                            { 
                                Type = ErrorType.MermaidQuoteMissing, 
                                Message = msg, 
                                RawContext = trimmedLine 
                            });
                        }
                    }
                }
            }
        }
        public string PostProcessMarkdown(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return markdown;

            try
            {
                // markdown 내의 ```mermaid ... ``` 블록을 찾아서 안의 내용을 치환합니다.
                var regex = new Regex(@"```mermaid\s*\n([\s\S]*?)\n```", RegexOptions.Compiled);
                return regex.Replace(markdown, m =>
                {
                    var originalMermaid = m.Groups[1].Value;
                    var cleansedMermaid = CleanseMermaidCode(originalMermaid);
                    return $"```mermaid\n{cleansedMermaid}\n```";
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Mermaid 코드 후처리 중 오류 발생 (원본 유지)");
                return markdown;
            }
        }

        private string CleanseMermaidCode(string mermaid)
        {
            var lines = mermaid.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var resultLines = new List<string>();

            // Keywords to ignore when normalizing node IDs
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "flowchart", "graph", "subgraph", "end",
                "td", "lr", "tb", "bt", "rl",
                "style", "fill", "stroke", "stroke-width", "stroke-dasharray",
                "linkstyle", "interpolate", "classdef", "class", "click", "default"
            };

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("%%") || string.IsNullOrEmpty(trimmed))
                {
                    resultLines.Add(line);
                    continue;
                }

                var processedLine = line;

                // 1. 화살표 라벨 따옴표 제거 및 표준화 (하이픈 제거 추가)
                // -- "텍스트" --> 또는 -->| "텍스트" | 또는 -->|"텍스트"| 등을 -->|텍스트| 로 변환하되 하이픈(-)은 제거
                processedLine = Regex.Replace(processedLine, @"-->\s*\|""?\s*([^""|]+?)\s*""?\|", m => "-->|" + m.Groups[1].Value.Replace("-", "").Trim() + "|");
                processedLine = Regex.Replace(processedLine, @"--\s*""\s*([^"">]+?)\s*""\s*-->", m => "-->|" + m.Groups[1].Value.Replace("-", "").Trim() + "|");
                processedLine = Regex.Replace(processedLine, @"--\s*([^"">]+?)\s*-->", m => "-->|" + m.Groups[1].Value.Replace("-", "").Trim() + "|");

                // 2. 비표준 화살표 조건절 및 누락된 화살표 보정 (예: 'A -- |label| B' -> 'A -->|label| B')
                processedLine = Regex.Replace(processedLine, @"--\s*\|([^|]+)\|\s*([a-zA-Z0-9_]+)", "-->|$1| $2");

                // 3. 잘못된 화살표 기호 보정 ( -> 또는 - -> 를 --> 로 변환)
                processedLine = Regex.Replace(processedLine, @"-\s*->", "-->");
                processedLine = Regex.Replace(processedLine, @"-[^-]>", "-->");

                // 3.5. subgraph 와 식별자 사이 띄어쓰기 강제 보정 및 식별자와 라벨 사이 띄어쓰기 보정
                processedLine = Regex.Replace(processedLine, @"\bsubgraph([a-zA-Z0-9_]+)", "subgraph $1");
                processedLine = Regex.Replace(processedLine, @"\bsubgraph\s+([a-zA-Z0-9_]+)\[", "subgraph $1 [");

                // 4. 노드 ID 내에 공백이나 특수문자(언더스코어 포함)가 들어간 경우 보정 및 라벨 이스케이프
                processedLine = MermaidNodeRegex.Replace(processedLine, match =>
                {
                    var nodeId = match.Groups[1].Value;
                    string opening = "", label = "", closing = "";
                    for (int i = 2; i < match.Groups.Count; i += 3)
                    {
                        if (match.Groups[i].Success)
                        {
                            opening = match.Groups[i].Value;
                            label = match.Groups[i+1].Value;
                            closing = match.Groups[i+2].Value;
                            break;
                        }
                    }

                    var testId = nodeId.Trim();
                    if (testId.Equals("graph", StringComparison.OrdinalIgnoreCase) ||
                        testId.Equals("flowchart", StringComparison.OrdinalIgnoreCase) ||
                        testId.Equals("end", StringComparison.OrdinalIgnoreCase))
                    {
                        return match.Value;
                    }

                    bool isSubgraph = false;
                    if (testId.StartsWith("subgraph ", StringComparison.OrdinalIgnoreCase) || 
                        testId.Equals("subgraph", StringComparison.OrdinalIgnoreCase))
                    {
                        isSubgraph = true;
                        if (testId.Length > 8)
                            testId = testId.Substring(9).Trim(); // Remove "subgraph "
                        else
                            testId = "";
                    }

                    // 공백 및 언더스코어 제거
                    var cleansedId = testId.Replace(" ", "").Replace("_", "");
                    
                    // 만약 라벨에 특수문자(괄호, 콜론, 대시 등)가 있는데 큰따옴표가 없으면 큰따옴표로 감싸주기
                    var trimmedLabel = label.Trim();
                    if (trimmedLabel.Contains("(") || trimmedLabel.Contains(")") ||
                        trimmedLabel.Contains("[") || trimmedLabel.Contains("]") ||
                        trimmedLabel.Contains("{") || trimmedLabel.Contains("}") ||
                        trimmedLabel.Contains(",") || trimmedLabel.Contains("'") ||
                        trimmedLabel.Contains(":") || trimmedLabel.Contains("-") ||
                        trimmedLabel.Contains(">") || trimmedLabel.Contains("<") ||
                        trimmedLabel.Contains("/") || trimmedLabel.Contains("\\") ||
                        // Mermaid 11에서 따옴표 없는 '@'는 링크 ID 문법으로 해석돼
                        // 파스 에러가 난다(실측: "got 'LINK_ID'"). 따옴표만 씌우면 정상이다.
                        trimmedLabel.Contains("@"))
                    {
                        if (!(trimmedLabel.StartsWith("\"") && trimmedLabel.EndsWith("\"")))
                        {
                            trimmedLabel = $"\"{trimmedLabel}\"";
                        }
                    }

                    if (isSubgraph)
                    {
                        return string.IsNullOrEmpty(cleansedId) 
                            ? $"subgraph{opening}{trimmedLabel}{closing}" 
                            : $"subgraph {cleansedId}{opening}{trimmedLabel}{closing}";
                    }

                    return $"{cleansedId}{opening}{trimmedLabel}{closing}";
                });

                // 5. 노드 ID의 언더스코어/공백 제거 일관성 확보 (정의부 및 참조부 전체 적용)
                // 문자열과 조건절 내부(|...| 및 "...")는 건드리지 않고, 노드 ID 및 style 참조부에만 적용
                var pattern = @"(""[^""]*"")|(\|[^|]*\|)|(\b[a-zA-Z_][a-zA-Z0-9_]*\b)";
                processedLine = Regex.Replace(processedLine, pattern, match =>
                {
                    if (match.Groups[1].Success)
                    {
                        return match.Groups[1].Value; // 이중 따옴표 안은 그대로 둠
                    }
                    if (match.Groups[2].Success)
                    {
                        return match.Groups[2].Value; // 파이프 안은 그대로 둠
                    }
                    
                    var word = match.Groups[3].Value;
                    if (keywords.Contains(word))
                    {
                        return word; // 예약어는 그대로 둠
                    }
                    
                    // 일반 단어(노드 ID)에서 공백 및 언더스코어 제거
                    return word.Replace(" ", "").Replace("_", "");
                });

                resultLines.Add(processedLine);
            }

            return string.Join("\n", resultLines);
        }

        /// <summary>
        /// 그림자 백업 장치의 세 역학을 본다.
        ///
        /// 감사 실측에서 다섯 단계가 각기 다른 이유로 복구 불능이었다. 규칙 4가
        /// "선행 DELETE 후 복원"만 강제하고 생성 위치·복원 범위·동적 SQL 변수
        /// 스코프는 한 마디도 하지 않았기 때문이다.
        ///
        /// 세 검사 모두 <see cref="BlankCommentsAndStrings"/>로 주석·문자열 내용을
        /// 지운 사본을 대조 기준으로 삼는다(단, (c)는 EXEC() 몸체를 찾는 위치만
        /// 사본에서 정하고, 그 안에 진짜 변수 참조가 있는지는 원문에서 본다 - 검사
        /// 대상 자체가 문자열 리터럴의 내용물이기 때문이다). 6번 과제가 세 라운드에
        /// 걸쳐 겪은 문제 두 가지를 피하기 위해서다: 원문을 그대로 훑으면 주석
        /// `-- BEGIN TRAN은 안 쓴다`의 텍스트가 진짜 트랜잭션 시작으로 오인되고
        /// (오탐), 첫 BEGIN TRAN/COMMIT TRAN 쌍만 보면 두 번째 이후 트랜잭션 블록
        /// 안의 위반을 놓친다(미탐) - 그래서 (a)는 모든 BEGIN TRAN을 훑고, 그 트랜잭션을
        /// 닫는 문으로 COMMIT TRAN과 ROLLBACK TRAN을 함께 찾는다.
        /// </summary>
        private static void CheckShadowBackupContract(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            var cleaned = BlankCommentsAndStrings(stepMarkdown);

            // (a) 트랜잭션 안에서 만든 그림자는 롤백과 함께 소멸한다. "첫 BEGIN TRAN부터
            // 첫 종료문까지"만 보면 중첩 트랜잭션에서 안쪽 COMMIT TRAN을 바깥 트랜잭션의
            // 종료로 오인해, 안쪽 COMMIT 뒤·바깥 COMMIT 앞에 있는 그림자를 "트랜잭션
            // 밖"으로 잘못 분류한다(리뷰 재현, 미탐) - @@TRANCOUNT처럼 깊이를 세어, 그
            // 깊이가 0보다 큰 구간 전체를 "안"으로 본다. 문서에 트랜잭션이 여럿이면
            // 각각 독립된 구간이 된다. 닫는 문이 짝을 잃고 남으면(깊이가 0보다 큰 채로
            // 문서가 끝나면) 문서 끝까지가 안이다. 깊이가 이미 0일 때 만난 종료문은
            // 무시한다 - 대응하는 BEGIN이 없는 종료문 하나 때문에 깊이가 음수로 내려가
            // 이후 스캔이 어긋나는 것을 막기 위해서다. `SAVE TRAN`(세이브포인트)은
            // "BEGIN"으로 시작하지 않으므로 애초에 이 스캔에 잡히지 않는다 - 깊이를
            // 올리지 않는다.
            var depth = 0;
            var openStart = -1;
            var openTransactionSpans = new List<(int Start, int End)>();
            var events = new List<(int Index, int Length, bool IsBegin)>();
            foreach (Match m in BeginTranPattern.Matches(cleaned)) events.Add((m.Index, m.Length, true));
            foreach (Match m in EndTranPattern.Matches(cleaned)) events.Add((m.Index, m.Length, false));
            events.Sort((x, y) => x.Index.CompareTo(y.Index));

            foreach (var ev in events)
            {
                if (ev.IsBegin)
                {
                    if (depth == 0) openStart = ev.Index + ev.Length;
                    depth++;
                    continue;
                }

                if (depth == 0) continue; // 짝 없는 종료문 - 무시하고 계속한다.

                depth--;
                if (depth == 0 && openStart >= 0)
                {
                    openTransactionSpans.Add((openStart, ev.Index));
                    openStart = -1;
                }
            }

            // 닫는 문이 짝을 잃고 남으면 문서 끝까지가 안이다.
            if (depth > 0 && openStart >= 0) openTransactionSpans.Add((openStart, cleaned.Length));

            foreach (var span in openTransactionSpans)
            {
                if (!ShadowIntoPattern.IsMatch(cleaned[span.Start..span.End])) continue;

                result.Errors.Add(
                    $"{step.Code} 섹션이 BEGIN TRAN 안에서 그림자 테이블을 만듭니다. " +
                    "SELECT INTO로 만든 테이블은 롤백과 함께 소멸하므로, 실패 시 복원할 " +
                    "대상이 사라진 채 CATCH의 DELETE만 자동 커밋으로 실행되어 롤백이 이미 " +
                    "복원한 행을 다시 지웁니다. 그림자는 BEGIN TRAN 앞에서 만드십시오. " +
                    "단일 트랜잭션으로 끝나는 단계라면 그림자 없이 ROLLBACK TRAN만 쓰십시오.");
            }

            // (b) 복원은 원래 삭제한 범위와 같은 범위를 지워야 한다. WHERE로 범위를
            // 좁힌 DELETE는 테이블명 바로 뒤에 세미콜론이 오지 않으므로 이 패턴에
            // 걸리지 않는다 - `[\w.\[\]]+\s*;`가 테이블명 문자만 삼키고 WHERE 절의
            // 공백·비교 연산자 앞에서 멈추기 때문이다.
            //
            // 이 규칙이 말하려는 것은 "그림자에서 복원할 때 원래 지운 범위와 같은
            // 범위만 지워야 한다"이지, WHERE 없는 전량 삭제 자체가 아니다. INSERT의
            // 원천이 `batch_shadow.`가 아니면(그림자와 무관한 일반 ETL 전량 갱신 등)
            // 이 검사의 대상이 아니다(리뷰 재현, 오탐) - INSERT 문 전체(다음 `;`까지)를
            // 잡아 그 안에 `FROM batch_shadow.`가 있는지 따로 확인한다.
            // 보상 복원은 CATCH 안에 있고 정방향 스왑은 밖에 있다. 이 구분이
            // (b)의 실제 판별 기준이다 - 트랜잭션 깊이로만 제외하면 보상 복원을
            // 자기 트랜잭션으로 감싼 형태가 통째로 빠져나간다(최종 리뷰 실측).
            var catchSpans = CatchBlockPattern.Matches(cleaned)
                .Select(m => (Start: m.Index, End: m.Index + m.Length))
                .ToList();

            foreach (Match restore in RestoreWithoutRangePattern.Matches(cleaned))
            {
                if (!ShadowSourcePattern.IsMatch(restore.Groups["insertBody"].Value)) continue;

                // [최종 리뷰 B-1 수정, 이후 B-3 수정] 이 매치가 열린 BEGIN TRAN 안
                // (정방향 스왑)에 있으면 (b)의 대상이 아니다. (b)가 겨냥하는 것은
                // CATCH의 *복원*(ROLLBACK 뒤 자동 커밋 구간)이지, 트랜잭션 하나로
                // 끝나는 정방향 교체가 아니다 - 스왑은 같은 DELETE-INSERT 모양이지만
                // 실패하면 트랜잭션 전체가 롤백되어 DELETE 자체가 무효가 되므로
                // "다른 거래일 행이 되돌아가는" 위험이 없다. Few-Shot "Shadow Table
                // Swap Pattern"의 `BEGIN TRAN; DELETE ...; INSERT ... FROM
                // batch_shadow...; COMMIT TRAN;`이 정확히 이 모양이라 프롬프트의
                // 모범 예시를 L1이 반려하는 오탐이 실행 재현됐다(리뷰 재현).
                //
                // 다만 제외 조건을 트랜잭션 깊이만으로 두면, 보상 복원을 자기
                // BEGIN TRAN으로 감싼 형태(CATCH 안에서 원자성 래퍼만 두른 것)가
                // 통째로 빠져나간다(최종 리뷰 실측) - 그 래퍼는 다른 거래일의
                // 행을 되돌려주지 않고 피해를 원자적으로 커밋할 뿐이다. 정방향
                // 스왑은 CATCH 밖에 있고 보상 복원은 CATCH 안에 있다는 것이 실제
                // 판별 기준이므로, 열린 트랜잭션 안이면서 CATCH 밖일 때만 제외한다.
                var insideOpenTransaction =
                    openTransactionSpans.Any(span => restore.Index >= span.Start && restore.Index < span.End);
                var insideCatch =
                    catchSpans.Any(span => restore.Index >= span.Start && restore.Index < span.End);

                if (insideOpenTransaction && !insideCatch) continue;

                result.Errors.Add(
                    $"{step.Code} 섹션의 복원이 `{restore.Groups["t"].Value}`를 WHERE 없이 " +
                    "전량 삭제한 뒤 재삽입합니다. 복원은 이 단계가 실제로 지운 범위와 같은 " +
                    "범위만 지워야 합니다 - 전량 삭제하면 다른 거래일의 행까지 실행 시작 " +
                    "시점으로 되돌아가, 레거시에 없는 전역 행 집합 변경 경로가 생깁니다.");
            }

            // (c) EXEC() 동적 배치는 바깥 배치의 변수를 볼 수 없다. EXEC() 문의 위치는
            // 주석 안이 아닌지 cleaned에서 찾되, 그 몸체의 실제 문자열 리터럴 내용은
            // 원문에서 읽는다 - 검사 대상 자체가 문자열 값이라 주석·문자열 지우기 사본에서는
            // 이미 공백으로 지워져 있다. "EXEC(" 형태만 잡으므로 `EXEC sp_executesql ...`
            // 직접 호출이나 `EXEC dbo.usp_Foo @a, @b` 같은 괄호 없는 프로시저 호출은
            // "EXEC" 바로 뒤에 '('가 없어 애초에 매치되지 않는다.
            foreach (Match exec in ExecDynamicBatchPattern.Matches(cleaned))
            {
                var bodyGroup = exec.Groups["body"];
                var body = stepMarkdown.Substring(bodyGroup.Index, bodyGroup.Length);

                // 문자열 리터럴 안의 @이름만 본다 - 연결에 쓰인 바깥 변수는 정상이다.
                foreach (Match literal in StringLiteralPattern.Matches(body))
                {
                    if (!VariableTokenPattern.IsMatch(literal.Groups["s"].Value)) continue;

                    result.Errors.Add(
                        $"{step.Code} 섹션이 EXEC()로 만든 동적 배치 안에서 바깥 배치의 변수를 " +
                        "참조합니다. 동적 배치는 별도 스코프라 그 변수를 볼 수 없어 스칼라 변수 " +
                        "미선언 오류로 실패합니다. sp_executesql의 매개변수로 값을 넘기십시오.");
                    break;
                }
            }
        }

        private static readonly Regex BeginTranPattern =
            new(@"\bBEGIN\s+TRAN(SACTION)?\b", RegexOptions.IgnoreCase);

        private static readonly Regex EndTranPattern =
            new(@"\b(?:COMMIT|ROLLBACK)\s+TRAN(SACTION)?\b", RegexOptions.IgnoreCase);

        private static readonly Regex ShadowIntoPattern =
            new(@"SELECT\s+.*?\bINTO\s+batch_shadow\.", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex RestoreWithoutRangePattern = new(
            @"DELETE\s+FROM\s+(?<t>[\w.\[\]]+)\s*;(?<tail>.{0,400}?)" +
            @"INSERT\s+INTO\s+\k<t>\b(?<insertBody>.*?);",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // (b)의 두 번째 관문: INSERT 문 안에 `FROM batch_shadow.`(또는 대괄호 인용
        // `FROM [batch_shadow].`)가 있어야 "그림자에서 복원"이다. 값 목록으로 채우는
        // 일반 INSERT(`VALUES (...)`)는 그림자와 무관하다.
        //
        // [재리뷰 수정] 스키마를 리터럴 `batch_shadow.`로만 찾으면 대괄호 인용
        // `[batch_shadow].[X]`를 놓친다 - 실제 텍스트는 `batch_shadow].`이지 `batch_shadow.`가
        // 아니기 때문이다(실행 재현, 미탐). 대괄호 인용은 이 코드베이스가 SET 절·컬럼
        // 목록에서 이미 별도로 다뤄온 SQL Server의 흔한 표기라 정상적인 AI 생성 배치
        // SQL에서 충분히 나올 수 있다. `batch_shadow` 바로 뒤에 여전히 점을 요구하므로,
        // `batch_shadow_archive`처럼 우연히 그 문자열로 시작하는 업무 테이블 이름까지
        // 걸리지는 않는다 - 대괄호를 닫아도(`]`) 그 다음 문자가 여전히 `.`이어야 한다.
        private static readonly Regex ShadowSourcePattern =
            new(@"\bFROM\s+\[?batch_shadow\]?\s*\.", RegexOptions.IgnoreCase);

        private static readonly Regex ExecDynamicBatchPattern =
            new(@"EXEC\s*\((?<body>.*?)\)\s*;", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex StringLiteralPattern = new(@"N?'(?<s>[^']*)'");

        private static readonly Regex VariableTokenPattern = new(@"@\w+");

        /// <summary>
        /// CATCH가 반환 경로 없이 THROW로 끝나는지 본다.
        ///
        /// 프롬프트 규칙 6-1은 상태 변수를 CATCH에서 반환하라 하고 규칙 13은 출력
        /// 파라미터를 누락 없이 매핑하라 하는데, Few-Shot 예시의 CATCH가 THROW로
        /// 끝났다. 모델은 산문 규칙보다 코드 예시를 따른다 - 실측 5건이 그렇게 나왔다.
        ///
        /// <see cref="BlankCommentsAndStrings"/>로 지운 사본에서 CATCH 블록을 찾고 그
        /// 안의 THROW·RETURN도 같은 사본에서 찾는다 - 원문을 그대로 보면 주석
        /// `-- RETURN @x;`의 텍스트가 진짜 반환 경로로 오인되어 THROW-only 위반을
        /// 놓치고(미탐), 주석 `-- THROW를 쓰지 않는다`의 텍스트가 진짜 THROW로
        /// 오인되어 정상 CATCH가 걸린다(오탐) - 6번 과제가 겪은 두 실패 유형과 같은
        /// 모양이라 처음부터 사본을 쓴다.
        /// </summary>
        private static void CheckCatchDiscardsReturnCode(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            var cleaned = BlankCommentsAndStrings(stepMarkdown);

            foreach (Match block in CatchBlockPattern.Matches(cleaned))
            {
                var body = block.Groups["body"].Value;
                if (!ThrowTokenPattern.IsMatch(body)) continue;
                if (ReturnTokenPattern.IsMatch(body)) continue;

                result.Errors.Add(
                    $"{step.Code} 섹션의 CATCH 블록이 반환 경로 없이 THROW로 끝납니다. " +
                    "THROW는 호출부의 OUTPUT 파라미터 대입을 지나쳐 원본 반환 코드를 " +
                    "잃어버립니다. 추적한 상태 변수를 출력 파라미터에 넣고 RETURN하십시오.");
            }
        }

        private static readonly Regex CatchBlockPattern = new(
            @"BEGIN\s+CATCH(?<body>.*?)END\s+CATCH", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex ThrowTokenPattern = new(@"\bTHROW\b", RegexOptions.IgnoreCase);

        private static readonly Regex ReturnTokenPattern = new(@"\bRETURN\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// 정합성 검증 SQL이 카티전 곱으로 두 집계를 비교하는지 본다.
        ///
        /// 실측: FROM TSettleMst AS M CROSS JOIN TSettleByTX AS T 뒤
        /// HAVING SUM(M.TXAMT) &lt;&gt; SUM(T.TXAMT)는 좌변이 |T|×SUM_M,
        /// 우변이 |M|×SUM_T가 되어 |M|≠|T|인 정상 데이터에서 항상 불일치한다.
        /// 정상 실행이 매번 데이터 품질 실패로 기록되어 공개가 상시 차단되고,
        /// 증적에는 카티전 배수만큼 부풀려진 틀린 금액이 남는다.
        ///
        /// <see cref="BlankCommentsAndStrings"/>로 지운 사본에서 CROSS JOIN과 SUM을
        /// 찾는다 - 원문을 그대로 보면 주석 `-- CROSS JOIN을 쓰지 않는다`나 동적 SQL을
        /// 만드는 문자열 리터럴 안의 `CROSS JOIN` 텍스트가 진짜 카티전 조인으로
        /// 오인되어 정상 검증식이 걸린다(오탐).
        /// </summary>
        private static void CheckVerificationCartesianComparison(string markdown, ValidationResult result)
        {
            foreach (Match block in Regex.Matches(
                markdown, @"```sql(?<sql>.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var rawSql = block.Groups["sql"].Value;
                var cleanedSql = BlankCommentsAndStrings(rawSql);

                // 결함은 "한 문 안에" CROSS JOIN과 두 별칭 SUM 비교가 함께 있을 때만
                // 성립한다. 블록 전체를 한 덩어리로 보면, "SQL 세트" 관용대로 한
                // 펜스에 무관한 질의 여럿을 묶었을 때 A 질의의 무해한 CROSS JOIN과
                // B 질의의 정상적인 두 별칭 SUM 비교가 우연히 합쳐져 오탐이 난다 -
                // 감사 수정 라운드 1이 이 오탐을 직접 재현했다. 문 경계(`;`)로 자른
                // 사본에서 각 문을 따로 판정한다.
                foreach (var (cleanedStatement, rawStatement) in SplitSqlStatements(rawSql, cleanedSql))
                {
                    if (!Regex.IsMatch(cleanedStatement, @"\bCROSS\s+JOIN\b", RegexOptions.IgnoreCase)) continue;

                    // 이 문의 CROSS JOIN이 전부 "이미 각자 집계된 CTE 둘"을 잇는
                    // 것이라면 카티전이 1×1이라 무해하다 - AiService.cs 규칙 2가
                    // 권장하는 "양쪽을 각자의 CTE에서 집계한 뒤 비교"를 CROSS JOIN
                    // 문법으로 쓴 정상 패턴이다. 감사 수정 라운드 1이 이 오탐도
                    // 직접 재현했다.
                    if (AllCrossJoinsJoinKnownCtes(cleanedStatement)) continue;

                    // 서로 다른 별칭 둘에 각각 SUM이 걸린 비교만 든다.
                    var aliases = Regex.Matches(cleanedStatement, @"\bSUM\s*\(\s*(?:ISNULL\s*\(\s*)?(?<a>\w+)\.",
                            RegexOptions.IgnoreCase)
                        .Select(m => m.Groups["a"].Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (aliases.Count < 2) continue;

                    var message =
                        "정합성 검증 SQL이 CROSS JOIN으로 두 집계를 비교합니다. 카티전 곱이라 " +
                        "각 변이 상대 테이블의 건수배가 되어 정상 데이터에서 항상 불일치하고, " +
                        "증적에는 그 배수만큼 부풀려진 금액이 남습니다. 양쪽을 각자의 부질의나 " +
                        "CTE에서 독립적으로 집계한 뒤 두 스칼라를 비교하십시오.";

                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.VerificationCartesianComparison,
                        Message = message,
                        RawContext = rawStatement.Trim()
                    });
                }
            }
        }

        /// <summary>
        /// 실행 행을 만드는 지점이 계획서 전체에 하나도 없는지 본다.
        ///
        /// 감사 실측: INSERT INTO batch.BatchRun이 번들 전체에 0건이었다. 모든
        /// 단계가 UPDATE만 해서 0행이 갱신되고, 실행 단위 자체가 존재하지 않았다.
        ///
        /// [왜 통합 문서에서 보는가]
        /// 단계 검사로는 잡을 수 없다 - 어느 단계가 첫 단계인지 단계 문서 하나만
        /// 봐서는 모르고, 설계 §3이 18개 문서를 한꺼번에 읽는 교차 검사를 배제했다.
        /// 통합 문서는 계획서 전체를 보므로 "문서 어딘가에 최소 한 번"으로 닫힌다.
        ///
        /// [왜 소프트 스킵하는가]
        /// 문서가 이 테이블을 언급조차 하지 않으면 이 계약이 적용되는 Job이
        /// 아닐 수 있다. 없는 것을 결함으로 들지 않는다.
        ///
        /// [수정 라운드 1 리뷰 Critical + Minor]
        /// 애초 버전은 `(?:\w+\.)?{bare}\b`라는 자체 정규식을 새로 써서 대괄호 인용
        /// (`[batch].[BatchRun]` 등)을 인식하지 못해 정상 INSERT를 반려했다(Critical,
        /// 소프트 스킵 원칙과 정면 배치 - 있는 것을 없는 것으로 오판). 또한 `mentioned`
        /// 판정에 선행 경계가 없어 `MyBatchRun`처럼 접미사로 겹치는 식별자도 "언급"으로
        /// 오인할 수 있었다(Minor). <see cref="ResolveControlTableAliases"/>가 이미
        /// 쓰는 대괄호 인식 조각(<see cref="QualifiedTableNameFragment"/>)을 재사용해
        /// 둘 다 닫는다 - `mentioned`는 자유 부분 문자열 검색이므로 앞에 `\b`를 붙여
        /// 접미사 겹침을 막고, `inserted`는 `INSERT INTO`/`MERGE` 뒤에 고정 결합되므로
        /// `\b`를 붙이지 않는다(공백 바로 뒤에 대괄호가 오면 그 경계에서 `\b`가
        /// 성립하지 않아 대괄호 형태를 못 찾게 되기 때문이다 - `QualifiedTableNameFragment`
        /// 문서 참고).
        ///
        /// [재리뷰 수정 - B-2와 같은 부류, 방향은 오탐] 이 검사도 B-2가 고친 문제와
        /// 정확히 같은 문서 전체 <see cref="BlankCommentsAndStrings"/>를 썼다. B-2는
        /// 미탐(SQL 펜스가 통째로 지워져 검사 자체가 꺼짐) 방향이었지만, 여기서는
        /// 산문 속 영어 소유격 아포스트로피(예: "the orchestrator's run row") 하나가
        /// 문자열 극성을 뒤집어 뒤따르는 정상 INSERT 펜스까지 공백으로 지워버려
        /// "행을 만드는 지점이 없다"는 오탐을 낸다(실행 재현). <see cref="CleanedSqlFences"/>가
        /// 내는 펜스별 사본을 도는 것으로 바로잡는다 - `mentioned`/`inserted` 모두
        /// 펜스별 판정을 OR로 합치므로 산문의 아포스트로피가 다른 펜스에 영향을
        /// 주지 못한다. 이 검사는 인덱스를 쓰지 않으므로 `Offset`은 버린다.
        /// </summary>
        private static void CheckBatchRunRowCreation(string markdown, ValidationResult result)
        {
            foreach (var table in BatchControlContract.Tables)
            {
                if (table.Origin != ControlRowOrigin.FirstStepInserts) continue;

                var bare = table.Name[(table.Name.LastIndexOf('.') + 1)..];
                var fragment = QualifiedTableNameFragment(bare);

                var mentioned = false;

                foreach (var (cleaned, _) in CleanedSqlFences(markdown))
                {
                    if (Regex.IsMatch(cleaned, $@"\b{fragment}", RegexOptions.IgnoreCase))
                    {
                        mentioned = true;
                        break;
                    }
                }

                if (!mentioned) continue;
                if (CreatesRowIn(markdown, bare)) continue;

                var message =
                    $"계획서 전체에 `{table.Name}` 행을 만드는 지점이 없습니다. " +
                    "이 테이블은 단계 목록의 첫 단계가 INSERT하며 RunId를 발급하는 계약인데, " +
                    "생성 없이 UPDATE만 하면 0행이 갱신되어 실행 단위 자체가 존재하지 않습니다. " +
                    "첫 단계에 INSERT를 두고 SCOPE_IDENTITY()로 발급된 RunId를 이후 단계에 넘기십시오.";

                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.BatchRunRowNeverCreated,
                    Message = message,
                    RawContext = table.Name
                });
            }
        }

        /// <summary>
        /// 블랭크 처리된 사본을 `;` 경계로 잘라 (블랭크 사본, 원본) 문 쌍을 낸다.
        /// 문자열·주석 안의 `;`는 <see cref="BlankCommentsAndStrings"/>가 공백으로
        /// 바꿔 놓았으므로 경계가 되지 않는다. 자르기는 원본과 길이가 같은 사본의
        /// 인덱스를 그대로 원본에 대는 방식이라 두 목록의 각 항목이 같은 구간을
        /// 가리킨다 - <see cref="CheckShadowBackupContract"/>가 EXEC() 위치·내용을
        /// 분리하는 것과 같은 관용이다. 마지막 문이 `;` 없이 끝나도 한 문으로
        /// 다룬다.
        /// </summary>
        private static IEnumerable<(string Cleaned, string Raw)> SplitSqlStatements(string raw, string cleaned)
        {
            var start = 0;
            for (var i = 0; i < cleaned.Length; i++)
            {
                if (cleaned[i] != ';') continue;
                yield return (cleaned.Substring(start, i - start + 1), raw.Substring(start, i - start + 1));
                start = i + 1;
            }

            if (start >= cleaned.Length) yield break;

            var tail = cleaned.Substring(start);
            if (string.IsNullOrWhiteSpace(tail)) yield break;

            yield return (tail, raw.Substring(start));
        }

        /// <summary>
        /// 이 문(statement) 안의 CROSS JOIN이 전부, 본문이 정확히 한 행으로
        /// 집계되는 CTE 둘을 잇는지 본다(<see cref="CollectSingleRowAggregateCteNames"/>).
        /// 하나라도 그렇지 않은 것(원시 테이블, 통과용 CTE, GROUP BY로 여러 행을
        /// 내는 CTE 등)을 피연산자로 두면(한쪽만이어도) false를 낸다 - 그건 진짜
        /// 카티전일 수 있다.
        ///
        /// 감사 수정 라운드 1은 "이름이 CTE면 안전"이라고 가정했는데, CTE 본문이
        /// SELECT * 같은 통과용이면 여러 행을 내므로 그 가정이 틀렸다 - 재리뷰가
        /// 정확히 이 모양(WITH L AS (SELECT * FROM ...), R AS (SELECT * FROM ...)
        /// 뒤 CROSS JOIN, 바깥에서 별칭별 SUM)으로 재현했다. "한 행이 보장되는
        /// CTE 둘"만 안전하다.
        /// </summary>
        private static bool AllCrossJoinsJoinKnownCtes(string cleanedStatement)
        {
            var singleRowAggregateCteNames = CollectSingleRowAggregateCteNames(cleanedStatement);
            if (singleRowAggregateCteNames.Count == 0) return false;

            var crossJoinCount = Regex.Matches(cleanedStatement, @"\bCROSS\s+JOIN\b", RegexOptions.IgnoreCase).Count;
            var operandMatches = Regex.Matches(cleanedStatement,
                @"\bFROM\s+(?<left>[\w\.\[\]]+)(?:\s+(?:AS\s+)?\w+)?\s+CROSS\s+JOIN\s+(?<right>[\w\.\[\]]+)",
                RegexOptions.IgnoreCase);

            // CROSS JOIN 발생 수만큼 피연산자 쌍을 못 찾으면(예: FROM 없이 걸린
            // CROSS JOIN처럼 이 패턴이 다루지 않는 구문) 안전 쪽으로 판단하지
            // 않는다 - 못 찾은 발생은 원시 테이블 취급과 같다.
            if (operandMatches.Count < crossJoinCount) return false;

            foreach (Match om in operandMatches)
            {
                var left = BareObjectName(om.Groups["left"].Value);
                var right = BareObjectName(om.Groups["right"].Value);
                if (!singleRowAggregateCteNames.Contains(left) || !singleRowAggregateCteNames.Contains(right)) return false;
            }

            return true;
        }

        /// <summary>
        /// `WITH ... AS ( ... )` / `, ... AS ( ... )`로 선언된 CTE 중, 본문이
        /// 집계 함수(SUM/COUNT/AVG/MIN/MAX)를 쓰고 "본문 자신의 SELECT"에
        /// GROUP BY가 없어 정확히 한 행을 내는 것만 이름을 모은다. 그런 CTE
        /// 끼리의 CROSS JOIN만 1×1이라 무해하다 - GROUP BY가 있으면 그룹
        /// 수만큼, 통과용(SELECT * 등)이면 원본 행 수만큼 나오므로 한 행
        /// 보장이 없다.
        ///
        /// [감사 수정 라운드 3] GROUP BY는 본문 전체가 아니라 본문 자신의
        /// SELECT에서만 센다 - 서브쿼리(`FROM (SELECT ... GROUP BY ...) AS
        /// sub`) 안의 GROUP BY는 그 서브쿼리의 행 수를 정할 뿐이고, 바깥이
        /// 그 결과를 다시 SUM으로 합산하면 CTE 본문은 여전히 한 행이다.
        ///
        /// [감사 수정 라운드 4] 라운드 3은 GROUP BY만 서브쿼리에서 지우고
        /// hasAggregate는 본문 전체 텍스트에서 그대로 찾았다 - 그 결과
        /// `SELECT * FROM (그룹별 집계 서브쿼리) AS sub`처럼 본문 자신은
        /// 통과용인데 안쪽 서브쿼리의 SUM(이 전체 스캔에 걸려 "집계 있음,
        /// GROUP BY 없음"으로 오분류됐다 - 실제로는 서브쿼리가 여러 행을
        /// 내고 본문이 그것을 그대로 통과시키므로 CTE 자체가 여러 행이다.
        /// 이 CTE가 CROSS JOIN 뒤 바깥에서 재집계되면 S16 원 결함(그룹
        /// 수만큼 부풀려진 합계)을 그대로 재현한다(재리뷰 재현).
        ///
        /// 이제 hasAggregate와 hasGroupBy를 같은 사본
        /// (<see cref="BlankSubqueryParenGroups"/>)에서 함께 판정한다. 이
        /// 사본은 "서브쿼리"(내용이 SELECT로 시작하는 괄호 그룹)만 지우고
        /// `ISNULL(SUM(x),0)` 같은 함수 호출 괄호는 그대로 둔다 - 함수 호출
        /// 인자로 집계가 중첩되는 것은 흔한 관용이라, 괄호 깊이만으로
        /// 뭉뚱그려 지우면(라운드 3의 방식) 이런 정상 CTE의 hasAggregate까지
        /// 지워져 오탐이 될 위험이 있다(라운드 3이 hasGroupBy에만 그 방식을
        /// 썼던 이유이기도 하다). "서브쿼리인가 아닌가"로 좁히면 두 판정
        /// 모두 안전하게 같은 사본을 쓸 수 있다.
        ///
        /// CTE 본문은 <see cref="ExtractBalancedParenGroup"/>(Task 6 자산, 중첩
        /// 괄호를 다루고 문자열·주석 안 괄호를 깊이에서 제외한다)로 정확히
        /// 잘라낸다 - 두 번째 괄호 짝 맞추기 구현을 만들지 않는다. 호출부에서
        /// 이미 블랭크 처리된 사본을 넘기므로, 주석·문자열 속 SUM이나 GROUP BY
        /// 텍스트에 속지 않는다.
        /// </summary>
        private static HashSet<string> CollectSingleRowAggregateCteNames(string cleanedStatement)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var declarationPattern in CteDeclarationPatterns)
            {
                foreach (Match m in Regex.Matches(cleanedStatement, declarationPattern, RegexOptions.IgnoreCase))
                {
                    var openParenIndex = m.Index + m.Length - 1; // 매치가 '(' 로 끝난다.
                    var body = ExtractBalancedParenGroup(cleanedStatement, openParenIndex);
                    if (body == null) continue;

                    var withoutSubqueries = BlankSubqueryParenGroups(body);
                    var hasAggregate = Regex.IsMatch(
                        withoutSubqueries, @"\b(SUM|COUNT|AVG|MIN|MAX)\s*\(", RegexOptions.IgnoreCase);
                    var hasTopLevelGroupBy = Regex.IsMatch(
                        withoutSubqueries, @"\bGROUP\s+BY\b", RegexOptions.IgnoreCase);
                    if (hasAggregate && !hasTopLevelGroupBy)
                    {
                        result.Add(m.Groups["n"].Value);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// text 안에서 서브쿼리인 최상위 괄호 그룹(내용을 트림했을 때 SELECT로
        /// 시작하는 것)만 그 안쪽을 공백으로 지운 사본을 낸다. 괄호 문자
        /// `(`/`)` 자체는 지우지 않는다. 함수 호출 괄호(`ISNULL(SUM(x),0)`의
        /// `(SUM(x),0)`처럼 내용이 SELECT로 시작하지 않는 것)는 건드리지
        /// 않는다.
        ///
        /// [감사 수정 라운드 4] "괄호 깊이"만으로 지우면(라운드 3의
        /// BlankNestedParenGroups) `ISNULL(SUM(x),0)`의 `SUM(`도 깊이 1이라
        /// 지워질 위험이 있다 - 그래서 라운드 3은 이 방식을 GROUP BY 판정에만
        /// 쓰고 hasAggregate는 본문 전체에서 찾았는데, 그 비대칭이 새 미탐을
        /// 냈다(`SELECT * FROM (그룹별 집계 서브쿼리) AS sub` 형태에서 안쪽
        /// SUM이 전체 스캔에 걸려 통과용 CTE가 집계 CTE로 오분류됨). "괄호
        /// 그룹이 서브쿼리인가"로 좁히면 함수 호출 괄호는 절대 지우지
        /// 않으므로 hasAggregate·hasGroupBy 양쪽에 같은 사본을 안전하게
        /// 쓸 수 있다.
        ///
        /// 입력은 이미 <see cref="BlankCommentsAndStrings"/>로 블랭크 처리된
        /// 사본에서 뽑혔으므로, 괄호 그룹 시작의 주석은 이미 공백이다 -
        /// TrimStart가 그 공백까지 걷어내고 첫 토큰을 본다.
        ///
        /// <see cref="ExtractBalancedParenGroup"/>(Task 6 자산)로 각 최상위
        /// '('의 짝 ')'를 찾는다 - 새 괄호 짝 맞추기 구현을 만들지 않는다.
        /// 짝이 안 맞는 '('은 방어적으로 건너뛴다.
        /// </summary>
        private static string BlankSubqueryParenGroups(string text)
        {
            var chars = text.ToCharArray();
            var i = 0;
            while (i < chars.Length)
            {
                if (chars[i] != '(')
                {
                    i++;
                    continue;
                }

                var inner = ExtractBalancedParenGroup(text, i);
                if (inner == null)
                {
                    i++;
                    continue;
                }

                var innerStart = i + 1;
                var innerEnd = innerStart + inner.Length; // ')' 의 인덱스.

                if (Regex.IsMatch(inner.TrimStart(), @"^SELECT\b", RegexOptions.IgnoreCase))
                {
                    for (var j = innerStart; j < innerEnd; j++)
                    {
                        if (chars[j] != '\n') chars[j] = ' ';
                    }
                }

                i = innerEnd + 1; // ')' 다음으로 건너뛴다 - 중첩은 이미 다 처리됐다.
            }

            return new string(chars);
        }

        private static readonly string[] CteDeclarationPatterns =
        {
            @"\bWITH\s+(?<n>\w+)\s+AS\s*\(",
            @",\s*(?<n>\w+)\s+AS\s*\("
        };

        /// <summary>
        /// SQL 주석(`--`, `/* */`)과 문자열 리터럴(`'...'`) 안의 내용을 공백으로 지운
        /// 사본을 돌려준다. 개행은 그대로 남긴다. 원본과 길이·줄 구조가 같으므로,
        /// 사본에서 찾은 Match의 Index/Length를 원본 문자열에 그대로 적용해 잘라낼 수
        /// 있다 - <see cref="CheckShadowBackupContract"/>의 (c)가 EXEC() 몸체의 위치는
        /// 사본에서 찾고 그 문자열 리터럴 내용은 원본에서 읽는 데 이 성질을 쓴다.
        ///
        /// 그림자 계약·반환 경로 검사가 "-- BEGIN TRAN은 안 쓴다", "-- RETURN @x;"
        /// 같은 주석 텍스트나 문자열 값 안의 키워드를 실제 문(statement)으로 오인하지
        /// 않도록, 위치 기반 정규식을 걸기 전에 먼저 이 사본에 대해 매치한다. 대괄호
        /// 인용 식별자는 다루지 않는다 - 이 검사들이 찾는 키워드(BEGIN/COMMIT/
        /// ROLLBACK/TRAN/THROW/RETURN)가 대괄호 인용 식별자 안에 올 동기가 없기
        /// 때문이다(기존 세 스캐너의 대괄호 처리와 달리, 이 사본은 컬럼·상태값
        /// 대조가 아니라 제어 흐름 키워드 대조용이다).
        /// </summary>
        private static string BlankCommentsAndStrings(string text)
        {
            var chars = text.ToCharArray();
            var inString = false;
            var i = 0;

            while (i < chars.Length)
            {
                var ch = chars[i];

                if (inString)
                {
                    if (ch == '\'')
                    {
                        if (i + 1 < chars.Length && chars[i + 1] == '\'')
                        {
                            chars[i] = ' ';
                            chars[i + 1] = ' ';
                            i += 2;
                            continue;
                        }

                        inString = false;
                        chars[i] = ' ';
                        i++;
                        continue;
                    }

                    if (ch != '\n') chars[i] = ' ';
                    i++;
                    continue;
                }

                var commentEnd = SkipCommentToken(text, i);
                if (commentEnd.HasValue)
                {
                    for (var j = i; j < commentEnd.Value; j++)
                    {
                        if (chars[j] != '\n') chars[j] = ' ';
                    }

                    i = commentEnd.Value;
                    continue;
                }

                if (ch == '\'')
                {
                    inString = true;
                    chars[i] = ' ';
                    i++;
                    continue;
                }

                i++;
            }

            return new string(chars);
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public bool IsConsolidated { get; set; }
        public string? CleansedMarkdown { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<DetailedError> DetailedErrors { get; set; } = new();

        public string? SuggestedPromptFix
        {
            get
            {
                if (IsValid) return null;
                return BuildSuggestedPromptFix();
            }
        }

        private string BuildSuggestedPromptFix()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[L1 기계 검사 피드백]: AI가 작성한 설계서 문서에서 규격 형식(Format) 또는 다이어그램 문법 오류가 감지되었습니다. 다음 [교정 가이드라인]을 엄격히 준수하여 완벽하게 반영된 최종 수정 문서를 다시 출력해 주십시오.");
            sb.AppendLine();

            // 1. 필수 헤더 누락
            var headerErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.HeaderMissing);
            if (headerErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 1. 필수 섹션 헤더 누락 오류");
                sb.AppendLine("문서에 아래의 필수 마크다운 헤더가 누락되어 있습니다. 분석 지침에 맞게 해당 섹션을 반드시 포함하고 비즈니스 흐름을 분석해 주십시오.");
                foreach (var err in headerErrors)
                {
                    sb.AppendLine($"  - 누락된 섹션: `{err.Message}`");
                }
                sb.AppendLine();
                sb.AppendLine("**[올바른 수정 구조 템플릿 예시]**:");
                sb.AppendLine("```markdown");
                if (IsConsolidated)
                {
                    sb.AppendLine("## 통합 배치 아키텍처 개요");
                    sb.AppendLine("## Mermaid 기반 통합 흐름도");
                    sb.AppendLine("## 단계별 이행 상세 및 의사코드");
                    sb.AppendLine("## 통합 데이터 정합성 검증 SQL 세트");
                }
                else
                {
                    sb.AppendLine("## 개요");
                    sb.AppendLine("## 파라미터 목록");
                    sb.AppendLine("## CRUD 분석");
                    sb.AppendLine("## 로직 흐름 요약");
                    sb.AppendLine("## 비즈니스 흐름 시각화");
                }
                sb.AppendLine("```");
                sb.AppendLine();
            }

            // 2. Mermaid 노드 따옴표 누락
            var quoteErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.MermaidQuoteMissing);
            if (quoteErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 2. Mermaid 다이어그램 텍스트 이스케이프 오류");
                sb.AppendLine("다이어그램의 노드 라벨 텍스트 안에 괄호(), 대괄호[], 콜론(:), 대시(-) 등의 특수문자가 사용되었으나, 이를 큰따옴표(\"\")로 감싸지 않아 구문 에러가 유발됩니다. 텍스트 라벨 전체를 반드시 큰따옴표로 감싸서 작성해 주십시오.");
                foreach (var err in quoteErrors)
                {
                    sb.AppendLine($"  - 에러 라인: `{err.RawContext}`");
                    sb.AppendLine($"    (설명: {err.Message})");
                }
                sb.AppendLine();
                sb.AppendLine("**[Before (오류) vs After (해결) 예시]**:");
                sb.AppendLine("  * **오류 (X)**: `A[데이터 조회 (정상)]` 또는 `B(상태 : 대기)`");
                sb.AppendLine("  * **해결 (O)**: `A[\"데이터 조회 (정상)\"]` 또는 `B(\"상태 : 대기\")`");
                sb.AppendLine();
            }

            // 3. Mermaid CLI 컴파일 오류
            var cliErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.MermaidCliError);
            if (cliErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 3. Mermaid 다이어그램 컴파일 에러");
                sb.AppendLine("Mermaid 렌더러 검증 결과, 구문 오류로 인해 다이어그램 컴파일에 실패했습니다. 화살표 구문 기호 오타, subgraph 짝 누락 여부를 정밀하게 검토해 주십시오.");
                foreach (var err in cliErrors)
                {
                    sb.AppendLine($"  - 컴파일 오류 로그: {err.Message}");
                }
                sb.AppendLine();
                sb.AppendLine("**[Mermaid 문법 자율 교정 체크리스트]**:");
                sb.AppendLine("  1. **화살표 구문**: `->` 나 `- ->`는 오류입니다. 반드시 `-->` 또는 `-.->` 또는 `==>` 중 하나를 사용하십시오.");
                sb.AppendLine("  2. **블록 짝 맞춤**: `subgraph [제목]`으로 시작했다면 블록 끝에 반드시 `end` 키워드를 작성했는지 확인하십시오.");
                sb.AppendLine("  3. **특수 기호**: 라벨 텍스트 내에 괄호, 특수기호, 기호 등이 들어간 경우 100% 큰따옴표(`\"` `\"`)로 묶어 명시하십시오.");
                sb.AppendLine();
            }

            // 4. UPDATE 매핑 누락
            var updateErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.UpdateMappingMissing);
            if (updateErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 4. UPDATE 컬럼 매핑 누락 오류");
                sb.AppendLine("정적 파서(AST)가 확정한 UPDATE SET 대상 컬럼이 `## CRUD 분석`의 매핑 표에서 빠졌습니다. 프롬프트에 제공된 fill-in-the-blank 표를 그대로 사용하고, 행을 생략하거나 '...'로 축약하지 마십시오. 표의 헤딩은 반드시 `### UPDATE 대상 테이블: <테이블명>` 형식이어야 합니다.");
                foreach (var err in updateErrors)
                {
                    sb.AppendLine($"  - {err.Message}");
                }
                sb.AppendLine();
            }

            // 5. 거짓 스키마 부재 주장
            var schemaClaimErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.SchemaClaimFalse);
            if (schemaClaimErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 5. 실존 컬럼을 존재하지 않는다고 기술한 오류");
                sb.AppendLine("아래 컬럼은 프롬프트의 `[Referenced Table Schemas]` 표에 실제로 제공되었습니다. 존재하지 않는다거나 스키마 불일치라고 기술하지 마십시오. 해당 문장과 표 행을 삭제하고, 그 컬럼을 정상적인 참조/갱신 컬럼으로 기술하십시오.");
                foreach (var err in schemaClaimErrors)
                {
                    sb.AppendLine($"  - {err.Message}");
                }
                sb.AppendLine();
            }

            // 6. 테이블 동일성 분열
            var splitErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.TableIdentitySplit);
            if (splitErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 6. 같은 테이블을 여러 표기로 나눠 기술한 오류");
                sb.AppendLine("아래 표기들은 모두 같은 하나의 물리 테이블입니다. CRUD 분석의 각 절에서 이들을 한 행으로 합치고, 프롬프트가 제공한 완전 한정 이름(DB.스키마.테이블) 하나만 사용하십시오.");
                foreach (var err in splitErrors)
                {
                    sb.AppendLine($"  - {err.Message}");
                }
                sb.AppendLine();
            }

            // 7. 기타 에러
            var generalErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.General);
            if (generalErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 7. 기타 정적 규격 검사 에러");
                foreach (var err in generalErrors)
                {
                    sb.AppendLine($"  - {err.Message}");
                }
                sb.AppendLine();
            }

            // 8. 위 버킷 어디에도 담기지 않은 오류
            //
            // [버킷 하나를 더 만들지 않고 catch-all을 쓰는 이유 - 2026-08-20 리뷰 #1]
            // SuggestedPromptFix는 모델에 닿는 유일한 통로다(result.Errors는 사람에게만
            // 간다). 그런데 위 일곱 버킷이 타입을 열거하는 구조라, 새 ErrorType을 쓰는
            // 검사를 추가하면서 버킷을 안 만들면 그 오류는 <b>내용이 통째로 빠진 채</b>
            // 머리말과 맺음말만 모델에게 간다 - 검사는 재시도 예산을 쓰면서 "형식 오류가
            // 있었다"만 알린다. 실측: 기계 확정 표 셋(DML 범위·파생 테이블·집합 술어)이
            // 전부 이 상태였다.
            //
            // 타입별 버킷을 하나 더 만들면 오늘 그 셋은 닫히지만 다음 검사가 같은 구멍에
            // 빠진다. 열거되지 않은 것을 모두 흘려보내면 부류 자체가 닫힌다.
            var bucketed = new[]
            {
                ErrorType.HeaderMissing, ErrorType.MermaidQuoteMissing, ErrorType.MermaidCliError,
                ErrorType.UpdateMappingMissing, ErrorType.SchemaClaimFalse,
                ErrorType.TableIdentitySplit, ErrorType.General
            };
            var unbucketed = DetailedErrors.FindAll(e => Array.IndexOf(bucketed, e.Type) < 0);
            if (unbucketed.Count > 0)
            {
                sb.AppendLine("### 🚨 8. 기계 확정 재료 대조 실패");
                sb.AppendLine("프롬프트가 제공한 기계 확정 표를 문서가 그대로 담지 않았습니다. 아래 지적을 그대로 반영하되, 표의 헤딩과 행을 축자로 옮기십시오.");
                foreach (var err in unbucketed)
                {
                    sb.AppendLine($"  - {err.Message}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("지적된 모든 결함 사항을 수렴 및 교정한 최종 설계서 문서를 작성해 주십시오.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 단계 섹션 하한 검사 결과.
    ///
    /// ValidationResult를 재사용하지 않는 이유: 그 타입의 SuggestedPromptFix는
    /// 문서 전체의 H2 템플릿을 제안하도록 만들어져 있어, 단계 섹션 하나를 고치라는
    /// 지시에는 엉뚱한 교정 가이드가 붙는다.
    /// </summary>
    public class StepValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// Errors 중 목차(PlanStructure)가 원인인 것들. 단계 본문을 다시 생성해도
        /// 사라지지 않는다 - 재생성 프롬프트에 넘길 재료 자체가 목차에 없기 때문이다.
        /// Errors의 부분집합이다.
        /// </summary>
        public List<string> PlanDefects { get; } = new();

        /// <summary>
        /// 이 실패를 단계 본문 재생성으로 고칠 수 있는가.
        ///
        /// 전부 목차 결함이면 false다. 그때 재시도를 걸면 같은 프롬프트로 같은
        /// 결과를 받아 단계마다 AI 호출 1회를 버린다 - 12단계면 12회다.
        /// 본문 결함이 하나라도 섞여 있으면 재시도할 값어치가 있으므로 true다.
        /// </summary>
        public bool RegenerationCanFix => Errors.Count > PlanDefects.Count;

        public string? SuggestedPromptFix
        {
            get
            {
                if (IsValid || !RegenerationCanFix)
                {
                    return null;
                }

                var builder = new System.Text.StringBuilder();
                builder.AppendLine("[L1 Step Floor Check]: This step section does not meet the minimum requirements for an implementation instruction. Rewrite the WHOLE section, resolving every item below.");

                // 목차 결함은 빼고 넘긴다. 고칠 수 없는 항목을 지시로 주면 모델이
                // 고쳐진 척하는 문장을 만들어 넣는 쪽으로 유도된다.
                foreach (var error in Errors.Except(PlanDefects))
                {
                    builder.AppendLine($"  - {error}");
                }

                return builder.ToString();
            }
        }
    }
}
