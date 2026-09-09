using System.IO;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 기계 확정 표에서 <b>두 행이 한 줄로 붙었을 때</b> 검사가 무엇이라 말하는지 본다.
    ///
    /// [실물 - 2026-09-09 EXCEPTION_PROC 세 번째 재생성, 시도 1]
    /// 프롬프트가 준 <c>### 변수 대입</c> 표는 <b>깨끗했다</b>. 모델이 줄바꿈 하나를
    /// 빠뜨려 라인 122 행과 라인 153 행이 한 줄로 붙었을 뿐이다:
    /// <code>| 122 | @po_intRetVal | -1 | 153 | @po_intRetVal | -1 |</code>
    ///
    /// 종전 문구는 두 가지가 없었다:
    ///   ① <b>위치</b> — 「5번째 행이 8칸」만 준다. 그 표는 19 행이고 전부
    ///      <c>| N | @po_intRetVal | -M |</c> 로 같은 모양이라 서수만으로는 못 찾는다.
    ///      게다가 서수 기준이 헤더·구분줄을 포함한 물리 행이라 사람이 세는 데이터
    ///      3 행째와 어긋난다.
    ///   ② <b>옳은 방향</b> — 「헤더와 같은 칸 수로 옮기십시오」는 <b>칸을 지우라</b>는
    ///      뜻으로 읽힌다. 실제 원인은 두 행이 붙은 것이고 옳은 처방은 <b>줄을 나누라</b>다.
    ///      「수정 금지」 표에서 칸을 지우면 확정된 행 하나가 통째로 사라진다 —
    ///      검사가 더 나쁜 결함을 지시하게 된다.
    ///
    /// 붙은 자리는 기계가 알 수 있다. 초과 칸이 본문 열 수의 배수면 N 행이 붙은 것이고,
    /// 나눈 결과까지 그대로 만들어 줄 수 있다 — 모델이 추측할 것을 남기지 않는다.
    ///
    /// 픽스처는 실행 로그에서 한 글자도 고치지 않고 오려 왔다.
    /// </summary>
    public class MergedTableRowRegressionTests
    {
        private static string LoadFixture(string name) =>
            File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", name));

        private static string ShapeError()
        {
            var markdown = string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용",
                "## CRUD 분석", LoadFixture("ExceptionProcMergedTableRowExcerpt.md"),
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });

            var result = new MechanicalValidator().Validate(markdown);

            return Assert.Single(result.DetailedErrors
                .Where(e => e.Type == ErrorType.MachineTableShapeBroken)
                .Select(e => e.Message));
        }

        // ① 어느 줄인지 말해야 한다. 같은 모양 19 행에서 서수만으로는 못 찾는다.
        [Fact]
        public void ShouldQuoteTheOffendingRow()
        {
            var error = ShapeError();

            Assert.Contains("| 122 | @po_intRetVal | -1 | 153 | @po_intRetVal | -1 |", error);
        }

        // ② 무엇을 하라는지 말해야 한다. 붙은 행은 「나누는」 것이지 「지우는」 것이 아니다.
        [Fact]
        public void ShouldPrescribeSplittingNotDeleting()
        {
            var error = ShapeError();

            // 진단 - 무슨 일이 일어났는지.
            Assert.Contains("두 행이 한 줄로 붙었습니다", error);
            // 처방 - 나눈 결과를 그대로 준다. 모델이 추측할 것을 남기지 않는다.
            Assert.Contains("| 122 | @po_intRetVal | -1 |\n| 153 | @po_intRetVal | -1 |", error);
        }

        // 「수정 금지」 표에서 칸을 지우면 확정된 행이 사라진다 - 그 방향을 말하면 안 된다.
        [Fact]
        public void ShouldNotTellTheModelToDropCells()
        {
            var error = ShapeError();

            Assert.DoesNotContain("헤더와 같은 칸 수로 옮기십시오", error);
        }

        // 배수가 아닌 진짜 칸 수 오류(구분 행이 한 칸 모자란 모양)는 종전 처방이 맞다.
        // ①②를 고치면서 이 자리를 잃으면 안 된다.
        [Fact]
        public void WhenCellCountIsNotAMultiple_ShouldKeepTheOriginalPrescription()
        {
            var markdown = string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용",
                "## CRUD 분석",
                DmlScopeExtractor.DmlScopeTableHeading,
                "| 문장 | 라인 | 대상 |",
                "| :--- | :--- |",
                "| INSERT 1 | 55 | dbo.T |",
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });

            var error = Assert.Single(new MechanicalValidator().Validate(markdown).DetailedErrors
                .Where(e => e.Type == ErrorType.MachineTableShapeBroken)
                .Select(e => e.Message));

            Assert.Contains("헤더와 같은 칸 수로", error);
            // 위치는 이 갈래에도 있어야 한다 - 서수만으로는 못 찾는 것은 마찬가지다.
            Assert.Contains("| :--- | :--- |", error);
        }
    }
}
