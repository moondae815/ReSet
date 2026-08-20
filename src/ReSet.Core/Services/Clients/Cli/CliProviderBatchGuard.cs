namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// 무인 배치 모드에서 CLI provider 사용을 차단할지 판정한다.
    ///
    /// 배치 도중 구독 쿼터가 소진되거나 CLI가 권한 프롬프트에서 멈추면 수십 분에서
    /// 수 시간짜리 실행이 통째로 날아간다. 시작 5초 만에 실패하는 편이 낫다.
    ///
    /// 다만 이 손실은 사람이 감수할 수 있는 종류다 - 종량제 API 키 없이 구독 계정만으로
    /// 배치를 돌려야 하는 실제 사정이 있다. 그래서 차단은 기본값으로 남기되
    /// <c>AiSettings:AllowCliProviderInBatch</c>로 열 수 있게 한다. 판단을 사람에게
    /// 넘기는 것이지 위험이 사라지는 것이 아니므로, 호출부는 통과시킬 때 경고를 남긴다.
    /// </summary>
    public static class CliProviderBatchGuard
    {
        /// <summary>
        /// 차단해야 할 역할 이름을 돌려준다. 문제가 없으면 null.
        /// criticProvider와 consolidatorProvider가 null이면 Actor 설정을 물려받는다.
        ///
        /// <paramref name="allowCliInBatch"/>가 true면 <c>claude-cli</c>와 <c>codex-cli</c>는
        /// 통과시키고 <c>agy-cli</c>만 계속 막는다. agy-cli는 툴 22종을 끌 수단이 없어
        /// 헤드리스에서 권한을 물을 수 없고, 자동 거부한 뒤 종료 코드 0과 빈 응답만 남긴다
        /// (실측: 46초·30,306토큰 소모 후 빈 응답). 배치 여부와 무관하게 분석 역할에서
        /// 깨지므로 옵트인의 대상이 아니다.
        /// </summary>
        public static string? FindBlockedRole(
            string actorProvider,
            string? criticProvider,
            string? consolidatorProvider,
            bool allowCliInBatch = false)
        {
            return FindRole(
                actorProvider,
                criticProvider,
                consolidatorProvider,
                allowCliInBatch ? IsAgyCli : IsCli);
        }

        /// <summary>
        /// CLI provider를 실제로 쓰는 역할 이름을 돌려준다. 없으면 null.
        ///
        /// 옵트인으로 통과시킨 실행에 경고를 남길지 판정한다. "차단할 역할"과는 다른
        /// 질문이다 - 옵트인을 켜면 차단 대상은 agy-cli뿐이지만, 경고는 claude-cli와
        /// codex-cli에도 필요하다. 통과시킨 것이지 위험이 사라진 것이 아니기 때문이다.
        /// </summary>
        public static string? FindCliRole(
            string actorProvider,
            string? criticProvider,
            string? consolidatorProvider)
        {
            return FindRole(actorProvider, criticProvider, consolidatorProvider, IsCli);
        }

        /// <summary>
        /// 세 역할을 Actor → Critic → Consolidator 순으로 훑어 술어에 걸리는 첫 역할을
        /// 돌려준다. 역할 provider가 null이면 Actor 설정을 물려받는 규칙을 여기 한 곳에만 둔다.
        /// </summary>
        private static string? FindRole(
            string actorProvider,
            string? criticProvider,
            string? consolidatorProvider,
            System.Func<string?, bool> matches)
        {
            if (matches(actorProvider))
            {
                return "Actor";
            }

            if (matches(criticProvider ?? actorProvider))
            {
                return "Critic";
            }

            if (matches(consolidatorProvider ?? actorProvider))
            {
                return "Consolidator";
            }

            return null;
        }

        // 이 네임스페이스는 ReSet.Core.Services.Clients 안에 있으므로
        // AiClientFactory가 using 없이 그대로 보인다.
        private static bool IsCli(string? provider) =>
            AiClientFactory.IsCliProvider(provider ?? string.Empty);

        private static bool IsAgyCli(string? provider) =>
            string.Equals(provider?.Trim(), "agy-cli", System.StringComparison.OrdinalIgnoreCase);
    }
}
