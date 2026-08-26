using System;

namespace ReSet.Core.Services
{
    /// <summary>
    /// ValidateBatchStep의 오류 문자열을 검사 A~E로 귀속시킨다.
    ///
    /// [왜 문자열 대조인가] StepValidationResult에는 타입 있는 오류 목록이 없다
    /// (MechanicalValidator.cs:7327 - List&lt;string&gt; Errors 하나뿐). 검사별 발화량을
    /// 재려면 메시지를 읽는 수밖에 없다.
    ///
    /// [그래서 미분류를 따로 센다] 검사의 문구가 바뀌면 이 대조가 무너지는데,
    /// 모르는 메시지를 아무 칸에나 접어 넣으면 집계가 틀린 채로 그럴듯해진다.
    /// Unclassified로 남겨 보고서에 개수가 찍히게 하고, 0이 아니면 사람이 본다.
    /// </summary>
    public static class StepSweepClassifier
    {
        // 판별 조각은 각 검사의 메시지 조립부에서 그대로 따왔다. 위치는
        // MechanicalValidator.cs의 CheckStatementCountAgainstSpec(:6046) ·
        // CheckAnchoredStatementFacts(:6249) · CheckAnchoredStatementExtras(:6458) ·
        // CheckSpecLocalVariablesDeclared(:6546) · CheckStepIdInitialValue(:5909).
        private const string MarkerA = "개만 담고 있습니다. 명세서 DML 범위 표는";
        private const string MarkerB = "문장에 명세서가 확정한";
        private const string MarkerC = "문장이 명세서에 없는";
        private const string MarkerD = "을(를) 선언 없이 씁니다. 명세서 지역 변수 표는";
        private const string MarkerE = "로 초기화하고 CATCH에서 그 값을";

        public static SweepCheck Classify(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return SweepCheck.Unclassified;

            if (message.Contains(MarkerA, StringComparison.Ordinal)) return SweepCheck.A;
            if (message.Contains(MarkerB, StringComparison.Ordinal)) return SweepCheck.B;
            if (message.Contains(MarkerC, StringComparison.Ordinal)) return SweepCheck.C;
            if (message.Contains(MarkerD, StringComparison.Ordinal)) return SweepCheck.D;
            if (message.Contains(MarkerE, StringComparison.Ordinal)) return SweepCheck.E;

            return SweepCheck.Unclassified;
        }
    }
}
