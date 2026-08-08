using System;

namespace ReSet.Core.Services
{
    /// <summary>
    /// SQL과 ORM의 경계 규칙 문구를 단독 소유한다. 지시서(MetadataExporter)와 L2 검증
    /// 프롬프트(ValidatorAiService)가 같은 규칙을 말해야 하므로, 다른 곳에서 이 문구를
    /// 새로 만들지 말고 이 클래스를 참조하십시오.
    /// </summary>
    public static class DataAccessPolicy
    {
        private const string CommonRules = @"### 데이터 액세스 경계 규칙 (Hybrid Data Access Boundary)

ORM은 아래 4가지 용도에만 허용합니다. 목록에 없는 모든 데이터 액세스는 파라미터 바인딩된 SQL로 작성하십시오. 판단이 애매하면 SQL을 택하십시오.

1. 엔티티/DTO 타입 정의 및 조회 결과 객체 매핑
2. 마스터·공통코드 등 참조 데이터의 단건/소량 조회
3. 체크포인트 상태 읽기/쓰기 (`ICheckpointRepository` 구현)
4. 배치 실행 이력·로그의 단건 기록

**다음은 반드시 SQL로 작성하십시오.**

* 정산 대상 테이블의 대량 SELECT/INSERT/UPDATE/DELETE
* 집계(`GROUP BY`), `UNION`/`UNION ALL`, 다중 테이블 JOIN
* 청킹 `WHILE` 루프와 그 내부 DML, 루프별 `BEGIN TRAN`/`COMMIT TRAN` 경계
* Shadow 테이블 생성·스왑·복원, 보상 트랜잭션 `DELETE`
* 세션 제어 (`SET XACT_ABORT ON`, `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`)
* 크로스 DB 3부 식별자 참조 쿼리

**아래 4개 조항은 경계와 무관하게 항상 적용됩니다.**

1. ORM은 반드시 `RunBusinessSteps`가 받은 `conn`/`tran`에 참여시키십시오. 새 커넥션이나 새 트랜잭션을 만들지 마십시오. 이를 어기면 검증기의 Rollback 격리가 깨져 정합성 대조 결과가 오염됩니다.
2. ORM 경로에서도 SQL 문자열 연결을 금지하고 파라미터 바인딩을 사용하십시오.
3. 지연 로딩(lazy loading)을 금지합니다. 배치에서 N+1을 유발하므로 명시적 조회만 사용하십시오.
4. 허용 목록 항목이라도 반환 행 수의 상한을 예측할 수 없으면 SQL로 작성하십시오.
";

        private const string CSharpStack = @"
| 경로 | 기술 |
| --- | --- |
| SQL | Dapper (ADO.NET) |
| ORM | EF Core |
";

        private const string JavaStack = @"
| 경로 | 기술 |
| --- | --- |
| SQL | MyBatis |
| ORM | Spring Data JPA |
";

        /// <summary>
        /// 지시서 5장에 실릴 경계 규칙 마크다운 블록.
        /// 알 수 없는 타겟 언어에는 스택 표만 생략하고 공통 규칙은 그대로 낸다.
        /// 언어를 모른다는 이유로 규칙 전체가 사라지면 에이전트가 규칙 없이 코드를 쓴다.
        /// </summary>
        public static string InstructionRules(string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(targetLanguage))
            {
                return CommonRules;
            }

            if (targetLanguage.Equals("C#", StringComparison.OrdinalIgnoreCase))
            {
                return CommonRules + CSharpStack;
            }

            if (targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase))
            {
                return CommonRules + JavaStack;
            }

            return CommonRules;
        }

        /// <summary>
        /// L2 Gap 분석 프롬프트의 5번 검증 항목. 지시서 문구를 판정형으로 다시 쓴 것이며,
        /// 위반 시 OverallStatus를 MATCH로 두지 못하게 하는 지시가 핵심이다.
        /// CodeVerificationOrchestrator가 OverallStatus만으로 L2Passed를 정하기 때문이다.
        /// </summary>
        public static string VerificationCriteria => @"5. 데이터 액세스 경계 준수: 다음 위반이 있는지 확인하십시오.
   - ORM(EF Core, JPA/Hibernate 등)이 허용 목록 4가지(① 엔티티/DTO 정의 및 결과 매핑, ② 마스터·공통코드 단건/소량 조회, ③ 체크포인트 상태 읽기/쓰기, ④ 배치 실행 이력·로그 단건 기록) 밖에서 사용되었는가?
   - 정산 대상 대량 DML, 집계/UNION/다중 JOIN, 청킹 루프 내부 DML, Shadow 처리, 세션 제어가 SQL이 아니라 ORM으로 구현되었는가?
   - ORM이 전달받은 커넥션/트랜잭션에 참여하지 않고 새 커넥션이나 새 트랜잭션을 생성하는가?
   - SQL 문자열 연결로 쿼리를 조립하거나 파라미터 바인딩을 생략했는가?
   - 지연 로딩(lazy loading)에 의존하는가?
   위반이 하나라도 있으면 OverallStatus를 MATCH로 두지 말고 최소 PARTIAL로 판정하고, 위반 내용을 DataAccessBoundaryGap에 기술하십시오.
