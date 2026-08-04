using System;
using System.Text;

namespace ReSet.Core.Services.Clients.Cli
{
    public enum CliFailureKind
    {
        NotAuthenticated,
        QuotaExhausted,
        Timeout,
        ToolPermissionDenied,
        Unknown
    }

    /// <summary>
    /// CLI 실패의 원인을 분류한다.
    ///
    /// 자동 폴백을 만들지 않기로 했으므로, 사람이 로그만 보고 "다른 CLI로 갈지
    /// API로 갈지"를 판단할 수 있어야 한다. 분류가 이 설계의 핵심 산출물이다.
    /// </summary>
    public static class CliFailureClassifier
    {
        // 쿼터를 먼저 본다. 쿼터 안내문에 "login" 같은 단어가 섞이는 경우가 있고,
        // 그때 인증 문제로 오진하면 사용자가 엉뚱한 조치를 한다.
        private static readonly string[] QuotaMarkers =
        {
            "usage limit", "rate limit", "rate_limit", "quota", "limit reached",
            "429", "out of credit", "insufficient_quota", "too many requests",
            "사용량", "한도"
        };

        private static readonly string[] AuthMarkers =
        {
            "not logged in", "unauthorized", "401", "authentication",
            "invalid api key", "credential", "please log in", "please login",
            "로그인", "인증"
        };

        // 툴을 끄지 못하는 CLI(agy)에서 모델이 툴을 잡으면, 헤드리스 모드는 권한을 물을
        // 수 없어 자동 거부하고 빈 응답을 남긴다. 종료 코드는 0, status도 SUCCESS다.
        //
        // 마커를 "permission"이나 "권한" 같은 일반 단어로 잡으면 안 된다. 이 분류의
        // haystack에는 extraDetail을 통해 CLI의 stdout 전문이 들어오고, ReSet의 도메인은
        // 정산 프로시저라 GRANT/DENY와 "권한"은 명세서 본문에 일상적으로 등장한다.
        // CLI 안내문에만 나타나는 고유 문구로 좁힌다.
        private static readonly string[] ToolPermissionMarkers =
        {
            "auto-denied", "auto denied",
            "--dangerously-skip-permissions",
            "permissions.allow",
            "headless mode cannot prompt"
        };

        public static CliFailureKind Classify(CliProcessResult result, string? extraDetail)
        {
            if (result.TimedOut)
            {
                return CliFailureKind.Timeout;
            }

            // stdout은 절대 보지 않는다. codex exec는 프롬프트와 추론 과정을 stdout으로
            // 흘리고, agy와 claude는 답변 본문을 stdout의 JSON에 담아 돌려준다. 이 저장소의
            // 도메인은 정산 프로시저이므로 "한도"(거래 한도, 결제 한도)와 "사용량"은 프롬프트와
            // 답변에 일상적으로 등장한다. stdout을 훑으면 로그인만 하면 될 상황을 "구독 한도
            // 소진"으로 오진해 사용자가 provider를 갈아엎게 만든다.
            //
            // 대신 stdout 안에만 담기는 오류(claude의 subtype/api_error_status, agy의 status
            // JSON)는 각 클라이언트가 extraDetail로 명시해 넘긴다.
            var haystack = $"{result.StandardError}\n{extraDetail}"
                .ToLowerInvariant();

            if (ContainsAny(haystack, QuotaMarkers))
            {
                return CliFailureKind.QuotaExhausted;
            }

            if (ContainsAny(haystack, AuthMarkers))
            {
                return CliFailureKind.NotAuthenticated;
            }

            // 쿼터·인증 뒤에 둔다. 기존 판정을 한 건도 바꾸지 않고 Unknown만 가져간다.
            if (ContainsAny(haystack, ToolPermissionMarkers))
            {
                return CliFailureKind.ToolPermissionDenied;
            }

            return CliFailureKind.Unknown;
        }

