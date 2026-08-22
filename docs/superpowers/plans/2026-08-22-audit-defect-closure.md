# 축 A 재감사 결함 — 도구 수정 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 축 A 재감사에서 도구 원인으로 분류된 결함 6건(🟠1·🟡5)과 정보 4건을, 추출기 두 곳과 L1 검사 세 개로 닫는다.

**Architecture:** 프롬프트 재료를 만드는 쪽(`SchemaPromptColumnSelector`·`DatabasePlacementExtractor`) 두 곳을 고쳐 잘못된 재료가 애초에 안 나가게 하고, 모델이 재료를 옮기다 깨뜨리는 세 부류를 L1 검사로 잡는다. 새 표나 새 추출기는 만들지 않는다 — 기존 구조 안에서 입력원과 검사만 넓힌다.

**Tech Stack:** C# / .NET 10, xUnit, `dotnet test`

**Spec:** `docs/superpowers/specs/2026-08-22-audit-defect-closure-design.md`

## Global Constraints

- 프로젝트 언어는 한국어다. 주석·오류 메시지·테스트 이름 설명을 한국어로 쓴다.
- **TDD 필수.** 모든 작업은 실패하는 테스트 → 실패 확인 → 최소 구현 → 통과 확인 → 커밋 순서다. 실패를 눈으로 보지 않고 구현으로 넘어가지 않는다.
- **테스트 파일명은 단계 코드로 시작하지 않는다**(`TaskFileComposerTests` 정책).
- `AGENTS.md` 목록 항목은 **600바이트 이하**다(`DocumentationBudgetTests.NoAutoLoadedDocumentHasAnOversizedLine`).
- 최종 상태는 `dotnet test` **실패 0 · 건너뜀 0**이어야 한다.
- 잘못 지목한 L1 오류는 재생성으로 고칠 수 없다. 귀속이 불가능하면 **침묵한다**(`CheckSchemaClaims`의 정책을 새 검사도 따른다).
- 작업 브랜치에서 진행한다. `main`에 직접 커밋하지 않는다.

---

### Task 1: `SchemaPromptColumnSelector`에 DML 대상 컬럼 입력원 추가

프롬프트 스키마 표의 컬럼 필터가 INSERT/UPDATE **대상** 컬럼을 보지 않아, 오직 INSERT 대상으로만 등장하는 컬럼(`X.PRODUCTNAME` → `ProductName`)이 잘려 나간다. 모델은 그 컬럼을 "스키마에 없다"고 단정하고, L1의 기준값도 같은 잘린 집합이라 그 거짓 주장을 잡지 못한다.

**Files:**
- Modify: `src/ReSet.Core/Services/SchemaPromptColumnSelector.cs` (`Select` 메서드, 입력원 4 뒤)
- Test: `tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs`

**Interfaces:**
- Consumes: `SpDefinition.StaticAnalysis.AstInsertMappings[].TargetTable/.TargetColumns`, `.AstUpdateMappings[].TargetTable/.Assignments[].Column`, 기존 `KeyMatchesDependency(string key, DependencyInfo dep, SpDefinition spDef)`, 기존 `ExtractBaseName(string?)`
- Produces: `Select`의 반환 집합이 넓어질 뿐 시그니처는 그대로다. 뒤 작업이 의존하는 새 이름은 없다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs`에 추가한다.

```csharp
        /// <summary>
        /// 2026-08-22 축 A 재감사 실측(UP_UTIL_SETTLE_INS_EXTRA·INS_EXTRA4PLCARD).
        /// 원본이 INSERT 대상 목록에 X.PRODUCTNAME으로 적으면 파서는 그 문자열을
        /// AstInsertMappings.TargetColumns에만 담고 ReferencedColumnsPerTable에는
        /// 담지 않는다. 입력원이 참조 컬럼뿐이면 ProductName이 스키마 표에서 잘리고,
        /// 모델이 "제공된 스키마에 없는 컬럼"이라 단정한다.
        /// </summary>
        [Fact]
        public void Select_InsertTargetColumnWithAliasQualifier_KeepsBaseColumn()
        {
            var dep = new DependencyInfo
            {
                Database = "SETTLE_POQ_DB",
                Schema = "dbo",
                Name = "TSettleMst",
                Columns =
                {
                    new ColumnInfo { ColumnName = "PLTID" },
                    new ColumnInfo { ColumnName = "ProductName" }
                }
            };
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;",
                ObjectKey = new CodeObjectKey { Database = "SETTLE_POQ_DB", Schema = "dbo", Name = "UP_TEST" },
                Dependencies = { dep },
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ReferencedColumnsPerTable = new Dictionary<string, List<string>>
                    {
                        ["SETTLE_POQ_DB.dbo.TSettleMst"] = new() { "PLTID" }
                    },
                    AstInsertMappings =
                    {
                        new AstInsertMapping
                        {
                            TargetTable = "SETTLE_POQ_DB.dbo.TSettleMst",
                            TargetColumns = { "PLTID", "X.PRODUCTNAME" }
                        }
                    }
                }
            };

            var shown = SchemaPromptColumnSelector.Select(dep, spDef);

            Assert.Contains("ProductName", shown);
            Assert.Contains("PLTID", shown);
        }

        /// <summary>
        /// 반대 방향도 고정한다. 파서가 파생 테이블의 계산 컬럼을 물리 테이블에
        /// 귀속시키는 일이 있는데(실측: UF_GET_COLLECTYMD의 X.YMD), 베이스 이름을
        /// keepCols에 넣어도 그것이 실제 컬럼이 아니면 실리지 않아야 한다.
        /// Select의 반환값이 dep.Columns와의 교집합이라는 구조가 이 가드다.
        /// </summary>
        [Fact]
        public void Select_InsertTargetColumnThatIsNotARealColumn_IsNotShown()
        {
            var dep = new DependencyInfo
            {
                Database = "SETTLE_POQ_DB",
                Schema = "dbo",
                Name = "TSettleMst",
                Columns = { new ColumnInfo { ColumnName = "PLTID" } }
            };
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;",
                ObjectKey = new CodeObjectKey { Database = "SETTLE_POQ_DB", Schema = "dbo", Name = "UP_TEST" },
                Dependencies = { dep },
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ReferencedColumnsPerTable = new Dictionary<string, List<string>>
                    {
                        ["SETTLE_POQ_DB.dbo.TSettleMst"] = new() { "PLTID" }
                    },
                    AstInsertMappings =
                    {
                        new AstInsertMapping
                        {
                            TargetTable = "SETTLE_POQ_DB.dbo.TSettleMst",
                            TargetColumns = { "PLTID", "X.YMD" }
                        }
                    }
                }
            };

            var shown = SchemaPromptColumnSelector.Select(dep, spDef);

            Assert.DoesNotContain("YMD", shown);
        }
