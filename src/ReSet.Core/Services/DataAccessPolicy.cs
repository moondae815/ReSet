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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        // 코어 한 어셈블리만 보면, 회차 0이 지시받은 헥사고날 구조를 다중 프로젝트로
        // 만든 순간 Tasklet과 Domain 타입이 시야에서 사라져 규칙들이 대상 0건으로
        // 조용히 통과한다. 테스트 어셈블리가 참조하는 ReSet.Batch.* 를 전부 훑는다.
        private static IReadOnlyList<Assembly> Targets
        {
            get
            {
                foreach (var reference in Assembly.GetExecutingAssembly().GetReferencedAssemblies())
                {
                    if ((reference.Name ?? string.Empty).StartsWith(""ReSet.Batch"", StringComparison.Ordinal))
                    {
                        // 아직 로드되지 않은 참조는 AppDomain에 나타나지 않는다.
                        try { Assembly.Load(reference); } catch { /* 로드 실패는 아래 필터가 흡수한다 */ }
                    }
                }

                return AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .Where(a => (a.GetName().Name ?? string.Empty).StartsWith(""ReSet.Batch"", StringComparison.Ordinal))
                    .Distinct()
                    .ToList();
            }
        }

        private static IEnumerable<Type> TargetTypes =>
            Targets.SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // 일부 타입이 로드되지 않아도 나머지는 검사한다.
                    return ex.Types.Where(t => t != null)!;
                }
            });

        [Fact]
        public void EverySettleStep_MustInherit_AbstractSettleTasklet()
        {
            var offenders = TargetTypes
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
            var offenders = new List<string>();

            foreach (var assembly in Targets)
            {
                var result = Types.InAssembly(assembly)
                    .That().Inherit(typeof(ReSet.Batch.Core.AbstractSettleTasklet))
                    .ShouldNot().HaveDependencyOn(""Microsoft.Data.SqlClient.SqlConnection"")
                    .GetResult();

                if (!result.IsSuccessful)
                {
                    offenders.AddRange(result.FailingTypeNames ?? Array.Empty<string>());
                }
            }

            Assert.True(offenders.Count == 0,
                ""SqlConnection을 직접 생성한 Tasklet: "" + string.Join("", "", offenders));
        }

        [Fact]
        public void Domain_MustNotDependOn_Infrastructure()
        {
            var offenders = new List<string>();

            foreach (var assembly in Targets)
            {
                var result = Types.InAssembly(assembly)
                    .That().ResideInNamespaceStartingWith(""ReSet.Batch.Domain"")
                    .ShouldNot().HaveDependencyOn(""ReSet.Batch.Infrastructure"")
                    .GetResult();

                if (!result.IsSuccessful)
                {
                    offenders.AddRange(result.FailingTypeNames ?? Array.Empty<string>());
                }
            }

            Assert.True(offenders.Count == 0,
                ""Infrastructure에 의존한 Domain 타입: "" + string.Join("", "", offenders));
        }

        [Fact]
        public void EveryTasklet_MustDeclare_StepNameAndSourceProcName()
        {
            // 검증기는 이 이름으로 설계서와 코드를 짝짓는다. 비어 있으면 매핑이 끊긴다.
            //
            // 생성자 주입(예: ISettleRepository)을 쓰는 Tasklet은 지침 4번(DIP)과
            // common/03-hosting-and-config.md가 권장하는 형태다. 매개변수 없는
            // Activator.CreateInstance가 실패한다는 이유로 그것을 위반으로 기록하면,
            // 도구가 권장한 설계를 도구가 벌하는 셈이 된다 - 에이전트는 이 파일을
            // 통과시키라는 지시를 받으므로 매개변수 없는 생성자로 물러서거나 이
            // 테스트를 고치는 쪽으로 떠밀린다. 그래서 생성자를 부를 수 없으면
            // 생성자를 거치지 않고 인스턴스를 만들어 값만 읽는다.
            //
            // 그 결과로 이 규칙은 ""StepName/SourceProcName은 상수여야 한다""는 요구를
            // 함께 강제한다. 생성자에서 주입받은 필드를 돌려주는 구현은 여기서 빈 값으로
            // 보인다. 검증기는 DI 컨테이너 없이 이 이름만으로 설계서와 코드를 짝짓기
            // 때문에 그 요구 자체가 옳다.
            //
            // SourceProcName은 protected다. 리플렉션은 BindingFlags.NonPublic으로
            // 그 값을 읽을 수 있다 - 이름만 약속하고 실제로는 검사하지 않는 규칙을
            // 남겨 두지 않는다.
            var offenders = new List<string>();
            foreach (var t in TargetTypes
                .Where(t => typeof(ReSet.Batch.Core.AbstractSettleTasklet).IsAssignableFrom(t))
                .Where(t => t.IsClass && !t.IsAbstract))
            {
                object instance;
                try
                {
                    instance = Activator.CreateInstance(t)!;
                }
                catch
                {
                    instance = RuntimeHelpers.GetUninitializedObject(t);
                }

                foreach (var member in new[]
                {
                    (Name: ""StepName"", Flags: BindingFlags.Public | BindingFlags.Instance),
                    (Name: ""SourceProcName"", Flags: BindingFlags.NonPublic | BindingFlags.Instance),
                })
                {
                    var property = t.GetProperty(member.Name, member.Flags);
                    if (property == null)
                    {
                        offenders.Add(t.FullName + $"" ({member.Name}을 선언하지 않음)"");
                        continue;
                    }

                    try
                    {
                        if (string.IsNullOrWhiteSpace(property.GetValue(instance) as string))
                        {
                            offenders.Add(t.FullName + $"" ({member.Name}이 비어 있음 - 상수로 선언하십시오)"");
                        }
                    }
                    catch (Exception ex)
                    {
                        offenders.Add(t.FullName +
                            $"" ({member.Name} 읽기 실패: {ex.GetType().Name} - 주입 필드가 아니라 상수로 선언하십시오)"");
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                ""StepName/SourceProcName 검증에 실패한 Tasklet: "" + string.Join("", "", offenders));
        }

        [Fact]
        public void SettleContext_MustExposeInjectableConnectionFactories()
        {
            // 회차 0에는 Tasklet이 아직 없어 위 규칙 1·2·4가 대상 0건으로 통과한다.
            // 그 시점에 실제로 무언가를 검사하는 규칙이 이것과 Domain 규칙뿐이다.
            //
            // ""DI에서 할당된다""를 컨테이너 없이 그대로 확인할 수는 없다. 대신 그것이
            // 성립하기 위한 두 조건을 검사한다: (1) 팩토리 속성이 밖에서 채워질 수 있는가,
            // (2) 채워 넣을 구현체가 실제로 존재하는가. 둘 중 하나라도 없으면 Tasklet은
            // 커넥션을 스스로 만드는 수밖에 없고, 그러면 검증기의 Rollback 격리가 깨진다.
            var factories = typeof(ReSet.Batch.Core.SettleContext)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => typeof(ReSet.Batch.Core.IDbConnectionFactory).IsAssignableFrom(p.PropertyType))
                .ToList();

            Assert.True(factories.Count > 0,
                ""SettleContext에 IDbConnectionFactory 속성이 하나도 없습니다 - 공통 계약 파일이 수정되었습니다."");

            var notInjectable = factories
                .Where(p => p.SetMethod == null || !p.SetMethod.IsPublic)
                .Select(p => p.Name)
                .ToList();

            Assert.True(notInjectable.Count == 0,
                ""DI가 채울 수 없는(공개 설정자가 없는) 커넥션 팩토리 속성: "" + string.Join("", "", notInjectable));

            var implementations = TargetTypes
                .Where(t => typeof(ReSet.Batch.Core.IDbConnectionFactory).IsAssignableFrom(t))
                .Where(t => t.IsClass && !t.IsAbstract)
                .ToList();

            Assert.True(implementations.Count > 0,
                ""IDbConnectionFactory 구현체가 없습니다 - 회차 0이 DB별 커넥션 팩토리를 만들어야 합니다."");
        }
    }
}
";

        private const string JavaArchitectureTests = @"package com.reset.batch.tests.architecture;

