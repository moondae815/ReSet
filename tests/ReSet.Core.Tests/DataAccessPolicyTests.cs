using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class DataAccessPolicyTests
    {
        [Fact]
        public void InstructionRules_ForCSharp_NamesDapperAndEfCoreOnly()
        {
            var rules = DataAccessPolicy.InstructionRules("C#");

            Assert.Contains("Dapper", rules);
            Assert.Contains("EF Core", rules);
            Assert.DoesNotContain("MyBatis", rules);
            Assert.DoesNotContain("Spring Data JPA", rules);
        }

        [Fact]
        public void InstructionRules_ForJava_NamesMyBatisAndJpaOnly()
        {
            var rules = DataAccessPolicy.InstructionRules("Java");

            Assert.Contains("MyBatis", rules);
            Assert.Contains("Spring Data JPA", rules);
            Assert.DoesNotContain("Dapper", rules);
            Assert.DoesNotContain("EF Core", rules);
        }

        [Theory]
        [InlineData("C#")]
        [InlineData("Java")]
        [InlineData("Kotlin")]
        [InlineData("")]
        public void InstructionRules_AlwaysCarriesAllowlistAndStandingClauses(string targetLanguage)
        {
            var rules = DataAccessPolicy.InstructionRules(targetLanguage);

            // 허용 목록 4항목
            Assert.Contains("엔티티/DTO 타입 정의", rules);
            Assert.Contains("마스터·공통코드", rules);
            Assert.Contains("체크포인트 상태 읽기/쓰기", rules);
            Assert.Contains("배치 실행 이력·로그의 단건 기록", rules);

            // SQL 필수 열거
            Assert.Contains("청킹", rules);
            Assert.Contains("Shadow 테이블", rules);
            Assert.Contains("SET TRANSACTION ISOLATION LEVEL SNAPSHOT", rules);

            // 항상 적용 조항 4개
            Assert.Contains("새 트랜잭션을 만들지 마십시오", rules);
            Assert.Contains("파라미터 바인딩을 사용하십시오", rules);
            Assert.Contains("지연 로딩", rules);
            Assert.Contains("상한을 예측할 수 없으면", rules);
        }

        [Fact]
        public void InstructionRules_ForUnknownLanguage_OmitsOnlyTheStackTable()
        {
            var rules = DataAccessPolicy.InstructionRules("Kotlin");

            Assert.DoesNotContain("Dapper", rules);
            Assert.DoesNotContain("MyBatis", rules);
            Assert.Contains("ORM은 아래 4가지 용도에만 허용합니다", rules);
        }

        [Fact]
        public void VerificationCriteria_DemandsPartialOnViolation()
        {
            var criteria = DataAccessPolicy.VerificationCriteria;

            Assert.StartsWith("5.", criteria);
            Assert.Contains("PARTIAL", criteria);
            Assert.Contains("DataAccessBoundaryGap", criteria);
        }

        [Fact]
        public void TaskletOrmComment_IsCommentOnlyAndShowsTransactionEnlistment()
        {
            var comment = DataAccessPolicy.TaskletOrmComment;

            Assert.Contains("UseTransaction", comment);
            foreach (var line in comment.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                Assert.StartsWith("//", line.Trim());
            }
        }
    }
}
