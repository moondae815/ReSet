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

        [Fact]
        public void 선행_적재이면서_EXEC_호출자인_SP는_한_번만_나온다()
        {
            // dbo.RATE_INS는 dbo.SETTLE_INS가 읽는 테이블을 쓰므로 "선행 적재" 단계
            // 후보이면서, 동시에 dbo.HELPER를 EXEC하므로 "호출 무리" 후보이기도 하다.
            // 리뷰어가 재현한 중복 등재 결함의 최소 재현 입력이다.
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.RATE_INS", "CREATE PROCEDURE dbo.RATE_INS AS BEGIN EXEC dbo.HELPER END",
                       writes: new[] { "dbo.TClientSettleRate" }),
                Source("dbo.HELPER", "CREATE PROCEDURE dbo.HELPER AS BEGIN SELECT 1 END"),
                Source("dbo.SETTLE_INS", "CREATE PROCEDURE dbo.SETTLE_INS AS BEGIN SELECT 1 END",
                       reads: new[] { "dbo.TClientSettleRate" }),
            });

            var roster = SettlementProcessRosterParser.Parse(draft);
            var all = roster.AllStagedProcedures().ToList();

            Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(1, all.Count(p => string.Equals(p, "dbo.RATE_INS", StringComparison.OrdinalIgnoreCase)));
        }

        [Fact]
        public void 빈_단계는_렌더링하지_않는다()
        {
            // 모든 SP가 선행 적재 하나로만 몰리면 호출 무리·순서 미상 단계는
            // 만들어 낼 내용이 없다 - 그 헤딩 자체를 찍지 않는다.
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.RATE_INS", "CREATE PROCEDURE dbo.RATE_INS AS BEGIN SELECT 1 END",
                       writes: new[] { "dbo.TClientSettleRate" }),
                Source("dbo.SETTLE_INS", "CREATE PROCEDURE dbo.SETTLE_INS AS BEGIN SELECT 1 END",
                       reads: new[] { "dbo.TClientSettleRate" }),
            });

            Assert.DoesNotContain("호출 무리", draft);
        }

        [Fact]
        public void 허브_테이블만_공유하는_SP들은_선행_적재가_아니라_순서_미상에_간다()
        {
            // dbo.Hub를 3개 이상의 SP가 쓰면 허브로 보고 순서 판정에서 뺀다. 허브
            // 제외가 없다면 dbo.W1~W3 모두 dbo.R1이 읽는 테이블을 쓰므로 "선행
            // 적재"로 오판된다 - 이 테스트는 그 오판을 막는 안전장치를 잠근다.
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.W1", "CREATE PROCEDURE dbo.W1 AS BEGIN SELECT 1 END", writes: new[] { "dbo.Hub" }),
                Source("dbo.W2", "CREATE PROCEDURE dbo.W2 AS BEGIN SELECT 1 END", writes: new[] { "dbo.Hub" }),
                Source("dbo.W3", "CREATE PROCEDURE dbo.W3 AS BEGIN SELECT 1 END", writes: new[] { "dbo.Hub" }),
                Source("dbo.R1", "CREATE PROCEDURE dbo.R1 AS BEGIN SELECT 1 END", reads: new[] { "dbo.Hub" }),
            });

            var roster = SettlementProcessRosterParser.Parse(draft);
            var producerStage = roster.Stages.FirstOrDefault(s => s.Title.Contains("선행 적재", StringComparison.Ordinal));
            var unknownStage = roster.Stages.FirstOrDefault(s => s.Title.Contains("순서 미상", StringComparison.Ordinal));

            Assert.NotNull(unknownStage);
            Assert.Contains("dbo.W1", unknownStage!.Procedures);
            Assert.Contains("dbo.W2", unknownStage.Procedures);
            Assert.Contains("dbo.W3", unknownStage.Procedures);

            if (producerStage is not null)
            {
                Assert.DoesNotContain("dbo.W1", producerStage.Procedures);
                Assert.DoesNotContain("dbo.W2", producerStage.Procedures);
                Assert.DoesNotContain("dbo.W3", producerStage.Procedures);
            }
        }

        [Fact]
        public void 스키마_없이_EXEC해도_대상을_찾는다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.SUMMARY", "CREATE PROCEDURE dbo.SUMMARY AS BEGIN EXEC SUMMARY_EXTRA END"),
                Source("dbo.SUMMARY_EXTRA", "CREATE PROCEDURE dbo.SUMMARY_EXTRA AS BEGIN SELECT 1 END"),
            });

            var roster = SettlementProcessRosterParser.Parse(draft);
            var stage = Assert.Single(roster.Stages, s => s.Procedures.Contains("dbo.SUMMARY"));
            Assert.Contains("dbo.SUMMARY_EXTRA", stage.Procedures);
        }

        [Fact]
        public void EXEC_대상_이름이_접미사만_같으면_묶이지_않는다()
        {
            // 리뷰어가 재현한 입력: "EXEC Ins"가 이름이 "Ins"로 끝난다는 이유만으로
            // dbo.BulkIns를 끌어들이면 안 된다. 마지막 점 구획이 완전히 같아야 한다.
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.CALLER", "CREATE PROCEDURE dbo.CALLER AS BEGIN EXEC Ins END"),
                Source("dbo.BulkIns", "CREATE PROCEDURE dbo.BulkIns AS BEGIN SELECT 1 END"),
            });

            var roster = SettlementProcessRosterParser.Parse(draft);

            // 「같은 자리(순서 미상)에 우연히 함께 담긴다」와 「호출 관계로 한
            // 단계에 묶인다」는 다르다 - 여기서 막는 것은 후자다.
            Assert.DoesNotContain(roster.Stages,
                s => s.Title.Contains("호출 무리", StringComparison.Ordinal)
                     && s.Procedures.Contains("dbo.CALLER") && s.Procedures.Contains("dbo.BulkIns"));
        }
    }
}
