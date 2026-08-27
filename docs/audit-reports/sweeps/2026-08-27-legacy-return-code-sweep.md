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
(b) `NULL`인 이유가 명시적이고 옳다 — `Batch1` 340행은 "`LegacyReturnCode`는 `INT`이므로
영숫자 코드 `B100`, `B101`은 저장하지 않고 `NULL`로 두며, 정확한 코드는 `ErrorMessage`에
보존한다"라 쓰고, `Proc17` 7441행도 같은 취지다. 즉 `NULL` 쓰기는 레거시 코드가 **없는**
오케스트레이션 단계(S01·S02·S16·S18)의 것이다.
(c) 레거시 코드가 **있는** 단계에 대해서는 결속을 산문으로 못박는다 — `Batch1` 77행
"`@po_intRetVal`을 `batch.BatchStepJournal.LegacyReturnCode`에 기록", `Proc17` 122행
"모든 단계의 `@po_intRetVal`은 `batch.BatchStepJournal.LegacyReturnCode`에 기록한다".

**다음 사람이 재검토할 지점은 여기다.** 더 엄한 기준(값을 싣는 SQL 문장을 요구)을 쓰면
`Batch1`과 `Proc17`은 실패로 넘어가고 이행은 4건이 된다. 이 보고서는 브리프 §Step 3이 쓴
기준("그 테이블에 쓰는 `INSERT` 또는 `UPDATE` 문장이 이 컬럼을 대상으로 삼는다")을 문자
그대로 적용해 6건으로 판정했다. Task 2의 검사가 값 적재까지 요구하도록 좁힌다면 발화 집합은
14가 아니라 16이 되며, **그 경우 이 표의 "실제 코드값" 열이 그대로 채점표다.**

### 3-3. 실패 — 14건

레거시 코드를 보존은 하되(`@po_intRetVal` 16~156회), 계약 컬럼에 닿지 않는다.
**"어디로 가는가"를 함께 적는다 — 실패의 모양이 셋으로 갈린다.**

