# 검증 종료 상태 표기 일관화 설계

- 작성일: 2026-08-01
- 상태: 설계 승인됨 (구현 계획 수립 전)

## 배경

`2026-08-01-batch-design-hardening` 브랜치를 병합한 뒤, 최종 리뷰가 남긴 후속 과제를 조사하다 같은 계열의 결함을 하나 더 발견했다.

검증 파이프라인은 네 가지로 끝난다. **통과**, **L1 기계 검증 재시도 소진**, **L2 품질 미달**, **L2 리뷰 미수행**. 경로는 단일 SP와 통합 배치 둘이므로 조합은 여덟이다. 그런데 문서에 흔적을 남기는 것은 "L2 품질 미달" 세 자리와, 직전 작업에서 추가한 "L2 리뷰 미수행(통합)" 하나뿐이다.

이번 세션에서 찾은 결함 셋이 전부 같은 모양이다. **종료 분기 하나가 표기를 빠뜨렸다.**

| # | 결함 | 위치 |
|---|---|---|
| A | 단일 SP 경로에서 L2 리뷰 미수행이 "검증 통과"로 표시된다 | `VerificationPipelineOrchestrator.cs:1057` |
| ② | L1 소진 시 문서 배너가 없다 (양쪽 경로) | `VerificationPipelineOrchestrator.cs:961`, `:1570` |
| ① | 지시서의 `Spec.md` 링크가 `Procedures/`로 하드코딩되어 있다 | `MetadataExporter.cs:446` |

### A — 가장 파급이 크다

```csharp
if (l1Result.IsValid && (l2Result == null || !l2Result.HasDefects))
{
    ...
    _userInteraction.NotifyValidationSuccess(selectedOption);
```

Critic 호출이 예외로 실패하면 `l2Result`가 null로 남고, 이 조건이 성공 분기를 탄다. 직전 작업에서 통합 경로의 같은 결함(④)을 고쳤으나 단일 SP 경로는 그대로였다.

파급이 통합 경로보다 넓다.

- 메뉴 1(SP 역공학 분석)이 주 사용 경로다.
- 재귀 분석은 객체마다 이 파이프라인을 돈다. 31개 객체 중 하나에서 일시적 API 오류가 나면 그 객체만 검증 없이 통과하고 드러나지 않는다.
- 문서에도 남지 않는다. `SpecificationDocumentFormatter`는 `review`가 null이면 YAML 신뢰도 헤더와 점수 줄을 통째로 생략한다. 리뷰를 못 돌린 문서와 구버전 문서가 구분되지 않는다.
- L3 인간 승인 화면(`ConsoleUserInteraction.cs:105-156`)은 그 YAML을 파싱해 상단 Rule에 신뢰도를 띄운다. 헤더가 없으면 조용히 비어 있고, 프롬프트 제목은 `"명세서 검증 완료."`라고 단언한다. **사용자가 교차 검증을 못 돌린 명세서를 승인하는 순간에도 그 사실을 알 수 없다.**

### 구조적 원인

`[!CAUTION]` 품질 미달 배너 문자열이 세 곳(`:723`, `:1050`, `:1615`)에 변수명만 다른 채 복제되어 있다. 종료 상태의 표기가 분기마다 흩어져 있고, 어떤 분기가 표기를 갖고 어떤 분기가 안 갖는지 강제하는 것이 없다. 결함 셋은 그 구조의 산물이다.

## 목표와 범위

### 목표

네 가지 종료 상태가 모두, 두 경로 모두에서 문서와 화면에 사실대로 표기되게 한다. 지시서의 명세서 링크가 실제 산출물 위치를 가리키게 한다.

### 범위 밖

