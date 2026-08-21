# 명세서 기계 확정 사실 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 축 A 감사가 찾은 `Spec.md` 결함을, AI가 판단할 여지를 없애는 기계 확정 표로 막는다.

**Architecture:** 추출기가 원본 DDL과 정적 분석에서 사실을 계산해 `기계 확정 — 수정 금지` 표 두 종으로 렌더하고, 프롬프트 4갈래가 공유 빌더 하나를 호출해 그 표를 싣고, L1(`MechanicalValidator`)이 산출물과 행 단위로 대조한다. 별도로 프롬프트 스키마 표가 컬럼을 잘라 내던 재료 결함(H)을 먼저 고친다.

**Tech Stack:** C# / .NET 10 · Microsoft.SqlServer.TransactSql.ScriptDom (`TSql160Parser`) · Serilog · xUnit

**Spec:** `docs/superpowers/specs/2026-08-22-spec-machine-facts-design.md`

## Global Constraints

- 빌드 경고 기준선은 **정확히 8개**다. `dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l` 가 8이어야 한다. (`AGENTS.md:187`)
- `dotnet test`는 **실패 0 · 건너뜀 0**이 기준이다. (`AGENTS.md:188`) 현재 기준선은 2176개 통과.
- 표 헤딩 상수의 구분자는 하이픈이 아니라 **em dash `—`(U+2014)** 이고 양옆에 공백이 하나씩이다: `### DML 범위 (기계 확정 — 수정 금지)`. 하이픈으로 바뀌면 프롬프트와 L1의 헤딩 대조가 조용히 어긋난다.
- 시스템 프롬프트 본문은 **영어**가 원칙이다(`AGENTS.md:93`). 표 안의 한국어 헤딩·열 이름은 산출물에 그대로 실릴 문자열이므로 예외다.
- **한국어 2인칭 명령문을 표 블록 안에 섞지 않는다.** 모델이 표째로 명세서에 베껴 쓴 실측 사고가 있어 `PromptInstructionMarker` / `CheckPromptInstructionLeak`이 도입됐다.
- 새 추출기는 전부 `AGENTS.md` 범주 2 **소프트 페일**이다 — `try/catch`로 감싸고 실패 시 `Array.Empty<T>()`를 돌려주며 `Log.Warning`만 남긴다.
- 새 추출기의 **파서 오류 정책은 "오류가 하나라도 있으면 빈 목록"** 으로 통일한다(`DmlScopeExtractor.ExtractLockHints`와 같은 쪽). 기계 확정 표에 부분 파스 결과가 섞이면 표 전체의 신뢰가 무너진다.
- 기존 검사 15개를 감싼 `MechanicalValidator.Validate`의 catch-all은 **건드리지 않는다**. 대신 새 검사는 각각 자기 `try/catch`를 갖는다.
- **`SpDefinition.StaticAnalysis`는 절대 `null`이 아니다** — `= new()`가 기본값이다. 그래서 `analysis == null` 검사만으로는 아무것도 걸러지지 않는다. "정적 분석이 없다"를 판정하려면 **`IsParsedSuccessfully`** 를 봐야 한다(`AiService.cs:147`의 기존 패턴). `SqlStaticParser.Analyze`는 이 플래그를 전부 아니면 전무로 설정하므로, 실패한 파스에서 나온 빈 목록은 "확인된 빈 값"이 아니라 **"보지 않았음"** 이다 — 그 상태에서 확정 사실을 만들면 안 된다. *(Wave 1 실측으로 확정)*
- `SpDefinition.ObjectKey`의 타입은 **`CodeObjectKey`** 다 — 위치 파라미터 record `(string Database, string Schema, string Name, CodeObjectType Type)`이고 무인자 생성자가 없다. 생성은 `CodeObjectKey.Create(db, schema, name, CodeObjectType.Procedure)` 관례를 따른다. *(Wave 1 실측으로 확정)*
- 테스트에서 `Assert.Single(collection.Where(pred))`를 쓰지 마라 — `xUnit2031` 분석기 경고가 난다. `Assert.Single(collection, pred)` 오버로드를 써라. 이 저장소는 비-CS 경고가 0인 상태를 유지해 왔다. *(Wave 1 실측)*
- **확정 사실 문장에 `N건` 같은 수치 표현으로 부재를 말하지 마라.** 기존 L1 검사 `CheckIdentifierNotationClaims`의 `NegationTokens`는 `없습니다`·`않습니다`·`아닙니다`·`없음`·`아님` 계열만 부정으로 인식한다. `3부 식별자 참조 0건`처럼 쓰면 그 검사가 **부정 없는 주장**으로 오해해 3부 참조가 없는 거의 모든 SP에서 오탐을 낸다 — Wave 2가 실제로 이 벽에 부딪혀 `NegationTokens`에 `"0건"`을 더해야 했고, 그 widening은 자기모순 문장을 가릴 수 있는 잔여 위험을 남겼다. **새 사실 문장은 이미 인식되는 부정 어휘를 써라.** *(Wave 2 실측)*

---

## File Structure

**신규 (`src/ReSet.Core/Services/`)**

| 파일 | 책임 |
|---|---|
| `ExecutionSemanticsFacts.cs` | `실행 의미` 표의 행 레코드 · 헤딩 상수 · 다섯 추출기를 합치는 `Collect` |
| `DatabasePlacementExtractor.cs` | E — `StaticAnalysis`에서 DB 배치 확정 문장을 만든다 |
| `AggregateAssignmentExtractor.cs` | B — `SELECT @v = AGG(...)` 무결과 의미 |
| `RowCountBoundaryExtractor.cs` | C — `@@ROWCOUNT` 앞의 `IF` 리셋 |
| `CursorLifecycleExtractor.cs` | F — 커서 미해제 · `LOCAL` 미지정 |
| `ExpressionTypePathExtractor.cs` | A — `CAST(... AS INT)`의 타입 경로 |
| `CaseBranchExtractor.cs` | D — `CASE` 분기 전수 · 헤딩 상수 |

**수정**

| 파일 | 무엇 |
|---|---|
| `SchemaPromptColumnSelector.cs` | H — 주석 컬럼 보강 · 별칭 한정 정규화 |
| `AiService.cs` | 렌더러 2개 + 4갈래 배선 |
| `SpecExpectations.cs` | 새 재료 2종 노출 + 이른 반환 AND-체인 항 추가 |
| `MechanicalValidator.cs` | `ErrorType` 2개 · 검사 2개 · 호출 배선 |
| `DmlScopeExtractor.cs` | G — `DmlScopeFact`에 `GroupByColumns` 추가 |

**테스트 (`tests/ReSet.Core.Tests/`)** — 신규 파일 6개(`ExecutionSemanticsFactsTests.cs` 외), 기존 `AiServiceTests_Rich.cs` · `MechanicalValidatorTests.cs` · `SchemaPromptColumnSelectorTests.cs` · `DmlScopeExtractorTests.cs` 확장.

---

### Task 1: H — 스키마 표 과소 포함 수정

프롬프트 스키마 표가 참조 컬럼만 남기고 잘라 내서, 주석에만 등장하는 컬럼(`TClient.ClientIDType`)과 별칭 한정 표기(`X.PRODUCTNAME`)가 사라진다. AI는 자기가 받은 표를 정직하게 읽고 "없습니다"라고 썼고, L1의 기준값도 같은 잘린 집합이라 잡히지 않았다.

**Files:**
- Modify: `src/ReSet.Core/Services/SchemaPromptColumnSelector.cs:28-67`
- Test: `tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs`

**Interfaces:**
- Consumes: 없음 (첫 과제)
- Produces: `SchemaPromptColumnSelector.Select(DependencyInfo, SpDefinition) → IReadOnlySet<string>` — 시그니처 불변. 반환 집합이 넓어질 뿐이다.

- [ ] **Step 1: 주석 컬럼이 잘려 나가는 것을 재현하는 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs` 끝에 추가한다.

```csharp
        [Fact]
        public void Select_ColumnMentionedOnlyInComment_ShouldStayInThePromptTable()
        {
            // UP_UTIL_SETTLE_PROC_ETC 실측: ClientIDType이 주석 처리된 조건에만 있어
            // 표에서 잘려 나갔고, 명세서가 "제공 스키마의 TClient에도 ClientIDType은
            // 없습니다"라고 썼다. AI의 환각이 아니라 재료가 잘린 결과다.
            var dep = new DependencyInfo
            {
                Schema = "dbo",
                Name = "TClient",
                Type = "USER_TABLE",
                Columns = new List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "ClientID", IsPrimaryKey = true },
                    new ColumnInfo { ColumnName = "ClientIDType" }
                }
            };

            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "PROC_ETC",
                DdlText = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    SELECT C.ClientID
    FROM   dbo.TClient C
    --AND    C.ClientIDType <> 1
END"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedColumnsPerTable = new Dictionary<string, List<string>>
                {
                    ["dbo.TClient"] = new List<string> { "ClientID" }
                }
            };

            var shown = SchemaPromptColumnSelector.Select(dep, spDef);

            Assert.Contains("ClientIDType", shown);
        }

        [Fact]
        public void Select_AliasQualifiedReferenceKey_ShouldMatchThePlainColumn()
        {
            // UP_UTIL_SETTLE_INS_EXTRA 실측: 원본 INSERT 목록이 X.PRODUCTNAME이라
            // 참조 키가 별칭 한정으로 기록됐고, 스키마의 ProductName과 맞지 않아
            // 잘려 나갔다.
            var dep = new DependencyInfo
            {
                Schema = "dbo",
                Name = "TSettleMst",
                Type = "USER_TABLE",
                Columns = new List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "PLTID", IsPrimaryKey = true },
                    new ColumnInfo { ColumnName = "ProductName" }
                }
            };

            var spDef = new SpDefinition { Schema = "dbo", Name = "INS_EXTRA", DdlText = "SELECT 1;" };
            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedColumnsPerTable = new Dictionary<string, List<string>>
                {
                    ["dbo.TSettleMst"] = new List<string> { "X.PRODUCTNAME" }
                }
            };

            var shown = SchemaPromptColumnSelector.Select(dep, spDef);

            Assert.Contains("ProductName", shown);
        }
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SchemaPromptColumnSelectorTests"`
Expected: 두 테스트 FAIL. 첫째는 `ClientIDType`이 `shown`에 없어서, 둘째는 `keepCols`에 `X.PRODUCTNAME`만 있고 `ProductName`이 없어 `keepCols.Contains("ProductName")`이 거짓이라 잘려서.

- [ ] **Step 3: 별칭 한정 정규화와 주석 컬럼 보강을 넣는다**

`SchemaPromptColumnSelector.cs`의 `using` 목록에 다음을 더한다.

```csharp
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;
```

`Select`의 1) 블록을 아래로 바꾼다(`:32-40` 자리). 원문과 베이스 이름을 **둘 다** 넣는다 — 이 파일 주석이 못박듯 과다 포함은 싸고 과소 포함이 결함을 만든다.

```csharp
            // 1) AST에서 감지한 실제 참조 컬럼
            var analysis = spDef.StaticAnalysis;
            if (analysis?.ReferencedColumnsPerTable != null)
            {
                foreach (var kvp in analysis.ReferencedColumnsPerTable)
                {
                    if (!KeyMatchesDependency(kvp.Key, dep, spDef)) continue;
                    foreach (var c in kvp.Value)
                    {
                        keepCols.Add(c);
                        // 원본이 INSERT 대상 목록에 X.PRODUCTNAME처럼 별칭을 붙여
                        // 적으면 파서가 그 문자열을 그대로 키에 담는다(실측:
                        // UP_UTIL_SETTLE_INS_EXTRA). 베이스 이름도 함께 넣어야
                        // 스키마의 ProductName과 맞는다.
                        keepCols.Add(ExtractBaseName(c));
                    }
                }
            }
```

3) 블록 뒤(`:57`, `var shown = ...` 바로 앞)에 4)를 더한다.

```csharp
            // 4) 주석에만 등장하는 컬럼
            //
            // 주석 처리된 조건이 참조하는 컬럼은 AST에 없고 PK/FK도 인덱스도 아니라
            // 1~3에서 전부 빠진다. 그러면 모델이 그 컬럼을 "스키마에 없다"고 기록하고
            // (실측: UP_UTIL_SETTLE_PROC_ETC의 TClient.ClientIDType), L1의 기준값도
            // 같은 잘린 집합이라 그 거짓 주장을 잡지 못한다. 이 클래스 문서가 이미
            // 경고한 과소 포함 결함이다.
            if (keepCols.Count > 0)
            {
                var commentWords = CollectCommentWords(spDef.DdlText);
                if (commentWords.Count > 0)
                {
                    foreach (var col in dep.Columns)
                    {
                        if (commentWords.Contains(col.ColumnName)) keepCols.Add(col.ColumnName);
                    }
                }
            }
```

`keepCols.Count > 0` 가드가 필요한 이유: `keepCols`가 비면 아래 폴백이 이미 전체를 찍으므로 보강할 것이 없고, 빈 집합에 주석 컬럼만 채우면 오히려 **폴백이 꺼져 다른 컬럼이 전부 사라진다.**

클래스 끝(`ExtractBaseName` 뒤)에 헬퍼를 더한다.

