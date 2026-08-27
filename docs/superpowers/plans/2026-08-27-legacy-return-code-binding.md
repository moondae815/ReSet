# 레거시 반환 코드 결속 검사 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 배치 단계가 보존한 레거시 반환 코드를 `batch.BatchStepJournal.LegacyReturnCode`에 결속하도록 L1이 강제한다 — 그래야 뒤이을 언어 이전(SQL → C#)이 오류 코드 체계를 잃지 않는다.

**Architecture:** 먼저 코퍼스에서 **의무 기준**(이름 기준이 아니라)으로 결속 실패 집합을 확정하고, 그 집합을 채점표로 삼아 검사 하나를 더한다. 규칙과 계획서는 건드리지 않는다 — 그래야 발화가 "검사가 일한 것"인지 "계획서가 달라진 것"인지 구별된다.

**Tech Stack:** .NET 10, xUnit

**Spec:** `docs/superpowers/specs/2026-08-27-sql-placement-criterion-design.md`

## Global Constraints

- **이름을 역할의 대리로 쓰지 않는다.** 이 계획의 존재 이유가 그 실패다 — 설계서 §5-1이 `LegacyReturnCode` 문자열로 기대 집합을 뽑았고 **틀렸다**. 20개 계획서 전부가 `@po_intRetVal`을 보존하는데 운반체 이름이 셋으로 갈린다(`LegacyReturnCode` 14 · `LegacyRetVal` 6 · 둘 다 없음 3). 판정은 **의무 이행 여부**로 하고, 이름은 증거이지 기준이 아니다.
- **코퍼스 검증은 심링크 둘을 걸고 건너뜀 0에서 한다.** 건너뜀 15면 아예 안 걸린 것, 2면 둘째 링크가 안 걸린 것이다.
  ```bash
  ln -s /Users/payletter/git-root/ReSet/output output
  ln -s /Users/payletter/git-root/ReSet/output.bak-2026-08-22 output.bak-2026-08-22
  ```
- **절대 테스트 통과 수를 게이트로 쓰지 않는다.** 환경마다 최대 5까지 어긋나는 것이 관측됐고 원인은 미상이다. 같은 환경에서 전후를 각각 돌려 **차분**을 본다.
- **`git stash` 금지.** 스택이 메인 체크아웃·다른 워크트리와 공유되고 다른 세션이 동시에 작업한다.
- **`cd` 뒤에 브랜치 단언을 같은 명령에 묶는다.** Bash 호출 사이에 작업 디렉터리가 유지되어, 이 설계서를 쓰는 중에 공유 체크아웃의 브랜치를 옮기는 사고가 났다.
  ```bash
  cd <경로> && [ "$(git branch --show-current)" = "<내 브랜치>" ] && git <변경 명령>
  ```
- **프롬프트(`AiService.cs`)와 `ConsolidatedPlanRules`를 건드리지 않는다.** 규칙 변경은 3단계이고 이 계획의 범위 밖이다. 만지면 접두사 캐시가 무효화되어 전건 재생성이 필요해진다.
- 기준선: `dotnet build` 경고 0·오류 0, `dotnet test` 실패 0·건너뜀 0.

---

## File Structure

| 파일 | 책임 | 태스크 |
|---|---|---|
| `docs/audit-reports/sweeps/2026-08-27-legacy-return-code-sweep.md` | 의무 기준 실측 보고서 — 결속 실패 집합의 정본 | 1 |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | `CheckLegacyReturnCodeBinding` 추가, `ValidateBatchStep`에서 호출 | 2 |
| `tests/ReSet.Core.Tests/LegacyReturnCodeBindingTests.cs` | 새 파일 — 검사 동작과 변이 잠금 | 2 |

`MechanicalValidator.cs`는 이미 매우 크지만 이 계획에서 쪼개지 않는다 — 배치 단계 검사 12개가 같은 진입점(`ValidateBatchStep`)을 공유하는 구조라, 분리하면 이 작업의 범위를 훨씬 넘는다.

---

### Task 1: 의무 기준으로 결속 실패 집합을 확정한다

