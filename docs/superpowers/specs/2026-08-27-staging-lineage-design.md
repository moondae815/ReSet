# 단계 내부 스테이징 계보 — 설계

**날짜**: 2026-08-27
**대상 결함**: `docs/known-defects.md` (5-3-3) 부류 3(9건)·부류 5(6건)
**로드맵 위치**: 축 B 원인 넷 중 (b)·(c). 로드맵 4(캐시 16 → 17)의 선결 조건.

## 1. 문제

이행이 원본 한 문장을 둘로 쪼갠다 — 「원천 → 스테이징 적재」와 「스테이징 → 대상
게시」. **업무 술어는 앞 문장에 남고, 코드 앵커는 뒤 문장에 붙는다.**

```
POQSettleProc2/S13
  112  INSERT INTO batch_shadow.S13_TSettleByTX_After   ← 술어 (WHERE M.YMD = @pi_strYMD …)
  385  INSERT INTO SETTLE_POQ_DB.dbo.TSettleByTX        ← 앵커 -5
  399    FROM batch_shadow.S13_TSettleByTX_After

POQSettleProc8/S06
   79  INSERT INTO stage.S06CancelSettle                ← 술어·조인 키, 앵커는 NULL이라 환산 불가
  217  INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst         ← 앵커 -1
  241    FROM stage.S06CancelSettle

POQSettleProc1/S02
  325  INSERT INTO dbo.__poq_S02_..._candidate          ← 조인 키 PGName
  468  INSERT INTO SETTLE_POQ_DB.dbo.TSettleMst         ← 앵커 -2
  488    FROM dbo.__poq_S02_..._candidate
```

검사가 서수를 쥔 게시문을 명세서 행과 맞대므로 두 방향으로 틀린다.

| | 증상 | 검사 | 건수 |
|---|---|---|---:|
| 부류 3 | 게시문에 술어가 없다 → 「명세서가 확정한 컬럼이 없어졌다」 | B | 9 |
| 부류 5 | 게시문의 `WHERE ExecutionId = …`이 원본에 없다 → 「술어를 더했다」 | C | 6 |

**둘은 한 사실에서 나온다**: 이 게시문의 `FROM`이 원본 원천이 아니라 이 단계가
앞서 만든 테이블이다.

부류 3 안에도 앵커가 어긋나는 방식이 셋이다 — 앵커가 적재문에 아예 없는 것
(Proc2/S13), 적재문의 앵커가 `SET @v_currentStepId = NULL;`이라 서수로 환산되지
않는 것(Proc8/S06), `ReadCodeAnchor`의 구간 규칙에 걸려 앵커가 **소실**되는 것
(Proc9/S03). 셋 다 계보로 닫힌다 — 앵커를 고치는 것이 아니라 **술어의 출처를
넓히는** 것이므로 앵커가 어떻게 어긋났는지와 무관하다.

## 2. 판정 기준

> **이 문장의 행 원천이 하나 이상이고 전부 「같은 단계의 앞선 문장이 쓴 테이블」이며,
> 그 테이블이 명세서 DML 범위 표의 대상 테이블이 아니면, 그 원천은 단계 내부
> 스테이징이다.**

이름 규칙을 쓰지 않는다. 실물이 `batch_shadow.`·`stage.`·`batch_work.`·
`dbo.__poq_…`로 제각각이고, 이름 목록으로 닫으면 다음 이행자가 고를 다섯 번째
이름에서 재발한다. 판정은 **역할** 두 개다 — 「이 단계가 만들었는가」와 「원본이
쓰는 테이블인가」.

### 2-1. 명세서 대상 제외가 필요한 이유 — 실측

「앞선 문장이 썼는가」만으로 판정하면(초안 접근 A) 오분류가 대량으로 난다. DML
문맥으로 좁힌 코퍼스 탐침에서 「행 원천이 전부 앞선 쓰기 대상」인 문장이 **118건**
이고, 원천 52종 중 최다가 **`tsettlemst` 52건 — 원본 SP의 대상 테이블 그 자체**다.

```
52  tsettlemst                       ← 원본 대상. 스테이징이 아니다
 5  tsettlemst__runid__s23           \
 5  tsettlemst_work_                  |  이행이 발명한 것 — 여기까지가 노림
 4  __poq_s07_..._candidate          /
```

`DELETE FROM TSettleMst` → `INSERT INTO TSettleMst` → 뒤에서
`UPDATE A … FROM TSettleMst AS A` 하는 재게시 관용구가 흔하고, 접근 A는 그
`UPDATE`를 게시문으로 오분류해 **검사 C를 통째로 끄고 술어까지 상속시킨다.**
거짓 음성 대량 생산이다.

명세서 대상 제외로 `tsettlemst`가 빠지고, 이행이 발명한 테이블만 남는다.

> 이 탐침은 정규식 근사다. 검증용 `SELECT`를 세지 않도록 DML 문맥으로 좁혔고
> 두 자리(`POQSettleBatch1/S06`·`POQSettleProc1/S02`)를 직접 열어 확인했으나,
> 실제 발화와 정확히 같은 수는 아니다. **A가 안전하지 않다는 결론을 뒤집을
> 방향의 오차가 아니라는 것까지가 이 측정이 말하는 것이다.**

