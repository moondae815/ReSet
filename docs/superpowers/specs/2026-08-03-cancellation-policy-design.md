# 취소 처리 정책과 재발 방지 장치 설계

- 작성일: 2026-08-03
- 상태: 설계 승인됨 (구현 계획 수립 전)
- 선행 작업: `2026-08-03-verification-annotation-cleanup` (병합 완료, `169e5f6`)

## 배경

세 사이클 연속으로 같은 결함을 발견했다. **`OperationCanceledException`이 삼켜져 사용자의 Ctrl-C가 무시된다.** 매번 고쳤고, 매번 다음 사이클에서 새 사례가 나왔다.

| 사이클 | 발견 방법 | 고친 곳 |
|---|---|---|
| 1 | 리뷰어가 diff 문맥에서 목격 | `catch { }` 1곳을 기록 |
| 2 | `catch { }` grep | 3곳 |
| 3 | 태스크 리뷰어가 증상을 추적 | `catch (Exception ex)` + 알림 2곳 |

세 번 모두 **사람이 눈으로 찾았고**, 매번 새 검색 패턴이 필요했다. 직전 사이클의 최종 리뷰가 남은 사례를 훑으면서 두 가지 새 모양을 찾았는데, 둘 다 어떤 `catch` 패턴 검색으로도 나오지 않는다.

이것은 "몇 군데를 더 고치는" 문제가 아니라 **규칙이 없는** 문제다.

## 결함의 네 가지 모양

| # | 모양 | 상태 | grep 가능 |
|---|---|---|---|
| 1 | 빈 `catch { }` | 고침 | ○ |
| 2 | `catch (Exception ex)` + 알림 후 계속 | 고침 | △ 문맥 필요 |
| 3 | 안쪽 catch가 바깥의 올바른 핸들러를 가림 | **남음** | ✗ |
| 4 | OCE를 다른 예외 타입으로 세탁 | **남음** | ✗ |

### 모양 3 — 가리는 catch

`src/ReSet.Cli/Program.cs`의 TUI 통합 설계 경로:

```csharp
try
{
    // …
    await RunCodegenEngineAsync(…, cancellationToken: activeCts.Token);   // :1338
}
catch (Exception ex)                                                       // :1348
{
    AnsiConsole.MarkupLine($"[red]에러:[/] … 오류 발생: {Markup.Escape(ex.Message)}");
}
```
```csharp
catch (OperationCanceledException)                                         // :1353 — 바깥 try
{
    AnsiConsole.MarkupLine("\n[yellow]통합 설계서 수립 작업이 사용자에 의해 중단되었습니다…[/]");
    continue;
}
```

올바른 핸들러가 **다섯 줄 아래**에 있지만 바깥 try에 속한다. 안쪽 catch가 먼저 취소를 소비하고, 흐름은 정상적으로 메인 메뉴 루프로 떨어진다.

배치 경로도 같다 — 코드젠 호출 `:833`, 안쪽 넓은 catch `:844`, 올바른 핸들러는 바깥 `:749`.

같은 파일 `:922` 호출부는 `catch (OperationCanceledException)`(`:936`)이 넓은 catch보다 **먼저** 와서 정상 동작한다. 규칙이 아니라 우연이다.

### 모양 4 — 타입 세탁

`src/ReSet.Core/Services/ExternalCliCodingEngine.cs:106`:

```csharp
catch (Exception ex)
{
    Log.Error(ex, "외부 코딩 에이전트 기동 중 예외 발생 …");
    throw new InvalidOperationException($"외부 코딩 엔진({Name}) 기동 중 오류가 발생했습니다. …", ex);
}
```

`process.WaitForExitAsync(cancellationToken)`가 던진 OCE가 `InvalidOperationException`으로 감싸여 올라간다. 하류의 올바른 핸들러 세 곳(`Program.cs:936`, `:1353`, 그리고 `:2003`)이 **전부 매칭에 실패한다.**

### 그 밖에 확인된 곳

