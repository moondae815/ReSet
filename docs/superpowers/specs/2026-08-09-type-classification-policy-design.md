# 타입 분류 판정 일원화와 정책 스캐너 설계

- 작성일: 2026-08-09
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 선행: [2026-08-08 정적 분석 식별자 정합성 복구](2026-08-08-static-analysis-identity-design.md)

## 배경

선행 브랜치가 `SqlObjectTypeClassifier`를 도입한 이유는 `"SQL_TABLE_VALUED_FUNCTION"`이 문자열 `"TABLE"`을 포함하기 때문이다. 원시 `.Contains("TABLE")` 판정을 쓰면 TVF가 테이블로 오분류되어 DDL이 수집되지 않는다. 실제로 `UIF_SettleYMD`(정산일을 계산하는 함수)가 그렇게 블랙박스가 됐고, `UP_UTIL_SETTLE_EXPECT_PROC`의 다섯 단계가 그 위에서 문서화됐다.

**그 브랜치는 이 결함을 네 번에 걸쳐 발견했다.**

| 회차 | 발견자 | 찾은 곳 |
|---|---|---|
| 1 | 최초 조사 | `DbMetadataService`의 재귀 경로 |
| 2 | Task 5 리뷰 | `DbMetadataService.cs:583` |
| 3 | 좌표자 확인 | `DbMetadataService.cs:1237` |
| 4 | 최종 브랜치 리뷰 | `SettlementPolicyService` ×2, `SnapshotManager`, `AiService` |

매 회차마다 "이제 다 찾았다"고 판단했고 매번 틀렸다. 그리고 이 설계를 쓰는 도중 **다섯 번째**가 나왔다 — `MetadataExporter.cs:340`.

원인은 표기가 매번 달랐다는 데 있다: `rawDep.Type` → `dep.Type` → `objectType` → `d.Type` → `type`. 사람이 만든 grep 패턴은 그때 눈에 띈 표기만 잡는다.

선행 브랜치가 남긴 가드(`DbMetadataService_DelegatesClassificationToSqlObjectTypeClassifier`)는 **파일 하나**에서 **리터럴 하나**(`"TABLE"`)만 본다. 다른 파일의 같은 결함도, 같은 파일의 `"VIEW"`/`"FUNCTION"`/`"PROCEDURE"`도 통과한다.

### 이 저장소에 이미 있는 해법

`CancellationPolicyScanner`가 같은 메타 문제를 이미 풀었다. 그 주석이 진단을 적어 두었다.

> 세 사이클 연속으로 같은 결함이 새 모양으로 나타났고 매번 사람이 새 grep 패턴을 만들어 찾았다. (…) grep이 놓친 것은 패턴이 달라서가 아니라 C# 구조를 못 읽어서다.

구성은 세 부분이다. Roslyn 구문 트리 스캐너, 파일별 허용 개수를 담은 baseline, 스캐너 자체의 양성·음성 테스트. `Microsoft.CodeAnalysis.CSharp` 5.6.0은 이미 테스트 프로젝트 의존성이다.

## 목표와 범위

원시 타입 분류 판정을 코드베이스에서 없애고, 다시 생기면 빌드가 막게 한다.

**범위 안**

- 남은 5곳을 `SqlObjectTypeClassifier`로 위임
- `TypeClassificationPolicyScanner` 신설과 그 자체 테스트
- 파일 단위 문자열 가드를 스캐너 기반 정책 테스트로 교체
- 중복·대체된 테스트 3건 삭제
- `StaticAnalysisNormalizer.SplitIdentifier`의 `]` 손상 수정

**범위 밖**

