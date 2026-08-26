using System;
using System.Collections.Generic;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 코퍼스 단계 지시서를 전수로 훑어 단계 검사 A~E의 발화량을 잰다.
    ///
    /// [왜 디스크를 모르는가] 로직이 CLI에 있으면 테스트가 코퍼스 의존 골든이 되고,
    /// 코퍼스가 없을 때 Skip으로 조용히 통과한다(CoverageMapGoldenTests가 그렇다).
    /// 측정을 재현 가능하게 만드는 것이 이 도구의 목적인데 그 도구의 회귀가 초록으로
    /// 숨으면 목적을 스스로 배반한다. 파일 읽기는 SweepCommand에만 있다.
    /// </summary>
    public static class StepSweepService
    {
        /// <summary>
        /// 원본 DDL에서 캐시 17 이후의 코드→서수 사전을 만든다.
        ///
        /// [왜 표로 렌더링해서 리더에 먹이는가] ExtractErrorCodes의 결과를 직접 사전으로
        /// 접으면 중복 코드 처리 규칙이 두 곳에 생긴다. 제품의 규칙
        /// (SpecStatementFactsExtractor.ReadErrorCodeToOrdinal:299 - 중복이면 덮어쓰지 않고
        /// 아예 빼고, dropped로 세 번째 등장도 막는다)과 조금만 달라도 실제 파이프라인이
        /// 결코 만들지 않을 사전으로 측정하게 된다. 읽는 쪽을 제품 코드 그대로 쓴다.
        /// </summary>
        public static IReadOnlyDictionary<string, (string Kind, int Ordinal)>
            BuildSimulatedErrorCodeMap(string? ddl, string dateParameterName)
        {
            var facts = DmlScopeExtractor.ExtractErrorCodes(ddl, dateParameterName);
            if (facts.Count == 0)
            {
                return new Dictionary<string, (string, int)>(StringComparer.Ordinal);
            }

            var synthesized = SpecStatementFactsExtractor.Extract(
                new List<(string FileName, string Content)>
                {
                    ("sweep.synthetic", RenderErrorCodeTable(facts)),
                });

            return synthesized.TryGetValue("synthetic", out var parsed)
                ? parsed.ErrorCodeToOrdinal
                : new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        }

        /// <summary>
        /// ExtractErrorCodes의 결과를 명세서에 실리는 표 모양으로 되돌린다.
        ///
        /// 헤딩과 열 이름은 AiService가 프롬프트에 싣는 것과 같아야 한다 - 어긋나면
        /// ReadErrorCodeToOrdinal이 표를 못 찾아 빈 사전이 나온다. 그 실패는 조용하지
        /// 않다: 조건 (B)의 발화가 통째로 0이 되어 보고서에 드러난다.
        /// </summary>
        public static string RenderErrorCodeTable(IReadOnlyList<ErrorCodeFact> facts)
        {
            var builder = new StringBuilder();
            builder.AppendLine(DmlScopeExtractor.ErrorCodeTableHeading);
            builder.AppendLine();
            builder.AppendLine("| 문장 | 오류 코드 | 설정 대상 |");
            builder.AppendLine("| :--- | :--- | :--- |");

            foreach (var fact in facts)
            {
                builder.AppendLine(
                    $"| {fact.Operation} {fact.StatementOrdinal} | {fact.Code} | {fact.Variable} |");
            }

            return builder.ToString();
        }
    }
}
