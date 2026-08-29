# 지역 변수 표 강제 — 변이 검증 (Task 9)

**잰 시각:** 2026-08-29 15:01 KST
**커밋 해시(`main`에서 도달 가능):** `0a3469b3` (`docs: 캐시 18 승격 근거를 Task 7의 실측으로 바꾼다`)
**작업 트리 청결도:** 변이 실험은 전부 되돌렸다. 최종 상태에서 `git diff HEAD`는
`src/` 전체가 비어 있다 — 이 보고서가 의존하는 변이 실험은 이 문서와 아래 커밋
로그로만 남는다. 살아남은 변이 하나(9번)의 보강만 `tests/`에 실물로 남는다.

## 맨 앞 — 세 가지 대답

1. **몇이 죽고 몇이 살았는가.** 최초 열셋 중 **열둘이 즉시 죽었다.** **하나(변이 9 —
   렌더러가 `InitialValue` 칸을 언제나 빈 칸으로 냄)가 살아남았다.** 픽스 라운드
   2에서 Task 6의 고유 값을 실측으로 확정하기 위해 **변이 14(리더 쪽)를 추가**했고
   — 이것도 죽었다(9개 테스트, 본문 §2 참고). **합산 열넷 중 열셋 죽음·하나 생존.**
   (**픽스 라운드 3 — 사후 정정**: 죽음/생존 수는 이 라운드에서 안 바뀌었지만,
   변이 2·14 결과에서 "Task 6은 값이 거의 없다"로 읽을 만한 결론이 있었다면
   그것은 **과소평가였다.** 변이 1(상수-축)에서는 Task 6의 테스트 둘이 이
   계획보다 앞선 리더 테스트 여섯 개가 전혀 못 잡는 실패군 — **이 계획이
   존재하는 이유인 (5-3-7) 그 자체** — 을 고유하게 잡는다. 축별 상세는
   본문 §2와 CONCERNS의 픽스 라운드 3 항목 참고.)