- **`src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:267`** — 캐시 유효성 확인을 감싼 catch. 취소가 "캐시 확인 중 오류"로 기록되고 파이프라인은 **전체 AI 분석으로 진행한다.** 창은 좁지만(로컬 파일 읽기) 결과는 가장 비싸다.
- **`src/ReSet.Core/Services/DbMetadataService.cs`** — 넓은 catch 12곳, OCE 필터 0곳. DFS로 의존성 그래프를 걷는 루프들이 예외를 삼키고 계속 걷는다.

## 규모

`src/` 전체에 넓은 catch 118곳, OCE 필터가 있는 것 13곳.

취소 토큰을 쓰는 파일별 분포(상한 추정 — 이 중 동기 IO를 감싸는 정당한 soft-fail이 섞여 있다):

| 파일 | 넓은 catch | 필터 |
|---|---|---|
| `ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 22 | 10 |
| `ReSet.Validator.Cli/Program.cs` | 17 | 0 |
| `ReSet.Cli/Program.cs` | 16 | 1 |
| `ReSet.Core/Services/DbMetadataService.cs` | 12 | 0 |
| `ReSet.Core/Services/DependencyAnalysisOrchestrator.cs` | 7 | 2 |
| `ReSet.Core/Services/MetadataExporter.cs` | 6 | 0 |
| 나머지 10개 파일 | 23 | 0 |

**정확한 위반 수는 도구 없이 셀 수 없다.** 넓은 catch 중 어느 것이 취소 가능한 `await`를 감싸는지는 C# 구조를 파싱해야 알 수 있다. 이 사실 자체가 도구를 만드는 근거다.

## 설계

### 하나의 규칙이 네 모양을 다 잡는다

모양 3과 4가 다른 결함처럼 보이는 것은 **결과**가 다르기 때문이지, `catch` 자체는 1·2와 같다. 안쪽 catch에 필터를 달면 OCE는 애초에 잡히지 않아 바깥 핸들러로 간다. 세탁도 마찬가지 — 필터가 있으면 OCE는 감싸이지 않고 그대로 통과한다.

**규칙:** `catch` 절이 다음을 **모두** 만족하면 위반이다.

1. `OperationCanceledException`을 잡을 수 있다 — 타입이 `Exception`, `SystemException`, `OperationCanceledException`, `TaskCanceledException`이거나 타입이 생략된 `catch`
2. 대응하는 `try` 블록 안에 **`CancellationToken`을 인수로 넘기는 `await`**가 있다
3. `when` 필터가 OCE를 배제하지 않는다
4. catch 본문이 OCE를 다시 던지지 않는다

2번이 정밀도의 핵심이다. 동기 IO를 감싸는 soft-fail은 취소와 무관하므로 지적하지 않는다. 이 코드베이스에 넓은 catch가 118곳이나 있는 정당한 이유이기도 하다.

### 구현

`tests/ReSet.Core.Tests/CancellationPolicyTests.cs`. 새 의존성은 `Microsoft.CodeAnalysis.CSharp`이며 **테스트 프로젝트에만** 추가한다.

`src/` 아래 모든 `.cs`를 **구문 트리로만** 파싱한다. 시맨틱 모델(컴파일 필요)을 쓰지 않으므로 빠르고 프로젝트 참조가 필요 없다.

구문 트리만으로는 `catch (SomeCustomException)`이 OCE를 잡을 수 있는지 판정할 수 없다. 이 코드베이스에 그런 사례는 없고, 있더라도 규칙이 놓치는 방향(거짓 음성)이므로 안전하다. 거짓 양성으로 개발을 막는 것보다 낫다.

`await` 판정은 `AwaitExpressionSyntax` 아래의 호출 인수 중 이름이 `cancellationToken`/`token`/`ct`이거나 `.Token`으로 끝나는 것이 있는지로 한다. 이름 기반이므로 완전하지 않지만, 이 저장소의 규약이 일관되어 실용적으로 충분하다.

### 기준선은 파일별 개수 래칫

체크인되는 텍스트 파일 하나: `tests/ReSet.Core.Tests/cancellation-policy-baseline.txt`

```
# 취소를 삼킬 수 있는 catch의 파일별 허용 개수.
# 목록에 없는 파일은 0건을 뜻한다.

