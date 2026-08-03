using System.Linq;
using Xunit;

namespace ReSet.Core.Tests;

public sealed class CancellationPolicyTests
{
    // 규칙: 취소 가능한 await를 감싸면서 OperationCanceledException을 거르지도
    // 다시 던지지도 않는 넓은 catch는 위반이다. 지금까지 발견된 네 모양
    // (빈 catch, 알림 후 계속, 바깥 핸들러 가리기, 타입 세탁)이 모두 이 서명을 갖는다.

    [Fact]
    public void Scanner_FlagsABroadCatchAroundACancellableAwait()
    {
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(System.Threading.CancellationToken cancellationToken)
    {
        try { await Work(cancellationToken); }
        catch (System.Exception ex) { System.Console.WriteLine(ex.Message); }
    }
    async System.Threading.Tasks.Task Work(System.Threading.CancellationToken ct) { }
}";

        var offenders = CancellationPolicyScanner.ScanSource(source, "Fake.cs");

        var offender = Assert.Single(offenders);
        Assert.Equal("Fake.cs", offender.RelativePath);
        Assert.Equal("M", offender.Member);
    }

    [Fact]
    public void Scanner_DoesNotFlagACatchWithNoCancellableAwait()
    {
        // 동기 IO의 soft-fail은 취소와 무관하다. 이 코드베이스에 넓은 catch가
        // 100곳 넘게 있는 정당한 이유이며, 여기서 거짓 양성을 내면 규칙이 버려진다.
        var source = @"
class C
{
    void M()
    {
        try { System.IO.File.Delete(""x""); }
        catch (System.Exception ex) { System.Console.WriteLine(ex.Message); }
    }
}";

        Assert.Empty(CancellationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagACatchThatFiltersCancellation()
    {
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(System.Threading.CancellationToken cancellationToken)
    {
        try { await Work(cancellationToken); }
        catch (System.Exception ex) when (ex is not System.OperationCanceledException) { }
    }
    async System.Threading.Tasks.Task Work(System.Threading.CancellationToken ct) { }
}";

        Assert.Empty(CancellationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagABroadCatchPrecededByAnOperationCanceledClause()
    {
        // C#은 catch 절을 위에서부터 매칭하므로 뒤의 넓은 catch는 OCE를 볼 수 없다.
        // 실례: src/ReSet.Core/Services/MetadataExporter.cs
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(System.Threading.CancellationToken cancellationToken)
    {
        try { await Work(cancellationToken); }
        catch (System.OperationCanceledException) { throw; }
        catch (System.Exception ex) { System.Console.WriteLine(ex.Message); }
    }
    async System.Threading.Tasks.Task Work(System.Threading.CancellationToken ct) { }
}";

        Assert.Empty(CancellationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void Scanner_DoesNotFlagAnExplicitOperationCanceledClause()
    {
        // 명시적으로 OCE를 잡는 것은 사고가 아니라 의도다.
        // 실례: src/ReSet.Core/Services/DependencyAnalysisOrchestrator.cs — 취소를
        // 예외가 아니라 결과 상태로 바꾸는 것이 그 메서드의 계약이다.
        var source = @"
class C
{
    async System.Threading.Tasks.Task M(System.Threading.CancellationToken cancellationToken)
    {
        try { await Work(cancellationToken); }
        catch (System.OperationCanceledException) { System.Console.WriteLine(""부분 취소""); }
    }
    async System.Threading.Tasks.Task Work(System.Threading.CancellationToken ct) { }
}";

        Assert.Empty(CancellationPolicyScanner.ScanSource(source, "Fake.cs"));
    }

    [Fact]
    public void NoFileExceedsItsCancellationBaseline()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var actual = CancellationPolicyScanner
            .ScanDirectory(System.IO.Path.Combine(repoRoot, "src"))
            .GroupBy(offender => offender.RelativePath)
            .ToDictionary(group => group.Key, group => group.ToList(), System.StringComparer.Ordinal);

        var baselinePath = System.IO.Path.Combine(
            repoRoot, "tests", "ReSet.Core.Tests", "cancellation-policy-baseline.txt");
        var allowed = ReadBaseline(baselinePath);

        var failures = new System.Text.StringBuilder();

        foreach (var path in actual.Keys.Union(allowed.Keys).OrderBy(key => key, System.StringComparer.Ordinal))
        {
            var actualOffenders = actual.TryGetValue(path, out var list) ? list : new System.Collections.Generic.List<CancellationOffender>();
            var allowedCount = allowed.TryGetValue(path, out var count) ? count : 0;

            if (actualOffenders.Count == allowedCount) continue;

            failures.AppendLine($"{path}: 허용 {allowedCount}건, 실제 {actualOffenders.Count}건");
            foreach (var offender in actualOffenders.OrderBy(item => item.Line))
            {
                failures.AppendLine($"  {offender.RelativePath}:{offender.Line} ({offender.Member})");
            }

            failures.AppendLine(actualOffenders.Count > allowedCount
                ? "  → 새 위반입니다. 위 목록에서 방금 편집한 줄을 찾으십시오."
                : $"  → 고쳤다면 기준선을 {actualOffenders.Count}로 내리십시오.");
            failures.AppendLine();
        }

        Assert.True(
            failures.Length == 0,
            "취소를 삼킬 수 있는 catch의 개수가 기준선과 다릅니다.\n\n" + failures);
    }

    private static System.Collections.Generic.Dictionary<string, int> ReadBaseline(string path)
    {
        var result = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
        foreach (var raw in System.IO.File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.LastIndexOf('=');
            Assert.True(separator > 0, $"기준선 파일의 형식이 잘못되었습니다: {raw}");
            result[line[..separator].Trim()] = int.Parse(line[(separator + 1)..].Trim());
        }

        return result;
    }
}
