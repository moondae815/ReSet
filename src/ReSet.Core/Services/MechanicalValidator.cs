using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        public ValidationResult Validate(string markdown)
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
        public StepValidationResult ValidateBatchStep(string? stepMarkdown, BatchStepPlan step)
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

            if (!step.ErrorCodes.Any(code => !string.IsNullOrWhiteSpace(code)))
            {
                result.PlanDefects.Add(
                    $"{step.Code}의 목차 ErrorCodes가 비어 있어 원본 오류코드 대조를 실행할 수 없습니다.");
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
        private static bool ContainsToken(string haystack, string token)
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
            string[] forbiddenShortcuts = new[] { "이하 생략", "(생략)", "위와 동일", "기타 등등", "etc.", "TS[]" };
            foreach (var forbidden in forbiddenShortcuts)
            {
                if (markdown.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
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

            // 4. 기타 에러
            var generalErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.General);
            if (generalErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 4. 기타 정적 규격 검사 에러");
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
