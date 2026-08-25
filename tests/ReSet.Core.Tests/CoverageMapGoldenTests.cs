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
        /// Task 4(<c>CoverageMapProbeTests</c>)가 이미 겪은 함정을 그대로 피한다: "output/
        /// 디렉터리를 가진 조상"까지만 올라가면 <c>bin/Debug/net10.0/output/</c>에 다른
        /// 테스트가 남긴 가짜 산출물(스크래치 <c>dbo.USP_Root</c> 1건)을 집어 조용히 틀린
        /// 숫자를 낸다. 그래서 "output/이 있다"가 아니라 "이 게이트가 아는 실물 SP 하나가
        /// 실제로 있다"로 판정 기준을 좁힌다. <c>CoverageMapProbeTests.RepoRoot</c>와 같은
        /// 판정이다 - 새로 짜지 않고 재사용한다.
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
        // 요구 1 — 현재 판 🟥 총계가 전이 창 실측값에 머무는가
        // ------------------------------------------------------------------

        /// <summary>
        /// 전이 창의 🟥 총계 실측값. <b>재생성 전까지만 유효한 임시값이다</b> - 영구 계약이
        /// 아니다. 명세서가 재생성되면 이 값은 내려가야 하고, 0에 도달하면 이 상수를 지우고
        /// 원래 계약(총계 0)으로 돌아간다.
        ///
        /// [왜 205인가 - 분해] 14 SP 코퍼스 실측(2026-08-24, WAVE_BASE <c>abbc0c6</c>):
        /// <code>
        ///   트랜잭션 경계  BEGIN 12 + COMMIT 12 + ROLLBACK 81 = 105  (잎 105개 전부 🟥)
        ///   변수 대입      SetVariableStatement 잎 103개 중 100개가 🟥
        ///   합계           105 + 100 = 205
        /// </code>
        /// SET 잎 103개 중 🟥이 아닌 3개는 <c>dbo.UP_UTIL_SETTLE_PROC_ETC</c>의 69·113·114행이다
        /// - <c>WHILE</c> 최상위 상수 재설정이라 기존 「실행 의미 (기계 확정 — 수정 금지)」 표
        /// 앵커가 이미 짚고 있어 🟩을 유지한다(앵커 출처를 직접 찍어 확인했다).
        ///
        /// [설계서 예측 202와 3건 어긋난 이유 - 이 브랜치의 결함이 아니다] 확장 설계서 §3의
        /// 202는 <c>105 + 97</c>이었고, 그 97은 DDL 커버리지 맵 설계서(<c>2026-08-24-ddl-coverage-map-design.md</c>)
        /// 🟧 백로그 표에서 온 값이다. 그 표의 <c>SetVariableStatement</c> 「건수」 칸 100은
        /// <b>이미 3건을 뺀 🟧 개수</b>(잎 103 − 재료 붙은 3)인데, 같은 칸의 서술이 거기서
        /// 3을 <b>한 번 더</b> 빼 "나머지 97건"이라고 적었다. 즉 상류 백로그의 뺄셈 중복이지
        /// 새 추출기의 오동작이 아니다. 의도한 메커니즘은 정확히 작동했다.
        /// </summary>
        private const int TransitionWindowSpecMissing = 205;

        /// <summary>
        /// 기계 확정 표 확장(<c>2026-08-24-machine-table-expansion</c>) 브랜치가 열어 둔
        /// <b>전이 창</b>의 실측값을 못박는다. 원래 계약은 "🟥 총계 0"이었다 - 그 계약은
        /// 명세서 재생성이 끝나면 되돌아온다(아래 <see cref="TransitionWindowSpecMissing"/>
        /// 주석 참고).
        /// </summary>
        [SkippableFact]
        public void Requirement1_CurrentEdition_SpecMissingShouldMatchTransitionWindowCount()
        {
            // [원래 계약] 감사 10회차가 🔴 0 · 🟠 0으로 끝났고 Task 4 실측(14 SP · 잎 487개)도
            // 🟥 0을 냈다 - 이 테스트는 그 0을 회귀로 잠그고 있었다.
            //
            // [왜 0이 아닌가 - 전이 창] 같은 브랜치가 기계 확정 표 둘(「트랜잭션 경계」·
            // 「변수 대입」)을 새로 만들면서 CoverageMapComposer.ExtractorFactLines가 그
            // 재료를 세기 시작했다. 그런데 output/**/Spec.md는 아직 옛 프롬프트로 생성된
            // 것이라 그 표가 없다. 재생성은 별도 회차로 미뤘다. 그래서 설계서 §3이 예측한
            // 창이 실제로 열렸다:
            //
            //     지금        재료 없음 + 앵커 없음 = 🟧 관할 밖
            //     구현 직후   재료 있음 + 앵커 없음 = 🟥 명세서 결함   ← 여기
            //     재생성 후   재료 있음 + 앵커 있음 = 🟩 정합
            //
            // 205는 맵이 틀린 것이 아니라 맵이 정직하게 말하는 참이다.
            var root = RepoRoot();
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

            var direction = total > TransitionWindowSpecMissing
                ? $"{total - TransitionWindowSpecMissing}건 늘었다. 새 결함이 들어왔거나 추출기 " +
                  "재료가 또 늘었다 - 위에 찍힌 문장 유형으로 가려라. 트랜잭션 경계·SET 대입 " +
                  "밖의 유형이 새로 보이면 재료가 는 것이고, 같은 유형인데 건수만 늘었으면 " +
                  "추출기나 앵커 인식이 퇴행한 것이다."
                : $"{TransitionWindowSpecMissing - total}건 줄었다. 재생성이 돌았거나 앵커가 " +
                  $"생겼다 - 기대한 방향이다. 이 단언의 기대값을 실측값 {total}으로 내려라. " +
                  "0에 도달했으면 TransitionWindowSpecMissing 상수와 이 전이 창 주석을 지우고 " +
                  "메서드 이름을 Requirement1_CurrentEdition_SpecMissingShouldBeZero로 되돌려 " +
                  "원래 계약(총계 0)을 복원하라 - 그것이 이 테스트의 최종 형태다.";

            Assert.True(total == TransitionWindowSpecMissing,
                $"🟥 총계가 {total}건이다. 전이 창 실측값 {TransitionWindowSpecMissing}건에서 {direction}");
        }

        // ------------------------------------------------------------------
        // 요구 2 — 과거 판 대비 감소가 감사 기록과 같은 방향인가
        // ------------------------------------------------------------------

        [SkippableFact]
        public void Requirement2_AgainstPriorEdition_DefectsShouldNotHaveGrown()
        {
            // 카탈로그 9회차: "8회차 34건 중 31건 소멸". 그 34건은 사람이 센 결함(전체 Job
            // 31개 객체 기준)이고, 이 맵은 14 SP의 잎 문장 🟥+🟧 개수를 센다 - 척도가 달라
            // 절대치가 같을 이유는 없지만, 방향(감소)은 같아야 한다.
            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/의 실물 SP 산출물을 찾지 못했다 - 요구 2 건너뜀");

            var bakDir = Path.Combine(root, "output.bak-2026-08-22", "Procedures");
            Skip.IfNot(Directory.Exists(bakDir),
                "output.bak-2026-08-22가 없다(.gitignore 대상) - 과거 판 대조 건너뜀");

            var priorTotal = 0;
            var currentTotal = 0;
            var pairCount = 0;
            var perObject = new List<string>();

            foreach (var objectDir in Directory.GetDirectories(bakDir))
            {
                var name = Path.GetFileName(objectDir);
                var prior = Load(root, "output.bak-2026-08-22", name);
                var current = Load(root, "output", name);
                if (prior == null || current == null)
                {
                    _output.WriteLine($"[짝 없음] {name} - 과거·현재 산출물 중 하나가 없다");
                    continue;
                }
                pairCount++;

                var priorBad = prior.Count(CoverageState.SpecMissing) + prior.Count(CoverageState.OutOfScope);
                var curBad = current.Count(CoverageState.SpecMissing) + current.Count(CoverageState.OutOfScope);
                priorTotal += priorBad;
                currentTotal += curBad;
                perObject.Add($"{name}: 과거 {priorBad} -> 현재 {curBad} (차이 {curBad - priorBad})");
            }

            Skip.If(pairCount == 0, "짝을 이루는 과거·현재 산출물이 하나도 없다 - 요구 2 건너뜀");

            foreach (var line in perObject) _output.WriteLine(line);
            _output.WriteLine($"대상 {pairCount}개 SP");
            _output.WriteLine($"과거 판 🟥+🟧 합계: {priorTotal}");
            _output.WriteLine($"현재 판 🟥+🟧 합계: {currentTotal}");

            Assert.True(currentTotal <= priorTotal,
                $"현재 판({currentTotal})이 과거 판({priorTotal})보다 나쁘다. " +
                "카탈로그 9회차가 기록한 방향(34건 중 31건 소멸)과 어긋난다.");
        }

        // ------------------------------------------------------------------
        // 요구 3 — 사각지대 실례 하나가 뒤집히는가 (셋 중 가장 날카롭다)
        // ------------------------------------------------------------------

        /// <summary>
        /// 9회차 🟠: `INS_EXTRA4PLCARD`의 조인 `ON` 절 리터럴 `PG.ExtraType IN (2,3)`
        /// (원본 줄 21·37·167·190·206, 5문장)이 집합 술어 표가 `WHERE`만 담아 받아 줄
        /// 표가 없던 ③ 사각지대였다. 10회차에 조인 `ON` 행이 생기며 닫혔다.
        ///
        /// [실측으로 계획을 고쳤다 - 플랜의 참조 구현은 쓸 수 없었다] 플랜 원안은 객체
        /// 전체의 🟩-🟧 점수 차로 판정한다. 실측하면 이 객체는 과거·현재 판 모두
        /// Consistent=4·OutOfScope=13으로 <b>점수가 완전히 같다</b> - 점수 차 기반 단언은
        /// 반드시 실패한다. 원인은 CoverageMapComposer가 잎 문장 전체 단위로 상태를 매기기
        /// 때문이다(DELETE/INSERT/UPDATE 잎 하나가 수십 줄을 덮고, 그 안에 다른 사실·앵커가
        /// 이미 많아 두 판 모두 그 잎 자체는 항상 Consistent였다). 게다가 줄 37·167·190·206은
        /// 과거 판에서도 "잠금 힌트" 표가 <b>같은 줄 번호</b>를 우연히 인용해 앵커가 이미
        /// 있었다(예: `| DELETE 1 | 37 | TPGProperty | PG | 최상위 | (없음) |`) - 앵커가
        /// 줄 번호로만 걸리고 어느 컬럼·리터럴을 짚는지는 구분하지 않기 때문에, "안 적힘"이
        /// "적혔지만 다른 이유로 적힘"과 State 레벨에서 구별되지 않는다. 감사 10회차 🟡
        /// (PGNAME 중복 전사)과 같은 부류의 줄-단위 해상도 한계다.
        ///
        /// 그래서 총량·잎 State가 아니라 <b>이 리터럴을 실제로 짚은 앵커가 있는지</b>를
        /// 직접 본다 - `StatementCoverage.Anchors[].Source`가 어느 (기계 확정) 표에서 왔는지
        /// 이미 싣고 있으므로, "집합 술어" 표발 앵커가 이 줄들에 새로 생겼는지를 잰다. 이것이
        /// 감사가 실제로 관찰한 변화(조인 ON 행 추가)와 정확히 대응하는, 이 데이터 구조에서
        /// 가능한 가장 날카로운 관측이다.
        /// </summary>
        [SkippableFact]
        public void Requirement3_JoinOnLiteral_SetPredicateAnchorShouldAppearAtGivenLines()
        {
            const string name = "dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD";
            var targetLines = new[] { 37, 167, 190, 206 };

            var root = RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), "output/의 실물 SP 산출물을 찾지 못했다 - 요구 3 건너뜀");

            var prior = Load(root, "output.bak-2026-08-22", name);
            var current = Load(root, "output", name);
            Skip.If(prior == null, "과거 판(output.bak-2026-08-22)에 INS_EXTRA4PLCARD 산출물이 없다 - 요구 3 건너뜀");
            Skip.If(current == null, "현재 판(output)에 INS_EXTRA4PLCARD 산출물이 없다 - 요구 3 건너뜀");

            static int CountSetPredicateAnchorsAt(ObjectCoverage coverage, int line) =>
                coverage.Statements
                    .SelectMany(s => s.Anchors)
                    .Count(a => a.Line == line && a.Source.Contains("집합 술어", StringComparison.Ordinal));

            var priorHits = targetLines.ToDictionary(l => l, l => CountSetPredicateAnchorsAt(prior!, l));
            var currentHits = targetLines.ToDictionary(l => l, l => CountSetPredicateAnchorsAt(current!, l));

            foreach (var line in targetLines)
            {
                _output.WriteLine($"줄 {line}: 과거 판 집합 술어 앵커 {priorHits[line]}개 -> 현재 판 {currentHits[line]}개");
            }

            var priorTotal = priorHits.Values.Sum();
            var currentTotal = currentHits.Values.Sum();
            _output.WriteLine($"합계: 과거 {priorTotal} -> 현재 {currentTotal}");

            // 참고: 잎 문장 State나 객체 전체 🟩-🟧 점수는 이 자리에서 움직이지 않는다(위
            // 문서 주석 참고) - 그래서 State가 아니라 앵커 출처를 직접 잰다.
            Assert.True(priorTotal == 0,
                $"과거 판에서 이미 집합 술어 앵커가 {priorTotal}개 잡혔다 - " +
                "③ 사각지대였다는 감사 9회차 전제와 어긋난다.");
            Assert.True(currentTotal == targetLines.Length,
                $"현재 판에서 집합 술어 앵커가 {targetLines.Length}개 중 {currentTotal}개만 잡혔다 - " +
                "조인 ON 리터럴 전부가 닫히지 않았다.");
        }
    }
}
