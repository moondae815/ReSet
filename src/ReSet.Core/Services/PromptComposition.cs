namespace ReSet.Core.Services
{
    /// <summary>
    /// 가변 접미사를 사용자 프롬프트에 합치는 규칙을 단독 소유한다.
    ///
    /// 메시지를 나눌 수 없는 경로(Chat Completions, Claude, Google, Ollama, CLI 등)가
    /// 전부 이곳을 쓴다. 각자 이어 붙이면 구분자가 달라져, 같은 작업인데 제공자마다
    /// 모델이 받는 프롬프트가 미묘하게 어긋난다.
    /// </summary>
    public static class PromptComposition
    {
        public static string MergeVolatileSuffix(string userPrompt, string? volatileUserSuffix) =>
            string.IsNullOrWhiteSpace(volatileUserSuffix)
                ? userPrompt
                : $"{userPrompt}\n\n{volatileUserSuffix}";
    }
}
