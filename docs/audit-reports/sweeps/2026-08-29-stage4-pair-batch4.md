# 4단계 3차 통제군 — L1 층이 처음으로 일했다 (2026-08-29)

`POQSettleBatch3`과 짝을 이루는 판(`POQSettleBatch4`). 모델·입력 트리·SP 12개가 같고
**규칙·Few-Shot·L1만 다르다.**

물음은 둘이었다.

1. §10-4의 Few-Shot 두 층 처방이 `S05`를 앱 코드로 끌어냈는가?
2. 2026-08-29에 넣은 L1 검사 넷이 실제 생성에서 재시도를 태우는가?

**둘 다 답이 나왔다. 1은 그렇다, 2는 아니다.** 그리고 셋째가 딸려 나왔다 —
**구속 조건이 L1에서 L2(Critic)로 옮겨갔다.** 그 사실이 남은 일의 순서를 바꾼다.

선행: `docs/superpowers/specs/2026-08-27-stage3-rule-rewrite-design.md` §9·§10 ·
`docs/audit-reports/sweeps/2026-08-29-rule-enforcement-census.md`

## 0. 측정 조건

- 산출물 `output.bak-stage4-control-20260828/Jobs/POQSettleBatch4`,
  로그 `logs-batch4/reset-20260829.log`. 하네스는 `run-pair-batch4.sh`.
- 구성: Actor `claude-cli`/`claude-sonnet-5` · Critic `OpenRouter`/`z-ai/glm-5.3` ·
  Consolidator `claude-cli`/`claude-sonnet-5`. **`Batch3`과 같다.**
- 레거시 SP 12개가 `Batch3`과 **같은 집합**이다(확인함). 단계 수만 16 → 17로 달라졌다.
- 실행 시각 14:15:34 ~ 16:39:00 (2시간 23분).
- **세는 법은 L1과 같다** — 코드 펜스 안 · mermaid 제외 · 주석과 문자열을 지운 사본.
  조사 §10-1의 줄 단위 주석 필터는 쓰지 않았다(그것이 §5의 NOLOCK 「2건」 착시를 낳았다).

> ⚠️ **도는 중에 공유 체크아웃의 바이너리가 한 번 덮여 썼다**(16:47:24, 다른 세션의 게이트).
> 실행 시작 시점 HEAD는 `f37c7154`였고, 그 뒤 HEAD까지 `ConsolidatedPlanRules`·
> Few-Shot 예시·`ValidateConsolidated`가 **바이트 동일**임을 확인했다. 이 판이 재는
> 것은 한 바이트도 안 움직였으므로 귀속은 살아 있다. 다음 판을 위해 하네스 머리말에
> 실행 전 점검 여덟 개를 박아 두었다.

## 1. 완주 판정 — 완주했다. 소진된 것은 L2다

**판독의 첫 물음은 「이 판은 완주했는가」다**(§9-0). `Batch2`가 그 물음을 안 던져
52/100짜리 반려 초안의 수치를 「옛 규칙의 정상 산출물」로 읽을 뻔했다.

| | `Batch2` 1차 | `Batch3` 2차 | **`Batch4` 3차** |
|---|---|---|---|
| 완주 | ✘ 52/100 구제 | ✔ | **✔ 84/100 구제** |
| 생성 실패·`exit 1` | 다수 | 0 | **0** |
| 플레이스홀더 단계 | S15·S16·S17 | 0 | **0** (17단계 전부 실체) |
| 소진된 층 | 생성 실패 | — | **L2(Critic)** |

`Batch2`와 달리 **본문이 죽어서 채택된 것이 아니다.** 17단계가 모두 실체이고,
채택본은 L1을 **오류 0으로** 통과했다(16:35:02 `기계적 검증 완료 - 결과: true, 에러 개수: 0개`).
그럼에도 Critic의 품질 기준을 못 넘겨 6회 예산이 끝났다.

## 2. 재시도 예산이 어디로 갔나 — L1 2회, L2 4회

