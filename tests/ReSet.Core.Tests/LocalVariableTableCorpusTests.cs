using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「지역 변수」 표가 실물 코퍼스에서 만족 가능한 요구인지 본다.
    ///
    /// [왜 이 테스트가 필요한가] 이 회차는 재생성을 하지 않는다(사용자 결정). 그래서
    /// 새 L1 검사 CheckLocalVariableDeclarationTable은 캐시 히트 산출물에서 **한 번도
    /// 실행돼 본 적 없는 검사**다. 로드맵은 이 위험을 「다섯 번째 위험」이라 불렀다 -
    /// CheckErrorCodes가 재료가 0이라 코퍼스에서 한 번도 발화한 적이 없었고, 목록이
    /// 채워지는 순간 통째로 켜져 문서마다 오류가 날 위험을 안았다. ErrorCodeTableCorpusTests가
    /// 캐시 17 승격 전에 31 객체 전건 만족 가능성을 재서 그 위험을 닫았고 예측이 맞았다 -
    /// 이 파일은 같은 자를 CheckLocalVariableDeclarationTable에 만든다. 오탐을 안은 채
    /// 다음 재생성이 걸리면 그것이 곧바로 재시도 소진으로 번진다(L1 실패는 보고가 아니라
    /// 되돌림이다 - VerificationPipelineOrchestrator.ComposeAfterL1Failure).
    ///
    /// [왜 세 루트를 다 도는가] `output/Procedures`(14) · `output/Functions`(10) ·
    /// `output/External/*/Functions`(7) = 31. 프로시저만 돌면 함수 쪽 추출기가 통째로
    /// 비어도 objectsWithFacts 하한이 프로시저만으로 만족돼 조용히 통과한다 - 이 회차가
    /// 계속 경계하는 바로 그 실패 양식이다. `output/`만 걷고 `output.bak-*`는 걷지 않는다.
    ///
    /// [무엇을 증명하고 무엇을 증명하지 못하는가] 증명하는 것은 「완전 전사된 표를 검사가
    /// 통과한다」뿐이다. 모델이 그 표를 실제로 맞힐지는 증명하지 못한다 - 그건 재시도
    /// 소진으로만 드러나고 카나리아로만 닫힌다.
    ///
    /// [왜 건수를 「하한」으로만 단언하는가] 숫자로 못박으면 코퍼스에 SP가 하나 늘 때마다
    /// 빨개지고 다음 사람이 관측을 읽는 대신 기대값을 고친다(ErrorCodeTableCorpusTests의
    /// 클래스 주석과 같은 근거). 하한은 코퍼스가 커져도 안 깨지고, 추출기가 조용히
    /// 망가져 전부 비는 경우와 루트 하나가 통째로 빠지는 회귀를 잡는다.
    /// </summary>
    public class LocalVariableTableCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public LocalVariableTableCorpusTests(ITestOutputHelper output) => _output = output;

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
        public void LocalVariableTable_RenderedFromDdl_IsAcceptedByTheCheck()
        {
            var root = CorpusPaths.RepoRoot();
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

            // Census delta 재료 - 프로시저 루트만 따로 합산한다.
            int procedureFactTotal = 0;
            int procedureParameterTotal = 0;

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

                    var facts = expectations.LocalVariableDeclarations;
                    if (facts.Count > 0)
                    {
                        objectsWithFacts++;
                        factTotal += facts.Count;
                        kindTotals.ObjectsWithFacts++;
                        kindTotals.FactTotal += facts.Count;
                    }
                    byKind[kind] = kindTotals;

                    if (kind == ObjectKind.Procedure)
                    {
                        procedureFactTotal += facts.Count;
                        procedureParameterTotal += expectations.ParameterNames.Count;
                    }

                    // 갈래 1 - 완전 전사된 표. 사실이 있든 없든 발화가 없어야 한다.
                    foreach (var message in LocalVariableMessages(
                                 validator, PerfectTranscription(facts), expectations))
                    {
                        violations.Add($"[{label}] {label2} [전사됨] {message}");
                    }

                    // 갈래 2 - 표가 아예 없는 문서. 사실 0건인 객체는 침묵(조기 반환),
                    // 사실이 있는 객체는 반드시 발화해야 한다 - 발화하지 않으면 검사가
                    // 아무것도 지키지 않는다는 뜻이다.
                    var withoutTable = "## 파라미터 목록\n\n표가 없는 문서다.\n";
                    var missing = LocalVariableMessages(validator, withoutTable, expectations).ToList();

                    if (facts.Count == 0 && missing.Count > 0)
                    {
                        violations.Add($"[{label}] {label2} [사실 0건인데 표를 요구] {missing[0]}");
                    }

                    if (facts.Count > 0 && missing.Count == 0)
                    {
                        violations.Add($"[{label}] {label2} [사실 {facts.Count}건인데 표 부재에 침묵]");
                    }

                    _output.WriteLine(
                        $"[{label,-12}] {label2,-45} DECLARE 사실 {facts.Count,3}");
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
            _output.WriteLine(
                $"[census delta] 프로시저 14편 DECLARE 사실 합 {procedureFactTotal} · "
                + $"파라미터 총수 {procedureParameterTotal} · 합 {procedureFactTotal + procedureParameterTotal} "
                + "(SpecMaterialCensus의 69와 대조)");

            Assert.True(objects > 0, "코퍼스 객체를 하나도 못 읽었다");

            // 하한이다. 정확값으로 박으면 코퍼스가 늘 때마다 빨개지고 다음 사람이
            // 관측을 읽는 대신 기대값을 고친다 - 그 근거는 ErrorCodeTableCorpusTests의
            // 클래스 주석에 있다. 하한은 루트 하나가 통째로 빠지는 회귀를 잡는다.
            Assert.True(objects >= 31, $"코퍼스 객체가 {objects}개다 - 31 이상이어야 한다");

            // 재료가 살아 있는가 - 추출기가 조용히 망가져 전부 비는 경우를 잡는다.
            // 이것이 없으면 이 테스트는 "발화 0"으로 통과하는데 그 0이 "검사가
            // 만족된다"가 아니라 "잴 재료가 없다"일 수 있다.
            Assert.True(objectsWithFacts >= 1, "DECLARE 사실을 가진 객체가 하나도 없다");
            Assert.True(factTotal >= 1, "DECLARE 사실 합이 0이다");

            Assert.Empty(violations);
        }

        private static IEnumerable<string> LocalVariableMessages(
            MechanicalValidator validator, string markdown, SpecExpectations expectations) =>
            validator.Validate(markdown, expectations).DetailedErrors
                .Where(e => e.Type == ErrorType.LocalVariableTableMismatch)
                .Select(e => e.Message);

        /// <summary>
        /// 완전 전사된 표를 테스트가 직접 만든다.
        ///
        /// [왜 AiService/StepSweepService의 렌더러를 안 부르는가] 그런 공개 렌더러가
        /// 없고, 있어도 부르면 안 된다 - 렌더러의 버그가 검사의 버그를 가려 준다. 두
        /// 자리가 같은 모양을 각자 적고 있으므로, 어긋나면 이 테스트가 빨개지는 것이
        /// 옳다.
        /// </summary>
        private static string PerfectTranscription(
            IReadOnlyList<LocalVariableDeclarationFact> facts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 파라미터 목록");
            sb.AppendLine();
            sb.AppendLine(LocalVariableDeclarationExtractor.TableHeading);
            sb.AppendLine("| 변수 명칭 | 데이터 타입 | 초기값 |");
            sb.AppendLine("| :--- | :--- | :--- |");
            foreach (var f in facts)
            {
                sb.AppendLine($"| {f.Name} | {f.DataType} | {f.InitialValue} |");
            }
            return sb.ToString();
        }
    }
}
