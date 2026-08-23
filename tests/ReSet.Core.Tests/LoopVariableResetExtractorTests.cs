using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class LoopVariableResetExtractorTests
    {
        private static readonly Dictionary<string, string> NoColumns = new();

        [Fact]
        public void Extract_ConstantSetAtTopOfWhileBody_ShouldSayItRepeatsEachIteration()
        {
            // UP_UTIL_SETTLE_PROC_ETC:69 실측(🔴) - WHILE 본문 첫 문장 SET @v_intID = 0이
            // 커서 행마다 재설정한다. 이 사실이 없으면 이행자가 재설정을 빠뜨리고, 무매칭
            // 행에서 선행 ID가 남아 IF @v_intID > 0이 반대 갈래를 타 UPDATE가 0행 갱신 →
            // 신규 INSERT 누락 → 금액 검증 불일치로 배치 전량 롤백된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT = 0
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SET @v_intID = 0
        SELECT @v_intID = ID FROM dbo.TA WITH(NOLOCK)
    END
END";

            var fact = Assert.Single(LoopVariableResetExtractor.Extract(ddl));

            Assert.Equal(7, fact.Line);
            Assert.Equal("@v_intID", fact.Variable);
            Assert.Equal("0", fact.Value);
            Assert.Contains("반복마다", fact.Sentence);
        }

        [Fact]
        public void Extract_SetOutsideLoop_ShouldNotBeCollected()
        {
            // 루프 밖 SET은 DECLARE 초기값과 다르지 않다 - 담을 사실이 없다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SET @v = 0
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_SetNestedInsideIfWithinLoop_ShouldNotBeCollected()
        {
            // 코퍼스 실물 6건이 이 모양이다(PROC_ETC:139 · SUMMARY_ETC:76·77·128·129 ·
            // WORKDAY2:36). 조건 안에 있으면 "반복마다 다시 실행된다"가 거짓이다 -
            // PROC_ETC:139는 뒤에 RETURN이 붙어 있어 평생 한 번 실행될 수도 있다.
            // 이 테스트가 "루프 본문 최상위" 규칙이 실제로 가르는 것이 있음을 못박는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @po_intRetVal INT
    WHILE (@@FETCH_STATUS = 0) BEGIN
        IF @@ERROR <> 0 BEGIN
            SET @po_intRetVal = -3
            RETURN
        END
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_AccumulatorSetInLoop_ShouldNotBeCollected()
        {
            // UF_GET_WORKDAY2:31 · SUMMARY_ETC:135 실측. 자기 자신을 읽어 더하는 대입은
            // 재설정이 아니라 누적이다 - 직전 반복의 값이 남는 것이 버그가 아니라 요점이다.
            // 종류 칸 "루프 내 재설정" 자체가 주장이므로 이런 행은 첫 칸부터 거짓이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intIdx INT = 0
    WHILE @v_intIdx < 10 BEGIN
        SET @v_intIdx = @v_intIdx + 1
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_NonConstantSetInLoop_ShouldNotBeCollected()
        {
            // SUMMARY_ETC:77·129 실측 - 문자열 이어붙이기 대입. 다른 변수를 읽으므로
            // "이 지점의 값은 언제나 이 상수"가 거짓이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @po_strErrMsg VARCHAR(200), @v_strClientID VARCHAR(20)
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SET @po_strErrMsg = 'DELETE 실패(' + @v_strClientID + ')'
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_SetPrecededByConditionalExit_ShouldNotBeCollected()
        {
            // "반복마다 다시 실행된다"는 앞선 문장이 루프를 벗어나지 않을 때만 참이다.
            // WHILE 1=1 + IF ... BREAK는 흔한 커서 관용구다. 코퍼스에는 이 모양이 0건이나
            // (실측), 조건을 확인할 수 있는데 확인하지 않으면 거짓 문장이 표에 실린다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    WHILE 1 = 1 BEGIN
        IF @@FETCH_STATUS <> 0 BREAK
        SET @v = 0
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_NegativeLiteral_ShouldBeCollected()
        {
            // 부호 붙은 리터럴도 상수다 - ScriptDom은 -1을 UnaryExpression으로 싼다.
            // 여기서 끊으면 SET @v = 0은 담고 SET @v = -1은 조용히 빠지는 구멍이 생긴다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intIdx INT
    WHILE @v_intIdx < 10 BEGIN
        SET @v_intIdx = -1
    END
END";

            var fact = Assert.Single(LoopVariableResetExtractor.Extract(ddl));

            Assert.Equal("@v_intIdx", fact.Variable);
            Assert.Equal("-1", fact.Value);
        }

        [Fact]
        public void Extract_WhileBodyWithoutBeginEnd_ShouldBeCollected()
        {
            // BEGIN/END 없는 단일 문장 본문도 그 문장은 최상위다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    WHILE (@@FETCH_STATUS = 0)
        SET @v = 0
END";

            var fact = Assert.Single(LoopVariableResetExtractor.Extract(ddl));

            Assert.Equal("@v", fact.Variable);
        }

        [Fact]
        public void Extract_CompoundAssignmentInLoop_ShouldNotBeCollected()
        {
            // SET @v += 1은 오른쪽이 리터럴이어도 직전 값을 읽는다 - 재설정이 아니다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT = 0
    WHILE @v < 10 BEGIN
        SET @v += 1
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_UnparseableDdl_ShouldReturnEmpty()
        {
            // AGENTS.md 범주 2 - 파싱은 실패할 수 있으므로 소프트 페일한다.
            Assert.Empty(LoopVariableResetExtractor.Extract("CREATE PROCEDURE ((("));
            Assert.Empty(LoopVariableResetExtractor.Extract(null));
            Assert.Empty(LoopVariableResetExtractor.Extract("   "));
        }

        [Fact]
        public void Collect_LoopVariableReset_ShouldLandInTheExecutionSemanticsTable()
        {
            // 추출기가 사실을 내도 Collect에 갈래가 없으면 표에 한 행도 실리지 않는다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT = 0
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SET @v_intID = 0
    END
END";

            var facts = ExecutionSemanticsFacts.Collect(ddl, null, null, NoColumns);

            var fact = Assert.Single(
                facts, f => f.Kind == ExecutionSemanticsFacts.LoopVariableResetKind);
            Assert.Equal("루프 내 재설정", fact.Kind);
            Assert.Equal("7", fact.Line);
            Assert.Equal("SET @v_intID = 0", fact.Target);
            Assert.Contains("반복마다", fact.Fact);
        }

        [Fact]
        public void Extract_Sentence_ShouldNotDenyTheDeclareInitialValue()
        {
            // 수정 라운드 1 - "DECLARE 시점의 값도 ... 아닙니다"는 출처가 아니라 값을
            // 부정하는 말로도 읽힌다. 이 문장이 실리는 코퍼스 3행 전부에서 재설정값과
            // DECLARE 초기값이 똑같은 0이라(PROC_ETC 22·32·33행 DECLARE, 69·113·114행
            // 재설정), 값으로 읽으면 3행 중 3행에서 거짓이 된다. 실행 의미 표는 Critic
            // 면제이고 L1이 축자 전사를 강제하므로 모호한 절을 걸러 낼 장치가 없다.
            // 출처만 말하면 두 읽기 모두에서 참이다.
            //
            // 덤으로 2차 위험도 닫힌다 - 지역 변수 표가 "@v_intID 초기값 0"을 싣고 있어,
            // 값 부정으로 읽히는 이 행과 맞붙으면 산문이 기계 확정 표를 뒤집었다고
            // 보고될 수 있다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_intID INT = 0
    WHILE (@@FETCH_STATUS = 0) BEGIN
        SET @v_intID = 0
    END
END";

            var fact = Assert.Single(LoopVariableResetExtractor.Extract(ddl));

            Assert.Contains("DECLARE의 초기값이 아니라", fact.Sentence);
            Assert.DoesNotContain("DECLARE 시점의 값도", fact.Sentence);
        }

        [Fact]
        public void Extract_SetPrecededByRaiserror_ShouldNotBeCollected()
        {
            // RAISERROR도 반복을 벗어날 수 있다 - TRY 블록 안이면 CATCH로 넘어가고,
            // 심각도 20 이상이면 연결이 끊긴다. 코퍼스에 RAISERROR는 0건이나, 확인할 수
            // 있는 조건을 확인하지 않으면 거짓 행이 새는 것은 다른 갈래와 같다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    WHILE (@@FETCH_STATUS = 0) BEGIN
        RAISERROR('중단', 16, 1)
        SET @v = 0
    END
END";

            Assert.Empty(LoopVariableResetExtractor.Extract(ddl));
        }

        [SkippableFact]
        public void Extract_OverTheCorpus_ShouldCollectExactlyTheThreeKnownResets()
        {
            // 파일 주석이 "WHILE 본문 안 SET 11건 중 3건"이라는 실측 위에 서 있다.
            // 규칙이 흘러도 단위 테스트는 그대로 통과하므로 그 숫자를 코퍼스에 직접
            // 못박는다. 코퍼스가 없으면 건너뜀으로 표시된다(CorpusSkip.Reason).
            var root = CorpusRoot();
            Skip.If(root == null, CorpusSkip.Reason);

            var collected = new List<string>();
            var setsInsideLoops = 0;

            foreach (var dir in Directory.GetDirectories(root))
            {
                var path = Path.Combine(dir, "raw", "object_definition.sql");
                if (!File.Exists(path)) continue;

                var ddl = File.ReadAllText(path);
                var name = Path.GetFileName(dir);

                foreach (var fact in LoopVariableResetExtractor.Extract(ddl))
                {
                    collected.Add($"{name}:{fact.Line} SET {fact.Variable} = {fact.Value}");
                }

                setsInsideLoops += CountSetStatementsInsideWhileBodies(ddl);
            }

            Assert.Equal(
                new[]
                {
                    "dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure:113 SET @v_intPostChkAmt1 = 0",
                    "dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure:114 SET @v_intPostChkAmt2 = 0",
                    "dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure:69 SET @v_intID = 0"
                },
                collected.OrderBy(x => x, StringComparer.Ordinal).ToArray());

            Assert.Equal(11, setsInsideLoops);
        }

        /// <summary>
        /// 코퍼스 뿌리. 없으면 null - 그때 코퍼스 테스트는 조용히 통과한다(계획서 STEP ZERO).
        ///
        /// "output/Objects를 가진 첫 조상"으로 찾으면 안 된다 - 다른 테스트가 실행 중에
        /// bin/Debug/net10.0/output/Objects에 가짜 객체(dbo.FN_Child 등)를 만들어 두어,
        /// 그쪽이 먼저 걸리면 이 테스트가 남의 테스트 찌꺼기를 코퍼스로 착각한다.
        /// 그래서 src/ReSet.Core를 가진 조상(저장소 뿌리)을 먼저 찾고 거기서만 본다.
        /// </summary>
        private static string? CorpusRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "ReSet.Core")))
                {
                    var candidate = Path.Combine(dir.FullName, "output", "Objects");
                    return Directory.Exists(candidate) ? candidate : null;
                }

                dir = dir.Parent;
            }

            return null;
        }

        /// <summary>
        /// 파일 주석의 분모(WHILE 본문 안 SET 전체)를 세는 테스트 전용 계수기.
        /// 추출기의 거르기를 거치지 않은 날것이라야 "11건 중 3건"을 못박을 수 있다.
        /// </summary>
        private static int CountSetStatementsInsideWhileBodies(string ddl)
        {
            var parser = new TSql160Parser(true);
            using var reader = new StringReader(ddl);
            var fragment = parser.Parse(reader, out var errors);
            if (fragment == null || errors.Count > 0) return 0;

            var counter = new SetInsideWhileCounter();
            fragment.Accept(counter);
            return counter.Count;
        }

        private sealed class SetInsideWhileCounter : TSqlFragmentVisitor
        {
            private int _whileDepth;

            public int Count { get; private set; }

            public override void ExplicitVisit(WhileStatement node)
            {
                _whileDepth++;
                base.ExplicitVisit(node);
                _whileDepth--;
            }

            public override void ExplicitVisit(SetVariableStatement node)
            {
                if (_whileDepth > 0) Count++;
                base.ExplicitVisit(node);
            }
        }
    }
}