2. **예측이 빗나간 자리.**
   - **변이 1 (픽스 라운드 1에서 재실측 — 최초 보고가 불완전했다)**: 계획서는
     "이음매 테스트 · 카탈로그 테스트"가 죽는다고 적었다. **최초 보고는 이 변이를
     `--filter LocalVariable`로만 돌려** 클래스명에 "LocalVariable"이 없는
     `MachineConfirmedTablesExpansionTests`를 실행 대상에서 빠뜨렸고, 그 결과
     "카탈로그 테스트는 구조적으로 안 죽는다"는 과장된 결론을 냈다. **전건
     테스트(`dotnet test tests/ReSet.Core.Tests --no-build`, 필터 없음)로 다시
     돌려 확인한 사망 목록은 다섯이다**: `LocalVariableDeclarationExtractorTests.TableHeading_ShouldUseTheSharedSuffix` ·
     `LocalVariableTableSeamTests.TheMachineHeading_ShouldBeReadableByTheCheckDReader` ·
     `...TheRenderedTable_ShouldBeReadableByTheCheckDReader` ·
     `...TheMachineHeading_ShouldStartWithAKnownReaderPrefix` ·
     **`MachineConfirmedTablesExpansionTests.CriticExemptionBlock_ShouldCoverTheLocalVariableTable`**.

     카탈로그 쪽 테스트 자체도 갈린다. `All_ShouldContainTheLocalVariableTable` ·
     `All_ShouldContainTheLocalVariableTableAtTheEnd` ·
     `All_ShouldAppendTheLocalVariableTableAfterTheErrorCodeTable`와 반사 불변식
     `MachineConfirmedTablesTests.EveryMachineConfirmedHeadingConstant_IsRegisteredInTheCatalog`
     넷은 전부 `LocalVariableDeclarationExtractor.TableHeading` **자기 자신을 읽어**
     `MachineConfirmedTables.All`과 대조하므로, 상수 값이 무엇으로 바뀌든 양쪽이
     같은 값을 보는 자기참조 비교가 되어 언제나 통과한다 — 이 넷은 실제로 안
     죽었다(재실측으로 확인). 반면 `CriticExemptionBlock_ShouldCoverTheLocalVariableTable`은
     `Assert.Contains("지역 변수", MachineConfirmedTables.CriticExemptionBlock)`로
     한글 리터럴 `"지역 변수"`를 **손으로 박아** 뒀다 — 헤딩이 "로컬 변수"로
     바뀌면 그 부분 문자열이 블록 텍스트에서 사라져 죽는다. **다만 이것은 설계된
     헤딩-개명 방어가 아니라 작성 방식의 우연이다.** 이 테스트의 목적은 "새
     표가 Critic 면제 블록에 실리는가"이지 "헤딩 이름이 안 바뀌었는가"가 아니다
     — 다음 사람이 이 테스트를 헤딩 개명 방어로 믿고 의지하면 안 된다.

     **Task 6에 대한 인과 — 두 번째 정정(픽스 라운드 2).** 픽스 라운드 1이 위에
     적은 "변이 2를 잡는 셋은 전부 Task 6 계열 커밋에서 나왔다"도 **틀렸다** —
     이번에도 `git log`의 커밋 메시지·파일명만 보고 귀속했지 **그 커밋의 실제
     diff를 열어보지 않았다.** `git show e00f787d`로 직접 확인한 결과:

     - `WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows` ·
       `FunctionBranch_ShouldCarryTheTableWithItsHeadingAndRows`는 **Task 4**의
       커밋 `e00f787d`(13:49:23, `LocalVariableTablePromptTests.cs`를 새로 만든
       커밋)에서 **처음부터**
       `Assert.Contains("| 변수 명칭 | 데이터 타입 | 초기값 |", prompt)`를
       갖고 있었다 — Task 6의 첫 커밋 `86210730`(14:25:43)보다 앞선다.
     - `TheRenderedTable_ShouldBeReadableByTheCheckDReader`만 **Task 6** 계열이
       맞다 — `LocalVariableTableSeamTests.cs` 자체가 `86210730`에서 신설됐고,
       이 테스트는 그 파일 안에서 `3ec3d3f9`가
       `TheMachineHeader_ShouldCarryTheTwoColumnFragmentsTheReaderLooksFor`를
       재작성해 만든 것이다(`git show 3ec3d3f9`로 확인).

     그러므로 **"Task 6이 없었으면 변이 2는 전 테스트를 통과했을 것"도 거짓이다**
     — Task 4의 두 테스트가 Task 6과 무관하게 이미 그 변이를 잡는다. Task 6이
     변이 2에서 고유하게 더한 것은 `TheRenderedTable_ShouldBeReadableByTheCheckDReader`
     하나뿐이다.

     **리더 쪽 변이(신규 — 변이 14)로 Task 6의 고유 값을 직접 쟀다.** 위 정정이
     추측으로 끝나지 않도록, 이번 라운드에서 실제로 리더를 망가뜨리는 변이를
     추가해 돌렸다: `SpecStatementFactsExtractor.LocalVariableHeadingPrefixes`에서
     `"### 지역 변수"`를 빼고(`"### 내부 변수"`만 남김) 렌더러·상수·카탈로그는
     하나도 안 건드린 채 전건 재실행. 죽은 테스트 아홉:
     `LocalVariableTableSeamTests.TheRenderedTable_ShouldBeReadableByTheCheckDReader` ·
     `...TheMachineHeading_ShouldBeReadableByTheCheckDReader` ·
     `...TheMachineHeading_ShouldStartWithAKnownReaderPrefix` ·
     `SpecMaterialCensusTests.Count_WhenSpecHasTheTable_ReportsNoLoss` ·
     `SpecStatementFactsExtractorTests.SystemValues_AreMarkedAndNotTreatedAsLocalVariables` ·
     `...LocalVariables_RecognizeExpectProcTypeOnlyHeaderAndSystemIntegerMarker` ·
     `...LocalVariables_CommUpdShape_StillReadsAndConditionHeaderCorrectly` ·
     `...LocalVariables_RecognizeProcEtcHeadingAndTypeOnlyHeader` ·
     `...LocalVariables_RecognizeAcqManualHeadingAndSystemStateMarker`.

     **정정(픽스 라운드 3) — 위 결론("리더 축에서는 Task 6의 고유 기여가 없다")은
     과소평가였다.** 변이 1(상수 개명)과 변이 14(접두사 배열에서 원소 제거)는
     서로 다른 축을 건드리는데, 그 차이를 이 문서 안의 데이터끼리도 대조하지
     않고 "겹친다"로 뭉뚱그렸다. 같은 여섯 테스트가 두 변이에서 실제로 어떻게
     갈리는지 이 보고서 자신의 MUTATION TABLE 데이터로 대조하면:

     | 테스트(군) | 변이 1(production 상수 `TableHeading` 개명) | 변이 14(리더 접두사 **배열**에서 원소 제거) |
     | :--- | :--- | :--- |
     | `SpecStatementFactsExtractorTests` 다섯 | **안 죽는다** | **죽는다** |
     | `SpecMaterialCensusTests.Count_WhenSpecHasTheTable_ReportsNoLoss` | **안 죽는다** | **죽는다** |
     | `LocalVariableTableSeamTests.TheMachineHeading_ShouldBeReadableByTheCheckDReader` | **죽는다** | 죽는다 |
     | `LocalVariableTableSeamTests.TheMachineHeading_ShouldStartWithAKnownReaderPrefix` | **죽는다** | 죽는다 |

     **왜 갈리는가.** 앞선 여섯(`SpecStatementFactsExtractorTests` 다섯 ·
     `SpecMaterialCensusTests` 하나)의 픽스처를 열어 보면(`grep -n "지역 변수"
     tests/ReSet.Core.Tests/SpecStatementFactsExtractorTests.cs`)
     `LocalVariableDeclarationExtractor.TableHeading` **상수를 전혀 참조하지
     않는다** — 코퍼스에서 오려온 독립 리터럴 헤딩(`"### 지역 변수 및 시스템
     값"`·`"### 지역 변수와 컬럼 매핑"`·`"### 지역 변수 및 시스템 상태값"`)을
     손으로 박아 두고 `LocalVariableHeadingPrefixes` 배열과의 `StartsWith`
     일치만 본다. 그래서 상수(`TableHeading`)가 "로컬 변수"로 개명돼도
     이 여섯은 그 상수를 아예 안 보므로 무영향이고, 배열 자체에서 원소가
     빠지는 변이 14에서만 함께 죽는다 — "겹친다"는 **배열-축(변이 14)에서만
     참이고, 상수-축(변이 1)에서는 거짓이다.**

     **그리고 상수-축이 정확히 (5-3-7)의 실패 양식이다** — 생산 코드의 헤딩
     상수가 리더가 아는 어떤 접두사와도 안 맞게 되는 것(모델 교체가 실제로
     그렇게 냈다). `LocalVariableTableSeamTests`의 클래스 주석이 스스로 그
     갭("검사 D의 리더에 실제로 닿는지")을 겨냥한다고 밝힌다.
     `LocalVariableDeclarationExtractorTests.TableHeading_ShouldUseTheSharedSuffix`는
     상수 값만 보고 리더를 아예 안 부르므로 그 갭을 못 채운다 — 상수-축에서
     리더까지 실제로 불러 검증하는 것은 `LocalVariableTableSeamTests`뿐이다.

     **TASK 6 UNIQUE VALUE(실측 결론, 축별로 가름).**
     - **변이 2·14 축(렌더러 헤더 칸 이름 · 리더 접두사 배열)**에서 Task 6이
       고유하게 잡는 것은 `TheRenderedTable_ShouldBeReadableByTheCheckDReader`
       하나다 — 이 축에서는 이 계획 이전의 리더 단위 테스트 여섯 개와
       Task 4의 두 테스트가 각자 다른 조각을 이미 덮고 있어서다.
     - **변이 1 축(production 헤딩 상수 개명)**에서는
       `TheMachineHeading_ShouldBeReadableByTheCheckDReader`와
       `TheMachineHeading_ShouldStartWithAKnownReaderPrefix`가 **선행 여섯이
       전혀 못 잡는 실패군을 고유하게 잡는다** — 근거는 위 표: 변이 1에서
       선행 여섯은 전부 통과하고 이 둘만(그리고 `TheRenderedTable_…`도) 죽는다.
       이 실패군이 정확히 (5-3-7)의 모양이다.
     - 두 `TheMachineHeading_…`은 **잡는 실패군이 같고 진단 폭만 다르다** —
       `...ShouldBeReadableByTheCheckDReader`는 손으로 쓴 표를 리더에 먹여
       종단으로 확인하고, `...ShouldStartWithAKnownReaderPrefix`는 리플렉션으로
       접두사 배열만 격리해 상수가 그 배열의 원소로 시작하는지만 본다. 이
       차이는 Minor한 진단 편의(어느 쪽이 실패해도 원인 좁히기가 다를 뿐)이지
       별개의 방어선이 아니다.
     - **요약: Task 6은 두 축 모두에서 값이 있다** — 변이 2·14 축에서는
       "렌더러 실제 출력을 실제 리더에 먹인다"는 종단 결합을 유일하게
       제공하고, 변이 1 축에서는 "생산 헤딩 상수가 리더의 접두사와 실제로
       맞물리는지"를 유일하게 검증한다(이 계획이 시작된 원인 그 자체,
       known-defects (5-3-7)).

     **같은 오류가 세 번 반복됐다.**
     1. `--filter LocalVariable`가 사망 목록을 잘라냈다(라운드 1 이전 — 원 보고).
     2. 커밋 메시지 제목과 파일 이름만으로 테스트의 소속(Task 4 vs Task 6)을
        추정했다(라운드 1의 정정 자체에서 새로 남).
     3. **부분 증거(변이 2·14)로 "이 계획 전체에서"를 일반화하면서, 같은
        문서 안에 이미 있는 반증 데이터(변이 1에서 선행 여섯이 살아남는다는,
        라운드 1이 스스로 재확인한 사실)를 대조하지 않았다**(라운드 2).

     셋을 관통하는 지침: **"어느 테스트가 무엇을 잡는가"는 실행 결과와
     `git show`/`git log -p`로 그 커밋의 실제 diff를 열어 확인해야 하고,
     필터 문자열이나 커밋 메시지·파일명으로 추정하면 안 된다.** 그리고
     이번 라운드가 더한 것: **결론을 쓰기 전에 같은 문서 안의 다른 측정과
     대조하라 — 새 측정이 옛 측정을 뒤집는지, 아니면 축이 달라서 둘 다
     참인지 먼저 보라.** 부분 증거의 결론(가령 "변이 X에서 A만 고유하게
     죽는다")을 전체 문장("이 계획 전체에서 A만 고유하다")으로 승격하기 전에,
     보고서 안의 다른 변이·다른 축 데이터가 그 승격을 반증하지 않는지
     반드시 되짚어야 한다. **이 문장이 이 보고서가 다음 회차에 남길 가장
     값진 줄이다.**
   - **변이 8**: 계획서는 `WhenTheHeadingIsMissing_ShouldReportOnce` 하나만
     꼽았는데, 실측은 그 테스트에 더해 `LocalVariableTableCorpusTests`(코퍼스
     31 객체 전건 검사)도 함께 죽였다 — 그 코퍼스 테스트가
     `ErrorType.LocalVariableTableMismatch`로 발화를 걸러 세기 때문이다. 예측보다
     방어가 넓었다(과소 예측, 나쁜 방향 아님).
   - **변이 12**: 계획서가 꼽은 `Extract_ShouldNotReturnProcedureParameters`에
     더해 `Extract_ShouldReturnNameTypeAndInitialValue`도 함께 죽었다 — 그 DDL도
     OUTPUT 파라미터를 하나 갖고 있어 사실 수가 2 → 3으로 튀기 때문이다. 역시
     과소 예측.
   - **변이 9가 실제로 살아남았다** — 계획서가 「직전 회차 교훈 반영, 표시
     계층」이라며 특별히 지목한 자리(★)였고, 그 지목이 정확히 들어맞았다.
     아래 SURVIVORS 절에 상세 기록.
3. **보강 뒤 다시 죽는 것을 확인했는가.** **예.** 변이 9의 보강(행 모양 전체
   단언)을 넣은 뒤: (a) 무변이 상태에서 보강 테스트가 초록임을 먼저 확인하고,
   (b) 같은 변이(InitialValue를 언제나 빈 칸으로)를 다시 넣어 보강된
   `WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows`·
   `FunctionBranch_ShouldCarryTheTableWithItsHeadingAndRows` 둘 다 빨개지는 것을
   실측했다. 그 뒤 프로덕션 변이만 되돌리고 보강은 남겼다.

## ★ 직전 회차 권고의 인과

직전 회차 보고서는 "변이 여덟이 전부 계수 로직을 겨눴는데 살아남은 결함 둘은
둘 다 표시 계층이었다"고 적고, 다음 회차 변이 목록은 "출력 칸마다 하나씩"으로
짜라고 권고했다. **이 회차의 변이 9(★ 표시)가 그 권고를 따라 넣은 것이고, 그것이
진짜 생존자였다.** 계수 로직을 겨눈 변이 열두 개(1~8, 10~13)는 전부 즉사했다 —
이 축의 계수 로직(추출·등록·프롬프트 배선·L1 양방향·널 체인·캐시 버전)은 이제
탄탄하다. **틈은 여전히 표시 계층에 있다** — "표에 무엇이 실리는가"를 세는
테스트는 많은데 "표의 각 칸에 무엇이 적히는가"를 통째로 대조하는 테스트가
얇았다. 직전 회차의 권고가 값을 냈다.

## MUTATION TABLE

| # | 변이 | 예상 | 실측 | 죽은 테스트 |
| ---: | :--- | :--- | :--- | :--- |
| 1 | `TableHeading`을 `"### 로컬 변수 " + HeadingSuffix`로(production 상수) | 이음매·카탈로그 테스트 | **죽음** — 카탈로그 테스트 중 자기참조 넷(`All_ShouldContainTheLocalVariableTable` 등 + 반사 불변식)은 안 죽고, 한글 리터럴을 손으로 박은 하나(`CriticExemptionBlock_ShouldCoverTheLocalVariableTable`)는 우연히 죽는다(전건 재실행으로 재확인, 위 참고) | `LocalVariableDeclarationExtractorTests.TableHeading_ShouldUseTheSharedSuffix` · `LocalVariableTableSeamTests.TheMachineHeading_ShouldBeReadableByTheCheckDReader` · `...TheRenderedTable_ShouldBeReadableByTheCheckDReader` · `...TheMachineHeading_ShouldStartWithAKnownReaderPrefix` · `MachineConfirmedTablesExpansionTests.CriticExemptionBlock_ShouldCoverTheLocalVariableTable` |
| 2 | 렌더러 헤더 `변수 명칭` → `변수 이름` | 이음매 종단 테스트 | **죽음**, 단 셋 중 둘(`WholeSpBranch_…`·`FunctionBranch_…`)은 Task 4 소속, Task 6 고유는 `TheRenderedTable_…` 하나뿐(픽스 라운드 2에서 재귀속) | `LocalVariableTableSeamTests.TheRenderedTable_ShouldBeReadableByTheCheckDReader` · `LocalVariableTablePromptTests.WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows` · `...FunctionBranch_ShouldCarryTheTableWithItsHeadingAndRows` |
| 3 | `MachineConfirmedTables.All`에서 등록 제거 | 확장 테스트 넷 + 반사 불변식 | **죽음**, 예측 정확히 일치 | `MachineConfirmedTablesExpansionTests.All_ShouldContainTheLocalVariableTableAtTheEnd` · `...All_ShouldContainTheLocalVariableTable` · `...All_ShouldAppendTheLocalVariableTableAfterTheErrorCodeTable` · `...CriticExemptionBlock_ShouldCoverTheLocalVariableTable` · `MachineConfirmedTablesTests.EveryMachineConfirmedHeadingConstant_IsRegisteredInTheCatalog` |
| 4 | `OverviewAndParameters` 갈래를 `Omit`으로 | `OverviewAndParametersBranch_ShouldCarryTheTable` | **죽음**, 예측 정확히 일치 | `LocalVariableTablePromptTests.OverviewAndParametersBranch_ShouldCarryTheTable` |
| 5 | `CrudAnalysis` 갈래를 `Table`로 | `BranchesThatCannotWriteParameterList_…` | **죽음**, 예측 정확히 일치 | `LocalVariableTablePromptTests.BranchesThatCannotWriteParameterList_ShouldNotCarryTheTable(sectionType: "CrudAnalysis")` |
| 6 | L1 역방향 절 무력화 | `WhenTheTableHasAnInventedRow_ShouldReportIt` | **죽음**, 예측 정확히 일치 | `LocalVariableTableL1Tests.WhenTheTableHasAnInventedRow_ShouldReportIt` |
| 7 | L1 정방향 절 무력화 | `WhenARowIsMissing…` · `WhenADeclaredTypeIsChanged…` | **죽음**, 예측 정확히 일치 | `LocalVariableTableL1Tests.WhenARowIsMissing_ShouldReportThatVariable` · `...WhenADeclaredTypeIsChanged_ShouldReportThatVariable` |
| 8 | `ErrorType.LocalVariableTableMismatch` → `General` | `WhenTheHeadingIsMissing_ShouldReportOnce` | **죽음**, 예측보다 넓음(과소 예측) | `LocalVariableTableL1Tests.WhenTheHeadingIsMissing_ShouldReportOnce` · `LocalVariableTableCorpusTests.LocalVariableTable_RenderedFromDdl_IsAcceptedByTheCheck` |
| 9 | 렌더러가 `InitialValue`를 언제나 빈 칸으로 | 프롬프트 테스트의 행 단언 | **생존** → 보강 → 보강 뒤 재확인 사망 | (생존 시점엔 없음) → 보강 후 `LocalVariableTablePromptTests.WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows` · `...FunctionBranch_ShouldCarryTheTableWithItsHeadingAndRows` |
| 10 | `SpecExpectations`의 널 체인 항 제거 | `From_WhenLocalVariablesAreTheOnlyMaterial_ShouldNotReturnNull` | **죽음**, 예측 정확히 일치 | `SpecExpectationsLocalVariableTests.From_WhenLocalVariablesAreTheOnlyMaterial_ShouldNotReturnNull` |
| 11 | 추출기의 이름 접기(`_seen.Add`) 제거 | `Extract_ShouldFoldRepeatedNamesKeepingTheFirst` | **죽음**, 예측 정확히 일치 | `LocalVariableDeclarationExtractorTests.Extract_ShouldFoldRepeatedNamesKeepingTheFirst` |
| 12 | 추출기의 `if (node is ProcedureParameter) return;` 제거 | `Extract_ShouldNotReturnProcedureParameters` + 코퍼스 CENSUS DELTA | **죽음**, 예측보다 넓음(과소 예측) + CENSUS DELTA 40→69 관측 | `LocalVariableDeclarationExtractorTests.Extract_ShouldNotReturnProcedureParameters` · `...Extract_ShouldReturnNameTypeAndInitialValue` |
| 13 | `CacheManager.CurrentCacheFormatVersion`을 17로 | `CacheManagerTests.UpdateCache_StampsTheCurrentFormatVersion` | **죽음**, 예측 정확히 일치(락 테스트 실재 확인) | `CacheManagerTests.UpdateCache_StampsTheCurrentFormatVersion` |
| 14(신규, 픽스 라운드 2) | `SpecStatementFactsExtractor.LocalVariableHeadingPrefixes`에서 `"### 지역 변수"` 제거(리더 쪽, 렌더러·상수·카탈로그는 불변) | (신규 - Task 6의 고유 값을 실측으로 확정하기 위해 추가) | **죽음, 9개** — `LocalVariableTableSeamTests` 셋(Task 6 소속) + 이 계획보다 앞선 리더 단위 테스트 여섯 개. **이 변이(배열-축) 하나만 보면 중복이지만, 변이 1(상수-축)에서는 그 여섯이 안 죽고 Task 6의 둘만 고유하게 죽는다 — 픽스 라운드 3의 AXIS TABLE·TASK 6 UNIQUE VALUE 참고, 「Task 6 고유 기여 없음」은 과소평가였다.** | `LocalVariableTableSeamTests.TheRenderedTable_ShouldBeReadableByTheCheckDReader` · `...TheMachineHeading_ShouldBeReadableByTheCheckDReader` · `...TheMachineHeading_ShouldStartWithAKnownReaderPrefix` · `SpecMaterialCensusTests.Count_WhenSpecHasTheTable_ReportsNoLoss` · `SpecStatementFactsExtractorTests.SystemValues_AreMarkedAndNotTreatedAsLocalVariables` · `...LocalVariables_RecognizeExpectProcTypeOnlyHeaderAndSystemIntegerMarker` · `...LocalVariables_CommUpdShape_StillReadsAndConditionHeaderCorrectly` · `...LocalVariables_RecognizeProcEtcHeadingAndTypeOnlyHeader` · `...LocalVariables_RecognizeAcqManualHeadingAndSystemStateMarker` |

## SURVIVORS

**변이 9만 살아남았다.**

### 왜 안 죽었는가

`BuildLocalVariableTableLines`(`AiService.cs`)의 행 렌더 줄을

```csharp
$"   | {EscapeTableCell(fact.Name)} | {EscapeTableCell(fact.DataType)} | {EscapeTableCell(fact.InitialValue)} |"
```

에서

```csharp
$"   | {EscapeTableCell(fact.Name)} | {EscapeTableCell(fact.DataType)} |  |"
```

로 바꿔 `InitialValue` 칸을 항상 비웠다. 이 변이를 넣은 채
`dotnet test --filter LocalVariable`을 돌렸더니 **43개 테스트가 전부 초록**이었다.

기존 `LocalVariableTablePromptTests.WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows`·
`FunctionBranch_ShouldCarryTheTableWithItsHeadingAndRows`는

```csharp
Assert.Contains(LocalVariableDeclarationExtractor.TableHeading, prompt);
Assert.Contains("| 변수 명칭 | 데이터 타입 | 초기값 |", prompt);
Assert.Contains("@v_intCLTotal", prompt);
Assert.Contains("MONEY", prompt);
```

넷만 단언했다 — 헤딩·헤더 리터럴·변수명·타입 문자열은 각각 프롬프트 어딘가에
있기만 하면 통과였고, `InitialValue` 칸(`0`)이 실제로 그 행에 실렸는지는 아무도
보지 않았다. `LocalVariableTableL1Tests`·`LocalVariableTableCorpusTests`는
L1 검사와 코퍼스를 보지만 **렌더러가 실제로 무엇을 내는지는 안 본다**(코퍼스
테스트는 `PerfectTranscription`을 직접 조립해 렌더러를 부르지 않는다 - 그 파일
자체의 주석이 "렌더러의 버그가 검사의 버그를 가려 준다"며 그 선택을 명시한다).
`LocalVariableTableSeamTests.TheRenderedTable_ShouldBeReadableByTheCheckDReader`는
렌더러를 실제로 부르지만, 그 테스트가 대조하는 것은 "리더가 이름·타입을
읽는가"이지 초기값 칸의 내용이 아니다(`SpecStatementFactsExtractor`의
`LocalVariableFact`에는 애초에 초기값 필드가 없다 - 리더가 그 칸을 읽지
않는다). 그래서 **렌더러가 초기값 칸을 지워도 리더 쪽에서는 관측할 길이
없다** - 이 결함은 오직 "프롬프트 문자열 자체"를 보는 테스트만 잡을 수 있다.

