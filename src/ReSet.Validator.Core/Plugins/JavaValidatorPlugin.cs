using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ReSet.Validator.Core.Abstractions;

namespace ReSet.Validator.Core.Plugins
{
    public class JavaValidatorPlugin : IValidatorPlugin
    {
        public string SupportedLanguage => "Java";

        public Task<L1ValidationResult> ValidateStaticAsync(string specContent, string sourceCodeContent)
        {
            var result = new L1ValidationResult { Passed = false };

            try
            {
                // 1. 소스코드 내 Java 클래스 또는 레코드 선언 추출
                // 예: public class CustOrderHistReader, class CustOrderHistBatch
                var classRegex = new Regex(@"(class|record|interface)\s+([A-Za-z0-9_]+)", RegexOptions.Compiled);
                var match = classRegex.Match(sourceCodeContent);
                
                if (!match.Success)
                {
                    result.ErrorMessage = "Java 소스코드 내에서 class, record, interface 정의를 찾을 수 없습니다.";
                    return Task.FromResult(result);
                }

                var className = match.Groups[2].Value;
                result.ClassOrMethodName = className;

                // 2. 간단한 중괄호 쌍 일치 여부 검사
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
                var boundaryViolation = TransactionEnlistmentCheck.FindJavaViolation(sourceCodeContent);
                if (boundaryViolation != null)
                {
                    result.ErrorMessage = boundaryViolation;
                    return Task.FromResult(result);
                }

                result.ExtractedMetadata.Add("ClassName", className);

                // L1 통과
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
