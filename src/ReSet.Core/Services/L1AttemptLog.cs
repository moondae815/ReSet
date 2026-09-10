using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>시도 하나에서 난 발화 하나.</summary>
    public sealed record L1AttemptFiring(int Attempt, string CheckKey, string Message);

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

                accumulated.AddRange(list.Select(f => new L1AttemptFiring(attempt, f.CheckKey, f.Message)));
                File.WriteAllText(path, JsonSerializer.Serialize(accumulated, Options));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[L1AttemptLog] 시도별 발화 기록에 실패했습니다 - 파이프라인은 계속합니다.");
            }
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
                if (deserialized.Any(f => f.Attempt <= 0 || string.IsNullOrEmpty(f.CheckKey)))
                {
                    throw new JsonException(
                        $"{jsonPath} 의 레코드가 명명 계약(PascalCase: Attempt·CheckKey·Message)을 " +
                        "어깁니다 - Attempt<=0 이거나 CheckKey 가 비었습니다.");
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
