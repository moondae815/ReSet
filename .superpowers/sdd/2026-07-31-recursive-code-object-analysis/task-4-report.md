# Task 4 보고서 — SP/UDF 공통 검증 파이프라인 및 UDF 프롬프트

## 상태

완료. 구현 커밋: `c4cc481` (`feat: analyze functions through verification pipeline`)

## 변경 사항

- `RunPipelineAsync`를 Procedure `CodeObjectKey`를 생성하는 호환 래퍼로 전환하고, 새 `RunCodeObjectPipelineAsync`가 공통 `IDbMetadataService.GetCodeObjectDetailsAsync` 진입점을 사용하도록 했다.
- 단일 코드 객체 실행 결과를 전달할 수 있도록 `CodeObjectPipelineResult`에 `SpDef`, `SpecMarkdown`, `Review`, `ThinkingText`를 추가했다. 기존 재귀 그래프 컬렉션 속성은 보존했다.
- 공통 파이프라인의 상태 문구에 `SP`/`UDF` 종류와 canonical object key를 표시했다. Thinking은 기존처럼 반환값으로만 축적하며 UI 상태 메시지로 노출하지 않는다.
- Function에는 Procedure 전용 로컬 분할(Deconstruct/section) 프롬프트를 적용하지 않고 일반 명세 생성 경로를 사용하게 했다.
- Function 전용 영문 시스템 프롬프트와 L2 리뷰 프롬프트를 추가했다. return contract, determinism, side effects, formula, referenced tables/functions, TVF result schema를 다루며 `BEGIN TRAN`, `ROLLBACK`, Procedure 오류 반환 코드 요구를 포함하지 않는다. 최종 명세 출력 언어는 한국어로 유지했다.
- UDF 공통 파이프라인 조회와 Function 프롬프트 계약 테스트를 추가했다. 기존 SP 단위 테스트는 새 공통 조회 진입점에 맞춰 레거시 메타데이터 fixture를 브리지했다.

## RED/GREEN 증거

1. RED

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~RunCodeObjectPipelineAsync_UsesFunctionMetadata|FullyQualifiedName~FunctionPrompt"
   ```

   새 테스트 추가 직후 `VerificationPipelineOrchestrator`에 `RunCodeObjectPipelineAsync` 정의가 없다는 `CS1061` 컴파일 오류로 실패했다.

2. GREEN — Task 4 추가 테스트

   같은 필터 명령이 `2 passed, 0 failed`로 통과했다.

3. GREEN — 집중 회귀

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests|FullyQualifiedName~AiServiceTests"
   ```

   `76 passed, 0 failed`.

4. GREEN — Core 전체

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj
   ```

   `257 passed, 0 failed`.

5. 변경 확인

   ```text
   git diff --check
   ```

   공백 오류 없음.

## 우려 및 후속 참고

- `CodeObjectPipelineResult`는 기존 재귀 그래프 결과 컬렉션을 보유하던 타입이어서, 새 단일 객체 실행 결과 속성을 추가해 API 계약을 충족했다. 기존 컬렉션은 변경하지 않았다.
- Procedure는 공통 조회가 비정상적으로 빈 결과를 반환하는 구형 메타데이터 어댑터에 한해 기존 `GetSpDetailsAsync`로 보완한다. 정상 경로와 Function은 공통 조회만 사용한다.
- 테스트 실행 중 기존 `DbMetadataServiceTests.cs`의 nullable 경고(CS8600/CS8602)가 출력되었으나, Task 4 변경으로 새로 발생한 오류는 없고 모든 테스트는 통과했다.

## Fix Round 1/5

### 수정 내용

- 요청 `CodeObjectKey.Type`과 메타데이터의 `SpDefinition.ObjectType`이 불일치할 때 요청 키를 권위 값으로 적용한다. 이 경우 `ObjectKey`도 요청 키로 동기화해 Function이 Procedure 프롬프트나 L2 체크리스트로 떨어지지 않게 했다.
- TVF return contract의 각 컬럼에 이름, 타입, nullable/not nullable을 명시했다. 생성 프롬프트와 Function L2 리뷰 프롬프트 컨텍스트 모두에 반영된다.
- dynamic-effort UDF가 Fast-Pass에 실패했을 때 Function 전용 합성 지시를 사용하도록 분기했다. 함수 품질 기준은 formula, referenced tables/functions, return contract/TVF schema, determinism/side effects, readability이며 Procedure/transaction/isolation 지시를 포함하지 않는다.

### RED/GREEN 증거

1. RED

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~RunCodeObjectPipelineAsync_UsesFunctionMetadata|FullyQualifiedName~DynamicFunctionConsolidation|FullyQualifiedName~FunctionPrompt_DoesNotRequireTransaction"
   ```

   구현 전 3건이 실패했다.

   - metadata `ObjectType=Procedure`인 Function 요청이 Function 생성기를 호출하지 못해 명세가 `null`이었다.
   - TVF `Amount decimal(18,2)`의 `(nullable)` 컨텍스트가 없었다.
   - dynamic Function 합성 지시에 `Stored Procedure` 및 transaction/isolation 평가 문구가 포함됐다.

2. GREEN — 결함별 및 Function L2 계약

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~FunctionPrompt|FullyQualifiedName~RunCodeObjectPipelineAsync_UsesFunctionMetadata|FullyQualifiedName~DynamicFunctionConsolidation"
   ```

   `4 passed, 0 failed`.

3. GREEN — 집중 회귀

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj --filter "FullyQualifiedName~VerificationPipelineOrchestratorTests|FullyQualifiedName~AiServiceTests"
   ```

   `78 passed, 0 failed`.

4. GREEN — Core 전체

   ```text
   dotnet test tests/ReSet.Core.Tests/ReSet.Core.Tests.csproj
   ```

   `259 passed, 0 failed`.

### 우려

- 기존 `DbMetadataServiceTests.cs`의 nullable 경고(CS8600/CS8602)는 유지되며, 이번 수정으로 새 경고나 실패는 발생하지 않았다.
