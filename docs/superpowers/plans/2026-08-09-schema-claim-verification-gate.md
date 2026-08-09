# 명세서 스키마 주장 검증 게이트 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 명세서가 실존 컬럼을 "존재하지 않음"으로 단정하거나 같은 물리 테이블을 여러 표기로 쪼개는 것을 기계적으로 잡되, 프롬프트 입력 자체가 진실을 담지 못한 경우는 재생성이 아니라 경고로 분리해 표면화한다.

**Architecture:** "프롬프트에 어떤 컬럼이 실렸는가"를 아는 필터를 `AiService`에서 순수 클래스로 추출해 단일 권위로 만든다. 그 클래스가 렌더러와 L1 양쪽에 답을 준다. 같은 클래스가 "참조 컬럼이 통째로 사라졌는가"(A)를 판정하고, `MechanicalValidator`가 명세서 본문의 거짓 부재 주장(B)과 테이블 동일성 분열(②)을 판정한다.

**Tech Stack:** C# / .NET 10, xUnit, Serilog, `System.Text.RegularExpressions`

## Global Constraints

- 모든 사용자 노출 문자열과 코드 주석은 한국어. 기술 식별자는 원문 유지.
- L1 오류(`ValidationResult.Errors`)에 들어가는 것은 **전부 재생성으로 고칠 수 있어야 한다.** 재생성이 고칠 수 없는 것은 `Errors`가 아니라 `spDef.Warnings`로 간다. 이 불변식을 깨면 무한 재시도가 생긴다.
- 새 검사는 `MechanicalValidator.Validate`의 기존 soft-fail `try` 블록 **안**에 둔다. 검증기 자체 오류가 툴을 중단시키면 안 된다.
- 테스트는 `output/` 아래 경로를 읽지 않는다. `.gitignore` 대상이라 CI에 없다. 픽스처는 `tests/ReSet.Core.Tests/Fixtures/`에 커밋한다.
- `CodeObjectKey`는 **위치 레코드**다: `CodeObjectKey(string Database, string Schema, string Name, CodeObjectType Type)`. 매개변수 없는 생성자가 없으므로 객체 초기자 구문(`new CodeObjectKey { ... }`)은 컴파일되지 않는다. 네 인자를 모두 넘겨라.
- 각 태스크 끝에서 `dotnet build`와 `dotnet test`가 모두 초록이어야 커밋한다.
- 모든 가드는 뮤테이션으로 하중을 확인한다. 가드를 지웠는데 아무 테스트도 깨지지 않으면 그 테스트를 고친다.

---

## File Structure

**생성**

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/SchemaPromptColumnSelector.cs` | 프롬프트 스키마 표에 실릴 컬럼 결정 + 참조 컬럼 유실(A) 판정. 단일 권위 |
| `tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs` | 위의 테스트 |
| `tests/ReSet.Core.Tests/SchemaClaimGateRegressionTests.cs` | 실물 명세서 발췌 픽스처 기반 수용 테스트 |
| `tests/ReSet.Core.Tests/Fixtures/SettleCommUpdSchemaMismatchExcerpt.md` | COMM_UPD의 `### 스키마 불일치 컬럼` 표 발췌 (실물) |
| `tests/ReSet.Core.Tests/Fixtures/ExceptionProcCrudExcerpt.md` | EXCEPTION_PROC의 CRUD 분석 발췌 (실물) |

**수정**

| 파일 | 변경 |
|---|---|
| `src/ReSet.Core/Services/AiService.cs` | `FormatTableSchemaToMarkdown`이 선택기에 위임. `ExtractBaseName` 이전. 프롬프트 문장 추가 |
| `src/ReSet.Core/Services/SpecExpectations.cs` | `From(spDef)`로 교체. `PromptSchemaColumns`, `InputDefects` 추가 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | `ErrorType` 둘 추가, `CheckSchemaClaims`, `CheckTableIdentitySplit`, `BuildSuggestedPromptFix` 블록 둘 |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 224행 `From(spDef)` + `InputDefects` → `spDef.Warnings` |
| `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` | 689·872행의 `FromStaticAnalysis` 호출 갱신 |
| `docs/architecture.md`, `AGENTS.md` | 게이트의 존재와 두 진실의 원천 구분 기록 |

---

## Task 1: 프롬프트 컬럼 선택기 추출

`AiService.FormatTableSchemaToMarkdown` 안에만 있는 "어떤 컬럼이 프롬프트에 실리는가" 지식을 순수 클래스로 꺼낸다. **동작은 한 글자도 바뀌지 않는다** — 순수 추출이다.

**Files:**
- Create: `src/ReSet.Core/Services/SchemaPromptColumnSelector.cs`
- Create: `tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs` (`FormatTableSchemaToMarkdown` 50-157행, `ExtractBaseName` 181-188행)

**Interfaces:**
- Consumes: `DependencyInfo`, `SpDefinition`, `StaticAnalysisNormalizer.Canonicalize`, `StaticAnalysisNormalizer.CanonicalizeParts`
- Produces:
  - `public static IReadOnlySet<string> SchemaPromptColumnSelector.Select(DependencyInfo dep, SpDefinition spDef)`
  - `public static string SchemaPromptColumnSelector.ExtractBaseName(string? qualifiedOrRawName)`
  - `internal static bool SchemaPromptColumnSelector.KeyMatchesDependency(string key, DependencyInfo dep, SpDefinition spDef)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs`:

```csharp
using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SchemaPromptColumnSelectorTests
    {
        private static DependencyInfo Dep(
            string name, string? database, params (string Name, bool Pk)[] columns)
        {
            var dep = new DependencyInfo { Name = name, Schema = "dbo", Database = database, Type = "USER_TABLE" };
            foreach (var (columnName, pk) in columns)
            {
                dep.Columns.Add(new ColumnInfo { ColumnName = columnName, DataType = "int", IsPrimaryKey = pk });
            }
            return dep;
        }

        private static SpDefinition Sp(string? database, Dictionary<string, List<string>> referenced)
        {
            return new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = database == null ? null : new CodeObjectKey(database, "dbo", "UP_PROBE", CodeObjectType.Procedure),
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ReferencedColumnsPerTable = new Dictionary<string, List<string>>(
                        referenced, System.StringComparer.OrdinalIgnoreCase)
                }
            };
        }

        [Fact]
        public void Select_WithReferencedColumns_ShouldKeepOnlyThoseAndKeys()
        {
            // Arrange - AMT는 참조되고 ID는 PK다. ETC는 둘 다 아니라 빠져야 한다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("ID", true), ("AMT", false), ("ETC", false));
            var sp = Sp("SETTLE_POQ_DB", new Dictionary<string, List<string>>
            {
                ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "AMT" }
            });

            // Act
            var shown = SchemaPromptColumnSelector.Select(dep, sp);

            // Assert
            Assert.Contains("AMT", shown);
            Assert.Contains("ID", shown);
            Assert.DoesNotContain("ETC", shown);
        }

        [Fact]
        public void Select_WhenNothingMatches_ShouldFallBackToAllColumns()
        {
            // Arrange - 참조 정보도 PK/FK도 인덱스도 없으면 필터를 걸지 않는다.
            // 이것이 현행 폴백이고, 과다 포함은 무해하지만 과소 포함은 거짓 "컬럼 없음"을 만든다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("ID", false), ("AMT", false));
            var sp = Sp("SETTLE_POQ_DB", new Dictionary<string, List<string>>());

            // Act
            var shown = SchemaPromptColumnSelector.Select(dep, sp);

            // Assert
            Assert.Equal(2, shown.Count);
        }

        [Fact]
        public void Select_WithoutDbContext_ShouldMatchByBaseName()
        {
            // Arrange - ObjectKey.Database가 없으면 3-part 정식 비교가 성립하지 않아
            // 베이스 이름 비교로 내려간다. 이 폴백이 없으면 컬럼이 통째로 유실된다.
            var dep = Dep("TSettleMst", null, ("AMT", false), ("ETC", false));
            var sp = Sp(null, new Dictionary<string, List<string>>
            {
                ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "AMT" }
            });

            // Act
            var shown = SchemaPromptColumnSelector.Select(dep, sp);

            // Assert
            Assert.Contains("AMT", shown);
            Assert.DoesNotContain("ETC", shown);
        }

        [Fact]
        public void Select_WithDbContext_ShouldNotMergeDifferentDatabases()
        {
            // Arrange - DB 컨텍스트가 있으면 정식 3-part 비교를 유지해야 한다.
            // dbo.TPGProperty와 PaymentDB.dbo.TPGProperty를 베이스 이름으로 합치면
            // 서로 다른 물리 테이블의 컬럼이 섞인다.
            var dep = Dep("TPGProperty", "SETTLE_POQ_DB", ("OPT", false), ("ETC", false));
            var sp = Sp("SETTLE_POQ_DB", new Dictionary<string, List<string>>
            {
                ["PaymentDB.dbo.TPGProperty"] = new List<string> { "OPT" }
            });

            // Act
            var shown = SchemaPromptColumnSelector.Select(dep, sp);

            // Assert - 매칭이 없으므로 폴백이 걸려 전체가 나온다. 섞이지는 않는다.
            Assert.Equal(2, shown.Count);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SchemaPromptColumnSelectorTests"
```

기대: 컴파일 실패 — `SchemaPromptColumnSelector`가 없다.

- [ ] **Step 3: 선택기를 만든다**