### 2-2. 탐지력을 먹지 않는다 — 실측

이 코퍼스가 찾아낸 **유일한 진짜 축 B 결함**((5-3-4), `POQSettleProc17/S06`이
원본에 없는 수수료율 속성 필터 넷을 새로 건다)의 문장은
`FROM PaymentDB.dbo.TTxMst AS A` — **원본 원천을 직접 읽는다.** 계보에 스테이징이
없으므로 면제 대상이 아니고, 이 변경 뒤에도 그대로 발화한다. 회귀 테스트로 못을
박는다.

## 3. 자료와 배선

### 3-1. 리더 (`StepSqlStatementReader`)

펜스를 전부 읽은 뒤 **후처리 한 번**으로 계보를 계산한다. 리더는 이미 단계 전체를
누적하고 문장마다 시작 오프셋을 갖고 있다.

```
1. 쓰기 목록   : DML 문장의 (시작 오프셋, 대상 테이블)
2. 행 원천     : FROM·JOIN의 이름 테이블. CTE 이름은 제외한다(테이블이 아니다)
3. 짝짓기     : 원천이 하나 이상이고 전부 「더 앞선 오프셋의 쓰기 대상」이면 후보
4. 상속       : 그 쓰기 문장의 Pred ∪ Join ∪ Subordinate 를 원천 테이블별로 매단다
```

`StepSqlStatement`에 필드 하나를 더한다.

```csharp
/// <summary>원천 테이블 → 그 테이블에 쓴 앞선 문장이 가진 컬럼.</summary>
public IReadOnlyList<(string SourceTable, IReadOnlyList<string> Columns)> LineageSources
```

**리더가 명세서 대상 제외를 하지 않는다** — 명세서를 못 보기 때문이다. 리더는
원시 계보만 낸다.

**불변식 (검사 쪽이 이것에 의존한다)**: `LineageSources`는 **행 원천이 전부 앞선
쓰기 대상일 때만** 채워진다. 하나라도 앞서 쓰인 적 없는 테이블이면 빈 목록이다.
이 불변식이 없으면 검사 쪽의 `All(…)`이 **부분집합 위에서 공허하게 참**이 된다 —
`stage.X`(앞서 씀)와 `dbo.TReal`(안 씀)을 함께 읽는 문장이 스테이징만 읽는
것으로 판정된다. 리더 테스트가 이 불변식을 직접 잰다.

테이블 이름은 `ResolveTargetTable`과 같은 규약으로 정규화한다(마지막 식별자).
검사 쪽이 명세서 행의 `TargetTable`과 직접 비교하므로 두 쪽 규약이 같아야 한다.

### 3-2. 검사 (`MechanicalValidator`)

명세서 대상 집합을 빼고 남은 것만 스테이징으로 인정한다. `rows`는 두 검사가 이미
갖고 있다(`facts.SelectMany(f => f.DmlRows)`).

```csharp
// 두 검사 공통 - 명세서 대상이 아닌 계보 원천만 남긴다
var specTargets = new HashSet<string>(
    rows.Select(r => r.TargetTable), StringComparer.OrdinalIgnoreCase);

static bool ReadsOnlyStaging(StepSqlStatement s, HashSet<string> specTargets) =>
    s.LineageSources.Count > 0
    && s.LineageSources.All(l => !specTargets.Contains(l.SourceTable));
```

**검사 B** (`CheckAnchoredStatementFacts`, `MechanicalValidator.cs:7106` 부근) —
계보 컬럼을 `relocated`에 합류시킨다.

```csharp
var relocated = new HashSet<string>(
    group.SelectMany(a => a.Statement.SubordinatePredicateColumns
        .Concat(a.Statement.LineageSources
            .Where(l => !specTargets.Contains(l.SourceTable))
            .SelectMany(l => l.Columns))),
    StringComparer.OrdinalIgnoreCase);
```

`relocated`에 넣는 것이 의미상 정확하다 — 「없어진 것이 아니라 **이 문장을 먹인
문장으로** 옮겨갔다」이고, 하위 범위 이전과 같은 개념의 한 층 위다.
**검사를 끄지 않는다**: 적재문에도 그 컬럼이 없으면 여전히 발화한다.

**검사 C** (`CheckAnchoredStatementExtras`, `MechanicalValidator.cs:7313` 부근) —
스테이징만 읽는 문장은 초과를 내지 않는다.

```csharp
var extras = group
    .SelectMany(a => ReadsOnlyStaging(a.Statement, specTargets)
        ? Array.Empty<string>() : a.Statement.PredicateColumns)
    …
```

게시문이 스테이징에 거는 술어는 **원본 원천의 술어가 아니므로** 명세서와 대조할
대상 자체가 아니다. 지금은 `BatchControlContract.Tables`의 컬럼 이름을 `allowed`로
깔아 이 부류를 면제하려 했는데, 면제가 역할이 아니라 **이름**으로 걸려 있어
계약이 아는 `RunId`만 통과하고 `ExecutionId`·`ProcessingYMD`는 발화했다. 이 변경이
그 면제를 역할로 옮긴다. `allowed`는 그대로 둔다 — 배치 제어 테이블을 **직접**
갱신하는 문장은 계보와 무관하게 여전히 그 면제가 필요하다.

