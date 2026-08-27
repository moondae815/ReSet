# 레거시 반환 코드 결속의 코퍼스 영향 측정 (2026-08-27)

언어 이전 후 레거시 오류 코드(`@po_intRetVal`)는 T-SQL 반환값이라는 거처를 잃는다.
레거시 호출자가 그 코드값에 의존하므로 버릴 수는 없고, 계약이 이미 정한 거처는
`batch.BatchStepJournal.LegacyReturnCode` 컬럼이다.

이 문서는 **지금 20개 통합 계획서 중 몇 개가 그 결속을 실제로 하고 있는지**를 잰
관측 기록이다. 프로덕션 코드는 바꾸지 않았다. 이 커밋에는 이 보고서만 들어 있다.

**결론 먼저: 이행 6건, 실패 14건.**

---

## 0. 측정 조건과 코퍼스

측정 대상은 통합 계획서(`BatchMigrationPlan.md`) **20건**이다. 브리프의 기대 수치와 같다.

```
$ ls /Users/payletter/git-root/ReSet/output/Jobs/*/docs/BatchMigrationPlan.md | wc -l
20
```

**다만 그 20건은 `POQSettleProc1`~`Proc20`이 아니다.** 실제 구성은 다음과 같다.

- `output/Jobs` 아래 Job 디렉터리는 **22개**다.
- 그중 `POQSettleProc5`와 `POQSettleProc20`은 `raw/`만 있고 `docs/`가 없다 — 계획서가
  생성된 적이 없다. 이 둘은 측정 대상 밖이다.
- 대신 `POQSettleBatch1`과 `POQSettlePrco20`(이름의 `Prco`는 원본 오타로 보이나 그대로 둔다)이
  계획서를 갖고 있다.

따라서 대상 20건은 `Batch1`, `Prco20`, `Proc1`~`Proc4`, `Proc6`~`Proc19`다.
**Job 이름 목록을 `Proc1..Proc20`으로 가정하고 판정하면 두 건이 틀린다.**

읽은 것은 `docs/BatchMigrationPlan.md` 본문뿐이다(총 161,285줄). 심링크 기반 코퍼스 테스트는
돌리지 않았다 — 이 측정은 C# 테스트가 아니라 원문 판독이다(§4 참조).

---

## 1. 이름 퍼짐 — 출발점

레거시 반환 코드의 운반체 이름이 계획서마다 갈린다. 브리프가 제시한 컨트롤러 실측을
직접 재현했고, **세 수치 모두 일치했다.**

```bash
grep -c "LegacyReturnCode" output/Jobs/*/docs/BatchMigrationPlan.md
grep -c "LegacyRetVal"     output/Jobs/*/docs/BatchMigrationPlan.md
grep -c "po_intRetVal"     output/Jobs/*/docs/BatchMigrationPlan.md
```

| Job | `LegacyReturnCode` | `LegacyRetVal` | `po_intRetVal` |
| :--- | ---: | ---: | ---: |
| `POQSettleBatch1` | 16 | 0 | 57 |
| `POQSettlePrco20` | 29 | 0 | 111 |
| `POQSettleProc1` | 0 | 37 | 76 |
| `POQSettleProc2` | 1 | 43 | 151 |
| `POQSettleProc3` | 25 | 0 | 83 |
| `POQSettleProc4` | 0 | 0 | 17 |
| `POQSettleProc6` | 111 | 0 | 156 |
| `POQSettleProc7` | 0 | 1 | 16 |
| `POQSettleProc8` | 22 | 6 | 88 |
| `POQSettleProc9` | 45 | 0 | 68 |
| `POQSettleProc10` | 0 | 85 | 100 |
| `POQSettleProc11` | 0 | 0 | 74 |
| `POQSettleProc12` | 65 | 0 | 63 |
| `POQSettleProc13` | 0 | 0 | 49 |
| `POQSettleProc14` | 14 | 17 | 74 |
| `POQSettleProc15` | 6 | 0 | 80 |
| `POQSettleProc16` | 38 | 0 | 58 |
| `POQSettleProc17` | 18 | 0 | 81 |
| `POQSettleProc18` | 34 | 0 | 106 |
| `POQSettleProc19` | 18 | 0 | 97 |

