using System;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// mermaid-cli 의 stderr 를 배송 문서에 실을 만큼만 남기는 계약.
    ///
    /// [왜 - 2026-09-07 축 A 감사]
    /// `ValidateMermaid` 가 stderr 를 <b>통째로</b> 오류 메시지에 이어 붙였고, 그 메시지가
    /// 배너를 타고 배송 문서에 실렸다 - puppeteer 내부 프레임 10 여 줄과 로컬 homebrew
    /// 절대경로, `mermaid-cli-intercept.invalid` 라는 없는 호스트까지. 실측으로 세 편이다
    /// (`UF_GET_PGCommOption`·`UF_GET_COMM4CLIENT4INTEREST`·`UF_GET_COMM4CLIENT4PARTIALCANCEL`).
    /// 그 문서는 마이그레이션 코딩 에이전트가 읽는 <b>입력</b>이다.
    ///
    /// 전체 stderr 는 잃지 않는다 - 바로 옆 <c>Log.Warning(… Stderr: {Stderr})</c> 가 그대로
    /// 남긴다. 사람이 볼 자리는 로그다.
    ///
    /// [왜 「앞 N 줄」로 안 자르는가]
    /// 줄 수로 박으면 mmdc 출력 형식이 바뀔 때 조용히 낡는다. 스택 프레임 줄의 모양으로
    /// 자르고, <b>프레임이 하나도 안 잡히면 전체를 그대로 둔다</b> - 「이 형식에는 X 가
    /// 없다」는 단정을 쓰지 않는 쪽으로 실패시킨다.
    ///
    /// 픽스처는 <b>실행 로그에서 오려 왔다</b>(`Fixtures/MermaidCliStderrExcerpt.txt`,
    /// 2026-09-07 19:17:27 판). 규격 기억으로 지어내면 내 오해를 시험이 확인해 준다.
    /// </summary>
    public sealed class RendererDiagnosticsTests
    {
        private static string RealStderr() => File.ReadAllText(Path.Combine(
            RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures",
            "MermaidCliStderrExcerpt.txt"));

        [Fact]
        public void 요구1_첫_스택_프레임에서_자른다()
        {
            var trimmed = RendererDiagnostics.TrimStackTrace(RealStderr());

            Assert.DoesNotContain("Parser.parseError (https://", trimmed);
            Assert.DoesNotContain("at async CdpPage.$eval", trimmed);
            Assert.DoesNotContain("/opt/homebrew/", trimmed);
        }

        [Fact]
        public void 요구2_진단_머리_네_줄은_보존된다()
        {
            var trimmed = RendererDiagnostics.TrimStackTrace(RealStderr());

            Assert.Contains("Error: Parse error on line 5:", trimmed);
            Assert.Contains("If1 -->|아니오 (1차 조회 결과 있음, IF 분기", trimmed);
            Assert.Contains("^", trimmed);
            Assert.Contains("Expecting 'SQE'", trimmed);
            Assert.Equal(4, trimmed.Split('\n').Length);
        }

        [Fact]
        public void 요구3_스택_프레임이_없으면_전체가_남는다()
        {
            var noFrames = "Error: something went wrong\ndetail line 1\ndetail line 2";

            Assert.Equal(noFrames, RendererDiagnostics.TrimStackTrace(noFrames));
        }

        [Fact]
        public void 요구4_프레임이_없어도_줄_수_상한을_넘으면_자른다()
        {
            var many = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}"));

            var trimmed = RendererDiagnostics.TrimStackTrace(many);
            var lines = trimmed.Split('\n');

            Assert.Equal("line 12", lines[11]);
            Assert.DoesNotContain("line 13", trimmed);
            Assert.Contains("생략", lines[^1]);
        }

        [Fact]
        public void 요구5_배너의_여러_줄_항목은_모든_줄이_인용_안에_있다()
        {
            var banner = VerificationBanner.L1Exhausted(new[] { "첫 줄\n둘째 줄\n셋째 줄" });

            var bodyLines = banner.Split('\n')
                .Where(l => l.Trim().Length > 0)
                .ToList();

            Assert.All(bodyLines, l => Assert.StartsWith(">", l));
            Assert.Contains(bodyLines, l => l.Contains("둘째 줄"));
        }

        [Fact]
        public void 요구6_한_줄_항목의_형식은_불변이다()
        {
            var banner = VerificationBanner.L1Exhausted(new[] { "단일 오류" });

            Assert.Contains(">   - 단일 오류", banner);
        }

        /// <summary>
        /// 자르기가 <b>재시도 프롬프트</b>의 진단까지 베면 아무도 모르게 진단이 0 이 된다.
        /// 동료 세션(reset-2a)이 착수 직전에 짚어 준 축이다 - 오늘 확인한 것이 「여섯 시도가
        /// 그 캐럿을 보고도 못 고쳤다」인데, 정화기가 원인을 없앤 지금 <b>캐럿이 다음
        /// 재생성의 유일한 진단 통로</b>다. 발화 0 오독과 같은 모양으로 조용해질 수 있다.
        /// </summary>
        [Fact]
        public void 요구7_재시도_프롬프트에_파스_오류와_캐럿이_남는다()
        {
            var message = "Mermaid 다이어그램이 렌더러에서 컴파일되지 않습니다. "
                + "아래 컴파일 로그의 줄 번호와 캐럿(^)이 가리키는 자리를 고치십시오. "
                + RendererDiagnostics.TrimStackTrace(RealStderr());

            var result = new ValidationResult
            {
                DetailedErrors =
                {
                    new DetailedError { Type = ErrorType.MermaidCliError, Message = message }
                }
            };

            var promptFix = result.SuggestedPromptFix;

            Assert.Contains("Parse error on line 5:", promptFix);
            Assert.Contains("^", promptFix);
            Assert.Contains("Expecting 'SQE'", promptFix);
            Assert.DoesNotContain("/opt/homebrew/", promptFix);
        }
    }
}
