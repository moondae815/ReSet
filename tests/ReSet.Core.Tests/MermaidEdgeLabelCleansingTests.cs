using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 엣지 레이블 정화의 계약을 못박는다.
    ///
    /// [왜 이 시험이 필요한가 - 2026-09-07]
    /// <c>CleanseMermaidCode</c> 규칙 1 이 엣지 레이블의 큰따옴표를 <b>무조건 벗겼다.</b>
    /// mermaid 는 레이블에 `(` 같은 특수문자가 있으면 따옴표를 요구하므로, 모델이 올바르게
    /// 쓴 다이어그램이 도구를 지나며 부서졌다. `mmdc` 11.16.0 실측:
    /// <c>--&gt;|일치 행 없음 (0행)|</c> 는 파스 실패, <c>--&gt;|"일치 행 없음 (0행)"|</c> 는
    /// 컴파일 성공이다.
    ///
    /// 노드 레이블에는 <b>씌우는</b> 규칙이 있는데(규칙 4) 엣지에는 없고 오히려 벗겼다.
    /// 배송본 <c>Spec.md</c> 전량의 엣지 레이블 280 개 중 따옴표 있는 것이 <b>0 개</b>이고,
    /// 정화 전 출력인 <c>docs/Thinking.md</c> 에는 모델이 쓴 따옴표가 남아 있다 -
    /// 「모델이 안 썼다」가 아니라 「썼는데 벗겨졌다」다.
    ///
    /// 이 저장소가 정화기에 물린 <b>세 번째</b> 자리다(첫째는 `ad90c004` 의 sequenceDiagram).
    /// 그래서 아래 다섯째·여섯째는 <b>사 주던 것을 안 잃었는지</b>를 지킨다.
    /// </summary>
    public sealed class MermaidEdgeLabelCleansingTests
    {
        private readonly MechanicalValidator _validator = new MechanicalValidator();

        private string Cleanse(string mermaidBody) =>
            _validator.PostProcessMarkdown("```mermaid\n" + mermaidBody + "\n```");

        [Fact]
        public void 요구1_괄호가_있는_무따옴표_엣지_레이블은_따옴표로_감싼다()
        {
            var result = Cleanse("flowchart TD\n    A{\"조건\"} -->|일치 행 없음 (0행)|B[\"대입 안 함\"]");

            Assert.Contains("-->|\"일치 행 없음 (0행)\"|", result);
        }

        [Fact]
        public void 요구2_이미_따옴표가_있는_엣지_레이블은_보존된다()
        {
            var result = Cleanse("flowchart TD\n    A{\"조건\"} -->|\"아니오 (무결과)\"|B[\"대입 안 함\"]");

            Assert.Contains("-->|\"아니오 (무결과)\"|", result);
        }

        [Fact]
        public void 요구3_엣지_레이블의_하이픈은_보존된다()
        {
            // 현행은 Replace("-", "") 로 무조건 지운다 - `1-5 범위`가 `15 범위`가 되는 내용 훼손이다.
            var result = Cleanse("flowchart TD\n    A[\"시작\"} -->|1-5 범위|B[\"끝\"]".Replace("}", "]"));

            Assert.Contains("1-5 범위", result);
            Assert.DoesNotContain("15 범위", result);
        }

        [Fact]
        public void 요구4_특수문자가_없는_레이블은_따옴표_없이_남는다()
        {
            var result = Cleanse("flowchart TD\n    A[\"시작\"] -->|성공|B[\"끝\"]");

            Assert.Contains("-->|성공|", result);
            Assert.DoesNotContain("-->|\"성공\"|", result);
        }

        [Fact]
        public void 요구5_화살표_모양_보정은_여전히_산다()
        {
            // 규칙 1 이 조용히 사 주던 것 - 이것을 잃으면 고치는 쪽이 커버리지를 버린 것이다.
            var result = Cleanse("flowchart TD\n    C -- \"Label\" --> D");

            Assert.Contains("-->|Label|", result);
        }

        [Fact]
        public void 요구6_노드_레이블_동작은_불변이다()
        {
            var result = Cleanse("graph TD\n    A_1[Invalid (Text)] - -> B_2{Condition : Check}");

            Assert.Contains("A1[\"Invalid (Text)\"]", result);
            Assert.Contains("B2{\"Condition : Check\"}", result);
        }
    }
}
