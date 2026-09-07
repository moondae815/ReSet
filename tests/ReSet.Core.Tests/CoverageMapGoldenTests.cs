using System;
using System.Collections.Generic;
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
    /// 설계서 §6 — 맵이 사람 감사와 같은 것을 보고 있는지 세 요구로 고정한다.
    ///
    /// [왜 감사 10회차의 🟡을 재현 목표로 삼지 않는가] 그 🟡은 COMM_UPD DML 범위 표의
    /// PGNAME 중복 전사다. "적힌 게 이상하다"이지 "안 적혔다"가 아니라 맵이 원리적으로
    /// 못 본다. 이걸 요구하면 통과할 수 없는 테스트가 된다(설계서 §6 서두).
    /// </summary>
    public class CoverageMapGoldenTests
    {
        private readonly ITestOutputHelper _output;

        public CoverageMapGoldenTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// 코퍼스 루트. 판정 근거(왜 "output/이 있다"로 판정하지 않는가)는
        /// <see cref="CorpusPaths.RepoRootIfCorpusPresent()"/>에 있다 - 이 판정이 세 곳에 복제돼 있던 것을
        /// 2026-08-26에 그리로 모았다.
        /// </summary>
        private static string RepoRootIfCorpusPresent() => CorpusPaths.RepoRootIfCorpusPresent();

        private static ObjectCoverage? Load(string root, string outputDirName, string objectName)
        {
            var baseDir = Path.Combine(root, outputDirName, "Procedures", objectName);
            var metaPath = Path.Combine(baseDir, "raw", "metadata.json");
            var specPath = Path.Combine(baseDir, "docs", "Spec.md");
            if (!File.Exists(metaPath) || !File.Exists(specPath)) return null;

            var spDef = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(metaPath));
            if (spDef == null) return null;

            return CoverageMapComposer.Compose(objectName, spDef, File.ReadAllText(specPath));
        }

        // ------------------------------------------------------------------
        // 요구 1 — 현재 판 🟥 총계가 0인가
        // ------------------------------------------------------------------

        /// <summary>
        /// 명세서가 짚지 못한 원본 문장(🟥)이 하나도 없어야 한다는 원래 계약이다.
        ///
        /// [전이 창은 닫혔다 - 2026-08-25] 기계 확정 표 둘(「트랜잭션 경계」·「변수 대입」)을
        /// 더한 회차가 이 단언을 한동안 임시 상수 205로 바꿔 두고 있었다. 재료를 세기
        /// 시작한 맵과 아직 옛 프롬프트로 만들어진 산출물 사이에 설계서 §3이 예측한 창이
        /// 열렸기 때문이다(재료 있음 + 앵커 없음 = 🟥). 캐시 16 전건 재생성이 그 창을 닫아
        /// <b>205 → 0</b>이 됐고, 그래서 임시 상수를 지우고 원래 계약을 복원한다.
        /// 전이 창의 분해(트랜잭션 105 + SET 100)는 확장 설계서에 남아 있다.
        /// </summary>
        [SkippableFact]
        public void Requirement1_CurrentEdition_SpecMissingShouldBeZero()
        {
            var root = RepoRootIfCorpusPresent();
            Skip.If(string.IsNullOrEmpty(root), "output/의 실물 SP 산출물을 찾지 못했다 - 요구 1 건너뜀");

            var procDir = Path.Combine(root, "output", "Procedures");
            var objectDirs = Directory.GetDirectories(procDir);
            Skip.If(objectDirs.Length == 0, "output/Procedures가 비어 있다 - 요구 1 건너뜀");

            var total = 0;
            var checkedCount = 0;
            foreach (var objectDir in objectDirs)
            {
                var name = Path.GetFileName(objectDir);
                var coverage = Load(root, "output", name);
                if (coverage == null) continue;
                checkedCount++;

                var missing = coverage.Count(CoverageState.SpecMissing);
                total += missing;
                if (missing > 0)
                {
                    _output.WriteLine($"{coverage.ObjectName}: 🟥 {missing}");
                    foreach (var s in coverage.Statements.Where(s => s.State == CoverageState.SpecMissing))
                    {
                        _output.WriteLine($"   줄 {s.Statement.StartLine}-{s.Statement.EndLine} {s.Statement.StatementType}");
                    }
                }
            }

            _output.WriteLine($"실측 대상: {checkedCount}/{objectDirs.Length} SP");
            _output.WriteLine($"현재 판 🟥 총계: {total}");

            Assert.True(total == 0,
                $"🟥 총계가 {total}건이다. 명세서가 짚지 못한 원본 문장이 그만큼 있다는 뜻이므로 " +
                "위에 찍힌 줄 번호와 문장 유형으로 가려라. 기계 확정 표를 새로 더한 직후라면 " +
                "재생성 전까지 이 값이 오르는 것이 정상이다(확장 설계서 §3의 전이 창) - 그 " +
                "경우에는 재생성을 돌려 창을 닫아라.");
        }

        // ------------------------------------------------------------------
        // 요구 2·3 — 2026-09-07 폐기 (재료를 사람이 지웠다)
        // ------------------------------------------------------------------
        //
        // 두 요구는 `output.bak-2026-08-22`(과거 판 스냅샷)를 기준 세대로 썼다.
        // 사람이 과거 판 코퍼스 전부를 지우기로 결정했고(2026-09-07), 그 스냅샷은
        // 「재생성할 수 없다」가 `CorpusPaths` 주석에 못박혀 있던 것이다 - 다시 뽑으면
        // 새 판이지 그 판이 아니다. 그래서 **대체 표본이 없다.**
        //
        // 무엇을 잃었는지 정확히 적는다(폐기의 값은 이 문장에 있다):
        //   • 요구 2 — 현재 판 🟥+🟧 합계가 과거 판보다 나쁘지 않은가. 맵이 감사 기록과
        //     같은 **방향**을 가리키는지 보던 유일한 자였다.
        //   • 요구 3 — `INS_EXTRA4PLCARD`의 조인 `ON` 리터럴 네 줄(37·167·190·206)에
        //     집합 술어 앵커가 **새로** 생겼는지. 셋 중 가장 날카로웠고, 과거 판에
        //     앵커가 0이었다는 사실 자체가 오라클이었다.
        //
        // 남은 요구 1은 현재 판만 본다 - **회귀의 방향은 이제 아무도 안 잰다.**
        // 경위와 판단 근거: docs/audit-reports/2026-09-07-과거판-코퍼스-폐기.md
    }
}
