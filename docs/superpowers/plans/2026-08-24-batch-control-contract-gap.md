# 배치 제어 계약 공백 메우기 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `batch.BatchControlTotal`과 `batch.BatchRunLock`을 `BatchControlContract`의 정본에 실어, 기준값이 없어 `검증 불가`로 남은 신설 단계들의 대조를 성립시킨다.

**Architecture:** 두 표를 기존 네 표와 같은 방식으로 `ControlTable` 레코드에 박는다. 프롬프트 표·부트스트랩 DDL·L1 어휘 검사가 모두 `Tables`를 순회하므로 소비처는 따라온다. 정본을 정해도 비정본 동의어가 아무 검사에도 걸리지 않으므로 `Aliases` 축과 별도 조회 `FindAlias`를 더하고, 그것을 쓰는 검사는 파일 경합 때문에 뒤로 미룬다.

**Tech Stack:** C# / .NET, xUnit. 빌드·테스트는 `dotnet build`·`dotnet test`.

**Spec:** [`docs/superpowers/specs/2026-08-24-batch-control-contract-gap-design.md`](../specs/2026-08-24-batch-control-contract-gap-design.md)

## Global Constraints

- **`MechanicalValidator.cs`와 `MechanicalValidatorTests.cs`를 Task 1~4에서 건드리지 않는다.** 병렬 회차(`2026-08-24-axis-b-step-check.md`의 Task 6·7)가 그 두 파일을 쥐고 있다. Task 5·6은 그 회차가 끝난 뒤에만 착수한다.
- **병합은 병렬 회차의 Task 10(POQSettleBatch1 재생성 실측) 이후다.** 계약 표가 늘면 프롬프트 표가 바뀌어 그 회차의 "🔴🟠 9건 소멸" 측정에 잡음이 섞인다.
- **`dotnet test`는 실패 0 · 건너뜀 0이어야 한다**(`AGENTS.md:200`). 기대 개수를 계획서에 적지 않는다.
- 주석과 커밋 메시지는 한국어로 쓰고, **왜 그런지**를 적는다. 기존 `BatchControlContract.cs`의 주석 밀도를 따른다.
- 테스트 주석에는 그 테스트가 막는 실측 결함을 적는다(기존 테스트 파일의 관용구).

---

### Task 1: `batch.BatchControlTotal`을 정본에 싣는다

**Files:**
- Modify: `src/ReSet.Core/Services/BatchControlContract.cs` (`Tables` 배열)
- Test: `tests/ReSet.Core.Tests/BatchControlContractTests.cs`

**Interfaces:**
- Consumes: 기존 `ControlColumn`·`ControlTable`·`ControlRowOrigin` 정의 그대로.
- Produces: `BatchControlContract.Find("batch.BatchControlTotal")`가 컬럼 5개(`RunId`·`StepCode`·`ControlName`·`ControlValue`·`CapturedAtUtc`)를 가진 `ControlTable`을 돌려준다. `Origin`은 `ProducerInsertsOnly`, `StatusColumn`은 `null`, `PrimaryKey`는 `["RunId","StepCode","ControlName"]`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/BatchControlContractTests.cs` 끝에 더한다.

```csharp
    // 감사 실측(POQSettleBatch1 축 B #10·#24·#36): S01·S03·S16이 batch.ControlTotal에
    // INSERT/SELECT를 걸었는데 컬럼 계약이 어디에도 없어 컬럼 수·값 수 대조가
    // 성립하지 않았다. 신설 단계 4개가 `검증 불가`로 남은 뿌리다.
    [Fact]
    public void ControlTotal_IsAKeyedBaselineStore()
    {
        var table = BatchControlContract.Find("batch.BatchControlTotal");

        Assert.NotNull(table);
        Assert.Equal(
            new[] { "RunId", "StepCode", "ControlName", "ControlValue", "CapturedAtUtc" },
            table!.Columns.Select(c => c.Name).ToArray());
        Assert.Equal(ControlRowOrigin.ProducerInsertsOnly, table.Origin);
        Assert.Null(table.StatusColumn);
        Assert.Equal(new[] { "RunId", "StepCode", "ControlName" }, table.PrimaryKey);
    }

    // 통제합계는 숫자 비교가 본질이다. 문자열로 두면 S16의 합계 대조가 문자열
    // 비교가 되어 조용히 틀린다. BatchValidationIssue.ExpectedValue가 nvarchar인
    // 것과 갈리지만, 그쪽은 사람이 읽는 오류 기록이고 이쪽은 기계가 비교하는
    // 기준값이라 역할이 다르다.
    [Fact]
    public void ControlValue_IsNumericSoTheComparisonIsNumeric()
    {
        var value = BatchControlContract.Find("batch.BatchControlTotal")!
            .Columns.Single(c => c.Name == "ControlValue");

        Assert.Equal("decimal(38,4)", value.SqlType);
        Assert.False(value.Nullable);
    }

    // 계약 주석의 "전이 없는 표에는 PK를 두지 않는다"에 대한 의도된 예외다.
    // 그 규칙의 이유는 "한 단계가 같은 IssueCode를 여러 번 낼 수 있어 자연 키가
    // 없다"였는데, 통제합계에는 자연 키가 있다 - 같은 실행의 같은 단계가 같은
    // 지표를 두 번 낼 이유가 없고, 두 번 나면 S16이 어느 행을 기준으로 삼을지
    // 모른다.
    [Fact]
    public void ControlTotal_KeepsAPrimaryKeyEvenThoughItHasNoTransition()
    {
        Assert.Contains(
            "CONSTRAINT PK_BatchControlTotal PRIMARY KEY (RunId, StepCode, ControlName)",
            BatchControlContract.RenderDdl());
    }
