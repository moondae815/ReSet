using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>한 산출물이 어느 시도에 쓰였고 그 내용이 무엇이었나.</summary>
    public sealed record PlanAttemptArtifact(int Attempt, string Sha256);

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
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
        /// 새 판을 연다. 계기는 둘 — 실행 시작(<c>run-start</c>)과 목차 재작성
        /// (<c>structure-redraft</c>).
        ///
        /// 목차가 바뀌면 기존 섹션은 전부 무효다(<c>ClearSplitGenerationCacheAfterRedraft</c>
        /// 가 인메모리에서 하는 그 일). 판을 새로 열면 그 무효화가 디렉터리 전환으로
        /// 표현되어 <b>지울 것이 없다.</b>
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

        private static void WriteAtomic(string path, string content)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, Encoding.UTF8);
            File.Move(temporary, path, overwrite: true);
        }

        public static string ComputeSha256(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