- `po_intRetVal`: **20개 전부**, 16~156회(최소 `Proc7` 16, 최대 `Proc6` 156). 레거시 출력
  파라미터 자체는 어디서도 버려지지 않았다 — 즉 **의무 1은 20건 모두 통과한다.**
- `LegacyReturnCode`: 14개. `LegacyRetVal`: 6개(`Proc1`·`Proc2`·`Proc7`·`Proc8`·`Proc10`·`Proc14`).
- 둘 다 없는 것 3개(`Proc4`·`Proc11`·`Proc13`), 두 이름을 함께 쓰는 것 3개(`Proc2`·`Proc8`·`Proc14`).

그리고 이름은 이 셋으로도 안 끝난다. 이름만 더 세면 다음이 더 나온다.

| Job | 실제로 쓰는 이름 | 좌표 |
| :--- | :--- | :--- |
| `POQSettleProc4` | `JobResult.LegacyCode` | 423, 447-449 |
| `POQSettleProc11` | `LegacyIntRetVal` | 74, 85-87 |
| `POQSettleProc13` | `LegacyPoIntRetVal` | 64 |
| `POQSettleProc7` | `RetValWasAssigned` · `LegacyRetValAssigned` | 99, 275 |
| `POQSettleProc6` | `LegacyOutcome.LegacyReturnCode` | 97, 197 |
| `POQSettleProc9` | `SettlementStepResult.LegacyReturnCode` | 61, 103 |
| `POQSettleProc17` | `@v_legacyReturnCode` (T-SQL 지역변수) | 223, 234-235 |

**이 표가 이름을 기준으로 삼으면 안 되는 이유 전부다.** 아래 판정은 이름을 세지 않는다.

---

## 2. 의무 정의

한 계획서가 의무를 이행했다고 보려면 다음 둘이 **모두** 참이어야 한다.

1. **레거시 반환 코드를 보존한다** — `@po_intRetVal` 또는 그에 상당하는 원본 출력
   파라미터를 서술한다.
2. **그 값이 `batch.BatchStepJournal`의 `LegacyReturnCode` 컬럼에 도달한다** — 그 테이블에
   쓰는 `INSERT` 또는 `UPDATE` 문장이 이 컬럼을 대상으로 삼는다.

2번이 핵심이다. C# 필드 이름이 `LegacyReturnCode`든 `LegacyRetVal`든 `LegacyIntRetVal`이든
상관없다 — **컬럼에 닿는지만 본다.** 반대로 컬럼 이름을 스키마 표나 매핑 표에 옮겨 적기만 하고
쓰는 문장이 없으면 이행이 아니다.

§1에서 본 대로 **의무 1은 20건 전부 통과**한다. 따라서 판정을 가르는 것은 오직 의무 2다.

---

## 3. 판정 결과

### 3-1. 먼저: 계약 테이블을 아예 부르지 않는 계획서가 13건이다

의무 2는 `batch.BatchStepJournal`에 쓰는 문장을 요구한다. 그 테이블을 한 번도 이름으로
부르지 않는 계획서는 그 문장을 가질 수 없다.

```
$ grep -ric "stepjournal" output/Jobs/*/docs/BatchMigrationPlan.md | grep -v ":0"
POQSettleBatch1:31   POQSettlePrco20:41   POQSettleProc15:57
POQSettleProc16:72   POQSettleProc17:46   POQSettleProc18:80   POQSettleProc19:43
```

**7건만 계약 테이블을 부른다.** 대소문자를 무시했으므로 표기 흔들림도 포함한다. 나머지
13건(`Proc1`·`2`·`3`·`4`·`6`·`7`·`8`·`9`·`10`·`11`·`12`·`13`·`14`)은 **후보에서 기계적으로 제외된다.**

### 3-2. 이행 — 6건

