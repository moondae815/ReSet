using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services;

/// <summary>
/// 검증 종료 상태를 문서 본문 앞에 붙일 배너로 렌더링한다.
/// 통과 상태에는 붙일 배너가 없으므로 해당 메서드를 두지 않는다.
/// </summary>
public static class VerificationBanner
{
    /// <summary>
    /// 배너 간 불릿 리스트 형식 계약을 한 곳에서 지킨다. 여러 배너가 동일한
    /// 불릿 형식(">   - {item}")을 사용하므로 중앙에서 관리한다.
    /// </summary>
    private static string RenderBulletList(IReadOnlyList<string> items, string emptyPlaceholder)
    {
        return items is { Count: > 0 }
            ? string.Join("\n", items.Select(item => $">   - {item}"))
            : $">   - {emptyPlaceholder}";
    }

    public static string L1Exhausted(IReadOnlyList<string> errors)
    {
        var errorLines = RenderBulletList(errors, "(상세 오류가 기록되지 않았습니다.)");

        return "\n> [!CAUTION]\n> **[검증 미완료] L1 기계 검증을 통과하지 못했습니다.**"
            + " 재시도를 모두 소진하여 마지막 작성 버전을 그대로 사용합니다.\n"
            + "> - **잔존 오류**:\n"
            + errorLines
            + "\n\n";
    }

    public static string QualityRejected(ReviewResult review, int scoreThreshold, RescueContext? rescue = null) =>
        $"\n> [!CAUTION]\n> **[품질 불합격] {RejectionReason(review, scoreThreshold)} (최종 신뢰도 점수: {review.NormalizedScore}/100)**\n"
        + RescueLine(rescue)
        + $"> - **평가 점수**: 정합성 {review.ScoreAccuracy}/10, CRUD {review.ScoreCrud}/10, 인터페이스 {review.ScoreInterface}/10, 가독성 {review.ScoreReadability}/10, 예외 {review.ScoreException}/10 (기준 점수: {scoreThreshold}/10)\n> - **최종 Critic 결함 피드백**:\n>   {review.FeedbackComment?.Replace("\n", "\n>   ")}\n\n";

    /// <summary>
    /// 불합격 사유를 실제 점수에서 계산한다.
    ///
    /// 이전에는 "정합성/가독성 기준 미달"이 하드코딩되어 있어, 그 두 항목이 만점이고
    /// 다른 항목만 미달인 문서에도 같은 문구가 붙었다. 헤더가 본문의 점수표와 어긋나면
    /// 읽는 사람이 어느 항목을 고쳐야 하는지 알 수 없다.
    ///
    /// 미달 판정 기준(점수 &lt; 기준점)은 VerificationPipelineOrchestrator가 재시도를
    /// 결정할 때 쓰는 조건과 같아야 한다. 한쪽만 바꾸면 "불합격인데 미달 항목 없음"이나
    /// 그 반대가 나온다.
    /// </summary>
    private static string RejectionReason(ReviewResult review, int scoreThreshold)
    {
        // 순서는 아래 점수표와 같게 유지한다. 헤더와 표를 눈으로 대조하기 때문이다.
        var failed = new List<string>();
        if (review.ScoreAccuracy < scoreThreshold) failed.Add("정합성");
        if (review.ScoreCrud < scoreThreshold) failed.Add("CRUD");
        if (review.ScoreInterface < scoreThreshold) failed.Add("인터페이스");
        if (review.ScoreReadability < scoreThreshold) failed.Add("가독성");
        if (review.ScoreException < scoreThreshold) failed.Add("예외");

        // 점수는 모두 기준을 넘겼는데 Critic이 결함을 지적한 경로가 있다.
        // 미달 항목이 없으므로 항목명을 지어내지 않는다.
        return failed.Count > 0
            ? $"{string.Join("/", failed)} 기준 미달"
            : "Critic 결함 지적";
    }

    /// <summary>
    /// 구제 시에만 붙는 첫 불릿. 뒤따르는 점수표가 어느 시도의 것인지 먼저 밝힌다.
    ///
    /// "다시 돌리면 나아진다" 같은 조언은 넣지 않는다. 사실만 적고 판단은 읽는
    /// 사람에게 맡긴다 — 3차가 쿼터로 죽은 경우와 정상 수행한 경우는 재실행 가치가
    /// 다른데, 그 판단에 필요한 사실이 바로 중단 사유다.
    /// </summary>
    private static string RescueLine(RescueContext? rescue)
    {
        if (rescue == null)
        {
            return string.Empty;
        }

        var cause = rescue.Reason switch
        {
            RetryAbortReason.GenerationFailed => "AI 생성 호출 실패",
            RetryAbortReason.L1Exhausted => "L1 기계 검증 실패",
            RetryAbortReason.ReviewFailed => "L2 리뷰 호출 실패",
            _ => "알 수 없는 사유"
        };

        return $"> - **채택 경위**: {rescue.AbortedAttempt}차 시도가 {cause}로 중단되어, "
            + $"검증을 마친 {rescue.AdoptedAttempt}차 시도를 채택했습니다.\n";
    }

    public static string ReviewNotRun(string reason) =>
        "> [!NOTE]\n> **L2 AI 교차 리뷰가 수행되지 않았습니다.** 리뷰 호출이 실패하여 정합성 검증 없이 확정된 문서입니다. 내용을 직접 검토하십시오.\n"
        + $"> - **실패 사유**: {reason}\n\n";

    /// <summary>
    /// 사용자 취소로 이 문서의 참조 객체 일부가 분석되지 않았음을 알린다.
    /// 개수 대신 이름을 싣는다 — 읽는 사람이 다음에 할 일이 그 객체를 다시
    /// 분석하는 것이기 때문이다.
    /// </summary>
    public static string UnresolvedReferences(IReadOnlyList<string> objectNames)
    {
        var nameLines = RenderBulletList(objectNames, "(미분석 객체명이 기록되지 않았습니다.)");

        return "\n> [!CAUTION]\n> **[참조 미완] 사용자 취소로 아래 참조 객체가 분석되지 않았습니다.**\n"
            + nameLines
            + "\n\n";
    }
}
