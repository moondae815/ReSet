using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using ReSet.Core.Services;
using ReSet.Validator.Core.Models;
using Serilog;

namespace ReSet.Validator.Core.Services
{
    public class ValidatorAiService
    {
        private readonly IAiClient _aiClient;

        public ValidatorAiService(IAiClient aiClient)
        {
            _aiClient = aiClient ?? throw new ArgumentNullException(nameof(aiClient));
        }

        public async Task<GapReport> VerifyCodeAsync(string specContent, string sourceCodeContent, string targetLanguage, string? previousFeedback = null, CancellationToken cancellationToken = default)
        {
            var systemPrompt = @"당신은 데이터베이스 Stored Procedure 역공학 명세서(*_Spec.md)와 이를 마이그레이션하여 구현한 프로그램 코드(C# 또는 Java)를 일대일 비교하여 기능적으로 완벽히 동일하게 구현되었는지 정밀 검증하는 전문 QA 에이전트입니다.

비교 검증 시 다음 항목들에 주목하십시오:
1. 입력 파라미터 매핑: 설계서에 명시된 파라미터들이 코드의 입력 인자나 객체 필드로 정확히 전달되는가?
2. 출력 데이터셋/반환값: 쿼리 조회 결과나 DTO 반환 필드가 누락 없이 매핑되는가?
3. 핵심 비즈니스 로직: 조건문 분기, 연산 로직, 주요 쿼리 실행 등이 설계서와 의미론적으로 완벽히 동일한가?
4. 예외 처리 및 트랜잭션: 오류 제어 구조 및 트랜잭션 제어 여부가 설계 사양서와 부합하는가?

당신의 분석 결과는 반드시 다음 JSON 형식으로만 응답해야 합니다. 다른 텍스트나 서론, 결론은 절대 포함하지 마십시오.

{
  ""OverallStatus"": ""MATCH"" | ""MISMATCH"" | ""PARTIAL"" (반드시 세 값 중 하나로만 지정하며, 다른 텍스트 추가 금지),
  ""InputParametersGap"": ""입력 인자 불일치 내용 기술 (없으면 빈 문자열)"",
  ""OutputResultSetsGap"": ""출력 컬럼/DTO 필드 불일치 내용 기술 (없으면 빈 문자열)"",
  ""BusinessLogicGap"": ""비즈니스 로직 및 쿼리 조건 불일치 내용 기술 (없으면 빈 문자열)"",
  ""ExceptionHandlingGap"": ""예외 및 트랜잭션 처리 불일치 내용 기술 (없으면 빈 문자열)"",
  ""Suggestions"": ""불일치 해결을 위한 구체적인 코드 수정 가이드라인""
}";

            var feedbackPart = string.IsNullOrEmpty(previousFeedback)
                ? ""
                : $"\n\n[이전 불일치 피드백에 의한 교정 요청]:\n{previousFeedback}\n위에서 제시된 불일치 문제를 해결하기 위해 제시한 수정 사항이 코드에 적절히 반영되었는지, 혹은 여전히 불일치가 존재하는지 다시 평가해 주십시오.";

            var userPrompt = $@"검증 대상 언어: {targetLanguage}

[비즈니스 기능 명세서]
{specContent}

[마이그레이션된 소스 코드]
{sourceCodeContent}{feedbackPart}

위 명세서와 소스 코드를 면밀히 비교하여 JSON 결과를 작성해 주세요.";

            try
            {
                Log.Debug("[ValidatorAI] 코드 검증 AI 요청 시작 - Language: {Language}, HasPreviousFeedback: {HasFeedback}",
                    targetLanguage, !string.IsNullOrEmpty(previousFeedback));
                var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.1f, effort: null, cancellationToken: cancellationToken);
                Log.Debug("[ValidatorAI] 코드 검증 AI 응답 수신 - 응답 길이: {Length}자", aiResult.Content.Length);
                return ParseGapReport(aiResult.Content);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ValidatorAI] 코드 검증 AI 요청 중 예외 발생");
                return new GapReport
                {
                    OverallStatus = "MISMATCH",
                    Suggestions = $"AI 분석 수행 중 예외가 발생했습니다: {ex.Message}"
                };
            }
        }

        private GapReport ParseGapReport(string rawResponse)
        {
            try
            {
                // markdown json 블록 정제 (예: ```json ... ```)
                var cleanJson = rawResponse.Trim();
                if (cleanJson.StartsWith("```"))
                {
                    var match = Regex.Match(cleanJson, @"```(?:json)?\s*([\s\S]+?)\s*```");
                    if (match.Success)
                    {
                        cleanJson = match.Groups[1].Value.Trim();
                    }
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var report = JsonSerializer.Deserialize<GapReport>(cleanJson, options);
                return report ?? new GapReport
                {
                    OverallStatus = "MISMATCH",
                    Suggestions = "AI의 응답 역직렬화 결과가 null입니다."
                };
            }
            catch (Exception ex)
            {
                return new GapReport
                {
                    OverallStatus = "MISMATCH",
                    Suggestions = $"AI 응답 파싱 실패. 원본 응답:\n{rawResponse}\n오류: {ex.Message}"
                };
            }
        }

        public async Task<string> GenerateTestParametersAsync(string specContent, string procedureName, CancellationToken cancellationToken = default)
        {
            var systemPrompt = @"당신은 데이터베이스 Stored Procedure의 비즈니스 기능 명세서(*_Spec.md)를 분석하여 데이터 정합성 검증용 입력 테스트 케이스 파라미터 세트를 설계하는 전문 QA 테스트 엔지니어입니다.

명세서에 선언된 입력 파라미터명과 데이터 타입을 분석하여 다음 조건을 충족하는 테스트 케이스 세트를 JSON 형식으로 생성해 주세요:
1. 정상 시나리오 (유효한 값 주입)
2. 경계값 시나리오 (최소/최대 길이, 특이 경계값)
3. 예외/오류 시나리오 (빈 문자열, Null, 잘못된 형식 등)

반드시 다음 JSON 규격으로만 응답해 주십시오. 다른 부가적인 텍스트나 코드 블록 기호는 포함하지 마십시오.

{
  ""ProcedureName"": ""[프로시저 이름]"",
  ""TestCases"": [
    {
      ""CaseId"": ""TC001_Normal_설명"",
      ""Parameters"": {
        ""[파라미터명1]"": ""값1"",
        ""[파라미터명2]"": ""값2""
      }
    }
  ]
}";

            var userPrompt = $@"대상 프로시저: {procedureName}

[비즈니스 기능 명세서]
{specContent}

위 명세서를 토대로 테스트 케이스 입력값 JSON을 작성해 주세요.";

            try
            {
                Log.Debug("[ValidatorAI] 테스트 파라미터 생성 AI 요청 시작 - ProcedureName: {ProcedureName}", procedureName);
                var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.2f, effort: null, cancellationToken: cancellationToken);
                Log.Debug("[ValidatorAI] 테스트 파라미터 생성 AI 응답 수신 - 응답 길이: {Length}자", aiResult.Content.Length);
                
                // markdown json 블록 정제
                var cleanJson = aiResult.Content.Trim();
                if (cleanJson.StartsWith("```"))
                {
                    var match = Regex.Match(cleanJson, @"```(?:json)?\s*([\s\S]+?)\s*```");
                    if (match.Success)
                    {
                        cleanJson = match.Groups[1].Value.Trim();
                    }
                }
                return cleanJson;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ValidatorAI] 테스트 파라미터 생성 AI 요청 중 예외 발생 - ProcedureName: {ProcedureName}", procedureName);
                return $"{{\"ProcedureName\":\"{procedureName}\", \"TestCases\":[], \"Error\":\"AI를 통한 테스트 파라미터 생성 실패: {ex.Message}\"}}";
            }
        }

        public async Task<string> GenerateMockTableDataAsync(string specContent, string procedureDdl, string dependenciesJson, CancellationToken cancellationToken = default)
        {
            var systemPrompt = @"당신은 데이터베이스 Stored Procedure 마이그레이션 후 데이터 정합성을 검증하기 위해 고품질의 모의 테이블 데이터(Mock Data)를 기획하고 설계하는 전문 QA 데이터 엔지니어입니다.

제공되는 SP DDL, 의존하는 테이블들의 컬럼 스펙(및 DDL), 그리고 비즈니스 기능 명세서(*_Spec.md)를 정밀 분석하여 각 테이블에 들어갈 가상 행(Row) 데이터를 JSON 형식으로 설계해 주십시오.

다음 설계 규칙을 반드시 엄격하게 준수하십시오:
1. [논리 조인 정합성 (가장 중요)]: 
   SP 소스코드 내에서 두 테이블이 JOIN되거나 연관 조건(예: O.CustomerId = C.CustomerId)을 가질 경우, 생성되는 모의 데이터에서도 해당 조인 컬럼들의 값이 서로 완벽히 일치해야 합니다. (예: Customers의 CustomerId에 'ALFKI'가 있다면, Orders의 CustomerId에도 'ALFKI'를 매칭하여 적재해야 데이터가 정상적으로 조인 조회됩니다.)
2. [비즈니스 코드 및 날짜 반영]: 
   명세서 및 DDL에 언급된 주요 하드코딩 코드값(예: STATUS = 'A01', TYPE = '2')이나 일자 범위, 정산 공식 등을 만족하는 적합한 값을 적재하십시오.
3. [제약조건 및 타입 준수]: 
   각 컬럼의 데이터 타입(정수, 실수, 날짜/시간, 문자열)과 Null 허용 여부를 준수하십시오. 날짜는 ISO 8601 포맷(예: '2026-06-19T11:00:00')을 준수해야 합니다.
4. [데이터 볼륨]:
   의존하는 각 테이블당 의미 있는 비즈니스 케이스를 표현할 수 있도록 최소 5개 ~ 최대 20개의 가상 행(Row) 데이터를 생성해 주십시오.

반드시 다음 JSON 형식으로만 응답해야 합니다. 서론, 결론, 마크다운 코드 블록 기호(```json) 등 다른 텍스트는 절대 포함하지 마십시오.

{
  ""Tables"": [
    {
      ""TableName"": ""[스키마].[테이블명]"",
      ""Rows"": [
        {
          ""[컬럼명1]"": ""값1"",
          ""[컬럼명2]"": 100,
          ""[컬럼명3]"": ""2026-06-19T11:00:00""
        }
      ]
    }
  ]
}";

            var userPrompt = $@"
[비즈니스 기능 명세서]
{specContent}

[대상 프로시저 DDL]
{procedureDdl}

[의존 테이블 정보 및 컬럼 구조]
{dependenciesJson}

위 정보를 바탕으로 테이블들의 논리 조인 정합성을 맞춘 가상 모의 데이터 JSON을 설계해 주세요.";

            try
            {
                Log.Debug("[ValidatorAI] 모의 테이블 데이터 생성 AI 요청 시작");
                var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.2f, effort: null, cancellationToken: cancellationToken);
                Log.Debug("[ValidatorAI] 모의 테이블 데이터 생성 AI 응답 수신 - 응답 길이: {Length}자", aiResult.Content.Length);
                
                // markdown json 블록 정제
                var cleanJson = aiResult.Content.Trim();
                if (cleanJson.StartsWith("```"))
                {
                    var match = Regex.Match(cleanJson, @"```(?:json)?\s*([\s\S]+?)\s*```");
                    if (match.Success)
                    {
                        cleanJson = match.Groups[1].Value.Trim();
                    }
                }
                return cleanJson;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ValidatorAI] 모의 테이블 데이터 생성 AI 요청 중 예외 발생");
                return $"{{\"Tables\":[], \"Error\":\"AI를 통한 모의 데이터 생성 실패: {ex.Message}\"}}";
            }
        }

        public async Task<string> GenerateUnitTestCodeAsync(string specContent, string procedureName, string targetLanguage, CancellationToken cancellationToken = default)
        {
            var isCSharp = targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase);
            var testFramework = isCSharp ? "xUnit" : "JUnit 5";
            var mockFramework = isCSharp ? "Moq" : "Mockito";

            var systemPrompt = $@"당신은 Stored Procedure 설계서(*_Spec.md)를 바탕으로 마이그레이션된 소스코드의 기능 정합성을 검증하기 위한 단위 테스트 코드를 작성하는 전문 QA 엔지니어입니다.
