using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 정적 <see cref="Serilog.Log.Logger"/>를 테스트 동안 갈아 끼우는 클래스들을 한 묶음으로
    /// 직렬화한다.
    ///
    /// xUnit은 <b>클래스 사이</b>를 병렬로 돌린다. 두 클래스가 각자 스냅샷을 뜨고
    /// try/finally로 되돌리는데 그 구간이 겹치면, 나중에 도는 finally가 상대의 임시 로거를
    /// "원래 값"으로 알고 영구히 복원한다 - 그 뒤의 모든 테스트가 남의 싱크로 로그를 쓴다.
    /// 각 클래스 안의 try/finally는 옳지만 그것만으로는 막을 수 없는 종류의 경합이다.
    ///
    /// 새로 전역 로거를 교체하는 테스트 클래스를 만들면 반드시 이 컬렉션에 넣을 것.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class GlobalSerilogLoggerCollection
    {
        public const string Name = "GlobalSerilogLogger";
    }
}
