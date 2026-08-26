# 표 블록 헤더 대조 이식 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `MechanicalValidator`의 표 대조가 헤딩 절 전체의 `|` 줄을 뭉뚱그려 모으는 탓에 인접 블록의 우연한 토큰 일치가 진짜 결손을 덮는 거짓 음성을, 헤더로 자기 표 블록을 특정해 막는다.

**Architecture:** 브랜치 `worktree-agent-af1fbecfcf4e8d9d5`(`032031b`)에서 리뷰 3라운드를 거친 헬퍼 셋을 고치지 않고 옮긴다. 헤더 셀은 검사 쪽에 복사하지 않고 **추출기가 소유하는 공유 상수**로 올려 프롬프트와 검사가 같은 문자열을 보게 한다. 어느 검사에 이식할지는 코퍼스 측정이 정하고, 이 계획서는 **측정 + 첫 검사 하나**까지만 다룬다.

**Tech Stack:** .NET 10, xUnit, NSubstitute

**Spec:** `docs/superpowers/specs/2026-08-26-table-block-matching-port-design.md`

## Global Constraints

- **프롬프트 본문은 접두사 캐싱과 얽혀 있다.** 상수 조립 결과가 지금 리터럴과 **선행 공백 세 칸을 포함해 바이트 단위로** 같아야 한다.
- **브랜치 B의 코드는 고치지 않고 옮긴다.** 리뷰 3라운드를 거친 코드이고, 그중 한 라운드는 뮤테이션 잠금이 헛돌던 것을 스스로 찾아 고친 것이다. 손질하면 그 검증이 무효가 된다. 옮기고, 필요하면 그다음에 별건으로 고친다.
- **긴 문서 주석을 그대로 옮긴다.** 헬퍼의 주석이 리뷰 재현 사례(P2·P10)를 담고 있다. 요약하지 말 것.
- **코퍼스 검증은 심링크 둘을 걸고 건너뜀 0에서 한다.** 건너뜀 15면 아예 안 걸린 것, 2면 둘째 링크가 안 걸린 것이다.
  ```bash
  ln -s /Users/payletter/git-root/ReSet/output output
  ln -s /Users/payletter/git-root/ReSet/output.bak-2026-08-22 output.bak-2026-08-22
  ```
- **`git stash` 금지.** 스택이 메인 체크아웃·다른 워크트리와 공유되고 다른 세션이 동시에 작업할 수 있다.
- 기준선: `dotnet build` 경고 0·오류 0, `dotnet test` 실패 0 / 통과 3001 / 건너뜀 0.

---

## File Structure

| 파일 | 책임 | 태스크 |
|---|---|---|
| `src/ReSet.Core/Services/TransactionBoundaryExtractor.cs` | `TableHeading` 옆에 `TableHeaderCells` 추가 | 2 |
| `src/ReSet.Core/Services/AiService.cs:1273` | 헤더 리터럴을 상수 조립으로 교체 | 2 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 헬퍼 셋 추가, `CheckTransactionBoundaries` 수집부 교체 | 3 |
| `tests/ReSet.Core.Tests/TableBlockMatchingTests.cs` | 새 파일 — 헬퍼와 검사 동작 | 3 |
| `tests/ReSet.Core.Tests/PromptTableHeaderParityTests.cs` | 새 파일 — 바이트 등가성 | 2 |
| `docs/audit-reports/sweeps/2026-08-26-table-block-sweep.md` | 측정 보고서 | 1 |

`MechanicalValidator.cs`는 이미 매우 크지만 이 계획에서 쪼개지 않는다 — 14개 검사가 공유 헬퍼를 쓰는 구조라 분리하면 이 작업의 범위를 훨씬 넘는다.

---

### Task 1: 코퍼스 측정과 보고서

**Files:**
- Create (임시, 끝나면 삭제): `tests/ReSet.Core.Tests/TempTableBlockProbe.cs`
- Create: `docs/audit-reports/sweeps/2026-08-26-table-block-sweep.md`

**Interfaces:**
- Consumes: 없음
- Produces: 보고서. Task 3 이후 물결의 범위가 여기서 정해진다. 이 계획서는 물결을 다루지 않는다.

**배경:** 같은 순진한 수집 모양이 14개 검사에 있다. **14개가 다 결함인지는 아무도 재지 않았다.** 절에 표가 하나뿐인 검사라면 블록 구분이 무의미해 영향이 없다.