- **`DependencyInfo.Type`의 타입화.** `string`을 전용 열거형으로 바꾸면 원시 판정이 원천 차단되지만 모델·직렬화·스냅샷 호환성까지 번진다. 별도 설계로 다룬다.
- **프롬프트 계약 강화 3건** — UPDATE 컬럼 매핑표 강제, `UPDATE ... FROM` 자기참조 의미 기술, `SET` 절 동시평가 명시. 선행 설계가 이미 범위 밖으로 분리했다.
- **명세서 재발 방지 검증 게이트.** L2 Critic에 스키마 부재 주장의 사실검증을 추가하는 일로, 무엇을 근거로 검증할지부터 설계해야 한다. 선행 브랜치의 최종 리뷰어가 남은 항목 중 가장 시급하다고 평가했다.
- **`VerificationPipelineOrchestrator`의 로그 텍스트 매칭.** `logUpper.Contains("TABLE")`은 타입 분류가 아니다. 건드리지 않으며, 스캐너가 이것을 오탐하지 않는 것이 설계 요건이다.

## 설계

### 1. 위반 서명

구문 트리에서 다음을 모두 만족하는 `InvocationExpression`이 위반이다.

1. 멤버 접근의 이름이 `Contains`
2. 인자에 문자열 리터럴 `TABLE` · `VIEW` · `FUNCTION` · `PROCEDURE` 중 하나 (대소문자 무시)
3. 수신자가 SQL 타입 표현식 — `.Type`으로 끝나는 멤버 접근(`dep.Type`, `d.Type`)이거나, 이름이 `type`이거나 `Type`으로 끝나는 식별자(`type`, `objectType`, `dependencyType`)

3번이 오탐을 막는다. 로그 매칭은 수신자가 `logUpper`라 걸리지 않는다.

`CancellationPolicyScanner`와 같이 시맨틱 모델 없이 구문 트리만 본다. 그 스캐너가 근거를 이미 적어 두었다 — 빠르고, 프로젝트 참조가 필요 없고, 이 저장소의 명명 규약이 일관되어 실용적으로 충분하다.

알려진 한계: `var t = dep.Type; t.Contains("TABLE")`은 놓친다. 가드는 휴리스틱이다.

**정정(최종 브랜치 리뷰, 발견 1):** 위 문단은 원래 "이 형태는 자연스러운 리팩터링에서 나오지 않는다"고 적었다. 이 코드베이스에 대해 사실이 아니다 — `dep.Type.ToUpper().Contains(...)`처럼 정규화 호출로 감싼 수신자는 실제로 나온다. 이 저장소가 타입 문자열을 정규화한 뒤 매칭하는 관용구를 이미 세 곳에서 쓰기 때문이다(`DependencyAnalysisOrchestrator.cs:329`, `MetadataExporter.cs:160`, `DbMetadataService.cs:216` — 전부 `Trim().ToUpperInvariant()` 뒤에 매칭한다). 이 형태 중 어느 곳에 `Contains` 기반 분기 하나만 덧붙이는 것이 다음 편집으로 자연스러웠을 것이고, 스캐너가 감싼 수신자를 못 보면 게이트가 침묵한 채 원래의 TVF 오분류 결함이 되살아났을 것이다. 스캐너는 이후 Trim/ToUpper/ToUpperInvariant/ToLower/ToLowerInvariant로 감싼 수신자를 풀어(중첩 포함) 안쪽 수신자로 재판정하도록 고쳐졌고, `Contains` 외에 `IndexOf`/`StartsWith`/`EndsWith`도 같은 판정으로 다루도록 넓어졌다.

**정정 2(재리뷰, 발견 1 - 부분적으로 닫힘):** 위 1차 수정은 불완전했다. `DependencyAnalysisOrchestrator.cs:329`와 `MetadataExporter.cs:160`이 실제로 쓰는 모양은 `dependencyType?.Trim().ToUpperInvariant()` — 즉 **널 조건부**(`?.`) 뒤에 정규화 호출이 이어지는 형태다. 1차 수정의 언래핑은 `invocation.Expression is MemberAccessExpressionSyntax`만 처리했는데, 널 조건부 체인의 *첫* 호출(`?.Trim`)은 `MemberBindingExpressionSyntax`로 파싱되고 더 안쪽 표현식이 없어 그 자리에서 멈췄다 - 근거로 인용한 두 지점의 실제 모양을 정작 못 잡는 상태로 "고쳐졌다"고 적은 것이었다. `TryUnwrapNormalizationCall`이 `MemberBindingExpressionSyntax`를 만나면 감싸는 `ConditionalAccessExpressionSyntax.Expression`을 진짜 수신자로 보도록 고쳐, 이제 두 지점의 실제 모양이 잡힌다(실측 확인). `var t = dep.Type; t.Contains(...)`처럼 이름이 다른 지역 변수로 옮겨 담는 형태, `dep?.Type?.Trim().Contains(...)`처럼 조건부 접근이 두 번 이상 이어지는 형태, `Contains`/`IndexOf`/`StartsWith`/`EndsWith` 외의 문자열 메서드, `Trim`/`ToUpper`류 밖의 메서드(`ToString`, `Substring` 등)로 감싼 수신자는 여전히 놓친다 - 전부 실측으로 확인했다(이 코드베이스에 해당 형태가 없거나, 인위적으로 만든 소스에서 놓치는 것을 직접 관찰했다).

