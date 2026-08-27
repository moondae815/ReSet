# 캐시 16 → 17 승격과 명세서 전건 재생성 — 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** `CurrentCacheFormatVersion`을 16에서 17로 올려 「오류 코드」 표를 산출물에 싣고, 명세서 31개를
전건 재생성한 뒤, **발화가 늘어난 자리와 줄어든 자리를 모두 세어** 실제로 켜진 발화를 부류로 판정한다.

**Architecture:** 계측을 승격 **앞에** 놓아 기준선과 사후를 같은 도구로 뜬다. 계기는 둘이다 — 좌표
3자 차분(pre-(B) / post-(A) / post-(B))과 침묵 분모(가드에 걸린 문장·컬럼의 개수). 검증기의 판정
로직은 바꾸지 않고 가시성만 연다. 재생성은 카나리아 4객체를 관문으로 두고, 실패는 기록하고 완주한다.

**Tech Stack:** C# / .NET 10 · xUnit(+`SkippableFact`) · ScriptDom · Spectre.Console CLI ·
OpenRouter(`z-ai/glm-5.2` Actor·Consolidator, `deepseek/deepseek-v4-pro-0813` Critic)

**Spec:** `docs/superpowers/specs/2026-08-27-cache17-promotion-design.md`

## Global Constraints

- **테스트 게이트는 실패 0 · 건너뜀 0 · 경고 0.** 절대 통과 수는 환경 안에서도 최대 5까지 흔들려
  게이트로 못 쓴다. 건너뜀이 나오면 코퍼스 심링크가 빠진 것이다(`CorpusSkip.Reason`이 절차를 적는다).
- **커밋은 반드시 `git commit -- <경로>` 로 경로를 명시한다.** 공유 체크아웃이라 git 인덱스가 공유
  상태이고 남의 스테이징이 딸려 온다. 새 파일은 `git add <경로>` 후에도 `git commit -- <경로>`로 닫는다.
  커밋 **전에** `git diff --cached --name-only`를 본다.
- **`--job-name`을 절대 주지 않는다.** 주는 순간 `InstructionBundleWriter`가 `output/Jobs/*/agent/steps/`와
  `.../verification/`을 통째로 지우는 경로가 열린다(`Program.cs:905`).
- **`output.bak-2026-08-22`을 건드리지 않는다.** 스냅샷이 아니라 테스트 재료다
  (`CorpusPaths.PriorEdition` = `"output.bak-2026-08-22"`, `CoverageMapGoldenTests`의 기준 세대).
- **코퍼스 수치는 실제 리더로 잰다.** 정규식 근사는 실제 파서와 다르다.
- **측정 스윕은 0%(승격 전)와 100%(전건 완료) 두 자리에서만 뜬다.** 16/17 혼재 상태의 스윕은
  아무 의미 없는 수치다.
- **남이 준 수치를 옮겨 적지 않는다.** 물려받은 값(발화 86 → 46 등)은 직접 뜬 값으로 대체한다.

---

## File Structure

| 파일 | 책임 | 태스크 |
| :--- | :--- | ---: |
| `tests/ReSet.Core.Tests/ErrorCodeTableCorpusTests.cs` (신규) | 오류 코드 재료의 코퍼스 실측 + `CheckErrorCodes` 만족가능성 왕복 가드 | 2 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` (수정) | 가시성 개방 넷 + `BuildSpecTargets` 추출 | 3 |
| `src/ReSet.Core/Services/StepSweepModels.cs` (수정) | `SweepIndicators`에 침묵 분모 아홉 | 4 |
| `src/ReSet.Core/Services/StepSweepService.cs` (수정) | 침묵 분모를 본 루프에서 센다 | 4 |
| `src/ReSet.Core/Services/StepSweepReportWriter.cs` (수정) | 침묵 분모를 보고서에 인쇄 | 5 |
| `tests/ReSet.Core.Tests/StepSweepSilenceDenominatorTests.cs` (신규) | 침묵 분모의 단위 가드 | 4 |
| `src/ReSet.Core/Services/CacheManager.cs` (수정) | 상수 16 → 17 + 주석 | 7 |
| `docs/architecture.md` (수정) | 캐시 버전 문단 | 7 |
| `docs/audit-reports/sweeps/2026-08-27-step-sweep-pre-cache17.md` (신규) | 기준선 | 6 |
| `docs/audit-reports/sweeps/2026-08-27-step-sweep-post-cache17.md` (신규) | 사후 + 3자 차분 | 11 |
| `docs/known-defects.md` (수정) | (5-3-6) 절 | 12 |

**태스크 1·6·8·9·10·11·12는 코드 태스크가 아니다.** 실행·측정·판정이다. TDD 사이클 대신 **정확한
명령과 합격 기준**을 싣는다. 그 사실을 각 태스크 머리에 적는다.

---

### Task 1: 조율과 스냅샷 (국면 0)

**코드 태스크가 아니다.** 실행과 확인이다.

**Files:** 없음 (파일 시스템 작업)

**Interfaces:**
- Produces: `output.bak-cache17-20260827/` — 태스크 8~11이 사고 시 되돌릴 자리

- [ ] **Step 1: 다른 세션이 코퍼스를 읽고 있지 않은지 확인한다**

재생성이 `output/`을 덮으므로 이것이 먼저다. 사람에게 묻는다 — 「지금 `output/`을 읽는 다른 세션이
있습니까? 재생성이 코퍼스를 덮습니다.」 확인 없이 다음 스텝으로 가지 않는다.

- [ ] **Step 2: `appsettings.json`을 재생성 구간에 고치지 않는다는 확인을 받는다**

`src/ReSet.Cli/appsettings.json`의 OpenRouter 라우팅 고정이 `4e3d7ee`로 커밋돼 있다.
`"AllowFallbacks": false`라 `digitalocean`·`streamlake` 둘이 막히면 **404 "No endpoints found"로 즉시
실패**하며, 그 실패는 이 회차의 실패 양식(재시도 소진)과 구분되지 않는다.

- [ ] **Step 3: 재생성 시작 시점의 커밋 해시를 기록한다**

```bash
git log --oneline -1 | tee /tmp/cache17-base-commit.txt
git rev-parse HEAD
```

이 해시를 태스크 6·11의 두 보고서 머리말에 적는다. 실패가 났을 때 라우팅 탓인지 검사 탓인지 가를
근거다.

- [ ] **Step 4: 스냅샷을 뜬다**

```bash
cp -a output "output.bak-cache17-20260827"
du -sh output.bak-cache17-20260827
ls output.bak-cache17-20260827/Procedures | wc -l
```

Expected: `Procedures` 14개. `.git/info/exclude`에 `output.bak-*`가 등록돼 있어 추적되지 않는다.

**절대 `output.bak-2026-08-22`에 쓰지 않는다** — 그것은 `CoverageMapGoldenTests`의 기준 세대이고,
덮으면 그 검사가 실패가 아니라 **건너뜀**이 되어 아무것도 재지 않는 상태로 조용히 바뀐다.

- [ ] **Step 5: 스냅샷이 추적되지 않는지 확인한다**

```bash
git status --short | grep "output.bak-cache17" || echo "OK - 추적되지 않음"
```

Expected: `OK - 추적되지 않음`

---

### Task 2: 오류 코드 재료의 코퍼스 실측과 만족가능성 왕복 가드 (국면 1a·1b·1c)

이 회차 전체가 이 태스크의 두 수치 위에 선다 — **객체별 사실 개수**(카나리아 선정의 입력)와
**왕복 발화 0**(`CheckErrorCodes`가 만족 불가능한 지시가 아니라는 증거).

**Files:**
- Test: `tests/ReSet.Core.Tests/ErrorCodeTableCorpusTests.cs` (신규)

**Interfaces:**
- Consumes: `SpecExpectations.From(SpDefinition?)` (public, `SpecExpectations.cs:184`) ·
  `StepSweepService.RenderErrorCodeTable(IReadOnlyList<ErrorCodeFact>)` (public) ·
  `MechanicalValidator.Validate(string markdown, SpecExpectations? expectations = null)` (public) ·
  `ErrorType.ErrorCodeTableMissing` (public enum, `MechanicalValidator.cs:15`) ·
  `CorpusPaths.RepoRoot()` · `CorpusSkip.Reason`
- Produces: 테스트 출력에 실린 **객체별 `ErrorCodeFact` 개수**. 태스크 8의 카나리아 넷을 이 출력이 고른다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/ErrorCodeTableCorpusTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 「오류 코드」 표가 실물 코퍼스에서 만족 가능한 요구인지 본다.
    ///
    /// [왜 이 테스트가 필요한가] CheckErrorCodes는 코퍼스에서 한 번도 발화한 적이 없다 -
    /// expectations.ErrorCodes.Count == 0에서 항상 즉시 반환하기 때문이 아니라, 캐시 히트
    /// 산출물에는 L1이 아예 안 돌기 때문이다. 캐시 17이 표를 실으면 **검증된 적 없는 검사가
    /// 통째로 켜진다.** 그 검사가 만족 불가능한 지시라면 31개 객체가 한꺼번에 재시도를
    /// 소진한다. 승격 전에 그것부터 닫는다.
    ///
    /// [무엇을 증명하고 무엇을 증명하지 못하는가] 증명하는 것은 「완전 전사된 표를 검사가
    /// 통과한다」뿐이다. **모델이 그 표를 실제로 맞힐지는 증명하지 못한다** - 그건 재시도
    /// 소진으로만 드러나고 카나리아(계획서 태스크 8)로만 닫힌다.
    ///
    /// [왜 건수를 「하한」으로만 단언하는가] MachineTableExpansionCorpusTests가 건수를 아예
    /// 단언하지 않는 근거를 적는다 - 숫자로 못박으면 코퍼스에 SP가 하나 늘 때마다 빨개지고
    /// 다음 사람이 관측을 읽는 대신 기대값을 고친다. 그 근거에 동의하되 **하한은 다르다**:
    /// 하한은 코퍼스가 커져도 안 깨지고, 추출기가 조용히 망가져 전부 비는 경우를 잡는다.
    /// 하한이 없으면 이 테스트는 「발화 0」을 찍고 통과하는데 그 0이 「검사가 만족된다」가
    /// 아니라 「잴 재료가 없다」일 수 있다.
    /// </summary>
    public class ErrorCodeTableCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public ErrorCodeTableCorpusTests(ITestOutputHelper output) => _output = output;

        [SkippableFact]
        public void ErrorCodeTable_RenderedFromDdl_IsAcceptedByCheckErrorCodes()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var procedures = Path.Combine(root, "output", "Procedures");
            Skip.IfNot(Directory.Exists(procedures), CorpusSkip.Reason);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var validator = new MechanicalValidator();

            int objects = 0, objectsWithFacts = 0, factTotal = 0;
            var violations = new List<string>();

            foreach (var dir in Directory.GetDirectories(procedures)
                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                var meta = Path.Combine(dir, "raw", "metadata.json");
                if (!File.Exists(meta)) continue;

                var def = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(meta), opts);
                if (def == null) continue;

                var expectations = SpecExpectations.From(def);
                if (expectations == null) continue;

                objects++;
                var facts = expectations.ErrorCodes;
                if (facts.Count > 0)
                {
                    objectsWithFacts++;
                    factTotal += facts.Count;
                }

                // 갈래 1 - 완전 전사된 표. 사실이 있든 없든 발화가 없어야 한다.
                var rendered = StepSweepService.RenderErrorCodeTable(facts);
                foreach (var message in ErrorCodeMessages(validator, rendered, expectations))
                {
                    violations.Add($"{Path.GetFileName(dir)} [전사됨] {message}");
                }

                // 갈래 2 - 표가 아예 없는 문서. 사실이 0건인 객체는 여기서도 침묵해야
                // 한다(조기 반환). 사실이 있는 객체는 여기서 반드시 발화해야 한다 -
                // 발화하지 않으면 검사가 아무것도 지키지 않는다는 뜻이다.
                var withoutTable = "## 개요\n\n표가 없는 문서다.\n";
                var missing = ErrorCodeMessages(validator, withoutTable, expectations).ToList();

                if (facts.Count == 0 && missing.Count > 0)
                {
                    violations.Add(
                        $"{Path.GetFileName(dir)} [사실 0건인데 표를 요구] {missing[0]}");
                }

                if (facts.Count > 0 && missing.Count == 0)
                {
                    violations.Add(
                        $"{Path.GetFileName(dir)} [사실 {facts.Count}건인데 표 부재에 침묵]");
                }

                _output.WriteLine($"{Path.GetFileName(dir),-45} 오류 코드 사실 {facts.Count,3}");
            }

            _output.WriteLine("");
            _output.WriteLine(
                $"객체 {objects} · 사실을 가진 객체 {objectsWithFacts} · 사실 합 {factTotal}");

            Assert.True(objects > 0, "코퍼스 객체를 하나도 못 읽었다");

            // 하한이다. 정확한 값이 아니라 「재료가 있다」를 지킨다 - 추출기가 조용히
            // 망가져 전부 비면 위의 발화 0이 아무 뜻도 없는 0이 된다.
            Assert.True(
                objectsWithFacts > 0,
                $"오류 코드 사실을 가진 객체가 하나도 없다(객체 {objects}) - "
                + "DmlScopeExtractor.ExtractErrorCodes가 조용히 비었을 수 있다");
            Assert.True(
                factTotal > 0,
                $"오류 코드 사실 총수가 0이다(객체 {objects}) - 같은 이유로 의심한다");

            Assert.True(
                violations.Count == 0,
                "CheckErrorCodes가 만족 불가능하거나 아무것도 지키지 않는 자리가 있다:\n  "
                + string.Join("\n  ", violations));
        }

        /// <summary>
        /// L1을 통째로 돌리되 오류 코드 표에 관한 발화만 걷는다. Validate는 모든 검사를
        /// 돌리므로 합성 문서에서는 헤딩 부재 같은 무관한 발화가 잔뜩 난다 - 그것을 이
        /// 테스트의 판정에 섞으면 무엇을 재는지 모르게 된다.
        /// </summary>
        private static IEnumerable<string> ErrorCodeMessages(
            MechanicalValidator validator, string markdown, SpecExpectations expectations) =>
            validator.Validate(markdown, expectations).DetailedErrors
                .Where(e => e.Type == ErrorType.ErrorCodeTableMissing)
                .Select(e => e.Message);
    }
}
```

