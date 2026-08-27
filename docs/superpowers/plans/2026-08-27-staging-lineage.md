# 단계 내부 스테이징 계보 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 이행이 원본 한 문장을 「스테이징 적재」와 「대상 게시」로 쪼갤 때, 앞 문장에 남은 술어를 뒤 문장의 것으로 인정해 검사 B·C의 구조적 오탐 15건을 닫는다.

**Architecture:** 리더가 단계 전체(펜스를 가로질러)에서 「행 원천이 전부 앞선 문장의 쓰기 대상」인 문장을 찾아 **원시 계보**를 낸다. 검사 쪽이 명세서 DML 범위 표의 대상 테이블을 빼고 남은 것만 스테이징으로 인정해, 검사 B는 그 컬럼을 `relocated`에 합류시키고 검사 C는 그 문장의 술어를 초과로 세지 않는다.

**Tech Stack:** .NET 10 · C# · `Microsoft.SqlServer.TransactSql.ScriptDom` · xUnit

**Spec:** `docs/superpowers/specs/2026-08-27-staging-lineage-design.md`

## Global Constraints

- **`output/` 쓰기 금지. 읽기만 한다.** CLI 재생성(`--regenerate` 류)을 절대 돌리지 않는다. `--sweep`은 읽기라 허용된다.
- 워크트리에서 작업하면 코퍼스 재료 **둘**을 심링크한다. 하나만 걸면 `CorpusSetupGuardTests`가 빨간불로 막는다.
  ```bash
  ln -s <메인 저장소>/output output
  ln -s <메인 저장소>/output.bak-2026-08-22 output.bak-2026-08-22
  ```
- **건너뜀 0**이 게이트다. 건너뜀이 0이 아니면 심링크가 덜 걸린 것이다.
- **절대 통과 수를 게이트로 쓰지 않는다.** 환경마다 최대 5까지 어긋난다(원인 미상). 같은 환경의 **전후 차분**만 근거로 쓴다.
- 빌드 경고 0을 유지한다.
- 주석에서 다른 파일을 가리킬 때 **줄 번호가 아니라 멤버 이름**을 쓴다.
- 심볼을 지웠다면 `grep -rn "<지운 이름>" docs/`로 남은 서술을 함께 고친다.
- 커밋 전 트리가 더러운 상태에서 스윕 보고서를 내지 않는다 — 「커밋: X」가 거짓이 된다.
- 이 변경의 위험축은 **거짓 음성**이다. 넓히는 결정마다 「무엇이 조용해지는가」를 먼저 답한다.

---

## 파일 구조

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/StepSqlStatementReader.cs` | 계보 계산(`LineageSources`). 명세서를 보지 않는다 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 명세서 대상 제외 + 검사 B·C 적용 |
| `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs` | 계보 계산의 판정 단위 |
| `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` | 검사 B·C 적용과 (5-3-4) 회귀 |
| `docs/known-defects.md` | 부류 3·5 해소 기록 |
| `docs/audit-reports/sweeps/` | 통제 대조 스윕 보고서 |

---

## Task 1: 리더가 계보를 낸다

**Files:**
- Modify: `src/ReSet.Core/Services/StepSqlStatementReader.cs` — `StepSqlStatement`에 `LineageSources` 추가, `Read`에 후처리 추가, `NamedSourceFinder` 재사용
- Test: `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs`

**Interfaces:**
- Produces: `StepSqlStatement.LineageSources` — `IReadOnlyList<StepLineageSource>`이고 `public sealed record StepLineageSource(string SourceTable, IReadOnlyList<string> Columns)`. Task 2·3이 이것을 읽는다.

**핵심 사실 셋 — 이걸 모르면 구현이 틀린다.**

1. **계보는 펜스를 가로지른다.** 실물 `POQSettleProc2/S13`에서 적재문은 펜스 2(110–307행), 게시문은 펜스 3(313–506행)에 있다. 따라서 후처리는 `ReadFence`가 **아니라** `Read`에서 돈다.
2. **순서는 리스트 인덱스로 충분하다.** `Read`는 펜스를 문서 순서로 순회하고 `ReadFence`는 반환 전에 `found.Sort`로 오프셋 정렬한다. 그래서 누적 리스트가 이미 문서 순서다. 오프셋을 새로 노출할 필요가 없다.
3. **`NamedSourceFinder`가 이미 있다.** `CteProjectionAliases`용으로 만든 것으로, `NamedTableReference`의 `(Binding, Source)`를 내고 `QueryDerivedTable`·`ScalarSubquery` 하강을 막는다. 그대로 쓴다.

**중첩 구조** — 헬퍼 클래스 넷(`DmlCollector`·`CteProjectionAliases`·`NamedSourceFinder`·`SubordinatePredicateCollector`)은 전부 `StepSqlStatementReader`에 직접 중첩된 **형제**다(`DmlCollector`는 그 안에서 닫힌다). 그래서 `DmlCollector` 안에서 `NamedSourceFinder`를 그대로 쓸 수 있고, `AttachLineage`는 `StepSqlStatementReader`의 정적 메서드로 둔다.

- [ ] **Step 1: 실패하는 테스트를 쓴다 — 계보의 기본형**

`tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs` 끝에 추가한다.

```csharp
    // ─────────────────────────────────────────────────────────────────────
    // 단계 내부 스테이징 계보 — 이행이 원본 한 문장을 「스테이징 적재」와
    // 「대상 게시」로 쪼개면 술어는 앞 문장에 남고 앵커는 뒤 문장에 붙는다
    // (docs/known-defects.md (5-3-3) 부류 3·5, 코퍼스 15건).
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lineage_PublishFromEarlierStagingWrite_InheritsItsColumns()
    {
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO batch_shadow.S13_After\n" +
            "SELECT M.PLTID FROM SETTLE_POQ_DB.dbo.TSettleMst AS M\n" +
            "WHERE M.YMD = @pi_strYMD AND M.USESTATE = 2;\n" +
            "INSERT INTO SETTLE_POQ_DB.dbo.TSettleByTX\n" +
            "SELECT PLTID FROM batch_shadow.S13_After\n" +
            "WHERE ExecutionId = @pi_executionId;"));

        var publish = statements.Single(s => s.TargetTable == "TSettleByTX");
        var source = Assert.Single(publish.LineageSources);
        Assert.Equal("S13_After", source.SourceTable);
        Assert.Contains("YMD", source.Columns);
        Assert.Contains("USESTATE", source.Columns);

        // 적재문 자신은 계보가 없다 - 원천 TSettleMst 를 앞서 쓴 문장이 없다.
        Assert.Empty(statements.Single(s => s.TargetTable == "S13_After").LineageSources);
    }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --nologo -v q --filter "FullyQualifiedName~Lineage_PublishFromEarlierStagingWrite"`
Expected: 컴파일 실패 — `LineageSources` 없음

- [ ] **Step 3: 레코드와 필드를 만든다**

`StepSqlStatementReader.cs`에서 `StepSqlStatement` 레코드 **바로 앞**에 추가한다.

```csharp
    /// <param name="SourceTable">이 문장이 읽는, 같은 단계의 앞선 문장이 쓴 테이블.</param>
    /// <param name="Columns">그 앞선 문장의 술어·조인 키·하위 범위 컬럼 전부.</param>
    public sealed record StepLineageSource(string SourceTable, IReadOnlyList<string> Columns);
