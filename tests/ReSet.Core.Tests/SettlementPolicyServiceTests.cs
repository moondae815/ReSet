using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private static string Stage(int n, string title, string label, string quote) =>
            $"## {n}. {title}\n\n산문 개요.\n\n"
            + PolicySectionContract.TableHeader + "\n" + PolicySectionContract.TableSeparator + "\n"
            + $"| S{n}-01 | 업무 규칙 | {label} · ## 개요 > \"{quote}\" | {PolicySectionContract.NoCodeValue} |\n";

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
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "settlement-process.md")));
        }

        [Fact]
        public async Task DB없이_완주하고_문서와_사전을_남긴다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

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
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

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
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

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
                AiWriting(Stage(1, "요율 적재 (선행)", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

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
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

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
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "원문에 없는 구절"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Contains(outcome.Defects, d => d.Type == PolicyDefectType.EvidenceQuoteNotFound);
            Assert.Contains("귀속", File.ReadAllText(outcome.PolicyPath));
        }

        [Fact]
        public async Task 한_번도_인용되지_않은_SP를_배너에_싣는다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_A", "요율을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Contains(outcome.Defects, d => d.Type == PolicyDefectType.ProcedureNeverCited);
            Assert.Contains("dbo.UP_B", File.ReadAllText(outcome.PolicyPath));
        }

        [Fact]
        public async Task DB가_없으면_코드값_번역_0건을_배너에_명시한다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Contains("DB 미연결", File.ReadAllText(outcome.PolicyPath));
        }

        [Fact]
        public async Task 문서는_검증_없음으로_표기된다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

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
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.True(File.Exists(outcome.PolicyPath));
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
                Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재"));
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
    }

    /// <summary>
    /// 좌표자 권고 ④ - 교차 태스크 불변식을 영구 테스트로 못박는다.
    ///
    /// 실물 코퍼스에서 만든 초안 명부를 파서와 대조기에 태우면 PlaceholderTitleRemaining
    /// 외의 결함이 없어야 한다. 절대 경로를 쓴다 - 상대 경로는 테스트 호스트 cwd로 풀려
    /// 픽스처 1편만 재고도 초록이 되는 함정이 실제로 있었다(좌표자가 밟았다). 이 클래스는
    /// 읽기만 하므로 임시 디렉터리가 필요 없다.
    /// </summary>
    public sealed class SettlementRosterDraftCrossTaskInvariantTests
    {
        private const string RealOutputRoot = "/Users/payletter/git-root/ReSet/output";

        [Fact]
        public void 실물_코퍼스의_대상_수는_14다()
        {
            var targets = PolicyTargetDiscovery.Find(RealOutputRoot);

            Assert.Equal(14, targets.Count);
        }

        [Fact]
        public void 실물_코퍼스_초안_명부는_PlaceholderTitleRemaining_외의_결함이_없다()
        {
            var targets = PolicyTargetDiscovery.Find(RealOutputRoot);
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
    }
}
