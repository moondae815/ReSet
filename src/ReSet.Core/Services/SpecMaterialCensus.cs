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
        IReadOnlyList<string> ObjectsWithLoss)
    {
        /// <summary>
        /// 이 회차가 접은 프로시저 수(재료 분모 절의 분모) - Job 여러 판에 걸친
        /// 같은 프로시저를 한 번으로 접은 뒤의 수다. <see cref="SpecMaterialCensus.Count"/>가
        /// 낸 모든 행에 같은 값이 실린다 - 재료별 분모가 아니라 census 전체의
        /// 분모라서다. Count()의 반환 타입을 바꾸면(예: 별도 래퍼 레코드) 이 값을
        /// 쓰는 StepSweepService.cs의 호출부(`SpecMaterialCensus.Count(input.Jobs).ToList()`)를
        /// 함께 고쳐야 하는데 그 파일은 이 태스크의 쓰기 집합 밖이다 - 그래서 기존
        /// 시그니처를 유지한 채 행마다 같은 값을 실어 나르는 쪽을 택했다.
        /// 0이면(그리고 Count가 빈 목록을 내지 않았다면) 프로시저를 하나도 못
        /// 접었다는 뜻이고, 보고서 라이터는 이 값이 0일 때 표 대신 "조사 실패"를
        /// 인쇄해야 한다 - 그러지 않으면 8개 행이 전부 "0 / 0 / 없음"으로 찍혀
        /// "쟀는데 소실이 없다"로 오독된다.
        /// </summary>
        public int FoldedProcedureCount { get; init; }

        /// <summary>
        /// 위 <see cref="FoldedProcedureCount"/>의 부분집합 - DECLARE 파싱에 실패해
        /// 소프트 페일(0)로 진행한 프로시저 수. <see cref="SpecMaterialCensus.CountDeclaredVariables"/>가
        /// 실패해도 0을 돌려주므로(AGENTS.md 범주 2), 그 0이 "변수가 없다"인지
        /// "파싱을 못 했다"인지 이 값이 없으면 구별할 수 없다. LocalVariables 하나만
        /// 재는 값이지만(다른 재료는 DDL 카운터 자체가 없다) 모든 행에 같은 값을
        /// 싣는다 - 이유는 <see cref="FoldedProcedureCount"/>와 같다.
        /// </summary>
        public int DdlParseFailureCount { get; init; }

        /// <summary>
        /// 이 Job의 명세서·DDL을 접다가 예외가 나서 census에서 통째로 건너뛴 Job
        /// 이름. StepSweepService가 이미 지키는 per-job try/catch 관용구
        /// (jobsThatThrew)를 census 자신의 루프 안으로도 내린 결과다 - 이 가드가
        /// 없으면 Job 하나의 결함이 여덟 재료 × 전체 Job의 census를 통째로 날리고
        /// 어느 Job이 던졌는지도 안 남는다. 모든 행에 같은 값이 실린다 - 이유는
        /// <see cref="FoldedProcedureCount"/>와 같다.
        /// </summary>
        public IReadOnlyList<string> JobsSkippedForFailure { get; init; } = Array.Empty<string>();
    }

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
        ///
        /// [왜 튜플을 내는가 - Fix Round 2 Important 2] 값(개수)만으로는 그 0이
        /// "DECLARE가 없다"인지 "파싱에 실패해 소프트 페일했다"인지 구별할 수 없다.
        /// CountDeclaredVariables(string?)의 공개 시그니처는 바꾸지 않는다 - Task 2가
        /// 승인·통합했고 테스트가 잠근다. 대신 내부 전용 CountDeclaredVariablesCore가
        /// 실패 여부를 out으로 더 내고, 이 딕셔너리는 그 내부 경로를 감싼다.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, Func<string?, (int Count, bool ParseFailed)>> DdlCounters =
            new Dictionary<string, Func<string?, (int, bool)>>(StringComparer.Ordinal)
            {
                ["LocalVariables"] = ddl => (CountDeclaredVariablesCore(ddl, out var failed), failed),
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

            // [Fix Round 2 Minor - per-job try/catch] StepSweepService가 이미 지키는
            // 관용구(jobsThatThrew)를 이 루프 안으로도 내린다. 이 가드가 없으면 한
            // Job의 결함(예: DdlByProcedure == null)이 이 메서드 전체를 던지게 하고,
            // 그 예외를 StepSweepService의 이음매 try/catch가 잡으면 이미 계산됐어야
            // 할 나머지 열일곱 Job의 census까지 통째로 빈 목록이 된다 - 검사가
            // 재료를 잃는 것과 정확히 같은 침묵이다. 이음매의 try/catch는 대체재가
            // 아니다 - 이 회차 이후에도 남겨 둔다(정말로 예외가 여기서 새면 여전히
            // 마지막 방어선이어야 한다).
            var jobsSkippedForFailure = new List<string>();
            foreach (var job in jobs)
            {
                try
                {
                    // [원자성 - 부분 적용 방지] job.Specs 순회는 끝까지 성공했는데
                    // job.DdlByProcedure 순회가 그다음에 던지면(예: null 컬렉션),
                    // specByProcedure/ddlByProcedure에 직접 쓰던 옛 버전은 이 Job의
                    // Spec만 절반 반영한 채로 catch에 들어간다 - "이 Job의 재료를
                    // census에서 건너뜁니다"라는 로그 문구와 실제 동작이 어긋난다.
                    // 그래서 이 Job의 몫을 임시 목록에 먼저 모으고, 둘 다 끝까지
                    // 성공한 뒤에야 공유 사전에 병합한다.
                    var jobSpecs = new List<(string Name, string Content)>();
                    foreach (var (fileName, content) in job.Specs)
                    {
                        // [실물 규약] SweepJob.Specs의 FileName은 파일 경로가 아니라
                        // 프로시저 이름("dbo.UP_X")이고 ".md" 접미사가 없다
                        // (SweepJob 문서 주석 · SweepCommand.cs:117 실측). StripExtension은
                        // 이 규약이 실물에서 한 번도 깨진 적이 없더라도 남겨 둔다 -
                        // 지우면 나중에 ".md"를 실은 호출자가 조용히 전량 미스를 낸다.
                        jobSpecs.Add((StripExtension(fileName), content));
                    }

                    var jobDdls = new List<(string Procedure, string Ddl)>();
                    foreach (var (procedure, ddl) in job.DdlByProcedure)
                    {
                        jobDdls.Add((procedure, ddl));
                    }

                    foreach (var (name, content) in jobSpecs)
                    {
                        if (!specByProcedure.ContainsKey(name)) specByProcedure[name] = content;
                    }
                    foreach (var (procedure, ddl) in jobDdls)
                    {
                        if (!ddlByProcedure.ContainsKey(procedure)) ddlByProcedure[procedure] = ddl;
                    }
                }
                catch (Exception ex)
                {
                    // 조용히 삼키지 않는다 - 로그와 함께 Job 이름을 census 결과에
                    // 실어 보고서가 인쇄하게 한다(SpecMaterialCensusRow.JobsSkippedForFailure).
                    Log.Warning(
                        ex,
                        "[SpecMaterialCensus] Job {JobName} 처리 실패 - 이 Job의 재료를 census에서 건너뜁니다.",
                        job.JobName);
                    jobsSkippedForFailure.Add(job.JobName);
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

            // [Fix Round 2 Important 2 - 파싱 실패 분모] LocalVariables의 DDL 카운터가
            // 소프트 페일(0)로 넘어간 프로시저 수. 재료별이 아니라 census 전체의
            // 분모라 모든 행에 같은 값을 싣는다(SpecMaterialCensusRow.DdlParseFailureCount
            // 문서 참고). DdlCounters에 실제로 재는 재료가 하나(LocalVariables)뿐이라
            // 아래 재료 루프를 도는 동안 그 재료를 처리할 때만 값이 늘어난다 - 같은
            // DDL 텍스트를 이중으로 파싱하지 않으려고 별도 사전 패스를 두지 않는다.
            //
            // [미결 Minor 2 - 프로시저당 한 번] 카운터를 정수 증가가 아니라
            // 프로시저 이름의 집합으로 센다. DdlCounters에 계수기가 하나뿐인
            // 오늘은 정수 증가와 값이 같지만, 둘째 DDL 계수기가 생기면 그 계수기도
            // 같은 프로시저에 대해 파싱 실패를 낼 수 있고(같은 DDL 텍스트를 각자
            // 파싱하므로 실패가 상관될 개연성이 높다) - 정수 증가는 그 경우 같은
            // 프로시저를 두 번 세어 인쇄값을 조용히 두 배로 만든다. 집합에 이름을
            // 더하는 것은(Add) 이미 있는 이름을 다시 더해도 개수가 안 늘어나므로
            // 이 결함이 구조적으로 불가능해진다.
            var ddlParseFailedProcedures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                        var (ddlCount, parseFailed) = ddlCounter!(ddl);
                        ddlFacts += ddlCount;
                        if (parseFailed) ddlParseFailedProcedures.Add(procedure);

                        if (ddlCount > 0 && specCount == 0) loss.Add(procedure);
                    }
                }

                rows.Add(new SpecMaterialCensusRow(material.Name, ddlFacts, specRows, loss));
            }

            // [왜 여기서 한 번 더 도는가] FoldedProcedureCount·DdlParseFailureCount는
            // 모든 행에 같은 값이 실려야 하는 census 전체의 분모다(레코드 문서 참고).
            // DdlParseFailureCount는 재료 루프 안에서 LocalVariables를 처리할 때만
            // 늘어나는데, LocalVariables는 SpecMaterials.All의 네 번째 항목이라 그
            // 앞에 추가된 행(DmlRows·ErrorCodeToOrdinal·SetTargets)은 루프 도중에
            // 값을 실으면 아직 최종값이 아닌 0을 갖게 된다 - 그래서 루프가 다 끝난
            // 뒤 한 번에 채운다.
            //
            // [미결 Minor 4 - 리스트 인스턴스를 공유하지 않는다] JobsSkippedForFailure는
            // "같은 값"을 모든 행에 실어야 하지만 "같은 리스트 인스턴스"를 실으면
            // 안 된다 - 호출자가 한 행의 결과를 IReadOnlyList<string>에서
            // List<string>으로 캐스팅해 고치면 여덟 행이 함께 바뀐다. 재료 루프 안에서
            // 매번 새로 만드는 loss(ObjectsWithLoss)는 이 문제가 원래 없다 - 여기서도
            // 같은 모양으로 행마다 독립된 배열을 만든다.
            return rows
                .Select(row => row with
                {
                    FoldedProcedureCount = procedures.Count,
                    DdlParseFailureCount = ddlParseFailedProcedures.Count,
                    JobsSkippedForFailure = jobsSkippedForFailure.ToArray(),
                })
                .ToList();
        }

        /// <summary>
        /// 원본 DDL이 DECLARE한 값 변수의 수. 커서 선언(DeclareCursorStatement)은
        /// 세지 않는다 - 검사 D(CheckSpecLocalVariablesDeclared)가 보는 것은 값
        /// 변수다.
        /// </summary>
        public static int CountDeclaredVariables(string? ddlText) =>
            CountDeclaredVariablesCore(ddlText, out _);

        /// <summary>
        /// <see cref="CountDeclaredVariables"/>의 내부 구현. 공개 시그니처(파싱
        /// 실패 여부를 안 내는 int 하나)는 Task 2가 승인·통합했고 테스트가
        /// 잠근다 - 바꾸지 않는다. 이 내부 경로만 실패 여부를 out으로 더 내서
        /// census의 파싱 실패 분모(SpecMaterialCensusRow.DdlParseFailureCount)를
        /// 셀 수 있게 한다.
        /// </summary>
        private static int CountDeclaredVariablesCore(string? ddlText, out bool parseFailed)
        {
            parseFailed = false;
            if (string.IsNullOrWhiteSpace(ddlText)) return 0;

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    parseFailed = true;
                    return 0;
                }

                var visitor = new DeclaredVariableVisitor();
                fragment.Accept(visitor);
                return visitor.Names.Count;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[SpecMaterialCensus] DECLARE 수집 실패 - 0으로 진행합니다.");
                parseFailed = true;
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
