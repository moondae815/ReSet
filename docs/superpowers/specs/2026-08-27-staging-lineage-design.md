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

### 2-1. 명세서 대상 제외가 필요한 이유 — 실측으로 정정 (2026-08-27)

> **이 절의 최초 근거는 부분적으로 틀렸다.** 아래는 구현 뒤 실측으로 다시 잡은
> 내용이다. 원래 있던 근사 탐침(118건 · `tsettlemst` 52건)은 「행 원천이 전부
> 앞선 쓰기 대상인 문장이 많다」는 규모 자체는 여전히 유효하지만, 그 방증으로
> 들었던 **재게시 관용구는 이 제외가 막는 것이 아니다.**
>
> `DELETE FROM T` → `INSERT INTO T` → 뒤에서 `UPDATE A … FROM T AS A` 하는
> 관용구는 **Task 1의 자기참조 가드**(`CollectRowSourceTables`의 `selfTarget`
> 제외)가 이미 막는다 — `UPDATE`가 대상 `T`를 FROM 별칭으로 다시 참조하는
> 문장은 애초에 자기 자신을 행 원천으로 세지 않으므로, 명세서 대상 제외가
> 없어도 이 관용구는 게시문으로 오분류되지 않는다. (계획서 원안 테스트
> `ValidateBatchStep_CheckB_SpecTargetSourceIsNotStaging`을 리뷰가 변이로
> 확인했다 — `specTargets` 필터를 제거하는 변이를 넣어도 이 테스트는 죽지
> 않았다. 그 테스트가 실제로 막는 것은 아래 다른 문제였다.)

`specTargets`(명세서 대상 제외)가 실제로 막는 것은 **스키마가 다른 동명
테이블의 베이스 이름 충돌**이다. 정규화가 "마지막 식별자만" 쓰므로, 스키마가
달라도 이름이 같으면 같은 물리 테이블로 오인한다. 실측 좌표:

```
POQSettleProc8/S08:109   INSERT INTO SETTLE_POQ_DB.shadow.TSettleMst   → 쓰기 등록 "TSettleMst"
POQSettleProc8/S08:130     FROM SETTLE_POQ_DB.dbo.TSettleMst           → 원천 "TSettleMst"  ← 충돌
POQSettleProc3/S06        INSERT -> BatchArtifactWarning <- TSettleMst[YMD,InState,CollectFlag,PGName,MallID,CollectPeriodID]
```

`shadow.TSettleMst`(단계가 만든 Before-Image 섀도)와 `dbo.TSettleMst`(원본
대상 그 자체)는 서로 다른 물리 테이블인데 이름만 같다. 명세서 대상 제외가
없으면 `dbo.TSettleMst`를 원천으로 읽는 문장(위 `:130`, `POQSettleProc3/S06`)이
스테이징으로 오분류되어 검사가 조용해진다.

**두 방어선은 서로 다른 것을 막는다 — 하나를 지워도 다른 하나가 대신 막아
주지 않는다.**

| 방어선 | 막는 것 | 실측 근거 |
|---|---|---|
| 자기참조 가드(`selfTarget` 제외, Task 1) | 문장이 **자기 자신의 쓰기 대상**을 FROM 별칭으로 되읽는 것(재게시 관용구) | 변이가 `specTargets` 제거에도 안 죽는다는 것으로 반증 |
| 명세서 대상 제외(`specTargets`, Task 2·3) | **스키마가 다른 동명 테이블**을 이름만 보고 같은 테이블로 오인하는 것 | `POQSettleProc8/S08:109·130`, `POQSettleProc3/S06` |

> **코퍼스 전수 확인 (2026-08-27, `StepSqlStatementReader.Read`를 직접
> 호출하는 임시 프로브, 커밋하지 않음).** 전체 2581문장 중 142문장이 비지
> 않은 `LineageSources`를 가졌고, 원천 113종 중 `dbo.TSettleMst`가 실제 원본
> 대상으로서 원천에 섞인 사례는 위 `POQSettleProc3/S06` 1건이었다(그 통계
> 목록 자체는 리더가 명세서를 보지 않으므로 미제외 상태의 원시 집계다 -
> `MechanicalValidator`의 `specTargets` 적용은 이 목록과 별도로 검증됐다).
> 이 프로브가 §2-1의 원래 규모 주장(118건 · 최다 원천 tsettlemst)을 그대로
> 재현하지는 않는다 — 원래 탐침은 정규식 근사였고 이 프로브는 실제 리더
> 로직이라 방법이 다르다. **A(앞선 쓰기 대상만으로 판정)가 안전하지 않다는
> 결론 자체는 바뀌지 않는다** — 다만 그 위험의 정체가 "재게시 관용구"가
> 아니라 "동명 스키마 충돌"이라는 것이 이번 실측이 정정한 부분이다.

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

