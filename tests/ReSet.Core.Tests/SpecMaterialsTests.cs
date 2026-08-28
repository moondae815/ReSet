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
        /// </summary>
        [Fact]
        public void EveryNamedCheck_ExistsOnMechanicalValidator()
        {
            var validator = typeof(MechanicalValidator);
            foreach (var name in SpecMaterials.All.SelectMany(m => m.ConsumingChecks).Distinct())
            {
                var method = validator.GetMethod(
                    name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                Assert.True(method != null,
                    $"SpecMaterials가 이름 댄 검사 `{name}`이 MechanicalValidator에 없습니다.");
            }
        }
    }
}
