# 재료 폐포 설계 — 참조 프로시저의 명세를 Job 재료에 넣는다 (2026-08-30)

**한 줄**: 사람이 고르는 것은 **진입점**이고, 재료의 **폐포**는 도구가 닫는다.

Job의 SP 목록은 사람이 TUI에서 고른다(`Program.cs:1474`의 `selectedFiles`).
그 선택이 부르는 **프로시저 타입 참조 객체**의 명세는 지금 프롬프트에도, L1 재료에도
실리지 않는다. 이 설계는 그 하나를 닫는다.

선행: `docs/audit-reports/sweeps/2026-08-29-critic-exception-axis.md` §5·§9 ·
`docs/audit-reports/sweeps/2026-08-29-stage4-pair-batch4.md` §4

## 0. 측정 조건

- 대상 `output/` 코퍼스(프로시저 14 · 함수 10 · 외부 함수 7)와
  `output.bak-stage4-control-20260828/Jobs/POQSettleBatch4`.
- 폐포는 각 SP의 `raw/dependency-manifest.json`의 `Nodes`로 계산했다.
  키 형식은 `<DB>.<스키마>.<이름>.<타입>`이고 타입은 `Procedure`·`Function`·… 이다.
- 판을 새로 돌리지 않았다. 전부 판독이다.

## 1. 무엇이 빠지는가 — 실측

`POQSettleBatch4`의 프롬프트(`raw/prompt-context.md`, 491,418바이트)에는
`Filename:` 항목이 **12개**다. 선택된 12편의 명세 전문이 그것이다.

참조 객체의 **링크는 실린다**(`:4209~4210`).

```
- [dbo.UP\_Util\_Settle\_Summary\_AcqManual](../../dbo.UP_Util_Settle_Summary_AcqManual/docs/Spec.md)
- [dbo.UP\_UTIL\_SETTLE\_SUMMARY\_EXTRA](../../dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA/docs/Spec.md)
```

**내용은 실리지 않는다.** 하위 명세에만 있는 문장별 코드로 갈랐다.

```
              프롬프트   EXTRA 명세
  4001            0          5
  4002~4007       0        각 3
```

프롬프트에 있는 `4008` 하나는 **부모 명세의 요약 문장**에서 온 것이다
(`dbo.UP_Util_Settle_Summary/docs/Spec.md:267` — 「오류 시 코드 `4000`부터 `4008`을
출력 파라미터에 설정합니다」). 링크는 상대 경로 텍스트로 흘러갈 뿐이고, 평평해진
프롬프트에 그 경로의 기준점이 없으며, 파일을 열라는 지시도 없다.

## 2. 함수와 프로시저는 대칭이 아니다

폐포 결손을 타입으로 가르면 갈린다.

| 타입 | 건수 | 판정 |
|---|--:|---|
| `Function` | 30 | **결손이 아니다.** 부모 명세의 「참조 함수 표」가 호출 지점·라인·**호출식 전문**을 담는다(`prompt-context.md:826`). 계획서는 함수를 재구현하지 않고 그 식을 보존하면 된다 |
| `Procedure` | **2** | **진짜 결손.** `UP_UTIL_SETTLE_SUMMARY_EXTRA` · `UP_Util_Settle_Summary_AcqManual` |

부모가 하위 프로시저에 대해 담는 것은 산문 셋뿐이다 — 요약(`:265`·`:267`) ·
호출 순서(`:286`·`:287`) · mermaid 노드. **DML 상세도, 문장별 오류 코드도,
대상 테이블도 없다.**

| | 부모가 주는 것 | 하위 명세가 가진 것 |
|---|---|---|
| `SUMMARY_EXTRA` | 요약 한 문장 | 28,174B · **기계 확정 표 6개** |
| `AcqManual` | 요약 한 문장 | 11,965B · **기계 확정 표 5개** |

그런데 **규칙 3-1이 신규 저장 프로시저를 금지**하므로 그 일을 단계가 인라인해야 한다.
`POQSettleBatch4`가 실제로 `S12`·`S13`으로 그렇게 했다.

## 3. 재료가 없는 게 아니라 틀린다

`S12`·`S13`의 `LegacyProcedures`가 **부모**를 가리킨다.

```
S11  legacy=dbo.UP_Util_Settle_Summary   ErrorCodes=['-1','-2','-3','-4','0']
S12  legacy=dbo.UP_Util_Settle_Summary   ErrorCodes=['-1','-2','-3','-4','0']
S13  legacy=dbo.UP_Util_Settle_Summary   ErrorCodes=['-1','-2','-3','-4','0']
```

