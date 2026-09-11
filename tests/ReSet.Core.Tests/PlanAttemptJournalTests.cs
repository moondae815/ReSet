using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    // Task 7 시험들이 전역 Serilog.Log.Logger 를 갈아 끼운다 - GlobalSerilogLoggerCollection.cs
    // 의 규칙대로 이 컬렉션에 들어간다.
    [Collection(GlobalSerilogLoggerCollection.Name)]
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

        /// <summary>
        /// 재개 후보를 만드는 헬퍼 — 판 하나를 완성된 모양으로 써 둔다.
        /// 골격 1개 · 건강한 섹션 2개 · 결함 섹션 1개 · 리뷰 2개.
        /// </summary>
        private PlanAttemptJournal WriteResumableRun(string planStructure = "## 목차 A")
        {
            var journal = NewJournal();
            journal.OpenRun(planStructure, "run-start");
            journal.RecordSkeleton(1, "골격 본문");
            journal.RecordStepSection(1, "S01", "S01 건강");
            journal.RecordStepSection(1, "S02", "S02 건강");
            journal.RecordStepSection(2, "S03", "S03 미달",
                new StepDefect(StepDefectKind.QualityFloor, "S03 (하한 미달: 이유)"));
            journal.RecordReview(1, new ReviewResult
            {
                HasDefects = true, FeedbackComment = "회차 1 지적",
                ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5,
                ScoreException = 5, ScoreReadability = 5
            });
            journal.RecordReview(2, new ReviewResult
            {
                HasDefects = true, FeedbackComment = "회차 2 지적",
                ScoreAccuracy = 7, ScoreCrud = 7, ScoreInterface = 7,
                ScoreException = 7, ScoreReadability = 7
            });
            return journal;
        }

        [Fact]
        public void TryResume_FindsTheRunAndSplitsHealthyFromDefective()
        {
            WriteResumableRun();

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.Equal(1, candidate!.Run);
            Assert.Equal("골격 본문", candidate.Skeleton);
            Assert.Equal(1, candidate.SkeletonAttempt);

            // DefectKind 가 있는 S03 은 재사용 대상이 아니다 — 다시 만들 목록으로 간다.
            Assert.Equal(new[] { "S01", "S02" }, candidate.ReusableSections.Keys.OrderBy(k => k).ToArray());
            Assert.Equal("S01 건강", candidate.ReusableSections["S01"]);
            Assert.Equal(new[] { "S03" }, candidate.DefectiveStepCodes.ToArray());
            Assert.Equal(3, candidate.TotalStepsInManifest);

            // 원래 Attempt 를 그대로 옮긴다(설계 §3-5) — 새 판에 다시 기록할 때 쓴다.
            Assert.Equal(1, candidate.SectionAttempts["S01"]);

            // 리뷰는 시도 번호가 큰 순으로 최근 3개까지(설계 §3-3).
            Assert.Equal(new[] { 2, 1 }, candidate.PriorReviews.Select(r => r.Attempt).ToArray());
            Assert.Equal("회차 2 지적", candidate.PriorReviews[0].Review.FeedbackComment);
        }

        // 일곱 항목 각각을 재야 한다 — 하나라도 안 재면 그 항목은 무방비다(설계 §5-1).
        // ContractVersion 은 const 라 이 Theory 로 못 바꾼다 — 바로 아래 별도 시험이 맡는다.
        [Theory]
        [InlineData("planStructure")]
        [InlineData("specsSha")]
        [InlineData("provider")]
        [InlineData("model")]
        [InlineData("effort")]
        [InlineData("targetLanguage")]
        public void TryResume_WhenAnyReuseKeyItemDiffers_FindsNothing(string differing)
        {
            WriteResumableRun();

            var journal = PlanAttemptJournal.Create(
                _root, "Job_Test",
                differing == "provider" ? "OtherProvider" : "OpenAI",
                differing == "model" ? "other-model" : "gpt-4",
                differing == "effort" ? "low" : "high",
                differing == "targetLanguage" ? "Java" : "C#",
                differing == "specsSha" ? "otherspecshash" : "specshash");

            var candidate = journal.TryResume(differing == "planStructure" ? "## 다른 목차" : "## 목차 A");

            Assert.Null(candidate);
        }

        /// <summary>
        /// 일곱 번째 항목. <c>PlanAttemptJournal.ContractVersion</c> 은 <c>const</c> 라
        /// 위 Theory 로 바꿀 수 없다 — manifest 를 직접 고쳐 「옛 계약으로 쓰인 판」을 만든다.
        ///
        /// <b>이 시험이 없으면 일곱 번째 항목이 무방비다.</b> 계약 버전이 올라간 뒤에도
        /// 옛 판을 주워 와, 새 규약을 안 지키는 섹션이 문서에 섞인다.
        /// </summary>
        [Fact]
        public void TryResume_WhenTheContractVersionDiffers_FindsNothing()
        {
            var journal = WriteResumableRun();
            var manifestPath = Path.Combine(journal.CurrentRunDirectory!, "manifest.json");

            var text = File.ReadAllText(manifestPath);
            var bumped = text.Replace(
                $"\"ContractVersion\": {PlanAttemptJournal.ContractVersion}",
                $"\"ContractVersion\": {PlanAttemptJournal.ContractVersion + 1}");
            Assert.NotEqual(text, bumped);          // 치환이 실제로 일어났는지 먼저 확인한다
            File.WriteAllText(manifestPath, bumped);

            Assert.Null(NewJournal().TryResume("## 목차 A"));
        }

        // 해시가 안 맞는 항목은 버린다 — 사람이 손댔거나 반쯤 쓰였다는 뜻이다(설계 §3-2).
        [Fact]
        public void TryResume_WhenASectionsHashDoesNotMatch_DropsThatSectionOnly()
        {
            var journal = WriteResumableRun();
            var dir = journal.CurrentRunDirectory!;
            File.WriteAllText(Path.Combine(dir, "steps", "S01.md"), "누군가 손댄 본문");

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.False(candidate!.ReusableSections.ContainsKey("S01"));
            Assert.True(candidate.ReusableSections.ContainsKey("S02"));
        }

        // 골격도 섹션도 없는 판은 재개 불가다(설계 §11-5 — 단일 호출 폴백 판).
        [Fact]
        public void TryResume_WhenTheRunHasNoSkeletonAndNoSections_FindsNothing()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차 A", "run-start");
            journal.RecordReview(1, new ReviewResult { HasDefects = false });

            Assert.Null(NewJournal().TryResume("## 목차 A"));
        }

        [Fact]
        public void TryResume_WhenSeveralRunsMatch_PicksTheNewest()
        {
            WriteResumableRun();                 // run-001
            var second = NewJournal();
            second.OpenRun("## 목차 A", "run-start");
            second.RecordSkeleton(1, "둘째 판 골격");
            second.RecordStepSection(1, "S01", "둘째 판 S01");

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.Equal(2, candidate!.Run);
            Assert.Equal("둘째 판 골격", candidate.Skeleton);
        }

        [Fact]
        public void TryResume_WhenNoRunExists_FindsNothing()
        {
            Assert.Null(NewJournal().TryResume("## 목차 A"));
        }

        // [대칭 훑기] 계획서가 준 시험은 섹션의 Sha256 불일치만 잰다(위 시험). 같은 규칙이
        // 골격에도 §3-2 대로 적용돼야 한다 — 안 재면 골격만 손댔을 때 무방비다. 골격이
        // 버려져도 건강한 섹션이 있으면 후보는 여전히 non-null 이어야 한다(§11-5 는
        // "둘 다 없을 때"만 제외한다).
        [Fact]
        public void TryResume_WhenTheSkeletonsHashDoesNotMatch_DropsTheSkeletonButKeepsSections()
        {
            var journal = WriteResumableRun();
            var dir = journal.CurrentRunDirectory!;
            File.WriteAllText(Path.Combine(dir, "skeleton.md"), "누군가 손댄 골격");

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.Equal(string.Empty, candidate!.Skeleton);
            Assert.Equal(0, candidate.SkeletonAttempt);
            Assert.True(candidate.ReusableSections.ContainsKey("S01"));
        }

        // [대칭 훑기] 골격 파일이 통째로 사라진 경우(해시 불일치와 다른 경로 —
        // File.Exists 분기)도 같은 결과여야 한다. steps 쪽은 이미
        // RecordStepSection_UnsafeCode 류로 파일 부재를 다루지만 TryResume 경로에서는
        // 안 재고 있었다.
        [Fact]
        public void TryResume_WhenTheSkeletonFileIsMissing_DropsTheSkeletonButKeepsSections()
        {
            var journal = WriteResumableRun();
            var dir = journal.CurrentRunDirectory!;
            File.Delete(Path.Combine(dir, "skeleton.md"));

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.Equal(string.Empty, candidate!.Skeleton);
            Assert.True(candidate.ReusableSections.ContainsKey("S01"));
        }

        // [대칭 훑기] 골격이 아예 기록된 적 없어도(manifest.Skeleton == null) 건강한
        // 섹션만으로 재개 후보가 되어야 한다 — §11-5 는 "골격도 섹션도 없을 때"만
        // 제외하지 "골격이 없을 때"를 제외하지 않는다. 주어진 시험들은 골격·섹션이
        // 둘 다 있거나(WriteResumableRun) 둘 다 없는 경우만 쟀다.
        [Fact]
        public void TryResume_WhenOnlySectionsExistWithNoSkeletonEverRecorded_StillFindsTheRun()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차 A", "run-start");
            journal.RecordStepSection(1, "S01", "S01 건강");

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.Equal(string.Empty, candidate!.Skeleton);
            Assert.Equal(0, candidate.SkeletonAttempt);
            Assert.True(candidate.ReusableSections.ContainsKey("S01"));
        }

        // [대칭 훑기] 설계 §3-3 은 "시도 번호가 큰 순으로 최근 3개"라고 못박고
        // CriticFeedbackLog.MaxRetainedRounds(3) 와 같은 상한이어야 한다고 적었다.
        // 주어진 시험은 리뷰 2개만 써서 상한에 못 미친다 — 4개를 쌓아 3개로 잘리는지,
        // 그리고 잘리는 것이 "오래된" 1회차인지(최근 3개 = 2,3,4)를 직접 잰다.
        [Fact]
        public void TryResume_WhenMoreReviewsThanMaxRetainedRoundsExist_KeepsOnlyTheMostRecentThree()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차 A", "run-start");
            journal.RecordStepSection(1, "S01", "S01 건강");
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                journal.RecordReview(attempt, new ReviewResult
                {
                    HasDefects = true, FeedbackComment = $"회차 {attempt} 지적",
                    ScoreAccuracy = 5, ScoreCrud = 5, ScoreInterface = 5,
                    ScoreException = 5, ScoreReadability = 5
                });
            }

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.Equal(CriticFeedbackLog.MaxRetainedRounds, candidate!.PriorReviews.Count);
            Assert.Equal(new[] { 4, 3, 2 }, candidate.PriorReviews.Select(r => r.Attempt).ToArray());
        }

        // [FIX ROUND 1 - Important 1] 최신 판의 manifest.json 이 깨진 JSON 이어도
        // TryResume 은 그 판만 포기해야 한다 — foreach 전체가 끊겨 더 오래된 건강한
        // 판까지 못 찾으면 안 된다. run-002(깨진 manifest)가 run-001(재사용 가능)보다
        // 최신이라 스캔 순서상 먼저 걸린다.
        [Fact]
        public void TryResume_WhenTheNewestRunsManifestIsCorrupt_StillFindsAnOlderHealthyRun()
        {
            var journal = WriteResumableRun();               // run-001, 재사용 가능
            var attemptsRoot = Path.GetDirectoryName(journal.CurrentRunDirectory!)!;
            var corruptRunDir = Path.Combine(attemptsRoot, "run-002");
            Directory.CreateDirectory(corruptRunDir);
            File.WriteAllText(Path.Combine(corruptRunDir, "manifest.json"), "{ 이건 유효한 JSON 이 아니다");

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.Equal(1, candidate!.Run);
            Assert.Equal("골격 본문", candidate.Skeleton);
        }

        // [FIX ROUND 1 - Minor] ReadPriorReviews 의 "리뷰 파일 하나가 깨져도 나머지가
        // 산다"는 동작(파일별 try/catch)은 코드로는 맞았지만 이를 직접 재는 시험이
        // 없었다 - 무방비였다. attempt-01.json 을 깨뜨려도 attempt-02.json 의 리뷰는
        // 남아야 한다.
        [Fact]
        public void TryResume_WhenOneReviewFileIsCorrupt_TheOtherReviewsSurvive()
        {
            var journal = WriteResumableRun();                // reviews: attempt-01, attempt-02
            var reviewsDir = Path.Combine(journal.CurrentRunDirectory!, "reviews");
            File.WriteAllText(Path.Combine(reviewsDir, "attempt-01.json"), "{ 이건 유효한 JSON 이 아니다");

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.Single(candidate!.PriorReviews);
            Assert.Equal(2, candidate.PriorReviews[0].Attempt);
            Assert.Equal("회차 2 지적", candidate.PriorReviews[0].Review.FeedbackComment);
        }

        // [FIX ROUND 1 - Task 2 리뷰가 지목한 근본 원인] 설계 §3-4 는 화면에 다시 만들
        // 단계의 "사유"까지 보이라고 요구한다. DefectKind 가 manifest 에 실제로 있는
        // 단계(S03·QualityFloor)는 그 종류가 그대로 옮겨져야 한다.
        [Fact]
        public void TryResume_DefectiveStepKinds_CarriesTheManifestsDefectKindForActualDefects()
        {
            WriteResumableRun();       // S03 은 QualityFloor 로 기록된다

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.True(candidate!.DefectiveStepKinds.ContainsKey("S03"));
            Assert.Equal(StepDefectKind.QualityFloor, candidate.DefectiveStepKinds["S03"]);
        }

        // DefectiveStepCodes 에는 세 부류가 섞인다: (1) DefectKind 가 있는 것,
        // (2) 파일이 없는 것, (3) 해시가 안 맞는 것. 뒤의 둘은 결함 "표시"가 없었을
        // 뿐 재료가 없어 다시 만들어야 하므로 DefectiveStepKinds 에서 null 이어야
        // 한다 — 화면이 "표시된 결함"과 "재료 없음"을 가를 수 있어야 한다.
        [Fact]
        public void TryResume_DefectiveStepKinds_IsNullForStepsDroppedByMissingFileOrHashMismatch()
        {
            var journal = WriteResumableRun();
            var dir = journal.CurrentRunDirectory!;
            File.WriteAllText(Path.Combine(dir, "steps", "S01.md"), "누군가 손댄 본문"); // 해시 불일치
            File.Delete(Path.Combine(dir, "steps", "S02.md"));                            // 파일 없음

            var candidate = NewJournal().TryResume("## 목차 A");

            Assert.NotNull(candidate);
            Assert.Equal(new[] { "S01", "S02", "S03" }, candidate!.DefectiveStepCodes.OrderBy(c => c).ToArray());
            Assert.True(candidate.DefectiveStepKinds.ContainsKey("S01"));
            Assert.Null(candidate.DefectiveStepKinds["S01"]);
            Assert.True(candidate.DefectiveStepKinds.ContainsKey("S02"));
            Assert.Null(candidate.DefectiveStepKinds["S02"]);
            Assert.Equal(StepDefectKind.QualityFloor, candidate.DefectiveStepKinds["S03"]);
        }

        // 설계 §4-4 형태("단계 h/n · 골격 · 회차 리뷰 k개")가 실제 값으로 채워지는지.
        // h(healthy=2)·n(total=4)·k(reviews=3)을 모두 다른 값으로 seed한다 - 셋이
        // 같으면 서식 안의 자리를 바꿔도(예: healthy와 reviews를 맞바꿔도) 이 시험이
        // 못 잡는다.
        [Fact]
        public void DescribeLatestRun_HealthyRun_SummarizesStepsSkeletonAndReviews()
        {
            var journal = NewJournal();
            journal.OpenRun("## 목차", "run-start");

            journal.RecordSkeleton(1, "골격 본문");
            journal.RecordStepSection(1, "S01", "S01 건강");
            journal.RecordStepSection(1, "S02", "S02 건강");
            journal.RecordStepSection(
                1, "S03", "### S03\n\n> [!WARNING]\n> 이 단계는 생성에 실패했습니다.",
                new StepDefect(StepDefectKind.GenerationFailed, "S03 (생성 실패)"));
            journal.RecordStepSection(
                1, "S04", "### S04\n\n> [!WARNING]\n> 본문 없음 - 하한 미달",
                new StepDefect(StepDefectKind.QualityFloor, "S04 (하한 미달)"));
            journal.RecordReview(1, new ReviewResult { HasDefects = true, DefectiveSteps = { "S03", "S04" } });
            journal.RecordReview(2, new ReviewResult { HasDefects = true, DefectiveSteps = { "S04" } });
            journal.RecordReview(3, new ReviewResult { HasDefects = false });

            var summary = PlanAttemptJournal.DescribeLatestRun(_root, "Job_Test");

            Assert.NotNull(summary);
            Assert.Contains("run-001", summary);
            Assert.Contains("2/4", summary);          // healthy=2, total=4
            Assert.Contains("골격", summary);
            Assert.Contains("회차 리뷰 3개", summary); // reviews=3
        }

        // 다섯 갈래 모두 null - 짐작이 아니라 읽지 못하면 조용히 포기한다(설계 §4-4).
        [Theory]
        [InlineData("no-attempts-directory")]
        [InlineData("no-run-directory")]
        [InlineData("no-manifest-file")]
        [InlineData("manifest-deserializes-to-null")]
        [InlineData("corrupt-json-throws")]
        public void DescribeLatestRun_MissingOrBrokenArtifacts_ReturnsNull(string scenario)
        {
            var jobName = $"Job_{scenario}";
            var attemptsRoot = Path.Combine(_root, "Jobs", jobName, "raw", "attempts");

            switch (scenario)
            {
                case "no-attempts-directory":
                    // Jobs/{jobName}/raw/attempts 자체를 만들지 않는다.
                    break;
                case "no-run-directory":
                    Directory.CreateDirectory(attemptsRoot);
                    break;
                case "no-manifest-file":
                    Directory.CreateDirectory(Path.Combine(attemptsRoot, "run-001"));
                    break;
                case "manifest-deserializes-to-null":
                    {
                        var runDir = Path.Combine(attemptsRoot, "run-001");
                        Directory.CreateDirectory(runDir);
                        File.WriteAllText(Path.Combine(runDir, "manifest.json"), "null");
                        break;
                    }
                case "corrupt-json-throws":
                    {
                        var runDir = Path.Combine(attemptsRoot, "run-001");
                        Directory.CreateDirectory(runDir);
                        File.WriteAllText(Path.Combine(runDir, "manifest.json"), "{ 이것은 JSON 이 아니다");
                        break;
                    }
            }

            var result = PlanAttemptJournal.DescribeLatestRun(_root, jobName);

            Assert.Null(result);
        }

        // ---------------------------------------------------------------
        // Task 7 - 재개가 조용히 안 되는 것을 말하게 한다
        // ---------------------------------------------------------------

        // 판이 있는데 ReuseKey 가 안 맞으면 지금까지는 완전히 조용했다(2026-09-11
        // 실물 실패 - POQSettleBatch7-resume). 어느 항이 어긋났는지 남겨야 한다.
        [Fact]
        public void TryResume_WhenReuseKeyMismatches_LogsWhichItemsDifferedForTheNewestRun()
        {
            WriteResumableRun(); // run-001 - Model=gpt-4, SpecsSha256=specshash

            var mismatched = PlanAttemptJournal.Create(
                _root, "Job_Test", "OpenAI", "other-model", "high", "C#", "other-specshash");

            var lines = CaptureLogs(() => mismatched.TryResume("## 목차 A"));

            var line = Assert.Single(lines, l => l.Contains("재개 후보를 찾지 못했습니다"));
            Assert.Contains("SpecsSha256", line);
            Assert.Contains("Model", line);
            // 안 바뀐 항목은 소음이니 안 실려야 한다.
            Assert.DoesNotContain("TargetLanguage", line);
            Assert.DoesNotContain("Provider", line);
        }

        // 판이 아예 없으면(첫 실행) 조용해야 한다 - 매 실행 경고는 소음이다.
        [Fact]
        public void TryResume_WhenNoRunExists_LogsNothing()
        {
            var lines = CaptureLogs(() => NewJournal().TryResume("## 목차 A"));

            Assert.DoesNotContain(lines, l => l.Contains("재개 후보를 찾지 못했습니다"));
        }

        // [대칭 훑기 - 반대 방향] 일곱 항목이 전부 맞아 후보가 잡히면 이 진단이
        // 발화하면 안 된다 - 발화하면 정상 재개마다 경고가 뜬다.
        [Fact]
        public void TryResume_WhenReuseKeyFullyMatches_LogsNothing()
        {
            WriteResumableRun();

            var lines = CaptureLogs(() => NewJournal().TryResume("## 목차 A"));

            Assert.DoesNotContain(lines, l => l.Contains("재개 후보를 찾지 못했습니다"));
        }

        // 판이 여럿이면 전부 쏟아내지 않는다 - 가장 최근 판 하나만 진단하고,
        // 몇 개를 봤는지는 남긴다.
        [Fact]
        public void TryResume_WhenSeveralRunsAllMismatch_DiagnosesOnlyTheNewestRun()
        {
            // run-001 은 Model 이 어긋나고, run-002(최신)는 Effort 가 어긋난다 - 쿼리
            // 본인과 둘 다 다르지만 이유가 서로 달라야 "가장 최근 것만" 진단됨을 잰다.
            var first = PlanAttemptJournal.Create(
                _root, "Job_Test", "OpenAI", "model-run1", "high", "C#", "specshash");
            first.OpenRun("## 목차 A", "run-start");
            first.RecordSkeleton(1, "run-001 골격");
            first.RecordStepSection(1, "S01", "run-001 S01");

            var second = PlanAttemptJournal.Create(
                _root, "Job_Test", "OpenAI", "model-query", "low", "C#", "specshash");
            second.OpenRun("## 목차 A", "run-start");
            second.RecordSkeleton(1, "run-002 골격");
            second.RecordStepSection(1, "S01", "run-002 S01");

            var query = PlanAttemptJournal.Create(
                _root, "Job_Test", "OpenAI", "model-query", "high", "C#", "specshash");

            var lines = CaptureLogs(() => query.TryResume("## 목차 A"));

            var line = Assert.Single(lines, l => l.Contains("재개 후보를 찾지 못했습니다"));
            Assert.Contains("run-002", line);              // 가장 최근 판
            Assert.Contains("Effort", line);                // run-002 가 어긋난 항목
            Assert.DoesNotContain("model-run1", line);       // run-001 항목(Model)은 안 쏟아낸다
            Assert.Contains("판 2개", line);                 // 판 몇 개를 봤는지
        }

        // SpecsSha256 이 다를 때, 파일 이름 목록이 양쪽에 다 있으면 "무엇이" 다른지도
        // 말해야 한다(§Step 2) - 여기서는 개수 차이를 잰다.
        [Fact]
        public void TryResume_WhenSpecsSha256DiffersAndFileNamesAreKnown_ReportsCountDifference()
        {
            var withNames = PlanAttemptJournal.Create(
                _root, "Job_Test", "OpenAI", "gpt-4", "high", "C#",
                PlanAttemptJournal.ComputeSha256("A\nbodyA\nB\nbodyB"),
                new[] { "Schema.A", "Schema.B" });
            withNames.OpenRun("## 목차 A", "run-start");
            withNames.RecordSkeleton(1, "골격");
            withNames.RecordStepSection(1, "S01", "S01 본문");

            var fewerSpecs = PlanAttemptJournal.Create(
                _root, "Job_Test", "OpenAI", "gpt-4", "high", "C#",
                PlanAttemptJournal.ComputeSha256("A\nbodyA"),
                new[] { "Schema.A" });

            var lines = CaptureLogs(() => fewerSpecs.TryResume("## 목차 A"));

            var line = Assert.Single(lines, l => l.Contains("재개 후보를 찾지 못했습니다"));
            Assert.Contains("SpecsSha256", line);
            Assert.Contains("개수", line);
            Assert.Contains("2", line);
            Assert.Contains("1", line);
        }

        // 이름 집합 자체가 다르면(개수는 같아도) "개수" 대신 "이름 다름"을 말해야
        // 한다.
        [Fact]
        public void TryResume_WhenSpecsSha256DiffersWithSameCountButDifferentNames_ReportsNameDifference()
        {
            var original = PlanAttemptJournal.Create(
                _root, "Job_Test", "OpenAI", "gpt-4", "high", "C#",
                PlanAttemptJournal.ComputeSha256("A\nbodyA"),
                new[] { "Schema.A" });
            original.OpenRun("## 목차 A", "run-start");
            original.RecordSkeleton(1, "골격");
            original.RecordStepSection(1, "S01", "S01 본문");

            var renamed = PlanAttemptJournal.Create(
                _root, "Job_Test", "OpenAI", "gpt-4", "high", "C#",
                PlanAttemptJournal.ComputeSha256("Z\nbodyZ"),
                new[] { "Schema.Z" });

            var lines = CaptureLogs(() => renamed.TryResume("## 목차 A"));

            var line = Assert.Single(lines, l => l.Contains("재개 후보를 찾지 못했습니다"));
            Assert.Contains("이름", line);
            Assert.Contains("Schema.A", line);
            Assert.Contains("Schema.Z", line);
        }

        // 옛 manifest(SpecsFileNames 필드가 없던 시절)를 읽어도 깨지면 안 된다 -
        // run-001 실물 회귀 표본과 같은 모양(필드 자체가 JSON에 없음)을 흉내 낸다.
        [Fact]
        public void TryResume_WhenStoredManifestHasNoSpecsFileNames_StillDiagnosesWithoutThrowing()
        {
            var journal = NewJournal(); // specsSha256="specshash"
            journal.OpenRun("## 목차 A", "run-start");
            journal.RecordSkeleton(1, "골격");
            journal.RecordStepSection(1, "S01", "S01 본문");

            // 옛 manifest 는 SpecsFileNames 필드가 JSON에 아예 없었다(이 필드를 도입하기
            // 전 판) - 손으로 지워 그 모양을 흉내 낸다. System.Text.Json 은 없는 필드를
            // null 로 두고 역직렬화해야 한다(전제).
            var manifestPath = Path.Combine(journal.CurrentRunDirectory!, "manifest.json");
            var withField = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
            var withoutField = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in withField.EnumerateObject())
            {
                if (prop.Name == "SpecsFileNames") continue;
                withoutField[prop.Name] = prop.Value.Clone();
            }
            var legacyJson = JsonSerializer.Serialize(withoutField);
            Assert.DoesNotContain("SpecsFileNames", legacyJson); // 전제 확인
            File.WriteAllText(manifestPath, legacyJson);

            var mismatched = PlanAttemptJournal.Create(
                _root, "Job_Test", "OpenAI", "gpt-4", "high", "C#", "otherspecshash",
                new[] { "Schema.Current" });

            List<string>? lines = null;
            var ex = Record.Exception(() => lines = CaptureLogs(() => mismatched.TryResume("## 목차 A")));
            Assert.Null(ex);

            var line = Assert.Single(lines!, l => l.Contains("재개 후보를 찾지 못했습니다"));
            Assert.Contains("SpecsSha256", line);
        }

        // 실물 회귀 표본 - output/Jobs/POQSettleBatch7/raw/attempts/run-001/manifest.json 을
        // 그대로 읽어도(SpecsFileNames 필드가 없다) TryResume 이 던지지 않아야 한다.
        [Fact]
        public void TryResume_ReadsTheRealPOQSettleBatch7Manifest_WithoutThrowing()
        {
            var fixturePath = Path.Combine(
                FindRepoRoot(), "output", "Jobs", "POQSettleBatch7", "raw", "attempts", "run-001", "manifest.json");
            if (!File.Exists(fixturePath))
            {
                // 코퍼스 심링크가 없는 워크트리 - 이 시험은 그 재료에 의존하므로 조용히 건너뛴다.
                return;
            }

            var runDir = Path.Combine(_root, "Jobs", "Job_Real", "raw", "attempts", "run-001");
            Directory.CreateDirectory(runDir);
            File.Copy(fixturePath, Path.Combine(runDir, "manifest.json"));

            var journal = PlanAttemptJournal.Create(
                _root, "Job_Real", "claude-cli", "claude-sonnet-5", "high", "C#", "다른-specs-해시");

            var ex = Record.Exception(() => CaptureLogs(() => journal.TryResume("## 다른 목차")));
            Assert.Null(ex);
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? throw new InvalidOperationException("저장소 루트를 못 찾았습니다.");
        }

        private static List<string> CaptureLogs(Action action)
        {
            var sink = new CapturingSink();
            var previousLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
            try
            {
                action();
            }
            finally
            {
                Log.CloseAndFlush();
                Log.Logger = previousLogger;
            }

            return sink.Messages;
        }

        private sealed class CapturingSink : ILogEventSink
        {
            public List<string> Messages { get; } = new();
            public void Emit(LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
        }
    }
}
