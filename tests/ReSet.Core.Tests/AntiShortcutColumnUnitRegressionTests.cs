using System.IO;
using System.Linq;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Anti-Shortcut 검사(<see cref="MechanicalValidator"/> Anti-Shortcut 갈래)가
    /// <b>줄 어디든</b> 토큰이 있으면 걸리던 것을, <b>값이 들어갔어야 할 자리</b>
    /// (표 행이면 칸, 아니면 줄)로 판정 단위를 바꾼 것을 잠근다.
    ///
    /// [실물 - 2026-09-09 UP_UTIL_SETTLE_EXCEPTION_PROC 재생성 3·4 판]
    /// PGCOMM 과 PGVT 는 하나의 CASE 조건을 공유하는 인접 두 컬럼이다. PGVT 행의
    /// 「설명」 칸은 그 조건을 <b>괄호 안에 글자로 다 풀어 쓴 뒤</b> "위와 동일한
    /// 조건 판단"이라는 접속 표현으로 시작한다 - 칸이 비어 있지 않다, 이미 채워져
    /// 있다. 그런데 종전 검사는 줄 안에 "위와 동일"이 있다는 사실만 보고
    /// "그 칸을 실제 값으로 채우십시오"라고 고발했다. 매 시도 설명을 새로 써도
    /// 같은 접속 표현이 나와(3판 시도 1·2·3, 4판 시도 1·2) 자기강화 루프가 됐다.
    ///
    /// [픽스처 출처 - 정확히 무엇이 실물이고 무엇이 아닌지]
    /// <c>Fixtures/SettleMstPgvtSameConditionExcerpt.md</c>(위 실물 결함) 하나만
    /// <c>output/logs/reset-20260909.log:27418-27422</c> 를 한 글자도 고치지 않고
    /// 오려 왔다(<c>sed -n '27418,27422p'</c>). 아래 <c>SettleMstClvtLiteralShortcutBoundarySample.md</c>
    /// 는 <b>다르다</b> - 원천 칸 자체가 축약어인 진짜 사례를 찾으려고 이 저장소가
    /// gitignore 로 갖고 있는 로그 30 개 전부를
    /// (<c>grep -rnE '\|\s*(이하 생략|\(생략\)|위와 동일|기타 등등)\s*\|'
    /// output/logs/*.log</c>) 뒤졌으나 <b>0 건</b>이었다 - 리뷰 라운드 1 에서
    /// 좌표자가 독립으로 같은 결과를 재현했다. 실물이 없으므로 그 자리는
    /// <b>의도적으로 합성한 경계 표본</b>이고, 파일 이름에서 로그 발췌를 뜻하는
    /// "Excerpt" 를 뺀 것도 그 뜻이다(이 저장소 관례: <c>PlanStructureWithEmptyErrorCodes.md</c>
    /// 도 같은 이유로 "Excerpt" 가 없다). 「이미 이 검사가 닫은 진짜 결함」이라는
    /// 주장은 <c>AntiShortcut_ShouldPointAtTheOffendingLines</c>
    /// (<c>MechanicalValidatorTests.cs</c> 10969행 부근, CLVT/PGCOMM 두 행 모두
    /// 원천 칸이 정확히 "위와 동일")가 이미 실제 프로덕션 코드로 잠그고 있다 -
    /// 아래 합성 표본은 그 잠금을 표 헤더에 스키마 접두사가 붙은 모양으로
    /// 한 번 더 확인하는 것이지, 새로 여는 커버리지가 아니다.
    /// </summary>
    public class AntiShortcutColumnUnitRegressionTests
    {
        private static string LoadFixture(string name) =>
            File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", name));

        private static string Wrap(string crudBody) => string.Join("\n", new[]
        {
            "## 개요", "내용", "## 파라미터 목록", "내용",
            "## CRUD 분석", crudBody,
            "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
            "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
        });

        // 1. 실물 행이 침묵한다 - 원천 칸에 CASE 식이 전부 들어 있고, 설명 칸은
        //    "위와 동일한 조건 판단(...)"으로 조건을 글자로 풀어 쓴 뒤 훨씬 긴
        //    산문이 이어진다. 칸 전체가 "위와 동일"인 것이 아니므로 위반이 아니다.
        [Fact]
        public void RealPgvtRow_WithSpelledOutConditionAfterConnective_ShouldNotFlag()
        {
            var markdown = Wrap(LoadFixture("SettleMstPgvtSameConditionExcerpt.md"));

            var result = new MechanicalValidator().Validate(markdown);

            Assert.True(result.IsValid,
                "Validation failed with errors: " + string.Join(", ", result.Errors));
        }

        // 2. 원천 칸이 정말로 "위와 동일" 인 행은 계속 발화한다 - 커버리지를 함께
        //    버리지 않았다는 증거. 다만 이 표는 합성 경계 표본이다(위 클래스
        //    문서의 [픽스처 출처] 참고 - 로그 30 개 전수 grep 0 건이라 실물이
        //    없다). 이 검사가 원래 닫은 진짜 결함은
        //    AntiShortcut_ShouldPointAtTheOffendingLines(10969행 부근)가 잠근다.
        [Fact]
        public void RowWhoseSourceCellIsLiterallyTheShortcut_ShouldStillFlag()
        {
            var markdown = Wrap(LoadFixture("SettleMstClvtLiteralShortcutBoundarySample.md"));

            var result = new MechanicalValidator().Validate(markdown);

            Assert.False(result.IsValid);
            Assert.Contains("허용되지 않는 축약어/생략 기호", string.Join(" ", result.Errors));
        }

        // 3. 표가 아닌 맨 산문 줄의 "위와 동일"은 계속 발화한다 - 기존 시험
        //    (Validate_WithForbiddenShortcuts_ShouldReturnFalse, 295행 부근)이
        //    요구하는 자리다. "표 칸만 본다"로 좁히면 이 자리가 깨진다.
        [Fact]
        public void StandaloneProseLine_ThatIsEntirelyTheShortcut_ShouldStillFlag()
        {
            var markdown = Wrap("이 절차는 앞서 기술한 내용과 같습니다.\n\n위와 동일\n");

            var result = new MechanicalValidator().Validate(markdown);

            Assert.False(result.IsValid);
            Assert.Contains("허용되지 않는 축약어/생략 기호", string.Join(" ", result.Errors));
        }

        // 4. 메시지가 그 자리를 지목한다 - 기존 문구의 "N자리에 있습니다: …" 계약을
        //    지킨다. 위반 자리(CLVT 행 - 합성 경계 표본, 위 [픽스처 출처] 참고)의
        //    원문 조각이 메시지에 그대로 나와야 모델이 그 줄을 찾아갈 수 있다.
        [Fact]
        public void Message_ShouldQuoteTheOffendingLine()
        {
            var markdown = Wrap(LoadFixture("SettleMstClvtLiteralShortcutBoundarySample.md"));

            var result = new MechanicalValidator().Validate(markdown);

            var error = Assert.Single(result.Errors, e => e.Contains("허용되지 않는 축약어/생략 기호"));
            Assert.Contains("1자리", error);
            Assert.Contains("CLVT", error);
        }
    }
}
