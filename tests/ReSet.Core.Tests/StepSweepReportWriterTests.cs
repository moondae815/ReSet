using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepSweepReportWriterTests
    {
        private static SweepReport Report(params SweepFinding[] findings) => new(
            findings,
            new SweepIndicators(3, 2, 1),
            new HarnessGaps(
                new List<string> { "POQSettleProc4" }, 51, 326, 18,
                StepInterfacesWereNull: true,
                RunRowOwnedTablesWereNull: true,
                KnownTableNamesWereEmpty: true));

        /// <summary>
        /// 세대 정보를 담은 HarnessGaps. 단계 번들과 명세서의 mtime 범위는
        /// SweepCommand가 실물에서 읽어 넣는다 - 여기서는 합성값이다.
        /// </summary>
        private static SweepReport ReportWithGenerations(
            DateTimeOffset? stepOldest, DateTimeOffset? stepNewest,
            DateTimeOffset? specOldest, DateTimeOffset? specNewest) => new(
            Array.Empty<SweepFinding>(),
            new SweepIndicators(3, 2, 1),
            new HarnessGaps(
                new List<string>(), 51, 326, 18,
                StepInterfacesWereNull: true,
                RunRowOwnedTablesWereNull: true,
                KnownTableNamesWereEmpty: true)
            {
                StepBundleOldest = stepOldest,
                StepBundleNewest = stepNewest,
                SpecOldest = specOldest,
                SpecNewest = specNewest,
            });

        // 가드가 침묵시킨 대가를 보고서가 드러내야 한다 - 조용한 결손은 이 저장소가
        // 반복해 겪은 실패 양식이다.
        [Fact]
        public void IndicatorsTableShowsStepsWithReusedCodeAnchors()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(3, 2, 1) { StepsWithReusedCodeAnchors = 81 },
                new HarnessGaps(
                    new List<string>(), 51, 326, 18,
                    StepInterfacesWereNull: true,
                    RunRowOwnedTablesWereNull: true,
                    KnownTableNamesWereEmpty: true));

            var section = Section(StepSweepReportWriter.Render(report, "abc1234", "16", 0), "## 캐시 17 선결 지표");
            Assert.Contains("| 코드 앵커가 둘 이상의 문장에 붙은 단계 수 | 81 |", section);
        }

        // 0이어도 행은 나와야 한다 - 0이라고 말하는 것과 아무 말도 안 하는 것은 다르다.
        [Fact]
        public void ReusedCodeAnchorRowPrintsEvenWhenZero()
        {
            var section = Section(
                StepSweepReportWriter.Render(Report(), "abc1234", "16", 0), "## 캐시 17 선결 지표");
            Assert.Contains("| 코드 앵커가 둘 이상의 문장에 붙은 단계 수 | 0 |", section);
        }

        private static DateTimeOffset D(string ymd) =>
            DateTimeOffset.Parse(ymd + "T00:00:00+09:00");

        // 보고서만 읽는 사람은 그 326쌍이 언제 만들어진 번들인지 알 수 없었다.
        // 축 B의 기준값이 명세서이므로 두 세대를 나란히 놓아야 대조가 유효한지 판단된다.
        [Fact]
        public void HeaderCarriesStepBundleAndSpecGenerations()
        {
            var markdown = StepSweepReportWriter.Render(
                ReportWithGenerations(D("2026-08-12"), D("2026-08-24"), D("2026-08-25"), D("2026-08-25")),
                "abc1234", "16", 0);
            var head = Section(markdown, "## 실행 조건");

            Assert.Contains("2026-08-12 ~ 2026-08-24", head);
            Assert.Contains("2026-08-25", head);
        }

        // 세대 차이가 있으면 이 스윕이 대조한 것이 이행 결함이 아니라 세대 차이일 수
        // 있다. 그 사실을 보고서가 스스로 말해야 한다.
        [Fact]
        public void StaleStepBundlesRaiseAGenerationWarning()
        {
            var markdown = StepSweepReportWriter.Render(
                ReportWithGenerations(D("2026-08-12"), D("2026-08-24"), D("2026-08-25"), D("2026-08-25")),
                "abc1234", "16", 0);

            Assert.Contains("단계 번들이 명세서보다 낡았다", Section(markdown, "## 실행 조건"));
        }

        // 매번 나오면 아무도 안 읽는다. 번들이 명세서만큼 새로우면 경고가 없어야 한다.
        [Fact]
        public void CurrentStepBundlesRaiseNoGenerationWarning()
        {
            var markdown = StepSweepReportWriter.Render(
                ReportWithGenerations(D("2026-08-25"), D("2026-08-26"), D("2026-08-25"), D("2026-08-25")),
                "abc1234", "16", 0);

            Assert.DoesNotContain("단계 번들이 명세서보다 낡았다", Section(markdown, "## 실행 조건"));
        }

        // 값이 없어도 행 자체는 나와야 한다 - 모른다는 사실이 사라지면 안 된다(§6).
        [Fact]
        public void UnknownGenerationsStillPrintTheirRows()
        {
            var markdown = StepSweepReportWriter.Render(
                ReportWithGenerations(null, null, null, null), "abc1234", "16", 0);
            var head = Section(markdown, "## 실행 조건");

            Assert.Contains("단계 번들 세대: 알 수 없음", head);
            Assert.Contains("명세서 세대: 알 수 없음", head);
        }

        /// <summary>
        /// `header`부터 다음 "## " 절 제목 직전까지만 잘라낸다. 여러 절이 같은 문자열
        /// 부분열을 낼 수 있어(예: Job별 표 행이 총계 표 행과 같은 문자열을 포함) 절을
        /// 가리지 않고 Assert.Contains(markdown 전체)만 쓰면 엉뚱한 절에서 우연히
        /// 일치해 회귀를 놓친다.
        /// </summary>
        private static string Section(string markdown, string header)
        {
            var start = markdown.IndexOf(header, StringComparison.Ordinal);
            Assert.True(start >= 0, $"section header not found: {header}");
            var contentStart = start + header.Length;
            var nextHeaderStart = markdown.IndexOf("\n## ", contentStart, StringComparison.Ordinal);
            return nextHeaderStart < 0
                ? markdown.Substring(contentStart)
                : markdown.Substring(contentStart, nextHeaderStart - contentStart);
        }

        // 결손을 안 실으면 줄어든 대상 범위가 개선처럼 보인다.
        [Fact]
        public void HeaderAlwaysCarriesHarnessGaps()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16", 0);

            Assert.Contains("abc1234", markdown);
            Assert.Contains("16", markdown);
            Assert.Contains("POQSettleProc4", markdown);
            Assert.Contains("51", markdown);
            Assert.Contains("326", markdown);
            Assert.Contains("stepInterfaces", markdown);
            Assert.Contains("runRowOwnedTables", markdown);
        }

        // 리뷰 발견 (3) — 프로시저 참조를 못 찾은 건수를 안 실으면 0인지 안 잰
        // 것인지 구분이 안 된다. 0이라고 말하는 것과 아무 말도 안 하는 것은 다르다.
        [Fact]
        public void HeaderCarriesUnresolvedProcedureReferenceCount()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(0, 0, 0),
                new HarnessGaps(
                    new List<string>(), 0, 0, 0,
                    StepInterfacesWereNull: false,
                    RunRowOwnedTablesWereNull: false,
                    KnownTableNamesWereEmpty: false)
                {
                    UnresolvedProcedureReferences = 0,
                });

            var markdown = StepSweepReportWriter.Render(report, "abc1234", "16", 0);
            var header = Section(markdown, "## 실행 조건");

            Assert.Contains("미해결 프로시저 참조: 0", header);
        }

        // 리뷰 발견 (4) — 측정 쌍이 0인 Job이 이름 없이 사라진다. Job별 표는 발화 0인
        // Job의 행을 생략하므로 거기서도 안 드러난다 - 머리말에서 이름으로 열거해야
        // 한다.
        [Fact]
        public void HeaderListsJobsWithZeroMeasuredPairsByName()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(0, 0, 0),
                new HarnessGaps(
                    new List<string>(), 0, 0, 0,
                    StepInterfacesWereNull: false,
                    RunRowOwnedTablesWereNull: false,
                    KnownTableNamesWereEmpty: false)
                {
                    JobsWithZeroMeasuredPairs = new[] { "POQSettleProc5", "POQSettleProc20" },
                });

            var markdown = StepSweepReportWriter.Render(report, "abc1234", "16", 0);
            var header = Section(markdown, "## 실행 조건");

            Assert.Contains("POQSettleProc5", header);
            Assert.Contains("POQSettleProc20", header);
        }

        // 목록이 비면 "없음"이라고 명시해야 한다 - 빈 문자열을 그대로 내면 결손이
        // 없다는 사실과 안 적었다는 사실을 구분 못 한다(PlanParseFailedJobs의 선례와
        // 같은 이유).
        [Fact]
        public void HeaderStatesNoneWhenNoJobHasZeroMeasuredPairs()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16", 0);
            var header = Section(markdown, "## 실행 조건");

            Assert.Contains("측정 쌍 0인 Job: 없음", header);
        }

        // 리뷰 발견 (7) — Job 단위 가드가 삼킨 예외를 조용히 넘기지 않는다.
        [Fact]
        public void HeaderListsJobsThatThrew()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(0, 0, 0),
                new HarnessGaps(
                    new List<string>(), 0, 0, 0,
                    StepInterfacesWereNull: false,
                    RunRowOwnedTablesWereNull: false,
                    KnownTableNamesWereEmpty: false)
                {
                    JobsThatThrew = new[] { "PoisonJob" },
                });

            var markdown = StepSweepReportWriter.Render(report, "abc1234", "16", 0);
            var header = Section(markdown, "## 실행 조건");

            Assert.Contains("PoisonJob", header);
        }

        // 리뷰 발견 (6) — "목차 파싱 실패"와 "상한(40단계) 초과로 제외"는 원인이
        // 다르다(전자는 JSON 자체를 못 읽고, 후자는 JSON은 정상 파싱되지만 버려진다).
        // 같은 라벨로 뭉치면 상한 초과 Job을 파싱 실패로 오인해 JSON을 디버깅하러
        // 가는 헛수고를 한다.
        [Fact]
        public void HeaderListsStepCountCapExceededJobsSeparatelyFromParseFailures()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(0, 0, 0),
                new HarnessGaps(
                    new List<string>(), 0, 0, 0,
                    StepInterfacesWereNull: false,
                    RunRowOwnedTablesWereNull: false,
                    KnownTableNamesWereEmpty: false)
                {
                    StepCountCapExceededJobs = new[] { "POQSettleProc4 (선언 73단계)" },
                });

            var markdown = StepSweepReportWriter.Render(report, "abc1234", "16", 0);
            var header = Section(markdown, "## 실행 조건");

            Assert.Contains("POQSettleProc4 (선언 73단계)", header);
            Assert.Contains("상한", header);
        }

        // (B)가 상한이라는 사실을 보고서가 스스로 말해야 한다 - 재생성 후 실제
        // 발화량의 예측으로 읽히면 다음 사람이 잘못된 기대를 갖는다.
        [Fact]
        public void ReportStatesThatConditionBIsAnUpperBound()
        {
            Assert.Contains("상한", StepSweepReportWriter.Render(Report(), "abc1234", "16", 0));
        }

        [Fact]
        public void TalliesSplitByCheckAndCondition()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(
                    new SweepFinding("J1", "S01", SweepCheck.B, SweepCondition.SimulatedCache17, "m"),
                    new SweepFinding("J1", "S02", SweepCheck.B, SweepCondition.SimulatedCache17, "m"),
                    new SweepFinding("J1", "S01", SweepCheck.A, SweepCondition.AsIs, "m")),
                "abc1234", "16", 0);

            // 총계 절만 잘라서 본다 - Job별 표의 행("| J1 | B | 0 | 2 |" 등)이 같은
            // 부분열을 내므로 markdown 전체에서 찾으면 (A)/(B) 열이 뒤바뀌어도 우연히
            // 통과한다.
            var totals = Section(markdown, "## 검사별 발화량");

            Assert.Contains("| B | 0 | 2 |", totals);
            Assert.Contains("| A | 1 | 0 |", totals);
        }

        [Fact]
        public void AnchoredFindingsBecomeAJudgementTableWithAnEmptyVerdictColumn()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(
                    new SweepFinding("POQSettleProc13", "S09", SweepCheck.B,
                        SweepCondition.SimulatedCache17, "m")
                    { Kind = "UPDATE", Ordinal = 3, Items = new[] { "PGNAME", "MALLID" } }),
                "abc1234", "16", 0);

            Assert.Contains("POQSettleProc13", markdown);
            Assert.Contains("UPDATE 3", markdown);
            Assert.Contains("PGNAME, MALLID", markdown);
            Assert.Contains("판정", markdown);

            // "판정"이 헤더 글자로만 있어도 위 단언은 통과한다 - 데이터 행의 마지막 칸이
            // 실제로 비어 있는지는 행 전체를 그대로 대조해야 잡힌다.
            Assert.Contains(
                "| 1 | B | B | POQSettleProc13 | S09 | UPDATE 3 | PGNAME, MALLID |  |",
                markdown);
        }

        [Fact]
        public void PerJobTableSplitsByJob()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(
                    new SweepFinding("J1", "S01", SweepCheck.B, SweepCondition.SimulatedCache17, "m"),
                    new SweepFinding("J2", "S01", SweepCheck.B, SweepCondition.SimulatedCache17, "m")),
                "abc1234", "16", 0);

            Assert.Contains("| J1 | B | 0 | 1 |", markdown);
            Assert.Contains("| J2 | B | 0 | 1 |", markdown);
        }

        // 미분류가 0이 아니면 검사 문구가 바뀐 것이다. 표에 안 실으면 아무도 모른다.
        //
        // 리뷰 발견 (5) — Checks 배열이 발화 0이어도 "미분류" 행을 항상 내므로
        // Assert.Contains("미분류", ...) 하나만으로는 라벨이 늘 있다는 것만 증명하고
        // 카운트가 조용히 0으로 굳어도 못 잡는다(뮤테이션으로 확인됨). 행 전체를
        // 대조해 (A) 열의 실제 카운트(1)까지 검증한다.
        [Fact]
        public void UnclassifiedCountIsShown()
        {
            var markdown = StepSweepReportWriter.Render(
                Report(new SweepFinding("J", "S01", SweepCheck.Unclassified, SweepCondition.AsIs, "m")),
                "abc1234", "16", 0);

            Assert.Contains("| 미분류 | 1 | 0 |", markdown);
        }

        // 목차 파싱 실패 Job이 하나도 없으면 "없음"이라고 명시해야 한다 - 빈 문자열을
        // 그대로 내면 결손이 없다는 사실과 결손 항목을 안 적었다는 사실을 구분 못 한다.
        // 공유 Report() 픽스처는 항상 값이 채워진 HarnessGaps를 주기 때문에 이 분기와
        // 0인 카운트가 실제로 찍히는지는 별도 픽스처로만 확인할 수 있다.
        [Fact]
        public void EmptyPlanParseFailedJobsRendersNoneAndZeroCountsStillPrint()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(0, 0, 0),
                new HarnessGaps(
                    new List<string>(), 0, 0, 0,
                    StepInterfacesWereNull: false,
                    RunRowOwnedTablesWereNull: false,
                    KnownTableNamesWereEmpty: false));

            var markdown = StepSweepReportWriter.Render(report, "abc1234", "16", 0);

            Assert.Contains("목차 파싱 실패 Job: 없음", markdown);
            Assert.Contains("측정 쌍: 0 (Job 0개)", markdown);
            Assert.Contains("단계 파일 누락: 0", markdown);
        }

        // 펜스 파싱 실패로 코드 집합 대조에서 뺀 단계 수를 지표 표에 안 실으면, 그
        // 지표의 분모가 줄어든 사실이 보고서 어디에도 드러나지 않는다.
        [Fact]
        public void IndicatorsTableShowsStepsSkippedForParseFailureCount()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(3, 2, 1) { StepsSkippedForParseFailure = 7 },
                new HarnessGaps(
                    new List<string>(), 0, 0, 0,
                    StepInterfacesWereNull: false,
                    RunRowOwnedTablesWereNull: false,
                    KnownTableNamesWereEmpty: false));

            var markdown = StepSweepReportWriter.Render(report, "abc1234", "16", 0);
            var indicators = Section(markdown, "## 캐시 17 선결 지표");

            Assert.Contains("| 펜스 파싱 실패로 코드 집합 대조에서 제외한 단계 수 | 7 |", indicators);
        }

        // 제외 건수가 0이 아니면, 코드 집합 지표(위 두 행)의 분모가 줄었다는 사실을
        // 사람이 놓치지 않도록 경고 문장을 낸다.
        [Fact]
        public void NonZeroSkippedCountEmitsWarningAboutReducedDenominator()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(3, 2, 1) { StepsSkippedForParseFailure = 7 },
                new HarnessGaps(
                    new List<string>(), 0, 0, 0,
                    StepInterfacesWereNull: false,
                    RunRowOwnedTablesWereNull: false,
                    KnownTableNamesWereEmpty: false));

            var markdown = StepSweepReportWriter.Render(report, "abc1234", "16", 0);
            var indicators = Section(markdown, "## 캐시 17 선결 지표");

            Assert.Contains("7", indicators);
            Assert.Contains("분모", indicators);
        }

        // 제외 건수가 0이면 경고를 내지 않는다 - 매번 나오면 아무도 안 읽는다. 공유
        // Report() 픽스처는 StepsSkippedForParseFailure가 기본값 0이므로 그대로 쓴다.
        [Fact]
        public void ZeroSkippedCountEmitsNoWarning()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16", 0);
            var indicators = Section(markdown, "## 캐시 17 선결 지표");

            Assert.DoesNotContain("분모", indicators);
        }
    
        // 「커밋: X」는 「X의 코드가 이 수치를 냈다」를 보증하지 못한다 - 더러운 트리에서
        // 내면 해시는 정직한데 수치는 커밋 안 된 코드의 것이다. 2026-08-27 그 틈으로
        // 거짓 기록이 실제로 커밋됐고 리뷰가 잡았으며, 최종 리뷰어는 「실행 당시 트리가
        // 깨끗했는지는 재현 없이 확인 불가」로 판정 불가를 남겼다. 그 판정 불가를 없앤다.
        [Fact]
        public void ExecutionConditionsReportACleanWorkingTree()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16", 0);
            Assert.Contains("- 작업 트리: 깨끗", markdown);
        }

        [Fact]
        public void ExecutionConditionsReportADirtyWorkingTree()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16", 3);
            Assert.Contains("- 작업 트리: **더러움** (변경된 파일 3개)", markdown);
        }

        // git 이 실패했을 때 「깨끗」으로 적으면 이 항목이 막으려던 거짓 기록을 다른
        // 자리에서 다시 만든다. 모르는 것은 모른다고 적는다.
        [Fact]
        public void ExecutionConditionsSayUnknownWhenGitCouldNotBeRead()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16", null);
            Assert.Contains("- 작업 트리: 알 수 없음", markdown);
        }

        // 침묵 분모 - 계측(태스크 4)이 있어도 보고서에 안 실리면 다음 사람이 못 읽는다.
        // 열 개 계수에 서로 다른 값을 넣고 각 값이 자기 라벨 줄에 찍히는지 확인한다 -
        // 열 개에 같은 값을 넣으면 라벨과 값이 뒤바뀌어도 통과한다.
        [Fact]
        public void SilenceDenominatorsSectionPrintsEachCounterOnItsOwnLabel()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(0, 0, 0)
                {
                    AnchorsResolved = 101,
                    AnchorsUnresolved = 202,
                    AnchorsDroppedForAmbiguity = 303,
                    StatementsWithLineage = 404,
                    StatementsReadingOnlyStaging = 505,
                    StatementsReadingOwnTarget = 606,
                    StagingExemptionsCancelledByOwnTarget = 707,
                    StatementsWithSubordinatePredicates = 808,
                    SubordinatePredicateColumnTotal = 909,
                    StagingSourceTotal = 1010,
                },
                new HarnessGaps(
                    new List<string>(), 0, 0, 0,
                    StepInterfacesWereNull: false,
                    RunRowOwnedTablesWereNull: false,
                    KnownTableNamesWereEmpty: false));

            var markdown = StepSweepReportWriter.Render(report, "abc1234", "16", 0);
            Assert.Contains("## 침묵 분모", markdown);

            var section = Section(markdown, "## 침묵 분모");
            Assert.Contains("| 앵커가 서수로 해결된 문장 수 | 101 |", section);
            Assert.Contains("| 앵커는 있으나 서수로 환산되지 않은 문장 수 | 202 |", section);
            Assert.Contains("| (Kind, Ordinal) 모호성 가드가 버린 문장 수 | 303 |", section);
            Assert.Contains("| 계보 원천을 가진 문장 수 | 404 |", section);
            Assert.Contains("| 스테이징만 읽어 검사 C 가 면제한 문장 수 | 505 |", section);
            Assert.Contains("| 자기 대상을 읽는 문장 수 | 606 |", section);
            Assert.Contains("| 자기 대상을 읽어 스테이징 면제가 취소된 문장 수 | 707 |", section);
            Assert.Contains("| 하위 범위 술어 컬럼을 가진 문장 수 | 808 |", section);
            Assert.Contains("| 하위 범위 술어 컬럼의 총수 | 909 |", section);
            Assert.Contains("| 스테이징 원천의 총수 | 1010 |", section);
        }

        // 2026-08-27 staging-lineage 최종 리뷰 Critical 1 - 이 계수가 0이면 방어가
        // 도달하지 못한 것이지 수정이 살아 있다는 증거가 아니다. 그 읽는 법을 절이
        // 스스로 적어야 한다.
        [Fact]
        public void SilenceDenominatorsSectionExplainsHowToReadAZeroCancellationCount()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16", 0);
            var section = Section(markdown, "## 침묵 분모");

            Assert.Contains("도달하지 못한 것이다", section);
            Assert.Contains("재지 않았다는 증거", section);
        }

        // 이 절은 사유(어느 좌표가 어느 가드에 침묵당했는가)가 아니라 분모라는 것을
        // 스스로 밝혀야 한다 - 안 그러면 표를 사유 목록으로 오독한다.
        [Fact]
        public void SilenceDenominatorsSectionStatesItIsADenominatorNotAReason()
        {
            var markdown = StepSweepReportWriter.Render(Report(), "abc1234", "16", 0);
            var section = Section(markdown, "## 침묵 분모");

            Assert.Contains("사유가 아니라 분모", section);
        }
}
}