```csharp
        /// <summary>
        /// DDL 주석 안의 식별자 후보 단어를 모은다.
        ///
        /// 토큰 스트림을 쓰는 이유는 RoundingSemanticsExtractor가 AST를 쓰는 이유와
        /// 같다 - 정규식으로 원문에서 "--"를 찾으면 문자열 리터럴 안의 텍스트까지
        /// 주석으로 오인한다. GetTokenStream은 렉서가 실제로 주석으로 분류한 것만
        /// 돌려준다.
        ///
        /// 단어를 통째로 담고 컬럼명과 대조하는 쪽을 택했다 - 주석 안의 SQL을 다시
        /// 파싱하려 들면 조각난 구문에서 실패하고, 실패는 곧 과소 포함이다.
        /// </summary>
        private static HashSet<string> CollectCommentWords(string? ddlText)
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(ddlText)) return words;

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var tokens = parser.GetTokenStream(reader, out _);
                if (tokens == null) return words;

                foreach (var token in tokens)
                {
                    if (token.TokenType != TSqlTokenType.SingleLineComment
                        && token.TokenType != TSqlTokenType.MultilineComment)
                    {
                        continue;
                    }

                    foreach (var word in Regex.Split(token.Text ?? string.Empty, "[^A-Za-z0-9_]+"))
                    {
                        if (word.Length > 0) words.Add(word);
                    }
                }
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 실패하면 보강 없이 진행한다. 기존 동작으로 돌아갈 뿐이다.
                Log.Warning(ex, "[SchemaPromptColumnSelector] 주석 토큰 수집 실패 - 주석 컬럼 보강 없이 진행합니다.");
            }

            return words;
        }
```

- [ ] **Step 4: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~SchemaPromptColumnSelectorTests"`
Expected: PASS

- [ ] **Step 5: 전체 테스트로 회귀를 확인한다**

Run: `dotnet test`
Expected: 실패 0 · 건너뜀 0. `SchemaClaimGateRegressionTests`가 함께 통과해야 한다 — `Select`가 넓어지면 `CheckSchemaClaims`의 기준값도 넓어져 이전에 침묵하던 자리에서 오류가 날 수 있다. 실패하면 그 테스트의 기대값이 "잘린 집합"을 전제하고 있었다는 뜻이므로, 테스트 쪽 기대값을 넓힌 집합으로 고친다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/SchemaPromptColumnSelector.cs tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs
git commit -m "fix: 주석에만 등장하는 컬럼과 별칭 한정 참조가 스키마 표에서 잘리지 않게 한다"
```

---

### Task 2: 실행 의미 표 — 골격과 E(DB 배치)

표 하나의 왕복(추출 → 렌더 → 4갈래 배선)을 가장 싼 항목 하나로 완성한다. 이후 B·C·F·A는 이 틀에 행을 얹기만 한다.

**Files:**
- Create: `src/ReSet.Core/Services/ExecutionSemanticsFacts.cs`
- Create: `src/ReSet.Core/Services/DatabasePlacementExtractor.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs` (렌더러 추가 + 4갈래 배선)
- Test: `tests/ReSet.Core.Tests/ExecutionSemanticsFactsTests.cs` (신규)
- Test: `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs` (추가)

**Interfaces:**
- Consumes: Task 1의 `SchemaPromptColumnSelector` (간접 — 프롬프트 스키마 표가 넓어진 상태)
- Produces:
  - `record ExecutionSemanticFact(string Kind, string Line, string Target, string Fact)`
  - `ExecutionSemanticsFacts.TableHeading` (const string)
  - `ExecutionSemanticsFacts.BuildColumnTypeMap(IEnumerable<DependencyInfo>?) → IReadOnlyDictionary<string, string>`
  - `ExecutionSemanticsFacts.Collect(string? ddlText, SpStaticAnalysisResult? analysis, CodeObjectKey? objectKey, IReadOnlyDictionary<string, string> columnTypes) → IReadOnlyList<ExecutionSemanticFact>`
  - `AiService.BuildMachineFactBlockLines(SpDefinition) → List<string>` (private static) — **4갈래가 부르는 단 하나의 진입점.** Task 7이 여기에 표를 하나 더 얹어도 갈래는 손대지 않는다.

**`Collect`의 네 번째 인자를 지금 넣는 이유**: Task 9(A)가 컬럼 타입 사전을 요구한다. 시그니처를 나중에 바꾸면 Task 2~6이 쓴 호출부 코드가 전부 컴파일되지 않는다. 지금은 쓰지 않아도 자리를 만들어 둔다.

- [ ] **Step 1: 실패하는 추출기 테스트를 쓴다**

`tests/ReSet.Core.Tests/ExecutionSemanticsFactsTests.cs` 신규.

```csharp
using System.Collections.Generic;
using System.Linq;
using Xunit;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class ExecutionSemanticsFactsTests
    {
        private static readonly Dictionary<string, string> NoColumns = new();

        [Fact]
        public void Collect_NoThreePartAndNoLinkedServer_ShouldStateLocalPlacementAsFact()
        {
            // F1 무리 실측: 파서가 ThreePartObjectReferences를 빈 배열로 확정했는데
            // 명세서 9곳이 "크로스 데이터베이스 참조라고 단언할 수 없습니다"로 되짚었다.
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedTables = new List<string> { "SETTLE_POQ_DB.dbo.TPGProperty" }
            };

            var facts = ExecutionSemanticsFacts.Collect(
                "SELECT 1;", analysis, CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure), NoColumns);

            var fact = Assert.Single(facts, f => f.Kind == ExecutionSemanticsFacts.DatabasePlacementKind);
            Assert.Contains("SETTLE_POQ_DB", fact.Fact);
            Assert.Contains("3부 식별자 참조 0건", fact.Fact);
            Assert.Contains("연결 서버 참조 0건", fact.Fact);
        }

        [Fact]
        public void Collect_WithThreePartReference_ShouldNameTheCrossDatabaseTargets()
        {
            var analysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ThreePartObjectReferences = new List<string> { "PaymentDB.dbo.TExtraSettleIn" }
            };

            var facts = ExecutionSemanticsFacts.Collect(
                "SELECT 1;", analysis, CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure), NoColumns);

            var fact = Assert.Single(facts, f => f.Kind == ExecutionSemanticsFacts.DatabasePlacementKind);
            Assert.Contains("PaymentDB.dbo.TExtraSettleIn", fact.Fact);
        }

        [Fact]
        public void Collect_WithoutAnalysis_ShouldReturnEmpty()
        {
            var facts = ExecutionSemanticsFacts.Collect("SELECT 1;", null, null, NoColumns);

            Assert.Empty(facts);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ExecutionSemanticsFactsTests"`
Expected: 컴파일 실패 — `ExecutionSemanticsFacts`가 없다.

- [ ] **Step 3: E 추출기와 집계자를 만든다**

`src/ReSet.Core/Services/DatabasePlacementExtractor.cs` 신규.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <param name="Sentence">확정형 한 문장. 프롬프트와 L1이 이 값을 함께 쓴다.</param>
    public sealed record DatabasePlacementFact(string Sentence);

    /// <summary>
    /// 참조 객체가 이 객체와 같은 DB에 있는지를 확정 문장으로 만든다.
    ///
    /// [왜 추출이 아니라 번역인가] 재료는 이미 StaticAnalysis에 있다 -
    /// ThreePartObjectReferences와 LinkedServerReferences가 빈 배열이면 "크로스 DB
    /// 참조가 아니다"가 확정값이지 미확정 사항이 아니다. 그런데 2026-08-22 축 A
    /// 감사에서 명세서 9곳이 그 확정값을 "단언할 수 없습니다"로 되짚었다. 그래서
    /// 판단을 모델에게 맡기지 않고 문장으로 못박아 표에 싣는다.
    /// </summary>
    public static class DatabasePlacementExtractor
    {
        public static DatabasePlacementFact? Extract(
            SpStaticAnalysisResult? analysis, CodeObjectKey? objectKey)
        {
            if (analysis == null) return null;

            var threePart = analysis.ThreePartObjectReferences ?? new List<string>();
            var linked = analysis.LinkedServerReferences ?? new List<string>();
            var home = string.IsNullOrWhiteSpace(objectKey?.Database) ? "(미상)" : objectKey!.Database!;

            if (threePart.Count == 0 && linked.Count == 0)
            {
                return new DatabasePlacementFact(
                    $"참조 객체는 전부 `{home}` 로컬입니다. 3부 식별자 참조 0건, 연결 서버 참조 0건 — 확정값입니다.");
            }

            var parts = new List<string>();
            if (threePart.Count > 0)
            {
                parts.Add($"3부 식별자 참조 {threePart.Count}건: {string.Join(", ", threePart)}");
            }
            if (linked.Count > 0)
            {
                parts.Add($"연결 서버 참조 {linked.Count}건: {string.Join(", ", linked)}");
            }

            return new DatabasePlacementFact(
                $"소속 DB는 `{home}`이고 다음은 그 밖입니다 — {string.Join(" / ", parts)}.");
        }
    }
}
```

`src/ReSet.Core/Services/ExecutionSemanticsFacts.cs` 신규.

```csharp
using System;
using System.Collections.Generic;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <param name="Kind">행의 종류. 표의 첫 칸이자 L1이 행을 특정하는 키의 일부다.</param>
    /// <param name="Line">원본 줄 번호. 줄에 매이지 않는 사실(DB 배치)은 "-".</param>
    /// <param name="Target">대상 원문 — 식·변수·커서 이름 등.</param>
    /// <param name="Fact">확정 사실 문장.</param>
    public sealed record ExecutionSemanticFact(string Kind, string Line, string Target, string Fact);

    /// <summary>
    /// 「실행 의미」 표의 행을 모은다.
    ///
    /// [왜 종류마다 표를 나누지 않았는가] 표 하나가 늘 때마다 헤딩 상수 · 렌더 조건 ·
    /// L1 검사 · 프롬프트 4갈래 배선 · 테스트 두 벌이 함께 늘어난다. 종류 칸 하나로
    /// 묶으면 그 비용을 한 번만 치른다. CASE 분기만 따로 둔 것은 행 수가 자릿수부터
    /// 다르기 때문이다(한 SP에서 수십 행).
    /// </summary>
    public static class ExecutionSemanticsFacts
    {
        public const string TableHeading = "### 실행 의미 (기계 확정 — 수정 금지)";

        public const string DatabasePlacementKind = "DB 배치";

        /// <summary>
        /// 컬럼명 → 데이터 타입 사전. ExpressionTypePathExtractor(Task 9)가 잎 타입을
        /// 판정할 때 쓴다. 같은 컬럼명이 테이블마다 타입이 다르면 판정할 수 없으므로
        /// "(모호)"로 표시해 그 CAST 행이 통째로 생략되게 한다.
        /// </summary>
        public static IReadOnlyDictionary<string, string> BuildColumnTypeMap(
            IEnumerable<DependencyInfo>? dependencies)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dep in dependencies ?? Enumerable.Empty<DependencyInfo>())
            {
                foreach (var col in dep.Columns)
                {
                    if (string.IsNullOrWhiteSpace(col.ColumnName)) continue;
                    if (string.IsNullOrWhiteSpace(col.DataType)) continue;

                    if (map.TryGetValue(col.ColumnName, out var existing)
                        && !string.Equals(existing, col.DataType, StringComparison.OrdinalIgnoreCase))
                    {
                        map[col.ColumnName] = "(모호)";
                        continue;
                    }

                    map[col.ColumnName] = col.DataType;
                }
            }

            return map;
        }

        public static IReadOnlyList<ExecutionSemanticFact> Collect(
            string? ddlText,
            SpStaticAnalysisResult? analysis,
            CodeObjectKey? objectKey,
            IReadOnlyDictionary<string, string> columnTypes)
        {
            var facts = new List<ExecutionSemanticFact>();

            var placement = DatabasePlacementExtractor.Extract(analysis, objectKey);
            if (placement != null)
            {
                facts.Add(new ExecutionSemanticFact(
                    DatabasePlacementKind, "-", "(객체 전체)", placement.Sentence));
            }

            return facts;
        }
    }
}
```

`using System.Linq;`를 파일 상단에 더한다(`BuildColumnTypeMap`의 `Enumerable.Empty`).

- [ ] **Step 4: 추출기 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ExecutionSemanticsFactsTests"`
Expected: PASS

