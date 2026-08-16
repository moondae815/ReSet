using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class SpecRoundingShapeExtractorTests
    {
        private static List<(string FileName, string Content)> Spec(string content) =>
            new() { ("dbo.UP_UTIL_SETTLE_INS", content) };

        private static IReadOnlyCollection<string> Shapes(string content) =>
            SpecRoundingShapeExtractor.Extract(Spec(content))["UP_UTIL_SETTLE_INS"];

        [Fact]
        public void Extract_ShouldEraseColumnNamesButKeepTheRoundingFlag()
        {
            // 계획서는 같은 계산을 자기 이름으로 부른다 - 원본의 X.PGCOMM4SUM이
            // X.RawPgComm4Sum이 된다. 이름까지 대조하면 정상 이행이 전부 걸리고,
            // 반대로 반올림 플래그까지 지우면 CommSumRoundFlag를 CommRoundFlag로
            // 바꿔 써도 통과한다 - 그러면 금액이 달라진다.
            var shapes = Shapes("`ROUND(ROUND(X.PGCOMM4SUM,0,Y.CommSumRoundFlag)/1.1,0,Y.CommRoundFlag)`");

            Assert.Equal(new[] { "round(round(?,0,commsumroundflag)/1.1,0,commroundflag)" }, shapes);
        }

        [Fact]
        public void Extract_ShouldGiveTheSameShapeWhenOnlyColumnNamesDiffer()
        {
            var fromSpec = Shapes("`ROUND(ROUND(X.PGCOMM4SUM,0,Y.CommSumRoundFlag)/1.1,0,Y.CommRoundFlag)`");
            var fromPlan = Shapes("`ROUND(ROUND(S.RawPGComm4Sum,0,P.CommSumRoundFlag)/1.1,0,P.CommRoundFlag)`");

            Assert.Equal(fromSpec, fromPlan);
        }

        [Fact]
        public void Extract_ShouldGiveADifferentShapeWhenTheRoundingFlagDiffers()
        {
            var original = Shapes("`ROUND(ROUND(X.A,0,Y.CommSumRoundFlag)/1.1,0,Y.CommRoundFlag)`");
            var altered = Shapes("`ROUND(ROUND(X.A,0,Y.CommRoundFlag)/1.1,0,Y.CommRoundFlag)`");

            Assert.NotEqual(original, altered);
        }

        [Fact]
        public void Extract_ShouldIgnoreASingleRound()
        {
            // 단일 ROUND는 너무 흔해 신호가 되지 않는다. 중첩만 본다.
            var result = SpecRoundingShapeExtractor.Extract(Spec("`ROUND(X.PGCOMM,0,Y.CommRoundFlag)`"));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldKeepNestingDepthAndOrder()
        {
            // 3중첩을 2중첩으로 줄이면 반올림이 한 번 덜 일어나 금액이 달라진다.
            var deep = Shapes("`ROUND(ROUND(ROUND(X.A,0,Y.CommSumRoundFlag),0,Y.CommRoundFlag),0,Y.VatRoundFlag)`");
            var shallow = Shapes("`ROUND(ROUND(X.A,0,Y.CommRoundFlag),0,Y.VatRoundFlag)`");

            Assert.NotEqual(deep, shallow);
        }

        [Fact]
        public void Extract_ShouldSkipAnExpressionWhoseRoundingModeComesFromAFunction()
        {
            // 실측(POQSettleProc14·15 S08): 원본이 반올림 방식을 플래그 컬럼이 아니라
            // UDF 호출로 정한다 - ROUND(IIF(...), 0, dbo.UF_GET_PGCommOption(A.PGNAME,3)).
            // 이런 수식은 IIF·UDF가 겹쳐 표현 차이만으로도 모양이 어긋나고, 실제로
            // 정상 이행을 결함으로 보고했다. 아는 플래그가 하나도 없으면 대조하지 않는다 -
            // 이 검사가 보려는 것은 "반올림 방식과 순서가 보존됐는가"이고, 방식이
            // 함수로 정해지는 경우 그 판정을 모양으로 할 수 없다.
            var result = SpecRoundingShapeExtractor.Extract(Spec(
                "`ROUND(ROUND(IIF(C.TYPE=0, A.TxAmt, C.AMT), 0, dbo.UF_GET_PGCommOption(A.PGNAME,3)) * 1.1, 0, 0)`"));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldNotCreateAKeyWhenTheSpecHasNoNestedRounding()
        {
            // 빈 목록과 "그런 프로시저 없음"이 같아지면 대조 0건이 통과로 읽힌다.
            var result = SpecRoundingShapeExtractor.Extract(Spec("반올림 서술이 없는 산문입니다."));

            Assert.Empty(result);
        }

        [Fact]
        public void Extract_ShouldMergeShapesForTheSameProcedure()
        {
            var specs = new List<(string FileName, string Content)>
            {
                ("dbo.UP_A", "`ROUND(ROUND(X.A,0,Y.CommSumRoundFlag),0,Y.CommRoundFlag)`"),
                ("dbo.UP_A", "`ROUND(ROUND(X.B,0,Y.VatRoundFlag),0,Y.CommRoundFlag)`")
            };

            var result = SpecRoundingShapeExtractor.Extract(specs);

            Assert.Equal(2, result["UP_A"].Count);
        }
    }
}
