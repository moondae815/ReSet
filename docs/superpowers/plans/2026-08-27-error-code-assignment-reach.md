# 오류 코드 대입 도달성 검사 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 단계가 선언한 레거시 오류 코드가 **표에 나열만 되고 실제로 실리지 않는 것**을 L1이 잡는다 — 규칙 6-1이 T-SQL 구문(`DECLARE @v_currentStepId INT = 0`)으로 지키던 실패 지점 충실도를, 언어 이전 뒤에도 남는 기록 기준으로 옮긴다.

**Architecture:** 먼저 코퍼스를 **의무 기준**으로 재서 대입 실패 집합을 확정하고, 그 집합을 채점표로 삼아 검사 하나를 더한다. 프롬프트 규칙은 건드리지 않는다 — 그래야 발화가 "검사가 일한 것"인지 "계획서가 달라진 것"인지 구별된다.

**Tech Stack:** .NET 10, xUnit

**Spec:** `docs/superpowers/specs/2026-08-27-stage3-rule-rewrite-design.md`

## Global Constraints

- **프롬프트(`AiService.cs`)와 `ConsolidatedPlanRules`를 건드리지 않는다.** 규칙 다시 쓰기는 3단계이고 이 계획의 범위 밖이다. 만지면 접두사 캐시가 무효화되어 전건 재생성 비용이 확정된다.
- **재생성(`output/` 쓰기)을 하지 않는다.** 다른 세션이 코퍼스 스윕 전후 대조를 증거로 쓰고 있어 분모가 바뀌면 그 대조가 무의미해진다. 이 계획은 **코퍼스를 읽기만** 한다.
- **이름을 역할의 대리로 쓰지 않는다.** 직전 회차가 같은 자리에서 세 라운드 연속 이 실수를 했다 — `Legacy*`라는 이름으로 운반체를 찾아 `ErrorCode`·`LegacyErrorCode`를 놓쳤다. 판정은 **의무 이행 여부**로 한다.
- **"문서에 등장"과 "실제로 싣는다"를 구별한다.** 컨트롤러 실측: 선언 코드 1,476개가 문서에는 **100%** 등장하지만 대입 자리에는 **90%**만 나타난다. 전자로 재면 검사가 아무것도 안 잡는다.
- **코퍼스 검증은 심링크 둘을 걸고 건너뜀 0에서 한다.**
  ```bash
  ln -s /Users/payletter/git-root/ReSet/output output
  ln -s /Users/payletter/git-root/ReSet/output.bak-2026-08-22 output.bak-2026-08-22
  ```
  ⚠️ `output.bak-2026-08-22`은 **테스트 재료**다(`CorpusPaths.PriorEdition`). 절대 쓰지 말 것.
- **절대 테스트 통과 수를 게이트로 쓰지 않는다.** 환경마다 최대 5까지 어긋나는 것이 관측됐고 원인은 미상이다. 같은 환경에서 전후 **차분**을 본다.
- **`git stash` 금지.** 스택이 메인 체크아웃·다른 워크트리와 공유되고 다른 세션이 동시에 작업한다.
- **`cd` 뒤에 브랜치 단언을 같은 명령에 묶는다.** 이 회차에 셸 작업 디렉터리가 흘러 공유 체크아웃의 브랜치를 옮기는 사고가 났고, 두 번째는 이 단언이 막았다.
  ```bash
  cd <경로> && [ "$(git branch --show-current)" = "<내 브랜치>" ] && git <변경 명령>
  ```
- **다른 세션이 `MechanicalValidator.cs`의 `CheckAnchoredStatementFacts`·`CheckAnchoredStatementExtras`를 동시에 만진다.** 그 둘을 건드리지 말 것.
- 기준선: `dotnet build` 경고 0·오류 0, `dotnet test` 실패 0·건너뜀 0.

---

## File Structure

| 파일 | 책임 | 태스크 |
|---|---|---|
| `docs/audit-reports/sweeps/2026-08-27-error-code-reach-sweep.md` | 의무 기준 실측 보고서 — 대입 실패 집합의 정본 | 1 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | `CheckErrorCodeAssignmentReach` 추가, `ValidateBatchStep`에서 호출 | 2 |
| `tests/ReSet.Core.Tests/ErrorCodeAssignmentReachTests.cs` | 새 파일 — 검사 동작과 변이 잠금 | 2 |

