using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 목차의 ErrorCodes를 명세서에서 추출한 코드로 채운다.
    ///
    /// 목차 마크다운을 받아 목차 마크다운을 돌려주는 이유: 파이프라인은 목차를
    /// 문자열로 들고 다니고 그 문자열 하나가 파일 기록·파싱·프롬프트의 단일
    /// 출처다. 파싱된 객체만 보강하면 PlanStructure.md에는 빈 배열이 남아,
    /// 나중에 파일을 여는 사람이 무엇을 검사했는지 알 수 없다.
    ///
    /// 실패는 예외가 아니라 원본 반환이다. 보강은 개선이지 필수 단계가 아니다 -
    /// 보강이 실패해도 하한 검사가 "검증 불가"로 그 사실을 기록한다.
    /// </summary>
    public static class PlanStructureEnricher
    {
        // BatchStepPlanParser와 같은 정규식이어야 한다. 두 곳이 다른 블록을
        // 고르면 파일에 기록된 목차와 실제로 쓰이는 목차가 갈라진다.
        private static readonly Regex JsonBlockRegex = new(
            @"```json\s*\r?\n(?<body>.*?)```",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            // 기본 인코더는 비ASCII를 \uXXXX로 이스케이프한다. 한글 단계명이
            // 그렇게 되면 PlanStructure.md를 사람이 읽을 수 없다.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Enrich(
            string? planStructureMarkdown,
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
        {
            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return planStructureMarkdown ?? string.Empty;
            }

            if (codesByProcedure == null || codesByProcedure.Count == 0)
            {
                // 명세서 추출이 통째로 0건이면 원본을 그대로 돌려주되 흔적을 남긴다.
                // 이게 없으면 "보강이 돌았는데 못 채운 것"과 "추출이 0건이라 시작조차
                // 안 된 것"을 운영자가 로그만 보고 구별할 수 없다.
                Log.Warning("명세서에서 추출한 오류코드가 없어 목차 보강을 건너뜁니다.");
                return planStructureMarkdown;
            }

            foreach (Match match in JsonBlockRegex.Matches(planStructureMarkdown))
            {
                var body = match.Groups["body"].Value;
                var rewritten = TryRewriteBlock(body, codesByProcedure);
                if (rewritten == null)
                {
                    // 파서와 같은 규칙: 유효하지 않은 블록은 건너뛰고 다음을 본다.
                    continue;
                }

                var bodyGroup = match.Groups["body"];
                return planStructureMarkdown[..bodyGroup.Index]
                    + rewritten
                    + planStructureMarkdown[(bodyGroup.Index + bodyGroup.Length)..];
            }

            Log.Warning("목차에서 보강할 단계 목록 JSON 블록을 찾지 못했습니다. 원본을 그대로 사용합니다.");
            return planStructureMarkdown;
        }

        /// <summary>
        /// 유효한 Steps 블록이면 보강된 JSON 문자열을, 아니면 null을 돌려준다.
        ///
        /// 본문 전체를 감싸는 이유: `JsonNode.Parse`가 통과시킨 입력이라도
        /// 프로퍼티 이름이 중복되면 이후 `TryGetPropertyValue`가 `JsonException`이
        /// 아닌 `ArgumentException`을 던진다("An item with the same key has
        /// already been added"). AI 산출 목차는 이 결함이 실재하는 산출물이라,
        /// try를 Parse 한 줄에만 두면 그 예외가 그대로 새 나가 Enrich를 호출한
        /// 파이프라인(재수립 헬퍼 포함)이 통째로 죽는다.
        /// </summary>
        private static string? TryRewriteBlock(
            string json, IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
        {
            try
            {
                var root = JsonNode.Parse(json);

                if (root is not JsonObject obj ||
                    !obj.TryGetPropertyValue("Steps", out var stepsNode) ||
                    stepsNode is not JsonArray steps)
                {
                    return null;
                }

                var enrichedCount = 0;
                foreach (var stepNode in steps)
                {
                    if (stepNode is not JsonObject step)
                    {
                        continue;
                    }

                    var merged = MergeCodes(step, codesByProcedure);
                    if (merged == null)
                    {
                        continue;
                    }

                    // ErrorCodes만 교체한다. 객체를 새로 만들면 Chunkable처럼 이미
                    // 있는 필드나 나중에 늘어날 필드가 조용히 사라진다.
                    step["ErrorCodes"] = new JsonArray(Array.ConvertAll(merged, c => (JsonNode?)JsonValue.Create(c)));
                    enrichedCount++;
                }

                if (enrichedCount > 0)
                {
                    Log.Information("목차의 오류코드를 명세서에서 보강했습니다 - 단계 수: {Count}개", enrichedCount);
                }

                // 파서가 다시 읽을 수 있는 형태여야 한다. 들여쓰기는 사람이 읽기 위한 것이다.
                return root.ToJsonString(WriteOptions) + "\n";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "목차 단계 목록 JSON 블록 보강 중 오류가 발생했습니다. 이 블록은 원본을 유지합니다.");
                return null;
            }
        }

        /// <summary>
        /// 이 단계의 최종 오류코드 목록. 바뀔 것이 없으면 null.
        ///
        /// 순서는 목차 선언분이 먼저, 그다음 명세서 등장 순서다. 같은 입력에
        /// 같은 출력이고 두 번 태워도 같다(멱등) - 목차는 재수립·구제 채택
        /// 경로에서 여러 번 오간다.
        /// </summary>
        private static string[]? MergeCodes(
            JsonObject step, IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
        {
            var declared = ReadStringArray(step, "ErrorCodes");
            var procedures = ReadStringArray(step, "LegacyProcedures");

            // 레거시 출신이 없으면 보존할 원본 코드가 애초에 없다. 비운 채 둔다.
            if (procedures.Count == 0)
            {
                return null;
            }

            var merged = new List<string>(declared);
            var seen = new HashSet<string>(declared, StringComparer.Ordinal);
            var changed = false;

            foreach (var procedure in procedures)
            {
                if (!codesByProcedure.TryGetValue(SpecReturnCodeExtractor.BareName(procedure), out var codes))
                {
                    continue;
                }

                foreach (var code in codes)
                {
                    if (seen.Add(code))
                    {
                        merged.Add(code);
                        changed = true;
                    }
                }
            }

            return changed ? merged.ToArray() : null;
        }

        private static List<string> ReadStringArray(JsonObject step, string name)
        {
            var values = new List<string>();
            if (!step.TryGetPropertyValue(name, out var node) || node is not JsonArray array)
            {
                return values;
            }

            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue(out string? text))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text);
                }
            }

            return values;
        }
    }
}
