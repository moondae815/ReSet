using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="MaterialName">SpecMaterials.All의 재료 이름과 같다.</param>
    /// <param name="DdlFactCount">
    /// 세 상태 중 하나다 - 정수(이 회차가 실제로 셌다) 또는 null(이 회차가 안
    /// 냈다). null의 두 가지 원인 - (1) DDL 대응물 자체가 없음
    /// (<see cref="SpecMaterial.DdlCounterpart"/> == null, 「잴 수 없음」),
    /// (2) 대응물은 있으나 이 회차가 아직 그 리더를 안 만듦(「안 쟀다」) -
    /// 을 이 레코드는 구별하지 않는다. Task 4의 보고서 라이터가
    /// SpecMaterials.All에서 그 재료의 DdlCounterpart를 조회해 라벨을 가른다.
    /// 0으로 찍지 않는 이유: 빈칸은 0으로 읽히고 0은 정상으로 읽힌다.
    /// </param>
    /// <param name="SpecRowCount">
    /// 명세서 쪽 행 수. DdlFactCount와 같은 null 규약을 따른다. StepTableSets처럼
    /// "명세서 쪽 행 수"라는 개념 자체가 없는 재료(원본 DDL 정적 분석 결과를
    /// 자기 자신과 대조하는 꼴)도, 개념은 있으나 이 회차가 아직 안 세는 재료도
    /// 여기서는 똑같이 null이다.
    /// </param>
    /// <param name="ObjectsWithLoss">
    /// DdlFactCount &gt; 0인데 SpecRowCount == 0인 프로시저 이름. 개수가 아니라
    /// 이름을 싣는다 - 이름이 없으면 다음 사람이 되짚을 수 없다. 양쪽이 모두
    /// 실측(null이 아님)인 재료에서만 채워질 수 있다.
    /// </param>
    public sealed record SpecMaterialCensusRow(
        string MaterialName,
        int? DdlFactCount,
        int? SpecRowCount,
        IReadOnlyList<string> ObjectsWithLoss);

    /// <summary>
    /// 명세서 재료가 원본 DDL 대비 소실됐는지, 프로시저 단위로 센다.
    ///
    /// [카탈로그는 여덟인데 이 회차가 실제로 세는 것은 훨씬 적다] SpecMaterials.All은
    /// Task 1이 확정한 여덟 재료를 전부 싣지만, 이 계기가 양쪽(DDL 사실 · 명세서
    /// 행) 모두를 실제로 낼 수 있는 재료는 LocalVariables 하나뿐이다
    /// (<see cref="SpecCountedMaterials"/>·<see cref="DdlCountedMaterials"/> 참고).
    /// 나머지는 대응물이 없어서(DdlCounterpart == null) 잴 수 없거나, 대응물은
    /// 있지만 이 회차가 그 리더를 아직 안 만들어서 안 쟀다 - 두 경우 모두 그 값은
    /// 0이 아니라 null이다. **커버리지를 늘리는 것은 이 회차의 범위가 아니다.**
    /// 조율자 판정: 침묵을 죽이는 것(구현 안 된 자리가 0을 찍지 않게 하는 것)이
    /// 이 회차의 산출이고, 다섯 DDL 대응물을 전부 세는 것은 후속 회차의 몫이다.
    ///
    /// [SpecCounters·DdlCounters를 딕셔너리로 짠 이유] "센다"는 재료 이름 집합과
    /// 실제로 세는 switch/분기 로직을 따로 두면 둘이 어긋날 수 있다 - 집합에는
    /// 넣었는데 분기를 깜빡하면 그 재료가 조용히 0을 찍는다. 정확히 (5-3-7)의
    /// 결함 모양이다. 여기서는 딕셔너리의 키 자체가 "센다"는 집합이므로(
    /// <see cref="SpecCountedMaterials"/>·<see cref="DdlCountedMaterials"/>가 그
    /// 키에서 파생된다) 이 어긋남이 구조적으로 불가능하다.
    ///
    /// [이 계기가 못 하는 것 - 원인 귀속] DdlFactCount &gt; 0 ∧ SpecRowCount == 0
    /// 이어도 「모델이 표를 안 썼다」와 「리더가 못 읽는다」가 같은 수로 보인다.
    /// 실물이 있다 - UP_UTIL_SETTLE_SUMMARY_EXTRA는 지역 변수 표를 쓰긴 썼는데
    /// 전용 헤딩이 없어 리더가 못 읽는다(SpecStatementFactsExtractor의 알려진
    /// 한계 6번). 원인을 가르는 것은 후속 회차의 몫이다.
    ///
    /// [기록 - Task 1이 확정한 사실 둘. 이 계기의 출력 해석에 영향을 준다]
    ///   - SetTargets는 소비자가 공집합이다 - MechanicalValidator.cs 전수 grep으로
    ///     확인됐다(SpecMaterials.cs의 SetTargets 항목 주석 참고). 추출은 되는데
    ///     어느 검사도 안 쓴다. 따라서 이 재료의 소실은 급하지 않다.
    ///   - StepTableSets는 DDL 정적 분석 결과를 자기 자신과 대조하는 꼴이다 -
    ///     SpecTargetTableExtractor.Extract는 스펙 마크다운을 전혀 받지 않고
    ///     원본 DDL을 SqlStaticParser가 파싱한 결과(definition.StaticAnalysis)를
    ///     그대로 프로시저별 사전으로 접는다. "명세서 쪽"이 애초에 없으므로
    ///     소실 개념 자체가 성립하지 않는다 - 그래서 SpecRowCount가 언제나
    ///     null이다.
    /// </summary>
    public static class SpecMaterialCensus
    {
        /// <summary>
        /// 명세서 쪽 행 수를 실제로 내는 재료. 키는 SpecMaterials.All의 Name과
        /// 같아야 한다(SpecMaterialCensusTests.CountedMaterialSets_OnlyNameMaterialsThatExistInTheCatalog가
        /// 대조한다). 넷 다 SpecStatementFactsExtractor 하나가 이미 만든 레코드
        /// (SpecStatementFacts)에서 바로 꺼낸다 - 나머지 넷(SpecConditions·
        /// RoundingShapes·SpecReturnCodes·StepTableSets)은 각자 다른 리더를 새로
        /// 불러야 해서 이 회차의 범위 밖이다(결함 C).
        /// </summary>
        private static readonly IReadOnlyDictionary<string, Func<SpecStatementFacts, int>> SpecCounters =
            new Dictionary<string, Func<SpecStatementFacts, int>>(StringComparer.Ordinal)
            {
                ["DmlRows"] = f => f.DmlRows.Count,
                ["ErrorCodeToOrdinal"] = f => f.ErrorCodeToOrdinal.Count,
                ["SetTargets"] = f => f.SetTargets.Count,
                ["LocalVariables"] = f => f.LocalVariables.Count,
            };

        /// <summary>
        /// DDL 사실 수를 실제로 내는 재료. LocalVariables 하나뿐이다 - 나머지 넷
        /// (DmlRows·ErrorCodeToOrdinal·SetTargets·SpecReturnCodes)은
        /// DdlCounterpart가 null이 아니므로 「잴 수 없음」은 아니지만, 이 회차가
        /// 그 대응 리더(DmlScopeExtractor 재사용 또는 신규 배선)를 아직 안
        /// 만들었으므로 「안 쟀다」로 null을 낸다(결함 D).
        /// </summary>
        private static readonly IReadOnlyDictionary<string, Func<string?, int>> DdlCounters =
            new Dictionary<string, Func<string?, int>>(StringComparer.Ordinal)
            {
                ["LocalVariables"] = CountDeclaredVariables,
            };

        /// <summary>이 회차가 명세서 쪽 행 수를 실제로 내는 재료 이름의 목록. 테스트용 노출.</summary>
        public static IReadOnlyList<string> SpecCountedMaterials { get; } =
            SpecCounters.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

        /// <summary>이 회차가 DDL 사실 수를 실제로 내는 재료 이름의 목록. 테스트용 노출.</summary>
        public static IReadOnlyList<string> DdlCountedMaterials { get; } =
            DdlCounters.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

        public static IReadOnlyList<SpecMaterialCensusRow> Count(IReadOnlyList<SweepJob>? jobs)
        {
            var rows = new List<SpecMaterialCensusRow>();
            if (jobs == null) return rows;

            // [판 접기] 같은 원본 SP가 최대 다섯 판에 나온다. 프로시저 이름으로
            // 접지 않으면 같은 소실이 다섯 번 세어져 수가 통째로 왜곡된다 -
            // 태스크 12에서 실제로 밟은 함정이다.
            var specByProcedure = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ddlByProcedure = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in jobs)
            {
                foreach (var (fileName, content) in job.Specs)
                {
                    // [실물 규약] SweepJob.Specs의 FileName은 파일 경로가 아니라
                    // 프로시저 이름("dbo.UP_X")이고 ".md" 접미사가 없다
                    // (SweepJob 문서 주석 · SweepCommand.cs:117 실측). StripExtension은
                    // 이 규약이 실물에서 한 번도 깨진 적이 없더라도 남겨 둔다 -
                    // 지우면 나중에 ".md"를 실은 호출자가 조용히 전량 미스를 낸다.
                    var name = StripExtension(fileName);
                    if (!specByProcedure.ContainsKey(name)) specByProcedure[name] = content;
                }
                foreach (var (procedure, ddl) in job.DdlByProcedure)
                {
                    if (!ddlByProcedure.ContainsKey(procedure)) ddlByProcedure[procedure] = ddl;
                }
            }

            var specs = specByProcedure
                .Select(kv => (FileName: kv.Key, Content: kv.Value))
                .ToList();
            // [키 규약 - 결함 B] SpecStatementFactsExtractor.Extract는
            // MechanicalValidator.BareObjectName(FileName)으로 결과 딕셔너리의
            // 키를 만든다(SpecStatementFactsExtractor.cs:164 - 스키마 접두사 없는
            // 참조와 규약을 맞추기 위해서다). specByProcedure/ddlByProcedure의
            // 키는 원문 프로시저 이름("dbo.UP_X")이므로, 아래에서 facts를 조회할
            // 때도 같은 BareObjectName을 거쳐야 한다 - 안 거치면 "dbo.UP_X"로
            // 조회해 전건 미스가 나고 모든 프로시저가 소실로 보고된다.
            var facts = SpecStatementFactsExtractor.Extract(specs);

            var procedures = specByProcedure.Keys
                .Union(ddlByProcedure.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            foreach (var material in SpecMaterials.All)
            {
                var specCounted = SpecCounters.TryGetValue(material.Name, out var specCounter);
                var ddlCounted = DdlCounters.TryGetValue(material.Name, out var ddlCounter);

                int? specRows = specCounted ? 0 : null;
                int? ddlFacts = ddlCounted ? 0 : null;
                var loss = new List<string>();

                foreach (var procedure in procedures)
                {
                    int? specCount = null;
                    if (specCounted)
                    {
                        var key = MechanicalValidator.BareObjectName(procedure);
                        specCount = facts.TryGetValue(key, out var f) ? specCounter!(f) : 0;
                        specRows += specCount.Value;
                    }

                    if (ddlCounted)
                    {
                        ddlByProcedure.TryGetValue(procedure, out var ddl);
                        var ddlCount = ddlCounter!(ddl);
                        ddlFacts += ddlCount;

                        if (ddlCount > 0 && specCount == 0) loss.Add(procedure);
                    }
                }

                rows.Add(new SpecMaterialCensusRow(material.Name, ddlFacts, specRows, loss));
            }

            return rows;
        }

        /// <summary>
        /// 원본 DDL이 DECLARE한 값 변수의 수. 커서 선언(DeclareCursorStatement)은
        /// 세지 않는다 - 검사 D(CheckSpecLocalVariablesDeclared)가 보는 것은 값
        /// 변수다.
        /// </summary>
        public static int CountDeclaredVariables(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return 0;

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0)) return 0;

                var visitor = new DeclaredVariableVisitor();
                fragment.Accept(visitor);
                return visitor.Names.Count;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[SpecMaterialCensus] DECLARE 수집 실패 - 0으로 진행합니다.");
                return 0;
            }
        }

        private static string StripExtension(string fileName) =>
            fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^3]
                : fileName;

        private sealed class DeclaredVariableVisitor : TSqlFragmentVisitor
        {
            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

            // [Visit인가 ExplicitVisit인가] 이 저장소에 둘 다 전례가 있다 -
            // SqlStaticParser.cs:1313은 ExplicitVisit(DeclareVariableElement),
            // ExpressionTypePathExtractor.cs:149는 Visit(DeclareVariableElement)다.
            // 여기서는 DeclareVariableElement 아래로 더 내려갈 하위 트리(변수
            // 초기값 식 등)를 볼 필요가 없어 하강을 막을 이유가 없으므로 Visit을
            // 쓴다 - ExplicitVisit은 기반 방문을 스스로 호출해야 하는 대신
            // 하강을 끊을 수 있다는 차이만 있다.
            public override void Visit(DeclareVariableElement node)
            {
                var name = node.VariableName?.Value;
                if (!string.IsNullOrWhiteSpace(name)) Names.Add(name!);
            }
        }
    }
}
