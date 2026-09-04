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
            "/tmp/output/External/AuditDB/Objects/calc.FN_Fee.Function/raw/object_definition.sql",
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
            "/tmp/output/External/Audit%2FDB/Functions/sales%2Fteam.FN%2FCalc/docs/Spec.md",
            paths.ResolveSpecPath(key));
    }

    [Fact]
    public void ResolveSpecPath_DoesNotCollideAfterEscapingIdentifierSegments()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var slashName = CodeObjectKey.Create(
            "PaymentDB",
            "dbo",
            "A/B",
            CodeObjectType.Procedure);
        var underscoreName = CodeObjectKey.Create(
            "PaymentDB",
            "dbo",
            "A_B",
            CodeObjectType.Procedure);
        var dottedSchema = CodeObjectKey.Create(
            "PaymentDB",
            "a.b",
            "c",
            CodeObjectType.Procedure);
        var dottedName = CodeObjectKey.Create(
            "PaymentDB",
            "a",
            "b.c",
            CodeObjectType.Procedure);

        Assert.NotEqual(paths.ResolveSpecPath(slashName), paths.ResolveSpecPath(underscoreName));
        Assert.NotEqual(paths.ResolveSpecPath(dottedSchema), paths.ResolveSpecPath(dottedName));
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
            "/tmp/output/External/%2E%2E/Procedures/dbo.usp_External/docs/Spec.md",
            paths.ResolveSpecPath(key));
    }

    /// <summary>
    /// 이름에 예약문자가 있으면 손조립 경로($"{schema}.{name}")는 명세서·캐시 조회
    /// 경로와 갈라진다. 해석기는 %XX로 인코딩하므로 두 자리가 같은 폴더를 가리킨다.
    /// Program.SaveMigrationPlanAsync와 RenderAnalysisResultPanel이 이 계약에 기댄다.
    /// 문자는 플랫폼을 타지 않는 것으로 고른다 - ':'는 Windows에서만 금지문자라
    /// macOS·Linux에서 인코딩되지 않지만, '.'과 '/'는 EncodePathSegment가
    /// 어느 플랫폼에서나 조건 없이 인코딩한다.
    /// </summary>
    [Fact]
    public void ResolveDocsDirectory_EncodesReservedCharactersInObjectName()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var key = CodeObjectKey.Create("PaymentDB", "dbo", "USP.Odd/Name", CodeObjectType.Procedure);

        var directory = paths.ResolveDocsDirectory(key);

        Assert.Equal("/tmp/output/Procedures/dbo.USP%2EOdd%2FName/docs", directory);
        Assert.NotEqual(
            Path.Combine("/tmp/output", "Procedures", $"{key.Schema}.{key.Name}", "docs"),
            directory);
    }

    /// <summary>
    /// 배치 전환 계획서(BatchMigrationPlan.md)는 명세서 옆에 놓인다는 것이
    /// Program.SaveMigrationPlanAsync가 기대는 계약이다. 이 둘이 갈라지면
    /// 계획서만 다른 폴더에 남아 아무도 찾지 못한다.
    /// </summary>
    [Fact]
    public void ResolveDocsDirectory_IsTheDirectoryThatHoldsTheSpec()
    {
        var paths = new OutputPathResolver("PaymentDB", "/tmp/output");
        var key = CodeObjectKey.Create("PaymentDB", "dbo", "usp_Settle", CodeObjectType.Procedure);

        Assert.Equal(
            Path.GetDirectoryName(paths.ResolveSpecPath(key)),
            paths.ResolveDocsDirectory(key));
    }

    [Fact]
    public void Constructor_RejectsMissingDatabaseOrOutputRoot()
    {
        Assert.Throws<ArgumentException>(() => new OutputPathResolver(" ", "/tmp/output"));
        Assert.Throws<ArgumentException>(() => new OutputPathResolver("PaymentDB", " "));
    }
}
