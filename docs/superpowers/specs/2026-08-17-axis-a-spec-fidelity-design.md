# 축 A 명세서 충실도 설계 — 원본 DDL ↔ `Spec.md`

**작성일**: 2026-08-17
**상태**: 설계 확정

## 목표

`Spec.md`가 원본 DDL과 **같은 이야기를 하도록** 생성기를 고친다. 지금은 프롬프트가
요구한 적 없는 사실이 소실되고, 프롬프트가 거짓을 말한 자리를 명세서가 그대로 옮겨 적고,
로직 층을 대조하는 기계 검사가 축 A에만 없다.

산출물을 손으로 수선하지 않는다. **다음 회차부터 같은 결함이 생기지 않게** 하는 것이 목표다.

## 배경 — POQSettleProc16 정합성 감사 실측

[`output/Jobs/POQSettleProc16/consistency/ConsistencyReport.md`](../../../output/Jobs/POQSettleProc16/consistency/ConsistencyReport.md)가
SP 14개를 전수 대조해 축 A에서 43건을 냈다(🔴 1 · 🟠 5 · 🟡 20 · ⚪ 17). 14개 중 11개가
`결함` 판정이다.

43건을 코드 근거까지 되짚으면 **세 뿌리**로 갈린다.

| 뿌리 | 성격 | 해당 결함 | 코드 근거 |
|---|---|---|---|
| ① | 프롬프트가 요구한 적 없음 | A2(ROUND 의미) · A3(주석 블록 9건) · A5(헤더 주석 괴리) · `NOCOUNT` 누락 | `AiService.cs:317-396` 규칙 목록에 주석·세션옵션·상수 의미 항목이 **하나도 없음** |
| ② | 프롬프트 입력이 이미 틀렸거나 잘림 | A4(표기 3건+3건) · A6(문장 순번) · `INS_EXTRA` 자기참조 오탐 · `EXPECT_PROC` 스키마 오단정 2건 | 아래 실측 표 |
| ③ | 지시는 있으나 강제가 없음 | 🔴 1건(파생테이블 표현식) · A1 🟠 4건(조건 범위) | 축 A에 로직 층 L1 검사 부재 |

### 이것은 문서 결함이 아니라 프로그램 결함이다

②는 전부 실제 프롬프트 산출물에서 확인했다. 근거는
`output/Objects/dbo.UP_UTIL_SETTLE_EXPECT_PROC.Procedure/raw/prompt-context.md`이고,
그 SP의 원본 DDL 203–205행 **한 문장이 세 결함을 동시에 낳는다.**

```sql
UPDATE dbo.TSettleMst
SET    OutState  = IIF(OutState=0, 2, OutState)
      ,OutYMD    = (SELECT OutYMD FROM dbo.UIF_SettleYMD(E.ReqYMD, B.SettlePeriodID))
      ,EDIReqYmd = E.ReqYMD
FROM   SETTLE_POQ_DB.dbo.TSettleMst A WITH(NOLOCK) ...
```

| 관측 | 프롬프트 산출물 근거 | 귀결 |
|---|---|---|
| 거짓 자기참조 | `prompt-context.md:67` — "자기 자신을 참조합니다: OutState, **OutYMD**" | `OutState`는 참(`IIF(OutState=0,2,OutState)`), `OutYMD`는 거짓 — 우변은 TVF `UIF_SettleYMD`의 반환 컬럼 |
| 문장 앵커 붕괴 | 같은 대상 테이블로 `문장 1`이 두 번 등장, `문장 2` 다음이 `문장 8` | 순번이 단조롭지도 유일하지도 않아 앵커로 못 씀 |
| 스키마 표에서 컬럼 유실 | `EDIReqYmd`가 `:82`(UPDATE 표)·`:766`(정적분석)·`:1026`(원본 DDL)에 있는데 `<referenced-table-schemas>`(144–323행)에 **없음** | 규칙 `AiService.cs:389`가 "스키마에 없으면 스키마 불일치로 표기하라"고 명령 → 명세서는 시킨 대로 함 |

