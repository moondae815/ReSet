namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실제 산출물(`output/`)을 읽는 테스트가 코퍼스를 못 찾았을 때 쓰는 건너뜀 사유.
    ///
    /// 2026-08-23까지는 `if (ddl == null) return;`으로 **조용히 통과**했다. 그래서
    /// `output/`이 없는 워크트리(gitignore 대상이라 갓 만든 워크트리에는 없다)에서는
    /// 코퍼스 단언이 한 줄도 안 돌았는데도 `dotnet test`가 "통과"라고 찍었고, 다른
    /// 세션의 parallel-sdd 실행이 그 통과를 믿었다. 이제는 `Skip.If(..., Reason)`으로
    /// "건너뜀 N"이 보이게 한다 - reset-l1-check 스킬의 완료 기준이 "건너뜀 0"이므로
    /// 심링크를 빠뜨리면 기준이 자동으로 실패한다.
    ///
    /// CI·갓 클론한 저장소에서는 건너뜀으로 표시될 뿐 실패하지 않는다(실패시키면
    /// output/이 없는 환경이 전부 빨개진다 - AxisAGoldenCaseTests 머리 주석 참고).
    /// </summary>
    public static class CorpusSkip
    {
        public const string Reason =
            "output/ 코퍼스가 없어 건너뜀 - 워크트리라면 메인 저장소의 output/을 " +
            "심링크한다(`ln -s <main>/output output`, .git/info/exclude에 output 등록됨). " +
            "건너뜀 0이어야 코퍼스 단언이 실제로 돈 것이다.";
    }
}
