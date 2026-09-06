using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementProcessRosterDraftTests
    {
        private static PolicySource Source(
            string label, string ddl, IEnumerable<string>? writes = null, IEnumerable<string>? reads = null) =>
            new(label, "## 개요\n\n본문\n", ddl,
                new SpStaticAnalysisResult
                {
                    InsertTables = new List<string>(writes ?? Array.Empty<string>()),
                    SelectTables = new List<string>(reads ?? Array.Empty<string>()),
                },
                Array.Empty<DependencyInfo>());

        [Fact]
        public void 산출을_남들이_읽는_SP를_맨_앞_단계에_놓는다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.RATE_INS", "CREATE PROCEDURE dbo.RATE_INS AS BEGIN SELECT 1 END",
                       writes: new[] { "dbo.TClientSettleRate" }),
                Source("dbo.SETTLE_INS", "CREATE PROCEDURE dbo.SETTLE_INS AS BEGIN SELECT 1 END",
                       reads: new[] { "dbo.TClientSettleRate" }),
            });

            var ratePos = draft.IndexOf("dbo.RATE_INS", StringComparison.Ordinal);
            var settlePos = draft.IndexOf("dbo.SETTLE_INS", StringComparison.Ordinal);

            Assert.True(ratePos >= 0 && settlePos >= 0);
            Assert.True(ratePos < settlePos, "산출을 남이 읽는 SP가 앞 단계에 놓여야 한다");
        }

        [Fact]
        public void 순서를_모르는_SP들은_자리표시자_단계에_모은다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.A", "CREATE PROCEDURE dbo.A AS BEGIN SELECT 1 END"),
                Source("dbo.B", "CREATE PROCEDURE dbo.B AS BEGIN SELECT 1 END"),
            });

            Assert.Contains(SettlementProcessRoster.PlaceholderMarker, draft);
        }

        [Fact]
        public void EXEC로_부르는_관계를_한_단계로_묶는다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.SUMMARY",
                    "CREATE PROCEDURE dbo.SUMMARY AS BEGIN EXEC dbo.SUMMARY_EXTRA END"),
                Source("dbo.SUMMARY_EXTRA",
                    "CREATE PROCEDURE dbo.SUMMARY_EXTRA AS BEGIN SELECT 1 END"),
            });

            var roster = SettlementProcessRosterParser.Parse(draft);
            var stage = Assert.Single(roster.Stages, s => s.Procedures.Contains("dbo.SUMMARY"));
            Assert.Contains("dbo.SUMMARY_EXTRA", stage.Procedures);
        }

        [Fact]
        public void 만든_초안은_다시_파싱된다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.A", "CREATE PROCEDURE dbo.A AS BEGIN SELECT 1 END"),
            });

            var roster = SettlementProcessRosterParser.Parse(draft);

            Assert.Contains(roster.AllStagedProcedures(), p => p == "dbo.A");
        }

        [Fact]
        public void 제외_섹션을_빈_채로_함께_낸다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.A", "CREATE PROCEDURE dbo.A AS BEGIN SELECT 1 END"),
            });

            Assert.Contains(SettlementProcessRoster.ExcludedHeading, draft);
            Assert.Empty(SettlementProcessRosterParser.Parse(draft).Excluded);
        }

        [Fact]
        public void 호출_무리가_둘_이상이면_단계_제목이_전부_다르다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.SUMMARY",
                    "CREATE PROCEDURE dbo.SUMMARY AS BEGIN EXEC dbo.SUMMARY_EXTRA END"),
                Source("dbo.SUMMARY_EXTRA",
                    "CREATE PROCEDURE dbo.SUMMARY_EXTRA AS BEGIN SELECT 1 END"),
                Source("dbo.OTHER",
                    "CREATE PROCEDURE dbo.OTHER AS BEGIN EXEC dbo.OTHER_EXTRA END"),
                Source("dbo.OTHER_EXTRA",
                    "CREATE PROCEDURE dbo.OTHER_EXTRA AS BEGIN SELECT 1 END"),
            });

            var roster = SettlementProcessRosterParser.Parse(draft);
            var callGroupTitles = roster.Stages
                .Select(s => s.Title)
                .Where(t => t.Contains("호출 무리", StringComparison.Ordinal))
                .ToList();

            Assert.True(callGroupTitles.Count >= 2, "호출 무리 단계가 둘 이상 나와야 이 회귀를 잡는다");
            Assert.Equal(callGroupTitles.Count, callGroupTitles.Distinct(StringComparer.Ordinal).Count());
        }
    }
}
