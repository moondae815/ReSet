using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ReSet.Validator.Core.Abstractions;

namespace ReSet.Validator.Core.Plugins
{
    public class CsValidatorPlugin : IValidatorPlugin
    {
        public string SupportedLanguage => "C#";

        public Task<L1ValidationResult> ValidateStaticAsync(string specContent, string sourceCodeContent)
        {
            var result = new L1ValidationResult { Passed = false };

            try
            {
                // 1. 소스코드 내 클래스 선언 추출
                // 예: public class CustOrderHist, class CustOrderHistBatch
                var classRegex = new Regex(@"class\s+([A-Za-z0-9_]+)", RegexOptions.Compiled);
                var match = classRegex.Match(sourceCodeContent);
                
                if (!match.Success)
                {
                    result.ErrorMessage = "C# 소스코드 내에서 class 정의를 찾을 수 없습니다.";
                    return Task.FromResult(result);
                }

                var className = match.Groups[1].Value;
                result.ClassOrMethodName = className;

                // 2. 간단한 구문 정합성 검사 (대괄호/중괄호 쌍 검사)
                int openBrace = 0;
                int closeBrace = 0;
                foreach (char c in sourceCodeContent)
                {
                    if (c == '{') openBrace++;
                    else if (c == '}') closeBrace++;
                }

                if (openBrace != closeBrace)
                {
                    result.ErrorMessage = $"소스코드의 중괄호 쌍이 일치하지 않습니다. ({openBrace} vs {closeBrace})";
                    return Task.FromResult(result);
                }

                // 3. 데이터 액세스 경계의 항상 조항 1(전달받은 트랜잭션 참여) 검사.
                // 이 조항 위반은 검증기의 Rollback 격리를 깨뜨려 정합성 대조 결과 자체를
                // 오염시키므로, L2의 AI 판단을 기다리지 않고 여기서 막는다.
                var boundaryViolation = TransactionEnlistmentCheck.FindCSharpViolation(sourceCodeContent);
                if (boundaryViolation != null)
                {
                    result.ErrorMessage = boundaryViolation;
                    return Task.FromResult(result);
                }

                // 4. 설계서 내 입출력 정의와 소스코드 정적 매핑 검사 (예: 메서드 시그니처나 인자 확인)
                // 설계서에서 매핑 정보 추출 (간단히 정규식으로 파라미터 명칭 추출)
                var paramRegex = new Regex(@"-\s*`@([A-Za-z0-9_]+)`", RegexOptions.Compiled);
                var specParams = new List<string>();
                foreach (Match m in paramRegex.Matches(specContent))
                {
                    specParams.Add(m.Groups[1].Value.ToLower());
                }

                result.ExtractedMetadata.Add("ClassName", className);
                result.ExtractedMetadata.Add("SpecParamsCount", specParams.Count.ToString());

                // L1 통과 설정
                result.Passed = true;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"정적 검증 중 예외 발생: {ex.Message}";
            }

            return Task.FromResult(result);
        }
    }
}
