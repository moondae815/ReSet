using System;

namespace ReSet.Core.Services.Clients.Cli
{
    public enum CliFailureKind
    {
        NotAuthenticated,
        QuotaExhausted,
        Timeout,
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

        public static CliFailureKind Classify(CliProcessResult result, string? extraDetail)
        {
            if (result.TimedOut)
            {
                return CliFailureKind.Timeout;
            }

            var haystack = $"{result.StandardError}\n{result.StandardOutput}\n{extraDetail}"
                .ToLowerInvariant();

            if (ContainsAny(haystack, QuotaMarkers))
            {
                return CliFailureKind.QuotaExhausted;
            }

            if (ContainsAny(haystack, AuthMarkers))
            {
                return CliFailureKind.NotAuthenticated;
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
                _ =>
                    $"{providerName} 호출이 실패했습니다 (종료 코드: {result.ExitCode})."
            };

            // 분류를 못 맞힌 경우에도 진단이 가능해야 한다. 원문을 자르지 않는다.
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? extraDetail
                : result.StandardError;

            var message = string.IsNullOrWhiteSpace(detail)
                ? summary
                : $"{summary}\n[CLI 출력]\n{detail}";

            return new InvalidOperationException(message);
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
                if (haystack.Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
