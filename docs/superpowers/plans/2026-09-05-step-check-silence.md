# 단계 검사의 침묵을 닫는 구현 계획서

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 통합 배치 계획서의 단계 결함이 L1을 조용히 통과하는 자리 셋을 닫는다 — 블록 주석 생략(A-3), 판별 토큰 없는 갱신 27건(A-2), 재료가 없어 켜지지 않는 검사 D(A-1).

**Architecture:** 생성 축은 건드리지 않는다(이미 SP당 1단계로 쪼개져 있다). 검사의 판별 규칙과 재료만 고친다. 모든 변경은 **고정된 두 판**에 걸어 판정이 갈리는 것으로 합격을 정의한다 — 결함 판에서 발화하고 현행 판에서 오탐 0.

**Tech Stack:** .NET (C#), xUnit, `Microsoft.SqlServer.TransactSql.ScriptDom`

**Spec:** `docs/superpowers/specs/2026-09-05-step-check-silence-design.md`

## Global Constraints

- **기준 커밋**: `d625ad01` 이상에서 분기한다. 병행 세션 `reset-ab`가 `MechanicalValidator.cs`에 두 번 손댔다 — 검사 B 접힘(`83bdf03f`)과 `CheckDuplicateProjectionNames` 신설(`1f7ea018`). **줄 번호로 자리를 지목하지 마라**: 뒤의 커밋이 112줄을 밀었고 파일 머리에 `using Microsoft.SqlServer.TransactSql.ScriptDom;`이 생겼다. 이름으로 찾아라.
- **작업 공간**: 격리 `git worktree`에서만 빌드·테스트한다. 공유 체크아웃은 인덱스·`appsettings.local.json`·`bin`/`obj`가 새고 `git stash`도 공유된다.
- **코퍼스 심링크 넷** — 워크트리에서 **가장 먼저** 건다. 전부 gitignore 대상이라 새 워크트리에 없다. **일부만 걸면 다른 테스트가 대신 꺼지는데 총 건너뜀 수는 줄어 진전처럼 보인다**(`CorpusSetupGuardTests` 머리 주석의 실측 사고 둘).

  ```bash
  cd <워크트리>
  ln -s <메인 저장소>/output output
  ln -s <메인 저장소>/output.bak-2026-08-22 output.bak-2026-08-22
  ln -s <메인 저장소>/output.bak-stage4-control-20260828 output.bak-stage4-control-20260828
  ln -s <메인 저장소>/output.bak-batch1-preregen-20260904 output.bak-batch1-preregen-20260904
  ```

  넷째는 이 계획서가 새로 더하는 것이다(Task 1 Step 5a가 상수·가드·문서에 등록한다).
  **`dotnet test`의 건너뜀이 0이 아니면 심링크가 덜 걸린 것이다** — 코드를 의심하기 전에 그것부터 본다.
- **테스트 게이트**: `dotnet test` **실패 0 · 건너뜀 0**. 절대 통과 수를 게이트로 쓰지 않는다(환경 내에서도 최대 5까지 흔들린다).
- **경고 게이트**: `dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"` → **0**. 증분 빌드는 기존 경고를 다시 보고하지 않는다.
- **건드리지 않는 파일**: `AiService.cs` · `StepSqlStatementReader.cs` · `docs/known-defects.md` (reset-ab 몫).
- **보고서 이름 가르기**: 내가 손으로 쓰는 판독 문서에는 `-b1`을 붙인다(reset-ab는 `-ab`). **`--sweep` 보고서는 예외다** — `SweepCommand.NextAvailablePath`가 `YYYY-MM-DD-step-sweep.md` → `-b` → `-c` 순으로 스스로 채번하므로 덮어쓰기는 일어나지 않지만 이름으로는 누구 것인지 알 수 없다. **스윕을 돌릴 때마다 출력이 알려 주는 경로를 판독 문서에 적어 두어라.**
- **고정 오라클 둘** — 경로를 바꾸지 않는다:
  - 결함 판 `output.bak-batch1-preregen-20260904/Jobs/POQSettleBatch1/agent/steps/`
  - 현행 판 `output/Jobs/POQSettleBatch1/docs/BatchMigrationPlan.md`
- **순서 고정**: Task 1 → 2 → 3 → (승인) → 4. Task 4(재생성)가 A-2의 오라클(`output/Procedures/*/docs/Spec.md`)을 바꾸므로 측정을 가운데 두고 걸치지 않는다.

---

## File Structure

| 파일 | 책임 | 태스크 |
| :--- | :--- | :--- |
| `src/ReSet.Core/Services/OmissionCommentScanner.cs` | 계획서 코드 블록에서 「구현 대신 주석이 선 자리」를 찾아 배너 재료를 낸다 | 1 |
| `tests/ReSet.Core.Tests/OmissionCommentScannerTests.cs` | 위의 발화·오탐 경계를 고정한다 | 1 |
| `tests/ReSet.Core.Tests/StepCheckOracleTests.cs` (신규) | 고정 오라클 두 판에 검사를 걸어 판정이 갈리는지 잠근다 | 1·3 |
| `tests/ReSet.Core.Tests/CorpusPaths.cs` | 넷째 코퍼스 재료(결함 판) 상수·존재 판정을 더한다 | 1 |
| `tests/ReSet.Core.Tests/CorpusSetupGuardTests.cs` | 넷째 재료의 「반쯤 설정」을 실패로 잡는다 | 1 |
| `tests/ReSet.Core.Tests/CorpusSkip.cs` · `AGENTS.md` | 재료 목록 「둘」·「셋」을 「넷」으로 정정한다 | 1 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` (`CheckSpecSetExpressions`·`DistinctiveExpressionTokens`) | `CheckSpecSetExpressions`·`DistinctiveExpressionTokens` — 명세서 SET 산식이 단계 본문에 실렸는지 본다 | 3 |
| `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs` | 위의 단위 경계 | 3 |
| `scripts/measure-set-expression-tokens.py` (신규) | A-2 후보별 발화/오탐을 두 판에서 재는 측정 하네스 | 2 |
| `docs/audit-reports/2026-09-05-set-expression-token-readout-b1.md` (신규) | 측정 결과와 규칙 선택 근거 | 2 |

---

## Task 1: 블록 주석 생략 자리를 스캐너가 보게 한다 (A-3)

**Files:**
- Modify: `src/ReSet.Core/Services/OmissionCommentScanner.cs:25-38, 60-90`
- Modify: `tests/ReSet.Core.Tests/OmissionCommentScannerTests.cs`
- Create: `tests/ReSet.Core.Tests/StepCheckOracleTests.cs`

**Interfaces:**
- Consumes: `MarkdownSectionLocator.SplitLines(string)` — 기존 헬퍼.
- Produces: `OmissionCommentScanner.Scan(string?)` → `IReadOnlyList<string>`. 시그니처는 **바뀌지 않는다**. 호출부(`VerificationPipelineOrchestrator`의 배너 조립)는 손대지 않는다.

**왜 이 태스크인가.** 감사가 🔴로 매긴 자리의 실제 모양은 이것이다
(`output.bak-batch1-preregen-20260904/.../agent/steps/S08.md:155-159`):

```
        SET @v_currentStepId = -21;
        /* UPDATE 13: CLVTType=1 금액 재배치.
           WHERE YMD=@pi_strYMD
             AND CLVTType=1 ... */
```

`UPDATE` 문이 서야 할 자리에 `/* ... */` 블록 주석이 서 있다. 현행 `CommentLineRegex`는
`^\s*(?:--|//)\s*(?<body>.+)$`라 **블록 주석을 아예 못 본다.** 실측: 결함 판 7건 · 현행 판 0건.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/OmissionCommentScannerTests.cs`에 추가한다.

```csharp
        [Fact]
        public void Scan_ShouldFlagBlockCommentStandingInForDml()
        {
            // 감사가 🔴로 매긴 실제 모양(S08.md:155-159). UPDATE 문이 서야 할 자리에
            // 블록 주석이 서 있다. `--`/`//`만 보던 종전 정규식은 이것을 못 봤다.
            var plan = string.Join("\n",
                "```sql",
                "        SET @v_currentStepId = -21;",
                "        /* UPDATE 13: CLVTType=1 금액 재배치.",
                "           WHERE YMD=@pi_strYMD",
                "             AND CLVTType=1 */",
                "```");

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldNotFlagBlockCommentThatOnlyAnnotatesRealDml()
        {
            // 앵커 주석(`/* U13: ... */`)은 뒤에 실제 DML 이 서 있으면 생략이 아니다.
            // 이 경계가 없으면 규칙 준수 문서가 통째로 발화한다.
            var plan = string.Join("\n",
                "```sql",
                "        /* U13: CLVTType=1 금액 재배치 */",
                "        UPDATE SETTLE_POQ_DB.dbo.TSettleMst",
                "        SET CLVT = 0",
                "        WHERE YMD = @pi_strYMD;",
                "```");

            Assert.Empty(OmissionCommentScanner.Scan(plan));
        }

        [Fact]
        public void Scan_ShouldFlagOmissionEvenWhenCommentSaysPreserve()
        {
            // 종전 PreservationMarkers 화이트리스트가 면제하던 자리다. "유지한다"가
            // 붙어 있어도, 그 주석이 선 자리에 실행 가능한 DML 이 없으면 생략이다.
            var plan = string.Join("\n",
                "```sql",
                "        /* UPDATE 4: 고객사 최저수수료. 원본 SET 산식을 그대로 유지한다. */",
                "```");

            Assert.Single(OmissionCommentScanner.Scan(plan));
        }
```

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~OmissionCommentScannerTests" -v minimal
```

기대: `Scan_ShouldFlagBlockCommentStandingInForDml`과 `Scan_ShouldFlagOmissionEvenWhenCommentSaysPreserve`가 **실패**
(`Assert.Single() Failure: The collection was empty`). `Scan_ShouldNotFlagBlockCommentThatOnlyAnnotatesRealDml`은 통과한다(아직 블록을 안 보므로).

- [ ] **Step 3: 블록 주석 인식과 구조 판별자를 구현한다**

`OmissionCommentScanner.cs`에서 `PreservationMarkers` 필드와 그것을 쓰는 `if` 블록(72-75행)을 **지우고**,
아래를 더한다. 기존 `--`/`//` 경로는 그대로 둔다.

```csharp
        // [왜 블록 주석을 따로 보는가] 감사가 🔴로 매긴 자리의 실제 모양은 `--`가 아니라
        // `/* UPDATE 13: ... */`였다(S08.md:155-159). 줄 단위 정규식은 그것을 못 본다.
        private static readonly Regex BlockCommentRegex = new(
            @"/\*(?<body>.*?)\*/",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // 생략으로 판정할 블록의 조건: DML 동사와 절 키워드를 함께 담아 "여기에 문장이
        // 있어야 한다"고 스스로 말하는 주석.
        private static readonly Regex DmlVerbRegex = new(
            @"\b(UPDATE|INSERT|DELETE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DmlClauseRegex = new(
            @"\b(WHERE|SET|VALUES|SELECT)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

`Scan`의 펜스 수집 루프 뒤에 블록 주석 경로를 더한다. 펜스 안 본문을 모아 두었다가
블록 단위로 판정한다 — 블록 주석은 여러 줄에 걸치므로 줄 단위로는 판정할 수 없다.

```csharp
        /// <summary>
        /// 펜스 안 본문에서 「구현 대신 선 블록 주석」을 찾는다.
        ///
        /// [판별자가 문구가 아니라 구조인 이유] 종전에는 "유지한다"·"보존한다"가 든
        /// 주석을 화이트리스트로 면제했다. 그러나 감사가 코퍼스 최악의 결함으로 매긴
        /// 자리(S07 - 갱신 18개 중 10개 소실)가 정확히 "원본대로 유지한다"였다. 문구는
        /// 생략인지 보존인지를 가르지 못한다. 가르는 것은 <b>그 주석이 선 자리에 실행
        /// 가능한 DML 이 있는가</b>이다.
        /// </summary>
        private static void ScanBlockComments(string fencedBody, List<string> hits, HashSet<string> seen)
        {
            foreach (Match match in BlockCommentRegex.Matches(fencedBody))
            {
                var body = match.Groups["body"].Value;
                if (!DmlVerbRegex.IsMatch(body) || !DmlClauseRegex.IsMatch(body))
                {
                    continue;
                }

                // 주석 뒤에 실제 DML 이 서 있으면 앵커 주석이다 - 생략이 아니다.
                var tail = fencedBody.Substring(match.Index + match.Length);
                if (StartsWithDmlStatement(tail))
                {
                    continue;
                }

                var label = Regex.Replace(body.Trim(), @"\s+", " ");
                if (label.Length > 70) label = label.Substring(0, 70);
                if (seen.Add(label) && hits.Count < MaxReported)
                {
                    hits.Add(label);
                }
            }
        }

        /// <summary>주석 바로 뒤(주석·공백만 건너뛰고)에 DML 문장이 시작하는가.</summary>
        private static bool StartsWithDmlStatement(string tail)
        {
            foreach (var line in MarkdownSectionLocator.SplitLines(tail))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith("--", StringComparison.Ordinal)) continue;
                // SET @v_... 대입은 문장이 아니라 오류 추적 표식이므로 건너뛴다.
                if (s.StartsWith("SET @", StringComparison.OrdinalIgnoreCase)) continue;
                return DmlVerbRegex.IsMatch(s);
            }

            return false;
        }
