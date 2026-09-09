using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// <c>CheckErrorCodeUniquenessClaim</c> 의 판정부가 실물에서 세 갈래로 틀린 자리다.
    ///
    /// [실측 - 2026-09-09 dbo.UP_UTIL_SETTLE_EXCEPTION_PROC 재생성, 캐시 v19]
    /// 이 검사 하나가 재시도 6 회를 전부 태우고 검증 미통과본을 배송했다
    /// (`output/logs/G1/reset-20260909.log` 9312·12565·15795·19016·22257 발화,
    /// 22259~22260 「L1 기계 검증 최종 실패 … 1차 시도(90/100)를 채택」). 코퍼스 31 편
    /// 중 이 한 편이다.
    ///
    /// 세 갈래는 서로 독립이라 시험도 셋으로 나눈다:
    ///
    ///   ① 부정문 오탐 — 시도 5·6 은 검사 메시지가 시킨 대로 「서로 다른 고유 … 가
    ///      아니라 … 중복 포함」이라고 <b>부정</b>했는데 판정부가 낱말만 보고 발화한다.
    ///      처방을 따르면 다시 걸리는 자기강화 루프다. 이것이 6 회를 태운 원인이다.
    ///   ② 지목 뒤집힘 — 배송본에서 <c>FirstOrDefault</c> 가 집는 것은 29 행,
    ///      <b>Critic 이 결함을 보고한 인용 블록</b>이다. 모델의 주장이 아니라 그 결함을
    ///      지적한 문장을 「고치라」고 돌려주고 있었다.
    ///   ③ 진짜 거짓 주장을 놓침 — 배송본 561 행 「`@po_intRetVal`에 고유 음수값 설정」과
    ///      110 행 「문장별로 고유한 음수값이 대입되며」가 통과한다. 두 문장 모두
    ///      「코드」가 같은 문장에 없어서다(110 행은 「처리 결과 코드.」 의 마침표에서
    ///      문장이 갈린다). <c>FirstOrDefault</c> 라 둘 중 하나만 잡아도 나머지는 조용하다.
    ///
    /// 픽스처는 실행 로그와 배송본에서 <b>한 글자도 고치지 않고</b> 오려 왔다. 합성
    /// 픽스처를 규격 기억으로 지으면 내 오해를 검사가 확인해 줄 뿐이다 - 잡아야 할 것은
    /// 우리가 상상한 모양이 아니라 모델이 실제로 쓴 모양이다.
    /// </summary>
    public class ErrorCodeUniquenessClaimRegressionTests
    {
        private static string LoadFixture(string name) =>
            File.ReadAllText(Path.Combine(
                RepoPaths.FindRepoRoot(), "tests", "ReSet.Core.Tests", "Fixtures", name));

        /// <summary>
        /// 비순환 오라클. 기준값은 검사가 보는 명세서가 아니라 <b>원본이 대입하는 코드</b>다
        /// (배송본 <c>### 오류 코드 (기계 확정 — 수정 금지)</c> 표와 같은 18 행 - 그 표
        /// 자체는 정적 파서가 채운 기계 확정 재료라 모델이 손대지 못한다).
        /// -1 은 UPDATE 3·4 가, -2 는 UPDATE 5·6 이 공유한다.
        /// </summary>
        private static SpecExpectations ExceptionProcErrorCodes()
        {
            var codes = new (int Ordinal, string Code)[]
            {
                (1, "-101"), (2, "-102"), (3, "-1"), (4, "-1"), (5, "-2"), (6, "-2"),
                (7, "-3"), (8, "-4"), (9, "-5"), (10, "-10"), (11, "-11"), (12, "-19"),
                (13, "-20"), (14, "-201"), (15, "-21"), (16, "-27"), (17, "-28"), (18, "-29"),
            };

            return new SpecExpectations(
                new List<UpdateColumnExpectation>(),
                new Dictionary<string, IReadOnlySet<string>>(),
                new HashSet<string>(),
                new List<string>())
            {
                ErrorCodes = codes
                    .Select(c => new ErrorCodeFact("UPDATE", c.Ordinal, c.Code, "@po_intRetVal"))
                    .ToList()
            };
        }

        private static string WrapSpec(string body) =>
            string.Join("\n", new[]
            {
                "## 개요", "내용", "## 파라미터 목록", "내용",
                "## CRUD 분석", body,
                "## 로직 흐름 요약", "내용", "## 비즈니스 흐름 시각화",
                "```mermaid", "flowchart TD", "A[\"시작\"] --> B[\"끝\"]", "```"
            });

        private static IReadOnlyList<string> UniquenessErrors(string fixtureName)
        {
            var result = new MechanicalValidator()
                .Validate(WrapSpec(LoadFixture(fixtureName)), ExceptionProcErrorCodes());

            // 이 검사만 골라 본다. 발췌라 다른 검사가 함께 우는 것은 이 시험의 관심사가 아니다.
            return result.Errors.Where(e => e.Contains("「고유」라 단정")).ToList();
        }

        // ① 시도 5 의 산문은 그 낱말이 문서에 단 한 곳 나오고 <b>그 문장이 옳다</b>.
        //    고칠 것이 없는데 발화하므로 모델은 다음 시도에서도 같은 자리를 맴돈다.
        [Fact]
        public void Attempt5_WhenProseDeniesUniqueness_ShouldStaySilent()
        {
            Assert.Empty(UniquenessErrors("ExceptionProcAttempt5UniqueDenialExcerpt.md"));
        }

        // ① 시도 6 도 같다. 게다가 「해당 문장 고유(단, 일부 중복 포함) 음수 코드」처럼
        //    괄호로 한정한 서술까지 들어 있다 - 이것도 유일성 주장이 아니다.
        [Fact]
        public void Attempt6_WhenProseDeniesUniqueness_ShouldStaySilent()
        {
            Assert.Empty(UniquenessErrors("ExceptionProcAttempt6UniqueDenialExcerpt.md"));
        }

        // ③ 배송본에는 진짜 거짓 주장이 둘 있다. 하나만 잡고 끝내면 나머지가 살아남아
        //    다음 시도에서 새로 발화한다 - 재시도를 태우는 모양 그대로다.
        [Fact]
        public void Delivered_ShouldReportBothFalseClaims()
        {
            var error = Assert.Single(UniquenessErrors("ExceptionProcDeliveredUniqueClaimExcerpt.md"));

            // 561 행 - 로직 흐름 요약의 「`@po_intRetVal`에 고유 음수값 설정」.
            Assert.Contains("고유 음수값 설정", error);
            // 110 행 - 파라미터 표 셀의 「문장별로 고유한 음수값이 대입되며」.
            //    「처리 결과 코드.」 에서 문장이 갈려 「코드」가 같은 문장에 없다.
            Assert.Contains("문장별로 고유한 음수값이 대입", error);
        }

        // ② 인용 줄(`>`)은 모델의 주장이 아니라 L2 리뷰가 <b>그 결함을 지적한</b> 블록이다.
        //    이것을 지목하면 「고치라」는 지시가 결함을 고발한 문장을 겨눈다.
        [Fact]
        public void Delivered_ShouldNotPointAtTheCriticQuoteBlock()
        {
            var error = Assert.Single(UniquenessErrors("ExceptionProcDeliveredUniqueClaimExcerpt.md"));

            Assert.DoesNotContain("[정확성 - 기계 확정 표와 모순]", error);
        }

        // ④ 「서로 다른」이 <b>코드가 아닌 것</b>을 꾸미면 주장이 아니다.
        //
        // [실물 - 2026-09-09 두 번째 재생성, 시도 3] ①②③ 을 닫은 뒤 새로 드러난 자리다.
        // 「이는 두 개의 서로 다른 업무 로직 단계가 같은 실패 코드로 매핑되어 있어…」는
        // 옳다 - 「서로 다른」이 꾸미는 것은 <b>단계</b>이고, 코드에 대해서는 「같은」이라고
        // 정확히 적었다. 그런데 낱말이 같은 절에 있다는 것만 보고 발화했다.
        //
        // 이 오탐이 특히 나쁜 이유: 이 문장을 쓰게 만든 것이 <b>이 검사의 처방 문구</b>다.
        // 「공유 관계를 그대로 적어 『같은 코드를 쓰는 문장이 있다』는 사실을 서술하라」를
        // 따르면 자연히 저 문장이 나오고, 그 문장이 다시 걸린다 - ① 과 똑같은 자기강화
        // 루프가 한 겹 아래에서 다시 열린 것이다.
        [Fact]
        public void Attempt3_WhenTheModifierIsNotAboutTheCodes_ShouldStaySilent()
        {
            var error = Assert.Single(UniquenessErrors("ExceptionProcAttempt3ScopeExcerpt.md"));

            Assert.DoesNotContain("서로 다른 업무 로직 단계", error);
        }

        // ④ 를 닫으면서 ①②③ 이 잡던 것을 함께 잃으면 안 된다. 같은 문서의 진짜 거짓
        //    주장 둘은 그대로 발화해야 한다 - 둘 다 「고유」가 오류 코드를 직접 꾸민다.
        [Fact]
        public void Attempt3_ShouldStillReportTheRealClaims()
        {
            var error = Assert.Single(UniquenessErrors("ExceptionProcAttempt3ScopeExcerpt.md"));

            // 개요 - 「고유한 음수 오류 코드를 출력 파라미터에 설정하고」
            Assert.Contains("고유한 음수 오류 코드", error);
            // 로직 흐름 요약 - 「해당 단계 고유의 음수 오류 코드를 `@po_intRetVal`에」
            Assert.Contains("고유의 음수 오류 코드", error);
            Assert.Contains("2자리", error);
        }

        // ④ 를 닫는 규칙(관형 수식만 본다)이 서술 용법에 구멍을 낸다 - 「코드는 고유하다」는
        //    지시어가 낱말보다 앞에 온다. 가장 좁은 모양 하나만 따로 받고 여기서 잠근다.
        //
        //    [이 시험은 오라클이 아니라 잠금이다] 위 넷과 달리 재료가 실물이 아니다 -
        //    이 모양은 코퍼스에서 관측된 적이 없고 예방으로 넣은 가지다. 「모델이 이렇게
        //    쓴다」의 근거로 쓰지 마라. 넓히려거든 실측부터 하라.
        [Fact]
        public void WhenTheClaimIsPredicative_ShouldStillReport()
        {
            var markdown = WrapSpec(
                "각 UPDATE 문의 오류 코드는 고유하므로 호출자가 실패 지점을 특정할 수 있다.\n\n"
                + LoadFixture("ExceptionProcAttempt3ScopeExcerpt.md")
                    .Split("\n")
                    .SkipWhile(l => !l.StartsWith("###"))
                    .Aggregate((a, b) => a + "\n" + b));

            var result = new MechanicalValidator().Validate(markdown, ExceptionProcErrorCodes());

            Assert.Contains(result.Errors, e => e.Contains("오류 코드는 고유하므로"));
        }

        // 한 절에 같은 낱말이 두 번 나오고 <b>앞의 것만</b> 코드가 아닌 것을 꾸미는 모양.
        // 앞자리에서 판정을 끝내면 뒷자리의 진짜 주장이 가려진다 - 이번 회차가 내내 잡은
        // FirstOrDefault 와 같은 병이라 자리를 잠근다.
        //
        // [이 시험도 오라클이 아니라 잠금이다] 위 예방 시험과 같다 - 코퍼스에서 관측된
        // 적이 없다. 「모델이 이렇게 쓴다」의 근거로 쓰지 마라.
        [Fact]
        public void WhenTheFirstOccurrenceIsNotAboutTheCodes_ShouldStillSeeTheSecond()
        {
            var markdown = WrapSpec(
                "서로 다른 업무 단계가 서로 다른 오류 코드를 갖는다.\n\n"
                + LoadFixture("ExceptionProcAttempt3ScopeExcerpt.md")
                    .Split("\n")
                    .SkipWhile(l => !l.StartsWith("###"))
                    .Aggregate((a, b) => a + "\n" + b));

            var result = new MechanicalValidator().Validate(markdown, ExceptionProcErrorCodes());

            Assert.Contains(result.Errors, e => e.Contains("서로 다른 업무 단계가 서로 다른 오류 코드"));
        }

        // 인용 블록 하나만 있고 모델의 주장이 없으면 발화 자체가 없어야 한다.
        // 위 시험은 「인용 말고 다른 것을 골랐다」로도 통과하므로 이 자리를 따로 잠근다.
        [Fact]
        public void WhenOnlyTheCriticQuoteMakesTheClaim_ShouldStaySilent()
        {
            var quoteOnly = LoadFixture("ExceptionProcDeliveredUniqueClaimExcerpt.md")
                .Split('\n')
                .Where(line => line.StartsWith(">") || line.StartsWith("|") || line.StartsWith("###"))
                .Where(line => !line.Contains("고유한 음수값이 대입"))
                .ToList();

            var result = new MechanicalValidator()
                .Validate(WrapSpec(string.Join("\n", quoteOnly)), ExceptionProcErrorCodes());

            Assert.DoesNotContain(result.Errors, e => e.Contains("「고유」라 단정"));
        }
    }
}