import com.tngtech.archunit.core.domain.JavaClass;
import com.tngtech.archunit.core.domain.JavaClasses;
import com.tngtech.archunit.core.domain.JavaModifier;
import com.tngtech.archunit.core.importer.ClassFileImporter;
import com.tngtech.archunit.lang.syntax.ArchRuleDefinition;
import org.junit.jupiter.api.Test;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * 지시서가 ""반드시""라고 말한 것을 ArchUnit이 강제한다.
 *
 * [이 테스트가 잡지 못하는 것]
 * 경계 규칙 조항 1의 후반부 - ORM(JPA)을 전달받은 커넥션/트랜잭션에 참여시켜야 한다는
 * 요구 - 는 호출 그래프 분석이 필요해 여기서 검증할 수 없다. 그 항목은 도구의 L1
 * 정적 검증이 본다. 이 테스트 통과를 경계 규칙 전체 준수로 읽지 마십시오.
 *
 * (C#과 달리 여기는 어셈블리 경계 문제가 없다 - importPackages가 com.reset.batch 전체를
 *  훑으므로 Tasklet이 어느 모듈에 있든 시야에 들어온다.)
 */
class ArchitectureTests {

    private final JavaClasses classes = new ClassFileImporter().importPackages(""com.reset.batch"");

    @Test
    void everySettleStepMustExtendAbstractSettleTasklet() {
        // ClassesThat 인터페이스에는 abstract 여부만 걸러내는 메서드가 없다 - ArchUnit이
        // 문서화한 방법은 haveModifier/doNotHaveModifier(JavaModifier.ABSTRACT)뿐이다.
        //
        // allowEmptyShould(true): 부트스트랩 회차에는 AbstractSettleTasklet(추상)만 있고
        // 구현체 Tasklet이 아직 없다. 이 규칙의 ""should"" 대상 집합(ISettleStep을 구현하고
        // 인터페이스도 추상도 아닌 클래스)은 그 시점에 비어 있는데, ArchUnit은 기본값으로
        // 빈 집합을 실패로 본다(archRule.failOnEmptyShould). 대상이 아직 없다는 뜻이지
        // 위반이라는 뜻이 아니므로, 없으면 통과로 취급한다.
        ArchRuleDefinition.classes()
            .that().implement(com.reset.batch.core.ISettleStep.class)
            .and().areNotInterfaces().and().doNotHaveModifier(JavaModifier.ABSTRACT)
            .should().beAssignableTo(com.reset.batch.core.AbstractSettleTasklet.class)
            .allowEmptyShould(true)
            .check(classes);
    }

