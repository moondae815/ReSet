using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReSet.Core.Tests;

/// <param name="File">저장소 루트 기준 상대 경로.</param>
/// <param name="Line">두 번째 <c>&lt;summary&gt;</c>가 시작하는 1-기반 줄 번호.</param>
public sealed record OrphanedDocComment(string File, int Line);

/// <summary>
/// 한 문서 주석 안에 <c>&lt;summary&gt;</c>가 둘 이상 있는 자리를 찾는다.
///
/// [왜 이것이 결함인가] C#은 연속된 <c>///</c> 줄을 <b>하나의</b> 문서 주석으로 묶어
/// 바로 다음 멤버에 붙인다. 그래서 <c>&lt;/summary&gt;</c> 뒤에 곧바로
/// <c>&lt;summary&gt;</c>가 오면, 앞 블록이 설명하던 멤버는 문서를 잃고 그 근거가
/// 엉뚱한 멤버에 실린다. 이 저장소는 근거를 코드 주석에 두는 것이 규약이므로
/// 「근거는 있는데 찾을 수 없는」 상태가 된다.
///
/// [왜 테스트로 막는가] 새 멤버를 기존 문서 블록과 그 멤버 사이에 끼워 넣으면
/// 조용히 발생하고 <b>빌드 경고가 나지 않는다</b>(XML 문서 생성이 꺼져 있다).
/// 2026-08-27 한 브랜치에서 두 번 났고 둘 다 사람이 리뷰로 잡았다 — 그 자리를
/// 사람에게 맡기지 않기 위한 가드다.
///
/// [의도적으로 쌓고 싶다면] 두 블록 사이에 빈 줄을 한 줄 넣으면 별개의 주석이
/// 되어 이 검사에 걸리지 않는다. 다만 앞 블록은 여전히 어느 멤버에도 안 붙는다.
/// </summary>
public static class OrphanedDocCommentScanner
{
    public static IReadOnlyList<OrphanedDocComment> ScanSource(string source, string path)
    {
        var found = new List<OrphanedDocComment>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var summariesInBlock = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                // 문서 주석이 아닌 줄(멤버 선언·빈 줄 등)을 만나면 블록이 끊긴다.
                summariesInBlock = 0;
                continue;
            }

            // 여는 태그의 **개수**를 센다. `</summary>`가 단독 줄인 여러 줄 형태와
            // `<summary>…</summary>`가 한 줄에 든 형태를 함께 잡으려면 닫는 태그를
            // 좇는 것보다 이쪽이 맞다 - 처음에 닫는 태그로 짰다가 한 줄 형태를
            // 통째로 놓쳤고, 그 사실을 단위 테스트가 잡았다.
            if (!trimmed[3..].Contains("<summary>", StringComparison.Ordinal)) continue;

            summariesInBlock++;
            if (summariesInBlock >= 2) found.Add(new OrphanedDocComment(path, i + 1));
        }

        return found;
    }

    public static IReadOnlyList<OrphanedDocComment> ScanRepository(string repoRoot)
    {
        var found = new List<OrphanedDocComment>();

        foreach (var root in new[] { "src", "tests" })
        {
            var dir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // 경로 세그먼트로 판정한다 - 부분 문자열 검사는 "Robot/" 같은 이름을
                // 오탐하고 최상위 obj/ 를 놓친다(CancellationPolicyScanner 와 같은 관례).
                if (file.Split(Path.DirectorySeparatorChar).Any(segment =>
                        segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(repoRoot, file);
                found.AddRange(ScanSource(File.ReadAllText(file), relative));
            }
        }

        return found;
    }
}