세 번째가 특히 중요하다. **명세서가 잘못 쓴 것이 아니라 프롬프트가 서로 모순된 두 사실을
동시에 제시했다.** 재생성으로는 영원히 고쳐지지 않는다.

여기에 죽은 변수 하나가 더 있다.

```csharp
// AiService.cs:303, :1542 — 계산하고 어디에서도 쓰지 않는다
bool hasComments = spDef.DdlText.Contains("--") || spDef.DdlText.Contains("/*");
```

원본 DDL 전문은 `<sp-source-ddl>`로 프롬프트에 들어간다(`AiService.cs:502-506`). 즉 **A3의 9건은
정보가 없어서가 아니라 요구한 적이 없어서** 생겼고, 요구를 붙이려던 자리가 비어 있다.

### 축 B에는 있고 축 A에는 없는 층

| | 이름·스키마 층 | 로직 층 |
|---|---|---|
| 축 B (Spec → 지시서) | `CheckUnknownTableReferences` 등 | `CheckMissingConditionColumns` + `SpecConditionColumnExtractor` + `SpecRoundingShapeExtractor` |
| 축 A (DDL → Spec) | `CheckUpdateMappings` · `CheckSchemaClaims` · `CheckTableIdentitySplit` | **없음** |

축 A 결함 분포가 정확히 이 공백을 그린다 — 🔴 1건과 A1 🟠 4건이 전부 로직 층이다.
축 B의 추출기들은 **Spec 마크다운**에서 재료를 뽑는데, 축 A는 기준값이 DDL이므로
추출기가 **AST 기반**이어야 한다는 점만 다르다.

## 범위

**포함**

- ②의 파서·필터 결함 4종 수정 (설계 1)
- ①을 닫는 재료 3종 신설과 그 짝인 L1 검사 (설계 2)
- ③을 닫는 기계 확정 표 2종과 그 짝인 L1 검사 (설계 3)

**제외** — 근거는 "닫지 못하는 것" 절에 있다.

- 축 B 124건 (별개 설계)
- 청킹 경로에서 파서 상태가 리셋되는 구조 자체
- UDF·TVF 내부 로직 대조
- 실행 회귀 검증

## 설계 0 — 재료 하나, 소비처 둘

새 재료를 놓을 자리는 저장소가 이미 정해 놓았다. `SchemaPromptColumnSelector`의 클래스
주석이 그 이유를 쓴다.

> 이 지식이 `AiService.FormatTableSchemaToMarkdown` 안에만 있으면 L1이 알 수 없다.
> 렌더링의 부수효과로 어딘가에 기록하는 방식은 택하지 않았다 — 렌더 경로가 둘이라
> 어느 쪽이 마지막에 기록했는지에 결과가 달라진다.

같은 형태를 축 A 재료 전부에 적용한다.

```
                    ┌──────────────────────┐
   SpDefinition ───►│  추출기 (순수 함수)   │──┬──► AiService: 프롬프트 체크리스트·표
   (DdlText,        │  재료 1건당 1개       │  │
    StaticAnalysis) └──────────────────────┘  └──► SpecExpectations.From: L1 대조 기준
```

### 0.1 계약

1. 추출기는 `SpDefinition`만 받아 사실을 내는 **순수 static 함수**다. IO도 LLM 호출도 없다.
   그래서 단위테스트가 곧 검증이다.
2. 프롬프트 항목과 L1 대조 기준은 **같은 추출기 호출 결과**에서 나온다. 어느 쪽도 자기
   계산을 갖지 않는다.
3. 재료가 통째로 비면 프롬프트 항목과 L1 검사가 **함께** 사라진다. 한쪽만 남는 경로를
   만들지 않는다.
