using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// L1 하한 검사의 재료(<c>codesByProcedure</c>)가 명세서 산문 하나에만 기대는
    /// 것을 고친 자리를 잠근다.
    ///
    /// [왜 필요한가 - 2026-08-31 POQSettleBatch5 대조 실행] S16이 원본에 있는
    /// <c>-5..-8</c>을 "발명했다"고 고발당했다. 추적하면 재료를 만드는
    /// <see cref="SpecReturnCodeExtractor"/>가 Spec.md <b>산문</b>을 정규식으로
    /// 훑는데, 그 명세서의 산문이 <c>-5</c>~<c>-8</c>만 "「-5」를 반환합니다" 꼴로
    /// 적어 <c>@po_intRetVal =</c> 패턴에 걸리지 않았다. 반환 코드 표는 여덟 개를
    /// 다 갖고 있었다 - 즉 오라클이 모델이 쓴 산문 표현의 흔들림에 걸려 있었다.
    ///
    /// [왜 교체가 아니라 합집합인가 - 코퍼스 120쌍 실측] 산출물 트리 5 × 명세서 24를
    /// 두 추출기로 대조했다. DDL에만 있고 산문이 떨어뜨린 것은 1편 2쌍뿐이지만,
    /// <b>산문에만 있고 DDL의 DML에는 없는 것이 6편 29쌍</b>이다(<c>-9</c>,
    /// <c>4000</c>, <c>-3</c>, <c>-15</c> - DML 문장에 붙지 않은 가드·CATCH 코드라
    /// <see cref="DmlScopeExtractor.ExtractErrorCodes"/>가 원리적으로 못 낸다).
    /// 재료를 DDL로 <b>교체</b>하면 그 29쌍이 전부 새 오탐이 된다 - 지금보다 14배
    /// 나쁘다. 그래서 합집합이다.
    /// </summary>
    public class ErrorCodeMaterialTests
    {
        /// <summary>
        /// 가드 넷이 각각 다른 코드를 다는 원본. 실물
        /// <c>UP_Util_Settle_Summary</c>의 축소판이다(그 원본은 같은 모양으로
        /// <c>-1</c>~<c>-8</c>을 단다).
        /// </summary>
        private const string DdlWithFourCodes = @"CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    UPDATE A SET A.X = 1 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -1 RETURN END
    UPDATE A SET A.Y = 2 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -2 RETURN END
    DELETE A FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -5 RETURN END
    INSERT INTO dbo.T (X) VALUES (1)
    IF @@ERROR <> 0 BEGIN SET @po_intRetVal = -6 RETURN END
END";

        private static SpDefinition Def(string name, string ddl) => new()
        {
            Name = name,
            DdlText = ddl,
        };

        /// <summary>
        /// 산문이 일부만 <c>@po_intRetVal =</c> 꼴로 적은 명세서. 실물의 결함 모양
        /// 그대로다 - 앞의 둘은 대입 꼴, 뒤의 둘은 "「-5」를 반환합니다" 꼴.
        /// </summary>
        private static IReadOnlyDictionary<string, IReadOnlyList<string>> SpecCodes(
            string fileName, string prose) =>
            SpecReturnCodeExtractor.Extract(new[] { (fileName, prose) });

        [Fact]
        public void Merge_WhenProseDroppedCodesTheDdlHas_ShouldRecoverThem()
        {
            var spec = SpecCodes(
                "dbo.P",
                "실패하면 `@po_intRetVal = -1`을 설정합니다.\n"
                + "다음 단계가 실패하면 `@po_intRetVal = -2`를 설정합니다.\n"
                + "삭제가 실패하면 `-5`를 반환합니다.\n"
                + "입력이 실패하면 `-6`을 반환합니다.\n");

            // 전제 - 이 테스트가 재는 결함이 실제로 재현된다.
            Assert.Equal(new[] { "-1", "-2" }, spec["p"]);

            var merged = ErrorCodeMaterial.Merge(spec, new[] { Def("dbo.P", DdlWithFourCodes) });

            // 순서는 계약이 아니다 - 산문 몫이 먼저 오고 DDL 몫이 뒤에 붙는 것이
            // 자연스럽지만, 소비자 셋 중 어느 것도 순서를 읽지 않는다.
            Assert.Equal(
                new[] { "-1", "-2", "-5", "-6" },
                merged["p"].OrderByDescending(c => int.Parse(c)).ToArray());
        }

        [Fact]
        public void Merge_WhenSpecHasACodeTheDdlCannotProduce_ShouldKeepIt()
        {
            // 교체가 아니라 합집합이라는 것. 가드가 DML 문장에 붙지 않은 코드는
            // ExtractErrorCodes가 원리적으로 못 내므로, 교체하면 이 코드가 사라지고
            // 그 즉시 순방향 검사의 새 오탐이 된다(코퍼스 29쌍).
            var spec = SpecCodes("dbo.P", "파라미터가 비면 `@po_intRetVal = 4000`을 설정하고 즉시 반환합니다.\n");

            var merged = ErrorCodeMaterial.Merge(spec, new[] { Def("dbo.P", DdlWithFourCodes) });

            Assert.Contains("4000", merged["p"]);
        }

        [Fact]
        public void Merge_ShouldIgnoreGuardsThatAssignToOtherVariables()
        {
            // 재료의 뜻은 「원본이 반환하는 오류코드」다. 가드가 지역 변수에 넣는
            // 값까지 끌어오면 검사가 반환 코드가 아닌 것을 반환 코드로 요구한다.
            const string ddl = @"CREATE PROCEDURE dbo.P @pi_strYMD CHAR(8), @po_intRetVal INT OUTPUT AS
BEGIN
    DECLARE @intErr INT
    UPDATE A SET A.X = 1 FROM dbo.T AS A WHERE A.YMD = @pi_strYMD
    IF @@ERROR <> 0 BEGIN SET @intErr = -99 RETURN END
END";

            var merged = ErrorCodeMaterial.Merge(
                SpecCodes("dbo.P", "실패하면 `@po_intRetVal = -1`을 설정합니다.\n"),
                new[] { Def("dbo.P", ddl) });

            Assert.Equal(new[] { "-1" }, merged["p"]);
        }

        [Fact]
        public void Merge_WhenNeitherSideHasCodes_ShouldNotCreateAKey()
        {
            // 빈 목록과 "그런 프로시저 없음"이 같아지면 안 된다는 것은
            // SpecReturnCodeExtractor.Extract가 이미 지키는 계약이다. 합집합도 같은
            // 계약을 지켜야 한다 - 빈 키를 만들면 CheckLegacyStepErrorCodeInvention의
            // hasMaterial이 참이 되어, 재료가 없는 프로시저에서 검사가 돌기 시작한다.
            const string ddl = @"CREATE PROCEDURE dbo.Q AS BEGIN SELECT 1 FROM dbo.T END";

            var merged = ErrorCodeMaterial.Merge(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                new[] { Def("dbo.Q", ddl) });

            Assert.False(merged.ContainsKey("q"));
        }

        [Fact]
        public void Merge_WithNullDefinitions_ShouldReturnTheSpecMaterialUnchanged()
        {
            // definitions는 선택 인자다(RunConsolidatedPipelineAsync). 없으면
            // 오늘과 똑같이 동작해야 한다 - 조용히 재료를 잃으면 안 된다.
            var spec = SpecCodes("dbo.P", "실패하면 `@po_intRetVal = -1`을 설정합니다.\n");

            var merged = ErrorCodeMaterial.Merge(spec, null);

            Assert.Equal(new[] { "-1" }, merged["p"]);
        }

        /// <summary>
        /// 재료를 만드는 자리가 파이프라인에 하나뿐이라는 것.
        ///
        /// [왜 소스 텍스트를 보는가] 이 결함의 실제 모양은 "한쪽만 고쳐졌다"이다 -
        /// 루프 안의 <c>CheckLegacyStepErrorCodeInvention</c>과 배너의
        /// <c>FindMissingErrorCodes</c>가 같은 사실을 각자 계산하면, 문서 본문은
        /// 통과했는데 배너는 "누락"이라고 말하는(또는 그 반대) 회차가 나온다.
        /// 그 어긋남은 파이프라인을 통째로 돌려야만 드러나므로 단위 테스트로
        /// 재현할 수 없다. 대신 <b>재료를 만드는 문장이 하나뿐</b>이라는 구조를
        /// 잠근다 - 갈라짐이 물리적으로 불가능해진다.
        ///
        /// 같은 취지의 경고가 이 파일 3226행 부근 주석에 이미 있었다("같은 사실을
        /// 두 번 계산하면 한쪽만 고쳐지는 사고가 난다 - 이 저장소가 이미 겪었다").
        /// 그 주석이 지키지 못한 것을 이 단언이 지킨다.
        /// </summary>
        [SkippableFact]
        public void Orchestrator_ShouldBuildTheErrorCodeMaterialInExactlyOnePlace()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var source = Path.Combine(
                root, "src", "ReSet.Core", "Services", "VerificationPipelineOrchestrator.cs");
            Skip.IfNot(File.Exists(source), CorpusSkip.Reason);

            var text = File.ReadAllText(source);

            var builds = text.Split("SpecReturnCodeExtractor.Extract(").Length - 1;
            Assert.True(
                builds == 1,
                $"오류코드 재료를 만드는 자리가 {builds}곳이다 - 하나여야 한다. "
                + "루프와 배너가 각자 만들면 두 오라클이 갈린다.");

            var merges = text.Split("ErrorCodeMaterial.Merge(").Length - 1;
            Assert.True(
                merges == 1,
                $"ErrorCodeMaterial.Merge 호출이 {merges}곳이다 - 하나여야 한다. "
                + "산문 재료를 합집합으로 감싸지 않은 자리가 있으면 오탐이 되살아난다.");
        }

        /// <summary>
        /// 실물 코퍼스에서 합집합이 (a) 산문 재료를 하나도 잃지 않고 (b) 재료를
        /// 실제로 만든다는 것.
        ///
        /// [왜 하한만 단언하는가] ErrorCodeTableCorpusTests의 같은 문단과 같은
        /// 근거다 - 정확값으로 못박으면 코퍼스가 늘 때마다 빨개지고 다음 사람이
        /// 관측을 읽는 대신 기대값을 고친다. 하한은 <b>추출기가 조용히 비는</b>
        /// 회귀를 잡는다 - 이 저장소가 반복해 겪은 실패 양식이고, 이 테스트가
        /// 없으면 Merge가 통째로 빈 사전을 돌려줘도 위 단위 테스트 넷은 여전히
        /// 초록이다(합성 DDL만 보므로).
        ///
        /// [이 테스트가 회복을 시연하지는 않는다] 오늘 <c>output/</c>의
        /// <c>UP_Util_Settle_Summary</c> 명세서는 산문에 <c>-1</c>~<c>-8</c>을 다
        /// 대입 꼴로 적은 건강한 표본이다(직접 실측). 결함이 있는 표본은 다른 두
        /// 산출물 트리에 있고 그것들은 gitignore라 여기서 못 읽는다. 회복 자체는
        /// 위 <c>Merge_WhenProseDroppedCodesTheDdlHas_ShouldRecoverThem</c>이
        /// 그 표본의 축소판으로 잠근다.
        /// </summary>
        [SkippableFact]
        public void Merge_OverTheRealCorpus_ShouldNeverLoseSpecMaterialAndShouldNotBeSilentlyEmpty()
        {
            var root = CorpusPaths.RepoRoot();
            Skip.If(string.IsNullOrEmpty(root), CorpusSkip.Reason);

            var proceduresRoot = Path.Combine(root, "output", "Procedures");
            Skip.IfNot(Directory.Exists(proceduresRoot), CorpusSkip.Reason);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var specs = new List<(string FileName, string Content)>();
            var definitions = new List<SpDefinition>();

            foreach (var dir in Directory.EnumerateDirectories(proceduresRoot).OrderBy(d => d, StringComparer.Ordinal))
            {
                var meta = Path.Combine(dir, "raw", "metadata.json");
                var spec = Path.Combine(dir, "docs", "Spec.md");
                if (!File.Exists(meta) || !File.Exists(spec)) continue;

                var def = JsonSerializer.Deserialize<SpDefinition>(File.ReadAllText(meta), opts);
                if (def == null) continue;

                definitions.Add(def);
                specs.Add((Path.GetFileName(dir), File.ReadAllText(spec)));
            }

            Skip.If(definitions.Count == 0, CorpusSkip.Reason);

            var specCodes = SpecReturnCodeExtractor.Extract(specs);
            var merged = ErrorCodeMaterial.Merge(specCodes, definitions);

            var lost = new List<string>();
            foreach (var (key, codes) in specCodes)
            {
                if (!merged.TryGetValue(key, out var after))
                {
                    lost.Add($"{key}: 키가 통째로 사라졌다");
                    continue;
                }

                foreach (var code in codes)
                {
                    if (!after.Contains(code, StringComparer.Ordinal))
                    {
                        lost.Add($"{key}: {code}");
                    }
                }
            }

            Assert.True(lost.Count == 0, "합집합이 산문 재료를 잃었다:\n  " + string.Join("\n  ", lost));

            // 하한. 합집합이 통째로 비면 위 단언은 공허하게 참이 된다.
            Assert.True(
                merged.Count > 0,
                $"합집합 재료를 가진 프로시저가 하나도 없다(프로시저 {definitions.Count}) - "
                + "SpecReturnCodeExtractor.Extract 또는 ErrorCodeMaterial.Merge가 조용히 비었다");
            Assert.True(
                merged.Values.Sum(v => v.Count) > 0,
                "합집합 코드 총수가 0이다 - 같은 이유로 의심한다");
        }
    }
}
