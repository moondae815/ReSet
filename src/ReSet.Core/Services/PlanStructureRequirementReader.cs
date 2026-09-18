using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 목차(PlanStructure) 마크다운에서 <b>단계별 요구 불릿</b>을 읽는다.
    ///
    /// [왜 <see cref="BatchStepPlan"/>이 아닌 별 파서인가]
    /// <see cref="BatchStepPlan"/>은 목차의 ```json 블록이 선언하는 것(코드·이름·테이블·오류 코드)이고,
    /// 요구는 그 옆 <b>산문 불릿</b>이다. 출처가 달라 한 레코드로 겸할 수 없다.
    ///
    /// [왜 필요한가 - B21 요청 본문 실측]
    /// 축 B 감사에서 가장 넓은 가족이 「목차가 건 요구에 대응 절·항목이 없다」(12 건 / 9 단계)였고,
    /// 원인은 <b>단계 섹션 요청 24 건 중 목차 요구 문구가 실린 것 0</b>(골격 요청은 3/3)이었다.
    /// 단계 작성자에게 그 요구를 실을 칸이 없었다. 요청 재생 실험에서 불릿을 싣자 대응이
    /// 0/9 → 9/9 로 뒤집혔다. 판독: docs/audit-reports/2026-09-18-목차요구-단계요청-재생-판독.md
    ///
    /// [헤딩 모양은 판마다 다르다 - 실측]
    /// 같은 프롬프트가 만든 목차가 단계를 `#### S03 — 이름`(B11·B12·B15·B18·B21)에도,
    /// `### S01 이름`(B13·B14·B19·B20)에도, `### S00. 이름`(B5)에도 뒀다. 그래서 레벨을
    /// 고정하지 않고 <b>단계 헤딩의 레벨을 읽어 그보다 얕거나 같은 헤딩에서만 블록을 끊는다</b> -
    /// B20 은 단계 헤딩(H3) 아래 `#### 목적과 검증 항목` 소제목이 불릿을 나눠 담는데, 소제목에서
    /// 끊으면 요구가 통째로 사라진다.
    ///
    /// 실패는 예외가 아니라 빈 사전이다. 요구를 못 읽어도 단계 생성은 종전대로 돌아야 한다 -
    /// 이 재료는 개선이지 필수가 아니다.
    /// </summary>
    public static class PlanStructureRequirementReader
    {
        // 단계·검증 코드 헤딩. `#### S03 — 이름` · `### S01 이름` · `### S00. 이름` 을 모두 받는다.
        private static readonly Regex StepHeading = new(
            @"^(?<level>#{2,5})\s+(?<code>[A-Z]{1,3}\d{2,3})\b",
            RegexOptions.Compiled);

        private static readonly Regex AnyHeading = new(@"^(?<level>#{1,6})\s", RegexOptions.Compiled);

        // `- ` 와 `* ` 만 받는다. 번호 목록은 받지 않는다 - 효력을 측정한 실험이 대시 불릿만
        // 실었고, 재지 않은 모양으로 갈아타지 않는다.
        private static readonly Regex Bullet = new(@"^\s*[-*]\s+(?<text>\S.*)$", RegexOptions.Compiled);

        // 요구가 아니라 메타를 적는 불릿. 이것을 요구로 실으면 모델이 「레거시 기원: 없음」을
        // 덮어야 할 요구로 읽는다.
        private static readonly Regex MetaBullet = new(
            @"^\s*[-*]\s+\**(레거시 기원|원본|대상 테이블|오류 코드|청킹|Legacy|TargetTables|ErrorCodes|Chunkable)\s*[:：]",
            RegexOptions.Compiled);

        /// <summary>
        /// 단계 코드 → 그 단계의 요구 불릿(원문 줄, 앞뒤 공백만 다듬는다).
        /// 불릿이 하나도 없는 블록은 <b>키 자체를 내지 않는다</b> - 빈 목록을 내면 호출부가
        /// 「목차가 요구를 안 냈다」와 「이 판의 목차가 산문이다」를 구분할 수 없다.
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(string? planStructureMarkdown)
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(planStructureMarkdown))
            {
                return result;
            }

            string? currentCode = null;
            var currentLevel = 0;
            List<string>? bullets = null;

            void Flush()
            {
                if (currentCode != null && bullets is { Count: > 0 })
                {
                    // 같은 코드가 두 번 나오면(목차가 단계를 두 자리에 적은 판) 뒤엣것을
                    // 버리지 않고 이어 붙인다 - 어느 쪽이 정본인지 이 파서가 정할 일이 아니다.
                    result[currentCode] = result.TryGetValue(currentCode, out var had)
                        ? had.Concat(bullets).ToList()
                        : bullets;
                }

                bullets = null;
            }

            foreach (var raw in MarkdownSectionLocator.SplitLines(planStructureMarkdown))
            {
                var line = raw;
                var heading = AnyHeading.Match(line);
                if (heading.Success)
                {
                    var step = StepHeading.Match(line);
                    if (step.Success)
                    {
                        Flush();
                        currentCode = step.Groups["code"].Value;
                        currentLevel = step.Groups["level"].Value.Length;
                        bullets = new List<string>();
                        continue;
                    }

                    // 단계 헤딩보다 깊은 소제목은 블록을 끊지 않는다.
                    if (currentCode != null && heading.Groups["level"].Value.Length <= currentLevel)
                    {
                        Flush();
                        currentCode = null;
                    }

                    continue;
                }

                if (currentCode == null)
                {
                    continue;
                }

                var bullet = Bullet.Match(line);
                if (bullet.Success && !MetaBullet.IsMatch(line))
                {
                    bullets!.Add(line.Trim());
                }
            }

            Flush();
            return result;
        }
    }
}
