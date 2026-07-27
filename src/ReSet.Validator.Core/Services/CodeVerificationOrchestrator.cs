using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Services;
using ReSet.Validator.Core.Abstractions;
using ReSet.Validator.Core.Models;
using ReSet.Validator.Core.Plugins;
using Serilog;
using ValidationResult = ReSet.Validator.Core.Models.ValidationResult;

namespace ReSet.Validator.Core.Services
{
    public class CodeVerificationOrchestrator
    {
        private readonly ValidatorConfig _config;
        private readonly FileMappingService _mappingService;
        private readonly ValidatorAiService _aiService;
        private readonly List<IValidatorPlugin> _plugins;
        private readonly IValidationUserInterface? _ui;

        public CodeVerificationOrchestrator(
            ValidatorConfig config,
            IAiClient aiClient,
            IValidationUserInterface? ui = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _mappingService = new FileMappingService();
            _aiService = new ValidatorAiService(aiClient);
            _ui = ui;

            // 기본 플러그인 로드
            _plugins = new List<IValidatorPlugin>
            {
                new CsValidatorPlugin(),
                new JavaValidatorPlugin()
            };
        }

        public async Task<List<ValidationResult>> RunVerificationAsync(bool isBatchMode, CancellationToken cancellationToken = default)
        {
            Log.Information("[코드검증] 검증 오케스트레이션 시작 - BatchMode: {IsBatchMode}, SpecDir: {SpecDir}, CodeDir: {CodeDir}",
                isBatchMode, _config.SpecDirectory, _config.SourceCodeDirectory);

            _ui?.ShowInfo("1. 설계서 및 소스코드 매핑 구성 중...");
            var mappedPairs = _mappingService.ResolveMappings(_config);

            if (mappedPairs.Count == 0)
            {
                Log.Warning("[코드검증] 검증 매핑 대상 없음 - 경로를 확인하십시오.");
                _ui?.ShowWarning("검증 매핑 대상(Spec & Code 파일 쌍)을 찾을 수 없습니다. 경로를 확인해 주세요.");
                return mappedPairs;
            }

            Log.Information("[코드검증] 총 {Count}개의 검증 대상 매핑 완료", mappedPairs.Count);
            _ui?.ShowInfo($"총 {mappedPairs.Count}개의 검증 대상이 매핑되었습니다.");

            foreach (var pair in mappedPairs)
            {
                if (cancellationToken.IsCancellationRequested) break;

                _ui?.ShowInfo($"\n--------------------------------------------");
                _ui?.ShowInfo($"🔍 검증 대상 분석 시작: {pair.MappedName}");
                _ui?.ShowInfo($" - 설계서: {Path.GetFileName(pair.SpecFilePath)}");
                _ui?.ShowInfo($" - 소스코드: {Path.GetFileName(pair.SourceCodePath)}");
                Log.Information("[코드검증] 검증 대상 처리 시작 - Name: {MappedName}, Spec: {SpecFile}, Code: {CodeFile}",
                    pair.MappedName, pair.SpecFilePath, pair.SourceCodePath);

                string specContent = await File.ReadAllTextAsync(pair.SpecFilePath, cancellationToken);
                string codeContent = "";
                string language = _config.TargetLanguage;

                if (Directory.Exists(pair.SourceCodePath))
                {
                    var sb = new System.Text.StringBuilder();
                    var files = Directory.GetFiles(pair.SourceCodePath, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (files.Count > 0)
                    {
                        var firstExt = Path.GetExtension(files.First()).ToLower();
                        if (string.Equals(language, "Auto", StringComparison.OrdinalIgnoreCase))
                        {
                            language = firstExt == ".cs" ? "C#" : "Java";
                        }

                        foreach (var file in files)
                        {
                            sb.AppendLine($"// File: {Path.GetFileName(file)}");
                            sb.AppendLine(await File.ReadAllTextAsync(file, cancellationToken));
                            sb.AppendLine();
                        }
                    }
                    codeContent = sb.ToString();
                }
                else
                {
                    codeContent = await File.ReadAllTextAsync(pair.SourceCodePath, cancellationToken);
                    var extension = Path.GetExtension(pair.SourceCodePath).ToLower();
                    if (string.Equals(language, "Auto", StringComparison.OrdinalIgnoreCase))
                    {
                        language = extension == ".cs" ? "C#" : "Java";
                    }
                }

                // --- Level 1: 정적 검증 ---
                var plugin = _plugins.FirstOrDefault(p => p.SupportedLanguage.Equals(language, StringComparison.OrdinalIgnoreCase));

                if (plugin != null)
                {
                    Log.Debug("[코드검증] L1 정적 검증 시작 - Name: {MappedName}, Language: {Language}", pair.MappedName, language);
                    var l1Result = await plugin.ValidateStaticAsync(specContent, codeContent);
                    pair.L1Passed = l1Result.Passed;
                    pair.L1Message = l1Result.ErrorMessage;
                    Log.Debug("[코드검증] L1 정적 검증 완료 - Name: {MappedName}, Passed: {Passed}, Message: {Message}",
                        pair.MappedName, l1Result.Passed, l1Result.ErrorMessage);
                    _ui?.ShowL1Result(pair.MappedName, l1Result);
                }
                else
                {
                    pair.L1Passed = false;
                    pair.L1Message = $"지원되지 않는 언어 또는 대상입니다: {language}";
                    Log.Warning("[코드검증] L1 정적 검증 플러그인 없음 - Name: {MappedName}, Language: {Language}", pair.MappedName, language);
                    _ui?.ShowWarning($"[L1 경고] {pair.MappedName} - 지원 플러그인 없음");
                }

                _ui?.ShowInfo(" - Level 2: AI 비즈니스 로직 일치성 분석 요청 중...");
                Log.Information("[코드검증] L2 AI 분석 시작 - Name: {MappedName}", pair.MappedName);
                var gapReport = await _aiService.VerifyCodeAsync(specContent, codeContent, language, null, cancellationToken);
                Log.Debug("[코드검증] L2 AI 분석 완료 - Name: {MappedName}, Status: {Status}", pair.MappedName, gapReport.OverallStatus);
                
                // L2 자체 교정 (Self-Correction) 시도 (선택)
                int attempt = 1;
                while (gapReport.OverallStatus != "MATCH" && (_config.MaxL2Attempts == -1 || attempt < _config.MaxL2Attempts))
                {
                    attempt++;
                    var attemptsTotalText = _config.MaxL2Attempts == -1 ? "무제한" : _config.MaxL2Attempts.ToString();
                    Log.Debug("[코드검증] L2 자체 교정 루프 - Name: {MappedName}, 시도: {Attempt}, 상태: {Status}",
                        pair.MappedName, attempt, gapReport.OverallStatus);
                    _ui?.ShowInfo($"   [L2 자체 교정 루프] AI 재검토 요청 중... (시도 {attempt}/{attemptsTotalText})");
                    
                    var feedback = $"- 종합 상태: {gapReport.OverallStatus}\n- 입력 파라미터 불일치: {gapReport.InputParametersGap}\n- 출력 데이터셋 불일치: {gapReport.OutputResultSetsGap}\n- 비즈니스 로직 불일치: {gapReport.BusinessLogicGap}\n- 예외 및 트랜잭션 불일치: {gapReport.ExceptionHandlingGap}\n- 수정 제안: {gapReport.Suggestions}";
                    
                    gapReport = await _aiService.VerifyCodeAsync(specContent, codeContent, language, feedback, cancellationToken);
                    Log.Debug("[코드검증] L2 자체 교정 후 상태 - Name: {MappedName}, 시도: {Attempt}, 상태: {Status}",
                        pair.MappedName, attempt, gapReport.OverallStatus);
                }

                pair.GapReport = gapReport;
                pair.L2Passed = gapReport.OverallStatus == "MATCH";
                Log.Information("[코드검증] L2 최종 판정 - Name: {MappedName}, Status: {Status}, L2Passed: {L2Passed}",
                    pair.MappedName, gapReport.OverallStatus, pair.L2Passed);
                _ui?.ShowL2Result(pair.MappedName, gapReport);

                // --- Level 3: 인간 최종 검토 ---
                if (!isBatchMode && _ui != null)
                {
                    var approved = await _ui.ConfirmValidationAsync(pair.MappedName, pair.SourceCodePath, gapReport);
                    pair.IsApproved = approved;
                    Log.Information("[코드검증] L3 인간 검토 결과 - Name: {MappedName}, Approved: {Approved}", pair.MappedName, approved);

                    if (!approved)
                    {
                        var feedback = await _ui.PromptFeedbackAsync(pair.MappedName);
                        pair.HumanFeedback = feedback;
                    }
                }
                else
                {
                    // 배치 모드일 때는 AI가 일치 판정을 내렸다면 자동 승인 처리
                    pair.IsApproved = pair.L2Passed;
                    Log.Information("[코드검증] L3 배치 자동 처리 - Name: {MappedName}, AutoApproved: {IsApproved}", pair.MappedName, pair.IsApproved);
                    _ui?.ShowInfo($" - [L3 자동 처리] 배치 모드로 인한 자동 승인 상태: {pair.IsApproved}");
                }
            }

            // 결과 최종 리포트 Export
            ExportReports(mappedPairs);

            _ui?.ShowSummary(mappedPairs);

            return mappedPairs;
        }

        private void ExportReports(List<ValidationResult> results)
        {
            Log.Information("[코드검증] 검증 리포트 내보내기 시작 - 총 {Count}개, OutputDir: {OutputDir}", results.Count, _config.OutputDirectory);
            try
            {
                if (!Directory.Exists(_config.OutputDirectory))
                {
                    Directory.CreateDirectory(_config.OutputDirectory);
                }

                // 1. 개별 Gap Report 마크다운 파일 저장
                foreach (var res in results)
                {
                    if (res.GapReport == null) continue;

                    var mdPath = Path.Combine(_config.OutputDirectory, $"{res.MappedName}_ValidationReport.md");
                    var content = $@"# 🔍 코드 일치성 검증 상세 보고서 - {res.MappedName}

- **설계서 경로**: `{res.SpecFilePath}`
- **소스코드 경로**: `{res.SourceCodePath}`
- **검증 일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

## 📊 종합 결과
- **정적 구조 검증 (L1)**: {(res.L1Passed ? "✅ PASS" : "❌ FAIL")} ({res.L1Message})
- **AI 의미론적 검증 (L2)**: {(res.L2Passed ? "✅ MATCH" : $"⚠️ {res.GapReport.OverallStatus}")}
- **개발자 승인 상태 (L3)**: {(res.IsApproved ? "✅ APPROVED" : "❌ REJECTED")}

{(string.IsNullOrEmpty(res.HumanFeedback) ? "" : $"### 💬 개발자 피드백\n> {res.HumanFeedback}\n")}

## 📝 항목별 로직 불일치(Gap) 상세
### 1. 입력 파라미터 매핑 Gap
{(string.IsNullOrEmpty(res.GapReport.InputParametersGap) ? "일치함 (차이점 없음)" : res.GapReport.InputParametersGap)}

### 2. 출력 데이터셋/DTO 필드 Gap
{(string.IsNullOrEmpty(res.GapReport.OutputResultSetsGap) ? "일치함 (차이점 없음)" : res.GapReport.OutputResultSetsGap)}

### 3. 핵심 비즈니스 로직 Gap
{(string.IsNullOrEmpty(res.GapReport.BusinessLogicGap) ? "일치함 (차이점 없음)" : res.GapReport.BusinessLogicGap)}

### 4. 예외 및 트랜잭션 처리 Gap
{(string.IsNullOrEmpty(res.GapReport.ExceptionHandlingGap) ? "일치함 (차이점 없음)" : res.GapReport.ExceptionHandlingGap)}

## 💡 수정 제안 사항 (Suggestions)
{res.GapReport.Suggestions}
";
                    File.WriteAllText(mdPath, content);
                    Log.Debug("[코드검증] 개별 검증 리포트 저장 - {ReportPath}", mdPath);
                }

                // 2. 종합 검증 요약 보고서 저장 (validation_summary.md)
                var summaryPath = Path.Combine(_config.OutputDirectory, "validation_summary.md");
                var summaryContent = $@"# 📋 코드 마일스톤 검증 요약 보고서

- **검증 대상 디렉토리**: `{_config.SourceCodeDirectory}`
- **총 검증 대상 수**: {results.Count} 개
- **일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

## 📈 검증 요약 통계
| 상태 | 개수 | 비율 |
| :--- | :---: | :---: |
| 최종 승인 (Approved) | {results.Count(r => r.IsApproved)} | {(results.Count > 0 ? (results.Count(r => r.IsApproved) * 100.0 / results.Count) : 0):F1}% |
| 불승인 및 보완 필요 (Rejected) | {results.Count(r => !r.IsApproved)} | {(results.Count > 0 ? (results.Count(r => !r.IsApproved) * 100.0 / results.Count) : 0):F1}% |

## 🔍 개별 파일 검증 상태
| 대상 이름 | L1 정적 검증 | L2 AI 일치여부 | L3 최종 승인 | 상세 보고서 링크 |
| :--- | :---: | :---: | :---: | :--- |
{string.Join("\n", results.Select(r => $"| {r.MappedName} | {(r.L1Passed ? "✅ PASS" : "❌ FAIL")} | {(r.L2Passed ? "✅ MATCH" : "⚠️ GAP")} | {(r.IsApproved ? "✅ APPROVED" : "❌ REJECTED")} | [{r.MappedName}_ValidationReport.md](./{r.MappedName}_ValidationReport.md) |"))}
";
                File.WriteAllText(summaryPath, summaryContent);
                Log.Information("[코드검증] 종합 검증 요약 리포트 저장 완료 - {SummaryPath}", summaryPath);
            }
            catch (Exception ex)
            {
                // Soft Fail 정책 준수: 파일 저장 중 에러가 나더라도 검증 프로세스 자체가 크래시되지 않음.
                Log.Error(ex, "[코드검증] 리포트 내보내기 중 예외 발생 (Soft Fail)");
                _ui?.ShowWarning($"보고서 내보내기 중 오류 발생: {ex.Message}");
            }
        }
    }
}
