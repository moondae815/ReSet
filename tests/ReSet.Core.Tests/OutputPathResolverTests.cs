using System;
using System.IO;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public class OutputPathResolverTests
{
    [Fact]
    public void ResolveSpecPath_KeepsExistingProcedurePathForCurrentDatabase()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var key = CodeObjectKey.Create(
            "PaymentDB",
            "dbo",
            "usp_Settle",
            CodeObjectType.Procedure);

        Assert.Equal(
            "/tmp/output/Procedures/dbo.usp_Settle/docs/Spec.md",
            paths.ResolveSpecPath(key));
    }

    [Fact]
    public void ResolveSpecPath_SeparatesExternalFunction()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var key = CodeObjectKey.Create(
            "AuditDB",
            "dbo",
            "FN_Calc",
            CodeObjectType.Function);

        Assert.Equal(
            "/tmp/output/External/AuditDB/Functions/dbo.FN_Calc/docs/Spec.md",
            paths.ResolveSpecPath(key));
    }

    [Fact]
    public void ResolveSpecPath_SeparatesCurrentFunctionFromSameNamedProcedure()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var functionKey = CodeObjectKey.Create(
            "paymentdb",
            "dbo",
            "Calculate",
            CodeObjectType.Function);

        Assert.Equal(
            "/tmp/output/Functions/dbo.Calculate/docs/Spec.md",
            paths.ResolveSpecPath(functionKey));
    }

    [Fact]
    public void ResolveArtifactPaths_UseCanonicalObjectAndDocumentLocations()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var currentProcedure = CodeObjectKey.Create(
            "PaymentDB",
            "dbo",
            "usp_Settle",
            CodeObjectType.Procedure);
        var externalFunction = CodeObjectKey.Create(
            "AuditDB",
            "calc",
            "FN_Fee",
            CodeObjectType.Function);

        Assert.Equal(
            "/tmp/output/Procedures/dbo.usp_Settle/docs",
            paths.ResolveDocsDirectory(currentProcedure));
        Assert.Equal(
            "/tmp/output/Objects/dbo.usp_Settle.Procedure/raw/object_definition.sql",
            paths.ResolveCanonicalDdlPath(currentProcedure));
        Assert.Equal(
            "/tmp/output/Objects/AuditDB/calc.FN_Fee.Function/raw/object_definition.sql",
            paths.ResolveCanonicalDdlPath(externalFunction));
        Assert.Equal(
            "/tmp/output/External/AuditDB/Functions/calc.FN_Fee/raw/dependency-manifest.json",
            paths.ResolveManifestPath(externalFunction));
    }

    [Fact]
    public void ResolveSpecPath_ReplacesInvalidFileNameCharactersInSegments()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var key = CodeObjectKey.Create(
            "Audit/DB",
            "sales/team",
            "FN/Calc",
            CodeObjectType.Function);

        Assert.Equal(
            "/tmp/output/External/Audit_DB/Functions/sales_team.FN_Calc/docs/Spec.md",
            paths.ResolveSpecPath(key));
    }

    [Fact]
    public void ResolveSpecPath_DoesNotAllowParentDirectorySegments()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var key = CodeObjectKey.Create(
            "..",
            "dbo",
            "usp_External",
            CodeObjectType.Procedure);

        Assert.Equal(
            "/tmp/output/External/__/Procedures/dbo.usp_External/docs/Spec.md",
            paths.ResolveSpecPath(key));
    }

    [Fact]
    public void Constructor_RejectsMissingDatabaseOrOutputRoot()
    {
        Assert.Throws<ArgumentException>(() => new OutputPathResolver(" ", "/tmp/output"));
        Assert.Throws<ArgumentException>(() => new OutputPathResolver("PaymentDB", " "));
    }
}