- **재시도 인프라(③).** 리뷰 호출이 일시적으로 실패해도 재시도가 없다. 고친다면 외부 재시도 루프가 아니라 `ReviewConsolidatedPlanAsync`/`ReviewSpecificationAsync` 주변이 옳다. 성격이 달라 별도 사이클로 미룬다.
- **통합 계획서 헤더 기계.** `BatchMigrationPlan.md`는 이 포매터를 쓰지 않고 `Program.cs:1170`이 자체 헤더를 만든다. 통합 경로는 직전 작업에서 `[!NOTE]` 배너를 받았으므로 그대로 둔다.
- **TUI 동명이DB 표시.** 선택 목록이 객체 디렉터리 이름만 렌더링해 서로 다른 DB의 동명 프로시저가 구분되지 않는다. 표시 계층 문제라 원인과 수정 지점이 다르다.
- **재시도 횟수·점수 임계값 정책**, `ReviewResult` 모델과 5개 점수 축.
- **파이프라인 루프의 상태 머신 재구조.** 1,900줄 오케스트레이터의 핵심 루프를 갈아엎는 위험이 이번 세 건에 비해 과하다.

## 결정 사항

| 항목 | 결정 | 근거 |
|---|---|---|
| 접근 | 종료 상태를 값으로 표현 + 배너 렌더러 공유 | 결함 셋이 모두 "흩어진 분기가 하나씩 빠뜨림"에서 나왔다. 원인을 직접 겨냥한다 |
| 배너 렌더러 형태 | 종료 상태별 메서드 셋 (단일 메서드 아님) | 상태마다 필요한 데이터가 다르다. 하나로 묶으면 대부분의 파라미터가 상황마다 null이 된다 |
| A 표현 | 포매터 확장 + L3 화면 표시 | 승인 직전에 사실을 알아야 한다. 재귀 객체에도 자동 적용된다 |
| YAML 출력 | 항상 출력하고 `검증 상태`를 명시 | 지금은 review가 null이면 헤더가 통째로 없어 구버전 문서와 구분되지 않는다 |
| 점수 필드 | `review`가 있을 때만 | 없는 점수를 0으로 채우면 "0점 받은 문서"로 읽혀 더 나쁘다 |
| ① 링크 | 리솔버로 계산 + 존재 확인 | `SpecificationLinker`가 이미 쓰는 패턴이다. 인코딩도 단일 소스에서 온다 |
| 지시서 시그니처 | `OutputPathResolver`를 받는다 | 같은 파일의 `ExportCodeObjectArtifactsAsync`가 이미 그 모양이다 |
| YAML 파싱 | 순수 함수로 추출 | 프롬프트 메서드 안에 인라인이라 테스트가 불가능하다 |

## 설계

### 1. `VerificationOutcome`

`ReSet.Core/Models/`에 검증 루프의 종료 사유를 표현하는 열거형을 둔다.

| 값 | 의미 |
|---|---|
| `Passed` | L1 통과 + L2 결함 없음 |
| `L1Exhausted` | L1 기계 검증 재시도 소진 |
| `QualityRejected` | L2 리뷰 완료, 점수 미달·결함 |
| `ReviewNotRun` | L2 리뷰 호출이 예외로 실패 |

네 값이 곧 루프의 네 종료 지점이다. 지금 이 구분은 `l2Result == null` 같은 암묵적 조건에 흩어져 있다.

`CodeObjectPipelineResult`에 `Outcome` 필드를 더해 하류로 전달한다. `SpecificationDocumentFormatter.Format`의 호출부는 둘이며(`DependencyAnalysisOrchestrator.cs:461`의 재귀 객체, `Program.cs:1626`의 루트 SP) **둘 다 이 필드에서 Outcome을 받는다.** 한쪽만 연결하면 재귀 객체와 루트 SP의 헤더 규약이 갈라진다.

### 2. `VerificationBanner`

`ReSet.Core/Services/`에 문서 앞에 붙일 배너 렌더러를 둔다.

```csharp
public static class VerificationBanner
{
    public static string L1Exhausted(IReadOnlyList<string> errors);
    public static string QualityRejected(ReviewResult review, int scoreThreshold);
    public static string ReviewNotRun(string reason);
}
```

`QualityRejected`가 세 곳에 복제된 문자열을 흡수한다. 나머지 둘은 현재 어디에도 없다.