- [ ] **Step 1: 14개 검사의 좌표를 조사해 표로 적는다**

`src/ReSet.Core/Services/MechanicalValidator.cs`에서 `StartsWith("|"`를 전부 찾고, 각각이 어느 메서드 안인지 확인한다. 기대 목록(이 값이 다르면 보고하라):

```
CheckInsertMappingTableNames   CheckParameterColumnClaims    CheckParameterTableRows
CheckDmlScopeTable             CheckSetPredicates            CheckReferencedFunctionsCore
CheckLockHints                 CheckExecutionSemantics       CheckCaseBranches
CheckTransactionBoundaries     CheckSetAssignments           CheckErrorCodes
ReportTableShapeBreaks
```

각 검사마다 셋을 적는다.

1. **절 앵커** — `LocateHeadingSection(lines, X)`의 `X`. 예: `CheckTransactionBoundaries`는 `TransactionBoundaryExtractor.TableHeading`
2. **추출기** — 그 헤딩 상수를 소유한 클래스. 없으면 "없음"
3. **프롬프트 렌더 지점** — `src/ReSet.Core/Services/AiService.cs`에서 그 표의 헤더 행이 렌더되는 줄 번호. 없으면 "없음"

**"있을 것이다"로 넘어가지 말 것.** 셋 다 실물을 찾아 좌표를 적는다. 못 찾으면 "없음"이 답이고, 그것도 결과다.

- [ ] **Step 2: 프로브를 만든다**

`tests/ReSet.Core.Tests/TempTableBlockProbe.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace ReSet.Core.Tests
{
    public class TempTableBlockProbe
    {
        private readonly ITestOutputHelper _out;
        public TempTableBlockProbe(ITestOutputHelper output) => _out = output;

        // Step 1에서 조사한 (검사 이름, 절 앵커 문자열) 쌍을 여기 채운다.
        // 앵커가 상수면 그 상수를 그대로 참조한다 - 문자열을 손으로 베끼지 말 것.
        private static readonly (string Check, string Heading)[] Anchors =
        {
            ("CheckTransactionBoundaries", TransactionBoundaryExtractor.TableHeading),
            ("CheckSetAssignments", SetAssignmentExtractor.TableHeading),
            // Step 1 결과의 나머지를 여기에 추가
        };

        [SkippableFact]
        public void Probe()
        {
            var root = CorpusSkip.FindCorpusRoot();
            Skip.If(root is null, CorpusSkip.Reason);

            var counts = new Dictionary<string, (int docs, int multi)>();

            foreach (var file in Directory.EnumerateFiles(
                Path.Combine(root!, "Jobs"), "Spec.md", SearchOption.AllDirectories))
            {
                var markdown = File.ReadAllText(file);
                var lines = MarkdownSectionLocator.SplitLines(markdown);

                foreach (var (check, heading) in Anchors)
                {
                    var (start, end) = MechanicalValidator.LocateHeadingSectionForProbe(lines, heading);
                    if (start < 0) continue;

                    var blocks = CountBlocks(lines, start + 1, end);
                    var prev = counts.TryGetValue(check, out var v) ? v : (0, 0);
                    counts[check] = (prev.docs + 1, prev.multi + (blocks > 1 ? 1 : 0));
                }
            }

            foreach (var kv in counts.OrderByDescending(k => k.Value.multi))
            {
                _out.WriteLine($"PROBE {kv.Key}: 절 발견 {kv.Value.docs}건, 블록 2개 이상 {kv.Value.multi}건");
            }
        }

        private static int CountBlocks(IReadOnlyList<string> lines, int start, int end)
        {
            var blocks = 0;
            var inBlock = false;
            for (var i = start; i < end; i++)
            {
                var isRow = lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal);
                if (isRow && !inBlock) { blocks++; inBlock = true; }
                else if (!isRow) inBlock = false;
            }
            return blocks;
        }
    }
}
```

`LocateHeadingSection`과 `CorpusSkip.FindCorpusRoot`의 실제 접근성을 확인하라. `private`이면 프로브에서만 쓸 `internal` 통로를 임시로 열되, **Step 6에서 프로브와 함께 반드시 되돌린다.** 되돌리지 않으면 프로덕션 표면이 넓어진 채 남는다.

- [ ] **Step 3: 심링크를 걸고 프로브를 돌린다**

