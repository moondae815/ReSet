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
            var callers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                var callees = ExecTargets(source.DdlText)
                    .Where(c => sources.Any(s => s.Label.EndsWith(c, StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(s.Label, c, StringComparison.OrdinalIgnoreCase)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (callees.Count > 0)
                {
                    callers[source.Label] = callees;
                }
            }

            var execGroups = new List<List<string>>();
            foreach (var (caller, callees) in callers)
            {
                var group = new List<string> { caller };
                group.AddRange(sources
                    .Select(s => s.Label)
                    .Where(label => callees.Any(c =>
                        label.EndsWith(c, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(label, c, StringComparison.OrdinalIgnoreCase))));
                execGroups.Add(group.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }

            var placed = new HashSet<string>(producers, StringComparer.OrdinalIgnoreCase);
            foreach (var label in execGroups.SelectMany(g => g))
            {
                placed.Add(label);
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
                sb.AppendLine("<!-- [기계 확정] 아래 SP의 산출을 다른 SP가 읽습니다. 앞선다고 말할 수 있습니다. -->");
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
                sb.AppendLine("<!-- [기계 확정] 첫 SP가 나머지를 EXEC 합니다. 한 단계로 묶을 수 있습니다. -->");
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