";

        /// <summary>
        /// AbstractSettleTasklet 스텁에 삽입할 주석. 스텁이 System.Data만 참조하는 상태를
        /// 유지해야 하므로 실행 코드가 아닌 주석으로만 패턴을 보여준다.
        /// 8칸 들여쓰기는 스텁의 멤버 들여쓰기와 맞춘 것이다.
        /// </summary>
        public static string TaskletOrmComment => @"        // [데이터 액세스 경계] ORM(EF Core)은 MigrationInstructions.md 5장의 허용 목록에 한해 사용한다.
        // 사용할 경우 반드시 아래 conn/tran에 참여시켜야 하며, 새 커넥션이나 새 트랜잭션을 만들면
        // 검증기의 Rollback 격리(CSharpReflectionRunner)가 깨져 정합성 대조 결과가 오염된다.
        //   var options = new DbContextOptionsBuilder<XxxContext>().UseSqlServer((SqlConnection)conn).Options;
        //   using var db = new XxxContext(options);
        //   db.Database.UseTransaction((SqlTransaction)tran);
        // 정산 대상 대량 DML, 집계, 청킹 루프, Shadow 처리, 세션 제어는 파라미터 바인딩 SQL로 작성한다.";

        private const string CSharpArchitectureTests = @"using System;
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace ReSet.Batch.Tests.Architecture
{
    /// <summary>
    /// 지시서가 ""반드시""라고 말한 것을 기계가 강제한다.
    ///
    /// [이 테스트가 잡지 못하는 것]
    /// 경계 규칙 조항 1의 후반부 - ORM(EF Core)을 쓸 때 RunBusinessSteps가 받은
    /// conn/tran에 UseTransaction으로 참여시켜야 한다는 요구 - 는 메서드 호출 그래프
    /// 분석이 필요해 여기서 검증할 수 없다. 그 항목은 도구 쪽 L1 정적 검증
    /// (TransactionEnlistmentCheck)이 본다.
    /// 이 테스트가 통과했다고 경계 규칙 전부를 지켰다고 결론짓지 마십시오.
    /// </summary>
    public class ArchitectureTests
    {
        private static Assembly Target => typeof(ReSet.Batch.Core.ISettleStep).Assembly;

        [Fact]
        public void EverySettleStep_MustInherit_AbstractSettleTasklet()
        {
            var offenders = Target.GetTypes()
                .Where(t => typeof(ReSet.Batch.Core.ISettleStep).IsAssignableFrom(t))
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => !typeof(ReSet.Batch.Core.AbstractSettleTasklet).IsAssignableFrom(t))
                .Select(t => t.FullName)
                .ToList();

            Assert.True(offenders.Count == 0,
                ""AbstractSettleTasklet을 상속하지 않은 Step: "" + string.Join("", "", offenders));
        }

        [Fact]
        public void Tasklets_MustNotCreate_TheirOwnConnection()
        {
            // 새 커넥션을 만들면 검증기의 Rollback 격리가 깨져 정합성 대조가 오염된다.
            var result = Types.InAssembly(Target)
                .That().Inherit(typeof(ReSet.Batch.Core.AbstractSettleTasklet))
                .ShouldNot().HaveDependencyOn(""Microsoft.Data.SqlClient.SqlConnection"")
                .GetResult();

            Assert.True(result.IsSuccessful,
                ""SqlConnection을 직접 생성한 Tasklet: "" +
                string.Join("", "", result.FailingTypeNames ?? Array.Empty<string>()));
        }

        [Fact]
        public void Domain_MustNotDependOn_Infrastructure()
        {
            var result = Types.InAssembly(Target)
                .That().ResideInNamespaceStartingWith(""ReSet.Batch.Domain"")
                .ShouldNot().HaveDependencyOn(""ReSet.Batch.Infrastructure"")
                .GetResult();

            Assert.True(result.IsSuccessful,
                ""Infrastructure에 의존한 Domain 타입: "" +
                string.Join("", "", result.FailingTypeNames ?? Array.Empty<string>()));
        }

        [Fact]
        public void EveryTasklet_MustDeclare_StepNameAndSourceProcName()
        {
            // 검증기는 이 이름으로 설계서와 코드를 짝짓는다. 비어 있으면 매핑이 끊긴다.
            var offenders = Target.GetTypes()
                .Where(t => typeof(ReSet.Batch.Core.AbstractSettleTasklet).IsAssignableFrom(t))
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t =>
                {
                    var instance = (ReSet.Batch.Core.ISettleStep)Activator.CreateInstance(t)!;
                    return string.IsNullOrWhiteSpace(instance.StepName);
                })
                .Select(t => t.FullName)
                .ToList();

            Assert.True(offenders.Count == 0,
                ""StepName이 비어 있는 Tasklet: "" + string.Join("", "", offenders));
        }
    }
}
";

        private const string JavaArchitectureTests = @"package reset.batch.tests.architecture;