```

- [ ] **Step 2: 테스트를 돌려 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SchemaPromptColumnSelectorTests.Select_InsertTargetColumn" 2>&1 | tail -20
```

기대: `Select_InsertTargetColumnWithAliasQualifier_KeepsBaseColumn`이 `Assert.Contains() Failure`로 **실패**한다(`ProductName`이 집합에 없다). 두 번째 테스트는 통과한다 — 그것이 구조가 이미 가드라는 주장의 확인이다. 실패 이유가 컴파일 오류라면 모델 클래스 이름·프로퍼티를 실제 정의(`src/ReSet.Core/Models/SpDefinition.cs`)와 맞춘 뒤 다시 돌린다.

- [ ] **Step 3: 입력원 ⑤를 더한다**

`src/ReSet.Core/Services/SchemaPromptColumnSelector.cs`의 입력원 4 블록(주석 `// 4) 주석에만 등장하는 컬럼`으로 시작하는 `if (keepCols.Count > 0) { ... }`) **바로 뒤**, `var shown = new HashSet<string>(...)` **앞**에 넣는다.

```csharp
            // 5) INSERT/UPDATE 대상 컬럼
            //
            // 파서는 INSERT 대상 목록을 AstInsertMappings.TargetColumns에만 담고
            // ReferencedColumnsPerTable에는 담지 않는다. 그래서 오직 대상으로만
            // 등장하는 컬럼이 1~4 어디에도 걸리지 않아 스키마 표에서 잘린다
            // (실측: UP_UTIL_SETTLE_INS_EXTRA의 X.PRODUCTNAME → ProductName).
            // 원문과 베이스 이름을 둘 다 넣는 것은 입력원 1과 같은 이유다.
            // 실제 컬럼이 아닌 이름을 넣어도 아래 shown이 dep.Columns와의
            // 교집합이라 실리지 않는다 - 가드를 따로 두지 않는 근거다.
            if (analysis != null)
            {
                foreach (var mapping in analysis.AstInsertMappings)
                {
                    if (!KeyMatchesDependency(mapping.TargetTable, dep, spDef)) continue;
                    foreach (var c in mapping.TargetColumns)
                    {
                        keepCols.Add(c);
                        keepCols.Add(ExtractBaseName(c));
                    }
                }

                foreach (var mapping in analysis.AstUpdateMappings)
                {
                    if (!KeyMatchesDependency(mapping.TargetTable, dep, spDef)) continue;
                    foreach (var assignment in mapping.Assignments)
                    {
                        keepCols.Add(assignment.Column);
                        keepCols.Add(ExtractBaseName(assignment.Column));
                    }
                }
            }
```

- [ ] **Step 4: 테스트를 돌려 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SchemaPromptColumnSelectorTests" 2>&1 | tail -5
```

기대: 실패 0. 이 파일의 기존 테스트도 전부 통과해야 한다(입력원을 넓혔으므로 "잘려야 할 컬럼이 실린다"는 기존 고정이 있으면 그 테스트가 깨진다 — 깨지면 멈추고 그 테스트의 의도를 읽은 뒤 보고한다).

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/SchemaPromptColumnSelector.cs tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs
git commit -m "fix: 스키마 표 컬럼 필터가 INSERT·UPDATE 대상 컬럼을 입력원으로 본다"
```

---

### Task 2: `DatabasePlacementExtractor`가 소속 DB 안과 밖을 가른다

3부 식별자 참조 목록을 소속 DB와 비교하지 않고 전부 "그 밖"으로 문장화한다. 감사 네 객체에서 홈 DB 참조가 크로스 DB로 실렸다. 소속 DB를 **모를 때**의 갈래는 이미 옳으므로 건드리지 않는다.

**Files:**
- Modify: `src/ReSet.Core/Services/DatabasePlacementExtractor.cs` (마지막 `return` 문)
- Test: `tests/ReSet.Core.Tests/ExecutionSemanticsFactsTests.cs`

**Interfaces:**
- Consumes: `DatabasePlacementExtractor.Extract(SpStaticAnalysisResult?, CodeObjectKey?)` → `DatabasePlacementFact?` (기존 시그니처 유지)
- Produces: 문장 문구만 달라진다. 새 타입·새 메서드 없음.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/ExecutionSemanticsFactsTests.cs`에 추가한다.

```csharp
        /// <summary>
        /// 2026-08-22 축 A 재감사 실측(EXPECT_PROC·INS_EXTRA4PLCARD·AcqManual·COLLECTYMD).
        /// 3부 식별자 참조에는 소속 DB를 3부로 적은 것도 섞인다. 전부 "그 밖"으로
        /// 문장화하면 명세서가 그 확정 문장을 그대로 베껴 홈 DB 참조가 크로스 DB로
        /// 읽힌다 - 이 표는 "수정 금지"라 산문이 바로잡을 수도 없다.
        /// </summary>
        [Fact]
        public void DatabasePlacement_ThreePartReferencesInsideHomeDatabase_AreNotCalledOutside()
        {
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ThreePartObjectReferences =
                {
                    "SETTLE_POQ_DB.dbo.TSettleMst",
                    "SETTLE_CARD_DB.dbo.TCardMst"
                }
            };
            var objectKey = new CodeObjectKey
            {
                Database = "SETTLE_POQ_DB", Schema = "dbo", Name = "UP_TEST"
            };

            var fact = DatabasePlacementExtractor.Extract(analysis, objectKey);

            Assert.NotNull(fact);
            // 밖인 것만 "그 밖" 목록에 있어야 한다.
            var outsideSegment = fact!.Sentence[fact.Sentence.IndexOf("그 밖", StringComparison.Ordinal)..];
            Assert.Contains("SETTLE_CARD_DB.dbo.TCardMst", outsideSegment);
            Assert.DoesNotContain("SETTLE_POQ_DB.dbo.TSettleMst", outsideSegment);
        }

        /// <summary>
        /// 3부 참조가 전부 소속 DB 안이면 "그 밖"이라는 분류어 자체가 나오면 안 된다.
        /// </summary>
        [Fact]
        public void DatabasePlacement_AllThreePartReferencesInsideHome_SaysNoneOutside()
        {
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ThreePartObjectReferences = { "SETTLE_POQ_DB.dbo.TSettleMst" }
            };
            var objectKey = new CodeObjectKey
            {
                Database = "SETTLE_POQ_DB", Schema = "dbo", Name = "UP_TEST"
            };

            var fact = DatabasePlacementExtractor.Extract(analysis, objectKey);

            Assert.NotNull(fact);
            Assert.DoesNotContain("그 밖", fact!.Sentence);
            Assert.Contains("SETTLE_POQ_DB.dbo.TSettleMst", fact.Sentence);
        }

        /// <summary>
        /// 소속 DB 이름을 모르는 갈래는 이미 옳다(분류어 없이 건수·목록만). 회귀 고정.
        /// </summary>
        [Fact]
        public void DatabasePlacement_HomeDatabaseUnknown_KeepsUnclassifiedSentence()
        {
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ThreePartObjectReferences = { "SETTLE_CARD_DB.dbo.TCardMst" }
            };

            var fact = DatabasePlacementExtractor.Extract(analysis, objectKey: null);

            Assert.NotNull(fact);
            Assert.Contains("소속 DB 이름은 미상입니다", fact!.Sentence);
            Assert.DoesNotContain("그 밖", fact.Sentence);
        }