> **[실행 결과]** 위 표의 **태스크 2 행 둘은 만들어지지 않았다.** Task 2가 취소됐다 — 아래 Task 2 머리의 상자를 보라. 실제로 남은 산출물은 1행(측정 보고서)뿐이다.

`MechanicalValidator.cs`는 이미 매우 크지만 이 계획에서 쪼개지 않는다 — 배치 단계 검사들이 같은 진입점을 공유하는 구조라 분리하면 범위를 훨씬 넘는다.

---

### Task 1: 의무 기준으로 대입 실패 집합을 확정한다

> **[실행 결과 — Step 3의 정지 조건이 실제로 발동했다]** 대입 자리에 없던 151개를
> 전수로 갈랐더니 **「정당한 미대입」이 62건**이었다(정규식의 맹점 87 · 진짜 결손 2).
> Step 3의 정지 조건대로 **Task 2로 넘어가지 않았다.** 측정 보고서는 계획대로
> `docs/audit-reports/sweeps/2026-08-27-error-code-reach-sweep.md`에 있다.

**Files:**
- Create (임시, 끝나면 삭제): `tests/ReSet.Core.Tests/TempReachProbe.cs`
- Create: `docs/audit-reports/sweeps/2026-08-27-error-code-reach-sweep.md`

**Interfaces:**
- Consumes: 없음
- Produces: 보고서. **Task 2의 채점표다** — 그 검사의 발화 집합이 이 목록과 정확히 일치해야 한다.

**배경:** 규칙 6-1이 `DECLARE @v_currentStepId INT = 0`을 지시하고 L1의 `CheckStepIdInitialValue`가 그 구문을 찾는다. 언어 이전 뒤에는 그 구문이 사라진다. 이 태스크는 **그 규칙이 지키려던 것**(실패 지점마다 다른 코드를 싣는다)이 지금 코퍼스에서 얼마나 지켜지는지 잰다.

- [ ] **Step 1: 코퍼스 경로와 재료를 먼저 확인한다**

계획서가 지정한 경로에 문서가 실제로 있는지 **먼저 세어라.** 직전 회차에 브리프가 지정한 글롭에 문서가 0건이었고, 그대로 돌렸다면 아무것도 안 읽고 "영향 0"이 나왔을 뻔했다.

```bash
ls /Users/payletter/git-root/ReSet/output/Jobs/*/raw/PlanStructure.md | wc -l
ls /Users/payletter/git-root/ReSet/output/Jobs/*/agent/steps/*.md | wc -l
```

기대: `PlanStructure.md` **22**(Job 전부), `steps/*.md`는 수백 건. 앞자리가 다르면 멈추고 보고하라.

- [ ] **Step 2: 컨트롤러 실측을 재현한다**

이 수치가 출발점이므로 직접 재현해 보고서에 싣는다. 컨트롤러가 쓴 프로브는 이렇다.

```python
import json, re, os, glob
ROOT = "/Users/payletter/git-root/ReSet"
for j in sorted(glob.glob(os.path.join(ROOT, "output/Jobs/*"))):
    ps = os.path.join(j, "raw", "PlanStructure.md")
    if not os.path.exists(ps): continue
    steps = None
    for b in re.findall(r"```json\s*(.*?)```", open(ps, encoding="utf-8").read(), re.S):
        try: d = json.loads(b)
        except Exception: continue
        if "Steps" in d: steps = d["Steps"]; break
    if not steps or len(steps) > 40: continue
    for s in steps:
        codes = [str(c).strip() for c in (s.get("ErrorCodes") or []) if str(c).strip()]
        if not codes: continue
        f = os.path.join(j, "agent", "steps", (s.get("Code") or "") + ".md")
        if not os.path.exists(f): continue
        body = open(f, encoding="utf-8").read()
        asg = set(re.findall(r"=\s*(-?\d+)\s*[;,)]", body)) | \
              set(re.findall(r"SET\s+@\w+\s*=\s*(-?\d+)", body, re.I))
        # 여기서 codes 각각이 body에 있는지(등장), asg에 있는지(대입) 센다
```

