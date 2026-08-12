using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 목차(PlanStructure)가 선언하는 통합 배치 단계 하나.
    ///
    /// 이 레코드가 존재하는 이유: 목차의 헤딩을 파싱해서는 단계 목록을 얻을 수 없다.
    /// 실측한 두 산출물이 이미 반증한다 — 한쪽은 단계를 H3(`### P00.`)에, 다른 쪽은
    /// H4(`#### S00.`)에 뒀고, 후자는 단계가 아닌 헤딩(`#### Phase 1.`)을 같은 레벨에
    /// 섞었다. 결정적으로 전자는 `### P20~P23.`으로 4개 단계를 헤딩 하나에 묶었다.
    ///
    /// 세 가지로 쓰인다: 분할 생성의 단위, 하한 검사의 기준(TargetTables/ErrorCodes),
    /// L2가 결함을 지목할 때의 좌표(Code).
    ///
    /// TargetTables와 SchemaTables를 나눠 두는 이유: 앞은 "본문이 이 테이블을
    /// 기술했는가"를 묻는 검증 재료이고, 뒤는 "이 회차 에이전트가 어떤 스키마를
    /// 봐야 하는가"를 정하는 스코프 재료다. 한 필드로 겸하면 읽기 원본을 넣을 때
    /// 검증이 과해지고, 빼면 에이전트가 SELECT를 쓸 스키마를 못 받는다.
    /// SchemaTables는 모델이 내지 않는다 - 도구가 정적 분석에서 채운다.
    /// </summary>
    public sealed record BatchStepPlan(
        string Code,
        string Name,
        IReadOnlyList<string> LegacyProcedures,
        IReadOnlyList<string> TargetTables,
        IReadOnlyList<string> ErrorCodes,
        bool Chunkable,
        IReadOnlyList<string> SchemaTables);

    /// <summary>
    /// `raw/PlanStructure.md` 안의 ```json 블록에서 단계 목록을 읽는다.
    ///
    /// 별도 파일로 빼지 않는 이유: PlanStructure.md가 산출물을 실제로 만든 목차를
    /// 담아야 한다는 계약이 이미 있고, 파일이 둘이면 재수립·구제 채택 시 두 파일의
    /// 원자성을 따로 보장해야 한다. 한 파일 안에 있으면 목차를 되돌리는 것만으로
    /// 단계 목록도 함께 되돌아간다.
    ///
    /// 실패는 예외가 아니라 null이다. 분할은 개선이지 필수 단계가 아니므로,
    /// 파싱하지 못하면 호출부가 현행 단일 호출 경로로 폴백한다.
    /// </summary>
    public static class BatchStepPlanParser
    {
        /// <summary>단계 수 상한. 목차가 폭주했을 때 호출을 무제한 늘리지 않기 위한 방어선이다.</summary>
        public const int MaxSteps = 40;

        // 닫는 펜스까지를 통째로 잡는다. 비탐욕 `\{.*?\}`로 잡으면 중첩 객체의
        // 첫 번째 `}`에서 끊겨 항상 파싱에 실패한다.
        private static readonly Regex JsonBlockRegex = new(
            @"```json\s*\r?\n(?<body>.*?)```",
            RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// 목차에서 유효한 단계 목록 블록의 위치와 파싱 결과.
        /// </summary>
        /// <param name="BodyIndex">원본 마크다운에서 ```json 본문이 시작하는 문자 인덱스.</param>
        /// <param name="BodyLength">본문의 길이. 이 구간만 갈아 끼우면 펜스는 보존된다.</param>
        /// <param name="Body">본문 원문.</param>
        /// <param name="Steps">그 본문을 파싱한 결과. 비어 있지 않다.</param>
        public readonly record struct StepsBlockLocation(
            int BodyIndex,
            int BodyLength,
            string Body,
            IReadOnlyList<BatchStepPlan> Steps);

        /// <summary>
        /// 파서와 보강기가 <b>같은</b> 블록을 고르게 하는 단일 진입점.
        ///
        /// 두 곳이 각자 블록을 고르면 PlanStructure.md에 기록된 목차와 파이프라인이 실제로
        /// 쓰는 목차가 갈라진다. 그 불일치는 어디에도 드러나지 않는다 - 파일을 여는 사람은
        /// 자기가 보는 것이 쓰인 것이라고 믿는다.
        ///
        /// 유효성 판정은 TryParseBlock 하나다. 그것이 버리는 블록은 이 선택기도 버린다.
        /// </summary>
        public static StepsBlockLocation? TryLocateStepsBlock(string? planStructureMarkdown)
        {
            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return null;
            }

            foreach (Match match in JsonBlockRegex.Matches(planStructureMarkdown))
            {
                var body = match.Groups["body"];
                var parsed = TryParseBlock(body.Value);
                if (parsed != null)
                {
                    return new StepsBlockLocation(body.Index, body.Length, body.Value, parsed);
                }
            }

            return null;
        }

        public static IReadOnlyList<BatchStepPlan>? TryParse(string? planStructureMarkdown)
        {
            // 빈 입력은 "목차가 아직 없다"는 뜻이라 경고할 일이 아니다. 종전 동작 그대로다.
            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return null;
            }

            var located = TryLocateStepsBlock(planStructureMarkdown);
            if (located == null)
            {
                Log.Warning("목차에서 유효한 단계 목록 JSON을 찾지 못했습니다. 분할 생성을 건너뜁니다.");
                return null;
            }

            Log.Information("목차에서 단계 목록을 읽었습니다 - 단계 수: {Count}개", located.Value.Steps.Count);
            return located.Value.Steps;
        }

        private static IReadOnlyList<BatchStepPlan>? TryParseBlock(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("Steps", out var stepsProperty) ||
                    stepsProperty.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var steps = new List<BatchStepPlan>();
                foreach (var element in stepsProperty.EnumerateArray())
                {
                    var code = ReadString(element, "Code");
                    var name = ReadString(element, "Name");

                    // Code나 Name이 없으면 그 단계를 특정할 수도, 헤딩을 검사할 수도 없다.
                    // 일부만 성한 목록을 쓰면 어느 단계가 누락됐는지 아무도 모른다.
                    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                    {
                        Log.Warning("단계 목록에 Code 또는 Name이 없는 항목이 있어 전체를 버립니다.");
                        return null;
                    }

                    steps.Add(new BatchStepPlan(
                        code.Trim(),
                        name.Trim(),
                        ReadStringArray(element, "LegacyProcedures"),
                        ReadStringArray(element, "TargetTables"),
                        ReadStringArray(element, "ErrorCodes"),
                        element.TryGetProperty("Chunkable", out var chunkable) &&
                            chunkable.ValueKind == JsonValueKind.True,
                        ReadStringArray(element, "SchemaTables")));
                }

                if (steps.Count == 0 || steps.Count > MaxSteps)
                {
                    Log.Warning("단계 목록 개수가 허용 범위를 벗어났습니다 - 개수: {Count}개, 상한: {Max}개",
                        steps.Count, MaxSteps);
                    return null;
                }

                return steps;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // JsonException만 잡으면 부족하다. Steps 배열 원소가 객체가 아니면
                // TryGetProperty가 InvalidOperationException을 던지는데, 이는
                // JsonException이 아니다. 이 구멍을 열어두면 여기서는 null을 돌려주는
                // 대신 예외가 TryLocateStepsBlock을 거쳐 TryParse와 Enrich 밖으로
                // 그대로 새 나간다 - 둘 다 "실패는 예외가 아니다"라는 계약을 진다.
                Log.Warning(ex, "단계 목록 JSON 블록 파싱 중 예상치 못한 오류가 발생했습니다. 이 블록은 버립니다.");
                return null;
            }
        }

        private static string ReadString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;

        private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text.Trim());
                }
            }

            return values;
        }
    }
}
