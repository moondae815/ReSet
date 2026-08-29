using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class StepInterfaceFactsTests
{
    private static BatchStepPlan Step(string code, params string[] legacy) => new(
        Code: code,
        Name: $"{code} 단계",
        LegacyProcedures: legacy,
        TargetTables: Array.Empty<string>(),
        ErrorCodes: Array.Empty<string>(),
        Chunkable: false,
        SchemaTables: Array.Empty<string>());

    private static SpDefinition Definition(string schema, string name, params string[] parameters)
    {
        var def = new SpDefinition { Schema = schema, Name = name };
        def.StaticAnalysis = new SpStaticAnalysisResult();
        def.StaticAnalysis.ProcedureParameters.AddRange(parameters);
        return def;
    }

    private static IReadOnlyList<StepInterface> BuildFrom(
        IReadOnlyList<BatchStepPlan> steps, params SpDefinition[] defs) =>
        StepInterfaceFacts.Build(steps, StepInterfaceFacts.CollectParameters(defs));

    [Fact]
    public void CollectParameters_KeysByBothTheBareAndTheQualifiedName()
    {
        var map = StepInterfaceFacts.CollectParameters(
            new[] { Definition("dbo", "UP_UTIL_SETTLE_INS", "@pi_strYMD varchar(8)") });

        Assert.True(map.ContainsKey("UP_UTIL_SETTLE_INS"));
        Assert.True(map.ContainsKey("dbo.UP_UTIL_SETTLE_INS"));
    }

    // 정적 분석이 파라미터를 내지 않았으면 재료가 없는 것이다. 빈 목록을
    // 사실로 내보내면 검사가 그 단계의 모든 파라미터를 결함으로 든다.
    [Fact]
    public void CollectParameters_OmitsAProcedureThatDeclaredNoParameters()
    {
        var map = StepInterfaceFacts.CollectParameters(
            new[] { new SpDefinition { Schema = "dbo", Name = "UP_UTIL_SETTLE_INS" } });

        Assert.Empty(map);
    }

    [Fact]
    public void Build_MapsEachStepToItsLegacyProcedureParameters()
    {
        var iface = Assert.Single(BuildFrom(
            new[] { Step("S05", "dbo.UP_UTIL_SETTLE_INS") },
            Definition("dbo", "UP_UTIL_SETTLE_INS", "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT")));

        Assert.Equal("S05", iface.StepCode);
        Assert.Equal(new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" }, iface.Parameters);
    }

    // 신설 단계는 원본이 없다. 행을 만들면 "파라미터 0개"가 사실처럼 보인다.
    [Fact]
    public void Build_SkipsStepsThatHaveNoLegacyProcedure()
    {
        var built = BuildFrom(
            new[] { Step("S01"), Step("S05", "dbo.UP_UTIL_SETTLE_INS") },
            Definition("dbo", "UP_UTIL_SETTLE_INS", "@pi_strYMD varchar(8)"));

        Assert.Single(built, i => i.StepCode == "S05");
        Assert.DoesNotContain(built, i => i.StepCode == "S01");
    }

    [Fact]
    public void Build_MatchesTheProcedureNameCaseInsensitivelyAndBare()
    {
        Assert.Single(BuildFrom(
            new[] { Step("S05", "UP_util_settle_ins") },
            Definition("dbo", "UP_UTIL_SETTLE_INS", "@pi_strYMD varchar(8)")));
    }

    [Fact]
    public void Build_MergesParametersWhenAStepConsumesTwoProcedures()
    {
        var iface = Assert.Single(BuildFrom(
            new[] { Step("S12", "dbo.UP_Util_Settle_Summary", "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA") },
            Definition("dbo", "UP_Util_Settle_Summary", "@pi_strYMD varchar(8)"),
            Definition("dbo", "UP_UTIL_SETTLE_SUMMARY_EXTRA", "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT")));

        // 같은 파라미터는 한 번만. 두 SP가 @pi_strYMD를 공유한다.
        Assert.Equal(new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" }, iface.Parameters);
    }

    [Fact]
    public void Build_ReturnsNothingWhenThereIsNoMaterial()
    {
        Assert.Empty(StepInterfaceFacts.Build(new[] { Step("S05", "dbo.X") }, null));
    }

    [Fact]
    public void ParameterNames_StripsTheTypeAndKeepsTheAtSign()
    {
        var iface = new StepInterface("S05", new[] { "dbo.X" },
            new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" });

        Assert.Equal(new[] { "@pi_strYMD", "@po_intRetVal" }, StepInterfaceFacts.ParameterNames(iface));
    }

    [Fact]
    public void RenderPromptTable_ListsEveryStepAndItsParameters()
    {
        var table = StepInterfaceFacts.RenderPromptTable(new[]
        {
            new StepInterface("S05", new[] { "dbo.UP_UTIL_SETTLE_INS" },
                new[] { "@pi_strYMD varchar(8)", "@po_intRetVal int OUTPUT" })
        });

        Assert.Contains("S05", table);
        Assert.Contains("@pi_strYMD varchar(8)", table);
        Assert.Contains("@po_intRetVal int OUTPUT", table);
    }

    // 캐시 불변성(설계 §4). 어느 단계를 만들든 같은 표가 실려야 한다.
    [Fact]
    public void RenderPromptTable_IsIndependentOfWhichStepIsBeingGenerated()
    {
        var interfaces = new[]
        {
            new StepInterface("S05", new[] { "dbo.A" }, new[] { "@pi_strYMD varchar(8)" }),
            new StepInterface("S08", new[] { "dbo.B" }, new[] { "@pi_strYMD varchar(8)" })
        };

        Assert.Equal(
            StepInterfaceFacts.RenderPromptTable(interfaces),
            StepInterfaceFacts.RenderPromptTable(interfaces));
    }

    // 스키마가 다른 동명 프로시저가 있으면 맨이름이 어느 쪽을 가리키는지
    // 더 이상 확정할 수 없다. 임의로 하나를 골라 덮어쓰면 틀린 사실을 낸다 -
    // 한정명 키 둘은 남기고 맨이름 키는 뺀다.
    [Fact]
    public void CollectParameters_TreatsBareNameAsAmbiguousWhenTwoSchemasShareIt()
    {
        var map = StepInterfaceFacts.CollectParameters(new[]
        {
            Definition("dbo", "UP_FOO", "@pi_strYMD varchar(8)"),
            Definition("archive", "UP_FOO", "@pi_strYMD varchar(8)")
        });

        Assert.True(map.ContainsKey("dbo.UP_FOO"));
        Assert.True(map.ContainsKey("archive.UP_FOO"));
        Assert.False(map.ContainsKey("UP_FOO"));
    }

    // 모호한 맨이름으로는 매칭이 안 된다. 틀린 파라미터를 붙이느니
    // 그 단계를 소프트 스킵한다(계획서 §Global Constraints와 같은 판단).
    [Fact]
    public void Build_SkipsAStepWhoseBareLegacyProcedureNameIsAmbiguous()
    {
        var built = BuildFrom(
            new[] { Step("S05", "UP_FOO") },
            Definition("dbo", "UP_FOO", "@pi_strYMD varchar(8)"),
            Definition("archive", "UP_FOO", "@pi_strYMD varchar(8)"));

        Assert.Empty(built);
    }

    // 원본 프로시저가 파라미터를 하나도 선언하지 않으면 CollectParameters가
    // 재료를 내지 않는다. Build는 그 단계에 대해서도 사실을 내면 안 된다.
    [Fact]
    public void Build_SkipsAStepWhoseOnlyLegacyProcedureDeclaredNoParameters()
    {
        var built = BuildFrom(
            new[] { Step("S05", "dbo.UP_UTIL_SETTLE_INS") },
            new SpDefinition { Schema = "dbo", Name = "UP_UTIL_SETTLE_INS" });

        Assert.Empty(built);
    }

    // ── 스키마 카탈로그 조립 ────────────────────────────────────────────────
    //
    // 미지 테이블 검사가 이 목록 하나로 "실재하는가"를 판정한다. 실측
    // (2026-08-29 ① 전수 분류): 계획서 20편·359단계의 발화 219건 중 29건이
    // 「원본 SP 자신이 카탈로그에 없다」 하나였고, 이 재료를 더해 190이 됐다.

    [Fact]
    public void CollectSchemaCatalog_IncludesTheAnalyzedProcedureItself()
    {
        var catalog = StepInterfaceFacts.CollectSchemaCatalog(new[]
        {
            new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_UTIL_SETTLE_INS",
                Dependencies = { new DependencyInfo { Schema = "dbo", Name = "TSettleMst" } },
            },
        });

        Assert.Contains("dbo.UP_UTIL_SETTLE_INS", catalog);
        Assert.Contains("dbo.TSettleMst", catalog);
    }

    [Fact]
    public void CollectSchemaCatalog_KeepsTheDatabaseQualifierOnDependencies()
    {
        var catalog = StepInterfaceFacts.CollectSchemaCatalog(new[]
        {
            new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_UTIL_SETTLE_INS",
                Dependencies = { new DependencyInfo { Database = "PaymentDB", Schema = "dbo", Name = "TTxMst" } },
            },
        });

        Assert.Contains("PaymentDB.dbo.TTxMst", catalog);
    }

    [Fact]
    public void CollectSchemaCatalog_DoesNotRepeatANameThatIsAlsoADependency()
    {
        // 한 SP가 다른 SP를 부르면 그 이름이 의존 대상이자 로스터 항목이 된다.
        var catalog = StepInterfaceFacts.CollectSchemaCatalog(new[]
        {
            new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_Util_Settle_Summary",
                Dependencies = { new DependencyInfo { Schema = "dbo", Name = "UP_UTIL_SETTLE_SUMMARY_ETC" } },
            },
            new SpDefinition { Schema = "dbo", Name = "UP_UTIL_SETTLE_SUMMARY_ETC" },
        });

        Assert.Single(catalog, name =>
            string.Equals(name, "dbo.UP_UTIL_SETTLE_SUMMARY_ETC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CollectSchemaCatalog_WithNoDefinitions_StaysEmptySoTheCheckSoftSkips()
    {
        // 비어 있으면 CheckUnknownTableReferences가 검사 자체를 건너뛴다. 카탈로그가
        // 없다는 사실을 "모든 테이블이 유령이다"로 바꾸지 않기 위한 계약이다.
        Assert.Empty(StepInterfaceFacts.CollectSchemaCatalog(null));
        Assert.Empty(StepInterfaceFacts.CollectSchemaCatalog(Array.Empty<SpDefinition>()));
    }
}