기대(2026-08-27 컨트롤러 실측): Job 17개 · 단계 241개 · 선언 코드 **1,476개**. 등장 **1,476(100%)**, 대입 자리 **1,325(90%)**. Job별로 76%~100%이고 `Proc19`가 76%로 가장 낮다.

수치가 다르면 그 사실을 보고서에 적고 **당신이 잰 값을 쓴다** — 위 수치가 틀렸을 수 있다.

`len(steps) > 40` 필터는 `BatchStepPlanParser.MaxSteps`와 같은 기준이다. 그래서 Job 22개 중 17개만 남는다 — **그 사실도 보고서에 적어라.**

- [ ] **Step 3: 151개를 세 갈래로 가른다 — 이 태스크의 본체**

Step 2의 정규식은 **근사**다. 이 회차에서 창 휴리스틱이 세 번 틀렸다. **대입 자리에 없는 151개를 하나씩 열어** 셋 중 어디인지 판정하라.

| 갈래 | 뜻 | 검사가 |
|---|---|---|
| **진짜 결손** | 코드를 선언하고 어디서도 안 싣는다 | **잡아야 한다** |
| **정규식의 맹점** | `CASE`·`ISNULL`·`CONVERT`·변수 경유로 실린다 | 잡으면 안 된다 → 기준을 넓힌다 |
| **정당한 미대입** | 그 코드가 **이 단계에서 발생할 수 없다** | 잡으면 안 된다 |

각 판정에 **원문 좌표**를 적어라 — 어느 파일 어느 줄이 근거인지.

> **정지 조건: 「정당한 미대입」이 하나라도 나오면 멈추고 보고하라.**
>
> 그러면 검사는 "선언 전부"가 아니라 "이 단계가 대체하는 분기의 코드"만 봐야 하고, **그 판정은 새 추정이다.** 설계를 다시 해야 하므로 Task 2로 넘어가지 마라.

- [ ] **Step 4: 자동 판정이 가능한지 시험하고, 안 되면 그렇게 적는다**

Step 3의 판정을 정규식으로 자동화할 수 있으면 프로브를 만들어 전수로 돌린다.

**자동 판정이 안 되면 억지로 만들지 마라.** 151개는 사람이 읽을 수 있는 수다. "정규식으로 가릴 수 없었고 수동으로 판정했다"고 적는 편이, 좁은 정규식이 일부를 놓치고 그 발화를 "다 잡았다"로 읽는 것보다 낫다. 직전 회차가 20개 문서를 사람이 읽어 성공했다.

151개가 많으면 **층화 표본**을 쓰되(예: Job별 최저·최고 비율에서 각 5개), **표본이라는 사실과 그 한계를 보고서에 명시하라.**

- [ ] **Step 5: 보고서를 쓰고 커밋한다**

`docs/audit-reports/sweeps/2026-08-27-error-code-reach-sweep.md`에 담을 것:

1. Step 1의 문서 수와 실제 경로, `MaxSteps` 필터로 빠진 Job
2. Step 2의 수치 표 (Job별 선언·등장·대입)
3. Step 3의 의무 정의와 **세 갈래 판정 결과**, 각각 좌표와 함께
4. **채점표: 진짜 결손인 (Job, 단계, 코드) 목록**
5. Step 4의 자동/수동/표본 여부와 한계
6. **재지 못한 것** — 이 판정이 놓칠 수 있는 것

**"등장 100% / 대입 90%"의 차이가 왜 중요한지 한 문단을 반드시 넣어라.** 전자로 재면 검사가 아무것도 안 잡는다는 것, 그리고 그 구별이 직전 회차의 결속 검사에서도 판정을 갈랐다는 것.

⚠️ **보고서에 커밋 해시를 적으려면 그 시점 트리가 깨끗해야 한다.** 다른 세션이 더러운 트리에서 낸 보고서에 수정 전 해시가 박힌 채 수정 후 수치가 담긴 사고를 겪었다.

```bash
cd /Users/payletter/git-root/ReSet/.claude/worktrees/control-step-code-type && \
  [ "$(git branch --show-current)" = "stage3-rule-rewrite" ] && \
  git add docs/audit-reports/sweeps/2026-08-27-error-code-reach-sweep.md && \
  git commit -m "docs: 오류 코드 대입 실패 집합을 의무 기준으로 잰다"
```

- [ ] **Step 6: 프로브를 되돌린다**