ReSet.Cli/Program.cs=<실측>
ReSet.Core/Services/DbMetadataService.cs=<실측>
ReSet.Validator.Cli/Program.cs=<실측>
```

**이 파일의 초기 숫자는 도구가 채운다.** 위 규모 표의 수치는 넓은 catch의 총량이지 위반 수가 아니다 — 위반은 그중 취소 가능한 `await`를 감싸는 것만이므로 더 적다. 구현 시 규칙을 먼저 만들고, 그 출력을 그대로 기준선의 초기값으로 삼는다. 계획이 숫자를 미리 지어내지 않는다.

경로는 `src/`를 기준으로 한 상대 경로이며 구분자는 `/`로 정규화한다(Windows에서도 같은 파일이 되도록).

| 상황 | 판정 |
|---|---|
| 실제 > 허용 | **실패** — 새 위반 |
| 실제 < 허용 | **실패** — 고쳤는데 기준선을 안 내렸다 |
| 실제 == 허용 | 통과 |

**두 번째 방향이 래칫의 핵심이다.** 허용치가 실제보다 높아도 통과하게 두면 목록이 썩어 무의미해진다. 고칠 때마다 기준선을 함께 내리도록 강제하면 숫자가 단조 감소하고, 부채가 눈에 보이는 채로 줄어든다.

**줄 번호가 아니라 개수를 쓰는 이유**는 줄 번호가 위쪽 편집만으로 어긋나기 때문이다. `(파일, 메서드)` 단위가 더 정밀하지만 메서드 이름이 바뀌면 기준선이 흔들린다.

파일 단위의 실재하는 한계: 한 파일에서 하나를 고치고 동시에 다른 하나를 새로 만들면 개수가 상쇄되어 놓친다. 그 시나리오는 같은 파일을 두 방향으로 동시에 편집해야 성립하며, 그때는 diff 리뷰가 잡는다.

### 실패 메시지가 장치의 절반이다

개수만 알려주면 쓸모없다. 위반한 파일의 **모든** 지점을 다음 형태로 출력한다.

```
ReSet.Cli/Program.cs: 허용 15건, 실제 16건

  ReSet.Cli/Program.cs:1348 (RunAsync)
  ReSet.Cli/Program.cs:844 (RunAsync)
  …

