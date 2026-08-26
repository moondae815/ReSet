using System.Linq;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class MachineConfirmedTablesExpansionTests
    {
        private static SpDefinition Def(string ddl) => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = ddl
        };

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

        [Fact]
        public void All_ShouldContainErrorCodeTableAtTheEnd()
        {
            var last = MachineConfirmedTables.All[^1];

            Assert.Equal(DmlScopeExtractor.ErrorCodeTableHeading, last.Heading);
        }

        [Fact]
        public void From_WhenErrorCodesAreTheOnlyMaterial_ShouldNotReturnNull()
        {
            // Fix Round 1 - 이전 버전은 DmlScopeExtractor.ExtractErrorCodes만 직접
            // 불러 SpecExpectations.From을 한 번도 부르지 않았다(리뷰 Important). 이제
            // From을 실제로 부르고 ErrorCodes가 옮겨진 값을 단언한다 - `ErrorCodes =
            // errorCodes,` 대입 자체나 From 안의 ExtractErrorCodes 호출이 깨지면 이
            // 단언이 잡는다(직접 실측: 그 대입 줄을 지우면 이 테스트가 FAIL한다).
            //
            // [null 체인 항 자체는 이 DDL로 독립 증명이 안 된다] 이 DDL의 UPDATE는
            // errorCodes와 별개로 dmlScopeFacts도 채운다(RecordErrorCode가
            // Record/Visit(InsertSpecification) 안, 즉 Facts.Add 직후에서만 불리므로
            // ErrorCodeFact는 이미 존재하는 DmlScopeFact 위의 주석일 수밖에 없다 -
            // SpecExpectations.cs의 errorCodes 항 주석 참고). 그래서
            // `&& errorCodes.Count == 0`만 지워도 dmlScopeFacts.Count == 0 항이 이미
            // false라 From은 여전히 null을 돌려주지 않는다 - 이 항을 오늘 독립적으로
            // 잠그는 DDL은 없다(직접 실측: 그 항만 지워도 이 테스트는 여전히 PASS).
            // 그래도 이 테스트는 남긴다 - ErrorCodes 배선 자체의 회귀는 잡고,
            // "재료가 오류 코드 하나뿐"이라는 이름의 전제가 오늘은 성립하지 않는다는
            // 사실을 다음에 이 자리를 읽는 사람이 놓치지 않게 한다.
            const string ddl = @"CREATE PROCEDURE dbo.P @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -1 END
END";

            var expectations = SpecExpectations.From(Def(ddl));

            Assert.NotNull(expectations);
            Assert.Single(expectations!.ErrorCodes);
        }
    }
}