- [ ] **Step 5: 렌더러가 표를 내는 실패 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests_Rich.cs` 끝에 픽스처와 테스트 셋을 추가한다.

```csharp
        /// <summary>
        /// 실행 의미 표 픽스처. DDL 조각 자체가 &lt;sp-source-ddl&gt;로도 실리므로,
        /// 표가 실제로 렌더됐는지는 표에서만 나오는 마크업(헤딩 상수)으로 대조해야
        /// 한다 - 원본 DDL에 우연히 있는 단어를 짚으면 거짓양성이 된다.
        /// </summary>
        private static SpDefinition ProbeExecutionSemanticsSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "COMM_UPD",
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 END",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure)
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldPrefillTheExecutionSemanticsTable()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeExecutionSemanticsSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(ExecutionSemanticsFacts.TableHeading, body);
            Assert.Contains("3부 식별자 참조 0건", body);
        }

        [Fact]
        public async Task GenerateSpecSectionAsync_CrudAnalysis_ShouldPrefillTheExecutionSemanticsTable()
        {
            // 지역 모델 경로는 BuildSpecificationPrompts를 전혀 호출하지 않는다.
            // 이 분기에서만 배선이 빠져도 실패해야 한다.
            var (service, handler) = CreateProbe();

            await service.GenerateSpecSectionAsync(
                ProbeExecutionSemanticsSpDef(), "CrudAnalysis", "지침", null, null, CancellationToken.None);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(ExecutionSemanticsFacts.TableHeading, body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutStaticAnalysis_ShouldOmitTheExecutionSemanticsTable()
        {
            var (service, handler) = CreateProbe();
            var spDef = new SpDefinition { Schema = "dbo", Name = "P", DdlText = "SELECT 1;" };

            await service.GenerateSpecificationAsync(spDef, "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain(ExecutionSemanticsFacts.TableHeading, body);
        }
```

- [ ] **Step 6: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "ExecutionSemanticsTable"`
Expected: FAIL — 프롬프트에 헤딩이 없다.

- [ ] **Step 7: 렌더러를 만들고 4갈래에 배선한다**

`AiService.cs`의 `BuildLockHintTableLines`(`:1012`) 바로 뒤에 렌더러를 더한다. 인트로를 렌더러 안에 두는 쪽(`참조 함수`·`잠금 힌트`와 같은 모양)이라 `ruleIndex`를 소비하지 않는다 — 갈래 2에는 채번이 아예 없으므로 이 쪽이어야 4갈래가 같은 모양이 된다.

```csharp
        /// <summary>
        /// 「실행 의미」 표를 렌더한다. 조립기가 채우고 LLM은 손대지 않는다.
        ///
        /// [왜 인트로가 렌더러 안에 있는가] 이 표는 갈래 2(함수 명세서 경로)에도
        /// 실리는데 그 갈래에는 ruleIndex 채번이 없다(규칙 1~7이 verbatim 문자열로
        /// 하드코딩돼 있다). 인트로를 번호 붙은 규칙으로 분리하면 갈래마다 모양이
        /// 갈리므로, 참조 함수·잠금 힌트 표와 같이 렌더러가 인트로를 진다.
        /// </summary>
        private static List<string> BuildExecutionSemanticsTableLines(
            IReadOnlyList<ExecutionSemanticFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL EXECUTION SEMANTICS TABLE] The following facts are MACHINE-DERIVED from the source DDL and static analysis. Copy this table verbatim into `## CRUD 분석` under the exact heading shown. These are settled values, not open questions - never restate any of them as unknown, unverifiable, or not provided.",
                $"   {ExecutionSemanticsFacts.TableHeading}",
                "   | 종류 | 라인 | 대상 | 확정 사실 |",
                "   | :--- | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add(
                    $"   | {EscapeTableCell(fact.Kind)} | {EscapeTableCell(fact.Line)} | "
                    + $"{EscapeTableCell(fact.Target)} | {EscapeTableCell(fact.Fact)} |");
            }

            lines.Add("");
            return lines;
        }
```

그 뒤에 **4갈래가 부르는 단 하나의 진입점**을 만든다. 이것이 설계 D5의 실체다 — 표가 늘어도 갈래는 손대지 않는다.

```csharp
        /// <summary>
        /// 새로 추가되는 기계 확정 표를 전부 모아 프롬프트에 붙일 줄 목록으로 돌려준다.
        ///
        /// [왜 갈래마다 Collect를 부르지 않는가 - 설계 D5]
        /// 프롬프트 빌더는 4갈래이고(SP 전체 · 함수 · 지역 모델 CRUD · 지역 모델 로직),
        /// 지역 모델 경로는 BuildSpecificationPrompts를 아예 호출하지 않는다. 표 하나를
        /// 늘릴 때마다 네 곳에 같은 조건문을 베끼면 "한 갈래만 고쳤다"는 이 코드베이스의
        /// 반복 사고가 그대로 재생산된다. 진입점을 하나로 두면 표를 늘려도 갈래는
        /// 바뀌지 않는다.
        ///
        /// 기존 표 6종은 이 함수로 옮기지 않는다 - 갈래별 렌더 조건에 미묘한 비대칭이
        /// 있어(집합 술어는 dmlScopeFacts가 비면 렌더하지 않는다) 잘못 통일하면 기존
        /// 표가 조용히 사라지거나 더해진다.
        /// </summary>
        private static List<string> BuildMachineFactBlockLines(SpDefinition spDef)
        {
            var lines = new List<string>();

            var executionSemantics = ExecutionSemanticsFacts.Collect(
                spDef.DdlText,
                spDef.StaticAnalysis,
                spDef.ObjectKey,
                ExecutionSemanticsFacts.BuildColumnTypeMap(spDef.Dependencies));
            if (executionSemantics.Count > 0)
            {
                lines.AddRange(BuildExecutionSemanticsTableLines(executionSemantics));
            }

            return lines;
        }
```

**갈래 1** — `AiService.cs:441`(잠금 힌트 배선) 바로 뒤에 넣는다.

```csharp
            rules.AddRange(BuildMachineFactBlockLines(spDef));
```

**갈래 2** — `AiService.cs:1358`(`objectDeclarationForFunctionDef` 배선) 바로 **앞**에 넣는다.

```csharp
            var machineFactLinesForFunctionDef = BuildMachineFactBlockLines(functionDef);
            if (machineFactLinesForFunctionDef.Count > 0)
            {
                systemPrompt += "\n\n" + string.Join("\n", machineFactLinesForFunctionDef);
            }
```

**갈래 3-2** — `AiService.cs:2568`(`lockHintsForCrud` 배선) 바로 뒤에 넣는다. 리스트 이름이 `rules`가 아니라 `sbRules`다.

```csharp
                sbRules.AddRange(BuildMachineFactBlockLines(spDef));
```

**갈래 3-3** — `AiService.cs:2727`(`lockHintsForLogic` 배선) 바로 뒤에 넣는다. Task 7의 `CASE 분기` 표가 `## 로직 흐름 요약` 소관이라 이 갈래도 진입점을 받아야 한다.

```csharp
                sbRules.AddRange(BuildMachineFactBlockLines(spDef));
```

- [ ] **Step 8: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "ExecutionSemanticsTable"`
Expected: PASS (셋 다)

- [ ] **Step 9: 전체 테스트와 경고 기준선을 확인하고 커밋한다**

```bash
dotnet test
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
git add src/ReSet.Core/Services/ExecutionSemanticsFacts.cs src/ReSet.Core/Services/DatabasePlacementExtractor.cs src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/ExecutionSemanticsFactsTests.cs tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -m "feat: DB 배치를 실행 의미 표의 기계 확정 행으로 싣는다"
```

Expected: 실패 0 · 건너뜀 0 · 경고 8

---

### Task 3: 실행 의미 표의 L1 검사

프롬프트가 표를 넣는 것과 모델이 그것을 옮기는 것은 다른 일이다. 「참조 함수」 표가 검증 없이 나갔다가 10행 중 8행이 결함이었던 선례가 있다.

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs:38-105`, `:209-259`
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs:14-47` (`ErrorType`), `:133-150` (호출), 검사 추가
- Test: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `ExecutionSemanticsFacts.Collect`, `ExecutionSemanticsFacts.TableHeading`, `ExecutionSemanticFact`
- Produces: `SpecExpectations.ExecutionSemantics` (init prop, `IReadOnlyList<ExecutionSemanticFact>`), `ErrorType.ExecutionSemanticsTableMissing`

- [ ] **Step 1: 실패하는 L1 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` 끝에 추가한다. 픽스처 DDL을 `SELECT`로만 쓰는 것이 중요하다 — `UPDATE`/`DELETE`가 있으면 `DmlScopeFacts`가 비지 않아 `From`의 이른 반환 AND-체인에서 **새 항을 빠뜨려도 테스트가 초록으로 통과**한다.

```csharp
        private static SpDefinition ExecutionSemanticsSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "P",
                DdlText = "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 END",
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "P", CodeObjectType.Procedure)
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public void From_WithOnlyExecutionSemantics_ShouldNotReturnNull()
        {
            // 이른 반환 AND-체인에 자기 항을 넣지 않으면 재료가 이것 하나뿐일 때
            // From이 null을 돌려주고 CheckExecutionSemantics가 한 번도 돌지 않는다.
            var expectations = SpecExpectations.From(ExecutionSemanticsSpDef());

            Assert.NotNull(expectations);
            Assert.NotEmpty(expectations!.ExecutionSemantics);
            // 격리 확인 - 다른 재료가 이 픽스처를 살려 주고 있지 않은지 못박는다.
            Assert.Empty(expectations.DmlScopeFacts);
        }

        [Fact]
        public void Validate_MissingExecutionSemanticsTable_ShouldReportAnError()
        {
            var expectations = SpecExpectations.From(ExecutionSemanticsSpDef());
            var validator = new MechanicalValidator();

            var result = validator.Validate("## 개요\n표가 없다.\n", expectations);

            Assert.Contains(result.DetailedErrors,
                e => e.Type == ErrorType.ExecutionSemanticsTableMissing);
        }

        [Fact]
        public void Validate_WithExecutionSemanticsTableCopied_ShouldNotReportThatError()
        {
            var expectations = SpecExpectations.From(ExecutionSemanticsSpDef());
            var fact = Assert.Single(expectations!.ExecutionSemantics);
            var markdown =
                "## 개요\n\n"
                + ExecutionSemanticsFacts.TableHeading + "\n\n"
                + "| 종류 | 라인 | 대상 | 확정 사실 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + $"| {fact.Kind} | {fact.Line} | {fact.Target} | {fact.Fact} |\n";
            var validator = new MechanicalValidator();

            var result = validator.Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors,
                e => e.Type == ErrorType.ExecutionSemanticsTableMissing);
        }
```

- [ ] **Step 2: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test --filter "ExecutionSemantics"`
Expected: 컴파일 실패 — `SpecExpectations.ExecutionSemantics`와 `ErrorType.ExecutionSemanticsTableMissing`이 없다.

- [ ] **Step 3: `SpecExpectations`에 재료를 노출한다**

`SpecExpectations.cs`의 `HasInternalProcedureCall` 프로퍼티 뒤에 더한다.

```csharp
        /// <summary>
        /// 실행 의미 표의 행. CheckExecutionSemantics가 소비한다.
        ///
        /// 프롬프트(AiService)와 같은 Collect를 불러야 한다 - 두 곳이 갈리면 모델이
        /// 표를 그대로 베껴도 L1이 틀렸다고 하는 재현 불가능한 실패가 난다.
        /// </summary>
        public IReadOnlyList<ExecutionSemanticFact> ExecutionSemantics { get; init; }
            = Array.Empty<ExecutionSemanticFact>();
```

`From()`에서 다른 추출 호출들 옆(`objectDeclaration` 계산 부근)에 더한다.

```csharp
            var executionSemantics = ExecutionSemanticsFacts.Collect(
                spDef.DdlText,
                analysis,
                spDef.ObjectKey,
                ExecutionSemanticsFacts.BuildColumnTypeMap(spDef.Dependencies));
```

**프롬프트와 L1이 같은 `Collect`를 부르는 것이 핵심이다.** 두 곳이 갈리면 모델이 표를 그대로 베껴도 L1이 틀렸다고 하는 재현 불가능한 실패가 난다 — 이 파일의 기준일 파라미터 주석이 같은 이유로 규칙을 공유한다.

이른 반환 AND-체인의 `&& objectDeclaration == null` **앞**에 항을 더한다.

```csharp
                // objectDeclaration과 같은 이유로 중복항이 아니다 - DB 배치 행은
                // DML이 하나도 없는 객체에서도 난다. 이 항을 빠뜨리면 재료가 이것
                // 하나뿐인 객체에서 From이 null을 돌려주고 CheckExecutionSemantics가
                // 한 번도 돌지 않는다.
                && executionSemantics.Count == 0
```

객체 이니셜라이저에 한 줄 더한다.

```csharp
                ExecutionSemantics = executionSemantics,
```

- [ ] **Step 4: `ErrorType`과 검사를 더한다**

`MechanicalValidator.cs`의 `ErrorType`에서 `General` **바로 앞**에 더한다.

```csharp
        ExecutionSemanticsTableMissing,
```

`CheckLockHints` 뒤에 검사를 더한다. 자기 `try/catch`로 감싸는 것이 핵심이다 — 이 검사가 던져도 기존 검사 15개의 결과가 지워지면 안 된다.

```csharp
        /// <summary>
        /// 기계 확정 실행 의미 표가 명세서에 옮겨졌는지 본다. 재료가 없으면 조용히
        /// 건너뛴다 - AiService도 그때는 표를 내지 않는다(CheckLockHints와 같은 가드).
        ///
        /// [행 식별 키가 (종류, 라인, 대상, 확정 사실) 네 값 전부인 이유]
        /// CheckLockHints와 같다 - 한 객체에 같은 종류의 행이 여럿 날 수 있어(식 타입
        /// 경로는 CAST마다 한 행) 종류 토큰만으로는 행이 특정되지 않는다. 확정 사실
        /// 칸까지 요구하는 것은 "표는 채웠는데 값이 틀린" 부류를 잡기 위해서다.
        ///
        /// [자기 try/catch를 두는 이유] Validate의 catch-all은 검사 하나가 던지면
        /// Errors를 통째로 지우고 IsValid = true로 통과시킨다. 새 검사의 실패가 기존
        /// 검사 15개의 판정까지 삼키면 안 된다.
        /// </summary>
        private static void CheckExecutionSemantics(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.ExecutionSemantics.Count == 0) return;

            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var (headingIndex, endIndex) = LocateHeadingSection(
                    lines, ExecutionSemanticsFacts.TableHeading);

                if (headingIndex < 0)
                {
                    var missing =
                        $"기계 확정 실행 의미 표가 명세서에 없습니다. `{ExecutionSemanticsFacts.TableHeading}` "
                        + $"헤딩과 {expectations.ExecutionSemantics.Count}개 행을 그대로 옮겨야 합니다.";
                    result.Errors.Add(missing);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.ExecutionSemanticsTableMissing,
                        Message = missing
                    });
                    return;
                }

                var rowLines = new List<string>();
                for (var i = headingIndex + 1; i < endIndex; i++)
                {
                    if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                    {
                        rowLines.Add(lines[i]);
                    }
                }

                foreach (var fact in expectations.ExecutionSemantics)
                {
                    var present = rowLines.Any(row =>
                    {
                        var cells = SplitTableRowCells(row);
                        return cells.Any(c => c == fact.Kind)
                            && cells.Any(c => c == fact.Line)
                            && cells.Any(c => c == fact.Target)
                            && cells.Any(c => c == fact.Fact);
                    });
                    if (present) continue;

                    var message =
                        $"실행 의미 표에 `{fact.Kind}`(라인 {fact.Line}, 대상 {fact.Target}) 행이 없거나 "
                        + $"확정 사실이 다릅니다. `{fact.Fact}`를 그대로 옮겨야 합니다 - 이것은 미확정 "
                        + "사항이 아니라 확정값입니다.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.ExecutionSemanticsTableMissing,
                        Message = message,
                        RawContext = $"{fact.Kind} @ {fact.Line} {fact.Target}"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] 실행 의미 표 대조 실패 - 이 검사만 건너뜁니다.");
            }
        }

        /// <summary>
        /// 헤딩 하나와 그 표가 끝나는 인덱스를 찾는다. LocateLockHintSection의 일반형이다 -
        /// 새 표가 둘 늘어 같은 코드를 세 번 쓰게 되므로 헤딩을 인자로 받는다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateHeadingSection(
            IReadOnlyList<string> lines, string heading)
        {
            var headerIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, 0, line => line.Trim() == heading);
            if (headerIndex < 0) return (-1, -1);

            var endIndex = MarkdownSectionLocator.FindIndexOutsideFence(
                lines, headerIndex + 1,
                line =>
                {
                    var trimmed = line.TrimStart();
                    return trimmed.StartsWith("## ", StringComparison.Ordinal)
                        || trimmed.StartsWith("### ", StringComparison.Ordinal);
                });

            return (headerIndex, endIndex < 0 ? lines.Count : endIndex);
        }