```

`StepSqlStatement`의 `SubordinatePredicateColumns` 아래에 추가한다.

```csharp
        /// <summary>
        /// 이 문장이 읽는 「단계 내부 스테이징」 후보와 그것을 쓴 문장의 컬럼.
        ///
        /// [불변식 - 검사 쪽이 이것에 의존한다] 행 원천이 **전부** 앞선 쓰기 대상일
        /// 때만 채워진다. 하나라도 앞서 쓰인 적 없는 테이블이면 빈 목록이다.
        /// 이 불변식이 없으면 검사 쪽의 All(…)이 부분집합 위에서 공허하게 참이 된다.
        ///
        /// [명세서 대상 제외는 여기서 하지 않는다] 리더는 명세서를 보지 않는다.
        /// 원본이 쓰는 테이블을 걸러 내는 것은 MechanicalValidator의 몫이다 -
        /// 그 제외가 없으면 DELETE 후 INSERT로 재게시하고 다시 UPDATE … FROM 하는
        /// 흔한 관용구가 게시문으로 오분류된다(설계서 §2-1, 코퍼스 탐침 118건 중
        /// 최다 원천이 원본 대상 테이블 tsettlemst 52건).
        /// </summary>
        public IReadOnlyList<StepLineageSource> LineageSources { get; init; }
            = Array.Empty<StepLineageSource>();
