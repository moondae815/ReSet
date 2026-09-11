using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>재사용 판정의 재료. 1단계는 적기만 하고 판정하지 않는다.</summary>
    public sealed record PlanAttemptReuseKey(
        int ContractVersion,
        string PlanStructureSha256,
        string SpecsSha256,
        string Provider,
        string Model,
        string? Effort,
        string TargetLanguage);

    /// <summary>
    /// 한 산출물이 어느 시도에 쓰였고 그 내용이 무엇이었나.
    ///
    /// <paramref name="DefectKind"/>·<paramref name="DefectReason"/> — [FINAL FIX -
    /// Important 2] 하한 미달 중간본("본문 없음 - 하한 미달")과 생성 실패 스텁
    /// ("이 단계는 생성에 실패했습니다")이 건강한 본문과 같은 (Attempt, Sha256)
    /// 모양으로 기록되면, 2단계가 "이 섹션을 재사용해도 되는가"를 manifest만으로
    /// 물을 방법이 없다 - 파일이 있다는 사실은 §11-1의 계약("`steps`가 진실이다")을
    /// 만족하지만 그 안의 본문이 건강한지는 말하지 않는다. 문서 전체 L1
    /// 재검사(§11-2)가 그물이긴 하나, 단계 하한 검사는 다른 축이라(생성 루프
    /// 안에서만 돌고 재사용된 섹션에는 안 돈다) 그 그물이 이 자리를 못 받는다.
    /// null 이면 건강한 본문(이 축의 하한 검사를 통과했거나, 검사가 아예 돌지
    /// 않은 자리라도 결함이 관측되지 않았다).
    /// </summary>
    public sealed record PlanAttemptArtifact(
        int Attempt, string Sha256, StepDefectKind? DefectKind = null, string? DefectReason = null);

    /// <summary>
    /// 재개 후보 하나. <b>2단계가 이 타입만 보고 재개를 판단한다</b> — manifest 해석은
    /// <c>TryResume</c> 안에서 끝난다(설계 §3-2).
    /// </summary>
    public sealed record PlanAttemptResumeCandidate(
        string RunDirectory,
        int Run,
        string StartedAt,
        string Skeleton,
        int SkeletonAttempt,
        IReadOnlyDictionary<string, string> ReusableSections,
        IReadOnlyDictionary<string, int> SectionAttempts,
        IReadOnlyList<string> DefectiveStepCodes,
        IReadOnlyDictionary<string, StepDefectKind?> DefectiveStepKinds,
        IReadOnlyList<(int Attempt, ReviewResult Review)> PriorReviews,
        int TotalStepsInManifest);

    /// <summary>
    /// 판 하나의 명세. <b>이 파일이 진실이고 디렉터리의 파일 존재는 진실이 아니다</b> —
    /// 반쯤 쓰인 판이 나중에 거짓 재료가 되는 것을 막는 유일한 장치다(설계서 §8).
    /// </summary>
    public sealed class PlanAttemptManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public int Run { get; set; }
        public string Job { get; set; } = string.Empty;
        public string StartedAt { get; set; } = string.Empty;
        public string OpenedBy { get; set; } = string.Empty;
        public PlanAttemptReuseKey? ReuseKey { get; set; }
        public PlanAttemptArtifact? Skeleton { get; set; }
        public Dictionary<string, PlanAttemptArtifact> Steps { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 배치 계획 생성이 회차 도중에 만든 것을 그 자리에서 디스크에 남긴다.
    ///
    /// [왜 필요한가 - 2026-09-10 판독] 회차가 넘어갈 때 그 회차의 계획서 본문·Critic
    /// 피드백·단계 섹션은 <b>메모리에만</b> 있다(<c>BestAttempt</c>·
    /// <c>feedbackHistory</c>·<c>lastStepSections</c>). 디스크로 나가는 것은 목차뿐이다.
    /// 그래서 쿼터 소진으로 중단되면 단계 16~18개의 생성 비용이 통째로 사라진다 —
    /// 1회차가 L1 에서 죽었으면 <c>BestAttempt</c> 가 비어 있어 구제도 없다.
    ///
    /// [왜 Null Object 인가] 열기에 실패해도 객체를 돌려주고 모든 기록이 no-op 이 된다.
    /// 호출부가 <c>if (journal != null)</c> 로 분기하기 시작하면 그 분기가 네 자리로
    /// 번지고, 한 자리를 빠뜨리면 manifest 가 거짓말을 한다.
    ///
    /// [왜 판 번호를 디렉터리로 박는가] <c>L1AttemptLog.ResolveRun</c> 은 「판 번호를
    /// 넘겨줄 자리가 없다」며 시도 번호의 단조성에서 판 경계를 <b>유도</b>하고, 그
    /// 주석이 스스로 취약하다고 적어 두었다. 여기는 디렉터리가 곧 판 경계다.
    ///
    /// [1단계의 관할] <b>쓰기만 한다.</b> 읽는 쪽(재개)은 2단계다.
    /// </summary>
    public sealed class PlanAttemptJournal
    {
        /// <summary>
        /// 프롬프트·규약이 바뀌면 옛 섹션이 새 규약을 안 지킨다. <b>사람이 올리는
        /// 상수다</b> — 규약을 바꾸는 커밋에서 함께 올려라. 안 올리고 지나가도
        /// 재사용한 섹션은 문서 전체 L1 을 다시 통과해야 하므로 L1 이 그물이 된다.
        /// </summary>
        public const int ContractVersion = 1;

        private readonly string _outputRoot;
        private readonly string _jobName;
        private readonly string _provider;
        private readonly string _model;
        private readonly string? _effort;
        private readonly string _targetLanguage;
        private readonly string _specsSha256;

        private readonly object _gate = new();
        private string? _runDirectory;
        private PlanAttemptManifest? _manifest;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // DefectKind(StepDefectKind)를 정수가 아니라 이름으로 남긴다 - 이 파일은
            // 감사가 직접 읽는 자리이고(§4-3), 숫자로는 "이 판이 왜 재사용 불가인가"에
            // 답할 수 없다.
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        private PlanAttemptJournal(
            string outputRoot, string jobName, string provider, string model,
            string? effort, string targetLanguage, string specsSha256)
        {
            _outputRoot = outputRoot;
            _jobName = jobName;
            _provider = provider;
            _model = model;
            _effort = effort;
            _targetLanguage = targetLanguage;
            _specsSha256 = specsSha256;
        }

        public static PlanAttemptJournal Create(
            string outputRoot, string jobName, string provider, string model,
            string? effort, string targetLanguage, string specsSha256) =>
            new(outputRoot, jobName, provider, model, effort, targetLanguage, specsSha256);

        /// <summary>열린 판의 디렉터리. 열린 판이 없으면 null(비활성).</summary>
        public string? CurrentRunDirectory
        {
            get { lock (_gate) { return _runDirectory; } }
        }

        /// <summary>
        /// 새 판을 연다. 계기는 셋 — 실행 시작(<c>run-start</c>), 목차 재작성
        /// (<c>structure-redraft</c>), 그리고 구제 채택이 재설계 이전 목차로
        /// 되돌아갈 때(<c>rescue-adopt</c>, <c>VerificationPipelineOrchestrator.
        /// AdoptPlanStructureForRescueAsync</c>).
        ///
        /// 목차가 바뀌면 기존 섹션은 전부 무효다(<c>ClearSplitGenerationCacheAfterRedraft</c>
        /// 가 인메모리에서 하는 그 일). 판을 새로 열면 그 무효화가 디렉터리 전환으로
        /// 표현되어 <b>지울 것이 없다.</b> 구제 채택도 마찬가지다 — 되돌아가는 목차의
        /// 해시가 이미 존재하는 옛 판(재료가 있다)과 같더라도, 그 판을 다시 열지
        /// 않고 새 판을 파는 이유는 §5(설계서)가 "판 하나는 목차 하나에 대한 작업"을
        /// 지키기 때문이다 — 옛 판을 덮어쓰면 그 판이 실제로 끝난 시각(<c>StartedAt</c>)이
        /// 거짓이 된다.
        /// </summary>
        public void OpenRun(string planStructure, string openedBy)
        {
            lock (_gate)
            {
                try
                {
                    var attemptsRoot = Path.Combine(
                        _outputRoot, "Jobs", _jobName, "raw", "attempts");
                    Directory.CreateDirectory(attemptsRoot);

                    var next = NextRunNumber(attemptsRoot);
                    var runDir = Path.Combine(attemptsRoot, $"run-{next:D3}");
                    Directory.CreateDirectory(runDir);

                    _manifest = new PlanAttemptManifest
                    {
                        Run = next,
                        Job = _jobName,
                        StartedAt = DateTimeOffset.Now.ToString("o"),
                        OpenedBy = openedBy,
                        ReuseKey = new PlanAttemptReuseKey(
                            ContractVersion,
                            ComputeSha256(planStructure),
                            _specsSha256,
                            _provider,
                            _model,
                            _effort,
                            _targetLanguage)
                    };
                    _runDirectory = runDir;
                    FlushManifest();
                }
                catch (Exception ex)
                {
                    // 판을 못 열면 그 실행 내내 조용하다. 매 단계마다 실패 로그를
                    // 쏟으면 진짜 신호를 덮는다.
                    Log.Debug(ex, "[PlanAttemptJournal] 판을 열지 못했습니다 - 이 실행에서는 시도를 남기지 않습니다.");
                    _runDirectory = null;
                    _manifest = null;
                }
            }
        }

        private static int NextRunNumber(string attemptsRoot) =>
            Directory.EnumerateDirectories(attemptsRoot, "run-*")
                .Select(Path.GetFileName)
                .Select(name => int.TryParse(name?.Substring(4), out var v) ? v : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;

        /// <summary>_gate 를 이미 쥔 채 부른다.</summary>
        private void FlushManifest()
        {
            if (_runDirectory == null || _manifest == null) return;
            WriteAtomic(
                Path.Combine(_runDirectory, "manifest.json"),
                JsonSerializer.Serialize(_manifest, Options));
        }

        /// <summary>
        /// BOM 없는 UTF-8.
        ///
        /// <c>Encoding.UTF8</c> 은 <b>BOM 을 쓴다</b> — 실물에서 저널이 쓴 파일 25개
        /// 전부에 붙어 python <c>json</c> 이 「Unexpected UTF-8 BOM」으로 거부했다
        /// (2026-09-11, `POQSettleBatch7`). C# 은 <c>File.ReadAllText</c> 가 BOM 을
        /// 벗겨 읽으므로 이 저장소의 왕복 시험 스물둘이 전부 초록이었다 —
        /// <c>EveryWrittenFile_HasNoUtf8Bom</c> 이 바이트로 잠근다.
        ///
        /// 자매 클래스 <c>L1AttemptLog</c> 는 인코딩 인자를 안 줘 기본값(BOM 없음)을
        /// 쓴다. 여기서 값을 명시하는 것은 <b>BOM 없음이 의도라는 것</b>을 코드가
        /// 말하게 하려는 것이다 — 인자를 지우면 다음 사람이 다시 `Encoding.UTF8` 을
        /// 넣는다.
        /// </summary>
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private static void WriteAtomic(string path, string content)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, Utf8NoBom);
            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>
        /// 파일명으로 안전한 단계 코드. <b>화이트리스트다</b> — 금지 문자 목록은
        /// 플랫폼마다 다르고 Windows 예약 이름(CON·PRN…)까지 다루려면 목록이
        /// 길어진다. 실물 코드는 <c>S01</c> 꼴이라 이 좁은 집합으로 충분하다.
        /// </summary>
        private static readonly Regex SafeStepCode =
            new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        public void RecordSkeleton(int attempt, string markdown)
        {
            lock (_gate)
            {
                if (_runDirectory == null || _manifest == null) return;
                try
                {
                    WriteAtomic(Path.Combine(_runDirectory, "skeleton.md"), markdown);
                    _manifest.Skeleton = new PlanAttemptArtifact(attempt, ComputeSha256(markdown));
                    FlushManifest();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[PlanAttemptJournal] 골격을 남기지 못했습니다 - 파이프라인은 계속합니다.");
                }
            }
        }

        /// <param name="defect">
        /// 이 본문이 안고 있는 결함(있다면). null이면 건강한 본문이다 - 기존 호출부가
        /// 이 인자를 안 넘겨도(생략 시 기본값 null) 종전 동작 그대로다.
        /// </param>
        public void RecordStepSection(int attempt, string stepCode, string markdown, StepDefect? defect = null)
        {
            lock (_gate)
            {
                if (_runDirectory == null || _manifest == null) return;

                if (stepCode == null || !SafeStepCode.IsMatch(stepCode))
                {
                    // 판 전체를 버리지 않는다 - 나머지 단계의 기록은 여전히 값이 있다.
                    Log.Debug(
                        "[PlanAttemptJournal] 단계 코드가 파일명으로 안전하지 않아 건너뜁니다 - Code: {Code}",
                        stepCode);
                    return;
                }

                try
                {
                    var stepsDir = Path.Combine(_runDirectory, "steps");
                    Directory.CreateDirectory(stepsDir);
                    WriteAtomic(Path.Combine(stepsDir, stepCode + ".md"), markdown);
                    _manifest.Steps[stepCode] = new PlanAttemptArtifact(
                        attempt, ComputeSha256(markdown), defect?.Kind, defect?.Reason);
                    FlushManifest();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[PlanAttemptJournal] 단계 섹션을 남기지 못했습니다 - Code: {Code}", stepCode);
                }
            }
        }

        /// <summary>
        /// 회차 하나의 채점 결과. <c>ReviewResult</c> 를 그대로 직렬화하지 않는 이유:
        /// 그 타입에는 <c>ThinkingText</c> 가 실려 있어 파일이 수십 KB 로 붇고,
        /// <c>NormalizedScore</c> 는 계산 속성이라 직렬화에서 빠질 수 있다. 무엇이
        /// 남는지 이 자리에서 못박는다.
        /// </summary>
        internal sealed record PlanAttemptReview(
            int Attempt,
            bool HasDefects,
            string? FeedbackComment,
            IReadOnlyList<string> DefectiveSteps,
            bool SkeletonDefective,
            bool StructureDefective,
            bool AxisThresholdForced,
            int ScoreAccuracy,
            int ScoreCrud,
            int ScoreInterface,
            int ScoreException,
            int ScoreReadability,
            int NormalizedScore);

        /// <summary>
        /// 회차 하나의 리뷰를 남긴다. 골격·섹션과 달리 <b>최신 하나로 덮지 않고
        /// 회차마다 별개 파일이다</b>(설계서 §4-2) — 리뷰는 누적이 아니라 시계열이고,
        /// <c>feedbackHistory</c> 가 최근 3라운드만 들고 있어 디스크가 메모리보다
        /// 오래 기억하는 유일한 자리이기 때문이다. manifest 에는 안 실린다 - "최신
        /// 하나"가 아니므로 골격·섹션의 인덱싱 계약이 안 맞는다.
        /// </summary>
        public void RecordReview(int attempt, ReviewResult review)
        {
            if (review == null) return;

            lock (_gate)
            {
                if (_runDirectory == null) return;
                try
                {
                    var reviewsDir = Path.Combine(_runDirectory, "reviews");
                    Directory.CreateDirectory(reviewsDir);

                    var payload = new PlanAttemptReview(
                        attempt,
                        review.HasDefects,
                        review.FeedbackComment,
                        review.DefectiveSteps?.ToList() ?? new List<string>(),
                        review.SkeletonDefective,
                        review.StructureDefective,
                        review.AxisThresholdForced,
                        review.ScoreAccuracy,
                        review.ScoreCrud,
                        review.ScoreInterface,
                        review.ScoreException,
                        review.ScoreReadability,
                        review.NormalizedScore);

                    WriteAtomic(
                        Path.Combine(reviewsDir, $"attempt-{attempt:D2}.json"),
                        JsonSerializer.Serialize(payload, Options));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[PlanAttemptJournal] 리뷰를 남기지 못했습니다 - 시도: {Attempt}", attempt);
                }
            }
        }

        /// <summary>
        /// 이어서 할 수 있는 판을 찾는다. 없으면 <c>null</c>.
        ///
        /// [왜 쓰기와 같은 클래스인가] 1단계 설계가 「읽는 쪽이 생길 때 같은 클래스에
        /// 들어가 규약이 갈라지지 않는다」고 약속한 자리다. manifest 의 키 대소문자·
        /// 진실 계약(§11-1)을 아는 곳이 둘이 되면 조용히 갈린다.
        ///
        /// [무엇을 거르나] <c>ReuseKey</c> 일곱 항목이 전부 같아야 한다 — 특히 모델이
        /// 다르면 문서의 목소리가 섞인다. 골격도 섹션도 없는 판(단일 호출 폴백)은
        /// 재개 불가다(§11-5). <c>Sha256</c> 이 안 맞는 항목은 버린다.
        ///
        /// [왜 소프트페일인가] 못 읽는 판은 <b>없는 것으로 친다.</b> 재개는 편의이지
        /// 파이프라인의 전제가 아니다.
        /// </summary>
        public PlanAttemptResumeCandidate? TryResume(string planStructure)
        {
            try
            {
                var attemptsRoot = Path.Combine(_outputRoot, "Jobs", _jobName, "raw", "attempts");
                if (!Directory.Exists(attemptsRoot)) return null;

                var wanted = new PlanAttemptReuseKey(
                    ContractVersion, ComputeSha256(planStructure), _specsSha256,
                    _provider, _model, _effort, _targetLanguage);

                foreach (var runDir in Directory.EnumerateDirectories(attemptsRoot, "run-*")
                             .OrderByDescending(d => d, StringComparer.Ordinal))
                {
                    var candidate = TryReadCandidate(runDir, wanted);
                    if (candidate != null) return candidate;
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[PlanAttemptJournal] 재개 후보를 찾지 못했습니다 - 처음부터 만듭니다.");
                return null;
            }
        }

        /// <summary>
        /// 판 하나를 읽는다. <b>이 판만 포기한다</b> — manifest 가 깨진 JSON이거나
        /// 그 밖의 방식으로 못 읽혀도, 그 사실이 <c>TryResume</c> 의 <c>foreach</c> 를
        /// 끊어 더 오래된 건강한 판을 스캔에서 빼면 안 된다(리뷰 발견 Important 1,
        /// 2026-09-11). <c>ReadPriorReviews</c> 가 리뷰 파일 하나마다 개별
        /// try/catch 로 이미 지키는 것과 같은 「부분 실패는 그 항목만 버린다」
        /// 규칙을 판 단위에도 적용한다.
        /// </summary>
        private static PlanAttemptResumeCandidate? TryReadCandidate(string runDir, PlanAttemptReuseKey wanted)
        {
            try
            {
                return TryReadCandidateCore(runDir, wanted);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[PlanAttemptJournal] 판을 읽지 못해 건너뜁니다 - {RunDir}", runDir);
                return null;
            }
        }

        private static PlanAttemptResumeCandidate? TryReadCandidateCore(string runDir, PlanAttemptReuseKey wanted)
        {
            var manifestPath = Path.Combine(runDir, "manifest.json");
            if (!File.Exists(manifestPath)) return null;

            var manifest = JsonSerializer.Deserialize<PlanAttemptManifest>(
                File.ReadAllText(manifestPath), Options);
            if (manifest?.ReuseKey == null || !manifest.ReuseKey.Equals(wanted)) return null;

            // 골격
            string? skeleton = null;
            var skeletonAttempt = 0;
            var skeletonPath = Path.Combine(runDir, "skeleton.md");
            if (manifest.Skeleton != null && File.Exists(skeletonPath))
            {
                var text = File.ReadAllText(skeletonPath);
                if (ComputeSha256(text) == manifest.Skeleton.Sha256)
                {
                    skeleton = text;
                    skeletonAttempt = manifest.Skeleton.Attempt;
                }
            }

            // 섹션 — DefectKind 가 있으면 재사용하지 않고 「다시 만들 것」으로 보낸다.
            // DefectiveStepCodes 에는 세 부류가 섞인다: (1) DefectKind 가 있는 것,
            // (2) 파일이 없는 것, (3) 해시가 안 맞는 것. DefectiveStepKinds 는 (1)만
            // 실제 종류를 옮기고 (2)·(3)은 null 이다 - "결함 표시는 없지만 재료가
            // 없어 다시 만들어야 함"을 화면이 가를 수 있어야 한다(리뷰 발견,
            // Task 2 의 ConfirmResumeAsync 가 사유를 못 보이던 근본 원인, 2026-09-11).
            var reusable = new Dictionary<string, string>(StringComparer.Ordinal);
            var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
            var defective = new List<string>();
            var defectiveKinds = new Dictionary<string, StepDefectKind?>(StringComparer.Ordinal);
            foreach (var (code, artifact) in manifest.Steps.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (artifact.DefectKind != null)
                {
                    defective.Add(code);
                    defectiveKinds[code] = artifact.DefectKind;
                    continue;
                }

                var path = Path.Combine(runDir, "steps", code + ".md");
                if (!File.Exists(path))
                {
                    defective.Add(code);
                    defectiveKinds[code] = null;
                    continue;
                }

                var text = File.ReadAllText(path);
                if (ComputeSha256(text) != artifact.Sha256)
                {
                    defective.Add(code);
                    defectiveKinds[code] = null;
                    continue;
                }

                reusable[code] = text;
                attempts[code] = artifact.Attempt;
            }

            // 골격도 섹션도 없으면 재개할 재료가 없다(§11-5).
            if (skeleton == null && reusable.Count == 0) return null;

            return new PlanAttemptResumeCandidate(
                runDir, manifest.Run, manifest.StartedAt,
                skeleton ?? string.Empty, skeletonAttempt,
                reusable, attempts, defective, defectiveKinds,
                ReadPriorReviews(runDir), manifest.Steps.Count);
        }

        /// <summary>
        /// 시도 번호가 큰 순으로 최근 <c>CriticFeedbackLog.MaxRetainedRounds</c> 개.
        /// 점수로 고르지 않는다 — <c>CriticFeedbackLog.Record</c> 가 시간순으로 쌓고
        /// 넘치면 앞에서 버리는 그 규칙과 같아야 <b>재개 전후로 동작이 같다</b>(설계 §3-3).
        /// </summary>
        private static IReadOnlyList<(int Attempt, ReviewResult Review)> ReadPriorReviews(string runDir)
        {
            var reviewsDir = Path.Combine(runDir, "reviews");
            if (!Directory.Exists(reviewsDir)) return new List<(int, ReviewResult)>();

            var result = new List<(int Attempt, ReviewResult Review)>();
            foreach (var path in Directory.EnumerateFiles(reviewsDir, "attempt-*.json"))
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<PlanAttemptReview>(File.ReadAllText(path), Options);
                    if (doc == null || doc.Attempt <= 0) continue;
                    result.Add((doc.Attempt, new ReviewResult
                    {
                        HasDefects = doc.HasDefects,
                        FeedbackComment = doc.FeedbackComment,
                        DefectiveSteps = doc.DefectiveSteps?.ToList() ?? new List<string>(),
                        SkeletonDefective = doc.SkeletonDefective,
                        StructureDefective = doc.StructureDefective,
                        AxisThresholdForced = doc.AxisThresholdForced,
                        ScoreAccuracy = doc.ScoreAccuracy,
                        ScoreCrud = doc.ScoreCrud,
                        ScoreInterface = doc.ScoreInterface,
                        ScoreException = doc.ScoreException,
                        ScoreReadability = doc.ScoreReadability
                    }));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[PlanAttemptJournal] 리뷰 파일을 건너뜁니다 - {Path}", path);
                }
            }

            return result
                .OrderByDescending(r => r.Attempt)
                .Take(CriticFeedbackLog.MaxRetainedRounds)
                .ToList();
        }

        public static string ComputeSha256(string input)
        {
            // null 만 가드한다 - 빈 문자열은 "미계산" sentinel 이 아니라 실제 빈
            // 내용이므로 그 다이제스트(e3b0c442...)를 돌려준다. 여기서 ""를
            // 돌리면 "미계산"과 "빈 내용"이 같은 값이 되어 재사용 키가 둘을
            // 구분 못 한다(리뷰 라운드 1).
            if (input is null) return string.Empty;

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