**Files:**
- Create (임시, 끝나면 삭제): `tests/ReSet.Core.Tests/TempBindingProbe.cs`
- Create: `docs/audit-reports/sweeps/2026-08-27-legacy-return-code-sweep.md`

**Interfaces:**
- Consumes: 없음
- Produces: 보고서. **Task 2의 채점표다** — 그 검사의 발화 집합이 이 목록과 정확히 일치해야 한다.

**배경:** 설계서 §5-1이 `LegacyReturnCode` 문자열로 기대 집합을 뽑아 여섯 개(`POQSettleProc1`·`4`·`7`·`10`·`11`·`13`)를 지목했다. **그 여섯은 틀렸다** — 확인해 보니 전부 레거시 반환 코드를 다른 이름으로 나르고 있었다. 이 태스크가 올바른 집합을 만든다.

- [ ] **Step 1: 코퍼스 경로를 먼저 확인한다**

계획서가 지정한 경로에 문서가 실제로 있는지 **먼저 세어라.** 직전 회차에 브리프가 지정한 글롭에 문서가 0건이었고, 그대로 돌렸다면 아무것도 안 읽고 "영향 0"이 나왔을 뻔했다.

```bash
ls /Users/payletter/git-root/ReSet/output/Jobs/*/docs/BatchMigrationPlan.md | wc -l
```

기대: **20**. 다르면 멈추고 보고하라.

- [ ] **Step 2: 이름 퍼짐을 표로 재현한다**

이 수치가 이 태스크의 출발점이므로 직접 재현해 보고서에 싣는다.

```bash
cd /Users/payletter/git-root/ReSet
for f in output/Jobs/*/docs/BatchMigrationPlan.md; do
  j=$(basename $(dirname $(dirname $f)))
  a=$(grep -c "LegacyReturnCode" "$f")
  b=$(grep -c "LegacyRetVal" "$f")
  c=$(grep -c "po_intRetVal" "$f")
  printf "%-18s %-4s %-4s %s\n" "$j" "$a" "$b" "$c"
done
```

기대(2026-08-27 컨트롤러 실측): `po_intRetVal`은 **20개 전부**에서 16~156회. `LegacyReturnCode`는 14개, `LegacyRetVal`은 6개(`Proc1`·`Proc2`·`Proc7`·`Proc8`·`Proc10`·`Proc14`), 둘 다 없는 것 3개(`Proc4`·`Proc11`·`Proc13`). `Proc2`·`Proc8`·`Proc14`는 **두 이름을 함께** 쓴다.

수치가 다르면 그 사실을 보고서에 적고 **당신이 잰 값을 쓴다** — 위 수치가 틀렸을 수 있다.

- [ ] **Step 3: 의무를 정의하고 그 기준으로 판정한다**

**판정 대상은 이름이 아니라 결속이다.** 한 계획서가 의무를 이행했다고 보려면 다음 둘이 모두 참이어야 한다.

1. 레거시 반환 코드를 보존한다 (`@po_intRetVal` 또는 그에 상당하는 원본 출력 파라미터를 서술한다)
2. 그 값이 **`batch.BatchStepJournal`의 `LegacyReturnCode` 컬럼에 도달한다** — 그 테이블에 쓰는 `INSERT` 또는 `UPDATE` 문장이 이 컬럼을 대상으로 삼는다

**2번이 핵심이다.** C# 필드 이름이 `LegacyReturnCode`든 `LegacyRetVal`든 상관없다 — 컬럼에 닿는지만 본다. 반대로 컬럼 이름을 스키마 표에 옮겨 적기만 하고 쓰는 문장이 없으면 **이행이 아니다.**

각 계획서를 열어 이 둘을 판정하고, 판정 근거(어느 줄이 그 문장인지)를 좌표로 적는다.

- [ ] **Step 4: 자동 판정이 가능한지 시험하고, 안 되면 그렇게 적는다**

Step 3의 판정을 정규식으로 자동화할 수 있으면 프로브를 만들어 전수로 돌린다. `tests/ReSet.Core.Tests/TempBindingProbe.cs`에 두고, 끝나면 Step 6에서 지운다.