```

`Scan` 본문에서 펜스 안 줄을 별도 버퍼에 모으고, 루프가 끝난 뒤 `ScanBlockComments`를 부른다.

```csharp
            var fenced = new System.Text.StringBuilder();
            // ... 기존 루프 안, insideFence 인 줄에서:
            //     fenced.AppendLine(line);
            // ... 루프 뒤:
            ScanBlockComments(fenced.ToString(), hits, seen);
```

- [ ] **Step 4: 단위 테스트 통과를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~OmissionCommentScannerTests" -v minimal
```

기대: **실패 0 · 건너뜀 0.** 기존 `Scan_ShouldNotFlagInstructionCommentsThatDemandPreservation`의
세 번째 케이스(`-- 나머지 컬럼도 같은 방식으로 유지한다`)는 `--` 경로이고 화이트리스트를 지웠으므로
이제 **발화한다.** 그 `[InlineData]` 줄을 지우고, 아래 주석을 그 자리에 남긴다.

```csharp
        // "-- 나머지 컬럼도 같은 방식으로 유지한다" 케이스는 2026-09-05 에 제거했다.
        // PreservationMarkers 화이트리스트가 없어졌기 때문이며, 없앤 이유는
        // 그 화이트리스트가 감사 🔴(S07 - 갱신 10개 소실)의 문구를 면제했기 때문이다.
        // 오탐 경계는 이제 문구가 아니라 구조가 지킨다(ScanBlockComments 참고).
```