- [ ] **Step 2: 테스트를 돌려 어떻게 되는지 본다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ErrorCodeTableCorpusTests" -v n
```

Expected: **셋 중 하나**. 어느 쪽이든 이 태스크의 산출이다.

| 결과 | 뜻 | 다음 |
| :--- | :--- | :--- |
| PASS | 검사가 만족 가능하고 재료도 있다 | Step 3으로 |
| FAIL, `[전사됨]` 위반 | **`CheckErrorCodes`가 만족 불가능하다** | 정지. 승격 금지. 원인을 먼저 닫는다 |
| FAIL, `사실 0건인데 표를 요구` | 조기 반환이 안 돈다 | 정지. 같음 |
| FAIL, `표 부재에 침묵` | 검사가 아무것도 안 지킨다 | 정지. 같음 |
| SKIP | 코퍼스 심링크 누락 | `CorpusSkip.Reason`의 절차를 따라 붙이고 다시 |

**FAIL이 이 태스크의 실패가 아니다.** 승격 전에 그것을 발견하는 것이 이 태스크의 목적이다.
FAIL이면 사람에게 보고하고 멈춘다 — 계획서를 계속 진행하지 않는다.

- [ ] **Step 3: 출력의 객체별 사실 개수를 적어 둔다**

`dotnet test`의 `_output` 줄을 그대로 옮겨 `/tmp/cache17-errorcode-facts.txt`에 저장한다.
태스크 8의 카나리아 넷을 이 표가 고른다.

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~ErrorCodeTableCorpusTests" -v n \
  | tee /tmp/cache17-errorcode-facts.txt
```

- [ ] **Step 4: 전체 테스트가 여전히 초록인지 본다**

```bash
dotnet test tests/ReSet.Core.Tests
```

Expected: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 5: 커밋**

```bash
git add tests/ReSet.Core.Tests/ErrorCodeTableCorpusTests.cs
git diff --cached --name-only
git commit -m "test: 오류 코드 표가 코퍼스에서 만족 가능한 요구인지 먼저 잰다

CheckErrorCodes 는 코퍼스에서 한 번도 돌아 본 적이 없다 - 캐시 히트 산출물에는
L1 이 아예 안 돌기 때문이다. 캐시 17 이 표를 실으면 검증된 적 없는 검사가 통째로
켜지고, 만족 불가능한 지시라면 31 개 객체가 한꺼번에 재시도를 소진한다.

갈래 둘을 함께 본다. 완전 전사된 표에 발화가 없어야 하고, 표가 없는 문서에는
사실이 있는 객체가 반드시 발화하고 사실이 0 건인 객체는 침묵해야 한다. 뒤의 둘을
빼면 「발화 0」이 검사가 만족된다는 뜻인지 아무것도 안 지킨다는 뜻인지 갈리지
않는다.

건수는 하한으로만 단언한다. MachineTableExpansionCorpusTests 가 건수를 아예 안
박는 근거(코퍼스가 커지면 다음 사람이 관측 대신 기대값을 고친다)에 동의하되,
하한은 코퍼스가 커져도 안 깨지면서 추출기가 조용히 비는 경우를 잡는다." \
  -- tests/ReSet.Core.Tests/ErrorCodeTableCorpusTests.cs
```

---

### Task 3: 침묵 분모가 제품 규칙을 그대로 쓰게 가시성을 연다 (국면 1d 준비)

**규칙 사본을 만들지 않는다.** 이 저장소는 사본에 이미 데였다 — `QuerySpecificationsOf` 사본이
`ParameterColumnBindingExtractor`·`DerivedTableColumnExtractor`에 남아 있고 「이번 결함이 정확히 이
중복에서 났다」가 로드맵 메모에 적혀 있다. `StepSweepService.BareProcedureName`이
`MechanicalValidator.BareObjectName`(`internal`)을 부르는 것이 이 태스크가 따르는 전례다.

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs:6808` (`ResolveOrdinal`) ·
  `:6894` (`ResolveAnchoredStatements`) · `:6941` (`MergeErrorCodeMaps`) ·
  `:6979` (`StagingSources`) · `:6999` (`ReadsOnlyStaging`) · `:7074`·`:7352` (`specTargets` 두 자리)

**Interfaces:**
- Produces: 태스크 4가 부르는 다섯 —
  - `internal static int? ResolveOrdinal(StepSqlStatement, IReadOnlyDictionary<string, (string Kind, int Ordinal)>)`
  - `internal static List<(StepSqlStatement Statement, int? Ordinal)> ResolveAnchoredStatements(IReadOnlyList<StepSqlStatement>, IReadOnlyDictionary<string, (string Kind, int Ordinal)>)`
  - `internal static IReadOnlyDictionary<string, (string Kind, int Ordinal)> MergeErrorCodeMaps(IEnumerable<SpecStatementFacts>)`
  - `internal static IEnumerable<StepLineageSource> StagingSources(StepSqlStatement, HashSet<string>)`
  - `internal static bool ReadsOnlyStaging(StepSqlStatement, HashSet<string>)`
  - `internal static HashSet<string> BuildSpecTargets(IEnumerable<SpecStatementFacts>)` (신규 추출)

**설계서와의 차이를 명시한다.** 설계서 §3-3은 「가시성 변경 둘」이라 적었다. 실물을 읽어 보니
앵커 해결 계수(1·2)가 `ResolveOrdinal`·`ResolveAnchoredStatements`·`MergeErrorCodeMaps`를 함께
필요로 해서 **다섯**이 되고, `specTargets` 구성이 `:7074`와 `:7352`에 **이미 두 벌로 복제돼 있어**
`BuildSpecTargets` 추출이 하나 더 붙는다. 추출은 동일 표현식의 기계적 이동이라 판정을 바꾸지
않으며, 사본을 하나 줄인다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/StepSweepSilenceDenominatorTests.cs` (신규):

```csharp
using System.Collections.Generic;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 침묵 분모가 제품 규칙을 그대로 부르는지 본다.
    ///
    /// [왜 스윕이 아니라 여기서 보는가] 스윕은 코퍼스가 있어야 돌아 CI에서 건너뛴다.
    /// 이 가드는 합성 재료로 돌아 어디서나 빨개진다.
    /// </summary>
    public class StepSweepSilenceDenominatorTests
    {
        [Fact]
        public void BuildSpecTargets_CollectsTargetTablesFromDmlRows()
        {
            var facts = new List<SpecStatementFacts>
            {
                SpecStatementFactsForTest("dbo.TSettleMst", "dbo.TSettleByTX"),
            };

            var targets = MechanicalValidator.BuildSpecTargets(facts);

            Assert.Contains("dbo.TSettleMst", targets);
            Assert.Contains("dbo.TSettleByTX", targets);
        }

        /// <summary>
        /// DmlRows의 TargetTable만 채운 최소 재료. SpecStatementFacts의 다른 칸은
        /// BuildSpecTargets가 읽지 않는다.
        /// </summary>
        private static SpecStatementFacts SpecStatementFactsForTest(params string[] targetTables)
        {
            // 실제 형태는 Step 2에서 SpecStatementFacts/DmlRow의 실물 시그니처를
            // 읽어 채운다 - 이 헬퍼가 컴파일되지 않는 것이 Step 2의 기대 결과다.
            throw new System.NotImplementedException();
        }
    }
}
```

