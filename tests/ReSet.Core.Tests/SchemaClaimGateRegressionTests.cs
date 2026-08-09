using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 88~94점으로 검증을 통과했던 실제 명세서가 이 게이트에서 떨어지는지 본다.
    ///
    /// 픽스처는 output/ 아래 실물에서 한 글자도 고치지 않고 발췌한 것이다. output/은
    /// .gitignore 대상이라 CI에 없으므로 여기로 옮겨 커밋했다. 문장을 우리가 다시 쓰지
    /// 않는 것이 요점이다 - 게이트가 잡아야 할 것은 우리가 상상한 형태가 아니라 AI가
    /// 실제로 쓴 형태다.
    /// </summary>
    public class SchemaClaimGateRegressionTests
    {
        private static string LoadFixture(string name) =>
            File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", name));

        private static string WrapAsSpec(string crudBody) =>
            string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용",
                "## CRUD 분석", crudBody,
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });

        /// <summary>
        /// TSettleMst는 하나의 물리 테이블이고 59개 컬럼을 갖는다(원본 DDL 대조 결과).
        /// 명세서가 "존재하지 않음"으로 적은 15개는 전부 실재한다. 라이브 DB 없이
        /// 재현할 수 있도록 대조에 필요한 것만 손으로 구성한다 - 두 픽스처가 지목하는
        /// 컬럼 전부(COMM_UPD의 15개 + EXCEPTION_PROC 조회 절이 참조하는 컬럼들)를
        /// 포함해야 대조가 성립한다.
        /// </summary>
        private static SpecExpectations BuildSettleMstTruth()
        {
            var dep = new DependencyInfo
            {
                Name = "TSettleMst", Schema = "dbo", Database = "SETTLE_POQ_DB", Type = "USER_TABLE"
            };

            var realColumns = new[]
            {
                // COMM_UPD의 "스키마 불일치 컬럼" 표가 "존재하지 않음"으로 적은 15개.
                "CLINTCOMM", "CLETC", "PGINTEXPCOMM", "PGINTREALCOMM", "PGETC",
                "PointAmt", "CardAmt", "CouponAmt", "MoneyAmt", "PGTOTAL",
                "POQINCOME", "SettleCurrency", "ForeignSettleAmt", "CLCOMMTYPE", "PGCOMMTYPE",
                // EXCEPTION_PROC의 "조회 대상 테이블" 표가 SETTLE_POQ_DB.dbo.TSettleMst
                // 행에서 참조하는 컬럼들. 여기 없으면 그 행은 대조 자체가 안 된다.
                "PLTID", "YMD", "UseState", "PGName", "TxAmt", "ID", "DiscountAmt",
                "MallID", "DiscountFlag", "AYMD", "ExtraSettleFlag", "NonSettleAmt",
                "AbroadChk", "CLIntComm", "CLVTType"
            };
            foreach (var column in realColumns)
            {
                dep.Columns.Add(new ColumnInfo { ColumnName = column, DataType = "int" });
            }

            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_UTIL_SETTLE_COMM_UPD",
                ObjectKey = new CodeObjectKey(
                    "SETTLE_POQ_DB", "dbo", "UP_UTIL_SETTLE_COMM_UPD", CodeObjectType.Procedure)
            };
            sp.Dependencies.Add(dep);
            return SpecExpectations.From(sp)!;
        }

        [Fact]
        public void TheSpecThatScoredNinetyOne_ShouldNowFailL1()
        {
            // Arrange
            var markdown = WrapAsSpec(LoadFixture("SettleCommUpdSchemaMismatchExcerpt.md"));

            // Act
            var result = new MechanicalValidator().Validate(markdown, BuildSettleMstTruth());

            // Assert - 이 단언이 이 브랜치의 존재 이유다.
            Assert.False(result.IsValid);

            var claims = result.DetailedErrors.Where(e => e.Type == ErrorType.SchemaClaimFalse).ToList();
            Assert.Equal(15, claims.Count);
            Assert.Contains(claims, e => e.Message.Contains("CLINTCOMM"));
            Assert.Contains(claims, e => e.Message.Contains("PGCOMMTYPE"));
        }

        [Fact]
        public void TheFailedSpec_ShouldProduceARegenerationInstruction()
        {
            // Arrange
            var markdown = WrapAsSpec(LoadFixture("SettleCommUpdSchemaMismatchExcerpt.md"));

            // Act
            var result = new MechanicalValidator().Validate(markdown, BuildSettleMstTruth());

            // Assert - 재생성이 무엇을 고쳐야 하는지 알 수 있어야 한다.
            Assert.NotNull(result.SuggestedPromptFix);
            Assert.Contains("CLINTCOMM", result.SuggestedPromptFix!);
        }

        [Fact]
        public void TheExceptionProcSpec_ShouldFailForSplittingOneTableAcrossSpellings()
        {
            // Arrange - 「조회 대상 테이블」 표만 넣는다. 이 표 안에서
            // SETTLE_POQ_DB.dbo.TSettleMst, dbo.TSettleMst, TSettleMst 세 표기가
            // 전부 나오므로 TableIdentitySplit이 정확히 한 번 잡혀야 한다.
            var markdown = WrapAsSpec("### 조회 대상 테이블\n\n" + LoadFixture("ExceptionProcCrudExcerpt.md"));

            // Act
            var result = new MechanicalValidator().Validate(markdown, BuildSettleMstTruth());

            // Assert
            var split = Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
            Assert.Contains("TSettleMst", split.Message);
        }

        [Fact]
        public void TrueAbsenceStatements_ShouldNotBeFlagged()
        {
            // Arrange - 스키마가 수집되지 않은 테이블에 대한 진술은 참이다.
            // 컬럼이 0개인 의존성은 애초에 대조 대상이 아니므로 걸리면 안 된다.
            var truth = BuildSettleMstTruth();
            var markdown = WrapAsSpec(string.Join("\n", new[]
            {
                "`TExchangeRateMst`, `TBasicCurrencyMst`의 스키마 정의는 제공되지 않았습니다.",
                "| 대상 없음 | 해당 없음 | 프로시저에는 `INSERT` 문이 없습니다. |"
            }));

            // Act
            var result = new MechanicalValidator().Validate(markdown, truth);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }
    }
}
