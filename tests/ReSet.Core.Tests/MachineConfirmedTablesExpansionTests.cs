using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class MachineConfirmedTablesExpansionTests
    {
        [Fact]
        public void All_ShouldContainTheTwoNewTables()
        {
            var headings = MachineConfirmedTables.All.Select(t => t.Heading).ToList();

            Assert.Contains(TransactionBoundaryExtractor.TableHeading, headings);
            Assert.Contains(SetAssignmentExtractor.TableHeading, headings);
        }

        [Fact]
        public void All_ShouldAppendNewTablesAtTheEnd()
        {
            // 순서가 곧 Critic 프롬프트에 실리는 순서다. 프롬프트 접두사 캐시가 바이트
            // 일치로 걸리므로 기존 항목 사이에 끼우면 캐시가 통째로 깨진다.
            var headings = MachineConfirmedTables.All.Select(t => t.Heading).ToList();
            var referencedFunctionIndex =
                headings.IndexOf(DmlScopeExtractor.ReferencedFunctionTableHeading);

            Assert.True(
                headings.IndexOf(TransactionBoundaryExtractor.TableHeading) > referencedFunctionIndex,
                "새 표는 기존 마지막 항목 뒤에 와야 한다");
            Assert.True(
                headings.IndexOf(SetAssignmentExtractor.TableHeading) > referencedFunctionIndex,
                "새 표는 기존 마지막 항목 뒤에 와야 한다");
        }

        [Fact]
        public void CriticExemptionBlock_ShouldCoverTheTwoNewTables()
        {
            // All에 넣으면 Critic 면제가 자동으로 따라온다. 이것이 없으면 Critic이
            // 새 표를 환각으로 오판하고 L1은 반대로 전사를 요구해 교착이 된다
            // (2026-08-22 재생성에서 실제로 세 번 났다).
            var block = MachineConfirmedTables.CriticExemptionBlock;

            Assert.Contains("트랜잭션 경계", block);
            Assert.Contains("변수 대입", block);
        }
    }
}
