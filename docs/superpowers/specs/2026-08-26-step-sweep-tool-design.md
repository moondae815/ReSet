# 스윕 도구화 — 단계 검사 A~E를 재현 가능한 측정으로

> 2026-08-26 · 브랜치 `step-sweep-tool` · 기반 `058363b`

## 0. 왜 지금인가

**하네스가 세 번 지어졌고 세 번 버려졌다.** Task 8·19·11이 각각 스크래치 디렉터리에
코퍼스 스윕 하네스를 새로 짓고, 측정하고, 던졌다. 셋 다 지금 없다. 남은 것은
`docs/known-defects.md`에 사람이 손으로 옮겨 적은 집계와 표본 13건 표뿐이고,
**검사 B·C가 발화한 103건의 목록 자체는 저장소 어디에도 없다.**

그래서 두 가지가 막힌다.

1. **캐시 17 선결 조건 (5)의 재측정 3종**(다중 레거시 SP 단계 수 · 103건 오탐률 ·
   코드 집합이 어긋나는 SP 수)을 재려면 하네스를 또 지어야 한다.
2. **회차 간 비교가 불가능하다.** `docs/audit-defect-catalog.md`가 "총량을 회차 간에
   비교하지 마라"고 경고하는데, 그 이유의 절반은 대상 범위 변동이고 나머지 절반은
   **측정 도구가 회차마다 다른 것**이다. 앞의 절반은 문서로 관리되지만 뒤의 절반은
   지금까지 아무 장치도 없다.

Task 10의 워커 오진이 이 부재의 직접적 결과다. 조건 (B)를 재야 할 자리에서 (A)를
재고 "코퍼스가 변했다"고 보고했다. 코퍼스는 변하지 않았고(`FormatVersion` 집합이
`{16}` 하나로 확인됨) 워커가 다른 하네스를 쓴 것이었다.

**이 설계의 목적은 결함을 닫는 것이 아니라 측정을 자산으로 만드는 것이다.**

## 1. 범위

**포함** — 단계 검사 다섯 개의 코퍼스 전수 발화량, 조건 (A)·(B) 양쪽, 검사 B·C
발화 전건 목록, 선결 지표 둘, 하네스 결손 보고.

| | 검사 | 메서드 |
|---|---|---|
| A | 명세서 대비 문장 개수 | `MechanicalValidator.CheckStatementCountAgainstSpec` (:6046) |
| B | 앵커 문장의 술어·조인 키 결측 | `CheckAnchoredStatementFacts` (:6249) |
| C | 앵커 문장의 초과 술어 | `CheckAnchoredStatementExtras` (:6458) |
| D | 명세서 지역 변수 미선언 | `CheckSpecLocalVariablesDeclared` (:6546) |
| E | 상태 변수 초기값이 오류 코드 | `CheckStepIdInitialValue` (:5909) |

**제외** — 코드 집합 대조 방어의 구현, CTE 가드를 검사 B·C로 확장, `StepSqlStatementReader`
INSERT 배선 수정, `CurrentCacheFormatVersion` 인상, 전건 재생성. 넷 다 이 도구가 낼
수치를 근거로 다음 회차에서 판단한다. **노출량을 재기 전에 방어를 만들지 않는다** —
`-9` 소실이 코퍼스에 1건인지 30건인지 모르는 채로 방어의 모양을 정하면 그 방어가
무엇을 막는지도 모른 채 남는다.

## 2. 구조

두 조각이다.

**`src/ReSet.Core/Services/StepSweepService.cs`** — 디스크를 모른다. 메모리에 올라온
Job 목록을 받아 보고서 객체를 낸다.

```
SweepInput   { IReadOnlyList<SweepJob> Jobs }
SweepJob     { JobName,
               IReadOnlyList<BatchStepPlan> Steps,
               IReadOnlyDictionary<string,string> StepMarkdownByCode,
               IReadOnlyList<(string FileName, string Content)> Specs,
               IReadOnlyDictionary<string,string> DdlByProcedure,
               IReadOnlyDictionary<string,string> DateParameterByProcedure,
               HarnessGaps Gaps }
SweepReport  { PerCheckPerCondition, PerJob, AnchoredFindings,
               Indicators, HarnessGaps }
```

