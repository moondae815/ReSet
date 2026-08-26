using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 재시도 회차마다 명세서 목록에 덧붙는 피드백 항목. 명세서가 아니다.
    ///
    /// [왜 구분이 필요한가]
    /// 파이프라인은 검토 피드백을 프롬프트에 실어 나르려고 명세서 목록에
    /// <c>Feedback_Log.txt</c>를 끼워 넣는다. 전달 수단으로는 편하지만, 받는 쪽이
    /// 그것을 명세서와 구분하지 않으면 세 가지가 어긋난다.
    ///
    /// 1. 개수 - 프롬프트가 "Total Legacy Stored Procedures to Consolidate: N"이라고
    ///    선언하는데, 재시도 회차부터 N이 하나 부풀어 실제 프로시저 수와 달라진다.
    ///    모델이 그 수를 단계 설계의 기준으로 삼으므로 틀린 수를 주면 안 된다.
    /// 2. 자리 - 피드백이 "[Provided Stored Procedure Specifications]" 안에
    ///    <c>Filename: Feedback_Log.txt</c>로 놓여 프로시저 명세서인 것처럼 읽힌다.
    /// 3. 캐시 - 리뷰 프롬프트는 명세서를 불변 접두사로 두어 캐시를 노리는데(실측
    ///    481KB), 회차마다 내용이 바뀌는 피드백이 그 접두사에 섞이면 접두사 일치가
    ///    매 회차 깨져 캐시가 통째로 무효가 된다.
    ///
    /// 그래서 명세서에서 재료를 뽑거나 개수를 세거나 캐시 접두사를 만들 때는
    /// <see cref="OnlyProcedureSpecs"/>로 걸러내고, 피드백은 <see cref="OnlyFeedback"/>으로
    /// 따로 뽑아 제 이름을 단 자리에 싣는다.
    ///
    /// 이름을 여기 상수로 두는 이유: 넣는 쪽(VerificationPipelineOrchestrator)과
    /// 거르는 쪽(AiService)이 다른 파일이라, 문자열을 양쪽에 적어두면 한쪽만 바뀌어도
    /// 필터가 조용히 아무것도 거르지 않게 된다.
    /// </summary>
    public static class FeedbackSpec
    {
        /// <summary>L2 교차 리뷰가 낸 지적을 다음 회차로 나르는 항목.</summary>
        public const string CriticFileName = "Feedback_Log.txt";

        /// <summary>L3에서 사용자가 직접 적은 보완 요구를 나르는 항목.</summary>
        public const string UserFileName = "User_Feedback_Log.txt";

        /// <summary>L1 기계 검증이 낸 교정 지시를 나르는 항목.</summary>
        public const string L1FixFileName = "L1_Re_Fix.txt";

        /// <summary>프롬프트에서 피드백이 놓이는 자리의 머리글.</summary>
        public const string PromptHeader = "[Review Feedback — NOT a procedure specification]";

        public static bool IsFeedback(string? fileName) =>
            string.Equals(fileName, CriticFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, UserFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, L1FixFileName, StringComparison.OrdinalIgnoreCase);

        /// <summary>피드백을 뺀 진짜 명세서만. 개수를 세거나 재료를 뽑을 때 쓴다.</summary>
        public static List<(string FileName, string Content)> OnlyProcedureSpecs(
            IEnumerable<(string FileName, string Content)>? specs) =>
            specs?.Where(spec => !IsFeedback(spec.FileName)).ToList()
            ?? new List<(string FileName, string Content)>();

        /// <summary>피드백만. 프롬프트에서 제 이름을 단 자리에 실을 때 쓴다.</summary>
        public static List<(string FileName, string Content)> OnlyFeedback(
            IEnumerable<(string FileName, string Content)>? specs) =>
            specs?.Where(spec => IsFeedback(spec.FileName)).ToList()
            ?? new List<(string FileName, string Content)>();
    }
}
