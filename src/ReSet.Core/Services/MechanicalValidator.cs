using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

                if (expectations != null)
                {
                    CheckUpdateMappings(cleansed, expectations, result);
                    CheckSchemaClaims(cleansed, expectations, result);
                    CheckTableIdentitySplit(cleansed, expectations, result);
                    CheckIdentifierNotationClaims(cleansed, expectations, result);
                    CheckSourceComments(cleansed, expectations, result);
                    CheckRoundingSemantics(cleansed, expectations, result);
                    CheckSessionOptions(cleansed, expectations, result);
                    CheckHeaderContractContradiction(cleansed, expectations, result);
                    CheckDmlScopeTable(cleansed, expectations, result);
                    CheckDerivedTableDefinitions(cleansed, expectations, result);
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
            IReadOnlyList<StepInterface>? stepInterfaces = null)
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

            foreach (Match declaration in Regex.Matches(
                stepMarkdown,
                // params는 [^)]*?가 아니라 .*?로 잡는다 - varchar(8)·decimal(18,2)처럼
                // 타입 선언 안에 ')'가 섞이면 [^)]*?는 그 첫 ')'에서 막혀 AS까지
                // 도달하지 못해 매치 자체가 실패한다(원본 계획서 정규식의 결함).
                // .*?는 Singleline과 함께 개행까지 넘나들며 첫 단독 AS까지 게으르게
                // 소비하므로 감싸는 괄호가 있든 없든, 타입 괄호가 있든 없든 안전하다.
                @"CREATE\s+PROC(?:EDURE)?\s+[^\s(]+\s*\(?(?<params>.*?)\bAS\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                foreach (Match parameter in Regex.Matches(
                    declaration.Groups["params"].Value, @"@\w+"))
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
            "없음", "아님", "않음"
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
        /// 작을 때 위험이 크다. 표 자체의 스키마(문장·라인·대상·술어·기준일·조인 키)
        /// 안에서는 라인 칸 말고 다른 칸이 순수 숫자로만 채워지지 않으므로(문장 칸은
        /// "UPDATE 1"처럼 접두어가 붙는다) 표 내부 충돌 위험은 낮게 남는다.
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

            foreach (var fact in expectations.DmlScopeFacts)
            {
                var lineToken = fact.Line.ToString();
                var present = rowLines.Any(
                    row => row.Split('|').Any(cell => cell.Trim() == lineToken));
                if (present) continue;

                var message =
                    $"DML 범위 표에 원본 DDL 라인 {fact.Line}의 {fact.Operation} 행이 없습니다. "
                    + "표는 기계가 확정한 것이므로 행을 생략하거나 합칠 수 없습니다.";
                result.Errors.Add(message);
                result.DetailedErrors.Add(new DetailedError
                {
                    Type = ErrorType.DmlScopeTableMissing,
                    Message = message,
                    RawContext = $"{fact.Operation} @ line {fact.Line}"
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
        /// `MarkdownSectionLocator`는 다른 소비자(계획서 분할)가 있으므로 그 클래스의
        /// 기존 동작은 바꾸지 않는다 - 폴백은 이 클래스 안에만 둔다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateCrudSection(IReadOnlyList<string> lines)
        {
            var exact = MarkdownSectionLocator.LocateSection(lines, "## CRUD 분석", "## ");
            if (exact.HeaderIndex >= 0) return exact;

            var headerIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0,
                line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal)
                     && line.Contains("CRUD 분석", StringComparison.OrdinalIgnoreCase));

            if (headerIndex < 0) return (-1, -1);

            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headerIndex + 1,
                line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
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
