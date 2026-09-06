using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// Fix Round 1 리뷰 발견(Important 3) - PolicyDocumentAssembler에는 전용 테스트가
    /// 없었다. 개요·단계 본문을 잇는 것은 단순 연결이라 자명하지만, 부록 A·B는
    /// 기계가 코드값 사전·명부에서 조립하는 실질 로직이고 설계서 §4-4가 코드값
    /// 사전 행의 세 가지 표기(번역됨/의미 미상 두 종류)를 구별하라고 요구한다 -
    /// 그 구별이 실제로 조립 결과에 나타나는지 직접 잰다.
    /// </summary>
    public sealed class PolicyDocumentAssemblerTests
    {
        private static SettlementCodebook Codebook() => new(
            new List<CodebookEntry>
            {
                // 매칭됨 - 프로파일링에서 실제 행을 찾은 경우
                new CodebookEntry(
                    "S02", "Status", new List<string> { "dbo.UP_A" }, true,
                    new List<CodebookMatch>
                    {
                        new CodebookMatch("dbo.CommonCode",
                            new Dictionary<string, string> { ["Code"] = "S02", ["Name"] = "정산보류" }),
                    }),
                // 매칭 대상이었으나(길이 조건 통과) 마스터 데이터에서 못 찾은 경우
                new CodebookEntry(
                    "XYZ999", null, new List<string> { "dbo.UP_B" }, true,
                    Array.Empty<CodebookMatch>()),
                // 값이 짧아 애초에 매칭 대상에서 제외된 경우
                new CodebookEntry(
                    "Y", null, new List<string> { "dbo.UP_A" }, false,
                    Array.Empty<CodebookMatch>()),
            },
            Array.Empty<string>());

        private static SettlementProcessRoster Roster() => new(
            new List<PolicyStage>
            {
                new PolicyStage("1. 요율 적재", new List<string> { "dbo.UP_A" }),
                new PolicyStage("2. 원장 적재", new List<string> { "dbo.UP_B" }),
            },
            new List<string> { "dbo.UP_EXCLUDED" });

        private static string Assemble() => PolicyDocumentAssembler.Assemble(
            "## 정산 업무 개요\n\n전체 조망.\n",
            new List<string> { "## 1. 요율 적재\n\n본문1.\n", "## 2. 원장 적재\n\n본문2.\n" },
            Codebook(),
            Roster());

        [Fact]
        public void 개요와_단계_본문이_그대로_들어간다()
        {
            var result = Assemble();

            Assert.Contains("## 정산 업무 개요", result);
            Assert.Contains("## 1. 요율 적재", result);
            Assert.Contains("## 2. 원장 적재", result);
        }

        [Fact]
        public void 부록_A와_B가_H2_헤딩으로_있다()
        {
            var result = Assemble();

            Assert.Contains("\n## 부록 A. 코드값 사전\n", result);
            Assert.Contains("\n## 부록 B. 단계별 원본 프로시저\n", result);
        }

        [Fact]
        public void 부록이_본문보다_뒤에_온다()
        {
            var result = Assemble();

            var stageIndex = result.IndexOf("## 2. 원장 적재", StringComparison.Ordinal);
            var appendixAIndex = result.IndexOf("## 부록 A. 코드값 사전", StringComparison.Ordinal);
            var appendixBIndex = result.IndexOf("## 부록 B. 단계별 원본 프로시저", StringComparison.Ordinal);

            Assert.True(stageIndex >= 0 && appendixAIndex > stageIndex);
            Assert.True(appendixBIndex > appendixAIndex);
        }

        /// <summary>
        /// Fix Round 2 리뷰 발견 - 이전 판은 값("`S02`")과 메시지("Code=S02, ...")를
        /// 문서 전체 기준으로 따로따로 Assert.Contains 했다. 그러면 둘이 같은 표
        /// 행에 있는지는 안 보고, 「문서 어딘가에 둘 다 있다」만 본다 - 카테고리를
        /// 맞바꾸는 변이(매칭됨과 매칭 대상이었으나 못 찾음을 서로 바꿔치기)가
        /// 7건 전부를 무사히 통과시켰다(2026-09-06 리뷰가 역변이로 실증).
        /// 값과 메시지를 하나의 연속 문자열(같은 행)로 이어 붙여 단언해야
        /// "그 값의 그 행에 그 메시지가 왔다"를 실제로 잰다. 실제 출력을 먼저
        /// 확인해(ZZZ_DEBUG_PRINT로 임시 출력) 이스케이프가 이 값들에는 사실상
        /// no-op임을(파이프·개행이 없어 MarkdownTableCellCodec.Escape가 손대지
        /// 않음) 확인한 뒤 그 원문 그대로를 기대값으로 적었다.
        /// </summary>
        [Fact]
        public void 매칭된_코드값은_같은_행에_매칭된_의미와_출처를_싣는다()
        {
            var result = Assemble();

            Assert.Contains("| `S02` | Code=S02, Name=정산보류 | dbo.CommonCode | dbo.UP_A |", result);
        }

        [Fact]
        public void 매칭_대상이었지만_찾지_못한_코드값은_같은_행에_찾지_못했다고_구별해_싣는다()
        {
            var result = Assemble();

            Assert.Contains("| `XYZ999` | 의미 미상 (마스터 데이터에서 찾지 못함) | - | dbo.UP_B |", result);
        }

        [Fact]
        public void 값이_짧아_대상에서_제외된_코드값은_같은_행에_판별_불가로_구별해_싣는다()
        {
            var result = Assemble();

            Assert.Contains("| `Y` | 의미 미상 (값이 짧아 판별 불가) | - | dbo.UP_A |", result);
        }

        [Fact]
        public void 부록_B에_명부의_단계와_소속_프로시저가_실린다()
        {
            var result = Assemble();

            Assert.Contains("| 1. 요율 적재 | dbo.UP_A |", result);
            Assert.Contains("| 2. 원장 적재 | dbo.UP_B |", result);
            Assert.Contains("| (제외) | dbo.UP_EXCLUDED |", result);
        }
    }
}