```

- [ ] **Step 2: 기존 목록 테스트를 이 회차의 사실로 고친다**

`Tables_CoverTheFourControlTables`(12행)가 정확히 네 이름을 순서까지 단정하므로 지금 깨진다. 이름과 본문을 함께 고친다. **Task 2에서 한 번 더 고친다** — 표가 하나씩 늘기 때문이고, 그 편이 각 Task를 독립적으로 통과시킨다.

```csharp
    [Fact]
    public void Tables_CoverTheFiveControlTables()
    {
        var names = BatchControlContract.Tables.Select(t => t.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "batch.BatchRun",
                "batch.BatchStepJournal",
                "batch.BatchCheckpoint",
                "batch.BatchValidationIssue",
                "batch.BatchControlTotal"
            },
            names);
    }
```

- [ ] **Step 3: 테스트를 돌려 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchControlContractTests"`
Expected: FAIL. `ControlTotal_IsAKeyedBaselineStore`가 `Assert.NotNull`에서, `Tables_CoverTheFiveControlTables`가 배열 길이 불일치로 실패한다.

- [ ] **Step 4: 계약에 표를 더한다**

`BatchControlContract.cs`의 `Tables` 배열에서 `batch.BatchValidationIssue` 항목 **뒤에** 더한다(위 테스트가 순서를 단정한다).

```csharp
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
```

- [ ] **Step 5: 왜 이 형태인지 표 위에 주석으로 남긴다**

바로 위에 붙인다.

```csharp
            // [왜 기준값 저장소로 좁히는가]
            // 코퍼스 관측에서 이 표의 컬럼 집합이 넷으로 갈렸고, 그중 하나는
            // ExpectedValue·ActualValue·IsMatched로 대조 결과까지 담았다. 그것은
            // batch.BatchValidationIssue와 역할이 겹친다. 산출물도 이미 나뉘어
            // 있다 - S16은 이 표를 "단계별 기준값"으로 읽고 대조 결과는 따로 쓴다.
            // 넷 중 하나를 고를 근거가 이것뿐이었다. 나머지 셋은 빈도뿐이다.
            //
            // 관측된 변이 하나는 RowCount를 컬럼명으로 썼다. T-SQL 예약어라
            // 대괄호 없이는 구문 오류다 - 계약이 그 이름을 배제하는 것 자체가 값이다.
```

- [ ] **Step 6: 테스트를 돌려 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchControlContractTests"`
Expected: PASS, 실패 0 · 건너뜀 0.

`RenderDdl_IssuesRunIdWithIdentityOnTheRunTableOnly`가 `IDENTITY(1,1)` 개수를 1로 단정하는데 새 표에는 IDENTITY가 없으므로 그대로 통과한다. `RenderDdl_DoesNotDeclareAPrimaryKeyForTheInsertOnlyTable`은 `PK_BatchValidationIssue`만 보므로 통과한다.

