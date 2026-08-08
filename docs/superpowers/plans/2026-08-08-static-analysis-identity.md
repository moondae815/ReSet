# 정적 분석 식별자 정합성 복구 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 정적 분석이 만들어 내는 테이블 식별자와 스키마 주입을 사실과 일치시켜, 명세서가 실존 컬럼을 "없다"고 적거나 같은 테이블을 셋으로 쪼개거나 정산일 산출 함수를 블랙박스로 남기는 일을 없앤다.

**Architecture:** 책임을 넷으로 가른다. 파서(`SqlStaticParser`)는 *무엇이 DML 대상인가*를 정하고, 신규 정규화기(`StaticAnalysisNormalizer`)는 *어떻게 부르는가*를 통일하며, 정의 조립부 두 곳(온라인 `DbMetadataService`, 오프라인 `OfflineDbMetadataService`)이 정규화기를 호출하고, 소비자(`AiService`)는 canonical 이름을 정확 비교로만 쓴다. 오프라인 경로는 저장된 파생 분석을 재생하지 않고 저장된 DDL에서 다시 계산한다.

**Tech Stack:** C# / .NET 10, xUnit, NSubstitute, Microsoft.SqlServer.TransactSql.ScriptDom 180.37.3, Serilog

## Global Constraints

- 설계 원본: `docs/superpowers/specs/2026-08-08-static-analysis-identity-design.md` (브랜치 `fix/static-analysis-identity`, 커밋 `ef22f57`)
- 작업 브랜치: `fix/static-analysis-identity` (이미 체크아웃되어 있음)
- 기준선: `dotnet clean && dotnet build` 경고 **정확히 8건** · 오류 0건, `dotnet test` 1,040건 통과. **모든 태스크가 끝날 때 이 기준선이 유지되어야 한다.**
- 그 8건은 `tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 기존 CS8600/CS8602이며 이번 작업 범위 밖이다. 이 저장소의 기존 설계 문서 3건이 같은 값을 기록하고 있다. **증분 빌드는 변경 없는 프로젝트의 경고를 다시 내지 않으므로 경고 수를 증분 빌드로 판정하지 말 것.**
- **소프트 페일 원칙(PRD §4.2)**: 이번 변경 중 어느 것도 새로운 예외 경로를 만들지 않는다. 해석 실패는 폴백으로 처리하며, 폴백은 반드시 현재 동작과 같거나 더 낫다.
- **이름을 지어내지 않는다.** DB·스키마 컨텍스트가 없으면 한정하지 않고 그대로 통과시킨다.
- **베이스 이름만으로 테이블을 병합하지 않는다.** 3-part 전체가 같아야 같은 테이블이다. (`dbo.TPGProperty`와 `PaymentDB.dbo.TPGProperty`는 컬럼 구성이 동일하지만 서로 다른 테이블이다.)
- 기존 `SqlStaticParserTests.cs:284-322`의 3-키 분리 단언은 **수정하지 않는다.** 파서 공개 계약이 안 바뀌었다는 증거로 남긴다.
- 테스트는 `output/`에 의존할 수 없다(`.gitignore` 대상, 추적되지 않음). 인라인 DDL 픽스처를 쓴다.
- 주석은 한국어로, 기존 코드의 밀도와 어조를 따른다. "무엇을"이 아니라 "왜"를 적는다.
- 커밋 메시지는 영어, `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`로 끝낸다.

---

## File Structure

| 파일 | 책임 |
|---|---|
| `src/ReSet.Core/Services/StaticAnalysisNormalizer.cs` (신규) | 테이블 표기를 canonical 3-part로 통일. 이름만 다루는 순수 함수 |
| `src/ReSet.Core/Services/SqlObjectTypeClassifier.cs` (신규) | `sys` 타입 문자열이 테이블/뷰인지 코드 객체인지 판정. TVF가 `"TABLE"`을 포함해 생기는 오분류를 한곳에서 막는다 |
| `src/ReSet.Core/Services/SqlStaticParser.cs` (수정) | UPDATE/DELETE의 대상 테이블 해석 |
| `src/ReSet.Core/Services/DbMetadataService.cs` (수정) | 분류 판정 위임, 정규화기 호출 |
| `src/ReSet.Core/Services/OfflineDbMetadataService.cs` (수정) | 저장된 DDL 재분석, 코드 객체 DDL 재링크, 정규화기 호출 |
| `src/ReSet.Core/Services/AiService.cs` (수정) | canonical 정확 비교로 컬럼 필터, 의존성 블록에 DB 표기 |
| `src/ReSet.Core/Services/CacheManager.cs` (수정) | 캐시 포맷 버전 상승 |

테스트는 각 대상과 짝을 이루는 기존 파일에 추가하고, 신규 클래스 둘만 새 테스트 파일을 만든다.

---

### Task 1: `StaticAnalysisNormalizer` — 표기 통일

**Files:**
- Create: `src/ReSet.Core/Services/StaticAnalysisNormalizer.cs`
- Test: `tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs`

**Interfaces:**
- Consumes: `ReSet.Core.Models.SpStaticAnalysisResult`, `ReSet.Core.Models.AstInsertMapping`
- Produces:
  - `public static SpStaticAnalysisResult StaticAnalysisNormalizer.Normalize(SpStaticAnalysisResult analysis, string? database, string? defaultSchema)` — 새 인스턴스를 돌려준다. 입력은 변경하지 않는다.
  - `public static string StaticAnalysisNormalizer.Canonicalize(string? writtenName, string? database, string? defaultSchema)`
  - `public static string StaticAnalysisNormalizer.CanonicalizeParts(string? database, string? schema, string name, string? fallbackDatabase, string? fallbackSchema)` — `DependencyInfo`처럼 이미 조각으로 나뉜 입력용. Task 5가 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs` 를 새로 만든다.

```csharp
using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StaticAnalysisNormalizerTests
    {
        private static SpStaticAnalysisResult Analysis() =>
            new SpStaticAnalysisResult { IsParsedSuccessfully = true };

        [Fact]
        public void Normalize_MergesTheThreeSpellingsOfOneTable()
        {
            // CANCEL_INS의 실제 형태다. 파서는 SELECT 측을 SETTLE_POQ_DB.dbo.TSettleMst로,
            // INSERT 대상 컬럼 목록을 한정 없는 TSettleMst로 키잉한다. 같은 물리 테이블이다.
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string>
            {
                "TSettleMst", "dbo.TSettleMst", "SETTLE_POQ_DB.dbo.TSettleMst"
            };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TSettleMst" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_KeepsSameNamedTablesInDifferentDatabasesApart()
        {
            // 4PLCARD는 dbo.TPGProperty와 PaymentDB.dbo.TPGProperty를 둘 다 참조한다.
            // 컬럼 구성이 동일해서 베이스 이름으로 병합하면 조용히 틀린다.
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "TPGProperty", "PaymentDB.dbo.TPGProperty" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TPGProperty", "PaymentDB.dbo.TPGProperty" },
                result.ReferencedTables);
        }

        [Fact]
        public void Normalize_UnionsColumnsOfMergedKeysInFirstSeenOrder()
        {
            // 프롬프트가 이 순서를 INSERT 매핑표의 행 순서로 쓴다.
            var analysis = Analysis();
            analysis.ReferencedColumnsPerTable = new Dictionary<string, List<string>>
            {
                { "SETTLE_POQ_DB.dbo.TSettleMst", new List<string> { "CLIENTID", "PGNAME" } },
                { "TSettleMst", new List<string> { "CLIENTID", "CYMD", "INSTATE" } }
            };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            var entry = Assert.Single(result.ReferencedColumnsPerTable);
            Assert.Equal("SETTLE_POQ_DB.dbo.TSettleMst", entry.Key);
            Assert.Equal(new[] { "CLIENTID", "PGNAME", "CYMD", "INSTATE" }, entry.Value);
        }

        [Fact]
        public void Normalize_LeavesTempTablesAndTableVariablesAlone()
        {
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "#TempBonus", "@RowSet" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "#TempBonus", "@RowSet" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_LeavesFourPartLinkedServerNamesAlone()
        {
            // 로컬 DB 이름을 씌우면 원격 참조가 로컬 테이블로 둔갑한다.
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "LINKED.RemoteDb.dbo.TRemote" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "LINKED.RemoteDb.dbo.TRemote" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_WithoutDatabaseContext_DoesNotInventQualifiers()
        {
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "TSettleMst", "dbo.TSettleMst" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, null, "dbo");

            Assert.Equal(new[] { "TSettleMst", "dbo.TSettleMst" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_StripsBrackets()
        {
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "[PaymentDB].[dbo].[TTxMst]" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "PaymentDB.dbo.TTxMst" }, result.ReferencedTables);
        }

        [Fact]
        public void Normalize_NormalizesEveryTableBearingList()
        {
            var analysis = Analysis();
            analysis.SelectTables = new List<string> { "TSettleMst" };
            analysis.InsertTables = new List<string> { "dbo.TSettleMst" };
            analysis.UpdateTables = new List<string> { "TSettleMst" };
            analysis.DeleteTables = new List<string> { "dbo.TSettleMst" };
            analysis.AstInsertMappings = new List<AstInsertMapping>
            {
                new AstInsertMapping
                {
                    TargetTable = "TSettleMst",
                    TargetColumns = new List<string> { "YMD" },
                    SourceQueryBlock = "SELECT 1"
                }
            };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            const string canonical = "SETTLE_POQ_DB.dbo.TSettleMst";
            Assert.Equal(new[] { canonical }, result.SelectTables);
            Assert.Equal(new[] { canonical }, result.InsertTables);
            Assert.Equal(new[] { canonical }, result.UpdateTables);
            Assert.Equal(new[] { canonical }, result.DeleteTables);
            Assert.Equal(canonical, Assert.Single(result.AstInsertMappings).TargetTable);
            Assert.Equal(new[] { "YMD" }, Assert.Single(result.AstInsertMappings).TargetColumns);
            Assert.Equal("SELECT 1", Assert.Single(result.AstInsertMappings).SourceQueryBlock);
        }

        [Fact]
        public void Normalize_CarriesUntouchedFieldsThrough()
        {
            // 새 인스턴스를 만들므로 옮기는 걸 빠뜨리면 조용히 데이터가 사라진다.
            var analysis = Analysis();
            analysis.ParserWarningMessage = "경고";
            analysis.ControlFlowSummary = new List<string> { "Line 1: IF" };
            analysis.ProcedureParameters = new List<string> { "@pi_strYMD" };
            analysis.DeclaredVariables = new List<string> { "@v_intID" };
            analysis.CreatedTempTables = new List<string> { "#Temp" };
            analysis.LinkedServerReferences = new List<string> { "LINKED.RemoteDb.dbo.TRemote" };
            analysis.ReferencedFunctions = new List<string> { "dbo.UF_GET_ROUND4VAT" };

            var result = StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal("경고", result.ParserWarningMessage);
            Assert.Equal(new[] { "Line 1: IF" }, result.ControlFlowSummary);
            Assert.Equal(new[] { "@pi_strYMD" }, result.ProcedureParameters);
            Assert.Equal(new[] { "@v_intID" }, result.DeclaredVariables);
            Assert.Equal(new[] { "#Temp" }, result.CreatedTempTables);
            Assert.Equal(new[] { "LINKED.RemoteDb.dbo.TRemote" }, result.LinkedServerReferences);
            Assert.Equal(new[] { "dbo.UF_GET_ROUND4VAT" }, result.ReferencedFunctions);
        }

        [Fact]
        public void Normalize_DoesNotMutateItsInput()
        {
            var analysis = Analysis();
            analysis.ReferencedTables = new List<string> { "TSettleMst" };

            StaticAnalysisNormalizer.Normalize(analysis, "SETTLE_POQ_DB", "dbo");

            Assert.Equal(new[] { "TSettleMst" }, analysis.ReferencedTables);
        }

        [Fact]
        public void CanonicalizeParts_FillsMissingDatabaseFromFallback()
        {
            // DependencyInfo.Database는 같은 DB일 때 null이다.
            var result = StaticAnalysisNormalizer.CanonicalizeParts(
                null, "dbo", "TSettleMst", "SETTLE_POQ_DB", "dbo");

            Assert.Equal("SETTLE_POQ_DB.dbo.TSettleMst", result);
        }

        [Fact]
        public void CanonicalizeParts_KeepsExplicitDatabase()
        {
            var result = StaticAnalysisNormalizer.CanonicalizeParts(
                "PaymentDB", "dbo", "TTxMst", "SETTLE_POQ_DB", "dbo");

            Assert.Equal("PaymentDB.dbo.TTxMst", result);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~StaticAnalysisNormalizerTests"`
