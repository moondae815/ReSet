using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 시도 하나에서 난 발화 하나.
    ///
    /// <paramref name="Run"/> 은 <b>재생성 판</b>이다. 같은 객체를 두 번 재생성하면 시도
    /// 번호가 1 부터 다시 시작하므로, 이 항이 없으면 두 판의 계열이 한 파일에서 구분되지
    /// 않는다(2026-09-10 감사 §4(a)).
    /// </summary>
    public sealed record L1AttemptFiring(int Run, int Attempt, string CheckKey, string Message);

    /// <summary>
    /// 시도별 L1 발화를 객체 산출물(<c>raw/l1-attempts.json</c>)에 <b>누적</b>해 남긴다.
    ///
    /// [왜 산출물인가 - 2026-09-09] 이 정보는 지금 gitignore 된 3 만 줄짜리 실행 로그에만
    /// 있다. 검사 결함 하나를 진단할 때마다 사람이 그 로그에서 실물을 오려 왔다.
    /// 산출물로 남으면 승격기가 커밋되는 코퍼스로 옮길 수 있고, 「한 객체의 연속한 두
    /// 시도에서 같은 검사키가 발화」를 게이트로 셀 수 있게 된다
    /// (docs/superpowers/specs/2026-09-09-거부된-시도-코퍼스-design.md).
    ///
    /// [왜 누적인가] 덮어쓰면 연속 서명을 셀 수 없다 - 시도 1 의 발화가 시도 2 를 쓸 때
    /// 사라지면 게이트가 통째로 눈이 먼다.
    ///
    /// [왜 소프트 페일인가] 관측이 파이프라인을 죽이면 안 된다. 이 저장소의
    /// <c>MechanicalValidator.Validate</c> 가 자기 오류에 소프트 패스하는 것과 같은 이유다.
    ///
    /// [알려진 한계 - 2026-09-10 재리뷰 지목, 고치지 않기로 판단] <c>Append</c> 는 기존
    /// 파일을 <c>Read</c> 로 읽어 누적하는데, <c>Read</c> 가 명명 계약 위반(위 참고)을
    /// 만나면 빈 목록을 낸다 - 그 상태에서 <c>Append</c> 가 새 시도만 담아 <b>덮어쓰면</b>
    /// 계약 위반 전의 시도들이 사라진다. 이 클래스 자신의 [왜 누적인가]와 정면으로
    /// 어긋나는 모양이다. 고치지 않는다 - 지금 이 저장소의 유일한 쓰기 경로
    /// (<c>VerificationPipelineOrchestrator</c>)는 이 클래스로만 파일을 쓰고 언제나
    /// PascalCase 로 직렬화하므로, 계약을 어긴 파일이 생기려면 사람이나 다른 도구가
    /// <c>raw/l1-attempts.json</c> 을 직접 건드려야 한다 - 지금 코드베이스의 쓰기
    /// 경로로는 도달 불가다. 도달 경로가 생기면(예: 다른 프로세스가 이 파일에 쓰기
    /// 시작하면) 이 문단을 다시 읽고 고쳐라 - 처방은 계약 위반이면 <c>Append</c> 도
    /// 소프트 페일(새 시도를 버리고 기존 파일은 그대로 둔다)하는 것이다.
    /// </summary>
    public static class L1AttemptLog
    {
        public const string FileName = "l1-attempts.json";

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static void Append(string objectDirectory, int attempt, IEnumerable<L1Firing> firings)
        {
            try
            {
                var list = firings?.ToList() ?? new List<L1Firing>();
                if (list.Count == 0) return;

                var rawDir = Path.Combine(objectDirectory, "raw");
                Directory.CreateDirectory(rawDir);
                var path = Path.Combine(rawDir, FileName);

                var accumulated = File.Exists(path)
                    ? Read(path).ToList()
                    : new List<L1AttemptFiring>();

                var run = ResolveRun(accumulated, attempt);
                accumulated.AddRange(list.Select(f => new L1AttemptFiring(run, attempt, f.CheckKey, f.Message)));
                File.WriteAllText(path, JsonSerializer.Serialize(accumulated, Options));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[L1AttemptLog] 시도별 발화 기록에 실패했습니다 - 파이프라인은 계속합니다.");
            }
        }

        /// <summary>
        /// 이 시도가 어느 판에 속하는지 정한다.
        ///
        /// [왜 유도하는가 - 2026-09-10] 판 번호를 넘겨줄 자리가 <b>없다.</b> 유일한 쓰기
        /// 경로(<c>VerificationPipelineOrchestrator</c>)가 아는 것은 <c>attempt</c> 하나이고,
        /// 재생성마다 1 부터 다시 센다. 파이프라인에 판 식별자를 새로 만들어 배선하는 것보다
        /// 이미 있는 신호에서 읽는 편이 반경이 작다.
        ///
        /// [규칙] 한 판 안에서 시도는 1·2·3… 으로 <b>단조 증가</b>한다(재시도 루프가
        /// <c>attempt++</c> 로만 움직인다). 그래서 <b>들어온 시도가 마지막 시도를 넘지
        /// 않으면 새 판</b>이다. 빈 파일이면 1 판이다.
        ///
        /// [이 규칙이 무엇을 고치는가] 판 사이에 <c>raw/</c> 를 지우는 코드가 이 저장소에
        /// 없다. 그래서 두 판이 한 파일에 이어 붙을 수 있고, 그러면 겉으로는 시도 7~11 처럼
        /// 보여 <c>FindSelfReinforcing</c> 이 판 경계를 가로지르는 인접을 후보로 낸다.
        /// 2026-09-10 에는 사람이 앞 판 파일을 손으로 개명해 그 경로를 비켜 갔다 - 그것이
        /// 다음번에 반복된다는 보장이 없으므로 코드가 대신한다.
        /// </summary>
        private static int ResolveRun(IReadOnlyList<L1AttemptFiring> accumulated, int attempt)
        {
            if (accumulated.Count == 0) return 1;

            var last = accumulated[^1];
            return attempt > last.Attempt ? last.Run : last.Run + 1;
        }

        public static IReadOnlyList<L1AttemptFiring> Read(string jsonPath)
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<List<L1AttemptFiring>>(File.ReadAllText(jsonPath))
                       ?? new List<L1AttemptFiring>();

                // [명명 계약 - 2026-09-10 최종 리뷰 발견] Deserialize 는 기본 옵션(대소문자
                // 구분)이라 camelCase 로 쓰인 파일에 예외를 안 던지고 Attempt=0·CheckKey
                // 없음인 레코드를 그대로 낸다(실측: Pascal 파일 count=1 Attempt=2
                // CheckKey=K, camel 파일 count=1 Attempt=0 CheckKey=<null>). 나중에
                // Append 의 쓰기 옵션이 바뀌면 FindSelfReinforcing 이 모든 시도를 0 으로
                // 보고 gap==1 을 하나도 못 찾는다 - 통째로 망가진 코퍼스에 대해 「발견
                // 없음」을 조용히 보고하는 것과 같다. Attempt 는 실물에서 언제나 1
                // 이상이고 CheckKey 는 언제나 있다 - 이 계약을 어기면 다른 손상 파일과
                // 같게(빈 목록) 취급한다.
                // [2026-09-10] Run 도 이 계약에 잇는다. 안 이으면 Run 이 없는 옛 파일이
                // Run=0 으로 **조용히** 역직렬화되고, 판 경계가 통째로 0 으로 접힌다 -
                // camelCase 가 Attempt=0 으로 조용히 읽히던 그 침묵을 같은 함수에서 두
                // 번째로 만드는 것이다. Run 은 실물에서 언제나 1 이상이다.
                if (deserialized.Any(f => f.Run <= 0 || f.Attempt <= 0 || string.IsNullOrEmpty(f.CheckKey)))
                {
                    throw new JsonException(
                        $"{jsonPath} 의 레코드가 명명 계약(PascalCase: Run·Attempt·CheckKey·Message)을 " +
                        "어깁니다 - Run<=0 이거나 Attempt<=0 이거나 CheckKey 가 비었습니다.");
                }

                return deserialized;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[L1AttemptLog] {Path} 를 읽지 못했습니다.", jsonPath);
                return new List<L1AttemptFiring>();
            }
        }
    }
}
