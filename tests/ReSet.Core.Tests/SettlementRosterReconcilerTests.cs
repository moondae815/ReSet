using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementRosterReconcilerTests
    {
        private static readonly string[] Discovered =
        {
            "dbo.UP_UTIL_SETTLE_INS",
            "dbo.UP_UTIL_SETTLE_INS_EXTRA",
            "dbo.UP_UTIL_STAT_PGCOLLECT_INS",
        };

        private static SettlementProcessRoster Roster(
            IReadOnlyList<string> staged, IReadOnlyList<string>? excluded = null, string title = "1. 정산 원장 적재") =>
            new(new[] { new PolicyStage(title, staged) }, excluded ?? Array.Empty<string>());

        [Fact]
        public void 전량이_한_번씩_실려_있으면_결함이_없다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0], Discovered[1] }, new[] { Discovered[2] }), Discovered);

            Assert.Empty(defects);
        }

        [Fact]
        public void 명부에서_빠진_SP를_고발한다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0] }, new[] { Discovered[2] }), Discovered);

            var defect = Assert.Single(defects);
            Assert.Equal(RosterDefectType.ProcedureMissing, defect.Type);
            Assert.Equal("dbo.UP_UTIL_SETTLE_INS_EXTRA", defect.Subject);
        }

        [Fact]
        public void 두_단계에_중복으로_실린_SP를_고발한다()
        {
            var roster = new SettlementProcessRoster(
                new[]
                {
                    new PolicyStage("1. 가", new[] { Discovered[0] }),
                    new PolicyStage("2. 나", new[] { Discovered[0], Discovered[1] }),
                },
                new[] { Discovered[2] });

            var defects = SettlementRosterReconciler.Reconcile(roster, Discovered);

            var defect = Assert.Single(defects);
            Assert.Equal(RosterDefectType.ProcedureDuplicated, defect.Type);
            Assert.Equal(Discovered[0], defect.Subject);
        }

        [Fact]
        public void 명세서가_없는_이름을_고발한다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0], Discovered[1], "dbo.UP_오타" }, new[] { Discovered[2] }),
                Discovered);

            var defect = Assert.Single(defects);
            Assert.Equal(RosterDefectType.ProcedureUnknown, defect.Type);
            Assert.Equal("dbo.UP_오타", defect.Subject);
        }

        [Fact]
        public void 초안_자리표시자가_남아_있으면_고발한다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0], Discovered[1] }, new[] { Discovered[2] },
                       title: "2. " + SettlementProcessRoster.PlaceholderMarker + " ①"),
                Discovered);

            Assert.Contains(defects, d => d.Type == RosterDefectType.PlaceholderTitleRemaining);
        }

        [Fact]
        public void 단계가_하나도_없으면_고발한다()
        {
            var defects = SettlementRosterReconciler.Reconcile(SettlementProcessRoster.Empty, Discovered);

            Assert.Contains(defects, d => d.Type == RosterDefectType.NoStages);
        }

        [Fact]
        public void 제외에_적힌_SP는_누락이_아니다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0], Discovered[1] }, new[] { Discovered[2] }), Discovered);

            Assert.DoesNotContain(defects, d => d.Type == RosterDefectType.ProcedureMissing);
        }

        // --- 추가 요구사항: StageTitleDuplicated ---
        // MarkdownSectionLocator.LocateSection(exact: true)는 `line.Trim() == headingLine`로
        // 대소문자를 구분하는(ordinal) 완전 일치를 쓴다. 같은 제목의 단계가 둘이면 두 순회가
        // 같은 절을 가리켜 두 번째 단계의 표가 통째로 증발한다. 이 검사는 그 판정 기준을
        // 그대로 따른다 - 대소문자만 다른 제목은 파서 입장에서 서로 다른 헤딩이므로
        // 여기서도 다른 제목으로 본다(대소문자 구분).

        [Fact]
        public void 같은_제목의_단계_둘이면_고발한다()
        {
            var roster = new SettlementProcessRoster(
                new[]
                {
                    new PolicyStage("1. 정산 원장 적재", new[] { Discovered[0] }),
                    new PolicyStage("1. 정산 원장 적재", new[] { Discovered[1] }),
                },
                new[] { Discovered[2] });

            var defects = SettlementRosterReconciler.Reconcile(roster, Discovered);

            var defect = Assert.Single(defects, d => d.Type == RosterDefectType.StageTitleDuplicated);
            Assert.Equal("1. 정산 원장 적재", defect.Subject);
        }

        [Fact]
        public void 제목이_전부_다르면_StageTitleDuplicated를_발화하지_않는다()
        {
            var roster = new SettlementProcessRoster(
                new[]
                {
                    new PolicyStage("1. 정산 원장 적재", new[] { Discovered[0] }),
                    new PolicyStage("2. 통계 집계", new[] { Discovered[1] }),
                },
                new[] { Discovered[2] });

            var defects = SettlementRosterReconciler.Reconcile(roster, Discovered);

            Assert.DoesNotContain(defects, d => d.Type == RosterDefectType.StageTitleDuplicated);
        }

        [Fact]
        public void 대소문자만_다른_제목은_중복으로_보지_않는다()
        {
            // LocateSection(exact: true)의 `line.Trim() == headingLine`는 대소문자를 구분한다 -
            // "Stage A"와 "STAGE A"는 서로 다른 헤딩 줄이라 파서가 서로 다른 절을 찾는다.
            // 그러므로 이 검사도 대소문자만 다른 제목을 다른 제목으로 취급해야 파서와
            // 같은 말을 한다.
            var roster = new SettlementProcessRoster(
                new[]
                {
                    new PolicyStage("1. Stage A", new[] { Discovered[0] }),
                    new PolicyStage("1. STAGE A", new[] { Discovered[1] }),
                },
                new[] { Discovered[2] });

            var defects = SettlementRosterReconciler.Reconcile(roster, Discovered);

            Assert.DoesNotContain(defects, d => d.Type == RosterDefectType.StageTitleDuplicated);
        }
    }
}