| Job | 컬럼을 대상으로 삼는 문장 (좌표) | 실제 코드값을 싣는 문장 |
| :--- | :--- | :--- |
| `POQSettleBatch1` | `INSERT INTO batch.BatchStepJournal` 353·536 (컬럼 목록에 `LegacyReturnCode`), `UPDATE` 472·495·589 | 없음(SQL은 전부 `NULL`) — 산문 결속 77·78·1499·2811·3112 |
| `POQSettlePrco20` | `UPDATE batch.BatchStepJournal ... LegacyReturnCode = @v_currentStepId` 229, `INSERT` 742·6334·6889 | **있음** 229 |
| `POQSettleProc16` | `UPDATE ... LegacyReturnCode = -3` 7644, `= @v_currentErrorCode` 7703, `INSERT` 2606·2634·4189 | **있음** 7644·7703 |
| `POQSettleProc17` | `INSERT` 7079·7519·7766, `UPDATE ... = NULL` 7621·7674·7881 | 없음(SQL은 전부 `NULL`) — 산문 결속 122·251·253·2647·2659·2932·2964 |
| `POQSettleProc18` | `UPDATE ... LegacyReturnCode = @po_intRetVal` 363, `= @v_currentStepId` 385, `= @LegacyReturnCode` 8326, `INSERT` 326·1266·1734·7990·8278·8477 | **있음** 363·385·8326 |
| `POQSettleProc19` | `UPDATE ... LegacyReturnCode = @v_currentStepId` 319·342·6091, `INSERT` 370·981·1212·5864 | **있음** 319·342·6091 |

**이름이 아니라 역할로 읽어야 하는 자리가 여기 있다.** `Prco20`·`Proc19`·`Proc18`의
`@v_currentStepId`는 이름이 "단계 ID"지만 역할은 레거시 오류 코드다. `Proc19` 309·312행이
`SET @v_currentStepId = -101; -- 원본 오류 코드 -101에 해당하는 DML`로 대입하고, 355행이
`SET @po_intRetVal = @v_currentStepId;`로 같은 값을 출력 파라미터에 되돌린다. 이름으로
걸렀다면 이 셋을 실패로 잘못 찍었을 것이다.

**경계 사례 둘 — `Batch1`과 `Proc17`은 이행으로 판정했고, 근거는 이렇다.**
두 계획서의 SQL 예시는 `LegacyReturnCode`에 `NULL`만 쓴다. 그러나
(a) 두 계획서 모두 컬럼을 대상으로 삼는 `INSERT`/`UPDATE` 문장을 실제로 갖고 있고,
(b) `NULL`인 이유가 명시적이고 옳다. `NULL`을 쓰는 자리는 레거시 반환값이 **애초에 없는**
오케스트레이션 단계뿐이며, 그 단계들의 오류 코드는 `B100`·`B101`·`B120`·`B121`·`B160`·`B161`
같은 **영숫자 배치 제어 코드**여서 `INT` 컬럼에 담기지 않는다.
- `Batch1` 340행: "`LegacyReturnCode`는 `INT`이므로 영숫자 코드 `B100`, `B101`은 저장하지
  않고 `NULL`로 두며, 정확한 코드는 `ErrorMessage`에 보존한다" — 해당 단계는 S01.
  같은 취지가 S02(`UPDATE ... StepCode = N'S02'`의 `LegacyReturnCode = NULL` 472·495),
  S03(519행, `B120`·`B121`), S16(3280행, `B160`·`B161`)에 이어진다.
  81행이 이를 규칙으로 총괄한다: "레거시 반환 코드가 없는 경우 `LegacyReturnCode`는 `NULL`이다."
  (`Batch1`에 S17은 존재하지 않는다 — 문서 내 `S17` 언급 0건.)
- `Proc17` 7441행: "`LegacyReturnCode`는 레거시 프로시저 오류 코드가 없으므로 `NULL`로
  유지하고, 오류 식별자는 `ErrorMessage`에 기록한다" — 해당 단계는 S17 게시 단계
  (`APP-PUBLISH-001`)이고, 7881행이 S18에 같은 처리를 한다.

