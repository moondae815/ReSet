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

            // objectKey?.Database가 비어 있으면 이 객체(프로시저 또는 함수 - 이 사실은
            // 함수 명세서 생성 경로에도 실린다)가 속한 DB의 "이름"만 모를 뿐, 3부
            // 식별자·연결 서버 참조 건수 자체는 파싱이 성공했으므로 여전히 확정값이다.
            // "(미상)"을 문장 안에 박으면 "확정값입니다"로 끝나는 문장에 미상값이 섞여
            // 표의 어조와 어긋난다(m2, 2026-08-22 최종 브랜치 리뷰) - 그래서 DB 이름을
            // 아예 언급하지 않는 문구로 대체하고, 확정된 건수만 그대로 싣는다. 파싱
            // 실패(:26-28)처럼 사실 자체를 못 내는 경우와 달리 여기서는 사실이 있으므로
            // 침묵하지 않는다.
            var home = objectKey?.Database;
            var hasHome = !string.IsNullOrWhiteSpace(home);

            if (threePart.Count == 0 && linked.Count == 0)
            {
                // m-b, Fix Round 1: "이 프로시저"라고 못박으면 함수 경로(AiService가
                // BuildMachineFactBlockLines(functionDef)를 부르는 자리)에서 거짓이 된다 -
                // 이 사실은 SP·함수 양쪽에 실린다. "이 객체"로 중립화한다.
                var subject = hasHome
                    ? $"참조 객체는 전부 `{home}` 로컬입니다."
                    : "참조 객체는 전부 이 객체와 같은 DB의 로컬 객체입니다.";
                return new DatabasePlacementFact(
                    $"{subject} 3부 식별자 참조 0건, 연결 서버 참조 0건 — 확정값입니다.");
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

            if (!hasHome)
            {
                // m-c, Fix Round 1: SqlStaticParser.ExplicitVisit(SchemaObjectName)는 3부
                // 식별자를 소속 DB 이름과 비교하지 않고 원문 부분 수만 보고 전부 담는다
                // (자기 자신을 3부로 적은 참조도 걸린다). 소속 DB 이름을 모르면 이 참조가
                // 실제로 "그 밖"인지 판정할 재료가 없다 - "다음은 그 밖입니다"라고 단정하면
                // m2를 고치다 이 클래스의 침묵 원칙을 한 칸 어기게 된다. 분류어 없이
                // 건수·목록만 싣는다.
                return new DatabasePlacementFact(
                    $"소속 DB 이름은 미상입니다. {string.Join(" / ", parts)}.");
            }

            return new DatabasePlacementFact(
                $"소속 DB는 `{home}`이고 다음은 그 밖입니다 — {string.Join(" / ", parts)}.");
        }
    }
}
