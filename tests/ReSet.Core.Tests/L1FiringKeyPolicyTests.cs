using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// L1 발화마다 검사 키가 붙는지 본다.
    ///
    /// [왜 필요한가 - 2026-09-09 실측] `result.Errors.Add` 97 자리 중
    /// `result.DetailedErrors.Add` 로 타입 키가 붙는 것은 57 자리뿐이고, 키가 하나도
    /// 없는 검사가 30 개(발화 자리 40)다. 그 30 안에 재시도 6 회를 태운
    /// `CheckErrorCodeUniquenessClaim` 이 있다 — 키 없이 만든 수렴 탐지기는 정확히
    /// 사고를 낸 검사들에 눈이 먼 채 초록을 찍는다.
    /// </summary>
    public class L1FiringKeyPolicyTests
    {
        [Fact]
        public void Report_AttachesTheCallerMethodNameAsTheCheckKey()
        {
            var result = new ValidationResult();

            ReportFromHere(result);

            var firing = Assert.Single(result.Firings);
            Assert.Equal("ReportFromHere", firing.CheckKey);
            Assert.Equal("발화 문구", firing.Message);
        }

        private static void ReportFromHere(ValidationResult result) =>
            result.Report("발화 문구");

        [Fact]
        public void Report_AlsoAppendsToErrors_SoExistingConsumersAreUnchanged()
        {
            // Errors 는 배너·NotifyL1Errors·프롬프트 처방이 읽는다. Firings 는 그것을
            // 대체하는 것이 아니라 곁에 붙는 관측 통로다.
            var result = new ValidationResult();

            ReportFromHere(result);

            Assert.Equal(new[] { "발화 문구" }, result.Errors);
        }

        [Fact]
        public void Report_DoesNotTouchDetailedErrors()
        {
            // DetailedErrors 는 RegenerationScopeSelector.FromL1Errors 와
            // BuildSuggestedPromptFix 가 소비해 **재생성 범위와 프롬프트 처방**을 정한다.
            // 키를 달자고 없던 DetailedError 를 40 자리에 새로 넣으면 그 동작이 바뀐다 -
            // 관측하려고 의미를 흔드는 셈이라 이 자리를 잠근다.
            var result = new ValidationResult();

            ReportFromHere(result);

            Assert.Empty(result.DetailedErrors);
        }

        // 스캐너 양성 표본 - 직접 Errors.Add 는 키가 안 붙으므로 위반이다.
        [Fact]
        public void Scanner_FlagsADirectErrorsAdd()
        {
            var source = @"
class C
{
    void M(ValidationResult result)
    {
        result.Errors.Add(""x"");
    }
}";

            var offender = Assert.Single(L1FiringKeyPolicyScanner.ScanSource(source, "Fake.cs"));

            Assert.Equal("Fake.cs", offender.RelativePath);
            Assert.Equal("M", offender.Member);
            Assert.Equal(6, offender.Line);
        }

        // 스캐너 음성 표본 - Report 는 키가 붙으므로 위반이 아니다.
        [Fact]
        public void Scanner_DoesNotFlagReport()
        {
            var source = @"
class C
{
    void M(ValidationResult result)
    {
        result.Report(""x"");
    }
}";

            Assert.Empty(L1FiringKeyPolicyScanner.ScanSource(source, "Fake.cs"));
        }

        // 다른 리스트의 Add 를 오탐하면 규칙이 버려진다.
        [Fact]
        public void Scanner_DoesNotFlagUnrelatedAdds()
        {
            var source = @"
class C
{
    void M(ValidationResult result, System.Collections.Generic.List<string> other)
    {
        other.Add(""x"");
        result.DetailedErrors.Add(null);
    }
}";

            Assert.Empty(L1FiringKeyPolicyScanner.ScanSource(source, "Fake.cs"));
        }
    }
}
