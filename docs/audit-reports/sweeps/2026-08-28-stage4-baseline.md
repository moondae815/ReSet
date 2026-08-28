# 4단계 재생성 전 기준선 (2026-08-28)

3단계(규칙 본문 다시 쓰기, `aec9ea1`)가 끝난 **직후·재생성 이전**의 코퍼스를 잰 기록이다.
4단계는 재생성 후 이 문서와 대조한다. **재생성하면 이 수치는 영영 다시 잴 수 없다** —
그래서 재생성 전에 뜬다.

설계서: `docs/superpowers/specs/2026-08-27-stage3-rule-rewrite-design.md` §5(4단계의 성공 기준)

## 0. 측정 조건

- 코퍼스: `output/Jobs` 아래 Job 디렉터리 22개, 그중 `docs/BatchMigrationPlan.md`를 가진 **20편**.
  `POQSettleProc5`·`POQSettleProc20`은 `raw/`만 있어 대상 밖이다(직전 두 스윕과 같은 판정).
- 산출물 생성 시기는 균질하지 않다 — 08-12(3) · 08-13(5) · 08-14(1) · 08-15(3) · 08-16(3) ·
  08-19(4) · 08-24(1). **한 세대가 아니다.**
- 측정은 코퍼스 심링크 둘(`output`, `output.bak-2026-08-22`)을 건 워크트리에서 임시 프로브로
  했다. 프로브는 이 커밋에 들어 있지 않다(§5에 원문을 싣는다).
- 프로덕션 코드는 바꾸지 않았다. 이 커밋에는 이 보고서만 들어 있다.

## 1. 축 1 — 신규 저장 프로시저 정의 수 (§5 표의 첫 행)

**측정 방법**: `output/Jobs/*/docs/BatchMigrationPlan.md` 본문에서
`CREATE\s+(OR\s+ALTER\s+)?PROCEDURE\s+([\w\.\[\]]+)` 매칭 수. 선행 설계서 §1이 쓴 것과 같은
기준이다. 「원본 인용」 열은 그 매칭의 프로시저 이름이 `output/Procedures`의 원본 SP 14개
가운데 하나인 것 — 새 규칙 3-1이 유일하게 허용하는 갈래다.

| Job | 정의 수 | 원본 인용 | 신규 | 결속 | `BatchMigrationPlan.md` sha256 |
|---|---:|---:|---:|:--|:--|
| `POQSettleBatch1` | 9 | 0 | **9** | 이행 | `A2831EA68B24` |
| `POQSettlePrco20` | 11 | 1 | **10** | 이행 | `163B0741D697` |
| `POQSettleProc1` | 1 | 0 | **1** | 실패 | `24DBFC16F72B` |
| `POQSettleProc10` | 11 | 0 | **11** | 실패 | `DE5C8AB2714B` |
| `POQSettleProc11` | 2 | 0 | **2** | 실패 | `9B66BDD4A957` |
| `POQSettleProc12` | 4 | 0 | **4** | 실패 | `3AFF9DEAA7B5` |
| `POQSettleProc13` | 2 | 0 | **2** | 실패 | `77A5D045D2B2` |
| `POQSettleProc14` | 1 | 0 | **1** | 실패 | `F1AEB62F5BEC` |
| `POQSettleProc15` | 6 | 0 | **6** | 실패 | `F557EE630191` |
| `POQSettleProc16` | 1 | 0 | **1** | 이행 | `EB091FBFD57A` |
| `POQSettleProc17` | 8 | 0 | **8** | 이행 | `7FC44E39DB68` |
| `POQSettleProc18` | 9 | 0 | **9** | 이행 | `746EC478B5E3` |
| `POQSettleProc19` | 7 | 0 | **7** | 이행 | `75C286C36C23` |
| `POQSettleProc2` | 18 | 0 | **18** | 실패 | `0D2E68CA8D99` |
| `POQSettleProc3` | 9 | 0 | **9** | 실패 | `3CCE8A5A5535` |
| `POQSettleProc4` | 0 | 0 | **0** | 실패 | `F49F8442DE92` |
| `POQSettleProc6` | 0 | 0 | **0** | 실패 | `D22C977AE816` |
| `POQSettleProc7` | 0 | 0 | **0** | 실패 | `B39E2D3A47FF` |
| `POQSettleProc8` | 14 | 0 | **14** | 실패 | `75D2B150F080` |
| `POQSettleProc9` | 0 | 0 | **0** | 실패 | `0E4DD627DDCA` |

