namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// 무인 배치 모드에서 CLI provider 사용을 차단할지 판정한다.
    ///
    /// 배치 도중 구독 쿼터가 소진되거나 CLI가 권한 프롬프트에서 멈추면 수십 분에서
    /// 수 시간짜리 실행이 통째로 날아간다. 시작 5초 만에 실패하는 편이 낫다.
    /// </summary>
    public static class CliProviderBatchGuard
    {
        /// <summary>
        /// 차단해야 할 역할 이름을 돌려준다. 문제가 없으면 null.
        /// criticProvider와 consolidatorProvider가 null이면 Actor 설정을 물려받는다.
        /// </summary>
        public static string? FindBlockedRole(
            string actorProvider,
            string? criticProvider,
            string? consolidatorProvider)
        {
            // 이 네임스페이스는 ReSet.Core.Services.Clients 안에 있으므로
            // AiClientFactory가 using 없이 그대로 보인다.
            if (AiClientFactory.IsCliProvider(actorProvider))
            {
                return "Actor";
            }

            if (AiClientFactory.IsCliProvider(criticProvider ?? actorProvider))
            {
                return "Critic";
            }

            if (AiClientFactory.IsCliProvider(consolidatorProvider ?? actorProvider))
            {
                return "Consolidator";
            }

            return null;
        }
    }
}