4. 재료의 각 항목은 **앵커**(원본에서 그대로 뽑은 식별자·리터럴)를 갖는지 필드로 표시한다.
   앵커가 있으면 프롬프트 + L1, 없으면 프롬프트만. **왜 검사하지 않는지가 코드에 남는다.**

계약 2가 우아함을 위한 것이 아님을 분명히 해 둔다. `RecordUpdateMapping`의 주석이 그 이유를
이미 적었다 — *"잘못 푼 테이블 이름에 컬럼을 붙이면 L1이 존재하지 않는 표를 요구하게 되고,
그것은 무한 재시도가 된다."* **새 L1 검사가 요구하는 것은 전부 모델이 프롬프트에서 실제로
받은 재료여야 한다.**

### 0.2 결함을 두 채널로 가른다

`VerificationPipelineOrchestrator.cs:226-238`에 이미 있는 구분을 따른다.

| 성격 | 채널 | 재시도 | 뿌리 |
|---|---|---|---|
| AI가 요구를 안 지킴 — 재생성이 고칠 수 있음 | `ValidationResult.Errors` (L1) | O | ① ③ |
| 프롬프트·파서가 거짓을 말함 — 재생성이 못 고침 | `SpecExpectations.InputDefects` → 경고 | **X** | ② |
| 판정 불가 | 재료에 싣되 검사 없음 | — | 앵커 없는 항목 |

②를 L1 오류로 만들면 무한 재시도가 된다(`:228`의 기존 판단). **1단계는 코드를 고치는 일이고
2·3단계는 검사를 더하는 일이다.** 성격이 달라 단계가 갈리는 것이지 편의상 나눈 것이 아니다.

## 설계 1 — 입력 정확성 (②)

### 1.1 거짓 자기참조 제거

`ColumnReferenceCollector`(`SqlStaticParser.cs:461-471`)가 `NewValue` 식 트리 전체를 훑으면서
**맨이름만** 비교한다. 중첩 스칼라 서브쿼리 스코프를 벗어나지 않는다.

수정 두 갈래를 함께 넣는다.

- collector가 하위 `ScalarSubquery` / `QuerySpecification`로 내려가지 않는다 — 그 스코프의
  컬럼은 다른 테이블 소속이다.
- 한정자가 붙은 참조(`A.OutYMD`)는 그 별칭이 갱신 대상 인스턴스일 때만 자기참조로 본다.

`FindSelfReferences`의 기존 주석이 밝힌 설계 의도("판정을 한 문장 안으로 제한한다")는
유지된다. 이 수정은 그 제한을 **문장 안의 스코프**까지 좁힐 뿐이다.

`InputDefects`로 표면화하지 않는다. 거짓 문장을 만들지 않으면 끝나는 문제다.

### 1.2 문장 앵커를 라인으로 병기

`_updateOrdinals`(`SqlStaticParser.cs:187`, 채번은 `:389-395`)는 대상 테이블 이름별 카운터인데, 청킹 경로가
파서를 여러 번 돌려 카운터가 리셋된다.

**순번을 없애지 않는다.** `CheckUpdateMappings`가 순번으로 표를 식별하므로 라인으로 갈아치우면
기존 L1이 깨진다. 대신 병기한다.

- `AstUpdateMapping`에 `SourceLine`을 더한다 (`node.StartLine`).
- 프롬프트 렌더를 `### UPDATE 대상 테이블: {표} (문장 N · 원본 DDL 라인 L)`로 바꾼다.
  (`AiService.cs:559`, `:196`)
- `StaticAnalysisNormalizer.cs:72`가 순번을 옮겨 담고 있으므로 라인도 함께 옮긴다.

라인은 청킹과 무관하게 유일하고, `object_definition.sql`로 사람이 직접 대조할 수 있다.
**감사가 실제로 쓰는 앵커가 그것이다** — 보고서 §4의 기준값 앵커 칸이 전부
`object_definition.sql:NNN` 형식이다.

### 1.3 UPDATE SET 대상 컬럼이 스키마 표에서 사라지는 것