- [ ] **Step 7: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/BatchControlContract.cs tests/ReSet.Core.Tests/BatchControlContractTests.cs
git commit -m "feat: batch.BatchControlTotal을 제어 계약의 정본에 싣는다"
```

---

### Task 2: `batch.BatchRunLock`을 정본에 싣는다

**Files:**
- Modify: `src/ReSet.Core/Services/BatchControlContract.cs` (`Tables` 배열, 상태 어휘 상수)
- Test: `tests/ReSet.Core.Tests/BatchControlContractTests.cs`

**Interfaces:**
- Consumes: Task 1이 남긴 `Tables` 배열의 순서(신규 표는 뒤에 붙인다).
- Produces: `BatchControlContract.Find("batch.BatchRunLock")`가 컬럼 7개(`JobName`·`BatchYmd`·`OwnerRunId`·`LockStatus`·`AcquiredAtUtc`·`HeartbeatAtUtc`·`ReleasedAtUtc`)를 가진 `ControlTable`을 돌려준다. `Origin`은 `FirstStepInserts`, `StatusColumn`은 `"LockStatus"`(허용값 `Held`/`Released`), `PrimaryKey`는 `["JobName","BatchYmd"]`.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
    // 코퍼스 관측: 이름은 다섯 코호트 전부에서 batch.BatchRunLock 하나로 수렴했는데
    // 컬럼이 갈렸다 - 날짜 4종(BatchYmd·ProcessingYmd·BusinessDate·BatchDate),
    // 소유자 3종, 상태 2종, 획득 시각 3종, 하트비트 3종. 이름이 이미 모였으므로
    // 고를 것은 컬럼뿐이다.
    [Fact]
    public void RunLock_IsKeyedByJobAndBusinessDay()
    {
        var table = BatchControlContract.Find("batch.BatchRunLock");

        Assert.NotNull(table);
        Assert.Equal(
            new[]
            {
                "JobName", "BatchYmd", "OwnerRunId", "LockStatus",
                "AcquiredAtUtc", "HeartbeatAtUtc", "ReleasedAtUtc"
            },
            table!.Columns.Select(c => c.Name).ToArray());
        Assert.Equal(ControlRowOrigin.FirstStepInserts, table.Origin);
        Assert.Equal("LockStatus", table.StatusColumn);
        Assert.Equal(new[] { "JobName", "BatchYmd" }, table.PrimaryKey);
    }

    // 같은 Job·같은 영업일에 잠금이 둘일 수 없다는 것이 이 표의 존재 이유다.
    // 키가 없으면 두 실행이 각자 자기 행을 넣고 둘 다 "잠갔다"고 믿는다.
    [Fact]
    public void RunLock_DeclaresItsPrimaryKeyAndStatusVocabularyInDdl()
    {
        var ddl = BatchControlContract.RenderDdl();

        Assert.Contains("CONSTRAINT PK_BatchRunLock PRIMARY KEY (JobName, BatchYmd)", ddl);
        Assert.Contains("CHECK (LockStatus IN (N'Held', N'Released'))", ddl);
    }

    // 소유자는 PK가 아니라 참조다. RunId라는 같은 이름을 쓰면 이 표의 키가
    // RunId라고 읽히고, 그러면 실행마다 잠금 행이 새로 생겨 잠금이 잠그지 않는다.
    [Fact]
    public void RunLock_NamesTheOwnerColumnApartFromRunId()
    {
        var table = BatchControlContract.Find("batch.BatchRunLock")!;

        Assert.Contains(table.Columns, c => c.Name == "OwnerRunId");
        Assert.DoesNotContain(table.Columns, c => c.Name == "RunId");
    }

    // 하트비트는 실물이다 - 번들 7개가 UPDATE ... HeartbeatUtc = SYSUTCDATETIME()
    // 형태로 실제로 갱신한다. 다만 최근 코호트 5개 중에서는 하나뿐이라 NULL을
    // 허용해 쓰지 않는 Job이 비워 둘 수 있게 한다. 빼면 하트비트 기반 잠금 회수를
    // 쓰는 Job이 어휘 검사에 걸려 그 설계 자체가 막힌다.
    [Fact]
    public void RunLock_LeavesTheOptionalTimestampsNullable()
    {
        var table = BatchControlContract.Find("batch.BatchRunLock")!;

        Assert.True(table.Columns.Single(c => c.Name == "HeartbeatAtUtc").Nullable);
        Assert.True(table.Columns.Single(c => c.Name == "ReleasedAtUtc").Nullable);
        Assert.False(table.Columns.Single(c => c.Name == "AcquiredAtUtc").Nullable);
    }
```

- [ ] **Step 2: 목록 테스트를 여섯으로 고친다**

Task 1에서 다섯으로 고쳤던 것을 다시 고친다.

```csharp
    [Fact]
    public void Tables_CoverTheSixControlTables()
    {
        var names = BatchControlContract.Tables.Select(t => t.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "batch.BatchRun",
                "batch.BatchStepJournal",
                "batch.BatchCheckpoint",
                "batch.BatchValidationIssue",
                "batch.BatchControlTotal",
                "batch.BatchRunLock"
            },
            names);
    }
```

- [ ] **Step 3: 테스트를 돌려 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchControlContractTests"`
Expected: FAIL. `RunLock_IsKeyedByJobAndBusinessDay`가 `Assert.NotNull`에서 실패한다.

- [ ] **Step 4: 상태 어휘 상수를 더한다**

`BatchControlContract` 클래스 상단, `CheckpointStates` 옆에 둔다.

```csharp
        private static readonly string[] LockStates = { "Held", "Released" };
```

- [ ] **Step 5: 계약에 표를 더한다**

`Tables` 배열의 `batch.BatchControlTotal` 항목 **뒤에** 더한다.

```csharp
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
                new[] { "JobName", "BatchYmd" }),
```

- [ ] **Step 6: 테스트를 돌려 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchControlContractTests"`
Expected: PASS.

`SuccessVocabulary_IsSucceededEverywhere_AndNeverCompleted`가 `StatusColumn`이 있는 모든 표를 돌며 `Completed`가 없음을 단정하는데 `Held`/`Released`에는 없으므로 통과한다.