### 보강 내용

`tests/ReSet.Core.Tests/LocalVariableTablePromptTests.cs`의 두 테스트에
행 전체 모양을 통째로 대조하는 단언을 더했다:

```csharp
Assert.Contains("| @v_intCLTotal | MONEY | 0 |", prompt);
```

`Ddl` 상수가 `DECLARE @v_intCLTotal MONEY = 0`이므로 `InitialValue`는 `"0"`이고,
이 행 리터럴은 이름·타입·초기값 세 칸을 한 번에 잠근다.

### 보강 뒤 재확인

1. **무변이 상태**에서 보강 테스트 5개(`LocalVariableTablePromptTests` 전체)
   실행 → 전부 초록(`통과: 5, 실패: 0`) — 보강이 실물에서 공허하게 항상
   참이 아님을 확인.
2. **같은 변이를 다시 넣고** 같은 필터로 재실행 → 보강한 두 테스트가
   정확히 빨개짐:
   ```
   Not found: "| @v_intCLTotal | MONEY | 0 |"
   ```
3. 프로덕션 변이만 `git checkout -- src/ReSet.Core/Services/AiService.cs`로
   되돌리고 보강 커밋(`405f0102`)은 남겼다.

**「실물에서 도달 불가」 판단은 없었다** — 이 자리는 실물 코퍼스에서 매번
렌더되는 경로이고 값도 자주 채워지므로(코퍼스 실측: DECLARE 사실을 가진
객체 25개), 보강이 옳은 선택이었다.

