# 재료 분모 계기의 변이 검증 (2026-08-29)

`SpecMaterialCensus`·`StepSweepService`·`StepSweepReportWriter`의 재료 분모 절이 **0 을
인쇄하고 통과했다고 말하는 또 하나의 계기**(5-3-7)가 되지 않게, 판정 단위마다 변이를 넣고
어느 테스트가 죽는지 봤다.

**제품 코드는 전부 되돌렸다.** 이 태스크는 별도 워크트리를 새로 만들지 않았다 - 조율자가
이미 전용 격리 워크트리에 파견했으므로, 그 워크트리 안에서 직접 변이시키고
`git checkout -- .`로 되돌렸다. 변이마다 되돌린 뒤 `git status --short`로 원상 복귀를
확인했다(아래 각 변이 절 참고).

## 실행 조건

- 커밋: `eef4c2790519d630e6a3e9159c6400491eda731c`(WAVE_BASE, 변이 워크트리도 같은 커밋)
- 잰 시각: 2026-08-29 09:58 KST(시작) ~ 2026-08-29 10:04 KST(종료)
- 워크트리: `/Users/payletter/git-root/ReSet/.claude/worktrees/agent-a7cd10c96f0d54b9c`
- 대상 테스트: `SpecMaterialCensusTests`(census 계산) · `StepSweepServiceTests`(Sweep 이음매) ·
  `StepSweepReportWriterTests`(보고서 렌더링)
- 기준선(변이 없이, 이 워크트리에서 전체 스위트): 실패 0 · 통과 3176 · 건너뜀 0 · 빌드 경고 0

## 변이 여덟의 결과 — **넷 죽음 · 넷 생존**

| # | 변이 | 죽을 것으로 예상한 테스트 | 판정 | 실제로 죽은 테스트 |
| ---: | :--- | :--- | :--- | :--- |
| 1 | `Count`의 판 접기 제거(`ContainsKey` 가드 삭제) | `Count_FoldsTheSameProcedureAcrossJobs` | **생존** | 없음(10/10 통과) |
| 2 | 소실 조건을 `ddlCount > 0 && specCount == 0`에서 `specCount == 0`으로 넓힘 | `Count_WhenSpecHasTheTable_ReportsNoLoss` | **생존** | 없음(10/10 통과) |
| 3 | 「안 쟀음」 재료의 `DdlFactCount`를 `null` 대신 `0`으로 | `Count_ForMaterials…NotCountedThisRound…` 등 | 죽음 | `Count_ForMaterialsWithDdlCounterpartButNotCountedThisRound_LeavesDdlFactCountNull`, `Count_ForMaterialsWithNullDdlCounterpart_LeavesDdlFactCountNull` (2건) |
| 4 | `CountDeclaredVariables`가 커서 선언도 세게 만듦 | (조율자: 아무것도 안 죽을 것으로 의심) | **생존** | 없음(70/70 통과) |
| 5 | 판 접기 동점 규칙을 「첫 판 승」에서 「마지막 판 승」으로(=변이 1과 동일 코드) | (조율자: 아무것도 안 죽을 것으로 의심) | **생존** | 없음(70/70 통과) |
| 6 | `StepSweepService`의 census `try/catch` 제거 | `MaterialCensusFailureDoesNotCrashSweepAndOtherIndicatorsSurvive` | 죽음 | 위와 동일(1건) |
| 7 | 보고서 라벨을 `DdlFactCount?.ToString() ?? "잴 수 없음"`으로 되돌림 | `MaterialCensusSectionDistinguishesAllFourNullStates` | 죽음 | 위와 동일(1건) |
| 8 | `StepSweepService.Sweep`의 `MaterialCensus = …` 배선 제거 | `IndicatorsCarryMaterialCensusThroughSweep` | 죽음 | 위와 동일(1건) |

**안 죽은 변이 넷: 1·2·4·5.** 계획서가 낸 변이 셋 중 둘(1·2)이 생존했고, 조율자가 리뷰의
미결 항목을 측정으로 바꾸려고 더한 변이 다섯 중 둘(4·5)이 정확히 조율자가 예상한 대로
생존했다. **변이 1과 5는 실행해 보니 완전히 같은 코드 변경**이었다 - `SpecMaterialCensus.Count`가
`specByProcedure`/`ddlByProcedure`를 채울 때 쓰는 `ContainsKey` 가드를 지우면, 그것이 곧
「판 접기 제거」(변이 1이 겨눈 것)이자 「동점 규칙을 마지막 판 승으로 바꾸는 것」(변이 5가
겨눈 것)이다 - 두 서술이 가리키는 자리가 하나였다.

