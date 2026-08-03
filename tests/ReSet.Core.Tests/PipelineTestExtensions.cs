using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests;

/// <summary>
/// 프로덕션에서 제거된 VerificationPipelineOrchestrator.RunPipelineAsync의
/// 테스트 전용 대체. 40여 개 기존 테스트가 쓰던 튜플 반환 형태를 유지한다.
/// 프로덕션 호출부는 RunCodeObjectPipelineAsync를 직접 쓴다.
/// </summary>
internal static class PipelineTestExtensions
{
    public static async Task<(string? SpecMarkdown, SpDefinition? SpDef, ReviewResult? Review, string? ThinkingText, VerificationOutcome Outcome)>
        RunPipelineAsync(
            this VerificationPipelineOrchestrator orchestrator,
            string connectionString,
            string schema,
            string name,
            int maxDepth,
            string provider,
            string instructions,
            bool isBatchMode,
            string outputDirectory = "./output",
            bool enableCache = false,
            CancellationToken cancellationToken = default)
    {
        var database = VerificationPipelineOrchestrator.ResolveCurrentDatabase(connectionString)
            ?? string.Empty;
        var key = CodeObjectKey.Create(database, schema, name, CodeObjectType.Procedure);
        var result = await orchestrator.RunCodeObjectPipelineAsync(
            connectionString,
            key,
            maxDepth,
            provider,
            instructions,
            isBatchMode,
            outputDirectory,
            enableCache,
            cancellationToken);

        return (result.SpecMarkdown, result.SpDef, result.Review, result.ThinkingText, result.Outcome);
    }
}