`src/ReSet.Core/Services/SchemaPromptColumnSelector.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 프롬프트의 스키마 표에 어떤 컬럼이 실리는지 결정하는 단일 권위.
    ///
    /// 이 지식이 AiService.FormatTableSchemaToMarkdown 안에만 있으면 L1이 알 수 없다.
    /// 렌더링의 부수효과로 어딘가에 기록하는 방식은 택하지 않았다 - 렌더 경로가 둘이라
    /// (BuildSpMetadataTexts, RAG 경로) 어느 쪽이 마지막에 기록했는지에 결과가 달라진다.
    ///
    /// 이 필터는 토큰 절약용 최적화이지 정확성 장치가 아니다. 과다 포함은 표에 불필요한
    /// 행을 몇 개 더할 뿐이지만, 과소 포함은 모델이 그 컬럼을 "존재하지 않는다"고 잘못
    /// 기록한다 - 14개 명세서를 망가뜨린 바로 그 결함이다.
    /// </summary>
    public static class SchemaPromptColumnSelector
    {
        /// <summary>
        /// 이 의존성에 대해 프롬프트 스키마 표가 실제로 보여줄 컬럼 이름들.
        ///
        /// 반환값은 "keepCols"가 아니라 <b>실제로 렌더링되는 집합</b>이다. keepCols가
        /// 비면 필터를 걸지 않고 전체를 찍는 폴백이 있어 둘이 다르다. L1이 대조해야 하는
        /// 것은 AI가 실제로 본 것이므로 후자여야 한다.
        /// </summary>
        public static IReadOnlySet<string> Select(DependencyInfo dep, SpDefinition spDef)
        {
            var keepCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) AST에서 감지한 실제 참조 컬럼
            var analysis = spDef.StaticAnalysis;
            if (analysis?.ReferencedColumnsPerTable != null)
            {
                foreach (var kvp in analysis.ReferencedColumnsPerTable)
                {
                    if (!KeyMatchesDependency(kvp.Key, dep, spDef)) continue;
                    foreach (var c in kvp.Value) keepCols.Add(c);
                }
            }

            // 2) PK / FK 컬럼
            foreach (var col in dep.Columns)
            {
                if (col.IsPrimaryKey || col.IsForeignKey) keepCols.Add(col.ColumnName);
            }

            // 3) 인덱스 구성 컬럼
            if (dep.Indexes != null)
            {
                foreach (var idx in dep.Indexes)
                {
                    foreach (var c in idx.Columns) keepCols.Add(c);
                }
            }

            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in dep.Columns)
            {
                // keepCols가 비어 있으면 정적 분석 정보가 없는 것으로 보고 전체를 찍는다.
                if (keepCols.Count > 0 && !keepCols.Contains(col.ColumnName)) continue;
                shown.Add(col.ColumnName);
            }

            return shown;
        }

        /// <summary>
        /// ReferencedColumnsPerTable의 키 하나가 이 의존성의 것인지 판정한다.
        ///
        /// 비교 양변의 한정 가능한 출처가 다르다. 의존성 쪽은 dep.Database가 있으면
        /// 그것으로 한정되지만, 키 쪽의 비한정 이름(예: "TSettleMst")이 암묵적으로
        /// 속하는 DB는 "분석 대상 객체 자신의 DB"이지 하필 지금 비교 중인 의존성의
        /// DB가 아니다. dep.Database로 키를 한정하면 존재하지 않는 테이블을 지어내는
        /// 것과 같다. 그래서 키 쪽 한정 가능 여부는 오직 spDef.ObjectKey?.Database
        /// 하나로만 결정된다.
        ///
        /// 컨텍스트가 없으면 베이스 이름 비교로 내려가 과다 포함 쪽으로 기운다. 이
        /// 폴백은 완전히 무해하지는 않다 - 스키마가 다른 진짜 다른 테이블의 컬럼이
        /// 섞일 수 있다. 그래도 거짓 "컬럼 없음"보다는 낫다.
        /// </summary>
        internal static bool KeyMatchesDependency(string key, DependencyInfo dep, SpDefinition spDef)
        {
            var depCanonicalName = StaticAnalysisNormalizer.CanonicalizeParts(
                dep.Database, dep.Schema, dep.Name, spDef.ObjectKey?.Database, spDef.Schema);

            var hasDbContext = !string.IsNullOrWhiteSpace(spDef.ObjectKey?.Database);

            var keyCanonicalName = StaticAnalysisNormalizer.Canonicalize(
                key, spDef.ObjectKey?.Database, spDef.Schema);

            return hasDbContext
                ? string.Equals(keyCanonicalName, depCanonicalName, StringComparison.OrdinalIgnoreCase)
                : string.Equals(
                    ExtractBaseName(keyCanonicalName), ExtractBaseName(dep.Name), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// canonical 이름(또는 원시 이름)에서 마지막 세그먼트만 뽑는다.
        /// DB 컨텍스트가 없어 3-part로 한정할 수 없을 때 폴백 비교 키로 쓴다.
        /// </summary>
        public static string ExtractBaseName(string? qualifiedOrRawName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedOrRawName)) return string.Empty;

            var trimmed = qualifiedOrRawName.Trim().Trim('[', ']');
            var lastDot = trimmed.LastIndexOf('.');
            return lastDot >= 0 ? trimmed[(lastDot + 1)..].Trim('[', ']') : trimmed;
        }
    }
}
```

- [ ] **Step 4: 테스트 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SchemaPromptColumnSelectorTests"
```

기대: 4개 통과.

- [ ] **Step 5: `AiService`가 선택기에 위임하게 한다**

`AiService.cs`에서 `FormatTableSchemaToMarkdown`의 50-144행(주석 블록 `// 엄격한 필터링 대상 컬럼 식별`부터 `keepCols.Count > 0 && !keepCols.Contains(...)` 검사까지)을 지우고 다음으로 바꾼다. 헤더·표 머리말(34-48행)과 그 아래 컬럼 행 렌더링(146-157행), 인덱스 표(159-172행)는 그대로 둔다.

```csharp
            // 프롬프트에 어떤 컬럼이 실리는지는 SchemaPromptColumnSelector가 단독으로
            // 결정한다. L1(SpecExpectations)이 같은 함수를 불러 대조 기준을 만들므로,
            // 여기서 판정을 복제하면 두 권위가 가장자리에서 어긋난다.
            var shownColumns = SchemaPromptColumnSelector.Select(dep, spDef);

            foreach (var col in dep.Columns)
            {
                if (!shownColumns.Contains(col.ColumnName))
                {
                    continue;
                }
```

그리고 `AiService`의 `private static string ExtractBaseName(...)` 메서드(181-188행)를 통째로 삭제한다 — 이 필터가 유일한 사용처였다.

- [ ] **Step 6: 전체 테스트가 여전히 초록인지 확인한다**

```bash
dotnet build --no-incremental && dotnet test
```

기대: 실패 0. 순수 추출이므로 회귀가 있으면 안 된다.

- [ ] **Step 7: 뮤테이션으로 하중을 확인한다**

`Select`의 폴백 조건 `keepCols.Count > 0 &&`를 지우고 테스트를 돌린다.

```bash
dotnet test --filter "FullyQualifiedName~SchemaPromptColumnSelectorTests"
```

기대: `Select_WhenNothingMatches_ShouldFallBackToAllColumns`가 깨진다(0개가 나온다). 깨지지 않으면 그 테스트가 하중을 지지 않는 것이니 고친다. 확인 후 원복한다.

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/SchemaPromptColumnSelector.cs \
        src/ReSet.Core/Services/AiService.cs \
        tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs
git commit -m "refactor: extract the prompt schema column filter into one authority"
```

---

## Task 2: 참조 컬럼 유실 검사 (A)

참조 컬럼 키가 어떤 의존성에도 병합되지 않아 컬럼이 통째로 사라지는 것을 잡는다. 원래 결함의 모양 그 자체다.

**Files:**
- Modify: `src/ReSet.Core/Services/SchemaPromptColumnSelector.cs`
- Modify: `tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs`

**Interfaces:**
- Consumes: Task 1의 `KeyMatchesDependency`, `ExtractBaseName`
- Produces: `public static IReadOnlyList<string> SchemaPromptColumnSelector.DetectOrphanedColumnKeys(SpDefinition spDef)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`SchemaPromptColumnSelectorTests.cs`의 클래스 안에 덧붙인다:

```csharp
        [Fact]
        public void DetectOrphanedColumnKeys_WhenCanonicalMismatchDropsColumns_ShouldReport()
        {
            // Arrange - 14개 명세서를 망가뜨린 결함의 재현. 의존성은 DB 한정
            // SETTLE_POQ_DB.dbo.TSettleMst인데 AST 키는 비한정 "TSettleMst"이고,
            // 분석 대상 SP는 다른 DB에 있다. 정식 비교가 어긋나 CYMD/INSTATE가
            // 프롬프트 어디에도 실리지 않는다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("CYMD", false), ("INSTATE", false));
            var sp = Sp("PaymentDB", new Dictionary<string, List<string>>
            {
                ["TSettleMst"] = new List<string> { "CYMD", "INSTATE" }
            });
            sp.Dependencies.Add(dep);

            // Act
            var defects = SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp);

            // Assert
            var defect = Assert.Single(defects);
            Assert.Contains("TSettleMst", defect);
            Assert.Contains("CYMD", defect);
        }

        [Fact]
        public void DetectOrphanedColumnKeys_WhenMatchingSucceeds_ShouldReportNothing()
        {
            // Arrange - 정상 경로. 정식 비교가 성립한다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("CYMD", false));
            var sp = Sp("SETTLE_POQ_DB", new Dictionary<string, List<string>>
            {
                ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "CYMD" }
            });
            sp.Dependencies.Add(dep);

            // Act & Assert
            Assert.Empty(SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp));
        }

        [Fact]
        public void DetectOrphanedColumnKeys_WithoutDbContext_ShouldReportNothing()
        {
            // Arrange - DB 컨텍스트가 없으면 실제 매칭이 이미 베이스 이름으로 내려가
            // 병합에 성공한다. 조건을 "정식 비교 실패"로 못 박으면 이 정상 동작이
            // 전부 위반으로 보고된다 - 그래서 조건은 "실제 매칭에서 병합되지 않음"이다.
            var dep = Dep("TSettleMst", null, ("CYMD", false));
            var sp = Sp(null, new Dictionary<string, List<string>>
            {
                ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "CYMD" }
            });
            sp.Dependencies.Add(dep);

            // Act & Assert
            Assert.Empty(SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp));
        }

        [Fact]
        public void DetectOrphanedColumnKeys_ForTempTable_ShouldReportNothing()
        {
            // Arrange - 임시 테이블은 애초에 의존성이 아니다. 정당하게 매칭되지 않는다.
            var dep = Dep("TSettleMst", "SETTLE_POQ_DB", ("CYMD", false));
            var sp = Sp("PaymentDB", new Dictionary<string, List<string>>
            {
                ["#TMP"] = new List<string> { "SEQ" }
            });
            sp.Dependencies.Add(dep);

            // Act & Assert
            Assert.Empty(SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp));
        }

        [Fact]
        public void DetectOrphanedColumnKeys_WhenDependencyHasNoColumns_ShouldReportNothing()
        {
            // Arrange - TPGProperty처럼 메타데이터 수집이 안 된 의존성은 스키마 표
            // 자체가 없다. 명세서가 "스키마 정의는 제공되지 않았습니다"라고 쓰는 것은
            // 참인 진술이고, 이것은 입력 결함이 아니다.
            var dep = Dep("TPGProperty", "SETTLE_POQ_DB");
            var sp = Sp("PaymentDB", new Dictionary<string, List<string>>
            {
                ["TPGProperty"] = new List<string> { "CommMethod" }
            });
            sp.Dependencies.Add(dep);

            // Act & Assert
            Assert.Empty(SchemaPromptColumnSelector.DetectOrphanedColumnKeys(sp));
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~DetectOrphanedColumnKeys"
```

