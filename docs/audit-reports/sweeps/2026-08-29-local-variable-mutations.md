# 지역 변수 표 강제 — 변이 검증 (Task 9)

**잰 시각:** 2026-08-29 15:01 KST
**커밋 해시(`main`에서 도달 가능):** `0a3469b3` (`docs: 캐시 18 승격 근거를 Task 7의 실측으로 바꾼다`)
**작업 트리 청결도:** 변이 실험은 전부 되돌렸다. 최종 상태에서 `git diff HEAD`는
`src/` 전체가 비어 있다 — 이 보고서가 의존하는 변이 실험은 이 문서와 아래 커밋
로그로만 남는다. 살아남은 변이 하나(9번)의 보강만 `tests/`에 실물로 남는다.

## 맨 앞 — 세 가지 대답

1. **몇이 죽고 몇이 살았는가.** 열셋 중 **열둘이 즉시 죽었다.** **하나(변이 9 —
   렌더러가 `InitialValue` 칸을 언제나 빈 칸으로 냄)가 살아남았다.**
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

     **Task 6에 대한 인과 — 정정.** `CriticExemptionBlock_ShouldCoverTheLocalVariableTable`은
     Task 3의 커밋 `19cc7098`(카탈로그 등록)에서 추가됐고 Task 6(이음매)보다
     앞선다(`git log`로 확인). **그러므로 "Task 6이 없었으면 이 헤딩 개명이 전
     테스트를 통과했을 것"은 이 사례에 대해 거짓이다** — Task 6 이전에도 이
     우연한 리터럴 하나가 헤딩 개명을 잡았을 것이다. **Task 6이 고유하게 잡는
     것은 이 헤딩 개명(변이 1)이 아니라 변이 2(렌더러의 헤더 칸 이름 변경)다.**
     변이 2를 같은 방식(필터 없는 전건 재실행)으로 다시 확인한 사망 목록은
     `LocalVariableTableSeamTests.TheRenderedTable_ShouldBeReadableByTheCheckDReader` ·
     `LocalVariableTablePromptTests.WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows` ·
     `...FunctionBranch_ShouldCarryTheTableWithItsHeadingAndRows` 셋뿐이고, **카탈로그
     테스트는 그중 어느 것도 죽지 않는다** — 헤더 칸 이름("변수 명칭")은
     `MachineConfirmedTables`가 전혀 다루지 않는 별개의 축이기 때문이다. 이
     셋은 전부 Task 6 계열 커밋(`e00f787d` "지역 변수 표를 Actor 프롬프트의 세
     갈래에 싣는다" 및 그 뒤 이음매 커밋들)에서 나왔다 — **Task 6이 없었으면
     변이 2는 전 테스트를 통과했을 것이고, 그것이 Task 6의 고유 값이다.**
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
| 2 | 렌더러 헤더 `변수 명칭` → `변수 이름` | 이음매 종단 테스트 | **죽음**, 예측대로 | `LocalVariableTableSeamTests.TheRenderedTable_ShouldBeReadableByTheCheckDReader` · `LocalVariableTablePromptTests.WholeSpBranch_ShouldCarryTheTableWithItsHeadingAndRows` · `...FunctionBranch_ShouldCarryTheTableWithItsHeadingAndRows` |
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
  거짓이다. **Task 6이 고유하게 잡는 것은 변이 2(렌더러 헤더 칸 이름 변경)다**
  — 그 변이에는 카탈로그 테스트가 전혀 반응하지 않는다(전건 재실행으로 확인,
  본문 §2 참고). 다음에 이 축을 다시 감사할 사람이 "카탈로그 테스트가 헤딩
  리터럴까지 잠근다"고 일반화하지 않도록, 그리고 "필터를 걸고 돌린 재실행이
  전건 결과와 같다"고 가정하지 않도록 이 문서에 남긴다.
- 변이 9의 생존은 값진 신호다(위 "직전 회차 권고의 인과" 참고) - 표시 계층은
  계수 로직보다 방어가 얇다. 이번에 발견된 틈(행 전체 모양 vs 부분 문자열
  포함)은 이 표 하나에 국한된 보강으로 닫았지만, 같은 패턴(부분 문자열 포함
  단언만으로 칸 하나가 통째로 비어도 안 걸리는 모양)이 이 코드베이스의 다른
  기계 확정 표 렌더러에도 있을 수 있다 - 이번 태스크의 허용 범위 밖이라 고치지
  않았지만, 다음 감사 후보로 남긴다.
