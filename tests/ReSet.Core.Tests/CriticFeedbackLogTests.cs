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

        // 아직 L2 라운드가 없으면 붙일 누적이 없다. 가장 흔한 경우의 프롬프트가
        // 오늘과 달라지면 안 되므로 L1 지시를 그대로 돌려준다.
        [Fact]
        public void ComposeAfterL1Failure_WithEmptyHistory_ReturnsTheL1FixVerbatim()
        {
            var composed = CriticFeedbackLog.ComposeAfterL1Failure("표 축약어를 제거하십시오.", new List<string>());

            Assert.Equal("표 축약어를 제거하십시오.", composed);
        }

        // Actor는 매번 백지에서 다시 쓴다. L1 지시만 보내면 그 회차는 내용 교정 이력이
        // 전부 빠진 채 생성된다. 이전 구현이 실제로 그랬다.
        [Fact]
        public void ComposeAfterL1Failure_KeepsTheAccumulatedCriticFeedbackBehindTheL1Fix()
        {
            var history = new List<string>();
            CriticFeedbackLog.Record(history, 1, Review("조인 서술을 고치십시오."), 8);
            CriticFeedbackLog.Record(history, 2, Review("NOLOCK 영향을 보완하십시오."), 8);

            var composed = CriticFeedbackLog.ComposeAfterL1Failure("표 축약어를 제거하십시오.", history);

            Assert.StartsWith("[L1 기계 검증 오류", composed);
            Assert.Contains("표 축약어를 제거하십시오.", composed);
            Assert.Contains("[L2 AI 리뷰 누적 피드백 (최근 2개 라운드)]", composed);
            Assert.Contains("조인 서술을 고치십시오.", composed);
            Assert.Contains("NOLOCK 영향을 보완하십시오.", composed);
            Assert.Contains("위 형식 오류를 먼저 해소하고", composed);
        }
    }
}