```

`Validate`의 검사 나열 끝(`CheckOrderByExpressions` 뒤)에 호출을 더한다.

```csharp
                    CheckExecutionSemantics(cleansed, expectations, result);
```

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "ExecutionSemantics"`
Expected: PASS

- [ ] **Step 6: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/SpecExpectations.cs src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: 실행 의미 표를 L1이 행 단위로 대조한다"
```

---

### Task 4: B — 집계 대입의 무결과 의미

`DECLARE @v VARCHAR(8) = ''` 뒤 `SELECT @v = MIN(x)`는 무결과여도 한 행을 돌려주므로 변수에 **NULL**이 대입된다. 초기값 `''`는 유지되지 않는다. 명세서 두 곳이 이 사실을 통째로 빠뜨려 대상 행 집합이 "없음"과 "전부"로 뒤집혔다.

**Files:**
- Create: `src/ReSet.Core/Services/AggregateAssignmentExtractor.cs`
- Modify: `src/ReSet.Core/Services/ExecutionSemanticsFacts.cs`
- Test: `tests/ReSet.Core.Tests/AggregateAssignmentExtractorTests.cs` (신규)

**Interfaces:**
- Consumes: `ExecutionSemanticFact`, `ExecutionSemanticsFacts.Collect`
- Produces: `AggregateAssignmentExtractor.Extract(string? ddlText) → IReadOnlyList<AggregateAssignmentFact>`, `ExecutionSemanticsFacts.AggregateAssignmentKind`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class AggregateAssignmentExtractorTests
    {
        [Fact]
        public void Extract_MinAssignment_ShouldReportNullOnNoRows()
        {
            // UP_UTIL_SETTLE_INS_EXTRA 실측: 초기값 ''가 집계 대입에 덮여 NULL이 되고,
            // 이후 여덟 DML의 YMD >= @v 술어가 전부 UNKNOWN이 되어 0행이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_strReqYMD VARCHAR(8) = ''
    SELECT @v_strReqYMD = MIN(ReqYMD) FROM dbo.TExtraSettleIn WHERE ResultCode = '00'
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("@v_strReqYMD", fact.Variable);
            Assert.Equal("MIN", fact.Aggregate);
            Assert.True(fact.HasInitializer);
            Assert.Contains("NULL", fact.Sentence);
            Assert.Contains("초기값", fact.Sentence);
        }

        [Fact]
        public void Extract_CountAssignment_ShouldReportZeroNotNull()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @n INT
    SELECT @n = COUNT(*) FROM dbo.T
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("COUNT", fact.Aggregate);
            Assert.Contains("0", fact.Sentence);
            Assert.DoesNotContain("NULL", fact.Sentence);
        }

        [Fact]
        public void Extract_NonAggregateAssignment_ShouldBeIgnored()
        {
            // 비집계 대입은 무결과면 변수가 그대로 남는다 - 반대 의미라 담으면 거짓이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = c FROM dbo.T
END";

            Assert.Empty(AggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(AggregateAssignmentExtractor.Extract("CREATE PROCEDURE ((("));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~AggregateAssignmentExtractorTests"`
Expected: 컴파일 실패 — 클래스가 없다.