기대: 컴파일 실패 — `DetectOrphanedColumnKeys`가 없다.

- [ ] **Step 3: 구현한다**

`SchemaPromptColumnSelector` 클래스 안, `Select` 바로 아래에 넣는다:

```csharp
        /// <summary>
        /// 참조 컬럼 키가 통째로 유실됐는지 본다.
        ///
        /// 명제: 키 K가 임시 테이블이 아니고, <b>실제 매칭에서 어떤 의존성에도 병합되지
        /// 않았는데</b>, 베이스 이름으로는 컬럼을 가진 의존성과 맞는다면 - K의 컬럼들은
        /// 프롬프트 어디에도 실리지 않았다.
        ///
        /// 첫째 조건이 "정식 비교 실패"가 아니라 "실제 매칭 실패"인 것이 중요하다.
        /// KeyMatchesDependency는 DB 컨텍스트가 없을 때 이미 베이스 이름 비교로
        /// 내려간다. 조건을 정식 비교로 못 박으면 그 폴백 경로의 정상 동작이 전부
        /// 위반으로 보고된다.
        ///
        /// 이 위반은 재생성으로 고칠 수 없다 - 프롬프트가 거짓말을 한 코드 버그이지
        /// AI의 잘못이 아니다. 그래서 호출부는 이것을 L1 오류가 아니라 경고로 다룬다.
        /// </summary>
        public static IReadOnlyList<string> DetectOrphanedColumnKeys(SpDefinition spDef)
        {
            var defects = new List<string>();
            var analysis = spDef.StaticAnalysis;
            if (analysis?.ReferencedColumnsPerTable == null) return defects;

            foreach (var kvp in analysis.ReferencedColumnsPerTable)
            {
                var key = kvp.Key;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (kvp.Value == null || kvp.Value.Count == 0) continue;

                var trimmed = key.TrimStart();
                if (trimmed.StartsWith("#", StringComparison.Ordinal)
                 || trimmed.StartsWith("@", StringComparison.Ordinal))
                {
                    continue;
                }

                if (spDef.Dependencies.Any(dep => KeyMatchesDependency(key, dep, spDef)))
                {
                    continue; // 어딘가에 병합됐다.
                }

                var baseName = ExtractBaseName(key);
                var lookalikes = spDef.Dependencies
                    .Where(dep => dep.Columns.Count > 0
                               && string.Equals(ExtractBaseName(dep.Name), baseName, StringComparison.OrdinalIgnoreCase))
                    .Select(dep => StaticAnalysisNormalizer.CanonicalizeParts(
                        dep.Database, dep.Schema, dep.Name, spDef.ObjectKey?.Database, spDef.Schema))
                    .ToList();

                if (lookalikes.Count == 0) continue;

                defects.Add(
                    $"[스키마 프롬프트] 참조 컬럼 키 `{key}`가 어떤 의존성에도 병합되지 않아 " +
                    $"컬럼 {kvp.Value.Count}개({string.Join(", ", kvp.Value)})가 프롬프트 스키마 표에서 누락되었습니다. " +
                    $"이름이 같은 의존성: {string.Join(", ", lookalikes)}. " +
                    "명세서가 해당 컬럼을 \"존재하지 않음\"으로 기술할 수 있습니다.");
            }

            return defects;
        }
```

- [ ] **Step 4: 테스트 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SchemaPromptColumnSelectorTests"
```

기대: 9개 통과.

- [ ] **Step 5: 뮤테이션으로 하중을 확인한다**

`if (lookalikes.Count == 0) continue;`를 지우고 돌린다.

기대: `DetectOrphanedColumnKeys_ForTempTable_ShouldReportNothing`은 임시 테이블 조기 반환에 이미 걸리므로 영향이 없고, `DetectOrphanedColumnKeys_WhenDependencyHasNoColumns_ShouldReportNothing`이 깨져야 한다. 깨지지 않으면 그 테스트를 고친다. 원복한다.

이어서 `dep.Columns.Count > 0 &&` 조건을 지우고 같은 테스트를 돌려 같은 것이 깨지는지 본다. 원복한다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/SchemaPromptColumnSelector.cs \
        tests/ReSet.Core.Tests/SchemaPromptColumnSelectorTests.cs
git commit -m "feat: detect referenced column keys dropped from the prompt schema"
```

---

## Task 3: `SpecExpectations`가 스키마 진실을 싣는다

L1이 대조할 기준(테이블별 프롬프트 컬럼 집합)과 A 위반 목록을 기대값에 싣고, 파이프라인에 배선한다.

**Files:**
- Modify: `src/ReSet.Core/Services/SpecExpectations.cs`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:224`
- Modify: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs:689,872`
- Create: `tests/ReSet.Core.Tests/SpecExpectationsTests.cs`