    @Test
    void taskletsMustNotCreateTheirOwnConnection() {
        // allowEmptyShould가 필요 없다 - isAssignableTo는 자기 자신을 포함하므로
        // AbstractSettleTasklet 자신이 항상 이 규칙의 대상 집합에 들어 있다(부트스트랩
        // 회차에도 그 파일은 이미 배치되어 있다).
        ArchRuleDefinition.noClasses()
            .that().areAssignableTo(com.reset.batch.core.AbstractSettleTasklet.class)
            .should().callMethod(javax.sql.DataSource.class, ""getConnection"")
            .check(classes);
    }

    @Test
    void domainMustNotDependOnInfrastructure() {
        // allowEmptyShould(true): 부트스트랩 회차의 스켈레톤에는 ..domain.. 패키지 자체가
        // 아직 없다. 위와 같은 이유로 대상 없음을 위반으로 취급하지 않는다.
        ArchRuleDefinition.noClasses()
            .that().resideInAPackage(""..domain.."")
            .should().dependOnClassesThat().resideInAPackage(""..infrastructure.."")
            .allowEmptyShould(true)
            .check(classes);
    }

    @Test
    void everyTaskletMustDeclareStepNameAndSourceProcName() {
        // 검증기는 이 이름으로 설계서와 코드를 짝짓는다. 비어 있으면 매핑이 끊긴다.
        //
        // 생성자 주입(예: ISettleRepository)을 쓰는 Tasklet은 지침 4번(DIP)이 권장하는
        // 형태다. 인스턴스를 만들 수 없다는 이유로 위반으로 기록하면 도구가 권장한 설계를
        // 도구가 벌한다. [알려진 한계] Java에는 생성자를 건너뛰고 객체를 만드는 표준 API가
        // 없다(C# 쪽은 RuntimeHelpers.GetUninitializedObject를 쓴다). 그래서 매개변수 없는
        // 생성자가 있으면 값까지 확인하고, 없으면 선언 여부까지만 확인한다.
        //
        // ArchUnit의 규칙 DSL 대신 여기서만 리플렉션을 쓰는 이유: 메서드의 반환 ""값""은
        // 바이트코드 분석으로 알 수 없다. 대상 클래스를 찾는 데에만 ArchUnit을 쓴다.
        List<String> offenders = new ArrayList<>();

        for (JavaClass javaClass : classes) {
            Class<?> clazz = javaClass.reflect();
            if (!com.reset.batch.core.AbstractSettleTasklet.class.isAssignableFrom(clazz)) continue;
            if (clazz.isInterface() || Modifier.isAbstract(clazz.getModifiers())) continue;

            Object instance = null;
            try {
                instance = clazz.getDeclaredConstructor().newInstance();
            } catch (ReflectiveOperationException | RuntimeException ignored) {
                // 생성자 주입 Tasklet이다. 값 확인은 포기하되 위반으로 기록하지 않는다.
            }

            for (String name : new String[] { ""getStepName"", ""getSourceProcName"" }) {
                Method method = null;
                try {
                    method = clazz.getMethod(name);
                } catch (NoSuchMethodException outer) {
                    try {
                        // getSourceProcName은 protected라 getMethod로는 찾지 못한다.
                        method = clazz.getDeclaredMethod(name);
                    } catch (NoSuchMethodException inner) {
                        offenders.add(clazz.getName() + ""이 "" + name + ""()을 선언하지 않았습니다"");
                    }
                }

                if (method == null || instance == null) continue;

                method.setAccessible(true);
                try {
                    Object value = method.invoke(instance);
                    if (value == null || value.toString().trim().isEmpty()) {
                        offenders.add(clazz.getName() + ""의 "" + name + ""()이 비어 있습니다 - 상수로 선언하십시오"");
                    }
                } catch (ReflectiveOperationException e) {
                    offenders.add(clazz.getName() + ""의 "" + name + ""() 호출 실패: "" + e.getClass().getSimpleName());
                }
            }
        }

        assertTrue(offenders.isEmpty(),
            ""StepName/SourceProcName 검증에 실패한 Tasklet: "" + String.join("", "", offenders));
    }

