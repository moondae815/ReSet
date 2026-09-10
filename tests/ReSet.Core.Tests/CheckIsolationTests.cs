using System;
using System.Collections.Generic;
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
            private readonly string? _throwAt;

            public Probe(string? throwAt = null) => _throwAt = throwAt;

            public List<string?> SeenCheckExpressions { get; } = new();

            /// <summary>
            /// <c>SafeCheck</c>가 <c>protected</c>라 시험이 직접 못 부른다. 동작을 안 바꾸는
            /// 통로만 연다 - 여기서 이름을 재면 <b>파생 타입의 override</b>가 기준이 되어
            /// 순환이므로, 이름은 제품 경로(<c>Validate</c>)에서만 잰다.
            /// </summary>
            public void RunThroughSafeCheck(Action work) => SafeCheck(work);

            protected override void SafeCheck(Action check, string? checkExpression = null)
            {
                SeenCheckExpressions.Add(checkExpression);

                if (_throwAt is not null && checkExpression is not null
                    && checkExpression.Contains(_throwAt, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("주입된 검사 예외 - " + _throwAt);
                }

                base.SafeCheck(check, checkExpression);
            }
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
            var result = new Probe(throwAt: "CheckMachineTableShape").Validate(MissingRequiredHeaders);

            Assert.Contains(result.Errors, e => e.Contains("CRUD 분석", StringComparison.Ordinal));
            Assert.False(result.IsValid);
        }

        [Fact]
        public void Validate_WhenALaterCheckThrows_DetailedErrorsSurviveToo()
        {
            // DetailedErrors는 RegenerationScopeSelector.FromL1Errors와
            // BuildSuggestedPromptFix가 소비해 **재생성 범위와 프롬프트 처방**을 정한다.
            // Errors만 살리고 이쪽을 지우면 재생성이 무엇을 고쳐야 하는지 모른 채 돈다.
            var result = new Probe(throwAt: "CheckMachineTableShape").Validate(MissingRequiredHeaders);

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
        public void Validate_WithoutExpectations_RunsExactlyTheThreeUnconditionalChecks()
        {
            // expectations가 null이면 조건부 27자리는 안 돈다. 이 수가 흔들리면 위
            // 이름 잠금이 어느 자리를 재는지 모르게 된다.
            var probe = new Probe();

            probe.Validate(MissingRequiredHeaders);

            Assert.Equal(3, probe.SeenCheckExpressions.Count);
        }
    }
}
