using System;
using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Models;

namespace ReSet.Core.Services
{
    /// <param name="Sentence">확정형 한 문장. 프롬프트와 L1이 이 값을 함께 쓴다.</param>
    public sealed record DatabasePlacementFact(string Sentence);

    /// <summary>
    /// 참조 객체가 이 객체와 같은 DB에 있는지를 확정 문장으로 만든다.
    ///
    /// [왜 추출이 아니라 번역인가] 재료는 이미 StaticAnalysis에 있다 -
    /// ThreePartObjectReferences와 LinkedServerReferences가 빈 배열이면 "크로스 DB
    /// 참조가 아니다"가 확정값이지 미확정 사항이 아니다. 그런데 2026-08-22 축 A
    /// 감사에서 명세서 9곳이 그 확정값을 "단언할 수 없습니다"로 되짚었다. 그래서
    /// 판단을 모델에게 맡기지 않고 문장으로 못박아 표에 싣는다.
    /// </summary>
    public static class DatabasePlacementExtractor
    {
        public static DatabasePlacementFact? Extract(
            SpStaticAnalysisResult? analysis, CodeObjectKey? objectKey)
        {
            // 파서가 실패했으면 ThreePartObjectReferences/LinkedServerReferences가
            // 비었다는 것 자체가 확정값이 아니라 "못 봤을 뿐"이다 - 소프트 페일로
            // 표를 아예 내지 않는다(AGENTS.md 범주 2).
            if (analysis == null || !analysis.IsParsedSuccessfully) return null;

            var threePart = analysis.ThreePartObjectReferences ?? new List<string>();
            var linked = analysis.LinkedServerReferences ?? new List<string>();
            var home = string.IsNullOrWhiteSpace(objectKey?.Database) ? "(미상)" : objectKey!.Database!;

            if (threePart.Count == 0 && linked.Count == 0)
            {
                return new DatabasePlacementFact(
                    $"참조 객체는 전부 `{home}` 로컬입니다. 3부 식별자 참조 0건, 연결 서버 참조 0건 — 확정값입니다.");
            }

            var parts = new List<string>();
            if (threePart.Count > 0)
            {
                parts.Add($"3부 식별자 참조 {threePart.Count}건: {string.Join(", ", threePart)}");
            }
            if (linked.Count > 0)
            {
                parts.Add($"연결 서버 참조 {linked.Count}건: {string.Join(", ", linked)}");
            }

            return new DatabasePlacementFact(
                $"소속 DB는 `{home}`이고 다음은 그 밖입니다 — {string.Join(" / ", parts)}.");
        }
    }
}