    @Test
    void settleContextMustExposeInjectableConnectionFactories() {
        // 회차 0에는 Tasklet이 아직 없어 위 규칙들이 대상 0건으로 통과한다. 그 시점에
        // 실제로 무언가를 검사하는 규칙이 이것과 domain 규칙뿐이다.
        //
        // ""DI에서 할당된다""를 컨테이너 없이 그대로 확인할 수는 없다. 대신 그것이 성립하기
        // 위한 두 조건을 본다: (1) 팩토리 필드가 밖에서 채워질 수 있는가, (2) 채워 넣을
        // 구현체가 실제로 존재하는가. 둘 중 하나라도 없으면 Tasklet은 커넥션을 스스로
        // 만드는 수밖에 없고, 그러면 검증기의 Rollback 격리가 깨진다.
        List<String> offenders = new ArrayList<>();
        int factoryFields = 0;

        for (Field field : com.reset.batch.core.SettleContext.class.getDeclaredFields()) {
            if (!com.reset.batch.core.IDbConnectionFactory.class.isAssignableFrom(field.getType())) continue;
            factoryFields++;

            String setter = ""set"" + Character.toUpperCase(field.getName().charAt(0)) + field.getName().substring(1);
            try {
                com.reset.batch.core.SettleContext.class.getMethod(
                    setter, com.reset.batch.core.IDbConnectionFactory.class);
            } catch (NoSuchMethodException e) {
                offenders.add(field.getName() + ""에 공개 설정자("" + setter + "")가 없어 DI가 채울 수 없습니다"");
            }
        }

        assertTrue(factoryFields > 0,
            ""SettleContext에 IDbConnectionFactory 필드가 하나도 없습니다 - 공통 계약 파일이 수정되었습니다."");
        assertTrue(offenders.isEmpty(), ""DI가 채울 수 없는 커넥션 팩토리: "" + String.join("", "", offenders));

        boolean hasImplementation = false;
        for (JavaClass javaClass : classes) {
            Class<?> clazz = javaClass.reflect();
            if (com.reset.batch.core.IDbConnectionFactory.class.isAssignableFrom(clazz)
                    && !clazz.isInterface() && !Modifier.isAbstract(clazz.getModifiers())) {
                hasImplementation = true;
                break;
            }
        }

        assertTrue(hasImplementation,
            ""IDbConnectionFactory 구현체가 없습니다 - 회차 0이 DB별 커넥션 팩토리를 만들어야 합니다."");
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

        private const string CSharpAssemblyCompletenessTests = @"using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ReSet.Batch.Tests.Architecture
{
    /// <summary>
    /// 조립 회차 전용. 이 파일은 조립 회차의 지시서만 배치를 요구한다.
    ///
    /// 아키텍처 규칙들은 대상이 0건이면 통과한다 - 회차 0에는 Tasklet이 없으므로
    /// 그것이 정상이다. 그래서 ""하나도 없다""를 실패로 보는 판정은 모든 단계가
    /// 구현된 뒤에만 켤 수 있고, 스텁은 자신이 몇 회차에 놓이는지 알 수 없다.
    /// 파일을 나누고 배치 지시를 조립 회차에만 두어 그 스위치를 만든다.
    /// </summary>
    public class AssemblyCompletenessTests
    {
        [Fact]
        public void Assembly_MustContainAtLeastOneTasklet()
        {
            var taskletCount = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Where(a => (a.GetName().Name ?? string.Empty).StartsWith(""ReSet.Batch"", StringComparison.Ordinal))
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
                })
                .Count(t => t != null
                    && t.IsClass
                    && !t.IsAbstract
                    && typeof(ReSet.Batch.Core.AbstractSettleTasklet).IsAssignableFrom(t));

            Assert.True(taskletCount > 0,
                ""Tasklet이 0개입니다. 아키텍처 규칙들이 대상 0건으로 통과했을 뿐 아무것도 검사하지 않았습니다."");
        }
    }
}
";

        private const string JavaAssemblyCompletenessTests = @"package com.reset.batch.tests.architecture;

