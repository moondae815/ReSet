using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    public class PrdDerivationServiceTests : IDisposable
    {
        private readonly string _docsDir;

        public PrdDerivationServiceTests()
        {
            _docsDir = Path.Combine(Path.GetTempPath(), "reset-prd-" + Guid.NewGuid().ToString("N"), "docs");
            Directory.CreateDirectory(_docsDir);
            File.WriteAllText(Path.Combine(_docsDir, "Spec.md"), Spec);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(_docsDir)!, true); } catch { }
        }

        private const string Spec = @"## 개요

일별 정산 마감을 수행한다.

## 파라미터 목록

@BaseDate 를 받는다.

## CRUD 분석

TB_SETTLE_DAILY에 INSERT 한다.

## 로직 흐름 요약

기준일자를 검증한다.
";

        private static string SoundPrd() =>
            "## 배경 및 목적\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-BG-01 | 일별 정산을 마감한다 | ## 개요 > \"일별 정산 마감\" | 도출 |\n\n"
            + "## 수행 조건 및 입력 계약\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-IN-01 | 기준일자를 받는다 | ## 파라미터 목록 > \"@BaseDate\" | 도출 |\n\n"
            + "## 데이터 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-DATA-01 | 미집계 건을 적재한다 | ## CRUD 분석 > \"TB_SETTLE_DAILY에 INSERT\" | 도출 |\n\n"
            + "## 기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-FUNC-01 | 기준일자를 검증한다 | ## 로직 흐름 요약 > \"기준일자를 검증한다\" | 도출 |\n\n"
            + "## 예외 및 비기능 요구사항\n\n| ID | 요구사항 | 근거 | 확신도 |\n| :--- | :--- | :--- | :--- |\n"
            + "| REQ-NFR-01 | 중복 적재를 막는다 | ## CRUD 분석 > \"INSERT\" | 추정 |\n";

        private static string BrokenPrd() =>
            SoundPrd().Replace("TB_SETTLE_DAILY에 INSERT\"", "TB_SETTLE_MONTHLY에 INSERT\"");

        private static IAiService AiReturning(params string[] bodies)
        {
            var ai = Substitute.For<IAiService>();
            var call = 0;
            ai.GeneratePrdFromSpecAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new AiResult { Content = bodies[Math.Min(call++, bodies.Length - 1)] }));
            ai.ProviderName.Returns("TestProvider");
            ai.ModelName.Returns("test-model");
            return ai;
        }

        [Fact]
        public async Task DeriveAsync_ShouldWritePrdBesideSpec()
        {
            var service = new PrdDerivationService(AiReturning(SoundPrd()));

            var outcome = await service.DeriveAsync(_docsDir, "dbo.UP_TEST", null, CancellationToken.None);

            Assert.Equal(Path.Combine(_docsDir, "Prd.md"), outcome.PrdPath);
            Assert.True(File.Exists(outcome.PrdPath));
            Assert.True(outcome.AttributionClean);
        }

        [Fact]
        public async Task DeriveAsync_ShouldRetryOnce_WhenAttributionFails()
        {
            var ai = AiReturning(BrokenPrd(), SoundPrd());
            var service = new PrdDerivationService(ai);

            var outcome = await service.DeriveAsync(_docsDir, "dbo.UP_TEST", null, CancellationToken.None);

            Assert.True(outcome.AttributionClean);
            await ai.Received(2).GeneratePrdFromSpecAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DeriveAsync_ShouldSaveWithDefectBanner_WhenRetryStillFails()
        {
            // 결함이 있다고 문서를 버리면 사람이 볼 것도, 무엇이 틀렸는지도 사라진다.
            var service = new PrdDerivationService(AiReturning(BrokenPrd(), BrokenPrd()));

            var outcome = await service.DeriveAsync(_docsDir, "dbo.UP_TEST", null, CancellationToken.None);

            Assert.False(outcome.AttributionClean);
            Assert.True(File.Exists(outcome.PrdPath));
            var written = await File.ReadAllTextAsync(outcome.PrdPath);
            Assert.Contains("CAUTION", written);
            Assert.Contains("REQ-DATA-01", written);
        }

        [Fact]
        public async Task DeriveAsync_ShouldNotRetry_WhenTheFirstDraftIsClean()
        {
            var ai = AiReturning(SoundPrd());
            var service = new PrdDerivationService(ai);

            await service.DeriveAsync(_docsDir, "dbo.UP_TEST", null, CancellationToken.None);

            await ai.Received(1).GeneratePrdFromSpecAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task DeriveAsync_ShouldThrow_WhenSpecIsAbsent()
        {
            var emptyDir = Path.Combine(Path.GetTempPath(), "reset-prd-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyDir);
            var service = new PrdDerivationService(AiReturning(SoundPrd()));

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => service.DeriveAsync(emptyDir, "dbo.UP_TEST", null, CancellationToken.None));

            Directory.Delete(emptyDir, true);
        }
    }
}
