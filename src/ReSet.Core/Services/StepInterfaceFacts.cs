using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <param name="Parameters">원본 선언 그대로. "@pi_strYMD varchar(8)" 형태다.</param>
    /// <param name="Guards">원본 DDL 의 <c>IF [NOT] EXISTS (SELECT … FROM T WHERE …)</c> 가드. DDL 이 없으면 null.</param>
    public sealed record StepInterface(
        string StepCode,
        IReadOnlyList<string> Procedures,
        IReadOnlyList<string> Parameters,
        IReadOnlyList<StepGuard>? Guards = null,
        IReadOnlyList<StepJoinPairs>? JoinPairs = null);

    /// <summary>
    /// 원본 한 문장의 조인 짝. <paramref name="Pairs"/> 는 검사 N5 가 쓰는 정규형(<c>TableA.Col=TableB.Col</c>)이고,
    /// 문장은 <c>(종류, 서수, 대상)</c> 으로 가리킨다 - 서수의 유일한 출처는 <c>DmlScopeExtractor.BuildStatementOrdinals</c> 다.
    /// </summary>
    public sealed record StepJoinPairs(string Kind, int Ordinal, string Target, IReadOnlyList<string> Pairs);

    /// <summary>
    /// 원본 가드 한 줄. <paramref name="Where"/> 는 WHERE 최상위 항의 원문을 AND 로 이은 것(공백 접힘),
    /// <paramref name="SelectColumns"/> 는 EXISTS 가 투영하는 목록일 뿐 조건이 아니다.
    /// </summary>
    /// <param name="Negated"><c>IF NOT EXISTS</c> 면 true - 같은 WHERE 라도 뜻이 반대라 표가 행마다 싣는다.</param>
    /// <param name="FirstBeginTranLine">
    /// 원본의 <b>첫</b> <c>BEGIN TRANSACTION</c> 줄. 이 값보다 <see cref="Line"/> 이 앞이면 원본은 그 가드를
    /// <b>트랜잭션 없이</b> 평가하고 그대로 반환했다는 뜻이다. 원본이 트랜잭션을 아예 안 열면 <c>null</c>(모름) -
    /// 0 이나 <c>int.MaxValue</c> 를 넣으면 표가 거짓을 적는다.
    ///
    /// [왜 이 칸이 필요한가] 재료는 이미 프롬프트에 둘 다 있었다 - 명세서의 「트랜잭션 경계 (기계 확정)」 표와
    /// 이 가드 표. 없던 것은 <b>두 표를 줄 번호로 맞춰 보라는 조항</b>이고, 그래서 B21 의 S10·S11 이 원본과 달리
    /// 트랜잭션을 먼저 열고 가드에서 롤백했다. 요청 재생 실험에서 이 칸을 주자 양성 3/3 이 고쳐지고
    /// 음성(가드 둘 중 하나는 트랜잭션 안이 옳은 S05)은 오탐 0 이었다.
    /// 판독: docs/audit-reports/2026-09-18-가드-트랜잭션-순서-판독.md
    /// </param>
    public sealed record StepGuard(string Procedure, int Line, string Table, string Where, IReadOnlyList<string> SelectColumns, bool Negated = false, int? FirstBeginTranLine = null);

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

        /// <param name="ddlByProcedure">
        /// [가드 조건 - 2026-09-14] 주면 단계의 원본 프로시저 DDL 에서 가드를 뽑아 <see cref="StepInterface.Guards"/> 에 싣는다.
        /// 단계 프롬프트에 원본 DDL 이 없어(<c>Narrow</c>) GPT 가 명세서 CRUD 참조 컬럼 칸의 SELECT 목록 PLTID 를 가드 조건으로 옮겼고,
        /// 같은 요청 재생에서 이 조건을 알려 주면 날조가 0/3 이었다(그대로 2/3). 판독 docs/audit-reports/2026-09-14-가드PLTID-날조-원인-측정.md
        /// </param>
        public static IReadOnlyList<StepInterface> Build(
            IReadOnlyList<BatchStepPlan>? steps,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? parametersByProcedure,
            IReadOnlyDictionary<string, string>? ddlByProcedure = null)
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
                var guards = new List<StepGuard>();
                var joinPairs = new List<StepJoinPairs>();

                foreach (var legacy in step.LegacyProcedures ?? (IReadOnlyList<string>)Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(legacy)) continue;

                    if (!parametersByProcedure.TryGetValue(legacy, out var declared) &&
                        !parametersByProcedure.TryGetValue(BareName(legacy), out declared))
                    {
                        continue;
                    }

                    procedures.Add(legacy);
                    if (ddlByProcedure != null &&
                        (ddlByProcedure.TryGetValue(legacy, out var ddl) || ddlByProcedure.TryGetValue(BareName(legacy), out ddl)))
                    {
                        // 첫 트랜잭션 개시 줄. 값은 제품 추출기에서만 온다 - 여기서 DDL 을 다시 훑으면
                        // 같은 사실에 두 권위가 생기고, 그 어긋남은 표 어디에도 드러나지 않는다.
                        var firstBeginTran = TransactionBoundaryExtractor.Extract(ddl)
                            .Where(b => b.Kind.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
                            .Select(b => (int?)b.Line)
                            .DefaultIfEmpty(null)
                            .Min();

                        foreach (var guard in GuardPredicateFacts.GuardsFromDdl(ddl))
                        {
                            guards.Add(new StepGuard(
                                legacy, guard.Line, guard.Table,
                                System.Text.RegularExpressions.Regex.Replace(string.Join(" AND ", guard.Terms.Select(t => t.Raw)), @"\s+", " ").Trim(),
                                guard.SelectColumns,
                                guard.Negated,
                                firstBeginTran));
                        }
                    }

                    foreach (var p in declared)
                    {
                        if (!parameters.Contains(p, StringComparer.OrdinalIgnoreCase))
                        {
                            parameters.Add(p);
                        }
                    }
                }

                // [조인 짝] 검사 N5 가 쓰는 재료를 그대로 읽는다 - 서수·대상 키 규약을 두 벌 두면 조용히 갈린다
                // (BuildOriginalJoinPairs 주석). 명세서 「조인 키」 칸은 컬럼 이름만 담아 어느 테이블끼리의 짝인지를
                // 말하지 않고, GPT 판 다섯이 그 자리에서 짝을 지어냈다(EXPECT_PROC UPDATE 11 에 A.ClientID = B.ClientID).
                // 판독: docs/audit-reports/2026-09-16-조인짝-프롬프트-사전선언.md
                if (ddlByProcedure is { Count: > 0 })
                {
                    foreach (var ((kind, ordinal, target), pairs) in MechanicalValidator.BuildOriginalJoinPairs(step, ddlByProcedure)
                                 .OrderBy(e => e.Key.Kind, StringComparer.Ordinal)
                                 .ThenBy(e => e.Key.Ordinal))
                    {
                        if (pairs.Count == 0) continue;
                        joinPairs.Add(new StepJoinPairs(kind, ordinal, target, pairs));
                    }
                }

                if (parameters.Count > 0)
                {
                    result.Add(new StepInterface(
                        step.Code, procedures, parameters,
                        guards.Count > 0 ? guards : null,
                        joinPairs.Count > 0 ? joinPairs : null));
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

        /// <summary>
        /// 원본 가드 표. 인터페이스 표와 같은 이유로 **전 단계** 것을 통째로 싣는다(공유 접두사 캐시). 가드가 없으면 빈 문자열 - 절을 싣지 않는다.
        /// </summary>
        public static string RenderGuardTable(IReadOnlyList<StepInterface> interfaces)
        {
            var rows = (interfaces ?? Array.Empty<StepInterface>())
                .SelectMany(i => (i.Guards ?? Array.Empty<StepGuard>()).Select(g => (i.StepCode, Guard: g)))
                .ToList();
            if (rows.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("| Step | Legacy procedure | DDL line | Check | Table | WHERE conditions (exact) | SELECT list (not a condition) | Original first BEGIN TRAN (DDL line) | Guard is outside the transaction |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var (code, guard) in rows)
            {
                var (tranLine, outside) = guard.FirstBeginTranLine is int first
                    ? (first.ToString(), guard.Line < first ? "YES" : "NO")
                    : ("-", "UNKNOWN");
                sb.AppendLine(
                    $"| {code} | {guard.Procedure} | {guard.Line} | {(guard.Negated ? "IF NOT EXISTS" : "IF EXISTS")} | {guard.Table} | `{guard.Where}` | " +
                    $"{(guard.SelectColumns.Count > 0 ? string.Join(", ", guard.SelectColumns.Select(c => "`" + c + "`")) : "-")} | {tranLine} | {outside} |");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 원본 조인 짝 표. 가드 표와 같은 이유로 **전 단계** 것을 통째로 싣는다(공유 접두사 캐시 — 단계마다 갈리면 단계 수만큼
        /// 캐시 미스가 난다). 짝이 없으면 빈 문자열 - 절을 싣지 않는다.
        ///
        /// [빈 짝은 행을 만들지 않는다] 짝이 0 인 문장에 빈 칸 행을 내면 그 행이 「이 문장에는 조인이 없다」는 <b>거짓 사실</b>이 된다 —
        /// 추출기는 문장의 최상위 FROM/JOIN 만 읽어, 파생 테이블·UNION 갈래 안의 조인은 못 본다(코퍼스 실측: INSERT 21/21 ·
        /// SELECT 10/10 이 짝 0). 그래서 <see cref="Build"/> 가 빈 짝을 아예 담지 않고, 프롬프트 머리글이 「표에 없는 문장이
        /// 조인 없는 문장은 아니다」를 밝힌다.
        /// </summary>
        public static string RenderJoinPairTable(IReadOnlyList<StepInterface> interfaces)
        {
            var rows = (interfaces ?? Array.Empty<StepInterface>())
                .SelectMany(i => (i.JoinPairs ?? Array.Empty<StepJoinPairs>()).Select(j => (i.StepCode, Join: j)))
                .ToList();
            if (rows.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("| Step | Statement | Target | Join pairs (exact) |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var (code, join) in rows)
            {
                sb.AppendLine($"| {code} | {join.Kind} {join.Ordinal} | {join.Target} | `{string.Join(" AND ", join.Pairs)}` |");
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
