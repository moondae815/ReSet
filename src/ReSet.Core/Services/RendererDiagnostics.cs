using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReSet.Core.Services
{
    /// <summary>
    /// 외부 렌더러(mermaid-cli)의 stderr 에서 <b>배송 문서에 실을 만큼</b>만 남긴다.
    ///
    /// [왜 - 2026-09-07 축 A 감사]
    /// <c>ValidateMermaid</c> 가 stderr 를 통째로 오류 메시지에 이어 붙였고, 그 메시지가
    /// 배너를 타고 배송 문서에 실렸다 - puppeteer 내부 프레임 10 여 줄, 로컬 homebrew
    /// 절대경로, <c>mermaid-cli-intercept.invalid</c> 라는 없는 호스트까지. 실측 세 편이다.
    /// 그 문서는 마이그레이션 코딩 에이전트가 읽는 <b>입력</b>이다.
    ///
    /// <b>전체 stderr 를 잃지는 않는다</b> - 바로 옆 <c>Log.Warning(… Stderr: {Stderr})</c> 가
    /// 그대로 남긴다. 사람이 볼 자리는 로그다.
    ///
    /// [왜 「앞 N 줄」이 아닌가]
    /// 줄 수로 박으면 mmdc 출력 형식이 바뀔 때 <b>조용히 낡는다</b>. 그래서 스택 프레임
    /// 줄의 모양으로 자르고, <b>프레임이 하나도 안 잡히면 전체를 그대로 둔다</b> -
    /// 「이 형식에는 X 가 없다」는 단정을 쓰지 않는 쪽으로 실패시킨다. 안전한 실패는
    /// 「덜 자른다」이지 「다 자른다」가 아니다.
    ///
    /// [무엇을 남기는가]
    /// mmdc 의 진단부는 프레임보다 <b>앞</b>에 온다 - `Error: Parse error on line N:` ·
    /// 문맥 줄 · 캐럿(^) 줄 · `Expecting … got 'X'`. 넷 다 남는다. 특히 캐럿은
    /// 재시도 프롬프트가 모델에게 자리를 알려 주는 <b>유일한 통로</b>라, 이것을 함께
    /// 베면 아무도 모르게 진단이 0 이 된다.
    /// </summary>
    public static class RendererDiagnostics
    {
        /// <summary>프레임이 안 잡힐 때를 위한 마지막 안전망. 진단부는 넷 줄이면 충분하다.</summary>
        private const int MaxLines = 12;

        /// <summary>
        /// 스택 프레임 줄. 두 모양이 있다 -
        /// <c>    at async CdpPage.$eval (file:///…)</c> 처럼 `at` 으로 시작하는 것과,
        /// <c>Parser.parseError (https://…)</c> 처럼 심볼 뒤에 바로 URL 이 오는 것.
        /// 후자가 실물에서 <b>첫</b> 프레임이라 `at` 만 보면 한 줄이 새어 나간다.
        /// </summary>
        private static readonly Regex StackFrameRegex = new Regex(
            @"^(?:at\s|\S+\s+\((?:https?|file):)", RegexOptions.Compiled);

        public static string TrimStackTrace(string? stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return string.Empty;
            }

            var lines = stderr.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var kept = new List<string>();
            foreach (var line in lines)
            {
                if (StackFrameRegex.IsMatch(line.TrimStart()))
                {
                    break;
                }

                kept.Add(line);
            }

            // 프레임을 하나도 못 찾았으면 자르지 않는다(위 주석의 실패 방향).
            if (kept.Count == lines.Length)
            {
                if (kept.Count <= MaxLines)
                {
                    return string.Join("\n", kept).TrimEnd();
                }

                return string.Join("\n", kept.Take(MaxLines))
                    + "\n(이하 생략 — 전체 컴파일 로그는 실행 로그에 있습니다.)";
            }

            return string.Join("\n", kept).TrimEnd();
        }
    }
}