**자동 판정이 안 되면 억지로 만들지 마라.** 20개는 사람이 읽을 수 있는 수다. "정규식으로 가릴 수 없었고 수동으로 판정했다"고 보고서에 적는 편이, 좁은 정규식이 일부를 놓치고 그 발화를 "다 잡았다"로 읽는 것보다 낫다.

- [ ] **Step 5: 보고서를 쓰고 커밋한다**

`docs/audit-reports/sweeps/2026-08-27-legacy-return-code-sweep.md`에 담을 것:

1. Step 1의 문서 수와 실제 경로
2. Step 2의 이름 퍼짐 표 (20행)
3. Step 3의 의무 정의 — 위 두 조건을 그대로
4. **판정 결과: 이행한 Job 목록과 실패한 Job 목록**, 각각 좌표와 함께
5. Step 4의 자동/수동 여부
6. **재지 못한 것** — 이 판정이 놓칠 수 있는 것. 예: 단계 문서(`agent/steps/*.md`)는 안 봤고 통합 계획서만 봤다면 그렇게 적는다

**설계서 §5-1의 여섯이 왜 틀렸는지 한 문단을 반드시 넣어라.** 이름으로 재고 의무라고 부른 것이 이 프로젝트가 반복해 앓는 병이며, 다음 사람이 같은 지름길을 타지 않게 하는 것이 이 문단의 목적이다.

```bash
cd /Users/payletter/git-root/ReSet/.claude/worktrees/control-step-code-type && \
  [ "$(git branch --show-current)" = "sql-placement-criterion" ] && \
  git add docs/audit-reports/sweeps/2026-08-27-legacy-return-code-sweep.md && \
  git commit -m "docs: 레거시 반환 코드의 결속 실패 집합을 의무 기준으로 잰다"
```

- [ ] **Step 6: 프로브를 되돌린다**

```bash
rm -f tests/ReSet.Core.Tests/TempBindingProbe.cs
git status --short
```

`git status --short`가 비어야 한다. 비지 않으면 무엇이 남았는지 보고하라.

- [ ] **Step 7: 기준선을 확인한다**

```bash
dotnet build
dotnet test
```

프로덕션 코드를 안 바꿨으므로 수치가 그대로여야 한다. 실패 0·건너뜀 0.

---

### Task 2: 결속 검사를 더한다

**Files:**
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`CheckLegacyReturnCodeBinding` 추가, `ValidateBatchStep` 본문 279~420행 구간에서 호출)
- Create: `tests/ReSet.Core.Tests/LegacyReturnCodeBindingTests.cs`

**Interfaces:**
- Consumes: Task 1의 보고서 — 발화 집합이 그 실패 목록과 일치해야 한다
- Produces:
  - `private static void CheckLegacyReturnCodeBinding(string markdown, BatchStepPlan step, StepValidationResult result)`

  `ValidateBatchStep`의 다른 검사들과 같은 관례를 따른다. 그 메서드의 시그니처는 이렇다(참고용, 바꾸지 마라):
  ```csharp
  public StepValidationResult ValidateBatchStep(
      string? stepMarkdown, BatchStepPlan step,
      IReadOnlyCollection<string> knownTableNames,
      IReadOnlyDictionary<string, SpecConditions> conditionColumnsByProcedure,
      IReadOnlyList<StepInterface>? stepInterfaces = null, …)
  ```

- [ ] **Step 1: 발화해야 하는 경우의 테스트를 먼저 쓴다**

`tests/ReSet.Core.Tests/LegacyReturnCodeBindingTests.cs`:

```csharp
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class LegacyReturnCodeBindingTests
    {
        /// <summary>
        /// 레거시 반환 코드를 보존한다고 서술하면서 그 값을 저널 컬럼에 결속하지 않으면
        /// 발화해야 한다. 이 결속이 없으면 뒤이을 언어 이전에서 코드가 갈 데를 잃는다.
        /// </summary>
        [Fact]
        public void ValidateBatchStep_ShouldReportWhenLegacyCodeIsPreservedButNeverBound()
        {
            const string markdown = @"## S01 정산 원장 적재

원본 출력 `@po_intRetVal`을 그대로 보존한다.

```sql
INSERT INTO batch.BatchStepJournal (RunId, StepCode, StepStatus, StartedAtUtc)
VALUES (@v_runId, N'S01', N'Running', SYSUTCDATETIME());
```
";
            var result = ValidateStep(markdown);

            Assert.Contains(result.Errors, e => e.Contains("LegacyReturnCode"));
        }
    }
}
```

