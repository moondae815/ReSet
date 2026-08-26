using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 기계 확정 표 대조가 헤딩 절 안의 <b>블록</b>을 구분하는지 본다.
    ///
    /// 여기 기대 사실은 손으로 채우지 않고 DDL에서 유도한다
    /// (<see cref="SpecExpectations.From(SpDefinition)"/>) - `SpecExpectationsTransactionAndSetTests`와
    /// 같은 방식이다. 아래 <see cref="TwoBoundaryDdl"/>이 내는 라인 번호는 3(BEGIN)과
    /// 6(COMMIT)이고, 픽스처의 `라인` 칸은 그 실측값을 쓴다 - 추측한 번호로 쓰면 RED가
    /// 엉뚱한 이유로 난다.
    /// </summary>
    public class TableBlockMatchingTests
    {
        private static SpDefinition Def(string ddl) => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = ddl
        };

        /// <summary>라인 3 BEGIN, 라인 6 COMMIT을 내는 DDL. 두 테스트가 공유한다.</summary>
        private const string TwoBoundaryDdl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    SELECT 1
    SELECT 2
    COMMIT TRANSACTION
END";

        private static ValidationResult ValidateTwoBoundaries(string markdown)
        {
            var expectations = SpecExpectations.From(Def(TwoBoundaryDdl));
            Assert.NotNull(expectations);
            return new MechanicalValidator().Validate(markdown, expectations!);
        }

        /// <summary>
        /// 헤딩 절 안에 산문으로 갈린 별개 블록이 있고, 그 블록의 행이 우연히 기대
        /// 토큰(라인 번호)을 담고 있다. 진짜 표에는 라인 6 COMMIT이 없다.
        /// 옛 수집(절 전체의 `|` 줄을 뭉뚱그림)은 우연한 일치에 속아 결손 0건을 냈다.
        ///
        /// [미끼 블록에 자기 헤더 행을 주는 이유] 미끼를 한 줄짜리로 두면
        /// `SelectMany(block => block.Skip(1))`이 그 한 줄을 통째로 떨어뜨린다 - 그러면
        /// 헤더 대조를 통째로 무력화하는 변이(`Where(block => true)`)에서도 라인 6이
        /// 여전히 "없음"으로 남아 이 테스트가 우연히 통과한다(2026-08-26 변이 잠금
        /// 실측). 미끼에 자기 첫 행을 주면 그 변이에서 라인 6이 되살아나 덮기가
        /// 재현되므로, 이 테스트가 헤더 대조 자체를 변별한다.
        /// </summary>
        [Fact]
        public void CheckTransactionBoundaries_ShouldNotLetAForeignBlockMaskAMissingRow()
        {
            var markdown = $@"## 로직 흐름 요약

{TransactionBoundaryExtractor.TableHeading}

| 라인 | 종류 | 이름 |
| :--- | :--- | :--- |
| 3 | BEGIN TRANSACTION | (없음) |

참고: 아래는 이 표가 아니라 별개의 보조 표다.

| 항목 | 값 | 비고 |
| 6 | COMMIT TRANSACTION | (없음) |
";

            var result = ValidateTwoBoundaries(markdown);

            Assert.Contains(
                result.Errors,
                e => e.Contains("라인 6") && e.Contains("COMMIT TRANSACTION"));
        }

        /// <summary>
        /// 헤더 행이 없는 렌더(모델이 예시를 안 따른 경우)에서도 관대한 전체 스캔으로
        /// 후퇴해 오류를 내지 않는다. 이 폴백이 없으면 LLM 출력의 사소한 형태 차이가
        /// 전부 거짓 양성이 된다.
        /// </summary>
        [Fact]
        public void CheckTransactionBoundaries_ShouldStayLenientWhenNoHeaderRowIsPresent()
        {
            var markdown = $@"## 로직 흐름 요약

{TransactionBoundaryExtractor.TableHeading}

| 3 | BEGIN TRANSACTION | (없음) |
| 6 | COMMIT TRANSACTION | (없음) |
";

            var result = ValidateTwoBoundaries(markdown);

            Assert.DoesNotContain(result.Errors, e => e.Contains("트랜잭션 경계"));
        }
    }
}
