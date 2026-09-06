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
        /// <summary>
        /// 미지 테이블 검사(<see cref="MechanicalValidator.ValidateBatchStep"/>)가 쓰는
        /// 스키마 카탈로그를 만든다. 담는 것은 둘이다 - 정적 분석이 확정한 <b>의존
        /// 대상</b>과, 이 Job이 대체하는 <b>원본 프로시저 자신</b>.
        ///
        /// [원본 자신을 왜 담는가 - 2026-08-29 ① 전수 분류]
        /// 의존 대상만 담으면 원본 SP가 카탈로그 밖에 남아, 단계가 "S04가 대체하는
        /// `dbo.UP_UTIL_SETTLE_INS`의 후속이다"라고 적을 때 그 이름이 유령으로
        /// 고발됐다. 실측: 계획서 20편·359단계에서 미지 테이블 발화 219건 중
        /// <b>29건(12개 이름)이 전부 이것</b>이었고, 원본 SP 목록을 나열하는
        /// 오케스트레이터 단계를 둔 POQSettleProc2·POQSettleProc6 두 편에 몰려 있었다.
        /// 이 재료를 더해 219 → 190이 됐고 다른 검사 카운트는 하나도 변하지 않았다.
        ///
        /// <see cref="MechanicalValidator"/> 쪽의 `step.LegacyProcedures` 화이트리스트로는
        /// 닫히지 않는다 - 그 칸은 목차가 채우는데 실측상 비어 있다(Proc6은 33단계
        /// 전부 빈 칸, Proc2는 18단계에 3개뿐). 목차의 선언이 아니라 정적 분석의
        /// 로스터를 근거로 삼는 이유가 그것이다.
        ///
        /// 비면 빈 목록을 낸다 - 호출부가 그때 검사를 건너뛴다(소프트 스킵). 카탈로그가
        /// 없다는 사실을 "모든 테이블이 유령이다"라는 판정으로 바꾸지 않기 위해서다.
        /// </summary>
        public static IReadOnlyList<string> CollectSchemaCatalog(
            IReadOnlyList<SpDefinition>? definitions)
        {
            var defs = definitions ?? Array.Empty<SpDefinition>();

            return defs
                .SelectMany(sp => sp.Dependencies)
                .Select(dep => string.IsNullOrEmpty(dep.Database)
                    ? $"{dep.Schema}.{dep.Name}"
                    : $"{dep.Database}.{dep.Schema}.{dep.Name}")
                .Concat(defs
                    .Select(sp => $"{sp.Schema}.{sp.Name}")
                    .Where(name => !string.IsNullOrWhiteSpace(name) && name.Trim('.').Length > 0))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 프로시저 맨이름과 한정명 양쪽으로 찾을 수 있게 담는다.
        ///
        /// [맨이름이 모호해지면 빼는 이유]
        /// 스키마가 다른 동명 프로시저가 있으면(예: dbo.UP_FOO와 archive.UP_FOO) 맨이름
        /// "UP_FOO"이 어느 쪽을 가리키는지 이 함수는 결정할 근거가 없다. 마지막으로 처리한
        /// 정의로 조용히 덮어쓰면 Build가 엉뚱한 SP의 파라미터를 단계에 붙일 수 있다 -
        /// 이 계획이 막으려는 "재료가 틀린 사실을 낸다"는 바로 그 실패다. 한정명 키는
        /// 모호하지 않으므로 그대로 남기고, 맨이름 키만 빼서 그 경로로의 조회가
        /// 실패하게 한다 - Build가 소프트 스킵한다(계획서 §Global Constraints와 같은 판단).
        /// 같은 스키마·같은 이름이 두 번 들어오는 것은 같은 프로시저의 재확인일 뿐이라
        /// 충돌로 치지 않는다.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> CollectParameters(
            IReadOnlyList<SpDefinition>? definitions)
        {
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (definitions == null) return map;

            // 맨이름별로 지금까지 관측한 서로 다른 한정명 집합을 추적한다.
            // 두 번째로 다른 한정명이 나타나는 순간 그 맨이름은 영구히 모호해진다.
            var bareNameOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var ambiguousBareNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in definitions)
            {
                if (def?.Name == null) continue;

                var declared = def.StaticAnalysis.ProcedureParameters;
                if (declared.Count == 0) continue;

                var snapshot = declared.ToList();
                var qualifiedName = $"{def.Schema}.{def.Name}";
                map[qualifiedName] = snapshot;

                if (ambiguousBareNames.Contains(def.Name)) continue;

                if (!bareNameOwners.TryGetValue(def.Name, out var owners))
                {
                    owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bareNameOwners[def.Name] = owners;
                }
                owners.Add(qualifiedName);

                if (owners.Count > 1)
                {
                    ambiguousBareNames.Add(def.Name);
                    map.Remove(def.Name);
                    continue;
                }

                map[def.Name] = snapshot;
            }

            return map;
        }

        /// <summary>
        /// 프로시저별 원본 DDL 원문. 조인 짝 대조(N5)가 <b>검사 때</b> 여기서 재파생한다.
        ///
        /// [왜 명세서가 아니라 DDL 인가] 조인 짝은 명세서에 실린 적이 없다. 실을 수도
        /// 있었으나 <b>재생성이 재료를 지운 전례</b>가 셋 있어 기각했다 - 이 값의 출처인
        /// <c>raw/metadata.json</c>의 <c>DdlText</c>는 불변 입력이라 지워지지 않는다.
        /// 결정은 <c>docs/audit-reports/2026-09-05-축B-잔여결함-분류.md</c> §10.
        ///
        /// [<see cref="CollectParameters"/>와 같은 키잉·같은 모호성 규칙] 맨이름이 서로
        /// 다른 한정명 둘을 가리키면 그 맨이름을 통째로 뺀다. 사본을 두면 한쪽만
        /// 고쳐져 조회가 조용히 갈린다.
        /// </summary>
        public static IReadOnlyDictionary<string, string> CollectDdl(
            IReadOnlyList<SpDefinition>? definitions)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (definitions == null) return map;

            var bareNameOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var ambiguousBareNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in definitions)
            {
                if (def?.Name == null) continue;
                if (string.IsNullOrWhiteSpace(def.DdlText)) continue;

                var qualifiedName = $"{def.Schema}.{def.Name}";
                map[qualifiedName] = def.DdlText;

                if (ambiguousBareNames.Contains(def.Name)) continue;

                if (!bareNameOwners.TryGetValue(def.Name, out var owners))
                {
                    owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bareNameOwners[def.Name] = owners;
                }
                owners.Add(qualifiedName);

                if (owners.Count > 1)
                {
                    ambiguousBareNames.Add(def.Name);
                    map.Remove(def.Name);
                    continue;
                }

                map[def.Name] = def.DdlText;
            }

            return map;
        }

        /// <summary>
        /// 프로시저 자신이 호출하는 다른 코드 객체(프로시저·함수)의 그래프.
        /// <see cref="PromptContextScope.NarrowSpecs"/>의 1-hop 이웃 판정 재료다.
        ///
        /// [테이블을 빼는 이유]
        /// Dependencies에는 테이블도 섞여 있다(<see cref="SqlObjectTypeClassifier.IsCodeObject"/>로
        /// 가른다). NarrowSpecs가 찾는 "이웃"은 명세서를 가진 대상뿐이고, 명세서는
        /// 프로시저·함수에만 있다 - 테이블을 넣어도 매칭될 명세서가 없어 순수 잡음이다.
        ///
        /// [호출이 없는 프로시저를 담지 않는 이유]
        /// CollectParameters와 같은 소프트 스킵 관례다. 빈 목록을 사실로 내보내면
        /// "이 프로시저는 아무것도 호출하지 않는다"는 확정된 사실처럼 읽히는데,
        /// 실제로는 "찾지 못했다"와 구분되지 않는다.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildCallGraph(
            IReadOnlyList<SpDefinition>? definitions)
        {
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (definitions == null) return map;

            foreach (var def in definitions)
            {
                if (def?.Name == null) continue;

                var callees = def.Dependencies
                    .Where(dep => SqlObjectTypeClassifier.IsCodeObject(dep.Type))
                    .Select(dep => $"{dep.Schema}.{dep.Name}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (callees.Count == 0) continue;

                map[$"{def.Schema}.{def.Name}"] = callees;
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
