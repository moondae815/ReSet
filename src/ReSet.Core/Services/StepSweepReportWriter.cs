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

        public static string Render(SweepReport report, string commitHash, string cacheFormatVersions)
        {
            var b = new StringBuilder();

            b.AppendLine("# 단계 검사 스윕");
            b.AppendLine();
            AppendConditions(b, report.Gaps, commitHash, cacheFormatVersions);
            AppendTotals(b, report.Findings);
            AppendPerJob(b, report.Findings);
            AppendUpperBoundNote(b);
            AppendAnchoredFindings(b, report.Findings);
            AppendIndicators(b, report.Indicators);

            return b.ToString();
        }

        private static void AppendConditions(
            StringBuilder b, HarnessGaps gaps, string commitHash, string cacheFormatVersions)
        {
            b.AppendLine("## 실행 조건");
            b.AppendLine();
            b.AppendLine($"- 커밋: `{commitHash}`");
            b.AppendLine($"- 캐시 인덱스 `FormatVersion` 집합: {cacheFormatVersions}");
            b.AppendLine($"- 측정 쌍: {gaps.MeasuredPairs} (Job {gaps.MeasuredJobs}개)");
            b.AppendLine($"- 단계 파일 누락: {gaps.MissingStepFiles}");

            var failed = gaps.PlanParseFailedJobs.Count == 0
                ? "없음"
                : string.Join(", ", gaps.PlanParseFailedJobs);
            b.AppendLine($"- 목차 파싱 실패 Job: {failed}");

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

        private static int Count(
            IEnumerable<SweepFinding> findings, SweepCheck check, SweepCondition condition) =>
            findings.Count(f => f.Check == check && f.Condition == condition);

        private static string Label(SweepCheck check) =>
            check == SweepCheck.Unclassified ? "미분류" : check.ToString();
    }
}