**합계 113건 · 그중 원본 인용은 1건(`Prco20`)뿐이다.** 즉 지금 코퍼스의 `CREATE PROCEDURE`는
사실상 전부 신규 정의다(112건). 분포는 **0~18**로 흩어지고 0인 Job이 넷
(`Proc4`·`Proc6`·`Proc7`·`Proc9`), 최대는 `Proc2`의 18이다.

> ⚠️ **0인 넷 가운데 셋은 「지켜진 0」이 아니다.** `Proc4`(단계 73 선언, 상한 40 초과)·
> `Proc7`(빈 `Steps`)·`Proc6`(`ErrorCodes` 전무)은 계획서 자체가 온전하지 않아서 0이다.
> 이 셋은 재생성 비교 표본에서 빼야 한다(같은 이유로 `Proc5`·`Proc20`은 애초에 대상 밖).
>
> **`Proc9`만 건강한 0이다** — 단계 18개를 갖춘 온전한 계획서가 신규 저장 프로시저를 하나도
> 정의하지 않았다. 규칙 없이도 그 모양이 나온 유일한 표본이므로, 4단계에서 「규칙이 먹었다」의
> 대조군으로 쓸 수 있다.

## 2. 축 2 — 레거시 반환 코드 결속 (§5 표의 둘째 행)

**측정 방법**: `MechanicalValidator.ValidateConsolidated`를 20편에 돌려
`ErrorType.LegacyReturnCodeNeverBound`의 발화 여부를 본다. **기계가 재현하는 목록이다.**

**이행 6 · 실패 14.** 2026-08-27 스윕(`2026-08-27-legacy-return-code-sweep.md` §8-1)의 실측과
**이름까지 정확히 일치**했다 — 이행은 `Batch1`·`Prco20`·`Proc16`·`17`·`18`·`19`, 나머지 14편이
실패. 그 사이 코퍼스도 검사도 바뀌지 않았음이 이 재현으로 확인된다.

## 3. 축 3 — 코드 대입 진짜 결손 (§5 표의 셋째 행)

**기계가 재 주지 않는다.** 대입 도달성 검사는 만들지 않기로 결정됐다(설계서 §3 머리의 상자).
정본 수치와 절차는 `docs/audit-reports/sweeps/2026-08-27-error-code-reach-sweep.md`에 있다.

| # | Job / 단계 | 코드 | 원본 근거 | 단계 문서 sha256 |
|---|---|---|---|:--|
| 1 | `POQSettleProc19/S11` | `-9` | `UP_UTIL_SETTLE_COMM_UPD` DDL 라인 291 | `6a76725e7cca` |
| 2 | `POQSettleProc13/S14` | `0` | `UP_UTIL_SETTLE_PROC_ETC` DDL 라인 153 | `4c312b8f7734` |

> ⚠️ **재측정은 반드시 「대입」 기준으로 하라. 「등장」으로 재면 아무것도 안 보인다.**
>
> 이 기준선을 뜨면서 확인용으로 순진한 grep을 돌려 봤더니 `Proc19/S11`에서 `-9`가 **2번**
> 잡혔다. 그대로 읽으면 「결손 아님」이 된다. 그 2건은 오류 코드 표와 산문의 **등장**이고
> 대입 자리가 아니다(스윕 §4가 151개를 하나씩 열어 가른 이유가 이것이다).
> `Proc13/S14`의 `= 0` 2건도 같다. **이 문단의 grep 수치는 근거가 아니라 반례다.**