```

- [ ] **Step 4: 계보 후처리를 만든다**

같은 파일의 `StepSqlStatementReader` 클래스 안, `ReadFence` **뒤**에 추가한다.

```csharp
        /// <summary>
        /// 단계 전체에서 「행 원천이 전부 앞선 문장의 쓰기 대상」인 문장을 찾아
        /// 그 쓰기 문장의 컬럼을 매단다.
        ///
        /// [왜 Read에서 도는가 - 실물] 적재문과 게시문이 **다른 펜스**에 있다.
        /// POQSettleProc2/S13은 적재가 펜스 2, 게시가 펜스 3이다. ReadFence 안에서
        /// 돌면 이 관용구를 통째로 놓친다.
        ///
        /// [왜 오프셋이 아니라 인덱스인가] Read는 펜스를 문서 순서로 순회하고
        /// ReadFence는 반환 전에 오프셋으로 정렬한다. 누적 리스트가 이미 문서
        /// 순서이므로 인덱스가 곧 순서다.
        ///
        /// [한 홉만] 사슬(A → S1, S1을 읽어 S2를 씀, S2를 읽는 문장)은 따라가지
        /// 않는다. 실물 셋 다 한 홉이고, 미추적은 오탐 방향이라 안전하다.
        ///
        /// [제어 흐름은 보지 않는다] 앞선다는 것은 문서 순서다. 조건부로만 실행되는
        /// 적재문의 술어도 상속되는데 이는 침묵 방향이다 - 한계로 기록한다.
        /// </summary>
        private static IReadOnlyList<StepSqlStatement> AttachLineage(
            IReadOnlyList<StepSqlStatement> statements)
        {
            if (statements.Count < 2) return statements;

            var writtenAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var attached = new List<StepSqlStatement>(statements.Count);

            for (var i = 0; i < statements.Count; i++)
            {
                var statement = statements[i];
                var sources = statement.RowSourceTables;

                // 원천이 하나도 없거나(VALUES 삽입 등) 하나라도 앞서 쓰인 적이
                // 없으면 계보가 아니다 - 불변식.
                if (sources.Count > 0 && sources.All(writtenAt.ContainsKey))
                {
                    attached.Add(statement with
                    {
                        LineageSources = sources
                            .Select(s => new StepLineageSource(
                                s, ColumnsOf(statements[writtenAt[s]])))
                            .ToList()
                    });
                }
                else
                {
                    attached.Add(statement);
                }

                // 자기 자신은 뒤 문장에게만 보인다 - 먼저 읽고 나중에 등록한다.
                if (!string.IsNullOrEmpty(statement.TargetTable))
                {
                    writtenAt.TryAdd(statement.TargetTable, i);
                }
            }

            return attached;

            static IReadOnlyList<string> ColumnsOf(StepSqlStatement writer) => writer
                .PredicateColumns
                .Concat(writer.JoinColumns)
                .Concat(writer.SubordinatePredicateColumns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
```

- [ ] **Step 5: 행 원천을 수집한다**

`RowSourceTables`는 아직 없다. `StepSqlStatement`에 필드를 하나 더 넣고 `DmlCollector.Add`에서 채운다.

`StepSqlStatement`의 `LineageSources` 위에 추가한다.

```csharp
        /// <summary>
        /// 이 문장의 FROM·JOIN이 이름으로 참조하는 테이블(마지막 식별자).
        /// CTE 이름은 제외한다 - 테이블이 아니다. 파생 테이블·스칼라 하위질의
        /// 안쪽은 이 층이 아니므로 NamedSourceFinder가 하강을 막는다.
        /// </summary>
        public IReadOnlyList<string> RowSourceTables { get; init; }
            = Array.Empty<string>();
```

`DmlCollector.Add`의 `Found.Add((new StepSqlStatement(…) { … })` 초기화자에 한 줄 더한다.

```csharp
                        SubordinatePredicateColumns = subordinate.Columns.ToList(),
                        RowSourceTables = CollectRowSourceTables(froms, ctes),
```

같은 클래스에 헬퍼를 추가한다.

```csharp
            /// <summary>
            /// FROM·JOIN의 이름 테이블에서 CTE 이름을 뺀 것. 대상 테이블 자신도
            /// 뺀다 - UPDATE … FROM 대상 AS A 는 자기를 읽는 것이 아니다.
            /// </summary>
            private static IReadOnlyList<string> CollectRowSourceTables(
                IReadOnlyList<FromClause> froms, WithCtesAndXmlNamespaces? ctes)
            {
                var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (ctes?.CommonTableExpressions != null)
                {
                    foreach (var cte in ctes.CommonTableExpressions)
                    {
                        var name = cte.ExpressionName?.Value;
                        if (!string.IsNullOrWhiteSpace(name)) cteNames.Add(name!);
                    }
                }

                var finder = new NamedSourceFinder();
                foreach (var from in froms) from.Accept(finder);

                return finder.Sources
                    .Select(s => s.Source)
                    .Where(s => !cteNames.Contains(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
```

- [ ] **Step 6: `Read`에서 후처리를 부른다**

`Read(string?, out int)`의 `return statements;` 바로 앞을 바꾼다.

```csharp
            return AttachLineage(statements);
```

- [ ] **Step 7: 테스트가 통과하는지 확인한다**

Run: `dotnet test --nologo -v q --filter "FullyQualifiedName~Lineage_"`
Expected: PASS

- [ ] **Step 8: 판정 단위마다 테스트를 더한다**

같은 파일에 이어서 쓴다. **각 테스트가 하나의 판정을 잰다.**

```csharp
    [Fact]
    public void Lineage_SourceNotWrittenEarlier_YieldsNoLineage()
    {
        // [불변식] 원천이 하나라도 앞서 쓰인 적 없으면 빈 목록이어야 한다.
        // 부분집합을 내면 검사 쪽 All(…)이 공허하게 참이 되어, 실물 테이블을
        // 함께 읽는 문장이 스테이징 전용으로 판정된다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.S06Cancel\n" +
            "SELECT A.PLTID FROM dbo.TTxMst AS A WHERE A.YMDCANCEL = @p;\n" +
            "INSERT INTO dbo.TSettleMst\n" +
            "SELECT S.PLTID FROM stage.S06Cancel AS S\n" +
            "INNER JOIN dbo.TReal AS R ON R.PLTID = S.PLTID;"));

        Assert.Empty(statements.Single(s => s.TargetTable == "TSettleMst").LineageSources);
    }

    [Fact]
    public void Lineage_WriteAfterRead_IsNotCounted()
    {
        // 앞선다는 것은 문서 순서다. 뒤에서 쓰는 테이블은 계보가 아니다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO dbo.TSettleMst\n" +
            "SELECT S.PLTID FROM stage.S06Cancel AS S;\n" +
            "INSERT INTO stage.S06Cancel\n" +
            "SELECT A.PLTID FROM dbo.TTxMst AS A WHERE A.YMDCANCEL = @p;"));

        Assert.Empty(statements.Single(s => s.TargetTable == "TSettleMst").LineageSources);
    }

    [Fact]
    public void Lineage_CteNameIsNotARowSource()
    {
        // CTE 이름은 테이블이 아니다. 세면 「원천이 전부 앞선 쓰기 대상」이
        // 거짓이 되어 진짜 계보를 놓친다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.S02Candidate\n" +
            "SELECT A.PGName FROM dbo.TTxMst AS A WHERE A.YMD = @p;\n" +
            ";WITH Pick AS ( SELECT PGName FROM stage.S02Candidate )\n" +
            "INSERT INTO dbo.TSettleMst SELECT PGName FROM Pick;"));

        var publish = statements.Single(s => s.TargetTable == "TSettleMst");
        Assert.Empty(publish.RowSourceTables.Where(t => t == "Pick"));
        Assert.Contains("S02Candidate", publish.LineageSources.Select(l => l.SourceTable));
    }

    [Fact]
    public void Lineage_DoesNotFollowChains()
    {
        // 한 홉만. S1 → S2 → 게시 사슬에서 게시문은 S2의 컬럼만 받는다.
        var statements = StepSqlStatementReader.Read(Fence(
            "INSERT INTO stage.S1 SELECT A.PLTID FROM dbo.TTxMst AS A WHERE A.HopOne = 1;\n" +
            "INSERT INTO stage.S2 SELECT PLTID FROM stage.S1 WHERE HopTwo = 2;\n" +
            "INSERT INTO dbo.TSettleMst SELECT PLTID FROM stage.S2;"));

        var publish = statements.Single(s => s.TargetTable == "TSettleMst");
        var columns = publish.LineageSources.SelectMany(l => l.Columns).ToList();
        Assert.Contains("HopTwo", columns);
        Assert.DoesNotContain("HopOne", columns);
    }

    [Fact]
    public void Lineage_InheritsJoinAndSubordinateColumns()
    {
        // 적재문의 조인 키와 하위 범위 컬럼도 함께 물려받는다 - 부류 3의 실물
        // 둘(Proc1/S02·Proc8/S05)이 조인 키 PGName만으로 발화했다.
        var statements = StepSqlStatementReader.Read(Fence(
            ";WITH Src AS ( SELECT A.PGName FROM dbo.TTxMst AS A WHERE A.SubCol = 1 )\n" +
            "INSERT INTO stage.S02Candidate\n" +
            "SELECT X.PGName FROM Src AS X\n" +
            "LEFT JOIN dbo.TPGProperty AS Y ON Y.PGName = X.PGName;\n" +
            "INSERT INTO dbo.TSettleMst SELECT PGName FROM stage.S02Candidate;"));

        var columns = statements.Single(s => s.TargetTable == "TSettleMst")
            .LineageSources.SelectMany(l => l.Columns).ToList();
        Assert.Contains("PGName", columns);   // 조인 키
        Assert.Contains("SubCol", columns);   // 하위 범위
    }

    [Fact]
    public void Lineage_SpansFences()
    {
        // [실물] POQSettleProc2/S13은 적재가 펜스 2, 게시가 펜스 3에 있다.
        // ReadFence 안에서 계보를 돌면 이 관용구를 통째로 놓친다.
        var markdown =
            "### S13 단계\n\n```sql\n" +
            "INSERT INTO batch_shadow.S13_After\n" +
            "SELECT M.PLTID FROM dbo.TSettleMst AS M WHERE M.YMD = @p;\n" +
            "```\n\n```sql\n" +
            "INSERT INTO dbo.TSettleByTX SELECT PLTID FROM batch_shadow.S13_After;\n" +
            "```\n";

        var statements = StepSqlStatementReader.Read(markdown);
        var columns = statements.Single(s => s.TargetTable == "TSettleByTX")
            .LineageSources.SelectMany(l => l.Columns).ToList();
        Assert.Contains("YMD", columns);
    }
