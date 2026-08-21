using System;

namespace ReSet.Core.Services.Clients.Cli
{
    /// <summary>
    /// CLI 호출 실패. 분류 결과를 속성으로 보존한다.
    ///
    /// 이전에는 CliFailureClassifier가 CliFailureKind를 계산해 안내 문구로 녹인 뒤
    /// 평범한 InvalidOperationException을 돌려주고 kind를 버렸다. 그래서 재시도 판정이
    /// 그 문구를 다시 파싱해야 했다 - 산문 매칭은 문구가 바뀌면 아무 신호 없이 오작동한다.
    ///
    /// InvalidOperationException 하위형이므로 기존 catch가 그대로 잡는다.
    /// </summary>
    public sealed class CliInvocationException : InvalidOperationException
    {
        public CliFailureKind Kind { get; }

        public CliInvocationException(string message, CliFailureKind kind)
            : base(message)
        {
            Kind = kind;
        }
    }
}