컬럼 참조 리졸버(`SqlStaticParser.cs:898-912`)에 **INSERT 분기는 있는데 UPDATE 분기가 없다.**

한정자 없는 `SET EDIReqYmd = ...`는 로컬 `QuerySpecification` 스코프에서 못 풀고 →
`_statementContext.Peek() == "INSERT"` 분기에 안 걸리고 → `ReferencedTables.Count == 1` 폴백도
큰 SP에서는 거짓 → `targetTable`이 `"Unknown"`으로 남아 **버려진다.**

INSERT와 대칭인 UPDATE 분기를 넣는다. 문맥이 `UPDATE`이고 `_dmlTargetResolved`가 참이면 그
해결된 대상 테이블에 귀속한다. 대상은 `RecordUpdateMapping`이 이미 풀어 놓았으므로 새로
추론하지 않는다.

`SchemaPromptColumnSelector`의 클래스 주석이 이 위험을 이미 적어 두었다 — *"과소 포함은 모델이
그 컬럼을 '존재하지 않는다'고 잘못 기록한다 — 14개 명세서를 망가뜨린 바로 그 결함이다."*
이번 감사가 그 예측이 실현된 사례다.

**미확정 잔여 — 2026-08-17 실측으로 확정됐다. 아래 원래 추정은 틀렸다.**

> ~~보고서가 함께 든 `CollectMonth2/3` 계열 8컬럼은 읽기 컬럼이라 경로가 다르다. 이 수정으로
> 닫히는지 확정하지 않았다. 안 닫히면 `DetectOrphanedColumnKeys`의 탐지 범위를 넓힌다.~~

측정 결과 **8컬럼은 이 수정으로 닫히지 않고, `DetectOrphanedColumnKeys`를 넓혀도 닫히지
않는다.** 원인이 추정과 다르다.

`CollectMonth2/3` · `CollectDay2/3` · `CollectTxSDay2/3` · `CollectTxEDay2/3`는
**`EXPECT_PROC`의 SQL 본문에 한 번도 나오지 않는다**(`object_definition.sql` grep 0건).
`EXPECT_PROC`가 호출하는 UDF `UF_GET_COLLECTYMD`의 본문에만 있고, 그 안에서는 단일 테이블
`SELECT ... FROM TPGCollectPeriodMst`의 비한정 컬럼 목록이다. 그 UDF 자신의
`prompt-context.md`에는 8컬럼이 스키마 표에 정상적으로 실려 있다 — **그 객체의 컬럼
리졸버는 잘 동작한다.**

따라서 이것은 리졸버 결함이 아니라 **객체 간 컬럼 의존성 병합의 공백**이다. SP가 DDL이
제공된 UDF를 호출할 때, 그 UDF가 읽는 컬럼이 SP 자신의 `TPGCollectPeriodMst` 스키마 표에
합쳐지지 않는다. `EXPECT_PROC`의 `Spec.md:341`이 그 상태를 정확히 서술한다 — 해당 컬럼이
"함수 소스"에는 있으나 "제공된 스키마 표"에는 없다고.

`DetectOrphanedColumnKeys`가 답이 될 수 없는 이유: 그 검출기는
`ReferencedColumnsPerTable`의 **키**가 어느 의존성에도 병합되지 않은 경우를 찾는다.
이 8컬럼은 `EXPECT_PROC`의 SQL에 없으므로 애초에 `ReferencedColumnsPerTable`에 들어가지
않는다. 검출기가 볼 것이 없다.

**이 계획의 범위 밖으로 남긴다.** 별개 기제이고, 고치려면 재귀 의존성 수집이 하위 객체의
`ReferencedColumnsPerTable`을 상위로 접어 올리는 설계가 필요하다 — 축 A 충실도가 아니라
프롬프트 입력 구성의 문제다.

### 1.4 표기 출처 병기