- [ ] **Step 3: 추출기를 만든다**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">대입문의 원본 줄 번호.</param>
    /// <param name="Variable">대입 대상 변수명.</param>
    /// <param name="Aggregate">집계 함수 이름(대문자).</param>
    /// <param name="HasInitializer">DECLARE에 초기값이 있었는가.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record AggregateAssignmentFact(
        int Line, string Variable, string Aggregate, bool HasInitializer, string Sentence);

    /// <summary>
    /// `SELECT @v = AGG(...)` 형태의 변수 대입을 뽑는다.
    ///
    /// [왜 이것이 확정 사실인가] T-SQL의 집계 SELECT는 GROUP BY가 없으면 일치 행이
    /// 0건이어도 한 행을 돌려준다. 그래서 대입은 항상 일어나고, MIN/MAX/SUM/AVG는
    /// NULL을, COUNT는 0을 넣는다. 이 사실은 이 SP의 사정이 아니라 T-SQL 명세다.
    ///
    /// [왜 비집계 대입은 담지 않는가] `SELECT @v = c FROM t`는 무결과면 대입 자체가
    /// 일어나지 않아 변수가 직전 값을 유지한다 - 정확히 반대 의미다. 담으면 거짓이 된다.
    ///
    /// [실측] UP_UTIL_SETTLE_INS_EXTRA:16,21-25와 UP_UTIL_SETTLE_SUMMARY_EXTRA:20,25-29.
    /// 둘 다 초기값 ''가 NULL로 덮이는 사실이 명세서 전체에 한 번도 없었고, 그 결과
    /// 후속 DML의 대상 행 집합이 "없음"과 "전부"로 뒤집히는 🟠이 났다.
    /// </summary>
    public static class AggregateAssignmentExtractor
    {
        private static readonly string[] NullOnEmptyAggregates = { "MIN", "MAX", "SUM", "AVG" };

        public static IReadOnlyList<AggregateAssignmentFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<AggregateAssignmentFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<AggregateAssignmentFact>();
                }

                var declareVisitor = new DeclareVisitor();
                fragment.Accept(declareVisitor);

                var visitor = new AggregateAssignmentVisitor(declareVisitor.Initialized);
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[AggregateAssignmentExtractor] 집계 대입 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<AggregateAssignmentFact>();
            }
        }

        private sealed class DeclareVisitor : TSqlFragmentVisitor
        {
            public HashSet<string> Initialized { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(DeclareVariableElement node)
            {
                if (node.Value == null) return;
                var name = node.VariableName?.Value;
                if (!string.IsNullOrWhiteSpace(name)) Initialized.Add(name!);
            }
        }

        private sealed class AggregateAssignmentVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _initialized;

            public AggregateAssignmentVisitor(HashSet<string> initialized) => _initialized = initialized;

            public List<AggregateAssignmentFact> Facts { get; } = new();

            public override void Visit(SelectSetVariable node)
            {
                if (node.Expression is not FunctionCall call) return;

                var name = call.FunctionName?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;

                var upper = name!.ToUpperInvariant();
                var isCount = upper == "COUNT" || upper == "COUNT_BIG";
                if (!isCount && !NullOnEmptyAggregates.Contains(upper)) return;

                var variable = node.Variable?.Name ?? "(미상)";
                var hasInitializer = _initialized.Contains(variable);

                var sentence = isCount
                    ? "집계 SELECT는 무결과여도 한 행을 돌려주므로 대입이 항상 일어납니다. COUNT는 0을 넣습니다."
                    : "집계 SELECT는 무결과여도 한 행을 돌려주므로 대입이 항상 일어납니다. 무결과 시 NULL이 대입됩니다"
                      + (hasInitializer ? " — DECLARE의 초기값은 유지되지 않습니다." : ".");

                Facts.Add(new AggregateAssignmentFact(
                    node.StartLine, variable, upper, hasInitializer, sentence));
            }
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~AggregateAssignmentExtractorTests"`
Expected: PASS

- [ ] **Step 5: 집계자에 종류를 더한다**

`ExecutionSemanticsFacts.cs`에 상수와 수집을 더한다.

```csharp
        public const string AggregateAssignmentKind = "집계 대입";
```

`Collect`의 `placement` 블록 뒤에 더한다.

```csharp
            foreach (var fact in AggregateAssignmentExtractor.Extract(ddlText))
            {
                facts.Add(new ExecutionSemanticFact(
                    AggregateAssignmentKind,
                    fact.Line.ToString(),
                    $"SELECT {fact.Variable} = {fact.Aggregate}(...)",
                    fact.Sentence));
            }
```

- [ ] **Step 6: 집계자 테스트를 더하고 통과를 확인한다**

`ExecutionSemanticsFactsTests.cs`에 추가한다.

```csharp
        [Fact]
        public void Collect_WithAggregateAssignment_ShouldEmitAnAggregateRow()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v VARCHAR(8) = ''
    SELECT @v = MIN(ReqYMD) FROM dbo.T
END";

            var facts = ExecutionSemanticsFacts.Collect(
                ddl, new SpStaticAnalysisResult { IsParsedSuccessfully = true }, null, NoColumns);

            Assert.Contains(facts, f => f.Kind == ExecutionSemanticsFacts.AggregateAssignmentKind);
        }
```

Run: `dotnet test --filter "FullyQualifiedName~ExecutionSemanticsFactsTests"`
Expected: PASS

- [ ] **Step 7: 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/AggregateAssignmentExtractor.cs src/ReSet.Core/Services/ExecutionSemanticsFacts.cs tests/ReSet.Core.Tests/AggregateAssignmentExtractorTests.cs tests/ReSet.Core.Tests/ExecutionSemanticsFactsTests.cs
git commit -m "feat: 집계 대입의 무결과 NULL 의미를 실행 의미 표에 싣는다"
```

---

### Task 5: C — `@@ROWCOUNT` 재설정 경계

`IF @@ROWCOUNT < 1` 앞의 `IF` 문이 `@@ROWCOUNT`를 0으로 리셋한다. 그래서 1차 조회가 행을 찾아도 3차 조회가 돈다 — 명세서의 mermaid는 건너뛰는 것으로 그려 금액 결정 규칙 자체가 달랐다(감사가 찾은 🔴 2건 중 하나). 실측으로 확인한 모양(직전 형제가 `IF`)에만 한정한다.

**Files:**
- Create: `src/ReSet.Core/Services/RowCountBoundaryExtractor.cs`
- Modify: `src/ReSet.Core/Services/ExecutionSemanticsFacts.cs`
- Test: `tests/ReSet.Core.Tests/RowCountBoundaryExtractorTests.cs` (신규)

**Interfaces:**
- Consumes: `ExecutionSemanticFact`
- Produces: `RowCountBoundaryExtractor.Extract(string? ddlText) → IReadOnlyList<RowCountBoundaryFact>`, `ExecutionSemanticsFacts.RowCountKind`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class RowCountBoundaryExtractorTests
    {
        [Fact]
        public void Extract_RowCountReadAfterAnIfStatement_ShouldReportTheReset()
        {
            // UF_GET_COMM4CLIENT 실측(실행 대조 2026-08-22, SQL Server 2022 16.0.4255.1):
            // 1차 조회가 행을 찾아 2차 블록이 건너뛰어져도, 그 IF 문이 @@ROWCOUNT를
            // 0으로 리셋해 3차 조회가 돈다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @x INT
    SELECT @x = c FROM dbo.T
    IF @@ROWCOUNT < 1 BEGIN SELECT TOP 1 @x = c FROM dbo.THist ORDER BY v DESC END
    IF @@ROWCOUNT < 1 BEGIN SELECT TOP 1 @x = c FROM dbo.T ORDER BY c DESC END
END";

            var facts = RowCountBoundaryExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Contains("직전 IF", fact.Sentence);
            Assert.DoesNotContain("항상 참", fact.Sentence);
            Assert.Contains("건너뛰", fact.Sentence);
            Assert.Contains("실행", fact.Sentence);
        }

        [Fact]
        public void Extract_RowCountReadRightAfterAQuery_ShouldNotBeReported()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @x INT
    SELECT @x = c FROM dbo.T
    IF @@ROWCOUNT < 1 BEGIN SET @x = 0 END
END";

            Assert.Empty(RowCountBoundaryExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(RowCountBoundaryExtractor.Extract("CREATE PROCEDURE ((("));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~RowCountBoundaryExtractorTests"`
Expected: 컴파일 실패.

- [ ] **Step 3: 추출기를 만든다**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">@@ROWCOUNT를 읽는 문장의 줄 번호.</param>
    /// <param name="Predicate">그 문장의 조건 원문.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record RowCountBoundaryFact(int Line, string Predicate, string Sentence);

    /// <summary>
    /// 직전 형제 문장이 IF인 자리에서 @@ROWCOUNT를 읽는 문장을 뽑는다.
    ///
    /// [실행으로 확정한 사실 - 2026-08-22, SQL Server 2022 16.0.4255.1]
    /// 원본 구조(SELECT → IF @@ROWCOUNT&lt;1 BEGIN…END → IF @@ROWCOUNT&lt;1 BEGIN…END)를
    /// 그대로 재현한 결과, 앞의 IF가 조건 거짓으로 블록을 건너뛰어도 그 IF 문 자체가
    /// @@ROWCOUNT를 0으로 만든다. 따라서 두 번째 IF의 조건이 참이 된다.
    ///
    /// [주의 - Wave 4 실측] 그 반대 경우는 다르다. 앞 IF의 분기가 실제로 실행되고
    /// 그 안 마지막 문장이 행에 영향을 주면 @@ROWCOUNT는 그 문장의 행 수로 남는다
    /// (CASE Y = NOT_RESET). 분기 실행 여부는 런타임 성질이라 정적으로 알 수 없으므로
    /// "항상 참"이라고 단정하면 안 된다 - 그 단정이 이 배치에서 실제로 Critical이 됐다.
    /// 실측 대상: UF_GET_COMM4CLIENT.Function:52,68 - 명세서 mermaid는 1차 성공 시
    /// 3차를 건너뛰는 것으로 그려 금액 결정 규칙 자체가 달랐다(🔴).
    ///
    /// [왜 이 모양에만 한정하는가] T-SQL에서 어떤 문장이 @@ROWCOUNT를 보존하고
    /// 어떤 문장이 0으로 만드는지의 일반 규칙을 전부 구현하려 들면 틀릴 여지가 크다.
    /// 기계 확정 표에 추측이 섞이면 표 전체의 신뢰가 무너진다. 실측으로 닫은 모양만
    /// 싣고 나머지는 침묵한다 - 실패 방향이 안전한 쪽이다.
    /// </summary>
    public static class RowCountBoundaryExtractor
    {
        public const string SemanticsSentence =
            "직전 문장이 IF입니다. 그 IF의 분기가 건너뛰어지면 @@ROWCOUNT가 0으로 리셋되어 "
            + "이 조건이 참이 됩니다. 분기가 실행되고 그 안 마지막 문장이 행에 영향을 주면 "
            + "@@ROWCOUNT는 그 문장의 행 수로 남아, 이 조건의 참·거짓은 그 값에 달려 있습니다.";

        public static IReadOnlyList<RowCountBoundaryFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<RowCountBoundaryFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<RowCountBoundaryFact>();
                }

                var visitor = new BlockVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[RowCountBoundaryExtractor] @@ROWCOUNT 경계 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<RowCountBoundaryFact>();
            }
        }

        private sealed class BlockVisitor : TSqlFragmentVisitor
        {
            public List<RowCountBoundaryFact> Facts { get; } = new();

            public override void Visit(StatementList node)
            {
                var statements = node.Statements;
                if (statements == null) return;

                for (var i = 1; i < statements.Count; i++)
                {
                    if (statements[i - 1] is not IfStatement) continue;
                    if (statements[i] is not IfStatement current) continue;
                    if (current.Predicate == null) continue;

                    var predicate = TextOf(current.Predicate);
                    if (predicate.IndexOf("@@ROWCOUNT", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    Facts.Add(new RowCountBoundaryFact(
                        current.StartLine, predicate, SemanticsSentence));
                }
            }

            private static string TextOf(TSqlFragment fragment)
            {
                return string.Concat(
                    fragment.ScriptTokenStream
                        .Skip(fragment.FirstTokenIndex)
                        .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                        .Select(t => t.Text)).Trim();
            }
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~RowCountBoundaryExtractorTests"`
Expected: PASS

- [ ] **Step 5: 집계자에 종류를 더한다**

```csharp
        public const string RowCountKind = "@@ROWCOUNT";
```

```csharp
            foreach (var fact in RowCountBoundaryExtractor.Extract(ddlText))
            {
                facts.Add(new ExecutionSemanticFact(
                    RowCountKind, fact.Line.ToString(), fact.Predicate, fact.Sentence));
            }
```

- [ ] **Step 6: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/RowCountBoundaryExtractor.cs src/ReSet.Core/Services/ExecutionSemanticsFacts.cs tests/ReSet.Core.Tests/RowCountBoundaryExtractorTests.cs
git commit -m "feat: IF 뒤 @@ROWCOUNT 리셋을 실행 의미 표에 싣는다"
```

---

### Task 6: F — 커서 수명 주기

`OPEN`과 `CLOSE` 사이에 `RETURN`이 있는 커서와, `LOCAL`을 지정하지 않아 범위가 **데이터베이스** 옵션에 달린 커서를 싣는다.

**문장이 무엇을 단정하는지 조심해라**(Wave 5 실측). "오류 경로에서 커서가 닫히지 않는다"는 그 `RETURN`이 오류 경로인지, 그 경로가 도달 가능한지를 단정한다 — 정적으로 알 수 없다. 관측과 그 직접 귀결까지만 말해라. 그리고 `default_to_local_cursor`는 **서버가 아니라 데이터베이스** 옵션이다(`docs/audit-reports/2026-08-20a-POQSettlePrco20-axisA.md:123`).

**이 원안 이후 바뀐 것 — I1 수정 라운드(2026-08-22 최종 브랜치 리뷰, Task 15).** 아래 Step 3 스케치의 게이트는 `LOCAL` 미지정만 봤다 — `GLOBAL`이 명시된 커서에도 "범위가 설정에 달려 있다"는 문장을 냈는데, `GLOBAL`이 명시되면 그 설정과 무관하게 범위가 전역으로 확정되므로 그 문장은 거짓이다. I1이 게이트를 `!declaration.IsLocal && !declaration.IsGlobal`로 고쳐 `GLOBAL` 명시 커서는 이 문장을 아예 내지 않는다(침묵). **아래 Step 3 코드는 그 수정 전의 원안이다 — 실제 게이트로 베끼지 마라.** 현재 게이트는 `src/ReSet.Core/Services/CursorLifecycleExtractor.cs`를 봐라(`needsScopeSentence`를 grep). 그리고 아래 docstring이 대는 "GLOBAL이면 같은 연결에서 재호출 시 DECLARE가 오류 16915로 실패해 처리 대상이 통째로 0이 된다"는 이 배치 어느 라운드에서도 실행으로 검증한 적이 없다 — **확인되지 않았다.** SUMMARY_ETC의 🟠 등급 근거는 커서 범위가 확정되지 않는다는 사실 자체이지, 오류 16915의 실측이 아니다.

**Files:**
- Create: `src/ReSet.Core/Services/CursorLifecycleExtractor.cs`
- Modify: `src/ReSet.Core/Services/ExecutionSemanticsFacts.cs`
- Test: `tests/ReSet.Core.Tests/CursorLifecycleExtractorTests.cs` (신규)

**Interfaces:**
- Consumes: `ExecutionSemanticFact`
- Produces: `CursorLifecycleExtractor.Extract(string? ddlText) → IReadOnlyList<CursorLifecycleFact>`, `ExecutionSemanticsFacts.CursorKind`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CursorLifecycleExtractorTests
    {
        [Fact]
        public void Extract_ReturnBetweenOpenAndClose_ShouldReportUnclosedOnErrorPath()
        {
            // UP_UTIL_SETTLE_SUMMARY_ETC 실측: 두 오류 경로가 ROLLBACK → SET → RETURN
            // 으로 끝나고 CLOSE/DEALLOCATE는 정상 종료 경로에만 있다. 커서는
            // BEGIN TRAN보다 먼저 OPEN돼 롤백으로도 닫히지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE GetDataCrsr CURSOR READ_ONLY FOR SELECT c FROM dbo.T
    OPEN GetDataCrsr
    IF @@ERROR <> 0 BEGIN RETURN END
    CLOSE GetDataCrsr
    DEALLOCATE GetDataCrsr
END";

            var facts = CursorLifecycleExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("GetDataCrsr", fact.CursorName);
            Assert.DoesNotContain("오류 경로", fact.Sentence);   // 도달 가능성을 단정하면 안 된다
            Assert.Contains("RETURN", fact.Sentence);
            Assert.Contains("데이터베이스", fact.Sentence);
            Assert.DoesNotContain("서버", fact.Sentence);
            Assert.Contains("LOCAL", fact.Sentence);
        }

        [Fact]
        public void Extract_CursorWithLocalAndNoEarlyReturn_ShouldNotBeReported()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE c1 CURSOR LOCAL FOR SELECT c FROM dbo.T
    OPEN c1
    CLOSE c1
    DEALLOCATE c1
END";

            Assert.Empty(CursorLifecycleExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(CursorLifecycleExtractor.Extract("CREATE PROCEDURE ((("));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CursorLifecycleExtractorTests"`
Expected: 컴파일 실패.

- [ ] **Step 3: 추출기를 만든다**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">DECLARE CURSOR의 줄 번호.</param>
    /// <param name="CursorName">커서 이름.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record CursorLifecycleFact(int Line, string CursorName, string Sentence);

    /// <summary>
    /// 커서의 수명 주기에서 확정할 수 있는 두 가지를 뽑는다 - OPEN~CLOSE 사이 RETURN 관측과
    /// LOCAL 미지정.
    ///
    /// [왜 렉시컬 관측인가] 완전한 경로 분석 대신 "OPEN과 CLOSE 사이에 RETURN이
    /// 있다"는 관측만 싣는다. 실측 두 건(UP_UTIL_SETTLE_SUMMARY_ETC:74-79,126-131과
    /// UP_UTIL_SETTLE_PROC_ETC:137-141)이 걸린 모양이 그것이고, 일반 경로 분석은
    /// 틀릴 여지가 크다. 기계 확정 표에 추측이 섞이면 표 전체의 신뢰가 무너진다.
    ///
    /// [왜 LOCAL 미지정이 사실인가] DECLARE CURSOR에 LOCAL이 없으면 커서 범위가
    /// DB의 default_to_local_cursor 설정에 달린다(SQL Server 기본값 OFF = GLOBAL).
    /// [M-a 수정 - 2026-08-22, 이력 문서 낡음 정리] "GLOBAL이면 같은 연결에서 재호출
    /// 시 DECLARE가 오류 16915로 실패해 처리 대상이 통째로 0이 된다"는 원안의 근거
    /// 문장이 여기 있었으나, 이 배치 어느 라운드도 이를 실행으로 검증하지 않았다 -
    /// 확인되지 않았다. SUMMARY_ETC 등급의 근거는 커서 범위 자체가 확정되지 않는다는
    /// 사실이지 오류 16915의 실측이 아니다. 또한 이 원안은 LOCAL 미지정만 게이트로
    /// 삼았는데, GLOBAL이 명시된 커서까지 이 문장을 내는 결함이 있었다(GLOBAL이면
    /// default_to_local_cursor와 무관하게 범위가 전역으로 확정되므로 "설정에 달려
    /// 있다"는 문장이 거짓이 된다) - I1(Task 15, 2026-08-22 최종 브랜치 리뷰)이
    /// `!declaration.IsLocal && !declaration.IsGlobal`로 게이트를 고쳐 GLOBAL 명시
    /// 커서는 침묵하게 했다. 실제 소스는 이 docstring이 아니라
    /// `src/ReSet.Core/Services/CursorLifecycleExtractor.cs`를 봐라.
    /// </summary>
    public static class CursorLifecycleExtractor
    {
        public static IReadOnlyList<CursorLifecycleFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<CursorLifecycleFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<CursorLifecycleFact>();
                }

                var visitor = new CursorVisitor();
                fragment.Accept(visitor);

                var facts = new List<CursorLifecycleFact>();
                foreach (var declaration in visitor.Declarations)
                {
                    var openLine = visitor.OpenLineOf(declaration.Name);
                    var closeLine = visitor.CloseLineOf(declaration.Name);
                    var unclosed = openLine > 0
                        && closeLine > openLine
                        && visitor.ReturnLines.Any(l => l > openLine && l < closeLine);

                    // [M-a 수정 - 이 게이트는 원안이 `declaration.IsLocal`만 봤다. I1이
                    // GLOBAL 명시 커서까지 아래 문장을 내던 결함을 고쳐 이렇게 바꿨다 -
                    // GLOBAL이 명시되면 default_to_local_cursor와 무관하게 범위가
                    // 전역으로 확정되므로 "설정에 달려 있다"는 문장이 거짓이 된다.
                    var needsScopeSentence = !declaration.IsLocal && !declaration.IsGlobal;
                    if (!unclosed && !needsScopeSentence) continue;

                    var parts = new List<string>();
                    if (unclosed)
                    {
                        parts.Add("OPEN과 CLOSE 사이에 RETURN이 있어 그 경로에서는 CLOSE/DEALLOCATE에 도달하지 않습니다");
                    }
                    if (needsScopeSentence)
                    {
                        // [M-a 수정] 원안은 "서버의"였다 - default_to_local_cursor는
                        // 서버가 아니라 데이터베이스 옵션이다(Wave 5 실측, 위 산문 참고).
                        parts.Add("LOCAL도 GLOBAL도 지정되지 않아 커서 범위가 데이터베이스의 default_to_local_cursor 설정에 달려 있습니다");
                    }

                    facts.Add(new CursorLifecycleFact(
                        declaration.Line, declaration.Name, string.Join(". ", parts) + "."));
                }

                return facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[CursorLifecycleExtractor] 커서 수명 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<CursorLifecycleFact>();
            }
        }

        private sealed record CursorDeclaration(int Line, string Name, bool IsLocal, bool IsGlobal);

        private sealed class CursorVisitor : TSqlFragmentVisitor
        {
            private readonly Dictionary<string, int> _opens = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _closes = new(StringComparer.OrdinalIgnoreCase);

            public List<CursorDeclaration> Declarations { get; } = new();
            public List<int> ReturnLines { get; } = new();

            public int OpenLineOf(string name) => _opens.TryGetValue(name, out var l) ? l : 0;
            public int CloseLineOf(string name) => _closes.TryGetValue(name, out var l) ? l : 0;

            public override void Visit(DeclareCursorStatement node)
            {
                var name = node.Name?.Value;
                if (string.IsNullOrWhiteSpace(name)) return;

                var options = node.CursorDefinition?.Options;
                var isLocal = options?.Any(o => o.OptionKind == CursorOptionKind.Local) == true;
                var isGlobal = options?.Any(o => o.OptionKind == CursorOptionKind.Global) == true;

                Declarations.Add(new CursorDeclaration(node.StartLine, name!, isLocal, isGlobal));
            }

            public override void Visit(OpenCursorStatement node)
            {
                var name = node.Cursor?.Name?.Value;
                if (!string.IsNullOrWhiteSpace(name) && !_opens.ContainsKey(name!))
                {
                    _opens[name!] = node.StartLine;
                }
            }

            public override void Visit(CloseCursorStatement node)
            {
                var name = node.Cursor?.Name?.Value;
                if (!string.IsNullOrWhiteSpace(name) && !_closes.ContainsKey(name!))
                {
                    _closes[name!] = node.StartLine;
                }
            }

            public override void Visit(ReturnStatement node) => ReturnLines.Add(node.StartLine);
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CursorLifecycleExtractorTests"`
Expected: PASS

- [ ] **Step 5: 집계자에 종류를 더한다**

```csharp
        public const string CursorKind = "커서 수명";
```

```csharp
            foreach (var fact in CursorLifecycleExtractor.Extract(ddlText))
            {
                facts.Add(new ExecutionSemanticFact(
                    CursorKind, fact.Line.ToString(), fact.CursorName, fact.Sentence));
            }
```

- [ ] **Step 6: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/CursorLifecycleExtractor.cs src/ReSet.Core/Services/ExecutionSemanticsFacts.cs tests/ReSet.Core.Tests/CursorLifecycleExtractorTests.cs
git commit -m "feat: 커서 OPEN-CLOSE 사이 RETURN 관측과 LOCAL 미지정을 실행 의미 표에 싣는다"
```

---

### Task 7: D — `CASE` 분기 표

`UIF_SettleYMD`의 🟠 3건(분기 뭉갬 · `>` 등호 생략 · 영 채움 누락)이 한 표로 닫힌다. 조건과 결과를 **원문 그대로** 싣는다.

**Files:**
- Create: `src/ReSet.Core/Services/CaseBranchExtractor.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs` (렌더러 + 4갈래)
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`, `src/ReSet.Core/Services/MechanicalValidator.cs`
- Test: `tests/ReSet.Core.Tests/CaseBranchExtractorTests.cs` (신규), `AiServiceTests_Rich.cs`, `MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `LocateHeadingSection`, `SplitTableRowCells` (Task 3에서 만듦)
- Produces: `CaseBranchExtractor.TableHeading`, `CaseBranchExtractor.Extract(string?) → IReadOnlyList<CaseBranchFact>`, `SpecExpectations.CaseBranches`, `ErrorType.CaseBranchTableMissing`

- [ ] **Step 1: 실패하는 추출기 테스트를 쓴다**

```csharp
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class CaseBranchExtractorTests
    {
        [Fact]
        public void Extract_SearchedCase_ShouldKeepOperatorsVerbatim()
        {
            // UIF_SettleYMD 실측: 명세서가 엄격 초과(>)를 "비교해"로 뭉개 경계에서
            // 오프셋이 일주일 어긋났다. 조건은 원문 그대로여야 한다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN DATEPART(DW, GETDATE()) > 3 THEN 7 ELSE 0 END
    RETURN @v
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Equal("WHEN 1", facts[0].Ordinal);
            Assert.Contains(">", facts[0].Condition);
            Assert.Equal("ELSE", facts[1].Ordinal);
            Assert.Equal("(그 외 전부)", facts[1].Condition);
        }

        [Fact]
        public void Extract_SimpleCase_ShouldRecordTheInputExpressionInEachCondition()
        {
            const string ddl = @"
CREATE FUNCTION dbo.F(@p VARCHAR(2)) RETURNS INT AS
BEGIN
    RETURN CASE @p WHEN '02' THEN 2 WHEN '03' THEN 3 END
END";

            var facts = CaseBranchExtractor.Extract(ddl);

            Assert.Equal(2, facts.Count);
            Assert.Contains("@p", facts[0].Condition);
            Assert.Contains("'02'", facts[0].Condition);
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(CaseBranchExtractor.Extract("CREATE FUNCTION ((("));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CaseBranchExtractorTests"`
Expected: 컴파일 실패.

- [ ] **Step 3: 추출기를 만든다**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">이 분기의 줄 번호.</param>
    /// <param name="Ordinal">"WHEN n" 또는 "ELSE".</param>
    /// <param name="Condition">조건 원문. ELSE는 "(그 외 전부)".</param>
    /// <param name="Result">결과 원문.</param>
    public sealed record CaseBranchFact(int Line, string Ordinal, string Condition, string Result);

    /// <summary>
    /// CASE 식의 분기를 순서대로 전수 뽑는다. 조건과 결과 모두 원문 그대로다.
    ///
    /// [왜 원문 그대로인가 - 2026-08-22 축 A 감사]
    /// UIF_SettleYMD에서 🟠 3건이 났고 셋 다 요약이 원인이었다. (1) SettleCount = 2와
    /// ELSE를 하나로 뭉개 제3 구간 판정이 잘못된 분기에 붙었다. (2) 엄격 초과(&gt;)를
    /// "비교해"로 적어 경계에서 오프셋이 일주일 어긋났다. (3) 결과식의
    /// RIGHT('0' + CONVERT(VARCHAR(2), SettleDayN), 2) 영 채움이 "결합합니다"로
    /// 요약돼 한 자리 일자에서 7자 문자열이 됐다. 셋 다 원문을 그대로 실으면 닫힌다.
    ///
    /// [왜 표를 따로 두는가] 실행 의미 표와 행 수의 자릿수가 다르다 - 한 함수에서
    /// WHEN이 24개 나는 실측(UF_GET_COMM4CLIENT4INTEREST)이 있다. 한 표에 섞으면
    /// 다른 종류가 묻힌다.
    /// </summary>
    public static class CaseBranchExtractor
    {
        public const string TableHeading = "### CASE 분기 (기계 확정 — 수정 금지)";

        public const string ElseConditionText = "(그 외 전부)";

        public static IReadOnlyList<CaseBranchFact> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<CaseBranchFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<CaseBranchFact>();
                }

                var visitor = new CaseVisitor();
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[CaseBranchExtractor] CASE 분기 수집 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<CaseBranchFact>();
            }
        }

        /// <summary>
        /// 조각의 원문 텍스트를 토큰 스트림에서 복원한다.
        ///
        /// `internal`인 이유는 Task 9의 ExpressionTypePathExtractor도 CAST 식 원문을
        /// 같은 방식으로 복원하기 때문이다 - 같은 관용구를 두 번 쓰면 한쪽만 고쳐졌을 때
        /// 표마다 원문 표기가 갈린다. 세 번째 소비자가 생기면 그때 중립 헬퍼 클래스로
        /// 옮긴다(선례: SplitTableRowCells가 MarkdownTableCellCodec으로 옮겨 간 경위).
        /// </summary>
        internal static string TextOf(TSqlFragment? fragment)
        {
            if (fragment == null) return string.Empty;
            return string.Concat(
                fragment.ScriptTokenStream
                    .Skip(fragment.FirstTokenIndex)
                    .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                    .Select(t => t.Text)).Trim();
        }

        private sealed class CaseVisitor : TSqlFragmentVisitor
        {
            public List<CaseBranchFact> Facts { get; } = new();

            public override void Visit(SearchedCaseExpression node)
            {
                var ordinal = 1;
                foreach (var clause in node.WhenClauses)
                {
                    Facts.Add(new CaseBranchFact(
                        clause.StartLine,
                        $"WHEN {ordinal++}",
                        TextOf(clause.WhenExpression),
                        TextOf(clause.ThenExpression)));
                }

                AddElse(node.ElseExpression, node.StartLine);
            }

            public override void Visit(SimpleCaseExpression node)
            {
                var input = TextOf(node.InputExpression);
                var ordinal = 1;
                foreach (var clause in node.WhenClauses)
                {
                    Facts.Add(new CaseBranchFact(
                        clause.StartLine,
                        $"WHEN {ordinal++}",
                        $"{input} = {TextOf(clause.WhenExpression)}",
                        TextOf(clause.ThenExpression)));
                }

                AddElse(node.ElseExpression, node.StartLine);
            }

            private void AddElse(ScalarExpression? elseExpression, int fallbackLine)
            {
                if (elseExpression == null) return;
                Facts.Add(new CaseBranchFact(
                    elseExpression.StartLine > 0 ? elseExpression.StartLine : fallbackLine,
                    "ELSE",
                    ElseConditionText,
                    TextOf(elseExpression)));
            }
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~CaseBranchExtractorTests"`
Expected: PASS

- [ ] **Step 5: 렌더러를 만들고 4갈래에 배선한다**

`AiService.cs`의 `BuildExecutionSemanticsTableLines` 뒤에 더한다.

```csharp
        /// <summary>
        /// 「CASE 분기」 표를 렌더한다. 조건·결과 모두 원문 그대로 실린다 -
        /// 요약이 곧 결함이었다(UIF_SettleYMD 🟠 3건).
        /// </summary>
        private static List<string> BuildCaseBranchTableLines(IReadOnlyList<CaseBranchFact> facts)
        {
            var lines = new List<string>
            {
                "   [CRITICAL CASE BRANCH TABLE] The following CASE branches are MACHINE-DERIVED from the source DDL. Copy this table verbatim into `## 로직 흐름 요약` under the exact heading shown. Never merge branches, never paraphrase a comparison operator, and never summarise a result expression - the verbatim text is the contract.",
                $"   {CaseBranchExtractor.TableHeading}",
                "   | 라인 | 순서 | 조건 원문 | 결과 원문 |",
                "   | :--- | :--- | :--- | :--- |"
            };

            foreach (var fact in facts)
            {
                lines.Add(
                    $"   | {fact.Line} | {EscapeTableCell(fact.Ordinal)} | "
                    + $"{EscapeTableCell(fact.Condition)} | {EscapeTableCell(fact.Result)} |");
            }

            lines.Add("");
            return lines;
        }
```

**갈래는 손대지 않는다.** Task 2가 만든 진입점 `BuildMachineFactBlockLines` 안에 한 블록을 더하면 4갈래가 자동으로 받는다 — 이것이 D5가 사는 값이다.

`BuildMachineFactBlockLines`의 `return lines;` 바로 앞에 더한다.

```csharp
            var caseBranches = CaseBranchExtractor.Extract(spDef.DdlText);
            if (caseBranches.Count > 0)
            {
                lines.AddRange(BuildCaseBranchTableLines(caseBranches));
            }
```

- [ ] **Step 6: 프롬프트 테스트를 쓰고 통과를 확인한다**

`AiServiceTests_Rich.cs`에 추가한다.

```csharp
        private static SpDefinition ProbeCaseBranchSpDef()
        {
            var spDef = new SpDefinition
            {
                Schema = "dbo",
                Name = "SETTLE_YMD",
                DdlText = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN DATEPART(DW, GETDATE()) > 3 THEN 7 ELSE 0 END
    RETURN @v
END"
            };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };
            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldPrefillTheCaseBranchTable()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeCaseBranchSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains(CaseBranchExtractor.TableHeading, body);
            Assert.Contains(CaseBranchExtractor.ElseConditionText, body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutCase_ShouldOmitTheCaseBranchTable()
        {
            var (service, handler) = CreateProbe();

            await service.GenerateSpecificationAsync(ProbeSpDef(), "지침", null);

            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain(CaseBranchExtractor.TableHeading, body);
        }
```

Run: `dotnet test --filter "CaseBranchTable"`
Expected: PASS

- [ ] **Step 7: L1 검사를 더한다**

`ErrorType`의 `General` 앞에 `CaseBranchTableMissing,`을 더한다.

`SpecExpectations`에 프로퍼티·수집·AND-체인 항·이니셜라이저를 Task 3과 같은 방식으로 더한다.

```csharp
        /// <summary>CASE 분기 원문. CheckCaseBranches가 소비한다.</summary>
        public IReadOnlyList<CaseBranchFact> CaseBranches { get; init; }
            = Array.Empty<CaseBranchFact>();
```

```csharp
            var caseBranches = CaseBranchExtractor.Extract(spDef.DdlText);
```

```csharp
                // executionSemantics와 같은 이유로 중복항이 아니다 - DML이 하나도 없는
                // 스칼라 함수도 CASE를 가질 수 있다.
                && caseBranches.Count == 0
```

```csharp
                CaseBranches = caseBranches,
```

`MechanicalValidator`에 검사를 더한다. `CheckExecutionSemantics`와 같은 모양이고 행 키만 다르다.

```csharp
        /// <summary>
        /// 기계 확정 CASE 분기 표가 명세서에 옮겨졌는지 본다. 재료가 없으면 조용히
        /// 건너뛴다(CheckExecutionSemantics와 같은 가드).
        ///
        /// 행 키는 (라인, 순서, 조건 원문) 셋이다. 결과 원문까지 넣지 않는 이유는
        /// 결과식이 여러 줄에 걸치면 모델이 줄바꿈을 공백으로 정규화해 옮기는 것이
        /// 정상이기 때문이다 - 조건까지 일치하면 행은 이미 특정된다.
        /// </summary>
        private static void CheckCaseBranches(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.CaseBranches.Count == 0) return;

            try
            {
                var lines = MarkdownSectionLocator.SplitLines(markdown);
                var (headingIndex, endIndex) = LocateHeadingSection(
                    lines, CaseBranchExtractor.TableHeading);

                if (headingIndex < 0)
                {
                    var missing =
                        $"기계 확정 CASE 분기 표가 명세서에 없습니다. `{CaseBranchExtractor.TableHeading}` "
                        + $"헤딩과 {expectations.CaseBranches.Count}개 행을 그대로 옮겨야 합니다.";
                    result.Errors.Add(missing);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.CaseBranchTableMissing,
                        Message = missing
                    });
                    return;
                }

                var rowLines = new List<string>();
                for (var i = headingIndex + 1; i < endIndex; i++)
                {
                    if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                    {
                        rowLines.Add(lines[i]);
                    }
                }

                foreach (var fact in expectations.CaseBranches)
                {
                    var lineToken = fact.Line.ToString();
                    var present = rowLines.Any(row =>
                    {
                        var cells = SplitTableRowCells(row);
                        return cells.Any(c => c == lineToken)
                            && cells.Any(c => c == fact.Ordinal)
                            && cells.Any(c => c == fact.Condition);
                    });
                    if (present) continue;

                    var message =
                        $"CASE 분기 표에 라인 {fact.Line}의 `{fact.Ordinal}` 행이 없거나 조건 원문이 "
                        + $"다릅니다. `{fact.Condition}`을 그대로 옮겨야 합니다 - 분기를 합치거나 "
                        + "비교 연산자를 말로 바꾸면 원문에서 찾을 수 없습니다.";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.CaseBranchTableMissing,
                        Message = message,
                        RawContext = $"{fact.Ordinal} @ line {fact.Line}"
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MechanicalValidator] CASE 분기 표 대조 실패 - 이 검사만 건너뜁니다.");
            }
        }
