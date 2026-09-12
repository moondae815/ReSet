using System.Collections.Generic;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// <see cref="AnchorKindOrdinalPairTests"/> 가 살아 있는 코퍼스에서 재던 <b>양성 대조군</b>을
    /// 커밋된 픽스처로 옮긴 것이다. <c>POQSettleBatch6</c> 를 코퍼스에서 뺄 때 그 Job 이
    /// 유일하게 들고 있던 「불일치가 실제로 나는 모양」이 함께 사라지므로, 이후 모든 Job 의
    /// 불일치 0 이 <b>계약 덕인지 탐지기가 죽어서인지</b> 못 가르게 된다.
    ///
    /// 설계서(2026-09-08-U앵커-계약-표기-설계.md) §8-7 「남은 것」이 그 조건을 적었다 —
    /// 「뺄 거면 그 8 건과 위 <c>U2</c>~<c>U5</c> 넷을 커밋된 픽스처로 먼저 옮겨라.」
    ///
    /// [자가 코퍼스 시험과 같다] 기대값을 재구현하지 않는다. 제품 리더 둘
    /// (<see cref="StepSqlStatementReader"/> · <see cref="SpecStatementFactsExtractor"/>)에
    /// 문자열을 먹여 <b>둘이 서로를 검산</b>하게 한다. 파일도 코퍼스도 타지 않으므로
    /// 코퍼스가 없어도 <b>건너뛰지 않는다</b> — 이것이 코퍼스 시험과 갈리는 유일한 점이고,
    /// 반경 밖 탐지기로서 값이 있는 자리다.
    ///
    /// [실물 출처] 아래 상수는 전부 <c>output/Jobs/POQSettleBatch6</c> 와 그 레거시 SP 의
    /// <c>docs/Spec.md</c> 에서 오렸다. 컬럼 목록처럼 판정과 무관한 덩치만 줄였고 앵커 주석·
    /// 문장 종류·대상 테이블·서수는 <b>한 글자도 안 바꿨다</b>. 규격 기억으로 지으면 내 오해를
    /// 시험이 확인해 줄 뿐이다.
    /// </summary>
    public class AnchorKindOrdinalPairFixtureTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // 명세서 쪽 — `### DML 범위` 표. 칸은 헤더 이름으로 찾으므로 여덟 칸을 다 둘
        // 필요가 없다. 판정에 쓰이는 `문장`·`대상`만 남긴다.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>`dbo.UP_Util_PG_Client_CMRate_Ins` — 종류마다 1 부터 센다.</summary>
        private const string SpecCmRate = @"# dbo.UP_Util_PG_Client_CMRate_Ins

### DML 범위 (기계 확정 — 수정 금지)

| 문장 | 라인 | 대상 |
| :--- | :--- | :--- |
| DELETE 1 | 38 | TPGSettleRate |
| INSERT 1 | 47 | TPGSettleRate |
| DELETE 2 | 67 | TClientSettleRate |
| INSERT 2 | 76 | TClientSettleRate |
| DELETE 3 | 126 | TPGSettleRate4Extra |
| INSERT 3 | 135 | TPGSettleRate4Extra |
| DELETE 4 | 150 | TClientSettleRate4Extra |
| INSERT 4 | 159 | TClientSettleRate4Extra |
| DELETE 5 | 205 | TClientSettleRate4MobileCo |
| INSERT 5 | 214 | TClientSettleRate4MobileCo |
";

        /// <summary>`dbo.UP_UTIL_STAT_PGCOLLECT_INS` — 문장이 둘뿐이다.</summary>
        private const string SpecPgCollect = @"# dbo.UP_UTIL_STAT_PGCOLLECT_INS

### DML 범위 (기계 확정 — 수정 금지)

| 문장 | 라인 | 대상 |
| :--- | :--- | :--- |
| DELETE 1 | 22 | TStatPGCollect |
| INSERT 1 | 31 | TStatPGCollect |
";

        /// <summary>`dbo.UP_UTIL_SETTLE_SUMMARY_ETC` — `SELECT 1` 이 표에 있다.</summary>
        private const string SpecSummaryEtc = @"# dbo.UP_UTIL_SETTLE_SUMMARY_ETC

