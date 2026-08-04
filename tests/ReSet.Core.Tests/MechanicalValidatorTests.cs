using System;
using System.Collections.Generic;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class MechanicalValidatorTests
    {
        private readonly MechanicalValidator _validator = new();

        [Fact]
        public void Validate_WithEmptyMarkdown_ShouldReturnFalse()
        {
            var result = _validator.Validate("");
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("비어있습니다"));
        }

        [Fact]
        public void ValidateConsolidated_WithEmptyMarkdown_ShouldReturnFalse()
        {
            var result = _validator.ValidateConsolidated("");
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("비어있습니다"));
        }

        [Fact]
        public void Validate_WithValidMarkdown_ShouldReturnTrue()
        {
            var validMarkdown = @"
# SP 명세서
## 개요
이 프로시저는 사용자를 조회합니다.

## 파라미터 목록
| 이름 | 타입 | 설명 |
| :--- | :--- | :--- |
| @UserId | INT | 사용자 ID |

## CRUD 분석
| 테이블 | CRUD |
| :--- | :---: |
| dbo.Users | R |

## 로직 흐름 요약
1. 사용자를 조회합니다.

## 비즈니스 흐름 시각화 (Mermaid Diagram)
```mermaid
graph TD
    A[""시작""] --> B[""조회""]
```
";
            var result = _validator.Validate(validMarkdown);

            Assert.True(result.IsValid, "Validation failed with errors: " + string.Join(", ", result.Errors));
            Assert.Empty(result.Errors);
        }
        [Fact]
        public void Validate_WithMermaidCli_FallsBackWhenMmdcNotFound()
        {
            var validatorWithCli = new MechanicalValidator(useMermaidCli: true);
            var validMarkdown = @"
# SP 명세서
## 개요
이 프로시저는 사용자를 조회합니다.

## 파라미터 목록
| 이름 | 타입 | 설명 |
| :--- | :--- | :--- |
| @UserId | INT | 사용자 ID |

## CRUD 분석
| 테이블 | CRUD |
| :--- | :---: |
| dbo.Users | R |

## 로직 흐름 요약
1. 사용자를 조회합니다.

## 비즈니스 흐름 시각화 (Mermaid Diagram)
```mermaid
graph TD
    A[""시작""] --> B[""조회""]
```
";
            var result = validatorWithCli.Validate(validMarkdown);

            Assert.True(result.IsValid, "Validation failed with errors: " + string.Join(", ", result.Errors));
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void Validate_WithMissingHeaders_ShouldReturnFalse()
        {
            var invalidMarkdown = @"
# SP 명세서
## 개요
이 프로시저는 사용자를 조회합니다.
";
            var result = _validator.Validate(invalidMarkdown);

            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Errors);
            Assert.Contains("## 파라미터 목록", result.SuggestedPromptFix);
        }

        [Fact]
        public void Validate_WithInvalidMermaidBrackets_ShouldBeCleansedAndReturnTrue()
        {
            var invalidMarkdown = @"
# SP 명세서
## 개요
## 파라미터 목록
## CRUD 분석
## 로직 흐름 요약
## 비즈니스 흐름 시각화 (Mermaid Diagram)
```mermaid
graph TD
    A[시작 (사용자ID)] --> B[종료]
```
";
            var result = _validator.Validate(invalidMarkdown);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
            Assert.Contains("A[\"시작 (사용자ID)\"]", result.CleansedMarkdown);
        }

        [Fact]
        public void ValidateConsolidated_WithValidMarkdown_ShouldReturnTrue()
        {
            var validMarkdown = @"
# 통합 계획서
## 통합 배치 아키텍처 개요
이 단계는 여러 SP를 묶어 단일 배치로 실행합니다.

## Mermaid 기반 통합 흐름도
```mermaid
graph TD
    A-->B
```

## 단계별 이행 상세 및 의사코드
의사코드입니다.

## 통합 데이터 정합성 검증 SQL 세트
SELECT COUNT(*) FROM dbo.Users;
";
            var result = _validator.ValidateConsolidated(validMarkdown);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateConsolidated_WithMissingHeaders_ShouldReturnFalse()
        {
            var invalidMarkdown = @"
# 통합 계획서
## 통합 배치 아키텍처 개요
내용
";
            var result = _validator.ValidateConsolidated(invalidMarkdown);

            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Errors);
            Assert.Contains("Mermaid 기반 통합 흐름도", result.SuggestedPromptFix);
            Assert.Contains("## 통합 배치 아키텍처 개요", result.SuggestedPromptFix);
            Assert.Contains("## 단계별 이행 상세 및 의사코드", result.SuggestedPromptFix);
            Assert.Contains("## 통합 데이터 정합성 검증 SQL 세트", result.SuggestedPromptFix);
            Assert.DoesNotContain("## 개요", result.SuggestedPromptFix);
        }

        [Fact]
        public void Validate_WithNullOrEmpty_ShouldReturnFalse()
        {
            var result = _validator.Validate(null!);
            Assert.False(result.IsValid);
            Assert.Contains("비어있습니다", result.Errors[0]);

            result = _validator.Validate("   ");
            Assert.False(result.IsValid);
            Assert.Contains("비어있습니다", result.Errors[0]);
        }

        [Fact]
        public void ValidateConsolidated_WithNullOrEmpty_ShouldReturnFalse()
        {
            var result = _validator.ValidateConsolidated(null!);
            Assert.False(result.IsValid);
            Assert.Contains("비어있습니다", result.Errors[0]);
        }

        [Fact]
        public void Validate_WithForbiddenShortcuts_ShouldReturnFalse()
        {
            var invalidMarkdown = @"
# SP 명세서
## 개요
이하 생략
## 파라미터 목록
(생략)
## CRUD 분석
위와 동일
## 로직 흐름 요약
기타 등등
## 비즈니스 흐름 시각화 (Mermaid Diagram)
```mermaid
graph TD
    A --> B
```
";
            var result = _validator.Validate(invalidMarkdown);

            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Errors);
            Assert.Contains("허용되지 않는 축약어/생략 기호", string.Join(" ", result.Errors));
        }

        [Fact]
        public void ValidationResult_SuggestedPromptFix_WithMermaidQuoteMissing_ShouldIncludeGuide()
        {
            var result = new ValidationResult
            {
                IsValid = false,
                DetailedErrors = new List<DetailedError>
                {
                    new DetailedError { Type = ErrorType.MermaidQuoteMissing, Message = "Missing quotes", RawContext = "A[Invalid (Text)]" }
                }
            };

            var promptFix = result.SuggestedPromptFix;
            Assert.Contains("다이어그램의 노드 라벨 텍스트 안에 괄호", promptFix);
            Assert.Contains("A[Invalid (Text)]", promptFix);
        }

        [Fact]
        public void ValidationResult_SuggestedPromptFix_WithMermaidCliError_ShouldIncludeGuide()
        {
            var result = new ValidationResult
            {
                IsValid = false,
                DetailedErrors = new List<DetailedError>
                {
                    new DetailedError { Type = ErrorType.MermaidCliError, Message = "Syntax error at line 3" }
                }
            };

            var promptFix = result.SuggestedPromptFix;
            Assert.Contains("Mermaid 렌더러 검증 결과, 구문 오류로 인해 다이어그램 컴파일에 실패했습니다", promptFix);
            Assert.Contains("Syntax error at line 3", promptFix);
        }

        [Fact]
        public void ValidationResult_SuggestedPromptFix_WithGeneralError_ShouldIncludeGuide()
        {
            var result = new ValidationResult
            {
                IsValid = false,
                DetailedErrors = new List<DetailedError>
                {
                    new DetailedError { Type = ErrorType.General, Message = "Some general error" }
                }
            };

            var promptFix = result.SuggestedPromptFix;
            Assert.Contains("기타 정적 규격 검사 에러", promptFix);
            Assert.Contains("Some general error", promptFix);
        }

        [Fact]
        public void PostProcessMarkdown_ShouldCleanseMermaidCode()
        {
            var dirtyMarkdown = @"
# SP 명세서
## 비즈니스 흐름 시각화 (Mermaid Diagram)
```mermaid
graph TD
    A_1[Invalid (Text)] - -> B_2{Condition : Check}
    C -- ""Label"" --> D
```
";
            var result = _validator.PostProcessMarkdown(dirtyMarkdown);
            
            Assert.Contains("A1[\"Invalid (Text)\"]", result);
            Assert.Contains("B2{\"Condition : Check\"}", result);
            Assert.Contains("-->|Label|", result);
        }

        [Fact]
        public void PostProcessMarkdown_ShouldPreserveSubgraphAndChainedArrows()
        {
            var dirtyMarkdown = @"
# 통합 계획서
## 비즈니스 흐름 시각화 (Mermaid Diagram)
```mermaid
graph TD
    subgraph SHARED_DB
        A --> B --> C
    end
```
";
            var result = _validator.PostProcessMarkdown(dirtyMarkdown);

            Assert.Contains("subgraph SHAREDDB", result);
            Assert.DoesNotContain("subgraphSHAREDDB", result);
            Assert.Contains("A --> B --> C", result);
            Assert.DoesNotContain("-->|>", result);
        }

        // 따옴표 없는 @는 Mermaid 파스 에러를 낸다(실측: mermaid-cli 11.16.0,
        // "got 'LINK_ID'"). 따옴표만 씌우면 정상 렌더링된다.
        [Fact]
        public void PostProcessMarkdown_ShouldQuoteLabelsContainingAtSign()
        {
            var dirtyMarkdown = @"
## 비즈니스 흐름 시각화
```mermaid
graph TD
    DELPG[TPGSettleRate 삭제] --> CHK{@@ERROR 확인}
```
";
            var result = _validator.PostProcessMarkdown(dirtyMarkdown);

            Assert.Contains("CHK{\"@@ERROR 확인\"}", result);
        }

        // 이미 따옴표가 있으면 이중으로 감싸지 않는다.
        [Fact]
        public void PostProcessMarkdown_ShouldNotDoubleQuoteAtSignLabels()
        {
            var markdown = @"
## 비즈니스 흐름 시각화
```mermaid
graph TD
    CHK{""@@ERROR 확인""} --> DONE[종료]
```
";
            var result = _validator.PostProcessMarkdown(markdown);

            Assert.Contains("CHK{\"@@ERROR 확인\"}", result);
            Assert.DoesNotContain("\"\"@@ERROR", result);
        }
    }
}