```

`Validate`의 나열에 `CheckCaseBranches(cleansed, expectations, result);`를 더한다.

- [ ] **Step 8: L1 테스트를 쓰고 통과를 확인한다**

`MechanicalValidatorTests.cs`에 추가한다. 픽스처 DDL에 `UPDATE`/`DELETE`가 없어야 `DmlScopeFacts`가 비고, 그래야 이른 반환 AND-체인에 `caseBranches` 항을 빠뜨렸을 때 테스트가 실제로 실패한다.

```csharp
        private static SpDefinition CaseBranchSpDef()
        {
            return new SpDefinition
            {
                Schema = "dbo",
                Name = "F",
                DdlText = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    DECLARE @v INT
    SET @v = CASE WHEN DATEPART(DW, GETDATE()) > 3 THEN 7 ELSE 0 END
    RETURN @v
END",
                StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true }
            };
        }

        [Fact]
        public void From_WithOnlyCaseBranches_ShouldNotReturnNull()
        {
            var expectations = SpecExpectations.From(CaseBranchSpDef());

            Assert.NotNull(expectations);
            Assert.NotEmpty(expectations!.CaseBranches);
            // 격리 확인 - 다른 재료가 이 픽스처를 살려 주고 있지 않은지 못박는다.
            Assert.Empty(expectations.DmlScopeFacts);
        }

        [Fact]
        public void Validate_MissingCaseBranchTable_ShouldReportAnError()
        {
            var expectations = SpecExpectations.From(CaseBranchSpDef());
            var validator = new MechanicalValidator();

            var result = validator.Validate("## 개요\n표가 없다.\n", expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.CaseBranchTableMissing);
        }

        [Fact]
        public void Validate_MergedCaseBranches_ShouldStillReportTheMissingRow()
        {
            // UIF_SettleYMD 실측: 두 분기를 하나로 뭉갠 것이 🟠이었다. 헤딩만 있고
            // 행이 빠지면 통과해서는 안 된다.
            var expectations = SpecExpectations.From(CaseBranchSpDef());
            var markdown =
                "## 로직 흐름 요약\n\n"
                + CaseBranchExtractor.TableHeading + "\n\n"
                + "| 라인 | 순서 | 조건 원문 | 결과 원문 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + "| 5 | WHEN 1 | 요일을 비교해 | 7 |\n";
            var validator = new MechanicalValidator();

            var result = validator.Validate(markdown, expectations);

            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.CaseBranchTableMissing);
        }

        [Fact]
        public void Validate_WithCaseBranchTableCopied_ShouldNotReportThatError()
        {
            var expectations = SpecExpectations.From(CaseBranchSpDef());
            var rows = string.Concat(expectations!.CaseBranches.Select(
                f => $"| {f.Line} | {f.Ordinal} | {f.Condition} | {f.Result} |\n"));
            var markdown =
                "## 로직 흐름 요약\n\n"
                + CaseBranchExtractor.TableHeading + "\n\n"
                + "| 라인 | 순서 | 조건 원문 | 결과 원문 |\n"
                + "| :--- | :--- | :--- | :--- |\n"
                + rows;
            var validator = new MechanicalValidator();

            var result = validator.Validate(markdown, expectations);

            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.CaseBranchTableMissing);
        }