```

- [ ] **Step 2: 테스트를 돌려 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~DatabasePlacement_" 2>&1 | tail -20
```

기대: 첫 둘이 **실패**한다(홈 DB 참조가 "그 밖" 목록에 섞여 있다). 셋째는 통과한다.

- [ ] **Step 3: 홈 DB 분할을 구현한다**

`src/ReSet.Core/Services/DatabasePlacementExtractor.cs`의 마지막 `return`을 아래로 바꾼다. 그 위의 `if (!hasHome)` 블록과 `parts` 조립은 그대로 둔다.

```csharp
            // 3부 식별자 참조에는 소속 DB를 3부로 적은 것도 섞인다(SqlStaticParser는
            // 원문 부분 수만 보고 전부 담는다). 소속 DB를 아는 이 갈래에서는 가를 수
            // 있으므로 가른다 - 전부 "그 밖"으로 적으면 홈 DB 참조가 크로스 DB로
            // 읽히고, 이 표는 "수정 금지"라 산문이 바로잡을 수도 없다
            // (2026-08-22 축 A 재감사, 네 객체 실측).
            var homePrefix = home + ".";
            var inside = threePart
                .Where(r => r.StartsWith(homePrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var outside = threePart
                .Where(r => !r.StartsWith(homePrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var segments = new List<string>();
            if (outside.Count > 0)
            {
                segments.Add($"3부 식별자 참조 {outside.Count}건: {string.Join(", ", outside)}");
            }
            if (linked.Count > 0)
            {
                segments.Add($"연결 서버 참조 {linked.Count}건: {string.Join(", ", linked)}");
            }

            if (segments.Count == 0)
            {
                // 3부 표기가 있었지만 전부 소속 DB 안이고 연결 서버도 없다.
                return new DatabasePlacementFact(
                    $"소속 DB는 `{home}`이고 소속 DB 밖 참조는 없습니다 — "
                    + $"소속 DB를 3부로 적은 참조 {inside.Count}건: {string.Join(", ", inside)}. "
                    + "확정값입니다.");
            }

            var insideNote = inside.Count > 0
                ? $" 소속 DB를 3부로 적은 참조 {inside.Count}건은 그 밖이 아닙니다: {string.Join(", ", inside)}."
                : string.Empty;

            return new DatabasePlacementFact(
                $"소속 DB는 `{home}`이고 다음은 그 밖입니다 — {string.Join(" / ", segments)}.{insideNote}");
```

파일 맨 위에 `using System.Linq;`가 없으면 더한다.

- [ ] **Step 4: 테스트를 돌려 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~ExecutionSemanticsFactsTests" 2>&1 | tail -5
```

기대: 실패 0. `MechanicalValidatorTests`의 실행 의미 관련 기존 테스트가 이 문장을 리터럴로 고정하고 있으면 함께 깨진다 — 깨지면 그 테스트의 기대 문자열을 새 문장으로 갱신하고, 갱신한 이유를 커밋 메시지에 적는다.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/DatabasePlacementExtractor.cs tests/ReSet.Core.Tests/ExecutionSemanticsFactsTests.cs
git commit -m "fix: DB 배치 문장이 소속 DB 안과 밖을 가른다"
```

---

### Task 3: L1 — 기계 확정 표의 마크다운 형태 검사

`UP_UTIL_STAT_PGCOLLECT_INS`에서 「DML 범위」 표의 구분 행이 7칸인데 헤더·데이터 행은 8칸이었다. GFM은 헤더와 구분 행의 셀 수가 다르면 표로 인식하지 않아, 확정값 전체가 평문으로 무너진다. 값은 맞는데 렌더링이 죽는 부류라 기존 행 대조 검사가 잡지 못한다.

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ErrorType` enum, `Validate` 호출부, 새 검사 메서드)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `MachineConfirmedTables.All` (각 항목의 `Heading`), 기존 `MarkdownSectionLocator.SplitLines`, 기존 `LocateHeadingSection(IReadOnlyList<string> lines, string heading)`
- Produces: `ErrorType.MachineTableShapeBroken` — Task 4·5는 각자 자기 enum 값을 더하므로 이 값에 의존하지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`에 추가한다.

```csharp
        /// <summary>
        /// 2026-08-22 축 A 재감사 실측(UP_UTIL_STAT_PGCOLLECT_INS). 구분 행의 셀 수가
        /// 헤더와 다르면 GFM이 표로 인식하지 않아 "수정 금지" 표가 통째로 평문이 된다.
        /// 행 내용 대조 검사들은 값만 보므로 이 부류를 잡지 못한다.
        /// </summary>
        [Fact]
        public void Validate_MachineTableWithMismatchedSeparatorCells_IsReported()
        {
            var markdown = BuildMinimalSpec()
                + "\n" + DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 |\n"
                + "| :--- | :--- |\n"
                + "| INSERT 1 | 55 | dbo.T |\n";

            var result = new MechanicalValidator().Validate(markdown);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.MachineTableShapeBroken);
        }

        /// <summary>셀 수가 맞는 표는 통과한다 - 오탐 고정.</summary>
        [Fact]
        public void Validate_MachineTableWithMatchingSeparatorCells_IsNotReported()
        {
            var markdown = BuildMinimalSpec()
                + "\n" + DmlScopeExtractor.DmlScopeTableHeading + "\n"
                + "| 문장 | 라인 | 대상 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| INSERT 1 | 55 | dbo.T |\n";

            var result = new MechanicalValidator().Validate(markdown);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.MachineTableShapeBroken);
        }
```