## 3-1. ⚠️ 이 기준선 직후에 캐시 17이 들어왔다

`58c1ef6`(다른 세션)이 `CurrentCacheFormatVersion`을 17로 올렸다. **명세서 전건 재생성이
무장됐다는 뜻이다** — 다음 SP 분석 실행이 캐시를 통째로 미스 처리한다. 이 기준선은 그
**승격 커밋 시점의 산출물**을 잰 것이고, 아직 명세서는 재생성되지 않았다.

`Spec.md`는 계획서 공유 프롬프트 접두사(잡당 약 481KB)의 **대부분**이다. 그래서 4단계
재생성이 명세서 재생성 **뒤에** 이뤄지면 입력과 규칙이 **함께** 바뀐 상태가 되고, 신규 SP
정의 수의 변화가 규칙 때문인지 명세서 세대 때문인지 이 표만으로는 가를 수 없다.

가르는 방법 둘 — **어느 쪽인지 4단계 착수 전에 정하고 이 문단에 적을 것.**

1. 명세서 재생성 **전에** 계획서를 재생성한다(입력 고정, 규칙만 변수). 이 기준선이 성립하는
   조건이다.
2. 명세서 재생성 **후에** 규칙 이전/이후를 각각 한 번씩 돌린다(비용 두 배, 대신 두 요인이
   갈린다).

같은 축의 다른 기준선이 `2026-08-27-step-sweep-pre-cache17.md`(단계 검사 쪽)에 있다.
이 문서는 계획서 쪽이다 — 둘은 재는 대상이 다르니 수치를 섞지 말 것.

> **[2026-08-28 정정] 재생성이 시작됐다. 갈래 1은 이미 닫혔고 코퍼스는 혼재 상태다.**
>
> 위 문단을 쓸 때는 명세서 14편이 전부 08-25(캐시 16)였다. 그 뒤 캐시 17 재생성이
> 시작됐고, 이 회차의 실측으로 `output/Procedures` 14편 중 **6편**이 캐시 17의
> 「오류 코드 (기계 확정 …)」 표를 갖고 나머지 8편은 갖지 않는다(08-27 이후 바뀐
> 명세서 17개). **한 세대가 아니다.**
>
> ⚠️ 동료 세션의 진행 기록은 「프로시저 17: 5/14 · 함수 17: 14/17」로 적는다. 프로시저
> 쪽은 이 실측과 같은 것을 세지만(6 대 5는 그 사이 한 편이 더 끝난 것으로 보인다),
> **함수 축에는 이 판정 기준이 성립하지 않는다** — 오류 코드 표는 함수에 붙는 표가
> 아니라서 이 회차의 함수 실측은 0/10이다. **두 수치를 같은 자로 읽지 말 것.**
>
> **결과: 지금 `output/`에서 계획서를 재생성하면 입력이 혼재 세대가 된다** — 규칙 효과와
> 세대 효과가 갈리지 않을 뿐 아니라, 계획서마다 입력 세대가 다를 수도 있다. 그래서
> 갈래 1(명세서 재생성 **전에** 계획서를 돌린다)은 선택지에서 사라졌다.
>
> **대신 통제군 입력 트리를 떴다** — `output.bak-stage4-control-20260828/`.
> `output.bak-2026-08-22/`에서 **읽기 복사**한 캐시 16 이전 세대 고정본이고(원본은
> 손대지 않았다), 그 세대가 바로 이 기준선의 계획서 20편이 실제로 본 명세서다.
> 실행 방법은 그 트리의 `README.md`와 `run-control.sh`에 있다 — 공유 `output/`도
> `appsettings.local.json`도 건드리지 않고 환경변수로 출력 경로를 프로세스 단위 주입한다.

## 4. 4단계에서 이 문서와 무엇을 비교하는가