```

Run: `dotnet test --filter "CaseBranch"`
Expected: PASS

- [ ] **Step 9: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/CaseBranchExtractor.cs src/ReSet.Core/Services/AiService.cs src/ReSet.Core/Services/SpecExpectations.cs src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/
git commit -m "feat: CASE 분기를 원문 그대로 기계 확정 표에 싣는다"
```

---

### Task 8: G — `DML 범위` 표에 `GROUP BY` 열

기존 표의 모양을 바꾸는 유일한 과제다. `MechanicalValidator.CheckDmlScopeTable`과 기존 테스트가 함께 움직인다.

**Files:**
- Modify: `src/ReSet.Core/Services/DmlScopeExtractor.cs` (`DmlScopeFact` · `DmlScopeVisitor`)
- Modify: `src/ReSet.Core/Services/AiService.cs:797-840` (`BuildDmlScopeTableLines` 헤더·행)
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`CheckDmlScopeTable`)
- Test: `tests/ReSet.Core.Tests/DmlScopeExtractorTests.cs`, `AiServiceTests_Rich.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `DmlScopeFact.GroupByColumns` (`IReadOnlyList<string>`) — 기존 소비자 전부가 새 속성을 무시해도 컴파일된다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Fact]
        public void Extract_InsertWithGroupBy_ShouldRecordTheGroupingKeys()
        {
            // UP_Util_Settle_Summary 실측: GROUP BY 첫 키 YMD가 매핑 표의 설명 칸에서
            // "그룹화 키"로 표기되지 않아, 표로 GROUP BY를 재구성하면 키가 빠졌다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
    @pi_strYMD CHAR(8)
AS
BEGIN
    INSERT INTO dbo.TSettleByTX (YMD, CLIENTID, CNT)
    SELECT YMD, CLIENTID, COUNT(*)
    FROM   dbo.TSettleMst
    WHERE  YMD = @pi_strYMD
    GROUP BY YMD, CLIENTID
END";

            var fact = Assert.Single(DmlScopeExtractor.Extract(ddl, "@pi_strYMD"));

            Assert.Equal(new[] { "YMD", "CLIENTID" }, fact.GroupByColumns);
        }
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScopeExtractorTests"`
Expected: 컴파일 실패 — `GroupByColumns`가 없다.

- [ ] **Step 3: `DmlScopeFact`에 속성을 더하고 수집한다**

`DmlScopeFact` record 선언 끝에 파라미터를 더한다. **기본값을 주어** 기존 생성 자리가 전부 컴파일되게 한다.

```csharp
    /// <param name="GroupByColumns">
    /// 문장의 GROUP BY 키. 없으면 빈 목록.
    ///
    /// 매핑 표의 설명 칸이 유일한 GROUP BY 기록처였고, 한 SP에서 세 문장의 첫 키가
    /// 통째로 빠진 실측이 있다(UP_Util_Settle_Summary). 기계 확정 열로 올려 그 자리를
    /// 산문에 맡기지 않는다.
    /// </param>
```

생성부(`DmlScopeVisitor`)에서 `QuerySpecification.GroupByClause`를 훑어 컬럼 이름을 모은다.

```csharp
        private static List<string> CollectGroupByColumns(QuerySpecification? query)
        {
            var columns = new List<string>();
            var clause = query?.GroupByClause;
            if (clause == null) return columns;

            foreach (var spec in clause.GroupingSpecifications)
            {
                if (spec is not ExpressionGroupingSpecification expr) continue;
                if (expr.Expression is not ColumnReferenceExpression column) continue;

                var name = column.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                if (!string.IsNullOrWhiteSpace(name)) columns.Add(name!);
            }

            return columns;
        }
```

- [ ] **Step 4: 렌더러와 L1을 함께 움직인다**

`BuildDmlScopeTableLines`의 헤더 줄(`AiService.cs:803`)에 열을 더한다.

```csharp
                "   | 문장 | 라인 | 대상 | WHERE 최상위 술어 컬럼(조인 결합 포함 · 대상 한정 아님) | 기준일 파라미터 적용(최상위 WHERE 기준) | 조인 키 | GROUP BY | ORDER BY |",
```

구분선 줄에도 `:---`를 하나 더하고, 행 조립에 칸을 더한다. `ORDER BY`가 `—`(문법상 불가)와 `(없음)`(절 부재)을 가르는 것과 같은 규약을 쓴다.

```csharp
                var groupBy = fact.GroupByColumns.Count == 0
                    ? "(없음)"
                    : string.Join(", ", fact.GroupByColumns);
```

`CheckDmlScopeTable`에 `GROUP BY` 값 검사를 더한다.

**실제 구현은 AND-체인이 아니다**(Wave 7 실측). 이 검사는 DDL 라인 번호가 DML 범위 절 안
어느 행의 어느 칸에든 나타나는지만 본다 — 술어·조인 키·`ORDER BY`를 칸 단위로 검증하지
않는다(`CheckOrderByExpressions`가 절 전체 `Contains`로 따로 덮는다). 그러니 검사를 통째로
재설계하지 말고 기존 per-fact 루프를 확장해라.

```csharp
                if (fact.GroupByColumns.Count == 0) continue;

                var groupByToken = string.Join(", ", fact.GroupByColumns);
```

`GroupByColumns`가 비면 **비교 자체를 하지 않는다.** 이것이 `(없음)` 충돌을 피하는 방법이다 —
그 토큰은 `조인 키` 칸에도 나오므로, 비교했다면 두 칸이 모두 `(없음)`일 때 검사가 자동으로
통과해 무력해진다.

- [ ] **Step 5: 기존 테스트를 고친다**

Run: `dotnet test --filter "FullyQualifiedName~DmlScope"`
Expected: 기존 표 테스트 몇 개가 열 수 변화로 FAIL한다. 실패한 테스트의 기대 문자열에 새 열을 반영해 고친다. **헤딩 상수로 단언하는 테스트는 손댈 필요가 없다** — 열이 늘어도 헤딩은 그대로다.

- [ ] **Step 6: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/DmlScopeExtractor.cs src/ReSet.Core/Services/AiService.cs src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/
git commit -m "feat: DML 범위 표에 GROUP BY 열을 더한다"
```

---

### Task 9: A — 식 타입 경로

`CAST(<산술식> AS INT)`에서 `/100.0`이 괄호 안에 있으면 `numeric → int`(절사), 밖에 있으면 `money → int`(반올림)다. 형제 함수 7개 중 2개만 뒤쪽이고 명세서 어디에도 그 갈림이 없다. **C#의 정수 캐스트는 절사이므로 자연스러운 번역이 바로 틀린 쪽**이다.

**Files:**
- Create: `src/ReSet.Core/Services/ExpressionTypePathExtractor.cs`
- Modify: `src/ReSet.Core/Services/ExecutionSemanticsFacts.cs`
- Test: `tests/ReSet.Core.Tests/ExpressionTypePathExtractorTests.cs` (신규)

**Interfaces:**
- Consumes: `ExecutionSemanticFact`
- Produces: `ExpressionTypePathExtractor.Extract(string? ddlText, IReadOnlyDictionary<string, string> columnTypes) → IReadOnlyList<TypePathFact>`, `ExecutionSemanticsFacts.TypePathKind`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
using System.Collections.Generic;
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class ExpressionTypePathExtractorTests
    {
        private static readonly Dictionary<string, string> NoColumns = new();

        [Fact]
        public void Extract_DivisionInsideTheCast_ShouldReportNumericTruncation()
        {
            // 실행 대조 2026-08-22: 10050 × 1.50%가 이 경로에서는 150이다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_intTxAmt MONEY) RETURNS INT AS
BEGIN
    DECLARE @v_intCommission MONEY
    SET @v_intCommission = 1.50
    RETURN CAST(@pi_intTxAmt * (@v_intCommission / 100.0) AS INT)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));

            Assert.Contains("numeric", fact.Sentence);
            Assert.Contains("절사", fact.Sentence);
        }

        [Fact]
        public void Extract_DivisionOutsideTheCast_ShouldReportMoneyRounding()
        {
            // 실행 대조 2026-08-22: 같은 값이 이 경로에서는 151이다.
            const string ddl = @"
CREATE FUNCTION dbo.F(@pi_intTxAmt MONEY) RETURNS INT AS
BEGIN
    DECLARE @v_intRate MONEY
    SET @v_intRate = 0.015
    RETURN CAST(@pi_intTxAmt * @v_intRate AS INT)
END";

            var fact = Assert.Single(ExpressionTypePathExtractor.Extract(ddl, NoColumns));

            Assert.Contains("money", fact.Sentence);
            Assert.Contains("반올림", fact.Sentence);
        }

        [Fact]
        public void Extract_UnknownLeafType_ShouldOmitTheRow()
        {
            // 기계 확정 표에 추측이 섞이면 표 전체의 신뢰가 무너진다.
            // 컬럼 타입 사전에 없는 컬럼이 잎으로 들어오면 행을 내지 않는다.
            const string ddl = @"
CREATE FUNCTION dbo.F() RETURNS INT AS
BEGIN
    RETURN (SELECT CAST(t.Amt * 2 AS INT) FROM dbo.T t)
END";

            Assert.Empty(ExpressionTypePathExtractor.Extract(ddl, NoColumns));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(ExpressionTypePathExtractor.Extract("CREATE FUNCTION (((", NoColumns));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ExpressionTypePathExtractorTests"`