(c) 레거시 코드가 **있는** 단계에 대해서는 결속을 산문으로 못박는다 — `Batch1` 77행
"`@po_intRetVal`을 `batch.BatchStepJournal.LegacyReturnCode`에 기록", `Proc17` 122행
"모든 단계의 `@po_intRetVal`은 `batch.BatchStepJournal.LegacyReturnCode`에 기록한다".
(d) 그리고 `Batch1`은 **성공값 적재까지** 못박는다 — 2811행: "성공 커밋 후
`LegacyReturnCode = 0`, `StepStatus = 'Succeeded'` … 로 기록한다. 실패 시
`StepStatus = 'Failed'`, **실제 반환 코드**와 예외 메시지를 기록하며". 즉 `NULL`만 쓰는
것처럼 보이는 것은 SQL 예시가 오케스트레이터 자신의 단계에 한정되기 때문이고, 값을 싣는
경로는 문서가 별도로 규정한다.

**다음 사람이 재검토할 지점은 여기다.** 더 엄한 기준(값을 싣는 SQL 문장을 요구)을 쓰면
`Batch1`과 `Proc17`은 실패로 넘어가고 이행은 4건이 된다. 이 보고서는 브리프 §Step 3이 쓴
기준("그 테이블에 쓰는 `INSERT` 또는 `UPDATE` 문장이 이 컬럼을 대상으로 삼는다")을 문자
그대로 적용해 6건으로 판정했다. Task 2의 검사가 값 적재까지 요구하도록 좁힌다면 발화 집합은
14가 아니라 16이 되며, **그 경우 이 표의 "실제 코드값" 열이 그대로 채점표다.**

### 3-3. 실패 — 14건

레거시 코드를 보존은 하되(`@po_intRetVal` 16~156회), 계약 컬럼에 닿지 않는다.
**"어디로 가는가"를 함께 적는다 — 이 열은 다음 사람이 「어떻게 고칠지」를 정하는 열이다.**
`DB 테이블`이면 **테이블을 옮기는 일**이고, `C# 객체뿐`이면 **저장 자체를 새로 만드는 일**이다.
둘은 고치는 방법이 다르므로 이 열의 오기는 판정의 오기만큼 비싸다.

아래 14행의 "값이 실제로 가는 곳"은 **전부 원문 write 문장을 열어 확인했다**(§4-1).