## MUTATION 12 — CENSUS DELTA 관측

무변이 상태(기준선)에서 `LocalVariableTableCorpusTests`가 표준출력에 낸 값:

```
[census delta] 프로시저 14편 DECLARE 사실 합 40 · 파라미터 총수 29 · 합 69 (SpecMaterialCensus의 69와 대조)
```

`if (node is ProcedureParameter) return;`을 지운 뒤 같은 테스트를 다시 돌린 값:

```
[census delta] 프로시저 14편 DECLARE 사실 합 69 · 파라미터 총수 29 · 합 98 (SpecMaterialCensus의 69와 대조)
```

**관측했다: 40 → 69로 정확히 29(파라미터 총수)만큼 튀었다** — 계획서가 예고한
그대로다. `LocalVariableTable_RenderedFromDdl_IsAcceptedByTheCheck` 자체는
이 변이로 **빨개지지 않았다**(`Assert.Empty(violations)`는 여전히 통과) —
`PerfectTranscription`이 `expectations.LocalVariableDeclarations`(이미 오염된
같은 facts)로부터 자체 조립되므로 자기 일관성만 재확인할 뿐, 이 변이의 정오는
독립적으로 증명하지 못한다(그 파일 자신의 클래스 주석이 이 한계를 이미
명시한다). **이 변이를 실제로 잡는 것은 단위 테스트
`Extract_ShouldNotReturnProcedureParameters`뿐이다** — CENSUS DELTA는 진단
신호일 뿐 게이트가 아니다.