프롬프트는 전 구간을 `SETTLE_POQ_DB.dbo.TSettleMst`(파서 정규화)로 쓰는데 원본은
`UPDATE dbo.TSettleMst`(2부)다. 명세서는 정규화 이름을 원문 표기처럼 서술했고,
`UP_UTIL_STAT_PGCOLLECT_INS`에서는 원본에 3부 참조가 **0건인데도** "3부 식별자 기반 크로스
데이터베이스 참조이며 Linked Server 원격 참조가 아닙니다"라고 단언했다.

두 갈래를 **짝으로** 넣는다. 어느 한쪽만으로는 성립하지 않는다.

- **(a) 렌더링에 원문 표기 병기** — `SETTLE_POQ_DB.dbo.TSettleMst (원문: dbo.TSettleMst)`.
  `AstUpdateMapping`과 `ReferencedColumnsPerTable` 렌더 지점 양쪽.
- **(b) 규칙 한 줄** — *"정규화된 이름은 분석 편의용이며 원문 표기가 아니다. 원본 식별자의
  부(部) 수를 서술할 때는 `<sp-source-ddl>`만 근거로 삼아라."*

(a) 없이 (b)만 넣으면 모델에게 지킬 근거가 없다.

**L1 검사.** Spec에 "3부 식별자" · "크로스 데이터베이스 참조" · "Linked Server" 같은 **표기
주장**이 있는데 원본 DDL의 3부 참조가 0건이고 `LinkedServerReferences`도 비어 있으면 오류.
결정적으로 판정된다.

## 설계 2 — 재료 3종 (①)

### 2.1 `SourceCommentBlocks`

`DdlText`에서 주석을 뽑되 **전부가 아니라 세 부류만** 뽑는다. `OmissionCommentScanner`가 남긴
교훈이 근거다 — *"패턴을 좁게 유지한다. 배너가 잦으면 사람이 읽지 않는다."*

| 부류 | 판정 | 앵커 | 닫는 결함 |
|---|---|---|---|
| 비실행 코드 주석 | 주석 본문에 SQL 토큰(`AND` / `SELECT` / `=` / 식별자) | 식별자·날짜 리터럴 | A3 핵심 |
| 코드 범례 주석 | `숫자:라벨` 나열 (`0:반올림`, `1:자동,7:수납`) | 숫자-라벨 쌍 | A2 · `EXPECT_PROC` 범례 |
| 헤더 주석 | `CREATE PROCEDURE` 앞 블록 | 선언 키워드 | A5 |

A3 표의 9건을 실제로 대조하면 `UF_GET_CLIENTID4TMONET` · `ClientIDType` · `FeeCharge` ·
`CLIENTFEEAMT` · `AHEADSETTLEAMT` · `ContractCancelYMD` · `ExtraSettleFlag` — **대부분이 식별자
앵커를 갖는다.** 앵커 없는 ⚪ 2건(`--매입요청일(D)+1 : 집계 고려`, 사유 주석)만 프롬프트
전용으로 빠진다.

- **프롬프트**: 비실행 주석 N건의 조건식 원문·도입 일자·사유를 제약 절에 기록하라는
  체크리스트 항목. 앵커를 함께 제시한다.
- **L1**: 앵커 토큰이 Spec 본문에 없으면 오류.

이 재료가 비지 않는 것이 규칙 활성 조건이 되므로, **죽어 있던 `hasComments`는
`SourceCommentBlocks.Count > 0`으로 대체되어 사라진다.**

### 2.2 `RoundingSemantics`

AST에서 `ROUND(식, 자릿수, 함수)` 3인자 호출을 뽑는다.

핵심은 **3번째 인자의 의미가 이 SP의 사정이 아니라 T-SQL 명세**라는 점이다. 0이면 반올림,
0이 아니면 절사. 재료가 그 문장을 상수로 들고 있으면 되고 추측이 아니다. 원본 주석
`--0:반올림, 0<>절사`는 그 명세를 재확인해 줄 뿐이다.