```bash
rm -f tests/ReSet.Core.Tests/TempReachProbe.cs
git status --short
```

`git status --short`가 비어야 한다.

- [ ] **Step 7: 기준선을 확인한다**

```bash
dotnet build
dotnet test
```

프로덕션 코드를 안 바꿨으므로 수치가 그대로여야 한다. 실패 0·건너뜀 0.

---

### Task 2: 대입 도달성 검사를 더한다

> **[실행 결과 — Task 2는 취소됐다]** Task 1의 정지 조건이 발동해 이 태스크는 **실행되지
> 않았다.** 검사를 만들지 않고 **측정만 남기기로** 결정했다. 근거 셋이다.
>
> **① 설계대로 만들면 오탐률이 96.9%다.** Task 1이 151개를 전수로 갈라 「정규식의 맹점 87 ·
> 정당한 미대입 62 · 진짜 결손 2」를 얻었다. 이 검사는 발화 64건 중 **62건이 오탐**이 된다.
> 원인이 구조적이다 — **`Steps[].ErrorCodes`가 단계별 집합이 아니라 통합 체인 전체의 승인
> 코드 합집합에 가깝다.** `-9`가 증거다: 124개 단계 문서가 선언하고 86개가 싣는데, **그 검사
> 분기가 없는 38개 단계에도 선언돼 있다.** 그래서 Task 2 「Interfaces」가 전제한 "발화 집합이
> 채점표와 정확히 일치"는 성립할 수 없다.
>
> **② 실측 결손률이 `2 / 1,476` = 0.14%다.** 그리고 그 둘 중 하나는 **성공 코드(`0`)**라,
> 검사가 그것을 볼지 자체가 새 설계 판단이다.
>
> **③ 진짜 위험인 「밀림」을 대입 유무로는 못 잡는다.** `POQSettleProc19/S11`이 사례다.
> 원본 `UP_UTIL_SETTLE_COMM_UPD`의 기계 확정 표가 DDL 라인 291→`-9`, 320→`-10`, 345→`-11`,
> 361→`-12`로 매기는데, 그 단계는 **한 칸씩 밀려** 싣는다 — `-10`을 "8. inivacct"에, `-11`을
> "9. easybank"에, `-12`를 "10. KFTC"에, 그리고 **`-12`를 "11. hectofirm"에 또** 싣는다.
> **대입 유무만 보면 떨어져 나간 `-9` 하나만 잡히고 `-10`·`-11`·`-12`는 "대입되어 있으므로"
> 통과한다.** 이관 후 `inivacct` 갱신이 실패하면 운영자는 `-10`을 받고 **`easybank`를
> 들여다본다** — 규칙 6-1이 막으려던 바로 그 사고인데, 이 검사는 그것을 못 본다.
>
> **아래 Task 2 본문(Files·Interfaces·Step 1~9)은 계획 당시의 것으로 남겨 두며, 실행되지
> 않았다.** `MechanicalValidator.cs`에 `CheckErrorCodeAssignmentReach`는 없고
> `tests/ReSet.Core.Tests/ErrorCodeAssignmentReachTests.cs`도 없다. 아래 코드 블록을 그대로
> 베끼지 말 것. 설계 쪽 정본은
> `docs/superpowers/specs/2026-08-27-stage3-rule-rewrite-design.md` §3 머리의 정정 상자다.

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`CheckErrorCodeAssignmentReach` 추가, `ValidateBatchStep` 본문에서 호출)
- Create: `tests/ReSet.Core.Tests/ErrorCodeAssignmentReachTests.cs`

**Interfaces:**
- Consumes: Task 1의 보고서 — 발화 집합이 그 채점표와 정확히 일치해야 한다
- Produces:
  - `private static void CheckErrorCodeAssignmentReach(string stepMarkdown, BatchStepPlan step, StepValidationResult result)`

  형제 검사 `CheckStepIdInitialValue`(`MechanicalValidator.cs:6201`)와 **같은 시그니처**다:
  ```csharp
  private static void CheckStepIdInitialValue(
      string stepMarkdown, BatchStepPlan step, StepValidationResult result)
  {
      if (step.ErrorCodes.Count == 0) return;
      var declaredCodes = step.ErrorCodes
          .Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
      …
      foreach (var (cleaned, _) in CleanedSqlFences(stepMarkdown))
  ```

