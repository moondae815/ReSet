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

    public static string QualityRejected(ReviewResult review, int scoreThreshold) =>
        $"\n> [!CAUTION]\n> **[품질 불합격] 정합성/가독성 기준 미달 (최종 신뢰도 점수: {review.NormalizedScore}/100)**\n> - **평가 점수**: 정합성 {review.ScoreAccuracy}/10, CRUD {review.ScoreCrud}/10, 인터페이스 {review.ScoreInterface}/10, 가독성 {review.ScoreReadability}/10, 예외 {review.ScoreException}/10 (기준 점수: {scoreThreshold}/10)\n> - **최종 Critic 결함 피드백**:\n>   {review.FeedbackComment?.Replace("\n", "\n>   ")}\n\n";

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
