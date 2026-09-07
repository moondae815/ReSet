using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementPolicyServiceTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "reset-policysvc-" + Guid.NewGuid().ToString("N"));

        public SettlementPolicyServiceTests()
        {
            Seed("dbo.UP_A", "## 개요\n\n요율을 적재한다.\n");
            Seed("dbo.UP_B", "## 개요\n\n원장을 적재한다.\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Seed(string label, string spec)
        {
            var docs = Path.Combine(_root, "Procedures", label, "docs");
            var raw = Path.Combine(_root, "Procedures", label, "raw");
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(raw);
            File.WriteAllText(Path.Combine(docs, "Spec.md"),
                "---\n검증 상태: 통과\n---\n\n" + spec);
            File.WriteAllText(Path.Combine(raw, "metadata.json"),
                "{\"Schema\":\"dbo\",\"Name\":\"X\",\"DdlText\":\"\",\"Dependencies\":[],\"StaticAnalysis\":{}}");
        }

        private void WriteRoster()
        {
            File.WriteAllText(Path.Combine(_root, "settlement-process.md"),
                "# 정산 프로세스 명부\n\n## 1. 요율 적재\n- dbo.UP_A\n\n## 2. 원장 적재\n- dbo.UP_B\n\n## 제외\n");
        }

        private static IAiService AiWriting(params string[] stageBodies)
        {
            var ai = Substitute.For<IAiService>();
            ai.GeneratePolicyStageAsync(
                    Arg.Any<int>(), Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<IReadOnlyList<CodebookEntry>>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    // stageNumber(첫 인자)로 고른다 - call 순서로 고르면 교정 재호출이
                    // 끼어들 때 다른 단계의 본문이 섞여 들어간다(2026-09-06 실측:
                    // 호출 순번 기반 스텁이 단계 1의 재호출에서 단계 2의 본문을
                    // 돌려줘 최종 문서에 "## 1." 헤딩이 아예 사라지는 결함을 놓쳤다).
                    var stageNumber = call.ArgAt<int>(0);
                    var index = Math.Min(stageNumber - 1, stageBodies.Length - 1);
                    return Task.FromResult(new AiResult { Content = stageBodies[index] });
                });
            ai.GeneratePolicyOverviewAsync(
                    Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 정산 업무 개요\n\n전체 조망.\n" }));
            ai.ProviderName.Returns("TestProvider");
            ai.ModelName.Returns("test-model");
            return ai;
        }

        /// <summary>
        /// 스텁이 낼 단계 본문. <paramref name="rosterTitle"/>는 <b>명부에 적힌 제목
        /// 그대로</b>이고, 헤딩은 손으로 짓지 않고 계약 함수에서 유도한다.
        ///
        /// [왜 손으로 안 짓는가 - 2026-09-06 C1] 종전에는 이 헬퍼가 `## {n}. {title}`을
        /// 손으로 만들고, 번호 없는 명부용으로 `StageNoNumber`가 따로 있었다. 즉 스텁이
        /// 계약의 한쪽 편(서비스가 찾는 헤딩)에 손으로 맞춰져 있었고 반대쪽(프롬프트가
        /// 실제로 요구하는 헤딩)은 시험에 든 적이 없었다 - 그래서 3720개가 전부 초록인
        /// 채로 헤딩 계약이 갈려 있었다. 계약 함수를 쓰면 스텁이 제품보다 초록일 수 없다.
        /// 명부에 번호가 있고 없고는 이제 rosterTitle 한 인자로 갈린다.
        /// ID 접두사(S{n}-)는 실효 단계 번호(EffectiveStageNumber)를 따른다.
        /// </summary>
        private static string Stage(int n, string rosterTitle, string label, string quote) =>
            BodyUnder(PolicySectionContract.StageHeading(rosterTitle), n, label, quote);

        [Fact]
        public async Task 명부가_없으면_초안을_쓰고_중단한다()
        {
            var service = new SettlementPolicyService(AiWriting("x"));

            await Assert.ThrowsAsync<PolicyRosterBlockedException>(
                () => service.GenerateAsync(_root, profiler: null, effort: null));

            Assert.True(File.Exists(Path.Combine(_root, "settlement-process.md")));
        }

        [Fact]
        public async Task 명부에_빠진_SP가_있으면_중단한다()
        {
            File.WriteAllText(Path.Combine(_root, "settlement-process.md"),
                "# 정산 프로세스 명부\n\n## 1. 요율 적재\n- dbo.UP_A\n\n## 제외\n");
            var service = new SettlementPolicyService(AiWriting("x"));

            var ex = await Assert.ThrowsAsync<PolicyRosterBlockedException>(
                () => service.GenerateAsync(_root, profiler: null, effort: null));

            Assert.Contains(ex.Defects, d => d.Type == RosterDefectType.ProcedureMissing);
        }

        [Fact]
        public async Task 기존_명부를_덮어쓰지_않는다()
        {
            WriteRoster();
            var before = File.ReadAllText(Path.Combine(_root, "settlement-process.md"));
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "settlement-process.md")));
        }

        [Fact]
        public async Task DB없이_완주하고_문서와_사전을_남긴다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.True(File.Exists(outcome.PolicyPath));
            Assert.True(File.Exists(outcome.CodebookPath));
            Assert.Equal(0, outcome.CodeValuesTranslated);
        }

        [Fact]
        public async Task 목차는_명부의_단계_제목_그대로다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);
            var document = File.ReadAllText(outcome.PolicyPath);

            Assert.Contains("## 1. 요율 적재", document);
            Assert.Contains("## 2. 원장 적재", document);
        }

        /// <summary>
        /// 위 테스트("목차는...")는 부분 문자열 일치라 "결함 메시지 안의 헤딩 인용"으로도
        /// 조용히 통과할 수 있다(실제로 그 함정을 한 번 밟았다 - StageMissing 배너 문구가
        /// "## 1. 요율 적재"를 문자 그대로 담고 있어 헤딩이 진짜 절로는 하나도 없는데도
        /// Assert.Contains가 통과했다). 이 테스트는 그 절이 실제 규칙 표를 담은 자리로
        /// 파싱되는지(즉 그 단계의 규칙이 배너가 아니라 본문에 있는지) 직접 잰다.
        /// </summary>
        [Fact]
        public async Task 각_단계의_규칙_표는_실제로_그_단계_헤딩_아래에서_파싱된다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);
            var document = File.ReadAllText(outcome.PolicyPath);

            var stageHeadings = new[] { "## 1. 요율 적재", "## 2. 원장 적재" };
            var rules = PolicyDocumentParser.Parse(document, stageHeadings);

            Assert.Contains(rules, r => r.StageHeading == "## 1. 요율 적재" && r.Id == "S1-01");
            Assert.Contains(rules, r => r.StageHeading == "## 2. 원장 적재" && r.Id == "S2-01");
            Assert.DoesNotContain(outcome.Defects, d => d.Type == PolicyDefectType.StageMissing);
        }

        /// <summary>
        /// T3 리뷰의 Cannot-Verify ① - stageHeadings 접합부.
        ///
        /// 명부 제목에 파서·검증기가 걸려 넘어질 수 있는 형태(마침표·괄호·후행 공백처럼
        /// 흔한 사람 입력)를 그대로 심어, 서비스가 "## " + Title을 변형 없이 이어 붙여
        /// PolicyDocumentParser가 그 절을 찾아내는지 못박는다. 서비스가 대소문자를
        /// 바꾸거나 다시 Trim/정규화하면 LocateSection의 정확 일치 경로가 깨져
        /// PolicyDocumentParser.Parse가 그 단계의 규칙 표를 하나도 못 읽는다 -
        /// 이 테스트는 그 결과로 규칙이 실제로 파싱되는지(즉 EvidenceQuoteNotFound 같은
        /// 부수 결함이 아니라 "표 자체가 안 보임"인지)를 직접 잰다.
        /// </summary>
        [Fact]
        public async Task 명부_제목이_기이해도_그대로_H2가_되고_파서가_그_절을_찾는다()
        {
            File.WriteAllText(Path.Combine(_root, "settlement-process.md"),
                "# 정산 프로세스 명부\n\n"
                + "## 1. 요율 적재 (선행) \n- dbo.UP_A\n\n"
                + "## 2. 원장 적재\n- dbo.UP_B\n\n"
                + "## 제외\n");
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재 (선행)", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);
            var document = File.ReadAllText(outcome.PolicyPath);

            Assert.Contains("## 1. 요율 적재 (선행)", document);

            // 파서가 그 절을 실제로 찾아 규칙을 읽었는지 - 못 찾으면 defects에
            // StageMissing이 실리고 그 단계의 규칙은 CheckCodeValues/CheckProcedureCitationCoverage
            // 어느 쪽 대상에도 오르지 않는다.
            Assert.DoesNotContain(outcome.Defects, d => d.Type == PolicyDefectType.StageMissing);
        }

        [Fact]
        public async Task 근거_명세서의_검증_상태를_헤더에_집계한다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);
            var document = File.ReadAllText(outcome.PolicyPath);

            Assert.Contains("근거 명세서 검증 상태:", document);
            Assert.Contains("통과 2", document);
        }

        [Fact]
        public async Task 인용이_원문에_없으면_배너에_결함을_싣는다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "원문에 없는 구절"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Contains(outcome.Defects, d => d.Type == PolicyDefectType.EvidenceQuoteNotFound);
            Assert.Contains("귀속", File.ReadAllText(outcome.PolicyPath));
        }

        [Fact]
        public async Task 한_번도_인용되지_않은_SP를_배너에_싣는다()
        {
            // 둘째 단언은 배너 구획 자체를 잰다 - "dbo.UP_B"만 찾으면 부록 B가 명부의
            // 모든 프로시저를 늘 나열하므로 배너가 옳든 그르든 참이 되는 동어반복이었다
            // (Fix Round 1 리뷰 Minor). 배너가 만드는 정확한 줄("> - dbo.UP_B")은
            // 부록 B의 표 행("| ... | dbo.UP_B |")과 형태가 달라 서로 혼동되지 않는다.
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_A", "요율을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);
            var document = File.ReadAllText(outcome.PolicyPath);

            Assert.Contains(outcome.Defects, d => d.Type == PolicyDefectType.ProcedureNeverCited);
            Assert.Contains("[미인용 프로시저]", document);
            Assert.Contains("> - dbo.UP_B", document);
        }

        [Fact]
        public async Task DB가_없으면_코드값_번역_0건을_배너에_명시한다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Contains("DB 미연결", File.ReadAllText(outcome.PolicyPath));
        }

        [Fact]
        public async Task 문서는_검증_없음으로_표기된다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.StartsWith("---", File.ReadAllText(outcome.PolicyPath));
            Assert.Contains("검증 상태: 검증 없음", File.ReadAllText(outcome.PolicyPath));
        }

        [Fact]
        public async Task 단계가_경고선을_넘으면_생성은_계속하되_경고한다()
        {
            // 경고선을 넘는 큰 명세서를 심는다. 생성이 막히지 않는 것이 이 테스트의 요지다 -
            // 자동 분할을 하지 않기로 했으므로 경고는 로그로만 나가고 문서는 나와야 한다.
            var big = "## 개요\n\n" + new string('가', SettlementPolicyService.StageSpecCharWarningThreshold + 1)
                      + "\n요율을 적재한다.\n";
            File.WriteAllText(
                Path.Combine(_root, "Procedures", "dbo.UP_A", "docs", "Spec.md"),
                "---\n검증 상태: 통과\n---\n\n" + big);
            WriteRoster();

            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.True(File.Exists(outcome.PolicyPath));
        }

        /// <summary>
        /// Fix Round 1 리뷰 발견(Important 3) - Policy/steps/*.md가 실제로 쓰이는지
        /// 재는 테스트가 없었다. WriteStagePartAsync가 무검증이었다.
        /// </summary>
        [Fact]
        public async Task steps_디렉터리에_단계_수만큼_파일이_단계_번호로_시작해_생긴다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            await service.GenerateAsync(_root, profiler: null, effort: null);

            var stepsDir = Path.Combine(_root, "Policy", "steps");
            var files = Directory.GetFiles(stepsDir).Select(Path.GetFileName).OrderBy(f => f).ToList();

            Assert.Equal(2, files.Count);
            Assert.Contains(files, f => f!.StartsWith("01-"));
            Assert.Contains(files, f => f!.StartsWith("02-"));
        }

        /// <summary>
        /// Fix Round 1 리뷰 발견(Important 3) - 「합쳐진 결과」축의 구멍. 조립된
        /// 정책서 실물(서비스 전체 경로를 거친 산출물)에 부록 A·B가 실제로
        /// 나타나고, 부록 B에 명부의 단계·프로시저가 실리는지 잰다.
        /// PolicyDocumentAssemblerTests가 단위 수준을 재므로 여기서는 통합
        /// 경로에서 같은 사실이 성립하는지만 확인한다.
        /// </summary>
        [Fact]
        public async Task 조립된_문서에_부록_A와_B가_실제로_나타난다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);
            var document = File.ReadAllText(outcome.PolicyPath);

            Assert.Contains("## 부록 A. 코드값 사전", document);
            Assert.Contains("## 부록 B. 단계별 원본 프로시저", document);
            Assert.Contains("| 1. 요율 적재 | dbo.UP_A |", document);
            Assert.Contains("| 2. 원장 적재 | dbo.UP_B |", document);
        }

        /// <summary>
        /// T3 리뷰의 Cannot-Verify ② - RosterDefect에는 심각도 필드가 없다.
        /// 어떤 종류의 명부 결함이든, 하나라도 있으면 서비스는 예외 없이 중단해야 한다.
        /// 특히 StageTitleDuplicated를 얕보고 넘기면 두 번째 "요율 적재" 단계의 표가
        /// MarkdownSectionLocator의 첫 일치 규칙 때문에 통째로 증발한다.
        /// </summary>
        [Fact]
        public async Task 단계_제목이_중복되면_중단한다()
        {
            File.WriteAllText(Path.Combine(_root, "settlement-process.md"),
                "# 정산 프로세스 명부\n\n## 1. 요율 적재\n- dbo.UP_A\n\n## 1. 요율 적재\n- dbo.UP_B\n\n## 제외\n");
            var service = new SettlementPolicyService(AiWriting("x"));

            var ex = await Assert.ThrowsAsync<PolicyRosterBlockedException>(
                () => service.GenerateAsync(_root, profiler: null, effort: null));

            Assert.Contains(ex.Defects, d => d.Type == RosterDefectType.StageTitleDuplicated);
        }

        /// <summary>
        /// T9 리뷰 - 단계별 명세서 선별은 이 서비스의 책임이다. GeneratePolicyStageAsync는
        /// 넘겨받은 sources만 돌 뿐 필터링을 스스로 하지 않는다. 단계 2를 생성할 때
        /// 단계 1의 SP(dbo.UP_A) 명세서가 함께 넘어가면, 단계로 나눈 의미(§9)가 사라지고
        /// 단계 2의 모델이 단계 1의 사실을 인용해 버릴 여지가 생긴다.
        /// </summary>
        [Fact]
        public async Task 단계별_생성은_그_단계에_속한_SP의_명세서만_넘긴다()
        {
            WriteRoster();
            var ai = AiWriting(
                Stage(1, "1. 요율 적재", "dbo.UP_A", "요율을 적재"),
                Stage(2, "2. 원장 적재", "dbo.UP_B", "원장을 적재"));
            var service = new SettlementPolicyService(ai);

            await service.GenerateAsync(_root, profiler: null, effort: null);

            var stage2Call = ai.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(IAiService.GeneratePolicyStageAsync))
                .Select(c => c.GetArguments())
                .First(args => (int)args[0]! == 2);

            var stage2Sources = (IReadOnlyList<(string Label, string SpecMarkdown)>)stage2Call[2]!;

            Assert.Single(stage2Sources);
            Assert.Equal("dbo.UP_B", stage2Sources[0].Label);
            Assert.DoesNotContain(stage2Sources, s => s.Label == "dbo.UP_A");
        }

        /// <summary>
        /// 리뷰 발견(Fix Round 1, Important 2) - 단계별 교정 재호출 검증에 단일 헤딩만
        /// 넘기면 PolicyDocumentParser.Parse 내부의 stageIndex가 항상 0이 되어, 번호
        /// 없는 제목의 실효 단계 번호가 EffectiveStageNumber(heading, 0)으로 계산된다.
        /// 명부의 두 번째 이후 단계가 번호를 안 붙이면 늘 1로 잘못 계산되어, 올바르게
        /// S2-01을 낸 AI 초안이 IdPrefixMismatch로 고발되고 무조건 교정 재호출을 탄다.
        /// 이 테스트는 그 재호출이 실제로 일어나지 않는지(호출 총량으로) 잰다 -
        /// 최종 조립 문서는 전체 목록으로 다시 검증되어 결함 자체는 안 보이므로,
        /// 결함 유무만으로는 이 낭비를 잡을 수 없다.
        /// </summary>
        [Fact]
        public async Task 번호_없는_제목의_두번째_단계는_교정_재호출_없이_받아들여진다()
        {
            File.WriteAllText(Path.Combine(_root, "settlement-process.md"),
                "# 정산 프로세스 명부\n\n## 요율 적재\n- dbo.UP_A\n\n## 원장 적재\n- dbo.UP_B\n\n## 제외\n");

            var ai = AiWriting(
                Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재"));
            var service = new SettlementPolicyService(ai);

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            // 재호출이 없었다면 stageNumber=2로 GeneratePolicyStageAsync를 부른 횟수는
            // 정확히 1이다(교정 재호출까지 있었다면 2가 된다).
            await ai.Received(1).GeneratePolicyStageAsync(
                2, Arg.Any<string>(),
                Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<IReadOnlyList<CodebookEntry>>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

            Assert.DoesNotContain(outcome.Defects, d => d.Type == PolicyDefectType.IdPrefixMismatch);
        }

        /// <summary>
        /// 최종 전체 리뷰 C1 - 단계 헤딩 계약이 두 파일로 갈려 있었다.
        /// 생성 프롬프트(AiService)는 `## {번호}. {제목}`을 요구했고, 서비스는
        /// `"## " + 제목`을 찾았다. 명부 제목에 번호가 있으면 프롬프트가 번호를
        /// 두 번 붙였고(`## 1. 1. 요율 적재`), 없으면 서비스가 못 찾았다 -
        /// 어느 명부로도 둘이 일치하지 않았다.
        ///
        /// [왜 스텁을 안 거치는가] 이 회차의 스텁(AiWriting)은 stageTitle 인자를
        /// 아예 읽지 않고 미리 만든 문자열을 stageNumber로 골랐다. 그래서 헤딩
        /// 계약의 한쪽 편만 손으로 맞춰 놓은 채 3720개가 전부 초록이었다.
        /// 이 테스트는 <b>제품(AiService)이 실제로 만든 시스템 프롬프트</b>에서
        /// 요구 헤딩을 뽑아 그것을 그대로 본문 H2로 쓴다 - 「프롬프트를 그대로
        /// 따르는 모델」이 온 경우다. 그 헤딩이 서비스가 찾는 것과 갈리면
        /// StageMissing이 나고 그 단계의 규칙 표가 통째로 검사에서 빠진다.
        ///
        /// 명부에 번호가 있는 경우와 없는 경우를 둘 다 잠근다 - 실효 번호 폴백
        /// (EffectiveStageNumber)이 그 둘을 가르는 자리이기 때문이다.
        /// </summary>
        [Theory]
        [InlineData("1. 요율 적재", "2. 원장 적재")]
        [InlineData("요율 적재", "원장 적재")]
        public async Task 프롬프트가_요구한_헤딩을_그대로_쓴_본문을_서비스가_찾아낸다(
            string title1, string title2)
        {
            File.WriteAllText(Path.Combine(_root, "settlement-process.md"),
                $"# 정산 프로세스 명부\n\n## {title1}\n- dbo.UP_A\n\n## {title2}\n- dbo.UP_B\n\n## 제외\n");

            var heading1 = await RequiredHeadingFromRealPromptAsync(1, title1);
            var heading2 = await RequiredHeadingFromRealPromptAsync(2, title2);

            var service = new SettlementPolicyService(AiWriting(
                BodyUnder(heading1, 1, "dbo.UP_A", "요율을 적재"),
                BodyUnder(heading2, 2, "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);
            var document = File.ReadAllText(outcome.PolicyPath);

            // ① 서비스가 찾는 헤딩(명부에서 유도)과 프롬프트가 요구한 헤딩이 갈리면
            //    여기서 StageMissing이 난다 - 두 경로를 맞대는 단언은 이것이다.
            Assert.DoesNotContain(outcome.Defects, d => d.Type == PolicyDefectType.StageMissing);
            Assert.DoesNotContain(outcome.Defects, d => d.Type == PolicyDefectType.IdPrefixMismatch);

            // ② 그 절이 배너 인용이 아니라 진짜 규칙 표로 파싱되는지까지 잰다.
            var rules = PolicyDocumentParser.Parse(document, new[] { heading1, heading2 });
            Assert.Contains(rules, r => r.StageHeading == heading1 && r.Id == "S1-01");
            Assert.Contains(rules, r => r.StageHeading == heading2 && r.Id == "S2-01");
        }

        /// <summary>
        /// 제품이 만든 시스템 프롬프트에서 「이 H2를 쓰라」고 지시한 헤딩 문자열을 뽑는다.
        /// 스텁 IAiClient를 물린 진짜 AiService를 부른다 - 프롬프트 문자열 자체가
        /// 시험 대상이므로 그것을 테스트가 다시 짓지 않는다.
        /// </summary>
        private static async Task<string> RequiredHeadingFromRealPromptAsync(
            int stageNumber, string stageTitle)
        {
            var client = Substitute.For<IAiClient>();
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    effort: Arg.Any<string?>(), cancellationToken: Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "본문" }));

            var result = await new AiService(client, 0.2f, false, 8, true, null)
                .GeneratePolicyStageAsync(
                    stageNumber, stageTitle,
                    new[] { ("dbo.UP_A", "## 개요\n\n본문\n") },
                    Array.Empty<CodebookEntry>());

            Assert.NotNull(result.SystemPrompt);
            var match = Regex.Match(result.SystemPrompt!, @"titled `(##[^`]*)`");
            Assert.True(match.Success, "프롬프트가 H2 헤딩을 백틱으로 지시하지 않습니다.");
            return match.Groups[1].Value;
        }

        /// <summary>주어진 헤딩 아래에 규칙 표 하나를 놓은 단계 본문.</summary>
        private static string BodyUnder(string heading, int n, string label, string quote) =>
            heading + "\n\n산문 개요.\n\n"
            + PolicySectionContract.TableHeader + "\n" + PolicySectionContract.TableSeparator + "\n"
            + $"| S{n}-01 | 업무 규칙 | {label} · ## 개요 > \"{quote}\" | {PolicySectionContract.NoCodeValue} |\n";
    }

    /// <summary>
    /// 좌표자 권고 ④ - 교차 태스크 불변식을 영구 테스트로 못박는다.
    ///
    /// 실물 코퍼스에서 만든 초안 명부를 파서와 대조기에 태우면 PlaceholderTitleRemaining
    /// 외의 결함이 없어야 한다. 이 클래스는 읽기만 하므로 임시 디렉터리가 필요 없다.
    ///
    /// [경로를 어떻게 잡는가 - 2026-09-06 I2] 상대 경로는 테스트 호스트 cwd로 풀려
    /// 픽스처 1편만 재고도 초록이 되는 함정이 있다. 그렇다고 절대 경로를 <b>박으면</b>
    /// 세 가지가 한꺼번에 생긴다 - 이미 있는 헬퍼의 네 번째 사본이 되고, 다른 기계·CI에서
    /// Find가 빈 목록을 돌려 「건너뜀이 아니라 실패」가 되며, 어느 워크트리에서 돌든
    /// 공유 체크아웃을 읽는다. 옳은 규칙은 「절대 경로를 써라」가 아니라 「경로가 자기
    /// 워크트리 밖으로 풀리지 않게 하라」이고,
    /// <see cref="CorpusPaths.RepoRootIfCorpusPresent()"/>가 그것을 한다.
    ///
    /// [2026-09-07 정정] 위 문단이 적어 두었던 단서 - 「워크트리가 메인 체크아웃 안에
    /// 있으면 자기 `output`이 없을 때 탐색이 메인까지 올라간다」 - 는 <b>닫혔다.</b>
    /// 루트를 이제 <c>ReSet.slnx</c>로 잡기 때문이다. 그 파일은 모든 워크트리에 있으므로
    /// 탐색이 자기 워크트리에서 멈춘다. 실측으로도 갈렸다: 정박 파일을 없앤 워크트리에서
    /// 옛 자는 `/Users/…/ReSet`(메인)을, 새 자는 워크트리 자신을 돌려줬다. 얕은
    /// 스크래치(bin/…/output의 dbo.USP_Root 1건)에서 멈추는 조용한 오측도 구조적으로
    /// 불가능해졌다 - bin 아래에는 <c>ReSet.slnx</c>가 없다.
    ///
    /// 코퍼스가 아예 없는 기계에서는 여전히 빈 문자열이 돌아와 <b>실패가 아니라
    /// 건너뜀</b>이 된다. 다만 이제 그 건너뜀은 <b>`output/`이 통째로 없을 때만</b>
    /// 나온다 - 재료 하나가 없는 것은 실패로 남는다.
    /// </summary>
    public sealed class SettlementRosterDraftCrossTaskInvariantTests
    {
        [SkippableFact]
        public void 실물_코퍼스의_대상_수는_14다()
        {
            var targets = PolicyTargetDiscovery.Find(RealOutputRoot());

            Assert.Equal(14, targets.Count);
        }

        [SkippableFact]
        public void 실물_코퍼스_초안_명부는_PlaceholderTitleRemaining_외의_결함이_없다()
        {
            var targets = PolicyTargetDiscovery.Find(RealOutputRoot());
            Assert.Equal(14, targets.Count);

            var sources = targets
                .Select(PolicyCorpusLoader.Load)
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            var draftMarkdown = SettlementProcessRosterDraft.Build(sources);
            var roster = SettlementProcessRosterParser.Parse(draftMarkdown);

            var defects = SettlementRosterReconciler.Reconcile(
                roster, sources.Select(s => s.Label).ToList());

            Assert.All(defects, d => Assert.Equal(RosterDefectType.PlaceholderTitleRemaining, d.Type));
        }

        /// <summary>
        /// 자기 워크트리(또는 체크아웃)에 걸린 `output/`. 코퍼스가 없으면 건너뛴다 -
        /// 다른 코퍼스 테스트와 같은 관례다(CorpusSkip.Reason이 심링크 넷을 안내한다).
        /// </summary>
        private static string RealOutputRoot()
        {
            var root = CorpusPaths.RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var outputRoot = Path.Combine(root, "output");
            Skip.IfNot(Directory.Exists(outputRoot), CorpusSkip.Reason);
            return outputRoot;
        }
    }
}