- [ ] **Step 5a: 결함 판을 네 번째 코퍼스 재료로 등록한다**

**이 단계를 건너뛰면 뒤의 오라클 테스트가 코퍼스 없는 워크트리에서 조용히 통과한다.**
`output.bak-batch1-preregen-20260904/`는 gitignore(`.gitignore:50` `output.bak-*/`) 대상이라
새 워크트리에 **없다.** 그리고 `AGENTS.md:157`의 정본 목록은 재료를 **셋**으로 적고 있어
이 넷째는 어느 가드도 모른다 — `CorpusPaths.ControlEdition` 주석이 기록한 사고와 같은 자리다.

`tests/ReSet.Core.Tests/CorpusPaths.cs`에 상수와 존재 판정을 더한다.

```csharp
        /// <summary>
        /// 결함 판 번들. <c>StepCheckOracleTests</c>가 「검사가 무엇을 가르는가」를
        /// 이 트리로 판정한다 — 감사가 🔴로 매긴 산출물이라 <b>재생성할 수 없다.</b>
        ///
        /// <see cref="PriorEdition"/>·<see cref="ControlEdition"/>과 같은 이유로
        /// `.git/info/exclude`의 `output.bak-*`에 걸려 `output/`과 별개로 없을 수 있다.
        /// 넷째 재료다 — 셋일 때 벌어진 일이 <see cref="ControlEdition"/> 주석에 있다.
        /// </summary>
        public const string DefectiveEdition = "output.bak-batch1-preregen-20260904";

        /// <summary>
        /// 결함 판이 실제로 닿는가. 디렉터리가 아니라 <b>소비자가 읽는 파일</b>로
        /// 판정한다 — <see cref="ControlEditionExists"/>와 같은 이유다.
        /// </summary>
        public static bool DefectiveEditionExists(string root) =>
            !string.IsNullOrEmpty(root) &&
            File.Exists(Path.Combine(
                root, DefectiveEdition, "Jobs", "POQSettleBatch1", "agent", "steps", "S08.md"));
```

`tests/ReSet.Core.Tests/CorpusSetupGuardTests.cs`에 세 번째 시험을 더한다.
기존 두 시험의 모양을 그대로 따른다(`[SkippableFact]` · `CorpusPaths.RepoRoot()` ·
`Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason)` · `IsLinkedWorktree` 좁힘).

```csharp
        [SkippableFact]
        public void CorpusSetup_WhenOutputIsPresent_DefectiveEditionMustAlsoBePresent()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);
            Skip.If(!CorpusPaths.IsLinkedWorktree(root),
                "연결된 워크트리가 아니다 - 가드가 막으려는 것은 워크트리 설정 실수다.");

            Assert.True(
                CorpusPaths.DefectiveEditionExists(root),
                $"`output/`은 있는데 {CorpusPaths.DefectiveEdition}이 없다. " +
                "StepCheckOracleTests가 「결함 판에서 발화한다」를 확인하지 못한 채 " +
                "초록이 된다 - 반쯤 설정된 상태다. " +
                $"ln -s <main>/{CorpusPaths.DefectiveEdition} {CorpusPaths.DefectiveEdition}");
        }
```

`tests/ReSet.Core.Tests/CorpusSkip.cs`의 `Reason` 문구에서 **「재료 둘」을 「재료 넷」으로** 고치고
`ControlEdition`·`DefectiveEdition` 두 줄을 `ln -s` 목록에 더한다. (그 문구는 이미 낡아 있었다 —
정본 `AGENTS.md`는 셋이고 이 상수는 둘이라 적혀 있었다.)

`AGENTS.md:157`과 `:233`의 **「셋」을 「넷」으로** 고치고 `ln -s` 예시에 한 줄을 더한다.
바이트 예산이 걸린 문서이므로 **줄을 더하는 것 외에 다른 편집을 하지 않는다.**

- [ ] **Step 5b: 고정 오라클 두 판에 걸어 판정이 갈리는지 잠근다**

`tests/ReSet.Core.Tests/StepCheckOracleTests.cs`를 만든다.
**자체 루트 탐색을 쓰지 않는다** — `CorpusPaths.RepoRoot()`가 실물 SP 메타데이터 파일로
판정해 `bin/Debug/net10.0/output/`의 스크래치를 걸러 낸다(그 함정의 실측이 그 주석에 있다).