- **프롬프트**: 값 매핑을 기술했는지 묻는 체크리스트 항목.
- **L1**: 3인자 호출이 있는데 Spec에 절사 계열 토큰이 하나도 없으면 오류. 동의어 집합으로
  판정한다 — 절사 · 버림 · 내림 · truncate.

**골든 케이스가 이미 있다.** `UP_UTIL_SETTLE_INS_EXTRA4PLCARD`의 Spec은 이 매핑을 정확히
기록한 반례다(보고서 §4-1 A2). 그 Spec을 테스트에 고정해 오탐을 사전에 막는다.

### 2.3 `SessionOptions`

AST의 `SET NOCOUNT` · `XACT_ABORT` · `TRANSACTION ISOLATION LEVEL` 등을 뽑는다. 셋 중 가장
단순하고 앵커가 옵션 이름 자체라 대조가 자명하다.

**프로시저 본문(`AS` 이후) 안의 것만** 재료로 삼는다. `SET ANSI_NULLS ON`은 CREATE 배치
앞머리에 관례적으로 붙는 노이즈다. `UP_Util_Settle_Summary`의 🟡이 정확히 *"AS 직후
BEGIN TRAN 앞"*의 `SET NOCOUNT ON`이었다.

### 2.4 A5는 별도 재료 없이 닫는다

헤더 주석이 2.1에 실리면 프롬프트가 "헤더 선언과 구현이 다르면 그 모순 자체를 기록하라"고
묻는다.

L1은 좁게 **한 패턴만** 본다 — 헤더 주석이 내부 SP 호출을 `NONE`으로 선언했는데 정적분석에
`EXEC`가 있으면 오류. `UP_Util_Settle_Summary`가 정확히 그 케이스라 기계가 직접 판정할 수 있다.
넓히지 않는다.

## 설계 3 — 기계 확정 표 2종 (③)

결함 다섯 건의 공통 구조는 이렇다: **Spec이 "범위가 이러이러하다"고 단언하는데 원본에는 그
필터가 없다.** `COMM_UPD` 문장 7은 갱신 대상에 `YMD` 필터가 없는데 Spec은 "정산 행은
`YMD = @pi_strYMD`"라 적었고, `INS_EXTRA`의 DELETE는 `OutState`/`OutYMD` 조건이 전혀 없는데
Spec은 "선행 EXISTS에서 중단되므로 삭제 대상에 포함되지 않습니다"라 단언했다.

**부재를 서술했는지는 자연어 판정이다.** 대조할 앵커가 없다. 여기서 축 B가 겪은 사고(실측
15건 중 14건 오탐)가 재현될 수 있다.

그래서 **서술을 판정하지 않고 표를 강제한다.** 저장소에 검증된 패턴이 있다 —
fill-in-the-blanks 표(`AiService.cs:343-367`)와 그 짝인 `CheckUpdateMappings`다. 프롬프트가 표를
미리 채워 주고, 모델은 설명 칸만 채우며, L1은 행의 존재와 확정 값의 보존을 대조한다.

### 3.1 DML 범위 표

```
### DML 범위 (기계 확정 — 수정 금지)
| 문장 | 라인 | 갱신/삭제 대상 | 대상에 적용된 WHERE 술어 컬럼 | 정산일 파라미터 적용 | 조인 키 |
| 7    | 227  | TSettleMst X   | PLTID                        | 아니오 (서브쿼리에만)  | PLTID, ID |
```

"정산일 파라미터 적용" 칸이 이 표의 핵심이다. AST로 결정적으로 판정된다 — 파라미터 비교가
WHERE 최상위에서 **대상 별칭에** 걸렸는가, 아니면 서브쿼리 안에만 있는가.
`EXCEPTION_PROC` 실행순서 18과 `COMM_UPD` 문장 7이 정확히 후자다.