그래서 `CheckStatementCountAgainstSpec`·`CheckAnchoredStatementFacts`·
`CheckMissingConditionColumns`, 그리고 `CheckLegacyStepErrorCodeInvention`이
**부모의 8개 DML과 부모의 코드 집합**으로 그 두 단계를 판정한다.

판독 §9가 「이 검사가 못 보는 것」으로 적은 자리가 이것이다 — `S12`·`S13`의
`-1~-4` 발명이 **부모의 허용 집합 안이라 통과한다.**

> **다만 이번 결함의 원인을 재료 부재로 돌리지 말 것.** 부모의 요약 문장이 계약을
> 이미 담았고 **Critic이 바로 그 문장을 근거로** 재매핑을 잡아냈다. 재료가 없어서
> 어긴 것이 아니라 손에 쥔 문장을 어긴 것이다(판독 §5-1의 「산문으로 옳게 쓰고
> 의사코드에서 어긴다」와 같은 형태). 이 설계가 고치는 것은 **L1 귀속**과
> **Actor가 쥔 정밀도**이지, 그 실패 양식 자체가 아니다.

## 4. 결정 — 재료 폐포를 도구가 넓힌다

사람이 고르는 `selectedFiles`의 의미를 **바꾸지 않는다.** 그것은 계속 진입점이다.

```
selectedFiles (사람)   12편   진입점. TUI 패널의 「실행 순서」 그대로
      ↓ 매니페스트 폐포
specs / spDefs (도구)  14편   재료
```

### 4-1. 검토하다 무너진 대안

**「재료만 넓히고 프롬프트는 그대로」는 성립하지 않는다.** 프롬프트가
*"[Source Procedures — use these names verbatim in `LegacyProcedures`]"*(`AiService.cs:4067`)로
로스터를 못박으므로 **로스터에 없는 이름은 목차가 쓸 수 없다** → 귀속이 안 붙는다.
이름만 로스터에 넣고 내용을 빼면 **Actor가 재료 없이 DML을 지어낸다** — 더 나쁘다.
**이름과 재료는 함께 가야 한다.**

**「TUI가 물어본다」도 채택하지 않았다.** 도구가 조용히 안 늘리는 것이 장점이지만,
이 축의 요점(참조 객체는 진입점이 아니다)과 어긋나고 사람이 같은 판단을 매번 반복하게 된다.

## 5. 폐포 계산

각 선택 SP의 `raw/dependency-manifest.json`에서 `Nodes`를 읽어, 아래 셋을 모두
만족하는 것을 더한다. **고정점까지 반복**하되 visited로 순환을 끊는다.

1. 키의 타입 접미사가 `Procedure`
2. `SpecPath`가 있고 그 파일이 **실제로 존재**한다
3. 아직 목록에 없다

`Summary → EXTRA → Summary`가 실제로 순환이므로 visited가 없으면 끝나지 않는다.

**함수는 더하지 않는다**(§2).

**상한**: 폐포가 진입점 수의 2배를 넘으면 더 넓히지 않고 경고한다.
`BatchStepPlanParser.MaxSteps = 40`이 이미 쓰는 폭주 방어와 같은 관용이다.

**실측 결과**: `POQSettleBatch4` 로스터 12 → 폐포 **14**. 더해지는 것은 정확히
`dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA`와 `dbo.UP_Util_Settle_Summary_AcqManual` 둘이다.

## 6. 순서 — 참조자 바로 뒤

`BatchStepCatalog.LoadDefinitionsAsync`의 계약이 순서를 하중으로 쓴다 —
*「입력 순서가 곧 배치 스텝 실행 순서이므로 순서를 흐트러뜨리면 안 된다」*.

더해진 SP는 **자기를 부른 SP 바로 뒤**에 넣는다. 하위 프로시저는 부모 흐름 **안에서**
실행되므로 그것이 실제 의미와 맞고, `POQSettleBatch4`가 스스로 `S11 → S12 → S13`으로
그렇게 배치했다. 끝에 붙이면 실행 순서가 틀린다.

같은 SP를 둘 이상이 부르면 **처음 부른 자 바로 뒤**에 한 번만 넣는다.

## 7. 배선 — 계산은 한 곳, 주입은 두 곳

### 7-1. 재료는 둘로 갈린다

넓혀야 할 것이 `specs` 하나가 아니다. **명세에서 오는 것과 정의에서 오는 것**이 다르다.

| 재료 | 출처 | `specs` 확장으로 따라오나 |
|---|---|:--:|
| 프롬프트 내용 (`specsCopy` `:1855`) | `specs` | ✔ |
| `sourceProcedureRoster` (`:1814`) | `specs` | ✔ |
| `codesByProcedure` (`SpecReturnCodeExtractor` `:1809`) | `specs` | ✔ |
| `statementFactsByProcedure` (`:3347`, `FeedbackSpec.OnlyProcedureSpecs(specs)`) | `specs` | ✔ |
| **`tablesByProcedure`** (`SpecTargetTableExtractor.Extract(definitions)`) | **`SpDefinition`** | ✘ |