```
시도 1  14:50  L2 결함  39/50
시도 2  15:06  L2 결함  38/50  → 점수가 안 올라 목차 재설계
시도 3  15:30  L1 실패  ← SqlSideControlFlow (`END TRY` 1건)
시도 4  15:51  L1 실패  ← BatchRunRowNeverCreated + LegacyReturnCodeNeverBound
시도 5  16:24  L1 통과 · L2 결함  42/50  ← 채택
시도 6  16:38  L1 통과 · L2 결함  37/50
        [ERR] L2 AI 리뷰 최종 보완 실패. 5차 시도(84/100) 채택.
```

시도 3·4는 L1에서 죽어 **Critic에 도달하지 못했다**(Critic 응답이 넷뿐인 것으로 확인).

**채택본을 막은 것은 한 축이다.** 5차의 축별 점수는 정확 8 · CRUD 9 · 인터페이스 9 ·
**예외처리 7** · 가독성 9이고, 기준은 축마다 8이다. **예외처리 7 하나가 6회를 끝냈다.**

> **이것이 이 판의 셋째 발견이다.** 조사 §6-(4)는 「강제는 셋이 겹쳐야 한다 —
> 프롬프트·Critic·L1」이라고 적었다. 그 셋 중 **지금 산출물을 붙들고 있는 것은
> Critic이다.** L1은 두 번 발화하고 두 번 다 닫혔다. **L1 검사를 더 늘리는 것의
> 한계 수익이 그만큼 줄었다** — 조사 §5의 B급 순서를 그대로 따르기 전에 이 사실을
> 함께 읽어야 한다.

## 3. 대조표 — 전 지표 개선

`Batch3` → `Batch4`, 코드 펜스 안 기준.

| 지표 | `Batch3` | `Batch4` |
|---|---:|---:|
| **L1 실존 API 타입** | **12** | **0** |
| L1 SQL 쪽 제어 흐름 | 0 | 0 |
| L1 `NOLOCK` | 0 | 0 |
| L1 신규 DB 객체 | 0 | 0 |
| `BEGIN TRAN` / `COMMIT TRAN` | 4 / 4 | **0 / 0** |
| `ROLLBACK TRAN` | 1 | **0** |
| `sp_executesql` | 0 | 0 |
| `SET TRANSACTION ISOLATION` | 0 | 0 |
| 펜스 | `sql` 69 · `csharp` 12 | **`sql` 80 · `pseudocode` 17 · `csharp` 0** |
| 규모 | 4,072줄 / 225,608B | 4,445줄 / 254,089B |

**API 지정 12 → 0이 이 표의 본론이다.** `Batch3`은 규칙 3-1에 그 조항이 **있는 채로**
12건을 냈고, Critic(`glm-5.3`)은 추론 로그에 그것을 적고도 감점하지 않았다(§10-4).
즉 프롬프트와 Critic 두 층이 **둘 다 흘린 축**이었다. L1을 넣자 사라졌다.

> **조사 §6-(4)의 세 번째 층이 일한 첫 실측이다.** 그 문단은 「L1이 놓칠 수 없는 축을
> 못박는다」를 원리로 적었을 뿐 실증이 없었다. 이제 있다.

## 4. §10-4 Few-Shot 두 층 처방 — 먹었다

`S05`는 §10-4가 지목한 유일한 잔존 사례였다. 규칙 2가 「chunk paging pseudocode」를
필수 산출물로 이름 붙이는데 그 이름에 대응하는 워크드 예시 둘이 전부 T-SQL이라,
**즉흥할 여지가 있는 단계는 규칙을 따르고 템플릿과 정확히 겹치는 단계는 템플릿을
따랐다.** 처방은 예시를 두 층으로 가르는 것이었다 — 바깥은 언어 중립 의사코드,
안쪽은 앱이 보내는 문장.

| `S05` 국소 | `Batch3` | `Batch4` |
|---|---|---|
| 펜스 구성 | `sql` · `sql` · `sql` | **`pseudocode` · `sql`** |
| `BEGIN TRAN` / `COMMIT TRAN` | 3 / 3 | **0 / 0** |
| SQL 쪽 제어 흐름 | 0 | 0 |

실물이 처방이 그린 모양 그대로다.