`BuildMinimalSpec()`은 이 테스트 파일에 이미 있는 헬퍼를 쓴다. 없으면 파일에서 다섯 필수 H2를 포함한 최소 마크다운을 만드는 기존 헬퍼 이름을 찾아 그것을 쓰고, 그런 헬퍼가 없으면 아래를 이 테스트 파일에 더한다.

```csharp
        private static string BuildMinimalSpec() =>
            "## 개요\n\n## 파라미터 목록\n\n## CRUD 분석\n\n## 로직 흐름 요약\n\n## 비즈니스 흐름 시각화\n";
```

- [ ] **Step 2: 테스트를 돌려 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~Validate_MachineTableWith" 2>&1 | tail -20
```

기대: 첫 테스트가 **컴파일 오류**(`ErrorType.MachineTableShapeBroken` 없음)로 떨어진다. Step 3에서 enum 값을 더한 뒤 다시 돌려 `Assert.Contains() Failure`로 **실패**하는 것을 확인하고 나서 Step 4로 간다.

- [ ] **Step 3: `ErrorType`에 값을 더한다**

`src/ReSet.Core/Services/MechanicalValidator.cs`의 `ErrorType` enum에서 `CaseBranchTableMissing` 바로 뒤, `General` 앞에 넣는다.

```csharp
        // 기계 확정 표가 GFM 표로 렌더링되지 않는 형태로 옮겨졌을 때의 L1 앵커.
        // 위와 같은 이유로 서수 이동은 기능에 영향이 없다.
        MachineTableShapeBroken,
```

- [ ] **Step 4: 실패를 다시 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~Validate_MachineTableWith" 2>&1 | tail -20
```

기대: `Validate_MachineTableWithMismatchedSeparatorCells_IsReported`가 `Assert.Contains() Failure`로 실패한다. 두 번째는 통과한다.

- [ ] **Step 5: 검사를 구현한다**

`src/ReSet.Core/Services/MechanicalValidator.cs`에 메서드를 더한다(`CheckCaseBranches` 뒤가 자연스럽다).

```csharp
        /// <summary>
        /// 기계 확정 표가 GFM 표로 렌더링되는 형태인지 본다.
        ///
        /// [왜 행 내용 대조로는 부족한가] 값이 전부 맞아도 구분 행의 셀 수가 헤더와
        /// 다르면 GFM이 표로 인식하지 않는다. 그러면 "수정 금지"로 못 박은 확정값이
        /// 평문 한 덩어리가 되어 이행 담당자가 표로 읽지 못한다
        /// (2026-08-22 축 A 재감사, UP_UTIL_STAT_PGCOLLECT_INS 실측).
        ///
        /// [왜 카탈로그를 도는가] MachineConfirmedTables.All이 표 목록의 단일 출처다.
        /// 표가 늘면 이 검사가 따로 손대지 않아도 따라온다.
        ///
        /// [왜 expectations를 받지 않는가] 재료 없이 마크다운만으로 판정되므로
        /// 재료가 없는 갈래에서도 돈다.
        ///
        /// [자기 try/catch를 두는 이유] Validate의 catch-all은 검사 하나가 던지면
        /// Errors를 통째로 지우고 통과시킨다. 새 검사의 실패가 기존 검사의 판정까지
        /// 삼키면 안 된다.
        /// </summary>
        private static void CheckMachineTableShape(string markdown, ValidationResult result)
        {
            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);

                foreach (var table in MachineConfirmedTables.All)
                {
                    var (headingIndex, endIndex) = LocateHeadingSection(lines, table.Heading);
                    if (headingIndex < 0) continue;

                    var rows = new List<string>();
                    for (var i = headingIndex + 1; i < endIndex; i++)
                    {
                        if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                        {
                            rows.Add(lines[i]);
                        }
                    }

                    if (rows.Count < 2) continue;

                    var headerCells = SplitTableRowCells(rows[0]).Count;
                    for (var i = 1; i < rows.Count; i++)
                    {
                        var cells = SplitTableRowCells(rows[i]).Count;
                        if (cells == headerCells) continue;

                        var message =
                            $"`{table.Heading}` 표의 {i + 1}번째 행이 {cells}칸인데 헤더 행은 "
                            + $"{headerCells}칸입니다. 셀 수가 다르면 표로 렌더링되지 않아 확정값이 "
                            + "평문으로 무너집니다. 헤더와 같은 칸 수로 옮기십시오.";
                        result.Errors.Add(message);
                        result.DetailedErrors.Add(new DetailedError
                        {
                            Type = ErrorType.MachineTableShapeBroken,
                            Message = message,
                            RawContext = rows[i]
                        });
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] 기계 확정 표 형태 검사 실패 - 이 검사만 건너뜁니다.");
            }
        }
```

`Validate` 본문에서 `CheckPromptInstructionLeak(cleansed, result);` 바로 다음 줄에 호출을 더한다(`expectations != null` 블록 **밖**이다).

```csharp
                CheckMachineTableShape(cleansed, result);
```

- [ ] **Step 6: 테스트를 돌려 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests" 2>&1 | tail -5
```

기대: 실패 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: L1이 기계 확정 표의 셀 수 불일치를 잡는다"
```

---

### Task 4: L1 — INSERT 매핑 표의 대상 테이블명 대조

