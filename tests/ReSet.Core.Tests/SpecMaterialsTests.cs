using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecMaterialsTests
    {
        /// <summary>
        /// [왜 이 테스트인가] 새 Spec*Extractor가 조용히 들어오면 카탈로그가
        /// "전수"이기를 그친다. (5-3-7)의 결함이 바로 "어디에도 안 적혀 있어서
        /// 아무도 몰랐다"였다.
        /// </summary>
        [Fact]
        public void EverySpecReader_IsListedInTheCatalog()
        {
            var readers = typeof(SpecMaterials).Assembly.GetTypes()
                .Where(t => t.IsClass && t.IsAbstract && t.IsSealed) // static class
                .Where(t => t.Name.StartsWith("Spec", StringComparison.Ordinal)
                            && t.Name.EndsWith("Extractor", StringComparison.Ordinal))
                .Select(t => t.Name)
                .ToHashSet(StringComparer.Ordinal);

            var listed = SpecMaterials.All.Select(m => m.ReaderTypeName).ToHashSet(StringComparer.Ordinal);

            Assert.Equal(readers.OrderBy(x => x), listed.Intersect(readers).OrderBy(x => x));
            var missing = readers.Except(listed).ToList();
            Assert.True(missing.Count == 0,
                $"카탈로그에 없는 명세서 리더: {string.Join(", ", missing)}");
        }

        /// <summary>
        /// [왜 이 테스트인가] "강제됨"이 거짓이 되면 다음 사람이 "강제된다니 안심"하고
        /// 지나간다 - 침묵이 관측되지 않는 것과 같은 결과다.
        /// </summary>
        [Fact]
        public void EveryEnforcedMaterial_HasItsHeadingInMachineConfirmedTables()
        {
            var enforcedHeadings = MachineConfirmedTables.All
                .Select(t => t.Heading).ToHashSet(StringComparer.Ordinal);

            foreach (var material in SpecMaterials.All.Where(m => m.Enforced))
            {
                Assert.All(material.SectionHeadings, heading =>
                    Assert.True(enforcedHeadings.Contains(heading),
                        $"{material.Name}은 강제됨으로 표시됐으나 헤딩 `{heading}`이 " +
                        "MachineConfirmedTables.All에 없습니다."));
            }
        }

        /// <summary>
        /// [왜 이 테스트인가] 이 저장소는 이미 한 번 당했다 - 주석이
        /// `CheckAddedPredicates`라는 저장소에 없는 이름을 댔고, 평문이라 컴파일
        /// 경고가 안 나 조용했다(실제는 CheckAnchoredStatementExtras).
        ///
        /// [왜 Public을 포함하는가 - 물결 1 리뷰 Important, 2026-08-29]
        /// `MechanicalValidator`의 검사 진입점이 전부 `private static`은 아니다 -
        /// `ValidateSplitProcedureObligations`·`FindMissingErrorCodes`는 `public`이고
        /// 어느 재료가 비면 실제로 죽는 1차 소비자다. `NonPublic`만 요구하면 이
        /// 리플렉션 조회 자체가 그 두 이름을 못 찾아 "존재하지 않는다"고 오판하므로,
        /// 카탈로그를 쓰는 사람이 애초에 그 이름을 못 적었다(§3-1이 막으려던 결함과
        /// 같은 모양 - 검사→재료 사상이 코드에 없어서 아무도 몰랐다). `Public`을
        /// 빼면 이 조회가 다시 같은 사각을 만든다 - "private 검사만 세는 게 맞지
        /// 않나" 하고 되돌리지 마십시오.
        /// </summary>
        [Fact]
        public void EveryNamedCheck_ExistsOnMechanicalValidator()
        {
            var validator = typeof(MechanicalValidator);
            foreach (var name in SpecMaterials.All.SelectMany(m => m.ConsumingChecks).Distinct())
            {
                var method = validator.GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                Assert.True(method != null,
                    $"SpecMaterials가 이름 댄 검사 `{name}`이 MechanicalValidator에 없습니다.");
            }
        }

        /// <summary>
        /// [왜 이 테스트인가 - 물결 1 리뷰 Important, 2026-08-29] `StepTableSets`가
        /// 비면 `IsTableOwedOnlyBySplitProcedures`(분할-SP 면제 판정)만 죽는 게
        /// 아니다 - `ValidateSplitProcedureObligations`(MechanicalValidator.cs:570)가
        /// `tablesByProcedure`를 직접 읽어 분할된 SP의 쓰기 대상 테이블이 합본
        /// 단계 본문 어디에도 없는지를 그 자리에서 판정한다(도우미를 거치지 않는다).
        /// 이 1차 소비자가 `public`이라는 이유만으로 카탈로그에서 빠진 적이 있다 -
        /// 이 테스트가 그 회귀를 잠근다.
        /// </summary>
        [Fact]
        public void StepTableSets_ListsItsFirstOrderPublicConsumer()
        {
            var material = SpecMaterials.All.Single(m => m.Name == "StepTableSets");
            Assert.Contains("ValidateSplitProcedureObligations", material.ConsumingChecks);
        }

        /// <summary>
        /// [왜 이 테스트인가 - 물결 1 리뷰 Important, 2026-08-29] `SpecReturnCodes`가
        /// 비면 도우미(`IsOwedOnlyBySplitProcedures`)만 죽는 게 아니다. 서로 다른 두
        /// `public` 검사가 `codesByProcedure`를 도우미 없이 직접 읽는다 -
        /// `FindMissingErrorCodes`(MechanicalValidator.cs:2082, "문서 어디에도 없는가"를
        /// 목차 없이 대조)와 `ValidateSplitProcedureObligations`(같은 클래스 570행,
        /// 분할된 SP의 코드가 합본 단계 본문에 있는지 대조)다. 이 둘이 `public`이라는
        /// 이유만으로 카탈로그에서 빠진 적이 있다 - 이 테스트가 그 회귀를 잠근다.
        /// </summary>
        [Fact]
        public void SpecReturnCodes_ListsBothFirstOrderPublicConsumers()
        {
            var material = SpecMaterials.All.Single(m => m.Name == "SpecReturnCodes");
            Assert.Contains("FindMissingErrorCodes", material.ConsumingChecks);
            Assert.Contains("ValidateSplitProcedureObligations", material.ConsumingChecks);
        }
    }
}
