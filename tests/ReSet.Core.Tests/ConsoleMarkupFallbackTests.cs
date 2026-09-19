using ReSet.Cli;
using Spectre.Console;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// 상태 한 줄의 마크업 결함이 파이프라인을 죽이지 않는다.
///
/// 실물(POQSettleBatch24, 2026-09-19 23:18): 단계 하한 검사 발화가 AI 본문을 인용하는데
/// 그 본문에 T-SQL 대괄호 식별자 <c>S.[value]</c>가 있었다. Spectre가 그것을 스타일 태그로
/// 읽어 "Could not find color or style 'value'"를 던졌고, 예외가 단계 태스크를 죽여
/// 19단계를 마친 회차 전체가 실패로 접혔다. 앞선 시도는 L1을 못 넘겨 점수가 없었으므로
/// 구제할 후보도 없어 40분치 유료 호출이 산출물 없이 버려졌다.
///
/// 발화 문구를 이스케이프하는 것(<c>VerificationPipelineOrchestrator</c>)과 별개로,
/// <b>어떤 문구가 와도 콘솔이 파이프라인을 죽이지 않는다</b>는 것이 이 시험의 몫이다.
/// 두 층은 서로를 대신하지 못한다 - 이스케이프를 빠뜨린 새 발화가 언젠가 또 생긴다.
/// </summary>
public sealed class ConsoleMarkupFallbackTests
{
    /// <summary>실제로 중단시킨 그 문구. 로그 4163줄의 예외가 이것에서 났다.</summary>
    private const string AiBodyWithBracketIdentifier =
        "  [grey]* S06 단계가 하한 검사를 통과하지 못해 다시 생성합니다: " +
        "「SELECT S.[value] FROM STRING_SPLIT(@v_strCardPGNames, '+') AS S」[/]";

    [Fact]
    public void SafeMarkupText_WhenTheMessageCarriesAnUnknownStyle_ReturnsTextSpectreCanParse()
    {
        var safe = ConsoleUserInteraction.SafeMarkupText(AiBodyWithBracketIdentifier);

        // Markup 생성자가 문자열 전체를 파싱한다 - 콘솔 없이 파싱 가능성만 잰다.
        Assert.Null(Record.Exception(() => { _ = new Markup(safe); }));
    }

    [Fact]
    public void SafeMarkupText_KeepsTheOffendingTextInTheOutput()
    {
        // 못 읽는 문구를 버리면 사람이 무엇 때문에 재생성이 돌았는지 알 수 없다.
        var safe = ConsoleUserInteraction.SafeMarkupText(AiBodyWithBracketIdentifier);

        Assert.Contains("value", safe);
        Assert.Contains("STRING_SPLIT", safe);
    }

    [Fact]
    public void SafeMarkupText_LeavesAValidMarkupMessageUntouched()
    {
        // 색은 살아 있어야 한다 - 언제나 전량 이스케이프로 도망가면 화면이 태그로 덮인다.
        const string valid = "  [grey]* S06 단계를 다시 생성합니다[/]";

        Assert.Equal(valid, ConsoleUserInteraction.SafeMarkupText(valid));
    }

    [Fact]
    public void NotifyStatus_DoesNotThrowOnTheMessageThatStoppedPOQSettleBatch24()
    {
        var userInteraction = new ConsoleUserInteraction();

        Assert.Null(Record.Exception(() => userInteraction.NotifyStatus(AiBodyWithBracketIdentifier)));
    }
}