```bash
ln -s /Users/payletter/git-root/ReSet/output output
ln -s /Users/payletter/git-root/ReSet/output.bak-2026-08-22 output.bak-2026-08-22
dotnet test --filter "FullyQualifiedName~TempTableBlockProbe" --logger "console;verbosity=detailed"
```

출력에 `PROBE` 줄이 나와야 한다. `Skip`으로 넘어가면 심링크가 안 걸린 것이다 — 고치고 다시 돌린다. **건너뛴 채로 "영향 0"이라 적지 말 것.**

- [ ] **Step 4: 블록 2개 이상이 나온 검사에 대해 2단계 대조를 한다**

1단계에서 `블록 2개 이상`이 **0건인 검사는 여기서 제외**한다(구조적으로 무관).

살아남은 검사마다, 같은 코퍼스 문서에서 두 수집 방식의 결과를 비교한다.

- **관대**: 절의 모든 `|` 줄
- **좁힘**: 헤더 셀이 전부 든 행으로 시작하는 블록들의 데이터 행 합집합

기대 사실 하나하나에 대해 `관대=present, 좁힘=absent`인 경우만 모아 **사례 목록**을 만든다. 각 사례에 잡 이름, 단계 코드, 기대 사실, 관대가 매칭한 행의 실물, 그 행이 속한 블록의 헤더 행을 적는다.

**숫자만 세지 말 것.** 이 목록은 사람이 (가)/(나)를 가르기 위한 것이다.

- [ ] **Step 5: 보고서를 쓰고 커밋한다**

`docs/audit-reports/sweeps/2026-08-26-table-block-sweep.md`에 담을 것:

1. Step 1의 좌표 표 (14행: 검사·절 앵커·추출기·프롬프트 렌더 지점)
2. Step 3의 1단계 수치 (검사별 절 발견 건수, 블록 2개 이상 건수)
3. Step 4의 사례 목록 (있으면). 없으면 "0건"이라고 명시
4. 설계서 §6-1의 조건 (가)·(나)를 각 검사에 적용한 결과 — 이식 대상 후보 목록

   **정지 조건(설계서 §3-4)을 함께 적용한다: 갈래 (나)가 한 건이라도 나온 검사는 이식
   대상에서 뺀다.** (나)는 "행이 제 블록에 있었는데 헤더로 그 블록을 못 찾았다"는 것이라
   좁히면 멀쩡한 행을 결손이라 발화한다 — 새 거짓 양성이다.

   **(가)와 (나)의 개수를 견주지 말 것.** (나)는 사용자가 곧바로 보는 거짓 발화이고
   (가)는 원래 놓치던 것을 이제 잡는 것이다. 같은 저울에 올라가지 않는다. 한 건이라도
   나오면 그 검사의 헤더 상수가 그 표에 안 맞는다는 뜻이고, 상수를 고칠 일이지 이식을
   밀어붙일 일이 아니다. 보고서에 "(나) N건으로 제외"라고 적고 그 사례를 싣는다.
5. **재지 못한 것** — 프로브가 놓칠 수 있는 것을 적는다. 예: `Spec.md`가 아닌 산출물, 절 앵커를 못 찾은 문서

```bash
git add docs/audit-reports/sweeps/2026-08-26-table-block-sweep.md
git commit -m "docs: 표 블록 수집의 코퍼스 영향을 잰다"
```

- [ ] **Step 6: 프로브와 임시 접근성 변경을 되돌린다**

```bash
rm tests/ReSet.Core.Tests/TempTableBlockProbe.cs
git checkout -- src/ReSet.Core/Services/MechanicalValidator.cs
git status --short
```

`git status --short`가 비어야 한다. 비지 않으면 무엇이 남았는지 보고한다.

- [ ] **Step 7: 기준선을 확인한다**

```bash
dotnet build
dotnet test
```

경고 0·오류 0, 실패 0 / 통과 3001 / 건너뜀 0. 이 태스크는 프로덕션 코드를 안 바꿨으므로 수치가 그대로여야 한다.

---

### Task 2: 헤더 셀을 공유 상수로 올린다

