# 재귀 분석 진행 UI 설계

## 목표

참조 SP/UDF를 재귀 분석할 때도 일반 분석과 동일하게 스피너·경과시간과 생성/검증 상태를 TUI에 표시한다.

## 원인

재귀 파이프라인은 `RecursiveAnalysisUserInteraction`을 사용한다. 이 어댑터는 모든 상태 알림을 무시하고 `NullProgressScope`를 반환한다. 의존성 오케스트레이터는 별도로 일반 텍스트 `분석 중`만 출력한다.

## 설계

`RecursiveAnalysisUserInteraction`은 `NotifyStatus`와 진행 스코프 생성을 원래 `ConsoleUserInteraction`으로 위임한다. 재귀 파이프라인이 생성 및 L2 검증 시 만드는 `ConsoleProgressScope`가 그대로 스피너와 경과시간을 표시한다.

의존성 오케스트레이터의 시작 콜백은 콘솔에 직접 출력하지 않는다. 객체별 파이프라인이 표시하는 상태와 진행 UI가 단일 출력 경로가 되어 TUI 화면 충돌을 방지한다. 실패 목록 렌더링과 L3 사용자 검토 위임은 유지한다.

## 검증

`RecursiveAnalysisUserInteraction`이 상태 알림과 진행 스코프 생성을 내부 UI에 위임하는 단위 테스트를 추가한다. 기존 재귀 의존성 분석 테스트와 전체 테스트·빌드를 실행한다.