## FINAL GATE

모든 변이를 되돌린 뒤:

- `dotnet build --no-incremental` → 경고 0 · 오류 0
- `dotnet test tests/ReSet.Core.Tests --no-build` → **실패 0 · 통과 3265 · 건너뜀 0**
  (기준선 `0a3469b3`과 정확히 일치 — 보강 두 단언은 기존 `[Fact]` 안에 더한
  것이라 테스트 개수는 늘지 않는다)
- `git diff HEAD -- src/` → 비어 있음(0줄)
- `git status --short` → 보고서 파일(신규)만 남고 그 외는 깨끗함(보강 커밋은
  이미 반영됨)

## 커밋

- `405f0102` test: 렌더러가 초기값 칸을 버리는 표시 계층 결함을 행 모양으로 잡는다
  (`tests/ReSet.Core.Tests/LocalVariableTablePromptTests.cs`)
- 이 보고서 커밋(다음 커밋)

## CONCERNS

- **정정(픽스 라운드 1).** 계획서의 예측 문구("카탈로그 테스트"가 변이 1을
  잡는다)에 대한 최초 판정("구조적으로 안 죽는다")은 **필터가 걸린 재실행
  탓에 불완전했다** — 카탈로그 테스트 넷(자기참조)은 정말 안 죽지만,
  `MachineConfirmedTablesExpansionTests.CriticExemptionBlock_ShouldCoverTheLocalVariableTable`
  하나는 한글 리터럴을 손으로 박아 뒀기 때문에 죽는다(전건 재실행으로 확인).
  다만 그것은 설계된 헤딩-개명 방어가 아니라 그 테스트의 작성 방식이 우연히
  겹친 것이고, Task 6(이음매)보다 앞선 Task 3 커밋(`19cc7098`)에서 이미
  존재했다 — 그래서 "Task 6이 없었으면 이 개명이 전 테스트를 통과했을 것"은
  거짓이다. 다음에 이 축을 다시 감사할 사람이 "카탈로그 테스트가 헤딩
  리터럴까지 잠근다"고 일반화하지 않도록, 그리고 "필터를 걸고 돌린 재실행이
  전건 결과와 같다"고 가정하지 않도록 이 문서에 남긴다.