**이름 정규화(마지막 식별자만)는 이행이 발명한 스키마끼리도 충돌한다 — 실측
(2026-08-27, 검증 단계).** §2-1의 명세서 대상 충돌과 별개로, 이 코퍼스에
**이행 자신의 스테이징 스키마 둘**이 같은 베이스 이름을 쓰는 사례가 실재한다:
`POQSettleProc9/S13`의 `batch_shadow.TPartialCancelByTX_Run_S13`(Before-Image
섀도, 조인 키 `YMD` 포함)과 `batch_work.TPartialCancelByTX_Run_S13`(위임된
`EXEC`만 채우는, 코퍼스에 본문이 없는 실제 스테이징)이 같은 이름
`TPartialCancelByTX_Run_S13`으로 정규화돼 충돌한다. 그 결과 게시문이 섀도
문장의 `YMD`를 (근거 없이) 계보로 물려받는다 — `TSettleByIN`·`TSettleByOUT`도
같은 구조다. **고치지 않는다** — 이 셋은 (5-3-3) 부류 4(판정 불가, 위임된
`EXEC` 본문이 코퍼스에 없음)이고, 방향이 침묵(거짓 음성)이라 이 코퍼스에서
관측 가능한 검사 결함으로 이어지지 않는다. 다음 회차 재료: 정규화가 스키마까지
같이 보게 좁히면(§2-1의 명세서 대상 제외와 같은 방식) 이 충돌도 함께 닫힌다.
상세는 `docs/known-defects.md` (5-3-3) 부류 4의 "부수 효과" 문단.

## 5. 검증

**통제 대조 스윕.** `git merge-base HEAD main`으로 잡은 커밋(이 브랜치를 딴
지점 — 2026-08-27 실측 시점엔 `fe89b5d`, 그 사이 병합된 피어 변경을 포함한다)
기준 워크트리에 코퍼스 심링크 둘을 걸고 스윕을 돌려 전문 `diff`한다. 커밋된
보고서를 기준선으로 쓰지 않는다 — 그 사이 병합된 남의 커밋이 새 검사를 넣으면
거짓 경보가 난다.

```
실측 (2026-08-27, fe89b5d → 883df24)   59 → 46   (검사 B 34 → 26, 검사 C 25 → 20)
```

이 절의 최초 「기대 59 → 44」는 사전 추정이었고 실측과 다르다 — **정정한
가설은 최대 59 → 46**이다(`docs/known-defects.md` (5-3-3) 부류 3·5 "해소"
문단 참고). 사라진 16건 중 13건이 부류 3·5의 좌표(부류 3의 8/9건, 부류 5의
5/6건)이고, 남은 3건은 부류 3·5가 아니라 부류 4(`POQSettleProc9/S13`)의
부수 효과(위 "이름 정규화는 이행이 발명한 스키마끼리도 충돌한다" 문단)다.
새로 생긴 발화가 0이 아니었던 것 자체가 실측 대상이었다 — 그 원인을 짚어
남겼다. `POQSettleProc8/S05`(부류 3의 `PGName`, 부류 5의 `ProcessingYMD`)
좌표 둘은 스테이징 적재문이 ` ```text ` 의사코드 펜스 안에 있어 리더가 못
읽으므로 계보가 붙지 않는다 — 닫히지 않은 채 남는 것이 정상이다.

**게시문 분류 목록을 눈으로 본다.** 스윕과 별도로, 코퍼스 전수에서 게시문으로
분류된 문장과 그 원천 테이블을 목록으로 뽑아 **진짜 업무 테이블이 한 건도 없는지**
직접 확인한다. 2026-08-27 실측(임시 프로브, 커밋하지 않음): 전체 2581문장 중
142문장에 `LineageSources`가 붙었고 원천 113종 중 원본 대상 테이블이 원천으로
섞인 사례는 `POQSettleProc3/S06`(`TSettleMst`) 1건 — 그 문장 자체는
`specTargets` 제외 대상이라 스테이징으로 인정되지 않는다. §2-1의 근사
탐침(118건 · `tsettlemst` 52건)과 이 프로브는 방법이 달라(정규식 근사 vs
실제 리더 로직) 수가 다르다 — 「A가 안전하지 않다」는 결론 자체는
바뀌지 않는다.

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
