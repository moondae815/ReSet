using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests;

/// <summary>
/// [P · 괄호 정규화] 최상위 술어 대조의 정규화는 토큰을 이어 붙인다(`RenderNormalizedTokens`). 그래서 함수 인자를 감싼
/// 괄호 하나가 다른 항으로 읽혀 GPT 판 <b>여섯 판 전부</b>가 같은 자리에서 재생성됐다(판 안 발화 7 중 6).
/// 판독: docs/audit-reports/2026-09-16-술어항-괄호정규화-사전선언.md
/// </summary>
public sealed class PredicateTermParenthesisTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", "predicate-paren", name));

    /// <summary>주어진 SQL 의 모든 WHERE 절에서 최상위 AND 항의 대조 키를 뽑는다(원본·이행이 같은 함수를 쓴다).</summary>
    private static IReadOnlyList<string> Keys(string sql)
    {
        var parser = new TSql160Parser(true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);

        var collector = new WhereCollector();
        fragment.Accept(collector);
        return DmlScopeExtractor.PredicateTermsOf(collector.Wheres).Select(t => t.Normalized).ToList();
    }

    private sealed class WhereCollector : TSqlFragmentVisitor
    {
        public List<WhereClause?> Wheres { get; } = new();
        public override void Visit(WhereClause node) => Wheres.Add(node);
    }

    private static string FunctionTermKey(IEnumerable<string> keys) =>
        Assert.Single(keys, k => k.Contains("UF_GET_CLIENTSECTIONRATE"));

    // R1: 원본과 첫 초안의 그 항은 같은 키여야 한다 - 차이는 네 번째 인자를 감싼 괄호뿐이다.
    [Fact]
    public void TheSameTermWithAndWithoutParenthesesAroundAFunctionArgument_NormalizesEqually()
    {
        var original = FunctionTermKey(Keys(Fixture("UP_UTIL_SETTLE_EXCEPTION_PROC.sql")));
        var draft = FunctionTermKey(Keys(Fixture("Batch16-S07-first-draft-update3.sql")));

        Assert.Equal(original, draft);
    }

    // R2: 우선순위를 바꾸는 괄호는 지우지 않는다 - 지우면 다른 식이 같은 키가 된다.
    [Fact]
    public void ParenthesesThatChangePrecedence_StayDifferent()
    {
        var left = Keys("UPDATE T SET C = 1 WHERE (A + B) * C = 10;");
        var right = Keys("UPDATE T SET C = 1 WHERE A + (B * C) = 10;");

        Assert.NotEqual(Assert.Single(left), Assert.Single(right));
    }

    // R4: 두 방향 모두 침묵한다 - 정규화기를 원본·이행이 공유하므로 한 방향만 재면 모자라다.
    [Theory]
    [InlineData("UPDATE T SET C = 1 WHERE dbo.F(A, (B - C)) <> 0;", "UPDATE T SET C = 1 WHERE dbo.F(A, B - C) <> 0;")]
    [InlineData("UPDATE T SET C = 1 WHERE dbo.F(A, B - C) <> 0;", "UPDATE T SET C = 1 WHERE dbo.F(A, (B - C)) <> 0;")]
    public void EitherSideMayCarryTheArgumentParentheses(string one, string other) =>
        Assert.Equal(Assert.Single(Keys(one)), Assert.Single(Keys(other)));

    // R4: 중첩 괄호도 같은 키다(`((B - C))`).
    [Fact]
    public void NestedArgumentParentheses_NormalizeEqually() =>
        Assert.Equal(
            Assert.Single(Keys("UPDATE T SET C = 1 WHERE dbo.F(A, ((B - C))) <> 0;")),
            Assert.Single(Keys("UPDATE T SET C = 1 WHERE dbo.F(A, B - C) <> 0;")));

    // [알려진 한계 - 2026-09-16 최종 리뷰 Minor 3] 도달 범위는 ScriptDom 의 FunctionCall 뿐이다. CAST·COALESCE·IIF 는
    // 별 노드라 같은 괄호 차이가 여전히 다른 키다 - 코퍼스(원본 DDL 31 편 · 배송 단계 펜스 1181 개) 도달 0 이라 좁게 뒀다.
    // 이 시험은 그 경계를 못박는다. 넓히는 날 이 시험이 빨개지면 기대값을 Equal 로 바꾸고 이 주석을 지워라.
    [Theory]
    [InlineData("CAST((A - B) AS INT) = 1", "CAST(A - B AS INT) = 1")]
    [InlineData("COALESCE(A, (B - C)) = 1", "COALESCE(A, B - C) = 1")]
    [InlineData("IIF(A = 1, (B - C), 0) = 1", "IIF(A = 1, B - C, 0) = 1")]
    public void NonFunctionCallNodesAreNotCoveredYet(string withParentheses, string without) =>
        Assert.NotEqual(
            Assert.Single(Keys($"UPDATE T SET X = 1 WHERE {withParentheses};")),
            Assert.Single(Keys($"UPDATE T SET X = 1 WHERE {without};")));

    // ISNULL·사용자 함수는 FunctionCall 이라 덮인다 - 위 한계와 경계를 갈라 못박는다.
    [Theory]
    [InlineData("ISNULL(A, (B - C)) = 1", "ISNULL(A, B - C) = 1")]
    [InlineData("dbo.F(A, dbo.G((B - C))) = 1", "dbo.F(A, dbo.G(B - C)) = 1")]
    public void FunctionCallNodesAreCovered(string withParentheses, string without) =>
        Assert.Equal(
            Assert.Single(Keys($"UPDATE T SET X = 1 WHERE {withParentheses};")),
            Assert.Single(Keys($"UPDATE T SET X = 1 WHERE {without};")));

    // R3: 원본에 없는 항은 계속 다르다 - B11 S10 UPDATE 9 가 더한 실물 모양이다.
    [Fact]
    public void ATermTheOriginalDoesNotHave_StaysDifferent()
    {
        var original = Keys("UPDATE T SET C = 1 WHERE A.PGName = 'X' AND E.ReqYMD = @p;");
        var drifted = Keys("UPDATE T SET C = 1 WHERE A.PGName = 'X' AND E.ReqYMD = @p AND ABS(IIF(ISNULL(A.DiscountFlag, 'N') = 'Y', A.DiscountAmt, A.TxAmt)) = ABS(E.Amt);");

        Assert.Equal(2, original.Count);
        Assert.Equal(3, drifted.Count);
        Assert.Empty(drifted.Where(d => d.Contains("ABS")).Intersect(original));
    }
}