**Files:**
- Modify: `src/ReSet.Core/Services/TransactionBoundaryExtractor.cs:31` 부근
- Modify: `src/ReSet.Core/Services/AiService.cs:1273`
- Create: `tests/ReSet.Core.Tests/PromptTableHeaderParityTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces: `public static readonly string[] TransactionBoundaryExtractor.TableHeaderCells` — Task 3이 `CollectTableMatchRows` 호출에 넘긴다.

**배경:** `TransactionBoundaryExtractor`는 이미 `public const string TableHeading`을 공개하고 `AiService`가 그것을 참조한다. 헤더 **행**만 프롬프트에 리터럴로 남아 있다. 검사 쪽에 복사본을 만들면 낡을 수 있으므로, 추출기가 소유하고 양쪽이 참조하게 한다.

- [ ] **Step 1: 바이트 등가성 테스트를 먼저 쓴다**

`tests/ReSet.Core.Tests/PromptTableHeaderParityTests.cs`:

```csharp
using System;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 프롬프트 본문은 접두사 캐싱과 얽혀 있다 - 공유 접두사가 호출 N번에 걸쳐
    /// 바이트 단위로 같아야 한다. 헤더 행을 상수 조립으로 바꾸는 변경이 그 바이트를
    /// 건드리면 캐시가 깨진다. 이 테스트가 조립 결과를 옛 리터럴에 못박는다.
    ///
    /// 상수를 쓴다고 해서 프롬프트가 그것을 쓰는지는 별개다. 참조를 끊고 리터럴로
    /// 되돌리는 변경까지 막으려면 조립식 자체를 여기서 재현해 비교해야 한다.
    /// </summary>
    public class PromptTableHeaderParityTests
    {
        [Fact]
        public void TransactionBoundaryHeaderRow_ShouldRenderByteIdenticalToTheOldLiteral()
        {
            var composed =
                $"   | {string.Join(" | ", TransactionBoundaryExtractor.TableHeaderCells)} |";

            Assert.Equal("   | 라인 | 종류 | 이름 |", composed);
        }

        [Fact]
        public void TransactionBoundaryHeaderCells_ShouldBeTheThreeColumnsInRenderOrder()
        {
            Assert.Equal(
                new[] { "라인", "종류", "이름" },
                TransactionBoundaryExtractor.TableHeaderCells);
        }
    }
}
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~PromptTableHeaderParityTests"
```

기대: 컴파일 실패 — `TableHeaderCells`가 없다.

- [ ] **Step 3: 상수를 추가한다**

`src/ReSet.Core/Services/TransactionBoundaryExtractor.cs`의 `TableHeading` 바로 아래:

```csharp
        /// <summary>
        /// 이 표의 헤더 셀. 프롬프트가 이 표를 렌더할 때와 L1이 명세서에서 이 표의
        /// 블록을 특정할 때 **같은 값**을 봐야 한다. 검사 쪽에 복사본을 두면 프롬프트가
        /// 바뀌는 날 조용히 낡고, 헤더 대조가 아무 블록도 못 찾아 관대한 폴백으로
        /// 후퇴하면서 결함이 소리 없이 되살아난다 - 그동안 테스트는 계속 초록이다.
        /// </summary>
        public static readonly string[] TableHeaderCells = { "라인", "종류", "이름" };
```

- [ ] **Step 4: 통과를 확인한다**

```bash
dotnet test --filter "FullyQualifiedName~PromptTableHeaderParityTests"
```

기대: 통과.

- [ ] **Step 5: 프롬프트가 상수를 쓰게 바꾼다**

`src/ReSet.Core/Services/AiService.cs:1273`:

```csharp
                // (전)
                "   | 라인 | 종류 | 이름 |",

                // (후)
                $"   | {string.Join(" | ", TransactionBoundaryExtractor.TableHeaderCells)} |",
```

**선행 공백 세 칸을 유지한다.** 이 줄의 바이트가 바뀌면 접두사 캐시가 깨진다.

- [ ] **Step 6: 전체를 돌린다**

```bash
dotnet build
dotnet test
```

경고 0·오류 0. 실패 0, 통과 3003(기존 3001 + 새 테스트 2), 건너뜀 0.

프롬프트 문자열을 단언하는 기존 테스트가 깨지면 **조립 결과가 리터럴과 다르다는 뜻**이다. 공백이나 구분자를 확인하라 — 테스트를 고치지 말고 조립을 고쳐라.

- [ ] **Step 7: 변이로 잠금을 확인한다**

```bash
# AiService.cs:1273을 옛 리터럴로 되돌린다 → 등가성 테스트는 여전히 통과한다.
# 이것은 정상이다: 그 테스트는 "상수가 옳은 값인가"를 보지 "프롬프트가 그것을 쓰는가"를
# 보지 않는다. 후자는 아래 변이로 확인한다.