`ValidateStep` 헬퍼는 이 파일 안에 둔다. **정확한 조립 방법은 `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs`에서 `ValidateBatchStep`을 부르는 기존 테스트를 찾아 그 방식을 그대로 따른다** — `BatchStepPlan`과 필수 인자 구성이 거기 있다. 새 방식을 발명하지 마라.

- [ ] **Step 2: 실패를 확인한다 (RED)**

```bash
dotnet test --filter "FullyQualifiedName~LegacyReturnCodeBindingTests"
```

기대: 컴파일 실패(`CheckLegacyReturnCodeBinding`이 없다) 또는 단언 실패.

**통과하면 멈추고 보고하라** — 이미 다른 검사가 이걸 잡고 있다는 뜻이고, 그러면 이 태스크의 전제가 틀렸다.

- [ ] **Step 3: 침묵해야 하는 경우의 테스트를 더한다**

발화 조건만 잠그면 "항상 발화"가 통과한다. 반대 방향도 고정한다.

```csharp
        /// <summary>
        /// C# 필드 이름이 무엇이든 - LegacyRetVal이든 무엇이든 - 값이 저널 컬럼에
        /// 닿으면 이행이다. 판정은 이름이 아니라 결속으로 한다.
        /// </summary>
        [Fact]
        public void ValidateBatchStep_ShouldStaySilentWhenTheValueReachesTheColumnUnderAnyFieldName()
        {
            const string markdown = @"## S01 정산 원장 적재

원본 출력 `@po_intRetVal`을 `LegacyRetVal`로 보존한다.

```sql
UPDATE batch.BatchStepJournal
   SET StepStatus = N'Failed', LegacyReturnCode = @v_legacyRetVal
 WHERE RunId = @v_runId AND StepCode = N'S01';
```
";
            var result = ValidateStep(markdown);

            Assert.DoesNotContain(result.Errors, e => e.Contains("LegacyReturnCode"));
        }
```

- [ ] **Step 4: 검사를 구현한다**

`src/ReSet.Core/Services/MechanicalValidator.cs`에 더한다. 두 조건이 **모두** 참일 때만 발화한다.

1. 문서가 레거시 반환 코드를 보존한다고 서술한다 (`@po_intRetVal` 또는 그에 상당하는 원본 출력 파라미터를 언급)
2. `batch.BatchStepJournal`에 쓰는 문장 어디에도 `LegacyReturnCode` 컬럼이 대상으로 나오지 않는다

