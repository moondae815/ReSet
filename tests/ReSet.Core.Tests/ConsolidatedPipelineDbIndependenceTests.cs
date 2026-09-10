using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using ReSet.Core.Models;
using ReSet.Core.Services;
using Xunit;

namespace ReSet.Core.Tests
{
    /// <summary>
    /// `--plan-only`(저장된 명세서만으로 계획을 세우는 무인 경로)가 서 있는 전제를 못박는다:
    /// <b>통합 배치 파이프라인은 DB를 부르지 않는다.</b> 개별 SP 분석 경로와 달리 재료가
    /// 이미 파일로 있기 때문이다.
    ///
    /// 이 전제가 깨지면 DB 없이 도는 경로가 널 연결 문자열로 DB에 붙으려 하다 죽는데,
    /// 그 고장은 계획 수립을 수십 분 돌린 뒤에야 드러난다. 그래서 "부르면 그 자리에서
    /// 터지는" 대역을 넣어 회귀를 즉시 드러낸다.
    /// </summary>
    public class ConsolidatedPipelineDbIndependenceTests
    {
        [Fact]
        public async Task 통합_배치_파이프라인은_DB를_한_번도_부르지_않는다()
        {
            var aiService = Substitute.For<IAiService>();
            aiService.ProviderName.Returns("stub");

            var orchestrator = new VerificationPipelineOrchestrator(
                new ThrowingDbMetadataService(), aiService, new MechanicalValidator(),
                Substitute.For<IVerificationUserInteraction>(),
                "0", "gpt-4", null, aiService, aiService, "high", "high", "default", 8,
                stepConcurrency: 1, maxL1RepairAttempts: 2);

            var specs = new List<(string FileName, string Content)> { ("dbo.UP_A", "# 명세서\n\n본문") };
            var outputRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"ReSet-PlanOnlyDb-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(outputRoot);

            try
            {
                var result = await orchestrator.RunConsolidatedPipelineAsync(
                    specs, "C#", "PlanOnlyJob", "stub", outputRoot,
                    isBatchMode: true,
                    definitions: new List<SpDefinition> { new SpDefinition { Schema = "dbo", Name = "UP_A" } },
                    cancellationToken: CancellationToken.None);

                Assert.NotNull(result);
            }
            finally
            {
                System.IO.Directory.Delete(outputRoot, true);
            }
        }

        /// <summary>
        /// 모든 멤버가 던지는 <see cref="IDbMetadataService"/>. NSubstitute 대역은 조용히
        /// 기본값을 돌려주므로 "안 불렀다"와 "불렀는데 빈 값을 받았다"를 가르지 못한다.
        /// </summary>
        private sealed class ThrowingDbMetadataService : IDbMetadataService
        {
            private static Exception Called([System.Runtime.CompilerServices.CallerMemberName] string member = "")
                => new InvalidOperationException(
                    $"통합 배치 파이프라인이 DB를 불렀습니다: {member}. 이 경로는 저장된 명세서만 재료로 써야 합니다.");

            public Task<string> GetCurrentDatabaseNameAsync(string connectionString, CancellationToken cancellationToken = default)
                => throw Called();

            public Task<List<string>> GetStoredProcedureNamesAsync(string connectionString, CancellationToken cancellationToken = default)
                => throw Called();

            public Task<SpDefinition> GetSpDetailsAsync(string connectionString, string schema, string spName, int maxDepth, CancellationToken cancellationToken = default)
                => throw Called();

            public Task<SpDefinition> GetCodeObjectDetailsAsync(string connectionString, CodeObjectKey objectKey, int maxDepth, CancellationToken cancellationToken = default)
                => throw Called();

            public Task<SpDefinition> GetCodeObjectDetailsDirectAsync(string connectionString, CodeObjectKey objectKey, CancellationToken cancellationToken = default, bool includeExternalCodeObjects = true)
                => throw Called();

            public Task<List<Dictionary<string, object>>> GetTableDataPreviewAsync(string connectionString, string? database, string schema, string tableName, int limit = 100, CancellationToken cancellationToken = default)
                => throw Called();

            public Task<int> GetTableRowCountAsync(string connectionString, string? database, string schema, string tableName, CancellationToken cancellationToken = default)
                => throw Called();
        }
    }
}
