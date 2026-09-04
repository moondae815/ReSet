using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 골격 패치 재생성의 기계 가드. 프롬프트의 「나머지는 바이트 그대로」 계약이
    /// 지켜졌는지를 사람이 아니라 기계가 본다 - 이 저장소에서 프롬프트 계약만으로
    /// 선 것은 조용히 무너진 전례가 있다(규칙 3-1·10의 기계 강제 0건 시절).
    ///
    /// 가드가 요구하는 것은 「직전 골격이 실제로 담고 있던 것」뿐이다. 목차가 아는
    /// 전부를 요구하면, 애초에 빠져 있던 앵커를 패치가 새로 만들어 내라고 조르게
    /// 되고 그것은 정합 검사가 아니라 새 요구다.
    /// </summary>
    public class SkeletonRevisionGuardTests
    {
        private static BatchStepPlan Step(string code, params string[] errorCodes) =>
            new(code, $"{code} 단계",
                LegacyProcedures: new[] { $"dbo.UP_{code}" },
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: errorCodes,
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        private const string Previous = """
            ## 통합 배치 아키텍처 개요

            | 순서 | 단계 | 허용 오류 코드 |
            |---|---|---|
            | S01 | 잠금 | `-1`, `-10` |
            | S02 | 정산 | `-15` |

            ## Mermaid 기반 통합 흐름도

            ```mermaid
            flowchart TD
            A-->B
            ```

            ## 단계별 이행 상세 및 의사코드

            ### 공통 SQL 오류 추적 패턴

            <!-- STEP:S01 -->
            <!-- STEP:S02 -->

            ## 통합 데이터 정합성 검증 SQL 세트

            ```sql
            SELECT 1;
            ```
            """;

        private static readonly BatchStepPlan[] Steps = { Step("S01", "-1", "-10"), Step("S02", "-15") };

        [Fact]
        public void UnchangedSkeleton_LosesNothing()
        {
            Assert.Empty(SkeletonRevisionGuard.FindLostAnchors(Previous, Previous, Steps));
        }

        // 지적된 자리만 고친 정상 패치. 화살표 하나가 바뀌었을 뿐 두 표는 그대로다.
        [Fact]
        public void PatchThatOnlyFixesTheDiagram_LosesNothing()
        {
            var revised = Previous.Replace("A-->B", "A --> B");

            Assert.Empty(SkeletonRevisionGuard.FindLostAnchors(Previous, revised, Steps));
        }

        // 정합이 깨지는 자리 ①: 오류 코드 표에서 코드가 사라진다. 그대로 두면 다음
        // 회차의 L1이 오류 코드 누락으로 발화하고, 그 귀속은 멀쩡한 단계를 연다.
        [Fact]
        public void DroppedErrorCode_IsReported()
        {
            var revised = Previous.Replace("| S02 | 정산 | `-15` |", "| S02 | 정산 |  |");

            var lost = SkeletonRevisionGuard.FindLostAnchors(Previous, revised, Steps);

            Assert.Contains(lost, anchor => anchor.Contains("-15"));
        }

        // 정합이 깨지는 자리 ②: 단계 목록 표에서 단계가 사라진다. 이미 만들어진
        // 그 단계 섹션은 동결된 채 남아 있으므로 문서가 스스로 모순된다.
        [Fact]
        public void DroppedStepCode_IsReported()
        {
            var revised = Previous
                .Replace("| S02 | 정산 | `-15` |", string.Empty)
                .Replace("<!-- STEP:S02 -->", string.Empty);

            var lost = SkeletonRevisionGuard.FindLostAnchors(Previous, revised, Steps);

            Assert.Contains(lost, anchor => anchor.Contains("S02"));
        }

        [Fact]
        public void DroppedRequiredHeader_IsReported()
        {
            var revised = Previous.Replace("## 통합 데이터 정합성 검증 SQL 세트", "## 검증");

            var lost = SkeletonRevisionGuard.FindLostAnchors(Previous, revised, Steps);

            Assert.Contains(lost, anchor => anchor.Contains("통합 데이터 정합성 검증 SQL 세트"));
        }

        // 가드의 기준은 목차가 아니라 직전 골격이다. 직전 골격에도 없던 코드를
        // 패치에 요구하면 그것은 정합 검사가 아니라 새 요구다 - 패치 한 번에
        // 고칠 수 없는 것을 계속 조르면서 예산만 태운다.
        [Fact]
        public void AnchorAbsentInPrevious_IsNotDemanded()
        {
            var steps = new[] { Step("S01", "-1", "-10"), Step("S02", "-15"), Step("S03", "-99") };
            var revised = Previous.Replace("A-->B", "A --> B");

            var lost = SkeletonRevisionGuard.FindLostAnchors(Previous, revised, steps);

            Assert.Empty(lost);
        }

        // 부분 문자열 대조로 하면 `-1`이 `-10` 안에서 걸려 가드가 조용히 무력해진다 -
        // MechanicalValidator.ContainsToken이 이미 이 함정을 문서로 남긴 자리다.
        [Fact]
        public void CodeSurvivingOnlyAsSubstring_IsStillReportedAsLost()
        {
            var revised = Previous.Replace("| S01 | 잠금 | `-1`, `-10` |", "| S01 | 잠금 | `-10` |");

            var lost = SkeletonRevisionGuard.FindLostAnchors(Previous, revised, Steps);

            Assert.Contains(lost, anchor => anchor.Contains("-1") && !anchor.Contains("-10"));
        }

        [Fact]
        public void NullPrevious_LosesNothing()
        {
            Assert.Empty(SkeletonRevisionGuard.FindLostAnchors(null, Previous, Steps));
        }
    }
}