- [ ] **Step 7: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/BatchControlContract.cs tests/ReSet.Core.Tests/BatchControlContractTests.cs
git commit -m "feat: batch.BatchRunLock을 제어 계약의 정본에 싣는다"
```

---

### Task 3: 동의어 축과 `FindAlias`

**Files:**
- Modify: `src/ReSet.Core/Services/BatchControlContract.cs` (`ControlTable` 레코드, `Tables`, 새 조회 메서드)
- Test: `tests/ReSet.Core.Tests/BatchControlContractTests.cs`

**Interfaces:**
- Consumes: Task 1의 `batch.BatchControlTotal` 항목.
- Produces: `ControlTable`에 선택 인자 `IReadOnlyList<string>? Aliases = null`이 생긴다. `public static ControlTable? FindAlias(string? name)` — 인자가 **비정본 동의어일 때만** 정본 `ControlTable`을 돌려주고, 정본 이름이거나 모르는 이름이면 `null`. 한정자 유무·대소문자를 가리지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
    // 설계 5절: 정본을 정해도 남은 batch.ControlTotal 16회가 아무 검사에도
    // 걸리지 않는다. 스키마 검사는 스키마 이름만 보고, 미지 테이블 검사는
    // IsInfraObject가 batch.*를 통째로 걸러내며, 어휘 검사는 Find()가 맨이름을
    // 맞추지 못해 그 표를 건너뛴다. 계약 위반이 아니라 침묵이라 다음 감사가
    // 같은 자리를 또 든다.
    [Fact]
    public void FindAlias_MapsTheObservedSynonymToTheCanonicalTable()
    {
        var table = BatchControlContract.FindAlias("batch.ControlTotal");

        Assert.NotNull(table);
        Assert.Equal("batch.BatchControlTotal", table!.Name);
    }

    // Find가 동의어를 겸해 받으면 CheckBatchControlVocabulary가 batch.ControlTotal을
    // 정본으로 착각해 컬럼만 검사하고 틀린 이름을 조용히 승인한다. 별칭은
    // 받아들일 것이 아니라 보고할 것이다.
    [Fact]
    public void Find_DoesNotAcceptTheSynonym()
    {
        Assert.Null(BatchControlContract.Find("batch.ControlTotal"));
    }

    // 정본 이름으로 물으면 "이것은 별칭이다"가 아니어야 한다 - 그러지 않으면
    // 호출부가 정상 이름을 오류로 보고한다.
    [Fact]
    public void FindAlias_ReturnsNullForACanonicalName()
    {
        Assert.Null(BatchControlContract.FindAlias("batch.BatchControlTotal"));
        Assert.Null(BatchControlContract.FindAlias("BatchControlTotal"));
    }

    // 단계 문서는 같은 표를 batch.ControlTotal로도 ControlTotal로도 쓴다.
    // 한쪽만 인식하면 검사가 절반만 돈다 - 기존 Find의 계약과 같다.
    [Fact]
    public void FindAlias_IsCaseInsensitiveAndAcceptsTheBareName()
    {
        Assert.Equal("batch.BatchControlTotal", BatchControlContract.FindAlias("CONTROLTOTAL")!.Name);
        Assert.Equal("batch.BatchControlTotal", BatchControlContract.FindAlias("[batch].[ControlTotal]")!.Name);
    }

    [Fact]
    public void FindAlias_ReturnsNullForAnUnknownName()
    {
        Assert.Null(BatchControlContract.FindAlias("batch.POQSettleS07Build"));
        Assert.Null(BatchControlContract.FindAlias(null));
        Assert.Null(BatchControlContract.FindAlias("   "));
    }
```

- [ ] **Step 2: 테스트를 돌려 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchControlContractTests"`
Expected: 컴파일 실패 — `FindAlias`가 없다.

- [ ] **Step 3: `ControlTable`에 `Aliases`를 더한다**

**`<param name="PrimaryKey">` 문서는 건드리지 않는다** — Task 1의 리뷰 소견으로 이미 그 자리에 `batch.BatchControlTotal` 예외 근거가 실렸다. 규칙을 깨는 커밋에 근거를 함께 두는 것이 맞아서 옮겼다. 여기서는 `Aliases` 항목만 더한다.

```csharp
    /// <param name="Aliases">
    /// 코퍼스에서 관측된 비정본 이름. 계약이 이것을 아는 이유는 정본을 정하는
    /// 것만으로는 이름이 수렴하지 않기 때문이다 - 동의어를 쓴 단계는 어느 검사에도
    /// 걸리지 않고 조용히 통과한다.
    /// </param>
    public sealed record ControlTable(
        string Name,
        IReadOnlyList<ControlColumn> Columns,
        ControlRowOrigin Origin,
        string? StatusColumn,
        IReadOnlyList<string>? PrimaryKey = null,
        IReadOnlyList<string>? Aliases = null);
```

- [ ] **Step 4: `batch.BatchControlTotal`에 별칭을 단다**

Task 1이 넣은 항목의 마지막 인자로 더한다.