- **정정(픽스 라운드 2) — 같은 오류가 반복됐다.** 픽스 라운드 1이 낸 "Task 6이
  고유하게 잡는 것은 변이 2다, 그 셋은 전부 Task 6 계열 커밋에서 나왔다"도
  틀렸다 — `git show e00f787d`로 직접 열어보니 셋 중 둘(`WholeSpBranch_…`·
  `FunctionBranch_…`)은 **Task 4**의 커밋에서 이미 헤더 행 단언을 갖고 있었다.
  Task 6이 변이 2에서 고유하게 잡는 것은 `TheRenderedTable_ShouldBeReadableByTheCheckDReader`
  하나뿐이다. 이를 리더 쪽 변이(신규 — 변이 14, `SpecStatementFactsExtractor`의
  헤딩 접두사 배열에서 `"### 지역 변수"` 제거)로 독립 검증했다.
- **정정(픽스 라운드 3) — "리더 축에서 Task 6의 고유 기여는 없다"는 과소평가였다.**
  변이 14에서 Task 6의 세 테스트가 이 계획보다 앞선 리더 단위 테스트 여섯 개와
  같이 죽는다는 관측 자체는 맞았지만, 거기서 "그러므로 리더 축 전체에서 Task 6은
  값이 없다"로 일반화한 것이 틀렸다 — **변이 1(production 상수 개명)에서는 그
  선행 여섯이 전부 살아남고 `TheMachineHeading_ShouldBeReadableByTheCheckDReader`·
  `TheMachineHeading_ShouldStartWithAKnownReaderPrefix` 둘만 죽는다**(본문 §2
  AXIS TABLE 참고) — 이 데이터는 라운드 1이 이미 이 문서에 적어 둔 것인데,
  라운드 2가 그것과 대조하지 않고 결론을 냈다. 선행 여섯은
  `LocalVariableDeclarationExtractor.TableHeading` 상수를 아예 참조하지 않는
  독립 리터럴 픽스처라 상수 개명에 무영향이다 — "겹친다"는 배열-축(변이 14)
  에서만 참이고 상수-축(변이 1)에서는 거짓이다. 그리고 상수-축이 정확히
  (5-3-7)의 실패 양식(생산 상수가 리더의 접두사와 어긋나 검사 D가 재료를
  잃음)이므로, Task 6은 그 축에서 이 계획이 존재하는 이유 자체를 검증하는
  유일한 방어다. **세 번의 정정이 모두 같은 뿌리에서 났다** — "어느 테스트가
  무엇을 잡는가"를 필터 문자열(라운드 1 이전)·커밋 메시지와 파일명(라운드 1의
  정정)으로 추정했고, 이번엔 **부분 증거(변이 2·14)로 "이 계획 전체에서"를
  일반화하면서 같은 문서 안의 반증 데이터(변이 1 결과)를 대조하지 않았다**
  (라운드 2). **다음에 이 축을 감사할 사람에게 남기는 관통 지침 둘:** (1)
  귀속 주장은 반드시 `git show <commit>`으로 그 diff를 열어보거나 변이를
  직접 돌려 확인한 뒤에만 적어라. (2) **결론을 쓰기 전에 같은 문서 안의 다른
  측정과 대조하라** — 새 측정이 옛 측정을 뒤집는지, 아니면 축이 달라서 둘 다
  참인지 먼저 보고 나서 일반화하라.
- 변이 9의 생존은 값진 신호다(위 "직전 회차 권고의 인과" 참고) - 표시 계층은
  계수 로직보다 방어가 얇다. 이번에 발견된 틈(행 전체 모양 vs 부분 문자열
  포함)은 이 표 하나에 국한된 보강으로 닫았지만, 같은 패턴(부분 문자열 포함
  단언만으로 칸 하나가 통째로 비어도 안 걸리는 모양)이 이 코드베이스의 다른
  기계 확정 표 렌더러에도 있을 수 있다 - 이번 태스크의 허용 범위 밖이라 고치지
  않았지만, 다음 감사 후보로 남긴다.
