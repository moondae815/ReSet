using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    [Collection(GlobalSerilogLoggerCollection.Name)]
    public class MechanicalValidatorTests
    {
        // 조건 컬럼 대조를 쓰지 않는 테스트용 빈 재료. 비어 있으면 검사가
        // 소프트 스킵하므로 이 테스트들이 보는 동작은 달라지지 않는다.
        private static readonly System.Collections.Generic.IReadOnlyDictionary<string, SpecConditions> NoConditions =
            new System.Collections.Generic.Dictionary<string, SpecConditions>();

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

        /// <summary>
        /// 컬럼명이 Etc로 끝나고 문장 끝에 오면 "CLEtc."가 되는데, 이것은 축약어가
        /// 아니다. COMM_UPD 실측에서 AiService가 프롬프트에 만들어 넣은 문장이
        /// 그대로 명세서에 실렸고, 부분 문자열 검사가 이를 'etc.'로 읽어 L1을
        /// 3회 연속 실패시켰다. AI가 쓴 것이 사실이므로 재생성으로 고칠 수 없었다.
        /// </summary>
        [Fact]
        public void Validate_WithColumnNameEndingInEtc_ShouldNotFlagShortcut()
        {
            var markdown = @"
# SP 명세서
## 개요
이 프로시저는 정산 수수료를 갱신합니다.

## 파라미터 목록
| 이름 | 타입 | 설명 |
| :--- | :--- | :--- |
| @pi_strYMD | CHAR(8) | 정산일 |

## CRUD 분석
| 테이블 | CRUD |
| :--- | :---: |
| dbo.TSettleMst | U |

다음 컬럼은 SET 우변에서 자기 자신을 참조합니다: CLComm, CLEtc. SQL의 SET 절은 우변을 모두 갱신 전 값으로 동시에 평가합니다.

## 로직 흐름 요약
1. 수수료를 갱신합니다.

## 비즈니스 흐름 시각화 (Mermaid Diagram)
```mermaid
graph TD
    A[""시작""] --> B[""갱신""]
```
";
            var result = _validator.Validate(markdown);

            Assert.True(result.IsValid, "Validation failed with errors: " + string.Join(", ", result.Errors));
        }

        /// <summary>
        /// 위 완화가 진짜 축약어까지 통과시키면 안 된다. 앞에 영문자가 붙지 않은
        /// 'etc.'는 여전히 걸려야 한다.
        /// </summary>
        [Fact]
        public void Validate_WithStandaloneEtcAbbreviation_ShouldStillReturnFalse()
        {
            var markdown = @"
# SP 명세서
## 개요
| 컬럼 | 설명 |
| :--- | :--- |
| CLComm, etc. | 나머지 컬럼은 생략했습니다 |

## 파라미터 목록
## CRUD 분석
## 로직 흐름 요약
## 비즈니스 흐름 시각화 (Mermaid Diagram)
```mermaid
graph TD
    A[""시작""] --> B[""끝""]
```
";
            var result = _validator.Validate(markdown);

            Assert.False(result.IsValid);
            Assert.Contains("허용되지 않는 축약어/생략 기호", string.Join(" ", result.Errors));
        }

        /// <summary>
        /// L1 실패 배너는 잔존 오류 메시지를 본문에 인용하는데, 그 메시지 자체가
        /// 금지 토큰을 따옴표로 담고 있다. 배너가 붙은 문서를 다시 검증하면 배너가
        /// 스스로를 오류로 만들어 영원히 통과할 수 없다(COMM_UPD 실측: 10:22:38).
        /// </summary>
        [Fact]
        public void Validate_WithL1ExhaustedBannerPrepended_ShouldNotReFlagItsOwnMessage()
        {
            var banner = VerificationBanner.L1Exhausted(new[]
            {
                "표 내부에 허용되지 않는 축약어/생략 기호('etc.')가 감지되었습니다. 모든 컬럼과 매핑을 완벽히 기술해야 합니다."
            });

            var body = @"
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
            var result = _validator.Validate(banner + body);

            Assert.True(result.IsValid, "Validation failed with errors: " + string.Join(", ", result.Errors));
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

        // ── ValidateBatchStep: 단계 섹션 하한 검사 ─────────────────────────
        //
        // 픽스처는 실제 산출물에서 가져온다. output/jobs/POQSettleProcDaily의
        // S10은 12줄에 코드 블록이 하나도 없어 붕괴한 단계이고, S12는 24줄로 짧지만
        // 자기 조인 SQL과 원본 오류코드를 갖춰 통과해야 하는 단계다. 이 둘을
        // 갈라내지 못하면 검사가 조준되지 않은 것이다.

        private static BatchStepPlan S10Plan() => new(
            "S10", "PG 회수 통계 생성",
            new[] { "UP_UTIL_STAT_PGCOLLECT_INS" },
            new[] { "dbo.TStatPGCollect", "dbo.TSettleMst" },
            new[] { "-1" },
            Chunkable: false,
            SchemaTables: Array.Empty<string>());

        private const string S10CollapsedSection = @"### 14. S10 PG 회수 통계 생성

`S10`은 `TSettleMst`, `TTArsPGCollect`, `TBArsPGCollect`를 `UNION ALL`로 결합한다.

- `TSettleMst`: `INYMD = @pi_strYMD AND INSTATE = 1`
- 고객사, PG, MallID는 소문자 변환 후 집계

복잡한 `UNION ALL` 집계이므로 chunking하지 않고 `TStatPGCollect`에 대한 Single-Transaction Shadow Swap을 사용한다. 오류코드 `-1`을 보존한다.";

        private const string S10HealthySection = @"### 14. S10 PG 회수 통계 생성

`S10`은 `TStatPGCollect`를 재생성한다. `TSettleMst`가 원천이다.

```sql
SET XACT_ABORT ON;
DECLARE @v_currentStepId int = -1;
INSERT INTO dbo.TStatPGCollect SELECT 1;
```";

        [Fact]
        public void ValidateBatchStep_WithCodeBlockAndAllTokens_IsValid()
        {
            var result = _validator.ValidateBatchStep(S10HealthySection, S10Plan(), Array.Empty<string>(), NoConditions);

            Assert.True(result.IsValid, string.Join(" / ", result.Errors));
            Assert.Null(result.SuggestedPromptFix);
        }

        [Fact]
        public void ValidateBatchStep_WithoutCodeBlock_Fails()
        {
            var result = _validator.ValidateBatchStep(S10CollapsedSection, S10Plan(), Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("의사코드 블록이 없습니다"));
            Assert.NotNull(result.SuggestedPromptFix);
        }

        [Fact]
        public void ValidateBatchStep_WithBareTableName_SatisfiesQualifiedRequirement()
        {
            // 실제 문서는 같은 테이블을 dbo.TSettleMst와 TSettleMst로 섞어 쓴다.
            // 접두사까지 포함해 대조하면 정상 문서가 실패한다.
            var section = "### S02 기본 정산 원장 생성\n\n본문은 TSettleMst만 적었다. 오류코드 -1.\n\n```sql\nSELECT 1;\n```";
            var plan = new BatchStepPlan("S02", "기본 정산 원장 생성",
                new[] { "UP_UTIL_SETTLE_INS" }, new[] { "dbo.TSettleMst" }, new[] { "-1" }, false,
                Array.Empty<string>());

            var result = _validator.ValidateBatchStep(section, plan, Array.Empty<string>(), NoConditions);

            Assert.True(result.IsValid, string.Join(" / ", result.Errors));
        }

        [Fact]
        public void ValidateBatchStep_WithErrorCodeSubstringOnly_Fails()
        {
            // -1을 요구하는데 본문에 -10만 있으면 실패해야 한다. 부분 문자열 대조로
            // 회귀하면 -1이 -10 안에서 걸려 이 검사가 통째로 무력해진다.
            var section = "### S08 회수일 산정\n\n대상은 TSettleMst이고 오류코드는 -10뿐이다.\n\n```sql\nSELECT 1;\n```";
            var plan = new BatchStepPlan("S08", "회수일 산정",
                new[] { "UP_UTIL_SETTLE_EXPECT_PROC" }, new[] { "dbo.TSettleMst" }, new[] { "-1" }, false,
                Array.Empty<string>());

            var result = _validator.ValidateBatchStep(section, plan, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("-1"));
        }

        [Fact]
        public void ValidateBatchStep_WithMissingTargetTable_Fails()
        {
            var section = "### S10 PG 회수 통계 생성\n\nTStatPGCollect만 적었다. 오류코드 -1.\n\n```sql\nSELECT 1;\n```";

            var result = _validator.ValidateBatchStep(section, S10Plan(), Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("TSettleMst"));
        }

        [Fact]
        public void ValidateBatchStep_WithWrongHeading_Fails()
        {
            var section = "## S10 PG 회수 통계 생성\n\nTStatPGCollect와 TSettleMst. 오류코드 -1.\n\n```sql\nSELECT 1;\n```";

            var result = _validator.ValidateBatchStep(section, S10Plan(), Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("헤딩"));
        }

        [Fact]
        public void ValidateBatchStep_WithHeadingMissingStepCode_Fails()
        {
            var section = "### PG 회수 통계 생성\n\nTStatPGCollect와 TSettleMst. 오류코드 -1.\n\n```sql\nSELECT 1;\n```";

            var result = _validator.ValidateBatchStep(section, S10Plan(), Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
        }

        [Fact]
        public void ValidateBatchStep_WithEmptyMarkdown_Fails()
        {
            var result = _validator.ValidateBatchStep("", S10Plan(), Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("비어있습니다"));
        }

        // ── 목차가 검사 재료를 안 냈을 때 ────────────────────────────────
        //
        // 실측: POQSettleProcDaily5는 12단계 전부 "ErrorCodes": []로 나왔다.
        // 계획서 본문에는 S01 `-9`~`-10`, S04 16개가 다 적혀 있는데 기계 판독
        // 배열만 비어 있었다. foreach가 0회 돌아 오류코드 검증이 12/12 무실행이
        // 됐고, 로그에는 "에러 개수: 0개"로 찍혔다. 재료가 없는 것과 대조해서
        // 깨끗한 것은 다른 사건인데 결과가 같으면 게이트가 아니다.

        [Fact]
        public void ValidateBatchStep_WithEmptyErrorCodes_Fails()
        {
            var plan = S10Plan() with { ErrorCodes = Array.Empty<string>() };

            var result = _validator.ValidateBatchStep(S10HealthySection, plan, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("ErrorCodes"));
        }

        [Fact]
        public void ValidateBatchStep_WithEmptyTargetTables_Fails()
        {
            var plan = S10Plan() with { TargetTables = Array.Empty<string>() };

            var result = _validator.ValidateBatchStep(S10HealthySection, plan, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("TargetTables"));
        }

        [Fact]
        public void ValidateBatchStep_WithBlankOnlyErrorCodes_Fails()
        {
            // 배열은 있는데 원소가 전부 공백이면 기존 루프가 continue로 전부
            // 건너뛰어 빈 배열과 똑같이 무실행이 된다.
            var plan = S10Plan() with { ErrorCodes = new[] { "", "  " } };

            var result = _validator.ValidateBatchStep(S10HealthySection, plan, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("ErrorCodes"));
        }

        [Fact]
        public void ValidateBatchStep_WithEmptyPlanArrays_IsNotFixableByRegeneration()
        {
            // 빈 배열은 목차의 결함이다. 단계 본문을 다시 생성해도 프롬프트에
            // 넘길 코드가 애초에 없으므로 결과가 같다. 재시도를 걸면 단계마다
            // AI 호출 1회를 버리고 같은 자리로 돌아온다.
            var plan = S10Plan() with
            {
                ErrorCodes = Array.Empty<string>(),
                TargetTables = Array.Empty<string>(),
            };

            var result = _validator.ValidateBatchStep(S10HealthySection, plan, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.False(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_WithBodyDefect_IsFixableByRegeneration()
        {
            // 본문에 코드 블록이 없는 것은 재생성으로 고쳐진다 - 기존 재시도가
            // 존재하는 이유이고, 그 경로가 살아 있어야 한다.
            var result = _validator.ValidateBatchStep(S10CollapsedSection, S10Plan(), Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_WithBothDefects_IsFixableByRegeneration()
        {
            // 목차 결함이 섞였다고 본문 결함까지 포기하지 않는다. 하나라도
            // 재생성으로 고칠 수 있으면 재시도할 값어치가 있다.
            var plan = S10Plan() with { ErrorCodes = Array.Empty<string>() };

            var result = _validator.ValidateBatchStep(S10CollapsedSection, plan, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_WithNoLegacyProcedure_TreatsEmptyErrorCodesAsNotApplicable()
        {
            // 레거시 출신이 없는 단계는 보존할 원본 코드가 애초에 없다. 실측
            // POQSettleProcDaily6의 S00(실행 잠금 사전검증)과 S08(수수료 총액 확정)이
            // 그런 경우로, 둘 다 계획이 새로 설계한 단계다.
            var plan = S10Plan() with
            {
                LegacyProcedures = Array.Empty<string>(),
                ErrorCodes = Array.Empty<string>(),
            };

            // [Task 3] S10HealthySection의 `-1`은 레거시 코드다. 이 테스트가 흉내 내는
            // 무출신 단계는 그 값을 쓸 자격이 없다(CheckControlStepErrorCodeBand 참고) -
            // 예약 블록 안의 값(S10 블록 시작 -9100)으로 바꿔 두 관심사(오류코드 대조 vs
            // 제어 코드 대역)를 섞지 않는다.
            const string section = @"### 14. S10 PG 회수 통계 생성

`S10`은 `TStatPGCollect`를 재생성한다. `TSettleMst`가 원천이다.

```sql
SET XACT_ABORT ON;
DECLARE @v_currentStepId int = -9100;
INSERT INTO dbo.TStatPGCollect SELECT 1;
```";

            var result = _validator.ValidateBatchStep(section, plan, Array.Empty<string>(), NoConditions);

            Assert.True(result.IsValid);
            Assert.Empty(result.PlanDefects);
        }

        [Fact]
        public void ValidateBatchStep_WithNoLegacyProcedure_LogsInsteadOfStayingSilent()
        {
            // "해당 없음"이 결함은 아니지만 침묵과 구별되지 않으면 "대조 항목 0개"가
            // "대조해서 깨끗함"과 로그에서 같아 보이는 결함이 되살아난다.
            var plan = S10Plan() with
            {
                LegacyProcedures = Array.Empty<string>(),
                ErrorCodes = Array.Empty<string>(),
            };

            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
            try
            {
                _validator.ValidateBatchStep(S10HealthySection, plan, Array.Empty<string>(), NoConditions);
            }
            finally
            {
                Log.CloseAndFlush();
                Log.Logger = previousLogger;
            }

            Assert.Contains(sink.Messages, m => m.Contains("S10") && m.Contains("오류코드 대조 대상이 아닙니다"));
        }

        [Fact]
        public void ValidateBatchStep_WithLegacyProcedureButNoErrorCodes_StillFails()
        {
            // 출신이 있는데 코드가 비었다면 보강이 실패한 것이다. 그 사실은 남아야 한다.
            var plan = S10Plan() with { ErrorCodes = Array.Empty<string>() };

            var result = _validator.ValidateBatchStep(S10HealthySection, plan, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.PlanDefects, d => d.Contains("ErrorCodes"));
        }

        [Fact]
        public void ValidateBatchStep_WithNoLegacyProcedure_StillChecksTargetTables()
        {
            // 두 축은 독립이다. 출신이 없다는 것과 쓰는 테이블이 없다는 것은 다른 사실이고,
            // 아무것도 쓰지 않는다는 선언은 그 자체로 확인이 필요하다.
            var plan = S10Plan() with
            {
                LegacyProcedures = Array.Empty<string>(),
                ErrorCodes = Array.Empty<string>(),
                TargetTables = Array.Empty<string>(),
            };

            var result = _validator.ValidateBatchStep(S10HealthySection, plan, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.PlanDefects, d => d.Contains("TargetTables"));
            Assert.DoesNotContain(result.PlanDefects, d => d.Contains("ErrorCodes"));
        }

        [Fact]
        public void ValidateBatchStep_WithNoLegacyProcedureButDeclaredErrorCodes_DoesNotLogNotApplicable()
        {
            // 스펙 §4: "해당 없음"은 ErrorCodes가 비었고 LegacyProcedures도 빈 경우에만
            // 해당한다. 레거시 출신이 없는 신설 단계라도 목차가 ErrorCodes를 선언했다면
            // 바로 아래 foreach가 실제로 대조하므로, "대조 대상이 아닙니다" 로그는 거짓말이 된다.
            var plan = S10Plan() with { LegacyProcedures = Array.Empty<string>() };

            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
            try
            {
                _validator.ValidateBatchStep(S10HealthySection, plan, Array.Empty<string>(), NoConditions);
            }
            finally
            {
                Log.CloseAndFlush();
                Log.Logger = previousLogger;
            }

            Assert.DoesNotContain(sink.Messages, m => m.Contains("오류코드 대조 대상이 아닙니다"));
        }

        private sealed class CapturingSink : ILogEventSink
        {
            public List<string> Messages { get; } = new();
            public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
        }

        private const string SpecSkeleton = @"## 개요
본문
## 파라미터 목록
본문
## CRUD 분석
{0}
## 로직 흐름 요약
본문
## 비즈니스 흐름 시각화
```mermaid
graph TD
A[""시작""] --> B[""끝""]
```
";

        private static string SpecWith(string crudBody) => SpecSkeleton.Replace("{0}", crudBody);

        private static SpecExpectations ExpectClvtAndPgvt()
        {
            var analysis = new SpStaticAnalysisResult();
            var mapping = new AstUpdateMapping { TargetTable = "DB.dbo.TCommMst", StatementOrdinal = 1 };
            mapping.Assignments.Add(new AstUpdateAssignment { Column = "CLVT", SourceExpression = "CLVT * -1" });
            mapping.Assignments.Add(new AstUpdateAssignment { Column = "PGVT", SourceExpression = "PGVT * -1" });
            analysis.AstUpdateMappings.Add(mapping);
            return SpecExpectations.From(new SpDefinition { StaticAnalysis = analysis })!;
        }

        [Fact]
        public void Validate_WithoutExpectations_ShouldBehaveAsBefore()
        {
            // Arrange
            var markdown = SpecWith("UPDATE 대상 테이블의 금액 컬럼을 -1배 처리합니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validate_WhenAllExpectedUpdateColumnsPresent_ShouldPass()
        {
            // Arrange
            var markdown = SpecWith(@"### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB.dbo.TCommMst | CLVT | CLVT * -1 | 취소 시 음수 전환 |
| DB.dbo.TCommMst | PGVT | PGVT * -1 | 취소 시 음수 전환 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert
            Assert.True(result.IsValid);
        }

        /// <summary>
        /// [2026-08-21 최종 브랜치 리뷰 재라운드 ⑤ - "원천 표현식(SET)" 칸(AiService.cs:736)은
        /// 파이프 왕복이 필요 없다는 것을 실측으로 증명한다] `CheckUpdateMappings`는
        /// `expectation.Columns`(컬럼명)만 `ContainsToken`으로 대조하고, `SourceExpression`
        /// 자체는 어떤 검사도 다시 들여다보지 않는다 - 렌더가 그 칸을 EscapeTableCell로
        /// 감싸도(비트 OR `A.Flags | 4`처럼 `|`가 든 식) 대조는 그 칸의 내용을 아예 보지
        /// 않으므로 이스케이프 여부가 판정에 영향을 줄 수 없다. 컬럼명 대조만 통과하면
        /// 되므로, 파이프가 든 표현식이어도 통과해야 한다 - 다른 취급이 필요 없다는
        /// 것을 이 테스트가 고정한다(회귀 시 이 테스트가 잡는다).
        /// </summary>
        [Fact]
        public void Validate_UpdateMappingSourceExpressionContainsPipe_RenderedEscaped_ShouldStillPass()
        {
            var analysis = new SpStaticAnalysisResult();
            var mapping = new AstUpdateMapping { TargetTable = "DB.dbo.TCommMst", StatementOrdinal = 1 };
            mapping.Assignments.Add(new AstUpdateAssignment { Column = "FLAGS", SourceExpression = "A.Flags | 4" });
            analysis.AstUpdateMappings.Add(mapping);
            var expectations = SpecExpectations.From(new SpDefinition { StaticAnalysis = analysis })!;

            var markdown = SpecWith(@"### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB.dbo.TCommMst | FLAGS | A.Flags \| 4 | 플래그 비트 설정 |");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validate_WhenAnExpectedUpdateColumnIsMissing_ShouldReportIt()
        {
            // Arrange
            var markdown = SpecWith(@"### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB.dbo.TCommMst | CLVT | CLVT * -1 | 취소 시 음수 전환 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.UpdateMappingMissing);
            Assert.Contains(result.Errors, e => e.Contains("PGVT"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("CLVT") && !e.Contains("PGVT"));
        }

        [Fact]
        public void Validate_WhenTheUpdateTableSectionIsAbsent_ShouldReportIt()
        {
            // Arrange
            var markdown = SpecWith("UPDATE 대상 테이블의 금액 컬럼을 -1배 처리합니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.UpdateMappingMissing);
        }

        [Fact]
        public void Validate_WhenCrudHeaderIsAbsent_ShouldNotDuplicateTheMappingError()
        {
            // Arrange - `## CRUD 분석` 헤더 자체가 없으면 ValidateMarkdownStructure가 헤더
            // 누락을 이미 보고한다. CheckUpdateMappings가 같은 결함을 매핑 누락으로 또
            // 보고하면 중복이다.
            var markdown = @"## 개요
본문
## 파라미터 목록
본문
## 로직 흐름 요약
본문
## 비즈니스 흐름 시각화
```mermaid
graph TD
A[""시작""] --> B[""끝""]
```
";

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.HeaderMissing);
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.UpdateMappingMissing);
        }

        [Fact]
        public void Validate_ShouldNotAcceptAPrefixMatchAsTheColumn()
        {
            // Arrange - CLVTOTAL은 CLVT가 아니다.
            var markdown = SpecWith(@"### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB.dbo.TCommMst | CLVTOTAL | 0 | 무관한 컬럼 |
| DB.dbo.TCommMst | PGVT | PGVT * -1 | 취소 시 음수 전환 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("CLVT"));
        }

        [Fact]
        public void Validate_WhenHeadingUsesTheShortTableName_ShouldStillMatch()
        {
            // Arrange
            var markdown = SpecWith(@"### UPDATE 대상 테이블: TCommMst
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| TCommMst | CLVT | CLVT * -1 | 취소 시 음수 전환 |
| TCommMst | PGVT | PGVT * -1 | 취소 시 음수 전환 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validate_WhenTheTableIsSplitAcrossTwoSections_ShouldUnionThem()
        {
            // Arrange
            var markdown = SpecWith(@"### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB.dbo.TCommMst | CLVT | CLVT * -1 | 취소 시 음수 전환 |

### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 2)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB.dbo.TCommMst | PGVT | PGVT * -1 | 취소 시 음수 전환 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public void SuggestedPromptFix_ShouldCarryTheUpdateMappingFailure()
        {
            // Arrange
            var markdown = SpecWith("UPDATE 대상 테이블의 금액 컬럼을 -1배 처리합니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert - 재생성 프롬프트에 실리지 않으면 L1이 실패해도 고칠 재료가 없다.
            Assert.NotNull(result.SuggestedPromptFix);
            Assert.Contains("UPDATE", result.SuggestedPromptFix!);
            Assert.Contains("CLVT", result.SuggestedPromptFix!);
        }

        private static SpecExpectations TwoDatabasesSharingLastNamePart(
            IReadOnlyList<string> db1Columns, IReadOnlyList<string> db2Columns)
        {
            var analysis = new SpStaticAnalysisResult();

            var mapping1 = new AstUpdateMapping { TargetTable = "DB1.dbo.TCommMst", StatementOrdinal = 1 };
            foreach (var column in db1Columns)
            {
                mapping1.Assignments.Add(new AstUpdateAssignment { Column = column, SourceExpression = $"{column} * -1" });
            }

            var mapping2 = new AstUpdateMapping { TargetTable = "DB2.dbo.TCommMst", StatementOrdinal = 1 };
            foreach (var column in db2Columns)
            {
                mapping2.Assignments.Add(new AstUpdateAssignment { Column = column, SourceExpression = $"{column} * -1" });
            }

            analysis.AstUpdateMappings.Add(mapping1);
            analysis.AstUpdateMappings.Add(mapping2);
            return SpecExpectations.From(new SpDefinition { StaticAnalysis = analysis })!;
        }

        [Fact]
        public void Validate_WhenTwoTablesShareLastNamePart_AndEachSectionIsComplete_ShouldPass()
        {
            // Arrange - DB1과 DB2는 마지막 파트(TCommMst)가 같지만 완전 한정 이름은 다르다.
            var expectations = TwoDatabasesSharingLastNamePart(
                db1Columns: new[] { "CLVT" }, db2Columns: new[] { "PGVT" });

            var markdown = SpecWith(@"### UPDATE 대상 테이블: DB1.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB1.dbo.TCommMst | CLVT | CLVT * -1 | 취소 시 음수 전환 |

### UPDATE 대상 테이블: DB2.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB2.dbo.TCommMst | PGVT | PGVT * -1 | 취소 시 음수 전환 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validate_WhenTwoTablesShareLastNamePart_ShouldNotMaskMissingColumnsAcrossThem()
        {
            // Arrange - 리뷰에서 실측된 결함 재현: DB1 섹션엔 CLVT만, DB2 섹션엔 PGVT만
            // 있는데 두 테이블 모두 CLVT와 PGVT를 요구한다. 마지막 파트로 접으면 두 섹션이
            // 합쳐져 서로의 결여를 가려버린다.
            var expectations = TwoDatabasesSharingLastNamePart(
                db1Columns: new[] { "CLVT", "PGVT" }, db2Columns: new[] { "CLVT", "PGVT" });

            var markdown = SpecWith(@"### UPDATE 대상 테이블: DB1.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB1.dbo.TCommMst | CLVT | CLVT * -1 | 취소 시 음수 전환 |

### UPDATE 대상 테이블: DB2.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB2.dbo.TCommMst | PGVT | PGVT * -1 | 취소 시 음수 전환 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert - DB1엔 PGVT가, DB2엔 CLVT가 없으므로 둘 다 잡혀야 한다.
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("DB1.dbo.TCommMst") && e.Contains("PGVT"));
            Assert.Contains(result.Errors, e => e.Contains("DB2.dbo.TCommMst") && e.Contains("CLVT"));
        }

        [Fact]
        public void Validate_WhenLastNamePartIsAmbiguousAcrossExpectations_ShouldReportAmbiguity()
        {
            // Arrange - DB1과 DB2 모두 TCommMst를 기대하는데 문서엔 짧은 이름 섹션 하나뿐이다.
            // 어느 쪽에 대응하는지 특정할 수 없으므로 합치지 말고 오류로 봐야 한다.
            var expectations = TwoDatabasesSharingLastNamePart(
                db1Columns: new[] { "CLVT" }, db2Columns: new[] { "PGVT" });

            var markdown = SpecWith(@"### UPDATE 대상 테이블: TCommMst
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| TCommMst | CLVT | CLVT * -1 | 취소 시 음수 전환 |
| TCommMst | PGVT | PGVT * -1 | 취소 시 음수 전환 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert - 짧은 이름 섹션 하나로 어느 완전 한정 테이블인지 특정할 수 없다.
            Assert.False(result.IsValid);
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.UpdateMappingMissing);
        }

        [Fact]
        public void Validate_WhenHeadingSuffixHasNoLeadingSpace_ShouldStillParseTableName()
        {
            // Arrange - "(문장 N)" 앞에 공백이 없어도 테이블명이 괄호를 삼키면 안 된다.
            var markdown = SpecWith(@"### UPDATE 대상 테이블: DB.dbo.TCommMst(문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB.dbo.TCommMst | CLVT | CLVT * -1 | 취소 시 음수 전환 |
| DB.dbo.TCommMst | PGVT | PGVT * -1 | 취소 시 음수 전환 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validate_WhenCrudHeadingHasNumericPrefix_ShouldStillDetectMissingColumn()
        {
            // Arrange - 실제 산출물은 "## CRUD 분석" 대신 "## 3. CRUD 분석"처럼 접두를
            // 붙이기도 한다. MarkdownSectionLocator.LocateSection은 완전 일치만 보므로
            // CheckUpdateMappings가 부분 일치 폴백 없이는 조용히 0회 돈다.
            var markdown = @"## 개요
본문
## 파라미터 목록
본문
## 3. CRUD 분석
### UPDATE 대상 테이블: DB.dbo.TCommMst (문장 1)
| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |
| :--- | :--- | :--- | :--- |
| DB.dbo.TCommMst | CLVT | CLVT * -1 | 취소 시 음수 전환 |
## 로직 흐름 요약
본문
## 비즈니스 흐름 시각화
```mermaid
graph TD
A[""시작""] --> B[""끝""]
```
";

            // Act
            var result = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());

            // Assert - PGVT가 누락됐다는 것이 잡혀야 한다. 폴백이 없으면 섹션을 못 찾아
            // 조용히 통과한다.
            Assert.False(result.IsValid);
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.UpdateMappingMissing);
            Assert.Contains(result.Errors, e => e.Contains("PGVT"));
        }

        [Fact]
        public void Validate_WhenCrudHeadingCannotBeLocatedEvenByFallback_ShouldLogWarning()
        {
            // Arrange - "### CRUD 분석"(h3)은 필수 헤더 부분 일치 검사(레벨 무관, Contains)는
            // 통과하지만, CheckUpdateMappings의 완전 일치("## CRUD 분석")도 부분 일치
            // 폴백(레벨 2 "## " 접두 전제)도 못 찾는다. 이 경우 조용히 0회 도는 대신
            // Log.Warning을 남겨야 한다 - 검사가 말없이 꺼지는 것이 이 저장소가 반복해서
            // 겪은 실패 양식이다.
            var markdown = @"## 개요
본문
## 파라미터 목록
본문
### CRUD 분석
UPDATE 대상 테이블의 금액 컬럼을 -1배 처리합니다.
## 로직 흐름 요약
본문
## 비즈니스 흐름 시각화
```mermaid
graph TD
A[""시작""] --> B[""끝""]
```
";

            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();

            try
            {
                _ = new MechanicalValidator().Validate(markdown, ExpectClvtAndPgvt());
            }
            finally
            {
                Log.CloseAndFlush();
                Log.Logger = previousLogger;
            }

            // Assert - 필수 헤더 누락 메시지("필수 섹션 헤더 ... 누락되었습니다")도 "CRUD 분석"을
            // 포함할 수 있으므로, 새로 추가한 경고에만 있는 고유 문구("완전/부분 일치 모두로
            // 찾지 못해")를 단언해야 헤더 메시지 형식이 바뀌어도 우연히 통과하지 않는다.
            Assert.Contains(sink.Messages, m => m.Contains("완전/부분 일치 모두로 찾지 못해"));
        }

        private static SpecExpectations SchemaExpectations(
            string canonicalTable, params string[] columns)
        {
            var dep = new DependencyInfo
            {
                Name = canonicalTable.Split('.')[^1],
                Schema = "dbo",
                Database = canonicalTable.Split('.').Length >= 3 ? canonicalTable.Split('.')[0] : null,
                Type = "USER_TABLE"
            };
            foreach (var column in columns)
            {
                dep.Columns.Add(new ColumnInfo { ColumnName = column, DataType = "int" });
            }

            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey(
                    canonicalTable.Split('.').Length >= 3 ? canonicalTable.Split('.')[0] : "DB",
                    "dbo", "UP_PROBE", CodeObjectType.Procedure)
            };
            sp.Dependencies.Add(dep);
            return SpecExpectations.From(sp)!;
        }

        private static string WrapSpec(string crudBody)
        {
            return string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용",
                "## CRUD 분석", crudBody,
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });
        }

        [Fact]
        public void Validate_WhenSpecClaimsAnExistingColumnIsAbsent_ShouldFail()
        {
            // Arrange - 14개 명세서를 통과시킨 결함의 모양.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM", "CLETC");
            var markdown = WrapSpec(
                "### 스키마 불일치 컬럼\n\n" +
                "| 테이블명 | 컬럼명 | 판정 | 용도 |\n" +
                "|---|---|---|---|\n" +
                "| `dbo.TSettleMst` | `CLINTCOMM` | 존재하지 않음 | 할부이자 고객사 수수료 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
            Assert.Contains(result.Errors, e => e.Contains("CLINTCOMM"));
        }

        [Fact]
        public void Validate_WhenTheAbsenceClaimIsTrue_ShouldPass()
        {
            // Arrange - 그 테이블에 없는 컬럼을 없다고 하는 것은 참인 진술이다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec(
                "`dbo.TSettleMst`의 `NotAColumn`은 제공된 스키마에 없는 열이므로 스키마 불일치입니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenNoTableCanBeAttributed_ShouldPass()
        {
            // Arrange - 테이블을 특정할 수 없으면 침묵한다. 잘못 지목한 오류는
            // 재생성으로 고칠 수 없다. "스키마"를 문장에 넣어 부재 표현 게이트를 통과시킨
            // 뒤에도 귀속 실패로 침묵하는지를 실제로 검증한다 - 게이트조차 안 열리면
            // 이 가드는 시험되지 않는다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec(
                "프로시저에는 `INSERT` 문이 없습니다. 제공된 스키마 기준으로 `CLINTCOMM`은 존재하지 않습니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WithoutAnAbsenceExpression_ShouldPass()
        {
            // Arrange - 부재 표현이 없으면 컬럼과 테이블이 같이 나와도 오류가 아니다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec("| `dbo.TSettleMst` | `CLINTCOMM` | 할부이자 고객사 수수료를 갱신합니다. |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenTheSameClaimRepeats_ShouldReportItOnce()
        {
            // Arrange - 같은 (테이블, 컬럼) 주장이 여러 줄에 나와도 재생성 지시는 하나면 된다.
            // 두 줄 모두 "스키마"를 포함시켜 부류 B 게이트를 통과하게 한다 - 그래야
            // reported.Add 중복 제거 가드가 실제로 시험된다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec(
                "`dbo.TSettleMst`의 스키마에 `CLINTCOMM`은 존재하지 않습니다.\n" +
                "다시 말해 `dbo.TSettleMst`의 스키마에 `CLINTCOMM`은 존재하지 않습니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenTheLastNamePartIsAmbiguous_ShouldStaySilent()
        {
            // Arrange - 마지막 파트가 같은 테이블이 둘이면 귀속이 불가능하다.
            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey("DB1", "dbo", "UP_PROBE", CodeObjectType.Procedure)
            };
            foreach (var db in new[] { "DB1", "DB2" })
            {
                var dep = new DependencyInfo { Name = "TCommMst", Schema = "dbo", Database = db, Type = "USER_TABLE" };
                dep.Columns.Add(new ColumnInfo { ColumnName = "AMT", DataType = "int" });
                sp.Dependencies.Add(dep);
            }
            var expectations = SpecExpectations.From(sp)!;
            // "스키마"를 문장에 넣어 부류 B 게이트를 통과하게 한다 - 그래야 귀속 모호
            // 시의 침묵이 게이트 자체가 안 열려서가 아니라 ResolveSchemaTableKey가
            // null을 돌려줘서임을 실제로 검증한다.
            var markdown = WrapSpec("`dbo.TCommMst`의 스키마에 `AMT`는 존재하지 않습니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        // 아래는 두 라운드의 리뷰에서 지적된 결함에 대한 회귀 테스트다.
        //
        // 1라운드 결함: AbsenceClaimTokens의 맨 "존재하지 않"이 스키마 부재 단정과
        // 런타임 NULL/기본값 서술을 구별하지 못했다.
        //
        // 2라운드 결함: 1라운드가 내놓은 "단정형이냐 조건형이냐"라는 판별자가 틀렸다.
        // 한국어 어미는 그렇게 이분법적이지 않다 - "존재하지 않아"(연결형)는 단정도
        // 조건도 아닌데, 실측 코퍼스(PG_Client_CMRate_Ins:71)의 진짜 결함 문장이 바로
        // 이 연결형을 쓴다. 진짜 판별자는 어미가 아니라 주장의 대상이다 - 스키마에
        // 대한 주장인가, 값에 대한 주장인가. 아래 두 부류로 나눈 이유는
        // MechanicalValidator.CheckSchemaClaims 바로 위 AbsenceClaimTokens 주석을 참고.
        //
        // 결함(가드 미검증): ResolveSchemaTableKey(candidate, …) != null 가드가 하중을
        // 지는데도 회귀 테스트가 없었다. 이 가드가 없으면 그 자체로 실재하는 테이블명이
        // 우연히 다른 테이블의 컬럼명과 같을 때, 테이블 부재 진술이 컬럼 부재 주장으로
        // 오귀속된다.

        [Fact]
        public void Validate_WhenSpecUsesSchemaMismatchPhraseForARealColumn_ShouldFail()
        {
            // Arrange - "스키마에 없는 열이므로 스키마 불일치입니다"로 실재하는 컬럼을
            // 부재로 단정하는 실측 결함 형태(리뷰 검증 시나리오 2).
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLCOMM");
            var markdown = WrapSpec(
                "`CLCOMM`은 제공된 `dbo.TSettleMst` 스키마에 없는 열이므로 스키마 불일치입니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
            Assert.Contains(result.Errors, e => e.Contains("CLCOMM"));
        }

        [Fact]
        public void Validate_WhenTableHasNoProvidedSchema_AbsenceClaimIsTrueAndShouldPass()
        {
            // Arrange - TPGProperty처럼 메타데이터 수집이 안 된(컬럼 0개) 의존성은
            // SpecExpectations.From이 PromptSchemaColumns에서 제외한다 - 스키마 표
            // 자체가 렌더링되지 않기 때문이다. 그 테이블에 대한 "스키마에 없는 열"
            // 주장은 참인 진술이므로 오류가 아니다(리뷰 검증 시나리오 3). expectations를
            // null로 만들지 않기 위해 컬럼을 가진 다른 테이블을 하나 더 둔다.
            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey("DB", "dbo", "UP_PROBE", CodeObjectType.Procedure)
            };

            var noSchemaDep = new DependencyInfo { Name = "TPGProperty", Schema = "dbo", Database = "DB", Type = "USER_TABLE" };
            sp.Dependencies.Add(noSchemaDep);

            var otherDep = new DependencyInfo { Name = "TSettleMst", Schema = "dbo", Database = "DB", Type = "USER_TABLE" };
            otherDep.Columns.Add(new ColumnInfo { ColumnName = "CLINTCOMM", DataType = "int" });
            sp.Dependencies.Add(otherDep);

            var expectations = SpecExpectations.From(sp)!;
            Assert.DoesNotContain("DB.dbo.TPGProperty", (IEnumerable<string>)expectations.PromptSchemaColumns.Keys);

            var markdown = WrapSpec("`PLTID`, `ID`는 제공된 `TPGProperty` 스키마에 없는 열입니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenSpecDescribesRuntimeNullHandling_UsingConditionalPhrase_ShouldPass()
        {
            // Arrange - "존재하지 않는 경우"는 런타임 NULL/기본값 처리를 설명하는 조건형
            // 문장이지 스키마 부재를 단정하는 문장이 아니다. 좁힌 어휘 목록은 단정형만
            // 잡으므로 이 문장은 걸리지 않아야 한다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec(
                "`CLINTCOMM` 값이 `dbo.TSettleMst`에 존재하지 않는 경우 기본값 0을 사용합니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenSpecDescribesRuntimeNullHandling_UsingConditionalConnective_ShouldPass()
        {
            // Arrange - "존재하지 않으면"도 마찬가지로 조건형이라 걸리면 안 된다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLVT");
            var markdown = WrapSpec(
                "실제 실행 대상 테이블에 해당 `CLVT` 컬럼이 `dbo.TSettleMst`에 존재하지 않으면 갱신을 건너뜁니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenAnotherTableNameCoincidesWithAColumnName_ShouldNotMisattribute()
        {
            // Arrange - 실측된 결함 시나리오(리뷰 발견 2). `TRate`는 그 자체로 실재하는
            // 별개 테이블이면서 우연히 `TSettleMst`의 컬럼명이기도 하다.
            // ResolveSchemaTableKey(candidate, …) != null 가드가 없으면 "TRate는 제공된
            // 스키마에 존재하지 않습니다"라는 테이블 부재 진술이 TSettleMst에 대한 거짓
            // 컬럼 주장으로 오귀속된다.
            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey("DB", "dbo", "UP_PROBE", CodeObjectType.Procedure)
            };

            var settleMst = new DependencyInfo { Name = "TSettleMst", Schema = "dbo", Database = "DB", Type = "USER_TABLE" };
            settleMst.Columns.Add(new ColumnInfo { ColumnName = "CLINTCOMM", DataType = "int" });
            settleMst.Columns.Add(new ColumnInfo { ColumnName = "TRate", DataType = "int" });
            sp.Dependencies.Add(settleMst);

            var rateTable = new DependencyInfo { Name = "TRate", Schema = "dbo", Database = "DB", Type = "USER_TABLE" };
            rateTable.Columns.Add(new ColumnInfo { ColumnName = "ID", DataType = "int" });
            sp.Dependencies.Add(rateTable);

            var expectations = SpecExpectations.From(sp)!;
            var markdown = WrapSpec("`dbo.TSettleMst`, `TRate`는 제공된 스키마에 존재하지 않습니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        // 아래는 2라운드 리뷰가 실제 코퍼스에서 재현한 미탐(false negative)에 대한
        // 회귀 테스트다. 1라운드의 "단정형/조건형" 판별자가 어휘를 좁히면서 연결형
        // 어미("존재하지 않아")를 쓰는 진짜 결함 문장까지 함께 놓쳤다.

        [Fact]
        public void Validate_WhenSpecUsesConnectiveFormWithSchemaContextToDenyRealColumns_ShouldFail()
        {
            // Arrange - dbo.UP_Util_PG_Client_CMRate_Ins/docs/Spec.md:71의 실측 원문.
            // 선행 설계 문서(2026-08-08-static-analysis-identity-design.md §확인된 결함 ①)가
            // 지목한 원본 결함 3종 중 하나이며, CompanySalesType·ExtraSettleFlag는 실재하는
            // 컬럼이다. "존재하지 않아"는 연결형 어미라 1라운드의 단정형 토큰 어느 것도
            // 매치하지 못했다 - 이번 라운드의 회귀 방지선이다.
            var expectations = SchemaExpectations("DB.dbo.TClient", "CompanySalesType", "ExtraSettleFlag");
            var markdown = WrapSpec(
                "다만 제공된 `dbo.TClient` 스키마에는 `CompanySalesType`, `ExtraSettleFlag` 컬럼이 " +
                "존재하지 않아 소스 코드와 제공 스키마 간 불일치가 있습니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
            Assert.Contains(result.Errors, e => e.Contains("CompanySalesType"));
            Assert.Contains(result.Errors, e => e.Contains("ExtraSettleFlag"));
        }

        [Fact]
        public void Validate_WhenLineMixesAnAssertionWithAConditionalHedge_ShouldStillFail()
        {
            // Arrange - dbo.UP_UTIL_SETTLE_CANCEL_INS/docs/Spec.md:110의 실측 원문 형태.
            // 1라운드 리뷰는 이 줄을 오탐 후보로 의심했지만, 코디네이터가 재확인한 대로
            // 이것은 오탐이 아니라 진짜 결함이다 - CYMD·INSTATE·OUTSTATE·NonSettleAmt는
            // 필터 결함으로 프롬프트에서 누락됐던 실재 컬럼들이다(선행 설계 문서 §확인된
            // 결함 ①). 한 물리 줄 안에 단정("컬럼 불일치가 존재합니다")과 조건형 헤징
            // ("존재하지 않으면")이 섞여 있어도, 그 줄에 "스키마"와 "존재하지 않"이 함께
            // 있으므로 부류 B로 걸려야 한다.
            var expectations = SchemaExpectations(
                "DB.dbo.TSettleMst", "CYMD", "INSTATE", "OUTSTATE", "NonSettleAmt");
            var markdown = WrapSpec(
                "`CYMD`, `INSTATE`, `OUTSTATE`, `NonSettleAmt`에 대해 컬럼 불일치가 존재합니다. " +
                "해당 컬럼이 `dbo.TSettleMst` 스키마에 존재하지 않으면 기본값을 사용합니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
            Assert.Contains(result.Errors, e => e.Contains("CYMD"));
            Assert.Contains(result.Errors, e => e.Contains("INSTATE"));
            Assert.Contains(result.Errors, e => e.Contains("OUTSTATE"));
            Assert.Contains(result.Errors, e => e.Contains("NonSettleAmt"));
        }

        [Fact]
        public void Validate_WhenOneTableIsSplitAcrossSpellings_ShouldFail()
        {
            // Arrange - EXCEPTION_PROC에서 실측된 결함. 한 표 안에 세 표기가 공존한다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "PLTID");
            var markdown = WrapSpec(
                "### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n" +
                "|---|---|\n" +
                "| `DB.dbo.TSettleMst` | `PLTID` |\n" +
                "| `dbo.TSettleMst` | `PLTID` |\n" +
                "| `TSettleMst` | `PLTID` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.False(result.IsValid);
            var error = Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
            Assert.Contains("dbo.TSettleMst", error.Message);
            Assert.Contains("TSettleMst", error.Message);
        }

        [Fact]
        public void Validate_WhenTheSameSpellingRepeats_ShouldPass()
        {
            // Arrange - 같은 문자열이 반복되는 것은 이 결함이 아니다. 문장별로 나눠
            // 적었을 수 있고, UPDATE 매핑 헤딩이 정확히 그렇게 한다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "PLTID");
            var markdown = WrapSpec(
                "### 갱신 대상 테이블\n\n" +
                "| 테이블명 | 갱신 컬럼 |\n" +
                "|---|---|\n" +
                "| `dbo.TSettleMst` | `PLTID` |\n" +
                "| `dbo.TSettleMst` | `PLTID` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }

        [Fact]
        public void Validate_WhenTwoRealTablesShareALastNamePart_ShouldPass()
        {
            // Arrange - DB1.dbo.TCommMst와 DB2.dbo.TCommMst는 서로 다른 물리 테이블이다.
            // 마지막 파트가 같다는 이유로 합치면 정상 명세서를 떨어뜨린다.
            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey("DB1", "dbo", "UP_PROBE", CodeObjectType.Procedure)
            };
            foreach (var db in new[] { "DB1", "DB2" })
            {
                var dep = new DependencyInfo { Name = "TCommMst", Schema = "dbo", Database = db, Type = "USER_TABLE" };
                dep.Columns.Add(new ColumnInfo { ColumnName = "AMT", DataType = "int" });
                sp.Dependencies.Add(dep);
            }
            var expectations = SpecExpectations.From(sp)!;
            var markdown = WrapSpec(
                "### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n" +
                "|---|---|\n" +
                "| `DB1.dbo.TCommMst` | `AMT` |\n" +
                "| `DB2.dbo.TCommMst` | `AMT` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }

        [Fact]
        public void Validate_WhenSpellingsAreInDifferentSubsections_ShouldPass()
        {
            // Arrange - 조회 절과 갱신 절에 각각 나오는 것은 정상이다.
            // 같은 테이블이 읽히고 갱신되는 것은 흔하다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "PLTID");
            var markdown = WrapSpec(
                "### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n|---|---|\n| `DB.dbo.TSettleMst` | `PLTID` |\n\n" +
                "### 갱신 대상 테이블\n\n" +
                "| 테이블명 | 갱신 컬럼 |\n|---|---|\n| `dbo.TSettleMst` | `PLTID` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }

        // 아래는 최종 브랜치 리뷰가 지적한 결함에 대한 회귀 테스트다.
        //
        // [Important] 컬럼 미수집 의존성이 대조 집합에서 빠지면서 동일 말단 이름
        // 모호성 가드가 무력화된다. DB1.dbo.TSettleMst(컬럼 있음)와
        // DB2.dbo.TSettleMst(컬럼 0개, 메타데이터 미수집)가 같이 있을 때, 컬럼 0개
        // 쪽에 대한 참인 문장이나 CRUD 표 행이 컬럼 있는 쪽으로 오귀속된다.

        private static SpDefinition BuildSettleMstWithColumnlessSibling()
        {
            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey("DB1", "dbo", "UP_PROBE", CodeObjectType.Procedure)
            };

            var withColumns = new DependencyInfo { Name = "TSettleMst", Schema = "dbo", Database = "DB1", Type = "USER_TABLE" };
            withColumns.Columns.Add(new ColumnInfo { ColumnName = "CLINTCOMM", DataType = "int" });
            sp.Dependencies.Add(withColumns);

            // 컬럼 0개 - 메타데이터가 수집되지 않은 동명(말단) 테이블.
            var columnless = new DependencyInfo { Name = "TSettleMst", Schema = "dbo", Database = "DB2", Type = "USER_TABLE" };
            sp.Dependencies.Add(columnless);

            return sp;
        }

        [Fact]
        public void Validate_WhenColumnlessSiblingSharesLastName_TrueAbsenceClaim_ShouldStaySilent()
        {
            // Arrange - 리뷰 실측 시나리오 ①. "DB2.dbo.TSettleMst의 스키마는 제공되지
            // 않아 CLINTCOMM 컬럼이 존재하지 않습니다"는 참인 진술이다(DB2는 컬럼 0개
            // 의존성이라 스키마 표 자체가 없다). 컬럼 0개 의존성이 말단 이름 모호성
            // 판정에서 빠지면 DB1 하나만 후보로 남아 DB1에 대한 거짓 오류로 오귀속된다.
            var sp = BuildSettleMstWithColumnlessSibling();
            var expectations = SpecExpectations.From(sp)!;
            var markdown = WrapSpec(
                "`DB2.dbo.TSettleMst`의 스키마는 제공되지 않아 `CLINTCOMM` 컬럼이 존재하지 않습니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenColumnlessSiblingSharesLastName_CrudRowPair_ShouldNotBeTreatedAsIdentitySplit()
        {
            // Arrange - 리뷰 실측 시나리오 ②. DB1.dbo.TSettleMst 행과
            // DB2.dbo.TSettleMst(컬럼 0개) 행은 서로 다른 물리 테이블이지 표기 분열이
            // 아니다. 컬럼 0개 의존성이 대조 집합에서 빠지면 DB2 행이 말단 이름만으로
            // DB1 키에 오귀속되어 두 표기가 한 테이블 아래 뭉쳐 TableIdentitySplit이
            // 잘못 발생한다.
            var sp = BuildSettleMstWithColumnlessSibling();
            var expectations = SpecExpectations.From(sp)!;
            var markdown = WrapSpec(
                "### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n|---|---|\n" +
                "| `DB1.dbo.TSettleMst` | `CLINTCOMM` |\n" +
                "| `DB2.dbo.TSettleMst` | `CLINTCOMM` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }

        // [Minor] 같은 canonical 이름이 PromptSchemaColumns와 ColumnlessDependencyTables에
        // 동시에 들어가면 ResolveSchemaTableKey가 자기 자신과 충돌한다고 오판해 항상
        // null을 돌려준다. SpecExpectations.From은 한 canonical을 두 집합 중 하나에만
        // 넣으므로 이 상태를 만들 수 없다 - 레코드를 직접 구성해야 재현된다.

        [Fact]
        public void Validate_WhenSameCanonicalIsInBothColumnSetAndColumnlessSet_ShouldNotSelfConflict()
        {
            // Arrange - 같은 canonical(DB1.dbo.TSettleMst)이 컬럼 보유 집합과 컬럼 0개
            // 집합에 동시에 실린 상태를 직접 구성한다. 자기 자신과의 충돌은 충돌이
            // 아니어야 하므로, 비한정 이름 `TSettleMst`에 대한 거짓 부재 주장은
            // 여전히 발화해야 한다(= 귀속이 null로 무력화되지 않았다).
            const string canonical = "DB1.dbo.TSettleMst";
            var promptSchemaColumns = new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [canonical] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CLINTCOMM" }
            };
            var columnlessDependencyTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                canonical
            };
            var expectations = new SpecExpectations(
                new List<UpdateColumnExpectation>(),
                promptSchemaColumns,
                columnlessDependencyTables,
                new List<string>());
            var markdown = WrapSpec(
                "`TSettleMst`의 스키마에 `CLINTCOMM`은 존재하지 않습니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        // [Minor] H4(####) 하위 절 경계에서 flush하지 않아 조회/갱신 절이 한 버킷으로
        // 합쳐진다. "### "만 경계로 취급하는 것이 원인이다. 프롬프트(AiService.cs)는
        // 하위 절 분리를 요구하면서 헤딩 레벨은 고정하지 않는다.

        [Fact]
        public void Validate_WhenSpellingsAreInDifferentH4Subsections_ShouldPass()
        {
            // Arrange - 실측된 오탐: "#### 조회 대상 테이블"과 "#### 갱신 대상 테이블"로
            // 쓰면(H4) 절 경계에서 flush되지 않아 같은 테이블의 두 표기가 한 절로
            // 뭉쳐 TableIdentitySplit이 잘못 발생했다. "###"로 쓴 동일 문서는 통과한다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "PLTID");
            var markdown = WrapSpec(
                "#### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n|---|---|\n| `DB.dbo.TSettleMst` | `PLTID` |\n\n" +
                "#### 갱신 대상 테이블\n\n" +
                "| 테이블명 | 갱신 컬럼 |\n|---|---|\n| `dbo.TSettleMst` | `PLTID` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }

        // [Minor] H5(#####) 이상 하위 절 경계에서도 flush하지 않는다. "### "와 "#### "
        // 두 레벨만 열거한 것이 원인이다. 프롬프트(AiService.cs)는 하위 절 분리를
        // 요구하면서 헤딩 레벨은 고정하지 않으므로 H5도 산출될 수 있다.

        [Fact]
        public void Validate_WhenSpellingsAreInDifferentH5Subsections_ShouldPass()
        {
            // Arrange - 실측된 오탐: "##### 조회 대상 테이블"과 "##### 갱신 대상 테이블"로
            // 쓰면(H5) 절 경계에서 flush되지 않아 같은 테이블의 두 표기가 한 절로
            // 뭉쳐 TableIdentitySplit이 잘못 발생했다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "PLTID");
            var markdown = WrapSpec(
                "##### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n|---|---|\n| `DB.dbo.TSettleMst` | `PLTID` |\n\n" +
                "##### 갱신 대상 테이블\n\n" +
                "| 테이블명 | 갱신 컬럼 |\n|---|---|\n| `dbo.TSettleMst` | `PLTID` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }

        // [Minor] 코드 펜스 안의 문장도 검사 대상이었다. LocateCrudSection은
        // MarkdownSectionLocator.FindIndexOutsideFence로 펜스를 추적하는데, 이 두 검사의
        // 줄 순회는 추적하지 않아 한 파일 안에서 판정 기준이 갈렸다.

        [Fact]
        public void Validate_WhenAbsenceClaimAppearsInsideCodeFence_ShouldBeIgnored()
        {
            // Arrange - 실측된 오탐: ```sql 블록 안 주석에 부재 주장 문구가 있어도
            // 오류로 잡혔다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec(
                "```sql\n" +
                "-- `dbo.TSettleMst` 스키마에 `CLINTCOMM`이 존재하지 않는 환경 대비\n" +
                "SELECT 1\n" +
                "```");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenTableCellLookingTextAppearsInsideCodeFence_ShouldNotCountTowardIdentitySplit()
        {
            // Arrange - 코드 펜스 안에 표 셀처럼 보이는 텍스트(예시 SQL 주석)가 있어도
            // 실제 CRUD 표 밖이므로 표기 분열 집계에 들어가면 안 된다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "PLTID");
            var markdown = WrapSpec(
                "### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n|---|---|\n| `dbo.TSettleMst` | `PLTID` |\n\n" +
                "```text\n" +
                "| `TSettleMst` | `PLTID` |\n" +
                "```");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }

        // 이 검사의 미덕은 목차가 필요 없다는 것이다. 명세서에서 직접 뽑으므로
        // 목차가 어떻게 망가지든 살아남는 유일한 검사다. POQSettleProc7에서 원본
        // 오류코드 76개 중 20개가 사라졌는데 아무도 알리지 않았다.
        [Fact]
        public void FindMissingErrorCodes_ReportsOnlyCodesAbsentFromTheWholeDocument()
        {
            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["UP_A"] = new[] { "-1", "-2", "-3" },
                ["UP_B"] = new[] { "-9" }
            };
            var document = "S01은 `-1`을 반환하고 `-3`도 반환한다. `-9`는 UP_B의 코드다.";

            var missing = MechanicalValidator.FindMissingErrorCodes(document, codes);

            var only = Assert.Single(missing);
            Assert.Equal("UP_A", only.Key);
            Assert.Equal(new[] { "-2" }, only.Value);
        }

        [Fact]
        public void FindMissingErrorCodes_WhenEveryCodeIsPresent_ReturnsEmpty()
        {
            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["UP_A"] = new[] { "-1", "-2" }
            };

            var missing = MechanicalValidator.FindMissingErrorCodes("`-1` `-2`", codes);

            Assert.Empty(missing);
        }

        // -1이 -10 안에서 오탐되면 진짜 누락이 통과한다. 단계별 검사와 같은
        // ContainsToken을 써야 두 경로의 판정이 갈리지 않는다.
        [Fact]
        public void FindMissingErrorCodes_DoesNotMatchACodeInsideALongerNumber()
        {
            var codes = new Dictionary<string, IReadOnlyList<string>>
            {
                ["UP_A"] = new[] { "-1" }
            };

            var missing = MechanicalValidator.FindMissingErrorCodes("반환값은 `-10`이다.", codes);

            var only = Assert.Single(missing);
            Assert.Equal(new[] { "-1" }, only.Value);
        }

        [Fact]
        public void Validate_ThreePartClaimWithoutAnyThreePartReference_ShouldBeAnError()
        {
            // STAT_PGCOLLECT_INS 실측. 원본은 전부 1부 표기인데 Spec이 3부
            // 크로스 데이터베이스 참조라고 단언했다.
            var expectations = EmptyExpectations() with
            {
                HasThreePartReference = false,
                HasLinkedServerReference = false
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n이 프로시저는 3부 식별자 기반 크로스 데이터베이스 참조이며 Linked Server 원격 참조가 아닙니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.IdentifierNotationClaim);
        }

        [Fact]
        public void Validate_TruthfulDenialOfThreePartNotation_ShouldNotBeAnError()
        {
            // 리뷰 실측: 원본에 3부 참조가 없다는 것을 정직하게 부정하는 명세서가
            // 오탐으로 걸리면, 재생성으로도 L1을 통과시킬 방법이 없다 - 모델이 참을
            // 거짓으로 바꿔야만 통과하게 된다. AiService.cs의 Linked Server 안내문이
            // 이런 "~이 아닙니다" 부정 어투를 이미 권장하고 있어 이 표현이 정상적으로
            // 나올 수 있다.
            var expectations = EmptyExpectations() with
            {
                HasThreePartReference = false,
                HasLinkedServerReference = false
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n이 프로시저는 3부 식별자를 사용하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.IdentifierNotationClaim);
        }

        [Fact]
        public void Validate_ThreePartClaimWithAThreePartReference_ShouldPass()
        {
            var expectations = EmptyExpectations() with { HasThreePartReference = true };
            var markdown = RequiredHeadersMarkdown()
                + "\n이 프로시저는 3부 식별자로 다른 데이터베이스를 참조합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.IdentifierNotationClaim);
        }

        [Fact]
        public void Validate_ThreePartClaimNegatedWithNominalForm_ShouldNotBeAnError()
        {
            // UF_GET_ROUND4VAT/docs/Spec.md:107 실측: "세 부분 식별자를 사용하는 동일
            // 서버 내 다른 데이터베이스 참조가 없음"처럼 표 셀은 종결형("없습니다")이
            // 아니라 명사형 부정("없음")을 쓴다. NegationTokens가 종결형만 담으면 이
            // 정직한 문장이 거짓 단언으로 오판된다.
            var expectations = EmptyExpectations() with
            {
                HasThreePartReference = false,
                HasLinkedServerReference = false
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n| 다른 데이터베이스 | 없음 | 세 부분 식별자를 사용하는 동일 서버 내 "
                + "다른 데이터베이스 참조가 없음 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.IdentifierNotationClaim);
        }

        [Fact]
        public void Validate_ThreePartClaimAndNegationSeparatedByAComma_ShouldNotBeAnError()
        {
            // 리뷰 실측 예시 형태: "크로스 데이터베이스 참조, Linked Server 원격 참조
            // 모두 없습니다"처럼 콤마로 나열한 대상을 공유 서술어 하나로 부정하는
            // 문장을, 콤마를 절 경계로 쪼개면 앞 절엔 주장만 남고 부정은 뒤 절에만
            // 남아 거짓 단언으로 오판된다.
            var expectations = EmptyExpectations() with
            {
                HasThreePartReference = false,
                HasLinkedServerReference = false
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n3부 식별자 기반 크로스 데이터베이스 참조, Linked Server 원격 참조 모두 "
                + "없습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.IdentifierNotationClaim);
        }

        [Fact]
        public void Validate_ThreePartClaimNegatedAfterAPeriodInsideAnIdentifier_ShouldNotBeAnError()
        {
            // SplitIntoClauses가 "dbo.UP_Legacy"의 마침표를 절 경계로 오인하면, 부정
            // "참조하지 않습니다"가 앞 절("3부 식별자")과 분리되어 정직한 부정문이
            // 거짓 단언으로 오판된다. CheckHeaderContractContradiction의
            // SentenceBoundaryRegex(Fix Round 3)가 이미 쓰는 "뒤에 공백/줄끝이 와야
            // 마침표를 경계로 센다"는 규칙을 여기도 적용해야 한다.
            var expectations = EmptyExpectations() with
            {
                HasThreePartReference = false,
                HasLinkedServerReference = false
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n이 프로시저는 3부 식별자로 dbo.UP_Legacy를 참조하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.IdentifierNotationClaim);
        }

        [Fact]
        public void Validate_MissingCommentAnchor_ShouldBeAnError()
        {
            var expectations = EmptyExpectations() with
            {
                SourceComments = new[]
                {
                    new SourceCommentBlock(
                        "NonExecutable",
                        "AND ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET())",
                        12,
                        new[] { "UF_GET_CLIENTID4TMONET" })
                }
            };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SourceCommentMissing);
        }

        [Fact]
        public void Validate_CommentAnchorPresent_ShouldPass()
        {
            var expectations = EmptyExpectations() with
            {
                SourceComments = new[]
                {
                    new SourceCommentBlock(
                        "NonExecutable", "AND ClientID NOT IN (...)", 12,
                        new[] { "UF_GET_CLIENTID4TMONET" })
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n주석 처리된 조건은 `dbo.UF_GET_CLIENTID4TMONET()`를 호출하며 실행되지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SourceCommentMissing);
        }

        [Fact]
        public void Validate_CodeLegendAnchorWrittenWithBackticksAndSpacing_ShouldPass()
        {
            // 실측: UF_GET_PGCommOption/docs/Spec.md:43-44,74. 생성기가 실제로 쓰는
            // 서식은 `1`: `CommMethod`(백틱·콜론 뒤 공백 포함)인데, 앵커는 추출
            // 시점의 원시 리터럴 "1:CommMethod"(백틱·공백 없음)다. 문자 그대로의
            // 부분 문자열 대조만 쓰면 정확히 옮겨 적은 문서가 오탐으로 떨어진다.
            var expectations = EmptyExpectations() with
            {
                SourceComments = new[]
                {
                    new SourceCommentBlock(
                        "CodeLegend", "1:CommMethod, 2:CommStandard", 74,
                        new[] { "1:CommMethod", "2:CommStandard" })
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n| `@pi_intOptionFlag` | 옵션 | `1`: `CommMethod`, `2`: `CommStandard` |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SourceCommentMissing);
        }

        [Fact]
        public void Validate_CodeLegendAnchorTrulyAbsent_ShouldStillBeAnError()
        {
            // 위 테스트의 짝 - 관용성을 너무 넓혀 진짜 누락까지 통과시키지 않았는지
            // 증명한다.
            var expectations = EmptyExpectations() with
            {
                SourceComments = new[]
                {
                    new SourceCommentBlock("CodeLegend", "1:CommMethod", 74, new[] { "1:CommMethod" })
                }
            };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SourceCommentMissing);
        }

        [Fact]
        public void Validate_AnchorlessProseComment_ShouldNotBeChecked()
        {
            // 앵커가 없는 항목은 L1이 손대지 않는다.
            var expectations = EmptyExpectations() with
            {
                SourceComments = new[]
                {
                    new SourceCommentBlock("Prose", "매입요청일(D)+1 : 집계 고려", 7, Array.Empty<string>())
                }
            };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SourceCommentMissing);
        }

        [Fact]
        public void Validate_RoundWithoutTruncationSemantics_ShouldBeAnError()
        {
            var expectations = EmptyExpectations() with
            {
                RoundingCalls = new[] { new RoundingCall(63, "dbo.UF_GET_PGCommOption(A.PGName)") }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\nPG 수수료 반올림 옵션으로 정수화합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.RoundingSemanticsMissing);
        }

        [Theory]
        [InlineData("절사")]
        [InlineData("버림")]
        [InlineData("내림")]
        [InlineData("truncate")]
        public void Validate_RoundWithTruncationSemantics_ShouldPass(string synonym)
        {
            // INS_EXTRA4PLCARD의 Spec이 이 매핑을 정확히 기록한 반례다(골든 케이스).
            var expectations = EmptyExpectations() with
            {
                RoundingCalls = new[] { new RoundingCall(63, "dbo.UF_GET_PGCommOption(A.PGName)") }
            };
            var markdown = RequiredHeadersMarkdown()
                + $"\n세 번째 인자가 0이면 반올림, 0이 아니면 {synonym}합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.RoundingSemanticsMissing);
        }

        [Fact]
        public void Validate_RoundWithOnlyDescendingOrderMention_ShouldStillBeAnError()
        {
            // 리뷰 실측(Fix Round 1): "내림"은 "내림차순"의 부분 문자열이다. 단순
            // substring 매칭이면 ORDER BY 방향 서술("생성일자 내림차순으로 정렬")이
            // 절사 의미 서술로 오인되어 결함이 은폐된다. 실측 코퍼스에도
            // UF_GET_CLIENTSECTIONRATE.Spec.md:157의 "내림차순으로"가 실재한다 -
            // 이 문서 자체는 3인자 ROUND가 없어 오늘은 영향이 없지만, ROUND와 ORDER BY
            // 서술이 함께 있는 미래의 SP는 이 결함에 노출된다.
            var expectations = EmptyExpectations() with
            {
                RoundingCalls = new[] { new RoundingCall(63, "dbo.UF_GET_PGCommOption(A.PGName)") }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n결과는 생성일자 내림차순으로 정렬합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.RoundingSemanticsMissing);
        }

        [Fact]
        public void Validate_RoundWithGenuineDescendingWordAdjacentToOrderMention_ShouldPass()
        {
            // 위 테스트의 짝 - 오탐(위양성)으로 되돌아가지 않았는지 증명한다. 같은
            // 문서에 "내림차순" 언급이 있어도, 진짜 절사 의미 서술("0이 아니면
            // 내림합니다")이 별도로 있으면 그 등장은 여전히 인정되어야 한다.
            var expectations = EmptyExpectations() with
            {
                RoundingCalls = new[] { new RoundingCall(63, "dbo.UF_GET_PGCommOption(A.PGName)") }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n결과는 생성일자 내림차순으로 정렬합니다. 세 번째 인자가 0이면 반올림, "
                + "0이 아니면 내림합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.RoundingSemanticsMissing);
        }

        [Fact]
        public void Validate_NoRoundingCalls_ShouldNotCheckSemantics()
        {
            // 재료가 비면 소프트 스킵이다 - ROUND가 없는 대다수 SP에서 거짓 결함이
            // 나면 안 된다.
            var expectations = EmptyExpectations();

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.RoundingSemanticsMissing);
        }

        [Fact]
        public void Validate_MissingSessionOption_ShouldBeAnError()
        {
            var expectations = EmptyExpectations() with { SessionOptions = new[] { "NOCOUNT" } };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SessionOptionMissing);
        }

        [Fact]
        public void Validate_StatedSessionOption_ShouldPass()
        {
            var expectations = EmptyExpectations() with { SessionOptions = new[] { "NOCOUNT" } };
            var markdown = RequiredHeadersMarkdown()
                + "\n`SET NOCOUNT ON`으로 행 수 메시지를 억제합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SessionOptionMissing);
        }

        [Fact]
        public void Validate_NoSessionOptions_ShouldNotCheckSessionOptions()
        {
            // 재료가 비면 소프트 스킵이다 - 세션 옵션이 없는 대다수 SP에서 거짓 결함이
            // 나면 안 된다.
            var expectations = EmptyExpectations();

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SessionOptionMissing);
        }

        [Fact]
        public void Validate_MissingNormalizedTransactionIsolationLevelOption_ShouldBeAnError()
        {
            // 위험 4: TRANSACTION ISOLATION LEVEL은 여러 단어이고 추출기가 내부 공백을
            // 단일 공백으로 정규화한다. L1 대조 기준도 같은 정규화 문자열을 써야 한다 -
            // 그렇지 않으면 이 검사가 영원히 통과하지 못하는 문자열을 요구하게 된다.
            var expectations = EmptyExpectations()
                with { SessionOptions = new[] { "TRANSACTION ISOLATION LEVEL" } };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SessionOptionMissing);
        }

        [Fact]
        public void Validate_StatedNormalizedTransactionIsolationLevelOption_ShouldPass()
        {
            var expectations = EmptyExpectations()
                with { SessionOptions = new[] { "TRANSACTION ISOLATION LEVEL" } };
            var markdown = RequiredHeadersMarkdown()
                + "\n`SET TRANSACTION ISOLATION LEVEL READ COMMITTED`으로 격리 수준을 지정합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SessionOptionMissing);
        }

        [Fact]
        public void Validate_HeaderClaimsNoInternalCallButExecExists_ShouldBeAnError()
        {
            // Util_Settle_Summary 실측 - 헤더 주석이 내부 SP 호출을 NONE이라 선언하는데
            // 실제로는 EXEC가 둘 있다. 명세서는 두 EXEC를 정확히 적었지만 헤더가
            // 모순된다는 사실 자체는 적지 않았다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n이 프로시저는 두 개의 하위 프로시저를 EXEC로 호출합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_HeaderContradictionAcknowledged_ShouldPass()
        {
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n헤더 주석은 내부 SP 호출이 NONE이라 선언하나 실제로는 EXEC가 둘 있어 "
                + "주석이 구현과 모순됩니다(스테일 주석).\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Theory]
        // Fix Round 1 - 독립 리뷰 실측. 아래 7개는 헤더/EXEC 모순을 인정하는 자연스러운
        // 한국어 표현들이다. 원래 토큰 4개(모순, 스테일, 다릅니다, 어긋)만으로는 이 중
        // 5개가 "인정하지 않음"으로 거짓 결함 처리됐다 - 모순을 정확히 기록한 문서를
        // 틀렸다고 판정하는 오탐이다. 이 테스트가 7개 전부를 통과로 요구한다.
        [InlineData("헤더 주석은 내부 SP 호출이 없다고 하나 실제로는 두 개를 호출한다.")]
        [InlineData("헤더 주석은 Inner SP: NONE이라 되어 있지만 실제로는 두 개의 프로시저를 EXEC로 호출합니다.")]
        [InlineData("주석과 실제 구현이 맞지 않습니다.")]
        [InlineData("헤더 주석이 오래되어 실제 구현을 반영하지 못합니다.")]
        [InlineData("헤더 주석의 선언과 실제 구현 사이에 차이가 있습니다.")]
        [InlineData("이는 헤더 주석과 모순됩니다.")]
        [InlineData("헤더 주석이 실제와 어긋납니다.")]
        public void Validate_NaturalAcknowledgementPhrasings_ShouldPass(string acknowledgement)
        {
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown() + "\n" + acknowledgement + "\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_UnrelatedMismatchPhraseDoesNotAcknowledgeTheHeaderContradiction_ShouldStillBeAnError()
        {
            // Fix Round 1 리뷰 실측(Task 6의 "내림"/"내림차순"과 같은 모양) - 후보 토큰
            // "일치하지 않"은 UP_Util_Settle_Summary의 실제 명세서(output/Procedures/
            // dbo.UP_Util_Settle_Summary/docs/Spec.md:278)에 "원천과 일치하지 않는
            // 값이 전달되면"이라는, 입력 검증을 말하는 문장으로 이미 등장한다. 그
            // 우연한 등장이 헤더 모순 인정으로 잘못 인정되면, 헤더가 모순된다는
            // 사실을 한 번도 적지 않은 바로 그 동기 사례 문서가 통과해 버린다 - 이
            // 검사가 막으려는 결함이 검사 자체의 허점으로 새어 나가는 것과 같다.
            // 그래서 "일치하지 않"은 인정 토큰 목록에서 뺐고, 이 테스트가 그 결정이
            // 되돌려지지 않았음을 증명한다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n이 프로시저는 두 개의 하위 프로시저를 EXEC로 호출합니다. "
                + "입력 날짜의 형식과 유효성을 검사하지 않습니다. NULL 또는 원천과 "
                + "일치하지 않는 값이 전달되면 직접 삭제·삽입 대상이 없을 수 있습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_UnrelatedDifferenceSentenceElsewhereInDocument_ShouldStillBeAnError()
        {
            // Fix Round 2 리뷰 실측. Round 1에서 넓힌 "실제로는"·"차이가 있" 같은
            // 토큰은 문서 전체(Contains)를 대상으로 하면 헤더와 무관한 문장에서도
            // 매치된다 - 리뷰가 이 정확한 문장으로 증명했다: 아래 "정산 금액..."
            // 문장은 원 단위 절사로 인한 두 집계값의 차이를 말할 뿐 헤더 주석과는
            // 아무 관계가 없는데도, 문서 전체 Contains로는 "차이가 있"이 매치되어
            // 진짜 헤더/EXEC 모순 신고를 조용히 삼켜 버렸다(DetailedErrors가
            // 비어짐). CheckHeaderContractContradiction이 이제 문장 단위로 인정
            // 토큰과 헤더 지시어(헤더·주석·Inner SP·NONE)의 공존을 요구하므로, 이
            // 무관한 문장은 인정으로 세지 않는다 - 결함은 그대로 신고돼야 한다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n이 프로시저는 두 개의 하위 프로시저를 EXEC로 호출합니다. "
                + "입력 날짜의 형식과 유효성을 검사하지 않습니다. NULL 또는 원천과 "
                + "일치하지 않는 값이 전달되면 직접 삭제·삽입 대상이 없을 수 있습니다.\n"
                + "정산 금액 계산 시 원 단위 절사로 인해 두 번째 집계와 세 번째 집계 "
                + "사이에 차이가 있습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_AcknowledgementSentenceWithDottedInternalCallIdentifier_ShouldPass()
        {
            // Fix Round 3 리뷰 실측. Round 2가 문장 경계로 바로 "."을 썼는데, 이
            // 코퍼스는 "dbo.UP_X" 같은 점(.) 포함 식별자가 산문 안에 흔하다(실측:
            // 26개 실제 명세서에서 문서당 45~65회). 그 점을 문장 경계로 오인하면
            // 인정 문장 하나가 "dbo"까지와 "UP_..." 이후로 쪼개져, 헤더 지시어와
            // 인정 토큰이 서로 다른 조각으로 갈라진다 - 정확히 인정한 문서를 틀렸다고
            // 판정하는 거짓 양성이다. 이 문장은 cbd6a5d(Round 2 팁)에서 거짓으로
            // 결함 처리됐다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n헤더 주석은 dbo.UP_Util_Settle_Summary_AcqManual을 호출하지 않는다고 "
                + "하나 실제로는 호출합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_LeakedPromptInstruction_ShouldBeAnError()
        {
            // 2026-08-18 축 A 감사 실측. UPDATE 매핑 표 블록은 "그대로 베끼라"는 지시를
            // 받는데 그 안에 작성 지시문이 섞여 있어 모델이 함께 옮겨 적었다 -
            // COMM_UPD 17곳, INS_EXTRA 5곳, INS_EXTRA4PLCARD 3곳. 지시문을 영어로
            // 되돌리고 표지를 붙이는 것은 규칙일 뿐이라, 설계 §0대로 검사를 짝지운다.
            var markdown = RequiredHeadersMarkdown()
                + "\n" + MechanicalValidator.PromptInstructionMarker
                + " This statement has a FROM clause: the update target is ...\n";

            var result = new MechanicalValidator().Validate(markdown, null);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.PromptInstructionLeak);
        }

        [Fact]
        public void Validate_WithoutLeakedInstruction_ShouldNotFlag()
        {
            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), null);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.PromptInstructionLeak);
        }

        [Fact]
        public void Validate_AcknowledgementUsingBulilchi_ShouldPass()
        {
            // 2026-08-17 전수 재생성 실측. 14개 SP를 새 파이프라인으로 돌렸을 때 유일한
            // L1 실패가 이 검사였고, 오탐이었다. 아래 문장은 그때 모델이 실제로 쓴 것을
            // 그대로 옮긴 것이다(UP_Util_Settle_Summary.Spec.md:26). 모순을 정확히
            // 적었는데도 인정 토큰 목록에 "불일치"가 없어 3회 재시도가 모두 거부됐고,
            // 명세서는 L1미통과로 출고됐다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 5, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n- **내부 프로시저 주석 불일치:** 원본 헤더 주석에는 `Inner SP        : NONE`으로 "
                + "선언되어 있으나 실제 구현은 `dbo.UP_Util_Settle_Summary_AcqManual` 및 "
                + "`dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA`를 순차 호출합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_AcknowledgementSentenceWithDateInParentheses_ShouldPass()
        {
            // 위 테스트의 짝 - 점(.) 포함 날짜(2021.11.29)도 같은 함정이다. 숫자
            // 사이의 점은 문장 경계가 아니다. 이 문장도 cbd6a5d에서 거짓으로 결함
            // 처리됐다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n헤더 주석(2021.11.29 기준)은 내부 SP 호출이 없다고 하나 실제로는 "
                + "두 개를 호출합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_TableRowsWithTokenAndHeaderTermInDifferentRows_ShouldStillBeAnError()
        {
            // Fix Round 3 - 줄바꿈을 경계로 잡지 않으면 재현되는 Round 1형 거짓
            // 음성이다. 표 형식 명세서는 논리적 진술마다 마침표 없는 별도 행을 쓴다
            // (실측: 코퍼스 대다수 표가 이 모양). 아래 표에서 "헤더" 언급은 3행에,
            // "차이가 있" 토큰은 4행에 있고 서로 무관하다 - 줄바꿈을 경계로 잡지
            // 않으면 두 행이 마침표 없이 한 "문장"으로 붙어 버려 거짓으로 인정
            // 처리된다. 이 테스트는 줄바꿈 경계 채택이 그 구멍을 막는지 증명한다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n| 항목 | 설명 |\n"
                + "| --- | --- |\n"
                + "| 헤더 주석 검토 | 별도 확인이 필요합니다 |\n"
                + "| 정산 처리 | 두 번째 집계와 세 번째 집계 사이에 차이가 있습니다 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_TwoBulletAcknowledgement_IsAKnownLimitation_AndStillReportsTheContradiction()
        {
            // Fix Round 4 - 알려진 한계, 고쳐지지 않았다(원하는 동작이 아니다).
            // 자세한 이유는 CheckHeaderContractContradiction의 "[Fix Round 4 - 알려진
            // 한계]" 주석 참고. 아래 명세서는 헤더/EXEC 모순을 실제로는 인정했다 -
            // 첫 불릿이 헤더의 NONE 선언을, 둘째 불릿이 실제 EXEC 존재를 말한다.
            // 그런데도 이 검사는 결함을 신고한다 - 두 불릿이 문장 경계(줄바꿈)로
            // 갈라져 헤더 지시어와 인정 토큰이 같은 문장에 없기 때문이다. Round
            // 3이 표 행 분리(위 테스트)를 막으려고 줄바꿈을 경계로 추가하면서
            // 얻은 대가다 - 언젠가 고쳐지면 이 어서션(Assert.Contains)이
            // 실패한다. 그 실패는 "좋은 소식이니 테스트를 갱신하라"로 읽어야
            // 한다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n- 헤더 주석은 내부 SP 호출이 없다고 선언한다\n"
                + "- 그러나 실제로는 두 개를 호출한다\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_NumberedListAcknowledgement_IsAKnownLimitation_AndStillReportsTheContradiction()
        {
            // Fix Round 4 - 알려진 한계, 고쳐지지 않았다(원하는 동작이 아니다).
            // 자세한 이유는 CheckHeaderContractContradiction의 "[Fix Round 4 - 알려진
            // 한계]" 주석 참고. 위 두 불릿 케이스와 달리 이 한계는 Round 3(줄바꿈
            // 경계 도입) 이전부터 있었다 - "1."·"2." 항목 표지 자체가 마침표+공백
            // 이라 Round 2의 마침표-단독 경계 규칙에서도 이미 두 항목이 갈라졌다.
            // 아래 명세서는 실제로는 모순을 인정했는데도 이 검사는 결함을
            // 신고한다. 언젠가 고쳐지면 이 어서션이 실패한다 - "좋은 소식이니
            // 테스트를 갱신하라"로 읽어야 한다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n1. 헤더 주석은 내부 SP 호출이 없다고 선언한다\n"
                + "2. 그러나 실제로는 두 개를 호출한다\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_TwoSentenceAcknowledgement_IsAKnownLimitation_AndStillReportsTheContradiction()
        {
            // Fix Round 4 - 알려진 한계, 고쳐지지 않았다(원하는 동작이 아니다).
            // 자세한 이유는 CheckHeaderContractContradiction의 "[Fix Round 4 - 알려진
            // 한계]" 주석 참고. 이 한계는 Round 2가 "같은 문장" 요구를 도입한
            // 순간부터 있었다 - 아래는 마침표 하나로 정말 두 문장이 나뉘어 있고,
            // 첫 문장이 헤더의 NONE 선언을, 둘째 문장이 실제 EXEC 존재를 말한다.
            // 실제로는 모순을 인정했는데도 이 검사는 결함을 신고한다. 언젠가
            // 고쳐지면 이 어서션이 실패한다 - "좋은 소식이니 테스트를
            // 갱신하라"로 읽어야 한다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n헤더 주석은 내부 SP 호출이 없다고 선언한다. 그러나 실제로는 "
                + "두 개를 호출한다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_NoInternalProcedureCall_ShouldNotCheckHeaderContractContradiction()
        {
            // 재료가 비면 소프트 스킵이다 - 내부 SP 호출이 없는 대다수 SP에서 거짓
            // 결함이 나면 안 된다.
            var expectations = EmptyExpectations() with
            {
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : NONE", 3, Array.Empty<string>())
                }
            };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void Validate_HeaderDoesNotClaimNone_ShouldNotCheckHeaderContractContradiction()
        {
            // 헤더가 NONE을 선언하지 않으면 대조할 모순이 없다 - 내부 SP 호출이
            // 있어도 이 패턴 밖이다.
            var expectations = EmptyExpectations() with
            {
                HasInternalProcedureCall = true,
                SourceComments = new[]
                {
                    new SourceCommentBlock("Header", "Inner SP        : dbo.UP_Other", 3, Array.Empty<string>())
                }
            };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.HeaderContractContradiction);
        }

        [Fact]
        public void From_WhenTheOnlyMaterialIsARoundingCall_ShouldNotBeNullAndShouldCarryTheCalls()
        {
            // Task 4가 세운 조기 반환 함정의 재현이다. roundingCalls를 조기 반환
            // 조건에 잇지 않으면 이 새 재료가 유일하게 있는 SpDefinition도 null을
            // 받아 CheckRoundingSemantics가 한 번도 돌지 않는다 - 이 테스트가 그
            // 배선이 실제로 넓혀졌는지를 From()을 통해 증명한다.
            //
            // [Fix Round 1] UPDATE/DELETE 문장을 쓰지 않는다 - Task 9가 더한
            // DmlScopeExtractor가 UPDATE/DELETE라면 무조건 사실을 하나 만들어 내어
            // (dmlScopeFacts.Count == 0) 항이 이미 false가 되고, roundingCalls 항을
            // 조기 반환식에서 지워도 이 테스트가 여전히 통과하는 거짓 안전망이
            // 된다. ROUND는 SELECT 대입식 안에서도 그대로 잡힌다 - 3인자 ROUND
            // 호출이라는 이 테스트의 본 주제는 그대로 살아 있다.
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT @PGComm = ROUND(A.TxAmt * B.Rate / 100, 0, dbo.UF_GET_PGCommOption(A.PGName))
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.Empty(expectations!.DmlScopeFacts);
            var call = Assert.Single(expectations.RoundingCalls);
            Assert.Contains("UF_GET_PGCommOption", call.ThirdArgument);
        }

        [Fact]
        public void From_WhenTheOnlyMaterialIsASourceComment_ShouldNotBeNullAndShouldCarryTheComments()
        {
            // Task 4가 세운 조기 반환 함정의 재현이다. SpecExpectations.From은 재료가
            // 전부 비면 null을 돌려주고 호출부는 null을 "대조 건너뜀"으로 받는다.
            // sourceComments를 조기 반환 조건에 잇지 않으면 이 새 재료가 유일하게
            // 있는 SpDefinition도 null을 받아 L1(CheckSourceComments)이 한 번도 돌지
            // 않는다 - 이 테스트가 그 배선이 실제로 넓혀졌는지를 From()을 통해 증명한다.
            //
            // [Fix Round 1] UPDATE/DELETE 문장을 쓰지 않는다 - Task 9가 더한
            // DmlScopeExtractor가 UPDATE/DELETE라면 무조건 사실을 하나 만들어 내어
            // (dmlScopeFacts.Count == 0) 항이 이미 false가 되고, sourceComments 항을
            // 조기 반환식에서 지워도 이 테스트가 여전히 통과하는 거짓 안전망이
            // 된다. SourceCommentExtractor는 CREATE 이후의 주석 줄을 문장 종류와
            // 무관하게 훑으므로, 앵커를 나르는 이 비실행 주석은 어떤 문장 아래에
            // 두어도 그대로 잡힌다 - 이 테스트의 본 주제는 그대로 살아 있다.
            //
            // [2026-08-22 축 A 재감사 ③ Task 4] FROM이 있는 독립 SELECT도 이제
            // DML 범위 사실을 하나 만든다(커서 원천의 ORDER BY를 담기 위해서다).
            // 그래서 신호를 하나만 남기려면 FROM이 없는 대입 SELECT여야 한다 -
            // `DmlScopeExtractor.HasFromClause`가 그 문장을 세지 않으므로
            // dmlScopeFacts가 비고, 잠금 힌트도 훑을 FROM이 없어 비어 있다.
            // 격리는 눈이 아니라 실측으로 확인했다(수정 라운드 1): SpecExpectations의
            // `&& sourceComments.Count == 0` 항을 임시로 지우면 이 테스트가 빨개진다 -
            // 즉 이 픽스처에서 From을 non-null로 만드는 재료는 주석 하나뿐이다.
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT @v_ID = 1
    --AND ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET()) --예외처리 제거(2021.11.29)
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.Empty(expectations!.DmlScopeFacts);
            Assert.Empty(expectations.LockHints);
            var block = Assert.Single(expectations.SourceComments);
            Assert.Contains("UF_GET_CLIENTID4TMONET", block.Anchors);
        }

        [Fact]
        public void From_WhenTheOnlyMaterialIsAThreePartReference_ShouldNotBeNullAndShouldCarryTheFlag()
        {
            // SpecExpectations.From은 세 재료(UpdateColumns, PromptSchemaColumns,
            // InputDefects)가 전부 비면 null을 돌려주고 호출부는 null을 "대조 건너뜀"으로
            // 받는다. ThreePartObjectReferences를 조기 반환 조건에 잇지 않으면 이 새 재료가
            // 유일하게 있는 SpDefinition도 null을 받아 L1이 한 번도 돌지 않는다 -
            // 이 테스트가 그 배선이 실제로 넓혀졌는지를 From()을 통해 증명한다.
            var analysis = new SpStaticAnalysisResult();
            analysis.ThreePartObjectReferences.Add("SETTLE_POQ_DB.dbo.TSettleMst");
            var sp = new SpDefinition { StaticAnalysis = analysis };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.True(expectations!.HasThreePartReference);
            Assert.False(expectations.HasLinkedServerReference);
        }

        [Fact]
        public void From_WhenTheOnlyMaterialIsALinkedServerReference_ShouldNotBeNullAndShouldCarryTheFlag()
        {
            // 위 ThreePartReference 테스트의 짝이다. From의 조기 반환 조건은 두 항
            // (!hasThreePartReference && !hasLinkedServerReference)을 모두 걸어야 하는데,
            // 한쪽만 테스트하면 다른 쪽 항이 조용히 빠져도 스위트가 초록으로 남는다.
            // 이 태스크는 뒤따르는 태스크(5, 6, 7, 10, 11)가 조기 반환 조건에 항을
            // 하나씩 잇는 템플릿이므로, 두 항을 각각 독립적으로 증명해 둔다.
            var analysis = new SpStaticAnalysisResult();
            analysis.LinkedServerReferences.Add("MyServer.RemoteDb.dbo.Orders");
            var sp = new SpDefinition { StaticAnalysis = analysis };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.True(expectations!.HasLinkedServerReference);
            Assert.False(expectations.HasThreePartReference);
        }

        [Fact]
        public void From_WhenTheOnlyMaterialIsASessionOption_ShouldNotBeNullAndShouldCarryTheOption()
        {
            // Task 4가 세운 조기 반환 함정의 재현이다. sessionOptions를 조기 반환
            // 조건에 잇지 않으면 이 새 재료가 유일하게 있는 SpDefinition도 null을
            // 받아 CheckSessionOptions가 한 번도 돌지 않는다 - 이 테스트가 그 배선이
            // 실제로 넓혀졌는지를 From()을 통해 증명한다.
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SET NOCOUNT ON
    BEGIN TRAN
    COMMIT TRAN
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.Contains("NOCOUNT", expectations!.SessionOptions);
        }

        [Fact]
        public void From_WhenDdlHasHeaderCommentAndNamedInternalExecCall_ShouldSetHasInternalProcedureCallTrue()
        {
            // Util_Settle_Summary 실측 형태 - 이름 고정 EXEC(EXEC dbo.OtherProc)는
            // SqlStaticParser.ControlFlowSummary에 아무 흔적도 남기지 않는다(동적 SQL만
            // 경고로 남는다). ControlFlowSummary.Any(s => s.Contains("EXEC"))로 이 신호를
            // 판정했다면 이 테스트가 실패해야 한다 - 그 판정식은 이 DDL에서 항상 false다.
            var sp = new SpDefinition
            {
                DdlText = @"
-- Inner SP        : NONE
CREATE PROCEDURE dbo.P AS
BEGIN
    EXEC dbo.OtherProc @Ymd
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.True(expectations!.HasInternalProcedureCall);
        }

        [Fact]
        public void From_WhenDdlOnlyHasDynamicSqlExec_ShouldNotSetHasInternalProcedureCall()
        {
            // 동적 SQL 실행(EXEC(@sql) 또는 sp_executesql)은 "내부 SP 호출"이 아니다.
            // SqlStaticParser가 남기는 경고 문구("EXEC (@SQL) 동적 SQL 문자열 실행
            // 감지됨")는 우연히 "EXEC" 부분 문자열을 포함하므로, ControlFlowSummary
            // 기반 판정식은 이 케이스에서 반대 방향으로(내부 SP 호출이 아닌데 있다고)
            // 오탐할 수 있었다. AST를 직접 보는 이 구현은 ExecutableStringList를
            // ExecutableProcedureReference와 구분하므로 여기서 false여야 한다.
            var sp = new SpDefinition
            {
                DdlText = @"
-- Inner SP        : NONE
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @sql NVARCHAR(100) = 'SELECT 1'
    EXEC (@sql)
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.False(expectations!.HasInternalProcedureCall);
        }

        [Fact]
        public void From_WhenOnlyMaterialIsInternalProcedureCallWithoutAnyComments_ShouldStayNull()
        {
            // 의도적 결정의 문서화다. HasInternalProcedureCall은 조기 반환 조건에
            // 잇지 않는다 - 이 신호는 헤더 주석이 NONE이라 선언했을 때만 의미가 있고,
            // 헤더 주석이 있는 SP는 sourceComments 항이 이미 null 판정을 넓혀 준다.
            // 반대로 주석이 하나도 없는 이 DDL처럼, 대조할 헤더 계약 자체가 없으면
            // HasInternalProcedureCall이 true여도 여전히 null이 맞다 - CheckHeaderContractContradiction이
            // 어차피 headerClaimsNone에서 조용히 스킵할 대상을 대조 표면에 끌어올릴
            // 이유가 없다.
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    EXEC dbo.OtherProc
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.Null(expectations);
        }

        [Fact]
        public void Validate_MissingDmlScopeTable_ShouldBeAnError()
        {
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[]
                {
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" }, Array.Empty<string>())
                }
            };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.DmlScopeTableMissing);
        }

        [Fact]
        public void Validate_DmlScopeRowMissingTheLine_ShouldBeAnError()
        {
            // 헤딩만 옮기고 행을 빠뜨리는 것을 잡는다.
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[]
                {
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" }, Array.Empty<string>()),
                    new DmlScopeFact("UPDATE", 331, "A", new[] { "YMD" }, true, Array.Empty<string>(), Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
                + "| 문장 | 라인 | 대상 |\n| :--- | :--- | :--- |\n| UPDATE 1 | 227 | A |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing && e.Message.Contains("331"));
        }

        [Fact]
        public void Validate_DmlScopeTableFullyCopied_ShouldPass()
        {
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[]
                {
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" }, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
                + "| 문장 | 라인 | 대상 | 술어 | 기준일 | 조인 키 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 227 | A | UseState | **아니오** | PLTID |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DmlScopeTableMissing);
        }

        // ─────────────────────────────────────────────────────────────────────
        // 술어 컬럼·조인 키 칸의 정확 일치 - 2026-08-24 10회차 🟡 (COMM_UPD UPDATE 10)
        //
        // 기계 원문 8개 토큰(CLIENTID, PGNAME, MALLID, YMD, USESTATE, CYMD, AYMD, RefundFlag)에
        // 모델이 PGNAME을 하나 더해 9개로 전사했는데 L1이 통과시켰다 - 이 검사는 그 칸을
        // 아예 대조하지 않았다. GROUP BY 칸과 같은 관례(행 매칭 후, 목록이 비지 않을 때만,
        // 렌더 문자열 정확 일치)로 두 칸을 요구한다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Validate_DmlScopePredicateCellWithDuplicatedToken_ShouldBeAnError()
        {
            // COMM_UPD:340 실물 모양 - 토큰 하나가 중복으로 끼어듦.
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[]
                {
                    new DmlScopeFact("UPDATE", 340, "A",
                        new[] { "CLIENTID", "PGNAME", "MALLID", "YMD", "USESTATE", "CYMD", "AYMD", "RefundFlag" },
                        true, new[] { "CLIENTID" }, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
                + "| 문장 | 라인 | 대상 | 술어 | 기준일 | 조인 키 |\n| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 340 | A | CLIENTID, PGNAME, MALLID, YMD, PGNAME, USESTATE, CYMD, AYMD, RefundFlag | 예 | CLIENTID |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing && e.Message.Contains("술어 컬럼"));
        }

        [Fact]
        public void Validate_DmlScopeJoinKeyCellRewritten_ShouldBeAnError()
        {
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[]
                {
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false,
                        new[] { "PLTID", "YMD" }, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
                + "| 문장 | 라인 | 대상 | 술어 | 기준일 | 조인 키 |\n| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 227 | A | UseState | **아니오** | PLTID |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing && e.Message.Contains("조인 키"));
        }

        [Fact]
        public void Validate_DmlScopeCellsCopiedVerbatim_Pass()
        {
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[]
                {
                    new DmlScopeFact("UPDATE", 340, "A",
                        new[] { "CLIENTID", "PGNAME", "MALLID" }, true, new[] { "CLIENTID", "PGNAME" }, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
                + "| 문장 | 라인 | 대상 | 술어 | 기준일 | 조인 키 |\n| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 340 | A | CLIENTID, PGNAME, MALLID | 예 | CLIENTID, PGNAME |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DmlScopeTableMissing);
        }

        [Fact]
        public void Validate_DmlScopeEmptyPredicateAndJoinLists_AreNotRequired()
        {
            // "(없음)" 토큰은 여러 칸에 나올 수 있어 요구하면 우연 일치가 검사를 무력화한다 -
            // GROUP BY 칸과 같은 이유로 비어 있으면 요구하지 않는다.
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[]
                {
                    new DmlScopeFact("INSERT", 52, "TSettleMst",
                        System.Array.Empty<string>(), false, System.Array.Empty<string>(), Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
                + "| 문장 | 라인 | 대상 | 술어 | 기준일 | 조인 키 |\n| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| INSERT 1 | 52 | TSettleMst | (없음) | **아니오** | (없음) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DmlScopeTableMissing);
        }

        [Fact]
        public void Validate_DmlScopeRowLineCollidesOnlyWithAnUnrelatedTableElsewhere_ShouldStillBeAnError()
        {
            // 라인 번호가 다른 표(스키마 대응 표 등)의 무관한 숫자 셀과 우연히 같을 수
            // 있다. 검사가 문서 전체를 훑으면 그 우연이 거짓 통과를 만든다 - 그래서
            // DML 범위 헤딩 다음 구간으로만 대조 범위를 좁혔다. 이 테스트는 헤딩 밖에
            // 있는 "227"이 통과를 만들지 않는다는 것을 증명한다.
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[]
                {
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" }, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### 무관한 다른 표\n"
                + "| 컬럼 | 길이 |\n| :--- | :--- |\n| Amount | 227 |\n"
                + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
                + "| 문장 | 라인 | 대상 |\n| :--- | :--- | :--- |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing && e.Message.Contains("227"));
        }

        [Fact]
        public void From_WhenTheOnlyMaterialIsADmlScopeFact_ShouldNotBeNullAndShouldCarryTheFact()
        {
            // Task 4가 세운 조기 반환 함정의 재현이다. dmlScopeFacts를 조기 반환
            // 조건에 잇지 않으면 이 새 재료가 유일하게 있는 SpDefinition도 null을
            // 받아 CheckDmlScopeTable이 한 번도 돌지 않는다 - 이 테스트가 그 배선이
            // 실제로 넓혀졌는지를 From()을 통해 증명한다.
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE ID > 0
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            var fact = Assert.Single(expectations!.DmlScopeFacts);
            Assert.Equal("UPDATE", fact.Operation);
        }

        [Fact]
        public void From_DmlScopeFacts_ShouldApplyTheSameDateParameterAsResolveDateParameter()
        {
            // 프롬프트(AiService)와 L1(SpecExpectations.From)이 서로 다른 기준일 파라미터를
            // 고르면, 모델이 표를 그대로 베껴도 L1은 틀렸다고 한다 - 재현 불가능한 실패다.
            // 두 곳 모두 SpecExpectations.ResolveDateParameter 하나만 부르는 것이 유일한
            // 방지책이다. 이 테스트는 From()이 실제로 그 헬퍼가 고르는 파라미터
            // (@pi_strYMD, 목록의 두 번째 것 - 첫 번째를 그냥 집는 실수를 잡는다)로
            // DateParameterApplied를 판정하는지 증명한다.
            //
            // [픽스처는 반드시 선언문 형태여야 한다] SqlStaticParser.ExplicitVisit(
            // ProcedureParameter)는 $"{VariableName} {DataType}"으로 담으므로 운영에서
            // 이 목록의 원소는 언제나 "@pi_strYMD varchar(8)"이지 "@pi_strYMD"가 아니다.
            // 맨 이름을 넣은 픽스처는 파서가 만들 수 없는 값이라 ResolveDateParameter가
            // 선언문을 그대로 흘려보내는 결함을 통째로 가렸고, 그 결함은 EXCEPTION_PROC
            // 재생성에서 기준일 칸이 전 행 '아니오'로 나가서야 드러났다.
            var analysis = new SpStaticAnalysisResult();
            analysis.ProcedureParameters.Add("@pi_intBatchNo int");
            analysis.ProcedureParameters.Add("@pi_strYMD varchar(8)");
            var sp = new SpDefinition
            {
                StaticAnalysis = analysis,
                DdlText = @"
CREATE PROCEDURE dbo.P
    @pi_intBatchNo INT,
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE YMD = @pi_strYMD
END"
            };

            var expectations = SpecExpectations.From(sp);
            var resolvedByHelper = SpecExpectations.ResolveDateParameter(analysis);

            Assert.NotNull(expectations);
            Assert.Equal("@pi_strYMD", resolvedByHelper);
            var fact = Assert.Single(expectations!.DmlScopeFacts);
            Assert.True(fact.DateParameterApplied);
        }

        [Fact]
        public void ResolveDateParameter_OnRealParserOutput_ShouldYieldBareName()
        {
            // 위 테스트는 픽스처를 손으로 만든다 - 그 형식이 파서의 실제 산출과 어긋나면
            // 통째로 헛돈다. 이 테스트만이 SqlStaticParser를 실제로 돌려서 이음매를
            // 못 박는다: 파서가 담는 것은 선언문이고, ResolveDateParameter가 돌려줘야
            // 하는 것은 DmlScopeExtractor의 VariableReference.Name과 맞물릴 맨 이름이다.
            var ddl = @"
CREATE PROCEDURE dbo.P
    @pi_intBatchNo INT,
    @pi_strYMD VARCHAR(8)
AS
BEGIN
    UPDATE dbo.T SET C = 1 WHERE YMD = @pi_strYMD
END";
            var analysis = new SqlStaticParser().Analyze(ddl);

            // 파서가 정말 "이름 타입" 형태로 담는다는 것을 먼저 확인한다.
            Assert.Contains(analysis.ProcedureParameters, p => p.StartsWith("@pi_strYMD ", StringComparison.Ordinal));

            Assert.Equal("@pi_strYMD", SpecExpectations.ResolveDateParameter(analysis));

            var facts = DmlScopeExtractor.Extract(ddl, SpecExpectations.ResolveDateParameter(analysis));
            Assert.True(Assert.Single(facts).DateParameterApplied);
        }

        [Fact]
        public void Validate_MissingDerivedTableDefinition_ShouldBeAnError()
        {
            // EXCEPTION_PROC 실행순서 13 실측. SET 우변이 ISNULL(X.PGCOMM,0)에서
            // 멈추고 X의 정의(프로모션 원가 기준금액 분기)가 본문 어디에도
            // 없으면, 이 검사가 없으면 재생성이 TxAmt 기준으로 계산해 금액이
            // 달라진다 - 이번 감사의 유일한 축 A 🔴.
            var expectations = EmptyExpectations() with
            {
                DerivedColumns = new[]
                {
                    new DerivedColumnDefinition(
                        "X", "PGCOMM",
                        "IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)",
                        new[] { "DiscountFlag", "DiscountAmt", "TxAmt" })
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\nPG 수수료는 `ISNULL(X.PGCOMM, 0)`으로 계산합니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.DerivedTableDefinitionMissing);
        }

        [Fact]
        public void Validate_DerivedTableDefinitionPresent_ShouldPass()
        {
            // 위 테스트의 짝 - 앵커 하나(여기서는 DiscountFlag)만 본문에 있으면
            // 통과다. 전부 요구하면 표현식을 풀어 설명한 정상 서술이 결함이
            // 된다(설계 의도).
            var expectations = EmptyExpectations() with
            {
                DerivedColumns = new[]
                {
                    new DerivedColumnDefinition(
                        "X", "PGCOMM",
                        "IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)",
                        new[] { "DiscountFlag", "DiscountAmt", "TxAmt" })
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### 파생 테이블 정의 (기계 확정 — 수정 금지)\n"
                + "| 별칭 | 컬럼 | 정의 표현식 |\n| :--- | :--- | :--- |\n"
                + "| X | PGCOMM | IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DerivedTableDefinitionMissing);
        }

        /// <summary>
        /// [2026-08-21 최종 브랜치 리뷰 재라운드 ⑤ - "정의 표현식" 칸(AiService.cs:1231)은
        /// 파이프 왕복이 필요 없다는 것을 실측으로 증명한다] `CheckDerivedTableDefinitions`는
        /// `definition.Expression` 전체가 아니라 `definition.Anchors`(정규식
        /// `\b[A-Za-z][A-Za-z0-9_]{2,}\b`로 뽑은 순수 식별자 토큰)만 `Contains`로 찾는다
        /// (`DerivedTableColumnExtractor.BuildAnchors`). 그 정규식은 `|`를 포함하는 매치를
        /// 만들 수 없으므로 앵커 자체에 `|`가 나타날 수 없다 - 렌더가 표현식 칸 전체를
        /// 이스케이프해도(비트 OR가 든 식) 앵커 대조에는 영향이 없다. 이 테스트가 그
        /// 사실을 고정한다.
        /// </summary>
        [Fact]
        public void Validate_DerivedColumnExpressionContainsPipe_RenderedEscaped_ShouldStillPass()
        {
            var expectations = EmptyExpectations() with
            {
                DerivedColumns = new[]
                {
                    new DerivedColumnDefinition(
                        "X", "FLAGS",
                        "A.Flags | 4",
                        new[] { "Flags" })
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### 파생 테이블 정의 (기계 확정 — 수정 금지)\n"
                + "| 별칭 | 컬럼 | 정의 표현식 |\n| :--- | :--- | :--- |\n"
                + "| X | FLAGS | A.Flags \\| 4 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DerivedTableDefinitionMissing);
        }

        [Fact]
        public void Validate_DerivedColumnAnchorsScatteredElsewhereInDocument_ShouldStillBeAnError()
        {
            // 실측: EXCEPTION_PROC의 저장된 Spec.md는 "### 파생 테이블 정의 (기계
            // 확정 — 수정 금지)" 헤딩이 아예 없는데도 DiscountFlag(6회)·
            // DiscountAmt(7회)·TxAmt(11회 이상)가 문서 다른 곳에 흩어져 등장해,
            // "문서 전체에 앵커가 있는가"만 보는 종전 구현이 21개 행 전부를 통과
            // 시켰다. 실제 정의식 IIF(ISNULL(A.DiscountFlag,'N')='Y',
            // A.DiscountAmt, A.TxAmt)는 어디에도 없다 - 이 브랜치의 유일한 축 A
            // 🔴가 프롬프트로만 닫혀 있었다는 뜻이다. CheckDmlScopeTable처럼
            // 헤딩을 강제하고, 헤딩이 없으면 앵커가 문서 다른 곳에 있어도 통과시키지
            // 않아야 한다.
            var expectations = EmptyExpectations() with
            {
                DerivedColumns = new[]
                {
                    new DerivedColumnDefinition(
                        "X", "PGCOMM",
                        "IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)",
                        new[] { "DiscountFlag", "DiscountAmt", "TxAmt" })
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\nPG 수수료는 `ISNULL(X.PGCOMM, 0)`으로 계산합니다. "
                + "DiscountFlag는 다른 조회 화면에서도 쓰인다. DiscountAmt와 TxAmt는 "
                + "정산 요약 표에도 등장한다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.DerivedTableDefinitionMissing);
        }

        [Fact]
        public void Validate_DerivedColumnAnchorOutsideTheDerivedTableSection_ShouldStillBeAnError()
        {
            // CheckDmlScopeTable과 같은 스코프 원칙 - 파생 테이블 헤딩 다음 구간
            // 안에서만 앵커를 찾아야 한다. 헤딩 밖(예: 앞선 CRUD 서술)에 우연히
            // 앵커가 나타나는 것을 통과로 치면, 헤딩 자체는 있어도 정의 표현식이
            // 빠진 행을 놓친다.
            var expectations = EmptyExpectations() with
            {
                DerivedColumns = new[]
                {
                    new DerivedColumnDefinition(
                        "X", "PGCOMM",
                        "IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)",
                        new[] { "DiscountFlag", "DiscountAmt", "TxAmt" })
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\nDiscountFlag는 다른 조회 화면에서도 쓰인다.\n"
                + "\n### 파생 테이블 정의 (기계 확정 — 수정 금지)\n"
                + "| 별칭 | 컬럼 | 정의 표현식 |\n| :--- | :--- | :--- |\n"
                + "| X | PGCOMM | (정의 누락) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.DerivedTableDefinitionMissing);
        }

        [Fact]
        public void Validate_DerivedColumnWithNoAnchors_ShouldBeSkipped()
        {
            // 앵커가 하나도 없는 파생 컬럼(예: 상수·리터럴만으로 정의된 컬럼)은
            // 행 단위 대조 근거가 없다 - 조용히 빠져야지 항상 결함으로 잡히면 안
            // 된다. 다만 헤딩 자체는 여전히 요구된다(Fix Round 5) - AiService는
            // DerivedColumns가 하나라도 있으면 앵커 유무와 무관하게 항상 이 표
            // 전체를 렌더링하므로, 앵커 없는 컬럼이 있다고 표를 통째로 생략해도
            // 되는 것은 아니다.
            var expectations = EmptyExpectations() with
            {
                DerivedColumns = new[]
                {
                    new DerivedColumnDefinition("X", "FLAG", "1", Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### 파생 테이블 정의 (기계 확정 — 수정 금지)\n"
                + "| 별칭 | 컬럼 | 정의 표현식 |\n| :--- | :--- | :--- |\n"
                + "| X | FLAG | 1 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DerivedTableDefinitionMissing);
        }

        [Fact]
        public void From_WhenTheOnlyMaterialIsADerivedColumn_ShouldNotBeNullAndShouldCarryIt()
        {
            // Task 4/9가 세운 조기 반환 함정의 재현이다. DerivedColumns를 조기 반환
            // 조건에 잇지 않으면, 이 새 재료가 유일하게 있는 SpDefinition도 null을
            // 받아 CheckDerivedTableDefinitions이 한 번도 돌지 않는다 - 이 테스트가
            // 그 배선이 실제로 넓혀졌는지를 From()을 통해 증명한다.
            //
            // 파생 테이블을 DML이 아니라 IF 술어 안의 질의에 둔다 - DML을 쓰면
            // DmlScopeExtractor도 동시에 사실을 만들어 내어 (dmlScopeFacts.Count == 0)
            // 조건이 이미 false가 되고, derivedColumns 항을 조기 반환식에서 지워도
            // 이 테스트가 여전히 통과하는 거짓 안전망이 된다 - 신호를 하나만 남겨야
            // 그 신호의 배선만 증명한다.
            //
            // [2026-08-22 축 A 재감사 ③ Task 4 · 수정 라운드 1] 이 픽스처는 두 번
            // 새 재료를 흘렸다. 예전의 단순 `SELECT ... FROM`은 이제 DML 범위 사실을
            // 하나 만들고(Task 4), 그것을 피해 `IF EXISTS` 안으로 옮겼더니 이번엔
            // 잠금 힌트가 샜다 - Task 2가 더한 `LockHintVisitor.ExplicitVisit(IfStatement)`
            // 이 IF 술어의 FROM을 훑어 파생 테이블 안의 `dbo.TSettleMst`를 `IF 1 · 파생`
            // 행으로 싣기 때문이다(힌트가 없어도 행은 난다). 실측으로 확인했다.
            //
            // 그래서 파생 테이블 본문에서 테이블 참조 자체를 없앴다. 잠금 힌트를
            // 만드는 것은 `FromTableCollector.Visit(NamedTableReference)`뿐이므로,
            // 테이블 참조가 없는 파생 테이블은 어느 방문자에게도 걸리지 않는다.
            // `DerivedTableVisitor`는 `Visit(QueryDerivedTable)` 하나로 별칭과 명명된
            // SELECT 항목만 보므로 이 모양에서도 정의를 그대로 뽑는다.
            //
            // 실행 가능한 SQL일 필요는 없다 - 이 테스트가 묻는 것은 파서가 이 DDL에서
            // 어떤 재료를 뽑는가뿐이다(같은 이유로 아래 twin은 `AS PGCOMM`을 지운다).
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    IF EXISTS (SELECT 1
               FROM   (SELECT IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt) AS PGCOMM) X)
        RETURN
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.Empty(expectations!.DmlScopeFacts);
            Assert.Empty(expectations.LockHints);
            var def = Assert.Single(expectations.DerivedColumns, d => d.Column == "PGCOMM");
            Assert.Contains("DiscountFlag", def.Anchors);

            // 격리를 눈이 아니라 코드로 못박는다: 이 DDL에서 파생 컬럼 하나만 빼면
            // (`AS PGCOMM`을 지워 이름 없는 SELECT 항목으로 만들면 DerivedTableVisitor가
            // 건너뛴다) From은 null을 돌려줘야 한다. 그렇지 않다면 이 픽스처에 다른
            // 재료가 섞여 있다는 뜻이고, derivedColumns 항을 조기 반환식에서 지워도
            // 이 테스트가 초록으로 남는 거짓 안전망이 된다.
            var withoutTheDerivedColumn = new SpDefinition
            {
                DdlText = sp.DdlText!.Replace(" AS PGCOMM", string.Empty)
            };

            Assert.Null(SpecExpectations.From(withoutTheDerivedColumn));
        }

        // Column은 한정자를 포함한 원문 표기다(DmlScopeExtractor.SetPredicateFact
        // 문서 참고) - 실측 DDL의 `A.PGName NOT IN (...)`을 그대로 반영해 "A.PGName"으로
        // 둔다. 마지막 식별자 조각만 담으면 코퍼스에서 키 충돌이 실제로 난다.
        // StatementOrdinal은 1이다 - 추출기는 연산별로 1부터 매기고(0은 실물에 없다), L1이
        // 문장 칸을 행에서 요구하므로(2026-08-23) 손으로 만든 사실도 렌더 행의 `UPDATE 1`과 맞아야 한다.
        private static SetPredicateFact NineePgFact() => new(
            "UPDATE", 39, "A.PGName", true,
            new[]
            {
                "'PLCard'", "'SamSungPay'", "'SSGPayCard'", "'KakaoPay'", "'KakaoCard'",
                "'impaymobile'", "'NaverCard'", "'ApplePay'", "'TossCardAuth'"
            },
            1, "NOT IN", "최상위", NineePgPredicateText);

        /// <summary>
        /// NineePgFact의 「술어 원문」 칸(2026-08-22 축 A 재감사 ③ Task 7). 기대와 표가
        /// 같은 상수를 쓰므로, 리터럴을 빼먹는 시나리오에서도 원문 칸은 양쪽이 같다 -
        /// 그 테스트가 겨냥한 단언(리터럴 목록 대조)만 홀로 실패한다.
        /// </summary>
        private const string NineePgPredicateText =
            "A.PGName NOT IN ('PLCard', 'SamSungPay', 'SSGPayCard', 'KakaoPay', 'KakaoCard', "
            + "'impaymobile', 'NaverCard', 'ApplePay', 'TossCardAuth')";

        private static string SetPredicateSection(string literalCell) =>
            "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
            + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
            + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
            + $"| UPDATE 1 | 39 | A.PGName | NOT IN | 최상위 | 9 | {literalCell} | {NineePgPredicateText} |\n";

        [Fact]
        public void Validate_SetPredicateTableMissing_ShouldBeAnError()
        {
            var expectations = EmptyExpectations() with { SetPredicates = new[] { NineePgFact() } };

            var result = new MechanicalValidator().Validate(RequiredHeadersMarkdown(), expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_SetPredicateWithAllLiterals_ShouldPass()
        {
            var expectations = EmptyExpectations() with { SetPredicates = new[] { NineePgFact() } };
            var markdown = RequiredHeadersMarkdown()
                + SetPredicateSection(
                    "'PLCard', 'SamSungPay', 'SSGPayCard', 'KakaoPay', 'KakaoCard', 'impaymobile', 'NaverCard', 'ApplePay', 'TossCardAuth'");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_SetPredicateDroppingTwoLiterals_ShouldBeAnError()
        {
            // 2026-08-18 축 A 감사의 실제 실패 방식. 명세서는 9개 중 7개를 문서
            // 어딘가에 담고 있었고, 빠진 것은 SSGPayCard와 KakaoCard다. 행 골격만
            // 요구하면 이 문서가 통과한다.
            var expectations = EmptyExpectations() with { SetPredicates = new[] { NineePgFact() } };
            var markdown = RequiredHeadersMarkdown()
                + SetPredicateSection(
                    "'PLCard', 'SamSungPay', 'KakaoPay', 'impaymobile', 'NaverCard', 'ApplePay', 'TossCardAuth'");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            var error = Assert.Single(
                result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
            Assert.Contains("SSGPayCard", error.Message);
            Assert.Contains("KakaoCard", error.Message);
        }

        [Fact]
        public void Validate_SetPredicateWithNumericLiterals_ShouldNotBeSatisfiedByLineNumber()
        {
            // 설계 §5.1. 행 전체를 부분 문자열로 훑으면 라인 번호 108이 이미 0과 1을
            // 담아 UseState IN (0,1) 대조가 무조건 통과한다 - 검사가 아무것도 묻지
            // 않게 된다. 대조 대상은 리터럴 목록 칸 하나여야 한다.
            var fact = new SetPredicateFact(
                "UPDATE", 108, "UseState", false, new[] { "0", "1" }, 1, "IN", "최상위",
                "UseState IN (0, 1)");
            var expectations = EmptyExpectations() with { SetPredicates = new[] { fact } };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 108 | UseState | IN | 최상위 | 2 | (생략) | UseState IN (0, 1) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_SetPredicateRowKeyedByLineAndColumn_ShouldDistinguishTwoInsOnOneStatement()
        {
            // 한 문장에 IN이 둘이면 라인만으로는 행을 특정할 수 없다.
            var facts = new[]
            {
                new SetPredicateFact(
                    "UPDATE", 30, "PGName", false, new[] { "'A'", "'B'" }, 1, "IN", "최상위",
                    "PGName IN ('A', 'B')"),
                new SetPredicateFact(
                    "UPDATE", 30, "UseState", false, new[] { "0", "1" }, 1, "IN", "최상위",
                    "UseState IN (0, 1)")
            };
            var expectations = EmptyExpectations() with { SetPredicates = facts };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 30 | PGName | IN | 최상위 | 2 | 'A', 'B' | PGName IN ('A', 'B') |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            // UseState 행이 없으므로 하나만 걸려야 한다 - PGName 행이 라인 30을
            // 담았다고 UseState까지 통과시키면 안 된다.
            var error = Assert.Single(
                result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
            Assert.Contains("UseState", error.Message);
        }

        [Fact]
        public void Validate_SamePairKeyTwiceWithOnlyOneRow_ShouldBeAnError()
        {
            // (Operation, Line, Column) 키가 유일하다는 가정이 깨지는 경우 - 같은
            // 한정 컬럼이 한 문장에서 IN으로 두 번 걸리면(`A.X IN (1) AND A.X IN (2)`)
            // 추출기는 사실을 둘 내고 합치지 않는다(설계·ExtractSetPredicates 주석).
            // 표에 행이 하나뿐이면 사실 하나가 통째로 증발한 것이므로 실패해야 한다 -
            // `rowLines.FirstOrDefault`로 첫 행 하나에 둘 다 매칭시키면 이 누락을
            // 조용히 통과시킨다.
            var facts = new[]
            {
                new SetPredicateFact("UPDATE", 50, "A.X", false, new[] { "1" }, 1),
                new SetPredicateFact("UPDATE", 50, "A.X", false, new[] { "2" }, 1)
            };
            var expectations = EmptyExpectations() with { SetPredicates = facts };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 50 | A.X | IN | 최상위 | 1 | 1 | A.X IN (1) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            var error = Assert.Single(
                result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
            Assert.Contains("A.X", error.Message);
        }

        [Fact]
        public void Validate_SameKeyTwiceWithTwoDistinctRows_ShouldPass()
        {
            // 위 실패 사례의 짝 - 행이 사실 수만큼 있고 각 행의 원소 집합이 기대
            // 집합들의 다중집합과 일치하면 통과해야 한다. 두 행의 순서는 사실의
            // 순서와 같을 필요가 없다 - 다중집합 비교이지 자리 대응이 아니다.
            var facts = new[]
            {
                new SetPredicateFact("UPDATE", 50, "A.X", false, new[] { "1" }, 1),
                new SetPredicateFact("UPDATE", 50, "A.X", false, new[] { "2" }, 1)
            };
            var expectations = EmptyExpectations() with { SetPredicates = facts };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                // 같은 문장(UPDATE 1)의 두 AND 항이므로 문장 칸은 둘 다 UPDATE 1이다 -
                // 문장 칸을 안 보던 시절에는 둘째 행이 UPDATE 2로 적혀 있었다(2026-08-23 정정).
                + "| UPDATE 1 | 50 | A.X | IN | 최상위 | 1 | 2 | A.X IN (2) |\n"
                + "| UPDATE 1 | 50 | A.X | IN | 최상위 | 1 | 1 | A.X IN (1) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_SetPredicateColumnContainsPipe_RenderedEscaped_ShouldPass()
        {
            // [2026-08-21 최종 브랜치 리뷰 재라운드 ⑤] AiService.BuildSetPredicateTableLines는
            // 컬럼·범위 칸도 EscapeTableCell을 거친다(AiService.cs:944, BuildSetPredicateTableLines) - 대괄호
            // 식별자(`A.[C|D]`)처럼 `|`가 든 컬럼은 `\|`로 이스케이프된 채 표에 나온다.
            // 그런데 이 검사(위 매칭 Where절)는 그 행을 `r.Split('|')`로 단순 분할했다 -
            // 이스케이프를 복원하지 않으면 셀이 잘못 쪼개져 모델이 표를 원문 그대로
            // 옮겨도 컬럼이 일치하지 않는다(LockHints·ORDER BY·객체 선언과 같은 실패 모양).
            var fact = new SetPredicateFact("UPDATE", 12, "A.[C|D]", false, new[] { "'X'" }, 1);
            var expectations = EmptyExpectations() with { SetPredicates = new[] { fact } };
            var escapedColumn = fact.Column.Replace("|", "\\|");
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| UPDATE 1 | 12 | {escapedColumn} | IN | 최상위 | 1 | 'X' | {escapedColumn} IN ('X') |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        /// <summary>
        /// UF_GET_COLLECTYMD:100의 실물. 독립 SELECT(여기서는 함수 본문 SELECT)의
        /// 최상위 WHERE에 있는 리터럴 우변 등치라, 2026-08-23 축 A ③(b) Task 2가
        /// 집합 술어 표의 문장 집합을 넓히기 전에는 어떤 기계 확정 표에도 없고
        /// 산문에만 있었다.
        /// </summary>
        private static SetPredicateFact CollectFlagSelectFact() => new(
            "SELECT", 100, "CollectFlag", false, new[] { "1" }, 1, "=", "최상위",
            "CollectFlag = 1");

        /// <summary>
        /// 넓어진 문장 집합의 `SELECT n` 행을 L1이 DML 행과 똑같이 대조하는지 확인한다.
        /// CheckSetPredicates가 사실을 묶는 키는 (연산 · 번호 · 라인 · 컬럼 · 범위 · 술어 원문)
        /// 여섯이고, 행을 찾는 술어도 2026-08-23부터 문장 토큰(`SELECT 1`)을 함께 요구한다
        /// (MechanicalValidator.cs의 `groups`와 `matchingRows` - 그 전에는 라인 · 컬럼 · 범위 ·
        /// 술어 원문 네 칸만 봐서 연산이 `SELECT`로 바뀌어도 검사가 달라질 자리가 없다는 것이
        /// Task 2의 주장이었다). 어느 쪽이든 확인 없이 넘기면 표만 넓어지고 검사는 침묵한다.
        ///
        /// 이 테스트 홀로는 "검사가 SELECT 행을 아예 건너뛴다"와 구분되지 않는다 -
        /// 바로 아래 Validate_SetPredicateSelectRowDropped_ShouldBeAnError가 그 갈래를
        /// 막는다(행을 빼면 반드시 보고돼야 한다).
        /// </summary>
        [Fact]
        public void Validate_SetPredicateSelectRow_ShouldCompareLikeDmlRows()
        {
            var expectations = EmptyExpectations() with
            {
                SetPredicates = new[] { CollectFlagSelectFact() }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| SELECT 1 | 100 | CollectFlag | = | 최상위 | 1 | 1 | CollectFlag = 1 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(
                result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_SetPredicateSelectRowDropped_ShouldBeAnError()
        {
            // 표도 헤더도 구분줄도 있는데 SELECT 행만 없는 명세서. 검사가 SELECT 행을
            // 그냥 흘려보낸다면 여기서도 조용히 통과할 것이다.
            var expectations = EmptyExpectations() with
            {
                SetPredicates = new[] { CollectFlagSelectFact() }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void From_WithSetPredicates_ShouldExposeThemAndNeverReturnNull()
        {
            // [조기 반환과 이 재료의 관계 - 설계 §6.3의 예외] SpecExpectations.From의
            // 조기 반환은 순수 AND-체인이고, 보통은 "이 재료만 만드는 픽스처"로 자기
            // 항을 지킨다. 그런데 이 재료는 그 격리가 <b>원리적으로 불가능하다</b> -
            // ExtractSetPredicates와 Extract가 UpdateSpecification·DeleteSpecification·
            // InsertSpecification이라는 같은 세 문장만 방문하므로, SetPredicates가
            // 비지 않으면 DmlScopeFacts도 결코 비지 않는다. 즉 setPredicates 항은
            // 단독 판별자가 될 수 없다.
            //
            // 그래서 이 테스트는 격리 대신 <b>그 불변식 자체</b>를 단언한다. 불변식이
            // 깨지는 날(예: 추출기 하나가 다른 문장까지 훑게 되는 날) 이 테스트가
            // 먼저 실패해, 조기 반환 항이 그때부터 실제로 필요해졌음을 알린다.
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DELETE FROM dbo.T WHERE UseState IN (0, 1)
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            var fact = Assert.Single(expectations!.SetPredicates);
            Assert.Equal("UseState", fact.Column);
            Assert.Equal(new[] { "0", "1" }, fact.Literals);

            // 불변식: 집합 술어가 있으면 DML 사실도 반드시 있다. 이것이 성립하는
            // 동안 setPredicates 항은 조기 반환의 중복항이다.
            Assert.NotEmpty(expectations.DmlScopeFacts);

            // 나머지 재료는 이 픽스처가 만들지 않는다 - 이 테스트가 무엇을 증명하는지
            // 좁혀 둔다.
            Assert.Empty(expectations.DerivedColumns);
            Assert.Empty(expectations.RoundingCalls);
        }

        private static SpecExpectations EmptyExpectations() =>
            new(
                Array.Empty<UpdateColumnExpectation>(),
                new Dictionary<string, IReadOnlySet<string>>(),
                new HashSet<string>(),
                Array.Empty<string>());

        /// <summary>
        /// L1 구조 검사를 통과하는 최소 명세서. 아래 테스트들은 여기에 문장을 이어
        /// 붙여 쓰는데, WrapSpec이 닫는 코드 펜스 뒤에 붙으므로 ComputeFenceLineFlags가
        /// 펜스 밖으로 본다 - 검사 대상이 된다.
        /// </summary>
        private static string RequiredHeadersMarkdown() => WrapSpec("내용");

        // 감사 🔴(S16): CROSS JOIN 뒤 양변 SUM 비교는 각 변이 상대 건수배가 되어
        // 정상 데이터에서 항상 불일치한다. 그 결과가 S17 공개 상시 차단으로 이어졌다.
        [Fact]
        public void ValidateConsolidated_RejectsACartesianAggregateComparison()
        {
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
SELECT ISNULL(SUM(M.TXAMT),0), ISNULL(SUM(T.TXAMT),0)
FROM dbo.TSettleMst AS M
CROSS JOIN dbo.TSettleByTX AS T
HAVING ISNULL(SUM(M.TXAMT),0) <> ISNULL(SUM(T.TXAMT),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_AcceptsIndependentAggregatesComparedAsScalars()
        {
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
WITH L AS (SELECT ISNULL(SUM(TXAMT),0) AS S FROM dbo.TSettleMst WHERE YMD = @BatchYmd),
     R AS (SELECT ISNULL(SUM(TXAMT),0) AS S FROM dbo.TSettleByTX WHERE YMD = @BatchYmd)
SELECT L.S, R.S FROM L, R WHERE L.S <> R.S;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_DoesNotFlagCrossJoinKeywordInsideAComment()
        {
            // CROSS JOIN이라는 낱말이 주석 안에만 있고, 실제 질의는 INNER JOIN으로
            // 두 원천을 건별로 맞춘 뒤 한 번만 집계한다 - 별칭이 둘이라는 표면
            // 패턴은 있지만 카티전이 아니므로 데이터 품질 실패로 잡히면 안 된다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
-- 과거에는 여기서 CROSS JOIN 방식을 썼으나 지금은 INNER JOIN 방식으로 건별 대사한다.
SELECT ISNULL(SUM(M.TXAMT),0), ISNULL(SUM(T.TXAMT),0)
FROM dbo.TSettleMst AS M
INNER JOIN dbo.TSettleByTX AS T ON T.PLTID = M.PLTID
HAVING ISNULL(SUM(M.TXAMT),0) <> ISNULL(SUM(T.TXAMT),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_DoesNotFlagCrossApply()
        {
            // CROSS APPLY는 CROSS JOIN이 아니다 - \bCROSS\s+JOIN\b이 이를 잡으면
            // 정상적인 상관 서브쿼리 패턴이 오탐으로 걸린다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
SELECT ISNULL(SUM(A.TXAMT),0), ISNULL(SUM(B.TXAMT),0)
FROM dbo.TSettleMst AS A
CROSS APPLY (SELECT TOP 1 TXAMT FROM dbo.TSettleByTX WHERE PLTID = A.PLTID) AS B
HAVING ISNULL(SUM(A.TXAMT),0) <> ISNULL(SUM(B.TXAMT),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_DoesNotFlagTheSameAliasSummedTwice()
        {
            // 같은 별칭에 걸린 SUM이 둘이면 Distinct로 별칭이 1개가 되어, 서로
            // 다른 두 집계를 비교하는 카티전 패턴이 아니다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
SELECT ISNULL(SUM(M.TXAMT),0), ISNULL(SUM(M.FEE),0)
FROM dbo.TSettleMst AS M
CROSS JOIN dbo.TSettleByTX AS T
HAVING ISNULL(SUM(M.TXAMT),0) <> ISNULL(SUM(M.FEE),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_DoesNotFlagCrossJoinOfTwoIndependentlyAggregatedCtes()
        {
            // 감사 수정 라운드 1: L, R이 각자 한 행으로 집계된 CTE라면 이들의
            // CROSS JOIN은 1×1이라 수학적으로 무해하다 - AiService.cs 규칙 2가
            // 권장하는 "각자 CTE에서 집계한 뒤 비교" 패턴을 CROSS JOIN 문법으로 쓴
            // 것뿐이다. 이것을 잡으면 이 검사가 막으려는 바로 그 증상(정상 실행이
            // 데이터 품질 실패로 기록되어 공개가 상시 차단됨)을 이 검사가 일으킨다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
WITH L AS (SELECT ISNULL(SUM(M.TXAMT),0) AS S FROM dbo.TSettleMst AS M WHERE M.YMD=@BatchYmd),
     R AS (SELECT ISNULL(SUM(T.TXAMT),0) AS S FROM dbo.TSettleByTX AS T WHERE T.YMD=@BatchYmd)
SELECT L.S, R.S FROM L CROSS JOIN R WHERE L.S <> R.S;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_DoesNotFlagAnUnrelatedCrossJoinInASeparateStatementInTheSameFence()
        {
            // 감사 수정 라운드 1: 한 ```sql 펜스 안에 무관한 질의 둘이 있다.
            // A는 무해한 CROSS JOIN(별칭 SUM 비교가 없다)이고, B는 INNER JOIN으로
            // 건별 대사한 뒤 별칭 둘에 SUM을 건다 - 카티전이 아니다. 블록 전체를
            // 한 덩어리로 보면 A의 CROSS JOIN과 B의 별칭 SUM 둘이 우연히 합쳐져
            // 오탐이 난다 - 문 단위로 잘라야 이 우연한 결합을 막을 수 있다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
SELECT COUNT(*) FROM dbo.TCodeType AS X CROSS JOIN dbo.TCodeStatus AS Y;

SELECT ISNULL(SUM(M.TXAMT),0), ISNULL(SUM(T.TXAMT),0)
FROM dbo.TSettleMst AS M
INNER JOIN dbo.TSettleByTX AS T ON T.PLTID = M.PLTID
HAVING ISNULL(SUM(M.TXAMT),0) <> ISNULL(SUM(T.TXAMT),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_DoesNotFlagCrossJoinKeywordInsideAStringLiteral()
        {
            // 동적 SQL을 만드는 문자열 리터럴 안에 'CROSS JOIN'이라는 텍스트가
            // 있을 뿐이고 실제 질의는 CROSS JOIN을 쓰지 않는다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
DECLARE @msg NVARCHAR(100) = 'do not use CROSS JOIN here';
WITH L AS (SELECT ISNULL(SUM(TXAMT),0) AS S FROM dbo.TSettleMst WHERE YMD = @BatchYmd),
     R AS (SELECT ISNULL(SUM(TXAMT),0) AS S FROM dbo.TSettleByTX WHERE YMD = @BatchYmd)
SELECT L.S, R.S FROM L, R WHERE L.S <> R.S;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_FlagsACrossJoinBetweenOneCteAndOneRawTable()
        {
            // 좁히기가 지나치면 안 된다 - 한쪽만 CTE(L)이고 다른 쪽은 원시
            // 테이블(dbo.TSettleByTX)이면 1×1 보장이 없으므로 진짜 카티전일 수
            // 있다. 두 CTE가 다 아는 이름일 때만 건너뛰어야 한다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
WITH L AS (SELECT ISNULL(SUM(TXAMT),0) AS S FROM dbo.TSettleMst WHERE YMD = @BatchYmd)
SELECT ISNULL(SUM(L.S),0), ISNULL(SUM(T.TXAMT),0)
FROM L
CROSS JOIN dbo.TSettleByTX AS T
HAVING ISNULL(SUM(L.S),0) <> ISNULL(SUM(T.TXAMT),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_FlagsPassThroughCtesCrossJoinedThenAggregatedOutside()
        {
            // 감사 수정 라운드 2: 라운드 1의 좁히기는 "이름이 CTE면 안전"이라고
            // 가정했는데 그 가정이 틀렸다. L, R이 통과용(SELECT *)일 뿐 한 행으로
            // 집계되지 않으면 CROSS JOIN은 여전히 진짜 카티전이고, 바깥에서
            // 별칭별로 SUM을 걸면 원래 결함(S16/S17)과 같은 모양이 된다. CTE
            // 이름만 보고 넘기면 이 재현을 놓친다 - 재리뷰가 직접 재현했다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
WITH L AS (SELECT * FROM dbo.TSettleMst), R AS (SELECT * FROM dbo.TSettleByTX)
SELECT ISNULL(SUM(L.TXAMT),0), ISNULL(SUM(R.TXAMT),0) FROM L CROSS JOIN R HAVING ISNULL(SUM(L.TXAMT),0) <> ISNULL(SUM(R.TXAMT),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_FlagsCrossJoinOfGroupedCtes()
        {
            // GROUP BY가 있는 CTE는 그룹 수만큼 행을 낸다 - 한 행이 보장되지
            // 않으므로 CROSS JOIN이 1×1이라는 근거가 없다. 넘기면 안 된다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
WITH L AS (SELECT M.PLTID, SUM(M.TXAMT) AS S FROM dbo.TSettleMst AS M GROUP BY M.PLTID),
     R AS (SELECT T.PLTID, SUM(T.TXAMT) AS S FROM dbo.TSettleByTX AS T GROUP BY T.PLTID)
SELECT ISNULL(SUM(L.S),0), ISNULL(SUM(R.S),0) FROM L CROSS JOIN R HAVING ISNULL(SUM(L.S),0) <> ISNULL(SUM(R.S),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_FlagsCrossJoinOfOneAggregateCteAndOnePassThroughCte()
        {
            // 한쪽(L)만 집계 CTE고 다른 쪽(R)은 통과용이면 R이 여러 행을 낼 수
            // 있어 1×1 보장이 없다 - 양쪽이 다 집계 CTE일 때만 넘겨야 한다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
WITH L AS (SELECT ISNULL(SUM(M.TXAMT),0) AS S FROM dbo.TSettleMst AS M WHERE M.YMD=@BatchYmd),
     R AS (SELECT * FROM dbo.TSettleByTX)
SELECT ISNULL(SUM(L.S),0), ISNULL(SUM(R.TXAMT),0) FROM L CROSS JOIN R HAVING ISNULL(SUM(L.S),0) <> ISNULL(SUM(R.TXAMT),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_DoesNotFlagCrossJoinOfCtesWhoseGroupByIsOnlyInsideANestedSubquery()
        {
            // 감사 수정 라운드 3: 라운드 2는 CTE 본문 전체에서 GROUP BY를 찾았다.
            // L의 본문은 "SELECT SUM(sub.S) FROM (그룹별 집계 서브쿼리) AS sub"
            // 처럼 서브쿼리 안에서만 GROUP BY를 쓰고, 바깥은 그 서브쿼리 결과를
            // 다시 SUM으로 합산한다 - 서브쿼리 자체는 여러 행을 내지만, 바깥의
            // SUM이 그것을 다시 한 행으로 만든다. L 본문 "자신의" SELECT에는
            // GROUP BY가 없으므로 L은 여전히 한 행이다. 재리뷰가 이 모양을
            // 재현했다 - 라운드 1 BASE에서는 안 잡혔는데(정탐) 라운드 2에서
            // 새로 잡히기 시작했다(오탐, 이번 라운드가 고칠 회귀).
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
WITH L AS (
    SELECT SUM(sub.S) AS S FROM (SELECT M.PLTID, SUM(M.TXAMT) AS S FROM dbo.TSettleMst AS M GROUP BY M.PLTID) AS sub
),
     R AS (SELECT ISNULL(SUM(T.TXAMT),0) AS S FROM dbo.TSettleByTX AS T WHERE T.YMD=@BatchYmd)
SELECT ISNULL(L.S,0), ISNULL(R.S,0) FROM L CROSS JOIN R HAVING ISNULL(L.S,0) <> ISNULL(R.S,0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        [Fact]
        public void ValidateConsolidated_FlagsCrossJoinOfAPassThroughCteWrappingAGroupedSubquery()
        {
            // 감사 수정 라운드 4: 라운드 3은 hasGroupBy만 depth 0으로 좁히고
            // hasAggregate는 본문 전체 텍스트를 그대로 스캔했다. L의 자기
            // SELECT는 통과용(SELECT *)인데, 안쪽 서브쿼리의 SUM(이 전체
            // 스캔에 걸려 L이 "집계 있음, GROUP BY 없음"으로 오분류됐다 -
            // 실제로는 서브쿼리가 PLTID별로 여러 행을 내고 L은 그것을 그대로
            // 통과시키므로 여러 행이다. CROSS JOIN 뒤 바깥에서 SUM(L.S)로
            // 재집계하면 S16 원 결함(그룹 수만큼 부풀려진 합계)을 그대로
            // 재현한다. 재리뷰가 라운드 2(fd5daaf)는 이걸 정탐으로 잡았는데
            // 라운드 3(af05381)이 미탐으로 되돌렸다고 재현했다.
            var markdown = ConsolidatedDocumentWithVerificationSql(@"
WITH L AS (
    SELECT * FROM (SELECT M.PLTID, SUM(M.TXAMT) AS S FROM dbo.TSettleMst AS M GROUP BY M.PLTID) AS sub
),
     R AS (SELECT ISNULL(SUM(T.TXAMT),0) AS S FROM dbo.TSettleByTX AS T WHERE T.YMD=@BatchYmd)
SELECT ISNULL(SUM(L.S),0), ISNULL(SUM(R.S),0) FROM L CROSS JOIN R HAVING ISNULL(SUM(L.S),0) <> ISNULL(SUM(R.S),0);");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.VerificationCartesianComparison);
        }

        // 감사 실측: INSERT INTO batch.BatchRun이 번들 전체에 0건이었다. 단계 검사로는
        // 잡을 수 없다 - 어느 단계가 첫 단계인지 단계 문서 하나만 봐서는 모른다.
        // 통합 문서는 계획서 전체를 보므로 여기서 닫는다.
        [Fact]
        public void ValidateConsolidated_RejectsADocumentThatUpdatesBatchRunButNeverInsertsIt()
        {
            var markdown = ConsolidatedDocumentWithStepBody(@"
UPDATE batch.BatchRun SET RunStatus = N'Succeeded', CompletedAtUtc = SYSUTCDATETIME()
WHERE RunId = @RunId;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        [Fact]
        public void ValidateConsolidated_AcceptsADocumentThatInsertsBatchRunSomewhere()
        {
            var markdown = ConsolidatedDocumentWithStepBody(@"
INSERT INTO batch.BatchRun (JobName, BatchYmd, RunStatus, StartedAtUtc)
VALUES (@JobName, @BatchYmd, N'Running', SYSUTCDATETIME());
SET @RunId = SCOPE_IDENTITY();
UPDATE batch.BatchRun SET RunStatus = N'Succeeded' WHERE RunId = @RunId;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        // 소프트 스킵: 문서가 이 테이블을 언급조차 하지 않으면 검사하지 않는다.
        [Fact]
        public void ValidateConsolidated_SkipsTheBatchRunCheckWhenTheDocumentNeverMentionsIt()
        {
            var markdown = ConsolidatedDocumentWithStepBody("SELECT 1;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        // 수정 라운드 1 리뷰 Critical 실측: 대괄호 인용 스키마/테이블([batch].[BatchRun]
        // 등)이 이 검사의 자체 정규식에 걸리지 않아 정상 INSERT를 반려했다(오탐).
        // ResolveControlTableAliases가 별칭 바인딩에 이미 쓰는 대괄호 인식 패턴을
        // 재사용해 네 혼합 형태(양쪽 대괄호/한쪽만 대괄호)를 전부 정상으로 받는지 잠근다.
        [Theory]
        [InlineData("[batch].[BatchRun]")]
        [InlineData("[dbo].[BatchRun]")]
        [InlineData("batch.[BatchRun]")]
        [InlineData("[batch].BatchRun")]
        public void ValidateConsolidated_AcceptsABracketQuotedInsertOfBatchRun(string qualifiedTable)
        {
            var markdown = ConsolidatedDocumentWithStepBody($@"
INSERT INTO {qualifiedTable} (JobName, BatchYmd, RunStatus, StartedAtUtc)
VALUES (@JobName, @BatchYmd, N'Running', SYSUTCDATETIME());");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        // 대괄호 인식을 넓히면서 미탐을 만들지 않았는지 잠근다 - 같은 대괄호 형태로
        // UPDATE만 하고 INSERT가 없으면 여전히 잡혀야 한다.
        [Fact]
        public void ValidateConsolidated_RejectsABracketQuotedUpdateOfBatchRunItNeverInserts()
        {
            var markdown = ConsolidatedDocumentWithStepBody(@"
UPDATE [batch].[BatchRun] SET RunStatus = N'Succeeded' WHERE RunId = @RunId;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        // 수정 라운드 1 리뷰 Minor 실측: 선행 경계 없이 접미사로 겹치는 식별자
        // (MyBatchRun)가 "언급"으로 오인되면 안 된다. batch.BatchRun 자체는 문서에
        // 없으므로 소프트 스킵이어야 한다.
        [Fact]
        public void ValidateConsolidated_SkipsWhenOnlyASuffixOverlappingIdentifierIsMentioned()
        {
            var markdown = ConsolidatedDocumentWithStepBody(@"
UPDATE dbo.MyBatchRun SET Foo = 1 WHERE Id = @Id;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        // ─────────────────────────────────────────────────────────────────────
        // TASK 14 - CheckFirstStepRowCreation/CheckBatchRunRowCreation이
        // IDENTITY 없는 FirstStepInserts 테이블에도 "SCOPE_IDENTITY()로 발급된
        // 값을 넘기라"고 지시했다. 그 값은 IDENTITY가 있을 때만 존재한다 -
        // 지금 이 축에는 batch.BatchRun(IsIdentity: true) 하나뿐이라 우연히
        // 참이었지만, IDENTITY 없는 테이블이 같은 축에 들어오는 순간 거짓
        // 지시가 된다. BatchControlContract.Tables는 지금 이 하나뿐이라 공개
        // 경로(ValidateBatchStep/ValidateConsolidated)로는 IDENTITY 없는
        // 테이블을 시험할 수 없으므로, 문구를 만드는 private 헬퍼를 리플렉션으로
        // 직접 불러 손으로 만든 ControlTable(IsIdentity 없음)을 넘긴다.
        // ─────────────────────────────────────────────────────────────────────

        private static string InvokeFirstStepRowCreationMessage(BatchStepPlan step, ControlTable table)
        {
            var method = typeof(MechanicalValidator).GetMethod(
                "FirstStepRowCreationMessage", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return (string)method!.Invoke(null, new object[] { step, table })!;
        }

        private static string InvokeBatchRunRowCreationMessage(ControlTable table)
        {
            var method = typeof(MechanicalValidator).GetMethod(
                "BatchRunRowCreationMessage", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return (string)method!.Invoke(null, new object[] { table })!;
        }

        // batch.BatchRunLock처럼 IDENTITY 없이 복합키로만 여는 제어 테이블을
        // 흉내낸다. BatchControlContract.cs는 이 태스크의 쓰기 범위 밖이라
        // 실제 정본에 추가하지 못하므로, 검사 함수가 받는 ControlTable을
        // 직접 만든다.
        private static ControlTable NoIdentityFirstStepTable() => new(
            "batch.BatchRunLock",
            new[]
            {
                new ControlColumn("RunId", "bigint", false),
                new ControlColumn("LockKey", "nvarchar(64)", false)
            },
            ControlRowOrigin.FirstStepInserts,
            null,
            new[] { "RunId", "LockKey" });

        [Fact]
        public void FirstStepRowCreationMessage_TableWithoutIdentity_OmitsScopeIdentityClause()
        {
            var step = LegacyStep("S02");
            var table = NoIdentityFirstStepTable();

            var message = InvokeFirstStepRowCreationMessage(step, table);

            Assert.DoesNotContain("SCOPE_IDENTITY", message);
            Assert.DoesNotContain("RunId를 이후 단계에", message);
            Assert.Equal(
                "S02 섹션에 `batch.BatchRunLock` 행을 만드는 INSERT가 없습니다. " +
                "이 테이블을 대상으로 선언한 첫 단계가 S02이므로 실행 행을 발급할 책임이 " +
                "이 단계에 있습니다. 생성 없이 UPDATE만 하면 0행이 갱신되어 실행 단위 자체가 " +
                "존재하지 않습니다.",
                message);
        }

        [Fact]
        public void FirstStepRowCreationMessage_TableWithIdentity_KeepsScopeIdentityClauseUnchanged()
        {
            var step = LegacyStep("S02");
            var table = BatchControlContract.Find("batch.BatchRun");
            Assert.NotNull(table);

            var message = InvokeFirstStepRowCreationMessage(step, table!);

            Assert.Equal(
                "S02 섹션에 `batch.BatchRun` 행을 만드는 INSERT가 없습니다. " +
                "이 테이블을 대상으로 선언한 첫 단계가 S02이므로 실행 행을 발급할 책임이 " +
                "이 단계에 있습니다. 생성 없이 UPDATE만 하면 0행이 갱신되어 실행 단위 자체가 " +
                "존재하지 않습니다. INSERT를 두고 SCOPE_IDENTITY()로 발급된 RunId를 이후 단계에 " +
                "넘기십시오.",
                message);
        }

        [Fact]
        public void BatchRunRowCreationMessage_TableWithoutIdentity_OmitsScopeIdentityClause()
        {
            var table = NoIdentityFirstStepTable();

            var message = InvokeBatchRunRowCreationMessage(table);

            Assert.DoesNotContain("SCOPE_IDENTITY", message);
            Assert.DoesNotContain("RunId를 발급하는", message);
            Assert.DoesNotContain("RunId를 이후 단계에", message);
            Assert.Equal(
                "계획서 전체에 `batch.BatchRunLock` 행을 만드는 지점이 없습니다. " +
                "이 테이블은 단계 목록의 첫 단계가 INSERT하는 계약인데, " +
                "생성 없이 UPDATE만 하면 0행이 갱신되어 실행 단위 자체가 존재하지 않습니다. " +
                "첫 단계에 INSERT를 두십시오.",
                message);
        }

        [Fact]
        public void BatchRunRowCreationMessage_TableWithIdentity_KeepsScopeIdentityClauseUnchanged()
        {
            var table = BatchControlContract.Find("batch.BatchRun");
            Assert.NotNull(table);

            var message = InvokeBatchRunRowCreationMessage(table!);

            Assert.Equal(
                "계획서 전체에 `batch.BatchRun` 행을 만드는 지점이 없습니다. " +
                "이 테이블은 단계 목록의 첫 단계가 INSERT하며 RunId를 발급하는 계약인데, " +
                "생성 없이 UPDATE만 하면 0행이 갱신되어 실행 단위 자체가 존재하지 않습니다. " +
                "첫 단계에 INSERT를 두고 SCOPE_IDENTITY()로 발급된 RunId를 이후 단계에 넘기십시오.",
                message);
        }

        // [재리뷰 수정 - B-2와 같은 부류, 방향은 오탐] 문서 전체를 한 번에 BlankCommentsAndStrings로
        // 지우면, 산문 속 영어 소유격 아포스트로피(the orchestrator's run row) 하나가 문자열
        // 극성을 뒤집어 뒤따르는 정상 INSERT 펜스까지 공백으로 지운다 - batch.BatchRun을 올바르게
        // INSERT하는 계획서인데도 "행을 만드는 지점이 없다"고 오탐한다(실행 재현). 펜스 단위로
        // 지우면 산문의 아포스트로피는 펜스 밖이라 스캔에 들어오지 않는다.
        [Fact]
        public void ValidateConsolidated_AcceptsAnInsertWhenProseHasAnUnpairedApostropheBeforeTheFence()
        {
            var markdown = ConsolidatedDocumentWithStepBody(
                "이 단계는 오케스트레이터가 batch.BatchRun 행을 만든다 - the orchestrator's run row를 시작하는 지점이다.",
                @"
INSERT INTO [batch].[BatchRun] (JobName, BatchYmd, RunStatus, StartedAtUtc)
VALUES (@JobName, @BatchYmd, N'Running', SYSUTCDATETIME());
SET @RunId = SCOPE_IDENTITY();
UPDATE batch.BatchRun SET RunStatus = N'Succeeded' WHERE RunId = @RunId;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        // 위와 같은 오탐 방어를 대괄호 없는 한정 표기(batch.BatchRun)로도 잠근다.
        [Fact]
        public void ValidateConsolidated_AcceptsAnUnbracketedInsertWhenProseHasAnUnpairedApostropheBeforeTheFence()
        {
            var markdown = ConsolidatedDocumentWithStepBody(
                "이 단계는 오케스트레이터가 batch.BatchRun 행을 만든다 - the orchestrator's run row를 시작하는 지점이다.",
                @"
INSERT INTO batch.BatchRun (JobName, BatchYmd, RunStatus, StartedAtUtc)
VALUES (@JobName, @BatchYmd, N'Running', SYSUTCDATETIME());
SET @RunId = SCOPE_IDENTITY();
UPDATE batch.BatchRun SET RunStatus = N'Succeeded' WHERE RunId = @RunId;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        // 미탐 방어: 같은 산문 아포스트로피가 있어도, 펜스 안이 실제로 UPDATE만 하고
        // INSERT가 없으면 여전히 잡혀야 한다 - 펜스 단위 지우기가 오탐만 없애고
        // 진짜 결함까지 덮어 버리면 안 된다.
        [Fact]
        public void ValidateConsolidated_StillRejectsAnUpdateOnlyFenceEvenWithAnUnpairedApostropheInProse()
        {
            var markdown = ConsolidatedDocumentWithStepBody(
                "이 단계는 오케스트레이터가 the orchestrator's run row를 종료 처리하는 지점이다.",
                @"
UPDATE batch.BatchRun SET RunStatus = N'Succeeded', CompletedAtUtc = SYSUTCDATETIME()
WHERE RunId = @RunId;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        // 소프트 스킵 보존: 산문에만 batch.BatchRun이 언급되고 SQL 펜스에는 전혀 나오지
        // 않으면 여전히 소프트 스킵이어야 한다(산문은 실행 지시가 아니다). 이 수정 전에는
        // mentioned 판정이 문서 전체(산문 포함)를 봤으므로 산문 언급만으로 "언급됨"이
        // 성립해 이 케이스가 오히려 오류로 잡혔다 - 펜스 단위로 좁히면서 소프트 스킵이
        // 바로잡힌다.
        [Fact]
        public void ValidateConsolidated_SkipsWhenBatchRunIsMentionedOnlyInProseNotInAnySqlFence()
        {
            var markdown = ConsolidatedDocumentWithStepBody(
                "이 단계는 batch.BatchRun 행의 상태를 참고만 한다.",
                "SELECT 1;");

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        // 여러 펜스: 첫 펜스는 UPDATE만 하고, 둘째 펜스가 INSERT한다 - 펜스별 판정을
        // OR로 합치므로 문서 전체 판정과 같은 의미가 되어 오류가 없어야 한다.
        [Fact]
        public void ValidateConsolidated_AcceptsWhenOneFenceUpdatesAndAnotherFenceInsertsBatchRun()
        {
            var markdown = $$"""
                ## 통합 배치 아키텍처 개요

                내용.

                ## Mermaid 기반 통합 흐름도

                ```mermaid
                flowchart TD
                A["시작"] --> B["끝"]
                ```

                ## 단계별 이행 상세 및 의사코드

                ```sql
                UPDATE batch.BatchRun SET RunStatus = N'Running' WHERE RunId = @RunId;
                ```

                ```sql
                INSERT INTO batch.BatchRun (JobName, BatchYmd, RunStatus, StartedAtUtc)
                VALUES (@JobName, @BatchYmd, N'Running', SYSUTCDATETIME());
                SET @RunId = SCOPE_IDENTITY();
                ```

                ## 통합 데이터 정합성 검증 SQL 세트

                내용.
                """;

            var result = new MechanicalValidator().ValidateConsolidated(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.BatchRunRowNeverCreated);
        }

        /// <summary>
        /// RequiredConsolidatedHeaders의 네 헤더를 갖춘 최소 통합 문서를 만들고,
        /// 단계 상세 절에 주어진 SQL을 싣는다. 검증 SQL 절은 비워 둔다 -
        /// 카티전 검사가 함께 발화하면 이 테스트가 무엇을 보는지 흐려진다.
        /// </summary>
        private static string ConsolidatedDocumentWithStepBody(string sql) => $$"""
            ## 통합 배치 아키텍처 개요

            내용.

            ## Mermaid 기반 통합 흐름도

            ```mermaid
            flowchart TD
            A["시작"] --> B["끝"]
            ```

            ## 단계별 이행 상세 및 의사코드

            ```sql
            {{sql}}
            ```

            ## 통합 데이터 정합성 검증 SQL 세트

            내용.
            """;

        /// <summary>
        /// <see cref="ConsolidatedDocumentWithStepBody(string)"/>와 같은 골격이되,
        /// SQL 펜스 앞에 산문 한 줄을 끼워 넣는다 - 산문 속 짝 없는 아포스트로피가
        /// 뒤따르는 펜스에 영향을 주는지(B-2와 같은 부류) 보는 회귀에 쓴다.
        /// </summary>
        private static string ConsolidatedDocumentWithStepBody(string prose, string sql) => $$"""
            ## 통합 배치 아키텍처 개요

            내용.

            ## Mermaid 기반 통합 흐름도

            ```mermaid
            flowchart TD
            A["시작"] --> B["끝"]
            ```

            ## 단계별 이행 상세 및 의사코드

            {{prose}}

            ```sql
            {{sql}}
            ```

            ## 통합 데이터 정합성 검증 SQL 세트

            내용.
            """;

        private static string ConsolidatedDocumentWithVerificationSql(string sql) => $"""
            ## 통합 배치 아키텍처 개요

            내용.

            ## Mermaid 기반 통합 흐름도

            ```mermaid
            flowchart TD
            A["시작"] --> B["끝"]
            ```

            ## 단계별 이행 상세 및 의사코드

            내용.

            ## 통합 데이터 정합성 검증 SQL 세트

            ```sql
            {sql}
            ```
            """;
    
        // === 범위 구별 (2026-08-19 축 A 감사) ================================
        //
        // 파생 테이블 내부 술어를 수집하면서 같은 표기의 컬럼이 최상위와 파생 양쪽에
        // 걸리는 형태가 생긴다. 별칭이 다르면 기존 (연산, 라인, 컬럼) 키로도 갈리지만,
        // 한정자 없는 컬럼이면 키가 겹친다 - 그때 범위를 대조하지 않으면 명세서가 두 행을
        // 모두 "최상위"로 적어도 통과한다. 파생 테이블 필터가 사라진 것을 못 잡는다는 뜻이고,
        // COMM_UPD:243·EXCEPTION_PROC:375가 정확히 그 자리에서 새어 나갔다.
        [Fact]
        public void Validate_TwoScopesWrittenAsTheSameScope_ShouldBeAnError()
        {
            var top = new SetPredicateFact(
                "UPDATE", 169, "UseState", false, new[] { "1" }, 4, "=", "최상위", "UseState = 1");
            var derived = new SetPredicateFact(
                "UPDATE", 169, "UseState", false, new[] { "1" }, 4, "=", "파생 테이블 D", "UseState = 1");
            var expectations = EmptyExpectations() with { SetPredicates = new[] { top, derived } };

            // 행 수는 맞지만 둘 다 최상위로 적었다 - 파생 테이블 필터가 문서에서 사라졌다.
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 4 | 169 | UseState | = | 최상위 | 1 | 1 | UseState = 1 |\n"
                + "| UPDATE 4 | 169 | UseState | = | 최상위 | 1 | 1 | UseState = 1 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_BothScopesWrittenCorrectly_ShouldPass()
        {
            var top = new SetPredicateFact(
                "UPDATE", 169, "UseState", false, new[] { "1" }, 4, "=", "최상위", "UseState = 1");
            var derived = new SetPredicateFact(
                "UPDATE", 169, "UseState", false, new[] { "1" }, 4, "=", "파생 테이블 D", "UseState = 1");
            var expectations = EmptyExpectations() with { SetPredicates = new[] { top, derived } };

            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 4 | 169 | UseState | = | 최상위 | 1 | 1 | UseState = 1 |\n"
                + "| UPDATE 4 | 169 | UseState | = | 파생 테이블 D | 1 | 1 | UseState = 1 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        // === 「술어 원문」 열 (2026-08-22 축 A 재감사 ③ Task 7) =================
        //
        // 표가 8열이 되면서 리터럴 목록 칸이 마지막이 아니라 뒤에서 세 번째가 됐다.
        // 인덱스를 안 고치면 원문 칸을 리터럴로 읽어 <b>옳게 옮긴 표</b>를 틀렸다고
        // 한다 - 이 저장소가 되풀이해 겪은 실패 모양이다
        // (ExtractSetPredicateLiteralCell 문서의 실측 근거).
        [Fact]
        public void CheckSetPredicates_LiteralCellShiftedByNewColumn_ShouldStillCompare()
        {
            var fact = new SetPredicateFact(
                "UPDATE", 130, "A.PGNAME", false, new[] { "'KFTC'", "'YELOPAY'" }, 4, "IN", "최상위",
                "A.PGNAME IN ('KFTC', 'YELOPAY')");
            var expectations = EmptyExpectations() with { SetPredicates = new[] { fact } };

            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 4 | 130 | A.PGNAME | IN | 최상위 | 2 | 'KFTC', 'YELOPAY' | "
                + "A.PGNAME IN ('KFTC', 'YELOPAY') |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void CheckSetPredicates_PredicateTextSummarized_ShouldReport()
        {
            // 분해되지 않은 항은 원문 칸이 <b>유일한</b> 기록처다 - 컬럼·연산·원소 수·
            // 리터럴이 전부 "—"이므로, 원문을 요약해 옮기면 그 필터가 문서에서 사라진다.
            var expectations = EmptyExpectations() with
            {
                SetPredicates = new[]
                {
                    new SetPredicateFact(
                        "UPDATE", 220, "—", false, Array.Empty<string>(), 7, "—", "최상위",
                        "(A.UseState <> 1 OR A.YMD = A.AYMD)")
                }
            };

            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 7 | 220 | — | — | 최상위 | — | — | 당일 이전 취소건 제외 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            // [왜 Type만으로는 부족한가 - 측정으로 확인함] 원문 칸을 대조하기 전에도
            // 이 문서는 SetPredicateMismatch를 냈다. 다만 이유가 달랐다 - 칸 인덱스를
            // 안 고친 파서가 마지막 칸(원문)을 리터럴 목록으로 읽어 "추가: 당일 이전
            // 취소건 제외"라고 보고했을 뿐이다. 그 메시지에는 <b>원본 술어</b>가 없다.
            // 그래서 "빠진 술어가 무엇인지 말하는가"로 단언한다 - 이것이 원문 대조가
            // 실제로 작동할 때만 참이 되는 조건이다.
            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetPredicateMismatch
                    && e.Message.Contains("(A.UseState <> 1 OR A.YMD = A.AYMD)"));
        }

        [Fact]
        public void CheckSetPredicates_UndecomposedRowCopiedVerbatim_ShouldPass()
        {
            // 위 실패 사례의 짝. 같은 재료를 원문 그대로 옮기면 통과해야 한다 -
            // 이 짝이 없으면 위 테스트는 "분해되지 않은 행을 늘 틀렸다고 한다"로도
            // 만족된다.
            var expectations = EmptyExpectations() with
            {
                SetPredicates = new[]
                {
                    new SetPredicateFact(
                        "UPDATE", 220, "—", false, Array.Empty<string>(), 7, "—", "최상위",
                        "(A.UseState <> 1 OR A.YMD = A.AYMD)")
                }
            };

            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 7 | 220 | — | — | 최상위 | — | — | (A.UseState <> 1 OR A.YMD = A.AYMD) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void CheckSetPredicates_TwoUndecomposedTermsOnOneLine_OneTextWrittenTwice_ShouldReport()
        {
            // 분해되지 않은 행은 컬럼·연산·원소 수·리터럴이 전부 "—"라서, 원문을 키에서
            // 빼면 같은 줄의 서로 다른 두 항이 완전히 같은 키가 된다. 그러면 문서가 한
            // 항의 원문을 두 번 적어도 "행이 사실 수만큼 있다"는 이유로 통과한다 -
            // 나머지 한 항이 문서에서 통째로 사라진 것을 못 잡는다는 뜻이다.
            var expectations = EmptyExpectations() with
            {
                SetPredicates = new[]
                {
                    new SetPredicateFact(
                        "UPDATE", 220, "—", false, Array.Empty<string>(), 7, "—", "최상위",
                        "(A.UseState <> 1 OR A.YMD = A.AYMD)"),
                    new SetPredicateFact(
                        "UPDATE", 220, "—", false, Array.Empty<string>(), 7, "—", "최상위",
                        "A.AYMD >= '20230101'")
                }
            };

            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 7 | 220 | — | — | 최상위 | — | — | (A.UseState <> 1 OR A.YMD = A.AYMD) |\n"
                + "| UPDATE 7 | 220 | — | — | 최상위 | — | — | (A.UseState <> 1 OR A.YMD = A.AYMD) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            // 사라진 항의 원문이 메시지에 실려야 어느 술어가 빠졌는지 보인다.
            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetPredicateMismatch
                    && e.Message.Contains("A.AYMD >= '20230101'"));
        }

        [Fact]
        public void CheckSetPredicates_TwoUndecomposedTermsOnOneLine_BothWrittenVerbatim_ShouldPass()
        {
            // 위 실패 사례의 짝 - 같은 입력에서 둘째 행의 원문만 사실대로 바꾸면
            // 통과해야 한다.
            var expectations = EmptyExpectations() with
            {
                SetPredicates = new[]
                {
                    new SetPredicateFact(
                        "UPDATE", 220, "—", false, Array.Empty<string>(), 7, "—", "최상위",
                        "(A.UseState <> 1 OR A.YMD = A.AYMD)"),
                    new SetPredicateFact(
                        "UPDATE", 220, "—", false, Array.Empty<string>(), 7, "—", "최상위",
                        "A.AYMD >= '20230101'")
                }
            };

            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 7 | 220 | — | — | 최상위 | — | — | (A.UseState <> 1 OR A.YMD = A.AYMD) |\n"
                + "| UPDATE 7 | 220 | — | — | 최상위 | — | — | A.AYMD >= '20230101' |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void CheckSetPredicates_PredicateTextKeepsOuterParentheses_ShouldPass()
        {
            // [괄호 계약] Task 6은 바깥 괄호를 포함한 원문을 그대로 담는다
            // (ParenthesizedDecomposableTerm_ShouldStillDecompose가 못박은 계약).
            // L1이 대조 전에 괄호를 벗기거나 공백을 정규화하면, 렌더된 그대로 옮긴
            // 표가 거부된다 - §0이 막으려는 실패 모양이다. 바깥 괄호가 붙어도 분해는
            // 되므로, 리터럴 칸이 찬 갈래에서 못박는다.
            var expectations = EmptyExpectations() with
            {
                SetPredicates = new[]
                {
                    new SetPredicateFact(
                        "UPDATE", 88, "A.PGName", false, new[] { "'PLCard'" }, 2, "=", "최상위",
                        "(A.PGName = 'PLCard')")
                }
            };

            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 2 | 88 | A.PGName | = | 최상위 | 1 | 'PLCard' | (A.PGName = 'PLCard') |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void CheckSetPredicates_PredicateTextContainsPipe_RenderedEscaped_ShouldPass()
        {
            // 원문 칸도 EscapeTableCell을 거친다 - 비트 연산자 `|`가 든 항은 `\|`로
            // 이스케이프된 채 표에 나온다. 대조가 그 복원을 안 하면 옳게 옮긴 표가
            // 거부된다(컬럼 칸이 이미 겪은 실패 모양 -
            // Validate_SetPredicateColumnContainsPipe_RenderedEscaped_ShouldPass 참고).
            const string predicateText = "A.Flags | 4 = 4";
            var expectations = EmptyExpectations() with
            {
                SetPredicates = new[]
                {
                    new SetPredicateFact(
                        "UPDATE", 91, "—", false, Array.Empty<string>(), 3, "—", "최상위", predicateText)
                }
            };

            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| UPDATE 3 | 91 | — | — | 최상위 | — | — | {predicateText.Replace("|", "\\|")} |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        /// <summary>참조 함수 호출이 하나 있는 최소 SP. 세 테스트가 공유한다.</summary>
        private static SpDefinition ReferencedFunctionSp() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.CLVT = dbo.UF_GET_ROUND4VAT(A.CLCOMM)
    FROM   dbo.TSettleMst A
END",
            Dependencies = new List<DependencyInfo>
            {
                new() { Database = null, Schema = "dbo", Name = "UF_GET_ROUND4VAT", Type = "SQL_SCALAR_FUNCTION" }
            }
        };

        [Fact]
        public void From_WithReferencedFunctionCalls_ShouldExposeThem()
        {
            // 재료를 SpecExpectations가 들지 못하면 CheckReferencedFunctions가 대조할
            // 기준값이 없다. AiService가 프롬프트 표를 만들 때 쓰는 것과 같은 규칙으로
            // 이름 집합을 뽑아야 한다 - 두 곳이 갈리면 모델이 표를 그대로 베껴도
            // L1이 틀렸다고 하는 재현 불가능한 실패가 난다.
            var expectations = SpecExpectations.From(ReferencedFunctionSp());

            Assert.NotNull(expectations);
            var call = Assert.Single(expectations!.ReferencedFunctionCalls);
            Assert.Equal("UF_GET_ROUND4VAT", call.QualifiedName);
            Assert.Equal("UPDATE", call.Operation);
            Assert.Equal(1, call.StatementOrdinal);
        }

        [Fact]
        public void Validate_MissingReferencedFunctionTable_ShouldReportError()
        {
            // 2026-08-20 최종 리뷰 M1. 조립기가 표를 프롬프트에 넣지만 모델이 그것을
            // 옮겼는지는 아무도 확인하지 않았다 - 설계가 집합 술어 표의 성공 요인으로
            // 꼽은 넷 중 "검증기가 확인한다"만 이 표에 없었다.
            var markdown = "## 개요\n내용\n\n## CRUD 분석\n표 없음\n";

            var result = new MechanicalValidator().Validate(
                markdown, SpecExpectations.From(ReferencedFunctionSp()));

            Assert.Contains(
                result.Errors,
                e => e.Contains(DmlScopeExtractor.ReferencedFunctionTableHeading));
        }

        [Fact]
        public void Validate_NoFunctionCalls_ShouldNotDemandTheReferencedFunctionTable()
        {
            // 거짓 실패 방지. 함수를 부르지 않는 SP는 조립기도 표를 내지 않으므로
            // (AiService는 functionCalls.Count > 0일 때만 렌더한다) 검사가 표를 요구하면
            // 그 SP는 영영 L1을 통과하지 못한다. 기존 세 표 검사가 전부 사실 유무로
            // 조기 반환하는 이유가 이것이다.
            var sp = new SpDefinition
            {
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = 1
    FROM   dbo.TSettleMst A
END",
                Dependencies = new List<DependencyInfo>()
            };

            var expectations = SpecExpectations.From(sp);
            Assert.Empty(expectations!.ReferencedFunctionCalls);

            var result = new MechanicalValidator().Validate("## 개요\n내용\n", expectations);

            Assert.DoesNotContain(
                result.Errors,
                e => e.Contains(DmlScopeExtractor.ReferencedFunctionTableHeading));
        }

        /// <summary>
        /// [2026-08-21 최종 브랜치 리뷰 재라운드 ⑤에서 시작, 2026-08-23 의미가 바뀜]
        /// 처음 이 테스트는 `CheckReferencedFunctions`가 헤딩 존재만 보고 행 내용을 대조하지
        /// 않는다는 것을 고정했다. 2026-08-23부터 그 검사는 행 단위(함수 · 호출 위치 · 인자)로
        /// 대조하므로, 이제 이 테스트가 고정하는 것은 <b>파이프 왕복</b>이다 - 인자에 `|`가
        /// 들면 렌더러가 `\|`로 이스케이프하고 SplitRow가 복원하므로, 실제 추출기가 낸
        /// 사실과 이스케이프된 행이 그대로 맞아야 한다. 단언을 새 ErrorType 부재로 좁혀
        /// 행 대조가 실제로 이 행을 받아들였음을 본다.
        /// </summary>
        [Fact]
        public void Validate_ReferencedFunctionCallExpressionContainsPipe_HeadingPresent_ShouldStillPass()
        {
            var sp = new SpDefinition
            {
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                ObjectType = CodeObjectType.Procedure,
                DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.CLVT = dbo.UF_GET_ROUND4VAT(A.CLCOMM | 1)
    FROM   dbo.TSettleMst A
END",
                Dependencies = new List<DependencyInfo>
                {
                    new() { Database = null, Schema = "dbo", Name = "UF_GET_ROUND4VAT", Type = "SQL_SCALAR_FUNCTION" }
                }
            };

            var expectations = SpecExpectations.From(sp);
            var call = Assert.Single(expectations!.ReferencedFunctionCalls);
            Assert.Contains("|", call.CallExpression, StringComparison.Ordinal);

            var markdown = DmlScopeExtractor.ReferencedFunctionTableHeading
                + "\n| 함수 | 호출 위치 | 호출식 | 링크 |\n| :--- | :--- | :--- | :--- |\n"
                + "| UF_GET_ROUND4VAT | UPDATE 1 (라인 5) | dbo.UF_GET_ROUND4VAT(A.CLCOMM \\| 1) | - |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(
                result.DetailedErrors, e => e.Type == ErrorType.ReferencedFunctionMismatch);
        }

        [Fact]
        public void SuggestedPromptFix_ShouldCarryMachineTableErrorsToTheModel()
        {
            // 2026-08-20 리뷰 #1. BuildSuggestedPromptFix가 DetailedErrors를 일곱 타입으로
            // 버킷팅하는데 SetPredicateMismatch가 그중에 없어, 기계 확정 표 관련 오류는
            // 일반 머리말과 맺음말만 남고 <b>내용이 통째로 빠진 채</b> 모델에게 간다.
            // SuggestedPromptFix가 모델에 닿는 유일한 통로다(result.Errors는 사람에게만
            // 간다). 그래서 이 검사는 재시도 예산만 쓰고 "형식 오류가 있었다"만 알린다.
            //
            // 세 표(DML 범위·파생 테이블·집합 술어)가 전부 같은 구멍을 갖고 있었다.
            var markdown = "## 개요\n내용\n\n## CRUD 분석\n표 없음\n";

            var result = new MechanicalValidator().Validate(
                markdown, SpecExpectations.From(ReferencedFunctionSp()));

            Assert.Contains(
                DmlScopeExtractor.ReferencedFunctionTableHeading,
                result.SuggestedPromptFix);
        }

        // ==========================================================================
        // 작업 5: L1 앵커 - 잠금 힌트 · 객체 선언 · ORDER BY
        //
        // 참조 함수 표가 검사 없이 한 판 나갔던 실수(위 SuggestedPromptFix_
        // ShouldCarryMachineTableErrorsToTheModel 테스트 참고)를 반복하지 않는다.
        // 표 셋 각각에 "재료가 있는데 표가 없으면 오류" 앵커를 건다.
        // ==========================================================================

        /// <summary>잠금 힌트가 하나(NOLOCK) 있는 최소 SP.</summary>
        private static SpDefinition SpWithLockHints() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = 1
    FROM dbo.TSettleMst A WITH (NOLOCK)
END",
            Dependencies = new List<DependencyInfo>()
        };

        /// <summary>WITH 절이 없는 함수 - ObjectDeclarationFact.WithOptions가 빈 목록이다.</summary>
        private static SpDefinition FunctionWithoutWithOptions() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UF_GET_X", CodeObjectType.Function),
            Schema = "dbo",
            Name = "UF_GET_X",
            ObjectType = CodeObjectType.Function,
            DdlText = @"
CREATE FUNCTION dbo.UF_GET_X(@p INT)
RETURNS INT
AS
BEGIN
    RETURN @p + 1
END",
            Dependencies = new List<DependencyInfo>()
        };

        /// <summary>
        /// 잠금 힌트가 없는 SP. 함수도 아니다 - 두 표 모두 재료가 없다.
        ///
        /// [FROM 절을 두지 않는 이유 - 실측] LockHintVisitor.CollectFrom은 FROM의
        /// 테이블 참조를 힌트 유무와 무관하게 전부 사실로 낸다(Hints가 빈 목록이어도
        /// 행이 생긴다) - 힌트가 조건인 것은 UPDATE/DELETE의 <b>대상 노드</b>뿐이다
        /// (RecordTargetHint 문서 참고). `UPDATE A SET ... FROM dbo.T A`처럼 FROM이
        /// 있는 픽스처를 썼더니 힌트가 하나도 없는데도 LockHints.Count가 1이 되어
        /// 이 테스트가 실패했다 - "재료가 없다"를 보이려면 FROM 자체가 없어야 한다.
        /// </summary>
        private static SpDefinition SpWithoutAnyScan() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE dbo.TSettleMst SET C = 1 WHERE ID = 1
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_ShouldFlagMissingLockHintTable()
        {
            // 표만 넣고 검사를 안 세우면 모델이 옮겼는지 아무도 모른다. 참조 함수 표가
            // 그 상태로 한 판 나갔고 L1 앵커를 나중에 따로 붙여야 했다.
            var markdown = "## 개요\n내용\n\n## CRUD 분석\n표 없음\n";

            var result = new MechanicalValidator().Validate(
                markdown, SpecExpectations.From(SpWithLockHints()));

            Assert.False(result.IsValid);
            Assert.Contains(
                result.DetailedErrors,
                e => e.Message.Contains(DmlScopeExtractor.LockHintTableHeading));
        }

        [Fact]
        public void Validate_ShouldFlagMissingObjectDeclarationTable()
        {
            var markdown = "## 개요\n내용\n";

            var result = new MechanicalValidator().Validate(
                markdown, SpecExpectations.From(FunctionWithoutWithOptions()));

            Assert.False(result.IsValid);
            Assert.Contains(
                result.DetailedErrors,
                e => e.Message.Contains(
                    ObjectDeclarationExtractor.ObjectDeclarationTableHeading));
        }

        [Fact]
        public void Validate_ShouldNotFlagWhenThereIsNoMaterial()
        {
            // 재료가 없으면 검사하지 않는다. 잠금 힌트가 없는 객체, 함수가 아닌 객체.
            var markdown = "## 개요\n내용\n\n## CRUD 분석\n표 없음\n";

            var result = new MechanicalValidator().Validate(
                markdown, SpecExpectations.From(SpWithoutAnyScan()));

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Message.Contains(DmlScopeExtractor.LockHintTableHeading));
            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Message.Contains(ObjectDeclarationExtractor.ObjectDeclarationTableHeading));
        }

        [Fact]
        public void From_ShouldExposeLockHintsAndObjectDeclaration()
        {
            // SpecExpectations.From의 조기 반환 AND 사슬에 두 재료를 잇지 않으면 재료가
            // 있는데도 기대값 전체가 null이 되어 아래 두 검사가 한 번도 돌지 않는다.
            var lockExpectations = SpecExpectations.From(SpWithLockHints());
            Assert.NotNull(lockExpectations);
            Assert.Single(lockExpectations!.LockHints);
            Assert.Null(lockExpectations.ObjectDeclaration);

            var functionExpectations = SpecExpectations.From(FunctionWithoutWithOptions());
            Assert.NotNull(functionExpectations);
            Assert.NotNull(functionExpectations!.ObjectDeclaration);
            Assert.Empty(functionExpectations.ObjectDeclaration!.WithOptions);
            Assert.Empty(functionExpectations.LockHints);
        }

        [Fact]
        public void Validate_LockHintRowPresentWithCorrectValue_ShouldPass()
        {
            var expectations = SpecExpectations.From(SpWithLockHints());
            var fact = Assert.Single(expectations!.LockHints);

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | {fact.Table} | "
                + $"{fact.Alias} | {fact.Scope} | {string.Join(", ", fact.Hints)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Message.Contains(DmlScopeExtractor.LockHintTableHeading));
        }

        /// <summary>
        /// 한 문장에 스캔 자리가 둘인 SP. FROM 절의 JOIN 두 참조가 한 줄에 있어
        /// 두 LockHintFact의 Line이 같다 - LockHintVisitor.Add의 중복 제거 키
        /// (Operation, StatementOrdinal, Table, Alias, Line) 문서가 실측으로 남긴
        /// 그 모양이다.
        /// </summary>
        private static SpDefinition SpWithTwoScansOnSameLine() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = B.D
    FROM dbo.TA A WITH (NOLOCK) JOIN dbo.TB B WITH (NOLOCK) ON A.ID = B.ID
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_TwoLockHintFactsShareALine_BothMustBeIdentifiedIndependently()
        {
            // [실측 근거] 이 픽스처는 두 사실의 Line이 정확히 같다(둘 다 FROM 절의
            // 같은 물리 줄에 있다). CheckDmlScopeTable처럼 Line 토큰만으로 행을
            // 찾으면, 문서가 dbo.TA/A의 행만 옮기고 dbo.TB/B의 행을 통째로 빠뜨려도
            // "그 Line 토큰이 문서 어딘가에 있다"는 사실만으로 통과해 버린다 - 정확히
            // INS_EXTRA4PLCARD에서 감사가 잡은 결함(TPGProperty가 P·Y에는 붙고 PG에는
            // 안 붙는데 뭉뚱그려 서술된 것)과 같은 실패 모양이다.
            var expectations = SpecExpectations.From(SpWithTwoScansOnSameLine());
            Assert.Equal(2, expectations!.LockHints.Count);
            var first = expectations.LockHints[0];
            var second = expectations.LockHints[1];
            Assert.Equal(first.Line, second.Line);
            Assert.NotEqual(first.Table, second.Table);

            // 문서는 첫 번째 사실의 행만 옮기고 두 번째는 빠뜨린다.
            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {first.Operation} {first.StatementOrdinal} | {first.Line} | {first.Table} | "
                + $"{first.Alias} | {first.Scope} | {string.Join(", ", first.Hints)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            // 헤딩은 있고(첫 번째 사실의 행도 있다) 두 번째 사실의 행만 빠졌으므로
            // 메시지는 "표가 없다"가 아니라 "행이 없다"이다 - Type과 누락된 테이블
            // 이름(RawContext)으로 대조한다.
            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.LockHintTableMissing
                    && e.RawContext != null && e.RawContext.Contains(second.Table));
        }

        [Fact]
        public void Validate_TwoLockHintFactsShareALine_BothRowsPresent_ShouldPass()
        {
            var expectations = SpecExpectations.From(SpWithTwoScansOnSameLine());
            var first = expectations!.LockHints[0];
            var second = expectations.LockHints[1];

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {first.Operation} {first.StatementOrdinal} | {first.Line} | {first.Table} | "
                + $"{first.Alias} | {first.Scope} | {string.Join(", ", first.Hints)} |\n"
                + $"| {second.Operation} {second.StatementOrdinal} | {second.Line} | {second.Table} | "
                + $"{second.Alias} | {second.Scope} | {string.Join(", ", second.Hints)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Message.Contains(DmlScopeExtractor.LockHintTableHeading));
        }

        // === 새 문장 종류·새 범위 값 (2026-08-22 축 A 재감사 ③ Task 7) ==========
        //
        // 스펙 §4 E가 "변경이 없을 것으로 보이나 테스트로 확인한다"고 남긴 자리다.
        // 확인 없이 넘기면 표만 넓어지고 검사는 침묵하는 상태가 된다.
        [Fact]
        public void CheckLockHints_SelectAndIfRows_ShouldCompareLikeDmlRows()
        {
            var expectations = EmptyExpectations() with
            {
                LockHints = new[]
                {
                    new LockHintFact("SELECT", 1, 22, "PaymentDB.dbo.TExtraSettleIn", "-", "최상위", new[] { "NOLOCK" }),
                    new LockHintFact("IF", 1, 31, "TSettleMst", "-", "최상위", new[] { "NOLOCK" }),
                    new LockHintFact("UPDATE", 12, 529, "TSettleMst", "-", "하위 질의", new[] { "NOLOCK" })
                }
            };

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| SELECT 1 | 22 | PaymentDB.dbo.TExtraSettleIn | - | 최상위 | NOLOCK |\n"
                + "| IF 1 | 31 | TSettleMst | - | 최상위 | NOLOCK |\n"
                + "| UPDATE 12 | 529 | TSettleMst | - | 하위 질의 | NOLOCK |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.LockHintTableMissing);
        }

        [Fact]
        public void CheckLockHints_DroppedSelectRow_ShouldReport()
        {
            // 위 통과 사례의 짝 - 같은 입력에서 SELECT 행만 빼면 걸려야 한다.
            // 짝이 없으면 위 테스트는 "이 검사가 아무것도 묻지 않는다"로도 만족된다.
            var expectations = EmptyExpectations() with
            {
                LockHints = new[]
                {
                    new LockHintFact("SELECT", 1, 22, "PaymentDB.dbo.TExtraSettleIn", "-", "최상위", new[] { "NOLOCK" }),
                    new LockHintFact("IF", 1, 31, "TSettleMst", "-", "최상위", new[] { "NOLOCK" })
                }
            };

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| IF 1 | 31 | TSettleMst | - | 최상위 | NOLOCK |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.LockHintTableMissing
                    && e.RawContext != null && e.RawContext.Contains("PaymentDB.dbo.TExtraSettleIn"));
        }

        /// <summary>파생 테이블 안에 스캔이 있는 SP. LockHintFact.Scope가 "파생"으로 찍힌다.</summary>
        private static SpDefinition SpWithDerivedScopeLockHint() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = 1
    FROM (SELECT X.C FROM dbo.TX X WITH (NOLOCK)) AS A
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_LockHintRowMissingEntirely_ForDerivedScopeFact_ShouldFlag()
        {
            // [실측 근거 - UP_UTIL_SETTLE_INS] 초안 규칙("파생 테이블 안으로 내려가지
            // 않는다")은 파생 테이블 하나뿐인 최상위 FROM에서 스캔이 통째로 0행이
            // 되어 PaymentDB.dbo.TTxMst WITH(NOLOCK, INDEX=...)를 포함한 네 테이블의
            // 힌트가 사라졌다(DmlScopeExtractor.ExtractLockHints 문서 참고). 이 검사가
            // 파생 스코프 행의 부재를 잡지 못하면 그 결함이 L1을 다시 통과한다.
            var expectations = SpecExpectations.From(SpWithDerivedScopeLockHint());
            var fact = Assert.Single(expectations!.LockHints);
            Assert.Equal("파생", fact.Scope);

            var markdown = "## 개요\n내용\n\n## CRUD 분석\n표 없음\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(
                result.DetailedErrors,
                e => e.Message.Contains(DmlScopeExtractor.LockHintTableHeading));
        }

        [Fact]
        public void Validate_LockHintRowScopeMislabeled_ShouldFlag()
        {
            // 행 자체는 있지만 범위 칸이 "파생" 대신 "최상위"로 잘못 적혔다 - 표는
            // 채워졌지만 내용이 틀린 경우다. 범위 칸도 대조 대상이어야 이 결함이 잡힌다.
            var expectations = SpecExpectations.From(SpWithDerivedScopeLockHint());
            var fact = Assert.Single(expectations!.LockHints);

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | {fact.Table} | "
                + $"{fact.Alias} | 최상위 | {string.Join(", ", fact.Hints)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.LockHintTableMissing);
        }

        [Fact]
        public void Validate_LockHintRowScopeCorrect_ShouldPass()
        {
            var expectations = SpecExpectations.From(SpWithDerivedScopeLockHint());
            var fact = Assert.Single(expectations!.LockHints);

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | {fact.Table} | "
                + $"{fact.Alias} | {fact.Scope} | {string.Join(", ", fact.Hints)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Message.Contains(DmlScopeExtractor.LockHintTableHeading));
        }

        /// <summary>값 있는 힌트(INDEX)를 지는 SP. RenderHint가 원문 토큰을 그대로 낸다.</summary>
        private static SpDefinition SpWithValueBearingHint() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = 1
    FROM dbo.TX A WITH (NOLOCK, INDEX(CIDX_TX_1))
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_LockHintValueCollapsedToKindOnly_ShouldFlag()
        {
            // [실측 근거] INDEX=CIDX_x -> INDEX처럼 "종류만 렌더하고 값을 버리는" 결함이
            // 이 배치에서 세 번 났다(작업 3 ObjectDeclarationExtractor 문서 참고). 힌트
            // 칸도 같은 함정이 있다 - 존재만 보고 값을 안 보면 "INDEX"만 적힌 표가
            // "INDEX(CIDX_TX_1)"을 옮긴 것으로 오판정된다.
            var expectations = SpecExpectations.From(SpWithValueBearingHint());
            var fact = Assert.Single(expectations!.LockHints);
            Assert.Contains(fact.Hints, h => h.StartsWith("INDEX", StringComparison.Ordinal) && h != "INDEX");

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | {fact.Table} | "
                + $"{fact.Alias} | {fact.Scope} | NOLOCK, INDEX |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.LockHintTableMissing);
        }

        [Fact]
        public void Validate_LockHintValuePreserved_ShouldPass()
        {
            var expectations = SpecExpectations.From(SpWithValueBearingHint());
            var fact = Assert.Single(expectations!.LockHints);

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | {fact.Table} | "
                + $"{fact.Alias} | {fact.Scope} | {string.Join(", ", fact.Hints)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Message.Contains(DmlScopeExtractor.LockHintTableHeading));
        }

        /// <summary>
        /// FROM 참조가 대괄호 식별자 안에 `|`를 지는 SP. 실물에서는 드물지만 T-SQL
        /// 문법상 유효한 식별자다(`[T|X]` -> `SchemaObject.Identifiers[i].Value`가
        /// 대괄호를 벗기고 "T|X"를 그대로 낸다).
        /// </summary>
        private static SpDefinition SpWithPipeInTableName() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = 1
    FROM dbo.[T|X] A WITH (NOLOCK)
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_LockHintTableNameContainsPipe_RenderedEscaped_ShouldPass()
        {
            // [2026-08-21 최종 리뷰 Important 1] AiService.EscapeTableCell은 렌더 시점에
            // 셀 안의 `|`를 `\|`로 바꾼다(셀 경계로 읽히지 않도록). 이 테스트는 렌더가
            // 실제로 내놓는 것과 같은 표(테이블 칸이 이스케이프된)를 흉내 낸다 -
            // CheckLockHints가 이 왕복을 되돌리지 못하면, 모델이 표를 원문 그대로
            // 옮겨도 대조가 실패한다(row.Split('|')가 `\|` 자리에서도 셀을 쪼갠다).
            var expectations = SpecExpectations.From(SpWithPipeInTableName());
            var fact = Assert.Single(expectations!.LockHints);
            Assert.Contains("|", fact.Table, StringComparison.Ordinal);

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} {fact.StatementOrdinal} | {fact.Line} | {fact.Table.Replace("|", "\\|")} | "
                + $"{fact.Alias} | {fact.Scope} | {string.Join(", ", fact.Hints)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.LockHintTableMissing);
        }

        /// <summary>EXECUTE AS CALLER를 지는 함수. RenderExecuteAs가 값을 원문으로 낸다.</summary>
        private static SpDefinition FunctionWithExecuteAsOption() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UF_GET_X", CodeObjectType.Function),
            Schema = "dbo",
            Name = "UF_GET_X",
            ObjectType = CodeObjectType.Function,
            DdlText = @"
CREATE FUNCTION dbo.UF_GET_X(@p INT)
RETURNS INT
WITH EXECUTE AS CALLER
AS
BEGIN
    RETURN @p + 1
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_ObjectDeclarationValueCollapsedToKindOnly_ShouldFlag()
        {
            // [실측 근거] EXECUTE AS CALLER -> EXECUTEAS처럼 주체가 사라지는 결함이
            // ObjectDeclarationExtractor.RenderExecuteAs 문서에 실측으로 남아 있다.
            // 이 검사가 옵션 종류만 보고 값을 안 보면 그 결함이 L1을 다시 통과한다.
            var expectations = SpecExpectations.From(FunctionWithExecuteAsOption());
            var fact = expectations!.ObjectDeclaration!;
            Assert.Equal(new[] { "EXECUTE AS CALLER" }, fact.WithOptions);

            var markdown = WrapSpec("표 없음") + "\n"
                + ObjectDeclarationExtractor.ObjectDeclarationTableHeading + "\n"
                + "| 객체 | WITH 옵션 |\n"
                + "| :--- | :--- |\n"
                + $"| {fact.QualifiedName} | EXECUTEAS |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.ObjectDeclarationTableMissing);
        }

        [Fact]
        public void Validate_ObjectDeclarationValuePreserved_ShouldPass()
        {
            var expectations = SpecExpectations.From(FunctionWithExecuteAsOption());
            var fact = expectations!.ObjectDeclaration!;

            var markdown = WrapSpec("표 없음") + "\n"
                + ObjectDeclarationExtractor.ObjectDeclarationTableHeading + "\n"
                + "| 객체 | WITH 옵션 |\n"
                + "| :--- | :--- |\n"
                + $"| {fact.QualifiedName} | {string.Join(", ", fact.WithOptions)} |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Message.Contains(ObjectDeclarationExtractor.ObjectDeclarationTableHeading));
        }

        /// <summary>
        /// EXECUTE AS '사용자'의 사용자명이 `|`를 지는 함수. RenderExecuteAs가 리터럴을
        /// 원문 그대로 되돌리므로(EscapeQuote는 따옴표만 다룬다) WithOptions 값에
        /// `|`가 그대로 남는다.
        /// </summary>
        private static SpDefinition FunctionWithPipeInExecuteAsLiteral() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UF_GET_X", CodeObjectType.Function),
            Schema = "dbo",
            Name = "UF_GET_X",
            ObjectType = CodeObjectType.Function,
            DdlText = @"
CREATE FUNCTION dbo.UF_GET_X(@p INT)
RETURNS INT
WITH EXECUTE AS 'my|user'
AS
BEGIN
    RETURN @p + 1
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_ObjectDeclarationOptionContainsPipe_RenderedEscaped_ShouldPass()
        {
            // [2026-08-21 최종 리뷰 Important 1] 같은 왕복 문제가 객체 선언 표에도 있다 -
            // CheckObjectDeclaration은 sectionText.Contains(expectedOptionsText)로
            // 이스케이프되지 않은 원문을 렌더된(이스케이프된) 구간 텍스트에서 찾는다.
            var expectations = SpecExpectations.From(FunctionWithPipeInExecuteAsLiteral());
            var fact = expectations!.ObjectDeclaration!;
            Assert.Contains("|", Assert.Single(fact.WithOptions), StringComparison.Ordinal);

            var expectedOptionsText = string.Join(", ", fact.WithOptions);
            var markdown = WrapSpec("표 없음") + "\n"
                + ObjectDeclarationExtractor.ObjectDeclarationTableHeading + "\n"
                + "| 객체 | WITH 옵션 |\n"
                + "| :--- | :--- |\n"
                + $"| {fact.QualifiedName} | {expectedOptionsText.Replace("|", "\\|")} |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ObjectDeclarationTableMissing);
        }

        [Fact]
        public void Validate_ObjectDeclarationNoOptions_RendersAsNone_ShouldPass()
        {
            var expectations = SpecExpectations.From(FunctionWithoutWithOptions());
            var fact = expectations!.ObjectDeclaration!;
            Assert.Empty(fact.WithOptions);

            var markdown = WrapSpec("표 없음") + "\n"
                + ObjectDeclarationExtractor.ObjectDeclarationTableHeading + "\n"
                + "| 객체 | WITH 옵션 |\n"
                + "| :--- | :--- |\n"
                + $"| {fact.QualifiedName} | (없음) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Message.Contains(ObjectDeclarationExtractor.ObjectDeclarationTableHeading));
        }

        /// <summary>ORDER BY를 지는 INSERT...SELECT 하나짜리 SP. STAT_PGCOLLECT_INS:113 실측 모양.</summary>
        private static SpDefinition SpWithOrderBy() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    INSERT INTO dbo.TX (A, B)
    SELECT A, B FROM dbo.TY
    ORDER BY A, B DESC
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_OrderByMissingFromDmlScopeTable_ShouldFlag()
        {
            // 세 번째 앵커 - 축 A 감사 실측: STAT_PGCOLLECT_INS:113의
            // `ORDER BY INYMD, CLIENTID, PGNAME, MALLID`가 문서 어디에도 없었다.
            // 브리프는 "ORDER BY는 기존 DML 범위 표의 칸이므로 그 표의 검사가 이미
            // 덮는다"고 적었지만, CheckDmlScopeTable(위)은 라인 토큰이 어느 행에든
            // 있는지만 보고 칸 내용은 대조하지 않으므로 ORDER BY 칸이 통째로
            // "(없음)"이어도 통과한다 - 이 검사가 그 구멍을 닫는다.
            var expectations = SpecExpectations.From(SpWithOrderBy());
            var fact = Assert.Single(expectations!.DmlScopeFacts);
            Assert.Equal(new[] { "A", "B DESC" }, fact.OrderByExpressions);

            var crud = DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼 | 기준일 파라미터 적용 | 조인 키 | ORDER BY |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} 1 | {fact.Line} | {fact.Target} | (없음) | (기준일 파라미터 없음) | (없음) | (없음) |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            // 행 자체는 있으므로(Line 토큰이 표에 있다) 메시지는 "표가 없다"가 아니라
            // "ORDER BY 값이 없다"이다 - 헤딩 문자열을 요구하지 않고 ORDER BY 값과
            // Type만 본다.
            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing
                    && e.Message.Contains("ORDER BY")
                    && e.Message.Contains("A, B DESC"));
        }

        [Fact]
        public void Validate_OrderByPresentElsewhereInDocumentButNotInDmlScopeTable_ShouldStillFlag()
        {
            // CheckDerivedTableDefinitions가 겪은 것과 같은 함정(주석 참고: 앵커가
            // "문서 전체 어딘가"에 있으면 통과시켜 21개 행이 전부 헛통과한 사건) -
            // ORDER BY 값도 표 구간 밖의 우연한 등장이 증거가 되어서는 안 된다.
            var expectations = SpecExpectations.From(SpWithOrderBy());
            var fact = Assert.Single(expectations!.DmlScopeFacts);

            var crud = "### 별도 서술\nORDER BY A, B DESC로 정렬해 삽입합니다.\n\n"
                + DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼 | 기준일 파라미터 적용 | 조인 키 | ORDER BY |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} 1 | {fact.Line} | {fact.Target} | (없음) | (기준일 파라미터 없음) | (없음) | (없음) |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing
                    && e.Message.Contains("ORDER BY")
                    && e.Message.Contains("A, B DESC"));
        }

        [Fact]
        public void Validate_OrderByPresentInDmlScopeTable_ShouldPass()
        {
            var expectations = SpecExpectations.From(SpWithOrderBy());
            var fact = Assert.Single(expectations!.DmlScopeFacts);

            var crud = DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼 | 기준일 파라미터 적용 | 조인 키 | ORDER BY |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} 1 | {fact.Line} | {fact.Target} | (없음) | (기준일 파라미터 없음) | (없음) | "
                + $"{string.Join(", ", fact.OrderByExpressions)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing
                    && e.Message.Contains("ORDER BY"));
        }

        /// <summary>ORDER BY 식이 비트 OR(`|`)를 지는 SP. 표에서 리터럴 그대로 보존된다.</summary>
        private static SpDefinition SpWithPipeInOrderBy() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    INSERT INTO dbo.TX (A, B)
    SELECT A, B FROM dbo.TY
    ORDER BY (A | B)
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_OrderByExpressionContainsPipe_RenderedEscaped_ShouldPass()
        {
            // [2026-08-21 최종 리뷰 Important 1] 같은 왕복 문제 - CheckOrderByExpressions는
            // sectionText.Contains(joined)로 이스케이프되지 않은 joined를 렌더된(이스케이프된)
            // 구간 텍스트에서 찾는다. ORDER BY 식은 임의 식이라(OrderByExpressionsOf 문서)
            // 비트 OR처럼 `|`가 든 식이 문법상 유효하다.
            var expectations = SpecExpectations.From(SpWithPipeInOrderBy());
            var fact = Assert.Single(expectations!.DmlScopeFacts);
            Assert.Contains("|", Assert.Single(fact.OrderByExpressions), StringComparison.Ordinal);

            var escapedOrderBy = string.Join(", ", fact.OrderByExpressions).Replace("|", "\\|");
            var crud = DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼 | 기준일 파라미터 적용 | 조인 키 | ORDER BY |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} 1 | {fact.Line} | {fact.Target} | (없음) | (기준일 파라미터 없음) | (없음) | "
                + $"{escapedOrderBy} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing
                    && e.Message.Contains("ORDER BY"));
        }

        /// <summary>GROUP BY를 지는 INSERT...SELECT 하나짜리 SP. UP_Util_Settle_Summary 실측 모양.</summary>
        private static SpDefinition SpWithGroupBy() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UP_Util_Settle_Summary", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "UP_Util_Settle_Summary",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.UP_Util_Settle_Summary
    @pi_strYMD CHAR(8)
AS
BEGIN
    INSERT INTO dbo.TSettleByTX (YMD, CLIENTID, CNT)
    SELECT YMD, CLIENTID, COUNT(*)
    FROM   dbo.TSettleMst
    WHERE  YMD = @pi_strYMD
    GROUP BY YMD, CLIENTID
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_GroupByMissingFromDmlScopeTable_ShouldFlag()
        {
            // UP_Util_Settle_Summary 실측 - GROUP BY 첫 키(YMD)가 매핑 표의 설명 칸에서만
            // 언급되다 표에서 통째로 빠졌다(🟡). CheckDmlScopeTable은 라인 토큰이 어느
            // 행에든 있는지만 보고(위 문서), GROUP BY 칸 값이 실제로 그 행에 있는지는
            // 대조하지 않으므로 GROUP BY 칸이 통째로 "(없음)"이어도 통과했다 - 이
            // 확장이 그 구멍을 닫는다.
            var expectations = SpecExpectations.From(SpWithGroupBy());
            var fact = Assert.Single(expectations!.DmlScopeFacts);
            Assert.Equal(new[] { "YMD", "CLIENTID" }, fact.GroupByColumns);

            var crud = DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼 | 기준일 파라미터 적용 | 조인 키 | GROUP BY | ORDER BY |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} 1 | {fact.Line} | {fact.Target} | (없음) | (기준일 파라미터 없음) | (없음) | (없음) | (없음) |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing
                    && e.Message.Contains("GROUP BY")
                    && e.Message.Contains("YMD, CLIENTID"));
        }

        [Fact]
        public void Validate_GroupByPresentInDmlScopeTable_ShouldPass()
        {
            var expectations = SpecExpectations.From(SpWithGroupBy());
            var fact = Assert.Single(expectations!.DmlScopeFacts);

            var crud = DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼 | 기준일 파라미터 적용 | 조인 키 | GROUP BY | ORDER BY |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Operation} 1 | {fact.Line} | {fact.Target} | (없음) | (기준일 파라미터 없음) | (없음) | "
                + $"{string.Join(", ", fact.GroupByColumns)} | (없음) |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing
                    && e.Message.Contains("GROUP BY"));
        }

        [Fact]
        public void Validate_DmlScopeFactWithoutGroupBy_ShouldNotRequireTheGroupByCell()
        {
            // 제약 2의 함정 재현 - GroupByColumns가 비어 있고(UPDATE는 항상 그렇다)
            // 표의 GROUP BY 칸도 조인 키 칸도 "(없음)"이면 두 칸이 같은 토큰이다.
            // 대조가 "값이 비어 있지 않을 때만 요구"를 지키지 않으면 이 우연한 일치가
            // 검사를 무력화한다 - 이 테스트는 GroupByColumns가 빈 사실에는 아무 것도
            // 요구하지 않는다는 것을 증명한다(라인 칸만 맞으면 통과).
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[]
                {
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" }, Array.Empty<string>())
                }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n### DML 범위 (기계 확정 — 수정 금지)\n"
                + "| 문장 | 라인 | 대상 | 술어 | 기준일 | 조인 키 | GROUP BY | ORDER BY |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 227 | A | UseState | **아니오** | PLTID | (없음) | — |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DmlScopeTableMissing);
        }

        [Fact]
        public void SuggestedPromptFix_ShouldCarryLockHintAndObjectDeclarationErrorsToTheModel()
        {
            // 위 SuggestedPromptFix_ShouldCarryMachineTableErrorsToTheModel과 같은 이유 -
            // BuildSuggestedPromptFix의 catch-all 버킷(2026-08-20)이 새 ErrorType도
            // 내용째로 실어야 검사를 세운 보람이 있다.
            var markdown = "## 개요\n내용\n\n## CRUD 분석\n표 없음\n";

            var lockResult = new MechanicalValidator().Validate(
                markdown, SpecExpectations.From(SpWithLockHints()));
            Assert.Contains(DmlScopeExtractor.LockHintTableHeading, lockResult.SuggestedPromptFix);

            var declResult = new MechanicalValidator().Validate(
                "## 개요\n내용\n", SpecExpectations.From(FunctionWithoutWithOptions()));
            Assert.Contains(
                ObjectDeclarationExtractor.ObjectDeclarationTableHeading, declResult.SuggestedPromptFix);
        }

        // ==========================================================================
        // 작업 5 수정 라운드 1 - 리뷰 실측(Important)
        //
        // SpWithTwoScansOnSameLine은 두 사실의 테이블이 다르다(dbo.TA/dbo.TB) - Table
        // 비교만으로 이미 두 행이 갈려 Alias 비교(MechanicalValidator.cs:3208)를
        // 지워도 MechanicalValidatorTests 210건이 그대로 통과했다(리뷰어 실측). Table이
        // 같고 Alias만 다른 가장 자연스러운 실제 패턴은 한 줄짜리 자기조인
        // (`FROM dbo.T A WITH(NOLOCK) JOIN dbo.T B WITH(NOLOCK) ON A.ID=B.ID`)이고,
        // 이 검사가 막으려는 부류가 정확히 그것이다 - Alias 비교가 없으면 A의 행이
        // B의 행으로도 오판정된다.
        // ==========================================================================

        /// <summary>
        /// 자기조인 - 같은 테이블, 두 별칭, 같은 물리 줄. Table·Line·Scope·Hints가
        /// 두 사실 사이에서 전부 같고 Alias만 다르다 - Alias 비교가 정확히 이 모양의
        /// 충돌을 막는다.
        /// </summary>
        private static SpDefinition SpWithSelfJoinOnSameLine() => new()
        {
            ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            ObjectType = CodeObjectType.Procedure,
            DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    UPDATE A SET A.C = B.D
    FROM dbo.T A WITH (NOLOCK) JOIN dbo.T B WITH (NOLOCK) ON A.ID = B.ID
END",
            Dependencies = new List<DependencyInfo>()
        };

        [Fact]
        public void Validate_SelfJoinSharesTableAndLine_MissingAliasRow_ShouldFlag()
        {
            var expectations = SpecExpectations.From(SpWithSelfJoinOnSameLine());
            Assert.Equal(2, expectations!.LockHints.Count);
            var first = expectations.LockHints[0];
            var second = expectations.LockHints[1];
            Assert.Equal(first.Table, second.Table);
            Assert.Equal(first.Line, second.Line);
            Assert.Equal(first.Scope, second.Scope);
            Assert.Equal(first.Hints, second.Hints);
            Assert.NotEqual(first.Alias, second.Alias);

            // 문서는 첫 번째 별칭(A)의 행만 옮기고 두 번째 별칭(B)은 빠뜨린다. Table·
            // Line·Scope·Hints가 전부 같으므로, Alias 비교가 없으면 A의 행이 B의 행도
            // 만족시켜 버린다.
            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {first.Operation} {first.StatementOrdinal} | {first.Line} | {first.Table} | "
                + $"{first.Alias} | {first.Scope} | {string.Join(", ", first.Hints)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            var expectedRawContext =
                $"{second.Operation} {second.StatementOrdinal} @ line {second.Line} {second.Table} {second.Alias}";
            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.LockHintTableMissing && e.RawContext == expectedRawContext);
        }

        [Fact]
        public void Validate_SelfJoinSharesTableAndLine_BothAliasRowsPresent_ShouldPass()
        {
            var expectations = SpecExpectations.From(SpWithSelfJoinOnSameLine());
            var first = expectations!.LockHints[0];
            var second = expectations.LockHints[1];

            var crud = DmlScopeExtractor.LockHintTableHeading + "\n"
                + "| 문장 | 라인 | 테이블 | 별칭 | 범위 | 힌트 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + $"| {first.Operation} {first.StatementOrdinal} | {first.Line} | {first.Table} | "
                + $"{first.Alias} | {first.Scope} | {string.Join(", ", first.Hints)} |\n"
                + $"| {second.Operation} {second.StatementOrdinal} | {second.Line} | {second.Table} | "
                + $"{second.Alias} | {second.Scope} | {string.Join(", ", second.Hints)} |\n";

            var result = new MechanicalValidator().Validate(WrapSpec(crud), expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.LockHintTableMissing);
        }

        /// <summary>
        /// 실행 의미 표에 종류가 둘 늘었을 때(2026-08-23 축 A ③(b) Task 4·5의
        /// `비집계 대입`·`루프 내 재설정`) L1이 그 행을 기존 종류와 똑같이 대조하는지
        /// 확인한다. CheckExecutionSemantics는 종류 칸을 <b>목록과 대조하지 않고</b>
        /// 네 칸(종류·라인·대상·확정 사실)의 문자열 일치만 보므로 손댈 것이 없다는 것이
        /// 주장인데, 확인 없이 넘기면 표만 넓어지고 검사는 침묵한다.
        /// </summary>
        [Fact]
        public void Validate_ExecutionSemanticsNewKinds_ShouldCompareLikeExistingKinds()
        {
            var expectations = EmptyExpectations() with
            {
                ExecutionSemantics = NewKindFacts()
            };

            var result = new MechanicalValidator().Validate(
                WrapSpec(ExecutionSemanticsSection(NewKindFacts())), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors, e => e.Type == ErrorType.ExecutionSemanticsTableMissing);
        }

        [Fact]
        public void Validate_ExecutionSemanticsNewKindRowDropped_ShouldBeAnError()
        {
            // 두 새 종류 중 `루프 내 재설정` 행만 뺀 명세서. 검사가 새 종류를 그냥
            // 흘려보낸다면 여기서도 조용히 통과할 것이다.
            var expectations = EmptyExpectations() with
            {
                ExecutionSemantics = NewKindFacts()
            };
            var kept = new[] { NewKindFacts()[0] };

            var result = new MechanicalValidator().Validate(
                WrapSpec(ExecutionSemanticsSection(kept)), expectations);

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.ExecutionSemanticsTableMissing
                    && e.RawContext == $"{ExecutionSemanticsFacts.LoopVariableResetKind} @ 69 @v_intID");
        }

        /// <summary>
        /// `AllKinds`에 없는 종류가 표에 와도 L1은 다르게 반응하지 않는다는 실측.
        /// 종류 칸을 목록과 대조하는 검사가 L1에 없기 때문이다 - `AllKinds`를 읽는
        /// 유일한 생산 코드는 `MachineConfirmedTables.CriticExemptionBlock`이고,
        /// 등재 누락을 잡는 트립와이어도 L1이 아니라
        /// `MachineConfirmedTablesTests.EveryExecutionSemanticKindConstant_IsListedInAllKinds`
        /// (`*Kind` 상수를 리플렉션으로 훑는다)와
        /// `CriticExemptionBlock_PutsExecutionSemanticsRowsOutOfReportingScope`다.
        /// 즉 새 종류가 목록에서 빠지면 잃는 것은 L1 대조가 아니라 <b>Critic 면제</b>다.
        /// </summary>
        [Fact]
        public void Validate_ExecutionSemanticsKindOutsideAllKinds_ShouldStillCompareRowWise()
        {
            const string unlistedKind = "이 목록에 없는 종류";
            Assert.DoesNotContain(unlistedKind, ExecutionSemanticsFacts.AllKinds);

            var facts = new[] { new ExecutionSemanticFact(unlistedKind, "7", "@v", "확정 사실.") };
            var expectations = EmptyExpectations() with { ExecutionSemantics = facts };

            var present = new MechanicalValidator().Validate(
                WrapSpec(ExecutionSemanticsSection(facts)), expectations);
            var dropped = new MechanicalValidator().Validate(
                WrapSpec(ExecutionSemanticsSection(Array.Empty<ExecutionSemanticFact>())),
                expectations);

            Assert.DoesNotContain(
                present.DetailedErrors, e => e.Type == ErrorType.ExecutionSemanticsTableMissing);
            Assert.Contains(
                dropped.DetailedErrors, e => e.Type == ErrorType.ExecutionSemanticsTableMissing);
        }

        /// <summary>
        /// Task 4·5가 더한 두 종류의 행. 문장은 각 추출기의 상수를 그대로 옮긴 것이
        /// 아니라 이 검사가 보는 것(네 칸의 문자열 일치)만 재현하는 표본이다.
        /// </summary>
        private static IReadOnlyList<ExecutionSemanticFact> NewKindFacts() => new[]
        {
            new ExecutionSemanticFact(
                ExecutionSemanticsFacts.NonAggregateAssignmentKind, "71", "@v_strYMD ← A.YMD",
                "비집계 SELECT는 결과가 없으면 대입 자체가 일어나지 않습니다. "
                + "무결과 시 변수에는 이 문장에 도달한 시점의 값이 그대로 남습니다."),
            new ExecutionSemanticFact(
                ExecutionSemanticsFacts.LoopVariableResetKind, "69", "@v_intID",
                "이 대입은 WHILE 본문의 최상위에 있고 앞에 루프를 벗어나는 문장이 없어, "
                + "반복마다 다시 실행됩니다.")
        };

        private static string ExecutionSemanticsSection(IReadOnlyList<ExecutionSemanticFact> facts)
        {
            var section = ExecutionSemanticsFacts.TableHeading + "\n"
                + "| 종류 | 라인 | 대상 | 확정 사실 |\n"
                + "| :--- | :--- | :--- | :--- |\n";
            foreach (var fact in facts)
            {
                section += $"| {fact.Kind} | {fact.Line} | {fact.Target} | {fact.Fact} |\n";
            }

            return section;
        }

        [Fact]
        public void Validate_ObjectDeclarationRowNamesADifferentObject_ShouldFlag()
        {
            // Minor - 리뷰 실측: sectionText.Contains(fact.QualifiedName, ...) 절을
            // 지워도 210건이 그대로 통과했다. WITH 옵션 값은 옳지만 객체 이름이 다른
            // 행(오귀속)을 쓰면 이 절이 유일하게 잡는 결함이 재현된다.
            var expectations = SpecExpectations.From(FunctionWithExecuteAsOption());
            var fact = expectations!.ObjectDeclaration!;

            var markdown = WrapSpec("표 없음") + "\n"
                + ObjectDeclarationExtractor.ObjectDeclarationTableHeading + "\n"
                + "| 객체 | WITH 옵션 |\n"
                + "| :--- | :--- |\n"
                + $"| dbo.UF_다른_함수 | {string.Join(", ", fact.WithOptions)} |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.ObjectDeclarationTableMissing);
        }

        private static SpDefinition ExecutionSemanticsSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 END",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure)
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public void From_WithOnlyExecutionSemantics_ShouldNotReturnNull()
        {
            // 이른 반환 AND-체인에 자기 항을 넣지 않으면 재료가 이것 하나뿐일 때
            // From이 null을 돌려주고 CheckExecutionSemantics가 한 번도 돌지 않는다.
            var expectations = SpecExpectations.From(ExecutionSemanticsSpDef());

            Assert.NotNull(expectations);
            Assert.NotEmpty(expectations!.ExecutionSemantics);
            // 격리 확인 - 다른 재료가 이 픽스처를 살려 주고 있지 않은지 못박는다.
            Assert.Empty(expectations.DmlScopeFacts);
        }

        [Fact]
        public void Validate_MissingExecutionSemanticsTable_ShouldReportAnError()
        {
            var expectations = SpecExpectations.From(ExecutionSemanticsSpDef());
            var validator = new MechanicalValidator();

            var result = validator.Validate("## 개요\n표가 없다.\n", expectations);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.ExecutionSemanticsTableMissing);
        }

        [Fact]
        public void Validate_WithExecutionSemanticsTableCopied_ShouldNotReportThatError()
        {
            var expectations = SpecExpectations.From(ExecutionSemanticsSpDef());
            var fact = Assert.Single(expectations!.ExecutionSemantics);
            var markdown =
                "## 개요\n\n"
                + ExecutionSemanticsFacts.TableHeading + "\n\n"
                + "| 종류 | 라인 | 대상 | 확정 사실 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Kind} | {fact.Line} | {fact.Target} | {fact.Fact} |\n";
            var validator = new MechanicalValidator();

            var result = validator.Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.ExecutionSemanticsTableMissing);
        }

        // [격리를 위해 CREATE PROCEDURE를 쓰고 StaticAnalysis를 세팅하지 않는다]
        // CaseBranchExtractor는 spDef.DdlText만 보고 StaticAnalysis를 전혀 쓰지
        // 않는다. 그런데 이 재료를 "하나만" 격리하려는 픽스처가 실측으로 두 함정에
        // 걸렸다:
        // (1) StaticAnalysis.IsParsedSuccessfully = true를 세팅하면
        //     DatabasePlacementExtractor.Extract가 (ThreePartObjectReferences·
        //     LinkedServerReferences가 비어도) "로컬입니다" 확정 문장을 하나 만들어
        //     executionSemantics가 저절로 비지 않는다 - StaticAnalysis를 기본값
        //     (new() - IsParsedSuccessfully = false)으로 남겨야 막힌다.
        // (2) DDL을 CREATE FUNCTION으로 쓰면 ObjectDeclarationExtractor.Extract가
        //     함수라는 사실만으로(WITH 옵션이 없어도) 항상 non-null 사실을 만들어
        //     objectDeclaration == null 항이 저절로 거짓이 된다 - CREATE PROCEDURE로
        //     바꿔야 이 추출기가 null을 돌려준다(프로시저에는 이 옵션 자체가 없다).
        // 실측: SpecExpectations.From의 AND-체인에서 caseBranches.Count == 0 항을
        // 통째로 지워도, 함정 (1)·(2) 각각이 별도로 AND-체인을 이미 거짓으로
        // 만들고 있어 From_WithOnlyCaseBranches_ShouldNotReturnNull이 계속
        // 통과했다 - roundingCalls/sourceComments 실측과 같은 모양이 두 번
        // 겹친 경우다. 둘 다 막아야 이 테스트가 실제로 caseBranches 항을 지킨다.
        private static SpDefinition CaseBranchSpDef()
        {
            return new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                DdlText = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN DATEPART(DW, GETDATE()) > 3 THEN 7 ELSE 0 END
    RETURN
END"
            };
        }

        [Fact]
        public void From_WithOnlyCaseBranches_ShouldNotReturnNull()
        {
            // 이른 반환 AND-체인에 자기 항을 넣지 않으면 재료가 이것 하나뿐일 때
            // From이 null을 돌려주고 CheckCaseBranches가 한 번도 돌지 않는다.
            var expectations = SpecExpectations.From(CaseBranchSpDef());

            Assert.NotNull(expectations);
            Assert.NotEmpty(expectations!.CaseBranches);
            // 격리 확인 - 다른 재료가 이 픽스처를 살려 주고 있지 않은지 못박는다.
            // ExecutionSemantics·ObjectDeclaration까지 비어(null) 있음을 함께
            // 단언해야 한다 - CaseBranchSpDef 위 주석의 두 함정(DB 배치 사실 ·
            // 함수 선언 사실이 이 항을 대신 가려 버리는 것)이 되풀이되지 않게 한다.
            Assert.Empty(expectations.DmlScopeFacts);
            Assert.Empty(expectations.ExecutionSemantics);
            Assert.Null(expectations.ObjectDeclaration);
        }

        [Fact]
        public void Validate_MissingCaseBranchTable_ShouldReportAnError()
        {
            var expectations = SpecExpectations.From(CaseBranchSpDef());
            var validator = new MechanicalValidator();

            var result = validator.Validate("## 개요\n표가 없다.\n", expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.CaseBranchTableMissing);
        }

        [Fact]
        public void Validate_MergedCaseBranches_ShouldStillReportTheMissingRow()
        {
            // UIF_SettleYMD 실측: 두 분기를 하나로 뭉갠 것이 🟠이었다. 헤딩만 있고
            // 행이 빠지면 통과해서는 안 된다.
            var expectations = SpecExpectations.From(CaseBranchSpDef());
            var markdown =
                "## 로직 흐름 요약\n\n"
                + CaseBranchExtractor.TableHeading + "\n\n"
                + "| 라인 | 순서 | 조건 원문 | 결과 원문 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + "| 5 | WHEN 1 | 요일을 비교해 | 7 |\n";
            var validator = new MechanicalValidator();

            var result = validator.Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.CaseBranchTableMissing);
        }

        [Fact]
        public void Validate_WithCaseBranchTableCopied_ShouldNotReportThatError()
        {
            var expectations = SpecExpectations.From(CaseBranchSpDef());
            var rows = string.Concat(expectations!.CaseBranches.Select(
                f => $"| {f.Line} | {f.Ordinal} | {f.Condition} | {f.Result} |\n"));
            var markdown =
                "## 로직 흐름 요약\n\n"
                + CaseBranchExtractor.TableHeading + "\n\n"
                + "| 라인 | 순서 | 조건 원문 | 결과 원문 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + rows;
            var validator = new MechanicalValidator();

            var result = validator.Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.CaseBranchTableMissing);
        }

        [Fact]
        public void Validate_CaseBranchTableWithoutElseRow_ShouldNotReportThatError()
        {
            // ELSE가 없는 CASE의 기대값에는 ELSE 행이 없다 - 명세서도 ELSE 행을
            // 싣지 않아야 통과한다. 거짓 ELSE 행을 요구하면 안 된다.
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "F",
                DdlText = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN CASE WHEN 1 = 1 THEN 10 END
END",
                StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true }
            };
            var expectations = SpecExpectations.From(spDef);
            var fact = Assert.Single(expectations!.CaseBranches);
            Assert.Equal("WHEN 1", fact.Ordinal);

            var markdown =
                "## 로직 흐름 요약\n\n"
                + CaseBranchExtractor.TableHeading + "\n\n"
                + "| 라인 | 순서 | 조건 원문 | 결과 원문 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Line} | {fact.Ordinal} | {fact.Condition} | {fact.Result} |\n";
            var validator = new MechanicalValidator();

            var result = validator.Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.CaseBranchTableMissing);
        }

        /// <summary>
        /// 2026-08-22 축 A 재감사 실측(UP_UTIL_STAT_PGCOLLECT_INS). 구분 행의 셀 수가
        /// 헤더와 다르면 GFM이 표로 인식하지 않아 "수정 금지" 표가 통째로 평문이 된다.
        /// 행 내용 대조 검사들은 값만 보므로 이 부류를 잡지 못한다.
        /// </summary>
        [Fact]
        public void Validate_MachineTableWithMismatchedSeparatorCells_IsReported()
        {
            var markdown = WrapSpec(
                DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 |\n"
                + "| :--- | :--- |\n"
                + "| INSERT 1 | 55 | dbo.T |\n");

            var result = new MechanicalValidator().Validate(markdown);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.MachineTableShapeBroken);
        }

        /// <summary>셀 수가 맞는 표는 통과한다 - 오탐 고정.</summary>
        [Fact]
        public void Validate_MachineTableWithMatchingSeparatorCells_IsNotReported()
        {
            var markdown = WrapSpec(
                DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| INSERT 1 | 55 | dbo.T |\n");

            var result = new MechanicalValidator().Validate(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.MachineTableShapeBroken);
        }

        /// <summary>
        /// 2026-08-22 최종 리뷰 Critical 실측(output/Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md:55-70).
        /// 기계 확정 표(4칸) 바로 뒤에 빈 줄로 구분된 정당한 별개 표(3칸)가 있다. 옛 구현은
        /// 빈 줄을 무시하고 두 표를 하나로 합쳐, 뒤 표의 행을 앞 표 헤더(4칸)와 비교해
        /// 거짓 형태 결함을 냈다. 빈 줄은 GFM의 표 종결자이므로 블록 경계로 삼아야 한다.
        /// </summary>
        [Fact]
        public void Validate_AdjacentWellFormedTableWithDifferentColumnCount_IsNotReported()
        {
            var markdown = WrapSpec(
                ExecutionSemanticsFacts.TableHeading + "\n\n"
                + "| 종류 | 라인 | 대상 | 확정 사실 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + "| DB 배치 | - | (객체 전체) | 참조 객체는 전부 SETTLE_POQ_DB 로컬입니다. |\n"
                + "\n"
                + "| 작업 | 대상 | 분석 결과 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 조회 | 물리 테이블 | 없음 |\n");

            var result = new MechanicalValidator().Validate(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.MachineTableShapeBroken);
        }

        /// <summary>
        /// 2026-08-22 최종 리뷰 Important I2 실측
        /// (output/Procedures/dbo.UP_UTIL_STAT_PGCOLLECT_INS/docs/Spec.md:71-72). INSERT
        /// 매핑 표는 MachineConfirmedTables.All의 여덟 헤딩에 없어 CheckMachineTableShape가
        /// 못 보고, CheckInsertMappingTableNames는 테이블명 칸만 본다. 4칸 헤더 위에 3칸
        /// 구분행이 그대로 있어도 아무 검사도 잡지 못했다 - 형태 검사를 INSERT 매핑
        /// 절까지 넓혀야 한다.
        /// </summary>
        [Fact]
        public void Validate_InsertMappingTableWithMismatchedSeparatorCells_IsReported()
        {
            var markdown = WrapSpec(
                "### INSERT 대상 테이블: SETTLE_POQ_DB.dbo.TStatPGCollect\n"
                + "| 테이블명 | 컬럼명 | 원천 데이터 (Mapping) | 설명 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| SETTLE_POQ_DB.dbo.TStatPGCollect | INYMD | 외부 집계 INYMD | 세 UNION ALL 원천 집합의 회수일자입니다. |\n");

            var result = new MechanicalValidator().Validate(markdown);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.MachineTableShapeBroken);
        }

        /// <summary>셀 수가 맞는 INSERT 매핑 표는 통과한다 - 오탐 고정.</summary>
        [Fact]
        public void Validate_InsertMappingTableWithMatchingSeparatorCells_IsNotReported()
        {
            var markdown = WrapSpec(
                "### INSERT 대상 테이블: SETTLE_POQ_DB.dbo.TStatPGCollect\n"
                + "| 테이블명 | 컬럼명 | 원천 데이터 (Mapping) | 설명 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + "| SETTLE_POQ_DB.dbo.TStatPGCollect | INYMD | 외부 집계 INYMD | 세 UNION ALL 원천 집합의 회수일자입니다. |\n");

            var result = new MechanicalValidator().Validate(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.MachineTableShapeBroken);
        }

        /// <summary>
        /// 2026-08-22 축 A 재감사 실측(UP_UTIL_SETTLE_SUMMARY_EXTRA). 매핑 표 한 행이
        /// TSetTleByOUT으로 적혔다 - 대소문자만 다르다. 실행은 무해하지만 매핑 표를
        /// 식별자 원천으로 삼는 이행·grep·자동 대조가 그 행에서 어긋난다.
        /// 대소문자를 무시하면 이 검사가 잡아야 할 것을 못 잡으므로 Ordinal로 본다.
        /// </summary>
        [Fact]
        public void Validate_InsertMappingTableNameDiffersOnlyByCase_IsReported()
        {
            var expectations = BuildExpectationsWithInsertTargets("SETTLE_POQ_DB.dbo.TSettleByOUT");
            var markdown = WrapSpec(
                "### INSERT 대상 테이블: SETTLE_POQ_DB.dbo.TSettleByOUT\n"
                + "| 테이블명 | 컬럼명 | 원천 데이터 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| SETTLE_POQ_DB.dbo.TSetTleByOUT | OUTCNT | COUNT(*) |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.InsertMappingTableNameMismatch);
        }

        /// <summary>표기가 정확히 같으면 통과한다 - 오탐 고정.</summary>
        [Fact]
        public void Validate_InsertMappingTableNameExact_IsNotReported()
        {
            var expectations = BuildExpectationsWithInsertTargets("SETTLE_POQ_DB.dbo.TSettleByOUT");
            var markdown = WrapSpec(
                "### INSERT 대상 테이블: SETTLE_POQ_DB.dbo.TSettleByOUT\n"
                + "| 테이블명 | 컬럼명 | 원천 데이터 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| SETTLE_POQ_DB.dbo.TSettleByOUT | OUTCNT | COUNT(*) |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.InsertMappingTableNameMismatch);
        }

        /// <summary>
        /// Fix Round 1 - 리뷰 Critical 실측. 옛 구현은 문서 전체의 `|`로 시작하는 모든 줄을
        /// 훑었다. UPDATE 매핑 표(`### UPDATE 대상 테이블: ...`)도 같은
        /// `| 테이블명 | 컬럼명 | ... |` 모양을 쓰므로(AiService.BuildUpdateMappingTemplateLines),
        /// 그 표의 테이블명 칸이 InsertTargetTables와 대소문자만 다르면 이 검사가 UPDATE
        /// 행을 INSERT 매핑 오류로 잘못 지목했다 - "원문 표기 그대로 옮기십시오"라는 안내가
        /// UPDATE 문장에는 맞지 않을 수 있다. 이 문서에는 `### INSERT 대상 테이블:` 절이
        /// 아예 없으므로, 검사가 제 절로 스코프를 좁혔다면 아무것도 보고하지 않아야 한다.
        /// </summary>
        [Fact]
        public void Validate_UpdateMappingTableNameDiffersOnlyByCase_IsNotAttributedToInsertCheck()
        {
            var expectations = BuildExpectationsWithInsertTargets("SETTLE_POQ_DB.dbo.TSettleByOUT");
            var markdown = WrapSpec(
                "### UPDATE 대상 테이블: SETTLE_POQ_DB.dbo.TSetTleByOUT (갱신 1)\n"
                + "| 테이블명 | 컬럼명 | 원천 표현식 (SET) | 설명 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + "| SETTLE_POQ_DB.dbo.TSetTleByOUT | OUTCNT | OUTCNT + 1 | 누적 갱신 |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.InsertMappingTableNameMismatch);
        }

        /// <summary>
        /// Fix Round 1 - 리뷰 Minor. InsertTargetTables 하나만 채운 픽스처는
        /// SpecExpectations.From이 (이 항이 AND-체인에 이어져 있어서) null이 아닌 값을
        /// 돌려주고, 그러면 Validate가 나머지 17개 CheckXxx를 전부 돈다. 이 파급이
        /// 다른 재료(UpdateColumns·PromptSchemaColumns 등)를 전혀 채우지 않았는데도
        /// 그 재료를 대조하는 검사들을 잘못 발동시키지 않는지를 잠근다 - 검사마다
        /// "자기 재료가 비었으면 조기 반환한다"는 관례를 읽어 확인한 것을, 읽기가 아니라
        /// 실행으로 못박는다.
        /// </summary>
        [Fact]
        public void Validate_WithOnlyInsertTargetTablesPopulated_DoesNotRippleIntoUnrelatedChecks()
        {
            var expectations = BuildExpectationsWithInsertTargets("SETTLE_POQ_DB.dbo.TSettleByOUT");
            var markdown = WrapSpec(
                "### INSERT 대상 테이블: SETTLE_POQ_DB.dbo.TSettleByOUT\n"
                + "| 테이블명 | 컬럼명 | 원천 데이터 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| SETTLE_POQ_DB.dbo.TSettleByOUT | OUTCNT | COUNT(*) |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            var unrelatedTypesThatMustNotFire = new[]
            {
                ErrorType.UpdateMappingMissing,
                ErrorType.SchemaClaimFalse,
                ErrorType.TableIdentitySplit,
                ErrorType.IdentifierNotationClaim,
                ErrorType.SourceCommentMissing,
                ErrorType.RoundingSemanticsMissing,
                ErrorType.SessionOptionMissing,
                ErrorType.HeaderContractContradiction,
                ErrorType.DmlScopeTableMissing,
                ErrorType.DerivedTableDefinitionMissing,
                ErrorType.SetPredicateMismatch,
                ErrorType.LockHintTableMissing,
                ErrorType.ObjectDeclarationTableMissing,
                ErrorType.CaseBranchTableMissing
            };

            foreach (var unrelatedType in unrelatedTypesThatMustNotFire)
            {
                Assert.DoesNotContain(result.DetailedErrors, e => e.Type == unrelatedType);
            }
        }

        private static SpecExpectations BuildExpectationsWithInsertTargets(params string[] tables)
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    InsertTables = new List<string>(tables)
                }
            };
            return SpecExpectations.From(spDef)!;
        }

        /// <summary>
        /// 2026-08-22 축 A 재감사 실측(UF_GET_COMM4PG4INTEREST). 필터 컬럼 UseState는
        /// IsNullable이 true인데 명세서가 "널을 허용하지 않습니다"로 단정했다. 이
        /// 단정을 근거로 이행 스키마에 NOT NULL을 세우면 원본이 3값 논리로 배제하던
        /// 행이 대상에 들어와 금액이 바뀐다.
        ///
        /// Fix Round 1 - 테이블 앵커가 필요해진 뒤로는 실측 문장 그대로
        /// `TTest.UseState`처럼 테이블.컬럼을 한 식별자에 같이 쓴다 - 실제 결함
        /// 문장(`TFreeInterestInstCommission.UseState`)과 같은 모양이다.
        /// </summary>
        [Fact]
        public void Validate_NotNullClaimOnNullableColumn_IsReported()
        {
            var expectations = BuildExpectationsWithNullableColumn("UseState", isNullable: true);
            var markdown = WrapSpec("표 없음")
                + "\n`TTest.UseState`는 `tinyint`이며 널을 허용하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.NullabilityClaimMismatch);
        }

        /// <summary>
        /// 실제로 NOT NULL인 컬럼에 대한 같은 문장은 통과한다 - 오탐 고정. 테이블
        /// 앵커를 붙여도(귀속이 성공해도) 참인 서술이므로 여전히 통과해야 한다는
        /// 것까지 검증한다.
        /// </summary>
        [Fact]
        public void Validate_NotNullClaimOnNotNullColumn_IsNotReported()
        {
            var expectations = BuildExpectationsWithNullableColumn("IsPGFlag", isNullable: false);
            var markdown = WrapSpec("표 없음")
                + "\n`TTest.IsPGFlag`는 `tinyint`이며 널을 허용하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.NullabilityClaimMismatch);
        }

        /// <summary>
        /// 테이블은 귀속되지만 그 테이블의 어느 컬럼에도 귀속되지 않는 이름은
        /// 침묵한다. 잘못 지목한 오류는 재생성으로 고칠 수 없다는 CheckSchemaClaims의
        /// 정책을 그대로 따른다.
        /// </summary>
        [Fact]
        public void Validate_NotNullClaimOnUnknownIdentifier_IsSilent()
        {
            var expectations = BuildExpectationsWithNullableColumn("UseState", isNullable: true);
            var markdown = WrapSpec("표 없음")
                + "\n`TTest.배치작업ID`는 널을 허용하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.NullabilityClaimMismatch);
        }

        /// <summary>
        /// 테이블 앵커 자체가 없는(같은 줄에 어느 식별자도 테이블로 풀리지 않는)
        /// 경우도 침묵한다 - Fix Round 1 이전 구현이 검증하지 못했던 경로다.
        /// </summary>
        [Fact]
        public void Validate_NotNullClaimWithoutAnyTableAnchor_IsSilent()
        {
            var expectations = BuildExpectationsWithNullableColumn("UseState", isNullable: true);
            var markdown = WrapSpec("표 없음")
                + "\n`UseState`는 `tinyint`이며 널을 허용하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.NullabilityClaimMismatch);
        }

        private static SpecExpectations BuildExpectationsWithNullableColumn(string column, bool isNullable)
        {
            var dep = new DependencyInfo
            {
                Database = "SETTLE_POQ_DB",
                Schema = "dbo",
                Name = "TTest",
                Columns = { new ColumnInfo { ColumnName = column, IsNullable = isNullable } }
            };
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Dependencies = { dep },
                StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true }
            };
            return SpecExpectations.From(spDef)!;
        }

        /// <summary>
        /// Fix Round 1 - 리뷰 Critical 실측(UF_GET_COMM4PG4INTEREST). `UseState`는
        /// `TCardContractMgmt`에서 NOT NULL, `TFreeInterestInstCommission`에서 널 허용으로
        /// 갈린다. 이름만 보는 대조는 이 갈림을 만나면 이름 자체를 버려, 이 감사가
        /// 잡아야 할 결함(후자를 NOT NULL로 단정한 문장)이 조용히 통과했다. 테이블별로
        /// 나누면 두 UseState가 서로를 가리지 않고, 정답 테이블만 보고돼야 한다.
        /// </summary>
        [Fact]
        public void Validate_NotNullClaimOnColumnAmbiguousAcrossTables_ReportsTheNullableTable()
        {
            var expectations = BuildExpectationsWithTwoDependencyTables(
                ("TCardContractMgmt", "UseState", false),
                ("TFreeInterestInstCommission", "UseState", true));
            var markdown = WrapSpec("표 없음")
                + "\n`TFreeInterestInstCommission.UseState`는 `tinyint`이며 널을 허용하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.NullabilityClaimMismatch);
        }

        /// <summary>
        /// Fix Round 1 - 리뷰 Critical 실측(UP_UTIL_SETTLE_SUMMARY_ETC:86 등 3곳). 표 행의
        /// 마지막 셀이 SQL 술어 `A.OutYMD IS NOT NULL`을 서술할 뿐인데, 같은 행의
        /// "참조 컬럼" 셀에 나열된 무관한 컬럼(`AYMD` 등)까지 널 불허 단정으로 잘못
        /// 지목했다. `AYMD`는 이 표에서 실제로 널 허용이지만, 이 줄은 `AYMD`에 대해
        /// 아무 단정도 하지 않았으므로 침묵해야 한다.
        /// </summary>
        [Fact]
        public void Validate_SqlPredicateIsNotNull_DoesNotFlagUnrelatedColumnsInSameRow()
        {
            var expectations = BuildExpectationsWithNullableColumn("AYMD", isNullable: true);
            var markdown = WrapSpec("표 없음")
                + "\n| `SETTLE_POQ_DB.dbo.TTest` | 커서 조회 별칭 `A` | `AYMD`, `OutState` | "
                + "`A.OutYMD IS NOT NULL` 조건을 적용합니다. |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.NullabilityClaimMismatch);
        }

        private static SpecExpectations BuildExpectationsWithTwoDependencyTables(
            (string TableName, string ColumnName, bool IsNullable) tableA,
            (string TableName, string ColumnName, bool IsNullable) tableB)
        {
            var depA = new DependencyInfo
            {
                Database = "SETTLE_CARD_DB",
                Schema = "dbo",
                Name = tableA.TableName,
                Columns = { new ColumnInfo { ColumnName = tableA.ColumnName, IsNullable = tableA.IsNullable } }
            };
            var depB = new DependencyInfo
            {
                Database = "SETTLE_CARD_DB",
                Schema = "dbo",
                Name = tableB.TableName,
                Columns = { new ColumnInfo { ColumnName = tableB.ColumnName, IsNullable = tableB.IsNullable } }
            };
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;",
                ObjectKey = CodeObjectKey.Create("SETTLE_CARD_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Dependencies = { depA, depB },
                StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true }
            };
            return SpecExpectations.From(spDef)!;
        }

        // ------------------------------------------------------------------
        // [문장 칸 대조 - 2026-08-23 ③(b) 최종 리뷰 에스컬레이션 1]
        // 네 표는 같은 문장 번호 체계를 공유한다고 문서가 약속하는데, L1은 잠금 힌트에서만
        // 문장 칸을 행 매칭 키에 넣고 있었다. 집합 술어·DML 범위는 라인 등으로만 행을
        // 찾아 `SELECT 1` 행을 `UPDATE 1`로 옮겨 적어도 침묵했고, 참조 함수는 헤딩
        // 존재만 봤다. 아래 테스트들이 그 세 구멍을 각각 고정한다.
        // ------------------------------------------------------------------

        [Fact]
        public void Validate_SetPredicateRowWithWrongStatementCell_ShouldReport()
        {
            var expectations = EmptyExpectations() with
            {
                SetPredicates = new[] { CollectFlagSelectFact() }
            };
            // 라인·컬럼·범위·원문은 전부 맞고 문장 칸만 UPDATE로 틀린 행.
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 범위 | 원소 수 | 리터럴 목록 | 술어 원문 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 100 | CollectFlag | = | 최상위 | 1 | 1 | CollectFlag = 1 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.SetPredicateMismatch && e.Message.Contains("SELECT 1"));
        }

        private static DmlScopeFact UpdateScopeFact(int line) => new(
            "UPDATE", line, "dbo.TSettleMst", new[] { "YMD" }, true,
            Array.Empty<string>(), Array.Empty<string>());

        private const string DmlScopeHeaderRows =
            "| 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼(조인 결합 포함 · 대상 한정 아님) | 기준일 파라미터 적용(최상위 WHERE 기준) | 조인 키 | GROUP BY | ORDER BY |\n"
            + "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |\n";

        [Fact]
        public void Validate_DmlScopeRowVerbatim_ShouldPass()
        {
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[] { UpdateScopeFact(50) }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.DmlScopeTableHeading + "\n" + DmlScopeHeaderRows
                + "| UPDATE 1 | 50 | dbo.TSettleMst | YMD | 예 | (없음) | — | — |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.DmlScopeTableMissing);
        }

        [Fact]
        public void Validate_DmlScopeRowWithWrongStatementCell_ShouldReport()
        {
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[] { UpdateScopeFact(50) }
            };
            // 라인은 맞고 문장 칸만 SELECT로 틀린 행.
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.DmlScopeTableHeading + "\n" + DmlScopeHeaderRows
                + "| SELECT 1 | 50 | dbo.TSettleMst | YMD | 예 | (없음) | — | — |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing && e.Message.Contains("UPDATE 1"));
        }

        [Fact]
        public void Validate_DmlScopeSecondUpdateRow_ShouldRequireItsOwnOrdinal()
        {
            // UPDATE 둘 중 둘째 행의 문장 칸이 첫째와 같은 "UPDATE 1"로 적힌 표.
            // 라인만 보면 두 행 다 있으므로 통과하던 모양이다.
            var expectations = EmptyExpectations() with
            {
                DmlScopeFacts = new[] { UpdateScopeFact(50), UpdateScopeFact(80) }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.DmlScopeTableHeading + "\n" + DmlScopeHeaderRows
                + "| UPDATE 1 | 50 | dbo.TSettleMst | YMD | 예 | (없음) | — | — |\n"
                + "| UPDATE 1 | 80 | dbo.TSettleMst | YMD | 예 | (없음) | — | — |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.DmlScopeTableMissing && e.Message.Contains("UPDATE 2"));
        }

        private static ReferencedFunctionCallFact Workday2CallFact() => new(
            "dbo.UF_GET_WORKDAY2", "SELECT", 1, 53, "dbo.UF_GET_WORKDAY2(@pi_strYMD, CollectDay)");

        private const string ReferencedFunctionHeaderRows =
            "| 함수 | 호출 위치 | 인자 | 명세서 |\n"
            + "| :--- | :--- | :--- | :--- |\n";

        [Fact]
        public void Validate_ReferencedFunctionRowVerbatim_ShouldPass()
        {
            // 렌더러는 의존성이 풀리면 함수 칸을 DB.스키마.이름으로 적는다
            // (BuildReferencedFunctionTableLines) - 사실의 QualifiedName(dbo.UF_…)과 다르다.
            // 링크 칸은 경로 의존이라 대조 대상이 아니다.
            var expectations = EmptyExpectations() with
            {
                ReferencedFunctionCalls = new[] { Workday2CallFact() }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.ReferencedFunctionTableHeading + "\n" + ReferencedFunctionHeaderRows
                + "| SETTLE_POQ_DB.dbo.UF_GET_WORKDAY2 | SELECT 1 (라인 53) | dbo.UF_GET_WORKDAY2(@pi_strYMD, CollectDay) | [Spec](../../../Functions/dbo.UF_GET_WORKDAY2/docs/Spec.md) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ReferencedFunctionMismatch);
        }

        [Fact]
        public void Validate_ReferencedFunctionRowDropped_ShouldReport()
        {
            var expectations = EmptyExpectations() with
            {
                ReferencedFunctionCalls = new[] { Workday2CallFact() }
            };
            // 헤딩도 헤더도 구분줄도 있는데 행만 없는 표 - 헤딩 존재만 보면 통과한다.
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.ReferencedFunctionTableHeading + "\n" + ReferencedFunctionHeaderRows;

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.ReferencedFunctionMismatch);
        }

        [Fact]
        public void Validate_ReferencedFunctionCallExpressionSpansLines_RenderedFolded_ShouldPass()
        {
            // CallExpression은 TextOf(node) 그대로라 원문의 개행이 남는다. 렌더러는
            // MarkdownTableCellCodec.Escape가 개행을 공백으로 접어 한 줄 칸으로 싣는다 -
            // 마크다운 표 행은 한 줄이므로 개행이 든 값을 그대로 요구하면 어떤 산출물도
            // 만족시킬 수 없다(CollapseWhitespace 문서). 집합 술어의
            // FoldNewlinesLikeRenderedCell과 같은 접기를 여기도 적용해야 한다.
            var call = new ReferencedFunctionCallFact(
                "dbo.UF_GET_WORKDAY2", "SELECT", 1, 78,
                "dbo.UF_GET_WORKDAY2( CONVERT(VARCHAR(6), DATEADD(M, CollectMonth, @pi_strYMD), 112) + '01',\n                                     CollectDay-1)");
            var expectations = EmptyExpectations() with { ReferencedFunctionCalls = new[] { call } };
            var renderedCell = MarkdownTableCellCodec.Escape(call.CallExpression);
            Assert.DoesNotContain("\n", renderedCell);
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.ReferencedFunctionTableHeading + "\n" + ReferencedFunctionHeaderRows
                + $"| dbo.UF_GET_WORKDAY2 | SELECT 1 (라인 78) | {renderedCell} | (명세서 없음) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ReferencedFunctionMismatch);
        }

        [Fact]
        public void Validate_ReferencedFunctionCellIsADifferentNameWithSameSuffix_ShouldReport()
        {
            // 함수 칸 대조는 `이름`·`스키마.이름`·`DB.스키마.이름` 세 표기를 받으려고 접미
            // 일치를 쓴다. 경계 없는 EndsWith면 `X_UF_GET_WORKDAY2` 같은 다른 이름도
            // 통과하므로, 점 경계 또는 전체 일치만 받아야 한다.
            var expectations = EmptyExpectations() with { ReferencedFunctionCalls = new[] { Workday2CallFact() } };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.ReferencedFunctionTableHeading + "\n" + ReferencedFunctionHeaderRows
                + "| X_UF_GET_WORKDAY2 | SELECT 1 (라인 53) | dbo.UF_GET_WORKDAY2(@pi_strYMD, CollectDay) | (명세서 없음) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.ReferencedFunctionMismatch);
        }

        [Fact]
        public void Validate_ReferencedFunctionRowWithWrongStatementCell_ShouldReport()
        {
            var expectations = EmptyExpectations() with
            {
                ReferencedFunctionCalls = new[] { Workday2CallFact() }
            };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.ReferencedFunctionTableHeading + "\n" + ReferencedFunctionHeaderRows
                + "| dbo.UF_GET_WORKDAY2 | UPDATE 1 (라인 53) | dbo.UF_GET_WORKDAY2(@pi_strYMD, CollectDay) | (명세서 없음) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.ReferencedFunctionMismatch && e.Message.Contains("SELECT 1 (라인 53)"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 파라미터 목록 표의 행 ↔ StaticAnalysis.ProcedureParameters — 2026-08-23 9회차 ⚪ (D)
        //
        // 실측: COMM_UPD(`@v_valIncVat`)·INS_EXTRA(`@v_strReqYMD`·`@v_strCurrYMD`·`@v_valIncVat`)가
        // 지역 변수를, AcqManual이 `구분` 칸으로 내부 변수·시스템 상태값(`@@ERROR`)·시스템
        // 함수까지 파라미터 목록 표에 실었다. 시그니처는 파서가 확정한 사실이므로 표의
        // `@이름` 행은 ProcedureParameters와 정확히 같아야 한다. 이름 열은 헤더로 찾고
        // (`매개변수 명칭`·`매개변수`·`파라미터`·`이름`), 찾지 못하면 침묵한다(귀속 불가).
        // 첫 표만 파라미터 표로 본다 - v14 EXPECT_PROC처럼 두 번째 표(내부 변수)는 대상이 아니다.
        // ─────────────────────────────────────────────────────────────────────

        private static SpecExpectations BuildExpectationsWithParameters(params string[] declarations)
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ProcedureParameters = new List<string>(declarations)
                }
            };
            return SpecExpectations.From(spDef)!;
        }

        private static string SpecWithParameterSection(string parameterSectionBody)
            => WrapSpec("표 없음").Replace("## 파라미터 목록\n내용", "## 파라미터 목록\n" + parameterSectionBody);

        private const string CommUpdShapedParameterTable =
            "| 매개변수 명칭 | 데이터 타입 | 입출력 구분 | Null 여부 | 연관 컬럼 및 사용 관계 |\n" +
            "| :--- | :--- | :--- | :--- | :--- |\n" +
            "| `@pi_strYMD` | `char(8)` | 입력 | 원본 DDL에 명시되지 않음 | 기준일 |\n" +
            "| `@po_intRetVal` | `int` | 출력 | 원본 DDL에 명시되지 않음 | 반환 코드 |\n";

        [Fact]
        public void Validate_ParameterTableWithLocalVariableRow_IsReported()
        {
            // COMM_UPD Spec.md:83 실물 - 세 번째 행이 DECLARE된 지역 변수다.
            var expectations = BuildExpectationsWithParameters("@pi_strYMD char(8)", "@po_intRetVal int OUTPUT");
            var markdown = SpecWithParameterSection(
                CommUpdShapedParameterTable +
                "| `@v_valIncVat` | `decimal(2,1)` | 지역 변수 | 해당 없음 | 부가세 배수 |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            var error = Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.ParameterTableRowMismatch);
            Assert.Contains("@v_valIncVat", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_ParameterTableMatchingProcedureParameters_Passes()
        {
            var expectations = BuildExpectationsWithParameters("@pi_strYMD char(8)", "@po_intRetVal int OUTPUT");
            var markdown = SpecWithParameterSection(CommUpdShapedParameterTable);

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ParameterTableRowMismatch);
        }

        [Fact]
        public void Validate_ParameterTableWithNameInSecondColumn_IsStillChecked()
        {
            // AcqManual 실물 - 첫 열이 `구분`이고 이름 열은 둘째. 내부 변수·시스템 상태값 행이 섞여 있다.
            var expectations = BuildExpectationsWithParameters("@pi_strYMD char(8)", "@po_intRetVal int OUTPUT");
            var markdown = SpecWithParameterSection(
                "| 구분 | 매개변수 명칭 | 데이터 타입 | Null 여부 | 기본값 | 데이터베이스 컬럼과의 관계 |\n" +
                "| :--- | :--- | :--- | :--- | :--- | :--- |\n" +
                "| 입력 | `@pi_strYMD` | `char(8)` | 명시 없음 | 없음 | 기준일 |\n" +
                "| 출력 | `@po_intRetVal` | `int` | 명시 없음 | 없음 | 반환 코드 |\n" +
                "| 내부 변수 | `@v_strYMD` | `char(8)` | 해당 없음 | 없음 | 커서 변수 |\n" +
                "| 시스템 상태값 | `@@ERROR` | `int` | 해당 없음 | 없음 | 오류 검사 |\n" +
                "| 시스템 함수 | `GETDATE()` | `datetime` | 해당 없음 | 없음 | 등록일 |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            var error = Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.ParameterTableRowMismatch);
            Assert.Contains("@v_strYMD", error.Message, StringComparison.Ordinal);
            Assert.Contains("@@ERROR", error.Message, StringComparison.Ordinal);
            // `GETDATE()`는 `@`로 시작하지 않아 이 검사의 대상이 아니다 - 지목하지 않는다.
            Assert.DoesNotContain("GETDATE", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_ParameterTableMissingAParameter_IsReported()
        {
            var expectations = BuildExpectationsWithParameters("@pi_strYMD char(8)", "@po_intRetVal int OUTPUT");
            var markdown = SpecWithParameterSection(
                "| 매개변수 명칭 | 데이터 타입 | 입출력 구분 |\n" +
                "| :--- | :--- | :--- |\n" +
                "| `@pi_strYMD` | `char(8)` | 입력 |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            var error = Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.ParameterTableRowMismatch);
            Assert.Contains("@po_intRetVal", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_LocalVariablesInASecondTableUnderParameterSection_Pass()
        {
            // v14 EXPECT_PROC 실물 - 첫 표는 파라미터만, 둘째 표가 내부 변수.
            var expectations = BuildExpectationsWithParameters("@pi_strYMD char(8)", "@po_intRetVal int OUTPUT");
            var markdown = SpecWithParameterSection(
                CommUpdShapedParameterTable +
                "\n| 내부 변수 명칭 | 데이터 타입 | 초기값 | 연결 컬럼 및 사용 관계 |\n" +
                "| :--- | :--- | :--- | :--- |\n" +
                "| `@v_PLCardSettlePeriodPG` | `varchar(200)` | `'PLCard'` | UPDATE 7의 NOT IN |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ParameterTableRowMismatch);
        }

        [Fact]
        public void Validate_ParameterTableWithoutRecognizableNameColumn_IsSilent()
        {
            // 이름 열을 헤더로 찾지 못하면 귀속할 수 없으므로 침묵한다.
            var expectations = BuildExpectationsWithParameters("@pi_strYMD char(8)");
            var markdown = SpecWithParameterSection(
                "| 번호 | 항목 | 설명 |\n" +
                "| :--- | :--- | :--- |\n" +
                "| 1 | `@v_x` | 임의 |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ParameterTableRowMismatch);
        }

        [Fact]
        public void Validate_ParameterNameComparison_IgnoresCase()
        {
            var expectations = BuildExpectationsWithParameters("@pi_strYMD char(8)");
            var markdown = SpecWithParameterSection(
                "| 파라미터 | 데이터 타입 |\n" +
                "| :--- | :--- |\n" +
                "| `@PI_STRYMD` | `char(8)` |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ParameterTableRowMismatch);
        }

        [Fact]
        public void From_WithOnlyProcedureParameters_ShouldNotReturnNull()
        {
            // authoring-contract §1 - 재료가 이것 하나뿐이어도 검사가 돌아야 한다.
            Assert.NotNull(BuildExpectationsWithParameters("@pi_strYMD char(8)"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 「파라미터 목록」 표의 연결 컬럼 주장 ↔ 변수-컬럼 결합 — 2026-08-23 9회차 🟡
        // (UP_UTIL_SETTLE_EXCEPTION_PROC Spec.md:34)
        //
        // 표가 `@pi_strYMD`의 연결 컬럼으로 `TPLCardTxMst.YMD`·`TClientSettleRate4MobileCo.YMD`를
        // 적었는데 DDL에서 그 둘은 @pi_strYMD와 결합되지 않는다. 주장은 행의 어느 칸이든
        // 백틱 `테이블.컬럼` 토큰이고, 테이블이 StaticAnalysis.ReferencedTables에 있는 것만
        // 대조한다(함수 `dbo.UF_X(`·별칭 `A.YMD`는 귀속 불가 - 침묵). 같은 H2 아래 모든 표를 본다.
        // ─────────────────────────────────────────────────────────────────────

        private static SpecExpectations BuildExpectationsWithDdl(string ddl, params string[] referencedTables)
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = ddl,
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ProcedureParameters = new List<string> { "@pi_strYMD char(8)", "@po_intRetVal int OUTPUT" },
                    ReferencedTables = new List<string>(referencedTables)
                }
            };
            return SpecExpectations.From(spDef)!;
        }

        private const string ExceptionProcShapedDdl = @"
CREATE PROCEDURE dbo.UP_TEST @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.CLCOMM = dbo.UF_X(B.ClientID, B.YMD, @pi_strYMD)
    FROM   dbo.TSettleMst A JOIN dbo.TPLCardTxMst B ON A.PLTID = B.PLTID
    WHERE  A.YMD = @pi_strYMD
    UPDATE A SET A.Flag = 1
    FROM   dbo.TSettleMst A JOIN dbo.TClientSettleRate4MobileCo B ON A.ClientID = B.ClientID
    WHERE  A.AYMD = B.YMD AND A.YMD = @pi_strYMD
END";

        [Fact]
        public void Validate_ParameterColumnClaimWithoutBinding_IsReported()
        {
            // EXCEPTION_PROC:34 실물 모양(헤더 `구분 | 명칭 | …`, 주장 칸은 넷째).
            var expectations = BuildExpectationsWithDdl(ExceptionProcShapedDdl,
                "dbo.TSettleMst", "dbo.TPLCardTxMst", "dbo.TClientSettleRate4MobileCo");
            var markdown = SpecWithParameterSection(
                "### 파라미터와 변수의 컬럼 관계\n\n" +
                "| 구분 | 명칭 | 데이터 타입 | 연결되는 컬럼 또는 표현식 | 설명 |\n" +
                "| :--- | :--- | :--- | :--- | :--- |\n" +
                "| 입력 매개변수 | `@pi_strYMD` | `char(8)` | `TSettleMst.YMD`, `TPLCardTxMst.YMD`, `TClientSettleRate4MobileCo.YMD` | 정산 기준일 필터 |\n" +
                "| 출력 매개변수 | `@po_intRetVal` | `int` | 대상 컬럼 없음 | 오류 코드 |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            var error = Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.ParameterColumnClaimMismatch);
            Assert.Contains("TPLCardTxMst.YMD", error.Message, StringComparison.Ordinal);
            Assert.Contains("TClientSettleRate4MobileCo.YMD", error.Message, StringComparison.Ordinal);
            // 지목 목록(안내문의 "DDL이 … 결합하는 컬럼" 앞부분)에는 결합된 `TSettleMst.YMD`가 없어야 한다.
            var flagged = error.Message.Substring(0, error.Message.IndexOf("DDL이", StringComparison.Ordinal));
            Assert.DoesNotContain("TSettleMst.YMD", flagged, StringComparison.Ordinal);
            // 별칭으로 쓰인 대상 이름(`A`)이 테이블로 새지 않는다.
            Assert.DoesNotContain("`A.", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_ParameterColumnClaimsAllBound_Pass()
        {
            var expectations = BuildExpectationsWithDdl(ExceptionProcShapedDdl,
                "dbo.TSettleMst", "dbo.TPLCardTxMst", "dbo.TClientSettleRate4MobileCo");
            var markdown = SpecWithParameterSection(
                "| 매개변수 명칭 | 데이터 타입 | 연결 컬럼 |\n" +
                "| :--- | :--- | :--- |\n" +
                "| `@pi_strYMD` | `char(8)` | `TSettleMst.YMD`와 비교, `TSettleMst.CLCOMM` 산출에 사용 |\n" +
                "| `@po_intRetVal` | `int` | 대상 컬럼 없음 |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ParameterColumnClaimMismatch);
        }

        [Fact]
        public void Validate_ParameterColumnClaim_IgnoresFunctionsAliasesAndUnknownTables()
        {
            // `dbo.UF_X(`는 함수, `A.YMD`는 별칭, `TUnknown.YMD`는 ReferencedTables 밖 - 전부 침묵.
            var expectations = BuildExpectationsWithDdl(ExceptionProcShapedDdl, "dbo.TSettleMst");
            var markdown = SpecWithParameterSection(
                "| 파라미터 | 데이터 타입 | 사용처 |\n" +
                "| :--- | :--- | :--- |\n" +
                "| `@pi_strYMD` | `char(8)` | `dbo.UF_X(B.ClientID, B.YMD, @pi_strYMD)`의 인자, `A.YMD` 비교, `TUnknown.YMD` |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ParameterColumnClaimMismatch);
        }

        [Fact]
        public void Validate_ParameterColumnClaim_ChecksEveryTableUnderTheSection()
        {
            // 관계 표가 둘째 표여도 본다 - 주장은 어느 표에 있든 주장이다.
            var expectations = BuildExpectationsWithDdl(ExceptionProcShapedDdl,
                "dbo.TSettleMst", "dbo.TClientSettleRate4MobileCo");
            var markdown = SpecWithParameterSection(
                CommUpdShapedParameterTable +
                "\n| 명칭 | 연결 컬럼 |\n" +
                "| :--- | :--- |\n" +
                "| `@pi_strYMD` | `TClientSettleRate4MobileCo.YMD` |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.ParameterColumnClaimMismatch);
        }

        [Fact]
        public void Validate_ParameterColumnClaim_AlsoChecksTablesUnderOverview()
        {
            // EXCEPTION_PROC 실물 - 관계 표가 `## 개요` 아래 `### 파라미터와 변수의 컬럼 관계`에 있다.
            var expectations = BuildExpectationsWithDdl(ExceptionProcShapedDdl,
                "dbo.TSettleMst", "dbo.TClientSettleRate4MobileCo");
            var markdown = WrapSpec("표 없음").Replace("## 개요\n내용",
                "## 개요\n내용\n\n### 파라미터와 변수의 컬럼 관계\n\n" +
                "| 구분 | 명칭 | 연결되는 컬럼 또는 표현식 |\n| :--- | :--- | :--- |\n" +
                "| 입력 매개변수 | `@pi_strYMD` | `TClientSettleRate4MobileCo.YMD` |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.ParameterColumnClaimMismatch);
        }

        [Fact]
        public void Validate_ParameterColumnClaim_IsSilentWhenDdlHasNoBindingsAtAll()
        {
            // 재료가 비면(파싱 실패·동적 SQL) 기각할 근거가 없다 - 침묵.
            var spDef = new SpDefinition
            {
                Schema = "dbo", Name = "UP_TEST", DdlText = "EXEC (@sql)",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ProcedureParameters = new List<string> { "@pi_strYMD char(8)" },
                    ReferencedTables = new List<string> { "dbo.TSettleMst" }
                }
            };
            var expectations = SpecExpectations.From(spDef)!;
            var markdown = SpecWithParameterSection(
                "| 파라미터 | 연결 컬럼 |\n| :--- | :--- |\n| `@pi_strYMD` | `TSettleMst.YMD` |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ParameterColumnClaimMismatch);
        }

        // ─────────────────────────────────────────────────────────────────────
        // 검사 A - 문장 개수 대조. POQSettleBatch1 축 B 감사 S07 🔴:
        // 명세서가 TSettleMst에 UPDATE 15개를 확정했는데 단계는 5개만 담고
        // 나머지 10개를 `/* U4: … */` 주석 한 줄로 대체했다.
        // ─────────────────────────────────────────────────────────────────────

        private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithUpdates(int count)
        {
            var rows = Enumerable.Range(1, count)
                .Select(i => new SpecDmlRow("UPDATE", i, i * 10, "TSettleMst",
                    new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()))
                .ToList();

            return new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    rows, Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };
        }

        private static BatchStepPlan LegacyStep(string code) => new(
            Code: code, Name: $"{code} 단계",
            LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC" },
            TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
            ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

        [Fact]
        public void ValidateBatchStep_FewerStatementsThanSpecConfirms_ShouldBeAnError()
        {
            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15));

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("15"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Task 17 I2 - C3를 고치면 드러나는 결함. 같은 레거시 SP가 이 Job의 여러
        // 단계에 나뉘어 있으면 개수 대조를 하지 않는다 - 한 단계가 그 SP의 문장
        // 몇 개를 맡는지 알 방법이 없다("귀속할 수 없으면 침묵한다").
        //
        // 실측(POQSettleProc4): UP_UTIL_SETTLE_EXCEPTION_PROC이 S10~S27 18개
        // 단계에 나뉘어 있다. C3 수정 전에는 이 SP의 재료를 아예 못 찾아 검사 A가
        // 무실행이었으므로 이 결함이 드러나지 않았다 - C3를 고치는 순간 33개
        // 단계가 만족 불가능한 개수 요구를 받는다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_LegacyProcedureSplitAcrossSteps_CountCheckIsSilent()
        {
            var allSteps = new[] { LegacyStep("S10"), LegacyStep("S11") }; // 같은 SP를 나눠 담당

            // 명세서는 UPDATE 15개를 확정하지만 이 단계는 1개만 담는다 - 분할되지
            // 않았다면 이것은 오류다(위 ShouldBeAnError 테스트가 그것을 지킨다).
            var markdown = "### S10 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S10"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15), allSteps);

            Assert.DoesNotContain(result.Errors, e => e.Contains("DML 범위 표는"));
        }

        [Fact]
        public void ValidateBatchStep_LegacyProcedureNotSplit_CountCheckStillFires_EvenWithAllStepsProvided()
        {
            // allSteps를 받아도, 이 SP를 담당하는 단계가 자기 자신뿐이면(분할 아님)
            // 개수 대조는 그대로 살아 있어야 한다 - I2 수정이 검사 A를 통째로
            // 죽이는 방향으로 과잉 적용되지 않았는지 본다.
            var allSteps = new[] { LegacyStep("S07") };

            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15), allSteps);

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("15"));
        }

        [Fact]
        public void ValidateBatchStep_MoreStatementsThanSpec_IsSilent()
        {
            // 단계는 배치 제어 테이블에 정당하게 더 쓴다. 초과는 결함이 아니다.
            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(1));

            Assert.DoesNotContain(result.Errors, e => e.Contains("DML 범위 표는"));
        }

        [Fact]
        public void ValidateBatchStep_NewStepWithoutLegacy_IsSilent()
        {
            var step = new BatchStepPlan("S01", "S01 단계", Array.Empty<string>(),
                new[] { "batch.BatchRun" }, Array.Empty<string>(), false, Array.Empty<string>());

            var result = new MechanicalValidator().ValidateBatchStep(
                "### S01 단계\n\n```sql\nSELECT 1;\n```\n", step, new[] { "batch.BatchRun" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15));

            Assert.DoesNotContain(result.Errors, e => e.Contains("DML 범위 표는"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Task 17 C3 - 조회 키가 이 파일 자신의 규약과 어긋난다.
        //
        // 실측: `LegacyProcedures` 항목 314개 중 134개(43%)가 스키마 접두사 없는
        // 이름이고(`UP_UTIL_SETTLE_EXCEPTION_PROC`), 명세서의 FileName은 언제나
        // `"{Schema}.{Name}"`이다(`ReSet.Cli/Program.cs:772-774`). 원문 그대로
        // 대조하면 이 조합은 영원히 못 찾아 POQSettleProc1~5의 검사 A·B·C·D가 조용히
        // 무실행이었다. `SpecStatementFactsExtractor.Extract`(실물)와
        // `ValidateBatchStep`을 함께 써서 두 파일의 수정이 실제로 맞물리는지 본다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_SchemaLessLegacyProcedureName_MatchesExtractedSpecFacts()
        {
            const string spec = """
                ### DML 범위 (기계 확정 — 수정 금지)

                | 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼(조인 결합 포함 · 대상 한정 아님) | 기준일 파라미터 적용(최상위 WHERE 기준) | 조인 키 | GROUP BY | ORDER BY |
                | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
                | UPDATE 1 | 30 | TSettleMst | YMD | 예 | (없음) | — | — |
                """;

            var statementFacts = SpecStatementFactsExtractor.Extract(
                new[] { ("dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", spec) });

            // 스키마 접두사 없음 - 실물 43%의 모양.
            var step = new BatchStepPlan(
                Code: "S10", Name: "S10 단계",
                LegacyProcedures: new[] { "UP_UTIL_SETTLE_EXCEPTION_PROC" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            // 명세서는 UPDATE 1개를 확정하는데 단계 본문에는 하나도 없다.
            var markdown = "### S10 단계\n\n```sql\nSELECT 1;\n```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, statementFacts);

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("TSettleMst"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 픽스 라운드 1 - 검사 A 리뷰 Critical 2건.
        //
        // [Critical 1] "빠진 번호" 목록이 접두사 스킵(actual개를 앞에서부터 스킵)으로
        // 계산돼, S07의 실제 모양(있음 1·2·3·12·13, 없음 4~11·14·15)에서 12·13을
        // 빠졌다고 오보하고 4·5는 빠진 목록에서 빠뜨렸다. 이 문자열은
        // SuggestedPromptFix를 거쳐 재생성 프롬프트의 floorFeedback으로 그대로
        // 들어가므로, 틀린 번호는 모델에게 틀린 시정 지시가 된다.
        //
        // [Critical 2] 레거시 SP가 둘 이상 합쳐진 단계는 Ordinal이 SP마다 1부터
        // 다시 시작해 번호를 합쳐 말할 수 없는데, 예전 코드는 DmlRows를 SP 경계
        // 없이 SelectMany로 평탄화해 번호 열거를 그대로 냈다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_MissingOrdinals_OmitsPresentOrdinalsForNonPrefixShortfall()
        {
            // 있음: 1·2·3·12·13(전부 앵커로 확인). 없음: 4~11·14·15.
            // 접두사 스킵이면 "6~15가 없다"고 잘못 말한다 - 12·13은 실제로 있다.
            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U1\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "-- U2\n" +
                "UPDATE A SET A.CLCOMM = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "-- U3\n" +
                "UPDATE A SET A.CLCOMM = 3 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "-- U12\n" +
                "UPDATE A SET A.CLCOMM = 12 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "-- U13\n" +
                "UPDATE A SET A.CLCOMM = 13 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15));

            Assert.Contains(result.Errors, e =>
                e.Contains("빠진 것으로 보이는 번호: 4, 5, 6, 7, 8, 9, 10, 11, 14, 15"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("번호: 6, 7, 8, 9, 10"));
        }

        [Fact]
        public void ValidateBatchStep_MultipleLegacyProcedures_DoesNotEnumerateOrdinals()
        {
            // 두 레거시 SP 모두 TSettleMst에 UPDATE 2개씩(각자 Ordinal 1·2부터
            // 다시 시작) 확정한다. 합계(4개) 대조는 옳아도, 번호를 합쳐 말하면
            // 서로 다른 SP의 "갱신 1"이 같은 번호로 충돌해 뜻을 잃는다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_A"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("UPDATE", 1, 10, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("UPDATE", 2, 20, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
                ["SP_B"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("UPDATE", 1, 10, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("UPDATE", 2, 20, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
            };

            var step = new BatchStepPlan(
                Code: "S07", Name: "S07 단계",
                LegacyProcedures: new[] { "dbo.SP_A", "dbo.SP_B" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U1\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("1개만") && e.Contains("4개를 확정"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("빠진 것으로 보이는 번호"));
        }

        [Fact]
        public void ValidateBatchStep_NoAnchoredStatements_OmitsOrdinalList()
        {
            // 단일 SP지만 앵커 주석이 하나도 없다 - 어느 갱신 번호가 실제로 있는지
            // 알 길이 없으므로 개수만 말하고 번호는 열거하지 않는다.
            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15));

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("1개만") && e.Contains("15개를 확정"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("빠진 것으로 보이는 번호"));
        }

        [Fact]
        public void ValidateBatchStep_PartiallyAnchoredStatements_OmitsOrdinalList()
        {
            // 두 문장 중 하나만 앵커가 있다. 앵커로 확인된 것만 "있음"으로 치면
            // 실제로 있는데 앵커가 없는 두 번째 문장이 "빠짐"으로 잘못 보고된다 -
            // 앵커 없는 문장이 하나라도 섞이면 번호 열거를 통째로 접는다.
            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U1\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "UPDATE A SET A.CLCOMM = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15));

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("2개만") && e.Contains("15개를 확정"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("빠진 것으로 보이는 번호"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 픽스 라운드 1 - Minor: off-by-one 경계. 기존 테스트는 1 대 15라
        // 경계(정확히 같음·하나만 부족)를 시험하지 않았다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_ExactStatementCountMatchesSpec_IsSilent()
        {
            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(1));

            Assert.DoesNotContain(result.Errors, e => e.Contains("DML 범위 표는"));
        }

        [Fact]
        public void ValidateBatchStep_OneFewerThanSpec_IsAnError()
        {
            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(2));

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("1개만") && e.Contains("2개를 확정"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 픽스 라운드 2 - 재리뷰 [2] PARTIALLY CLOSED + 새 Important.
        //
        // [2] facts는 statementFactsByProcedure에서 실제로 찾은 것만 남긴
        // 부분집합이라(365-369행), 재료를 못 찾은 SP가 있어도 facts.Count == 1일
        // 수 있다 - 그 상태에서도 단계 SQL에는 못 찾은 SP 출신 문장이 섞여 있고
        // 그 SP의 앵커 번호도 1부터 다시 시작한다. 게이트는 재료를 찾았는지가
        // 아니라 원본 SP가 정말 하나인지(step.LegacyProcedures.Count == 1)를
        // 물어야 한다.
        //
        // [새 Important] 청크 분할 시 물리 조각마다 같은 앵커 주석을 반복하는
        // 것은 자연스러운 작성 패턴이다. 앵커를 HashSet으로 모으면 중복이
        // 합쳐져 missing 개수가 실제 부족분(expectedCount - actual)과 어긋난다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_FactsCoverOnlyOneOfMultipleLegacyProcedures_DoesNotEnumerateOrdinals()
        {
            // step.LegacyProcedures는 둘(SP_A, SP_B)인데 명세서 재료는 SP_A만
            // 찾았다(SP_B의 Spec.md 파싱 실패·specs 배치 누락 등). facts.Count == 1을
            // 게이트로 쓰면 이 상태를 "레거시 SP가 하나"로 오인해 번호를 열거한다 -
            // 그러나 SP_B도 단계 SQL에 문장을 남길 수 있고 자기 번호로 1부터
            // 다시 앵커한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_A"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("UPDATE", 1, 10, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("UPDATE", 2, 20, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("UPDATE", 3, 30, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
            };

            var step = new BatchStepPlan(
                Code: "S07", Name: "S07 단계",
                LegacyProcedures: new[] { "dbo.SP_A", "dbo.SP_B" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U1\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("1개만") && e.Contains("3개를 확정"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("빠진 것으로 보이는 번호"));
        }

        [Fact]
        public void ValidateBatchStep_DuplicateAnchorsInSameGroup_DoesNotEnumerateOrdinals()
        {
            // 두 문장이 모두 `-- U4`로 앵커됐다(청크 분할 시 물리 조각마다 같은
            // 앵커를 반복하는 자연스러운 작성 패턴). 앵커를 집합으로 모으면 4가
            // 하나로 합쳐져 missing 개수(14)가 실제 부족분(expectedCount 15 -
            // actual 2 = 13)과 어긋난다 - 그 상태의 번호는 믿을 수 없다.
            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U4\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "-- U4\n" +
                "UPDATE A SET A.CLCOMM = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15));

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("2개만") && e.Contains("15개를 확정"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("빠진 것으로 보이는 번호"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Task 16 - C1. 명세서 DML 범위 표에는 대조 불가능한 행이 실재한다:
        // Kind == "SELECT" 행(DmlCollector가 SelectStatement를 아예 방문하지
        // 않는다)과, 대상 칸이 "—"이거나 한 글자 별칭(예: "A")인 행(실물
        // 테이블명이 아니다 - 실측: output/Procedures/dbo.UP_UTIL_SETTLE_PROC_ETC
        // SELECT 1 대상 "—", output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD
        // DELETE 1 대상 "A"). 이 행들은 actual이 영구히 0이라 모델이 무엇을
        // 써도 매 회차 같은 오류가 재발한다. 대조 가능한 행만 대상으로 좁힌다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_SpecSelectRow_IsNotDemanded()
        {
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("SELECT", 1, 43, "—",
                            Array.Empty<string>(), Array.Empty<string>(),
                            Array.Empty<string>(), Array.Empty<string>()),
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "INSERT INTO batch.BatchStepJournal (RunId) VALUES (1);\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("SELECT"));
        }

        [Fact]
        public void ValidateBatchStep_SpecRowTargetIsBareAlias_IsNotDemanded()
        {
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("DELETE", 1, 36, "A",
                            new[] { "ProcYMD" }, Array.Empty<string>(),
                            Array.Empty<string>(), Array.Empty<string>()),
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "INSERT INTO batch.BatchStepJournal (RunId) VALUES (1);\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("DELETE"));
        }

        [Fact]
        public void ValidateBatchStep_ComparableRowStillFlagged_AlongsideUncomparableRows()
        {
            // 대조 불가능한 행을 침묵으로 걸러도, 실물 테이블명을 가진 행의
            // 진짜 결손은 여전히 잡혀야 한다 - 좁히다가 검사를 죽이면 안 된다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("SELECT", 1, 10, "—",
                            Array.Empty<string>(), Array.Empty<string>(),
                            Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("UPDATE", 1, 20, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(),
                            Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("UPDATE", 2, 30, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(),
                            Array.Empty<string>(), Array.Empty<string>()),
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("1개만") && e.Contains("2개를 확정"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("SELECT"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Task 16 - C2. 파싱 실패가 실재하는 문장을 "없다"로 만든다. 실측:
        // `output/Jobs/POQSettleBatch1/agent/steps/S12.md`는 DELETE·INSERT가
        // 전문으로 있는데, 같은 펜스 뒤쪽의 `INSERT … SELECT /* 주석만 */
        // FROM …`이 파싱에 실패해 펜스 전체가 버려지고 검사 A가 "0개"라고
        // 잘못 보고했다. 하나라도 못 읽은 펜스가 있으면 검사 A는 이 단계의
        // 개수 대조를 통째로 접는다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_UnparsableFenceElsewhereInStep_FoldsCountCheck()
        {
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_Util_Settle_Summary"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("DELETE", 1, 31, "TSettleByTX",
                            new[] { "YMD" }, Array.Empty<string>(),
                            Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("DELETE", 2, 35, "TPartialCancelByTX",
                            new[] { "YMD" }, Array.Empty<string>(),
                            Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("DELETE", 3, 39, "TSettleByIN",
                            new[] { "YMD" }, Array.Empty<string>(),
                            Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("DELETE", 4, 43, "TSettleByOUT",
                            new[] { "YMD" }, Array.Empty<string>(),
                            Array.Empty<string>(), Array.Empty<string>()),
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var step = new BatchStepPlan(
                Code: "S12", Name: "S12 단계",
                LegacyProcedures: new[] { "dbo.UP_Util_Settle_Summary" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleByTX" },
                ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            // 첫 펜스는 DELETE 1개만 담아(명세서는 4개를 확정) 정상적으로는
            // 부족을 보고해야 한다. 둘째 펜스는 S12.md:75-80과 같은 모양으로
            // 파싱에 실패한다(SELECT 목록이 통째로 주석) - 이 펜스 안에도
            // 진짜 DELETE·INSERT가 있을 수 있으므로 개수 대조 전체를 접는다.
            var markdown = "### S12 단계\n\n" +
                "```sql\n" +
                "DELETE FROM SETTLE_POQ_DB.dbo.TSettleByTX WHERE YMD = @pi_strYMD;\n" +
                "```\n\n" +
                "```sql\n" +
                "INSERT INTO SETTLE_POQ_DB.dbo.TPartialCancelByTX\n" +
                "SELECT\n" +
                "    /* 동일한 집계 열 */\n" +
                "    /* PLTID를 포함 */\n" +
                "FROM SETTLE_POQ_DB.dbo.TSettleMst\n" +
                "WHERE YMD = @pi_strYMD\n" +
                "GROUP BY YMD;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleByTX", "dbo.TPartialCancelByTX" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("DML 범위 표는"));
        }

        [Fact]
        public void ValidateBatchStep_AllFencesParsable_StillReportsShortfall()
        {
            // 대조 회귀 방지: 못 읽은 펜스가 하나도 없으면 검사 A는 평소대로
            // 부족을 그대로 보고해야 한다 - C2 픽스가 검사 자체를 죽이면 안 된다.
            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, FactsWithUpdates(15));

            Assert.Contains(result.Errors, e => e.Contains("UPDATE") && e.Contains("1개만") && e.Contains("15개를 확정"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 검사 B - 앵커 문장의 조인 키·최상위 WHERE 술어 컬럼 누락.
        // S07 🟠: 갱신 13의 최상위 WHERE(Y.YMD, Y.PGNAME)가 통째로 빠졌다.
        // S11 🟠: 갱신 9의 TPLCardEDIMst 조인에서 YMD·UseState 결합이 빠졌다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_AnchoredStatementMissingPredicateColumn_ShouldBeAnError()
        {
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 13, 410, "TSettleMst",
                        new[] { "PLTID", "ID", "YMD", "PGNAME" }, new[] { "PLTID", "ID" },
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U13: 카드사 원가 반영 */\n" +
                "UPDATE Y SET Y.CLCOMM = X.Amt FROM dbo.TSettleMst AS Y\n" +
                "INNER JOIN CardCost AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("갱신 13") && e.Contains("YMD"));
            Assert.Contains(result.Errors, e => e.Contains("PGNAME"));
        }

        [Fact]
        public void ValidateBatchStep_AnchoredStatementMissingJoinKey_ShouldBeAnError()
        {
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 9, 300, "TSettleMst",
                        new[] { "PLTID" }, new[] { "PLTID", "YMD", "UseState" },
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S11 단계\n\n```sql\n" +
                "-- 갱신 9\n" +
                "UPDATE A SET A.EDIReqYMD = E.ReqYMD FROM dbo.TSettleMst AS A\n" +
                "INNER JOIN dbo.TPLCardEDIMst AS E ON A.PLTID = E.PLTID\n" +
                "WHERE A.PLTID > 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S11"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("조인 키") && e.Contains("YMD"));
        }

        [Fact]
        public void ValidateBatchStep_WithoutAnchors_StaysSilent()
        {
            // 코퍼스 실측(2026-08-24): 326개 단계 파일 전체에서 앵커가 0개 잡힌다 -
            // 실물이 `/* U1: … */` → `SET @v_currentStepId = -101;` → `UPDATE …`
            // 순서인데 ReadAnchor가 문장 직전의 공백·주석만 보기 때문이다. 이 상태에서
            // "번호를 다십시오" 요구를 내면 실물과 맞지 않는 거짓 지시가 되고,
            // SuggestedPromptFix → floorFeedback으로 재생성 프롬프트에 실려도 모델이
            // 앵커를 달아도 여전히 못 읽히므로 maxTries를 소진할 수 있다
            // (docs/known-defects.md 참고). 그래서 앵커가 하나도 없으면 검사 B는
            // 조용히 지나가야 한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("UPDATE", 1, 30, "TSettleMst", new[] { "YMD" },
                            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("UPDATE", 2, 58, "TSettleMst", new[] { "YMD" },
                            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("갱신 번호를 주석"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 검사 B 픽스 라운드 1.
        //
        // [1] Critical - 앵커 행 조회가 SP 경계를 무시했다. `rows.FirstOrDefault(Ordinal,
        // Kind)`만 쓰면 레거시 SP가 둘 이상이고 두 SP 모두 같은 (Ordinal, Kind)(예:
        // 둘 다 "UPDATE 1")를 가질 때 먼저 열거된 SP의 행과 대조되어 실제로는 다른 SP의
        // 요구를 충족한 문장에 거짓 오류가 난다. `CheckStatementCountAgainstSpec`이
        // `singleSource` 가드로 막아 놓은 것과 같은 문제라, (Ordinal, Kind)로 매칭되는
        // 명세서 행이 정확히 하나일 때만 대조하고 둘 이상이면(어느 SP 것인지 알 수 없음)
        // 그 문장만 침묵한다 - 번호가 겹치지 않는 다중 SP 단계에서는 검출력이 유지된다.
        //
        // [2] Important - `JoinKeys` 칸을 WHERE·JOIN 합집합에 대조하면 조인 키가
        // ON절에서 빠지고 WHERE에만 남은 것(S11 🟠의 실제 모양)을 놓친다. 명세서 DML
        // 범위 표 헤더상 "조인 결합 포함"은 술어 칸(PredicateColumns)에만 해당하므로,
        // 술어 칸은 합집합, 조인 키 칸은 JoinColumns(ON절)에만 대조한다.
        //
        // [3] Important - 같은 앵커가 청크로 반복된 문장들을 독립적으로 대조하면
        // 합치면 충족되는 요구를 조각 단위로만 봐서 오검출한다. 같은 (앵커, 종류)의
        // 조각을 합쳐 한 번만 대조한다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_AmbiguousAnchorAcrossMultipleLegacyProcedures_IsSilent()
        {
            // SP_A의 UPDATE 1은 YMD만 요구하고 SP_B의 UPDATE 1은 PLTID·PGNAME을
            // 요구한다. 문장은 실제로 SP_B의 요구(PLTID, PGNAME)를 충족하지만, 어느
            // SP 출신인지 표시가 없어 (Ordinal, Kind)만으로는 두 행 중 하나를 고를
            // 수 없다 - 귀속할 수 없으면 침묵해야 한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_A"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 1, 10, "TSettleMst",
                        new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
                ["SP_B"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 1, 10, "TSettleMst",
                        new[] { "PLTID", "PGNAME" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
            };

            var step = new BatchStepPlan(
                Code: "S07", Name: "S07 단계",
                LegacyProcedures: new[] { "dbo.SP_A", "dbo.SP_B" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U1\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A " +
                "WHERE A.PLTID = @p AND A.PGNAME = @p2;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_NonOverlappingAnchorsAcrossMultipleLegacyProcedures_IsStillChecked()
        {
            // SP_A는 UPDATE 1, SP_B는 UPDATE 2를 확정해 번호가 겹치지 않는다 - 이때는
            // (Ordinal, Kind)로 유일하게 식별되므로 대조 능력이 그대로 유지되어야 한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_A"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 1, 10, "TSettleMst",
                        new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
                ["SP_B"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 2, 20, "TSettleMst",
                        new[] { "PLTID" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
            };

            var step = new BatchStepPlan(
                Code: "S07", Name: "S07 단계",
                LegacyProcedures: new[] { "dbo.SP_A", "dbo.SP_B" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            // U1(SP_A)은 YMD가 빠져 있어 오류가 나야 한다.
            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U1\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.PLTID = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼") && e.Contains("YMD"));
        }

        [Fact]
        public void ValidateBatchStep_JoinKeyPresentOnlyInWhereNotOn_ShouldBeAnError()
        {
            // S11 🟠의 실제 모양: 조인 키(PLTID, YMD)가 ON절이 아니라 WHERE 필터로만
            // 등장한다. WHERE·JOIN 합집합으로 대조하면 이 결함을 놓친다 - 조인 키 칸은
            // JoinColumns(ON절)에만 대조해야 한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 9, 300, "TSettleMst",
                        Array.Empty<string>(), new[] { "PLTID", "YMD" },
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S11 단계\n\n```sql\n" +
                "-- 갱신 9\n" +
                "UPDATE A SET A.EDIReqYMD = E.ReqYMD FROM dbo.TSettleMst AS A\n" +
                "INNER JOIN dbo.TPLCardEDIMst AS E ON A.RowGuid = E.RowGuid\n" +
                "WHERE A.PLTID = @p AND A.YMD = @p2;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S11"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("조인 키") && e.Contains("PLTID") && e.Contains("YMD"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 검사 B 태스크 22 - 문장↔spec 행 대응 재설계 뒤 드러난 두 새 함정.
        //
        // [1] 대상 테이블을 대조하지 않았다. (Ordinal, Kind)만 보면 단계가 완전히
        // 다른 물리 테이블(섀도·스테이징 테이블)을 갱신하는 문장도 원본 대상
        // 테이블의 행과 매칭된다 - 실물(POQSettleProc10/S08)은 `batch.
        // POQSettleLedgerStageImage`를 갱신하는데 원본은 `TSettleMst`이고,
        // 스테이징 전용 제어 컬럼(ImageRunId·ImageType)이 "명세서에 없는 술어"로
        // 거짓 발화했다.
        //
        // [2] 조인 파트너가 CTE·파생 테이블이면 조인 키 대조가 못 미덥다. 실물
        // (POQSettleBatch1/S07 U2·U13·U17)은 원본 단일 UPDATE를 `UPDATE 대상 ...
        // FROM 대상 AS Y INNER JOIN <계산용 CTE> ON <좁은 키>`로 재구성한다 -
        // 진짜 필터(PGName·ClientID 등)는 그 CTE 안의 WHERE에 있는데 최상위만
        // 보는 조인 키 대조는 이를 "없다"고 거짓 보고한다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_AnchoredStatementTargetsADifferentPhysicalTable_StaysSilent()
        {
            // 실물 모양(POQSettleProc10/S08): 같은 (Ordinal, Kind)라도 문장의
            // 실제 대상이 원본 spec 행의 대상 테이블과 다르면(섀도·스테이징
            // 테이블) 그 행과 대조해서는 안 된다 - 스테이징 전용 제어 컬럼을
            // "명세서가 확정한 컬럼이 없다"로 오인한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 7, 200, "TSettleMst",
                        new[] { "PLTID", "YMD" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S08 단계\n\n```sql\n" +
                "-- 갱신 7\n" +
                "UPDATE B SET B.CLCOMM = 1 FROM [batch].[POQSettleLedgerStageImage] AS B\n" +
                "WHERE B.ImageRunId = @pi_runId AND B.ImageType = 'Build';\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S08"), new[] { "batch.POQSettleLedgerStageImage" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_AnchoredStatementWithOpaqueJoinSource_SuppressesJoinKeyReportOnly()
        {
            // 이 픽스처가 잠그는 것 - (1) 조인 파트너가 CTE면(HasOpaqueJoinSource)
            // 조인 키 칸 대조는 침묵한다 - CTE 안 실제 키를 최상위 JoinColumns로는
            // 볼 수 없기 때문이다. (2) 최상위 WHERE 술어 컬럼은 컬럼 단위로
            // 판정한다 - 이 합성 사례는 PGNAME만 CTE로 이전했고 YMD는 SQL
            // 어디에도 없는 "하나는 이전, 하나는 소실" 조합이라 PGNAME은
            // 침묵하고 YMD만 발화해야 한다.
            //
            // [주의] 이 SQL은 실물 S07 U13이 아니라 그 모양을 본뜬 합성 사례다.
            // 실제 POQSettleBatch1/S07의 U13은 YMD와 PGNAME을 둘 다 CTE 안
            // WHERE(`WHERE A.YMD = @pi_strYMD AND A.PGNAME IN (…)`)에 두므로
            // 둘 다 이전이고, 검사 B는 완전히 침묵한다 - 그 재판정은 Task 3이
            // 기록한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 13, 336, "TSettleMst",
                        new[] { "PLTID", "ID", "YMD", "PGNAME" }, new[] { "PLTID", "ID", "ClientID", "CardCPID" },
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U13: 원천카드 수수료 */\n" +
                "SET @v_currentStepId = -20;\n" +
                ";WITH CardCost AS\n" +
                "(\n" +
                "    SELECT A.PLTID, A.ID, A.PGName\n" +
                "    FROM dbo.TSettleMst AS A\n" +
                "    INNER JOIN dbo.TPLCardTxMst AS B ON A.PLTID = B.PLTID\n" +
                "    INNER JOIN dbo.TCardContractMgmt AS C ON B.CardCPID = C.CardCPID\n" +
                "    WHERE A.PGNAME IN (SELECT value FROM STRING_SPLIT(@v_strCardPGNames, '+'))\n" +
                ")\n" +
                "UPDATE Y SET Y.CLCOMM = X.PLTID\n" +
                "FROM dbo.TSettleMst AS Y\n" +
                "INNER JOIN CardCost AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));

            // 메시지 뒷부분은 명세서 행의 값을 그대로 인용하므로 PGNAME이
            // 정당하게 나온다(명세서가 확정한 전체 목록). 보고된 "빠진 컬럼"
            // 목록만 잘라서 본다 - 거기엔 YMD만 있어야 한다.
            var reported = error[..error.IndexOf("이(가) 없습니다", StringComparison.Ordinal)];
            Assert.Contains("YMD", reported);
            Assert.DoesNotContain("PGNAME", reported);

            Assert.DoesNotContain(result.Errors, e => e.Contains("조인 키"));
        }

        [Fact]
        public void ValidateBatchStep_ChunkedAnchoredStatementsMergeColumns_IsSilentWhenUnionSatisfies()
        {
            // 같은 앵커(U4)가 물리 조각 둘에 반복된다. 조각1엔 YMD만, 조각2엔
            // PGNAME만 있어 개별 조각 기준으로는 둘 다 부족해 보이지만, 청크 분할은
            // 논리적으로 한 문장이라 합치면 요구(YMD, PGNAME)를 전부 충족한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 4, 100, "TSettleMst",
                        new[] { "YMD", "PGNAME" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U4\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "-- U4\n" +
                "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.PGNAME = @p2;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 검사 B·C 태스크 10 - INSERT 문장을 후보에서 뺀다.
        //
        // 코퍼스 전수 스윕(2026-08-25, 326개 단계) 실측: 코드 앵커를 켠 뒤
        // 검사 B 발화가 1건 → 269건으로 늘었는데 그중 199건(74%, 15개 조합)이
        // 이 축의 구조적 거짓양성이었다. 원인은 StepSqlStatementReader.cs의
        // DmlCollector.Visit(InsertStatement)가 `Add("INSERT", node,
        // node.InsertSpecification?.Target, null, null)`로 where·from에 항상
        // null을 넘긴다는 데 있다(같은 파일 Visit(UpdateStatement)·
        // Visit(DeleteStatement)는 실제 WhereClause·FromClause를 넘긴다) -
        // 그래서 모든 INSERT 문장의 PredicateColumns·JoinColumns는 실제 SQL과
        // 무관하게 구조적으로 항상 빈 목록이고, 검사 B는 그 빈 목록을 "명세서가
        // 확정한 술어 컬럼이 없다"로 오인한다. 실물:
        // output/Jobs/POQSettleBatch1/agent/steps/S04.md:39-52가 실제로는
        // `WHERE USESTATE = 0`을 담은 INSERT...SELECT인데도 이 결함으로
        // 오탐이 났다.
        //
        // 이 좁힘은 **INSERT를 영구히 검사하지 않는다는 정책이 아니다** - 재료
        // (StepSqlStatementReader가 where·from을 null로 넘기는 배선) 결손에
        // 대한 한시적 조치다. **되돌릴 지점**: DmlCollector.Visit(InsertStatement)이
        // `InsertSpecification.InsertSource`의 SELECT에서 WHERE·FROM을 실제로
        // 꺼내 PredicateColumns·JoinColumns를 채우도록 고쳐지면, 그때 이 좁힘도
        // 함께 걷어내고 재측정해야 한다(그 배선 수정 자체는 이 태스크 범위 밖).
        //
        // 검사 C(CheckAnchoredStatementExtras)도 같은 이유로 INSERT를 후보에서
        // 뺀다 - extras 역시 Statement.PredicateColumns(구조적으로 항상 빈
        // 목록)에서만 뽑으므로 오늘은 침묵하지만, 검사 B만 고치고 검사 C를 그대로
        // 두면 두 검사가 서로 다른 후보 집합을 본다는 불변식이 깨진다(코퍼스
        // 스윕상 검사 C의 38건은 12개 조합 전부 UPDATE라 이 좁힘의 영향을 받지
        // 않아야 한다).
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_AnchoredInsertStatement_IsExcludedFromCheckB()
        {
            // 실물 결함 모양(S04.md:39-52)을 재현한다: INSERT INTO ... SELECT ...
            // WHERE ...는 실제로 술어 컬럼(USESTATE)을 담지만, DmlCollector가
            // INSERT의 where·from을 항상 null로 넘겨 StepSqlStatement.PredicateColumns가
            // 구조적으로 비어 있다. 좁히기 전에는 이 빈 목록을 근거로 검사 B가
            // "명세서가 확정한 최상위 WHERE 술어 컬럼 USESTATE이(가) 없습니다"를
            // 낸다 - 문장이 실제로는 그 컬럼을 담고 있는데도.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("INSERT", 5, 120, "TSettleMst",
                        new[] { "USESTATE" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S04 단계\n\n```sql\n" +
                "-- 갱신 5\n" +
                "INSERT INTO dbo.TSettleMst (PLTID, YMD)\n" +
                "SELECT PLTID, YMD FROM dbo.TSettleSrc WHERE USESTATE = 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S04"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_AnchoredInsertStatement_IsExcludedFromCheckC()
        {
            // 검사 C도 같은 (Ordinal, Kind) 그룹핑·후보 선정 경로를 타므로 INSERT를
            // 같이 뺀다 - 오늘은 extras가 구조적으로 항상 비어 있어 이 시나리오
            // 단독으로는 검사 C가 발화하지 않지만(Statement.PredicateColumns가
            // where=null이라 항상 빈 목록), 두 검사가 같은 후보 집합 규약을
            // 지킨다는 것을 회귀로 고정한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("INSERT", 5, 120, "TSettleMst",
                        new[] { "USESTATE" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S04 단계\n\n```sql\n" +
                "-- 갱신 5\n" +
                "INSERT INTO dbo.TSettleMst (PLTID, YMD)\n" +
                "SELECT PLTID, YMD FROM dbo.TSettleSrc WHERE USESTATE = 0 AND RUNID = @pi_runId;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S04"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("명세서에 없는 술어 컬럼"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 검사 C - 명세서에 없는 최상위 술어 컬럼이 문장에 붙었는가.
        //
        // [이 검사가 POQSettleBatch1 축 B 감사의 S07 🟠·S09 🟠 어느 쪽도 닫지
        // 못한다 - 픽스 라운드 1 리뷰 실측]
        // 아래 첫 [Fact]는 합성(synthetic) 시나리오다 - "최상위 WHERE에 명세서
        // 밖 컬럼이 붙으면 잡는다"는 검사의 일반 동작만 보여줄 뿐, 실제 S09
        // 결함을 재현하지 않는다. 실제 S09 결함(`-9` 사전 검증 EXISTS에
        // `SM.TxAmt = 0` 추가)은 `output/Jobs/POQSettleBatch1/agent/steps/S09.md`의
        // `IF EXISTS (...) BEGIN ... END` 가드 안에 있다. 리뷰가 그 파일 전체를
        // StepSqlStatementReader.Read()로 파싱해 실측한 결과: DELETE 1·INSERT 1·
        // UPDATE 5개, 총 7개 문장만 나오고 가드는 전혀 보이지 않는다 -
        // DmlCollector(StepSqlStatementReader.cs)가 UpdateStatement·
        // DeleteStatement·InsertStatement만 방문하고 IfStatement를 방문하지
        // 않기 때문이다. 설사 방문하더라도 명세서 DML 범위 표
        // (`output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md:218-229`)에는
        // "IF" 종류 행이 아예 없다 - SpecStatementFactsExtractor가 UPDATE·INSERT·
        // DELETE·SELECT만 갱신 표로 인정한다. 그 가드의 `TxAmt = 0`은 같은
        // Spec.md의 DELETE 1·UPDATE 1~5 행에서는 정당한 최상위 술어다(223~229행) -
        // "TxAmt가 명세서에 없다"가 아니라 "가드 자체가 재료에 없다"가 원인이라,
        // 이 검사로도 CheckAnchoredStatementFacts로도 닿지 않는다.
        //
        // [집계(GROUP BY·HAVING) 검사를 넣지 않은 이유 - 실측, S07도 닫지 못함]
        // 프로브 두 가지로 확인: (1) `WHERE Y.PLTID IN (SELECT PLTID FROM dbo.TTx
        // GROUP BY PLTID HAVING SUM(TxAmt) = 0)`(WHERE의 IN 하위질의) -
        // HasGrouping=True. (2) `FROM T INNER JOIN (SELECT ... GROUP BY ...
        // HAVING SUM(TxAmt)=0) AS K ON ...`(FROM절 파생 테이블) - 역시
        // HasGrouping=True.
        //
        // [정정 - 픽스 라운드 2] 이전 버전은 여기서 "실제 S07 결함이 이 두 번째
        // 모양"이라며 `dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md:480`을 근거로
        // 들었다 - 틀렸다. S07의 레거시는 `dbo.UP_UTIL_SETTLE_EXCEPTION_PROC`이고
        // (`[Approved Step List]`: S07 | 예외 정책 적용 | Legacy:
        // dbo.UP_UTIL_SETTLE_EXCEPTION_PROC), 그 SP의 명세서에는 HAVING·GROUP BY
        // 사용이 원본부터 0건이다(실측: `grep -c HAVING`·`grep -c "GROUP BY"` 모두
        // output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md에서 0 -
        // 유일한 "GROUP BY" 등장은 DML 범위 표의 열 헤더 문구다). 인용했던 두
        // 번째 모양(파생 테이블 `K`의 `HAVING SUM(TxAmt) = 0`)은 실제로는
        // COMM_UPD(별도 SP, S08의 레거시)의 UPDATE 7 원본에 있는 정상 집계다
        // (같은 Spec.md:480·492) - 두 SP가 우연히 같은 서수("UPDATE 7")를 써서
        // 혼동이 생겼다.
        //
        // 그래서 이 검사를 넣지 않는 진짜 이유는 "명세서가 S07의 원본 집계를
        // 기록하지 않아 구별할 수 없다"가 아니라, 이 대조가 원본부터 안쪽에서
        // 집계하는 다른 SP(COMM_UPD 등)의 정상 문장에 거짓 오류를 낸다는
        // 것이다. 이 신호는 "WHERE의 IN/EXISTS 하위질의"로 좁혀 말할 수도 없다 -
        // StepSqlStatementReader.GroupingProbe는 하위질의든 파생 테이블이든
        // 구분 없이 문장 전체를 훑는다. T-SQL 문법상 UPDATE·DELETE 문 자체는
        // GROUP BY·HAVING을 가질 수 없다 - 그 절은 반드시 더 안쪽 SELECT
        // (하위질의·파생 테이블) 안에서만 등장하므로, "원본에 원래 있던 집계"
        // (COMM_UPD UPDATE 7 같은)와 "이번에 새로 붙은 집계"를 이름만으로는
        // 구별할 수 없다. StepSqlStatement는 Kind·TargetTable·Anchor·
        // PredicateColumns·JoinColumns·HasGrouping만 노출하고 원본 파싱 트리를
        // 주지 않으므로, 이 파일만 고치는 범위(StepSqlStatementReader.cs는 이
        // 태스크의 쓰기 허용 범위 밖)에서는 최상위 여부를 가려낼 수 없다. 오탐을
        // 내느니(정상 문장을 결함으로 몰아 단계 재생성 예산을 낭비하느니) 이
        // 검사는 넣지 않는다.
        //
        // [검사 C가 실제로 잡는 것]
        // "명세서 DML 범위 표가 확정한 그 행의 최상위 WHERE 술어 컬럼 밖의 이름이
        // 앵커로 매칭된 문장에 붙었다"는 일반 사실만 잡는다. POQSettleBatch1의
        // 9건 중 어느 것도 이 모양이 아니라서 닫지 못하지만, 이 모양의 결함이
        // 나타나는 다른 산출물에서는 여전히 동작한다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_SyntheticTopLevelPredicateColumnNotInSpecRow_ShouldBeAnError()
        {
            // 이 시나리오는 합성이다 - 실제 S09 결함(IF EXISTS 가드 안의
            // SM.TxAmt = 0)을 재현하지 않는다(위 섹션 주석 참고). 여기서는
            // "최상위 WHERE에 명세서 밖 컬럼이 있으면 잡는다"는 검사의 일반
            // 동작만 확인한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 1, 30, "TSettleMst",
                        new[] { "YMD", "OutState" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S09 단계\n\n```sql\n" +
                "/* U1 */\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A\n" +
                "WHERE A.YMD = @p AND A.OutState IN (1,5) AND A.TxAmt = 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S09"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("TxAmt") && e.Contains("명세서에 없는"));
        }

        [Fact]
        public void ValidateBatchStep_BatchControlColumnsAreNotExtras()
        {
            // 단계는 배치 제어 컬럼으로 자기 실행을 한정한다. 이것을 결함으로 들면
            // 모든 단계가 걸린다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 1, 30, "TSettleMst",
                        new[] { "YMD" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S09 단계\n\n```sql\n" +
                "/* U1 */\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A\n" +
                "WHERE A.YMD = @p AND A.RunId = @runId;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S09"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("명세서에 없는"));
        }

        [Fact]
        public void ValidateBatchStep_WhereSubqueryGroupingIsNotFlagged_BecauseTopLevelCannotBeDistinguished()
        {
            // WHERE의 IN 하위질의 안에 HAVING SUM(TxAmt)=0이 있는 모양. UPDATE 문
            // 자체는 GROUP BY·HAVING을 가질 수 없어 이 신호는 항상 하위질의발이다 -
            // 원본에 이미 있던 하위질의 집계와 구별할 수 없으므로 이 검사는 집계를
            // 결함으로 들지 않는다(위 섹션 주석 참고). 이 모양은 실제 S07 결함의
            // 모양은 아니다 - 실제 결함은 아래
            // ValidateBatchStep_DerivedTableGroupingIsNotFlagged_MatchingRealS07Shape가
            // 재현하는 FROM절 파생 테이블 쪽이다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 7, 223, "TSettleMst",
                        new[] { "PLTID" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U7 */\n" +
                "UPDATE Y SET Y.CLCOMM = 0 FROM dbo.TSettleMst AS Y\n" +
                "WHERE Y.PLTID IN (SELECT PLTID FROM dbo.TTx GROUP BY PLTID HAVING SUM(TxAmt) = 0);\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("GROUP BY") || e.Contains("집계"));
        }

        [Fact]
        public void ValidateBatchStep_DerivedTableGroupingIsNotFlagged_MatchingRealS07Shape()
        {
            // 실제 S07 결함(output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/
            // Spec.md:480)의 모양 - HAVING SUM(TxAmt)=0이 WHERE 하위질의가 아니라
            // FROM절 파생 테이블 K의 정의 안에 있다. 프로브 실측: 이 모양도
            // HasGrouping=True를 낸다(GroupingProbe가 ScalarSubquery·
            // QueryDerivedTable을 가리지 않고 문장 전체를 훑는다) - "WHERE의
            // IN/EXISTS 하위질의"로 위험을 좁혀 말하면 이 실제 모양을 놓친다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 7, 223, "TSettleMst",
                        new[] { "YMD" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U7 */\n" +
                "UPDATE A SET A.CLCOMM = K.CLComm FROM dbo.TSettleMst AS A\n" +
                "INNER JOIN (SELECT C.PLTID, MAX(ID) AS ID FROM dbo.TX C GROUP BY C.PLTID HAVING SUM(TxAmt) = 0) AS K\n" +
                "ON A.PLTID = K.PLTID\n" +
                "WHERE A.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("GROUP BY") || e.Contains("집계"));
        }

        [Fact]
        public void ValidateBatchStep_ExtraPredicate_TargetsADifferentPhysicalTable_StaysSilent()
        {
            // 검사 B의 대상 테이블 미대조와 같은 함정(위 태스크 22 주석 참고) -
            // 검사 C도 (Ordinal, Kind)만 보면 섀도·스테이징 테이블의 제어 컬럼을
            // "명세서에 없는 술어"로 오인한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 7, 200, "TSettleMst",
                        new[] { "PLTID", "YMD" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S08 단계\n\n```sql\n" +
                "-- 갱신 7\n" +
                "UPDATE B SET B.CLCOMM = 1 FROM [batch].[POQSettleLedgerStageImage] AS B\n" +
                "WHERE B.ImageRunId = @pi_runId AND B.ImageType = 'Build';\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S08"), new[] { "batch.POQSettleLedgerStageImage" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("명세서에 없는 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_ExtraPredicate_AmbiguousAnchorAcrossMultipleLegacyProcedures_IsSilent()
        {
            // 검사 B가 이미 물려받은 함정과 같다 - 레거시 SP가 둘 이상이면 Ordinal은
            // SP마다 1부터 다시 시작한다. 두 SP가 모두 (UPDATE, 1)을 가지면 그
            // 조합만으로는 어느 SP의 행인지 알 수 없으므로 침묵한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_A"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 1, 10, "TSettleMst",
                        new[] { "YMD" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
                ["SP_B"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 1, 10, "TSettleMst",
                        new[] { "PLTID", "TxAmt" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>()),
            };

            var step = new BatchStepPlan(
                Code: "S07", Name: "S07 단계",
                LegacyProcedures: new[] { "dbo.SP_A", "dbo.SP_B" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U1\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p AND A.TxAmt = 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("명세서에 없는"));
        }

        [Fact]
        public void ValidateBatchStep_ExtraPredicate_ChunkedAnchoredStatementsMergeColumns_IsSilentWhenUnionSatisfies()
        {
            // 같은 앵커(U4)가 물리 조각 둘에 반복된다. 조각1엔 YMD, 조각2엔 PGNAME이
            // 있고 명세서는 (YMD, PGNAME) 둘 다 인정한다 - 조각별로 독립 판정해도
            // 이 경우는 결과가 같지만(양쪽 다 known), 청크 분할이 논리적으로 한
            // 문장이라는 원칙을 검사 B와 동일하게 지키는지 함께 본다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 4, 100, "TSettleMst",
                        new[] { "YMD", "PGNAME" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U4\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p;\n" +
                "-- U4\n" +
                "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.PGNAME = @p2;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("명세서에 없는"));
        }

        [Fact]
        public void ValidateBatchStep_ExtraPredicate_ChunkedAnchoredStatementsReportOnce()
        {
            // 같은 앵커(U9)가 물리 조각 둘에 반복되고, 둘 다 같은 명세서에 없는
            // 컬럼(TxAmt)을 쓴다. 조각별로 독립 대조하면 같은 오류가 두 번 실려
            // 재생성 프롬프트에 같은 지적이 중복된다 - 같은 (앵커, 종류)는 한
            // 번만 대조해 오류도 한 번만 낸다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 9, 300, "TSettleMst",
                        new[] { "YMD" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var markdown = "### S09 단계\n\n```sql\n" +
                "-- U9\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A WHERE A.YMD = @p AND A.TxAmt = 0;\n" +
                "-- U9\n" +
                "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.TxAmt = 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S09"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Single(result.Errors, e => e.Contains("TxAmt") && e.Contains("명세서에 없는"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 검사 B·C 태스크 6 - 두 신원 축(U-앵커·코드 앵커)을 합쳐 문장을
        // 귀속시킨다. 판정표(계획서 Task 6·설계 §3):
        //
        //   U-앵커 | 코드 앵커 | 판정
        //   있음   | 없음      | U-앵커 사용(기존 동작 보존)
        //   없음   | 있음      | 코드 앵커를 환산해 사용
        //   있음   | 있음·일치 | 사용
        //   있음   | 있음·불일치 | 침묵(그 문장을 후보에서 뺀다)
        //   없음   | 없음      | 후보 아님(기존 동작)
        //
        // 코드 앵커는 SET @<변수> = <음수 정수 리터럴>; 하나가 구간(직전 문장의
        // 끝 ~ 이 문장의 시작)에 정확히 하나일 때만 읽힌다
        // (StepSqlStatementReader.ReadCodeAnchor). 환산은 SpecStatementFacts.
        // ErrorCodeToOrdinal(원문 코드 → (Kind, Ordinal))로 하고, Kind도
        // 함께 대조해야 한다(코드 사전이 ("UPDATE", 9)를 주는데 문장이
        // DELETE면 매칭이 아니다).
        // ─────────────────────────────────────────────────────────────────────

        private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithCode(
            int ordinal, IReadOnlyList<string> predicateColumns, string? code)
        {
            var facts = new SpecStatementFacts(
                new[]
                {
                    new SpecDmlRow("UPDATE", ordinal, ordinal * 10, "TSettleMst",
                        predicateColumns, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                },
                Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>());

            if (code != null)
            {
                facts = facts with
                {
                    ErrorCodeToOrdinal = new Dictionary<string, (string, int)> { [code] = ("UPDATE", ordinal) }
                };
            }

            return new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = facts
            };
        }

        /// <summary>
        /// DELETE 행 하나짜리 명세서 재료. 기존 FactsWithCode는 Kind를 "UPDATE"로
        /// 못 박고 있어 어법 테스트에는 쓸 수 없다.
        /// </summary>
        private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithDeleteRow(
            int ordinal, IReadOnlyList<string> predicateColumns)
        {
            var facts = new SpecStatementFacts(
                new[]
                {
                    new SpecDmlRow("DELETE", ordinal, ordinal * 10, "TSettleMst",
                        predicateColumns, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                },
                Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>());

            return new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = facts
            };
        }

        /// <summary>
        /// INSERT 행 하나짜리 명세서 재료.
        /// </summary>
        private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithInsertRow(
            int ordinal, IReadOnlyList<string> predicateColumns)
        {
            var facts = new SpecStatementFacts(
                new[]
                {
                    new SpecDmlRow("INSERT", ordinal, ordinal * 10, "TSettleSum",
                        predicateColumns, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                },
                Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>());

            return new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = facts
            };
        }

        [Fact]
        public void ValidateBatchStep_CheckB_InsertMissingPredicate_Reports()
        {
            // 명세서가 INSERT 1의 최상위 술어로 UseState를 확정했는데 단계 SQL의
            // 원천 SELECT에 그 필터가 없다 - 실릴 행 집합이 원본과 달라진다.
            var facts = FactsWithInsertRow(1, new[] { "UseState" });

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U1: 정산 요약 적재 */\n" +
                "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
                "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleSum" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
            Assert.Contains("INSERT 1 문장에", error);
            var reported = error[..error.IndexOf("이(가) 없습니다", StringComparison.Ordinal)];
            Assert.Contains("UseState", reported);
        }

        [Fact]
        public void ValidateBatchStep_CheckB_InsertWithPredicate_Silent()
        {
            // 오탐 회귀 방지 - 이 침묵이 깨지면 코퍼스 스윕 199건(전체의 74%)의
            // 구조적 거짓양성이 되살아난 것이다.
            var facts = FactsWithInsertRow(1, new[] { "UseState" });

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U1: 정산 요약 적재 */\n" +
                "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
                "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S WHERE S.UseState = 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleSum" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_CheckC_InsertExtraPredicate_Reports()
        {
            // 명세서가 INSERT 1의 최상위 술어를 UseState 하나로 확정했는데 단계가
            // YMD를 더 붙였다 - 실릴 행 집합이 원본보다 좁아진다.
            var facts = FactsWithInsertRow(1, new[] { "UseState" });

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U1: 정산 요약 적재 */\n" +
                "INSERT INTO dbo.TSettleSum (YMD, Amt)\n" +
                "SELECT S.YMD, S.TXAMT FROM dbo.TSettleMst AS S\n" +
                "WHERE S.UseState = 0 AND S.YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleSum" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("명세서에 없는"));
            Assert.Contains("INSERT 1 문장이", error);
            Assert.Contains("YMD", error);
        }

        [Fact]
        public void ValidateBatchStep_CheckB_InsertPresence_DoesNotShiftUpdateOrdinal()
        {
            // [이 테스트가 무는 것 - 리뷰 라운드 1에서 다시 짰다]
            // 앞선 판(명세서 재료에 INSERT 행이 없어 INSERT의 Ordinal이 애초에 null인
            // 픽스처)은 INSERT를 지워도, 옛 배제 필터를 되살려도 똑같이 통과해서 아무것도
            // 물지 않았다. 그래서 INSERT가 **실제로 서수로 환산되는** 재료를 준다.
            //
            // 둘 다 U-앵커 없이 코드 앵커로만 서수를 얻게 두는 것이 핵심이다 -
            // ResolveAnchoredStatements의 재사용 가드가 그때만 작동하므로, 가드가
            // 서수를 종류 없이 묶으면 INSERT 13이 UPDATE 13을 끌고 함께 버려진다.
            // 즉 이 테스트는 "INSERT를 후보로 되돌려도 UPDATE 서수 판정이 흔들리지
            // 않는다"를 두 방향에서 문다: INSERT가 후보에서 빠지면 INSERT 단언이,
            // 가드가 Kind를 잃으면 두 단언이 함께 죽는다.
            var facts = FactsSharingOrdinal(
                13,
                ("UPDATE", "-13", "TSettleMst", "YMD"),
                ("INSERT", "-8", "TSettleSum", "UseState"));

            var markdown = "### S07 단계\n\n```sql\n" +
                "SET @v_currentStepId = -8;\n" +
                "INSERT INTO dbo.TSettleSum (YMD) SELECT S.YMD FROM dbo.TSettleMst AS S;\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst", "dbo.TSettleSum" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("UPDATE 13(갱신 13) 문장에") && e.Contains("YMD"));
            Assert.Contains(result.Errors, e => e.Contains("INSERT 13 문장에") && e.Contains("UseState"));
        }

        [Fact]
        public void ValidateBatchStep_CheckB_NonUpdateKind_OmitsUpdateGloss()
        {
            // "(갱신 N)"은 명세서의 UPDATE 갱신 절 표를 가리키는 말이다. DELETE·INSERT에는
            // 그 표가 없다(명세서 전체에서 `(삽입 N`·`(삭제 N`은 0건 - SpecSetTarget 문서).
            var facts = FactsWithDeleteRow(3, new[] { "YMD" });

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- DELETE 3\n" +
                "DELETE A FROM dbo.TSettleMst AS A;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
            Assert.Contains("DELETE 3 문장에", error);
            Assert.DoesNotContain("갱신 3", error);
        }

        [Fact]
        public void ValidateBatchStep_CheckB_UpdateKind_KeepsUpdateGloss()
        {
            var facts = FactsWithCode(13, new[] { "YMD" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U13\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
            Assert.Contains("UPDATE 13(갱신 13) 문장에", error);
        }

        // ─────────────────────────────────────────────────────────────────────
        // 하위 스코프 이전 — 원본이 최상위에 두었던 술어를 이행이 CTE·파생
        // 테이블·EXISTS로 옮긴다. 그 컬럼은 없어진 것이 아니라 옮겨간 것이므로
        // 검사 B가 발화하면 거짓양성이다(2026-08-26 표본 판정 30건).
        //
        // 컬럼 단위로 거른다 - 전부-접기가 아니다. 하나는 이전이고 하나는 진짜
        // 소실이면 소실만 발화해야 한다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_CheckB_PredicateRelocatedIntoCte_IsSilent()
        {
            var facts = FactsWithCode(13, new[] { "YMD", "PGName" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U13\n" +
                ";WITH CardCost AS (\n" +
                "    SELECT A.PLTID, A.ID FROM dbo.TSettleMst AS A\n" +
                "    WHERE A.YMD = @p AND A.PGName = 'PLCard'\n" +
                ")\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y\n" +
                "INNER JOIN CardCost AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("갱신 13"));
        }

        // 전부-접기 구현은 이 테스트에 죽는다.
        [Fact]
        public void ValidateBatchStep_CheckB_OneRelocatedOneMissing_ReportsOnlyTheMissing()
        {
            var facts = FactsWithCode(13, new[] { "YMD", "PGName" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U13\n" +
                ";WITH CardCost AS (\n" +
                "    SELECT A.PLTID, A.ID FROM dbo.TSettleMst AS A\n" +
                "    WHERE A.YMD = @p\n" +
                ")\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y\n" +
                "INNER JOIN CardCost AS X ON X.PLTID = Y.PLTID AND X.ID = Y.ID;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            var error = Assert.Single(result.Errors, e => e.Contains("갱신 13"));

            // 메시지 뒷부분은 명세서 행의 값을 그대로 인용하므로 YMD가 정당하게
            // 나온다. 보고된 "빠진 컬럼" 목록만 잘라서 본다.
            var reported = error[..error.IndexOf("이(가) 없습니다", StringComparison.Ordinal)];
            Assert.Contains("PGName", reported);
            Assert.DoesNotContain("YMD", reported);
        }

        [Fact]
        public void ValidateBatchStep_CheckB_NoSubordinateScope_StillReportsMissing()
        {
            var facts = FactsWithCode(13, new[] { "YMD" }, code: null);

            // 최상위 WHERE를 아예 없앤다 - 남겨두면(예: `WHERE Y.UseState = 0`)
            // 명세서에 없는 최상위 술어가 생겨 검사 C(CheckAnchoredStatementExtras,
            // 무관한 별도 검사)가 "갱신 13"·"YMD"를 함께 담은 별개 메시지를 내고,
            // 그 메시지가 아래 단언을 우연히 통과시킨다 - 검사 B가 실제로 발화했는지는
            // 증명하지 못한 채로.
            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U13\n" +
                "UPDATE Y SET Y.CLCOMM = 1 FROM dbo.TSettleMst AS Y;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            // 검사 B의 라벨("최상위 WHERE 술어 컬럼")로 좁혀서 단 하나만 잡히는지
            // 본다 - 검사 C는 같은 라벨을 쓰지 않으므로 둘 이상 잡히면 그 자체가
            // 실패다.
            var error = Assert.Single(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
            var reported = error[..error.IndexOf("이(가) 없습니다", StringComparison.Ordinal)];
            Assert.Contains("YMD", reported);
        }

        [Fact]
        public void ValidateBatchStep_CheckB_UAnchorOnly_UsesUAnchor()
        {
            // 판정표 1행: U-앵커 있음·코드 앵커 없음 → U-앵커 사용(기존 동작 보존).
            var facts = FactsWithCode(13, new[] { "YMD" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "-- U13\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("갱신 13") && e.Contains("YMD"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 코드 앵커 재사용 가드 — 한 서수를 둘 이상의 문장이 주장하면 침묵한다.
        //
        // [왜 필요한가 - 2026-08-26 코퍼스 실측]
        // AiService의 [Precise Error Tracking]은 문장마다 고유한
        // `SET @v_currentStepId`를 요구하지만 실물은 한 코드를 여러 문장에 붙인다 -
        // 326개 단계 중 100개(음수 코드로 좁히면 81개, 25%)가 그렇고,
        // POQSettleBatch1/S10은 `-2`를 INSERT 하나와 UPDATE 둘, 세 문장에 붙였다.
        // ResolveOrdinal은 문장 단위 함수라 중복을 볼 수 없다(그게 정상이다 -
        // 모호성은 집합 수준의 성질이다). 그 결과 같은 Kind의 문장 둘이 모두 같은
        // 서수로 환산돼 엉뚱한 행과 대조됐다. 스윕 발화 109건 중 87건(80%)이
        // 그 자리에서 났다.
        //
        // 「귀속할 수 없으면 침묵한다」는 이 저장소의 규약을 그대로 따른다.
        // 대가는 커버리지다 - 중복 코드 단계에 진짜 결함이 있어도 함께 침묵한다.
        // 그 대가는 스윕 보고서의 지표가 드러낸다.
        // ─────────────────────────────────────────────────────────────────────

        private const string TwoStatementsSharingOneCode =
            "### S10 단계\n\n```sql\n" +
            "SET @v_currentStepId = -13;\n" +
            "UPDATE A SET A.CLComm = A.CLComm * -1 FROM dbo.TSettleMst AS A;\n" +
            "SET @v_currentStepId = -13;\n" +
            "UPDATE A SET A.CLTotal = A.CLComm + A.CLVT FROM dbo.TSettleMst AS A;\n" +
            "```\n";

        /// <summary>
        /// <b>같은 서수를 서로 다른 종류가 함께 쓰는</b> 명세서 재료. 서수는 문장
        /// 종류별로 1부터 다시 시작하므로 이런 표가 정상이다 - 실물
        /// `dbo.UP_Util_Settle_Summary`의 명세서가 DELETE 1~4와 INSERT 1~4를 둘 다
        /// 갖고 오류 코드 -1~-4/-5~-8로 가른다.
        /// </summary>
        private static IReadOnlyDictionary<string, SpecStatementFacts> FactsSharingOrdinal(
            int ordinal,
            (string Kind, string Code, string TargetTable, string PredicateColumn) first,
            (string Kind, string Code, string TargetTable, string PredicateColumn) second)
        {
            var facts = new SpecStatementFacts(
                new[]
                {
                    new SpecDmlRow(first.Kind, ordinal, ordinal * 10, first.TargetTable,
                        new[] { first.PredicateColumn }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()),
                    new SpecDmlRow(second.Kind, ordinal, ordinal * 10 + 5, second.TargetTable,
                        new[] { second.PredicateColumn }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>())
                },
                Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            {
                ErrorCodeToOrdinal = new Dictionary<string, (string, int)>
                {
                    [first.Code] = (first.Kind, ordinal),
                    [second.Code] = (second.Kind, ordinal)
                }
            };

            return new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = facts
            };
        }

        [Fact]
        public void ValidateBatchStep_CheckB_SameOrdinalDifferentKinds_BothReport()
        {
            // 서수는 문장 종류별로 1부터 다시 시작한다 - 명세서 DML 범위 표의
            // `DELETE 4`와 `INSERT 4`는 서로 다른 행이다. 재사용 가드가 Ordinal만으로
            // 묶으면 이 둘이 한 그룹이 되어 "한 서수를 둘이 주장한다"로 오인하고 둘 다
            // 조용히 버린다. 실측(2026-08-26): INSERT 재편입 스윕에서
            // `POQSettleProc1/S11`·`POQSettleProc9/S13`의 `DELETE 4 · OUTSTATE` 발화
            // 둘이 이 충돌로 사라졌다.
            var facts = FactsSharingOrdinal(
                4,
                ("DELETE", "-4", "TSettleMst", "OutState"),
                ("INSERT", "-8", "TSettleSum", "UseState"));

            var markdown = "### S11 단계\n\n```sql\n" +
                "SET @v_currentStepId = -4;\n" +
                "DELETE A FROM dbo.TSettleMst AS A;\n" +
                "SET @v_currentStepId = -8;\n" +
                "INSERT INTO dbo.TSettleSum (YMD)\n" +
                "SELECT S.YMD FROM dbo.TSettleMst AS S;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S11"), new[] { "dbo.TSettleMst", "dbo.TSettleSum" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("DELETE 4 문장에") && e.Contains("OutState"));
            Assert.Contains(result.Errors, e => e.Contains("INSERT 4 문장에") && e.Contains("UseState"));
        }

        [Fact]
        public void ValidateBatchStep_CheckB_CodeAnchorClaimedByTwoStatements_IsSilent()
        {
            var facts = FactsWithCode(9, new[] { "YMD", "UseState" }, code: "-13");

            var result = new MechanicalValidator().ValidateBatchStep(
                TwoStatementsSharingOneCode, LegacyStep("S10"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("갱신 9"));
        }

        [Fact]
        public void ValidateBatchStep_CheckC_CodeAnchorClaimedByTwoStatements_IsSilent()
        {
            // 검사 C는 "명세서에 없는 술어"를 본다. 명세서 UPDATE 9에 술어가 없고
            // 두 문장이 WHERE를 쓰면 원래는 초과 술어로 발화한다 - 가드가 그것도 막는다.
            var facts = FactsWithCode(9, Array.Empty<string>(), code: "-13");

            var markdown = "### S10 단계\n\n```sql\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE A SET A.CLComm = 1 FROM dbo.TSettleMst AS A WHERE A.UseState = 0;\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE A SET A.CLVT = 2 FROM dbo.TSettleMst AS A WHERE A.UseState = 1;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S10"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("명세서에 없는"));
        }

        [Fact]
        public void ValidateBatchStep_CheckB_CodeAnchorOnOneStatement_StillFires()
        {
            // 가드가 코드 앵커 축을 통째로 끄지 않았음을 못 박는다. 이 테스트가 없으면
            // "항상 침묵한다"는 구현이 위 두 테스트를 통과시킨다.
            var facts = FactsWithCode(9, new[] { "YMD", "UseState" }, code: "-13");

            var markdown = "### S10 단계\n\n```sql\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE A SET A.CLComm = A.CLComm * -1 FROM dbo.TSettleMst AS A;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S10"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("갱신 9") && e.Contains("YMD"));
        }

        [Fact]
        public void ValidateBatchStep_CheckB_CodeAnchorOnly_ResolvesOrdinalAndCompares()
        {
            // 판정표 2행: U-앵커 없음·코드 앵커 있음 → 코드 앵커를 환산해 사용.
            // `-- 원본 오류코드 -9` 주석은 LegacyStep의 목차 ErrorCodes(-9) 전사
            // 대조(검사 A와 무관한 별개 검사)를 만족시키기 위한 것으로, 주석이라
            // ReadCodeAnchor·ReadAnchor 둘 다 실코드로 보지 않는다.
            var facts = FactsWithCode(9, new[] { "YMD", "UseState" }, code: "-13");

            var markdown = "### S11 단계\n\n```sql\n" +
                "-- 원본 오류코드 -9\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S11"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("갱신 9") && e.Contains("YMD"));
        }

        [Fact]
        public void ValidateBatchStep_CheckB_BothAnchorsAgree_UsesResolvedOrdinal()
        {
            // 판정표 3행: 둘 다 있고 일치 → 사용.
            var facts = FactsWithCode(13, new[] { "YMD", "PGNAME" }, code: "-13");

            var markdown = "### S07 단계\n\n```sql\n" +
                "/* U13: 카드사 원가 반영 */\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("갱신 13") && e.Contains("YMD") && e.Contains("PGNAME"));
        }

        [Fact]
        public void ValidateBatchStep_CheckB_ConflictingAnchors_StaysSilent()
        {
            // 판정표 4행: 둘 다 있고 불일치 → 침묵(그 문장을 후보에서 뺀다).
            // U-앵커는 4를 가리키는데 코드 앵커(-13)는 명세서에서 9로 환산된다.
            // `-9` 주석은 목차 ErrorCodes 전사 대조를 만족시키기 위한 것(위 2행
            // 테스트와 같은 이유)이다.
            //
            // [픽스 라운드 1 - U-앵커가 가리키는 갱신 4 행도 명세서에 함께 둔다]
            // 이 행이 없으면 "U-앵커 우선"으로 잘못 뮤테이션해도(불일치 시
            // statement.Anchor를 그대로 반환) 갱신 4로 매칭되는 명세서 행이
            // 없어 candidates.Count != 1로 우연히 침묵한다 - 그러면 이 테스트는
            // "불일치 시 침묵" 규칙이 아니라 하위 매칭 가드 덕에 통과해 뮤테이션을
            // 못 잡는다. 갱신 4 행(ZZZ 요구)을 함께 둬서, U-앵커가 잘못 채택되면
            // 실제로 갱신 4와 대조돼 ZZZ 결측 오류가 나도록 만든다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("UPDATE", 4, 40, "TSettleMst",
                            new[] { "ZZZ" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("UPDATE", 9, 90, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
                {
                    ErrorCodeToOrdinal = new Dictionary<string, (string, int)> { ["-13"] = ("UPDATE", 9) }
                }
            };

            var markdown = "### S11 단계\n\n```sql\n" +
                "-- 원본 오류코드 -9\n" +
                "/* U4: 앵커는 4를 가리키는데 */\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S11"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            // 갱신 4(ZZZ)·갱신 9(YMD) 어느 쪽으로도 대조돼서는 안 된다 - 문장이
            // 후보에서 완전히 빠져야 한다. 문장 개수 대조(검사 A)는 이 픽스처가
            // 명세서 행 2개 대 마크다운 문장 1개로 어긋나 별도로 발화할 수 있으나
            // (이 테스트가 보는 것과 무관), 그 메시지는 컬럼 이름을 담지 않는다.
            Assert.DoesNotContain(result.Errors, e => e.Contains("ZZZ"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_CheckB_NoAnchors_StaysSilent()
        {
            // 판정표 5행: 둘 다 없음 → 후보 아님(기존 동작).
            var facts = FactsWithCode(1, new[] { "YMD" }, code: null);

            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE A SET A.CLCOMM = 1 FROM dbo.TSettleMst AS A;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_CheckB_CodeAnchorKindMismatch_IsIgnored()
        {
            // 환산 시 Kind도 대조해야 한다 - 코드 사전이 ("UPDATE", 9)를 주는데
            // 문장이 DELETE면 매칭이 아니다. U-앵커도 없으므로 결과는 판정표
            // 5행("둘 다 없음")과 같아야 한다.
            //
            // [픽스 라운드 1 - 같은 Ordinal에 다른 Kind 행을 함께 둔다]
            // 명세서에 UPDATE 9만 있으면 ResolveOrdinal 내부의 Kind 가드를
            // 지워도(문장이 DELETE인데 코드 사전의 UPDATE 9를 그대로 채택) 하위
            // 그룹핑(`r.Kind.Equals(group.Key.Kind, …)`)이 DELETE 9 행을 못 찾아
            // 우연히 침묵한다 - 그러면 이 테스트는 ResolveOrdinal의 Kind 가드가
            // 아니라 하위 매칭 가드 덕에 통과해 뮤테이션을 못 잡는다. DELETE 9
            // 행(ZZZ 요구)을 함께 둬서, Kind를 안 가리면 실제로 DELETE 9와
            // 대조돼 ZZZ 결측 오류가 나도록 만든다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("UPDATE", 9, 90, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("DELETE", 9, 91, "TSettleMst",
                            new[] { "ZZZ" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
                {
                    ErrorCodeToOrdinal = new Dictionary<string, (string, int)> { ["-13"] = ("UPDATE", 9) }
                }
            };

            var markdown = "### S11 단계\n\n```sql\n" +
                "SET @v_currentStepId = -13;\n" +
                "DELETE FROM dbo.TSettleMst WHERE PLTID = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S11"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("ZZZ"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_CheckC_CodeAnchorOnly_ResolvesOrdinalAndCompares()
        {
            // 검사 C도 같은 환산을 받는다 - U-앵커 없이 코드 앵커만으로 명세서에
            // 없는 최상위 술어 컬럼(TxAmt)을 잡아야 한다. `-9` 주석은 목차
            // ErrorCodes 전사 대조를 만족시키기 위한 것이다.
            var facts = FactsWithCode(9, new[] { "YMD" }, code: "-13");

            var markdown = "### S11 단계\n\n```sql\n" +
                "-- 원본 오류코드 -9\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A WHERE A.YMD = @p AND A.TxAmt = 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S11"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("TxAmt") && e.Contains("명세서에 없는"));
        }

        [Fact]
        public void ValidateBatchStep_CheckC_ConflictingAnchors_StaysSilent()
        {
            // 검사 C도 두 축이 불일치하면 침묵해야 한다.
            //
            // [픽스 라운드 1 - U-앵커가 가리키는 갱신 4 행도 함께 둔다]
            // 위 검사 B의 같은 이유(ValidateBatchStep_CheckB_ConflictingAnchors_
            // StaysSilent 참고) - 갱신 4 행이 없으면 "U-앵커 우선" 뮤테이션이
            // candidates.Count != 1로 우연히 침묵해 이 테스트가 그 뮤테이션을
            // 못 잡는다. 갱신 4 행은 TxAmt를 인정하지 않으므로, U-앵커가 잘못
            // 채택되면 실제로 갱신 4와 대조돼 "명세서에 없는" TxAmt 오류가 난다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    new[]
                    {
                        new SpecDmlRow("UPDATE", 4, 40, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                        new SpecDmlRow("UPDATE", 9, 90, "TSettleMst",
                            new[] { "YMD" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                    },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
                {
                    ErrorCodeToOrdinal = new Dictionary<string, (string, int)> { ["-13"] = ("UPDATE", 9) }
                }
            };

            var markdown = "### S11 단계\n\n```sql\n" +
                "/* U4: 앵커는 4를 가리키는데 */\n" +
                "SET @v_currentStepId = -13;\n" +
                "UPDATE A SET A.OutState = 2 FROM dbo.TSettleMst AS A WHERE A.TxAmt = 0;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S11"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("명세서에 없는"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 검사 D - 지역 변수 선언. S14 🔴: 지역 변수 9개가 DECLARE 없이 쓰였고
        // 그중 금액 3종이 원본 MONEY인데 변수명은 int를 시사한다.
        // ─────────────────────────────────────────────────────────────────────

        private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithVariables(
            params SpecLocalVariable[] variables) =>
            new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_EXCEPTION_PROC"] = new SpecStatementFacts(
                    Array.Empty<SpecDmlRow>(), Array.Empty<SpecSetTarget>(), variables)
            };

        [Fact]
        public void ValidateBatchStep_SpecVariableUsedWithoutDeclare_ShouldBeAnErrorWithItsType()
        {
            var markdown = "### S14 단계\n\n```sql\n" +
                "DECLARE @v_currentStepId INT = 0;\n" +
                "SET @v_intCLTotal = 100;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S14"), new[] { "dbo.TSettleMiss" },
                new Dictionary<string, SpecConditions>(), null, null,
                FactsWithVariables(new SpecLocalVariable("@v_intCLTotal", "MONEY", false)));

            Assert.Contains(result.Errors, e => e.Contains("@v_intCLTotal") && e.Contains("MONEY"));
        }

        [Fact]
        public void ValidateBatchStep_SystemValues_AreNotRequiredToBeDeclared()
        {
            var markdown = "### S14 단계\n\n```sql\nIF @@ERROR <> 0 RETURN -1;\n```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S14"), new[] { "dbo.TSettleMiss" },
                new Dictionary<string, SpecConditions>(), null, null,
                FactsWithVariables(new SpecLocalVariable("@@ERROR", "SQL Server 시스템 값", true)));

            Assert.DoesNotContain(result.Errors, e => e.Contains("@@ERROR"));
        }

        [Fact]
        public void ValidateBatchStep_VariableNotUsedByTheStep_IsSilent()
        {
            // 단계가 그 변수를 아예 쓰지 않으면 선언을 요구할 이유가 없다.
            var markdown = "### S14 단계\n\n```sql\nSELECT 1;\n```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S14"), new[] { "dbo.TSettleMiss" },
                new Dictionary<string, SpecConditions>(), null, null,
                FactsWithVariables(new SpecLocalVariable("@v_intCLTotal", "MONEY", false)));

            Assert.DoesNotContain(result.Errors, e => e.Contains("@v_intCLTotal"));
        }

        [Fact]
        public void ValidateBatchStep_SystemValueNotFlaggedButDoubleAtPrefixed_IsSilent()
        {
            // 실측: output/Procedures/dbo.UP_UTIL_SETTLE_EXPECT_PROC/docs/Spec.md의
            // 지역 변수 표는 헤더가 "데이터 타입"뿐이라("또는 구분"이 없다) 타입 칸을
            // 못 찾고, @@ERROR 행의 구분 칸도 "시스템 정수 값"이라 추출기의
            // SystemValueMarker("SQL Server 시스템 값")와 글자가 달라 IsSystemValue가
            // False로 나온다(실행 재현). 이 상태를 그대로 신뢰하면 SQL Server가 DECLARE를
            // 애초에 허락하지 않는 @@ERROR에 "선언하라"는 오류가 나 거짓 결함이 된다.
            // `@@` 접두사는 T-SQL 문법상 사용자가 DECLARE할 수 없는 시스템 전역값의
            // 표식이라 IsSystemValue 값과 무관하게 항상 안전하게 제외할 수 있다.
            var markdown = "### S14 단계\n\n```sql\nIF @@ERROR <> 0 RETURN -1;\n```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S14"), new[] { "dbo.TSettleMiss" },
                new Dictionary<string, SpecConditions>(), null, null,
                FactsWithVariables(new SpecLocalVariable("@@ERROR", string.Empty, false)));

            Assert.DoesNotContain(result.Errors, e => e.Contains("@@ERROR"));
        }

        [Fact]
        public void ValidateBatchStep_PrefixOverlappingVariableName_DoesNotFalselyMatch()
        {
            // `@v_int`가 명세서 표에 있어도 단계 SQL에는 `@v_intCLTotal`만 있으면
            // `@v_int`는 실제로 쓰이지 않은 것이다 - 접두사 겹침으로 오매치되면 안 된다.
            var markdown = "### S14 단계\n\n```sql\n" +
                "DECLARE @v_intCLTotal MONEY = 0;\n" +
                "SET @v_intCLTotal = 100;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S14"), new[] { "dbo.TSettleMiss" },
                new Dictionary<string, SpecConditions>(), null, null,
                FactsWithVariables(new SpecLocalVariable("@v_int", "INT", false)));

            Assert.DoesNotContain(result.Errors, e => e.Contains("`@v_int`"));
        }

        [Fact]
        public void ValidateBatchStep_MultipleUndeclaredVariables_EachReportedWithItsOwnType()
        {
            // S14 실물 모양: 여러 변수가 동시에 선언 없이 쓰인다. 금액 변수마다
            // 자기 타입(MONEY)이 실려야 이행자가 int로 잘못 선언하지 않는다.
            var markdown = "### S14 단계\n\n```sql\n" +
                "DECLARE @v_currentStepId INT = 0;\n" +
                "SET @v_intCLTotal = 100;\n" +
                "SET @v_intCLComm = 10;\n" +
                "SET @v_intCLVT = 1;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, LegacyStep("S14"), new[] { "dbo.TSettleMiss" },
                new Dictionary<string, SpecConditions>(), null, null,
                FactsWithVariables(
                    new SpecLocalVariable("@v_intCLTotal", "MONEY", false),
                    new SpecLocalVariable("@v_intCLComm", "MONEY", false),
                    new SpecLocalVariable("@v_intCLVT", "MONEY", false)));

            Assert.Contains(result.Errors, e => e.Contains("@v_intCLTotal") && e.Contains("MONEY"));
            Assert.Contains(result.Errors, e => e.Contains("@v_intCLComm") && e.Contains("MONEY"));
            Assert.Contains(result.Errors, e => e.Contains("@v_intCLVT") && e.Contains("MONEY"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 검사 E - 상태 변수 초기값. S13 🟠: @v_currentStepId INT = 0으로 시작하고
        // CATCH가 SET @po_intRetVal = @v_currentStepId를 무조건 수행해, DML 바깥에서
        // 난 장애(커서 DECLARE/OPEN, 행 0건)가 성공 코드 0으로 보고된다.
        // ─────────────────────────────────────────────────────────────────────

        private static BatchStepPlan StepWithCodes(string code, params string[] errorCodes) => new(
            Code: code, Name: $"{code} 단계",
            LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_SUMMARY_ETC" },
            TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleByOUT" },
            ErrorCodes: errorCodes, Chunkable: false, SchemaTables: Array.Empty<string>());

        [Fact]
        public void ValidateBatchStep_StatusVariableInitializedToSuccessCode_ShouldBeAnError()
        {
            var markdown = "### S13 단계\n\n```sql\n" +
                "DECLARE @v_currentStepId INT = 0;\n" +
                "BEGIN TRY\n  SET @v_currentStepId = 1001;\nEND TRY\n" +
                "BEGIN CATCH\n  SET @po_intRetVal = @v_currentStepId;\nEND CATCH\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, StepWithCodes("S13", "-9", "0", "1001", "1002"), new[] { "dbo.TSettleByOUT" },
                new Dictionary<string, SpecConditions>());

            Assert.Contains(result.Errors, e => e.Contains("@v_currentStepId") && e.Contains("0"));
        }

        [Fact]
        public void ValidateBatchStep_StatusVariableInitializedOutsideErrorCodeSet_IsSilent()
        {
            var markdown = "### S13 단계\n\n```sql\n" +
                "DECLARE @v_currentStepId INT = -999;\n" +
                "BEGIN CATCH\n  SET @po_intRetVal = @v_currentStepId;\nEND CATCH\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, StepWithCodes("S13", "-9", "0", "1001", "1002"), new[] { "dbo.TSettleByOUT" },
                new Dictionary<string, SpecConditions>());

            Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
        }

        [Fact]
        public void ValidateBatchStep_NoCatchReturnStructure_IsSilent()
        {
            var markdown = "### S13 단계\n\n```sql\nDECLARE @v_currentStepId INT = 0;\nSELECT 1;\n```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, StepWithCodes("S13", "0", "1001"), new[] { "dbo.TSettleByOUT" },
                new Dictionary<string, SpecConditions>());

            Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Task 17 I1 - 출처 없는 단언을 프롬프트에 싣는다.
        //
        // 예전 메시지("다만 이 저장소는 반환값이 0이면 목차 기재 여부와 무관하게
        // 무조건 성공으로 해석합니다")의 근거를 `src/`·`docs/`·`AGENTS.md`에서
        // 찾지 못했다 - 오히려 `AiService.cs:3212`가 정반대를 지시한다("명시적
        // SET 없이 헤더 주석만으로 0을 성공으로 가정하지 말라"). 판정에서 합성
        // "0"을 없애고, 목차 `ErrorCodes`에 실제로 있는 값과만 대조한다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ValidateBatchStep_SuccessCodeZeroNotDeclaredInPlan_IsSilent_NoSyntheticZeroAssumption()
        {
            // 실물 모양: POQSettleBatch1/S06 (ErrorCodes=["-9","-1"], DECLARE
            // @v_currentStepId INT = 0). "0"이 목차 오류 코드 집합에 없으므로,
            // 근거 없는 "0=성공" 가정을 걷어낸 뒤에는 이 초기값을 문제 삼을 근거가
            // 없다 - 침묵해야 한다.
            var markdown = "### S06 단계\n\n```sql\n" +
                "DECLARE @v_currentStepId INT = 0;\n" +
                "BEGIN CATCH\n  SET @po_intRetVal = @v_currentStepId;\nEND CATCH\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, StepWithCodes("S06", "-9", "-1"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>());

            Assert.DoesNotContain(result.Errors, e => e.Contains("@v_currentStepId"));
        }

        [Fact]
        public void ValidateBatchStep_StatusVariableMessage_NeverClaimsUnconditionalZeroSuccessInterpretation()
        {
            // S13처럼 "0"이 목차 ErrorCodes에 실제로 있는 경우조차, 근거 없는
            // 저장소 차원의 단언 문구는 다시는 나오면 안 된다.
            var markdown = "### S13 단계\n\n```sql\n" +
                "DECLARE @v_currentStepId INT = 0;\n" +
                "BEGIN CATCH\n  SET @po_intRetVal = @v_currentStepId;\nEND CATCH\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, StepWithCodes("S13", "-9", "0", "1001", "1002"), new[] { "dbo.TSettleByOUT" },
                new Dictionary<string, SpecConditions>());

            Assert.DoesNotContain(result.Errors, e => e.Contains("무조건 성공으로 해석"));
        }

        [Fact]
        public void ValidateBatchStep_NonZeroCatchAllInitialValue_MessageDoesNotClaimMaybeSuccessCode()
        {
            // 실물 모양: POQSettleProc10/S16 (ErrorCodes에 "4000" 포함, DECLARE
            // @v_currentStepId INT = 4000 - "4000"은 이 단계가 스스로 문서화한
            // 범용 catch-all 코드다). 이 값으로 보고되는 것 자체는 설계 의도와
            // 같으므로 "성공 코드일 수도 있습니다"라는 문구는 부정확하다.
            var markdown = "### S16 단계\n\n```sql\n" +
                "DECLARE @v_currentStepId INT = 4000;\n" +
                "BEGIN CATCH\n  SET @po_intRetVal = @v_currentStepId;\nEND CATCH\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, StepWithCodes("S16", "-1", "-3", "0", "4000"), new[] { "dbo.TSettleMiss" },
                new Dictionary<string, SpecConditions>());

            Assert.Contains(result.Errors, e => e.Contains("@v_currentStepId") && e.Contains("4000"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("성공 코드일 수도 있습니다"));
        }

        // ── Task 5: 트랜잭션 경계 · 변수 대입 표의 전사 대조 ──────────────────

        private static SpecExpectations TransactionExpectations() =>
            SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "P",
                DdlText = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END"
            })!;

        [Fact]
        public void Validate_TransactionBoundaryTableMissing_ShouldReport()
        {
            var markdown = WrapSpec("### SELECT 대상 테이블\n\n내용 없음\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }

        [Fact]
        public void Validate_TransactionBoundaryRowsPresent_ShouldNotReport()
        {
            var markdown = WrapSpec(
                "### SELECT 대상 테이블\n\n내용 없음\n\n"
                + TransactionBoundaryExtractor.TableHeading + "\n\n"
                + "| 라인 | 종류 | 이름 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 3 | BEGIN TRANSACTION | (없음) |\n"
                + "| 4 | COMMIT TRANSACTION | (없음) |\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }

        [Fact]
        public void Validate_TransactionBoundaryRowMissing_ShouldReportThatRow()
        {
            var markdown = WrapSpec(
                TransactionBoundaryExtractor.TableHeading + "\n\n"
                + "| 라인 | 종류 | 이름 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 3 | BEGIN TRANSACTION | (없음) |\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing
                     && e.Message.Contains("4"));
        }

        [Fact]
        public void Validate_TransactionTableFollowedByAnotherTable_ShouldNotBleedIntoIt()
        {
            // 작성 계약 4: 표 경계는 빈 줄이고, `|`로 시작하지 않는 임의의 줄도 표를
            // 끝낸다. 인접 표를 합치면 뒤 표 헤더가 앞 표 너비와 비교돼 거짓 오류가 난다
            // (2026-08-22 실측: 코퍼스 31개 중 9개에 거짓 양성 10건).
            var markdown = WrapSpec(
                TransactionBoundaryExtractor.TableHeading + "\n\n"
                + "| 라인 | 종류 | 이름 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 3 | BEGIN TRANSACTION | (없음) |\n"
                + "| 4 | COMMIT TRANSACTION | (없음) |\n"
                + "\n"
                + "보조 설명 한 줄.\n"
                + "\n"
                + "| 다른 표 | 칸 |\n"
                + "| :--- | :--- |\n"
                + "| x | y |\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }

        [Fact]
        public void Validate_IndentedTransactionHeading_ShouldStillBeFound()
        {
            // 작성 계약 5: 프롬프트는 헤딩을 3칸 들여써서 렌더한다. 모델이 그것을
            // 보존하는 회차가 오면 접두사 비교가 실패해 검사가 조용히 죽는다.
            var markdown = WrapSpec(
                "   " + TransactionBoundaryExtractor.TableHeading + "\n\n"
                + "| 라인 | 종류 | 이름 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 3 | BEGIN TRANSACTION | (없음) |\n"
                + "| 4 | COMMIT TRANSACTION | (없음) |\n");

            var result = new MechanicalValidator().Validate(markdown, TransactionExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }

        [Fact]
        public void Validate_NoTransactionMaterial_ShouldNotReportAnything()
        {
            // 작성 계약 1의 뒷면: null 체인을 넓혔으므로 이전에 L1을 안 받던 명세서가
            // 이제 모든 검사를 받는다. 자기 재료가 비면 조용히 넘어가야 한다.
            var expectations = SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "Q", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "Q",
                DdlText = @"CREATE PROCEDURE dbo.Q AS
BEGIN
    DECLARE @v INT
    SET @v = 1
END"
            });

            var result = new MechanicalValidator().Validate(WrapSpec("내용\n"), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.TransactionBoundaryTableMissing);
        }

        private static SpecExpectations SetAssignmentExpectations() =>
            SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "S", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "S",
                DdlText = @"CREATE PROCEDURE dbo.S AS
BEGIN
    DECLARE @v INT
    SET @v = 1
    SET @v = @v + 1
END"
            })!;

        [Fact]
        public void Validate_SetAssignmentTableMissing_ShouldReport()
        {
            var markdown = WrapSpec("### SELECT 대상 테이블\n\n내용 없음\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }

        [Fact]
        public void Validate_SetAssignmentRowsPresent_ShouldNotReport()
        {
            var markdown = WrapSpec(
                "### SELECT 대상 테이블\n\n내용 없음\n\n"
                + SetAssignmentExtractor.TableHeading + "\n\n"
                + "| 라인 | 변수 | 대입식 원문 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 4 | @v | 1 |\n"
                + "| 5 | @v | @v + 1 |\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }

        [Fact]
        public void Validate_SetAssignmentExpressionParaphrased_ShouldReportThatRow()
        {
            // 대입식을 말로 바꾸면 원문에서 찾을 수 없다. CheckCaseBranches가 조건
            // 원문까지 대조하는 것과 같은 강도다.
            var markdown = WrapSpec(
                SetAssignmentExtractor.TableHeading + "\n\n"
                + "| 라인 | 변수 | 대입식 원문 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 4 | @v | 1 |\n"
                + "| 5 | @v | 1씩 증가시킵니다 |\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.Contains(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing
                     && e.Message.Contains("5"));
        }

        [Fact]
        public void Validate_SetAssignmentTableFollowedByAnotherTable_ShouldNotBleedIntoIt()
        {
            // 작성 계약 4: 표 경계는 빈 줄이고 `|`로 시작하지 않는 임의의 줄도 표를
            // 끝낸다. 2026-08-22 실측: 인접 표를 합쳐 코퍼스 31개 중 9개에 거짓 양성 10건.
            var markdown = WrapSpec(
                SetAssignmentExtractor.TableHeading + "\n\n"
                + "| 라인 | 변수 | 대입식 원문 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 4 | @v | 1 |\n"
                + "| 5 | @v | @v + 1 |\n"
                + "\n"
                + "보조 설명 한 줄.\n"
                + "\n"
                + "| 다른 표 | 칸 |\n"
                + "| :--- | :--- |\n"
                + "| x | y |\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }

        [Fact]
        public void Validate_IndentedSetAssignmentHeading_ShouldStillBeFound()
        {
            // 작성 계약 5: 프롬프트는 헤딩을 3칸 들여쓴다.
            var markdown = WrapSpec(
                "   " + SetAssignmentExtractor.TableHeading + "\n\n"
                + "| 라인 | 변수 | 대입식 원문 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| 4 | @v | 1 |\n"
                + "| 5 | @v | @v + 1 |\n");

            var result = new MechanicalValidator().Validate(markdown, SetAssignmentExpectations());

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }

        [Fact]
        public void Validate_NoSetAssignmentMaterial_ShouldNotReportAnything()
        {
            // 작성 계약 1의 뒷면: 체인을 넓혔으므로 이전에 L1을 안 받던 명세서가 이제
            // 모든 검사를 받는다. 자기 재료가 비면 조용히 넘어가야 한다.
            var expectations = SpecExpectations.From(new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("DB", "dbo", "T", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "T",
                DdlText = @"CREATE PROCEDURE dbo.T AS
BEGIN
    BEGIN TRANSACTION
    COMMIT TRANSACTION
END"
            });

            var result = new MechanicalValidator().Validate(WrapSpec("내용\n"), expectations);

            Assert.DoesNotContain(
                result.DetailedErrors,
                e => e.Type == ErrorType.SetAssignmentTableMissing);
        }

        /// <summary>
        /// 4개 필수 위치 인자를 빈 컬렉션으로 채운 `SpecExpectations`. `ErrorCodes` 등
        /// `init` 전용 속성만 object initializer로 얹어 쓰기 위한 최소 뼈대다.
        /// </summary>
        private static SpecExpectations EmptySpecExpectations() =>
            new(
                new List<UpdateColumnExpectation>(),
                new Dictionary<string, IReadOnlySet<string>>(),
                new HashSet<string>(),
                new List<string>());

        [Fact]
        public void ErrorCodeTable_WhenSpecOmitsARow_ShouldReportMissing()
        {
            var expectations = EmptySpecExpectations() with
            {
                ErrorCodes = new[]
                {
                    new ErrorCodeFact("UPDATE", 1, "-1", "@po_intRetVal"),
                    new ErrorCodeFact("UPDATE", 2, "-2", "@po_intRetVal"),
                }
            };

            var markdown = WrapSpec(
                DmlScopeExtractor.ErrorCodeTableHeading + "\n\n"
                + "| 문장 | 오류 코드 | 설정 대상 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| UPDATE 1 | -1 | @po_intRetVal |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.ErrorCodeTableMissing);
        }

        [Fact]
        public void ErrorCodeTable_WhenTranscribedVerbatim_ShouldNotReport()
        {
            var expectations = EmptySpecExpectations() with
            {
                ErrorCodes = new[] { new ErrorCodeFact("UPDATE", 9, "-13", "@po_intRetVal") }
            };

            var markdown = WrapSpec(
                DmlScopeExtractor.ErrorCodeTableHeading + "\n\n"
                + "| 문장 | 오류 코드 | 설정 대상 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| UPDATE 9 | -13 | @po_intRetVal |\n");

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ErrorCodeTableMissing);
        }

        [Fact]
        public void ErrorCodeTable_WhenThereAreNoFacts_ShouldNotRequireTheTable()
        {
            // 오류 가드가 없는 SP는 표가 없는 것이 정상이다. 요구하면 만족 불가능한
            // 지시가 되어 재시도를 소진한다(2026-08-24 검사 A C1과 같은 부류).
            var expectations = EmptySpecExpectations();

            var result = new MechanicalValidator().Validate(WrapSpec("내용\n"), expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.ErrorCodeTableMissing);
        }
    }
}
