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
            Chunkable: false);

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
            var result = _validator.ValidateBatchStep(S10HealthySection, S10Plan());

            Assert.True(result.IsValid, string.Join(" / ", result.Errors));
            Assert.Null(result.SuggestedPromptFix);
        }

        [Fact]
        public void ValidateBatchStep_WithoutCodeBlock_Fails()
        {
            var result = _validator.ValidateBatchStep(S10CollapsedSection, S10Plan());

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
                new[] { "UP_UTIL_SETTLE_INS" }, new[] { "dbo.TSettleMst" }, new[] { "-1" }, false);

            var result = _validator.ValidateBatchStep(section, plan);

            Assert.True(result.IsValid, string.Join(" / ", result.Errors));
        }

        [Fact]
        public void ValidateBatchStep_WithErrorCodeSubstringOnly_Fails()
        {
            // -1을 요구하는데 본문에 -10만 있으면 실패해야 한다. 부분 문자열 대조로
            // 회귀하면 -1이 -10 안에서 걸려 이 검사가 통째로 무력해진다.
            var section = "### S08 회수일 산정\n\n대상은 TSettleMst이고 오류코드는 -10뿐이다.\n\n```sql\nSELECT 1;\n```";
            var plan = new BatchStepPlan("S08", "회수일 산정",
                new[] { "UP_UTIL_SETTLE_EXPECT_PROC" }, new[] { "dbo.TSettleMst" }, new[] { "-1" }, false);

            var result = _validator.ValidateBatchStep(section, plan);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("-1"));
        }

        [Fact]
        public void ValidateBatchStep_WithMissingTargetTable_Fails()
        {
            var section = "### S10 PG 회수 통계 생성\n\nTStatPGCollect만 적었다. 오류코드 -1.\n\n```sql\nSELECT 1;\n```";

            var result = _validator.ValidateBatchStep(section, S10Plan());

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("TSettleMst"));
        }

        [Fact]
        public void ValidateBatchStep_WithWrongHeading_Fails()
        {
            var section = "## S10 PG 회수 통계 생성\n\nTStatPGCollect와 TSettleMst. 오류코드 -1.\n\n```sql\nSELECT 1;\n```";

            var result = _validator.ValidateBatchStep(section, S10Plan());

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("헤딩"));
        }

        [Fact]
        public void ValidateBatchStep_WithHeadingMissingStepCode_Fails()
        {
            var section = "### PG 회수 통계 생성\n\nTStatPGCollect와 TSettleMst. 오류코드 -1.\n\n```sql\nSELECT 1;\n```";

            var result = _validator.ValidateBatchStep(section, S10Plan());

            Assert.False(result.IsValid);
        }

        [Fact]
        public void ValidateBatchStep_WithEmptyMarkdown_Fails()
        {
            var result = _validator.ValidateBatchStep("", S10Plan());

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

            var result = _validator.ValidateBatchStep(S10HealthySection, plan);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("ErrorCodes"));
        }

        [Fact]
        public void ValidateBatchStep_WithEmptyTargetTables_Fails()
        {
            var plan = S10Plan() with { TargetTables = Array.Empty<string>() };

            var result = _validator.ValidateBatchStep(S10HealthySection, plan);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("TargetTables"));
        }

        [Fact]
        public void ValidateBatchStep_WithBlankOnlyErrorCodes_Fails()
        {
            // 배열은 있는데 원소가 전부 공백이면 기존 루프가 continue로 전부
            // 건너뛰어 빈 배열과 똑같이 무실행이 된다.
            var plan = S10Plan() with { ErrorCodes = new[] { "", "  " } };

            var result = _validator.ValidateBatchStep(S10HealthySection, plan);

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

            var result = _validator.ValidateBatchStep(S10HealthySection, plan);

            Assert.False(result.IsValid);
            Assert.False(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_WithBodyDefect_IsFixableByRegeneration()
        {
            // 본문에 코드 블록이 없는 것은 재생성으로 고쳐진다 - 기존 재시도가
            // 존재하는 이유이고, 그 경로가 살아 있어야 한다.
            var result = _validator.ValidateBatchStep(S10CollapsedSection, S10Plan());

            Assert.False(result.IsValid);
            Assert.True(result.RegenerationCanFix);
        }

        [Fact]
        public void ValidateBatchStep_WithBothDefects_IsFixableByRegeneration()
        {
            // 목차 결함이 섞였다고 본문 결함까지 포기하지 않는다. 하나라도
            // 재생성으로 고칠 수 있으면 재시도할 값어치가 있다.
            var plan = S10Plan() with { ErrorCodes = Array.Empty<string>() };

            var result = _validator.ValidateBatchStep(S10CollapsedSection, plan);

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

            var result = _validator.ValidateBatchStep(S10HealthySection, plan);

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
                _validator.ValidateBatchStep(S10HealthySection, plan);
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

            var result = _validator.ValidateBatchStep(S10HealthySection, plan);

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

            var result = _validator.ValidateBatchStep(S10HealthySection, plan);

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
                _validator.ValidateBatchStep(S10HealthySection, plan);
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
            return SpecExpectations.FromStaticAnalysis(analysis)!;
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
    }
}
