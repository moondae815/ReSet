using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「실행 의미」 표가 실물 코퍼스에서 만족 가능한 요구인지 본다.
    ///
    /// [왜 이 테스트가 필요한가] `2026-09-06-대입-감쌈-벗기기-design.md`의 다섯 회차가
    /// 비집계·집계 대입 두 갈래를 넓혀 `CheckExecutionSemantics`가 정확 일치로 대조하는
    /// 대상 칸이 커졌다(실측 - 대상 칸 52개, 최대 834자, 500자 초과 2, 공백 한 칸만
    /// 다른 쌍 존재). L1 실패는 보고가 아니라 되돌림이라(`VerificationPipelineOrchestrator`의
    /// `ComposeAfterL1Failure` → `attempt++`) 재시도 소진으로 곧장 번진다. 이 검사가
    /// 무엇을 증명하고 무엇을 증명하지 못하는지는
    /// `docs/audit-reports/2026-09-07-실행의미-표-만족가능성-사전선언.md` §2에 있다 -
    /// 증명하는 것은 「완전 전사된 표를 검사가 통과한다」뿐이고, 모델이 그 표를 실제로
    /// 맞힐지는 카나리아로만 닫힌다.
    ///
    /// [왜 세 루트를 다 도는가] `output/Procedures`(14) · `output/Functions`(10) ·
    /// `output/External/*/Functions`(7) = 31. 프로시저만 돌면 함수 쪽 추출기가 통째로
    /// 비어도 `objectsWithFacts` 하한이 프로시저만으로 만족돼 조용히 통과한다 - 이
    /// 회차가 계속 경계하는 바로 그 실패 양식이다. `output/`만 걷고 `output.bak-*`는
    /// 걷지 않는다.
    ///
    /// [왜 렌더를 리플렉션으로 부르는가] `AiService.BuildExecutionSemanticsTableLines`가
    /// 「조립기가 채우고 LLM은 손대지 않는다」고 스스로 적은 정본 렌더러다. 표 모양을
    /// 이 테스트가 손으로 다시 적으면(LocalVariableTableCorpusTests의 PerfectTranscription
    /// 방식) `EscapeTableCell` 이스케이프 규칙이 두 곳에 생겨 어긋날 수 있다 -
    /// ErrorCodeTableCorpusTests가 `StepSweepService.RenderErrorCodeTable`이라는 공개
    /// 진입점을 부르는 것과 같은 이유다. 다만 실행 의미 표에는 그런 공개 창구가 없어
    /// `BatchStepPromptContractTests`·`MechanicalValidatorTests`(`FirstStepRowCreationMessage`
    /// 리플렉션)와 같은 관례로 private 메서드를 직접 부른다.
    ///
    /// [왜 건수를 「하한」으로만 단언하는가] 숫자로 못박으면 코퍼스에 SP가 하나 늘 때마다
    /// 빨개지고 다음 사람이 관측을 읽는 대신 기대값을 고친다(ErrorCodeTableCorpusTests의
    /// 클래스 주석과 같은 근거).
    /// </summary>
    public class ExecutionSemanticsTableCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public ExecutionSemanticsTableCorpusTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// 코퍼스가 아는 객체 종류 셋.
        /// </summary>
        private enum ObjectKind
        {
            Procedure,
            FunctionSameDb,
            FunctionExternalDb,
        }

        [SkippableFact]
        public void ExecutionSemanticsTable_RenderedFromDdl_IsAcceptedByTheCheck()
        {
            var root = CorpusPaths.RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var outputRoot = Path.Combine(root, "output");
            Skip.IfNot(Directory.Exists(outputRoot), CorpusSkip.Reason);

            // 세 루트 - 하드코딩된 DB 이름 없이 재귀 탐색으로 External 밑의 임의 DB
            // 폴더를 다 잡는다.
            var roots = new (ObjectKind Kind, string Label, string Dir)[]
            {
                (ObjectKind.Procedure, "프로시저", Path.Combine(outputRoot, "Procedures")),
                (ObjectKind.FunctionSameDb, "함수(같은 DB)", Path.Combine(outputRoot, "Functions")),
                (ObjectKind.FunctionExternalDb, "함수(외부 DB)", Path.Combine(outputRoot, "External")),
            };
            Skip.IfNot(roots.Any(r => Directory.Exists(r.Dir)), CorpusSkip.Reason);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var validator = new MechanicalValidator();

            int objects = 0, objectsWithFacts = 0, factTotal = 0;
            var byKind = roots.ToDictionary(
                r => r.Kind,
                r => (Objects: 0, ObjectsWithFacts: 0, FactTotal: 0));
            var violations = new List<string>();

            // COMM4CLIENT 관측 - 사전선언 §1이 지목한, 834자 셀과 공백 한 칸 차이 쌍을
            // 동시에 가진 객체가 이 검사를 통과하는지 별도로 적는다(다음 카나리아 대상).
            var comm4ClientObservations = new List<string>();

            foreach (var (kind, label, rootDir) in roots)
            {
                if (!Directory.Exists(rootDir)) continue;

                var metadataFiles = Directory
                    .EnumerateFiles(rootDir, "metadata.json", SearchOption.AllDirectories)
                    .Where(m => string.Equals(
                        Path.GetFileName(Path.GetDirectoryName(m)) ?? string.Empty,
                        "raw", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m, StringComparer.Ordinal);

                foreach (var meta in metadataFiles)
                {
                    // raw/의 부모가 객체 디렉터리다 - Procedures/Functions는 <obj>/raw/,
                    // External은 <db>/Functions/<obj>/raw/로 깊이가 다르지만 이 관계는
                    // 어느 쪽이든 성립한다.
                    var dir = Path.GetDirectoryName(Path.GetDirectoryName(meta))!;
                    var label2 = Path.GetFileName(dir);

                    var def = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(meta), opts);
                    if (def == null) continue;

                    var expectations = SpecExpectations.From(def);
                    if (expectations == null) continue;

                    objects++;
                    var kindTotals = byKind[kind];
                    kindTotals.Objects++;

                    var facts = expectations.ExecutionSemantics;
                    if (facts.Count > 0)
                    {
                        objectsWithFacts++;
                        factTotal += facts.Count;
                        kindTotals.ObjectsWithFacts++;
                        kindTotals.FactTotal += facts.Count;
                    }
                    byKind[kind] = kindTotals;

                    // 갈래 1 - 완전 전사된 표(정본 렌더러 그대로). 사실이 있든 없든
                    // 발화가 없어야 한다.
                    var rendered = RenderAsFinalDocument(facts);
                    var perfectMessages = ExecutionSemanticsMessages(validator, rendered, expectations).ToList();
                    foreach (var message in perfectMessages)
                    {
                        violations.Add($"[{label}] {label2} [전사됨] {message}");
                    }

                    // 갈래 2 - 표가 아예 없는 문서. 사실 0건인 객체는 침묵(조기 반환),
                    // 사실이 있는 객체는 반드시 발화해야 한다 - 발화하지 않으면 검사가
                    // 아무것도 지키지 않는다는 뜻이다.
                    var withoutTable = "## CRUD 분석\n\n표가 없는 문서다.\n";
                    var missing = ExecutionSemanticsMessages(validator, withoutTable, expectations).ToList();

                    if (facts.Count == 0 && missing.Count > 0)
                    {
                        violations.Add($"[{label}] {label2} [사실 0건인데 표를 요구] {missing[0]}");
                    }

                    if (facts.Count > 0 && missing.Count == 0)
                    {
                        violations.Add($"[{label}] {label2} [사실 {facts.Count}건인데 표 부재에 침묵]");
                    }

                    if (string.Equals(label2, "dbo.UF_GET_COMM4CLIENT", StringComparison.OrdinalIgnoreCase))
                    {
                        var maxCellLength = facts.Count == 0
                            ? 0
                            : facts.Max(f => f.Target.Length);
                        comm4ClientObservations.Add(
                            $"{label2}: 사실 {facts.Count}건 · 대상 칸 최대 {maxCellLength}자 · "
                            + $"[전사됨] 발화 {perfectMessages.Count}건"
                            + (perfectMessages.Count > 0
                                ? $" ({string.Join(" / ", perfectMessages)})"
                                : " (통과)"));
                    }

                    _output.WriteLine(
                        $"[{label,-12}] {label2,-55} 실행 의미 사실 {facts.Count,3}");
                }
            }

            _output.WriteLine("");
            foreach (var (kind, label, _) in roots)
            {
                var t = byKind[kind];
                _output.WriteLine(
                    $"{label,-12} - 객체 {t.Objects,3} · 사실을 가진 객체 {t.ObjectsWithFacts,3} · 사실 합 {t.FactTotal,3}");
            }
            _output.WriteLine("");
            _output.WriteLine(
                $"객체 {objects} · 사실을 가진 객체 {objectsWithFacts} · 사실 합 {factTotal}");

            _output.WriteLine("");
            _output.WriteLine("[COMM4CLIENT 관측]");
            foreach (var line in comm4ClientObservations)
            {
                _output.WriteLine("  " + line);
            }
            if (comm4ClientObservations.Count == 0)
            {
                _output.WriteLine("  (dbo.UF_GET_COMM4CLIENT를 코퍼스에서 못 찾음)");
            }

            Assert.True(objects > 0, "코퍼스 객체를 하나도 못 읽었다");

            // 하한이다. 정확값으로 박으면 코퍼스가 늘 때마다 빨개지고 다음 사람이
            // 관측을 읽는 대신 기대값을 고친다 - 그 근거는 ErrorCodeTableCorpusTests의
            // 클래스 주석에 있다. 하한은 루트 하나가 통째로 빠지는 회귀를 잡는다.
            Assert.True(objects >= 31, $"코퍼스 객체가 {objects}개다 - 31 이상이어야 한다");

            // 재료가 살아 있는가 - 추출기가 조용히 망가져 전부 비는 경우를 잡는다.
            // 이것이 없으면 이 테스트는 "발화 0"으로 통과하는데 그 0이 "검사가
            // 만족된다"가 아니라 "잴 재료가 없다"일 수 있다.
            Assert.True(objectsWithFacts >= 1, "실행 의미 사실을 가진 객체가 하나도 없다");
            Assert.True(factTotal >= 1, "실행 의미 사실 합이 0이다");

            Assert.Empty(violations);
        }

        /// <summary>
        /// 조건 ⑥ - 뮤턴트. 렌더된 표의 셀 하나(대상 칸)에서 공백 한 칸을 지워
        /// `CheckExecutionSemantics`가 실제로 잡는지 본다. 안 잡히면 이 검사는 정확
        /// 일치를 요구한다고 스스로 적어 놓고도(MechanicalValidator.cs의
        /// `cells.Any(c => c == fact.Target)`) 아무것도 안 잠근 것이다(R2).
        ///
        /// 사전선언 §1이 지목한 `dbo.UF_GET_COMM4CLIENT`를 쓴다 - 그 객체의 DDL이
        /// `IIF(@pi_intFreeInterestFlag IN (0,2), ...)`와
        /// `IIF(@pi_intFreeInterestFlag IN(0,2), ...)`처럼 공백 한 칸만 다른 두 대상
        /// 칸을 실제로 나란히 갖고 있어, 이 검사가 지키려는 대조가 만족 불가능한 정밀도를
        /// 요구하는 게 아닌지를 가장 먼저 시험해야 할 자리다.
        /// </summary>
        [SkippableFact]
        public void ExecutionSemanticsTable_MutatedCell_IsRejectedByTheCheck()
        {
            var root = CorpusPaths.RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var meta = Path.Combine(
                root, "output", "External", "SETTLE_CARD_DB", "Functions",
                "dbo.UF_GET_COMM4CLIENT", "raw", "metadata.json");
            Skip.IfNot(File.Exists(meta), CorpusSkip.Reason);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var def = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(meta), opts);
            Assert.NotNull(def);

            var expectations = SpecExpectations.From(def);
            Assert.NotNull(expectations);

            var facts = expectations!.ExecutionSemantics;
            Assert.True(facts.Count > 0, "dbo.UF_GET_COMM4CLIENT에 실행 의미 사실이 없다 - 뮤턴트를 만들 수 없다");

            var validator = new MechanicalValidator();

            // 대조군 - 완전 전사는 통과해야 한다(위 코퍼스 테스트와 같은 사실).
            var control = RenderAsFinalDocument(facts);
            var controlMessages = ExecutionSemanticsMessages(validator, control, expectations).ToList();
            Assert.Empty(controlMessages);

            // 뮤턴트 - 대상 칸에 공백이 있는 첫 사실을 골라 공백 한 칸을 지운다.
            var mutationTarget = facts.FirstOrDefault(f => f.Target.Contains(' '));
            Assert.True(mutationTarget != null, "공백을 지울 수 있는 대상 칸이 없다");

            var mutatedFacts = facts
                .Select(f => f == mutationTarget
                    ? f with { Target = RemoveOneSpace(f.Target) }
                    : f)
                .ToList();
            var mutated = RenderAsFinalDocument(mutatedFacts);
            var mutatedMessages = ExecutionSemanticsMessages(validator, mutated, expectations).ToList();

            Assert.True(
                mutatedMessages.Count > 0,
                "공백 한 칸을 지운 대상 칸을 CheckExecutionSemantics가 못 잡는다 - "
                + $"원래 대상 `{mutationTarget!.Target}` → 뮤턴트 `{RemoveOneSpace(mutationTarget.Target)}`. "
                + "이 검사가 정확 일치를 요구한다면서 실제로는 아무것도 안 잠근 것이다.");
        }

        private static string RemoveOneSpace(string s)
        {
            var idx = s.IndexOf(' ');
            Assert.True(idx >= 0, "공백이 없는 문자열에서 공백을 지우려 했다");
            return s.Remove(idx, 1);
        }

        private static IEnumerable<string> ExecutionSemanticsMessages(
            MechanicalValidator validator, string markdown, SpecExpectations expectations) =>
            validator.Validate(markdown, expectations).DetailedErrors
                .Where(e => e.Type == ErrorType.ExecutionSemanticsTableMissing)
                .Select(e => e.Message);

        /// <summary>
        /// 정본 렌더러(`AiService.BuildExecutionSemanticsTableLines`)를 리플렉션으로 불러
        /// 「완전 전사된 문서」 모양으로 접는다.
        ///
        /// [왜 CRITICAL 지시 줄과 3칸 들여쓰기를 벗기는가] 그 렌더러는 프롬프트의 번호
        /// 목록 안에 끼워 넣기 위한 장식(들여쓰기)과 모델에게 주는 지시문
        /// (`[CRITICAL EXECUTION SEMANTICS TABLE] ...`)을 함께 낸다. 이 테스트가 재는
        /// 것은 "모델이 그 지시를 따라 표를 문서에 verbatim으로 옮겼을 때 문서가 어떤
        /// 모양인가"이지 프롬프트 자체가 아니다 - LocalVariableTableCorpusTests의
        /// PerfectTranscription과 같은 자리다.
        /// </summary>
        private static string RenderAsFinalDocument(IReadOnlyList<ExecutionSemanticFact> facts)
        {
            var method = typeof(AiService).GetMethod(
                "BuildExecutionSemanticsTableLines", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.True(
                method is not null,
                "AiService.BuildExecutionSemanticsTableLines를 찾지 못했다. 이름이 바뀌었다면 "
                + "이 테스트가 부르는 정본 렌더러도 같이 옮겨졌는지 확인하라.");

            var lines = (List<string>)method!.Invoke(null, new object[] { facts })!;

            var sb = new StringBuilder();
            sb.AppendLine("## CRUD 분석");
            sb.AppendLine();
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("[CRITICAL", StringComparison.Ordinal))
                {
                    // 지시문 줄 - 문서에 실리는 게 아니라 모델에게 "이 표를 그대로
                    // 옮겨라"라고 말하는 프롬프트 장식이다.
                    continue;
                }

                sb.AppendLine(
                    line.StartsWith("   ", StringComparison.Ordinal) ? line.Substring(3) : line);
            }

            return sb.ToString();
        }
    }
}
