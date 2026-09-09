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
                return JsonSerializer.Deserialize<List<L1AttemptFiring>>(File.ReadAllText(jsonPath))
                       ?? new List<L1AttemptFiring>();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[L1AttemptLog] {Path} 를 읽지 못했습니다.", jsonPath);
                return new List<L1AttemptFiring>();
            }
        }
    }
}