```csharp
                ControlRowOrigin.ProducerInsertsOnly,
                null,
                new[] { "RunId", "StepCode", "ControlName" },
                new[] { "ControlTotal" }),
```

- [ ] **Step 5: `FindAlias`를 더한다**

`Find` 바로 아래에 둔다.

```csharp
        /// <summary>
        /// 관측된 비정본 이름을 정본 표로 되짚는다. 정본 이름이면 null이다.
        ///
        /// [왜 Find가 이것을 겸하지 않는가]
        /// 겸하면 CheckBatchControlVocabulary가 batch.ControlTotal을 정본으로 착각해
        /// 컬럼만 검사하고 틀린 이름을 조용히 승인한다. 별칭은 받아들일 것이 아니라
        /// 보고할 것이다. 순서도 그것이 맞다 - 이름을 먼저 정본으로 바꾸게 하고,
        /// 그다음 회차에 컬럼 검사가 걸린다.
        /// </summary>
        public static ControlTable? FindAlias(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (Find(name) != null) return null;

            var bare = BareName(name);
            return Tables.FirstOrDefault(t =>
                t.Aliases != null &&
                t.Aliases.Any(alias =>
                    string.Equals(BareName(alias), bare, StringComparison.OrdinalIgnoreCase)));
        }
```

- [ ] **Step 6: 대괄호 표기를 `BareName`이 견디는지 확인한다**

`BareName`은 마지막 `.` 뒤를 자를 뿐이라 `[batch].[ControlTotal]`에서 `[ControlTotal]`이 나온다. Step 1의 테스트가 이 표기를 단정하므로 실패하면 `BareName` 대신 대괄호를 벗기는 정규화가 필요하다. 기존 `Find`도 같은 한계를 가지므로 **`Find`와 같은 자리에 고친다.**

Run: `dotnet test --filter "FullyQualifiedName~FindAlias_IsCaseInsensitive"`

실패하면 `BareName`을 이렇게 고친다(정본 이름에는 대괄호가 없으므로 기존 동작은 바뀌지 않는다).

```csharp
        private static string BareName(string name)
        {
            var trimmed = name.Trim();
            var idx = trimmed.LastIndexOf('.');
            var bare = idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
            return bare.Trim('[', ']', ' ');
        }
```

- [ ] **Step 7: 테스트를 돌려 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchControlContractTests"`
Expected: PASS.

- [ ] **Step 8: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. `Find_IsCaseInsensitiveAndAcceptsTheBareName`이 Step 6의 변경으로 깨지지 않았는지 특히 본다.

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Core/Services/BatchControlContract.cs tests/ReSet.Core.Tests/BatchControlContractTests.cs
git commit -m "feat: 제어 계약이 비정본 동의어를 알고 되짚는다"
```

---

### Task 4: 프롬프트 표가 없는 IDENTITY를 말하지 않게 한다

**Files:**
- Modify: `src/ReSet.Core/Services/BatchControlContract.cs` (`RenderPromptTable`의 `origin` 스위치)
- Test: `tests/ReSet.Core.Tests/BatchControlContractTests.cs`

**Interfaces:**
- Consumes: Task 2의 `batch.BatchRunLock`(IDENTITY 컬럼이 없는 `FirstStepInserts` 표).
- Produces: `RenderPromptTable()`의 `FirstStepInserts` 문구가 IDENTITY 컬럼이 있는 표에만 `SCOPE_IDENTITY()` 문장을 싣는다. 출력 형식(열 구성·행 수)은 바뀌지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
    // FirstStepInserts 문구가 "RunId is issued by IDENTITY, so read it back with
    // SCOPE_IDENTITY()"를 무조건 실었다. batch.BatchRunLock은 같은 행 출처 모양이지만
    // IDENTITY 컬럼이 없어, 그대로 두면 프롬프트에 거짓 지시가 실린다.
    [Fact]
    public void RenderPromptTable_DoesNotClaimIdentityForATableThatHasNone()
    {
        var rows = BatchControlContract.RenderPromptTable()
            .Split('\n')
            .Where(line => line.Contains("`batch.BatchRunLock`", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.DoesNotContain("SCOPE_IDENTITY", row));
        Assert.All(rows, row => Assert.DoesNotContain("IDENTITY", row));
    }

    // 반대쪽도 지킨다 - BatchRun에서 그 문장이 사라지면 18번의 독립 호출이
    // 각자 RunId 발급 방식을 지어낸다.
    [Fact]
    public void RenderPromptTable_StillSaysHowRunIdIsIssuedForTheRunTable()
    {
        var rows = BatchControlContract.RenderPromptTable()
            .Split('\n')
            .Where(line => line.Contains("`batch.BatchRun`", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Contains("SCOPE_IDENTITY", row));
    }
```

- [ ] **Step 2: 테스트를 돌려 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~RenderPromptTable_DoesNotClaimIdentity"`
Expected: FAIL — `BatchRunLock` 행에 `SCOPE_IDENTITY`가 들어 있다.

