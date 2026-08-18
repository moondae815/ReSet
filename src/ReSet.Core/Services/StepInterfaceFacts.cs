using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <param name="Parameters">원본 선언 그대로. "@pi_strYMD varchar(8)" 형태다.</param>
    public sealed record StepInterface(
        string StepCode,
        IReadOnlyList<string> Procedures,
        IReadOnlyList<string> Parameters);

    /// <summary>
    /// 단계별 원본 프로시저 인터페이스를 모은다.
    ///
    /// [새 추출기를 만들지 않는 이유]
    /// SqlStaticParser가 ProcedureParameters로 이미 확정하고 있다. 문제는 이 사실이
    /// Job 단계 프롬프트에 실리지 않는다는 것뿐이었다 - AppendSharedStepContext는
    /// jobName·targetLanguage·specs·conventions만 날랐다. 18번의 호출이 원본
    /// 인터페이스에 대한 기계 사실을 하나도 못 받은 채, ConsolidatedPlanRules 규칙 5는
    /// "@pi_bypassPreCheck 파라미터를 제공하라"고 명령했다. 산출물이 원본에 없는 입력을
    /// 지어낸 것이 아니라 프롬프트가 그 이름까지 적어 시켰다.
    ///
    /// [조달을 둘로 가르는 이유]
    /// 오케스트레이터에서 definitions가 있는 지점에는 steps가 아직 없고, steps가 있는
    /// 지점에는 definitions가 없다. CollectParameters가 knownTableNames와 같은 자리에서
    /// 돌아 아래로 실려 내려가고, Build는 steps가 있는 곳에서 돈다.
    ///
    /// [파라미터가 없는 프로시저를 담지 않는 이유]
    /// 정적 분석이 실패했거나 파라미터가 없으면 재료가 없는 것이다. 빈 목록을 사실로
    /// 내보내면 검사가 그 단계의 모든 파라미터를 결함으로 든다. 담지 않으면 소프트 스킵한다.
    /// </summary>
    public static class StepInterfaceFacts
    {
        /// <summary>프로시저 맨이름과 한정명 양쪽으로 찾을 수 있게 담는다.</summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> CollectParameters(
            IReadOnlyList<SpDefinition>? definitions)
        {
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (definitions == null) return map;

            foreach (var def in definitions)
            {
                if (def?.Name == null) continue;

                var declared = def.StaticAnalysis.ProcedureParameters;
                if (declared.Count == 0) continue;

                var snapshot = declared.ToList();
                map[def.Name] = snapshot;
                map[$"{def.Schema}.{def.Name}"] = snapshot;
            }

            return map;
        }

        public static IReadOnlyList<StepInterface> Build(
            IReadOnlyList<BatchStepPlan>? steps,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? parametersByProcedure)
        {
            if (steps == null || steps.Count == 0 ||
                parametersByProcedure == null || parametersByProcedure.Count == 0)
            {
                return Array.Empty<StepInterface>();
            }

            var result = new List<StepInterface>();

            foreach (var step in steps)
            {
                var procedures = new List<string>();
                var parameters = new List<string>();

                foreach (var legacy in step.LegacyProcedures ?? (IReadOnlyList<string>)Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(legacy)) continue;

                    if (!parametersByProcedure.TryGetValue(legacy, out var declared) &&
                        !parametersByProcedure.TryGetValue(BareName(legacy), out declared))
                    {
                        continue;
                    }

                    procedures.Add(legacy);
                    foreach (var p in declared)
                    {
                        if (!parameters.Contains(p, StringComparer.OrdinalIgnoreCase))
                        {
                            parameters.Add(p);
                        }
                    }
                }

                if (parameters.Count > 0)
                {
                    result.Add(new StepInterface(step.Code, procedures, parameters));
                }
            }

            return result;
        }

        /// <summary>"@pi_strYMD varchar(8)" -&gt; "@pi_strYMD".</summary>
        public static IReadOnlyList<string> ParameterNames(StepInterface iface)
        {
            var names = new List<string>();
            foreach (var declaration in iface.Parameters)
            {
                var trimmed = declaration.Trim();
                var space = trimmed.IndexOf(' ');
                names.Add(space > 0 ? trimmed[..space] : trimmed);
            }
            return names;
        }

        /// <summary>
        /// 단계 프롬프트가 실을 표.
        ///
        /// 어느 단계를 생성하든 전 단계 표를 통째로 싣는다. 단계별로 자기 것만
        /// 실으면 공유 접두사가 매 호출 달라져 프롬프트 캐시가 전부 미스가 되고,
        /// 입력 토큰이 1배에서 18배로 뛴다 - 산출물은 그대로라 코드만 봐서는
        /// 알 수 없는 실패다(architecture.md §4.13).
        /// </summary>
        public static string RenderPromptTable(IReadOnlyList<StepInterface> interfaces)
        {
            if (interfaces == null || interfaces.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("| Step | Legacy procedure | Parameters (this list is exhaustive) |");
            sb.AppendLine("|---|---|---|");

            foreach (var iface in interfaces)
            {
                sb.AppendLine(
                    $"| {iface.StepCode} | {string.Join(", ", iface.Procedures)} | " +
                    $"{string.Join(" · ", iface.Parameters)} |");
            }

            return sb.ToString();
        }

        private static string BareName(string name)
        {
            var idx = name.LastIndexOf('.');
            return idx >= 0 ? name[(idx + 1)..] : name;
        }
    }
}