```csharp
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 고정 오라클 두 판에 검사를 걸어 <b>판정이 갈리는지</b> 잠근다.
    ///
    /// [왜 발화 수가 아니라 두 판인가] 발화 수와 통과 수는 활동이지 효력이 아니다.
    /// 결함이 있다고 감사가 판정한 판에서 발화하고, 현행 판에서 침묵해야 비로소
    /// 그 검사가 무언가를 가른다고 말할 수 있다.
    ///
    /// [왜 조용히 통과시키지 않는가] `if (없으면) return;`으로 두면 코퍼스가 없는
    /// 워크트리에서 단언이 한 줄도 안 돌면서 초록이 된다 - <see cref="CorpusSkip"/>가
    /// 기록한 2026-08-23 사고가 정확히 그것이고, 다른 세션의 parallel-sdd 실행이
    /// 그 통과를 믿었다. 그래서 Skip 으로 드러낸다. 완료 기준이 「건너뜀 0」이므로
    /// 심링크를 빠뜨리면 기준이 자동으로 실패한다.
    /// </summary>
    public class StepCheckOracleTests
    {
        [SkippableFact]
        public void OmissionScanner_FiresOnDefectiveBundle_AndIsSilentOnCurrentPlan()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);
            Skip.If(!CorpusPaths.DefectiveEditionExists(root),
                $"{CorpusPaths.DefectiveEdition}이 없어 건너뜀 - " +
                $"ln -s <main>/{CorpusPaths.DefectiveEdition} {CorpusPaths.DefectiveEdition}");

            var bundleDir = Path.Combine(
                root, CorpusPaths.DefectiveEdition, "Jobs", "POQSettleBatch1", "agent", "steps");
            var planPath = Path.Combine(
                root, "output", "Jobs", "POQSettleBatch1", "docs", "BatchMigrationPlan.md");
            Skip.If(!File.Exists(planPath), CorpusSkip.Reason);

            var defectiveHits = Directory.GetFiles(bundleDir, "*.md")
                .Sum(f => OmissionCommentScanner.Scan(File.ReadAllText(f)).Count);
            var currentHits = OmissionCommentScanner.Scan(File.ReadAllText(planPath)).Count;

            // 실측(2026-09-05, 커밋 8c00813e): 결함 판 7건 · 현행 판 0건.
            // 발화 수 자체를 못 박지 않는 이유: 스캐너를 넓히면 결함 판 수가 늘 수 있고
            // 그것은 개선이다. 잠그는 것은 「갈린다」이지 특정 수가 아니다.
            Assert.True(defectiveHits > 0,
                $"결함 판에서 발화하지 않았다 - 검사가 무엇도 가르지 못한다 (발화 {defectiveHits})");
            Assert.Equal(0, currentHits);
        }
    }
}
```

- [ ] **Step 6: 오라클 테스트 통과를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepCheckOracleTests" -v minimal
```

기대: **PASS.** 실패하면 `ScanBlockComments`의 `StartsWithDmlStatement` 경계가 너무 넓거나 좁은 것이다 —
현행 판에서 발화하면 앵커 주석을 생략으로 오판한 것이고, 결함 판에서 침묵하면 블록 수집이 안 된 것이다.

- [ ] **Step 7: 전체 게이트를 통과한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"   # 기대: 0
dotnet test                                                  # 기대: 실패 0 · 건너뜀 0
```

- [ ] **Step 8: 커밋**

```bash
git add src/ReSet.Core/Services/OmissionCommentScanner.cs \
        tests/ReSet.Core.Tests/OmissionCommentScannerTests.cs \
        tests/ReSet.Core.Tests/StepCheckOracleTests.cs
git commit -m "fix: 생략 판별을 문구에서 구조로 옮긴다 — 화이트리스트가 🔴를 면제했다

스캐너가 --/// 만 보아 감사가 🔴로 매긴 자리(/* UPDATE 13: ... */ 블록
주석이 UPDATE 문 자리에 선 것)를 구조적으로 못 봤다. 그리고 그 자리의 문구가
'원본대로 유지한다'였는데, 그것이 PreservationMarkers 화이트리스트에 있었다.

문구는 생략인지 보존인지를 가르지 못한다. 가르는 것은 그 주석이 선 자리에
실행 가능한 DML 이 있는가다. 화이트리스트를 지우고 그 판별로 바꿨다.

고정 오라클 두 판으로 잠갔다 - 결함 판 7건 발화, 현행 판 0건."
```

---

## Task 2: A-2 측정 하네스를 만들고 후보별로 잰다

**Files:**
- Create: `scripts/measure-set-expression-tokens.py`
- Create: `docs/audit-reports/2026-09-05-set-expression-token-readout-b1.md`

**Interfaces:**
- Consumes: 고정 오라클 둘 + `output/Procedures/*/docs/Spec.md`(명세서 재료).
- Produces: 후보별 `(결함 판 발화, 현행 판 오탐)` 표. **Task 3이 이 표로 규칙을 고른다.**

**왜 코드를 먼저 안 고치는가.** `CheckSpecSetExpressions`의 현행 판정은
「토큰 하나라도 있으면 통과」다. 흔한 토큰(컬럼명)을 보태면 발화가 **줄어든다.**
어느 후보가 어느 방향으로 움직이는지는 읽어서 알 수 없다 — 재야 한다.

- [ ] **Step 1: 측정 스크립트를 쓴다**

`scripts/measure-set-expression-tokens.py`를 만든다. 아래 전문을 그대로 쓴다.

