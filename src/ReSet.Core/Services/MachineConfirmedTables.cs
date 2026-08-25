using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 기계 확정 표의 행을 Critic이 어디까지 심판할 수 있는지 가르는 축.
    /// </summary>
    public enum MachineConfirmedTableVerification
    {
        /// <summary>DDL 원문을 축자로 옮긴 표. 명세서의 사본이 원문과 다를 때만 결함이다.</summary>
        DdlTranscription,

        /// <summary>
        /// DDL 본문이 말하지 않는 사실을 담은 표. Critic 프롬프트에는 이 행들을 뽑아낸
        /// 재료가 실리지 않으므로 대조할 원천 자체가 없다 - 어떤 이유로도 보고 대상이 아니다.
        /// </summary>
        ExecutionSemantics,

        /// <summary>일부 칸만 DDL로 대조되는 표.</summary>
        Mixed
    }

    /// <param name="Heading">표의 헤딩 리터럴. 추출기의 상수를 그대로 참조한다.</param>
    /// <param name="Verification">Critic이 이 표의 행을 어디까지 볼 수 있는가.</param>
    /// <param name="MixedScopeNote">
    /// Mixed 표만 채운다 - 어느 칸이 DDL로 대조되고 어느 칸이 아닌지는 표마다 다르므로
    /// 부류 하나로는 표현되지 않는다. Critic 블록의 그 표 항목 본문이 된다.
    /// </param>
    public sealed record MachineConfirmedTable(
        string Heading,
        MachineConfirmedTableVerification Verification,
        string? MixedScopeNote = null);

    /// <summary>
    /// 기계 확정 표 목록의 단일 출처다.
    /// </summary>
    public static class MachineConfirmedTables
    {

        public const string HeadingSuffix = "(기계 확정 — 수정 금지)";

        /// <summary>
        /// 목록의 순서가 곧 Critic 프롬프트에 실리는 순서다. 프롬프트 접두사 캐시가
        /// 바이트 일치로 걸리므로 순서를 흔들지 마십시오 - 리플렉션으로 모으지 않고
        /// 여기 손으로 적어 두는 이유가 그것이다.
        /// </summary>
        public static readonly IReadOnlyList<MachineConfirmedTable> All = new[]
        {
            new MachineConfirmedTable(
                DmlScopeExtractor.DmlScopeTableHeading,
                MachineConfirmedTableVerification.DdlTranscription),
            new MachineConfirmedTable(
                DmlScopeExtractor.SetPredicateTableHeading,
                MachineConfirmedTableVerification.DdlTranscription),
            new MachineConfirmedTable(
                DerivedTableColumnExtractor.DerivedTableHeading,
                MachineConfirmedTableVerification.DdlTranscription),
            new MachineConfirmedTable(
                DmlScopeExtractor.LockHintTableHeading,
                MachineConfirmedTableVerification.DdlTranscription),
            new MachineConfirmedTable(
                ObjectDeclarationExtractor.ObjectDeclarationTableHeading,
                MachineConfirmedTableVerification.DdlTranscription),
            new MachineConfirmedTable(
                CaseBranchExtractor.TableHeading,
                MachineConfirmedTableVerification.DdlTranscription),
            // 이 표만 부류가 다르다. DB 배치는 메타데이터에서, 나머지 넷은 실행 의미에서
            // 오므로 DDL 본문에는 근거가 없다.
            new MachineConfirmedTable(
                ExecutionSemanticsFacts.TableHeading,
                MachineConfirmedTableVerification.ExecutionSemantics),
            // 호출부·인자는 DDL로 대조되지만 Spec.md 링크 칸은 파이프라인 산출물이다.
            new MachineConfirmedTable(
                DmlScopeExtractor.ReferencedFunctionTableHeading,
                MachineConfirmedTableVerification.Mixed,
                "the call site and arguments come from the DDL and are checkable, but the Spec.md "
                + "link column comes from the pipeline's own output. Never report that column."),
            // 둘 다 DDL 본문에서 그대로 읽히는 전사 표다.
            new MachineConfirmedTable(
                TransactionBoundaryExtractor.TableHeading,
                MachineConfirmedTableVerification.DdlTranscription),
            new MachineConfirmedTable(
                SetAssignmentExtractor.TableHeading,
                MachineConfirmedTableVerification.DdlTranscription)
        };

        // [선언 순서 주의] 아래 초기화는 All을 읽는다. 정적 필드 초기화는 선언 순서대로
        // 돌므로 이 블록이 All보다 위로 올라가면 All이 null인 채로 실행되어
        // TypeInitializationException이 난다(실제로 한 번 냈다).
        /// <summary>
        /// Critic 시스템 프롬프트에 실리는 면제 블록이다. SP·함수 두 갈래가 이 하나를
        /// 이어 붙여 쓰므로 문구가 갈라지지 않는다.
        ///
        /// [왜 Critic에게 면제가 필요한가] Critic 프롬프트에는 기계 확정 재료가 실리지
        /// 않는다(BuildMachineFactBlockLines의 호출부는 전부 Actor 갈래다). 그래서 DDL
        /// 본문에 근거가 없는 행을 환각으로 오판하고, L1은 반대로 그 행의 원문 복원을
        /// 요구해 교착이 되며 재시도가 소진된다 - 2026-08-22 재생성에서 실제로 세 번 났다.
        ///
        /// [왜 표 종류로 가르는가] "환각으로 보지 마라"만으로는 부족하다. 전사 표는
        /// 원문 대조가 살아 있어야 하고, 실행 의미 표는 대조할 원천 자체가 Critic에게
        /// 없어 어떤 이유로도 보고 대상이 아니다. 두 규칙을 한 문장으로 합치면 "DDL과
        /// 다르면 보고"라는 탈출구가 남아 같은 오판이 되돌아온다.
        /// </summary>
        public static string CriticExemptionBlock { get; } = BuildCriticExemptionBlock();

        private static string BuildCriticExemptionBlock()
        {
            var lines = new List<string>
            {
                "[Machine-Confirmed Tables - NOT subject to your review]",
                $"Any table under a heading ending with `{HeadingSuffix}` is MACHINE-DERIVED from the source DDL, its static-analysis AST, and database metadata. It is not the model's claim. Do NOT report its rows as hallucination, invention, or unsupported assertion, and do NOT lower any score because a fact in it cannot be traced to the DDL body - several of these facts come from metadata outside the DDL text (the object's own database name) or from execution semantics that the DDL text does not state (CAST rounding direction, @@ROWCOUNT reset boundaries).",
                "Two of these are known to trip reviewers, both verified by execution on SQL Server 2022:",
                "  - `money`/`smallmoney` -> `int` CAST ROUNDS away from zero (12.5 -> 13), while `numeric`/`decimal` -> `int` truncates toward zero (12.5 -> 12). They differ; do not collapse them into one rule.",
                "  - A skipped `IF` branch DOES reset `@@ROWCOUNT` to 0.",
                "Which rows you may report at all depends on where the row's fact comes from:"
            };

            var transcription = HeadingList(MachineConfirmedTableVerification.DdlTranscription);
            if (transcription.Count > 0)
            {
                lines.Add(
                    $"  - Transcription tables - {Join(transcription)} - carry text transcribed from the DDL. "
                    + "Report such a row ONLY when the specification's copy differs from the DDL text that the row cites.");
            }

            var executionSemantics = HeadingList(MachineConfirmedTableVerification.ExecutionSemantics);
            if (executionSemantics.Count > 0)
            {
                var kinds = string.Join(", ", ExecutionSemanticsFacts.AllKinds.Select(kind => $"`{kind}`"));
                lines.Add(
                    $"  - {Join(executionSemantics)} carries facts the DDL text does NOT state. Its 종류 column is one of {kinds} "
                    + $"- `{ExecutionSemanticsFacts.DatabasePlacementKind}` comes from database metadata, the rest from execution semantics. "
                    + "You are not given the material these rows were derived from, so you have no way to check them here: "
                    + "NEVER report one - not as hallucination, not as a mismatch with the DDL, not as unverifiable, "
                    + "not as unsupported by the evidence. The two facts spelled out above are the most common traps, not the whole list; "
                    + "this rule covers every row of that table, including the ones no example mentions.");
            }

            foreach (var table in All.Where(t => t.Verification == MachineConfirmedTableVerification.Mixed))
            {
                lines.Add($"  - `{table.Heading}` - {table.MixedScopeNote}");
            }

            lines.Add("Prose elsewhere in the document that contradicts one of these rows IS a defect - report that.");

            // 개행을 \n으로 고정한다 - 프롬프트 접두사 캐시가 바이트 일치이기 때문이다.
            return string.Join("\n", lines);
        }

        private static List<string> HeadingList(MachineConfirmedTableVerification verification) =>
            All.Where(table => table.Verification == verification)
                .Select(table => table.Heading)
                .ToList();

        private static string Join(IEnumerable<string> headings) =>
            string.Join(", ", headings.Select(heading => $"`{heading}`"));
    }
}