설계서의 비즈니스 로직, 입력값 규격, 출력값 데이터셋을 검증할 수 있는 단위 테스트 코드를 작성해 주십시오.

[설계 및 작성 규칙]:
1. 대상 언어: {targetLanguage} (테스트 프레임워크: {testFramework}, 모킹 라이브러리: {mockFramework})
2. 검증 대상 구조: Hexagonal Architecture를 적용하여 비즈니스 도메인 서비스가 DB 포트(인터페이스)를 호출하는 구조를 전제합니다.
3. 테스트 커버리지:
   - 정상 케이스 (유효한 값 주입 시 정상 비즈니스 로직 수행 및 포트 호출 검증)
   - 입력 파라미터 예외 케이스 (DbC 계약에 따른 Null, 빈 값, 잘못된 타입 주입 시 예외 발생 검증)
   - 비즈니스 분기 조건별 검증
4. 추가 아키텍처 검증 (ArchUnit 스타일):
   - 해당 어셈블리/패키지 내 클래스들이 올바른 아키텍처 규칙(예: Domain이 Infrastructure를 직접 호출하지 않는지 등)을 위반하지 않는지 검사하는 간단한 아키텍처 정적 규칙 테스트 메서드도 1개 포함시켜 주십시오.
5. 출력 포맷:
   - 다른 설명, 서론, 결론은 절대 쓰지 마십시오.
   - 오직 컴파일이 가능한 순수 {targetLanguage} 소스코드만 Markdown 코드 블록(```{targetLanguage.ToLower()} ... ```) 형식으로 감싸서 리턴해 주십시오.";

            var userPrompt = $@"대상 객체 이름: {procedureName}
            
