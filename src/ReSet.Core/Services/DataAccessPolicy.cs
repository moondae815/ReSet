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
    }
}