`UP_UTIL_SETTLE_SUMMARY_EXTRA`의 매핑 표 한 행이 `TSetTleByOUT`으로 적혔다. 실측된 오타가 **대소문자만 다른 경우**라 Ordinal로 대조해야 잡힌다. SQL Server 기본 콜레이션에서 실행은 무해하지만, 매핑 표를 식별자 원천으로 삼는 이행·grep·자동 대조가 어긋난다.

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs` (필드 추가 + `From`에서 채움)
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ErrorType`, `Validate` 호출부, 새 검사)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `SpDefinition.StaticAnalysis.InsertTables` (`List<string>`)
- Produces: `SpecExpectations.InsertTargetTables` (`IReadOnlyList<string>`, 기본값 `Array.Empty<string>()`), `ErrorType.InsertMappingTableNameMismatch`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        /// <summary>
        /// 2026-08-22 축 A 재감사 실측(UP_UTIL_SETTLE_SUMMARY_EXTRA). 매핑 표 한 행이
        /// TSetTleByOUT으로 적혔다 - 대소문자만 다르다. 실행은 무해하지만 매핑 표를
        /// 식별자 원천으로 삼는 이행·grep·자동 대조가 그 행에서 어긋난다.
        /// 대소문자를 무시하면 이 검사가 잡아야 할 것을 못 잡으므로 Ordinal로 본다.
        /// </summary>
        [Fact]
        public void Validate_InsertMappingTableNameDiffersOnlyByCase_IsReported()
        {
            var expectations = BuildExpectationsWithInsertTargets("SETTLE_POQ_DB.dbo.TSettleByOUT");
            var markdown = BuildMinimalSpec()
                + "\n### INSERT 대상 테이블\n"
                + "| 테이블명 | 컬럼명 | 원천 데이터 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| SETTLE_POQ_DB.dbo.TSetTleByOUT | OUTCNT | COUNT(*) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.InsertMappingTableNameMismatch);
        }

        /// <summary>표기가 정확히 같으면 통과한다 - 오탐 고정.</summary>
        [Fact]
        public void Validate_InsertMappingTableNameExact_IsNotReported()
        {
            var expectations = BuildExpectationsWithInsertTargets("SETTLE_POQ_DB.dbo.TSettleByOUT");
            var markdown = BuildMinimalSpec()
                + "\n### INSERT 대상 테이블\n"
                + "| 테이블명 | 컬럼명 | 원천 데이터 |\n"
                + "| :--- | :--- | :--- |\n"
                + "| SETTLE_POQ_DB.dbo.TSettleByOUT | OUTCNT | COUNT(*) |\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.InsertMappingTableNameMismatch);
        }

        private static SpecExpectations BuildExpectationsWithInsertTargets(params string[] tables)
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;",
                ObjectKey = new CodeObjectKey { Database = "SETTLE_POQ_DB", Schema = "dbo", Name = "UP_TEST" },
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    InsertTables = new List<string>(tables)
                }
            };
            return SpecExpectations.From(spDef)!;
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~Validate_InsertMappingTableName" 2>&1 | tail -20
```

기대: 컴파일 오류(`ErrorType.InsertMappingTableNameMismatch` 없음). Step 3~4를 마친 뒤 다시 돌려 `Assert.Contains() Failure`로 실패하는 것을 확인하고 Step 5로 간다.

- [ ] **Step 3: `SpecExpectations`에 필드를 더한다**

`src/ReSet.Core/Services/SpecExpectations.cs`의 다른 `init` 속성들 옆에 넣는다.

```csharp
        /// <summary>
        /// 파서가 확정한 INSERT 대상 테이블(canonical 표기). 매핑 표의 테이블명 칸이
        /// 이것과 표기까지 같은지 대조하는 기준이다.
        /// </summary>
        public IReadOnlyList<string> InsertTargetTables { get; init; } = Array.Empty<string>();
```

`From`의 `return new SpecExpectations(...) { ... }` 초기화 목록에 한 줄을 더한다.

```csharp
                InsertTargetTables = spDef.StaticAnalysis?.InsertTables is { Count: > 0 } insertTables
                    ? new List<string>(insertTables)
                    : Array.Empty<string>(),
```

- [ ] **Step 4: `ErrorType`에 값을 더한다**

`MachineTableShapeBroken` 뒤, `General` 앞에 넣는다.

```csharp
        // INSERT 매핑 표의 테이블명 표기 어긋남의 L1 앵커.
        InsertMappingTableNameMismatch,
```

- [ ] **Step 5: 검사를 구현한다**

`src/ReSet.Core/Services/MechanicalValidator.cs`에 더한다.

```csharp
        /// <summary>
        /// INSERT 매핑 표의 테이블명 칸이 파서가 확정한 대상 테이블과 표기까지 같은지 본다.
        ///
        /// [왜 Ordinal인가] 실측된 오타가 대소문자만 다른 경우다(TSetTleByOUT 대
        /// TSettleByOUT, 2026-08-22 축 A 재감사). 대소문자를 무시하면 이 검사가
        /// 잡아야 할 것을 정확히 못 잡는다. 실행은 무해해도 매핑 표를 식별자 원천으로
        /// 삼는 이행·grep·자동 대조가 그 행에서 어긋난다.
        ///
        /// [왜 말단 이름으로 비교하는가] 명세서가 3부·2부·비한정 어느 표기를 쓸지는
        /// 문서마다 다르다. 말단 이름이 같은데 표기 폭만 다른 것은 결함이 아니므로,
        /// 말단 이름이 대소문자까지 같은지만 본다. 귀속이 불가능하면(말단 이름이
        /// 어느 대상과도 안 맞으면) 침묵한다 - 잘못 지목한 오류는 재생성으로
        /// 고칠 수 없다(CheckSchemaClaims의 정책).
        /// </summary>
        private static void CheckInsertMappingTableNames(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.InsertTargetTables.Count == 0) return;

            try
            {
                var expectedLeaves = expectations.InsertTargetTables
                    .Select(t => t.Split('.')[^1])
                    .ToList();

                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var reported = new HashSet<string>(StringComparer.Ordinal);

                foreach (var line in lines)
                {
                    if (!line.TrimStart().StartsWith("|", StringComparison.Ordinal)) continue;

                    var cells = SplitTableRowCells(line);
                    if (cells.Count == 0) continue;

                    var candidate = cells[0].Trim();
                    if (candidate.Length == 0) continue;

                    var leaf = candidate.Split('.')[^1];
                    if (expectedLeaves.Any(e => string.Equals(e, leaf, StringComparison.Ordinal))) continue;

                    var caseOnly = expectedLeaves.FirstOrDefault(
                        e => string.Equals(e, leaf, StringComparison.OrdinalIgnoreCase));
                    if (caseOnly == null) continue;
                    if (!reported.Add(leaf)) continue;

                    var message =
                        $"INSERT 매핑 표의 테이블명 `{candidate}`이 파서가 확정한 표기 `{caseOnly}`와 "
                        + "대소문자가 다릅니다. 실행은 무해하지만 이 표를 식별자 원천으로 삼는 "
                        + "이행·대조가 어긋납니다. 원문 표기 그대로 옮기십시오.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.InsertMappingTableNameMismatch,
                        Message = message,
                        RawContext = candidate
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] INSERT 매핑 표 테이블명 대조 실패 - 이 검사만 건너뜁니다.");
            }
        }
