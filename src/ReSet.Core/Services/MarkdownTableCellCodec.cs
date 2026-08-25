using System.Collections.Generic;
using System.Text;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 마크다운 표 셀의 이스케이프/복원을 렌더와 대조가 공유하는 자리.
    ///
    /// [왜 중립 헬퍼로 옮겼는가 - 2026-08-21 최종 브랜치 리뷰 재라운드 Minor(설계)]
    /// 원래 <see cref="Escape"/>는 <c>AiService</c>의 private(이후 internal) 메서드였고
    /// <see cref="SplitRow"/>는 <c>MechanicalValidator</c>의 private 메서드였다 - 왕복의
    /// 두 짝이 서로 다른 클래스에 갈려 있었다. 그 결과 <c>MechanicalValidator</c>가
    /// <c>AiService</c>에 처음으로 컴파일 의존하게 됐는데, 이 저장소는 검증기가
    /// <c>SpDefinition</c>·<c>SpecExpectations</c> 같은 값 타입만 알고 조립기(AiService)를
    /// 몰라도 되게 하는 방향으로 계속 다듬어 왔다(<see cref="SpecExpectations"/> 참고) - 그
    /// 전례와 정반대다. "렌더와 대조가 한 함수를 공유해야 규칙이 갈리지 않는다"는 목적은
    /// 유지하되, 그 함수가 두 소비자 중 하나에 속하지 않도록 <see cref="MarkdownSectionLocator"/>
    /// 옆에 중립적으로 둔다.
    /// </summary>
    public static class MarkdownTableCellCodec
    {
        /// <summary>
        /// 마크다운 표 셀에 넣을 수 있게 다듬는다. SET 우변에 비트 연산자 `|`가 들어가면
        /// (예: FLAGS | 4) 셀 경계로 읽혀 표가 통째로 어긋난다. 개행도 같은 이유로 접는다.
        /// </summary>
        public static string Escape(string expression)
        {
            if (string.IsNullOrEmpty(expression)) return string.Empty;

            return CollapseNewlines(expression)
                .Replace("|", "\\|");
        }

        /// <summary>
        /// 개행만 공백 하나로 접는다. 스페이스·탭은 손대지 않는다 - <see cref="Escape"/>가
        /// 하는 정규화가 정확히 이만큼이기 때문이다.
        ///
        /// [왜 이 메서드를 따로 공개하는가 - 2026-08-24 축 B Fix Round 2] 렌더(Escape)와
        /// 추출기(예: `SetAssignmentExtractor`)가 원문에서 표 셀로 가는 정규화를 각자
        /// 따로 구현하면 둘이 갈릴 수 있다 - 렌더가 개행만 접는데 추출기가 그보다
        /// 덜 접으면(예: 리터럴 안 개행을 보존) 모델이 볼 수 있는 값(렌더된 값)과
        /// `MechanicalValidator`가 대조하는 fact가 영원히 어긋나 개행이 있는 값은
        /// 어떤 산출물도 만족시킬 수 없는 요구가 된다. 반대로 추출기가 더 접으면(예:
        /// 스페이스·탭까지 하나로 뭉개면) 렌더가 지키는 값보다 값 충실도를 공짜로
        /// 버린다. 이 메서드 하나를 두 소비자가 공유하게 해서 그 드리프트를 구조적으로
        /// 막는다 - 한쪽만 바뀌는 것이 불가능하다.
        /// </summary>
        public static string CollapseNewlines(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            return text
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ");
        }

        /// <summary>
        /// 마크다운 표 행을 `|`로 나누되, <see cref="Escape"/>가 렌더 시점에 남긴
        /// `\|`(이스케이프된 파이프)는 셀 경계로 보지 않고 칸 내용의 일부로 되돌린다
        /// (`|`로 복원). 이 복원이 없으면 셀 안의 `|`가 행 자체를 잘못 쪼갠다 -
        /// 실측: `Nm IN ('a|b','c')`가 렌더된 칸 `'a\|b', 'c'`를 단순 분할하면 행이
        /// 그 자리에서 잘못 쪼개져 마지막 조각이 `b'`만 남는다.
        /// </summary>
        public static List<string> SplitRow(string row)
        {
            var cells = new List<string>();
            var current = new StringBuilder();

            for (var i = 0; i < row.Length; i++)
            {
                var c = row[i];
                if (c == '\\' && i + 1 < row.Length && row[i + 1] == '|')
                {
                    current.Append('|');
                    i++;
                    continue;
                }

                if (c == '|')
                {
                    cells.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            cells.Add(current.ToString().Trim());
            return cells;
        }
    }
}
