using System.Collections.Generic;
using System.Threading.Tasks;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public interface IVerificationUserInteraction
    {
        // 일반 진행 상태 및 안내 메시지 출력
        void NotifyStatus(string message);

        // 예외 메시지 및 경고 출력
        void NotifyError(string message);
        
        // DB 메타데이터 수집 중 발생한 경고 목록 출력
        void NotifyWarnings(string selectedOption, List<string> warnings);

        // L1 기계 검증 단계의 오류 정보 출력
        void NotifyL1Errors(string selectedOption, int attempt, int maxAttempts, List<string> errors);

        // L2 AI 리뷰의 결함 피드백 코멘트 출력
        void NotifyL2Defects(string selectedOption, int attempt, int maxAttempts, string feedbackComment);

        // 검증 파이프라인 단계 성공 알림
        void NotifyValidationSuccess(string selectedOption);

        // L3 인간 개입형 검증 화면 제공 및 승인/피드백 결과 대기
        // outcome은 파이프라인이 실제로 도달한 종료 상태를 명시적으로 전달한다.
        // specificationMarkdown 문자열을 파싱해 상태를 되짚는 방식은 문서 헤더가
        // 아직 씌워지지 않은 시점(파이프라인 진행 중)에는 항상 실패하므로 쓰지 않는다.
        //
        // structureRedraftSupported가 필요한 이유: 이 메서드는 두 파이프라인이 함께 쓴다.
        // 통합 배치 계획 경로에만 다시 세울 목차(PlanStructure)가 있고, 단일 SP 명세서
        // 경로에는 아예 없다. 구조 변경 여부를 무조건 물으면 명세서 경로에서는 사용자가
        // 답해도 그 답을 쓸 곳이 없어 "답했는데 아무 일도 일어나지 않는" 상태가 된다.
        // 기본값 false는 목차를 가지지 않은 호출부의 안전한 쪽이다.
        Task<HumanReviewResult> RequestHumanReviewAsync(
            string selectedOption,
            string specificationMarkdown,
            VerificationOutcome outcome,
            bool structureRedraftSupported = false);

        // AI가 유추한 메타데이터 설명을 DB에 동기화할지 사용자 동의 요청
        Task<bool> ConfirmMetadataSyncAsync(string selectedOption);

        // 멀티태스크 진행률 상황 표시 스코프 생성
        IMultiProgressScope CreateProgressScope(string title);
    }
}