조인 키 칸이 A1의 셋째(`실행순서 4`의 `MallID` 누락)를 닫는다. DELETE 문장도 같은 표에
실으면 넷째(`INS_EXTRA`)가 닫힌다.

### 3.2 파생 테이블 정의 표

🔴 1건은 조건 범위가 아니라 **표현식 깊이**의 문제다. `EXCEPTION_PROC`의 SET 우변이
`ISNULL(X.PGCOMM,0)`에서 멈추는데, `X`는 파생 테이블이고 그 안에
`IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt)` — 프로모션 건의 원가 기준금액 —
이 들어 있다. Spec은 X의 정의를 어디에도 적지 않았다.

```
### 파생 테이블 정의 (기계 확정 — 수정 금지)
| 별칭 | 컬럼 | 정의 표현식 |
| X | PGCOMM | dbo.UF_GET_COMM4PG(..., IIF(ISNULL(A.DiscountFlag,'N')='Y', A.DiscountAmt, A.TxAmt), ...) |
```

UPDATE의 FROM 절에 `QueryDerivedTable`이 있고 SET 우변이 그 별칭을 참조하면, 그 파생 테이블의
SELECT 컬럼 표현식을 재료로 싣는다. L1은 표 행의 존재와 표현식 앵커(`DiscountFlag`,
`DiscountAmt`)를 대조한다.

### 3.3 표 비대 우려는 실측으로 반박된다

`EXCEPTION_PROC`는 UPDATE가 20개 이상이지만 `prompt-context.md`를 보면 UPDATE 채우기 표가
**이미 그 규모를 감당하고 있다**(문장 1~8 이상이 나란히 실려 있다). 새 표는 문장당 한 행이라
기존 표보다 작다. 규칙 `:380`의 ANTI-SHORTCUT 조항도 이미 그 자리에 있다.

### 3.4 이 설계가 판정하지 않는 것

- **조인 키의 유일성.** 규칙 `:569`가 이미 *"유일성 여부를 추측하지 마십시오"*라고 못박았다.
  재료는 키 목록만 준다.
- **WHERE 술어의 값·연산자.** 축 B가 이미 결론 낸 지점이다 — *"값까지 대조하면 노이즈"*
  (`SpecConditionColumnExtractor` 주석). 컬럼 이름과 파라미터 적용 여부까지만 본다.

## 단계 분할

성격이 달라 나뉜다. 중간에 멈춰도 손해가 없다.

| 단계 | 내용 | 채널 | 여기까지만 해도 얻는 것 |
|---|---|---|---|
| 1 | 설계 1 (파서·필터 4종) | `InputDefects` / 수정 | 거짓 자기참조·거짓 스키마 불일치 소멸, 대조 가능한 앵커 확보 (약 8건) |
| 2 | 설계 2 (재료 3종) | L1 오류 | A2 · A3 · A5 (약 15건) |
| 3 | 설계 3 (표 2종) | L1 오류 | 🔴 1건 + 🟠 4건 — **금액 영향 전부** |

## 오류 처리

이 설계가 추가하는 모든 경로는 AGENTS.md 범주 2를 따른다.

| 상황 | 처리 |
|---|---|
| `StaticAnalysis`가 없거나 파싱 실패 | 해당 재료 빈 목록. 프롬프트 항목과 L1 검사가 **함께** 미실행 (계약 3) |
| 추출기 자체 예외 | try-catch 격리, 소프트 패스. 재료를 빈 목록으로 두고 생성은 계속 |
| L1 검사기 자체 예외 | `MechanicalValidator.Validate`의 기존 catch-all이 소프트 패스 처리 (`:112-121`) |
| 파생 테이블 정의를 못 푼 경우 | 표에 행을 만들지 않는다. 못 푼 대상에 컬럼을 붙이지 않는다 (`RecordUpdateMapping` 가드와 같은 이유) |
| 취소 | `OperationCanceledException`은 소프트 페일 대상이 아님. `when (ex is not OperationCanceledException)` 필터 필수 (`CancellationPolicyTests`가 Roslyn으로 자동 검사) |