**정정 3(재리뷰 Critical, 발견 1 - 완전히 닫힘):** 위 정정 2의 "조건부 접근이 두 번 이상 이어지는 형태는 놓친다"는 문장이 사실과 달랐다 - 실제로는 "놓치는" 것이 아니라 **무한 루프**였다. 2차 수정이 추가한 조상 탐색(`TryFindEnclosingConditionalAccess`)이 부모를 따라 올라가다 "처음 만나는" 조건부 접근식을 무조건 소유자로 봤는데, `a?.Trim()?.Contains(...)`처럼 `?.`가 두 번 이어지면 안쪽 조건부 접근식의 `Expression`이 바로 `?.Trim()` 호출 자신이라 "처음 만나는" 조건부 접근식이 오히려 자기 자신을 감싸는 것이었다 - `conditional.Expression`이 언래핑 대상 노드 자신을 돌려주고, `IsSqlTypeExpression`의 while 루프가 같은 노드를 영원히 반복했다. 재현·워치독 실측은 실행 보고에 남아 있다(4개 형태가 5초 타임아웃 안에 안 끝남, 수정 후 전부 25ms 이내로 종료).

고친 내용은 두 가지다. (1) 조상 탐색을 "처음 만나는 CA"가 아니라 "실제로 `WhenNotNull`을 소유하는 CA"로 바로잡았다(`TryFindOwningConditionalAccess`) - 노드가 어떤 CA의 `Expression`(수신자) 쪽에 있으면 그 CA는 소유자가 아니므로, 그 CA 자체를 새 시작점 삼아 계속 올라간다. 이 수정만으로 `?.`가 몇 번 이어지든(세 번·네 번까지 실측) 안전하게 끝까지 풀리고, 밀리초 단위로 끝난다. (2) 그와 별개로 `IsSqlTypeExpression`의 while 루프에 방어적 루프 안전장치(직전과 같은 노드가 돌아오면 중단)를 추가했다 - 조상 탐색이 정확해도 남겨 뒀다. 게이트가 무응답이 되는 것은 판정을 놓치는 것보다 나쁘고, 구문 트리 형태는 앞으로도 예상 밖이 나올 수 있기 때문이다.

같은 재리뷰에서 `dep?.Type.Contains("TABLE")`(첫 널 조건부 접근 자체가 수신자, 언래핑을 거치지 않는 경우)도 안 잡히는 것이 발견됐다. 조상 탐색 없이 `IsSqlTypeExpression`의 switch에 `MemberBindingExpressionSyntax` 분기 하나를 추가하는 것으로 안전하게 고쳐졌다(무한 루프를 일으킨 조상 탐색 로직과는 무관한 별개의 코드 경로다).

이번 라운드의 방침은 지난 두 라운드와 달랐다 - 탐지 능력을 넓히지 않고 Critical과 위 두 가지만 고쳤다. 남는 한계는 위 정정 2의 목록에서 "이중 널 조건부 체인" 항목만 제외한 나머지 그대로다: 지역 변수 재대입, `Contains`/`IndexOf`/`StartsWith`/`EndsWith` 외의 문자열 메서드, `Trim`/`ToUpper`류 밖의 메서드로 감싼 수신자.