- [ ] **Step 1: 발화해야 하는 경우의 테스트를 먼저 쓴다**

`tests/ReSet.Core.Tests/ErrorCodeAssignmentReachTests.cs`:

```csharp
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ErrorCodeAssignmentReachTests
    {
        /// <summary>
        /// 선언한 코드가 오류 코드 표에 나열만 되고 어느 대입 자리에도 없으면 발화해야
        /// 한다. 규칙 6-1이 T-SQL 구문으로 지키던 실패 지점 충실도를, 언어 이전 뒤에도
        /// 남는 기록 기준으로 옮긴 것이다.
        /// </summary>
        [Fact]
        public void ValidateBatchStep_ShouldReportWhenADeclaredCodeIsOnlyListedInATable()
        {
            const string markdown = @"## S01 정산 원장 적재

| 오류 코드 | 뜻 |
| :--- | :--- |
| -101 | 원장 없음 |
| -102 | 마감 이후 |

```sql
SET @v_currentStepId = -101;
DELETE FROM dbo.TSettleLedger WHERE YMD = @pi_ymd;
```
";
            var result = ValidateStep(markdown, new[] { "-101", "-102" });

            Assert.Contains(result.Errors, e => e.Contains("-102"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("-101"));
        }
    }
}
```

`ValidateStep` 헬퍼는 이 파일 안에 둔다. **정확한 조립 방법은 `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`에서 `ValidateBatchStep`을 부르며 `BatchStepPlan.ErrorCodes`를 채우는 기존 테스트를 찾아 그 방식을 그대로 따른다.** 새 방식을 발명하지 마라.

- [ ] **Step 2: 실패를 확인한다 (RED)**

```bash
dotnet test --filter "FullyQualifiedName~ErrorCodeAssignmentReachTests"
```

기대: 컴파일 실패(`CheckErrorCodeAssignmentReach`가 없다) 또는 단언 실패.

**통과하면 멈추고 보고하라** — 이미 다른 검사가 이걸 잡고 있다는 뜻이고, 그러면 이 태스크의 전제가 틀렸다.

- [ ] **Step 3: 침묵해야 하는 경우의 테스트를 더한다**

발화 조건만 잠그면 "항상 발화"가 통과한다. 반대 방향도 고정한다. **Task 1이 「정규식의 맹점」으로 분류한 형태를 여기에 그대로 넣어라** — 그 형태들이 침묵해야 한다.

```csharp
        /// <summary>
        /// 값이 함수나 CASE를 거쳐 실려도 대입이다. 직전 회차가 `Legacy*` 이름으로
        /// 운반체를 찾다가 세 라운드 연속 틀렸다 - 형태로 좁히면 같은 실수를 반복한다.
        /// </summary>
        [Fact]
        public void ValidateBatchStep_ShouldStaySilentWhenTheCodeIsAssignedThroughAnExpression()
        {
            const string markdown = @"## S01 정산 원장 적재

```sql
SET @v_currentStepId = CASE WHEN @v_rows = 0 THEN -102 ELSE -101 END;
```
";
            var result = ValidateStep(markdown, new[] { "-101", "-102" });

            Assert.DoesNotContain(result.Errors, e => e.Contains("-101"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("-102"));
        }
```

**Task 1이 실제로 찾은 맹점 형태가 위와 다르면 그것을 쓰라.** 위 픽스처는 컨트롤러의 예상이지 실측이 아니다.

- [ ] **Step 4: 검사를 구현한다**

`src/ReSet.Core/Services/MechanicalValidator.cs`에 더한다. 형제 검사 `CheckStepIdInitialValue`(`:6201`)의 관례를 따른다 — `step.ErrorCodes` 다듬기, `CleanedSqlFences`로 펜스·주석 처리, 오류 문구 형식, 자기 `try/catch` 여부.