- [ ] **Step 2: 테스트를 돌려 실패를 확인하고 실물 시그니처를 읽는다**

```bash
dotnet build tests/ReSet.Core.Tests 2>&1 | grep -E "error CS" | head
```

Expected: `MechanicalValidator.BuildSpecTargets` 가 없다는 CS0117, 그리고 헬퍼의
`NotImplementedException`.

이제 실물 시그니처를 읽어 헬퍼를 채운다:

```bash
grep -n "record SpecStatementFacts\|class SpecStatementFacts" -A 20 \
  src/ReSet.Core/Services/SpecStatementFactsExtractor.cs | head -30
grep -n "DmlRows" src/ReSet.Core/Services/SpecStatementFactsExtractor.cs | head -5
```

읽은 시그니처로 `SpecStatementFactsForTest`를 실제 생성으로 바꾼다. `throw`를 남겨 두지 않는다.

- [ ] **Step 3: `BuildSpecTargets`를 추출한다**

`MechanicalValidator.cs`의 `StagingSources` 바로 위에 넣는다:

```csharp
        /// <summary>
        /// 명세서 DML 범위 표의 대상 테이블 집합. 계보 판정의 「원본이 쓰는 테이블인가」가
        /// 이 집합으로 결정된다.
        ///
        /// [왜 뽑아냈는가] 같은 두 줄이 CheckAnchoredStatementFacts와
        /// CheckAnchoredStatementExtras 두 자리에 복제돼 있었다. 스윕의 침묵 분모가 같은 집합을
        /// 세 번째로 복제할 자리라 여기서 끊는다 - BareObjectName·BareProcedureName이
        /// 따른 것과 같은 전례다.
        ///
        /// [왜 OrdinalIgnoreCase인가] 복제된 두 자리가 그랬다. 정규화가 마지막 식별자만
        /// 쓰므로 대소문자만 다른 표기가 같은 물리 테이블을 가리킨다.
        /// </summary>
        internal static HashSet<string> BuildSpecTargets(IEnumerable<SpecStatementFacts> facts) =>
            new HashSet<string>(
                facts.SelectMany(f => f.DmlRows).Select(r => r.TargetTable),
                StringComparer.OrdinalIgnoreCase);
```

그리고 `:7074`와 `:7352`의 두 자리를 각각 바꾼다:

```csharp
            var specTargets = BuildSpecTargets(facts);
```

- [ ] **Step 4: 가시성 다섯을 연다**

`private static` → `internal static`으로 바꾼다. **본문은 한 글자도 바꾸지 않는다.**

| 줄 | 지금 | 바꾼 뒤 |
| ---: | :--- | :--- |
| 6808 | `private static int? ResolveOrdinal(` | `internal static int? ResolveOrdinal(` |
| 6894 | `private static List<(StepSqlStatement Statement, int? Ordinal)> ResolveAnchoredStatements(` | `internal static List<...> ResolveAnchoredStatements(` |
| 6941 | `private static IReadOnlyDictionary<string, (string Kind, int Ordinal)> MergeErrorCodeMaps(` | `internal static ...` |
| 6979 | `private static IEnumerable<StepLineageSource> StagingSources(` | `internal static ...` |
| 6999 | `private static bool ReadsOnlyStaging(` | `internal static ...` |

각 멤버의 XML 문서 주석 끝에 한 줄을 더한다:

```
        /// [왜 internal인가] StepSweepService의 침묵 분모가 이 판정을 그대로 쓴다.
        /// 스윕이 사본을 두면 규칙이 두 곳에 생겨 미묘하게 갈린다 - BareObjectName이
        /// 같은 이유로 internal이다.
```

- [ ] **Step 5: 테스트가 통과하는지 본다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepSilenceDenominatorTests" -v n
dotnet test tests/ReSet.Core.Tests
```

Expected: 새 테스트 PASS. 전체 실패 0 · 건너뜀 0 · 경고 0.
**기존 검사 B·C 테스트가 하나라도 빨개지면 추출이 판정을 바꾼 것이다** — 되돌리고 다시 본다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/MechanicalValidator.cs \
        tests/ReSet.Core.Tests/StepSweepSilenceDenominatorTests.cs
git diff --cached --name-only
git commit -m "refactor: 침묵 분모가 쓸 판정 다섯을 internal 로 열고 specTargets 사본을 접는다

캐시 17 은 검사의 관할을 바꾼다. 앵커가 정상화되면 침묵하던 것이 발화하는 만큼
가려져 있던 침묵도 함께 켜지는데, 좌표 차분으로는 그 부류가 안 보인다 - 가드가
(A)에서도 (B)에서도 같이 침묵시키면 차분이 정의상 0 이다. 그래서 스윕이 가드에
걸린 문장 수를 직접 세야 하고, 그러려면 판정을 부를 수 있어야 한다.

사본을 만들지 않는다. QuerySpecificationsOf 사본이 낳은 결함이 로드맵 메모에
적혀 있고, BareProcedureName 이 BareObjectName 을 부르는 전례가 있다.

specTargets 구성은 이미 두 자리에 복제돼 있었다. 세 번째 사본이 될 자리라 여기서
BuildSpecTargets 로 접는다. 본문은 옮기기만 했고 판정은 바뀌지 않는다." \
  -- src/ReSet.Core/Services/MechanicalValidator.cs \
     tests/ReSet.Core.Tests/StepSweepSilenceDenominatorTests.cs
```

---

### Task 4: 침묵 분모 아홉을 스윕이 센다 (국면 1d)

**Files:**
- Modify: `src/ReSet.Core/Services/StepSweepModels.cs` (`SweepIndicators`에 init 속성 아홉)
- Modify: `src/ReSet.Core/Services/StepSweepService.cs:196~205` (본 루프의 `hasReusedCode` 계산 직후)
- Test: `tests/ReSet.Core.Tests/StepSweepSilenceDenominatorTests.cs` (태스크 3에서 만든 파일에 추가)

**Interfaces:**
- Consumes: 태스크 3이 연 다섯 + `StepSqlStatement.LineageSources` ·
  `.ReadsOwnTarget` · `.SubordinatePredicateColumns` · `.CodeAnchor` (전부 public)
- Produces: `SweepIndicators`의 새 속성 아홉 — 태스크 5의 보고서 작성기가 읽는다

```csharp
public int AnchorsResolved { get; init; }
public int AnchorsUnresolved { get; init; }
public int AnchorsDroppedForAmbiguity { get; init; }
public int StatementsWithLineage { get; init; }
public int StatementsReadingOnlyStaging { get; init; }
public int StatementsReadingOwnTarget { get; init; }
public int StagingExemptionsCancelledByOwnTarget { get; init; }
public int StatementsWithSubordinatePredicates { get; init; }
public int SubordinatePredicateColumnTotal { get; init; }
public int StagingSourceTotal { get; init; }
```

(열 개다 — 계수 1이 해결/미해결 둘로, 계수 5가 「자기 대상을 읽는다」와 「그래서 면제가 취소됐다」
둘로, 계수 6이 문장 수와 컬럼 수 둘로 갈린다.)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`StepSweepSilenceDenominatorTests.cs`에 더한다. 계수 5(취소)가 이 표에서 가장 중요하다 —
로드맵 메모가 경고한 Critical의 자리이고, 발견 당시 **관측 변화가 0**이었다.

```csharp
        /// <summary>
        /// 계수 5 - 「자기 대상을 읽어 스테이징 면제가 취소된 문장」을 실제로 센다.
        ///
        /// [왜 이 계수가 가장 중요한가] 2026-08-27 최종 리뷰가 잡은 Critical 이 정확히
        /// 이 자리다. 방어선 둘이 서로의 전제를 무너뜨려 검사 C 가 35 좌표에서 꺼졌는데
        /// **관측 변화가 0 이었다.** 이 계수가 승격 후에도 0 이면 그 방어가 도달하지
        /// 못한 것이고, 그건 수정이 살아 있다는 증거가 아니라 재지 않았다는 증거다.
        ///
        /// [왜 ReadsOwnTarget 을 직접 안 보는가] allSourcesAreStaging && !ReadsOnlyStaging
        /// 으로 유도한다. ReadsOnlyStaging 이 나중에 조건을 하나 더 얻어도 이 계수는
        /// 「무슨 이유로든 취소됐다」를 계속 옳게 센다 - 조건을 베끼면 그때 갈린다.
        /// </summary>
        [Fact]
        public void StagingExemptionCancelledByOwnTarget_IsCounted()
        {
            var specTargets = new HashSet<string>(
                new[] { "TSettleMst" }, StringComparer.OrdinalIgnoreCase);

            // 자기 대상(TSettleMst)을 FROM 별칭으로 다시 읽으면서 스테이징도 조인한다.
            // 리더의 자기참조 가드가 TSettleMst 를 행 원천에서 이미 뺐으므로 남은
            // LineageSources 는 스테이징 하나뿐이다.
            var statement = StatementWithLineage(
                readsOwnTarget: true, lineageSourceTables: new[] { "S06CancelSettle" });

            var allSourcesAreStaging =
                statement.LineageSources.Count > 0
                && MechanicalValidator.StagingSources(statement, specTargets).Count()
                   == statement.LineageSources.Count;

            Assert.True(allSourcesAreStaging, "재료가 틀렸다 - 원천이 전부 스테이징이어야 한다");
            Assert.False(
                MechanicalValidator.ReadsOnlyStaging(statement, specTargets),
                "ReadsOwnTarget 이 참인데 면제가 살아 있다 - 가드가 죽었다");
        }

        /// <summary>
        /// 자기 대상을 읽지 않으면 면제가 그대로 산다 - 위 테스트의 대조군이다.
        /// 이것이 없으면 위 단언이 「언제나 false」로도 통과한다.
        /// </summary>
        [Fact]
        public void StagingExemption_Survives_WhenStatementDoesNotReadItsOwnTarget()
        {
            var specTargets = new HashSet<string>(
                new[] { "TSettleMst" }, StringComparer.OrdinalIgnoreCase);

            var statement = StatementWithLineage(
                readsOwnTarget: false, lineageSourceTables: new[] { "S06CancelSettle" });

            Assert.True(MechanicalValidator.ReadsOnlyStaging(statement, specTargets));
        }
```

`StatementWithLineage` 헬퍼는 `StepSqlStatement`의 실물 init 속성으로 짓는다 —
`StepSqlStatementReader.cs:67·75·98·123`을 읽어 채운다. 이 헬퍼를 쓰기 전에:

```bash
sed -n '40,130p' src/ReSet.Core/Services/StepSqlStatementReader.cs
grep -n "record StepLineageSource\|class StepLineageSource" -A 8 \
  src/ReSet.Core/Services/StepSqlStatementReader.cs
```

