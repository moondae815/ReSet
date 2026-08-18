using System;
using System.Collections.Generic;
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

            var result = _validator.ValidateBatchStep(S10HealthySection, plan, Array.Empty<string>(), NoConditions);

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
            // 무관하게 훑으므로, 앵커를 나르는 이 비실행 주석은 SELECT 아래에
            // 두어도 그대로 잡힌다 - 이 테스트의 본 주제는 그대로 살아 있다.
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT ID FROM dbo.T
    WHERE  ID > 0
    --AND ClientID NOT IN (SELECT ClientID FROM dbo.UF_GET_CLIENTID4TMONET()) --예외처리 제거(2021.11.29)
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.Empty(expectations!.DmlScopeFacts);
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
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" })
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
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" }),
                    new DmlScopeFact("UPDATE", 331, "A", new[] { "YMD" }, true, Array.Empty<string>())
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
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" })
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
                    new DmlScopeFact("UPDATE", 227, "A", new[] { "UseState" }, false, new[] { "PLTID" })
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
            // 파생 테이블을 UPDATE...FROM이 아니라 단순 SELECT...FROM에 둔다 -
            // UPDATE/DELETE를 쓰면 DmlScopeExtractor도 동시에 사실을 만들어 내어
            // (dmlScopeFacts.Count == 0) 조건이 이미 false가 되고, derivedColumns
            // 항을 조기 반환식에서 지워도 이 테스트가 여전히 통과하는 거짓
            // 안전망이 된다 - 신호를 하나만 남겨야 그 신호의 배선만 증명한다.
            var sp = new SpDefinition
            {
                DdlText = @"
CREATE PROCEDURE dbo.P AS
BEGIN
    SELECT X.PGCOMM
    FROM   (SELECT PLTID,
                   IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt) AS PGCOMM
            FROM   dbo.TSettleMst A) X
END"
            };

            var expectations = SpecExpectations.From(sp);

            Assert.NotNull(expectations);
            Assert.Empty(expectations!.DmlScopeFacts);
            var def = Assert.Single(expectations.DerivedColumns, d => d.Column == "PGCOMM");
            Assert.Contains("DiscountFlag", def.Anchors);
        }

        // Column은 한정자를 포함한 원문 표기다(DmlScopeExtractor.SetPredicateFact
        // 문서 참고) - 실측 DDL의 `A.PGName NOT IN (...)`을 그대로 반영해 "A.PGName"으로
        // 둔다. 마지막 식별자 조각만 담으면 코퍼스에서 키 충돌이 실제로 난다.
        private static SetPredicateFact NineePgFact() => new(
            "UPDATE", 39, "A.PGName", true,
            new[]
            {
                "'PLCard'", "'SamSungPay'", "'SSGPayCard'", "'KakaoPay'", "'KakaoCard'",
                "'impaymobile'", "'NaverCard'", "'ApplePay'", "'TossCardAuth'"
            });

        private static string SetPredicateSection(string literalCell) =>
            "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
            + "| 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |\n"
            + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
            + $"| UPDATE 1 | 39 | A.PGName | NOT IN | 9 | {literalCell} |\n";

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
            var fact = new SetPredicateFact("UPDATE", 108, "UseState", false, new[] { "0", "1" });
            var expectations = EmptyExpectations() with { SetPredicates = new[] { fact } };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 108 | UseState | IN | 2 | (생략) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
        }

        [Fact]
        public void Validate_SetPredicateRowKeyedByLineAndColumn_ShouldDistinguishTwoInsOnOneStatement()
        {
            // 한 문장에 IN이 둘이면 라인만으로는 행을 특정할 수 없다.
            var facts = new[]
            {
                new SetPredicateFact("UPDATE", 30, "PGName", false, new[] { "'A'", "'B'" }),
                new SetPredicateFact("UPDATE", 30, "UseState", false, new[] { "0", "1" })
            };
            var expectations = EmptyExpectations() with { SetPredicates = facts };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 30 | PGName | IN | 2 | 'A', 'B' |\n";

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
                new SetPredicateFact("UPDATE", 50, "A.X", false, new[] { "1" }),
                new SetPredicateFact("UPDATE", 50, "A.X", false, new[] { "2" })
            };
            var expectations = EmptyExpectations() with { SetPredicates = facts };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 50 | A.X | IN | 1 | 1 |\n";

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
                new SetPredicateFact("UPDATE", 50, "A.X", false, new[] { "1" }),
                new SetPredicateFact("UPDATE", 50, "A.X", false, new[] { "2" })
            };
            var expectations = EmptyExpectations() with { SetPredicates = facts };
            var markdown = RequiredHeadersMarkdown()
                + "\n" + DmlScopeExtractor.SetPredicateTableHeading + "\n"
                + "| 문장 | 라인 | 컬럼 | 연산 | 원소 수 | 리터럴 목록 |\n"
                + "| :--- | :--- | :--- | :--- | :--- | :--- |\n"
                + "| UPDATE 1 | 50 | A.X | IN | 1 | 2 |\n"
                + "| UPDATE 2 | 50 | A.X | IN | 1 | 1 |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SetPredicateMismatch);
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
    }
}
