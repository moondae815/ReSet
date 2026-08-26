using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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

        // "S07 섹션의 UPDATE 13(갱신 13) 문장에" / "S07 섹션의 INSERT 2 문장에" 양쪽을 잡는다.
        // 여는 괄호는 UPDATE에만 붙으므로(MechanicalValidator의 gloss 참고) 경계로 쓸 수 없다 -
        // 서수 뒤의 공백이나 괄호 어느 쪽이든 받는다.
        private static readonly Regex CoordinatePattern = new(
            @"섹션의\s+(?<kind>[A-Z]+)\s+(?<ordinal>\d+)(?=\s|\()",
            RegexOptions.Compiled);

        // 검사 B: "확정한 <라벨> A, B이(가) 없습니다".
        // 라벨을 `.*?`로 넘기면 게으른 수량자가 라벨의 일부를 items에 남긴다
        // ("컬럼 YMD, PGNAME"). 라벨은 두 개뿐이므로(MechanicalValidator.cs:6334·6345)
        // 그대로 못 박는다 - 라벨이 늘면 여기도 늘려야 하고, 그때 태스크 1의
        // 판별 조각 테스트와 함께 갱신한다.
        private static readonly Regex MissingItemsPattern = new(
            @"확정한\s+(?:최상위\s+WHERE\s+술어\s+컬럼|조인\s+키)\s+(?<items>.*?)이\(가\)\s*없습니다",
            RegexOptions.Compiled);

        // 검사 C: "없는 술어 컬럼 A, B을(를) 씁니다"
        private static readonly Regex ExtraItemsPattern = new(
            @"없는\s+술어\s+컬럼\s+(?<items>.*?)을\(를\)\s*씁니다",
            RegexOptions.Compiled);

        /// <summary>
        /// 발화 하나를 판정표의 한 행으로 만든다.
        ///
        /// [왜 좌표를 메시지에서 뽑는가] StepValidationResult가 구조화된 값을 내지 않으므로
        /// 메시지가 유일한 출처다. 뽑히지 않아도 발화는 센다 - 집계까지 잃으면 검사가
        /// 침묵한 것과 구분되지 않는다.
        /// </summary>
        public static SweepFinding Describe(
            string jobName, string stepCode, SweepCheck check,
            SweepCondition condition, string message)
        {
            var finding = new SweepFinding(jobName, stepCode, check, condition, message);
            if (check != SweepCheck.B && check != SweepCheck.C) return finding;

            var coordinate = CoordinatePattern.Match(message);
            if (coordinate.Success)
            {
                finding = finding with
                {
                    Kind = coordinate.Groups["kind"].Value,
                    Ordinal = int.Parse(coordinate.Groups["ordinal"].Value),
                };
            }

            var items = check == SweepCheck.B
                ? MissingItemsPattern.Match(message)
                : ExtraItemsPattern.Match(message);

            if (items.Success)
            {
                finding = finding with { Items = SplitItems(items.Groups["items"].Value) };
            }

            return finding;
        }

        private static IReadOnlyList<string> SplitItems(string raw) =>
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