| 축 | 기준선 | 규칙이 먹었다면 |
|---|---|---|
| 신규 SP 정의 수 | **112** (0~18 분포, 20편) | **표본 전부 0** — 원본 인용 갈래만 남는다 |
| 결속 실패 | **14 / 20** | 줄어야 한다 |
| 코드 대입 진짜 결손 | **2건**(위 좌표) | 줄어야 한다. **손으로 잰다** |

**오독 방지 셋.**

1. **분모는 2건이지 151개가 아니다.** 정당한 미대입 62 · 정규식 맹점 87은 재생성해도 남는다 —
   원인이 `Steps[].ErrorCodes`가 체인 합집합이라는 **구조**이고 규칙 변경이 그 구조를 안 바꾼다.
2. **C# 모양(`StepResult` vs `TaskletResult`, `SqlTransaction` vs `TransactionScope`)은 수렴하지
   않는다.** 규칙이 API를 정하지 않기로 했으므로 의도된 결과다(설계서 §1).
3. **L1 검사 셋의 침묵은 통과가 아니다** — `CheckStepIdInitialValue`·
   `CheckCatchDiscardsReturnCode`·`CheckShadowBackupContract`(설계서 §8-4).

## 5. 재실행 레시피

프로브는 일회성이라 커밋하지 않는다. 다시 만들 때 쓸 원문을 싣는다 —
`tests/ReSet.Core.Tests/TempBaselineProbe.cs`로 두고 돌린 뒤 지운다. 코퍼스 심링크 둘을 건
워크트리에서 돌려야 한다(하나만 걸면 다른 계열이 조용히 꺼진다 — AGENTS.md 범주 8).

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    // [임시 프로브 - 커밋하지 않는다] 4단계 재생성 전 기준선을 실제 검사로 잰다.
    public class TempBaselineProbe
    {
        [Fact]
        public void MeasureBaseline()
        {
            var root = CorpusPaths.RepoRoot();
            Assert.NotEqual(string.Empty, root);

            var plans = Directory
                .GetFiles(Path.Combine(root, "output", "Jobs"), "BatchMigrationPlan.md", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            var originals = Directory
                .GetDirectories(Path.Combine(root, "output", "Procedures"))
                .Select(d => Path.GetFileName(d).Split('.').Last().ToUpperInvariant())
                .ToHashSet();

            var createProc = new Regex(@"CREATE\s+(OR\s+ALTER\s+)?PROCEDURE\s+([\w\.\[\]]+)", RegexOptions.IgnoreCase);
            var sb = new StringBuilder();
            sb.AppendLine("job\tsp_total\tsp_quoting_original\tsp_new\tbinding\tsha256_12");

            foreach (var plan in plans)
            {
                var job = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(plan))!);
                var text = File.ReadAllText(plan);

                var matches = createProc.Matches(text);
                var quoting = matches.Count(m =>
                    originals.Contains(m.Groups[2].Value.Trim('[', ']').Split('.').Last().Trim('[', ']').ToUpperInvariant()));

                var result = new MechanicalValidator().ValidateConsolidated(text);
                var bound = !result.DetailedErrors.Any(e => e.Type == ErrorType.LegacyReturnCodeNeverBound);

                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(plan)))[..12];
                sb.AppendLine($"{job}\t{matches.Count}\t{quoting}\t{matches.Count - quoting}\t{(bound ? "이행" : "실패")}\t{hash}");
            }

            File.WriteAllText(
                "/private/tmp/claude-501/-Users-payletter-git-root-ReSet/7e5ff759-bb25-46fd-ad4d-fb369fa9c231/scratchpad/baseline.tsv",
                sb.ToString());
        }
    }
}
```

```bash
dotnet test tests/ReSet.Core.Tests --filter "FullyQualifiedName~TempBaselineProbe"
```

축 3은 이 프로브가 재지 않는다 — `2026-08-27-error-code-reach-sweep.md`의 절차를 따르라.