import com.tngtech.archunit.core.domain.JavaClass;
import com.tngtech.archunit.core.domain.JavaClasses;
import com.tngtech.archunit.core.domain.JavaModifier;
import com.tngtech.archunit.core.importer.ClassFileImporter;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * 조립 회차 전용. 이 파일은 조립 회차의 지시서만 배치를 요구한다.
 *
 * 아키텍처 규칙들은 대상이 0건이면 통과한다 - 부트스트랩 회차에는 Tasklet이 없으므로
 * 그것이 정상이다. ""하나도 없다""를 실패로 보는 판정은 모든 단계가 구현된 뒤에만
 * 켤 수 있다.
 */
class AssemblyCompletenessTests {

    private final JavaClasses classes = new ClassFileImporter().importPackages(""com.reset.batch"");

    @Test
    void assemblyMustContainAtLeastOneTasklet() {
        long taskletCount = classes.stream()
            .filter(c -> !c.getModifiers().contains(JavaModifier.ABSTRACT))
            .filter(c -> c.isAssignableTo(""com.reset.batch.core.AbstractSettleTasklet""))
            .count();

        assertTrue(taskletCount > 0,
            ""Tasklet이 0개입니다. 아키텍처 규칙들이 대상 0건으로 통과했을 뿐 아무것도 검사하지 않았습니다."");
    }
}
";

        /// <summary>
        /// 조립 회차에서만 켜지는 0건 판정. 배치 지시가 활성화 스위치다.
        /// </summary>
        public static string AssemblyCompletenessTestStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaAssemblyCompletenessTests
                : CSharpAssemblyCompletenessTests;

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

        // C#은 파일 하나에 public 타입을 여러 개 둘 수 있어 ISettleStepDescriptor와
        // ISettleRepository를 CSharpRepositoryContract 한 곳에 묶어도 컴파일된다. Java는
        // 파일당 public 최상위 타입 하나 규칙이 있고, 두 타입 모두 구현체가 core 패키지
        // 밖(에이전트가 만드는 Step/Repository 구현 패키지)에 있어야 하므로 각각 public이어야
        // 한다 - 그래서 JavaRepositoryContract는 ISettleStepDescriptor만 담고,
        // ISettleRepository는 JavaRepositoryInterfaceStub이라는 별도 파일로 낸다.
        private const string JavaRepositoryContract = @"package com.reset.batch.core;

/**
 * 단계 실행 순서를 선언으로 고정한다. 회차마다 다른 프로세스가 Tasklet을 추가하므로,
 * 순서를 조립 코드에 흩어 두면 회차 간에 어긋난다.
 */
public interface ISettleStepDescriptor {
    int getOrder();
    ISettleStep getStep();
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

        private const string JavaSettleRepositoryInterface = @"package com.reset.batch.core;

import java.util.List;

/**
 * 데이터 액세스 계층의 최소 계약. 구현체는 회차 0에서 만든다.
 * 대량 DML·집계·청킹은 이 인터페이스 뒤에서도 파라미터 바인딩 SQL로 작성한다.
 *
 * ISettleStepDescriptor와 한 파일에 묶지 않는다 - 이 인터페이스의 구현체는 인프라
 * 패키지(core 밖)에 있어야 하는데, Java에서 다른 패키지가 구현할 수 있으려면
 * 인터페이스 자체가 public이어야 하고, public 최상위 타입은 파일당 하나만 허용된다.
 */
public interface ISettleRepository {
    int executeNonQuery(String sql, Object parameters);
    <T> List<T> query(String sql, Object parameters, Class<T> type);
}
";

        /// <summary>
        /// Java 전용. ISettleRepository를 ISettleStepDescriptor.java와 분리된 public
        /// 파일(ISettleRepository.java)로 낸다. C#은 ISettleRepository가 이미
        /// RepositoryContractStub("C#") 안에 public으로 들어 있으므로 이 상수를 쓰지 않는다.
        /// </summary>
        public static string JavaRepositoryInterfaceStub => JavaSettleRepositoryInterface;

        private const string CSharpAbstractTasklet = @"using System;
using System.Data;

namespace ReSet.Batch.Core
{
    public interface ISettleStep
    {
        string StepName { get; }
        StepResult Execute(SettleContext context);
    }

    public abstract class AbstractSettleTasklet : ISettleStep
    {
        public abstract string StepName { get; }
        protected abstract string SourceProcName { get; }

