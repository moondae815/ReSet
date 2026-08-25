using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실물 코퍼스에서 두 추출기가 폭주하지 않는지 보고 실제 건수를 출력한다.
    /// 합성 픽스처가 못 보는 것을 잡는 자리다 - 실제 DDL에만 있는 모양(파이프·개행이
    /// 든 대입식, 이름 있는 트랜잭션)은 여기서만 드러난다.
    ///
    /// [왜 건수를 단언하지 않는가] 건수는 코퍼스가 정하는 관측값이지 이 코드가 지켜야 할
    /// 계약이 아니다. 숫자로 못박으면 코퍼스에 SP가 하나 늘 때마다 이 테스트가 빨개지고,
    /// 다음 사람은 관측을 읽는 대신 기대값을 고치게 된다. 추출기가 죽지 않았다는 것만
    /// 단언하고 숫자는 출력으로 남긴다 - 설계서 「미확정 사항」이 그 숫자를 받는 자리다.
    /// </summary>
    public class MachineTableExpansionCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public MachineTableExpansionCorpusTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// 저장소 루트를 찾는다.
        ///
        /// [왜 `output/` 존재만 보면 안 되는가 - 2026-08-25 실측] 다른 테스트들이
        /// `bin/Debug/net10.0/output/Procedures/` 아래에 합성 픽스처(dbo.USP_Root 등)를
        /// 만든다. `Directory.Exists(dir/"output")`으로 올라가면 그 픽스처 디렉터리가
        /// **먼저** 걸려 스모크가 실물 코퍼스 대신 픽스처 1개를 재고 "객체 1 · 트랜잭션
        /// 합 0 · SET 합 0"을 초록으로 찍는다 - 건너뜀도 실패도 아닌 조용한 오측이다.
        /// 그래서 `CoverageMapGoldenTests.RepoRoot`의 관례를 따라 **실물 코퍼스에만 있는
        /// 객체 하나**를 앵커로 삼는다.
        /// </summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(
                       dir.FullName, "output", "Procedures",
                       "dbo.UP_UTIL_SETTLE_EXCEPTION_PROC", "raw", "metadata.json")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? string.Empty;
        }

        [SkippableFact]
        public void Extractors_OverTheCorpus_ShouldReportCountsWithoutExploding()
        {
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var procedures = Path.Combine(root, "output", "Procedures");
            Skip.IfNot(Directory.Exists(procedures), CorpusSkip.Reason);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            int tranTotal = 0, setTotal = 0, objects = 0;
            int pipeOrNewlineInExpression = 0;

            foreach (var dir in Directory.GetDirectories(procedures))
            {
                var meta = Path.Combine(dir, "raw", "metadata.json");
                if (!File.Exists(meta)) continue;

                var def = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(meta), opts);
                if (def == null) continue;
                objects++;

                var trans = TransactionBoundaryExtractor.Extract(def.DdlText);
                var sets = SetAssignmentExtractor.Extract(def.DdlText);
                tranTotal += trans.Count;
                setTotal += sets.Count;

                // 설계서 「미확정 사항」2번 - 셀 이스케이프 왕복이 실전에서 검증되는가.
                pipeOrNewlineInExpression += sets.Count(
                    f => f.Expression.Contains('|') || f.Expression.Contains('\n'));

                _output.WriteLine($"{Path.GetFileName(dir),-45} 트랜잭션 {trans.Count,3} · SET {sets.Count,3}");
            }

            _output.WriteLine("");
            _output.WriteLine($"객체 {objects} · 트랜잭션 합 {tranTotal} · SET 합 {setTotal}");
            _output.WriteLine($"대입식에 파이프/개행이 든 건수: {pipeOrNewlineInExpression}");
            _output.WriteLine("백로그 예측: 트랜잭션 105 · SET 97");

            // 건수는 관측 대상이지 계약이 아니다. 추출기가 죽지 않았다는 것만 단언한다.
            Assert.True(objects > 0, "코퍼스 객체를 하나도 못 읽었다");
        }
    }
}