### DML 범위 (기계 확정 — 수정 금지)

| 문장 | 라인 | 대상 |
| :--- | :--- | :--- |
| SELECT 1 | 40 | — |
| DELETE 1 | 59 | TSettleByOUT |
| INSERT 1 | 81 | TSettleByOUT |
";

        // ─────────────────────────────────────────────────────────────────────
        // 단계 쪽 — 버릇 셋. 앵커 주석은 Batch6 실물 그대로다.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 버릇 ① <b>종류를 넘어 이어지는 연번</b> (Batch6/S01).
        ///
        /// 같은 파일 안에서 번호 체계가 둘로 갈린다 — <c>-- SQL_DELETE_3</c> 라벨은 종류마다
        /// 1 부터 세는데 <c>/* U5 */</c> 앵커만 통짜로 이어진다. 앞의 넷(<c>U1</c>~<c>U4</c>)은
        /// 우연히 명세서에 있는 쌍으로 읽혀 <b>조용히 통과</b>하고, <c>U6</c> 부터 다섯이
        /// 어긋난다. 하나의 버릇이 넷을 숨기고 다섯을 드러낸다.
        /// </summary>
        private const string StepRunningOrdinals = @"### S01 요율 이력 적재

```sql
-- SQL_DELETE_1
/* U1: TPGSettleRate 청크 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @p_ymd;

-- SQL_INSERT_1
/* U2: TPGSettleRate 청크 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate (YMD, PGNAME)
SELECT @p_ymd, PGNAME
  FROM SETTLE_POQ_DB.dbo.TPGCMRate
 WHERE USESTATE = 0;
```

```sql
-- SQL_DELETE_2
/* U3: TClientSettleRate 청크 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate
 WHERE YMD = @p_ymd;

-- SQL_INSERT_2
/* U4: TClientSettleRate 청크 등록 (UNION ALL 2분기, 동일 컬럼 순서) */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate (YMD, CLIENTID)
SELECT @p_ymd, B.CLIENTID
  FROM SETTLE_POQ_DB.dbo.TClientContract AS A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B ON A.CLIENTID = B.CLIENTID
 WHERE A.USESTATE IN (0,4,5,6);
```

```sql
-- SQL_DELETE_3
/* U5: TPGSettleRate4Extra 청크 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate4Extra
 WHERE YMD = @p_ymd;

-- SQL_INSERT_3
/* U6: TPGSettleRate4Extra 청크 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate4Extra (YMD, PGName)
SELECT @p_ymd, PGName
  FROM SETTLE_POQ_DB.dbo.TPGCMRate
 WHERE USESTATE = 0;
```

```sql
-- SQL_DELETE_4
/* U7: TClientSettleRate4Extra 청크 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4Extra
 WHERE YMD = @p_ymd;

-- SQL_INSERT_4
/* U8: TClientSettleRate4Extra 청크 등록 (UNION ALL 2분기, 동일 컬럼 순서) */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4Extra (YMD, ClientID)
SELECT @p_ymd, B.ClientID
  FROM SETTLE_POQ_DB.dbo.TClientContract AS A
  JOIN SETTLE_POQ_DB.dbo.TClientCMRate AS B ON A.ClientID = B.ClientID
 WHERE B.USESTATE = 5;
```