        public StepResult Execute(SettleContext context)
        {
            if (context.Checkpoint?.IsStepCompleted(StepName, context.Ymd) == true)
            {
                return new StepResult { Code = 0, Message = ""이미 완료된 Step 재시작 스킵"", SourceProcName = SourceProcName };
            }

            int stateCode = 0;
            using var conn = context.MainDb.CreateConnection();
            conn.Open();
            using (var cmdIso = conn.CreateCommand())
            {
                cmdIso.CommandText = ""SET XACT_ABORT ON; SET TRANSACTION ISOLATION LEVEL SNAPSHOT;"";
                cmdIso.ExecuteNonQuery();
            }

            try
            {
                var preCheckFail = PreCheck(conn, context, ref stateCode);
                if (preCheckFail != null) return preCheckFail;

                using var tran = conn.BeginTransaction();
                try
                {
                    RunBusinessSteps(conn, tran, context, ref stateCode);
                    tran.Commit();
                    context.Checkpoint.MarkStepCompleted(StepName, context.Ymd);
                    return new StepResult { Code = 0, Message = ""정상 완료"", SourceProcName = SourceProcName };
                }
                catch
                {
                    if (tran.Connection != null) tran.Rollback();
                    OnFailureCompensation(context, stateCode);
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new StepResult { Code = stateCode, Message = ex.Message, SourceProcName = SourceProcName };
            }
        }

        protected abstract StepResult PreCheck(IDbConnection conn, SettleContext context, ref int stateCode);
[[ORM_BOUNDARY]]
        protected abstract void RunBusinessSteps(IDbConnection conn, IDbTransaction tran, SettleContext context, ref int stateCode);
        protected virtual void OnFailureCompensation(SettleContext context, int failedStateCode) { }
    }

    public class SettleContext
    {
        public string Ymd { get; set; }
        public bool BypassPreCheck { get; set; }
        // 계획서는 Shadow 이름(batch_shadow.<Table>_<RunId>_<StepCode>), 체크포인트 키,
        // 오류 로그, 게시 Manifest를 전부 아래 값으로 짓는다. 스텁이 주지 않으면
        // 회차마다 다른 우회가 생겨 회차 간 코드가 어긋난다.
        //
        // 계획서 본문의 비동기 실행 계약·확장 결과 타입은 설계 의도 설명이다.
        // 실행 계약은 여기 있는 동기 Execute 하나다.
        public Guid RunId { get; set; }
        public string InputHash { get; set; }
        public string SourceSnapshotId { get; set; }
        public IDbConnectionFactory MainDb { get; set; }
        public IDbConnectionFactory PaymentDb { get; set; }
        public IDbConnectionFactory SettleCardDb { get; set; }
        public IDbConnectionFactory PlCardDb { get; set; }
        public ICheckpointRepository Checkpoint { get; set; }
    }

    public class StepResult
    {
        public int Code { get; set; }
        public string Message { get; set; }
        public string SourceProcName { get; set; }
        public string PoStrErrMsg { get; set; }
        public bool IsSuccess => Code == 0;
    }

    public interface IDbConnectionFactory { IDbConnection CreateConnection(); }
    public interface ICheckpointRepository
    {
        bool IsStepCompleted(string stepName, string ymd);
        void MarkStepCompleted(string stepName, string ymd);
    }
}";

        private const string JavaSettleContext = @"package com.reset.batch.core;

/**
 * Step 실행 컨텍스트. Tasklet 서브클래스가 다른 패키지에 있으므로 public이어야 한다 -
 * package-private이면 그 패키지에서 execute/preCheck/runBusinessSteps의 시그니처
 * 자체를 적을 수 없다.
 */
public class SettleContext {
    private String ymd;
    private boolean bypassPreCheck;
    private java.util.UUID runId;
    private String inputHash;
    private String sourceSnapshotId;
    private IDbConnectionFactory mainDb;
    private IDbConnectionFactory paymentDb;
    private IDbConnectionFactory settleCardDb;
    private IDbConnectionFactory plCardDb;
    private ICheckpointRepository checkpoint;

    public String getYmd() { return ymd; }
    public void setYmd(String ymd) { this.ymd = ymd; }
    public boolean isBypassPreCheck() { return bypassPreCheck; }
    public void setBypassPreCheck(boolean bypassPreCheck) { this.bypassPreCheck = bypassPreCheck; }
    public java.util.UUID getRunId() { return runId; }
    public void setRunId(java.util.UUID runId) { this.runId = runId; }
    public String getInputHash() { return inputHash; }
    public void setInputHash(String inputHash) { this.inputHash = inputHash; }
    public String getSourceSnapshotId() { return sourceSnapshotId; }
    public void setSourceSnapshotId(String sourceSnapshotId) { this.sourceSnapshotId = sourceSnapshotId; }
    public IDbConnectionFactory getMainDb() { return mainDb; }
    public void setMainDb(IDbConnectionFactory mainDb) { this.mainDb = mainDb; }
    public IDbConnectionFactory getPaymentDb() { return paymentDb; }
    public void setPaymentDb(IDbConnectionFactory paymentDb) { this.paymentDb = paymentDb; }
    public IDbConnectionFactory getSettleCardDb() { return settleCardDb; }
    public void setSettleCardDb(IDbConnectionFactory settleCardDb) { this.settleCardDb = settleCardDb; }
    public IDbConnectionFactory getPlCardDb() { return plCardDb; }
    public void setPlCardDb(IDbConnectionFactory plCardDb) { this.plCardDb = plCardDb; }
    public ICheckpointRepository getCheckpoint() { return checkpoint; }
    public void setCheckpoint(ICheckpointRepository checkpoint) { this.checkpoint = checkpoint; }
}
";