- [ ] **Step 3: 문구를 IDENTITY 컬럼 유무로 가른다**

`RenderPromptTable`의 `foreach (var table in Tables)` 안, `origin` 대입을 이렇게 바꾼다.

```csharp
                // IDENTITY 문장은 그 표에 실제로 IDENTITY 컬럼이 있을 때만 싣는다.
                // 없는 표에 실으면 프롬프트가 존재하지 않는 발급 수단을 지시한다.
                var identity = table.Columns.FirstOrDefault(c => c.IsIdentity);

                var origin = table.Origin switch
                {
                    ControlRowOrigin.FirstStepInserts when identity != null =>
                        "The FIRST step that lists this table as a target INSERTs this row; " +
                        $"{identity.Name} is issued by IDENTITY, " +
                        "so read it back with SCOPE_IDENTITY() and pass it to every later step. " +
                        $"NEVER compute a {identity.Name} yourself. Later steps UPDATE this row.",
                    ControlRowOrigin.FirstStepInserts =>
                        "The FIRST step that lists this table as a target INSERTs this row. " +
                        "Later steps UPDATE this row. This table has no IDENTITY column - " +
                        "every key value is supplied by the step.",
                    ControlRowOrigin.EachStepInserts =>
                        "EACH step INSERTs its own row when it starts, then UPDATEs it when it ends. Never UPDATE a row you did not insert.",
                    _ => "The producing step INSERTs only. There is no state transition."
                };
```

- [ ] **Step 4: 테스트를 돌려 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchControlContractTests"`
Expected: PASS. `RenderPromptTable_SaysHowRunIdIsIssued`(137행)가 표 전체에 `IDENTITY`가 있는지만 보므로 `BatchRun` 덕분에 그대로 통과한다.

- [ ] **Step 5: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/BatchControlContract.cs tests/ReSet.Core.Tests/BatchControlContractTests.cs
git commit -m "fix: 프롬프트 표가 IDENTITY 없는 표에 발급 수단을 지시하지 않는다"
```

---

## 여기서 멈춘다 — Task 5·6은 게이트되어 있다

**Task 5와 6은 `MechanicalValidator.cs`를 건드린다.** 병렬 회차(`docs/superpowers/plans/2026-08-24-axis-b-step-check.md`)의 **Task 6(검사 D)과 Task 7(검사 E)이 그 파일을 쥐고 있다.** 두 Task가 끝나 그 회차의 브랜치에 병합되기 전에는 착수하지 않는다.

착수 가능 여부를 이렇게 확인한다.

```bash
git log --oneline axis-b-step-check | grep -E "검사 (D|E)"
```

두 줄이 다 나오면 진행한다. 아니면 **Task 4까지로 이 계획서를 닫고** 그 회차가 끝난 뒤 다시 연다.

---

### Task 5: 비정본 이름을 보고하는 단계 검사 (게이트됨)

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ValidateBatchStep` 호출부와 새 메서드)
- Create: `tests/ReSet.Core.Tests/BatchControlTableAliasTests.cs`

**Interfaces:**
- Consumes: Task 3의 `BatchControlContract.FindAlias(string?)`.
- Produces: `ValidateBatchStep`이 비정본 제어 표 이름 하나마다 오류 한 건을 낸다. 오류 문자열은 `` `batch.{관측이름}` `` 과 정본 이름을 모두 담는다.

- [ ] **Step 1: 실패하는 테스트를 새 파일에 쓴다**

`MechanicalValidatorTests.cs`는 **건드리지 않는다.** 새 파일 `tests/ReSet.Core.Tests/BatchControlTableAliasTests.cs`를 만든다.

