using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    public enum RosterDefectType
    {
        ProcedureMissing,
        ProcedureDuplicated,
        ProcedureUnknown,
        PlaceholderTitleRemaining,
        NoStages,
        StageTitleDuplicated,
    }

    public sealed record RosterDefect(RosterDefectType Type, string Subject, string Message);

    /// <summary>
    /// 명부와 실제 명세서 목록을 대조한다.
    ///
    /// [왜 배너가 아니라 중단인가] 명부에서 빠진 SP의 규칙은 문서에서 아무 흔적 없이
    /// 사라지고, 읽는 사람이 그 사실을 알 방법이 없다. 조용한 결함은 이 저장소가 반복해
    /// 물린 자리다. 대신 `## 제외` 섹션이 의도한 제외를 명시적으로 허용하므로, 사람이
    /// 일부러 빼는 길은 열려 있고 조용히 빠지는 길만 막힌다.
    ///
    /// [왜 자리표시자도 중단인가] 무인 배치가 도구 초안대로의 순서로 인수인계 문서를
    /// 배송하는 것이 이 기능에서 가장 나쁜 결말이다.
    ///
    /// [왜 제목 중복도 중단인가 (2026-09-06 T7 리뷰 프로브)] MarkdownSectionLocator.LocateSection
    /// (exact: true)은 같은 헤딩 문자열을 늘 첫 번째 줄로 해석한다. 정책서 파서
    /// (PolicyDocumentParser.Parse)가 단계 제목 목록을 순회할 때 두 제목이 같으면
    /// 두 순회가 같은 절을 가리키고, 진짜 두 번째 단계의 표는 한 번도 파싱되지 않는다 -
    /// 그 자리에 첫 단계 행의 유령 사본이 들어앉는다. 오탐 하나가 아니라 한 단계의
    /// 업무 규칙 전부가 문서 검증에서 증발하는 문제라, 파서 쪽에서 안전하게 다룰 방법이
    /// 없다. 그래서 명부가 애초에 금지해야 한다.
    ///
    /// [대소문자 판단] LocateSection(exact: true)의 비교는 `line.Trim() == headingLine`로
    /// 대소문자를 구분하는(ordinal) 완전 일치다. 그러므로 이 검사도 대소문자만 다른
    /// 제목은 서로 다른 제목으로 본다 - 파서가 실제로 다른 헤딩으로 취급하는 것과
    /// 어긋나면 검사와 파서가 다른 말을 하게 된다.
    /// </summary>
    public static class SettlementRosterReconciler
    {
        public static IReadOnlyList<RosterDefect> Reconcile(
            SettlementProcessRoster roster,
            IReadOnlyList<string> discoveredLabels)
        {
            var defects = new List<RosterDefect>();

            if (roster.Stages.Count == 0)
            {
                defects.Add(new RosterDefect(
                    RosterDefectType.NoStages,
                    string.Empty,
                    "명부에 단계가 하나도 없습니다. output/settlement-process.md를 채우십시오."));
            }

            foreach (var stage in roster.Stages)
            {
                if (stage.Title.Contains(SettlementProcessRoster.PlaceholderMarker, StringComparison.Ordinal))
                {
                    defects.Add(new RosterDefect(
                        RosterDefectType.PlaceholderTitleRemaining,
                        stage.Title,
                        $"단계 '{stage.Title}'이 초안 자리표시자 그대로입니다. 업무 이름을 붙이십시오 - 이 제목이 정책서의 목차가 됩니다."));
                }
            }

            foreach (var duplicateTitle in roster.Stages
                         .Select(s => s.Title)
                         .GroupBy(t => t, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                defects.Add(new RosterDefect(
                    RosterDefectType.StageTitleDuplicated,
                    duplicateTitle.Key,
                    $"단계 제목 '{duplicateTitle.Key}'이 {duplicateTitle.Count()}번 실려 있습니다. " +
                    "MarkdownSectionLocator는 같은 제목의 첫 헤딩만 찾으므로 뒤 단계의 표가 통째로 사라집니다. 제목을 다르게 붙이십시오."));
            }

            var staged = roster.AllStagedProcedures().ToList();
            var discovered = new HashSet<string>(discoveredLabels, StringComparer.OrdinalIgnoreCase);
            var accountedFor = new HashSet<string>(staged.Concat(roster.Excluded), StringComparer.OrdinalIgnoreCase);

            foreach (var label in discoveredLabels.Where(l => !accountedFor.Contains(l)))
            {
                defects.Add(new RosterDefect(
                    RosterDefectType.ProcedureMissing,
                    label,
                    $"명세서가 있는 '{label}'이 명부에 없습니다. 어느 단계에 넣거나 '## 제외'에 적으십시오."));
            }

            foreach (var duplicate in staged
                         .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                defects.Add(new RosterDefect(
                    RosterDefectType.ProcedureDuplicated,
                    duplicate.Key,
                    $"'{duplicate.Key}'이 명부에 {duplicate.Count()}번 실려 있습니다. 한 번만 실으십시오."));
            }

            foreach (var unknown in staged.Concat(roster.Excluded)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(p => !discovered.Contains(p)))
            {
                defects.Add(new RosterDefect(
                    RosterDefectType.ProcedureUnknown,
                    unknown,
                    $"명부의 '{unknown}'에 해당하는 명세서가 없습니다. 이름을 확인하십시오."));
            }

            return defects;
        }
    }
}
