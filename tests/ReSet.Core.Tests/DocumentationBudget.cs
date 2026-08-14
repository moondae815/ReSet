using System.Collections.Generic;
using System.Text;

namespace ReSet.Core.Tests;

/// <summary>라인 예산을 넘긴 줄 하나.</summary>
public sealed record OversizedLine(int Line, int Bytes, string Excerpt);

/// <summary>
/// 자동 로드되는 문서의 크기를 잰다.
///
/// 총량과 라인 길이를 따로 재는 이유: 실제 병리는 총량이 아니라 4,162바이트짜리
/// "목록 항목" 하나였다. 총량 상한은 여러 항목에 분산시켜 우회할 수 있지만, 라인
/// 상한은 그 병리 자체를 겨냥하고 문서가 정당하게 자라도 계속 참이다.
/// </summary>
public static class DocumentationBudget
{
    public static int MeasureBytes(string text) => Encoding.UTF8.GetByteCount(text);

    public static IReadOnlyList<OversizedLine> FindOversizedLines(string text, int maxLineBytes)
    {
        var result = new List<OversizedLine>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var bytes = Encoding.UTF8.GetByteCount(lines[index]);
            if (bytes <= maxLineBytes) continue;

            result.Add(new OversizedLine(index + 1, bytes, Excerpt(lines[index])));
        }

        return result;
    }

    private static string Excerpt(string line)
    {
        const int maxChars = 60;
        if (line.Length <= maxChars) return line;

        var cut = maxChars;
        // 서러게이트 쌍을 가르지 않는다. 문서 헤딩에 이모지가 흔하다.
        if (char.IsHighSurrogate(line[cut - 1])) cut--;

        return line[..cut] + "…";
    }
}
