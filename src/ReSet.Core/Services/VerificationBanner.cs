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
    /// 하한 검사를 통과하지 못한 단계를 알린다.
    ///
    /// VerificationOutcome에 상태를 새로 만들지 않는다. L2를 통과한 문서의 종료
    /// 상태는 Passed가 맞고, 미달 사실은 이 배너가 나른다. 이것은 절대적 보장이
    /// 아니라 가시성 확보다 — 강제로 막으려면 골격+단계 전체 재생성을 유발해야
    /// 해서 비용이 맞지 않는다.
    ///
    /// 개수 대신 단계명을 싣는다. 읽는 사람이 다음에 할 일이 그 단계의 원본
    /// 프로시저를 직접 보는 것이기 때문이다.
    /// </summary>
    public static string StepFloorViolations(IReadOnlyList<string> steps)
    {
        var stepLines = RenderBulletList(steps, "(단계명이 기록되지 않았습니다.)");

        return "\n> [!WARNING]\n> **[하한 미달] 아래 단계 섹션이 최소 요건을 충족하지 못했습니다.**"
            + " 최소 요건은 SQL 또는 의사코드 블록 1개 이상, 선언된 대상 테이블 전부, 원본 오류코드 전부입니다."
            + " 해당 단계는 원본 프로시저를 직접 확인해야 합니다.\n"
            + stepLines
            + "\n\n";
    }

    /// <summary>
    /// 모든 시도가 빈 응답을 돌려줘 섹션 본문 자체가 없는 단계를 알린다.
    ///
    /// StepFloorViolations와 다른 사실을 나른다 - 저건 "섹션이 최소 요건을 못
    /// 채웠다"이고 이건 "채울 섹션이 아예 없다"이다. 하한 미달 배너에 섞으면
    /// "최소 요건을 충족하지 못했습니다"라는 문구가 거짓이 된다. 검증 불가를
    /// 하한 미달에서 갈라낸 것과 같은 이유다.
    ///
    /// 읽는 순서에서는 하한 미달보다 위에 온다. 본문이 없는 것이 부실한 것보다
    /// 심각하고, 읽는 사람이 먼저 손대야 할 곳이다.
    /// </summary>
    public static string GenerationFailedSteps(IReadOnlyList<string> steps)
    {
        var stepLines = RenderBulletList(steps, "(단계명이 기록되지 않았습니다.)");

        return "\n> [!WARNING]\n> **[생성 실패] 아래 단계는 본문이 생성되지 않았습니다.**"
            + " 재시도까지 모두 빈 응답이 돌아와 섹션에 담을 내용이 없으며,"
            + " 따라서 단계 하한 검사도 실행되지 못했습니다."
            + " 해당 단계는 원본 프로시저를 직접 확인해야 합니다.\n"
            + stepLines
            + "\n\n";
    }

    /// <summary>
    /// 계획서 자신이 코드 자리에 주석을 세워 둔 곳을 알린다.
    ///
    /// 재생성을 걸지 않는다 - 지시 주석과 생략 주석을 기계가 완벽히 가르지 못해
    /// 모델이 표현만 바꿔 우회할 위험이 크다. 사람이 판단하도록 사실만 남긴다.
    /// </summary>
    public static string OmissionComments(IReadOnlyList<string> comments)
    {
        var lines = RenderBulletList(comments, "(주석이 기록되지 않았습니다.)");

        return "\n> [!WARNING]\n> **[생략 주석] 계획서의 코드 블록에 구현 대신 주석이 서 있는 곳이 있습니다.**"
            + " 지시서 규칙 7은 코딩 에이전트에게 이 형태를 금지하는데, 계획서가 그것을 본보기로 보이고"
            + " 있습니다. 아래 자리는 에이전트가 그대로 복사할 수 있으니 구현 전에 사람이 확인하십시오.\n"
            + lines
            + "\n\n";
    }

    /// <summary>
    /// 하한 검사가 대조할 재료를 얻지 못한 단계를 알린다.
    ///
    /// StepFloorViolations와 다른 사실을 나른다 - 저건 "섹션이 부실하다"이고
    /// 이건 "섹션은 멀쩡할 수 있는데 검사를 돌리지 못했다"이다. 실측에서 14개
    /// 단계 중 13개가 후자였는데 전자의 문구로 나갔고, 그 결과 진입점의
    /// "모두 통과"와 배너가 정면으로 모순됐다.
    ///
    /// "원본 프로시저를 직접 확인하십시오" 같은 지시를 붙이지 않는다. 섹션이
    /// 부실하다는 근거가 없으므로 과잉이다.
    /// </summary>
    public static string UnverifiableSteps(IReadOnlyList<string> steps)
    {
        var stepLines = RenderBulletList(steps, "(단계명이 기록되지 않았습니다.)");

        return "\n> [!WARNING]\n> **[검증 불가] 아래 단계는 대조할 재료가 목차에 없어 검증되지 못했습니다.**"
            + " 섹션 내용이 부실하다는 뜻은 아닙니다 - 선언된 대상 테이블이나 원본 오류코드가 없어"
            + " 기계 대조를 실행할 수 없었다는 뜻입니다.\n"
            + stepLines
            + "\n\n";
    }

    /// <summary>
    /// 목차가 어느 단계에도 담지 못한 원본 프로시저를 알린다.
    ///
    /// StepFloorViolations와 다른 사실을 나른다 — 저건 "단계는 있는데 내용이
    /// 부실하다"이고 이건 "그 프로시저를 다룰 단계 자체가 목차에 없다"이다.
    /// 후자가 더 심각하다. 부실 단계는 최소한 존재를 알리기라도 하지만,
    /// 커버되지 않은 프로시저는 최종 문서 어디에도 흔적이 없다.
    ///
    /// VerificationOutcome에 상태를 새로 만들지 않고 목차 재수립도 유발하지
    /// 않는다 — StepFloorViolations와 같은 이유다: 이것은 절대적 보장이 아니라
    /// 가시성 확보이고, 재수립 예산은 다른 결함(점수 정체)을 위해 남겨둔다.
    ///
    /// 개수 대신 프로시저명을 싣는다. 읽는 사람이 다음에 할 일이 그 프로시저를
    /// 직접 확인하거나 목차를 손으로 보완하는 것이기 때문이다.
    /// </summary>
    public static string UncoveredProcedures(
        IReadOnlyList<string> procedureNames,
        int stepsWithoutLegacyProcedures = 0)
    {
        var nameLines = RenderBulletList(procedureNames, "(프로시저명이 기록되지 않았습니다.)");

        // 출신을 밝히지 않은 단계가 있으면 이 목록은 과다 보고일 수 있다. 그 단계가
        // 실제로는 그 프로시저를 다루고 있어도 검사는 알 방법이 없기 때문이다.
        // 단서 없이 내보내면 읽는 사람이 멀쩡한 프로시저를 다시 뒤지게 된다.
        var caveat = stepsWithoutLegacyProcedures > 0
            ? $" 다만 목차의 {stepsWithoutLegacyProcedures}개 단계가 원본 프로시저 표기를 비워 두었으므로,"
              + " 이 목록에는 실제로는 다뤄진 프로시저가 섞여 있을 수 있습니다."
            : string.Empty;

        return "\n> [!WARNING]\n> **[커버리지 누락] 목차가 아래 프로시저를 어느 단계에도 포함하지 않았습니다.**"
            + " 해당 프로시저는 최종 문서 어디에도 반영되지 않았을 가능성이 높습니다."
            + " 원본 프로시저를 직접 확인하거나 목차를 보완하십시오."
            + caveat
            + "\n"
            + nameLines
            + "\n\n";
    }

    /// <summary>
    /// 목차의 어느 단계도 원본 프로시저를 밝히지 않아 커버리지 대조 자체가
    /// 불가능했음을 알린다.
    ///
    /// UncoveredProcedures와 다른 사실을 나른다 - 저건 "이 프로시저를 다룰 단계가
    /// 없다"이고 이건 "무엇이 다뤄졌는지 판정할 재료가 없다"이다. 둘을 뭉개면
    /// 근거 0인 상태가 확정된 누락으로 보고된다. 실측(POQSettleProc6)에서 33단계가
    /// 전부 표기를 비운 채 나왔고, 본문은 12개 프로시저를 모두 다루고 있었는데도
    /// 12개 전부가 누락으로 보고됐다.
    ///
    /// 프로시저명을 싣지 않는다. 이름을 나열하면 그 자체가 누락 목록으로 읽히는데,
    /// 이 배너의 요지는 정확히 그 판정을 할 수 없었다는 것이다. 읽는 사람이 할 일은
    /// 개별 프로시저 확인이 아니라 목차를 고쳐 다시 돌리는 것이다.
    /// </summary>
    public static string CoverageUnverifiable(int totalSteps, int procedureCount)
    {
        return "\n> [!WARNING]\n> **[커버리지 검증 불가] 목차의 모든 단계"
            + $"({totalSteps}개)가 원본 프로시저 표기(`LegacyProcedures`)를 비워 두어"
            + " 커버리지 대조를 실행할 수 없었습니다.**"
            + $" 원본 명세서 {procedureCount}개가 어느 단계에 대응하는지 확인되지 않았다는 뜻이며,"
            + " 문서가 그 프로시저들을 다루지 않았다는 뜻은 아닙니다."
            + " 같은 이유로 단계별 하한 검사의 대상 테이블·오류코드 대조도 실행되지 못했습니다."
            + " 목차를 보완해 다시 실행하면 두 검사 모두 복구됩니다."
            + "\n\n";
    }

    /// <summary>
    /// 목차가 유효한 단계 목록을 내지 못해 분할 생성이 실행되지 않았음을 알린다.
    ///
    /// 다른 배너들과 달리 이것은 "무엇이 잘못됐다"가 아니라 "무엇을 검사하지
    /// 않았다"를 나른다. 그래서 더 중요하다 - 실측(POQSettleProc7)에서 이 경로를
    /// 탄 문서가 배너 하나 없이 92점으로 끝났고, 분할된 문서(88점)보다 높았다.
    /// 짧고 깔끔한 문서가 읽기 좋았기 때문이다. 점수는 누락을 볼 수 없다.
    ///
    /// 사유(JSON 블록 없음, 0단계, 상한 초과, 파싱 실패)는 구분하지 않는다.
    /// 운영상 결과가 같고, 사유는 이미 경고 로그에 남는다.
    /// </summary>
    public static string SplitGenerationSkipped()
    {
        return "\n> [!WARNING]\n> **[분할 미실행] 목차가 유효한 단계 목록을 내지 못해"
            + " 문서가 단일 호출로 생성되었습니다.**"
            + " 단계별 섹션 생성과 단계별 하한 검사(대상 테이블·오류코드 대조)가"
            + " 실행되지 않았습니다. 내용이 부실하다는 뜻은 아니지만, 이 문서는"
            + " 단계 단위 기계 검증을 받지 않았습니다."
            + "\n\n";
    }

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

    /// <summary>
    /// 원본 명세서의 오류코드 중 최종 문서 어디에도 없는 것을 알린다.
    ///
    /// 레거시 반환 코드를 그대로 계승하는 것은 이 문서의 핵심 계약이다. 실측
    /// (POQSettleProc7)에서 그 계약이 20군데 깨졌는데 아무 신호도 나가지 않았다 -
    /// 오류코드 대조가 단계별 경로에만 붙어 있었고 그 경로가 통째로 건너뛰어졌기 때문이다.
    ///
    /// 분모를 함께 싣는다. "9개 누락"만으로는 읽는 사람이 심각도를 가늠할 수 없다.
    /// </summary>
    public static string MissingErrorCodes(
        IReadOnlyDictionary<string, IReadOnlyList<string>> missingByProcedure,
        IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
    {
        var lines = new List<string>();
        foreach (var (procedure, missing) in missingByProcedure)
        {
            var total = codesByProcedure != null
                        && codesByProcedure.TryGetValue(procedure, out var all)
                ? all.Count
                : missing.Count;

            // procedure는 SpecReturnCodeExtractor.BareName이 낮춘 소문자 키다. 그대로
            // 찍으면 같은 문서의 UncoveredProcedures(예: "dbo.UP_UTIL_SETTLE_INS")와
            // 표기가 어긋난다. 스키마 접두사까지는 복원할 수 없지만, 대문자로는
            // 맞춰 원본 프로시저명 표기 관례를 따른다.
            lines.Add($"{procedure.ToUpperInvariant()}: {total}개 중 {missing.Count}개 누락 — {string.Join(", ", missing)}");
        }

        var body = RenderBulletList(lines, "(누락 내역이 기록되지 않았습니다.)");

        return "\n> [!WARNING]\n> **[오류코드 누락] 원본 명세서의 반환 코드가 최종 문서에서"
            + " 확인되지 않았습니다.**"
            + " 레거시 반환 코드의 보존은 이 문서의 핵심 계약이므로, 아래 항목은 문서를"
            + " 넘기기 전에 직접 확인하십시오.\n"
            + body
            + "\n\n";
    }
}
