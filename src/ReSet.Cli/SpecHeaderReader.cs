using System;

namespace ReSet.Cli
{
    /// <summary>명세서 상단 YAML 헤더에서 검증 상태와 점수를 읽는다.</summary>
    public sealed record SpecHeader(
        string? VerificationStatus,
        int? NormalizedScore,
        int? Accuracy,
        int? Crud,
        int? Readability,
        int? Exception);

    public static class SpecHeaderReader
    {
        public static SpecHeader Read(string markdown)
        {
            string? status = null;
            int? score = null, acc = null, crud = null, read = null, ex = null;

            if (!string.IsNullOrEmpty(markdown) && markdown.StartsWith("---"))
            {
                var endOfYaml = markdown.IndexOf("---", 3, StringComparison.Ordinal);
                if (endOfYaml > 0)
                {
                    foreach (var line in markdown.Substring(3, endOfYaml - 3).Split('\n'))
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length != 2) continue;

                        var key = parts[0].Trim();
                        var val = parts[1].Trim();

                        var commentIdx = val.IndexOf('#');
                        if (commentIdx >= 0) val = val.Substring(0, commentIdx).Trim();

                        var parenIdx = val.IndexOf('(');
                        if (parenIdx >= 0) val = val.Substring(0, parenIdx).Trim();

                        if (key == "검증 상태")
                        {
                            status = string.IsNullOrWhiteSpace(val) ? null : val;
                            continue;
                        }

                        var slashIdx = val.IndexOf('/');
                        var numberPart = slashIdx >= 0 ? val.Substring(0, slashIdx).Trim() : val;

                        if ((key == "AiConfidenceScore" || key == "종합 신뢰도 점수" || key == "종합 신뢰도" || key == "종합신뢰도") && int.TryParse(numberPart, out var scoreVal)) score = scoreVal;
                        else if ((key == "AccuracyScore" || key == "정합성 점수" || key == "정합성") && int.TryParse(numberPart, out var accVal)) acc = accVal;
                        else if ((key == "CrudScore" || key == "CRUD 점수" || key == "CRUD") && int.TryParse(numberPart, out var crudVal)) crud = crudVal;
                        else if ((key == "ReadabilityScore" || key == "가독성 점수" || key == "가독성") && int.TryParse(numberPart, out var readVal)) read = readVal;
                        else if ((key == "ExceptionScore" || key == "예외처리 점수" || key == "예외처리" || key == "예외 처리 점수" || key == "예외 처리") && int.TryParse(numberPart, out var exVal)) ex = exVal;
                    }
                }
            }

            return new SpecHeader(status, score, acc, crud, read, ex);
        }
    }
}
