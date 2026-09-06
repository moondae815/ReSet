using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 명부가 없을 때 초안 마크다운을 만든다.
    ///
    /// [초안이 말해도 되는 것과 안 되는 것] 2026-09-06 실측 - 코퍼스 전체의 EXEC 간선은
    /// 2개뿐이고, 쓰기→읽기 그래프는 허브 테이블(3개 이상이 쓰는 테이블) 때문에 간선
    /// 99개의 거의 완전 그래프가 된다. 허브를 빼면 5개만 남고 전부 출발점이 요율
    /// 스냅샷 적재 SP다. 그래서 초안이 확정할 수 있는 것은 「산출을 남이 읽는 SP가
    /// 앞」과 「EXEC로 묶인 무리」뿐이고, 나머지의 상호 순서는 모른다.
    ///
    /// 모르는 것을 지어내지 않는다. 자리표시자 단계에 모아 두고 사람에게 넘기며,
    /// 그 자리표시자가 남아 있는 한 SettlementRosterReconciler가 생성을 중단시킨다.
    ///
    /// [기계 확정 단계에도 자리표시자를 붙인다 - 2026-09-06 M2] 「기계 확정」이 확정하는
    /// 것은 <b>묶음</b>(과 생산자 단계에서는 그 묶음이 앞선다는 것)뿐이고 <b>단계 이름</b>이
    /// 아니다. 업무 이름은 코드에 없으므로 여기서도 지어내지 않는다 - 그래서 세 종류
    /// 단계가 모두 자리표시자 제목을 받고, 사람이 이름을 붙이기 전에는 생성이 막힌다.
    /// 각 단계에 붙는 주석도 그 구분을 그대로 말한다(「기계 확정: 묶음」 + 「이름은
    /// 기계가 모른다」). 종전 주석은 「[기계 확정]」만 적어 제 옆의 자리표시자 제목과
    /// 모순돼 보였다.
    /// </summary>
    public static class SettlementProcessRosterDraft
    {
        /// <summary>3개 이상이 쓰는 테이블은 허브로 보고 순서 판정에서 뺀다.</summary>
        private const int HubWriterThreshold = 3;

        public static string Build(IReadOnlyList<PolicySource> sources)
        {
            var writes = sources.ToDictionary(s => s.Label, s => WriteSet(s), StringComparer.OrdinalIgnoreCase);
            var reads = sources.ToDictionary(s => s.Label, s => ReadSet(s), StringComparer.OrdinalIgnoreCase);

            var hubs = writes.Values
                .SelectMany(set => set)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= HubWriterThreshold)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 1. 허브를 뺀 산출을 남이 읽는 SP - 앞선다고 말할 수 있다.
            var producers = sources
                .Where(s => sources.Any(other =>
                    !string.Equals(other.Label, s.Label, StringComparison.OrdinalIgnoreCase)
                    && writes[s.Label].Except(hubs, StringComparer.OrdinalIgnoreCase)
                        .Intersect(reads[other.Label], StringComparer.OrdinalIgnoreCase).Any()))
                .Select(s => s.Label)
                .ToList();

            // 2. EXEC로 묶인 무리 - 한 단계에 함께 놓는다고 말할 수 있다.
            //
            // [왜 마지막 점 구획으로 대조하는가] EXEC 대상은 스키마를 붙여
            // (`EXEC dbo.X`) 쓸 수도, 안 붙여(`EXEC X`) 쓸 수도 있다. 대상 이름의
            // 부분 문자열(EndsWith)로 대조하면 "EXEC Ins"가 이름이 우연히 "Ins"로
            // 끝나는 dbo.BulkIns를 끌어들이는 오탐이 생긴다(2026-09-06 리뷰 재현).
            // Normalize는 이미 스키마·대괄호를 벗기고 마지막 구획만 남기므로, 두
            // 자리(대상 판별·무리 구성) 모두 이 함수 하나만 거치게 해 판정이
            // 갈리지 않게 한다.
            var callers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                var callees = ExecTargets(source.DdlText)
                    .Where(c => sources.Any(s =>
                        string.Equals(Normalize(s.Label), Normalize(c), StringComparison.OrdinalIgnoreCase)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (callees.Count > 0)
                {
                    callers[source.Label] = callees;
                }
            }

            var rawExecGroups = new List<List<string>>();
            foreach (var (caller, callees) in callers)
            {
                var group = new List<string> { caller };
                group.AddRange(sources
                    .Select(s => s.Label)
                    .Where(label => callees.Any(c =>
                        string.Equals(Normalize(label), Normalize(c), StringComparison.OrdinalIgnoreCase))));
                rawExecGroups.Add(group.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }

            // 3. 이미 자리 잡은 SP는 뒤 구획에서 뺀다 - 명부의 한 SP는 정확히
            // 한 번만 나와야 SettlementRosterReconciler의 ProcedureDuplicated를
            // 만들지 않는다. 우선순위는 선행 적재 → 호출 무리 → 순서 미상이다:
            // 앞 둘은 기계가 근거(쓰기→읽기, EXEC)를 대는 확정 판정이고 순서
            // 미상은 그 무엇도 확정하지 못한 잔여이므로, 확정 판정이 항상
            // 잔여보다 우선한다. 이 방향을 지키지 않으면(예: 순서 미상이 먼저
            // 자리를 차지) 기계가 아는 것이 사람에게 안 보이게 된다.
            var placed = new HashSet<string>(producers, StringComparer.OrdinalIgnoreCase);
            var execGroups = new List<List<string>>();
            foreach (var group in rawExecGroups)
            {
                var remaining = group.Where(label => !placed.Contains(label)).ToList();
                if (remaining.Count == 0)
                {
                    continue;
                }

                foreach (var label in remaining)
                {
                    placed.Add(label);
                }

                execGroups.Add(remaining);
            }

            var unknown = sources.Select(s => s.Label).Where(l => !placed.Contains(l)).ToList();

            return Render(producers, execGroups, unknown);
        }

        private static string Render(
            IReadOnlyList<string> producers,
            IReadOnlyList<List<string>> execGroups,
            IReadOnlyList<string> unknown)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 정산 프로세스 명부");
            sb.AppendLine("<!-- 도구는 이 파일이 없을 때만 초안을 만들고, 있으면 읽기만 합니다. -->");
            sb.AppendLine("<!-- 단계 순서 = 등장 순서. H2 제목이 그대로 정책서의 목차가 됩니다. -->");
            sb.AppendLine();

            var number = 1;

            if (producers.Count > 0)
            {
                sb.AppendLine($"## {number++}. {SettlementProcessRoster.PlaceholderMarker} (선행 적재)");
                sb.AppendLine("<!-- [기계 확정: 묶음과 순서] 아래 SP의 산출을 다른 SP가 읽습니다. 앞선다고 말할 수 있습니다.");
                sb.AppendLine("     단계 이름은 기계가 모릅니다 - 업무 이름을 붙여 주십시오(자리표시자가 남아 있으면 생성이 중단됩니다). -->");
                foreach (var label in producers)
                {
                    sb.AppendLine($"- {label}");
                }

                sb.AppendLine();
            }

            if (unknown.Count > 0)
            {
                sb.AppendLine($"## {number++}. {SettlementProcessRoster.PlaceholderMarker} (순서 미상)");
                sb.AppendLine("<!-- [순서 미상] 기계가 상호 순서를 판별하지 못했습니다.");
                sb.AppendLine("     허브 테이블을 함께 쓰고 읽어 쓰기-읽기 관계로는 갈리지 않습니다.");
                sb.AppendLine("     실제 배치 실행 순서를 아는 분이 단계로 나눠 주십시오. -->");
                foreach (var label in unknown)
                {
                    sb.AppendLine($"- {label}");
                }

                sb.AppendLine();
            }

            foreach (var group in execGroups)
            {
                sb.AppendLine($"## {number++}. {SettlementProcessRoster.PlaceholderMarker} (호출 무리)");
                sb.AppendLine("<!-- [기계 확정: 묶음] 첫 SP가 나머지를 EXEC 합니다. 한 단계로 묶을 수 있습니다.");
                sb.AppendLine("     단계 이름은 기계가 모릅니다 - 업무 이름을 붙여 주십시오(자리표시자가 남아 있으면 생성이 중단됩니다). -->");
                foreach (var label in group)
                {
                    sb.AppendLine($"- {label}");
                }

                sb.AppendLine();
            }

            sb.AppendLine(SettlementProcessRoster.ExcludedHeading);
            sb.AppendLine("<!-- 정산 정책서에서 빼고 싶은 SP를 여기 적으십시오. 비워 두어도 됩니다. -->");

            return sb.ToString();
        }

        private static HashSet<string> WriteSet(PolicySource source) =>
            source.Analysis.InsertTables
                .Concat(source.Analysis.UpdateTables)
                .Concat(source.Analysis.DeleteTables)
                .Select(Normalize)
                .Where(t => t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> ReadSet(PolicySource source) =>
            source.Analysis.SelectTables
                .Select(Normalize)
                .Where(t => t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>스키마·DB 접두와 대괄호를 벗겨 테이블 이름만 남긴다.</summary>
        private static string Normalize(string raw) =>
            (raw ?? string.Empty).Split('.').Last().Trim('[', ']').Trim();

        /// <summary>EXEC로 부르는 프로시저 이름을 AST로 뽑는다(동적 SQL 문자열은 걸리지 않는다).</summary>
        private static IReadOnlyList<string> ExecTargets(string ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText))
            {
                return Array.Empty<string>();
            }

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null)
                {
                    return Array.Empty<string>();
                }

                var visitor = new ExecVisitor();
                fragment.Accept(visitor);
                return visitor.Targets;
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private sealed class ExecVisitor : TSqlFragmentVisitor
        {
            public List<string> Targets { get; } = new();

            public override void Visit(ExecutableProcedureReference node)
            {
                var identifiers = node.ProcedureReference?.ProcedureReference?.Name?.Identifiers;
                if (identifiers is { Count: > 0 })
                {
                    Targets.Add(string.Join(".", identifiers.Select(i => i.Value)));
                }
            }
        }
    }
}