**Interfaces:**
- Consumes: `SchemaPromptColumnSelector.Select`, `SchemaPromptColumnSelector.DetectOrphanedColumnKeys`
- Produces:
  - `public sealed record SpecExpectations(IReadOnlyList<UpdateColumnExpectation> UpdateColumns, IReadOnlyDictionary<string, IReadOnlySet<string>> PromptSchemaColumns, IReadOnlyList<string> InputDefects)`
  - `public static SpecExpectations? SpecExpectations.From(SpDefinition? spDef)`
  - `FromStaticAnalysis`는 **삭제된다**

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SpecExpectationsTests.cs`:

```csharp
using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class SpecExpectationsTests
    {
        private static SpDefinition BuildSp()
        {
            var dep = new DependencyInfo
            {
                Name = "TSettleMst", Schema = "dbo", Database = "SETTLE_POQ_DB", Type = "USER_TABLE"
            };
            dep.Columns.Add(new ColumnInfo { ColumnName = "CLINTCOMM", DataType = "int" });
            dep.Columns.Add(new ColumnInfo { ColumnName = "CLETC", DataType = "int" });

            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey("SETTLE_POQ_DB", "dbo", "UP_PROBE", CodeObjectType.Procedure),
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ReferencedColumnsPerTable = new Dictionary<string, List<string>>(
                        System.StringComparer.OrdinalIgnoreCase)
                    {
                        ["SETTLE_POQ_DB.dbo.TSettleMst"] = new List<string> { "CLINTCOMM", "CLETC" }
                    }
                }
            };
            sp.Dependencies.Add(dep);
            return sp;
        }

        [Fact]
        public void From_ShouldExposePromptSchemaColumnsKeyedByCanonicalName()
        {
            // Act
            var expectations = SpecExpectations.From(BuildSp());

            // Assert
            Assert.NotNull(expectations);
            var columns = Assert.Contains("SETTLE_POQ_DB.dbo.TSettleMst", expectations!.PromptSchemaColumns);
            Assert.Contains("CLINTCOMM", columns);
            Assert.Contains("CLETC", columns);
        }

        [Fact]
        public void From_WhenDependencyHasNoColumns_ShouldNotCreateAnEntry()
        {
            // Arrange - 스키마 표가 아예 렌더링되지 않는 의존성은 대조 기준이 될 수 없다.
            // 여기에 빈 항목을 만들면 "제공되지 않았습니다"라는 참인 진술이
            // 대조 대상으로 잘못 올라간다.
            var sp = BuildSp();
            sp.Dependencies[0].Columns.Clear();

            // Act
            var expectations = SpecExpectations.From(sp);

            // Assert
            Assert.DoesNotContain("SETTLE_POQ_DB.dbo.TSettleMst", expectations?.PromptSchemaColumns ?? new Dictionary<string, IReadOnlySet<string>>());
        }

        [Fact]
        public void From_WithNullSpDefinition_ShouldReturnNull()
        {
            Assert.Null(SpecExpectations.From(null));
        }

        [Fact]
        public void From_ShouldCarryInputDefects()
        {
            // Arrange - 정식 비교가 어긋나 컬럼이 유실되는 구성.
            var sp = BuildSp();
            sp.StaticAnalysis.ReferencedColumnsPerTable.Clear();
            sp.StaticAnalysis.ReferencedColumnsPerTable["OtherDb.dbo.TSettleMst"] =
                new List<string> { "CLINTCOMM" };

            // Act
            var expectations = SpecExpectations.From(sp);

            // Assert
            Assert.NotNull(expectations);
            Assert.NotEmpty(expectations!.InputDefects);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~SpecExpectationsTests"
```

기대: 컴파일 실패 — `From`이 없다.

- [ ] **Step 3: `SpecExpectations`를 고친다**

`SpecExpectations.cs` 전체를 다음으로 바꾼다:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정적 분석과 스키마 메타데이터가 확정한 사실 중 L1이 명세서 본문과 기계적으로
    /// 대조할 것들.
    ///
    /// MechanicalValidator에 두지 않는 이유: 기대값 <b>생성</b>은 정적 분석과 의존성을
    /// 읽는 일이고 <b>소비</b>는 검증기의 일이다. 나눠 두면 검증기가 SpDefinition을
    /// 몰라도 된다.
    /// </summary>
    /// <param name="UpdateColumns">정적 파서가 확정한 UPDATE SET 대상 컬럼.</param>
    /// <param name="PromptSchemaColumns">
    /// 테이블별로 프롬프트 스키마 표에 실제로 실린 컬럼. 키는 canonical 3-part 이름이다.
    /// 이것이 거짓 부재 주장 대조의 기준이다 - DB 전체 컬럼이 아니다. 정당하게 필터에서
    /// 빠진 컬럼을 기준으로 삼으면 재생성으로 고칠 수 없는 오류가 생긴다.
    /// </param>
    /// <param name="InputDefects">
    /// 프롬프트가 진실을 담지 못한 경우의 서술. <b>L1 오류가 아니다</b> - 재생성이
    /// 고칠 수 없는 코드 버그이므로 호출부가 경고로 표면화한다.
    /// </param>
    public sealed record SpecExpectations(
        IReadOnlyList<UpdateColumnExpectation> UpdateColumns,
        IReadOnlyDictionary<string, IReadOnlySet<string>> PromptSchemaColumns,
        IReadOnlyList<string> InputDefects)
    {
        /// <summary>
        /// 대조할 것이 하나도 없으면 null을 돌려준다. 호출부가 null 검사를 하지 않고
        /// 그대로 넘길 수 있게 하기 위해서다 - Validate는 null을 "종전 동작"으로 받는다.
        /// </summary>
        public static SpecExpectations? From(SpDefinition? spDef)
        {
            if (spDef == null) return null;

            var updateColumns = BuildUpdateColumns(spDef.StaticAnalysis);

            var promptSchemaColumns = new Dictionary<string, IReadOnlySet<string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var dep in spDef.Dependencies)
            {
                // 컬럼이 없는 의존성은 스키마 표 자체가 렌더링되지 않는다
                // (BuildSpMetadataTexts의 dep.Columns.Count > 0 조건). 대조 기준으로
                // 삼으면 "스키마 정의는 제공되지 않았습니다"라는 참인 진술이 대조
                // 대상으로 잘못 올라간다.
                if (dep.Columns.Count == 0) continue;

                var canonical = StaticAnalysisNormalizer.CanonicalizeParts(
                    dep.Database, dep.Schema, dep.Name, spDef.ObjectKey?.Database, spDef.Schema);
                if (string.IsNullOrWhiteSpace(canonical)) continue;

                promptSchemaColumns[canonical] = SchemaPromptColumnSelector.Select(dep, spDef);
            }

            var inputDefects = SchemaPromptColumnSelector.DetectOrphanedColumnKeys(spDef);

            if (updateColumns.Count == 0 && promptSchemaColumns.Count == 0 && inputDefects.Count == 0)
            {
                return null;
            }

            return new SpecExpectations(updateColumns, promptSchemaColumns, inputDefects);
        }

        /// <summary>
        /// 테이블 단위로 접는다. 대조가 테이블 합집합이므로 기대도 같은 단위여야 한다.
        /// </summary>
        private static List<UpdateColumnExpectation> BuildUpdateColumns(SpStaticAnalysisResult? analysis)
        {
            if (analysis == null || analysis.AstUpdateMappings.Count == 0)
            {
                return new List<UpdateColumnExpectation>();
            }

            var byTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in analysis.AstUpdateMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.TargetTable)) continue;

                if (!byTable.TryGetValue(mapping.TargetTable, out var columns))
                {
                    columns = new List<string>();
                    byTable[mapping.TargetTable] = columns;
                }

                foreach (var assignment in mapping.Assignments)
                {
                    if (string.IsNullOrWhiteSpace(assignment.Column)) continue;
                    if (columns.Contains(assignment.Column, StringComparer.OrdinalIgnoreCase)) continue;
                    columns.Add(assignment.Column);
                }
            }

            return byTable
                .Where(kvp => kvp.Value.Count > 0)
                .Select(kvp => new UpdateColumnExpectation(kvp.Key, kvp.Value))
                .ToList();
        }
    }

    /// <summary>한 테이블에 대해 명세서의 UPDATE 매핑 표에 반드시 있어야 하는 컬럼들.</summary>
    public sealed record UpdateColumnExpectation(string Table, IReadOnlyList<string> Columns);
}
```

- [ ] **Step 4: 기존 호출부를 갱신한다**

`MechanicalValidatorTests.cs` 689행과 872행:

```csharp
            return SpecExpectations.FromStaticAnalysis(analysis)!;
```

를 다음으로 바꾼다. 두 헬퍼는 `SpStaticAnalysisResult`만 갖고 있으므로 `SpDefinition`으로 감싼다.

```csharp
            return SpecExpectations.From(new SpDefinition { StaticAnalysis = analysis })!;
```

`VerificationPipelineOrchestrator.cs` 224행을 다음으로 바꾼다.

```csharp
            var specExpectations = SpecExpectations.From(spDef);

            // A 위반(프롬프트가 진실을 담지 못함)은 재생성으로 고칠 수 없는 코드 버그다.
            // L1 오류로 만들면 무한 재시도가 된다. 아래 NotifyWarnings가 이미 사용자에게
            // 보여 주는 채널이므로 새 채널을 만들지 않는다.
            if (specExpectations != null && specExpectations.InputDefects.Count > 0)
            {
                foreach (var defect in specExpectations.InputDefects)
                {
                    Log.Warning("[파이프라인] {Defect}", defect);
                    if (!spDef.Warnings.Contains(defect))
                    {
                        spDef.Warnings.Add(defect);
                    }
                }
            }
```

`SpecExpectationsWiringPolicyScanner.cs` 22행의 주석이 `FromStaticAnalysis`를 언급한다. `From`으로 고친다(주석만, 로직은 그대로).

- [ ] **Step 5: 배선 테스트를 더한다**

`SpecExpectationsTests.cs`에 덧붙인다:

```csharp
        [Fact]
        public void InputDefects_ShouldNotBecomeValidationErrors()
        {
            // Arrange - A 위반이 있는 기대값으로 정상 명세서를 검증한다.
            var sp = BuildSp();
            sp.StaticAnalysis.ReferencedColumnsPerTable.Clear();
            sp.StaticAnalysis.ReferencedColumnsPerTable["OtherDb.dbo.TSettleMst"] =
                new List<string> { "CLINTCOMM" };
            var expectations = SpecExpectations.From(sp);
            Assert.NotEmpty(expectations!.InputDefects);

            var markdown = string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용", "## CRUD 분석", "내용",
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert - 입력 결함은 재생성 루프에 들어가면 안 된다.
            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }
```

- [ ] **Step 6: 전체 테스트를 돌린다**

```bash
dotnet build --no-incremental && dotnet test
```

기대: 실패 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/SpecExpectations.cs \
        src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs \
        tests/ReSet.Core.Tests/SpecExpectationsTests.cs \
        tests/ReSet.Core.Tests/MechanicalValidatorTests.cs \
        tests/ReSet.Core.Tests/SpecExpectationsWiringPolicyScanner.cs
git commit -m "feat: carry prompt schema columns and input defects in SpecExpectations"
```

---

## Task 4: 거짓 부재 주장 검사 (B)

명세서가 프롬프트에 실린 컬럼을 "없다"고 단정하는 것을 잡는다.

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Modify: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: `SpecExpectations.PromptSchemaColumns`, 기존 `MarkdownSectionLocator.SplitLines`, 기존 `NormalizeQualifiedName`, 기존 `LastNamePart`
- Produces: `ErrorType.SchemaClaimFalse`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`MechanicalValidatorTests.cs`의 클래스 안에 덧붙인다. 헬퍼부터 만든다:

```csharp
        private static SpecExpectations SchemaExpectations(
            string canonicalTable, params string[] columns)
        {
            var dep = new DependencyInfo
            {
                Name = canonicalTable.Split('.')[^1],
                Schema = "dbo",
                Database = canonicalTable.Split('.').Length >= 3 ? canonicalTable.Split('.')[0] : null,
                Type = "USER_TABLE"
            };
            foreach (var column in columns)
            {
                dep.Columns.Add(new ColumnInfo { ColumnName = column, DataType = "int" });
            }

            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey(
                    canonicalTable.Split('.').Length >= 3 ? canonicalTable.Split('.')[0] : "DB",
                    "dbo", "UP_PROBE", CodeObjectType.Procedure)
            };
            sp.Dependencies.Add(dep);
            return SpecExpectations.From(sp)!;
        }

        private static string WrapSpec(string crudBody)
        {
            return string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용",
                "## CRUD 분석", crudBody,
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });
        }

        [Fact]
        public void Validate_WhenSpecClaimsAnExistingColumnIsAbsent_ShouldFail()
        {
            // Arrange - 14개 명세서를 통과시킨 결함의 모양.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM", "CLETC");
            var markdown = WrapSpec(
                "### 스키마 불일치 컬럼\n\n" +
                "| 테이블명 | 컬럼명 | 판정 | 용도 |\n" +
                "|---|---|---|---|\n" +
                "| `dbo.TSettleMst` | `CLINTCOMM` | 존재하지 않음 | 할부이자 고객사 수수료 |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
            Assert.Contains(result.Errors, e => e.Contains("CLINTCOMM"));
        }

        [Fact]
        public void Validate_WhenTheAbsenceClaimIsTrue_ShouldPass()
        {
            // Arrange - 그 테이블에 없는 컬럼을 없다고 하는 것은 참인 진술이다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec(
                "`dbo.TSettleMst`의 `NotAColumn`은 제공된 스키마에 없는 열이므로 스키마 불일치입니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenNoTableCanBeAttributed_ShouldPass()
        {
            // Arrange - 테이블을 특정할 수 없으면 침묵한다. 잘못 지목한 오류는
            // 재생성으로 고칠 수 없다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec("프로시저에는 `INSERT` 문이 없습니다. `CLINTCOMM`은 존재하지 않습니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WithoutAnAbsenceExpression_ShouldPass()
        {
            // Arrange - 부재 표현이 없으면 컬럼과 테이블이 같이 나와도 오류가 아니다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec("| `dbo.TSettleMst` | `CLINTCOMM` | 할부이자 고객사 수수료를 갱신합니다. |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenTheSameClaimRepeats_ShouldReportItOnce()
        {
            // Arrange - 같은 (테이블, 컬럼) 주장이 여러 줄에 나와도 재생성 지시는 하나면 된다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "CLINTCOMM");
            var markdown = WrapSpec(
                "`dbo.TSettleMst`의 `CLINTCOMM`은 존재하지 않습니다.\n" +
                "다시 말해 `dbo.TSettleMst`의 `CLINTCOMM`은 스키마 불일치입니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }

        [Fact]
        public void Validate_WhenTheLastNamePartIsAmbiguous_ShouldStaySilent()
        {
            // Arrange - 마지막 파트가 같은 테이블이 둘이면 귀속이 불가능하다.
            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey("DB1", "dbo", "UP_PROBE", CodeObjectType.Procedure)
            };
            foreach (var db in new[] { "DB1", "DB2" })
            {
                var dep = new DependencyInfo { Name = "TCommMst", Schema = "dbo", Database = db, Type = "USER_TABLE" };
                dep.Columns.Add(new ColumnInfo { ColumnName = "AMT", DataType = "int" });
                sp.Dependencies.Add(dep);
            }
            var expectations = SpecExpectations.From(sp)!;
            var markdown = WrapSpec("`dbo.TCommMst`의 `AMT`는 존재하지 않습니다.");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests"
```

기대: 컴파일 실패 — `ErrorType.SchemaClaimFalse`가 없다.

- [ ] **Step 3: 구현한다**

`MechanicalValidator.cs`의 `ErrorType`에 항목을 더한다(`General` 앞에):

```csharp
    public enum ErrorType
    {
        HeaderMissing,
        MermaidQuoteMissing,
        MermaidCliError,
        UpdateMappingMissing,
        SchemaClaimFalse,
        General
    }
```

`Validate`의 `try` 블록 안, `CheckUpdateMappings` 호출 바로 아래에 더한다:

```csharp
                if (expectations != null)
                {
                    CheckUpdateMappings(cleansed, expectations, result);
                    CheckSchemaClaims(cleansed, expectations, result);
                }
```

`CheckUpdateMappings` 아래에 다음을 더한다:

```csharp
        /// <summary>
        /// 명세서가 프롬프트에 실린 컬럼을 "없다"고 단정하는 것을 잡는다.
        ///
        /// 목록은 지어낸 것이 아니라 실제 14개 명세서에서 관찰된 형태다. 어미 변화를
        /// 한 항목이 덮도록 어간에서 끊었다. 맨 "없습니다"는 넣지 않는다 - 명세서
        /// 전체에 일상적으로 쓰이는 말이라 표면이 너무 넓다.
        ///
        /// 목록이 완전하지 않다는 것은 인정된 한계다. 새 표현이 나타나면 그 명세서가
        /// 통과한다. 대신 목록에 없는 표현이 오탐을 만들지는 않는다 - 실패 방향이
        /// 안전한 쪽이다.
        /// </summary>
        private static readonly string[] AbsenceClaimTokens =
        {
            "스키마 불일치",
            "존재하지 않",
            "정의되어 있지 않",
            "스키마에 없",
            "스키마가 없"
        };

        private static readonly Regex BacktickIdentifierRegex =
            new Regex(@"`([^`\r\n]+)`", RegexOptions.Compiled);

        /// <summary>
        /// 한 줄이 오류가 되려면 셋이 동시에 성립해야 한다.
        ///   1. 줄에 부재 표현이 있다
        ///   2. 줄의 백틱 식별자 중 하나가 의존성 테이블로 해석된다
        ///   3. 줄의 다른 백틱 식별자 중 하나가 그 테이블의 프롬프트 컬럼 집합에 있다
        ///
        /// 셋째 조건이 오탐을 막는 핵심이다. "`INSERT` 문이 없습니다"는 INSERT가 어느
        /// 테이블의 컬럼도 아니라 통과하고, "`TExchangeRateMst`의 스키마 정의는
        /// 제공되지 않았습니다"는 그 의존성에 컬럼이 0개라 애초에 대조 대상에 없다
        /// (SpecExpectations.From이 제외한다) - 그리고 그것은 참인 주장이므로 통과가 옳다.
        ///
        /// 둘째 조건은 귀속이 불가능할 때 침묵하게 만든다. 잘못 지목한 오류는 재생성으로
        /// 고칠 수 없고, 그것이 이 저장소가 직전 브랜치에서 무한 재시도로 겪은 실패다.
        /// </summary>
        private static void CheckSchemaClaims(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.PromptSchemaColumns.Count == 0) return;

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in MarkdownSectionLocator.SplitLines(markdown))
            {
                if (!Array.Exists(AbsenceClaimTokens, t => line.Contains(t, StringComparison.Ordinal)))
                {
                    continue;
                }

                var identifiers = new List<string>();
                foreach (Match match in BacktickIdentifierRegex.Matches(line))
                {
                    var identifier = match.Groups[1].Value.Trim();
                    if (identifier.Length > 0) identifiers.Add(identifier);
                }

                if (identifiers.Count < 2) continue;

                foreach (var identifier in identifiers)
                {
                    var tableKey = ResolveSchemaTableKey(identifier, expectations);
                    if (tableKey == null) continue;

                    var columns = expectations.PromptSchemaColumns[tableKey];

                    foreach (var candidate in identifiers)
                    {
                        if (ReferenceEquals(candidate, identifier)) continue;
                        if (ResolveSchemaTableKey(candidate, expectations) != null) continue;
                        if (!columns.Contains(candidate)) continue;

                        if (!reported.Add($"{tableKey}|{candidate}")) continue;

                        var message =
                            $"명세서가 `{tableKey}`의 컬럼 `{candidate}`을(를) 존재하지 않는 것으로 기술했습니다. " +
                            "이 컬럼은 프롬프트의 스키마 표에 실제로 제공되었습니다.";
                        result.Errors.Add(message);
                        result.DetailedErrors.Add(new DetailedError
                        {
                            Type = ErrorType.SchemaClaimFalse,
                            Message = message,
                            RawContext = line.Trim()
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 문서에 적힌 이름 하나를 PromptSchemaColumns의 키로 해석한다.
        ///
        /// 평소엔 관대하게, 충돌할 때만 침묵: 완전 한정 이름이 맞으면 그것을 쓰고,
        /// 아니면 마지막 파트로 찾되 후보가 정확히 하나일 때만 인정한다. 둘 이상이면
        /// null을 돌려 검사를 건너뛴다 - 오류로 만들지 않는다.
        /// </summary>
        private static string? ResolveSchemaTableKey(string writtenName, SpecExpectations expectations)
        {
            var normalized = NormalizeQualifiedName(writtenName);
            if (normalized.Length == 0) return null;

            if (expectations.PromptSchemaColumns.ContainsKey(normalized)) return normalized;

            var lastPart = LastNamePart(writtenName);
            if (lastPart.Length == 0) return null;

            string? single = null;
            foreach (var key in expectations.PromptSchemaColumns.Keys)
            {
                if (!string.Equals(LastNamePart(key), lastPart, StringComparison.OrdinalIgnoreCase)) continue;
                if (single != null) return null; // 모호하다.
                single = key;
            }

            return single;
        }
```

`BuildSuggestedPromptFix`의 "5. 기타 에러" 블록 **앞**에 더한다:

```csharp
            // 5. 거짓 스키마 부재 주장
            var schemaClaimErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.SchemaClaimFalse);
            if (schemaClaimErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 5. 실존 컬럼을 존재하지 않는다고 기술한 오류");
                sb.AppendLine("아래 컬럼은 프롬프트의 `[Referenced Table Schemas]` 표에 실제로 제공되었습니다. 존재하지 않는다거나 스키마 불일치라고 기술하지 마십시오. 해당 문장과 표 행을 삭제하고, 그 컬럼을 정상적인 참조/갱신 컬럼으로 기술하십시오.");
                foreach (var err in schemaClaimErrors)
                {
                    sb.AppendLine($"  - {err.Message}");
                }
                sb.AppendLine();
            }
```

기존 "5. 기타 에러" 주석 번호를 "6. 기타 에러"로, 헤딩 문자열을 `### 🚨 6. 기타 정적 규격 검사 에러`로 바꾼다.

- [ ] **Step 4: 테스트 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests"
```

기대: 전부 통과.

- [ ] **Step 5: 뮤테이션으로 하중을 확인한다**

세 가드를 하나씩 지우고 각각 무엇이 깨지는지 본다. 매번 원복한다.

1. `if (ResolveSchemaTableKey(candidate, expectations) != null) continue;` 삭제
   → 테이블명이 컬럼으로도 해석되는 경우가 새 오류를 만드는지 확인. 아무것도 깨지지 않으면 그 사실을 기록하고 넘어간다(현재 픽스처에 테이블명과 같은 컬럼명이 없다).
2. `if (!reported.Add($"{tableKey}|{candidate}")) continue;` 삭제
   → `Validate_WhenTheSameClaimRepeats_ShouldReportItOnce`가 깨져야 한다.
3. `ResolveSchemaTableKey`의 `if (single != null) return null;` 삭제
   → `Validate_WhenTheLastNamePartIsAmbiguous_ShouldStaySilent`가 깨져야 한다.

- [ ] **Step 6: 전체 테스트를 돌린다**

```bash
dotnet build --no-incremental && dotnet test
```

기대: 실패 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: fail L1 when the spec denies a column the prompt provided"
```

---

## Task 5: 테이블 동일성 분열 검사 (②)

같은 물리 테이블이 CRUD 표 한 절 안에서 서로 다른 표기로 여러 행이 되는 것을 잡는다.

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs`
- Modify: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`

**Interfaces:**
- Consumes: Task 4의 `ResolveSchemaTableKey`, 기존 `LocateCrudSection`, 기존 `LastNamePart`
- Produces: `ErrorType.TableIdentitySplit`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        [Fact]
        public void Validate_WhenOneTableIsSplitAcrossSpellings_ShouldFail()
        {
            // Arrange - EXCEPTION_PROC에서 실측된 결함. 한 표 안에 세 표기가 공존한다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "PLTID");
            var markdown = WrapSpec(
                "### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n" +
                "|---|---|\n" +
                "| `DB.dbo.TSettleMst` | `PLTID` |\n" +
                "| `dbo.TSettleMst` | `PLTID` |\n" +
                "| `TSettleMst` | `PLTID` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.False(result.IsValid);
            var error = Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
            Assert.Contains("dbo.TSettleMst", error.Message);
            Assert.Contains("TSettleMst", error.Message);
        }

        [Fact]
        public void Validate_WhenTheSameSpellingRepeats_ShouldPass()
        {
            // Arrange - 같은 문자열이 반복되는 것은 이 결함이 아니다. 문장별로 나눠
            // 적었을 수 있고, UPDATE 매핑 헤딩이 정확히 그렇게 한다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "PLTID");
            var markdown = WrapSpec(
                "### 갱신 대상 테이블\n\n" +
                "| 테이블명 | 갱신 컬럼 |\n" +
                "|---|---|\n" +
                "| `dbo.TSettleMst` | `PLTID` |\n" +
                "| `dbo.TSettleMst` | `PLTID` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }

        [Fact]
        public void Validate_WhenTwoRealTablesShareALastNamePart_ShouldPass()
        {
            // Arrange - DB1.dbo.TCommMst와 DB2.dbo.TCommMst는 서로 다른 물리 테이블이다.
            // 마지막 파트가 같다는 이유로 합치면 정상 명세서를 떨어뜨린다.
            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_PROBE",
                ObjectKey = new CodeObjectKey("DB1", "dbo", "UP_PROBE", CodeObjectType.Procedure)
            };
            foreach (var db in new[] { "DB1", "DB2" })
            {
                var dep = new DependencyInfo { Name = "TCommMst", Schema = "dbo", Database = db, Type = "USER_TABLE" };
                dep.Columns.Add(new ColumnInfo { ColumnName = "AMT", DataType = "int" });
                sp.Dependencies.Add(dep);
            }
            var expectations = SpecExpectations.From(sp)!;
            var markdown = WrapSpec(
                "### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n" +
                "|---|---|\n" +
                "| `DB1.dbo.TCommMst` | `AMT` |\n" +
                "| `DB2.dbo.TCommMst` | `AMT` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }

        [Fact]
        public void Validate_WhenSpellingsAreInDifferentSubsections_ShouldPass()
        {
            // Arrange - 조회 절과 갱신 절에 각각 나오는 것은 정상이다.
            // 같은 테이블이 읽히고 갱신되는 것은 흔하다.
            var expectations = SchemaExpectations("DB.dbo.TSettleMst", "PLTID");
            var markdown = WrapSpec(
                "### 조회 대상 테이블\n\n" +
                "| 테이블명 | 참조 컬럼 |\n|---|---|\n| `DB.dbo.TSettleMst` | `PLTID` |\n\n" +
                "### 갱신 대상 테이블\n\n" +
                "| 테이블명 | 갱신 컬럼 |\n|---|---|\n| `dbo.TSettleMst` | `PLTID` |");

            // Act
            var result = new MechanicalValidator().Validate(markdown, expectations);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests"
```

기대: 컴파일 실패 — `ErrorType.TableIdentitySplit`이 없다.

- [ ] **Step 3: 구현한다**

`ErrorType`에 `SchemaClaimFalse` 바로 뒤로 더한다:

```csharp
        SchemaClaimFalse,
        TableIdentitySplit,
```

`Validate`의 `try` 블록에서 호출을 더한다:

```csharp
                if (expectations != null)
                {
                    CheckUpdateMappings(cleansed, expectations, result);
                    CheckSchemaClaims(cleansed, expectations, result);
                    CheckTableIdentitySplit(cleansed, expectations, result);
                }
```

`CheckSchemaClaims` 아래에 더한다:

```csharp
        private static readonly Regex TableCellRegex =
            new Regex(@"^\s*\|\s*`([^`\r\n]+)`\s*\|", RegexOptions.Compiled);

        /// <summary>
        /// 같은 물리 테이블이 CRUD 표 한 절 안에서 서로 다른 표기로 여러 행이 되는 것을
        /// 잡는다. EXCEPTION_PROC에서 SETTLE_POQ_DB.dbo.TSettleMst / dbo.TSettleMst /
        /// TSettleMst 세 표기가 한 표에 공존한 것이 실측된 결함이다.
        ///
        /// "서로 다른 표기"라는 단서가 중요하다. 같은 문자열이 두 번 나오는 것은 이 결함이
        /// 아니다 - 문장별로 나눠 적었을 수 있고, UPDATE 매핑 헤딩이 정확히 그렇게 한다.
        ///
        /// 절 경계를 넘지 않는다. 같은 테이블이 조회 절과 갱신 절에 각각 나오는 것은
        /// 정상이다.
        ///
        /// 귀속은 ResolveSchemaTableKey에 맡긴다. 마지막 파트가 같은 실제 테이블이
        /// 둘이면 그 함수가 null을 돌려주므로, DB1.dbo.TCommMst와 DB2.dbo.TCommMst가
        /// 합쳐지는 오탐이 생기지 않는다.
        /// </summary>
        private static void CheckTableIdentitySplit(
            string markdown, SpecExpectations expectations, ValidationResult result)
        {
            if (expectations.PromptSchemaColumns.Count == 0) return;

            var lines = MarkdownSectionLocator.SplitLines(markdown);
            var (crudStart, crudEnd) = LocateCrudSection(lines);
            if (crudStart < 0) return;

            var spellingsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            void Flush()
            {
                foreach (var kvp in spellingsByTable)
                {
                    if (kvp.Value.Count < 2) continue;

                    var message =
                        $"같은 물리 테이블 `{kvp.Key}`이(가) `## CRUD 분석`의 한 절 안에서 " +
                        $"서로 다른 표기 {kvp.Value.Count}개로 나뉘어 기술되었습니다: " +
                        string.Join(", ", kvp.Value.Select(s => $"`{s}`")) + ".";
                    result.Errors.Add(message);
                    result.DetailedErrors.Add(new DetailedError
                    {
                        Type = ErrorType.TableIdentitySplit,
                        Message = message
                    });
                }
                spellingsByTable.Clear();
            }

            for (var index = crudStart + 1; index < crudEnd; index++)
            {
                var trimmed = lines[index].TrimStart();

                if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                {
                    Flush();
                    continue;
                }

                var match = TableCellRegex.Match(lines[index]);
                if (!match.Success) continue;

                var written = match.Groups[1].Value.Trim();
                var key = ResolveSchemaTableKey(written, expectations);
                if (key == null) continue;

                if (!spellingsByTable.TryGetValue(key, out var spellings))
                {
                    spellings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    spellingsByTable[key] = spellings;
                }
                spellings.Add(NormalizeQualifiedName(written));
            }

            Flush();
        }
```

`BuildSuggestedPromptFix`의 "기타 에러" 블록 앞(스키마 주장 블록 뒤)에 더한다:

```csharp
            // 6. 테이블 동일성 분열
            var splitErrors = DetailedErrors.FindAll(e => e.Type == ErrorType.TableIdentitySplit);
            if (splitErrors.Count > 0)
            {
                sb.AppendLine("### 🚨 6. 같은 테이블을 여러 표기로 나눠 기술한 오류");
                sb.AppendLine("아래 표기들은 모두 같은 하나의 물리 테이블입니다. CRUD 분석의 각 절에서 이들을 한 행으로 합치고, 프롬프트가 제공한 완전 한정 이름(DB.스키마.테이블) 하나만 사용하십시오.");
                foreach (var err in splitErrors)
                {
                    sb.AppendLine($"  - {err.Message}");
                }
                sb.AppendLine();
            }
```

기타 에러 블록의 번호를 "7"로 올리고 헤딩을 `### 🚨 7. 기타 정적 규격 검사 에러`로 바꾼다.

- [ ] **Step 4: 테스트 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~MechanicalValidatorTests"
```

기대: 전부 통과.

- [ ] **Step 5: 뮤테이션으로 하중을 확인한다**

각각 지우고 원복한다.

1. `if (trimmed.StartsWith("### ", ...)) { Flush(); continue; }`의 `Flush()` 호출 삭제
   → `Validate_WhenSpellingsAreInDifferentSubsections_ShouldPass`가 깨져야 한다.
2. `if (kvp.Value.Count < 2) continue;`를 `< 1`로 변경
   → `Validate_WhenTheSameSpellingRepeats_ShouldPass`가 깨져야 한다.
3. `spellings`를 `HashSet`에서 `List`로 바꿔 중복을 허용
   → `Validate_WhenTheSameSpellingRepeats_ShouldPass`가 깨져야 한다.

- [ ] **Step 6: 전체 테스트를 돌린다**

```bash
dotnet build --no-incremental && dotnet test
```

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs tests/ReSet.Core.Tests/MechanicalValidatorTests.cs
git commit -m "feat: fail L1 when one physical table is split across spellings"
```

---

## Task 6: 실물 명세서 픽스처 수용 테스트

이 브랜치가 성공했다는 증거를 만든다. **88~94점으로 통과했던 그 명세서가 L1에서 떨어져야 한다.**

**Files:**
- Create: `tests/ReSet.Core.Tests/Fixtures/SettleCommUpdSchemaMismatchExcerpt.md`
- Create: `tests/ReSet.Core.Tests/Fixtures/ExceptionProcCrudExcerpt.md`
- Create: `tests/ReSet.Core.Tests/SchemaClaimGateRegressionTests.cs`

**Interfaces:**
- Consumes: `RepoPaths.FindRepoRoot()`(`StepErrorCodeRegressionTests.cs:21` 참고), Task 3~5의 전부

- [ ] **Step 1: 픽스처를 실물에서 발췌한다**

개발 기계의 `output/`에서 **한 글자도 고치지 않고** 복사한다. `output/`은 `.gitignore` 대상이라 CI에 없으므로 반드시 커밋되는 위치로 옮겨야 한다.

```bash
mkdir -p tests/ReSet.Core.Tests/Fixtures

# COMM_UPD: `### 스키마 불일치 컬럼` 표 (217-235행)
sed -n '217,235p' output/Procedures/dbo.UP_UTIL_SETTLE_COMM_UPD/docs/Spec.md \
  > tests/ReSet.Core.Tests/Fixtures/SettleCommUpdSchemaMismatchExcerpt.md

# EXCEPTION_PROC: 조회 대상 테이블 표 (46-57행)
sed -n '46,57p' output/Procedures/dbo.UP_UTIL_SETTLE_EXCEPTION_PROC/docs/Spec.md \
  > tests/ReSet.Core.Tests/Fixtures/ExceptionProcCrudExcerpt.md
```

발췌 후 두 파일을 눈으로 확인한다. `SettleCommUpdSchemaMismatchExcerpt.md`는 `### 스키마 불일치 컬럼` 헤딩과 `존재하지 않음` 15행을 담아야 하고, `ExceptionProcCrudExcerpt.md`는 `SETTLE_POQ_DB.dbo.TSettleMst` · `dbo.TSettleMst` · `TSettleMst` 세 표기를 모두 담아야 한다. 행 번호가 어긋나면 범위를 조정해 다시 뽑는다.

- [ ] **Step 2: 수용 테스트를 쓴다**

`tests/ReSet.Core.Tests/SchemaClaimGateRegressionTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 88~94점으로 검증을 통과했던 실제 명세서가 이 게이트에서 떨어지는지 본다.
    ///
    /// 픽스처는 output/ 아래 실물에서 한 글자도 고치지 않고 발췌한 것이다. output/은
    /// .gitignore 대상이라 CI에 없으므로 여기로 옮겨 커밋했다. 문장을 우리가 다시 쓰지
    /// 않는 것이 요점이다 - 게이트가 잡아야 할 것은 우리가 상상한 형태가 아니라 AI가
    /// 실제로 쓴 형태다.
    /// </summary>
    public class SchemaClaimGateRegressionTests
    {
        private static string LoadFixture(string name) =>
            File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", name));

        private static string WrapAsSpec(string crudBody) =>
            string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용",
                "## CRUD 분석", crudBody,
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });

        /// <summary>
        /// TSettleMst는 하나의 물리 테이블이고 59개 컬럼을 갖는다(원본 DDL 대조 결과).
        /// 명세서가 "존재하지 않음"으로 적은 15개는 전부 실재한다. 라이브 DB 없이
        /// 재현할 수 있도록 대조에 필요한 것만 손으로 구성한다.
        /// </summary>
        private static SpecExpectations BuildSettleMstTruth()
        {
            var dep = new DependencyInfo
            {
                Name = "TSettleMst", Schema = "dbo", Database = "SETTLE_POQ_DB", Type = "USER_TABLE"
            };

            var realColumns = new[]
            {
                "CLINTCOMM", "CLETC", "PGINTEXPCOMM", "PGINTREALCOMM", "PGETC",
                "PointAmt", "CardAmt", "CouponAmt", "MoneyAmt", "PGTOTAL",
                "POQINCOME", "SettleCurrency", "ForeignSettleAmt", "CLCOMMTYPE", "PGCOMMTYPE",
                "PLTID", "YMD", "TxAmt", "CLCOMM", "CLVT", "PGCOMM", "PGVT"
            };
            foreach (var column in realColumns)
            {
                dep.Columns.Add(new ColumnInfo { ColumnName = column, DataType = "int" });
            }

            var sp = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_UTIL_SETTLE_COMM_UPD",
                ObjectKey = new CodeObjectKey(
                    "SETTLE_POQ_DB", "dbo", "UP_UTIL_SETTLE_COMM_UPD", CodeObjectType.Procedure)
            };
            sp.Dependencies.Add(dep);
            return SpecExpectations.From(sp)!;
        }

        [Fact]
        public void TheSpecThatScoredNinetyOne_ShouldNowFailL1()
        {
            // Arrange
            var markdown = WrapAsSpec(LoadFixture("SettleCommUpdSchemaMismatchExcerpt.md"));

            // Act
            var result = new MechanicalValidator().Validate(markdown, BuildSettleMstTruth());

            // Assert - 이 단언이 이 브랜치의 존재 이유다.
            Assert.False(result.IsValid);

            var claims = result.DetailedErrors.Where(e => e.Type == ErrorType.SchemaClaimFalse).ToList();
            Assert.Equal(15, claims.Count);
            Assert.Contains(claims, e => e.Message.Contains("CLINTCOMM"));
            Assert.Contains(claims, e => e.Message.Contains("PGCOMMTYPE"));
        }

        [Fact]
        public void TheFailedSpec_ShouldProduceARegenerationInstruction()
        {
            // Arrange
            var markdown = WrapAsSpec(LoadFixture("SettleCommUpdSchemaMismatchExcerpt.md"));

            // Act
            var result = new MechanicalValidator().Validate(markdown, BuildSettleMstTruth());

            // Assert - 재생성이 무엇을 고쳐야 하는지 알 수 있어야 한다.
            Assert.NotNull(result.SuggestedPromptFix);
            Assert.Contains("CLINTCOMM", result.SuggestedPromptFix!);
            Assert.Contains("스키마 표에 실제로 제공", result.SuggestedPromptFix!);
        }

        [Fact]
        public void TheExceptionProcSpec_ShouldFailForSplittingOneTableAcrossSpellings()
        {
            // Arrange
            var markdown = WrapAsSpec("### 조회 대상 테이블\n\n" + LoadFixture("ExceptionProcCrudExcerpt.md"));

            // Act
            var result = new MechanicalValidator().Validate(markdown, BuildSettleMstTruth());

            // Assert
            var split = Assert.Single(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
            Assert.Contains("TSettleMst", split.Message);
        }

        [Fact]
        public void TrueAbsenceStatements_ShouldNotBeFlagged()
        {
            // Arrange - 스키마가 수집되지 않은 테이블에 대한 진술은 참이다.
            // 컬럼이 0개인 의존성은 애초에 대조 대상이 아니므로 걸리면 안 된다.
            var truth = BuildSettleMstTruth();
            var markdown = WrapAsSpec(string.Join("\n", new[]
            {
                "`TExchangeRateMst`, `TBasicCurrencyMst`의 스키마 정의는 제공되지 않았습니다.",
                "| 대상 없음 | 해당 없음 | 프로시저에는 `INSERT` 문이 없습니다. |"
            }));

            // Act
            var result = new MechanicalValidator().Validate(markdown, truth);

            // Assert
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.SchemaClaimFalse);
            Assert.DoesNotContain(result.DetailedErrors, e => e.Type == ErrorType.TableIdentitySplit);
        }
    }
}
```

- [ ] **Step 3: 테스트를 돌린다**

```bash
dotnet test --filter "FullyQualifiedName~SchemaClaimGateRegressionTests"
```

기대: 4개 통과. `Assert.Equal(15, claims.Count)`가 다른 수로 실패하면 픽스처의 실제 행 수를 세어 기대값을 맞추되, **테스트를 통과시키려고 픽스처를 고치지 않는다.** 픽스처는 실물이다.

- [ ] **Step 4: 전체 테스트를 돌린다**

```bash
dotnet build --no-incremental && dotnet test
```

- [ ] **Step 5: 커밋**

```bash
git add tests/ReSet.Core.Tests/Fixtures/SettleCommUpdSchemaMismatchExcerpt.md \
        tests/ReSet.Core.Tests/Fixtures/ExceptionProcCrudExcerpt.md \
        tests/ReSet.Core.Tests/SchemaClaimGateRegressionTests.cs
git commit -m "test: prove the gate fails the specs that scored 88-94"
```

---

## Task 7: 프롬프트 지시와 문서 갱신

A가 보장하는 사실을 AI에게 알려 주고, 게이트의 존재를 문서에 남긴다.

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs`
- Modify: `docs/architecture.md`
- Modify: `AGENTS.md`
- Modify: `tests/ReSet.Core.Tests/AiServiceTests_Rich.cs`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`AiServiceTests_Rich.cs`에 덧붙인다. **한글 리터럴을 원시 JSON 본문에 대고 단언하지 않는다** — `System.Text.Json`이 비ASCII를 `\uXXXX`로 이스케이프하므로 그런 단언은 결코 맞지 않는다. 이 파일에 이미 있는 `DecodeMessageContents`(224행)를 쓴다.

`ProbeSpDef`(193행)는 의존성이 없어 스키마 표가 아예 렌더링되지 않는다(`BuildSpMetadataTexts`는 `dep.Columns.Count > 0`일 때만 표를 찍는다). 그래서 의존성을 가진 별도 헬퍼가 필요하다.

```csharp
        private static SpDefinition ProbeSpDefWithSchema()
        {
            var spDef = new SpDefinition { Schema = "dbo", Name = "COMM_UPD", DdlText = "SELECT 1;" };
            spDef.StaticAnalysis = new SpStaticAnalysisResult { IsParsedSuccessfully = true };

            var dep = new DependencyInfo
            {
                Name = "TCommMst", Schema = "dbo", Database = "DB", Type = "USER_TABLE"
            };
            dep.Columns.Add(new ColumnInfo { ColumnName = "CLVT", DataType = "int" });
            spDef.Dependencies.Add(dep);

            return spDef;
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithSchemaTable_ShouldDeclareItComplete()
        {
            // Arrange - A 검사가 "참조 컬럼은 빠짐없이 실린다"를 보장하므로, 모델에게 줄
            // 올바른 지시는 부재 주장을 적을 빈칸을 여는 것이 아니라 그 반대다.
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDefWithSchema(), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.Contains("이 표는 이 프로시저가 참조하는 컬럼에 대해 완전합니다", body);
            Assert.Contains("스키마에 없다고 기술하지 마십시오", body);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_WithoutSchemaTable_ShouldOmitTheDeclaration()
        {
            // Arrange - 스키마 표가 없으면 "이 표는 완전합니다"는 가리킬 대상이 없는
            // 거짓 문장이 된다. ProbeSpDef는 의존성이 없어 표가 렌더링되지 않는다.
            var (service, handler) = CreateProbe();

            // Act
            await service.GenerateSpecificationAsync(ProbeSpDef(), "지침", null);

            // Assert
            var body = DecodeMessageContents(handler.LastRequestBody);
            Assert.DoesNotContain("이 표는 이 프로시저가 참조하는 컬럼에 대해 완전합니다", body);
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~GenerateSpecificationAsync_WithSchemaTable_ShouldDeclareItComplete"
```

기대: 문자열이 없어 실패. 두 번째 테스트(`WithoutSchemaTable`)는 지금도 통과한다 — 문장 자체가 없기 때문이다. 구현 후에도 통과해야 하며, 그때 비로소 "표가 있을 때만 붙는다"를 증명한다.

- [ ] **Step 3: 프롬프트에 문장을 더한다**

`AiService.BuildSpMetadataTexts`에서 `tableSchemasText`를 만드는 루프 **뒤**, 스키마 표가 하나라도 있을 때만 붙인다:

```csharp
            if (tableSchemasText.Length > 0)
            {
                // A 검사(SchemaPromptColumnSelector.DetectOrphanedColumnKeys)가 이 문장을
                // 참으로 유지하고, L1의 CheckSchemaClaims가 위반을 잡는다. 부재 주장을
                // 적을 자리를 규정하지 않는 이유는 설계 문서에 있다 - 빈칸을 규정하는
                // 것 자체가 주장을 유도한다.
                tableSchemasText.AppendLine(
                    "> 이 표는 이 프로시저가 참조하는 컬럼에 대해 완전합니다. " +
                    "참조 컬럼이 스키마에 없다고 기술하지 마십시오.");
                tableSchemasText.AppendLine();
            }
```

- [ ] **Step 4: 테스트 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~AiServiceTests_Rich"
```

- [ ] **Step 5: 문서를 갱신한다**

`docs/architecture.md`의 검증 파이프라인 절에 더한다:

```markdown
L1 기계 검증은 헤더·Mermaid 문법·UPDATE 컬럼 매핑에 더해 **스키마 주장 사실검증**을 한다.
명세서가 프롬프트에 실린 컬럼을 "존재하지 않음"으로 단정하면(`SchemaClaimFalse`), 또는 같은
물리 테이블을 서로 다른 표기로 나눠 적으면(`TableIdentitySplit`) 재생성을 요구한다.

대조 기준은 **DB 전체 컬럼이 아니라 프롬프트에 실제로 실린 컬럼**이다. 정당하게 필터에서
빠진 컬럼을 기준으로 삼으면 재생성으로 고칠 수 없는 L1 오류가 생겨 무한 재시도가 된다.
프롬프트가 참조 컬럼을 통째로 빠뜨린 경우는 별개의 결함으로 보고, L1 오류가 아니라
`spDef.Warnings`로 표면화한다 — 그것은 코드 버그이지 AI의 잘못이 아니다.

프롬프트에 어떤 컬럼이 실리는지는 `SchemaPromptColumnSelector`가 단독으로 결정한다.
`AiService`의 렌더러와 `SpecExpectations`가 같은 함수를 부른다. 이 판정을 어느 쪽에서든
복제하면 두 권위가 가장자리에서 어긋난다.
```

`AGENTS.md`에 한 줄 더한다:

```markdown
- 명세서의 스키마 주장은 L1이 기계적으로 대조한다. 대조 기준은 프롬프트에 실린 컬럼이며,
  DB 전체 컬럼이 아니다(`SchemaPromptColumnSelector`, `MechanicalValidator.CheckSchemaClaims`).
```

- [ ] **Step 6: 전체 테스트를 돌린다**

```bash
dotnet build --no-incremental && dotnet test
```

기대: 실패 0.

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/AiService.cs docs/architecture.md AGENTS.md \
        tests/ReSet.Core.Tests/AiServiceTests_Rich.cs
git commit -m "feat: tell the model the schema table is complete for referenced columns"
```

---

## Self-Review

**스펙 커버리지**

| 스펙 요구 | 태스크 |
|---|---|
| 진실의 원천 단일화(§아키텍처 1) | Task 1 |
| A 검사 — 참조 컬럼 유실(§아키텍처 2) | Task 2 |
| B 검사 — 거짓 부재 주장(§아키텍처 3) | Task 4 |
| 부재 표현 목록을 관찰에서(§아키텍처 3) | Task 4 Step 3 |
| ② 검사 — 테이블 동일성 분열(§아키텍처 4) | Task 5 |
| 배선(§아키텍처 5) | Task 3 |
| A 위반의 표면화(§아키텍처 6) | Task 3 Step 4 |
| 프롬프트 한 문장(§아키텍처 7) | Task 7 |
| 오류 타입 둘 + SuggestedPromptFix | Task 4, Task 5 |
| 수용 기준 1~4(실물 픽스처) | Task 6 |
| 수용 기준 5(A가 IsValid 불변) | Task 3 Step 5 |
| 수용 기준 6(단일 권위) | Task 1 — 렌더러가 선택기에 위임하므로 구조적으로 성립 |
| 뮤테이션 확인 | Task 1·2·4·5의 각 Step |

**타입 정합성**

- `SpecExpectations`는 Task 3에서 3-파라미터 레코드가 되고, Task 4·5는 그 `PromptSchemaColumns`만 읽는다.
- `ResolveSchemaTableKey`는 Task 4에서 정의되고 Task 5가 재사용한다 — Task 5는 Task 4에 의존한다.
- `SchemaPromptColumnSelector.KeyMatchesDependency`는 Task 1에서 `internal`로 정의되고 Task 2가 같은 어셈블리에서 쓴다.
- `ErrorType`은 Task 4에서 `SchemaClaimFalse`, Task 5에서 `TableIdentitySplit`이 더해진다. `BuildSuggestedPromptFix`의 블록 번호는 Task 4에서 5/6, Task 5에서 6/7로 두 번 밀린다 — Task 5가 Task 4의 번호를 다시 조정한다.

**의존 순서**

Task 1 → 2 → 3 → 4 → 5 → 6, Task 7은 3 이후 언제든. 병렬 실행은 1과 7을 제외하면 이득이 없다.

**남은 위험**

- Task 6의 `Assert.Equal(15, claims.Count)`는 픽스처 발췌 범위에 달려 있다. 행 번호가 어긋나면 Step 3에서 기대값을 실제 행 수로 맞춘다 — 픽스처를 고치지 않는다.
- Task 6의 `ExceptionProcCrudExcerpt.md`는 `SETTLE_POQ_DB.dbo.TSettleMst` 외에 `TPGProperty`·`TPGSettleRate` 등 다른 테이블 행도 담는다. `BuildSettleMstTruth`에는 `TSettleMst` 의존성 하나만 있으므로 나머지 행은 `ResolveSchemaTableKey`가 null을 돌려 조용히 건너뛴다 — 의도된 동작이다. `Assert.Single`이 실패하면 다른 테이블이 우연히 마지막 파트로 매칭된 것이니, 그 이름을 확인하고 truth에 명시적으로 더한다.
- Task 7 Step 3의 문장은 `tableSchemasText`에 붙는다. `BuildSpMetadataTexts`의 반환값을 쓰는 곳이 여럿이므로(명세서 생성, 함수 명세서, L2 리뷰, RAG) 이 문장이 **L2 Critic 프롬프트에도 들어간다.** Critic이 이것을 "표가 완전하다"는 사실로 읽는 것은 의도한 바이며, 해로운 방향이 아니다. 다만 Step 6의 전체 테스트에서 기존 프롬프트 단언이 깨지지 않는지 확인한다.
- `AbsenceClaimTokens`가 `Contains`로 원시 줄을 훑으므로, 코드 펜스(```` ```sql ````) 안의 SQL 주석에 그런 한국어 표현이 있으면 검사가 돈다. 셋째 조건(컬럼 실재)이 걸러 주지만, 오탐이 관측되면 `MarkdownSectionLocator.FindIndexOutsideFence`가 쓰는 펜스 추적을 이 루프에도 도입한다.