**책임 경계.** CLI가 디스크에서 읽고 파싱까지 마쳐서 넘긴다 — `PlanStructure.md`를
`BatchStepPlanParser.TryParse`로 `Steps`로, `metadata.json`을 `SpDefinition`으로
역직렬화해 `DdlText`와 `SpecExpectations.ResolveDateParameter(StaticAnalysis)`의
결과를 각각 사전에 담아서. `Specs`는 `SpecStatementFactsExtractor.Extract`가 받는
모양(`(FileName, Content)` 목록) 그대로 넘긴다 — 서비스가 그 함수를 부르므로 형태를
바꾸면 두 곳이 갈린다. 서비스는 파일도 `SpStaticAnalysisResult`도 모른다.

**`src/ReSet.Cli/SweepCommand.cs`** — `CoverageMapCommand`의 선례를 그대로 따른다.
`output/`을 읽어 `SweepInput`을 만들고, 서비스를 부르고, 마크다운을 쓴다.
`Program.cs`에서 배치 가드 **앞**에 분기하고(무인 배치 안전 장치에 걸리지 않게),
아무것도 재지 못하면 `Environment.ExitCode = 1`로 끝낸다 —
`CoverageMapCommand`가 "종료 코드 0으로 끝나면 아무것도 만들지 않았는데도 파이프라인이
초록으로 통과한다"고 적어 둔 것과 같은 규약이다.

### 왜 로직이 Core에 있어야 하는가

`CoverageMapCommand`는 로직을 CLI에 두었고, 그 결과 테스트가
`CoverageMapGoldenTests`가 되었으며 **코퍼스가 없으면 `Skip.If`로 조용히 건너뛴다.**
2026-08-26 기준 `dotnet test`의 건너뜀은 정확히 그 두 건이었다(코퍼스를 붙인 뒤 0이 됨).

이 회차의 목적이 "측정을 재현 가능하게 만드는 것"인데 그 도구의 회귀 테스트가
코퍼스 유무에 따라 조용히 통과하면 목적을 스스로 배반한다. 로직을 Core에 두면
집계·조건 (B) 주입·지표 계산을 **합성 `SweepJob`으로 코퍼스 없이** 테스트할 수 있다.
CLI에 남는 것은 파일 읽기와 마크다운 쓰기뿐이고, 그 둘은 골든 테스트가 없어도
실행 한 번으로 확인된다.

## 3. 입력 계약

`VerificationPipelineOrchestrator.cs:3238`의 호출을 그대로 본뜬다. 갈라지면 스윕이
파이프라인이 실제로 하지 않는 판정을 재게 된다.

| `ValidateBatchStep` 인자 | 출처 | 로컬 가용성 |
|---|---|---|
| `stepMarkdown` | `output/Jobs/<job>/agent/steps/<code>.md` | ○ |
| `step` · `allSteps` | `PlanStructure.md` → `BatchStepPlanParser.TryParse` | ○ |
| `statementFactsByProcedure` | `Spec.md` → `SpecStatementFactsExtractor.Extract` | ○ |
| `conditionColumnsByProcedure` | `Spec.md` → `SpecConditionColumnExtractor.Extract` | ○ |
| `knownTableNames` | 카탈로그 | △ — 비면 소프트 스킵 |
| `stepInterfaces` | DB 메타데이터 | ✗ → `null` |
| `runRowOwnedTables` | DB 메타데이터 | ✗ → `null` |

마지막 둘은 로컬에서 만들 수 없다. **A~E 어느 검사도 그 둘을 읽지 않는다** —
`stepInterfaces`는 `CheckStepInterface`(:600)가, `runRowOwnedTables`는
`CheckFirstStepRowCreation`(:1518)이 쓰고, 둘 다 이 스윕의 측정 대상이 아니다.
Task 19가 같은 조건으로 측정했으므로 수치가 이어붙는다. **보고서 머리말에 이 두
`null`을 매번 적는다** — 다음 사람이 "전 검사를 쟀다"고 오독하지 않게.

`output/`은 `.gitignore`이고 새 워크트리에는 없다. 실행 전에 심링크를 붙인다.

## 4. 조건 (A)와 (B)

측정에는 조건이 둘이다.

- **(A) 오늘 그대로.** `CurrentCacheFormatVersion`이 16이고 캐시 31건이 전부 16이라
  「오류 코드」 표가 어느 `Spec.md`에도 없다. `ErrorCodeToOrdinal`이 항상 비고,
  `ResolveOrdinal`의 코드 앵커 경로는 도달 불가다. 검사 B·C가 사실상 0을 낸다.