## 넷 다 되돌린 것을 확인했다

변이마다 `git checkout -- <파일>` 직후 `git status --short`로 되돌아왔는지 확인했다. 매
변이 뒤 출력은 비어 있었다(추적되는 파일에 남은 변경 없음) - 아래는 그 확인이 반복된
로그의 요지다. 최종적으로 이 태스크가 커밋하는 시점의 상태:

```
$ git status --short
$ git diff --stat HEAD
 tests/ReSet.Core.Tests/SpecMaterialCensusTests.cs | 82 +++++++++++++++++++++++
 1 file changed, 82 insertions(+)
```

제품 코드(`src/`)는 **한 줄도 안 바뀌었다** - 82줄은 전부 새 테스트 셋이다.

## 안 죽은 변이 넷을 어떻게 처리했는가

「안 죽은 변이는 테스트의 결함이지 변이의 결함이 아니다」라는 원칙에 따라, 넷 모두 **테스트를
보강했다.** 각 변이를 보강 전/후로 다시 넣어 판정이 뒤집히는지 확인했다.

### 변이 4 — `CountDeclaredVariables`의 커서 방어를 잠그는 테스트가 없었다

`DeclareCursorStatement`의 자식은 `Name`·`CursorDefinition`뿐이라 `DeclareVariableElement`와
구조적으로 분리돼 있다 - "커서를 안 센다"는 오늘 참이지만, 그 참을 잠그는 단언이 이 태스크
이전에는 없었다. `Visit(DeclareCursorStatement)`를 추가해 커서 이름도 세게 만드는 변이를
넣었더니 census 스위트 70개가 전부 그대로 통과했다(조율자의 예상 그대로).

**보강**: `CountDeclaredVariables_DoesNotCountCursorDeclarations`를 추가했다 - `DECLARE`
값 변수 하나와 `DECLARE CURSOR` 하나를 함께 담은 DDL을 넣고 `CountDeclaredVariables`가
`1`(커서 제외)을 내는지 확인한다.

- 보강 전: 기준선(변이 없음) 통과, 변이 4 적용 후에도 통과(생존) - 기존 테스트 스위트로는
  이 변이를 잡을 수 없음을 재확인.
- 새 테스트 추가 후 기준선: **통과**(13/13, 제품 코드 무변경).
- 변이 4를 다시 넣은 뒤: **`CountDeclaredVariables_DoesNotCountCursorDeclarations` 실패**
  (12 통과, 1 실패) - 이번엔 죽는다.
- 변이를 되돌리고 `git status --short`로 원상 복귀 확인.

### 변이 2 — DDL도 명세서도 표가 없는 「진짜 무해」 케이스를 잠그는 테스트가 없었다

기존 소실 조건 `ddlCount > 0 && specCount == 0`을 `specCount == 0`으로 넓혀도 기존
픽스처는 전부 `DdlWithTwoDeclares`(ddlCount == 2 > 0)만 썼으므로 `&&`가 지워져도 판정이
안 바뀌었다. 실물에서는 지역 변수를 아예 안 쓰는 프로시저(DDL에도 명세서에도 표가 없는
흔한 경우)가 이 넓어진 조건에서 **거짓 소실**로 잡힌다.

**보강**: `Count_WhenDdlHasNoFactsAndSpecHasNone_DoesNotReportLoss`를 추가했다 - DDL에
`DECLARE`가 하나도 없고(`DdlFactCount == 0`) 명세서에도 지역 변수 표가 없는(`SpecRowCount
== 0`) 프로시저가 소실 목록에 안 들어가는지 확인한다.

- 새 테스트 추가 후 기준선: **통과**(13/13).
- 변이 2(`specCount == 0`만으로 넓힘)를 다시 넣은 뒤:
  **`Count_WhenDdlHasNoFactsAndSpecHasNone_DoesNotReportLoss` 실패**(12 통과, 1 실패).
- 변이를 되돌리고 원상 복귀 확인.

### 변이 1·5(같은 코드) — 판 접기의 동점 규칙(첫 판 승)을 잠그는 테스트가 없었다

`Count_FoldsTheSameProcedureAcrossJobs`는 Job 셋의 DDL이 바이트 동일해서 `ContainsKey`
가드를 지워도(=매번 덮어써 마지막 판이 이기게 해도) 통과했다 - 중복 카운팅 방지는
`ContainsKey` 가드가 아니라 **`Dictionary`가 프로시저 이름을 유일 키로 접는 구조 자체**에서
나온다. 가드가 실제로 결정하는 것은 Job마다 내용이 "다를" 때 어느 판이 남는가뿐이다.

