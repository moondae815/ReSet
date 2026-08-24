using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>제어 테이블 컬럼 하나. AllowedValues가 있으면 그것이 상태 어휘 전부다.</summary>
    /// <param name="IsIdentity">
    /// 이 컬럼이 값을 스스로 발급하는가. batch.BatchRun.RunId만 참이다 -
    /// 발급 지점이 하나여야 실행 단위가 갈라지지 않는다.
    /// </param>
    public sealed record ControlColumn(
        string Name,
        string SqlType,
        bool Nullable,
        IReadOnlyList<string>? AllowedValues = null,
        bool IsIdentity = false);

    /// <summary>
    /// 제어 행을 누가 만드는가.
    ///
    /// 이 축이 계약에 있어야 하는 이유: 실측에서 INSERT INTO batch.BatchRun이
    /// 번들 전체에 0건이었고 S03·S06·S17이 자기 저널·체크포인트 행을 만드는
    /// 지점 없이 UPDATE만 했다. @@ROWCOUNT 검사가 있는 곳은 정상 실행에서도
    /// 상시 실패하고, 없는 곳은 0행 갱신을 오류 없이 지나간다.
    /// </summary>
    public enum ControlRowOrigin
    {
        /// <summary>
        /// 이 테이블을 대상으로 선언한 첫 단계가 INSERT하며 RunId를 발급한다.
        /// 위치가 아니라 대상 보유로 정하는 이유는 <see cref="BatchControlContract.ResolveRowCreators"/>에 있다.
        /// </summary>
        FirstStepInserts,

        /// <summary>각 단계가 시작 시 자기 행을 INSERT한 뒤 종료 시 UPDATE한다.</summary>
        EachStepInserts,

        /// <summary>생산 단계가 INSERT만 한다. 전이가 없다.</summary>
        ProducerInsertsOnly
    }

    /// <param name="StatusColumn">상태 어휘를 담은 컬럼. 없으면 null.</param>
    /// <param name="PrimaryKey">
    /// 기본 키 컬럼 목록. 전이가 없는 테이블(ProducerInsertsOnly)에는 원칙적으로 두지
    /// 않는다 - 한 단계가 같은 IssueCode를 여러 번 낼 수 있어 자연 키가 없고, 대리 키를
    /// 넣으면 단계가 써야 할 컬럼이 늘어난다.
    ///
    /// 예외는 batch.BatchControlTotal이다. 그 표에는 자연 키가 있다 - 같은 실행의 같은
    /// 단계가 같은 지표를 두 번 낼 이유가 없고, 두 번 들어오면 검증 단계가 어느 행을
    /// 기준으로 삼을지 모른 채 대조가 갈린다. 위 규칙의 이유가 "자연 키가 없다"였으므로,
    /// 자연 키가 있는 표에는 그 이유가 적용되지 않는다.
    /// </param>
    public sealed record ControlTable(
        string Name,
        IReadOnlyList<ControlColumn> Columns,
        ControlRowOrigin Origin,
        string? StatusColumn,
        IReadOnlyList<string>? PrimaryKey = null);

    /// <summary>
    /// 배치 실행 제어 테이블의 정본.
    ///
    /// [왜 ReSet이 정하는가]
    /// 배치 골격에는 레거시 원본이 없다. 원본에서 추출할 수 있는 사실이 아니므로
    /// 누군가는 정해야 하는데, 지금까지 아무도 정하지 않았다. 그 결과 단계 18개가
    /// 각각 독립된 LLM 호출이라 같은 batch.BatchStepJournal에 대해 S01은
    /// StepStatus='Succeeded'를, S02는 ExecutionStatus='Completed'를, S17은
    /// StepState를, integrity-sql.md는 j.Status를 썼다. 어느 쪽으로 DDL을 만들어도
    /// 반대편 단계가 컴파일되지 않는다.
    ///
    /// DataAccessPolicy가 생성 번들의 계약 자산을 단독 소유하는 것과 같은 패턴이다.
    /// 계약 문구를 조립 코드에서 다시 쓰지 마십시오 - 테스트가 닿지 않는 계약이 된다.
    ///
    /// [왜 Completed를 버리는가]
    /// 성공 종료 어휘가 Succeeded와 Completed 둘로 갈리면
    /// CP.CheckpointStatus='Completed' AND SJ.ExecutionStatus&lt;&gt;'Completed' 같은
    /// 대조가 정상 성공한 단계에서 참이 되어 모든 재시작이 차단된다. 규칙을 하나로
    /// 만드는 것이 어느 쪽을 고르는가보다 중요하다.
    ///
    /// [담지 않는 것]
    /// BatchSourceWatermark와 BatchImmutableLedgerBaseline은 어느 원천을 워터마킹하고
    /// 어느 원장을 기준선으로 잡는지에 따라 컬럼이 달라지는 Job 형상 객체다. ReSet이
    /// 정할 수 있는 사실이 아니므로 스키마·명명 규칙만 적용하고 DDL은 계획서에 맡긴다.
    /// </summary>
    public static class BatchControlContract
    {
        private static readonly string[] RunStates = { "Running", "Succeeded", "Failed", "Restarting" };
        private static readonly string[] StepStates = { "Running", "Succeeded", "Failed", "Skipped" };
        private static readonly string[] CheckpointStates = { "Pending", "Succeeded" };
        private static readonly string[] LockStates = { "Held", "Released" };

        public static IReadOnlyList<ControlTable> Tables { get; } = new[]
        {
            new ControlTable(
                "batch.BatchRun",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false, null, IsIdentity: true),
                    new ControlColumn("JobName", "nvarchar(128)", false),
                    new ControlColumn("BatchYmd", "varchar(8)", false),
                    new ControlColumn("RunStatus", "nvarchar(20)", false, RunStates),
                    new ControlColumn("ResumeFromStepCode", "nvarchar(10)", true),
                    new ControlColumn("StartedAtUtc", "datetime2(3)", false),
                    new ControlColumn("CompletedAtUtc", "datetime2(3)", true),
                    new ControlColumn("ErrorMessage", "nvarchar(max)", true)
                },
                ControlRowOrigin.FirstStepInserts,
                "RunStatus",
                new[] { "RunId" }),

            new ControlTable(
                "batch.BatchStepJournal",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false),
                    new ControlColumn("StepCode", "nvarchar(10)", false),
                    new ControlColumn("StepStatus", "nvarchar(20)", false, StepStates),
                    new ControlColumn("LegacyReturnCode", "int", true),
                    new ControlColumn("StartedAtUtc", "datetime2(3)", false),
                    new ControlColumn("CompletedAtUtc", "datetime2(3)", true),
                    new ControlColumn("ErrorMessage", "nvarchar(max)", true)
                },
                ControlRowOrigin.EachStepInserts,
                "StepStatus",
                new[] { "RunId", "StepCode" }),

            new ControlTable(
                "batch.BatchCheckpoint",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false),
                    new ControlColumn("StepCode", "nvarchar(10)", false),
                    new ControlColumn("CheckpointStatus", "nvarchar(20)", false, CheckpointStates),
                    new ControlColumn("CompletedAtUtc", "datetime2(3)", true)
                },
                ControlRowOrigin.EachStepInserts,
                "CheckpointStatus",
                new[] { "RunId", "StepCode" }),

            new ControlTable(
                "batch.BatchValidationIssue",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false),
                    new ControlColumn("StepCode", "nvarchar(10)", false),
                    new ControlColumn("IssueCode", "nvarchar(64)", false),
                    new ControlColumn("Severity", "nvarchar(20)", false,
                        new[] { "Info", "Warning", "Error", "Critical" }),
                    new ControlColumn("ExpectedValue", "nvarchar(200)", true),
                    new ControlColumn("ActualValue", "nvarchar(200)", true),
                    new ControlColumn("DetectedAtUtc", "datetime2(3)", false)
                },
                ControlRowOrigin.ProducerInsertsOnly,
                "Severity"),

            // [왜 기준값 저장소로 좁히는가]
            // 코퍼스 관측에서 이 표의 컬럼 집합이 넷으로 갈렸고, 그중 하나는
            // ExpectedValue·ActualValue·IsMatched로 대조 결과까지 담았다. 그것은
            // batch.BatchValidationIssue와 역할이 겹친다. 산출물도 이미 나뉘어
            // 있다 - S16은 이 표를 "단계별 기준값"으로 읽고 대조 결과는 따로 쓴다.
            // 넷 중 하나를 고를 근거가 이것뿐이었다. 나머지 셋은 빈도뿐이다.
            //
            // 관측된 변이 하나는 RowCount를 컬럼명으로 썼다. T-SQL 예약어라
            // 대괄호 없이는 구문 오류다 - 계약이 그 이름을 배제하는 것 자체가 값이다.
            new ControlTable(
                "batch.BatchControlTotal",
                new[]
                {
                    new ControlColumn("RunId", "bigint", false),
                    new ControlColumn("StepCode", "nvarchar(10)", false),
                    new ControlColumn("ControlName", "nvarchar(64)", false),
                    new ControlColumn("ControlValue", "decimal(38,4)", false),
                    new ControlColumn("CapturedAtUtc", "datetime2(3)", false)
                },
                ControlRowOrigin.ProducerInsertsOnly,
                null,
                new[] { "RunId", "StepCode", "ControlName" }),

            // [왜 소유자 컬럼 이름을 RunId와 가르는가]
            // 이 표의 키는 (JobName, BatchYmd)다. 소유자 컬럼을 RunId라고 부르면
            // 키가 RunId라고 읽혀 실행마다 잠금 행이 새로 생기고, 그러면 잠금이
            // 잠그지 않는다. 관측된 변이 셋(RunId·OwnerRunId·LockOwnerRunId) 중
            // 역할이 이름에 드러나는 것을 고른다.
            new ControlTable(
                "batch.BatchRunLock",
                new[]
                {
                    new ControlColumn("JobName", "nvarchar(128)", false),
                    new ControlColumn("BatchYmd", "varchar(8)", false),
                    new ControlColumn("OwnerRunId", "bigint", false),
                    new ControlColumn("LockStatus", "nvarchar(20)", false, LockStates),
                    new ControlColumn("AcquiredAtUtc", "datetime2(3)", false),
                    new ControlColumn("HeartbeatAtUtc", "datetime2(3)", true),
                    new ControlColumn("ReleasedAtUtc", "datetime2(3)", true)
                },
                ControlRowOrigin.FirstStepInserts,
                "LockStatus",
                new[] { "JobName", "BatchYmd" })
        };

        /// <summary>
        /// 한정자가 있든 없든, 대소문자가 어떻든 찾는다. 단계 문서는 같은 테이블을
        /// batch.BatchRun으로도 BatchRun으로도 쓴다 - 한쪽만 인식하면 검사가 절반만 돈다.
        /// </summary>
        public static ControlTable? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var bare = BareName(name);
            return Tables.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BareName(t.Name), bare, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 단계 목록에서 <see cref="ControlRowOrigin.FirstStepInserts"/> 테이블의 행을
        /// 만들 책임이 어느 단계에 있는지 정한다 - 그 테이블을 대상으로 선언한 첫 단계다.
        /// 돌려주는 것은 단계 코드 → 그 단계가 만들어야 할 테이블 이름 목록이다.
        ///
        /// [왜 위치가 아니라 대상 보유인가]
        /// 실측(POQSettleProc17): 계약이 "목록의 첫 단계"라고 위치로 지목했는데 승인된
        /// 첫 단계 S01은 대상 테이블이 없는 비변경 사전검증이었다. S01은 자기 정의를
        /// 따라 INSERT를 쓰지 않았고, batch.BatchRun을 실제로 가진 S02는 계약을 믿고
        /// UPDATE만 했다. 아무도 행을 만들지 않은 계획서가 세 번 연속 같은 자리에서
        /// 반려됐다. 첫 단계를 순수 검증 단계로 두는 것은 흔한 설계이므로(Proc12·14도
        /// 그랬다) 계약이 위치를 강제하면 목차 설계와 계속 충돌한다.
        ///
        /// 아무 단계도 그 테이블을 대상으로 갖지 않으면 담당을 두지 않는다 - 계약을
        /// 쓰지 않는 Job일 수 있고, 없는 것을 결함으로 들지 않는다는 소프트 스킵
        /// 원칙이 여기에도 적용된다. 그 자리는 문서 전체를 보는 통합 검사가 덮는다.
        ///
        /// <see cref="ControlRowOrigin.EachStepInserts"/> 테이블은 단계 검사가 이미 모든
        /// 단계에 자기 행 INSERT를 요구하므로 여기서 담당을 지목하지 않는다 - 지목하면
        /// 같은 결함이 두 경로로 두 번 보고된다.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> ResolveRowCreators(
            IReadOnlyList<BatchStepPlan>? steps)
        {
            var creators = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (steps == null) return ToReadOnly(creators);

            foreach (var table in Tables)
            {
                if (table.Origin != ControlRowOrigin.FirstStepInserts) continue;

                foreach (var step in steps)
                {
                    if (step?.TargetTables == null || string.IsNullOrWhiteSpace(step.Code)) continue;

                    // Find는 맨이름으로도 맞춰 주므로 `[batch].[BatchRun]`이나 한정자
                    // 표기 차이가 담당 판정을 비껴가지 않는다. 이름이 다른 테이블
                    // (`dbo.TBatchRun`)은 맨이름이 달라 매치하지 않는다.
                    if (!step.TargetTables.Any(t => ReferenceEquals(Find(t), table))) continue;

                    if (!creators.TryGetValue(step.Code, out var owned))
                    {
                        owned = new List<string>();
                        creators[step.Code] = owned;
                    }

                    owned.Add(table.Name);
                    break;
                }
            }

            return ToReadOnly(creators);
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToReadOnly(
            Dictionary<string, List<string>> source) =>
            source.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase);

        private static string BareName(string name)
        {
            var idx = name.LastIndexOf('.');
            return idx >= 0 ? name[(idx + 1)..] : name;
        }

        /// <summary>회차 0 부트스트랩 문서가 실을 실제 DDL.</summary>
        public static string RenderDdl()
        {
            var sb = new StringBuilder();

            foreach (var table in Tables)
            {
                sb.AppendLine($"CREATE TABLE {table.Name}");
                sb.AppendLine("(");

                var lines = new List<string>();
                foreach (var col in table.Columns)
                {
                    var identity = col.IsIdentity ? " IDENTITY(1,1)" : "";
                    var nullability = col.Nullable ? "NULL" : "NOT NULL";
                    lines.Add($"    {col.Name} {col.SqlType}{identity} {nullability}");
                }

                if (table.PrimaryKey is { Count: > 0 })
                {
                    lines.Add($"    CONSTRAINT PK_{BareName(table.Name)} " +
                              $"PRIMARY KEY ({string.Join(", ", table.PrimaryKey)})");
                }

                foreach (var col in table.Columns.Where(c => c.AllowedValues is { Count: > 0 }))
                {
                    var values = string.Join(", ", col.AllowedValues!.Select(v => $"N'{v}'"));
                    lines.Add($"    CONSTRAINT CK_{BareName(table.Name)}_{col.Name} " +
                              $"CHECK ({col.Name} IN ({values}))");
                }

                sb.AppendLine(string.Join(",\n", lines));
                sb.AppendLine(");");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd() + "\n";
        }

        /// <summary>단계 프롬프트가 실을 계약 표.</summary>
        public static string RenderPromptTable()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| Table | Column | Type | Allowed values | Row origin |");
            sb.AppendLine("|---|---|---|---|---|");

            foreach (var table in Tables)
            {
                var origin = table.Origin switch
                {
                    ControlRowOrigin.FirstStepInserts =>
                        "The FIRST step that lists this table as a target INSERTs this row; " +
                        "RunId is issued by IDENTITY, " +
                        "so read it back with SCOPE_IDENTITY() and pass it to every later step. " +
                        "NEVER compute a RunId yourself. Later steps UPDATE this row.",
                    ControlRowOrigin.EachStepInserts =>
                        "EACH step INSERTs its own row when it starts, then UPDATEs it when it ends. Never UPDATE a row you did not insert.",
                    _ => "The producing step INSERTs only. There is no state transition."
                };

                foreach (var col in table.Columns)
                {
                    var values = col.AllowedValues is { Count: > 0 }
                        ? string.Join(" / ", col.AllowedValues)
                        : "-";
                    var nullability = col.Nullable ? "" : " NOT NULL";
                    sb.AppendLine(
                        $"| `{table.Name}` | `{col.Name}` | {col.SqlType}{nullability} | {values} | {origin} |");
                }
            }

            return sb.ToString();
        }
    }
}