```csharp
        /// <summary>
        /// 레거시 반환 코드가 저널 컬럼에 결속되는지 본다.
        ///
        /// [왜 이름이 아니라 결속인가] 코퍼스 20개 전부가 `@po_intRetVal`을 보존하지만
        /// 운반체 이름은 셋으로 갈린다(LegacyReturnCode 14 · LegacyRetVal 6 · 둘 다
        /// 없음 3). 이름으로 재면 같은 의무를 이행한 계획서가 실패로 잡힌다 - 설계서
        /// 초안이 정확히 그 실수를 했다. 판정 기준은 값이 컬럼에 닿는가 하나다.
        ///
        /// [왜 지금 강제하는가] 뒤이을 언어 이전에서 트랜잭션과 오류 처리가 C#으로
        /// 옮겨 가면 T-SQL 반환값이라는 거처가 사라진다. 그 전에 값을 언어 밖
        /// (저널 컬럼)에 못박아야 코드 체계가 이전을 견딘다.
        /// </summary>
        private static void CheckLegacyReturnCodeBinding(
            string markdown, BatchStepPlan step, StepValidationResult result)
        {
            // 조건 1: 레거시 반환 코드를 보존한다고 서술하는가.
            // 원문(펜스 밖 산문 포함)을 본다 - 인터페이스 표에만 적고 SQL에는
            // 안 쓰는 계획서가 실재하므로 펜스만 보면 놓친다.
            if (!Regex.IsMatch(markdown, @"@po_\w*RetVal", RegexOptions.IgnoreCase)) return;

            // 조건 2: 저널에 쓰는 문장이 이 컬럼을 대상으로 삼는가.
            // 펜스 안만 본다 - 산문의 언급은 결속이 아니다. CleanedSqlFences가
            // 주석과 문자열을 지운 본문을 준다(인접 검사와 같은 관례).
            var bound = false;
            foreach (var (cleaned, _) in CleanedSqlFences(markdown))
            {
                if (!Regex.IsMatch(
                        cleaned, @"BatchStepJournal", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(
                        cleaned, @"LegacyReturnCode", RegexOptions.IgnoreCase))
                {
                    bound = true;
                    break;
                }
            }

            if (bound) return;

            result.Errors.Add(
                $"{step.Code} 단계가 레거시 반환 코드를 보존하면서 "
                + $"batch.BatchStepJournal의 LegacyReturnCode 컬럼에 결속하지 않습니다. "
                + $"그 값이 저널에 남지 않으면 오류 코드가 이 단계 밖에서 확인되지 않습니다.");
        }
```

**위 본문은 출발점이지 완성이 아니다.** 두 가지를 실물로 확인하고 고쳐라.

1. **조건 2가 너무 넓은가** — `BatchStepJournal`과 `LegacyReturnCode`가 같은 펜스에 있기만 하면 참이다. `SELECT`로 읽기만 하는 펜스도 통과한다. Task 1에서 실물을 봤을 때 그런 형태가 있었으면 `INSERT`/`UPDATE` 대상 절로 좁혀라.
2. **자기 `try/catch`가 필요한가** — 이 파일의 일부 검사는 자기 try/catch를 둔다(`Validate`의 catch-all이 검사 하나의 예외로 전체 판정을 삼키기 때문). 인접 검사가 그러는지 보고 관례를 따르라.

`CleanedSqlFences`·`step.ErrorCodes`·오류 문구 형식은 `CheckStepIdInitialValue`(`:6193`)의 관례를 그대로 따른 것이다.

- [ ] **Step 5: `ValidateBatchStep`에서 부른다**

279~420행 구간의 다른 `Check…` 호출 옆에 더한다. 인접 호출의 인자 전달 방식을 그대로 따른다.

- [ ] **Step 6: 통과를 확인한다 (GREEN)**

```bash
dotnet test --filter "FullyQualifiedName~LegacyReturnCodeBindingTests"
```

기대: 두 테스트 모두 통과.

- [ ] **Step 7: 커밋한다 — 변이 전에**

```bash
cd /Users/payletter/git-root/ReSet/.claude/worktrees/control-step-code-type && \
  [ "$(git branch --show-current)" = "sql-placement-criterion" ] && \
  git add src/ReSet.Core/Services/MechanicalValidator.cs \
          tests/ReSet.Core.Tests/LegacyReturnCodeBindingTests.cs && \
  git commit -m "feat: 레거시 반환 코드가 저널 컬럼에 결속되는지 검사한다"
```

**커밋을 변이보다 먼저 한다.** 변이를 되돌리는 `git checkout --`이 커밋 전이면 작업이 통째로 날아간다 — 직전 회차에 실제로 겪었다.

- [ ] **Step 8: 변이로 잠금을 확인한다**

각 변이를 넣고 지정 테스트가 **죽는지** 본 뒤 `git checkout -- src/ReSet.Core/Services/MechanicalValidator.cs`로 되돌린다.

| 변이 | 죽어야 하는 것 |
|---|---|
| 조건 2를 지우고 조건 1만으로 발화하게 한다 | `ShouldStaySilentWhenTheValueReachesTheColumnUnderAnyFieldName` |
| 조건 1을 지우고 조건 2만으로 발화하게 한다 | (새 테스트가 필요하다 — 아래) |
| 컬럼 매칭을 `LegacyReturnCode` 대신 `LegacyRetVal`로 바꾼다 | `ShouldStaySilentWhenTheValueReachesTheColumnUnderAnyFieldName` |