```

- [ ] **Step 9: 전부 통과하는지 확인한다**

Run: `dotnet test --nologo -v q --filter "FullyQualifiedName~Lineage_"`
Expected: PASS (7개)

- [ ] **Step 10: 변이를 넣어 각 판정이 잠겼는지 확인한다**

**조건 하나당이 아니라 조건 안의 판정 하나당이다.** 아래 여섯을 하나씩 넣고, 각각 **정확히 의도한 테스트만** 죽는지 본다. 죽지 않으면 그 결정은 검증되지 않고 있는 것이므로 테스트를 고친다(코드가 아니라).

| 변이 | 죽어야 할 테스트 |
|---|---|
| `sources.All(...)` → `sources.Any(...)` | `SourceNotWrittenEarlier_YieldsNoLineage` |
| `writtenAt.TryAdd`를 루프 **앞**으로 옮겨 자기 자신도 보이게 | `WriteAfterRead_IsNotCounted` |
| `CollectRowSourceTables`의 `!cteNames.Contains(s)` 제거 | `CteNameIsNotARowSource` |
| `ColumnsOf`가 `writer.LineageSources`도 합치게(사슬 추적) | `DoesNotFollowChains` |
| `ColumnsOf`에서 `JoinColumns`·`SubordinatePredicateColumns` 제거 | `InheritsJoinAndSubordinateColumns` |
| `AttachLineage` 호출을 `Read`에서 `ReadFence`로 옮김 | `SpansFences` |

변이는 반드시 원복한다. `git status --short`가 깨끗해야 한다.

- [ ] **Step 11: 전체 스위트와 빌드**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: 경고 0 · 오류 0 · 실패 0 · **건너뜀 0**

- [ ] **Step 12: 커밋**

```bash
git add src/ReSet.Core/Services/StepSqlStatementReader.cs tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs
git commit -m "feat: 리더가 단계 내부 스테이징 계보를 낸다"
```

---

## Task 2: 검사 B가 계보 컬럼을 이전으로 인정한다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `CheckAnchoredStatementFacts`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `StepSqlStatement.LineageSources` (`IReadOnlyList<StepLineageSource>`), `StepLineageSource.SourceTable`·`.Columns` — Task 1이 만든다.
- Produces: `private static HashSet<string> SpecTargetTables(IReadOnlyList<SpecDmlRow> rows)` — Task 3이 같은 헬퍼를 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`의 검사 B 구역 끝에 추가한다.

