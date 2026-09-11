using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 앵커 DML 최상위 술어 대조의 정규화기(R1~R6).
    /// 설계: docs/superpowers/specs/2026-09-11-앵커-DML-최상위-술어-대조-design.md §2-2
    ///
    /// [픽스처 출처] R5·R6 의 SQL 은 원본 DDL 과 단계 파일에서 오려 왔다 - 기억으로 지은
    /// 픽스처는 내 오해를 검사가 확인해 준다. 줄인 것은 SELECT 목록뿐이다.
    /// </summary>
    public class PredicateTermNormalizerTests
    {
        private static WhereClause WhereOf(string sql)
        {
            var fragment = new TSql160Parser(true).Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
            var finder = new WhereFinder();
            fragment.Accept(finder);
            return Assert.Single(finder.Wheres);
        }

        /// <summary>문장 최상위 WHERE 만 모은다 - 하위질의 WHERE 는 건너뛴다.</summary>
        private sealed class WhereFinder : TSqlFragmentVisitor
        {
            public List<WhereClause> Wheres { get; } = new();
            public override void Visit(UpdateSpecification node) { if (node.WhereClause != null) Wheres.Add(node.WhereClause); }
            public override void Visit(DeleteSpecification node) { if (node.WhereClause != null) Wheres.Add(node.WhereClause); }
        }

        private static IReadOnlyList<PredicateTerm> Terms(string where) =>
            DmlScopeExtractor.PredicateTermsOf(new[] { WhereOf($"UPDATE T SET X = 1 WHERE {where}") });

        private static string[] Normalized(string where) =>
            Terms(where).Select(t => t.Normalized).OrderBy(n => n, System.StringComparer.Ordinal).ToArray();

        [Fact]
        public void R1_QualifiersDoNotMatter()
        {
            Assert.Equal(Normalized("YMD = @a"), Normalized("A.YMD = @a"));
        }

        [Fact]
        public void R2_VariableNamesDoNotMatter()
        {
            Assert.Equal(Normalized("YMD = @pi_strYMD"), Normalized("YMD = @p_batchYmd"));
        }

        [Fact]
        public void R3_LiteralsAreKept_AndInListsAreSets()
        {
            Assert.NotEqual(Normalized("USESTATE = 0"), Normalized("USESTATE = 1"));
            Assert.Equal(Normalized("PGNAME IN ('b', 'a')"), Normalized("PGNAME IN ('a','b','a')"));
        }

        [Fact]
        public void R4_TheColumnGoesLeft_AndTheInequalityFlips()
        {
            Assert.Equal(Normalized("OUTYMD >= @v"), Normalized("@v <= OUTYMD"));
            Assert.NotEqual(Normalized("OUTYMD >= @v"), Normalized("OUTYMD <= @v"));
        }

        [Fact]
        public void R5_SubqueriesAreNormalizedInside_AndLockHintsAndCommentsAreDropped()
        {
            // 원본: output/Objects/dbo.UP_UTIL_SETTLE_COMM_UPD.Procedure/raw/object_definition.sql:139-146
            var original = Normalized(
                "YMD = @pi_strYMD\n" +
                "    AND    USESTATE IN (1,2,3)  --상태(0:전체건, 1:취소건, 2:부분취소건, 3:환불건)\n" +
                "    AND    PLTID NOT IN         --강제취소일을 당일로 변경하였기 때문에 강제취소건은 마이너스처리하지 않도록 수정, tigerfive, 2009-05-22\n" +
                "          (SELECT ' '\n" +
                "           UNION ALL\n" +
                "           SELECT PLTID\n" +
                "           FROM   PaymentDB.dbo.TCCanceledMst WITH(NOLOCK)\n" +
                "           WHERE  CYMD = @pi_strYMD )");

            // 이행: output/Jobs/POQSettleBatch7/agent/steps/S06.md 의 SQL_UPD3
            var step = Normalized(
                "YMD = @p_batchYmd\n" +
                "   AND USESTATE IN (1,2,3)\n" +
                "   AND PLTID NOT IN (\n" +
                "        SELECT ' ' UNION ALL\n" +
                "        SELECT PLTID FROM PaymentDB.dbo.TCCanceledMst WHERE CYMD = @p_batchYmd\n" +
                "   )");

            Assert.Equal(original, step);
        }

        [Fact]
        public void Parentheses_AroundAWholeTermDoNotMatter()
        {
            Assert.Equal(Normalized("(A.X = 1 OR B.Y = 2)"), Normalized("A.X = 1 OR B.Y = 2"));
        }

        [Fact]
        public void Parentheses_ThatChangePrecedenceAreKept()
        {
            Assert.NotEqual(Normalized("(A = 1 OR B = 2) AND C = 3"), Normalized("A = 1 OR (B = 2 AND C = 3)"));
        }

        [Fact]
        public void SymmetricComparisonsBetweenColumnsAreDirectionless()
        {
            Assert.Equal(Normalized("A.YMD = A.AYMD"), Normalized("A.AYMD = A.YMD"));
        }

        [Fact]
        public void JoinEqualitiesAreFlagged_OnlyWhenTheQualifiersDiffer()
        {
            // N5 가 소유하는 모양이다(HaveDifferentQualifiers - 리뷰 라운드 2 가 오탐 여섯 자리로 산 규칙).
            Assert.True(Assert.Single(Terms("A.CLIENTID = B.CLIENTID")).IsJoinEquality);
            Assert.False(Assert.Single(Terms("A.YMD = A.AYMD")).IsJoinEquality);
            Assert.False(Assert.Single(Terms("TID = CID")).IsJoinEquality);
        }

        [Fact]
        public void VariablesAreListedBeforeR2Erases_Them()
        {
            var term = Assert.Single(Terms("(A.ContractCancelYMD = @p_ymd OR B.ContractCancelYMD = @p_from)"));
            Assert.Equal(new[] { "@p_from", "@p_ymd" }, term.Variables.OrderBy(v => v, System.StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void RawIsTheSourceTextWithoutCommentsOnOneLine()
        {
            var term = Terms("USESTATE IN (1,2,3)  --상태\n AND YMD = @p")[0];
            Assert.Equal("USESTATE IN (1,2,3)", term.Raw);
        }

        [Fact]
        public void R6_TermsFromSeveralWhereClausesAreUnioned()
        {
            var terms = DmlScopeExtractor.PredicateTermsOf(new WhereClause?[]
            {
                WhereOf("UPDATE T SET X = 1 WHERE A = 1 AND B = 2"),
                WhereOf("UPDATE T SET X = 1 WHERE B = 2 AND C = 3"),
                null,
            });

            Assert.Equal(3, terms.Count);
        }

        // ── R7 (설계 §8-1) ── 원본 리터럴 등식은 매개변수로 바꾼 모양의 키도 함께 낸다.
        [Fact]
        public void R7_ALiteralEqualityAlsoCarriesItsParameterisedForm()
        {
            var literal = Assert.Single(Terms("OutState = 2"));
            var flipped = Assert.Single(Terms("2 = OutState"));
            var parameter = Assert.Single(Terms("OutState = @p_intOutState"));

            Assert.Equal(parameter.Normalized, literal.LiteralAsParameter);
            Assert.Equal(parameter.Normalized, flipped.LiteralAsParameter);
            Assert.Null(parameter.LiteralAsParameter);
        }

        [Fact]
        public void R7_OnlyPlainEqualityWithALiteralQualifies()
        {
            Assert.Null(Assert.Single(Terms("OutState > 2")).LiteralAsParameter);
            Assert.Null(Assert.Single(Terms("ISNULL(OUTYMD,'') <> ''")).LiteralAsParameter);
            Assert.Null(Assert.Single(Terms("PGNAME IN ('a', 'b')")).LiteralAsParameter);
            Assert.Null(Assert.Single(Terms("(A.X = 1 OR B.Y = 2)")).LiteralAsParameter);
            Assert.Null(Assert.Single(Terms("A.YMD = A.AYMD")).LiteralAsParameter);
        }
    }
}
