using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// 실측 회차를 축약한 픽스처로, 결함이 되살아나면 잡는다.
    ///
    /// 실측 산출물(output/Jobs/...)은 git 추적 대상이 아니라 픽스처로 쓸 수 없다.
    /// 결함을 재현하는 최소 형태만 저장소에 남긴다.
    /// </summary>
    public class StepErrorCodeRegressionTests
    {
        // 조건 컬럼 대조를 쓰지 않는 테스트용 빈 재료. 비어 있으면 검사가
        // 소프트 스킵하므로 이 테스트들이 보는 동작은 달라지지 않는다.
        private static readonly System.Collections.Generic.IReadOnlyDictionary<string, SpecConditions> NoConditions =
            new System.Collections.Generic.Dictionary<string, SpecConditions>();

        // CancellationPolicyTests가 baseline 파일을 읽는 방식과 같다 - 저장소
        // 루트에서 소스 트리를 직접 읽는다. 이 저장소에는 출력 디렉터리 복사
        // 설정이 없으므로 픽스처 하나 때문에 도입하지 않는다.
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", name));

        [Fact]
        public void MeasuredPlanStructure_GainsErrorCodesFromTheSpec()
        {
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var before = BatchStepPlanParser.TryParse(Fixture("PlanStructureWithEmptyErrorCodes.md"))!;
            var after = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(
                    Fixture("PlanStructureWithEmptyErrorCodes.md"),
                    codes,
                    new Dictionary<string, SpecTargetTableExtractor.StepTableSets>()).Markdown)!;

            Assert.Empty(before.Single(s => s.Code == "S06").ErrorCodes);
            Assert.Equal(16, after.Single(s => s.Code == "S06").ErrorCodes.Count);
        }

        /// <summary>
        /// 옛 명제("레거시 출신이 없으면 ErrorCodes가 비고, 대조 항목이 없어서
        /// 하한 검사를 통과한다")는 더 이상 참이 아니다 - PlanStructureEnricher가
        /// 이제 그런 단계에 예약 대역 코드를 발급한다(S00 → -9000, Task 2). 그러나
        /// 이 테스트가 원래 지키던 성질 - "레거시 출신이 없는 단계가 코드가 없다는
        /// 이유로 결함 판정을 받지 않는다" - 은 여전히 지켜져야 한다. 다만 방식이
        /// 바뀐다: 대조 항목이 0개라서 통과하는 것이 아니라, 발급된 코드가 본문에
        /// 실제로 등장해서 통과한다.
        /// </summary>
        [Fact]
        public void StepsWithoutLegacyOriginReceiveTheReservedCodeAndPassTheFloorCheckWhenTheBodyCarriesIt()
        {
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var steps = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(
                    Fixture("PlanStructureWithEmptyErrorCodes.md"),
                    codes,
                    new Dictionary<string, SpecTargetTableExtractor.StepTableSets>()).Markdown)!;

            var s00 = steps.Single(s => s.Code == "S00");

            // S00의 블록 시작은 -9000 - 0*10 = -9000이다.
            Assert.Equal(new[] { "-9000" }, s00.ErrorCodes);

            var body = $"### {s00.Code} {s00.Name}\n\n```sql\nSELECT 1 FROM {s00.TargetTables[0]};\n```\n\n-9000";
            var result = new MechanicalValidator().ValidateBatchStep(body, s00, Array.Empty<string>(), NoConditions);

            Assert.True(result.IsValid);
        }

        /// <summary>
        /// 새 설계의 실제 회귀 방어선이다. 예약 코드가 발급됐는데 본문이 그 코드를
        /// 담지 않으면 하한 검사가 걸어야 한다 - 그렇지 않으면 예약 대역 발급은
        /// 검증되지 않는 장식일 뿐이고, 옛 결함(모델이 지어낸 코드가 아무 대조 없이
        /// 통과하던 것)과 본질이 같은 구멍이 다시 열린다.
        /// </summary>
        [Fact]
        public void StepsWithoutLegacyOriginFailTheFloorCheckWhenTheBodyOmitsTheReservedCode()
        {
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var steps = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(
                    Fixture("PlanStructureWithEmptyErrorCodes.md"),
                    codes,
                    new Dictionary<string, SpecTargetTableExtractor.StepTableSets>()).Markdown)!;

            var s00 = steps.Single(s => s.Code == "S00");

            // 발급된 코드(-9000)를 본문에 싣지 않는다 - 옛 테스트의 본문과 같은 모양이다.
            var body = $"### {s00.Code} {s00.Name}\n\n```sql\nSELECT 1 FROM {s00.TargetTables[0]};\n```";
            var result = new MechanicalValidator().ValidateBatchStep(body, s00, Array.Empty<string>(), NoConditions);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("-9000"));
        }

        [Fact]
        public void EnrichedStepPassesTheFloorCheckWhenTheBodyCarriesEveryCode()
        {
            // 실측에서 24개 단계 전부가 이미 코드를 본문에 담고 있었다. 보강이
            // 검사를 진짜로 만들되 재시도 폭주를 부르지는 않는다는 뜻이다.
            var codes = SpecReturnCodeExtractor.Extract(new[]
            {
                ("dbo.UP_UTIL_SETTLE_COMM_UPD", Fixture("SettleCommUpdSpecExcerpt.md")),
            });

            var s06 = BatchStepPlanParser.TryParse(
                PlanStructureEnricher.Enrich(
                    Fixture("PlanStructureWithEmptyErrorCodes.md"),
                    codes,
                    new Dictionary<string, SpecTargetTableExtractor.StepTableSets>()).Markdown)!
                .Single(s => s.Code == "S06");

            var body = $"### {s06.Code} {s06.Name}\n\n```sql\nSELECT 1 FROM {s06.TargetTables[0]};\n```\n\n"
                + string.Join(" ", s06.ErrorCodes.Select(c => $"`{c}`"));

            var result = new MechanicalValidator().ValidateBatchStep(body, s06, Array.Empty<string>(), NoConditions);

            Assert.True(result.IsValid);
        }

        // ── 규칙 9의 본문: 레거시 출신 단계가 원본에 없는 코드를 발명하는가 ──
        //
        // 조사(2026-08-29-rule-enforcement-census.md §2)가 이 규칙을 ◐로 표시하고
        // "레거시 출신 단계가 원본 코드를 그대로 쓰는가는 아무도 안 본다"라고 적었다.
        // 3차 통제군 판독(2026-08-29-critic-exception-axis.md)이 그 대가를 쟀다 -
        // 축 4(예외처리)가 그 판의 유일한 불합격 사유였고, Critic 발화의 절반이 이
        // 부류였으며, 채택본에 `S04`의 `-2` 발명이 무경고로 살아남았다.

        // 이 검사가 아닌 다른 하한 검사에 걸리지 않을 최소 단계다. 목차 ErrorCodes를
        // 비우면 "대조를 실행할 수 없습니다"가 먼저 걸리고, 본문에 대상 테이블이
        // 없으면 그쪽이 먼저 걸린다 - 둘 다 이 검사와 무관한 잡음이므로 채워 둔다.
        private static BatchStepPlan LegacyStep(string code, params string[] legacy) =>
            new(code, $"{code} 이름", legacy, new[] { "dbo.T1" },
                new[] { "-1" }, false, Array.Empty<string>());

        /// <summary>
        /// 머리글 + 대상 테이블 + 선언 코드(산문)를 갖춘 몸통에 <paramref name="fenced"/>를
        /// 얹는다. 앞의 셋은 다른 검사를 잠재우기 위한 것이고, 판정 대상은 뒤의 것뿐이다.
        /// </summary>
        private static string Body(string code, string fenced) =>
            $"### {code} 단계\n\n```sql\nSELECT 1 FROM dbo.T1;\n```\n\n"
            + "원본이 정의한 코드는 `-1`이다.\n\n"
            + fenced;

        // 사전을 손으로 짓지 않고 실제 추출기로 짓는다. 키 규약(맨이름 접기)과
        // 코드가 실리는 모양이 프로덕션과 테스트에서 갈라지면, 한쪽만 바뀌어도
        // 방어가 조용히 꺼진다 - 이 저장소가 헤더 셀 상수에서 이미 겪은 축이다.
        private static IReadOnlyDictionary<string, IReadOnlyList<string>> Codes(
            string procedure, params string[] codes) =>
            SpecReturnCodeExtractor.Extract(new[]
            {
                (procedure,
                 "## 반환 코드\n\n" + string.Join("\n", codes.Select(c => $"- `@po_intRetVal = {c}`를 설정합니다.")))
            });

        private static StepValidationResult Validate(
            string body, BatchStepPlan step,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? codesByProcedure) =>
            new MechanicalValidator().ValidateBatchStep(
                body, step, Array.Empty<string>(), NoConditions,
                codesByProcedure: codesByProcedure);

        /// <summary>
        /// 3차 통제군 채택본의 실물이다. 원본 `UP_UTIL_SETTLE_CANCEL_INS`에는 `-1`만
        /// 있는데 `S04`가 가드 실패용으로 `-2`를 만들었다. Critic은 6차 시도에서만
        /// 지적했고 채택된 5차에서는 놓쳤다 - 그래서 무경고로 배송됐다.
        /// </summary>
        [Fact]
        public void LegacyStepAssigningACodeTheSpecNeverDefinesIsReported()
        {
            var body = Body("S04", "```sql\nSET @po_intRetVal = -2;\n```\n");

            var result = Validate(
                body,
                LegacyStep("S04", "dbo.UP_UTIL_SETTLE_CANCEL_INS"),
                Codes("dbo.UP_UTIL_SETTLE_CANCEL_INS", "-1"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("-2") && e.Contains("S04"));
        }

        /// <summary>
        /// 앱 코드 의사코드에도 같은 판정이 서야 한다. 새 규칙 판(3단계 이후)은
        /// `SET @po_intRetVal`을 쓰지 않고 `currentStepErrorCode = -2`로 쓴다 -
        /// 명세와 같은 변수 하나만 보면 이 판의 산출물에서는 통째로 침묵한다.
        /// </summary>
        [Fact]
        public void InventedCodeIsReportedInApplicationPseudocodeSpelling()
        {
            var body = Body("S04",
                "```pseudocode\ncurrentStepErrorCode = -2\n"
                + "execute(SQL_S04_GUARD_DELETE, { p_batchYmd: batchYmd })\n```\n");

            var result = Validate(
                body,
                LegacyStep("S04", "dbo.UP_UTIL_SETTLE_CANCEL_INS"),
                Codes("dbo.UP_UTIL_SETTLE_CANCEL_INS", "-1"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("-2"));
        }

        [Fact]
        public void LegacyStepUsingOnlyTheSpecCodesIsSilent()
        {
            var body = Body("S04",
                "```pseudocode\ncurrentStepErrorCode = -1\n"
                + "journal.updateSucceeded(runId, \"S04\", LegacyReturnCode: 0)\n```\n");

            var result = Validate(
                body,
                LegacyStep("S04", "dbo.UP_UTIL_SETTLE_CANCEL_INS"),
                Codes("dbo.UP_UTIL_SETTLE_CANCEL_INS", "-1"));

            Assert.True(result.IsValid);
        }

        /// <summary>
        /// 회귀 위험이 가장 큰 자리다. `dbo.UP_Util_Settle_Summary_AcqManual`은 오류를
        /// `ERROR_NUMBER()`로만 내므로 명세에서 뽑히는 리터럴이 **0개**다(실측 14편 중
        /// 둘이 그렇다). 빈 허용 집합을 "아무 코드도 허용하지 않는다"로 읽으면 그
        /// 단계의 모든 대입이 발명으로 고발된다 - 거짓 고발 공장이 된다.
        /// 재료를 얻지 못한 것과 결함이 없는 것은 다르다: 귀속할 수 없으면 침묵한다.
        /// </summary>
        [Fact]
        public void StepWhoseSpecYieldsNoLiteralCodeIsSilent()
        {
            var body = Body("S12", "```pseudocode\ncurrentStepErrorCode = -3\n```\n");

            var result = Validate(
                body,
                LegacyStep("S12", "dbo.UP_Util_Settle_Summary_AcqManual"),
                Codes("dbo.UP_Util_Settle_Summary_AcqManual"));

            Assert.True(result.IsValid);
        }

        /// <summary>
        /// `LegacyProcedures` 항목의 43%가 스키마 접두사 없이 적힌다(`:446`의 실측).
        /// 원문 그대로 조회하면 그 항목은 영원히 재료를 못 찾아 조용히 통과한다 -
        /// `CheckMissingConditionColumns`와 같은 `BareObjectName` 규약을 따라야 한다.
        /// </summary>
        [Fact]
        public void ProcedureLookupFoldsTheSchemaPrefix()
        {
            var body = Body("S04", "```pseudocode\ncurrentStepErrorCode = -2\n```\n");

            var result = Validate(
                body,
                LegacyStep("S04", "UP_UTIL_SETTLE_CANCEL_INS"),
                Codes("dbo.UP_UTIL_SETTLE_CANCEL_INS", "-1"));

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("-2"));
        }

        /// <summary>
        /// 성공 코드 `0`은 명세가 명시적으로 대입하지 않는 SP가 흔하다("명시적 실패
        /// 신호 없음 = 성공"). 허용 집합에 `0`을 항상 넣지 않으면 그 관행을 따른
        /// 문서가 전부 발화한다.
        /// </summary>
        [Fact]
        public void SuccessCodeZeroIsAlwaysAllowed()
        {
            var body = Body("S04", "```pseudocode\ncurrentStepErrorCode = 0\n```\n");

            var result = Validate(
                body,
                LegacyStep("S04", "dbo.UP_UTIL_SETTLE_CANCEL_INS"),
                Codes("dbo.UP_UTIL_SETTLE_CANCEL_INS", "-1"));

            Assert.True(result.IsValid, string.Join(" | ", result.Errors));
        }

        /// <summary>
        /// 여집합을 유지한다. 레거시 출신이 없는 제어 단계는
        /// `CheckControlStepErrorCodeBand`의 몫이고, 이 검사는 손대지 않는다 -
        /// 두 검사가 같은 대입을 두 번 고발하면 시정 지시가 서로 모순된다.
        /// </summary>
        [Fact]
        public void ControlStepWithNoLegacyOriginIsLeftToTheBandCheck()
        {
            var body = Body("S01", "```pseudocode\ncurrentStepErrorCode = -2\n```\n");

            var result = Validate(
                body,
                LegacyStep("S01"),
                Codes("dbo.UP_UTIL_SETTLE_CANCEL_INS", "-1"));

            Assert.DoesNotContain(result.Errors, e => e.Contains("발명"));
        }

        /// <summary>
        /// 재료가 아예 없으면(호출부가 아직 넘기지 않으면) 종전 동작 그대로다.
        /// 재료 없음을 결함 없음으로도, 결함 있음으로도 바꾸지 않는다.
        /// </summary>
        [Fact]
        public void MissingMaterialLeavesTheStepUntouched()
        {
            var body = Body("S04", "```pseudocode\ncurrentStepErrorCode = -2\n```\n");

            var result = Validate(body, LegacyStep("S04", "dbo.UP_UTIL_SETTLE_CANCEL_INS"), null);

            Assert.True(result.IsValid);
        }

        /// <summary>
        /// 코드가 변수로 넘어가는 자리는 세지 않는다. 그 변수의 대입 자리를 이미
        /// 따로 보므로, 여기서 또 세면 같은 결함이 두 번 발화한다.
        /// </summary>
        [Fact]
        public void PassingTheCodeThroughAVariableIsNotCountedTwice()
        {
            var body = Body("S04",
                "```pseudocode\ncurrentStepErrorCode = -2\n"
                + "journal.updateFailed(runId, \"S04\", LegacyReturnCode: currentStepErrorCode)\n```\n");

            var result = Validate(
                body,
                LegacyStep("S04", "dbo.UP_UTIL_SETTLE_CANCEL_INS"),
                Codes("dbo.UP_UTIL_SETTLE_CANCEL_INS", "-1"));

            Assert.Single(result.Errors, e => e.Contains("-2"));
        }

        /// <summary>
        /// 산문은 재료가 아니다. 세는 법은 L1의 다른 검사와 같아야 한다 -
        /// 코드 펜스 안만, mermaid는 제외. NOLOCK 축에서 문서 전수 grep이 거의
        /// 전량 이행 서술을 고발했던 것과 같은 함정이다.
        /// </summary>
        [Fact]
        public void ProseOutsideCodeFencesIsNotAnAssignment()
        {
            var body = Body("S04",
                "원본이 정의하지 않은 `-2`는 도입하지 않는다. currentStepErrorCode = -2 라고\n"
                + "쓰지 않는다는 뜻이다.\n\n"
                + "```mermaid\nflowchart TD\n  A[\"currentStepErrorCode = -2\"]\n```\n");

            var result = Validate(
                body,
                LegacyStep("S04", "dbo.UP_UTIL_SETTLE_CANCEL_INS"),
                Codes("dbo.UP_UTIL_SETTLE_CANCEL_INS", "-1"));

            Assert.True(result.IsValid);
        }
    }
}
