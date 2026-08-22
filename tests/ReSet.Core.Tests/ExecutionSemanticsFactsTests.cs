using System;
using System.Collections.Generic;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class ExecutionSemanticsFactsTests
    {
        private static readonly Dictionary<string, string> NoColumns = new();

        [Fact]
        public void Collect_NoThreePartAndNoLinkedServer_ShouldStateLocalPlacementAsFact()
        {
            // F1 무리 실측: 파서가 ThreePartObjectReferences를 빈 배열로 확정했는데
            // 명세서 9곳이 "크로스 데이터베이스 참조라고 단언할 수 없습니다"로 되짚었다.
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedTables = new List<string> { "SETTLE_POQ_DB.dbo.TPGProperty" }
            };

            var facts = ExecutionSemanticsFacts.Collect(
                "SELECT 1;",
                analysis,
                CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
                NoColumns);

            var fact = Assert.Single(facts, f => f.Kind == ExecutionSemanticsFacts.DatabasePlacementKind);
            Assert.Contains("SETTLE_POQ_DB", fact.Fact);
            Assert.Contains("3부 식별자 참조 0건", fact.Fact);
            Assert.Contains("연결 서버 참조 0건", fact.Fact);
        }

        [Fact]
        public void Collect_WithThreePartReference_ShouldNameTheCrossDatabaseTargets()
        {
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ThreePartObjectReferences = new List<string> { "PaymentDB.dbo.TExtraSettleIn" }
            };

            var facts = ExecutionSemanticsFacts.Collect(
                "SELECT 1;",
                analysis,
                CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure),
                NoColumns);

            var fact = Assert.Single(facts, f => f.Kind == ExecutionSemanticsFacts.DatabasePlacementKind);
            Assert.Contains("PaymentDB.dbo.TExtraSettleIn", fact.Fact);
        }

        [Fact]
        public void Collect_WithoutAnalysis_ShouldReturnEmpty()
        {
            var facts = ExecutionSemanticsFacts.Collect("SELECT 1;", null, null, NoColumns);

            Assert.Empty(facts);
        }

        /// <summary>
        /// 2026-08-22 축 A 재감사 실측(EXPECT_PROC·INS_EXTRA4PLCARD·AcqManual·COLLECTYMD).
        /// 3부 식별자 참조에는 소속 DB를 3부로 적은 것도 섞인다. 전부 "그 밖"으로
        /// 문장화하면 명세서가 그 확정 문장을 그대로 베껴 홈 DB 참조가 크로스 DB로
        /// 읽힌다 - 이 표는 "수정 금지"라 산문이 바로잡을 수도 없다.
        /// </summary>
        [Fact]
        public void DatabasePlacement_ThreePartReferencesInsideHomeDatabase_AreNotCalledOutside()
        {
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ThreePartObjectReferences =
                {
                    "SETTLE_POQ_DB.dbo.TSettleMst",
                    "SETTLE_CARD_DB.dbo.TCardMst"
                }
            };
            var objectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure);

            var fact = DatabasePlacementExtractor.Extract(analysis, objectKey);

            Assert.NotNull(fact);
            // 밖인 것만 "그 밖" 목록에 있어야 한다.
            var outsideSegment = fact!.Sentence[fact.Sentence.IndexOf("그 밖", StringComparison.Ordinal)..];
            Assert.Contains("SETTLE_CARD_DB.dbo.TCardMst", outsideSegment);
            Assert.DoesNotContain("SETTLE_POQ_DB.dbo.TSettleMst", outsideSegment);
        }

        /// <summary>
        /// 3부 참조가 전부 소속 DB 안이면 "그 밖"이라는 분류어 자체가 나오면 안 된다.
        /// </summary>
        [Fact]
        public void DatabasePlacement_AllThreePartReferencesInsideHome_SaysNoneOutside()
        {
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ThreePartObjectReferences = { "SETTLE_POQ_DB.dbo.TSettleMst" }
            };
            var objectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure);

            var fact = DatabasePlacementExtractor.Extract(analysis, objectKey);

            Assert.NotNull(fact);
            Assert.DoesNotContain("그 밖", fact!.Sentence);
            Assert.Contains("SETTLE_POQ_DB.dbo.TSettleMst", fact.Sentence);
        }

        /// <summary>
        /// 소속 DB 이름을 모르는 갈래는 이미 옳다(분류어 없이 건수·목록만). 회귀 고정.
        /// </summary>
        [Fact]
        public void DatabasePlacement_HomeDatabaseUnknown_KeepsUnclassifiedSentence()
        {
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ThreePartObjectReferences = { "SETTLE_CARD_DB.dbo.TCardMst" }
            };

            var fact = DatabasePlacementExtractor.Extract(analysis, objectKey: null);

            Assert.NotNull(fact);
            Assert.Contains("소속 DB 이름은 미상입니다", fact!.Sentence);
            Assert.DoesNotContain("그 밖", fact.Sentence);
        }

        [Fact]
        public void Collect_WithAggregateAssignment_ShouldEmitAnAggregateRow()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v VARCHAR(8) = ''
    SELECT @v = MIN(ReqYMD) FROM dbo.T
END";

            var facts = ExecutionSemanticsFacts.Collect(
                ddl, new SpStaticAnalysisResult { IsParsedSuccessfully = true }, null, NoColumns);

            Assert.Contains(facts, f => f.Kind == ExecutionSemanticsFacts.AggregateAssignmentKind);
        }
    }
}
