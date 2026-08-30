# 통합 계획서 루프 재현성 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 통합 배치 계획서 생성 루프에서 회차가 늘면 품질이 반드시 오르도록 만들고, 회차당 입력을 줄인다.

**Architecture:** 최고 후보 상태를 매 회차 롤백 기준으로 삼아 단조성을 구조로 강제한다. 결정적 기계 검사(오류 코드 누락·L1 위반)를 루프 안으로 올려 결함이 발생한 단계에 귀속시키고, 지목되지 않은 단계는 바이트 그대로 동결한다. 섹션 재생성은 백지 재작성에서 패치로 바꾸고, 캐시가 죽어 있는 CLI 제공자에서는 단계별 입력을 좁힌다.

**Tech Stack:** .NET 10.0 · C# · xUnit · NSubstitute · Serilog

**Spec:** `docs/superpowers/specs/2026-08-30-batch-plan-loop-reproducibility-design.md`

## Global Constraints

- **게이트를 낮추지 않는다.** `AiSettings:Critic:ThresholdScore`는 `8`, 5축 AND 판정(`CriticScoreGate.FailedAxes`)은 그대로 둔다.
- **`Full` 모드 프롬프트 접두사는 바이트 하나 바뀌지 않아야 한다.** `AppendSharedStepContext`가 만드는 구간은 단계 호출 간 완전히 동일해야 캐시가 산다.
- **테스트 게이트는 `dotnet test` 실패 0 · 건너뜀 0 · 경고 0이다.** 통과 개수는 게이트로 쓰지 않는다(환경 내에서도 최대 5까지 흔들린다).
- **빌드·테스트는 격리 워크트리에서만 실행한다** (`AGENTS.md` §8). 워크트리에는 코퍼스 재료 **둘**(`output/`·`output.bak-2026-08-22/`)을 모두 심링크한다. 하나만 걸면 다른 테스트가 조용히 꺼진다.
- **검사가 0건을 세면 실패하게 한다.** 재료를 잃고 조용히 통과하는 것이 이 저장소가 이미 겪은 결함이다.
- **커밋 메시지는 한국어 현재형**(`feat: ~한다` / `fix: ~을 고친다`)으로 쓴다. 저장소 이력이 그 형식이다.
- `AGENTS.md`는 바이트 상한이 걸린 문서다. 이 계획은 `AGENTS.md`를 수정하지 않는다.

---

## File Structure

### 새로 만드는 파일

| 파일 | 책임 |
| :--- | :--- |
| `src/ReSet.Core/Services/BatchRunLogReader.cs` | 실행 로그에서 재현성 지표를 기계 추출한다 |
| `src/ReSet.Core/Services/ErrorCodeAttribution.cs` | 누락 오류 코드를 선언 단계로 귀속한다 |
| `src/ReSet.Core/Services/L1ViolationAttribution.cs` | L1 위반을 그 위반이 실린 단계로 귀속한다 |
| `src/ReSet.Core/Services/StepFreezeState.cs` | 단계의 동결·개방 상태와 그 근거를 든다 |
| `src/ReSet.Core/Services/PromptContextScope.cs` | 제공자별 `Full`/`Narrow` 판정과 명세서 좁히기 |
| `tests/ReSet.Core.Tests/BatchRunLogReaderTests.cs` | Task 1 |
| `tests/ReSet.Core.Tests/ErrorCodeAttributionTests.cs` | Task 5 |
| `tests/ReSet.Core.Tests/L1ViolationAttributionTests.cs` | Task 6 |
| `tests/ReSet.Core.Tests/StepFreezeStateTests.cs` | Task 7 |
| `tests/ReSet.Core.Tests/PromptContextScopeTests.cs` | Task 10 |

### 고치는 파일

| 파일 | 무엇을 |
| :--- | :--- |
| `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` | 회귀 롤백 · 예산 분리 · 오라클 배선 · 동결 적용 |
| `src/ReSet.Core/Services/IAiService.cs` | `ReviewResult`에 플래그 둘 추가 · `GenerateBatchStepSectionAsync`에 `previousBody` 추가 |
| `src/ReSet.Core/Services/AiService.cs` | Critic 프롬프트 계약 확장 · 패치 계약 · Narrow 모드 배선 |
| `src/ReSet.Core/Services/StructureRedraftPolicy.cs` | 발동 조건을 둘의 AND로 |
| `src/ReSet.Cli/appsettings.json` | `MaxL1RepairAttempts` · `PromptContextScope` 추가 |
| `src/ReSet.Cli/Program.cs` | 새 설정 두 개를 오케스트레이터에 전달 |
| `tests/ReSet.Core.Tests/StructureRedraftPolicyTests.cs` | 새 조건에 맞춰 갱신 |
| `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs` | 롤백·예산 분리·동결 통합 테스트 추가 |

---

## Task 1: 실행 로그 판독기

**Why:** 기준선을 못 읽으면 나머지를 증명할 수 없다. 이 설계를 쓰는 동안에도 손으로 만든 첫 `grep` 추출기가 명세서 본문의 숫자를 잡아 오염된 값을 냈다.

**Files:**
- Create: `src/ReSet.Core/Services/BatchRunLogReader.cs`
- Test: `tests/ReSet.Core.Tests/BatchRunLogReaderTests.cs`

**Interfaces:**
- Consumes: 없음 (첫 태스크)
- Produces: `BatchRunLogReader.Read(string logText) -> BatchRunMetrics`, 레코드 `AttemptScore`, `BatchRunMetrics`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/BatchRunLogReaderTests.cs`:

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchRunLogReaderTests
    {
        // POQSettleBatch4(2026-08-29) 실측 로그의 형태를 그대로 축약한 픽스처.
        // 회차별 점수 78 -> 76 -> (L1 실패) -> (L1 실패) -> 84 -> 74 를 재현한다.
        private const string Batch4Shape = """
            2026-08-29 14:15:34.321 +09:00 [INF] POQSettleBatch4 - AI 통합 배치 전환 계획 수립 중 [[1차 분석]]...
            2026-08-29 14:17:26.447 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 238908, 캐시 읽기: 0, 출력: 7544, 추론: 미보고
            2026-08-29 14:50:19.405 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 2802
              "ScoreAccuracy": 6,
              "ScoreCrud": 9,
              "ScoreInterface": 7,
              "ScoreException": 7,
              "ScoreReadability": 10
            2026-08-29 14:50:19.552 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 1/6): 결함
            2026-08-29 14:51:46.828 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 251376, 캐시 읽기: 8518, 출력: 11160, 추론: 미보고
            2026-08-29 15:06:38.299 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 1482
              "ScoreAccuracy": 6,
              "ScoreCrud": 8,
              "ScoreInterface": 6,
              "ScoreException": 9,
              "ScoreReadability": 9
            2026-08-29 15:06:38.490 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 2/6): 결함
            2026-08-29 15:06:38.491 +09:00 [INF] POQSettleBatch4 - 재시도가 점수를 개선하지 못해 목차를 다시 설계합니다...
            2026-08-29 15:30:27.712 +09:00 [WRN] [POQSettleBatch4] L1 기계 검증 오류 발견 (시도 3/6):
            2026-08-29 15:51:52.668 +09:00 [WRN] [POQSettleBatch4] L1 기계 검증 오류 발견 (시도 4/6):
            2026-08-29 16:24:53.100 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 3000
              "ScoreAccuracy": 8,
              "ScoreCrud": 9,
              "ScoreInterface": 9,
              "ScoreException": 7,
              "ScoreReadability": 9
            2026-08-29 16:24:53.134 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 5/6): 결함
            2026-08-29 16:38:45.600 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 2500
              "ScoreAccuracy": 7,
              "ScoreCrud": 8,
              "ScoreInterface": 7,
              "ScoreException": 6,
              "ScoreReadability": 9
            2026-08-29 16:38:45.609 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 6/6): 결함
            2026-08-29 16:39:00.199 +09:00 [INF] POQSettleBatch4 - 배치 모드로 인해 통합 계획서가 자동으로 최종 승인되었습니다.
            """;

        [Fact]
        public void Read_ExtractsTrajectoryInOrder()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.Equal(new[] { 78, 76, 84, 74 },
                metrics.Trajectory.Select(a => a.NormalizedScore).ToArray());
        }

        // 이 계획 전체가 이 값을 0으로 만드는 것을 목표로 한다.
        [Fact]
        public void Read_CountsMonotonicityViolations()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            // 76 < 78, 74 < 84. 84 > 78 은 갱신이므로 위반이 아니다.
            Assert.Equal(2, metrics.MonotonicityViolations);
        }

        [Fact]
        public void Read_CountsL1ExhaustedAttempts()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.Equal(2, metrics.L1ExhaustedAttempts);
            Assert.Equal(6, metrics.TotalAttempts);
        }

        [Fact]
        public void Read_SumsTokenUsage()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.Equal(238908 + 251376, metrics.CacheWriteTokens);
            Assert.Equal(0 + 8518, metrics.CacheReadTokens);
            Assert.Equal(7544 + 11160, metrics.OutputTokens);
        }

        [Fact]
        public void Read_MeasuresWallClockFromFirstToLastTimestamp()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            // 14:15:34 -> 16:39:00
            Assert.NotNull(metrics.WallClock);
            Assert.Equal(2, metrics.WallClock!.Value.Hours);
            Assert.Equal(23, metrics.WallClock.Value.Minutes);
        }

        [Fact]
        public void Read_DetectsStructureRedraft()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.True(metrics.StructureRedrafted);
        }

        // 명세서 본문에도 숫자가 있다. 리뷰 응답 앵커 밖의 값을 점수로 읽으면
        // 궤적이 오염된다 - 이 계획을 쓰는 동안 실제로 그 사고가 났다.
        [Fact]
        public void Read_IgnoresScoreLikeNumbersOutsideReviewAnchor()
        {
            var polluted = """
                2026-08-29 10:00:00.000 +09:00 [INF] 명세서 본문
                  "ScoreAccuracy": 10,
                  "ScoreCrud": 10,
                  "ScoreInterface": 10,
                  "ScoreException": 10,
                  "ScoreReadability": 10
                """ + "\n" + Batch4Shape;

            var metrics = BatchRunLogReader.Read(polluted);

            Assert.Equal(4, metrics.Trajectory.Count);
        }

        // 값이 0~10을 벗어나면 그 블록은 점수가 아니다(타임스탬프·오류코드가 섞인 것).
        // 채택하지 않고 버린다 - 거짓 궤적보다 짧은 궤적이 낫다.
        [Fact]
        public void Read_RejectsOutOfRangeScoreBlock()
        {
            var corrupt = """
                2026-08-29 10:00:00.000 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료
                  "ScoreAccuracy": 4,
                  "ScoreCrud": 2026,
                  "ScoreInterface": 10,
                  "ScoreException": 10,
                  "ScoreReadability": 10
                """;

            var metrics = BatchRunLogReader.Read(corrupt);

            Assert.Empty(metrics.Trajectory);
        }

        // 재료가 없으면 0이 아니라 "없음"이다. 0으로 채우면 "캐시를 안 썼다"는
        // 거짓 측정값이 되어 나중에 판정 근거로 쓰인다.
        [Fact]
        public void Read_EmptyLog_ReportsNoWallClock()
        {
            var metrics = BatchRunLogReader.Read(string.Empty);

            Assert.Null(metrics.WallClock);
            Assert.Empty(metrics.Trajectory);
            Assert.Equal(0, metrics.TotalAttempts);
        }
    }
}
```

