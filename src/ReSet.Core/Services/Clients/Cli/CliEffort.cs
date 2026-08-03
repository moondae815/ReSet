namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// ReSet의 effort(low|medium|high|xhigh)를 각 CLI가 받는 값으로 옮긴다.
    /// 알 수 없는 값이나 빈 값에는 null을 돌려주고, 호출자는 플래그를 아예 붙이지
    /// 않아 CLI 기본값을 따르게 한다.
    /// </summary>
    public static class CliEffort
    {
        /// <summary>claude는 low|medium|high|xhigh|max를 받는다. ReSet의 값이 그대로 통한다.</summary>
        public static string? ForClaude(string? effort)
        {
            return Normalize(effort) switch
            {
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "xhigh" => "xhigh",
                "max" => "max",
                _ => null
            };
        }

        /// <summary>
        /// codex와 agy는 low|medium|high 세 단계만 받는다. 그 위는 high로 낮춘다.
        /// 낮췄다는 사실을 호출자가 로그에 남길 수 있도록 clamped로 알린다 —
        /// 요청한 추론 강도가 조용히 떨어지면 품질 차이의 원인을 찾을 수 없다.
        /// </summary>
        public static string? ForThreeLevel(string? effort, out bool clamped)
        {
            clamped = false;

            switch (Normalize(effort))
            {
                case "low":
                    return "low";
                case "medium":
                    return "medium";
                case "high":
                    return "high";
                case "xhigh":
                case "max":
                    clamped = true;
                    return "high";
                default:
                    return null;
            }
        }

        private static string? Normalize(string? effort) =>
            string.IsNullOrWhiteSpace(effort) ? null : effort.Trim().ToLowerInvariant();
    }
}