```csharp
using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class BatchControlTableAliasTests
{
    private static readonly IReadOnlyDictionary<string, SpecConditions> NoConditions =
        new Dictionary<string, SpecConditions>();

    private static BatchStepPlan Plan() => new(
        Code: "S03",
        Name: "입력 기준시점 고정",
        LegacyProcedures: Array.Empty<string>(),
        TargetTables: new[] { "batch.BatchControlTotal" },
        ErrorCodes: Array.Empty<string>(),
        Chunkable: false,
        SchemaTables: Array.Empty<string>());

    // 감사 실측: 산출물이 같은 표를 batch.ControlTotal(16회)과
    // batch.BatchControlTotal(64회) 두 이름으로 부른다. 정본을 정해도 동의어 쪽은
    // 어느 검사에도 걸리지 않아 조용히 통과한다 - 계약 위반이 아니라 침묵이라
    // 다음 감사가 같은 자리를 또 든다.
    [Fact]
    public void ValidateBatchStep_NonCanonicalControlTableName_ShouldBeAnError()
    {
        var markdown =
            "### S03 단계\n\n```sql\nINSERT INTO batch.ControlTotal\n" +
            "(RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)\n" +
            "VALUES (@RunId, 'S03', 'SourceRows', 100, SYSUTCDATETIME());\n```\n";

        var result = new MechanicalValidator()
            .ValidateBatchStep(markdown, Plan(), Array.Empty<string>(), NoConditions);

        Assert.Contains(result.Errors, e =>
            e.Contains("batch.ControlTotal") && e.Contains("batch.BatchControlTotal"));
    }

    [Fact]
    public void ValidateBatchStep_CanonicalControlTableName_ShouldNotReportTheAlias()
    {
        var markdown =
            "### S03 단계\n\n```sql\nINSERT INTO batch.BatchControlTotal\n" +
            "(RunId, StepCode, ControlName, ControlValue, CapturedAtUtc)\n" +
            "VALUES (@RunId, 'S03', 'SourceRows', 100, SYSUTCDATETIME());\n```\n";

        var result = new MechanicalValidator()
            .ValidateBatchStep(markdown, Plan(), Array.Empty<string>(), NoConditions);

        Assert.DoesNotContain(result.Errors, e => e.Contains("정본 이름은"));
    }

    // Job 형상 스테이징 표는 계약 밖에 있는 것이 정상이다. 계약에 없다는 이유로
    // 들면 모든 Job이 자기 작업 표마다 오류를 받는다.
    [Fact]
    public void ValidateBatchStep_JobShapedStagingTable_ShouldBeSilent()
    {
        var markdown =
            "### S03 단계\n\n```sql\nSELECT * FROM batch.POQSettleS07Build;\n```\n";

        var result = new MechanicalValidator()
            .ValidateBatchStep(markdown, Plan(), Array.Empty<string>(), NoConditions);

        Assert.DoesNotContain(result.Errors, e => e.Contains("정본 이름은"));
    }

    // 같은 이름이 문서에 여러 번 나와도 오류는 한 건이다. 재생성 프롬프트에
    // 같은 지시가 열 번 실리면 다른 시정 항목이 밀려난다.
    [Fact]
    public void ValidateBatchStep_RepeatedAlias_ShouldBeReportedOnce()
    {
        var markdown =
            "### S03 단계\n\n```sql\nINSERT INTO batch.ControlTotal VALUES (1);\n" +
            "SELECT * FROM batch.ControlTotal;\nUPDATE batch.ControlTotal SET ControlValue = 2;\n```\n";

        var result = new MechanicalValidator()
            .ValidateBatchStep(markdown, Plan(), Array.Empty<string>(), NoConditions);

        var hits = 0;
        foreach (var error in result.Errors)
        {
            if (error.Contains("batch.ControlTotal") && error.Contains("정본 이름은")) hits++;
        }

        Assert.Equal(1, hits);
    }
}
```

- [ ] **Step 2: 테스트를 돌려 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchControlTableAliasTests"`
Expected: FAIL — 첫 테스트가 오류를 찾지 못한다.

`BatchStepPlan`의 생성자 인자 이름이 위와 다르면 `src/ReSet.Core/Services/BatchStepPlan.cs`를 열어 실제 이름으로 맞춘다. 나머지 테스트 본문은 그대로 둔다.

- [ ] **Step 3: 검사를 더한다**

`MechanicalValidator.cs`에서 `CheckBatchControlVocabulary` 메서드 **바로 위**에 둔다.

```csharp
        /// <summary>
        /// 제어 표를 정본 아닌 이름으로 부르는 자리를 보고한다.
        ///
        /// [왜 다른 검사가 못 잡는가 - 실측]
        /// CheckNonCanonicalBatchSchema는 스키마 이름(batch/batch_shadow)만 본다.
        /// CheckUnknownTableReferences는 IsInfraObject가 batch.*를 후보 단계에서
        /// 통째로 걸러내므로 batch.무엇이든 통과한다. CheckBatchControlVocabulary는
        /// Find()가 맨이름을 맞추지 못해 그 표를 건너뛴다. 그래서 정본을 정해도
        /// 동의어 쪽은 침묵한다 - 감사가 같은 자리를 다시 드는 이유다.
        ///
        /// 계약에 없는 batch 객체 전부를 들지는 않는다. batch.POQSettleS07Build 같은
        /// Job 형상 스테이징 표는 계약 밖에 있는 것이 정상이고, 그것까지 들면
        /// 모든 Job이 자기 작업 표마다 오류를 받는다. 관측된 동의어만 든다.
        /// </summary>
        private static void CheckControlTableAlias(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in Regex.Matches(
                stepMarkdown, @"\bbatch\.([A-Za-z_][A-Za-z_0-9]*)", RegexOptions.IgnoreCase))
            {
                var observed = match.Groups[1].Value;
                if (!reported.Add(observed)) continue;

                var canonical = BatchControlContract.FindAlias(observed);
                if (canonical == null) continue;

                result.Errors.Add(
                    $"{step.Code} 섹션이 제어 표를 `batch.{observed}`으로 부릅니다. " +
                    $"정본 이름은 `{canonical.Name}`입니다 - 같은 표를 두 이름으로 부르면 " +
                    "회차 0이 만드는 객체와 단계가 쓰는 객체가 갈립니다.");
            }
        }
```