| Job | 실패 모양 | 값이 실제로 가는 곳 | 좌표(write 문장) |
| :--- | :--- | :--- | :--- |
| `POQSettleProc1` | **DB 테이블** | `dbo.POQSettleSqlErrorLog`(컬럼 `LegacyRetVal`) | `INSERT` 420-434 · 2081 · 2904 |
| `POQSettleProc2` | **DB 테이블** | `batch.POQBatchCheckpoint`(컬럼 `LegacyRetVal`) | `INSERT` 974-989 · `UPDATE` 1961-1964 · 4934-4937 |
| `POQSettleProc3` | C# 객체뿐 | `StepResult.LegacyReturnCode` — DB 컬럼 아님 | 1354 · 1386 · 1995 |
| `POQSettleProc4` | C# 객체뿐 | `JobResult.LegacyCode` — DB 컬럼 아님 | 423 · 447-449 |
| `POQSettleProc6` | **DB 테이블**(셋) | `dbo.POQBatchStepCheckpoint` · `dbo.POQBatchErrorLog` · `dbo.POQBatchRun`(모두 컬럼 `LegacyReturnCode`) | `UPDATE ... = @po_intRetVal` 1170 · `= @v_currentStepId` 1497 · `INSERT` 4675-4683 · `UPDATE` 11685-11688 |
| `POQSettleProc7` | C# 객체뿐 | `RetValWasAssigned` 플래그 — 저장 대상 없음 | 99 · 275 · 301 |
| `POQSettleProc8` | **DB 테이블** | `SETTLE_POQ_DB.dbo.BatchStepCheckpoint` — **같은 테이블에 컬럼명이 둘**(`LegacyRetVal` 4997, `LegacyReturnCode` 6114) | `UPDATE` 4996-4998 · 6111-6114 |
| `POQSettleProc9` | **DB 테이블** | `batch.POQSettleCheckpoint`(컬럼 `LegacyReturnCode`) · 저장 프로시저 `batch.POQSettleError_Log`의 `@LegacyReturnCode` 인자 | `UPDATE` 2207-2209 · 읽기 회수 `SELECT @po_intRetVal = LegacyReturnCode` 4365 · `EXEC` 267-271 |
| `POQSettleProc10` | **DB 테이블**(같은 표를 **아홉 갈래**로 부른다) | `batch.POQSettleStepRun` 18 · `dbo.POQSettleStepRun` 13 · `POQBatch.SettleStepRun` 5 · `poqbatch.StepRun` 4 · `poqbatch.POQSettleStepRun` 3 · `[batch].[POQSettleStepRun]` 3 · `SETTLE_POQ_DB.POQSettleBatch.StepRun` 2 · `SETTLE_POQ_DB.POQBatch.POQSettleStepRun` 2 · `POQSettleBatch.POQSettleStepRun` 2 (모두 컬럼 `LegacyRetVal`) | `UPDATE` 561-563 · 2211-2213 · 3772-3774 · 5263-5265 |
| `POQSettleProc11` | **DB 테이블** | `batch.BatchStepExecution` / `dbo.BatchStepExecution`(컬럼 `LegacyIntRetVal`) · `batch.BatchSqlError.LegacyIntRetVal` 5855 | `UPDATE ... = @v_currentStepId` 700 · `= NULL` 677 · `= @po_intRetVal` 5760 · `= -3` 6151 · `EXEC @p_LegacyIntRetVal` 2952 · 매핑 133·290·1149·1792·2413·3609·5507 |
| `POQSettleProc12` | **DB 테이블 — 컬럼명은 맞고 테이블이 틀림** | `batch.BatchTaskRun.LegacyReturnCode` — 620-631행에서 `CREATE TABLE`로 새로 만든다 | `CREATE TABLE` 618-632 · `UPDATE` 242 · 288 · 1121 · 매핑 1198 |
| `POQSettleProc13` | C# 객체뿐 | `LegacyPoIntRetVal` — DB 컬럼 아님 | 64 · 72 |
| `POQSettleProc14` | **DB 테이블**(둘) | `batch.BatchRunStep` · `batch.BatchExecutionJournal`(모두 컬럼 `LegacyRetVal`) | `UPDATE ... = @po_intRetVal` 1757 · `= @v_currentStepId` 1814 · `INSERT` 1819-1826 |
| `POQSettleProc15` | C# 객체뿐 | `context.LegacyReturnCode` / 결과 모델 — `batch.BatchStepJournal` 쓰기 문장 35개 어디에도 이 컬럼이 없다 | 1043 · 1065 · 1067-1068 · 3614 · 4114 |

**고침 유형별 집계: DB 테이블 이전 9건**(`Proc1`·`2`·`6`·`8`·`9`·`10`·`11`·`12`·`14`),
**저장 신설 5건**(`Proc3`·`4`·`7`·`13`·`15`).

`Proc15`가 유일하게 §3-1을 통과하고 §3-2에서 떨어진 건이다. 계약 테이블에 35개 문장
(`INSERT` 26 · `UPDATE` 9, 좌표 306~6590)을 쓰면서 그중 어느 것도 `LegacyReturnCode`를
대상으로 삼지 않는다. `LegacyReturnCode` 6회는 전부 매핑 표(1043) 또는 C# 결과 모델
(1065·1067·1068) 이야기다. **컬럼 이름을 표에 옮겨 적기만 하고 쓰는 문장이 없는** 정확한
사례다.

`Proc12`는 반대 방향의 교훈이다. 컬럼 이름 `LegacyReturnCode`를 65회 쓰고 `int NULL`로
DDL까지 정의하지만, 그 컬럼은 **자기가 새로 만든 `batch.BatchTaskRun`** 위에 있다. 문자열
`LegacyReturnCode`로 grep하면 가장 성실해 보이는 계획서가 실패다.

---

## 4. 자동 판정 여부 — 기계 선별 + 사람 판정(하이브리드)

**전수 자동 판정은 하지 않았다. C# 프로브도 만들지 않았다.** 무엇을 기계가 닫았고 무엇을
사람이 읽었는지 **판정(§3-2·§3-3의 이행/실패)과 서술(§3-3의 "가는 곳" 열)을 나누어** 적는다.

### 4-1. 판정 — 기계 13건, 사람 7건

