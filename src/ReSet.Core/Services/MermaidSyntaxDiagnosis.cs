using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// mermaid 파스 오류의 <b>원인을 이름으로</b> 말한다. 렌더러 원문만 돌려주면 모델이
    /// 무엇을 고칠지 모른 채 같은 문서를 다시 쓴다.
    ///
    /// [왜 필요한가 - 2026-09-10 실측] 캐시 v22 재생성 첫 판에서
    /// <c>UF_GET_INCVTAXRATE</c> 가 5 시도를 **글자까지 같은 오류**로 태웠다. 원인은
    /// 모델이 노드 <b>ID</b> 를 따옴표로 감싼 것인데(<c>"시작"["함수 호출: …"]</c>),
    /// L1 이 돌려준 것은 mmdc 원문(<c>… got 'STR'</c>)뿐이었다. 그 판의 mermaid 블록은
    /// 정상 ID 0 · 따옴표 ID 40 이라 31 편 전부에서 같은 일이 났을 자리다.
    ///
    /// 같은 부류를 이미 고친 전례가 있다 - <c>17da79de</c> 가 「매핑 표가 없습니다」를
    /// 「헤딩 레벨 `####` 로 쓰여 있습니다 - 표 자체는 있습니다」로 바꾸자 한 시도에
    /// 닫혔다. <b>고치는 것은 판정이 아니라 문구다.</b>
    ///
    /// [범위를 좁게 두는 이유] 진단을 지어내면 틀린 원인을 확신 있게 말하게 되고,
    /// 그것은 원문만 주는 것보다 나쁘다. 그래서 <b>코퍼스에서 실제로 관측된 모양 하나</b>
    /// 로 시작한다(작성 계약 §8 - 트리거는 코퍼스가 정한다). 새 모양이 실측되면 그때
    /// 갈래를 더한다.
    /// </summary>
    public static class MermaidSyntaxDiagnosis
    {
        /// <summary>
        /// 노드 <b>ID 자리</b>에 놓인 따옴표 문자열. 줄머리(또는 화살표 뒤)에서 시작해
        /// 곧바로 모양 괄호가 오는 것만 센다 - 라벨 <b>안</b>의 따옴표·대괄호는 정상이므로
        /// 「따옴표 뒤에 대괄호」라는 넓은 그물을 쓰면 멀쩡한 라벨을 문다.
        /// </summary>
        private static readonly Regex QuotedNodeId = new(
            @"(?m)(?:^|-->|---|==>|-\.->)\s*""(?<id>[^""\r\n]+)""\s*(?:\[|\{|\(|>)",
            RegexOptions.Compiled);

        /// <summary>
        /// 원인을 이름으로 말할 수 있으면 그 문장을, 못 하면 <c>null</c> 을 돌려준다.
        /// null 이면 호출부는 종전대로 렌더러 원문만 싣는다 - <b>모르면 지어내지 않는다.</b>
        /// </summary>
        public static string? Explain(string? mermaidContent)
        {
            if (string.IsNullOrWhiteSpace(mermaidContent)) return null;

            var ids = QuotedNodeId.Matches(mermaidContent)
                .Select(m => m.Groups["id"].Value.Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0) return null;

            var shown = ids.Take(5).ToList();
            var tail = ids.Count > shown.Count ? $" 외 {ids.Count - shown.Count}개" : string.Empty;

            // 문구에 「…십시오」류 지시 어미를 쓰지 않는다 - 이 문장은 재시도 프롬프트로
            // 돌아가고, 모델이 문서에 옮기면 CheckDocumentInstructsItsAuthor 가 발화해
            // 고칠 것이 없는데 재시도가 소진된다(그 검사 주석의 자기강화 루프와 같은 자리).
            return
                "원인: 노드 ID가 따옴표로 감싸여 있습니다. mermaid에서 따옴표는 "
                + "대괄호·중괄호 **안의 라벨**에만 쓰고, 그 앞의 ID는 맨 영숫자여야 합니다. "
                + $"해당 ID {ids.Count}개: {string.Join(", ", shown.Select(i => "\"" + i + "\""))}{tail}. "
                + "옳은 모양은 `START[\"함수 호출: …\"]` · `CHK{\"@@ERROR <> 0\"}`이고, "
                + "틀린 모양이 `\"시작\"[\"함수 호출: …\"]`입니다. "
                + "ID는 화살표 양쪽에서도 같은 맨 토큰으로 씁니다(`START --> CHK`).";
        }
    }
}
