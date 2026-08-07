using System;

namespace ReSet.Core.Services
{
    /// <summary>
    /// Thinking.md의 본문 전체 — 헤더와 추론 본문 — 를 조립한다. 파일을 쓰는 자리는
    /// 전부 이곳을 거쳐야 하며, 헤더 문구를 다른 곳에서 새로 쓰지 마십시오.
    ///
    /// 추론 본문이 비어도 문서는 나온다. 이것이 이 클래스가 존재하는 이유다.
    /// 예전에는 호출부 네 곳이 각자 "본문이 비었으면 쓰지 않는다"를 구현했고,
    /// gpt-5 Responses API가 빈 summary를 돌려준 회차에서 docs/Thinking.md가 통째로
    /// 사라졌다 — README·docs/architecture.md·AGENTS.md가 산출물로 보장한 파일이다.
    /// 본문이 없다는 사실 자체가 기록할 가치가 있는 정보이므로,
    /// <see cref="ThinkingLogPlaceholder"/>가 소유한 사유 문구를 대신 싣는다.
    /// </summary>
    public static class ThinkingLogDocument
    {
        public static string Compose(
            string? thinkingText,
            string? providerName,
            string? modelName,
            string? effort,
            DateTime writtenAt)
        {
            var effortSuffix = string.IsNullOrWhiteSpace(effort) ? string.Empty : $", Effort: {effort}";
            var header =
                "# AI 추론 과정 로그 (Thinking Process Log)\n\n" +
                $"- **기본 분석 AI 정보**: {providerName} ({modelName}{effortSuffix})\n" +
                $"- **문서 작성일시**: {writtenAt:yyyy-MM-dd HH:mm:ss}\n\n" +
                "본 문서는 ReSet 파이프라인 수행 중 사용된 AI 모델들의 추론 과정(Thinking Process)을 기록한 마크다운 문서입니다.\n\n" +
                "---\n\n";

            var body = string.IsNullOrWhiteSpace(thinkingText)
                ? ThinkingLogPlaceholder.For(providerName)
                : thinkingText;

            return header + body;
        }
    }
}