```csharp
        /// <summary>
        /// 선언한 레거시 오류 코드가 실제 대입 자리에 나타나는지 본다.
        ///
        /// [왜 "등장"이 아니라 "대입"인가] 컨트롤러 실측에서 선언 코드 1,476개가 문서에는
        /// 100% 등장하지만 대입 자리에는 90%만 나타났다 - 151개가 오류 코드 표에 나열만
        /// 된다. "등장"으로 재면 이 검사는 아무것도 잡지 못한다.
        ///
        /// [왜 이 검사가 필요한가] 규칙 6-1이 `DECLARE @v_currentStepId INT = 0`이라는
        /// T-SQL 구문으로 실패 지점 충실도를 지켰다. 언어 이전 뒤에 그 구문이 사라지면
        /// 이 검사가 그 자리를 대신한다 - 값이 기록되는가는 언어와 무관하다.
        /// </summary>
        private static void CheckErrorCodeAssignmentReach(
            string stepMarkdown, BatchStepPlan step, StepValidationResult result)
        {
            if (step.ErrorCodes.Count == 0) return;

            var declared = step.ErrorCodes
                .Select(c => c.Trim()).Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal).ToList();
            if (declared.Count == 0) return;

            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (cleaned, _) in CleanedSqlFences(stepMarkdown))
            {
                // 대입 자리를 모은다. Task 1이 확정한 형태를 여기에 반영할 것 -
                // 아래는 컨트롤러의 근사이고 실측이 아니다.
                foreach (Match m in Regex.Matches(cleaned, @"=\s*(-?\d+)\s*[;,)]"))
                    assigned.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(
                             cleaned, @"SET\s+@\w+\s*=\s*(-?\d+)", RegexOptions.IgnoreCase))
                    assigned.Add(m.Groups[1].Value);
            }

            var missing = declared.Where(c => !assigned.Contains(c)).ToList();
            if (missing.Count == 0) return;

            result.Errors.Add(
                $"{step.Code} 단계가 선언한 오류 코드 {string.Join(", ", missing)}가(이) "
                + $"어느 대입 자리에도 나타나지 않습니다. 표에 나열만 하면 그 코드는 "
                + $"실행 중 기록되지 않아 실패 지점을 식별할 수 없습니다.");
        }
```

**위 대입 자리 정규식은 출발점이지 완성이 아니다.** Task 1이 「정규식의 맹점」으로 분류한 형태를 전부 인정하도록 넓혀라. **넓힐 때마다 그것이 새 판정**이므로 Step 7의 변이 표에 한 줄씩 더한다.

⚠️ **`result.Errors.Add`를 반드시 쓸 것.** `DetailedErrors`만 채우면 `IsValid`가 참으로 남아 오케스트레이터 게이트가 안 걸린다 — 직전 회차에 그 결함이 테스트 15개가 전부 초록인 채로 있었다. `DetailedErrors`도 함께 채운다면 형제 검사의 관례를 따르되, **`Errors`를 빠뜨리지 마라.**

- [ ] **Step 5: `ValidateBatchStep`에서 부른다**

`ValidateBatchStep` 본문의 다른 `Check…` 호출 옆에 더한다. 인접 호출의 인자 전달 방식을 그대로 따른다.

⚠️ `CheckAnchoredStatementFacts`·`CheckAnchoredStatementExtras`를 건드리지 마라 — 다른 세션이 동시에 만진다.

- [ ] **Step 6: 통과를 확인하고 커밋한다 (GREEN)**

```bash
dotnet test --filter "FullyQualifiedName~ErrorCodeAssignmentReachTests"
```

기대: 전부 통과.

```bash
cd /Users/payletter/git-root/ReSet/.claude/worktrees/control-step-code-type && \
  [ "$(git branch --show-current)" = "stage3-rule-rewrite" ] && \
  git add src/ReSet.Core/Services/MechanicalValidator.cs \
          tests/ReSet.Core.Tests/ErrorCodeAssignmentReachTests.cs && \
  git commit -m "feat: 선언한 오류 코드가 대입 자리에 나타나는지 검사한다"
```

**커밋을 변이보다 먼저 한다.** 커밋 전 `git checkout --`은 작업을 통째로 날린다 — 이 저장소에서 실제로 겪었고, 100턴 한계에 걸린 워커를 그 순서가 살렸다.

- [ ] **Step 7: 변이로 잠금을 확인한다 — 조건마다가 아니라 판정마다**

각 변이를 넣고 지정 테스트가 **죽는지**, 그리고 **딱 하나만 죽는지** 본 뒤 `git checkout -- src/ReSet.Core/Services/MechanicalValidator.cs`로 되돌린다.