그래서 `specs`와 `definitions`를 **함께** 넓힌다. `definitions`를 빼면
`PlanStructureEnricher.RewriteTables`가 그 단계의 `TargetTables`를 못 고치고,
「재료를 잃은 검사가 조용해진다」의 새 사례가 하나 는다.

### 7-2. 진입점이 둘이다

`RunConsolidatedPipelineAsync(specs, …, definitions: spDefs, …)`를 부르는 자리가 둘이다.

```
Program.cs:972    CLI 배치 모드(--job-name). 분석 흐름이 곧바로 Job을 만든다
Program.cs:1531   TUI 흐름. selectedFiles 로 specsData(:1483)·spDefs(:1515)를 짓는다
```

**계산은 한 곳**(`BatchStepCatalog`의 새 정적 메서드)에 두고, **주입은 두 호출부**에서
한다. Core 안쪽(`RunConsolidatedPipelineAsync`)에서 한 번에 넓히는 쪽이 더 짧아 보이지만
`SpDefinition` 적재기(`BatchStepCatalog.LoadDefinitionsAsync`)가 `src/ReSet.Cli/`에 있어
Core가 부를 수 없다 — 그것을 Core로 옮기는 것은 이 회차의 범위를 넘는다.

**비용**: 프롬프트 491KB → 약 531KB(**+8%**). 함수까지 넣었다면 +34%였다.

## 8. 안전장치

- **매니페스트가 없거나 못 읽으면 조용히 넘어간다.** 기존 `MissingMetadata`·
  `FailedToParse` 관용과 같다 — 재료 없음을 실패로 바꾸지 않는다.
- **명세 파일이 실제로 있을 때만 더한다.** 없으면 경고 한 줄.
- **더해진 것을 화면과 로그에 명시한다.** 사람이 고르지 않은 것이 들어갔음을 숨기지 않는다.
- **상한 초과 시 중단하고 경고한다**(§5).

## 9. 검증

**단위 테스트**

| 잠글 것 | 왜 |
|---|---|
| 프로시저 참조가 더해진다 | 이 설계의 본체 |
| **함수 참조는 안 더해진다** | §2. 함께 더하면 프롬프트가 +34%가 되고 부모의 호출 표와 중복된다 |
| 순환에서 종료한다 | `Summary → EXTRA → Summary`가 실물이다 |
| 순서가 참조자 바로 뒤다 | §6. 순서가 실행 순서다 |
| 매니페스트 부재 시 침묵 | 소프트 스킵 관용 |
| 상한 초과 시 중단·경고 | 폭주 방어 |

**코퍼스 테스트**: `POQSettleBatch4` 로스터로 폐포를 계산해 **12 → 14**와 더해진 둘의
이름을 잠근다. 코퍼스가 없으면 건너뛴다(기존 `CorpusSkip` 관용).

**통제군**: 이 변경의 효과는 **다음 생성 회차에서만** 드러난다. §10-4 Few-Shot 두 층
처방의 검증은 `Batch3 ↔ Batch4`가 이미 끝냈으므로(판독 §4 「먹었다」), 다음 판은
`Batch4`(12편) ↔ `Batch5`(폐포 14편)로 **변인이 하나**다.

그때 볼 것:

1. `S12`·`S13`의 `LegacyProcedures`가 하위 SP를 가리키는가
2. 그 단계의 오류 코드가 `4000~4008`·`ERROR_NUMBER`로 서는가
3. `CheckLegacyStepErrorCodeInvention`이 그 단계를 **올바른 집합으로** 판정하는가
4. 프롬프트 증가가 실측 +8% 안에 있는가

## 10. 이 설계가 답하지 않는 것

- **함수 참조를 영영 안 넣는가** — 이 회차의 판단은 「부모의 호출 표로 충분하다」이고,
  근거는 계획서가 함수를 재구현하지 않는다는 것이다. 이행 라운드가 함수 본문을
  필요로 하게 되면 그때 다시 잰다.
- **하위 SP가 자기 단계로 서야 하는가** — 이 설계는 재료를 대는 것까지다.
  목차가 그것을 몇 단계로 가를지는 모델의 판단이고, `Batch4`는 자발적으로 갈랐다.
- **「산문으로 옳게 쓰고 의사코드에서 어긴다」** — §3의 상자. 재료를 늘려도 그 실패
  양식은 안 바뀐다. 그것은 별개 축이다.
