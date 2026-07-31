using ReSet.Core.Models;

namespace ReSet.Core.Tests;

public class CodeObjectKeyTests
{
    [Fact]
    public void CodeObjectKey_DistinguishesDatabaseAndObjectType()
    {
        var procedure = new CodeObjectKey("PaymentDB", "dbo", "Calc", CodeObjectType.Procedure);
        var function = new CodeObjectKey("PaymentDB", "dbo", "Calc", CodeObjectType.Function);
        var external = new CodeObjectKey("AuditDB", "dbo", "Calc", CodeObjectType.Procedure);

        Assert.NotEqual(procedure, function);
        Assert.NotEqual(procedure, external);
        Assert.Equal(procedure, new CodeObjectKey("paymentdb", "DBO", "calc", CodeObjectType.Procedure));
    }

    [Fact]
    public void AnalysisNode_InitializesAsQueued()
    {
        var node = new AnalysisNode(new CodeObjectKey("PaymentDB", "dbo", "usp_A", CodeObjectType.Procedure));

        Assert.Equal(AnalysisNodeStatus.Queued, node.Status);
    }
}