        private const string JavaAbstractTasklet = @"package com.reset.batch.core;

import java.sql.Connection;
import java.sql.SQLException;
import java.sql.Statement;

/**
 * C# 쪽 AbstractSettleTasklet과 같은 책임을 진다: 재시작 스킵 확인, 격리 수준 설정,
 * 트랜잭션 경계, 실패 시 보상 호출을 여기서 한 번만 구현하고 Step 저자는 preCheck·
 * runBusinessSteps만 채운다.
 *
 * JDBC에는 IDbTransaction에 대응하는 별도 타입이 없다 - Connection의 autoCommit을
 * 끄고 commit()/rollback()으로 경계를 표시하므로, C# 쪽 conn/tran 두 인자가 여기서는
 * Connection 하나로 합쳐진다. ref int stateCode도 Java에는 대응이 없어 out 매개변수
 * 대신 보호된 필드로 옮겼다 - preCheck/runBusinessSteps 구현체가 실패 분류 코드를
 * 남기고 싶으면 setStateCode를 호출한다.
 */
public abstract class AbstractSettleTasklet implements ISettleStep {

    private int stateCode = 0;

    protected abstract String getSourceProcName();

    @Override
    public StepResult execute(SettleContext context) {
        if (context.getCheckpoint() != null
                && context.getCheckpoint().isStepCompleted(getStepName(), context.getYmd())) {
            return new StepResult(0, ""이미 완료된 Step 재시작 스킵"", getSourceProcName());
        }

        try (Connection conn = context.getMainDb().createConnection()) {
            try (Statement isolationStmt = conn.createStatement()) {
                isolationStmt.execute(""SET XACT_ABORT ON; SET TRANSACTION ISOLATION LEVEL SNAPSHOT;"");
            }

            StepResult preCheckFail = preCheck(conn, context);
            if (preCheckFail != null) {
                return preCheckFail;
            }

            conn.setAutoCommit(false);
            try {
                runBusinessSteps(conn, context);
                conn.commit();
                context.getCheckpoint().markStepCompleted(getStepName(), context.getYmd());
                return new StepResult(0, ""정상 완료"", getSourceProcName());
            } catch (Exception ex) {
                conn.rollback();
                onFailureCompensation(context, stateCode);
                throw ex;
            }
        } catch (Exception ex) {
            return new StepResult(stateCode, ex.getMessage(), getSourceProcName());
        }
    }

    /** preCheck/runBusinessSteps 구현체가 실패 분류 코드를 남기고 싶으면 이 메서드로 갱신한다. */
    protected void setStateCode(int stateCode) {
        this.stateCode = stateCode;
    }

    protected abstract StepResult preCheck(Connection conn, SettleContext context) throws SQLException;

[[ORM_BOUNDARY_JAVA]]
    protected abstract void runBusinessSteps(Connection conn, SettleContext context) throws SQLException;

    protected void onFailureCompensation(SettleContext context, int failedStateCode) {
    }
}
";

        /// <summary>
        /// AbstractSettleTasklet(Java) 스텁에 삽입할 ORM 경계 주석. C# 쪽 TaskletOrmComment와
        /// 같은 위치(runBusinessSteps 바로 위)에 심는다. TaskletOrmComment는 EF Core/
        /// SqlConnection 전용 C# 구문이라 그대로 재사용할 수 없어 별도로 둔다.
        /// </summary>
        public static string JavaTaskletOrmComment => @"    // [데이터 액세스 경계] ORM(Spring Data JPA)은 MigrationInstructions.md 5장의 허용 목록에
    // 한해 사용한다. 사용할 경우 반드시 이 메서드가 받은 conn에 참여시켜야 하며, 새
    // 커넥션이나 새 트랜잭션을 만들면 검증기의 Rollback 격리가 깨져 정합성 대조 결과가
    // 오염된다. Spring 관리 트랜잭션(JpaTransactionManager)을 쓰더라도 그 트랜잭션이
    // 이 conn 위에서 열려야 한다. 정산 대상 대량 DML, 집계, 청킹 루프, Shadow 처리,
    // 세션 제어는 파라미터 바인딩 SQL(MyBatis)로 작성한다.";

