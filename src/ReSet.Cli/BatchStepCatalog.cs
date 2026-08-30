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
        //
        // ExtractProcedureIdentifier가 인식하는 형태인지로 판정을 위임한다 — 두 메서드가
        // 각자 레이아웃을 다시 판정하면 한쪽만 고쳐질 때 조용히 어긋난다(실제로 그런
        // 사고가 있었다: FindStepCandidates가 통과시킨 경로에서 정작 식별자를 뽑지
        // 못하는 경로가 있었다). "이 경로가 프로시저 명세서인가"와 "그렇다면 식별자가
        // 무엇인가"는 같은 판정을 공유해야 한다.
        private static bool IsProcedureSpec(string relativePath) =>
            ExtractProcedureIdentifier(relativePath) != null;

        /// <summary>
        /// FindStepCandidates가 돌려주는 outputRoot 기준 상대 경로에서 객체 식별자
        /// ("스키마.이름", OutputPathResolver가 쓰는 것과 같은 형태)를 뽑는다. 인식하지
        /// 못하는 형태면 null을 돌려준다.
        ///
        /// 이 메서드가 필요한 이유: 통합 배치 파이프라인(`VerificationPipelineOrchestrator`)의
        /// 목차 커버리지 검사와 AI 프롬프트의 "Filename:" 레이블이 명세서를 구분하는
        /// 유일한 근거가 (FileName, Content) 튜플의 FileName이다. 이 상대 경로를 그대로
        /// 쓰면 마지막 세그먼트가 항상 "Spec.md"라서 모든 명세서가 같은 값으로 뭉개진다
        /// (실측된 결함). 식별자는 경로의 "Procedures" 세그먼트 바로 다음 세그먼트에
        /// 있으므로, 마지막 세그먼트가 아니라 그 자리를 읽어야 한다.
        /// </summary>
        public static string? ExtractProcedureIdentifier(string relativePath)
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
                return segments[1];
            }

            // External/<DB>/Procedures/<객체>/docs/Spec.md
            if (segments.Length == 6 &&
                segments[0].Equals("External", StringComparison.OrdinalIgnoreCase) &&
                segments[2].Equals("Procedures", StringComparison.OrdinalIgnoreCase) &&
                segments[4].Equals("docs", StringComparison.OrdinalIgnoreCase))
            {
                return segments[3];
            }

            return null;
        }

        private const string ManifestFileName = "dependency-manifest.json";

        /// <summary>
        /// 이 명세가 부르는 <b>프로시저 타입</b> 참조 객체의 명세 경로를 outputRoot 기준
        /// 상대 경로로 돌려준다.
        ///
        /// [왜 프로시저만인가] 함수 참조는 결손이 아니다 - 부모 명세의 「참조 함수 표」가
        /// 호출 지점·라인·호출식 전문을 이미 담고, 계획서는 함수를 재구현하지 않는다.
        /// 실측(설계서 §2): 함수 참조 30건을 함께 더하면 프롬프트가 +34%가 된다.
        ///
        /// [왜 파일 존재를 확인하는가] 없는 경로를 재료 목록에 넣으면
        /// <see cref="LoadDefinitionsAsync"/>가 그것을 MissingMetadata 로 세어, 사람이
        /// 고르지도 않은 항목 때문에 경고가 뜬다.
        ///
        /// 매니페스트가 없거나 JSON 이 아니면 빈 목록이다 - 재료 없음을 실패로 바꾸지 않는다.
        /// </summary>
        public static IReadOnlyList<string> ReadProcedureReferences(
            string outputRoot, string specRelativePath)
        {
            if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(specRelativePath))
            {
                return Array.Empty<string>();
            }

            var objectDirectory = Path.GetDirectoryName(
                Path.GetDirectoryName(Path.Combine(outputRoot, specRelativePath)));
            if (objectDirectory is null) return Array.Empty<string>();

            var manifestPath = Path.Combine(objectDirectory, "raw", ManifestFileName);
            if (!File.Exists(manifestPath)) return Array.Empty<string>();

            ManifestShape? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ManifestShape>(
                    File.ReadAllText(manifestPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warning(
                    exception,
                    "[배치 설계] 의존 매니페스트를 읽지 못했습니다 (계속 진행): {ManifestPath}",
                    manifestPath);
                return Array.Empty<string>();
            }

            if (manifest is null) return Array.Empty<string>();

            var results = new List<string>();
            // "Nodes": null 은 문법은 맞지만 퇴화한 모양이다 - System.Text.Json 이 키가
            // 명시적으로 null 이면 ManifestShape.Nodes 의 `= new()` 기본값을 덮어쓴다.
            // null 검사 없이 순회하면 catch 블록 밖에서 NullReferenceException 이 던져져
            // §8 의 "예외를 밖으로 던지지 않는다"를 어긴다.
            foreach (var node in manifest.Nodes ?? Enumerable.Empty<ManifestNodeShape>())
            {
                if (string.IsNullOrWhiteSpace(node.Key) || string.IsNullOrWhiteSpace(node.SpecPath)) continue;
                if (!node.Key.EndsWith(".Procedure", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(node.Key, manifest.Key, StringComparison.OrdinalIgnoreCase)) continue;

                var absolute = Path.GetFullPath(Path.Combine(objectDirectory, node.SpecPath));
                if (!File.Exists(absolute))
                {
                    // 설계서 §8: 없으면 조용히 빼되 한 줄 남긴다. 매니페스트가 가리키는데
                    // 파일이 없다는 것은 분석이 중간에 끊겼다는 뜻이라 사람이 알아야 한다.
                    Log.Warning(
                        "[배치 설계] 참조 프로시저의 명세가 없어 재료에서 제외합니다: {NodeKey} ({SpecPath})",
                        node.Key, absolute);
                    continue;
                }

                results.Add(Path.GetRelativePath(outputRoot, absolute));
            }

            return results;
        }

        // 매니페스트에서 이 클래스가 쓰는 두 칸만 받는다. MetadataExporter 의 전체
        // 모델을 여기서 다시 만들지 않는 이유는 그것이 private 이고, 이 판정에 필요한
        // 것이 Key 와 SpecPath 둘뿐이기 때문이다.
        private sealed class ManifestShape
        {
            public string Key { get; init; } = string.Empty;
            public List<ManifestNodeShape> Nodes { get; init; } = new();
        }

        private sealed class ManifestNodeShape
        {
            public string Key { get; init; } = string.Empty;
            public string SpecPath { get; init; } = string.Empty;
        }
    }
}