두 번째 변이를 잡는 테스트가 없으면 **하나 더 써라**: 레거시 반환 코드를 보존하지 않는 제어 단계(레거시 출신이 아닌 단계)가 컬럼을 안 써도 침묵해야 한다.

**죽지 않는 변이가 있으면 그 테스트는 변별하지 않는 것이다.** 픽스처가 우연히 성립하는지 의심하라 — 직전 회차에 미끼 블록이 한 줄이라 변이가 안 죽은 일이 있었다.

- [ ] **Step 9: 코퍼스에서 발화 집합을 확인한다 — 이 태스크의 정지 조건**

심링크 둘을 걸고 전체를 돌린다.

```bash
dotnet build
dotnet test
```

경고 0·오류 0, 실패 0, **건너뜀 0**.

그다음 **코퍼스 전수에서 이 검사가 어느 Job에서 발화하는지** 확인하고, **Task 1 보고서의 실패 목록과 대조하라.**

> **정지 조건: 발화 집합이 Task 1의 목록과 정확히 일치해야 한다.**
>
> **"발화 > 0"으로는 부족하다.** 검사가 실물보다 좁으면 발화는 하되 일부만 잡고, 그 발화를 "살아 있다"로 읽고 넘어가게 된다. 같은 날 다른 세션이 겪은 실패다 — 진단서가 한 구문 형태만 서술했는데 실물 셋 중 둘은 다른 형태였고, **틀린 서술이 아니라 좁은 서술이라 읽어서는 드러나지 않았다.**

일치하지 않으면 **멈추고 보고하라.** 차이가 나는 Job을 이름으로 적고, 검사가 좁은 것인지 Task 1의 판정이 틀린 것인지 밝혀라. 어느 쪽이든 이 태스크는 끝나지 않은 것이다.

- [ ] **Step 10: 결과를 보고서에 덧붙이고 커밋한다**

Step 9의 대조 결과를 Task 1의 보고서에 한 절로 더한다 — 발화 집합, 목록과의 일치 여부, 불일치가 있었다면 그 해소 과정.

```bash
cd /Users/payletter/git-root/ReSet/.claude/worktrees/control-step-code-type && \
  [ "$(git branch --show-current)" = "sql-placement-criterion" ] && \
  git add docs/audit-reports/sweeps/2026-08-27-legacy-return-code-sweep.md && \
  git commit -m "docs: 결속 검사의 발화 집합을 실측 목록과 대조한 결과를 싣는다"
```

---

## 이 계획서가 다루지 않는 것

- **3단계 — 규칙 변경과 전건 재생성.** 설계서 §3의 프롬프트 규칙 변경(새 규칙 하나, 다시 쓰는 다섯, 표적 바꾸는 둘, 4-1에서 `helper procedure` 제거)과 그에 따른 재생성은 범위 밖이다. `ConsolidatedPlanRules`는 공유 접두사라 고치면 캐시가 전부 무효화되고 전건 재생성이 필요하다 — 별도 결정과 비용 승인이 선행되어야 한다.
- **설계서 §4-2의 「기존 검사 셋 재조준」.** 초안은 `CheckCatchDiscardsReturnCode`·`CheckStepIdInitialValue`·`CheckControlStepErrorCodeBand`를 2단계에서 기록 기준으로 옮기려 했다. **그 셋은 3단계 몫이다** — 오늘의 계획서는 여전히 T-SQL이라 세 검사의 근거가 아직 사라지지 않았고, 지금 옮기면 옮긴 검사가 무엇을 잡는지 확인할 대상이 없다. 2단계의 정합한 산출물은 **결속 검사 하나**이며, 그것만이 독립 유도한 기대 집합으로 채점된다.
- **`agent/steps/*.md` 단계 문서.** 이 계획은 통합 계획서(`docs/BatchMigrationPlan.md`)를 대상으로 한다. 단계 문서까지 볼지는 Task 1의 「재지 못한 것」에 기록하고 후속에서 판단한다.