- **(B) 캐시 17 이후 모사.** 원본 DDL에서 코드→서수 사전을 만들어 주입하면 코드
  앵커가 켜진다. Task 8이 잰 "2건 → 199건"이 정확히 이 두 조건의 차이다.

**각 `(Job, Step)`마다 `ValidateBatchStep`을 두 번 부르고 나란히 보고한다.** 조건을
골라 실행할 여지를 없애는 것이 목적이다 — Task 10의 오진은 고를 수 있었기 때문에
일어났다. 두 조건의 **차이 자체가 캐시 17이 켜질 때 생기는 변화량**이므로 어차피
둘 다 필요하다.

### (B) 사전을 만드는 방법 — 진짜 계약을 왕복시킨다

순진한 구현은 `ExtractErrorCodes`의 결과를 직접 사전으로 접는 것이다. 그러면
**중복 코드 처리 규칙이 두 곳에 생긴다.** `SpecStatementFactsExtractor.ReadErrorCodeToOrdinal`
(:299)은 같은 코드가 두 문장에 붙으면 덮어쓰지 않고 아예 빼며, `dropped` 집합으로
세 번째 등장까지 막는다. 스윕이 이 규칙을 다시 구현하면 조금만 달라도 **실제
파이프라인이 결코 만들지 않을 사전으로 측정하게 된다.**

그래서 왕복시킨다.

```
metadata.json.DdlText
  → DmlScopeExtractor.ExtractErrorCodes(ddl, dateParam)   ← 둘 다 CLI가 넘긴 값
  → 「오류 코드」 표를 Spec.md 형태로 렌더링
      DmlScopeExtractor.ErrorCodeTableHeading
      | 문장 | 오류 코드 | 설정 대상 |
      | UPDATE 9 | -13 | @v_errCode |
  → SpecStatementFactsExtractor.Extract(합성 Spec.md)   ← 진짜 리더
  → facts with { ErrorCodeToOrdinal = 그 결과 }
```

**읽는 쪽은 제품 코드 그대로다.** 쓰는 쪽만 스윕이 재현하는데, 표 모양이 어긋나면
`ReadErrorCodeToOrdinal`이 헤더를 못 찾아 **빈 사전**을 돌려준다 — 조용히 틀리지 않고
"(B)에서도 0"이라는 눈에 띄는 결과로 나온다. 테스트가 이 자리를 못으로 박는다(§8).

`ErrorCodeToOrdinal`은 `init` 속성이므로 `with` 한 줄로 갈아끼운다. **제품 코드는
바뀌지 않는다.** `ResolveDateParameter`는 이미 `public static`이고
`ProcedureParameters`는 `metadata.json`의 `StaticAnalysis`에 있다(실측:
`UP_UTIL_SETTLE_COMM_UPD` → `["@pi_strYMD CHAR(8)", "@po_intRetVal INT"]`).

### (B)가 무엇이 아닌지

**(B)는 완전 전사를 가정한다.** 실제 재생성에서는 모델이 표를 옮기다 틀릴 수 있고,
그 전사 오류는 `MechanicalValidator`의 전사 대조 검사(`ErrorType.ErrorCodeTableMissing`)가
따로 잡는다. 따라서 **(B)는 "축이 켜졌을 때의 상한"이지 재생성 후 실제 발화량의
예측이 아니다.** 보고서에 이 문장을 그대로 싣는다.

## 5. 선결 지표 둘

같은 순회에서 부수적으로 센다. 둘 다 캐시 17 선결 조건이 "인상 전에 세야 할 수치"로
지목한 것이다.

**(1) 다중 레거시 SP 단계 수.** 한 단계가 참조하는 원본 SP가 2개 이상인 건수.
`MergeErrorCodeMaps`가 코드 문자열만을 키로 삼고 SP로 스코프하지 않으므로, SP A에만
있는 코드가 병합 사전에 남아 실제로는 SP B에서 온 문장을 A의 (Kind, Ordinal)로
환산할 수 있다. 하위 가드(후보 1개 판정 + `TargetTable` 대조)가 일부만 막는다 —
두 SP가 같은 물리 테이블을 갱신하면 통과한다. **이 지표가 그 위험의 노출량이다.**

**(2) 코드 집합이 어긋나는 단계 수.** 단계의 코드 라벨 집합 ≠ 그 단계가 참조하는
SP들의 `ExtractErrorCodes` 코드 집합인 건수. 방향을 나눠 센다 — 표에는 있는데 단계에
없는 것, 단계에는 있는데 표에 없는 것.

