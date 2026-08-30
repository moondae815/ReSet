using System;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BatchRunLogReaderTests
    {
        // POQSettleBatch4(2026-08-29) 실측 로그의 형태를 그대로 축약한 픽스처.
        // 회차별 점수 78 -> 76 -> (L1 실패) -> (L1 실패) -> 84 -> 74 를 재현한다.
        //
        // [2026-08-30 수정 - Fix Round 1] 실제 로그는 앵커 바로 뒤에 "리뷰 응답 수신
        // 완료" 앵커와 다른 타임스탬프를 가진 `[DBG] [AI 응답 내용]:` 줄이 낀 뒤에야
        // JSON 본문이 시작하고, 같은 점수 블록이 `[추출된 JSON 내용]`으로 한 번 더
        // 실린다(회차당 두 번). 이전 픽스처는 이 형태를 생략해서, 9개 테스트가 전부
        // 통과하는 동안 실물 로그에서는 리더가 0% 작동했다 - 리뷰가 이 사고를 잡았다.
        private const string Batch4Shape = """
            2026-08-29 14:15:34.321 +09:00 [INF] POQSettleBatch4 - AI 통합 배치 전환 계획 수립 중 [[1차 분석]]...
            2026-08-29 14:17:26.447 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 238908, 캐시 읽기: 0, 출력: 7544, 추론: 미보고
            2026-08-29 14:50:19.405 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 2802
            2026-08-29 14:50:19.406 +09:00 [DBG] [AI 응답 내용]:
            {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S02"],
              "ScoreAccuracy": 6,
              "ScoreCrud": 9,
              "ScoreInterface": 7,
              "ScoreException": 7,
              "ScoreReadability": 10
            }
            2026-08-29 14:50:19.410 +09:00 [DBG] [추출된 JSON 내용]: {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S02"],
              "ScoreAccuracy": 6,
              "ScoreCrud": 9,
              "ScoreInterface": 7,
              "ScoreException": 7,
              "ScoreReadability": 10
            }
            2026-08-29 14:50:19.552 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 1/6): 결함
            2026-08-29 14:51:46.828 +09:00 [INF] claude-cli 토큰 사용량 - 입력: 2, 캐시 쓰기: 251376, 캐시 읽기: 8518, 출력: 11160, 추론: 미보고
            2026-08-29 15:06:38.299 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 1482
            2026-08-29 15:06:38.299 +09:00 [DBG] [AI 응답 내용]:
            ```json
            {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S06"],
              "ScoreAccuracy": 6,
              "ScoreCrud": 8,
              "ScoreInterface": 6,
              "ScoreException": 9,
              "ScoreReadability": 9
            }
            ```
            2026-08-29 15:06:38.299 +09:00 [DBG] [추출된 JSON 내용]: {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S06"],
              "ScoreAccuracy": 6,
              "ScoreCrud": 8,
              "ScoreInterface": 6,
              "ScoreException": 9,
              "ScoreReadability": 9
            }
            2026-08-29 15:06:38.490 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 2/6): 결함
            2026-08-29 15:06:38.491 +09:00 [INF] POQSettleBatch4 - 재시도가 점수를 개선하지 못해 목차를 다시 설계합니다...
            2026-08-29 15:30:27.712 +09:00 [WRN] [POQSettleBatch4] L1 기계 검증 오류 발견 (시도 3/6):
            2026-08-29 15:51:52.668 +09:00 [WRN] [POQSettleBatch4] L1 기계 검증 오류 발견 (시도 4/6):
            2026-08-29 16:24:53.100 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 3000
            2026-08-29 16:24:53.101 +09:00 [DBG] [AI 응답 내용]:
            {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S09"],
              "ScoreAccuracy": 8,
              "ScoreCrud": 9,
              "ScoreInterface": 9,
              "ScoreException": 7,
              "ScoreReadability": 9
            }
            2026-08-29 16:24:53.105 +09:00 [DBG] [추출된 JSON 내용]: {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S09"],
              "ScoreAccuracy": 8,
              "ScoreCrud": 9,
              "ScoreInterface": 9,
              "ScoreException": 7,
              "ScoreReadability": 9
            }
            2026-08-29 16:24:53.134 +09:00 [WRN] [POQSettleBatch4] L2 AI 리뷰 결함 발견 (시도 5/6): 결함
            2026-08-29 16:38:45.600 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료 - JobName: POQSettleBatch4, 응답 길이: 2500
            2026-08-29 16:38:45.601 +09:00 [DBG] [AI 응답 내용]:
            {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S11"],
              "ScoreAccuracy": 7,
              "ScoreCrud": 8,
              "ScoreInterface": 7,
              "ScoreException": 6,
              "ScoreReadability": 9
            }
            2026-08-29 16:38:45.605 +09:00 [DBG] [추출된 JSON 내용]: {
              "HasDefects": true,
              "FeedbackComment": "결함",
              "DefectiveSteps": ["S11"],
              "ScoreAccuracy": 7,
              "ScoreCrud": 8,
              "ScoreInterface": 7,
              "ScoreException": 6,
              "ScoreReadability": 9
            }
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

        // 실물 로그는 같은 점수 블록을 [AI 응답 내용]과 [추출된 JSON 내용]으로 두 번
        // 싣는다. 회차당 궤적 항목은 하나여야 한다 - 둘 다 채택하면 궤적 길이가
        // 두 배가 되어 단조성 위반 셈이 통째로 어긋난다.
        [Fact]
        public void Read_DuplicateJsonBlockPerResponse_CountsOnce()
        {
            var metrics = BatchRunLogReader.Read(Batch4Shape);

            Assert.Equal(4, metrics.Trajectory.Count);
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

        // 실측 로그 210번째 줄 부근: few-shot 프롬프트 템플릿이 실제 응답과 똑같은
        // "ScoreAccuracy": 10 ... "ScoreReadability": 10 5-튜플을 싣는다. 이 텍스트
        // 앞에는 어떤 타임스탬프도, 앵커도 없다 - "앵커를 보기 전에는 절대 collecting
        // 상태에 들어가지 않는다"가 우연이 아니라 구조적 성질임을 이 실물 형태로 고정한다.
        [Fact]
        public void Read_IgnoresFewShotTemplateWithoutAnyPrecedingAnchor()
        {
            var template = """
                [Output Format]
                Output ONLY the final JSON payload. Do not include markdown block markers (```json) or conversational text. Output raw JSON:
                {
                  "HasDefects": true or false (boolean),
                  "FeedbackComment": "Detailed correction instructions if defects are found. Return empty string if HasDefects is false.",
                  "DefectiveSteps": ["S08", "S10"],
                  "ScoreAccuracy": 10,
                  "ScoreCrud": 10,
                  "ScoreInterface": 10,
                  "ScoreException": 10,
                  "ScoreReadability": 10
                }
                """;

            var metrics = BatchRunLogReader.Read(template);

            Assert.Empty(metrics.Trajectory);
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

        // "동점은 위반이 아니다"라는 CountMonotonicityViolations 주석이 실제로 지켜지는지
        // 고정한다. 엄격 부등호(<)를 (<=)로 잘못 고치면 이 테스트가 잡는다.
        [Fact]
        public void Read_TiedScore_IsNotAViolation()
        {
            var tied = """
                2026-08-29 10:00:00.000 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료
                2026-08-29 10:00:00.001 +09:00 [DBG] [AI 응답 내용]:
                {
                  "ScoreAccuracy": 8,
                  "ScoreCrud": 8,
                  "ScoreInterface": 8,
                  "ScoreException": 8,
                  "ScoreReadability": 8
                }
                2026-08-29 10:05:00.000 +09:00 [INF] AI 통합 배치 계획서 리뷰 응답 수신 완료
                2026-08-29 10:05:00.001 +09:00 [DBG] [AI 응답 내용]:
                {
                  "ScoreAccuracy": 8,
                  "ScoreCrud": 8,
                  "ScoreInterface": 8,
                  "ScoreException": 8,
                  "ScoreReadability": 8
                }
                """;

            var metrics = BatchRunLogReader.Read(tied);

            Assert.Equal(new[] { 80, 80 }, metrics.Trajectory.Select(a => a.NormalizedScore).ToArray());
            Assert.Equal(0, metrics.MonotonicityViolations);
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

        // 기준선 검증(코디네이터 실측 오라클, 2026-08-30). Skip하지 않는다 - 재료가
        // 없으면 그 사실이 실패로 드러나야 한다(Skip은 이 저장소가 반복해 데인 조용한
        // 실패 양식이다). 저장소 루트는 CorpusPaths.RepoRoot()의 조상 탐색 관례를
        // 그대로 쓴다 - 이 통제군 로그 트리는 output/·output.bak-2026-08-22/와 같은
        // 방식으로 워크트리에 심링크해 둔다.
        [Fact]
        public void Read_Batch4BaselineLog()
        {
            var root = CorpusPaths.RepoRoot();
            var path = Path.Combine(root, "output.bak-stage4-control-20260828", "logs-batch4", "reset-20260829.log");

            Assert.True(File.Exists(path),
                $"통제군 기준선 로그가 없습니다: '{path}'. " +
                "output.bak-stage4-control-20260828/를 output/·output.bak-2026-08-22/와 같은 방식으로 심링크했는지 확인하십시오.");

            var metrics = BatchRunLogReader.Read(File.ReadAllText(path));

            Assert.Equal(new[] { 78, 76, 84, 74 }, metrics.Trajectory.Select(a => a.NormalizedScore).ToArray());
            Assert.Equal(2, metrics.MonotonicityViolations);
            Assert.Equal(2, metrics.L1ExhaustedAttempts);
            Assert.Equal(6, metrics.TotalAttempts);
            Assert.Equal(24_065_539, metrics.CacheWriteTokens);
            Assert.Equal(775_702, metrics.CacheReadTokens);
            Assert.Equal(2_054_632, metrics.OutputTokens);
            Assert.NotNull(metrics.WallClock);
            Assert.Equal(new TimeSpan(2, 23, 26), metrics.WallClock!.Value);
            Assert.True(metrics.StructureRedrafted);
        }
    }
}