```

`Validate`의 `expectations != null` 블록에서 `CheckUpdateMappings(cleansed, expectations, result);` 다음 줄에 호출을 더한다.

```csharp
                    CheckInsertMappingTableNames(cleansed, expectations, result);
```

- [ ] **Step 6: 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests|FullyQualifiedName~SpecExpectationsTests" 2>&1 | tail -5
```

기대: 실패 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/SpecExpectations.cs src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: L1이 INSERT 매핑 표의 테이블명 표기 어긋남을 잡는다"
```

---

### Task 5: L1 — 컬럼 널 허용 주장 대조

`UF_GET_COMM4PG4INTEREST`가 필터 컬럼 `UseState`를 "널을 허용하지 않습니다"로 단정했으나 메타데이터는 `IsNullable: true`, 기본값 `((0))`이다. 이행 스키마에 NOT NULL 제약을 세우거나 필터를 바꾸면 원본이 3값 논리로 배제하던 행이 대상에 들어와 금액이 바뀐다.

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs` (필드 추가 + `From`에서 채움)
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ErrorType`, `Validate` 호출부, 새 검사)
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `DependencyInfo.Columns[].ColumnName`, `.IsNullable`
- Produces: `SpecExpectations.NullableColumnNames` (`IReadOnlySet<string>`, 컬럼 말단 이름 집합), `ErrorType.NullabilityClaimMismatch`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        /// <summary>
        /// 2026-08-22 축 A 재감사 실측(UF_GET_COMM4PG4INTEREST). 필터 컬럼 UseState는
        /// IsNullable이 true인데 명세서가 "널을 허용하지 않습니다"로 단정했다. 이
        /// 단정을 근거로 이행 스키마에 NOT NULL을 세우면 원본이 3값 논리로 배제하던
        /// 행이 대상에 들어와 금액이 바뀐다.
        /// </summary>
        [Fact]
        public void Validate_NotNullClaimOnNullableColumn_IsReported()
        {
            var expectations = BuildExpectationsWithNullableColumn("UseState", isNullable: true);
            var markdown = BuildMinimalSpec()
                + "\n`UseState`는 `tinyint`이며 널을 허용하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.NullabilityClaimMismatch);
        }

        /// <summary>실제로 NOT NULL인 컬럼에 대한 같은 문장은 통과한다 - 오탐 고정.</summary>
        [Fact]
        public void Validate_NotNullClaimOnNotNullColumn_IsNotReported()
        {
            var expectations = BuildExpectationsWithNullableColumn("IsPGFlag", isNullable: false);
            var markdown = BuildMinimalSpec()
                + "\n`IsPGFlag`는 `tinyint`이며 널을 허용하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.NullabilityClaimMismatch);
        }

        /// <summary>
        /// 어느 의존성 컬럼에도 귀속되지 않는 이름은 침묵한다. 잘못 지목한 오류는
        /// 재생성으로 고칠 수 없다는 CheckSchemaClaims의 정책을 그대로 따른다.
        /// </summary>
        [Fact]
        public void Validate_NotNullClaimOnUnknownIdentifier_IsSilent()
        {
            var expectations = BuildExpectationsWithNullableColumn("UseState", isNullable: true);
            var markdown = BuildMinimalSpec()
                + "\n`배치작업ID`는 널을 허용하지 않습니다.\n";

            var result = new MechanicalValidator().Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.NullabilityClaimMismatch);
        }

        private static SpecExpectations BuildExpectationsWithNullableColumn(string column, bool isNullable)
        {
            var dep = new DependencyInfo
            {
                Database = "SETTLE_POQ_DB",
                Schema = "dbo",
                Name = "TTest",
                Columns = { new ColumnInfo { ColumnName = column, IsNullable = isNullable } }
            };
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;",
                ObjectKey = new CodeObjectKey { Database = "SETTLE_POQ_DB", Schema = "dbo", Name = "UP_TEST" },
                Dependencies = { dep },
                StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true }
            };
            return SpecExpectations.From(spDef)!;
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~Validate_NotNullClaim" 2>&1 | tail -20
```

기대: 컴파일 오류. Step 3~4 뒤 다시 돌려 첫 테스트가 `Assert.Contains() Failure`로 실패하고 나머지 둘이 통과하는 것을 확인한 뒤 Step 5로 간다.

- [ ] **Step 3: `SpecExpectations`에 필드를 더한다**

```csharp
        /// <summary>
        /// 의존성 스키마가 널 허용으로 확정한 컬럼의 말단 이름. 명세서가 "널을
        /// 허용하지 않습니다"로 단정한 줄을 대조하는 기준이다. 컬럼 이름이 여러
        /// 테이블에 걸쳐 같으면 널 허용 여부가 갈릴 수 있으므로, 갈리는 이름은
        /// 담지 않는다 - 담으면 참인 서술이 오류로 지목된다.
        /// </summary>
        public IReadOnlySet<string> NullableColumnNames { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
```

`From` 본문에서 `return new SpecExpectations(...)` **앞**에 아래를 넣고, 초기화 목록에 `NullableColumnNames = nullableColumnNames,`를 더한다.

```csharp
            // 같은 컬럼 이름이 테이블마다 널 허용 여부가 다르면 어느 쪽도 기준이 될 수
            // 없다. 그런 이름은 빼서 침묵시킨다 - 참인 서술을 오류로 지목하면
            // 재생성으로 고칠 수 없다.
            var nullableByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var ambiguousNullability = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dep in spDef.Dependencies)
            {
                foreach (var col in dep.Columns)
                {
                    if (string.IsNullOrWhiteSpace(col.ColumnName)) continue;
                    if (nullableByName.TryGetValue(col.ColumnName, out var known))
                    {
                        if (known != col.IsNullable) ambiguousNullability.Add(col.ColumnName);
                        continue;
                    }
                    nullableByName[col.ColumnName] = col.IsNullable;
                }
            }
            var nullableColumnNames = new HashSet<string>(
                nullableByName.Where(kv => kv.Value && !ambiguousNullability.Contains(kv.Key))
                    .Select(kv => kv.Key),
                StringComparer.OrdinalIgnoreCase);
```

