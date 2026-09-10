using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 매핑 표가 <b>있는데</b> 헤딩 레벨만 한 단계 깊을 때, 검사가 무어라 말하는지 본다.
    ///
    /// [실측 - 2026-09-10 dbo.UP_UTIL_SETTLE_EXCEPTION_PROC 재생성, 6 회 소진]
    /// 그 판을 끝낸 것이 이 검사다(시도 1·2·5·6 발화, 마지막까지 남아 L1 미통과본이
    /// 배송됐다). 그런데 <b>매핑 표는 18 개가 내용까지 온전히 있었다</b> —
    /// `#### UPDATE 대상 테이블: …`(레벨 4)로 썼을 뿐이다. `UpdateHeadingPrefix` 는
    /// `### `(레벨 3)를 요구하므로 `CollectUpdateSections` 가 0 건을 내고, 메시지는
    /// **「매핑 표가 없습니다」**라고 말한다. 그것은 사실이 아니다.
    ///
    /// 문구가 진짜 원인(`#` 한 글자)을 한 마디도 안 하므로 모델은 이미 쓴 표를 다시
    /// 쓰고 헤딩은 동전 던지기로 남는다 — 시도 3·4 에서는 맞혔고 5 에서 되돌아갔다.
    /// 이 저장소는 같은 부류를 이미 결함으로 인정하고 고쳤다(<c>1e362c0b</c> — 「칸을
    /// 지우라」로 읽히던 붙은 표 행 문구를 「두 행이 붙었으니 나누라」로).
    ///
    /// [발화 자체는 옳다] 계약은 레벨 3 이다 — 프롬프트가 `### ` 예시를 주고
    /// `CollectUpdateSections` 가 그것을 읽는다. 문서는 실제로 틀렸다. 고칠 것은
    /// **판정이 아니라 문구**다.
    ///
    /// 픽스처는 배송본에서 오려 왔다(`output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/
    /// docs/Spec.md` 99~104 행, 한 글자도 안 고쳤다 — `diff` 로 확인).
    /// </summary>
    public class UpdateMappingHeadingLevelRegressionTests
    {
        private static string LoadFixture(string name) =>
            File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", name));

        private const string Table = "SETTLE_POQ_DB.dbo.TSettleMst";

        /// <summary>
        /// 픽스처가 실제로 담은 두 컬럼만 기대한다. 컬럼 누락은 이 시험의 관심사가
        /// 아니다 — 헤딩 레벨 하나만 재려는 것이므로 다른 발화가 섞이면 안 된다.
        /// </summary>
        private static SpecExpectations ExpectTSettleMst() =>
            new(
                new List<UpdateColumnExpectation>
                {
                    new(Table, new List<string> { "DiscountFlag", "DiscountAmt" })
                },
                new Dictionary<string, IReadOnlySet<string>>(),
                new HashSet<string>(),
                new List<string>());

        private static string WrapSpec(string crudBody) =>
            string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용",
                "## CRUD 분석", crudBody,
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });

        private static string MappingError(string crudBody)
        {
            var result = new MechanicalValidator().Validate(WrapSpec(crudBody), ExpectTSettleMst());
            return Assert.Single(result.DetailedErrors
                .Where(e => e.Type == ErrorType.UpdateMappingMissing)
                .Select(e => e.Message));
        }

        // ① 실재하지 않는 부재를 주장하면 안 된다. 표는 있다.
        [Fact]
        public void WhenTheHeadingIsOneLevelDeeper_ShouldNotClaimTheTableIsAbsent()
        {
            var error = MappingError(LoadFixture("ExceptionProcUpdateHeadingLevelExcerpt.md"));

            Assert.DoesNotContain("매핑 표가 없습니다", error);
        }

        // ② 진짜 원인을 말해야 한다 - 모델이 고칠 자리는 `#` 한 글자다.
        [Fact]
        public void WhenTheHeadingIsOneLevelDeeper_ShouldNameTheHeadingLevel()
        {
            var error = MappingError(LoadFixture("ExceptionProcUpdateHeadingLevelExcerpt.md"));

            Assert.Contains("####", error);
            Assert.Contains("###", error);
            Assert.Contains(Table, error);
        }

        // ③ 진짜 부재는 여전히 「없다」고 말해야 한다. ①을 고치면서 이 자리를 잃으면
        //    검사가 자기 존재 이유를 놓친다.
        [Fact]
        public void WhenTheSectionIsGenuinelyAbsent_ShouldStillSayItIsMissing()
        {
            var error = MappingError("UPDATE 대상 테이블의 금액 컬럼을 -1배 처리합니다.");

            Assert.Contains("매핑 표가 없습니다", error);
            Assert.DoesNotContain("####", error);
        }

        // ④ 올바른 레벨 3 은 아무 발화도 없어야 한다 - 오탐 고정.
        [Fact]
        public void WhenTheHeadingIsAtLevelThree_ShouldStaySilent()
        {
            var body = LoadFixture("ExceptionProcUpdateHeadingLevelExcerpt.md")
                .Replace("#### UPDATE 대상 테이블:", "### UPDATE 대상 테이블:");

            var result = new MechanicalValidator().Validate(WrapSpec(body), ExpectTSettleMst());

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.UpdateMappingMissing);
        }

        // ⑤ [작성 계약 9] 시정 문구에 백틱 토큰을 실으면 그것이 귀속 어휘가 된다.
        //    `####`·`###` 를 실으면서 귀속 어휘를 직접 안 실으면, 재생성이 그 기호를
        //    문서에서 되찾으려 해 엉뚱한 자리를 지목한다.
        [Fact]
        public void TheErrorShouldCarryTheTableNameAsItsAttributionLexeme()
        {
            var result = new MechanicalValidator()
                .Validate(WrapSpec(LoadFixture("ExceptionProcUpdateHeadingLevelExcerpt.md")), ExpectTSettleMst());

            var detailed = Assert.Single(
                result.DetailedErrors, e => e.Type == ErrorType.UpdateMappingMissing);

            var lexemes = MechanicalValidator.ViolationLexemes(detailed);
            Assert.Contains(Table, lexemes);
            Assert.DoesNotContain(lexemes, l => l.Contains("#"));
        }
    }
}
