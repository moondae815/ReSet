using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「검사 하나가 던져도, 다른 검사가 <b>이미 찾은</b> 결함은 살아남는가.」
    ///
    /// [무엇이 결함이었나 - 2026-09-10 감사 [4]] <c>Validate</c>·<c>ValidateConsolidated</c>의
    /// catch-all이 <c>Errors.Clear()</c> + <c>DetailedErrors.Clear()</c> + <c>IsValid = true</c>
    /// 였다. 검사 셋째가 던지면 첫째·둘째가 이미 찾은 결함까지 지우고 통과한다 -
    /// 「조용히 통과」가 아니라 <b>「찾은 것을 지우고 통과」</b>다.
    ///
    /// [왜 주입인가 - 434회 실측] 이 파급을 실물 데이터로는 못 잰다:
    ///   실행 로그 2,549,970줄(40파일·38일)에서 catch-all 발동 0 · 자기 가드 발동 0
    ///   배송 코퍼스 31편 × 던지게 만들려는 변형 14가지 = 434회에서 발동 0
    /// 완전히 잠재라 「지금 던지는 자리」가 없다. 그래서 <c>SafeCheck</c>를 열어 두고
    /// 시험이 한 검사만 던지게 만든다.
    ///
    /// [★ 오라클을 IsValid로 두지 마라] <c>IsValid</c>는 catch-all이 <b>직접 조작하는</b>
    /// 값이다. 그것을 기준으로 삼으면 검사가 자기 자신을 채점한다. 기준은
    /// <b>「앞선 검사가 넣은 구체적 발화 문자열이 <c>Errors</c>에 살아 있는가」</b>다.
    /// 사전선언: <c>docs/audit-reports/2026-09-10-L1-예외격리-사전선언.md</c> §3.
    /// </summary>
    public class CheckIsolationTests
    {
        /// <summary>필수 H2 다섯 중 셋이 빠져 <c>ValidateMarkdownStructure</c>가 반드시 발화하는 문서.</summary>
        private const string MissingRequiredHeaders = "## 개요\n내용\n\n## 파라미터 목록\n내용\n";

        /// <summary>
        /// <c>SafeCheck</c>를 가로채는 시험용 파생 타입.
        ///
        /// <paramref name="throwAt"/>에 이름이 걸리는 검사는 <c>SafeCheck</c> <b>바깥</b>에서
        /// 던진다 - 그러면 예외가 <c>Validate</c>의 catch-all까지 그대로 올라간다.
        /// 감사 [4]가 말한 경로가 정확히 그것이다.
        /// </summary>
        private sealed class Probe : MechanicalValidator
        {
            private readonly string? _throwOutsideAt;
            private readonly string? _throwInsideAt;

            /// <param name="throwOutsideAt">
            /// 이름이 걸리면 <c>SafeCheck</c> <b>바깥</b>에서 던진다 - <b>감싸지 않은 호출</b>이
            /// 던지는 상황이다. <c>Validate</c>에서는 그 예외가 catch-all까지 올라간다.
            /// </param>
            /// <param name="throwInsideAt">
            /// 이름이 걸리면 검사 <b>안</b>에서 던진다 - <b>감싼 호출</b>의 검사가 던지는
            /// 상황이다. <c>SafeCheck</c>가 삼켜야 하고 나머지 검사는 계속 돌아야 한다.
            /// </param>
            public Probe(string? throwOutsideAt = null, string? throwInsideAt = null)
            {
                _throwOutsideAt = throwOutsideAt;
                _throwInsideAt = throwInsideAt;
            }

            public List<string?> SeenCheckExpressions { get; } = new();

            /// <summary>
            /// <c>SafeCheck</c>가 <c>protected</c>라 시험이 직접 못 부른다. 동작을 안 바꾸는
            /// 통로만 연다 - 여기서 이름을 재면 <b>파생 타입의 override</b>가 기준이 되어
            /// 순환이므로, 이름은 제품 경로에서만 잰다.
            /// </summary>
            public void RunThroughSafeCheck(Action work) => SafeCheck(work);

            protected override void SafeCheck(Action check, string? checkExpression = null)
            {
                SeenCheckExpressions.Add(checkExpression);

                if (Matches(_throwOutsideAt, checkExpression))
                {
                    throw new InvalidOperationException("주입 - 감싸지 않은 호출이 던진다: " + _throwOutsideAt);
                }

                if (Matches(_throwInsideAt, checkExpression))
                {
                    base.SafeCheck(
                        () => throw new InvalidOperationException("주입 - 검사가 던진다: " + _throwInsideAt),
                        checkExpression);
                    return;
                }

                base.SafeCheck(check, checkExpression);
            }

            private static bool Matches(string? needle, string? expression) =>
                needle is not null && expression is not null
                && expression.Contains(needle, StringComparison.Ordinal);
        }

        [Fact]
        public void Validate_WithoutAnyInjection_ReportsTheMissingHeaders()
        {
            // 정박 - 아래 두 시험의 전건이다. 이 발화가 사라지면 두 시험이 무엇을 재는지
            // 모른 채 초록이 된다(폭발 반경 안의 탐지기가 되는 것을 막는다).
            var result = new MechanicalValidator().Validate(MissingRequiredHeaders);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("CRUD 분석", StringComparison.Ordinal));
        }

        [Fact]
        public void Validate_WhenALaterCheckThrows_TheEarlierFindingSurvives()
        {
            // ValidateMarkdownStructure(첫째)가 발화한 뒤 CheckMachineTableShape(셋째)가
            // 던진다. 종전 코드에서는 catch-all이 첫째의 발화까지 지우고 IsValid = true로
            // 통과시켰다.
            var result = new Probe(throwOutsideAt: "CheckMachineTableShape").Validate(MissingRequiredHeaders);

            Assert.Contains(result.Errors, e => e.Contains("CRUD 분석", StringComparison.Ordinal));
            Assert.False(result.IsValid);
        }

        [Fact]
        public void Validate_WhenALaterCheckThrows_DetailedErrorsSurviveToo()
        {
            // DetailedErrors는 RegenerationScopeSelector.FromL1Errors와
            // BuildSuggestedPromptFix가 소비해 **재생성 범위와 프롬프트 처방**을 정한다.
            // Errors만 살리고 이쪽을 지우면 재생성이 무엇을 고쳐야 하는지 모른 채 돈다.
            var result = new Probe(throwOutsideAt: "CheckMachineTableShape").Validate(MissingRequiredHeaders);

            Assert.NotEmpty(result.DetailedErrors);
        }

        [Fact]
        public void SafeCheck_WhenTheCheckItselfThrows_OnlyThatCheckDies()
        {
            // 이쪽은 A(호출부 SafeCheck)가 사는 자리다. 검사가 SafeCheck **안에서** 던지면
            // catch-all까지 가지 않고 그 검사만 죽는다.
            var probe = new Probe();
            var result = new ValidationResult();
            var ran = 0;

            probe.RunThroughSafeCheck(() => { ran++; result.Report("첫째 발화"); });
            probe.RunThroughSafeCheck(() => { ran++; throw new InvalidOperationException("둘째가 던진다"); });
            probe.RunThroughSafeCheck(() => { ran++; result.Report("셋째 발화"); });

            Assert.Equal(3, ran);
            Assert.Equal(new[] { "첫째 발화", "셋째 발화" }, result.Errors);
        }

        [Fact]
        public void Validate_NamesEveryCheckItRunsThroughSafeCheck()
        {
            // [부작용 축] 종전 문구는 "단계 검사 하나가 실패해 건너뜁니다."로 **어느 검사가
            // 죽었는지 안 남겼다**. 감싸는 자리를 21에서 44로 넓히면서 그대로 두면
            // 「찾은 것을 지우고 통과」를 「어느 것이 안 돌았는지 모른 채 통과」로 옮기는
            // 것뿐이다 - 값 0을 게이트 통과시키는 그 모양이다.
            //
            // 기대값은 시험이 지어낸 것이 아니라 **컴파일러가 Validate의 호출 자리에서 뜬
            // 원문**이다(CallerArgumentExpression). 그래서 이 단언은 제품 배선이 실제로
            // 그 모양일 때만 통과한다 - 파생 타입의 override에서 재면 순환이라 안 잰다.
            var probe = new Probe();

            probe.Validate(MissingRequiredHeaders);

            Assert.Equal(
                "() => ValidateMarkdownStructure(cleansed, RequiredHeaders, result)",
                probe.SeenCheckExpressions[0]);
            Assert.All(probe.SeenCheckExpressions, e => Assert.NotNull(e));
        }

        [Fact]
        public void Validate_WithoutExpectations_RunsExactlyTheFourUnconditionalChecks()
        {
            // expectations가 null이면 조건부 27자리는 안 돈다. 이 수가 흔들리면 위
            // 이름 잠금이 어느 자리를 재는지 모르게 된다.
            //
            // 4: 2026-09-10 에 CheckDocumentInstructsItsAuthor 가 더해졌다. 그 검사는
            // expectations를 안 받으므로 무조건 검사 쪽에 선다 - 이 수가 그 배선을
            // 재는 자다(스윕의 「발화 11」은 검사 로직을 재지 배선을 재지 않는다).
            var probe = new Probe();

            probe.Validate(MissingRequiredHeaders);

            Assert.Equal(4, probe.SeenCheckExpressions.Count);
        }

        [Fact]
        public void Validate_WhenAnEarlyCheckThrowsInside_TheLaterChecksStillRun()
        {
            // 감싸기가 사 주는 것은 「다음 검사가 계속 돈다」이다 - B(지우지 않기)만으로는
            // 못 산다. 첫째가 안에서 던져도 셋 다 돌아야 한다.
            var probe = new Probe(throwInsideAt: "ValidateMarkdownStructure");

            probe.Validate(MissingRequiredHeaders);

            Assert.Equal(4, probe.SeenCheckExpressions.Count);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ValidateConsolidated — Validate 와 같은 catch-all 을 가졌다.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>필수 통합 H2 가 빠져 <c>ValidateMarkdownStructure</c>가 반드시 발화하는 계획서.</summary>
        private const string ConsolidatedMissingHeaders = "## 개요\n내용\n";

        [Fact]
        public void ValidateConsolidated_WithoutAnyInjection_ReportsTheMissingHeaders()
        {
            // 정박 - 아래 시험의 전건.
            var result = new MechanicalValidator().ValidateConsolidated(ConsolidatedMissingHeaders);

            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Errors);
        }

        [Fact]
        public void ValidateConsolidated_WhenALaterCheckThrows_TheEarlierFindingSurvives()
        {
            // 종전에 이 메서드의 넷만 SafeCheck 밖이었고, 바로 아래 주석이 그 위험을
            // 명시하면서 그 위의 넷을 안 감쌌다.
            var before = new MechanicalValidator().ValidateConsolidated(ConsolidatedMissingHeaders).Errors.Count;

            var result = new Probe(throwOutsideAt: "CheckControlTotalProducer")
                .ValidateConsolidated(ConsolidatedMissingHeaders);

            Assert.True(result.Errors.Count >= before,
                $"주입 전 {before} 건이었는데 주입 후 {result.Errors.Count} 건이다 - 지워졌다.");
            Assert.False(result.IsValid);
        }

        [Fact]
        public void ValidateConsolidated_RunsEveryCheckThroughSafeCheck()
        {
            // [되돌림 판독 2026-09-10] 이 시험이 없을 때 이 메서드의 감싸기를 통째로
            // 걷어내 보니 **빨개지는 시험이 0** 이었다 - 아홉 자리가 시험 없는 가지였다.
            // 위 「먼저 찾은 것이 살아남는가」는 SafeCheck 바깥 주입이라 감싸든 말든
            // 같은 결과를 낸다. 감싸기 자체를 재는 자는 이 시험이다.
            var probe = new Probe();

            probe.ValidateConsolidated(ConsolidatedMissingHeaders);

            // 2026-09-14 CheckGateRequiresStepsBeforeRunId 를 더해 9 → 10.
            // 2026-09-16 CheckControlTableColumnContract(계약 밖 batch 표의 컬럼 계약 분열)를 더해 10 → 11.
            // 2026-09-16 CheckPreRunIdRunIdWrites(발급 전 절이 run id 자리에 쓴다)를 더해 11 → 12.
            // 2026-09-19 CheckReservedWordAliasInSql(SQL 펜스의 예약어 별칭 - 컴파일 오류)를 더해 12 → 13.
            // 2026-09-19 CheckSqlFenceParses(펜스 전체가 파싱되는가 - 위 항목의 상위 축)를 더해 13 → 14.
            Assert.Equal(14, probe.SeenCheckExpressions.Count);
            Assert.All(probe.SeenCheckExpressions, e => Assert.StartsWith("() => ", e!, StringComparison.Ordinal));
        }

        [Fact]
        public void ValidateConsolidated_WhenAnEarlyCheckThrowsInside_TheLaterChecksStillRun()
        {
            // 감싸기가 사 주는 것은 「다음 검사가 계속 돈다」이다. 감싸지 않으면 예외가
            // catch-all 로 점프해 뒤 검사들이 통째로 안 돈다 - B(지우지 않기)만으로는
            // 그것까지 못 산다.
            var probe = new Probe(throwInsideAt: "ValidateMarkdownStructure");

            probe.ValidateConsolidated(ConsolidatedMissingHeaders);

            Assert.Equal(14, probe.SeenCheckExpressions.Count);
        }

        // ─────────────────────────────────────────────────────────────────────
        // ValidateBatchStep — 고장 모양이 다르다. 지우는 catch-all 이 없어서
        // 미보호 호출이 던지면 예외가 **호출자로 전파**된다.
        // ─────────────────────────────────────────────────────────────────────

        private static BatchStepPlan BareStep() => new(
            Code: "S01", Name: "S01 단계",
            LegacyProcedures: new[] { "dbo.UP_NOT_REAL" },
            TargetTables: Array.Empty<string>(),
            ErrorCodes: Array.Empty<string>(), Chunkable: false,
            SchemaTables: Array.Empty<string>());

        private static StepValidationResult RunStep(MechanicalValidator validator) =>
            validator.ValidateBatchStep(
                "### S01 단계\n\n```sql\nSELECT 1;\n```\n",
                BareStep(),
                Array.Empty<string>(),
                new Dictionary<string, SpecConditions>());

        [Fact]
        public void ValidateBatchStep_WhenACheckThrows_TheExceptionDoesNotEscapeToTheCaller()
        {
            // 이 메서드에는 지우는 catch-all 이 없다(try/catch 자체가 없다). 그래서 고장
            // 모양이 Validate 와 다르다 - 감싸지 않은 검사가 던지면 예외가 호출자로 그대로
            // 전파되고, 호출부(VerificationPipelineOrchestrator 의 ValidateBatchStep 호출)는
            // 그것을 감싸지 않는다(바로 위 try/catch 는 그 호출 앞에서 닫힌다).
            // 종전에 CheckForbiddenShortcuts 를 포함한 열 자리가 감싸지 않은 상태였다.
            var validator = new Probe(throwInsideAt: "CheckForbiddenShortcuts");

            var result = RunStep(validator);

            Assert.NotNull(result);
        }

        [Fact]
        public void ValidateBatchStep_WhenAnUnwrappedCheckThrows_ItDoesEscape()
        {
            // 되돌림 판정의 반대편. 위 시험이 무엇을 사 주는지 이 시험이 말한다 -
            // 감싸지 않으면(= SafeCheck 바깥에서 던지면) 예외가 실제로 탈출한다.
            // 이 단언이 깨지면 위 시험은 감싸기가 없어도 통과하는 시험 없는 가지가 된다.
            var validator = new Probe(throwOutsideAt: "CheckForbiddenShortcuts");

            Assert.Throws<InvalidOperationException>(() => RunStep(validator));
        }

        [Fact]
        public void ValidateBatchStep_RunsEveryCheckThroughSafeCheck()
        {
            // 26 자리가 전부 SafeCheck 를 타는가. 이름이 안 실리는 자리가 하나라도 있으면
            // 그 자리는 감싸지 않은 것이다.
            var probe = new Probe();

            RunStep(probe);

            Assert.NotEmpty(probe.SeenCheckExpressions);
            Assert.All(probe.SeenCheckExpressions, e => Assert.NotNull(e));
            Assert.All(probe.SeenCheckExpressions, e => Assert.StartsWith("() => ", e!, StringComparison.Ordinal));
        }
    }
}
