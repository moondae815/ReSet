using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReSet.Validator.Core.Services
{
    public class DataComparisonService
    {
        public string CompareOutputs(string legacyJson, string newJson)
        {
            var sb = new StringBuilder();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            ExecutionOutput? legacy = null;
            ExecutionOutput? target = null;
            
            try
            {
                legacy = JsonSerializer.Deserialize<ExecutionOutput>(legacyJson, options);
                target = JsonSerializer.Deserialize<ExecutionOutput>(newJson, options);
            }
            catch (JsonException)
            {
                // Fallthrough to null check below
            }

            if (legacy == null || target == null)
            {
                return "# ❌ 데이터 비교 실패\n\n결과 파일 중 하나를 파싱할 수 없거나 형식이 올바르지 않습니다.";
            }

            sb.AppendLine($"# 📊 데이터 정합성 비교 검증 보고서 - {legacy.ProcedureName}");
            sb.AppendLine($"- **검증 일시**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            sb.AppendLine("## 📈 종합 비교 요약");
            
            var totalCases = legacy.ExecutionResults.Count;
            var matchedCases = 0;
            var detailsList = new List<string>();

            var targetDict = target.ExecutionResults.ToDictionary(r => r.CaseId, r => r);

            foreach (var legResult in legacy.ExecutionResults)
            {
                var caseId = legResult.CaseId;
                if (!targetDict.TryGetValue(caseId, out var tarResult))
                {
                    detailsList.Add($"### ❌ `{caseId}` - 신규 결과에 테스트 케이스가 누락되었습니다.");
                    continue;
                }

                var casePassed = true;
                var caseDetails = new StringBuilder();
                caseDetails.AppendLine($"### 🔍 테스트 케이스: `{caseId}`");

                // 1. 상태 및 에러코드 비교
                if (legResult.Status != tarResult.Status)
                {
                    casePassed = false;
                    caseDetails.AppendLine($"- **상태 불일치**: 레거시 `{legResult.Status}` (에러: {legResult.ErrorCode}) vs 신규 `{tarResult.Status}` (에러: {tarResult.ErrorCode})");
                }

                // 2. Result Set 개수 비교
                if (legResult.ResultSets.Count != tarResult.ResultSets.Count)
                {
                    casePassed = false;
                    caseDetails.AppendLine($"- **결과셋 개수 불일치**: 레거시 `{legResult.ResultSets.Count}` 개 vs 신규 `{tarResult.ResultSets.Count}` 개");
                }
                else
                {
                    // 각 Result Set 비교
                    for (int rIdx = 0; rIdx < legResult.ResultSets.Count; rIdx++)
                    {
                        var legSet = legResult.ResultSets[rIdx];
                        var tarSet = tarResult.ResultSets[rIdx];

                        if (legSet.Count != tarSet.Count)
                        {
                            casePassed = false;
                            caseDetails.AppendLine($"- **결과셋 #{rIdx + 1} 행 수(Row Count) 불일치**: 레거시 `{legSet.Count}` 행 vs 신규 `{tarSet.Count}` 행");
                            continue;
                        }

                        // 행 단위 데이터 상세 비교
                        for (int rowIdx = 0; rowIdx < legSet.Count; rowIdx++)
                        {
                            var legRow = legSet[rowIdx];
                            var tarRow = tarSet[rowIdx];

                            // 키(컬럼) 비교
                            var allKeys = legRow.Keys.Union(tarRow.Keys).Distinct();
                            var rowMismatches = new List<string>();

                            foreach (var key in allKeys)
                            {
                                var hasLeg = legRow.TryGetValue(key, out var legVal);
                                var hasTar = tarRow.TryGetValue(key, out var tarVal);

                                if (!hasLeg)
                                {
                                    rowMismatches.Add($"컬럼 `{key}` 누락 (레거시에는 없고 신규에만 존재)");
                                    casePassed = false;
                                }
                                else if (!hasTar)
                                {
                                    rowMismatches.Add($"컬럼 `{key}` 누락 (신규 코드에 해당 컬럼 없음)");
                                    casePassed = false;
                                }
                                else if (!ValuesAreEqual(legVal, tarVal))
                                {
                                    rowMismatches.Add($"`{key}` 값 불일치: 레거시 `{legVal ?? "null"}` (타입: {legVal?.GetType().Name}) vs 신규 `{tarVal ?? "null"}` (타입: {tarVal?.GetType().Name})");
                                    casePassed = false;
                                }
                            }

                            if (rowMismatches.Any())
                            {
                                caseDetails.AppendLine($"- **결과셋 #{rIdx + 1}의 행 #{rowIdx + 1} 데이터 불일치**:");
                                foreach (var mis in rowMismatches)
                                {
                                    caseDetails.AppendLine($"  - {mis}");
                                }
                            }
                        }
                    }
                }

                if (casePassed)
                {
                    matchedCases++;
                    caseDetails.AppendLine("- **결과**: ✅ 데이터 정합성 100% 일치");
                }
                else
                {
                    caseDetails.AppendLine("- **결과**: ❌ 데이터 정합성 불일치 발생");
                }

                detailsList.Add(caseDetails.ToString());
            }

            var successRate = totalCases > 0 ? (matchedCases * 100.0 / totalCases) : 0;
            sb.AppendLine($"| 구분 | 수치 |");
            sb.AppendLine($"| :--- | :---: |");
            sb.AppendLine($"| 총 테스트 케이스 수 | {totalCases} 개 |");
            sb.AppendLine($"| 정합성 일치 케이스 수 | {matchedCases} 개 |");
            sb.AppendLine($"| **데이터 일치율** | **{successRate:F1}%** |");
            sb.AppendLine();

            sb.AppendLine("## 📝 테스트 케이스별 상세 검증 내역");
            foreach (var detail in detailsList)
            {
                sb.AppendLine(detail);
            }

            return sb.ToString();
        }

        private bool ValuesAreEqual(object? val1, object? val2)
        {
            if (val1 == null && val2 == null) return true;
            if (val1 == null || val2 == null) return false;

            // 문자열 표현으로 대조 (소수점 자릿수 표현이나 타입 불일치 유연성 부여)
            var str1 = NormalizeValueString(val1);
            var str2 = NormalizeValueString(val2);

            return str1.Equals(str2, StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeValueString(object val)
        {
            if (val is JsonElement je)
            {
                return je.ValueKind switch
                {
                    JsonValueKind.Null => string.Empty,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => je.GetRawText(),
                    _ => je.ToString()
                };
            }

            if (val is double d)
            {
                // 실수의 표현 정밀도 통일화
                return d.ToString("0.000");
            }
            if (val is float f)
            {
                return f.ToString("0.000");
            }
            if (val is decimal dec)
            {
                return dec.ToString("0.000");
            }
            if (val is DateTime dt)
            {
                // 날짜 포맷 통일 (시분초 유무 고려)
                return dt.ToString("yyyy-MM-dd HH:mm:ss");
            }

            return val.ToString() ?? string.Empty;
        }

        // --- 내부 DTO 모델 구조 ---
        private class ExecutionOutput
        {
            public string ProcedureName { get; set; } = string.Empty;
            public List<TestCaseResult> ExecutionResults { get; set; } = new();
        }

        private class TestCaseResult
        {
            public string CaseId { get; set; } = string.Empty;
            public string Status { get; set; } = "SUCCESS";
            public string? ErrorCode { get; set; }
            public List<List<Dictionary<string, object>>> ResultSets { get; set; } = new();
        }
    }
}
