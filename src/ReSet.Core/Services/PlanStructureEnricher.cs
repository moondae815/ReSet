using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            // 기본 인코더는 비ASCII를 \uXXXX로 이스케이프한다. 한글 단계명이
            // 그렇게 되면 PlanStructure.md를 사람이 읽을 수 없다.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// 보강 결과. 마크다운과, 검사에서 제외된 목차 선언의 보고를 함께 낸다.
        ///
        /// 버린 선언을 반환값에 싣는 이유: 그것을 계산하는 곳은 여기 하나여야 한다.
        /// 오케스트레이터가 따로 비교하면 두 권위가 생기고, 이 저장소는 그 어긋남을
        /// 이미 여러 번 겪었다.
        /// </summary>
        public sealed record PlanStructureEnrichment(
            string Markdown,
            IReadOnlyList<string> DroppedTableDeclarations);

        public static PlanStructureEnrichment Enrich(
            string? planStructureMarkdown,
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure)
        {
            var empty = Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return new PlanStructureEnrichment(planStructureMarkdown ?? string.Empty, empty);
            }

            // "재료가 없다"는 사실만으로 보강 자체를 건너뛸 수 없다 - 레거시 출신이
            // 없는 단계는 codesByProcedure·tablesByProcedure가 통째로 비어도 예약
            // 대역에서 코드를 발급받는다(MergeCodes). 그 발급은 이 재료에 기대지
            // 않는다. 그래서 이 두 변수는 "정말 아무것도 못 채웠는가"를 뒤에서
            // 판단하는 재료로만 쓰고, 여기서 곧장 반환하지 않는다.
            var hasCodes = codesByProcedure != null && codesByProcedure.Count > 0;
            var hasTables = tablesByProcedure != null && tablesByProcedure.Count > 0;

            // 블록 선택은 파서가 소유한다. 여기서 따로 고르면 파일에 기록된 목차와
            // 파이프라인이 실제로 쓰는 목차가 갈라진다.
            var located = BatchStepPlanParser.TryLocateStepsBlock(planStructureMarkdown);
            if (located == null)
            {
                Log.Warning("목차에서 보강할 단계 목록 JSON 블록을 찾지 못했습니다. 원본을 그대로 사용합니다.");
                return new PlanStructureEnrichment(planStructureMarkdown, empty);
            }

            var dropped = new List<string>();
            var rewritten = TryRewriteBlock(
                located.Value.Body,
                codesByProcedure ?? new Dictionary<string, IReadOnlyList<string>>(),
                tablesByProcedure ?? new Dictionary<string, SpecTargetTableExtractor.StepTableSets>(),
                dropped,
                out var anyStepChanged);

            if (rewritten == null)
            {
                // 뒤 블록으로 넘어가지 않는다. 파서가 읽는 블록을 보강하지 못했다면 보강을
                // 포기하는 것이 맞다 - 다른 블록을 고치면 두 목차가 갈라지고, 그 불일치는
                // 어디에도 드러나지 않는다. 보강되지 않은 단계는 하한 검사가 "검증 불가"로
                // 보고하므로 침묵하지도 않는다.
                return new PlanStructureEnrichment(planStructureMarkdown, empty);
            }

            if (!hasCodes && !hasTables && !anyStepChanged)
            {
                // 명세서·정적 분석 재료도 없고 예약 대역 발급도 없었다 - 정말 아무것도
                // 못 채운 것이다. 운영자가 "보강이 돌았는데 못 채운 것"과 "추출이 0건이라
                // 시작조차 안 된 것"을 로그만 보고 구별할 수 있어야 한다.
                Log.Warning("명세서와 정적 분석에서 추출한 보강 재료가 없어 목차 보강을 건너뜁니다.");
                return new PlanStructureEnrichment(planStructureMarkdown, empty);
            }

            var markdown = planStructureMarkdown[..located.Value.BodyIndex]
                + rewritten
                + planStructureMarkdown[(located.Value.BodyIndex + located.Value.BodyLength)..];

            return new PlanStructureEnrichment(markdown, dropped);
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
            string json,
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure,
            List<string> dropped,
            out bool anyStepChanged)
        {
            anyStepChanged = false;
            try
            {
                var root = JsonNode.Parse(json);

                if (root is not JsonObject obj ||
                    !obj.TryGetPropertyValue("Steps", out var stepsNode) ||
                    stepsNode is not JsonArray steps)
                {
                    return null;
                }

                // [최종 리뷰 픽스] MergeCodes는 서로 다른 두 기전을 섞어서 낸다 -
                // 명세서에서 실제로 코드를 물려받은 경우(procedures.Count > 0)와,
                // 레거시 출신이 없어 예약 대역에서 새 코드를 발급한 경우(procedures.Count
                // == 0)다. 예전에는 하나의 카운터(enrichedCodeCount)로 둘을 합쳐 세고
                // "명세서에서 보강했습니다"라는 문구 하나로 냈다 - 명세서에서 온 것이
                // 0건이어도(전부 예약 발급이어도) 그 문장이 나와 거짓 진술이 됐다.
                var enrichedFromSpecCount = 0;
                var issuedReservedCount = 0;
                var enrichedTableCount = 0;
                foreach (var stepNode in steps)
                {
                    if (stepNode is not JsonObject step)
                    {
                        continue;
                    }

                    var merged = MergeCodes(step, codesByProcedure, out var isReservedIssuance);
                    if (merged != null)
                    {
                        step["ErrorCodes"] = new JsonArray(
                            Array.ConvertAll(merged, c => (JsonNode?)JsonValue.Create(c)));
                        if (isReservedIssuance)
                        {
                            issuedReservedCount++;
                        }
                        else
                        {
                            enrichedFromSpecCount++;
                        }
                    }

                    if (RewriteTables(step, tablesByProcedure, dropped))
                    {
                        enrichedTableCount++;
                    }
                }

                if (enrichedFromSpecCount > 0)
                {
                    Log.Information("목차의 오류코드를 명세서에서 보강했습니다 - 단계 수: {Count}개", enrichedFromSpecCount);
                }

                if (issuedReservedCount > 0)
                {
                    Log.Information("목차의 오류코드에 제어 단계 예약 대역 코드를 발급했습니다 - 단계 수: {Count}개", issuedReservedCount);
                }

                if (enrichedTableCount > 0)
                {
                    Log.Information("목차의 대상 테이블을 정적 분석에서 보강했습니다 - 단계 수: {Count}개", enrichedTableCount);
                }

                anyStepChanged = enrichedFromSpecCount > 0 || issuedReservedCount > 0 || enrichedTableCount > 0;

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
            JsonObject step,
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure,
            out bool isReservedIssuance)
        {
            isReservedIssuance = false;
            var declared = ReadStringArray(step, "ErrorCodes");
            var procedures = ReadStringArray(step, "LegacyProcedures");

            // 레거시 출신이 없으면 보존할 원본 코드가 없다. 그렇다고 비워 두면 모델이
            // 자기 체계를 지어낸다 - 실측(POQSettleBatch1)에서 목차가 B100·B110·B160
            // 같은 코드를 발급했고 계획서에 54회 등장했으며, 그중 하나가
            // `DECLARE @v_currentStepId INT = B161`로 새어 컴파일되지 않는 SQL이 됐다.
            // 예약 대역에서 결정적으로 발급한다.
            if (procedures.Count == 0)
            {
                isReservedIssuance = true;
                var blockStart = ControlStepErrorCodes.BlockStart(ReadScalarString(step, "Code"));
                if (blockStart == null)
                {
                    return null;
                }

                // 레거시 코드가 예약 대역에 걸리면 발급하지 않는다. 조용히 겹치는 것이
                // 지어낸 어휘보다 나쁘다 - 원본 코드가 제어 코드로 오인된다.
                if (AnyLegacyCodeIsReserved(codesByProcedure))
                {
                    Log.Warning(
                        "레거시 오류코드가 제어 단계 예약 대역과 겹쳐 예약 코드를 발급하지 않습니다 - 단계: {Code}",
                        ReadScalarString(step, "Code"));
                    return null;
                }

                var issued = blockStart.Value.ToString(CultureInfo.InvariantCulture);

                // 합집합이 아니라 교체다. 모델이 지어낸 코드를 남기면 등장 검사가 계속
                // 그것을 인증한다.
                return declared.Count == 1 && declared[0] == issued
                    ? null
                    : new[] { issued };
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

        /// <summary>
        /// 이 단계의 TargetTables를 정적 분석의 쓰기 대상으로 교체하고 SchemaTables를 채운다.
        /// 바뀐 것이 있으면 true.
        ///
        /// 오류코드와 달리 합집합하지 않는 이유: 두 재료의 신뢰도가 대칭이 아니다.
        /// 오류코드는 명세서 산문에서 뽑고 모델도 같은 산문을 보지만, 테이블은 파서가
        /// AST에서 확정하고 모델은 추측한다. 실측에서 한 단계가 선언한 네 테이블 중
        /// 셋이 원본 DDL에 0회 등장했다 - 합집합했다면 그 허위가 검증 요건이 되고,
        /// 재생성이 그것을 고착시켰을 것이다.
        ///
        /// SchemaTables는 이 메서드가 유일하게 채우는 곳이다(BatchStepPlan.cs의 불변식).
        /// 도구가 채울 재료가 없는 두 경로 - LegacyProcedures가 비어 대조할 원본이
        /// 없는 경우, 그리고 있어도 정적 분석 매칭이 하나도 안 남는 경우 - 에서는
        /// 기존 SchemaTables를 지운다. 재수립 프롬프트가 이전 목차를 그대로 예시로
        /// 붙여넣으므로, 지우지 않으면 모델이 흉내 낸 값이 살아남아 도구가 낸 것으로
        /// 오인된다.
        /// </summary>
        private static bool RewriteTables(
            JsonObject step,
            IReadOnlyDictionary<string, SpecTargetTableExtractor.StepTableSets> tablesByProcedure,
            List<string> dropped)
        {
            var procedures = ReadStringArray(step, "LegacyProcedures");
            if (procedures.Count == 0)
            {
                // 레거시 출신이 없는 단계는 계획이 새로 설계한 것이다. 대조할 원본이
                // 없으니 SchemaTables도 채울 수 없다 - 모델이 흉내 낸 값이 있다면 지운다.
                return step.Remove("SchemaTables");
            }

            var write = new List<string>();
            var writeSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var schema = new List<string>();
            var schemaSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var procedure in procedures)
            {
                if (!tablesByProcedure.TryGetValue(SpecReturnCodeExtractor.BareName(procedure), out var sets))
                {
                    continue;
                }

                foreach (var table in sets.WriteTables)
                {
                    if (writeSeen.Add(table)) write.Add(table);
                    if (schemaSeen.Add(table)) schema.Add(table);
                }

                foreach (var table in sets.ReadTables)
                {
                    if (schemaSeen.Add(table)) schema.Add(table);
                }
            }

            var changed = false;

            // 쓰기 대상을 하나도 못 뽑았으면 기존 선언을 유지한다. 지우면 멀쩡한
            // 단계가 "검증 불가"로 떨어져 지금보다 나빠진다.
            if (write.Count > 0)
            {
                var declared = ReadStringArray(step, "TargetTables");
                var extractedBareNames = new HashSet<string>(
                    write.ConvertAll(SpecTargetTableExtractor.BareTableName), StringComparer.Ordinal);

                var lost = declared.FindAll(
                    d => !extractedBareNames.Contains(SpecTargetTableExtractor.BareTableName(d)));

                if (lost.Count > 0)
                {
                    // "정적 분석에 없어 하한 검사와 스키마 범위에서 모두 제외했다"는
                    // 하나의 문장으로 뭉뚱그리면 결함 3(S09) 같은 읽기 전용 사례에서
                    // 거짓이 된다. 그 이름은 write에는 없어도(하한 검사 탈락) read에는
                    // 있어 schema(write+read)에는 남는다(스키마 범위 잔류). 두 집합을
                    // 나눠 각자에게 맞는 결과만 보고한다.
                    var schemaBareNames = new HashSet<string>(
                        schema.ConvertAll(SpecTargetTableExtractor.BareTableName), StringComparer.Ordinal);

                    var absent = lost.FindAll(
                        d => !schemaBareNames.Contains(SpecTargetTableExtractor.BareTableName(d)));
                    var readOnly = lost.FindAll(
                        d => schemaBareNames.Contains(SpecTargetTableExtractor.BareTableName(d)));

                    var code = ReadScalarString(step, "Code");
                    var sentence = new System.Text.StringBuilder($"{code}: 목차가 선언한 대상 테이블 중 ");
                    if (absent.Count > 0)
                    {
                        sentence.Append(string.Join(", ", absent));
                        sentence.Append("은(는) 정적 분석에 전혀 없어 하한 검사와 이 단계의 스키마 범위에서 모두 제외했습니다. ");
                    }
                    if (readOnly.Count > 0)
                    {
                        sentence.Append(string.Join(", ", readOnly));
                        sentence.Append("은(는) 정적 분석에서 읽기 전용으로만 쓰여 하한 검사에서는 제외했으나 이 단계의 스키마 범위에는 남아 있습니다. ");
                    }
                    sentence.Append("계획서 본문도 함께 확인하십시오.");
                    dropped.Add(sentence.ToString());
                }

                step["TargetTables"] = new JsonArray(
                    write.ConvertAll(t => (JsonNode?)JsonValue.Create(t)).ToArray());
                changed = true;
            }

            if (schema.Count > 0)
            {
                step["SchemaTables"] = new JsonArray(
                    schema.ConvertAll(t => (JsonNode?)JsonValue.Create(t)).ToArray());
                changed = true;
            }
            else if (step.Remove("SchemaTables"))
            {
                // 정적 분석 매칭이 하나도 안 남아 이번 회차에 도구가 채울 값이 없다.
                // 기존 값이 모델이 흉내 낸 것일 수 있으므로 지운다.
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// 명세서에서 뽑은 레거시 코드 중 예약 대역에 걸리는 것이 있는가.
        /// 있으면 제어 단계 발급을 포기한다 - 겹친 코드는 어느 쪽 뜻인지 알 수 없다.
        /// </summary>
        private static bool AnyLegacyCodeIsReserved(
            IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure)
        {
            foreach (var codes in codesByProcedure.Values)
            {
                foreach (var code in codes)
                {
                    if (int.TryParse(code, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                        && ControlStepErrorCodes.IsReserved(value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string ReadScalarString(JsonObject step, string name) =>
            step.TryGetPropertyValue(name, out var node) &&
            node is JsonValue value &&
            value.TryGetValue(out string? text)
                ? text ?? string.Empty
                : string.Empty;

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