`Passed`용 메서드는 두지 않는다. 통과 시 붙일 배너가 없으므로 없는 것을 표현하는 메서드는 군더더기다.

오케스트레이터의 두 경로(단일 SP `:940~1070`, 통합 `:1548~1650`)가 각 종료 분기에서 `VerificationOutcome`을 확정하고 같은 렌더러를 호출한다.

### 3. 포매터 확장

`SpecificationDocumentFormatter.Format`에 `VerificationOutcome`을 추가한다.

| Outcome | YAML | NOTE 블록 |
|---|---|---|
| `Passed` | `검증 상태: 통과` + 점수 5종 | 현행 점수 줄 |
| `QualityRejected` | `검증 상태: 품질 미달` + 점수 5종 | 현행 점수 줄 |
| `ReviewNotRun` | `검증 상태: 리뷰 미수행` (점수 없음) | 미수행 사실과 사유 |
| `L1Exhausted` | `검증 상태: L1 미통과` (점수 없음) | L1 미통과 사실 |

**점수 필드는 Outcome이 결정한다.** `L1Exhausted`와 `ReviewNotRun`에서는 `review` 인자가 null이 아니더라도 점수 필드를 내보내지 않는다. 단일 SP 경로에서 1차 시도가 L2까지 갔다가 2차 시도가 L1에서 소진되면 이전 시도의 `ReviewResult`가 남아 있을 수 있는데, 그 점수를 헤더에 실으면 실제로 검증되지 않은 최종 문서에 옛 점수가 붙는다. 판정 기준은 `review != null`이 아니라 Outcome이다.

배너와의 순서는 현행 그대로다. 배너는 루프 안에서 본문 앞에 붙고 포매터가 그 위에 헤더를 얹는다. YAML이 문서 맨 앞에 와야 파서가 동작한다.

### 4. L3 화면

`ConsoleUserInteraction`이 `검증 상태`를 읽어 표시한다.

- 상단 Rule에 상태를 붙인다. 통과가 아니면 붉은 계열로. 승인 직전에 눈에 들어오는 자리다.
- 프롬프트 제목의 `"명세서 검증 완료."`를 상태에 따라 바꾼다. 리뷰를 못 돌렸으면 완료라고 말하지 않는다.
- `검증 상태` 필드가 없는 기존 문서는 현행대로 동작한다. 없는 필드를 "미상"으로 표시하면 정상 문서까지 경고처럼 보인다.

YAML 파싱은 마크다운을 받아 헤더 값을 돌려주는 순수 함수로 추출한다. 렌더링과 프롬프트는 그대로 둔다.

### 5. 지시서 링크 정확성

`ExportConsolidatedMigrationInstructionsAsync`가 `OutputPathResolver`를 받는다.

```csharp
Task ExportConsolidatedMigrationInstructionsAsync(
    List<SpDefinition> spDefs,
    string consolidatedPlan,
    string jobName,
    string baseOutputDir,
    string targetLanguage,
    OutputPathResolver paths);
```

호출부(`Program.cs:737`, `:1208`)는 `new OutputPathResolver(database, outputDir)`를 넘긴다. `database`는 `:125`에 있다.

3절의 링크 생성이 바뀐다.

```
현재:  $"../../../Procedures/{spDef.Schema}.{spDef.Name}/docs/Spec.md"   (무조건)

변경:  paths.ResolveSpecPath(key) 로 절대 경로 계산
       → Path.GetRelativePath(agentDir, 절대경로) 로 상대화, 구분자를 / 로 정규화
       → 파일이 있으면 링크, 없으면 사유 표기
```

External DB 프로시저는 리솔버가 `External/<DB>/Procedures/...`로 보내고 `EncodePathSegment`도 함께 따라온다.

`spDef.ObjectKey`가 null인 구버전 `metadata.json`은 `Schema`/`Name`과 리솔버의 기준 DB로 키를 만든다. 결과적으로 현행과 같은 `Procedures/` 경로가 나오므로 기존 산출물의 동작이 유지된다.