```sql
-- SQL_DELETE_5
/* U9: TClientSettleRate4MobileCo 청크 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
 WHERE YMD = @p_ymd;

-- SQL_INSERT_5
/* U10: TClientSettleRate4MobileCo 청크 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo (YMD, ClientID)
SELECT @p_ymd, ClientID
  FROM SETTLE_POQ_DB.dbo.TClientCMRate
 WHERE MobileCoCommApply = 1;
```
";

        /// <summary>
        /// 버릇 ② <b>원본 라인 번호를 서수로 쓴다</b> (Batch6/S09).
        /// 명세서 `라인` 칸이 22·31 인 것과 값이 일치한다 — 관측이지 확인은 아니다.
        /// 주석 본문에는 <c>DELETE 1</c>·<c>INSERT 1</c> 이라는 <b>맞는 표기가 이미 적혀</b>
        /// 있는데 리더가 먼저 만나는 <c>U22</c> 를 집는다.
        /// </summary>
        private const string StepSourceLineOrdinals = @"### S09 PG 수집 통계 생성

```sql
-- SQL_DELETE_TSTATPGCollECT
/* U22: DELETE 1 기준일 기존 통계 삭제 (INYMD = @p_ymd) */
DELETE FROM SETTLE_POQ_DB.dbo.TStatPGCollect
 WHERE INYMD = @p_ymd;
```

```sql
-- SQL_INSERT_TSTATPGCollECT
/* U31: INSERT 1 통계 데이터 생성 및 삽입 (3개 원천 UNION ALL 후 GROUP BY) */
INSERT INTO SETTLE_POQ_DB.dbo.TStatPGCollect (INYMD, CLIENTID)
SELECT TBL1.INYMD, LOWER(TBL1.CLIENTID)
  FROM (SELECT INYMD, CLIENTID FROM SETTLE_POQ_DB.dbo.TSettleMst WHERE INYMD = @p_ymd) AS TBL1
 GROUP BY TBL1.INYMD, TBL1.CLIENTID;
```
";

        /// <summary>
        /// 버릇 ③ <b>단계 자체 순번을 서수로 쓴다</b> (Batch6/S13).
        /// <c>U1</c> 은 DELETE 에 붙어 (DELETE,1) 로 읽혀 <b>우연히 통과</b>하고,
        /// <c>U2</c> 는 INSERT 에 붙어 (INSERT,2) 가 되는데 명세서에 <c>INSERT 2</c> 가 없어
        /// 하나만 드러난다. 여기서도 맞는 표기가 괄호 안에 이미 적혀 있다.
        /// </summary>
        private const string StepStepLocalOrdinals = @"### S13 후회수 취소 재집계

```sql
-- SQL_DELETE_RANGE
/* U1: 후회수 취소 기존 집계 삭제 (DELETE 1) */
DELETE FROM SETTLE_POQ_DB.dbo.TSettleByOUT
 WHERE OUTYMD = @v_strOutYMD
   AND YMD = @v_strYMD;
```

```sql
-- SQL_INSERT_CHUNK_BY_KEY
/* U2: 후회수 취소 재집계 등록 (INSERT 1) */
INSERT INTO SETTLE_POQ_DB.dbo.TSettleByOUT (YMD, OUTYMD)
SELECT YMD, OUTYMD
  FROM SETTLE_POQ_DB.dbo.TSettleMst
 WHERE ProcYMD = @p_ymd
 GROUP BY YMD, OUTYMD;
```
";

        /// <summary>
        /// 음성 대조군 — 버릇 셋을 계약이 정한 <c>DELETE n</c>·<c>INSERT n</c> 표기로 고친 판.
        /// <c>POQSettleBatch7</c> 이 실제로 낸 모양이다(설계서 §8-7 기제 표).
        /// 이것이 빨개지면 탐지기가 아무거나 잡는 것이므로 위 셋의 빨강은 값이 없다.
        /// </summary>
        private const string StepContractCompliant = @"### S01 요율 이력 적재

```sql
/* DELETE 1: TPGSettleRate 청크 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TPGSettleRate
 WHERE YMD = @p_ymd;

/* INSERT 1: TPGSettleRate 청크 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TPGSettleRate (YMD, PGNAME)
SELECT @p_ymd, PGNAME
  FROM SETTLE_POQ_DB.dbo.TPGCMRate
 WHERE USESTATE = 0;
```