- **기계가 결정적으로 닫은 13건:** §3-1의 `grep -ric "stepjournal"`. 계약 테이블을 이름으로
  부르지 않는 계획서는 그 테이블에 쓰는 문장을 가질 수 없다. 대소문자를 무시했고 스키마
  접두사도 요구하지 않았다. **잔여 가정은 §4-2에 적는다.**
- **사람이 읽어 판정한 7건:** 계약 테이블을 부르는 7건(`Batch1`·`Prco20`·`Proc15`~`Proc19`)은
  원문 블록을 직접 읽었다. 정규식이 판정하지 못하는 것이 셋 있기 때문이다.
  (a) `INSERT`의 컬럼 목록과 `VALUES` 절이 떨어져 있어 **무슨 값이 실리는지**는 창(window)
  정규식으로 안 잡힌다. (b) `@v_currentStepId`처럼 **이름이 역할을 배신하는** 변수는
  대입 지점을 읽어야 판정된다. (c) `NULL` 쓰기가 결손인지 정당한지는 그 단계에 레거시
  코드가 있는지를 읽어야 안다.

**즉 실패 14건 중 13건은 정규식이, 1건(`Proc15`)과 이행 6건은 사람이 판정했다.**

### 4-2. 서술("가는 곳" 열) — 초안은 기계, 확정은 사람 전수

이 열의 **초안**은 `awk`로 만들었다 — "각 `LegacyReturnCode`/`LegacyRetVal` 줄이 어느
`INSERT`/`UPDATE` 문장 **20줄 안**에 있는가"를 집계하는 창 휴리스틱이다.
**그 초안은 틀린 것을 냈다.** 픽스 라운드에서 14행 전부의 write 문장을 열어 확인한 결과:

- `Proc11`: "C# 객체뿐"으로 적었으나 **실물은 DB 컬럼**이다(`batch.BatchStepExecution`
  `LegacyIntRetVal`, `UPDATE ... = @v_currentStepId` 700행). 고침 유형이 정반대로 읽혔다.
- `Proc9`: `batch.POQSettleChunkKey`를 운반체로 적었으나 **그 테이블은 레거시 코드를 안
  나른다**(552-571행의 컬럼은 `ChunkState`·`LastSqlError`·`LastErrorMessage`뿐). 창
  휴리스틱이 근처 줄을 엉뚱한 문장에 붙인 오귀속이다.
- `Proc10`: 표기 갈래를 "여덟"로 셌으나 **아홉**이다.
- 나머지 11행(`Proc1`·`2`·`3`·`4`·`6`·`7`·`8`·`12`·`13`·`14`·`15`)은 확인 결과 맞았다.

**교훈: 창 휴리스틱은 "어느 문장 근처인가"를 셀 뿐 "그 문장의 대상인가"를 세지 못한다.**
같은 도구로 만든 서술은 같은 방식으로 틀릴 수 있으므로 전수 재확인이 필요했다.
현재 §3-3의 열은 **20건 전체를 사람이 원문에서 확인한 것**이다.

### 4-3. §3-1 제외 논증의 잔여 가정

"이름으로 부르지 않으면 쓸 수 없다"가 성립하려면 **테이블 이름이 문헌에 문자 그대로 있어야
한다.** 즉 다음이 없어야 한다.

1. 동적 SQL로 테이블명을 조립하는 자리(`N'INSERT INTO ' + @tbl` 류)
2. 시노님(`CREATE SYNONYM`)이나 별칭으로 계약 테이블을 다른 이름으로 부르는 자리
3. 테이블명을 변수·파라미터로 받는 자리

**셋 중 2번·3번은 없고, 1번은 있다 — 다만 계약 테이블에 닿지 않는다.** 직접 확인한 결과는
이렇다.

- **시노님(2번): 코퍼스 전체 0건.** `grep -ric "CREATE SYNONYM"`이 20건 모두에서 0이다.
- **변수 테이블명(3번): 없다.** `INSERT INTO @X` 형태는 나오지만 전부 **테이블 변수**
  (`@ValidationResult`·`@Violations`·`@Checks` 등 `DECLARE @X TABLE`)이며, 테이블 이름을
  담은 변수가 아니다.