        /// <summary>
        /// 코딩 에이전트가 강제로 상속해야 하는 베이스 클래스 스텁.
        ///
        /// MetadataExporter의 인라인 문자열에서 여기로 옮겼다. 나머지 계약 자산
        /// (ArchitectureTests·SettleContracts)은 이미 이 클래스에 있어 테스트가
        /// 붙어 있었는데, 정작 "반드시 상속하라"고 지시받는 이 파일만 테스트가
        /// 없었다 - 지시서가 가장 강하게 요구하는 것이 가장 검사되지 않았다.
        ///
        /// ORM 경계 주석 치환까지 마친 최종 문자열을 돌려준다. 두 언어가 서로 다른
        /// 자리표시자를 쓰므로 치환도 언어별로 다르다 - 치환을 호출부에 남기면
        /// 호출부가 하나 늘 때마다 자리표시자가 그대로 나갈 위험이 생긴다.
        /// </summary>
        public static string AbstractTaskletStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaAbstractTasklet.Replace("[[ORM_BOUNDARY_JAVA]]", JavaTaskletOrmComment)
                : CSharpAbstractTasklet.Replace("[[ORM_BOUNDARY]]", TaskletOrmComment);

        /// <summary>
        /// Java 전용 SettleContext.java. C#의 SettleContext는
        /// <see cref="AbstractTaskletStub"/> 문자열 안에 들어 있으므로 이 메서드로
        /// 얻을 수 없다 - 언어를 착각한 호출은 중복 파일을 산출물로 내보내므로
        /// 조용히 통과시키지 않고 던진다.
        /// </summary>
        public static string SettleContextStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaSettleContext
                : throw new NotSupportedException(
                    "C#의 SettleContext는 AbstractTaskletStub 안에 포함되어 있습니다.");

        private const string CSharpStepLogicTests = @"using Xunit;
using Moq;

namespace ReSet.Batch.Tests
{
    /// <summary>
    /// 이 회차가 구현한 단계의 동작을 검증하는 테스트를 여기에 쓰십시오.
    ///
    /// 파일명은 반드시 <c>LogicTests_&lt;단계코드&gt;.cs</c> 형태로 만드십시오
    /// (예: LogicTests_S08.cs). 단계 코드로 <b>시작하는</b> 이름(S08LogicTests.cs)은
    /// 쓰지 마십시오 - 검증기가 파일명 접두사로 그 회차의 산출물을 찾기 때문에,
    /// 테스트 파일이 Tasklet 자리를 차지해 구현을 빼먹어도 통과한 것처럼 보입니다.
    ///
    /// 최소 한 개: PreCheck 차단 경로 또는 RunBusinessSteps의 대표 분기.
    /// </summary>
    public class StepLogicTests
    {
        [Fact]
        public void Step_ShouldHaveAtLeastOneBehaviourTest()
        {
            Assert.Fail(
                ""이 회차의 단계 동작 테스트가 아직 없습니다. 이 Fact를 실제 테스트로 교체하십시오."");
        }
    }
}
";

        private const string JavaStepLogicTests = @"package com.reset.batch.tests;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.fail;

/**
 * 이 회차가 구현한 단계의 동작을 검증하는 테스트를 여기에 쓰십시오.
 *
 * 파일명은 반드시 LogicTests_<단계코드>.java 형태로 만드십시오(예: LogicTests_S08.java).
 * 단계 코드로 시작하는 이름(S08LogicTests.java)은 쓰지 마십시오 - 검증기가 파일명
 * 접두사로 그 회차의 산출물을 찾기 때문에, 테스트 파일이 Tasklet 자리를 차지해
 * 구현을 빼먹어도 통과한 것처럼 보입니다.
 *
 * 최소 한 개: preCheck 차단 경로 또는 runBusinessSteps의 대표 분기.
 */
public class StepLogicTests {
    @Test
    public void step_ShouldHaveAtLeastOneBehaviourTest() {
        fail(""이 회차의 단계 동작 테스트가 아직 없습니다. 이 테스트를 실제 테스트로 교체하십시오."");
    }
}
";

        /// <summary>
        /// 회차가 채워야 하는 단계 동작 테스트의 스캐폴드.
        ///
        /// 이전 스텁은 본문이 주석 세 줄이라 통과해도 아무것도 보장하지 않았는데,
        /// 지시서 규칙 6은 "제공된 자가 검증용 단위 테스트를 통과시키라"고 말한다 -
        /// 빈 테스트를 방어로 착각하는 구조였다. 미구현 상태가 실패로 드러나게 한다.
        /// </summary>
        public static string StepLogicTestStub(string targetLanguage) =>
            targetLanguage.Equals("Java", StringComparison.OrdinalIgnoreCase)
                ? JavaStepLogicTests
                : CSharpStepLogicTests;
    }
}
