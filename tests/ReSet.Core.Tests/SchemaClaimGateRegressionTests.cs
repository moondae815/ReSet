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

            // Assert - 재생성이 무엇을 고쳐야 하는지, 그리고 왜 고쳐야 하는지 둘 다 알 수
            // 있어야 한다. "스키마 표에 실제로 제공되었습니다"는 CheckSchemaClaims가 만드는
            // 오류 메시지(MechanicalValidator.cs의 CheckSchemaClaims)의 고정 부분이다 -
            // 컬럼명·테이블명처럼 픽스처마다 달라지는 부분이 아니라 매 위반마다 동일하게
            // 붙는 설명 문장이므로, 그 문장 자체가 바뀌지 않는 한 안정적이다.
            Assert.NotNull(result.SuggestedPromptFix);
            Assert.Contains("CLINTCOMM", result.SuggestedPromptFix!);
            Assert.Contains("스키마 표에 실제로 제공되었습니다", result.SuggestedPromptFix!);
        }

        [Fact]
        public void TheExceptionProcSpec_ShouldFailForSplitAndForProseAbsenceClaims()
        {
            // Arrange - 「조회 대상 테이블」 표만 넣는다. 이 표 안에서
            // SETTLE_POQ_DB.dbo.TSettleMst, dbo.TSettleMst, TSettleMst 세 표기가
            // 전부 나오므로 TableIdentitySplit이 정확히 한 번 잡혀야 한다.
            var markdown = WrapAsSpec("### 조회 대상 테이블\n\n" + LoadFixture("ExceptionProcCrudExcerpt.md"));

            // Act
            var result = new MechanicalValidator().Validate(markdown, BuildSettleMstTruth());

            // Assert - 테이블 동일성 분열.
            var split = Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
            Assert.Contains("TSettleMst", split.Message);

            // Assert - 거짓 부재 주장. 이 개수(21)는 프로덕션에서 관찰되는 수가 아니다.
            // EXCEPTION_PROC 원본 51행("`dbo.TSettleMst` | ... | ... `CLCOMM`, `CLIntComm`,
            // `CLVT`는 제공된 `dbo.TSettleMst` 스키마에 없는 열이므로 스키마 불일치입니다.")은
            // 실제 운영에서는 이 테이블의 스키마 자체가 수집되지 않아(dep.Columns.Count == 0)
            // PromptSchemaColumns에 항목이 생기지 않고 CheckSchemaClaims가 조용히 넘어간다.
            // 여기서는 세 표기 분열을 같은 한 픽스처로 시험하려고 BuildSettleMstTruth가
            // "이 테이블의 스키마는 제공됐다"는 진실을 의도적으로 구성했다. 그 진실 아래에서는
            // 조회 대상 테이블 표의 아홉 행이 각각 스스로 "스키마에 없" / "스키마 불일치"
            // 같은 부재 트리거 어휘를 담은 한 줄이고, 그 줄에 함께 등장하는 백틱 식별자 중
            // 진실에 있는 컬럼이 전부 후보가 된다 - 그래서 21건이 나온다. 버그가 아니라
            // 이 구성의 논리적 귀결이므로, 조용한 노이즈로 남기지 않고 계약으로 고정한다.
            var claims = result.DetailedErrors.Where(e => e.Type == ErrorType.SchemaClaimFalse).ToList();
            Assert.Equal(21, claims.Count);

            // Assert - 설계서 수용 기준 3: 전용 "스키마 불일치 컬럼" 표가 아니라, CRUD 표
            // 한 행의 설명 셀에 산문으로 박힌 부재 주장도 잡힌다는 증명. 51행은 전용 판정
            // 표 형태가 아니라 "조건과 사용 방식" 셀 안의 문장("`CLCOMM`, `CLIntComm`,
            // `CLVT`는 제공된 `dbo.TSettleMst` 스키마에 없는 열이므로 스키마 불일치입니다.")
            // 이고, 그 문장이 지목한 컬럼 중 진실에 실재하는 것은 CLIntComm 하나다
            // (CLCOMM·CLVT는 대조 기준 밖이라 이 컬럼으로는 증명할 수 없다).
            Assert.Contains(claims, e => e.Message.Contains("CLIntComm"));
        }

        [Fact]
        public void UnattributableAbsenceStatements_ShouldBeSilentlySkipped()
        {
            // Arrange - 이 테스트가 실제로 증명하는 것은 "귀속이 안 되는 진술은 검사
            // 자체가 돌지 않는다"이지 "검사했는데 참이라 통과했다"가 아니다. 아래 두
            // 문장의 식별자(TExchangeRateMst, TBasicCurrencyMst, 대상 없음, 해당 없음)는
            // 진실의 테이블(SETTLE_POQ_DB.dbo.TSettleMst)로 귀속되지 않으므로
            // ResolveSchemaTableKey가 전부 null을 돌려주고, CheckSchemaClaims가 애초에
            // 판정할 대상을 찾지 못한다. 컬럼이 0개인 의존성은 SpecExpectations.From
            // 단계에서부터 PromptSchemaColumns에 항목이 생기지 않으므로 역시 대조 대상이
            // 아니다.
            //
            // 검사가 실제로 열리고 나서 참인 부재 주장이 정상 통과하는 경우(테이블은
            // 귀속되지만 주장이 사실인 경우)는 MechanicalValidatorTests.cs의
            // Validate_WhenTheAbsenceClaimIsTrue_ShouldPass가 덮는다 - 이 브랜치 전체로는
            // 그 경로가 비어 있지 않다.
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