**단위가 SP가 아니라 단계인 이유**(2026-08-26 정정 — 이 절은 원래 "SP 수"라고 썼다).
아래 §5가 제안하는 방어는 "코드 집합이 어긋나면 **그 단계에서** 코드 축을 통째로
끈다"이다. 방어가 단계 단위로 작동하므로 노출량도 단계 단위로 세야 그 수치가 곧
"방어가 켜질 단계 수"가 된다. SP 단위로 세면 한 SP가 여러 단계에 나뉘었을 때 방어가
실제로 몇 곳에서 켜지는지 알 수 없다. 같은 이유로, 한 단계가 SP를 둘 이상 참조하면
그 SP들의 코드를 **합집합**으로 놓고 대조한다 — 방어가 단계 전체의 코드 축을 끄므로
합집합이 방어의 의미와 일치한다. 다만 그 대가로 **정상 분업(SP A의 코드만 쓰는 단계)과
진짜 소실이 구분되지 않는다.** 다중 레거시 SP 단계 수를 함께 재는 이유가 이것이다 —
두 수치를 나란히 놓아야 이 오탐의 상한을 읽을 수 있다.

**펜스 파싱에 실패한 단계는 이 대조에서 뺀다.** `StepSqlStatementReader`의 실측이
코퍼스 891개 펜스 중 191개(21%) 파싱 실패, 326개 파일 중 119개(36%)가 최소 하나라고
기록한다(`StepSqlStatementReader.cs:70-77`). 파싱에 실패하면 코드 앵커가 하나도 안
읽히므로, 빼지 않으면 이 지표가 재는 것이 "코드 라벨 소실"이 아니라 "ScriptDom이 못
읽는 관용구의 분포"가 된다. 검사 A(`CheckStatementCountAgainstSpec`)가
`lostStatementCount > 0`에서 통째로 접는 것과 같은 규약이다
(`MechanicalValidator.cs:6053`). 뺀 건수는 `StepsSkippedForParseFailure`로 보고서에
싣는다 — 그 값이 크면 지표 자체를 믿을 수 없다는 신호다.

실측된 사례가 있다. `UP_UTIL_SETTLE_COMM_UPD`의 원본은 `PGNAME IN ('inivacct')` 블록에
`-9`, easybank 블록에 `-10`, KFTC/INIBANK 블록에 `-11`을 쓰는데, 이행 코드
(`POQSettleProc19/S11.md`)는 같은 세 블록에 `-10`·`-11`·`-12`를 단다. **`-9`가
소실되고 이후 전체가 1씩 밀렸다.** 한 문장의 오기재가 아니라 SP 꼬리 전체의 체계적
이동이다. `AiService.cs:2117`의 `[Error Codes]` 규약이 재매핑을 금지하지만 프롬프트
수준 강제라 지켜지지 않았고, §3 불일치 침묵도 못 막는다 — 그 문장 주변에 U-앵커가
없어서 대조할 상대가 없다.

이 지표는 밀림을 직접 보지 않고 **밀림의 원인(라벨 소실)**을 본다. 집합 단위라 값싸다.

## 6. 하네스 결손을 매번 화면에 남긴다

`CoverageMapCommand`가 빠진 객체를 화면에 남기는 이유와 같다 — *"폐포 31개 중 몇
개가 조용히 빠지면 맵은 멀쩡해 보이는데 대조 범위가 줄어든 것을 아무도 모른다."*

보고서 머리말에 **항상** 찍는다. Task 19가 기록한 결손이 기준선이다.

- `PlanStructure.md` 파싱 실패 Job 목록 — `POQSettleProc4`(73단계 선언, `BatchStepPlanParser.MaxSteps` 40 초과로 `TryParse`가 `null`) · `POQSettleProc7`(`"Steps": []`)
- 선언됐으나 `agent/steps/`에 실물이 없는 단계 수 (Task 19 기준 51)
- 측정 쌍 수 (Task 19 기준 326, Job 18개)
- `stepInterfaces`·`runRowOwnedTables`를 `null`로 넘긴 사실
- `knownTableNames`가 비어 소프트 스킵된 검사

**줄어든 대상 범위가 개선처럼 보이는 것을 막는 것이 목적이다.** 결손 수치가 이전
회차와 다르면 그 자체가 보고 대상이다.

## 7. 결과물

