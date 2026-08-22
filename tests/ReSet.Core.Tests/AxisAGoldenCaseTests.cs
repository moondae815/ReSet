using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 감사가 `정합`으로 판정한 세 SP를 회귀 안전판으로 고정한다.
    ///
    /// [규칙의 단서 — 2026-08-17] 설계 문서(§골든 케이스 "그 규칙이 적용되지 않는
    /// 경우")가 정정한 대로, "이 셋이 깨지면 검사가 틀린 것이다"는 조건 없이 성립하지
    /// 않는다. L1 검사가 요구하는 재료(DML 범위 표 등)가 그 Spec을 생성할 당시
    /// 프롬프트에 없었다면, 재생성 전 실패는 검사의 결함이 아니라 설계 0의 계약이
    /// 의도한 정상 동작이다 - 실측으로 `CheckDmlScopeTable`을 켜면 저장된 14개 프로시저
    /// 중 13개가 실패하고 그중 골든 둘(`INS_EXTRA4PLCARD`, `Util_Settle_Summary_AcqManual`)
    /// 이 포함된다. 이 규칙 하나를 이유로 <b>저장된 Spec.md에 L1을 걸지 않는다</b> -
    /// output/이 .gitignore 대상이라 CI에서 비결정적이 된다는 이유와, "재생성 전에는
    /// 실패가 정상"이라는 이유 둘 다 때문이다. 다음에 이 파일을 읽는 사람이 L1
    /// 어서션을 "복구"하지 않도록 여기 명시해 둔다.
    ///
    /// 대신 두 층을 고정한다.
    /// 1. 추출기가 실제 원본에서 폭발하거나 재료를 폭주시키지 않는다(하한).
    /// 2. 추출기가 뽑은 앵커가 과거에 L1을 불능으로 만들었던 모양(맨 숫자:숫자,
    ///    문자열 리터럴 값)을 다시는 내지 않는다(회귀 가드) - Task 5의 `17:37)`과
    ///    Task 11의 `dacomcard`/`tosscard`가 그 실물이다. 개수만 세는 것보다
    ///    이쪽이 실제 결함을 다시 잡을 가능성이 높다.
    /// </summary>
    public class AxisAGoldenCaseTests
    {
        private static readonly string[] GoldenProcedures =
        {
            "dbo.UP_UTIL_SETTLE_CANCEL_INS",
            "dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD",
            "dbo.UP_Util_Settle_Summary_AcqManual"
        };

        [Theory]
        [InlineData("dbo.UP_UTIL_SETTLE_CANCEL_INS")]
        [InlineData("dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD")]
        [InlineData("dbo.UP_Util_Settle_Summary_AcqManual")]
        public void Extractors_ShouldNotThrowOnGoldenProcedures(string procedureName)
        {
            var ddl = TryReadObjectDefinition(procedureName);
            if (ddl == null) return; // 산출물이 없는 환경(예: 갓 클론한 워크트리)에서는 건너뛴다.

            var comments = SourceCommentExtractor.Extract(ddl);
            var rounding = RoundingSemanticsExtractor.Extract(ddl);
            var options = SessionOptionsExtractor.Extract(ddl);
            var scopes = DmlScopeExtractor.Extract(ddl, "@pi_strYMD");
            var derived = DerivedTableColumnExtractor.Extract(ddl);

            // 배너가 잦으면 사람이 읽지 않는다 - 재료가 폭주하지 않는지 본다.
            // 상한 40은 SourceCommentExtractor.MaxBlocks(Task 5)와 같은 값이다 - 그
            // 캡이 실제로 지켜지는지 원본 데이터로 다시 확인하는 것이지, 값을
            // 새로 정하는 것이 아니다.
            Assert.InRange(comments.Count, 0, 40);
            Assert.All(rounding, c => Assert.False(string.IsNullOrWhiteSpace(c.ThirdArgument)));
            Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o)));
            Assert.All(scopes, s => Assert.True(s.Line > 0));
            Assert.All(derived, d => Assert.False(string.IsNullOrWhiteSpace(d.Column)));
        }

        [Fact]
        public void GoldenProcedureList_ShouldMatchTheAuditVerdict()
        {
            // 감사 보고서 §3-A에서 `정합`으로 판정된 셋. 이 목록을 줄이려면
            // 감사를 다시 돌려 근거를 바꿔야 한다.
            Assert.Equal(3, GoldenProcedures.Length);
        }

        /// <summary>
        /// 맨 "숫자:숫자" 모양의 앵커. `UP_UTIL_SETTLE_COMM_UPD.Procedure:95`의
        /// "2019.06-10 17:37" 안 "17:37"이 정확히 이 모양이었다 - 시각이지 코드
        /// 범례가 아닌데, 라벨이 글자로 시작해야 한다는 판별자가 없던 시절에는
        /// 이것이 유일한 앵커가 되어 L1이 재생성으로 고칠 수 없는 요구를 냈다
        /// (SourceCommentExtractor.CodeLegendRegex 주석, Task 5). 이 정규식은 그
        /// 판별자가 실제 원본 전체에서 여전히 지켜지는지를 본다.
        /// </summary>
        private static readonly Regex BareDigitColonDigit = new(@"^\d+:\d+$", RegexOptions.Compiled);

        /// <summary>
        /// DerivedTableColumnExtractor가 앵커를 뽑기 전에 지우는 것과 같은 모양의
        /// SQL 작은따옴표 문자열 리터럴. 원본(정화 전) 표현식에서 리터럴 값을 따로
        /// 뽑아, 그 값이 앵커 목록에 새지 않았는지 독립적으로 확인하는 데 쓴다.
        /// </summary>
        private static readonly Regex StringLiteral = new(@"'(?:[^']|'')*'", RegexOptions.Compiled);

        [Fact]
        public void SourceCommentAnchors_ShouldNeverBeBareDigitColonDigit()
        {
            var files = FindAllObjectDefinitions();
            if (files.Count == 0) return; // 산출물이 없는 환경에서는 건너뛴다.

            var offenders = new List<string>();
            foreach (var file in files)
            {
                var ddl = File.ReadAllText(file);
                foreach (var block in SourceCommentExtractor.Extract(ddl))
                {
                    foreach (var anchor in block.Anchors)
                    {
                        if (BareDigitColonDigit.IsMatch(anchor))
                        {
                            offenders.Add($"{ObjectNameOf(file)}: \"{anchor}\" ({block.Line}행)");
                        }
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "시각을 코드 범례로 오분류한 앵커가 나왔습니다(Task 5가 고친 `17:37)` 재발):\n"
                + string.Join("\n", offenders));
        }

        [Fact]
        public void DerivedTableAnchors_ShouldNeverBeStringLiteralValues()
        {
            var files = FindAllObjectDefinitions();
            if (files.Count == 0) return; // 산출물이 없는 환경에서는 건너뛴다.

            var offenders = new List<string>();
            foreach (var file in files)
            {
                var ddl = File.ReadAllText(file);
                foreach (var definition in DerivedTableColumnExtractor.Extract(ddl))
                {
                    var literalValues = StringLiteral.Matches(definition.Expression)
                        .Select(m => m.Value.Trim('\''))
                        .Where(v => v.Length > 0)
                        .ToList();

                    foreach (var anchor in definition.Anchors)
                    {
                        if (literalValues.Any(v => string.Equals(v, anchor, StringComparison.OrdinalIgnoreCase)))
                        {
                            offenders.Add(
                                $"{ObjectNameOf(file)}: 별칭 {definition.Alias}.{definition.Column}의 "
                                + $"앵커 \"{anchor}\"가 문자열 리터럴 값입니다");
                        }
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "문자열 리터럴 값이 식별자 앵커로 샜습니다(Task 11이 고친 `dacomcard`/`tosscard` 재발):\n"
                + string.Join("\n", offenders));
        }

        [Fact]
        public void ExtractSetPredicates_OnExpectProc_ShouldCarryTheNinePgLiterals()
        {
            // 2026-08-18 축 A 감사의 🟠. object_definition.sql:39의 9개 리터럴이
            // 명세서 어디에도 하나의 집합으로 제시되지 않아, 이관하면 4개 PG가
            // 자동회수 대상에 잘못 편입된다. 픽스처가 아니라 실물 DDL로 잡는 이유는
            // 최종 리뷰의 Critical이 "12개 태스크 리뷰가 전부 픽스처만 썼고 실물
            // 코퍼스를 안 봐서 감사의 그 문서가 통과했다"였기 때문이다.
            //
            // Column="A.PGName"은 좌변 원문 표기(한정자 포함) 계약이라 "PGName"이
            // 아니라 "A.PGName"이다.
            //
            // [Line이 27에서 39로 바뀐 이유 - 2026-08-22 축 A 재감사 ③ Task 5, 설계 §4 C]
            // 라인 칸이 문장 시작줄이 아니라 그 술어 항 자신의 줄이 됐다
            // (SetPredicateFact.Line 문서 참고). 실물 DDL을 직접 세어 확인했다:
            // object_definition.sql의 27행이 이 UPDATE의 시작이고,
            // `AND    A.PGName NOT IN ('PLCard',...)`은 39행이다.
            var ddl = TryReadObjectDefinition("dbo.UP_UTIL_SETTLE_EXPECT_PROC");
            if (ddl == null) return;

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);
            var pgName = Assert.Single(
                facts, f => f.Line == 39 && f.Column.Equals("A.PGName", StringComparison.OrdinalIgnoreCase));

            Assert.True(pgName.IsNegated);
            Assert.Equal(9, pgName.Literals.Count);
            Assert.Contains("'SSGPayCard'", pgName.Literals);
            Assert.Contains("'KakaoCard'", pgName.Literals);
        }

        [Fact]
        public void ExtractSetPredicates_OnCommUpd_ShouldCarryTheSixPgWhitelist()
        {
            // 같은 감사의 두 번째 🟠. object_definition.sql:77의 6개 화이트리스트가
            // 명세서에 없어, 해외카드 수수료율이 국내건·타 PG건까지 적용될 수 있다.
            //
            // Column="A.PGNAME"은 좌변 원문 표기 계약이고, 나머지 14개 집합 술어 중
            // 원소 6개에 'DACOMCARD'를 담은 것은 이 행 하나뿐이라 Assert.Single이 안전하다.
            //
            // [Line이 58에서 77로 바뀐 이유 - 2026-08-22 축 A 재감사 ③ Task 5, 설계 §4 C]
            // 라인 칸이 그 술어 항 자신의 줄이 됐다. 실물 DDL을 직접 세어 확인했다:
            // 58행이 이 UPDATE의 시작이고 화이트리스트 IN은 77행이다(위 주석이 이미
            // "object_definition.sql:77의 6개 화이트리스트"라고 적은 그 줄이다).
            var ddl = TryReadObjectDefinition("dbo.UP_UTIL_SETTLE_COMM_UPD");
            if (ddl == null) return;

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);
            var whitelist = Assert.Single(
                facts, f => f.Line == 77 && f.Column.Equals("A.PGNAME", StringComparison.OrdinalIgnoreCase)
                    && f.Literals.Count == 6 && f.Literals.Contains("'DACOMCARD'"));

            Assert.False(whitelist.IsNegated);
            Assert.Contains("'INICARD'", whitelist.Literals);
            Assert.Contains("'TOSSCARD'", whitelist.Literals);

            // 원문 칸도 실물로 못 박는다 - 77행 원문은 `A.PGNAME     IN (...)`처럼
            // 한정자와 IN 사이에 공백이 다섯이라, CollapseWhitespace를 거치지 않으면
            // 렌더와 검증기가 대조할 수 없는 값이 된다(SetPredicateFact.PredicateText
            // 문서 참고). 픽스처가 아니라 코퍼스가 이 접기를 실제로 요구한다.
            Assert.Equal(
                "A.PGNAME IN ('ALLTHEGATE','DACOMCARD','UNIONPAY','INICARD','TOSSCARD','NICECARD')",
                whitelist.PredicateText);
        }

        // 2026-08-18 최종 브랜치 리뷰 실측(Minor 2) - 위 InRange(0, 40)와 두 All은
        // 셋 중 둘(CANCEL_INS, AcqManual)에서 공허하게 통과했다: 그 둘의 실제 집합
        // 술어 개수가 0이므로 Assert.All(빈 컬렉션)은 아무것도 검사하지 않고
        // 무조건 통과한다. "폭발하지 않는다"는 의도는 유지하되, 각 프로시저의
        // 정확한 기대 개수를 직접 실측해 단언한다 - 추정하지 않았다.
        //
        // [2026-08-19 재실측] 축 A 감사 후 수집 범위를 넓혔다(리터럴 우변 등호·부등호,
        // ISNULL 래핑 좌변, 파생 테이블 내부 술어). 기대 개수를 임시 프로브로 다시 재
        // 갱신한다: CANCEL_INS 0→2, INS_EXTRA4PLCARD 1→13, AcqManual 0→0(그대로).
        // 코퍼스 전체로는 79 → 198행이고, "폭발하지 않는다"는 이 테스트의 의도는
        // 아래 InRange 상한(40)이 계속 지킨다 - 최대치인 EXCEPTION_PROC이 정확히 40이다.
        [Theory]
        [InlineData("dbo.UP_UTIL_SETTLE_CANCEL_INS", 2)]
        [InlineData("dbo.UP_UTIL_SETTLE_INS_EXTRA4PLCARD", 13)]
        [InlineData("dbo.UP_Util_Settle_Summary_AcqManual", 0)]
        public void ExtractSetPredicates_ShouldNotExplodeOnGoldenProcedures(string procedureName, int expectedCount)
        {
            var ddl = TryReadObjectDefinition(procedureName);
            if (ddl == null) return;

            var facts = DmlScopeExtractor.ExtractSetPredicates(ddl);

            Assert.Equal(expectedCount, facts.Count);
            // 빈 집합은 표에 쓸 것이 없다 - 추출기가 그런 사실을 내면 안 된다.
            Assert.All(facts, f => Assert.NotEmpty(f.Literals));
            Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Column)));
        }

        private static string? TryReadObjectDefinition(string procedureName)
        {
            var root = TryFindRepoRoot();
            if (root == null) return null;

            var path = Path.Combine(
                root, "output", "Objects", $"{procedureName}.Procedure", "raw", "object_definition.sql");

            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>
        /// output/ 전체(Objects·External 포함)에서 object_definition.sql을 전부
        /// 찾는다. 골든 셋 셋만이 아니라 감사 대상 코퍼스 전체를 훑어야, 코퍼스
        /// 어딘가(예: 골든 셋 밖의 COMM_UPD)에서 재발한 결함도 잡는다.
        /// </summary>
        private static IReadOnlyList<string> FindAllObjectDefinitions()
        {
            var root = TryFindRepoRoot();
            if (root == null) return Array.Empty<string>();

            var outputDir = Path.Combine(root, "output");
            if (!Directory.Exists(outputDir)) return Array.Empty<string>();

            return Directory.EnumerateFiles(outputDir, "object_definition.sql", SearchOption.AllDirectories)
                .ToList();
        }

        private static string ObjectNameOf(string objectDefinitionPath) =>
            // .../output/.../<객체이름>.<Procedure|Function>/raw/object_definition.sql
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(objectDefinitionPath))) ?? objectDefinitionPath;

        /// <summary>
        /// RepoPaths.FindRepoRoot()는 ReSet.slnx가 없으면 예외를 던진다 - 테스트
        /// 어셈블리가 도는 환경이라면 그 파일은 항상 있으므로(output/의 존재 여부와
        /// 무관하게 저장소 자체는 있다) 그 경로를 그대로 신뢰해도 된다. 이 메서드는
        /// output/이 없는 환경에서 이 클래스 전체가 조용히 건너뛸 수 있도록 null
        /// 허용 형태로만 감싼다.
        /// </summary>
        private static string? TryFindRepoRoot()
        {
            try
            {
                return RepoPaths.FindRepoRoot();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
