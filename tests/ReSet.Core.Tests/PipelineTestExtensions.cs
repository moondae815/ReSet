using ReSet.Core.Models;
using ReSet.Core.Services;

namespace ReSet.Core.Tests;

/// <summary>
/// 프로덕션에서 제거된 VerificationPipelineOrchestrator.RunPipelineAsync의
/// 테스트 전용 대체. 40여 개 기존 테스트가 쓰던 튜플 반환 형태를 유지한다.
/// 프로덕션 호출부는 RunCodeObjectPipelineAsync를 직접 쓴다.
/// </summary>
/// <remarks>
/// 키 조립은 반드시 <see cref="VerificationPipelineOrchestrator.CreateProcedureKey"/>를
/// 거친다. 그 메서드는 비재귀 경로가 오케스트레이터로 통일된 뒤로 호출부가 여기뿐이므로,
/// 사본을 만들면 그것을 지나는 테스트가 0개가 된다.
/// 반환 형태(튜플 평탄화)만이 이 파일이 갖는 테스트 전용 로직이다.
/// </remarks>
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
        var key = VerificationPipelineOrchestrator.CreateProcedureKey(
            connectionString, schema, name);
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
