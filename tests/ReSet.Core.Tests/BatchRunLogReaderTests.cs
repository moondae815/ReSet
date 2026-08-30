using System.Linq;
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