새 위반을 만들었다면 위 목록에서 방금 편집한 줄을 찾으십시오.
의도한 수정이라면 cancellation-policy-baseline.txt의 개수를 조정하십시오.
```

메서드명은 가장 가까운 `MethodDeclarationSyntax`/`LocalFunctionStatementSyntax`에서 취하고, 최상위 문 안이면 `<top-level>`로 표기한다.

## 이번 사이클에 비우는 파일

**순서가 설계의 일부다.** 규칙을 먼저 만들고 기준선을 현재 수치로 고정한 뒤(전부 통과), 그다음 파일별로 고치며 기준선을 내린다. 실제 위반 개수는 도구가 알려주므로 계획이 미리 숫자를 지어내지 않는다.

최종 리뷰가 구체적 실패 시나리오를 실증한 네 파일을 비운다.

| 파일 | 확인된 해악 |
|---|---|
| `src/ReSet.Cli/Program.cs` | 코드젠 호출의 안쪽 catch가 다섯 줄 아래 올바른 핸들러를 가림 (`:844`, `:1348`) |
| `src/ReSet.Core/Services/ExternalCliCodingEngine.cs` | OCE를 `InvalidOperationException`으로 세탁 (`:106`) |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 캐시 확인 중 취소가 삼켜지고 전체 AI 분석으로 진행 (`:267`) |
| `src/ReSet.Core/Services/DbMetadataService.cs` | DFS 그래프 순회 중 취소해도 계속 걸음 |

나머지(`ReSet.Validator.Cli` 17건, `MetadataExporter` 6건 등)는 기준선에 남겨 다음 사이클로 넘긴다.

### 두 Important는 함께 착지해야 한다

`ExternalCliCodingEngine`만 고치면 OCE가 날것으로 올라오지만 `Program.cs`의 가리는 catch가 여전히 삼킨다. 반대로 `Program.cs`만 고치면 `InvalidOperationException`이 올라와 OCE 핸들러에 걸리지 않는다. **어느 한쪽만으로는 증상이 그대로다.** 같은 태스크에 넣는다.

## 테스트 전략

| 대상 | 방법 |
|---|---|
| 규칙 자체 | 임시 위반을 심어 실패를 확인하고 되돌린다. 래칫 양방향(실제>허용, 실제<허용)을 각각 확인한다 |
| 규칙의 정밀도 | `CancellationToken`을 넘기지 않는 `await`를 감싼 catch가 지적되지 **않는지** 확인한다. 거짓 양성은 개발을 막으므로 거짓 음성보다 비싸다 |
| `VerificationPipelineOrchestrator` 캐시 경로 | 행동 테스트 — `ICacheManager.IsCacheValid`를 스텁해 OCE를 던지고 `RunCodeObjectPipelineAsync`가 전파하는지 단언 |
| `Program.cs` + `ExternalCliCodingEngine` | **행동 테스트 없음** |
| `DbMetadataService` DFS | **행동 테스트 없음** |

**뒤 두 줄이 이 사이클의 정직한 한계다.** `Program.cs`의 해당 구간은 최상위 문 안의 지역 흐름이고, `ExternalCliCodingEngine`은 외부 프로세스를 기동하며, `DbMetadataService`는 실제 SQL 연결을 요구한다. 셋 다 단위 테스트로 격리할 수 없다.

그래서 규칙이 중요하다. **행동 테스트를 쓸 수 없는 곳일수록 구문 규칙이 유일한 방어선이다.**

캐시 경로 테스트는 `ICacheManager`가 인터페이스라 스텁 가능하다. `MechanicalValidator`와 달리 NSubstitute로 대체된다.

## 에러 처리

프로젝트 규약을 그대로 따른다.

- 필터 추가는 취소 외 예외의 soft-fail 동작을 바꾸지 않는다. 로그와 알림이 그대로 남는다
- 아키텍처 테스트는 파일을 읽기만 한다. 읽기 실패는 테스트 실패로 드러나야 하므로 삼키지 않는다
- Spectre.Console 출력에 새로 들어가는 런타임 값이 없다
- API 키를 소스나 `appsettings.json`에 하드코딩하지 않는다

## 범위 밖

- **`ReSet.Validator.Cli`(17건)와 `ReSet.Validator.Core`** — 기준선에 남긴다. 이번 세 사이클 동안 한 번도 검토하지 않은 영역이라 별도 조사가 필요하다
- **`MetadataExporter`(6건), `AiService`(5건), `DependencyAnalysisOrchestrator`(5건 미필터)** — 기준선에 남긴다
- **`await Task.WhenAll`이 첫 예외만 표면화하는 문제** — `VerificationPipelineOrchestrator`의 로컬 프로바이더 병렬 분기에서, `IOException`과 `OperationCanceledException`이 동시에 발생하면 필터가 통과해 취소가 삼켜진다. 규칙은 이를 잡지 못한다(필터가 있으므로). 별도 결정이 필요하다
- **L2 리뷰 호출 재시도 인프라**와 **통합 루프의 점수 임계값 강제** — 취소가 아니라 검증 강도의 문제이며 새 정책 결정을 요구한다
- **`SpecHeader`의 인터페이스 점수 필드 부재** — 고칠 때 `?? 10` 폴백 뒤에 놓여 조작 만점 위험이 생기므로 별도 설계가 필요하다