```python
#!/usr/bin/env python3
"""CheckSpecSetExpressions 의 토큰 후보별 발화/오탐을 두 판에서 잰다.

합격 기준은 발화 수가 아니라 판정이 갈리는 것이다 - 결함 판에서 늘고
현행 판에서 0 을 지키는 후보만 채택 가능하다.

사용법:  python3 scripts/measure-set-expression-tokens.py
"""
import glob
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFECTIVE = os.path.join(REPO, "output.bak-batch1-preregen-20260904/Jobs/POQSettleBatch1/agent/steps")
CURRENT = os.path.join(REPO, "output/Jobs/POQSettleBatch1/docs/BatchMigrationPlan.md")
SPECS = os.path.join(REPO, "output/Procedures/*/docs/Spec.md")

UPDATE_SECTION = re.compile(
    r"^###\s+UPDATE\s+대상 테이블:\s*([^\(]+?)\s*\(\s*갱신\s*(\d+)")

# 후보. 이름 -> 표현식에서 토큰을 뽑는 함수.
BASE = [
    (r"'([^']{2,})'", "인용 리터럴"),
    (r"\b(UF_[A-Za-z0-9_]+)", "UF_ 함수"),
    (r"(?<![\w.])(\d+\.\d+|\d{2,})(?![\w])", "2자리+ 숫자"),
]
CANDIDATES = {
    "base (현행)": BASE,
    "base + 별칭.컬럼": BASE + [(r"\b([A-Za-z]\.[A-Za-z_][A-Za-z0-9_]*)", "별칭.컬럼")],
    "base + 구조토큰": BASE + [(r"\b(CAST|ISNULL|IIF|ROUND)\s*\(", "구조토큰")],
    "base + 부호반전": BASE + [(r"(\*\s*\(?\s*-\s*1\s*\)?)", "부호반전")],
}


def tokens(expressions, patterns):
    out = []
    for expr in expressions:
        for pat, _ in patterns:
            for m in re.findall(pat, expr, re.IGNORECASE):
                t = (m if isinstance(m, str) else m[0]).strip()
                if t and t.lower() not in [x.lower() for x in out]:
                    out.append(t)
    return out


def read_targets(spec_path, patterns):
    lines = open(spec_path, encoding="utf-8").read().split("\n")
    rows = []
    for i, line in enumerate(lines):
        m = UPDATE_SECTION.match(line)
        if not m:
            continue
        end = next((j for j in range(i + 1, len(lines))
                    if lines[j].startswith("### ")), len(lines))
        blk = lines[i + 1:end]
        hdr = next((j for j, x in enumerate(blk) if x.strip().startswith("|")), None)
        if hdr is None:
            continue
        cols = [c.strip() for c in blk[hdr].strip("|").split("|")]
        ic = next((k for k, c in enumerate(cols) if "컬럼명" in c), -1)
        ie = next((k for k, c in enumerate(cols) if "원천 표현식" in c), -1)
        if ic < 0 or ie < 0:
            continue
        exprs = []
        for x in blk[hdr + 2:]:
            if not x.strip().startswith("|"):
                break
            c = [y.strip() for y in x.strip("|").split("|")]
            if ic < len(c) and c[ic] and ie < len(c):
                exprs.append(c[ie])
        rows.append((int(m.group(2)), tokens(exprs, patterns)))
    return rows


def bare(name):
    return name.strip().split(".")[-1].lower()


def spec_for(body, spec_paths):
    """단계 본문의 UP_ 토큰으로 명세서 하나를 고른다. 하나로 안 좁혀지면 None."""
    ups = {u.lower() for u in re.findall(r"\bUP_[A-Za-z_0-9]+", body)}
    cand = [p for p in spec_paths
            if bare(os.path.basename(os.path.dirname(os.path.dirname(p)))) in ups]
    return cand[0] if len(cand) == 1 else None


def step_bodies_defective():
    for f in sorted(glob.glob(os.path.join(DEFECTIVE, "*.md"))):
        yield os.path.basename(f)[:-3], open(f, encoding="utf-8").read()


def step_bodies_current():
    text = open(CURRENT, encoding="utf-8").read().split("\n")
    idx = [(i, l) for i, l in enumerate(text) if re.match(r"^### S\d\d", l)]
    for k, (i, l) in enumerate(idx):
        e = idx[k + 1][0] if k + 1 < len(idx) else len(text)
        yield re.match(r"^### (S\d\d)", l).group(1), "\n".join(text[i:e])


def evaluate(bodies, patterns, rule):
    spec_paths = sorted(glob.glob(SPECS))
    fired = comparable = zero = 0
    for _, body in bodies:
        sp = spec_for(body, spec_paths)
        if sp is None:
            continue
        low = body.lower()
        for _, tk in read_targets(sp, patterns):
            if not tk:
                zero += 1
                continue
            comparable += 1
            hits = sum(1 for t in tk if t.lower() in low)
            if rule == "any" and hits == 0:
                fired += 1
            elif rule == "all" and hits < len(tk):
                fired += 1
            elif rule == "majority" and hits * 2 < len(tk):
                fired += 1
    return fired, comparable, zero


def main():
    if not os.path.isdir(DEFECTIVE) or not os.path.isfile(CURRENT):
        print("고정 오라클이 없다. 경로를 확인하라.", file=sys.stderr)
        return 1

    print(f"{'후보':22} {'규칙':9} {'결함판 발화':>10} {'현행판 오탐':>10} {'대조가능':>8} {'토큰0':>6}")
    print("-" * 74)
    for name, patterns in CANDIDATES.items():
        for rule in ("any", "all", "majority"):
            df, dc, dz = evaluate(step_bodies_defective(), patterns, rule)
            cf, cc, cz = evaluate(step_bodies_current(), patterns, rule)
            print(f"{name:22} {rule:9} {df:>10} {cf:>10} {dc:>8} {dz:>6}")
    print()
    print("채택 조건: 결함판 발화 > base(any) 이고 현행판 오탐 == 0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 2: 측정을 돌린다**

```bash
python3 scripts/measure-set-expression-tokens.py | tee /tmp/set-token-readout.txt
```

기대: `base (현행)` × `any` 행이 **결함판 7 · 현행판 0**으로 나온다(2026-09-05 실측치와 일치).
**이 값이 안 나오면 하네스가 틀린 것이다** — 규칙을 고르기 전에 하네스부터 고쳐라.

- [ ] **Step 3: 판독 문서를 쓴다**

`docs/audit-reports/2026-09-05-set-expression-token-readout-b1.md`를 만든다. 담을 것:

1. 기준 커밋과 오라클 두 판의 경로·크기
2. Step 2 출력 표 전문
3. **채택한 후보와 규칙, 그리고 기각한 것마다 기각 사유** — 「발화가 안 늘어서」인지 「현행 판에 오탐이 나서」인지 구별해 적는다
4. **채택 후보의 토큰0 잔량** — 27건 중 몇이 남는지. 남는 것은 이 회차가 못 닫는 사각지대이므로 숫자로 남긴다

- [ ] **Step 4: 커밋**

```bash
git add scripts/measure-set-expression-tokens.py \
        docs/audit-reports/2026-09-05-set-expression-token-readout-b1.md
git commit -m "docs: SET 산식 토큰 후보를 두 판에 돌려 재고 규칙을 고른다