기대: 컴파일 실패 — `StaticAnalysisNormalizer` 형식을 찾을 수 없음 (CS0103 / CS0246)

- [ ] **Step 3: 정규화기를 구현한다**

`src/ReSet.Core/Services/StaticAnalysisNormalizer.cs` 를 새로 만든다.

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정적 분석이 "쓰인 대로" 남긴 테이블 표기를 canonical 3-part로 통일한다.
    ///
    /// 파서는 SQL에 적힌 표기를 그대로 보고한다(그게 파서의 계약이다). 그래서 같은
    /// 물리 테이블이 TSettleMst / dbo.TSettleMst / SETTLE_POQ_DB.dbo.TSettleMst 세
    /// 갈래로 나뉜다. 소비자가 이를 세 테이블로 읽으면 스키마 표가 갈라지고, 배치
    /// 계획의 대상 테이블 목록이 부풀며, 컬럼 필터가 한 갈래만 보고 나머지 컬럼을
    /// "존재하지 않음"으로 만든다.
    ///
    /// AST도 DB도 보지 않는다. 이름만 다룬다.
    /// </summary>
    public static class StaticAnalysisNormalizer
    {
        /// <summary>
        /// 입력을 변경하지 않고 정리본을 돌려준다. 이름을 담지 않는 항목은 그대로 옮긴다.
        /// </summary>
        public static SpStaticAnalysisResult Normalize(
            SpStaticAnalysisResult analysis,
            string? database,
            string? defaultSchema)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            var normalized = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = analysis.IsParsedSuccessfully,
                ParserWarningMessage = analysis.ParserWarningMessage,

                // 이름을 담지 않거나 정규화 대상이 아닌 항목은 복사만 한다.
                // 임시 테이블은 세션 지역이라 DB 한정이 무의미하고, 링크드 서버는
                // 4파트 원격 참조이며, 함수 이름은 이번 범위 밖이다.
                ControlFlowSummary = new List<string>(analysis.ControlFlowSummary),
                ProcedureParameters = new List<string>(analysis.ProcedureParameters),
                DeclaredVariables = new List<string>(analysis.DeclaredVariables),
                CreatedTempTables = new List<string>(analysis.CreatedTempTables),
                LinkedServerReferences = new List<string>(analysis.LinkedServerReferences),
                ReferencedFunctions = new List<string>(analysis.ReferencedFunctions),

                ReferencedTables = NormalizeList(analysis.ReferencedTables, database, defaultSchema),
                SelectTables = NormalizeList(analysis.SelectTables, database, defaultSchema),
                InsertTables = NormalizeList(analysis.InsertTables, database, defaultSchema),
                UpdateTables = NormalizeList(analysis.UpdateTables, database, defaultSchema),
                DeleteTables = NormalizeList(analysis.DeleteTables, database, defaultSchema),
                ReferencedColumnsPerTable = MergeColumnsByTable(
                    analysis.ReferencedColumnsPerTable, database, defaultSchema)
            };

            foreach (var mapping in analysis.AstInsertMappings)
            {
                normalized.AstInsertMappings.Add(new AstInsertMapping
                {
                    TargetTable = Canonicalize(mapping.TargetTable, database, defaultSchema),
                    TargetColumns = new List<string>(mapping.TargetColumns),
                    SourceQueryBlock = mapping.SourceQueryBlock
                });
            }

            return normalized;
        }

        /// <summary>
        /// SQL에 적힌 표기 하나를 canonical 3-part로 바꾼다.
        ///
        /// DB나 스키마 컨텍스트가 없으면 한정하지 않는다 - 없는 이름을 지어내는 것보다
        /// 갈라진 채 남는 편이 낫다.
        /// </summary>
        public static string Canonicalize(string? writtenName, string? database, string? defaultSchema)
        {
            if (string.IsNullOrWhiteSpace(writtenName)) return string.Empty;

            var trimmed = writtenName.Trim();

            // 임시 테이블과 테이블 변수는 스키마 한정 대상이 아니다.
            if (trimmed.StartsWith("#", StringComparison.Ordinal) ||
                trimmed.StartsWith("@", StringComparison.Ordinal))
            {
                return trimmed;
            }

            var parts = SplitIdentifier(trimmed);

            // 4파트는 링크드 서버 참조다. 로컬 DB 이름을 씌우면 원격 테이블이
            // 로컬 테이블로 둔갑한다.
            if (parts.Count >= 4) return string.Join(".", parts);

            if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(defaultSchema))
            {
                return string.Join(".", parts);
            }

            return parts.Count switch
            {
                1 => $"{database}.{defaultSchema}.{parts[0]}",
                2 => $"{database}.{parts[0]}.{parts[1]}",
                3 => $"{parts[0]}.{parts[1]}.{parts[2]}",
                _ => string.Join(".", parts)
            };
        }

        /// <summary>
        /// 이미 조각으로 나뉜 입력(DependencyInfo 등)을 같은 규칙으로 맞춘다.
        /// DependencyInfo.Database는 분석 대상과 같은 DB일 때 null이다.
        /// </summary>
        public static string CanonicalizeParts(
            string? database,
            string? schema,
            string name,
            string? fallbackDatabase,
            string? fallbackSchema)
        {
            var resolvedDatabase = string.IsNullOrWhiteSpace(database) ? fallbackDatabase : database;
            var resolvedSchema = string.IsNullOrWhiteSpace(schema) ? fallbackSchema : schema;

            if (string.IsNullOrWhiteSpace(resolvedDatabase) || string.IsNullOrWhiteSpace(resolvedSchema))
            {
                return Canonicalize(name, null, null);
            }

            return Canonicalize($"{resolvedDatabase}.{resolvedSchema}.{name}", resolvedDatabase, resolvedSchema);
        }

        private static List<string> NormalizeList(
            IEnumerable<string> names,
            string? database,
            string? defaultSchema)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var name in names)
            {
                var canonical = Canonicalize(name, database, defaultSchema);
                if (string.IsNullOrWhiteSpace(canonical)) continue;
                if (seen.Add(canonical)) result.Add(canonical);
            }

            return result;
        }

        private static Dictionary<string, List<string>> MergeColumnsByTable(
            Dictionary<string, List<string>> source,
            string? database,
            string? defaultSchema)
        {
            var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var seenColumns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in source)
            {
                var canonical = Canonicalize(entry.Key, database, defaultSchema);
                if (string.IsNullOrWhiteSpace(canonical)) continue;

                if (!merged.TryGetValue(canonical, out var columns))
                {
                    columns = new List<string>();
                    merged[canonical] = columns;
                    seenColumns[canonical] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                var seen = seenColumns[canonical];
                foreach (var column in entry.Value)
                {
                    // 첫 등장 순서를 보존한다 - 프롬프트가 이 순서를 INSERT 매핑표의
                    // 행 순서로 쓴다.
                    if (seen.Add(column)) columns.Add(column);
                }
            }

            return merged;
        }

        /// <summary>
        /// 대괄호 안의 점은 구분자가 아니다. [my.table] 같은 이름을 쪼개지 않는다.
        /// </summary>
        private static List<string> SplitIdentifier(string name)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            var inBracket = false;

            foreach (var ch in name)
            {
                if (ch == '[') { inBracket = true; continue; }
                if (ch == ']') { inBracket = false; continue; }
                if (ch == '.' && !inBracket)
                {
                    parts.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
                current.Append(ch);
            }

            parts.Add(current.ToString().Trim());
            return parts;
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~StaticAnalysisNormalizerTests"`
기대: 12건 전부 PASS

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/StaticAnalysisNormalizer.cs tests/ReSet.Core.Tests/StaticAnalysisNormalizerTests.cs
git commit -m "$(cat <<'EOF'
feat: canonicalize table identifiers from static analysis

The parser reports names as they were written, so one physical table
arrives as three: TSettleMst, dbo.TSettleMst, SETTLE_POQ_DB.dbo.TSettleMst.
Consumers that read those as three tables split the schema table, inflate
the batch plan's target list, and drop columns the filter never saw.

Merge on the full three-part name only. dbo.TPGProperty and
PaymentDB.dbo.TPGProperty carry identical column sets but are different
tables; base-name merging would collapse them silently.

Temp tables, table variables and four-part linked-server names pass
through untouched, and without a database context nothing is qualified at
all — a split name beats an invented one.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 파서 — UPDATE/DELETE 대상 해석

**Files:**
- Modify: `src/ReSet.Core/Services/SqlStaticParser.cs`
- Test: `tests/ReSet.Core.Tests/SqlStaticParserTests.cs`

**Interfaces:**
- Consumes: 없음 (Task 1과 독립)
- Produces: `SpStaticAnalysisResult.UpdateTables` / `.DeleteTables`가 DML 대상만 담는다. FROM 절 조인 원본은 `.SelectTables`로 간다. 대상 별칭은 실제 테이블로 해석된다.

**배경:** `_statementContext`가 `"UPDATE"`/`"DELETE"`로 눌린 동안 방문되는 모든 `NamedTableReference`가 대상 목록에 들어간다(`SqlStaticParser.cs:405,408`). INSERT는 이미 `InsertSpecification.Target`을 붙잡는 올바른 패턴을 갖고 있다(258-260행). 같은 패턴을 UPDATE/DELETE에 대칭으로 넣는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SqlStaticParserTests.cs` 의 `SqlStaticParserTests` 클래스 안, 마지막 `}` 두 개 앞에 아래를 추가한다. **기존 테스트는 하나도 수정하지 않는다.**

```csharp
        [Fact]
        public void Analyze_UpdateWithAliasTarget_ShouldRecordOnlyTheResolvedTarget()
        {
            // EXPECT_PROC 2-6절의 형태다. 예전에는 별칭 'A' 자체가 테이블로 등록되고
            // FROM 절 조인 원본까지 전부 UPDATE 대상이 됐다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateTarget
AS
BEGIN
    UPDATE A
    SET    A.OutYMD = B.OutYMD
    FROM   SETTLE_POQ_DB.dbo.TSettleMst A
    JOIN   SETTLE_POQ_DB.dbo.TClientCMRate C ON A.ClientID = C.ClientID
    JOIN   SETTLE_POQ_DB.dbo.TSettleMst B ON A.MPLTID = B.PLTID;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "SETTLE_POQ_DB.dbo.TSettleMst" }, result.UpdateTables);
            Assert.DoesNotContain("A", result.ReferencedTables);
            Assert.Contains("SETTLE_POQ_DB.dbo.TClientCMRate", result.SelectTables);
        }

        [Fact]
        public void Analyze_UpdateWithFromSources_ShouldFileJoinSourcesAsReads()
        {
            // COMM_UPD의 지배적 형태. 대상은 TSettleMst 하나뿐이고 나머지는 읽기다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateFrom
AS
BEGIN
    UPDATE TSettleMst
    SET    CLCOMM = B.CommissionAmt
    FROM   TSettleMst        A
          ,TClientSettleRate B
          ,TPGSettleRate     C
    WHERE  A.ClientID = B.ClientID
    AND    A.PGName   = C.PGName;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "TSettleMst" }, result.UpdateTables);
            Assert.Contains("TClientSettleRate", result.SelectTables);
            Assert.Contains("TPGSettleRate", result.SelectTables);
            Assert.DoesNotContain("TClientSettleRate", result.UpdateTables);
            Assert.DoesNotContain("TPGSettleRate", result.UpdateTables);
        }

        [Fact]
        public void Analyze_UpdateTargetAlsoInFromClause_ShouldAppearAsBothTargetAndRead()
        {
            // 대상이 FROM 절에도 나타나면 실제로 읽고 쓴다. 양쪽에 기록하는 게 사실이다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateSelfRead
AS
BEGIN
    UPDATE TSettleMst
    SET    CLTotal = A.CLComm + A.CLVT
    FROM   TSettleMst A
    WHERE  A.YMD = '20260808';
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "TSettleMst" }, result.UpdateTables);
            Assert.Contains("TSettleMst", result.SelectTables);
        }

        [Fact]
        public void Analyze_DeleteWithAliasTarget_ShouldRecordOnlyTheResolvedTarget()
        {
            // 4PLCARD의 형태. DeleteTables가 ['A','TSettleMst','TPGProperty']였다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestDeleteTarget
AS
BEGIN
    DELETE A
    FROM   TSettleMst A
    INNER JOIN TPGProperty AS PG ON A.PGName = PG.PGName
    WHERE  A.TxAmt = 0;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "TSettleMst" }, result.DeleteTables);
            Assert.DoesNotContain("A", result.ReferencedTables);
            Assert.Contains("TPGProperty", result.SelectTables);
        }

        [Fact]
        public void Analyze_DeleteWithQualifiedFromSource_ShouldNotDoubleCountTheTarget()
        {
            // AcqManual의 형태. 한정 없는 대상과 3파트 FROM 원본이 같은 테이블이다.
            // 표기 통일은 정규화기 몫이고, 여기서는 대상이 하나만 잡히면 된다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestDeleteQualified
AS
BEGIN
    DELETE TSettleByOUT
    FROM   SETTLE_POQ_DB.dbo.TSettleByOUT
    WHERE  OutYMD = '20260808';
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "TSettleByOUT" }, result.DeleteTables);
        }

        [Fact]
        public void Analyze_PlainUpdateWithoutFromClause_ShouldStillRecordTheTarget()
        {
            var ddlText = @"
CREATE PROCEDURE dbo.TestPlainUpdate
AS
BEGIN
    UPDATE dbo.TSettleMst
    SET    PGComm = 0
    WHERE  YMD = '20260808';
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Equal(new[] { "dbo.TSettleMst" }, result.UpdateTables);
            Assert.Contains("dbo.TSettleMst", result.ReferencedTables);
        }

        [Fact]
        public void Analyze_UpdateTargetingTableVariable_ShouldFallBackToOldBehaviour()
        {
            // 대상을 해석할 수 없으면 그 문장에 한해 예전처럼 문맥 내 전체를 수집한다.
            // 대상을 통째로 잃는 것보다 과다 보고가 낫다.
            var ddlText = @"
CREATE PROCEDURE dbo.TestUpdateTableVariable
AS
BEGIN
    DECLARE @Buffer TABLE (Id INT, Amt INT);

    UPDATE @Buffer
    SET    Amt = S.TxAmt
    FROM   @Buffer B
    JOIN   TSettleMst S ON B.Id = S.ID;
END;
";
            var parser = new SqlStaticParser();

            var result = parser.Analyze(ddlText);

            Assert.True(result.IsParsedSuccessfully);
            Assert.Contains("TSettleMst", result.UpdateTables);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~SqlStaticParserTests"`
기대: 새로 넣은 7건 중 최소 5건 FAIL. `Analyze_UpdateWithAliasTarget_ShouldRecordOnlyTheResolvedTarget`은 `UpdateTables`가 `["A", "SETTLE_POQ_DB.dbo.TSettleMst", "SETTLE_POQ_DB.dbo.TClientCMRate"]`라서 실패한다.

- [ ] **Step 3: `GetSchemaObjectString`을 static으로 바꾼다**

`src/ReSet.Core/Services/SqlStaticParser.cs:631` 의 시그니처만 바꾼다. 본문은 인스턴스 상태를 쓰지 않는다.

```csharp
        private static string GetSchemaObjectString(SchemaObjectName schemaObject)
```

(745행의 `TableAliasVisitor` 쪽 동명 메서드는 건드리지 않는다.)

- [ ] **Step 4: 대상 추적 필드를 추가한다**

`src/ReSet.Core/Services/SqlStaticParser.cs:193` 의 `_currentInsertTarget` 선언 바로 아래에 두 줄을 넣는다.

```csharp
        private string? _currentInsertTarget = null;
        private TSqlFragment? _currentDmlTargetNode = null;
        private bool _dmlTargetResolved = false;
```

- [ ] **Step 5: `UpdateSpecification` / `DeleteSpecification` 방문자를 교체한다**

`src/ReSet.Core/Services/SqlStaticParser.cs:292-304` 의 두 메서드를 통째로 아래로 바꾼다.

```csharp
        public override void ExplicitVisit(UpdateSpecification node)
        {
            _statementContext.Push("UPDATE");
            var prevTargetNode = _currentDmlTargetNode;
            var prevResolved = _dmlTargetResolved;

            _currentDmlTargetNode = node.Target;
            _dmlTargetResolved = RecordDmlTarget(node.Target, node.FromClause, UpdateTables, _foundUpdate);

            base.ExplicitVisit(node);

            _currentDmlTargetNode = prevTargetNode;
            _dmlTargetResolved = prevResolved;
            _statementContext.Pop();
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            _statementContext.Push("DELETE");
            var prevTargetNode = _currentDmlTargetNode;
            var prevResolved = _dmlTargetResolved;

            _currentDmlTargetNode = node.Target;
            _dmlTargetResolved = RecordDmlTarget(node.Target, node.FromClause, DeleteTables, _foundDelete);

            base.ExplicitVisit(node);

            _currentDmlTargetNode = prevTargetNode;
            _dmlTargetResolved = prevResolved;
            _statementContext.Pop();
        }

        /// <summary>
        /// UPDATE·DELETE의 대상 테이블 하나만 기록한다. INSERT가 이미 하는 것과 대칭이다.
        ///
        /// 대상이 별칭이면(UPDATE A SET ... FROM T A) 그 문장의 FROM 절에서 푼다.
        /// 전역 별칭 사전을 쓰지 않는 이유: 마지막 등록이 이기므로, 같은 별칭을 다른
        /// 문장이 다른 테이블에 쓰면 엉뚱한 테이블로 풀린다.
        ///
        /// 풀지 못하면 false를 돌려주고 호출부는 그 문장에 한해 기존 동작(문맥 내 전체
        /// 수집)으로 돌아간다. 대상을 통째로 잃는 것보다 과다 보고가 낫다.
        /// </summary>
        private bool RecordDmlTarget(
            TableReference? target,
            FromClause? fromClause,
            List<string> targetList,
            HashSet<string> seen)
        {
            if (target is not NamedTableReference named || named.SchemaObject == null) return false;

            var written = GetSchemaObjectString(named.SchemaObject);
            if (string.IsNullOrWhiteSpace(written)) return false;

            var resolved = ResolveDmlTargetName(written, fromClause);

            if (resolved.StartsWith("#", StringComparison.Ordinal))
            {
                if (_foundTemps.Add(resolved)) CreatedTempTables.Add(resolved);
                return true;
            }

            if (_foundTables.Add(resolved)) ReferencedTables.Add(resolved);
            if (seen.Add(resolved)) targetList.Add(resolved);
            return true;
        }

        private static string ResolveDmlTargetName(string written, FromClause? fromClause)
        {
            // 한정된 이름은 별칭일 수 없다.
            if (written.Contains('.')) return written;

            var fromAlias = ResolveAliasWithinFromClause(fromClause, written);
            return string.IsNullOrWhiteSpace(fromAlias) ? written : fromAlias!;
        }

        private static string? ResolveAliasWithinFromClause(FromClause? fromClause, string alias)
        {
            if (fromClause == null) return null;

            var finder = new AliasTargetFinder(alias);
            fromClause.Accept(finder);
            return finder.ResolvedTableName;
        }

        /// <summary>
        /// FROM 절 하나 안에서 주어진 별칭이 가리키는 테이블을 찾는다.
        /// </summary>
        private sealed class AliasTargetFinder : TSqlFragmentVisitor
        {
            private readonly string _alias;

            public string? ResolvedTableName { get; private set; }

            public AliasTargetFinder(string alias)
            {
                _alias = alias;
            }

            public override void Visit(NamedTableReference node)
            {
                if (ResolvedTableName != null) return;
                if (node.SchemaObject == null) return;
                if (node.Alias == null || string.IsNullOrWhiteSpace(node.Alias.Value)) return;
                if (!string.Equals(node.Alias.Value, _alias, StringComparison.OrdinalIgnoreCase)) return;

                ResolvedTableName = GetSchemaObjectString(node.SchemaObject);
            }
        }
```

- [ ] **Step 6: `NamedTableReference`에서 대상 노드를 건너뛰고 조인 원본을 읽기로 분류한다**

`src/ReSet.Core/Services/SqlStaticParser.cs:343-345`, 메서드 시작부를 아래로 바꾼다.

```csharp
        public override void ExplicitVisit(NamedTableReference node)
        {
            base.ExplicitVisit(node);

            // DML 대상 노드는 RecordDmlTarget이 이미 해석해 기록했다. 여기서 다시 보면
            // UPDATE A 의 'A' 같은 별칭이 테이블 이름으로 새어 들어간다.
            if (_dmlTargetResolved && ReferenceEquals(node, _currentDmlTargetNode)) return;

            if (node.SchemaObject != null)
```

이어서 396-409행의 `switch` 중 `UPDATE`/`DELETE` 두 갈래를 아래로 바꾼다.

```csharp
                            case "UPDATE":
                                // FROM 절 조인 원본은 읽기일 뿐 갱신 대상이 아니다.
                                // 대상은 RecordDmlTarget이 이미 기록했다.
                                if (_dmlTargetResolved)
                                {
                                    if (_foundSelect.Add(tableName)) SelectTables.Add(tableName);
                                }
                                else if (_foundUpdate.Add(tableName))
                                {
                                    UpdateTables.Add(tableName);
                                }
                                break;
                            case "DELETE":
                                if (_dmlTargetResolved)
                                {
                                    if (_foundSelect.Add(tableName)) SelectTables.Add(tableName);
                                }
                                else if (_foundDelete.Add(tableName))
                                {
                                    DeleteTables.Add(tableName);
                                }
                                break;
```

- [ ] **Step 7: 테스트가 통과하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~SqlStaticParserTests"`
기대: 신규 7건 PASS, **기존 테스트 전부 PASS** (특히 `Analyze_WithAliasesAndInsertTarget_ShouldResolveColumnsCorrectly`)

- [ ] **Step 8: 전체 테스트로 회귀를 확인한다**

실행: `dotnet test`
기대: 실패 0건. 통과 수는 1,040 + 신규분.

- [ ] **Step 9: 커밋한다**

```bash
git add src/ReSet.Core/Services/SqlStaticParser.cs tests/ReSet.Core.Tests/SqlStaticParserTests.cs
git commit -m "$(cat <<'EOF'
fix: record only the real target of an UPDATE or DELETE

The statement context was pushed for the whole statement, so every table
reached while inside it — including every FROM-clause join source — was
filed as a DML target. EXCEPTION_PROC came out with eleven update targets
when it has one, and DELETE A FROM T A left the bare alias 'A' sitting in
the table list.

INSERT already captures InsertSpecification.Target and tracks it. Give
UPDATE and DELETE the same treatment, resolving an aliased target against
that statement's own FROM clause rather than the global alias map, whose
last-write-wins entries point at whichever table used the alias last.

When the target cannot be resolved at all — a table variable, a function
call — that one statement falls back to the previous behaviour. Over-
reporting beats losing the target entirely.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `SqlObjectTypeClassifier` — TVF 오분류 차단

**Files:**
- Create: `src/ReSet.Core/Services/SqlObjectTypeClassifier.cs`
- Modify: `src/ReSet.Core/Services/DbMetadataService.cs:703, 756-763, 828, 844`
- Test: `tests/ReSet.Core.Tests/SqlObjectTypeClassifierTests.cs`

**Interfaces:**
- Consumes: `ReSet.Core.Models.CodeObjectType`
- Produces:
  - `public static bool SqlObjectTypeClassifier.IsCodeObject(string? sqlObjectType)`
  - `public static bool SqlObjectTypeClassifier.IsTableOrView(string? sqlObjectType)`
  - `public static CodeObjectType SqlObjectTypeClassifier.ResolveCodeObjectType(string? sqlObjectType)` — Task 4가 쓴다

**배경:** `DbMetadataService.cs:828`의 재귀 경로가 `rawDep.Type.Contains("TABLE")`로 분기한다. `SQL_TABLE_VALUED_FUNCTION`이 여기 걸려 테이블 취급되고 `ReferencedDdlText`를 가져오지 않는다. 형제 경로인 `IsTableOrViewType`(756행)은 `!IsCodeObjectType(...)` 가드를 이미 갖고 있다 — 한쪽만 고쳐져 있다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SqlObjectTypeClassifierTests.cs` 를 새로 만든다.

```csharp
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SqlObjectTypeClassifierTests
    {
        [Theory]
        [InlineData("SQL_TABLE_VALUED_FUNCTION")]
        [InlineData("SQL_INLINE_TABLE_VALUED_FUNCTION")]
        [InlineData("SQL_SCALAR_FUNCTION")]
        [InlineData("SQL_STORED_PROCEDURE")]
        public void IsCodeObject_ShouldRecogniseFunctionsAndProcedures(string sqlObjectType)
        {
            Assert.True(SqlObjectTypeClassifier.IsCodeObject(sqlObjectType));
        }

        [Theory]
        [InlineData("USER_TABLE")]
        [InlineData("VIEW")]
        [InlineData("SYSTEM_TABLE")]
        public void IsCodeObject_ShouldRejectTablesAndViews(string sqlObjectType)
        {
            Assert.False(SqlObjectTypeClassifier.IsCodeObject(sqlObjectType));
        }

        [Fact]
        public void IsTableOrView_ShouldRejectTableValuedFunctions()
        {
            // 이것이 UIF_SettleYMD의 DDL이 주입되지 않은 이유다.
            // "SQL_TABLE_VALUED_FUNCTION"은 "TABLE"을 포함한다.
            Assert.False(SqlObjectTypeClassifier.IsTableOrView("SQL_TABLE_VALUED_FUNCTION"));
        }

        [Theory]
        [InlineData("USER_TABLE")]
        [InlineData("VIEW")]
        public void IsTableOrView_ShouldAcceptTablesAndViews(string sqlObjectType)
        {
            Assert.True(SqlObjectTypeClassifier.IsTableOrView(sqlObjectType));
        }

        [Fact]
        public void Predicates_ShouldTreatNullAsNeither()
        {
            Assert.False(SqlObjectTypeClassifier.IsCodeObject(null));
            Assert.False(SqlObjectTypeClassifier.IsTableOrView(null));
        }

        [Theory]
        [InlineData("SQL_TABLE_VALUED_FUNCTION", CodeObjectType.Function)]
        [InlineData("SQL_SCALAR_FUNCTION", CodeObjectType.Function)]
        [InlineData("SQL_STORED_PROCEDURE", CodeObjectType.Procedure)]
        [InlineData("USER_TABLE", CodeObjectType.Unresolved)]
        [InlineData(null, CodeObjectType.Unresolved)]
        public void ResolveCodeObjectType_ShouldMapSqlTypeStrings(string? sqlObjectType, CodeObjectType expected)
        {
            Assert.Equal(expected, SqlObjectTypeClassifier.ResolveCodeObjectType(sqlObjectType));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~SqlObjectTypeClassifierTests"`
기대: 컴파일 실패 — `SqlObjectTypeClassifier` 형식을 찾을 수 없음

- [ ] **Step 3: 분류기를 구현한다**

`src/ReSet.Core/Services/SqlObjectTypeClassifier.cs` 를 새로 만든다.

```csharp
using System;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>
    /// sys 카탈로그의 타입 문자열이 테이블/뷰인지 코드 객체인지 판정한다.
    ///
    /// 부분 문자열 판정을 한곳에 모으는 이유: "SQL_TABLE_VALUED_FUNCTION"은
    /// "TABLE"을 포함한다. 호출부마다 따로 판정하면 한쪽만 가드를 갖게 되고,
    /// 실제로 그렇게 되어 TVF의 DDL이 수집되지 않았다.
    /// </summary>
    public static class SqlObjectTypeClassifier
    {
        public static bool IsCodeObject(string? sqlObjectType) =>
            sqlObjectType?.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase) == true ||
            sqlObjectType?.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase) == true;

        public static bool IsTableOrView(string? sqlObjectType) =>
            !IsCodeObject(sqlObjectType) &&
            (sqlObjectType?.Contains("TABLE", StringComparison.OrdinalIgnoreCase) == true ||
             sqlObjectType?.Contains("VIEW", StringComparison.OrdinalIgnoreCase) == true);

        public static CodeObjectType ResolveCodeObjectType(string? sqlObjectType)
        {
            if (sqlObjectType?.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase) == true)
            {
                return CodeObjectType.Function;
            }

            if (sqlObjectType?.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase) == true)
            {
                return CodeObjectType.Procedure;
            }

            return CodeObjectType.Unresolved;
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~SqlObjectTypeClassifierTests"`
기대: 16건 PASS

- [ ] **Step 5: `DbMetadataService`가 분류기에 위임하게 한다**

`src/ReSet.Core/Services/DbMetadataService.cs:756-763` 의 두 private 메서드를 지우고, 세 호출 지점을 바꾼다.

먼저 756-763행을 삭제한다.

```csharp
        private static bool IsTableOrViewType(string? dependencyType) =>
            !IsCodeObjectType(dependencyType) &&
            (dependencyType?.Contains("TABLE", StringComparison.OrdinalIgnoreCase) == true ||
             dependencyType?.Contains("VIEW", StringComparison.OrdinalIgnoreCase) == true);

        private static bool IsCodeObjectType(string? dependencyType) =>
            dependencyType?.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase) == true ||
            dependencyType?.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase) == true;
```

703행의 `IsTableOrViewType(directDependency.Type)` 를 바꾼다.

```csharp
                        if (!isExternalDependency &&
                            SqlObjectTypeClassifier.IsTableOrView(directDependency.Type))
```

726행의 `else if (IsCodeObjectType(directDependency.Type))` 를 바꾼다.

```csharp
                        else if (SqlObjectTypeClassifier.IsCodeObject(directDependency.Type))
```

`GatherDependenciesRecursiveAsync` 안, `// 스키마 조회 분기 (테이블, 뷰)` 주석 바로 아래 줄 — **이것이 TVF 결함의 지점이다.**

찾을 것:
```csharp
                if (rawDep.Type.Contains("TABLE") || rawDep.Type.Contains("VIEW"))
```
바꿀 것:
```csharp
                if (SqlObjectTypeClassifier.IsTableOrView(rawDep.Type))
```

같은 메서드의 `// 코드 수집 및 하위 재귀 분기 (UDF, SP)` 주석 바로 아래 줄.

찾을 것:
```csharp
                else if (rawDep.Type.Contains("FUNCTION") || rawDep.Type.Contains("PROCEDURE"))
```
바꿀 것:
```csharp
                else if (SqlObjectTypeClassifier.IsCodeObject(rawDep.Type))
```

그 블록 안에서 `childType`을 계산하는 삼항 연산자.

찾을 것:
```csharp
                        var childType = rawDep.Type.Contains("FUNCTION")
                            ? CodeObjectType.Function
                            : CodeObjectType.Procedure;
```
바꿀 것:
```csharp
                        var childType = SqlObjectTypeClassifier.ResolveCodeObjectType(rawDep.Type);
```

- [ ] **Step 6: 빌드와 전체 테스트를 확인한다**

실행: `dotnet build`
기대: 오류 0건. 경고는 기존 8건(`DbMetadataServiceTests`의 CS8600/CS8602)만 남고 새 경고가 없어야 한다

실행: `dotnet test`
기대: 실패 0건

- [ ] **Step 7: 커밋한다**

```bash
git add src/ReSet.Core/Services/SqlObjectTypeClassifier.cs \
        tests/ReSet.Core.Tests/SqlObjectTypeClassifierTests.cs \
        src/ReSet.Core/Services/DbMetadataService.cs
git commit -m "$(cat <<'EOF'
fix: stop filing table-valued functions as tables

The recursive dependency walk branched on Type.Contains("TABLE"), and
SQL_TABLE_VALUED_FUNCTION contains "TABLE". UIF_SettleYMD — the function
that computes every settlement date in this batch — was collected as a
table, so its DDL was never fetched and five steps of EXPECT_PROC were
documented against a black box.

The direct-dependency path already guarded against this with a predicate
that excludes code objects first. Only the recursive path was missing it.

Move both predicates into one classifier so the two paths cannot drift
apart again, and give it the sys-type-to-CodeObjectType mapping the
offline relink needs next.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: 온라인 경로에 정규화기 연결

**Files:**
- Modify: `src/ReSet.Core/Services/DbMetadataService.cs:615-616`
- Test: `tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs`

**Interfaces:**
- Consumes: `StaticAnalysisNormalizer.Normalize` (Task 1)
- Produces: `SpDefinition.StaticAnalysis`가 canonical 표기로 저장된다. `metadata.json`과 스냅샷에도 그대로 반영된다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs` 의 마지막 클래스 닫는 중괄호 앞에 추가한다. 파일 상단에 `using ReSet.Core.Models;` 와 `using ReSet.Core.Services;` 가 없으면 추가한다.

```csharp
        [Fact]
        public void NormalizeStaticAnalysisForDefinition_ShouldCanonicaliseAgainstTheObjectKey()
        {
            // DB 연결 없이 정규화 배선만 확인한다. 실제 수집은 통합 검증 몫이다.
            var definition = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_TEST",
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    ReferencedTables = new System.Collections.Generic.List<string>
                    {
                        "TSettleMst", "dbo.TSettleMst", "SETTLE_POQ_DB.dbo.TSettleMst"
                    }
                }
            };

            definition.StaticAnalysis = StaticAnalysisNormalizer.Normalize(
                definition.StaticAnalysis,
                definition.ObjectKey?.Database,
                definition.Schema);

            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                definition.StaticAnalysis.ReferencedTables);
        }

        [Fact]
        public void DbMetadataService_ShouldNormaliseStaticAnalysisBeforeReturning()
        {
            // 배선이 빠지면 조용히 예전 표기가 저장된다. 소스에 호출이 있는지 고정한다.
            var source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/DbMetadataService.cs"));

            Assert.Contains("StaticAnalysisNormalizer.Normalize(", source);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~DbMetadataServiceDetailsTests"`
기대: `DbMetadataService_ShouldNormaliseStaticAnalysisBeforeReturning` FAIL (문자열 없음). 첫 번째는 PASS (Task 1이 이미 있으므로).

- [ ] **Step 3: 정규화 호출을 넣는다**

`src/ReSet.Core/Services/DbMetadataService.cs`, 2차 정밀 분석 `catch` 블록(612-615행) 바로 다음, 617행의 완료 로그 바로 앞에 넣는다.

```csharp
            // 정적 분석은 SQL에 적힌 표기를 그대로 남긴다. 여기서 canonical 3-part로
            // 통일해 두면 metadata.json·스냅샷·프롬프트가 같은 이름을 쓰게 된다.
            objectDefinition.StaticAnalysis = StaticAnalysisNormalizer.Normalize(
                objectDefinition.StaticAnalysis,
                objectDefinition.ObjectKey?.Database,
                objectDefinition.Schema);

            Log.Information(
                "[DbMetadata] 코드 객체 메타데이터 수집 완료 - 객체: {ObjectFullName}, 의존 객체: {DepCount}개, 경고: {WarnCount}개",
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~DbMetadataServiceDetailsTests"`
기대: 전부 PASS

- [ ] **Step 5: 전체 테스트를 확인한다**

실행: `dotnet test`
기대: 실패 0건

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/DbMetadataService.cs tests/ReSet.Core.Tests/DbMetadataServiceDetailsTests.cs
git commit -m "$(cat <<'EOF'
fix: canonicalize static analysis before it is persisted

Normalize right after the refined parse, so metadata.json, the snapshot
and the prompt all carry the same spelling of a table. Without this the
consumer has to canonicalize on every read, and any consumer that forgets
sees the split names again.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: 오프라인 경로 — 저장된 DDL 재분석과 DDL 재링크

**Files:**
- Modify: `src/ReSet.Core/Services/OfflineDbMetadataService.cs:106-143`
- Test: `tests/ReSet.Core.Tests/OfflineDbMetadataServiceTests.cs`

**Interfaces:**
- Consumes: `StaticAnalysisNormalizer.Normalize` (Task 1), `SqlStaticParser.Analyze` (Task 2), `SqlObjectTypeClassifier.ResolveCodeObjectType` (Task 3)
- Produces: 오프라인 모드가 온라인과 같은 정적 분석 결과를 낸다.

**배경:** 스냅샷은 `StaticAnalysis`를 결과물로 저장하고 파서를 다시 돌리지 않는다. 정규화기만 걸면 표기는 통일되지만 Task 2의 파서 수정이 반영되지 않는다. 스냅샷은 `DdlText`를 온전히 갖고 있으므로 저장된 원본에서 다시 계산한다. TVF DDL도 마찬가지로 `CodeObjects`에 이미 들어 있고 의존성 항목의 링크만 비어 있다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/OfflineDbMetadataServiceTests.cs` 의 클래스 안 마지막에 추가한다.

```csharp
        [Fact]
        public async Task GetCodeObjectDetailsDirectAsync_ShouldReparseStoredDdlInsteadOfReplayingStaleAnalysis()
        {
            // 스냅샷에 저장된 StaticAnalysis는 옛 파서가 만든 것이다. 그대로 재생하면
            // 파서를 고쳐도 오프라인 모드는 영원히 예전 결과를 낸다.
            var snapshot = new DbSnapshot { Database = "SETTLE_POQ_DB" };
            var stored = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = @"
CREATE PROCEDURE dbo.UP_TEST
AS
BEGIN
    UPDATE A
    SET    A.OutYMD = '20260808'
    FROM   TSettleMst A
    JOIN   TClientCMRate C ON A.ClientID = C.ClientID;
END;
",
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    // 옛 파서의 산출물을 흉내 낸다.
                    UpdateTables = new List<string> { "A", "TSettleMst", "TClientCMRate" }
                }
            };
            snapshot.StoredProcedures.Add("dbo.UP_TEST", stored);

            var service = new OfflineDbMetadataService(snapshot);
            var objectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure);

            var definition = await service.GetCodeObjectDetailsDirectAsync(
                "dummy", objectKey, CancellationToken.None);

            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                definition.StaticAnalysis.UpdateTables);
            Assert.Contains("SETTLE_POQ_DB.dbo.TClientCMRate", definition.StaticAnalysis.SelectTables);
        }

        [Fact]
        public async Task GetCodeObjectDetailsDirectAsync_ShouldRelinkCodeObjectDdlFromSnapshot()
        {
            // UIF_SettleYMD의 DDL은 CodeObjects에 들어 있는데 의존성 항목의 링크만 비어 있다.
            var snapshot = new DbSnapshot { Database = "SETTLE_POQ_DB" };
            var functionKey = CodeObjectKey.Create(
                "SETTLE_POQ_DB", "dbo", "UIF_SettleYMD", CodeObjectType.Function);
            snapshot.CodeObjects.Add(
                functionKey.CanonicalName,
                new SpDefinition
                {
                    Schema = "dbo",
                    Name = "UIF_SettleYMD",
                    DdlText = "CREATE FUNCTION dbo.UIF_SettleYMD() RETURNS TABLE AS RETURN SELECT 1 AS OutYMD;"
                });

            var stored = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "CREATE PROCEDURE dbo.UP_TEST AS BEGIN SELECT 1; END;"
            };
            stored.Dependencies.Add(new DependencyInfo
            {
                SourceObjectKey = CodeObjectKey.Create(
                    "SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UIF_SettleYMD",
                Type = "SQL_TABLE_VALUED_FUNCTION",
                ReferencedDdlText = null
            });
            snapshot.StoredProcedures.Add("dbo.UP_TEST", stored);

            var service = new OfflineDbMetadataService(snapshot);
            var objectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure);

            var definition = await service.GetCodeObjectDetailsDirectAsync(
                "dummy", objectKey, CancellationToken.None);

            var dependency = Assert.Single(definition.Dependencies);
            Assert.Contains("RETURNS TABLE", dependency.ReferencedDdlText);
        }

        [Fact]
        public async Task GetCodeObjectDetailsDirectAsync_WhenDdlCannotBeParsed_ShouldKeepStoredAnalysis()
        {
            // 재파싱이 실패해도 오프라인 모드가 지금보다 나빠지면 안 된다.
            var snapshot = new DbSnapshot { Database = "SETTLE_POQ_DB" };
            var stored = new SpDefinition
            {
                Schema = "dbo",
                Name = "UP_BROKEN",
                // SELECT 뒤에 선택 목록 없이 FROM이 오면 T-SQL 문법 오류다.
                DdlText = "CREATE PROCEDURE dbo.UP_BROKEN AS BEGIN SELECT FROM; END;",
                StaticAnalysis = new SpStaticAnalysisResult
                {
                    IsParsedSuccessfully = true,
                    UpdateTables = new List<string> { "TSettleMst" }
                }
            };
            snapshot.StoredProcedures.Add("dbo.UP_BROKEN", stored);

            var service = new OfflineDbMetadataService(snapshot);
            var objectKey = CodeObjectKey.Create("SETTLE_POQ_DB", "dbo", "UP_BROKEN", CodeObjectType.Procedure);

            var definition = await service.GetCodeObjectDetailsDirectAsync(
                "dummy", objectKey, CancellationToken.None);

            // 저장본이 살아남되 표기는 통일된다.
            Assert.Equal(
                new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
                definition.StaticAnalysis.UpdateTables);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~OfflineDbMetadataServiceTests"`
기대: 신규 3건 FAIL. 첫 번째는 `UpdateTables`가 `["A","TSettleMst","TClientCMRate"]` 그대로라 실패한다.

- [ ] **Step 3: 재분석·재링크·정규화를 넣는다**

`src/ReSet.Core/Services/OfflineDbMetadataService.cs`, `GetDirectDefinitionAsync` 의 `return directDefinition;`(141행) 바로 앞에 넣는다.

```csharp
            RelinkCodeObjectDdl(directDefinition);
            RefreshStaticAnalysis(directDefinition, resolvedKey);

            return directDefinition;
        }

        /// <summary>
        /// 스냅샷의 의존성 항목은 코드 객체의 DDL 링크가 비어 있을 수 있다. 정작 DDL 자체는
        /// CodeObjects에 들어 있으므로 여기서 이어 붙인다. 이렇게 하지 않으면 UIF_SettleYMD
        /// 같은 함수가 프롬프트에서 "DDL 수집 실패"로 남는다.
        /// </summary>
        private void RelinkCodeObjectDdl(SpDefinition definition)
        {
            foreach (var dependency in definition.Dependencies)
            {
                if (!string.IsNullOrWhiteSpace(dependency.ReferencedDdlText)) continue;

                var codeObjectType = SqlObjectTypeClassifier.ResolveCodeObjectType(dependency.Type);
                if (codeObjectType == CodeObjectType.Unresolved) continue;

                var dependencyKey = CodeObjectKey.Create(
                    string.IsNullOrWhiteSpace(dependency.Database) ? _snapshot.Database : dependency.Database!,
                    dependency.Schema,
                    dependency.Name,
                    codeObjectType);

                if (_snapshot.CodeObjects.TryGetValue(dependencyKey.CanonicalName, out var stored) ||
                    _snapshot.CodeObjects.TryGetValue(dependencyKey.LegacyCanonicalName, out stored))
                {
                    dependency.ReferencedDdlText = stored.DdlText;
                }
            }
        }

        /// <summary>
        /// 저장된 파생 분석을 재생하지 않고 저장된 DDL에서 다시 계산한다. 스냅샷은
        /// 데이터베이스의 스냅샷이지 분석 결과의 스냅샷이 아니다. 이렇게 해야 파서를
        /// 고칠 때마다 스냅샷 재추출을 요구하지 않는다.
        ///
        /// 스냅샷에 호환성 수준이 없어 파서 기본값(160)을 쓴다. 재파싱이 실패하면
        /// 저장본을 그대로 두어 오프라인 모드가 지금보다 나빠지지 않게 한다.
        /// </summary>
        private static void RefreshStaticAnalysis(SpDefinition definition, CodeObjectKey resolvedKey)
        {
            if (!string.IsNullOrWhiteSpace(definition.DdlText))
            {
                var tableColumnsMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var dependency in definition.Dependencies)
                {
                    if (!SqlObjectTypeClassifier.IsTableOrView(dependency.Type)) continue;
                    if (dependency.Columns == null || dependency.Columns.Count == 0) continue;

                    var dependencyName = string.IsNullOrEmpty(dependency.Database)
                        ? $"{dependency.Schema}.{dependency.Name}"
                        : $"[{dependency.Database}].[{dependency.Schema}].[{dependency.Name}]";

                    var columnNames = new List<string>();
                    foreach (var column in dependency.Columns)
                    {
                        columnNames.Add(column.ColumnName);
                    }
                    tableColumnsMap[dependencyName] = columnNames;
                }

                var reparsed = new SqlStaticParser().Analyze(
                    definition.DdlText,
                    tableColumnsMap: tableColumnsMap.Count > 0 ? tableColumnsMap : null);

                if (reparsed.IsParsedSuccessfully)
                {
                    definition.StaticAnalysis = reparsed;
                }
            }

            definition.StaticAnalysis = StaticAnalysisNormalizer.Normalize(
                definition.StaticAnalysis,
                resolvedKey.Database,
                definition.Schema);
        }
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~OfflineDbMetadataServiceTests"`
기대: 전부 PASS

- [ ] **Step 5: 빌드와 전체 테스트를 확인한다**

실행: `dotnet build`
기대: 오류 0건. 경고는 기존 8건(`DbMetadataServiceTests`의 CS8600/CS8602)만 남고 새 경고가 없어야 한다

실행: `dotnet test`
기대: 실패 0건

- [ ] **Step 6: 커밋한다**

```bash
git add src/ReSet.Core/Services/OfflineDbMetadataService.cs tests/ReSet.Core.Tests/OfflineDbMetadataServiceTests.cs
git commit -m "$(cat <<'EOF'
fix: recompute offline analysis from the stored DDL

Offline mode replayed the StaticAnalysis frozen into the snapshot, so a
parser fix landed for connected runs and silently missed every offline
one. The snapshot carries the full DdlText; parse that instead.

Relink code-object DDL from CodeObjects while we are here. UIF_SettleYMD's
definition was already sitting in the snapshot — only the dependency entry
that points at it was empty, which is why the prompt reported it missing.

A failed reparse keeps the stored analysis, so offline mode never comes
out worse than before.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: `AiService` 소비자 정리

**Files:**
- Modify: `src/ReSet.Core/Services/AiService.cs:32-135`
- Test: `tests/ReSet.Core.Tests/AiServiceTests.cs`

**Interfaces:**
- Consumes: `StaticAnalysisNormalizer.CanonicalizeParts` (Task 1)
- Produces: 없음 (최종 소비자)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests.cs` 의 `AiServiceTests` 클래스 안 마지막에 추가한다. `MockHttpMessageHandler`는 같은 파일 1096행에 이미 있다.

```csharp
        private static SpDefinition SchemaFilterSpDef(
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> referencedColumns,
            string dependencyName)
        {
            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;"
            };

            spDef.Dependencies.Add(new DependencyInfo
            {
                Schema = "dbo",
                Name = dependencyName,
                Type = "USER_TABLE",
                DiscoveryDepth = 1,
                Columns = new System.Collections.Generic.List<ColumnInfo>
                {
                    new ColumnInfo { ColumnName = "CLIENTID", DataType = "varchar(20)" },
                    new ColumnInfo { ColumnName = "CYMD", DataType = "char(8)" },
                    new ColumnInfo { ColumnName = "NonSettleAmt", DataType = "money" }
                }
            });

            spDef.StaticAnalysis = new SpStaticAnalysisResult
            {
                IsParsedSuccessfully = true,
                ReferencedColumnsPerTable = referencedColumns
            };

            return spDef;
        }

        private static IAiService SpecService()
        {
            var mockResponse = "{\"choices\":[{\"message\":{\"content\":\"## 개요\"}}]}";
            var httpClient = new HttpClient(new MockHttpMessageHandler(mockResponse));
            var client = new OpenAiClient(httpClient, "test_key", "https://api.openai.com/v1", "gpt-4o");
            return new AiService(client, 0.2f);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldKeepColumnsFromEveryCanonicalMatch()
        {
            // 정규화가 한정을 못 한 경우(ObjectKey 없음 등) 키가 갈라진 채 남을 수 있다.
            // 첫 매치에서 멈추면 INSERT 전용 컬럼이 스키마 표에서 사라진다.
            var spDef = SchemaFilterSpDef(
                new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
                {
                    { "SETTLE_POQ_DB.dbo.TSettleMst", new System.Collections.Generic.List<string> { "CLIENTID" } },
                    { "TSettleMst", new System.Collections.Generic.List<string> { "CYMD", "NonSettleAmt" } }
                },
                "TSettleMst");

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("| CLIENTID |", result.UserPrompt);
            Assert.Contains("| CYMD |", result.UserPrompt);
            Assert.Contains("| NonSettleAmt |", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldNotMatchATableWhoseNameMerelyContainsTheDependency()
        {
            // dep.Name = "TSettleMst" 가 "TSettleMstBackup" 키에 부분 매칭되던 버그.
            // 백업 테이블의 참조 컬럼이 본 테이블의 필터를 통과시켜선 안 된다.
            var spDef = SchemaFilterSpDef(
                new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
                {
                    { "SETTLE_POQ_DB.dbo.TSettleMstBackup", new System.Collections.Generic.List<string> { "CYMD" } },
                    { "SETTLE_POQ_DB.dbo.TSettleMst", new System.Collections.Generic.List<string> { "CLIENTID" } }
                },
                "TSettleMst");

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("| CLIENTID |", result.UserPrompt);
            Assert.DoesNotContain("| CYMD |", result.UserPrompt);
        }

        [Fact]
        public async Task GenerateSpecificationAsync_ShouldQualifyDependencyListWithItsDatabase()
        {
            // 의존성 목록이 DB를 안 찍으면 PaymentDB.dbo.TTxMst 와 dbo.TTxMst 가
            // 프롬프트에서 구별되지 않는다. 바로 아래 스키마 블록은 3파트로 찍는다.
            var spDef = new SpDefinition
            {
                ObjectKey = CodeObjectKey.Create(
                    "SETTLE_POQ_DB", "dbo", "UP_TEST", CodeObjectType.Procedure),
                Schema = "dbo",
                Name = "UP_TEST",
                DdlText = "SELECT 1;"
            };
            spDef.Dependencies.Add(new DependencyInfo
            {
                Database = "PaymentDB",
                Schema = "dbo",
                Name = "TTxMst",
                Type = "USER_TABLE",
                DiscoveryDepth = 1
            });

            var result = await SpecService().GenerateSpecificationAsync(spDef, "지침");

            Assert.Contains("PaymentDB.dbo.TTxMst", result.UserPrompt);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~AiServiceTests"`
기대: 신규 3건 FAIL

- [ ] **Step 3: `keepCols` 매칭을 정확 비교로 바꾼다**

`src/ReSet.Core/Services/AiService.cs:47-61` 의 주석과 블록을 아래로 바꾼다.

```csharp
            // 엄격한 필터링 대상 컬럼 식별
            var keepCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) AST에서 감지한 실제 참조 컬럼 추가
            //
            // canonical 3-part로 정확 비교한다. 예전에는 dep.Name을 substring으로 찾고
            // 첫 매치에서 멈췄다. 그러면 INSERT 대상 컬럼만 담긴 키를 놓쳐 CYMD·INSTATE·
            // OUTSTATE·NonSettleAmt가 스키마 표에서 사라지고, AI가 "존재하지 않는 컬럼"
            // 이라고 적었다. 또 "TSettleMst"가 "TSettleMstBackup"에도 매칭됐다.
            //
            // break를 두지 않는 것은 폴백 대비다. 정규화가 DB 컨텍스트를 못 얻으면
            // 키가 갈라진 채 남는데, 그때도 컬럼이 유실되면 안 된다.
            if (spDef.StaticAnalysis != null && spDef.StaticAnalysis.ReferencedColumnsPerTable != null)
            {
                var depCanonicalName = StaticAnalysisNormalizer.CanonicalizeParts(
                    dep.Database,
                    dep.Schema,
                    dep.Name,
                    spDef.ObjectKey?.Database,
                    spDef.Schema);

                foreach (var kvp in spDef.StaticAnalysis.ReferencedColumnsPerTable)
                {
                    var keyCanonicalName = StaticAnalysisNormalizer.Canonicalize(
                        kvp.Key,
                        spDef.ObjectKey?.Database,
                        spDef.Schema);

                    if (!string.Equals(keyCanonicalName, depCanonicalName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var c in kvp.Value) keepCols.Add(c);
                }
            }
```

- [ ] **Step 4: 의존성 목록에 DB를 찍는다**

`src/ReSet.Core/Services/AiService.cs:128` 한 줄을 바꾼다.

```csharp
                // 바로 아래 <referenced-table-schemas>가 3파트로 찍으므로 여기서도 DB를
                // 밝힌다. 안 그러면 PaymentDB.dbo.TTxMst와 dbo.TTxMst가 같은 줄로 보인다.
                var depQualifiedName = StaticAnalysisNormalizer.CanonicalizeParts(
                    dep.Database, dep.Schema, dep.Name, spDef.ObjectKey?.Database, spDef.Schema);
                dependenciesText.AppendLine($"- Name: {depQualifiedName}, Type: {dep.Type} (발견 깊이: {dep.DiscoveryDepth}단계)");
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~AiServiceTests"`
기대: 신규 3건 PASS, 기존 AiService 테스트 전부 PASS

- [ ] **Step 6: 빌드와 전체 테스트를 확인한다**

실행: `dotnet build`
기대: 오류 0건. 경고는 기존 8건(`DbMetadataServiceTests`의 CS8600/CS8602)만 남고 새 경고가 없어야 한다

실행: `dotnet test`
기대: 실패 0건

- [ ] **Step 7: 커밋한다**

```bash
git add src/ReSet.Core/Services/AiService.cs tests/ReSet.Core.Tests/AiServiceTests.cs
git commit -m "$(cat <<'EOF'
fix: match schema-filter columns on the full table identity

The column filter looked up the dependency name with a substring match
and stopped at the first hit. For TSettleMst that hit the SELECT-side key
and never reached the key holding the INSERT target columns, so CYMD,
INSTATE, OUTSTATE and NonSettleAmt vanished from the schema table the
model was given — and the model reported them as columns that do not
exist. The same substring match would have accepted TSettleMstBackup.

Compare canonical three-part names instead, and keep scanning: if
normalization had no database context to work with, the keys stay split
and every one of them still has to contribute.

Also print the database in the dependency list. The schema block right
below it prints three-part names, so PaymentDB.dbo.TTxMst and dbo.TTxMst
were indistinguishable in the half of the prompt that named them first.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: 캐시 무효화

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs:20`
- Test: `tests/ReSet.Core.Tests/CacheManagerTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: 없음

**배경:** 복합 해시는 DDL만 본다. 원본 SP가 그대로면 고친 코드가 무의미하다. 포맷 버전 불일치는 이미 캐시 미스로 처리된다(`CacheManager.cs:90-95`).

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/CacheManagerTests.cs` 의 클래스 안 마지막에 추가한다.

```csharp
        [Fact]
        public void CacheFormatVersion_ShouldBeTwoSoPreNormalizationArtifactsAreRebuilt()
        {
            // 복합 해시는 DDL만 본다. 원본 SP가 안 바뀌었으므로 버전을 올리지 않으면
            // 정규화 이전에 만들어진 잘못된 Spec.md가 그대로 복원된다.
            var source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    RepoPaths.FindRepoRoot(), "src/ReSet.Core/Services/CacheManager.cs"));

            Assert.Contains("CurrentCacheFormatVersion = 2", source);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~CacheManagerTests"`
기대: 신규 1건 FAIL

- [ ] **Step 3: 버전을 올린다**

`src/ReSet.Core/Services/CacheManager.cs:20` 한 줄을 바꾼다.

```csharp
        // 2: 정적 분석 식별자 정규화. DDL이 안 바뀌어도 프롬프트에 들어가는 스키마 표와
        //    테이블 목록이 달라지므로, 이전 버전으로 만든 산출물은 전부 다시 만들어야 한다.
        private const int CurrentCacheFormatVersion = 2;
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

실행: `dotnet test --filter "FullyQualifiedName~CacheManagerTests"`
기대: 전부 PASS

- [ ] **Step 5: 커밋한다**

```bash
git add src/ReSet.Core/Services/CacheManager.cs tests/ReSet.Core.Tests/CacheManagerTests.cs
git commit -m "$(cat <<'EOF'
fix: bump the cache format version so stale specs are rebuilt

The composite hash covers DDL only. None of the procedures changed, so
every fixed run would hit the cache and restore the very documents this
branch exists to correct.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: 문서 동기화

**Files:**
- Modify: `docs/architecture.md:393-401` (§4.3), `docs/architecture.md:601-605` (§4.8)
- Modify: `AGENTS.md:220-232` (범주 7)
- Modify: `README.md:37`

**Interfaces:**
- Consumes: 없음
- Produces: 없음

- [ ] **Step 1: `docs/architecture.md` §4.3에 정규화 단계를 추가한다**

`### 4.3.` 절의 마지막 항목(`* **호환성 레벨 파서 다변화**...`) 바로 뒤에 두 줄을 추가한다.

```markdown
* **DML 대상 해석 (Target Resolution)**: `UpdateSpecification`/`DeleteSpecification`의 `Target`을 `InsertSpecification`과 동일한 패턴으로 선취해, 갱신·삭제 대상 테이블 하나만 `UpdateTables`/`DeleteTables`에 기록하고 FROM 절 조인 원본은 `SelectTables`로 분류합니다. 대상이 별칭인 경우(`UPDATE A SET ... FROM T A`) 전역 별칭 사전이 아니라 **그 문장 자신의 FROM 절**에서 해석합니다. 전역 사전은 마지막 등록이 이기므로 같은 별칭을 다른 문장이 다른 테이블에 쓰면 오해석됩니다. 대상을 해석할 수 없으면(테이블 변수 등) 해당 문장에 한해 문맥 내 전체 수집으로 폴백합니다.
* **식별자 정규화 (`StaticAnalysisNormalizer`)**: 파서는 SQL에 적힌 표기를 그대로 보고하므로 같은 물리 테이블이 `TSettleMst`/`dbo.TSettleMst`/`SETTLE_POQ_DB.dbo.TSettleMst` 세 갈래로 나뉩니다. 정의 조립 직후 canonical 3-part(`{Database}.{Schema}.{Name}`)로 통일하고 중복을 제거하여 `metadata.json`·스냅샷·프롬프트가 같은 이름을 쓰게 합니다. 병합은 3-part 전체 일치일 때만 수행합니다(`dbo.TPGProperty`와 `PaymentDB.dbo.TPGProperty`는 컬럼 구성이 같아도 다른 테이블입니다). 임시 테이블, 테이블 변수, 4파트 링크드 서버 이름, DB 컨텍스트가 없는 경우는 한정하지 않고 통과시킵니다.
```

- [ ] **Step 2: `docs/architecture.md` §4.8에 오프라인 재분석 규칙을 추가한다**

`### 4.8.` 절의 마지막 항목(`* **레거시 캐시 자동 마이그레이션**...`) 뒤에 두 줄을 추가한다.

```markdown
* **캐시 포맷 버전과 강제 재분석**: 복합 해시는 DDL만 보므로, 프롬프트에 주입되는 메타데이터의 형태가 바뀌면 DDL이 그대로여도 기존 산출물이 무효가 됩니다. 이 경우 `CurrentCacheFormatVersion`을 올려 전체 캐시를 미스 처리합니다(현재 값 2 — 정적 분석 식별자 정규화 도입).
* **오프라인 스냅샷은 원본만 신뢰**: `OfflineDbMetadataService`는 스냅샷에 저장된 `StaticAnalysis`를 재생하지 않고, 저장된 `DdlText`로 파서를 다시 돌린 뒤 정규화합니다. 스냅샷은 *데이터베이스*의 스냅샷이지 *분석 결과*의 스냅샷이 아니므로, 파서를 고칠 때마다 스냅샷 재추출을 요구하지 않기 위함입니다. 코드 객체의 DDL도 `CodeObjects`에서 의존성 항목으로 재링크합니다. 재파싱이 실패하면 저장본을 유지합니다. (스냅샷에 호환성 수준이 없어 오프라인 재파싱은 파서 기본값 160을 사용합니다.)
```

- [ ] **Step 3: `AGENTS.md` 범주 7의 KeepCols 항목을 갱신한다**

`* **의존 스키마 덤프 필터링**:` 으로 시작하는 항목 전체를 아래로 바꾼다.

```markdown
    *   **의존 스키마 덤프 필터링**: 테이블 상세 스키마 정보를 마크다운 테이블로 덤프할 때, AST 정적 분석이 감지한 실제 참조 컬럼(`ReferencedColumnsPerTable`), PK/FK 컬럼, 인덱스 구성 컬럼만 선별적으로 필터링 출력(KeepCols 필터링)하여 AI 프롬프트 토큰을 절약하도록 구현되어 있습니다. 이 최적화 로직의 정합성을 유지해 주십시오. **테이블 식별자 비교는 반드시 canonical 3-part(`{Database}.{Schema}.{Name}`) 정확 일치로만 수행하십시오.** 부분 문자열 매칭은 `TSettleMst`를 `TSettleMstBackup`에 걸리게 하고, 첫 매치에서 중단하면 INSERT 대상 전용 컬럼이 담긴 키를 놓쳐 실존 컬럼이 프롬프트에서 사라집니다. 실제로 그 결함이 14개 명세서에 "스키마 불일치" 허위 경고를 만들어 냈습니다.
```

- [ ] **Step 4: `README.md`의 캐싱 설명에 한 줄을 더한다**

37행 `* **해시 기반 글로벌 증분 캐싱 (Global Cache)**:` 항목 문장 끝에 이어 붙인다.

```markdown
분석 파이프라인이 프롬프트에 주입하는 메타데이터 형태를 바꾸면 DDL이 동일해도 기존 산출물이 무효가 되므로, 이때는 캐시 포맷 버전을 올려 전체를 강제 재분석합니다.
```

- [ ] **Step 5: 빌드와 전체 테스트로 기준선을 확인한다**

실행: `dotnet build`
기대: 오류 0건. 경고는 기존 8건(`DbMetadataServiceTests`의 CS8600/CS8602)만 남고 새 경고가 없어야 한다

실행: `dotnet test`
기대: 실패 0건

- [ ] **Step 6: 커밋한다**

```bash
git add docs/architecture.md AGENTS.md README.md
git commit -m "$(cat <<'EOF'
docs: record identifier normalization and offline reparse

Three reference docs described a static-analysis stage that reported table
names as written and a cache keyed on DDL alone. Both statements are now
wrong. Say what the pipeline does, and say why the identifier comparison
rule exists — the substring match it replaces is what put false schema
warnings into fourteen specifications.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## 수동 검증 (전체 태스크 완료 후)

단위 테스트로는 "14개 문서가 실제로 좋아졌는가"를 잡을 수 없다. `output/`이 추적되지 않으므로 골든 테스트를 만들지 않는다. 지금 있는 `output/offline_snapshot.json`으로 14개 SP를 재분석한 뒤 아래를 확인한다.

| 항목 | 기대 |
|---|---|
| 전 스펙의 "스키마 불일치 / 존재하지 않음" 문구 | 0건 (TSettleMst·TClient·TPGCollectPeriodMst 대상) |
| EXCEPTION_PROC·EXPECT_PROC의 CRUD 표 TSettleMst 행 | 3행 → 1행 |
| EXCEPTION_PROC `metadata.json`의 `UpdateTables` | 11개 → `SETTLE_POQ_DB.dbo.TSettleMst` 1개 |
| EXPECT_PROC `UpdateTables` | 11개(`'A'` 포함) → 1개 |
| 4PLCARD `DeleteTables` | `['A','TSettleMst','TPGProperty']` → 1개 |
| AcqManual `DeleteTables` | 2개 → 1개 |
| EXPECT_PROC의 `UIF_SettleYMD` 기술 | `definition not provided` 문구 소멸, 정산일 산출 로직 기술됨 |
| 오류코드 재현율 | 100% 유지 |

마지막 줄이 기준선이다. 이번 변경이 이미 잘 되던 것을 깨지 않았는지 본다.

## 완료 기준

- `dotnet clean && dotnet build`에서 오류 0건, 경고 정확히 8건 (기존 `DbMetadataServiceTests`의 CS8600/CS8602)
- `dotnet test`가 기존 1,040건 + 신규 40건 내외 전부 통과
- 문서 3종(`docs/architecture.md`, `AGENTS.md`, `README.md`) 동기화 완료
- 수동 검증 체크리스트 8항목 전부 충족