이 배경 노트가 지적한 대로, **실물에서는 한 스윕 안에서 같은 프로시저가 언제나 같은 단일
Spec.md/DDL을 읽어 내용이 바이트 동일하므로 이 분기는 오늘의 코퍼스로는 도달 불가하다.**
그럼에도 **테스트로 잠그는 쪽을 골랐다** - 이 태스크의 쓰기 집합에 테스트 파일이 명시적으로
포함돼 있고, 합성 픽스처(서로 다른 DDL을 가진 두 Job)로 정책 자체는 검증 가능했기
때문이다. 판단 근거는 아래 CONCERNS에도 남긴다.

**보강**: `Count_WhenProcedureAppearsInMultipleJobsWithDifferentDdl_FirstJobWins`을
추가했다 - `DECLARE` 하나짜리 DDL을 가진 JobA와 `DECLARE` 둘짜리 DDL을 가진 JobB가 같은
프로시저 이름으로 들어왔을 때, `DdlFactCount`가 `1`(JobA, 첫 판)이어야 한다고 잠근다.

- 새 테스트 추가 후 기준선: **통과**(13/13).
- 변이 1/5(가드 삭제)를 다시 넣은 뒤:
  **`Count_WhenProcedureAppearsInMultipleJobsWithDifferentDdl_FirstJobWins` 실패**
  (12 통과, 1 실패).
- 변이를 되돌리고 원상 복귀 확인.

## 죽은 변이 넷 — 계획서와 조율자가 겨눈 자리를 그대로 잡았다

- **변이 3**(「안 쟀음」을 `0`으로): `SpecMaterialCensusTests`에서 2건 실패
  (`Count_ForMaterialsWithDdlCounterpartButNotCountedThisRound_LeavesDdlFactCountNull`,
  `Count_ForMaterialsWithNullDdlCounterpart_LeavesDdlFactCountNull`).
- **변이 6**(census `try/catch` 제거): `StepSweepServiceTests`에서
  `MaterialCensusFailureDoesNotCrashSweepAndOtherIndicatorsSurvive` 1건 실패.
- **변이 7**(라벨을 `잴 수 없음`으로 뭉갬): `StepSweepReportWriterTests`에서
  `MaterialCensusSectionDistinguishesAllFourNullStates` 1건 실패.
- **변이 8**(`MaterialCensus = …` 배선 제거): `StepSweepServiceTests`에서
  `IndicatorsCarryMaterialCensusThroughSweep` 1건 실패.

## 되돌린 뒤 전체 게이트 재확인

보강한 테스트 세 개를 포함해, 제품 코드가 기준선(`eef4c27`)과 완전히 같은 상태에서 전체
스위트를 다시 돌렸다.

```
$ dotnet build tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj
빌드했습니다. 경고 0개 오류 0개

$ dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj -v quiet
통과! - 실패: 0, 통과: 3179, 건너뜀: 0, 전체: 3179
```

기준선 3176 + 새 테스트 3 = 3179. 실패 0 · 건너뜀 0 · 빌드 경고 0 - 게이트를 만족한다.
(코퍼스 테스트가 건너뛰지 않도록 `output`·`output.bak-2026-08-22`를 메인 체크아웃에서
이 워크트리로 심링크했다 - 건너뜀 0을 눈으로 확인했다.)

## 잰 것과 안 잰 것

- **잰 것**: 변이 여덟 전부(계획서 셋 + 조율자 다섯)를 이 워크트리에서 실제로 넣고 대상
  테스트를 돌렸다. 생존한 넷(1·2·4·5)에 대해 테스트를 보강하고, 보강 전/후 각각
  변이를 다시 넣어 판정이 뒤집히는 것까지 확인했다(보강 전 생존 → 보강 후 사망, 4쌍
  모두). 보강 뒤 제품 코드를 기준선으로 되돌린 상태에서 전체 스위트(3179개)와 빌드
  경고(0개)를 재확인했다.
- **안 잰 것**: `SpecStatementFactsExtractor`·`SqlStaticParser` 등 census가 위임하는
  하위 리더 자체에 대한 변이(이 태스크는 `SpecMaterialCensus`·`StepSweepService`·
  `StepSweepReportWriter`·census 테스트만 겨눴다) · 실물 코퍼스로 판 접기 동점 규칙이
  실제로 관찰되는지(변이 1/5의 배경 노트대로 오늘은 도달 불가) · Task 6이 잴 실물 스윕
  수치.