- **동적 SQL(1번): 있다.** `INSERT INTO ' +` 15회, `FROM ' +` 44회, `QUOTENAME` 119회가
  실재한다. 제외된 13건 중 `Proc11`·`Proc12`·`Proc13`·`Proc14`가 테이블명을 조립하고,
  `Proc2`·`Proc3`·`Proc9`·`Proc10`도 `QUOTENAME`을 쓴다.

  **그러나 조립되는 이름은 전부 Shadow 테이블이다** — `@v_shadowTable`·`@ShadowTableName`·
  `@v_shadowName`이며, 값은 `batch_shadow.S06_TSettleMst_BeforeImage`,
  `batch_shadow.S12_TSettleMiss_BeforeImage`처럼 **업무 테이블의 before-image**로 해석된다.
  대상 원천도 `SETTLE_POQ_DB.dbo.TSettleMst`·`TSettleMiss`·`TStatPGCollect` 같은 업무
  테이블뿐이다. 감사·저널 계열 테이블을 동적으로 조립하는 자리는 없다.

따라서 §3-1의 제외는 **"동적 SQL이 없어서"가 아니라 "동적 SQL이 Shadow 전용이어서"**
성립한다. 대소문자·괄호·스키마 접두사 흔들림은 `grep -ric`의 substring 매칭이 이미
흡수한다(`[batch].[BatchStepJournal]`도 `stepjournal`을 포함한다).

**다음 사람이 이 논증을 재사용할 때 다시 확인할 것:** 시노님 0건이 유지되는가, 그리고
동적 SQL의 조립 대상이 여전히 Shadow/업무 테이블에 한정되는가. 감사 계열 테이블명을
조립하는 자리가 하나라도 생기면 §3-1의 13건 제외는 더 이상 결정적이지 않다.

`tests/ReSet.Core.Tests/TempBindingProbe.cs`는 만들지 않았다. 위 기계 선별이 grep/awk로
같은 일을 하고 사람 판정이 나머지를 덮으므로, C# 프로브를 억지로 만들어 그 통과를
"다 잡았다"로 읽을 여지를 두지 않았다.

---

## 5. 설계서 §5-1의 여섯이 왜 틀렸는가

설계서(`docs/superpowers/specs/2026-08-27-sql-placement-criterion-design.md`) §5-1은 기대
집합으로 여섯을 지목했다 — `POQSettleProc1`·`4`·`7`·`10`·`11`·`13`. 그 여섯은 **문자열
`LegacyReturnCode`가 0회인 계획서 목록**이다(§1 표에서 그대로 확인된다). 즉 잰 것은
의무 이행이 아니라 **철자였다.**

그 결과 두 가지가 동시에 틀렸다. 첫째, 지목된 여섯은 실제로도 실패지만 **이유가 다르고
고치는 방법도 둘로 갈린다** — "그 이름을 안 쓴다"가 아니라 "계약 컬럼에 닿지 않는다"가
이유이며, 그 여섯 중 셋(`Proc1`→`dbo.POQSettleSqlErrorLog`, `Proc10`→아홉 갈래
`POQSettleStepRun`, `Proc11`→`batch.BatchStepExecution`)은 **값이 이미 DB 테이블에 있고
테이블만 틀린** 경우이고, 나머지 셋(`Proc4`·`Proc7`·`Proc13`)만 C# 객체에 머문다.
둘째, 그리고 더 나쁘게, **나머지 14건을 이행으로
암묵 판정했다.** 그중 8건(`Proc2`·`3`·`6`·`8`·`9`·`12`·`14`·`15`)이 실패다. 문자열은 있는데
결속이 없기 때문이다. `Proc12`가 표본이다 — 이름을 65회 쓰고 DDL까지 쓰지만 대상 테이블이
자기가 만든 `batch.BatchTaskRun`이다. `Proc15`는 계약 테이블에 35개 문장을 쓰면서 그 컬럼만
빼놓는다.

