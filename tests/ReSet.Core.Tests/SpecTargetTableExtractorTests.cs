using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecTargetTableExtractorTests
    {
        // 픽스처는 실측 SP(dbo.UP_Util_PG_Client_CMRate_Ins)의 정적 분석을 그대로 옮긴 것이다.
        // 두 제공자 회차가 모두 이 단계의 TargetTables를 빈 배열로 냈고, 정적 분석에는
        // 대상이 다 들어 있었다 - 이 작업이 존재하는 이유다.
        private static SpDefinition RateSnapshotSp() => new()
        {
            Schema = "dbo",
            Name = "UP_Util_PG_Client_CMRate_Ins",
            StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                InsertTables =
                {
                    "SETTLE_POQ_DB.dbo.TPGSettleRate",
                    "SETTLE_POQ_DB.dbo.TClientSettleRate",
                },
                DeleteTables =
                {
                    "SETTLE_POQ_DB.dbo.TPGSettleRate",
                },
                SelectTables =
                {
                    "SETTLE_POQ_DB.dbo.TSettleMst",
                    "SETTLE_POQ_DB.dbo.TClient",
                },
            },
        };

        [Fact]
        public void Extract_ShouldSplitWriteTargetsFromReadSources()
        {
            var result = SpecTargetTableExtractor.Extract(new[] { RateSnapshotSp() });

            var sets = result["up_util_pg_client_cmrate_ins"];
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TPGSettleRate", "SETTLE_POQ_DB.dbo.TClientSettleRate" },
                sets.WriteTables);
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst", "SETTLE_POQ_DB.dbo.TClient" },
                sets.ReadTables);
        }

        [Fact]
        public void Extract_ShouldUseTheSameKeyRuleAsTheReturnCodeExtractor()
        {
            // 두 추출기가 다른 키 규칙을 쓰면 목차의 LegacyProcedures가 한쪽에만 매칭된다.
            var result = SpecTargetTableExtractor.Extract(new[] { RateSnapshotSp() });

            Assert.True(result.ContainsKey(
                SpecReturnCodeExtractor.BareName("dbo.UP_Util_PG_Client_CMRate_Ins")));
        }

        [Fact]
        public void Extract_ShouldExcludeTempTablesAndTableVariables()
        {
            // 임시 테이블과 테이블 변수는 물리 테이블이 아니라 DDL도 없다. 검증에 걸면
            // 존재하지 않는 요건을 만들고, 그것은 재생성으로 고칠 수 없다.
            var sp = new SpDefinition
            {
                Name = "UP_X",
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    InsertTables = { "#TMP", "##Global", "SETTLE_POQ_DB.dbo.TReal" },
                    SelectTables = { "@Buffer", "SETTLE_POQ_DB.dbo.TSource" },
                },
            };

            var sets = SpecTargetTableExtractor.Extract(new[] { sp })["up_x"];

            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TReal" }, sets.WriteTables);
            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TSource" }, sets.ReadTables);
        }

        [Fact]
        public void Extract_ShouldNotCreateAKeyWhenNothingWasFound()
        {
            // 빈 목록과 "그런 프로시저 없음"이 같아지면 보강기가 둘을 구별할 수 없다.
            var sp = new SpDefinition { Name = "UP_Empty", StaticAnalysis = new SpStaticAnalysisResult() };

            Assert.Empty(SpecTargetTableExtractor.Extract(new[] { sp }));
        }

        [Fact]
        public void Extract_ShouldSurviveANullStaticAnalysis()
        {
            var sp = new SpDefinition { Name = "UP_Null", StaticAnalysis = null! };

            Assert.Empty(SpecTargetTableExtractor.Extract(new[] { sp }));
        }

        [Fact]
        public void Extract_ShouldMergeTwoDefinitionsThatShareABareName()
        {
            var first = new SpDefinition
            {
                Name = "dbo.UP_Dup",
                StaticAnalysis = new SpStaticAnalysisResult { InsertTables = { "DB.dbo.TA" } },
            };
            var second = new SpDefinition
            {
                Name = "other.UP_Dup",
                StaticAnalysis = new SpStaticAnalysisResult { InsertTables = { "DB.dbo.TB" } },
            };

            var sets = SpecTargetTableExtractor.Extract(new[] { first, second })["up_dup"];

            Assert.Equal(new[] { "DB.dbo.TA", "DB.dbo.TB" }, sets.WriteTables);
        }

        [Fact]
        public void BareTableName_ShouldStripQualifiersAndBrackets()
        {
            Assert.Equal("tsettlemst", SpecTargetTableExtractor.BareTableName("SETTLE_POQ_DB.dbo.TSettleMst"));
            Assert.Equal("tsettlemst", SpecTargetTableExtractor.BareTableName("[dbo].[TSettleMst]"));
            Assert.Equal("tsettlemst", SpecTargetTableExtractor.BareTableName("TSettleMst"));
        }
    }
}
