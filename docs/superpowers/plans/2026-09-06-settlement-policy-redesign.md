# 정산 정책 문서 도출(메뉴 4) 재설계 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 메뉴 4를 「사람이 SP를 하나씩 고르고 DDL을 다시 긁어 미검증 문서를 뱉는 흐름」에서 「이미 쌓인 명세서를 근거로, 사람이 파일로 준 업무 순서를 목차 삼아, 기계가 만든 코드값 사전으로 구속되는 검증 가능한 인수인계 문서를 만드는 흐름」으로 바꾼다.

**Architecture:** 근거는 `output/Procedures/*/docs/Spec.md`로 한정하고 DB는 코드값 사전의 우변에만 쓴다(없으면 `의미 미상`으로 완주). 업무 순서는 사람이 고치는 `output/settlement-process.md`가 쥐며 그 H2가 그대로 문서의 목차가 된다. 단계마다 AI를 부르고 조립은 기계가 하며, PRD의 귀속 검사기를 공유 자산으로 끌어올려 정책서에는 원본이 여럿인 버전을 쓴다.

**Tech Stack:** .NET / C# 12, xUnit + NSubstitute, Microsoft.SqlServer.TransactSql.ScriptDom, Spectre.Console(TUI), Serilog

**Spec:** `docs/superpowers/specs/2026-09-06-settlement-policy-redesign-design.md`

## Global Constraints

- 게이트는 **실패 0 · 건너뜀 0 · 빌드 경고 0**이다. 통과 개수를 합격 근거로 쓰지 않는다.
- 워크트리를 새로 만들면 **AGENTS.md 워크트리 코퍼스 절의 재료 넷을 모두 심링크로 걸고 시작한다.** 안 걸어서 건너뜀이 난 일이 세 세션에서 3/3이다.
- 취소 가능한 `await`를 감싸는 광범위 `catch`에는 `when (ex is not OperationCanceledException)`을 붙인다(`CancellationPolicyTests`가 자동 검사하며, 기준선 파일 `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`의 숫자도 함께 내린다).
- SQL 객체 타입 판정은 `SqlObjectTypeClassifier`만 쓴다. `Contains("TABLE")` 류 부분 문자열 판정 금지(`TypeClassificationPolicyTests`).
- C# 보간 문자열 프롬프트 안의 `{`/`}`는 `{{`/`}}`로 이스케이프한다.
- 새 검사를 더한 태스크는 **역변이(구현을 한 줄 뒤집어 빨간불 확인 → 되돌려 초록 확인)를 실제로 돌리고, 되돌린 상태로 커밋**한다. 이것이 낡은 어셈블리를 물지 않았다는 증거를 겸한다.
- 문자열 리터럴 추출은 **정규식이 아니라 ScriptDom**으로 한다. 정규식은 이 코퍼스에서 고유 상수 82개 중 18개를 동적 SQL 조각으로 오염시켰다.
- 새 공개 타입에는 「왜 이렇게 했는가」를 담은 XML 주석을 단다. 이 저장소의 기존 서비스 클래스가 모두 그렇게 되어 있다.

---

## File Structure

**새로 만드는 것 (ReSet.Core/Services)**

| 파일 | 책임 |
| --- | --- |
| `ConstantComparisonExtractor.cs` | DDL에서 비교식의 (컬럼, 문자열 리터럴) 쌍을 ScriptDom으로 뽑는다 |
| `SettlementProcessRoster.cs` | 명부의 모델(`PolicyStage`, `SettlementProcessRoster`) |
| `SettlementProcessRosterParser.cs` | `settlement-process.md`를 모델로 읽는다 |
| `SettlementProcessRosterDraft.cs` | 명부가 없을 때 초안 마크다운을 만든다 |
| `SettlementRosterReconciler.cs` | 명부와 발견된 명세서를 대조한다(중단 조건) |
| `PolicyTargetDiscovery.cs` | `output/Procedures` 아래 명세서가 있는 대상을 찾는다 |
| `PolicyCorpusLoader.cs` | 대상 하나의 Spec/DDL/정적분석을 읽어 `PolicySource`로 만든다 |
| `SettlementCodebook.cs` | 코드값 사전의 모델 |
| `SettlementCodebookBuilder.cs` | 좌변 확정과 2단 매칭 |
| `ICodeTableProfiler.cs` / `CodeTableProfiler.cs` | 행 수가 작은 의존 테이블만 읽는다(DB 있을 때만) |
| `EvidenceQuoteMatcher.cs` | PRD에서 뽑아낸 인용 대조 공통 로직 |
| `PolicySectionContract.cs` | 정책서 표의 계약(헤더·ID 접두사·근거 셀 형식) |
| `PolicyDocumentParser.cs` | 정책서의 규칙 표를 읽는다 |
| `PolicyAttributionValidator.cs` | 원본이 여럿인 귀속 검사 |
| `PolicyDocumentChecks.cs` | 코드값 대조·SP 인용 커버리지 |
| `PolicyDocumentAssembler.cs` | 개요·단계·부록을 하나의 문서로 잇는다(기계 조립) |
| `PolicyReportBanner.cs` | 배너 셋(귀속 결함·코드값 커버리지·미인용 SP) |
| `PolicyDerivationOutcome.cs` | 서비스 결과 모델 |

**고치는 것**

| 파일 | 무엇을 |
| --- | --- |
| `PrdAttributionValidator.cs` | 인용 대조 로직을 `EvidenceQuoteMatcher`에 위임(동작 불변) |
| `SettlementPolicyService.cs` / `ISettlementPolicyService.cs` | 전면 재작성 |
| `AiService.cs` / `IAiService.cs` | `GenerateSettlementPolicyRulebookAsync` 제거, 단계·개요 두 메서드 추가 |
| `VerificationDocumentFormatter.cs` | 근거 상태 집계 오버로드 추가 |
| `SpecHeaderReader.cs` | `ReSet.Cli` → `ReSet.Core/Services`로 이동 |
| `IDbMetadataService.cs` / `DbMetadataService.cs` / `OfflineDbMetadataService.cs` | `GetTableRowCountAsync` 추가 |
| `Program.cs` | 메뉴 4 블록 교체, `--policy-sps` 제거 |
| `CliArgs.cs` | `PolicyProcedures` 제거 |
| `README.md` / `docs/architecture.md` / `AGENTS.md` | 문서 동기화 |

---

## Task 1: 상수 비교 쌍 추출기

**Files:**
- Create: `src/ReSet.Core/Services/ConstantComparisonExtractor.cs`
- Test: `tests/ReSet.Core.Tests/ConstantComparisonExtractorTests.cs`

**Interfaces:**
- Consumes: 없음 (첫 태스크)
- Produces:
  - `public sealed record ConstantComparison(string? Column, string Value)`
  - `public static IReadOnlyList<ConstantComparison> ConstantComparisonExtractor.Extract(string? ddlText)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/ConstantComparisonExtractorTests.cs`:

```csharp
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ConstantComparisonExtractorTests
    {
        private const string Ddl = @"
CREATE PROCEDURE dbo.UP_Test AS
BEGIN
    DECLARE @v_strSql NVARCHAR(MAX)
    SELECT * FROM TSettleMst WHERE PayMethod = 'impaymobile'
    UPDATE TSettleMst SET OutState = 'B' WHERE PGName IN ('payco', 'INIBANK')
    SELECT * FROM TClient WHERE MallID LIKE 'LOLLETTER4'
    SET @v_strSql = 'SELECT * FROM T WHERE C = ''' + @v_strPGName + ''''
END";

        [Fact]
        public void 등호비교의_컬럼과_값을_함께_뽑는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.Contains(pairs, p => p.Column == "PayMethod" && p.Value == "impaymobile");
        }

        [Fact]
        public void IN_목록의_각_값을_같은_컬럼에_붙여_뽑는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.Contains(pairs, p => p.Column == "PGName" && p.Value == "payco");
            Assert.Contains(pairs, p => p.Column == "PGName" && p.Value == "INIBANK");
        }

        [Fact]
        public void LIKE_패턴도_뽑는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.Contains(pairs, p => p.Column == "MallID" && p.Value == "LOLLETTER4");
        }

        [Fact]
        public void SET절의_대입값은_비교가_아니므로_뽑지_않는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.DoesNotContain(pairs, p => p.Value == "B");
        }

        // 이 테스트가 이 추출기의 존재 이유다. 정규식판은 이 코퍼스에서
        // 고유 상수 82개 중 18개를 동적 SQL 조각으로 오염시켰다.
        [Fact]
        public void 동적SQL_문자열_조립_조각은_뽑지_않는다()
        {
            var pairs = ConstantComparisonExtractor.Extract(Ddl);

            Assert.DoesNotContain(pairs, p => p.Value.Contains("SELECT"));
            Assert.DoesNotContain(pairs, p => p.Value.Trim() == "'");
            Assert.DoesNotContain(pairs, p => p.Value.Contains("@"));
        }

        [Fact]
        public void 파스에_실패해도_빈_목록을_돌려준다()
        {
            Assert.Empty(ConstantComparisonExtractor.Extract("이건 SQL이 아니다 ((("));
            Assert.Empty(ConstantComparisonExtractor.Extract(null));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~ConstantComparisonExtractorTests`
Expected: 컴파일 실패 — `ConstantComparisonExtractor` 이름을 찾을 수 없음

- [ ] **Step 3: 추출기를 구현한다**