[비즈니스 기능 명세서]
{specContent}

위 명세서를 완벽히 분석하여 검증용 단위 테스트 클래스 코드를 작성해 주세요.";

            try
            {
                Log.Debug("[ValidatorAI] 단위 테스트 코드 생성 AI 요청 시작 - Name: {SpName}, Language: {Language}", procedureName, targetLanguage);
                var aiResult = await _aiClient.ChatAsync(systemPrompt, userPrompt, 0.2f, effort: null, cancellationToken: cancellationToken);
                Log.Debug("[ValidatorAI] 단위 테스트 코드 생성 AI 응답 수신 - 응답 길이: {Length}자", aiResult.Content.Length);

                // markdown 코드 블록 정제
                var cleanCode = aiResult.Content.Trim();
                var blockRegex = new Regex(@"```(?:[A-Za-z0-9_#\-]+)?\s*([\s\S]+?)\s*```");
                var match = blockRegex.Match(cleanCode);
                if (match.Success)
                {
                    cleanCode = match.Groups[1].Value.Trim();
                }

                return cleanCode;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ValidatorAI] 단위 테스트 코드 생성 AI 요청 중 예외 발생");
                return $"// AI를 통한 단위 테스트 코드 생성 실패: {ex.Message}";
            }
        }
    }
}
