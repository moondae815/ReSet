using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class PlanAttemptJournalTests : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), $"ReSet-Journal-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private PlanAttemptJournal NewJournal() =>
            PlanAttemptJournal.Create(_root, "Job_Test", "OpenAI", "gpt-4", "high", "C#", "specshash");

        [Fact]
        public void OpenRun_CreatesNumberedRunDirectoryWithManifest()
        {
            var journal = NewJournal();

            journal.OpenRun("## 목차 A", "run-start");

            var dir = journal.CurrentRunDirectory;
            Assert.NotNull(dir);
            Assert.Equal("run-001", Path.GetFileName(dir));

            var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dir!, "manifest.json"))).RootElement;
            Assert.Equal(1, manifest.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(1, manifest.GetProperty("Run").GetInt32());
            Assert.Equal("Job_Test", manifest.GetProperty("Job").GetString());
            Assert.Equal("run-start", manifest.GetProperty("OpenedBy").GetString());

            var key = manifest.GetProperty("ReuseKey");
            Assert.Equal("gpt-4", key.GetProperty("Model").GetString());
            Assert.Equal("specshash", key.GetProperty("SpecsSha256").GetString());
            Assert.Equal(
                PlanAttemptJournal.ComputeSha256("## 목차 A"),
                key.GetProperty("PlanStructureSha256").GetString());
        }

        // 판 경계는 유도하지 않고 디렉터리로 박는다 - L1AttemptLog.ResolveRun 이
        // 시도 번호에서 판을 유도하며 감수한 취약함을 반복하지 않기 위해서다.
        [Fact]
        public void OpenRun_Twice_OpensASecondRunDirectory()
        {
            var journal = NewJournal();

            journal.OpenRun("## 목차 A", "run-start");
            journal.OpenRun("## 목차 B", "structure-redraft");

            Assert.Equal("run-002", Path.GetFileName(journal.CurrentRunDirectory));
            Assert.True(Directory.Exists(Path.Combine(
                _root, "Jobs", "Job_Test", "raw", "attempts", "run-001")));
        }

        // 같은 Job 을 다시 돌린 판이 이어 붙는다 - 앞 판의 번호를 이어받는다.
        [Fact]
        public void Create_AfterAPreviousProcess_ContinuesTheRunNumbering()
        {
            NewJournal().OpenRun("## 목차 A", "run-start");

            var second = NewJournal();
            second.OpenRun("## 목차 A", "run-start");

            Assert.Equal("run-002", Path.GetFileName(second.CurrentRunDirectory));
        }

        // 빈 내용의 해시는 "미계산" sentinel 과 달라야 한다 - 둘 다 "" 면 2단계
        // 재개가 "빈 목차끼리 일치"와 "둘 다 미계산"을 구분 못 한다.
        [Fact]
        public void ComputeSha256_EmptyString_ReturnsTheRealDigestNotASentinel()
        {
            Assert.Equal(
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                PlanAttemptJournal.ComputeSha256(string.Empty));
        }

        // null 은 계산할 내용이 없으므로 미계산을 뜻하는 "" 로 남는다.
        [Fact]
        public void ComputeSha256_Null_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, PlanAttemptJournal.ComputeSha256(null!));
        }

        // 소프트페일 - 관측이 파이프라인을 죽이면 안 된다.
        [Fact]
        public void OpenRun_WhenOutputRootIsAFile_StaysInactiveAndDoesNotThrow()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"ReSet-NotADir-{Guid.NewGuid():N}");
            File.WriteAllText(filePath, "이 경로는 디렉터리가 아니다");
            try
            {
                var journal = PlanAttemptJournal.Create(
                    filePath, "Job_Test", "OpenAI", "gpt-4", null, "C#", "specshash");

                journal.OpenRun("## 목차", "run-start");

                Assert.Null(journal.CurrentRunDirectory);
            }
            finally
            {
                File.Delete(filePath);
            }
        }

        [Fact]
        public void RecordStepSection_WritesTheFileAndIndexesItInTheManifest()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");

            journal.RecordStepSection(attempt: 1, stepCode: "S01", markdown: "S01 본문");

            var dir = journal.CurrentRunDirectory!;
            Assert.Equal("S01 본문", File.ReadAllText(Path.Combine(dir, "steps", "S01.md")));

            var steps = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))
                .RootElement.GetProperty("Steps");
            Assert.Equal(1, steps.GetProperty("S01").GetProperty("Attempt").GetInt32());
            Assert.Equal(
                PlanAttemptJournal.ComputeSha256("S01 본문"),
                steps.GetProperty("S01").GetProperty("Sha256").GetString());
        }

        // 회차 2가 한 단계만 다시 만들면 그 단계만 덮이고 나머지는 회차 1의 것이
        // 그대로 유효하다 - lastStepSections 가 들고 있는 누적 최신 상태와 같다.
        [Fact]
        public void RecordStepSection_OverwritesOnlyTheRewrittenStep()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");
            journal.RecordStepSection(1, "S01", "회차1 S01");
            journal.RecordStepSection(1, "S02", "회차1 S02");

            journal.RecordStepSection(2, "S01", "회차2 S01");

            var dir = journal.CurrentRunDirectory!;
            Assert.Equal("회차2 S01", File.ReadAllText(Path.Combine(dir, "steps", "S01.md")));
            Assert.Equal("회차1 S02", File.ReadAllText(Path.Combine(dir, "steps", "S02.md")));

            var steps = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))
                .RootElement.GetProperty("Steps");
            Assert.Equal(2, steps.GetProperty("S01").GetProperty("Attempt").GetInt32());
            Assert.Equal(1, steps.GetProperty("S02").GetProperty("Attempt").GetInt32());
        }

        // [FINAL FIX - Important 2] 하한 미달 중간본·생성 실패 스텁이 건강한 본문과
        // 같은 (Attempt, Sha256) 모양으로 기록되면, 2단계가 "이 섹션을 재사용해도
        // 되는가"를 manifest만으로 물을 방법이 없다. 결함 종류·사유를 칸으로 남긴다.
        [Fact]
        public void RecordStepSection_WithDefect_RecordsTheDefectKindAndReasonInTheManifest()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");

            journal.RecordStepSection(
                1, "S01", "### S01\n\n> [!WARNING]\n> 이 단계는 생성에 실패했습니다.",
                new StepDefect(StepDefectKind.GenerationFailed, "S01 (생성 실패)"));

            var s01 = JsonDocument.Parse(File.ReadAllText(Path.Combine(journal.CurrentRunDirectory!, "manifest.json")))
                .RootElement.GetProperty("Steps").GetProperty("S01");
            Assert.Equal("GenerationFailed", s01.GetProperty("DefectKind").GetString());
            Assert.Equal("S01 (생성 실패)", s01.GetProperty("DefectReason").GetString());
        }

        // 건강한 본문은 결함 인자를 안 넘긴 기존 호출부 그대로 동작해야 한다 - 그
        // 자리는 여전히 결함이 null이라는 뜻이다.
        [Fact]
        public void RecordStepSection_WithoutDefect_LeavesDefectKindNull()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");

            journal.RecordStepSection(1, "S01", "건강한 본문");

            var s01 = JsonDocument.Parse(File.ReadAllText(Path.Combine(journal.CurrentRunDirectory!, "manifest.json")))
                .RootElement.GetProperty("Steps").GetProperty("S01");
            Assert.Equal(JsonValueKind.Null, s01.GetProperty("DefectKind").ValueKind);
            Assert.Equal(JsonValueKind.Null, s01.GetProperty("DefectReason").ValueKind);
        }

        [Fact]
        public void RecordSkeleton_WritesTheFileAndIndexesIt()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");

            journal.RecordSkeleton(attempt: 2, markdown: "골격 본문");

            var dir = journal.CurrentRunDirectory!;
            Assert.Equal("골격 본문", File.ReadAllText(Path.Combine(dir, "skeleton.md")));

            var skeleton = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))
                .RootElement.GetProperty("Skeleton");
            Assert.Equal(2, skeleton.GetProperty("Attempt").GetInt32());
        }

        // 골격은 시도별이 아니라 "최신 하나" 다(설계서 §4-1) - 회차 2가 다시 만들면
        // 회차 1의 골격은 파일에서도 manifest 에서도 사라져야 한다. RecordStepSection
        // 쪽은 RecordStepSection_OverwritesOnlyTheRewrittenStep 이 이미 잡고 있었다.
        [Fact]
        public void RecordSkeleton_OverwritesWithTheLatestAttempt()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");
            journal.RecordSkeleton(1, "회차1 골격");

            journal.RecordSkeleton(2, "회차2 골격");

            var dir = journal.CurrentRunDirectory!;
            Assert.Equal("회차2 골격", File.ReadAllText(Path.Combine(dir, "skeleton.md")));

            var skeleton = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))
                .RootElement.GetProperty("Skeleton");
            Assert.Equal(2, skeleton.GetProperty("Attempt").GetInt32());
            Assert.Equal(
                PlanAttemptJournal.ComputeSha256("회차2 골격"),
                skeleton.GetProperty("Sha256").GetString());
        }

        // 단계 코드는 모델이 목차에 채운 값이고 BatchStepPlanParser 는 빈 값과 중복만
        // 본다 - 파일명 안전성은 아무도 안 본다. 그 단계만 건너뛰고 판은 살린다.
        [Theory]
        [InlineData("../escape")]
        [InlineData("S01/S02")]
        [InlineData("")]
        [InlineData("S01 S02")]
        public void RecordStepSection_UnsafeCode_SkipsThatStepAndKeepsTheRun(string unsafeCode)
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");
            journal.RecordStepSection(1, "S01", "안전한 본문");

            journal.RecordStepSection(1, unsafeCode, "위험한 본문");

            var dir = journal.CurrentRunDirectory!;
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "steps"), "*.md"));

            var steps = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))
                .RootElement.GetProperty("Steps");
            Assert.Single(steps.EnumerateObject());
        }

        // 판이 안 열렸으면 조용히 아무것도 하지 않는다(Null Object).
        [Fact]
        public void RecordStepSection_WithoutAnOpenRun_DoesNothingAndDoesNotThrow()
        {
            var journal = NewJournal();

            journal.RecordStepSection(1, "S01", "본문");

            Assert.Null(journal.CurrentRunDirectory);
        }

        // 단계 생성은 StepConcurrency 만큼 병렬이다. 잃어버린 갱신이 나면
        // 「섹션 파일은 있는데 manifest 가 모른다」가 된다.
        [Fact]
        public void RecordStepSection_ConcurrentWrites_AllLandInTheManifest()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");

            System.Threading.Tasks.Parallel.For(0, 32, i =>
                journal.RecordStepSection(1, $"S{i:D2}", $"본문 {i}"));

            var steps = JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(journal.CurrentRunDirectory!, "manifest.json")))
                .RootElement.GetProperty("Steps");
            Assert.Equal(32, steps.EnumerateObject().Count());
        }

        // 리뷰는 누적이 아니라 시계열이라 회차마다 별개 파일이다. feedbackHistory 가
        // 최근 3라운드만 들고 있으므로(CriticFeedbackLog.MaxRetainedRounds) 디스크가
        // 메모리보다 오래 기억하는 유일한 자리다.
        [Fact]
        public void RecordReview_WritesOneFilePerAttempt()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");

            journal.RecordReview(1, new ReviewResult
            {
                HasDefects = true,
                FeedbackComment = "S02 의 청크 경계가 최대 키 행을 빠뜨립니다",
                DefectiveSteps = { "S02" },
                ScoreAccuracy = 3, ScoreCrud = 4, ScoreInterface = 5,
                ScoreException = 6, ScoreReadability = 7
            });
            journal.RecordReview(2, new ReviewResult { HasDefects = false });

            var dir = journal.CurrentRunDirectory!;
            var first = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(dir, "reviews", "attempt-01.json"))).RootElement;

            Assert.Equal(1, first.GetProperty("Attempt").GetInt32());
            Assert.True(first.GetProperty("HasDefects").GetBoolean());
            Assert.Equal(
                "S02 의 청크 경계가 최대 키 행을 빠뜨립니다",
                first.GetProperty("FeedbackComment").GetString());
            Assert.Equal("S02", first.GetProperty("DefectiveSteps")[0].GetString());
            Assert.Equal(3, first.GetProperty("ScoreAccuracy").GetInt32());
            Assert.Equal(50, first.GetProperty("NormalizedScore").GetInt32());

            Assert.True(File.Exists(Path.Combine(dir, "reviews", "attempt-02.json")));
        }

        // WritesOneFilePerAttempt 는 축 점수·코멘트만 지목한다. AxisThresholdForced 는
        // "결함 없음" 신고를 오케스트레이터가 축 미달로 뒤집은 유일한 신호이고,
        // SkeletonDefective·StructureDefective 는 재생성 범위(단계 재작성 vs 목차
        // 재설계)를 가르는 값이다 - 셋 다 조용히 빠져도 그 시험은 못 잡는다.
        // ThinkingText 는 반대 방향 대칭 - PlanAttemptReview 의 클래스 주석이
        // "그대로 직렬화하면 수십 KB 로 붇는다"며 일부러 뺀 필드인데, 빼먹었다는
        // 사실 자체를 재는 시험이 없으면 나중에 조용히 되살아나도 아무도 모른다.
        [Fact]
        public void RecordReview_CapturesTheAxisGateFlagsAndOmitsThinkingText()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");

            journal.RecordReview(1, new ReviewResult
            {
                HasDefects = true,
                SkeletonDefective = true,
                StructureDefective = true,
                AxisThresholdForced = true,
                ThinkingText = new string('가', 50_000)
            });

            var raw = File.ReadAllText(Path.Combine(
                journal.CurrentRunDirectory!, "reviews", "attempt-01.json"));
            var review = JsonDocument.Parse(raw).RootElement;

            Assert.True(review.GetProperty("SkeletonDefective").GetBoolean());
            Assert.True(review.GetProperty("StructureDefective").GetBoolean());
            Assert.True(review.GetProperty("AxisThresholdForced").GetBoolean());
            Assert.DoesNotContain("ThinkingText", raw);
        }

        [Fact]
        public void RecordReview_WithoutAnOpenRun_DoesNothingAndDoesNotThrow()
        {
            var journal = NewJournal();

            journal.RecordReview(1, new ReviewResult { HasDefects = false });

            Assert.Null(journal.CurrentRunDirectory);
        }

        // review == null 가드는 한 줄이라 지워도 겉으로는 안 죽는다 - RecordReview
        // 본문이 일반 catch 로 NullReferenceException 을 삼키기 때문이다(소프트페일).
        // 다만 가드 없이 지나가면 review.HasDefects 를 읽기 전에 이미
        // Directory.CreateDirectory(reviewsDir) 를 실행해 빈 reviews/ 디렉터리를
        // 남긴다 - ComputeSha256 의 sentinel 분기(리뷰 라운드 1)와 같은 모양의
        // "지워도 통과"를 이 자리에서 막는다.
        [Fact]
        public void RecordReview_WithNullReview_DoesNothingAndDoesNotCreateReviewsDirectory()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");

            journal.RecordReview(1, null!);

            Assert.False(Directory.Exists(
                Path.Combine(journal.CurrentRunDirectory!, "reviews")));
        }

        /// <summary>
        /// 저널이 쓰는 파일에 UTF-8 BOM 이 붙으면 안 된다.
        ///
        /// [왜 바이트로 재는가 - 2026-09-11 실물에서 났다] C# 은 <c>File.ReadAllText</c> 가
        /// BOM 을 자동으로 벗겨 읽으므로 <b>왕복 시험으로는 안 잡힌다.</b> 이 클래스의
        /// 다른 시험 스물둘이 전부 통과하는 동안 실물 판(`POQSettleBatch7`)의 파일
        /// <b>25개 전부</b>에 BOM 이 붙어 있었고, python <c>json</c> 이
        /// 「Unexpected UTF-8 BOM」으로 거부해서야 드러났다. 검사가 보는 통로가 결함을
        /// 가린 자리다 — 그래서 여기서는 파일을 <b>바이트로</b> 연다.
        ///
        /// manifest 와 리뷰는 JSON 이라 저장소 밖 도구(jq·승격 스크립트)가 읽고, 섹션은
        /// 2단계가 본문 중간에 이어 붙인다 — 어느 쪽도 BOM 을 견디지 못한다. 자매 클래스
        /// <c>L1AttemptLog</c> 는 인코딩 인자를 안 줘 기본값(BOM 없음)을 쓴다.
        /// </summary>
        [Fact]
        public void EveryWrittenFile_HasNoUtf8Bom()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");
            journal.RecordSkeleton(1, "골격 본문");
            journal.RecordStepSection(1, "S01", "섹션 본문");
            journal.RecordReview(1, new ReviewResult { HasDefects = false });

            var files = Directory
                .EnumerateFiles(journal.CurrentRunDirectory!, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            // 판에 파일이 없으면 이 시험은 아무것도 안 재고 초록이 된다.
            Assert.Equal(4, files.Count);

            foreach (var path in files)
            {
                var head = new byte[3];
                int read;
                using (var stream = File.OpenRead(path)) read = stream.Read(head, 0, 3);

                var hasBom = read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                Assert.False(hasBom,
                    $"{Path.GetFileName(path)} 에 UTF-8 BOM 이 붙었다 — " +
                    "저장소 밖 도구(python json·jq)가 거부하고 섹션은 이어 붙일 때 본문 중간에 낀다.");
            }
        }
    }
}