```
```pseudocode
currentStepErrorCode = 0   // 0 = 아직 실패 지점 없음
journal.insertRunning(runId, "S05", startedAtUtc: now())
reqYmd = queryScalar(SQL_S05_GET_REQ_YMD, { p_batchYmd: batchYmd })
...
beginTransaction()
try:
    currentStepErrorCode = -1
    execute(SQL_S05_DELETE, { p_batchYmd: batchYmd, p_reqYmd: reqYmd })
```

문서 전체에서 `csharp` 펜스 12개가 **0이 되고 `pseudocode` 17개로 갈렸다.** 처방이
바깥 층을 언어 중립으로 못박은 결과이고, 규칙 3-1의 「특정 API를 정하지 말라」와도
같은 방향이다(§3의 API 12 → 0과 같은 뿌리다).

## 5. 새 L1 넷의 실전 성적 — 재시도를 태우지 않았다

이것이 이 판을 돌린 두 번째 이유였다. `reset-l1-check` 스킬의 「흔한 실패」 마지막 줄이
겨눈 위험 — **스윕은 이미 쓰인 산출물만 보고, 재생성에서 모델이 새로 쓰는 모양은
스윕에 없다.** 선례로 `CheckParameterColumnClaims`가 스윕 0으로 통과했다가 재생성에서
`PROC_ETC` 6/6을 소진시켰다.

| 검사 | 발화 | 결말 |
|---|---|---|
| `CheckSqlSideControlFlow` | 시도 3에 1건(`END TRY`) | **다음 회차에 사라짐** |
| `CheckNoLockHints` | 0 | — |
| `CheckPrescribedFrameworkType` | 0 | 산출물의 12 → 0에 기여(3절) |
| `CheckNewDatabaseObjectDefinition` | 0 | — |

**넷 중 하나가 한 번 발화하고 한 회에 닫혔다.** 시도 4의 L1 실패 둘은 새 검사가 아니라
2026-08-27부터 있던 `CheckBatchRunRowCreation`·`CheckLegacyReturnCodeBinding`이다.

> **`END TRY` 1건은 오탐 의심을 받았으나 취소한다.** 진짜 T-SQL `TRY...CATCH`라면 넷이
> 함께 나오므로(`BEGIN TRY`·`END TRY`·`BEGIN CATCH`·`END CATCH`) 하나만 걸린 것이
> 수상했고, 판정식의 `\s+`가 개행을 넘어 앱 의사코드의 `END` 다음 줄 `TRY`를 잡을 수
> 있다는 기제도 실재했다. **그러나 다음 회차에 재발하지 않았다.** 붙박이 거짓 고발이면
> 모델이 앱 쪽 `try`를 계속 쓰는 한 매 회 다시 나왔어야 한다. 실제로 채택본은
> `try:`를 앱 의사코드에 쓰면서 이 검사에 걸리지 않는다(3·4절). `\s+` 수정은 근거가
> 없으므로 하지 않는다 — 재발하면 그때 `[^\S\n]+`로 좁힌다.

## 6. 새로 드러난 결함 — 컴파일 안 되는 mermaid가 채택본에 남았다

**🔴 채택본 3번째 mermaid 펜스 20행이 문법 오류다.**

```
sequenceDiagram
...
Settle--->Batch: S12 반환 코드 전달      ← `--->`는 sequenceDiagram에 없는 화살표
```

mermaid CLI가 **두 번(16:21:18 · 16:35:01) 이것을 정확히 잡았다.**

```
Error: Parse error on line 20:
... 하위 호출 실행    Settle--->Batch: S12 반환 코드
-----------------------^
```

그런데 산출물에 남았다. 경위는 설계된 것이다 — `MechanicalValidator.ValidateMermaid`가
CLI 종료 코드 ≠ 0을 만나면 *"린트 실패를 치명적 오류로 처리하지 않고, Fallback 기계
린터로 검증 우회"*한다(`:5871`). 그리고 `ValidateMermaidFallback`은 **노드 라벨 따옴표**를
보는 flowchart 지향 린터라 sequenceDiagram의 화살표 문법을 못 본다.

**문제는 강등 자체가 아니라 두 상황을 못 가르는 것이다.** CLI가 없거나 시간 초과인 것과,
**CLI가 정상 실행되어 파스 오류를 stderr로 보고한 것**은 다른 사실이다. 앞은 도구 부재라
강등이 옳고, 뒤는 **확정된 발견**이다. 지금은 둘 다 같은 경로로 흘러간다.

조사 §2의 규칙 14 행이 「직접 검사 없음 · ◐」이었는데, 이 건은 그것과 또 다르다 —
**검사가 있고, 잡았고, 통과시켰다.**

## 7. 이 판이 답하지 못한 것

- **「규칙만으로 신규 SP가 사라지는가」** — 답하려 들지 마라. `sonnet-5`는 원래
  `CREATE PROCEDURE`를 안 쓴다(§10-2). 그리고 이제 L1이 못박았으므로 그 물음은
  결함이 아니라 재시도 수의 문제로 격하됐다(§10-2의 ⛔ 상자).
- **terra 계열에서 L1이 요동치는가** — terra가 커밋된 기본값이므로 다음 실제 생성이
  저절로 답한다.
- **예외처리 7/10의 내용** — Critic이 무엇을 지적했는지는 이 조사가 세지 않았다.
  L2가 구속 조건이 된 이상 그것이 다음 조사의 대상이다.

## 8. 다음 — 순서가 바뀌었다

**②③ 재정박이 B급 5보다 앞선다.** 근거 둘.

**(1) 이 판이 막혔던 재료를 만들었다.** 조사 §3이 `CheckCatchDiscardsReturnCode`·
`CheckStepIdInitialValue`를 「침묵」으로 판정했고(재료인 `BEGIN CATCH`가 옛 규칙 판
314/343 → 새 규칙 판 0/16), 재정박은 「앱 실패 경로가 어떤 모양인가」를 몰라 보류됐다.
이제 실물이 있다.

```
currentStepErrorCode = -1
execute(SQL_S05_DELETE, {...})
...
journal.updateFailed(runId, "S05", LegacyReturnCode: currentStepErrorCode, ...)
```

두 검사가 지키던 의무 — 「실패 경로가 반환 코드를 흘리지 않는가」와 「상태 변수 초기값이
업무 코드와 겹쳐 장애를 성공으로 위장하지 않는가」 — 가 앱 쪽에서 이 모양으로 나타난다.

**(2) L2가 구속 조건이므로 L1을 아홉 번째로 늘리는 것의 수익이 줄었다**(2절).
B급 5(규칙 12 청크 키 실재)는 재료가 싸다는 것이 유일한 장점이었는데, 그 장점이
「지금 산출물을 붙들고 있는 것은 L1이 아니다」 앞에서 약해진다.

**mermaid 강등(6절)은 별개 축이고 값이 싸다.** CLI가 파스 오류를 보고한 경우와 도구
부재를 가르는 것은 국소 수정이고, 실측된 결함이 하나 있다.

## 9. 재실행 레시피

```bash
C=output.bak-stage4-control-20260828

# 1. 완주했는가 — 다른 무엇보다 먼저
grep -nE '중단되어 가장 높은 점수|최종 보완 실패|생성 실패|종료 코드: [1-9]' $C/logs-batch4/*.log

# 2. L1과 L2가 각각 몇 번 잡았나
grep -nE 'L1 기계 검증 오류 발견|L2 AI 리뷰 결함 발견' $C/logs-batch4/*.log

# 3. Critic 축별 점수 (스키마 예시 50/50이 섞이므로 중복을 접어 읽을 것)
grep -oE '"Score[A-Za-z]+":[0-9]+' $C/logs-batch4/*.log

# 4. 산출물 수치 — 세는 법은 L1과 같다(펜스 안·mermaid 제외·주석/문자열 블랭킹)
#    조사 §10-1의 줄 단위 주석 필터를 쓰지 말 것
```

**주의**: 생 `grep`으로 세지 마라. `NOLOCK`은 산문에 약 300건, 코드 안에 0건이고
산문의 것은 전부 「제거했다」는 이행 서술이다. 같은 함정이 `SqlConnection`에도 있다
(산문 35 대 코드 26).
