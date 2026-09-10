using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// L1 발화마다 검사 키가 붙는지 본다.
    ///
    /// [왜 필요한가 - 2026-09-09 실측, 2026-09-10 최종 리뷰에서 관할 정정]
    /// 리팩터 전 `result.Errors.Add` 는 97 자리였다. 그중 `result.DetailedErrors.Add`
    /// 로 타입 키가 붙는 것은 57 자리뿐이고, 키가 하나도 없는 검사가 30 개(발화
    /// 자리 40)였다. 그 30 안에 재시도 6 회를 태운 `CheckErrorCodeUniquenessClaim`
    /// 이 있다 — 키 없이 만든 수렴 탐지기는 정확히 사고를 낸 검사들에 눈이 먼 채
    /// 초록을 찍는다.
    ///
    /// **「97 자리 치환」은 일어나지 않았다** - 관할이 갈린다(HEAD 실측):
    ///   `ValidationResult` 59 자리 - 전부 `Report()` 로 옮겨 이 시험이 잠근다.
    ///   `StepValidationResult` 38 자리(`Errors.Add` 37 + `AddRange` 1) - 그대로
    ///   남았다. 이 타입엔 애초 `Report`/`Firings` 통로가 없고(`ValidateBatchStep`
    ///   재시도 루프가 `L1AttemptLog.Append` 를 부르지도 않는다), 자기강화 게이트의
    ///   관할 밖으로 명시적으로 남겨졌다(자세한 근거는
    ///   `tests/ReSet.Core.Tests/l1-firing-key-baseline.txt` 와
    ///   `StepValidationResult` 클래스 요약 참고). 59+38=97 로 총수는 그대로다 -
    ///   숫자가 틀린 게 아니라 「전부 옮겨졌다」는 서술이 틀렸었다.
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

        // 새 위반은 실패한다. 이 게이트가 없으면 다음 사람이 Errors.Add 를 다시 쓰고
        // 수렴 탐지기가 그 검사에 조용히 눈이 먼다.
        [Fact]
        public void NoNewDirectErrorsAdd()
        {
            var repoRoot = RepoPaths.FindRepoRoot();
            var relative = "ReSet.Core/Services/MechanicalValidator.cs";
            var absolute = System.IO.Path.Combine(repoRoot, "src", relative);

            var actual = L1FiringKeyPolicyScanner.ScanFile(absolute, relative);
            var allowed = ReadBaseline(System.IO.Path.Combine(
                repoRoot, "tests", "ReSet.Core.Tests", "l1-firing-key-baseline.txt"));
            var allowedCount = allowed.TryGetValue(relative, out var count) ? count : 0;

            Assert.True(
                actual.Count <= allowedCount,
                $"{relative}: 허용 {allowedCount}건, 실제 {actual.Count}건.\n"
                + "검사 키가 안 붙는 발화입니다 - result.Report(...) 를 쓰십시오.\n"
                + string.Join("\n", actual.Select(o => $"  {o.RelativePath}:{o.Line} ({o.Member})")));
        }

        private static System.Collections.Generic.Dictionary<string, int> ReadBaseline(string path)
        {
            var result = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", System.StringComparison.Ordinal)) continue;
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var count))
                {
                    result[parts[0].Trim()] = count;
                }
            }

            return result;
        }
    }
}