# 상수의 셀 하나를 "라인" → "행"으로 바꾼다
dotnet test --filter "FullyQualifiedName~PromptTableHeaderParityTests"
```

기대: 두 테스트가 **모두 죽는다**. 죽지 않으면 테스트가 변별하지 않는 것이니 보고한다.

변이를 되돌린다: `git checkout -- src/ReSet.Core/Services/TransactionBoundaryExtractor.cs`

- [ ] **Step 8: 커밋한다**

```bash
git add src/ReSet.Core/Services/TransactionBoundaryExtractor.cs \
        src/ReSet.Core/Services/AiService.cs \
        tests/ReSet.Core.Tests/PromptTableHeaderParityTests.cs
git commit -m "feat: 트랜잭션 경계 표의 헤더 셀을 추출기가 소유하게 한다"
```

---

### Task 3: 헬퍼 셋과 첫 검사 이식

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (헬퍼 셋 추가, `CheckTransactionBoundaries`의 수집부 교체 — 현재 `4669-4676`)
- Create: `tests/ReSet.Core.Tests/TableBlockMatchingTests.cs`

**Interfaces:**
- Consumes: `TransactionBoundaryExtractor.TableHeaderCells` (Task 2)
- Produces:
  - `private static List<List<string>> SplitIntoTableBlocks(IReadOnlyList<string> lines, int start, int end)`
  - `private static bool IsHeaderRow(string row, IReadOnlyList<string> expectedHeaderCells)`
  - `private static List<string> CollectTableMatchRows(IReadOnlyList<string> lines, int start, int end, IReadOnlyList<string> expectedHeaderCells)`

  이후 물결의 검사들이 `CollectTableMatchRows`를 같은 시그니처로 호출한다.

**원본:** `git show 032031b:src/ReSet.Core/Services/MechanicalValidator.cs` 의 4403–4530행. **문서 주석을 그대로 옮긴다** — P2·P10 재현 사례가 거기 있다.

- [ ] **Step 1: 거짓 음성 재현 테스트를 먼저 쓴다**

`tests/ReSet.Core.Tests/TableBlockMatchingTests.cs`. 인접 블록의 우연한 토큰 일치가 진짜 결손을 덮는 상황을 만든다.

```csharp
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class TableBlockMatchingTests
    {
        /// <summary>
        /// 헤딩 절 안에 산문으로 갈린 별개 블록이 있고, 그 블록의 행이 우연히 기대
        /// 토큰(라인 번호)을 담고 있다. 진짜 표에는 라인 4 COMMIT이 없다.
        /// 옛 수집(절 전체의 `|` 줄을 뭉뚱그림)은 우연한 일치에 속아 결손 0건을 냈다.
        /// </summary>
        [Fact]
        public void CheckTransactionBoundaries_ShouldNotLetAForeignBlockMaskAMissingRow()
        {
            var markdown = $@"## 로직 흐름 요약

{TransactionBoundaryExtractor.TableHeading}

| 라인 | 종류 | 이름 |
| :--- | :--- | :--- |
| 1 | BEGIN TRANSACTION | (없음) |

참고: 아래는 표가 아니라 설명이다.

| 4 | COMMIT TRANSACTION | (없음) |
";

            var result = ValidateTwoBoundaries(markdown);

            Assert.Contains(result.Errors, e => e.Contains("4"));
        }
    }
}
```

**`SpecExpectations`는 손으로 채우지 않는다 — DDL에서 유도한다.** `SpecExpectations.From(SpDefinition)`이 DDL 본문을 읽어 `TransactionBoundaries`를 만든다(`tests/ReSet.Core.Tests/SpecExpectationsTransactionAndSetTests.cs:9-32`가 같은 방식). 따라서 위 테스트의 기대 사실은 **DDL로** 준다.

같은 파일에 헬퍼 둘을 둔다.

```csharp
        private static SpDefinition Def(string ddl) => new()
        {
            ObjectKey = CodeObjectKey.Create("DB", "dbo", "P", CodeObjectType.Procedure),
            Schema = "dbo",
            Name = "P",
            DdlText = ddl
        };

        /// <summary>라인 1 BEGIN, 라인 4 COMMIT을 내는 DDL. 두 테스트가 공유한다.</summary>
        private const string TwoBoundaryDdl = @"CREATE PROCEDURE dbo.P AS
