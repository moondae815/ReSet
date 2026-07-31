# 재귀 SP 명세 NOTE 헤더 일관성 설계

## 목적

루트 SP와 재귀 분석된 참조 SP/UDF의 `docs/Spec.md`가 동일한 NOTE 메타데이터를 표시하게 한다.

## 현재 문제

루트 SP는 CLI의 `SaveOutputsAsync`에서 작성일시, 분석 AI 정보 및 최종 신뢰도 점수를 포함한 NOTE 헤더를 추가한다. 재귀 객체는 `DependencyAnalysisOrchestrator`의 별도 저장 경로를 사용하므로 동일한 헤더가 누락된다.

## 설계

1. NOTE 헤더를 렌더링하는 공통 함수를 도입한다.
2. 루트 저장과 재귀 객체 저장이 공통 함수를 호출한다.
3. 공통 함수는 작성일시, 공급자, 모델명, Effort 및 `ReviewResult`를 받아 현재 루트 문서와 동일한 NOTE 형식을 반환한다.
4. 재귀 객체는 `CodeObjectAnalysisResult.Review`를 전달하여 최종 신뢰도 점수를 포함한다.
5. YAML 점수 헤더, 본문, Thinking 산출물 및 의존성 링크 동작은 변경하지 않는다.

## 오류 처리

리뷰 결과가 없으면 기존 루트 저장 정책과 동일하게 신뢰도 점수 줄을 생략하고, NOTE의 작성일시와 분석 AI 정보는 계속 출력한다.

## 검증

재귀 분석 산출물의 `Spec.md`에 `[!NOTE]`, 분석 AI 정보, 그리고 5개 세부 점수를 포함한 최종 신뢰도 줄이 기록되는 회귀 테스트를 추가한다.