현행 판정은 '토큰 하나라도 있으면 통과'라 흔한 토큰을 보태면 발화가
줄어든다. 어느 후보가 어느 방향으로 움직이는지는 읽어서 알 수 없어
측정 하네스를 먼저 만들었다. 채택 조건은 결함판 발화 증가 + 현행판 오탐 0."
```

---

## Task 3: 고른 규칙을 `DistinctiveExpressionTokens`에 반영한다 (A-2)

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` — `CheckSpecSetExpressions`·`DistinctiveExpressionTokens` (줄 번호로 찾지 마라: `d625ad01`이 112줄 밀었다. 이름으로 찾아라)
- Modify: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`
- Modify: `tests/ReSet.Core.Tests/StepCheckOracleTests.cs`

**Interfaces:**
- Consumes: `SpecStatementFacts.SetTargets` → `SpecSetTarget(int Ordinal, string TargetTable, IReadOnlyList<string> Columns, IReadOnlyList<string> Expressions)`.
- Produces: `CheckSpecSetExpressions`의 발화가 Task 2가 고른 규칙을 따른다. **시그니처는 바뀌지 않는다.**

**전제.** Task 2의 판독 문서가 후보와 규칙을 확정했다. 그것을 여기서 구현한다.
판독 문서가 「채택 가능한 후보 없음」으로 끝났으면 **이 태스크는 코드를 바꾸지 않고**,
Step 4의 잔량 기록만 수행한 뒤 넘어간다 — 근거 없이 규칙을 넓히면 오탐이 배너를 죽인다.

- [ ] **Step 1: 실패하는 단위 테스트를 쓴다**

`tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`에 추가한다.
`<채택토큰>`은 판독 문서가 고른 것으로 바꾼다(예: 별칭.컬럼이면 `B.TXAMT`).

테스트는 **명세서 마크다운을 파싱하지 않는다** — 이 파일의 관례대로 `SpecStatementFacts`를
직접 만들어 넘긴다(`MechanicalValidatorTests.cs:6922-6947`의 `FactsWithUpdates`·`LegacyStep` 참고).

```csharp
        // ── CheckSpecSetExpressions: 순수 컬럼 산술 갱신 ────────────────────
        private static IReadOnlyDictionary<string, SpecStatementFacts> FactsWithPlainArithmeticSet() =>
            new Dictionary<string, SpecStatementFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["UP_UTIL_SETTLE_COMM_UPD"] = new SpecStatementFacts(
                    Array.Empty<SpecDmlRow>(),
                    new[]
                    {
                        new SpecSetTarget(
                            Ordinal: 1,
                            TargetTable: "TSettleMst",
                            Columns: new[] { "CLTOTAL" },
                            Expressions: new[] { "A.CLCOMM + A.CLVT + A.CLETC" })
                    },
                    Array.Empty<SpecLocalVariable>())
            };

        private static BatchStepPlan CommUpdStep(string code) => new(
            Code: code, Name: $"{code} 단계",
            LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_COMM_UPD" },
            TargetTables: new[] { "SETTLE_POQ_DB.dbo.TSettleMst" },
            ErrorCodes: new[] { "-9" }, Chunkable: false, SchemaTables: Array.Empty<string>());

        [Fact]
        public void ValidateBatchStep_CheckSetExpressions_FiresWhenOnlyPlainColumnArithmeticIsMissing()
        {
            // 판별 토큰이 인용 리터럴·UF_·2자리 숫자뿐이던 동안, 순수 컬럼 산술만으로
            // 이뤄진 갱신은 토큰 0 이라 구조적으로 보이지 않았다(코퍼스 52건 중 27건).
            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE dbo.TSettleMst SET CLTOTAL = 0 WHERE YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, CommUpdStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null,
                FactsWithPlainArithmeticSet());

            Assert.Contains(result.Errors, e => e.Contains("SET 산식"));
        }

        [Fact]
        public void ValidateBatchStep_CheckSetExpressions_StaysSilentWhenExpressionIsCarried()
        {
            // 오탐 경계. 산식이 본문에 실려 있으면 발화하지 않는다.
            var markdown = "### S07 단계\n\n```sql\n" +
                "UPDATE dbo.TSettleMst\n" +
                "SET CLTOTAL = A.CLCOMM + A.CLVT + A.CLETC\n" +
                "WHERE YMD = @p;\n" +
                "```\n";

            var result = new MechanicalValidator().ValidateBatchStep(
                markdown, CommUpdStep("S07"), new[] { "dbo.TSettleMst" },
                new Dictionary<string, SpecConditions>(), null, null,
                FactsWithPlainArithmeticSet());

            Assert.DoesNotContain(result.Errors, e => e.Contains("SET 산식"));
        }
```

레코드 시그니처(그대로 쓴다):
`SpecSetTarget(int Ordinal, string TargetTable, IReadOnlyList<string> Columns, IReadOnlyList<string> Expressions)` ·
`SpecStatementFacts(IReadOnlyList<SpecDmlRow> DmlRows, IReadOnlyList<SpecSetTarget> SetTargets, IReadOnlyList<SpecLocalVariable> LocalVariables)`

- [ ] **Step 2: 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CheckSetExpressions" -v minimal
```

기대: 첫 테스트가 **실패**(발화가 없다). 둘째는 통과.

- [ ] **Step 3: 판독 문서가 고른 후보를 구현한다**

`DistinctiveExpressionTokens`에 채택 패턴을 더한다.
아래는 「별칭.컬럼」이 채택된 경우의 예다 — **판독 문서가 다른 것을 골랐으면 그것을 쓴다.**

```csharp
                // [2026-09-05 추가] 순수 컬럼 산술만으로 이뤄진 갱신은 위 셋으로는
                // 토큰이 0 이라 구조적으로 보이지 않았다(코퍼스 52건 중 27건).
                // 후보별 발화/오탐은
                // docs/audit-reports/2026-09-05-set-expression-token-readout-b1.md 에 있다.
                foreach (Match m in Regex.Matches(expression, @"\b([A-Za-z]\.[A-Za-z_][A-Za-z0-9_]*)"))
                {
                    Add(m.Groups[1].Value);
                }
```

규칙(`any`/`all`/과반)이 바뀌었으면 `CheckSpecSetExpressions`의 판정도 함께 바꾼다.

```csharp
                // 판정 규칙은 위 판독 문서가 실측으로 골랐다. 바꾸려면 같은 하네스를
                // 다시 돌려 두 판의 판정이 갈리는지 확인하라 - 발화 수만으로는 못 고른다.
                if (tokens.Count(token => ContainsToken(stepMarkdown, token)) * 2 >= tokens.Count) continue;
```

- [ ] **Step 4: 단위 테스트와 오라클 테스트를 함께 통과시킨다**