`docs/audit-reports/sweeps/2026-08-26-step-sweep.md` — **커밋한다.** 이름 규칙은
`YYYY-MM-DD-step-sweep.md`이고 회차마다 새 파일을 만든다(덮지 않는다). 카탈로그가
기록한 사고 — `ConsistencyReport.md`가 이름 고정이라 5회차 보고서가 6회차 실행에
밀려났던 일 — 을 되풀이하지 않기 위해서다. `output/`은
`.gitignore`라 거기 쓰면 다음 재생성이나 다른 세션이 덮어 사라진다. 지금 고치려는
문제가 그대로 남는다.

구성:

1. **실행 조건 머리말** — 커밋 해시 · `CurrentCacheFormatVersion` · 캐시 인덱스의
   `FormatVersion` 집합 · 하네스 결손(§6)
2. **검사별 발화량** — (A)·(B) 나란히, 전체와 Job별
3. **검사 B·C 발화 전건 목록** — `(Job, Step, 문장, 결측/초과 항목, TargetTable, 판정)`.
   이것이 103건 판정의 작업 대상이고, 판정 칸을 채워 나가면 그대로 기록이 된다.
   Task 11의 표본 13건 표와 같은 열 구성을 쓴다 — 그 표가 이어붙게.
4. **선결 지표** (§5) — 다중 레거시 SP 단계 수 · 코드 집합 어긋남(양방향) · 파싱 실패로 제외한 단계 수

## 8. 테스트

전부 `tests/ReSet.Core.Tests`에 합성 `SweepJob`으로. **코퍼스에 의존하는 테스트를
만들지 않는다** — §2의 이유 그대로.

- **집계가 옳게 갈린다** — 발화 3건이 검사별·Job별로 옳은 칸에 들어간다.
- **조건 (B)가 실제로 다른 결과를 낸다** — 코드 라벨은 있고 U-앵커는 없는 단계에서
  (A)는 0, (B)는 1. **주입을 무력화하면 죽는 미끼다.** 이 테스트가 없으면 (B)
  경로가 통째로 죽어도 "(A)와 (B)가 같다"는 그럴듯한 결과로 통과한다.
- **왕복이 진짜 리더를 지난다** — 렌더링한 표의 헤딩을 한 글자 바꾸면 (B) 사전이
  비고 발화가 0이 된다. §4의 "조용히 틀리지 않는다"를 못으로 박는다.
- **중복 코드가 사전에서 빠진다** — 같은 코드가 두 문장에 붙은 DDL에서 그 코드가
  (B) 사전에 없다. 제품 규칙(`ReadErrorCodeToOrdinal`)과 같은 결과여야 한다.
- **코드 집합 어긋남 지표가 `-9` 소실 모양에서 발화한다** — 양방향 각각.
- **다중 레거시 SP 지표가 SP 2개짜리 단계를 센다.**
- **하네스 결손이 보고서에 실린다** — 파싱 실패 Job을 넣으면 결손 칸이 1이 된다.

## 9. 가정과 미해결

- **(B)는 상한이다.** §4 끝 참고. 재생성 후 실제 발화량은 전사 품질에 달렸다.
- **103건 판정은 사람이 한다.** 도구는 목록과 근거 좌표를 낼 뿐이고, 진짜 결함인지
  오탐인지는 원본 DDL과 이행 SQL을 읽어야 갈린다. Task 11 표본에서 13건 중 2건이
  진짜였다(`POQSettleProc13/S09` · `POQSettleProc17/S07` — 원본이 명시한 존재-필터용
  조인이 이행에서 통째로 소거). **이 비율을 103건 전체로 외삽하지 않는다** — 표본이
  검사 B에 치우쳐 있었다.
- **`knownTableNames`를 로컬에서 채울 방법은 이번 범위 밖이다.** 비면 소프트 스킵이고,
  A~E에는 영향이 없지만 보고서에 사실로 남긴다.
- **`MaxSteps` 40이 `POQSettleProc4`(73단계)를 배제한다.** 이 상한을 올릴지는 별개
  판단이라 이번에 건드리지 않고 결손으로 보고만 한다.

## 10. 진행 순서

1. ~~`error-code-anchor`를 `main`에 병합~~ — 완료(`058363b`, 빌드 경고 0·테스트 2785 통과)
2. `StepSweepService` + 테스트 (TDD)
3. `SweepCommand` + `Program.cs` 분기
4. 실행 → 보고서 커밋
5. 103건 판정 — 결과를 같은 보고서에 채운다
