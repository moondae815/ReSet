using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 지역 모델 경로에서 이번 회차에 다시 만들 범위.
    /// Overview는 `## 개요`+`## 파라미터 목록`, Crud는 `## CRUD 분석`,
    /// Logic은 `## 로직 흐름 요약`+`## 비즈니스 흐름 시각화`에 해당한다.
    /// </summary>
    public sealed record RegenerationScope(bool RunStage1, bool Overview, bool Crud, bool Logic)
    {
        public static readonly RegenerationScope Everything = new(true, true, true, true);
    }

    /// <summary>
    /// 재생성 범위를 구조화된 신호에서 계산한다.
    ///
    /// 이 클래스가 존재하는 이유: 이전 구현은 Actor에게 보낼 피드백 문자열에 키워드를
    /// 매칭해 범위를 정했다. CriticFeedbackLog가 넣는 항목별 점수 줄이 항상 "CRUD"라는
    /// 글자를 포함하므로, 누적 이력이 있는 모든 재시도 회차에서 CRUD 섹션이 무조건
    /// 재생성됐다. 더 근본적으로는 LLM이 쓴 산문에 키워드를 거는 방식이라 프롬프트
    /// 문구가 바뀌면 아무 신호 없이 오작동한다.
    ///
    /// Critic은 이미 항목별 점수를 구조화된 값으로 돌려주고, MechanicalValidator는
    /// 오류를 ErrorType으로 분류해 둔다. 그 둘을 쓴다.
    /// </summary>
    public static class RegenerationScopeSelector
    {
        /// <summary>
        /// L2 리뷰 점수에서 범위를 정한다. 정합성·CRUD가 미달이면 구조화 데이터 자체가
        /// 틀렸다는 뜻이므로 Stage 1을 다시 돈다. 나머지 셋은 표현의 문제라 이미 뽑아
        /// 둔 구조를 재사용한다.
        /// </summary>
        public static RegenerationScope FromReview(ReviewResult review, int scoreThreshold)
        {
            if (review == null)
            {
                return RegenerationScope.Everything;
            }

            bool accuracy = review.ScoreAccuracy < scoreThreshold;
            bool crud = review.ScoreCrud < scoreThreshold;
            bool interfaceDefinition = review.ScoreInterface < scoreThreshold;
            bool readability = review.ScoreReadability < scoreThreshold;
            bool exception = review.ScoreException < scoreThreshold;

            var scope = new RegenerationScope(
                RunStage1: accuracy || crud,
                Overview: interfaceDefinition,
                Crud: crud,
                Logic: accuracy || readability || exception);

            // 점수는 모두 기준을 넘겼는데 결함이 지적된 경로가 있다.
            // 어느 섹션인지 지역화할 근거가 없으므로 전부 다시 만든다.
            return scope.Overview || scope.Crud || scope.Logic
                ? scope
                : RegenerationScope.Everything;
        }

        /// <summary>
        /// L1 오류 종류에서 범위를 정한다. L1은 형식 검증이라 구조화 데이터에 영향이
        /// 없으므로 Stage 1은 언제나 건너뛴다.
        ///
        /// HeaderMissing 메시지에서 헤더 이름을 파싱해 섹션을 특정하지 않는다 —
        /// 산문 추측을 없애자는 것이 이 클래스의 취지이므로 자기모순이 된다.
        /// </summary>
        public static RegenerationScope FromL1Errors(IReadOnlyList<DetailedError> errors)
        {
            if (errors == null || errors.Count == 0)
            {
                return RegenerationScope.Everything;
            }

            bool allMermaid = errors.All(e =>
                e.Type == ErrorType.MermaidQuoteMissing || e.Type == ErrorType.MermaidCliError);

            return allMermaid
                ? new RegenerationScope(RunStage1: false, Overview: false, Crud: false, Logic: true)
                : new RegenerationScope(RunStage1: false, Overview: true, Crud: true, Logic: true);
        }
    }
}
