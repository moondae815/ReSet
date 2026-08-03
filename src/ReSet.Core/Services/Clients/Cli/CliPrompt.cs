namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// codex와 agy는 시스템 프롬프트를 별도로 받지 않는다. 둘 다 하나로 합쳐
    /// 넘겨야 하므로, 어느 한 클라이언트에 두지 않고 공용으로 둔다.
    /// </summary>
    public static class CliPrompt
    {
        public static string Combine(string systemPrompt, string userPrompt)
        {
            return string.IsNullOrWhiteSpace(systemPrompt)
                ? userPrompt
                : $"{systemPrompt}\n\n{userPrompt}";
        }
    }
}