| Job | 실패 모양 | 값이 실제로 가는 곳 | 좌표 |
| :--- | :--- | :--- | :--- |
| `POQSettleProc1` | 다른 테이블 | `dbo.POQSettleSqlErrorLog` | 420·2081·2904 |
| `POQSettleProc2` | 다른 테이블 | `batch.POQBatchCheckpoint`(컬럼명 `LegacyRetVal`) | 974-989, 2199 |
| `POQSettleProc3` | C# 객체에만 | `StepResult.LegacyReturnCode` — DB 컬럼 아님 | 1354·1386·1995 |
| `POQSettleProc4` | 이름조차 없음 | `JobResult.LegacyCode`(C# 객체) | 423·447-449 |
| `POQSettleProc6` | 다른 테이블 | `dbo.POQBatchStepCheckpoint` · `dbo.POQBatchErrorLog` · `dbo.POQBatchRun` | 197·216·384 |
| `POQSettleProc7` | C# 객체에만 | `RetValWasAssigned` 플래그 — 저장 대상 없음 | 99·275·301 |
| `POQSettleProc8` | 다른 테이블 | `SETTLE_POQ_DB.dbo.BatchStepCheckpoint` | 2378-2379·2716 |
| `POQSettleProc9` | 다른 테이블 | `batch.POQSettleCheckpoint` · `batch.POQSettleChunkKey` | 103·271·305 |
| `POQSettleProc10` | 다른 테이블(그것도 여덟 갈래 표기) | `dbo.POQSettleStepRun` · `batch.POQSettleStepRun` · `[batch].[POQSettleStepRun]` · `poqbatch.POQSettleStepRun` · `poqbatch.StepRun` · `POQBatch.SettleStepRun` · `POQSettleBatch.POQSettleStepRun` · `SETTLE_POQ_DB.POQBatch.POQSettleStepRun` | 192·563·590 |
| `POQSettleProc11` | 이름조차 없음 | `LegacyIntRetVal`(C# 객체) | 74·85-87 |
| `POQSettleProc12` | **컬럼명은 맞고 테이블이 틀림** | `batch.BatchTaskRun.LegacyReturnCode` — 620-631행에서 `CREATE TABLE`로 새로 만든다 | 618-632, 1198 |
| `POQSettleProc13` | 이름조차 없음 | `LegacyPoIntRetVal`(C# 객체) | 64·72 |
| `POQSettleProc14` | 다른 테이블 | `batch.BatchRunStep` · `batch.BatchExecutionJournal`(`LegacyRetVal = @po_intRetVal` 1757) | 78·1757·1814·1821 |
| `POQSettleProc15` | C# 객체에만 | `context.LegacyReturnCode` / 결과 모델 — `batch.BatchStepJournal` 쓰기 문장 35개 어디에도 이 컬럼이 없다 | 1043·1065·1067-1068·3614·4114 |

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

**전수 자동 판정은 하지 않았다. C# 프로브도 만들지 않았다.** 실제로 한 일은 이렇다.

- **기계로 닫은 부분(결정적):** §3-1의 `grep -ric "stepjournal"`. 계약 테이블을 이름으로
  부르지 않는 계획서는 그 테이블에 쓰는 문장을 가질 수 없으므로, 13건 제외는 정규식만으로
  안전하다. 대소문자를 무시했고 스키마 접두사도 요구하지 않았다.
- **기계로 좁힌 부분(보조):** 남은 7건에 대해
  `grep -niE -A14 "(INSERT +INTO|UPDATE) +batch\.BatchStepJournal" | grep -i LegacyR`로
  후보 줄을 뽑았다. 그리고 `awk`로 "각 `LegacyReturnCode`/`LegacyRetVal` 줄이 어느
  `INSERT`/`UPDATE` 문장 20줄 안에 있는가"를 20건 전체에 대해 집계해 §3-3의 "가는 곳" 열을
  만들었다.
- **사람이 판정한 부분:** 위 두 도구는 셋 중 어느 것도 판정하지 못한다.
  (a) `INSERT`의 컬럼 목록과 `VALUES` 절이 떨어져 있어 **무슨 값이 실리는지**는 창(window)
  정규식으로 안 잡힌다. (b) `@v_currentStepId`처럼 **이름이 역할을 배신하는** 변수는
  대입 지점을 읽어야 판정된다. (c) `NULL` 쓰기가 결손인지 정당한지는 그 단계에 레거시
  코드가 있는지를 읽어야 안다. 그래서 7건은 원문 블록을 직접 읽어 판정했고, 실패 14건도
  운반체 좌표를 각각 열어 확인했다.

**요약: 실패 집합의 13/14는 정규식으로 결정적으로 닫혔고, 나머지 1건(`Proc15`)과 이행 6건은
사람이 원문을 읽어 판정했다.** 20건은 사람이 읽을 수 있는 수였고, 좁은 정규식 하나로
전수를 대신하지 않았다.

`tests/ReSet.Core.Tests/TempBindingProbe.cs`는 만들지 않았다. 위 기계 선별이 grep/awk로
같은 일을 하고 사람 판정이 나머지를 덮으므로, C# 프로브를 억지로 만들어 그 통과를
"다 잡았다"로 읽을 여지를 두지 않았다.

---

## 5. 설계서 §5-1의 여섯이 왜 틀렸는가

설계서(`docs/superpowers/specs/2026-08-27-sql-placement-criterion-design.md`) §5-1은 기대
집합으로 여섯을 지목했다 — `POQSettleProc1`·`4`·`7`·`10`·`11`·`13`. 그 여섯은 **문자열
`LegacyReturnCode`가 0회인 계획서 목록**이다(§1 표에서 그대로 확인된다). 즉 잰 것은
의무 이행이 아니라 **철자였다.**

그 결과 두 가지가 동시에 틀렸다. 첫째, 지목된 여섯은 실제로도 실패지만 **이유가 다르다** —
"그 이름을 안 쓴다"가 아니라 "코드를 `JobResult.LegacyCode`·`LegacyIntRetVal`·
`LegacyPoIntRetVal`·`dbo.POQSettleSqlErrorLog`·여덟 갈래 `POQSettleStepRun`으로 흘려보내
계약 컬럼에 닿지 않는다"가 이유다. 둘째, 그리고 더 나쁘게, **나머지 14건을 이행으로
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
