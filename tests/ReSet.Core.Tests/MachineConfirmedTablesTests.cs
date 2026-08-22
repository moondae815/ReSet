using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「기계 확정 — 수정 금지」 표 목록의 단일 출처가 <see cref="MachineConfirmedTables"/>임을
    /// 지킨다.
    ///
    /// [왜 리플렉션인가] 헤딩은 그냥 문자열 상수라 컴파일러가 등록을 강제하지 못한다.
    /// 표는 지금까지 하나씩 늘어왔고(CacheManager의 버전 주석 4→9가 그 이력이다),
    /// 새 표가 Actor·L1에는 상수로 자동 배선되면서 Critic 프롬프트의 부류 목록에서만
    /// 빠지는 것이 이 검사가 막으려는 모양이다. 그때 그 표는 "세 부류 어디에도 없는 표"가
    /// 되어 보고 가능 범위가 미지정 상태로 모델에게 넘어간다.
    /// </summary>
    public class MachineConfirmedTablesTests
    {
        /// <summary>
        /// ReSet.Core.Services의 모든 문자열 상수 중 기계 확정 표 헤딩으로 보이는 것을 모은다.
        /// 접미사 상수 자신은 헤딩이 아니므로 뺀다.
        /// </summary>
        private static IEnumerable<(string Owner, string Value)> DiscoverHeadingConstants()
        {
            var assembly = typeof(DmlScopeExtractor).Assembly;
            foreach (var type in assembly.GetTypes())
            {
                if (type.Namespace != "ReSet.Core.Services") continue;

                var fields = type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                foreach (var field in fields)
                {
                    if (!field.IsLiteral || field.IsInitOnly) continue;
                    if (field.FieldType != typeof(string)) continue;

                    var value = field.GetRawConstantValue() as string;
                    if (string.IsNullOrEmpty(value)) continue;
                    if (!value.EndsWith(MachineConfirmedTables.HeadingSuffix, StringComparison.Ordinal)) continue;
                    if (value == MachineConfirmedTables.HeadingSuffix) continue;

                    yield return ($"{type.Name}.{field.Name}", value);
                }
            }
        }

        [Fact]
        public void EveryMachineConfirmedHeadingConstant_IsRegisteredInTheCatalog()
        {
            var registered = MachineConfirmedTables.All
                .Select(table => table.Heading)
                .ToHashSet(StringComparer.Ordinal);

            var unregistered = DiscoverHeadingConstants()
                .Where(found => !registered.Contains(found.Value))
                .Select(found => $"{found.Owner} = {found.Value}")
                .ToList();

            Assert.True(
                unregistered.Count == 0,
                "기계 확정 표 헤딩 상수가 카탈로그에 등록되지 않았습니다. "
                + "MachineConfirmedTables.All에 검증 부류와 함께 추가하십시오: "
                + string.Join(", ", unregistered));
        }

        [Fact]
        public void CatalogHasNoEntryWithoutAHeadingConstant()
        {
            // 반대 방향도 본다 - 추출기에서 사라진 표가 카탈로그에 유령으로 남으면
            // Critic 프롬프트가 존재하지 않는 헤딩을 계속 지시한다.
            var discovered = DiscoverHeadingConstants()
                .Select(found => found.Value)
                .ToHashSet(StringComparer.Ordinal);

            var ghosts = MachineConfirmedTables.All
                .Select(table => table.Heading)
                .Where(heading => !discovered.Contains(heading))
                .ToList();

            Assert.True(
                ghosts.Count == 0,
                "카탈로그에 상수가 없는 표가 남아 있습니다: " + string.Join(", ", ghosts));
        }

        [Fact]
        public void EveryExecutionSemanticKindConstant_IsListedInAllKinds()
        {
            // Critic 블록은 실행 의미 표의 종류 칸을 열거해 "이 행들은 보고 대상이 아니다"를
            // 특정한다. 여섯 번째 종류가 생겼는데 이 목록에서 빠지면 그 종류만 조용히
            // 보호 밖으로 나간다 - 인라인 예시 두 개 밖의 종류가 정확히 그 자리였다.
            var declared = typeof(ExecutionSemanticsFacts)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(field => field.IsLiteral && !field.IsInitOnly)
                .Where(field => field.FieldType == typeof(string))
                .Where(field => field.Name.EndsWith("Kind", StringComparison.Ordinal))
                .Select(field => (string)field.GetRawConstantValue()!)
                .ToList();

            Assert.NotEmpty(declared);
            var missing = declared.Where(kind => !ExecutionSemanticsFacts.AllKinds.Contains(kind)).ToList();
            Assert.True(
                missing.Count == 0,
                "실행 의미 종류 상수가 AllKinds에 없습니다: " + string.Join(", ", missing));
        }

        [Fact]
        public void CriticExemptionBlock_ListsEveryTranscriptionTableAsCheckableAgainstTheDdl()
        {
            var block = MachineConfirmedTables.CriticExemptionBlock;

            var transcription = MachineConfirmedTables.All
                .Where(table => table.Verification == MachineConfirmedTableVerification.DdlTranscription)
                .Select(table => table.Heading);
            foreach (var heading in transcription)
            {
                Assert.Contains(heading, block);
            }

            Assert.Contains("differs from the DDL text that the row cites", block);
        }

        [Fact]
        public void CriticExemptionBlock_PutsExecutionSemanticsRowsOutOfReportingScope()
        {
            var block = MachineConfirmedTables.CriticExemptionBlock;

            Assert.Contains(ExecutionSemanticsFacts.TableHeading, block);
            Assert.Contains("NEVER report one", block);
            foreach (var kind in ExecutionSemanticsFacts.AllKinds)
            {
                Assert.Contains(kind, block);
            }

            // 옛 문구는 "DDL 원문과 다르면 보고"라는 탈출구를 실행 의미 행에도 열어 뒀다.
            // 그 문장이 되살아나면 Critic이 같은 오판을 되풀이할 수 있다.
            Assert.DoesNotContain("differs from the source DDL that the row cites", block);
        }

        [Fact]
        public void CriticExemptionBlock_ExcludesOnlyThePipelineWrittenColumnOfMixedTables()
        {
            var block = MachineConfirmedTables.CriticExemptionBlock;

            Assert.Contains(DmlScopeExtractor.ReferencedFunctionTableHeading, block);
            Assert.Contains("Never report that column", block);
        }

        [Fact]
        public void MixedTables_CarryTheirOwnScopeNote()
        {
            // Mixed의 "어느 칸이 대조 가능한가"는 표마다 다르다. 두 번째 Mixed 표가
            // 참조 함수의 문구를 물려받으면 그 표에 대해 거짓 지시가 나간다.
            foreach (var table in MachineConfirmedTables.All)
            {
                if (table.Verification == MachineConfirmedTableVerification.Mixed)
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(table.MixedScopeNote),
                        $"Mixed 표 `{table.Heading}`에 MixedScopeNote가 없습니다.");
                }
                else
                {
                    Assert.Null(table.MixedScopeNote);
                }
            }
        }

        [Fact]
        public void CriticExemptionBlock_KeepsProseContradictionsReportable()
        {
            // 면제는 표의 행에만 적용된다. 여기까지 눈감기면 표를 뒤집는 산문이 그대로 남는다.
            Assert.Contains(
                "contradicts one of these rows IS a defect",
                MachineConfirmedTables.CriticExemptionBlock);
        }

        [Fact]
        public void CriticExemptionBlock_CarriesTheTwoMeasuredSqlFacts()
        {
            // 지시만 주면 Critic이 자기 오해를 유지한 채 "그래도 틀렸다"고 적을 수 있어
            // 실측으로 확정한 두 사실을 근거로 함께 싣는다.
            var block = MachineConfirmedTables.CriticExemptionBlock;
            Assert.Contains("ROUNDS away from zero", block);
            Assert.Contains("DOES reset", block);
        }

        [Fact]
        public void CriticExemptionBlock_UsesLineFeedsOnly()
        {
            // 프롬프트 접두사 캐시는 바이트 일치라 개행이 플랫폼마다 달라지면 안 된다.
            Assert.DoesNotContain("\r", MachineConfirmedTables.CriticExemptionBlock);
        }

        [Fact]
        public void EveryRegisteredHeadingEndsWithTheSharedSuffix()
        {
            // 접미사가 한 자리에서만 정의되므로, em dash가 바뀌면 여기서 한 번에 걸린다.
            foreach (var table in MachineConfirmedTables.All)
            {
                Assert.EndsWith(MachineConfirmedTables.HeadingSuffix, table.Heading, StringComparison.Ordinal);
            }
        }
    }
}
