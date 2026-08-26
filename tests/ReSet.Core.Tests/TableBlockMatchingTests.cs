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

        /// <summary>
        /// [변이 잠금 A - 합집합] 헤더가 일치하는 블록이 <b>둘</b>이고 기대 사실이
        /// <b>둘째</b> 블록에만 있다. L1 재시도에서 모델이 틀린 표를 지우지 않고 그
        /// 아래에 고친 표를 덧붙이는 모양이 정확히 이것이다.
        ///
        /// 합집합(<c>Where(...).SelectMany(block =&gt; block.Skip(1))</c>)이면 둘째 블록의
        /// 라인 6이 발견되어 침묵한다. 첫 일치 블록만 쓰면(<c>FirstOrDefault</c> - 원본
        /// 브랜치가 FIX ROUND 2에서 되돌린 바로 그 구현) 첫 블록에 없는 라인 6이
        /// 결손으로 발화한다. 즉 이 테스트가 그 되돌림을 막는다.
        /// </summary>
        [Fact]
        public void CheckTransactionBoundaries_ShouldUnionEveryHeaderMatchingBlock_NotJustTheFirst()
        {
            var markdown = $@"## 로직 흐름 요약

{TransactionBoundaryExtractor.TableHeading}

| 라인 | 종류 | 이름 |
| :--- | :--- | :--- |
| 3 | BEGIN TRANSACTION | (없음) |

재시도에서 위 표가 불완전해 아래에 고친 표를 덧붙였다.

| 라인 | 종류 | 이름 |
| :--- | :--- | :--- |
| 3 | BEGIN TRANSACTION | (없음) |
| 6 | COMMIT TRANSACTION | (없음) |
";

            var result = ValidateTwoBoundaries(markdown);

            Assert.DoesNotContain(result.Errors, e => e.Contains("트랜잭션 경계 표에 라인"));
        }

        /// <summary>
        /// [변이 잠금 C - 헤더 술어의 All] 미끼 블록의 헤더가 기대 헤더 셀과
        /// <b>정확히 한 칸만</b> 겹친다(`라인`). 이 모양은 코퍼스의 실물이다 -
        /// `output/Functions/dbo.UF_GET_ROUND4VAT/docs/Spec.md`에
        /// `| 종류 | 라인 | 대상 | 확정 사실 |`과 `| 작업 | 대상 | 분석 결과 |`가 함께 있고
        /// `대상` 한 칸이 겹친다. 기존 미끼 픽스처(`| 항목 | 값 | 비고 |`)는 기대 헤더와
        /// 한 칸도 안 겹쳐서 이 술어를 변별하지 못한다.
        ///
        /// 헤더 판정이 <c>All</c>이면 미끼가 배제되어 진짜 표에 없는 라인 6이 결손으로
        /// 발화한다. <c>Any</c>로 느슨해지면 미끼가 "자기 표"로 인정되어 그 행이 라인 6을
        /// 덮고 결손이 사라진다 - 이 브랜치가 고친 바로 그 거짓 음성이 되살아난다.
        ///
        /// [미끼 데이터 행의 셀이 기대값과 <b>정확히</b> 같아야 하는 이유] 대조는 셀 단위
        /// 완전 일치라 `COMMIT TRANSACTION 관련`처럼 덧붙이면 <c>Any</c> 변이에서도 덮기가
        /// 일어나지 않아 테스트가 우연히 통과한다.
        /// </summary>
        [Fact]
        public void CheckTransactionBoundaries_ShouldRejectADecoyHeaderSharingExactlyOneCell()
        {
            var markdown = $@"## 로직 흐름 요약

{TransactionBoundaryExtractor.TableHeading}

| 라인 | 종류 | 이름 |
| :--- | :--- | :--- |
| 3 | BEGIN TRANSACTION | (없음) |

참고: 아래는 이 표가 아니라 라인별 보조 설명 표다.

| 라인 | 설명 | 비고 |
| :--- | :--- | :--- |
| 6 | COMMIT TRANSACTION | 참고 |
";

            var result = ValidateTwoBoundaries(markdown);

            Assert.Contains(
                result.Errors,
                e => e.Contains("라인 6") && e.Contains("COMMIT TRANSACTION"));
        }

        /// <summary>
        /// [변이 잠금 B - 폴백 발동 조건] 헤더 행은 있는데 그 블록에 <b>데이터 행이
        /// 하나도 없다</b>(구분선도 없다). 그리고 기대 토큰을 담은 미끼 블록이 뒤에 있다.
        ///
        /// 현 구현은 폴백을 <c>matched.Count &gt; 0</c>으로 가른다 - 헤더만 있는 블록은
        /// 데이터 행을 하나도 내놓지 못하므로 <c>matched</c>가 비고, 관대한 전체 스캔으로
        /// 후퇴해 미끼 행까지 대조 대상이 되어 <b>침묵</b>한다. 조건을 "헤더 행을 가진
        /// 블록이 하나라도 있는가"(<c>blocks.Any(b =&gt; IsHeaderRow(b[0], cells))</c>)로
        /// 바꾸면 빈 <c>matched</c>를 그대로 돌려주어 <b>모든</b> 기대 사실이 결손으로
        /// 발화한다.
        ///
        /// 어느 쪽도 자명하게 옳지 않다 - 여기가 거짓 양성과 거짓 음성의 경계다. 이
        /// 테스트는 오늘의 선택(관대함 우선)을 고정해서, 바꾸려는 사람이 <b>의식적으로</b>
        /// 바꾸게 한다.
        /// </summary>
        [Fact]
        public void CheckTransactionBoundaries_ShouldFallBackWhenTheHeaderBlockHasNoDataRows()
        {
            var markdown = $@"## 로직 흐름 요약

{TransactionBoundaryExtractor.TableHeading}

| 라인 | 종류 | 이름 |

참고: 아래는 이 표가 아니라 별개의 보조 표다.

| 항목 | 값 | 비고 |
| 3 | BEGIN TRANSACTION | (없음) |
| 6 | COMMIT TRANSACTION | (없음) |
";

            var result = ValidateTwoBoundaries(markdown);

            Assert.DoesNotContain(result.Errors, e => e.Contains("트랜잭션 경계 표에 라인"));
        }

        /// <summary>
        /// [특성화 테스트 - 좁힘이 새로 만든 거짓 양성 경로] 표 한가운데에 빈 줄이 끼어
        /// 헤더+구분선과 데이터 행이 <b>서로 다른 블록</b>으로 갈렸다. 헤더를 가진 블록은
        /// 구분선 행 하나만 내놓으므로 <c>matched.Count &gt; 0</c>이 성립해 폴백이 걸리지
        /// 않고, 아래 데이터 행 전부가 대조에서 빠져 <b>모든</b> 기대 사실이 결손으로
        /// 발화한다 - 문서 단위 거짓 양성이다.
        ///
        /// 옛 관대한 전체 스캔에서는 이 실패가 구조적으로 불가능했다. 즉 <b>이 좁힘이
        /// 새로 만든 유일한 회귀 경로</b>다. 오늘 코퍼스에서는 이 모양이 0건이라 실해가
        /// 없어 고치지 않고 <b>고정만</b> 한다 - 다음 사람이 놀라지 않고 의도로 읽도록.
        /// (원인 자체는 CheckMachineTableShape/ReportTableShapeBreaks의 몫인데, 그 경로가
        /// 이 모양에서 침묵한다는 것은 CollectTableMatchRows 주석과 리뷰 백로그 D6에 있다.)
        ///
        /// 이 기대가 깨지면 좁힘의 거짓 양성 경계가 움직인 것이다 - 코퍼스를 다시 재고
        /// 이 주석을 고쳐 쓰십시오.
        /// </summary>
        [Fact]
        public void CheckTransactionBoundaries_WhenABlankLineSplitsHeaderFromData_ReportsEveryFactMissing()
        {
            var markdown = $@"## 로직 흐름 요약

{TransactionBoundaryExtractor.TableHeading}

| 라인 | 종류 | 이름 |
| :--- | :--- | :--- |

| 3 | BEGIN TRANSACTION | (없음) |
| 6 | COMMIT TRANSACTION | (없음) |
";

            var result = ValidateTwoBoundaries(markdown);

            Assert.Contains(
                result.Errors,
                e => e.Contains("라인 3") && e.Contains("BEGIN TRANSACTION"));
            Assert.Contains(
                result.Errors,
                e => e.Contains("라인 6") && e.Contains("COMMIT TRANSACTION"));
        }
    }
}
