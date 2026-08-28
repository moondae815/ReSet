using System;
using System.Collections.Generic;
using System.Linq;

namespace ReSet.Core.Services.Clients
{
    /// <summary>
    /// OpenRouter의 백엔드 라우팅 선호. 같은 모델이라도 어느 제공자를 거치느냐에 따라
    /// 양자화·컨텍스트 길이·지원 파라미터가 달라, 분석 결과의 재현성이 흔들린다.
    /// 값이 지정된 항목만 요청에 실린다 - 비어 있으면 <c>provider</c> 필드 자체를
    /// 보내지 않아 OpenRouter의 기본 라우팅을 그대로 쓴다.
    /// </summary>
    public sealed class OpenRouterRoutingOptions
    {
        /// <summary>시도할 백엔드 제공자 순서(예: <c>anthropic</c>, <c>google-vertex</c>).</summary>
        public IReadOnlyList<string>? Order { get; init; }

        /// <summary><c>Order</c>가 모두 실패했을 때 다른 제공자로 넘어갈지 여부.</summary>
        public bool? AllowFallbacks { get; init; }

        /// <summary>요청에 실린 파라미터를 모두 지원하는 제공자로만 라우팅할지 여부.</summary>
        public bool? RequireParameters { get; init; }

        public bool IsEmpty =>
            (Order is null || Order.Count == 0) && !AllowFallbacks.HasValue && !RequireParameters.HasValue;

        /// <summary>
        /// 설정 값에서 라우팅 선호를 읽는다. 아무것도 지정되지 않았으면 <c>null</c>을
        /// 돌려주어 호출부가 요청에 <c>provider</c> 필드를 넣지 않도록 한다.
        ///
        /// 문자열을 받는 것은 이 프로젝트가 설정을 IConfiguration 인덱서로 읽기 때문이며,
        /// 덕분에 ReSet.Core는 설정 패키지에 의존하지 않는다. 참/거짓으로 읽히지 않는
        /// 값은 무시한다 - 오타 하나로 분석 기동이 죽는 것보다 기본 라우팅으로 도는
        /// 편이 낫다.
        /// </summary>
        public static OpenRouterRoutingOptions? Parse(
            IEnumerable<string>? order,
            string? allowFallbacks,
            string? requireParameters)
        {
            var cleanedOrder = order?
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => o.Trim())
                .ToArray();

            var options = new OpenRouterRoutingOptions
            {
                Order = cleanedOrder is { Length: > 0 } ? cleanedOrder : null,
                AllowFallbacks = bool.TryParse(allowFallbacks, out var af) ? af : null,
                RequireParameters = bool.TryParse(requireParameters, out var rp) ? rp : null
            };

            return options.IsEmpty ? null : options;
        }

        /// <summary>
        /// 두 라우팅 선호를 항목 단위로 겹친다 - <paramref name="overrideOptions"/>에서
        /// 값이 지정된 항목만 <paramref name="baseOptions"/>를 덮고, 지정되지 않은
        /// 항목은 바탕의 값이 그대로 남는다.
        ///
        /// 통째로 대체하지 않는 이유: 모델별 목록은 대개 <c>Order</c>만 적는다.
        /// 대체 방식이면 그때 <c>AllowFallbacks</c>가 조용히 null이 되어 요청에서
        /// 빠지고, 목록 밖 백엔드(fp4 양자화 포함)로의 이동이 말없이 다시 열린다.
        /// </summary>
        public static OpenRouterRoutingOptions? Merge(
            OpenRouterRoutingOptions? baseOptions,
            OpenRouterRoutingOptions? overrideOptions)
        {
            if (overrideOptions is null)
            {
                return baseOptions;
            }
            if (baseOptions is null)
            {
                return overrideOptions;
            }

            var merged = new OpenRouterRoutingOptions
            {
                Order = overrideOptions.Order ?? baseOptions.Order,
                AllowFallbacks = overrideOptions.AllowFallbacks ?? baseOptions.AllowFallbacks,
                RequireParameters = overrideOptions.RequireParameters ?? baseOptions.RequireParameters
            };

            return merged.IsEmpty ? null : merged;
        }
    }
}
