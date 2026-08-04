using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class BestAttemptTests
    {
        // 2026-08-04 dbo.UP_Util_PG_Client_CMRate_Ins 실행에서 실제로 나온 세 시도의 점수.
        // 파이프라인은 마지막(78점)을 채택했고 90점짜리를 버렸다.
        private static ReviewResult Attempt1() => new()
        { ScoreAccuracy = 8, ScoreCrud = 8, ScoreInterface = 7, ScoreReadability = 5, ScoreException = 7 };   // 70

        private static ReviewResult Attempt2() => new()
        { ScoreAccuracy = 10, ScoreCrud = 9, ScoreInterface = 9, ScoreReadability = 10, ScoreException = 7 }; // 90

        private static ReviewResult Attempt3() => new()
        { ScoreAccuracy = 8, ScoreCrud = 9, ScoreInterface = 6, ScoreReadability = 7, ScoreException = 9 };   // 78

        [Fact]
        public void NoCandidateRecorded_ExposesEmptyState()
        {
            var best = new BestAttempt();

            Assert.False(best.HasCandidate);
            Assert.Null(best.Markdown);
            Assert.Null(best.Review);
        }

        [Fact]
        public void FirstCandidate_IsAlwaysRecorded()
        {
            var best = new BestAttempt();

            Assert.True(best.TryRecord(1, "문서1", Attempt1()));
            Assert.True(best.HasCandidate);
            Assert.Equal("문서1", best.Markdown);
            Assert.Equal(1, best.AttemptNumber);
        }

        [Fact]
        public void HigherScore_ReplacesTheCurrentBest()
        {
            var best = new BestAttempt();
            best.TryRecord(1, "문서1", Attempt1());

            Assert.True(best.TryRecord(2, "문서2", Attempt2()));
            Assert.Equal("문서2", best.Markdown);
            Assert.Equal(2, best.AttemptNumber);
            Assert.Equal(90, best.Review!.NormalizedScore);
        }

        // 이번 사고의 핵심. 78점짜리가 90점짜리를 밀어내면 안 된다.
        [Fact]
        public void LowerScore_DoesNotReplaceTheCurrentBest()
        {
            var best = new BestAttempt();
            best.TryRecord(1, "문서1", Attempt1());
            best.TryRecord(2, "문서2", Attempt2());

            Assert.False(best.TryRecord(3, "문서3", Attempt3()));
            Assert.Equal("문서2", best.Markdown);
            Assert.Equal(2, best.AttemptNumber);
        }

        // 나중 시도가 더 낫다는 근거가 없고, 실제로 후속 시도가 다른 축을 망가뜨렸다.
        [Fact]
        public void EqualScore_KeepsTheEarlierAttempt()
        {
            var best = new BestAttempt();
            best.TryRecord(1, "문서1", Attempt2());

            Assert.False(best.TryRecord(2, "문서2", Attempt2()));
            Assert.Equal("문서1", best.Markdown);
            Assert.Equal(1, best.AttemptNumber);
        }
    }
}