존재 확인이 잡는 것은 "메타데이터는 복원됐는데 `Spec.md`가 옮겨졌거나 지워진" 경우다. 메타데이터 복원 자체에 실패한 SP는 직전 작업에서 이미 `spDefs`에서 빠지고 경고로 드러난다.

### 6. 오류 처리

새로 생기는 실패 지점이 거의 없다. `VerificationBanner`와 `SpecificationDocumentFormatter`는 순수 문자열 렌더링이라 IO도 예외 경로도 없다. 방어할 것은 `ReviewResult.FeedbackComment`의 null뿐이며 현행 코드가 이미 `?.Replace(...)`로 처리한다.

새 IO는 링크 존재 확인 하나다. `File.Exists`는 잘못된 경로에 예외 대신 `false`를 돌려준다. 앞단의 `Path.GetRelativePath`가 빈 문자열에서 던지므로 `ObjectKey` 폴백이 그것을 막는다. 지시서 생성 전체는 두 호출부 모두 try/catch 안에 있어(`Program.cs:694-760`, `:1214-1242`) 실패해도 배치 설계 흐름을 중단시키지 않는다.

`OperationCanceledException`은 어떤 catch에서도 삼키지 않는다.

## 테스트 계획

| 대상 | 검증 |
|---|---|
| `VerificationBanner` | 세 종료 상태별 렌더링. `QualityRejected`는 기존 세 곳이 만들던 문자열과 동일해야 한다 — 추출이 문구를 바꾸지 않았음을 잠그는 회귀 가드 |
| `SpecificationDocumentFormatter` | 네 Outcome × YAML `검증 상태` + 점수 필드 유무. `ReviewNotRun`·`L1Exhausted`에 점수가 새어나오지 않을 것 |
| 헤더 파서 (신규 추출) | `검증 상태` 파싱, 필드 없는 구버전 문서, YAML 없는 문서 |
| `VerificationPipelineOrchestrator` (단일 SP) | Critic 예외 → `ReviewNotRun` + `NotifyValidationSuccess` 미호출 / L1 소진 → 배너 삽입. 통합 경로에 이미 있는 테스트의 대칭형 |
| `MetadataExporter` | External DB 프로시저가 `External/<DB>/Procedures/...`로 링크 / `Spec.md` 부재 시 링크 대신 사유 |

가장 중요한 것은 첫 줄이다. 세 곳에 복제된 문자열을 하나로 합치는 것이 이번 변경의 실질이고, 그 과정에서 문구가 미묘하게 달라지면 기존 산출물과 어긋난다.

### 테스트하지 않는 것

L3 화면 렌더링 자체. Spectre.Console 출력이라 테스트에서 도달할 수 없고, 파싱을 빼내고 나면 남는 것은 표시뿐이다.

## 검증 시나리오

1. `dotnet build` 오류 0. 기존 경고 8건(`tests/ReSet.Core.Tests/DbMetadataServiceTests.cs`의 CS8600/CS8602)은 이 브랜치 소관이 아니며 늘어나지 않아야 한다.
2. `dotnet test` 전량 통과. 현재 329 + 신규.
3. `AGENTS.md` 체크리스트의 단위 테스트 개수를 실제 실행 결과로 갱신.

## 알려진 리스크 (이번 범위에서 수정하지 않음)

- **직전 병합분을 다시 건드린다.** 통합 경로의 배너 조립을 공유 렌더러로 바꾸므로 방금 병합한 코드가 수정된다. 해당 동작은 테스트로 잠겨 있어 회귀는 잡히지만, 변경 자체는 최소로 유지한다.
- **`검증 상태` 값은 한국어 문자열이다.** 파서가 문자열을 비교하므로 표기를 바꾸면 기존 문서와 어긋난다. 값 목록을 늘릴 때는 파서와 함께 바꿔야 한다.
- **일시적 리뷰 실패가 여전히 즉시 포기로 이어진다.** 이번 변경은 그 사실을 정직하게 표시할 뿐 복원력을 더하지 않는다. 재시도는 ③의 몫이다.
