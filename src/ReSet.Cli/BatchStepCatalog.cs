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

        /// <summary>
        /// <paramref name="items"/>를 <paramref name="closure"/>.<see cref="ProcedureClosure.SpecPaths"/>
        /// 순서로 재정렬한다. <paramref name="relativePathOf"/>가 돌려준 경로가
        /// <c>SpecPaths</c>에 있는 항목은 그 순서대로 나온다.
        ///
        /// <c>SpecPaths</c>에 없는 항목(경로를 못 만든 것 포함, <paramref name="relativePathOf"/>가
        /// null을 돌려줘도 된다)은 <b>하나도 사라지지 않는다</b> — 자신의 원래 바로
        /// 앞에 있던 매치된 항목이 재정렬로 어디로 옮겨가든 그 뒤에 그대로 붙어
        /// 원래 상대 위치를 유지한다. 앞에 매치된 항목이 하나도 없었으면 맨 앞에 남는다.
        ///
        /// [왜 필요한가] 배치 모드(`Program.cs`)는 진입점으로 채운 재료 목록 끝에
        /// 참조 프로시저를 덧붙인다. <see cref="LoadDefinitionsAsync"/>의 계약은
        /// 입력 순서를 실행 순서로 쓰므로, 끝에 붙은 채로 넘기면 실행 순서가 틀린다
        /// (설계서 §6). 이 헬퍼가 <c>SpecPaths</c>(참조자 바로 뒤에 삽입된 순서)로
        /// 다시 줄 세운다. TUI 흐름은 애초에 <c>SpecPaths</c>를 직접 순회해 재료를
        /// 짓기 때문에 이 헬퍼가 필요 없다.
        /// </summary>
        public static IReadOnlyList<T> ReorderByClosure<T>(
            IReadOnlyList<T> items,
            Func<T, string?> relativePathOf,
            ProcedureClosure closure)
        {
            if (items is null || items.Count == 0)
            {
                return Array.Empty<T>();
            }

            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < closure.SpecPaths.Count; i++)
            {
                // 폐포 안에 같은 경로가 중복되면 처음 나온 자리를 신뢰한다(실물에서
                // 중복은 안 생기지만, 방어적으로 첫 등장만 쓴다).
                if (!order.ContainsKey(closure.SpecPaths[i]))
                {
                    order[closure.SpecPaths[i]] = i;
                }
            }

            // 매치된 항목: (폐포 인덱스, 원래 인덱스, 항목). 원래 인덱스는 같은 폐포
            // 인덱스를 공유하는 항목들 사이의 동률을 원래 순서로 깬다.
            var matched = new List<(int ClosureIndex, int OriginalIndex, T Item)>();
            // 매치 안 된 항목은 자기 바로 앞의 매치된 항목(폐포 인덱스)에 붙는다.
            // -1은 "아직 아무것도 매치 안 됐을 때"(맨 앞)를 뜻한다.
            var unmatchedAfter = new Dictionary<int, List<T>>();

            var anchor = -1;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var path = relativePathOf(item);
                if (path is not null && order.TryGetValue(path, out var closureIndex))
                {
                    matched.Add((closureIndex, i, item));
                    anchor = closureIndex;
                    continue;
                }

                if (!unmatchedAfter.TryGetValue(anchor, out var bucket))
                {
                    bucket = new List<T>();
                    unmatchedAfter[anchor] = bucket;
                }
                bucket.Add(item);
            }

            var result = new List<T>(items.Count);

            if (unmatchedAfter.TryGetValue(-1, out var leading))
            {
                result.AddRange(leading);
                unmatchedAfter.Remove(-1);
            }

            foreach (var entry in matched.OrderBy(m => m.ClosureIndex).ThenBy(m => m.OriginalIndex))
            {
                result.Add(entry.Item);
                if (unmatchedAfter.TryGetValue(entry.ClosureIndex, out var bucket))
                {
                    result.AddRange(bucket);
                    unmatchedAfter.Remove(entry.ClosureIndex);
                }
            }

            return result;
        }

        private const string ManifestFileName = "dependency-manifest.json";

        /// <summary>
        /// 진입점 목록을 프로시저 참조 폐포로 닫은 결과.
        /// </summary>
        /// <param name="SpecPaths">진입점 + 더해진 것. 순서가 실행 순서다.</param>
        /// <param name="Added">더해진 것만. 호출부가 사람에게 알리는 데 쓴다.</param>
        /// <param name="CapExceeded">상한에 걸려 더 넓히지 않고 멈췄는가.</param>
        public sealed record ProcedureClosure(
            IReadOnlyList<string> SpecPaths,
            IReadOnlyList<string> Added,
            bool CapExceeded);

        /// <summary>
        /// 사람이 고른 <b>진입점</b> 목록에, 그것이 부르는 프로시저 타입 참조를 고정점까지
        /// 더한다. 사람의 선택 의미는 바뀌지 않는다 - 진입점은 그대로이고 <b>재료</b>만 닫는다.
        ///
        /// [왜 참조자 바로 뒤인가] <see cref="LoadDefinitionsAsync"/>의 계약이 순서를
        /// 실행 순서로 쓴다. 하위 프로시저는 부모 흐름 <b>안에서</b> 실행되므로 끝에
        /// 붙이면 실행 순서가 틀린다(설계서 §6).
        ///
        /// [왜 상한이 필요한가] 매니페스트가 예상보다 넓게 물리면 프롬프트가 폭주한다.
        /// <c>BatchStepPlanParser.MaxSteps</c>가 이미 쓰는 방어와 같은 관용이다.
        /// </summary>
        public static ProcedureClosure CloseOverProcedureReferences(
            string outputRoot, IReadOnlyList<string> entryPointSpecPaths)
        {
            if (entryPointSpecPaths is null || entryPointSpecPaths.Count == 0)
            {
                return new ProcedureClosure(Array.Empty<string>(), Array.Empty<string>(), false);
            }

            var cap = entryPointSpecPaths.Count * 2;

            // 대소문자만 다른 중복 진입점은 같은 프로시저다(Global Constraints: 경로
            // 비교·중복 판정은 OrdinalIgnoreCase). seen은 이미 OrdinalIgnoreCase로
            // 만들지만 예전 코드는 ordered를 entryPointSpecPaths에서 그대로 복사해
            // seen과 별개로 중복을 남겼다 - 처음 나온 표기만 남기고 접는다.
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entryPoint in entryPointSpecPaths)
            {
                if (seen.Add(entryPoint))
                {
                    ordered.Add(entryPoint);
                }
            }

            var added = new List<string>();
            var capExceeded = false;

            // 인덱스로 돈다 - 더해진 항목도 자기 참조를 펼쳐야 고정점이 된다.
            for (var i = 0; i < ordered.Count && !capExceeded; i++)
            {
                var insertAt = i + 1;
                foreach (var reference in ReadProcedureReferences(outputRoot, ordered[i]))
                {
                    if (!seen.Add(reference)) continue;

                    if (ordered.Count >= cap)
                    {
                        capExceeded = true;
                        Log.Warning(
                            "[배치 설계] 참조 폐포가 상한({Cap})에 걸려 더 넓히지 않습니다. 진입점 {EntryCount}개.",
                            cap, entryPointSpecPaths.Count);
                        break;
                    }

                    ordered.Insert(insertAt++, reference);
                    added.Add(reference);
                }
            }

            return new ProcedureClosure(ordered, added, capExceeded);
        }

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