- [ ] **Step 4: `ErrorType`에 값을 더한다**

```csharp
        // 컬럼 널 허용 주장 어긋남의 L1 앵커.
        NullabilityClaimMismatch,
```

- [ ] **Step 5: 검사를 구현한다**

```csharp
        /// <summary>
        /// 명세서가 "널을 허용하지 않습니다"로 단정한 컬럼이 실제로는 널 허용인지 본다.
        ///
        /// [왜 한 방향만 보는가] 널 허용인데 NOT NULL로 단정하는 쪽만 위험하다 -
        /// 그 단정을 근거로 이행 스키마에 제약을 세우거나 필터를 바꾸면 원본이
        /// 3값 논리로 배제하던 행이 대상에 들어온다(2026-08-22 축 A 재감사,
        /// UF_GET_COMM4PG4INTEREST의 UseState). 반대 방향은 과한 방어라 무해하다.
        ///
        /// [귀속 불가 시 침묵] 백틱 식별자가 의존성 컬럼으로 해석되지 않으면 넘어간다.
        /// 잘못 지목한 오류는 재생성으로 고칠 수 없고, 그것이 이 저장소가 무한
        /// 재시도로 겪은 실패다(CheckSchemaClaims 주석).
        /// </summary>
        private static void CheckNullabilityClaims(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.NullableColumnNames.Count == 0) return;

            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var fenceFlags = ComputeFenceLineFlags(lines);
                var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    if (fenceFlags[lineIndex]) continue;

                    var line = lines[lineIndex];
                    if (!line.Contains("널을 허용하지 않습니다", StringComparison.Ordinal)
                        && !line.Contains("NOT NULL", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (Match match in BacktickIdentifierRegex.Matches(line))
                    {
                        var identifier = match.Groups[1].Value.Trim();
                        var leaf = identifier.Split('.')[^1];
                        if (!expectations.NullableColumnNames.Contains(leaf)) continue;
                        if (!reported.Add(leaf)) continue;

                        var message =
                            $"명세서가 `{leaf}` 컬럼을 널 불허로 단정했으나 의존성 스키마는 널 허용으로 "
                            + "확정했습니다. 이 단정을 근거로 제약을 세우거나 필터를 바꾸면 원본이 "
                            + "배제하던 NULL 행이 대상에 들어옵니다.";
                        result.Errors.Add(message);
                        result.DetailedErrors.Add(new DetailedError
                        {
                            Type = ErrorType.NullabilityClaimMismatch,
                            Message = message,
                            RawContext = line.Trim()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] 널 허용 주장 대조 실패 - 이 검사만 건너뜁니다.");
            }
        }
```

`Validate`의 `expectations != null` 블록에서 `CheckSchemaClaims(cleansed, expectations, result);` 다음 줄에 호출을 더한다.

```csharp
                    CheckNullabilityClaims(cleansed, expectations, result);
```

- [ ] **Step 6: 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests|FullyQualifiedName~SpecExpectationsTests" 2>&1 | tail -5
```

기대: 실패 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/SpecExpectations.cs src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: L1이 널 허용 컬럼의 NOT NULL 단정을 잡는다"
```

---

### Task 6: 캐시 버전 10과 문서 반영

Task 1·2가 프롬프트 입력(스키마 표 컬럼 집합 · `DB 배치` 문장)을 바꾼다. 옛 엔트리를 재사용하면 틀린 재료로 만든 산출물이 그대로 남고, Task 3~5가 세운 L1 검사도 캐시 히트에서는 발동하지 않는다.

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs`
- Modify: `docs/architecture.md` (§4.9)
- Modify: `AGENTS.md` (범주 7)
- Test: 기존 `dotnet test` 전체

**Interfaces:**
- Consumes: 없음
- Produces: `CacheManager.CurrentCacheFormatVersion = 10`

- [ ] **Step 1: 캐시 버전을 올린다**

`src/ReSet.Core/Services/CacheManager.cs`에서 `private const int CurrentCacheFormatVersion = 9;`를 `10`으로 바꾸고, 9번 주석 바로 아래에 기존 양식대로 더한다.

```csharp
        // 10: 프롬프트 스키마 표의 컬럼 필터가 INSERT·UPDATE 대상 컬럼을 입력원으로
        //     보게 됐고(오직 대상으로만 등장하는 컬럼이 잘려 모델이 "스키마에 없다"고
        //     단정하던 결함), 실행 의미 표의 DB 배치 문장이 소속 DB 안과 밖을 가른다.
        //     둘 다 프롬프트 입력이 달라진 것이므로 옛 엔트리를 재사용하면 틀린 재료로
        //     만든 산출물이 그대로 남는다. 이 회차가 세운 L1 검사 셋(표 셀 수·매핑 표
        //     테이블명·널 허용 주장)도 캐시 히트에서는 영영 발동하지 않는다.
        //     2026-08-22 축 A 재감사 실측 6결함이 근거다.
```

- [ ] **Step 2: 전체 테스트를 돌린다**

```bash
dotnet test 2>&1 | tail -5
```

기대: 실패 0 · 건너뜀 0. `CacheManagerTests`가 버전 숫자를 리터럴로 고정하고 있으면 깨진다 — 깨지면 그 기대값을 10으로 갱신한다.

- [ ] **Step 3: 문서를 반영한다**

`docs/architecture.md` §4.9의 "기계 확정 표 원문 복사 지시" 항목 뒤에 한 문단을 더한다.

```markdown
* **재료가 잘리지 않게 하는 쪽의 계약**: 확정 사실을 표로 싣는 것만으로는 부족하다. 프롬프트 스키마 표의 컬럼 필터가 INSERT·UPDATE 대상 컬럼을 입력원으로 보지 않으면, 오직 대상으로만 등장하는 컬럼이 잘려 모델이 그것을 "스키마에 없다"고 단정하고 L1의 기준값도 같은 잘린 집합이라 그 거짓 주장을 잡지 못합니다(2026-08-22 축 A 재감사 실측). 「실행 의미」의 `DB 배치` 문장도 3부 식별자 참조를 소속 DB와 비교해 안과 밖을 가릅니다 — 가르지 않으면 홈 DB 참조가 크로스 DB로 읽히고, 이 표는 "수정 금지"라 산문이 바로잡을 수도 없습니다.
```

`AGENTS.md` 범주 7의 L1 관련 항목 뒤에 한 줄을 더한다. **600바이트를 넘지 않게** 쓴다.

```markdown
    *   L1은 기계 확정 표의 셀 수, INSERT 매핑 표의 테이블명 표기(Ordinal), 널 허용 주장을 함께 봅니다. 프롬프트 스키마 표의 컬럼 필터는 INSERT·UPDATE 대상 컬럼도 입력원으로 삼으십시오(`architecture.md §4.9`).