## 테스트

세 층으로 나눈다.

| 층 | 입력 → 출력 | 픽스처 |
|---|---|---|
| 추출기 | DDL 조각 → 사실 | 실제 SP DDL 발췌 |
| L1 검사 | 재료 + Spec 마크다운 → 오류 유무 | **양방향** — 결함 Spec은 잡고 골든 Spec은 통과 |
| 프롬프트 골든 | `SpDefinition` → `prompt-context.md` | 디스크에 이미 있는 산출물과 diff |

### 픽스처

`EXPECT_PROC:203-205` 한 문장이 설계 1의 세 항목을 동시에 검증한다. 특히 자기참조는
**참 1건(`OutState`)과 거짓 1건(`OutYMD`)이 같은 SET 절에 있어** "판정을 통째로 끄는" 편법으로는
통과할 수 없다.

프롬프트 골든 층은 특히 싸다. `prompt-context.md`가 이미 저장돼 있어 1.1을 고치면 diff에
`자기 자신을 참조합니다: OutState, OutYMD` → `... OutState`로 정확히 나타난다.

### 골든 케이스 — 반드시 통과해야 하는 것

감사가 `정합`으로 판정한 세 Spec을 세 단계 전부의 회귀 기준으로 고정한다.

- `UP_UTIL_SETTLE_CANCEL_INS`
- `UP_UTIL_SETTLE_INS_EXTRA4PLCARD` — ROUND 매핑 정확 기록 + 파서 오탐을 Spec이 정정한 반례
- `UP_Util_Settle_Summary_AcqManual`

**새 검사를 켰을 때 이 셋이 깨지면 검사가 틀린 것이다.**

## 닫지 못하는 것

보고서 §6이 한 것과 같은 이유로 정직하게 남긴다.

1. **파서 오수집 일반.** 보고서 §6-2가 지적한 사각지대다 — 축 A 감사조차 파서 자체의 오수집은
   잡지 못한다. 설계 1.1·1.3은 **알려진 두 건**을 고치는 것이지 오수집 일반을 막지 않는다.
   1.4의 표기 병기가 부분적 완화다 — 사람이 원문과 대조할 수 있게 된다.

2. **표 밖의 산문.** 표로 강제한 것만 보장된다. 나머지 서술은 여전히 비결정적이다.

3. **실행 대조가 아니다.** 보고서 §6-1과 같다. 문서가 옳아졌다는 것이 실행 결과가 같다는
   뜻이 아니다.

4. **A6의 청킹 리셋은 증상만 없앤다.** 1.2는 앵커를 라인으로 병기해 문제를 무해하게 만들지만,
   **청크 경계마다 파서 상태가 리셋되는 구조는 그대로다.** 다른 카운터가 생기면 같은 문제가
   재발한다. 근본 수정은 청킹 경로 전반의 재설계가 되고, 실제 피해가 앵커 하나였으므로 이번
   범위 밖에 둔다.

5. **축 B는 건드리지 않는다.** 별개 설계다. 다만 축 A가 고쳐지면 축 B의 기준값인 `Spec.md`가
   정확해지므로 **축 B 재감사 시 일부 판정이 바뀔 수 있다.**

## `docs/todo.md`의 유예를 깬다

이 설계는 todo.md가 *"프롬프트는 재생성 결과가 비결정적이라 별도 설계로 다룬다"*며 통째로
미뤄 둔 영역(`:12-17`)에 처음 들어간다.

유예를 깨는 근거는 설계 0의 계약이다 — **비결정성을 없애는 것이 아니라, 비결정적 산출물을
결정적으로 검사한다.** 프롬프트 규칙을 혼자 추가하는 일은 여전히 하지 않는다. 규칙과 검사가
같은 재료에서 나오는 한에서만 추가한다.

구현 시 todo.md의 해당 서술을 갱신한다.
