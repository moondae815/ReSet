using System;
using ReSet.Core.Services.Clients;
using Xunit;

namespace ReSet.Core.Tests
{
    public class OpenRouterRoutingOptionsQuantizationTests
    {
        // 양자화만 지정해도 라우팅이 성립해야 한다. IsEmpty가 이것을 세지 않으면
        // Parse가 null을 돌려주고, 요청에서 provider 구획이 통째로 사라진다.
        [Fact]
        public void Parse_WithOnlyQuantizations_ReturnsOptions()
        {
            var options = OpenRouterRoutingOptions.Parse(
                order: null, allowFallbacks: null, requireParameters: null,
                quantizations: new[] { "fp8" });

            Assert.NotNull(options);
            Assert.Equal(new[] { "fp8" }, options!.Quantizations);
            Assert.Null(options.Order);
        }

        // 공백 항목은 Order와 같은 규칙으로 걸러야 한다 - 설정에서 배열 원소를
        // 지우면 빈 문자열이 남는데, 그것이 그대로 나가면 400이 난다.
        [Fact]
        public void Parse_WithBlankQuantizationEntries_DropsThem()
        {
            var options = OpenRouterRoutingOptions.Parse(
                order: null, allowFallbacks: null, requireParameters: null,
                quantizations: new[] { " fp8 ", "", "   " });

            Assert.NotNull(options);
            Assert.Equal(new[] { "fp8" }, options!.Quantizations);
        }

        // 모델별 항목은 Order만 적는다. 그때 Default의 양자화 하한이 살아남지 않으면
        // 그 모델 호출에서만 fp4로 샐 길이 조용히 열린다.
        [Fact]
        public void Merge_PerModelOrderOnly_InheritsQuantizationsFromDefault()
        {
            var defaults = new OpenRouterRoutingOptions
            {
                Quantizations = new[] { "fp8" },
                AllowFallbacks = true
            };
            var perModel = new OpenRouterRoutingOptions
            {
                Order = new[] { "gmicloud/fp8", "baseten/fp8" }
            };

            var merged = OpenRouterRoutingOptions.Merge(defaults, perModel);

            Assert.NotNull(merged);
            Assert.Equal(new[] { "gmicloud/fp8", "baseten/fp8" }, merged!.Order);
            Assert.Equal(new[] { "fp8" }, merged.Quantizations);
            Assert.True(merged.AllowFallbacks);
        }

        // Default와 모델별 항목이 둘 다 양자화를 적으면 모델별 값이 이겨야 한다.
        // ??가 피연산자 순서를 반대로 짜도(baseOptions ?? overrideOptions) 앞의
        // 테스트는 한쪽이 항상 null이라 못 잡는다 - 이 테스트는 둘 다 값을 채워
        // 어느 쪽이 실제로 이기는지를 가른다.
        [Fact]
        public void Merge_PerModelAndDefaultBothSpecifyQuantizations_OverrideWins()
        {
            var defaults = new OpenRouterRoutingOptions
            {
                Quantizations = new[] { "fp8" }
            };
            var perModel = new OpenRouterRoutingOptions
            {
                Quantizations = new[] { "fp16" }
            };

            var merged = OpenRouterRoutingOptions.Merge(defaults, perModel);

            Assert.NotNull(merged);
            Assert.Equal(new[] { "fp16" }, merged!.Quantizations);
        }
    }
}