`SqlObjectTypeClassifier.cs`는 스캐너가 건너뛴다. 그 파일이 정책의 구현체다.

스캔 대상은 `src/` 아래 모든 프로젝트(`ReSet.Core` · `ReSet.Cli` · `ReSet.Validator.Core` · `ReSet.Validator.Cli`)의 `.cs` 파일이며, 빌드 산출물(`bin/` · `obj/`)은 제외한다. 산출물을 훑으면 생성 코드가 오탐을 만들고 스캔이 느려진다.

### 2. baseline 파일을 만들지 않는 이유

`CancellationPolicy`는 한 번에 고칠 수 없는 19건이 있어 파일별 허용 개수를 쓴다. 이번은 다섯 곳을 전부 고칠 수 있으므로 목표가 0이다. `src/` 전체를 훑어 확인했고, 다섯 곳을 고치면 서명에 걸리는 곳이 없다.

빈 baseline은 "0을 단언한다"를 돌려 말한 것에 불과하다. 정당한 예외가 실제로 생기면 그때 도입한다.

### 3. 위임 대상 5곳

| 위치 | 변경 | 동작 변화 |
|---|---|---|
| `SettlementPolicyService.cs:46` | `SqlObjectTypeClassifier.IsTableOrView(dep.Type)` | TVF가 프로파일링 대상에서 빠짐 |
| `SettlementPolicyService.cs:157` | `SqlObjectTypeClassifier.IsTableOrView(d.Type)` | TVF 참조가 테이블 경고 귀속에서 빠짐 |
| `AiService.cs:221` | `SqlObjectTypeClassifier.IsCodeObject(dep.Type)` | 대소문자 무시로 바뀜 |
| `SnapshotManager.cs:158` | private `GetDependencyCodeObjectType` 삭제, 호출부가 `ResolveCodeObjectType` 사용 | 없음 |
| `MetadataExporter.cs:340` | `ResolveCodeObjectType(dep.Type) == CodeObjectType.Procedure` | 없음 |

`SettlementPolicyService` 두 곳이 유일한 실질 변화다. 46행은 프로파일링 대상 진입을 막는 관문이고, 그 안에서 이름 키워드 필터(`Code`·`Master`·`Policy`·`Setting`·`Map`·`Type`·`Group`·`Rate`)가 한 번 더 좁힌다. TVF가 그 키워드에 걸리면 인자 없이 `SELECT ... FROM <TVF>`를 시도해 실패한다. 이 코퍼스의 유일한 TVF `UIF_SettleYMD`는 키워드에 걸리지 않으므로 오늘 눈에 보이는 변화는 없다. 예방이다.

`SnapshotManager`는 판정 순서가 다르다(PROCEDURE 먼저 vs FUNCTION 먼저). 두 리터럴을 동시에 포함하는 타입 문자열이 없으므로 결과는 같다. 반환 계약이 `CodeObjectType?`(null)과 `Unresolved`로 다르니, 얇은 어댑터를 남기지 말고 호출부를 `Unresolved` 검사로 바꿔 사본을 완전히 없앤다.

`MetadataExporter`는 `Unresolved`일 때 `functions`로 가며, 이는 현재의 거짓 분기와 같다.

### 4. 테스트 3건 삭제

**`CacheFormatVersion_ShouldBeTwoSoPreNormalizationArtifactsAreRebuilt`** (`CacheManagerTests.cs`) — 소스 문자열 `"CurrentCacheFormatVersion = 2"`만 단언한다. 형제 `IsCacheValid_ReturnsFalse_ForEntriesFromFormatVersionOne`이 실제 캐시 항목을 찍고 JSON 인덱스의 `FormatVersion`을 1로 되돌린 뒤 `IsCacheValid`가 false를 반환하는지 확인한다. 후자가 더 강하고 전자를 완전히 포함한다.

