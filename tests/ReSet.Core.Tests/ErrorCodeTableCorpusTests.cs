using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「오류 코드」 표가 실물 코퍼스에서 만족 가능한 요구인지 본다.
    ///
    /// [왜 이 테스트가 필요한가] CheckErrorCodes는 코퍼스에서 한 번도 발화한 적이 없다 -
    /// expectations.ErrorCodes.Count == 0에서 항상 즉시 반환하기 때문이 아니라, 캐시 히트
    /// 산출물에는 L1이 아예 안 돌기 때문이다. 캐시 17이 표를 실으면 **검증된 적 없는 검사가
    /// 통째로 켜진다.** 그 검사가 만족 불가능한 지시라면 31개 객체가 한꺼번에 재시도를
    /// 소진한다. 승격 전에 그것부터 닫는다.
    ///
    /// [무엇을 증명하고 무엇을 증명하지 못하는가] 증명하는 것은 「완전 전사된 표를 검사가
    /// 통과한다」뿐이다. **모델이 그 표를 실제로 맞힐지는 증명하지 못한다** - 그건 재시도
    /// 소진으로만 드러나고 카나리아(계획서 태스크 8)로만 닫힌다.
    ///
    /// [왜 건수를 「하한」으로만 단언하는가] MachineTableExpansionCorpusTests가 건수를 아예
    /// 단언하지 않는 근거를 적는다 - 숫자로 못박으면 코퍼스에 SP가 하나 늘 때마다 빨개지고
    /// 다음 사람이 관측을 읽는 대신 기대값을 고친다. 그 근거에 동의하되 **하한은 다르다**:
    /// 하한은 코퍼스가 커져도 안 깨지고, 추출기가 조용히 망가져 전부 비는 경우를 잡는다.
    /// 하한이 없으면 이 테스트는 「발화 0」을 찍고 통과하는데 그 0이 「검사가 만족된다」가
    /// 아니라 「잴 재료가 없다」일 수 있다.
    /// </summary>
    public class ErrorCodeTableCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public ErrorCodeTableCorpusTests(ITestOutputHelper output) => _output = output;

        [SkippableFact]
        public void ErrorCodeTable_RenderedFromDdl_IsAcceptedByCheckErrorCodes()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var procedures = Path.Combine(root, "output", "Procedures");
            Skip.IfNot(Directory.Exists(procedures), CorpusSkip.Reason);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var validator = new MechanicalValidator();

            int objects = 0, objectsWithFacts = 0, factTotal = 0;
            var violations = new List<string>();

            foreach (var dir in Directory.GetDirectories(procedures)
                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                var meta = Path.Combine(dir, "raw", "metadata.json");
                if (!File.Exists(meta)) continue;

                var def = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(meta), opts);
                if (def == null) continue;

                var expectations = SpecExpectations.From(def);
                if (expectations == null) continue;

                objects++;
                var facts = expectations.ErrorCodes;
                if (facts.Count > 0)
                {
                    objectsWithFacts++;
                    factTotal += facts.Count;
                }

                // 갈래 1 - 완전 전사된 표. 사실이 있든 없든 발화가 없어야 한다.
                var rendered = StepSweepService.RenderErrorCodeTable(facts);
                foreach (var message in ErrorCodeMessages(validator, rendered, expectations))
                {
                    violations.Add($"{Path.GetFileName(dir)} [전사됨] {message}");
                }

                // 갈래 2 - 표가 아예 없는 문서. 사실이 0건인 객체는 여기서도 침묵해야
                // 한다(조기 반환). 사실이 있는 객체는 여기서 반드시 발화해야 한다 -
                // 발화하지 않으면 검사가 아무것도 지키지 않는다는 뜻이다.
                var withoutTable = "## 개요\n\n표가 없는 문서다.\n";
                var missing = ErrorCodeMessages(validator, withoutTable, expectations).ToList();

                if (facts.Count == 0 && missing.Count > 0)
                {
                    violations.Add(
                        $"{Path.GetFileName(dir)} [사실 0건인데 표를 요구] {missing[0]}");
                }

                if (facts.Count > 0 && missing.Count == 0)
                {
                    violations.Add(
                        $"{Path.GetFileName(dir)} [사실 {facts.Count}건인데 표 부재에 침묵]");
                }

                _output.WriteLine($"{Path.GetFileName(dir),-45} 오류 코드 사실 {facts.Count,3}");
            }

            _output.WriteLine("");
            _output.WriteLine(
                $"객체 {objects} · 사실을 가진 객체 {objectsWithFacts} · 사실 합 {factTotal}");

            Assert.True(objects > 0, "코퍼스 객체를 하나도 못 읽었다");

            // 하한이다. 정확한 값이 아니라 「재료가 있다」를 지킨다 - 추출기가 조용히
            // 망가져 전부 비면 위의 발화 0이 아무 뜻도 없는 0이 된다.
            Assert.True(
                objectsWithFacts > 0,
                $"오류 코드 사실을 가진 객체가 하나도 없다(객체 {objects}) - "
                + "DmlScopeExtractor.ExtractErrorCodes가 조용히 비었을 수 있다");
            Assert.True(
                factTotal > 0,
                $"오류 코드 사실 총수가 0이다(객체 {objects}) - 같은 이유로 의심한다");

            Assert.True(
                violations.Count == 0,
                "CheckErrorCodes가 만족 불가능하거나 아무것도 지키지 않는 자리가 있다:\n  "
                + string.Join("\n  ", violations));
        }

        /// <summary>
        /// L1을 통째로 돌리되 오류 코드 표에 관한 발화만 걷는다. Validate는 모든 검사를
        /// 돌리므로 합성 문서에서는 헤딩 부재 같은 무관한 발화가 잔뜩 난다 - 그것을 이
        /// 테스트의 판정에 섞으면 무엇을 재는지 모르게 된다.
        /// </summary>
        private static IEnumerable<string> ErrorCodeMessages(
            MechanicalValidator validator, string markdown, SpecExpectations expectations) =>
            validator.Validate(markdown, expectations).DetailedErrors
                .Where(e => e.Type == ErrorType.ErrorCodeTableMissing)
                .Select(e => e.Message);
    }
}
