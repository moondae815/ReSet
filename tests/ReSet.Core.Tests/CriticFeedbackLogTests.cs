using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class CriticFeedbackLogTests
    {
        private static ReviewResult Review(string comment) => new()
        {
            FeedbackComment = comment,
            ScoreAccuracy = 10,
            ScoreCrud = 9,
            ScoreInterface = 9,
            ScoreReadability = 10,
            ScoreException = 7
        };

        // Actor는 지금까지 어느 항목이 미달인지 몰랐다. 산문 피드백만 받았다.
        [Fact]
        public void Record_EmbedsThePerDimensionScoresAndThreshold()
        {
            var history = new List<string>();

            CriticFeedbackLog.Record(history, 2, Review("예외 처리를 보완하십시오."), 8);

            var entry = Assert.Single(history);
            Assert.Contains("시도 2", entry);
            Assert.Contains("정합성 10", entry);
            Assert.Contains("CRUD 9", entry);
            Assert.Contains("인터페이스 9", entry);
            Assert.Contains("가독성 10", entry);
            Assert.Contains("예외 7", entry);
            Assert.Contains("기준 8", entry);
            Assert.Contains("예외 처리를 보완하십시오.", entry);
        }

        // 이전 라운드 지적이 유실되면 Actor가 같은 오류를 다시 만든다.
        [Fact]
        public void Record_AccumulatesAcrossRoundsInsteadOfReplacing()
        {
            var history = new List<string>();

            CriticFeedbackLog.Record(history, 1, Review("1차 지적"), 8);
            CriticFeedbackLog.Record(history, 2, Review("2차 지적"), 8);

            Assert.Equal(2, history.Count);
            Assert.Contains("1차 지적", history[0]);
            Assert.Contains("2차 지적", history[1]);
        }

        [Fact]
        public void Record_DropsTheOldestBeyondTheRetentionCap()
        {
            var history = new List<string>();

            CriticFeedbackLog.Record(history, 1, Review("1차 지적"), 8);
            CriticFeedbackLog.Record(history, 2, Review("2차 지적"), 8);
            CriticFeedbackLog.Record(history, 3, Review("3차 지적"), 8);
            CriticFeedbackLog.Record(history, 4, Review("4차 지적"), 8);

            Assert.Equal(CriticFeedbackLog.MaxRetainedRounds, history.Count);
            Assert.DoesNotContain(history, entry => entry.Contains("1차 지적"));
            Assert.Contains(history, entry => entry.Contains("4차 지적"));
        }

        [Fact]
        public void Compose_JoinsEveryRetainedRoundAndAppendsTheInstruction()
        {
            var history = new List<string>();
            CriticFeedbackLog.Record(history, 1, Review("1차 지적"), 8);
            CriticFeedbackLog.Record(history, 2, Review("2차 지적"), 8);

            var composed = CriticFeedbackLog.Compose(history, "※ 지시사항: 테스트 지시");

            Assert.Contains("1차 지적", composed);
            Assert.Contains("2차 지적", composed);
            Assert.Contains("※ 지시사항: 테스트 지시", composed);
        }
    }
}
