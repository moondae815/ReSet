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
    /// </summary>
    public sealed record BatchStepPlan(
        string Code,
        string Name,
        IReadOnlyList<string> LegacyProcedures,
        IReadOnlyList<string> TargetTables,
        IReadOnlyList<string> ErrorCodes,
        bool Chunkable);

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

        public static IReadOnlyList<BatchStepPlan>? TryParse(string? planStructureMarkdown)
        {
            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return null;
            }

            foreach (Match match in JsonBlockRegex.Matches(planStructureMarkdown))
            {
                var parsed = TryParseBlock(match.Groups["body"].Value);
                if (parsed != null)
                {
                    Log.Information("목차에서 단계 목록을 읽었습니다 - 단계 수: {Count}개", parsed.Count);
                    return parsed;
                }
            }

            Log.Warning("목차에서 유효한 단계 목록 JSON을 찾지 못했습니다. 분할 생성을 건너뜁니다.");
            return null;
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
                            chunkable.ValueKind == JsonValueKind.True));
                }

                if (steps.Count == 0 || steps.Count > MaxSteps)
                {
                    Log.Warning("단계 목록 개수가 허용 범위를 벗어났습니다 - 개수: {Count}개, 상한: {Max}개",
                        steps.Count, MaxSteps);
                    return null;
                }

                return steps;
            }
            catch (JsonException)
            {
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