**`NormalizeStaticAnalysisForDefinition_ShouldCanonicaliseAgainstTheObjectKey`** (`DbMetadataServiceDetailsTests.cs`) — 본문이 `StaticAnalysisNormalizer.Normalize`를 직접 호출하고 `DbMetadataService`를 전혀 건드리지 않는다. `StaticAnalysisNormalizerTests`의 복제이며 이름이 서비스 커버리지를 암시한다. 실제 배선은 `DbMetadataService_ShouldNormaliseStaticAnalysisBeforeReturning`이 덮는다.

**`DbMetadataService_DelegatesClassificationToSqlObjectTypeClassifier`** (`DbMetadataServiceDetailsTests.cs`) — 스캐너가 이 가드를 대체한다. 파일 하나·리터럴 하나만 보던 것을 코드베이스 전체·네 리터럴로 넓히는 것이 §1의 목적이므로, 둘을 함께 두면 좁은 쪽이 넓은 쪽의 부분집합이 된다.

이 가드는 위임 호출의 등장 횟수(`>= 2`)도 단언한다. 그 단언은 파일 단위 검사가 단일 호출부 되돌리기를 못 잡는 약점을 메우려던 우회였고, 스캐너가 원시 판정 자체를 금지하면 필요가 없어진다. 다만 "위임이 통째로 사라지는" 경우는 스캐너가 잡지 못하므로(없앨 원시 판정도 함께 사라지므로), 그 경우는 각 지점의 동작 테스트가 맡는다.

세 건 모두 삭제 근거를 커밋 메시지에 남긴다. 테스트를 지우는 커밋은 왜 지웠는지가 코드보다 중요하다.

### 5. `]` 손상 수정

`StaticAnalysisNormalizer.SplitIdentifier`가 `]`를 무조건 버려서 `my]table`이 `mytable`이 된다. 미지원이 아니라 손상이다.

호출부를 전부 추적한 결과 대괄호 이름은 `Canonicalize`에 도달하지 않는다. 대괄호 형식은 `tableColumnsMap`(파서로 감), `MetadataExporter`(문서 렌더링), 정산 정책 프로파일링 키에서만 만들어진다. 즉 방어 코드가 손상 경로를 만든 셈이다.

방어를 없애지 않고 손상만 멈춘다. 분리할 때는 대괄호로 구분자 판단만 하고 문자는 보존한 뒤, 조각 단위로 `[`로 시작하고 `]`로 끝날 때만 양 끝을 벗긴다.

```
my]table            → 감싸지 않음 → my]table   (보존)
[PaymentDB].[dbo]   → 각각 감쌈   → PaymentDB, dbo
[my.table]          → 안 쪼갬     → my.table
```

ScriptDom이 이미 `]]`를 해제하므로 입력에 `]]`는 오지 않는다. T-SQL 이스케이프 파싱은 구현하지 않는다 — 오지 않는 입력을 위한 코드다.

### 6. 실패 처리

새 예외 경로를 만들지 않는다. `IsTableOrView` · `IsCodeObject` · `ResolveCodeObjectType`은 모두 null 입력에 안전하다(각각 `false` · `false` · `Unresolved`).

스캐너는 파싱할 수 없는 파일을 만나면 그 파일을 건너뛰고 계속한다. 정책 테스트가 스캔 자체의 실패로 깨지면 규칙이 버려진다.

## 테스트

**`TypeClassificationPolicyTests`** (신규)

스캐너 자체의 양성·음성을 인라인 소스로 고정한다. `CancellationPolicyTests`의 패턴을 따른다.

| 방향 | 케이스 |
|---|---|
| 양성 | `dep.Type.Contains("TABLE")` |
| 양성 | `objectType.Contains("VIEW")` |
| 양성 | `type.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase)` |
| 음성 | `logUpper.Contains("TABLE")` — 로그 텍스트 매칭 |
| 음성 | 주석 안의 `.Contains("TABLE")` |
| 음성 | 문자열 리터럴 안의 같은 텍스트 |
| 정책 | `src/` 전체 스캔 결과가 비어 있음 |

음성 케이스가 특히 중요하다. `CancellationPolicyTests`가 이유를 적어 두었다 — 거짓 양성을 내면 규칙이 버려진다.

**기존 테스트 파일 추가분**

