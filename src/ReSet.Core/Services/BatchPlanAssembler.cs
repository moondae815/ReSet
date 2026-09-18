using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 골격 문서와 단계별 섹션을 하나의 계획서로 합친다.
    ///
    /// 조립은 모델이 넣은 자리표시자의 위치를 신뢰하지 않는다. 자리표시자가
    /// 빠지거나 순서가 틀려도 조립이 깨지지 않도록, 목록 순서대로 `## 단계별
    /// 이행 상세 및 의사코드` 블록 끝에 결정적으로 덧붙이고 자리표시자는 지운다.
    /// 프롬프트가 자리표시자를 요구하는 것은 모델이 단계 본문까지 써 버리는 것을
    /// 막기 위해서지, 조립이 그것에 의존하기 때문이 아니다.
    /// </summary>
    public static class BatchPlanAssembler
    {
        public const string StepDetailHeader = "## 단계별 이행 상세 및 의사코드";

        /// <summary>검증 SQL 세트 H2. 느슨하게 찾는다 - 모델이 꼬리표를 붙여 쓰는 일이 있다(LocateStepDetailBlock 주석 참고).</summary>
        public const string VerificationSetHeader = "## 통합 데이터 정합성 검증 SQL 세트";

        private static readonly Regex StepPlaceholderRegex = new(
            @"(?m)^[ \t]*<!--\s*STEP:[^>]*-->[ \t]*\r?\n?",
            RegexOptions.Compiled);

        /// <summary>
        /// 골격의 `## 단계별 이행 상세 및 의사코드` 본문(공통 규약 소절들)만 뽑는다.
        /// 단계별 호출에 그대로 실어, 13개 단계가 서로 다른 오류 처리 관례를
        /// 선언하는 일을 막는다.
        /// </summary>
        public static string ExtractSharedConventions(string? skeletonMarkdown)
        {
            var lines = Normalize(skeletonMarkdown);
            var (headerIndex, endIndex) = LocateStepDetailBlock(lines);
            if (headerIndex < 0)
            {
                return string.Empty;
            }

            return string.Join("\n", lines.Skip(headerIndex + 1).Take(endIndex - headerIndex - 1)).Trim();
        }

        /// <summary>
        /// 골격의 `## 통합 데이터 정합성 검증 SQL 세트` 절을 **헤딩 줄까지 포함해** 뽑는다.
        /// 단계별 호출의 공유 접두사에 그대로 실어, 단계가 이미 정의된 검증 SQL 을
        /// 다시 짓지 않고 이름으로 부르게 한다.
        ///
        /// [왜 필요한가 - B22 요청 본문 실측]
        /// 축 B 감사의 도달 1 위 가족이 「검증 SQL 이름 공간이 통째로 둘로 갈렸다」였다.
        /// 골격은 목차의 V01~V12 요구 87/87 을 받아 검증 세트 24 정의를 쓰는데,
        /// <b>단계 섹션 요청 20 건에는 그 본문이 0 건</b> 실린다 - <see cref="ExtractSharedConventions"/>
        /// 가 H2③ 만 자르기 때문이다. 그래서 S18 은 같은 검증을 새 이름으로 다시 짓고,
        /// 그중 일부가 원본이 커밋하는 입력을 거부한다.
        ///
        /// 요청 재생 실험(18 호출 $3.38, 자리 S18·S14 × 팔 3 × 3 회): 세트 이름 호출이
        /// <b>A0 0/3 → 세트를 실은 팔 3/3</b>(매번 23~24 이름), 자기 지역 이름 16·8·7 → 1,
        /// <b>신설 Error 검증 14·8·0 → 0·0·0</b>, 전재 0/18, 길이 0.55 배.
        /// 선언·판독: docs/audit-reports/2026-09-18-검증세트-단계요청-재생-{사전선언,판독}.md
        ///
        /// [왜 헤딩 줄을 포함하는가] 실은 것이 문서의 어느 절인지 모델이 알아야 한다.
        /// <see cref="ExtractSharedConventions"/> 는 본문만 주지만 그쪽은 「공통 규약」이라는
        /// 이름을 호출부가 따로 붙여 준다 - 이쪽은 절 제목 자체가 계약의 일부다.
        ///
        /// 실패는 예외가 아니라 빈 문자열이다. 못 잘라도 단계 생성은 종전대로 돌아야 한다.
        /// </summary>
        public static string ExtractVerificationSet(string? skeletonMarkdown)
        {
            var lines = Normalize(skeletonMarkdown);
            var (headerIndex, endIndex) = MarkdownSectionLocator.LocateSection(
                lines, VerificationSetHeader, "## ", exact: false);
            if (headerIndex < 0)
            {
                return string.Empty;
            }

            return string.Join("\n", lines.Skip(headerIndex).Take(endIndex - headerIndex)).Trim();
        }

        public static string Assemble(string? skeletonMarkdown, IReadOnlyList<string> stepSections)
        {
            var sections = (stepSections ?? Array.Empty<string>())
                .Where(section => !string.IsNullOrWhiteSpace(section))
                .Select(section => section.Trim())
                .ToList();

            var lines = Normalize(skeletonMarkdown);
            if (sections.Count == 0)
            {
                return string.Join("\n", lines);
            }

            var body = string.Join("\n\n", sections);
            var (headerIndex, endIndex) = LocateStepDetailBlock(lines);

            // 골격이 H2를 빠뜨렸더라도 단계 본문을 잃지 않는다. 아래에서 헤더를
            // 직접 합성해 붙이므로 문서 레벨 L1은 이 누락을 볼 수 없다 — 그래서
            // 여기서 조용히 버리면 그 결함을 잡아낼 곳이 아무 데도 없다.
            if (headerIndex < 0)
            {
                return string.Join("\n", lines).TrimEnd() + "\n\n" + StepDetailHeader + "\n\n" + body + "\n";
            }

            var merged = new List<string>(lines);
            merged.InsertRange(endIndex, new[] { string.Empty }.Concat(body.Split('\n')).Append(string.Empty));
            return string.Join("\n", merged);
        }

        private static List<string> Normalize(string? markdown)
        {
            var stripped = StepPlaceholderRegex.Replace(markdown ?? string.Empty, string.Empty);
            return MarkdownSectionLocator.SplitLines(stripped);
        }

        /// <summary>
        /// 단계 상세 H2의 헤더 줄 인덱스와, 그 블록이 끝나는(= 다음 H2가 시작하는)
        /// 인덱스를 돌려준다. 헤더가 없으면 (-1, -1).
        ///
        /// [왜 폴백이 있는가 - POQSettleProc17·18 연속 재발]
        /// 골격 프롬프트는 이 H2를 VERBATIM으로 쓰라고 요구하지만(AiService의 Skeleton
        /// Contract) 모델은 `## 단계별 이행 상세 및 의사코드:`처럼 꼬리표를 붙여 쓴다.
        /// 정확 일치로만 찾으면 못 찾고, 호출부가 문서 끝에 같은 H2를 새로 합성해
        /// 계획서에 H2가 둘이 된다 - 공통 규약 절과 단계 본문이 갈라지고,
        /// MechanicalValidator는 헤더를 Contains로 보므로 그 문서를 통과시킨다.
        ///
        /// 조립 시점의 문서는 골격 하나뿐이라 같은 텍스트를 담은 H2가 둘일 수 없다.
        /// 폴백이 잡는 자리는 유일하다.
        ///
        /// 폴백으로 찾았다는 사실은 남긴다. 조립이 성공했다고 계약 위반이 없던 일이
        /// 되지는 않으며, 흔적이 없으면 골격 프롬프트가 지켜지지 않는다는 사실을
        /// 아무도 보지 못한다.
        /// </summary>
        private static (int HeaderIndex, int EndIndex) LocateStepDetailBlock(List<string> lines)
        {
            var exact = MarkdownSectionLocator.LocateSection(lines, StepDetailHeader, "## ");
            if (exact.HeaderIndex >= 0) return exact;

            var loose = MarkdownSectionLocator.LocateSection(lines, StepDetailHeader, "## ", exact: false);
            if (loose.HeaderIndex >= 0)
            {
                Log.Warning(
                    "골격의 단계 상세 H2가 계약 문구와 달라 느슨하게 찾았습니다 - 기대: {Expected}, 실제: {Actual}",
                    StepDetailHeader, lines[loose.HeaderIndex].Trim());
            }

            return loose;
        }
    }
}