| 변이 | 죽어야 하는 것 |
|---|---|
| `result.Errors.Add`를 지우고 `DetailedErrors`만 남긴다 | `IsValid`를 단언하는 테스트 |
| `missing.Count == 0` 조기 반환을 `true`로 바꾼다 | 발화 테스트 |
| `CleanedSqlFences` 대신 원문 전체를 훑는다 | 표에만 나열된 코드가 대입으로 오인되는 것을 잡는 테스트 |
| Task 1이 넓히게 한 형태마다 그 인정을 되돌린다 | 그 형태의 침묵 테스트 (형태 하나당 한 줄) |

**"발화 > 0"이나 "코퍼스 일치"로는 부족하다.** 직전 회차에서 **코퍼스 발화 집합이 정확히 일치하는데도 판정이 무방비인 경우가 네 번** 나왔다. 그중 하나는 `result.Errors.Add` 한 줄이 빠져도 테스트 15개가 전부 초록이고 코퍼스도 일치했는데 **검사가 생산에서 완전히 무력**했다.

**죽지 않는 변이가 있으면 픽스처가 우연히 성립하는지 의심하라** — 직전 회차에 두 판정이 한 픽스처에 얹혀 있어 두 변이가 같은 쌍을 죽인 일이 있었다. 그때는 **하나가 무방비여도 잠긴 것처럼 보였다.**

- [ ] **Step 8: 코퍼스에서 발화 집합을 확인한다 — 이 태스크의 정지 조건**

심링크 둘을 걸고 전체를 돌린다.

```bash
dotnet build
dotnet test
```

경고 0·오류 0, 실패 0, **건너뜀 0**.

그다음 **코퍼스 전수에서 이 검사가 발화하는 (Job, 단계, 코드)**를 뽑아 **Task 1 보고서의 채점표와 대조하라.**

> **정지 조건: 발화 집합이 Task 1의 채점표와 정확히 일치해야 한다.**

일치하지 않으면 **멈추고 보고하라.** 차이 나는 항목을 이름으로 적고, 검사가 좁은 것인지 Task 1의 판정이 틀린 것인지 밝혀라. 고치려 들지 말고 보고하라 — 어느 쪽이든 이 태스크는 끝나지 않은 것이다.

- [ ] **Step 9: 결과를 보고서에 덧붙이고 커밋한다**

Step 8의 대조 결과를 Task 1의 보고서에 한 절로 더한다 — 발화 집합, 채점표와의 일치 여부, 불일치가 있었다면 그 해소 과정, 그리고 **이 검사가 강제하지 못하는 것**(알려진 한계).

```bash
cd /Users/payletter/git-root/ReSet/.claude/worktrees/control-step-code-type && \
  [ "$(git branch --show-current)" = "stage3-rule-rewrite" ] && \
  git add docs/audit-reports/sweeps/2026-08-27-error-code-reach-sweep.md && \
  git commit -m "docs: 대입 도달성 검사의 발화 집합을 실측 채점표와 대조한 결과를 싣는다"
```

---

## 이 계획서가 다루지 않는 것

- **3단계 — 규칙 본문 다시 쓰기.** 설계서 §2의 규칙 넷(4·6-1·8-1·4-1)과 규칙 2의 한 줄은 범위 밖이다. `ConsolidatedPlanRules`가 공유 프롬프트 접두사라 고치면 캐시가 무효화되고 **전건 재생성 비용이 확정**된다 — 별도 승인 자리를 둔다.
- **4단계 — 전건 재생성.** 다른 세션의 통지, 스냅샷, 한 Job 시험이 선행되어야 한다(설계서 §6).
- **`CheckCatchDiscardsReturnCode`의 재조준.** 선행 설계서가 죽는 검사 셋을 지목했고 이 계획은 `CheckStepIdInitialValue`에 해당하는 것만 옮긴다. 나머지 하나는 4단계에서 실제로 침묵하는지 확인한 뒤 판단한다.
- **`CheckStepIdInitialValue` 자체의 제거.** 새 검사가 그 자리를 대신하지만, 지금은 계획서가 여전히 T-SQL이라 옛 검사도 살아 있다. **둘을 함께 두고**, 3단계 이후 옛 검사가 침묵하는지 보고 그때 지운다.