파일 상단에 `using System.Linq;`를 추가한다.

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~BatchRunLogReaderTests
```

기대: 컴파일 실패 — `BatchRunLogReader`가 존재하지 않는다.

- [ ] **Step 3: 최소 구현을 쓴다**

`src/ReSet.Core/Services/BatchRunLogReader.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>회차 하나의 Critic 점수. ReviewResult와 달리 로그에서 복원된 값만 든다.</summary>
    public sealed record AttemptScore(
        int ScoreAccuracy, int ScoreCrud, int ScoreInterface, int ScoreException, int ScoreReadability)
    {
        public int TotalScore =>
            ScoreAccuracy + ScoreCrud + ScoreInterface + ScoreException + ScoreReadability;

        public int NormalizedScore => (int)Math.Round((TotalScore * 100.0) / 50.0);
    }

    /// <summary>
    /// 실행 한 판의 재현성 지표. WallClock이 nullable인 이유: 재료가 없을 때
    /// 0으로 채우면 "0초에 끝났다"는 거짓 측정값이 남는다.
    /// </summary>
    public sealed record BatchRunMetrics(
        IReadOnlyList<AttemptScore> Trajectory,
        int MonotonicityViolations,
        int L1ExhaustedAttempts,
        int TotalAttempts,
        long CacheWriteTokens,
        long CacheReadTokens,
        long OutputTokens,
        TimeSpan? WallClock,
        bool StructureRedrafted);

    /// <summary>
    /// 실행 로그에서 재현성 지표를 뽑는다.
    ///
    /// 손으로 grep하지 않는 이유: 명세서 본문에도 `"ScoreAccuracy"`와 숫자가 있어서
    /// 앵커 없이 읽으면 궤적이 오염된다. 이 설계를 쓰는 동안 실제로 그 사고가 났고,
    /// 오염된 값으로 "Batch2는 66 -> 80 -> 86"이라는 거짓 궤적을 만들었다.
    ///
    /// 방어는 둘이다. (1) `리뷰 응답 수신 완료` 앵커 뒤에서만 읽는다.
    /// (2) 다섯 값이 모두 0~10이어야 채택한다. 하나라도 벗어나면 그 블록 전체를 버린다 -
    /// 거짓 궤적보다 짧은 궤적이 낫다.
    /// </summary>
    public static class BatchRunLogReader
    {
        private static readonly Regex ReviewAnchor = new(@"리뷰 응답 수신 완료", RegexOptions.Compiled);
        private static readonly Regex ScoreLine = new(
            @"""Score(?<axis>Accuracy|Crud|Interface|Exception|Readability)""\s*:\s*(?<value>\d+)",
            RegexOptions.Compiled);
        private static readonly Regex AttemptLine = new(@"\(시도 (?<n>\d+)/(?<max>\d+)\)", RegexOptions.Compiled);
        private static readonly Regex L1Line = new(@"L1 기계 검증 오류 발견 \(시도 \d+/\d+\)", RegexOptions.Compiled);
        private static readonly Regex UsageLine = new(
            @"캐시 쓰기:\s*(?<w>\d+),\s*캐시 읽기:\s*(?<r>\d+),\s*출력:\s*(?<o>\d+)",
            RegexOptions.Compiled);
        private static readonly Regex TimestampLine = new(
            @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+", RegexOptions.Compiled);
        private static readonly Regex RedraftLine = new(@"목차를 다시 설계합니다", RegexOptions.Compiled);

        public static BatchRunMetrics Read(string? logText)
        {
            var lines = (logText ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            var trajectory = new List<AttemptScore>();
            long cacheWrite = 0, cacheRead = 0, output = 0;
            int l1Exhausted = 0, totalAttempts = 0;
            DateTime? first = null, last = null;
            var redrafted = false;

            var collecting = false;
            var axes = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                var ts = TimestampLine.Match(line);
                if (ts.Success &&
                    DateTime.TryParseExact(ts.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    first ??= parsed;
                    last = parsed;
                }

                if (ReviewAnchor.IsMatch(line))
                {
                    collecting = true;
                    axes.Clear();
                    continue;
                }

                if (collecting)
                {
                    var score = ScoreLine.Match(line);
                    if (score.Success)
                    {
                        var value = int.Parse(score.Groups["value"].Value, CultureInfo.InvariantCulture);
                        if (value < 0 || value > 10)
                        {
                            // 점수가 아니다. 이 블록을 통째로 버린다.
                            collecting = false;
                            axes.Clear();
                        }
                        else
                        {
                            axes[score.Groups["axis"].Value] = value;
                            if (axes.Count == 5)
                            {
                                trajectory.Add(new AttemptScore(
                                    axes["Accuracy"], axes["Crud"], axes["Interface"],
                                    axes["Exception"], axes["Readability"]));
                                collecting = false;
                                axes.Clear();
                            }
                        }

                        continue;
                    }

                    // 점수 줄도 아니고 앵커도 아닌 줄이 타임스탬프로 시작하면 블록이 끝난 것이다.
                    if (ts.Success)
                    {
                        collecting = false;
                        axes.Clear();
                    }
                }

                if (L1Line.IsMatch(line)) l1Exhausted++;
                if (RedraftLine.IsMatch(line)) redrafted = true;

                var attempt = AttemptLine.Match(line);
                if (attempt.Success)
                {
                    totalAttempts = Math.Max(
                        totalAttempts, int.Parse(attempt.Groups["n"].Value, CultureInfo.InvariantCulture));
                }

                var usage = UsageLine.Match(line);
                if (usage.Success)
                {
                    cacheWrite += long.Parse(usage.Groups["w"].Value, CultureInfo.InvariantCulture);
                    cacheRead += long.Parse(usage.Groups["r"].Value, CultureInfo.InvariantCulture);
                    output += long.Parse(usage.Groups["o"].Value, CultureInfo.InvariantCulture);
                }
            }

            return new BatchRunMetrics(
                trajectory,
                CountMonotonicityViolations(trajectory),
                l1Exhausted,
                totalAttempts,
                cacheWrite,
                cacheRead,
                output,
                first.HasValue && last.HasValue ? last.Value - first.Value : null,
                redrafted);
        }

        /// <summary>
        /// 직전까지의 최고점보다 낮은 회차의 수. 동점은 위반이 아니다 —
        /// BestAttempt.TryRecord가 동점을 교체하지 않는 것과 같은 규칙이다.
        /// </summary>
        private static int CountMonotonicityViolations(IReadOnlyList<AttemptScore> trajectory)
        {
            var violations = 0;
            var runningMax = int.MinValue;

            foreach (var attempt in trajectory)
            {
                if (runningMax != int.MinValue && attempt.NormalizedScore < runningMax)
                {
                    violations++;
                }

                runningMax = Math.Max(runningMax, attempt.NormalizedScore);
            }

            return violations;
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~BatchRunLogReaderTests
```

기대: 9개 전부 PASS.

- [ ] **Step 5: 실제 기준선 로그로 확인한다**

임시 콘솔 코드를 쓰지 말고, 아래 임시 테스트를 추가해 실측값을 눈으로 확인한 뒤 **지운다**. 로그 파일은 `.gitignore` 대상이라 저장소에 없으므로 이 테스트를 남기면 다른 환경에서 실패한다.

```csharp
[Fact(Skip = "기준선 확인용. 확인 후 지운다.")]
public void Read_Batch4BaselineLog()
{
    var path = "/Users/payletter/git-root/ReSet/output.bak-stage4-control-20260828/logs-batch4/reset-20260829.log";
    var metrics = BatchRunLogReader.Read(File.ReadAllText(path));

    // 기대: 궤적 [78,76,84,74] · 위반 2 · L1 소진 2 · 총 6회
    //       캐시 쓰기 24,065,539 · 읽기 775,702 · 출력 2,054,632 · 벽시계 2:23:26
    Assert.Equal(new[] { 78, 76, 84, 74 }, metrics.Trajectory.Select(a => a.NormalizedScore).ToArray());
    Assert.Equal(2, metrics.MonotonicityViolations);
    Assert.Equal(24_065_539, metrics.CacheWriteTokens);
}
```

`Skip`을 잠시 떼고 돌려 값이 맞는지 확인한 뒤, 테스트를 **삭제**하고 커밋한다.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/BatchRunLogReader.cs tests/ReSet.Core.Tests/BatchRunLogReaderTests.cs
git commit -m "feat: 실행 로그에서 재현성 지표를 기계로 뽑는다 — 손 grep은 궤적을 오염시킨다"
```

---

## Task 2: 회귀 롤백 — 최고 후보 위에서만 다음 회차를 시작한다

**Why:** 실측 궤적이 `78 → 76 → … → 84 → 74`다. 회차가 늘어도 품질이 오른다는 보장이 없다. 이 태스크 하나로 그 보장이 생긴다.

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:2081-2098` (후보 등록 블록)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: 기존 `BestAttempt.TryRecord`, `AdoptedGenerationState`, `RestoreAdoptedGenerationState`
- Produces: 없음 (오케스트레이터 내부 동작 변경)

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`VerificationPipelineOrchestratorTests.cs` 끝(마지막 `}` 두 개 앞)에 추가한다:

```csharp
        /// <summary>
        /// 실측(POQSettleBatch4 2026-08-29): 궤적이 78 -> 76 -> 84 -> 74였다.
        /// 회차 n이 최고점을 못 넘으면 그 회차 산출물을 버리고 최고 후보 상태로
        /// 되감아야 한다 - 그래야 다음 회차가 76점짜리가 아니라 78점짜리 위에서 시작한다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipelineAsync_ScoreRegression_RollsBackToBestCandidate()
        {
            var specs = new List<(string, string)> { ("dbo.USP_Test1", "내용") };
            var header = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";
            var planA = header + "\nA";   // 78점
            var planB = header + "\nB";   // 76점 - 회귀
            var planC = header + "\nC";   // 84점

            // 총 3회를 돌 수 있도록 maxL2Attempts를 2로 준다(총 시도 = 1 + 2).
            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "2", "gpt-4");

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<IReadOnlyList<StepInterface>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = planA }),
                    _ => Task.FromResult(new AiResult { Content = planB }),
                    _ => Task.FromResult(new AiResult { Content = planC }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), planA, "Job_Test", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = true, FeedbackComment = "A 결함",
                    ScoreAccuracy = 6, ScoreCrud = 9, ScoreInterface = 7, ScoreException = 7, ScoreReadability = 10
                }));
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), planB, "Job_Test", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = true, FeedbackComment = "B 결함",
                    ScoreAccuracy = 6, ScoreCrud = 8, ScoreInterface = 6, ScoreException = 9, ScoreReadability = 9
                }));
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), planC, "Job_Test", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = true, FeedbackComment = "C 결함",
                    ScoreAccuracy = 8, ScoreCrud = 9, ScoreInterface = 9, ScoreException = 7, ScoreReadability = 9
                }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 최고점 84(planC)가 채택된다.
            Assert.NotNull(result.Review);
            Assert.Equal(84, result.Review!.NormalizedScore);

            // 회귀한 2회차에서 롤백이 화면에 고지되어야 한다.
            _userInteraction.Received().NotifyStatus(
                Arg.Is<string>(s => s.Contains("최고 후보") && s.Contains("되돌립니다")));
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~ScoreRegression_RollsBackToBestCandidate
```

기대: FAIL — `NotifyStatus`가 롤백 메시지로 불리지 않는다.

- [ ] **Step 3: 롤백을 구현한다**

`VerificationPipelineOrchestrator.cs`의 후보 등록 블록(`:2081` 근방, `bool improvedThisAttempt = false;`로 시작하는 곳)을 아래로 바꾼다:

```csharp
                // 불합격 여부와 무관하게 후보로 등록한다.
                // 반환값은 "이번 회차가 최고점을 갱신했는가"이며, 그것이 곧 정체 신호다.
                bool improvedThisAttempt = false;
                if (reviewSuccess && l2Result != null)
                {
                    improvedThisAttempt = bestAttempt.TryRecord(attempt, consolidatedPlan, l2Result, finalAiResult);
                    if (improvedThisAttempt)
                    {
                        // 후보가 교체되는 바로 그 자리에서 그 후보를 만든 상태를
                        // 통째로 붙잡는다. 다른 곳에서 갱신하면 어긋나는 순간이 생긴다.
                        adoptedState = new AdoptedGenerationState(
                            currentPlanStructure,
                            lastSkeleton,
                            lastSkeletonResult,
                            lastStepSections == null ? null : new Dictionary<string, string>(lastStepSections),
                            new Dictionary<string, StepDefect>(stepFloorViolations));
                    }
                    else if (bestAttempt.Current != null)
                    {
                        // 회귀 롤백. 이번 회차는 최고 후보보다 나쁘므로 산출물을 버리고
                        // 최고 후보 상태로 되감는다 - 다음 회차가 나쁜 문서 위에서
                        // 시작하면 회차를 늘려도 품질이 오른다는 보장이 없다.
                        // 실측(POQSettleBatch4): 78 -> 76 -> 84 -> 74. 마지막이 첫 회차보다 낮았다.
                        //
                        // 피드백 누적(feedbackHistory)은 되돌리지 않는다. 버린 회차에서
                        // 얻은 지적도 정보이고, 그것까지 버리면 같은 결함을 다시 만든다.
                        currentPlanStructure = adoptedState.PlanStructure ?? currentPlanStructure;
                        RestoreAdoptedGenerationState(
                            adoptedState, out lastSkeleton, out lastSkeletonResult, out lastStepSections, out stepFloorViolations);

                        // currentSteps는 RestoreAdoptedGenerationState가 되돌리지 않는다
                        // (살아있는 루프 변수라서다 - :1827의 주석 참조). 여기서 채택된
                        // 목차 하나에서 다시 파싱하지 않으면 Sections는 채택본인데
                        // Steps는 폐기본을 서술하는 모순이 생긴다.
                        currentSteps = BatchStepPlanParser.TryParse(currentPlanStructure);

                        _userInteraction.NotifyStatus(
                            $"[yellow]{jobName}[/] - {attempt}차 시도({l2Result.NormalizedScore}/100)가 " +
                            $"최고 후보({bestAttempt.Current.AttemptNumber}차, {bestAttempt.Current.Review.NormalizedScore}/100)를 " +
                            "넘지 못해 최고 후보 상태로 되돌립니다.");
                    }
                }
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~VerificationPipelineOrchestratorTests
```

기대: 새 테스트 PASS, 기존 테스트 전부 PASS.

- [ ] **Step 5: 전체 테스트를 돌린다**

```bash
dotnet test
```

기대: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: 회귀한 회차를 최고 후보로 되감는다 — 회차가 늘면 품질이 오르게 한다"
```

---

## Task 3: 예산 분리 — L1 실패가 채점 회차를 먹지 않게 한다

**Why:** 실측 6회 중 2회(33%)가 채점도 못 받고 예산을 소진했다. `MaxL2Attempts` 주석 자체가 이 사실을 인정하고 있다.

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (생성자 + L1 실패 분기 `:2005-2050`)
- Modify: `src/ReSet.Core/Services/MechanicalValidator.cs` (`ViolationLexemes` 추가)
- Modify: `src/ReSet.Cli/appsettings.json`
- Modify: `src/ReSet.Cli/Program.cs`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`
- Test: `tests/ReSet.Core.Tests/L1ViolationAttributionTests.cs` (`ViolationLexemes` 테스트 셋 추가)

**Interfaces:**
- Consumes: Task 2의 롤백 (같은 루프) · **Task 6의 `L1ViolationAttribution.AttributeByLexeme`**
- Produces: 생성자 파라미터 `int maxL1RepairAttempts = 2` (기존 파라미터 **뒤에** 붙인다 — 앞에 끼우면 위치 인자로 부르는 기존 호출부가 조용히 깨진다) · `MechanicalValidator.ViolationLexemes(DetailedError) -> IReadOnlyList<string>`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        /// <summary>
        /// 실측(POQSettleBatch4): 6회 중 2회가 L1에서 소진되어 채점을 못 받았다.
        /// L1 위반은 결정적 결함이므로 자기 예산으로 처리하고 채점 예산을 건드리면 안 된다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipelineAsync_L1Failure_DoesNotConsumeScoringBudget()
        {
            var specs = new List<(string, string)> { ("dbo.USP_Test1", "내용") };
            var header = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";
            var badPlan = "L1을 통과하지 못하는 문서";
            var goodPlan = header + "\nOK";

            // 채점 예산 1회(총 시도 2회) + L1 수리 예산 2회.
            // L1이 예산을 공유하면 badPlan 두 번으로 소진되어 goodPlan에 도달하지 못한다.
            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "1", "gpt-4",
                maxL1RepairAttempts: 2);

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<IReadOnlyList<StepInterface>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = badPlan }),
                    _ => Task.FromResult(new AiResult { Content = badPlan }),
                    _ => Task.FromResult(new AiResult { Content = goodPlan }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), goodPlan, "Job_Test", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = false,
                    ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
                }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // L1을 두 번 수리하고도 채점 회차가 남아 통과에 도달한다.
            Assert.Equal(VerificationOutcome.Passed, result.Outcome);
            _userInteraction.Received(2).NotifyL1Errors("Job_Test", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<List<string>>());
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~L1Failure_DoesNotConsumeScoringBudget
```

기대: 컴파일 실패 — `maxL1RepairAttempts` 파라미터가 없다.

- [ ] **Step 3: 생성자에 예산을 추가한다**

`VerificationPipelineOrchestrator.cs` 생성자(`:32`)의 **마지막 파라미터 뒤**에 추가한다:

```csharp
            int criticScoreThreshold = 8,
            int stepConcurrency = 1,     // 기본값 1 = 종전 순차. 실사용 값은 appsettings.json이 4로 넘긴다.
            int maxL1RepairAttempts = 2) // L1 위반 수리 전용 예산. 채점 예산과 분리한다.
```

필드와 대입을 추가한다:

```csharp
        /// <summary>
        /// L1 위반 수리 시도의 상한. 채점 예산(_maxAttempts)과 분리돼 있다.
        ///
        /// 나눈 이유: 실측(POQSettleBatch4 2026-08-29)에서 6회 중 2회가 L1에서 소진되어
        /// 채점조차 받지 못했다. L1 위반은 결정적 결함이라 자리를 특정할 수 있고, 그 자리만
        /// 고치면 되는데도 채점 회차를 통째로 먹었다.
        /// </summary>
        private readonly int _maxL1RepairAttempts;
```

```csharp
            _maxL1RepairAttempts = Math.Max(1, maxL1RepairAttempts);
```

- [ ] **Step 4: L1 실패 분기가 별도 카운터를 쓰게 한다**

루프 앞(`int attempt = 1;` 옆)에 카운터를 둔다:

```csharp
            int attempt = 1;
            int l1RepairAttempt = 0;
            var bestAttempt = new BestAttempt();
```

L1 실패 분기(`if (!l1Result.IsValid)`)의 `canRetry` 판정을 바꾼다:

```csharp
                if (!l1Result.IsValid)
                {
                    _userInteraction.NotifyL1Errors(jobName, attempt, _maxAttempts, l1Result.Errors);

                    // ── 순서가 이 블록의 전부다 ──
                    // (1) 귀속을 **먼저** 시도해 pendingDefectiveSteps 를 채운다 (아래 블록)
                    // (2) 그 결과로 예산을 고른다
                    //
                    // 뒤집으면 회귀가 난다. 귀속 불가한 문서 전역 위반(헤더 누락·Mermaid
                    // 문법 오류)이 L1 예산(기본 2)만 받게 되어 개정 전 채점 예산(기본 6)보다
                    // **적어지고**, 이 태스크가 없애려는 L1 소진을 오히려 더 쉽게 만든다.
                    // 리뷰가 실제로 이 회귀를 잡았다(초판이 그렇게 쓰여 있었다).
                    {
                        // 위반을 단계에 귀속해 그 단계만 다시 뽑는다. 실측(POQSettleBatch4
                        // 시도 3)의 L1 실패는 `END TRY` 하나였는데 문서 전체를 다시 만들었다.
                        //
                        // 귀속하지 못하면 pendingDefectiveSteps가 비고, 그러면 종전대로
                        // 전량 재생성이 된다 - 억지로 아무 단계에나 붙이면 멀쩡한 단계를
                        // 다시 쓰게 되어 회귀 롤백이 막으려는 회귀를 다시 들인다.
                        //
                        // 귀속은 두 갈래다. 위반 유형 자체가 자리를 아는 것은 그 규칙으로
                        // 바로 귀속하고, 나머지만 어휘 검색으로 넘긴다.
                        //
                        // 나누는 이유: BatchRunRowNeverCreated와 LegacyReturnCodeNeverBound는
                        // **없는 것이 위반**이라 문서에서 어휘를 찾을 수 없다. 어휘 검색에만
                        // 맡기면 영원히 귀속 실패로 떨어져 전량 재생성을 부른다 - 설계서
                        // §3-5(c) 표가 이 둘을 하드 귀속으로 규정한 이유가 그것이다.
                        pendingDefectiveSteps.Clear();

                        void AddOwner(string? code)
                        {
                            if (!string.IsNullOrEmpty(code) &&
                                !pendingDefectiveSteps.Contains(code, StringComparer.OrdinalIgnoreCase))
                            {
                                pendingDefectiveSteps.Add(code);
                            }
                        }

                        foreach (var detail in l1Result.DetailedErrors)
                        {
                            switch (detail.Type)
                            {
                                case ErrorType.BatchRunRowNeverCreated:
                                    // RunId 발급 계약은 단계 목록의 첫 단계가 진다.
                                    if (currentSteps is { Count: > 0 }) AddOwner(currentSteps[0].Code);
                                    break;

                                case ErrorType.LegacyReturnCodeNeverBound:
                                    // 이 값의 거처는 오류 코드를 선언한 단계들이다.
                                    foreach (var step in currentSteps ?? Enumerable.Empty<BatchStepPlan>())
                                    {
                                        if (step.ErrorCodes.Count > 0) AddOwner(step.Code);
                                    }
                                    break;

                                default:
                                    foreach (var lexeme in MechanicalValidator.ViolationLexemes(detail))
                                    {
                                        AddOwner(L1ViolationAttribution.AttributeByLexeme(
                                            consolidatedPlan, lexeme, currentSteps));
                                    }
                                    break;
                            }
                        }

                    }   // ← 귀속 블록 끝. 여기서부터 (2) 예산 선택이다.

                    // 지목 재생성(귀속 성공)은 L1 자기 예산을 쓰고 채점 예산을 건드리지
                    // 않는다. 전량 재생성(귀속 실패)은 채점 대상 문서를 새로 만드는
                    // 일이므로 채점 예산을 쓴다 - 예산 분리 이전(단일 _maxAttempts)
                    // 동작과 같아서 회귀가 아니다.
                    bool attributedToSteps = pendingDefectiveSteps.Count > 0;
                    bool canRetry = attributedToSteps
                        ? l1RepairAttempt + 1 <= _maxL1RepairAttempts
                        : _maxAttempts == -1 || attempt < _maxAttempts;

                    if (canRetry)
                    {
                        if (attributedToSteps)
                        {
                            l1RepairAttempt++;
                            _userInteraction.NotifyStatus(
                                $"[yellow]{jobName}[/] - L1 위반을 {string.Join(", ", pendingDefectiveSteps)} 단계로 " +
                                "좁혀 그 단계만 다시 만듭니다.");
                        }
                        else
                        {
                            attempt++;
                        }

                        feedbackLog = CriticFeedbackLog.ComposeAfterL1Failure(l1Result.SuggestedPromptFix, feedbackHistory);
                        continue;
                    }
```

**두 경로가 각자의 카운터만 올린다.** 귀속 성공은 `l1RepairAttempt`, 실패는 `attempt`.
어느 쪽도 상대의 카운터를 건드리지 않는다.

L2 결함 분기의 `canRetry`는 그대로 둔다(`_maxAttempts == -1 || attempt < _maxAttempts`).

**테스트로 두 방향을 모두 고정하라.** 한쪽만 재면 이 회귀가 다시 들어온다 — 초판의
테스트가 「L1 실패가 채점 예산을 안 먹는다」만 재고 「귀속 실패 시엔 먹어야 한다」를
안 재서, 리뷰가 별도로 잡을 때까지 결함이 통과했다.

`MechanicalValidator`에 위반 어휘 추출기를 추가한다. `ValidationResult.DetailedErrors`가 이미 유형별 오류를 들고 있으므로 거기서 뽑는다:

```csharp
        /// <summary>
        /// L1 위반 하나에서 문서를 훑을 어휘를 뽑는다. L1ViolationAttribution이
        /// 이것으로 위반이 실린 단계를 찾는다.
        ///
        /// 백틱으로 감싼 토큰만 쓴다 - 검사 메시지는 규칙 설명과 어휘를 함께 싣는데,
        /// 산문까지 문서에서 찾으면 아무 단계에나 걸린다. 어휘가 없는 메시지는
        /// 귀속 대상이 아니다(문서 전역 위반이다).
        ///
        /// ValidationResult 전체가 아니라 DetailedError 하나를 받는 이유: 호출부가
        /// 위반 유형별로 다른 귀속 규칙을 쓴다. 전체를 받으면 유형이 뭉개져
        /// 하드 귀속 대상과 어휘 검색 대상을 가를 수 없다.
        /// </summary>
        public static IReadOnlyList<string> ViolationLexemes(DetailedError error)
        {
            var lexemes = new List<string>();
            if (error == null) return lexemes;

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(
                    error.Message ?? string.Empty, @"`(?<token>[^`\n]{2,80})`"))
            {
                var token = match.Groups["token"].Value.Trim();
                if (token.Length > 0 && !lexemes.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    lexemes.Add(token);
                }
            }

            return lexemes;
        }
```

**`DetailedError`에 유형이 이미 있다.** `ErrorType`은 `BatchRunRowNeverCreated` ·
`LegacyReturnCodeNeverBound` · `SqlSideControlFlow`를 포함해 37개 값을 든다
(`MechanicalValidator.cs`의 `ErrorType` enum). 문자열을 뒤져 유형을 추정하지 마라 —
구조화 신호가 이미 있다.

**주의**: `l1Result.DetailedErrors`가 비고 `Errors`만 채워지는 검사가 있는지 확인하라.
있다면 그 검사들은 위 `switch`의 `default`에도 닿지 못해 귀속이 통째로 건너뛰어진다.
그 경우 `DetailedErrors`가 빈 위반은 어휘 검색으로 넘기는 폴백을 두되, **폴백이 돌았다는
사실을 로그에 남겨라** — 조용히 넘어가면 어느 검사가 구조화 신호를 안 내는지 아무도 모른다.

이 메서드의 테스트를 함께 쓴다:

```csharp
        // 실측(POQSettleBatch4 시도 3): 규칙 3-1 위반 메시지가 어휘를 백틱으로 싣는다 -
        // "(발화 1건 · 어휘: `END TRY` · ...)". 산문까지 문서에서 찾으면 아무 단계에나 걸린다.
        [Fact]
        public void ViolationLexemes_ExtractsBacktickedTokensOnly()
        {
            var result = new ValidationResult
            {
                IsValid = false,
                Errors = { "계획서의 코드 블록에서 SQL 문장이 자기 실행 결과를 보고 분기합니다. `END TRY` 를 쓰지 마십시오." }
            };

            Assert.Equal(new[] { "END TRY" }, MechanicalValidator.ViolationLexemes(result));
        }

        [Fact]
        public void ViolationLexemes_WithoutBackticks_ReturnsEmpty()
        {
            var result = new ValidationResult
            {
                IsValid = false,
                Errors = { "문서 전역에 문제가 있습니다." }
            };

            Assert.Empty(MechanicalValidator.ViolationLexemes(result));
        }

        [Fact]
        public void ViolationLexemes_Deduplicates()
        {
            var result = new ValidationResult
            {
                IsValid = false,
                Errors = { "`END TRY` 금지", "`END TRY` 를 다시 지적한다" }
            };

            Assert.Single(MechanicalValidator.ViolationLexemes(result));
        }
```

**이 배선 때문에 Task 3은 Task 6에 의존한다.** Task 6이 먼저 병합돼 있어야 `L1ViolationAttribution`을 부를 수 있다.

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~VerificationPipelineOrchestratorTests
```

기대: 전부 PASS.

- [ ] **Step 6: 설정과 배선을 추가한다**

`src/ReSet.Cli/appsettings.json`의 `MaxTotalAttempts` 줄 다음에 추가한다:

```jsonc
    "MaxL1RepairAttempts": 2,          // [통합 계획서] L1 기계 검증 위반을 수리하는 시도의 상한. MaxL2Attempts(채점 예산)와 분리돼 있습니다. 실측(POQSettleBatch4 2026-08-29)에서 6회 중 2회가 L1에서 소진되어 채점을 못 받았는데, L1 위반은 자리를 특정할 수 있는 결정적 결함이라 채점 회차를 먹을 이유가 없습니다. (1 이상의 정수, 기본 2)
```

`src/ReSet.Cli/Program.cs`에서 `VerificationPipelineOrchestrator`를 만드는 자리를 찾아(`stepConcurrency`를 넘기는 곳) 인자를 추가한다:

```csharp
    maxL1RepairAttempts: configuration.GetValue<int?>("AiSettings:MaxL1RepairAttempts") ?? 2,
```

- [ ] **Step 7: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs src/ReSet.Cli/appsettings.json src/ReSet.Cli/Program.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: L1 수리 예산을 채점 예산에서 분리한다 — 6회 중 2회가 채점 없이 사라졌다"
```

---

## Task 4: 오류 코드 누락 검사를 루프 안으로 올린다

**Why:** `MechanicalValidator.FindMissingErrorCodes`는 원본 오류 코드 보존을 결정적으로 판정하는데, 루프가 **끝난 뒤** 배너로만 나간다(현행 호출부는 `VerificationPipelineOrchestrator.cs:2678` 하나뿐이다). 그런데 예외 축(= 오류 코드 보존)은 미달 5편 중 3편의 유일한 불합격 사유다.

**혼동하지 말 것.** `d9a455e1`(2026-08-30)이 넣은 `CheckLegacyStepErrorCodeInvention`은 **다른 검사**다 — `ValidateBatchStep`(단계 단위, 이미 루프 안)에서 돌고 방향이 역방향이다(*명세에 없는 코드를 실었는가* = 발명 검출). 이 태스크가 올리는 것은 순방향(*명세가 정의한 코드가 문서 어디에도 없는가* = 누락 검출)이고, 두 검사는 상보적이다.

그 커밋이 「96.9% 거짓 고발로 접혔다」고 적은 순방향은 또 다른 것이다 — 「목차가 **선언한** 코드가 그 단계 본문에 대입되었는가」. `FindMissingErrorCodes`는 목차를 전혀 읽지 않고 명세서만 오라클로 삼으며, 그 메서드 주석이 「후자에 걸리면 조건 없이 진짜 누락이다」라고 적는다. 셋을 혼동하면 살아 있는 검사를 접힌 것으로 오인해 이 태스크를 통째로 건너뛰게 된다.

**Files:**
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (L1 블록 + `AttachPipelineBanners`)
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: `MechanicalValidator.FindMissingErrorCodes(string documentMarkdown, IReadOnlyDictionary<string, IReadOnlyList<string>> codesByProcedure) -> IReadOnlyDictionary<string, IReadOnlyList<string>>`, `SpecReturnCodeExtractor.Extract(specs)`
- Produces: `AttachPipelineBanners`가 `IReadOnlyDictionary<string, IReadOnlyList<string>>? precomputedMissingCodes` 파라미터를 받는다

- [ ] **Step 1: 실패하는 테스트를 쓴다**

```csharp
        /// <summary>
        /// 실측: 품질 미달 5편 중 3편의 유일한 불합격 사유가 예외 축(= 원본 오류 코드
        /// 보존)이었다. 기계가 결정적으로 판정할 수 있는 것을 확률적인 Critic에게 맡기고,
        /// 검사 결과는 루프가 끝난 뒤 배너로만 나갔다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipelineAsync_MissingErrorCode_FailsL1AndRetries()
        {
            var header = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";
            // SpecReturnCodeExtractor가 읽는 형태의 명세서. -9010을 원본 반환 코드로 선언한다.
            var specs = new List<(string, string)>
            {
                ("dbo.USP_Test1", "## 개요\n\n## 반환 코드\n\n| 코드 | 의미 |\n| :--- | :--- |\n| -9010 | 예약 블록 |\n")
            };
            var planWithoutCode = header + "\n오류 코드를 옮기지 않은 본문";
            var planWithCode = header + "\n실패 시 -9010을 LegacyReturnCode에 기록한다";

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "1", "gpt-4",
                maxL1RepairAttempts: 2);

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<IReadOnlyList<StepInterface>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new AiResult { Content = planWithoutCode }),
                    _ => Task.FromResult(new AiResult { Content = planWithCode }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), "Job_Test", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = false,
                    ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
                }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 1회차는 오류 코드 누락으로 L1에서 반려되고, 2회차가 채택된다.
            _userInteraction.Received().NotifyL1Errors(
                "Job_Test", Arg.Any<int>(), Arg.Any<int>(),
                Arg.Is<List<string>>(errors => errors.Any(e => e.Contains("-9010"))));
            Assert.Contains("-9010", result.Plan);
        }
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~MissingErrorCode_FailsL1AndRetries
```

기대: FAIL — 누락이 L1을 떨어뜨리지 않는다.

- [ ] **Step 3: L1 블록에서 검사를 돌린다**

`VerificationPipelineOrchestrator.cs`의 L1 블록(`var l1Result = _validator.ValidateConsolidated(consolidatedPlan);` 바로 뒤)에 추가한다. `specReturnCodes`는 루프 밖에서 이미 계산돼 있다(`:1818` 근방).

```csharp
                // L1: 기계적 무결성 검사
                var l1Result = _validator.ValidateConsolidated(consolidatedPlan);
                consolidatedPlan = l1Result.CleansedMarkdown ?? consolidatedPlan;

                // 원본 오류 코드 누락은 결정적으로 판정된다. 루프 밖 배너로만 내보내면
                // 재시도에 한 번도 먹이지 못한다 - 실측에서 이 축이 미달 5편 중 3편의
                // 유일한 불합격 사유였다.
                //
                // 계산 결과는 아래 AttachPipelineBanners로 넘긴다. 같은 사실을 두 곳에서
                // 각자 계산하면 갈라진다.
                missingErrorCodes = MechanicalValidator.FindMissingErrorCodes(consolidatedPlan, specReturnCodes);
                if (missingErrorCodes.Count > 0)
                {
                    foreach (var (procedure, codes) in missingErrorCodes)
                    {
                        l1Result.Errors.Add(
                            $"원본 프로시저 `{procedure}`의 반환 코드 {string.Join(", ", codes)}이(가) " +
                            "계획서 어디에도 없습니다. 레거시 호출자가 읽던 코드가 사라지므로, " +
                            "해당 단계 본문에 원본 코드를 그대로 실으십시오.");
                    }

                    l1Result.IsValid = false;
                }

                if (!l1Result.IsValid)
```

루프 앞(`int l1RepairAttempt = 0;` 옆)에 변수를 선언한다:

```csharp
            IReadOnlyDictionary<string, IReadOnlyList<string>> missingErrorCodes =
                new Dictionary<string, IReadOnlyList<string>>();
```

- [ ] **Step 4: `AttachPipelineBanners`가 계산을 재사용하게 한다**

`AttachPipelineBanners`의 시그니처에 파라미터를 추가한다:

```csharp
        private (string Plan, VerificationCoverage Coverage) AttachPipelineBanners(
            string consolidatedPlan,
            string documentBody,
            IReadOnlyDictionary<string, StepDefect> stepFloorViolations,
            IReadOnlyList<BatchStepPlan>? adoptedSteps,
            System.Collections.Generic.List<(string FileName, string Content)> specs,
            string jobName,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? precomputedMissingCodes)
```

메서드 안에서 `FindMissingErrorCodes`를 부르던 자리를 바꾼다:

```csharp
                // 루프가 이미 계산했으면 그 값을 쓴다. 같은 사실을 두 번 계산하면
                // 한쪽만 고쳐지는 사고가 난다 - 이 저장소가 이미 겪었다.
                var missingCodes = precomputedMissingCodes
                    ?? MechanicalValidator.FindMissingErrorCodes(documentBody, specReturnCodes);
```

호출부에 인자를 넘긴다:

```csharp
            (consolidatedPlan, coverage) = AttachPipelineBanners(
                consolidatedPlan, documentBodyForChecks, stepFloorViolations, adoptedSteps, specs, jobName,
                missingErrorCodes);
```

- [ ] **Step 5: 테스트가 통과하는지 확인한다**

```bash
dotnet test
```

기대: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: 오류 코드 누락 검사를 루프 안으로 올린다 — 미달 5편 중 3편의 유일한 사유였다"
```

---

## Task 5: 누락 오류 코드를 선언 단계로 귀속한다

**Why:** Task 4는 누락을 잡지만 문서 전체를 다시 만든다. `BatchStepPlan.ErrorCodes`가 단계별 코드를 이미 들고 있으므로, 그 단계만 다시 뽑으면 된다.

**Files:**
- Create: `src/ReSet.Core/Services/ErrorCodeAttribution.cs`
- Test: `tests/ReSet.Core.Tests/ErrorCodeAttributionTests.cs`

**Interfaces:**
- Consumes: `BatchStepPlan(string Code, string Name, IReadOnlyList<string> LegacyProcedures, IReadOnlyList<string> TargetTables, IReadOnlyList<string> ErrorCodes, bool Chunkable, IReadOnlyList<string> SchemaTables)`
- Produces: `ErrorCodeAttribution.Attribute(IReadOnlyDictionary<string, IReadOnlyList<string>> missingByProcedure, IReadOnlyList<BatchStepPlan>? steps) -> ErrorCodeAttributionResult(IReadOnlyList<string> StepCodes, bool HasUnattributed)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/ErrorCodeAttributionTests.cs`:

```csharp
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class ErrorCodeAttributionTests
    {
        private static BatchStepPlan Step(string code, params string[] errorCodes) =>
            new(code, $"{code} 단계",
                LegacyProcedures: new[] { $"dbo.UP_{code}" },
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: errorCodes,
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        [Fact]
        public void MissingCode_IsAttributedToTheStepThatDeclaredIt()
        {
            var steps = new[] { Step("S01", "-9010"), Step("S02", "-9140") };
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_S02"] = new[] { "-9140" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps);

            Assert.Equal(new[] { "S02" }, result.StepCodes);
            Assert.False(result.HasUnattributed);
        }

        // 어느 쪽이 빠뜨렸는지 모른다. 좁히지 않고 둘 다 연다 -
        // 잘못 좁히면 결함이 남은 단계가 동결된다.
        [Fact]
        public void CodeDeclaredByTwoSteps_OpensBoth()
        {
            var steps = new[] { Step("S05", "-9010"), Step("S06", "-9010") };
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_S05"] = new[] { "-9010" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps);

            Assert.Equal(new[] { "S05", "S06" }, result.StepCodes);
        }

        // 어느 단계도 선언하지 않은 코드가 누락됐다면 목차 결함이다.
        // 아무 단계나 골라 붙이면 멀쩡한 단계를 다시 쓰게 된다.
        [Fact]
        public void CodeDeclaredByNoStep_IsReportedAsUnattributed()
        {
            var steps = new[] { Step("S01", "-9010") };
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_Other"] = new[] { "-4000" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps);

            Assert.Empty(result.StepCodes);
            Assert.True(result.HasUnattributed);
        }

        // 목차가 없으면 귀속할 좌표 자체가 없다. 조용히 빈 목록을 내면
        // "고칠 단계가 없다"로 읽혀 누락이 사라진다.
        [Fact]
        public void NullSteps_ReportsUnattributed()
        {
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_S01"] = new[] { "-9010" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps: null);

            Assert.Empty(result.StepCodes);
            Assert.True(result.HasUnattributed);
        }

        [Fact]
        public void NoMissingCodes_AttributesNothing()
        {
            var steps = new[] { Step("S01", "-9010") };

            var result = ErrorCodeAttribution.Attribute(
                new Dictionary<string, IReadOnlyList<string>>(), steps);

            Assert.Empty(result.StepCodes);
            Assert.False(result.HasUnattributed);
        }

        // 코드 표기는 공백과 대소문자로 갈린다. 정규화하지 않으면
        // 선언된 코드를 못 찾아 전부 미귀속이 된다.
        [Fact]
        public void CodeMatching_IgnoresSurroundingWhitespace()
        {
            var steps = new[] { Step("S01", " -9010 ") };
            var missing = new Dictionary<string, IReadOnlyList<string>>
            {
                ["dbo.UP_S01"] = new[] { "-9010" }
            };

            var result = ErrorCodeAttribution.Attribute(missing, steps);

            Assert.Equal(new[] { "S01" }, result.StepCodes);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~ErrorCodeAttributionTests
```

기대: 컴파일 실패 — `ErrorCodeAttribution`이 없다.

- [ ] **Step 3: 구현한다**

`src/ReSet.Core/Services/ErrorCodeAttribution.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 귀속 결과. StepCodes가 비고 HasUnattributed가 참이면 "고칠 단계가 없다"가 아니라
    /// "어디를 고쳐야 할지 모른다"는 뜻이다. 둘을 한 필드로 겸하면 후자가 전자로 읽혀
    /// 누락이 조용히 사라진다.
    /// </summary>
    public sealed record ErrorCodeAttributionResult(
        IReadOnlyList<string> StepCodes,
        bool HasUnattributed);

    /// <summary>
    /// 문서에서 빠진 원본 오류 코드를 그 코드를 선언한 단계로 되돌린다.
    ///
    /// 이 클래스가 존재하는 이유: 누락 자체는 MechanicalValidator가 결정적으로 잡지만,
    /// 그 결과가 "문서 어딘가"라서 문서 전체를 다시 만들게 했다. 목차(BatchStepPlan)는
    /// 단계별 ErrorCodes를 이미 들고 있으므로 좌표를 복원할 수 있다.
    ///
    /// 귀속하지 못하는 것을 억지로 붙이지 않는다. 잘못 귀속하면 멀쩡한 단계를 다시 쓰게
    /// 되어, 회귀 롤백이 막으려는 회귀를 다시 들인다.
    /// </summary>
    public static class ErrorCodeAttribution
    {
        public static ErrorCodeAttributionResult Attribute(
            IReadOnlyDictionary<string, IReadOnlyList<string>>? missingByProcedure,
            IReadOnlyList<BatchStepPlan>? steps)
        {
            if (missingByProcedure == null || missingByProcedure.Count == 0)
            {
                return new ErrorCodeAttributionResult(Array.Empty<string>(), false);
            }

            // 목차가 없으면 귀속할 좌표가 없다. 빈 목록만 돌려주면 "고칠 단계가 없다"로
            // 읽히므로 미귀속을 함께 알린다.
            if (steps == null || steps.Count == 0)
            {
                return new ErrorCodeAttributionResult(Array.Empty<string>(), true);
            }

            var attributed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var unattributed = false;

            foreach (var codes in missingByProcedure.Values)
            {
                foreach (var raw in codes)
                {
                    var code = raw?.Trim();
                    if (string.IsNullOrEmpty(code)) continue;

                    var owners = steps
                        .Where(step => step.ErrorCodes.Any(declared =>
                            string.Equals(declared?.Trim(), code, StringComparison.OrdinalIgnoreCase)))
                        .Select(step => step.Code)
                        .ToList();

                    if (owners.Count == 0)
                    {
                        // 어느 단계도 이 코드를 맡겠다고 선언하지 않았다. 목차 결함이다.
                        unattributed = true;
                        continue;
                    }

                    // 둘 이상이면 좁히지 않는다 - 어느 쪽이 빠뜨렸는지 모른다.
                    foreach (var owner in owners) attributed.Add(owner);
                }
            }

            return new ErrorCodeAttributionResult(attributed.ToList(), unattributed);
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~ErrorCodeAttributionTests
```

기대: 6개 전부 PASS.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/ErrorCodeAttribution.cs tests/ReSet.Core.Tests/ErrorCodeAttributionTests.cs
git commit -m "feat: 누락 오류 코드를 선언 단계로 귀속한다 — 문서 전체를 다시 만들지 않는다"
```

---

## Task 6: L1 위반을 단계로 귀속한다

**Why:** 실측 3차 L1 실패는 `END TRY` 하나였고 4차는 `BatchRun` INSERT 부재였다. 지점이 특정되는데도 문서 전체를 다시 만들었다.

**Files:**
- Create: `src/ReSet.Core/Services/L1ViolationAttribution.cs`
- Test: `tests/ReSet.Core.Tests/L1ViolationAttributionTests.cs`

**Interfaces:**
- Consumes: `MarkdownSectionLocator.SplitLines(string?) -> List<string>`, `BatchStepPlan`
- Produces: `L1ViolationAttribution.AttributeByLexeme(string documentMarkdown, string lexeme, IReadOnlyList<BatchStepPlan>? steps) -> string?` (단계 코드 또는 null) — **Task 3의 L1 분기가 이것을 소비한다**

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/L1ViolationAttributionTests.cs`:

```csharp
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class L1ViolationAttributionTests
    {
        private static BatchStepPlan Step(string code) =>
            new(code, $"{code} 단계",
                LegacyProcedures: new[] { $"dbo.UP_{code}" },
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        private const string Document = """
            ## 단계별 이행 상세 및 의사코드

            ### S01. 잠금과 RunId 발급

            ```sql
            INSERT INTO batch.BatchRun (JobName) VALUES (@JobName);
            ```

            ### S02. 수수료율 스냅샷

            ```sql
            BEGIN TRY
                INSERT INTO dbo.TS02 SELECT 1;
            END TRY
            BEGIN CATCH
            END CATCH
            ```

            ### S03. 정산 원장

            ```sql
            SELECT 1;
            ```
            """;

        // 실측(POQSettleBatch4 시도 3): 규칙 3-1 위반 `END TRY` 하나로 문서 전체를 다시 만들었다.
        [Fact]
        public void LexemeInsideStepSection_IsAttributedToThatStep()
        {
            var steps = new[] { Step("S01"), Step("S02"), Step("S03") };

            var code = L1ViolationAttribution.AttributeByLexeme(Document, "END TRY", steps);

            Assert.Equal("S02", code);
        }

        [Fact]
        public void LexemeInFirstStep_IsAttributedToFirstStep()
        {
            var steps = new[] { Step("S01"), Step("S02"), Step("S03") };

            var code = L1ViolationAttribution.AttributeByLexeme(Document, "batch.BatchRun", steps);

            Assert.Equal("S01", code);
        }

        // 어디에도 없으면 귀속하지 않는다. 억지로 붙이면 멀쩡한 단계를 다시 쓴다.
        [Fact]
        public void LexemeNotFound_ReturnsNull()
        {
            var steps = new[] { Step("S01"), Step("S02") };

            Assert.Null(L1ViolationAttribution.AttributeByLexeme(Document, "MERGE INTO", steps));
        }

        [Fact]
        public void NullSteps_ReturnsNull()
        {
            Assert.Null(L1ViolationAttribution.AttributeByLexeme(Document, "END TRY", steps: null));
        }

        // 단계 헤딩 앞(공통 규약 절)에 있는 어휘는 어느 단계의 것도 아니다.
        // 골격의 결함이므로 단계에 붙이면 안 된다.
        [Fact]
        public void LexemeBeforeAnyStepHeading_ReturnsNull()
        {
            var doc = "## 단계별 이행 상세 및 의사코드\n\n공통 규약에서 END TRY 를 금지한다.\n\n### S01. 첫 단계\n\n본문\n";
            var steps = new[] { Step("S01") };

            Assert.Null(L1ViolationAttribution.AttributeByLexeme(doc, "END TRY", steps));
        }

        // 목차에 없는 단계 헤딩 안에서 발견되면 귀속하지 않는다 -
        // 그 헤딩은 우리가 아는 단계가 아니다.
        [Fact]
        public void LexemeInUnknownStepSection_ReturnsNull()
        {
            var doc = "## 단계별 이행 상세 및 의사코드\n\n### S99. 모르는 단계\n\nEND TRY\n";
            var steps = new[] { Step("S01") };

            Assert.Null(L1ViolationAttribution.AttributeByLexeme(doc, "END TRY", steps));
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~L1ViolationAttributionTests
```

기대: 컴파일 실패.

- [ ] **Step 3: 구현한다**

`src/ReSet.Core/Services/L1ViolationAttribution.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// L1 위반이 어느 단계 섹션 안에서 일어났는지 찾는다.
    ///
    /// 이 클래스가 존재하는 이유: 실측(POQSettleBatch4 2026-08-29)의 3차 L1 실패는
    /// 규칙 3-1 위반 `END TRY` 하나였고 4차는 `batch.BatchRun` INSERT 부재였다.
    /// 지점이 특정되는 결함인데도 문서 전체를 다시 만들었고, 그렇게 두 회차를 태웠다.
    ///
    /// 귀속하지 못하면 null을 돌려준다. 억지로 아무 단계에나 붙이면 멀쩡한 단계를
    /// 다시 쓰게 되어, 회귀 롤백이 막으려는 회귀를 다시 들인다. 호출부는 null을
    /// "전량 재생성"으로 읽는다.
    /// </summary>
    public static class L1ViolationAttribution
    {
        /// <summary>
        /// 어휘가 처음 나타나는 자리를 감싼 `###` 단계 헤딩의 단계 코드를 돌려준다.
        ///
        /// 코드 펜스 안을 건너뛰지 않는 이유: 위반 어휘 자체가 대개 SQL 코드 블록
        /// 안에 있다(`END TRY`가 그렇다). 헤딩 탐지만 펜스를 존중한다 -
        /// MarkdownSectionLocator가 이미 그 판정을 소유한다.
        /// </summary>
        public static string? AttributeByLexeme(
            string? documentMarkdown, string lexeme, IReadOnlyList<BatchStepPlan>? steps)
        {
            if (string.IsNullOrEmpty(documentMarkdown) ||
                string.IsNullOrWhiteSpace(lexeme) ||
                steps == null || steps.Count == 0)
            {
                return null;
            }

            var lines = MarkdownSectionLocator.SplitLines(documentMarkdown);
            string? currentStep = null;

            foreach (var line in lines)
            {
                var heading = TryReadStepHeading(line, steps);
                if (heading != null)
                {
                    currentStep = heading;
                    continue;
                }

                if (line.IndexOf(lexeme, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // currentStep이 null이면 아직 어떤 단계 섹션에도 들어가지 않았다는 뜻 -
                    // 공통 규약 절의 어휘이므로 단계에 붙이지 않는다.
                    return currentStep;
                }
            }

            return null;
        }

        /// <summary>
        /// `### S02. 이름` 꼴에서 목차가 아는 단계 코드를 읽는다. 목차에 없는 코드는
        /// null이다 - 우리가 아는 단계가 아니면 귀속의 근거가 없다.
        /// </summary>
        private static string? TryReadStepHeading(string line, IReadOnlyList<BatchStepPlan> steps)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("###", StringComparison.Ordinal)) return null;

            return steps
                .Select(step => step.Code)
                .FirstOrDefault(code =>
                    !string.IsNullOrWhiteSpace(code) &&
                    trimmed.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~L1ViolationAttributionTests
```

기대: 6개 전부 PASS.

- [ ] **Step 5: 커밋**

```bash
git add src/ReSet.Core/Services/L1ViolationAttribution.cs tests/ReSet.Core.Tests/L1ViolationAttributionTests.cs
git commit -m "feat: L1 위반을 그것이 실린 단계로 귀속한다 — END TRY 하나에 문서 전체를 다시 쓰지 않는다"
```

---

## Task 7: 단계 동결 — 지목되지 않은 단계는 바이트 그대로 재사용한다

**Why:** 회차마다 통과한 단계까지 다시 쓰이며 새 결함이 들어온다. 실측 6차가 5차 대비 정합성 8→7, 예외 7→6으로 떨어진 것이 그 결과다.

**Files:**
- Create: `src/ReSet.Core/Services/StepFreezeState.cs`
- Test: `tests/ReSet.Core.Tests/StepFreezeStateTests.cs`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs:2148-2156` (`pendingDefectiveSteps` 수집부)

**Interfaces:**
- Consumes: Task 5의 `ErrorCodeAttribution.Attribute`, Task 6의 `L1ViolationAttribution.AttributeByLexeme`, 기존 `StepDefect`
- Produces: `StepFreezeState.OpenSteps(IReadOnlyList<BatchStepPlan>? steps, IReadOnlyCollection<string> criticDefectiveSteps, IReadOnlyDictionary<string, StepDefect> floorViolations, IReadOnlyList<string> errorCodeSteps) -> IReadOnlyList<string>`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/StepFreezeStateTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StepFreezeStateTests
    {
        private static BatchStepPlan Step(string code) =>
            new(code, $"{code} 단계",
                LegacyProcedures: new[] { $"dbo.UP_{code}" },
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        private static readonly IReadOnlyList<BatchStepPlan> Steps =
            new[] { Step("S01"), Step("S02"), Step("S03") };

        private static readonly IReadOnlyDictionary<string, StepDefect> NoFloorViolations =
            new Dictionary<string, StepDefect>();

        [Fact]
        public void NoSignals_FreezesEveryStep()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), NoFloorViolations, Array.Empty<string>());

            Assert.Empty(open);
        }

        [Fact]
        public void CriticDefectiveStep_IsOpen()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S02" }, NoFloorViolations, Array.Empty<string>());

            Assert.Equal(new[] { "S02" }, open);
        }

        [Fact]
        public void QualityFloorViolation_IsOpen()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S03"] = new StepDefect(StepDefectKind.QualityFloor, "S03 (본문이 하한 미달)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), floor, Array.Empty<string>());

            Assert.Equal(new[] { "S03" }, open);
        }

        [Fact]
        public void GenerationFailure_IsOpen()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S03"] = new StepDefect(StepDefectKind.GenerationFailed, "S03 (본문 없음)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), floor, Array.Empty<string>());

            Assert.Equal(new[] { "S03" }, open);
        }

        // Unverifiable 은 "대조할 재료가 목차에 없어 검사가 돌지 못했다"이지
        // "본문이 나쁘다"가 아니다. StepDefectKind 의 주석이 "재생성으로 고쳐지지
        // 않는다"고 명시한다. 열어 두면 매 회차 같은 단계를 다시 뽑으면서 판정은
        // 영원히 그대로다 - 예산만 태우고 새 결함을 들인다.
        //
        // 재생성이 못 고치는 것은 루프가 아니라 배너가 처리한다(설계서 §3-7).
        [Fact]
        public void UnverifiableStep_IsNotOpen()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S03"] = new StepDefect(StepDefectKind.Unverifiable, "S03 (대조 재료 없음)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), floor, Array.Empty<string>());

            Assert.Empty(open);
        }

        // 단, Critic 이 그 단계를 따로 지목했다면 연다 - 재료가 없는 것과
        // 본문에 결함이 있는 것은 별개다.
        [Fact]
        public void UnverifiableStep_StillOpensWhenCriticNamesIt()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S03"] = new StepDefect(StepDefectKind.Unverifiable, "S03 (대조 재료 없음)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S03" }, floor, Array.Empty<string>());

            Assert.Equal(new[] { "S03" }, open);
        }

        [Fact]
        public void ErrorCodeAttributedStep_IsOpen()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, Array.Empty<string>(), NoFloorViolations, new[] { "S01" });

            Assert.Equal(new[] { "S01" }, open);
        }

        // 세 신호가 겹쳐도 한 번만 연다. 중복이 남으면 같은 단계를 두 번 생성한다.
        [Fact]
        public void OverlappingSignals_OpenStepOnlyOnce()
        {
            var floor = new Dictionary<string, StepDefect>
            {
                ["S02"] = new StepDefect(StepDefectKind.QualityFloor, "S02 (본문이 하한 미달)")
            };

            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S02" }, floor, new[] { "S02" });

            Assert.Equal(new[] { "S02" }, open);
        }

        // 목차에 없는 코드를 Critic이 지목하면 버린다 - 생성할 대상이 없다.
        [Fact]
        public void UnknownStepCode_IsDiscarded()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S99" }, NoFloorViolations, Array.Empty<string>());

            Assert.Empty(open);
        }

        // 목차가 없으면 단계 단위로 열 수 없다. 빈 목록을 내면 "고칠 것이 없다"로
        // 읽히므로 호출부가 전량 재생성을 택하도록 null을 돌려준다.
        [Fact]
        public void NullSteps_ReturnsNull()
        {
            Assert.Null(StepFreezeState.OpenSteps(
                null, new[] { "S01" }, NoFloorViolations, Array.Empty<string>()));
        }

        // 순서는 목차 순서를 따른다. 집합 열거 순서에 맡기면 회차마다 생성 순서가
        // 달라져 로그 대조가 불가능해진다.
        [Fact]
        public void OpenSteps_FollowPlanOrder()
        {
            var open = StepFreezeState.OpenSteps(
                Steps, new[] { "S03", "S01" }, NoFloorViolations, Array.Empty<string>());

            Assert.Equal(new[] { "S01", "S03" }, open);
        }
    }
}
```

`StepDefect`는 `record StepDefect(StepDefectKind Kind, string Reason)`이고 `StepDefectKind`는 `QualityFloor` · `Unverifiable` · `GenerationFailed` 셋이다(`src/ReSet.Core/Services/StepDefect.cs`). 파일 상단에 `using System.Collections.Generic;`과 `using System;`을 넣는다.

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~StepFreezeStateTests
```

기대: 컴파일 실패.

- [ ] **Step 3: 구현한다**

`src/ReSet.Core/Services/StepFreezeState.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 이번 회차에 다시 생성할 단계를 정한다. 나머지는 동결되어 직전 본문이
    /// 바이트 그대로 재사용된다.
    ///
    /// 이 클래스가 존재하는 이유: 회차마다 통과한 단계까지 다시 쓰이며 새 결함이
    /// 들어왔다. 실측(POQSettleBatch4)에서 6차가 5차 대비 정합성 8->7, 예외 7->6으로
    /// 떨어진 것이 그 결과다.
    ///
    /// 동결은 셋의 AND다 - 하한 검사 통과 · 오류 코드 검사 통과 · Critic 미지목.
    /// 확률적인 신호(Critic) 하나에만 맡기지 않는 것이 요점이다. 기계가 아는 결함은
    /// 동결되지 않는다.
    ///
    /// 단 하나의 예외가 Unverifiable 이다 - 재생성으로 고쳐지지 않는 판정이므로
    /// 열어 두면 예산만 태운다. 아래 루프의 주석 참조.
    /// </summary>
    public static class StepFreezeState
    {
        /// <summary>
        /// 다시 생성할 단계 코드를 목차 순서로 돌려준다.
        ///
        /// null을 돌려주는 경우: 목차가 단계 목록을 내지 못했다. 빈 목록을 돌려주면
        /// "고칠 것이 없다"로 읽혀 결함이 조용히 남으므로, 호출부가 전량 재생성을
        /// 택할 수 있도록 없음과 구분한다.
        /// </summary>
        public static IReadOnlyList<string>? OpenSteps(
            IReadOnlyList<BatchStepPlan>? steps,
            IReadOnlyCollection<string> criticDefectiveSteps,
            IReadOnlyDictionary<string, StepDefect> floorViolations,
            IReadOnlyList<string> errorCodeSteps)
        {
            if (steps == null || steps.Count == 0)
            {
                return null;
            }

            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var code in criticDefectiveSteps ?? Array.Empty<string>()) open.Add(code);
            foreach (var code in errorCodeSteps ?? Array.Empty<string>()) open.Add(code);

            // 하한 위반 중 재생성으로 고칠 수 있는 것만 연다.
            //
            // Unverifiable 을 빼는 이유: 그것은 "대조할 재료가 목차에 없어 검사가 돌지
            // 못했다"이지 "본문이 나쁘다"가 아니다(StepDefectKind 의 주석이 "재생성으로
            // 고쳐지지 않는다"고 명시한다). 열어 두면 매 회차 같은 단계를 다시 뽑는데
            // 판정은 영원히 그대로다 - 예산만 태우고 새 결함을 들인다. 재생성이 못 고치는
            // 것은 루프가 아니라 배너가 처리한다(설계서 §3-7).
            //
            // Critic 이 그 단계를 따로 지목했다면 위에서 이미 열렸다 - 재료가 없는 것과
            // 본문에 결함이 있는 것은 별개다.
            foreach (var (code, defect) in floorViolations ?? new Dictionary<string, StepDefect>())
            {
                if (defect.Kind != StepDefectKind.Unverifiable) open.Add(code);
            }

            // 목차 순서로 투영한다. HashSet 열거 순서에 맡기면 회차마다 생성 순서가
            // 달라져 로그 대조가 불가능해진다. 목차에 없는 코드는 여기서 자연히 빠진다 -
            // 생성할 대상이 없기 때문이다.
            return steps
                .Where(step => open.Contains(step.Code))
                .Select(step => step.Code)
                .ToList();
        }
    }
}
```

- [ ] **Step 4: 오케스트레이터가 이것을 쓰게 한다**

`VerificationPipelineOrchestrator.cs`의 `pendingDefectiveSteps` 수집부(`:2148` 근방)를 바꾼다:

```csharp
                        // 어느 단계가 문제인지 세 신호를 합쳐 정한다. Critic 지목만 쓰면
                        // 기계가 아는 결함(하한 미달·오류 코드 누락)이 있는 단계가 동결된다.
                        pendingDefectiveSteps.Clear();
                        var codeAttribution = ErrorCodeAttribution.Attribute(missingErrorCodes, currentSteps);
                        var openSteps = StepFreezeState.OpenSteps(
                            currentSteps, l2Result.DefectiveSteps, stepFloorViolations, codeAttribution.StepCodes);

                        if (openSteps != null)
                        {
                            pendingDefectiveSteps.AddRange(openSteps);
                        }

                        // 어느 단계도 선언하지 않은 원본 오류 코드가 누락됐다면 그것은
                        // 본문이 아니라 목차의 결함이다(설계서 §3-5(b)). 기계가 발견한
                        // 이 사실을 Critic의 자기 신고와 OR로 합쳐 재설계 조건에 넘긴다.
                        //
                        // 합치지 않으면 HasUnattributed가 소비자 없는 신호로 남는다 -
                        // 만들어졌으나 아무도 안 쓰는 산출물이 이 계획에서 두 번 나왔다.
                        machineFoundStructureDefect = codeAttribution.HasUnattributed;
                        if (machineFoundStructureDefect)
                        {
                            _userInteraction.NotifyStatus(
                                $"[yellow]{jobName}[/] - 어느 단계도 맡지 않은 원본 오류 코드가 누락되어 " +
                                "목차 결함으로 기록합니다.");
                        }
```

`:2343`의 「지목이 비면 골격까지 새로 만든다」 분기는 Task 8에서 함께 다룬다 — 그 분기가 사라지려면 Critic이 지목을 반드시 내야 하고, 그 계약 변경이 Task 8이다.

- [ ] **Step 5: 전체 테스트를 돌린다**

```bash
dotnet test
```

기대: 실패 0 · 건너뜀 0 · 경고 0.

- [ ] **Step 6: 커밋**

```bash
git add src/ReSet.Core/Services/StepFreezeState.cs tests/ReSet.Core.Tests/StepFreezeStateTests.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs
git commit -m "feat: 세 신호로 단계 동결을 정한다 — 통과한 단계가 다음 회차에 망가지지 않게 한다"
```

---

## Task 8: Critic 계약 확장 — 전역 결함과 구조 결함에 자리를 준다

**Why:** 실측 「필수 수정 3」(1.4절 그룹 트랜잭션 선언과 S11~S13 의사코드의 모순)은 어느 한 단계의 결함이 아니다. 담을 자리가 없어 관련 단계들이 `DefectiveSteps`에 실렸고, 각자 다시 쓰이며 모순이 재생산됐다.

**Files:**
- Modify: `src/ReSet.Core/Services/IAiService.cs` (`ReviewResult`)
- Modify: `src/ReSet.Core/Services/AiService.cs:2420` 근방(JSON 파싱) · `:4549` 근방(Critic 프롬프트)
- Modify: `src/ReSet.Core/Services/StructureRedraftPolicy.cs`
- Modify: `tests/ReSet.Core.Tests/StructureRedraftPolicyTests.cs`
- Test: `tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs`

**Interfaces:**
- Consumes: Task 7의 `StepFreezeState.OpenSteps`
- Produces: `ReviewResult.SkeletonDefective` (bool), `ReviewResult.StructureDefective` (bool), `StructureRedraftPolicy.TryConsume(bool improvedThisAttempt, bool structureDefective)`

- [ ] **Step 1: 실패하는 테스트를 쓴다 — 재설계 정책**

`tests/ReSet.Core.Tests/StructureRedraftPolicyTests.cs`를 통째로 바꾼다:

```csharp
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class StructureRedraftPolicyTests
    {
        [Fact]
        public void NewPolicy_HasNotConsumedItsRedraft()
        {
            var policy = new StructureRedraftPolicy();

            Assert.False(policy.Consumed);
        }

        // 개선이 나오는 동안은 목차가 원인이 아니다. 멀쩡한 구조를 갈아엎지 않는다.
        [Fact]
        public void ImprovingAttempt_DoesNotRedraft()
        {
            var policy = new StructureRedraftPolicy();

            Assert.False(policy.TryConsume(improvedThisAttempt: true, structureDefective: true));
            Assert.False(policy.Consumed);
        }

        // 실측(POQSettleBatch4 2026-08-29): 미갱신 1회로 발동한 재설계가 14단계 체계를
        // 16단계로 갈아엎고 골격·섹션 캐시를 전부 폐기했고, 곧바로 3·4차가 L1에서
        // 연속으로 떨어져 예산 4회 중 2회를 태웠다. 1차는 후보가 없어 항상 갱신되므로
        // 미갱신 1회 조건은 사실상 2차 결과 하나에 거는 도박이었다.
        [Fact]
        public void SingleStagnantAttempt_DoesNotRedraft()
        {
            var policy = new StructureRedraftPolicy();

            Assert.False(policy.TryConsume(improvedThisAttempt: false, structureDefective: true));
            Assert.False(policy.Consumed);
        }

        [Fact]
        public void TwoConsecutiveStagnantAttemptsWithStructureDefect_Redrafts()
        {
            var policy = new StructureRedraftPolicy();

            policy.TryConsume(improvedThisAttempt: false, structureDefective: true);

            Assert.True(policy.TryConsume(improvedThisAttempt: false, structureDefective: true));
            Assert.True(policy.Consumed);
        }

        // 정체해도 Critic이 구조 결함을 짚지 않았다면 원인은 본문이다.
        // 목차를 갈아엎으면 L1을 통과하던 구조를 잃는다.
        [Fact]
        public void StagnantWithoutStructureDefect_DoesNotRedraft()
        {
            var policy = new StructureRedraftPolicy();

            policy.TryConsume(improvedThisAttempt: false, structureDefective: false);

            Assert.False(policy.TryConsume(improvedThisAttempt: false, structureDefective: false));
            Assert.False(policy.Consumed);
        }

        // 개선이 한 번 나오면 연속 카운터가 끊긴다.
        [Fact]
        public void ImprovementResetsTheStagnationStreak()
        {
            var policy = new StructureRedraftPolicy();

            policy.TryConsume(improvedThisAttempt: false, structureDefective: true);
            policy.TryConsume(improvedThisAttempt: true, structureDefective: true);

            Assert.False(policy.TryConsume(improvedThisAttempt: false, structureDefective: true));
            Assert.False(policy.Consumed);
        }

        // Job당 1회. 구조를 한 번 갈아엎었는데도 정체하면 원인은 목차가 아니다.
        [Fact]
        public void AfterConsumption_NeverRedraftsAgain()
        {
            var policy = new StructureRedraftPolicy();
            policy.TryConsume(improvedThisAttempt: false, structureDefective: true);
            policy.TryConsume(improvedThisAttempt: false, structureDefective: true);

            Assert.False(policy.TryConsume(improvedThisAttempt: false, structureDefective: true));
            Assert.True(policy.Consumed);
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~StructureRedraftPolicyTests
```

기대: 컴파일 실패 — `TryConsume`이 인자 하나만 받는다.

- [ ] **Step 3: 정책을 구현한다**

`src/ReSet.Core/Services/StructureRedraftPolicy.cs`의 `TryConsume`을 바꾼다:

```csharp
        private int _stagnantStreak;

        /// <summary>
        /// 목차를 다시 세울지 판정한다. 둘이 모두 참일 때만 발동한다 —
        /// 2회 연속 미갱신, 그리고 Critic이 구조 결함을 지목했을 것.
        ///
        /// 조건을 둘로 늘린 이유: 미갱신 1회로 발동하던 종전 규칙은 1차가 항상
        /// 갱신되므로 사실상 2차 결과 하나에 거는 도박이었다. 실측(POQSettleBatch4
        /// 2026-08-29)에서 그것이 터져, L1을 통과하며 점수를 받고 있던 14단계 체계를
        /// 16단계로 갈아엎고 골격·섹션 캐시를 전부 폐기했다. 곧바로 3·4차가 L1에서
        /// 연속으로 떨어져 예산 4회 중 2회를 태웠다.
        ///
        /// 두 번째 조건이 없으면 "본문이 나쁜 것"과 "목차가 나쁜 것"을 가르지 못한다.
        ///
        /// 기본 예산(MaxL2Attempts=5 -> 총 6회)에서 2회 연속 미갱신은 이르면 3차에
        /// 성립하므로 발동할 자리가 남는다.
        ///
        /// L3 사용자 지시는 이 정책을 거치지 않는다.
        /// </summary>
        public bool TryConsume(bool improvedThisAttempt, bool structureDefective)
        {
            if (improvedThisAttempt)
            {
                _stagnantStreak = 0;
                return false;
            }

            _stagnantStreak++;

            if (Consumed || _stagnantStreak < 2 || !structureDefective)
            {
                return false;
            }

            Consumed = true;
            return true;
        }
```

- [ ] **Step 4: `ReviewResult`에 플래그 둘을 추가한다**

`src/ReSet.Core/Services/IAiService.cs`의 `ReviewResult`에 추가한다:

```csharp
        /// <summary>
        /// 공통 규약(골격)과 단계 본문이 서로 모순되는가.
        ///
        /// 이 필드가 필요한 이유: 실측 「필수 수정 3」은 1.4절이 선언한 그룹 트랜잭션
        /// 계약과 S11~S13 의사코드의 모순이었다. 어느 한 단계의 결함이 아니므로 각
        /// 섹션은 자기 안에서 일관되고, 단계 재생성으로는 영원히 고쳐지지 않는다.
        /// 담을 자리가 없던 동안은 관련 단계들이 DefectiveSteps에 실렸고, 그 단계들이
        /// 각자 다시 쓰이며 모순이 재생산됐다.
        /// </summary>
        public bool SkeletonDefective { get; set; }

        /// <summary>
        /// 목차 자체가 결함인가 — 단계 누락, 단계 배치 오류, 청킹 불가 단계의 청킹 지정.
        /// StructureRedraftPolicy가 목차 재설계를 발동할 두 조건 중 하나다.
        /// </summary>
        public bool StructureDefective { get; set; }
```

- [ ] **Step 5: JSON 파싱과 프롬프트를 확장한다**

`AiService.cs`의 리뷰 JSON 파싱부(`:2420` 근방, `DefectiveSteps`를 읽는 곳 옆)에 추가한다:

```csharp
                    var skeletonDefective =
                        resultRoot.TryGetProperty("SkeletonDefective", out var skeletonProp) &&
                        skeletonProp.ValueKind == System.Text.Json.JsonValueKind.True;
                    var structureDefective =
                        resultRoot.TryGetProperty("StructureDefective", out var structureProp) &&
                        structureProp.ValueKind == System.Text.Json.JsonValueKind.True;
```

`ReviewResult` 생성부에 대입을 추가한다:

```csharp
                        SkeletonDefective = skeletonDefective,
                        StructureDefective = structureDefective,
```

`:4549` 근방의 Critic 출력 계약에 아래를 추가한다:

```
- `DefectiveSteps` is MANDATORY whenever `HasDefects` is true. If a defect is real, you can point to the `###` section that carries it. An empty list with `HasDefects: true` will be rejected as an invalid review.
- Set `SkeletonDefective` to true when the shared-conventions subsections (transaction boundaries, checkpoint status values, the common error-tracking pattern) CONTRADICT the step bodies. That defect belongs to the skeleton, not to any one step — also list the contradicting steps in `DefectiveSteps`.
- Set `StructureDefective` to true when the approved step list itself is wrong: a source procedure that no step claims, a step placed out of order, or a step marked chunkable that cannot be chunked.
```

`:4558` 근방의 예시 JSON에 두 필드를 넣는다:

```
  "DefectiveSteps": ["S08", "S10"],
  "SkeletonDefective": false,
  "StructureDefective": false,
```

- [ ] **Step 6: 오케스트레이터가 두 플래그를 쓰게 한다**

재설계 호출부를 바꾼다. **Critic의 자기 신고와 기계가 발견한 목차 결함을 OR로 합친다** —
Task 7이 `machineFoundStructureDefect`에 담아 둔 값이 그것이다(어느 단계도 선언하지 않은
원본 오류 코드의 누락, 설계서 §3-5(b)).

```csharp
                        if (redraftPolicy.TryConsume(
                                improvedThisAttempt,
                                l2Result.StructureDefective || machineFoundStructureDefect))
```

루프 앞에 회차별 변수를 선언한다(Task 7이 채우고 이 자리가 읽는다):

```csharp
            // 기계가 발견한 목차 결함. Critic의 StructureDefective와 OR로 합쳐진다 -
            // 목차가 원본 오류 코드를 어느 단계에도 배정하지 않은 것은 모델의 판단을
            // 기다릴 일이 아니라 결정적으로 아는 사실이다.
            bool machineFoundStructureDefect = false;
```

지목이 빈 경우의 처리를 바꾼다. `:2343`의 「지목이 비면 골격까지 새로 만든다」 자리에서, Task 7이 넣은 `openSteps` 계산 뒤에 다음을 둔다:

```csharp
                        // 결함이 있다면서 자리를 못 대는 리뷰는 재생성의 근거가 될 수 없다.
                        // 종전에는 이 경우 골격까지 새로 만들어 전량 재생성을 불렀다.
                        //
                        // 재호출은 한 회차당 1회다. 상한이 없으면 Critic이 계속 자리를
                        // 못 대는 동안 유료 호출이 무한히 돈다. 두 번째도 못 대면 통과가
                        // 아니라 "리뷰 무효"로 확정한다 - 자리를 못 대는 리뷰를 통과로
                        // 읽는 것이 이 설계가 막으려는 침묵이다.
                        if (pendingDefectiveSteps.Count == 0 &&
                            !l2Result.SkeletonDefective &&
                            !l2Result.StructureDefective)
                        {
                            if (!reviewRetriedThisAttempt)
                            {
                                reviewRetriedThisAttempt = true;
                                _userInteraction.NotifyStatus(
                                    $"[yellow]{jobName}[/] - Critic이 결함을 신고했으나 자리를 대지 못해 리뷰를 다시 요청합니다.");
                                continue;   // attempt 를 올리지 않는다
                            }

                            _userInteraction.NotifyError(
                                $"{jobName} - Critic이 두 번 연속 결함의 자리를 대지 못했습니다. 리뷰 무효로 확정합니다.");
                            planOutcome = VerificationOutcome.ReviewNotRun;
                            documentBodyForChecks = consolidatedPlan;
                            consolidatedPlan =
                                VerificationBanner.ReviewNotRun("Critic이 결함의 자리를 대지 못했습니다.") + consolidatedPlan;
                            break;
                        }
```

루프 시작부(`while (true)` 바로 뒤)에 회차별 플래그를 초기화한다:

```csharp
                bool reviewRetriedThisAttempt = false;
```

`SkeletonDefective`가 참이면 골격만 다시 만들도록, `pendingDefectiveSteps`를 채운 뒤에 골격 캐시만 지운다:

```csharp
                        if (l2Result.SkeletonDefective)
                        {
                            // 골격만 버린다. 섹션은 동결 상태로 남겨 다음 회차가
                            // 새 골격 아래에 그대로 조립한다. 성립하지 않으면
                            // 회귀 롤백이 그 회차를 되감는다.
                            lastSkeleton = null;
                            lastSkeletonResult = null;
                            _userInteraction.NotifyStatus(
                                $"[yellow]{jobName}[/] - 공통 규약과 단계 본문의 모순이 지적되어 골격만 다시 만듭니다.");
                        }
```

- [ ] **Step 7: 통합 테스트를 추가한다**

```csharp
        /// <summary>
        /// 실측 「필수 수정 3」: 1.4절의 그룹 트랜잭션 선언과 S11~S13 의사코드가 모순됐다.
        /// 어느 한 단계의 결함이 아니므로 단계 재생성으로는 고쳐지지 않는다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipelineAsync_SkeletonDefective_RegeneratesSkeletonOnly()
        {
            var specs = new List<(string, string)> { ("dbo.USP_Test1", "내용") };
            var header = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";
            var plan = header + "\n본문";

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "1", "gpt-4");

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<IReadOnlyList<StepInterface>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), "Job_Test", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromResult(new ReviewResult
                    {
                        HasDefects = true, SkeletonDefective = true, FeedbackComment = "골격 모순",
                        ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 8, ScoreException = 6, ScoreReadability = 9
                    }),
                    _ => Task.FromResult(new ReviewResult
                    {
                        HasDefects = false,
                        ScoreAccuracy = 10, ScoreCrud = 10, ScoreInterface = 10, ScoreException = 10, ScoreReadability = 10
                    }));

            await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            // 지목이 비었어도 SkeletonDefective가 참이므로 리뷰 재호출로 빠지지 않는다.
            _userInteraction.Received().NotifyStatus(
                Arg.Is<string>(s => s.Contains("골격만 다시 만듭니다")));
        }

        /// <summary>
        /// 결함이 있다면서 자리를 못 대는 리뷰는 재생성의 근거가 될 수 없다.
        /// 종전에는 이 경우 골격까지 새로 만들어 전량 재생성을 불렀다.
        /// </summary>
        [Fact]
        public async Task RunConsolidatedPipelineAsync_DefectWithoutLocation_RetriesReviewOnce()
        {
            var specs = new List<(string, string)> { ("dbo.USP_Test1", "내용") };
            var header = "## 통합 배치 아키텍처 개요\n## Mermaid 기반 통합 흐름도\n## 단계별 이행 상세 및 의사코드\n## 통합 데이터 정합성 검증 SQL 세트";
            var plan = header + "\n본문";

            var orchestrator = new VerificationPipelineOrchestrator(
                _dbService, _aiService, _validator, _userInteraction, "1", "gpt-4");

            _aiService.BrainstormBatchPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Brainstorm Result" });
            _aiService.DraftBatchPlanStructureAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(new AiResult { Content = "Plan Structure" });
            _aiService.GenerateConsolidatedBatchPlanAsync(Arg.Any<string>(), Arg.Any<List<(string, string)>>(), "C#", "Job_Test", Arg.Any<string>(), Arg.Any<IReadOnlyList<StepInterface>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new AiResult { Content = plan }));

            // 두 번 다 자리를 못 댄다.
            _aiService.ReviewConsolidatedPlanAsync(Arg.Any<List<(string, string)>>(), Arg.Any<string>(), "Job_Test", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ReviewResult
                {
                    HasDefects = true, FeedbackComment = "어딘가 결함이 있다",
                    ScoreAccuracy = 6, ScoreCrud = 6, ScoreInterface = 6, ScoreException = 6, ScoreReadability = 6
                }));

            var result = await orchestrator.RunConsolidatedPipelineAsync(
                specs, "C#", "Job_Test", "OpenAI", _consolidatedOutputRoot, isBatchMode: true);

            Assert.Equal(VerificationOutcome.ReviewNotRun, result.Outcome);
            _userInteraction.Received().NotifyError(
                Arg.Is<string>(s => s.Contains("자리를 대지 못했습니다")));
        }
```

- [ ] **Step 8: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/IAiService.cs src/ReSet.Core/Services/AiService.cs src/ReSet.Core/Services/StructureRedraftPolicy.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/StructureRedraftPolicyTests.cs tests/ReSet.Core.Tests/VerificationPipelineOrchestratorTests.cs
git commit -m "feat: 전역 결함과 구조 결함에 자리를 준다 — 목차 재설계는 두 조건이 모두 참일 때만 발동한다"
```

---

## Task 9: 패치 재생성 — 섹션을 백지에서 다시 쓰지 않는다

**Why:** `GenerateBatchStepSectionAsync`는 지적은 주지만 직전 본문은 주지 않는다. 그래서 지적받은 단계는 매번 백지에서 다시 쓰이며, 고친 것과 멀쩡하던 것을 함께 다시 쓴다.

**Files:**
- Modify: `src/ReSet.Core/Services/IAiService.cs` (시그니처)
- Modify: `src/ReSet.Core/Services/AiService.cs:4270-4345`
- Modify: `src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs` (호출부에 직전 본문 전달)
- Test: `tests/ReSet.Core.Tests/AiServiceTests.cs`

**Interfaces:**
- Consumes: Task 7의 `pendingDefectiveSteps`, 기존 `lastStepSections`
- Produces: `GenerateBatchStepSectionAsync(..., string? floorFeedback = null, string? previousBody = null, CancellationToken cancellationToken = default)`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/AiServiceTests.cs`에 추가한다. 기존 파일의 `AiService` 생성 방식(가짜 `IAiClient` 주입)을 그대로 따르고, 프롬프트를 붙잡아 검사한다.

```csharp
        // 실측(POQSettleBatch4): 6차가 5차 대비 정합성 8->7, 예외 7->6으로 떨어졌다.
        // 지적된 단계를 백지에서 다시 쓰면 고친 것과 멀쩡하던 것을 함께 다시 쓴다.
        [Fact]
        public async Task GenerateBatchStepSectionAsync_WithPreviousBody_SendsRevisionContract()
        {
            var (service, client) = CreateServiceWithCapturingClient();
            var step = new BatchStepPlan("S04", "취소 원장",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_CANCEL_INS" },
                TargetTables: new[] { "dbo.TSettleByTX" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: true,
                SchemaTables: new[] { "dbo.TSettleByTX" });

            await service.GenerateBatchStepSectionAsync(
                step, new[] { step }, "공통 규약",
                new List<(string, string)> { ("dbo.UP_UTIL_SETTLE_CANCEL_INS", "명세서") },
                Array.Empty<StepInterface>(), "C#", "Job_Test",
                floorFeedback: "청크 진행 위치를 기록하라",
                previousBody: "### S04. 취소 원장\n\n```sql\nSELECT 1;\n```");

            var sent = client.LastUserPrompt + client.LastVolatileSuffix;
            Assert.Contains("[Previous Section Body]", sent);
            Assert.Contains("SELECT 1;", sent);
            Assert.Contains("byte-for-byte", sent);
        }

        // previousBody가 없으면 프롬프트가 한 바이트도 달라지면 안 된다.
        // 1차 회차 프롬프트가 재시도 회차와 같은 바이트를 유지해야 접두사 캐시가 산다.
        [Fact]
        public async Task GenerateBatchStepSectionAsync_WithoutPreviousBody_DoesNotMentionRevision()
        {
            var (service, client) = CreateServiceWithCapturingClient();
            var step = new BatchStepPlan("S04", "취소 원장",
                LegacyProcedures: new[] { "dbo.UP_UTIL_SETTLE_CANCEL_INS" },
                TargetTables: new[] { "dbo.TSettleByTX" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: true,
                SchemaTables: new[] { "dbo.TSettleByTX" });

            await service.GenerateBatchStepSectionAsync(
                step, new[] { step }, "공통 규약",
                new List<(string, string)> { ("dbo.UP_UTIL_SETTLE_CANCEL_INS", "명세서") },
                Array.Empty<StepInterface>(), "C#", "Job_Test");

            var sent = client.LastUserPrompt + client.LastVolatileSuffix;
            Assert.DoesNotContain("[Previous Section Body]", sent);
            Assert.DoesNotContain("[Revision Contract]", sent);
        }
```

`CreateServiceWithCapturingClient`가 기존 파일에 없으면, 파일 안의 기존 가짜 클라이언트 패턴을 그대로 본떠 `LastUserPrompt`·`LastVolatileSuffix`를 노출하는 헬퍼를 추가한다.

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~GenerateBatchStepSectionAsync_WithPreviousBody
```

기대: 컴파일 실패 — `previousBody` 파라미터가 없다.

- [ ] **Step 3: 구현한다**

`IAiService.cs`의 시그니처를 바꾼다(`floorFeedback` **뒤**, `cancellationToken` **앞**):

```csharp
        Task<AiResult> GenerateBatchStepSectionAsync(BatchStepPlan step, IReadOnlyList<BatchStepPlan> allSteps, string sharedConventions, System.Collections.Generic.List<(string FileName, string Content)> specs, IReadOnlyList<StepInterface> stepInterfaces, string targetLanguage, string jobName, string? effort = null, string? floorFeedback = null, string? previousBody = null, CancellationToken cancellationToken = default);
```

`AiService.cs`의 구현에서 같은 파라미터를 받고, `volatileSuffix`에 계약을 싣는다. **공유 접두사(`AppendSharedStepContext`)는 건드리지 않는다** — 회차마다 달라지는 것을 그곳에 두면 단계 N개의 캐시가 통째로 죽는다.

```csharp
            var volatileSuffix = new StringBuilder();
            volatileSuffix.AppendLine($"Now write the section for step {step.Code} ({step.Name}) ONLY.");

            // 직전 본문이 있으면 백지 재작성이 아니라 패치를 요구한다.
            // 실측(POQSettleBatch4): 6차가 5차 대비 정합성 8->7, 예외 7->6으로 떨어졌다 -
            // 지적된 자리를 고치면서 멀쩡하던 자리를 함께 다시 썼기 때문이다.
            if (!string.IsNullOrWhiteSpace(previousBody))
            {
                volatileSuffix.AppendLine();
                volatileSuffix.AppendLine("[Previous Section Body]");
                volatileSuffix.AppendLine(previousBody);
                volatileSuffix.AppendLine();
                volatileSuffix.AppendLine("[Revision Contract]");
                volatileSuffix.AppendLine("- Output the FULL section again, but change ONLY what the feedback below identifies.");
                volatileSuffix.AppendLine("- Every sentence not implicated by the feedback MUST be reproduced byte-for-byte.");
                volatileSuffix.AppendLine("- Do NOT rewrite, reorder, or \"improve\" untouched parts.");
            }

            if (!string.IsNullOrWhiteSpace(floorFeedback))
            {
                volatileSuffix.AppendLine();
                volatileSuffix.AppendLine("[Previous Attempt Rejected]");
                volatileSuffix.AppendLine(floorFeedback);
            }
```

응답을 받은 뒤 변경 비율을 로그에 남긴다:

```csharp
            if (!string.IsNullOrWhiteSpace(previousBody) && aiResult.Content.Length > 0)
            {
                // 패치가 재작성으로 변질됐는지의 신호. 자동으로 막지는 않는다 -
                // 정당한 대규모 수정과 구분할 기준이 아직 없다.
                var ratio = 1.0 - (LongestCommonPrefixLength(previousBody!, aiResult.Content)
                    / (double)Math.Max(previousBody!.Length, aiResult.Content.Length));
                Log.Information(
                    "AI 배치 단계 섹션 패치 변경 비율 - JobName: {JobName}, Step: {Step}, 비율: {Ratio:P1}",
                    jobName, step.Code, ratio);
            }
```

같은 클래스에 헬퍼를 둔다:

```csharp
        /// <summary>두 문자열이 앞에서부터 몇 글자를 공유하는가. 패치 변경 비율의 근사다.</summary>
        private static int LongestCommonPrefixLength(string a, string b)
        {
            var limit = Math.Min(a.Length, b.Length);
            var i = 0;
            while (i < limit && a[i] == b[i]) i++;
            return i;
        }
```

- [ ] **Step 4: 오케스트레이터가 직전 본문을 넘기게 한다**

`GenerateBySplitAsync` 안에서 단계 섹션을 부르는 자리를 찾아, 그 단계의 직전 본문을 넘긴다. 직전 본문은 `lastStepSections`(재생성 전 스냅샷)에서 읽는다.

```csharp
                previousBody: previousSections != null &&
                              previousSections.TryGetValue(step.Code, out var priorBody)
                    ? priorBody
                    : null,
```

에스컬레이션을 위해, 같은 단계가 같은 결함으로 연속 2회 지목됐는지를 추적하는 사전을 오케스트레이터 루프 앞에 둔다:

```csharp
            // 같은 단계가 같은 결함으로 연속 2회 지목되면 패치를 포기하고 백지
            // 재작성으로 올린다. 「최소 변경만 하고 근본 결함을 안 고친다」가 패치의
            // 고유 실패 모드이고, 그 상태로 예산을 태우면 안 된다.
            var repeatedDefects = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
```

`pendingDefectiveSteps`를 채운 직후에 갱신한다:

```csharp
                        foreach (var code in currentSteps?.Select(s => s.Code) ?? Enumerable.Empty<string>())
                        {
                            if (pendingDefectiveSteps.Contains(code, StringComparer.OrdinalIgnoreCase))
                            {
                                repeatedDefects[code] = repeatedDefects.TryGetValue(code, out var n) ? n + 1 : 1;
                            }
                            else
                            {
                                repeatedDefects.Remove(code);
                            }
                        }
```

`GenerateBySplitAsync`에 `repeatedDefects`를 넘기고, 값이 2 이상인 단계에는 `previousBody`를 `null`로 준다.

- [ ] **Step 5: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/IAiService.cs src/ReSet.Core/Services/AiService.cs src/ReSet.Core/Services/VerificationPipelineOrchestrator.cs tests/ReSet.Core.Tests/AiServiceTests.cs
git commit -m "feat: 섹션 재생성을 패치로 바꾼다 — 고친 것과 멀쩡하던 것을 함께 다시 쓰지 않는다"
```

---

## Task 10: 입력 축소 — 제공자별 `Full`/`Narrow` 모드

**Why:** 단계 호출 하나가 명세서 전량(실측 481KB · 약 250K 토큰)을 싣는다. 이는 접두사 캐시가 산다는 전제 위의 선택인데, CLI 제공자에서 재사용률이 3.1%다. 대가만 남고 이득이 없다.

**Files:**
- Create: `src/ReSet.Core/Services/PromptContextScope.cs`
- Test: `tests/ReSet.Core.Tests/PromptContextScopeTests.cs`
- Modify: `src/ReSet.Core/Services/AiService.cs` (`GenerateBatchStepSectionAsync`가 좁힌 명세서를 쓰게)
- Modify: `src/ReSet.Cli/appsettings.json` · `src/ReSet.Cli/Program.cs`

**Interfaces:**
- Consumes: `BatchStepPlan.LegacyProcedures`, **`AiClientFactory.IsCliProvider`** (제공자 분류의 정본. 여기서 `EndsWith("-cli")` 같은 사본을 만들지 마라 — 한쪽만 고쳐질 때 조용히 어긋난다)
- Produces: `PromptContextScope.ResolveMode(string providerName, string? configured) -> ContextScopeMode`, `PromptContextScope.NarrowSpecs(specs, step, callGraph) -> List<(string FileName, string Content)>`

- [ ] **Step 1: 실패하는 테스트를 쓴다**

`tests/ReSet.Core.Tests/PromptContextScopeTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PromptContextScopeTests
    {
        private static BatchStepPlan Step(string code, params string[] procedures) =>
            new(code, $"{code} 단계",
                LegacyProcedures: procedures,
                TargetTables: new[] { $"dbo.T{code}" },
                ErrorCodes: new[] { "-9010" },
                Chunkable: false,
                SchemaTables: new[] { $"dbo.T{code}" });

        private static readonly List<(string FileName, string Content)> AllSpecs = new()
        {
            ("dbo.UP_Util_Settle_Summary", "S11 명세서"),
            ("dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA", "S13 명세서 — 오류 시 4000~4008"),
            ("dbo.UP_UTIL_SETTLE_INS", "S03 명세서"),
        };

        // CLI 제공자는 프롬프트를 단일 텍스트로만 받아 cache_control을 찍을 자리가 없다.
        // 실측 재사용률 3.1%. 접두사를 부풀린 대가만 남는다.
        [Theory]
        [InlineData("claude-cli")]
        [InlineData("codex-cli")]
        [InlineData("agy-cli")]
        public void CliProviders_DefaultToNarrow(string provider)
        {
            Assert.Equal(ContextScopeMode.Narrow, PromptContextScope.ResolveMode(provider, configured: null));
        }

        [Theory]
        [InlineData("OpenAI")]
        [InlineData("Claude")]
        [InlineData("OpenRouter")]
        public void ApiProviders_DefaultToFull(string provider)
        {
            Assert.Equal(ContextScopeMode.Full, PromptContextScope.ResolveMode(provider, configured: null));
        }

        [Fact]
        public void ConfiguredValue_OverridesTheProviderDefault()
        {
            Assert.Equal(ContextScopeMode.Full, PromptContextScope.ResolveMode("claude-cli", "Full"));
            Assert.Equal(ContextScopeMode.Narrow, PromptContextScope.ResolveMode("OpenAI", "Narrow"));
        }

        [Fact]
        public void UnknownConfiguredValue_FallsBackToProviderDefault()
        {
            Assert.Equal(ContextScopeMode.Narrow, PromptContextScope.ResolveMode("claude-cli", "쓰레기값"));
        }

        [Fact]
        public void NarrowSpecs_KeepsTheStepsOwnProcedure()
        {
            var narrowed = PromptContextScope.NarrowSpecs(
                AllSpecs, Step("S03", "dbo.UP_UTIL_SETTLE_INS"),
                callGraph: new Dictionary<string, IReadOnlyList<string>>());

            Assert.Single(narrowed);
            Assert.Equal("dbo.UP_UTIL_SETTLE_INS", narrowed[0].FileName);
        }

        // 실측 「필수 수정 1·2」가 이 관계였다: S13이 S11 명세가 규정한 오류 코드
        // 4000~4008을 지켜야 했다. 이웃을 빼면 이 유형의 결함이 오히려 늘어난다.
        [Fact]
        public void NarrowSpecs_IncludesOneHopCallees()
        {
            var callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo.UP_Util_Settle_Summary"] = new[] { "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA" }
            };

            var narrowed = PromptContextScope.NarrowSpecs(
                AllSpecs, Step("S11", "dbo.UP_Util_Settle_Summary"), callGraph);

            Assert.Equal(2, narrowed.Count);
            Assert.Contains(narrowed, s => s.FileName == "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA");
        }

        // 1-hop만 넣는다. 2-hop까지 끌면 전량으로 되돌아간다.
        [Fact]
        public void NarrowSpecs_DoesNotFollowTwoHops()
        {
            var specs = new List<(string FileName, string Content)>
            {
                ("A", "a"), ("B", "b"), ("C", "c")
            };
            var callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = new[] { "B" },
                ["B"] = new[] { "C" }
            };

            var narrowed = PromptContextScope.NarrowSpecs(specs, Step("S01", "A"), callGraph);

            Assert.Equal(2, narrowed.Count);
            Assert.DoesNotContain(narrowed, s => s.FileName == "C");
        }

        // 좁힐 근거가 없으면 좁히지 않는다. 빈 목록을 보내면 모델이 "원본이 없다"로
        // 읽고 지어낸다.
        [Fact]
        public void NarrowSpecs_WhenNothingMatches_ReturnsEverything()
        {
            var narrowed = PromptContextScope.NarrowSpecs(
                AllSpecs, Step("S99", "dbo.UP_Unknown"),
                callGraph: new Dictionary<string, IReadOnlyList<string>>());

            Assert.Equal(AllSpecs.Count, narrowed.Count);
        }

        // 순서는 원본 목록 순서를 지킨다. 순서가 흔들리면 같은 재료라도
        // 접두사가 달라져 캐시가 죽고, 회차 간 대조도 불가능해진다.
        [Fact]
        public void NarrowSpecs_PreservesSourceOrder()
        {
            var callGraph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbo.UP_UTIL_SETTLE_INS"] = new[] { "dbo.UP_Util_Settle_Summary" }
            };

            var narrowed = PromptContextScope.NarrowSpecs(
                AllSpecs, Step("S03", "dbo.UP_UTIL_SETTLE_INS"), callGraph);

            Assert.Equal(
                new[] { "dbo.UP_Util_Settle_Summary", "dbo.UP_UTIL_SETTLE_INS" },
                narrowed.Select(s => s.FileName).ToArray());
        }
    }
}
```

- [ ] **Step 2: 테스트가 실패하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PromptContextScopeTests
```

기대: 컴파일 실패.

- [ ] **Step 3: 구현한다**

`src/ReSet.Core/Services/PromptContextScope.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services
{
    public enum ContextScopeMode
    {
        /// <summary>명세서 전량을 접두사에 싣는다. 접두사 캐시가 사는 제공자용.</summary>
        Full,

        /// <summary>단계의 프로시저와 그 1-hop 이웃만 싣는다. 캐시가 죽는 제공자용.</summary>
        Narrow
    }

    /// <summary>
    /// 단계 호출이 실을 명세서의 범위를 정한다.
    ///
    /// 이 클래스가 존재하는 이유: AppendSharedStepContext가 명세서 전량(실측 481KB)을
    /// 매 단계 호출에 싣는 것은 버그가 아니라 트레이드오프다 — 접두사가 단계 간 바이트까지
    /// 같아야 캐시가 살기 때문에, 캐시를 사려고 입력을 부풀렸다.
    ///
    /// 그런데 세 CLI 제공자는 모두 프롬프트를 단일 텍스트로만 받아 cache_control을 찍을
    /// 자리가 없다. 실측(POQSettleBatch4 2026-08-29): 캐시 쓰기 24,065,539 대 읽기
    /// 775,702 — 재사용률 3.1%. 전제가 거짓이므로 대가만 남는다.
    ///
    /// 비용만의 문제가 아니다. 250K 컨텍스트에서 16단계 규칙을 전부 지키게 하는 것
    /// 자체가 지시 이행력을 떨어뜨린다 — 재현성 저하의 원인이기도 하다.
    /// </summary>
    public static class PromptContextScope
    {
        public static ContextScopeMode ResolveMode(string? providerName, string? configured)
        {
            if (Enum.TryParse<ContextScopeMode>(configured, ignoreCase: true, out var explicitMode))
            {
                return explicitMode;
            }

            // CLI 제공자는 프롬프트를 단일 텍스트로만 받는다. 블록 배열도 다중 user
            // 메시지도 넘길 수단이 없어 cache_control을 찍을 자리가 물리적으로 없다.
            var isCli =
                providerName != null &&
                providerName.EndsWith("-cli", StringComparison.OrdinalIgnoreCase);

            return isCli ? ContextScopeMode.Narrow : ContextScopeMode.Full;
        }

        /// <summary>
        /// 이 단계가 봐야 할 명세서만 남긴다 — 자기 LegacyProcedures와 그것이 호출하는
        /// 1-hop 이웃.
        ///
        /// 이웃을 넣는 것이 요점이다. 실측 「필수 수정 1·2」가 정확히 그 관계였다:
        /// S13/S12가 S11 명세가 규정한 오류 코드(4000~4008 · ERROR_NUMBER 전파)를
        /// 지켜야 했다. 이웃을 빼면 이 유형의 결함이 오히려 늘어난다.
        ///
        /// 2-hop까지 끌지 않는다 — 그러면 전량으로 되돌아간다.
        ///
        /// 하나도 맞지 않으면 전량을 돌려준다. 빈 목록을 보내면 모델이 "원본 명세서가
        /// 없다"로 읽고 지어낸다.
        /// </summary>
        public static List<(string FileName, string Content)> NarrowSpecs(
            IReadOnlyList<(string FileName, string Content)> specs,
            BatchStepPlan step,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? callGraph)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var procedure in step.LegacyProcedures ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(procedure)) continue;
                wanted.Add(procedure.Trim());

                if (callGraph != null && callGraph.TryGetValue(procedure.Trim(), out var callees))
                {
                    foreach (var callee in callees)
                    {
                        if (!string.IsNullOrWhiteSpace(callee)) wanted.Add(callee.Trim());
                    }
                }
            }

            // 원본 목록 순서를 지킨다. 순서가 흔들리면 같은 재료라도 접두사가 달라져
            // 캐시가 죽고, 회차 간 프롬프트 대조도 불가능해진다.
            var narrowed = specs
                .Where(spec => wanted.Any(name => MatchesSpecName(spec.FileName, name)))
                .ToList();

            return narrowed.Count > 0 ? narrowed : specs.ToList();
        }

        /// <summary>
        /// 명세서 파일명과 프로시저 이름을 맞춘다. 파일명에 확장자나 경로가 붙는 경우가
        /// 있어 완전 일치만으로는 못 찾는다.
        /// </summary>
        private static bool MatchesSpecName(string fileName, string procedureName) =>
            !string.IsNullOrWhiteSpace(fileName) &&
            (string.Equals(fileName, procedureName, StringComparison.OrdinalIgnoreCase) ||
             fileName.IndexOf(procedureName, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
```

- [ ] **Step 4: 테스트가 통과하는지 확인한다**

```bash
dotnet test tests/ReSet.Core.Tests --filter FullyQualifiedName~PromptContextScopeTests
```

기대: 13개 전부 PASS (`[Theory]` 인라인 데이터가 전개되어 12가 아니라 13이다).

- [ ] **Step 5: `AiService`가 이 모드를 쓰게 한다**

`AiService`에 필드와 생성자 인자를 추가한다(기존 인자 뒤에):

```csharp
        private readonly ContextScopeMode _contextScope;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _callGraph;
```

`GenerateBatchStepSectionAsync` 안에서 `AppendSharedStepContext`에 넘길 명세서를 고른다:

```csharp
            // Full 모드에서는 접두사가 바이트 하나도 달라지면 안 된다 - 단계 N개가
            // 공유하는 캐시가 통째로 죽는다. 그래서 좁히기는 Narrow에서만 한다.
            var scopedSpecs = _contextScope == ContextScopeMode.Narrow
                ? PromptContextScope.NarrowSpecs(specs, step, _callGraph)
                : specs;

            var userPrompt = new StringBuilder();
            AppendSharedStepContext(
                userPrompt, allSteps, sharedConventions, scopedSpecs, stepInterfaces, targetLanguage, jobName);
```

- [ ] **Step 6: 설정과 배선을 추가한다**

`src/ReSet.Cli/appsettings.json`의 `StepConcurrency` 다음에 추가한다:

```jsonc
    "PromptContextScope": "",          // [통합 배치] 단계 본문 호출이 실을 명세서의 범위. ""(기본)이면 제공자로 정합니다 — CLI 제공자(claude-cli·codex-cli·agy-cli)는 Narrow, 그 외는 Full. "Full"은 명세서 전량을 접두사에 실어 단계 간 프롬프트 캐시를 노립니다. "Narrow"는 그 단계의 프로시저와 그것이 호출하는 1-hop 이웃만 싣습니다. CLI 제공자는 프롬프트를 단일 텍스트로만 받아 cache_control을 찍을 자리가 없으므로 Full을 써도 캐시가 살지 않습니다(실측 재사용률 3.1%).
```

`Program.cs`에서 `AiService`를 만드는 자리에 값을 넘긴다.

- [ ] **Step 7: 전체 테스트와 커밋**

```bash
dotnet test
git add src/ReSet.Core/Services/PromptContextScope.cs tests/ReSet.Core.Tests/PromptContextScopeTests.cs src/ReSet.Core/Services/AiService.cs src/ReSet.Cli/appsettings.json src/ReSet.Cli/Program.cs
git commit -m "feat: CLI 제공자에서 단계별 입력을 좁힌다 — 캐시가 안 사는데 접두사만 부풀렸다"
```

---

## Task 11: 대조 실행 — 개선을 증명한다

**Why:** 앞의 열 태스크가 실제로 지표를 움직였는지는 한 판을 돌려야 안다. 단조성 위반·L1 소진·토큰·벽시계는 결정적 지표라 1판으로 판정된다.

**Files:**
- Create: `output.bak-stage4-control-20260828/run-verify-batch4.sh` (gitignore 대상 트리 — 저장소에 커밋되지 않는다)
- Modify: `docs/superpowers/specs/2026-08-30-batch-plan-loop-reproducibility-design.md` (§5-3 표에 실측 결과 열 추가)

**Interfaces:**
- Consumes: Task 1의 `BatchRunLogReader`
- Produces: 없음 (측정 결과)

- [ ] **Step 1: 실행 스크립트를 쓴다**

`run-pair-batch4.sh`의 머리말을 읽고 그 통제 조건을 그대로 따른다. 로그 디렉터리를 **새로** 만드는 것이 핵심이다 — 같은 날 두 판이 한 파일에 이어 붙으면 완주 판정이 불가능해진다(실제로 Batch2에서 그 사고가 났다).

```bash
#!/bin/zsh
# 재현성 개선 검증판 — POQSettleBatch4 와 짝을 이룬다.
#
# 기준선: output.bak-stage4-control-20260828/logs-batch4/reset-20260829.log
#   궤적 [78,76,84,74] · 단조성 위반 2 · L1 소진 2 · 벽시계 2:23:26
#   캐시 쓰기 24,065,539 · 읽기 775,702
#
# 변인은 코드뿐이다. 모델·입력 트리·SP 목록이 기준선과 같아야 한다.
#   Actor        claude-cli / claude-sonnet-5
#   Critic       OpenRouter / z-ai/glm-5.3
#   Consolidator claude-cli / claude-sonnet-5
#
# [실행 전 점검]
#   1. 이 계획의 Task 1~10 이 전부 병합됐는가.  git log --oneline -12
#   2. Jobs/ 에 POQSettleBatch5 가 없는가.  ls Jobs/
#   3. 메뉴에서 "2. 통합 배치 마이그레이션 설계"로 갈 것.
#      "1. 개별 SP 역공학 분석"을 고르면 얼어붙은 입력 트리가 그 자리에서 깨진다.
#   4. 도는 동안 공유 체크아웃에서 빌드하지 말 것 (AGENTS.md §8).

set -e
REPO=/Users/payletter/git-root/ReSet
CONTROL=$REPO/output.bak-stage4-control-20260828

cd $REPO
OutputSettings__Directory=$CONTROL \
DatabaseSettings__OfflineSnapshotPath=$CONTROL/offline_snapshot.json \
LoggingSettings__LogDirectory=$CONTROL/logs-batch5-verify \
AiSettings__Provider=claude-cli \
AiSettings__ModelName=claude-sonnet-5 \
AiSettings__Critic__Provider=OpenRouter \
AiSettings__Critic__ModelName=z-ai/glm-5.3 \
AiSettings__Consolidator__Provider=claude-cli \
AiSettings__Consolidator__ModelName=claude-sonnet-5 \
AiSettings__AllowCliProviderInBatch=true \
    dotnet run --project src/ReSet.Cli
```

기존 `run-pair-batch4.sh`의 환경변수 이름과 값을 그대로 대조해 맞춘다 — 위 목록에 빠진 변수가 있으면 그 파일의 것을 따른다.

- [ ] **Step 2: 판을 돌린다**

```bash
zsh output.bak-stage4-control-20260828/run-verify-batch4.sh
```

Job 이름은 `POQSettleBatch5`로 입력한다. SP 목록은 기준선과 같은 14편을 고른다.

- [ ] **Step 3: 판독한다**

임시 테스트로 `BatchRunLogReader`를 새 로그에 돌린다(Task 1 Step 5와 같은 방식으로, 확인 후 삭제).

```csharp
var metrics = BatchRunLogReader.Read(File.ReadAllText(
    "/Users/payletter/git-root/ReSet/output.bak-stage4-control-20260828/logs-batch5-verify/reset-<날짜>.log"));
```

**판독 순서를 지킨다** — 완주 여부 → L1 발화와 `(시도 N/6)` → 수치 → 눈으로. 수치보다 완주가 먼저다.

- [ ] **Step 4: 합격 기준과 대조한다**

**기준선이 오염돼 있다.** Batch4 로그는 2026-08-29 실행이고, 8-30에 규칙이 두 번 바뀌었다 — `d9a455e1`(L1 검사 `CheckLegacyStepErrorCodeInvention` 추가)과 `0186c9a8`(Few-Shot 프롬프트 변경). 그래서 **오염 없는 셋으로만 판정한다.**

| 지표 | 기준선 | 합격 기준 | 실측 |
| :--- | ---: | :--- | ---: |
| 단조성 위반 | 2건 | **0건** | |
| L1 소진 회차 | 2회 | **0회** | |
| **호출당** 캐시 쓰기 | 248,098 | ≤ **99,000** | |
| — 아래는 참고값 (규칙 변경에 오염됨) — | | | |
| 총 캐시 쓰기 | 24.07M | 기록만 | |
| 벽시계 | 2h 23m | 기록만 | |
| 최종 환산 점수 | 84 | 하락하지 않을 것 | |

호출당 캐시 쓰기는 `metrics.CacheWriteTokens`를 토큰 사용량 로그 행 수로 나눠 구한다. 총량으로 재면 회차 수 변화에 오염되지만 호출당은 그렇지 않다 — §3-9가 줄이는 것이 호출당 입력이기 때문이다.

**단조성 위반이 1건이라도 나오면 Task 2의 구현 결함이다.** 롤백이 어느 경로에서 안 걸리는지 로그로 좁힌다 — 채점 직후 블록을 지나지 않는 종료 경로가 있을 가능성이 높다.

**이 판이 돈 커밋 해시를 로그와 함께 기록한다.** Batch4 기준선은 그것을 안 적어, 대조 시점에야 사이에 두 커밋이 들어온 것을 발견했다.

```bash
git rev-parse --short HEAD | tee output.bak-stage4-control-20260828/logs-batch5-verify/COMMIT
```

- [ ] **Step 5: 설계서에 실측 결과를 기록한다**

`docs/superpowers/specs/2026-08-30-batch-plan-loop-reproducibility-design.md`의 §5-3 표에 「실측」 열을 채우고, 기준을 못 맞춘 항목이 있으면 그 사유를 §7에 적는다.

- [ ] **Step 6: 커밋**

```bash
git add docs/superpowers/specs/2026-08-30-batch-plan-loop-reproducibility-design.md
git commit -m "docs(spec): 재현성 개선의 대조 실행 결과를 기록한다"
```

---

## Self-Review

**Spec coverage**

| 스펙 항목 | 태스크 |
| :--- | :--- |
| §3-1 회귀 롤백 | Task 2 |
| §3-2(a) 지목 없는 리뷰 무효 처리 · 재호출 1회 상한 | Task 8 Step 6 |
| §3-2(b) 동결 조건 셋의 AND | Task 7 |
| §3-2(c) 바이트 동일 재사용 | Task 7 Step 4 · Task 9 Step 4 |
| §3-3 예산 분리 | Task 3 |
| §3-4 재설계 조건 강화 | Task 8 Step 1~3 |
| §3-5(a) 오라클 승격 · 이중 계산 방지 | Task 4 |
| §3-5(b) 오류 코드 단계 귀속 | Task 5 |
| §3-5(c) L1 위반 단계 귀속 | Task 6 |
| §3-6 `SkeletonDefective` | Task 8 |
| §3-7 동결 고착 보고 | **미할당** — 아래 참조 |
| §3-8 패치 재생성 · 에스컬레이션 · 변경 비율 로그 | Task 9 |
| §3-9 `Full`/`Narrow` · 1-hop 이웃 | Task 10 |
| §5-2 판독 스크립트 | Task 1 |
| §5-1 대조 실행 | Task 11 |

**§3-7(동결 이력을 커버리지 축에 싣기)에 태스크가 없다.** 의도적으로 뺐다 — `VerificationCoverage`의 형태를 바꾸는 일이라 배너·진입점 `§0`·커버리지 맵까지 파급되고, 그것 없이도 §5-3의 다섯 지표는 전부 판정된다. 다음 회차의 첫 항목으로 남긴다. 대조 실행에서 동결이 실제로 결함을 고착시키는 징후(같은 축이 회차 내내 7에 머무름)가 보이면 그때 우선순위를 올린다.

**Type consistency**

- `ErrorCodeAttribution.Attribute`는 `ErrorCodeAttributionResult`를 낸다. Task 7 Step 4가 `.StepCodes`로 읽는다 — 일치.
- `StepFreezeState.OpenSteps`는 `IReadOnlyList<string>?`를 낸다(목차 없음 = null). Task 7 Step 4가 null 검사를 한다 — 일치.
- `StructureRedraftPolicy.TryConsume`이 인자 둘을 받도록 바뀐다. 호출부는 Task 8 Step 6 하나뿐이고 함께 고친다 — 일치.
- `GenerateBatchStepSectionAsync`의 `previousBody`는 `floorFeedback` 뒤·`cancellationToken` 앞에 온다. `IAiService`와 `AiService` 양쪽을 Task 9 Step 3에서 함께 고친다 — 일치.
- `AttachPipelineBanners`의 새 파라미터는 `precomputedMissingCodes`이고 호출부가 `missingErrorCodes`를 넘긴다 — 이름이 다르나 위치 대응이 맞다.
- 생성자 신규 인자 둘(`maxL1RepairAttempts`, `PromptContextScope` 관련)은 모두 **기존 파라미터 뒤에** 붙는다. 앞에 끼우면 위치 인자로 부르는 테스트 40여 개가 조용히 깨진다.
