using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReSet.Core.Models;
using Serilog;

namespace ReSet.Cli
{
    /// <summary>
    /// 배치 스텝 메타데이터 복원 결과. 복원 실패를 원인별로 나누어 호출부가 사실대로 알릴 수 있게 한다.
    /// </summary>
    public sealed record BatchStepLoadResult(
        IReadOnlyList<SpDefinition> Definitions,
        IReadOnlyList<string> MissingMetadata,
        IReadOnlyList<string> FailedToParse);

    /// <summary>
    /// 통합 배치 설계의 스텝 후보를 선별하고 각 스텝의 분석 메타데이터를 복원한다.
    /// </summary>
    public static class BatchStepCatalog
    {
        private const string SpecFileName = "Spec.md";

        /// <summary>
        /// 배치 스텝 자격이 있는 명세서만 outputRoot 기준 상대 경로로 돌려준다.
        /// 배치 스텝은 프로시저이므로 UDF와 Job 검증 중간산출물은 제외한다.
        /// </summary>
        public static IReadOnlyList<string> FindStepCandidates(string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
            {
                return Array.Empty<string>();
            }

            try
            {
                return Directory
                    .GetFiles(outputRoot, SpecFileName, SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(outputRoot, path))
                    .Where(IsProcedureSpec)
                    .ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Warning(
                    exception,
                    "[배치 설계] 스텝 후보 탐색 실패 (계속 진행): {OutputRoot}",
                    outputRoot);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 선택된 명세서들의 분석 메타데이터를 입력 순서 그대로 복원한다.
        /// 입력 순서가 곧 배치 스텝 실행 순서이므로 순서를 흐트러뜨리면 안 된다.
        /// </summary>
        public static async Task<BatchStepLoadResult> LoadDefinitionsAsync(
            string outputRoot,
            IEnumerable<string> specRelativePaths,
            CancellationToken cancellationToken = default)
        {
            var definitions = new List<SpDefinition>();
            var missingMetadata = new List<string>();
            var failedToParse = new List<string>();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (var specRelativePath in specRelativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var metadataRelativePath = specRelativePath.Replace(
                    Path.Combine("docs", "Spec.md"),
                    Path.Combine("raw", "metadata.json"));
                var metadataPath = Path.Combine(outputRoot, metadataRelativePath);

                if (!File.Exists(metadataPath))
                {
                    missingMetadata.Add(specRelativePath);
                    continue;
                }

                try
                {
                    var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
                    var definition = JsonSerializer.Deserialize<SpDefinition>(json, options);
                    if (definition is null)
                    {
                        failedToParse.Add(specRelativePath);
                        continue;
                    }

                    definitions.Add(definition);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Log.Warning(
                        exception,
                        "[배치 설계] 스텝 메타데이터 복원 실패 (계속 진행): {SpecPath}",
                        specRelativePath);
                    failedToParse.Add(specRelativePath);
                }
            }

            return new BatchStepLoadResult(definitions, missingMetadata, failedToParse);
        }

        // 객체 유형은 OutputPathResolver가 디렉터리 이름으로 인코딩하므로
        // 파일을 열지 않고 경로 형태만으로 판정할 수 있다.
        private static bool IsProcedureSpec(string relativePath)
        {
            var segments = relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            // Procedures/<객체>/docs/Spec.md
            if (segments.Length == 4 &&
                segments[0].Equals("Procedures", StringComparison.OrdinalIgnoreCase) &&
                segments[2].Equals("docs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // External/<DB>/Procedures/<객체>/docs/Spec.md
            return segments.Length == 6 &&
                   segments[0].Equals("External", StringComparison.OrdinalIgnoreCase) &&
                   segments[2].Equals("Procedures", StringComparison.OrdinalIgnoreCase) &&
                   segments[4].Equals("docs", StringComparison.OrdinalIgnoreCase);
        }
    }
}