## 4. 결정과 한계

**한 홉만 따라간다.** `A → S1`, `S1을 읽어 S2를 씀`, `S2를 읽는 게시문` 같은
사슬은 추적하지 않는다. 실물 셋 다 한 홉이고, 사슬 미추적은 **오탐 방향**이라
안전하다. (5-3-3) 부류 2의 별칭 사슬과 같은 결정이다.

**「전부」를 요구한다.** 행 원천 중 하나라도 스테이징이 아니면 게시문으로 보지
않는다. 스테이징과 실물 조회 테이블을 조인하는 게시문은 이 코퍼스에 없고,
느슨하게 잡을수록 침묵이 는다.

**쓰기는 DML 대상 전부로 센다** — INSERT뿐 아니라 UPDATE·DELETE 대상도 「이 단계가
건드린 테이블」이다. 다만 명세서 대상 제외가 그 위에 걸리므로, 원본이 쓰는
테이블은 어느 종류로 썼든 스테이징이 되지 않는다.

**앞선다는 것은 오프셋 순서다.** 제어 흐름(`IF`·`WHILE`)은 보지 않는다. 조건부로만
실행되는 적재문의 술어도 상속되는데, 이는 오탐이 아니라 **침묵 방향**이므로
한계로 기록한다. 코퍼스 실측은 검증 단계에서 한다.

## 5. 검증

**통제 대조 스윕.** `c1842ca` 기준 워크트리에 코퍼스 심링크 둘을 걸고 스윕을 돌려
전문 `diff`한다. 커밋된 보고서를 기준선으로 쓰지 않는다.

```
기대        59 → 44   (검사 B 34 → 25, 검사 C 25 → 19)
필수 확인    사라지는 15건이 부류 3·5의 좌표 그대로일 것
            새로 생기는 것 0
            미분류·검사 A·D·E·「실행 조건」 절 문자 동일 (분모 불변)
```

**게시문 분류 목록을 눈으로 본다.** 스윕과 별도로, 코퍼스 전수에서 게시문으로
분류된 문장과 그 원천 테이블을 목록으로 뽑아 **진짜 업무 테이블이 한 건도 없는지**
직접 확인한다. §2-1의 118건이 어디까지 줄었는지가 이 설계의 실측 근거가 된다.

**회귀.** (5-3-4) 🔴이 계속 발화하는지 테스트로 못을 박는다.

**변이는 판정 단위로 넣는다.** 조건 하나당이 아니라 조건 안의 판정 하나당이다.

```
앞선 오프셋 요구 / 「전부」 요구 / CTE 이름 제외 / 명세서 대상 제외 /
한 홉 / relocated 합류 / 검사 C 면제 / 이름 정규화 규약
```

각각을 되돌리는 변이가 **정확히 하나의 테스트를 죽여야** 한다. 죽지 않으면 그
결정은 검증되지 않고 있는 것이다 — (5-3-3) 부류 2에서 세 회차 연속으로 변이가
코드가 아니라 **테스트의** 결함을 먼저 잡았다.

## 6. 이 변경의 위험축

`SubordinatePredicateColumns`와 같다 — **거짓 음성**이다. 계보 컬럼은
`MechanicalValidator`에서 `relocated`가 되어 **검사를 침묵시킨다.** 넓히는 모든
결정은 「무엇이 조용해지는가」를 먼저 물어야 한다. 부류 2에서 독립 리뷰 두 번이
각각 이 축에 구멍을 찾았고(별칭 사상이 문장 전역, 스코프 안의 동명 실컬럼) 둘 다
코퍼스 실발생은 0이었다 — **스윕으로는 보이지 않는 종류다.**

## 7. 닿는 파일

| 파일 | 무엇 |
|---|---|
| `src/ReSet.Core/Services/StepSqlStatementReader.cs` | 계보 후처리, `LineageSources` |
| `src/ReSet.Core/Services/MechanicalValidator.cs` | 검사 B의 `relocated`, 검사 C의 `extras` |
| `tests/ReSet.Core.Tests/StepSqlStatementReaderTests.cs` | 계보 계산 |
| `tests/ReSet.Core.Tests/MechanicalValidatorBatchStepTests.cs` | 두 검사의 적용·회귀 |
| `docs/known-defects.md` | 부류 3·5 해소 기록 |
| `docs/audit-reports/sweeps/` | 대조 스윕 보고서 |

## 8. 조율

`reset-38`이 같은 시기에 `MechanicalValidator.cs`를 편집한다(새 함수
`CheckLegacyReturnCodeBinding`과 `ValidateBatchStep` 호출부 ~279–420행). 이 설계가
만지는 ~7106·~7313과 **행 구간이 떨어져 있어 병행 가능**하다는 것을 양쪽이
확인했다. 어느 쪽이 먼저 `main`에 들어가든 나머지가 리베이스한다.