- `SettlementPolicyServiceTests` — TVF 의존성이 프로파일링 대상에서 빠지는지. `IDbMetadataService`/`IAiService`를 NSubstitute로 대체하므로 DB가 필요 없다.
- `StaticAnalysisNormalizerTests` — `]`를 포함한 이름이 보존되는지, 감싼 대괄호는 여전히 벗겨지는지.
- `DbMetadataServiceDetailsTests` — 파일 단위 가드 삭제(§4). 남은 테스트는 그대로 통과해야 한다.

## 문서 동기화

- `docs/architecture.md` §4.1의 `SqlObjectTypeClassifier` 항목에 정책 스캐너로 강제된다는 사실 추가
- `AGENTS.md`에 타입 분류는 반드시 `SqlObjectTypeClassifier`를 거친다는 규칙과 그 근거(TVF가 `"TABLE"`을 포함함)

## 완료 기준

- `dotnet clean && dotnet build`에서 오류 0건, 경고 정확히 8건 (기존 `DbMetadataServiceTests`의 CS8600/CS8602)
- `dotnet test`가 전부 통과. 기대 총수는 `1,135 − 3(삭제) + 신규분`이며, 실측치가 이 산식과 어긋나면 의도치 않은 테스트 증감이 있었다는 뜻이다
- `TypeClassificationPolicyScanner`가 `src/` 전체에서 위반 0건 보고
- 위 문서 2종 동기화 완료

## 후속 (이번 범위 밖)

1. **`DependencyInfo.Type`의 타입화** — 문자열 가드가 아니라 타입 시스템으로 원시 판정을 차단한다. 근본적이지만 직렬화·스냅샷 호환성까지 번진다.
2. **프롬프트 계약 강화 3건** — UPDATE 컬럼 매핑표, `UPDATE ... FROM` 자기참조 의미, `SET` 절 동시평가.
3. **명세서 재발 방지 검증 게이트** — 남은 항목 중 가장 시급하다는 것이 선행 브랜치 최종 리뷰어의 평가다. 14개 명세서가 88~94점으로 검증을 통과하는 동안 이번 결함들이 하나도 걸리지 않았고, 선행 브랜치의 어떤 변경도 같은 종류의 허위 "컬럼 없음" 주장이 다시 만점권으로 통과하는 것을 막지 못한다.
4. **정확 일치 테이블 두 곳을 분류기로 통합**(출처: 최종 브랜치 리뷰) — `DependencyAnalysisOrchestrator.TryParseCodeObjectType`과 `MetadataExporter.NormalizeCodeObjectDdlFolder`가 `SqlObjectTypeClassifier` 밖에서 각자 정확 일치 `switch` 테이블로 코드 객체 여부를 판정한다. 두 권위가 가장자리에서 어긋난다 — `"P"`/`"FN"`/`"TF"`는 두 테이블에서 Procedure/Function이지만 분류기에서는 `Unresolved`이고, 반대로 `AGGREGATE_FUNCTION`/`EXTENDED_STORED_PROCEDURE`는 분류기에서는 코드 객체이지만 두 테이블은 모른다. `MetadataExporter`는 같은 `procedures`/`functions` 판정에 두 메커니즘을 함께 돌리고 있다(159행의 `NormalizeCodeObjectDdlFolder`와 341행의 분류기 위임). 스캐너는 원시 부분 문자열 판정만 잡으므로 이 정확 일치 테이블은 못 본다 — 오늘 오작동하지 않는 것은 실제 `Type` 값이 전부 `type_desc`에서 오기 때문이지, 게이트가 막고 있어서가 아니다.
5. **`DbMetadataService`의 재귀 의존성 경로에 대한 동작 테스트 부재**(출처: 최종 브랜치 리뷰) — §4는 "위임이 통째로 사라지는" 경우를 각 지점의 동작 테스트가 잡는다고 적었지만, `DbMetadataService`의 재귀 의존성 경로는 라이브 DB가 있어야 실행되어 커버되지 않는다. 그래서 이 경로에서 분류기 위임을 통째로 되돌리는 편집이 있어도 지금은 테스트로 잡히지 않는다.
