using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 프롬프트 캐시 중단점을 찍을지 판정한다.
    ///
    /// Anthropic은 캐시 쓰기에 1.25배(5분 TTL), 읽기에 0.1배를 청구한다. 실측에서 L2
    /// 리뷰는 5개 잡 중 4개가 1회차에 끝났으므로, 무조건 중단점을 찍으면 그 4건에서
    /// 손해가 확정되어 표본 전체로는 순손실이 난다. 두 번째 전송부터 찍으면 1회차 잡의
    /// 비용은 그대로이고 재생성 회차만 이득을 본다.
    ///
    /// "2회차인가?"는 "이 접두사를 전에 보냈는가?"와 같은 질문이므로, 회차 번호를
    /// 파이프라인에서 전달받지 않고 여기서 판정한다. 덕분에 IAiService가 바뀌지 않는다.
    ///
    /// 시계를 쓰지 않아 결정론적이다. 캐시 TTL(5분)을 넘겨 재전송되면 쓴 캐시가 읽히지
    /// 못하고 버려지는데, 그 손실은 감수한다 — 회차 간격을 예측해 회피하려는 시도는
    /// 과적합이다.
    /// </summary>
    public sealed class PromptCacheBreakpointPolicy
    {
        public const int DefaultCapacity = 64;

        private readonly ConcurrentDictionary<string, byte> _seen = new();
        private readonly ConcurrentQueue<string> _insertionOrder = new();
        private readonly int _capacity;

        public PromptCacheBreakpointPolicy(int capacity = DefaultCapacity)
        {
            _capacity = capacity > 0 ? capacity : DefaultCapacity;
        }

        /// <summary>
        /// 처음 보는 접두사면 false(중단점 없음), 이미 본 접두사면 true를 돌려준다.
        /// 판정에 실패하면 false를 돌려준다 — 중단점 없는 요청은 오늘과 동일한 동작이라
        /// 파이프라인을 멈추지 않는다.
        /// </summary>
        public bool ShouldMarkBreakpoint(string systemPrompt, string userPrompt)
        {
            try
            {
                var key = ComputeKey(systemPrompt, userPrompt);

                if (!_seen.TryAdd(key, 0))
                {
                    return true;
                }

                _insertionOrder.Enqueue(key);
                while (_insertionOrder.Count > _capacity && _insertionOrder.TryDequeue(out var evicted))
                {
                    _seen.TryRemove(evicted, out _);
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "프롬프트 캐시 중단점 판정 실패 - 중단점 없이 진행합니다.");
                return false;
            }
        }

        /// <summary>
        /// 접두사 본문이 아니라 해시만 들고 있는다. 두 프롬프트 사이에 NUL을 넣어
        /// 경계를 고정한다 — 넣지 않으면 ("AB", "C")와 ("A", "BC")가 같은 키가 된다.
        /// </summary>
        private static string ComputeKey(string systemPrompt, string userPrompt)
        {
            var bytes = Encoding.UTF8.GetBytes($"{systemPrompt}\u0000{userPrompt}");
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
    }
}
