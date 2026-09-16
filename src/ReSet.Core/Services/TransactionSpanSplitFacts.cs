using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 원본의 한 트랜잭션 안에서 실행되던 피호출자가 이행에서 <b>다른 단계</b>로 갈린 자리 하나.
    /// </summary>
    /// <param name="CallerStepCode">호출자 프로시저를 맡은 단계.</param>
    /// <param name="CallerProcedure">호출자 프로시저 맨이름.</param>
    /// <param name="SpanFrom">호출자 DDL 의 <c>BEGIN TRAN</c> 줄.</param>
    /// <param name="SpanTo">호출자 DDL 의 <c>COMMIT TRAN</c> 줄.</param>
    /// <param name="CalleeProcedure">그 구간 안에서 실행되던 피호출자 맨이름.</param>
    /// <param name="CalleeStepCode">피호출자를 맡은 다른 단계.</param>
    public sealed record TransactionSpanSplit(
        string CallerStepCode,
        string CallerProcedure,
        int SpanFrom,
        int SpanTo,
        string CalleeProcedure,
        string CalleeStepCode);

    /// <summary>
    /// 원본의 <b>한 트랜잭션 안에 있던 일</b>이 이행에서 <b>서로 다른 단계</b>로 갈린 것을 목차와 원본 DDL 로만 찾는다
    /// (축 B 잔여 결함 <b>N6</b> = Batch5 가족 <b>F2</b>).
    ///
    /// [실물 - POQSettleBatch1·4·5 에서 3/3 재현]
    /// <code>
    /// 원본  UP_Util_Settle_Summary : BEGIN TRAN(23) … EXEC AcqManual(221) … EXEC SUMMARY_EXTRA(230) … COMMIT TRAN(239)
    /// 이행  S14 = Summary           S15 = AcqManual          S16 = SUMMARY_EXTRA
    /// </code>
    /// 피호출자 둘은 <b>자기 DDL 에 트랜잭션이 없다</b> - 언제나 호출자의 트랜잭션 안에서 돌았기 때문이다.
    ///
    /// [이것은 결함 판정이 아니다 - 2026-09-16]
    /// 종전에는 <c>MechanicalValidator.CheckTransactionSpanSplit</c> 이 이 사실을 단계 하한 검사의 목차 결함
    /// (<c>PlanDefects</c>)으로 들어 「검증 불가」 배너로 배송했다. 그러나 이 재료는 <b>목차만</b> 안다 —
    /// 섹션이 단계를 넘어 한 트랜잭션을 공유하면 원자성은 보존된다. 배송 다섯 판(B11·B12·B13·B15·B16)을
    /// 섹션 줄 단위로 판독한 결과 <b>5/5 가 보존</b>이었고 배너는 5/5 거짓이었다
    /// (docs/audit-reports/2026-09-14-GPT판-B11-B16-교차분류-사전선언.md §후속 1 결과).
    /// 보존 여부는 의사코드·산문을 읽어야 알 수 있고 그 판독은 실측 오탐 15 중 14 였다(같은 검사의 옛 주석).
    /// 그래서 사람 결정으로(2026-09-16) 판정은 Critic 에 넘기고, 이 클래스는 <b>확인 요청 재료</b>만 낸다 —
    /// docs/audit-reports/2026-09-16-트랜잭션분할-Critic확인항목-사전선언.md
    ///
    /// [오라클은 원본 DDL 이다 - 비순환]
    /// 명세서의 「트랜잭션 경계」 표도 같은 사실을 담지만 그것은 <b>모델이 전사한 것</b>이라, 재생성이 그 표를
    /// 지우면 재료가 조용해진다(검사 D 가 18 → 0 으로 꺼진 그 실패 모드다).
    ///
    /// [침묵의 범위 - 넷 중 하나라도 아니면 내지 않는다]
    /// ① 호출자 DDL 이 단일 <c>BEGIN…COMMIT</c> 이 아니다(귀속 불가) ② 그 구간 <b>밖</b>의 호출이다(원자성이
    /// 애초에 없었다) ③ 피호출자가 자기 트랜잭션을 갖거나 DML 이 없다(갈라도 잃을 것이 없다) ④ 피호출자가 같은
    /// 단계이거나 단계가 아니다(보존됐다). 코퍼스 14 편 중 <b>12 편이 ③ 에서 걸러진다</b> - 자기 트랜잭션이
    /// 없는 것은 <c>Summary_AcqManual</c>·<c>SUMMARY_EXTRA</c> 둘뿐이다.
    /// </summary>
    public static class TransactionSpanSplitFacts
    {
        /// <summary>목차와 원본 DDL 로 갈린 자리를 찾는다(목차 순서, 호출자 단계 → 호출 줄 순서).</summary>
        public static IReadOnlyList<TransactionSpanSplit> Find(
            IReadOnlyList<BatchStepPlan>? steps,
            IReadOnlyDictionary<string, string>? ddlByProcedure)
        {
            var found = new List<TransactionSpanSplit>();
            if (steps == null || steps.Count == 0) return found;
            if (ddlByProcedure == null || ddlByProcedure.Count == 0) return found;

            foreach (var step in steps)
            {
                foreach (var procedure in step.LegacyProcedures ?? Array.Empty<string>())
                {
                    var callerBare = MechanicalValidator.BareObjectName(procedure);
                    if (!ddlByProcedure.TryGetValue(callerBare, out var callerDdl) ||
                        string.IsNullOrWhiteSpace(callerDdl))
                    {
                        continue;
                    }

                    var boundaries = TransactionBoundaryExtractor.Extract(callerDdl);
                    var begins = boundaries.Where(b => b.Kind == "BEGIN TRANSACTION").ToList();
                    var commits = boundaries.Where(b => b.Kind == "COMMIT TRANSACTION").ToList();

                    // 단일 구간일 때만 "그 안"을 말할 수 있다. 둘 이상이면 어느 EXEC 가 어느
                    // 트랜잭션에 속하는지가 중첩·분기에 따라 갈려 귀속이 틀리기 쉽다.
                    if (begins.Count != 1 || commits.Count != 1) continue;
                    var spanFrom = begins[0].Line;
                    var spanTo = commits[0].Line;
                    if (spanTo <= spanFrom) continue;

                    foreach (var call in ProcedureCallLines(callerDdl))
                    {
                        if (call.Line < spanFrom || call.Line > spanTo) continue;

                        var calleeBare = MechanicalValidator.BareObjectName(call.Name);
                        if (string.Equals(calleeBare, callerBare, StringComparison.OrdinalIgnoreCase)) continue;

                        // 피호출자 DDL 이 없으면 ③ 을 판정할 수 없다 - 내지 않는다.
                        if (!ddlByProcedure.TryGetValue(calleeBare, out var calleeDdl) ||
                            string.IsNullOrWhiteSpace(calleeDdl))
                        {
                            continue;
                        }

                        // ③ 자기 트랜잭션을 가졌으면 갈라도 원자성이 안 바뀐다.
                        if (TransactionBoundaryExtractor.Extract(calleeDdl)
                            .Any(b => b.Kind == "BEGIN TRANSACTION"))
                        {
                            continue;
                        }

                        // ③ 쓰기가 없으면 잃을 원자성이 없다.
                        if (DmlScopeExtractor.Extract(calleeDdl, string.Empty).Count == 0) continue;

                        // ④ 별도 단계로 승격됐는가.
                        var calleeStep = steps.FirstOrDefault(s => (s.LegacyProcedures ?? Array.Empty<string>())
                            .Any(p => string.Equals(MechanicalValidator.BareObjectName(p), calleeBare, StringComparison.OrdinalIgnoreCase)));
                        if (calleeStep == null) continue;
                        if (string.Equals(calleeStep.Code, step.Code, StringComparison.OrdinalIgnoreCase)) continue;

                        found.Add(new TransactionSpanSplit(
                            step.Code, callerBare, spanFrom, spanTo, calleeBare, calleeStep.Code));
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Critic 에게 넘길 확인 요청 문장. <b>결함 단정이 아니다</b> - 이 재료가 아는 것(목차가 갈랐다)과
        /// 모르는 것(섹션이 한 트랜잭션을 공유하는가)을 그대로 적는다.
        /// </summary>
        public static IReadOnlyList<string> ConfirmationItems(IReadOnlyList<TransactionSpanSplit> splits) =>
            splits.Select(split =>
                $"The original `{split.CallerProcedure}` ran `{split.CalleeProcedure}` inside ONE transaction " +
                $"(source lines {split.SpanFrom}-{split.SpanTo}), and the callee has no transaction of its own, " +
                $"so both used to commit or roll back together. The outline split them into steps " +
                $"{split.CallerStepCode} and {split.CalleeStepCode}. CONFIRM in the step sections whether the two " +
                $"steps share one business transaction (the earlier step opens it and does not commit, the later " +
                $"step commits once) and whether a failure in {split.CalleeStepCode} rolls back what " +
                $"{split.CallerStepCode} wrote. If each step commits on its own, that is a defect: report it. " +
                "If they share one transaction, this is already correct - do NOT report it.")
                .ToList();

        /// <param name="Line">원본 DDL에서의 줄 번호(1부터).</param>
        /// <param name="Name">호출 대상 프로시저 이름(원문 표기).</param>
        private readonly record struct ProcedureCallFact(int Line, string Name);

        /// <summary>
        /// 이름 고정 프로시저 호출(<c>EXEC dbo.X</c>)만 줄 번호와 함께 뽑는다.
        ///
        /// <c>SpecExpectations.InternalProcedureCallVisitor</c> 와 같은 판별을 쓴다 -
        /// <see cref="ExecutableProcedureReference"/> 이고 <c>sp_executesql</c> 이 아닐 것.
        /// <c>EXEC(@sql)</c> 같은 문자열 실행은 매치되지 않는다(동적 SQL 이지 내부 호출이 아니다).
        ///
        /// [정규식을 쓰지 않는 이유] 주석과 문자열 리터럴 안의 <c>EXEC</c> 가 걸린다.
        /// 이 저장소는 그 함정을 이미 겪었다 - 「구문을 보는 검사는 원문을 파싱하라」.
        /// </summary>
        private static IReadOnlyList<ProcedureCallFact> ProcedureCallLines(string ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<ProcedureCallFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);

                // 부분 파스 결과로 판정하지 않는다 - TransactionBoundaryExtractor 와 같은 정책.
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<ProcedureCallFact>();
                }

                var visitor = new ProcedureCallVisitor();
                fragment.Accept(visitor);
                return visitor.Calls;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[N6] 프로시저 호출 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<ProcedureCallFact>();
            }
        }

        private sealed class ProcedureCallVisitor : TSqlFragmentVisitor
        {
            public List<ProcedureCallFact> Calls { get; } = new();

            public override void Visit(ExecuteStatement node)
            {
                if (node.ExecuteSpecification?.ExecutableEntity is not ExecutableProcedureReference procRef) return;

                var name = procRef.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value;
                if (string.IsNullOrEmpty(name)) return;
                if (string.Equals(name, "sp_executesql", StringComparison.OrdinalIgnoreCase)) return;
                if (node.StartLine <= 0) return;

                Calls.Add(new ProcedureCallFact(node.StartLine, name));
            }
        }
    }
}