- [ ] **Step 4: 배선한다**

`ValidateBatchStep` 안, `CheckBatchControlVocabulary(stepMarkdown, step, result);` **바로 위**에 한 줄을 더한다.

```csharp
            CheckControlTableAlias(stepMarkdown, step, result);
```

- [ ] **Step 5: 테스트를 돌려 통과를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~BatchControlTableAliasTests"`
Expected: PASS.

- [ ] **Step 6: 전체 테스트를 돌린다**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. 기존 `MechanicalValidatorTests`의 단계 검사 테스트가 새 오류 한 건 때문에 `IsValid`를 잃지 않았는지 본다 — 그 픽스처들이 `batch.ControlTotal`을 쓰고 있으면 정본 이름으로 고친다.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/BatchControlTableAliasTests.cs
git commit -m "feat: 단계 검사가 제어 표의 비정본 이름을 보고한다"
```

---

### Task 6: 코퍼스 스윕과 문서 갱신 (게이트됨)

**Files:**
- Create: 스크래치 하네스(저장소에 커밋하지 않는다)
- Modify: `docs/superpowers/specs/2026-08-24-axis-b-46-triage-design.md`(분류표 #10·#24·#36의 상태)

**Interfaces:**
- Consumes: Task 5까지의 계약과 검사.
- Produces: 검출량 실측과 그 결과를 반영한 분류표.

- [ ] **Step 1: 하네스를 만든다**

병렬 회차 계획서 Task 9의 `sweep-stepl1`을 골격으로 복사한다(`ProjectReference` 방식과 인자 처리가 같다). 스크래치패드 아래에 두고 저장소에 커밋하지 않는다.

```bash
SCRATCH="$(dirname "$(mktemp -u)")"   # 세션 스크래치패드 경로로 바꾼다
mkdir -p "$SCRATCH/sweep-contract"
cp "$SCRATCH/sweep-stepl1/"*.csproj "$SCRATCH/sweep-contract/" 2>/dev/null || true
```

`Program.cs`는 `output/Jobs/*/agent/steps/*.md`를 전부 돌며 각 단계에 대해 `PlanStructure.md`의 `BatchStepPlan`으로 `ValidateBatchStep`을 부르고, 오류 중 **아래 두 종류만** 골라 한 줄씩 낸다.

- 문자열에 `정본 이름은`이 있는 것 → 별칭 검사(Task 5)
- 문자열에 `batch.BatchControlTotal` 또는 `batch.BatchRunLock`이 있으면서 어휘 검사가 낸 것

CSV 열은 `Job,Step,Check,Message` 넷으로 고정한다 — Step 2의 `cut -d, -f3`이 `Check` 열을 센다.

- [ ] **Step 2: 스윕을 돌리고 검출량을 센다**

```bash
dotnet run --project "$SCRATCH/sweep-contract" > "$SCRATCH/sweep-contract/result.csv"
wc -l "$SCRATCH/sweep-contract/result.csv"
cut -d, -f3 "$SCRATCH/sweep-contract/result.csv" | sort | uniq -c | sort -rn
```

예상 대상은 `BatchControlTotal` 19단계 · `BatchRunLock` 19단계 · 별칭 16회다. **예상과 다르면 그 차이를 먼저 설명하고** 넘어간다.

- [ ] **Step 3: 표본으로 오탐을 확인한다**

검출이 30건 이하면 전건, 그보다 많으면 검사별로 무작위 10건씩 뽑아 해당 단계 파일을 직접 열어 실제 계약 위반인지 본다. 오탐이 하나라도 있으면 원인을 검사에 반영하고 스윕을 다시 돈다.

- [ ] **Step 4: 분류표를 갱신한다**

`2026-08-24-axis-b-46-triage-design.md`의 C층 표에서 **#10·#24·#36**에 닫힘 근거(스윕 결과)를 적는다. **#11·#25는 열린 채로 둔다** — 이 계획서가 닫지 않는다.

- [ ] **Step 5: 커밋**

```bash
git add docs/superpowers/specs/2026-08-24-axis-b-46-triage-design.md
git commit -m "docs: 제어 계약 확장의 코퍼스 검출량을 분류표에 싣는다"
```

---

## 이 계획서가 닫지 않는 것

- **`batch.ValidationResult`·`batch.SourceSnapshot`** — 설계 9절. `SourceSnapshot`은 이름이 셋으로 갈려 정본 판단이 선행해야 한다.
- **기존 네 표의 동의어 여섯** — `BatchRunStep`·`BatchStepRun`·`BatchStageRun`·`BatchTaskRun`·`BatchExecutionJournal`·`BatchStepExecution`. 카탈로그 B2 부류 재확인 과제.
- **분류표 #25(`CompletedAtUtc` 미기록)** — 종결 상태 전이 규칙은 `BatchRun` 계약을 건드리는 별개 축이다.
