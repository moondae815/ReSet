using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    public interface IAiClient
    {
        string ProviderName { get; }
        string ModelName { get; }
        /// <summary>
        /// <paramref name="volatileUserSuffix"/>는 요청마다 달라지는 짧은 지시다.
        /// 이것을 <paramref name="userPrompt"/>에 이어 붙여 넘기지 마십시오 — gpt-5.6 이후
        /// 모델은 암묵적 cache breakpoint를 마지막 메시지에 놓고 그 지점의 접두사 전체를
        /// 비교하므로, 공통 컨텍스트 뒤에 한 줄만 달라져도 캐시가 통째로 죽습니다.
        /// 별도 인자로 넘기면 Responses API 경로가 별개 메시지로 떼어내고, 그 외 경로는
        /// 이어 붙여 모델이 받는 내용을 같게 유지합니다.
        /// </summary>
        Task<AiResult> ChatAsync(string systemPrompt, string userPrompt, float temperature, string? effort = null, string? volatileUserSuffix = null, CancellationToken cancellationToken = default);
    }
}