        public static InvalidOperationException ToException(
            string providerName,
            string command,
            CliProcessResult result,
            string? extraDetail)
        {
            var kind = Classify(result, extraDetail);

            var summary = kind switch
            {
                CliFailureKind.Timeout =>
                    $"{providerName} 호출이 제한 시간을 초과해 프로세스를 강제 종료했습니다. " +
                    "AiSettings:TimeoutSeconds 값을 늘리거나 더 작은 대상으로 나누어 실행하십시오.",
                CliFailureKind.QuotaExhausted =>
                    $"{providerName}의 구독 사용 한도가 소진되었습니다. " +
                    "appsettings.json에서 다른 CLI provider 또는 API provider로 변경한 뒤 다시 실행하십시오.",
                CliFailureKind.NotAuthenticated =>
                    $"{providerName}이(가) 로그인되어 있지 않습니다. " +
                    $"터미널에서 '{command}'를 직접 실행해 로그인을 완료하십시오.",
                // 종료 코드를 싣지 않는다. 이 실패는 종료 코드 0으로 도착하므로
                // "실패했습니다 (종료 코드: 0)"이라고 쓰면 자기모순이 된다.
                CliFailureKind.ToolPermissionDenied =>
                    $"{providerName}이(가) 헤드리스 모드에서 툴 권한 요청을 자동 거부해 빈 응답을 " +
                    "반환했습니다. 이 provider는 툴을 끄는 인자를 제공하지 않아 분석용 순수 LLM으로 " +
                    "사용할 수 없습니다. claude-cli 또는 API provider로 변경하십시오. " +
                    "(툴을 자동 승인하는 우회는 무인 배치에서 임의 명령 실행을 허용하므로 권장하지 않습니다.)",
                _ =>
                    $"{providerName} 호출이 실패했습니다 (종료 코드: {result.ExitCode})."
            };

            // 분류를 못 맞힌 경우에도 진단이 가능해야 한다. 원문을 자르지 않는다.
            //
            // 둘 중 하나만 싣던 이전 구현은 codex가 stderr에 진행 로그를 한 줄이라도 남기면
            // "codex가 결과 파일을 남기지 않았습니다" 같은 가장 구체적인 진단을 버렸다.
            // 종료 코드 0을 실패로 부르면서 이유를 말하지 않는 메시지가 남았다.
            // 둘 다 있으면 각각 자기 구획에 싣는다.
            var builder = new StringBuilder(summary);

            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                builder.Append("\n[CLI 출력]\n").Append(result.StandardError);
            }

            if (!string.IsNullOrWhiteSpace(extraDetail))
            {
                builder.Append("\n[추가 진단]\n").Append(extraDetail);
            }

            return new InvalidOperationException(builder.ToString());
        }

        public static InvalidOperationException CommandNotFound(
            string providerName,
            string command,
            Exception inner)
        {
            return new InvalidOperationException(
                $"{providerName}을(를) 실행할 수 없습니다. '{command}' 명령을 찾지 못했습니다. " +
                $"CLI가 설치되어 있는지, PATH에 등록되어 있는지 확인하거나 " +
                $"appsettings.json의 AiSettings:Providers:{providerName}:Command에 절대 경로를 지정하십시오. " +
                $"(원인: {inner.Message})",
                inner);
        }

        private static bool ContainsAny(string haystack, string[] markers)
        {
            foreach (var marker in markers)
            {
                if (ContainsMarker(haystack, marker))
                {
                    return true;
                }
            }

            return false;
        }

        // "429", "401" 같은 숫자 마커는 단어 경계 없이 매칭하면 "14293"이나 "40100ms" 같은
        // 무관한 숫자에도 걸린다. 숫자로만 이루어진 마커는 앞뒤가 숫자가 아닐 때만 인정한다.
        // 한 위치에서 경계 검사에 실패해도 뒤에 다른 위치가 있을 수 있으므로 계속 찾는다.
        private static bool ContainsMarker(string haystack, string marker)
        {
            if (!IsAllAsciiDigits(marker))
            {
                return haystack.Contains(marker, StringComparison.Ordinal);
            }

            var searchStart = 0;
            while (true)
            {
                var index = haystack.IndexOf(marker, searchStart, StringComparison.Ordinal);
                if (index < 0)
                {
                    return false;
                }

                var beforeIsBoundary = index == 0 || !char.IsAsciiDigit(haystack[index - 1]);
                var afterPosition = index + marker.Length;
                var afterIsBoundary = afterPosition >= haystack.Length || !char.IsAsciiDigit(haystack[afterPosition]);

                if (beforeIsBoundary && afterIsBoundary)
                {
                    return true;
                }

                searchStart = index + 1;
            }
        }

        private static bool IsAllAsciiDigits(string value)
        {
            if (value.Length == 0)
            {
                return false;
            }

            foreach (var c in value)
            {
                if (!char.IsAsciiDigit(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