`StepCheckOracleTests.cs`에 `CheckSpecSetExpressions` 오라클 테스트를 더한다 —
Task 1의 `OmissionScanner_...`와 같은 모양으로, 결함 판에서 발화하고 현행 판에서 0이어야 한다.
기대값은 **판독 문서의 채택 행 수치를 그대로 쓴다**(하드코딩한 숫자에 주석으로 출처를 단다).

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~CheckSetExpressions|FullyQualifiedName~StepCheckOracleTests" -v minimal
```

기대: **실패 0 · 건너뜀 0.**

- [ ] **Step 5: 코퍼스 스윕으로 회귀를 본다**

```bash
dotnet run --project src/ReSet.Cli -- --sweep 2>&1 | tee /tmp/sweep-task3-b1.txt | tail -30
grep -oE 'docs/audit-reports/sweeps/[0-9-]+-step-sweep[a-z-]*\.md' /tmp/sweep-task3-b1.txt | tail -1
```

**출력이 알려 준 보고서 경로를 판독 문서에 적는다** — 같은 날 reset-ab의 보고서가 섞이므로
파일명만으로는 누구 것인지 구별되지 않는다.

판독 문서에 **스윕 전후 차분**을 덧붙인다. 검사 A~E 중 이 변경이 건드리지 않은 검사의 수치가
움직였으면 그것은 회귀다 — 원인을 찾기 전에 다음으로 넘어가지 않는다.

- [ ] **Step 6: 전체 게이트를 통과한다**

```bash
dotnet clean && dotnet build 2>&1 | grep -cE "warning CS"   # 기대: 0
dotnet test                                                  # 기대: 실패 0 · 건너뜀 0
```

- [ ] **Step 7: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs \
        tests/ReSet.Core.Tests/MechanicalValidatorTests.cs \
        tests/ReSet.Core.Tests/StepCheckOracleTests.cs \
        docs/audit-reports/2026-09-05-set-expression-token-readout-b1.md
git commit -m "fix: 순수 컬럼 산술 갱신을 SET 산식 검사가 보게 한다

판별 토큰이 인용 리터럴·UF_·2자리 숫자뿐이라, 순수 컬럼 산술만으로 된
갱신은 토큰 0 이라 구조적으로 안 보였다 - 코퍼스 52건 중 27건이 그 자리다.

규칙은 읽어서 고르지 않았다. 현행 판정이 '토큰 하나라도 있으면 통과'라
흔한 토큰을 보태면 발화가 오히려 줄기 때문이다. 후보 넷 × 규칙 셋을 고정
오라클 두 판에 돌려 재고, 결함판 발화가 늘고 현행판 오탐이 0 인 것만 채택했다."
```

---

## Task 4: 명세서 13개를 재생성해 검사 D에 재료를 준다 (A-1)

> **⚠ 착수 전 사용자 승인이 필요하다.** Task 1~3이 끝난 뒤 결과를 보고하고 승인을 받는다.
> 약 3시간이 걸리고 구독 쿼터를 그만큼 쓴다(실측: SP당 13~16분).
> 그리고 이 태스크는 **A-2의 오라클(`output/Procedures/*/docs/Spec.md`)을 바꾼다** —
> Task 2·3의 측정이 모두 끝난 뒤에만 돌린다.

**Files:**
- Modify: `output/Procedures/*/docs/Spec.md` (13개 — `dbo.UP_UTIL_SETTLE_PROC_ETC` 제외, reset-ab가 완료)
- Create: `output.bak-b1-preregen-<날짜>/` (재생성 전 스냅샷)
- Create: `docs/audit-reports/2026-09-05-spec-regen-material-diff-b1.md`

**대상 13개** (`dbo.` 접두):
`UP_Util_PG_Client_CMRate_Ins` · `UP_UTIL_SETTLE_CANCEL_INS` · `UP_UTIL_SETTLE_COMM_UPD` ·
`UP_UTIL_SETTLE_EXCEPTION_PROC` · `UP_UTIL_SETTLE_EXPECT_PROC` · `UP_UTIL_SETTLE_INS` ·
`UP_UTIL_SETTLE_INS_EXTRA` · `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` · `UP_Util_Settle_Summary` ·
`UP_Util_Settle_Summary_AcqManual` · `UP_UTIL_SETTLE_SUMMARY_ETC` · `UP_UTIL_SETTLE_SUMMARY_EXTRA` ·
`UP_UTIL_STAT_PGCOLLECT_INS`

- [ ] **Step 1: 재생성 전 스냅샷과 기준선을 뜬다**

```bash
cp -a output/Procedures "output.bak-b1-preregen-$(date +%Y%m%d)"
dotnet run --project src/ReSet.Cli -- --sweep 2>&1 | tee /tmp/sweep-before-b1.txt
```

**「재료 분모」 절을 따로 보관한다** — 이것이 전후 대조의 주 계기다.
보고서 경로는 글롭으로 찾지 않는다(같은 날 reset-ab의 것이 섞인다) — **스윕 출력이 알려 주는 경로를 쓴다.**

```bash
BEFORE=$(grep -oE 'docs/audit-reports/sweeps/[0-9-]+-step-sweep[a-z-]*\.md' /tmp/sweep-before-b1.txt | tail -1)
echo "내 스윕 보고서(전): $BEFORE"
sed -n '/^## 재료 분모/,/^## [^재]/p' "$BEFORE" > /tmp/material-before-b1.txt
```

- [ ] **Step 2: 카나리아를 미리 잰다**

재생성 후에만 재면 「원래 그랬는지」를 모른다. 13개 각각에서 **첫 변수 헤딩**이 무엇인지 기록한다.

```bash
for f in output/Procedures/*/docs/Spec.md; do
  echo "$(basename $(dirname $(dirname $f))) : $(grep -m1 -E '^### (지역 변수|내부 변수)' "$f")"
done | tee /tmp/canary-before-b1.txt
```

- [ ] **Step 3: 재생성한다 (SP 하나씩)**

`--all`을 쓰지 않는다. 하나가 쿼터·권한으로 죽으면 어디까지 됐는지 알아야 한다.

```bash
for sp in UP_Util_PG_Client_CMRate_Ins UP_UTIL_SETTLE_CANCEL_INS UP_UTIL_SETTLE_COMM_UPD \
          UP_UTIL_SETTLE_EXCEPTION_PROC UP_UTIL_SETTLE_EXPECT_PROC UP_UTIL_SETTLE_INS \
          UP_UTIL_SETTLE_INS_EXTRA UP_UTIL_SETTLE_INS_EXTRA4PLCARD UP_Util_Settle_Summary \
          UP_Util_Settle_Summary_AcqManual UP_UTIL_SETTLE_SUMMARY_ETC \
          UP_UTIL_SETTLE_SUMMARY_EXTRA UP_UTIL_STAT_PGCOLLECT_INS; do
  echo "=== $sp $(date +%T)"
  # --batch 플래그는 없다. --sp 가 있으면 IsBatchMode 가 참이 된다(CliArgs.cs:24).
  dotnet run --project src/ReSet.Cli -- --sp "$sp" 2>&1 | tail -5
done | tee /tmp/regen-b1.log
```

- [ ] **Step 4: 종료 상태를 객체마다 확인한다**

재생성은 「통과하는 명세서」를 내주지 않는다 — PROC_ETC는 6회 전부 통과하지 못해 3차(96점)를
구제 채택했다. **채택본이 마지막 회차가 아니면 마지막 L2 지적은 반영되어 있지 않다.**

```bash
for f in output/Procedures/*/docs/Spec.md; do
  echo "$(basename $(dirname $(dirname $f))) : $(grep -m1 '검증 상태' "$f")"
done
grep -E "채택|구제|시도" /tmp/regen-b1.log | tail -40
```

판독 문서에 **객체별 `검증 상태`와 몇 차가 채택됐는지**를 표로 적는다.

- [ ] **Step 5: 재료 단위로 전후를 대조한다**

