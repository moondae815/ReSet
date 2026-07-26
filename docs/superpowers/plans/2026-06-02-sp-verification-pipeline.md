# [구현 계획서] SP Reverse-Engineering 명세서 3단계 검증 파이프라인 (Verification Pipeline) 연동

본 구현 계획서는 검증 설계서([2026-06-02-sp-verification-pipeline-design.md](../specs/2026-06-02-sp-verification-pipeline-design.md))에 명세된 3단계 검증 아키텍처를 점진적이고 안전하게 연동하기 위한 상세 수행 일정 및 작업 단위 리스트를 제공합니다.

---

## 🛠️ 작업 목록 (Tasks Checklist)

### [x] Task 1: MechanicalValidator 구현 (1단계 검증)
- **목표**: 마크다운 결과물 내에 필수 색션 헤더 및 Mermaid 구문 문법 오류 여부를 정적으로 체크하는 유틸리티 구현.
- **수행 상세**:
  - [xUnit 테스트 작성]: `tests/ReSet.Core.Tests/MechanicalValidatorTests.cs`를 생성하여 아래 항목을 테스트합니다. (실패하는 테스트부터 작성)
    - `Validate_WithValidMarkdown_ShouldReturnTrue`
    - `Validate_WithMissingHeaders_ShouldReturnFalse`
    - `Validate_WithInvalidMermaidBrackets_ShouldReturnFalse`
  - [구현]: `src/ReSet.Core/Services/MechanicalValidator.cs`를 생성하여 정규식 기반 정적 분석 로직 구현.
  - [빌드 및 확인]: `dotnet test`로 구현 코드가 모든 테스트를 통과하는지 확인.
  - [Git 커밋]: `feat: Implement MechanicalValidator and associated unit tests`

### [x] Task 2: AI 교차 검증 및 자가 수정 (2단계 검증)
- **목표**: `AiService` 내에 교차 검토용 API를 구축하고, 1차 오류 검출 시 최대 1회 자가 보완 재생성 요청 연동.
- **수행 상세**:
  - [xUnit 테스트 작성]: `tests/ReSet.Core.Tests/AiServiceTests.cs` 파일에 `ReviewSpecificationAsync` 호출 시 API Key 누락에 대해 ArgumentException이 발생하는지 등 검증 테스트 추가.
  - [구현]: 
    - `src/ReSet.Core/Services/IAiService.cs` 및 `src/ReSet.Core/Services/AiService.cs` 수정.
    - `ReviewSpecificationAsync` 메서드 구현 및 리뷰어 전용 System Prompt 작성.
    - `GenerateSpecificationAsync` 메서드 시그니처에 `feedbackLog` 매개변수 추가하여 이전 실패 이력 적용.
  - [빌드 및 확인]: `dotnet test` 동작 확인.
  - [Git 커밋]: `feat: Add AI Cross-Review API and self-correction input support`

### [x] Task 3: TUI 인간 개입형(Human-in-the-loop) 피드백 루프 연동 (3단계 검증)
- **목표**: `Program.cs` 내의 메인 비즈니스 루프(TUI 및 배치)에 3단계 검증 파이프라인 연동.
- **수행 상세**:
  - [구현]: 
    - `src/ReSet.Cli/Program.cs`를 수정하여 1차 명세서 작성 후 L1(기계 검증), L2(AI 리뷰) 검사를 수행하고, 자가 수정 N=1회 제한 제어 루프 연동.
    - TUI 모드 한정으로 1차 완료본 정보를 요약 패널로 띄우고, `[Approve]`, `[Feedback]`, `[Cancel]` 선택 메뉴 노출 및 피드백 텍스트 프롬프트 동작 구현.
    - 배치 모드에서는 L3를 생략하고 검증 완료본을 즉시 저장하도록 분기 처리.
  - [빌드 및 확인]: `dotnet build` 수행하여 정상 컴파일 확인.
  - [Git 커밋]: `feat: Integrate 3-stage verification pipeline flow in Program.cs`

---

## 🧪 TDD(테스트 주도 개발) 규칙

모든 비즈니스 로직(Task 1, Task 2)은 다음 순서를 엄격히 준수합니다.
1. `tests/` 디렉터리에 실패하는(컴파일 에러 혹은 Assert 실패) xUnit 테스트 작성.
2. `dotnet test`로 실패를 수동 확인.
3. `src/` 디렉터리에 최소한의 제품 코드를 작성하여 빌드 에러 해결 및 테스트 성공.
4. `dotnet test` 성공 확인 후 해당 기능 단위로 개별 Git 커밋 실행.