`src/ReSet.Core/Services/ConstantComparisonExtractor.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>비교식 하나에서 뽑은 (컬럼, 문자열 리터럴) 쌍. 컬럼을 특정하지 못하면 Column은 null.</summary>
    public sealed record ConstantComparison(string? Column, string Value);

    /// <summary>
    /// DDL의 비교식에서 코드 상수를 뽑는다.
    ///
    /// [왜 정규식이 아니라 AST인가] 이 코퍼스는 동적 SQL 조립이 많아, 정규식으로
    /// `= '...'`를 찾으면 `' + @v_strClientID+ '` 같은 문자열 연결 조각이 상수로
    /// 잡힌다(고유 상수 82개 중 18개, 2026-09-06 실측). `NOT`을 컬럼명으로 잡기도
    /// 했다. 파스 트리를 보면 문자열 연결의 피연산자는 비교식의 우변이 아니므로
    /// 구조적으로 배제된다.
    ///
    /// [왜 SET 대입을 뽑지 않는가] 코드값 사전의 좌변은 「이 값과 같으면 이 분기」라는
    /// 판단 기준이다. 대입은 판단이 아니라 결과이므로 사전의 좌변이 아니다.
    /// </summary>
    public static class ConstantComparisonExtractor
    {
        public static IReadOnlyList<ConstantComparison> Extract(string? ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText))
            {
                return Array.Empty<ConstantComparison>();
            }

            TSqlFragment? fragment;
            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                fragment = parser.Parse(reader, out _);
            }
            catch (Exception)
            {
                // 파서가 던지면 재료가 없는 것이지 실행을 세울 일은 아니다 -
                // 이 저장소의 다른 추출기와 같은 소프트 페일이다.
                return Array.Empty<ConstantComparison>();
            }

            if (fragment == null)
            {
                return Array.Empty<ConstantComparison>();
            }

            var visitor = new ComparisonVisitor();
            fragment.Accept(visitor);
            return visitor.Pairs;
        }

        private sealed class ComparisonVisitor : TSqlFragmentVisitor
        {
            public List<ConstantComparison> Pairs { get; } = new();

            public override void Visit(BooleanComparisonExpression node)
            {
                Add(node.FirstExpression, node.SecondExpression);
                Add(node.SecondExpression, node.FirstExpression);
            }

            public override void Visit(InPredicate node)
            {
                var column = ColumnNameOf(node.Expression);
                foreach (var value in node.Values)
                {
                    if (value is StringLiteral literal)
                    {
                        Pairs.Add(new ConstantComparison(column, literal.Value));
                    }
                }
            }

            public override void Visit(LikePredicate node)
            {
                if (node.SecondExpression is StringLiteral literal)
                {
                    Pairs.Add(new ConstantComparison(ColumnNameOf(node.FirstExpression), literal.Value));
                }
            }

            private void Add(ScalarExpression columnSide, ScalarExpression valueSide)
            {
                if (valueSide is StringLiteral literal)
                {
                    Pairs.Add(new ConstantComparison(ColumnNameOf(columnSide), literal.Value));
                }
            }

            /// <summary>컬럼 참조이면 마지막 식별자를, 아니면 null을 준다(변수·함수·식은 컬럼이 아니다).</summary>
            private static string? ColumnNameOf(ScalarExpression? expression) =>
                expression is ColumnReferenceExpression column && column.MultiPartIdentifier?.Identifiers.Count > 0
                    ? column.MultiPartIdentifier.Identifiers[^1].Value
                    : null;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~ConstantComparisonExtractorTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 5: 재료가 실재하는지 확인한다 (앞 층 ①)**

Run: `ls output/Procedures/*/raw/metadata.json | wc -l`

Expected: `14`. 이 추출기가 볼 재료가 14편이라는 뜻이다. 0이 나오면 코퍼스 심링크를
안 건 것이므로 AGENTS.md 워크트리 코퍼스 절의 재료 넷을 걸고 다시 잰다. 실제 추출
건수는 Task 5에서 사전을 만들 때 잰다.

- [ ] **Step 6: 역변이로 효력을 확인한다**

`ColumnNameOf`가 항상 `null`을 돌려주도록 한 줄 뒤집고 테스트를 돌린다.
Expected: `등호비교의_컬럼과_값을_함께_뽑는다`·`IN_목록의_각_값을...`·`LIKE_패턴도_뽑는다` 3건 실패.
되돌리고 다시 초록을 확인한 뒤 **되돌린 상태로** 다음 단계로 간다.

- [ ] **Step 7: 커밋**

```bash
git add tests/ReSet.Core.Tests/ConstantComparisonExtractorTests.cs src/ReSet.Core/Services/ConstantComparisonExtractor.cs
git commit -m "feat: DDL 비교식의 (컬럼, 상수) 쌍을 ScriptDom으로 뽑는다

정규식은 이 코퍼스에서 고유 상수 82개 중 18개를 동적 SQL 조각으로
오염시켰다. 역변이(ColumnNameOf를 null 고정) 확인: 3건 실패 후 복구."
```

---

## Task 2: 프로세스 명부 모델과 파서

**Files:**
- Create: `src/ReSet.Core/Services/SettlementProcessRoster.cs`
- Create: `src/ReSet.Core/Services/SettlementProcessRosterParser.cs`
- Test: `tests/ReSet.Core.Tests/SettlementProcessRosterParserTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `public sealed record PolicyStage(string Title, IReadOnlyList<string> Procedures)`
  - `public sealed record SettlementProcessRoster(IReadOnlyList<PolicyStage> Stages, IReadOnlyList<string> Excluded)`
  - `public const string SettlementProcessRoster.PlaceholderMarker = "단계 이름을 붙여 주세요"`
  - `public const string SettlementProcessRoster.ExcludedHeading = "## 제외"`
  - `public static SettlementProcessRoster SettlementProcessRosterParser.Parse(string? markdown)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SettlementProcessRosterParserTests.cs`:

```csharp
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementProcessRosterParserTests
    {
        private const string Roster = @"# 정산 프로세스 명부
<!-- 도구는 이 파일이 없을 때만 초안을 만듭니다. -->

## 1. 수수료율 스냅샷 적재
<!-- [기계 확정] 5개 SP가 이 산출을 읽습니다 -->
- dbo.UP_Util_PG_Client_CMRate_Ins

## 2. 정산 원장 적재
- dbo.UP_UTIL_SETTLE_INS
- dbo.UP_UTIL_SETTLE_INS_EXTRA

## 제외
- dbo.UP_UTIL_STAT_PGCOLLECT_INS
";

        [Fact]
        public void 단계를_문서_등장_순서대로_읽는다()
        {
            var roster = SettlementProcessRosterParser.Parse(Roster);

            Assert.Equal(2, roster.Stages.Count);
            Assert.Equal("1. 수수료율 스냅샷 적재", roster.Stages[0].Title);
            Assert.Equal("2. 정산 원장 적재", roster.Stages[1].Title);
        }

        [Fact]
        public void 단계별_소속_SP를_읽는다()
        {
            var roster = SettlementProcessRosterParser.Parse(Roster);

            Assert.Equal(new[] { "dbo.UP_Util_PG_Client_CMRate_Ins" }, roster.Stages[0].Procedures);
            Assert.Equal(
                new[] { "dbo.UP_UTIL_SETTLE_INS", "dbo.UP_UTIL_SETTLE_INS_EXTRA" },
                roster.Stages[1].Procedures);
        }

        [Fact]
        public void 제외_섹션은_단계가_아니라_제외목록으로_읽는다()
        {
            var roster = SettlementProcessRosterParser.Parse(Roster);

            Assert.Equal(new[] { "dbo.UP_UTIL_STAT_PGCOLLECT_INS" }, roster.Excluded);
            Assert.DoesNotContain(roster.Stages, s => s.Title.Contains("제외"));
        }

        [Fact]
        public void 주석줄은_SP로_읽지_않는다()
        {
            var roster = SettlementProcessRosterParser.Parse(Roster);

            Assert.DoesNotContain(roster.Stages[0].Procedures, p => p.Contains("기계 확정"));
        }

        [Fact]
        public void 빈_문서는_단계도_제외도_없다()
        {
            var roster = SettlementProcessRosterParser.Parse(null);

            Assert.Empty(roster.Stages);
            Assert.Empty(roster.Excluded);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementProcessRosterParserTests`
Expected: 컴파일 실패 — `SettlementProcessRosterParser` 없음

- [ ] **Step 3: 모델을 만든다**

`src/ReSet.Core/Services/SettlementProcessRoster.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>정책서의 한 단계 - 사람이 붙인 업무 이름과 그 단계에 속한 프로시저들.</summary>
    public sealed record PolicyStage(string Title, IReadOnlyList<string> Procedures);

    /// <summary>
    /// 사람이 소유하는 정산 프로세스 명부.
    ///
    /// [왜 파일인가] 업무 순서는 코드 안에 없다 - 코퍼스 전체의 EXEC 간선은 2개뿐이고
    /// 11개 SP가 호출 그래프 밖이다(2026-09-06 실측). 쓰기→읽기 그래프는 허브 테이블
    /// TSettleMst 하나 때문에 간선 99개의 거의 완전 그래프가 되어 순서를 주지 못한다.
    /// 순서는 외부 스케줄러가 쥔 지식이므로 사람에게 받아 파일에 남긴다.
    ///
    /// [왜 H2가 목차인가] 인수인계 문서의 독자는 SP 이름을 모른다. 사람이 붙인 업무
    /// 이름이 그대로 목차가 되어야 읽힌다.
    /// </summary>
    public sealed record SettlementProcessRoster(
        IReadOnlyList<PolicyStage> Stages,
        IReadOnlyList<string> Excluded)
    {
        /// <summary>초안이 아직 사람 손을 안 탔음을 알리는 표식. 이게 남아 있으면 생성을 중단한다.</summary>
        public const string PlaceholderMarker = "단계 이름을 붙여 주세요";

        /// <summary>의도한 제외를 적는 섹션. 이 섹션이 있어야 「조용한 누락」과 「명시적 제외」가 갈린다.</summary>
        public const string ExcludedHeading = "## 제외";

        public static SettlementProcessRoster Empty { get; } =
            new(Array.Empty<PolicyStage>(), Array.Empty<string>());

        /// <summary>단계에 실린 프로시저 전량(제외 목록은 빼고).</summary>
        public IEnumerable<string> AllStagedProcedures()
        {
            foreach (var stage in Stages)
            {
                foreach (var procedure in stage.Procedures)
                {
                    yield return procedure;
                }
            }
        }
    }
}
```

- [ ] **Step 4: 파서를 만든다**

`src/ReSet.Core/Services/SettlementProcessRosterParser.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// `settlement-process.md`를 모델로 읽는다.
    ///
    /// H2가 단계, 그 아래 `- ` 목록이 소속 프로시저, 순서는 등장 순서다.
    /// HTML 주석(`&lt;!--`)은 초안이 근거를 남기는 자리이므로 항목으로 읽지 않는다.
    /// 섹션 경계 판정은 MarkdownSectionLocator의 관행(H2 접두사)을 따르되, 이 문서는
    /// 코드 펜스를 쓰지 않으므로 펜스 계산은 하지 않는다.
    /// </summary>
    public static class SettlementProcessRosterParser
    {
        public static SettlementProcessRoster Parse(string? markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return SettlementProcessRoster.Empty;
            }

            var stages = new List<PolicyStage>();
            var excluded = new List<string>();

            string? currentTitle = null;
            var currentItems = new List<string>();
            var inExcluded = false;

            void Flush()
            {
                if (inExcluded)
                {
                    excluded.AddRange(currentItems);
                }
                else if (currentTitle is not null)
                {
                    stages.Add(new PolicyStage(currentTitle, currentItems.ToList()));
                }

                currentItems.Clear();
            }

            foreach (var raw in MarkdownSectionLocator.SplitLines(markdown))
            {
                var line = raw.Trim();

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    Flush();
                    inExcluded = line.Equals(SettlementProcessRoster.ExcludedHeading, StringComparison.Ordinal);
                    currentTitle = inExcluded ? null : line[3..].Trim();
                    continue;
                }

                if (!line.StartsWith("- ", StringComparison.Ordinal))
                {
                    continue;
                }

                var item = line[2..].Trim();
                if (item.Length == 0 || item.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                currentItems.Add(item);
            }

            Flush();

            return new SettlementProcessRoster(stages, excluded);
        }
    }
}
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementProcessRosterParserTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 6: 역변이로 효력을 확인한다**

`inExcluded` 판정을 항상 `false`로 뒤집고 테스트를 돌린다.
Expected: `제외_섹션은_단계가_아니라_제외목록으로_읽는다`·`단계를_문서_등장_순서대로_읽는다` 실패.
되돌리고 초록을 확인한 뒤 **되돌린 상태로** 커밋한다.

- [ ] **Step 7: 커밋**

```bash
git add tests/ReSet.Core.Tests/SettlementProcessRosterParserTests.cs \
        src/ReSet.Core/Services/SettlementProcessRoster.cs \
        src/ReSet.Core/Services/SettlementProcessRosterParser.cs
git commit -m "feat: 정산 프로세스 명부의 모델과 파서

업무 순서는 코드 안에 없다(EXEC 간선 2개, 11개 SP가 그래프 밖).
사람이 소유하는 파일에서 단계와 소속 SP를 읽는다.
역변이(제외 판정 false 고정) 확인: 2건 실패 후 복구."
```

---

## Task 3: 명부 대조 검사 (중단 조건)

**Files:**
- Create: `src/ReSet.Core/Services/SettlementRosterReconciler.cs`
- Test: `tests/ReSet.Core.Tests/SettlementRosterReconcilerTests.cs`

**Interfaces:**
- Consumes: `SettlementProcessRoster`, `PolicyStage` (Task 2)
- Produces:
  - `public enum RosterDefectType { ProcedureMissing, ProcedureDuplicated, ProcedureUnknown, PlaceholderTitleRemaining, NoStages }`
  - `public sealed record RosterDefect(RosterDefectType Type, string Subject, string Message)`
  - `public static IReadOnlyList<RosterDefect> SettlementRosterReconciler.Reconcile(SettlementProcessRoster roster, IReadOnlyList<string> discoveredLabels)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SettlementRosterReconcilerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementRosterReconcilerTests
    {
        private static readonly string[] Discovered =
        {
            "dbo.UP_UTIL_SETTLE_INS",
            "dbo.UP_UTIL_SETTLE_INS_EXTRA",
            "dbo.UP_UTIL_STAT_PGCOLLECT_INS",
        };

        private static SettlementProcessRoster Roster(
            IReadOnlyList<string> staged, IReadOnlyList<string>? excluded = null, string title = "1. 정산 원장 적재") =>
            new(new[] { new PolicyStage(title, staged) }, excluded ?? Array.Empty<string>());

        [Fact]
        public void 전량이_한_번씩_실려_있으면_결함이_없다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0], Discovered[1] }, new[] { Discovered[2] }), Discovered);

            Assert.Empty(defects);
        }

        [Fact]
        public void 명부에서_빠진_SP를_고발한다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0] }, new[] { Discovered[2] }), Discovered);

            var defect = Assert.Single(defects);
            Assert.Equal(RosterDefectType.ProcedureMissing, defect.Type);
            Assert.Equal("dbo.UP_UTIL_SETTLE_INS_EXTRA", defect.Subject);
        }

        [Fact]
        public void 두_단계에_중복으로_실린_SP를_고발한다()
        {
            var roster = new SettlementProcessRoster(
                new[]
                {
                    new PolicyStage("1. 가", new[] { Discovered[0] }),
                    new PolicyStage("2. 나", new[] { Discovered[0], Discovered[1] }),
                },
                new[] { Discovered[2] });

            var defects = SettlementRosterReconciler.Reconcile(roster, Discovered);

            var defect = Assert.Single(defects);
            Assert.Equal(RosterDefectType.ProcedureDuplicated, defect.Type);
            Assert.Equal(Discovered[0], defect.Subject);
        }

        [Fact]
        public void 명세서가_없는_이름을_고발한다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0], Discovered[1], "dbo.UP_오타" }, new[] { Discovered[2] }),
                Discovered);

            var defect = Assert.Single(defects);
            Assert.Equal(RosterDefectType.ProcedureUnknown, defect.Type);
            Assert.Equal("dbo.UP_오타", defect.Subject);
        }

        [Fact]
        public void 초안_자리표시자가_남아_있으면_고발한다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0], Discovered[1] }, new[] { Discovered[2] },
                       title: "2. " + SettlementProcessRoster.PlaceholderMarker + " ①"),
                Discovered);

            Assert.Contains(defects, d => d.Type == RosterDefectType.PlaceholderTitleRemaining);
        }

        [Fact]
        public void 단계가_하나도_없으면_고발한다()
        {
            var defects = SettlementRosterReconciler.Reconcile(SettlementProcessRoster.Empty, Discovered);

            Assert.Contains(defects, d => d.Type == RosterDefectType.NoStages);
        }

        [Fact]
        public void 제외에_적힌_SP는_누락이_아니다()
        {
            var defects = SettlementRosterReconciler.Reconcile(
                Roster(new[] { Discovered[0], Discovered[1] }, new[] { Discovered[2] }), Discovered);

            Assert.DoesNotContain(defects, d => d.Type == RosterDefectType.ProcedureMissing);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementRosterReconcilerTests`
Expected: 컴파일 실패 — `SettlementRosterReconciler` 없음

- [ ] **Step 3: 대조기를 구현한다**

`src/ReSet.Core/Services/SettlementRosterReconciler.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    public enum RosterDefectType
    {
        ProcedureMissing,
        ProcedureDuplicated,
        ProcedureUnknown,
        PlaceholderTitleRemaining,
        NoStages,
    }

    public sealed record RosterDefect(RosterDefectType Type, string Subject, string Message);

    /// <summary>
    /// 명부와 실제 명세서 목록을 대조한다.
    ///
    /// [왜 배너가 아니라 중단인가] 명부에서 빠진 SP의 규칙은 문서에서 아무 흔적 없이
    /// 사라지고, 읽는 사람이 그 사실을 알 방법이 없다. 조용한 결함은 이 저장소가 반복해
    /// 물린 자리다. 대신 `## 제외` 섹션이 의도한 제외를 명시적으로 허용하므로, 사람이
    /// 일부러 빼는 길은 열려 있고 조용히 빠지는 길만 막힌다.
    ///
    /// [왜 자리표시자도 중단인가] 무인 배치가 도구 초안대로의 순서로 인수인계 문서를
    /// 배송하는 것이 이 기능에서 가장 나쁜 결말이다.
    /// </summary>
    public static class SettlementRosterReconciler
    {
        public static IReadOnlyList<RosterDefect> Reconcile(
            SettlementProcessRoster roster,
            IReadOnlyList<string> discoveredLabels)
        {
            var defects = new List<RosterDefect>();

            if (roster.Stages.Count == 0)
            {
                defects.Add(new RosterDefect(
                    RosterDefectType.NoStages,
                    string.Empty,
                    "명부에 단계가 하나도 없습니다. output/settlement-process.md를 채우십시오."));
            }

            foreach (var stage in roster.Stages)
            {
                if (stage.Title.Contains(SettlementProcessRoster.PlaceholderMarker, StringComparison.Ordinal))
                {
                    defects.Add(new RosterDefect(
                        RosterDefectType.PlaceholderTitleRemaining,
                        stage.Title,
                        $"단계 '{stage.Title}'이 초안 자리표시자 그대로입니다. 업무 이름을 붙이십시오 - 이 제목이 정책서의 목차가 됩니다."));
                }
            }

            var staged = roster.AllStagedProcedures().ToList();
            var discovered = new HashSet<string>(discoveredLabels, StringComparer.OrdinalIgnoreCase);
            var accountedFor = new HashSet<string>(staged.Concat(roster.Excluded), StringComparer.OrdinalIgnoreCase);

            foreach (var label in discoveredLabels.Where(l => !accountedFor.Contains(l)))
            {
                defects.Add(new RosterDefect(
                    RosterDefectType.ProcedureMissing,
                    label,
                    $"명세서가 있는 '{label}'이 명부에 없습니다. 어느 단계에 넣거나 '## 제외'에 적으십시오."));
            }

            foreach (var duplicate in staged
                         .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                defects.Add(new RosterDefect(
                    RosterDefectType.ProcedureDuplicated,
                    duplicate.Key,
                    $"'{duplicate.Key}'이 명부에 {duplicate.Count()}번 실려 있습니다. 한 번만 실으십시오."));
            }

            foreach (var unknown in staged.Concat(roster.Excluded)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(p => !discovered.Contains(p)))
            {
                defects.Add(new RosterDefect(
                    RosterDefectType.ProcedureUnknown,
                    unknown,
                    $"명부의 '{unknown}'에 해당하는 명세서가 없습니다. 이름을 확인하십시오."));
            }

            return defects;
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementRosterReconcilerTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 5: 역변이로 효력을 확인한다 (뒤 층 ⑥)**

`ProcedureMissing`을 내는 `foreach` 블록을 통째로 주석 처리하고 테스트를 돌린다.
Expected: `명부에서_빠진_SP를_고발한다` 실패.
되돌려 초록을 확인한다. 나머지 셋(`Duplicated`/`Unknown`/`Placeholder`)에 대해서도 같은 방식으로 하나씩 지워 각각 정확히 대응하는 테스트만 실패하는 것을 확인한 뒤 **전부 되돌린 상태로** 커밋한다.

- [ ] **Step 6: 커밋**

```bash
git add tests/ReSet.Core.Tests/SettlementRosterReconcilerTests.cs \
        src/ReSet.Core/Services/SettlementRosterReconciler.cs
git commit -m "feat: 명부와 명세서 목록을 대조해 조용한 누락을 막는다

누락·중복·오타·자리표시자·단계 0을 발화한다. 배너가 아니라 중단이다 -
빠진 SP의 규칙은 문서에서 흔적 없이 사라지고 독자가 알 방법이 없다.
역변이 네 번(각 발화 블록 제거) 확인: 각각 대응 테스트만 실패 후 복구."
```

---

## Task 4: 대상 탐색과 코퍼스 적재, 명부 초안 생성

**Files:**
- Create: `src/ReSet.Core/Services/PolicyTargetDiscovery.cs`
- Create: `src/ReSet.Core/Services/PolicyCorpusLoader.cs`
- Create: `src/ReSet.Core/Services/SettlementProcessRosterDraft.cs`
- Test: `tests/ReSet.Core.Tests/PolicyTargetDiscoveryTests.cs`
- Test: `tests/ReSet.Core.Tests/SettlementProcessRosterDraftTests.cs`

**Interfaces:**
- Consumes: `SettlementProcessRoster.PlaceholderMarker` (Task 2)
- Produces:
  - `public sealed record PolicyTarget(string Label, string DocsDirectory, string MetadataPath)`
  - `public static IReadOnlyList<PolicyTarget> PolicyTargetDiscovery.Find(string outputRoot)`
  - `public sealed record PolicySource(string Label, string SpecMarkdown, string DdlText, SpStaticAnalysisResult Analysis, IReadOnlyList<DependencyInfo> Dependencies)`
  - `public static PolicySource? PolicyCorpusLoader.Load(PolicyTarget target)`
  - `public static string SettlementProcessRosterDraft.Build(IReadOnlyList<PolicySource> sources)`

- [ ] **Step 1: 대상 탐색 테스트를 쓴다**

`tests/ReSet.Core.Tests/PolicyTargetDiscoveryTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyTargetDiscoveryTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "reset-policy-" + Guid.NewGuid().ToString("N"));

        private void Seed(string label, bool withSpec, bool withMetadata)
        {
            var docs = Path.Combine(_root, "Procedures", label, "docs");
            var raw = Path.Combine(_root, "Procedures", label, "raw");
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(raw);
            if (withSpec) File.WriteAllText(Path.Combine(docs, "Spec.md"), "## 개요\n\n본문\n");
            if (withMetadata) File.WriteAllText(Path.Combine(raw, "metadata.json"), "{}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void 명세서가_있는_대상만_찾는다()
        {
            Seed("dbo.A", withSpec: true, withMetadata: true);
            Seed("dbo.B", withSpec: false, withMetadata: true);

            var targets = PolicyTargetDiscovery.Find(_root);

            Assert.Equal(new[] { "dbo.A" }, targets.Select(t => t.Label));
        }

        [Fact]
        public void 라벨_사전순으로_돌려준다()
        {
            Seed("dbo.Z", withSpec: true, withMetadata: true);
            Seed("dbo.A", withSpec: true, withMetadata: true);

            var targets = PolicyTargetDiscovery.Find(_root);

            Assert.Equal(new[] { "dbo.A", "dbo.Z" }, targets.Select(t => t.Label));
        }

        [Fact]
        public void Procedures_디렉터리가_없으면_빈_목록이다()
        {
            Directory.CreateDirectory(_root);

            Assert.Empty(PolicyTargetDiscovery.Find(_root));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PolicyTargetDiscoveryTests`
Expected: 컴파일 실패 — `PolicyTargetDiscovery` 없음

- [ ] **Step 3: 대상 탐색기를 만든다**

`src/ReSet.Core/Services/PolicyTargetDiscovery.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>정책서의 근거가 될 수 있는 대상 하나 - 명세서와 메타데이터가 함께 있는 객체.</summary>
    public sealed record PolicyTarget(string Label, string DocsDirectory, string MetadataPath);

    /// <summary>
    /// output/Procedures 아래에서 명세서가 있는 객체를 찾는다.
    ///
    /// [왜 DB에 붙지 않는가] 이 기능의 근거는 Spec.md 하나이고 상수 추출 보조로 쓰는
    /// DDL 사본도 raw/metadata.json에 이미 있다. 파일시스템만 읽으므로 이미 쌓인
    /// 산출물에 재분석 없이 소급 적용된다 - PrdTargetDiscovery와 같은 판단이다.
    ///
    /// [Functions·External을 뺀 이유] PrdTargetDiscovery와 같다. 넓힐 때는 여기 한 곳만
    /// 고치면 된다.
    /// </summary>
    public static class PolicyTargetDiscovery
    {
        public static IReadOnlyList<PolicyTarget> Find(string outputRoot)
        {
            var proceduresRoot = Path.Combine(outputRoot, "Procedures");
            if (!Directory.Exists(proceduresRoot))
            {
                return Array.Empty<PolicyTarget>();
            }

            var targets = new List<PolicyTarget>();
            foreach (var objectDir in Directory.EnumerateDirectories(proceduresRoot))
            {
                var docs = Path.Combine(objectDir, "docs");
                if (!File.Exists(Path.Combine(docs, OutputPathResolver.SpecFileNamePublic)))
                {
                    continue;
                }

                targets.Add(new PolicyTarget(
                    Path.GetFileName(objectDir),
                    docs,
                    Path.Combine(objectDir, "raw", "metadata.json")));
            }

            return targets.OrderBy(t => t.Label, StringComparer.Ordinal).ToList();
        }
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PolicyTargetDiscoveryTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 5: 코퍼스 적재기를 만든다**

`src/ReSet.Core/Services/PolicyCorpusLoader.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>정책 도출이 대상 하나에 대해 필요로 하는 재료 전부.</summary>
    public sealed record PolicySource(
        string Label,
        string SpecMarkdown,
        string DdlText,
        SpStaticAnalysisResult Analysis,
        IReadOnlyList<DependencyInfo> Dependencies);

    /// <summary>
    /// 대상 하나의 Spec.md와 raw/metadata.json을 읽어 재료로 만든다.
    ///
    /// [BOM 주의] 이 저장소의 metadata.json은 UTF-8 BOM으로 저장된다. BOM을 벗기지
    /// 않으면 System.Text.Json이 첫 글자에서 던진다.
    ///
    /// [소프트 페일] 한 대상의 metadata.json이 깨져도 나머지 대상의 정책 도출을
    /// 세우지 않는다. 그 대상은 DDL 없이(상수 좌변 없이) 명세서만으로 들어간다.
    /// </summary>
    public static class PolicyCorpusLoader
    {
        public static PolicySource? Load(PolicyTarget target)
        {
            var specPath = Path.Combine(target.DocsDirectory, OutputPathResolver.SpecFileNamePublic);
            if (!File.Exists(specPath))
            {
                return null;
            }

            var spec = File.ReadAllText(specPath);
            var ddl = string.Empty;
            var analysis = new SpStaticAnalysisResult();
            IReadOnlyList<DependencyInfo> dependencies = Array.Empty<DependencyInfo>();

            if (File.Exists(target.MetadataPath))
            {
                try
                {
                    // BOM은 여기서 벗긴다. 파일 바이트를 그대로 넘기면 던진다.
                    var json = File.ReadAllText(target.MetadataPath).TrimStart('﻿');
                    var definition = JsonSerializer.Deserialize<SpDefinition>(
                        json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (definition is not null)
                    {
                        ddl = definition.DdlText ?? string.Empty;
                        analysis = definition.StaticAnalysis ?? new SpStaticAnalysisResult();
                        dependencies = definition.Dependencies ?? new List<DependencyInfo>();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "정책 도출 재료 적재 실패 - {Label}의 metadata.json을 읽지 못해 명세서만으로 진행합니다.",
                        target.Label);
                }
            }

            return new PolicySource(target.Label, spec, ddl, analysis, dependencies);
        }
    }
}
```

**확인된 모델 이름** (`src/ReSet.Core/Models/`): `SpDefinition.DdlText`(string), `SpDefinition.StaticAnalysis`(`SpStaticAnalysisResult`), `SpDefinition.Dependencies`(`List<DependencyInfo>`). `DependencyInfo`는 `Database`(string?)·`Schema`·`Name`·`Type`·`Columns`를 갖는다.

- [ ] **Step 6: 초안 생성기 테스트를 쓴다**

`tests/ReSet.Core.Tests/SettlementProcessRosterDraftTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementProcessRosterDraftTests
    {
        private static PolicySource Source(
            string label, string ddl, IEnumerable<string>? writes = null, IEnumerable<string>? reads = null) =>
            new(label, "## 개요\n\n본문\n", ddl,
                new SpStaticAnalysisResult
                {
                    InsertTables = new List<string>(writes ?? Array.Empty<string>()),
                    SelectTables = new List<string>(reads ?? Array.Empty<string>()),
                },
                Array.Empty<DependencyInfo>());

        [Fact]
        public void 산출을_남들이_읽는_SP를_맨_앞_단계에_놓는다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.RATE_INS", "CREATE PROCEDURE dbo.RATE_INS AS BEGIN SELECT 1 END",
                       writes: new[] { "dbo.TClientSettleRate" }),
                Source("dbo.SETTLE_INS", "CREATE PROCEDURE dbo.SETTLE_INS AS BEGIN SELECT 1 END",
                       reads: new[] { "dbo.TClientSettleRate" }),
            });

            var ratePos = draft.IndexOf("dbo.RATE_INS", StringComparison.Ordinal);
            var settlePos = draft.IndexOf("dbo.SETTLE_INS", StringComparison.Ordinal);

            Assert.True(ratePos >= 0 && settlePos >= 0);
            Assert.True(ratePos < settlePos, "산출을 남이 읽는 SP가 앞 단계에 놓여야 한다");
        }

        [Fact]
        public void 순서를_모르는_SP들은_자리표시자_단계에_모은다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.A", "CREATE PROCEDURE dbo.A AS BEGIN SELECT 1 END"),
                Source("dbo.B", "CREATE PROCEDURE dbo.B AS BEGIN SELECT 1 END"),
            });

            Assert.Contains(SettlementProcessRoster.PlaceholderMarker, draft);
        }

        [Fact]
        public void EXEC로_부르는_관계를_한_단계로_묶는다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.SUMMARY",
                    "CREATE PROCEDURE dbo.SUMMARY AS BEGIN EXEC dbo.SUMMARY_EXTRA END"),
                Source("dbo.SUMMARY_EXTRA",
                    "CREATE PROCEDURE dbo.SUMMARY_EXTRA AS BEGIN SELECT 1 END"),
            });

            var roster = SettlementProcessRosterParser.Parse(draft);
            var stage = Assert.Single(roster.Stages, s => s.Procedures.Contains("dbo.SUMMARY"));
            Assert.Contains("dbo.SUMMARY_EXTRA", stage.Procedures);
        }

        [Fact]
        public void 만든_초안은_다시_파싱된다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.A", "CREATE PROCEDURE dbo.A AS BEGIN SELECT 1 END"),
            });

            var roster = SettlementProcessRosterParser.Parse(draft);

            Assert.Contains(roster.AllStagedProcedures(), p => p == "dbo.A");
        }

        [Fact]
        public void 제외_섹션을_빈_채로_함께_낸다()
        {
            var draft = SettlementProcessRosterDraft.Build(new[]
            {
                Source("dbo.A", "CREATE PROCEDURE dbo.A AS BEGIN SELECT 1 END"),
            });

            Assert.Contains(SettlementProcessRoster.ExcludedHeading, draft);
            Assert.Empty(SettlementProcessRosterParser.Parse(draft).Excluded);
        }
    }
}
```

- [ ] **Step 7: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementProcessRosterDraftTests`
Expected: 컴파일 실패 — `SettlementProcessRosterDraft` 없음

- [ ] **Step 8: 초안 생성기를 만든다**

`src/ReSet.Core/Services/SettlementProcessRosterDraft.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 명부가 없을 때 초안 마크다운을 만든다.
    ///
    /// [초안이 말해도 되는 것과 안 되는 것] 2026-09-06 실측 - 코퍼스 전체의 EXEC 간선은
    /// 2개뿐이고, 쓰기→읽기 그래프는 허브 테이블(3개 이상이 쓰는 테이블) 때문에 간선
    /// 99개의 거의 완전 그래프가 된다. 허브를 빼면 5개만 남고 전부 출발점이 요율
    /// 스냅샷 적재 SP다. 그래서 초안이 확정할 수 있는 것은 「산출을 남이 읽는 SP가
    /// 앞」과 「EXEC로 묶인 무리」뿐이고, 나머지의 상호 순서는 모른다.
    ///
    /// 모르는 것을 지어내지 않는다. 자리표시자 단계에 모아 두고 사람에게 넘기며,
    /// 그 자리표시자가 남아 있는 한 SettlementRosterReconciler가 생성을 중단시킨다.
    /// </summary>
    public static class SettlementProcessRosterDraft
    {
        /// <summary>3개 이상이 쓰는 테이블은 허브로 보고 순서 판정에서 뺀다.</summary>
        private const int HubWriterThreshold = 3;

        public static string Build(IReadOnlyList<PolicySource> sources)
        {
            var writes = sources.ToDictionary(s => s.Label, s => WriteSet(s), StringComparer.OrdinalIgnoreCase);
            var reads = sources.ToDictionary(s => s.Label, s => ReadSet(s), StringComparer.OrdinalIgnoreCase);

            var hubs = writes.Values
                .SelectMany(set => set)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= HubWriterThreshold)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 1. 허브를 뺀 산출을 남이 읽는 SP - 앞선다고 말할 수 있다.
            var producers = sources
                .Where(s => sources.Any(other =>
                    !string.Equals(other.Label, s.Label, StringComparison.OrdinalIgnoreCase)
                    && writes[s.Label].Except(hubs, StringComparer.OrdinalIgnoreCase)
                        .Intersect(reads[other.Label], StringComparer.OrdinalIgnoreCase).Any()))
                .Select(s => s.Label)
                .ToList();

            // 2. EXEC로 묶인 무리 - 한 단계에 함께 놓는다고 말할 수 있다.
            var callers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                var callees = ExecTargets(source.DdlText)
                    .Where(c => sources.Any(s => s.Label.EndsWith(c, StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(s.Label, c, StringComparison.OrdinalIgnoreCase)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (callees.Count > 0)
                {
                    callers[source.Label] = callees;
                }
            }

            var execGroups = new List<List<string>>();
            foreach (var (caller, callees) in callers)
            {
                var group = new List<string> { caller };
                group.AddRange(sources
                    .Select(s => s.Label)
                    .Where(label => callees.Any(c =>
                        label.EndsWith(c, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(label, c, StringComparison.OrdinalIgnoreCase))));
                execGroups.Add(group.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }

            var placed = new HashSet<string>(producers, StringComparer.OrdinalIgnoreCase);
            foreach (var label in execGroups.SelectMany(g => g))
            {
                placed.Add(label);
            }

            var unknown = sources.Select(s => s.Label).Where(l => !placed.Contains(l)).ToList();

            return Render(producers, execGroups, unknown);
        }

        private static string Render(
            IReadOnlyList<string> producers,
            IReadOnlyList<List<string>> execGroups,
            IReadOnlyList<string> unknown)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 정산 프로세스 명부");
            sb.AppendLine("<!-- 도구는 이 파일이 없을 때만 초안을 만들고, 있으면 읽기만 합니다. -->");
            sb.AppendLine("<!-- 단계 순서 = 등장 순서. H2 제목이 그대로 정책서의 목차가 됩니다. -->");
            sb.AppendLine();

            var number = 1;

            if (producers.Count > 0)
            {
                sb.AppendLine($"## {number++}. {SettlementProcessRoster.PlaceholderMarker} (선행 적재)");
                sb.AppendLine("<!-- [기계 확정] 아래 SP의 산출을 다른 SP가 읽습니다. 앞선다고 말할 수 있습니다. -->");
                foreach (var label in producers)
                {
                    sb.AppendLine($"- {label}");
                }

                sb.AppendLine();
            }

            if (unknown.Count > 0)
            {
                sb.AppendLine($"## {number++}. {SettlementProcessRoster.PlaceholderMarker} (순서 미상)");
                sb.AppendLine("<!-- [순서 미상] 기계가 상호 순서를 판별하지 못했습니다.");
                sb.AppendLine("     허브 테이블을 함께 쓰고 읽어 쓰기-읽기 관계로는 갈리지 않습니다.");
                sb.AppendLine("     실제 배치 실행 순서를 아는 분이 단계로 나눠 주십시오. -->");
                foreach (var label in unknown)
                {
                    sb.AppendLine($"- {label}");
                }

                sb.AppendLine();
            }

            foreach (var group in execGroups)
            {
                sb.AppendLine($"## {number++}. {SettlementProcessRoster.PlaceholderMarker} (호출 무리)");
                sb.AppendLine("<!-- [기계 확정] 첫 SP가 나머지를 EXEC 합니다. 한 단계로 묶을 수 있습니다. -->");
                foreach (var label in group)
                {
                    sb.AppendLine($"- {label}");
                }

                sb.AppendLine();
            }

            sb.AppendLine(SettlementProcessRoster.ExcludedHeading);
            sb.AppendLine("<!-- 정산 정책서에서 빼고 싶은 SP를 여기 적으십시오. 비워 두어도 됩니다. -->");

            return sb.ToString();
        }

        private static HashSet<string> WriteSet(PolicySource source) =>
            source.Analysis.InsertTables
                .Concat(source.Analysis.UpdateTables)
                .Concat(source.Analysis.DeleteTables)
                .Select(Normalize)
                .Where(t => t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> ReadSet(PolicySource source) =>
            source.Analysis.SelectTables
                .Select(Normalize)
                .Where(t => t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>스키마·DB 접두와 대괄호를 벗겨 테이블 이름만 남긴다.</summary>
        private static string Normalize(string raw) =>
            (raw ?? string.Empty).Split('.').Last().Trim('[', ']').Trim();

        /// <summary>EXEC로 부르는 프로시저 이름을 AST로 뽑는다(동적 SQL 문자열은 걸리지 않는다).</summary>
        private static IReadOnlyList<string> ExecTargets(string ddlText)
        {
            if (string.IsNullOrWhiteSpace(ddlText))
            {
                return Array.Empty<string>();
            }

            try
            {
                var parser = new TSql160Parser(true);
                using var reader = new StringReader(ddlText);
                var fragment = parser.Parse(reader, out _);
                if (fragment == null)
                {
                    return Array.Empty<string>();
                }

                var visitor = new ExecVisitor();
                fragment.Accept(visitor);
                return visitor.Targets;
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private sealed class ExecVisitor : TSqlFragmentVisitor
        {
            public List<string> Targets { get; } = new();

            public override void Visit(ExecutableProcedureReference node)
            {
                var identifiers = node.ProcedureReference?.ProcedureReference?.Name?.Identifiers;
                if (identifiers is { Count: > 0 })
                {
                    Targets.Add(string.Join(".", identifiers.Select(i => i.Value)));
                }
            }
        }
    }
}
```

- [ ] **Step 9: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementProcessRosterDraftTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 10: 실물 코퍼스로 초안을 눈으로 확인한다 (앞 층 ②)**

임시 xUnit 테스트를 하나 만들어 `output/Procedures`를 실제로 읽고 초안을 콘솔에 찍는다.

```csharp
[Fact(Skip = "수동 확인용 - 초안 육안 검토")]
public void 실물_코퍼스_초안을_찍는다()
{
    var targets = PolicyTargetDiscovery.Find("output");
    var sources = targets.Select(PolicyCorpusLoader.Load).Where(s => s is not null).Select(s => s!).ToList();
    System.Console.WriteLine(SettlementProcessRosterDraft.Build(sources));
}
```

Run: `dotnet test tests/ReSet.Core.Tests --filter 실물_코퍼스_초안을_찍는다`

기대: 선행 적재 단계에 `dbo.UP_Util_PG_Client_CMRate_Ins`가 들어가고, 호출 무리 단계에
`dbo.UP_Util_Settle_Summary`와 그것이 EXEC 하는 둘이 함께 들어가며, 나머지 11개가 순서
미상 단계에 모인다. 확인 후 **이 임시 테스트는 지우고** 커밋한다(`Skip` 테스트를 남기면
게이트의 「건너뜀 0」이 깨진다).

- [ ] **Step 11: 역변이로 효력을 확인한다**

`hubs`를 항상 빈 집합으로 만들고 실물 초안을 다시 찍는다.
Expected: 선행 적재 단계에 거의 모든 SP가 들어가 초안이 무의미해진다(허브 제외가
실제로 일하고 있다는 증거). 되돌리고 **되돌린 상태로** 커밋한다.

- [ ] **Step 12: 커밋**

```bash
git add tests/ReSet.Core.Tests/PolicyTargetDiscoveryTests.cs \
        tests/ReSet.Core.Tests/SettlementProcessRosterDraftTests.cs \
        src/ReSet.Core/Services/PolicyTargetDiscovery.cs \
        src/ReSet.Core/Services/PolicyCorpusLoader.cs \
        src/ReSet.Core/Services/SettlementProcessRosterDraft.cs
git commit -m "feat: 정책 도출 대상 탐색과 명부 초안 생성

파일시스템만 읽는다. 초안은 아는 것(산출을 남이 읽는 SP, EXEC 무리)과
모르는 것(허브에 묻힌 나머지)을 갈라 적고 모르는 것은 지어내지 않는다.
역변이(허브 제외 해제) 확인: 초안이 무의미해짐."
```

---

## Task 5: 코드값 사전 — 좌변 확정 (오프라인)

**Files:**
- Create: `src/ReSet.Core/Services/SettlementCodebook.cs`
- Create: `src/ReSet.Core/Services/SettlementCodebookBuilder.cs`
- Test: `tests/ReSet.Core.Tests/SettlementCodebookBuilderTests.cs`

**Interfaces:**
- Consumes: `ConstantComparison`, `ConstantComparisonExtractor.Extract` (Task 1), `PolicySource` (Task 4)
- Produces:
  - `public sealed record CodebookMatch(string Table, IReadOnlyDictionary<string, string> Row)`
  - `public sealed record CodebookEntry(string Value, string? Column, IReadOnlyList<string> Procedures, bool MatchEligible, IReadOnlyList<CodebookMatch> Matches)`
  - `public sealed record SettlementCodebook(IReadOnlyList<CodebookEntry> Entries, IReadOnlyList<string> SpecUnlistedConstants)`
  - `public const int SettlementCodebookBuilder.MinimumMatchableLength = 3`
  - `public static SettlementCodebook SettlementCodebookBuilder.BuildLeftSide(IReadOnlyList<PolicySource> sources)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/SettlementCodebookBuilderTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementCodebookBuilderTests
    {
        private static PolicySource Source(string label, string ddl, string spec) =>
            new(label, spec, ddl, new SpStaticAnalysisResult(), Array.Empty<DependencyInfo>());

        private const string Ddl = @"
CREATE PROCEDURE dbo.UP_Test AS
BEGIN
    SELECT * FROM T WHERE PayMethod = 'impaymobile'
    SELECT * FROM T WHERE UseFlag = 'Y'
    SELECT * FROM T WHERE PGName = 'onlyInDdl'
END";

        private const string Spec = @"## 개요

결제수단이 'impaymobile'인 건을 대상으로 한다. 사용 여부는 'Y'로 판정한다.
";

        [Fact]
        public void 명세서에_등장하는_상수만_채택한다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.Contains(book.Entries, e => e.Value == "impaymobile");
            Assert.DoesNotContain(book.Entries, e => e.Value == "onlyInDdl");
        }

        [Fact]
        public void 명세서에_없는_상수는_버리지_않고_별도로_기록한다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.Contains("onlyInDdl", book.SpecUnlistedConstants);
        }

        [Fact]
        public void 좌변_컬럼을_함께_담는다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.Equal("PayMethod", Assert.Single(book.Entries, e => e.Value == "impaymobile").Column);
        }

        // 이 조건이 없으면 'Y'가 아무 테이블에서나 걸려 매칭이 잡음이 된다.
        // 실측: 실질 상수 64개 중 13개가 길이 2 이하.
        [Fact]
        public void 길이_2_이하는_매칭_대상에서_뺀다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.False(Assert.Single(book.Entries, e => e.Value == "Y").MatchEligible);
            Assert.True(Assert.Single(book.Entries, e => e.Value == "impaymobile").MatchEligible);
        }

        [Fact]
        public void 여러_SP에_나오는_상수는_한_항목에_출처를_모은다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[]
            {
                Source("dbo.A", Ddl, Spec),
                Source("dbo.B", Ddl, Spec),
            });

            var entry = Assert.Single(book.Entries, e => e.Value == "impaymobile");
            Assert.Equal(new[] { "dbo.A", "dbo.B" }, entry.Procedures);
        }

        [Fact]
        public void 좌변만_만든_사전은_매칭이_비어_있다()
        {
            var book = SettlementCodebookBuilder.BuildLeftSide(new[] { Source("dbo.A", Ddl, Spec) });

            Assert.All(book.Entries, e => Assert.Empty(e.Matches));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementCodebookBuilderTests`
Expected: 컴파일 실패 — `SettlementCodebookBuilder` 없음

- [ ] **Step 3: 모델을 만든다**

`src/ReSet.Core/Services/SettlementCodebook.cs`:

```csharp
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    /// <summary>코드 상수 하나가 마스터 데이터에서 발견된 자리. 행 전체를 담는다 - 설명 컬럼을 추정하지 않는다.</summary>
    public sealed record CodebookMatch(string Table, IReadOnlyDictionary<string, string> Row);

    /// <summary>사전의 한 항목. Matches가 비면 「의미 미상」으로 문서에 나간다.</summary>
    public sealed record CodebookEntry(
        string Value,
        string? Column,
        IReadOnlyList<string> Procedures,
        bool MatchEligible,
        IReadOnlyList<CodebookMatch> Matches);

    /// <summary>
    /// 정책서가 실을 수 있는 코드값의 전부.
    ///
    /// [왜 사전이 문서를 구속하는가] 정책서에는 이 사전에 있는 번역만 실린다. 그러면
    /// 완성된 문서의 매핑을 사전과 대조해 지어낸 번역을 잡을 수 있고, 검사가 보는 파일
    /// (정책서)과 기준이 되는 파일(사전)이 달라 오라클이 순환하지 않는다.
    /// </summary>
    public sealed record SettlementCodebook(
        IReadOnlyList<CodebookEntry> Entries,
        IReadOnlyList<string> SpecUnlistedConstants);
}
```

- [ ] **Step 4: 좌변 확정기를 만든다**

`src/ReSet.Core/Services/SettlementCodebookBuilder.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코드값 사전을 만든다. 좌변(코드 상수)은 오프라인, 우변(업무 의미)은 DB가 있을 때만.
    ///
    /// [왜 채택을 명세서가 정하는가] 추출은 DDL로 해야 정확하지만(동적 SQL 조각을 걸러야
    /// 하므로 ScriptDom), 채택은 Spec.md가 정해야 「근거는 명세서뿐」이라는 계약이 선다.
    /// 둘을 나눠 두면 정확도와 계약을 함께 가진다. 실측 채택률은 (SP, 상수) 출현 기준
    /// 114/115다.
    /// </summary>
    public static class SettlementCodebookBuilder
    {
        /// <summary>
        /// 값만으로 매칭할 때 요구하는 최소 길이.
        ///
        /// 'Y'·'N'·'1' 같은 값은 아무 테이블에서나 걸려 매칭이 잡음이 된다(실질 상수
        /// 64개 중 13개가 길이 2 이하, 2026-09-06 실측). 이런 플래그의 의미는 코드
        /// 테이블이 아니라 컬럼 이름과 명세서 서술에 있고, 그건 AI가 Spec 인용으로
        /// 이미 다룬다.
        /// </summary>
        public const int MinimumMatchableLength = 3;

        public static SettlementCodebook BuildLeftSide(IReadOnlyList<PolicySource> sources)
        {
            // 값 → (컬럼 후보, 그 값을 쓰는 SP들)
            var adopted = new Dictionary<string, (string? Column, List<string> Procedures)>(StringComparer.Ordinal);
            var unlisted = new List<string>();

            foreach (var source in sources)
            {
                foreach (var pair in ConstantComparisonExtractor.Extract(source.DdlText))
                {
                    if (string.IsNullOrWhiteSpace(pair.Value))
                    {
                        continue;
                    }

                    if (!source.SpecMarkdown.Contains(pair.Value, StringComparison.Ordinal))
                    {
                        if (!unlisted.Contains(pair.Value, StringComparer.Ordinal))
                        {
                            unlisted.Add(pair.Value);
                        }

                        continue;
                    }

                    if (!adopted.TryGetValue(pair.Value, out var existing))
                    {
                        adopted[pair.Value] = (pair.Column, new List<string> { source.Label });
                        continue;
                    }

                    // 컬럼은 먼저 잡힌 것을 지킨다. 같은 값이 여러 컬럼과 비교되면
                    // 어느 하나를 고를 근거가 없고, 매칭은 컬럼이 없어도 2단으로 돈다.
                    if (!existing.Procedures.Contains(source.Label, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.Procedures.Add(source.Label);
                    }

                    adopted[pair.Value] = (existing.Column ?? pair.Column, existing.Procedures);
                }
            }

            var entries = adopted
                .Select(kv => new CodebookEntry(
                    kv.Key,
                    kv.Value.Column,
                    kv.Value.Procedures,
                    kv.Key.Length >= MinimumMatchableLength,
                    Array.Empty<CodebookMatch>()))
                .OrderBy(e => e.Value, StringComparer.Ordinal)
                .ToList();

            return new SettlementCodebook(entries, unlisted);
        }
    }
}
```

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementCodebookBuilderTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 6: 실물 코퍼스에서 좌변 규모를 잰다 (앞 층 ①)**

임시 테스트로 실물 코퍼스의 좌변을 세어 콘솔에 찍는다.

```csharp
[Fact(Skip = "수동 확인용 - 좌변 규모 실측")]
public void 실물_코퍼스_좌변을_잰다()
{
    var sources = PolicyTargetDiscovery.Find("output")
        .Select(PolicyCorpusLoader.Load).Where(s => s is not null).Select(s => s!).ToList();
    var book = SettlementCodebookBuilder.BuildLeftSide(sources);
    System.Console.WriteLine($"채택 {book.Entries.Count} · 매칭대상 {book.Entries.Count(e => e.MatchEligible)} · 명세서미수록 {book.SpecUnlistedConstants.Count}");
}
```

Run: `dotnet test tests/ReSet.Core.Tests --filter 실물_코퍼스_좌변을_잰다`

기대: 채택이 수십 건대이고 매칭 대상이 그보다 10건 남짓 적다. **정규식판이 냈던 동적 SQL
조각(`' + @v_str...`, `'+'`, `','`)이 하나도 없어야 한다** — 있으면 Task 1의 AST 추출기가
제 일을 못 한 것이므로 여기서 멈추고 Task 1로 돌아간다. 확인 후 임시 테스트는 지운다.

- [ ] **Step 7: 역변이로 효력을 확인한다**

명세서 등장 여부 검사(`source.SpecMarkdown.Contains`)를 항상 `true`로 뒤집고 테스트를 돌린다.
Expected: `명세서에_등장하는_상수만_채택한다`·`명세서에_없는_상수는_버리지_않고...` 2건 실패.
되돌려 초록을 확인하고 **되돌린 상태로** 커밋한다.

- [ ] **Step 8: 커밋**

```bash
git add tests/ReSet.Core.Tests/SettlementCodebookBuilderTests.cs \
        src/ReSet.Core/Services/SettlementCodebook.cs \
        src/ReSet.Core/Services/SettlementCodebookBuilder.cs
git commit -m "feat: 코드값 사전의 좌변을 명세서로 채택한다

추출은 DDL(AST)로 정확하게, 채택은 Spec.md가 결정한다. 길이 2 이하는
매칭 대상에서 뺀다 - 'Y'는 아무 테이블에서나 걸려 잡음이 된다.
역변이(명세서 등장 검사 true 고정) 확인: 2건 실패 후 복구."
```

---

## Task 6: 코드값 사전 — 우변 프로파일링과 2단 매칭

**Files:**
- Create: `src/ReSet.Core/Services/ICodeTableProfiler.cs`
- Create: `src/ReSet.Core/Services/CodeTableProfiler.cs`
- Modify: `src/ReSet.Core/Services/SettlementCodebookBuilder.cs` (`ApplyMatches` 추가)
- Modify: `src/ReSet.Core/Services/IDbMetadataService.cs`
- Modify: `src/ReSet.Core/Services/DbMetadataService.cs`
- Modify: `src/ReSet.Core/Services/OfflineDbMetadataService.cs`
- Test: `tests/ReSet.Core.Tests/SettlementCodebookMatchingTests.cs`

**Interfaces:**
- Consumes: `SettlementCodebook`, `CodebookEntry`, `CodebookMatch`, `SettlementCodebookBuilder.MinimumMatchableLength` (Task 5); `PolicySource.Dependencies` (Task 4)
- Produces:
  - `public sealed record ProfiledTable(string Table, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows)`
  - `public interface ICodeTableProfiler { Task<IReadOnlyList<ProfiledTable>> ProfileAsync(IReadOnlyList<DependencyInfo> dependencies, CancellationToken cancellationToken = default); }`
  - `public static SettlementCodebook SettlementCodebookBuilder.ApplyMatches(SettlementCodebook leftSide, IReadOnlyList<ProfiledTable> tables)`
  - `Task<int> IDbMetadataService.GetTableRowCountAsync(string connectionString, string? database, string schema, string tableName, CancellationToken cancellationToken = default)`

- [ ] **Step 1: 매칭 테스트를 쓴다**

`tests/ReSet.Core.Tests/SettlementCodebookMatchingTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementCodebookMatchingTests
    {
        private static SettlementCodebook LeftSide(params CodebookEntry[] entries) =>
            new(entries, Array.Empty<string>());

        private static CodebookEntry Entry(string value, string? column, bool eligible = true) =>
            new(value, column, new[] { "dbo.A" }, eligible, Array.Empty<CodebookMatch>());

        private static ProfiledTable Table(string name, params (string Col, string Val)[][] rows) =>
            new(name, rows
                .Select(r => (IReadOnlyDictionary<string, string>)r.ToDictionary(c => c.Col, c => c.Val))
                .ToList());

        [Fact]
        public void 컬럼을_아는_값은_같은_이름의_컬럼에서_찾는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("impaymobile", "PayMethod")),
                new[]
                {
                    Table("dbo.TCode",
                        new[] { ("PayMethod", "impaymobile"), ("Name", "간편결제") }),
                });

            var match = Assert.Single(Assert.Single(book.Entries).Matches);
            Assert.Equal("dbo.TCode", match.Table);
            Assert.Equal("간편결제", match.Row["Name"]);
        }

        [Fact]
        public void 컬럼을_모르는_값은_아무_문자열_칸에서나_찾는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("payco", column: null)),
                new[] { Table("dbo.TPg", new[] { ("PgCode", "payco"), ("PgName", "페이코") }) });

            Assert.Single(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 매칭_대상이_아닌_값은_아예_찾지_않는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("Y", "UseFlag", eligible: false)),
                new[] { Table("dbo.TAny", new[] { ("UseFlag", "Y"), ("Name", "사용") }) });

            Assert.Empty(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 대소문자를_무시하고_찾는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("NICECARD", column: null)),
                new[] { Table("dbo.TPg", new[] { ("PgCode", "nicecard") }) });

            Assert.Single(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 여러_테이블에서_나오면_전부_담고_출처를_남긴다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("payco", column: null)),
                new[]
                {
                    Table("dbo.TPg", new[] { ("PgCode", "payco") }),
                    Table("dbo.TMall", new[] { ("PgCode", "payco") }),
                });

            var matches = Assert.Single(book.Entries).Matches;
            Assert.Equal(2, matches.Count);
            Assert.Contains(matches, m => m.Table == "dbo.TPg");
            Assert.Contains(matches, m => m.Table == "dbo.TMall");
        }

        [Fact]
        public void 프로파일링_결과가_없으면_전량_미매칭으로_남는다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("impaymobile", "PayMethod")), Array.Empty<ProfiledTable>());

            Assert.Empty(Assert.Single(book.Entries).Matches);
        }

        [Fact]
        public void 부분_문자열은_매칭이_아니다()
        {
            var book = SettlementCodebookBuilder.ApplyMatches(
                LeftSide(Entry("payco", column: null)),
                new[] { Table("dbo.TPg", new[] { ("PgCode", "payco_extra") }) });

            Assert.Empty(Assert.Single(book.Entries).Matches);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementCodebookMatchingTests`
Expected: 컴파일 실패 — `ProfiledTable`·`ApplyMatches` 없음

- [ ] **Step 3: 프로파일러 계약과 모델을 만든다**

`src/ReSet.Core/Services/ICodeTableProfiler.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <summary>읽어 온 테이블 하나. 값은 전부 문자열로 정규화해 담는다(매칭이 문자열 대조이므로).</summary>
    public sealed record ProfiledTable(
        string Table,
        IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

    /// <summary>
    /// 코드값의 우변을 채울 마스터 데이터를 읽는다.
    ///
    /// [왜 인터페이스인가] 정책 도출의 기본 경로는 DB 없이 완주한다. 프로파일러를
    /// 선택 의존으로 두면 서비스와 검사 전체가 DB 없이 테스트된다 - 종전
    /// SettlementPolicyService가 IDbMetadataService를 필수로 물어 사실상 테스트가
    /// 없었던 것이 이 분리의 이유다.
    /// </summary>
    public interface ICodeTableProfiler
    {
        Task<IReadOnlyList<ProfiledTable>> ProfileAsync(
            IReadOnlyList<DependencyInfo> dependencies,
            CancellationToken cancellationToken = default);
    }
}
```

- [ ] **Step 4: 2단 매칭을 구현한다**

`src/ReSet.Core/Services/SettlementCodebookBuilder.cs`에 추가:

```csharp
        /// <summary>
        /// 좌변만 있는 사전에 프로파일링 결과를 붙인다. 2단이다.
        ///
        /// 1단 - 컬럼을 아는 값은 같은 이름의 컬럼에서만 찾는다(정밀).
        /// 2단 - 컬럼을 모르는 값은 아무 문자열 칸에서나 찾되, 길이 조건을 통과한
        ///        값만 시도한다(MatchEligible).
        ///
        /// 실측으로 컬럼까지 잡히는 비율은 값 29개 / 82개다. 즉 2단이 다수 경로이고,
        /// 길이 조건이 실질적인 잡음 차단선이다.
        ///
        /// 부분 문자열은 매칭이 아니다 - 'payco'가 'payco_extra'에 걸리면 사전이
        /// 거짓 번역을 문서에 허가하게 된다.
        /// </summary>
        public static SettlementCodebook ApplyMatches(
            SettlementCodebook leftSide,
            IReadOnlyList<ProfiledTable> tables)
        {
            var entries = leftSide.Entries.Select(entry =>
            {
                if (!entry.MatchEligible)
                {
                    return entry;
                }

                var matches = new List<CodebookMatch>();

                foreach (var table in tables)
                {
                    foreach (var row in table.Rows)
                    {
                        var hit = entry.Column is null
                            ? row.Values.Any(v => Equals(v, entry.Value))
                            : row.TryGetValue(entry.Column, out var cell) && Equals(cell, entry.Value);

                        if (hit)
                        {
                            matches.Add(new CodebookMatch(table.Table, row));
                            break; // 한 테이블에서 첫 행이면 충분하다 - 코드 테이블은 값이 유일하다.
                        }
                    }
                }

                return entry with { Matches = matches };
            }).ToList();

            return leftSide with { Entries = entries };
        }

        private static bool Equals(string? cell, string value) =>
            cell is not null && string.Equals(cell, value, StringComparison.OrdinalIgnoreCase);
```

**주의:** `entry.Column`으로 `row.TryGetValue`를 부를 때 딕셔너리가
`StringComparer.OrdinalIgnoreCase`로 만들어져야 컬럼 이름 대소문자가 달라도 걸린다.
`CodeTableProfiler`가 행 딕셔너리를 그 비교자로 만든다(Step 5).

- [ ] **Step 5: DB 프로파일러와 행 수 조회를 만든다**

먼저 `IDbMetadataService`에 추가:

```csharp
        /// <summary>테이블의 행 수. 코드 테이블은 작다는 성질로 프로파일링 대상을 고르기 위한 것이다.</summary>
        Task<int> GetTableRowCountAsync(string connectionString, string? database, string schema, string tableName, CancellationToken cancellationToken = default);
```

`DbMetadataService`에 구현(`GetTableDataPreviewAsync` 바로 아래, 같은 이스케이프 관행을 따른다):

```csharp
        public async Task<int> GetTableRowCountAsync(
            string connectionString, string? database, string schema, string tableName,
            CancellationToken cancellationToken = default)
        {
            var cleanDb = string.IsNullOrEmpty(database) ? "" : $"[{database.Replace("]", "]]")}].";
            var escapedSchema = $"[{schema.Replace("]", "]]")}]";
            var escapedTable = $"[{tableName.Replace("]", "]]")}]";

            var query = $"SELECT COUNT_BIG(1) FROM {cleanDb}{escapedSchema}.{escapedTable};";

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);
            using var cmd = new SqlCommand(query, conn);
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);

            // int 범위를 넘는 큰 테이블은 어차피 임계 초과라 상한으로 잘라도 판정이 같다.
            return scalar is long count ? (int)Math.Min(count, int.MaxValue) : 0;
        }
```

`OfflineDbMetadataService`에 구현:

```csharp
        public Task<int> GetTableRowCountAsync(
            string connectionString, string? database, string schema, string tableName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "GetTableRowCountAsync is not supported in offline mode because table data is not cached in the snapshot.");
```

`src/ReSet.Core/Services/CodeTableProfiler.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 의존 테이블 중 행 수가 작은 것만 읽는다.
    ///
    /// [왜 이름으로 고르지 않는가] 종전 구현은 이름에 Code|Master|Policy|Setting|Map|
    /// Type|Group|Rate가 든 테이블을 골랐는데, 이 코퍼스에서 그 조건에 걸리는 9개가
    /// 전부 요율표였다. 'S02'를 '정산보류'로 번역하겠다는 이 기능의 간판이 0건을
    /// 겨누고 있었다는 뜻이다. 코드·마스터 테이블은 「작다」는 성질로 판정하는 편이
    /// 이름 규칙이 다른 조직에서도 선다.
    ///
    /// [부수 효과] 거래 테이블이 크기에서 걸러지므로 실거래 100행을 무조건 긁어
    /// 프롬프트에 싣던 일이 사라진다.
    /// </summary>
    public sealed class CodeTableProfiler : ICodeTableProfiler
    {
        private readonly IDbMetadataService _dbService;
        private readonly string _connectionString;
        private readonly int _rowThreshold;

        public CodeTableProfiler(IDbMetadataService dbService, string connectionString, int rowThreshold = 500)
        {
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _connectionString = connectionString;
            _rowThreshold = rowThreshold;
        }

        public async Task<IReadOnlyList<ProfiledTable>> ProfileAsync(
            IReadOnlyList<DependencyInfo> dependencies,
            CancellationToken cancellationToken = default)
        {
            var results = new List<ProfiledTable>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dependency in dependencies.Where(d => SqlObjectTypeClassifier.IsTableOrView(d.Type)))
            {
                var key = string.IsNullOrEmpty(dependency.Database)
                    ? $"{dependency.Schema}.{dependency.Name}"
                    : $"[{dependency.Database}].[{dependency.Schema}].[{dependency.Name}]";

                if (!seen.Add(key))
                {
                    continue;
                }

                try
                {
                    var rowCount = await _dbService.GetTableRowCountAsync(
                        _connectionString, dependency.Database, dependency.Schema, dependency.Name, cancellationToken);

                    if (rowCount > _rowThreshold)
                    {
                        continue;
                    }

                    var rows = await _dbService.GetTableDataPreviewAsync(
                        _connectionString, dependency.Database, dependency.Schema, dependency.Name,
                        _rowThreshold, cancellationToken);

                    results.Add(new ProfiledTable(key, rows.Select(ToStringRow).ToList()));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 테이블 하나를 못 읽는다고 정책 도출을 세우지 않는다. 그 값은
                    // 「의미 미상」으로 문서에 나가고, 그 사실이 배너에 실린다.
                    Log.Warning(ex, "코드 테이블 프로파일링 실패 - {Table}", key);
                }
            }

            return results;
        }

        /// <summary>매칭은 문자열 대조이므로 모든 칸을 문화권 불변 문자열로 정규화한다.</summary>
        private static IReadOnlyDictionary<string, string> ToStringRow(Dictionary<string, object> row)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, value) in row)
            {
                normalized[column] = value switch
                {
                    null or DBNull => string.Empty,
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? string.Empty,
                };
            }

            return normalized;
        }
    }
}
```

- [ ] **Step 6: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementCodebookMatchingTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 7: 전체 테스트로 인터페이스 추가의 파급을 확인한다**

Run: `dotnet build && dotnet test tests/ReSet.Core.Tests`
Expected: 실패 0 · 건너뜀 0 · 빌드 경고 0.
`IDbMetadataService`에 메서드를 더했으므로 그것을 구현하는 모든 클래스와 테스트
대역(`NSubstitute` 대역은 자동으로 따라온다)이 컴파일되는지 여기서 확인한다.

- [ ] **Step 8: 역변이로 효력을 확인한다**

`ApplyMatches`의 `if (!entry.MatchEligible) return entry;`를 지우고 테스트를 돌린다.
Expected: `매칭_대상이_아닌_값은_아예_찾지_않는다` 실패.
이어서 `Equals`의 `string.Equals`를 `cell.Contains(value, ...)`로 바꾸고 돌린다.
Expected: `부분_문자열은_매칭이_아니다` 실패.
둘 다 되돌려 초록을 확인하고 **되돌린 상태로** 커밋한다.

- [ ] **Step 9: 커밋**

```bash
git add tests/ReSet.Core.Tests/SettlementCodebookMatchingTests.cs \
        src/ReSet.Core/Services/ICodeTableProfiler.cs \
        src/ReSet.Core/Services/CodeTableProfiler.cs \
        src/ReSet.Core/Services/SettlementCodebookBuilder.cs \
        src/ReSet.Core/Services/IDbMetadataService.cs \
        src/ReSet.Core/Services/DbMetadataService.cs \
        src/ReSet.Core/Services/OfflineDbMetadataService.cs
git commit -m "feat: 코드값 사전의 우변을 크기로 고른 테이블에서 기계 매칭한다

이름 휴리스틱을 버린다 - 그 조건에 걸리는 9개가 전부 요율표라
코드값 번역이 0건을 겨누고 있었다. 대신 행 수가 작은 것만 읽는다.
매칭은 2단(컬럼 쌍 → 값+길이조건)이고 부분 문자열은 매칭이 아니다.
역변이 둘(길이조건 제거, 정확일치→부분일치) 확인: 각 1건 실패 후 복구."
```

---

## Task 7: 인용 대조 공통화와 정책서 문서 계약

**Files:**
- Create: `src/ReSet.Core/Services/EvidenceQuoteMatcher.cs`
- Create: `src/ReSet.Core/Services/PolicySectionContract.cs`
- Create: `src/ReSet.Core/Services/PolicyDocumentParser.cs`
- Create: `src/ReSet.Core/Services/PolicyAttributionValidator.cs`
- Modify: `src/ReSet.Core/Services/PrdAttributionValidator.cs` (공통 로직 위임, 동작 불변)
- Test: `tests/ReSet.Core.Tests/PolicyDocumentParserTests.cs`
- Test: `tests/ReSet.Core.Tests/PolicyAttributionValidatorTests.cs`

**Interfaces:**
- Consumes: 없음(PRD 기존 코드에서 로직을 옮겨 온다)
- Produces:
  - `public static string EvidenceQuoteMatcher.NormalizeForQuoteMatch(string text)`
  - `public static string EvidenceQuoteMatcher.NormalizeHeading(string heading)`
  - `public static string? EvidenceQuoteMatcher.ExtractSectionBody(IReadOnlyList<string> sourceLines, string heading)`
  - `public static bool EvidenceQuoteMatcher.QuoteExistsIn(string sectionBody, string quote)`
  - `public const string PolicySectionContract.TableHeader = "| ID | 업무 규칙 | 근거 | 코드값 |"`
  - `public static string PolicySectionContract.IdPrefixFor(int stageNumber)` → `"S{n}"`
  - `public static bool PolicySectionContract.TryParseEvidence(string? raw, out PolicyEvidenceReference reference)`
  - `public sealed record PolicyEvidenceReference(string Label, string Heading, string Quote)`
  - `public sealed record PolicyRule(string StageHeading, int StageNumber, string Id, string Text, string EvidenceRaw, string CodeValue, int LineNumber)`
  - `public static IReadOnlyList<PolicyRule> PolicyDocumentParser.Parse(string? policyMarkdown, IReadOnlyList<string> stageHeadings)`
  - `public enum PolicyDefectType { StageMissing, StageOutOfOrder, IdPrefixMismatch, EvidenceMissing, EvidenceLabelUnknown, EvidenceHeadingNotFound, EvidenceQuoteNotFound, CodeValueNotInCodebook, ProcedureNeverCited }`
  - `public sealed record PolicyDefect(PolicyDefectType Type, string Subject, string RuleId, string Message)`
  - `public sealed class PolicyValidationResult { IReadOnlyList<PolicyDefect> Defects; bool IsValid; }`
  - `public static PolicyValidationResult PolicyAttributionValidator.Validate(string? policyMarkdown, IReadOnlyList<string> stageHeadings, IReadOnlyDictionary<string, string> specsByLabel)`

- [ ] **Step 1: 공통 인용 대조기를 뽑아낸다 (동작 불변 리팩터)**

`src/ReSet.Core/Services/EvidenceQuoteMatcher.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 「인용문이 원본의 그 헤딩 절 안에 축자로 있는가」를 재는 공통 로직.
    ///
    /// [왜 뽑아냈는가] PRD와 정책서가 같은 질문을 한다. 다른 것은 원본이 하나냐
    /// 여럿이냐뿐이다. 두 벌로 두면 한쪽의 정규화 규칙만 고쳐져 같은 인용이 한 문서에서는
    /// 통과하고 다른 문서에서는 결함이 되는 날이 온다.
    ///
    /// 이 클래스는 PrdAttributionValidator의 private 메서드를 그대로 옮긴 것이며
    /// 동작을 바꾸지 않는다. PRD 쪽 회귀는 기존 PRD 테스트가 지킨다.
    /// </summary>
    public static class EvidenceQuoteMatcher
    {
        /// <summary>인용 대조용 정규화. 공백과 마크다운 강조·표 파이프를 걷어낸다.</summary>
        public static string NormalizeForQuoteMatch(string text) =>
            string.Concat(text.Where(ch => !char.IsWhiteSpace(ch)
                                           && ch != '*' && ch != '`' && ch != '|'
                                           && ch != '_' && ch != '~'));

        /// <summary>마크다운 헤딩에서 부호를 정규화한다. 앞의 #, 공백, 접두 숫자·번호, 뒤의 구두점을 제거한다.</summary>
        public static string NormalizeHeading(string heading)
        {
            var text = heading.TrimStart('#').Trim();

            var match = Regex.Match(text, @"^\d+[.\)\s-]+(.*)$");
            if (match.Success)
            {
                text = match.Groups[1].Value.Trim();
            }

            return text.TrimEnd(':').Trim();
        }

        /// <summary>지정 헤딩 아래 본문만 이어 붙인다. 헤딩이 없으면 null. 정확 일치 뒤 부분 일치로 폴백한다.</summary>
        public static string? ExtractSectionBody(IReadOnlyList<string> sourceLines, string heading)
        {
            var exact = MarkdownSectionLocator.LocateSection(sourceLines, heading, "## ");
            var (headerIndex, endIndex) = exact.HeaderIndex >= 0
                ? exact
                : MarkdownSectionLocator.LocateSection(
                    sourceLines, "## " + NormalizeHeading(heading), "## ", exact: false);

            if (headerIndex < 0)
            {
                return null;
            }

            return string.Join("\n", sourceLines.Skip(headerIndex + 1).Take(endIndex - headerIndex - 1));
        }

        /// <summary>절 본문 안에 인용이 축자로 있는가(정규화 후 부분 문자열 대조).</summary>
        public static bool QuoteExistsIn(string sectionBody, string quote) =>
            NormalizeForQuoteMatch(sectionBody)
                .Contains(NormalizeForQuoteMatch(quote), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: PRD 검증기를 공통 로직에 위임시킨다**

`PrdAttributionValidator.cs`에서 `NormalizeForQuoteMatch`·`NormalizeHeading`·`ExtractSectionBody`
세 private 메서드를 **지우고**, 호출부를 `EvidenceQuoteMatcher.*`로 바꾼다. 인용 대조부는
다음으로 바꾼다:

```csharp
                if (!EvidenceQuoteMatcher.QuoteExistsIn(body, evidence.Quote))
```

`TryParseEvidence`와 `PrdEvidenceReference`는 PRD 계약(`## 헤딩 > "구절"`)이라 그대로 둔다 —
정책서의 근거 셀은 SP 식별자가 앞에 붙는 다른 문법이다.

- [ ] **Step 3: PRD 회귀가 그대로인지 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~Prd`
Expected: 실패 0 · 건너뜀 0. **한 건이라도 달라지면 리팩터가 동작을 바꾼 것이므로 멈추고 되돌린다.**

- [ ] **Step 4: 커밋 (리팩터만 따로)**

```bash
git add src/ReSet.Core/Services/EvidenceQuoteMatcher.cs src/ReSet.Core/Services/PrdAttributionValidator.cs
git commit -m "refactor: 인용 대조 로직을 EvidenceQuoteMatcher로 뽑는다

PRD와 정책서가 같은 질문을 한다. 두 벌로 두면 한쪽의 정규화만 고쳐져
같은 인용이 문서마다 다르게 판정되는 날이 온다. 동작은 불변이고
PRD 테스트 전량이 그대로 통과한다."
```

- [ ] **Step 5: 파서 테스트를 쓴다**

`tests/ReSet.Core.Tests/PolicyDocumentParserTests.cs`:

```csharp
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyDocumentParserTests
    {
        private static readonly string[] Stages = { "## 1. 수수료율 스냅샷 적재", "## 2. 정산 원장 적재" };

        private const string Policy = @"## 정산 업무 개요

전체 조망.

## 1. 수수료율 스냅샷 적재

요율을 하루 한 번 적재한다.

| ID | 업무 규칙 | 근거 | 코드값 |
| :--- | :--- | :--- | :--- |
| S1-01 | 요율을 당일자로 적재한다 | dbo.UP_RATE · ## 개요 > ""요율을 적재"" | - |

## 2. 정산 원장 적재

원장을 적재한다.

| ID | 업무 규칙 | 근거 | 코드값 |
| :--- | :--- | :--- | :--- |
| S2-01 | 간편결제 건을 대상으로 한다 | dbo.UP_INS · ## 개요 > ""결제수단이 impaymobile"" | impaymobile |

## 부록 A. 코드값 사전

| 코드값 | 의미 |
| :--- | :--- |
| impaymobile | 간편결제 |
";

        [Fact]
        public void 단계별_규칙을_읽는다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.Equal(2, rules.Count);
            Assert.Equal("S1-01", rules[0].Id);
            Assert.Equal("S2-01", rules[1].Id);
        }

        [Fact]
        public void 단계_번호를_함께_담는다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.Equal(1, rules[0].StageNumber);
            Assert.Equal(2, rules[1].StageNumber);
        }

        [Fact]
        public void 코드값_칸을_읽는다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.Equal("-", rules[0].CodeValue);
            Assert.Equal("impaymobile", rules[1].CodeValue);
        }

        [Fact]
        public void 부록의_표는_규칙으로_읽지_않는다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.DoesNotContain(rules, r => r.StageHeading.Contains("부록"));
        }

        [Fact]
        public void 표_머리와_구분선은_규칙이_아니다()
        {
            var rules = PolicyDocumentParser.Parse(Policy, Stages);

            Assert.DoesNotContain(rules, r => r.Id == "ID");
            Assert.DoesNotContain(rules, r => r.Id.All(c => c == ':' || c == '-'));
        }
    }
}
```

- [ ] **Step 6: 계약과 파서를 만든다**

`src/ReSet.Core/Services/PolicySectionContract.cs`:

```csharp
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>정책서 근거 칸을 셋으로 가른 것 - 어느 명세서의, 어느 헤딩의, 어느 구절인가.</summary>
    public sealed record PolicyEvidenceReference(string Label, string Heading, string Quote);

    /// <summary>
    /// 정책서 규칙 표의 계약.
    ///
    /// [왜 계약을 클래스가 소유하는가] 생성 프롬프트와 검증기가 같은 표를 읽어야 둘이
    /// 갈라지지 않는다. PrdSectionContract가 이미 같은 자리를 지키고 있고, 거기 달린
    /// 주석이 하드코딩하면 조용히 갈라진다고 경고한다.
    ///
    /// [PRD와 다른 점] 정책서는 근거가 여러 명세서에 흩어져 있어 근거 칸이
    /// `dbo.UP_X · ## 헤딩 > "구절"` 로 SP 식별자를 앞에 단다.
    /// </summary>
    public static class PolicySectionContract
    {
        public const string TableHeader = "| ID | 업무 규칙 | 근거 | 코드값 |";
        public const string TableSeparator = "| :--- | :--- | :--- | :--- |";
        public const int ExpectedCellCount = 4;

        /// <summary>근거 칸에서 SP 식별자와 나머지를 가르는 구분자.</summary>
        public const string LabelSeparator = " · ";

        /// <summary>코드값이 없을 때 쓰는 표기. 빈 칸이 아니라 이 값이어야 「빠뜨림」과 「해당 없음」이 갈린다.</summary>
        public const string NoCodeValue = "-";

        public static string IdPrefixFor(int stageNumber) =>
            "S" + stageNumber.ToString(CultureInfo.InvariantCulture);

        /// <summary>`## 1. 제목` 에서 1을 뽑는다. 번호가 없으면 0.</summary>
        public static int StageNumberOf(string heading)
        {
            var match = Regex.Match(heading.TrimStart('#').Trim(), @"^(\d+)[.\)\s-]");
            return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
        }

        /// <summary>`dbo.UP_X · ## 헤딩 > "구절"` 을 가른다.</summary>
        public static bool TryParseEvidence(string? raw, out PolicyEvidenceReference reference)
        {
            reference = new PolicyEvidenceReference(string.Empty, string.Empty, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var separator = raw.IndexOf(LabelSeparator, StringComparison.Ordinal);
            if (separator < 0)
            {
                return false;
            }

            var label = raw[..separator].Trim();
            var rest = raw[(separator + LabelSeparator.Length)..].Trim();

            var arrow = rest.IndexOf('>');
            if (arrow < 0)
            {
                return false;
            }

            var heading = rest[..arrow].Trim();
            var quoted = rest[(arrow + 1)..].Trim();

            var first = quoted.IndexOfAny(new[] { '"', '“', '”' });
            var last = quoted.LastIndexOfAny(new[] { '"', '“', '”' });
            if (first < 0 || last <= first)
            {
                return false;
            }

            var quote = quoted[(first + 1)..last].Trim();
            if (label.Length == 0 || heading.Length == 0 || quote.Length == 0)
            {
                return false;
            }

            reference = new PolicyEvidenceReference(label, heading, quote);
            return true;
        }
    }
}
```

`src/ReSet.Core/Services/PolicyDocumentParser.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>정책서 규칙 표의 한 행.</summary>
    public sealed record PolicyRule(
        string StageHeading,
        int StageNumber,
        string Id,
        string Text,
        string EvidenceRaw,
        string CodeValue,
        int LineNumber);

    /// <summary>
    /// 정책서의 규칙 표를 읽는다.
    ///
    /// 섹션 경계와 코드 펜스 판정은 MarkdownSectionLocator에, 파이프 분해는
    /// MarkdownTableCellCodec에 맡긴다 - PrdDocumentParser 주석이 지적한 대로,
    /// 파이프 분해를 손수 다시 구현하는 자리를 늘리지 않는다.
    ///
    /// [부록을 읽지 않는 이유] 부록은 기계가 만든 표이지 AI가 쓴 규칙이 아니다.
    /// 검사 대상은 AI가 쓴 것뿐이어야 한다.
    /// </summary>
    public static class PolicyDocumentParser
    {
        public static IReadOnlyList<PolicyRule> Parse(
            string? policyMarkdown, IReadOnlyList<string> stageHeadings)
        {
            var lines = MarkdownSectionLocator.SplitLines(policyMarkdown);
            var fenceFlags = MarkdownSectionLocator.ComputeFenceFlags(lines);
            var rules = new List<PolicyRule>();

            foreach (var heading in stageHeadings)
            {
                var (headerIndex, endIndex) = MarkdownSectionLocator.LocateSection(lines, heading, "## ");
                if (headerIndex < 0)
                {
                    continue;
                }

                var stageNumber = PolicySectionContract.StageNumberOf(heading);

                for (var i = headerIndex + 1; i < endIndex; i++)
                {
                    if (fenceFlags[i])
                    {
                        continue;
                    }

                    var cells = SplitRow(lines[i]);
                    if (cells is null
                        || cells.Count != PolicySectionContract.ExpectedCellCount
                        || IsHeaderOrSeparator(cells))
                    {
                        continue;
                    }

                    rules.Add(new PolicyRule(
                        heading, stageNumber, cells[0], cells[1], cells[2], cells[3], i + 1));
                }
            }

            return rules;
        }

        private static List<string>? SplitRow(string line)
        {
            var trimmed = line.Trim();
            return trimmed.StartsWith("|", StringComparison.Ordinal)
                ? MarkdownTableCellCodec.SplitRow(trimmed.Trim('|'))
                : null;
        }

        private static bool IsHeaderOrSeparator(List<string> cells) =>
            cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase)
            || cells.All(c => c.Length > 0 && c.All(ch => ch == ':' || ch == '-'));
    }
}
```

- [ ] **Step 7: 파서 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PolicyDocumentParserTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 8: 귀속 검사 테스트를 쓴다**

`tests/ReSet.Core.Tests/PolicyAttributionValidatorTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyAttributionValidatorTests
    {
        private static readonly string[] Stages = { "## 1. 수수료율 스냅샷 적재", "## 2. 정산 원장 적재" };

        private static readonly Dictionary<string, string> Specs = new()
        {
            ["dbo.UP_RATE"] = "## 개요\n\n요율을 적재한다.\n",
            ["dbo.UP_INS"] = "## 개요\n\n결제수단이 impaymobile인 건을 대상으로 한다.\n",
        };

        private static string Sound() =>
            "## 1. 수수료율 스냅샷 적재\n\n" + PolicySectionContract.TableHeader + "\n"
            + PolicySectionContract.TableSeparator + "\n"
            + "| S1-01 | 요율을 적재한다 | dbo.UP_RATE · ## 개요 > \"요율을 적재\" | - |\n\n"
            + "## 2. 정산 원장 적재\n\n" + PolicySectionContract.TableHeader + "\n"
            + PolicySectionContract.TableSeparator + "\n"
            + "| S2-01 | 간편결제 건을 대상으로 한다 | dbo.UP_INS · ## 개요 > \"impaymobile인 건\" | impaymobile |\n";

        [Fact]
        public void 근거가_실재하면_결함이_없다()
        {
            var result = PolicyAttributionValidator.Validate(Sound(), Stages, Specs);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void 명부에_있는_단계가_문서에_없으면_고발한다()
        {
            var body = Sound().Replace("## 2. 정산 원장 적재", "## 2. 다른 이름");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.StageMissing);
        }

        [Fact]
        public void 단계_순서가_뒤바뀌면_고발한다()
        {
            var body =
                "## 2. 정산 원장 적재\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S2-01 | 간편결제 건 | dbo.UP_INS · ## 개요 > \"impaymobile인 건\" | impaymobile |\n\n"
                + "## 1. 수수료율 스냅샷 적재\n\n" + PolicySectionContract.TableHeader + "\n"
                + PolicySectionContract.TableSeparator + "\n"
                + "| S1-01 | 요율을 적재한다 | dbo.UP_RATE · ## 개요 > \"요율을 적재\" | - |\n";

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.StageOutOfOrder);
        }

        [Fact]
        public void 없는_명세서를_인용하면_고발한다()
        {
            var body = Sound().Replace("dbo.UP_RATE ·", "dbo.UP_없는것 ·");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.EvidenceLabelUnknown);
        }

        [Fact]
        public void 없는_헤딩을_인용하면_고발한다()
        {
            var body = Sound().Replace("## 개요 > \"요율을 적재\"", "## 없는절 > \"요율을 적재\"");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.EvidenceHeadingNotFound);
        }

        [Fact]
        public void 원문에_없는_구절을_인용하면_고발한다()
        {
            var body = Sound().Replace("\"요율을 적재\"", "\"요율을 삭제\"");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.EvidenceQuoteNotFound);
        }

        [Fact]
        public void ID_접두사가_단계_번호와_다르면_고발한다()
        {
            var body = Sound().Replace("| S2-01 |", "| S9-01 |");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.IdPrefixMismatch);
        }

        [Fact]
        public void 근거_칸이_형식을_어기면_고발한다()
        {
            var body = Sound().Replace("dbo.UP_RATE · ## 개요 > \"요율을 적재\"", "그냥 문장");

            var result = PolicyAttributionValidator.Validate(body, Stages, Specs);

            Assert.Contains(result.Defects, d => d.Type == PolicyDefectType.EvidenceMissing);
        }
    }
}
```

- [ ] **Step 9: 귀속 검사기를 만든다**

`src/ReSet.Core/Services/PolicyAttributionValidator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    public enum PolicyDefectType
    {
        StageMissing,
        StageOutOfOrder,
        IdPrefixMismatch,
        EvidenceMissing,
        EvidenceLabelUnknown,
        EvidenceHeadingNotFound,
        EvidenceQuoteNotFound,
        CodeValueNotInCodebook,
        ProcedureNeverCited,
    }

    public sealed record PolicyDefect(PolicyDefectType Type, string Subject, string RuleId, string Message);

    public sealed class PolicyValidationResult
    {
        public PolicyValidationResult(IReadOnlyList<PolicyDefect> defects) => Defects = defects;

        public IReadOnlyList<PolicyDefect> Defects { get; }

        public bool IsValid => Defects.Count == 0;
    }

    /// <summary>
    /// 정책서의 규칙이 원본 명세서의 실재하는 자리를 인용하는지 대조한다.
    ///
    /// [PRD와 무엇이 다른가] 원본이 여럿이다. 근거 칸의 SP 식별자로 어느 명세서를 볼지
    /// 고른 뒤, 그 다음은 EvidenceQuoteMatcher가 PRD와 똑같이 잰다.
    ///
    /// [무엇을 못 재는가] PrdAttributionValidator와 같다 - 인용이 진짜인데 규칙 서술이
    /// 그 인용과 무관한 경우(귀속 오배치)는 이 오라클로 잴 수 없다. 정책서에도 L2가
    /// 없으므로 그 구멍은 사람 검토에 남고 문서 배너가 그 사실을 명시한다.
    /// </summary>
    public static class PolicyAttributionValidator
    {
        public static PolicyValidationResult Validate(
            string? policyMarkdown,
            IReadOnlyList<string> stageHeadings,
            IReadOnlyDictionary<string, string> specsByLabel)
        {
            var defects = new List<PolicyDefect>();
            var lines = MarkdownSectionLocator.SplitLines(policyMarkdown);

            // 1. 단계 완전성과 순서
            var positions = new List<(string Heading, int Index)>();
            foreach (var heading in stageHeadings)
            {
                var (headerIndex, _) = MarkdownSectionLocator.LocateSection(lines, heading, "## ");
                if (headerIndex < 0)
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.StageMissing, heading, string.Empty,
                        $"명부의 단계 '{heading}'이 정책서에 없습니다."));
                    continue;
                }

                positions.Add((heading, headerIndex));
            }

            for (var i = 1; i < positions.Count; i++)
            {
                if (positions[i].Index < positions[i - 1].Index)
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.StageOutOfOrder, positions[i].Heading, string.Empty,
                        $"단계 '{positions[i].Heading}'이 명부 순서보다 앞에 있습니다."));
                }
            }

            // 2. 규칙별 귀속
            var sectionBodyCache = new Dictionary<(string Label, string Heading), string?>();

            foreach (var rule in PolicyDocumentParser.Parse(policyMarkdown, stageHeadings))
            {
                var expectedPrefix = PolicySectionContract.IdPrefixFor(rule.StageNumber) + "-";
                if (!rule.Id.StartsWith(expectedPrefix, StringComparison.Ordinal))
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.IdPrefixMismatch, rule.StageHeading, rule.Id,
                        $"'{rule.StageHeading}'의 규칙 ID는 '{expectedPrefix}'로 시작해야 합니다."));
                }

                if (!PolicySectionContract.TryParseEvidence(rule.EvidenceRaw, out var evidence))
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.EvidenceMissing, rule.StageHeading, rule.Id,
                        "근거 칸이 '<SP> · ## 헤딩 > \"원문 구절\"' 형식이 아닙니다."));
                    continue;
                }

                if (!specsByLabel.TryGetValue(evidence.Label, out var specMarkdown))
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.EvidenceLabelUnknown, rule.StageHeading, rule.Id,
                        $"근거로 인용한 '{evidence.Label}'의 명세서가 재료에 없습니다."));
                    continue;
                }

                var cacheKey = (evidence.Label, evidence.Heading);
                if (!sectionBodyCache.TryGetValue(cacheKey, out var body))
                {
                    body = EvidenceQuoteMatcher.ExtractSectionBody(
                        MarkdownSectionLocator.SplitLines(specMarkdown), evidence.Heading);
                    sectionBodyCache[cacheKey] = body;
                }

                if (body is null)
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.EvidenceHeadingNotFound, rule.StageHeading, rule.Id,
                        $"인용한 헤딩 '{evidence.Heading}'이 {evidence.Label}의 명세서에 없습니다."));
                    continue;
                }

                if (!EvidenceQuoteMatcher.QuoteExistsIn(body, evidence.Quote))
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.EvidenceQuoteNotFound, rule.StageHeading, rule.Id,
                        $"인용 구절 \"{evidence.Quote}\"을 {evidence.Label}의 '{evidence.Heading}' 절에서 찾을 수 없습니다."));
                }
            }

            return new PolicyValidationResult(defects);
        }
    }
}
```

- [ ] **Step 10: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PolicyAttributionValidatorTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 11: 역변이로 효력을 확인한다**

`QuoteExistsIn` 호출을 `true`로 고정하고 돌린다 → `원문에_없는_구절을_인용하면_고발한다` 실패.
`StageOutOfOrder`를 내는 `for` 블록을 지우고 돌린다 → `단계_순서가_뒤바뀌면_고발한다` 실패.
`specsByLabel.TryGetValue` 실패 분기를 `continue`만 하도록 바꾸고 돌린다 → `없는_명세서를_인용하면_고발한다` 실패.
셋 다 되돌려 초록을 확인하고 **되돌린 상태로** 커밋한다.

- [ ] **Step 12: 커밋**

```bash
git add tests/ReSet.Core.Tests/PolicyDocumentParserTests.cs \
        tests/ReSet.Core.Tests/PolicyAttributionValidatorTests.cs \
        src/ReSet.Core/Services/PolicySectionContract.cs \
        src/ReSet.Core/Services/PolicyDocumentParser.cs \
        src/ReSet.Core/Services/PolicyAttributionValidator.cs
git commit -m "feat: 원본이 여럿인 정책서 귀속 검사

근거 칸의 SP 식별자로 명세서를 고른 뒤 PRD와 같은 자로 인용을 잰다.
단계 완전성과 순서도 여기서 함께 본다.
역변이 셋(인용대조 true 고정, 순서검사 제거, 라벨검사 제거) 확인:
각각 대응 테스트만 실패 후 복구."
```

---

## Task 8: 코드값 대조와 SP 인용 커버리지

**Files:**
- Create: `src/ReSet.Core/Services/PolicyDocumentChecks.cs`
- Test: `tests/ReSet.Core.Tests/PolicyDocumentChecksTests.cs`

**Interfaces:**
- Consumes: `PolicyRule`, `PolicyDocumentParser.Parse`, `PolicyDefect`, `PolicyDefectType` (Task 7); `SettlementCodebook`, `CodebookEntry` (Task 5); `SettlementProcessRoster` (Task 2)
- Produces:
  - `public static IReadOnlyList<PolicyDefect> PolicyDocumentChecks.CheckCodeValues(IReadOnlyList<PolicyRule> rules, SettlementCodebook codebook)`
  - `public static IReadOnlyList<PolicyDefect> PolicyDocumentChecks.CheckProcedureCitationCoverage(IReadOnlyList<PolicyRule> rules, SettlementProcessRoster roster)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/PolicyDocumentChecksTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyDocumentChecksTests
    {
        private static PolicyRule Rule(string id, string evidence, string codeValue) =>
            new("## 1. 단계", 1, id, "규칙", evidence, codeValue, 10);

        private static SettlementCodebook Codebook(params string[] values) =>
            new(values.Select(v => new CodebookEntry(
                    v, null, new[] { "dbo.UP_A" }, true,
                    new[] { new CodebookMatch("dbo.TCode", new Dictionary<string, string> { ["Name"] = "뜻" }) }))
                .ToList(),
                Array.Empty<string>());

        private static SettlementProcessRoster Roster(params string[] procedures) =>
            new(new[] { new PolicyStage("1. 단계", procedures) }, Array.Empty<string>());

        [Fact]
        public void 사전에_있는_코드값은_통과한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "impaymobile") },
                Codebook("impaymobile"));

            Assert.Empty(defects);
        }

        // 이 검사가 「AI가 지어낸 번역」을 막는 자리다.
        [Fact]
        public void 사전에_없는_코드값을_고발한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "지어낸값") },
                Codebook("impaymobile"));

            var defect = Assert.Single(defects);
            Assert.Equal(PolicyDefectType.CodeValueNotInCodebook, defect.Type);
            Assert.Equal("S1-01", defect.RuleId);
        }

        [Fact]
        public void 하이픈은_해당없음_표기이므로_고발하지_않는다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", PolicySectionContract.NoCodeValue) },
                Codebook("impaymobile"));

            Assert.Empty(defects);
        }

        [Fact]
        public void 한_칸에_쉼표로_여러_코드값을_적어도_각각_대조한다()
        {
            var defects = PolicyDocumentChecks.CheckCodeValues(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "impaymobile, 지어낸값") },
                Codebook("impaymobile"));

            var defect = Assert.Single(defects);
            Assert.Contains("지어낸값", defect.Message);
        }

        [Fact]
        public void 명부의_SP가_모두_인용되면_통과한다()
        {
            var defects = PolicyDocumentChecks.CheckProcedureCitationCoverage(
                new[]
                {
                    Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "-"),
                    Rule("S1-02", "dbo.UP_B · ## 개요 > \"y\"", "-"),
                },
                Roster("dbo.UP_A", "dbo.UP_B"));

            Assert.Empty(defects);
        }

        // 명부 대조(Task 3)는 「명부에 있는가」만 본다. 명부에 있어도 AI가 그 명세서를
        // 안 읽고 지나가면 규칙이 통째로 빠지고 문서는 멀쩡해 보인다. 여기가 그 두 번째 문이다.
        [Fact]
        public void 한_번도_인용되지_않은_SP를_고발한다()
        {
            var defects = PolicyDocumentChecks.CheckProcedureCitationCoverage(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "-") },
                Roster("dbo.UP_A", "dbo.UP_B"));

            var defect = Assert.Single(defects);
            Assert.Equal(PolicyDefectType.ProcedureNeverCited, defect.Type);
            Assert.Equal("dbo.UP_B", defect.Subject);
        }

        [Fact]
        public void 제외에_적힌_SP는_인용_의무가_없다()
        {
            var roster = new SettlementProcessRoster(
                new[] { new PolicyStage("1. 단계", new[] { "dbo.UP_A" }) },
                new[] { "dbo.UP_B" });

            var defects = PolicyDocumentChecks.CheckProcedureCitationCoverage(
                new[] { Rule("S1-01", "dbo.UP_A · ## 개요 > \"x\"", "-") }, roster);

            Assert.Empty(defects);
        }

        [Fact]
        public void 형식이_깨진_근거는_인용으로_세지_않는다()
        {
            var defects = PolicyDocumentChecks.CheckProcedureCitationCoverage(
                new[] { Rule("S1-01", "그냥 문장", "-") },
                Roster("dbo.UP_A"));

            Assert.Contains(defects, d => d.Type == PolicyDefectType.ProcedureNeverCited);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PolicyDocumentChecksTests`
Expected: 컴파일 실패 — `PolicyDocumentChecks` 없음

- [ ] **Step 3: 검사를 구현한다**

`src/ReSet.Core/Services/PolicyDocumentChecks.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 정책서 고유의 검사 둘 - 코드값 대조와 SP 인용 커버리지.
    ///
    /// [왜 귀속 검사와 나눠 두는가] 귀속 검사의 오라클은 명세서 텍스트이고, 이 둘의
    /// 오라클은 각각 코드값 사전과 명부다. 재료가 다른 검사를 한 클래스에 넣으면
    /// 인자 목록이 부풀고 어느 검사가 어느 재료를 쓰는지 흐려진다 -
    /// PrdAttributionValidator가 MechanicalValidator에 들어가지 않은 것과 같은 판단이다.
    /// </summary>
    public static class PolicyDocumentChecks
    {
        /// <summary>
        /// 문서에 실린 코드값이 사전에 있는지 본다.
        ///
        /// [비순환 오라클] 검사가 보는 파일은 정책서이고 기준이 되는 파일은
        /// settlement-codebook.json이다. AI 출력을 기계 산출물로 재므로 순환하지 않는다.
        /// </summary>
        public static IReadOnlyList<PolicyDefect> CheckCodeValues(
            IReadOnlyList<PolicyRule> rules, SettlementCodebook codebook)
        {
            var known = codebook.Entries
                .Select(e => e.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var defects = new List<PolicyDefect>();

            foreach (var rule in rules)
            {
                var unknown = SplitCodeValues(rule.CodeValue)
                    .Where(v => !known.Contains(v))
                    .ToList();

                if (unknown.Count > 0)
                {
                    defects.Add(new PolicyDefect(
                        PolicyDefectType.CodeValueNotInCodebook,
                        rule.StageHeading,
                        rule.Id,
                        $"코드값 {string.Join(", ", unknown.Select(v => $"'{v}'"))}이 코드값 사전에 없습니다. 사전에 있는 값만 실을 수 있습니다."));
                }
            }

            return defects;
        }

        /// <summary>
        /// 명부에 실린 SP가 최소 한 번은 규칙의 근거로 인용됐는지 본다.
        ///
        /// [왜 필요한가] 명부 대조(SettlementRosterReconciler)는 「명부에 있는가」만 본다.
        /// 명부에 있어도 AI가 그 명세서를 읽지 않고 지나가면 그 SP의 규칙이 통째로
        /// 빠지는데 문서는 멀쩡해 보인다. 조용한 누락의 두 번째 문이다.
        /// </summary>
        public static IReadOnlyList<PolicyDefect> CheckProcedureCitationCoverage(
            IReadOnlyList<PolicyRule> rules, SettlementProcessRoster roster)
        {
            var cited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
            {
                if (PolicySectionContract.TryParseEvidence(rule.EvidenceRaw, out var evidence))
                {
                    cited.Add(evidence.Label);
                }
            }

            return roster.AllStagedProcedures()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !cited.Contains(p))
                .Select(p => new PolicyDefect(
                    PolicyDefectType.ProcedureNeverCited,
                    p,
                    string.Empty,
                    $"'{p}'의 명세서가 어떤 규칙의 근거로도 인용되지 않았습니다. 그 SP의 업무 규칙이 문서에서 빠졌을 수 있습니다."))
                .ToList();
        }

        /// <summary>한 칸에 쉼표로 여러 코드값이 적힐 수 있다. '-'는 해당 없음 표기이므로 뺀다.</summary>
        private static IEnumerable<string> SplitCodeValues(string? cell) =>
            (cell ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => v.Length > 0 && v != PolicySectionContract.NoCodeValue);
    }
}
```

- [ ] **Step 4: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PolicyDocumentChecksTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 5: 역변이로 효력을 확인한다**

`CheckCodeValues`의 `known.Contains(v)` 판정을 `true`로 고정 → `사전에_없는_코드값을_고발한다`·`한_칸에_쉼표로...` 2건 실패.
`CheckProcedureCitationCoverage`의 `cited.Contains(p)` 판정을 `true`로 고정 → `한_번도_인용되지_않은_SP를_고발한다`·`형식이_깨진_근거는...` 2건 실패.
둘 다 되돌려 초록을 확인하고 **되돌린 상태로** 커밋한다.

- [ ] **Step 6: 커밋**

```bash
git add tests/ReSet.Core.Tests/PolicyDocumentChecksTests.cs \
        src/ReSet.Core/Services/PolicyDocumentChecks.cs
git commit -m "feat: 코드값 대조와 SP 인용 커버리지 검사

코드값 대조는 AI가 지어낸 번역을 막고, 인용 커버리지는 명부에 있어도
AI가 안 읽고 지나간 SP를 잡는다(조용한 누락의 두 번째 문).
역변이 둘(각 판정 true 고정) 확인: 각 2건 실패 후 복구."
```

---

## Task 9: AI 프롬프트 — 단계별 서술과 전체 개요

**Files:**
- Modify: `src/ReSet.Core/Services/IAiService.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs:4810-4873` (`GenerateSettlementPolicyRulebookAsync` 제거 후 두 메서드 추가)
- Test: `tests/ReSet.Core.Tests/PolicyPromptTests.cs`

**Interfaces:**
- Consumes: `PolicySectionContract.TableHeader`, `.LabelSeparator`, `.NoCodeValue`, `.IdPrefixFor` (Task 7); `CodebookEntry` (Task 5)
- Produces:
  - `Task<AiResult> IAiService.GeneratePolicyStageAsync(int stageNumber, string stageTitle, IReadOnlyList<(string Label, string SpecMarkdown)> sources, IReadOnlyList<CodebookEntry> codeValues, string? attributionFeedback = null, string? effort = null, CancellationToken cancellationToken = default)`
  - `Task<AiResult> IAiService.GeneratePolicyOverviewAsync(IReadOnlyList<string> stageTitles, string assembledStages, string? effort = null, CancellationToken cancellationToken = default)`
- Removes: `Task<AiResult> IAiService.GenerateSettlementPolicyRulebookAsync(...)`

- [ ] **Step 1: 프롬프트 테스트를 쓴다**

기존 `tests/ReSet.Core.Tests/PrdPromptTests.cs`를 열어 「프롬프트가 계약에서 문구를 읽는지」를
어떻게 검사하는지 확인하고, 같은 방식으로 `tests/ReSet.Core.Tests/PolicyPromptTests.cs`를 쓴다.
핵심은 **프롬프트 문자열에 계약 상수가 실제로 박혀 있는지**를 재는 것이다(하드코딩하면 검증기와
갈라진다는 것이 `PrdSectionContract` 주석의 경고다).

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyPromptTests
    {
        private static (AiService Service, IAiClient Client) Build()
        {
            var client = Substitute.For<IAiClient>();
            client.ChatAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float>(),
                    effort: Arg.Any<string?>(), cancellationToken: Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "본문" }));
            client.ProviderName.Returns("TestProvider");
            client.ModelName.Returns("test-model");
            return (new AiService(client), client);
        }

        private static IReadOnlyList<(string, string)> Sources() =>
            new[] { ("dbo.UP_A", "## 개요\n\n본문\n") };

        private static IReadOnlyList<CodebookEntry> CodeValues() =>
            new[]
            {
                new CodebookEntry("impaymobile", "PayMethod", new[] { "dbo.UP_A" }, true,
                    new[] { new CodebookMatch("dbo.TCode", new Dictionary<string, string> { ["Name"] = "간편결제" }) }),
                new CodebookEntry("payco", null, new[] { "dbo.UP_A" }, true, Array.Empty<CodebookMatch>()),
            };

        [Fact]
        public async Task 단계_프롬프트는_계약의_표머리를_그대로_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "수수료율 스냅샷 적재", Sources(), CodeValues());

            Assert.Contains(PolicySectionContract.TableHeader, result.SystemPrompt);
        }

        [Fact]
        public async Task 단계_프롬프트는_그_단계의_ID_접두사를_지시한다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                3, "정산 집계", Sources(), CodeValues());

            Assert.Contains(PolicySectionContract.IdPrefixFor(3) + "-", result.SystemPrompt);
        }

        [Fact]
        public async Task 매칭된_코드값은_의미와_함께_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "단계", Sources(), CodeValues());

            Assert.Contains("impaymobile", result.UserPrompt);
            Assert.Contains("간편결제", result.UserPrompt);
        }

        [Fact]
        public async Task 미매칭_코드값은_의미_미상으로_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "단계", Sources(), CodeValues());

            Assert.Contains("payco", result.UserPrompt);
            Assert.Contains("의미 미상", result.UserPrompt);
        }

        [Fact]
        public async Task 그_단계의_명세서만_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "단계",
                new[] { ("dbo.UP_A", "## 개요\n\nA의 본문\n") },
                CodeValues());

            Assert.Contains("A의 본문", result.UserPrompt);
            Assert.DoesNotContain("B의 본문", result.UserPrompt);
        }

        [Fact]
        public async Task 교정_피드백을_주면_프롬프트에_실린다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyStageAsync(
                1, "단계", Sources(), CodeValues(), attributionFeedback: "S1-01의 인용이 원문에 없습니다");

            Assert.Contains("S1-01의 인용이 원문에 없습니다", result.UserPrompt);
        }

        [Fact]
        public async Task 개요_프롬프트는_단계_제목을_전부_싣는다()
        {
            var (service, _) = Build();

            var result = await service.GeneratePolicyOverviewAsync(
                new[] { "1. 요율 적재", "2. 원장 적재" }, "조립된 단계 본문");

            Assert.Contains("1. 요율 적재", result.UserPrompt);
            Assert.Contains("2. 원장 적재", result.UserPrompt);
        }
    }
}
```

**주의:** `AiService`의 생성자 시그니처와 `IAiClient.ChatAsync`의 인자 이름을 구현 전에
`src/ReSet.Core/Services/AiService.cs` 상단과 `PrdPromptTests.cs`에서 확인하고, 위 `Build()`를
실제 시그니처에 맞춘다. 기존 PRD 프롬프트 테스트가 이미 같은 일을 하고 있으므로 그 파일의
설정 코드를 그대로 따르면 된다.

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PolicyPromptTests`
Expected: 컴파일 실패 — `GeneratePolicyStageAsync` 없음

- [ ] **Step 3: 인터페이스에서 옛 메서드를 빼고 새 메서드를 넣는다**

`src/ReSet.Core/Services/IAiService.cs`에서 다음 줄을 **지운다**:

```csharp
        Task<AiResult> GenerateSettlementPolicyRulebookAsync(System.Collections.Generic.List<SpDefinition> spDefs, string profilingDataJson, CancellationToken cancellationToken = default);
```

대신 다음을 넣는다:

```csharp
        Task<AiResult> GeneratePolicyStageAsync(int stageNumber, string stageTitle, System.Collections.Generic.IReadOnlyList<(string Label, string SpecMarkdown)> sources, System.Collections.Generic.IReadOnlyList<CodebookEntry> codeValues, string? attributionFeedback = null, string? effort = null, CancellationToken cancellationToken = default);
        Task<AiResult> GeneratePolicyOverviewAsync(System.Collections.Generic.IReadOnlyList<string> stageTitles, string assembledStages, string? effort = null, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: AiService에 두 메서드를 구현한다**

`AiService.cs`의 `GenerateSettlementPolicyRulebookAsync`(약 4810~4873행) 전체를 **지우고**
그 자리에 다음을 넣는다:

```csharp
        /// <summary>
        /// 한 단계의 업무 규칙을 서술한다.
        ///
        /// [왜 단계마다 나눠 부르는가] 명세서 14편의 합계가 421,121자(약 19만 토큰)다.
        /// 단발 호출은 넣더라도 그 분량을 한 번에 요약하라는 요청이 되어 품질이 무너진다.
        ///
        /// [왜 코드값을 프롬프트가 정하는가] 사전에 있는 번역만 실려야 완성된 문서를
        /// 사전과 대조할 수 있다. 모델이 코드값을 지어내면 PolicyDocumentChecks가
        /// 잡지만, 애초에 지어낼 여지를 주지 않는 편이 재호출을 줄인다.
        /// </summary>
        public async Task<AiResult> GeneratePolicyStageAsync(
            int stageNumber,
            string stageTitle,
            IReadOnlyList<(string Label, string SpecMarkdown)> sources,
            IReadOnlyList<CodebookEntry> codeValues,
            string? attributionFeedback = null,
            string? effort = null,
            CancellationToken cancellationToken = default)
        {
            var idPrefix = PolicySectionContract.IdPrefixFor(stageNumber);

            var systemPrompt = $@"You are a business analyst writing a settlement operations handover document for staff who do NOT know the legacy system.
Your ONLY source is the Korean specification documents supplied by the user. You have no access to the original SQL.

[Absolute rules]
1. Write in Korean. Output exactly one H2 section titled `## {stageNumber}. {stageTitle}` and nothing else — no preamble, no closing summary, no other headings.
2. Open with 2 to 4 sentences of prose explaining what this stage does in BUSINESS terms. Then one markdown table.
3. The table header row MUST be exactly:
   {PolicySectionContract.TableHeader}
   followed by the separator row:
   {PolicySectionContract.TableSeparator}
4. Rule IDs are `{idPrefix}-<two digits>`, numbered from 01.
5. `업무 규칙` is one sentence in business language. NEVER describe SQL, joins, table names, or control flow — the reader does not know them.
6. `근거` MUST be `<procedure> {PolicySectionContract.LabelSeparator}## <specification heading> > ""<verbatim excerpt>""`.
   The procedure must be one of the identifiers listed under [Source specifications].
   The excerpt MUST appear verbatim inside that heading's section of THAT procedure's specification.
   If the excerpt contains a `|` character — specification facts often live in markdown tables — write each one as `\|`.
7. `코드값` MUST be either `{PolicySectionContract.NoCodeValue}` or one or more values taken VERBATIM from [Code values]. Never invent a code value and never invent its meaning.
8. Every procedure listed under [Source specifications] MUST be the 근거 of at least one rule.
9. Do not include Mermaid diagrams. Do not wrap the response in a markdown code block.";

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine("[Source specifications — the only source of truth]");
            foreach (var (label, spec) in sources)
            {
                userPrompt.AppendLine($"### {label}");
                userPrompt.AppendLine(spec);
                userPrompt.AppendLine();
            }

            userPrompt.AppendLine("[Code values — use only these, verbatim]");
            if (codeValues.Count == 0)
            {
                userPrompt.AppendLine("(없음)");
            }
            else
            {
                foreach (var entry in codeValues)
                {
                    if (entry.Matches.Count == 0)
                    {
                        userPrompt.AppendLine($"- `{entry.Value}` : 의미 미상 (마스터 데이터에서 찾지 못함)");
                        continue;
                    }

                    foreach (var match in entry.Matches)
                    {
                        var row = string.Join(", ", match.Row.Select(kv => $"{kv.Key}={kv.Value}"));
                        userPrompt.AppendLine($"- `{entry.Value}` : {match.Table} → {row}");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(attributionFeedback))
            {
                userPrompt.AppendLine();
                userPrompt.AppendLine("[Attribution check feedback — the previous draft failed these]");
                userPrompt.AppendLine(attributionFeedback);
            }

            Log.Information("AI 정책 단계 서술 요청 - 단계 {Stage}: {Title}", stageNumber, stageTitle);

            var aiResult = await _aiClient.ChatAsync(
                systemPrompt, userPrompt.ToString(), _temperature,
                effort: effort, cancellationToken: cancellationToken) ?? new AiResult();

            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt.ToString();
            return aiResult;
        }

        /// <summary>
        /// 조립된 단계들 위에 얹을 전체 개요를 쓴다.
        ///
        /// 목차는 이미 사람이 명부에 정했고 기계가 조립했다. 여기서 AI가 하는 일은
        /// 「이 정산 업무 전체가 무엇인가」를 산문으로 여는 것뿐이며, 새 사실을
        /// 만들지 않는다.
        /// </summary>
        public async Task<AiResult> GeneratePolicyOverviewAsync(
            IReadOnlyList<string> stageTitles,
            string assembledStages,
            string? effort = null,
            CancellationToken cancellationToken = default)
        {
            var systemPrompt = @"You are a business analyst writing the opening overview of a settlement operations handover document.

[Absolute rules]
1. Write in Korean. Output exactly one H2 section titled `## 정산 업무 개요` and nothing else.
2. Explain, in 3 to 6 short paragraphs, what this settlement process does as a whole and how the stages relate in business terms.
3. State ONLY what the supplied stage bodies already say. Do not introduce facts that are not there.
4. Do not restate the stage list as a bullet list — the document already has a table of contents.
5. Do not include Mermaid diagrams. Do not wrap the response in a markdown code block.";

            var userPrompt = new StringBuilder();
            userPrompt.AppendLine("[Stages in order]");
            foreach (var title in stageTitles)
            {
                userPrompt.AppendLine($"- {title}");
            }

            userPrompt.AppendLine();
            userPrompt.AppendLine("[Assembled stage bodies]");
            userPrompt.AppendLine(assembledStages);

            Log.Information("AI 정책 개요 서술 요청 - 단계 {Count}개", stageTitles.Count);

            var aiResult = await _aiClient.ChatAsync(
                systemPrompt, userPrompt.ToString(), _temperature,
                effort: effort, cancellationToken: cancellationToken) ?? new AiResult();

            aiResult.SystemPrompt = systemPrompt;
            aiResult.UserPrompt = userPrompt.ToString();
            return aiResult;
        }
```

**주의:** `_aiClient.ChatAsync`를 부를 때 `cancellationToken`을 **명명 인자로** 넘긴다.
위치 인자로 넘기면 `volatileUserSuffix`에 바인딩된다 — `GeneratePrdFromSpecAsync` 바로 위에
그 사고를 막는 주석이 이미 달려 있다.

- [ ] **Step 5: 통과를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PolicyPromptTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 6: 전체 빌드로 옛 메서드 제거의 파급을 확인한다**

Run: `dotnet build`
Expected: `GenerateSettlementPolicyRulebookAsync`를 부르던 자리
(`SettlementPolicyService.cs`, `Program.cs`)가 컴파일 오류로 드러난다. **이 오류는 Task 10·11이
닫는다.** 여기서는 오류 목록이 그 두 파일에만 한정되는지 확인하고 다음 태스크로 간다.

- [ ] **Step 7: 커밋 (컴파일이 깨진 중간 상태이므로 Task 10과 함께 커밋한다)**

이 태스크는 단독 커밋하지 않는다. Task 10의 커밋에 함께 실린다 — 인터페이스에서 메서드를
빼는 변경은 그 호출부를 고치기 전까지 빌드가 서지 않으므로, 「빌드 경고 0」 게이트를 만족하는
가장 작은 단위가 Task 9+10이다.

---

## Task 10: 서비스 재작성 — 조립·배너·저장

**Files:**
- Rewrite: `src/ReSet.Core/Services/SettlementPolicyService.cs`
- Rewrite: `src/ReSet.Core/Services/ISettlementPolicyService.cs`
- Create: `src/ReSet.Core/Services/PolicyDerivationOutcome.cs`
- Create: `src/ReSet.Core/Services/PolicyDocumentAssembler.cs`
- Create: `src/ReSet.Core/Services/PolicyReportBanner.cs`
- Move: `src/ReSet.Cli/SpecHeaderReader.cs` → `src/ReSet.Core/Services/SpecHeaderReader.cs` (네임스페이스를 `ReSet.Core.Services`로)
- Modify: `src/ReSet.Core/Services/VerificationDocumentFormatter.cs` (집계 오버로드)
- Test: `tests/ReSet.Core.Tests/SettlementPolicyServiceTests.cs`

**Interfaces:**
- Consumes: 전 태스크의 모든 산출물
- Produces:
  - `public sealed record PolicyDerivationOutcome(string PolicyPath, string CodebookPath, IReadOnlyList<PolicyDefect> Defects, int CodeValuesTranslated, int CodeValuesUnmatched, int CodeValuesSkippedShort, bool ProfilingRan)`
  - `public sealed class PolicyRosterBlockedException : Exception` — `IReadOnlyList<RosterDefect> Defects`, `string RosterPath`
  - `public static string PolicyReportBanner.Build(IReadOnlyList<PolicyDefect> defects, int translated, int unmatched, int skippedShort, bool profilingRan)`
  - `public const int SettlementPolicyService.StageSpecCharWarningThreshold = 120_000`
  - `Task<PolicyDerivationOutcome> ISettlementPolicyService.GenerateAsync(string outputRoot, ICodeTableProfiler? profiler, string? effort, CancellationToken cancellationToken = default)`
  - `public static string PolicyDocumentAssembler.Assemble(string overview, IReadOnlyList<string> stageBodies, SettlementCodebook codebook, SettlementProcessRoster roster)`
  - `public static string VerificationDocumentFormatter.FormatUnverifiedDocument(string body, IReadOnlyDictionary<string, int> sourceStatusCounts, string provider, string modelName, string? effort, DateTime timestamp)`

- [ ] **Step 1: 서비스 테스트를 쓴다 (DB 없이 전량 돈다)**

`tests/ReSet.Core.Tests/SettlementPolicyServiceTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SettlementPolicyServiceTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "reset-policysvc-" + Guid.NewGuid().ToString("N"));

        public SettlementPolicyServiceTests()
        {
            Seed("dbo.UP_A", "## 개요\n\n요율을 적재한다.\n");
            Seed("dbo.UP_B", "## 개요\n\n원장을 적재한다.\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void Seed(string label, string spec)
        {
            var docs = Path.Combine(_root, "Procedures", label, "docs");
            var raw = Path.Combine(_root, "Procedures", label, "raw");
            Directory.CreateDirectory(docs);
            Directory.CreateDirectory(raw);
            File.WriteAllText(Path.Combine(docs, "Spec.md"),
                "---\n검증 상태: 통과\n---\n\n" + spec);
            File.WriteAllText(Path.Combine(raw, "metadata.json"),
                "{\"Schema\":\"dbo\",\"Name\":\"X\",\"DdlText\":\"\",\"Dependencies\":[],\"StaticAnalysis\":{}}");
        }

        private void WriteRoster()
        {
            File.WriteAllText(Path.Combine(_root, "settlement-process.md"),
                "# 정산 프로세스 명부\n\n## 1. 요율 적재\n- dbo.UP_A\n\n## 2. 원장 적재\n- dbo.UP_B\n\n## 제외\n");
        }

        private static IAiService AiWriting(params string[] stageBodies)
        {
            var ai = Substitute.For<IAiService>();
            var call = 0;
            ai.GeneratePolicyStageAsync(
                    Arg.Any<int>(), Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<IReadOnlyList<CodebookEntry>>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(
                    new AiResult { Content = stageBodies[Math.Min(call++, stageBodies.Length - 1)] }));
            ai.GeneratePolicyOverviewAsync(
                    Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = "## 정산 업무 개요\n\n전체 조망.\n" }));
            ai.ProviderName.Returns("TestProvider");
            ai.ModelName.Returns("test-model");
            return ai;
        }

        private static string Stage(int n, string title, string label, string quote) =>
            $"## {n}. {title}\n\n산문 개요.\n\n"
            + PolicySectionContract.TableHeader + "\n" + PolicySectionContract.TableSeparator + "\n"
            + $"| S{n}-01 | 업무 규칙 | {label} · ## 개요 > \"{quote}\" | {PolicySectionContract.NoCodeValue} |\n";

        [Fact]
        public async Task 명부가_없으면_초안을_쓰고_중단한다()
        {
            var service = new SettlementPolicyService(AiWriting("x"));

            await Assert.ThrowsAsync<PolicyRosterBlockedException>(
                () => service.GenerateAsync(_root, profiler: null, effort: null));

            Assert.True(File.Exists(Path.Combine(_root, "settlement-process.md")));
        }

        [Fact]
        public async Task 명부에_빠진_SP가_있으면_중단한다()
        {
            File.WriteAllText(Path.Combine(_root, "settlement-process.md"),
                "# 정산 프로세스 명부\n\n## 1. 요율 적재\n- dbo.UP_A\n\n## 제외\n");
            var service = new SettlementPolicyService(AiWriting("x"));

            var ex = await Assert.ThrowsAsync<PolicyRosterBlockedException>(
                () => service.GenerateAsync(_root, profiler: null, effort: null));

            Assert.Contains(ex.Defects, d => d.Type == RosterDefectType.ProcedureMissing);
        }

        [Fact]
        public async Task 기존_명부를_덮어쓰지_않는다()
        {
            WriteRoster();
            var before = File.ReadAllText(Path.Combine(_root, "settlement-process.md"));
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "settlement-process.md")));
        }

        [Fact]
        public async Task DB없이_완주하고_문서와_사전을_남긴다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.True(File.Exists(outcome.PolicyPath));
            Assert.True(File.Exists(outcome.CodebookPath));
            Assert.Equal(0, outcome.CodeValuesTranslated);
        }

        [Fact]
        public async Task 목차는_명부의_단계_제목_그대로다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);
            var document = File.ReadAllText(outcome.PolicyPath);

            Assert.Contains("## 1. 요율 적재", document);
            Assert.Contains("## 2. 원장 적재", document);
        }

        [Fact]
        public async Task 근거_명세서의_검증_상태를_헤더에_집계한다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);
            var document = File.ReadAllText(outcome.PolicyPath);

            Assert.Contains("근거 명세서 검증 상태:", document);
            Assert.Contains("통과 2", document);
        }

        [Fact]
        public async Task 인용이_원문에_없으면_배너에_결함을_싣는다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "원문에 없는 구절"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Contains(outcome.Defects, d => d.Type == PolicyDefectType.EvidenceQuoteNotFound);
            Assert.Contains("귀속", File.ReadAllText(outcome.PolicyPath));
        }

        [Fact]
        public async Task 한_번도_인용되지_않은_SP를_배너에_싣는다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_A", "요율을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Contains(outcome.Defects, d => d.Type == PolicyDefectType.ProcedureNeverCited);
            Assert.Contains("dbo.UP_B", File.ReadAllText(outcome.PolicyPath));
        }

        [Fact]
        public async Task DB가_없으면_코드값_번역_0건을_배너에_명시한다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.Contains("DB 미연결", File.ReadAllText(outcome.PolicyPath));
        }

        [Fact]
        public async Task 문서는_검증_없음으로_표기된다()
        {
            WriteRoster();
            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.StartsWith("---", File.ReadAllText(outcome.PolicyPath));
            Assert.Contains("검증 상태: 검증 없음", File.ReadAllText(outcome.PolicyPath));
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementPolicyServiceTests`
Expected: 컴파일 실패 — `PolicyRosterBlockedException`·새 `GenerateAsync` 없음

- [ ] **Step 3: SpecHeaderReader를 Core로 옮긴다**

`src/ReSet.Cli/SpecHeaderReader.cs`를 `src/ReSet.Core/Services/SpecHeaderReader.cs`로 옮기고
네임스페이스를 `ReSet.Core.Services`로 바꾼다. `Program.cs`의 `using`을 정리한다(`ReSet.Cli`
안에서 쓰던 자리는 이미 `ReSet.Core.Services`를 using 하고 있으므로 대개 자동으로 해결된다).

```bash
git mv src/ReSet.Cli/SpecHeaderReader.cs src/ReSet.Core/Services/SpecHeaderReader.cs
```

파일 안의 `namespace ReSet.Cli`를 `namespace ReSet.Core.Services`로 바꾼다.

Run: `dotnet build`
Expected: `SpecHeaderReader`를 쓰던 `Program.cs` 자리가 그대로 컴파일된다(같은 어셈블리를 이미 참조).

- [ ] **Step 4: 검증 표기 집계 오버로드를 더한다**

`VerificationDocumentFormatter.cs`에 추가:

```csharp
    /// <summary>
    /// 근거가 여러 편일 때 쓰는 진입점. 종료 상태를 세어 한 줄로 싣는다.
    ///
    /// [왜 필요한가] 정책서의 근거는 명세서 14편이고 그중 일부가 품질 미달일 수 있다.
    /// 단수 오버로드(sourceOutcome 하나)로는 그 사실을 실을 자리가 없어, 지금까지
    /// 정책서는 자기가 무엇 위에 서 있는지 말하지 못했다.
    /// </summary>
    public static string FormatUnverifiedDocument(
        string body,
        IReadOnlyDictionary<string, int> sourceStatusCounts,
        string provider,
        string modelName,
        string? effort,
        DateTime timestamp)
    {
        var sourceLine = sourceStatusCounts.Count > 0
            ? "근거 명세서 검증 상태: "
              + string.Join(" · ", sourceStatusCounts
                  .OrderByDescending(kv => kv.Value)
                  .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                  .Select(kv => $"{kv.Key} {kv.Value}"))
              + "\n"
            : string.Empty;

        var yamlFrontMatter = $@"---
검증 상태: 검증 없음 # 이 문서는 L1/L2 검증을 거치지 않음
{sourceLine}---

";

        var statusNote =
            "> **검증 상태**: 이 문서는 검증 파이프라인을 거치지 않았습니다. 내용을 직접 검토하십시오.\n";

        return yamlFrontMatter + MetadataHeader(provider, modelName, effort, timestamp, string.Empty, statusNote) + body;
    }
```

- [ ] **Step 5: 조립기를 만든다**

`src/ReSet.Core/Services/PolicyDocumentAssembler.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 개요·단계 본문·부록을 하나의 문서로 잇는다.
    ///
    /// [왜 AI가 조립하지 않는가] 목차와 부록은 명부와 사전에서 기계적으로 나오는 것이다.
    /// AI에게 조립을 맡기면 그 과정에서 근거가 흐려지고, 목차가 매 회차 달라져
    /// 단계 완전성 검사의 기준이 흔들린다.
    /// </summary>
    public static class PolicyDocumentAssembler
    {
        public static string Assemble(
            string overview,
            IReadOnlyList<string> stageBodies,
            SettlementCodebook codebook,
            SettlementProcessRoster roster)
        {
            var sb = new StringBuilder();
            sb.AppendLine(overview.TrimEnd());
            sb.AppendLine();

            foreach (var body in stageBodies)
            {
                sb.AppendLine(body.TrimEnd());
                sb.AppendLine();
            }

            sb.AppendLine("## 부록 A. 코드값 사전");
            sb.AppendLine();
            sb.AppendLine("| 코드값 | 의미 | 출처 | 쓰이는 프로시저 |");
            sb.AppendLine("| :--- | :--- | :--- | :--- |");
            foreach (var entry in codebook.Entries)
            {
                var meaning = entry.Matches.Count > 0
                    ? MarkdownTableCellCodec.Escape(string.Join(
                        " / ", entry.Matches.Select(m => string.Join(", ", m.Row.Select(kv => $"{kv.Key}={kv.Value}")))))
                    : entry.MatchEligible
                        ? "의미 미상 (마스터 데이터에서 찾지 못함)"
                        : "의미 미상 (값이 짧아 판별 불가)";
                var source = entry.Matches.Count > 0
                    ? string.Join(", ", entry.Matches.Select(m => m.Table))
                    : "-";

                sb.AppendLine($"| `{entry.Value}` | {meaning} | {source} | {string.Join(", ", entry.Procedures)} |");
            }

            sb.AppendLine();
            sb.AppendLine("## 부록 B. 단계별 원본 프로시저");
            sb.AppendLine();
            sb.AppendLine("| 단계 | 원본 프로시저 |");
            sb.AppendLine("| :--- | :--- |");
            foreach (var stage in roster.Stages)
            {
                sb.AppendLine($"| {stage.Title} | {string.Join(", ", stage.Procedures)} |");
            }

            if (roster.Excluded.Count > 0)
            {
                sb.AppendLine($"| (제외) | {string.Join(", ", roster.Excluded)} |");
            }

            return sb.ToString();
        }
    }
}
```

- [ ] **Step 6: 서비스를 재작성한다**

`src/ReSet.Core/Services/ISettlementPolicyService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReSet.Core.Services
{
    /// <summary>명부가 준비되지 않아 정책 도출을 시작할 수 없다.</summary>
    public sealed class PolicyRosterBlockedException : Exception
    {
        public PolicyRosterBlockedException(IReadOnlyList<RosterDefect> defects, string rosterPath)
            : base($"정산 프로세스 명부가 준비되지 않았습니다({defects.Count}건). {rosterPath}를 확인하십시오.")
        {
            Defects = defects;
            RosterPath = rosterPath;
        }

        public IReadOnlyList<RosterDefect> Defects { get; }

        public string RosterPath { get; }
    }

    public interface ISettlementPolicyService
    {
        Task<PolicyDerivationOutcome> GenerateAsync(
            string outputRoot,
            ICodeTableProfiler? profiler,
            string? effort,
            CancellationToken cancellationToken = default);
    }
}
```

`src/ReSet.Core/Services/PolicyDerivationOutcome.cs`:

```csharp
using System.Collections.Generic;

namespace ReSet.Core.Services
{
    public sealed record PolicyDerivationOutcome(
        string PolicyPath,
        string CodebookPath,
        IReadOnlyList<PolicyDefect> Defects,
        int CodeValuesTranslated,
        int CodeValuesUnmatched,
        int CodeValuesSkippedShort,
        bool ProfilingRan);
}
```

`src/ReSet.Core/Services/SettlementPolicyService.cs` — 전면 재작성. 골격은 다음과 같다.
각 단계는 앞 태스크의 산출물을 순서대로 부르기만 한다.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 이미 쌓인 명세서에서 정산 업무 인수인계 문서를 만든다.
    ///
    /// [왜 DB가 선택인가] 근거는 Spec.md 하나이고 상수 추출 보조로 쓰는 DDL 사본도
    /// raw/metadata.json에 있다. DB는 코드값의 우변(업무 의미)에만 쓰이며, 없으면
    /// 「의미 미상」으로 표기된 채 문서가 완주한다. 그 덕에 파이프라인 전체가
    /// DB 없이 테스트된다.
    ///
    /// [왜 L2가 없는가] PrdDerivationService와 같다 - 수렴하지 않는 루프를 새로 만드는
    /// 대신 단계마다 한 번 되돌리고, 남은 결함은 배너에 박아 사람 검토로 넘긴다.
    /// </summary>
    public sealed class SettlementPolicyService : ISettlementPolicyService
    {
        public const string RosterFileName = "settlement-process.md";
        public const string PolicyDirectoryName = "Policy";
        public const string PolicyFileName = "SettlementPolicy.md";
        public const string CodebookFileName = "settlement-codebook.json";

        private readonly IAiService _aiService;

        public SettlementPolicyService(IAiService aiService) =>
            _aiService = aiService ?? throw new ArgumentNullException(nameof(aiService));

        public async Task<PolicyDerivationOutcome> GenerateAsync(
            string outputRoot,
            ICodeTableProfiler? profiler,
            string? effort,
            CancellationToken cancellationToken = default)
        {
            // 1. 재료 적재
            var targets = PolicyTargetDiscovery.Find(outputRoot);
            var sources = targets
                .Select(PolicyCorpusLoader.Load)
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            if (sources.Count == 0)
            {
                throw new InvalidOperationException(
                    "명세서가 있는 분석 산출물이 없습니다. 개별 SP 분석을 먼저 수행하십시오.");
            }

            // 2. 명부 - 없으면 초안을 쓰고 멈춘다(덮어쓰지 않는다)
            var rosterPath = Path.Combine(outputRoot, RosterFileName);
            if (!File.Exists(rosterPath))
            {
                await File.WriteAllTextAsync(
                    rosterPath, SettlementProcessRosterDraft.Build(sources), cancellationToken);
                throw new PolicyRosterBlockedException(
                    new[]
                    {
                        new RosterDefect(
                            RosterDefectType.PlaceholderTitleRemaining, RosterFileName,
                            "명부 초안을 만들었습니다. 단계 이름과 순서를 채운 뒤 다시 실행하십시오."),
                    },
                    rosterPath);
            }

            var roster = SettlementProcessRosterParser.Parse(
                await File.ReadAllTextAsync(rosterPath, cancellationToken));

            var rosterDefects = SettlementRosterReconciler.Reconcile(
                roster, sources.Select(s => s.Label).ToList());
            if (rosterDefects.Count > 0)
            {
                throw new PolicyRosterBlockedException(rosterDefects, rosterPath);
            }

            // 3. 코드값 사전
            var staged = roster.AllStagedProcedures().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stagedSources = sources.Where(s => staged.Contains(s.Label)).ToList();
            var codebook = SettlementCodebookBuilder.BuildLeftSide(stagedSources);

            var profilingRan = false;
            if (profiler is not null)
            {
                var dependencies = stagedSources.SelectMany(s => s.Dependencies).ToList();
                var tables = await profiler.ProfileAsync(dependencies, cancellationToken);
                codebook = SettlementCodebookBuilder.ApplyMatches(codebook, tables);
                profilingRan = true;
            }

            // 4. 단계별 서술 - 실패하면 그 단계만 교정 재호출 1회
            var specsByLabel = stagedSources.ToDictionary(
                s => s.Label, s => s.SpecMarkdown, StringComparer.OrdinalIgnoreCase);
            var stageHeadings = roster.Stages.Select(s => "## " + s.Title).ToList();
            var stageBodies = new List<string>();

            for (var i = 0; i < roster.Stages.Count; i++)
            {
                var stage = roster.Stages[i];
                var stageNumber = PolicySectionContract.StageNumberOf(stage.Title) is var n and > 0 ? n : i + 1;

                var stageSources = stage.Procedures
                    .Select(p => stagedSources.First(s =>
                        string.Equals(s.Label, p, StringComparison.OrdinalIgnoreCase)))
                    .Select(s => (s.Label, s.SpecMarkdown))
                    .ToList();

                var stageCodeValues = codebook.Entries
                    .Where(e => e.Procedures.Any(p => stage.Procedures.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    .ToList();

                var body = await GenerateStageWithOneRepairAsync(
                    stageNumber, stage, stageSources, stageCodeValues, stageHeadings, specsByLabel,
                    effort, cancellationToken);

                stageBodies.Add(body);
                await WriteStagePartAsync(outputRoot, stageNumber, stage.Title, body, cancellationToken);
            }

            // 5. 개요와 조립
            var assembledStages = string.Join("\n\n", stageBodies);
            var overview = (await _aiService.GeneratePolicyOverviewAsync(
                roster.Stages.Select(s => s.Title).ToList(), assembledStages, effort, cancellationToken))
                .Content ?? "## 정산 업무 개요\n";

            var documentBody = PolicyDocumentAssembler.Assemble(overview, stageBodies, codebook, roster);

            // 6. 최종 검사와 배너
            var rules = PolicyDocumentParser.Parse(documentBody, stageHeadings);
            var defects = PolicyAttributionValidator
                .Validate(documentBody, stageHeadings, specsByLabel).Defects
                .Concat(PolicyDocumentChecks.CheckCodeValues(rules, codebook))
                .Concat(PolicyDocumentChecks.CheckProcedureCitationCoverage(rules, roster))
                .ToList();

            var translated = codebook.Entries.Count(e => e.Matches.Count > 0);
            var skippedShort = codebook.Entries.Count(e => !e.MatchEligible);
            var unmatched = codebook.Entries.Count - translated - skippedShort;

            var banner = PolicyReportBanner.Build(defects, translated, unmatched, skippedShort, profilingRan);

            var sourceStatusCounts = CountSourceStatuses(stagedSources);
            var document = VerificationDocumentFormatter.FormatUnverifiedDocument(
                banner + documentBody, sourceStatusCounts,
                _aiService.ProviderName, _aiService.ModelName, effort, DateTime.Now);

            // 7. 저장
            var policyDir = Path.Combine(outputRoot, PolicyDirectoryName);
            Directory.CreateDirectory(policyDir);

            var policyPath = Path.Combine(policyDir, PolicyFileName);
            await File.WriteAllTextAsync(policyPath, document, cancellationToken);

            var codebookPath = Path.Combine(policyDir, CodebookFileName);
            await File.WriteAllTextAsync(
                codebookPath,
                JsonSerializer.Serialize(codebook, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }),
                cancellationToken);

            Log.Information(
                "정산 정책서 생성 완료 - 단계 {Stages}개, 결함 {Defects}건, 코드값 번역 {Translated}건",
                roster.Stages.Count, defects.Count, translated);

            return new PolicyDerivationOutcome(
                policyPath, codebookPath, defects, translated, unmatched, skippedShort, profilingRan);
        }

        /// <summary>
        /// 단계 하나를 생성하고, 귀속 결함이 있으면 한 번만 교정 재호출한다.
        /// 결함이 늘면 첫 초안을 지킨다 - 결함 수가 유일하게 비교 가능한 척도다
        /// (PrdDerivationService와 같은 규칙).
        /// </summary>
        private async Task<string> GenerateStageWithOneRepairAsync(
            int stageNumber,
            PolicyStage stage,
            IReadOnlyList<(string Label, string SpecMarkdown)> stageSources,
            IReadOnlyList<CodebookEntry> stageCodeValues,
            IReadOnlyList<string> stageHeadings,
            IReadOnlyDictionary<string, string> specsByLabel,
            string? effort,
            CancellationToken cancellationToken)
        {
            var draft = (await _aiService.GeneratePolicyStageAsync(
                stageNumber, stage.Title, stageSources, stageCodeValues, null, effort, cancellationToken))
                .Content ?? string.Empty;

            var validation = PolicyAttributionValidator.Validate(draft, stageHeadings, specsByLabel);
            if (validation.IsValid)
            {
                return draft;
            }

            Log.Information(
                "정책 단계 귀속 검사 미통과 - 단계 {Stage}, 결함 {Count}건. 교정 재호출 1회를 시도합니다.",
                stage.Title, validation.Defects.Count);

            var feedback = string.Join("\n", validation.Defects.Select(d => $"- [{d.RuleId}] {d.Message}"));
            var retry = (await _aiService.GeneratePolicyStageAsync(
                stageNumber, stage.Title, stageSources, stageCodeValues, feedback, effort, cancellationToken))
                .Content ?? string.Empty;

            var retryValidation = PolicyAttributionValidator.Validate(retry, stageHeadings, specsByLabel);
            return retryValidation.Defects.Count <= validation.Defects.Count ? retry : draft;
        }

        private static async Task WriteStagePartAsync(
            string outputRoot, int stageNumber, string title, string body, CancellationToken cancellationToken)
        {
            var stepsDir = Path.Combine(outputRoot, PolicyDirectoryName, "steps");
            Directory.CreateDirectory(stepsDir);

            var slug = string.Concat(title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
            var path = Path.Combine(stepsDir, $"{stageNumber:D2}-{slug}.md");
            await File.WriteAllTextAsync(path, body, cancellationToken);
        }

        /// <summary>근거 명세서의 종료 상태를 센다. 상태 표기가 없으면 「알 수 없음」으로 센다.</summary>
        private static IReadOnlyDictionary<string, int> CountSourceStatuses(IReadOnlyList<PolicySource> sources)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var status = SpecHeaderReader.Read(source.SpecMarkdown).VerificationStatus ?? "알 수 없음";
                counts[status] = counts.TryGetValue(status, out var n) ? n + 1 : 1;
            }

            return counts;
        }
    }
}
```

- [ ] **Step 7: 단계가 너무 커지면 경고한다 (설계서 §8-1)**

한 단계에 SP가 몰리면 그 단계의 명세서 합계가 커져 단발 호출로 나눈 의미가 없어진다.
**자동으로 나누지 않는다** — 목차는 사람이 명부에 정한 것이고 도구가 그것을 바꾸면
§3의 원칙과 충돌한다. 경고만 하고 사람에게 맡긴다.

`SettlementPolicyService`에 상수와 검사를 더한다:

```csharp
        /// <summary>
        /// 한 단계에 실리는 명세서 합계의 경고선.
        ///
        /// 코퍼스 14편의 합계가 421,121자다. 한 단계가 그 3할에 가까워지면 단계로
        /// 나눈 이득이 사라지므로 사람에게 알린다. 자동 분할은 하지 않는다 - 목차는
        /// 사람이 명부에 정한 것이고, 도구가 그것을 바꾸면 단계 완전성 검사의 기준이
        /// 흔들린다.
        /// </summary>
        public const int StageSpecCharWarningThreshold = 120_000;
```

단계 루프 안, `GenerateStageWithOneRepairAsync`를 부르기 **직전**에 넣는다:

```csharp
                var stageChars = stageSources.Sum(s => s.SpecMarkdown.Length);
                if (stageChars > StageSpecCharWarningThreshold)
                {
                    Log.Warning(
                        "정책 단계가 큽니다 - '{Stage}'의 명세서 합계 {Chars:N0}자 (경고선 {Threshold:N0}자). "
                        + "명부에서 이 단계를 더 잘게 나누면 서술 품질이 올라갑니다.",
                        stage.Title, stageChars, StageSpecCharWarningThreshold);
                }
```

테스트를 `SettlementPolicyServiceTests`에 더한다:

```csharp
        [Fact]
        public async Task 단계가_경고선을_넘으면_생성은_계속하되_경고한다()
        {
            // 경고선을 넘는 큰 명세서를 심는다. 생성이 막히지 않는 것이 이 테스트의 요지다 -
            // 자동 분할을 하지 않기로 했으므로 경고는 로그로만 나가고 문서는 나와야 한다.
            var big = "## 개요\n\n" + new string('가', SettlementPolicyService.StageSpecCharWarningThreshold + 1)
                      + "\n요율을 적재한다.\n";
            File.WriteAllText(
                Path.Combine(_root, "Procedures", "dbo.UP_A", "docs", "Spec.md"),
                "---\n검증 상태: 통과\n---\n\n" + big);
            WriteRoster();

            var service = new SettlementPolicyService(
                AiWriting(Stage(1, "요율 적재", "dbo.UP_A", "요율을 적재"),
                          Stage(2, "원장 적재", "dbo.UP_B", "원장을 적재")));

            var outcome = await service.GenerateAsync(_root, profiler: null, effort: null);

            Assert.True(File.Exists(outcome.PolicyPath));
        }
```

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementPolicyServiceTests`
Expected: 실패 0 · 건너뜀 0

- [ ] **Step 8: 배너 생성기를 만든다**

`src/ReSet.Core/Services/PolicyReportBanner.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 문서 상단의 배너 셋을 만든다 - 귀속 결함, 코드값 커버리지, 미인용 SP.
    ///
    /// [순서 주의] 이 배너는 FormatUnverifiedDocument의 body 인자로 들어가야 한다.
    /// 반환값 앞에 이어붙이면 YAML 프런트매터가 오프셋 0을 잃어 가로줄로 렌더링된다
    /// (PrdDerivationService에 같은 주석이 있다).
    /// </summary>
    public static class PolicyReportBanner
    {
        public static string Build(
            IReadOnlyList<PolicyDefect> defects,
            int translated,
            int unmatched,
            int skippedShort,
            bool profilingRan)
        {
            var sb = new StringBuilder();

            var attribution = defects
                .Where(d => d.Type != PolicyDefectType.ProcedureNeverCited)
                .ToList();

            if (attribution.Count > 0)
            {
                sb.AppendLine("> [!WARNING]");
                sb.AppendLine($"> **[귀속 검사 미통과] {attribution.Count}건** — 아래 자리는 근거가 원본 명세서에서 확인되지 않았습니다.");
                foreach (var defect in attribution.Take(20))
                {
                    sb.AppendLine($"> - [{defect.RuleId}] {defect.Message}");
                }

                if (attribution.Count > 20)
                {
                    sb.AppendLine($"> - (외 {attribution.Count - 20}건)");
                }

                sb.AppendLine();
            }

            sb.AppendLine("> [!NOTE]");
            sb.AppendLine(profilingRan
                ? $"> **코드값 번역**: 매칭 대상 {translated + unmatched}개 중 {translated}개 번역 · {unmatched}개 의미 미상 (값이 짧아 대상에서 제외한 것 {skippedShort}개)"
                : $"> **코드값 번역**: 0건 (DB 미연결) — 코드 상수 {translated + unmatched}개의 업무 의미가 비어 있습니다. (값이 짧아 대상에서 제외한 것 {skippedShort}개)");
            sb.AppendLine();

            var neverCited = defects
                .Where(d => d.Type == PolicyDefectType.ProcedureNeverCited)
                .ToList();

            if (neverCited.Count > 0)
            {
                sb.AppendLine("> [!WARNING]");
                sb.AppendLine($"> **[미인용 프로시저] {neverCited.Count}건** — 명부에 실렸으나 어떤 규칙의 근거로도 인용되지 않았습니다. 그 업무 규칙이 문서에서 빠졌을 수 있습니다.");
                foreach (var defect in neverCited)
                {
                    sb.AppendLine($"> - {defect.Subject}");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
```

- [ ] **Step 9: 통과를 확인한다**

Run: `dotnet build && dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~SettlementPolicyServiceTests`
Expected: 실패 0 · 건너뜀 0. `Program.cs`는 아직 깨져 있으므로 `dotnet build`는
`ReSet.Cli`에서만 오류가 난다 — Task 11이 닫는다.

- [ ] **Step 10: 역변이로 효력을 확인한다**

명부 파일 존재 검사(`if (!File.Exists(rosterPath))`)를 `if (false)`로 바꾸고 돌린다.
Expected: `명부가_없으면_초안을_쓰고_중단한다` 실패.
`rosterDefects.Count > 0` 던지기를 지우고 돌린다 → `명부에_빠진_SP가_있으면_중단한다` 실패.
초안 쓰기 앞의 존재 검사를 지워 항상 덮어쓰게 하고 돌린다 → `기존_명부를_덮어쓰지_않는다` 실패.
셋 다 되돌려 초록을 확인하고 **되돌린 상태로** 다음 태스크로 간다.

- [ ] **Step 11: 커밋 (Task 9의 변경과 함께)**

```bash
git add src/ReSet.Core/Services/ src/ReSet.Cli/SpecHeaderReader.cs \
        tests/ReSet.Core.Tests/PolicyPromptTests.cs \
        tests/ReSet.Core.Tests/SettlementPolicyServiceTests.cs
git commit -m "feat: 정산 정책 도출을 명세서 기반 오프라인 파이프라인으로 재작성

DB는 코드값의 우변에만 쓰이고 없으면 '의미 미상'으로 완주한다.
목차는 사람이 명부에 붙인 단계 이름이고 조립은 기계가 한다.
근거 명세서의 검증 상태를 헤더에 집계해, 정책서가 무엇 위에 서 있는지
문서 첫 줄에서 보이게 했다.
역변이 셋(명부 존재검사 무력화, 대조 결함 무시, 초안 덮어쓰기) 확인:
각각 대응 테스트만 실패 후 복구."
```

---

## Task 11: CLI 배선

**Files:**
- Modify: `src/ReSet.Cli/Program.cs` (메뉴 4 블록 `1716-1852`, 배치 `--policy` 블록 `763-820`, 인자 파싱 `146-160`, 서비스 조립 `731`)
- Modify: `src/ReSet.Cli/CliArgs.cs`
- Test: `tests/ReSet.Core.Tests/PolicyCliArgumentTests.cs`

**Interfaces:**
- Consumes: `ISettlementPolicyService.GenerateAsync`, `PolicyRosterBlockedException`, `PolicyDerivationOutcome`, `CodeTableProfiler` (Task 6·10)
- Produces: 없음(최종 배선)

- [ ] **Step 1: `--policy-sps` 폐기를 재는 테스트를 쓴다**

`CliArgs`와 인자 파서가 `ReSet.Cli`에 있으므로, 파서를 테스트에서 부를 수 있는 형태인지 먼저
확인한다. `Program.ParseArguments`가 private이면 **`internal static`으로 바꾸고**
`ReSet.Cli.csproj`에 다음을 더한다:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="ReSet.Core.Tests" />
  </ItemGroup>
```

`tests/ReSet.Core.Tests/PolicyCliArgumentTests.cs`:

```csharp
using System;
using ReSet.Cli;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PolicyCliArgumentTests
    {
        // 조용히 무시하면 사용자는 자기가 지정한 SP만 들어간 줄 안다.
        [Fact]
        public void 폐기된_policy_sps를_주면_오류로_중단한다()
        {
            var ex = Assert.Throws<ArgumentException>(
                () => Program.ParseArguments(new[] { "--policy", "--policy-sps", "dbo.A" }));

            Assert.Contains("settlement-process.md", ex.Message);
        }

        [Fact]
        public void policy만_주면_배치_모드가_켜진다()
        {
            var args = Program.ParseArguments(new[] { "--policy" });

            Assert.True(args.GeneratePolicy);
            Assert.True(args.IsBatchMode);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

Run: `dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PolicyCliArgumentTests`
Expected: 컴파일 실패 또는 `ArgumentException`이 안 던져져 실패

- [ ] **Step 3: CliArgs에서 PolicyProcedures를 지운다**

`src/ReSet.Cli/CliArgs.cs`에서 다음 줄을 지운다:

```csharp
        public List<string> PolicyProcedures { get; set; } = new();
```

`IsBatchMode`가 `PolicyProcedures`를 보고 있으면 그 항목도 뺀다(`GeneratePolicy`만 남긴다).

- [ ] **Step 4: 인자 파서를 고친다**

`Program.cs`의 `--policy-sps` 분기(약 150~160행)를 다음으로 바꾼다:

```csharp
                else if (arg.Equals("--policy-sps", StringComparison.OrdinalIgnoreCase))
                {
                    // 조용히 무시하지 않는다 - 무시하면 사용자는 자기가 지정한 SP만
                    // 들어간 줄 안다. 대상은 이제 명부 파일이 정한다.
                    throw new ArgumentException(
                        "--policy-sps는 폐기되었습니다. 정책 도출 대상과 순서는 output/settlement-process.md가 정합니다.");
                }
```

- [ ] **Step 5: 서비스 조립과 배치 블록을 고친다**

`Program.cs:731`의 서비스 조립을 바꾼다:

```csharp
            ISettlementPolicyService policyService = new SettlementPolicyService(aiService);
            ICodeTableProfiler? codeTableProfiler = string.IsNullOrEmpty(connectionString)
                ? null
                : new CodeTableProfiler(dbService, connectionString);
```

배치 `--policy` 블록(약 763~820행)의 본문을 다음으로 바꾼다:

```csharp
                if (cliArgs.GeneratePolicy)
                {
                    AnsiConsole.MarkupLine("[bold blue]=== 정산 정책 문서 도출 시작 ===[/]");

                    try
                    {
                        PolicyDerivationOutcome? outcome = null;
                        await AnsiConsole.Status()
                            .StartAsync("정산 정책 문서 생성 중...", async ctx =>
                            {
                                outcome = await policyService.GenerateAsync(
                                    outputDir, codeTableProfiler, actorEffort, globalCts.Token);
                            });

                        if (outcome is not null)
                        {
                            AnsiConsole.MarkupLine(
                                $"[green]성공: 정산 정책 문서 생성 완료![/] {Markup.Escape(outcome.PolicyPath)}");
                            AnsiConsole.MarkupLine(
                                $"[grey]코드값 번역 {outcome.CodeValuesTranslated}건 · 결함 {outcome.Defects.Count}건[/]");
                        }
                    }
                    catch (PolicyRosterBlockedException ex)
                    {
                        AnsiConsole.MarkupLine("[yellow]정산 프로세스 명부가 준비되지 않아 중단했습니다.[/]");
                        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ex.RosterPath)}[/]");
                        foreach (var defect in ex.Defects)
                        {
                            AnsiConsole.MarkupLine($"  [grey]- {Markup.Escape(defect.Message)}[/]");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AnsiConsole.MarkupLine(
                            $"[red]에러: 정산 정책 문서 도출 실패:[/] {Markup.Escape(ex.Message)}");
                    }
                }
```

- [ ] **Step 6: TUI 메뉴 4 블록을 교체한다**

`Program.cs:1716-1852`의 `else if (selectedMenu.StartsWith("4"))` 블록 **전체**를 다음으로
바꾼다. 순차 선택 루프와 Job 이름 프롬프트가 통째로 사라진다.

```csharp
                    else if (selectedMenu.StartsWith("4"))
                    {
                        var rosterPath = Path.Combine(outputDir, SettlementPolicyService.RosterFileName);
                        if (File.Exists(rosterPath))
                        {
                            var roster = SettlementProcessRosterParser.Parse(
                                await File.ReadAllTextAsync(rosterPath));
                            AnsiConsole.Write(new Panel(new Markup(
                                $"[bold]명부:[/] {Markup.Escape(rosterPath)}\n"
                                + $"단계 {roster.Stages.Count}개 · 프로시저 {roster.AllStagedProcedures().Count()}개 · 제외 {roster.Excluded.Count}개"))
                            {
                                Border = BoxBorder.Rounded,
                                Header = new PanelHeader(" 정산 프로세스 명부 "),
                            });

                            if (!AnsiConsole.Confirm("이 명부로 정책 문서를 생성할까요?", true))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine(
                                "[yellow]정산 프로세스 명부가 없습니다. 초안을 만들고 안내합니다.[/]");
                        }

                        using var activeCts = new CancellationTokenSource();
                        _currentCts = activeCts;

                        try
                        {
                            PolicyDerivationOutcome? outcome = null;
                            await AnsiConsole.Status()
                                .StartAsync("정산 정책 문서 생성 중...", async ctx =>
                                {
                                    outcome = await policyService.GenerateAsync(
                                        outputDir, codeTableProfiler, actorEffort, activeCts.Token);
                                });

                            if (outcome is not null)
                            {
                                AnsiConsole.Write(new Panel(new Markup(
                                    $"[green]정산 정책 문서가 생성되었습니다![/]\n"
                                    + $"[bold]저장 경로:[/] {Markup.Escape(outcome.PolicyPath)}\n"
                                    + $"[bold]코드값 사전:[/] {Markup.Escape(outcome.CodebookPath)}\n"
                                    + $"코드값 번역 {outcome.CodeValuesTranslated}건 · 의미 미상 {outcome.CodeValuesUnmatched}건 · 결함 {outcome.Defects.Count}건"))
                                {
                                    Border = BoxBorder.Rounded,
                                    Header = new PanelHeader(" 정책 분석 완료 "),
                                });
                            }
                        }
                        catch (PolicyRosterBlockedException ex)
                        {
                            AnsiConsole.MarkupLine("\n[yellow]정산 프로세스 명부가 준비되지 않아 중단했습니다.[/]");
                            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ex.RosterPath)}을 열어 다음을 고친 뒤 다시 실행하십시오:[/]");
                            foreach (var defect in ex.Defects)
                            {
                                AnsiConsole.MarkupLine($"  [grey]- {Markup.Escape(defect.Message)}[/]");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            AnsiConsole.MarkupLine("\n[yellow]정책 문서 도출이 중단되었습니다. 메인 메뉴로 돌아갑니다.[/]");
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]에러:[/] 정책 문서 도출 중 오류 발생: {Markup.Escape(ex.Message)}");
                        }
                        finally
                        {
                            _currentCts = globalCts;
                        }

                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[yellow]아무 키나 누르면 메인 메뉴로 돌아갑니다...[/]");
                        Console.ReadKey(true);
                    }
```

- [ ] **Step 7: 빌드와 전체 테스트**

Run: `dotnet build && dotnet test`
Expected: 실패 0 · 건너뜀 0 · 빌드 경고 0.
`CancellationPolicyTests`가 새 `catch`를 지적하면 기준선 파일
`tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`의 해당 파일 숫자를 실제에 맞춰 내린다
(**올리지 말고 내린다** — 새 위반을 허용하는 방향이면 `when (ex is not OperationCanceledException)`을
붙여 위반 자체를 없앤다).

- [ ] **Step 8: 실물로 한 번 돌려 본다**

Run: `dotnet run --project src/ReSet.Cli -- --policy`

DB가 없으면 명부 초안이 `output/settlement-process.md`에 생기고 중단되는지 확인한다.
그 초안을 열어 단계 이름을 채운 뒤 다시 돌려, `output/Policy/SettlementPolicy.md`와
`output/Policy/settlement-codebook.json`이 생기는지 확인한다.

**이 실행으로 생긴 산출물은 커밋하지 않는다.** 확인 후 지운다:

```bash
rm -rf output/Policy output/settlement-process.md
```

- [ ] **Step 9: 커밋**

```bash
git add src/ReSet.Cli/ tests/ReSet.Core.Tests/PolicyCliArgumentTests.cs
git commit -m "feat: 메뉴 4의 순차 선택 루프를 명부 확인으로 대체한다

SP를 하나씩 고르는 화면과 Job 이름 프롬프트가 사라진다. --policy-sps는
조용히 무시하지 않고 오류로 중단한다 - 무시하면 사용자는 자기가 지정한
SP만 들어간 줄 안다."
```

---

## Task 12: 문서 동기화

**Files:**
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: 전 태스크의 최종 동작
- Produces: 없음

- [ ] **Step 1: `reset-doc-sync` 스킬을 부른다**

Run: 스킬 `reset-doc-sync`를 호출한다. 그 스킬이 세 문서의 동기화 절차를 소유하고 있으므로
여기서 절차를 다시 쓰지 않는다.

- [ ] **Step 2: 스킬이 놓칠 수 있는 자리를 직접 확인한다**

아래는 이번 변경으로 **거짓이 되는 문장들**이다. 스킬 수행 뒤 각각이 고쳐졌는지 확인한다.

| 파일 | 지금 문장 | 왜 거짓이 되는가 |
| --- | --- | --- |
| `AGENTS.md` 9번 규칙 | "정산 정책서는 DDL 분기와 데이터 프로파일링을 결합해 지정된 5개 헤더를 따릅니다" | 근거가 Spec.md로 바뀌었고 헤더는 명부가 정한다 |
| `README.md` §7 | "Stored Procedure 코드(DDL)에 숨겨진 비즈니스 분기 조건과 … 데이터를 1:1 결합" | DDL을 다시 긁지 않는다 |
| `README.md` 메뉴 4 설명 | "분석할 SP들을 순차 선택하고 Job 이름을 입력하여" | 둘 다 사라졌다 |
| `README.md` `--policy-sps` | "정산 정책 도출에 쓰일 분석 대상 SP들을 지정합니다" | 폐기됐고 주면 오류다 |
| `README.md` `--job-name` | "정산 정책 문서 작성 시에도 파일명 접두사로 기능합니다" | 접두사가 없어졌다 |
| `README.md` 출력 트리 | `[Job이름]_Settlement_Policy_Rulebook.md` | `Policy/SettlementPolicy.md`로 바뀌었다 |
| `docs/architecture.md` §5 흐름도 | `PathPolicy["DDL 상수 분기 분석 + 마스터 데이터 프로파일링 ➔ 정산 정책서 저장"]` | 흐름이 바뀌었다 |
| `docs/architecture.md` §4.10 | 메커니즘 서술 전체 | 전면 교체 |
| `docs/architecture.md` 서비스 표 | `SettlementPolicyService` 행 | "DDL 상수 분석 및 DB 마스터 데이터 프로파일링을 결합"이 거짓이 된다 |

`AGENTS.md`는 바이트 상한이 걸린 문서다. 9번 규칙을 늘리지 말고, 늘려야 하면 근거를
`docs/architecture.md`로 옮긴다. **상한 상향은 사람이 결정한다.**

- [ ] **Step 3: 새 문서 자리를 아키텍처 표에 더한다**

`docs/architecture.md`의 서비스 표에 새 클래스 행을 더한다 — `PolicyTargetDiscovery`,
`SettlementProcessRosterParser`/`Draft`, `SettlementRosterReconciler`,
`SettlementCodebookBuilder`, `CodeTableProfiler`, `PolicyAttributionValidator`,
`PolicyDocumentChecks`, `EvidenceQuoteMatcher`, `PolicyDocumentAssembler`.
각 행에는 **왜 그렇게 했는지**를 한 줄 담는다(이 표의 기존 행들이 전부 그렇게 되어 있다).

- [ ] **Step 4: 문서에 실린 수치가 실측과 맞는지 확인한다**

이번 변경 서술에 수치를 쓸 때는 **분자의 이름·분모의 관할**을 함께 적는다. 이 계획서가 쓰는
값은 다음이며, 다른 값을 쓰려면 다시 재고 나서 쓴다.

- 명세서 14편, 합계 421,121자(약 19만 토큰)
- DDL 문자열 비교 리터럴이 Spec.md에 살아 있는 비율: **(SP, 상수) 출현 기준 114/115건**
- 고유 상수 값 82개, 그중 정규식이 잘못 잡은 동적 SQL 조각 18개, 실질 64개, 길이 3 이상 51개
- 컬럼까지 잡히는 값 29개 / 82개
- EXEC 간선 2개, 쓰기→읽기 간선 99개(허브 둘 제외 시 5개)
- 의존 테이블 37개, 종전 이름 휴리스틱에 걸리는 것 9개(전부 요율표)

- [ ] **Step 5: 전체 테스트와 커밋**

Run: `dotnet build && dotnet test`
Expected: 실패 0 · 건너뜀 0 · 빌드 경고 0

```bash
git add README.md docs/architecture.md AGENTS.md
git commit -m "docs: 정산 정책 도출 재설계를 문서에 반영한다

AGENTS.md 9번 규칙의 'DDL 분기와 데이터 프로파일링을 결합해 5개 헤더',
README §7의 '1:1 결합', --policy-sps와 --job-name 접두사 설명이
이번 변경으로 전부 거짓이 되어 고친다."
```
