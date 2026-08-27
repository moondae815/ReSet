using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// SweepReport를 사람이 읽고 회차 간에 견줄 수 있는 마크다운으로 낸다.
    ///
    /// [왜 결손을 머리말에 강제로 싣는가] 대상 범위가 줄면 발화량도 줄어드는데, 결손을
    /// 안 적으면 그 감소가 개선처럼 읽힌다. 카탈로그가 "총량을 회차 간에 비교하지 마라"고
    /// 경고하는 함정이 정확히 이것이다. 메시지 원문은 표에 싣지 않는다 - 파이프 문자가
    /// 섞이면 표가 깨진다.
    /// </summary>
    public static class StepSweepReportWriter
    {
        // 0이어도 행을 낸다. 빠진 검사와 발화가 0인 검사는 다른 사실이다.
        private static readonly SweepCheck[] Checks =
        {
            SweepCheck.A, SweepCheck.B, SweepCheck.C,
            SweepCheck.D, SweepCheck.E, SweepCheck.Unclassified,
        };

        public static string Render(
            SweepReport report, string commitHash, string cacheFormatVersions, int? changedFileCount)
        {
            var b = new StringBuilder();

            b.AppendLine("# 단계 검사 스윕");
            b.AppendLine();
            AppendConditions(b, report.Gaps, commitHash, cacheFormatVersions, changedFileCount);
            AppendTotals(b, report.Findings);
            AppendPerJob(b, report.Findings);
            AppendUpperBoundNote(b);
            AppendAnchoredFindings(b, report.Findings);
            AppendIndicators(b, report.Indicators);
            AppendSilenceDenominators(b, report.Indicators);

            return b.ToString();
        }

        private static void AppendConditions(
            StringBuilder b, HarnessGaps gaps, string commitHash, string cacheFormatVersions,
            int? changedFileCount)
        {
            b.AppendLine("## 실행 조건");
            b.AppendLine();
            b.AppendLine($"- 커밋: `{commitHash}`");

            // [왜 트리 청결도를 함께 싣는가 - 2026-08-27]
            // 「커밋: X」만으로는 「X의 코드가 이 수치를 냈다」가 보증되지 않는다.
            // 더러운 트리에서 내면 해시는 정직한데 수치는 커밋 안 된 코드의 것이다.
            // 그 틈으로 거짓 기록이 실제로 커밋됐고, 최종 리뷰어는 「실행 당시 트리가
            // 깨끗했는지는 재현 없이 확인 불가」로 판정 불가를 남겼다.
            //
            // null 은 「깨끗」이 아니라 「알 수 없음」이다 - git 호출이 실패했을 때
            // 깨끗으로 적으면 이 항목이 막으려던 거짓 기록을 다른 자리에서 다시 만든다.
            b.AppendLine(changedFileCount switch
            {
                0 => "- 작업 트리: 깨끗",
                > 0 => $"- 작업 트리: **더러움** (변경된 파일 {changedFileCount}개) — "
                       + "이 수치는 커밋되지 않은 코드가 낸 것이다",
                _ => "- 작업 트리: 알 수 없음 (git 상태를 읽지 못했다)",
            });
            b.AppendLine($"- 캐시 인덱스 `FormatVersion` 집합: {cacheFormatVersions}");
            b.AppendLine($"- 측정 쌍: {gaps.MeasuredPairs} (Job {gaps.MeasuredJobs}개)");
            b.AppendLine($"- 단계 파일 누락: {gaps.MissingStepFiles}");

            var failed = gaps.PlanParseFailedJobs.Count == 0
                ? "없음"
                : string.Join(", ", gaps.PlanParseFailedJobs);
            b.AppendLine($"- 목차 파싱 실패 Job: {failed}");

            // 리뷰 발견 (6) - "목차 파싱 실패"와 원인이 다르다: JSON은 정상 파싱되지만
            // BatchStepPlanParser.MaxSteps(40) 상한을 넘어 버려진 Job이다. 같은
            // 라벨로 뭉치면 라벨을 믿고 JSON을 디버깅하러 가는 사람이 헛수고한다.
            if (gaps.StepCountCapExceededJobs.Count > 0)
            {
                b.AppendLine(
                    $"- 목차 단계 수 상한(40단계) 초과로 제외된 Job: " +
                    $"{string.Join(", ", gaps.StepCountCapExceededJobs)}");
            }

            // 리뷰 발견 (3) - 프로시저 참조를 못 찾으면(SweepCommand의 디렉터리 색인
            // 미스, StepSweepService의 DdlByProcedure 조회 미스) 조용히 continue하던
            // 두 자리의 합. 0이라고 말하는 것과 아무 말도 안 하는 것은 다르다.
            b.AppendLine($"- 단계 번들 세대: {Generation(gaps.StepBundleOldest, gaps.StepBundleNewest)}");
            b.AppendLine($"- 명세서 세대: {Generation(gaps.SpecOldest, gaps.SpecNewest)}");
            b.AppendLine($"- 미해결 프로시저 참조: {gaps.UnresolvedProcedureReferences}");

            // 리뷰 발견 (4) - 목차 파싱은 됐지만(PlanParseFailedJobs에는 안 실림)
            // 측정 쌍이 0인 Job. Job별 표는 발화 0인 Job의 행을 생략하므로 거기서도
            // 안 드러난다 - 대상 범위가 준 것이 개선처럼 읽히지 않게 이름을 댄다.
            var zeroMeasured = gaps.JobsWithZeroMeasuredPairs.Count == 0
                ? "없음"
                : string.Join(", ", gaps.JobsWithZeroMeasuredPairs);
            b.AppendLine($"- 측정 쌍 0인 Job: {zeroMeasured}");

            // 리뷰 발견 (7) - Job 단위 가드가 삼킨 예외의 Job 이름. 조용히 삼키지
            // 않는다 - 몇 개는 못 쟀는지 다음 사람이 알아야 한다.
            if (gaps.JobsThatThrew.Count > 0)
            {
                b.AppendLine($"- 예외로 건너뛴 Job: {string.Join(", ", gaps.JobsThatThrew)}");
            }

            if (gaps.StepInterfacesWereNull)
            {
                b.AppendLine(
                    "- `stepInterfaces`를 `null`로 넘겼다(DB 메타데이터가 필요해 로컬에서 " +
                    "만들 수 없다). 검사 A~E는 이 값을 읽지 않는다.");
            }

            if (gaps.RunRowOwnedTablesWereNull)
            {
                b.AppendLine("- `runRowOwnedTables`를 `null`로 넘겼다(같은 이유). 검사 A~E는 이 값을 읽지 않는다.");
            }

            if (gaps.KnownTableNamesWereEmpty)
            {
                b.AppendLine("- `knownTableNames`가 비어 유령 테이블 검사가 소프트 스킵됐다.");
            }

            if (gaps.StepBundleNewest is { } stepNewest
                && gaps.SpecNewest is { } specNewest
                && stepNewest < specNewest)
            {
                b.AppendLine();
                b.AppendLine(
                    "**단계 번들이 명세서보다 낡았다.** 축 B의 기준값은 명세서이므로, 이 스윕이 " +
                    "잡은 불일치 중 일부는 이행 결함이 아니라 **세대 차이**일 수 있다 — 폐기된 " +
                    "명세서로 만든 지시서를 현행 명세서와 맞댄 것이기 때문이다. 번들을 재생성한 " +
                    "뒤 다시 재는 것이 순서다(`docs/audit-defect-catalog.md` 3절).");
            }

            b.AppendLine();
        }

        private static void AppendTotals(StringBuilder b, IReadOnlyList<SweepFinding> findings)
        {
            b.AppendLine("## 검사별 발화량");
            b.AppendLine();
            b.AppendLine("| 검사 | (A) 오늘 | (B) 캐시 17 모사 |");
            b.AppendLine("| :--- | ---: | ---: |");

            foreach (var check in Checks)
            {
                b.AppendLine(
                    $"| {Label(check)} | {Count(findings, check, SweepCondition.AsIs)} " +
                    $"| {Count(findings, check, SweepCondition.SimulatedCache17)} |");
            }

            b.AppendLine();
        }

        private static void AppendPerJob(StringBuilder b, IReadOnlyList<SweepFinding> findings)
        {
            b.AppendLine("## Job별 발화량");
            b.AppendLine();
            b.AppendLine("| Job | 검사 | (A) 오늘 | (B) 캐시 17 모사 |");
            b.AppendLine("| :--- | :--- | ---: | ---: |");

            var jobs = findings
                .Select(f => f.JobName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal);

            foreach (var job in jobs)
            {
                var ofJob = findings.Where(f => f.JobName == job).ToList();
                foreach (var check in Checks)
                {
                    var asIs = Count(ofJob, check, SweepCondition.AsIs);
                    var simulated = Count(ofJob, check, SweepCondition.SimulatedCache17);
                    if (asIs == 0 && simulated == 0) continue;

                    b.AppendLine($"| {job} | {Label(check)} | {asIs} | {simulated} |");
                }
            }

            b.AppendLine();
        }

        private static void AppendUpperBoundNote(StringBuilder b)
        {
            b.AppendLine("## 조건 (B)는 상한이다");
            b.AppendLine();
            b.AppendLine(
                "(B)는 모델이 「오류 코드」 표를 완전히 전사한다고 가정하고 원본 DDL에서 만든 " +
                "사전을 주입한 값이다. 실제 재생성에서는 전사 오류가 나고, 그 오류는 " +
                "`ErrorType.ErrorCodeTableMissing` 전사 대조가 따로 잡는다. **따라서 (B)는 " +
                "축이 켜졌을 때의 상한이지 재생성 후 실제 발화량의 예측이 아니다.**");
            b.AppendLine();
        }

        private static void AppendAnchoredFindings(
            StringBuilder b, IReadOnlyList<SweepFinding> findings)
        {
            b.AppendLine("## 검사 B·C 발화 목록");
            b.AppendLine();
            b.AppendLine("판정 칸은 비어 있다 — 원본 DDL과 이행 SQL을 읽어 사람이 채운다.");
            b.AppendLine();
            b.AppendLine("| # | 검사 | 조건 | Job | 단계 | 문장 | 항목 | 판정 |");
            b.AppendLine("| ---: | :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

            var rows = findings
                .Where(f => f.Check == SweepCheck.B || f.Check == SweepCheck.C)
                .ToList();

            for (var i = 0; i < rows.Count; i++)
            {
                var f = rows[i];
                var statement = f.Kind == null ? "—" : $"{f.Kind} {f.Ordinal}";
                var items = f.Items.Count == 0 ? "—" : string.Join(", ", f.Items);
                var condition = f.Condition == SweepCondition.AsIs ? "A" : "B";

                b.AppendLine(
                    $"| {i + 1} | {Label(f.Check)} | {condition} | {f.JobName} | {f.StepCode} " +
                    $"| {statement} | {items} |  |");
            }

            b.AppendLine();
        }

        private static void AppendIndicators(StringBuilder b, SweepIndicators indicators)
        {
            b.AppendLine("## 캐시 17 선결 지표");
            b.AppendLine();
            b.AppendLine("| 지표 | 값 |");
            b.AppendLine("| :--- | ---: |");
            b.AppendLine($"| 다중 레거시 SP 단계 수 | {indicators.MultiProcedureSteps} |");
            b.AppendLine($"| SP 표에는 있는데 단계에 없는 코드가 있는 단계 수 | {indicators.StepsMissingSpecCodes} |");
            b.AppendLine($"| 단계에는 있는데 SP 표에 없는 코드가 있는 단계 수 | {indicators.StepsWithUnknownCodes} |");
            b.AppendLine(
                $"| 펜스 파싱 실패로 코드 집합 대조에서 제외한 단계 수 | " +
                $"{indicators.StepsSkippedForParseFailure} |");
            b.AppendLine(
                $"| 코드 앵커가 둘 이상의 문장에 붙은 단계 수 | " +
                $"{indicators.StepsWithReusedCodeAnchors} |");
            b.AppendLine();

            if (indicators.StepsSkippedForParseFailure > 0)
            {
                b.AppendLine(
                    $"펜스 파싱 실패로 {indicators.StepsSkippedForParseFailure}개 단계를 코드 집합 " +
                    "대조에서 제외했다 - 위 두 코드 집합 지표(SP 표에는 있는데 단계에 없는 코드, " +
                    "단계에는 있는데 SP 표에 없는 코드)의 분모가 그만큼 줄었다는 뜻이다. 이 값이 " +
                    "크면 두 지표가 코퍼스 전체를 대표하지 않는다.");
                b.AppendLine();
            }
        }

        /// <summary>
        /// [왜 이 절이 있는가] 승격 전에는 앵커가 안 풀려 검사 C의 스테이징 면제 같은
        /// 가드가 도달 불가능하다 - 그래서 승격 전후 값의 **증가분이 곧 이번에 새로
        /// 생긴 침묵**이다. 좌표 차분(검사별 발화량 표)은 이 부류를 못 본다 - 가드가
        /// 조건 (A)에서도 (B)에서도 같은 좌표를 침묵시키면 차분이 정의상 0이기
        /// 때문이다. 계측(<see cref="SweepIndicators"/>)이 있어도 보고서에 안 실리면
        /// 다음 사람이 못 읽는다 - 이 절이 그 자리다.
        /// </summary>
        private static void AppendSilenceDenominators(StringBuilder b, SweepIndicators indicators)
        {
            b.AppendLine("## 침묵 분모");
            b.AppendLine();
            b.AppendLine(
                "발화가 늘어난 자리만 보면 가려져 있던 침묵이 함께 켜지는 것을 못 본다. " +
                "승격 전에는 앵커가 안 풀려 면제가 도달 불가능하므로, 아래 값의 **증가분이 곧 " +
                "이번에 새로 생긴 침묵**이다. 좌표 차분은 이 부류를 못 본다 - 가드가 조건 " +
                "(A)에서도 (B)에서도 같은 좌표를 침묵시키면 차분이 정의상 0이기 때문이다.");
            b.AppendLine();
            b.AppendLine("| 분모 | 값 |");
            b.AppendLine("| :--- | ---: |");

            // [라벨이 계획서와 다르다 - 분모를 보이게 적는다]
            // 태스크 4의 수정으로 이 두 계수의 분모가 "그 단계의 모든 문장"에서
            // "앵커(U-앵커 또는 CodeAnchor)를 보유한 문장"으로 좁혀졌다 -
            // SweepIndicators.AnchorsResolved 문서 및 StepSweepService.cs의
            // anchorBearing 계산 참고. 라벨이 분모를 말하지 않으면 사람이 전체
            // 문장 대비로 오독한다(코퍼스 실측으로 실제로 그렇게 갈렸다:
            // AnchorsUnresolved=1641 vs AnchorsResolved+AnchorsDroppedForAmbiguity=940).
            b.AppendLine($"| 앵커가 서수로 해결된 문장 수 | {indicators.AnchorsResolved} |");
            b.AppendLine(
                $"| 앵커는 있으나 서수로 환산되지 않은 문장 수 | {indicators.AnchorsUnresolved} |");
            b.AppendLine(
                $"| (Kind, Ordinal) 모호성 가드가 버린 문장 수 | " +
                $"{indicators.AnchorsDroppedForAmbiguity} |");
            b.AppendLine($"| 계보 원천을 가진 문장 수 | {indicators.StatementsWithLineage} |");
            b.AppendLine(
                $"| 스테이징만 읽어 검사 C 가 면제한 문장 수 | " +
                $"{indicators.StatementsReadingOnlyStaging} |");
            b.AppendLine($"| 자기 대상을 읽는 문장 수 | {indicators.StatementsReadingOwnTarget} |");
            b.AppendLine(
                "| 자기 대상을 읽어 스테이징 면제가 취소된 문장 수 | " +
                $"{indicators.StagingExemptionsCancelledByOwnTarget} |");
            b.AppendLine(
                "| 하위 범위 술어 컬럼을 가진 문장 수 | " +
                $"{indicators.StatementsWithSubordinatePredicates} |");
            b.AppendLine($"| 그 컬럼의 총수 | {indicators.SubordinatePredicateColumnTotal} |");
            b.AppendLine($"| 스테이징 원천의 총수 | {indicators.StagingSourceTotal} |");
            b.AppendLine();
            b.AppendLine(
                "**「자기 대상을 읽어 스테이징 면제가 취소된 문장 수」가 0 이면 그 방어가 도달하지 " +
                "못한 것이다.** 수정이 살아 있다는 증거가 아니라 재지 않았다는 증거로 읽는다 " +
                "(2026-08-27 staging-lineage 최종 리뷰 Critical 1).");
            b.AppendLine();
            b.AppendLine(
                "이 표는 **사유가 아니라 분모**다. 어느 좌표가 어느 가드에 침묵당했는지는 세지 " +
                "않는다 - 그러려면 검증기가 판정 사유를 내보내야 한다.");
            b.AppendLine();
        }

        /// <summary>
        /// mtime 범위를 사람이 읽는 한 줄로. 값이 없어도 "알 수 없음"으로 행을 낸다 -
        /// 모른다는 사실이 사라지면 그것도 감춰진 결손이다(§6).
        /// </summary>
        private static string Generation(DateTimeOffset? oldest, DateTimeOffset? newest)
        {
            if (oldest is not { } from || newest is not { } to) return "알 수 없음";

            var a = from.ToString("yyyy-MM-dd");
            var z = to.ToString("yyyy-MM-dd");
            return a == z ? a : $"{a} ~ {z}";
        }

        private static int Count(
            IEnumerable<SweepFinding> findings, SweepCheck check, SweepCondition condition) =>
            findings.Count(f => f.Check == check && f.Condition == condition);

        private static string Label(SweepCheck check) =>
            check == SweepCheck.Unclassified ? "미분류" : check.ToString();
    }
}