Expected: 컴파일 실패.

- [ ] **Step 3: 추출기를 만든다**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Serilog;

namespace ReSet.Core.Services
{
    /// <param name="Line">CAST 식의 줄 번호.</param>
    /// <param name="Expression">CAST 식 원문.</param>
    /// <param name="Sentence">확정 사실 문장.</param>
    public sealed record TypePathFact(int Line, string Expression, string Sentence);

    /// <summary>
    /// CAST(&lt;산술식&gt; AS INT)의 피연산자 타입 경로를 판정한다.
    ///
    /// [실행으로 확정한 사실 - 2026-08-22, SQL Server 2022 16.0.4255.1]
    /// decimal/numeric이 money보다 데이터 형식 우선순위가 높다. 리터럴 100.0은
    /// numeric(4,1)이므로 CAST 안에 있으면 money 피연산자가 numeric으로 승격돼
    /// 결과가 0 방향 절사되고, 밖에 있으면 money * money가 남아 0에서 먼 쪽으로
    /// 반올림된다. 같은 값(10050 × 1.50%)이 앞은 150, 뒤는 151이다.
    ///
    /// [왜 잎 타입을 모르면 행을 내지 않는가] 기계 확정 표에 추측이 섞이면 표 전체의
    /// 신뢰가 무너진다. 컬럼·변수·파라미터 중 하나라도 타입을 모르면 그 CAST는
    /// 침묵한다 - 실패 방향이 안전한 쪽이다.
    /// </summary>
    public static class ExpressionTypePathExtractor
    {
        public const string MoneyRoundingSentence =
            "피연산자가 money로 유지되어 money → int 변환입니다. 0에서 먼 쪽으로 반올림합니다(12.5 → 13, -12.5 → -13).";

        public const string NumericTruncationSentence =
            "리터럴이 numeric이라 피연산자가 numeric으로 승격되어 numeric → int 변환입니다. 0 방향으로 절사합니다(12.5 → 12, -12.5 → -12).";

        public static IReadOnlyList<TypePathFact> Extract(
            string? ddlText, IReadOnlyDictionary<string, string> columnTypes)
        {
            if (string.IsNullOrWhiteSpace(ddlText)) return Array.Empty<TypePathFact>();

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out var errors);
                if (fragment == null || (errors != null && errors.Count > 0))
                {
                    return Array.Empty<TypePathFact>();
                }

                var declared = new DeclaredTypeVisitor();
                fragment.Accept(declared);

                var visitor = new CastVisitor(declared.Types, columnTypes);
                fragment.Accept(visitor);
                return visitor.Facts;
            }
            catch (Exception ex)
            {
                // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
                Log.Warning(ex, "[ExpressionTypePathExtractor] 타입 경로 판정 실패 - 빈 목록으로 진행합니다.");
                return Array.Empty<TypePathFact>();
            }
        }

        /// <summary>파라미터와 지역 변수의 선언 타입을 모은다.</summary>
        private sealed class DeclaredTypeVisitor : TSqlFragmentVisitor
        {
            public Dictionary<string, string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(DeclareVariableElement node) => Record(node.VariableName?.Value, node.DataType);

            public override void Visit(ProcedureParameter node) => Record(node.VariableName?.Value, node.DataType);

            private void Record(string? name, DataTypeReference? type)
            {
                var typeName = (type as SqlDataTypeReference)?.SqlDataTypeOption.ToString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(typeName)) return;
                Types[name!] = typeName!.ToLowerInvariant();
            }
        }

        private sealed class CastVisitor : TSqlFragmentVisitor
        {
            private readonly IReadOnlyDictionary<string, string> _variables;
            private readonly IReadOnlyDictionary<string, string> _columns;

            public CastVisitor(
                IReadOnlyDictionary<string, string> variables,
                IReadOnlyDictionary<string, string> columns)
            {
                _variables = variables;
                _columns = columns;
            }

            public List<TypePathFact> Facts { get; } = new();

            public override void Visit(CastCall node)
            {
                var target = (node.DataType as SqlDataTypeReference)?.SqlDataTypeOption;
                if (target != SqlDataTypeOption.Int) return;

                var leaves = new LeafCollector();
                node.Parameter?.Accept(leaves);
                if (leaves.HasUnknown) return;

                var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var leaf in leaves.Variables)
                {
                    if (!_variables.TryGetValue(leaf, out var type)) return;
                    kinds.Add(type);
                }
                foreach (var leaf in leaves.Columns)
                {
                    if (!_columns.TryGetValue(leaf, out var type)) return;
                    kinds.Add(type.ToLowerInvariant());
                }
                foreach (var literalKind in leaves.LiteralKinds) kinds.Add(literalKind);

                if (kinds.Count == 0) return;

                var promotesToNumeric = kinds.Any(
                    k => k.StartsWith("numeric", StringComparison.Ordinal)
                      || k.StartsWith("decimal", StringComparison.Ordinal));
                var hasMoney = kinds.Any(k => k == "money" || k == "smallmoney");

                if (!hasMoney && !promotesToNumeric) return;

                var sentence = promotesToNumeric ? NumericTruncationSentence : MoneyRoundingSentence;
                Facts.Add(new TypePathFact(node.StartLine, CaseBranchExtractor.TextOf(node), sentence));
            }
        }

        /// <summary>CAST 인자 식의 잎 노드를 모은다. 모르는 잎이 하나라도 있으면 표시한다.</summary>
        private sealed class LeafCollector : TSqlFragmentVisitor
        {
            public HashSet<string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> LiteralKinds { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool HasUnknown { get; private set; }

            public override void Visit(VariableReference node)
            {
                if (!string.IsNullOrWhiteSpace(node.Name)) Variables.Add(node.Name);
            }

            public override void Visit(ColumnReferenceExpression node)
            {
                var name = node.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value;
                if (string.IsNullOrWhiteSpace(name)) { HasUnknown = true; return; }
                Columns.Add(name!);
            }

            public override void Visit(NumericLiteral node) => LiteralKinds.Add("numeric");

            public override void Visit(IntegerLiteral node) => LiteralKinds.Add("int");

            public override void Visit(FunctionCall node) => HasUnknown = true;
        }
    }
}
```

- [ ] **Step 4: 추출기 테스트가 통과하는 것을 확인한다**

Run: `dotnet test --filter "FullyQualifiedName~ExpressionTypePathExtractorTests"`
Expected: PASS

- [ ] **Step 5: 집계자에 종류를 더한다**

`Collect`의 시그니처와 `BuildColumnTypeMap`은 **Task 2에서 이미 만들었다.** 여기서는 종류 상수와 수집 한 블록만 더한다.

```csharp
        public const string TypePathKind = "식 타입 경로";
```

`Collect`에 더한다.

```csharp
            foreach (var fact in ExpressionTypePathExtractor.Extract(ddlText, columnTypes))
            {
                facts.Add(new ExecutionSemanticFact(
                    TypePathKind, fact.Line.ToString(), fact.Expression, fact.Sentence));
            }
```

- [ ] **Step 6: 전체 테스트와 커밋**

```bash
dotnet test
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
git add src/ReSet.Core/Services/ExpressionTypePathExtractor.cs src/ReSet.Core/Services/ExecutionSemanticsFacts.cs src/ReSet.Core/Services/AiService.cs src/ReSet.Core/Services/SpecExpectations.cs tests/ReSet.Core.Tests/ExpressionTypePathExtractorTests.cs
git commit -m "feat: CAST의 타입 경로와 반올림 방향을 실행 의미 표에 싣는다"
```

---

### Task 10: 감사 계약 갱신

승격의 값어치는 감사가 대조해야 실현된다. `Spec.md`에 표가 실려도 계약이 그 표를 모르면 다음 감사는 검증하지 않고 지나간다.

**Files:**
- Modify: `.claude/skills/reset-consistency-audit/references/axis-a.md`

**Interfaces:**
- Consumes: `ExecutionSemanticsFacts.TableHeading`, `CaseBranchExtractor.TableHeading` (문자열 값)
- Produces: 없음 (문서)

- [ ] **Step 1: 3-1절 대조 항목에 새 표 2종을 더한다**

기존 표 6종을 다루는 자리와 같은 어법으로, 아래 두 항목을 3-1절의 대조 항목 표에 더한다.

```markdown
| `### 실행 의미 (기계 확정 — 수정 금지)` | 표가 있으면 행이 원문 그대로 실렸는가(`종류`·`라인`·`대상`·`확정 사실` 네 칸). 그리고 **표의 사실을 산문이 뒤집지 않았는가** — `DB 배치` 행이 로컬을 확정했는데 산문이 "크로스 DB 참조라고 단언할 수 없습니다"로 되짚으면 결함이다. `식 타입 경로` 행이 반올림을 확정했는데 산문이 "소수 부분이 제거됩니다"로 적어도 같다 |
| `### CASE 분기 (기계 확정 — 수정 금지)` | 행 수가 원본 `CASE`의 `WHEN` + `ELSE` 전수와 맞는가. 조건 원문의 **비교 연산자가 말로 바뀌지 않았는가**(`>`를 "비교해"로). 결과 원문이 요약되지 않았는가(`RIGHT('0' + …, 2)` 같은 결합식을 "결합합니다"로) |
```

- [ ] **Step 2: 표 부재의 의미를 명시한다**

3-1절에 한 문단을 더한다. 스킬이 이미 `참조 함수` 표에 쓰는 어법을 그대로 쓴다.

```markdown
**두 표가 없는 것은 결함이 아니다.** 추출기가 재료를 하나도 찾지 못하면 조립기가 표를
싣지 않는다 — `CASE`가 없는 객체에 `CASE 분기` 표가 없는 것은 정상이다. **다만 표가
없는데 산문이 그 종류를 단정했으면 그것이 결함이다** — 표 없이 "이 함수는 절사합니다"
라고 적었으면 그 판단의 출처가 없다. 없는 것과 비어 있는 것을 가르라.
```

- [ ] **Step 3: 3-2-1 사각지대 절을 줄인다**

`CASE` 분기와 실행 의미가 기계 확정으로 올라오면 그 사실들을 산문이 홀로 지고 있던 구간이 사라진다. 사각지대가 맡던 일부를 표 대조로 옮긴다.

- [ ] **Step 4: `DML 범위` 표의 `GROUP BY` 열을 반영한다**

- [ ] **Step 5: 실측 수치 자리를 비워 둔다**

스킬은 `SP는 호출 75건이 전부 표에 실린다` 같은 실측을 담고 있다. 새 표의 실측은 재생성 후에야 나온다. **지어내지 말고** 그 자리를 비워 두고, 재생성 뒤 감사에서 채운다고 적는다.

- [ ] **Step 6: 커밋**

```bash
git add .claude/skills/reset-consistency-audit/references/axis-a.md
git commit -m "docs: 감사 계약에 실행 의미·CASE 분기 표 대조를 더한다"
```

---

### Task 11: 프로젝트 문서 동기화

**Files:**
- Modify: `AGENTS.md`, `docs/architecture.md` (§4.9가 프롬프트 문구 담당 절)

**Interfaces:**
- Consumes: Task 2~9의 최종 코드
- Produces: 없음 (문서)

- [ ] **Step 1: `reset-doc-sync` 스킬을 부른다**

Run: 세션에서 `/reset-doc-sync`

이 저장소의 문서 라우팅 표(`AGENTS.md:35`)가 프롬프트 문구를 `architecture.md §4.9` 소관으로 지정한다. 스킬이 그 절과 `AGENTS.md`를 코드와 대조해 갱신한다.

- [ ] **Step 2: 최종 기준선을 확인한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -E "warning CS" | sort -u | wc -l
dotnet test
```

Expected: 경고 8 · 실패 0 · 건너뜀 0

- [ ] **Step 3: 커밋**

```bash
git add AGENTS.md docs/architecture.md
git commit -m "docs: 기계 확정 표 2종을 아키텍처 문서에 반영한다"
```

---

## 완료 후

이 계획이 끝나면 명세서를 재생성하고 **축 A 감사를 다시 돌린다.** 캐시 키가 파일 해시라 고쳐진 명세서만 재검증되므로 값이 싸다. 남은 결함이 무엇인지 그때 확인한다 — 이 계획이 닫는 것은 A~H가 겨냥한 결함뿐이고, 개별 🟠 전부가 자동으로 사라지지는 않는다.

축 B는 그 뒤다. 현행 `agent/` 번들은 옛 명세서로 만든 것이라 지금 대조하면 세대 차이가 결함으로 잡힌다.
