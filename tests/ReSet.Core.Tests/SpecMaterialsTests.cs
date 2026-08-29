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

        /// <summary>
        /// [왜 이 테스트인가 - Fix Round 1, 2026-08-29 리뷰 Important] `ReadsSpecMarkdown`은
        /// `DdlCounterpart`와 대칭인 손 적은 판정이다 - 리뷰가 잡아낸 결함은 정확히
        /// 이 판정이 손으로 틀리게 적혀도 컴파일러가 못 잡는다는 것이었다
        /// (`SpecConditions`·`RoundingShapes`·`SpecReturnCodes`가 명세서를 실제로
        /// 읽는데도 "안 읽는다"로 뭉개져 있었다). 이 테스트는 그 판정을 리플렉션으로
        /// 기계 확인한다 - `ReaderTypeName`이 가리키는 타입의 `Extract` 메서드가 실제로
        /// 명세서 튜플 `(string FileName, string Content)`을 인자로 받는지 직접 보고,
        /// 카탈로그의 값과 대조한다. `StepTableSets`만 `SpDefinition`을 받아 이 모양이
        /// 아니다 - 그것이 유일하게 "안 읽는다"여야 하는 이유다.
        /// </summary>
        [Fact]
        public void ReadsSpecMarkdown_MatchesTheExtractMethodsParameterType()
        {
            foreach (var material in SpecMaterials.All)
            {
                var readerType = typeof(SpecMaterials).Assembly.GetTypes()
                    .Single(t => t.Name == material.ReaderTypeName);
                var extract = readerType.GetMethod("Extract", BindingFlags.Public | BindingFlags.Static);
                Assert.True(extract != null,
                    $"{material.ReaderTypeName}에 public static Extract가 없습니다.");

                var parameterType = extract!.GetParameters().Single().ParameterType;
                var elementType = parameterType.GetGenericArguments().Single();
                var actuallyReadsSpecMarkdown =
                    elementType.IsGenericType
                    && elementType.GetGenericTypeDefinition() == typeof(ValueTuple<,>)
                    && elementType.GetGenericArguments().SequenceEqual(new[] { typeof(string), typeof(string) });

                Assert.True(material.ReadsSpecMarkdown == actuallyReadsSpecMarkdown,
                    $"{material.Name}: 카탈로그는 ReadsSpecMarkdown={material.ReadsSpecMarkdown}로 " +
                    $"적었지만, {material.ReaderTypeName}.Extract의 인자 타입을 보면 그것이 " +
                    $"{(actuallyReadsSpecMarkdown ? "참" : "거짓")}입니다.");
            }
        }

        /// <summary>
        /// [왜 이 테스트인가 - Fix Round 1, 2026-08-29 리뷰 Important 3] `ReaderTypeName`은
        /// `ReadsSpecMarkdown_MatchesTheExtractMethodsParameterType`가, `ConsumingChecks`는
        /// `EveryNamedCheck_ExistsOnMechanicalValidator`가, `Enforced`는
        /// `EveryEnforcedMaterial_HasItsHeadingInMachineConfirmedTables`가 잠근다.
        /// `DdlCounterpart`만 기계 확인이 없었다 - 「잴 수 없음/안 쟀음」을 가르는 값인데도
        /// 오기 하나가 그 재료를 영구히 "잴 수 없음"으로 인쇄해 다음 회차의 범위에서
        /// 조용히 빼 버릴 수 있었다. `LocalVariables`의 `DdlCounterpart`가 한때 손으로 적은
        /// 문자열 리터럴("SpecMaterialCensus")이었던 것이 정확히 그 위험이 실물로 있었다는
        /// 증거다 - 값 자체는 우연히 맞았지만 오타가 나도 이 테스트가 없으면 아무도 몰랐다.
        /// </summary>
        [Fact]
        public void DdlCounterpart_NamesATypeThatActuallyExistsInTheAssembly()
        {
            var typeNames = typeof(SpecMaterials).Assembly.GetTypes()
                .Select(t => t.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var material in SpecMaterials.All.Where(m => m.DdlCounterpart != null))
            {
                Assert.True(typeNames.Contains(material.DdlCounterpart!),
                    $"{material.Name}: DdlCounterpart `{material.DdlCounterpart}`가 어셈블리에 " +
                    "실재하는 타입이 아닙니다 - 오기이거나 지워진 타입입니다.");
            }
        }

        /// <summary>
        /// [왜 이 테스트인가 - Fix Round 1, 2026-08-29 리뷰 Important 4] 카탈로그 잠금
        /// (`EverySpecReader_IsListedInTheCatalog`)은 `Spec*Extractor` 타입 이름 집합만
        /// 대조한다. 그런데 재료 여덟 중 넷은 리더 하나(`SpecStatementFactsExtractor`)가
        /// 낸다 - `SpecStatementFacts`의 public 멤버 넷(`DmlRows`·`SetTargets`·
        /// `LocalVariables`·`ErrorCodeToOrdinal`)이 각각 한 재료다. 누가 그 레코드에
        /// 다섯째 멤버를 더하면 리더 이름은 그대로라 `EverySpecReader_IsListedInTheCatalog`는
        /// 계속 초록이고, 카탈로그는 여덟에 머물고, census는 그 재료를 안 세고, 보고서에
        /// 행조차 안 생긴다 - `SpecMaterialCensusTests.Count_EveryMaterialInCatalog_AppearsAsARow`가
        /// "가장 조용한 실패 양식"이라 부르는 그것인데, 그 테스트는 카탈로그 대비 census만
        /// 보지 코드(SpecStatementFacts 레코드) 대비 카탈로그는 안 본다. 이 테스트가 그
        /// 마지막 틈을 리플렉션으로 막는다.
        /// </summary>
        [Fact]
        public void SpecStatementFactsPublicMembers_AreAllListedInTheCatalog()
        {
            var recordMemberNames = typeof(SpecStatementFacts)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            var catalogNamesForThisReader = SpecMaterials.All
                .Where(m => m.ReaderTypeName == nameof(SpecStatementFactsExtractor))
                .Select(m => m.Name)
                .ToHashSet(StringComparer.Ordinal);

            var missing = recordMemberNames.Except(catalogNamesForThisReader).ToList();
            Assert.True(missing.Count == 0,
                "SpecStatementFacts의 public 멤버가 카탈로그(SpecMaterials.All)에 없습니다: " +
                $"{string.Join(", ", missing)}");
        }
    }
}