```

- [ ] **Step 4: 문서 예산 검사를 포함해 전체 테스트를 돌린다**

```bash
dotnet test 2>&1 | tail -5
```

기대: 실패 0 · 건너뜀 0. `DocumentationBudgetTests`가 걸리면 `AGENTS.md` 줄을 600바이트 이하로 줄인다.

```bash
python3 -c "import io;print(max((len(l.encode()),l[:40]) for l in io.open('AGENTS.md',encoding='utf-8') if l.startswith('    *')))"
```

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/CacheManager.cs docs/architecture.md AGENTS.md
git commit -m "chore: 캐시 버전 10으로 올리고 재료·검사 계약을 문서에 반영한다"
```

---

### Task 7: 재생성으로 실제로 닫혔는지 확인한다

단위 테스트는 "검사가 돈다"까지만 증명한다. 결함이 닫혔는지는 산출물에서 본다.

**Files:**
- 변경 없음. 관측만 한다.

**Interfaces:**
- Consumes: Task 1~6의 결과 전부
- Produces: 확인 결과(커밋 없음, 보고만)

- [ ] **Step 1: 현재 산출물을 백업한다**

```bash
SP=$(mktemp -d)
for f in output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA/docs/Spec.md \
         output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md; do
  cp "$f" "$SP/$(echo "$f" | tr '/' '_')"
done
echo "$SP"
```

- [ ] **Step 2: 두 SP를 재생성한다**

```bash
dotnet run --project src/ReSet.Cli -- --sp UP_UTIL_SETTLE_INS_EXTRA,UP_UTIL_SETTLE_EXCEPTION_PROC < /dev/null 2>&1 | tail -30
```

기대: `오프라인 모드로 동작합니다`가 찍히고 `=== 배치 모드 자동 분석 완료 ===`로 끝난다. 쿼터 소진이나 권한 프롬프트로 멈추면 진행 중이던 객체부터 다시 돌린다.

- [ ] **Step 3: 세 가지를 확인한다**

```bash
python3 - <<'PY'
import json,io,re
p='output/Procedures/dbo.UP_UTIL_SETTLE_INS_EXTRA'
d=json.load(io.open(f'{p}/raw/metadata.json',encoding='utf-8-sig'))
rpc=d.get('RawPromptContext') or ''
m=re.search(r'###\s*테이블[^\n]*TSettleMst.*?(?=\n###|\Z)', rpc, re.S)
print("1) 스키마 블록에 ProductName:", 'ProductName' in (m.group(0) if m else ''))
spec=io.open(f'{p}/docs/Spec.md',encoding='utf-8').read()
print("2) '스키마 불일치' 단정 사라짐:", '스키마 불일치' not in spec)
row=[l for l in spec.split('\n') if l.startswith('| DB 배치 ')]
print("3) DB 배치 행 존재:", bool(row))
if row: print("   문장:", row[0][:160])
PY
```

기대: 1과 2가 `True`, 3이 `True`이고 문장이 소속 DB 안과 밖을 가른다.

**세 번째가 가장 중요하다.** 앞의 둘만 보면 모델이 그 행을 지워서 통과한 경우를 성공으로 오독한다. 행이 사라졌으면 실패로 보고한다.

- [ ] **Step 4: 스키마 블록이 얼마나 넓어졌는지 잰다**

스펙 §8이 남긴 측정 항목이다. 입력원을 넓혔으므로 컬럼이 늘고 토큰이 는다. 과다 포함이
싸다는 것이 `SchemaPromptColumnSelector`의 정책이지만 증가폭 자체는 재본 적이 없다.

```bash
python3 - <<'PYEOF'
import json,io,re
for sp in ['UP_UTIL_SETTLE_INS_EXTRA','UP_UTIL_SETTLE_EXCEPTION_PROC']:
    d=json.load(io.open(f'output/Procedures/dbo.{sp}/raw/metadata.json',encoding='utf-8-sig'))
    rpc=d.get('RawPromptContext') or ''
    blocks=re.findall(r'^###\s*테이블[^\n]*$', rpc, re.M)
    rows=len(re.findall(r'^\|\s*[A-Za-z_]', rpc, re.M))
    print(f"{sp}: 스키마 블록 {len(blocks)}개 · 컬럼 행 {rows}개 · RawPromptContext {len(rpc)}자")
PYEOF
```

백업본(Step 1)의 같은 수치와 비교해 증가폭을 적는다. 판정 기준은 두지 않는다 — 이 단계는
사실을 남기는 것이 목적이고, 증가가 문제인지는 사람이 판단한다.

- [ ] **Step 5: 결과를 보고한다**

Step 3의 세 확인과 Step 4의 증가폭을 그대로 적는다. 하나라도 기대와 다르면 무엇이 어떻게 달랐는지 백업본과 대조해 적고, 추측으로 원인을 단정하지 않는다.

---

## 완료 기준

- Task 1~6의 커밋 여섯 개가 있고, 마지막 커밋 시점에 `dotnet test`가 실패 0 · 건너뜀 0이다.
- Task 7의 세 확인이 전부 기대대로다.
- 닫힌 결함: 🟠 1건(`UF_GET_COMM4PG4INTEREST`의 널 허용), 🟡 5건(`X.PRODUCTNAME` 2 · 표 셀 수 2 · 매핑 표 오타 1), ⚪ 4건(`DB 배치`).

## 이 계획이 닫지 않는 것

스펙 §7 그대로다. ③ 기존 표의 관할 밖(약 15건, 🔴2·🟠5), ④ 산문이 표를 뒤집음(3건, 🔴1·🟠2), ⑤ 감사 기준과 도구 정책의 불일치(5건)는 각각 별도 설계 사이클이 필요하다. 특히 ③에 결함의 무게가 몰려 있으므로 이 계획을 끝낸 뒤 곧바로 ③의 브레인스토밍으로 넘어가는 것이 자연스럽다.