**헤딩 단위로 세지 않는다** — 절 이름은 모델 재량이라 개명이 섞인다
(`SELECT 대상 테이블` → `SELECT 조회 테이블`). 세 가지를 잰다.

```bash
# ① 기계 확정 표의 전후 개수
for f in output/Procedures/*/docs/Spec.md; do
  n=$(basename $(dirname $(dirname $f)))
  a="output.bak-b1-preregen-$(date +%Y%m%d)/$n/docs/Spec.md"
  [ -f "$a" ] || continue
  echo "$n : 전 $(grep -cE '^### .*기계 확정' $a) / 후 $(grep -cE '^### .*기계 확정' $f)"
done

# ② 앵커 토큰 (세션 옵션)
for f in output/Procedures/*/docs/Spec.md; do
  n=$(basename $(dirname $(dirname $f)))
  a="output.bak-b1-preregen-$(date +%Y%m%d)/$n/docs/Spec.md"
  [ -f "$a" ] || continue
  echo "$n : NOCOUNT 전 $(grep -c NOCOUNT $a) / 후 $(grep -c NOCOUNT $f)"
done

# ③ 스윕 「재료 분모」 절 — 주 계기
dotnet run --project src/ReSet.Cli -- --sweep 2>&1 | tee /tmp/sweep-after-b1.txt
AFTER=$(grep -oE 'docs/audit-reports/sweeps/[0-9-]+-step-sweep[a-z-]*\.md' /tmp/sweep-after-b1.txt | tail -1)
echo "내 스윕 보고서(후): $AFTER"
sed -n '/^## 재료 분모/,/^## [^재]/p' "$AFTER" > /tmp/material-after-b1.txt
diff /tmp/material-before-b1.txt /tmp/material-after-b1.txt
```

**합격 조건**: 기계 확정 표가 어느 객체에서도 **줄지 않는다**. 줄었으면 그 객체는 재생성을 되돌리고
(스냅샷에서 복원) 원인을 적는다. 「재료 분모」에서 **소실 프로시저 목록이 늘지 않는다.**

- [ ] **Step 6: 카나리아를 확인한다**

`ReadLocalVariables`는 `ReadTable(lines, predicate)`로 **먼저 나오는 헤딩 하나**만 잡고,
후보에 `### 지역 변수`와 `### 내부 변수`가 **둘 다** 있다(`SpecStatementFactsExtractor.cs:285-291`).
모델이 자기 변수 표를 기계 표 앞에 쓰면 **L1은 초록인데 검사 D만 침묵한다.**

```bash
for f in output/Procedures/*/docs/Spec.md; do
  echo "$(basename $(dirname $(dirname $f))) : $(grep -m1 -E '^### (지역 변수|내부 변수)' "$f")"
done | tee /tmp/canary-after-b1.txt
diff /tmp/canary-before-b1.txt /tmp/canary-after-b1.txt
```

**합격 조건**: 13개 전부에서 첫 변수 헤딩이 `### 지역 변수 (기계 확정 — 수정 금지)`다.
`### 내부 변수`가 앞에 선 객체가 있으면 그 객체는 검사 D가 침묵하므로 **결함으로 기록**한다.

- [ ] **Step 7: 검사 D가 켜졌는지 확인한다**

```bash
grep -A 20 "^## " /tmp/sweep-after-b1.txt | grep -iE "^\s*D\b|검사 D"
```

**기대값에 주의**: reset-ab 실측에서 검사 D의 발화는 전부 `POQSettleProc14`였고 **Batch1이 아니다** —
Batch1의 해당 단계는 축 B 로드맵 5가 의사코드로 바꿔 놓아 볼 `DECLARE`가 없어 검사 D가 도달하지 못한다.
**Batch1에서 발화를 기대하지 마라.** 재료(`### 지역 변수` 표)가 13개에 생겼는지가 이 태스크의 성과다.

- [ ] **Step 8: 판독 문서를 쓰고 커밋한다**

`docs/audit-reports/2026-09-05-spec-regen-material-diff-b1.md`에 Step 4~7의 표 전부와,
사라진 절 중 **개명인 것과 실제 소실인 것을 갈라** 적는다.

```bash
git add output/Procedures docs/audit-reports/2026-09-05-spec-regen-material-diff-b1.md
git commit -m "chore: 명세서 13개를 재생성해 검사 D 에 재료를 준다

지역 변수 표가 코퍼스 14개 중 1개(PROC_ETC, reset-ab 회차)에만 있어
검사 D 가 재료 부재로 침묵하고 있었다. 나머지 13개를 재생성했다.

전후 대조는 재료 단위로 했다 - 헤딩으로 세면 개명이 섞여 오탐한다.
기계 확정 표 개수 · 앵커 토큰 개수 · 스윕 「재료 분모」 절 셋을 맞댔고,
카나리아(기계 표가 문서의 첫 변수 헤딩인가)도 객체마다 확인했다."
```

---

## Self-Review

**스펙 커버리지**

| 스펙 요구 | 태스크 |
| :--- | :--- |
| §3 A-3 판별자 교체 | Task 1 |
| §3 A-2 토큰 후보 측정·규칙 선택 | Task 2 |
| §3 A-2 규칙 구현 | Task 3 |
| §3 A-1 재생성 (별도 승인) | Task 4 (헤더에 승인 게이트 명시) |
| §4 검증 계약 — 두 판에서 판정이 갈릴 것 | Task 1 Step 5-6, Task 2 Step 2, Task 3 Step 4 |
| §4 A-1 합격 ① 재료 단위 대조 셋 | Task 4 Step 5 |
| §4 A-1 합격 ② 카나리아 | Task 4 Step 2·6 |
| §4 A-1 합격 ③ 종료 상태 확인 | Task 4 Step 4 |
| §5 순서 제약 (재생성을 측정 뒤로) | Global Constraints + Task 4 헤더 |
| §6 범위 밖 (B·골격 결함) | 태스크 없음 — 의도된 것 |
| §7 병렬 경계 | Global Constraints |

**타입 정합**: `OmissionCommentScanner.Scan(string?) → IReadOnlyList<string>`와
`CheckSpecSetExpressions`의 시그니처는 어느 태스크도 바꾸지 않는다.
`SpecSetTarget`의 `Ordinal`·`Expressions`는 Task 3에서만 읽는다.
`StepCheckOracleTests`의 상수 `DefectiveBundle`·`CurrentPlan`은 Task 1이 정의하고 Task 3이 재사용한다.

**열린 항목** (스펙 §6에서 범위 밖으로 정한 것 — 태스크 없음이 의도다):
- B(채점 축 분할) — A 결과를 본 뒤 결정
- 골격 유래 결함: 공통 CATCH `@v_currentStepId` 초기값, `batch.ControlTotal` 컬럼 미정의