```sql
/* DELETE 5: TClientSettleRate4MobileCo 청크 삭제 */
DELETE FROM SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo
 WHERE YMD = @p_ymd;

/* INSERT 5: TClientSettleRate4MobileCo 청크 등록 */
INSERT INTO SETTLE_POQ_DB.dbo.TClientSettleRate4MobileCo (YMD, ClientID)
SELECT @p_ymd, ClientID
  FROM SETTLE_POQ_DB.dbo.TClientCMRate
 WHERE MobileCoCommApply = 1;
```
";

        // ─────────────────────────────────────────────────────────────────────
        // 자 — 코퍼스 시험의 판정부를 그대로 옮긴다.
        // ─────────────────────────────────────────────────────────────────────

        private static IReadOnlyList<StepSqlStatement> Statements(string stepMarkdown) =>
            StepSqlStatementReader.Read(stepMarkdown, out _);

        private static HashSet<(string Kind, int Ordinal)> Declared(string procedure, string specMarkdown)
        {
            var facts = SpecStatementFactsExtractor.Extract(new[] { (procedure, specMarkdown) });
            Assert.True(facts.TryGetValue(procedure, out var procedureFacts),
                $"명세서 픽스처에서 {procedure} 의 사실을 못 읽었다 - 표 헤딩이나 문장 칸 모양이 실물과 갈렸다.");
            Assert.NotEmpty(procedureFacts!.DmlRows);

            return procedureFacts.DmlRows
                .Select(row => (row.Kind.ToUpperInvariant(), row.Ordinal))
                .ToHashSet();
        }

        private static List<StepSqlStatement> Mismatches(string stepMarkdown, string procedure, string specMarkdown)
        {
            var declared = Declared(procedure, specMarkdown);
            return Statements(stepMarkdown)
                .Where(s => s.Anchor != null && !declared.Contains((s.Kind.ToUpperInvariant(), s.Anchor.Value)))
                .ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // 불일치로 드러나는 자리 — Batch6 이 코퍼스에서 내던 8 건이 여기 산다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void RunningOrdinalsAcrossKinds_AreCaughtFromSixOnward()
        {
            var mismatches = Mismatches(StepRunningOrdinals, "UP_Util_PG_Client_CMRate_Ins", SpecCmRate);

            Assert.Equal(
                new[] { ("INSERT", 6), ("DELETE", 7), ("INSERT", 8), ("DELETE", 9), ("INSERT", 10) },
                mismatches.Select(m => (m.Kind.ToUpperInvariant(), m.Anchor!.Value)));
        }

        [Fact]
        public void SourceLineOrdinals_AreCaught()
        {
            var mismatches = Mismatches(StepSourceLineOrdinals, "UP_UTIL_STAT_PGCOLLECT_INS", SpecPgCollect);

            Assert.Equal(
                new[] { ("DELETE", 22), ("INSERT", 31) },
                mismatches.Select(m => (m.Kind.ToUpperInvariant(), m.Anchor!.Value)));
        }

        [Fact]
        public void StepLocalOrdinals_AreCaughtOnTheInsertOnly()
        {
            var mismatches = Mismatches(StepStepLocalOrdinals, "UP_UTIL_SETTLE_SUMMARY_ETC", SpecSummaryEtc);

            Assert.Equal(
                new[] { ("INSERT", 2) },
                mismatches.Select(m => (m.Kind.ToUpperInvariant(), m.Anchor!.Value)));
        }

        /// <summary>세 픽스처의 불일치 합이 Batch6 이 코퍼스에서 내던 <b>8</b> 이다.</summary>
        [Fact]
        public void TheThreeHabitsTogether_ReproduceTheEightMismatchesBatch6Carried()
        {
            var total =
                Mismatches(StepRunningOrdinals, "UP_Util_PG_Client_CMRate_Ins", SpecCmRate).Count +
                Mismatches(StepSourceLineOrdinals, "UP_UTIL_STAT_PGCOLLECT_INS", SpecPgCollect).Count +
                Mismatches(StepStepLocalOrdinals, "UP_UTIL_SETTLE_SUMMARY_ETC", SpecSummaryEtc).Count;

            Assert.Equal(8, total);
        }

        // ─────────────────────────────────────────────────────────────────────
        // 음성 대조군 — 탐지기가 아무거나 빨갛게 만드는 것이 아니다.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ContractCompliantNotation_ProducesNoMismatch()
        {
            Assert.Empty(Mismatches(StepContractCompliant, "UP_Util_PG_Client_CMRate_Ins", SpecCmRate));
        }

        [Fact]
        public void ContractCompliantNotation_StillCarriesAnchors()
        {
            // R2 의 모양 — 표기를 좁혀 모델이 아예 앵커를 안 붙이면 불일치도 0 이 된다.
            // 「불일치 0」만 단언하면 그 둘을 못 가른다.
            Assert.Equal(4, Statements(StepContractCompliant).Count(s => s.Anchor != null));
        }

        // ─────────────────────────────────────────────────────────────────────
        // ★ 지금 아무 시험도 안 지키는 자리 — 있는 쌍을 엉뚱한 문장에 붙인 것.
        //
        // 설계서 §8-7 「탐지기의 한계」: 이 자는 (종류,서수) 쌍의 존재만 보므로
        // `U2`~`U5` 넷은 통과한다. `INSERT 1` 문장에 붙은 `U2` 는 (INSERT,2) 로 읽히고
        // 명세서에 `INSERT 2` 가 있으니 초록이다 - 그런데 그 `INSERT 2` 의 대상은
        // 다른 테이블이다. 쌍이 아니라 **대상까지** 맞대면 드러난다.
        // ─────────────────────────────────────────────────────────────────────

        private static List<(string Kind, int Ordinal, string StepTarget, string SpecTarget)> TargetDisagreements(
            string stepMarkdown, string procedure, string specMarkdown)
        {
            var facts = SpecStatementFactsExtractor.Extract(new[] { (procedure, specMarkdown) });
            var byPair = facts[procedure].DmlRows
                .ToDictionary(row => (row.Kind.ToUpperInvariant(), row.Ordinal), row => row.TargetTable);

            var disagreements = new List<(string, int, string, string)>();
            foreach (var statement in Statements(stepMarkdown))
            {
                if (statement.Anchor == null) continue;

                var pair = (statement.Kind.ToUpperInvariant(), statement.Anchor.Value);
                if (!byPair.TryGetValue(pair, out var specTarget)) continue;   // 쌍이 없는 것은 위 시험의 몫

                var stepTarget = statement.TargetTable;
                var dot = stepTarget.LastIndexOf('.');
                if (dot >= 0) stepTarget = stepTarget[(dot + 1)..];

                if (!string.Equals(stepTarget, specTarget, System.StringComparison.OrdinalIgnoreCase))
                {
                    disagreements.Add((pair.Item1, pair.Item2, stepTarget, specTarget));
                }
            }

            return disagreements;
        }

        [Fact]
        public void RunningOrdinals_AlsoMisattributeFourAnchorsThatThePairRulerLetsThrough()
        {
            var disagreements = TargetDisagreements(
                StepRunningOrdinals, "UP_Util_PG_Client_CMRate_Ins", SpecCmRate);

            // U2·U3·U4·U5 - 쌍은 명세서에 있는데 그 쌍의 대상이 다른 테이블이다.
            Assert.Equal(
                new[]
                {
                    ("INSERT", 2, "TPGSettleRate", "TClientSettleRate"),
                    ("DELETE", 3, "TClientSettleRate", "TPGSettleRate4Extra"),
                    ("INSERT", 4, "TClientSettleRate", "TClientSettleRate4Extra"),
                    ("DELETE", 5, "TPGSettleRate4Extra", "TClientSettleRate4MobileCo"),
                },
                disagreements);
        }

        [Fact]
        public void ContractCompliantNotation_HasNoTargetDisagreement()
        {
            Assert.Empty(TargetDisagreements(
                StepContractCompliant, "UP_Util_PG_Client_CMRate_Ins", SpecCmRate));
        }
    }
}
