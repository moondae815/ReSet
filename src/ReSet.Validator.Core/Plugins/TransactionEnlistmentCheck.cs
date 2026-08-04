using System.Text.RegularExpressions;

namespace ReSet.Validator.Core.Plugins
{
    /// <summary>
    /// 데이터 액세스 경계의 "항상 적용 조항 1"(ORM을 전달받은 커넥션/트랜잭션에 참여시킬 것)을
    /// L1에서 기계적으로 검사한다. 규칙 본문은 <see cref="ReSet.Core.Services.DataAccessPolicy"/>가
    /// 소유하며, 이 클래스는 그중 기계 판정이 가능한 한 조항만 잡는다.
    ///
    /// 이 조항을 따로 떼어내는 이유: 이를 어기면 검증기의 Rollback 격리(CSharpReflectionRunner)가
    /// 깨져 1:1 정합성 대조 결과 자체가 오염된다. 다른 조항의 위반은 코드 품질 문제지만,
    /// 이 조항의 위반은 검증 결과를 신뢰할 수 없게 만든다.
    ///
    /// 한계 — 오탐이 정상 코드를 파이프라인에서 막으므로 명백한 위반만 잡는다. DI로 주입된
    /// 컨텍스트가 참여하지 않는 경우는 파일 단위 검사로 판정할 수 없어 L2의 AI 판단에 남는다.
    /// </summary>
    public static class TransactionEnlistmentCheck
    {
        // 컨텍스트를 코드에서 직접 만드는 형태만 잡는다. DbContext 파생 클래스의
        // OnConfiguring(DbContextOptionsBuilder ...)은 인자로 받을 뿐 생성하지 않으므로 걸리지 않는다.
        private static readonly Regex ContextConstruction =
            new Regex(@"new\s+DbContextOptionsBuilder\s*<", RegexOptions.Compiled);

        private static readonly Regex Enlistment =
            new Regex(@"\.UseTransaction\s*\(", RegexOptions.Compiled);

        private static readonly Regex AmbientTransaction =
            new Regex(@"new\s+TransactionScope\s*\(", RegexOptions.Compiled);

        // EF가 스스로 트랜잭션을 여는 형태만 잡는다. 전달용 트랜잭션을 만드는
        // conn.BeginTransaction()은 ReSet이 생성하는 AbstractSettleTasklet 스텁의 정상 형태다.
        private static readonly Regex EfOwnedTransaction =
            new Regex(@"\.Database\s*\.\s*BeginTransaction\s*\(", RegexOptions.Compiled);

        private static readonly Regex JavaOwnedTransaction =
            new Regex(@"\.getTransaction\s*\(\s*\)\s*\.\s*begin\s*\(", RegexOptions.Compiled);

        private static readonly Regex JavaRequiresNew =
            new Regex(@"REQUIRES_NEW", RegexOptions.Compiled);

        /// <summary>
        /// 위반이 없으면 null, 있으면 L1 오류 메시지를 돌려준다.
        /// </summary>
        public static string? FindCSharpViolation(string sourceCode)
        {
            if (AmbientTransaction.IsMatch(sourceCode))
            {
                return "데이터 액세스 경계 위반: new TransactionScope로 새 트랜잭션을 만들었습니다. "
                    + "전달받은 트랜잭션에 참여시켜야 합니다 (지시서 5장 항상 조항 1).";
            }

            if (EfOwnedTransaction.IsMatch(sourceCode))
            {
                return "데이터 액세스 경계 위반: Database.BeginTransaction으로 ORM이 자체 트랜잭션을 열었습니다. "
                    + "전달받은 트랜잭션에 참여시켜야 합니다 (지시서 5장 항상 조항 1).";
            }

            if (ContextConstruction.IsMatch(sourceCode) && !Enlistment.IsMatch(sourceCode))
            {
                return "데이터 액세스 경계 위반: ORM 컨텍스트를 생성했으나 UseTransaction 호출이 없습니다. "
                    + "전달받은 트랜잭션에 참여시켜야 합니다 (지시서 5장 항상 조항 1).";
            }

            return null;
        }

        /// <summary>
        /// 위반이 없으면 null, 있으면 L1 오류 메시지를 돌려준다.
        /// </summary>
        public static string? FindJavaViolation(string sourceCode)
        {
            if (JavaOwnedTransaction.IsMatch(sourceCode))
            {
                return "데이터 액세스 경계 위반: getTransaction().begin()으로 ORM이 자체 트랜잭션을 열었습니다. "
                    + "컨테이너가 관리하는 트랜잭션에 참여시켜야 합니다 (지시서 5장 항상 조항 1).";
            }

            if (JavaRequiresNew.IsMatch(sourceCode))
            {
                return "데이터 액세스 경계 위반: REQUIRES_NEW 전파 설정으로 새 트랜잭션을 만들었습니다. "
                    + "전달받은 트랜잭션에 참여시켜야 합니다 (지시서 5장 항상 조항 1).";
            }

            return null;
        }
    }
}
