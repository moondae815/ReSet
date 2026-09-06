using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    public class AggregateAssignmentExtractorTests
    {
        [Fact]
        public void Extract_MinAssignment_ShouldReportNullOnNoRows()
        {
            // UP_UTIL_SETTLE_INS_EXTRA 실측: 초기값 ''가 집계 대입에 덮여 NULL이 되고,
            // 이후 여덟 DML의 YMD >= @v 술어가 전부 UNKNOWN이 되어 0행이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v_strReqYMD VARCHAR(8) = ''
    SELECT @v_strReqYMD = MIN(ReqYMD) FROM dbo.TExtraSettleIn WHERE ResultCode = '00'
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("@v_strReqYMD", fact.Variable);
            Assert.Equal("MIN", fact.Aggregate);
            Assert.True(fact.HasInitializer);
            Assert.Contains("NULL", fact.Sentence);
            Assert.Contains("초기값", fact.Sentence);
        }

        [Fact]
        public void Extract_CompoundAssignment_ShouldNotBeCollected()
        {
            // `SELECT @v += MAX(x)`도 SelectSetVariable로 담기지만 대상 칸은
            // `SELECT @v = MAX(x)`로 렌더된다(ExecutionSemanticsFacts) - 원문에 없는 문장이
            // 「수정 금지」 표에 실린다. 형제 둘(LoopVariableResetExtractor ·
            // NonAggregateAssignmentExtractor)이 같은 자리에서 거르는 것과 같은 규칙이다.
            // 코퍼스 영향은 0건이다 - 24개 객체의 SelectSetVariable 26건 중 복합 대입이
            // 0건임을 NonAggregateAssignmentExtractorTests의 코퍼스 테스트가 못박고 있고,
            // 그 분모는 이 추출기에도 그대로 적용된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT = 0
    SELECT @v += MAX(ID) FROM dbo.TA
END";

            Assert.Empty(AggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_CountAssignment_ShouldReportZeroNotNull()
        {
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @n INT
    SELECT @n = COUNT(*) FROM dbo.T
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("COUNT", fact.Aggregate);
            Assert.Contains("0", fact.Sentence);
            Assert.DoesNotContain("NULL", fact.Sentence);
        }

        [Fact]
        public void Extract_NonAggregateAssignment_ShouldBeIgnored()
        {
            // 비집계 대입은 무결과면 변수가 그대로 남는다 - 반대 의미라 담으면 거짓이다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v INT
    SELECT @v = c FROM dbo.T
END";

            Assert.Empty(AggregateAssignmentExtractor.Extract(ddl));
        }

        [Fact]
        public void Extract_WithSyntaxErrors_ShouldReturnEmpty()
        {
            Assert.Empty(AggregateAssignmentExtractor.Extract("CREATE PROCEDURE ((("));
        }

        [Fact]
        public void Extract_MinWithGroupBy_ShouldReportPreviousValueRetainedNotNull()
        {
            // 리뷰 발견(수정 라운드 1): GROUP BY가 있으면 무결과 시 그룹이 0개이므로
            // 이 SELECT 자체가 0행을 돌려주고 대입이 일어나지 않는다 - NULL이 아니라
            // 변수가 대입 전 값을 그대로 유지한다. GROUP BY 없는 경우와 정반대다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @v VARCHAR(8) = ''
    SELECT @v = MIN(ReqYMD) FROM dbo.T GROUP BY Grp
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("MIN", fact.Aggregate);
            Assert.DoesNotContain("NULL", fact.Sentence);
            Assert.Contains("이전 값", fact.Sentence);
            Assert.Contains("일어나지 않습니다", fact.Sentence);
        }

        [Fact]
        public void Extract_CountWithGroupBy_ShouldReportPreviousValueRetainedNotZero()
        {
            // COUNT도 GROUP BY 앞에서는 예외가 아니다 - 그룹이 0개면 이 SELECT가
            // 0행이므로 대입 자체가 없다. 0이 들어간다는 주장은 거짓이 된다.
            const string ddl = @"
CREATE PROCEDURE dbo.P
AS
BEGIN
    DECLARE @n INT
    SELECT @n = COUNT(*) FROM dbo.T GROUP BY Grp
END";

            var facts = AggregateAssignmentExtractor.Extract(ddl);

            var fact = Assert.Single(facts);
            Assert.Equal("COUNT", fact.Aggregate);
            Assert.Contains("이전 값", fact.Sentence);
            Assert.Contains("일어나지 않습니다", fact.Sentence);
        }

        [SkippableFact]
        public void Extract_OverTheCorpus_ShouldCollectExactlyTheseRows()
        {
            // 이 클래스에는 코퍼스 대장이 없었다. 형제 NonAggregateAssignmentExtractorTests가
            // 대장을 가진 덕에 그쪽 회차의 증분은 눈에 보였지만, 집계 쪽은 규칙이 흘러도
            // 단위 테스트가 그대로 통과한다. 감쌈 벗기기 회차(2026-09-06)가 이 표의 행을
            // 늘리므로, 늘기 **전에** 여기 대장을 세워 증분이 diff로 읽히게 한다.
            //
            // 갈래를 행에 함께 싣는다 - 이 추출기의 요점은 무결과 귀결이 갈린다는 것이고
            // (GROUP BY / COUNT / NULL), 갈래가 바뀌는 것은 대상 칸이 바뀌는 것보다
            // 훨씬 무겁다(확정 문장이 거짓이 된다).
            var objects = CorpusObjects().ToList();
            Skip.If(objects.Count == 0, CorpusSkip.Reason);

            var collected = new List<string>();
            foreach (var (name, ddl) in objects)
            {
                foreach (var fact in AggregateAssignmentExtractor.Extract(ddl))
                {
                    var branch =
                        fact.Sentence.Contains("이전 값을 그대로 유지합니다") ? "유지"
                        : fact.Sentence.Contains("COUNT는 0을 넣습니다") ? "COUNT0"
                        : "NULL대입";
                    collected.Add($"{name}:{fact.Line} {fact.Variable} = {fact.Aggregate} [{branch}]");
                }
            }

            Assert.Equal(
                new[]
                {
                    "dbo.UF_GET_COLLECTYMD.Function:123 @po_strCollectYMD = MIN [NULL대입]",
                    "dbo.UF_GET_COLLECTYMD.Function:138 @po_strCollectYMD = MAX [NULL대입]",
                    "dbo.UF_GET_OUTYMD4REFUND.Function:22 @po_strOutYMD = MIN [NULL대입]",
                    "dbo.UIF_SettleYMD.Function:125 @po_strSettleYMD = MIN [NULL대입]",
                    "dbo.UIF_SettleYMD.Function:140 @po_strSettleYMD = MAX [NULL대입]",
                    "dbo.UP_UTIL_SETTLE_INS_EXTRA.Procedure:21 @v_strReqYMD = MIN [NULL대입]",
                    "dbo.UP_UTIL_SETTLE_PROC_ETC.Procedure:79 @v_intID = MAX [NULL대입]",
                    "dbo.UP_UTIL_SETTLE_SUMMARY_EXTRA.Procedure:25 @v_strReqYMD = MIN [NULL대입]"
                },
                collected.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        /// <summary>
        /// 저장소 뿌리. "output/Objects를 가진 첫 조상"으로 찾으면 안 된다 - 다른 테스트가
        /// 실행 중에 bin/Debug/net10.0/output/Objects에 가짜 객체를 만들어 두어, 그쪽이
        /// 먼저 걸리면 남의 테스트 찌꺼기를 코퍼스로 착각한다. 그래서 src/ReSet.Core를
        /// 가진 조상을 찾는다.
        /// </summary>
        private static string? RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "ReSet.Core")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return null;
        }

        /// <summary>
        /// 코퍼스 객체 전량 - 로컬 <c>output/Objects</c> **와** 외부 DB
        /// <c>output/External/[DB]/Objects</c> 둘 다.
        ///
        /// [왜 External을 함께 훑는가 - 2026-09-06] 이 대장은 오래도록 로컬 24개만 훑었다.
        /// 그런데 명세서가 만들어지는 객체는 그 24개가 아니라 **참조 폐포**이고, 폐포에는
        /// 외부 DB 함수 7개가 들어 있다(reset-consistency-audit SKILL.md 1-1절). 실제로
        /// 축 A 🔴 하나의 대상 <c>UF_GET_COMM4CLIENT4PARTIALCANCEL</c>이 그 7개 안에
        /// 있어서, 로컬만 훑는 자로는 그 결함이 이 대장에 **한 번도 나타나지 않았다.**
        /// 자가 관할을 좁게 잡으면 결함이 아니라 자가 침묵한다.
        ///
        /// 이름은 외부 DB만 <c>[DB]/</c>로 접두한다 - 로컬 이름 24개가 그대로 남아야
        /// 이 회차의 증분이 diff에서 바로 읽힌다.
        /// </summary>
        private static IEnumerable<(string Name, string Ddl)> CorpusObjects()
        {
            var root = RepoRoot();
            if (root == null) yield break;

            var roots = new List<(string Prefix, string Dir)>();

            var local = Path.Combine(root, "output", "Objects");
            if (Directory.Exists(local)) roots.Add((string.Empty, local));

            var external = Path.Combine(root, "output", "External");
            if (Directory.Exists(external))
            {
                foreach (var db in Directory.GetDirectories(external).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var objects = Path.Combine(db, "Objects");
                    if (Directory.Exists(objects)) roots.Add((Path.GetFileName(db) + "/", objects));
                }
            }

            foreach (var (prefix, dir) in roots)
            {
                foreach (var objectDir in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var path = Path.Combine(objectDir, "raw", "object_definition.sql");
                    if (!File.Exists(path)) continue;
                    yield return (prefix + Path.GetFileName(objectDir), File.ReadAllText(path));
                }
            }
        }
    }
}