BEGIN
    BEGIN TRANSACTION
    SELECT 1
    SELECT 2
    COMMIT TRANSACTION
END";

        private static ValidationResult ValidateTwoBoundaries(string markdown)
        {
            var expectations = SpecExpectations.From(Def(TwoBoundaryDdl));
            Assert.NotNull(expectations);
            return MechanicalValidator.Validate(markdown, expectations!);
        }
```

**DDL의 줄 번호가 표의 `라인` 값을 정한다.** 위 DDL이 실제로 어떤 라인 번호를 내는지 먼저 확인하라 — `Assert.Equal(2, expectations!.TransactionBoundaries.Count)`와 각 `Fact.Line`을 찍어 보고, 마크다운 픽스처의 `| 1 |`·`| 4 |`를 **실제 값에 맞춰 고친다.** 추측한 번호로 테스트를 쓰면 RED가 엉뚱한 이유로 나온다.

`MechanicalValidator.Validate`의 정확한 시그니처(인자 개수·반환형)는 `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`의 기존 호출을 그대로 따른다.

- [ ] **Step 2: 실패를 확인한다 (RED)**

```bash
dotnet test --filter "FullyQualifiedName~TableBlockMatchingTests"
```

기대: **실패.** 지금 구현은 절 전체를 모으므로 라인 4가 "있다"고 판정해 결손을 안 낸다.

**통과하면 멈추고 보고하라.** 재현이 안 된다는 뜻이고, 결함의 전제가 틀렸을 수 있다.

- [ ] **Step 3: 헬퍼 셋을 옮긴다**

`git show 032031b:src/ReSet.Core/Services/MechanicalValidator.cs`에서 4403–4530행을 읽어 `IsHeaderRow`·`SplitIntoTableBlocks`·`CollectTableMatchRows` 셋을 문서 주석과 함께 그대로 가져온다. `SplitTableRowCells`는 main에 이미 있으므로 옮기지 않는다.

본문은 이렇다(주석은 원본에서 가져올 것):

```csharp
        private static List<List<string>> SplitIntoTableBlocks(
            IReadOnlyList<string> lines, int start, int end)
        {
            var blocks = new List<List<string>>();
            var current = new List<string>();
            for (var i = start; i < end; i++)
            {
                if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    current.Add(lines[i]);
                }
                else if (current.Count > 0)
                {
                    blocks.Add(current);
                    current = new List<string>();
                }
            }
            if (current.Count > 0) blocks.Add(current);
            return blocks;
        }

        private static bool IsHeaderRow(string row, IReadOnlyList<string> expectedHeaderCells)
        {
            var cells = SplitTableRowCells(row);
            return expectedHeaderCells.All(expected => cells.Any(c => c == expected));
        }

        private static List<string> CollectTableMatchRows(
            IReadOnlyList<string> lines, int start, int end, IReadOnlyList<string> expectedHeaderCells)
        {
            var blocks = SplitIntoTableBlocks(lines, start, end);
            var matched = blocks
                .Where(block => block.Count > 0 && IsHeaderRow(block[0], expectedHeaderCells))
                .SelectMany(block => block.Skip(1))
                .ToList();

            if (matched.Count > 0)
            {
                return matched;
            }

            // 후퇴: 헤더로 자기 블록을 하나도 특정할 수 없으면 옛 동작대로 구간의
            // 모든 `|` 줄을 그대로 쓴다. 관대함을 유지해 새 거짓 양성을 만들지 않는다.
            var all = new List<string>();
            for (var i = start; i < end; i++)
            {
                if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    all.Add(lines[i]);
                }
            }
            return all;
        }