**한 문장으로: 이름 검색은 실패 6건을 맞히고 8건을 놓쳤다.** 놓친 8건은 "이름이 있으니
됐다"는 신호를 냈으므로, 그 목록을 그대로 썼다면 결손의 57%가 조용히 통과했을 것이다.
이 저장소가 반복해 앓는 병이 이것이다 — **이름을 역할의 대리로 쓰는 것.** 다음 사람은
`grep LegacyReturnCode`로 시작하지 말고, `batch.BatchStepJournal`에 쓰는 문장을 먼저 찾은
다음 그 문장이 이 컬럼을 대상으로 삼는지를 보라.

---

## 6. 재지 못한 것

이 판정이 놓칠 수 있는 것을 남긴다.

1. **단계 문서를 안 봤다.** 읽은 것은 `docs/BatchMigrationPlan.md` 20건뿐이다. 각 Job에는
   `agent/steps/*.md`, `agent/task-NN-SNN.md`, `agent/src`, `agent/tests`가 더 있다. 통합
   계획서가 빠뜨린 결속을 단계 문서가 갖고 있을 가능성은 이 측정으로 배제되지 않는다.
   다만 표본 셋(`Proc1`·`Proc4`·`Proc12`)의 `agent/` 트리 전체를 `grep -ril BatchStepJournal`로
   훑었을 때 **0건**이었다 — 적어도 이 셋에서는 결손이 통합 계획서만의 문제가 아니다.
   **나머지 11건의 실패 Job에는 이 확인을 하지 않았다.**
2. **`Proc5`와 `Proc20`은 판정 대상이 아니다.** 계획서가 없다(`raw/`만 있음). 나중에
   생성되면 판정이 다시 필요하다 — 20이라는 수는 오늘의 수다.
3. **§3-2의 경계 사례 둘.** `Batch1`과 `Proc17`은 브리프의 기준 문언대로 이행이지만, 값을
   싣는 SQL 문장은 없다. 기준을 한 칸 좁히면 실패로 넘어간다(§3-2 참조).
4. **실행 의미는 안 봤다.** 계획서가 그렇게 쓴다는 것만 확인했고, 그 SQL이 실제로 옳게
   동작하는지, 컬럼 타입(`INT`)이 모든 레거시 코드를 담을 수 있는지는 이 측정의 범위 밖이다.
   `Batch1`이 지적한 `B100`류 영숫자 코드는 이미 그 타입 한계에 걸려 있다.
5. **`ErrorMessage` 우회는 결속으로 세지 않았다.** 여러 계획서가 코드를 `ErrorMessage`
   문자열에 넣어 보존한다. 레거시 호출자가 정수 코드를 읽는다면 그것은 거처가 아니므로
   실패로 뒀다.
6. **"가는 곳" 열의 신뢰 수준은 판정보다 낮았다가, 지금은 같아졌다.** 초판의 이 열은 창
   휴리스틱 초안이었고 3건이 틀렸다(§4-2). 픽스 라운드에서 14행 전부를 원문 확인했으므로
   현재 값은 판정과 같은 근거를 갖는다. **다만 각 Job에서 운반체가 쓰이는 자리를 전수로
   센 것은 아니다** — 대표 write 문장을 확인했다. 한 계획서가 여러 테이블에 나누어 쓰는
   경우(`Proc6` 셋, `Proc14` 둘) 표에 없는 네 번째 테이블이 더 있을 가능성은 남는다.
7. **동적 SQL의 조립 대상은 표본이 아니라 패턴으로 확인했다.** `INSERT INTO ' +` 15회와
   `FROM ' +` 44회의 대상이 모두 Shadow 계열임을 확인했으나(§4-3), 119회의 `QUOTENAME`
   전부를 한 줄씩 읽지는 않았다.

---

## 7. Task 2를 위한 채점표

의무 위반(발화 대상) **14건**:

```
POQSettleProc1   POQSettleProc2   POQSettleProc3   POQSettleProc4
POQSettleProc6   POQSettleProc7   POQSettleProc8   POQSettleProc9
POQSettleProc10  POQSettleProc11  POQSettleProc12  POQSettleProc13
POQSettleProc14  POQSettleProc15
```

발화하지 않아야 할 **6건**:

```
POQSettleBatch1  POQSettlePrco20  POQSettleProc16
POQSettleProc17  POQSettleProc18  POQSettleProc19
```