- [ ] **Step 2: 테스트를 돌려 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepSilenceDenominatorTests" -v n
```

Expected: 컴파일 실패(헬퍼 미구현) 또는 FAIL. 헬퍼를 실물 시그니처로 채운 뒤 두 테스트가 PASS해야
한다 — 이 둘은 태스크 3이 연 가시성이 옳게 동작하는지 보는 것이므로, 여기서는 **PASS가 정상**이다.
FAIL이면 태스크 3의 개방이 잘못됐다.

- [ ] **Step 3: `SweepIndicators`에 속성 열을 더한다**

`StepSweepModels.cs`의 `SweepIndicators` 안, `StepsWithReusedCodeAnchors` 아래에 넣는다:

```csharp
        /// <summary>
        /// [침묵 분모] 캐시 17 이 앵커를 정상화하면 발화가 켜지는 만큼 **가려져 있던
        /// 침묵도 함께 켜진다.** 좌표 차분으로는 그 부류가 보이지 않는다 - 가드가
        /// 조건 (A)에서도 (B)에서도 같은 좌표를 침묵시키면 차분이 정의상 0 이기 때문이다.
        ///
        /// 승격 전에는 앵커가 안 풀려 면제가 **도달 불가능**하다. 그래서 아래 계수들의
        /// 증가분이 곧 「이번에 새로 생긴 침묵」이다.
        ///
        /// [사유가 아니라 분모다] 어느 좌표가 어느 가드에 침묵당했는지는 세지 않는다 -
        /// 그러려면 검증기가 판정 사유를 내보내야 하고, 그 결합보다 이 분모가 낫다고
        /// 봤다(StepsWithReusedCodeAnchors 가 같은 판단을 적는다).
        /// </summary>
        public int AnchorsResolved { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int AnchorsUnresolved { get; init; }

        /// <summary>
        /// 서수로는 환산됐으나 (Kind, Ordinal) 모호성 가드가 버린 문장 수.
        /// ResolveOrdinal 이 값을 낸 문장 수에서 ResolveAnchoredStatements 가 돌려준
        /// 문장 수를 뺀 값이라 근사가 아니라 같은 재료다.
        /// </summary>
        public int AnchorsDroppedForAmbiguity { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StatementsWithLineage { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StatementsReadingOnlyStaging { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StatementsReadingOwnTarget { get; init; }

        /// <summary>
        /// 원천이 전부 스테이징인데 자기 대상을 함께 읽어 **면제가 취소된** 문장 수.
        ///
        /// [이 회차에서 가장 중요한 계수] 2026-08-27 최종 리뷰의 Critical 이 이 자리다 -
        /// 방어선 둘이 서로의 전제를 무너뜨려 검사 C 가 35 좌표에서 꺼졌는데 발견 당시
        /// **관측 변화가 0 이었다.** 승격 후에도 이 값이 0 이면 방어가 도달하지 못한
        /// 것이고, 그건 수정이 살아 있다는 증거가 아니라 재지 않았다는 증거다.
        /// </summary>
        public int StagingExemptionsCancelledByOwnTarget { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StatementsWithSubordinatePredicates { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int SubordinatePredicateColumnTotal { get; init; }

        /// <inheritdoc cref="AnchorsResolved"/>
        public int StagingSourceTotal { get; init; }
```

- [ ] **Step 4: 본 루프에서 센다**

`StepSweepService.cs`의 `if (hasReusedCode) stepsWithReusedCodeAnchors++;` **바로 아래**에 넣는다.
조건 (B)의 사전(`factsSimulated`)으로 센다 — 캐시 17 이후의 관할을 재는 것이 목적이다.

```csharp
                        // [침묵 분모] SweepIndicators.AnchorsResolved 문서 참고.
                        // 조건 (B)의 사전으로 센다 - 캐시 17 이후의 관할을 재는 것이
                        // 목적이고, 조건 (A)로 세면 사전이 비어 전부 0 이 나와 승격
                        // 전후를 대조할 기준선이 생기지 않는다.
                        var simulatedFactsForStep = step.LegacyProcedures
                            .Select(p => MechanicalValidator.BareObjectName(p))
                            .Where(factsSimulated.ContainsKey)
                            .Select(bare => factsSimulated[bare])
                            .ToList();

                        var codeMapForStep = MechanicalValidator.MergeErrorCodeMaps(simulatedFactsForStep);
                        var specTargetsForStep = MechanicalValidator.BuildSpecTargets(simulatedFactsForStep);

                        var ordinalResolvable = stepStatements
                            .Count(st => MechanicalValidator.ResolveOrdinal(st, codeMapForStep).HasValue);
                        var anchoredForStep =
                            MechanicalValidator.ResolveAnchoredStatements(stepStatements, codeMapForStep);

                        anchorsResolved += anchoredForStep.Count;
                        anchorsUnresolved += stepStatements.Count - ordinalResolvable;
                        anchorsDroppedForAmbiguity += ordinalResolvable - anchoredForStep.Count;

                        foreach (var st in stepStatements)
                        {
                            if (st.LineageSources.Count > 0) statementsWithLineage++;
                            if (st.ReadsOwnTarget) statementsReadingOwnTarget++;

                            var stagingSourceCount =
                                MechanicalValidator.StagingSources(st, specTargetsForStep).Count();
                            stagingSourceTotal += stagingSourceCount;

                            var readsOnlyStaging =
                                MechanicalValidator.ReadsOnlyStaging(st, specTargetsForStep);
                            if (readsOnlyStaging) statementsReadingOnlyStaging++;

                            // [왜 ReadsOwnTarget 을 직접 안 보는가] 조건을 베끼면
                            // ReadsOnlyStaging 이 조건을 하나 더 얻을 때 갈린다.
                            // 「원천이 전부 스테이징인데 면제가 안 났다」로 유도하면
                            // 무슨 이유로 취소됐든 계속 옳게 센다.
                            var allSourcesAreStaging = st.LineageSources.Count > 0
                                && stagingSourceCount == st.LineageSources.Count;
                            if (allSourcesAreStaging && !readsOnlyStaging)
                            {
                                stagingExemptionsCancelledByOwnTarget++;
                            }

                            if (st.SubordinatePredicateColumns.Count > 0)
                            {
                                statementsWithSubordinatePredicates++;
                                subordinatePredicateColumnTotal += st.SubordinatePredicateColumns.Count;
                            }
                        }
```

메서드 상단의 카운터 선언부(`var multiProcedureSteps = 0;` 무리)에 열 개를 더한다:

```csharp
            var anchorsResolved = 0;
            var anchorsUnresolved = 0;
            var anchorsDroppedForAmbiguity = 0;
            var statementsWithLineage = 0;
            var statementsReadingOnlyStaging = 0;
            var statementsReadingOwnTarget = 0;
            var stagingExemptionsCancelledByOwnTarget = 0;
            var statementsWithSubordinatePredicates = 0;
            var subordinatePredicateColumnTotal = 0;
            var stagingSourceTotal = 0;
```

그리고 `return new SweepReport(...)`의 `SweepIndicators` 초기화에 열 줄을 더한다:

```csharp
                    AnchorsResolved = anchorsResolved,
                    AnchorsUnresolved = anchorsUnresolved,
                    AnchorsDroppedForAmbiguity = anchorsDroppedForAmbiguity,
                    StatementsWithLineage = statementsWithLineage,
                    StatementsReadingOnlyStaging = statementsReadingOnlyStaging,
                    StatementsReadingOwnTarget = statementsReadingOwnTarget,
                    StagingExemptionsCancelledByOwnTarget = stagingExemptionsCancelledByOwnTarget,
                    StatementsWithSubordinatePredicates = statementsWithSubordinatePredicates,
                    SubordinatePredicateColumnTotal = subordinatePredicateColumnTotal,
                    StagingSourceTotal = stagingSourceTotal,
```

- [ ] **Step 5: 스윕 서비스 테스트가 여전히 초록인지 본다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweep" -v n
dotnet test tests/ReSet.Core.Tests
```

Expected: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/StepSweepModels.cs \
        src/ReSet.Core/Services/StepSweepService.cs \
        tests/ReSet.Core.Tests/StepSweepSilenceDenominatorTests.cs
git diff --cached --name-only
git commit -m "feat: 스윕이 가드에 걸린 문장 수를 세어 새로 생긴 침묵을 드러낸다

승격 전에는 앵커가 안 풀려 면제가 도달 불가능하다. 그래서 면제 계수의 증가분이
곧 이번에 새로 생긴 침묵이다. 좌표 차분은 이 부류를 못 본다 - 가드가 (A)에서도
(B)에서도 같은 좌표를 침묵시키면 차분이 정의상 0 이다.

열 계수 중 StagingExemptionsCancelledByOwnTarget 이 가장 중요하다. 2026-08-27
최종 리뷰의 Critical 이 그 자리이고 발견 당시 관측 변화가 0 이었다. 승격 후에도
0 이면 방어가 도달하지 못한 것이지 수정이 살아 있다는 뜻이 아니다.

그 계수를 ReadsOwnTarget 으로 직접 세지 않고 「원천이 전부 스테이징인데 면제가
안 났다」로 유도한다. 조건을 베끼면 ReadsOnlyStaging 이 조건을 하나 더 얻을 때
갈리지만, 유도하면 무슨 이유로 취소됐든 계속 옳게 센다.

사유가 아니라 분모다. 어느 좌표가 어느 가드에 침묵당했는지는 세지 않는다 -
그러려면 검증기가 판정 사유를 내보내야 하고, StepsWithReusedCodeAnchors 가 같은
자리에서 같은 판단을 이미 적어 두었다." \
  -- src/ReSet.Core/Services/StepSweepModels.cs \
     src/ReSet.Core/Services/StepSweepService.cs \
     tests/ReSet.Core.Tests/StepSweepSilenceDenominatorTests.cs
```

---

### Task 5: 보고서가 침묵 분모를 인쇄한다 (국면 1d)

**Files:**
- Modify: `src/ReSet.Core/Services/StepSweepReportWriter.cs:226~249`
- Test: `tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs` (기존 파일에 추가)

**Interfaces:**
- Consumes: 태스크 4의 `SweepIndicators` 속성 열
- Produces: 보고서의 `## 침묵 분모` 절 — 태스크 6·11의 두 보고서가 이 절을 갖는다

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`StepSweepReportWriterTests.cs`에 더한다:

```csharp
        /// <summary>
        /// 침묵 분모가 보고서에 실리는지 본다. 안 실리면 계측이 있어도 다음 사람이
        /// 못 읽는다 - 이 회차의 산출은 코드가 아니라 두 보고서다.
        /// </summary>
        [Fact]
        public void Report_PrintsSilenceDenominators()
        {
            var report = new SweepReport(
                Array.Empty<SweepFinding>(),
                new SweepIndicators(0, 0, 0)
                {
                    AnchorsResolved = 12,
                    AnchorsUnresolved = 34,
                    AnchorsDroppedForAmbiguity = 5,
                    StatementsWithLineage = 7,
                    StatementsReadingOnlyStaging = 3,
                    StatementsReadingOwnTarget = 2,
                    StagingExemptionsCancelledByOwnTarget = 1,
                    StatementsWithSubordinatePredicates = 9,
                    SubordinatePredicateColumnTotal = 21,
                    StagingSourceTotal = 8,
                },
                GapsForTest());

            var markdown = StepSweepReportWriter.Write(report);

            Assert.Contains("## 침묵 분모", markdown);
            Assert.Contains("| 서수로 해결된 앵커 문장 수 | 12 |", markdown);
            Assert.Contains("| 자기 대상을 읽어 스테이징 면제가 취소된 문장 수 | 1 |", markdown);
        }
```

`GapsForTest()`와 `StepSweepReportWriter.Write`의 실물 시그니처는 같은 파일의 기존 테스트에서 그대로
가져온다 — 새로 짓지 않는다:

```bash
grep -n "StepSweepReportWriter.Write\|new HarnessGaps" tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs | head
```

- [ ] **Step 2: 테스트를 돌려 실패를 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~Report_PrintsSilenceDenominators" -v n
```

Expected: FAIL — `## 침묵 분모`가 없다.

- [ ] **Step 3: 절을 인쇄한다**

`StepSweepReportWriter.cs`의 「캐시 17 선결 지표」 절이 끝나는 자리(`:249`의 닫는 중괄호 앞)에 넣는다:

```csharp
            b.AppendLine("## 침묵 분모");
            b.AppendLine();
            b.AppendLine(
                "발화가 늘어난 자리만 보면 가려져 있던 침묵이 함께 켜지는 것을 못 본다. "
                + "승격 전에는 앵커가 안 풀려 면제가 도달 불가능하므로, 아래 값의 **증가분이 곧 "
                + "이번에 새로 생긴 침묵**이다. 좌표 차분은 이 부류를 못 본다 - 가드가 조건 (A)에서도 "
                + "(B)에서도 같은 좌표를 침묵시키면 차분이 정의상 0 이기 때문이다.");
            b.AppendLine();
            b.AppendLine("| 분모 | 값 |");
            b.AppendLine("| :--- | ---: |");
            b.AppendLine($"| 서수로 해결된 앵커 문장 수 | {indicators.AnchorsResolved} |");
            b.AppendLine($"| 서수로 환산되지 않은 문장 수 | {indicators.AnchorsUnresolved} |");
            b.AppendLine(
                $"| (Kind, Ordinal) 모호성 가드가 버린 문장 수 | {indicators.AnchorsDroppedForAmbiguity} |");
            b.AppendLine($"| 계보 원천을 가진 문장 수 | {indicators.StatementsWithLineage} |");
            b.AppendLine(
                $"| 스테이징만 읽어 검사 C 가 면제한 문장 수 | {indicators.StatementsReadingOnlyStaging} |");
            b.AppendLine($"| 자기 대상을 읽는 문장 수 | {indicators.StatementsReadingOwnTarget} |");
            b.AppendLine(
                "| 자기 대상을 읽어 스테이징 면제가 취소된 문장 수 | "
                + $"{indicators.StagingExemptionsCancelledByOwnTarget} |");
            b.AppendLine(
                "| 하위 범위 술어 컬럼을 가진 문장 수 | "
                + $"{indicators.StatementsWithSubordinatePredicates} |");
            b.AppendLine(
                $"| 그 컬럼의 총수 | {indicators.SubordinatePredicateColumnTotal} |");
            b.AppendLine($"| 스테이징 원천의 총수 | {indicators.StagingSourceTotal} |");
            b.AppendLine();
            b.AppendLine(
                "**「자기 대상을 읽어 스테이징 면제가 취소된 문장 수」가 0 이면 그 방어가 도달하지 "
                + "못한 것이다.** 수정이 살아 있다는 증거가 아니라 재지 않았다는 증거로 읽는다 "
                + "(2026-08-27 staging-lineage 최종 리뷰 Critical 1).");
            b.AppendLine();
            b.AppendLine(
                "이 표는 **사유가 아니라 분모**다. 어느 좌표가 어느 가드에 침묵당했는지는 세지 "
                + "않는다 - 그러려면 검증기가 판정 사유를 내보내야 한다.");
            b.AppendLine();
```

- [ ] **Step 4: 테스트가 통과하는지 본다**

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~StepSweepReportWriter" -v n
dotnet test tests/ReSet.Core.Tests
```

Expected: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/StepSweepReportWriter.cs \
        tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs
git diff --cached --name-only
git commit -m "feat: 스윕 보고서가 침묵 분모를 싣는다

계측이 있어도 보고서에 안 실리면 다음 사람이 못 읽는다. 이 회차의 산출은 코드가
아니라 승격 전후 두 보고서다.

취소 계수가 0 일 때의 읽는 법을 표 아래에 못 박는다 - 그 값이 0 인 것은 수정이
살아 있다는 증거가 아니라 재지 않았다는 증거다." \
  -- src/ReSet.Core/Services/StepSweepReportWriter.cs \
     tests/ReSet.Core.Tests/StepSweepReportWriterTests.cs
```

---

### Task 6: 계기 자체를 변이로 검증한다 (국면 1e)

**코드 태스크가 아니다.** 제품 코드에 일시적 변이를 넣고 계수가 움직이는지 보고, **반드시 되돌린다.**

**안 죽은 변이가 가장 값진 정보다.** 움직이지 않는 계수는 지우거나 고친다 — 안 움직이는 계기를
보고서에 싣는 것이 아무것도 안 싣는 것보다 나쁘다.

**Files:**
- 일시 수정 후 되돌림: `src/ReSet.Core/Services/MechanicalValidator.cs` ·
  `src/ReSet.Core/Services/StepSweepService.cs`
- Create: `docs/audit-reports/sweeps/2026-08-27-silence-denominator-mutations.md`

- [ ] **Step 1: 변이 전 기준값을 뜬다**

```bash
dotnet run --project src/ReSet.Cli -- --sweep
cp docs/audit-reports/sweeps/$(ls -t docs/audit-reports/sweeps | head -1) \
   /tmp/mutation-base.md
grep -A 14 "## 침묵 분모" /tmp/mutation-base.md
```

이 열 개 값을 적어 둔다.

- [ ] **Step 2: 변이 1 — `ReadsOnlyStaging`에서 `!statement.ReadsOwnTarget`을 뺀다**

`MechanicalValidator.cs:7001`의 `!statement.ReadsOwnTarget` 줄과 뒤따르는 `&&`를 지운다.

```bash
dotnet build src/ReSet.Cli && dotnet run --project src/ReSet.Cli -- --sweep
grep -A 14 "## 침묵 분모" docs/audit-reports/sweeps/$(ls -t docs/audit-reports/sweeps | head -1)
```

Expected: **「자기 대상을 읽어 스테이징 면제가 취소된 문장 수」가 0으로 떨어진다.**
그리고 「스테이징만 읽어 검사 C 가 면제한 문장 수」가 그만큼 는다.

**안 움직이면 그 계수는 아무것도 안 재고 있다.** 기록하고 계수를 고친다.

`git checkout -- src/ReSet.Core/Services/MechanicalValidator.cs`로 되돌린다.

- [ ] **Step 3: 변이 2 — `specTargetsForStep`을 빈 집합으로 만든다**

`StepSweepService.cs`의 `var specTargetsForStep = MechanicalValidator.BuildSpecTargets(...)` 를
`var specTargetsForStep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);` 로 바꾼다.

```bash
dotnet build src/ReSet.Cli && dotnet run --project src/ReSet.Cli -- --sweep
grep -A 14 "## 침묵 분모" docs/audit-reports/sweeps/$(ls -t docs/audit-reports/sweeps | head -1)
```

Expected: 「스테이징 원천의 총수」와 「스테이징만 읽어 검사 C 가 면제한 문장 수」가 **둘 다 는다**
(명세서 대상 제외가 사라지면 모든 계보 원천이 스테이징으로 보인다).

`git checkout -- src/ReSet.Core/Services/StepSweepService.cs`로 되돌린다.

- [ ] **Step 4: 변이 3 — `codeMapForStep`을 빈 사전으로 만든다**

`StepSweepService.cs`의 `var codeMapForStep = MechanicalValidator.MergeErrorCodeMaps(...)` 를
`var codeMapForStep = new Dictionary<string, (string Kind, int Ordinal)>(StringComparer.Ordinal);` 로 바꾼다.

Expected: 「서수로 해결된 앵커 문장 수」가 **0**이 되고 「서수로 환산되지 않은 문장 수」가 전체
문장 수와 같아진다.

`git checkout -- src/ReSet.Core/Services/StepSweepService.cs`로 되돌린다.

- [ ] **Step 5: 되돌아왔는지 확인한다**

```bash
git status --short src/
```

Expected: 비어 있음. **제품 코드에 변이가 남으면 이후 모든 측정이 오염된다.**

- [ ] **Step 6: 변이 결과를 기록한다**

`docs/audit-reports/sweeps/2026-08-27-silence-denominator-mutations.md`:

```markdown
# 침묵 분모의 변이 검증 (2026-08-27)

계기가 0 을 인쇄하고 통과했다고 말하는 또 하나의 검사가 되지 않게, 판정 단위마다
변이를 넣고 계수가 움직이는지 봤다. 제품 코드는 전부 되돌렸다.

| 변이 | 움직여야 하는 계수 | 실측 | 판정 |
| :--- | :--- | :--- | :--- |
| `ReadsOnlyStaging`에서 `!ReadsOwnTarget` 제거 | 면제 취소 수 | (기준 N → 변이 M) | (죽음/안 죽음) |
| `specTargetsForStep`을 빈 집합으로 | 스테이징 원천 총수 · 면제 수 | (…) | (…) |
| `codeMapForStep`을 빈 사전으로 | 해결/미해결 | (…) | (…) |

**안 죽은 변이**: (있으면 여기 적고, 그 계수를 고치거나 지운 결과를 함께 적는다.
없으면 「없음」이라고 적는다 — 빈 칸으로 두지 않는다.)
```

- [ ] **Step 7: 커밋**

```bash
git add docs/audit-reports/sweeps/2026-08-27-silence-denominator-mutations.md
git diff --cached --name-only
git commit -m "docs: 침묵 분모 계기를 변이로 검증한 결과를 적는다

계수가 0 을 인쇄하고 통과했다고 말하는 또 하나의 검사가 되지 않게 판정 단위마다
변이를 넣었다. 조건이 아니라 판정 단위다 - 안 죽은 변이가 가장 값진 정보다." \
  -- docs/audit-reports/sweeps/2026-08-27-silence-denominator-mutations.md
```

---

### Task 7: 기준선 스윕 (국면 2)

**코드 태스크가 아니다.** 이 회차의 기준선을 뜨는 자리다.

**Files:**
- Create: `docs/audit-reports/sweeps/2026-08-27-step-sweep-pre-cache17.md`

**Interfaces:**
- Produces: **pre-(B) 좌표 집합**과 **침묵 분모 열 개의 승격 전 값**. 태스크 11의 3자 차분이 이것을 쓴다.

- [ ] **Step 1: 작업 트리가 깨끗한지 본다**

```bash
git status --short
```

Expected: 비어 있음. 보고서가 실행 시점의 작업 트리 청결도를 함께 싣게 돼 있으므로(`2c549db`),
더러운 상태로 뜨면 그 사실이 보고서에 박힌다.

- [ ] **Step 2: 스윕을 돌린다**

```bash
dotnet run --project src/ReSet.Cli -- --sweep
```

- [ ] **Step 3: `FormatVersion`이 `{16}` 하나인지 확인한다**

```bash
NEW=$(ls -t docs/audit-reports/sweeps | head -1)
head -20 "docs/audit-reports/sweeps/$NEW"
```

Expected: `캐시 인덱스 FormatVersion 집합: {16} — 항목 31개`.
**`{16, 17}`이면 즉시 멈춘다** — 누군가 이미 승격했거나 부분 재생성이 돌았다는 뜻이고, 그 상태의
스윕은 아무 의미 없는 수치다.

- [ ] **Step 4: 기준선 이름으로 옮기고 머리말을 적는다**

```bash
mv "docs/audit-reports/sweeps/$NEW" \
   docs/audit-reports/sweeps/2026-08-27-step-sweep-pre-cache17.md
```

문서 맨 위에 절을 하나 더한다:

```markdown
## 이 보고서의 자리

**캐시 17 승격 전 기준선이다.** 태스크 11의 3자 차분이 이 문서의 조건 (B) 좌표 집합을
**pre-(B)** 로 쓴다.

- 착수 커밋: (Task 1 Step 3에서 기록한 해시)
- 물려받은 값과의 대조: `2026-08-27-step-sweep-c.md`(커밋 `be1f9b7`)는 조건 (B) 46 건이었다.
  이 회차의 기준선은 그 46 이 아니라 **이 문서가 직접 뜬 값**이다. 두 값이 다르면 그 사이
  변경이 발화를 움직인 것이고, **그것은 실패가 아니라 정보다.**

### 잰 것과 안 잰 것

- **잰 것**: 조건 (A)·(B) 발화, 침묵 분모 열, 캐시 17 선결 지표
- **안 잰 것**: 재생성의 시간·비용, 재생성 후 실제 발화량, 모델의 전사 정확도
```

- [ ] **Step 5: 좌표 집합을 기계가 읽을 수 있게 뽑아 둔다**

```bash
grep -E "^\| [0-9]+ \| [BC] \|" docs/audit-reports/sweeps/2026-08-27-step-sweep-pre-cache17.md \
  | awk -F'|' '{print $3","$5","$6","$7","$8}' | sed 's/ //g' | sort \
  > /tmp/pre-B-coordinates.txt
wc -l /tmp/pre-B-coordinates.txt
```

이 파일이 태스크 11의 차분 입력이다. **줄 수를 보고서 머리말에 적는다.**

- [ ] **Step 6: 커밋**

```bash
git add docs/audit-reports/sweeps/2026-08-27-step-sweep-pre-cache17.md
git diff --cached --name-only
git commit -m "docs: 캐시 17 승격 전 기준선 스윕을 뜬다

3 자 차분의 pre-(B) 다. 계측을 승격 앞에 둔 이유가 이 문서다 - 기준선과 사후를
같은 도구로 떠야 「발화가 줄어든 자리」를 셀 수 있다.

물려받은 46 을 옮겨 적지 않고 직접 떴다. 값이 다르면 be1f9b7 이후의 변경이 발화를
움직인 것이고 그것은 실패가 아니라 정보다." \
  -- docs/audit-reports/sweeps/2026-08-27-step-sweep-pre-cache17.md
```

---

### Task 8: 캐시 16 → 17 승격 (국면 3)

**Files:**
- Modify: `src/ReSet.Core/Services/CacheManager.cs:174` (+ 위 주석 블록)
- Modify: `docs/architecture.md:775` · `:828`

**Interfaces:**
- Produces: `CurrentCacheFormatVersion = 17`. 태스크 9·10의 재생성이 이 값으로 캐시를 미스 처리한다.

- [ ] **Step 1: 번호 충돌을 확인한다**

```bash
git branch -a --format='%(refname:short)' | while read b; do
  v=$(git show "$b:src/ReSet.Core/Services/CacheManager.cs" 2>/dev/null \
      | grep -o "CurrentCacheFormatVersion = [0-9]*")
  [ -n "$v" ] && echo "$b -> $v"
done
```

Expected: 어느 브랜치도 17을 쓰지 않는다. **쓰고 있으면 멈추고 그 브랜치와 조율한다** —
캐시 16 주석이 이 확인을 전례로 남겼다(`reset-l1-check` 스킬의 번호 충돌 규칙).

- [ ] **Step 2: 상수와 주석을 바꾼다**

`CacheManager.cs`의 `private const int CurrentCacheFormatVersion = 16;` 바로 위, 16번 주석 블록
아래에 넣고 상수를 `17`로 바꾼다:

```csharp
        // 17: 2026-08-27 「오류 코드」 표(문장 번호·오류 코드·대입 대상 변수)가 산출물에
        //     실린다. 표 자체는 2026-08-25에 프롬프트와 카탈로그에 들어갔으나 버전은 16에
        //     머물러 있었다 - 인상 전 스윕에서 단계 검사의 거짓 양성 원인 넷이 남아
        //     있었고, 그것을 닫기 전에 전건 재생성을 걸면 거짓 오류 33건이 한꺼번에
        //     켜지기 때문이다(같은 규칙의 두 번째 적용). 원인 넷은 2026-08-27에 닫혔다.
        //     프롬프트 입력(BuildErrorCodeTableLines가 싣는 표)과 출력 계약(명세서가 그
        //     표를 담아야 한다)이 함께 바뀌므로 인상 대상이다.
        //     이 표의 사실은 이미 「DML 범위」로 실린 문장 위에만 얹히므로 커버리지 맵의
        //     🟧→🟥 전이 창은 열리지 않는다.
        //     [이 인상이 처음 켜는 검사] CheckErrorCodes는 코퍼스에서 한 번도 돌아 본 적이
        //     없다 - 캐시 히트 산출물에는 L1이 영영 안 돌기 때문이다. 인상 전에
        //     ErrorCodeTableCorpusTests로 만족가능성을 확인했다: 완전 전사된 표에 발화 0,
        //     사실 0건인 객체는 조기 반환으로 침묵, 사실이 있는 객체는 표 부재에 발화.
        //     오탐을 안은 채 전건 재생성을 걸면 그것이 곧바로 재시도 소진으로 번진다.
        //     [이 인상이 관할을 바꾼다] 앵커가 정상화되면 검사 B·C가 도달하는 문장이
        //     늘고, 그만큼 **가려져 있던 침묵도 함께 켜진다.** 승격 전후 스윕의 「침묵
        //     분모」 절이 그 변화를 센다 - 발화가 늘었는지만 보면 그 부류를 못 본다.
        //     (main 대조: 인상 직전 main 값 16. 다른 브랜치도 16 이하라 17이 비어 있음을
        //     확인했다 - reset-l1-check 스킬의 번호 충돌 규칙.)
        private const int CurrentCacheFormatVersion = 17;
```

- [ ] **Step 3: `architecture.md`를 고친다**

`:775`의 긴 문단 끝에 있는 문장을 바꾼다. 지금은 이렇게 적혀 있다:

> **아홉째 표 「오류 코드」(문장 번호·오류 코드·대입 대상 변수)는 2026-08-25에 프롬프트와
> 카탈로그에 실렸지만 버전은 아직 16입니다**

이것을 다음으로 바꾼다:

> **17은 아홉째 표 「오류 코드」(문장 번호·오류 코드·대입 대상 변수)가 산출물에 실리는
> 회차입니다**(2026-08-27). 표 자체는 2026-08-25에 프롬프트와 카탈로그에 들어갔으나, 인상 전
> 스윕에서 단계 검사의 거짓 양성 원인 넷이 남아 있어 그것을 닫기 전에는 전건 재생성을 걸지
> 않기로 했습니다(같은 규칙의 두 번째 적용). 원인 넷은 2026-08-27에 닫혔습니다. 이 표의
> 사실은 이미 「DML 범위」로 실린 문장 위에만 얹히므로 커버리지 맵의 🟧→🟥 전이 창은 열리지
> 않습니다. 이 인상은 `CheckErrorCodes`가 **코퍼스에서 처음 도는 자리**이기도 합니다 —
> 인상 전에 `ErrorCodeTableCorpusTests`로 만족가능성을 확인했습니다. 설계와 측정은
> `docs/superpowers/specs/2026-08-27-cache17-promotion-design.md`와
> `docs/audit-reports/sweeps/2026-08-27-step-sweep-{pre,post}-cache17.md`에 있습니다.

`:775` 문단 앞부분의 「현재 값 16」도 「현재 값 17」로 바꾼다.

- [ ] **Step 4: 빌드와 테스트**

```bash
dotnet build src/ReSet.Cli
dotnet test tests/ReSet.Core.Tests
```

Expected: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/CacheManager.cs docs/architecture.md
git diff --cached --name-only
git commit -m "feat: 캐시 포맷 버전을 17 로 올려 오류 코드 표를 산출물에 싣는다

표 자체는 2026-08-25 에 프롬프트와 카탈로그에 들어갔으나 버전은 16 에 머물러
있었다. 인상 전 스윕에 단계 검사의 거짓 양성 원인 넷이 남아 있었고, 그것을 닫기
전에 전건 재생성을 걸면 거짓 오류 33 건이 한꺼번에 켜지기 때문이다. 원인 넷은
2026-08-27 에 닫혔다.

이 인상이 CheckErrorCodes 가 코퍼스에서 처음 도는 자리다. 캐시 히트 산출물에는
L1 이 영영 안 돌아 그 검사는 한 번도 실행돼 본 적이 없다. 인상 전에
ErrorCodeTableCorpusTests 로 만족가능성을 확인했다.

그리고 이 인상은 검사의 관할을 바꾼다. 앵커가 정상화되면 검사가 도달하는 문장이
느는 만큼 가려져 있던 침묵도 함께 켜진다 - 승격 전후 스윕의 침묵 분모 절이 그
변화를 센다." \
  -- src/ReSet.Core/Services/CacheManager.cs docs/architecture.md
```

---

### Task 9: 카나리아 4객체 (국면 4) — **관문**

**코드 태스크가 아니다.** 재생성의 첫 실측이자 관문이다.

**Files:** 없음 (`output/`이 바뀐다)

**Interfaces:**
- Consumes: 태스크 2가 낸 객체별 사실 개수 표(`/tmp/cache17-errorcode-facts.txt`)
- Produces: 재생성의 시간·비용 첫 실측. 태스크 10이 이 값으로 나머지 27을 계획한다.

- [ ] **Step 1: 카나리아 넷을 고른다**

`/tmp/cache17-errorcode-facts.txt`의 표에서 규칙대로 넷을 고른다:

| 자리 | 고르는 것 | 재려는 갈래 |
| ---: | :--- | :--- |
| 1 | 오류 코드 사실 **최다** 프로시저 | 전사 난이도 상한 |
| 2 | 사실 **최소(≠0)** 프로시저 | 하한 |
| 3 | 사실 **0건** 객체 | 조기 반환 — 검사가 **잠자야** 정상인 갈래 |
| 4 | 함수 하나 | 코퍼스 절반 이상(17/31)이 함수다 |

고른 넷의 이름과 사실 개수를 적어 둔다. 3번 자리에 해당하는 객체가 없으면(= 모든 객체가 사실을
가지면) 그 사실을 기록하고 자리 3을 함수 하나로 대체한다 — **없는 것을 억지로 만들지 않는다.**

- [ ] **Step 2: 넷을 재생성한다**

```bash
time dotnet run --project src/ReSet.Cli -- --sp <이름1>,<이름2>,<이름3>,<이름4>
```

**`--job-name`을 주지 않는다.** 주면 `output/Jobs/*/agent/steps/`가 통째로 지워진다.

- [ ] **Step 3: 관문을 판정한다**

세 가지를 넷 전부에 대해 본다.

```bash
python3 -c "
import json
d=json.load(open('output/.sp_cache_index.json'))
for k,v in sorted(d['Entries'].items()):
    print(v.get('FormatVersion'), k)
" | sort | uniq -c | head
```

① `FormatVersion` 17로 기록된 객체가 정확히 넷.

```bash
for n in <이름1> <이름2> <이름3> <이름4>; do
  f="output/Procedures/$n/docs/Spec.md"; [ -f "$f" ] || f="output/Functions/$n/docs/Spec.md"
  printf "%-50s " "$n"
  grep -c "### 오류 코드 (기계 확정 — 수정 금지)" "$f" 2>/dev/null || echo "파일 없음"
done
```

② 사실이 있는 객체는 표를 **담고**, 사실이 0건인 객체는 표를 **담지 않는다.**

**3번 자리의 합격 조건이 반대다.** 담고 나오면 프롬프트가 사실 없이도 표를 요구했다는 뜻이고,
만족 불가능한 지시가 27개 객체에 퍼지기 직전이라는 신호다.

```bash
grep -iE "재시도|retry|오류 코드 표" output/logs/*.log | tail -40
```

③ L1 재시도 소진 0.

| 판정 | 다음 |
| :--- | :--- |
| ①②③ 전부 통과 | 태스크 10으로 |
| 하나라도 어긋남 | **정지.** 사람에게 보고한다. 27개를 더 태운 뒤에 알면 되돌릴 것이 27배가 된다 |
| 404 "No endpoints found" | 라우팅 문제다(`AllowFallbacks: false`). 검사 문제와 구분해 보고한다 |

- [ ] **Step 4: 시간과 비용을 적어 둔다**

`time`의 출력과 OpenRouter 사용량을 `/tmp/cache17-canary-cost.txt`에 남긴다. **이것이 재생성 비용의
첫 실측**이고, 설계서가 「안 잰 값」으로 두었던 자리다. 이 값으로 나머지 27의 소요를 추정해 사람에게
보고한다.

---

### Task 10: 나머지 27 재생성 (국면 5)

**코드 태스크가 아니다.** 실패는 기록하고 완주한다.

**Files:** 없음 (`output/`이 바뀐다)

**Interfaces:**
- Produces: `FormatVersion` 17인 캐시 항목 31개(또는 「31 − 명시된 제외」). 태스크 11이 그 상태를 잰다.

- [ ] **Step 1: 남은 객체 목록을 뽑는다**

```bash
python3 -c "
import json
d=json.load(open('output/.sp_cache_index.json'))
for k,v in sorted(d['Entries'].items()):
    if v.get('FormatVersion') != 17:
        print(k.rsplit('.',1)[0])
" > /tmp/cache17-remaining.txt
wc -l /tmp/cache17-remaining.txt
```

Expected: 27줄.

- [ ] **Step 2: 나눠서 재생성한다**

README의 관례대로 2~4개씩 묶어 돌린다. **실패해도 다음 묶음으로 계속 간다.**

```bash
while read -r name; do
  echo "=== $name ==="
  dotnet run --project src/ReSet.Cli -- --sp "$name" \
    || echo "$name" >> /tmp/cache17-failed.txt
done < /tmp/cache17-remaining.txt
```

- [ ] **Step 3: 실패분만 2회차로 다시 돌린다**

```bash
[ -f /tmp/cache17-failed.txt ] && cat /tmp/cache17-failed.txt
mv /tmp/cache17-failed.txt /tmp/cache17-failed-round1.txt 2>/dev/null
while read -r name; do
  dotnet run --project src/ReSet.Cli -- --sp "$name" \
    || echo "$name" >> /tmp/cache17-failed.txt
done < /tmp/cache17-failed-round1.txt 2>/dev/null
```

- [ ] **Step 4: 최종 상태를 확인한다**

```bash
python3 -c "
import json
d=json.load(open('output/.sp_cache_index.json'))
from collections import Counter
c=Counter(v.get('FormatVersion') for v in d['Entries'].values())
print(dict(c))
for k,v in sorted(d['Entries'].items()):
    if v.get('FormatVersion') != 17: print('  남음:', k)
"
```

Expected: `{17: 31}`. 남은 것이 있으면 **이름을 그대로** 태스크 11의 보고서 제외 목록에 싣는다 —
「몇 개 실패」가 아니라 이름이다. 개수만 적으면 다음 사람이 어느 수치가 오염됐는지 못 되짚는다.

---

### Task 11: 사후 스윕과 3자 차분 (국면 6)

**코드 태스크가 아니다.** 이 회차의 산출이 여기서 나온다.

**Files:**
- Create: `docs/audit-reports/sweeps/2026-08-27-step-sweep-post-cache17.md`

**Interfaces:**
- Consumes: `/tmp/pre-B-coordinates.txt`(태스크 7) · 태스크 7 보고서의 침묵 분모 열
- Produces: 「줄어든 자리」 좌표 목록. 태스크 12의 판정 대상이다.

- [ ] **Step 1: 스윕을 돌린다**

```bash
git status --short
dotnet run --project src/ReSet.Cli -- --sweep
NEW=$(ls -t docs/audit-reports/sweeps | head -1)
head -20 "docs/audit-reports/sweeps/$NEW"
mv "docs/audit-reports/sweeps/$NEW" \
   docs/audit-reports/sweeps/2026-08-27-step-sweep-post-cache17.md
```

`FormatVersion` 집합이 `{17}`인지 본다. `{16, 17}`이면 태스크 10에 남은 것이 있다는 뜻이고,
그 이름을 보고서에 명시한다.

- [ ] **Step 2: 사후 좌표 집합 둘을 뽑는다**

```bash
P=docs/audit-reports/sweeps/2026-08-27-step-sweep-post-cache17.md
grep -E "^\| [0-9]+ \| [BC] \| A \|" "$P" \
  | awk -F'|' '{print $3","$5","$6","$7","$8}' | sed 's/ //g' | sort > /tmp/post-A-coordinates.txt
grep -E "^\| [0-9]+ \| [BC] \| B \|" "$P" \
  | awk -F'|' '{print $3","$5","$6","$7","$8}' | sed 's/ //g' | sort > /tmp/post-B-coordinates.txt
wc -l /tmp/post-A-coordinates.txt /tmp/post-B-coordinates.txt
```

발화 목록 표의 「조건」 칸 값이 `A`/`B`가 아니면 실물 라벨을 먼저 확인하고 필터를 고친다:

```bash
grep -E "^\| [0-9]+ \|" "$P" | head -3
```

- [ ] **Step 3: 다섯 영역을 계산한다**

```bash
echo "=== 예측대로 켜졌다 (pre-B ∩ post-A) ==="
comm -12 /tmp/pre-B-coordinates.txt /tmp/post-A-coordinates.txt | tee /tmp/diff-asexpected.txt | wc -l

echo "=== 줄어든 자리 (pre-B - post-B) ==="
comm -23 /tmp/pre-B-coordinates.txt /tmp/post-B-coordinates.txt | tee /tmp/diff-vanished.txt

echo "=== 전사 오류 (post-B - post-A) ==="
comm -23 /tmp/post-B-coordinates.txt /tmp/post-A-coordinates.txt | tee /tmp/diff-transcription.txt

echo "=== 모사가 못 낸 발화 (post-A - post-B) ==="
comm -13 /tmp/post-B-coordinates.txt /tmp/post-A-coordinates.txt | tee /tmp/diff-simgap.txt

echo "=== 새로 켜졌다 (post-B - pre-B) ==="
comm -13 /tmp/pre-B-coordinates.txt /tmp/post-B-coordinates.txt | tee /tmp/diff-new.txt
```

**「줄어든 자리」가 이 회차의 본체다.** 비어 있어도 그 사실을 적는다 — 빈 것과 안 잰 것은 다르다.

- [ ] **Step 4: 침묵 분모를 전후로 맞댄다**

```bash
diff <(grep -A 14 "## 침묵 분모" docs/audit-reports/sweeps/2026-08-27-step-sweep-pre-cache17.md) \
     <(grep -A 14 "## 침묵 분모" docs/audit-reports/sweeps/2026-08-27-step-sweep-post-cache17.md)
```

**「자기 대상을 읽어 스테이징 면제가 취소된 문장 수」를 반드시 확인한다.** 승격 후에도 0이면 그
방어가 도달하지 못한 것이고, 수정이 살아 있다는 증거가 아니라 **재지 않았다는 증거**다.

- [ ] **Step 5: 보고서에 차분 절을 더한다**

`2026-08-27-step-sweep-post-cache17.md` 맨 위에 넣는다:

```markdown
## 이 보고서의 자리

**캐시 17 승격 + 명세서 전건 재생성 후의 측정이다.** 기준선은
`2026-08-27-step-sweep-pre-cache17.md`.

- 착수 커밋: (Task 1 Step 3의 해시) / 측정 커밋: (지금 HEAD)
- 재생성 제외 객체: (태스크 10에서 남은 것의 **이름**. 없으면 「없음」)

## 3자 좌표 차분

| 영역 | 건수 | 읽는 법 |
| :--- | ---: | :--- |
| `pre-(B) ∩ post-(A)` | (…) | 예측대로 켜졌다 |
| **`pre-(B) − post-(B)`** | (…) | **명세서가 바뀌어 사라졌다 — 「줄어든 자리」** |
| `post-(B) − post-(A)` | (…) | 전사 오류. L1이 잡았어야 하는데 통과했다 |
| `post-(A) − post-(B)` | (…) | 모사가 못 낸 발화 |
| `post-(B) − pre-(B)` | (…) | 명세서가 새 술어·행을 얻어 켜졌다 |

**집합 크기를 결론으로 쓰지 않는다.** `output/Jobs` 20편은 같은 원본 SP 12~14개를
8/12~8/24에 반복 생성한 판이라 좌표가 독립이 아니다 — 같은 문장 하나가 다섯 번까지
세어진다. 판정은 태스크 12의 부류 접기 뒤에만 선다.

**비교에서 뺀 특이 표본 다섯**: Proc4(73단계 선언, 상한 40 초과) · Proc7(빈 `Steps`) ·
Proc5·Proc20(`agent/steps/` 0건) · Proc6. 잡 자체의 고질이라 세대 차이가 아니다.

### 줄어든 자리 — 전건

(`/tmp/diff-vanished.txt`의 좌표를 표로. 비어 있으면 「없음」이라고 적는다.)

### 전사 오류 — 전건

(`/tmp/diff-transcription.txt`. 0 이면 L1의 세 칸 대조가 간극을 닫았다는 뜻이다.
0 이 아니면 **그쪽이 더 중요한 발견**이다 — 세 칸을 다 맞대는데도 통과한 전사 오류다.)

## 침묵 분모 전후

| 분모 | 승격 전 | 승격 후 | 증감 |
| :--- | ---: | ---: | ---: |
(열 줄)

### 잰 것과 안 잰 것

- **잰 것**: 조건 (A)·(B) 발화, 3자 차분, 침묵 분모 전후, 재생성 성공 객체 수
- **안 잰 것**: (실제로 못 잰 것을 적는다. 「없음」으로 때우지 않는다)
```

- [ ] **Step 6: 커밋**

```bash
git add docs/audit-reports/sweeps/2026-08-27-step-sweep-post-cache17.md
git diff --cached --name-only
git commit -m "docs: 캐시 17 승격 후 스윕과 3 자 좌표 차분을 뜬다

발화가 늘어난 자리만 보면 가려져 있던 침묵이 함께 켜지는 것을 못 본다. 줄어든
자리(pre-(B) - post-(B))를 전건으로 싣고, 좌표 차분이 구조적으로 못 보는 부류는
침묵 분모 전후 대조가 잡는다.

집합 크기를 결론으로 쓰지 않는다. Jobs 20 편이 같은 원본 SP 의 반복 판이라 같은
문장 하나가 다섯 번까지 세어진다." \
  -- docs/audit-reports/sweeps/2026-08-27-step-sweep-post-cache17.md
```

---

### Task 12: 부류 판정과 기록 (국면 7·8)

**코드 태스크가 아니다.** (5-3-3)이 37건에 쓴 방법을 그대로 쓴다 — 새 방법을 만들지 않는다.

**Files:**
- Modify: `docs/known-defects.md` (새 절 (5-3-6))
- Modify: `/Users/payletter/.claude/projects/-Users-payletter-git-root-ReSet/memory/axis-b-roadmap.md`

**Interfaces:**
- Consumes: `/tmp/post-A-coordinates.txt` · `/tmp/diff-vanished.txt`

- [ ] **Step 1: 판정 대상을 확정한다**

**둘 다 판정한다.** 후자를 빼면 이 회차의 목적 절반이 사라진다.

1. `post-(A)`의 발화 전부 (실제로 켜진 것)
2. `pre-(B) − post-(B)` 전부 (줄어든 자리)

- [ ] **Step 2: 좌표마다 셋을 함께 연다**

각 좌표 `(검사, Job, 단계, 문장, 항목)`에 대해:

```bash
sed -n '/```sql/,/```/p' "output/Jobs/<Job>/agent/steps/<단계>.md" | head -80
grep -n -A 30 "DML 범위" "output/Procedures/<SP>/docs/Spec.md"
grep -n -A 30 "집합 술어" "output/Procedures/<SP>/docs/Spec.md"
python3 -c "
import json,sys
d=json.load(open('output/Procedures/<SP>/raw/metadata.json'))
print(d['DdlText'][:4000])
"
```

- [ ] **Step 3: 두 축으로 접는다**

**접는 축이 둘이다.**

1. **같은 원본 SP의 반복 판** — 먼저 접는다. 안 접으면 같은 문장 하나가 다섯 배로 세어져 부류별
   건수가 통째로 왜곡된다.
2. **원인 축** — (5-3-3)이 쓴 것(37건 → 좌표 15가지 → 부류 여섯).

**부류로 접는 것은 인스턴스를 최소 둘 열어 구조가 같음을 확인한 뒤에만** 한다.

- [ ] **Step 4: `known-defects.md`에 (5-3-6) 절을 쓴다**

`(5-3-5)` 절 뒤에 넣는다. 담을 것:

- 승격 결과 (`FormatVersion` 분포, 재생성 성공/제외 객체의 **이름**)
- 3자 차분 다섯 영역의 건수와 그 읽는 법
- **줄어든 자리 전건의 좌표와 판정** — 이 회차에서 가장 중요한 절
- 침묵 분모 전후 (특히 「면제 취소」 계수)
- 부류 판정 표 (부류 · 건수 · 판정: 진짜 결함 / 구조적 오탐 / 판정 불가)
- 전사 오류가 0이 아니면 그 원인 진단
- **「잰 것」과 「안 잰 것」**

- [ ] **Step 5: 로드맵 메모를 갱신한다**

`memory/axis-b-roadmap.md`에서 고칠 것:

- 「4. 캐시 16 → 17 · 명세서 전건 재생성」을 **완료**로 바꾸고 커밋과 두 보고서를 건다
- 「⚠ 로드맵 4가 방어를 끄는 형태의 위험」 절에 **실측 결과**를 더한다 — 침묵 분모의 「면제 취소」
  계수가 실제로 얼마였는지. 이 경고가 값을 냈는지 안 냈는지가 다음 사람에게 필요한 정보다
- 미결 항목에서 `CheckErrorCodes` 줄을 **해소**로 바꾼다(태스크 2가 닫았다)
- 로드맵 5의 입력을 적는다 — 20편 전부가 아니라 특이 표본 다섯을 뺀 6~8편 표본이면 분포 비교가
  선다(`memory/jobs-corpus-shape.md`), 그리고 `aec9ea1`의 새 규칙 여섯이 아직 산출물에 없다

- [ ] **Step 6: 커밋**

```bash
git add docs/known-defects.md
git diff --cached --name-only
git commit -m "docs: 캐시 17 승격 후 발화의 부류 판정을 적는다

post-(A) 의 발화와 줄어든 자리(pre-(B) - post-(B))를 둘 다 판정했다. 후자를 빼면
이 회차의 목적 절반이 사라진다.

두 축으로 접었다. 같은 원본 SP 의 반복 판을 먼저 접고 그 다음 원인 축으로 접는다 -
반복 판을 안 접으면 같은 문장 하나가 다섯 배로 세어진다." \
  -- docs/known-defects.md
```

- [ ] **Step 7: 스냅샷을 지울지 사람에게 묻는다**

```bash
du -sh output.bak-cache17-20260827
```

**스스로 지우지 않는다.** 다음 회차(로드맵 5)가 되짚을 자리일 수 있다.
`output.bak-2026-08-22`은 어느 경우에도 건드리지 않는다.

---

## Self-Review

**1. 설계서 각 절의 담당 태스크**

| 설계서 | 태스크 |
| :--- | :--- |
| §0 착수 시점의 실측 | 1 (해시 기록) · 2 (사실 개수) |
| §1-1 삭제 경로 | 9·10의 `--job-name` 금지 · Global Constraints |
| §1-2·1-3 렌더·이스케이프 대칭 | 2 (왕복 가드가 못 박는다) |
| §1-4 L1이 더 엄하다 | 2 (테스트 주석) · 11 Step 3 (전사 오류 영역) |
| §2 위험 1 (방어를 끈다) | 4 (면제 취소 계수) · 5 (읽는 법) · 11 Step 4 |
| §2 위험 2 (검증된 적 없는 검사) | 2 · 9 (카나리아 관문 ②③) |
| §2 위험 3 (되돌릴 수 없음) | 1 Step 4 (스냅샷) |
| §3-1 3자 좌표 차분 | 7 Step 5 · 11 Step 2~3 |
| §3-1 좌표는 독립이 아니다 | 11 Step 5 · 12 Step 3 |
| §3-2 침묵 분모 | 3 · 4 · 5 |
| §3-3 판정 로직 불변 | 3 (가시성만, 본문 불변) · 3 Step 5 |
| §3-4 변이 검증 | 6 |
| §3-5 못 하는 것 | 5 (보고서가 스스로 적는다) |
| §3-6 0%/100% | 7 Step 3 · 11 Step 1 |
| §4 국면 0~8 | 1·2~6·7·8·9·10·11·12 |
| §4-1 조율 위험 | 1 Step 2~3 · 9 Step 3(404 갈래) |
| §5 게이트와 규약 | Global Constraints |
| §6-1 다른 세션의 기대 | 12 Step 5 (로드맵 5 입력) |
| §6 닫지 않는 것 | 12 Step 5 |

**빠진 것 없음.**

**2. 플레이스홀더 스캔**

`(…)`는 보고서 **양식의 빈칸**이며, 그 자리를 채우는 명령이 바로 앞 스텝에 있다. 태스크 3 Step 1의
`throw new NotImplementedException()`은 **의도된 실패**이고 Step 2가 실물 시그니처로 채운다 —
그 사실을 코드 주석과 스텝 본문이 둘 다 적는다. 「적절히 처리」류 문장 없음.

**3. 이름 일관성**

`BuildSpecTargets`(태스크 3 정의 → 4 사용) · `StagingExemptionsCancelledByOwnTarget`(4 정의 →
5 인쇄 → 11 대조) · `AnchorsResolved`(4 → 5) · `RenderErrorCodeTable`(기존 public, 2 사용) ·
`ErrorType.ErrorCodeTableMissing`(기존, 2 사용) · `/tmp/pre-B-coordinates.txt`(7 생성 → 11 소비) ·
`/tmp/cache17-errorcode-facts.txt`(2 생성 → 9 소비). **어긋남 없음.**

**한 가지 설계서와의 차이를 명시한다.** 설계서 §3-3은 가시성 변경을 「둘」이라 적었으나 실물을 읽어
보니 **다섯 + `BuildSpecTargets` 추출**이 필요하다(태스크 3 머리에 근거를 적었다). 판정 로직은
여전히 바뀌지 않는다.