```

- [ ] **Step 4: 호출부를 교체한다**

`CheckTransactionBoundaries`의 수집부(현재 `4669-4676`):

```csharp
                // (전)
                var rowLines = new List<string>();
                for (var i = headingIndex + 1; i < endIndex; i++)
                {
                    if (lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                    {
                        rowLines.Add(lines[i]);
                    }
                }

                // (후)
                var rowLines = CollectTableMatchRows(
                    lines, headingIndex + 1, endIndex,
                    TransactionBoundaryExtractor.TableHeaderCells);
```

- [ ] **Step 5: 통과를 확인한다 (GREEN)**

```bash
dotnet test --filter "FullyQualifiedName~TableBlockMatchingTests"
```

기대: 통과.

- [ ] **Step 6: 폴백을 지키는 테스트를 더한다**

폴백은 없으면 안 되는 것이므로 **그것을 지키는 테스트도 있어야 한다** — 없으면 다음 사람이 "이 폴백 왜 있지" 하고 지운다.

```csharp
        /// <summary>
        /// 헤더 행이 없는 렌더(모델이 예시를 안 따른 경우)에서도 관대한 전체 스캔으로
        /// 후퇴해 오류를 내지 않는다. 이 폴백이 없으면 LLM 출력의 사소한 형태 차이가
        /// 전부 거짓 양성이 된다.
        /// </summary>
        [Fact]
        public void CheckTransactionBoundaries_ShouldStayLenientWhenNoHeaderRowIsPresent()
        {
            var markdown = $@"## 로직 흐름 요약

{TransactionBoundaryExtractor.TableHeading}

| 1 | BEGIN TRANSACTION | (없음) |
| 4 | COMMIT TRANSACTION | (없음) |
";

            var result = ValidateTwoBoundaries(markdown);

            Assert.DoesNotContain(result.Errors, e => e.Contains("트랜잭션 경계"));
        }
```

- [ ] **Step 7: 변이로 잠금을 확인한다**

세 변이를 하나씩 넣고 지정된 테스트가 **죽는지** 본다. 각 변이 뒤 `git checkout -- src/ReSet.Core/Services/MechanicalValidator.cs`로 되돌린다.

| 변이 | 죽어야 하는 것 |
|---|---|
| Step 4의 호출부를 Step 3 이전의 순진한 수집으로 되돌린다 | `ShouldNotLetAForeignBlockMaskAMissingRow` |
| `CollectTableMatchRows`의 폴백 블록을 지우고 `matched`를 그대로 반환한다 | `ShouldStayLenientWhenNoHeaderRowIsPresent` |
| `Where(...)`의 `IsHeaderRow` 조건을 `true`로 바꾼다 | `ShouldNotLetAForeignBlockMaskAMissingRow` |

**죽지 않는 변이가 있으면 그 테스트는 변별하지 않는 것이다.** 고치거나, 못 고치면 보고한다.

- [ ] **Step 8: 코퍼스에서 회귀가 없는지 본다**

심링크를 걸고 전체를 돌린다.

```bash
dotnet build
dotnet test
```

경고 0·오류 0, 실패 0, **건너뜀 0**, 통과 3005(3003 + 새 테스트 2).

**코퍼스 골든 테스트가 깨지면 멈추고 보고하라.** 이 변경이 실제 산출물의 판정을 바꿨다는 뜻이고, Task 1 보고서의 사례 목록과 대조해야 한다 — (가)면 옳은 변화이고 (나)면 새 거짓 양성이다. 임의로 골든을 갱신하지 말 것.

- [ ] **Step 9: 커밋한다**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs \
        tests/ReSet.Core.Tests/TableBlockMatchingTests.cs
git commit -m "fix: 표 대조가 인접 블록의 우연한 일치에 결손을 덮지 않게 한다"
```

---

## 이 계획서가 다루지 않는 것

- **나머지 검사로의 물결.** Task 1 보고서가 대상을 정한 뒤 별도 계획서를 쓴다. 지금 규모를 확정할 수 없다 — 검사 하나짜리로 줄 수도, 열 개짜리로 늘 수도 있다.
- **`CheckParameterColumnClaims`·`CheckParameterTableRows`.** 수집 모양이 한 줄 교체가 아니다(각각 `2491-2493`, `2594-2602`). 개별 설계가 필요하고 물결에서 별건으로 다룬다.
- **브랜치 B의 테스트 20개.** main이 검사를 독자 구현했으므로 그대로 붙는지 확인되지 않았다. Task 3은 재현 테스트를 새로 쓴다. 물결 단계에서 브랜치 B 테스트의 이식 가능성을 따로 평가한다.
- **`(B)` 불변식의 codify.** 브랜치 B가 세운 "인접 블록의 우연한 토큰 일치가 진짜 결손을 덮으면 안 된다"는 작성 계약 4번의 뒷면인데 저장소에 명문화돼 있지 않다. 이 계획은 테스트로만 잠근다.
