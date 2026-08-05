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

            Assert.Null(best.Current);
        }

        [Fact]
        public void FirstCandidate_IsAlwaysRecorded()
        {
            var best = new BestAttempt();

            Assert.True(best.TryRecord(1, "문서1", Attempt1()));
            Assert.NotNull(best.Current);
            Assert.Equal("문서1", best.Current!.Markdown);
            Assert.Equal(1, best.Current.AttemptNumber);
            Assert.Equal(70, best.Current.Review.NormalizedScore);
        }

        [Fact]
        public void HigherScore_ReplacesTheCurrentBest()
        {
            var best = new BestAttempt();
            best.TryRecord(1, "문서1", Attempt1());

            Assert.True(best.TryRecord(2, "문서2", Attempt2()));
            Assert.Equal("문서2", best.Current!.Markdown);
            Assert.Equal(2, best.Current.AttemptNumber);
            Assert.Equal(90, best.Current.Review.NormalizedScore);
        }

        // 이번 사고의 핵심. 78점짜리가 90점짜리를 밀어내면 안 된다.
        [Fact]
        public void LowerScore_DoesNotReplaceTheCurrentBest()
        {
            var best = new BestAttempt();
            best.TryRecord(1, "문서1", Attempt1());
            best.TryRecord(2, "문서2", Attempt2());

            Assert.False(best.TryRecord(3, "문서3", Attempt3()));
            Assert.Equal("문서2", best.Current!.Markdown);
            Assert.Equal(2, best.Current.AttemptNumber);
        }

        // 나중 시도가 더 낫다는 근거가 없고, 실제로 후속 시도가 다른 축을 망가뜨렸다.
        [Fact]
        public void EqualScore_KeepsTheEarlierAttempt()
        {
            var best = new BestAttempt();
            best.TryRecord(1, "문서1", Attempt2());

            Assert.False(best.TryRecord(2, "문서2", Attempt2()));
            Assert.Equal("문서1", best.Current!.Markdown);
            Assert.Equal(1, best.Current.AttemptNumber);
        }

        // 네 값이 한 덩어리로 움직인다 — 하나만 갱신되어 어긋날 자리가 없다.
        [Fact]
        public void Current_CarriesEveryValueOfTheSameAttempt()
        {
            var best = new BestAttempt();
            best.TryRecord(2, "문서2", Attempt2());

            var candidate = best.Current;

            Assert.NotNull(candidate);
            Assert.Equal("문서2", candidate!.Markdown);
            Assert.Equal(2, candidate.AttemptNumber);
            Assert.Equal(90, candidate.Review.NormalizedScore);
        }
    }
}
