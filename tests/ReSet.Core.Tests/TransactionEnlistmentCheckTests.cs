using ReSet.Validator.Core.Plugins;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 데이터 액세스 경계의 항상 조항 1(전달받은 커넥션/트랜잭션에 참여)을 L1에서 기계적으로
    /// 잡는 검사. 오탐이 나면 정상 코드가 파이프라인에서 막히므로, 모호한 신호는 통과시키고
    /// 명백한 위반만 잡는지를 함께 고정한다.
    /// </summary>
    public class TransactionEnlistmentCheckTests
    {
        [Fact]
        public void CSharp_WithApprovedEnlistmentPattern_ReportsNoViolation()
        {
            var source = @"
public class SettleTasklet : AbstractSettleTasklet
{
    protected override void RunBusinessSteps(IDbConnection conn, IDbTransaction tran, SettleContext context, ref int stateCode)
    {
        var options = new DbContextOptionsBuilder<SettleContextDb>().UseSqlServer((SqlConnection)conn).Options;
        using var db = new SettleContextDb(options);
        db.Database.UseTransaction((SqlTransaction)tran);
    }
}";

            Assert.Null(TransactionEnlistmentCheck.FindCSharpViolation(source));
        }

        [Fact]
        public void CSharp_ConstructingAContextWithoutUseTransaction_ReportsViolation()
        {
            var source = @"
public class SettleTasklet : AbstractSettleTasklet
{
    protected override void RunBusinessSteps(IDbConnection conn, IDbTransaction tran, SettleContext context, ref int stateCode)
    {
        var options = new DbContextOptionsBuilder<SettleContextDb>().UseSqlServer((SqlConnection)conn).Options;
        using var db = new SettleContextDb(options);
        db.SaveChanges();
    }
}";

            var violation = TransactionEnlistmentCheck.FindCSharpViolation(source);

            Assert.NotNull(violation);
            Assert.Contains("UseTransaction", violation);
        }

        [Fact]
        public void CSharp_WithTransactionScope_ReportsViolation()
        {
            var source = @"
public class SettleTasklet
{
    public void Run(IDbConnection conn, IDbTransaction tran)
    {
        using var scope = new TransactionScope();
        scope.Complete();
    }
}";

            var violation = TransactionEnlistmentCheck.FindCSharpViolation(source);

            Assert.NotNull(violation);
            Assert.Contains("TransactionScope", violation);
        }

        [Fact]
        public void CSharp_WithEfOwnedTransaction_ReportsViolation()
        {
            var source = @"
public class SettleTasklet
{
    public void Run(SettleContextDb db)
    {
        using var tx = db.Database.BeginTransaction();
        db.SaveChanges();
        tx.Commit();
    }
}";

            var violation = TransactionEnlistmentCheck.FindCSharpViolation(source);

            Assert.NotNull(violation);
            Assert.Contains("BeginTransaction", violation);
        }

        [Fact]
        public void CSharp_WithTheGeneratedBaseClassTransaction_ReportsNoViolation()
        {
            // ReSet이 직접 생성해 주는 AbstractSettleTasklet 스텁이 이 형태다.
            // 이것이 바로 ORM이 참여해야 할 트랜잭션이므로 위반이 아니다.
            var source = @"
public abstract class AbstractSettleTasklet
{
    public StepResult Execute(SettleContext context)
    {
        using var conn = new SqlConnection(context.ConnectionString);
        conn.Open();
        using var tran = conn.BeginTransaction();
        RunBusinessSteps(conn, tran, context, ref stateCode);
        tran.Commit();
    }
}";

            Assert.Null(TransactionEnlistmentCheck.FindCSharpViolation(source));
        }

        [Fact]
        public void CSharp_WithADbContextClassDefinition_ReportsNoViolation()
        {
            // DbContext 파생 클래스는 OnConfiguring에서 DbContextOptionsBuilder를 인자로 받는다.
            // 컨텍스트를 생성하는 코드가 아니므로 UseTransaction이 없어도 위반이 아니다.
            var source = @"
public class SettleContextDb : DbContext
{
    public DbSet<SettleRow> SettleRows { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
    }
}";

            Assert.Null(TransactionEnlistmentCheck.FindCSharpViolation(source));
        }

        [Fact]
        public void CSharp_WithPlainSqlOnly_ReportsNoViolation()
        {
            var source = @"
public class SettleTasklet : AbstractSettleTasklet
{
    protected override void RunBusinessSteps(IDbConnection conn, IDbTransaction tran, SettleContext context, ref int stateCode)
    {
        conn.Execute(@""UPDATE dbo.Settle SET Amount = @Amount WHERE Id = @Id"", new { Amount = 1, Id = 2 }, tran);
    }
}";

            Assert.Null(TransactionEnlistmentCheck.FindCSharpViolation(source));
        }

        [Fact]
        public void Java_WithEntityManagerOwnedTransaction_ReportsViolation()
        {
            var source = @"
public class SettleTasklet implements Tasklet {
    public RepeatStatus execute(StepContribution contribution, ChunkContext chunkContext) {
        entityManager.getTransaction().begin();
        entityManager.persist(row);
        entityManager.getTransaction().commit();
        return RepeatStatus.FINISHED;
    }
}";

            var violation = TransactionEnlistmentCheck.FindJavaViolation(source);

            Assert.NotNull(violation);
            Assert.Contains("getTransaction", violation);
        }

        [Fact]
        public void Java_WithRequiresNewPropagation_ReportsViolation()
        {
            var source = @"
public class SettleTasklet implements Tasklet {
    @Transactional(propagation = Propagation.REQUIRES_NEW)
    public void run() {
        repository.save(row);
    }
}";

            var violation = TransactionEnlistmentCheck.FindJavaViolation(source);

            Assert.NotNull(violation);
            Assert.Contains("REQUIRES_NEW", violation);
        }

        [Fact]
        public void Java_WithPlainMyBatisMapper_ReportsNoViolation()
        {
            var source = @"
public class SettleTasklet implements Tasklet {
    public RepeatStatus execute(StepContribution contribution, ChunkContext chunkContext) {
        settleMapper.updateAmount(id, amount);
        return RepeatStatus.FINISHED;
    }
}";

            Assert.Null(TransactionEnlistmentCheck.FindJavaViolation(source));
        }
    }
}