```csharp
        [Fact]
        public void ValidateBatchStep_CheckB_StagingLineagePredicate_IsTreatedAsRelocated()
        {
            // (5-3-3) 부류 3. 이행이 원본 한 문장을 「스테이징 적재」와 「대상 게시」로
            // 쪼개면 술어는 앞 문장에 남고 코드 앵커는 뒤 문장에 붙는다. 게시문만
            // 보면 YMD가 없어진 것처럼 보이지만 없어진 게 아니라 옮겨간 것이다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_A"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("INSERT", 1, 10, "TSettleByTX",
                        new[] { "YMD" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var step = new BatchStepPlan(
                Code: "S13", Name: "S13 단계",
                LegacyProcedures: new[] { "dbo.SP_A" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleByTX" },
                ErrorCodes: new[] { "-1" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            var markdown = "### S13 단계\n\n```sql\n" +
                "INSERT INTO batch_shadow.S13_After\n" +
                "SELECT M.PLTID FROM SETTLE_POQ_DB.dbo.TSettleMst AS M WHERE M.YMD = @p;\n" +
                "-- INSERT 1\n" +
                "INSERT INTO SETTLE_POQ_DB.dbo.TSettleByTX\n" +
                "SELECT PLTID FROM batch_shadow.S13_After WHERE ExecutionId = @e;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleByTX" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼"));
        }

        [Fact]
        public void ValidateBatchStep_CheckB_SpecTargetSourceIsNotStaging()
        {
            // 원본이 쓰는 테이블은 스테이징이 아니다. DELETE 후 INSERT로 재게시하고
            // 뒤에서 다시 읽는 관용구가 흔한데(코퍼스 탐침 118건 중 최다 원천이
            // 원본 대상 tsettlemst 52건), 그것을 게시문으로 보면 검사가 통째로
            // 조용해진다. 여기서는 YMD가 진짜로 빠졌으므로 발화해야 한다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_A"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("UPDATE", 1, 10, "TSettleMst",
                        new[] { "YMD" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var step = new BatchStepPlan(
                Code: "S07", Name: "S07 단계",
                LegacyProcedures: new[] { "dbo.SP_A" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                ErrorCodes: new[] { "-1" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            // 앞 INSERT가 TSettleMst(= 명세서 대상)에 쓰고, 뒤 UPDATE가 그것을 읽는다.
            var markdown = "### S07 단계\n\n```sql\n" +
                "INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst\n" +
                "SELECT A.PLTID FROM dbo.TTxMst AS A WHERE A.YMD = @p;\n" +
                "-- U1\n" +
                "UPDATE A SET A.CLCOMM = 1\n" +
                "FROM SETTLE_POQ_DB.dbo.TSettleMst AS A WHERE A.PLTID = @q;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("최상위 WHERE 술어 컬럼") && e.Contains("YMD"));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --nologo -v q --filter "FullyQualifiedName~CheckB_StagingLineage|FullyQualifiedName~CheckB_SpecTargetSource"`
Expected: 첫째 FAIL(「최상위 WHERE 술어 컬럼 … YMD」가 발화), 둘째 PASS(현재도 발화)

- [ ] **Step 3: 공용 헬퍼를 만든다**

`MechanicalValidator.cs`의 `CheckAnchoredStatementFacts` **바로 앞**에 추가한다.

```csharp
        /// <summary>
        /// 계보 원천 중 「원본이 쓰는 테이블」을 뺀 것 — 그 나머지가 단계 내부
        /// 스테이징이다.
        ///
        /// [왜 명세서 대상을 빼는가 - 설계서 §2-1 실측] 「앞선 문장이 썼는가」만으로
        /// 판정하면 DELETE 후 INSERT로 재게시하고 뒤에서 UPDATE … FROM 하는 흔한
        /// 관용구가 게시문으로 오분류된다. 코퍼스 탐침 118건 중 최다 원천이
        /// 원본 대상 테이블 자신(tsettlemst 52건)이었다.
        ///
        /// [왜 이름 규칙이 아닌가] 실물이 batch_shadow.·stage.·batch_work.·
        /// dbo.__poq_ 로 제각각이다. 이름 목록은 다섯 번째 이름에서 깨진다.
        /// </summary>
        private static IEnumerable<StepLineageSource> StagingSources(
            StepSqlStatement statement, HashSet<string> specTargets) => statement
            .LineageSources
            .Where(l => !specTargets.Contains(l.SourceTable));

        /// <summary>
        /// 행 원천이 전부 단계 내부 스테이징인가. 리더의 불변식(LineageSources는
        /// 원천이 전부 앞선 쓰기 대상일 때만 채워진다)에 기대므로, 여기서는
        /// 명세서 대상이 하나라도 섞였는지만 보면 된다.
        /// </summary>
        private static bool ReadsOnlyStaging(
            StepSqlStatement statement, HashSet<string> specTargets) =>
            statement.LineageSources.Count > 0
            && statement.LineageSources.All(l => !specTargets.Contains(l.SourceTable));
```

- [ ] **Step 4: 검사 B에 배선한다**

`CheckAnchoredStatementFacts`에서 `var rows = facts.SelectMany(f => f.DmlRows).ToList();` 다음 줄에 추가한다.

```csharp
            var specTargets = new HashSet<string>(
                rows.Select(r => r.TargetTable), StringComparer.OrdinalIgnoreCase);
```

같은 함수의 `relocated` 초기화를 바꾼다.

```csharp
                var relocated = new HashSet<string>(
                    group.SelectMany(a => a.Statement.SubordinatePredicateColumns
                        .Concat(StagingSources(a.Statement, specTargets)
                            .SelectMany(l => l.Columns))),
                    StringComparer.OrdinalIgnoreCase);
```

기존 `relocated` 주석 블록의 끝에 문단을 덧붙인다.

```csharp
                // [계보 이전 - 한 층 위의 같은 개념]
                // 이행이 원본 한 문장을 「스테이징 적재」와 「대상 게시」로 쪼개면
                // 술어는 앞 문장에 남고 코드 앵커는 뒤 문장에 붙는다((5-3-3) 부류 3).
                // 하위 범위 이전이 "같은 문장 안에서 옮겨갔다"라면 이것은 "이 문장을
                // 먹인 문장으로 옮겨갔다"이다. 검사를 끄지 않으므로, 적재문에도 그
                // 컬럼이 없으면 여전히 발화한다.
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test --nologo -v q --filter "FullyQualifiedName~CheckB_StagingLineage|FullyQualifiedName~CheckB_SpecTargetSource"`
Expected: 둘 다 PASS

- [ ] **Step 6: 변이로 잠금을 확인한다**

| 변이 | 죽어야 할 테스트 |
|---|---|
| `relocated`에서 `StagingSources(...)` 합류 제거 | `CheckB_StagingLineagePredicate_IsTreatedAsRelocated` |
| `StagingSources`의 `!specTargets.Contains(...)` 제거 | `CheckB_SpecTargetSourceIsNotStaging` |

변이는 반드시 원복하고 `git status --short`가 깨끗한지 확인한다.

- [ ] **Step 7: 전체 스위트와 빌드**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: 경고 0 · 실패 0 · 건너뜀 0

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "fix: 검사 B가 스테이징 적재문의 술어를 이전으로 인정한다"
```

---

## Task 3: 검사 C가 스테이징 술어를 초과로 세지 않는다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `CheckAnchoredStatementExtras`
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `ReadsOnlyStaging(StepSqlStatement, HashSet<string>)` — Task 2가 만든다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Fact]
        public void ValidateBatchStep_CheckC_StagingScopePredicate_IsNotExtra()
        {
            // (5-3-3) 부류 5. 게시문이 자기 실행이 적재한 스테이징 행만 되읽으려고
            // 거는 술어다. 원본 원천의 술어가 아니므로 명세서와 대조할 대상 자체가
            // 아니다. 지금은 BatchControlContract가 아는 RunId만 통과하고
            // ExecutionId는 발화한다 - 면제가 역할이 아니라 이름으로 걸려 있다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_A"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("INSERT", 1, 10, "TSettleByTX",
                        new[] { "YMD" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var step = new BatchStepPlan(
                Code: "S13", Name: "S13 단계",
                LegacyProcedures: new[] { "dbo.SP_A" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleByTX" },
                ErrorCodes: new[] { "-1" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            var markdown = "### S13 단계\n\n```sql\n" +
                "INSERT INTO batch_shadow.S13_After\n" +
                "SELECT M.PLTID FROM SETTLE_POQ_DB.dbo.TSettleMst AS M WHERE M.YMD = @p;\n" +
                "-- INSERT 1\n" +
                "INSERT INTO SETTLE_POQ_DB.dbo.TSettleByTX\n" +
                "SELECT PLTID FROM batch_shadow.S13_After WHERE ExecutionId = @e;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleByTX" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.DoesNotContain(result.Errors, e => e.Contains("ExecutionId"));
        }

        [Fact]
        public void ValidateBatchStep_CheckC_AddedFilterOnOriginalSource_StillFires()
        {
            // 회귀 - (5-3-4) 🔴. 이 코퍼스의 유일한 진짜 축 B 결함은 원본 원천을
            // **직접** 읽는 문장이 원본에 없는 필터를 새로 거는 것이다. 계보에
            // 스테이징이 없으므로 면제 대상이 아니고 계속 발화해야 한다.
            // 면제가 탐지력을 먹지 않는다는 것을 이 테스트가 못 박는다.
            var facts = new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["SP_A"] = new SpecStatementFacts(
                    new[] { new SpecDmlRow("INSERT", 1, 10, "TSettleMst",
                        new[] { "PLTID", "YMDCANCEL" }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()) },
                    Array.Empty<SpecSetTarget>(), Array.Empty<SpecLocalVariable>())
            };

            var step = new BatchStepPlan(
                Code: "S06", Name: "S06 단계",
                LegacyProcedures: new[] { "dbo.SP_A" },
                TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                ErrorCodes: new[] { "-1" }, Chunkable: false, SchemaTables: Array.Empty<string>());

            var markdown = "### S06 단계\n\n```sql\n" +
                "-- INSERT 1\n" +
                "INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst\n" +
                "SELECT A.PLTID FROM PaymentDB.dbo.TTxMst AS A\n" +
                "WHERE A.YMDCANCEL = @p AND A.CLVTTYPE = 1;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, step, new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null, facts);

            Assert.Contains(result.Errors, e => e.Contains("CLVTTYPE"));
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --nologo -v q --filter "FullyQualifiedName~CheckC_StagingScope|FullyQualifiedName~CheckC_AddedFilterOnOriginal"`
Expected: 첫째 FAIL(`ExecutionId` 발화), 둘째 PASS

- [ ] **Step 3: 검사 C에 배선한다**

`CheckAnchoredStatementExtras`에서 `var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);` **앞**에 추가한다.

```csharp
            var specTargets = new HashSet<string>(
                rows.Select(r => r.TargetTable), StringComparer.OrdinalIgnoreCase);
```

`extras` 계산을 바꾼다.

```csharp
                // [단계 내부 스테이징 - 대조할 원천이 아니다]
                // 게시문이 자기 실행이 적재한 스테이징 행만 되읽으려고 거는 술어는
                // 원본 원천의 술어가 아니다((5-3-3) 부류 5). 예전에는
                // BatchControlContract.Tables의 컬럼 이름을 allowed로 깔아 이 부류를
                // 면제하려 했는데, 면제가 역할이 아니라 **이름**으로 걸려 있어
                // 계약이 아는 RunId만 통과하고 ExecutionId·ProcessingYMD는 발화했다.
                // 같은 코퍼스의 POQSettleProc9/S13은 구조가 같은데 식별자를 RunId로
                // 부른다는 이유만으로 조용했다 - 발화를 가른 것이 업무적 성질이
                // 아니라 이행자가 고른 이름이었다는 증거다.
                //
                // allowed는 그대로 둔다 - 배치 제어 테이블을 **직접** 갱신하는
                // 문장은 계보와 무관하게 여전히 그 면제가 필요하다.
                var extras = group
                    .SelectMany(a => ReadsOnlyStaging(a.Statement, specTargets)
                        ? Array.Empty<string>()
                        : a.Statement.PredicateColumns)
                    .Where(c => !known.Contains(c) && !allowed.Contains(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test --nologo -v q --filter "FullyQualifiedName~CheckC_StagingScope|FullyQualifiedName~CheckC_AddedFilterOnOriginal"`
Expected: 둘 다 PASS

- [ ] **Step 5: 변이로 잠금을 확인한다**

| 변이 | 죽어야 할 테스트 |
|---|---|
| `ReadsOnlyStaging(...)` 삼항을 `a.Statement.PredicateColumns`로 되돌림 | `CheckC_StagingScopePredicate_IsNotExtra` |
| `ReadsOnlyStaging`의 `LineageSources.Count > 0` 제거 | `CheckC_AddedFilterOnOriginalSource_StillFires` |

둘째 변이가 중요하다 — `Count > 0`이 없으면 계보가 **아예 없는** 문장(원본 원천을 직접 읽는 문장)도 `All(...)`이 공허하게 참이 되어 면제된다. 그것이 (5-3-4) 🔴을 조용히 죽이는 경로다.

변이는 반드시 원복하고 `git status --short`가 깨끗한지 확인한다.

- [ ] **Step 6: 전체 스위트와 빌드**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: 경고 0 · 실패 0 · 건너뜀 0

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "fix: 검사 C가 스테이징 되읽기 술어를 초과로 세지 않는다"
```

---

## Task 4: 코퍼스 실측과 기록

**Files:**
- Create: `docs/audit-reports/sweeps/2026-08-27-step-sweep-<다음 문자>.md` (`SweepCommand`가 이름을 정한다)
- Modify: `docs/known-defects.md` — (5-3-3) 부류 3·5에 해소 기록

**Interfaces:**
- Consumes: Task 1~3의 통합 결과.

- [ ] **Step 1: 통제 워크트리를 세운다**

`ORIGINAL_BASE`는 이 브랜치를 딴 커밋이다(`git merge-base HEAD main`으로 확인).

```bash
BASE=$(git merge-base HEAD main)
git worktree add --detach .worktrees/ctl "$BASE"
ln -s "$(git rev-parse --show-toplevel)/output" .worktrees/ctl/output
ln -s "$(git rev-parse --show-toplevel)/output.bak-2026-08-22" .worktrees/ctl/output.bak-2026-08-22
```

**커밋된 스윕 보고서를 기준선으로 쓰지 않는다.** 그 사이 병합된 남의 커밋이 새 검사를 넣으면 거짓 경보가 난다.

- [ ] **Step 2: 양쪽 스윕을 돌린다**

트리가 깨끗한 상태에서 돌린다 — 「커밋: X」가 거짓이 되면 안 된다.

```bash
(cd .worktrees/ctl && dotnet run --project src/ReSet.Cli -- --sweep)
dotnet run --project src/ReSet.Cli -- --sweep
```

- [ ] **Step 3: 전문 diff로 대조한다**

```bash
A=.worktrees/ctl/docs/audit-reports/sweeps/<통제 보고서>
B=docs/audit-reports/sweeps/<이번 보고서>
norm(){ grep -E '^\| [0-9]+ \|' "$1" | sed -E 's/^\| [0-9]+ \|/|/'; }
echo "통제 $(norm $A|wc -l)  /  수정후 $(norm $B|wc -l)"
diff <(norm $A) <(norm $B) | grep '^<'   # 사라진 발화
diff <(norm $A) <(norm $B) | grep '^>'   # 새로 생긴 발화
diff <(grep -vE '^\| [0-9]+ \|' "$A") <(grep -vE '^\| [0-9]+ \|' "$B")   # 발화 표 밖
```

기대: **59 → 44** (검사 B 34 → 25, 검사 C 25 → 19).
필수: 사라지는 15건이 부류 3·5의 좌표 그대로일 것 · 새로 생기는 것 0 · 발화 표 밖 차이가 「커밋:」 줄과 개수 칸뿐일 것(분모 불변).

**어긋나면 그 자체가 조사 대상이다.** 숫자를 맞추려고 코드를 고치지 않는다.

- [ ] **Step 4: 게시문 분류 목록을 눈으로 본다**

스윕 수치와 별개로, 계보가 실제로 어디에 붙었는지 확인한다. 임시 프로브를 써서 코퍼스 전수에서 `LineageSources`가 비지 않은 문장과 그 원천 테이블을 뽑고, **진짜 업무 테이블이 한 건도 없는지** 직접 본다.

설계서 §2-1의 근사 탐침에서 원천 52종 중 최다가 `tsettlemst` 52건이었다. 명세서 대상 제외 뒤 그 이름이 남아 있으면 제외가 안 걸린 것이다.

프로브는 커밋하지 않는다. 결과 수치만 기록에 남긴다.

- [ ] **Step 5: 통제 워크트리를 지운다**

```bash
cd "$(git rev-parse --show-toplevel)"
git worktree remove --force .worktrees/ctl && git worktree prune
```

`cd`가 Bash 호출 사이에 유지된다 — 지운 디렉터리에 서 있으면 다음 명령이 통째로 실패한다.

- [ ] **Step 6: `docs/known-defects.md`에 기록한다**

(5-3-3) 부류 3과 부류 5의 제목에 `2026-08-27 해소.`를 붙이고, 각각 아래를 담은 문단을 넣는다.

- 무엇을 만들었나(단계 내부 스테이징 계보), 판정 기준 둘(「이 단계가 앞서 만들었는가」·「원본이 쓰는 테이블인가」)
- **왜 이름 규칙이 아닌가** — 실물이 `batch_shadow.`·`stage.`·`batch_work.`·`dbo.__poq_`로 제각각
- **왜 명세서 대상 제외가 필요한가** — 접근 A 반증 실측(탐침 118건, 원천 최다가 원본 대상 `tsettlemst` 52건)
- **면제가 탐지력을 먹지 않는다는 근거** — (5-3-4) 🔴의 문장은 원본 원천을 직접 읽어 계속 발화, 회귀 테스트가 고정
- 실측(통제 대조 스윕의 전후, 사라진 좌표, 새로 생긴 것 0, 분모 불변)
- 남긴 한계 셋과 **각각의 근거 세기** — 한 홉만(실물 셋 다 한 홉), 제어 흐름 미고려(침묵 방향), 「전부」 요구(느슨하면 침묵이 는다)
- 변이로 잠근 판정 목록

**한계를 「실측 0건」으로 뭉뚱그려 적지 않는다.** 잰 것과 안 잰 것을 갈라 쓴다.

- [ ] **Step 7: 커밋**

```bash
git add docs/known-defects.md docs/audit-reports/sweeps/
git commit -m "docs: 단계 내부 스테이징 계보의 코퍼스 실측과 부류 3·5 해소 기록"
```

---

## 조율 — 같은 파일을 다른 세션이 만진다

`reset-38` 세션이 같은 시기에 `MechanicalValidator.cs`를 편집한다.

```
그쪽   새 함수 CheckLegacyReturnCodeBinding + ValidateBatchStep 호출부 ~279-420행
이쪽   CheckAnchoredStatementFacts (~7106) · CheckAnchoredStatementExtras (~7313)
```

**행 구간이 떨어져 있어 병행 가능**하다는 것을 양쪽이 확인했다. 어느 쪽이 먼저
`main`에 들어가든 나머지가 리베이스한다. Task 2·3 착수 전에 `git log --oneline -5 main`으로
그쪽 커밋이 들어왔는지 확인하고, 들어왔으면 먼저 리베이스한다.

**공유 체크아웃의 브랜치를 옮기지 않는다.** `cd`가 Bash 호출 사이에 유지되어
남의 체크아웃에서 `git checkout`이 도는 사고가 이 프로젝트에서 두 번 났다.
`git` 변경 명령은 브랜치 단언과 같은 줄에 묶는다.

```bash
[ "$(git branch --show-current)" = "<내 브랜치>" ] && git commit ...
```

---

## 완료 기준

- [ ] 빌드 경고 0 · 오류 0
- [ ] 전체 스위트 실패 0 · **건너뜀 0**
- [ ] 통제 대조 스윕에서 사라진 발화가 부류 3·5의 좌표뿐이고 새로 생긴 것 0
- [ ] 계보가 붙은 문장의 원천에 원본 대상 테이블이 0건
- [ ] (5-3-4) 🔴 회귀 테스트가 통과
- [ ] 위 변이 열 개가 각각 의도한 테스트만 죽인다
- [ ] `git status --short`가 깨끗하다
