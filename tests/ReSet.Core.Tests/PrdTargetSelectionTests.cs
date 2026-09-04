using System;
using System.Collections.Generic;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdTargetSelectionTests
    {
        [Fact]
        public void Resolve_ShouldNotPullInATargetWhoseLabelIsAPrefixOfAnotherSelectedLabel()
        {
            // 실제 코퍼스 사례: "dbo.UP_UTIL_SETTLE_INS"는
            // "dbo.UP_UTIL_SETTLE_INS_EXTRA"의 접두어다. 긴 쪽만 골랐을 때
            // 짧은 쪽까지 딸려 들어오면 안 된다(StartsWith 되짚기의 결함).
            var shorter = new PrdTarget("dbo.UP_UTIL_SETTLE_INS", "/docs/short", HasExistingPrd: false);
            var longer = new PrdTarget("dbo.UP_UTIL_SETTLE_INS_EXTRA", "/docs/long", HasExistingPrd: false);
            var targets = new List<PrdTarget> { shorter, longer };

            var picked = new List<string> { PrdTargetSelection.ToDisplayLabel(longer) };

            var resolved = PrdTargetSelection.Resolve(targets, picked);

            Assert.Single(resolved);
            Assert.Equal("dbo.UP_UTIL_SETTLE_INS_EXTRA", resolved[0].Label);
        }

        [Fact]
        public void Resolve_ShouldMatchDisplayLabelIncludingExistingPrdSuffix()
        {
            var target = new PrdTarget("dbo.UP_A", "/docs/a", HasExistingPrd: true);
            var picked = new List<string> { PrdTargetSelection.ToDisplayLabel(target) };

            var resolved = PrdTargetSelection.Resolve(new List<PrdTarget> { target }, picked);

            Assert.Single(resolved);
            Assert.Equal("dbo.UP_A", resolved[0].Label);
        }

        [Fact]
        public void Resolve_ShouldReturnEmpty_WhenNothingPicked()
        {
            var target = new PrdTarget("dbo.UP_A", "/docs/a", HasExistingPrd: false);

            var resolved = PrdTargetSelection.Resolve(new List<PrdTarget> { target }, new List<string>());

            Assert.Empty(resolved);
        }

        // Spectre.Console의 실제 Markup.Escape는 "["→"[[", "]"→"]]"로 두 배로 만든다.
        // CLI 없이도 같은 이스케이프 성질(원문을 그대로 복원할 수 있는 가역 변환)을 재현하려고
        // 이 테스트 전용의 단순 이스케이프 함수를 쓴다 - Spectre.Console을 참조하지 않고도
        // "선택지와 키가 같은 함수에서 나와야 한다"는 계약을 검증할 수 있다.
        private static string FakeEscape(string s) => s.Replace("[", "[[").Replace("]", "]]");

        [Fact]
        public void Resolve_ShouldMatchTarget_WhenChoicesAndKeysUseTheSameEscapingSelector()
        {
            // CLI 쪽 시나리오를 그대로 재현한다: 대상 Label에 Spectre 마크업 구분자("[", "]")가
            // 들어있고, 선택지 문자열도 되짚기 키도 정확히 같은 selector(FakeEscape ∘
            // ToDisplayLabel)로 만든다. 이 경우엔 이스케이프가 걸려 있어도 정확히 매칭돼야 한다.
            var target = new PrdTarget("dbo.UP_[Legacy]", "/docs/legacy", HasExistingPrd: false);
            var targets = new List<PrdTarget> { target };
            Func<PrdTarget, string> escapingSelector = t => FakeEscape(PrdTargetSelection.ToDisplayLabel(t));

            var picked = new List<string> { escapingSelector(target) };

            var resolved = PrdTargetSelection.Resolve(targets, picked, escapingSelector);

            Assert.Single(resolved);
            Assert.Equal("dbo.UP_[Legacy]", resolved[0].Label);
        }

        [Fact]
        public void Resolve_ShouldReturnEmpty_WhenChoiceSelectorAndKeySelectorDiverge()
        {
            // 코디네이터가 지적한 침묵 결함을 그대로 고정한다: 선택지는 이스케이프된 문자열로
            // 만들었는데(escapingSelector), 되짚기는 이스케이프 안 된 기본 selector(ToDisplayLabel,
            // 2-인자 Resolve 오버로드가 쓰는 것)로 하면 사전 키가 어긋나 아무것도 안 잡힌다 -
            // 예외도 없이 결과가 조용히 비어버린다. 이 테스트는 "선택지 생성 함수와 키 생성
            // 함수가 다르면 실패해야 한다"를 못박아, 나중에 누군가 이 계약을 깨도 여기서 걸린다.
            var target = new PrdTarget("dbo.UP_[Legacy]", "/docs/legacy", HasExistingPrd: false);
            var targets = new List<PrdTarget> { target };
            Func<PrdTarget, string> escapingSelector = t => FakeEscape(PrdTargetSelection.ToDisplayLabel(t));

            var picked = new List<string> { escapingSelector(target) };

            // 2-인자 오버로드는 내부적으로 ToDisplayLabel(이스케이프 없음)을 키로 쓴다 -
            // escapingSelector로 만든 picked 문자열과 어긋난다.
            var resolved = PrdTargetSelection.Resolve(targets, picked);

            Assert.Empty(resolved);
        }
    }
}