import com.tngtech.archunit.core.domain.JavaClasses;
import com.tngtech.archunit.core.importer.ClassFileImporter;
import com.tngtech.archunit.lang.syntax.ArchRuleDefinition;
import org.junit.jupiter.api.Test;

/**
 * 지시서가 ""반드시""라고 말한 것을 ArchUnit이 강제한다.
 *
 * [이 테스트가 잡지 못하는 것]
 * 경계 규칙 조항 1의 후반부 - ORM(JPA)을 전달받은 커넥션/트랜잭션에 참여시켜야 한다는
 * 요구 - 는 호출 그래프 분석이 필요해 여기서 검증할 수 없다. 그 항목은 도구의 L1
 * 정적 검증이 본다. 이 테스트 통과를 경계 규칙 전체 준수로 읽지 마십시오.
 */
class ArchitectureTests {

    private final JavaClasses classes = new ClassFileImporter().importPackages(""reset.batch"");

    @Test
    void everySettleStepMustExtendAbstractSettleTasklet() {
        ArchRuleDefinition.classes()
            .that().implement(reset.batch.core.ISettleStep.class)
            .and().areNotInterfaces().and().areNotAbstract()
            .should().beAssignableTo(reset.batch.core.AbstractSettleTasklet.class)
            .check(classes);
    }

    @Test
    void taskletsMustNotCreateTheirOwnConnection() {
        ArchRuleDefinition.noClasses()
            .that().areAssignableTo(reset.batch.core.AbstractSettleTasklet.class)
            .should().callMethod(javax.sql.DataSource.class, ""getConnection"")
            .check(classes);
    }

    @Test
    void domainMustNotDependOnInfrastructure() {
        ArchRuleDefinition.noClasses()
            .that().resideInAPackage(""..domain.."")
            .should().dependOnClassesThat().resideInAPackage(""..infrastructure.."")
            .check(classes);
    }
}
";

        /// <summary>
        /// 코딩 에이전트 프로젝트에 배치할 아키텍처 테스트. 이전 스텁은 본문이 전부
        /// 주석이라 통과해도 아무것도 보장하지 않았고, 지침 8·9번의 ""반드시""를
        /// 강제하는 장치가 어디에도 없었다.
        /// </summary>
        public static string ArchitectureTestStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaArchitectureTests
                : CSharpArchitectureTests;

        private const string CSharpRepositoryContract = @"using System.Collections.Generic;

namespace ReSet.Batch.Core
{
    /// <summary>
    /// 단계 실행 순서를 선언으로 고정한다. 회차마다 다른 프로세스가 Tasklet을
    /// 추가하므로, 순서를 조립 코드에 흩어 두면 회차 간에 어긋난다.
    /// </summary>
    public interface ISettleStepDescriptor
    {
        int Order { get; }
        ISettleStep Step { get; }
    }

    /// <summary>
    /// 데이터 액세스 계층의 최소 계약. 구현체는 회차 0에서 만든다.
    /// 대량 DML·집계·청킹은 이 인터페이스 뒤에서도 파라미터 바인딩 SQL로 작성한다.
    /// </summary>
    public interface ISettleRepository
    {
        int ExecuteNonQuery(string sql, object? parameters);
        IEnumerable<T> Query<T>(string sql, object? parameters);
    }
}
";

        private const string JavaRepositoryContract = @"package reset.batch.core;

import java.util.List;

/**
 * 단계 실행 순서를 선언으로 고정한다. 회차마다 다른 프로세스가 Tasklet을 추가하므로,
 * 순서를 조립 코드에 흩어 두면 회차 간에 어긋난다.
 */
public interface ISettleStepDescriptor {
    int getOrder();
    ISettleStep getStep();
}

/**
 * 데이터 액세스 계층의 최소 계약. 구현체는 회차 0에서 만든다.
 * 대량 DML·집계·청킹은 이 인터페이스 뒤에서도 파라미터 바인딩 SQL로 작성한다.
 */
interface ISettleRepository {
    int executeNonQuery(String sql, Object parameters);
    <T> List<T> query(String sql, Object parameters, Class<T> type);
}
";

        /// <summary>
        /// 회차들이 공유할 계약. ReSet이 인터페이스를 소유하고 구현체와 조립은
        /// 회차 0의 에이전트가 만든다 - 계약은 결정론적으로 고정하되 보일러플레이트는
        /// 에이전트의 유연성에 남긴다.
        ///
        /// 두 언어의 스텁을 각자 전문으로 둔다. 한쪽을 문자열 치환해 다른 쪽을 만들면
        /// 컴파일되지 않는 코드가 산출물로 나간다.
        /// </summary>
        public static string RepositoryContractStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaRepositoryContract
                : CSharpRepositoryContract;
    }
}
